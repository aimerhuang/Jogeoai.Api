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
/// PackyAPI 适配器：千问（packy_qwen）、智谱清言（packy_glm）与元宝（packy_yuanbao，混元 hy4）走 OpenAI Responses 流式接口 + web_search，
/// Kimi（packy_kimi）走 Anthropic Messages 接口 + web_search_20250305（max_uses=1）。结果规范化为
/// <see cref="CloroAnswerSnapshot"/>（captureMode=api、evidenceClass=api_response）。
/// 千问 / GLM 的来源是全部检索结果（只有 URL，回答标注的 url_citation 补标题）；Kimi 的来源是一轮搜索的检索结果（带标题）；
/// 元宝渠道不返回检索结果，来源只有 visit 动作打开过的网页，一条都没有时记为 not_exposed。
/// 凭据与响应正文从不进入异常消息或日志；日志只记录 HTTP 状态与已校验的错误标识。
/// </summary>
public sealed class PackyAnswerAdapter(
    IHttpClientFactory clients,
    IAnswerProviderSettingsStore settings,
    ILogger<PackyAnswerAdapter> logger) : IPackyAnswerClient
{
    private const int MaximumResponseBytes = 16 * 1024 * 1024;
    private const int MaximumErrorBytes = 64 * 1024;
    private const int MaximumQueries = 100;
    private const int MaximumQueryLength = 2048;
    private const string EmptyAnswer = "empty_answer";
    // A single line shorter than this after a Messages search is a progress note, not an answer (live answers were ≥1,600 chars).
    private const int MinimumAnswerAfterSearch = 50;
    // \z, not $: "$" also matches before a trailing newline, which would let a line break into the log message.
    private static readonly Regex SafeCode = new(@"^[A-Za-z0-9_.-]{1,64}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // Tool-call markup the kimi-officially channel leaks into the text when a second search round is attempted.
    private static readonly string[] LeakedToolMarkup = ["<|open|>", "<|close|>"];

    /// <inheritdoc />
    public async Task<CloroAnswerSnapshot> AskAsync(PackyAnswerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var definition = AnswerProviderDefinition.Packy.FirstOrDefault(d => d.Provider == request.Provider)
            ?? throw new PackyProviderException(PackyErrorCodes.InvalidRequest, "Provider 不是 PackyAPI Provider。", false);
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new PackyProviderException(PackyErrorCodes.InvalidRequest, "prompt 不能为空。", false);
        var configuration = await settings.GetConfigurationAsync(definition.Provider, cancellationToken)
            ?? throw new PackyProviderException(PackyErrorCodes.NotConfigured, $"{definition.DisplayName}尚未配置、未启用或缺少凭据。", false);
        if (!AnswerModeOverride.TryApply(definition, configuration, request.AnswerMode, out configuration, out var modeError))
            throw new PackyProviderException(PackyErrorCodes.InvalidRequest, modeError, false);
        var options = configuration.Options;
        if (!string.Equals(options.RequestMode, AnswerProviderOptions.WebSearchMode, StringComparison.Ordinal))
            throw new PackyProviderException(PackyErrorCodes.InvalidRequest, "options.requestMode 不受支持。", false);
        var thinking = string.IsNullOrWhiteSpace(options.Thinking) ? AnswerProviderOptions.ThinkingDisabled : options.Thinking;
        if (!AnswerProviderOptions.ThinkingModes.Contains(thinking, StringComparer.Ordinal))
            throw new PackyProviderException(PackyErrorCodes.InvalidRequest, "options.thinking 不受支持。", false);
        var messages = definition.Provider == AnswerProviderDefinition.PackyKimi.Provider;

        var body = messages ? BuildMessagesBody(configuration, request.Prompt, thinking)
            : BuildResponsesBody(definition, configuration, request.Prompt, thinking, options.ReasoningEffort, options.MaxToolCalls);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(configuration.TimeoutSeconds, 1, 3600)));
        HttpStatusCode status;
        string payload;
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, configuration.ApiUrl);
            message.Headers.TryAddWithoutValidation("Authorization", $"Bearer {configuration.Credential}");
            if (messages)
            {
                message.Headers.TryAddWithoutValidation("x-api-key", configuration.Credential);
                message.Headers.TryAddWithoutValidation("anthropic-version", PackyDefaults.AnthropicVersion);
            }
            message.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await clients.CreateClient(PackyDefaults.HttpClientName)
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            status = response.StatusCode;
            if (!response.IsSuccessStatusCode)
                payload = await ReadBoundedAsync(response.Content, MaximumErrorBytes, truncate: true, timeout.Token);
            else if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
                payload = await ReadTerminalResponseAsync(response.Content, timeout.Token);
            else
                payload = await ReadBoundedAsync(response.Content, MaximumResponseBytes, truncate: false, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("PackyAPI {Provider} request exceeded the configured timeout of {TimeoutSeconds}s.",
                definition.Provider, configuration.TimeoutSeconds);
            throw new PackyProviderException(PackyErrorCodes.Timeout, $"PackyAPI 接口在 {configuration.TimeoutSeconds} 秒内未返回。", true);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning("PackyAPI {Provider} request failed before a response was read (HTTP {Status}).",
                definition.Provider, (int?)exception.StatusCode);
            throw new PackyProviderException(PackyErrorCodes.RequestFailed, "PackyAPI 接口网络请求失败。", true);
        }
        catch (IOException)
        {
            // The connection dropped while the body or stream was being read; the call may be repeated safely.
            logger.LogWarning("PackyAPI {Provider} response body could not be read completely.", definition.Provider);
            throw new PackyProviderException(PackyErrorCodes.RequestFailed, "PackyAPI 接口响应读取失败。", true);
        }

        if ((int)status is >= 200 and < 300)
        {
            try
            {
                return messages ? ParseMessages(payload, definition, configuration) : ParseResponses(payload, definition, configuration);
            }
            catch (PackyProviderException exception)
            {
                // The store keeps only a generic message per code; the adapter message (Chinese text + validated tokens) is the only detail.
                logger.LogWarning("PackyAPI {Provider} response rejected with {Code}: {Detail}",
                    definition.Provider, exception.Code, exception.Message);
                throw;
            }
        }
        var (code, type) = ReadErrorTokens(payload);
        logger.LogWarning("PackyAPI {Provider} returned HTTP {Status} with error code {Code} / type {Type}.",
            definition.Provider, (int)status, code ?? "-", type ?? "-");
        throw MapFailure(status, code, type);
    }

    // Qwen takes enable_thinking, GLM takes thinking.type; without disabling it the Qwen model spends the whole
    // output budget on reasoning and never searches. glm-5.3-flash ignores thinking.type=disabled, so a GLM reasoning
    // effort replaces the thinking switch (low ≈ the web "fast" mode). max_tool_calls is only sent for Yuanbao (hy4): verified on
    // hunyuan-officially (3 → exactly 3 searches); untested for Qwen / GLM. hy4 thinks regardless of the thinking switch (probe 2026-11-08).
    private static string BuildResponsesBody(AnswerProviderDefinition definition, AnswerProviderConfiguration configuration,
        string prompt, string thinking, string? reasoningEffort, int maxToolCalls)
    {
        var body = new JsonObject
        {
            ["model"] = configuration.Model,
            ["input"] = prompt,
            ["stream"] = true,
            ["max_output_tokens"] = PackyDefaults.OutputTokens(definition.Provider),
            ["tools"] = new JsonArray(new JsonObject { ["type"] = "web_search" }),
        };
        if (definition.Provider == AnswerProviderDefinition.PackyYuanbao.Provider)
            body["max_tool_calls"] = maxToolCalls;
        if (definition.Provider == AnswerProviderDefinition.PackyQwen.Provider)
            body["enable_thinking"] = thinking == AnswerProviderOptions.ThinkingEnabled;
        else if (!string.IsNullOrWhiteSpace(reasoningEffort))
            body["reasoning"] = new JsonObject { ["effort"] = reasoningEffort };
        else
            body["thinking"] = new JsonObject { ["type"] = thinking };
        return body.ToJsonString();
    }

    // max_uses is pinned to 1: the kimi-officially channel only executes the first server-side search round.
    private static string BuildMessagesBody(AnswerProviderConfiguration configuration, string prompt, string thinking)
    {
        var body = new JsonObject
        {
            ["model"] = configuration.Model,
            ["max_tokens"] = PackyDefaults.MaxOutputTokens,
            ["thinking"] = new JsonObject { ["type"] = thinking },
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }),
            ["tools"] = new JsonArray(new JsonObject
            {
                ["type"] = PackyDefaults.AnthropicWebSearchToolType,
                ["name"] = "web_search",
                ["max_uses"] = 1,
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
                throw new PackyProviderException(PackyErrorCodes.InvalidResponse, "PackyAPI 响应超出大小限制。", false);
            }
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    // Server-sent events: only the terminal event's "response" object is kept; it has the same shape as a
    // non-streaming Responses body. Deltas are discarded as they arrive, so memory stays bounded by one event.
    private static async Task<string> ReadTerminalResponseAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        long total = 0;
        string? terminal = null;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            total += line.Length + 1;
            if (total > MaximumResponseBytes)
                throw new PackyProviderException(PackyErrorCodes.InvalidResponse, "PackyAPI 响应超出大小限制。", false);
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data.Length == 0 || data == "[DONE]" || !data.StartsWith('{')) continue;
            // Cheap pre-filter: deltas are the bulk of the stream and never terminal.
            if (!data.Contains("\"response.completed\"", StringComparison.Ordinal)
                && !data.Contains("\"response.incomplete\"", StringComparison.Ordinal)
                && !data.Contains("\"response.failed\"", StringComparison.Ordinal)
                && !data.Contains("\"error\"", StringComparison.Ordinal)) continue;
            JsonDocument document;
            try { document = JsonDocument.Parse(data); }
            catch (JsonException) { continue; }
            using (document)
            {
                var root = document.RootElement;
                switch (ReadString(root, "type"))
                {
                    case "response.completed" or "response.incomplete" or "response.failed":
                        if (root.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object)
                            terminal = response.GetRawText();
                        break;
                    case "error":
                        throw new PackyProviderException(PackyErrorCodes.RequestFailed, "PackyAPI 流式响应返回错误事件。", true);
                }
            }
        }
        return terminal ?? throw new PackyProviderException(PackyErrorCodes.RequestFailed, "PackyAPI 流式响应在完成前中断。", true);
    }

    // Only token-shaped values are retained (error.code may also be a number such as -1); message/param are never read.
    private static (string? Code, string? Type) ReadErrorTokens(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return (null, null);
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
                return (null, null);
            return (Token(error, "code"), Token(error, "type"));
        }
        catch (JsonException) { return (null, null); }

        static string? Token(JsonElement error, string name) =>
            error.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { } token && SafeCode.IsMatch(token) ? token : null;
    }

    private static PackyProviderException MapFailure(HttpStatusCode status, string? code, string? type)
    {
        var http = (int)status;
        var label = code ?? type;
        var suffix = label is null ? "" : $"（{label}）";
        // A group without a channel for the model answers 503 model_not_found; retrying cannot help.
        if (code == "model_not_found")
            return new(PackyErrorCodes.InvalidRequest, "PackyAPI 当前分组没有该模型的可用渠道" + suffix, false);
        if (label is not null && label.Contains("overloaded", StringComparison.OrdinalIgnoreCase))
            return new(PackyErrorCodes.RateLimited, "PackyAPI 上游服务过载，请稍后重试" + suffix, true);
        return http switch
        {
            401 or 403 => new(PackyErrorCodes.AuthFailed, "PackyAPI 凭据无效或无权访问" + suffix, false),
            402 => new(PackyErrorCodes.InsufficientBalance, "PackyAPI 账户余额不足" + suffix, true, 10),
            429 => new(PackyErrorCodes.RateLimited, "PackyAPI 接口限流，请稍后重试" + suffix, true),
            503 or 529 => new(PackyErrorCodes.RateLimited, $"PackyAPI 服务繁忙（HTTP {http}）" + suffix, true),
            400 or 404 or 413 or 422 => new(PackyErrorCodes.InvalidRequest, $"PackyAPI 请求参数无效（HTTP {http}）" + suffix, false),
            408 or >= 500 => new(PackyErrorCodes.RequestFailed, $"PackyAPI 接口暂时不可用（HTTP {http}）" + suffix, true),
            >= 400 and < 500 => new(PackyErrorCodes.InvalidRequest, $"PackyAPI 请求被拒绝（HTTP {http}）" + suffix, false),
            _ => new(PackyErrorCodes.RequestFailed, $"PackyAPI 接口返回意外状态（HTTP {http}）" + suffix, true),
        };
    }

    private static CloroAnswerSnapshot ParseResponses(string payload, AnswerProviderDefinition definition,
        AnswerProviderConfiguration configuration)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(payload); }
        catch (JsonException) { throw InvalidResponse("Responses"); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
                throw InvalidResponse("Responses");
            switch (ReadString(root, "status"))
            {
                case "incomplete":
                    // A truncated answer would be stored as if complete; fail terminally so the reservation is released.
                    var reason = root.TryGetProperty("incomplete_details", out var details) ? ReadString(details, "reason") : null;
                    reason = reason is not null && SafeCode.IsMatch(reason) ? reason : "输出上限或内容过滤";
                    throw new PackyProviderException(PackyErrorCodes.InvalidResponse, $"PackyAPI 回答未完成（{reason}），已放弃。", false);
                case "failed":
                    throw new PackyProviderException(PackyErrorCodes.RequestFailed, "PackyAPI 回答生成失败。", true);
            }

            var items = output.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).ToArray();
            var sources = new SourceCollector();
            var queries = new List<string>();
            var seenQueries = new HashSet<string>(StringComparer.Ordinal);
            var searchCalls = 0;
            // Earlier message items are progress notes between search rounds ("我先获取了…，让我再搜索…"); only the last is the answer.
            var lastMessage = Array.FindLastIndex(items, item => ReadString(item, "type") == "message");
            var text = new StringBuilder();
            for (var i = 0; i < items.Length; i++)
            {
                var item = items[i];
                switch (ReadString(item, "type"))
                {
                    case "web_search_call":
                        searchCalls++;
                        if (!item.TryGetProperty("action", out var action) || action.ValueKind != JsonValueKind.Object) continue;
                        if (action.TryGetProperty("query", out var query)) AddQueries(query, queries, seenQueries);
                        if (action.TryGetProperty("queries", out var list) && list.ValueKind == JsonValueKind.Array)
                            foreach (var entry in list.EnumerateArray()) AddQueries(entry, queries, seenQueries);
                        // A "visit" action is a page the model opened; hy4 exposes no search results, so these are its only URLs.
                        if (ReadString(action, "type") == "visit" && ReadString(action, "url") is { Length: > 0 } visited)
                            sources.Add(visited, null);
                        if (!action.TryGetProperty("sources", out var found)) continue;
                        if (found.ValueKind != JsonValueKind.Array) { sources.Malformed(); continue; }
                        foreach (var source in found.EnumerateArray())
                            if (source.ValueKind == JsonValueKind.Object) sources.Add(ReadString(source, "url"), ReadString(source, "title"));
                        continue;
                    case "message":
                        if (!item.TryGetProperty("content", out var parts) || parts.ValueKind != JsonValueKind.Array) continue;
                        foreach (var part in parts.EnumerateArray())
                        {
                            if (part.ValueKind != JsonValueKind.Object || ReadString(part, "type") != "output_text") continue;
                            // Annotations of every message count: the progress notes cite sources the answer does not repeat.
                            if (part.TryGetProperty("annotations", out var annotations))
                            {
                                if (annotations.ValueKind != JsonValueKind.Array) sources.Malformed();
                                else
                                    foreach (var annotation in annotations.EnumerateArray())
                                        if (annotation.ValueKind == JsonValueKind.Object && ReadString(annotation, "type") == "url_citation")
                                            sources.Add(ReadString(annotation, "url"), ReadString(annotation, "title"));
                            }
                            if (i == lastMessage) text.Append(ReadString(part, "text"));
                        }
                        continue;
                }
            }

            // The hunyuan-officially channel never returns search results: a Yuanbao answer without any opened page has
            // sources that were not exposed, not an empty result list.
            var exposed = sources.Received > 0
                || (searchCalls > 0 && definition.Provider != AnswerProviderDefinition.PackyYuanbao.Provider);
            return Snapshot(definition, configuration, root, payload, text.ToString(), sources, exposed, queries, searchCalls, requireAnswerAfterSearch: false);
        }
    }

    private static CloroAnswerSnapshot ParseMessages(string payload, AnswerProviderDefinition definition,
        AnswerProviderConfiguration configuration)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(payload); }
        catch (JsonException) { throw InvalidResponse("Messages"); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                throw InvalidResponse("Messages");
            switch (ReadString(root, "stop_reason"))
            {
                case "max_tokens":
                    throw new PackyProviderException(PackyErrorCodes.InvalidResponse, "PackyAPI 回答超过 max_tokens 被截断。", false);
                case "pause_turn":
                    throw new PackyProviderException(PackyErrorCodes.RequestFailed, "PackyAPI 回答在工具调用中暂停（pause_turn）。", true);
            }

            var blocks = content.EnumerateArray().Where(block => block.ValueKind == JsonValueKind.Object).ToArray();
            // Text before the last tool block echoes the search ("Search results for query: …"); only text after it is the answer.
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

            var sources = new SourceCollector();
            var queries = new List<string>();
            var seenQueries = new HashSet<string>(StringComparer.Ordinal);
            var searchCalls = 0;
            var exposed = false;
            foreach (var block in blocks)
            {
                switch (ReadString(block, "type"))
                {
                    case "server_tool_use":
                        if (ReadString(block, "name") != "web_search") continue;
                        searchCalls++;
                        if (block.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object
                            && input.TryGetProperty("query", out var query))
                            AddQueries(query, queries, seenQueries);
                        continue;
                    case "web_search_tool_result":
                        exposed = true;
                        if (!block.TryGetProperty("content", out var results)) { sources.Malformed(); continue; }
                        // A failed search (max_uses_exceeded …) is not a failure of the answer.
                        if (results.ValueKind == JsonValueKind.Object && ReadString(results, "type") == "web_search_tool_result_error") continue;
                        if (results.ValueKind != JsonValueKind.Array) { sources.Malformed(); continue; }
                        foreach (var item in results.EnumerateArray())
                            if (item.ValueKind == JsonValueKind.Object && ReadString(item, "type") == "web_search_result")
                                sources.Add(ReadString(item, "url"), ReadString(item, "title"));
                        continue;
                }
            }

            return Snapshot(definition, configuration, root, payload, text.ToString(), sources, exposed || searchCalls > 0, queries,
                searchCalls, requireAnswerAfterSearch: lastTool >= 0);
        }
    }

    private static CloroAnswerSnapshot Snapshot(AnswerProviderDefinition definition, AnswerProviderConfiguration configuration,
        JsonElement root, string payload, string rawText, SourceCollector sources, bool exposed, IReadOnlyList<string> queries,
        int searchCalls, bool requireAnswerAfterSearch)
    {
        if (LeakedToolMarkup.Any(marker => rawText.Contains(marker, StringComparison.Ordinal)))
            throw new PackyProviderException(PackyErrorCodes.IncompleteAnswer, "PackyAPI 回答中混入了未执行的工具调用标记。", true);
        var answer = DeepSeekMarkdown.Normalize(rawText);
        // The kimi-officially channel may end the turn right after the search results when the model wants another round:
        // either with no text at all, or with a one-line progress note ("我再搜索一些更具体的型号和实测案例信息。", live 2026-10-29).
        if (requireAnswerAfterSearch && answer.Length == 0)
            throw new PackyProviderException(PackyErrorCodes.IncompleteAnswer, "PackyAPI 搜索完成后没有生成回答正文。", true);
        if (requireAnswerAfterSearch && answer.Length < MinimumAnswerAfterSearch && !answer.Contains('\n'))
            throw new PackyProviderException(PackyErrorCodes.IncompleteAnswer, "PackyAPI 搜索完成后只生成了一句过渡语，没有完整回答。", true);
        var present = answer.Length > 0;
        var providedModel = ReadString(root, "model");
        var model = CitationUrlRules.BoundString(string.IsNullOrWhiteSpace(providedModel) ? configuration.Model : providedModel!, 128);
        var quality = sources.Quality(exposed);
        // The model decides whether to search: no search call means no queries were ever produced.
        var queryAvailability = searchCalls == 0 ? "empty" : queries.Count > 0 ? "available" : "unknown";
        return new CloroAnswerSnapshot(definition.Provider, definition.Engines[0], definition.CaptureMode, definition.EvidenceClass,
            model, answer, present, sources.Citations, queries, StripOpaqueContent(payload), DateTimeOffset.UtcNow,
            ParserVersion: definition.ParserVersion, QueryAvailability: queryAvailability,
            ResultQuality: new(false, !string.IsNullOrWhiteSpace(providedModel), quality, present ? null : EmptyAnswer));
    }

    // Responses queries may join several keywords with ';' (Ark style); each keyword is a search term.
    private static void AddQueries(JsonElement value, List<string> queries, HashSet<string> seen)
    {
        if (value.ValueKind != JsonValueKind.String) return;
        foreach (var part in (value.GetString() ?? "").Split(';'))
        {
            if (queries.Count >= MaximumQueries) return;
            var query = part.Trim();
            if (query.Length == 0) continue;
            query = CitationUrlRules.BoundString(query, MaximumQueryLength);
            if (seen.Add(query)) queries.Add(query);
        }
    }

    // encrypted_content (opaque page state), signatures, reasoning text and signed logo links are never persisted.
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
                obj.Remove("logo_url");
                var type = obj["type"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
                if (type == "thinking") obj.Remove("thinking");
                if (type == "redacted_thinking") obj.Remove("data");
                if (type == "reasoning") { obj.Remove("summary"); obj.Remove("content"); }
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

    private static PackyProviderException InvalidResponse(string api) =>
        new(PackyErrorCodes.InvalidResponse, $"PackyAPI 响应不是合法的 {api} API 结构。", false);

    /// <summary>按出现顺序、规范化 URL 去重收集来源；同一 URL 先出现无标题、后出现带标题时补上标题。</summary>
    private sealed class SourceCollector
    {
        private readonly List<CloroCitation> citations = [];
        private readonly Dictionary<string, int> positions = new(StringComparer.Ordinal);
        private readonly HashSet<string> seen = new(StringComparer.Ordinal);
        private readonly HashSet<string> reasons = new(StringComparer.Ordinal);
        private bool malformed;
        private int filtered;
        private int invalid;
        private int truncated;

        public int Received { get; private set; }
        public IReadOnlyList<CloroCitation> Citations => citations;

        public void Malformed()
        {
            malformed = true;
            reasons.Add("INVALID_SOURCE_SHAPE");
        }

        public void Add(string? url, string? title)
        {
            var result = CitationUrlRules.Normalize(url);
            var key = result.Url ?? url ?? $"\0invalid:{Received}";
            if (!seen.Add(key))
            {
                // The answer's url_citation carries the title the search result list lacks.
                if (result.Url is not null && positions.TryGetValue(result.Url, out var index) && citations[index].Title is null
                    && !string.IsNullOrWhiteSpace(title))
                {
                    if (CitationUrlRules.BoundTitle(title, out var bounded)) reasons.Add("SOURCE_FIELD_LIMIT");
                    citations[index] = citations[index] with { Title = bounded };
                }
                return;
            }
            Received++;
            switch (result.Outcome)
            {
                case CitationUrlOutcome.Invalid: invalid++; reasons.Add("INVALID_SOURCE_SHAPE"); return;
                case CitationUrlOutcome.Unsafe: filtered++; reasons.Add("UNSAFE_SOURCE"); return;
            }
            if (citations.Count == CitationUrlRules.MaximumCitations) { truncated++; reasons.Add("SOURCE_LIMIT"); return; }
            if (CitationUrlRules.BoundTitle(title, out var boundedTitle)) reasons.Add("SOURCE_FIELD_LIMIT");
            positions[result.Url!] = citations.Count;
            citations.Add(new CloroCitation(result.Url!, result.Host!, boundedTitle, citations.Count + 1));
        }

        public CloroCitationQuality Quality(bool exposed)
        {
            var availability = !exposed ? "not_exposed" : malformed || invalid > 0 ? "unknown" : Received == 0 ? "empty" : "available";
            var completeness = !exposed ? "unknown" : reasons.Count == 0 ? "complete" : malformed && Received == 0 ? "unknown" : "partial";
            return new CloroCitationQuality(availability, completeness, !exposed || malformed ? null : Received,
                citations.Count, filtered, invalid, truncated, reasons.Order(StringComparer.Ordinal).ToArray());
        }
    }
}
