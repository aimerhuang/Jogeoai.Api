using System.Collections.Concurrent;
using System.Diagnostics;
using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jogeoai.Infrastructure;

/// <summary>
/// 站点图标获取编排：未过期缓存直接返回；否则同一域名的并发请求合并为一次抓取，全局信号量限制同时抓取数，
/// 结果按 found / missing / 仅网络失败三种有效期写回 <c>site_icons</c>。过期的 found 在仅网络失败时继续返回并顺延；
/// 子域名最终仍拿不到时借用其可注册域名的图标（只额外查一次），可注册域名结果按自身单独缓存。
/// </summary>
public sealed class SiteIconService : ISiteIconService
{
    private readonly ISiteIconStore store;
    private readonly SiteIconFetcher fetcher;
    private readonly SiteIconOptions settings;
    private readonly ILogger<SiteIconService> logger;
    private readonly TimeProvider time;
    private readonly SemaphoreSlim slots;
    private readonly ConcurrentDictionary<string, Lazy<Task<SiteIconRecord>>> inflight = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> aliases = new(StringComparer.Ordinal);

    /// <summary>创建服务；<paramref name="time"/> 为空时使用系统时钟。</summary>
    public SiteIconService(ISiteIconStore store, SiteIconFetcher fetcher, IOptions<SiteIconOptions> options,
        ILogger<SiteIconService> logger, TimeProvider? time = null)
    {
        this.store = store;
        this.fetcher = fetcher;
        settings = options.Value;
        this.logger = logger;
        this.time = time ?? TimeProvider.System;
        var concurrency = Math.Clamp(settings.MaxConcurrency, 1, 64);
        slots = new SemaphoreSlim(concurrency, concurrency);
        foreach (var (from, to) in settings.DomainAliases)
        {
            if (SiteIconRules.TryNormalizeDomain(from, out var source) && SiteIconRules.TryNormalizeDomain(to, out var target))
                aliases[source] = target;
            else
                logger.LogWarning("Ignoring invalid SiteIcons:DomainAliases entry {From} -> {To}.", from, to);
        }
    }

    /// <summary>按 <see cref="SiteIconOptions.DomainAliases"/> 解析实际取图标的域名：先匹配完整域名，再逐级匹配父域名（至少两段）。</summary>
    internal string ResolveAlias(string normalized)
    {
        for (var candidate = normalized; candidate.Count(c => c == '.') >= 1; candidate = candidate[(candidate.IndexOf('.') + 1)..])
            if (aliases.TryGetValue(candidate, out var target)) return target;
        return normalized;
    }

    /// <inheritdoc />
    public async Task<SiteIconRecord> GetAsync(string domain, CancellationToken cancellationToken = default)
    {
        if (!SiteIconRules.TryNormalizeDomain(domain, out var requested))
            throw new CollectorStoreException("INVALID_SITE_DOMAIN", "站点域名不合法。", 400);
        return await LookupAsync(ResolveAlias(requested), cancellationToken);
    }

    /// <summary>已规范化（且已解析别名）域名的查找：未过期缓存 → 合并并发的刷新。上级域名兜底也走这里，结果按上级域名缓存。</summary>
    private async Task<SiteIconRecord> LookupAsync(string normalized, CancellationToken cancellationToken)
    {
        var cached = await store.GetAsync(normalized, cancellationToken);
        if (cached is not null && !cached.IsExpired(time.GetUtcNow())) return cached;

        Lazy<Task<SiteIconRecord>>? created = null;
        created = new Lazy<Task<SiteIconRecord>>(() => RefreshAsync(normalized, cached, created!));
        var shared = inflight.GetOrAdd(normalized, created);
        // 抓取不随单个调用方取消：其他合并等待者仍需要结果，且结果会写入缓存。
        return await shared.Value.WaitAsync(cancellationToken);
    }

    private async Task<SiteIconRecord> RefreshAsync(string domain, SiteIconRecord? previous, Lazy<Task<SiteIconRecord>> self)
    {
        try
        {
            var stopwatch = Stopwatch.StartNew();
            SiteIconFetchResult result;
            await slots.WaitAsync();
            try
            {
                result = await fetcher.FetchAsync(domain);
            }
            finally
            {
                // 先释放全局名额再查上级域名：否则 MaxConcurrency 个子域名同时等待上级抓取时会互相卡死。
                slots.Release();
            }

            var now = time.GetUtcNow();
            var transientTtl = TimeSpan.FromHours(Math.Max(1, settings.TransientMissingTtlHours));
            var foundTtl = TimeSpan.FromDays(Math.Max(1, settings.FoundTtlDays));
            SiteIconRecord record;
            string code;
            if (result.Outcome == SiteIconFetchOutcome.Found)
            {
                record = new SiteIconRecord(domain, SiteIconStatus.Found, result.ContentType, result.Bytes, result.Source, now, now + foundTtl);
                code = "found";
            }
            else if (result.Outcome == SiteIconFetchOutcome.Transient && previous is { IsFound: true })
            {
                // 短暂断网不覆盖已有图标：保留旧字节与 fetched_at，只顺延有效期以便稍后重试。
                record = previous with { ExpiresAt = now + transientTtl };
                code = "stale_retained";
            }
            else if (await ParentIconAsync(domain) is { } parent)
            {
                // 子域名自身拿不到时借用上级域名的图标（上级记录已按上级域名单独缓存）。
                record = new SiteIconRecord(domain, SiteIconStatus.Found, parent.ContentType, parent.Bytes, parent.Source, now, now + foundTtl);
                code = "parent_found";
            }
            else if (result.Outcome == SiteIconFetchOutcome.Missing)
            {
                record = new SiteIconRecord(domain, SiteIconStatus.Missing, null, null, null,
                    now, now.AddDays(Math.Max(1, settings.MissingTtlDays)));
                code = "missing";
            }
            else
            {
                record = new SiteIconRecord(domain, SiteIconStatus.Missing, null, null, null, now, now + transientTtl);
                code = "transient_missing";
            }
            await store.SaveAsync(record);
            logger.LogInformation("Site icon {Domain} source {Source} result {ResultCode} in {ElapsedMs} ms",
                domain, record.Source ?? "none", code, stopwatch.ElapsedMilliseconds);
            return record;
        }
        finally
        {
            inflight.TryRemove(new KeyValuePair<string, Lazy<Task<SiteIconRecord>>>(domain, self));
        }
    }

    /// <summary>
    /// 子域名的上级域名兜底：沿 <see cref="SiteIconRules.ParentCandidate"/> 直接定位到可注册域名（纯计算，不逐级抓取），
    /// 只对它走一次完整查找路径（缓存 → 合并 → 抓取）；可注册域名自身不再向上，所以任意深度的子域名最多多抓一次。
    /// 可注册域名也拿不到时返回 null。
    /// </summary>
    private async Task<SiteIconRecord?> ParentIconAsync(string domain)
    {
        string? parent = null;
        for (var next = SiteIconRules.ParentCandidate(domain); next is not null; next = SiteIconRules.ParentCandidate(next))
            parent = next;
        if (parent is null) return null;
        try
        {
            var record = await LookupAsync(parent, CancellationToken.None);
            return record.IsFound ? record : null;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Site icon parent lookup {Parent} for {Domain} failed.", parent, domain);
            return null;
        }
    }
}
