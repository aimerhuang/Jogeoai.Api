using Jogeoai.Domain;

namespace Jogeoai.Application;

/// <summary>为登录后的已选问题启动并查询后台每日监控。</summary>
public interface IPromptMonitoringService
{
    /// <summary>幂等建立每日计划和首轮采集，保留已经暂停的计划。</summary>
    Task<PromptMonitoringView> EnsureAsync(Guid tenantId, Guid siteId, CancellationToken cancellationToken = default);

    /// <summary>读取当前计划与真实执行进度，不创建任务。</summary>
    Task<PromptMonitoringView> GetAsync(Guid tenantId, Guid siteId, CancellationToken cancellationToken = default);

    /// <summary>预估新增问题后每轮检测与积分（与每日监控预留同一矩阵），只读，不创建计划或任务。</summary>
    Task<PromptMonitoringEstimateView> EstimateAsync(Guid tenantId, Guid siteId, PromptMonitoringEstimateRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>预估品牌监控目标草稿（模型 × 回答模式 × 采集频率）的每周与下一轮检测、积分，只读，不保存目标。</summary>
    Task<MonitoringTargetsEstimateView> EstimateTargetsAsync(Guid tenantId, Guid siteId, MonitoringTargetsEstimateRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 预估团队默认监控配置草稿对所有启用且跟随品牌的影响：逐个品牌把草稿物化为该品牌的目标后复用品牌目标预估，再汇总每周与下一轮积分；
    /// 只读，不保存。
    /// </summary>
    Task<TeamMonitoringEstimateView> EstimateTeamMonitoringAsync(Guid tenantId, TeamMonitoringEstimateRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 新建提问后的立即采集：对仍为启用状态的新提问 × 它的全部有效单元格（不看采集频率）建立一轮按每日监控计费的运行，
    /// 当天已采集的组合不重复采集。品牌尚无监控计划时建立计划与首轮。不适用（没有提问、计划无法建立且没有可报告的原因）时返回 null；
    /// 业务原因（余额不足、服务不可用、计划暂停等）不抛异常，而是返回 skipped 结果。
    /// </summary>
    /// <param name="tenantId">租户标识。</param>
    /// <param name="siteId">品牌标识。</param>
    /// <param name="promptIds">本次实际新建的提问标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<InstantCollectionView?> CollectNewPromptsAsync(Guid tenantId, Guid siteId, IReadOnlyList<Guid> promptIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 提问平台范围变更后，对新增的有效模型单元格立即补采一次（该提问 × 新增单元格）；没有新增、或保存前后提问不是启用状态时返回 null。
    /// 业务原因不抛异常，而是返回 skipped 结果。
    /// </summary>
    /// <param name="tenantId">租户标识。</param>
    /// <param name="siteId">品牌标识。</param>
    /// <param name="before">保存前的提问（须与本次更新的预期版本一致）。</param>
    /// <param name="after">保存后的提问。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<InstantCollectionView?> CollectAddedTargetsAsync(Guid tenantId, Guid siteId, PromptView before, PromptView after,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 提问写入（新建、导入、批量创建、编辑、启停）后同步品牌的 App 计划：当前周期尚未覆盖的「提问 × App 平台」（例如新提问、
    /// 新启用的提问、平台范围新加入 App 平台）立即进入一个即时时隙，Worker 下一轮展开成采集任务，不等下一个每周时隙。
    /// 尽力而为：租户未开放 App 渠道、计划不存在或已暂停时不做任何事，失败不抛异常，下一次懒对账会重试。
    /// </summary>
    /// <param name="tenantId">租户标识。</param>
    /// <param name="siteId">品牌标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task SyncAppPlanAsync(Guid tenantId, Guid siteId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
