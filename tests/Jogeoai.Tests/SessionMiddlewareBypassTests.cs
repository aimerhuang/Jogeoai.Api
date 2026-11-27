using System.Net;
using System.Text;
using Jogeoai.Api;
using Jogeoai.Api.Controllers;
using Jogeoai.Api.Mcp;
using Jogeoai.Application;
using Microsoft.AspNetCore.Http;

namespace Jogeoai.Tests;

/// <summary>
/// 会话中间件对 <c>/internal</c> 与 <c>/mcp</c> 不查会话（RB3）。进程内 Kestrel，<see cref="IIdentityStore"/> 是计数桩：
/// 这两个前缀下带 Bearer 或会话 Cookie 的请求，<c>ValidateSessionAsync</c> 调用数为 0，各自的令牌鉴权照常工作；
/// <c>/api/v1</c> 与只是前缀相似的路径照常查询。不连数据库。
/// </summary>
public sealed class SessionMiddlewareBypassTests : IAsyncLifetime
{
    private const string Ping = """{"jsonrpc":"2.0","id":1,"method":"ping"}""";

    private CollectorPreauthTestHost host = null!;

    public async Task InitializeAsync() => host = await CollectorPreauthTestHost.StartAsync();

    public async Task DisposeAsync() => await host.DisposeAsync();

    private int SessionLookups => host.Identity.Count(nameof(IIdentityStore.ValidateSessionAsync));

    [Theory]
    [InlineData("/internal", true)]
    [InlineData("/internal/", true)]
    [InlineData("/internal/collector/health", true)]
    [InlineData("/internal/collector/app/leases", true)]
    [InlineData("/INTERNAL/Collector/App/leases", true)]
    [InlineData("/mcp", true)]
    [InlineData("/mcp/", true)]
    [InlineData("/Mcp/anything", true)]
    [InlineData("", false)]
    [InlineData("/", false)]
    [InlineData("/internalx", false)]
    [InlineData("/internal-tools/x", false)]
    [InlineData("/mcp-tokens", false)]
    [InlineData("/api/v1/mcp-tokens", false)]
    [InlineData("/api/v1/auth/me", false)]
    [InlineData("/api/internal/collector", false)]
    [InlineData("/health/live", false)]
    public void SkipsSessionMatchesWholeSegmentsCaseInsensitively(string path, bool skipped)
        => Assert.Equal(skipped, SessionAuthenticationMiddleware.SkipsSession(new PathString(path)));

    [Fact]
    public async Task AppCollectorRequestsWithABearerOrCookieNeverLookUpASession()
    {
        var route = CollectorPreauthTestHost.Route("/internal/collector/app/leases");

        // 会话令牌不是节点令牌：进入节点鉴权并得到它的 401，没有查会话。
        using var sessionBearer = await host.PostAsync(route.Path, route.ValidBody, CollectorPreauthTestHost.SessionToken);
        Assert.Equal(HttpStatusCode.Unauthorized, sessionBearer.StatusCode);
        Assert.Equal("UNAUTHORIZED", (await CollectorPreauthTestHost.ErrorAsync(sessionBearer)).Code);

        using var nodeBearer = await host.PostAsync(route.Path, route.ValidBody, CollectorPreauthTestHost.NodeToken);
        Assert.Equal(HttpStatusCode.OK, nodeBearer.StatusCode);

        using var withCookie = await host.PostAsync(route.Path, Encoding.UTF8.GetBytes(route.ValidBody), CollectorPreauthTestHost.NodeToken,
            configure: request => request.Headers.Add("Cookie", $"{AuthController.CookieName}={CollectorPreauthTestHost.SessionToken}"));
        Assert.Equal(HttpStatusCode.OK, withCookie.StatusCode);

        using var upperCase = await host.PostAsync("/INTERNAL/collector/app/leases", route.ValidBody, CollectorPreauthTestHost.NodeToken);
        Assert.Equal(HttpStatusCode.OK, upperCase.StatusCode);

        Assert.Equal(0, SessionLookups);
        // 节点鉴权照常：四个带 Bearer 的请求各查一次节点表。
        Assert.Equal(4, host.NodeLookups);
    }

    [Fact]
    public async Task WebCollectorRequestsAndHealthNeverLookUpASession()
    {
        var route = CollectorPreauthTestHost.Route("/internal/collector/leases");

        using var lease = await host.PostAsync(route.Path, route.ValidBody, CollectorPreauthTestHost.WebToken);
        Assert.Equal(HttpStatusCode.OK, lease.StatusCode);
        using var wrong = await host.PostAsync(route.Path, route.ValidBody, CollectorPreauthTestHost.SessionToken);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        using var health = await host.GetAsync("/internal/collector/health", CollectorPreauthTestHost.SessionToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, health.StatusCode);

        Assert.Equal(0, SessionLookups);
    }

    [Fact]
    public async Task McpRequestsNeverLookUpASessionAndStillRequireAnMcpToken()
    {
        // 会话 Bearer、会话 Cookie 在 /mcp 上仍然是 401（MCP 中间件的拒绝），而且没有为它们查会话。
        using var sessionBearer = await McpAsync("/mcp", request => request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + CollectorPreauthTestHost.SessionToken));
        Assert.Equal(HttpStatusCode.Unauthorized, sessionBearer.StatusCode);
        Assert.Contains(McpTokenAuthenticationMiddleware.InvalidTokenCode, await sessionBearer.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var cookie = await McpAsync("/mcp", request => request.Headers.Add("Cookie", $"{AuthController.CookieName}={CollectorPreauthTestHost.SessionToken}"));
        Assert.Equal(HttpStatusCode.Unauthorized, cookie.StatusCode);

        using var upperCase = await McpAsync("/MCP", request => request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + CollectorPreauthTestHost.SessionToken));
        Assert.Equal(HttpStatusCode.Unauthorized, upperCase.StatusCode);

