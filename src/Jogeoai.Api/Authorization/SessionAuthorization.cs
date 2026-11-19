using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Jogeoai.Api.Authorization;

/// <summary>
/// Controller 默认会话策略：所有 Controller endpoint 默认要求服务端会话；匿名入口只能用 <see cref="AllowAnonymousAttribute"/>
/// 显式标注，并同步维护 <c>SessionAuthorizationEndpointTests</c> 中的白名单。
/// 只挂在 Controller 上（不是 FallbackPolicy），因此 <c>/mcp</c>（MCP 令牌）、<c>/health/ready</c> 与 OpenAPI 文档不受影响。
/// </summary>
public static class SessionAuthorization
{
    /// <summary>默认会话策略名称。</summary>
    public const string Policy = "JogeoaiSession";

    /// <summary>Development 兼容桥使用的租户请求头。</summary>
    public const string TenantHeader = "X-Tenant-Id";

    /// <summary>注册默认会话策略、其要求处理器与统一的 401/403 JSON 结果处理器。Program.cs 与测试宿主共用。</summary>
    /// <param name="services">服务集合。</param>
    /// <returns>同一服务集合，便于链式调用。</returns>
    public static IServiceCollection AddJogeoaiSessionAuthorization(this IServiceCollection services)
    {
        services.AddAuthorization(options => options.AddPolicy(Policy, policy =>
            policy.AddRequirements(new SessionOrDevelopmentBridgeRequirement())));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuthorizationHandler, SessionOrDevelopmentBridgeHandler>());
        services.Replace(ServiceDescriptor.Singleton<IAuthorizationMiddlewareResultHandler, SessionAuthorizationResultHandler>());
        return services;
    }

    /// <summary>让全部 Controller endpoint 默认要求 <see cref="Policy"/>；带 <see cref="AllowAnonymousAttribute"/> 的入口除外。</summary>
    /// <param name="builder"><c>MapControllers()</c> 返回的约定构建器。</param>
    /// <returns>同一构建器。</returns>
    public static ControllerActionEndpointConventionBuilder RequireJogeoaiSession(this ControllerActionEndpointConventionBuilder builder)
        => builder.RequireAuthorization(Policy);

    /// <summary>
    /// 判断请求是否满足默认会话策略：已由 <see cref="SessionAuthenticationMiddleware"/> 加载服务端会话，
    /// 或处于 Development 且携带可解析的非空 GUID <c>X-Tenant-Id</c>（兼容桥；后续租户解析仍由 <see cref="TenantContextResolver"/> 负责）。
    /// 判断依据是会话身份本身，而不是 <c>User.Identity.IsAuthenticated</c>，避免其他 principal（如 MCP 令牌）被当成会话。
    /// </summary>
    /// <param name="httpContext">当前 HTTP 请求。</param>
    /// <param name="environment">宿主环境。</param>
    /// <returns>满足时为 <see langword="true"/>。</returns>
    public static bool IsSatisfied(HttpContext httpContext, IHostEnvironment environment)
    {
        if (SessionAuthenticationMiddleware.GetContext(httpContext) is not null) return true;
        if (!environment.IsDevelopment()) return false;
        var header = httpContext.Request.Headers[TenantHeader].FirstOrDefault();
        return Guid.TryParse(header, out var tenantId) && tenantId != Guid.Empty;
    }
}

/// <summary>默认会话策略的授权要求：已登录会话，或 Development 兼容桥（有效 <c>X-Tenant-Id</c>）。</summary>
public sealed class SessionOrDevelopmentBridgeRequirement : IAuthorizationRequirement;

/// <summary>按 <see cref="SessionAuthorization.IsSatisfied"/> 判定 <see cref="SessionOrDevelopmentBridgeRequirement"/>。</summary>
/// <param name="environment">宿主环境；只有 Development 才放行兼容桥。</param>
public sealed class SessionOrDevelopmentBridgeHandler(IHostEnvironment environment)
    : AuthorizationHandler<SessionOrDevelopmentBridgeRequirement>
{
    /// <summary>资源为当前 <see cref="HttpContext"/> 且满足会话或兼容桥条件时标记成功；否则保持未满足。</summary>
    /// <param name="context">授权上下文。</param>
    /// <param name="requirement">会话要求。</param>
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, SessionOrDevelopmentBridgeRequirement requirement)
    {
        if (context.Resource is HttpContext httpContext && SessionAuthorization.IsSatisfied(httpContext, environment))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

/// <summary>
/// 把授权拒绝写成项目统一的错误结构：未通过认证 401 <c>AUTHENTICATION_REQUIRED</c>，已认证但被拒 403 <c>CONTENT_FORBIDDEN</c>（兜底）。
/// 项目没有注册 authentication scheme，交给默认处理器会调用 <c>ChallengeAsync</c> 并抛出异常，因此必须在这里自行响应。
/// </summary>
public sealed class SessionAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    /// <summary>成功时继续管道；拒绝时直接写出 JSON 错误，不激活 Controller。</summary>
    /// <param name="next">后续管道。</param>
    /// <param name="context">当前 HTTP 请求。</param>
    /// <param name="policy">已评估的策略。</param>
    /// <param name="authorizeResult">策略评估结果。</param>
    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Challenged)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = new { code = "AUTHENTICATION_REQUIRED", message = "请先登录。" } },
                context.RequestAborted);
            return;
        }
        if (authorizeResult.Forbidden)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = new { code = "CONTENT_FORBIDDEN", message = "当前角色无权执行此操作。" } },
                context.RequestAborted);
            return;
        }
        await next(context);
    }
}
