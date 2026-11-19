using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Jogeoai.Api.Controllers;

/// <summary>引用文章（URL）的「自有」标记：把 URL 登记为本品牌有效发布链接，或停用其全部有效发布链接。只读成员无权操作。</summary>
[ApiController]
[Route("api/v1/sites/{siteId:guid}/content/owned-urls")]
public sealed class ContentOwnedUrlsController(IContentStore store, TenantContextResolver tenantContext)
    : ContentControllerBase(tenantContext)
{
    /// <summary>标记 URL 为自有：已有有效发布链接时不变（unchanged）；只有已停用链接时恢复为有效、所属文章已归档则一并恢复（reactivated）；都没有时新建一篇导入文章与有效发布链接（created）。</summary>
    /// <remarks>
    /// 新建的导入文章出现在内容管理列表，追踪默认开启并进入文章监控计费口径（与「导入链接」相同）；不抓取网页、不消耗积分。
    /// 标记后不会回算历史「我的引用次数」：只统计登记之后的引用。官网 / 外部归属沿用服务端按品牌域名计算的规则。
    /// 非法 URL 返回 400 INVALID_SOURCE_URL；只读成员返回 403 CONTENT_FORBIDDEN；品牌不存在返回 404。
    /// </remarks>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="request">要标记的 URL 与可选标题。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    [HttpPost]
    [ProducesResponseType(typeof(OwnedUrlMarkResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Mark(Guid siteId, [FromBody] OwnedUrlMarkRequest request, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var user = RequireWriter();
            return Ok(await store.MarkOwnedUrlAsync(user.TenantId, siteId, user.UserId, request.Url, request.Title,
                cancellationToken));
        });

    /// <summary>移除 URL 的自有标记：停用本品牌下该 URL 摘要的全部有效发布链接（disabled）；导入文章因此不再有有效链接时同时归档，生成类文章只停用链接；没有有效链接时幂等返回 unchanged。</summary>
    /// <remarks>urlHash 取自引用文章榜行的 urlHash（六十四位十六进制），格式错误返回 400 INVALID_URL_HASH；只读成员返回 403 CONTENT_FORBIDDEN。</remarks>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="urlHash">规范化 URL 的 SHA-256 十六进制摘要。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    [HttpDelete("{urlHash}")]
    [ProducesResponseType(typeof(OwnedUrlMarkResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Unmark(Guid siteId, string urlHash, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var user = RequireWriter();
            return Ok(await store.UnmarkOwnedUrlAsync(user.TenantId, siteId, user.UserId, urlHash, cancellationToken));
        });
}
