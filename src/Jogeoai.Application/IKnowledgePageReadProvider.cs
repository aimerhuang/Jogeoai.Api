namespace Jogeoai.Application;

/// <summary>
/// 网址识别的「AI 联网读取网页」兜底：服务端无法直接读取网页（本地 fake-ip、站点拦截服务器抓取等）时，
/// 请模型网关用联网工具打开该网址并返回页面主要正文。网页正文只在内存中传递，不落库、不写日志。
/// </summary>
public interface IKnowledgePageReadProvider
{
    /// <summary>请模型打开指定网址并返回页面正文；只有引用了同一主机的结果才视为成功。</summary>
    /// <param name="url">已规范化的公开网址（主机必须是 DNS 域名）。</param>
    /// <param name="configuration">研究 Provider 配置（openai-compatible，回退 openai-api）。</param>
    /// <param name="cancellationToken">取消令牌；调用方取消时抛出 <see cref="OperationCanceledException"/>，内部超时返回失败结果。</param>
    /// <returns>读取结果；网关错误、超时、无引用或引用主机不符都返回失败结果而不是抛出异常。</returns>
    Task<KnowledgePageReadResult> ReadAsync(string url, ResearchProviderConfiguration configuration, CancellationToken cancellationToken = default);
}

/// <summary>AI 联网读取网页的结果。</summary>
/// <param name="Succeeded">是否成功读取到该网页的正文。</param>
/// <param name="Title">与请求网址同主机的引用标题；可能为空。</param>
/// <param name="BodyText">页面正文纯文本（已去掉引用链接标记，最多 60 000 字）；失败时为空。</param>
/// <param name="CitationUrls">模型返回的引用网址（去重）。</param>
/// <param name="ErrorMessage">失败原因（中文，面向用户，不含网页正文、凭据或模型原始响应）；成功时为空。</param>
/// <param name="DurationMs">网关调用耗时（毫秒）。</param>
/// <param name="RequestBytes">请求体字节数（仅用于日志）。</param>
/// <param name="ResponseBytes">响应体字节数（仅用于日志）。</param>
public sealed record KnowledgePageReadResult(bool Succeeded, string? Title, string? BodyText, IReadOnlyList<string> CitationUrls,
    string? ErrorMessage, long DurationMs, long RequestBytes, long ResponseBytes)
{
    /// <summary>构造失败结果。</summary>
    /// <param name="message">失败原因（中文）。</param>
    /// <param name="durationMs">耗时（毫秒）。</param>
    /// <param name="requestBytes">请求体字节数。</param>
    /// <param name="responseBytes">响应体字节数。</param>
    /// <param name="citationUrls">已解析出的引用网址。</param>
    public static KnowledgePageReadResult Failed(string message, long durationMs = 0, long requestBytes = 0, long responseBytes = 0,
        IReadOnlyList<string>? citationUrls = null)
        => new(false, null, null, citationUrls ?? [], message, durationMs, requestBytes, responseBytes);
}
