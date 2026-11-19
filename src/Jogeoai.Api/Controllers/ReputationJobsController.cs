using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Jogeoai.Api.Controllers;

/// <summary>站点级口碑判定补录：把当前已发布分析版本中的历史有效回答登记为待判定，由情感 Worker 异步处理。</summary>
[ApiController]
[Route("api/v1/sites/{siteId:guid}/reputation-jobs")]
public sealed class ReputationJobsController(IReputationStore store, TenantContextResolver tenantContext) : ControllerBase
{
    /// <summary>
    /// 创建补录任务：只登记工作项、不立即调用模型；同幂等键同请求返回原任务，不同请求返回 409 REPUTATION_IDEMPOTENCY_CONFLICT。
    /// 情感 Worker 默认关闭，未启用时工作项保持待判定。口碑迁移 038 未部署时返回 503 REPUTATION_SCHEMA_NOT_MIGRATED。
    /// </summary>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="request">可选的采集时间范围。</param>
    /// <param name="idempotencyKey">必填的幂等键（不超过 191 字符）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpPost]
    [ProducesResponseType(typeof(ReputationJobView), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> Create(Guid siteId, [FromBody] ReputationJobRequest? request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var tenantId = tenantContext.Resolve(HttpContext);
            BrandWriteAccess.Require(HttpContext, tenantId);
            return await store.CreateJobAsync(tenantId, siteId, idempotencyKey ?? "", request ?? new ReputationJobRequest(),
                cancellationToken);
        }, 202);

    /// <summary>读取补录任务的实时进度（已完成、待处理、失败与未知计数）。</summary>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="jobId">补录任务标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpGet("{jobId:guid}")]
    [ProducesResponseType(typeof(ReputationJobView), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> Get(Guid siteId, Guid jobId, CancellationToken cancellationToken)
        => Execute(async () => await store.GetJobAsync(tenantContext.Resolve(HttpContext), siteId, jobId, cancellationToken));

    private async Task<IActionResult> Execute(Func<Task<object?>> action, int success = 200)
    {
        try
        {
            var value = await action();
            return value is null ? NotFound(new { error = new { code = "RESOURCE_NOT_FOUND", message = "资源不存在。" } })
                : StatusCode(success, value);
        }
        catch (CollectorStoreException exception)
        { return StatusCode(exception.StatusCode, new { error = new { code = exception.Code, message = exception.Message } }); }
    }
}
