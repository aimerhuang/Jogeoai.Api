using System.Text.Json.Serialization;

namespace Jogeoai.Domain;

/// <summary>目录行与回答记录上的采集渠道。</summary>
public enum CollectionChannel
{
    /// <summary>API 渠道：Cloro 的海外引擎与国内官方接口。</summary>
    [JsonStringEnumMemberName("api")]
    Api,

    /// <summary>App 渠道：自研安卓模拟器采集器在手机 App 里提问。</summary>
    [JsonStringEnumMemberName("app")]
    App,

    /// <summary>自研国内网页采集；只出现在目录里，不参与定时监控。</summary>
    [JsonStringEnumMemberName("web")]
    Web,
}

/// <summary>App 采集节点（一台电脑上的采集器）的状态。</summary>
public enum AppNodeStatus
{
    /// <summary>可以上报绑定和领取任务。</summary>
    [JsonStringEnumMemberName("active")]
    Active,

    /// <summary>被运营停用，它的全部请求被拒绝。</summary>
    [JsonStringEnumMemberName("disabled")]
    Disabled,
}

/// <summary>App 绑定（设备 × App × 账号）的状态；冷却不是单独的状态，由冷却截止时间表示。</summary>
public enum AppBindingStatus
{
    /// <summary>可以领取任务。</summary>
    [JsonStringEnumMemberName("active")]
    Active,

    /// <summary>已停止，原因见 <see cref="AppBindingStopReason"/>；需要人工处理后恢复。</summary>
    [JsonStringEnumMemberName("stopped")]
    Stopped,
}

/// <summary>App 绑定停止的原因；也是采集器上报绑定健康时除 ok 以外的取值。</summary>
public enum AppBindingStopReason
{
    /// <summary>App 登录失效，需要授权人员在模拟器内重新登录。</summary>
    [JsonStringEnumMemberName("login_required")]
    LoginRequired,

    /// <summary>App 要求人工验证，需要授权人员在模拟器内处理。</summary>
    [JsonStringEnumMemberName("challenge_required")]
    ChallengeRequired,

    /// <summary>设备离线。</summary>
    [JsonStringEnumMemberName("device_offline")]
    DeviceOffline,

    /// <summary>App 界面与已审阅的界面契约不一致，等待人工复核。</summary>
    [JsonStringEnumMemberName("contract_changed")]
    ContractChanged,

    /// <summary>界面契约尚未审阅。</summary>
    [JsonStringEnumMemberName("contract_unverified")]
    ContractUnverified,

    /// <summary>App 已装版本与已审阅版本不一致。</summary>
    [JsonStringEnumMemberName("version_mismatch")]
    VersionMismatch,

    /// <summary>App 拒绝在模拟器上运行或要求设备验证；不做任何规避。</summary>
    [JsonStringEnumMemberName("emulator_rejected")]
    EmulatorRejected,

    /// <summary>被运营人员停用。</summary>
    [JsonStringEnumMemberName("disabled_by_operator")]
    DisabledByOperator,
}

/// <summary>App 领取接口没有返回任务时的原因。</summary>
public enum AppLeaseEmptyReason
{
    /// <summary>该平台当前没有可领取的任务。</summary>
    [JsonStringEnumMemberName("no_task")]
    NoTask,

    /// <summary>绑定已停止。</summary>
    [JsonStringEnumMemberName("binding_stopped")]
    BindingStopped,

    /// <summary>绑定在频率受限后的冷却期内。</summary>
    [JsonStringEnumMemberName("binding_cooldown")]
    BindingCooldown,

    /// <summary>绑定自身的领取间隔未到（默认 100 秒，迁移 050）。</summary>
    [JsonStringEnumMemberName("binding_interval")]
    BindingInterval,

    /// <summary>绑定最近 24 小时的领取次数已达运营设置的每日上限（默认不设）。</summary>
    [JsonStringEnumMemberName("binding_quota")]
    BindingQuota,

    /// <summary>同一台设备上已有未过期的租约：一台设备同一时刻只执行一个任务。</summary>
    [JsonStringEnumMemberName("device_busy")]
    DeviceBusy,

    /// <summary>目标虚机当前未在线。</summary>
    [JsonStringEnumMemberName("device_offline")]
    DeviceOffline,

    /// <summary>目标虚机的上一次实际发送尚不满 100 秒。</summary>
    [JsonStringEnumMemberName("device_cooldown")]
    DeviceCooldown,

    /// <summary>平台合计的领取间隔未满足。</summary>
    [JsonStringEnumMemberName("platform_interval")]
    PlatformInterval,

    /// <summary>平台当日的领取次数已达运营设置的上限。</summary>
    [JsonStringEnumMemberName("platform_daily_cap")]
    PlatformDailyCap,

