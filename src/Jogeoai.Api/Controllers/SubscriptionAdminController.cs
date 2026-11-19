using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Jogeoai.Api.Controllers;

/// <summary>系统管理员维护卡密批次、租户订阅指派与套餐目录；未登录 401、非系统管理员 403，依赖迁移 024。</summary>
[ApiController]
[Route("api/v1/admin")]
public sealed class SubscriptionAdminController(ISubscriptionStore store, IResearchStore adminStore) : ControllerBase
{
    /// <summary>批量生成卡密；响应一次性返回明文数组，数据库只保存 SHA-256 与末 4 位，之后无法再次查看明文。</summary>
    /// <param name="request">套餐编码、月数（1–36）、数量（1–500）、兑换截止时间、备注。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpPost("plan-codes/batches")]
    [ProducesResponseType(typeof(PlanCodeBatchResult), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<PlanCodeBatchResult>> CreateBatch([FromBody] PlanCodeBatchRequest request, CancellationToken cancellationToken)
    {
        if (await SystemAdministratorAccess.RequireAsync(this, adminStore, cancellationToken) is { } failure) return failure;
        try { return StatusCode(StatusCodes.Status201Created, await store.CreateCodeBatchAsync(request, Actor(), cancellationToken)); }
        catch (CollectorStoreException exception) { return Error(exception); }
    }

    /// <summary>分页查询卡密（末 4 位、状态、兑换租户与时间）；可按批次、状态（unused / redeemed / revoked / expired）、套餐过滤。</summary>
    /// <param name="batchId">批次标识。</param>
    /// <param name="status">状态过滤。</param>
    /// <param name="planCode">套餐编码过滤。</param>
    /// <param name="cursor">上页返回的游标。</param>
    /// <param name="limit">每页数量，范围 1–100。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpGet("plan-codes")]
    [ProducesResponseType(typeof(PlanCodePage), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<PlanCodePage>> ListCodes([FromQuery] Guid? batchId, [FromQuery] string? status, [FromQuery] string? planCode,
        [FromQuery] string? cursor, [FromQuery] int limit = 50, CancellationToken cancellationToken = default)
    {
        if (await SystemAdministratorAccess.RequireAsync(this, adminStore, cancellationToken) is { } failure) return failure;
        try { return Ok(await store.ListCodesAsync(batchId, status, planCode, cursor, limit, cancellationToken)); }
        catch (CollectorStoreException exception) { return Error(exception); }
    }

    /// <summary>作废未使用的卡密；已兑换返回 409 PLAN_CODE_USED，已作废幂等返回。</summary>
    /// <param name="codeId">卡密标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpPost("plan-codes/{codeId:guid}/revoke")]
    [ProducesResponseType(typeof(PlanCodeView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PlanCodeView>> RevokeCode(Guid codeId, CancellationToken cancellationToken)
    {
        if (await SystemAdministratorAccess.RequireAsync(this, adminStore, cancellationToken) is { } failure) return failure;
        try { return Ok(await store.RevokeCodeAsync(codeId, Actor(), cancellationToken)); }
        catch (CollectorStoreException exception) { return Error(exception); }
    }

    /// <summary>查看任一租户的订阅详情、用量与最近 50 条订阅事件。</summary>
    /// <param name="tenantId">租户标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpGet("tenants/{tenantId:guid}/subscription")]
    [ProducesResponseType(typeof(AdminTenantSubscriptionView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdminTenantSubscriptionView>> GetTenantSubscription(Guid tenantId, CancellationToken cancellationToken)
    {
        if (await SystemAdministratorAccess.RequireAsync(this, adminStore, cancellationToken) is { } failure) return failure;
        try { return Ok(await store.GetForAdminAsync(tenantId, cancellationToken)); }
        catch (CollectorStoreException exception) { return Error(exception); }
    }

    /// <summary>
    /// 为租户指派套餐：规则与卡密升级相同，但允许降级（立即生效，不清零、不发放）。
    /// months 与 expiresAt 二选一；目标为 free_trial 时两者都省略。停用的套餐返回 404 PLAN_NOT_FOUND。
    /// </summary>
    /// <param name="tenantId">租户标识。</param>
    /// <param name="request">目标套餐、时长或到期时间、原因。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpPost("tenants/{tenantId:guid}/subscription")]
    [ProducesResponseType(typeof(TenantSubscriptionView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TenantSubscriptionView>> AssignTenantSubscription(Guid tenantId, [FromBody] AssignSubscriptionRequest request,
        CancellationToken cancellationToken)
    {
        if (await SystemAdministratorAccess.RequireAsync(this, adminStore, cancellationToken) is { } failure) return failure;
        try { return Ok(await store.AssignAsync(tenantId, Actor(), request, cancellationToken)); }
        catch (CollectorStoreException exception) { return Error(exception); }
    }

    /// <summary>修改套餐目录的上限、月度额度、一次性赠送、文案、功能标记、推荐标记与启用状态；编码不可新增或删除，修改不追溯已发放额度。</summary>
    /// <param name="code">套餐编码。</param>
    /// <param name="update">需要修改的字段；limits 与 features 提供时整体替换。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpPut("plans/{code}")]
    [ProducesResponseType(typeof(SubscriptionPlanView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SubscriptionPlanView>> UpdatePlan(string code, [FromBody] SubscriptionPlanUpdate update,
        CancellationToken cancellationToken)
    {
        if (await SystemAdministratorAccess.RequireAsync(this, adminStore, cancellationToken) is { } failure) return failure;
        try { return Ok(await store.UpdatePlanAsync(code, update, Actor(), cancellationToken)); }
        catch (CollectorStoreException exception) { return Error(exception); }
    }

    private Guid Actor() => SessionAuthenticationMiddleware.GetContext(HttpContext)!.User.UserId;
    private ObjectResult Error(CollectorStoreException exception) => StatusCode(exception.StatusCode, StoreErrorResponse.Body(exception));
}
