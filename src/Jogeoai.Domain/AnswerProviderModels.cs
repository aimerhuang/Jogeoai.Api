using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jogeoai.Domain;

/// <summary>API 回答 Provider 的专属选项，持久化在 answer_provider_settings.options。</summary>
public sealed record AnswerProviderOptions
{
    /// <summary>web_search 工具：豆包为方舟 Responses API + 豆包搜索（只返回正文角标引用），DeepSeek 为服务端 web_search（唯一可用模式）。</summary>
    public const string WebSearchMode = "web_search";
    /// <summary>豆包助手 API（doubao_app 工具）；本期仅占位，调用时返回明确错误。</summary>
    public const string DoubaoAppMode = "doubao_app";
    /// <summary>豆包专用：方舟 Anthropic 兼容 Messages API + web_search_20250305 工具（后端为豆包搜索），引用为全部检索来源；apiUrl 路径须以 /messages 结尾。</summary>
    public const string MessagesMode = "messages";
    /// <summary>豆包允许的请求模式：web_search、doubao_app、messages。</summary>
    public static IReadOnlyList<string> RequestModes { get; } = [WebSearchMode, DoubaoAppMode, MessagesMode];
    /// <summary>允许的深度思考档位。</summary>
    public static IReadOnlyList<string> ReasoningEfforts { get; } = ["minimal", "low", "medium", "high"];
    /// <summary>DeepSeek 思考开关：关闭（默认，贴近网页端“深度思考关”）。</summary>
    public const string ThinkingDisabled = "disabled";
    /// <summary>DeepSeek 思考开关：开启。</summary>
    public const string ThinkingEnabled = "enabled";
    /// <summary>允许的 DeepSeek 思考开关取值。</summary>
    public static IReadOnlyList<string> ThinkingModes { get; } = [ThinkingDisabled, ThinkingEnabled];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IReadOnlyList<string> sources = ["doubao"];

    /// <summary>请求模式：web_search（默认）；豆包另可选 messages（方舟 Messages API，记录全部检索来源）与 doubao_app（占位），DeepSeek 与 PackyAPI 只接受 web_search。</summary>
    public string RequestMode { get; init; } = WebSearchMode;

    /// <summary>web_search 工具的搜索源：豆包默认 ["doubao"]，doubao 不能与其他源混用；DeepSeek 与 PackyAPI 必须为空数组（省略时规范化为 []）。</summary>
    public IReadOnlyList<string> Sources
    {
        get => sources;
        init { sources = value; SourcesSpecified = true; }
    }

    // Distinguishes an omitted "sources" (DeepSeek normalizes it to []) from an explicit ["doubao"]; never serialized.
    [JsonIgnore]
    internal bool SourcesSpecified { get; private init; }

    /// <summary>单次请求最多触发的工具调用次数，1–10；豆包默认 3，DeepSeek 默认 5（作为 web_search 的 max_uses）；PackyAPI 千问 / GLM 只做记录不下发，Kimi 必须为 1（作为 max_uses）。</summary>
    public int MaxToolCalls { get; init; } = 3;

    /// <summary>深度思考档位 minimal / low / medium / high（豆包 web_search 模式与 PackyAPI GLM）；为空时不传，保持模型默认（豆包为开启，GLM 按 thinking 开关）；豆包 messages 模式必须为空。</summary>
    public string? ReasoningEffort { get; init; }

    /// <summary>思考开关 disabled / enabled（DeepSeek 与 PackyAPI，省略时为 disabled；豆包 messages 模式可选，省略时不传、保持模型默认开启）；豆包 web_search / doubao_app 模式必须为空；为空时不写入存储 JSON。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Thinking { get; init; }

