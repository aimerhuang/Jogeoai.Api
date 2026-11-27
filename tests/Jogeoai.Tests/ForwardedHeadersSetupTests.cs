using System.Net;
using System.Net.Http.Json;
using System.Threading.RateLimiting;
using Jogeoai.Api.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jogeoai.Tests;

/// <summary>
/// 受信转发头（<see cref="ForwardedHeadersSetup"/>）的宿主级测试：进程内 Kestrel 监听 127.0.0.1:0，
/// 调用与 Program.cs 相同的 <c>AddJogeoaiForwardedHeaders</c> / <c>UseJogeoaiForwardedHeaders</c>，
/// 用回显端点读取处理后的 <c>RemoteIpAddress</c>、scheme 与 Host。不连数据库、不发出站请求。
/// 测试客户端从 127.0.0.1 连接，所以“受信代理 = 127.0.0.1”代表紧邻 API 的那一跳。
/// </summary>
public sealed class ForwardedHeadersSetupTests
{
    private const string Client = "203.0.113.5";
    private const string OtherClient = "203.0.113.77";
    private const string FrontendProxy = "198.51.100.7";
    private const string ComposeSubnet = "172.30.250.0/24";
    private const string ComposeGatewayMapped = "::ffff:172.30.250.1";
    private const string ComposeGateway = "172.30.250.1";

    // ---- design §7 第 1 条：六个用例 ----

    [Fact]
    public async Task Unconfigured_ForgedHeadersAreIgnored()
    {
        await using var host = await Host.StartAsync();

        var echo = await host.EchoAsync(forwardedFor: Client, forwardedProto: "https");

        Assert.False(host.Settings.Enabled);
        Assert.Equal("127.0.0.1", echo.Ip);
        Assert.Equal("http", echo.Scheme);
    }

    [Fact]
    public async Task TrustedProxy_SingleHop_YieldsClientIpAndHttps()
    {
        await using var host = await Host.StartAsync(
            ("ForwardedHeaders:KnownProxies:0", "127.0.0.1"),
            ("ForwardedHeaders:ForwardLimit", "1"));

        var echo = await host.EchoAsync(forwardedFor: Client, forwardedProto: "https");

        Assert.Equal(Client, echo.Ip);
        Assert.Equal("https", echo.Scheme);
    }

    [Fact]
    public async Task TwoHops_YieldsBrowserIp()
    {
        await using var host = await Host.StartAsync(
            ("ForwardedHeaders:KnownProxies:0", "127.0.0.1"),
            ("ForwardedHeaders:KnownProxies:1", FrontendProxy),
            ("ForwardedHeaders:ForwardLimit", "2"));

        var echo = await host.EchoAsync(forwardedFor: $"{Client}, {FrontendProxy}", forwardedProto: "https");

        Assert.Equal(Client, echo.Ip);
        Assert.Equal("https", echo.Scheme);
    }

    [Fact]
    public async Task TwoHops_ClientSuppliedEntryBeyondLimitIsIgnored()
    {
        await using var host = await Host.StartAsync(
            ("ForwardedHeaders:KnownProxies:0", "127.0.0.1"),
            ("ForwardedHeaders:KnownProxies:1", FrontendProxy),
            ("ForwardedHeaders:ForwardLimit", "2"));

        // 前端服务器如果把客户端自带的 X-Forwarded-For 追加而不是覆盖，最左边的值来自客户端；跳数上限让它不被采用。
        var echo = await host.EchoAsync(forwardedFor: $"6.6.6.6, {Client}, {FrontendProxy}");

        Assert.Equal(Client, echo.Ip);
    }

    [Fact]
    public async Task UntrustedMiddleHop_StopsAtThatHop()
    {
        // 跳数上限放到 2，证明停下来是因为中间一跳不受信，而不是因为上限。
        await using var host = await Host.StartAsync(
            ("ForwardedHeaders:KnownProxies:0", "127.0.0.1"),
            ("ForwardedHeaders:ForwardLimit", "2"));

        var echo = await host.EchoAsync(forwardedFor: $"{Client}, 192.0.2.9");

        Assert.Equal("192.0.2.9", echo.Ip);
    }

