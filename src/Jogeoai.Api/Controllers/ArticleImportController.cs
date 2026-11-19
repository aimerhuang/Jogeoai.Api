using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Jogeoai.Api.Controllers;

/// <summary>已发布文章 URL 的安全读取、预览和批量导入。</summary>
[ApiController]
[Route("api/v1/sites/{siteId:guid}/article-imports")]
public sealed class ArticleImportController(IContentStore store, TenantContextResolver tenantContext) : ContentControllerBase(tenantContext)
{
    /// <summary>读取最多 20 个 URL 并返回逐项结果。</summary>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="urls">待读取的公开链接。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    [HttpPost("preview")]
    [ProducesResponseType(typeof(ArticleImportPreviewView), StatusCodes.Status200OK)]
    public Task<IActionResult> Preview(Guid siteId, [FromBody] IReadOnlyList<string> urls, CancellationToken cancellationToken)
        => Execute(async () => { var user = RequireWriter(); return Ok(await store.PreviewImportAsync(user.TenantId, siteId, user.UserId, urls, cancellationToken)); });

    /// <summary>按预览摘要确认导入；导入不消费生文积分。</summary>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="request">导入预览和标题覆盖。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    [HttpPost("confirm")]
    [ProducesResponseType(typeof(IReadOnlyList<ArticleView>), StatusCodes.Status201Created)]
    public Task<IActionResult> Confirm(Guid siteId, [FromBody] ArticleImportConfirmRequest request, CancellationToken cancellationToken)
        => Execute(async () => { var user = RequireWriter(); var key = Request.Headers["Idempotency-Key"].FirstOrDefault() ?? string.Empty; return StatusCode(201, await store.ConfirmImportAsync(user.TenantId, siteId, user.UserId, request, key, cancellationToken)); });
}
