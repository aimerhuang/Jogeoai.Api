using Jogeoai.Domain;

namespace Jogeoai.Application;

/// <summary>品牌知识条目的持久化边界：列表与计数、增删改（50 条上限、重名与乐观锁）、研究与写稿读取、检索投影同步租约。</summary>
public interface IBrandKnowledgeItemStore
{
    /// <summary>列出品牌未删除的条目：按维度固定顺序、同维度按创建时间正序；计数恒含 6 个维度且与筛选无关。</summary>
    /// <param name="tenantId">租户。</param>
    /// <param name="siteId">品牌。</param>
    /// <param name="dimension">维度筛选；为空表示全部维度。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="CollectorStoreException">品牌不存在或不属于该租户时 RESOURCE_NOT_FOUND（404）。</exception>
    Task<BrandKnowledgeItemList> ListAsync(Guid tenantId, Guid siteId, BrandKnowledgeDimension? dimension = null,
        CancellationToken cancellationToken = default);

    /// <summary>读取一条未删除的条目；不存在、已删除或不属于该品牌时返回 null。</summary>
    /// <param name="tenantId">租户。</param>
    /// <param name="siteId">品牌。</param>
    /// <param name="itemId">条目。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="CollectorStoreException">品牌已归档、不存在或不属于该租户时 RESOURCE_NOT_FOUND（404）。</exception>
    Task<BrandKnowledgeItemView?> GetAsync(Guid tenantId, Guid siteId, Guid itemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 在同一事务内批量新增条目（单条新增与网址识别批量保存共用）：锁品牌行后检查「已有 + 新增 ≤ 50」，
    /// 超出返回 KNOWLEDGE_ITEM_LIMIT（409）；同品牌同维度标题重复返回 KNOWLEDGE_ITEM_TITLE_CONFLICT（409）。任一条失败则全部不写入。
    /// </summary>
    /// <param name="tenantId">租户。</param>
    /// <param name="siteId">品牌。</param>
    /// <param name="userId">创建人。</param>
    /// <param name="items">待新增条目，按输入顺序写入与返回。</param>
    /// <param name="source">来源（手动填写或网址识别），整批共用。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>新增后的条目视图，顺序与输入一致。</returns>
    /// <exception cref="CollectorStoreException">KNOWLEDGE_ITEM_VALIDATION（400）、RESOURCE_NOT_FOUND（404）、KNOWLEDGE_ITEM_LIMIT（409）、KNOWLEDGE_ITEM_TITLE_CONFLICT（409）。</exception>
    Task<IReadOnlyList<BrandKnowledgeItemView>> CreateManyAsync(Guid tenantId, Guid siteId, Guid userId,
        IReadOnlyList<BrandKnowledgeItemCreate> items, BrandKnowledgeItemSource source, CancellationToken cancellationToken = default);

    /// <summary>
    /// 编辑条目：<see cref="BrandKnowledgeItemUpdate.ExpectedVersion"/> 与当前内容版本不一致时返回 KNOWLEDGE_ITEM_STALE（409）；
    /// 维度、标题、内容都未变化时不升版本、不触发同步；有变化时内容版本 + 1 并置为待同步。
    /// </summary>
    /// <param name="tenantId">租户。</param>
    /// <param name="siteId">品牌。</param>
    /// <param name="itemId">条目。</param>
    /// <param name="userId">修改人。</param>
    /// <param name="update">编辑请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="CollectorStoreException">KNOWLEDGE_ITEM_VALIDATION（400）、KNOWLEDGE_ITEM_NOT_FOUND（404）、品牌已归档或不存在时 RESOURCE_NOT_FOUND（404）、KNOWLEDGE_ITEM_STALE（409）、KNOWLEDGE_ITEM_TITLE_CONFLICT（409）。</exception>
    Task<BrandKnowledgeItemView> UpdateAsync(Guid tenantId, Guid siteId, Guid itemId, Guid userId, BrandKnowledgeItemUpdate update,
        CancellationToken cancellationToken = default);

    /// <summary>软删除条目并置为待同步（同步 Worker 随后停用其检索投影文档）；软删后同维度可再使用同名标题。</summary>
    /// <param name="tenantId">租户。</param>
    /// <param name="siteId">品牌。</param>
    /// <param name="itemId">条目。</param>
    /// <param name="userId">删除人。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="CollectorStoreException">KNOWLEDGE_ITEM_NOT_FOUND（404）；品牌已归档或不存在时 RESOURCE_NOT_FOUND（404）。</exception>
    Task DeleteAsync(Guid tenantId, Guid siteId, Guid itemId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 读取品牌研究使用的条目：按维度优先级（品牌介绍 → 核心卖点 → 目标人群 → 差异化优势 → 产品/服务 → 常见问题）、
    /// 同维度按更新时间倒序，最多 12 条，单条内容不超过 1200 字，内容合计不超过 8000 字。
    /// </summary>
    /// <param name="tenantId">租户。</param>
    /// <param name="siteId">品牌。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyList<BrandKnowledgeResearchItem>> ListForResearchAsync(Guid tenantId, Guid siteId,
        CancellationToken cancellationToken = default);

    /// <summary>列出可在文章生成中指定的产品：「产品/服务」维度、未删除且已有检索投影文档的条目。</summary>
    /// <param name="tenantId">租户。</param>
    /// <param name="siteId">品牌。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyList<KnowledgeProductOption>> ListProductOptionsAsync(Guid tenantId, Guid siteId,
        CancellationToken cancellationToken = default);

