using System.Net;
using System.Text;
using System.Text.Json;
using Jogeoai.Application;

namespace Jogeoai.Infrastructure;

/// <summary>
/// 固定 Luna 模型识别类 Provider 共用的 OpenAI 兼容网关客户端：组装 chat/completions 请求、
/// 以 2 MiB 上限有界读取响应、对网关状态码分类，以及解析 choices[0].message.content 中的 JSON 对象。
/// 错误的具体错误码由调用方的 <see cref="LunaGatewayErrors"/> 决定；本类不记录请求正文、凭据或模型原始响应。
/// </summary>
internal sealed class LunaGatewayClient(IHttpClientFactory clients)
{
    /// <summary>成功响应的最大字节数。</summary>
    internal const int MaxResponseBytes = 2 * 1024 * 1024;
    /// <summary>错误响应只读取前 64 KB 做关键词判断。</summary>
    internal const int MaxErrorBodyBytes = 64 * 1024;
    /// <summary>使用的命名 HttpClient（超时由调用方的取消令牌控制）。</summary>
    internal const string HttpClientName = "research";

    private static readonly JsonSerializerOptions RequestJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>发送 system + user 两条消息、要求 json_object 响应，返回有界读取的响应体与字节数。</summary>
    /// <param name="configuration">网关地址与凭据。</param>
    /// <param name="model">实际调用模型。</param>
    /// <param name="systemPrompt">系统提示。</param>
    /// <param name="userContent">user 消息的 content：字符串或内容块数组。</param>
    /// <param name="errors">调用方的错误映射。</param>
    /// <param name="cancellationToken">取消令牌（含调用方超时）。</param>
    internal async Task<LunaGatewayResponse> SendAsync(ResearchProviderConfiguration configuration, string model, string systemPrompt,
        object userContent, LunaGatewayErrors errors, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = model,
            ["messages"] = new object[]
            {
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["role"] = "system", ["content"] = systemPrompt },
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["role"] = "user", ["content"] = userContent },
            },
            ["response_format"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["type"] = "json_object" },
        }, RequestJsonOptions);
        return await PostJsonAsync(new Uri(configuration.ApiUrl, UriKind.RelativeOrAbsolute), configuration.Credential, payload, errors, cancellationToken);
    }

    /// <summary>以 Bearer 凭据 POST 已序列化的 JSON 请求体，按同一错误映射有界读取响应（chat/completions 与 responses 共用）。</summary>
    /// <param name="endpoint">网关地址。</param>
    /// <param name="credential">网关凭据。</param>
    /// <param name="payload">UTF-8 JSON 请求体。</param>
    /// <param name="errors">调用方的错误映射。</param>
    /// <param name="cancellationToken">取消令牌（含调用方超时）。</param>
    internal async Task<LunaGatewayResponse> PostJsonAsync(Uri endpoint, string? credential, byte[] payload, LunaGatewayErrors errors,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
        message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential);
        message.Content = new ByteArrayContent(payload);
        message.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        HttpResponseMessage response;
        try
        {
            response = await clients.CreateClient(HttpClientName).SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw errors.Unavailable(exception);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await ReadBoundedAsync(response, MaxErrorBodyBytes, errors, cancellationToken);
                throw errors.Rejected(response.StatusCode, body);
            }
            var text = await ReadBoundedAsync(response, MaxResponseBytes, errors, cancellationToken);
            return new LunaGatewayResponse(text, payload.Length, Encoding.UTF8.GetByteCount(text));
        }
    }

    /// <summary>
    /// 由已配置的 chat/completions 地址推导同网关的 responses 地址（品牌联网搜索与 AI 联网读取网页共用）；
    /// 非 http(s)、含用户信息或查询串、路径不以 /chat/completions 结尾时返回 null。Provider 是否匹配由调用方判断。
    /// </summary>
    /// <param name="apiUrl">已配置的 chat/completions 地址。</param>
    internal static Uri? ResponsesEndpoint(string? apiUrl)
    {
        if (!Uri.TryCreate(apiUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query)) return null;
        var path = uri.AbsolutePath.TrimEnd('/');
        const string suffix = "/chat/completions";
        if (!path.EndsWith(suffix, StringComparison.Ordinal)) return null;
        return new UriBuilder(uri) { Path = path[..^suffix.Length] + "/responses" }.Uri;
    }

    /// <summary>有界读取响应体：声明长度超过 2 MiB 直接拒绝；错误体超过上限时截断，成功体超过上限时拒绝。</summary>
    private static async Task<string> ReadBoundedAsync(HttpResponseMessage response, int limit, LunaGatewayErrors errors,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaxResponseBytes) throw errors.TooLarge();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken);
            if (read == 0) break;
            if (buffer.Length + read > limit)
            {
                if (limit < MaxResponseBytes) break; // 错误体只需前 64 KB 做关键词判断。
                throw errors.TooLarge();
            }
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>对网关非成功状态码分类：400/415/422 请求被拒、401/403 配置无效、408/429/5xx 可重试，其余为其他失败。</summary>
    /// <param name="statusCode">HTTP 状态码。</param>
    internal static LunaGatewayFailure Classify(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        if (code is 400 or 415 or 422) return LunaGatewayFailure.Rejected;
        if (code is 401 or 403) return LunaGatewayFailure.Unauthorized;
        if (code is 408 or 429 || code >= 500) return LunaGatewayFailure.Retryable;
        return LunaGatewayFailure.Other;
    }

    /// <summary>配置是否可用：有凭据、http(s) 绝对地址，Provider 为 openai-compatible 或回退的 openai-api。</summary>
    /// <param name="configuration">研究 Provider 配置。</param>
    /// <param name="providerName">期望的 Provider 标识。</param>
    internal static bool IsConfigured(ResearchProviderConfiguration? configuration, string providerName)
        => configuration is not null
           && !string.IsNullOrWhiteSpace(configuration.Credential)
           && Uri.TryCreate(configuration.ApiUrl, UriKind.Absolute, out var uri)
           && uri.Scheme is ("http" or "https")
           && (string.Equals(configuration.Provider, providerName, StringComparison.OrdinalIgnoreCase)
               || string.Equals(configuration.Provider, "openai-api", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 取出 choices[0].message.content（没有 choices 时取整个响应），去掉 ``` 包裹后截取第一个 { 到最后一个 } 并解析。
    /// </summary>
    /// <param name="rawBody">网关响应体。</param>
    /// <exception cref="JsonException">没有可解析的 JSON 对象。</exception>
    /// <exception cref="KeyNotFoundException">choices 结构缺少 message 或 content。</exception>
    /// <exception cref="InvalidOperationException">choices 结构类型不符。</exception>
    internal static JsonDocument ParseContentObject(string rawBody)
    {
        using var envelope = JsonDocument.Parse(rawBody);
        var root = envelope.RootElement;
        string json;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
        {
            var content = choices[0].GetProperty("message").GetProperty("content");
            json = content.ValueKind == JsonValueKind.String ? content.GetString() ?? string.Empty : content.GetRawText();
        }
        else json = root.GetRawText();
        json = StripFences(json);
        var start = json.IndexOf('{');
        var end = json.LastIndexOf('}');
        if (start < 0 || end <= start) throw new JsonException("no object");
        return JsonDocument.Parse(json[start..(end + 1)]);
    }

    /// <summary>去掉 ```json … ``` 包裹及残留的 ```。</summary>
    /// <param name="value">原始文本。</param>
    internal static string StripFences(string value)
    {
        var text = value.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstBreak = text.IndexOf('\n');
            text = firstBreak >= 0 ? text[(firstBreak + 1)..] : text[3..];
            if (text.EndsWith("```", StringComparison.Ordinal)) text = text[..^3];
        }
        return text.Replace("```", string.Empty, StringComparison.Ordinal).Trim();
    }

    /// <summary>清理写入提示的短字段：去掉 ``` 与控制字符（保留换行和制表），空值输出 (none)，按长度截断。</summary>
    /// <param name="value">原始值。</param>
    /// <param name="max">最大字符数。</param>
    internal static string Sanitize(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return "(none)";
        var cleaned = new string(value.Replace("```", string.Empty, StringComparison.Ordinal)
            .Where(character => !char.IsControl(character) || character is '\n' or '\r' or '\t').ToArray()).Trim();
        return Limit(cleaned, max);
    }

    /// <summary>按 UTF-16 长度截断。</summary>
    /// <param name="value">原始值。</param>
    /// <param name="max">最大长度。</param>
    internal static string Limit(string value, int max) => value.Length <= max ? value : value[..max];
}

