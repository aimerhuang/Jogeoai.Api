namespace Jogeoai.Domain;

/// <summary>
/// App 全局账号池运营接口（/api/v1/admin/collector-app/，implement 8.3）的取值范围与常量。纯规则，不访问数据库。
/// 范围之外的请求返回 400；数据库约束只要求间隔大于 0、上限大于 0，这里的范围更窄，防止误操作把真实账号的提问频率调得过高。
/// </summary>
public static class AppAdminRules
{
    /// <summary>运营可设置的绑定领取间隔下限（秒）。默认值是 100（迁移 050，取代 KD-21 的 300）。</summary>
    public const int MinBindingIntervalSeconds = 60;

    /// <summary>运营可设置的绑定领取间隔上限（秒），即一天。</summary>
    public const int MaxBindingIntervalSeconds = 86_400;

    /// <summary>运营可设置的绑定每日（最近 24 小时）领取上限的最大值；为空表示不设上限（默认）。</summary>
    public const int MaxBindingDailyQuota = 10_000;

    /// <summary>运营可设置的平台合计领取间隔下限（秒）。默认值是 30（KD-21）。</summary>
    public const int MinPlatformIntervalSeconds = 10;

    /// <summary>运营可设置的平台合计领取间隔上限（秒），即一天。</summary>
    public const int MaxPlatformIntervalSeconds = 86_400;

    /// <summary>运营可设置的平台每日（Asia/Shanghai 自然日）领取上限的最大值；为空表示不设上限（默认）。</summary>
    public const int MaxPlatformDailyCap = 100_000;

    /// <summary>运营暂停平台时写入 collector_app_platform_states.pause_reason 的取值（验证熔断写的是 verification_breaker）。</summary>
    public const string OperatorPauseReason = "operator_paused";

    /// <summary>审计事件列表每页的默认条数。</summary>
    public const int DefaultEventPageSize = 50;

    /// <summary>审计事件列表每页的最大条数。</summary>
    public const int MaxEventPageSize = 200;

    /// <summary>每日汇报历史列表每页的默认条数。</summary>
    public const int DefaultReportPageSize = 30;

    /// <summary>每日汇报历史列表每页的最大条数。</summary>
    public const int MaxReportPageSize = 100;

    /// <summary>绑定的领取间隔与每日上限是否在运营可设置的范围内。</summary>
    /// <param name="minIntervalSeconds">领取间隔秒数。</param>
    /// <param name="dailyQuota">每日上限；为空表示不设上限。</param>
    public static bool BindingLimitsValid(int minIntervalSeconds, int? dailyQuota)
        => minIntervalSeconds is >= MinBindingIntervalSeconds and <= MaxBindingIntervalSeconds
           && dailyQuota is null or (>= 1 and <= MaxBindingDailyQuota);

    /// <summary>平台的合计领取间隔与每日上限是否在运营可设置的范围内。</summary>
    /// <param name="minIntervalSeconds">领取间隔秒数。</param>
    /// <param name="dailyCap">每日上限；为空表示不设上限。</param>
    public static bool PlatformLimitsValid(int minIntervalSeconds, int? dailyCap)
        => minIntervalSeconds is >= MinPlatformIntervalSeconds and <= MaxPlatformIntervalSeconds
           && dailyCap is null or (>= 1 and <= MaxPlatformDailyCap);

    /// <summary>运营设置的节点设备上限是否在范围内（1–<see cref="AppPoolRules.MaxDevicesPerNodeLimit"/>，与迁移 052 的 CHECK 相同）。</summary>
    /// <param name="maxDevices">设备上限。</param>
    public static bool MaxDevicesValid(int maxDevices) => maxDevices is >= 1 and <= AppPoolRules.MaxDevicesPerNodeLimit;
}

