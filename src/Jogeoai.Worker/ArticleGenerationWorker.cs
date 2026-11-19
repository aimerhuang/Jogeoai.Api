using System.Text.Json;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;

namespace Jogeoai.Worker;

/// <summary>领取文章生成任务，固定调用 DeepSeek 文章 Provider，并在成功/失败时结算积分；pipeline = studio_v1 的任务交给文章创作六阶段管线。</summary>
public sealed class ArticleGenerationWorker(
    IContentStore store,
    IResearchStore research,
    IKnowledgeRetrievalService? knowledge,
    IArticleGenerationProvider provider,
    ILogger<ArticleGenerationWorker> logger,
    ArticleStudioPipeline? studioPipeline = null) : BackgroundService
{
    private readonly string workerId = $"article-generation-{Environment.MachineName}-{Guid.NewGuid():N}";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // The provider deadline is 15 minutes.  Keep the database lease
                // at the same upper bound so a long request is not reclaimed by
                // a second worker before its result can be fenced.
                var task = await store.ClaimGenerationTaskAsync(workerId, TimeSpan.FromMinutes(15), stoppingToken);
                if (task is null) { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); continue; }
                await ProcessAsync(task, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Article generation worker iteration failed");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task ProcessAsync(ArticleGenerationWorkItem task, CancellationToken stoppingToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(15));
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var leaseLost = false;
        var heartbeat = MaintainLeaseAsync(task, execution, () => leaseLost = true, stoppingToken);
        try
        {
            var configuration = await research.GetProviderConfigurationAsync(ArticleGenerationProvider.ProviderName, execution.Token);
            if (configuration is null || string.IsNullOrWhiteSpace(configuration.Credential))
                throw new CollectorStoreException("ARTICLE_PROVIDER_UNAVAILABLE", "文章生成 Provider 未配置。", 503);

            // A task must contain the immutable context captured at preview/
            // submit time.  Reading the live context by ContextId here would
            // allow a document update to change a queued task's evidence.
            var evidence = ReadFrozenEvidence(task);
            if (!task.WithoutRag && evidence.Count == 0)
                throw new CollectorStoreException("KNOWLEDGE_CONTEXT_EMPTY", "文章生成缺少已冻结的知识依据。", 422);

            // Keep the dependency in the constructor for compatibility with
            // existing hosts.  It is deliberately never used for generation.
            _ = knowledge;
            ArticleGenerationResult result;
            if (task.Pipeline == ArticleStudioLimits.StudioPipeline)
            {
                if (studioPipeline is null)
                    throw new CollectorStoreException("ARTICLE_STUDIO_UNAVAILABLE", "文章创作管线未在当前 Worker 中启用。", 503);
                result = await studioPipeline.RunAsync(task, configuration, evidence, execution.Token);
            }
            else
            {
                // Legacy wizard path: unchanged single Provider call with no references.
                result = await provider.GenerateAsync(task, configuration, evidence, [], execution.Token);
            }
            await store.CompleteGenerationTaskAsync(task, result, stoppingToken);
        }
        catch (OperationCanceledException) when (leaseLost)
        {
            // A different worker owns the lease now.  Do not release credits or
            // persist a result from this fenced worker.
            logger.LogWarning("Article generation task {TaskId} lease was fenced", task.TaskId);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Article generation task {TaskId} exceeded the 15 minute deadline", task.TaskId);
            await TryFailAsync(task, "ARTICLE_GENERATION_TIMEOUT", "文章生成超过时间限制。", true, stoppingToken);
        }
        catch (ArticleStudioLeaseLostException)
        {
            // A stage/source write found another owner: same fencing rule as a lost heartbeat.
            logger.LogWarning("Article generation task {TaskId} lease was fenced during a studio stage", task.TaskId);
        }
        catch (CollectorStoreException exception)
        {
            await TryFailAsync(task, exception.Code, exception.Message, exception.StatusCode >= 500, stoppingToken);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning("Article generation provider request failed for task {TaskId}: {Type}", task.TaskId, exception.GetType().Name);
            await TryFailAsync(task, "ARTICLE_PROVIDER_RETRYABLE", "文章生成服务暂时不可用。", true, stoppingToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning("Article generation task {TaskId} failed: {Type}", task.TaskId, exception.GetType().Name);
            await TryFailAsync(task, "ARTICLE_GENERATION_FAILED", "文章生成失败，请稍后重试。", false, stoppingToken);
        }
        finally
        {
            execution.Cancel();
            try { await heartbeat; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception) { logger.LogWarning(exception, "Article generation heartbeat stopped for task {TaskId}", task.TaskId); }
        }
    }

    private async Task MaintainLeaseAsync(ArticleGenerationWorkItem task, CancellationTokenSource execution,
        Action markLeaseLost, CancellationToken stoppingToken)
    {
        try
        {
            while (!execution.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), execution.Token);
                if (execution.IsCancellationRequested) break;
                if (!await store.HeartbeatGenerationTaskAsync(task, TimeSpan.FromMinutes(3), stoppingToken))
                {
                    markLeaseLost();
                    execution.Cancel();
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested || execution.IsCancellationRequested) { }
        catch (Exception)
        {
            // A failed heartbeat is treated as a lost lease.  Continuing to
            // call the Provider would only produce a result that cannot be
            // safely committed after another worker takes over.
            markLeaseLost();
            execution.Cancel();
        }
    }

    private async Task TryFailAsync(ArticleGenerationWorkItem task, string code, string message,
        bool retryable, CancellationToken cancellationToken)
    {
        try { await store.FailGenerationTaskAsync(task, code, message, retryable, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { logger.LogError(exception, "Failed to persist article generation failure for task {TaskId}", task.TaskId); }
    }

    private static IReadOnlyList<KnowledgeEvidenceView> ReadFrozenEvidence(ArticleGenerationWorkItem task)
    {
        if (task.WithoutRag) return [];
        if (string.IsNullOrWhiteSpace(task.ContextSnapshot)) return [];
        try
        {
            var context = JsonSerializer.Deserialize<KnowledgeContextResponse>(task.ContextSnapshot, JsonOptions);
            if (context is null || context.ContextId != (task.ContextId ?? context.ContextId))
                throw new JsonException();
            // context_snapshot is a MySQL JSON column, so the stored text is
            // normalized; verify the re-serialized object, not the raw string.
            if (!string.IsNullOrWhiteSpace(task.ContextHash))
            {
                var hash = KnowledgeContextSnapshot.Hash(context);
                if (!string.Equals(hash, task.ContextHash, StringComparison.OrdinalIgnoreCase))
                    throw new CollectorStoreException("KNOWLEDGE_CONTEXT_STALE", "文章参考素材快照校验失败。", 409);
            }
            return context.Items;
        }
        catch (CollectorStoreException) { throw; }
        catch (Exception) when (task.ContextSnapshot is not null)
        {
            throw new CollectorStoreException("KNOWLEDGE_CONTEXT_STALE", "文章参考素材快照无效，请重新预览。", 409);
        }
    }
}
