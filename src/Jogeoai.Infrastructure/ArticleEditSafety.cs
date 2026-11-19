using System.Net;
using System.Text.RegularExpressions;
using Jogeoai.Domain;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Jogeoai.Infrastructure;

/// <summary>编辑器正文的 Markdown 解析（与 docx 导出共用同一管线）。</summary>
public static class ArticleMarkdown
{
    /// <summary>CommonMark + 管道表格；不启用原始 HTML 渲染以外的扩展。</summary>
    public static MarkdownPipeline Pipeline { get; } = new MarkdownPipelineBuilder().UsePipeTables().Build();

    /// <summary>解析 Markdown 文档；嵌套过深或表格过大时 Markdig 抛出 <see cref="ArgumentException"/>。</summary>
    /// <param name="markdown">Markdown 正文。</param>
    public static MarkdownDocument Parse(string markdown) => Markdown.Parse(markdown ?? string.Empty, Pipeline);

    /// <summary>解析 Markdown 文档；超过 Markdig 嵌套 / 表格上限时返回空，而不是抛出异常。</summary>
    /// <param name="markdown">Markdown 正文。</param>
    public static MarkdownDocument? TryParse(string? markdown)
    {
        try { return Parse(markdown ?? string.Empty); }
        catch (ArgumentException) { return null; }
    }

    /// <summary>按出现顺序返回正文中所有 Markdown 图片地址（含引用式图片）；无法解析时返回空列表。</summary>
    /// <param name="markdown">Markdown 正文。</param>
    public static IReadOnlyList<string> ImageUrls(string markdown)
        => TryParse(markdown)?.Descendants<LinkInline>().Where(link => link.IsImage)
            .Select(link => (link.Url ?? string.Empty).Trim()).ToArray() ?? [];

    /// <summary>平台图片地址（/api/v1/content/images/{imageId}）对应的图片标识；其他地址返回空。</summary>
    /// <param name="url">图片地址。</param>
    public static Guid? PlatformImageId(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var value = url.Trim();
        if (!value.StartsWith(ArticleEditorLimits.ImageUrlPrefix, StringComparison.Ordinal)) return null;
        var id = value[ArticleEditorLimits.ImageUrlPrefix.Length..];
        return Guid.TryParse(id, out var parsed) && parsed != Guid.Empty && (id.Length is 32 or 36) ? parsed : null;
    }

    /// <summary>是否为带主机名的绝对 https 链接。</summary>
    /// <param name="url">链接地址。</param>
    public static bool IsHttps(string? url)
        => Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Length > 0;
}

