using System.Text.Json;
using Jogeoai.Domain;
using Jogeoai.Providers.Abstractions;

namespace Jogeoai.Providers.Domestic;

/// <summary>调用 DeepSeek 官方 Anthropic 兼容 Messages API（服务端 web_search）获取一条规范化回答；同步调用，没有“已提交未知结果”状态。</summary>
public interface IDeepSeekAnswerClient
{
    /// <summary>提交一个问题并返回规范化回答快照（provider=deepseek_api、engine=deepseek、captureMode=api、evidenceClass=api_response）。</summary>
    /// <param name="request">问题、区域语言、国家与可选的 Provider Task 标识。</param>
    /// <param name="cancellationToken">取消外部请求的令牌。</param>
    Task<CloroAnswerSnapshot> AskAsync(DeepSeekAnswerRequest request, CancellationToken cancellationToken = default);
}

/// <summary>发送给 DeepSeek 的回答请求。</summary>
/// <param name="Prompt">提交给模型的问题快照。</param>
/// <param name="Locale">BCP-47 区域语言；DeepSeek 固定 zh-CN。</param>
/// <param name="Country">两位 ISO 国家代码；DeepSeek 固定 CN。</param>
/// <param name="ProviderTaskId">本地 Provider Task 标识，诊断请求为空。</param>
/// <param name="AnswerMode">回答模式存储值（fast / deep）。为空时不覆盖，按 Provider 全局配置的思考参数下发（管理员诊断）；
/// 非空时按 <see cref="Jogeoai.Domain.AnswerModeProfile"/> 覆盖思考参数（每日监控按品牌回答模式），不支持的模式返回 InvalidRequest。</param>
public sealed record DeepSeekAnswerRequest(string Prompt, string Locale = "zh-CN", string Country = "CN", Guid? ProviderTaskId = null,
    string? AnswerMode = null);

/// <summary>DeepSeek Provider 的默认连接参数（与迁移 030 的种子一致）。</summary>
public static class DeepSeekDefaults
{
    /// <summary>Anthropic 兼容 Messages API 端点。</summary>
    public const string ApiUrl = "https://api.deepseek.com/anthropic/v1/messages";
    /// <summary>默认模型（配合关闭思考，贴近网页端“智能搜索开、深度思考关”）。</summary>
    public const string Model = "deepseek-flash";
    /// <summary>默认超时秒数；实测单次 15–45 秒。Worker 租约应不小于该值。</summary>
    public const int TimeoutSeconds = 240;
    /// <summary>单次回答最多搜索次数的默认值（web_search 工具的 max_uses）。</summary>
    public const int MaxToolCalls = 5;
    /// <summary>每次请求的 max_tokens。</summary>
    public const int MaxTokens = 16000;
    /// <summary>anthropic-version 请求头的取值。</summary>
    public const string AnthropicVersion = "2023-06-01";
    /// <summary>服务端搜索工具类型。</summary>
    public const string WebSearchToolType = "web_search_20250305";
    /// <summary>命名 HttpClient。</summary>
    public const string HttpClientName = "deepseek";

    /// <summary>DeepSeek 的默认选项：web_search、空 sources、最多 5 次搜索、关闭思考。</summary>
    public static AnswerProviderOptions Options() => new()
    {
        RequestMode = AnswerProviderOptions.WebSearchMode,
        Sources = [],
        MaxToolCalls = MaxToolCalls,
        Thinking = AnswerProviderOptions.ThinkingDisabled,
    };
}

/// <summary>DeepSeek 外部调用的安全错误（错误码 DEEPSEEK_*）；消息只含中文说明和已校验的错误类型标识，不含响应正文或凭据。</summary>
/// <param name="code">稳定错误码（DEEPSEEK_*）。</param>
/// <param name="message">安全的中文说明。</param>
/// <param name="retryable">是否允许重试当前调用。</param>
/// <param name="deferMinutes">建议延后的分钟数（余额不足时为 10）；为空表示按普通退避重试或不重试。</param>
public sealed class DeepSeekProviderException(string code, string message, bool retryable, int? deferMinutes = null)
    : AnswerProviderException(code, message, retryable, deferMinutes);

/// <summary>DeepSeek 响应 usage 的摘要，用于成本核对，不对终端用户展示。</summary>
/// <param name="InputTokens">输入 token 数（未命中缓存部分）。</param>
/// <param name="CacheReadInputTokens">命中缓存的输入 token 数。</param>
/// <param name="OutputTokens">输出 token 数。</param>
/// <param name="WebSearchRequests">服务端 web_search 调用次数（含超出 max_uses 的那一次）。</param>
public sealed record DeepSeekUsageSummary(
    long? InputTokens,
    long? CacheReadInputTokens,
    long? OutputTokens,
    long? WebSearchRequests)
{
    /// <summary>从规范化快照的 RawOutput（已剥离 encrypted_content、signature 与思考正文的响应 JSON）读取 usage；缺失时返回空值。</summary>
    /// <param name="rawOutput">适配器返回的 RawOutput。</param>
    public static DeepSeekUsageSummary? FromRawOutput(JsonElement rawOutput)
    {
        if (rawOutput.ValueKind != JsonValueKind.Object || !rawOutput.TryGetProperty("usage", out var usage)
            || usage.ValueKind != JsonValueKind.Object) return null;
        return new(Number(usage, "input_tokens"), Number(usage, "cache_read_input_tokens"), Number(usage, "output_tokens"),
            Number(usage, "server_tool_use", "web_search_requests"));
    }

    private static long? Number(JsonElement value, params string[] path)
    {
        foreach (var key in path)
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out value)) return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
    }
}
