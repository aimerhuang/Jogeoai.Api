using Jogeoai.Domain;

namespace Jogeoai.Application;

/// <summary>国内 Collector 控制面和可见性查询的应用服务契约。</summary>
public interface IVisibilityCollectorStore
{
    /// <summary>创建一次运行及其采集任务。</summary>
    Task<VisibilityRunCreated> CreateRunAsync(
        CreateVisibilityRunCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>旧 HTTP 运行缺少请求身份摘要时，按国内请求规范化摘要核对幂等键。</summary>
    Task<Guid?> GetExistingRunByKeyAsync(CreateVisibilityRunCommand expected, CancellationToken cancellationToken = default);

    /// <summary>领取一批可处理的 Collector 任务。</summary>
    Task<IReadOnlyList<LeasedCollectorTask>> LeaseAsync(
        CollectorLeaseCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>续租当前 Collector 任务。</summary>
    Task<DateTimeOffset> HeartbeatAsync(
        CollectorHeartbeatCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>接收 Collector 结果并执行幂等入库。</summary>
    Task<CollectorResultResponse> SubmitResultAsync(
        CollectorResultCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>查询运行概要。</summary>
    Task<VisibilityRunSummary?> GetRunAsync(
        Guid tenantId,
        Guid runId,
        CancellationToken cancellationToken = default);

    /// <summary>分页查询运行检查结果。</summary>
    Task<CursorPage<VisibilityCheckView>> GetChecksAsync(
        Guid tenantId,
        Guid runId,
        string? cursor,
        int limit,
        string? status,
        CancellationToken cancellationToken = default);

    /// <summary>回收已经过期的租约并安排重试。</summary>
    Task<int> RecoverExpiredTasksAsync(CancellationToken cancellationToken = default);
}
