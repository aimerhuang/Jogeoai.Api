using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using Jogeoai.Api.Controllers;
using Jogeoai.Api.Security;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace Jogeoai.Tests;

/// <summary>
/// 采集接口的请求体上限（RB1）：12 个 POST 动作的 <c>[RequestSizeLimit]</c> 与设计表一致；
/// 进程内 Kestrel 上，令牌正确时超过上限返回 413（声明长度超限时一个字节都不用发；chunked 在实际读到超限字节时），
/// 恰好等于上限的合法请求体照常到达 Store；令牌错误时一律 401，不是 413。不连数据库。
/// </summary>
public sealed class CollectorRequestLimitTests : IAsyncLifetime
{
    private const long KiB = 1024;
    private const long MiB = 1024 * 1024;

    /// <summary>design.md 第 1 节的表。</summary>
    private static readonly Dictionary<string, long> DesignLimits = new()
    {
        ["/internal/collector/app/bindings/report"] = 1 * MiB,
        ["/internal/collector/app/leases"] = 16 * KiB,
        ["/internal/collector/app/sent"] = 16 * KiB,
        ["/internal/collector/app/heartbeats"] = 16 * KiB,
        ["/internal/collector/app/failures"] = 16 * KiB,
        ["/internal/collector/app/results"] = 4 * MiB,
        ["/internal/collector/leases"] = 16 * KiB,
        ["/internal/collector/heartbeats"] = 16 * KiB,
        ["/internal/collector/eligibility"] = 16 * KiB,
        ["/internal/collector/failures"] = 16 * KiB,
        ["/internal/collector/session-events"] = 16 * KiB,
        ["/internal/collector/results"] = 6 * MiB,
    };

    private CollectorPreauthTestHost host = null!;

    public async Task InitializeAsync() => host = await CollectorPreauthTestHost.StartAsync();

    public async Task DisposeAsync() => await host.DisposeAsync();

    public static TheoryData<string> AllPaths() => CollectorPreauthTestHost.PathData();

    public static TheoryData<string> SmallPaths() => CollectorPreauthTestHost.PathData(smallOnly: true);

    [Fact]
    public void ConstantsEqualTheDesignValues()
    {
        Assert.Equal((16_384L, 1_048_576L, 4_194_304L, 6_291_456L),
            (CollectorRequestLimits.SmallBody, CollectorRequestLimits.AppBindingReport, CollectorRequestLimits.AppResult, CollectorRequestLimits.WebResult));
        Assert.Equal(DesignLimits.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            CollectorPreauthTestHost.Routes.ToDictionary(route => route.Path, route => route.Limit).OrderBy(pair => pair.Key, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(typeof(AppCollectorController))]
    [InlineData(typeof(CollectorController))]
    public void EveryPostActionDeclaresItsDesignLimitAndNoOtherActionAcceptsABody(Type controller)
    {
        var prefix = "/" + controller.GetCustomAttribute<RouteAttribute>()!.Template;
        var actions = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttributes<HttpMethodAttribute>().Any())
            .ToArray();
        var posts = actions.Where(method => method.GetCustomAttribute<HttpPostAttribute>() is not null).ToArray();
        Assert.Equal(6, posts.Length);
        foreach (var method in posts)
        {
            var path = prefix + "/" + method.GetCustomAttribute<HttpPostAttribute>()!.Template;
            var limit = Assert.Single(method.GetCustomAttributes<RequestSizeLimitAttribute>());
            Assert.True(DesignLimits.TryGetValue(path, out var expected), $"{path} 不在设计表里；新增采集接口必须先定上限。");
            Assert.Equal(expected, ((IRequestSizeLimitMetadata)limit).MaxRequestBodySize);
            // 测试宿主的路由表与控制器一致：同一路径、同一动作。
            Assert.Equal((controller, method.Name), (CollectorPreauthTestHost.Route(path).Controller, CollectorPreauthTestHost.Route(path).Action));
        }
        // 其余动作只有不带请求体的 GET（health）。
        Assert.All(actions.Except(posts), method => Assert.NotNull(method.GetCustomAttribute<HttpGetAttribute>()));
        Assert.Equal(controller == typeof(CollectorController) ? 1 : 0, actions.Length - posts.Length);
    }

    [Fact]
    public void TheServedEndpointsCarryTheSameLimits()
    {
        var served = host.CollectorEndpoints
            .Where(item => item.Endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()!.HttpMethods.Contains("POST"))
            .ToDictionary(
                item => "/" + ((Microsoft.AspNetCore.Routing.RouteEndpoint)item.Endpoint).RoutePattern.RawText,
                item => item.Endpoint.Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize);
        Assert.Equal(DesignLimits.Select(pair => (pair.Key, (long?)pair.Value)).OrderBy(pair => pair.Key, StringComparer.Ordinal),
            served.Select(pair => (pair.Key, pair.Value)).OrderBy(pair => pair.Key, StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(AllPaths))]
    public async Task ADeclaredLengthOneByteOverTheLimitIs413WithoutSendingAnyBody(string path)
    {
        var route = CollectorPreauthTestHost.Route(path);

        // 只发送请求头：413 在读取请求体之前就返回；服务器若先等请求体，这个请求会超时。
        var response = await host.RawPostAsync(path, route.Token, ContentLength(route.Limit + 1));

        Assert.Equal(413, response.Status);
        Assert.Equal(0, host.AppStoreWork);
        Assert.Equal(0, host.WebStoreWork);
    }

    [Theory]
    [MemberData(nameof(SmallPaths))]
    public async Task AOneMebibyteBodyOnASmallEndpointIs413(string path)
    {
        var route = CollectorPreauthTestHost.Route(path);
        var body = CollectorPreauthTestHost.Pad(route.ValidBody, MiB);

        // Expect: 100-continue —— 客户端先等服务器的答复再发请求体；服务器直接答复 413，1 MiB 不会被读取。
        using var response = await host.PostAsync(path, body, route.Token, configure: request => request.Headers.ExpectContinue = true);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, host.AppStoreWork);
        Assert.Equal(0, host.WebStoreWork);
    }

    [Theory]
    [InlineData("/internal/collector/app/bindings/report")]
    [InlineData("/internal/collector/app/results")]
    [InlineData("/internal/collector/results")]
    public async Task ALargeEndpointRejectsOneByteOverItsOwnLimit(string path)
    {
        var route = CollectorPreauthTestHost.Route(path);
        var body = CollectorPreauthTestHost.Pad(route.ValidBody, route.Limit + 1);

        using var response = await host.PostAsync(path, body, route.Token, configure: request => request.Headers.ExpectContinue = true);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, host.AppStoreWork);
        Assert.Equal(0, host.WebStoreWork);
    }

