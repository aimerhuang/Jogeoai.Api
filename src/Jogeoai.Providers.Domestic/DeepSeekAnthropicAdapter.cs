using System.Net;
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
/// DeepSeek 官方 Anthropic 兼容 Messages API 适配器：非流式调用服务端 web_search 工具（web_search_20250305），把 content[] 规范化为
/// <see cref="CloroAnswerSnapshot"/>（provider=deepseek_api、engine=deepseek、captureMode=api、evidenceClass=api_response）。
/// 引用记录的是检索来源（每次搜索返回的 web_search_result），接口不提供行内引用。
/// 凭据与响应正文从不进入异常消息或日志；日志只记录 HTTP 状态与已校验的错误类型。
/// </summary>
public sealed class DeepSeekAnthropicAdapter(
    IHttpClientFactory clients,
    IAnswerProviderSettingsStore settings,
    ILogger<DeepSeekAnthropicAdapter> logger) : IDeepSeekAnswerClient
{
    private const int MaximumResponseBytes = 16 * 1024 * 1024;
    private const int MaximumErrorBytes = 64 * 1024;
    private const int MaximumQueries = 100;
    private const int MaximumQueryLength = 2048;
    private const string EmptyAnswer = "empty_answer";
    private static readonly AnswerProviderDefinition Definition = AnswerProviderDefinition.DeepSeekApi;
    private static readonly Regex SafeCode = new("^[A-Za-z0-9_.-]{1,64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <inheritdoc />
    public async Task<CloroAnswerSnapshot> AskAsync(DeepSeekAnswerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new DeepSeekProviderException(DeepSeekErrorCodes.InvalidRequest, "prompt 不能为空。", false);
        var configuration = await settings.GetConfigurationAsync(Definition.Provider, cancellationToken)
            ?? throw new DeepSeekProviderException(DeepSeekErrorCodes.NotConfigured, "DeepSeek Provider 尚未配置、未启用或缺少凭据。", false);
        if (!AnswerModeOverride.TryApply(Definition, configuration, request.AnswerMode, out configuration, out var modeError))
            throw new DeepSeekProviderException(DeepSeekErrorCodes.InvalidRequest, modeError, false);
        var options = configuration.Options;
        if (!string.Equals(options.RequestMode, AnswerProviderOptions.WebSearchMode, StringComparison.Ordinal))
            throw new DeepSeekProviderException(DeepSeekErrorCodes.InvalidRequest, "options.requestMode 不受支持。", false);
        var thinking = string.IsNullOrWhiteSpace(options.Thinking) ? AnswerProviderOptions.ThinkingDisabled : options.Thinking;
        if (!AnswerProviderOptions.ThinkingModes.Contains(thinking, StringComparer.Ordinal))
            throw new DeepSeekProviderException(DeepSeekErrorCodes.InvalidRequest, "options.thinking 不受支持。", false);

        var body = BuildRequestBody(configuration, request.Prompt, thinking);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(configuration.TimeoutSeconds, 1, 3600)));
        HttpStatusCode status;
        string payload;
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, configuration.ApiUrl);
            message.Headers.TryAddWithoutValidation("x-api-key", configuration.Credential);
            message.Headers.TryAddWithoutValidation("anthropic-version", DeepSeekDefaults.AnthropicVersion);
            message.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await clients.CreateClient(DeepSeekDefaults.HttpClientName)
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            status = response.StatusCode;
            payload = response.IsSuccessStatusCode
                ? await ReadBoundedAsync(response.Content, MaximumResponseBytes, truncate: false, timeout.Token)
                : await ReadBoundedAsync(response.Content, MaximumErrorBytes, truncate: true, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("DeepSeek request exceeded the configured timeout of {TimeoutSeconds}s.", configuration.TimeoutSeconds);
            throw new DeepSeekProviderException(DeepSeekErrorCodes.Timeout, $"DeepSeek 接口在 {configuration.TimeoutSeconds} 秒内未返回。", true);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning("DeepSeek request failed before a response was read (HTTP {Status}).", (int?)exception.StatusCode);
            throw new DeepSeekProviderException(DeepSeekErrorCodes.RequestFailed, "DeepSeek 接口网络请求失败。", true);
        }
        catch (IOException)
        {
            // The connection dropped while the body was being read; the call may be repeated safely.
            logger.LogWarning("DeepSeek response body could not be read completely.");
            throw new DeepSeekProviderException(DeepSeekErrorCodes.RequestFailed, "DeepSeek 接口响应读取失败。", true);
        }

        if ((int)status is >= 200 and < 300) return ParseSuccess(payload, configuration);
        var code = ReadErrorCode(payload);
        logger.LogWarning("DeepSeek returned HTTP {Status} with error type {Code}.", (int)status, code ?? "-");
        throw MapFailure(status, code);
    }

    // thinking is always sent explicitly: the service default is thinking enabled.
    private static string BuildRequestBody(AnswerProviderConfiguration configuration, string prompt, string thinking)
    {
        var body = new JsonObject
        {
            ["model"] = configuration.Model,
            ["max_tokens"] = DeepSeekDefaults.MaxTokens,
            ["thinking"] = new JsonObject { ["type"] = thinking },
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }),
            ["tools"] = new JsonArray(new JsonObject
            {
                ["type"] = DeepSeekDefaults.WebSearchToolType,
                ["name"] = "web_search",
                ["max_uses"] = Math.Clamp(configuration.Options.MaxToolCalls, 1, 10),
            }),
        };
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
                throw new DeepSeekProviderException(DeepSeekErrorCodes.InvalidResponse, "DeepSeek 响应超出大小限制。", false);
            }
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    // Only a token-shaped error.type (Anthropic shape) or error.code (OpenAI shape) is retained; message/param are never read.
    private static string? ReadErrorCode(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
                return null;
            foreach (var name in (string[])["type", "code"])
            {
                if (error.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    && value.GetString() is { } token && SafeCode.IsMatch(token))
                    return token;
            }
            return null;
        }
        catch (JsonException) { return null; }
    }

    private static DeepSeekProviderException MapFailure(HttpStatusCode status, string? code)
    {
        var http = (int)status;
        var suffix = code is null ? "" : $"（{code}）";
        if (code is not null && code.Contains("overloaded", StringComparison.OrdinalIgnoreCase))
            return new(DeepSeekErrorCodes.RateLimited, "DeepSeek 服务过载，请稍后重试" + suffix, true);
        return http switch
        {
            401 or 403 => new(DeepSeekErrorCodes.AuthFailed, "DeepSeek 凭据无效或无权访问" + suffix, false),
            402 => new(DeepSeekErrorCodes.InsufficientBalance, "DeepSeek 账户余额不足" + suffix, true, 10),
            429 => new(DeepSeekErrorCodes.RateLimited, "DeepSeek 接口限流，请稍后重试" + suffix, true),
            503 or 529 => new(DeepSeekErrorCodes.RateLimited, $"DeepSeek 服务繁忙（HTTP {http}）" + suffix, true),
            400 or 404 or 413 or 422 => new(DeepSeekErrorCodes.InvalidRequest, $"DeepSeek 请求参数无效（HTTP {http}）" + suffix, false),
            408 or >= 500 => new(DeepSeekErrorCodes.RequestFailed, $"DeepSeek 接口暂时不可用（HTTP {http}）" + suffix, true),
            >= 400 and < 500 => new(DeepSeekErrorCodes.InvalidRequest, $"DeepSeek 请求被拒绝（HTTP {http}）" + suffix, false),
            _ => new(DeepSeekErrorCodes.RequestFailed, $"DeepSeek 接口返回意外状态（HTTP {http}）" + suffix, true),
        };
    }

    private static CloroAnswerSnapshot ParseSuccess(string payload, AnswerProviderConfiguration configuration)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(payload); }
        catch (JsonException) { throw InvalidResponse(); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                throw InvalidResponse();
            switch (ReadString(root, "stop_reason"))
            {
                case "max_tokens":
                    // A truncated answer would be stored as if complete; fail terminally so the reservation is released.
                    throw new DeepSeekProviderException(DeepSeekErrorCodes.InvalidResponse, "DeepSeek 回答超过 max_tokens 被截断。", false);
                case "pause_turn":
                    // The server paused a long tool turn; repeating the whole call is the only safe continuation.
                    throw new DeepSeekProviderException(DeepSeekErrorCodes.RequestFailed, "DeepSeek 回答在工具调用中暂停（pause_turn）。", true);
            }

            var blocks = content.EnumerateArray().Where(block => block.ValueKind == JsonValueKind.Object).ToArray();
            // Text before the last tool block is a preamble ("我来帮您查找…") or an intermediate note, not the answer.
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
                ParserVersion: Definition.ParserVersion, QueryAvailability: queryAvailability,
                ResultQuality: new(false, !string.IsNullOrWhiteSpace(providedModel), quality, present ? null : EmptyAnswer));
        }
    }

    private static void AddQuery(JsonElement value, List<string> queries, HashSet<string> seen)
    {
        if (value.ValueKind != JsonValueKind.String || queries.Count >= MaximumQueries) return;
        var query = value.GetString()?.Trim();
        // An empty query is rejected by the server with invalid_tool_input; it is not a search term.
        if (string.IsNullOrEmpty(query)) return;
        query = CitationUrlRules.BoundString(query, MaximumQueryLength);
        if (seen.Add(query)) queries.Add(query);
    }

    // encrypted_content (opaque page state), signature and the reasoning text of thinking blocks are never persisted.
    // The store applies its own key-based redaction afterwards.
    private static JsonElement StripOpaqueContent(string payload)
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
                obj.Remove("signature");
                var type = obj["type"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
                if (type == "thinking") obj.Remove("thinking");
                if (type == "redacted_thinking") obj.Remove("data");
                foreach (var child in obj.Select(pair => pair.Value).ToArray()) Strip(child);
                break;
            case JsonArray array:
                foreach (var child in array) Strip(child);
                break;
        }
    }

    private static string? ReadString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String
            ? field.GetString() : null;

    private static DeepSeekProviderException InvalidResponse() =>
        new(DeepSeekErrorCodes.InvalidResponse, "DeepSeek 响应不是合法的 Messages API 结构。", false);
}