/// <summary>运营视图里的一个 App 采集节点（一台电脑上的采集器）。不含令牌或令牌哈希。</summary>
/// <param name="NodeId">节点标识。</param>
/// <param name="Status">节点状态。</param>
/// <param name="LastSeenAt">节点最近一次上报绑定的时间；从未上报时为空。</param>
/// <param name="LastActivityAt">节点最近一次出现的时间：它的绑定上报，以及它任一绑定的领取、失败上报、结果提交中最晚的一次；从未出现时为空。</param>
/// <param name="Offline">节点为 active 且超过 15 分钟没有出现（或从未出现）时为 true；被停用的节点为 false。</param>
/// <param name="CollectorBuildRevision">最近一次上报的采集器构建版本。</param>
/// <param name="Bindings">该节点登记的绑定数。</param>
/// <param name="ActiveBindings">其中状态为 active 的绑定数。</param>
/// <param name="CreatedAt">登记时间。</param>
/// <param name="UpdatedAt">最近一次修改时间。</param>
public sealed record AppNodeView(string NodeId, AppNodeStatus Status, DateTimeOffset? LastSeenAt, DateTimeOffset? LastActivityAt, bool Offline,
    string? CollectorBuildRevision, int Bindings, int ActiveBindings, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    /// <summary>
    /// 该节点可登记的不同设备（AVD）数上限，默认 4，范围 1–64；超出上限的新设备的绑定在上报时被拒绝（device_limit_exceeded），不入库。
    /// 用 PUT nodes/{nodeId}/max-devices 调整。
    /// </summary>
    public int MaxDevices { get; init; } = AppPoolRules.DefaultMaxDevicesPerNode;
}

/// <summary>运营对节点的一次启停操作的结果。</summary>
/// <param name="Changed">true 表示状态确实发生了变化（并写了审计事件）；false 表示本来就是目标状态。</param>
/// <param name="Node">操作之后的节点。</param>
public sealed record AppNodeChange(bool Changed, AppNodeView Node);

/// <summary>运营视图里的一条 App 绑定（设备 × App × 账号）。账号只以代号出现，不含任何凭证或可识别的账号信息。</summary>
/// <param name="Id">绑定标识。</param>
/// <param name="NodeId">所属节点。</param>
/// <param name="DeviceId">设备标识（AVD 名称）。</param>
/// <param name="PlatformCode">平台代码。</param>
/// <param name="AccountLabel">账号代号。</param>
/// <param name="AppPackage">App 包名。</param>
/// <param name="ReviewedAppVersion">已审阅的 App 版本。</param>
/// <param name="InstalledAppVersion">设备上已装的 App 版本。</param>
/// <param name="AndroidVersion">Android 版本。</param>
/// <param name="Status">绑定状态。</param>
/// <param name="StopReason">停止原因；未停止时为空。</param>
/// <param name="StoppedAt">进入停止状态的时间；未停止时为空。</param>
/// <param name="StoppedSeconds">已经停止了多少秒；未停止时为空。</param>
/// <param name="CooldownUntil">仍在生效的冷却截止时间；不在冷却中时为空。</param>
/// <param name="Offline">超过 15 分钟没有出现（或从未出现）时为 true；只用于展示，领取时不按它判断。</param>
/// <param name="LastSeenAt">最近一次出现（上报、领取、失败上报、结果提交）的时间。</param>
/// <param name="LastLeasedAt">最近一次领取到任务的时间。</param>
/// <param name="LastSuccessAt">最近一次结果被接受的时间。</param>
/// <param name="MinIntervalSeconds">兼容字段：设备实际发送后的固定冷却秒数，不是可配置的绑定间隔。</param>
/// <param name="DailyQuota">兼容字段，固定 null，设备不设领取配额。</param>
/// <param name="CreatedAt">登记时间。</param>
/// <param name="UpdatedAt">最近一次修改时间。</param>
public sealed record AppBindingAdminView(Guid Id, string NodeId, string DeviceId, string PlatformCode, string AccountLabel, string AppPackage,
    string? ReviewedAppVersion, string? InstalledAppVersion, string? AndroidVersion, AppBindingStatus Status, AppBindingStopReason? StopReason,
    DateTimeOffset? StoppedAt, long? StoppedSeconds, DateTimeOffset? CooldownUntil, bool Offline, DateTimeOffset? LastSeenAt,
    DateTimeOffset? LastLeasedAt, DateTimeOffset? LastSuccessAt, int MinIntervalSeconds, int? DailyQuota, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>
    /// 是否待批准：true 表示这条绑定还没有被运营批准过。待批准的绑定不能领取任务，它的失败上报不写事件、不计入平台熔断；
    /// 它的停止原因显示为 disabled_by_operator。调用恢复接口（POST bindings/{bindingId}/resume）即批准。
    /// </summary>
    public bool ApprovalPending { get; init; }

    /// <summary>运营批准这条绑定的时间；待批准时为空。批准之后再被停用、恢复，这个时间不变。迁移 052 之前登记的绑定取登记时间。</summary>
    public DateTimeOffset? ApprovedAt { get; init; }
}

