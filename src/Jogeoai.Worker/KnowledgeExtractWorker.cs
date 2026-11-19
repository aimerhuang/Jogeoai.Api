using System.Security.Cryptography;
using System.Text;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;

namespace Jogeoai.Worker;

/// <summary>
/// 领取网址识别任务：fetch（<see cref="IArticleSourceReader"/> 安全读取公开网页；读不到或正文过少时由
/// <see cref="IKnowledgePageReadProvider"/> 请模型联网打开该网址）→ model（固定 Luna 模型识别候选）→
/// finalize（原文依据校验、批内去重与已有条目查重）→ 写入候选。网页正文只在内存中使用，不落库、不写日志；
/// 心跳发现任务被取消或租约丢失时立即放弃本次执行。
/// </summary>
public sealed class KnowledgeExtractWorker(
    IKnowledgeExtractJobStore store,
    IArticleSourceReader reader,
    IKnowledgePageReadProvider pageReader,
    IKnowledgeExtractProvider provider,
    IBrandKnowledgeItemStore items,
    IResearchStore research,
    ILogger<KnowledgeExtractWorker> logger) : BackgroundService
{
    /// <summary>网页正文的最少字数，少于该值直接失败为 KNOWLEDGE_EXTRACT_SOURCE_TOO_SHORT。</summary>
    public const int MinPageCharacters = 200;
    /// <summary>模型调用超时上限（秒）；配置为 0 或更大时取该值。</summary>
    public const int MaxModelTimeoutSeconds = 180;

    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    private readonly string workerId = $"knowledge-extract-{Environment.MachineName}-{Guid.NewGuid():N}";

    /// <summary>心跳间隔（默认 30 秒，租约 2 分钟）；测试可缩短以验证取消。</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>AI 联网读取网页的最多尝试次数：首次未得到可用正文（未打开 / 超时 / 网关错误 / 正文过少）时再试一次。</summary>
    public const int ModelPageReadAttempts = 2;

    /// <summary>AI 联网读取两次尝试之间的默认等待（2 秒）。</summary>
    public static readonly TimeSpan DefaultModelPageReadRetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>AI 联网读取两次尝试之间的等待（默认 <see cref="DefaultModelPageReadRetryDelay"/>）；测试可缩短。</summary>
    public TimeSpan ModelPageReadRetryDelay { get; init; } = DefaultModelPageReadRetryDelay;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await RunOnceAsync(stoppingToken)) await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                // 任务级错误已写入任务表；这里只记录循环异常与 Worker 标识，不记录网页内容。
                logger.LogError(exception, "Knowledge extract worker iteration failed. WorkerId={WorkerId}", workerId);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    /// <summary>领取并处理一条识别任务。</summary>
    /// <param name="stoppingToken">停止令牌。</param>
    /// <returns>是否领取到任务。</returns>
    public async Task<bool> RunOnceAsync(CancellationToken stoppingToken = default)
    {
        var job = await store.ClaimAsync(workerId, Lease, stoppingToken);
        if (job is null) return false;
        await ProcessAsync(job, stoppingToken);
        return true;
    }

    private async Task ProcessAsync(KnowledgeExtractJobLease job, CancellationToken stoppingToken)
    {
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var stop = KnowledgeExtractHeartbeat.Alive;
        var heartbeat = MaintainLeaseAsync(job, execution, state => stop = state, stoppingToken);
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (!await store.MarkStepAsync(job, "fetch", execution.Token)) { LogStopped(job, KnowledgeExtractHeartbeat.LeaseLost); return; }
            // 配置在读取前加载：直接读取失败时的 AI 联网读取兜底与模型识别共用同一配置。
            var configuration = await research.GetProviderConfigurationAsync("openai-compatible", execution.Token)
                ?? await research.GetProviderConfigurationAsync("openai-api", execution.Token);
            if (configuration is null || string.IsNullOrWhiteSpace(configuration.Credential))
                throw new KnowledgeExtractException("KNOWLEDGE_EXTRACT_NOT_CONFIGURED", "网址识别服务未配置。", false);
            var fetched = await FetchPageAsync(job, configuration, execution.Token);
            var body = fetched.Body;
            var pageCharacters = BrandKnowledgeRules.CharacterCount(body);

            if (!await store.MarkStepAsync(job, "model", execution.Token)) { LogStopped(job, KnowledgeExtractHeartbeat.LeaseLost); return; }
            var existing = (await items.ListAsync(job.TenantId, job.SiteId, null, execution.Token)).Items;
            var input = new KnowledgeExtractInput(job.BrandName, job.Url, fetched.Title, body, job.Dimensions,
                existing.Select(item => new KnowledgeExtractExistingTitle(item.Dimension, item.Title)).ToArray());

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(execution.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(ModelTimeoutSeconds(configuration.TimeoutSeconds)));
            KnowledgeExtractProviderResult result;
            try
            {
                result = await provider.ExtractAsync(input, configuration, timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !execution.IsCancellationRequested)
            {
                throw new KnowledgeExtractException("KNOWLEDGE_EXTRACT_TIMEOUT", "模型识别超时，请稍后重试。", true);
            }

            if (!await store.MarkStepAsync(job, "finalize", execution.Token)) { LogStopped(job, KnowledgeExtractHeartbeat.LeaseLost); return; }
            var normalized = BrandKnowledgeRules.NormalizeCandidates(result.Items, body, job.Dimensions, existing);
            var completion = new KnowledgeExtractCompletion(fetched.Title,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant(), pageCharacters,
                result.Truncated || normalized.Truncated, normalized.Candidates, result.ModelName, result.Provider, result.DurationMs);
            if (!await store.CompleteAsync(job, completion, stoppingToken)) { LogStopped(job, stop == KnowledgeExtractHeartbeat.Alive ? KnowledgeExtractHeartbeat.LeaseLost : stop); return; }
            logger.LogInformation(
                "Knowledge extract job {JobId} completed. Tenant={TenantId} Model={Model} ReadMode={ReadMode} PageChars={PageChars} RequestBytes={RequestBytes} ResponseBytes={ResponseBytes} Raw={RawCount} Kept={Kept} Dropped={Dropped} Duplicates={Duplicates} ModelMs={ModelMs} DurationMs={DurationMs}",
                job.JobId, job.TenantId, result.ModelName, fetched.Mode, pageCharacters, result.RequestBytes, result.ResponseBytes, result.Items.Count,
                normalized.Candidates.Count, normalized.DroppedCount, normalized.Candidates.Count(candidate => candidate.DuplicateOfItemId is not null),
                result.DurationMs, started.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (stop != KnowledgeExtractHeartbeat.Alive && !stoppingToken.IsCancellationRequested)
        {
            LogStopped(job, stop);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
        catch (KnowledgeExtractException exception)
        {
            await TryFailAsync(job, exception.Code, exception.Message, exception.Retryable, stoppingToken);
        }
        catch (CollectorStoreException exception)
        {
            await TryFailAsync(job, exception.Code, exception.Message, exception.StatusCode >= 500, stoppingToken);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning("Knowledge extract request failed for job {JobId}: {Type}", job.JobId, exception.GetType().Name);
            await TryFailAsync(job, "KNOWLEDGE_EXTRACT_RETRYABLE", "识别服务暂时不可用，请稍后重试。", true, stoppingToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning("Knowledge extract job {JobId} failed: {Type}", job.JobId, exception.GetType().Name);
            await TryFailAsync(job, "KNOWLEDGE_EXTRACT_FAILED", "识别失败，请稍后重试。", false, stoppingToken);
        }
        finally
        {
            execution.Cancel();
            try { await heartbeat; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception) { logger.LogWarning(exception, "Knowledge extract heartbeat stopped for job {JobId}", job.JobId); }
        }
    }

    /// <summary>
    /// 读取网页正文：先用 <see cref="IArticleSourceReader"/> 直接读取；直接读取失败或正文不足 200 字、且主机是公开 DNS 域名时，
    /// 请模型联网打开该网址（<see cref="IKnowledgePageReadProvider"/>），首次未得到可用正文时等待
    /// <see cref="ModelPageReadRetryDelay"/> 后再试一次（共 <see cref="ModelPageReadAttempts"/> 次）。日志只记录读取方式、尝试次数、状态码、耗时与字节数，不记录正文。
    /// </summary>
    private async Task<FetchedPage> FetchPageAsync(KnowledgeExtractJobLease job, ResearchProviderConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var directStarted = System.Diagnostics.Stopwatch.StartNew();
        var page = await reader.ReadAsync(job.Url, cancellationToken);
        var directMs = directStarted.ElapsedMilliseconds;
        var directReady = string.Equals(page.ReadStatus, "ready", StringComparison.Ordinal);
        var directBody = directReady ? (page.BodyMarkdown ?? string.Empty).Trim() : string.Empty;
        var directCharacters = BrandKnowledgeRules.CharacterCount(directBody);
        if (directReady && directCharacters >= MinPageCharacters)
        {
            logger.LogInformation("Knowledge extract job {JobId} page read. ReadMode=direct DirectMs={DirectMs} PageChars={PageChars}",
                job.JobId, directMs, directCharacters);
            return new FetchedPage(page.Title, directBody, "direct");
        }

        var directReason = string.IsNullOrWhiteSpace(page.ErrorMessage) ? "网页无法读取。" : page.ErrorMessage.Trim();
        if (!ArticleSourceReader.IsPublicDnsHostName(job.Url))
        {
            logger.LogInformation("Knowledge extract job {JobId} direct read not usable and host is not eligible for model read. DirectStatus={DirectStatus} DirectCode={DirectCode} DirectMs={DirectMs}",
                job.JobId, page.ReadStatus, page.ErrorCode, directMs);
            throw directReady ? TooShort() : new KnowledgeExtractException("KNOWLEDGE_LINK_UNREADABLE", directReason, false);
        }

        KnowledgePageReadResult fallback = null!;
        for (var attempt = 1; attempt <= ModelPageReadAttempts; attempt++)
        {
            // 两次尝试之间短暂等待；等待与读取都使用执行令牌，用户取消、租约丢失或停止时立即中断，不再重试。
            if (attempt > 1) await Task.Delay(ModelPageReadRetryDelay, cancellationToken);
            fallback = await pageReader.ReadAsync(job.Url, configuration, cancellationToken);
            var fallbackBody = fallback.Succeeded ? (fallback.BodyText ?? string.Empty).Trim() : string.Empty;
            var fallbackCharacters = BrandKnowledgeRules.CharacterCount(fallbackBody);
            var usable = fallback.Succeeded && fallbackCharacters >= MinPageCharacters;
            logger.LogInformation(
                "Knowledge extract job {JobId} model page read finished. Attempt={Attempt}/{MaxAttempts} Succeeded={Succeeded} Usable={Usable} DirectStatus={DirectStatus} DirectCode={DirectCode} DirectChars={DirectChars} DirectMs={DirectMs} ModelReadMs={ModelReadMs} RequestBytes={RequestBytes} ResponseBytes={ResponseBytes} PageChars={PageChars} Citations={Citations}",
                job.JobId, attempt, ModelPageReadAttempts, fallback.Succeeded, usable, page.ReadStatus, page.ErrorCode, directCharacters, directMs,
                fallback.DurationMs, fallback.RequestBytes, fallback.ResponseBytes, fallbackCharacters, fallback.CitationUrls.Count);
            if (usable)
                return new FetchedPage(directReady && !string.IsNullOrWhiteSpace(page.Title) ? page.Title : null, fallbackBody, "model");
        }

        if (directReady) throw TooShort();
        var fallbackReason = !fallback.Succeeded
            ? (string.IsNullOrWhiteSpace(fallback.ErrorMessage) ? "AI 未能打开该网页。" : fallback.ErrorMessage.Trim())
            : $"读取到的正文过少（不足 {MinPageCharacters} 字）。";
        throw new KnowledgeExtractException("KNOWLEDGE_LINK_UNREADABLE",
            $"网页无法直接读取（{directReason.TrimEnd('。', '.')}），AI 联网读取也未成功：{EndSentence(fallbackReason)}", false);
    }

    private static KnowledgeExtractException TooShort()
        => new("KNOWLEDGE_EXTRACT_SOURCE_TOO_SHORT", $"网页正文过少（不足 {MinPageCharacters} 字），无法识别。", false);

    private static string EndSentence(string value) => value.EndsWith('。') ? value : value.TrimEnd('.') + "。";

    /// <summary>用于识别的网页：标题、正文与读取方式（direct / model）。</summary>
    private sealed record FetchedPage(string? Title, string Body, string Mode);

    /// <summary>模型超时秒数：取配置值，未配置（≤ 0）或超过 180 秒时取 180 秒。</summary>
    /// <param name="configuredSeconds">Provider 配置的超时秒数。</param>
    public static int ModelTimeoutSeconds(int configuredSeconds)
        => configuredSeconds <= 0 ? MaxModelTimeoutSeconds : Math.Clamp(configuredSeconds, 1, MaxModelTimeoutSeconds);

    private async Task MaintainLeaseAsync(KnowledgeExtractJobLease job, CancellationTokenSource execution, Action<KnowledgeExtractHeartbeat> markStopped,
        CancellationToken stoppingToken)
    {
        try
        {
            while (!execution.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(HeartbeatInterval, execution.Token);
                if (execution.IsCancellationRequested) break;
                var state = await store.HeartbeatAsync(job, Lease, stoppingToken);
                if (state != KnowledgeExtractHeartbeat.Alive)
                {
                    markStopped(state);
                    execution.Cancel();
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested || execution.IsCancellationRequested) { }
        catch (Exception)
        {
            // 心跳失败按租约丢失处理，避免另一 Worker 领取后本 Worker 仍写入结果。
            markStopped(KnowledgeExtractHeartbeat.LeaseLost);
            execution.Cancel();
        }
    }

    private void LogStopped(KnowledgeExtractJobLease job, KnowledgeExtractHeartbeat state)
    {
        if (state == KnowledgeExtractHeartbeat.Cancelled)
            logger.LogInformation("Knowledge extract job {JobId} was cancelled by the user; execution abandoned", job.JobId);
        else
            logger.LogWarning("Knowledge extract job {JobId} lease was fenced", job.JobId);
    }

    private async Task TryFailAsync(KnowledgeExtractJobLease job, string code, string message, bool retryable, CancellationToken cancellationToken)
    {
        try
        {
            if (!await store.FailAsync(job, code, message, retryable, cancellationToken))
                logger.LogWarning("Knowledge extract job {JobId} failure {Code} not recorded: lease fenced or job cancelled", job.JobId, code);
            else
                logger.LogInformation("Knowledge extract job {JobId} failed with {Code}. Retryable={Retryable} Attempt={Attempt}",
                    job.JobId, code, retryable, job.AttemptCount);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { logger.LogError(exception, "Failed to persist knowledge extract failure for job {JobId}", job.JobId); }
    }
}
