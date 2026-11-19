using Jogeoai.Domain;

namespace Jogeoai.Application;

/// <summary>文章、版本、发布链接和生成任务的持久化边界。</summary>
public interface IContentStore
{
    /// <summary>分页读取当前品牌文章，并返回同一筛选下的总数、有效链接数和各阶段计数。</summary>
    Task<ArticleListPage> ListArticlesAsync(Guid tenantId, Guid siteId, ArticleListFilter filter,
        CancellationToken cancellationToken = default);

    /// <summary>读取文章详情。</summary>
    Task<ArticleDetailView?> GetArticleAsync(Guid tenantId, Guid siteId, Guid articleId,
        CancellationToken cancellationToken = default);

    /// <summary>创建手工编辑版本。</summary>
    Task<ArticleDetailView> EditArticleAsync(Guid tenantId, Guid siteId, Guid articleId, Guid userId,
        ArticleEditRequest request, CancellationToken cancellationToken = default);

    /// <summary>归档或恢复文章。</summary>
    Task<ArticleView?> SetArchivedAsync(Guid tenantId, Guid siteId, Guid articleId, Guid userId,
        bool archived, CancellationToken cancellationToken = default);

    /// <summary>启用、暂停或停用文章监控。</summary>
    Task<ArticleView?> SetMonitorStatusAsync(Guid tenantId, Guid siteId, Guid articleId, Guid userId,
        ArticleMonitorStatus status, CancellationToken cancellationToken = default);

    /// <summary>登记一个已发布 URL。</summary>
    Task<ArticlePublicationLinkView> AddPublicationLinkAsync(Guid tenantId, Guid siteId, Guid articleId,
        Guid userId, ArticlePublicationLinkWrite request, CancellationToken cancellationToken = default);

