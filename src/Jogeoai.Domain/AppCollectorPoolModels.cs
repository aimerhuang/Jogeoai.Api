namespace Jogeoai.Domain;

/// <summary>
/// App 全局账号池的纯规则（design 6.1 / 6.2，contract.md 第 9 节）：限速与熔断的常量、停止原因的先后、重试秒数。
/// 不访问数据库，同样的输入结果恒定。
/// </summary>
public static class AppPoolRules
{
    /// <summary>设备级派单协议。旧采集端不能领新租约。</summary>
    public const string DispatchProtocol = "device-v1";
    /// <summary>同一节点、同一虚机的两次实际发题至少间隔的秒数。</summary>
    public const int DeviceCooldownSeconds = 100;
    /// <summary>领取与续租的租约秒数下限。</summary>
    public const int MinLeaseSeconds = 5;

    /// <summary>领取与续租的租约秒数上限。</summary>
    public const int MaxLeaseSeconds = 900;

    /// <summary>领取返回空时 retryAfterSeconds 的下限。</summary>
    public const int RetryMinSeconds = 30;

    /// <summary>领取返回空时 retryAfterSeconds 的上限。</summary>
    public const int RetryMaxSeconds = 600;

    /// <summary>没有可领取任务（no_task）时的 retryAfterSeconds。</summary>
    public const int NoTaskRetrySeconds = 60;

    /// <summary>
    /// 可领取的任务所属租户余额都不足（credit_insufficient）时的 retryAfterSeconds：与 no_task 相同，
    /// 因为补足余额或别的租户出现新任务都可能随时发生。
    /// </summary>
    public const int CreditInsufficientRetrySeconds = 60;

    /// <summary>
    /// 一次领取最多加锁查看的候选任务数。余额不足的租户的任务在选任务的查询里就被越过，不占这个数；
    /// 占用它的只有两种：加锁后才发现余额已被别的事务用掉的任务，以及积分条目已结算或释放、任务却仍可领取的任务。
    /// </summary>
    public const int MaxLeaseCandidates = 8;

    /// <summary>租约过期、既没有结果也没有失败上报的任务再次被领取时（视同上一次尝试失败）写入 collector_tasks.last_error_code 的取值。</summary>
    public const string LeaseExpiredErrorCode = "lease_expired";

    /// <summary>计入失败次数的失败（App 崩溃、采集超时）之后，任务重新排队前的退避秒数；与网页账号池的默认值相同。</summary>
    public const int CountedFailureBackoffSeconds = 1800;

    /// <summary>验证熔断的时间窗口（分钟）：窗口内同一平台有足够多的不同绑定上报需要登录或需要人工验证时暂停平台。</summary>
    public const int VerificationBreakerWindowMinutes = 60;

    /// <summary>验证熔断的阈值：窗口内上报需要登录或需要人工验证的不同绑定数达到这个值时暂停平台。</summary>
    public const int VerificationBreakerBindingThreshold = 2;

    /// <summary>验证熔断写入 collector_app_platform_states.pause_reason 的取值。</summary>
    public const string VerificationBreakerPauseReason = "verification_breaker";

    /// <summary>绑定超过这么多分钟没有上报或领取，视为离线（contract.md 第 9 节）；用于判断平台是否还有可用的绑定。</summary>
    public const int BindingOfflineMinutes = 15;

    /// <summary>一次绑定上报最多包含的绑定数。</summary>
    public const int MaxReportedBindings = 200;

    /// <summary>每个节点可登记的不同设备数的默认上限（迁移 052 的列默认值）；系统管理员可按节点调整。</summary>
    public const int DefaultMaxDevicesPerNode = 4;

    /// <summary>每个节点设备上限可设置的最大值（迁移 052 的 CHECK：1–64）。</summary>
    public const int MaxDevicesPerNodeLimit = 64;

    /// <summary>绑定上报里因为超出节点设备上限而被拒绝的那一条的错误码；这样的绑定不入库。</summary>
    public const string DeviceLimitExceededCode = "device_limit_exceeded";

    /// <summary>一次租约从领取起最多可以续到多少秒的默认值（配置项 AppChannel:MaxLeaseTotalSeconds）。</summary>
    public const int DefaultMaxLeaseTotalSeconds = 1800;

    /// <summary>AppChannel:MaxLeaseTotalSeconds 可设置的最大值（一天）；最小值是单次租约的上限 <see cref="MaxLeaseSeconds"/>。</summary>
    public const int MaxLeaseTotalSecondsLimit = 86_400;

