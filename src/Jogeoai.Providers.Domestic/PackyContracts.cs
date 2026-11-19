using System.Text.Json;
using Jogeoai.Domain;
using Jogeoai.Providers.Abstractions;

namespace Jogeoai.Providers.Domestic;

/// <summary>经 PackyAPI 调用千问、智谱清言、Kimi 与元宝获取一条带检索来源的规范化回答；同步调用，没有“已提交未知结果”状态。</summary>
public interface IPackyAnswerClient
{
    /// <summary>按 <see cref="PackyAnswerRequest.Provider"/> 选择协议提交一个问题，返回规范化回答快照（captureMode=api、evidenceClass=api_response）。</summary>
    /// <param name="request">Provider、问题、区域语言、国家与可选的 Provider Task 标识。</param>
    /// <param name="cancellationToken">取消外部请求的令牌。</param>
    Task<CloroAnswerSnapshot> AskAsync(PackyAnswerRequest request, CancellationToken cancellationToken = default);
}

/// <summary>发送给 PackyAPI 的回答请求。</summary>
/// <param name="Provider">packy_qwen、packy_glm、packy_kimi 或 packy_yuanbao。</param>
/// <param name="Prompt">提交给模型的问题快照。</param>
/// <param name="Locale">BCP-47 区域语言；固定 zh-CN。</param>
/// <param name="Country">两位 ISO 国家代码；固定 CN。</param>
/// <param name="ProviderTaskId">本地 Provider Task 标识，诊断请求为空。</param>
/// <param name="AnswerMode">回答模式存储值（fast / deep）。为空时不覆盖，按 Provider 全局配置的思考参数下发（管理员诊断）；
/// 非空时按 <see cref="Jogeoai.Domain.AnswerModeProfile"/> 覆盖思考参数（每日监控按品牌回答模式），不支持的模式返回 InvalidRequest。</param>
public sealed record PackyAnswerRequest(string Provider, string Prompt, string Locale = "zh-CN", string Country = "CN",
    Guid? ProviderTaskId = null, string? AnswerMode = null);

/// <summary>PackyAPI Provider 的默认连接参数（与迁移 036 / 046 的种子一致）。</summary>
public static class PackyDefaults
{
    /// <summary>OpenAI Responses 端点（千问、GLM、元宝）。</summary>
    public const string ResponsesUrl = "https://www.packyapi.ai/v1/responses";
    /// <summary>Anthropic Messages 端点（Kimi）。</summary>
    public const string MessagesUrl = "https://www.packyapi.ai/v1/messages";
    /// <summary>GLM / Kimi 的默认超时秒数；实测单次 30–90 秒。Worker 租约应不小于该值。</summary>
    public const int TimeoutSeconds = 240;
    /// <summary>千问 / 元宝的默认超时秒数；千问完整回答实测 2–4 分钟以上（搜索可达十余次），元宝 hy4 限 3 次搜索仍约 5 分钟（思考关不掉）。</summary>
    public const int QwenTimeoutSeconds = 540;
    /// <summary>元宝默认的搜索次数上限（下发为 max_tool_calls）；不限时 hy4 一题搜索 20 次以上、输入 30 万 token 以上。</summary>
    public const int YuanbaoMaxToolCalls = 3;

    /// <summary>Provider 的默认超时秒数（与迁移 036 / 046 的种子一致）。</summary>
    /// <param name="provider">packy_qwen、packy_glm、packy_kimi 或 packy_yuanbao。</param>
    public static int Timeout(string provider) =>
        provider == AnswerProviderDefinition.PackyQwen.Provider || provider == AnswerProviderDefinition.PackyYuanbao.Provider
            ? QwenTimeoutSeconds : TimeoutSeconds;
    /// <summary>每次请求的输出上限（Responses 的 max_output_tokens、Messages 的 max_tokens）。</summary>
    public const int MaxOutputTokens = 16000;
    /// <summary>元宝（hy4）的输出上限：该渠道思考关不掉（网关回显 reasoning.effort=high），推理 token 计入输出，
    /// 16000 时 2026-11-08 验收 10 题中 2 题以 incomplete 结束；hy4 接受 32000 / 64000。</summary>
    public const int YuanbaoMaxOutputTokens = 32000;

    /// <summary>Provider 的输出上限（Responses 的 max_output_tokens、Messages 的 max_tokens）。</summary>
    /// <param name="provider">packy_qwen、packy_glm、packy_kimi 或 packy_yuanbao。</param>
    public static int OutputTokens(string provider) =>
        provider == AnswerProviderDefinition.PackyYuanbao.Provider ? YuanbaoMaxOutputTokens : MaxOutputTokens;
    /// <summary>anthropic-version 请求头的取值。</summary>
    public const string AnthropicVersion = "2023-06-01";
    /// <summary>Anthropic 服务端搜索工具类型。</summary>
    public const string AnthropicWebSearchToolType = "web_search_20250305";
    /// <summary>命名 HttpClient。</summary>
    public const string HttpClientName = "packy";

