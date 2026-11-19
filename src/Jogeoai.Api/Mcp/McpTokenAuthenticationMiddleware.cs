using System.Security.Claims;
using Jogeoai.Application;
using Jogeoai.Domain;

namespace Jogeoai.Api.Mcp;

/// <summary>
/// 只挂在 /mcp 分支的令牌校验：只接受 <c>Authorization: Bearer jgm_…</c>，按 SHA-256 哈希查找未吊销、未过期的令牌。
/// 会话 Cookie 与会话 Bearer 一律不认（先清除会话中间件已加载的身份）；失败直接返回 401，不进入 MCP 管道。
/// 日志只记录令牌标识，不记录明文或哈希。
/// </summary>
public sealed class McpTokenAuthenticationMiddleware(RequestDelegate next, ILogger<McpTokenAuthenticationMiddleware> logger)
{
    /// <summary>401 响应的错误码。</summary>
    public const string InvalidTokenCode = "MCP_TOKEN_INVALID";

    /// <summary>MCP 端点路径。</summary>
    public const string Path = "/mcp";

    /// <summary>请求是否属于 MCP 端点（/mcp 及其子路径，大小写不敏感）。</summary>
    /// <param name="context">当前 HTTP 请求。</param>
    public static bool IsMcpRequest(HttpContext context) => context.Request.Path.StartsWithSegments(Path);

    /// <summary>校验 MCP 令牌并写入 <see cref="McpCallerContext"/>。</summary>
    /// <param name="context">当前 HTTP 请求。</param>
    /// <param name="store">令牌存储。</param>
    public async Task InvokeAsync(HttpContext context, IMcpTokenStore store)
    {
        // /mcp 不接受用户会话：无论会话中间件是否已加载身份，都在这里清除。
        context.Items.Remove(SessionAuthenticationMiddleware.ContextItemKey);
        context.Items.Remove(McpCallerContext.ItemKey);
        context.User = new ClaimsPrincipal(new ClaimsIdentity());

        var token = ReadBearer(context);
        if (!McpTokenCodec.IsWellFormed(token))
        {
            await RejectAsync(context);
            return;
        }

        var hash = McpTokenCodec.Hash(token!);
        McpTokenPrincipal? principal;
        try
        {
            principal = await store.FindActiveByHashAsync(hash, context.RequestAborted);
        }
        catch (CollectorStoreException exception)
        {
            await WriteErrorAsync(context, exception.StatusCode, exception.Code, exception.Message);
            return;
        }
        if (principal is null || !McpTokenCodec.FixedTimeEquals(principal.TokenHash, hash))
        {
            await RejectAsync(context);
            return;
        }

        var caller = new McpCallerContext(principal.TokenId, principal.TenantId, principal.SiteIds);
        context.Items[McpCallerContext.ItemKey] = caller;
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("mcp_token_id", principal.TokenId.ToString()),
            new Claim("tenant_id", principal.TenantId.ToString()),
        ], "Jogeoai.McpToken"));

        var now = DateTimeOffset.UtcNow;
        if (McpTokenCodec.ShouldTouchLastUsed(principal.LastUsedAt, now))
        {
            try
            {
                await store.TouchLastUsedAsync(principal.TokenId, now, context.RequestAborted);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // 最后使用时间只是展示信息，写入失败不阻断查询。
                logger.LogWarning("MCP token {TokenId} last-used update failed ({ErrorType}).",
                    principal.TokenId, exception.GetType().Name);
            }
        }

        await next(context);
    }

    private static string? ReadBearer(HttpContext context)
    {
        var authorization = context.Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(authorization)
            || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var bearer = authorization[7..].Trim();
        return bearer.Length == 0 ? null : bearer;
    }

    private static Task RejectAsync(HttpContext context)
    {
        context.Response.Headers.WWWAuthenticate = "Bearer realm=\"jogeoai-mcp\"";
        return WriteErrorAsync(context, StatusCodes.Status401Unauthorized, InvalidTokenCode,
            "MCP 访问令牌缺失、无效、已过期或已吊销。请使用 Authorization: Bearer jgm_… 访问。");
    }

    private static async Task WriteErrorAsync(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { error = new { code, message } }, context.RequestAborted);
    }
}
