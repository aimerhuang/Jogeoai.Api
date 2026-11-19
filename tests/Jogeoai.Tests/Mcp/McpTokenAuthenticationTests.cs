using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Jogeoai.Api;
using Jogeoai.Api.Controllers;
using Jogeoai.Api.Mcp;
using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Jogeoai.Tests.Mcp;

/// <summary>
/// 进程内 Kestrel 管道测试（与 Program.cs 共用 McpEndpointExtensions）：有效令牌可列出全部只读工具并调用；
/// 吊销 / 过期令牌、会话 Bearer、浏览器 Cookie、缺 Authorization 访问 /mcp 均为 401；缺表 503；
/// 超过每分钟 60 次返回 429；会话中间件对 /api 照常生效；审计日志含令牌 ID 与工具名、不含令牌明文。
/// </summary>
public sealed class McpTokenAuthenticationTests : IAsyncLifetime
{
    private const string SessionToken = "session-token-for-mcp-tests";
    private static readonly string[] ExpectedTools =
    [
        "get_monitoring_record", "get_monitoring_summary", "get_share_of_voice", "get_visibility",
        "list_citation_domains", "list_monitoring_records", "list_prompts", "list_sites"
    ];

    private readonly FakeMcpTokenStore tokens = new();
    private readonly CapturingLoggerProvider logs = new();
    private readonly Guid tenant = Guid.NewGuid();
    private readonly Guid site = Guid.NewGuid();
    private McpStoreProxy management = null!;
    private WebApplication app = null!;
    private HttpClient http = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(McpTokensController).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders().AddProvider(logs).SetMinimumLevel(LogLevel.Information);

        var (managementStore, managementProxy) = McpStoreProxy.Create<IManagementStore>();
        management = managementProxy;
        management.Handlers[nameof(IManagementStore.ListBrandsAsync)] = args =>
        {
            Assert.Equal(tenant, args[0]);
            return Task.FromResult(new CursorPage<BrandView>([McpFixtures.Brand(site)], null));
        };
        management.Handlers[nameof(IManagementStore.GetBrandAsync)] = args =>
            Task.FromResult((Guid)args[0]! == tenant && (Guid)args[1]! == site ? McpFixtures.Brand(site) : null);
        var (analyticsStore, _) = McpStoreProxy.Create<IAnalyticsQueryStore>();
        var (identityStore, identityProxy) = McpStoreProxy.Create<IIdentityStore>();
        identityProxy.Handlers[nameof(IIdentityStore.ValidateSessionAsync)] = args => Task.FromResult(
            (string)args[0]! == SessionToken
                ? new AuthenticatedRequestContext(new(Guid.NewGuid(), "owner@example.test", null, "active",
                    [new(tenant, "Team", "owner", "active")]), Guid.NewGuid(), SessionToken)
                : null);

        builder.Services.AddSingleton<IMcpTokenStore>(tokens);
        builder.Services.AddSingleton(managementStore);
        builder.Services.AddSingleton(analyticsStore);
        builder.Services.AddSingleton(identityStore);
        builder.Services.AddRateLimiter(options =>
        {
            options.OnRejected = (context, _) =>
            {
                context.HttpContext.Response.StatusCode = 429;
                return ValueTask.CompletedTask;
            };
            options.AddMcpTokenPolicy();
        });
        builder.Services.AddJogeoaiMcp();

