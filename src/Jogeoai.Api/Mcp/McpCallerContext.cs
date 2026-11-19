namespace Jogeoai.Api.Mcp;

/// <summary>通过 MCP 令牌校验的调用方：令牌、租户与可访问站点范围。只存在于 /mcp 请求。</summary>
/// <param name="TokenId">令牌标识（审计与限流分区使用，不是令牌明文）。</param>
/// <param name="TenantId">令牌所属租户。</param>
/// <param name="SiteIds">可访问的站点；为空表示租户全部站点。</param>
public sealed record McpCallerContext(Guid TokenId, Guid TenantId, IReadOnlyList<Guid>? SiteIds)
{
    /// <summary>HttpContext.Items 中保存调用方的键。</summary>
    public const string ItemKey = "Jogeoai.McpCallerContext";

    /// <summary>令牌范围是否包含该站点（不校验站点是否存在）。</summary>
    /// <param name="siteId">站点标识。</param>
    public bool AllowsSite(Guid siteId) => siteId != Guid.Empty && (SiteIds is null || SiteIds.Contains(siteId));

    /// <summary>读取当前请求的 MCP 调用方；未通过令牌校验时为空。</summary>
    /// <param name="context">当前 HTTP 请求。</param>
    public static McpCallerContext? Get(HttpContext? context)
        => context is not null && context.Items.TryGetValue(ItemKey, out var value) ? value as McpCallerContext : null;
}
