using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Jogeoai.Api;
using Jogeoai.Api.Authorization;
using Jogeoai.Api.Controllers;
using Jogeoai.Api.Mcp;
using Jogeoai.Api.Security;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Jogeoai.Tests.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jogeoai.Tests;

/// <summary>
/// 采集接口鉴权前开销限制（请求体上限、先鉴权后绑定、不查会话、按 IP 限流）的进程内宿主：Kestrel 监听 127.0.0.1:0，
/// 管道顺序与 Program.cs 相同（转发头 → 会话 → MCP 令牌 → 限流 → 授权 → Controller），调用同一组扩展方法；
/// Store 全部是按方法名分派的桩，不连数据库、不发出站请求。配置只来自本宿主给出的内存键值，不读本机环境变量。
/// </summary>
internal sealed class CollectorPreauthTestHost : IAsyncDisposable
{
    internal const string NodeToken = "app-node-preauth-token";
    internal const string NodeId = "mac-1";
    internal const string WebToken = "web-collector-preauth-token";
    internal const string SessionToken = "session-token-for-collector-preauth-tests";
    internal const string HourlyPolicy = "test-hourly";

    /// <summary>测试专用请求头：模拟 TCP 对端地址。真实部署中对端地址来自连接本身，不来自请求头。</summary>
    internal const string TestPeerHeader = "X-Test-Peer";

    internal static readonly Guid Id = Guid.Parse("11111111-2222-3333-4444-555555555555");
    internal static readonly Guid Tenant = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    internal static readonly DateTimeOffset At = new(2026, 11, 17, 1, 11, 0, 123, TimeSpan.Zero);

    /// <summary>与 Program.cs 的 AddJsonOptions 相同：Web 默认值 + 字符串枚举。</summary>
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly WebApplication app;

