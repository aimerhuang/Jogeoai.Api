using Jogeoai.Api;
using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Jogeoai.Api.Controllers;

/// <summary>查询 Cloro 消费者页面采集证据。</summary>
[ApiController]
[Route("api/v1/visibility-checks")]
public sealed class VisibilityChecksController(ICloroVisibilityStore store, IAnalyticsQueryStore analytics,
    TenantContextResolver tenantContext) : ControllerBase
{
    /// <summary>返回当前租户的一条 Cloro Check 及其引用。</summary>
    /// <param name="checkId">检查标识。</param>
    /// <param name="snapshotToken">可选的分析快照令牌。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpGet("{checkId:guid}")]
    [ProducesResponseType(typeof(CloroVisibilityCheckView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid checkId, [FromQuery] string? snapshotToken,
        CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = tenantContext.Resolve(HttpContext);
            var detail = await analytics.CheckDetailAsync(tenantId, checkId, snapshotToken, cancellationToken);
            if (detail is null) return NotFound(new { error = new { code = "RESOURCE_NOT_FOUND", message = "Check 不存在。" } });
            var legacy = await store.GetCheckAsync(tenantId, checkId, cancellationToken);
            return legacy is null ? Ok(detail) : Ok(legacy with { Analysis = detail.Analysis });
        }
        catch (CollectorStoreException exception)
        { return StatusCode(exception.StatusCode, new { error = new { code = exception.Code, message = exception.Message } }); }
    }
}
