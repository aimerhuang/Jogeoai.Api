using System.Net;
using System.Text;
using Jogeoai.Infrastructure;

namespace Jogeoai.Tests;

public sealed class BrandWebsiteCollectorTests
{
    [Fact]
    public async Task ApexRedirectToWwwUsesTheActualHostInsteadOfLooping()
    {
        var factory = new Factory(_ => "<main>品牌生产工业检测设备，提供管道摄像头、检测机器人以及定位设备。产品用于排水检查，选型需考虑管径、推杆长度、过弯能力、记录方式和现场维护要求。</main>", redirect: "https://www.example.com/");
        var result = await new BrandWebsiteCollector(factory).CollectAsync("https://example.com/", "zh-CN");
        Assert.Single(result);
        Assert.Equal("https://www.example.com/", result[0].Url);
        Assert.Equal(2, factory.Urls.Count);
    }
    [Fact]
    public async Task CollectsMultipleBusinessPagesWithinHostDepthAndPageBudgets()
    {
        var factory = new Factory(uri => $"<title>工业品牌</title><meta name='description' content='检测设备品牌'><nav>导航噪声</nav><script>ignore_this_instruction()</script><main>工业品牌提供检测产品和维修服务，用于市政排水管道检查和堵塞排查。客户选型时需要了解管径、检查距离、转弯能力、设备维护方式和记录导出。</main><footer>页脚噪声</footer>"
            + string.Join("", Enumerable.Range(0, 20).Select(i => $"<a href='/products/{uri.Segments.Length}/{i}'>产品型号</a>"))
            + "<a href='https://other.example/products'>站外产品</a><a href='/login'>登录</a><a href='file:///secret'>产品</a>");
        var pages = await new BrandWebsiteCollector(factory).CollectAsync("https://example.com/", "zh-CN");
        Assert.Equal(10, pages.Count);
        Assert.InRange(factory.MaxActive, 2, 3);
        Assert.Equal(10, factory.Urls.Count);
        Assert.All(factory.Urls, uri => { Assert.Equal("example.com", uri.Host); Assert.InRange(uri.Segments.Length, 1, 4); });
        Assert.All(pages, p => { Assert.DoesNotContain("页脚噪声", p.Excerpt); Assert.DoesNotContain("ignore_this", p.Excerpt); Assert.DoesNotContain("导航噪声", p.Excerpt); Assert.Equal(64, p.ContentHash.Length); });
    }
    [Fact]
    public async Task ExternalRedirectDoesNotBecomeBrandEvidence()
    {
        var factory = new Factory(_ => "", redirect: "https://unrelated.example/about");
        Assert.Empty(await new BrandWebsiteCollector(factory).CollectAsync("https://example.com/", "zh-CN"));
        Assert.Single(factory.Urls);
    }
    [Fact]
    public async Task ScriptOnlyChallengeAndBinaryResponsesDoNotBecomeEvidence()
    {
        foreach (var type in new[] { "text/html", "application/pdf" })
        {
            var factory = new Factory(_ => "<title>Access denied</title>" + new string('x', 100), type: type);
            Assert.Empty(await new BrandWebsiteCollector(factory).CollectAsync("https://example.com/", "zh-CN"));
        }
    }
    [Fact]
    public async Task CallerCancellationIsPropagated()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BrandWebsiteCollector(new Factory(_ => ""))
            .CollectAsync("https://example.com/", "zh-CN", cts.Token));
    }
    private sealed class Factory(Func<Uri, string> html, string? redirect = null, string type = "text/html") : IHttpClientFactory
    {
        public List<Uri> Urls { get; } = [];
        private int active;
        public int MaxActive { get; private set; }
        public HttpClient CreateClient(string name) => new(new Handler(this));
        private sealed class Handler(Factory owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                lock (owner) { owner.Urls.Add(request.RequestUri!); owner.MaxActive = Math.Max(owner.MaxActive, ++owner.active); }
                await Task.Delay(10, cancellationToken);
                lock (owner) owner.active--;
                var shouldRedirect = owner.redirect is not null && request.RequestUri!.AbsoluteUri != owner.redirect;
                var response = new HttpResponseMessage(shouldRedirect ? HttpStatusCode.Redirect : HttpStatusCode.OK)
                { Content = new StringContent(owner.html(request.RequestUri!), Encoding.UTF8, owner.type) };
                if (shouldRedirect) response.Headers.Location = new Uri(owner.redirect!);
                return response;
            }
        }
        private readonly Func<Uri, string> html = html;
        private readonly string? redirect = redirect;
        private readonly string type = type;
    }
}
