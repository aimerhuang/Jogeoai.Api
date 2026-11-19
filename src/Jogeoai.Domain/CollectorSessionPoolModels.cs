using System.Text.Json.Serialization;

namespace Jogeoai.Domain;

/// <summary>采集账号（session profile）状态。</summary>
public enum CollectorSessionProfileStatus
{
    /// <summary>可领取任务。</summary>
    [JsonStringEnumMemberName("active")]
    Active,

    /// <summary>被平台限流后冷却中，到期自动恢复。</summary>
    [JsonStringEnumMemberName("cooling")]
    Cooling,

    /// <summary>遇到验证码，需要人工在账号窗口处理。</summary>
    [JsonStringEnumMemberName("needs_verification")]
    NeedsVerification,

    /// <summary>登录失效，需要人工重新登录。</summary>
    [JsonStringEnumMemberName("expired")]
    Expired,

    /// <summary>运维停用。</summary>
    [JsonStringEnumMemberName("disabled")]
    Disabled,
}

/// <summary>Collector 可上报的分类失败码；陈旧租约、落盘、停机等本地情况不上报。</summary>
public enum CollectorFailureCode
{
    /// <summary>需要登录。</summary>
    [JsonStringEnumMemberName("login_required")]
    LoginRequired,

    /// <summary>平台限制当前区域。</summary>
    [JsonStringEnumMemberName("region_restricted")]
    RegionRestricted,

    /// <summary>出现验证码且等待超时。</summary>
    [JsonStringEnumMemberName("challenge_required")]
    ChallengeRequired,

    /// <summary>被平台限流。</summary>
    [JsonStringEnumMemberName("rate_limited")]
    RateLimited,

    /// <summary>页面结构与已审页面契约不一致。</summary>
    [JsonStringEnumMemberName("page_contract_changed")]
    PageContractChanged,

    /// <summary>页面契约未审核。</summary>
    [JsonStringEnumMemberName("page_contract_unverified")]
    PageContractUnverified,

    /// <summary>采集超时。</summary>
    [JsonStringEnumMemberName("collection_timeout")]
    CollectionTimeout,

    /// <summary>浏览器异常。</summary>
    [JsonStringEnumMemberName("browser_failed")]
    BrowserFailed,
}

/// <summary>失败或事件发生的采集阶段。</summary>
public enum CollectorPhase
{
    /// <summary>领取前的页面探测，不带任务。</summary>
    [JsonStringEnumMemberName("probe")]
    Probe,

    /// <summary>发送问题之前。</summary>
    [JsonStringEnumMemberName("pre_send")]
    PreSend,

    /// <summary>问题已发送、回答生成中。</summary>
    [JsonStringEnumMemberName("post_send")]
    PostSend,
}

/// <summary>Collector 上报的非失败账号事件。</summary>
public enum CollectorSessionEventKind
{
    /// <summary>开始等待人工处理验证码。</summary>
    [JsonStringEnumMemberName("challenge_wait_started")]
    ChallengeWaitStarted,

    /// <summary>验证码在等待期内消失。</summary>
    [JsonStringEnumMemberName("challenge_cleared")]
    ChallengeCleared,

    /// <summary>运维 probe 结果为 ready，账号恢复可用。</summary>
    [JsonStringEnumMemberName("verified")]
    Verified,
}

/// <summary>失败上报对任务的处理结果。</summary>
public enum CollectorTaskDisposition
{
    /// <summary>租约已释放，退避后重新入队。</summary>
    [JsonStringEnumMemberName("requeued")]
    Requeued,

    /// <summary>达到失败上限，任务终止。</summary>
    [JsonStringEnumMemberName("failed")]
    Failed,

    /// <summary>上报的租约已不是当前租约，任务未改动。</summary>
    [JsonStringEnumMemberName("stale")]
    Stale,
}

