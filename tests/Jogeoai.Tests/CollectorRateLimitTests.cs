using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using Jogeoai.Api.Security;
using Jogeoai.Application;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Jogeoai.Tests;

/// <summary>
/// 采集接口按客户端 IP 限流（RB4）。进程内 Kestrel，阈值配成 2；客户端 IP 通过受信转发头（受信代理 = 127.0.0.1）给出，
/// 与 Program.cs 相同的 <c>ParsePermitPerMinute</c> / <c>AddCollectorInternalPolicy</c> / <c>RateLimitRejection.WriteAsync</c>。
/// 同一 IP 的第三个请求 429 且带 Retry-After，另一 IP 不受影响；被拒请求不触发任何 Store 调用；
/// 阈值非法时启动失败；其他限流策略共用的拒绝响应保持原有响应体。不连数据库。
/// 2026-11-27 评审后修订：只有客户端地址已确定且不是受信代理时才限流。没有启用受信转发头、没有对端地址、
/// 或处理后的地址仍是受信代理（网关没有给出客户端地址）时不限流，避免所有采集电脑共用一个桶后被匿名请求一起锁住。
/// 对端地址（compose 网关、IPv4 映射写法）由测试宿主的专用中间件模拟，是否采用转发头仍由被测的中间件决定。
/// </summary>
public sealed class CollectorRateLimitTests
{
    private const string AppLeases = "/internal/collector/app/leases";
    private const string WebLeases = "/internal/collector/leases";
    private const string Health = "/internal/collector/health";
    private const string ClientA = "203.0.113.5";
    private const string ClientB = "203.0.113.77";
    private const string ClientC = "203.0.113.200";
    private const string FrontendProxy = "198.51.100.7";
    private const string ComposeSubnet = "172.30.250.0/24";
    private const string ComposeGateway = "172.30.250.1";
    private const string ComposeGatewayMapped = "::ffff:172.30.250.1";
    private const HttpStatusCode Allowed = HttpStatusCode.Unauthorized; // 没带令牌：放行的请求得到节点鉴权的 401。
    private const HttpStatusCode Limited = HttpStatusCode.TooManyRequests;
    private const string RateLimitedBody = """{"error":{"code":"RATE_LIMITED","message":"请求过于频繁，请稍后重试。"}}""";

    private static readonly (string Key, string? Value)[] TwoPerMinuteBehindProxy =
    [
        (CollectorRateLimit.PermitPerMinuteKey, "2"),
        ("ForwardedHeaders:KnownProxies:0", "127.0.0.1"),
    ];

    [Fact]
    public async Task TheThirdRequestFromOneIpIs429WithRetryAfterAndAnotherIpIsUnaffected()
    {
        await using var host = await CollectorPreauthTestHost.StartAsync(TwoPerMinuteBehindProxy);

        using var first = await host.PostAsync(AppLeases, "{}", forwardedFor: ClientA);
        using var second = await host.PostAsync(AppLeases, "{}", forwardedFor: ClientA);
        using var third = await host.PostAsync(AppLeases, "{}", forwardedFor: ClientA);
        using var other = await host.PostAsync(AppLeases, "{}", forwardedFor: ClientB);

        // 没带令牌：放行的请求得到节点鉴权的 401，被限流的得到 429。
        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, other.StatusCode);

