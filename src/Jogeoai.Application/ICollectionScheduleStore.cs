using Jogeoai.Domain;

namespace Jogeoai.Application;

/// <summary>计划管理、到期领取与崩溃恢复的应用层契约。</summary>
public interface ICollectionScheduleStore
{
    /// <summary>创建计划；未指定启用时默认为关闭。</summary>
    Task<CollectionScheduleView> CreateAsync(CreateCollectionScheduleCommand command, CancellationToken cancellationToken = default);

    /// <summary>按租户和站点稳定分页列出计划。</summary>
    Task<CursorPage<CollectionScheduleView>> ListAsync(Guid tenantId, Guid siteId,
        string? cursor, int limit, CancellationToken cancellationToken = default);

    /// <summary>修改计划；跨租户或不存在时返回 null。</summary>
    Task<CollectionScheduleView?> UpdateAsync(UpdateCollectionScheduleCommand command, CancellationToken cancellationToken = default);

    /// <summary>以数据库行锁领取到期计划，只记录最近时隙和遗漏数。</summary>
    Task<int> MaterializeDueSlotsAsync(DateTimeOffset nowUtc, int limit, CancellationToken cancellationToken = default);

    /// <summary>读取尚未关联 Run 的时隙，供 Worker 恢复。</summary>
    Task<IReadOnlyList<PendingCollectionSlot>> GetPendingSlotsAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>锁定一个时隙，按计划启用状态创建或恢复唯一 Run，并关联或跳过该时隙。</summary>
    Task<bool> ProcessSlotAsync(PendingCollectionSlot slot, ICloroVisibilityStore runs, CancellationToken cancellationToken = default);
}

/// <summary>创建计划的应用命令。</summary>
/// <param name="TenantId">租户标识。</param>
/// <param name="SiteId">站点标识。</param>
/// <param name="Name">计划名称。</param>
/// <param name="Cadence">daily 或 weekly。</param>
/// <param name="DayOfWeek">每周计划的星期日 0 至星期六 6。</param>
/// <param name="LocalTime">当地时刻，格式 HH:mm。</param>
/// <param name="TimeZone">IANA 时区标识。</param>
/// <param name="Selection">固定的 Prompt、Target 与样本数选择。</param>
/// <param name="Enabled">是否立即启用，默认关闭。</param>
public sealed record CreateCollectionScheduleCommand(
    Guid TenantId, Guid SiteId, string Name, string Cadence, int? DayOfWeek,
    string LocalTime, string TimeZone, CollectionScheduleSelection Selection,
    bool Enabled = false);

/// <summary>修改计划的应用命令；null 属性保持原值。</summary>
/// <param name="TenantId">租户标识。</param>
/// <param name="SiteId">站点标识。</param>
/// <param name="ScheduleId">计划标识。</param>
/// <param name="ExpectedVersion">客户端读取到的配置版本。</param>
/// <param name="Name">新的名称。</param>
/// <param name="Cadence">新的频率。</param>
/// <param name="DayOfWeek">新的每周星期；每日计划自动清空。</param>
/// <param name="LocalTime">新的当地时刻。</param>
/// <param name="TimeZone">新的 IANA 时区。</param>
/// <param name="Selection">新的采集选择。</param>
/// <param name="Enabled">新的启用状态。</param>
public sealed record UpdateCollectionScheduleCommand(
    Guid TenantId, Guid SiteId, Guid ScheduleId, int ExpectedVersion, string? Name = null,
    string? Cadence = null, int? DayOfWeek = null, string? LocalTime = null,
    string? TimeZone = null, CollectionScheduleSelection? Selection = null,
    bool? Enabled = null);