        // 有效的 MCP 令牌照常通过。
        var (token, _) = host.McpTokens.Issue(CollectorPreauthTestHost.Tenant);
        using var valid = await McpAsync("/mcp", request => request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token));
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);

        Assert.Equal(0, SessionLookups);
    }

    [Fact]
    public async Task ApiRequestsStillLoadTheSession()
    {
        using var me = await host.GetAsync("/api/v1/auth/me", CollectorPreauthTestHost.SessionToken);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(1, SessionLookups);

        using var invalid = await host.GetAsync("/api/v1/auth/me", "not-a-session");
        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
        Assert.Equal(2, SessionLookups);

        using var cookie = await host.GetAsync("/api/v1/auth/me",
            configure: request => request.Headers.Add("Cookie", $"{AuthController.CookieName}={CollectorPreauthTestHost.SessionToken}"));
        Assert.Equal(HttpStatusCode.OK, cookie.StatusCode);
        Assert.Equal(3, SessionLookups);

        // 没有令牌时本来就不查。
        using var anonymous = await host.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(3, SessionLookups);
    }

    [Theory]
    [InlineData("/internalx/collector/app/leases")]
    [InlineData("/mcp-tokens")]
    [InlineData("/api/internal/collector/app/leases")]
    public async Task PathsThatOnlyResembleThePrefixesAreNotSkipped(string path)
    {
        using var response = await host.GetAsync(path, CollectorPreauthTestHost.SessionToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, SessionLookups);
    }

    /// <summary>
    /// 路径规范化：请求行原样发出（原始 TCP），带一个有效的会话令牌。会话中间件与路由看到的是同一个规范化后的路径，所以
    /// 要么路径被跳过且只能到达采集接口（health 的 503 是测试宿主数据库不可达的 degraded），要么照常查会话；
    /// 没有哪一种写法能让要求会话的 /api/v1/auth/me 在不查会话的情况下被访问，也没有哪一种写法让它因为被跳过而变成 401。
    /// </summary>
    [Theory]
    // 被跳过：规范化之后仍然以 /internal 段开头，只能到达采集控制器。
    [InlineData("/internal/collector/health/", 503, 0)]
    [InlineData("/INTERNAL/COLLECTOR/HEALTH", 503, 0)]
    [InlineData("/internal/collector/health?next=/api/v1/auth/me", 503, 0)]
    [InlineData("/%69nternal/collector/health", 503, 0)]
    [InlineData("/api/v1/../../internal/collector/health", 503, 0)]
    [InlineData("/internal/collector/health/../../../internal/collector/health", 503, 0)]
    // 不被跳过：不是完整的 /internal 段，路由也匹配不到采集接口。
    [InlineData("//internal/collector/health", 404, 1)]
    [InlineData("/internal%2Fcollector%2Fhealth", 404, 1)]
    [InlineData("/internal;x/collector/health", 404, 1)]
    [InlineData("/api/v1/../internal/collector/health", 404, 1)]
    [InlineData("/internal%00/collector/health", 400, 0)]
    // 以 /internal 或 /mcp 开头的写法经规范化后落到 /api/v1：照常查会话并通过。
    [InlineData("/internal/../api/v1/auth/me", 200, 1)]
    [InlineData("/internal/collector/app/../../../api/v1/auth/me", 200, 1)]
    [InlineData("/mcp/../api/v1/auth/me", 200, 1)]
    [InlineData("/internal/%2e%2e/api/v1/auth/me", 200, 1)]
    // 被跳过的前缀下没有要求会话的路由：/internal/api/v1/auth/me 不存在。
    [InlineData("/internal/api/v1/auth/me", 404, 0)]
    public async Task PathNormalisationNeverServesASessionRouteWithoutASessionLookup(string target, int status, int sessionLookups)
    {
        var response = await host.RawAsync("GET", target, CollectorPreauthTestHost.SessionToken);

        Assert.True(status == response.Status, $"{target}：期望 {status}，实际 {response.Status}");
        Assert.Equal(sessionLookups, SessionLookups);
        Assert.Equal(0, host.AppStoreWork + host.WebStoreWork);
    }

    [Fact]
    public async Task TheMcpTokenManagementApiIsNotCaughtByTheMcpPrefix()
    {
        // /api/v1/mcp-tokens 是要求会话的 Controller：没有会话时是会话策略的 401，带会话令牌时照常查会话。
        using var anonymous = await host.GetAsync("/api/v1/mcp-tokens");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal("AUTHENTICATION_REQUIRED", (await CollectorPreauthTestHost.ErrorAsync(anonymous)).Code);
        Assert.Equal(0, SessionLookups);

        using var withSession = await host.GetAsync("/api/v1/mcp-tokens", CollectorPreauthTestHost.SessionToken);
        Assert.NotEqual(HttpStatusCode.Unauthorized, withSession.StatusCode);
        Assert.Equal(1, SessionLookups);
    }

    private async Task<HttpResponseMessage> McpAsync(string path, Action<HttpRequestMessage> configure)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(Ping, Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        configure(request);
        return await host.Client.SendAsync(request);
    }
}
