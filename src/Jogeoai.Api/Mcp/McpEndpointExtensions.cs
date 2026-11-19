using Microsoft.AspNetCore.RateLimiting;

namespace Jogeoai.Api.Mcp;

/// <summary>MCP 端点的注册入口；Program.cs 与集成测试共用，保证中间件顺序与限流策略一致。</summary>
public static class McpEndpointExtensions
{
    /// <summary>注册 MCP 服务：Streamable HTTP 无状态模式与只读工具；工具通过 HttpContext 读取调用方。</summary>
    /// <param name="services">服务集合。</param>
    public static IServiceCollection AddJogeoaiMcp(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithTools<McpReadTools>();
        return services;
    }

    /// <summary>注册按令牌分区的 mcp-token 限流策略。</summary>
    /// <param name="options">限流配置。</param>
    public static RateLimiterOptions AddMcpTokenPolicy(this RateLimiterOptions options)
        => options.AddPolicy(McpTokenRateLimit.Policy, McpTokenRateLimit.Partition);

    /// <summary>只在 /mcp 分支挂载令牌校验；必须位于会话中间件之后、UseRateLimiter 之前。</summary>
    /// <param name="app">应用管道。</param>
    public static IApplicationBuilder UseMcpTokenAuthentication(this IApplicationBuilder app)
        => app.UseWhen(McpTokenAuthenticationMiddleware.IsMcpRequest,
            branch => branch.UseMiddleware<McpTokenAuthenticationMiddleware>());

    /// <summary>映射 /mcp 端点，按令牌限流，并排除在 Swagger / OpenAPI 文档之外。</summary>
    /// <param name="endpoints">路由。</param>
    public static IEndpointConventionBuilder MapJogeoaiMcp(this IEndpointRouteBuilder endpoints)
        => endpoints.MapMcp(McpTokenAuthenticationMiddleware.Path)
            .RequireRateLimiting(McpTokenRateLimit.Policy)
            .ExcludeFromDescription();
}
