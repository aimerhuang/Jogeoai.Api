using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Jogeoai.Api.Controllers;

/// <summary>品牌 App 渠道（每周一次）的采集状态。</summary>
/// <param name="status">App 渠道采集状态查询。</param>
/// <param name="tenants">租户解析。</param>
[ApiController]
[Route("api/v1/sites/{siteId:guid}/app-collection")]
public sealed class AppCollectionStatusController(IAppCollectionStatusQuery status, TenantContextResolver tenants) : ControllerBase
{
    /// <summary>
    /// 读取品牌每个已启用 App 目标的采集状态：当前周期（包含现在的那个周期）的起止、计划数、已采集数、进行中数、未采集数及原因分布、
    /// 不进入 App 渠道的提问数，预计首次结果时间，以及最近一个已结束周期的同样统计。所有角色可读（含 viewer）。
    /// 团队未开放 App 渠道、或迁移 049 未部署时返回空的目标列表。只读，不创建计划或任务。
    /// </summary>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <response code="200">App 渠道采集状态。</response>
    /// <response code="401">未登录（AUTHENTICATION_REQUIRED）。</response>
    /// <response code="403">当前用户不是该团队成员（TENANT_FORBIDDEN）。</response>
    /// <response code="404">品牌不存在、已归档或不属于当前团队（RESOURCE_NOT_FOUND）。</response>
    [HttpGet("status")]
    [ProducesResponseType(typeof(AppCollectionStatusView), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AppCollectionStatusView>> Get(Guid siteId, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = tenants.Resolve(HttpContext);
            return Ok(await status.GetAsync(tenantId, siteId, cancellationToken));
        }
        catch (CollectorStoreException error)
        { return StatusCode(error.StatusCode, StoreErrorResponse.Body(error)); }
    }
}
