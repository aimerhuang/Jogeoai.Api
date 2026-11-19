using System.Text.Json.Serialization;

namespace Jogeoai.Domain;

/// <summary>定时采集的固定选择；编辑计划不会改变已经领取的时隙。</summary>
/// <param name="PromptIds">要采集的 Prompt 标识。</param>
/// <param name="Targets">要采集的目标；存在自定义平台范围时为各提问有效目标的并集。</param>
/// <param name="SampleCount">每个 Prompt 与目标的独立样本数。</param>
/// <param name="PromptTargets">只列出自定义平台范围的提问及其有效目标；为空表示全部提问 × 全部目标（全笛卡尔积）。只用于每日自动监控，手动计划忽略此字段。</param>
public sealed record CollectionScheduleSelection(
    IReadOnlyList<Guid> PromptIds,
    IReadOnlyList<CloroVisibilityTarget> Targets,
    int SampleCount = 1,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<CollectionPromptTargets>? PromptTargets = null);

/// <summary>自定义平台范围提问在一次选择中的有效目标。</summary>
/// <param name="PromptId">提问标识。</param>
/// <param name="TargetKeys">有效目标键（<see cref="CollectionIdentity.TargetKey(CloroVisibilityTarget)"/>），互不重复且按序数排序。</param>
public sealed record CollectionPromptTargets(Guid PromptId, IReadOnlyList<string> TargetKeys);

/// <summary>定时采集计划的公开视图。</summary>
/// <param name="Id">计划标识。</param>
/// <param name="SiteId">所属站点。</param>
/// <param name="Name">计划名称。</param>
/// <param name="Cadence">每日 daily 或每周 weekly。</param>
/// <param name="DayOfWeek">每周计划的星期，星期日为 0。</param>
/// <param name="LocalTime">计划的当地时刻，格式 HH:mm。</param>
/// <param name="TimeZone">IANA 时区标识。</param>
/// <param name="Selection">固定的采集选择。</param>
/// <param name="Enabled">是否启用。</param>
/// <param name="NextSlotAt">下次执行的 UTC 时间。</param>
/// <param name="MissedSlots">因停机而跳过的累计时隙数。</param>
/// <param name="Version">配置版本，PATCH 时用作预期版本。</param>
/// <param name="CreatedAt">创建的 UTC 时间。</param>
/// <param name="UpdatedAt">更新的 UTC 时间。</param>
public sealed record CollectionScheduleView(
    Guid Id, Guid SiteId, string Name, string Cadence, int? DayOfWeek,
    string LocalTime, string TimeZone, CollectionScheduleSelection Selection,
    bool Enabled, DateTimeOffset? NextSlotAt, int MissedSlots, int Version,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>已经领取、等待幂等创建 Run 的计划时隙。</summary>
/// <param name="Id">时隙记录标识。</param>
/// <param name="ScheduleId">计划标识。</param>
/// <param name="TenantId">租户标识。</param>
/// <param name="SiteId">站点标识。</param>
/// <param name="LocalSlotKey">原始计划当地日期及时刻组成的稳定键。</param>
/// <param name="Selection">领取时冻结的采集选择。</param>
/// <param name="MissedSlots">本轮跳过的较早时隙数。</param>
public sealed record PendingCollectionSlot(
    Guid Id, Guid ScheduleId, Guid TenantId, Guid SiteId, string LocalSlotKey,
    CollectionScheduleSelection Selection, int MissedSlots);