    private CollectorPreauthTestHost(WebApplication app, McpStoreProxy appPool, McpStoreProxy web, McpStoreProxy pool,
        McpStoreProxy identity, FakeMcpTokenStore mcpTokens)
    {
        this.app = app;
        AppPool = appPool;
        Web = web;
        Pool = pool;
        Identity = identity;
        McpTokens = mcpTokens;
        Address = new Uri(app.Urls.Single());
        Client = new HttpClient(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false }) { BaseAddress = Address };
    }

    internal Uri Address { get; }
    internal HttpClient Client { get; }
    internal McpStoreProxy AppPool { get; }
    internal McpStoreProxy Web { get; }
    internal McpStoreProxy Pool { get; }
    internal McpStoreProxy Identity { get; }
    internal FakeMcpTokenStore McpTokens { get; }

    /// <summary>节点令牌对应节点的状态。</summary>
    internal AppNodeStatus NodeStatus { get; set; } = AppNodeStatus.Active;

    /// <summary>
    /// 不为 null 时节点令牌查询抛出它：<see cref="CollectorStoreException"/> 模拟未执行迁移 049 的 503，
    /// 其他类型模拟 Store 的意外故障（例如数据库连接失败）。
    /// </summary>
    internal Exception? NodeLookupError { get; set; }

    internal IServiceProvider Services => app.Services;

    internal IReadOnlyList<Endpoint> Endpoints
        => ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).ToArray();

    /// <summary>两个采集控制器的全部 endpoint（12 个 POST 加 health）。</summary>
    internal IReadOnlyList<(Endpoint Endpoint, ControllerActionDescriptor Action)> CollectorEndpoints
        => Endpoints.Select(endpoint => (Endpoint: endpoint, Action: endpoint.Metadata.GetMetadata<ControllerActionDescriptor>()))
            .Where(item => item.Action is not null
                && (item.Action.ControllerTypeInfo.AsType() == typeof(AppCollectorController)
                    || item.Action.ControllerTypeInfo.AsType() == typeof(CollectorController)))
            .Select(item => (item.Endpoint, item.Action!))
            .ToArray();

    /// <summary>App 节点 Store 桩上除令牌查询以外的调用数。</summary>
    internal int AppStoreWork => AppPool.Calls.Count(call => call.Method != nameof(IAppCollectorPoolStore.AuthenticateNodeAsync));

    /// <summary>节点令牌查询次数。</summary>
    internal int NodeLookups => AppPool.Count(nameof(IAppCollectorPoolStore.AuthenticateNodeAsync));

    /// <summary>网页采集两个 Store 桩上的调用数。</summary>
    internal int WebStoreWork => Web.Calls.Count + Pool.Calls.Count;

    internal static async Task<CollectorPreauthTestHost> StartAsync(params (string Key, string? Value)[] settings)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(HealthController).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production
        });
        // 只读本测试给出的配置：本机环境变量或 appsettings 里的采集令牌、租户名单、限流阈值都不参与。
        builder.Configuration.Sources.Clear();
        var values = new Dictionary<string, string?> { ["Collector:ChinaServiceToken"] = WebToken };
        foreach (var (key, value) in settings) values[key] = value;
        builder.Configuration.AddInMemoryCollection(values);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        CollectorPreauthTestHost? host = null;
        var (appPoolStore, appPool) = McpStoreProxy.Create<IAppCollectorPoolStore>();
        appPool.Handlers[nameof(IAppCollectorPoolStore.AuthenticateNodeAsync)] = args =>
        {
            if (host!.NodeLookupError is { } error) return Task.FromException<AppNodeIdentity?>(error);
            return Task.FromResult((string?)args[0] == NodeToken ? new AppNodeIdentity(NodeId, host.NodeStatus) : null);
        };
        appPool.Handlers[nameof(IAppCollectorPoolStore.ReportBindingsAsync)] = _ => Task.FromResult(new AppBindingReportResult([], []));
        appPool.Handlers[nameof(IAppCollectorPoolStore.LeaseAsync)] = _ => Task.FromResult(new AppLeaseResult(null, AppLeaseEmptyReason.NoTask, 60));
        appPool.Handlers[nameof(IAppCollectorPoolStore.ReportSentAsync)] = _ => Task.FromResult("accepted");
        appPool.Handlers[nameof(IAppCollectorPoolStore.HeartbeatAsync)] = _ => Task.FromResult(At);
        appPool.Handlers[nameof(IAppCollectorPoolStore.ReportFailureAsync)] = _ =>
            Task.FromResult(new AppFailureResult(AppBindingStatus.Active, null, null, false, false));
        appPool.Handlers[nameof(IAppCollectorPoolStore.SubmitResultAsync)] = _ =>
            Task.FromResult(new CollectorResultResponse(CollectorResultDisposition.Accepted, Id));

        var (webStore, web) = McpStoreProxy.Create<IVisibilityCollectorStore>();
        web.Handlers[nameof(IVisibilityCollectorStore.LeaseAsync)] = _ => Task.FromResult<IReadOnlyList<LeasedCollectorTask>>([]);
        web.Handlers[nameof(IVisibilityCollectorStore.HeartbeatAsync)] = _ => Task.FromResult(At);
        web.Handlers[nameof(IVisibilityCollectorStore.SubmitResultAsync)] = _ =>
            Task.FromResult(new CollectorResultResponse(CollectorResultDisposition.Accepted, Id));

        var (poolStore, pool) = McpStoreProxy.Create<ICollectorSessionPoolStore>();
        pool.Handlers[nameof(ICollectorSessionPoolStore.CheckEligibilityAsync)] = _ => Task.FromResult(new CollectorEligibilityResult(true, 30));
        pool.Handlers[nameof(ICollectorSessionPoolStore.ReportFailureAsync)] = _ =>
            Task.FromResult(new CollectorFailureResult(CollectorSessionProfileStatus.Active, null, false));
        pool.Handlers[nameof(ICollectorSessionPoolStore.RecordSessionEventAsync)] = _ =>
            Task.FromResult(new CollectorSessionEventResult(CollectorSessionProfileStatus.Active));

        var (identityStore, identity) = McpStoreProxy.Create<IIdentityStore>();
        identity.Handlers[nameof(IIdentityStore.ValidateSessionAsync)] = args => Task.FromResult(
            (string)args[0]! == SessionToken
                ? new AuthenticatedRequestContext(new(Guid.NewGuid(), "member@example.test", null, "active",
                    [new(Tenant, "Team", "member", "active")]), Guid.NewGuid(), SessionToken)
                : null);
        // 只为激活 AuthController 构造；本宿主不走登录路径。
        var (throttleStore, _) = McpStoreProxy.Create<ILoginThrottleStore>();
        // MCP 工具依赖的 Store；本宿主只发 ping，不调用工具。
        var (managementStore, _) = McpStoreProxy.Create<IManagementStore>();
        var (analyticsStore, _) = McpStoreProxy.Create<IAnalyticsQueryStore>();
        var mcpTokens = new FakeMcpTokenStore();

        builder.Services.AddSingleton(appPoolStore);
        builder.Services.AddSingleton(webStore);
        builder.Services.AddSingleton(poolStore);
        builder.Services.AddSingleton(identityStore);
        builder.Services.AddSingleton(throttleStore);
        builder.Services.AddSingleton(managementStore);
        builder.Services.AddSingleton(analyticsStore);
        builder.Services.AddSingleton<IMcpTokenStore>(mcpTokens);
        builder.Services.AddSingleton(TimeProvider.System);
        // 只为激活 CollectorController 构造；health 会尝试连接 127.0.0.1:1，立即被拒绝并返回 degraded。
        builder.Services.AddSingleton(new MySqlDatabase("Server=127.0.0.1;Port=1;Database=unused;User ID=unused;Password=unused;ConnectionTimeout=1"));
        builder.Services.AddSingleton<TenantContextResolver>();
        builder.Services.AddJogeoaiForwardedHeaders(builder.Configuration);
        builder.Services.AddJogeoaiSessionAuthorization();
        builder.Services.AddJogeoaiMcp();
        builder.Services.AddControllers().AddApplicationPart(typeof(HealthController).Assembly).AddJsonOptions(options =>
            options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        // 与 Program.cs 相同的解析、拒绝响应与策略注册；另加一个 1 小时窗口的测试策略，用来核对其他策略共用同一个拒绝响应。
        var permitPerMinute = CollectorRateLimit.ParsePermitPerMinute(builder.Configuration);
        builder.Services.AddRateLimiter(options =>
        {
            options.OnRejected = RateLimitRejection.WriteAsync;
            options.AddMcpTokenPolicy();
            options.AddCollectorInternalPolicy(permitPerMinute);
            options.AddPolicy(HourlyPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
                { PermitLimit = 1, Window = TimeSpan.FromHours(1), QueueLimit = 0 }));
        });

        var app = builder.Build();
        app.Use((context, next) =>
        {
            // 测试专用：模拟 TCP 对端地址（例如 compose 网络的网关）。只改写进入转发头中间件之前的对端地址；
            // 写错的地址直接抛出（请求 500、用例失败），不会悄悄退回真实对端。
            var simulated = context.Request.Headers[TestPeerHeader].FirstOrDefault();
            if (simulated is not null) context.Connection.RemoteIpAddress = IPAddress.Parse(simulated);
            return next(context);
        });
        app.UseJogeoaiForwardedHeaders();
        CollectorRateLimit.LogStartupState(app.Services, app.Environment);
        app.UseMiddleware<SessionAuthenticationMiddleware>();
        app.UseMcpTokenAuthentication();
        app.UseRateLimiter();
        app.UseAuthorization();
        app.MapControllers().RequireJogeoaiSession();
        app.MapJogeoaiMcp();
        app.MapGet("/test/hourly", () => Results.Text("ok")).RequireRateLimiting(HourlyPolicy);
        await app.StartAsync();
        host = new CollectorPreauthTestHost(app, appPool, web, pool, identity, mcpTokens);
        return host;
    }

    /// <summary>全部 12 个 POST 动作：路径、上限、所用令牌和一个能通过全部校验并到达 Store 的请求体。</summary>
    internal static IReadOnlyList<CollectorRoute> Routes { get; } =
    [
        new("/internal/collector/app/bindings/report", CollectorRequestLimits.AppBindingReport, NodeToken, typeof(AppCollectorController),
            nameof(AppCollectorController.ReportBindings), $$"""{"nodeId":"{{NodeId}}","collectorBuildRevision":"rev-1","bindings":[]}"""),
        new("/internal/collector/app/leases", CollectorRequestLimits.SmallBody, NodeToken, typeof(AppCollectorController),
            nameof(AppCollectorController.Lease),
            $$"""{"nodeId":"{{NodeId}}","bindingId":"{{Id}}","leaseSeconds":60,"dispatchProtocol":"device-v1","deviceOnline":true}"""),
        new("/internal/collector/app/sent", CollectorRequestLimits.SmallBody, NodeToken, typeof(AppCollectorController),
            nameof(AppCollectorController.Sent),
            $$"""{"nodeId":"{{NodeId}}","bindingId":"{{Id}}","taskId":"{{Id}}","leaseVersion":2,"sentAt":"2026-11-17T01:11:00Z"}"""),
        new("/internal/collector/app/heartbeats", CollectorRequestLimits.SmallBody, NodeToken, typeof(AppCollectorController),
            nameof(AppCollectorController.Heartbeat), $$"""{"nodeId":"{{NodeId}}","taskId":"{{Id}}","leaseVersion":1}"""),
        new("/internal/collector/app/failures", CollectorRequestLimits.SmallBody, NodeToken, typeof(AppCollectorController),
            nameof(AppCollectorController.Failure),
            $$"""{"nodeId":"{{NodeId}}","bindingId":"{{Id}}","failureCode":"login_required","phase":"probe"}"""),
        new("/internal/collector/app/results", CollectorRequestLimits.AppResult, NodeToken, typeof(AppCollectorController),
            nameof(AppCollectorController.Result), JsonSerializer.Serialize(AppResultContractTests.Request(NodeId), Json)),
        new("/internal/collector/leases", CollectorRequestLimits.SmallBody, WebToken, typeof(CollectorController),
            nameof(CollectorController.Lease),
            $$"""{"tenantId":"{{Tenant}}","nodeId":"web-1","region":"cn","supportedPlatformCodes":["doubao"]}"""),
        new("/internal/collector/heartbeats", CollectorRequestLimits.SmallBody, WebToken, typeof(CollectorController),
            nameof(CollectorController.Heartbeat), $$"""{"tenantId":"{{Tenant}}","taskId":"{{Id}}","nodeId":"web-1","leaseVersion":1}"""),
        new("/internal/collector/results", CollectorRequestLimits.WebResult, WebToken, typeof(CollectorController),
            nameof(CollectorController.Result),
            $$"""{"tenantId":"{{Tenant}}","taskId":"{{Id}}","nodeId":"web-1","region":"cn","leaseVersion":1,"idempotencyKey":"preauth-result-0001","egressCountry":"CN","collectorBuildRevision":"rev-1","answerText":"回答正文","citations":[],"completedAt":"2026-11-17T01:11:00Z"}"""),
        new("/internal/collector/eligibility", CollectorRequestLimits.SmallBody, WebToken, typeof(CollectorController),
            nameof(CollectorController.Eligibility),
            $$"""{"tenantId":"{{Tenant}}","nodeId":"web-1","region":"cn","sessionProfileId":"{{Id}}","platformCode":"doubao"}"""),
        new("/internal/collector/failures", CollectorRequestLimits.SmallBody, WebToken, typeof(CollectorController),
            nameof(CollectorController.Failure),
            $$"""{"tenantId":"{{Tenant}}","nodeId":"web-1","region":"cn","sessionProfileId":"{{Id}}","failureCode":"login_required","phase":"probe"}"""),
        new("/internal/collector/session-events", CollectorRequestLimits.SmallBody, WebToken, typeof(CollectorController),
            nameof(CollectorController.SessionEvent),
            $$"""{"tenantId":"{{Tenant}}","nodeId":"web-1","region":"cn","sessionProfileId":"{{Id}}","eventKind":"verified","phase":"probe"}"""),
    ];

    internal static CollectorRoute Route(string path) => Routes.Single(route => route.Path == path);

    /// <summary>xUnit 的 MemberData：12 个接口的路径；<paramref name="smallOnly"/> 为真时只含上限为 16 KiB 的 9 个。</summary>
    internal static TheoryData<string> PathData(bool smallOnly = false)
    {
        var data = new TheoryData<string>();
        foreach (var route in Routes)
            if (!smallOnly || route.Limit == CollectorRequestLimits.SmallBody) data.Add(route.Path);
        return data;
    }

    /// <summary>经 HttpClient 发送 JSON 字节；<paramref name="forwardedFor"/> 只在宿主配置了受信代理 127.0.0.1 时生效。</summary>
    internal async Task<HttpResponseMessage> PostAsync(string path, byte[] body, string? bearer = null, string? forwardedFor = null,
        Action<HttpRequestMessage>? configure = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        if (bearer is not null) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
        if (forwardedFor is not null) request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        configure?.Invoke(request);
        return await Client.SendAsync(request);
    }

    internal Task<HttpResponseMessage> PostAsync(string path, string body, string? bearer = null, string? forwardedFor = null)
        => PostAsync(path, Encoding.UTF8.GetBytes(body), bearer, forwardedFor);

    internal async Task<HttpResponseMessage> GetAsync(string path, string? bearer = null, string? forwardedFor = null,
        Action<HttpRequestMessage>? configure = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (bearer is not null) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
        if (forwardedFor is not null) request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        configure?.Invoke(request);
        return await Client.SendAsync(request);
    }

    /// <summary>
    /// 用原始 TCP 连接发送一个 POST：请求头声明 <paramref name="framing"/>（Content-Length 或 chunked），只发送
    /// <paramref name="body"/> 给出的字节（可以少于声明的长度，甚至不发送），然后读取到响应完整为止并关闭连接。
    /// 返回值里的 <see cref="RawResponse.Elapsed"/> 是从发完请求到收全响应的时间：服务器如果先等请求体再答复，
    /// 它会一直等下去（这里 15 秒后超时并让用例失败）。
    /// </summary>
    internal Task<RawResponse> RawPostAsync(string path, string? bearer, string framing, ReadOnlyMemory<byte> body = default)
        => RawAsync("POST", path, bearer, framing, body);

    /// <summary>
    /// 用原始 TCP 连接发送一个请求，请求目标 <paramref name="target"/> 原样写在请求行里：HttpClient 会在发送前规范化路径
    /// （去掉 <c>..</c>、改写百分号编码），路径规范化的用例必须绕开它。<paramref name="framing"/> 为 null 时不声明请求体。
    /// </summary>
    internal async Task<RawResponse> RawAsync(string method, string target, string? bearer, string? framing = null,
        ReadOnlyMemory<byte> body = default)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, Address.Port, timeout.Token);
        var stream = client.GetStream();
        var head = new StringBuilder()
            .Append(method).Append(' ').Append(target).Append(" HTTP/1.1\r\n")
            .Append("Host: ").Append(Address.Authority).Append("\r\n")
            .Append("Content-Type: application/json\r\n");
        if (bearer is not null) head.Append("Authorization: Bearer ").Append(bearer).Append("\r\n");
        if (framing is not null) head.Append(framing).Append("\r\n");
        head.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), timeout.Token);
        if (!body.IsEmpty) await stream.WriteAsync(body, timeout.Token);
        await stream.FlushAsync(timeout.Token);

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        using var received = new MemoryStream();
        var chunk = new byte[8192];
        var text = string.Empty;
        while (!IsComplete(text))
        {
            var read = await stream.ReadAsync(chunk, timeout.Token);
            if (read == 0) break;
            received.Write(chunk, 0, read);
            text = Encoding.UTF8.GetString(received.GetBuffer(), 0, (int)received.Length);
        }
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        Assert.True(text.StartsWith("HTTP/1.1 ", StringComparison.Ordinal) && text.Length >= 12, "没有收到 HTTP 响应：" + text);
        return new RawResponse(int.Parse(text.AsSpan(9, 3)), text, elapsed);
    }

    // 响应是否已经收全：有 Content-Length 时按长度，chunked 时看结束块；两者都没有时只能读到连接关闭。
    private static bool IsComplete(string text)
    {
        var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (headerEnd < 0) return false;
        var headers = text[..headerEnd];
        var bodyText = text[(headerEnd + 4)..];
        foreach (var line in headers.Split("\r\n"))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                return Encoding.UTF8.GetByteCount(bodyText) >= long.Parse(line["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
            if (line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase) && line.Contains("chunked", StringComparison.OrdinalIgnoreCase))
                return bodyText.EndsWith("0\r\n\r\n", StringComparison.Ordinal);
        }
        return false;
    }

    /// <summary>请求体补空白到恰好 <paramref name="length"/> 字节（JSON 允许结尾空白）。</summary>
    internal static byte[] Pad(string json, long length)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        Assert.True(bytes.Length <= length, "基础请求体已超过目标长度。");
        var padded = new byte[length];
        Array.Fill(padded, (byte)' ');
        bytes.CopyTo(padded, 0);
        return padded;
    }

    internal static async Task<(string Code, string Message)> ErrorAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = body.RootElement.GetProperty("error");
        return (error.GetProperty("code").GetString()!, error.GetProperty("message").GetString()!);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
    }
}

/// <summary>一个采集接口：路径、请求体上限、所用令牌、动作方法和一个合法请求体。</summary>
internal sealed record CollectorRoute(string Path, long Limit, string Token, Type Controller, string Action, string ValidBody);

/// <summary>原始 TCP 请求得到的响应：状态码、完整响应文本，以及从发完请求到收全响应的时间。</summary>
internal sealed record RawResponse(int Status, string Text, TimeSpan Elapsed);
