using Jogeoai.Domain;

namespace Jogeoai.Application;

/// <summary>情感判定链路的稳定错误；Worker 据此决定重试、释放或记失败。不包含模型原始响应或凭据。</summary>
public sealed class ReputationClassifierException(string code, string message, bool retryable, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>Provider 未配置或配置无效（401 / 403）：工作项退回 pending，不消耗重试次数。</summary>
    public const string ProviderConfigInvalid = "REPUTATION_PROVIDER_CONFIG_INVALID";
    /// <summary>网关暂时不可用、限流或超时：可重试。</summary>
    public const string Retryable = "REPUTATION_PROVIDER_RETRYABLE";
    /// <summary>网关拒绝请求：不可重试。</summary>
    public const string Rejected = "REPUTATION_PROVIDER_REJECTED";

    /// <summary>稳定错误码。</summary>
    public string Code { get; } = code;
    /// <summary>是否允许 Worker 重试。</summary>
    public bool IsRetryable { get; } = retryable;
}

/// <summary>口碑情感分类器：只对已提及本品牌的有效回答调用，结果状态只可能是正面、中性、负面或未知。</summary>
public interface IReputationSentimentClassifier
{
    /// <summary>实际调用的模型名称。</summary>
    string ModelName { get; }

    /// <summary>判定回答对本品牌的整体态度；输出非法、低置信度、依据片段不在原文或认为未提及时返回 unknown + 原因码，而不是抛出异常。</summary>
    /// <param name="input">冻结的判定输入。</param>
    /// <param name="configuration">OpenAI 兼容网关配置（openai-compatible，回退 openai-api）。</param>
    /// <param name="cancellationToken">取消令牌（含 Worker 超时）。</param>
    /// <exception cref="ReputationClassifierException">网关不可用、配置无效或拒绝请求。</exception>
    Task<ReputationClassification> ClassifyAsync(ReputationClassificationInput input,
        ResearchProviderConfiguration configuration, CancellationToken cancellationToken = default);
}

/// <summary>读取情感分类器使用的网关配置；未配置时返回空。</summary>
public interface IReputationClassifierSettings
{
    /// <summary>读取 openai-compatible（回退 openai-api）配置；凭据为空视为未配置。</summary>
    Task<ResearchProviderConfiguration?> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>Worker 领取到的一条情感判定工作项。</summary>
/// <param name="ReleaseId">分析版本标识。</param>
/// <param name="CheckId">检测标识。</param>
/// <param name="TenantId">租户标识。</param>
/// <param name="SiteId">品牌工作区标识。</param>
/// <param name="LeaseVersion">本次租约版本，写入时校验。</param>
/// <param name="AttemptCount">包含本次在内的尝试次数。</param>
/// <param name="BrandSubjectId">分析版本冻结配置中的本品牌主体标识。</param>
/// <param name="BrandName">本品牌名称。</param>
/// <param name="BrandAliases">本品牌别名（不含域名）。</param>
/// <param name="ExcludedTerms">不能作为印象词的名称（品牌域名、竞品名称与别名）。</param>
/// <param name="Answer">完整回答原文。</param>
/// <param name="BrandMentioned">现有提及分析是否在该版本中命中本品牌。</param>
/// <param name="MentionSnippets">本品牌命中片段（最多 5 条）。</param>
public sealed record ReputationWorkLease(Guid ReleaseId, Guid CheckId, Guid TenantId, Guid SiteId, int LeaseVersion,
    int AttemptCount, Guid BrandSubjectId, string BrandName, IReadOnlyList<string> BrandAliases,
    IReadOnlyList<string> ExcludedTerms, string Answer, bool BrandMentioned, IReadOnlyList<string> MentionSnippets);

/// <summary>情感事实与工作项的持久化边界；所有方法在 038 未执行时不得影响采集、提及分析或旧分析查询。</summary>
public interface IReputationStore
{
    /// <summary>038 的四张表是否都已存在。</summary>
    Task<bool> SchemaReadyAsync(CancellationToken cancellationToken = default);

    /// <summary>把已发布或构建中分析版本里、在 <paramref name="completedSince"/> 之后完成提及分析的有效回答登记为待判定（INSERT IGNORE，幂等）。</summary>
    /// <param name="completedSince">提及分析完成时间下限（UTC）；更早的历史回答需通过站点级补录任务登记。</param>
    /// <param name="limit">本次最多登记条数。</param>
    /// <param name="maxAttempts">最大尝试次数；超过且租约已过期的处理中工作项在此记为失败。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<int> EnqueueRecentAsync(DateTimeOffset completedSince, int limit, int maxAttempts,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 把尝试次数已达上限、不会再被领取的工作项记为 failed 终态（租约过期的处理中工作项与上限调低后遗留的 retry）；
    /// 不自动登记时也必须执行，否则 Worker 在最后一次尝试中退出的工作项会永久停在待判定。
    /// </summary>
    /// <param name="maxAttempts">最大尝试次数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<int> ExpireExhaustedAsync(int maxAttempts, CancellationToken cancellationToken = default);

    /// <summary>领取一条工作项；<paramref name="includeMentioned"/> 为 false 时只领取未提及本品牌的回答（Provider 未配置时仍可短路完成）。</summary>
    Task<ReputationWorkLease?> ClaimAsync(string workerId, bool includeMentioned, TimeSpan lease, int maxAttempts,
        CancellationToken cancellationToken = default);

    /// <summary>若其他分析版本中存在相同输入摘要与口径版本的判定，则复制到当前工作项并完成；返回是否复用成功。</summary>
    Task<bool> TryReuseAsync(ReputationWorkLease lease, string workerId, string inputHash,
        CancellationToken cancellationToken = default);

    /// <summary>在一个事务内写入判定、印象词并完成工作项；租约已被他人接管时返回 false 且不写入。</summary>
    Task<bool> CompleteAsync(ReputationWorkLease lease, string workerId, ReputationClassification classification,
        string inputHash, CancellationToken cancellationToken = default);

    /// <summary>把工作项退回 pending 且不计本次尝试（Provider 未配置或配置无效）。</summary>
    Task ReleaseAsync(ReputationWorkLease lease, string workerId, string errorCode, CancellationToken cancellationToken = default);

    /// <summary>记录一次失败：<paramref name="terminal"/> 为 true 时进入 failed 终态，否则进入 retry。</summary>
    Task FailAsync(ReputationWorkLease lease, string workerId, string errorCode, bool terminal,
        CancellationToken cancellationToken = default);

    /// <summary>创建站点级补录任务：把当前已发布版本中范围内的有效回答登记为待判定；同幂等键同请求返回原任务。</summary>
    Task<ReputationJobView> CreateJobAsync(Guid tenantId, Guid siteId, string idempotencyKey,
        ReputationJobRequest request, CancellationToken cancellationToken = default);

    /// <summary>读取补录任务的实时进度；跨租户或不存在时返回空。</summary>
    Task<ReputationJobView?> GetJobAsync(Guid tenantId, Guid siteId, Guid jobId,
        CancellationToken cancellationToken = default);
}
