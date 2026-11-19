using Jogeoai.Domain;

namespace Jogeoai.Application;

/// <summary>跨 Provider 的运行历史和 HTTP 幂等键查询。</summary>
public interface IVisibilityRunQueryStore
{
    /// <summary>在读取可变配置前按租户和幂等键查找原始运行。</summary>
    Task<VisibilityRunReplay?> FindReplayAsync(Guid tenantId, string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>按租户、站点和筛选条件稳定分页读取运行历史。</summary>
    Task<CursorPage<VisibilityRunSummary>> ListSiteRunsAsync(Guid tenantId, Guid siteId, string? status,
        string? provider, DateTimeOffset? from, DateTimeOffset? to, string? cursor, int limit,
        CancellationToken cancellationToken = default);
}