    /// <summary>
    /// 一次绑定上报之后这条绑定的停止原因（空值 = active）。新登记的绑定和还没有被运营批准的绑定一律是被运营停用，
    /// 上报的健康状态不起作用：采集节点上报的任何内容都不能让未批准的绑定生效。已批准的绑定按健康状态转换：
    /// 上报停止原因时取 <see cref="NextStopReason"/>，上报 ok 时只解除 <see cref="ClearsOnOkReport"/> 的原因。
    /// </summary>
    /// <param name="registered">这条绑定（节点、设备、平台）是否已经登记。</param>
    /// <param name="approved">已登记的绑定是否已被运营批准。</param>
    /// <param name="current">绑定当前的停止原因；未停止或未登记时为空。</param>
    /// <param name="reportedHealth">上报的停止原因；上报 ok 时为空。</param>
    public static AppBindingStopReason? StopReasonAfterReport(bool registered, bool approved, AppBindingStopReason? current,
        AppBindingStopReason? reportedHealth)
    {
        if (!registered || !approved) return AppBindingStopReason.DisabledByOperator;
        if (reportedHealth is { } reason) return NextStopReason(current, reason);
        return current is { } stopped && ClearsOnOkReport(stopped) ? null : current;
    }

    /// <summary>
    /// 节点上报的一条新绑定是否在设备上限之内：设备已经登记过（同一设备的另一个平台）总是允许；
    /// 新设备只在已登记的不同设备数还没有达到上限时允许。
    /// </summary>
    /// <param name="deviceRegistered">该节点是否已有这台设备的绑定。</param>
    /// <param name="registeredDevices">该节点已登记的不同设备数。</param>
    /// <param name="maxDevices">该节点的设备上限。</param>
    public static bool DeviceWithinLimit(bool deviceRegistered, int registeredDevices, int maxDevices)
        => deviceRegistered || registeredDevices < maxDevices;

    /// <summary>
    /// 在线上报解除旧设备/版本/界面契约停止；登录、验证、App 拒绝模拟器和运营停用仍需人工恢复。
    /// </summary>
    /// <param name="reason">绑定当前的停止原因。</param>
    public static bool ClearsOnOkReport(AppBindingStopReason reason)
        => reason is AppBindingStopReason.DeviceOffline or AppBindingStopReason.VersionMismatch or AppBindingStopReason.ContractUnverified
            or AppBindingStopReason.ContractChanged;

    /// <summary>
    /// 已停止的绑定又收到一个停止原因（健康上报或失败上报）时保留哪一个：被运营停用的不变；需要运营恢复的原因不被
    /// 可自动恢复的原因覆盖（否则之后的一次 ok 上报会把它放开）；其余情况取新的原因。
    /// </summary>
    /// <param name="current">绑定当前的停止原因；未停止时为空。</param>
    /// <param name="reported">新上报的停止原因；不能是被运营停用。</param>
    public static AppBindingStopReason NextStopReason(AppBindingStopReason? current, AppBindingStopReason reported)
    {
        if (current is not { } held) return reported;
        if (held == AppBindingStopReason.DisabledByOperator) return held;
        return !ClearsOnOkReport(held) && ClearsOnOkReport(reported) ? held : reported;
    }

    /// <summary>该失败码是否计入验证熔断：需要登录、需要人工验证。</summary>
    /// <param name="code">失败码。</param>
    public static bool CountsTowardVerificationBreaker(AppFailureCode code)
        => code is AppFailureCode.LoginRequired or AppFailureCode.ChallengeRequired;

    /// <summary>把剩余秒数向上取整并限制在 <see cref="RetryMinSeconds"/>–<see cref="RetryMaxSeconds"/>；空值或无效值取上限。</summary>
    /// <param name="seconds">距离阻塞条件解除的秒数。</param>
    public static int ClampRetry(double? seconds)
        => seconds is not { } value || double.IsNaN(value) || double.IsInfinity(value)
            ? RetryMaxSeconds
            : (int)Math.Min(RetryMaxSeconds, Math.Max(RetryMinSeconds, Math.Ceiling(value)));
}

/// <summary>令牌对应的 App 采集节点。</summary>
/// <param name="NodeId">节点标识。</param>
/// <param name="Status">节点状态；被停用的节点的全部请求被拒绝。</param>
public sealed record AppNodeIdentity(string NodeId, AppNodeStatus Status);

/// <summary>节点登记或更换令牌的结果。令牌明文只在这里出现一次，控制面只保存它的 SHA-256。</summary>
/// <param name="NodeId">节点标识。</param>
/// <param name="Token">节点令牌明文，放在请求头 Authorization: Bearer 里。</param>
public sealed record AppNodeCredential(string NodeId, string Token);

