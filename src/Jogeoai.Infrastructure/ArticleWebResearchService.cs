using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Providers.Abstractions;

namespace Jogeoai.Infrastructure;

/// <summary>
/// 文章创作阶段 4：复用 deepseek-article 设置行的凭据，调用 DeepSeek 官方 Anthropic 兼容 Messages API 的服务端
/// web_search 工具（web_search_20250305）。请求与解析沿用 DeepSeekAnthropicAdapter：只取最后一个工具块之后的文字、
/// 只收 type=web_search_result 的来源。设置行未开启 web_search_enabled、未配置凭据或接口地址不是 DeepSeek 官方域名时跳过。
/// 凭据与响应正文从不进入异常消息或日志。
/// </summary>
public sealed class ArticleWebResearchService(IHttpClientFactory clients) : IArticleWebResearchService
{
    /// <summary>命名 HttpClient（不跟随重定向：x-api-key 是自定义请求头，会被重放到任意重定向目标）。</summary>
    public const string HttpClientName = "article-web-research";
    /// <summary>Anthropic 兼容接口版本头。</summary>
    public const string AnthropicVersion = "2023-06-01";
    /// <summary>服务端联网搜索工具类型。</summary>
    public const string WebSearchToolType = "web_search_20250305";
    private const int MaxOutputTokens = 8_000;
    private const int MaxSearches = 5;
    private const int MaxResults = 30;
    private const int MaxResponseBytes = 8 * 1024 * 1024;
    private const int MaxSummaryCharacters = 12_000;
    private static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(180);
    private static readonly Regex SafeCode = new("^[A-Za-z0-9_.-]{1,64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>由 deepseek-article 的 Chat Completions 地址推导 Anthropic 兼容 Messages 地址；非 DeepSeek 官方域名返回空。</summary>
    /// <param name="apiUrl">设置行中的接口地址。</param>
    public static string? AnthropicMessagesUrl(string? apiUrl)
    {
        if (!Uri.TryCreate(apiUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
            return null;
        if (!string.Equals(uri.Host, "api.deepseek.com", StringComparison.OrdinalIgnoreCase)) return null;
        return uri.AbsolutePath.StartsWith("/anthropic/", StringComparison.OrdinalIgnoreCase)
            ? uri.GetLeftPart(UriPartial.Path)
            : $"https://{uri.Host}/anthropic/v1/messages";
    }

    /// <summary>构造请求体（thinking 始终显式关闭，因服务端默认开启）。</summary>
    /// <param name="request">联网搜索输入。</param>
    public static string BuildRequestBody(ArticleWebResearchRequest request)
        => new JsonObject
        {
            ["model"] = ArticleGenerationProvider.ModelName,
            ["max_tokens"] = MaxOutputTokens,
            ["thinking"] = new JsonObject { ["type"] = "disabled" },
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = BuildPrompt(request) }),
            ["tools"] = new JsonArray(new JsonObject
            {
                ["type"] = WebSearchToolType,
                ["name"] = "web_search",
                ["max_uses"] = MaxSearches,
            }),
        }.ToJsonString();

    /// <inheritdoc />
    public async Task<ArticleWebResearchResult> ResearchAsync(ResearchProviderConfiguration configuration,
        ArticleWebResearchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(configuration.Credential)) return ArticleWebResearchResult.Skip("provider_not_configured");
        if (!configuration.WebSearchEnabled) return ArticleWebResearchResult.Skip("web_search_disabled");
        var url = AnthropicMessagesUrl(configuration.ApiUrl);
        if (url is null) return ArticleWebResearchResult.Skip("provider_unsupported");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var seconds = configuration.TimeoutSeconds <= 0 ? MaxDuration.TotalSeconds : Math.Min(configuration.TimeoutSeconds, MaxDuration.TotalSeconds);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        HttpStatusCode status;
        string payload;
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, url);
            message.Headers.TryAddWithoutValidation("x-api-key", configuration.Credential);
            message.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
            message.Content = new StringContent(BuildRequestBody(request), Encoding.UTF8, "application/json");
            using var response = await clients.CreateClient(HttpClientName)
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            status = response.StatusCode;
            payload = await ReadBoundedAsync(response.Content, response.IsSuccessStatusCode ? MaxResponseBytes : 64 * 1024,
                truncate: !response.IsSuccessStatusCode, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CollectorStoreException("WEB_RESEARCH_TIMEOUT", "联网搜索超时。", 503);
        }
        catch (HttpRequestException)
        {
            throw new CollectorStoreException("WEB_RESEARCH_REQUEST_FAILED", "联网搜索网络请求失败。", 503);
        }
        catch (IOException)
        {
            throw new CollectorStoreException("WEB_RESEARCH_REQUEST_FAILED", "联网搜索响应读取失败。", 503);
        }

