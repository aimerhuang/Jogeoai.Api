using System.Globalization;
using System.Text;

namespace Jogeoai.Domain;

/// <summary>失联的 App 采集节点：状态为 active，超过 15 分钟没有出现（或登记后从未出现）。</summary>
/// <param name="NodeId">节点标识。</param>
/// <param name="LastActivityAt">节点最近一次出现的时间（它的绑定上报，以及它任一绑定的领取、失败上报、结果提交中最晚的一次）；从未出现时为空。</param>
/// <param name="OfflineSeconds">失联了多少秒：从最近一次出现（从未出现时从登记时间）算到数据截止时刻。</param>
public sealed record AppOfflineNodeAlert(string NodeId, DateTimeOffset? LastActivityAt, long OfflineSeconds);

/// <summary>停止的 App 绑定。只含节点、设备、平台和账号代号，不含任何账号凭证。</summary>
/// <param name="BindingId">绑定标识。</param>
/// <param name="NodeId">所属节点。</param>
/// <param name="DeviceId">设备标识（AVD 名称）。</param>
/// <param name="PlatformCode">平台代码。</param>
/// <param name="AccountLabel">账号代号。</param>
/// <param name="StopReason">停止原因。</param>
/// <param name="StoppedAt">进入停止状态的时间。</param>
/// <param name="StoppedSeconds">停止了多少秒（算到数据截止时刻）。</param>
public sealed record AppStoppedBindingAlert(Guid BindingId, string NodeId, string DeviceId, string PlatformCode, string AccountLabel,
    AppBindingStopReason StopReason, DateTimeOffset StoppedAt, long StoppedSeconds);

/// <summary>被暂停或 App 入口被判定为不可用的平台。入口种子即为停用、原因是 APP_CONTENT_UNREADABLE 的平台（文心一言）不在这里。</summary>
/// <param name="PlatformCode">平台代码。</param>
/// <param name="Paused">平台是否被暂停。</param>
/// <param name="PauseReason">暂停原因：operator_paused 或 verification_breaker；未暂停时为空。</param>
/// <param name="PausedAt">暂停的时间；未暂停时为空。</param>
/// <param name="Unavailable">平台的 App 入口是否不可用。</param>
/// <param name="UnavailableReason">入口不可用的原因码；可用时为空。</param>
public sealed record AppPlatformAlert(string PlatformCode, bool Paused, string? PauseReason, DateTimeOffset? PausedAt, bool Unavailable,
    string? UnavailableReason);

/// <summary>过去 24 小时内被判定为未采集的 App 任务数（一个平台、一个原因一行）。</summary>
/// <param name="PlatformCode">平台代码。</param>
/// <param name="ReasonCode">未采集原因码（APP_NOT_COLLECTED_*）。</param>
/// <param name="Count">任务数。</param>
public sealed record AppNotCollectedCount(string PlatformCode, string ReasonCode, long Count);

/// <summary>一个 App 平台过去 24 小时的领取、成功与失败次数。</summary>
/// <param name="PlatformCode">平台代码。</param>
/// <param name="Leases">领取到任务的次数。</param>
/// <param name="Successes">结果被接受的次数。</param>
/// <param name="Failures">采集器带着任务上报的分类失败次数（不带任务的 probe 失败不写领取记录，不计在这里）。</param>
/// <param name="LeaseExpirations">租约过期、既没有结果也没有失败上报、在之后被记一次失败的次数。</param>
public sealed record AppPlatformActivity(string PlatformCode, long Leases, long Successes, long Failures, long LeaseExpirations);

/// <summary>一个 App 平台本周的容量占用。</summary>
/// <param name="Capacity">平台的周容量视图（design 6.5，与运营接口的容量视图相同）。</param>
/// <param name="DemandPercent">周需求占周容量的百分比（四舍五入为整数）；周容量为 0 时为空。</param>
public sealed record AppCapacityUsage(AppPlatformCapacity Capacity, int? DemandPercent);