/// <summary>运营对绑定的一次操作的结果。</summary>
/// <param name="Changed">true 表示绑定确实发生了变化（并写了审计事件）；false 表示本来就是目标状态或目标取值。</param>
/// <param name="Binding">操作之后的绑定。</param>
public sealed record AppBindingChange(bool Changed, AppBindingAdminView Binding);

/// <summary>运营视图里的一个 App 平台的闸门与入口状态。</summary>
/// <param name="PlatformCode">平台代码。</param>
/// <param name="DisplayName">平台显示名。</param>
/// <param name="MinIntervalSeconds">兼容字段，固定 0，无平台派单间隔。</param>
/// <param name="DailyCap">兼容字段，固定 null，无平台配额。</param>
/// <param name="LastDispatchedAt">最近一次领取的时间。</param>
/// <param name="Paused">平台是否被暂停（运营暂停或验证熔断）。</param>
/// <param name="PausedAt">暂停的时间；未暂停时为空。</param>
/// <param name="PauseReason">暂停原因：operator_paused 或 verification_breaker；未暂停时为空。</param>
/// <param name="EntryAvailable">平台的 App 入口是否可用（启用）。</param>
/// <param name="UnavailableReason">入口不可用的原因码（如 APP_EMULATOR_REJECTED、APP_CONTENT_UNREADABLE）；可用时为空。</param>
/// <param name="EntryRestorable">入口不可用且原因是 APP_EMULATOR_REJECTED，可以由运营恢复。</param>
public sealed record AppPlatformStateView(string PlatformCode, string DisplayName, int MinIntervalSeconds, int? DailyCap,
    DateTimeOffset? LastDispatchedAt, bool Paused, DateTimeOffset? PausedAt, string? PauseReason, bool EntryAvailable,
    string? UnavailableReason, bool EntryRestorable);

/// <summary>运营对平台的一次操作的结果。</summary>
/// <param name="Changed">true 表示平台确实发生了变化（并写了审计事件）；false 表示本来就是目标状态或目标取值。</param>
/// <param name="Platform">操作之后的平台。</param>
public sealed record AppPlatformChange(bool Changed, AppPlatformStateView Platform);

/// <summary>一个 App 平台的任务队列。</summary>
/// <param name="PlatformCode">平台代码。</param>
/// <param name="Waiting">待领取：未过截止时点、尚未成功或终态、当前没有有效租约的 App 任务数（含退避中的任务和所属租户已关闭的任务）。</param>
/// <param name="InProgress">进行中：持有未过期租约的 App 任务数。</param>
public sealed record AppPlatformQueueView(string PlatformCode, long Waiting, long InProgress);

/// <summary>一个 App 平台的容量与队列（运营视图）。</summary>
/// <param name="Capacity">平台的周容量视图（design 6.5）。</param>
/// <param name="Queue">平台当前的任务队列。</param>
public sealed record AppPlatformCapacityQueueView(AppPlatformCapacity Capacity, AppPlatformQueueView Queue);

/// <summary>审计事件的筛选条件；为空的条件不筛选。时间按 occurred_at 的半开区间 [From, To)。</summary>
/// <param name="From">起始时间（含）。</param>
/// <param name="To">结束时间（不含）。</param>
/// <param name="NodeId">节点标识。</param>
/// <param name="BindingId">绑定标识。</param>
/// <param name="PlatformCode">平台代码。</param>
/// <param name="EventKind">事件种类，例如 binding_stopped、platform_paused。</param>
public sealed record AppEventQuery(DateTimeOffset? From = null, DateTimeOffset? To = null, string? NodeId = null, Guid? BindingId = null,
    string? PlatformCode = null, string? EventKind = null);

