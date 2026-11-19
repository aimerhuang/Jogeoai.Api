using Jogeoai.Domain;

namespace Jogeoai.Application;

/// <summary>
/// App 采集渠道的周期 Run：读取、截止处理，以及提问的人工排除。
/// 周期 Run 由采集计划的时隙处理创建（每周一次，不经过本接口）；领取、续租、结果入库和计费不在这里。
/// </summary>
public interface IAppCollectionStore
{
    /// <summary>
    /// 分页读取一个 App 周期 Run 的检测。未采集的检测状态为 cancelled，错误码是未采集原因码。
    /// 不返回账号代号、采集节点或绑定信息。
    /// </summary>
    /// <param name="tenantId">租户标识。</param>
    /// <param name="runId">运行标识。</param>
    /// <param name="cursor">上页返回的不透明游标。</param>
    /// <param name="limit">每页数量，范围 1 到 100。</param>
    /// <param name="engine">可选的平台代码（App 目标的引擎）。</param>
    /// <param name="status">可选的检测状态。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<CursorPage<CloroVisibilityCheckView>> GetChecksAsync(Guid tenantId, Guid runId, string? cursor, int limit,
        string? engine, string? status, CancellationToken cancellationToken = default);

    /// <summary>
    /// 统计 App 周期 Run 里未采集（状态为 cancelled）的检测数，用于 Run 摘要的“采集已结束的样本数”。
    /// 只统计 Provider 为 app_collector 的检测；没有未采集检测的 Run 不在结果里。
    /// </summary>
    /// <param name="tenantId">租户标识。</param>
    /// <param name="runIds">运行标识；空列表不访问数据库。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyDictionary<Guid, int>> CountNotCollectedAsync(Guid tenantId, IReadOnlyList<Guid> runIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 截止处理：已过截止时点且未成功的 App 任务置为终态，对应的检测置为 cancelled 并写未采集原因码，同时进入分析队列并更新所属 Run 的状态。
    /// 租约尚未过期的任务留给下一次。只处理 App 渠道的任务。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本次置为未采集的检测数。</returns>
    Task<int> ExpireOverdueTasksAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 把一条提问排除出 App 渠道（运营操作）：从下一个尚未展开的周期起不再生成 App 任务，API 渠道不受影响。
    /// 同一条提问同时最多有一条生效的排除；已有时返回 409 APP_PROMPT_ALREADY_EXCLUDED。
    /// </summary>
    /// <param name="tenantId">租户标识。</param>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="promptId">提问标识。</param>
    /// <param name="reason">排除原因，1–500 个字符。</param>
    /// <param name="actorUserId">执行操作的用户标识，写入审计。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<AppPromptExclusionView> ExcludePromptAsync(Guid tenantId, Guid siteId, Guid promptId, string reason, Guid actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 撤销一条提问生效中的排除；记录保留，只写入撤销人和撤销时间。没有生效的排除时返回 404 APP_PROMPT_EXCLUSION_NOT_FOUND。
    /// </summary>
    /// <param name="tenantId">租户标识。</param>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="promptId">提问标识。</param>
    /// <param name="actorUserId">执行操作的用户标识，写入审计。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<AppPromptExclusionView> RevokePromptExclusionAsync(Guid tenantId, Guid siteId, Guid promptId, Guid actorUserId,
        CancellationToken cancellationToken = default);
}