/// <summary>
/// 最近连续在发题前失败的 App 绑定：过去 24 小时里它最近 <see cref="AppDailyReportRules.PreSendFailureStreak"/> 次已有结论的尝试都在发题前失败
/// （例如 App 界面改版后认不出发送界面）。没有自动熔断：这一项只是提示，不会停用绑定、暂停平台或改变任何状态；
/// 这些任务已经逐条转给其他设备。只含节点、设备、平台、账号代号和失败码。
/// </summary>
/// <param name="BindingId">绑定标识。</param>
/// <param name="NodeId">所属节点。</param>
/// <param name="DeviceId">设备标识（AVD 名称）。</param>
/// <param name="PlatformCode">平台代码。</param>
/// <param name="AccountLabel">账号代号。</param>
/// <param name="LastFailureCode">最近一次发题前失败的失败码。</param>
/// <param name="LastFailedAt">最近一次发题前失败的上报时间。</param>
public sealed record AppPreSendFailureAlert(Guid BindingId, string NodeId, string DeviceId, string PlatformCode, string AccountLabel,
    string? LastFailureCode, DateTimeOffset LastFailedAt);

/// <summary>
/// App 渠道当前的告警信息（results.md 8.4 的六项，加上连续发题前失败的绑定）。运营接口的告警视图与每日汇报读同一个查询。
/// 只含节点、设备、平台、账号代号和计数：不含提问原文、租户名称、品牌名称或任何账号凭证。
/// </summary>
/// <param name="AsOf">数据截止时刻（数据库的 UTC 时间）；失联时长、停止时长和“过去 24 小时”都按它计算。</param>
/// <param name="HasAlerts">是否有告警：有失联节点、停止的绑定、暂停或不可用的平台、过去 24 小时有未采集的任务、有平台预计排不到，或者有绑定连续在发题前失败。</param>
/// <param name="OfflineNodes">失联的节点（超过 15 分钟没有出现）及失联时长，按节点标识排序。</param>
/// <param name="StoppedBindings">停止的绑定及原因、停止了多久，按平台、节点、设备排序。</param>
/// <param name="Platforms">被暂停或判定不可用的平台，按平台顺序。</param>
/// <param name="NotCollectedLast24Hours">过去 24 小时内未采集的任务数，按平台和原因分列。</param>
/// <param name="ActivityLast24Hours">过去 24 小时的领取、成功、失败次数，每个已知平台一行。</param>
/// <param name="Capacity">各平台本周的容量占用，每个已知平台一行。</param>
public sealed record AppAlertsView(DateTimeOffset AsOf, bool HasAlerts, IReadOnlyList<AppOfflineNodeAlert> OfflineNodes,
    IReadOnlyList<AppStoppedBindingAlert> StoppedBindings, IReadOnlyList<AppPlatformAlert> Platforms,
    IReadOnlyList<AppNotCollectedCount> NotCollectedLast24Hours, IReadOnlyList<AppPlatformActivity> ActivityLast24Hours,
    IReadOnlyList<AppCapacityUsage> Capacity)
{
    /// <summary>
    /// 最近连续在发题前失败的绑定，按平台、节点、设备排序；没有时为空数组（这一项加入之前存档的汇报里也是空数组）。
    /// 只是提示：不会因此停用绑定或暂停平台。
    /// </summary>
    public IReadOnlyList<AppPreSendFailureAlert> PreSendFailures { get; init; } = [];
}

/// <summary>每日告警汇报的历史列表中的一项（不含内容）。</summary>
/// <param name="Id">汇报标识。</param>
/// <param name="ReportDate">汇报日期（Asia/Shanghai 本地日期），每天一份。</param>
/// <param name="GeneratedAt">生成时间。</param>
/// <param name="HasAlerts">生成时是否有告警。</param>
/// <param name="DeliveryStatus">发送状态；首期只有 not_configured（只存档，不发送）。</param>
/// <param name="SentAt">发送时间；未发送时为空。</param>
/// <param name="SendError">发送失败的原因；没有失败时为空。</param>
public sealed record AppDailyReportItem(Guid Id, DateOnly ReportDate, DateTimeOffset GeneratedAt, bool HasAlerts,
    AppDailyReportDeliveryStatus DeliveryStatus, DateTimeOffset? SentAt, string? SendError);

