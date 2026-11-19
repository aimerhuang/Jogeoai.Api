namespace Jogeoai.Application;

/// <summary>MCP 只读访问令牌的持久化合同（迁移 044 <c>mcp_access_tokens</c>）；未执行迁移时各方法抛出 503 MCP_SCHEMA_NOT_MIGRATED。</summary>
public interface IMcpTokenStore
{
    /// <summary>在事务内检查租户启用令牌上限并保存新令牌（只保存哈希与前缀）；达到上限时抛出 409 MCP_TOKEN_LIMIT_REACHED。</summary>
    /// <param name="request">已校验的创建参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<McpTokenView> CreateAsync(McpTokenCreate request, CancellationToken cancellationToken = default);

    /// <summary>列出租户的令牌（含已吊销、已过期），按创建时间倒序；不含明文和哈希。</summary>
    /// <param name="tenantId">租户标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyList<McpTokenView>> ListAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>吊销令牌，幂等：已吊销时保留原吊销时间；令牌不属于该租户或不存在时返回空。</summary>
    /// <param name="tenantId">租户标识。</param>
    /// <param name="tokenId">令牌标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<McpTokenView?> RevokeAsync(Guid tenantId, Guid tokenId, CancellationToken cancellationToken = default);

    /// <summary>按 SHA-256 哈希查找未吊销、未过期且租户有效的令牌；查不到返回空。</summary>
    /// <param name="tokenHash">令牌明文的 SHA-256 小写 hex。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<McpTokenPrincipal?> FindActiveByHashAsync(string tokenHash, CancellationToken cancellationToken = default);

    /// <summary>节流更新最后使用时间：只有距上次记录超过 60 秒才写库。</summary>
    /// <param name="tokenId">令牌标识。</param>
    /// <param name="usedAt">本次使用的 UTC 时间。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task TouchLastUsedAsync(Guid tokenId, DateTimeOffset usedAt, CancellationToken cancellationToken = default);
}

/// <summary>Store 层的令牌创建参数；明文不进入 Store。</summary>
/// <param name="TokenId">新令牌标识。</param>
/// <param name="TenantId">所属租户。</param>
/// <param name="Name">管理员填写的名称（1–100 字符）。</param>
/// <param name="TokenHash">明文的 SHA-256 小写 hex。</param>
/// <param name="TokenPrefix">明文前 12 个字符（含 jgm_ 前缀）。</param>
/// <param name="SiteIds">可访问的站点；为空表示租户全部站点。</param>
/// <param name="CreatedByUserId">创建人。</param>
/// <param name="CreatedAt">创建 UTC 时间。</param>
/// <param name="ExpiresAt">过期 UTC 时间。</param>
public sealed record McpTokenCreate(Guid TokenId, Guid TenantId, string Name, string TokenHash, string TokenPrefix,
    IReadOnlyList<Guid>? SiteIds, Guid CreatedByUserId, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

/// <summary>MCP 访问令牌（不含明文和哈希）。</summary>
/// <param name="Id">令牌标识。</param>
/// <param name="Name">令牌名称。</param>
/// <param name="TokenPrefix">明文前 12 个字符（含 jgm_ 前缀），用于识别令牌。</param>
/// <param name="SiteIds">可访问的站点（品牌）标识；为空表示租户全部站点。</param>
/// <param name="CreatedByUserId">创建人用户标识。</param>
/// <param name="CreatedAt">创建 UTC 时间。</param>
/// <param name="ExpiresAt">过期 UTC 时间。</param>
/// <param name="RevokedAt">吊销 UTC 时间；未吊销时为空。</param>
/// <param name="LastUsedAt">最后使用 UTC 时间（约每分钟最多更新一次）；从未使用时为空。</param>
/// <param name="Status">状态：active 启用、expired 已过期、revoked 已吊销。</param>
public sealed record McpTokenView(Guid Id, string Name, string TokenPrefix, IReadOnlyList<Guid>? SiteIds,
    Guid CreatedByUserId, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt,
    DateTimeOffset? LastUsedAt, string Status);

/// <summary>创建令牌的响应：明文只在此返回一次，之后无法再次查看。</summary>
/// <param name="Id">令牌标识。</param>
/// <param name="Name">令牌名称。</param>
/// <param name="Token">令牌明文（jgm_ 开头）；请立即保存，服务端不保存明文。</param>
/// <param name="TokenPrefix">明文前 12 个字符，用于在列表中识别。</param>
/// <param name="ExpiresAt">过期 UTC 时间。</param>
/// <param name="SiteIds">可访问的站点（品牌）标识；为空表示租户全部站点。</param>
public sealed record McpTokenCreatedView(Guid Id, string Name, string Token, string TokenPrefix,
    DateTimeOffset ExpiresAt, IReadOnlyList<Guid>? SiteIds);

/// <summary>令牌列表。</summary>
/// <param name="Items">令牌（含已吊销、已过期），按创建时间倒序。</param>
/// <param name="ActiveCount">当前启用（未吊销且未过期）的令牌数。</param>
/// <param name="ActiveLimit">每个租户启用令牌数上限。</param>
public sealed record McpTokenListView(IReadOnlyList<McpTokenView> Items, int ActiveCount, int ActiveLimit);

/// <summary>通过校验的 MCP 调用方身份。</summary>
/// <param name="TokenId">令牌标识。</param>
/// <param name="TenantId">所属租户。</param>
/// <param name="TokenHash">库内保存的哈希，供定长比较。</param>
/// <param name="SiteIds">可访问的站点；为空表示租户全部站点。</param>
/// <param name="ExpiresAt">过期 UTC 时间。</param>
/// <param name="LastUsedAt">最后使用 UTC 时间。</param>
public sealed record McpTokenPrincipal(Guid TokenId, Guid TenantId, string TokenHash, IReadOnlyList<Guid>? SiteIds,
    DateTimeOffset ExpiresAt, DateTimeOffset? LastUsedAt);
