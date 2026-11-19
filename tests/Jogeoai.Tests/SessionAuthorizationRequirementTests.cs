using System.Security.Claims;
using System.Text.Json;
using Jogeoai.Api;
using Jogeoai.Api.Authorization;
using Jogeoai.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Jogeoai.Tests;

/// <summary>
/// 默认会话策略的单元测试：{会话有/无} × {Development/Production} × {X-Tenant-Id 有效/缺失/非法/空 GUID} 组合矩阵；
/// MCP 等非会话 principal 不算会话；结果处理器输出统一 401/403 JSON；扩展方法注册了策略与处理器。不连数据库、不发请求。
/// </summary>
public sealed class SessionAuthorizationRequirementTests
{
    /// <summary>X-Tenant-Id 取值：有效 GUID / 缺失 / 非 GUID / 空 GUID。</summary>
    public enum Header { Valid, Missing, Malformed, EmptyGuid }

    /// <summary>完整组合矩阵与期望结果。</summary>
    public static TheoryData<bool, string, Header, bool> Matrix()
    {
        var data = new TheoryData<bool, string, Header, bool>();
        foreach (var session in new[] { true, false })
        foreach (var environment in new[] { Environments.Development, Environments.Production })
        foreach (var header in Enum.GetValues<Header>())
        {
            // 有会话一律通过；无会话只有 Development + 有效非空 GUID 头才通过（兼容桥）。
            var expected = session || (environment == Environments.Development && header == Header.Valid);
            data.Add(session, environment, header, expected);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task RequirementMatrix(bool session, string environment, Header header, bool expected)
    {
        var context = new DefaultHttpContext();
        if (session) context.Items[SessionAuthenticationMiddleware.ContextItemKey] = Identity();
        switch (header)
        {
            case Header.Valid: context.Request.Headers["X-Tenant-Id"] = Guid.NewGuid().ToString(); break;
            case Header.Malformed: context.Request.Headers["X-Tenant-Id"] = "not-a-guid"; break;
            case Header.EmptyGuid: context.Request.Headers["X-Tenant-Id"] = Guid.Empty.ToString(); break;
        }

        Assert.Equal(expected, await EvaluateAsync(context, environment));
        Assert.Equal(expected, SessionAuthorization.IsSatisfied(context, new TestEnvironment(environment)));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task AuthenticatedNonSessionPrincipalIsNotASession(string environment)
    {
        // 例如 MCP 令牌 principal：User 已认证，但 HttpContext.Items 中没有会话身份。
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenant_id", Guid.NewGuid().ToString())], "Jogeoai.McpToken"))
        };
        Assert.False(await EvaluateAsync(context, environment));
    }

    [Fact]
    public async Task NonHttpContextResourceNeverSucceeds()
    {
        var requirement = new SessionOrDevelopmentBridgeRequirement();
        var handlerContext = new AuthorizationHandlerContext([requirement], new ClaimsPrincipal(), resource: null);
        await new SessionOrDevelopmentBridgeHandler(new TestEnvironment(Environments.Development)).HandleAsync(handlerContext);
        Assert.False(handlerContext.HasSucceeded);
    }

    [Fact]
    public void ExtensionRegistersPolicyAndHandlers()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestEnvironment(Environments.Production));
        services.AddJogeoaiSessionAuthorization();
        using var provider = services.BuildServiceProvider();

        var policy = provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value.GetPolicy(SessionAuthorization.Policy);
        Assert.NotNull(policy);
        Assert.IsType<SessionOrDevelopmentBridgeRequirement>(Assert.Single(policy.Requirements));
        Assert.Contains(provider.GetServices<IAuthorizationHandler>(), handler => handler is SessionOrDevelopmentBridgeHandler);
        Assert.IsType<SessionAuthorizationResultHandler>(provider.GetRequiredService<IAuthorizationMiddlewareResultHandler>());
        Assert.Equal("JogeoaiSession", SessionAuthorization.Policy);
    }

    [Fact]
    public async Task ChallengeWrites401AuthenticationRequired()
    {
        var (status, code, nextCalled) = await RunResultHandlerAsync(PolicyAuthorizationResult.Challenge());
        Assert.Equal(401, status);
        Assert.Equal("AUTHENTICATION_REQUIRED", code);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task ForbidWrites403ContentForbidden()
    {
        var (status, code, nextCalled) = await RunResultHandlerAsync(PolicyAuthorizationResult.Forbid());
        Assert.Equal(403, status);
        Assert.Equal("CONTENT_FORBIDDEN", code);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task SuccessCallsNext()
    {
        var (status, code, nextCalled) = await RunResultHandlerAsync(PolicyAuthorizationResult.Success());
        Assert.True(nextCalled);
        Assert.Equal(200, status);
        Assert.Null(code);
    }

    private static async Task<bool> EvaluateAsync(HttpContext context, string environment)
    {
        var requirement = new SessionOrDevelopmentBridgeRequirement();
        var handlerContext = new AuthorizationHandlerContext([requirement], context.User, context);
        await new SessionOrDevelopmentBridgeHandler(new TestEnvironment(environment)).HandleAsync(handlerContext);
        return handlerContext.HasSucceeded;
    }

    private static async Task<(int Status, string? Code, bool NextCalled)> RunResultHandlerAsync(PolicyAuthorizationResult result)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var nextCalled = false;
        var policy = new AuthorizationPolicyBuilder().AddRequirements(new SessionOrDevelopmentBridgeRequirement()).Build();
        await new SessionAuthorizationResultHandler().HandleAsync(_ => { nextCalled = true; return Task.CompletedTask; },
            context, policy, result);
        string? code = null;
        if (context.Response.Body.Length > 0)
        {
            context.Response.Body.Position = 0;
            using var body = await JsonDocument.ParseAsync(context.Response.Body);
            code = body.RootElement.GetProperty("error").GetProperty("code").GetString();
            Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("error").GetProperty("message").GetString()));
        }
        return (context.Response.StatusCode, code, nextCalled);
    }

    private static AuthenticatedRequestContext Identity() => new(
        new(Guid.NewGuid(), "session@example.test", null, "active", [new(Guid.NewGuid(), "Team", "viewer", "active")]),
        Guid.NewGuid(), "session-token");

    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
