using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Jogeoai.Api.Controllers;

/// <summary>引用来源站点图标：由后端抓取并缓存公开站点的位图图标，前端统一通过本接口展示。</summary>
[ApiController]
[Route("api/v1/site-icons")]
public sealed class SiteIconsController(ISiteIconService icons) : ControllerBase
{
    /// <summary>found 响应的浏览器缓存头（7 天）。</summary>
    public const string FoundCacheControl = "private, max-age=604800";

    /// <summary>404 响应的浏览器缓存头（不缓存，每次回源确认；服务端从库缓存直接返回）。</summary>
    public const string MissingCacheControl = "private, no-cache";

    /// <summary>读取站点图标。</summary>
    /// <remarks>
    /// 需要登录会话（Cookie 或 Bearer）。先按 <c>SiteIcons:DomainAliases</c> 解析别名，再按顺序获取：未过期缓存 → 首页 HTML 声明的图标 →
    /// <c>/favicon.ico</c> →（可注册域名）<c>www.</c> 子域名的同样步骤 → 第三方图标服务（按 <c>SiteIcons:ThirdParty</c> 配置依次尝试，丢弃占位图）
    /// →（子域名）借用其可注册域名的图标。只返回经魔数识别的 png / x-icon / gif / jpeg / webp / bmp，
    /// 拒绝 HTML 与 SVG。成功与失败结果都缓存在全局表 <c>site_icons</c>（迁移 028、029）。
    /// 响应总带 <c>X-Content-Type-Options: nosniff</c> 与 <c>Content-Security-Policy: default-src 'none'</c>；
    /// 200 带 <c>Cache-Control: private, max-age=604800</c>，404 带 <c>Cache-Control: private, no-cache</c>。
    /// </remarks>
    /// <param name="domain">站点 ASCII 主机名（如 example.com）：大小写不敏感、可带末尾点、长度 ≤ 253、每段为 [a-z0-9-] 且至少两段；不能是 IP、localhost、.local 或 .internal。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <response code="200">图标字节，Content-Type 为识别出的真实图片类型。</response>
    /// <response code="400">INVALID_SITE_DOMAIN：域名不合法，不发出出站请求。</response>
    /// <response code="401">AUTHENTICATION_REQUIRED：未登录。</response>
    /// <response code="404">SITE_ICON_NOT_FOUND：所有来源都拿不到图标。</response>
    [HttpGet("{domain}")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK,
        "image/png", "image/x-icon", "image/gif", "image/jpeg", "image/webp", "image/bmp")]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get([FromRoute] string domain, CancellationToken cancellationToken)
    {
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.ContentSecurityPolicy = "default-src 'none'";
        if (SessionAuthenticationMiddleware.GetContext(HttpContext) is null)
            return Error(401, "AUTHENTICATION_REQUIRED", "请先登录。");
        if (!SiteIconRules.TryNormalizeDomain(domain, out var normalized))
            return Error(400, "INVALID_SITE_DOMAIN", "站点域名不合法。");

        SiteIconRecord record;
        try { record = await icons.GetAsync(normalized, cancellationToken); }
        catch (CollectorStoreException exception)
        { return StatusCode(exception.StatusCode, StoreErrorResponse.Body(exception)); }

        if (record.IsFound)
        {
            Response.Headers.CacheControl = FoundCacheControl;
            return File(record.Bytes!, record.ContentType!);
        }
        Response.Headers.CacheControl = MissingCacheControl;
        return Error(404, "SITE_ICON_NOT_FOUND", "没有找到该站点的图标。");
    }

    private ObjectResult Error(int status, string code, string message) =>
        StatusCode(status, new { error = new { code, message } });
}