    [Fact]
    public async Task RateLimitPartition_UsesForwardedClientIp()
    {
        await using var host = await Host.StartAsync(
            ("ForwardedHeaders:KnownProxies:0", "127.0.0.1"));

        // 策略与 Program.cs 的注册限流相同：按 Connection.RemoteIpAddress 分区；这里每个分区只放行 1 次。
        Assert.Equal(HttpStatusCode.OK, await host.LimitedAsync(forwardedFor: Client));
        Assert.Equal(HttpStatusCode.OK, await host.LimitedAsync(forwardedFor: OtherClient));
        Assert.Equal(HttpStatusCode.TooManyRequests, await host.LimitedAsync(forwardedFor: Client));
        Assert.Equal(HttpStatusCode.TooManyRequests, await host.LimitedAsync(forwardedFor: OtherClient));
    }

    [Fact]
    public async Task RateLimitPartition_Unconfigured_ForgedHeaderDoesNotChangePartition()
    {
        await using var host = await Host.StartAsync();

        Assert.Equal(HttpStatusCode.OK, await host.LimitedAsync(forwardedFor: Client));
        // 换一个伪造的来源不会得到新的分区：两次请求都按真实对端 127.0.0.1 计数。
        Assert.Equal(HttpStatusCode.TooManyRequests, await host.LimitedAsync(forwardedFor: OtherClient));
    }

    // ---- 空白项与默认可信项 ----

    [Fact]
    public async Task BlankEntries_AreTreatedAsNotConfigured()
    {
        // Compose 变量未设置时，环境变量绑定得到的就是值为空字符串的列表项。
        await using var host = await Host.StartAsync(
            ("ForwardedHeaders:KnownProxies:0", ""),
            ("ForwardedHeaders:KnownNetworks:0", "   "),
            ("ForwardedHeaders:ForwardLimit", "2"));

        var echo = await host.EchoAsync(forwardedFor: Client, forwardedProto: "https");

        Assert.False(host.Settings.Enabled);
        Assert.Empty(host.Settings.KnownProxies);
        Assert.Empty(host.Settings.KnownNetworks);
        Assert.Equal("127.0.0.1", echo.Ip);
        Assert.Equal("http", echo.Scheme);
    }

    [Fact]
    public async Task BlankEntryNextToRealEntry_OnlyTheRealEntryIsTrusted()
    {
        await using var host = await Host.StartAsync(
            ("ForwardedHeaders:KnownProxies:0", ""),
            ("ForwardedHeaders:KnownProxies:1", "127.0.0.1"),
            ("ForwardedHeaders:ForwardLimit", ""));

        var echo = await host.EchoAsync(forwardedFor: $"{Client}, 192.0.2.9");

        Assert.Single(host.Settings.KnownProxies);
        Assert.Equal(ForwardedHeadersSetup.DefaultForwardLimit, host.Settings.ForwardLimit);
        Assert.Equal("192.0.2.9", echo.Ip);
    }

    [Fact]
    public async Task LoopbackIsNotTrustedUnlessConfigured()
    {
        // 框架默认信任 127.0.0.0/8 与 ::1；配置了别的代理后，来自 127.0.0.1 的转发头仍然不被采用。
        await using var host = await Host.StartAsync(
            ("ForwardedHeaders:KnownProxies:0", FrontendProxy),
            ("ForwardedHeaders:ForwardLimit", "2"));

        var echo = await host.EchoAsync(forwardedFor: Client, forwardedProto: "https");

        Assert.True(host.Settings.Enabled);
        Assert.Equal("127.0.0.1", echo.Ip);
        Assert.Equal("http", echo.Scheme);
    }

    [Fact]
    public async Task ForwardedHostIsNeverApplied()
    {
        await using var host = await Host.StartAsync(
            ("ForwardedHeaders:KnownProxies:0", "127.0.0.1"));

        var echo = await host.EchoAsync(forwardedFor: Client, forwardedHost: "evil.example");

        Assert.Equal(Client, echo.Ip);
        Assert.Equal(host.Authority, echo.Host);
    }

    [Fact]
    public async Task HttpsRedirection_SeesForwardedScheme()
    {
        await using var host = await Host.StartAsync(
            ("ForwardedHeaders:KnownProxies:0", "127.0.0.1"));

        using var forwardedHttps = await host.SendAsync("/redirect/echo", forwardedFor: Client, forwardedProto: "https");
        using var plainHttp = await host.SendAsync("/redirect/echo", forwardedFor: Client);

        Assert.Equal(HttpStatusCode.OK, forwardedHttps.StatusCode);
        Assert.Equal(HttpStatusCode.TemporaryRedirect, plainHttp.StatusCode);
    }

