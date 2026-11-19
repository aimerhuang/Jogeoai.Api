namespace Jogeoai.Domain;

/// <summary>一个 App 周期的采集统计（GET /sites/{siteId}/app-collection/status）。</summary>
/// <param name="StartsAt">周期开始（UTC）：该周期起始日计划时区 09:11 的时隙。</param>
/// <param name="EndsAt">周期截止（UTC）：开始后 7 天，即下一个周期的开始；到点仍未成功的检测记为未采集。</param>
/// <param name="RunCreated">该周期是否已为这个目标展开了周期 Run。为 false 时下面的检测数都是 0，被排除的提问是按当前的提问在读取时现算的。</param>
/// <param name="SkipCode">该周期的时隙被跳过时的原因码（例如 APP_CHANNEL_NOT_OPEN、SCHEDULE_DISABLED、MONITORING_SELECTION_EMPTY、
/// APP_PLATFORM_UNAVAILABLE）；时隙未跳过或尚未生成时为空。</param>
/// <param name="Planned">计划数：该周期为这个目标生成的检测数。</param>
/// <param name="Collected">已采集数：成功的检测数。</param>
/// <param name="InProgress">进行中数：等待领取或采集中的检测数。</param>
/// <param name="NotCollected">未采集数：截止或重试用尽后记为未采集（cancelled）的检测数。</param>
/// <param name="NotCollectedByReason">未采集的原因分布（<see cref="AppNotCollectedCodes"/>），按原因码排序；没有时为空列表。</param>
/// <param name="ExcludedPrompts">该周期里平台范围包含这个目标、但没有进入 App 渠道的提问，按原因码汇总，按原因码排序。已展开周期 Run 时取自展开时的记录，
/// 否则按当前的提问现算；没有时为空列表。</param>
public sealed record AppCollectionCycleStatus(DateTimeOffset StartsAt, DateTimeOffset EndsAt, bool RunCreated, string? SkipCode,
    int Planned, int Collected, int InProgress, int NotCollected, IReadOnlyList<AppReasonCount> NotCollectedByReason,
    IReadOnlyList<AppReasonCount> ExcludedPrompts);

/// <summary>一个已启用 App 目标的采集状态。</summary>
/// <param name="TargetId">站点目标标识。</param>
/// <param name="Engine">平台代码（App 目标的引擎）。</param>
/// <param name="DisplayName">平台显示名。</param>
/// <param name="TargetKey">目标键。</param>
/// <param name="Availability">平台的 App 入口当前是否可用：available 或 unavailable（不可用时不再展开任务）。</param>
/// <param name="FrequencyAnchor">周期锚点（第一个周期的起始日，计划时区的本地日期）。</param>
/// <param name="CurrentCycle">当前周期（包含现在的那个周期）；第一个周期还没有开始时为空。</param>
/// <param name="LastCompletedCycle">最近一个已结束的周期（当前周期的前一个）；还没有结束过的周期时为空。</param>
/// <param name="ExpectedFirstResultAt">预计首次结果时间（UTC）：该目标自启用以来还没有采到回答时，当前周期仍有进行中的检测则为当前周期的开始，
/// 否则为下一个周期的开始；已经采到过回答、或品牌的 App 计划不存在或已暂停时为空。</param>
public sealed record AppTargetCollectionStatus(Guid TargetId, string Engine, string DisplayName, string TargetKey, string Availability,
    DateOnly? FrequencyAnchor, AppCollectionCycleStatus? CurrentCycle, AppCollectionCycleStatus? LastCompletedCycle,
    DateTimeOffset? ExpectedFirstResultAt);

/// <summary>品牌的 App 渠道采集状态。团队未开放 App 渠道、或迁移 049 未部署时，<see cref="Targets"/> 为空列表。</summary>
/// <param name="PlanActive">品牌的 App 每周计划是否存在且启用（只启用 App 目标的品牌，监控已开启即为 true）。</param>
/// <param name="Targets">每个已启用的 App 目标，按目录顺序排列。</param>
public sealed record AppCollectionStatusView(bool PlanActive, IReadOnlyList<AppTargetCollectionStatus> Targets);
