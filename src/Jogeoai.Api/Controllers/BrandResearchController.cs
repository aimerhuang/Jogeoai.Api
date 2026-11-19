using Jogeoai.Api;
using Jogeoai.Api.Authorization;
using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Jogeoai.Api.Controllers;

/// <summary>管理品牌研究候选结果和引导完成提交。</summary>
[ApiController]
[Route("api/v1/sites/{siteId:guid}/brand-research")]
public sealed class BrandResearchController(IResearchStore store, TenantContextResolver tenantContext) : ControllerBase
{
    /// <summary>异步发起一次品牌研究。</summary>
    /// <param name="siteId">品牌站点标识。</param>
    /// <param name="request">官网地址、品牌名称和候选数量限制。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpPost]
    [ProducesResponseType(typeof(BrandResearchAccepted), StatusCodes.Status202Accepted)]
    public async Task<ActionResult<BrandResearchAccepted>> Start(Guid siteId, [FromBody] StartBrandResearchRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryIdentity(out var tenantId, out var userId, out var failure)) return failure!;
        try
        {
            TenantWriteAccess.Require(HttpContext, tenantId, requireSession: true);
            return Accepted(await store.EnqueueAsync(tenantId, siteId, userId, request, cancellationToken));
        }
        catch (ArgumentException exception) { return BadRequest(new { error = new { code = "INVALID_RESEARCH_REQUEST", message = exception.Message } }); }
        catch (CollectorStoreException exception) { return StatusCode(exception.StatusCode, new { error = new { code = exception.Code, message = exception.Message } }); }
    }

    /// <summary>查询品牌研究任务状态和候选结果。</summary>
    /// <param name="siteId">品牌站点标识。</param>
    /// <param name="jobId">研究任务标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpGet("{jobId:guid}")]
    [ProducesResponseType(typeof(BrandResearchJobView), StatusCodes.Status200OK)]
    public async Task<ActionResult<BrandResearchJobView>> Get(Guid siteId, Guid jobId, CancellationToken cancellationToken)
    {
        if (!TryIdentity(out var tenantId, out _, out var failure)) return failure!;
        var result = await store.GetAsync(tenantId, siteId, jobId, cancellationToken);
        return result is null ? NotFound(new { error = new { code = "RESOURCE_NOT_FOUND", message = "研究任务不存在。" } }) : Ok(result);
    }

    /// <summary>取消尚未完成的品牌研究任务。</summary>
    /// <param name="siteId">品牌站点标识。</param>
    /// <param name="jobId">研究任务标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpPost("{jobId:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid siteId, Guid jobId, CancellationToken cancellationToken)
    {
        if (!TryIdentity(out var tenantId, out _, out var failure)) return failure!;
        try
        {
            TenantWriteAccess.Require(HttpContext, tenantId, requireSession: true);
            return await store.CancelAsync(tenantId, siteId, jobId, cancellationToken)
                ? NoContent()
                : NotFound(new { error = new { code = "RESOURCE_NOT_FOUND", message = "研究任务不存在或已完成。" } });
        }
        catch (CollectorStoreException exception) { return StatusCode(exception.StatusCode, new { error = new { code = exception.Code, message = exception.Message } }); }
    }

    private bool TryIdentity(out Guid tenantId, out Guid userId, out ActionResult? failure)
    {
        tenantId = Guid.Empty;
        userId = Guid.Empty;
        var identity = SessionAuthenticationMiddleware.GetContext(HttpContext);
        if (identity is not null)
        {
            try
            {
                tenantId = tenantContext.Resolve(HttpContext);
                userId = identity.User.UserId;
                failure = null;
                return true;
            }
            catch (CollectorStoreException exception)
            {
                failure = StatusCode(exception.StatusCode, new { error = new { code = exception.Code, message = exception.Message } });
                return false;
            }
        }
        failure = Unauthorized(new { error = new { code = "AUTHENTICATION_REQUIRED", message = "请先登录。" } });
        return false;
    }
}