    /// <summary>停用一个发布 URL。</summary>
    Task<ArticlePublicationLinkView?> DisablePublicationLinkAsync(Guid tenantId, Guid siteId, Guid articleId,
        Guid linkId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 把一个 URL 标记为本品牌自有：已有有效发布链接时不变；只有已停用链接时恢复为有效（文章已归档则恢复文章）；
    /// 都没有时新建一篇导入文章与有效发布链接。不抓取网页、不消耗积分。
    /// </summary>
    Task<OwnedUrlMarkResult> MarkOwnedUrlAsync(Guid tenantId, Guid siteId, Guid userId, string url, string? title,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 移除 URL 的自有标记：停用本品牌下该 URL 摘要的全部有效发布链接；导入文章因此不再有有效链接时归档。
    /// 没有有效链接时幂等返回未变化。
    /// </summary>
    Task<OwnedUrlMarkResult> UnmarkOwnedUrlAsync(Guid tenantId, Guid siteId, Guid userId, string urlHash,
        CancellationToken cancellationToken = default);

    /// <summary>生成文章选项目录。</summary>
    Task<ArticleGenerationOptionsView> GetGenerationOptionsAsync(Guid tenantId, Guid siteId,
        CancellationToken cancellationToken = default);

    /// <summary>保存素材预览。</summary>
    Task<ArticleMaterialPreviewView> SaveMaterialPreviewAsync(Guid tenantId, Guid siteId, Guid userId,
        ArticleMaterialPreviewView preview, CancellationToken cancellationToken = default);

    /// <summary>读取未过期素材预览。</summary>
    Task<ArticleMaterialPreviewView?> GetMaterialPreviewAsync(Guid tenantId, Guid siteId, Guid previewId,
        CancellationToken cancellationToken = default);

    /// <summary>保存生成预览。</summary>
    Task<ArticleGenerationPreviewView> SaveGenerationPreviewAsync(Guid tenantId, Guid siteId, Guid userId,
        ArticleGenerationPreviewRequest request, ArticleGenerationPreviewView preview,
        CancellationToken cancellationToken = default);

    /// <summary>读取未过期生成预览。</summary>
    Task<ArticleGenerationPreviewRecord?> GetGenerationPreviewAsync(Guid tenantId, Guid siteId, Guid previewId,
        CancellationToken cancellationToken = default);

    /// <summary>按服务端费用和已冻结快照提交生成批次。</summary>
    Task<ArticleGenerationAccepted> SubmitGenerationAsync(Guid tenantId, Guid siteId, Guid userId,
        ArticleGenerationSubmitRequest request, string idempotencyKey,
        CancellationToken cancellationToken = default);

    /// <summary>读取生成批次。</summary>
    Task<ArticleGenerationBatchView?> GetGenerationBatchAsync(Guid tenantId, Guid siteId, Guid batchId,
        CancellationToken cancellationToken = default);

    /// <summary>取消尚未开始的生成任务。</summary>
    Task<ArticleGenerationBatchView?> CancelGenerationAsync(Guid tenantId, Guid siteId, Guid userId, Guid batchId,
        CancellationToken cancellationToken = default);

    /// <summary>预览并导入已发布链接。</summary>
    Task<ArticleImportPreviewView> PreviewImportAsync(Guid tenantId, Guid siteId, Guid userId,
        IReadOnlyList<string> urls, CancellationToken cancellationToken = default);

    /// <summary>确认导入预览。</summary>
    Task<IReadOnlyList<ArticleView>> ConfirmImportAsync(Guid tenantId, Guid siteId, Guid userId,
        ArticleImportConfirmRequest request, string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>领取一项文章生成任务。</summary>
    Task<ArticleGenerationWorkItem?> ClaimGenerationTaskAsync(string workerId, TimeSpan lease,
        CancellationToken cancellationToken = default);

    /// <summary>延长当前文章生成任务的租约；租约持有者不匹配时返回 false。</summary>
    Task<bool> HeartbeatGenerationTaskAsync(ArticleGenerationWorkItem workItem, TimeSpan lease,
        CancellationToken cancellationToken = default);

    /// <summary>保存生成结果并捕获积分。</summary>
    Task CompleteGenerationTaskAsync(ArticleGenerationWorkItem workItem, ArticleGenerationResult result,
        CancellationToken cancellationToken = default);

    /// <summary>记录生成失败并释放或重试积分。</summary>
    Task FailGenerationTaskAsync(ArticleGenerationWorkItem workItem, string errorCode, string message,
        bool retryable, CancellationToken cancellationToken = default);
}

/// <summary>素材目录和 RAG 上下文预览服务。</summary>
public interface IArticleMaterialSelectionService
{
    /// <summary>列出文章生成可见素材目录。</summary>
    Task<ArticleMaterialCatalogView> ListCatalogAsync(Guid tenantId, Guid siteId,
        CancellationToken cancellationToken = default);

    /// <summary>检索并冻结文章参考素材。</summary>
    Task<ArticleMaterialPreviewView> PreviewAsync(Guid tenantId, Guid siteId, Guid userId,
        ArticleMaterialPreviewRequest request, CancellationToken cancellationToken = default);
}

/// <summary>文章生成选项目录。</summary>
/// <param name="Targets">接口字段。</param>
/// <param name="Prompts">接口字段。</param>
/// <param name="ArticleTags">接口字段。</param>
/// <param name="Languages">接口字段。</param>
/// <param name="Strategies">接口字段。</param>
/// <param name="LengthProfiles">接口字段。</param>
/// <param name="Styles">接口字段。</param>
/// <param name="ProviderConfigured">接口字段。</param>
/// <param name="ProviderModel">接口字段。</param>
/// <param name="ProviderLastCheckedAt">接口字段。</param>
/// <param name="Credits">接口字段。</param>
/// <param name="Knowledge">接口字段。</param>
/// <param name="Products">当前品牌已入库的产品信息，供「指定产品」下拉使用；归档产品不出现。</param>
public sealed record ArticleGenerationOptionsView(IReadOnlyList<CollectionTargetView> Targets,
    IReadOnlyList<PromptView> Prompts, IReadOnlyList<TagView> ArticleTags,
    IReadOnlyList<string> Languages, IReadOnlyList<string> Strategies,
    IReadOnlyList<string> LengthProfiles, IReadOnlyList<string> Styles,
    bool ProviderConfigured, string? ProviderModel,
    DateTimeOffset? ProviderLastCheckedAt, CreditAccountView Credits, ArticleMaterialCatalogView Knowledge,
    IReadOnlyList<KnowledgeProductOption>? Products = null);

/// <summary>知识素材目录。</summary>
/// <param name="Folders">接口字段。</param>
/// <param name="Documents">接口字段。</param>
/// <param name="Tags">接口字段。</param>
/// <param name="DocumentTypes">接口字段。</param>
/// <param name="ProductKeys">接口字段。</param>
/// <param name="Languages">接口字段。</param>
/// <param name="SupportedScopes">接口字段。</param>
/// <param name="MaxDocuments">接口字段。</param>
/// <param name="MaxChunks">接口字段。</param>
/// <param name="MaxCharacters">接口字段。</param>
public sealed record ArticleMaterialCatalogView(IReadOnlyList<KnowledgeFolderView> Folders,
    IReadOnlyList<KnowledgeDocumentView> Documents, IReadOnlyList<string> Tags,
    IReadOnlyList<string> DocumentTypes, IReadOnlyList<string> ProductKeys,
    IReadOnlyList<string> Languages, IReadOnlyList<string> SupportedScopes, int MaxDocuments,
    int MaxChunks, int MaxCharacters);

/// <summary>生成预览及提交时的冻结请求。</summary>
/// <param name="Preview">接口字段。</param>
/// <param name="Request">接口字段。</param>
/// <param name="ExpiresAt">接口字段。</param>
/// <param name="RequestHash">接口字段。</param>
/// <param name="TenantId">接口字段。</param>
/// <param name="SiteId">接口字段。</param>
/// <param name="UserId">接口字段。</param>
/// <param name="PromptIds">接口字段。</param>
/// <param name="TargetIds">接口字段。</param>
/// <param name="MaterialPreviewId">接口字段。</param>
/// <param name="ContextId">接口字段。</param>
/// <param name="ContextSnapshotHash">接口字段。</param>
/// <param name="KnowledgeSelectionSnapshot">接口字段。</param>
/// <param name="ReferenceSnapshot">接口字段。</param>
public sealed record ArticleGenerationPreviewRecord(ArticleGenerationPreviewView Preview,
    ArticleGenerationPreviewRequest Request, DateTimeOffset ExpiresAt, string RequestHash,
    Guid TenantId, Guid SiteId, Guid UserId, IReadOnlyList<Guid> PromptIds,
    IReadOnlyList<Guid> TargetIds, Guid? MaterialPreviewId, Guid? ContextId,
    string? ContextSnapshotHash, string? KnowledgeSelectionSnapshot, string? ReferenceSnapshot);
