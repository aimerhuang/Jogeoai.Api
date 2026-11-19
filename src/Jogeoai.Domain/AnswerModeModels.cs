using System.Text.Json.Serialization;

namespace Jogeoai.Domain;

/// <summary>回答模式：模型以快速回答还是深度思考方式作答。每个 Provider 支持的模式由 <see cref="AnswerProviderDefinition.SupportedAnswerModes"/> 声明。</summary>
public enum AnswerMode
{
    /// <summary>快速回答：关闭或压低思考，直接作答。</summary>
    [JsonStringEnumMemberName("fast")]
    Fast,

    /// <summary>深度思考：开启模型思考后作答。</summary>
    [JsonStringEnumMemberName("deep")]
    Deep,
}

/// <summary>每日监控单元格（品牌 × 模型 × 回答模式）的采集频率；时刻按品牌（团队）时区计算。</summary>
public enum MonitoringFrequency
{
    /// <summary>1次/天（默认）：每天早上 09:01 一轮。</summary>
    [JsonStringEnumMemberName("daily")]
    Daily,

    /// <summary>2次/天：每天凌晨 00:05 和早上 09:01 各一轮。</summary>
    [JsonStringEnumMemberName("twice_daily")]
    TwiceDaily,

    /// <summary>每2天1次：从锚点日期起每隔一天的 09:01 一轮。</summary>
    [JsonStringEnumMemberName("every_other_day")]
    EveryOtherDay,

    /// <summary>1次/周：每周与锚点日期同一星期几的 09:01 一轮。</summary>
    [JsonStringEnumMemberName("weekly")]
    Weekly,
}

/// <summary>每日监控时隙的种类。</summary>
public enum MonitoringSlotKind
{
    /// <summary>主计划时隙：本地 09:01，包含所有到期的单元格。</summary>
    [JsonStringEnumMemberName("main")]
    Main,

    /// <summary>凌晨计划时隙：本地 00:05，只包含 2次/天 的单元格。</summary>
    [JsonStringEnumMemberName("night")]
    Night,

    /// <summary>App 计划时隙：本地 09:11，只包含到期的 App 目标（1次/周，按锚点的星期几）。</summary>
    [JsonStringEnumMemberName("app")]
    App,
}

/// <summary>回答模式的存储字符串、解析与目标规范化规则；失败抛出 400 级 <see cref="CollectorStoreException"/>。</summary>
public static class AnswerModeRules
{
    /// <summary>Provider 不支持所选回答模式，或模式取值无效。</summary>
    public const string AnswerModeUnsupported = "ANSWER_MODE_UNSUPPORTED";

    /// <summary>快速回答的存储 / 传输值。</summary>
    public const string Fast = "fast";
    /// <summary>深度思考的存储 / 传输值。</summary>
    public const string Deep = "deep";