/// <summary>网关响应：响应体与请求、响应字节数（仅用于日志）。</summary>
/// <param name="Body">有界读取的响应体。</param>
/// <param name="RequestBytes">请求体字节数。</param>
/// <param name="ResponseBytes">响应体字节数。</param>
internal sealed record LunaGatewayResponse(string Body, long RequestBytes, long ResponseBytes);

/// <summary>网关非成功状态码的分类。</summary>
internal enum LunaGatewayFailure
{
    /// <summary>400/415/422：请求未被网关接受。</summary>
    Rejected,
    /// <summary>401/403：凭据或配置无效。</summary>
    Unauthorized,
    /// <summary>408/429/5xx：暂时不可用，可重试。</summary>
    Retryable,
    /// <summary>其他失败。</summary>
    Other
}

/// <summary>调用方提供的错误映射。</summary>
/// <param name="Unavailable">网络异常（HttpRequestException）。</param>
/// <param name="Rejected">非成功状态码；第二个参数是最多 64 KB 的错误体，只能用于关键词判断，不得写入消息。</param>
/// <param name="TooLarge">响应超过大小限制。</param>
internal sealed record LunaGatewayErrors(
    Func<HttpRequestException, Exception> Unavailable,
    Func<HttpStatusCode, string, Exception> Rejected,
    Func<Exception> TooLarge);