    // ---- 只配置网段、不模拟对端：受信判断用的是真实 TCP 对端 127.0.0.1 ----

    [Fact]
    public async Task NetworkOnly_RealPeerInsideNetwork_IsTrusted()
    {
        await using var host = await Host.StartAsync(
            ("ForwardedHeaders:KnownNetworks:0", "127.0.0.1/32"));

        var echo = await host.EchoAsync(forwardedFor: Client, forwardedProto: "https");

        Assert.Empty(host.Settings.KnownProxies);
        Assert.Equal(Client, echo.Ip);
        Assert.Equal("https", echo.Scheme);
    }

    [Fact]
    public async Task NetworkOnly_RealPeerOutsideNetwork_IsNotTrusted()
    {
        // 代理列表为空、只有网段时，框架中间件不能退化成“信任任意来源”。
        await using var host = await Host.StartAsync(
            ("ForwardedHeaders:KnownNetworks:0", ComposeSubnet),
            ("ForwardedHeaders:ForwardLimit", "2"));

        var echo = await host.EchoAsync(forwardedFor: Client, forwardedProto: "https");

        Assert.True(host.Settings.Enabled);
        Assert.Empty(host.Settings.KnownProxies);
        Assert.Equal("127.0.0.1", echo.Ip);
        Assert.Equal("http", echo.Scheme);
    }

    // ---- 框架中间件实际拿到的 options ----

    [Fact]
    public async Task FrameworkOptionsRegisteredInDi_DoNotReplaceTheConfiguredTrustList()
    {
        // DI 里另有一份“信任所有来源、不限跳数、处理全部转发头”的 ForwardedHeadersOptions。
        // UseJogeoaiForwardedHeaders 传给框架中间件的是自己构造的对象，这份 DI 配置不能生效。
        await using var host = await Host.StartWithServicesAsync(
            services =>
            {
                services.Configure<ForwardedHeadersOptions>(Loosen);
                services.PostConfigure<ForwardedHeadersOptions>(Loosen);
            },
            ("ForwardedHeaders:KnownProxies:0", "127.0.0.1"));

        var echo = await host.EchoAsync(forwardedFor: $"6.6.6.6, {Client}", forwardedProto: "https", forwardedHost: "evil.example");

        // 跳数仍是配置的 1（缺省值）：只取最右一项；6.6.6.6 与 X-Forwarded-Host 都不被采用。
        Assert.Equal(Client, echo.Ip);
        Assert.Equal("https", echo.Scheme);
        Assert.Equal(host.Authority, echo.Host);

        static void Loosen(ForwardedHeadersOptions options)
        {
            options.ForwardedHeaders = ForwardedHeaders.All;
            options.ForwardLimit = null;
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
        }
    }

    [Fact]
    public async Task PeerWithoutAddress_ForwardedHeadersAreIgnored()
    {
        // 没有对端地址的连接（Unix 套接字、命名管道）不是“已配置的受信代理”。框架中间件对这种连接不做受信检查。
        await using var host = await Host.StartAsync(
            ("ForwardedHeaders:KnownProxies:0", "127.0.0.1"),
            ("ForwardedHeaders:ForwardLimit", "2"));

        var echo = await host.EchoAsync(forwardedFor: Client, forwardedProto: "https", peer: Host.NoPeer);
        var control = await host.EchoAsync(forwardedFor: Client, forwardedProto: "https");

        Assert.Null(echo.Ip);
        Assert.Equal("http", echo.Scheme);
        // 对照：同一个宿主、同样的请求头，对端是受信的 127.0.0.1 时会被采用。
        Assert.Equal(Client, control.Ip);
        Assert.Equal("https", control.Scheme);
    }

    // ---- 部署拓扑（design §11）：对端是 compose 网络的网关地址，容器内可能以 IPv4 映射的 IPv6 形式出现 ----
    // 对端地址由测试专用中间件模拟（见 Host.TestPeerHeader），采用哪一项仍由框架中间件决定。
    // 判断“对端是否受信”的用例都带一个只差对端地址的对照请求，证明结果来自中间件的受信判断，而不是模拟本身。