/// <summary>只有人工才能解除的账号状态；Collector 收到后退出而不是轮询。</summary>
public enum CollectorEligibilityStopReason
{
    /// <summary>需要人工处理验证码。</summary>
    [JsonStringEnumMemberName("needs_verification")]
    NeedsVerification,

    /// <summary>登录失效。</summary>
    [JsonStringEnumMemberName("expired")]
    Expired,

    /// <summary>运维停用。</summary>
    [JsonStringEnumMemberName("disabled")]
    Disabled,
}

/// <summary>上报中引用的租约任务。</summary>
/// <param name="TaskId">采集任务标识。</param>
/// <param name="LeaseVersion">上报时持有的租约版本。</param>
public sealed record CollectorReportedTask(Guid TaskId, int LeaseVersion);

/// <summary>账号模式 Collector 访问平台页面前的只读预检。</summary>
public sealed record CollectorEligibilityCommand(
    Guid TenantId,
    string NodeId,
    CollectorRegion Region,
    Guid SessionProfileId,
    string PlatformCode);

/// <summary>预检结果；eligible 为 true 时租约仍可能因竞争返回空。</summary>
/// <param name="Eligible">当前是否可以领取任务。</param>
/// <param name="RetryAfterSeconds">下次检查前等待的秒数（30–120）。</param>
/// <param name="StopReason">需要人工处理时的停止原因；无值时不输出该字段。</param>
public sealed record CollectorEligibilityResult(
    bool Eligible,
    int RetryAfterSeconds,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CollectorEligibilityStopReason? StopReason = null);

/// <summary>Collector 分类失败上报。</summary>
public sealed record CollectorFailureCommand(
    Guid TenantId,
    string NodeId,
    CollectorRegion Region,
    Guid SessionProfileId,
    CollectorFailureCode FailureCode,
    CollectorPhase Phase,
    CollectorReportedTask? Task);

/// <summary>失败上报的处理结果。</summary>
/// <param name="SessionProfileStatus">处理后的账号状态。</param>
/// <param name="TaskDisposition">任务处理结果；probe 失败不带任务时为 null。</param>
/// <param name="PlatformPaused">平台是否处于熔断暂停。</param>
public sealed record CollectorFailureResult(
    CollectorSessionProfileStatus SessionProfileStatus,
    CollectorTaskDisposition? TaskDisposition,
    bool PlatformPaused);

/// <summary>Collector 上报的非失败账号事件。</summary>
public sealed record CollectorSessionEventCommand(
    Guid TenantId,
    string NodeId,
    CollectorRegion Region,
    Guid SessionProfileId,
    CollectorSessionEventKind EventKind,
    CollectorPhase Phase,
    CollectorReportedTask? Task);

/// <summary>账号事件的处理结果。</summary>
/// <param name="SessionProfileStatus">处理后的账号状态。</param>
public sealed record CollectorSessionEventResult(CollectorSessionProfileStatus SessionProfileStatus);

/// <summary>运维登记一个授权账号；只保存非敏感元数据。</summary>
public sealed record RegisterCollectorSessionProfileCommand(
    Guid TenantId,
    string PlatformCode,
    string Region,
    string Label,
    int? HourlyQuota,
    int? DailyQuota,
    int? MinIntervalSeconds,
    Guid? OperatorUserId);

/// <summary>运维设置平台闸门参数；为 null 的字段保持原值。</summary>
public sealed record ConfigureCollectorPlatformDispatchCommand(
    Guid TenantId,
    string Region,
    string PlatformCode,
    int? MinIntervalSeconds,
    int? DailyCap,
    int? BreakerProfileThreshold,
    int? BreakerWindowMinutes,
    int? RetryBackoffSeconds,
    int? MaxTaskFailures,
    int? RateLimitCooldownBaseSeconds,
    int? RateLimitCooldownMaxSeconds,
    int? ProbeFailureOfflineMinutes,
    Guid? OperatorUserId);

