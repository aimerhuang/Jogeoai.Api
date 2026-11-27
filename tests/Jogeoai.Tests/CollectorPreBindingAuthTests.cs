using System.Net;
using System.Reflection;
using System.Text.Json;
using Jogeoai.Api.Controllers;
using Jogeoai.Api.Security;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Jogeoai.Tests;

/// <summary>
/// 采集接口先鉴权后读请求体（RB2）。进程内 Kestrel + Store 桩：令牌缺失或错误时返回 401，损坏的 JSON 也不会先变成 400，
/// 也不等待请求体；令牌正确时损坏的 JSON 仍是 400，合法请求照常到达 Store 且节点表只查一次；
/// 过滤器的失败响应与动作自己返回的状态码、响应体逐字节相同；nodeId、区域、租户名单的检查仍在动作内；health 仍匿名。
/// </summary>
public sealed class CollectorPreBindingAuthTests : IAsyncLifetime
{
    private const string Malformed = "{\"nodeId\":";
    private const string AppUnauthorized = """{"error":{"code":"UNAUTHORIZED","message":"App collector node authentication failed."}}""";
    private const string WebUnauthorized = """{"error":{"code":"UNAUTHORIZED","message":"Collector service authentication failed."}}""";

    /// <summary>不发请求体时答复应当立即返回；这个上限远低于“等请求体”会出现的无限等待，也低于 Kestrel 丢弃未读请求体的 5 秒。</summary>
    private static readonly TimeSpan NoBodyWait = TimeSpan.FromSeconds(3);

    private CollectorPreauthTestHost host = null!;

    public async Task InitializeAsync() => host = await CollectorPreauthTestHost.StartAsync();

    public async Task DisposeAsync() => await host.DisposeAsync();

    public static TheoryData<string> AllPaths() => CollectorPreauthTestHost.PathData();

