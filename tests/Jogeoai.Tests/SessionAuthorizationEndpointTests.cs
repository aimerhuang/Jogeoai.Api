using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Jogeoai.Api;
using Jogeoai.Api.Authorization;
using Jogeoai.Api.Controllers;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Jogeoai.Tests.Mcp;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jogeoai.Tests;

/// <summary>
/// 默认会话策略的宿主级测试：自建进程内宿主，使用与 Program.cs 相同的 <c>AddJogeoaiSessionAuthorization()</c> /
/// <c>RequireJogeoaiSession()</c>。
/// 1) 枚举全部 Controller endpoint：匿名入口必须在写死的白名单里、白名单无过期条目、其余 endpoint 都带会话策略、已下线路由不存在；
/// 2) Production HTTP：非白名单路由匿名 401 AUTHENTICATION_REQUIRED（Controller 未激活即证明 Store 零调用），白名单入口照常进入 Controller；
/// 3) Development：兼容桥（有效 X-Tenant-Id）仍可进入 Controller。不连数据库、不发出站请求。
/// </summary>
public sealed class SessionAuthorizationEndpointTests : IAsyncLifetime
{
    private const string SessionToken = "session-token-for-authorization-tests";

    /// <summary>
    /// 匿名白名单（Controller 类型名, 方法名）。新增 [AllowAnonymous] 必须同步修改这里，作为评审闸门。
    /// </summary>
    private static readonly HashSet<(string Controller, string Action)> AnonymousWhitelist =
    [
        ("AuthController", "SendCode"),
        ("AuthController", "Register"),
        ("AuthController", "Login"),
        ("AuthController", "Logout"),
        ("HealthController", "Live"),
        ("CollectorController", "Lease"),
        ("CollectorController", "Heartbeat"),
        ("CollectorController", "Result"),
        ("CollectorController", "Eligibility"),
        ("CollectorController", "Failure"),
        ("CollectorController", "SessionEvent"),
        ("CollectorController", "Health"),
        // App 采集节点的内部接口：由每个节点自己的令牌认证（collector_app_nodes），不走用户会话。
        ("AppCollectorController", "ReportBindings"),
        ("AppCollectorController", "Lease"),
        ("AppCollectorController", "Sent"),
        ("AppCollectorController", "Heartbeat"),
        ("AppCollectorController", "Failure"),
        ("AppCollectorController", "Result"),
    ];

    private readonly Guid tenant = Guid.NewGuid();
    private readonly Guid site = Guid.NewGuid();
    private Host production = null!;
    private Host development = null!;

    public async Task InitializeAsync()
    {
        production = await Host.StartAsync(Environments.Production, tenant);
        development = await Host.StartAsync(Environments.Development, tenant);
    }

    public async Task DisposeAsync()
    {
        await production.DisposeAsync();
        await development.DisposeAsync();
    }

    [Fact]
    public void EveryControllerEndpointIsSessionProtectedOrWhitelisted()
    {
        var endpoints = production.ControllerEndpoints();
        Assert.True(endpoints.Count > 100, $"只发现 {endpoints.Count} 个 Controller endpoint，宿主未加载全部 Controller。");

        var anonymous = new HashSet<(string Controller, string Action)>();
        var unprotected = new List<string>();
        foreach (var (endpoint, action) in endpoints)
        {
            var key = (action.ControllerTypeInfo.Name, action.MethodInfo.Name);
            if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            {
                anonymous.Add(key);
                Assert.True(AnonymousWhitelist.Contains(key),
                    $"{key.Item1}.{key.Item2} 标注了 [AllowAnonymous] 但不在匿名白名单中；确需匿名请同步修改白名单并说明理由。");
                continue;
            }
            if (!endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(data => data.Policy == SessionAuthorization.Policy))
                unprotected.Add($"{key.Item1}.{key.Item2} ({Template(endpoint)})");
        }

        Assert.True(unprotected.Count == 0, "以下 endpoint 未受默认会话策略保护：" + string.Join("、", unprotected));
        var stale = AnonymousWhitelist.Except(anonymous).Select(item => $"{item.Controller}.{item.Action}").ToArray();
        Assert.True(stale.Length == 0, "白名单中的条目不存在或不再匿名（过期条目）：" + string.Join("、", stale));
    }

    [Fact]
    public void ProgramWiresSessionPolicyOnControllersOnly()
    {
        // 本类宿主自行调用扩展方法；这里防止 Program.cs 漏接或改成 FallbackPolicy（后者会误拒 /mcp、/health/ready、OpenAPI）。
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Jogeoai.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var program = File.ReadAllText(Path.Combine(directory.FullName, "src", "Jogeoai.Api", "Program.cs"));

        Assert.Contains("builder.Services.AddJogeoaiSessionAuthorization();", program);
        Assert.Contains("app.MapControllers().RequireJogeoaiSession();", program);
        Assert.DoesNotContain("FallbackPolicy", program);
        var session = program.IndexOf("app.UseMiddleware<SessionAuthenticationMiddleware>();", StringComparison.Ordinal);
        var authorization = program.IndexOf("app.UseAuthorization();", StringComparison.Ordinal);
        Assert.True(session >= 0 && authorization > session, "UseAuthorization 必须位于会话中间件之后。");
    }

