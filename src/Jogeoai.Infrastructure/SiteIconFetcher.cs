using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jogeoai.Infrastructure;

/// <summary>出站主机校验结果。</summary>
public enum SiteIconHostCheck
{
    /// <summary>主机解析到的全部地址都是公网地址。</summary>
    Public,

    /// <summary>本地、保留或内网主机，拒绝访问。</summary>
    Private,

    /// <summary>DNS 解析失败（按网络错误处理）。</summary>
    Unresolved,
}

/// <summary>站点图标出站请求的主机校验；生产实现复用 <see cref="ArticleSourceReader"/> 的公网规则，测试可替换以免访问 DNS。</summary>
public interface ISiteIconHostGuard
{
    /// <summary>校验 URL 主机是否允许访问。</summary>
    Task<SiteIconHostCheck> CheckAsync(Uri url, CancellationToken cancellationToken);
}

/// <summary>
/// 基于 <see cref="ArticleSourceReader.ValidatePublicHostAsync(string, Func{System.Net.IPAddress, bool}, CancellationToken)"/> 的主机校验，
/// 地址按 <see cref="SiteIconAddressPolicy"/> 判断（与 <c>site-icon</c> 客户端的连接回调一致）。
/// </summary>
public sealed class PublicSiteIconHostGuard(SiteIconAddressPolicy policy) : ISiteIconHostGuard
{
    /// <inheritdoc />
    public async Task<SiteIconHostCheck> CheckAsync(Uri url, CancellationToken cancellationToken) =>
        await ArticleSourceReader.ValidatePublicHostAsync(url.AbsoluteUri, policy.IsAllowed, cancellationToken) switch
        {
            null => SiteIconHostCheck.Public,
            "SOURCE_DNS_FAILED" => SiteIconHostCheck.Unresolved,
            _ => SiteIconHostCheck.Private,
        };
}

/// <summary>一次抓取链路的结论。</summary>
public enum SiteIconFetchOutcome
{
    /// <summary>取得通过魔数校验的图标。</summary>
    Found,

    /// <summary>至少有一个来源明确回答没有可用图标。</summary>
    Missing,

    /// <summary>所有来源都只因超时 / 网络错误失败。</summary>
    Transient,
}

/// <summary>抓取结果；只有 Found 带类型、字节与来源。</summary>
public sealed record SiteIconFetchResult(SiteIconFetchOutcome Outcome, string? ContentType = null, byte[]? Bytes = null, string? Source = null);