/// <summary>一条 App 审计事件（collector_app_events，只追加）。detail 不含提问原文、回答内容或账号凭证。</summary>
/// <param name="Id">事件标识。</param>
/// <param name="OccurredAt">发生时间。</param>
/// <param name="EventKind">事件种类。</param>
/// <param name="NodeId">相关节点。</param>
/// <param name="BindingId">相关绑定。</param>
/// <param name="PlatformCode">相关平台。</param>
/// <param name="TaskId">相关采集任务。</param>
/// <param name="Actor">触发者：node:&lt;节点&gt;、operator:&lt;用户&gt;、web-collector:&lt;节点&gt; 等。</param>
/// <param name="Detail">事件详情（JSON）；没有时为空。</param>
public sealed record AppEventView(Guid Id, DateTimeOffset OccurredAt, string EventKind, string? NodeId, Guid? BindingId, string? PlatformCode,
    Guid? TaskId, string Actor, System.Text.Json.JsonElement? Detail);

/// <summary>
/// 一条 App 采集任务的尝试历史（运营按任务标识追溯）：任务当前的状态与最终结果，以及每一次租约的设备、账号代号、发题与失败情况。
/// 只读；不含提问原文、回答内容或任何账号凭证。
/// </summary>
/// <param name="TaskId">采集任务标识。</param>
/// <param name="PlatformCode">任务的目标平台代码。</param>
/// <param name="Status">任务当前的状态：pending、leased、collecting、retryable_failed（等待另一台设备领取）、succeeded 或 terminal_failed。</param>
/// <param name="Outcome">最终结果：collected（已取得正式回答）或 not_collected（未采集）；任务尚未结束时为空。</param>
/// <param name="NotCollectedCode">未采集的原因码（APP_NOT_COLLECTED_*）；不是未采集时为空。</param>
/// <param name="LeaseVersion">任务当前的租约版本，也就是已经发出的租约次数；从未被领取时为 0。</param>
/// <param name="LastErrorCode">最近一次失败码（含 lease_expired：租约过期而设备没有任何回报）；没有失败过时为空。</param>
/// <param name="DeadlineAt">任务所属周期的截止时点；到截止仍未成功即记为未采集。</param>
/// <param name="CompletedAt">任务结束（成功或未采集）的时间；尚未结束时为空。</param>
/// <param name="Attempts">每一次租约一行，按租约版本升序。迁移 051 之前发出的租约没有记录。</param>
public sealed record AppTaskAttemptsView(Guid TaskId, string PlatformCode, string Status, string? Outcome, string? NotCollectedCode,
    int LeaseVersion, string? LastErrorCode, DateTimeOffset? DeadlineAt, DateTimeOffset? CompletedAt, IReadOnlyList<AppTaskAttemptView> Attempts)
{
    /// <summary><see cref="Outcome"/> 的取值：任务已取得正式回答。</summary>
    public const string OutcomeCollected = "collected";

    /// <summary><see cref="Outcome"/> 的取值：任务已记为未采集。</summary>
    public const string OutcomeNotCollected = "not_collected";
}

/// <summary>一条 App 采集任务的一次尝试（一次租约）。账号只以代号出现。</summary>
/// <param name="LeaseVersion">这次尝试的租约版本。</param>
/// <param name="NodeId">执行这次尝试的节点。</param>
/// <param name="DeviceId">执行这次尝试的设备（AVD 名称）；设备由节点和它共同标识。</param>
/// <param name="BindingId">持有这次租约的绑定。</param>
/// <param name="PlatformCode">这次尝试的平台代码。</param>
/// <param name="AccountLabel">绑定的账号代号；绑定已不存在时为空。</param>
/// <param name="LeasedAt">领取的时间。</param>
/// <param name="LeaseUntil">租约截止时间（含续租）。没有任何回报而过了这个时间的尝试视同失败，任务由另一台设备接手。</param>
/// <param name="SendState">发送状态：sent（已发题）、not_sent（确认没有发出）、unknown（无法确认是否发出）；设备没有回报时为空。</param>
/// <param name="SentAt">设备实际发题的时间；没有发题或没有回报时为空。</param>
/// <param name="FailureCode">设备上报的失败码；没有上报失败时为空。</param>
/// <param name="FailedAt">收到失败上报的时间；没有上报失败时为空。</param>
/// <param name="Accepted">这次尝试提交的结果是否就是被接受的正式回答；一个任务最多一次为 true。</param>
public sealed record AppTaskAttemptView(int LeaseVersion, string NodeId, string DeviceId, Guid BindingId, string PlatformCode, string? AccountLabel,
    DateTimeOffset LeasedAt, DateTimeOffset LeaseUntil, string? SendState, DateTimeOffset? SentAt, string? FailureCode, DateTimeOffset? FailedAt,
    bool Accepted);