        var retryAfter = int.Parse(Assert.Single(third.Headers.GetValues("Retry-After")), NumberStyles.None, CultureInfo.InvariantCulture);
        Assert.InRange(retryAfter, 1, 60);
        // 响应体与其他限流策略一直使用的相同。
        Assert.Equal("application/json; charset=utf-8", third.Content.Headers.ContentType?.ToString());
        Assert.Equal(RateLimitedBody, await third.Content.ReadAsStringAsync());
        Assert.False(first.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task BothControllersAndHealthShareOneBucketPerIp()
    {
        await using var host = await CollectorPreauthTestHost.StartAsync(TwoPerMinuteBehindProxy);

        using var app = await host.PostAsync(AppLeases, "{}", forwardedFor: ClientC);
        using var web = await host.PostAsync(WebLeases, "{}", forwardedFor: ClientC);
        using var health = await host.GetAsync(Health, forwardedFor: ClientC);
        using var healthFromAnotherIp = await host.GetAsync(Health, forwardedFor: ClientB);

        Assert.Equal(HttpStatusCode.Unauthorized, app.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, web.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, health.StatusCode);
        Assert.True(health.Headers.Contains("Retry-After"));
        // 另一 IP 的 health 进入动作（测试宿主的数据库不可达，所以是 degraded）。
        Assert.Equal(HttpStatusCode.ServiceUnavailable, healthFromAnotherIp.StatusCode);
    }

    [Fact]
    public async Task ARejectedRequestDoesNoStoreWorkEvenWithAValidToken()
    {
        await using var host = await CollectorPreauthTestHost.StartAsync(TwoPerMinuteBehindProxy);
        var route = CollectorPreauthTestHost.Route(AppLeases);

        using var first = await host.PostAsync(AppLeases, route.ValidBody, CollectorPreauthTestHost.NodeToken, ClientA);
        using var second = await host.PostAsync(AppLeases, route.ValidBody, CollectorPreauthTestHost.NodeToken, ClientA);
        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (first.StatusCode, second.StatusCode));
        var (lookups, work) = (host.NodeLookups, host.AppStoreWork);
        Assert.Equal((2, 2), (lookups, work));

        using var rejected = await host.PostAsync(AppLeases, route.ValidBody, CollectorPreauthTestHost.NodeToken, ClientA);
        using var rejectedMalformed = await host.PostAsync(AppLeases, "{\"nodeId\":", CollectorPreauthTestHost.NodeToken, ClientA);

        Assert.Equal((HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests), (rejected.StatusCode, rejectedMalformed.StatusCode));
        // 限流先于鉴权和模型绑定：节点表没有被再查，也没有别的 Store 调用，会话更没有查。
        Assert.Equal((lookups, work), (host.NodeLookups, host.AppStoreWork));
        Assert.Equal(0, host.Identity.Count(nameof(IIdentityStore.ValidateSessionAsync)));
    }

    // ---- 2026-11-27 评审后修订：客户端地址未知时不限流 ----