/// <summary>
/// 站点图标出站抓取：首页 HTML 声明（https 失败再 http）→ /favicon.ico →（可注册域名）www 子域名的同样步骤 → 第三方服务。
/// 所有请求走命名 HttpClient <c>site-icon</c>（<see cref="ArticleSourceReader.CreatePinnedHandler(Func{System.Net.IPAddress, bool})"/>），手动处理重定向
/// （≤ 5 跳，每跳重新校验主机），限制单次超时、总预算与图标大小，只接受魔数识别的位图。日志不记录响应内容。
/// </summary>
public sealed class SiteIconFetcher(
    IHttpClientFactory clients,
    ISiteIconHostGuard guard,
    IOptions<SiteIconOptions> options,
    ILogger<SiteIconFetcher> logger)
{
    /// <summary>命名 HttpClient。</summary>
    public const string ClientName = "site-icon";

    /// <summary>首页 HTML 最多读取的字节数（超出部分截断，不视为失败）。</summary>
    public const int MaxHtmlBytes = 256 * 1024;

    private const int MaxRedirects = 5;
    private const int HardMaxIconBytes = 1_048_576;
    private const string UserAgent = "Mozilla/5.0 (compatible; JogeoaiSiteIcon/1.0)";

    private enum Verdict { Found, Missing, Transient }

    private sealed record Probe(Verdict Verdict, string Code, Uri? FinalUrl = null, string? ContentType = null, byte[]? Bytes = null, string? Html = null);

    /// <summary>
    /// 对已规范化的域名执行完整抓取链路：站点自身（首页 link → /favicon.ico）→ 可注册域名再试 <c>www.{domain}</c> 的站点自身步骤 → 第三方服务。
    /// 上级域名兜底由 <see cref="SiteIconService"/> 负责，不在本方法内。
    /// </summary>
    public async Task<SiteIconFetchResult> FetchAsync(string domain, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        var requestTimeout = TimeSpan.FromSeconds(Math.Clamp(settings.RequestTimeoutSeconds, 1, 60));
        var total = TimeSpan.FromSeconds(Math.Clamp(settings.TotalBudgetSeconds, 1, 300));
        var maxBytes = Math.Clamp(settings.MaxIconBytes, 1024, HardMaxIconBytes);
        var providers = settings.ThirdParty.EffectiveProviders();
        var started = Stopwatch.GetTimestamp();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(total);
        using var client = clients.CreateClient(ClientName);
        var definitive = false;
        var tried = new HashSet<string>(StringComparer.Ordinal);

        var siteBudget = SiteBudget(total, requestTimeout, providers.Count);
        using (var site = CancellationTokenSource.CreateLinkedTokenSource(budget.Token))
        {
            site.CancelAfter(siteBudget);
            var own = await FetchOwnSiteAsync(client, domain, tried, maxBytes, requestTimeout, site.Token);
            if (own.Found is not null) return own.Found;
            definitive |= own.Definitive;

            // 可注册域名自身拿不到时再试 www 子域名（不少站点只在 www 上提供首页或图标）；
            // 首页本就跳转到 www 时已经试过，不重复。剩余站点预算不足一次请求时跳过，避免挤占第三方预留。
            var www = "www." + domain;
            if (ShouldTryWww(domain) && www.Length <= SiteIconRules.MaxDomainLength
                && !string.Equals(own.OriginHost, www, StringComparison.Ordinal)
                && siteBudget - Stopwatch.GetElapsedTime(started) >= requestTimeout)
            {
                var alternate = await FetchOwnSiteAsync(client, www, tried, maxBytes, requestTimeout, site.Token);
                if (alternate.Found is not null) return alternate.Found;
                definitive |= alternate.Definitive;
            }
        }

        foreach (var provider in providers)
        {
            var url = ThirdPartyUrl(provider, domain);
            var icon = await GetAsync(client, url, html: false, maxBytes, requestTimeout, budget.Token);
            if (icon.Verdict == Verdict.Found && IsPlaceholder(provider, icon.Bytes!))
                icon = icon with { Verdict = Verdict.Missing, Code = "placeholder" };
            Log(domain, provider, icon.Code);
            if (icon.Verdict == Verdict.Found) return Found(icon, provider);
            definitive |= icon.Verdict == Verdict.Missing;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new SiteIconFetchResult(definitive ? SiteIconFetchOutcome.Missing : SiteIconFetchOutcome.Transient);
    }

    /// <summary>
    /// 站点自身步骤（含 www 兜底）可用的预算：启用第三方时为其保留 <paramref name="requestTimeout"/> × min(2, 服务数)；
    /// 总预算不足以保留时逐次减少保留的请求数（至少给站点自身留出一点时间），不启用第三方时站点自身用满总预算。
    /// </summary>
    public static TimeSpan SiteBudget(TimeSpan total, TimeSpan requestTimeout, int providerCount)
    {
        var reserved = Math.Min(2, Math.Max(0, providerCount));
        while (reserved > 0 && requestTimeout * reserved >= total) reserved--;
        return total - requestTimeout * reserved;
    }

    /// <summary>是否对该域名追加 www 子域名的站点自身步骤：仅可注册域名，且本身不以 <c>www.</c> 开头。</summary>
    public static bool ShouldTryWww(string domain) =>
        SiteIconRules.IsRegistrableDomain(domain) && !domain.StartsWith("www.", StringComparison.Ordinal);

    /// <summary>第三方服务的固定公网地址。</summary>
    public static Uri ThirdPartyUrl(string provider, string domain) => provider switch
    {
        SiteIconSource.Google => new Uri("https://t1.gstatic.com/faviconV2?client=SOCIAL&type=FAVICON&fallback_opts=TYPE,SIZE,URL&url="
            + Uri.EscapeDataString("https://" + domain) + "&size=64"),
        SiteIconSource.DuckDuckGo => new Uri($"https://icons.duckduckgo.com/ip3/{domain}.ico"),
        SiteIconSource.Yandex => new Uri($"https://favicon.yandex.net/favicon/v2/{domain}?size=32"),
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown site icon provider."),
    };

    /// <summary>
    /// 第三方占位图识别：Google 找不到时返回 16px 地球图，Yandex 未收录时返回 1×1 图片，宽度 ≤ 16 一律视为占位。
    /// DuckDuckGo 找不到时返回 404，不需要按尺寸判断。
    /// </summary>
    public static bool IsPlaceholder(string provider, ReadOnlySpan<byte> bytes) =>
        provider is SiteIconSource.Google or SiteIconSource.Yandex && SiteIconRules.ReadWidth(bytes) is { } width && width <= 16;

    private sealed record SiteAttempt(SiteIconFetchResult? Found, bool Definitive, string? OriginHost);

    /// <summary>站点自身步骤：首页（https 失败再 http）声明的图标 → 最终源站的 /favicon.ico。</summary>
    private async Task<SiteAttempt> FetchOwnSiteAsync(HttpClient client, string host, HashSet<string> tried, int maxBytes,
        TimeSpan requestTimeout, CancellationToken token)
    {
        var definitive = false;
        Uri? origin = null;
        foreach (var scheme in new[] { "https", "http" })
        {
            var home = await GetAsync(client, new Uri($"{scheme}://{host}/"), html: true, maxBytes, requestTimeout, token);
            Log(host, "homepage", home.Code);
            if (home.Verdict == Verdict.Transient) continue;
            origin = new Uri((home.FinalUrl ?? new Uri($"{scheme}://{host}/")).GetLeftPart(UriPartial.Authority) + "/");
            if (home.Verdict == Verdict.Found && home.Html is not null && home.FinalUrl is not null)
            {
                foreach (var candidate in SiteIconRules.ParseIconLinks(home.Html, home.FinalUrl))
                {
                    if (!tried.Add(candidate.AbsoluteUri)) continue;
                    var icon = await GetAsync(client, candidate, html: false, maxBytes, requestTimeout, token);
                    Log(host, SiteIconSource.Html, icon.Code);
                    if (icon.Verdict == Verdict.Found) return new SiteAttempt(Found(icon, SiteIconSource.Html), true, origin.Host);
                    definitive |= icon.Verdict == Verdict.Missing;
                }
            }
            break;
        }

        // 首页两种协议都连不上时站点视为不可达，跳过 /favicon.ico。
        if (origin is null) return new SiteAttempt(null, definitive, null);
        var favicon = new Uri(origin, "/favicon.ico");
        if (tried.Add(favicon.AbsoluteUri))
        {
            var icon = await GetAsync(client, favicon, html: false, maxBytes, requestTimeout, token);
            Log(host, SiteIconSource.Favicon, icon.Code);
            if (icon.Verdict == Verdict.Found) return new SiteAttempt(Found(icon, SiteIconSource.Favicon), true, origin.Host);
            definitive |= icon.Verdict == Verdict.Missing;
        }
        return new SiteAttempt(null, definitive, origin.Host);
    }

    private static SiteIconFetchResult Found(Probe probe, string source) =>
        new(SiteIconFetchOutcome.Found, probe.ContentType, probe.Bytes, source);

    private void Log(string domain, string source, string code) =>
        logger.LogDebug("Site icon attempt {Domain} {Source} {ResultCode}", domain, source, code);

    private async Task<Probe> GetAsync(HttpClient client, Uri start, bool html, int maxBytes, TimeSpan requestTimeout, CancellationToken phase)
    {
        var current = start;
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            if (!IsAllowedUrl(current)) return new Probe(Verdict.Missing, "blocked_url");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(phase);
            timeout.CancelAfter(requestTimeout);
            try
            {
                switch (await guard.CheckAsync(current, timeout.Token))
                {
                    case SiteIconHostCheck.Private: return new Probe(Verdict.Missing, "private_host");
                    case SiteIconHostCheck.Unresolved: return new Probe(Verdict.Transient, "dns_failed");
                }
                timeout.Token.ThrowIfCancellationRequested();
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                request.Headers.UserAgent.ParseAdd(UserAgent);
                if (html)
                {
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xhtml+xml"));
                }
                else
                {
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
                }
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                var status = (int)response.StatusCode;
                if (status is >= 300 and < 400 && response.Headers.Location is not null)
                {
                    if (hop == MaxRedirects) return new Probe(Verdict.Missing, "too_many_redirects");
                    if (!Uri.TryCreate(current, response.Headers.Location, out var next)) return new Probe(Verdict.Missing, "invalid_redirect");
                    current = next;
                    continue;
                }
                if (status is 408 or 429 or >= 500) return new Probe(Verdict.Transient, $"http_{status}");
                if (!response.IsSuccessStatusCode) return new Probe(Verdict.Missing, $"http_{status}");
                if (html)
                {
                    var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
                    if (mediaType is not null and not ("text/html" or "application/xhtml+xml"))
                        return new Probe(Verdict.Missing, "not_html", current);
                    var page = await ReadAsync(response, MaxHtmlBytes, truncate: true, timeout.Token);
                    return new Probe(Verdict.Found, "ok", current, Html: Encoding.UTF8.GetString(page!));
                }
                if (response.Content.Headers.ContentLength > maxBytes) return new Probe(Verdict.Missing, "too_large", current);
                var body = await ReadAsync(response, maxBytes, truncate: false, timeout.Token);
                if (body is null) return new Probe(Verdict.Missing, "too_large", current);
                var contentType = SiteIconRules.DetectContentType(body);
                return contentType is null
                    ? new Probe(Verdict.Missing, "not_image", current)
                    : new Probe(Verdict.Found, "ok", current, contentType, body);
            }
            catch (OperationCanceledException)
            {
                return new Probe(Verdict.Transient, "timeout");
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                return new Probe(Verdict.Transient, "network");
            }
        }
        return new Probe(Verdict.Missing, "too_many_redirects");
    }

    /// <summary>流式读取响应体；超过上限时 <paramref name="truncate"/> 为 true 返回前缀，否则返回 null。</summary>
    private static async Task<byte[]?> ReadAsync(HttpResponseMessage response, int limit, bool truncate, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var room = limit + 1 - (int)buffer.Length;
            var read = await stream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, room)), cancellationToken);
            if (read == 0) break;
            buffer.Write(chunk, 0, read);
            if (buffer.Length > limit)
            {
                if (!truncate) return null;
                buffer.SetLength(limit);
                break;
            }
        }
        return buffer.ToArray();
    }

    private static bool IsAllowedUrl(Uri url) =>
        url.IsAbsoluteUri && url.Scheme is "http" or "https" && string.IsNullOrEmpty(url.UserInfo)
        && !string.IsNullOrEmpty(url.Host) && (url.IsDefaultPort || url.Port is 80 or 443)
        && url.AbsoluteUri.Length <= 2048;
}
