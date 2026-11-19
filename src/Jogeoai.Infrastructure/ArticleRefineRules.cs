using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jogeoai.Application;
using Jogeoai.Domain;

namespace Jogeoai.Infrastructure;

/// <summary>智能微调的纯函数：请求摘要、框选片段唯一匹配与替换、模型输出校验、错误码到 HTTP 状态的映射。</summary>
public static class ArticleRefineRules
{
    /// <summary>held 预占超过该时长仍未结算（进程崩溃等）即视为放弃并释放；远大于 120 秒的模型调用上限。</summary>
    public static readonly TimeSpan AbandonAfter = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    // Same shapes as the generation-side output checks (ArticleGenerationProvider): leaked credentials and role tags.
    private static readonly Regex SecretShape = new("(?:sk|rk)-[A-Za-z0-9_-]{12,}|Bearer\\s+[A-Za-z0-9._~+/=-]{12,}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RoleTag = new("<\\s*/?\\s*(system|user|assistant)\\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>统一换行为 \n。</summary>
    /// <param name="value">文本。</param>
    public static string NormalizeNewlines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>框选片段的规范形式：统一换行并去除首尾空白；为空时返回空。</summary>
    /// <param name="selection">前端提交的框选 Markdown。</param>
    public static string? NormalizeSelection(string? selection)
    {
        if (selection is null) return null;
        var value = NormalizeNewlines(selection).Trim();
        return value.Length == 0 ? null : value;
    }

    /// <summary>在正文中查找片段的唯一出现位置：不存在返回 -1，出现多次（含重叠）返回 -2。</summary>
    /// <param name="body">正文（已统一换行）。</param>
    /// <param name="selection">片段（已规范化）。</param>
    public static int FindUnique(string body, string selection)
    {
        if (string.IsNullOrEmpty(selection)) return -1;
        var first = body.IndexOf(selection, StringComparison.Ordinal);
        if (first < 0) return -1;
        return body.IndexOf(selection, first + 1, StringComparison.Ordinal) >= 0 ? -2 : first;
    }

    /// <summary>请求摘要：同一幂等键只能对应同一品牌、文章与请求内容。</summary>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="articleId">文章标识。</param>
    /// <param name="request">已规范化的微调请求。</param>
    public static string RequestHash(Guid siteId, Guid articleId, ArticleRefineRequest request)
    {
        var json = JsonSerializer.Serialize(new
        {
            siteId,
            articleId,
            scope = request.Scope == ArticleRefineScope.Whole ? "whole" : "selection",
            selection = request.SelectionMarkdown,
            instruction = request.Instruction,
            request.BaseVersionId,
            request.ExpectedRevision
        }, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    /// <summary>校验并规范化请求；不通过时抛出 400 / 422。</summary>
    /// <param name="request">原始请求。</param>
    public static ArticleRefineRequest Normalize(ArticleRefineRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Scope))
            throw new CollectorStoreException("INVALID_REFINE_REQUEST", "微调范围无效。", 400);
        var instruction = request.Instruction?.Trim() ?? string.Empty;
        if (instruction.Length is < 1 or > ArticleEditorLimits.InstructionMaxLength)
            throw new CollectorStoreException("INVALID_REFINE_REQUEST", "微调指令需为 1–500 字符。", 400);
        if (request.BaseVersionId == Guid.Empty || request.ExpectedRevision < 0)
            throw new CollectorStoreException("INVALID_REFINE_REQUEST", "基础版本或修订号无效。", 400);
        if (ArticleEditSafety.FindUnsafe(instruction) is not null)
            throw new CollectorStoreException(ArticleEditorErrorCodes.ContentUnsafe, "微调指令包含不允许的内容。", 400);
        string? selection = null;
        if (request.Scope == ArticleRefineScope.Selection)
        {
            selection = NormalizeSelection(request.SelectionMarkdown)
                ?? throw new CollectorStoreException("INVALID_REFINE_REQUEST", "框选段落微调需要提供选中内容。", 400);
            if (selection.Length > ArticleEditorLimits.SelectionMaxLength)
                throw new CollectorStoreException(ArticleEditorErrorCodes.RefineInputTooLong, "框选内容不能超过 10000 字符。", 422);
        }
        return request with { Instruction = instruction, SelectionMarkdown = selection };
    }

    /// <summary>把模型输出校验后组装为新标题与正文；不通过时抛出 502 REFINE_OUTPUT_INVALID。</summary>
    /// <param name="hold">预占时冻结的基础版本内容。</param>
    /// <param name="scope">微调范围。</param>
    /// <param name="selection">规范化后的框选片段。</param>
    /// <param name="output">模型输出。</param>
    public static (string Title, string Body) BuildResult(ArticleRefineHold hold, ArticleRefineScope scope, string? selection,
        ArticleRefineModelOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        string title, body, generated, source;
        if (scope == ArticleRefineScope.Whole)
        {
            body = NormalizeNewlines(output.BodyMarkdown ?? string.Empty).Trim();
            var newTitle = output.Title?.Trim();
            title = string.IsNullOrEmpty(newTitle) ? hold.Title : newTitle;
            generated = title + "\n" + body;
            source = hold.Title + "\n" + hold.BodyMarkdown;
        }
        else
        {
            if (selection is null || hold.SelectionStart < 0) throw Invalid();
            var replacement = NormalizeNewlines(output.ReplacementMarkdown ?? string.Empty).Trim();
            if (replacement.Length == 0) throw Invalid();
            title = hold.Title;
            body = hold.BodyMarkdown[..hold.SelectionStart] + replacement + hold.BodyMarkdown[(hold.SelectionStart + selection.Length)..];
            generated = replacement;
            source = selection;
        }
        if (body.Length is 0 or > ArticleEditorLimits.BodyMaxLength || title.Length is 0 or > ArticleEditorLimits.TitleMaxLength)
            throw Invalid();
        if (ArticleEditSafety.FindUnsafe(generated) is not null || SecretShape.IsMatch(generated) || RoleTag.IsMatch(generated)
            || generated.Any(character => char.IsControl(character) && character is not ('\n' or '\t')))
            throw Invalid();
        // Code fences and template braces are allowed in user-authored text, but the model may not introduce them.
        foreach (var marker in new[] { "```", "{{", "}}" })
            if (generated.Contains(marker, StringComparison.Ordinal) && !source.Contains(marker, StringComparison.Ordinal))
                throw Invalid();
        // The model may keep, move or drop existing images but never invent a new image URL.
        var known = ArticleMarkdown.ImageUrls(hold.BodyMarkdown).ToHashSet(StringComparer.Ordinal);
        if (ArticleMarkdown.ImageUrls(generated).Any(url => !known.Contains(url))) throw Invalid();
        // Judged against the base body: images and text outside the rewritten part were already stored and must not make
        // an otherwise valid refine fail (e.g. a legacy http:// image elsewhere in the article).
        try { ArticleEditSafety.Validate(title, body, hold.BodyMarkdown); }
        catch (CollectorStoreException) { throw Invalid(); }
        return (title, body);
    }

    /// <summary>失败错误码对应的 HTTP 状态（含同键重放已释放的操作）。</summary>
    /// <param name="code">错误码。</param>
    public static int StatusFor(string? code) => code switch
    {
        ArticleEditorErrorCodes.VersionConflict or "ARTICLE_ARCHIVED" or ArticleEditorErrorCodes.RefineInProgress => 409,
        ArticleEditorErrorCodes.RefineTimeout => 504,
        "ARTICLE_PROVIDER_UNAVAILABLE" or "ARTICLE_PROVIDER_CONFIG_INVALID" or "ARTICLE_PROVIDER_RETRYABLE" or "REFINE_CANCELLED"
            or "CREDIT_RECONCILIATION_REQUIRED" => 503,
        _ => 502,
    };

    private static CollectorStoreException Invalid()
        => new(ArticleEditorErrorCodes.RefineOutputInvalid, "模型返回的微调结果未通过格式或安全校验，未扣费。", 502);
}
