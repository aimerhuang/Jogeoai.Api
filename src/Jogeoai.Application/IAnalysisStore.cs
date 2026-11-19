using Jogeoai.Domain;

namespace Jogeoai.Application;

/// <summary>异步提及分析、重算和 Check 证据的持久化边界。</summary>
public interface IAnalysisStore
{
    /// <summary>领取并处理一批已持久化的回答。</summary>
    Task<int> ProcessPendingAsync(string workerId, int limit = 10, CancellationToken cancellationToken = default);

    /// <summary>按幂等键创建新的待发布重算版本。</summary>
    Task<AnalysisJobView> CreateJobAsync(Guid tenantId, Guid siteId, string idempotencyKey,
        AnalysisJobRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// 查找已发布结果的识别配置版本或识别器版本落后于当前值、且没有进行中重算的站点（跨租户，供 Worker 自动重算）。
    /// 尚未建立分析状态的站点不返回。
    /// </summary>
    Task<IReadOnlyList<AnalysisRebuildCandidate>> FindSitesNeedingRebuildAsync(int limit,
        CancellationToken cancellationToken = default);

    /// <summary>读取租户内重算任务的真实进度。</summary>
    Task<AnalysisJobView?> GetJobAsync(Guid tenantId, Guid siteId, Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>读取已发布版本中的一条 Check 提及与分析状态。</summary>
    Task<CheckAnalysisView?> GetCheckAnalysisAsync(Guid tenantId, Guid checkId,
        Guid? releaseId = null, long? analysisWatermark = null, CancellationToken cancellationToken = default);
}
