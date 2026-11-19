using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Jogeoai.Api.Controllers;

/// <summary>登录工作空间后的已选问题每日监控。</summary>
[ApiController]
[Route("api/v1/sites/{siteId:guid}/prompt-monitoring")]
public sealed class PromptMonitoringController(IPromptMonitoringService monitoring, TenantContextResolver tenants) : ControllerBase
{
    /// <summary>幂等启动首轮采集和每日计划；重复登录不会重复创建。</summary>
    [HttpPost]
    [ProducesResponseType(typeof(PromptMonitoringView), StatusCodes.Status200OK)]
    public Task<ActionResult<PromptMonitoringView>> Start(Guid siteId, CancellationToken cancellationToken)
        => Execute(siteId, true, cancellationToken);

    /// <summary>查询真实监控进度和下次执行时间。</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PromptMonitoringView), StatusCodes.Status200OK)]
    public Task<ActionResult<PromptMonitoringView>> Get(Guid siteId, CancellationToken cancellationToken)
        => Execute(siteId, false, cancellationToken);

    /// <summary>
    /// 预估新增监测问题后每轮（每日）的检测次数与积分，口径与每日监控预留一致；只读，不创建计划或任务。
    /// 余额不足只体现在 shortfall，不作为阻断项；blockers 列出阻止创建的原因（MONITORING_TOO_LARGE、PLAN_LIMIT_EXCEEDED、
    /// MONITORING_TARGETS_REQUIRED）。viewer 返回 403。
    /// </summary>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="request">新增问题数与平台范围。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpPost("estimate")]
    [ProducesResponseType(typeof(PromptMonitoringEstimateView), StatusCodes.Status200OK)]
    public async Task<ActionResult<PromptMonitoringEstimateView>> Estimate(Guid siteId, [FromBody] PromptMonitoringEstimateRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = tenants.Resolve(HttpContext);
            var identity = SessionAuthenticationMiddleware.GetContext(HttpContext);
            var membership = identity?.User.Memberships.FirstOrDefault(item => item.TenantId == tenantId && item.Status == "active");
            // Same write boundary as ManagementController.TenantForWrite: viewers cannot create prompts, nor see the shared balance.
            if (membership?.Role.Equals("viewer", StringComparison.OrdinalIgnoreCase) == true)
                throw new CollectorStoreException("CONTENT_FORBIDDEN", "当前角色无权修改提问。", 403);
            return Ok(await monitoring.EstimateAsync(tenantId, siteId, request, cancellationToken));
        }
        catch (CollectorStoreException error)
        { return StatusCode(error.StatusCode, StoreErrorResponse.Body(error)); }
    }

    private async Task<ActionResult<PromptMonitoringView>> Execute(Guid siteId, bool start, CancellationToken ct)
    {
        try
        {
            var tenantId = tenants.Resolve(HttpContext);
            var identity = SessionAuthenticationMiddleware.GetContext(HttpContext);
            var membership = identity?.User.Memberships.FirstOrDefault(item => item.TenantId == tenantId && item.Status == "active");
            // Viewers may read monitoring state but must not create a plan or
            // trigger the first billable run through the shared POST lifecycle.
            var result = start && !string.Equals(membership?.Role, "viewer", StringComparison.OrdinalIgnoreCase)
                ? await monitoring.EnsureAsync(tenantId, siteId, ct)
                : await monitoring.GetAsync(tenantId, siteId, ct);
            // Prompt monitoring remains readable to viewers, but the shared
            // team balance and credit settlement details follow the same
            // visibility boundary as /credits. Do not turn the redaction into
            // a zero value because zero is a meaningful balance.
            if (membership?.Role.Equals("viewer", StringComparison.OrdinalIgnoreCase) == true)
                result = result with { Billing = null };
            return Ok(result);
        }
        catch (CollectorStoreException error)
        { return StatusCode(error.StatusCode, new { error = new { code = error.Code, message = error.Message } }); }
    }
}