    /// <summary>把回答模式转换为稳定的小写存储字符串（fast / deep）。</summary>
    /// <param name="mode">回答模式。</param>
    public static string ToStorage(AnswerMode mode) => mode switch
    {
        AnswerMode.Fast => Fast,
        AnswerMode.Deep => Deep,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    /// <summary>解析存储字符串（忽略首尾空白与大小写）；空值或未知取值返回 false。</summary>
    /// <param name="value">存储字符串。</param>
    /// <param name="mode">解析结果。</param>
    public static bool TryParse(string? value, out AnswerMode mode)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case Fast: mode = AnswerMode.Fast; return true;
            case Deep: mode = AnswerMode.Deep; return true;
            default: mode = default; return false;
        }
    }

    /// <summary>把可空的模式字符串解析为实际模式：空值取 Provider 默认模式；无效或不受支持时返回 false。</summary>
    /// <param name="definition">Provider 定义。</param>
    /// <param name="value">目标或检测上的模式字符串，空值表示默认模式。</param>
    /// <param name="mode">实际模式。</param>
    public static bool TryResolve(AnswerProviderDefinition definition, string? value, out AnswerMode mode)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(value)) { mode = definition.DefaultAnswerMode; return true; }
        return TryParse(value, out mode) && definition.SupportsAnswerMode(mode);
    }

    /// <summary>
    /// 规范化目标的回答模式：与 Provider 默认模式相同（忽略首尾空白与大小写）或为空时折叠为 null，保证目标键与哈希不变；
    /// 其他受支持的模式写为小写存储值。已注册 Provider 的模式无效或不受支持时返回 false。
    /// 未注册 Provider 没有能力表：非空模式只做小写化并保留（目标键会追加模式段）。
    /// App 目标（<see cref="AppChannelDefinition.Provider"/>）只有快速回答：fast 折叠为 null，其他取值返回 false。
    /// </summary>
    /// <param name="target">待规范化的目标。</param>
    /// <param name="normalized">规范化后的目标；失败时为原目标。</param>
    public static bool TryNormalize(CloroVisibilityTarget target, out CloroVisibilityTarget normalized)
    {
        ArgumentNullException.ThrowIfNull(target);
        normalized = target;
        if (string.IsNullOrWhiteSpace(target.AnswerMode))
        {
            normalized = target.AnswerMode is null ? target : target with { AnswerMode = null };
            return true;
        }
        // App targets only have the fast position and their key carries no mode segment: fast folds to null, anything else fails.
        if (MonitoringProviders.IsApp(target.Provider))
        {
            if (!TryParse(target.AnswerMode, out var appMode) || !AppChannelDefinition.SupportsAnswerMode(appMode)) return false;
            normalized = target with { AnswerMode = null };
            return true;
        }
        var definition = AnswerProviderDefinition.TryGet(target.Provider);
        if (definition is null)
        {
            normalized = target with { AnswerMode = target.AnswerMode.Trim().ToLowerInvariant() };
            return true;
        }
        if (!TryParse(target.AnswerMode, out var mode) || !definition.SupportsAnswerMode(mode)) return false;
        normalized = target with { AnswerMode = mode == definition.DefaultAnswerMode ? null : ToStorage(mode) };
        return true;
    }

    /// <summary>规范化目标的回答模式（规则同 <see cref="TryNormalize"/>）；不受支持时抛出 400 ANSWER_MODE_UNSUPPORTED。</summary>
    /// <param name="target">待规范化的目标。</param>
    public static CloroVisibilityTarget Normalize(CloroVisibilityTarget target)
        => TryNormalize(target, out var normalized) ? normalized : throw Unsupported(target.Provider, target.AnswerMode);

    /// <summary>构造 400 ANSWER_MODE_UNSUPPORTED 异常。</summary>
    /// <param name="provider">Provider 标识。</param>
    /// <param name="mode">请求的模式。</param>
    public static CollectorStoreException Unsupported(string? provider, string? mode)
    {
        var name = AnswerProviderDefinition.TryGet(provider)?.DisplayName
            ?? (MonitoringProviders.IsApp(provider) ? AppChannelDefinition.DisplayName : provider);
        return new(AnswerModeUnsupported, $"{name} 不支持回答模式 {mode?.Trim()}。", 400);
    }
}

/// <summary>采集频率的存储字符串、解析、每周次数与到期判定；时隙展开、预估与测试共用。</summary>
public static class MonitoringFrequencyRules
{
    /// <summary>采集频率取值无效。</summary>
    public const string FrequencyInvalid = "FREQUENCY_INVALID";

    /// <summary>全部频率，按界面展示顺序排列。</summary>
    public static IReadOnlyList<MonitoringFrequency> All { get; } =
        [MonitoringFrequency.Daily, MonitoringFrequency.TwiceDaily, MonitoringFrequency.EveryOtherDay, MonitoringFrequency.Weekly];

    /// <summary>把频率转换为稳定的小写存储字符串（twice_daily / daily / every_other_day / weekly）。</summary>
    /// <param name="frequency">采集频率。</param>
    public static string ToStorage(MonitoringFrequency frequency) => frequency switch
    {
        MonitoringFrequency.TwiceDaily => "twice_daily",
        MonitoringFrequency.Daily => "daily",
        MonitoringFrequency.EveryOtherDay => "every_other_day",
        MonitoringFrequency.Weekly => "weekly",
        _ => throw new ArgumentOutOfRangeException(nameof(frequency), frequency, null),
    };