/// <summary>一份每日告警汇报：生成时的告警信息与据此写成的正文。</summary>
/// <param name="Id">汇报标识。</param>
/// <param name="ReportDate">汇报日期（Asia/Shanghai 本地日期）。</param>
/// <param name="GeneratedAt">生成时间。</param>
/// <param name="HasAlerts">是否有告警。</param>
/// <param name="DeliveryStatus">发送状态；首期只有 not_configured。</param>
/// <param name="SentAt">发送时间；未发送时为空。</param>
/// <param name="SendError">发送失败的原因；没有失败时为空。</param>
/// <param name="Alerts">生成时的告警信息，与运营接口的告警视图同一形状。</param>
/// <param name="Body">中文正文；没有告警时写明“无告警”。</param>
public sealed record AppDailyReportView(Guid Id, DateOnly ReportDate, DateTimeOffset GeneratedAt, bool HasAlerts,
    AppDailyReportDeliveryStatus DeliveryStatus, DateTimeOffset? SentAt, string? SendError, AppAlertsView Alerts, string Body);

/// <summary>一次生成每日汇报的结果。</summary>
/// <param name="Created">true 表示这次新生成了一份；false 表示这一天已经有了，返回的是已有的那一份。</param>
/// <param name="Report">这一天的汇报。</param>
public sealed record AppDailyReportGeneration(bool Created, AppDailyReportView Report);

/// <summary>一次发送每日汇报的结果。</summary>
/// <param name="Status">发送状态；没有配置发送渠道时为 not_configured。</param>
/// <param name="Error">发送失败的原因；其余情况为空。</param>
public sealed record AppDailyReportDelivery(AppDailyReportDeliveryStatus Status, string? Error = null);

/// <summary>每日告警汇报的纯规则（results.md 8.4）：何时到期、何时算有告警、正文的写法。不访问数据库。</summary>
public static class AppDailyReportRules
{
    /// <summary>节点超过这么多分钟没有出现视为失联；与绑定的离线判定（<see cref="AppPoolRules.BindingOfflineMinutes"/>）相同。</summary>
    public const int NodeOfflineMinutes = AppPoolRules.BindingOfflineMinutes;

    /// <summary>
    /// 一个绑定最近连续多少次已有结论的尝试都在发题前失败时，在告警信息里列出它。没有自动熔断，这个数只决定何时提示。
    /// </summary>
    public const int PreSendFailureStreak = 3;

    /// <summary>默认的生成时刻（本地时间）。</summary>
    public static readonly TimeOnly DefaultLocalTime = new(9, 30);

    /// <summary>默认的时区。</summary>
    public const string DefaultTimeZone = "Asia/Shanghai";

    /// <summary>正文的最大字符数（body 列是 TEXT，65 535 字节；按每个汉字 3 字节留出余量）。超过时从末尾截断并注明。</summary>
    public const int MaxBodyCharacters = 20_000;

    /// <summary>汇报内容（summary 列）的字节上限，与迁移 049 的 CHECK 相同。</summary>
    public const int MaxSummaryBytes = 262_144;

