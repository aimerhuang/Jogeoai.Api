using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Jogeoai.Api.Controllers;

/// <summary>
/// MCP 只读访问令牌管理：签发（明文只返回一次）、列表、吊销。仅当前团队的所有者（owner）或管理员（admin）可调用，且必须是真实登录会话；
/// 令牌用于外部 MCP 客户端以 <c>Authorization: Bearer jgm_…</c> 访问 <c>/mcp</c>。依赖迁移 044，未执行时返回 503 MCP_SCHEMA_NOT_MIGRATED。
/// </summary>
[ApiController]
[Route("api/v1/mcp-tokens")]
public sealed class McpTokensController(IMcpTokenStore tokens, IManagementStore management, TenantContextResolver tenantContext)
    : ContentControllerBase(tenantContext)
{
    /// <summary>签发 MCP 访问令牌；响应中的 token 明文只返回这一次，服务端只保存哈希。</summary>
    /// <param name="request">令牌名称、有效天数（1–365）与可选的站点范围。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <response code="201">签发成功，返回明文令牌。</response>
    /// <response code="400">名称、有效期或站点范围无效（VALIDATION_FAILED / MCP_TOKEN_SITE_INVALID）。</response>
    /// <response code="401">未登录。</response>
    /// <response code="403">当前角色不是所有者或管理员（MCP_TOKEN_FORBIDDEN）。</response>
    /// <response code="409">启用令牌已达上限 20 个（MCP_TOKEN_LIMIT_REACHED）。</response>
    /// <response code="503">迁移 044 尚未部署（MCP_SCHEMA_NOT_MIGRATED）。</response>
    [HttpPost]
    [ProducesResponseType(typeof(McpTokenCreatedView), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> Create([FromBody] McpTokenCreateRequest request, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var user = RequireAdmin();
            var name = (request?.Name ?? "").Trim();
            if (name.Length is < 1 or > McpTokenCodec.MaxNameLength)
                throw new CollectorStoreException("VALIDATION_FAILED", $"name 长度必须为 1–{McpTokenCodec.MaxNameLength} 个字符。", 400);
            if (request!.ExpiresInDays is < 1 or > McpTokenCodec.MaxExpiresInDays)
                throw new CollectorStoreException("VALIDATION_FAILED", $"expiresInDays 必须在 1–{McpTokenCodec.MaxExpiresInDays} 之间。", 400);
            var siteIds = await ValidateSitesAsync(user.TenantId, request.SiteIds, cancellationToken);

            var token = McpTokenCodec.Generate();
            var now = DateTimeOffset.UtcNow;
            var created = await tokens.CreateAsync(new McpTokenCreate(Guid.NewGuid(), user.TenantId, name,
                McpTokenCodec.Hash(token), McpTokenCodec.DisplayPrefix(token), siteIds, user.UserId, now,
                now.AddDays(request.ExpiresInDays)), cancellationToken);
            return StatusCode(StatusCodes.Status201Created,
                new McpTokenCreatedView(created.Id, created.Name, token, created.TokenPrefix, created.ExpiresAt, created.SiteIds));
        });

    /// <summary>列出当前团队的 MCP 访问令牌（含已吊销、已过期）；不返回明文和哈希。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <response code="200">令牌列表，启用令牌在前。</response>
    /// <response code="401">未登录。</response>
    /// <response code="403">当前角色不是所有者或管理员（MCP_TOKEN_FORBIDDEN）。</response>
    /// <response code="503">迁移 044 尚未部署（MCP_SCHEMA_NOT_MIGRATED）。</response>
    [HttpGet]
    [ProducesResponseType(typeof(McpTokenListView), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> List(CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var user = RequireAdmin();
            var items = await tokens.ListAsync(user.TenantId, cancellationToken);
            return Ok(new McpTokenListView(items, items.Count(item => item.Status == "active"), McpTokenCodec.MaxActiveTokensPerTenant));
        });

    /// <summary>吊销 MCP 访问令牌，幂等；吊销后使用该令牌访问 /mcp 立即返回 401。</summary>
    /// <param name="id">令牌标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <response code="204">已吊销（含此前已吊销）。</response>
    /// <response code="401">未登录。</response>
    /// <response code="403">当前角色不是所有者或管理员（MCP_TOKEN_FORBIDDEN）。</response>
    /// <response code="404">令牌不存在或不属于当前团队（MCP_TOKEN_NOT_FOUND）。</response>
    /// <response code="503">迁移 044 尚未部署（MCP_SCHEMA_NOT_MIGRATED）。</response>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> Revoke(Guid id, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var user = RequireAdmin();
            var revoked = await tokens.RevokeAsync(user.TenantId, id, cancellationToken);
            if (revoked is null) throw new CollectorStoreException("MCP_TOKEN_NOT_FOUND", "令牌不存在。", 404);
            return NoContent();
        });

    // 必须有真实会话（RequireUser 不走开发环境租户桥），且是当前租户的 owner / admin。
    private (Guid TenantId, Guid UserId, string Role) RequireAdmin()
    {
        var user = RequireUser();
        if (!user.Role.Equals("owner", StringComparison.OrdinalIgnoreCase) && !user.Role.Equals("admin", StringComparison.OrdinalIgnoreCase))
            throw new CollectorStoreException("MCP_TOKEN_FORBIDDEN", "只有团队所有者或管理员可以管理 MCP 令牌。", 403);
        return user;
    }

    private async Task<IReadOnlyList<Guid>?> ValidateSitesAsync(Guid tenantId, IReadOnlyList<Guid>? requested,
        CancellationToken cancellationToken)
    {
        if (requested is null) return null;
        if (requested.Count == 0)
            throw new CollectorStoreException("VALIDATION_FAILED", "siteIds 不能为空数组；省略该字段表示团队全部品牌。", 400);
        var distinct = requested.Distinct().ToArray();
        if (distinct.Length > McpTokenCodec.MaxSiteIds)
            throw new CollectorStoreException("VALIDATION_FAILED", $"siteIds 最多 {McpTokenCodec.MaxSiteIds} 个。", 400);
        foreach (var siteId in distinct)
        {
            if (siteId == Guid.Empty || await management.GetBrandAsync(tenantId, siteId, cancellationToken) is null)
                throw new CollectorStoreException("MCP_TOKEN_SITE_INVALID", "siteIds 中包含不属于当前团队的品牌。", 400);
        }
        return distinct;
    }
}

/// <summary>签发 MCP 访问令牌的请求。</summary>
/// <param name="Name">令牌名称（1–100 字符），便于识别用途，例如「Grok Bot」。</param>
/// <param name="ExpiresInDays">有效天数，1–365。</param>
/// <param name="SiteIds">可访问的品牌（站点）标识，最多 50 个且必须属于当前团队；省略表示团队全部品牌（含之后新建的品牌），不能传空数组。</param>
// Name 声明为可空：缺字段时由 Create 返回项目统一的 VALIDATION_FAILED，而不是框架自动 400。
public sealed record McpTokenCreateRequest(string? Name, int ExpiresInDays, IReadOnlyList<Guid>? SiteIds = null);