    /// <summary>按条目标识解析指定产品；不是「产品/服务」维度、已删除、尚无检索投影文档或不属于该品牌时返回 null。</summary>
    /// <param name="tenantId">租户。</param>
    /// <param name="siteId">品牌。</param>
    /// <param name="itemId">条目（产品键）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<ArticleProductRef?> GetProductRefAsync(Guid tenantId, Guid siteId, Guid itemId, CancellationToken cancellationToken = default);

    /// <summary>领取一条待同步（pending 或未超过重试次数的 failed、到达重试时间且无有效租约）的条目并建立租约，尝试次数 + 1。</summary>
    /// <param name="workerId">Worker 标识，作为租约持有人。</param>
    /// <param name="lease">租约时长。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<BrandKnowledgeSyncLease?> ClaimSyncAsync(string workerId, TimeSpan lease, CancellationToken cancellationToken = default);

    /// <summary>
    /// 记录同步完成：只有内容版本与删除状态仍与领取时一致才写 synced，否则保持 pending 立即进入下一轮；
    /// 本次上传得到的文档总会记录到条目（租约丢失时只在条目尚无文档时补记），避免重复建文档。
    /// </summary>
    /// <param name="lease">领取时的租约。</param>
    /// <param name="documentId">本次上传后的检索投影文档；停用分支或无需上传时为空。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<BrandKnowledgeSyncOutcome> CompleteSyncAsync(BrandKnowledgeSyncLease lease, Guid? documentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 记录同步失败：可重试且未满 5 次时置 failed 并按指数退避（1、2、4、8 分钟，最长 30 分钟）安排重试；
    /// 不可重试或已满 5 次时停止自动重试，直到条目再次被编辑或删除。领取后内容又被编辑时改回 pending 立即重试。
    /// </summary>
    /// <param name="lease">领取时的租约。</param>
    /// <param name="errorCode">失败原因码。</param>
    /// <param name="errorMessage">失败说明，不得包含条目正文、凭据或原始响应。</param>
    /// <param name="retryable">是否可重试。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task FailSyncAsync(BrandKnowledgeSyncLease lease, string errorCode, string errorMessage, bool retryable,
        CancellationToken cancellationToken = default);
}

/// <summary>新增条目时的单条输入。</summary>
/// <param name="Write">维度、标题与内容。</param>
/// <param name="SourceExcerpt">网址识别时的原文依据片段（不超过 1000 字，超出截断）；手动填写为空。</param>
public sealed record BrandKnowledgeItemCreate(BrandKnowledgeItemWrite Write, string? SourceExcerpt = null);

/// <summary>新增条目的来源，整批共用。</summary>
/// <param name="Kind">manual（手动填写）或 url（网址识别）；migrated 只由 043 迁移写入。</param>
/// <param name="Url">网址识别的规范化网址；url 来源必填。</param>
/// <param name="ExtractJobId">产生候选的网址识别任务。</param>
public sealed record BrandKnowledgeItemSource(string Kind, string? Url = null, Guid? ExtractJobId = null)
{
    /// <summary>手动填写。</summary>
    public static BrandKnowledgeItemSource Manual { get; } = new("manual");
}

/// <summary>品牌研究读取的一条条目（内容已按预算截断）。</summary>
/// <param name="ItemId">条目标识。</param>
/// <param name="Dimension">维度。</param>
/// <param name="Title">标题。</param>
/// <param name="Content">内容，单条不超过 1200 字。</param>
/// <param name="Truncated">内容是否因预算被截断。</param>
/// <param name="UpdatedAt">最后修改时间（UTC）。</param>
public sealed record BrandKnowledgeResearchItem(Guid ItemId, BrandKnowledgeDimension Dimension, string Title, string Content,
    bool Truncated, DateTimeOffset UpdatedAt);

/// <summary>检索投影同步租约：领取时的条目快照。</summary>
/// <param name="ItemId">条目标识。</param>
/// <param name="TenantId">租户。</param>
/// <param name="SiteId">品牌。</param>
/// <param name="Dimension">维度。</param>
/// <param name="Title">标题。</param>
/// <param name="Content">内容。</param>
/// <param name="SourceUrl">来源网址。</param>
/// <param name="ContentVersion">领取时的内容版本。</param>
/// <param name="SyncedVersion">领取时已同步的内容版本。</param>
/// <param name="KnowledgeDocumentId">已有的检索投影文档；首次同步时为空。</param>
/// <param name="Deleted">领取时条目是否已软删除。</param>
/// <param name="AttemptCount">包含本次在内的尝试次数。</param>
/// <param name="LeaseOwner">租约持有人。</param>
public sealed record BrandKnowledgeSyncLease(Guid ItemId, Guid TenantId, Guid SiteId, BrandKnowledgeDimension Dimension,
    string Title, string Content, string? SourceUrl, int ContentVersion, int SyncedVersion, Guid? KnowledgeDocumentId,
    bool Deleted, int AttemptCount, string LeaseOwner);

/// <summary>同步完成的结果。</summary>
public enum BrandKnowledgeSyncOutcome
{
    /// <summary>已写入 synced。</summary>
    Synced,
    /// <summary>同步期间内容或删除状态又有变化，保持 pending 等待下一轮。</summary>
    StillPending,
    /// <summary>租约已失效（被其他 Worker 接管），状态未改写。</summary>
    LeaseLost
}
