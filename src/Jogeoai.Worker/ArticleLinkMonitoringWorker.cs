using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.Extensions.Options;

namespace Jogeoai.Worker;

/// <summary>文章链接监控 Worker 的配置。</summary>
public sealed class ArticleLinkMonitoringOptions
{
    /// <summary>是否启用自动发现与每日计费；false 时不注册后台任务（回滚开关）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>两轮处理之间的间隔；默认 5 分钟。</summary>
    public TimeSpan PassInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>每轮每品牌最多读取标题的链接数；默认 20。</summary>
    public int TitleReadLimit { get; set; } = 20;
}

/// <summary>
/// 每轮依次：按品牌自动发现大模型回答引用的品牌域名链接并建文 → 读取待读标题 → 为当天开计费轮次并预占 →
/// 结算本地自然日已结束的轮次。品牌之间互不阻塞，单品牌失败只记录日志。依赖迁移 027。
/// </summary>
public sealed class ArticleLinkMonitoringWorker(
    ILogger<ArticleLinkMonitoringWorker> logger,
    IArticleLinkMonitoringStore store,
    IOptions<ArticleLinkMonitoringOptions> options) : BackgroundService
{
    /// <summary>执行一轮；返回处理汇总，便于测试与日志。</summary>
    /// <param name="utcNow">本轮使用的 UTC 时间。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<ArticleLinkMonitoringPassResult> RunPassAsync(DateTimeOffset utcNow, CancellationToken cancellationToken = default)
    {
        var sites = await store.ListSitesAsync(cancellationToken);
        var discovered = 0; var titles = 0;
        foreach (var (tenantId, siteId) in sites)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await store.DiscoverAsync(tenantId, siteId, utcNow, cancellationToken);
                discovered += result.Created;
                if (result.Created > 0)
                    logger.LogInformation("Citation discovery created {Created} articles on site {SiteId} ({Scanned} candidates, {Skipped} already registered).",
                        result.Created, siteId, result.Scanned, result.SkippedExisting);
                titles += await store.ReadPendingTitlesAsync(tenantId, siteId, Math.Max(1, options.Value.TitleReadLimit), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                logger.LogWarning("Citation discovery failed on site {SiteId} with {ExceptionType}.", siteId, exception.GetType().Name);
            }
        }
        var opened = 0; var settled = 0;
        try { opened = await store.OpenRoundsAsync(utcNow, null, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { logger.LogWarning("Opening article monitoring rounds failed with {ExceptionType}.", exception.GetType().Name); }
        try { settled = await store.SettleRoundsAsync(utcNow, null, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { logger.LogWarning("Settling article monitoring rounds failed with {ExceptionType}.", exception.GetType().Name); }
        if (discovered > 0 || opened > 0 || settled > 0)
            logger.LogInformation("Article link monitoring pass: {Sites} sites, {Discovered} discovered, {Titles} titles read, {Opened} rounds opened, {Settled} rounds settled.",
                sites.Count, discovered, titles, opened, settled);
        return new ArticleLinkMonitoringPassResult(sites.Count, discovered, titles, opened, settled);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.PassInterval > TimeSpan.Zero ? options.Value.PassInterval : TimeSpan.FromMinutes(5);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunPassAsync(DateTimeOffset.UtcNow, stoppingToken);
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                logger.LogWarning("Article link monitoring loop failed with {ExceptionType}.", exception.GetType().Name);
                try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            }
        }
    }
}