    /// <summary>解析存储字符串（忽略首尾空白与大小写）；空值或未知取值返回 false。</summary>
    /// <param name="value">存储字符串。</param>
    /// <param name="frequency">解析结果。</param>
    public static bool TryParse(string? value, out MonitoringFrequency frequency)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "twice_daily": frequency = MonitoringFrequency.TwiceDaily; return true;
            case "daily": frequency = MonitoringFrequency.Daily; return true;
            case "every_other_day": frequency = MonitoringFrequency.EveryOtherDay; return true;
            case "weekly": frequency = MonitoringFrequency.Weekly; return true;
            default: frequency = default; return false;
        }
    }

    /// <summary>每周采集次数（用于预估）：2次/天 14、1次/天 7、每2天1次 3.5、1次/周 1。</summary>
    /// <param name="frequency">采集频率。</param>
    public static decimal WeeklyRuns(MonitoringFrequency frequency) => frequency switch
    {
        MonitoringFrequency.TwiceDaily => 14m,
        MonitoringFrequency.Daily => 7m,
        MonitoringFrequency.EveryOtherDay => 3.5m,
        MonitoringFrequency.Weekly => 1m,
        _ => throw new ArgumentOutOfRangeException(nameof(frequency), frequency, null),
    };

    /// <summary>
    /// 判断单元格是否在某个时隙到期。凌晨时隙（00:05）只包含 2次/天；主时隙（09:01）包含 2次/天与 1次/天，
    /// 每2天1次在 (时隙日期 − 锚点).Days 为偶数时到期，1次/周在与锚点同一星期几时到期；两者在锚点之前的日期都不到期。
    /// 锚点为空时每2天1次与1次/周按每天处理（存储层启用或改为这两种频率时总会写入锚点，空值只出现在历史数据或预估草稿中）。
    /// 1次/天与2次/天不看锚点。
    /// App 时隙（09:11）只包含 1次/周：时隙日期不早于锚点且与锚点同一星期几时到期；锚点为空时<b>不到期</b>
    /// （App 目标启用时总会写入锚点，这里不像主时隙那样把空锚点当作每天），其他频率在 App 时隙永不到期。
    /// </summary>
    /// <param name="frequency">采集频率。</param>
    /// <param name="anchor">锚点日期（品牌时区的本地日期）。</param>
    /// <param name="slotLocalDate">时隙在品牌时区的本地日期。</param>
    /// <param name="kind">时隙种类。</param>
    public static bool IsDue(MonitoringFrequency frequency, DateOnly? anchor, DateOnly slotLocalDate, MonitoringSlotKind kind)
    {
        _ = WeeklyRuns(frequency); // rejects undefined values
        if (kind == MonitoringSlotKind.Night) return frequency == MonitoringFrequency.TwiceDaily;
        if (kind == MonitoringSlotKind.App)
            return frequency == MonitoringFrequency.Weekly && anchor is { } appStart
                && slotLocalDate >= appStart && slotLocalDate.DayOfWeek == appStart.DayOfWeek;
        if (kind != MonitoringSlotKind.Main) throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        if (frequency is MonitoringFrequency.TwiceDaily or MonitoringFrequency.Daily) return true;
        if (anchor is not { } start) return true;
        if (slotLocalDate < start) return false;
        return frequency == MonitoringFrequency.EveryOtherDay
            ? (slotLocalDate.DayNumber - start.DayNumber) % 2 == 0
            : slotLocalDate.DayOfWeek == start.DayOfWeek;
    }
}