    /// <summary>从已存储的 JSON 解析选项；空值或无法解析时返回默认值。</summary>
    /// <param name="json">answer_provider_settings.options 的内容。</param>
    public static AnswerProviderOptions Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<AnswerProviderOptions>(json, Json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    /// <summary>序列化为存储用 JSON。</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);
}

/// <summary>API 回答 Provider 设置的安全视图，不包含凭据。</summary>
/// <param name="Provider">Provider 标识，如 doubao_api、deepseek_api。</param>
/// <param name="ApiUrl">接口地址。</param>
/// <param name="Model">模型标识。</param>
/// <param name="Enabled">是否启用。</param>
/// <param name="TimeoutSeconds">单次调用超时秒数（5–600）。</param>
/// <param name="Options">Provider 专属选项。</param>
/// <param name="CredentialHint">凭据末尾脱敏提示；未保存凭据时为空。</param>
/// <param name="Configured">是否启用且已保存凭据，可被采集使用。</param>
/// <param name="UpdatedAt">最近一次更新的 UTC 时间。</param>
public sealed record AnswerProviderSettingsView(
    string Provider,
    string ApiUrl,
    string Model,
    bool Enabled,
    int TimeoutSeconds,
    AnswerProviderOptions Options,
    string? CredentialHint,
    bool Configured,
    DateTimeOffset? UpdatedAt);

/// <summary>Provider 适配器内部使用的完整配置，含凭据；ToString 与 JSON 序列化都不输出凭据。</summary>
/// <param name="Provider">Provider 标识。</param>
/// <param name="ApiUrl">接口地址。</param>
/// <param name="Model">模型标识。</param>
/// <param name="Credential">明文凭据，只用于 Authorization 请求头。</param>
/// <param name="TimeoutSeconds">单次调用超时秒数。</param>
/// <param name="Options">Provider 专属选项。</param>
public sealed record AnswerProviderConfiguration(
    string Provider,
    string ApiUrl,
    string Model,
    [property: JsonIgnore] string Credential,
    int TimeoutSeconds,
    AnswerProviderOptions Options)
{
    /// <summary>返回不含凭据的描述。</summary>
    public override string ToString() =>
        $"AnswerProviderConfiguration {{ Provider = {Provider}, ApiUrl = {ApiUrl}, Model = {Model}, TimeoutSeconds = {TimeoutSeconds} }}";
}

/// <summary>保存 API 回答 Provider 设置的请求。</summary>
/// <param name="ApiUrl">接口的绝对 HTTP 或 HTTPS 地址，不含用户信息、查询串和片段。</param>
/// <param name="Model">模型标识，1–128 字符。</param>
/// <param name="Credential">新凭据；省略或留空时保留已保存的凭据。只保存，不会返回。</param>
/// <param name="Enabled">是否启用。</param>
/// <param name="TimeoutSeconds">单次调用超时秒数，5–600。</param>
/// <param name="Options">Provider 专属选项；省略时使用默认值。</param>
public sealed record SaveAnswerProviderSettingsRequest(
    string ApiUrl,
    string Model,
    string? Credential = null,
    bool Enabled = false,
    int TimeoutSeconds = 180,
    AnswerProviderOptions? Options = null);

/// <summary>校验通过后的规范化设置，供存储层直接写入。</summary>
/// <param name="Provider">小写 Provider 标识。</param>
/// <param name="ApiUrl">规范化接口地址（去掉结尾斜杠）。</param>
/// <param name="Model">模型标识。</param>
/// <param name="Credential">新凭据；为空表示保留已有值。</param>
/// <param name="CredentialHint">新凭据的脱敏提示；为空表示保留已有值。</param>
/// <param name="Enabled">是否启用。</param>
/// <param name="TimeoutSeconds">超时秒数。</param>
/// <param name="Options">规范化选项。</param>
/// <param name="OptionsJson">选项的存储 JSON。</param>
public sealed record NormalizedAnswerProviderSettings(
    string Provider,
    string ApiUrl,
    string Model,
    [property: JsonIgnore] string? Credential,
    string? CredentialHint,
    bool Enabled,
    int TimeoutSeconds,
    AnswerProviderOptions Options,
    string OptionsJson);

/// <summary>API 回答 Provider 设置的校验规则；控制器与存储层共用，失败抛出 400 级 <see cref="CollectorStoreException"/>。</summary>
public static class AnswerProviderSettingsRules
{
    /// <summary>Provider 未注册。</summary>
    public const string ProviderNotSupported = "PROVIDER_NOT_SUPPORTED";
    /// <summary>设置内容不符合要求。</summary>
    public const string InvalidSettings = "INVALID_PROVIDER_SETTINGS";

    /// <summary>解析 Provider 标识；未注册时抛出 400 PROVIDER_NOT_SUPPORTED。</summary>
    /// <param name="provider">Provider 标识。</param>
    public static AnswerProviderDefinition RequireProvider(string? provider) =>
        AnswerProviderDefinition.TryGet(provider)
            ?? throw new CollectorStoreException(ProviderNotSupported, "Provider 不受支持。", 400);

