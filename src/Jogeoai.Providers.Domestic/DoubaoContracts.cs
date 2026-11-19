using System.Text.Json;
using Jogeoai.Domain;
using Jogeoai.Providers.Abstractions;

namespace Jogeoai.Providers.Domestic;

/// <summary>调用豆包（火山方舟 Responses API，或 messages 模式下的 Anthropic 兼容 Messages API）获取一条规范化回答；同步调用，没有“已提交未知结果”状态。</summary>
public interface IDoubaoAnswerClient
{
    /// <summary>提交一个问题并返回规范化回答快照（provider=doubao_api、engine=doubao、captureMode=api、evidenceClass=api_response）。</summary>
    /// <param name="request">问题、区域语言、国家与可选的 Provider Task 标识。</param>
    /// <param name="cancellationToken">取消外部请求的令牌。</param>
    Task<CloroAnswerSnapshot> AskAsync(DoubaoAnswerRequest request, CancellationToken cancellationToken = default);
}

/// <summary>发送给豆包的回答请求。</summary>
/// <param name="Prompt">提交给模型的问题快照。</param>
/// <param name="Locale">BCP-47 区域语言；豆包固定 zh-CN。</param>
/// <param name="Country">两位 ISO 国家代码；豆包固定 CN。</param>
/// <param name="ProviderTaskId">本地 Provider Task 标识，诊断请求为空。</param>
/// <param name="AnswerMode">回答模式存储值（fast / deep）。为空时不覆盖，按 Provider 全局配置的思考参数下发（管理员诊断）；
/// 非空时按 <see cref="Jogeoai.Domain.AnswerModeProfile"/> 覆盖思考参数（每日监控按品牌回答模式），不支持的模式返回 InvalidRequest。</param>
public sealed record DoubaoAnswerRequest(string Prompt, string Locale = "zh-CN", string Country = "CN", Guid? ProviderTaskId = null,
    string? AnswerMode = null);

/// <summary>豆包 Provider 的默认连接参数（与迁移 025 的种子一致）。</summary>
public static class DoubaoDefaults
{
    /// <summary>方舟 Responses API 端点（web_search 模式）。</summary>
    public const string ApiUrl = "https://ark.cn-beijing.volces.com/api/v3/responses";
    /// <summary>方舟 Anthropic 兼容 Messages API 端点（messages 模式）。</summary>
    public const string MessagesApiUrl = "https://ark.cn-beijing.volces.com/api/compatible/v1/messages";
    /// <summary>messages 模式的 anthropic-version 请求头。</summary>
    public const string AnthropicVersion = "2023-06-01";
    /// <summary>messages 模式的服务端搜索工具类型（后端为豆包搜索）。</summary>
    public const string WebSearchToolType = "web_search_20250305";
    /// <summary>messages 模式的 max_tokens（含思考与正文）。</summary>
    public const int MessagesMaxTokens = 16000;
    /// <summary>messages 模式写入 visibility_checks.parser_version 的解析器版本（Responses 模式仍为 doubao-api-v1）。</summary>
    public const string MessagesParserVersion = "doubao-api-messages-v1";
    /// <summary>默认模型。</summary>
    public const string Model = "doubao-seed-2-1-pro-260915";
    /// <summary>默认超时秒数。</summary>
    public const int TimeoutSeconds = 180;
    /// <summary>命名 HttpClient。</summary>
    public const string HttpClientName = "doubao";
}

/// <summary>豆包外部调用的安全错误（错误码 DOUBAO_*）；消息只含中文说明和方舟错误码标识，不含响应正文或凭据。</summary>
/// <param name="code">稳定错误码（DOUBAO_*）。</param>
/// <param name="message">安全的中文说明。</param>
/// <param name="retryable">是否允许重试当前调用。</param>
/// <param name="deferMinutes">建议延后的分钟数（配额/欠费时为 10）；为空表示按普通退避重试或不重试。</param>
public sealed class DoubaoProviderException(string code, string message, bool retryable, int? deferMinutes = null)
    : AnswerProviderException(code, message, retryable, deferMinutes);

/// <summary>方舟响应 usage 的摘要，用于成本核对，不对终端用户展示；兼容 Responses 与 Messages 两种 usage 形状。</summary>
/// <param name="InputTokens">输入 token 数。</param>
/// <param name="OutputTokens">输出 token 数。</param>
/// <param name="ReasoningTokens">输出中用于深度思考的 token 数；Messages 响应不提供，为空。</param>
/// <param name="TotalTokens">总 token 数；Messages 响应未提供时为输入与输出之和。</param>
/// <param name="WebSearchCalls">web_search 工具调用次数（Messages 为 server_tool_use.web_search_requests）。</param>
/// <param name="DoubaoSearchCalls">其中豆包搜索源的调用次数（Messages 后端固定为豆包搜索，与 WebSearchCalls 相同）。</param>
public sealed record DoubaoUsageSummary(
    long? InputTokens,
    long? OutputTokens,
    long? ReasoningTokens,
    long? TotalTokens,
    long? WebSearchCalls,
    long? DoubaoSearchCalls)
{
    /// <summary>从规范化快照的 RawOutput（剥离 encrypted_content 等不透明字段后的响应 JSON）读取 usage；缺失时返回空值。</summary>
    /// <param name="rawOutput">适配器返回的 RawOutput。</param>
    public static DoubaoUsageSummary? FromRawOutput(JsonElement rawOutput)
    {
        if (rawOutput.ValueKind != JsonValueKind.Object || !rawOutput.TryGetProperty("usage", out var usage)
            || usage.ValueKind != JsonValueKind.Object) return null;
        // Messages usage has server_tool_use.web_search_requests instead of tool_usage, and no total or reasoning count.
        var messages = usage.TryGetProperty("server_tool_use", out _)
            || (rawOutput.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
                && !rawOutput.TryGetProperty("output", out _));
        if (messages)
        {
            var input = Number(usage, "input_tokens");
            var output = Number(usage, "output_tokens");
            var searches = Number(usage, "server_tool_use", "web_search_requests");
            return new(input, output, null, Number(usage, "total_tokens") ?? (input is null || output is null ? null : input + output),
                searches, searches);
        }
        return new(Number(usage, "input_tokens"), Number(usage, "output_tokens"),
            Number(usage, "output_tokens_details", "reasoning_tokens"), Number(usage, "total_tokens"),
            Number(usage, "tool_usage", "web_search"), Number(usage, "tool_usage_details", "web_search", "doubao"));
    }

    private static long? Number(JsonElement value, params string[] path)
    {
        foreach (var key in path)
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out value)) return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
    }
}
