using Jogeoai.Domain;

namespace Jogeoai.Application;

/// <summary>采集器账号池控制面：预检、失败上报、账号事件与运维操作（依赖迁移 035）。</summary>
public interface ICollectorSessionPoolStore
{
    /// <summary>账号模式 Collector 访问平台页面前的只读预检；唯一的写入是 last_seen_at。</summary>
    Task<CollectorEligibilityResult> CheckEligibilityAsync(CollectorEligibilityCommand command, CancellationToken cancellationToken = default);

    /// <summary>应用一次分类失败：账号健康、立即释放租约并退避重排、失败尝试记录和平台熔断。</summary>
    Task<CollectorFailureResult> ReportFailureAsync(CollectorFailureCommand command, CancellationToken cancellationToken = default);

    /// <summary>记录非失败账号事件；verified 把账号恢复为 active。</summary>
    Task<CollectorSessionEventResult> RecordSessionEventAsync(CollectorSessionEventCommand command, CancellationToken cancellationToken = default);

    /// <summary>登记一个授权账号并返回其标识。</summary>
    Task<Guid> RegisterProfileAsync(RegisterCollectorSessionProfileCommand command, CancellationToken cancellationToken = default);

    /// <summary>运维启用或停用账号。</summary>
    Task SetProfileStatusAsync(Guid tenantId, Guid sessionProfileId, CollectorSessionProfileStatus status, Guid? operatorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>设置平台闸门参数；未提供的字段保持原值。</summary>
    Task ConfigurePlatformAsync(ConfigureCollectorPlatformDispatchCommand command, CancellationToken cancellationToken = default);

    /// <summary>解除平台熔断；平台未暂停时返回 false。</summary>
    Task<bool> ResumePlatformAsync(Guid tenantId, string region, string platformCode, Guid? operatorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>按账号列出运维指标。</summary>
    Task<IReadOnlyList<CollectorSessionProfileMetrics>> ListProfileMetricsAsync(Guid tenantId, string? platformCode, string? region,
        CancellationToken cancellationToken = default);

    /// <summary>列出平台闸门状态。</summary>
    Task<IReadOnlyList<CollectorPlatformDispatchStateView>> ListPlatformStatesAsync(Guid tenantId, CancellationToken cancellationToken = default);
}
