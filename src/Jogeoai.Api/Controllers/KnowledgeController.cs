using System.Globalization;
using Jogeoai.Api;
using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Jogeoai.Api.Controllers;

/// <summary>管理租户和品牌知识资料，并提供可追溯检索上下文。</summary>
[ApiController]
[Route("api/v1")]
public sealed class KnowledgeController(IKnowledgeStore store, IKnowledgeRetrievalService retrieval,
    TenantContextResolver tenantContext) : ControllerBase
{
    /// <summary>创建公司级资料文件夹。</summary>
    /// <param name="request">文件夹名称和可选父目录。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpPost("knowledge/folders")]
    [ProducesResponseType(typeof(KnowledgeFolderView), StatusCodes.Status201Created)]
    public Task<IActionResult> CreateTenantFolder([FromBody] KnowledgeFolderWrite request, CancellationToken cancellationToken)
        => CreateFolder(null, request, cancellationToken);

    /// <summary>创建品牌级资料文件夹。</summary>
    /// <param name="siteId">品牌标识。</param>
    /// <param name="request">文件夹名称和可选父目录。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpPost("sites/{siteId:guid}/knowledge/folders")]
    [ProducesResponseType(typeof(KnowledgeFolderView), StatusCodes.Status201Created)]
    public Task<IActionResult> CreateSiteFolder(Guid siteId, [FromBody] KnowledgeFolderWrite request, CancellationToken cancellationToken)
        => CreateFolder(siteId, request, cancellationToken);

    /// <summary>列出公司级资料文件夹。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpGet("knowledge/folders")]
    public Task<IActionResult> ListTenantFolders(CancellationToken cancellationToken) => ListFolders(null, cancellationToken);

    /// <summary>列出品牌级资料文件夹。</summary>
    /// <param name="siteId">品牌标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpGet("sites/{siteId:guid}/knowledge/folders")]
    public Task<IActionResult> ListSiteFolders(Guid siteId, CancellationToken cancellationToken) => ListFolders(siteId, cancellationToken);

    /// <summary>重命名、移动或归档资料文件夹。</summary>
    /// <param name="folderId">文件夹标识。</param>
    /// <param name="request">需要修改的名称、父目录、移动到根目录或归档标记。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpPatch("knowledge/folders/{folderId:guid}")]
    public Task<IActionResult> UpdateFolder(Guid folderId, [FromBody] KnowledgeFolderUpdate request, CancellationToken cancellationToken)
        => UpdateFolderScoped(folderId, null, request, cancellationToken);

    /// <summary>在指定品牌范围内重命名、移动或归档资料文件夹。</summary>
    /// <param name="siteId">品牌标识。</param>
    /// <param name="folderId">文件夹标识。</param>
    /// <param name="request">文件夹变更内容。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpPatch("sites/{siteId:guid}/knowledge/folders/{folderId:guid}")]
    [ProducesResponseType(typeof(KnowledgeFolderView), StatusCodes.Status200OK)]
    public Task<IActionResult> UpdateSiteFolder(Guid siteId, Guid folderId, [FromBody] KnowledgeFolderUpdate request,
        CancellationToken cancellationToken) => UpdateFolderScoped(folderId, siteId, request, cancellationToken);

    /// <summary>上传公司级资料（不再推荐，品牌事实请改用品牌知识库）；接口只接收首期支持的 PDF、DOCX、Markdown 和 TXT。</summary>
    /// <param name="form">文件和资料元数据。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <response code="409">KNOWLEDGE_DOCUMENT_MANAGED_BY_ITEM：指定的资料由品牌知识库条目自动维护，不能上传新版本。</response>
    [HttpPost("knowledge/documents")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(110L * 1024 * 1024)]
    [ProducesResponseType(typeof(KnowledgeDocumentView), StatusCodes.Status202Accepted)]
    [Obsolete("文件资料上传入口已从品牌信息页移除，品牌事实请改用品牌知识库 /api/v1/sites/{siteId}/knowledge/items；本接口仅为兼容保留。")]
    public Task<IActionResult> UploadTenant([FromForm] KnowledgeUploadForm form, CancellationToken cancellationToken)
        => Upload(null, form, cancellationToken);

    /// <summary>上传当前品牌级资料（不再推荐，品牌事实请改用品牌知识库）。</summary>
    /// <param name="siteId">品牌标识。</param>
    /// <param name="form">资料文件和元数据。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <response code="409">KNOWLEDGE_DOCUMENT_MANAGED_BY_ITEM：指定的资料由品牌知识库条目自动维护，不能上传新版本。</response>
    [HttpPost("sites/{siteId:guid}/knowledge/documents")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(110L * 1024 * 1024)]
    [ProducesResponseType(typeof(KnowledgeDocumentView), StatusCodes.Status202Accepted)]
    [Obsolete("文件资料上传入口已从品牌信息页移除，品牌事实请改用品牌知识库 /api/v1/sites/{siteId}/knowledge/items；本接口仅为兼容保留。")]
    public Task<IActionResult> UploadSite(Guid siteId, [FromForm] KnowledgeUploadForm form, CancellationToken cancellationToken)
        => Upload(siteId, form, cancellationToken);

    /// <summary>列出当前租户的公司级资料。</summary>
    /// <param name="cursor">上一页返回的不透明游标。</param>
    /// <param name="limit">每页数量，范围 1–100。</param>
    /// <param name="status">可选资料状态。</param>
    /// <param name="tag">可选资料标签。</param>
    /// <param name="documentType">可选资料类型。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpGet("knowledge/documents")]
    [ProducesResponseType(typeof(CursorPage<KnowledgeDocumentView>), StatusCodes.Status200OK)]
    public Task<IActionResult> ListTenant([FromQuery] string? cursor, [FromQuery] int limit = 50,
        [FromQuery] string? status = null, [FromQuery] string? tag = null, [FromQuery] string? documentType = null,
        CancellationToken cancellationToken = default)
        => List(null, cursor, limit, null, status, tag, documentType, cancellationToken);

    /// <summary>列出当前品牌资料及可见的公司级资料。</summary>
    /// <param name="siteId">品牌标识。</param>
    /// <param name="cursor">上一页游标。</param>
    /// <param name="limit">每页数量，范围 1–100。</param>
    /// <param name="scopeMode">default 或 brand_only。</param>
    /// <param name="status">可选资料状态。</param>
    /// <param name="tag">可选资料标签。</param>
    /// <param name="documentType">可选资料类型。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpGet("sites/{siteId:guid}/knowledge/documents")]
    [ProducesResponseType(typeof(CursorPage<KnowledgeDocumentView>), StatusCodes.Status200OK)]
    public Task<IActionResult> ListSite(Guid siteId, [FromQuery] string? cursor, [FromQuery] int limit = 50,
        [FromQuery] string? scopeMode = null, [FromQuery] string? status = null,
        [FromQuery] string? tag = null, [FromQuery] string? documentType = null,
        CancellationToken cancellationToken = default)
        => List(siteId, cursor, limit, scopeMode, status, tag, documentType, cancellationToken);

    /// <summary>读取资料及当前版本的处理状态。</summary>
    /// <param name="documentId">资料标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpGet("knowledge/documents/{documentId:guid}")]
    [ProducesResponseType(typeof(KnowledgeDocumentView), StatusCodes.Status200OK)]
    public Task<IActionResult> Get(Guid documentId, CancellationToken cancellationToken)
        => GetScoped(documentId, null, cancellationToken);

    /// <summary>读取指定品牌范围内的资料及当前版本状态。</summary>
    /// <param name="siteId">品牌标识。</param>
    /// <param name="documentId">资料标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpGet("sites/{siteId:guid}/knowledge/documents/{documentId:guid}")]
    [ProducesResponseType(typeof(KnowledgeDocumentView), StatusCodes.Status200OK)]
    public Task<IActionResult> GetSite(Guid siteId, Guid documentId, CancellationToken cancellationToken)
        => GetScoped(documentId, siteId, cancellationToken);

    /// <summary>鉴权后流式下载资料原文件。</summary>
    /// <param name="documentId">资料标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpGet("knowledge/documents/{documentId:guid}/download")]
    public Task<IActionResult> Download(Guid documentId, CancellationToken cancellationToken)
        => DownloadScoped(documentId, null, cancellationToken);

    /// <summary>在指定品牌范围内鉴权并流式下载资料原文件。</summary>
    /// <param name="siteId">品牌标识。</param>
    /// <param name="documentId">资料标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpGet("sites/{siteId:guid}/knowledge/documents/{documentId:guid}/download")]
    public Task<IActionResult> DownloadSite(Guid siteId, Guid documentId, CancellationToken cancellationToken)
        => DownloadScoped(documentId, siteId, cancellationToken);

    /// <summary>更新资料元数据并使用配置版本进行并发保护。</summary>
    /// <param name="documentId">资料标识。</param>
    /// <param name="request">需要修改的元数据。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <response code="409">KNOWLEDGE_DOCUMENT_MANAGED_BY_ITEM：资料由品牌知识库条目自动维护，请到品牌知识库编辑或删除对应条目。</response>
    [HttpPatch("knowledge/documents/{documentId:guid}")]
    [ProducesResponseType(typeof(KnowledgeDocumentView), StatusCodes.Status200OK)]
    public Task<IActionResult> Update(Guid documentId, [FromBody] KnowledgeDocumentUpdate request,
        CancellationToken cancellationToken) => Execute(async () =>
        await UpdateScoped(documentId, null, request, cancellationToken));

    /// <summary>在指定品牌范围内更新资料元数据。</summary>
    /// <param name="siteId">品牌标识。</param>
    /// <param name="documentId">资料标识。</param>
    /// <param name="request">需要修改的元数据。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <response code="409">KNOWLEDGE_DOCUMENT_MANAGED_BY_ITEM：资料由品牌知识库条目自动维护，请到品牌知识库编辑或删除对应条目。</response>
    [HttpPatch("sites/{siteId:guid}/knowledge/documents/{documentId:guid}")]
    [ProducesResponseType(typeof(KnowledgeDocumentView), StatusCodes.Status200OK)]
    public Task<IActionResult> UpdateSite(Guid siteId, Guid documentId, [FromBody] KnowledgeDocumentUpdate request,
        CancellationToken cancellationToken) => Execute(async () => await UpdateScoped(documentId, siteId, request, cancellationToken));

    /// <summary>重试当前资料的失败版本。</summary>
    /// <param name="documentId">资料标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <response code="409">KNOWLEDGE_DOCUMENT_MANAGED_BY_ITEM：资料由品牌知识库条目自动维护，请到品牌知识库编辑或删除对应条目。</response>
    [HttpPost("knowledge/documents/{documentId:guid}/retry")]
    [ProducesResponseType(typeof(KnowledgeDocumentView), StatusCodes.Status202Accepted)]
    public Task<IActionResult> Retry(Guid documentId, CancellationToken cancellationToken)
        => RetryScoped(documentId, null, cancellationToken);

    /// <summary>在指定品牌范围内重试资料的失败版本。</summary>
    /// <param name="siteId">品牌标识。</param>
    /// <param name="documentId">资料标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <response code="409">KNOWLEDGE_DOCUMENT_MANAGED_BY_ITEM：资料由品牌知识库条目自动维护，请到品牌知识库编辑或删除对应条目。</response>
    [HttpPost("sites/{siteId:guid}/knowledge/documents/{documentId:guid}/retry")]
    [ProducesResponseType(typeof(KnowledgeDocumentView), StatusCodes.Status202Accepted)]
    public Task<IActionResult> RetrySite(Guid siteId, Guid documentId, CancellationToken cancellationToken)
        => RetryScoped(documentId, siteId, cancellationToken);

    /// <summary>停用资料，使其立即从新检索中排除。</summary>
    /// <param name="documentId">资料标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <response code="409">KNOWLEDGE_DOCUMENT_MANAGED_BY_ITEM：资料由品牌知识库条目自动维护，请到品牌知识库编辑或删除对应条目。</response>
    [HttpPost("knowledge/documents/{documentId:guid}/disable")]
    [ProducesResponseType(typeof(KnowledgeDocumentView), StatusCodes.Status200OK)]
    public Task<IActionResult> Disable(Guid documentId, CancellationToken cancellationToken)
        => DisableScoped(documentId, null, cancellationToken);

    /// <summary>在指定品牌范围内停用资料。</summary>
    /// <param name="siteId">品牌标识。</param>
    /// <param name="documentId">资料标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <response code="409">KNOWLEDGE_DOCUMENT_MANAGED_BY_ITEM：资料由品牌知识库条目自动维护，请到品牌知识库编辑或删除对应条目。</response>
    [HttpPost("sites/{siteId:guid}/knowledge/documents/{documentId:guid}/disable")]
    [ProducesResponseType(typeof(KnowledgeDocumentView), StatusCodes.Status200OK)]
    public Task<IActionResult> DisableSite(Guid siteId, Guid documentId, CancellationToken cancellationToken)
        => DisableScoped(documentId, siteId, cancellationToken);

    /// <summary>读取当前租户的知识库容量使用情况。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpGet("knowledge/usage")]
    [ProducesResponseType(typeof(KnowledgeUsageView), StatusCodes.Status200OK)]
    public Task<IActionResult> Usage(CancellationToken cancellationToken) => Execute(async () => Ok(await store.GetUsageAsync(Tenant(), cancellationToken)));

    /// <summary>测试当前品牌可见的资料证据。</summary>
    /// <param name="siteId">品牌标识。</param>
    /// <param name="request">检索问题、范围和过滤器。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpPost("sites/{siteId:guid}/knowledge/search")]
    [ProducesResponseType(typeof(KnowledgeSearchResponse), StatusCodes.Status200OK)]
    public Task<IActionResult> Search(Guid siteId, [FromBody] KnowledgeSearchRequest request,
        CancellationToken cancellationToken) => Execute(async () => Ok(await retrieval.SearchAsync(Tenant(), siteId, request, cancellationToken)));

    /// <summary>创建含证据、缺口和冲突提示的写作上下文快照。</summary>
    /// <param name="siteId">品牌标识。</param>
    /// <param name="request">主题、问题、范围和上下文限制。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpPost("sites/{siteId:guid}/knowledge/context")]
    [ProducesResponseType(typeof(KnowledgeContextResponse), StatusCodes.Status200OK)]
    public Task<IActionResult> Context(Guid siteId, [FromBody] KnowledgeContextRequest request,
        CancellationToken cancellationToken) => Execute(async () => Ok(await retrieval.CreateContextAsync(Tenant(), siteId, request, cancellationToken)));

    /// <summary>读取当前租户保存的上下文命中快照。</summary>
    /// <param name="contextId">上下文标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpGet("knowledge/context/{contextId:guid}")]
    [ProducesResponseType(typeof(KnowledgeContextResponse), StatusCodes.Status200OK)]
    public Task<IActionResult> Context(Guid contextId, CancellationToken cancellationToken) => Execute(async () =>
    {
        var value = await retrieval.GetContextAsync(Tenant(), contextId, cancellationToken);
        return value is null ? NotFound(NotFoundBody()) : Ok(value);
    });

    private Task<IActionResult> Upload(Guid? siteId, KnowledgeUploadForm form, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            if (form.File is null || form.File.Length == 0) throw new CollectorStoreException("KNOWLEDGE_EMPTY_CONTENT", "请选择要上传的文件。", 400);
            var tenant = WriterTenant();
            // 上传新版本前先拒绝条目投影资料；Store 在锁内会再次检查，这里只是提前给出明确错误。
            if (form.DocumentId is Guid existingId && await store.GetDocumentAsync(tenant, existingId, cancellationToken) is { } existing)
                BrandKnowledgeRules.AssertDocumentNotItemManaged(existing.SourceKind);
            await using var content = form.File.OpenReadStream();
            var value = await store.CreateDocumentAsync(tenant, siteId, new KnowledgeUploadCommand(content,
                form.File.FileName, form.File.ContentType, form.Title, form.FolderId, form.DocumentType ?? "general",
                form.ProductKey, ParseTags(form.Tags), form.Language ?? "und", ParseDate(form.EffectiveAt), ParseDate(form.ExpiresAt),
                Request.Headers["Idempotency-Key"].FirstOrDefault(), form.DocumentId), cancellationToken);
            return siteId is null
                ? AcceptedAtAction(nameof(Get), new { documentId = value.DocumentId }, value)
                : AcceptedAtAction(nameof(GetSite), new { siteId, documentId = value.DocumentId }, value);
        });

    private Task<IActionResult> List(Guid? siteId, string? cursor, int limit, string? scopeMode,
        string? status, string? tag, string? documentType, CancellationToken cancellationToken)
        => Execute(async () => Ok(await store.ListDocumentsAsync(Tenant(), siteId, cursor, limit,
            new KnowledgeDocumentListFilter(ParseScopeMode(scopeMode), status, tag, documentType), cancellationToken)));

    private Task<IActionResult> CreateFolder(Guid? siteId, KnowledgeFolderWrite request, CancellationToken cancellationToken)
        => Execute(async () => StatusCode(StatusCodes.Status201Created,
            await store.CreateFolderAsync(WriterTenant(), siteId, request, cancellationToken)));

    private Task<IActionResult> ListFolders(Guid? siteId, CancellationToken cancellationToken)
        => Execute(async () => Ok(await store.ListFoldersAsync(Tenant(), siteId, cancellationToken)));

    private Task<IActionResult> UpdateFolderScoped(Guid folderId, Guid? siteId, KnowledgeFolderUpdate request,
        CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var existing = (await store.ListFoldersAsync(WriterTenant(), siteId, cancellationToken))
                .FirstOrDefault(item => item.FolderId == folderId);
            if (existing is null || existing.SiteId != siteId) return NotFound(NotFoundBody());
            var value = await store.UpdateFolderAsync(WriterTenant(), folderId, request, cancellationToken);
            return value is null || value.SiteId != siteId ? NotFound(NotFoundBody()) : Ok(value);
        });

    private Task<IActionResult> GetScoped(Guid documentId, Guid? siteId, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var value = await store.GetDocumentAsync(Tenant(), documentId, cancellationToken);
            return value is null || value.SiteId != siteId ? NotFound(NotFoundBody()) : Ok(value);
        });

    private Task<IActionResult> DownloadScoped(Guid documentId, Guid? siteId, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var value = await store.GetDocumentAsync(Tenant(), documentId, cancellationToken);
            if (value is null || value.SiteId != siteId || value.CurrentVersion is null) return NotFound(NotFoundBody());
            var stream = await store.OpenDocumentAsync(Tenant(), documentId, cancellationToken);
            if (stream is null) return NotFound(NotFoundBody());
            return File(stream, value.CurrentVersion.ContentType, SafeDownloadName(value.CurrentVersion.OriginalFileName), enableRangeProcessing: true);
        });

    private async Task<IActionResult> UpdateScoped(Guid documentId, Guid? siteId, KnowledgeDocumentUpdate request,
        CancellationToken cancellationToken)
    {
        var existing = await store.GetDocumentAsync(WriterTenant(), documentId, cancellationToken);
        if (existing is null || existing.SiteId != siteId) return NotFound(NotFoundBody());
        BrandKnowledgeRules.AssertDocumentNotItemManaged(existing.SourceKind);
        var value = await store.UpdateDocumentAsync(WriterTenant(), documentId, request, cancellationToken);
        return value is null || value.SiteId != siteId ? NotFound(NotFoundBody()) : Ok(value);
    }

    private Task<IActionResult> RetryScoped(Guid documentId, Guid? siteId, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var existing = await store.GetDocumentAsync(WriterTenant(), documentId, cancellationToken);
            if (existing is null || existing.SiteId != siteId) return NotFound(NotFoundBody());
            BrandKnowledgeRules.AssertDocumentNotItemManaged(existing.SourceKind);
            var value = await store.RetryDocumentAsync(WriterTenant(), documentId, cancellationToken);
            return value is null || value.SiteId != siteId ? NotFound(NotFoundBody()) : Accepted(value);
        });

    private Task<IActionResult> DisableScoped(Guid documentId, Guid? siteId, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var existing = await store.GetDocumentAsync(WriterTenant(), documentId, cancellationToken);
            if (existing is null || existing.SiteId != siteId) return NotFound(NotFoundBody());
            BrandKnowledgeRules.AssertDocumentNotItemManaged(existing.SourceKind);
            var value = await store.DisableDocumentAsync(WriterTenant(), documentId, cancellationToken);
            return value is null || value.SiteId != siteId ? NotFound(NotFoundBody()) : Ok(value);
        });

    private async Task<IActionResult> Execute(Func<Task<IActionResult>> action)
    {
        try { return await action(); }
        catch (CollectorStoreException exception) { return StatusCode(exception.StatusCode, new { error = new { code = exception.Code, message = exception.Message } }); }
    }

    private Guid Tenant() => tenantContext.Resolve(HttpContext);

    /// <summary>写操作使用的租户解析：已登录的 viewer 成员拒绝写入；开发环境的匿名 X-Tenant-Id 兼容桥保持不变。</summary>
    private Guid WriterTenant()
    {
        var tenant = Tenant();
        var identity = SessionAuthenticationMiddleware.GetContext(HttpContext);
        var membership = identity?.User.Memberships.FirstOrDefault(item => item.TenantId == tenant);
        if (membership is not null && membership.Role.Equals("viewer", StringComparison.OrdinalIgnoreCase))
            throw new CollectorStoreException("KNOWLEDGE_FORBIDDEN", "当前角色无权修改知识库资料。", 403);
        return tenant;
    }
    private static object NotFoundBody() => new { error = new { code = "RESOURCE_NOT_FOUND", message = "资料不存在。" } };
    private static string SafeDownloadName(string fileName) => fileName.Replace('"', '_').Replace('\r', '_').Replace('\n', '_');
    private static KnowledgeScopeMode ParseScopeMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "default" => KnowledgeScopeMode.Default,
        "brand_only" => KnowledgeScopeMode.BrandOnly,
        _ => throw new CollectorStoreException("INVALID_KNOWLEDGE_SCOPE", "资料范围必须为 default 或 brand_only。", 400)
    };
    private static DateTimeOffset? ParseDate(string? value) => string.IsNullOrWhiteSpace(value) ? null : DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var result) ? result.ToUniversalTime() : throw new CollectorStoreException("INVALID_KNOWLEDGE_DATES", "日期格式无效。", 400);
    private static IReadOnlyList<string>? ParseTags(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>知识库 multipart 上传字段。</summary>
public sealed class KnowledgeUploadForm
{
    /// <summary>文件内容。</summary>
    public IFormFile? File { get; set; }
    /// <summary>替换已有逻辑资料时的资料标识；省略时新建资料。</summary>
    public Guid? DocumentId { get; set; }
    /// <summary>资料标题。</summary>
    public string? Title { get; set; }
    /// <summary>目标文件夹。</summary>
    public Guid? FolderId { get; set; }
    /// <summary>资料类型。</summary>
    public string? DocumentType { get; set; }
    /// <summary>产品标识。</summary>
    public string? ProductKey { get; set; }
    /// <summary>逗号分隔的标签。</summary>
    public string? Tags { get; set; }
    /// <summary>资料语言。</summary>
    public string? Language { get; set; }
    /// <summary>生效时间 ISO-8601 字符串。</summary>
    public string? EffectiveAt { get; set; }
    /// <summary>失效时间 ISO-8601 字符串。</summary>
    public string? ExpiresAt { get; set; }
}
