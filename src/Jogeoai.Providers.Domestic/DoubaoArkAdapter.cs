using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Providers.Abstractions;
using Microsoft.Extensions.Logging;

namespace Jogeoai.Providers.Domestic;

/// <summary>
/// 火山方舟豆包适配器，按 options.requestMode 分派：web_search 模式非流式调用 Responses API 的 web_search 工具（sources=["doubao"]），
/// 解析 output[] 的正文角标引用；messages 模式非流式调用 Anthropic 兼容 Messages API 的 web_search_20250305 工具（后端为豆包搜索），
/// 引用为全部检索来源（web_search_result）。两者都规范化为
/// <see cref="CloroAnswerSnapshot"/>（provider=doubao_api、engine=doubao、captureMode=api、evidenceClass=api_response）。
/// 凭据与响应正文从不进入异常消息或日志；日志只记录 HTTP 状态与方舟错误码。
/// </summary>
public sealed class DoubaoArkAdapter(
    IHttpClientFactory clients,
    IAnswerProviderSettingsStore settings,
    ILogger<DoubaoArkAdapter> logger) : IDoubaoAnswerClient
{
    private const int MaximumResponseBytes = 16 * 1024 * 1024;
    private const int MaximumErrorBytes = 64 * 1024;
    private const int MaximumQueries = 100;
    private const int MaximumQueryLength = 2048;
    private const string EmptyAnswer = "empty_answer";
    private const string SafetyFiltered = "safety_filtered";
    private static readonly AnswerProviderDefinition Definition = AnswerProviderDefinition.DoubaoApi;
    private static readonly Regex SafeCode = new("^[A-Za-z0-9_.-]{1,64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <inheritdoc />
    public async Task<CloroAnswerSnapshot> AskAsync(DoubaoAnswerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new DoubaoProviderException(DoubaoErrorCodes.InvalidRequest, "prompt 不能为空。", false);
        var configuration = await settings.GetConfigurationAsync(Definition.Provider, cancellationToken)
            ?? throw new DoubaoProviderException(DoubaoErrorCodes.NotConfigured, "豆包 Provider 尚未配置、未启用或缺少凭据。", false);
        if (!AnswerModeOverride.TryApply(Definition, configuration, request.AnswerMode, out configuration, out var modeError))
            throw new DoubaoProviderException(DoubaoErrorCodes.InvalidRequest, modeError, false);
        var options = configuration.Options;
        if (string.Equals(options.RequestMode, AnswerProviderOptions.DoubaoAppMode, StringComparison.Ordinal))
        {
            // 豆包助手 API（doubao_app 工具）beta 申请已被拒绝，本期只保留配置占位。开通后在此新增请求构造
            // （ark-beta-doubao-app 请求头、doubao_app 工具）与 output[] 解析分支，任务/Check/积分契约不变。
            // 见 .trellis/tasks/10-20-doubao-api-provider/research/ark-api-notes.md §5。
            throw new DoubaoProviderException(DoubaoErrorCodes.InvalidRequest, "豆包助手 API 尚未开通。", false);
        }
        var messagesMode = string.Equals(options.RequestMode, AnswerProviderOptions.MessagesMode, StringComparison.Ordinal);
        if (!messagesMode && !string.Equals(options.RequestMode, AnswerProviderOptions.WebSearchMode, StringComparison.Ordinal))
            throw new DoubaoProviderException(DoubaoErrorCodes.InvalidRequest, "options.requestMode 不受支持。", false);
        // Settings rows can be edited directly in SQL: re-check that the endpoint matches the request shape before calling it.
        if (Uri.TryCreate(configuration.ApiUrl, UriKind.Absolute, out var endpoint)
            && AnswerProviderSettingsRules.IsMessagesApiUrl(endpoint) != messagesMode)
            throw new DoubaoProviderException(DoubaoErrorCodes.InvalidRequest, messagesMode
                ? "options.requestMode 为 messages 时 apiUrl 路径必须以 /messages 结尾。"
                : "options.requestMode 为 web_search 时 apiUrl 不能是 /messages 端点。", false);
        if (messagesMode && !string.IsNullOrWhiteSpace(options.ReasoningEffort))
            throw new DoubaoProviderException(DoubaoErrorCodes.InvalidRequest, "options.reasoningEffort 在 messages 模式下必须为空。", false);
        if (messagesMode && options.Thinking is { } thinking && !AnswerProviderOptions.ThinkingModes.Contains(thinking, StringComparer.Ordinal))
            throw new DoubaoProviderException(DoubaoErrorCodes.InvalidRequest, "options.thinking 不受支持。", false);
        var parserVersion = messagesMode ? DoubaoDefaults.MessagesParserVersion : Definition.ParserVersion;

        var body = messagesMode ? BuildMessagesBody(configuration, request.Prompt) : BuildRequestBody(configuration, request.Prompt);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(configuration.TimeoutSeconds, 1, 3600)));
        HttpStatusCode status;
        string payload;
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, configuration.ApiUrl);
            if (messagesMode)
            {
                message.Headers.TryAddWithoutValidation("x-api-key", configuration.Credential);
                message.Headers.TryAddWithoutValidation("anthropic-version", DoubaoDefaults.AnthropicVersion);
            }
            else message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration.Credential);
            message.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await clients.CreateClient(DoubaoDefaults.HttpClientName)
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            status = response.StatusCode;
            payload = response.IsSuccessStatusCode
                ? await ReadBoundedAsync(response.Content, MaximumResponseBytes, truncate: false, timeout.Token)
                : await ReadBoundedAsync(response.Content, MaximumErrorBytes, truncate: true, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Doubao Ark request exceeded the configured timeout of {TimeoutSeconds}s.", configuration.TimeoutSeconds);
            throw new DoubaoProviderException(DoubaoErrorCodes.Timeout, $"豆包接口在 {configuration.TimeoutSeconds} 秒内未返回。", true);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning("Doubao Ark request failed before a response was read (HTTP {Status}).", (int?)exception.StatusCode);
            throw new DoubaoProviderException(DoubaoErrorCodes.RequestFailed, "豆包接口网络请求失败。", true);
        }
        catch (IOException)
        {
            // The connection dropped while the body was being read; the call may be repeated safely.
            logger.LogWarning("Doubao Ark response body could not be read completely.");
            throw new DoubaoProviderException(DoubaoErrorCodes.RequestFailed, "豆包接口响应读取失败。", true);
        }

        if ((int)status is >= 200 and < 300)
            return messagesMode ? ParseMessagesSuccess(payload, configuration) : ParseSuccess(payload, configuration);
        // Both Ark endpoints report failures in the same {"error":{"code":…}} shape.
        var code = ReadErrorCode(payload);
        logger.LogWarning("Doubao Ark returned HTTP {Status} with error code {Code}.", (int)status, code ?? "-");
        return MapFailure(status, code, configuration, parserVersion);
    }

    private static string BuildRequestBody(AnswerProviderConfiguration configuration, string prompt)
    {
        var options = configuration.Options;
        var body = new JsonObject
        {
            ["model"] = configuration.Model,
            ["input"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = prompt }),
            }),
            ["tools"] = new JsonArray(new JsonObject
            {
                ["type"] = "web_search",
                ["sources"] = new JsonArray(options.Sources.Select(source => (JsonNode?)JsonValue.Create(source)).ToArray()),
            }),
            ["max_tool_calls"] = options.MaxToolCalls,
        };
        // Default keeps deep thinking on (closest to the Doubao app); operators may lower it via options.reasoningEffort.
        if (!string.IsNullOrWhiteSpace(options.ReasoningEffort))
            body["reasoning"] = new JsonObject { ["effort"] = options.ReasoningEffort };
        return body.ToJsonString();
    }

    // Messages web_search has no sources field: the Ark backend is always Doubao search. thinking is sent only when configured,
    // otherwise the model default (enabled) applies.
    private static string BuildMessagesBody(AnswerProviderConfiguration configuration, string prompt)
    {
        var options = configuration.Options;
        var body = new JsonObject
        {
            ["model"] = configuration.Model,
            ["max_tokens"] = DoubaoDefaults.MessagesMaxTokens,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }),
            ["tools"] = new JsonArray(new JsonObject
            {
                ["type"] = DoubaoDefaults.WebSearchToolType,
                ["name"] = "web_search",
                ["max_uses"] = Math.Clamp(options.MaxToolCalls, 1, 10),
            }),
        };
        if (!string.IsNullOrWhiteSpace(options.Thinking))
            body["thinking"] = new JsonObject { ["type"] = options.Thinking };
        return body.ToJsonString();
    }

    private static async Task<string> ReadBoundedAsync(HttpContent content, int limit, bool truncate, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                if (truncate) { buffer.Write(chunk, 0, (int)Math.Max(0, limit - buffer.Length)); break; }
                throw new DoubaoProviderException(DoubaoErrorCodes.InvalidResponse, "豆包响应超出大小限制。", false);
            }
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    // Only a token-shaped error.code is retained; the message/param fields are never read.
    private static string? ReadErrorCode(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out var error)) return null;
            if (error.ValueKind != JsonValueKind.Object || !error.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.String) return null;
            var value = code.GetString();
            return value is not null && SafeCode.IsMatch(value) ? value : null;
        }
        catch (JsonException) { return null; }
    }

    private static CloroAnswerSnapshot MapFailure(HttpStatusCode status, string? code, AnswerProviderConfiguration configuration, string parserVersion)
    {
        var http = (int)status;
        var token = code ?? "";
        var suffix = code is null ? "" : $"（{code}）";
        bool Has(string part) => token.Contains(part, StringComparison.OrdinalIgnoreCase);
        bool Starts(string part) => token.StartsWith(part, StringComparison.OrdinalIgnoreCase);

        if (Has("RequestBurstTooFast") || Has("ServerOverloaded"))
            throw new DoubaoProviderException(DoubaoErrorCodes.RateLimited, "豆包接口限流或过载，请稍后重试" + suffix, true);
        switch (http)
        {
            case 401:
                throw new DoubaoProviderException(DoubaoErrorCodes.AuthFailed, "豆包凭据无效或账号状态异常" + suffix, false);
            case 403:
                if (Starts("AccountOverdue") || Has("ServiceOverdue"))
                    throw new DoubaoProviderException(DoubaoErrorCodes.QuotaExceeded, "方舟账号或服务欠费" + suffix, true, 10);
                if (Starts("AccessDenied") || Has("ServiceNotOpen"))
                    throw new DoubaoProviderException(DoubaoErrorCodes.ServiceNotOpen, "豆包搜索或模型服务尚未开通" + suffix, false);
                throw new DoubaoProviderException(DoubaoErrorCodes.AuthFailed, "豆包接口拒绝访问" + suffix, false);
            case 404:
                throw new DoubaoProviderException(DoubaoErrorCodes.ServiceNotOpen, "模型未开通或接入点无效" + suffix, false);
            case 429:
                if (Starts("QuotaExceeded") || Starts("ModelAccountTpd") || Starts("SetLimitExceeded"))
                    throw new DoubaoProviderException(DoubaoErrorCodes.QuotaExceeded, "豆包配额已用尽" + suffix, true, 10);
                throw new DoubaoProviderException(DoubaoErrorCodes.RateLimited, "豆包接口限流，请稍后重试" + suffix, true);
            case 400:
                var moderation = Has("SensitiveContent") || Has("RiskDetection") || Has("ContentSecurityDetection");
                if (moderation && Has("Output")) return SafetyFilteredSnapshot(configuration, http, code, parserVersion);
                if (moderation) throw new DoubaoProviderException(DoubaoErrorCodes.InputFiltered, "问题触发平台内容审核" + suffix, false);
                if (Has("ClosedEndpoint")) throw new DoubaoProviderException(DoubaoErrorCodes.ServiceNotOpen, "接入点已关闭" + suffix, false);
                throw new DoubaoProviderException(DoubaoErrorCodes.InvalidRequest, "豆包请求参数无效" + suffix, false);
            case 408:
            case 499:
            case >= 500:
                throw new DoubaoProviderException(DoubaoErrorCodes.RequestFailed, $"豆包接口暂时不可用（HTTP {http}）" + suffix, true);
            case >= 400 and < 500:
                throw new DoubaoProviderException(DoubaoErrorCodes.InvalidRequest, $"豆包请求被拒绝（HTTP {http}）" + suffix, false);
            default:
                throw new DoubaoProviderException(DoubaoErrorCodes.RequestFailed, $"豆包接口返回意外状态（HTTP {http}）" + suffix, true);
        }
    }

    // Output moderation removed the answer: the check succeeds with no answer and a known absence reason.
    private static CloroAnswerSnapshot SafetyFilteredSnapshot(AnswerProviderConfiguration configuration, int http, string? code, string parserVersion)
    {
        var raw = JsonSerializer.SerializeToElement(new { status = SafetyFiltered, httpStatus = http, code });
        return new CloroAnswerSnapshot(Definition.Provider, Definition.Engines[0], Definition.CaptureMode, Definition.EvidenceClass,
            CitationUrlRules.BoundString(configuration.Model, 128), string.Empty, false, [], [], raw, DateTimeOffset.UtcNow,
            ParserVersion: parserVersion, QueryAvailability: "not_exposed",
            ResultQuality: new(false, false, new("not_exposed", "unknown", null, 0, 0, 0, 0, []), SafetyFiltered));
    }

    private static CloroAnswerSnapshot ParseSuccess(string payload, AnswerProviderConfiguration configuration)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(payload); }
        catch (JsonException) { throw InvalidResponse(); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
                throw InvalidResponse();
            if (root.TryGetProperty("status", out var statusValue) && statusValue.ValueKind == JsonValueKind.String
                && string.Equals(statusValue.GetString(), "failed", StringComparison.OrdinalIgnoreCase))
                throw new DoubaoProviderException(DoubaoErrorCodes.RequestFailed, "豆包响应状态为 failed。", true);

            var text = new StringBuilder();
            var queries = new List<string>();
            var seenQueries = new HashSet<string>(StringComparer.Ordinal);
            var searchCalls = 0;
            var citations = new List<CloroCitation>();
            var seenUrls = new HashSet<string>(StringComparer.Ordinal);
            var exposed = false;
            var malformed = false;
            var received = 0;
            var filtered = 0;
            var invalid = 0;
            var truncated = 0;
            var reasons = new HashSet<string>(StringComparer.Ordinal);

            foreach (var item in output.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) { continue; }
                var type = ReadString(item, "type");
                if (type == "web_search_call")
                {
                    searchCalls++;
                    if (item.TryGetProperty("action", out var action) && action.ValueKind == JsonValueKind.Object)
                    {
                        if (action.TryGetProperty("queries", out var list) && list.ValueKind == JsonValueKind.Array)
                            foreach (var query in list.EnumerateArray()) AddQuery(query, queries, seenQueries);
                        else if (action.TryGetProperty("query", out var single)) AddQuery(single, queries, seenQueries);
                    }
                    continue;
                }
                if (type != "message" || !item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
                foreach (var part in content.EnumerateArray())
                {
                    if (part.ValueKind != JsonValueKind.Object || ReadString(part, "type") != "output_text") continue;
                    var fragment = ReadString(part, "text");
                    if (!string.IsNullOrWhiteSpace(fragment))
                    {
                        if (text.Length > 0) text.Append("\n\n");
                        text.Append(fragment);
                    }
                    if (!part.TryGetProperty("annotations", out var annotations)) continue;
                    exposed = true;
                    if (annotations.ValueKind != JsonValueKind.Array) { malformed = true; reasons.Add("INVALID_SOURCE_SHAPE"); continue; }
                    foreach (var annotation in annotations.EnumerateArray())
                    {
                        if (annotation.ValueKind != JsonValueKind.Object || ReadString(annotation, "type") != "url_citation") continue;
                        var url = ReadString(annotation, "url");
                        var result = CitationUrlRules.Normalize(url);
                        // The same source cited again at another position is one citation; it is not counted twice.
                        if (!seenUrls.Add(result.Url ?? url ?? $"\0invalid:{received}")) continue;
                        received++;
                        switch (result.Outcome)
                        {
                            case CitationUrlOutcome.Invalid: invalid++; reasons.Add("INVALID_SOURCE_SHAPE"); continue;
                            case CitationUrlOutcome.Unsafe: filtered++; reasons.Add("UNSAFE_SOURCE"); continue;
                        }
                        if (citations.Count == CitationUrlRules.MaximumCitations) { truncated++; reasons.Add("SOURCE_LIMIT"); continue; }
                        if (CitationUrlRules.BoundTitle(ReadString(annotation, "title"), out var title)) reasons.Add("SOURCE_FIELD_LIMIT");
                        citations.Add(new CloroCitation(result.Url!, result.Host!, title, citations.Count + 1));
                    }
                }
            }

            var answer = text.ToString();
            var present = !string.IsNullOrWhiteSpace(answer);
            var providedModel = ReadString(root, "model");
            var model = CitationUrlRules.BoundString(string.IsNullOrWhiteSpace(providedModel) ? configuration.Model : providedModel!, 128);
            var availability = !exposed ? "not_exposed" : malformed || invalid > 0 ? "unknown" : received == 0 ? "empty" : "available";
            var completeness = !exposed ? "unknown" : reasons.Count == 0 ? "complete" : malformed && received == 0 ? "unknown" : "partial";
            var quality = new CloroCitationQuality(availability, completeness, !exposed || malformed ? null : received,
                citations.Count, filtered, invalid, truncated, reasons.Order(StringComparer.Ordinal).ToArray());
            // The model decides whether to search: no web_search_call means no queries were ever produced.
            var queryAvailability = searchCalls == 0 ? "empty" : queries.Count > 0 ? "available" : "unknown";
            return new CloroAnswerSnapshot(Definition.Provider, Definition.Engines[0], Definition.CaptureMode, Definition.EvidenceClass,
                model, answer, present, citations, queries, StripEncryptedContent(payload), DateTimeOffset.UtcNow,
                ParserVersion: Definition.ParserVersion, QueryAvailability: queryAvailability,
                ResultQuality: new(false, !string.IsNullOrWhiteSpace(providedModel), quality, present ? null : EmptyAnswer));
        }
    }

    // Same mapping rules as DeepSeekAnthropicAdapter.ParseSuccess: every web_search_result is a retrieved source (text blocks carry no citations).
    private static CloroAnswerSnapshot ParseMessagesSuccess(string payload, AnswerProviderConfiguration configuration)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(payload); }
        catch (JsonException) { throw InvalidMessagesResponse(); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                throw InvalidMessagesResponse();
            switch (ReadString(root, "stop_reason"))
            {
                case "max_tokens":
                    // A truncated answer would be stored as if complete; fail terminally so the reservation is released.
                    throw new DoubaoProviderException(DoubaoErrorCodes.InvalidResponse, "豆包回答超过 max_tokens 被截断。", false);
                case "pause_turn":
                    // The server paused a long tool turn; repeating the whole call is the only safe continuation.
                    throw new DoubaoProviderException(DoubaoErrorCodes.RequestFailed, "豆包回答在工具调用中暂停（pause_turn）。", true);
            }

            var blocks = content.EnumerateArray().Where(block => block.ValueKind == JsonValueKind.Object).ToArray();
            // Text before the last tool block is a preamble ("我来搜索…") or an intermediate note, not the answer.
            var lastTool = Array.FindLastIndex(blocks, block => ReadString(block, "type") is "server_tool_use" or "web_search_tool_result");
            var text = new StringBuilder();
            for (var i = lastTool + 1; i < blocks.Length; i++)
            {
                if (ReadString(blocks[i], "type") != "text") continue;
                var fragment = ReadString(blocks[i], "text");
                if (string.IsNullOrWhiteSpace(fragment)) continue;
                if (text.Length > 0) text.Append("\n\n");
                text.Append(fragment);
            }

            var queries = new List<string>();
            var seenQueries = new HashSet<string>(StringComparer.Ordinal);
            var searchCalls = 0;
            var citations = new List<CloroCitation>();
            var seenUrls = new HashSet<string>(StringComparer.Ordinal);
            var exposed = false;
            var malformed = false;
            var received = 0;
            var filtered = 0;
            var invalid = 0;
            var truncated = 0;
            var reasons = new HashSet<string>(StringComparer.Ordinal);

            foreach (var block in blocks)
            {
                switch (ReadString(block, "type"))
                {
                    case "server_tool_use":
                        if (ReadString(block, "name") != "web_search") continue;
                        searchCalls++;
                        if (block.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object
                            && input.TryGetProperty("query", out var query))
                            AddQuery(query, queries, seenQueries);
                        continue;
                    case "web_search_tool_result":
                        exposed = true;
                        if (!block.TryGetProperty("content", out var results)) { malformed = true; reasons.Add("INVALID_SOURCE_SHAPE"); continue; }
                        // A failed search (max_uses_exceeded, invalid_tool_input …) is not a failure of the answer.
                        if (results.ValueKind == JsonValueKind.Object && ReadString(results, "type") == "web_search_tool_result_error") continue;
                        if (results.ValueKind != JsonValueKind.Array) { malformed = true; reasons.Add("INVALID_SOURCE_SHAPE"); continue; }
                        foreach (var item in results.EnumerateArray())
                        {
                            // Error items may be mixed into the list; anything that is not a search result carries no source.
                            if (item.ValueKind != JsonValueKind.Object || ReadString(item, "type") != "web_search_result") continue;
                            var url = ReadString(item, "url");
                            var result = CitationUrlRules.Normalize(url);
                            // The same source returned by several searches is one citation; it is not counted twice.
                            if (!seenUrls.Add(result.Url ?? url ?? $"\0invalid:{received}")) continue;
                            received++;
                            switch (result.Outcome)
                            {
                                case CitationUrlOutcome.Invalid: invalid++; reasons.Add("INVALID_SOURCE_SHAPE"); continue;
                                case CitationUrlOutcome.Unsafe: filtered++; reasons.Add("UNSAFE_SOURCE"); continue;
                            }
                            if (citations.Count == CitationUrlRules.MaximumCitations) { truncated++; reasons.Add("SOURCE_LIMIT"); continue; }
                            if (CitationUrlRules.BoundTitle(ReadString(item, "title"), out var title)) reasons.Add("SOURCE_FIELD_LIMIT");
                            citations.Add(new CloroCitation(result.Url!, result.Host!, title, citations.Count + 1));
                        }
                        continue;
                }
            }

            // DeepSeekMarkdown only normalizes whitespace; it carries no DeepSeek-specific rules.
            var answer = DeepSeekMarkdown.Normalize(text.ToString());
            var present = answer.Length > 0;
            var providedModel = ReadString(root, "model");
            var model = CitationUrlRules.BoundString(string.IsNullOrWhiteSpace(providedModel) ? configuration.Model : providedModel!, 128);
            var availability = !exposed ? "not_exposed" : malformed || invalid > 0 ? "unknown" : received == 0 ? "empty" : "available";
            var completeness = !exposed ? "unknown" : reasons.Count == 0 ? "complete" : malformed && received == 0 ? "unknown" : "partial";
            var quality = new CloroCitationQuality(availability, completeness, !exposed || malformed ? null : received,
                citations.Count, filtered, invalid, truncated, reasons.Order(StringComparer.Ordinal).ToArray());
            // The model decides whether to search: no server_tool_use means no queries were ever produced.
            var queryAvailability = searchCalls == 0 ? "empty" : queries.Count > 0 ? "available" : "unknown";
            return new CloroAnswerSnapshot(Definition.Provider, Definition.Engines[0], Definition.CaptureMode, Definition.EvidenceClass,
                model, answer, present, citations, queries, StripOpaqueContent(payload), DateTimeOffset.UtcNow,
                ParserVersion: DoubaoDefaults.MessagesParserVersion, QueryAvailability: queryAvailability,
                ResultQuality: new(false, !string.IsNullOrWhiteSpace(providedModel), quality, present ? null : EmptyAnswer));
        }
    }

    private static void AddQuery(JsonElement value, List<string> queries, HashSet<string> seen)
    {
        if (value.ValueKind != JsonValueKind.String || queries.Count >= MaximumQueries) return;
        var query = value.GetString()?.Trim();
        if (string.IsNullOrEmpty(query)) return;
        query = CitationUrlRules.BoundString(query, MaximumQueryLength);
        if (seen.Add(query)) queries.Add(query);
    }

    // encrypted_content carries opaque reasoning state; it is never persisted. The store applies its own key-based redaction afterwards.
    private static JsonElement StripEncryptedContent(string payload)
    {
        var node = JsonNode.Parse(payload);
        Strip(node);
        return JsonSerializer.SerializeToElement(node);
    }

    private static void Strip(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                obj.Remove("encrypted_content");
                foreach (var child in obj.Select(pair => pair.Value).ToArray()) Strip(child);
                break;
            case JsonArray array:
                foreach (var child in array) Strip(child);
                break;
        }
    }

    // Messages mode: encrypted_content (opaque page state), signature and the reasoning text of thinking blocks are never persisted.
    private static JsonElement StripOpaqueContent(string payload)
    {
        var node = JsonNode.Parse(payload);
        StripOpaque(node);
        return JsonSerializer.SerializeToElement(node);
    }

    private static void StripOpaque(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                obj.Remove("encrypted_content");
                obj.Remove("signature");
                var type = obj["type"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
                if (type == "thinking") obj.Remove("thinking");
                if (type == "redacted_thinking") obj.Remove("data");
                foreach (var child in obj.Select(pair => pair.Value).ToArray()) StripOpaque(child);
                break;
            case JsonArray array:
                foreach (var child in array) StripOpaque(child);
                break;
        }
    }

    private static string? ReadString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String
            ? field.GetString() : null;

    private static DoubaoProviderException InvalidResponse() =>
        new(DoubaoErrorCodes.InvalidResponse, "豆包响应不是合法的 Responses API 结构。", false);

    private static DoubaoProviderException InvalidMessagesResponse() =>
        new(DoubaoErrorCodes.InvalidResponse, "豆包响应不是合法的 Messages API 结构。", false);
}
