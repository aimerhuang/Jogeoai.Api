using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.Extensions.Options;

namespace Jogeoai.Worker;

/// <summary>口碑情感判定 Worker 配置（配置节 Reputation:Worker）。appsettings 默认启用；配置节缺失时按关闭处理。</summary>
public sealed class ReputationSentimentWorkerOptions
{
    /// <summary>
    /// 是否注册后台任务；代码默认 false（配置节缺失时关闭），appsettings 默认启用。
    /// 回滚：置 Reputation__Worker__Enabled=false 后重启 Worker；关闭时不判定，已写入的判定保留，既有采集与提及分析不受影响。
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>每轮最多处理的工作项数；默认 10。</summary>
    public int BatchSize { get; set; } = 10;

    /// <summary>无工作时的轮询间隔秒数；默认 5。</summary>
    public int PollSeconds { get; set; } = 5;

    /// <summary>单次模型调用超时秒数；默认 60，超时按可重试失败处理。</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>最大尝试次数（含首次）；默认 3，耗尽后工作项进入 failed 终态。</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>租约秒数；默认 180。</summary>
    public int LeaseSeconds { get; set; } = 180;

    /// <summary>置信度阈值，低于该值记 unknown + LOW_CONFIDENCE；默认 0.6。</summary>
    public double ConfidenceThreshold { get; set; } = 0.6;

    /// <summary>
    /// 自动登记的起点（UTC）：只把提及分析完成时间不早于该时刻的有效回答登记为待判定（含新发布版本中的全部回答）。
    /// appsettings 默认 2000-01-01T00:00:00Z，远早于任何数据，因此历史回答自动补录、新回答完成提及分析后自动登记。
    /// 为空时不自动登记，只处理站点级补录任务登记的工作项；更早的历史回答通过 POST reputation-jobs 补录。
    /// </summary>
    public DateTimeOffset? AutoEnqueueSinceUtc { get; set; }

    /// <summary>每轮自动登记的最大条数；默认 200。</summary>
    public int EnqueueBatchSize { get; set; } = 200;
}

/// <summary>单轮情感判定的汇总。</summary>
/// <param name="Enqueued">本轮自动登记的工作项数。</param>
/// <param name="Completed">写入判定（含未关联短路与复用）的工作项数。</param>
/// <param name="NotMentioned">未提及本品牌、未调用模型直接完成的工作项数。</param>
/// <param name="Reused">复用相同输入历史判定的工作项数。</param>
/// <param name="Classified">调用模型并写入判定的工作项数。</param>
/// <param name="Retried">可重试失败数。</param>
/// <param name="Failed">重试耗尽或不可重试的失败数。</param>
/// <param name="Released">因 Provider 未配置或配置无效而退回 pending 的工作项数。</param>
/// <param name="ProviderConfigured">本轮开始时 Provider 是否已配置。</param>
public sealed record ReputationPassResult(int Enqueued, int Completed, int NotMentioned, int Reused, int Classified,
    int Retried, int Failed, int Released, bool ProviderConfigured);