    /// <summary>平台被运营暂停或熔断。</summary>
    [JsonStringEnumMemberName("platform_paused")]
    PlatformPaused,

    /// <summary>平台的 App 入口已停用（判定为不可用）。</summary>
    [JsonStringEnumMemberName("platform_unavailable")]
    PlatformUnavailable,

    /// <summary>此刻有任务只因为所属租户的可用积分不够一次检测（10 积分）而没有被领取，并且没有别的任务可以领取。</summary>
    [JsonStringEnumMemberName("credit_insufficient")]
    CreditInsufficient,
}

/// <summary>
/// App 采集器可上报的分类失败码，写入 collector_attempts.error_code。失败发生的阶段沿用 <see cref="CollectorPhase"/>
/// （probe / pre_send / post_send），对任务的处理结果沿用 <see cref="CollectorTaskDisposition"/>。
/// </summary>
public enum AppFailureCode
{
    /// <summary>需要登录：绑定停止，等人处理；任务重新排队。</summary>
    [JsonStringEnumMemberName("login_required")]
    LoginRequired,

    /// <summary>需要人工验证：绑定停止，等人处理；任务重新排队。一小时内同一平台两个不同绑定出现验证时暂停平台。</summary>
    [JsonStringEnumMemberName("challenge_required")]
    ChallengeRequired,

    /// <summary>出现频率限制提示：绑定冷却到当日结束；任务重新排队。</summary>
    [JsonStringEnumMemberName("rate_limited")]
    RateLimited,

    /// <summary>
    /// App 拒绝模拟器或要求设备验证：绑定停止；任务重新排队。该平台所有没有被运营停用的绑定都因它停止时，
    /// 平台的 App 入口才置为不可用（只有一个这样的绑定时，一次上报即满足）。
    /// </summary>
    [JsonStringEnumMemberName("emulator_rejected")]
    EmulatorRejected,

    /// <summary>界面契约变化：绑定停止，等待人工复核；任务重新排队。</summary>
    [JsonStringEnumMemberName("contract_changed")]
    ContractChanged,

    /// <summary>界面契约未审阅：绑定停止；任务重新排队。</summary>
    [JsonStringEnumMemberName("contract_unverified")]
    ContractUnverified,

    /// <summary>App 已装版本与已审阅版本不一致：绑定停止；任务重新排队。</summary>
    [JsonStringEnumMemberName("version_mismatch")]
    VersionMismatch,

    /// <summary>设备离线：绑定停止（停止原因 device_offline，之后绑定上报 ok 即自动恢复）；任务重新排队，不计失败次数。</summary>
    [JsonStringEnumMemberName("device_offline")]
    DeviceOffline,

    /// <summary>App 崩溃：绑定状态不变；任务重新排队，累计 3 次后终态。</summary>
    [JsonStringEnumMemberName("app_crashed")]
    AppCrashed,

    /// <summary>采集超时：绑定状态不变；任务重新排队，累计 3 次后终态。</summary>
    [JsonStringEnumMemberName("collection_timeout")]
    CollectionTimeout,
}

/// <summary>App 回答的引用来源读取状态。</summary>
public enum AppCitationsStatus
{
    /// <summary>读到了引用，引用列表非空。</summary>
    [JsonStringEnumMemberName("read")]
    Read,

    /// <summary>界面没有展示引用。</summary>
    [JsonStringEnumMemberName("absent")]
    Absent,

    /// <summary>界面展示了引用但读取失败，引用列表留空，原因见 citationsIssue。</summary>
    [JsonStringEnumMemberName("unreadable")]
    Unreadable,

    /// <summary>界面契约没有声明引用区域。</summary>
    [JsonStringEnumMemberName("not_declared")]
    NotDeclared,
}

/// <summary>App 界面上可见的开关状态（联网搜索、深度思考）；采集器只读取，不操作。</summary>
public enum AppVisibleState
{
    /// <summary>界面显示为开启。</summary>
    [JsonStringEnumMemberName("on")]
    On,

    /// <summary>界面显示为关闭。</summary>
    [JsonStringEnumMemberName("off")]
    Off,

    /// <summary>界面上看不出状态。</summary>
    [JsonStringEnumMemberName("unknown")]
    Unknown,
}

/// <summary>每日告警汇报的发送状态。</summary>
public enum AppDailyReportDeliveryStatus
{
    /// <summary>未配置发送渠道：只生成并存档（首期的唯一取值）。</summary>
    [JsonStringEnumMemberName("not_configured")]
    NotConfigured,

    /// <summary>已发送。</summary>
    [JsonStringEnumMemberName("sent")]
    Sent,

    /// <summary>发送失败；不影响存档。</summary>
    [JsonStringEnumMemberName("failed")]
    Failed,
}