/// <summary>
/// 编辑侧（草稿自动保存、PATCH 手工编辑与智能微调结果共用）的内容安全检查：拒绝 script/iframe/object/embed/form 标签、HTML 标签上的
/// on* 事件属性，以及链接 / 图片地址或 HTML 属性中的 javascript: / vbscript: / data: / file: 协议；图片只允许平台图片地址或 https 链接。
/// 与生成侧不同，不禁止 Markdown 代码块；正文里作为普通文字出现的「JavaScript:」不算危险。
/// 与基础版本相同的正文不再检查（不让历史内容阻塞保存），基础版本已有的图片地址可以保留（不能是危险协议）。
/// </summary>
public static class ArticleEditSafety
{
    private static readonly Regex DangerousTag = new(@"<\s*/?\s*(script|iframe|object|embed|form)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // A scheme is only dangerous in a URL position: after "(", "<", a quote or "=" (Markdown destinations, autolinks,
    // HTML attributes). Prose such as "JavaScript: The Good Parts" is plain text and stays allowed.
    private static readonly Regex ScriptScheme = new(@"[(<""'=]\s*(?:java|vb)script\s*:",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DataUri = new(@"[(<""'=]\s*data\s*:\s*[a-z]+\s*/\s*[a-z0-9.+-]+\s*[;,]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // An on* attribute only matters inside an HTML start tag; plain prose such as "conditional=" is allowed.
    private static readonly Regex EventAttribute = new(@"<[a-z][^<>]*?[\s/""']on[a-z]+\s*=",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HtmlImage = new(@"<\s*img\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HtmlSource = new(@"\bsrc\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly string[] BlockedLinkSchemes = ["javascript:", "vbscript:", "data:", "file:"];

    /// <summary>校验标题与正文；不通过时抛出 400 ARTICLE_CONTENT_UNSAFE 或 ARTICLE_IMAGE_SOURCE_INVALID。</summary>
    /// <param name="title">标题。</param>
    /// <param name="body">Markdown 正文。</param>
    /// <param name="baseBody">编辑所基于的当前版本正文；正文与之完全相同时跳过正文检查，其中已有的非危险图片地址允许保留。</param>
    public static void Validate(string? title, string? body, string? baseBody = null)
    {
        if (FindUnsafe(title) is { } titleReason)
            throw new CollectorStoreException(ArticleEditorErrorCodes.ContentUnsafe, $"标题包含不允许的内容（{titleReason}）。", 400);
        if (string.IsNullOrEmpty(body)) return;
        // Content that is already stored is not re-judged: saving metadata or the title must not fail on legacy bodies.
        if (baseBody is not null && string.Equals(body, baseBody, StringComparison.Ordinal)) return;
        if (FindUnsafe(body) is { } bodyReason)
            throw new CollectorStoreException(ArticleEditorErrorCodes.ContentUnsafe, $"正文包含不允许的内容（{bodyReason}）。", 400);

        var document = ArticleMarkdown.TryParse(body)
            ?? throw new CollectorStoreException(ArticleEditorErrorCodes.ContentUnsafe, "正文嵌套层级过深或表格过大，无法解析。", 400);
        var existing = ExistingImageUrls(baseBody);
        foreach (var inline in document.Descendants<Inline>())
        {
            switch (inline)
            {
                case LinkInline { IsImage: true } image:
                    if (!IsAllowedImage((image.Url ?? string.Empty).Trim(), existing))
                        throw new CollectorStoreException(ArticleEditorErrorCodes.ImageSourceInvalid, "图片只允许平台上传图片或 https 链接。", 400);
                    break;
                case LinkInline link when HasBlockedScheme(link.Url ?? string.Empty):
                case AutolinkInline autolink when HasBlockedScheme(autolink.Url ?? string.Empty):
                    throw new CollectorStoreException(ArticleEditorErrorCodes.ContentUnsafe, "正文包含不允许的链接协议。", 400);
            }
        }
        foreach (Match image in HtmlImage.Matches(body))
            if (!IsAllowedImage(HtmlImageSource(image.Value), existing))
                throw new CollectorStoreException(ArticleEditorErrorCodes.ImageSourceInvalid, "图片只允许平台上传图片或 https 链接。", 400);
    }

    /// <summary>图片地址是否允许：平台图片地址或带主机名的 https 链接。</summary>
    /// <param name="url">图片地址。</param>
    public static bool IsAllowedImage(string? url)
        => ArticleMarkdown.PlatformImageId(url) is not null || ArticleMarkdown.IsHttps(url);

    /// <summary>
    /// 返回第一处危险标记的类别；安全时返回空。原文与 HTML 实体解码后的文本都会检查。用于标题、摘要、SEO 字段、微调指令与模型输出：
    /// 危险标签、事件属性，以及出现在地址位置（括号、尖括号、引号或等号之后）的 javascript: / vbscript: / data: 协议。
    /// </summary>
    /// <param name="text">待检查文本。</param>
    public static string? FindUnsafe(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        foreach (var candidate in new[] { text, WebUtility.HtmlDecode(text) })
        {
            if (DangerousTag.IsMatch(candidate)) return "script/iframe/object/embed/form 标签";
            if (ScriptScheme.IsMatch(candidate)) return "javascript: 链接";
            if (DataUri.IsMatch(candidate)) return "data: 链接";
            if (EventAttribute.IsMatch(candidate)) return "on* 事件属性";
        }
        return null;
    }

    private static bool IsAllowedImage(string url, IReadOnlySet<string> existing)
        => IsAllowedImage(url) || (existing.Contains(url) && !HasBlockedScheme(url));

    private static IReadOnlySet<string> ExistingImageUrls(string? baseBody)
    {
        if (string.IsNullOrEmpty(baseBody)) return new HashSet<string>(StringComparer.Ordinal);
        var urls = new HashSet<string>(ArticleMarkdown.ImageUrls(baseBody), StringComparer.Ordinal);
        foreach (Match image in HtmlImage.Matches(baseBody)) urls.Add(HtmlImageSource(image.Value));
        urls.Remove(string.Empty);
        return urls;
    }

    private static string HtmlImageSource(string tag)
    {
        var source = HtmlSource.Match(tag);
        return source.Success ? WebUtility.HtmlDecode(source.Groups[1].Success ? source.Groups[1].Value
            : source.Groups[2].Success ? source.Groups[2].Value : source.Groups[3].Value).Trim() : string.Empty;
    }

    private static bool HasBlockedScheme(string url)
    {
        var compact = new string(WebUtility.HtmlDecode(url).Where(character => !char.IsWhiteSpace(character) && !char.IsControl(character)).ToArray());
        return BlockedLinkSchemes.Any(scheme => compact.StartsWith(scheme, StringComparison.OrdinalIgnoreCase));
    }
}
