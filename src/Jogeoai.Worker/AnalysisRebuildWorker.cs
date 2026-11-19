using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.Extensions.Options;

namespace Jogeoai.Worker;

/// <summary>识别配置或识别器版本变化后自动重算的 Worker 配置（配置节 AnalysisRebuild）。</summary>
public sealed class AnalysisRebuildOptions
{
    /// <summary>是否启用自动重算；false 时不注册后台任务，恢复手动发起（回滚开关）。默认 true。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>两次检测之间的间隔秒数；默认 30。</summary>
    public int IntervalSeconds { get; set; } = 30;

    /// <summary>最后一次识别配置修改后的合并等待秒数；默认 60。识别器版本变化不等待。</summary>
    public int SettleSeconds { get; set; } = 60;

    /// <summary>每轮最多检查的站点数；默认 20。</summary>
    public int BatchSize { get; set; } = 20;
}

/// <summary>单轮自动重算检测的汇总。</summary>
/// <param name="Candidates">查询到的落后站点数。</param>
/// <param name="Started">本轮新发起（或首次观察到）的重算作业数。</param>
/// <param name="Waiting">仍在合并等待中的站点数。</param>
/// <param name="Skipped">因已有进行中重算、作业已失败或单站点出错而跳过的站点数。</param>
public sealed record AnalysisRebuildPassResult(int Candidates, int Started, int Waiting, int Skipped);

/// <summary>
/// 定期检测已发布分析结果落后于当前识别配置或识别器版本的站点，并通过与手动作业相同的
/// <see cref="IAnalysisStore.CreateJobAsync"/> 发起全站重算（不带范围、不复用旧事实）。
/// 幂等键按站点、配置版本与识别器版本生成，多个 Worker 并发时不会产生重复作业；失败的作业不自动重试。
/// </summary>
public sealed class AnalysisRebuildWorker(
    IAnalysisStore store,
    IOptions<AnalysisRebuildOptions> options,
    ILogger<AnalysisRebuildWorker> logger) : BackgroundService
{
    // 已记录过日志的幂等键：同一个键的作业只记一次“已发起”或“已失败”日志。
    private readonly HashSet<string> startedKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> failedKeys = new(StringComparer.Ordinal);

    /// <summary>执行一轮检测与发起；返回汇总，便于测试与日志。</summary>
    /// <param name="utcNow">本轮使用的 UTC 时间。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<AnalysisRebuildPassResult> RunPassAsync(DateTimeOffset utcNow, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        var settle = TimeSpan.FromSeconds(Math.Max(0, settings.SettleSeconds));
        var candidates = await store.FindSitesNeedingRebuildAsync(Math.Max(1, settings.BatchSize), cancellationToken);
        int started = 0, waiting = 0, skipped = 0;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var decision = AnalysisRebuildRules.Decide(candidate.LatestConfigVersion, candidate.ReleaseConfigVersion,
                candidate.ReleaseExtractorVersion, MentionMatcher.Version, candidate.LatestRevisionCreatedAt, utcNow, settle);
            if (!decision.Rebuild)
            {
                if (decision.Reason == AnalysisRebuildRules.WaitingForSettle) waiting++;
                continue;
            }
            var key = AnalysisRebuildRules.IdempotencyKey(candidate.SiteId, candidate.LatestConfigVersion, MentionMatcher.Version);
            try
            {
                var job = await store.CreateJobAsync(candidate.TenantId, candidate.SiteId, key,
                    new AnalysisJobRequest(), cancellationToken);
                if (job.Status == "failed")
                {
                    skipped++;
                    if (failedKeys.Add(key))
                        logger.LogWarning("Automatic analysis rebuild {JobId} for site {SiteId} (key {IdempotencyKey}) has failed; it will not be retried automatically. Start a manual analysis job after fixing the cause.",
                            job.JobId, candidate.SiteId, key);
                    continue;
                }
                if (startedKeys.Add(key))
                {
                    started++;
                    logger.LogInformation("Automatic analysis rebuild {JobId} started for site {SiteId}: config {FromConfig} -> {ToConfig}, extractor {FromExtractor} -> {ToExtractor}, reason {Reason}, effective scope {EffectiveScope}, {TotalChecks} checks.",
                        job.JobId, candidate.SiteId, candidate.ReleaseConfigVersion, candidate.LatestConfigVersion,
                        candidate.ReleaseExtractorVersion, MentionMatcher.Version, decision.Reason, job.EffectiveScope, job.TotalChecks);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (CollectorStoreException exception) when (exception.Code == "ANALYSIS_VERSION_NOT_READY")
            {
                // 另一个 Worker 或手动作业已在重算该站点：等它发布后再检查。
                skipped++;
            }
            catch (Exception exception)
            {
                skipped++;
                logger.LogWarning(exception, "Automatic analysis rebuild failed to start for site {SiteId} (key {IdempotencyKey}).",
                    candidate.SiteId, key);
            }
        }
        return new AnalysisRebuildPassResult(candidates.Count, started, waiting, skipped);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(options.Value.IntervalSeconds > 0 ? options.Value.IntervalSeconds : 30);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunPassAsync(DateTimeOffset.UtcNow, stoppingToken);
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Analysis rebuild worker iteration failed");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }
}