    [Theory]
    [InlineData(ComposeGatewayMapped)]
    [InlineData(ComposeGateway)]
    public async Task ComposeTopology_BrowserPathYieldsBrowserIp(string gatewayPeer)
    {
        await using var host = await Host.StartAsync(
            ("ForwardedHeaders:KnownNetworks:0", ComposeSubnet),
            ("ForwardedHeaders:KnownProxies:0", FrontendProxy),
            ("ForwardedHeaders:ForwardLimit", "2"));

        var echo = await host.EchoAsync(forwardedFor: $"{Client}, {FrontendProxy}", forwardedProto: "https", peer: gatewayPeer);
        var control = await host.EchoAsync(forwardedFor: $"{Client}, {FrontendProxy}", forwardedProto: "https");

        Assert.Equal(Client, echo.Ip);
        Assert.Equal("https", echo.Scheme);
        // 对照：真实对端 127.0.0.1 不在受信网段内，同样的请求头一项也不采用。
        Assert.Equal("127.0.0.1", control.Ip);
        Assert.Equal("http", control.Scheme);
    }

    [Theory]
    [InlineData(ComposeGatewayMapped)]
    [InlineData(ComposeGateway)]
    public async Task ComposeTopology_SingleHopClientCannotSupplyItsOwnAddress(string gatewayPeer)
    {
        await using var host = await Host.StartAsync(
            ("ForwardedHeaders:KnownNetworks:0", ComposeSubnet),
            ("ForwardedHeaders:KnownProxies:0", FrontendProxy),
            ("ForwardedHeaders:ForwardLimit", "2"));

        // /internal/collector、/mcp：网关把对端 192.0.2.44 写在最右边；它不是受信代理，所以它带来的 6.6.6.6 不被采用。
        var echo = await host.EchoAsync(forwardedFor: "6.6.6.6, 192.0.2.44", peer: gatewayPeer);
        // 对照：最右一项换成受信的前端服务器，才会再往左取一项。
        var control = await host.EchoAsync(forwardedFor: $"6.6.6.6, {FrontendProxy}", peer: gatewayPeer);

        Assert.Equal("192.0.2.44", echo.Ip);
        Assert.Equal("6.6.6.6", control.Ip);
    }

    [Fact]
    public async Task ComposeTopology_ClientNamingTheFrontendProxyStillCannotGoPastTheLimit()
    {
        await using var host = await Host.StartAsync(
            ("ForwardedHeaders:KnownNetworks:0", ComposeSubnet),
            ("ForwardedHeaders:KnownProxies:0", FrontendProxy),
            ("ForwardedHeaders:ForwardLimit", "2"));

        // 前端服务器追加而不是覆盖时，浏览器自带的两项（其中一项冒充前端服务器）排在左边；取满 2 项即停。
        var echo = await host.EchoAsync(
            forwardedFor: $"6.6.6.6, {FrontendProxy}, {Client}, {FrontendProxy}", peer: ComposeGatewayMapped);

        Assert.Equal(Client, echo.Ip);
    }

    [Fact]
    public async Task ComposeTopology_BlankFrontendProxy_TrustsOnlyTheGatewayHop()
    {
        await using var host = await Host.StartAsync(
            ("ForwardedHeaders:KnownNetworks:0", ComposeSubnet),
            ("ForwardedHeaders:KnownProxies:0", ""),
            ("ForwardedHeaders:ForwardLimit", "2"));

        var echo = await host.EchoAsync(forwardedFor: $"{Client}, {FrontendProxy}", peer: ComposeGatewayMapped);
        var control = await host.EchoAsync(forwardedFor: $"{Client}, {FrontendProxy}");

        Assert.Equal(FrontendProxy, echo.Ip);
        Assert.Equal("127.0.0.1", control.Ip);
    }

    [Theory]
    [InlineData("172.30.251.1")]
    [InlineData("::ffff:172.30.251.1")]
    [InlineData("172.30.249.255")]
    [InlineData("10.30.250.1")]
    public async Task ComposeTopology_PeerOutsideTheSubnetIsNotTrusted(string outsidePeer)
    {
        await using var host = await Host.StartAsync(
            ("ForwardedHeaders:KnownNetworks:0", ComposeSubnet),
            ("ForwardedHeaders:ForwardLimit", "2"));

        var echo = await host.EchoAsync(forwardedFor: Client, forwardedProto: "https", peer: outsidePeer);
        var control = await host.EchoAsync(forwardedFor: Client, forwardedProto: "https", peer: ComposeGatewayMapped);

        Assert.Equal(IPAddress.Parse(outsidePeer).ToString(), echo.Ip);
        Assert.Equal("http", echo.Scheme);
        // 对照：同一个宿主，对端换成网段内的地址，同样的请求头会被采用——上面的“不采用”不是因为中间件没有注册。
        Assert.Equal(Client, control.Ip);
        Assert.Equal("https", control.Scheme);
    }