    /// <summary>校验并规范化保存请求。</summary>
    /// <param name="provider">Provider 标识。</param>
    /// <param name="request">保存请求。</param>
    public static NormalizedAnswerProviderSettings Normalize(string? provider, SaveAnswerProviderSettingsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var definition = RequireProvider(provider);
        var model = request.Model?.Trim() ?? "";
        if (model.Length is < 1 or > 128 || model.Any(char.IsControl))
            throw Invalid("model 长度必须为 1–128 且不含控制字符。");
        if (!Uri.TryCreate(request.ApiUrl?.Trim(), UriKind.Absolute, out var apiUrl) || apiUrl.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(apiUrl.Host) || !string.IsNullOrEmpty(apiUrl.UserInfo)
            || !string.IsNullOrEmpty(apiUrl.Query) || !string.IsNullOrEmpty(apiUrl.Fragment)
            || apiUrl.ToString().Length > 2048)
            throw Invalid("apiUrl 必须是不含用户信息、查询串和片段的绝对 HTTP(S) 地址，且不超过 2048 字符。");
        if (request.TimeoutSeconds is < 5 or > 600) throw Invalid("timeoutSeconds 必须为 5–600。");
        var credential = string.IsNullOrWhiteSpace(request.Credential) ? null : request.Credential.Trim();
        if (credential is not null && (credential.Length > 1024 || credential.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))))
            throw Invalid("credential 不能包含空白或控制字符，且不超过 1024 字符。");
        var options = NormalizeOptions(definition, request.Options ?? new());
        if (definition.Provider == AnswerProviderDefinition.DoubaoApi.Provider)
        {
            // The two Ark endpoints take different bodies and auth headers; a mismatched pair would fail on every call.
            var messagesUrl = IsMessagesApiUrl(apiUrl);
            if (options.RequestMode == AnswerProviderOptions.MessagesMode && !messagesUrl)
                throw Invalid("options.requestMode 为 messages 时 apiUrl 路径必须以 /messages 结尾。");
            if (options.RequestMode == AnswerProviderOptions.WebSearchMode && messagesUrl)
                throw Invalid("options.requestMode 为 web_search 时 apiUrl 不能是 /messages 端点。");
        }
        return new(definition.Provider, apiUrl.ToString().TrimEnd('/'), model, credential,
            credential is null ? null : Mask(credential), request.Enabled, request.TimeoutSeconds, options, options.ToJson());
    }

    /// <summary>判断接口地址的路径是否为 Messages 端点（以 /messages 结尾，忽略结尾斜杠与大小写）。</summary>
    /// <param name="apiUrl">绝对接口地址。</param>
    public static bool IsMessagesApiUrl(Uri apiUrl)
    {
        ArgumentNullException.ThrowIfNull(apiUrl);
        return apiUrl.IsAbsoluteUri && apiUrl.AbsolutePath.TrimEnd('/').EndsWith("/messages", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>按 Provider 校验并规范化选项：豆包沿用 requestMode / sources / maxToolCalls / reasoningEffort 规则，
    /// web_search / doubao_app 模式 thinking 必须为空，messages 模式 sources 必须为 ["doubao"]、thinking 可为 enabled / disabled、reasoningEffort 必须为空；
    /// DeepSeek 与 PackyAPI 只接受 web_search、空 sources、maxToolCalls 1–10（packy_kimi 必须为 1；packy_yuanbao 会下发为 max_tool_calls）、
    /// thinking disabled / enabled（默认 disabled）与空 reasoningEffort（packy_glm 另可为 minimal / low / medium / high）。</summary>
    /// <param name="definition">已注册的 Provider 定义。</param>
    /// <param name="options">待校验的选项。</param>
    public static AnswerProviderOptions NormalizeOptions(AnswerProviderDefinition definition, AnswerProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(options);
        if (definition.Provider == AnswerProviderDefinition.DeepSeekApi.Provider) return NormalizeThinkingOptions(options, "DeepSeek", false);
        if (AnswerProviderDefinition.Packy.Any(packy => packy.Provider == definition.Provider))
        {
            // glm-5.3* always thinks on the zai-officially channel, so "fast" is expressed as reasoning effort instead.
            var normalized = NormalizeThinkingOptions(options, "PackyAPI",
                definition.Provider == AnswerProviderDefinition.PackyGlm.Provider);
            // The kimi-officially channel only runs the first server-side search; a second round ends the turn without an answer.
            if (definition.Provider == AnswerProviderDefinition.PackyKimi.Provider && normalized.MaxToolCalls != 1)
                throw Invalid("options.maxToolCalls 必须为 1（Kimi 渠道只执行一轮服务端搜索）。");
            return normalized;
        }
        return NormalizeDoubaoOptions(options);
    }

    private static AnswerProviderOptions NormalizeThinkingOptions(AnswerProviderOptions options, string name, bool allowEffort)
    {
        var mode = options.RequestMode?.Trim().ToLowerInvariant() ?? "";
        if (mode != AnswerProviderOptions.WebSearchMode) throw Invalid("options.requestMode 必须为 web_search。");
        // An omitted "sources" keeps the Doubao default in the record; only an explicit non-empty list is rejected.
        if (options.SourcesSpecified && options.Sources is { Count: > 0 })
            throw Invalid($"options.sources 必须为空数组（{name} web_search 不支持指定搜索源）。");
        if (options.MaxToolCalls is < 1 or > 10) throw Invalid("options.maxToolCalls 必须为 1–10。");
        var effort = string.IsNullOrWhiteSpace(options.ReasoningEffort) ? null : options.ReasoningEffort.Trim().ToLowerInvariant();
        if (effort is not null && !allowEffort)
            throw Invalid($"options.reasoningEffort 必须为空（{name} 只支持 thinking 开关）。");
        if (effort is not null && !AnswerProviderOptions.ReasoningEfforts.Contains(effort, StringComparer.Ordinal))
            throw Invalid("options.reasoningEffort 必须为空或 minimal / low / medium / high。");
        var thinking = string.IsNullOrWhiteSpace(options.Thinking)
            ? AnswerProviderOptions.ThinkingDisabled : options.Thinking.Trim().ToLowerInvariant();
        if (!AnswerProviderOptions.ThinkingModes.Contains(thinking, StringComparer.Ordinal))
            throw Invalid("options.thinking 必须为 disabled 或 enabled。");
        return new() { RequestMode = mode, Sources = [], MaxToolCalls = options.MaxToolCalls, ReasoningEffort = effort, Thinking = thinking };
    }

    // Doubao rules: web_search / doubao_app are unchanged since migration 025; messages adds the thinking switch.
    private static AnswerProviderOptions NormalizeDoubaoOptions(AnswerProviderOptions options)
    {
        var mode = options.RequestMode?.Trim().ToLowerInvariant() ?? "";
        var messages = mode == AnswerProviderOptions.MessagesMode;
        // Checked before the mode so an invalid web_search row keeps its historical message.
        if (!messages && !string.IsNullOrWhiteSpace(options.Thinking))
            throw Invalid("options.thinking 仅适用于 DeepSeek，豆包请使用 reasoningEffort。");
        if (!AnswerProviderOptions.RequestModes.Contains(mode, StringComparer.Ordinal))
            throw Invalid("options.requestMode 必须为 web_search、messages 或 doubao_app。");
        var sources = options.Sources?.Select(s => s?.Trim().ToLowerInvariant() ?? "").ToArray() ?? [];
        if (sources.Length is < 1 or > 10 || sources.Any(s => s.Length is < 1 or > 64 || s.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-')))
            || sources.Distinct(StringComparer.Ordinal).Count() != sources.Length)
            throw Invalid("options.sources 必须是 1–10 个不重复的非空标识。");
        if (options.MaxToolCalls is < 1 or > 10) throw Invalid("options.maxToolCalls 必须为 1–10。");
        var effort = string.IsNullOrWhiteSpace(options.ReasoningEffort) ? null : options.ReasoningEffort.Trim().ToLowerInvariant();
        if (effort is not null && !AnswerProviderOptions.ReasoningEfforts.Contains(effort, StringComparer.Ordinal))
            throw Invalid("options.reasoningEffort 必须为空或 minimal / low / medium / high。");
        if (!messages) return new() { RequestMode = mode, Sources = sources, MaxToolCalls = options.MaxToolCalls, ReasoningEffort = effort };

        // The Messages web_search tool has no sources field (the backend is always Doubao search); the stored shape stays ["doubao"].
        if (sources is not ["doubao"]) throw Invalid("options.sources 必须为 [\"doubao\"]（messages 模式固定使用豆包搜索）。");
        if (effort is not null) throw Invalid("options.reasoningEffort 必须为空（messages 模式请使用 thinking 开关）。");
        // An omitted thinking stays null so it is neither sent nor written to the stored JSON (model default is enabled).
        var thinking = string.IsNullOrWhiteSpace(options.Thinking) ? null : options.Thinking.Trim().ToLowerInvariant();
        if (thinking is not null && !AnswerProviderOptions.ThinkingModes.Contains(thinking, StringComparer.Ordinal))
            throw Invalid("options.thinking 必须为空、disabled 或 enabled。");
        return new() { RequestMode = mode, Sources = sources, MaxToolCalls = options.MaxToolCalls, Thinking = thinking };
    }

    /// <summary>凭据脱敏提示：保留末尾 4 位。</summary>
    /// <param name="value">明文凭据。</param>
    public static string Mask(string value) => value.Length <= 4 ? "****" : $"****{value[^4..]}";

    private static CollectorStoreException Invalid(string message) => new(InvalidSettings, message, 400);
}