/// <summary>
/// 按回答模式覆盖 Provider 全局选项中的思考参数（只改 Thinking / ReasoningEffort，requestMode、sources、maxToolCalls 仍取全局配置）。
/// 默认模式的结果必须与迁移前全局配置下发的选项一致：
/// deepseek_api / packy_qwen / packy_kimi：fast → thinking=disabled，deep → thinking=enabled；
/// packy_glm：fast → thinking=disabled + reasoningEffort=low（不支持 deep）；
/// doubao_api：messages 模式 fast → thinking=disabled、deep → thinking 为空（模型默认开启）；非 messages 模式只支持 deep（原样返回），fast 视为不支持；
/// packy_yuanbao：只支持 deep，原样返回；cloro：只支持 fast，原样返回。
/// </summary>
public static class AnswerModeProfile
{
    /// <summary>按模式覆盖思考参数；Provider 不支持该模式时抛出 400 ANSWER_MODE_UNSUPPORTED。</summary>
    /// <param name="definition">Provider 定义。</param>
    /// <param name="options">Provider 全局选项。</param>
    /// <param name="mode">回答模式。</param>
    public static AnswerProviderOptions Apply(AnswerProviderDefinition definition, AnswerProviderOptions options, AnswerMode mode)
        => TryApply(definition, options, mode, out var applied)
            ? applied
            : throw AnswerModeRules.Unsupported(definition.Provider, AnswerModeRules.ToStorage(mode));

    /// <summary>按模式字符串覆盖思考参数；空值取 Provider 默认模式，无效或不受支持时抛出 400 ANSWER_MODE_UNSUPPORTED。</summary>
    /// <param name="definition">Provider 定义。</param>
    /// <param name="options">Provider 全局选项。</param>
    /// <param name="mode">模式存储字符串，空值表示默认模式。</param>
    public static AnswerProviderOptions Apply(AnswerProviderDefinition definition, AnswerProviderOptions options, string? mode)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return AnswerModeRules.TryResolve(definition, mode, out var resolved)
            ? Apply(definition, options, resolved)
            : throw AnswerModeRules.Unsupported(definition.Provider, mode);
    }

    /// <summary>按模式覆盖思考参数；不支持时返回 false 且 <paramref name="applied"/> 为原选项。</summary>
    /// <param name="definition">Provider 定义。</param>
    /// <param name="options">Provider 全局选项。</param>
    /// <param name="mode">回答模式。</param>
    /// <param name="applied">覆盖后的选项。</param>
    public static bool TryApply(AnswerProviderDefinition definition, AnswerProviderOptions options, AnswerMode mode,
        out AnswerProviderOptions applied)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(options);
        applied = options;
        if (!definition.SupportsAnswerMode(mode)) return false;
        var provider = definition.Provider;
        if (provider == AnswerProviderDefinition.DeepSeekApi.Provider
            || provider == AnswerProviderDefinition.PackyQwen.Provider
            || provider == AnswerProviderDefinition.PackyKimi.Provider)
        {
            applied = options with
            {
                Thinking = mode == AnswerMode.Deep ? AnswerProviderOptions.ThinkingEnabled : AnswerProviderOptions.ThinkingDisabled,
            };
            return true;
        }
        if (provider == AnswerProviderDefinition.PackyGlm.Provider)
        {
            // glm-5.3-flash ignores thinking=disabled, so "fast" is pinned by the low reasoning effort as well.
            applied = options with { Thinking = AnswerProviderOptions.ThinkingDisabled, ReasoningEffort = "low" };
            return true;
        }
        if (provider == AnswerProviderDefinition.DoubaoApi.Provider)
        {
            if (!string.Equals(options.RequestMode?.Trim(), AnswerProviderOptions.MessagesMode, StringComparison.OrdinalIgnoreCase))
            {
                // web_search / doubao_app have no verified way to switch thinking off; only the existing (deep) behaviour is allowed.
                return mode == AnswerMode.Deep;
            }
            // Deep omits thinking so the model default (on) applies, byte-identical to the pre-mode messages request.
            applied = options with { Thinking = mode == AnswerMode.Fast ? AnswerProviderOptions.ThinkingDisabled : null };
            return true;
        }
        // cloro (fast only) and packy_yuanbao (deep only) have no thinking parameter to override.
        return true;
    }
}