    /// <summary>
    /// 现在是否该生成某一天的汇报：本地时间（<paramref name="zone"/>）已到 <paramref name="localTime"/> 时返回本地的今天，否则返回空。
    /// 只返回“今天”：进程在生成时刻之后启动也会补上当天的那一份；更早漏掉的日子不补。
    /// </summary>
    /// <param name="nowUtc">当前时刻。</param>
    /// <param name="zone">本地时区。</param>
    /// <param name="localTime">每天的生成时刻（本地时间）。</param>
    public static DateOnly? DueDate(DateTimeOffset nowUtc, TimeZoneInfo zone, TimeOnly localTime)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = TimeZoneInfo.ConvertTime(nowUtc, zone);
        return TimeOnly.FromDateTime(local.DateTime) >= localTime ? DateOnly.FromDateTime(local.DateTime) : null;
    }

    /// <summary>是否有告警：任一列表非空、过去 24 小时有未采集的任务，或者有平台预计排不到。领取、成功、失败次数只是统计，不算告警。</summary>
    /// <param name="offline">失联的节点。</param>
    /// <param name="stopped">停止的绑定。</param>
    /// <param name="platforms">暂停或不可用的平台。</param>
    /// <param name="notCollected">过去 24 小时的未采集数。</param>
    /// <param name="capacity">容量占用。</param>
    /// <param name="preSendFailures">最近连续在发题前失败的绑定。</param>
    public static bool HasAlerts(IReadOnlyCollection<AppOfflineNodeAlert> offline, IReadOnlyCollection<AppStoppedBindingAlert> stopped,
        IReadOnlyCollection<AppPlatformAlert> platforms, IEnumerable<AppNotCollectedCount> notCollected, IEnumerable<AppCapacityUsage> capacity,
        IReadOnlyCollection<AppPreSendFailureAlert> preSendFailures)
        => offline.Count > 0 || stopped.Count > 0 || platforms.Count > 0 || notCollected.Any(item => item.Count > 0)
           || capacity.Any(item => item.Capacity.ExpectedUncollected > 0) || preSendFailures.Count > 0;

    /// <summary>周需求占周容量的百分比（四舍五入为整数）；周容量不大于 0 时为空。</summary>
    /// <param name="capacity">平台的容量视图。</param>
    public static int? DemandPercent(AppPlatformCapacity capacity)
        => capacity.WeeklyCapacity <= 0 ? null
            : (int)Math.Min(int.MaxValue, Math.Round(100m * capacity.WeeklyDemand / capacity.WeeklyCapacity, MidpointRounding.AwayFromZero));

    /// <summary>
    /// 汇报的中文正文。只用告警信息里的节点、设备、平台、账号代号、原因码和计数，没有提问原文、租户名称或品牌名称。
    /// 没有告警时第一段写明“无告警”。超过 <see cref="MaxBodyCharacters"/> 时从末尾截断并注明。
    /// </summary>
    /// <param name="reportDate">汇报日期。</param>
    /// <param name="alerts">告警信息。</param>
    public static string Render(DateOnly reportDate, AppAlertsView alerts)
    {
        ArgumentNullException.ThrowIfNull(alerts);
        var text = new StringBuilder();
        text.Append("App 渠道每日汇报：").AppendLine(reportDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        text.Append("数据截至：").Append(Utc(alerts.AsOf)).AppendLine("（UTC）");
        if (!alerts.HasAlerts) text.AppendLine("结论：无告警。");
        else
        {
            var parts = new List<string>();
            if (alerts.OfflineNodes.Count > 0) parts.Add($"失联节点 {alerts.OfflineNodes.Count} 个");
            if (alerts.StoppedBindings.Count > 0) parts.Add($"停止的绑定 {alerts.StoppedBindings.Count} 条");
            if (alerts.Platforms.Count > 0) parts.Add($"暂停或不可用的平台 {alerts.Platforms.Count} 个");
            var notCollected = alerts.NotCollectedLast24Hours.Sum(item => item.Count);
            if (notCollected > 0) parts.Add($"过去 24 小时未采集 {notCollected} 条");
            var shortPlatforms = alerts.Capacity.Count(item => item.Capacity.ExpectedUncollected > 0);
            if (shortPlatforms > 0) parts.Add($"预计排不到的平台 {shortPlatforms} 个");
            if (alerts.PreSendFailures.Count > 0) parts.Add($"连续发题前失败的绑定 {alerts.PreSendFailures.Count} 条");
            text.Append("结论：有告警（").Append(string.Join("，", parts)).AppendLine("）。");
        }

        text.AppendLine().AppendLine($"一、失联的节点（超过 {NodeOfflineMinutes} 分钟没有出现）");
        if (alerts.OfflineNodes.Count == 0) text.AppendLine("  无");
        foreach (var node in alerts.OfflineNodes)
            text.Append("  - ").Append(node.NodeId).Append("：最近出现 ")
                .Append(node.LastActivityAt is { } seen ? Utc(seen) : "从未出现").Append("，已失联 ").AppendLine(Duration(node.OfflineSeconds));

        text.AppendLine().AppendLine("二、停止的绑定");
        if (alerts.StoppedBindings.Count == 0) text.AppendLine("  无");
        foreach (var binding in alerts.StoppedBindings)
            text.Append("  - ").Append(Platform(binding.PlatformCode)).Append(' ').Append(binding.NodeId).Append(" / ").Append(binding.DeviceId)
                .Append(" / ").Append(binding.AccountLabel).Append("：").Append(AppContractNames.ToStorage(binding.StopReason))
                .Append("，已停止 ").AppendLine(Duration(binding.StoppedSeconds));

        text.AppendLine().AppendLine("三、被暂停或判定不可用的平台");
        if (alerts.Platforms.Count == 0) text.AppendLine("  无");
        foreach (var platform in alerts.Platforms)
        {
            var states = new List<string>();
            if (platform.Paused) states.Add($"已暂停（{platform.PauseReason}）");
            if (platform.Unavailable) states.Add($"入口不可用（{platform.UnavailableReason ?? "未注明原因"}）");
            text.Append("  - ").Append(Platform(platform.PlatformCode)).Append("：").AppendLine(string.Join("，", states));
        }

        text.AppendLine().AppendLine("四、过去 24 小时未采集的任务");
        if (!alerts.NotCollectedLast24Hours.Any(item => item.Count > 0)) text.AppendLine("  无");
        foreach (var item in alerts.NotCollectedLast24Hours.Where(item => item.Count > 0))
            text.Append("  - ").Append(Platform(item.PlatformCode)).Append("：").Append(item.ReasonCode).Append(' ').AppendLine(Count(item.Count));

        text.AppendLine().AppendLine("五、过去 24 小时的领取 / 成功 / 失败");
        foreach (var item in alerts.ActivityLast24Hours)
            text.Append("  - ").Append(Platform(item.PlatformCode)).Append("：领取 ").Append(Count(item.Leases)).Append("，成功 ")
                .Append(Count(item.Successes)).Append("，失败 ").Append(Count(item.Failures)).Append("，租约过期 ")
                .AppendLine(Count(item.LeaseExpirations));

        text.AppendLine().AppendLine("六、本周容量占用");
        foreach (var item in alerts.Capacity)
        {
            var capacity = item.Capacity;
            text.Append("  - ").Append(Platform(capacity.Platform)).Append("：");
            if (!capacity.Available) text.Append("入口不可用，");
            text.Append("需求 ").Append(Count(capacity.WeeklyDemand)).Append(" / 容量 ").Append(Count(capacity.WeeklyCapacity))
                .Append(item.DemandPercent is { } percent ? $"（{percent}%）" : "").Append("，可用绑定 ").Append(capacity.ActiveBindings)
                .Append("，预计排不到 ").AppendLine(Count(capacity.ExpectedUncollected));
        }

        text.AppendLine().AppendLine($"七、连续发题前失败的绑定（最近 {PreSendFailureStreak} 次尝试都在发题前失败；只提示，不自动停用）");
        if (alerts.PreSendFailures.Count == 0) text.AppendLine("  无");
        foreach (var binding in alerts.PreSendFailures)
            text.Append("  - ").Append(Platform(binding.PlatformCode)).Append(' ').Append(binding.NodeId).Append(" / ").Append(binding.DeviceId)
                .Append(" / ").Append(binding.AccountLabel).Append("：最近一次 ").Append(binding.LastFailureCode ?? "未注明失败码")
                .Append("，").AppendLine(Utc(binding.LastFailedAt));

        var body = text.ToString();
        const string truncated = "\n……（正文过长，已截断；完整内容见汇报的告警信息）";
        return body.Length <= MaxBodyCharacters ? body : body[..(MaxBodyCharacters - truncated.Length)] + truncated;
    }

    private static string Platform(string code)
        => AppChannelDefinition.TryGetPlatform(code) is { } platform ? $"{platform.DisplayName}（{platform.Code}）" : code;

    private static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Count(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>把秒数写成“x 天 y 小时 z 分钟”（不足一分钟写“不到 1 分钟”）。</summary>
    /// <param name="seconds">秒数；负数按 0 计。</param>
    public static string Duration(long seconds)
    {
        seconds = Math.Max(0, seconds);
        if (seconds < 60) return "不到 1 分钟";
        var (days, hours, minutes) = (seconds / 86_400, seconds % 86_400 / 3_600, seconds % 3_600 / 60);
        var parts = new List<string>();
        if (days > 0) parts.Add($"{days} 天");
        if (hours > 0) parts.Add($"{hours} 小时");
        if (minutes > 0) parts.Add($"{minutes} 分钟");
        return string.Join(" ", parts);
    }
}
