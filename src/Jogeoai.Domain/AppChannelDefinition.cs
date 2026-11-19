namespace Jogeoai.Domain;

/// <summary>App 渠道已知的一个平台（每个平台只有一个 App 目标）。</summary>
/// <param name="Code">平台代码（小写），同时是 App 目标的引擎标识与 collector_platforms.code。</param>
/// <param name="DisplayName">面向用户的平台显示名。</param>
public sealed record AppChannelPlatform(string Code, string DisplayName);

/// <summary>
/// App 采集渠道的静态定义：由自研安卓模拟器采集器在手机 App 里提问得到的回答。
/// 它与 <see cref="AnswerProviderDefinition"/> 并列，<b>不加入</b> <see cref="AnswerProviderDefinition.All"/>：
/// App 目标不进入 API 的 Run、不占每轮 20 个目标的名额、不参与立即采集和手动采集，只由每周的 App 计划展开。
/// 平台是否可用（入口是否启用、不可用原因）来自数据库的 consumer_app 入口，不在这里。
/// </summary>
public static class AppChannelDefinition
{
    /// <summary>App 目标的 Provider 标识，写入 site_targets / run_engines / visibility_checks 的 provider 列。</summary>
    public const string Provider = "app_collector";

    /// <summary>App 渠道面向用户的显示名（错误信息中代替 Provider 标识）。</summary>
    public const string DisplayName = "App 采集";

    /// <summary>手动采集、立即采集、采集预览中显式指定 App 目标时返回的错误码（400）：App 目标只由每周计划展开。</summary>
    public const string TargetNotAllowedCode = "APP_TARGET_NOT_ALLOWED";

    /// <summary><see cref="TargetNotAllowedCode"/> 的说明文字。</summary>
    public const string TargetNotAllowedMessage = "App 目标只按每周计划采集，不支持手动采集、立即采集或采集预览；请从所选目标中移除 App 目标。";

    /// <summary>App 目标、Run 目标与回答三处统一使用的采集模式。</summary>
    public const string CaptureMode = "app";

    /// <summary>证据类别：观察到的 App 界面。</summary>
    public const string EvidenceClass = "observed_surface";

    /// <summary>App 目标固定的两位 ISO 国家代码；品牌的地区不同不改变它。</summary>
    public const string Country = "CN";

    /// <summary>App 目标固定的 BCP-47 区域语言；品牌的语言不同不改变它。</summary>
    public const string Locale = "zh-CN";

    /// <summary>
    /// App 目标唯一支持、也是默认的回答模式。这是归类位置（配置矩阵里的“快速回答”列），不表示采集器会在 App 里切换模式；
    /// 目标键不带模式段。
    /// </summary>
    public const AnswerMode DefaultAnswerMode = AnswerMode.Fast;

    /// <summary>App 目标唯一接受的采集频率（首期固定 1次/周）。</summary>
    public const MonitoringFrequency Frequency = MonitoringFrequency.Weekly;

    /// <summary>Collector 一侧（collector_platform_entries / collector_tasks 的 channel 列与内部协议）的渠道值，与 <see cref="CaptureMode"/> 固定一一对应。</summary>
    public const string CollectorChannel = "consumer_app";

    /// <summary>
    /// App 渠道已知的平台，按配置矩阵的展示顺序排列。文心一言（ernie）是已知平台，但其 App 的聊天内容读不到，
    /// 入口种子即为停用；可用性以数据库为准，不由本列表决定。
    /// </summary>
    public static IReadOnlyList<AppChannelPlatform> Platforms { get; } =
    [
        new("doubao", "豆包"),
        new("deepseek", "DeepSeek"),
        new("tencent_yuanbao", "腾讯元宝"),
        new("qwen", "千问"),
        new("kimi", "Kimi"),
        new("zhipu_qingyan", "智谱清言"),
        new("ernie", "文心一言"),
    ];

    /// <summary>App 渠道已知的平台代码（即 App 目标的引擎标识），顺序同 <see cref="Platforms"/>。</summary>
    public static IReadOnlyList<string> Engines { get; } = Platforms.Select(platform => platform.Code).ToArray();

    /// <summary>App 目标支持的回答模式：只有快速回答。</summary>
    public static IReadOnlyList<AnswerMode> SupportedAnswerModes { get; } = [DefaultAnswerMode];

    /// <summary>按平台代码查找平台（忽略首尾空白与大小写）；不是已知平台时返回空值。</summary>
    /// <param name="engine">平台代码（引擎标识）。</param>
    public static AppChannelPlatform? TryGetPlatform(string? engine)
    {
        var normalized = engine?.Trim();
        if (string.IsNullOrEmpty(normalized)) return null;
        foreach (var platform in Platforms)
            if (string.Equals(platform.Code, normalized, StringComparison.OrdinalIgnoreCase)) return platform;
        return null;
    }

    /// <summary>引擎标识是否为 App 渠道已知的平台（忽略首尾空白与大小写）；只表示“认识这个平台”，不表示它当前可用。</summary>
    /// <param name="engine">平台代码（引擎标识）。</param>
    public static bool SupportsEngine(string? engine) => TryGetPlatform(engine) is not null;

    /// <summary>App 目标是否支持指定回答模式（只支持快速回答）。</summary>
    /// <param name="mode">回答模式。</param>
    public static bool SupportsAnswerMode(AnswerMode mode) => mode == DefaultAnswerMode;

    /// <summary>App 目标是否接受指定采集频率（只接受 1次/周）。</summary>
    /// <param name="frequency">采集频率。</param>
    public static bool SupportsFrequency(MonitoringFrequency frequency) => frequency == Frequency;

    /// <summary>平台的 App 入口代码 <c>&lt;platform&gt;.consumer_app</c>（collector_platform_entries.entry_code）。</summary>
    /// <param name="platform">平台代码（忽略首尾空白与大小写）。</param>
    /// <exception cref="ArgumentException">不是 App 渠道已知的平台。</exception>
    public static string EntryCode(string platform)
    {
        var known = TryGetPlatform(platform)
            ?? throw new ArgumentException($"未知的 App 平台：{platform}", nameof(platform));
        return $"{known.Code}.{CollectorChannel}";
    }
}