    public static TheoryData<string> AppPaths()
    {
        var data = new TheoryData<string>();
        foreach (var route in CollectorPreauthTestHost.Routes)
            if (route.Controller == typeof(AppCollectorController)) data.Add(route.Path);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllPaths))]
    public async Task AMissingOrWrongTokenWithMalformedJsonIs401AndNoStoreWorkHappens(string path)
    {
        var route = CollectorPreauthTestHost.Route(path);
        var isApp = route.Controller == typeof(AppCollectorController);
        // 另一条通道的令牌在这里同样无效。
        var otherChannel = isApp ? CollectorPreauthTestHost.WebToken : CollectorPreauthTestHost.NodeToken;

        foreach (var bearer in new[] { null, "", "wrong-token", otherChannel })
        {
            using var response = await host.PostAsync(path, Malformed, bearer);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
            Assert.Equal(isApp ? AppUnauthorized : WebUnauthorized, await response.Content.ReadAsStringAsync());
        }

        Assert.Equal(0, host.AppStoreWork);
        Assert.Equal(0, host.WebStoreWork);
        // 没有 Bearer 令牌时连节点表都不查；带令牌时只查了令牌。网页通道不查库。
        string?[] lookedUp = isApp ? ["wrong-token", otherChannel] : [];
        Assert.Equal(lookedUp, host.AppPool.Calls.Select(call => (string?)call.Args[0]).ToArray());
    }

    [Theory]
    [MemberData(nameof(AllPaths))]
    public async Task AWrongTokenIs401WithoutWaitingForTheBody(string path)
    {
        // 声明 512 字节但一个字节都不发：鉴权若发生在读取请求体之后，这个请求会一直等到超时。
        var response = await host.RawPostAsync(path, "wrong-token", "Content-Length: 512");

        Assert.Equal(401, response.Status);
        Assert.Contains("\"code\":\"UNAUTHORIZED\"", response.Text, StringComparison.Ordinal);
        Assert.True(response.Elapsed < NoBodyWait, $"401 用了 {response.Elapsed.TotalMilliseconds:F0} ms，像是在等请求体。");
    }

    [Theory]
    [MemberData(nameof(AllPaths))]
    public async Task TheCorrectTokenWithMalformedJsonIsStill400(string path)
    {
        var route = CollectorPreauthTestHost.Route(path);

        using var response = await host.PostAsync(path, Malformed, route.Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // 框架默认的校验错误体，不是采集接口自己的 error 对象。
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.TryGetProperty("error", out _));
        Assert.True(body.RootElement.TryGetProperty("errors", out _));
        Assert.Equal(0, host.AppStoreWork);
        Assert.Equal(0, host.WebStoreWork);
        Assert.Equal(route.Controller == typeof(AppCollectorController) ? 1 : 0, host.NodeLookups);
    }

    [Theory]
    [MemberData(nameof(AllPaths))]
    public async Task TheCorrectTokenAndAValidBodyReachTheStoreWithASingleNodeLookup(string path)
    {
        var route = CollectorPreauthTestHost.Route(path);
        var isApp = route.Controller == typeof(AppCollectorController);

        using var response = await host.PostAsync(path, route.ValidBody, route.Token);

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}：{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        Assert.Equal((isApp ? 1 : 0, isApp ? 0 : 1), (host.AppStoreWork, host.WebStoreWork));
        // 过滤器查过一次节点表，动作从 HttpContext.Items 读取，不再查第二次。
        Assert.Equal(isApp ? 1 : 0, host.NodeLookups);
    }

    [Fact]
    public async Task TheAuthenticatedNodeFromTheTokenIsWhatTheStoreReceives()
    {
        var route = CollectorPreauthTestHost.Route("/internal/collector/app/leases");

        using var response = await host.PostAsync(route.Path, route.ValidBody, route.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"task":null,"reason":"no_task","retryAfterSeconds":60}""", await response.Content.ReadAsStringAsync());
        var command = Assert.IsType<AppLeaseCommand>(Assert.Single(host.AppPool.Calls,
            call => call.Method == nameof(IAppCollectorPoolStore.LeaseAsync)).Args[0]);
        Assert.Equal(new AppLeaseCommand(CollectorPreauthTestHost.NodeId, CollectorPreauthTestHost.Id, 60), command);
    }

    [Fact]
    public async Task TheNodeIdChecksStayInTheActionAfterTheTokenCheck()
    {
        var path = "/internal/collector/app/leases";
        string Body(string? nodeId) => JsonSerializer.Serialize(
            new { nodeId, bindingId = CollectorPreauthTestHost.Id, leaseSeconds = 60, dispatchProtocol = "device-v1", deviceOnline = true });

        using var other = await host.PostAsync(path, Body("another-node"), CollectorPreauthTestHost.NodeToken);
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        Assert.Equal(("APP_NODE_MISMATCH", "请求里的 nodeId 与令牌对应的节点不一致。"), await CollectorPreauthTestHost.ErrorAsync(other));

        using var differentCase = await host.PostAsync(path, Body("MAC-1"), CollectorPreauthTestHost.NodeToken);
        Assert.Equal(HttpStatusCode.Forbidden, differentCase.StatusCode);
        Assert.Equal("APP_NODE_MISMATCH", (await CollectorPreauthTestHost.ErrorAsync(differentCase)).Code);

        using var missing = await host.PostAsync(path, Body(null), CollectorPreauthTestHost.NodeToken);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(("INVALID_REQUEST", "nodeId 必须为 1–128 个字符。"), await CollectorPreauthTestHost.ErrorAsync(missing));

        // 令牌错误时，nodeId 再怎么不对也只是 401。
        using var wrongToken = await host.PostAsync(path, Body("another-node"), "wrong-token");
        Assert.Equal(HttpStatusCode.Unauthorized, wrongToken.StatusCode);

        Assert.Equal(0, host.AppStoreWork);
        Assert.Equal(4, host.NodeLookups);
    }

    [Fact]
    public async Task ADisabledNodeIs403BeforeTheBodyIsReadOrParsed()
    {
        host.NodeStatus = AppNodeStatus.Disabled;

        using var malformed = await host.PostAsync("/internal/collector/app/results", Malformed, CollectorPreauthTestHost.NodeToken);
        Assert.Equal(HttpStatusCode.Forbidden, malformed.StatusCode);
        Assert.Equal(("APP_NODE_DISABLED", "该采集节点已被停用。"), await CollectorPreauthTestHost.ErrorAsync(malformed));

        var noBody = await host.RawPostAsync("/internal/collector/app/leases", CollectorPreauthTestHost.NodeToken, "Content-Length: 512");
        Assert.Equal(403, noBody.Status);
        Assert.Contains("\"code\":\"APP_NODE_DISABLED\"", noBody.Text, StringComparison.Ordinal);
        Assert.True(noBody.Elapsed < NoBodyWait, $"403 用了 {noBody.Elapsed.TotalMilliseconds:F0} ms，像是在等请求体。");

        Assert.Equal(0, host.AppStoreWork);
    }

    [Fact]
    public async Task AMissingMigrationIs503FromTheTokenLookup()
    {
        host.NodeLookupError = new CollectorStoreException("APP_CHANNEL_SCHEMA_NOT_MIGRATED", "App 渠道所需迁移 049 尚未执行。", 503);

        using var response = await host.PostAsync("/internal/collector/app/leases", Malformed, CollectorPreauthTestHost.NodeToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(("APP_CHANNEL_SCHEMA_NOT_MIGRATED", "App 渠道所需迁移 049 尚未执行。"), await CollectorPreauthTestHost.ErrorAsync(response));
        // 没有令牌时不查库，仍然是 401，不是 503。
        using var anonymous = await host.PostAsync("/internal/collector/app/leases", Malformed);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(0, host.AppStoreWork);
    }

    [Fact]
    public async Task ANodeIsAuthenticatedAgainOnEveryRequestOfOneConnection()
    {
        var route = CollectorPreauthTestHost.Route("/internal/collector/app/leases");
        // 同一个 HttpClient、同一条保持的连接：上一个请求鉴权得到的节点不会留给下一个请求。
        using var first = await host.PostAsync(route.Path, route.ValidBody, route.Token);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var withoutToken = await host.PostAsync(route.Path, route.ValidBody);
        Assert.Equal(HttpStatusCode.Unauthorized, withoutToken.StatusCode);

        host.NodeStatus = AppNodeStatus.Disabled;
        using var disabled = await host.PostAsync(route.Path, route.ValidBody, route.Token);
        Assert.Equal(HttpStatusCode.Forbidden, disabled.StatusCode);
        Assert.Equal("APP_NODE_DISABLED", (await CollectorPreauthTestHost.ErrorAsync(disabled)).Code);

        host.NodeStatus = AppNodeStatus.Active;
        using var enabledAgain = await host.PostAsync(route.Path, route.ValidBody, route.Token);
        Assert.Equal(HttpStatusCode.OK, enabledAgain.StatusCode);

        // 带令牌的三个请求各查一次节点表；只有两个通过的请求到达 Store。
        Assert.Equal((3, 2), (host.NodeLookups, host.AppStoreWork));
    }

    [Theory]
    [MemberData(nameof(AppPaths))]
    public async Task AnUnexpectedStoreFailureDuringTheTokenLookupNeverReachesTheAction(string path)
    {
        // 过滤器只把 CollectorStoreException 转成响应；其他异常原样抛出，请求以 500 结束，不会“没有结果也没有节点”地继续执行动作。
        host.NodeLookupError = new TimeoutException("simulated store outage");
        var route = CollectorPreauthTestHost.Route(path);

        using var response = await host.PostAsync(path, route.ValidBody, route.Token);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        Assert.Equal((1, 0), (host.NodeLookups, host.AppStoreWork));
    }

    /// <summary>
    /// 过滤器（HTTP，损坏的 JSON 请求体）与动作自身（直接调用，同一个 Store 桩、同一个请求头）对同一种失败给出相同的状态码，
    /// 响应体与用宿主的 MVC JSON 选项序列化动作返回值得到的字节完全相同。
    /// </summary>
    [Fact]
    public async Task AppFilterFailuresAreByteForByteTheActionsOwnErrorResponses()
    {
        var store = host.Services.GetRequiredService<IAppCollectorPoolStore>();
        var cases = new (string Name, string? Authorization, Action Arrange, int Status, string Code)[]
        {
            ("no header", null, () => { }, 401, "UNAUTHORIZED"),
            ("not bearer", "Basic " + CollectorPreauthTestHost.NodeToken, () => { }, 401, "UNAUTHORIZED"),
            ("unknown token", "Bearer unknown-token", () => { }, 401, "UNAUTHORIZED"),
            ("disabled node", "Bearer " + CollectorPreauthTestHost.NodeToken, () => host.NodeStatus = AppNodeStatus.Disabled, 403, "APP_NODE_DISABLED"),
            ("missing migration", "Bearer " + CollectorPreauthTestHost.NodeToken,
                () => host.NodeLookupError = new CollectorStoreException("APP_CHANNEL_SCHEMA_NOT_MIGRATED", "迁移 049 尚未执行。", 503),
                503, "APP_CHANNEL_SCHEMA_NOT_MIGRATED"),
        };

        foreach (var (name, authorization, arrange, status, code) in cases)
        {
            host.NodeStatus = AppNodeStatus.Active;
            host.NodeLookupError = null;
            arrange();

            var context = new DefaultHttpContext();
            if (authorization is not null) context.Request.Headers.Authorization = authorization;
            var controller = new AppCollectorController(store) { ControllerContext = new() { HttpContext = context } };
            var direct = Assert.IsType<ObjectResult>((await controller.Lease(
                new AppLeaseRequest(CollectorPreauthTestHost.NodeId, CollectorPreauthTestHost.Id.ToString(), 60, "device-v1", true), default)).Result);

            using var response = await host.PostAsync("/internal/collector/app/leases", System.Text.Encoding.UTF8.GetBytes(Malformed),
                configure: request => { if (authorization is not null) request.Headers.TryAddWithoutValidation("Authorization", authorization); });

            Assert.True(status == direct.StatusCode && status == (int)response.StatusCode,
                $"{name}：期望 {status}，动作 {direct.StatusCode}，过滤器 {(int)response.StatusCode}");
            Assert.Equal(MvcJson(direct.Value), await response.Content.ReadAsByteArrayAsync());
            Assert.Equal(code, (await CollectorPreauthTestHost.ErrorAsync(response)).Code);
            Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        }
    }

    [Theory]
    [InlineData(CollectorPreauthTestHost.WebToken)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task WebFilterFailuresAreByteForByteTheActionsOwnErrorResponses(string configuredToken)
    {
        // 服务端令牌为空或空白时，任何请求都是 401，包括不带令牌和带空令牌的请求。
        await using var webHost = await CollectorPreauthTestHost.StartAsync(("Collector:ChinaServiceToken", configuredToken));
        var configuration = webHost.Services.GetRequiredService<IConfiguration>();
        var rejected = new List<string?> { null, "Bearer", "Bearer wrong-token", "Basic " + CollectorPreauthTestHost.WebToken, "Bearer " + CollectorPreauthTestHost.NodeToken };
        if (configuredToken != CollectorPreauthTestHost.WebToken) rejected.Add("Bearer " + CollectorPreauthTestHost.WebToken);

        foreach (var authorization in rejected)
        {
            var context = new DefaultHttpContext();
            if (authorization is not null) context.Request.Headers.Authorization = authorization;
            var controller = new CollectorController(webHost.Services.GetRequiredService<IVisibilityCollectorStore>(), configuration,
                webHost.Services.GetRequiredService<MySqlDatabase>()) { ControllerContext = new() { HttpContext = context } };
            var direct = Assert.IsType<ObjectResult>((await controller.Lease(
                new CollectorLeaseRequest(CollectorPreauthTestHost.Tenant.ToString(), "web-1", "cn", ["doubao"]), default)).Result);

            using var response = await webHost.PostAsync("/internal/collector/leases", System.Text.Encoding.UTF8.GetBytes(Malformed),
                configure: request => { if (authorization is not null) request.Headers.TryAddWithoutValidation("Authorization", authorization); });

            Assert.Equal((401, HttpStatusCode.Unauthorized), (direct.StatusCode, response.StatusCode));
            var body = await response.Content.ReadAsByteArrayAsync();
            Assert.Equal(MvcJson(direct.Value), body);
            Assert.Equal(WebUnauthorized, System.Text.Encoding.UTF8.GetString(body));
        }

        Assert.Equal(0, webHost.WebStoreWork);
    }

    [Fact]
    public async Task RegionAndTenantChecksStayInTheActionAndRunOnlyAfterTheToken()
    {
        var allowed = Guid.NewGuid();
        await using var webHost = await CollectorPreauthTestHost.StartAsync(("Collector:ChinaTenantIds:0", allowed.ToString()));
        string Lease(string tenantId, string region) => JsonSerializer.Serialize(new { tenantId, nodeId = "web-1", region, supportedPlatformCodes = new[] { "doubao" } });

        using var region = await webHost.PostAsync("/internal/collector/leases", Lease(allowed.ToString(), "us"), CollectorPreauthTestHost.WebToken);
        Assert.Equal(HttpStatusCode.Forbidden, region.StatusCode);
        Assert.Equal(("COLLECTOR_REGION_FORBIDDEN", "Only cn collectors are enabled."), await CollectorPreauthTestHost.ErrorAsync(region));

        using var tenant = await webHost.PostAsync("/internal/collector/leases", Lease(CollectorPreauthTestHost.Tenant.ToString(), "cn"), CollectorPreauthTestHost.WebToken);
        Assert.Equal(HttpStatusCode.Forbidden, tenant.StatusCode);
        Assert.Equal(("TENANT_NOT_ALLOWED", "Collector tenant is not allowed."), await CollectorPreauthTestHost.ErrorAsync(tenant));

        using var invalidTenant = await webHost.PostAsync("/internal/collector/leases", Lease("not-a-guid", "cn"), CollectorPreauthTestHost.WebToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalidTenant.StatusCode);
        Assert.Equal("INVALID_TENANT", (await CollectorPreauthTestHost.ErrorAsync(invalidTenant)).Code);
        Assert.Equal(0, webHost.WebStoreWork);

        using var ok = await webHost.PostAsync("/internal/collector/leases", Lease(allowed.ToString(), "cn"), CollectorPreauthTestHost.WebToken);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(1, webHost.Web.Count(nameof(IVisibilityCollectorStore.LeaseAsync)));

        // 令牌错误时不再看请求体：区域不对、租户不合法都只得到 401（此前分别是 403 和 400）。
        foreach (var body in new[] { Lease(allowed.ToString(), "us"), Lease("not-a-guid", "cn") })
        {
            using var wrongToken = await webHost.PostAsync("/internal/collector/leases", body, "wrong-token");
            Assert.Equal(HttpStatusCode.Unauthorized, wrongToken.StatusCode);
            Assert.Equal(WebUnauthorized, await wrongToken.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task HealthStaysAnonymousAndUnfiltered()
    {
        // 没有令牌：进入动作，测试宿主的数据库不可达，所以是 degraded，而不是 401。
        using var response = await host.GetAsync("/internal/collector/health");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("""{"status":"degraded","region":"cn","database":"unavailable"}""", await response.Content.ReadAsStringAsync());

        var health = typeof(CollectorController).GetMethod(nameof(CollectorController.Health))!;
        Assert.Null(health.GetCustomAttribute<CollectorServiceTokenFilter>());
        Assert.Null(typeof(CollectorController).GetCustomAttribute<CollectorServiceTokenFilter>());
    }

    [Fact]
    public void NoActionBypassesItsFilter()
    {
        var endpoints = host.CollectorEndpoints;
        Assert.Equal(13, endpoints.Count);
        foreach (var (endpoint, action) in endpoints)
        {
            var isApp = action.ControllerTypeInfo.AsType() == typeof(AppCollectorController);
            var isHealth = action.MethodInfo.Name == nameof(CollectorController.Health) && !isApp;
            var filters = action.FilterDescriptors.Select(descriptor => descriptor.Filter).ToArray();
            Assert.Equal(isApp ? 1 : 0, filters.OfType<AppNodeAuthenticationFilter>().Count());
            Assert.Equal(isApp || isHealth ? 0 : 1, filters.OfType<CollectorServiceTokenFilter>().Count());
            // 鉴权过滤器的顺序为 0，先于请求体上限过滤器（900）；两者都在模型绑定之前。
            Assert.Equal(isHealth ? 0 : 1, filters.OfType<RequestSizeLimitAttribute>().Count());
            Assert.Equal(isHealth, endpoint.Metadata.GetMetadata<IRequestSizeLimitMetadata>() is null);
            Assert.All(action.FilterDescriptors.Where(descriptor => descriptor.Filter is AppNodeAuthenticationFilter or CollectorServiceTokenFilter),
                descriptor => Assert.Equal(0, descriptor.Order));
            Assert.All(action.FilterDescriptors.Where(descriptor => descriptor.Filter is RequestSizeLimitAttribute),
                descriptor => Assert.Equal(900, descriptor.Order));
        }
    }

    // MVC 的 JSON 输出格式化器：用 AddJsonOptions 配置的选项，没有显式指定编码器时改用不转义非 ASCII 字符的编码器。
    private byte[] MvcJson(object? value)
    {
        var configured = host.Services.GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;
        return JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(configured)
        {
            Encoder = configured.Encoder ?? System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }
}