/// <summary>采集器上报的一条绑定（设备 × App × 账号）。</summary>
/// <param name="DeviceId">设备标识（AVD 名称），在节点内唯一，1–128 个字符。</param>
/// <param name="PlatformCode">平台代码。</param>
/// <param name="AccountLabel">账号代号，1–128 个字符，不含可识别的账号信息；同一平台内全局唯一。</param>
/// <param name="AppPackage">App 包名，1–255 个字符。</param>
/// <param name="ReviewedAppVersion">已审阅的 App 版本，最多 64 个字符。</param>
/// <param name="InstalledAppVersion">设备上已装的 App 版本，最多 64 个字符。</param>
/// <param name="AndroidVersion">Android 版本，最多 32 个字符。</param>
/// <param name="Health">ok，或除 disabled_by_operator 之外的停止原因。</param>
public sealed record AppBindingReport(string? DeviceId, string? PlatformCode, string? AccountLabel, string? AppPackage,
    string? ReviewedAppVersion, string? InstalledAppVersion, string? AndroidVersion, string? Health);

/// <summary>一个节点的绑定上报。</summary>
/// <param name="NodeId">令牌对应的节点。</param>
/// <param name="CollectorBuildRevision">采集器构建版本，1–128 个字符。</param>
/// <param name="Bindings">该节点的绑定；没有出现在这里的已登记绑定保持不变。</param>
public sealed record AppBindingReportCommand(string NodeId, string CollectorBuildRevision, IReadOnlyList<AppBindingReport> Bindings);

/// <summary>一条被接受的绑定在控制面里的状态。限速值和停用以控制面为准，采集器不能放宽。</summary>
/// <param name="DeviceId">请求里的设备标识（原样返回，用于对应）。</param>
/// <param name="PlatformCode">请求里的平台代码（原样返回，用于对应）。</param>
/// <param name="BindingId">控制面生成的绑定标识。</param>
/// <param name="Status">绑定状态。新登记的绑定在运营批准之前是 stopped。</param>
/// <param name="StopReason">停止原因；未停止时为空。待运营批准的绑定是 disabled_by_operator。</param>
/// <param name="CooldownUntil">仍在生效的冷却截止时间；不在冷却中时为空。</param>
/// <param name="MinIntervalSeconds">该绑定两次领取的最小间隔秒数。</param>
/// <param name="DailyQuota">运营设置的每日领取上限；未设置时为空。</param>
public sealed record AppBindingState(string DeviceId, string PlatformCode, Guid BindingId, AppBindingStatus Status,
    AppBindingStopReason? StopReason, DateTimeOffset? CooldownUntil, int MinIntervalSeconds, int? DailyQuota);

/// <summary>一条没有被接受的绑定；控制面没有因为它做任何修改（账号重复时另写一条审计事件）。</summary>
/// <param name="DeviceId">请求里的设备标识（原样返回，用于对应）。</param>
/// <param name="PlatformCode">请求里的平台代码（原样返回，用于对应）。</param>
/// <param name="Code">稳定错误码：INVALID_BINDING、INVALID_PLATFORM、INVALID_BINDING_HEALTH、APP_ACCOUNT_ALREADY_BOUND、BINDING_REPORT_CONFLICT，以及超出节点设备上限时的 device_limit_exceeded。</param>
/// <param name="Message">说明。</param>
public sealed record AppBindingRejection(string? DeviceId, string? PlatformCode, string Code, string Message);

/// <summary>绑定上报的结果：被接受的绑定与被拒绝的绑定分开列出，两者合起来与请求里的绑定一一对应。</summary>
/// <param name="Bindings">被接受的绑定的当前状态。</param>
/// <param name="Rejected">被拒绝的绑定及原因；没有时为空数组。</param>
public sealed record AppBindingReportResult(IReadOnlyList<AppBindingState> Bindings, IReadOnlyList<AppBindingRejection> Rejected);

/// <summary>App 绑定领取任务的请求。</summary>
/// <param name="NodeId">令牌对应的节点。</param>
/// <param name="BindingId">领取任务的绑定，必须属于该节点。</param>
/// <param name="LeaseSeconds">租约秒数（5–900）。</param>
public sealed record AppLeaseCommand(string NodeId, Guid BindingId, int LeaseSeconds);

