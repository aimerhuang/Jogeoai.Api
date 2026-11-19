using Jogeoai.Domain;

namespace Jogeoai.Application;

/// <summary>产品信息实体、识别任务与核验入库的持久化边界。</summary>
public interface IKnowledgeProductStore
{
    /// <summary>按品牌分页读取产品信息（最后更新时间倒序，游标绑定品牌与筛选）。</summary>
    Task<KnowledgeProductListPage> ListAsync(Guid tenantId, Guid siteId, KnowledgeProductListFilter filter,
        string? cursor = null, int limit = 20, CancellationToken cancellationToken = default);

    /// <summary>读取产品详情（含最近一次识别任务）；不属于该品牌时返回 null。</summary>
    Task<KnowledgeProductView?> GetAsync(Guid tenantId, Guid siteId, Guid productId,
        CancellationToken cancellationToken = default);

    /// <summary>读取品牌下已入库的产品，供文章生成「指定产品」使用。</summary>
    Task<IReadOnlyList<KnowledgeProductOption>> ListApprovedAsync(Guid tenantId, Guid siteId,
        CancellationToken cancellationToken = default);

    /// <summary>创建产品草稿；同品牌重名返回 409 PRODUCT_NAME_CONFLICT。</summary>
    Task<KnowledgeProductView> CreateAsync(Guid tenantId, Guid siteId, Guid userId, KnowledgeProductWrite request,
        CancellationToken cancellationToken = default);

    /// <summary>保存草稿；已入库产品的编辑直接生效并重新同步知识库。识别中时返回 409 PRODUCT_PARSE_IN_PROGRESS。</summary>
    Task<KnowledgeProductView?> UpdateDraftAsync(Guid tenantId, Guid siteId, Guid productId, Guid userId,
        KnowledgeProductWrite request, CancellationToken cancellationToken = default);

    /// <summary>创建识别任务：校验状态、每租户每日 50 次频控（与网址识别共用）与同产品活动任务唯一，并把产品置为识别中。</summary>
    Task<ProductParseJobView> EnqueueParseAsync(Guid tenantId, Guid siteId, Guid productId, Guid userId,
        ProductParseRequest request, CancellationToken cancellationToken = default);

    /// <summary>读取识别任务。</summary>
    Task<ProductParseJobView?> GetJobAsync(Guid tenantId, Guid siteId, Guid productId, Guid jobId,
        CancellationToken cancellationToken = default);

    /// <summary>取消尚未开始的识别任务。</summary>
    Task<ProductParseJobView?> CancelJobAsync(Guid tenantId, Guid siteId, Guid productId, Guid jobId,
        CancellationToken cancellationToken = default);

    /// <summary>当日（UTC）该租户已创建的识别任务数量：产品识别任务与网址识别任务合计（两者共用每日 50 次额度）。</summary>
    Task<int> CountParseJobsTodayAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>产品是否已由 043 迁移为品牌知识条目（brand_knowledge_items.legacy_product_id 命中，不论条目是否已删除）。</summary>
    /// <param name="tenantId">租户。</param>
    /// <param name="productId">产品标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<bool> IsMigratedAsync(Guid tenantId, Guid productId, CancellationToken cancellationToken = default);

    /// <summary>领取一个识别任务并建立租约。</summary>
    Task<ProductParseJobLease?> ClaimParseJobAsync(string workerId, TimeSpan lease,
        CancellationToken cancellationToken = default);

    /// <summary>续期租约；任务已被取消、过期或被他人领取时返回 false。</summary>
    Task<bool> HeartbeatAsync(ProductParseJobLease lease, TimeSpan extend, CancellationToken cancellationToken = default);

    /// <summary>记录当前阶段与输入摘要（不含资料原文）。</summary>
    Task MarkStepAsync(ProductParseJobLease lease, string step, string? inputSummaryJson, int imageCount,
        CancellationToken cancellationToken = default);

    /// <summary>写入识别结果为待核验草稿；人工填写的字段保留。租约失效时返回 409 PRODUCT_PARSE_STALE_LEASE。</summary>
    Task CompleteParseAsync(ProductParseJobLease lease, ProductParseResult result,
        CancellationToken cancellationToken = default);

    /// <summary>记录识别失败；可重试且未超过 3 次时按指数退避重新排队，否则产品置为识别失败。</summary>
    Task FailParseAsync(ProductParseJobLease lease, string errorCode, string errorMessage, bool retryable,
        CancellationToken cancellationToken = default);

    /// <summary>核验入库：校验必填、长度与已核对字段，写入 approved，并在事务外同步品牌知识库。</summary>
    Task<KnowledgeProductView?> ApproveAsync(Guid tenantId, Guid siteId, Guid productId, Guid userId,
        ProductApproveRequest request, CancellationToken cancellationToken = default);

    /// <summary>驳回待核验产品回到草稿。</summary>
    Task<KnowledgeProductView?> RejectAsync(Guid tenantId, Guid siteId, Guid productId, Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>归档产品并停用其知识库资料。</summary>
    Task<KnowledgeProductView?> ArchiveAsync(Guid tenantId, Guid siteId, Guid productId, Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>重试把已入库产品写入品牌知识库。</summary>
    Task<KnowledgeProductView?> SyncKnowledgeAsync(Guid tenantId, Guid siteId, Guid productId, Guid userId,
        CancellationToken cancellationToken = default);
}