    // ---- 启动校验：AddJogeoaiForwardedHeaders 在 builder.Build() 之前抛出，应用不启动 ----

    [Theory]
    [InlineData("0")]
    [InlineData("6")]
    [InlineData("-1")]
    [InlineData("2.5")]
    [InlineData("sentinel-limit-9731")]
    public void ForwardLimitOutsideOneToFive_FailsStartup(string limit)
    {
        foreach (var withProxy in new[] { false, true })
        {
            var settings = new List<(string, string?)> { ("ForwardedHeaders:ForwardLimit", limit) };
            if (withProxy) settings.Add(("ForwardedHeaders:KnownProxies:0", "127.0.0.1"));

            var exception = Assert.Throws<InvalidOperationException>(() =>
                new ServiceCollection().AddJogeoaiForwardedHeaders(Configuration(settings.ToArray())));

            Assert.Contains(ForwardedHeadersSetup.ForwardLimitKey, exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("sentinel", exception.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData("1", 1)]
    [InlineData("2", 2)]
    [InlineData("5", 5)]
    public void ForwardLimitInRange_IsAccepted(string? limit, int expected)
    {
        var settings = ForwardedHeadersSetup.Parse(Configuration(
            ("ForwardedHeaders:KnownProxies:0", "127.0.0.1"),
            ("ForwardedHeaders:ForwardLimit", limit)));

        Assert.Equal(expected, settings.ForwardLimit);
    }

    [Theory]
    [InlineData("ForwardedHeaders:KnownProxies:0", "sentinel-host-9731")]
    [InlineData("ForwardedHeaders:KnownProxies:1", "172.30.250.0/24")]
    [InlineData("ForwardedHeaders:KnownNetworks:0", "sentinel-net-9731")]
    [InlineData("ForwardedHeaders:KnownNetworks:0", "10.20.30.40")]
    [InlineData("ForwardedHeaders:KnownNetworks:1", "10.20.30.0/99")]
    // 前缀长度为 0 的网段等于信任所有来源，必须在启动时拒绝。
    [InlineData("ForwardedHeaders:KnownNetworks:0", "0.0.0.0/0")]
    [InlineData("ForwardedHeaders:KnownNetworks:0", "::/0")]
    [InlineData("ForwardedHeaders:KnownNetworks:1", "203.0.113.9/0")]
    public void UnparseableEntry_FailsStartupWithoutEchoingTheValue(string key, string value)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddJogeoaiForwardedHeaders(Configuration((key, value))));

        Assert.Contains(key, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(value, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NetworkWithHostBits_IsReadAsTheEnclosingNetwork()
    {
        // .NET 10 的 IPNetwork 解析会把主机位清零：172.30.250.1/24 等同于 172.30.250.0/24，范围不会变大。
        var settings = ForwardedHeadersSetup.Parse(Configuration(("ForwardedHeaders:KnownNetworks:0", "172.30.250.1/24")));

        var network = Assert.Single(settings.KnownNetworks);
        Assert.Equal(24, network.PrefixLength);
        Assert.Equal(IPAddress.Parse("172.30.250.0"), network.BaseAddress);
        Assert.False(network.Contains(IPAddress.Parse("172.30.251.1")));
    }

    [Theory]
    [InlineData(ForwardedHeadersSetup.KnownProxiesKey, "198.51.100.7")]
    [InlineData(ForwardedHeadersSetup.KnownNetworksKey, "172.30.250.0/24")]
    public void ScalarInsteadOfList_FailsStartupInsteadOfBeingSilentlyIgnored(string key, string value)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddJogeoaiForwardedHeaders(Configuration((key, value))));

        Assert.Contains(key, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(value, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UseWithoutAdd_FailsStartup()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var app = builder.Build();

        Assert.Throws<InvalidOperationException>(() => app.UseJogeoaiForwardedHeaders());
    }

    private static IConfiguration Configuration(params (string Key, string? Value)[] settings)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(item => new KeyValuePair<string, string?>(item.Key, item.Value)))
            .Build();

    private sealed record Echo(string? Ip, string Scheme, string Host);

    private sealed class Host : IAsyncDisposable
    {
        // 测试专用：模拟 TCP 对端地址（例如 compose 网络的网关）。真实部署中对端地址来自连接本身，不来自请求头。
        // 它只改写进入转发头中间件之前的对端地址；是否采用 X-Forwarded-*、采用哪一项，仍由被测的中间件决定。
        private const string TestPeerHeader = "X-Test-Peer";

        /// <summary>模拟没有对端地址的连接（Unix 套接字、命名管道）。</summary>
        internal const string NoPeer = "none";

        private readonly WebApplication app;
        private readonly HttpClient client;

        private Host(WebApplication app)
        {
            this.app = app;
            var address = new Uri(app.Urls.Single());
            Authority = address.Authority;
            client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = address };
            Settings = app.Services.GetRequiredService<ForwardedHeadersSettings>();
        }

        internal string Authority { get; }
        internal ForwardedHeadersSettings Settings { get; }

        internal static Task<Host> StartAsync(params (string Key, string? Value)[] settings)
            => StartWithServicesAsync(null, settings);

        internal static async Task<Host> StartWithServicesAsync(
            Action<IServiceCollection>? configureServices, params (string Key, string? Value)[] settings)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = AppContext.BaseDirectory,
                EnvironmentName = Environments.Production
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();

            // 只读本测试给出的配置，不受本机环境变量或 appsettings 影响。
            builder.Services.AddJogeoaiForwardedHeaders(Configuration(settings));
            builder.Services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                options.AddPolicy("per-ip", context => RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
                    { PermitLimit = 1, Window = TimeSpan.FromHours(1), QueueLimit = 0 }));
            });
            builder.Services.Configure<HttpsRedirectionOptions>(options => options.HttpsPort = 443);
            configureServices?.Invoke(builder.Services);

