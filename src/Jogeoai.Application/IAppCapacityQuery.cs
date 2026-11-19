using Jogeoai.Domain;

namespace Jogeoai.Application;

/// <summary>
/// App 各平台的容量视图（design 6.5）：限速上限、实测产能、周容量、周需求与预计排不到。只读，不加锁。
/// 计算规则在 <see cref="AppCapacityRules"/>；预估（阶段 8A）与运营视图、每日汇报（阶段 8B）读同一份数据。
/// </summary>
public interface IAppCapacityQuery
{
    /// <summary>
    /// 读取每个已知 App 平台（按 <see cref="AppChannelDefinition.Platforms"/> 的顺序）的当前容量视图。
    /// 周需求只统计 Collector:AppTenantIds 里的租户；名单为空时每个平台的周需求都是 0。迁移 049 未部署时返回 503 APP_CHANNEL_SCHEMA_NOT_MIGRATED。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyList<AppPlatformCapacity>> GetPlatformCapacitiesAsync(CancellationToken cancellationToken = default);
}

/// <summary>品牌的 App 渠道采集状态（GET /sites/{siteId}/app-collection/status）。只读。</summary>
public interface IAppCollectionStatusQuery
{
    /// <summary>
    /// 读取品牌每个已启用 App 目标的当前周期与最近一个已结束周期的统计。品牌不存在、已归档或不属于该租户时返回 404 RESOURCE_NOT_FOUND；
    /// 团队未开放 App 渠道、或迁移 049 未部署时返回空的目标列表（与目录、站点目标不返回 App 行一致）。
    /// </summary>
    /// <param name="tenantId">租户标识。</param>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<AppCollectionStatusView> GetAsync(Guid tenantId, Guid siteId, CancellationToken cancellationToken = default);
}