    [Fact]
    public async Task WithForwardedHeadersDisabledNothingIsLimited()
    {
        // 没有受信代理：所有请求都来自同一个对端（这里是 127.0.0.1），按它限流等于所有采集电脑共用一个桶。
        await using var host = await CollectorPreauthTestHost.StartAsync((CollectorRateLimit.PermitPerMinuteKey, "2"));

        for (var index = 0; index < 6; index++) Assert.Equal(Allowed, await SendAsync(host));
        // 带上转发头也一样：它不被采用，既不会开出新的桶，也不会让请求被限流。
        foreach (var forged in new[] { ClientA, ClientA, ClientA, ClientB, "127.0.0.1" })
            Assert.Equal(Allowed, await SendAsync(host, forwardedFor: forged));
        using var health = await host.GetAsync(Health);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, health.StatusCode);
        using var web = await host.PostAsync(WebLeases, "{}");
        Assert.Equal(HttpStatusCode.Unauthorized, web.StatusCode);
    }

    [Fact]
    public async Task ARequestWithoutForwardedForResolvesToTheTrustedProxyItselfAndIsNotLimited()
    {
        await using var host = await CollectorPreauthTestHost.StartAsync(TwoPerMinuteBehindProxy);

        // 网关没有写 X-Forwarded-For：处理后的地址就是受信代理 127.0.0.1，不知道客户端是谁，不限流。
        for (var index = 0; index < 6; index++) Assert.Equal(Allowed, await SendAsync(host));

        // 同一个宿主上，给出了客户端地址的请求照常按客户端限流，两个客户端互不影响。
        Assert.Equal([Allowed, Allowed, Limited], [await SendAsync(host, ClientA), await SendAsync(host, ClientA), await SendAsync(host, ClientA)]);
        Assert.Equal([Allowed, Allowed, Limited], [await SendAsync(host, ClientB), await SendAsync(host, ClientB), await SendAsync(host, ClientB)]);
        // 没有转发头的请求仍然不受影响。
        Assert.Equal(Allowed, await SendAsync(host));
    }

    [Theory]
    [InlineData(ComposeGateway)]
    [InlineData(ComposeGatewayMapped)]
    public async Task APeerInsideATrustedNetworkIsRecognisedInBothAddressForms(string gateway)
    {
        // 部署形态：受信的是 compose 网段；容器看到的网关地址可能是 IPv4，也可能是 IPv4 映射的 IPv6 写法。
        await using var host = await CollectorPreauthTestHost.StartAsync(
            (CollectorRateLimit.PermitPerMinuteKey, "2"), ("ForwardedHeaders:KnownNetworks:0", ComposeSubnet));

        // 网关没有给出客户端地址：处理后的地址是网关自己，不限流。
        for (var index = 0; index < 5; index++) Assert.Equal(Allowed, await SendAsync(host, peer: gateway));
        // 网关给出了客户端地址：按客户端限流。
        Assert.Equal([Allowed, Allowed, Limited],
            [await SendAsync(host, ClientA, gateway), await SendAsync(host, ClientA, gateway), await SendAsync(host, ClientA, gateway)]);
        Assert.Equal(Allowed, await SendAsync(host, ClientB, gateway));
    }

    [Theory]
    [InlineData("203.0.113.9")]
    [InlineData("::ffff:203.0.113.9")]
    [InlineData("2001:db8::9")]
    public async Task ADirectPeerThatIsNotATrustedProxyIsLimitedOnItsOwnAddress(string peer)
    {
        // 对照：转发头已启用，对端不在受信列表里（绕过网关直接连到 API）。它带来的转发头不被采用，按对端地址限流。
        await using var host = await CollectorPreauthTestHost.StartAsync(
            (CollectorRateLimit.PermitPerMinuteKey, "2"), ("ForwardedHeaders:KnownNetworks:0", ComposeSubnet));

        Assert.Equal(Allowed, await SendAsync(host, peer: peer));
        Assert.Equal(Allowed, await SendAsync(host, forwardedFor: ComposeGateway, peer: peer));
        Assert.Equal(Limited, await SendAsync(host, forwardedFor: ClientB, peer: peer));
        Assert.Equal(Limited, await SendAsync(host, peer: peer));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("5")]
    public async Task AForgedEntryLeftOfTheAddressTheGatewayWroteNeverOptsTheClientOut(string forwardLimit)
    {
        // 部署形态的受信列表：compose 网段 + 前端服务器。测试客户端扮演宿主机网关（对端 = compose 网关地址）。
        await using var host = await CollectorPreauthTestHost.StartAsync(
            (CollectorRateLimit.PermitPerMinuteKey, "2"),
            ("ForwardedHeaders:KnownNetworks:0", ComposeSubnet),
            ("ForwardedHeaders:KnownProxies:0", FrontendProxy),
            ("ForwardedHeaders:ForwardLimit", forwardLimit));

        // 1) 网关覆盖：API 收到的只有网关看到的对端地址。客户端自带的值（哪怕写的是受信地址）根本到不了 API。
        Assert.Equal(Allowed, await SendAsync(host, ClientA, ComposeGateway));
        // 2) 网关追加：客户端自带的值留在左边，网关把真实对端写在最右。框架从右往左取，取到真实对端后发现它不受信就停，
        //    左边写的无论是受信网段地址、前端服务器、回环地址还是它们的 IPv4 映射写法，都不会被采用。
        Assert.Equal(Allowed, await SendAsync(host, $"{ComposeGateway}, {ClientA}", ComposeGateway));
        foreach (var forged in new[] { ComposeGateway, ComposeGatewayMapped, FrontendProxy, "::ffff:" + FrontendProxy, "127.0.0.1", $"{FrontendProxy}, {ComposeGateway}" })
            Assert.Equal(Limited, await SendAsync(host, $"{forged}, {ClientA}", ComposeGateway));
        // 另一个客户端不受影响，同样不能靠伪造的左侧条目换桶。
        Assert.Equal([Allowed, Allowed, Limited], [
            await SendAsync(host, $"{ComposeGateway}, {ClientB}", ComposeGateway),
            await SendAsync(host, $"{ClientA}, {ClientB}", ComposeGateway),
            await SendAsync(host, $"{FrontendProxy}, {ClientB}", ComposeGateway)]);
    }

    [Fact]
    public async Task BrowserTrafficThroughTheFrontendProxyIsLimitedPerBrowser()
    {
        // 两跳：浏览器 → 前端服务器 → 宿主机网关 → API。前端服务器覆盖成浏览器地址，网关在后面追加前端服务器地址。
        await using var host = await CollectorPreauthTestHost.StartAsync(
            (CollectorRateLimit.PermitPerMinuteKey, "2"),
            ("ForwardedHeaders:KnownNetworks:0", ComposeSubnet),
            ("ForwardedHeaders:KnownProxies:0", FrontendProxy),
            ("ForwardedHeaders:ForwardLimit", "2"));

        Assert.Equal(Allowed, await SendAsync(host, $"{ClientA}, {FrontendProxy}", ComposeGateway));
        Assert.Equal(Allowed, await SendAsync(host, $"{ClientA}, {FrontendProxy}", ComposeGateway));
        Assert.Equal(Limited, await SendAsync(host, $"{ClientA}, {FrontendProxy}", ComposeGateway));
        // 前端服务器如果误用了追加，浏览器自带的值排在最左，超出跳数上限，不被采用。
        Assert.Equal(Limited, await SendAsync(host, $"{ComposeGateway}, {ClientA}, {FrontendProxy}", ComposeGateway));
        // 请求由前端服务器自己发出（没有更左边的条目）：处理后的地址是受信代理，不限流。
        for (var index = 0; index < 5; index++) Assert.Equal(Allowed, await SendAsync(host, FrontendProxy, ComposeGateway));
    }

    [Fact]
    public async Task APassThroughGatewayAlreadyLetsTheClientChooseItsBucketWithOrWithoutThisRule()
    {
        // 记录已知的边界，不是本规则新增的口子：网关把客户端自带的 X-Forwarded-For 原样透传（部署说明 2.4 明确禁止）时，
        // 最右一项就是客户端写的。它每次换一个地址就得到一个新桶，本来就限不住；写一个受信地址得到的“不限流”与此等价。
        await using var host = await CollectorPreauthTestHost.StartAsync(
            (CollectorRateLimit.PermitPerMinuteKey, "2"), ("ForwardedHeaders:KnownNetworks:0", ComposeSubnet));

        for (var index = 0; index < 6; index++)
            Assert.Equal(Allowed, await SendAsync(host, $"203.0.113.{100 + index}", ComposeGateway));
        for (var index = 0; index < 6; index++)
            Assert.Equal(Allowed, await SendAsync(host, ComposeGateway, ComposeGateway));
    }

    public static TheoryData<string[], string[], string, bool> TrustedProxyCases() => new()
    {
        // 受信代理、受信网段、待判断的地址、是否受信。
        { ["198.51.100.7"], [], "198.51.100.7", true },
        { ["198.51.100.7"], [], "::ffff:198.51.100.7", true },
        { ["::ffff:198.51.100.7"], [], "198.51.100.7", true },
        { ["::ffff:198.51.100.7"], [], "::ffff:198.51.100.7", true },
        { ["198.51.100.7"], [], "198.51.100.8", false },
        { ["198.51.100.7"], [], "::ffff:198.51.100.8", false },
        { [], ["172.30.250.0/24"], "172.30.250.1", true },
        { [], ["172.30.250.0/24"], "::ffff:172.30.250.1", true },
        { [], ["::ffff:172.30.250.0/120"], "172.30.250.1", true },
        { [], ["::ffff:172.30.250.0/120"], "::ffff:172.30.250.1", true },
        { [], ["172.30.250.0/24"], "172.30.251.1", false },
        { [], ["172.30.250.0/24"], "::ffff:172.30.251.1", false },
        { [], ["::ffff:172.30.250.0/120"], "172.30.251.1", false },
        { ["2001:db8::1"], ["2001:db8:1::/48"], "2001:db8::1", true },
        { ["2001:db8::1"], ["2001:db8:1::/48"], "2001:db8:1:2::3", true },
        { ["2001:db8::1"], ["2001:db8:1::/48"], "2001:db8::2", false },
        // IPv4 兼容写法（::a.b.c.d）不是 IPv4 映射地址，不与 IPv4 等同。
        { ["198.51.100.7"], [], "::198.51.100.7", false },
        { ["127.0.0.1"], [], "::1", false },
    };

    [Theory]
    [MemberData(nameof(TrustedProxyCases))]
    public void TrustedProxyMatchingTreatsAnIpv4AddressAndItsMappedFormAsTheSameAddress(string[] proxies, string[] networks, string address, bool trusted)
    {
        var settings = Settings(proxies, networks);
        var parsed = IPAddress.Parse(address);

        Assert.Equal(trusted, CollectorRateLimit.IsTrustedProxy(settings, parsed));
        // 受信代理不限流；其余地址原样作为分区键。
        Assert.Equal(trusted ? null : parsed, CollectorRateLimit.LimitedClient(settings, parsed));
    }

    [Fact]
    public void LimitedClientIsNullWheneverTheClientAddressIsUnknown()
    {
        var client = IPAddress.Parse(ClientA);
        var enabled = Settings([FrontendProxy], [ComposeSubnet]);

        Assert.Equal(client, CollectorRateLimit.LimitedClient(enabled, client));
        // 没有注册设置、两个列表都为空（未启用）、没有对端地址：都不限流。
        Assert.Null(CollectorRateLimit.LimitedClient(null, client));
        Assert.Null(CollectorRateLimit.LimitedClient(Settings([], []), client));
        Assert.Null(CollectorRateLimit.LimitedClient(Settings([], []), IPAddress.Loopback));
        Assert.Null(CollectorRateLimit.LimitedClient(enabled, null));
        Assert.Null(CollectorRateLimit.LimitedClient(enabled, IPAddress.Parse(FrontendProxy)));
        Assert.Null(CollectorRateLimit.LimitedClient(enabled, IPAddress.Parse(ComposeGatewayMapped)));
        // 不限流的分区键不是 IP 地址的写法，不会与某个客户端的分区重名。
        Assert.False(IPAddress.TryParse(CollectorRateLimit.UnlimitedPartitionKey, out _));
    }

    [Theory]
    [InlineData(true, LogLevel.Information)]
    [InlineData(false, LogLevel.Warning)]
    public void StartupLogsOneLineWhenTheLimiterIsInactiveAndNothingWhenItIsActive(bool isDevelopment, LogLevel expected)
    {
        var disabled = new RecordingLogger();
        CollectorRateLimit.LogStartupState(disabled, Settings([], []), isDevelopment);
        var (level, message) = Assert.Single(disabled.Entries);
        Assert.Equal(expected, level);
        Assert.Contains("rate limit inactive", message, StringComparison.Ordinal);
        Assert.Contains("/internal/collector", message, StringComparison.Ordinal);
        // 日志里没有配置值：没有数字（阈值、地址、网段都带数字）。
        Assert.DoesNotContain(message, char.IsDigit);

        var unregistered = new RecordingLogger();
        CollectorRateLimit.LogStartupState(unregistered, null, isDevelopment);
        Assert.Equal(expected, Assert.Single(unregistered.Entries).Level);

        var enabled = new RecordingLogger();
        CollectorRateLimit.LogStartupState(enabled, Settings([FrontendProxy], [ComposeSubnet]), isDevelopment);
        Assert.Empty(enabled.Entries);
    }

    [Fact]
    public async Task TheDefaultOf600IsNotReachedByABurstFromOneCollector()
    {
        await using var host = await CollectorPreauthTestHost.StartAsync(("ForwardedHeaders:KnownProxies:0", "127.0.0.1"));

        // 一个网页采集进程约每分钟 100 次；这里同一个客户端地址连续发 150 次，没有一次被限流。
        for (var index = 0; index < 150; index++) Assert.Equal(Allowed, await SendAsync(host, ClientA));
    }

    [Fact]
    public void EveryCollectorEndpointIncludingHealthCarriesThePolicy()
    {
        Assert.Equal("collector-internal", CollectorRateLimit.Policy);
        foreach (var controller in new[] { typeof(Jogeoai.Api.Controllers.AppCollectorController), typeof(Jogeoai.Api.Controllers.CollectorController) })
        {
            var attribute = Assert.Single(controller.GetCustomAttributes(typeof(EnableRateLimitingAttribute), false).Cast<EnableRateLimitingAttribute>());
            Assert.Equal(CollectorRateLimit.Policy, attribute.PolicyName);
            // 没有动作用自己的策略或 [DisableRateLimiting] 覆盖控制器上的策略。
            Assert.All(controller.GetMethods(), method =>
            {
                Assert.Empty(method.GetCustomAttributes(typeof(EnableRateLimitingAttribute), false));
                Assert.Empty(method.GetCustomAttributes(typeof(DisableRateLimitingAttribute), false));
            });
        }
    }

    [Fact]
    public async Task TheServedEndpointsCarryThePolicy()
    {
        await using var host = await CollectorPreauthTestHost.StartAsync();

        var endpoints = host.CollectorEndpoints;
        Assert.Equal(13, endpoints.Count);
        Assert.All(endpoints, item =>
        {
            Assert.Equal(CollectorRateLimit.Policy, item.Endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName);
            Assert.Null(item.Endpoint.Metadata.GetMetadata<DisableRateLimitingAttribute>());
        });
    }

    [Theory]
    [InlineData(null, 600)]
    [InlineData("", 600)]
    [InlineData("   ", 600)]
    [InlineData("600", 600)]
    [InlineData(" 900 ", 900)]
    [InlineData("1", 1)]
    public void PermitPerMinuteDefaultsTo600AndAcceptsPositiveIntegers(string? configured, int expected)
    {
        Assert.Equal(600, CollectorRateLimit.DefaultPermitPerMinute);
        Assert.Equal("Collector:RateLimit:PermitPerMinute", CollectorRateLimit.PermitPerMinuteKey);
        Assert.Equal(expected, CollectorRateLimit.ParsePermitPerMinute(Configuration(configured)));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("+5")]
    [InlineData("1.5")]
    [InlineData("many")]
    [InlineData("99999999999")]
    public async Task APermitBelowOneOrNotAnIntegerFailsAtStartup(string configured)
    {
        var parse = Assert.Throws<InvalidOperationException>(() => CollectorRateLimit.ParsePermitPerMinute(Configuration(configured)));
        Assert.Contains(CollectorRateLimit.PermitPerMinuteKey, parse.Message, StringComparison.Ordinal);
        // 错误信息只含配置键名，不回显配置值。
        Assert.DoesNotContain(configured, parse.Message.Replace(CollectorRateLimit.PermitPerMinuteKey, string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);

        // 宿主与 Program.cs 一样在 Build 之前解析：应用起不来。
        await Assert.ThrowsAsync<InvalidOperationException>(() => CollectorPreauthTestHost.StartAsync((CollectorRateLimit.PermitPerMinuteKey, configured)));
    }

    [Fact]
    public void ProgramParsesTheLimitBeforeBuildAndUsesTheSharedPolicyAndRejection()
    {
        // 测试宿主自行调用这三个入口；这里防止 Program.cs 漏接。
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Jogeoai.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var statements = File.ReadAllText(Path.Combine(directory.FullName, "src", "Jogeoai.Api", "Program.cs")).Split('\n')
            .Select(line => line.Trim())
            .Where(line => !line.StartsWith("//", StringComparison.Ordinal))
            .ToList();

        var parse = statements.IndexOf("var collectorPermitPerMinute = CollectorRateLimit.ParsePermitPerMinute(builder.Configuration);");
        var build = statements.IndexOf("var app = builder.Build();");
        Assert.True(parse >= 0 && build > parse, "Collector:RateLimit:PermitPerMinute 必须在 builder.Build() 之前解析，非法值才会让应用无法启动。");
        Assert.Contains("options.OnRejected = RateLimitRejection.WriteAsync;", statements);
        Assert.Contains("options.AddCollectorInternalPolicy(collectorPermitPerMinute);", statements);
        // 限流未生效时的启动日志：在 Build 之后调用一次。
        var log = statements.IndexOf("CollectorRateLimit.LogStartupState(app.Services, app.Environment);");
        Assert.True(log > build, "Program.cs 必须在 builder.Build() 之后调用 CollectorRateLimit.LogStartupState。");
        Assert.Single(statements, line => line.Contains("LogStartupState", StringComparison.Ordinal));
        // 限流中间件在端点执行之前，因此也在模型绑定和鉴权过滤器之前。
        var limiter = statements.FindIndex(line => line.StartsWith("app.UseRateLimiter(", StringComparison.Ordinal));
        var controllers = statements.FindIndex(line => line.StartsWith("app.MapControllers(", StringComparison.Ordinal));
        Assert.True(limiter >= 0 && controllers > limiter);
    }

    [Fact]
    public async Task OtherPoliciesKeepTheirStatusAndBodyAndGainRetryAfter()
    {
        await using var host = await CollectorPreauthTestHost.StartAsync();

        using var allowed = await host.GetAsync("/test/hourly");
        using var rejected = await host.GetAsync("/test/hourly");

        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("application/json; charset=utf-8", rejected.Content.Headers.ContentType?.ToString());
        Assert.Equal(RateLimitedBody, await rejected.Content.ReadAsStringAsync());
        // 一小时窗口：重试时间来自限流器，不是固定的 60。
        Assert.InRange(int.Parse(Assert.Single(rejected.Headers.GetValues("Retry-After")), NumberStyles.None, CultureInfo.InvariantCulture), 61, 3600);
    }

    [Fact]
    public void RetryAfterIsCeiledToAtLeastOneSecondAndFallsBackTo60WithoutMetadata()
    {
        Assert.Equal(60, RateLimitRejection.FallbackRetryAfterSeconds);
        Assert.Equal(60, RateLimitRejection.RetryAfterSeconds(new Lease(null)));
        Assert.Equal(1, RateLimitRejection.RetryAfterSeconds(new Lease(TimeSpan.Zero)));
        Assert.Equal(1, RateLimitRejection.RetryAfterSeconds(new Lease(TimeSpan.FromSeconds(-3))));
        Assert.Equal(1, RateLimitRejection.RetryAfterSeconds(new Lease(TimeSpan.FromMilliseconds(200))));
        Assert.Equal(42, RateLimitRejection.RetryAfterSeconds(new Lease(TimeSpan.FromSeconds(41.01))));
        Assert.Equal(3600, RateLimitRejection.RetryAfterSeconds(new Lease(TimeSpan.FromHours(1))));
    }

    // 不带令牌的 App 领取请求：放行时是节点鉴权的 401，被限流时是 429。forwardedFor 是“网关”写的 X-Forwarded-For，
    // peer 是模拟的 TCP 对端地址（不给时就是真实对端 127.0.0.1）。
    private static async Task<HttpStatusCode> SendAsync(CollectorPreauthTestHost host, string? forwardedFor = null, string? peer = null)
    {
        using var response = await host.PostAsync(AppLeases, "{}"u8.ToArray(), forwardedFor: forwardedFor,
            configure: request => { if (peer is not null) request.Headers.TryAddWithoutValidation(CollectorPreauthTestHost.TestPeerHeader, peer); });
        return response.StatusCode;
    }

    private static ForwardedHeadersSettings Settings(string[] proxies, string[] networks)
        => new(proxies.Select(IPAddress.Parse).ToArray(), networks.Select(value => System.Net.IPNetwork.Parse(value)).ToArray(), 2);

    private static IConfiguration Configuration(string? permit)
        => new ConfigurationBuilder().AddInMemoryCollection(
            permit is null ? [] : new Dictionary<string, string?> { [CollectorRateLimit.PermitPerMinuteKey] = permit }).Build();

    /// <summary>记录日志级别与文本。</summary>
    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    /// <summary>被拒绝的限流租约；<paramref name="retryAfter"/> 为 null 时不带重试时间。</summary>
    private sealed class Lease(TimeSpan? retryAfter) : RateLimitLease
    {
        public override bool IsAcquired => false;

        public override IEnumerable<string> MetadataNames => retryAfter is null ? [] : [MetadataName.RetryAfter.Name];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = retryAfter;
            return retryAfter is not null && metadataName == MetadataName.RetryAfter.Name;
        }
    }
}