/// <summary>
/// 领取口碑工作项：未提及本品牌直接写 not_mentioned（不调用模型）；已提及时先尝试复用相同输入的历史判定，再调用分类器；
/// 校验后的结论与印象词在一个事务内写入。Provider 未配置时只处理未提及的工作项，已提及的保持 pending。
/// 与采集、提及分析 Worker 完全独立：本 Worker 的任何失败只影响口碑工作项。日志只记录标识、错误码与耗时，不记录回答或模型响应。
/// </summary>
public sealed class ReputationSentimentWorker(
    IReputationStore store,
    IReputationSentimentClassifier classifier,
    IReputationClassifierSettings settings,
    IOptions<ReputationSentimentWorkerOptions> options,
    ILogger<ReputationSentimentWorker> logger) : BackgroundService
{
    private readonly string workerId = $"reputation-{Environment.MachineName}-{Guid.NewGuid():N}";
    private DateTimeOffset lastSchemaWarning = DateTimeOffset.MinValue;
    private DateTimeOffset lastProviderWarning = DateTimeOffset.MinValue;
    private DateTimeOffset providerRejectedUntil = DateTimeOffset.MinValue;

    /// <summary>网关拒绝凭据（401 / 403）后暂停调用分类器的时长，避免每个轮询周期都打一次网关并刷日志。</summary>
    public static readonly TimeSpan ProviderRejectedBackoff = TimeSpan.FromMinutes(10);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var poll = TimeSpan.FromSeconds(Math.Clamp(options.Value.PollSeconds, 1, 300));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await RunPassAsync(stoppingToken);
                if (result.Completed + result.Retried + result.Failed == 0) await Task.Delay(poll, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError("Reputation sentiment worker iteration failed: {Type}. WorkerId={WorkerId}",
                    exception.GetType().Name, workerId);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    /// <summary>执行一轮登记与判定；返回汇总，便于测试与日志。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<ReputationPassResult> RunPassAsync(CancellationToken cancellationToken = default)
    {
        var settingsValue = options.Value;
        if (!await store.SchemaReadyAsync(cancellationToken))
        {
            if (DateTimeOffset.UtcNow - lastSchemaWarning > TimeSpan.FromMinutes(10))
            {
                lastSchemaWarning = DateTimeOffset.UtcNow;
                logger.LogWarning("Reputation sentiment worker idle: migration 038 is not applied (REPUTATION_SCHEMA_NOT_MIGRATED).");
            }
            return new ReputationPassResult(0, 0, 0, 0, 0, 0, 0, 0, false);
        }
        var maxAttempts = Math.Clamp(settingsValue.MaxAttempts, 1, 10);
        // 自动登记时在同一事务内收尾耗尽的工作项；不自动登记（默认）时单独收尾，避免最后一次尝试中断的工作项永久待判定。
        var enqueued = 0;
        if (settingsValue.AutoEnqueueSinceUtc is { } since)
            enqueued = await store.EnqueueRecentAsync(since, Math.Clamp(settingsValue.EnqueueBatchSize, 1, 5000), maxAttempts,
                cancellationToken);
        else
            await store.ExpireExhaustedAsync(maxAttempts, cancellationToken);
        var configuration = await settings.GetAsync(cancellationToken);
        var configuredAtStart = configuration is not null;
        // 凭据被拒后的退避期内按未就绪处理：已提及的工作项保持 pending，未提及的照常短路完成。
        var providerReady = configuredAtStart && DateTimeOffset.UtcNow >= providerRejectedUntil;
        if (!configuredAtStart && DateTimeOffset.UtcNow - lastProviderWarning > TimeSpan.FromMinutes(10))
        {
            lastProviderWarning = DateTimeOffset.UtcNow;
            logger.LogWarning("Reputation classifier provider is not configured (PROVIDER_NOT_CONFIGURED); mentioned answers stay pending.");
        }
        int completed = 0, notMentioned = 0, reused = 0, classified = 0, retried = 0, failed = 0, released = 0;
        var lease = TimeSpan.FromSeconds(Math.Clamp(settingsValue.LeaseSeconds, 30, 3600));
        for (var i = 0; i < Math.Clamp(settingsValue.BatchSize, 1, 100); i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var work = await store.ClaimAsync(workerId, providerReady, lease, maxAttempts, cancellationToken);
            if (work is null) break;
            try
            {
                if (!work.BrandMentioned)
                {
                    // not_mentioned 只依据现有提及分析写入，不调用分类器。
                    var decision = new ReputationClassification(ReputationSentimentState.NotMentioned, null,
                        ReputationRules.NotMentionedCode, null, null, [], ReputationRules.ClassifierVersion, null);
                    if (await store.CompleteAsync(work, workerId, decision,
                            ReputationRules.InputHash(work.BrandName, work.BrandAliases, work.Answer), cancellationToken))
                    { completed++; notMentioned++; }
                    continue;
                }
                if (!providerReady || configuration is null)
                {
                    await store.ReleaseAsync(work, workerId, "PROVIDER_NOT_CONFIGURED", cancellationToken);
                    released++;
                    continue;
                }
                var threshold = Math.Clamp(settingsValue.ConfidenceThreshold, 0, 1);
                var inputHash = ReputationRules.InputHash(work.BrandName, work.BrandAliases, work.Answer,
                    work.ExcludedTerms, threshold);
                if (await store.TryReuseAsync(work, workerId, inputHash, cancellationToken))
                { completed++; reused++; continue; }
                var input = new ReputationClassificationInput(work.BrandName, work.BrandAliases, work.ExcludedTerms,
                    work.Answer, work.MentionSnippets, threshold);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settingsValue.TimeoutSeconds, 5, 600)));
                ReputationClassification result;
                try
                {
                    result = await classifier.ClassifyAsync(input, configuration, timeout.Token);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw new ReputationClassifierException("REPUTATION_PROVIDER_TIMEOUT", "情感判定超时。", true);
                }
                if (await store.CompleteAsync(work, workerId, result, inputHash, cancellationToken))
                { completed++; classified++; }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (ReputationClassifierException exception) when (exception.Code == ReputationClassifierException.ProviderConfigInvalid)
            {
                // 配置无效不消耗重试次数；退避期内不再领取已提及的工作项。
                providerReady = false;
                providerRejectedUntil = DateTimeOffset.UtcNow + ProviderRejectedBackoff;
                await TryAsync(() => store.ReleaseAsync(work, workerId, exception.Code, cancellationToken), work);
                released++;
                logger.LogWarning("Reputation classifier configuration rejected ({Code}); work released. Check={CheckId}",
                    exception.Code, work.CheckId);
            }
            catch (ReputationClassifierException exception)
            {
                var terminal = !exception.IsRetryable || work.AttemptCount >= maxAttempts;
                await TryAsync(() => store.FailAsync(work, workerId, exception.Code, terminal, cancellationToken), work);
                if (terminal) failed++; else retried++;
                logger.LogWarning("Reputation classification failed ({Code}, attempt {Attempt}, terminal {Terminal}). Check={CheckId}",
                    exception.Code, work.AttemptCount, terminal, work.CheckId);
            }
            catch (Exception exception)
            {
                var terminal = work.AttemptCount >= maxAttempts;
                await TryAsync(() => store.FailAsync(work, workerId, "REPUTATION_ERROR", terminal, cancellationToken), work);
                if (terminal) failed++; else retried++;
                logger.LogWarning("Reputation work failed: {Type} (attempt {Attempt}). Check={CheckId}",
                    exception.GetType().Name, work.AttemptCount, work.CheckId);
            }
        }
        if (completed + retried + failed + released > 0)
            logger.LogInformation("Reputation pass: enqueued {Enqueued}, completed {Completed} (not mentioned {NotMentioned}, reused {Reused}, classified {Classified}), retry {Retried}, failed {Failed}, released {Released}.",
                enqueued, completed, notMentioned, reused, classified, retried, failed, released);
        return new ReputationPassResult(enqueued, completed, notMentioned, reused, classified, retried, failed, released,
            configuredAtStart);
    }

    private async Task TryAsync(Func<Task> action, ReputationWorkLease work)
    {
        try { await action(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            logger.LogError("Failed to persist reputation work state: {Type}. Check={CheckId}", exception.GetType().Name, work.CheckId);
        }
    }
}
