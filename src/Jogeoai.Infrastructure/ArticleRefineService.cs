using Jogeoai.Application;
using Jogeoai.Domain;

namespace Jogeoai.Infrastructure;

/// <summary>
/// 智能微调编排：幂等重放 → 检查 deepseek-article 配置 → 预占 40 积分（短事务）→ 不持有数据库连接地同步调用模型（≤120 秒）
/// → 校验输出并写入 refined 版本 + 结算；任何失败都释放预占。
/// </summary>
public sealed class ArticleRefineService(
    IArticleEditorStore store,
    IResearchStore research,
    IArticleRefineProvider provider) : IArticleRefineService
{
    /// <inheritdoc />
    public async Task<ArticleRefineResult> RefineAsync(Guid tenantId, Guid siteId, Guid articleId, Guid userId,
        ArticleRefineRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey)) throw new CollectorStoreException("IDEMPOTENCY_KEY_REQUIRED", "请提供 Idempotency-Key。", 400);
        if (idempotencyKey.Length > 191) throw new CollectorStoreException("INVALID_IDEMPOTENCY_KEY", "幂等键无效。", 400);
        var normalized = ArticleRefineRules.Normalize(request);
        var requestHash = ArticleRefineRules.RequestHash(siteId, articleId, normalized);

        // Replays are resolved before any balance, provider or article state is read again.
        if (await store.FindRefineAsync(tenantId, idempotencyKey, cancellationToken) is { } existing)
        {
            if (existing.SiteId != siteId || existing.ArticleId != articleId
                || !string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
                throw new CollectorStoreException("IDEMPOTENCY_CONFLICT", "幂等键已用于不同的微调请求。", 409);
            return existing.Status switch
            {
                CreditReservationStatus.Captured => await store.GetRefineResultAsync(tenantId, existing.OperationId, cancellationToken)
                    ?? throw new CollectorStoreException("CREDIT_RECONCILIATION_REQUIRED", "微调结算记录缺少结果版本。", 503),
                CreditReservationStatus.Released => throw new CollectorStoreException(existing.ErrorCode ?? "REFINE_FAILED",
                    "该请求的智能微调已失败并释放积分，未扣费；请重新发起。", ArticleRefineRules.StatusFor(existing.ErrorCode)),
                _ => throw new CollectorStoreException(ArticleEditorErrorCodes.RefineInProgress, "该请求的智能微调正在进行，请稍后重试。", 409),
            };
        }

        var configuration = await research.GetProviderConfigurationAsync(ArticleGenerationProvider.ProviderName, cancellationToken);
        if (configuration is null || string.IsNullOrWhiteSpace(configuration.Credential))
            throw new CollectorStoreException("ARTICLE_PROVIDER_UNAVAILABLE", "文章生成 Provider 未配置。", 503);

        var hold = await store.ReserveRefineAsync(new ArticleRefineReservation(tenantId, siteId, articleId, userId, idempotencyKey,
            requestHash, normalized.Scope, normalized.SelectionMarkdown, normalized.BaseVersionId, normalized.ExpectedRevision,
            ArticleRefinePricing.UnitPrice), cancellationToken);

        (string Title, string Body) refined;
        var seconds = configuration.TimeoutSeconds is > 0 and < ArticleEditorLimits.RefineTimeoutSeconds
            ? configuration.TimeoutSeconds : ArticleEditorLimits.RefineTimeoutSeconds;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
            try
            {
                var output = await provider.RefineAsync(configuration, new ArticleRefineModelRequest(normalized.Scope, normalized.Instruction,
                    hold.Title, hold.BodyMarkdown, normalized.SelectionMarkdown, hold.Language), timeout.Token);
                refined = ArticleRefineRules.BuildResult(hold, normalized.Scope, normalized.SelectionMarkdown, output);
            }
            catch (Exception exception)
            {
                var failure = exception switch
                {
                    CollectorStoreException known => known,
                    OperationCanceledException when cancellationToken.IsCancellationRequested
                        => new CollectorStoreException("REFINE_CANCELLED", "请求已取消，积分已释放。", 503),
                    OperationCanceledException => new CollectorStoreException(ArticleEditorErrorCodes.RefineTimeout, "智能微调超时，积分已释放，请稍后重试。", 504),
                    _ => new CollectorStoreException("REFINE_FAILED", "智能微调失败，积分已释放。", 502),
                };
                // Settlement must not depend on the caller still waiting.
                await store.ReleaseRefineAsync(tenantId, hold.OperationId, failure.Code, CancellationToken.None);
                if (ReferenceEquals(failure, exception)) throw;
                throw failure;
            }
        }
        try
        {
            return await store.CompleteRefineAsync(tenantId, hold.OperationId, refined.Title, refined.Body, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not CollectorStoreException)
        {
            // Settlement failed unexpectedly (database or connection error). Release is a no-op when the capture did commit
            // (status is no longer held), otherwise it returns the 40 credits now instead of after the abandon sweep.
            try { await store.ReleaseRefineAsync(tenantId, hold.OperationId, "REFINE_SETTLEMENT_FAILED", CancellationToken.None); }
            catch (Exception) { /* the abandoned-hold sweep releases it later */ }
            throw;
        }
    }
}
