using Jogeoai.Domain;

namespace Jogeoai.Application;

/// <summary>
/// 网址识别任务（迁移 043 的 brand_knowledge_extract_jobs）的持久化边界：入队（惰性过期、与产品识别共用的每日频控、每品牌单任务）、
/// 查询、取消、Worker 租约与结果写入，以及保存或放弃时的状态检查。网页正文从不落库。
/// </summary>
public interface IKnowledgeExtractJobStore
{
    /// <summary>
    /// 发起网址识别：先把本品牌已过期的候选置为 discarded，再在锁住租户行后检查当日（UTC）识别任务数 + 产品识别任务数，
    /// 达到 50 返回 KNOWLEDGE_EXTRACT_RATE_LIMITED（429）；本品牌已有未结束任务返回 KNOWLEDGE_EXTRACT_IN_PROGRESS（409）。
    /// </summary>
    /// <param name="tenantId">租户。</param>
    /// <param name="siteId">品牌。</param>
    /// <param name="userId">发起人。</param>
    /// <param name="request">网址与限定维度。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="CollectorStoreException">KNOWLEDGE_ITEM_VALIDATION（400）、RESOURCE_NOT_FOUND（404）、KNOWLEDGE_EXTRACT_IN_PROGRESS（409）、KNOWLEDGE_EXTRACT_RATE_LIMITED（429）。</exception>
    Task<KnowledgeExtractJobView> EnqueueAsync(Guid tenantId, Guid siteId, Guid userId, KnowledgeExtractRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 读取品牌当前任务（先惰性过期）：进行中或候选待处理（pending、running、done 且未保存/放弃）的任务优先；
    /// 否则返回 24 小时内失败且未被放弃的最近一次任务，便于页面显示失败原因；都没有时返回 null。
    /// </summary>
    /// <param name="tenantId">租户。</param>
    /// <param name="siteId">品牌。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="CollectorStoreException">品牌不存在或不属于该租户时 RESOURCE_NOT_FOUND（404）。</exception>
    Task<KnowledgeExtractJobView?> GetCurrentAsync(Guid tenantId, Guid siteId, CancellationToken cancellationToken = default);

    /// <summary>读取任务（先惰性过期）；不存在或不属于该租户与品牌时返回 null。</summary>
    /// <param name="tenantId">租户。</param>
    /// <param name="siteId">品牌。</param>
    /// <param name="jobId">任务。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<KnowledgeExtractJobView?> GetAsync(Guid tenantId, Guid siteId, Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 取消 pending 或 running 的任务；running 任务由 Worker 在下一次心跳发现后放弃。已取消的任务重复取消直接返回；
    /// 其他状态返回 KNOWLEDGE_EXTRACT_STATE_CONFLICT（409）。
    /// </summary>
    /// <param name="tenantId">租户。</param>
    /// <param name="siteId">品牌。</param>
    /// <param name="jobId">任务。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="CollectorStoreException">KNOWLEDGE_EXTRACT_NOT_FOUND（404）、KNOWLEDGE_EXTRACT_STATE_CONFLICT（409）。</exception>
    Task<KnowledgeExtractJobView> CancelAsync(Guid tenantId, Guid siteId, Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 原子保存识别候选（保存接口唯一入口），在一个事务内：锁任务行（锁顺序固定为任务行 → 品牌行）→ 校验租户与品牌、
    /// 状态为 done 且未处理、未过期（过期时置为 discarded 并返回 410 KNOWLEDGE_EXTRACT_EXPIRED；其他状态 409 KNOWLEDGE_EXTRACT_NOT_READY）→
    /// 校验每个 CandidateId 都属于该任务且不重复（否则 400 KNOWLEDGE_ITEM_VALIDATION）→ 按 <see cref="BrandKnowledgeRules"/> 重新校验
    /// 用户编辑后的维度、标题与内容 → 与 <see cref="IBrandKnowledgeItemStore.CreateManyAsync"/> 相同的锁品牌行、50 条上限与重名检查后插入
    /// （来源 url 为任务网址，source_excerpt 为候选原文依据，extract_job_id 为本任务）→ 任务置为 saved 并写 resolved_at。
    /// 任一步失败整体回滚，任务保持 done。同一任务的并发保存只有一个成功，其余返回 409 KNOWLEDGE_EXTRACT_NOT_READY。
    /// </summary>
    /// <param name="tenantId">租户。</param>
    /// <param name="siteId">品牌。</param>
    /// <param name="jobId">任务。</param>
    /// <param name="userId">保存人，作为新条目的创建人。</param>
    /// <param name="request">用户勾选并可能编辑过的候选。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>新增的条目，顺序与提交一致。</returns>
    /// <exception cref="CollectorStoreException">KNOWLEDGE_ITEM_VALIDATION（400）、KNOWLEDGE_EXTRACT_NOT_FOUND（404）、RESOURCE_NOT_FOUND（404）、KNOWLEDGE_EXTRACT_NOT_READY（409）、KNOWLEDGE_ITEM_LIMIT（409）、KNOWLEDGE_ITEM_TITLE_CONFLICT（409）、KNOWLEDGE_EXTRACT_EXPIRED（410）。</exception>
    Task<IReadOnlyList<BrandKnowledgeItemView>> SaveCandidatesAsync(Guid tenantId, Guid siteId, Guid jobId, Guid userId,
        KnowledgeExtractSaveRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// 放弃识别结果：只接受 resolution = discarded（保存只走 <see cref="SaveCandidatesAsync"/>，其他取值抛出 <see cref="ArgumentOutOfRangeException"/>）。
    /// 接受 done 或 failed 的任务（已 discarded 或已取消时直接返回；pending/running/saved 返回 409 KNOWLEDGE_EXTRACT_NOT_READY）。
    /// </summary>
    /// <param name="tenantId">租户。</param>
    /// <param name="siteId">品牌。</param>
    /// <param name="jobId">任务。</param>
    /// <param name="resolution">固定为 discarded。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="CollectorStoreException">KNOWLEDGE_EXTRACT_NOT_FOUND（404）、KNOWLEDGE_EXTRACT_NOT_READY（409）。</exception>
    Task<KnowledgeExtractJobView> MarkResolvedAsync(Guid tenantId, Guid siteId, Guid jobId, string resolution,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 领取一条到期的 pending 任务或租约已过期的 running 任务并建立租约，尝试次数 + 1；中断次数已满的过期任务直接记为失败。
    /// 品牌已归档或已不存在的任务不领取，直接记为失败（RESOURCE_NOT_FOUND），避免一直停在待处理；这两种情况本次都返回 null。
    /// </summary>
    /// <param name="workerId">Worker 标识，作为租约持有人。</param>
    /// <param name="lease">租约时长。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<KnowledgeExtractJobLease?> ClaimAsync(string workerId, TimeSpan lease, CancellationToken cancellationToken = default);

    /// <summary>延长租约；任务已被取消时返回 <see cref="KnowledgeExtractHeartbeat.Cancelled"/>，被其他 Worker 接管或已结束时返回 <see cref="KnowledgeExtractHeartbeat.LeaseLost"/>。</summary>
    /// <param name="lease">领取时的租约。</param>
    /// <param name="extend">延长时长。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<KnowledgeExtractHeartbeat> HeartbeatAsync(KnowledgeExtractJobLease lease, TimeSpan extend, CancellationToken cancellationToken = default);

    /// <summary>记录当前阶段（fetch、model、finalize）；租约失效或任务已取消时返回 false。</summary>
    /// <param name="lease">领取时的租约。</param>
    /// <param name="step">阶段。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<bool> MarkStepAsync(KnowledgeExtractJobLease lease, string step, CancellationToken cancellationToken = default);

    /// <summary>
    /// 写入候选并置为 done，expires_at = completed_at + 24 小时；候选 JSON 超过 256 KiB 时从末尾丢弃候选并标记截断。
    /// 只写网页标题、hash 与字数，不写正文。租约失效或任务已取消时不写入并返回 false。
    /// </summary>
    /// <param name="lease">领取时的租约。</param>
    /// <param name="completion">识别结果。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<bool> CompleteAsync(KnowledgeExtractJobLease lease, KnowledgeExtractCompletion completion, CancellationToken cancellationToken = default);

    /// <summary>
    /// 记录失败：可重试且未满 3 次时回到 pending 并按 30 秒起的指数退避重试；否则置为 failed。租约失效或任务已取消时不写入并返回 false。
    /// </summary>
    /// <param name="lease">领取时的租约。</param>
    /// <param name="errorCode">失败原因码。</param>
    /// <param name="errorMessage">面向用户的失败说明，不得包含网页正文、凭据或原始响应。</param>
    /// <param name="retryable">是否可重试。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<bool> FailAsync(KnowledgeExtractJobLease lease, string errorCode, string errorMessage, bool retryable,
        CancellationToken cancellationToken = default);
}

/// <summary>网址识别任务租约：领取时的任务快照。</summary>
/// <param name="JobId">任务标识。</param>
/// <param name="TenantId">租户。</param>
/// <param name="SiteId">品牌。</param>
/// <param name="BrandName">品牌名称（写入模型提示）。</param>
/// <param name="RequestedBy">发起人。</param>
/// <param name="Url">规范化后的网址。</param>
/// <param name="Dimensions">限定维度；为空表示全部维度。</param>
/// <param name="AttemptCount">包含本次在内的尝试次数。</param>
/// <param name="LeaseOwner">租约持有人。</param>
public sealed record KnowledgeExtractJobLease(Guid JobId, Guid TenantId, Guid SiteId, string BrandName, Guid RequestedBy, string Url,
    IReadOnlyList<BrandKnowledgeDimension>? Dimensions, int AttemptCount, string LeaseOwner);

/// <summary>心跳结果。</summary>
public enum KnowledgeExtractHeartbeat
{
    /// <summary>租约已延长，继续执行。</summary>
    Alive,
    /// <summary>任务已被用户取消，Worker 应放弃本次执行。</summary>
    Cancelled,
    /// <summary>租约已失效（被其他 Worker 接管或任务已结束），Worker 应停止写入。</summary>
    LeaseLost
}

/// <summary>识别完成时写入的结果（不含网页正文）。</summary>
/// <param name="PageTitle">网页标题（超过 500 字截断）。</param>
/// <param name="PageHash">网页正文的 SHA-256（小写十六进制）。</param>
/// <param name="PageCharacters">网页正文字数。</param>
/// <param name="Truncated">网页正文或候选是否因长度被截断。</param>
/// <param name="Candidates">标准化并查重后的候选。</param>
/// <param name="ModelName">实际调用模型。</param>
/// <param name="Provider">Provider 标识。</param>
/// <param name="DurationMs">模型调用耗时。</param>
public sealed record KnowledgeExtractCompletion(string? PageTitle, string PageHash, int PageCharacters, bool Truncated,
    IReadOnlyList<KnowledgeExtractCandidate> Candidates, string ModelName, string Provider, long DurationMs);
