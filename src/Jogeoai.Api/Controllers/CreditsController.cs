using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Jogeoai.Api.Controllers;

/// <summary>租户共享积分余额与账本查询。</summary>
[ApiController]
[Route("api/v1/credits")]
public sealed class CreditsController(ICreditStore credits, IResearchStore research, TenantContextResolver tenantContext) : ContentControllerBase(tenantContext)
{
    /// <summary>读取当前租户积分余额。</summary>
    [HttpGet]
    [ProducesResponseType(typeof(CreditAccountView), StatusCodes.Status200OK)]
    public Task<IActionResult> Account(CancellationToken cancellationToken)
        => Execute(async () => { var user = RequireUser(); if (user.Role.Equals("viewer", StringComparison.OrdinalIgnoreCase)) throw new CollectorStoreException("CONTENT_FORBIDDEN", "当前角色无权查看积分余额。", 403); return Ok(await credits.GetAccountAsync(user.TenantId, cancellationToken)); });

    /// <summary>读取积分账本；成员只能查看自己发起的消费。</summary>
    [HttpGet("ledger")]
    [ProducesResponseType(typeof(CreditLedgerPage), StatusCodes.Status200OK)]
    public Task<IActionResult> Ledger([FromQuery] string? cursor, [FromQuery] int limit = 50, CancellationToken cancellationToken = default)
        => Execute(async () =>
        {
            var user = RequireUser();
            if (user.Role.Equals("viewer", StringComparison.OrdinalIgnoreCase))
                throw new CollectorStoreException("CONTENT_FORBIDDEN", "当前角色无权查看积分账本。", 403);
            var tenantWide = user.Role.Equals("owner", StringComparison.OrdinalIgnoreCase)
                || user.Role.Equals("admin", StringComparison.OrdinalIgnoreCase);
            return Ok(await credits.ListLedgerAsync(user.TenantId, tenantWide ? null : user.UserId, cursor, limit, cancellationToken));
        });

    /// <summary>按团队时区自然日与操作类型汇总积分消耗（仅 capture）；viewer 无权查看，成员只统计自己发起的消费。</summary>
    /// <param name="from">起始自然日（yyyy-MM-dd，含）；默认结束日前 29 天。</param>
    /// <param name="to">结束自然日（yyyy-MM-dd，含）；默认团队时区的今天，跨度最长 366 天。</param>
    /// <param name="timeZone">IANA 时区；默认最早创建的启用品牌时区，没有品牌时为 Asia/Shanghai。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpGet("usage")]
    [ProducesResponseType(typeof(CreditUsageView), StatusCodes.Status200OK)]
    public Task<IActionResult> Usage([FromQuery] string? from, [FromQuery] string? to, [FromQuery] string? timeZone,
        CancellationToken cancellationToken = default)
        => Execute(async () =>
        {
            var user = RequireUser();
            if (user.Role.Equals("viewer", StringComparison.OrdinalIgnoreCase))
                throw new CollectorStoreException("CONTENT_FORBIDDEN", "当前角色无权查看积分用量。", 403);
            var tenantWide = user.Role.Equals("owner", StringComparison.OrdinalIgnoreCase)
                || user.Role.Equals("admin", StringComparison.OrdinalIgnoreCase);
            var request = new CreditUsageRequest(ParseDay(from, "from"), ParseDay(to, "to"),
                string.IsNullOrWhiteSpace(timeZone) ? null : timeZone.Trim());
            return Ok(await credits.UsageAsync(user.TenantId, tenantWide ? null : user.UserId, request, cancellationToken));
        });

    private static DateOnly? ParseDay(string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var day))
            throw new CollectorStoreException("INVALID_TIME_RANGE", $"{key} 必须是 yyyy-MM-dd 格式的日期。", 400);
        return day;
    }

    /// <summary>系统管理员为租户幂等入账；普通租户成员不可调用。</summary>
    [HttpPost("/api/v1/admin/tenants/{tenantId:guid}/credits/grants")]
    [ProducesResponseType(typeof(CreditLedgerEntryView), StatusCodes.Status201Created)]
    public Task<IActionResult> Grant(Guid tenantId, [FromBody] CreditGrantRequest request, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var user = RequireUser();
            if (!await research.IsSystemAdministratorAsync(user.UserId, cancellationToken)) throw new CollectorStoreException("ADMIN_REQUIRED", "需要系统管理员权限。", 403);
            return StatusCode(201, await credits.GrantAsync(tenantId, request.Amount, request.ExternalReference, user.UserId, request.Reason, cancellationToken));
        });
}

/// <summary>系统管理员积分入账请求。</summary>
/// <param name="Amount">入账积分数。</param>
/// <param name="ExternalReference">外部幂等流水号。</param>
/// <param name="Reason">入账原因。</param>
public sealed record CreditGrantRequest(decimal Amount, string ExternalReference, string Reason);