    [Fact]
    public void AuthMeIsNotWhitelisted()
    {
        var me = Assert.Single(production.ControllerEndpoints(), item =>
            item.Action.ControllerTypeInfo.AsType() == typeof(AuthController) && item.Action.MethodInfo.Name == nameof(AuthController.Me));
        Assert.Null(me.Endpoint.Metadata.GetMetadata<IAllowAnonymous>());
    }

    [Fact]
    public void RemovedRoutesAreGone()
    {
        var templates = production.ControllerEndpoints().Select(item => Template(item.Endpoint)).ToArray();
        Assert.DoesNotContain(templates, template => template.Contains("question-groups", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(templates, template => template.Contains("/questions", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(templates, template => template.Contains("knowledge/links", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Production 下代表性非白名单路由（Management GET/POST、Analytics、BrandResearch、Onboarding、Knowledge、Auth/me）。</summary>
    public static TheoryData<string, string> ProtectedRoutes() => new()
    {
        { "GET", "/api/v1/sites/{site}/competitors" },
        { "POST", "/api/v1/sites/{site}/competitors" },
        { "POST", "/api/v1/sites/{site}/tags" },
        { "POST", "/api/v1/sites/{site}/analysis-jobs" },
        { "GET", "/api/v1/sites/{site}/analytics/overview" },
        { "POST", "/api/v1/sites/{site}/brand-research" },
        { "POST", "/api/v1/sites/{site}/brand-research/{guid}/cancel" },
        { "POST", "/api/v1/onboarding/brands/{site}/complete" },
        { "PUT", "/api/v1/onboarding/{site}/draft" },
        { "GET", "/api/v1/onboarding" },
        { "GET", "/api/v1/knowledge/folders" },
        { "GET", "/api/v1/auth/me" },
    };

    [Theory]
    [MemberData(nameof(ProtectedRoutes))]
    public async Task AnonymousProductionRequestIs401(string method, string route)
    {
        using var response = await production.SendAsync(method, Route(route));
        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, "AUTHENTICATION_REQUIRED");
        Assert.Empty(production.Management.Calls);
    }

    [Theory]
    [MemberData(nameof(ProtectedRoutes))]
    public async Task AnonymousProductionRequestWithTenantHeaderIsStill401(string method, string route)
    {
        using var response = await production.SendAsync(method, Route(route), request =>
            request.Headers.Add("X-Tenant-Id", tenant.ToString()));
        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, "AUTHENTICATION_REQUIRED");
        Assert.Empty(production.Management.Calls);
    }

    [Fact]
    public async Task InvalidSessionTokenIs401()
    {
        using var response = await production.SendAsync("GET", Route("/api/v1/sites/{site}/competitors"), request =>
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-session"));
        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, "AUTHENTICATION_REQUIRED");
        Assert.Empty(production.Management.Calls);
    }

    [Fact]
    public async Task ValidSessionReachesController()
    {
        using var me = await production.SendAsync("GET", "/api/v1/auth/me", Bearer);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        using var competitors = await production.SendAsync("GET", Route("/api/v1/sites/{site}/competitors"), Bearer);
        Assert.Equal(HttpStatusCode.OK, competitors.StatusCode);
        Assert.Equal(1, production.Management.Count(nameof(IManagementStore.ListCompetitorsAsync)));
    }

    [Fact]
    public async Task HealthLiveIsAnonymous()
    {
        using var response = await production.SendAsync("GET", "/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CollectorLeaseUsesServiceTokenNotSession()
    {
        // 白名单生效：请求进入 Collector 自己的服务令牌认证，返回它的 401 UNAUTHORIZED，而不是会话策略的 AUTHENTICATION_REQUIRED。
        using var response = await production.SendAsync("POST", "/internal/collector/leases", request =>
            request.Content = JsonContent.Create(new { tenantId = tenant, nodeId = "node-1", region = "cn" }));
        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, "UNAUTHORIZED");
        Assert.Empty(production.Collector.Calls);
    }

    [Fact]
    public async Task AnonymousAuthEntriesReachController()
    {
        using var logout = await production.SendAsync("POST", "/api/v1/auth/logout");
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        using var login = await production.SendAsync("POST", "/api/v1/auth/login", request =>
            request.Content = JsonContent.Create(new { email = "nobody@example.test", password = "wrong-password" }));
        await AssertErrorAsync(login, HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        Assert.Equal(1, production.Identity.Count(nameof(IIdentityStore.LoginAsync)));
    }

    [Fact]
    public async Task DevelopmentBridgeWithValidTenantHeaderReachesController()
    {
        using var response = await development.SendAsync("GET", Route("/api/v1/sites/{site}/competitors"), request =>
            request.Headers.Add("X-Tenant-Id", tenant.ToString()));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, development.Management.Count(nameof(IManagementStore.ListCompetitorsAsync)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task DevelopmentWithoutValidTenantHeaderIs401(string? header)
    {
        using var response = await development.SendAsync("GET", Route("/api/v1/sites/{site}/competitors"), request =>
        {
            if (header is not null) request.Headers.Add("X-Tenant-Id", header);
        });
        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, "AUTHENTICATION_REQUIRED");
        Assert.Empty(development.Management.Calls);
    }

    private string Route(string template) => template.Replace("{site}", site.ToString()).Replace("{guid}", Guid.NewGuid().ToString());

    private static void Bearer(HttpRequestMessage request)
        => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", SessionToken);

    private static string Template(Endpoint endpoint) => (endpoint as RouteEndpoint)?.RoutePattern.RawText ?? endpoint.DisplayName ?? string.Empty;

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"期望 {(int)status}，实际 {(int)response.StatusCode}：{text}");
        using var body = JsonDocument.Parse(text);
        Assert.Equal(code, body.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    /// <summary>进程内宿主：真实会话中间件 + 默认会话策略 + 全部 Controller；只注册测试用到的 Store 桩。</summary>
    private sealed class Host : IAsyncDisposable
    {
        private Host(WebApplication app, McpStoreProxy management, McpStoreProxy identity, McpStoreProxy collector)
        {
            App = app;
            Management = management;
            Identity = identity;
            Collector = collector;
            Client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        }

        internal WebApplication App { get; }
        internal HttpClient Client { get; }
        internal McpStoreProxy Management { get; }
        internal McpStoreProxy Identity { get; }
        internal McpStoreProxy Collector { get; }

        internal static async Task<Host> StartAsync(string environment, Guid tenant)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(HealthController).Assembly.GetName().Name,
                ContentRootPath = AppContext.BaseDirectory,
                EnvironmentName = environment
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();

            var (managementStore, management) = McpStoreProxy.Create<IManagementStore>();
            management.Handlers[nameof(IManagementStore.ListCompetitorsAsync)] = _ =>
                Task.FromResult(new CursorPage<CompetitorView>([], null));
            var (identityStore, identity) = McpStoreProxy.Create<IIdentityStore>();
            identity.Handlers[nameof(IIdentityStore.ValidateSessionAsync)] = args => Task.FromResult(
                (string)args[0]! == SessionToken
                    ? new AuthenticatedRequestContext(new(Guid.NewGuid(), "member@example.test", null, "active",
                        [new(tenant, "Team", "member", "active")]), Guid.NewGuid(), SessionToken)
                    : null);
            identity.Handlers[nameof(IIdentityStore.LoginAsync)] = _ => Task.FromResult<AuthSessionView?>(null);
            var (collectorStore, collector) = McpStoreProxy.Create<IVisibilityCollectorStore>();
            // 登录频控桩：始终放行；频控行为由 AuthLoginThrottleTests 覆盖。
            var (throttleStore, throttle) = McpStoreProxy.Create<ILoginThrottleStore>();
            throttle.Handlers[nameof(ILoginThrottleStore.CheckAsync)] = _ => Task.FromResult(LoginThrottleDecision.Allowed);
            throttle.Handlers[nameof(ILoginThrottleStore.RecordResultAsync)] = _ => Task.CompletedTask;

            builder.Services.AddSingleton(managementStore);
            builder.Services.AddSingleton(identityStore);
            builder.Services.AddSingleton(collectorStore);
            builder.Services.AddSingleton(throttleStore);
            builder.Services.AddSingleton(TimeProvider.System);
            // 只为激活 CollectorController 构造；测试路径不会打开连接。
            builder.Services.AddSingleton(new MySqlDatabase("Server=127.0.0.1;Port=1;Database=unused;User ID=unused;Password=unused"));
            builder.Services.AddSingleton<TenantContextResolver>();
            builder.Services.AddJogeoaiSessionAuthorization();
            builder.Services.AddControllers().AddApplicationPart(typeof(HealthController).Assembly);

            var app = builder.Build();
            app.UseMiddleware<SessionAuthenticationMiddleware>();
            app.UseAuthorization();
            app.MapControllers().RequireJogeoaiSession();
            await app.StartAsync();
            return new Host(app, management, identity, collector);
        }

        internal IReadOnlyList<(Endpoint Endpoint, ControllerActionDescriptor Action)> ControllerEndpoints()
            => ((IEndpointRouteBuilder)App).DataSources.SelectMany(source => source.Endpoints)
                .Select(endpoint => (Endpoint: endpoint, Action: endpoint.Metadata.GetMetadata<ControllerActionDescriptor>()))
                .Where(item => item.Action is not null)
                .Select(item => (item.Endpoint, item.Action!))
                .ToArray();

        internal async Task<HttpResponseMessage> SendAsync(string method, string path, Action<HttpRequestMessage>? configure = null)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (method is "POST" or "PUT" or "PATCH") request.Content = JsonContent.Create(new { });
            configure?.Invoke(request);
            return await Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }
}