    [Theory]
    [InlineData("/internal/collector/app/leases")]
    [InlineData("/internal/collector/leases")]
    public async Task AChunkedBodyIsCutOffAt413OnceItPassesTheLimit(string path)
    {
        var route = CollectorPreauthTestHost.Route(path);
        // 不声明长度：一个 limit + 1 字节的分块，不发送结束块。服务器读到超限的那个字节时返回 413。
        var data = CollectorPreauthTestHost.Pad(route.ValidBody, route.Limit + 1);
        var chunk = Encoding.ASCII.GetBytes(data.Length.ToString("x", CultureInfo.InvariantCulture) + "\r\n").Concat(data).ToArray();

        var response = await host.RawPostAsync(path, route.Token, "Transfer-Encoding: chunked", chunk);

        Assert.Equal(413, response.Status);
        Assert.Equal(0, host.AppStoreWork);
        Assert.Equal(0, host.WebStoreWork);
    }

    [Theory]
    [MemberData(nameof(AllPaths))]
    public async Task ABodyExactlyAtTheLimitIsReadAndReachesTheStore(string path)
    {
        var route = CollectorPreauthTestHost.Route(path);

        using var response = await host.PostAsync(path, CollectorPreauthTestHost.Pad(route.ValidBody, route.Limit), route.Token);

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}：{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        Assert.Equal(1, host.AppStoreWork + host.WebStoreWork);
    }

    [Theory]
    [MemberData(nameof(AllPaths))]
    public async Task AWrongOrMissingTokenIs401EvenWhenTheDeclaredLengthIsOverTheLimit(string path)
    {
        var route = CollectorPreauthTestHost.Route(path);

        foreach (var bearer in new[] { null, "wrong-token" })
        {
            var response = await host.RawPostAsync(path, bearer, ContentLength(route.Limit + 1));

            Assert.Equal(401, response.Status);
            Assert.Contains("\"code\":\"UNAUTHORIZED\"", response.Text, StringComparison.Ordinal);
        }
        Assert.Equal(0, host.AppStoreWork);
        Assert.Equal(0, host.WebStoreWork);
    }

    private static string ContentLength(long length) => "Content-Length: " + length.ToString(CultureInfo.InvariantCulture);
}
