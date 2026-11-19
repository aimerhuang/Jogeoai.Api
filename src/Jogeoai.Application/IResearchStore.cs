using Jogeoai.Domain;

namespace Jogeoai.Application;

/// <summary>品牌研究任务和全局 Provider 配置的持久化合同。</summary>
public interface IResearchStore
{
    /// <summary>创建一个品牌研究任务。</summary>
    Task<BrandResearchAccepted> EnqueueAsync(Guid tenantId, Guid siteId, Guid userId, StartBrandResearchRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>读取当前租户下的研究任务。</summary>
    Task<BrandResearchJobView?> GetAsync(Guid tenantId, Guid siteId, Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>取消等待中的研究任务。</summary>
    Task<bool> CancelAsync(Guid tenantId, Guid siteId, Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>领取一个待处理研究任务。</summary>
    Task<ResearchWorkItem?> LeaseAsync(string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default);

    /// <summary>续期当前租约；取消、过期或被重新领取时返回 false。</summary>
    Task<bool> RenewLeaseAsync(Guid jobId, string leaseOwner, TimeSpan leaseDuration, CancellationToken cancellationToken = default);

    /// <summary>保存当前阶段、来源和画像；只允许未过期的租约持有者写入。</summary>
    Task<bool> SaveProgressAsync(Guid jobId, string leaseOwner, ResearchContext context, CancellationToken cancellationToken = default);

    /// <summary>保存研究任务成功结果；仅当前租约持有者可以提交。</summary>
    Task CompleteAsync(Guid jobId, string leaseOwner, ResearchProviderResult result,
        CancellationToken cancellationToken = default);

    /// <summary>保存研究任务失败结果并安排重试或终止；仅当前租约持有者可以提交。</summary>
    Task FailAsync(Guid jobId, string leaseOwner, string code, string message, bool retryable,
        CancellationToken cancellationToken = default);

    /// <summary>读取全局 Provider 配置元数据。</summary>
    Task<IReadOnlyList<ResearchProviderSettingsView>> ListProviderSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>判断用户是否被数据库显式授予系统管理员权限。</summary>
    Task<bool> IsSystemAdministratorAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>读取一个 Provider 的完整配置供 Worker 使用。</summary>
    Task<ResearchProviderConfiguration?> GetProviderConfigurationAsync(string provider, CancellationToken cancellationToken = default);

    /// <summary>保存全局 Provider 配置。</summary>
    Task SaveProviderSettingsAsync(string provider, SaveResearchProviderSettingsRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Worker 领取到的研究任务。</summary>
/// <param name="JobId">任务标识。</param>
/// <param name="TenantId">租户标识。</param>
/// <param name="SiteId">品牌站点标识。</param>
/// <param name="UserId">发起用户标识。</param>
/// <param name="Request">规范化后的研究请求。</param>
/// <param name="AttemptCount">当前尝试次数。</param>
/// <param name="LeaseOwner">本次租约持有者，提交结果时用于防止过期 Worker 覆盖新租约。</param>
/// <param name="Research">持久化进度，供失败重试和换批复用。</param>
public sealed record ResearchWorkItem(
    Guid JobId,
    Guid TenantId,
    Guid SiteId,
    Guid UserId,
    StartBrandResearchRequest Request,
    int AttemptCount,
    string LeaseOwner,
    ResearchContext? Research = null);

/// <summary>供 Worker 读取和调用的 Provider 配置。</summary>
/// <param name="Provider">Provider 标识。</param>
/// <param name="Model">模型标识。</param>
/// <param name="ApiUrl">兼容接口地址。</param>
/// <param name="Credential">从数据库读取的调用凭据。</param>
/// <param name="WebSearchEnabled">是否允许联网搜索。</param>
/// <param name="TimeoutSeconds">调用超时秒数。</param>
/// <param name="Priority">配置优先级。</param>
public sealed record ResearchProviderConfiguration(
    string Provider,
    string Model,
    string ApiUrl,
    string Credential,
    bool WebSearchEnabled,
    int TimeoutSeconds,
    int Priority);