    /// <summary>Provider 的默认地址。</summary>
    /// <param name="provider">packy_qwen、packy_glm、packy_kimi 或 packy_yuanbao。</param>
    public static string ApiUrl(string provider) =>
        provider == AnswerProviderDefinition.PackyKimi.Provider ? MessagesUrl : ResponsesUrl;

    /// <summary>Provider 的默认模型，贴近网页端默认（R0 / 2026-10-29 实测通过的模型）。</summary>
    /// <param name="provider">packy_qwen、packy_glm、packy_kimi 或 packy_yuanbao。</param>
    public static string Model(string provider) => provider switch
    {
        "packy_qwen" => "qwen3.8-flash",
        "packy_glm" => "glm-5.3-flash",
        "packy_kimi" => "kimi-k3",
        "packy_yuanbao" => "hy4",
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    /// <summary>Provider 的默认选项：web_search、空 sources、关闭思考（GLM 另用 reasoningEffort=low，对应网页端“快速”）；Kimi 最多 1 次搜索，元宝最多 3 次（下发），千问 / GLM 记录 5 次（不下发）。</summary>
    /// <param name="provider">packy_qwen、packy_glm、packy_kimi 或 packy_yuanbao。</param>
    public static AnswerProviderOptions Options(string provider) => new()
    {
        RequestMode = AnswerProviderOptions.WebSearchMode,
        Sources = [],
        MaxToolCalls = provider == AnswerProviderDefinition.PackyKimi.Provider ? 1
            : provider == AnswerProviderDefinition.PackyYuanbao.Provider ? YuanbaoMaxToolCalls : 5,
        Thinking = AnswerProviderOptions.ThinkingDisabled,
        ReasoningEffort = provider == AnswerProviderDefinition.PackyGlm.Provider ? "low" : null,
    };
}

/// <summary>PackyAPI 外部调用的安全错误（错误码 PACKY_*）；消息只含中文说明和已校验的错误标识，不含响应正文或凭据。</summary>
/// <param name="code">稳定错误码（PACKY_*）。</param>
/// <param name="message">安全的中文说明。</param>
/// <param name="retryable">是否允许重试当前调用。</param>
/// <param name="deferMinutes">建议延后的分钟数（余额不足时为 10）；为空表示按普通退避重试或不重试。</param>
public sealed class PackyProviderException(string code, string message, bool retryable, int? deferMinutes = null)
    : AnswerProviderException(code, message, retryable, deferMinutes);

/// <summary>PackyAPI 响应 usage 的摘要，用于成本核对，不对终端用户展示。</summary>
/// <param name="InputTokens">输入 token 数。</param>
/// <param name="CachedInputTokens">命中缓存的输入 token 数。</param>
/// <param name="OutputTokens">输出 token 数。</param>
/// <param name="ReasoningTokens">推理 / 思考 token 数。</param>
/// <param name="SearchCalls">响应中的搜索调用数（web_search_call 或 server_tool_use 项）。</param>
public sealed record PackyUsageSummary(
    long? InputTokens,
    long? CachedInputTokens,
    long? OutputTokens,
    long? ReasoningTokens,
    int SearchCalls)
{
    /// <summary>从规范化快照的 RawOutput（已剥离加密内容与推理正文的响应 JSON）读取 usage；缺失时返回空值。</summary>
    /// <param name="rawOutput">适配器返回的 RawOutput。</param>
    public static PackyUsageSummary? FromRawOutput(JsonElement rawOutput)
    {
        if (rawOutput.ValueKind != JsonValueKind.Object || !rawOutput.TryGetProperty("usage", out var usage)
            || usage.ValueKind != JsonValueKind.Object) return null;
        var searches = 0;
        foreach (var name in (string[])["output", "content"])
            if (rawOutput.TryGetProperty(name, out var items) && items.ValueKind == JsonValueKind.Array)
                searches += items.EnumerateArray().Count(item => item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("type", out var type) && type.GetString() is "web_search_call" or "server_tool_use");
        return new(Number(usage, "input_tokens"),
            Number(usage, "input_tokens_details", "cached_tokens") ?? Number(usage, "cache_read_input_tokens"),
            Number(usage, "output_tokens"),
            Number(usage, "output_tokens_details", "reasoning_tokens") ?? Number(usage, "output_tokens_details", "thinking_tokens"),
            searches);
    }

    private static long? Number(JsonElement value, params string[] path)
    {
        foreach (var key in path)
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out value)) return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
    }
}