        app = builder.Build();
        app.UseMiddleware<SessionAuthenticationMiddleware>();
        app.UseMcpTokenAuthentication();
        app.UseRateLimiter();
        // 代表 /api/v1：只报告会话中间件是否加载了身份。
        app.MapGet("/api/v1/probe", (HttpContext context) =>
            Results.Text(SessionAuthenticationMiddleware.GetContext(context) is null ? "anonymous" : "session"));
        app.MapJogeoaiMcp();
        await app.StartAsync();
        http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
    }

    public async Task DisposeAsync()
    {
        http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
    }

    [Fact]
    public async Task ValidTokenListsAllReadToolsAndCallsAreAudited()
    {
        var (token, principal) = tokens.Issue(tenant);
        await using var client = await ConnectAsync(token);

        var tools = await client.ListToolsAsync();
        Assert.Equal(ExpectedTools, tools.Select(tool => tool.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.All(tools, tool => Assert.Contains(tool.Description!, character => character is >= '一' and <= '鿿'));

        var sites = await client.CallToolAsync("list_sites", new Dictionary<string, object?>());
        Assert.NotEqual(true, sites.IsError);
        Assert.Contains(site.ToString(), Text(sites), StringComparison.Ordinal);
        Assert.Contains("\"siteId\"", Text(sites), StringComparison.Ordinal);

        var denied = await client.CallToolAsync("get_visibility", new Dictionary<string, object?> { ["siteId"] = Guid.NewGuid() });
        Assert.True(denied.IsError);
        Assert.Contains(McpScope.SiteDenied, Text(denied), StringComparison.Ordinal);

        var audit = logs.Entries.Where(entry => entry.Message.StartsWith("MCP tool call", StringComparison.Ordinal)).ToArray();
        Assert.Contains(audit, entry => entry.Message.Contains(principal.TokenId.ToString(), StringComparison.Ordinal)
            && entry.Message.Contains("list_sites", StringComparison.Ordinal) && entry.Message.Contains(" ok ", StringComparison.Ordinal));
        Assert.Contains(audit, entry => entry.Message.Contains("get_visibility", StringComparison.Ordinal)
            && entry.Message.Contains("denied", StringComparison.Ordinal));
        Assert.All(audit, entry => Assert.Contains(entry.State, pair => pair.Key == "TokenId" && Equals(pair.Value, principal.TokenId)));
        Assert.DoesNotContain(logs.Entries, entry => entry.Message.Contains(token, StringComparison.Ordinal)
            || entry.Message.Contains(principal.TokenHash, StringComparison.Ordinal)
            || entry.State.Any(pair => pair.Value?.ToString() is { } value
                && (value.Contains(token, StringComparison.Ordinal) || value.Contains(principal.TokenHash, StringComparison.Ordinal))));
        Assert.True(tokens.Touches >= 1);
    }

    [Fact]
    public async Task RevokedOrExpiredTokenIs401()
    {
        var (token, _) = tokens.Issue(tenant);
        Assert.NotEqual(HttpStatusCode.Unauthorized, (await PostAsync(Bearer(token))).StatusCode);

        tokens.Deactivate(token);
        var response = await PostAsync(Bearer(token));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(McpTokenAuthenticationMiddleware.InvalidTokenCode, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionBearerCookieAndMissingHeaderAre401OnMcpButSessionStillWorksOnApi()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(Bearer(SessionToken))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(request =>
            request.Headers.Add("Cookie", $"{AuthController.CookieName}={SessionToken}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(_ => { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(request =>
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", "Zm9vOmJhcg=="))).StatusCode);

        using var probe = new HttpRequestMessage(HttpMethod.Get, "/api/v1/probe");
        probe.Headers.Authorization = new AuthenticationHeaderValue("Bearer", SessionToken);
        Assert.Equal("session", await (await http.SendAsync(probe)).Content.ReadAsStringAsync());
        // MCP 令牌不是会话令牌，/api 不会因此获得身份。
        var (token, _) = tokens.Issue(tenant);
        using var probeWithMcp = new HttpRequestMessage(HttpMethod.Get, "/api/v1/probe");
        probeWithMcp.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal("anonymous", await (await http.SendAsync(probeWithMcp)).Content.ReadAsStringAsync());
        Assert.Equal(0, tokens.Touches);
    }

    [Fact]
    public async Task MissingSchemaIs503()
    {
        var (token, _) = tokens.Issue(tenant);
        tokens.SchemaMissing = true;
        var response = await PostAsync(Bearer(token));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("MCP_SCHEMA_NOT_MIGRATED", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestsBeyondSixtyPerMinutePerTokenAre429AndOtherTokensAreUnaffected()
    {
        var (token, _) = tokens.Issue(tenant);
        for (var index = 0; index < McpTokenRateLimit.PermitLimit; index++)
            Assert.NotEqual(HttpStatusCode.TooManyRequests, (await PostAsync(Bearer(token))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostAsync(Bearer(token))).StatusCode);

        var (other, _) = tokens.Issue(tenant);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await PostAsync(Bearer(other))).StatusCode);
    }

    private static Action<HttpRequestMessage> Bearer(string token)
        => request => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private async Task<HttpResponseMessage> PostAsync(Action<HttpRequestMessage> configure)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        configure(request);
        return await http.SendAsync(request);
    }

    private async Task<McpClient> ConnectAsync(string token)
        => await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token }
        }));

    private static string Text(CallToolResult result)
        => string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
}
