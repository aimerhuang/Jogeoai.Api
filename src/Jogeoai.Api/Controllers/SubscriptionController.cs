using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Jogeoai.Api.Controllers;

/// <summary>套餐目录、当前租户订阅与卡密兑换。需要会话 Cookie 与 X-Tenant-Id；依赖迁移 024。</summary>
[ApiController]
[Route("api/v1")]
public sealed class SubscriptionController(ISubscriptionStore store, TenantContextResolver tenantContext) : ContentControllerBase(tenantContext)
{
    /// <summary>读取套餐目录（排序、上限、月度额度、一次性赠送、功能标记、推荐标记、isCurrent）；任意已登录角色可读。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpGet("plans")]
    [ProducesResponseType(typeof(IReadOnlyList<SubscriptionPlanView>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> Plans(CancellationToken cancellationToken)
        => Execute(async () => Ok(await store.ListPlansAsync(RequireUser().TenantId, cancellationToken)));

    /// <summary>读取当前租户订阅：套餐、来源、生效 / 到期、周期额度与已用、各上限与当前用量；任意已登录角色可读。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpGet("subscription")]
    [ProducesResponseType(typeof(TenantSubscriptionView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> Current(CancellationToken cancellationToken)
        => Execute(async () => Ok(await store.GetAsync(RequireUser().TenantId, cancellationToken)));

    /// <summary>
    /// 兑换卡密升级或续期：同套餐顺延、更高套餐立即切换并发放当月额度、更低套餐返回 409 PLAN_DOWNGRADE_NOT_ALLOWED。
    /// 仅团队所有者（owner）或管理员（admin）可兑换，其他角色返回 403 CONTENT_FORBIDDEN；错误尝试按租户与 IP 每小时限流（429 PLAN_REDEEM_RATE_LIMITED）。
    /// </summary>
    /// <param name="request">卡密明文。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpPost("subscription/redeem")]
    [ProducesResponseType(typeof(TenantSubscriptionView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> Redeem([FromBody] RedeemPlanCodeRequest request, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var user = RequireUser();
            // 父任务 FR-20 / 子任务 PRD §2：只有租户所有者或管理员可兑换；member、viewer 均 403。
            if (!user.Role.Equals("owner", StringComparison.OrdinalIgnoreCase) && !user.Role.Equals("admin", StringComparison.OrdinalIgnoreCase))
                throw new CollectorStoreException("CONTENT_FORBIDDEN", "只有团队所有者或管理员可以兑换卡密。", 403);
            var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return Ok(await store.RedeemAsync(user.TenantId, user.UserId, request?.Code ?? "", clientIp, cancellationToken));
        });
}
