using Jogeoai.Api;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Jogeoai.Api.Controllers;

/// <summary>文章列表、编辑（含自动保存草稿与智能微调）、归档、导出和发布链接管理。</summary>
[ApiController]
[Route("api/v1/sites/{siteId:guid}/articles")]
public sealed class ContentController(
    IContentStore store,
    TenantContextResolver tenantContext,
    IArticleEditorStore editor,
    IArticleRefineService refine,
    IContentImageService images) : ContentControllerBase(tenantContext)
{
    /// <summary>分页读取当前品牌文章，并返回同一筛选下的总数、有效链接数、各阶段页签计数和被引计数；每行附带主发布链接与引用摘要（次数、平台、最早/最近被引）。</summary>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="q">标题搜索词。</param>
    /// <param name="status">文章状态。</param>
    /// <param name="sourceKind">文章来源：ai_generated 或 published_import。</param>
    /// <param name="monitorStatus">监控状态。</param>
    /// <param name="createdBy">创建人。</param>
    /// <param name="promptId">生成文章使用的主问题。</param>
    /// <param name="targetId">生成文章选择的目标平台。</param>
    /// <param name="tagId">文章标签。</param>
    /// <param name="stage">派生阶段筛选：generating、generation_failed、pending_publish、publishing、tracking 或 archived；页签计数不受此筛选影响。</param>
    /// <param name="language">文章语言筛选：zh-CN 或 en-US；其他取值返回 400 INVALID_FILTER。</param>
    /// <param name="onlyMine">是否只读取当前用户创建的文章。</param>
    /// <param name="ownedOnly">是否只读取存在官网域名有效发布链接的文章（仅官网文章）；等同于 ownership=owned，与 ownership=external 同时传返回 400 INVALID_FILTER。</param>
    /// <param name="ownership">域名归属筛选：owned（存在官网域名有效链接）或 external（存在有效链接且其中没有官网域名链接）；不传则不筛选。</param>
    /// <param name="citationDays">引用统计区间：站点时区近 7、30 或 90 个自然日，默认 30；引用次数、引用平台、最早/最近被引、是否被引用、被引计数和按引用排序都使用该区间；其他取值返回 400 INVALID_FILTER。</param>
    /// <param name="cited">是否被引用：true 只返回区间内引用次数大于 0 的文章；false 只返回等于 0 的文章，包含没有发布链接的文章（待发布、生成中、生成失败）；不传则不筛选。被引计数不受此筛选影响。</param>
    /// <param name="citationEngine">引用平台代码（如 chatgpt、doubao，规范化为小写，最长 64 字符）；设置后不过滤文章，但引用次数、引用平台、最早/最近被引、是否被引用、被引计数和按引用排序都只统计该平台的观测。</param>
    /// <param name="from">文章创建时间起点。</param>
    /// <param name="to">文章创建时间终点（不含）。</param>
    /// <param name="sort">排序方式：created_desc（默认，创建时间倒序）、updated_desc（更新时间倒序）、citation_desc（引用次数倒序）、last_cited_desc（最近被引时间倒序）或 first_cited_desc（最早被引时间倒序）；按被引时间排序时区间内没有被引的文章排在最后。</param>
    /// <param name="cursor">分页游标。</param>
    /// <param name="limit">每页数量。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    [HttpGet]
    [ProducesResponseType(typeof(ArticleListPage), StatusCodes.Status200OK)]
    public Task<IActionResult> List(Guid siteId, [FromQuery] string? q, [FromQuery] ArticleStatus? status,
        [FromQueryJsonEnum] ArticleSourceKind? sourceKind, [FromQuery] ArticleMonitorStatus? monitorStatus,
        [FromQuery] Guid? createdBy, [FromQuery] Guid? promptId, [FromQuery] Guid? targetId,
        [FromQuery] Guid? tagId, [FromQueryJsonEnum] ArticleStage? stage,
        [FromQuery] string? language, [FromQuery] bool onlyMine = false, [FromQuery] bool ownedOnly = false,
        [FromQueryJsonEnum] ArticleOwnershipFilter? ownership = null, [FromQuery] int citationDays = 30,
        [FromQuery] bool? cited = null, [FromQuery] string? citationEngine = null,
        [FromQuery] DateTimeOffset? from = null, [FromQuery] DateTimeOffset? to = null,
        [FromQuery] string sort = "created_desc", [FromQuery] string? cursor = null,
        [FromQuery] int limit = 20, CancellationToken cancellationToken = default)
        => Execute(async () =>
        {
            var user = RequireUser();
            if (onlyMine && createdBy is not null && createdBy != user.UserId)
                throw new CollectorStoreException("INVALID_FILTER", "仅看我的不能与其他创建人筛选同时使用。", 400);
            var effectiveCreatedBy = onlyMine ? user.UserId : createdBy;
            return Ok(await store.ListArticlesAsync(user.TenantId, siteId,
                new ArticleListFilter(q, status, sourceKind, monitorStatus, effectiveCreatedBy, tagId, from, to, sort, cursor, limit, promptId, targetId,
                    Stage: stage, Language: language, OwnedOnly: ownedOnly, CitationDays: citationDays, Cited: cited,
                    CitationEngine: citationEngine, Ownership: ownership), cancellationToken));
        });

    /// <summary>读取文章正文、版本和发布链接。</summary>
    [HttpGet("{articleId:guid}")]
    [ProducesResponseType(typeof(ArticleDetailView), StatusCodes.Status200OK)]
    public Task<IActionResult> Get(Guid siteId, Guid articleId, CancellationToken cancellationToken)
        => Execute(async () => { var user = RequireUser(); var result = await store.GetArticleAsync(user.TenantId, siteId, articleId, cancellationToken); return result is null ? NotFound(new { error = new { code = "RESOURCE_NOT_FOUND", message = "文章不存在。" } }) : Ok(result); });

    /// <summary>保存文章手工编辑版本并校验版本号。</summary>
    /// <remarks>每次保存都新建人工编辑版本并按 tagIds 重写标签；标题、正文与 SEO 字段拒绝 script/iframe/object/embed/form 标签、on* 事件属性与链接地址中的 javascript: / data: 协议，正文新增图片只允许平台图片或 https 链接（400 ARTICLE_CONTENT_UNSAFE / ARTICLE_IMAGE_SOURCE_INVALID）。正文与当前版本完全相同时不检查正文，当前版本已有的图片地址可以保留。提供 expectedRevision 时与当前版本草稿修订号不一致返回 409。</remarks>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="articleId">文章标识。</param>
    /// <param name="request">编辑内容。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    [HttpPatch("{articleId:guid}")]
    [ProducesResponseType(typeof(ArticleDetailView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> Edit(Guid siteId, Guid articleId, [FromBody] ArticleEditRequest request, CancellationToken cancellationToken)
        => Execute(async () => { var user = await RequireArticleWriterAsync(siteId, articleId, cancellationToken); return Ok(await store.EditArticleAsync(user.TenantId, siteId, articleId, user.UserId, request, cancellationToken)); });

    /// <summary>自动保存草稿：只保存标题与正文，不修改标签、SEO 与语言。</summary>
    /// <remarks>当前版本是未被发布链接引用的人工编辑版本且等于 baseVersionId、修订号等于 expectedRevision 时原地覆盖并使 editRevision 加 1；当前版本为 AI 生成、智能微调、导入或已被发布链接引用时新建人工编辑版本（editRevision 为 0）并切为当前版本。基础版本或修订号不一致返回 409 ARTICLE_VERSION_CONFLICT；危险标记返回 400。迁移 041 未部署时返回 503。</remarks>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="articleId">文章标识。</param>
    /// <param name="request">草稿内容与基础版本。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    [HttpPut("{articleId:guid}/draft")]
    [ProducesResponseType(typeof(ArticleDraftResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> SaveDraft(Guid siteId, Guid articleId, [FromBody] ArticleDraftRequest request, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var user = await RequireArticleWriterAsync(siteId, articleId, cancellationToken);
            return Ok(await editor.SaveDraftAsync(user.TenantId, siteId, articleId, user.UserId, request, cancellationToken));
        });

    /// <summary>智能微调：整体文章或框选段落按指令改写，成功后生成新的智能微调版本；每次 40 积分，失败不扣费。需要 Idempotency-Key 请求头。</summary>
    /// <remarks>同步调用文章模型（最长 120 秒）：先预占 40 积分，成功写入 refined 版本并结算，模型失败、超时或期间文章被修改时释放。框选内容必须在当前正文中恰好出现一次，否则 422 REFINE_SELECTION_NOT_FOUND；整体微调正文超过 20000 字符返回 422 REFINE_INPUT_TOO_LONG。同一幂等键重放返回同一结果且不重复扣费，用于不同请求返回 409 IDEMPOTENCY_CONFLICT。</remarks>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="articleId">文章标识。</param>
    /// <param name="request">微调范围、选中内容、指令与基础版本。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    [HttpPost("{articleId:guid}/refine")]
    [ProducesResponseType(typeof(ArticleRefineResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status402PaymentRequired)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(StatusCodes.Status504GatewayTimeout)]
    public Task<IActionResult> Refine(Guid siteId, Guid articleId, [FromBody] ArticleRefineRequest request, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var user = await RequireArticleWriterAsync(siteId, articleId, cancellationToken);
            var key = Request.Headers["Idempotency-Key"].FirstOrDefault() ?? string.Empty;
            return Ok(await refine.RefineAsync(user.TenantId, siteId, articleId, user.UserId, request, key, cancellationToken));
        });

    /// <summary>归档文章；归档不删除历史版本或已发布链接。</summary>
    [HttpPost("{articleId:guid}/archive")]
    [ProducesResponseType(typeof(ArticleView), StatusCodes.Status200OK)]
    public Task<IActionResult> Archive(Guid siteId, Guid articleId, CancellationToken cancellationToken)
        => Execute(async () => { var user = await RequireArticleWriterAsync(siteId, articleId, cancellationToken); var result = await store.SetArchivedAsync(user.TenantId, siteId, articleId, user.UserId, true, cancellationToken); return result is null ? NotFound() : Ok(result); });

    /// <summary>恢复已归档文章。</summary>
    [HttpPost("{articleId:guid}/restore")]
    [ProducesResponseType(typeof(ArticleView), StatusCodes.Status200OK)]
    public Task<IActionResult> Restore(Guid siteId, Guid articleId, CancellationToken cancellationToken)
        => Execute(async () => { var user = await RequireArticleWriterAsync(siteId, articleId, cancellationToken); var result = await store.SetArchivedAsync(user.TenantId, siteId, articleId, user.UserId, false, cancellationToken); return result is null ? NotFound() : Ok(result); });

    /// <summary>启用、暂停或停用文章引用监控。</summary>
    [HttpPatch("{articleId:guid}/monitor")]
    [ProducesResponseType(typeof(ArticleView), StatusCodes.Status200OK)]
    public Task<IActionResult> Monitor(Guid siteId, Guid articleId, [FromBody] ArticleMonitorStatus status, CancellationToken cancellationToken)
        => Execute(async () => { var user = await RequireArticleWriterAsync(siteId, articleId, cancellationToken); var result = await store.SetMonitorStatusAsync(user.TenantId, siteId, articleId, user.UserId, status, cancellationToken); return result is null ? NotFound() : Ok(result); });

    /// <summary>登记一条人工发布 URL。</summary>
    [HttpPost("{articleId:guid}/publication-links")]
    [ProducesResponseType(typeof(ArticlePublicationLinkView), StatusCodes.Status201Created)]
    public Task<IActionResult> AddLink(Guid siteId, Guid articleId, [FromBody] ArticlePublicationLinkWrite request, CancellationToken cancellationToken)
        => Execute(async () => { var user = await RequireArticleWriterAsync(siteId, articleId, cancellationToken); return StatusCode(201, await store.AddPublicationLinkAsync(user.TenantId, siteId, articleId, user.UserId, request, cancellationToken)); });

    /// <summary>停用文章发布 URL。</summary>
    [HttpPatch("{articleId:guid}/publication-links/{linkId:guid}")]
    [ProducesResponseType(typeof(ArticlePublicationLinkView), StatusCodes.Status200OK)]
    public Task<IActionResult> DisableLink(Guid siteId, Guid articleId, Guid linkId, CancellationToken cancellationToken)
        => Execute(async () => { var user = await RequireArticleWriterAsync(siteId, articleId, cancellationToken); var result = await store.DisablePublicationLinkAsync(user.TenantId, siteId, articleId, linkId, user.UserId, cancellationToken); return result is null ? NotFound() : Ok(result); });

    /// <summary>导出当前版本：默认 Markdown（与旧行为一致），format=docx 时导出 Word 文档。</summary>
    /// <remarks>docx 保留标题层级、段落、粗体 / 斜体、有序 / 无序列表、引用、超链接与表格；同一租户的平台上传图片嵌入文档，外链图片不下载，降级为链接文字。</remarks>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="articleId">文章标识。</param>
    /// <param name="format">导出格式：markdown（默认）或 docx；其他取值返回 400。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    [HttpGet("{articleId:guid}/export")]
    // Response content types are documented only: a [Produces] filter would turn JSON error bodies (400/401/404) into 406.
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK, "text/markdown", ArticleDocxExporter.ContentType)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Export(Guid siteId, Guid articleId, [FromQueryJsonEnum] ArticleExportFormat? format, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            if (!ModelState.IsValid)
                return BadRequest(new { error = new { code = "INVALID_EXPORT_FORMAT", message = "导出格式只支持 markdown 或 docx。" } });
            var user = RequireUser();
            var result = await store.GetArticleAsync(user.TenantId, siteId, articleId, cancellationToken);
            if (result?.CurrentVersion is null) return NotFound();
            var version = result.CurrentVersion;
            if (format != ArticleExportFormat.Docx)
                return File(System.Text.Encoding.UTF8.GetBytes($"# {version.Title}\n\n{version.BodyMarkdown}"), "text/markdown", $"article-{articleId:N}.md");
            var imageIds = ArticleMarkdown.ImageUrls(version.BodyMarkdown)
                .Select(ArticleMarkdown.PlatformImageId).OfType<Guid>().Distinct().ToArray();
            var binaries = imageIds.Length == 0
                ? new Dictionary<Guid, ContentImageBinary>()
                : await images.LoadAsync(user.TenantId, imageIds, cancellationToken);
            return File(ArticleDocxExporter.Export(version.Title, version.BodyMarkdown, binaries), ArticleDocxExporter.ContentType, $"article-{articleId:N}.docx");
        });

    private async Task<(Guid TenantId, Guid UserId, string Role)> RequireArticleWriterAsync(Guid siteId, Guid articleId, CancellationToken cancellationToken)
    {
        var user = RequireWriter();
        var article = await store.GetArticleAsync(user.TenantId, siteId, articleId, cancellationToken);
        if (article is null) throw new CollectorStoreException("RESOURCE_NOT_FOUND", "文章不存在。", 404);
        if (user.Role.Equals("member", StringComparison.OrdinalIgnoreCase) && article.Article.CreatedBy != user.UserId)
            throw new CollectorStoreException("CONTENT_FORBIDDEN", "成员只能修改自己创建的文章。", 403);
        return user;
    }
}
