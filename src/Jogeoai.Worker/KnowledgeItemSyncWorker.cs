using Jogeoai.Application;
using Jogeoai.Domain;

namespace Jogeoai.Worker;

/// <summary>
/// 品牌知识条目检索投影的唯一写入方：领取待同步条目 → 未删除时渲染 Markdown 上传为知识库资料（首次新建、之后为新版本），
/// 已删除时停用资料 → 只在内容版本未再变化时写 synced。上传后的摄取由 <see cref="KnowledgeIngestWorker"/> 完成。日志不含条目标题与正文。
/// </summary>
public sealed class KnowledgeItemSyncWorker(
    IBrandKnowledgeItemStore store,
    IKnowledgeStore knowledge,
    ILogger<KnowledgeItemSyncWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(2);
    private readonly string workerId = $"knowledge-item-sync-{Environment.MachineName}-{Guid.NewGuid():N}";

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await RunOnceAsync(stoppingToken)) await Task.Delay(IdleDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                // 条目级错误已写入 sync_error；这里只记录循环异常与 Worker 标识，不记录条目内容。
                logger.LogError(exception, "Knowledge item sync worker iteration failed. WorkerId={WorkerId}", workerId);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    /// <summary>领取并处理一条待同步条目。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>是否领取到条目。</returns>
    public async Task<bool> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var lease = await store.ClaimSyncAsync(workerId, Lease, cancellationToken);
        if (lease is null) return false;
        await ProcessAsync(lease, cancellationToken);
        return true;
    }

    private async Task ProcessAsync(BrandKnowledgeSyncLease lease, CancellationToken cancellationToken)
    {
        try
        {
            Guid? documentId = null;
            string action;
            if (lease.Deleted)
            {
                action = "disable";
                // 公开的 DisableDocumentAsync 拒绝条目投影资料（409 KNOWLEDGE_DOCUMENT_MANAGED_BY_ITEM），Worker 作为唯一写入方走专用入口；
                // 不存在的资料返回 null，已停用的资料重复停用无副作用。
                if (lease.KnowledgeDocumentId is Guid existing)
                    await knowledge.DisableKnowledgeItemDocumentAsync(lease.TenantId, existing, cancellationToken);
            }
            else if (lease.KnowledgeDocumentId is not null && lease.SyncedVersion >= lease.ContentVersion)
            {
                action = "noop";
            }
            else
            {
                action = lease.KnowledgeDocumentId is null ? "create" : "version";
                documentId = await UploadAsync(lease, cancellationToken);
            }
            var outcome = await store.CompleteSyncAsync(lease, documentId, cancellationToken);
            logger.LogInformation(
                "Knowledge item {ItemId} sync {Action} finished with {Outcome}. Tenant={TenantId} Dimension={Dimension} Version={Version} Document={DocumentId} Attempt={Attempt}",
                lease.ItemId, action, outcome, lease.TenantId, BrandKnowledgeRules.ToKey(lease.Dimension), lease.ContentVersion,
                documentId ?? lease.KnowledgeDocumentId, lease.AttemptCount);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (CollectorStoreException exception)
        {
            logger.LogWarning("Knowledge item {ItemId} sync failed with {Code}. Tenant={TenantId} Attempt={Attempt}",
                lease.ItemId, exception.Code, lease.TenantId, lease.AttemptCount);
            await TryFailAsync(lease, exception.Code, exception.Message,
                exception.StatusCode >= 500 || exception.StatusCode is 409 or 413 or 429, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning("Knowledge item {ItemId} sync failed: {Type}. Tenant={TenantId} Attempt={Attempt}",
                lease.ItemId, exception.GetType().Name, lease.TenantId, lease.AttemptCount);
            await TryFailAsync(lease, "KNOWLEDGE_ITEM_SYNC_FAILED", "知识库同步暂时失败，将自动重试。", true, cancellationToken);
        }
    }

    private async Task<Guid> UploadAsync(BrandKnowledgeSyncLease lease, CancellationToken cancellationToken)
    {
        var markdown = BrandKnowledgeRules.RenderMarkdown(lease.Dimension, lease.Title, lease.Content, lease.SourceUrl);
        try
        {
            var document = await knowledge.UploadKnowledgeItemDocumentAsync(lease.TenantId, lease.SiteId, lease.ItemId, lease.Dimension,
                lease.Title, markdown, lease.KnowledgeDocumentId, cancellationToken);
            return document.DocumentId;
        }
        catch (CollectorStoreException exception) when (lease.KnowledgeDocumentId is not null && exception.Code == "RESOURCE_NOT_FOUND")
        {
            // 已有投影文档被停用或不存在（例如 043 之前被旧文件资料接口停用）：新建一份文档接管，CompleteSyncAsync 会改写条目的文档标识。
            logger.LogWarning("Knowledge item {ItemId} projection document {DocumentId} is unavailable; creating a new document.",
                lease.ItemId, lease.KnowledgeDocumentId);
            var document = await knowledge.UploadKnowledgeItemDocumentAsync(lease.TenantId, lease.SiteId, lease.ItemId, lease.Dimension,
                lease.Title, markdown, null, cancellationToken);
            return document.DocumentId;
        }
    }

    private async Task TryFailAsync(BrandKnowledgeSyncLease lease, string code, string message, bool retryable, CancellationToken cancellationToken)
    {
        try { await store.FailSyncAsync(lease, code, message, retryable, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { logger.LogError(exception, "Failed to persist knowledge item sync failure for item {ItemId}", lease.ItemId); }
    }
}