/// <summary>领取到的 App 采集任务。</summary>
/// <param name="Id">采集任务标识。</param>
/// <param name="TenantId">租户标识。</param>
/// <param name="RunId">周期 Run 标识。</param>
/// <param name="CheckId">检测标识。</param>
/// <param name="SiteId">品牌工作区标识。</param>
/// <param name="PlatformCode">平台代码。</param>
/// <param name="PlatformEntryCode">平台入口代码，例如 doubao.consumer_app。</param>
/// <param name="PromptId">提问标识。</param>
/// <param name="QuestionText">提问原文，按原样发送。</param>
/// <param name="Region">采集区域，固定 cn。</param>
/// <param name="Language">回答语言，固定 zh-CN。</param>
/// <param name="Channel">采集渠道，固定 consumer_app。</param>
/// <param name="PromptRuleVersion">该提问通过的提问检查规则版本。</param>
/// <param name="DeadlineAt">所属周期的截止时点。</param>
/// <param name="LeaseVersion">租约版本。</param>
/// <param name="LeaseExpiresAt">租约截止时间。</param>
/// <param name="BindingId">持有租约的绑定。</param>
public sealed record AppLeasedTask(Guid Id, Guid TenantId, Guid RunId, Guid CheckId, Guid SiteId, string PlatformCode,
    string PlatformEntryCode, Guid PromptId, string QuestionText, string Region, string Language, string Channel,
    int PromptRuleVersion, DateTimeOffset DeadlineAt, int LeaseVersion, DateTimeOffset LeaseExpiresAt, Guid BindingId);

/// <summary>领取结果：领到任务时只有 <paramref name="Task"/>；没有领到时给出原因和建议的重试间隔。</summary>
/// <param name="Task">领到的任务；没有时为空。</param>
/// <param name="Reason">没有领到任务的原因；领到时为空。</param>
/// <param name="RetryAfterSeconds">建议多少秒后再试（30–600）；领到时为空。</param>
public sealed record AppLeaseResult(AppLeasedTask? Task, AppLeaseEmptyReason? Reason, int? RetryAfterSeconds);

/// <summary>App 任务的续租请求。</summary>
/// <param name="NodeId">令牌对应的节点。</param>
/// <param name="TaskId">采集任务标识。</param>
/// <param name="LeaseVersion">持有的租约版本。</param>
/// <param name="ExtendSeconds">从现在起延长的秒数（5–900）；实际截止时间不超过租约的总时长上限和任务的截止时点。</param>
public sealed record AppHeartbeatCommand(string NodeId, Guid TaskId, int LeaseVersion, int ExtendSeconds);

/// <summary>App 采集器上报的一次分类失败。probe 阶段不带任务；pre_send / post_send 必须带任务。</summary>
/// <param name="NodeId">令牌对应的节点。</param>
/// <param name="BindingId">发生失败的绑定，必须属于该节点。</param>
/// <param name="FailureCode">失败码。</param>
/// <param name="Phase">失败发生的阶段。</param>
/// <param name="Task">失败时持有的租约任务。</param>
/// <param name="SendState">任务尝试的发送状态：not_sent、sent 或 unknown；只用于设备冷却和审计，不决定任务去向。旧节点缺失时按 unknown 记录。</param>
/// <param name="SentAt">仅 sent 时填写，必须与已有发送事实一致。</param>
public sealed record AppFailureCommand(string NodeId, Guid BindingId, AppFailureCode FailureCode, CollectorPhase Phase,
    CollectorReportedTask? Task, string? SendState = null, DateTimeOffset? SentAt = null);

/// <summary>一次实际发题的不可变事实（按任务和租约版本幂等）。</summary>
public sealed record AppSentCommand(string NodeId, Guid BindingId, Guid TaskId, int LeaseVersion, DateTimeOffset SentAt);

/// <summary>失败上报的处理结果。</summary>
/// <param name="BindingStatus">处理后的绑定状态。</param>
/// <param name="StopReason">绑定的停止原因；未停止时为空。</param>
/// <param name="TaskDisposition">对上报任务的处理：重新排队（可由另一台设备领取）、终态（仅需要人工验证、App 拒绝模拟器、频率受限）、租约已不是当前租约；没有带任务时为空。</param>
/// <param name="PlatformPaused">处理后平台是否处于暂停。</param>
/// <param name="PlatformUnavailable">处理后平台的 App 入口是否不可用。</param>
/// <param name="CooldownUntil">处理后绑定仍在生效的冷却截止时间（频率受限后到 Asia/Shanghai 当日结束）；不在冷却中时为空。</param>
public sealed record AppFailureResult(AppBindingStatus BindingStatus, AppBindingStopReason? StopReason,
    CollectorTaskDisposition? TaskDisposition, bool PlatformPaused, bool PlatformUnavailable, DateTimeOffset? CooldownUntil = null);
