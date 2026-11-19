using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Jogeoai.Api.Controllers;

/// <summary>文章编辑器图片：上传到平台对象存储，并按图片标识读取（同租户登录用户）。</summary>
[ApiController]
public sealed class ContentImagesController(
    IContentImageService images,
    TenantContextResolver tenantContext) : ContentControllerBase(tenantContext)
{
    /// <summary>上传一张编辑器图片（multipart/form-data 字段 file），返回可写入 Markdown 的平台图片地址。</summary>
    /// <remarks>只接受 PNG、JPEG、WebP、GIF，按文件头魔数识别（扩展名与客户端 Content-Type 不作为依据），最大 5 MB；超限返回 413 CONTENT_IMAGE_TOO_LARGE，格式不符返回 415 CONTENT_IMAGE_TYPE_UNSUPPORTED。迁移 041 未部署时返回 503。</remarks>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="file">图片文件。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    [HttpPost("api/v1/sites/{siteId:guid}/content/images")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(ArticleEditorLimits.ImageMaxBytes + 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = ArticleEditorLimits.ImageMaxBytes + 1024 * 1024)]
    [ProducesResponseType(typeof(ContentImageUploadResult), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> Upload(Guid siteId, IFormFile? file, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var user = RequireWriter();
            if (file is null || file.Length == 0)
                throw new CollectorStoreException("INVALID_REQUEST", "请选择要上传的图片文件。", 400);
            if (file.Length > ArticleEditorLimits.ImageMaxBytes)
                throw new CollectorStoreException(ArticleEditorErrorCodes.ImageTooLarge, "图片不能超过 5 MB。", 413);
            await using var input = file.OpenReadStream();
            var content = await ReadBoundedAsync(input, cancellationToken);
            var result = await images.UploadAsync(user.TenantId, siteId, user.UserId, content, cancellationToken);
            return StatusCode(StatusCodes.Status201Created, result);
        });

    /// <summary>读取编辑器图片：仅图片所属租户的登录成员可读，响应带 nosniff。</summary>
    /// <remarks>浏览器 img 标签无法携带 X-Tenant-Id：未提供该请求头时，只要当前用户是图片所属租户的有效成员即可读取；提供时必须与图片租户一致。其他情况统一返回 404，不暴露图片是否存在。</remarks>
    /// <param name="imageId">图片标识。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    [HttpGet("api/v1/content/images/{imageId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Read(Guid imageId, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var identity = SessionAuthenticationMiddleware.GetContext(HttpContext)
                ?? throw new CollectorStoreException("AUTHENTICATION_REQUIRED", "请先登录。", 401);
            var image = await images.FindAsync(imageId, cancellationToken);
            var requestedTenant = Request.Headers["X-Tenant-Id"].FirstOrDefault();
            if (image is null || !CanRead(identity.User.Memberships.Where(m => m.Status == "active").Select(m => m.TenantId), requestedTenant, image.TenantId))
                return NotFound(new { error = new { code = "RESOURCE_NOT_FOUND", message = "图片不存在。" } });
            var stream = await images.OpenAsync(image, cancellationToken);
            if (stream is null) return NotFound(new { error = new { code = "RESOURCE_NOT_FOUND", message = "图片不存在。" } });
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
            Response.Headers.CacheControl = "private, max-age=86400";
            return File(stream, image.ContentType);
        });

    /// <summary>图片读取的租户判定：请求头指定租户时必须与图片租户一致，且用户必须是图片租户的有效成员。</summary>
    /// <param name="activeTenants">当前用户的有效成员租户。</param>
    /// <param name="requestedTenant">X-Tenant-Id 请求头。</param>
    /// <param name="imageTenant">图片所属租户。</param>
    public static bool CanRead(IEnumerable<Guid> activeTenants, string? requestedTenant, Guid imageTenant)
    {
        if (!string.IsNullOrWhiteSpace(requestedTenant)
            && (!Guid.TryParse(requestedTenant, out var requested) || requested != imageTenant)) return false;
        return activeTenants.Contains(imageTenant);
    }

    private static async Task<ReadOnlyMemory<byte>> ReadBoundedAsync(Stream input, CancellationToken cancellationToken)
    {
        await using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await input.ReadAsync(chunk, cancellationToken);
            if (read == 0) break;
            if (buffer.Length + read > ArticleEditorLimits.ImageMaxBytes)
                throw new CollectorStoreException(ArticleEditorErrorCodes.ImageTooLarge, "图片不能超过 5 MB。", 413);
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
