using System.Security.Claims;
using Jogeoai.Application;
using Jogeoai.Api.Controllers;
using Jogeoai.Domain;

namespace Jogeoai.Api;

/// <summary>从 Cookie 或 Bearer 令牌加载服务端会话身份。</summary>
public sealed class SessionAuthenticationMiddleware(RequestDelegate next)
{
    /// <summary>HttpContext.Items 中保存已验证身份的键。</summary>
    public const string ContextItemKey = "Jogeoai.AuthenticatedRequestContext";

    /// <summary>执行当前请求的会话验证。</summary>
    public async Task InvokeAsync(HttpContext context, IIdentityStore identityStore)
    {
        var token = ReadToken(context);
        if (token is not null)
        {
            var identity = await identityStore.ValidateSessionAsync(token, context.RequestAborted);
            if (identity is not null)
            {
                context.Items[ContextItemKey] = identity;
                var claims = new List<Claim>
                {
                    new Claim(ClaimTypes.NameIdentifier, identity.User.UserId.ToString()),
                    new Claim(ClaimTypes.Email, identity.User.Email),
                    new Claim(ClaimTypes.Name, identity.User.DisplayName ?? identity.User.Email),
                };
                foreach (var membership in identity.User.Memberships)
                {
                    claims.Add(new Claim("tenant_id", membership.TenantId.ToString()));
                    claims.Add(new Claim("tenant_role", membership.Role));
                }
                context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Jogeoai.Session"));
            }
        }

        await next(context);
    }

    /// <summary>读取请求中的 Cookie 或 Bearer 会话令牌。</summary>
    public static string? ReadToken(HttpContext context)
    {
        var authorization = context.Request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(authorization)
            && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var bearer = authorization[7..].Trim();
            if (bearer.Length > 0) return bearer;
        }

        return context.Request.Cookies.TryGetValue(AuthController.CookieName, out var cookie)
            && !string.IsNullOrWhiteSpace(cookie) ? cookie : null;
    }

    /// <summary>读取当前请求已经验证的身份。</summary>
    public static AuthenticatedRequestContext? GetContext(HttpContext context) =>
        context.Items.TryGetValue(ContextItemKey, out var value) ? value as AuthenticatedRequestContext : null;
}

/// <summary>将当前请求解析为有成员关系的租户。</summary>
public sealed class TenantContextResolver(IHostEnvironment environment)
{
    /// <summary>解析租户；开发环境保留无会话的 X-Tenant-Id 兼容桥。</summary>
    /// <param name="context">当前 HTTP 请求。</param>
    public Guid Resolve(HttpContext context)
    {
        var identity = SessionAuthenticationMiddleware.GetContext(context);
        var header = context.Request.Headers["X-Tenant-Id"].FirstOrDefault();
        var hasHeader = !string.IsNullOrWhiteSpace(header);
        // Production requests must authenticate before any tenant-header
        // validation.  A malformed compatibility header must not turn an
        // anonymous production request into a misleading tenant 400 response.
        if (identity is null && !environment.IsDevelopment())
            throw new CollectorStoreException("AUTHENTICATION_REQUIRED", "请先登录。", 401);
        if (hasHeader && (!Guid.TryParse(header, out var requestedTenant) || requestedTenant == Guid.Empty))
            throw new CollectorStoreException("TENANT_REQUIRED", "请提供有效的 X-Tenant-Id。", 400);

        if (identity is not null)
        {
            if (hasHeader)
            {
                var requestedTenantWithIdentity = Guid.Parse(header!);
                if (!identity.User.Memberships.Any(m => m.TenantId == requestedTenantWithIdentity))
                    throw new CollectorStoreException("TENANT_FORBIDDEN", "当前用户不是该租户成员。", 403);
                return requestedTenantWithIdentity;
            }

            if (identity.User.Memberships.Count == 1) return identity.User.Memberships[0].TenantId;
            throw new CollectorStoreException("TENANT_REQUIRED", "当前用户属于多个租户，请提供 X-Tenant-Id。", 400);
        }

        if (environment.IsDevelopment() && hasHeader) return Guid.Parse(header!);
        throw new CollectorStoreException("TENANT_REQUIRED", "请提供有效的 X-Tenant-Id。", 400);
    }
}
