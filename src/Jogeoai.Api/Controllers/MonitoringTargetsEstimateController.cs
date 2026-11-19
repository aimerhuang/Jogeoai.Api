using Jogeoai.Api.Authorization;
using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Jogeoai.Api.Controllers;

/// <summary>品牌监控目标（模型 × 回答模式 × 采集频率）配置的积分预估。</summary>
[ApiController]
[Route("api/v1/sites/{siteId:guid}/targets")]
public sealed class MonitoringTargetsEstimateController(IPromptMonitoringService monitoring, TenantContextResolver tenants) : ControllerBase
{
    /// <summary>
    /// 预估监控目标草稿保存后的每周与下一轮检测次数、积分；只计算，不保存目标、不创建计划或任务。
    /// 草稿校验与保存站点目标（PUT /targets）相同：回答模式不受支持返回 400 ANSWER_MODE_UNSUPPORTED，频率无效返回 400 FREQUENCY_INVALID，
    /// 目标（含回答模式）重复返回 400 DUPLICATE_TARGET；启用单元格超过 20 个不返回 400，而是在 blockers 中报告 MONITORING_TOO_LARGE。
    /// 只有已启用且可参与每日监控的目标计入预估，国内网页采集目标与停用目标被忽略。
    /// 下一轮取品牌时区凌晨 00:05（仅存在 2次/天 单元格时）与早上 09:01 中较近的时隙，并按采集频率过滤；余额不足只体现在 shortfall。viewer 返回 403。
    /// </summary>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="request">监控目标草稿。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpPost("estimate")]
    [ProducesResponseType(typeof(MonitoringTargetsEstimateView), StatusCodes.Status200OK)]
    public async Task<ActionResult<MonitoringTargetsEstimateView>> Estimate(Guid siteId, [FromBody] MonitoringTargetsEstimateRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = tenants.Resolve(HttpContext);
            // Same write boundary as PUT /targets: viewers cannot edit targets, nor see the shared balance.
            TenantWriteAccess.Require(HttpContext, tenantId, message: "当前角色无权修改监控目标。");
            return Ok(await monitoring.EstimateTargetsAsync(tenantId, siteId, request, cancellationToken));
        }
        catch (CollectorStoreException error)
        { return StatusCode(error.StatusCode, StoreErrorResponse.Body(error)); }
    }
}