        if ((int)status is >= 200 and < 300) return Parse(payload);
        var code = ReadErrorCode(payload);
        return (int)status switch
        {
            // An invalid request most likely means this key/model does not accept the server tool:
            // record it as "unsupported" so the article is still generated without web research.
            400 or 404 or 422 => ArticleWebResearchResult.Skip("provider_unsupported"),
            401 or 403 => throw new CollectorStoreException("WEB_RESEARCH_AUTH_FAILED", "联网搜索凭据无效或无权访问。", 503),
            402 => throw new CollectorStoreException("WEB_RESEARCH_INSUFFICIENT_BALANCE", "联网搜索账户余额不足。", 503),
            429 or 503 or 529 => throw new CollectorStoreException("WEB_RESEARCH_RATE_LIMITED", "联网搜索服务繁忙。", 503),
            _ => throw new CollectorStoreException("WEB_RESEARCH_REQUEST_FAILED",
                code is null ? $"联网搜索返回 HTTP {(int)status}。" : $"联网搜索返回 HTTP {(int)status}（{code}）。", 503)
        };
    }

    /// <summary>解析 Messages 响应：最后一个工具块之后的文字为摘要；web_search_result 按规范化 URL 去重。</summary>
    /// <param name="payload">响应正文。</param>
    public static ArticleWebResearchResult Parse(string payload)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(payload); }
        catch (JsonException) { throw InvalidResponse(); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                throw InvalidResponse();
            if (ReadString(root, "stop_reason") is "max_tokens" or "pause_turn")
                throw new CollectorStoreException("WEB_RESEARCH_INCOMPLETE", "联网搜索结果不完整。", 502);
            var blocks = content.EnumerateArray().Where(block => block.ValueKind == JsonValueKind.Object).ToArray();
            var lastTool = Array.FindLastIndex(blocks, block => ReadString(block, "type") is "server_tool_use" or "web_search_tool_result");
            var text = new StringBuilder();
            for (var i = lastTool + 1; i < blocks.Length; i++)
            {
                if (ReadString(blocks[i], "type") != "text" || ReadString(blocks[i], "text") is not { } fragment || string.IsNullOrWhiteSpace(fragment)) continue;
                if (text.Length > 0) text.Append("\n\n");
                text.Append(fragment.Trim());
            }
            var searches = 0;
            var results = new List<ArticleWebResearchSource>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var block in blocks)
            {
                switch (ReadString(block, "type"))
                {
                    case "server_tool_use" when ReadString(block, "name") == "web_search":
                        searches++;
                        break;
                    case "web_search_tool_result":
                        if (!block.TryGetProperty("content", out var items) || items.ValueKind != JsonValueKind.Array) break;
                        foreach (var item in items.EnumerateArray())
                        {
                            if (item.ValueKind != JsonValueKind.Object || ReadString(item, "type") != "web_search_result") continue;
                            var normalized = CitationUrlRules.Normalize(ReadString(item, "url"));
                            if (normalized.Outcome is CitationUrlOutcome.Invalid or CitationUrlOutcome.Unsafe || normalized.Url is null) continue;
                            if (!seen.Add(normalized.Url) || results.Count >= MaxResults) continue;
                            CitationUrlRules.BoundTitle(ReadString(item, "title"), out var title);
                            results.Add(new ArticleWebResearchSource(normalized.Url, normalized.Host ?? string.Empty, title));
                        }
                        break;
                }
            }
            var summary = text.ToString();
            if (summary.Length > MaxSummaryCharacters) summary = summary[..MaxSummaryCharacters];
            return new ArticleWebResearchResult(false, null, summary.Length == 0 ? null : summary, results, searches);
        }
    }

    private static string BuildPrompt(ArticleWebResearchRequest request)
    {
        var english = request.Language == "en-US";
        var builder = new StringBuilder();
        builder.Append(english
            ? "Search the web for authoritative, recent and verifiable information that helps answer the question below. Prefer official sites, standards bodies, reputable media and industry reports. Summarize the key facts with their source URLs; mark anything uncertain. Do not invent data."
            : "请联网检索能帮助回答下面问题的权威、近期且可核验的资料，优先官方网站、标准机构、权威媒体与行业报告。用要点总结关键事实并逐条注明来源链接；不确定的内容请标注。不要编造数据。");
        builder.Append("\n\n").Append(english ? "Question: " : "问题：").Append(Limit(request.Question, 2_000));
        if (request.CompetitorNames.Count > 0)
        {
            builder.Append("\n\n").Append(english
                ? "Also verify the official websites and publicly available information of these brands (names only; report only what sources state): "
                : "另外请核验以下品牌的官方网站与公开信息（只报告来源明确写出的内容）：");
            builder.Append(string.Join(english ? ", " : "、", request.CompetitorNames.Take(ArticleStudioLimits.MaxCompetitors).Select(name => Limit(name, 100))));
        }
        return builder.ToString();
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
                throw new CollectorStoreException("WEB_RESEARCH_RESPONSE_TOO_LARGE", "联网搜索响应超过大小限制。", 502);
            }
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static string? ReadErrorCode(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.Object) return null;
            foreach (var name in (string[])["type", "code"])
                if (error.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    && value.GetString() is { } token && SafeCode.IsMatch(token)) return token;
            return null;
        }
        catch (JsonException) { return null; }
    }

    private static string? ReadString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String
            ? field.GetString() : null;

    private static string Limit(string value, int max) => value.Length <= max ? value : value[..max];

    private static CollectorStoreException InvalidResponse() =>
        new("WEB_RESEARCH_INVALID_RESPONSE", "联网搜索响应不是合法的 Messages API 结构。", 502);
}
