namespace Jogeoai.Domain;

/// <summary>按原因码汇总的计数。</summary>
/// <param name="ReasonCode">原因码：提问未进入 App 渠道时是 <see cref="AppPromptRules"/> 的原因码（含被运营人员排除的 EXCLUDED_BY_OPERATOR，
/// 以及周期 Run 展开时提问已停用或归档的 PROMPT_UNAVAILABLE）；未采集时是 <see cref="AppNotCollectedCodes"/> 之一。</param>
/// <param name="Count">数量。</param>
public sealed record AppReasonCount(string ReasonCode, int Count);

/// <summary>预估中 API 渠道的部分（每日监控）。</summary>
/// <param name="Targets">已启用的 API 单元格数。</param>
/// <param name="WeeklyChecks">API 渠道每周检测次数（与没有 App 单元格时顶层的 weeklyChecks 相同）。</param>
/// <param name="WeeklyCredits">API 渠道每周积分。</param>
public sealed record ApiChannelEstimate(int Targets, decimal WeeklyChecks, decimal WeeklyCredits);

/// <summary>预估中一个 App 平台的容量（design 6.5）。是估算，不阻止保存。</summary>
/// <param name="Engine">平台代码（App 目标的引擎）。</param>
/// <param name="WeeklyCapacity">该平台的周容量（全部开放团队共用）；平台的 App 入口不可用时为 0。</param>
/// <param name="WeeklyDemand">该平台的周需求：全部开放团队里启用了该平台 App 目标的品牌的“通过检查的启用提问数”之和，本品牌按草稿计算。</param>
/// <param name="ExpectedUncollected">该平台预计排不到的检测数：max(0, 周需求 − 周容量)。</param>
public sealed record AppPlatformEstimate(string Engine, long WeeklyCapacity, long WeeklyDemand, long ExpectedUncollected);

/// <summary>预估中 App 渠道的部分（每周一次，按次计费）。</summary>
/// <param name="Targets">草稿中已启用的 App 单元格数。</param>
/// <param name="WeeklyChecks">App 渠道每周检测次数：只数通过检查、且按平台容量与排队顺序预计排得到的检测。</param>
/// <param name="WeeklyCredits">App 渠道每周积分：WeeklyChecks × 单价。</param>
/// <param name="ExcludedPrompts">不进入 App 渠道的启用提问（只数平台范围包含至少一个草稿 App 单元格的提问），按原因码汇总，按原因码排序；没有时为空列表。</param>
/// <param name="Platforms">草稿中每个已启用 App 单元格所在平台的容量，按目录顺序排列。</param>
/// <param name="FirstResultAt">预计首次结果时间（UTC）：草稿 App 单元格里预计排得到检测的那些，下一个 App 时隙（计划时区 09:11）中最早的一个；
/// 新启用的 App 目标从次日开始第一个周期。没有预计排得到的检测时为空。</param>
public sealed record AppChannelEstimate(int Targets, decimal WeeklyChecks, decimal WeeklyCredits, IReadOnlyList<AppReasonCount> ExcludedPrompts,
    IReadOnlyList<AppPlatformEstimate> Platforms, DateTimeOffset? FirstResultAt);

/// <summary>预估的渠道分项。只在草稿含已启用的 App 单元格时出现；没有 App 单元格时响应里没有这个字段，与引入 App 渠道之前完全相同。</summary>
/// <param name="Api">API 渠道（每日监控）的部分。</param>
/// <param name="App">App 渠道（每周一次）的部分。</param>
public sealed record MonitoringEstimateChannels(ApiChannelEstimate Api, AppChannelEstimate App);

/// <summary>
/// App 周期的日历（design 3.5 / 4.1 / 4.2）：App 目标从锚点日开始，每 7 天在计划时区的 09:11 开始一个周期，截止时点是下一个周期的开始。
/// 纯函数，不访问数据库。
/// </summary>
public static class AppCycleCalendar
{
    /// <summary>App 计划的本地时刻（09:11）。</summary>
    public static TimeOnly SlotLocalTime { get; } = CollectionScheduleCalendar.ParseLocalTime(PromptMonitoringIdentity.AppLocalTime);

    /// <summary>
    /// 某个本地日期的 App 时隙（09:11）对应的 UTC 时刻。夏令时的处理与 <see cref="AppCycleRules.DeadlineUtc"/> 相同：
    /// 落在不存在的当地时刻时顺延到第一个有效分钟；落在重复的当地时刻时取较早的 UTC 时刻。
    /// </summary>
    /// <param name="date">本地日期。</param>
    /// <param name="zone">计划时区。</param>
    public static DateTimeOffset SlotUtc(DateOnly date, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = date.ToDateTime(SlotLocalTime, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    /// <summary>某个周期起始日的时隙键（<c>yyyyMMddT0911</c>，与 schedule_slots.local_slot_key 相同）。</summary>
    /// <param name="cycleStart">周期起始的本地日期。</param>
    public static string SlotKey(DateOnly cycleStart) => CollectionScheduleCalendar.LocalSlotKey(cycleStart, SlotLocalTime);

    /// <summary>
    /// 包含 <paramref name="nowUtc"/> 的周期的起始日期：不晚于现在、不早于锚点、与锚点同一星期几的最近一个时隙。
    /// 锚点为空，或第一个周期还没有开始时返回空。
    /// </summary>
    /// <param name="anchor">目标的周期锚点（frequency_anchor）。</param>
    /// <param name="nowUtc">现在。</param>
    /// <param name="zone">计划时区。</param>
    public static DateOnly? CurrentCycleStart(DateOnly? anchor, DateTimeOffset nowUtc, TimeZoneInfo zone)
    {
        if (anchor is not { } start) return null;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, zone).DateTime);
        if (today < start) return null;
        var candidate = start.AddDays((today.DayNumber - start.DayNumber) / AppCycleRules.CycleDays * AppCycleRules.CycleDays);
        if (SlotUtc(candidate, zone) > nowUtc) candidate = candidate.AddDays(-AppCycleRules.CycleDays);
        return candidate < start ? null : candidate;
    }

    /// <summary>下一个（严格晚于现在的）周期的起始日期；锚点在未来时就是锚点。锚点为空时返回空。</summary>
    /// <param name="anchor">目标的周期锚点。</param>
    /// <param name="nowUtc">现在。</param>
    /// <param name="zone">计划时区。</param>
    public static DateOnly? NextCycleStart(DateOnly? anchor, DateTimeOffset nowUtc, TimeZoneInfo zone)
    {
        if (anchor is not { } start) return null;
        return CurrentCycleStart(start, nowUtc, zone) is { } current ? current.AddDays(AppCycleRules.CycleDays)
            : SlotUtc(start, zone) > nowUtc ? start : start.AddDays(AppCycleRules.CycleDays);
    }
}