            var app = builder.Build();
            app.Use((context, next) =>
            {
                // 写错的地址直接抛出（请求 500、用例失败），不会悄悄退回真实对端。
                var simulated = context.Request.Headers[TestPeerHeader].FirstOrDefault();
                if (simulated == NoPeer) context.Connection.RemoteIpAddress = null;
                else if (simulated is not null) context.Connection.RemoteIpAddress = IPAddress.Parse(simulated);
                return next(context);
            });
            // 与 Program.cs 相同：转发头中间件排在限流和 HTTPS 重定向之前。
            app.UseJogeoaiForwardedHeaders();
            app.UseRateLimiter();
            app.UseWhen(context => context.Request.Path.StartsWithSegments("/redirect"), branch => branch.UseHttpsRedirection());

            IResult EchoResult(HttpContext context) => Results.Json(new Echo(
                context.Connection.RemoteIpAddress?.ToString(), context.Request.Scheme, context.Request.Host.Value ?? string.Empty));
            app.MapGet("/echo", EchoResult);
            app.MapGet("/redirect/echo", EchoResult);
            app.MapGet("/limited", EchoResult).RequireRateLimiting("per-ip");

            await app.StartAsync();
            return new Host(app);
        }

        internal async Task<Echo> EchoAsync(string? forwardedFor = null, string? forwardedProto = null,
            string? forwardedHost = null, string? peer = null)
        {
            using var response = await SendAsync("/echo", forwardedFor, forwardedProto, forwardedHost, peer);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<Echo>())!;
        }

        internal async Task<HttpStatusCode> LimitedAsync(string forwardedFor)
        {
            using var response = await SendAsync("/limited", forwardedFor);
            return response.StatusCode;
        }

        internal async Task<HttpResponseMessage> SendAsync(string path, string? forwardedFor = null, string? forwardedProto = null,
            string? forwardedHost = null, string? peer = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (forwardedFor is not null) request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
            if (forwardedProto is not null) request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", forwardedProto);
            if (forwardedHost is not null) request.Headers.TryAddWithoutValidation("X-Forwarded-Host", forwardedHost);
            if (peer is not null) request.Headers.TryAddWithoutValidation(TestPeerHeader, peer);
            return await client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
