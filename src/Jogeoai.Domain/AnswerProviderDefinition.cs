namespace Jogeoai.Domain;

/// <summary>API 回答 Provider 的静态定义：身份、证据口径、解析器版本、用量流水类型与支持的引擎。</summary>
/// <param name="Provider">Provider 标识，写入 run_engines / visibility_checks / provider_tasks 的 provider 列。</param>
/// <param name="CaptureMode">采集模式：scraped 表示消费者页面抓取，api 表示官方接口调用。</param>
/// <param name="EvidenceClass">证据类别：observed_surface 表示观察到的页面，api_response 表示接口响应。</param>
/// <param name="ParserVersion">适配器解析器版本，写入 visibility_checks.parser_version。</param>
/// <param name="UsageEntryType">usage_ledger.entry_type 的取值。</param>
/// <param name="UsageReferencePrefix">usage_ledger.reference 的前缀，后接 Provider Task 标识。</param>
/// <param name="DefaultCountry">目标未指定国家时的两位 ISO 国家代码。</param>
/// <param name="DefaultLocale">目标默认的 BCP-47 区域语言。</param>
/// <param name="Engines">该 Provider 支持的引擎代码（小写）。</param>
public sealed record AnswerProviderDefinition(
    string Provider,
    string CaptureMode,
    string EvidenceClass,
    string ParserVersion,
    string UsageEntryType,
    string UsageReferencePrefix,
    string DefaultCountry,
    string DefaultLocale,
    IReadOnlyList<string> Engines)
{
    /// <summary>面向用户的中文显示名，用于错误文案（如“豆包目标仅支持 CN/zh-CN。”）。</summary>
    public string DisplayName { get; init; } = Provider;

    /// <summary>是否把目标固定在 <see cref="DefaultCountry"/> / <see cref="DefaultLocale"/>；为 true 时其他国家或语言的目标被拒绝。</summary>
    public bool PinnedSurface { get; init; }

    /// <summary>依赖 answer_provider_settings 的 Provider 在未配置时返回的错误码（手动 Run 503）；为空表示沿用 Cloro 的设置与 key 池规则。</summary>
    public string? NotConfiguredCode { get; init; }

    /// <summary>该 Provider 支持的回答模式（快速回答 / 深度思考），按界面列顺序排列；由真实调用实测确定。</summary>
    public IReadOnlyList<AnswerMode> SupportedAnswerModes { get; init; } = [AnswerMode.Fast];

    /// <summary>
    /// 默认回答模式，等于引入回答模式前该 Provider 的实际采集行为。<b>发布后绝不能修改</b>：默认模式目标的目标键省略模式段，
    /// 与历史键逐字节一致；改动默认值会让已有目标键、提问平台范围、计划快照和幂等键的含义漂移。新增模式只扩展 <see cref="SupportedAnswerModes"/>。
    /// </summary>
    public AnswerMode DefaultAnswerMode { get; init; } = AnswerMode.Fast;

    /// <summary>Cloro：海外回答引擎的页面抓取证据，键值与历史 site_targets.target_key 保持一致。</summary>
    public static AnswerProviderDefinition Cloro { get; } = new(
        "cloro", "scraped", "observed_surface", "cloro-v2", "cloro_call", "cloro:", "US", "en-US",
        ["chatgpt", "perplexity", "copilot", "gemini", "google-ai-mode", "google-ai-overview"])
    {
        DisplayName = "Cloro",
        SupportedAnswerModes = [AnswerMode.Fast],
        DefaultAnswerMode = AnswerMode.Fast,
    };

    /// <summary>豆包：火山方舟 Responses API + 豆包搜索的官方接口证据，固定 CN / zh-CN。</summary>
    public static AnswerProviderDefinition DoubaoApi { get; } = new(
        "doubao_api", "api", "api_response", "doubao-api-v1", "doubao_api_call", "doubao:", "CN", "zh-CN",
        ["doubao"])
    {
        DisplayName = "豆包",
        PinnedSurface = true,
        NotConfiguredCode = DoubaoErrorCodes.NotConfigured,
        SupportedAnswerModes = [AnswerMode.Fast, AnswerMode.Deep],
        DefaultAnswerMode = AnswerMode.Deep,
    };

    /// <summary>DeepSeek：官方 Anthropic 兼容 Messages API + 服务端 web_search 的接口证据，固定 CN / zh-CN。</summary>
    public static AnswerProviderDefinition DeepSeekApi { get; } = new(
        "deepseek_api", "api", "api_response", "deepseek-api-v1", "deepseek_api_call", "deepseek:", "CN", "zh-CN",
        ["deepseek"])
    {
        DisplayName = "DeepSeek",
        PinnedSurface = true,
        NotConfiguredCode = DeepSeekErrorCodes.NotConfigured,
        SupportedAnswerModes = [AnswerMode.Fast, AnswerMode.Deep],
        DefaultAnswerMode = AnswerMode.Fast,
    };

    /// <summary>千问：PackyAPI（bailian 分组）OpenAI Responses + web_search 的接口证据，固定 CN / zh-CN；来源为全部检索结果（只有 URL）。</summary>
    public static AnswerProviderDefinition PackyQwen { get; } = new(
        "packy_qwen", "api", "api_response", "packy-responses-v1", "packy_qwen_call", "packy_qwen:", "CN", "zh-CN",
        ["qwen"])
    {
        DisplayName = "千问（PackyAPI）",
        PinnedSurface = true,
        NotConfiguredCode = PackyErrorCodes.NotConfigured,
        SupportedAnswerModes = [AnswerMode.Fast, AnswerMode.Deep],
        DefaultAnswerMode = AnswerMode.Fast,
    };

    /// <summary>智谱清言：PackyAPI（zai-officially 分组）OpenAI Responses + web_search 的接口证据，固定 CN / zh-CN；来源为全部检索结果（只有 URL）。</summary>
    public static AnswerProviderDefinition PackyGlm { get; } = new(
        "packy_glm", "api", "api_response", "packy-responses-v1", "packy_glm_call", "packy_glm:", "CN", "zh-CN",
        ["zhipu_qingyan"])
    {
        DisplayName = "智谱清言（PackyAPI）",
        PinnedSurface = true,
        NotConfiguredCode = PackyErrorCodes.NotConfigured,
        SupportedAnswerModes = [AnswerMode.Fast],
        DefaultAnswerMode = AnswerMode.Fast,
    };

    /// <summary>Kimi：PackyAPI（kimi-officially 分组）Anthropic Messages + web_search_20250305（max_uses=1）的接口证据，固定 CN / zh-CN；来源为一轮搜索的检索结果（带标题）。</summary>
    public static AnswerProviderDefinition PackyKimi { get; } = new(
        "packy_kimi", "api", "api_response", "packy-anthropic-v1", "packy_kimi_call", "packy_kimi:", "CN", "zh-CN",
        ["kimi"])
    {
        DisplayName = "Kimi（PackyAPI）",
        PinnedSurface = true,
        NotConfiguredCode = PackyErrorCodes.NotConfigured,
        SupportedAnswerModes = [AnswerMode.Fast, AnswerMode.Deep],
        DefaultAnswerMode = AnswerMode.Fast,
    };

    /// <summary>元宝：PackyAPI（hunyuan-officially 分组）混元 hy4 的 OpenAI Responses + web_search（max_tool_calls 限次）接口证据，固定 CN / zh-CN；
    /// 该渠道不返回检索结果，来源只有模型打开过的网页（visit 动作），多数回答没有来源。</summary>
    public static AnswerProviderDefinition PackyYuanbao { get; } = new(
        "packy_yuanbao", "api", "api_response", "packy-responses-v1", "packy_yuanbao_call", "packy_yuanbao:", "CN", "zh-CN",
        ["tencent_yuanbao"])
    {
        DisplayName = "元宝（PackyAPI）",
        PinnedSurface = true,
        NotConfiguredCode = PackyErrorCodes.NotConfigured,
        SupportedAnswerModes = [AnswerMode.Deep],
        DefaultAnswerMode = AnswerMode.Deep,
    };

    /// <summary>经 PackyAPI 接入的四个 Provider（每个分组一把 key、一行设置）。</summary>
    public static IReadOnlyList<AnswerProviderDefinition> Packy { get; } = [PackyQwen, PackyGlm, PackyKimi, PackyYuanbao];

    /// <summary>全部已注册的 API 回答 Provider，按接入顺序排列。</summary>
    public static IReadOnlyList<AnswerProviderDefinition> All { get; } = [Cloro, DoubaoApi, DeepSeekApi, PackyQwen, PackyGlm, PackyKimi, PackyYuanbao];

    /// <summary>按 Provider 标识查找定义（忽略首尾空白与大小写）；未注册时返回空值。</summary>
    /// <param name="provider">Provider 标识。</param>
    public static AnswerProviderDefinition? TryGet(string? provider)
    {
        var normalized = provider?.Trim();
        if (string.IsNullOrEmpty(normalized)) return null;
        foreach (var definition in All)
            if (string.Equals(definition.Provider, normalized, StringComparison.OrdinalIgnoreCase)) return definition;
        return null;
    }

    /// <summary>Provider 是否已注册为 API 回答 Provider。</summary>
    /// <param name="provider">Provider 标识。</param>
    public static bool IsRegistered(string? provider) => TryGet(provider) is not null;

    /// <summary>引擎代码是否属于该 Provider（忽略首尾空白与大小写）。</summary>
    /// <param name="engine">引擎代码。</param>
    public bool SupportsEngine(string? engine)
    {
        var normalized = engine?.Trim();
        return !string.IsNullOrEmpty(normalized)
            && Engines.Any(candidate => string.Equals(candidate, normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>该 Provider 是否支持指定回答模式。</summary>
    /// <param name="mode">回答模式。</param>
    public bool SupportsAnswerMode(AnswerMode mode) => SupportedAnswerModes.Contains(mode);

    /// <summary>生成 usage_ledger.reference，与 Cloro 现有写法 <c>cloro:{taskId}</c> 同形。</summary>
    /// <param name="providerTaskId">Provider Task 标识。</param>
    public string UsageReference(Guid providerTaskId) => $"{UsageReferencePrefix}{providerTaskId}";
}