/// <summary>按账号的运维指标；计数来自尝试记录和健康事件，不来自可变计数器。</summary>
/// <param name="Id">账号标识，即 Collector 的 COLLECTOR_SESSION_PROFILE_ID。</param>
/// <param name="Label">运维别名。</param>
/// <param name="PlatformCode">平台代码。</param>
/// <param name="Region">区域。</param>
/// <param name="Status">账号状态。</param>
/// <param name="CooldownUntil">冷却截止时间。</param>
/// <param name="HourlyQuota">每小时配额。</param>
/// <param name="DailyQuota">每日配额。</param>
/// <param name="MinIntervalSeconds">账号最小领取间隔。</param>
/// <param name="ConsecutiveChallenges">连续验证码次数。</param>
/// <param name="LastLeasedAt">最近领取时间。</param>
/// <param name="LastSeenAt">最近在线时间。</param>
/// <param name="LeasedLastHour">近 1 小时领取数。</param>
/// <param name="LeasedLast24h">近 24 小时领取数。</param>
/// <param name="AcceptedLast24h">近 24 小时成功提交数。</param>
/// <param name="ChallengesLast24h">近 24 小时验证码次数（一次超时的等待只计一次）。</param>
/// <param name="ChallengeRateLast24h">验证码次数 / 领取数；无领取时为 null。</param>
/// <param name="LastChallengeAt">最近一次验证码时间。</param>
/// <param name="LastProbeFailedAt">最近一次 probe 阶段失败时间。</param>
/// <param name="ExcludedFromFairness">是否因近期 probe 失败退出其他账号的公平队列。</param>
public sealed record CollectorSessionProfileMetrics(
    Guid Id,
    string Label,
    string PlatformCode,
    string Region,
    CollectorSessionProfileStatus Status,
    string? CooldownUntil,
    int HourlyQuota,
    int DailyQuota,
    int MinIntervalSeconds,
    int ConsecutiveChallenges,
    string? LastLeasedAt,
    string? LastSeenAt,
    int LeasedLastHour,
    int LeasedLast24h,
    int AcceptedLast24h,
    int ChallengesLast24h,
    double? ChallengeRateLast24h,
    string? LastChallengeAt,
    string? LastProbeFailedAt,
    bool ExcludedFromFairness);

/// <summary>平台闸门状态。</summary>
/// <param name="Region">区域。</param>
/// <param name="PlatformCode">平台代码。</param>
/// <param name="MinIntervalSeconds">平台最小派发间隔。</param>
/// <param name="DailyCap">平台每日总上限（北京时间自然日）。</param>
/// <param name="LastDispatchedAt">最近派发时间。</param>
/// <param name="PausedAt">熔断暂停时间；为空表示未暂停。</param>
/// <param name="PauseReason">暂停原因。</param>
/// <param name="BreakerProfileThreshold">触发熔断的不同账号数。</param>
/// <param name="BreakerWindowMinutes">熔断统计窗口（分钟）。</param>
/// <param name="RetryBackoffSeconds">失败任务重新入队的退避秒数。</param>
/// <param name="MaxTaskFailures">任务失败上限。</param>
/// <param name="RateLimitCooldownBaseSeconds">限流冷却起始秒数。</param>
/// <param name="RateLimitCooldownMaxSeconds">限流冷却上限秒数。</param>
/// <param name="ProbeFailureOfflineMinutes">probe 失败后退出公平队列的分钟数。</param>
public sealed record CollectorPlatformDispatchStateView(
    string Region,
    string PlatformCode,
    int MinIntervalSeconds,
    int DailyCap,
    string? LastDispatchedAt,
    string? PausedAt,
    string? PauseReason,
    int BreakerProfileThreshold,
    int BreakerWindowMinutes,
    int RetryBackoffSeconds,
    int MaxTaskFailures,
    int RateLimitCooldownBaseSeconds,
    int RateLimitCooldownMaxSeconds,
    int ProbeFailureOfflineMinutes);
