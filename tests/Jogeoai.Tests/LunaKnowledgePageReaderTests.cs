using System.Net;
using System.Text;
using System.Text.Json;
using Jogeoai.Application;
using Jogeoai.Infrastructure;

namespace Jogeoai.Tests;

/// <summary>Luna AI 联网读取网页：请求体、引用主机校验、引用标记清理、截断与网关错误映射，全部用假 Handler，不触网。</summary>
public sealed class LunaKnowledgePageReaderTests
{
    private const string Secret = "secret-token-1234567890";
    private const string PageUrl = "https://clearofchina.com/products/a";
    private static readonly ResearchProviderConfiguration Configuration =
        new("openai-compatible", "attacker-model", "https://gw.test/v1/chat/completions", Secret, false, 30, 1);

    [Fact]
    public async Task RequestTargetsResponsesEndpointWithFixedModelWebSearchAndUrlOnlyInput()
    {
        var text = "柯丽尔 CR-10 牙科影像板，分辨率 20 lp/mm。 ([clearofchina.com](https://www.clearofchina.com/products/a?utm_source=openai))\n适用于口腔诊所。";
        var factory = new FakeHttpClientFactory((_, _) => Task.FromResult(Ok(Reply(text,
            Citation("https://www.clearofchina.com/products/a?utm_source=openai", "CR-10 产品页"), Citation("https://other.test/x", "其他")))));

        var result = await new LunaKnowledgePageReader(factory).ReadAsync(PageUrl, Configuration);

        var request = Assert.Single(factory.Requests);
        Assert.Equal("https://gw.test/v1/responses", request.Uri);
        Assert.Equal($"Bearer {Secret}", request.Authorization);
        using var body = JsonDocument.Parse(request.Body);
        var root = body.RootElement;
        Assert.Equal("gpt-6-luna", root.GetProperty("model").GetString());
        Assert.Contains("untrusted data", root.GetProperty("instructions").GetString(), StringComparison.Ordinal);
        var tool = Assert.Single(root.GetProperty("tools").EnumerateArray());
        Assert.Equal("web_search", tool.GetProperty("type").GetString());
        Assert.Equal("medium", tool.GetProperty("search_context_size").GetString());
        Assert.Equal("approximate", tool.GetProperty("user_location").GetProperty("type").GetString());
        Assert.Equal("CN", tool.GetProperty("user_location").GetProperty("country").GetString());
        using var input = JsonDocument.Parse(root.GetProperty("input").GetString()!);
        Assert.Equal(PageUrl, input.RootElement.GetProperty("url").GetString());
        Assert.Single(input.RootElement.EnumerateObject());
        Assert.DoesNotContain(Secret, request.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("attacker-model", request.Body, StringComparison.Ordinal);

        Assert.True(result.Succeeded);
        Assert.Null(result.ErrorMessage);
        Assert.Equal("柯丽尔 CR-10 牙科影像板，分辨率 20 lp/mm。\n适用于口腔诊所。", result.BodyText);
        Assert.Equal("CR-10 产品页", result.Title);
        Assert.Equal(["https://www.clearofchina.com/products/a?utm_source=openai", "https://other.test/x"], result.CitationUrls);
        Assert.True(result.RequestBytes > 0);
        Assert.True(result.ResponseBytes > 0);
    }

    [Theory]
    [InlineData("https://example.com/a", "https://www.example.com/a", true)]
    [InlineData("https://www.example.com/a", "https://example.com/b", true)]
    [InlineData("https://WWW.Example.com/a", "https://example.COM/", true)]
    [InlineData("https://www.example.com/a", "https://www.example.com/a#frag", true)]
    [InlineData("https://example.com/a", "https://other.com/a", false)]
    [InlineData("https://example.com/a", "https://shop.example.com/a", false)]
    [InlineData("https://example.com/a", "https://example.com.evil.test/a", false)]
    [InlineData("https://example.com/a", "javascript:alert(1)", false)]
    public void ResultIsAcceptedOnlyWhenACitationHasTheRequestedHost(string requested, string citation, bool accepted)
    {
        var result = LunaKnowledgePageReader.Parse(Reply("页面正文。", Citation(citation, "标题")), new Uri(requested));

        Assert.Equal(accepted, result.Succeeded);
        if (accepted) Assert.Equal("页面正文。", result.BodyText);
        else
        {
            Assert.Null(result.BodyText);
            Assert.Equal("AI 未能打开该网页。", result.ErrorMessage);
        }
    }

    [Theory]
    [InlineData("{\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"我无法打开该网页。\",\"annotations\":[]}]}]}", "AI 未能打开该网页。")]
    [InlineData("{\"output\":[{\"type\":\"web_search_call\",\"status\":\"completed\"}]}", "AI 未能打开该网页。")]
    [InlineData("{\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\" ([example.com](https://example.com/a)) \",\"annotations\":[{\"type\":\"url_citation\",\"url\":\"https://example.com/a\"}]}]}]}", "AI 未能打开该网页。")]
    [InlineData("{}", "AI 联网读取返回的结果无法解析。")]
    [InlineData("not-json", "AI 联网读取返回的结果无法解析。")]
    public void MissingCitationTextOrStructureFails(string raw, string message)
    {
        var result = LunaKnowledgePageReader.Parse(raw, new Uri("https://example.com/a"));

        Assert.False(result.Succeeded);
        Assert.Null(result.BodyText);
        Assert.Equal(message, result.ErrorMessage);
    }

    [Fact]
    public void OnlyTheFinalMessageIsParsed()
    {
        var raw = JsonSerializer.Serialize(new
        {
            output = new object[]
            {
                Message("早先的草稿。", Citation("https://example.com/a", "a")),
                new { type = "web_search_call", status = "completed", action = new { type = "open_page", url = "https://example.com/a" } },
                Message("最终正文。", Citation("https://example.com/a", "a")),
            },
        });

        var result = LunaKnowledgePageReader.Parse(raw, new Uri("https://example.com/a"));

        Assert.True(result.Succeeded);
        Assert.Equal("最终正文。", result.BodyText);
    }

    [Fact]
    public void CitationMarkdownIsStrippedAndLongBodiesAreTruncated()
    {
        Assert.Equal("第一句。\n第二句。",
            LunaKnowledgePageReader.StripCitationMarkdown("第一句。 ([a.com](https://a.com/x?utm_source=openai)) ([b.com](http://b.com/))   \r\n第二句。 ([a.com](https://a.com/))"));
        Assert.Equal("保留 [普通链接](https://a.com/) 不变。", LunaKnowledgePageReader.StripCitationMarkdown("保留 [普通链接](https://a.com/) 不变。"));

        var result = LunaKnowledgePageReader.Parse(Reply(new string('文', 61_000) + "尾部标记", Citation("https://example.com/", "t")),
            new Uri("https://example.com/"));

        Assert.True(result.Succeeded);
        Assert.Equal(LunaKnowledgePageReader.MaxBodyCharacters, result.BodyText!.Length);
        Assert.DoesNotContain("尾部标记", result.BodyText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "AI 联网读取请求未被模型网关接受。")]
    [InlineData(HttpStatusCode.Unauthorized, "AI 联网读取服务配置无效。")]
    [InlineData(HttpStatusCode.TooManyRequests, "AI 联网读取服务暂时繁忙。")]
    [InlineData(HttpStatusCode.BadGateway, "AI 联网读取服务暂时繁忙。")]
    [InlineData(HttpStatusCode.NotFound, "AI 联网读取请求未通过。")]
    public async Task GatewayErrorsFailWithoutRawBody(HttpStatusCode status, string message)
    {
        var factory = new FakeHttpClientFactory((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent("private raw gateway body", Encoding.UTF8, "application/json")
        }));

        var result = await new LunaKnowledgePageReader(factory).ReadAsync(PageUrl, Configuration);

        Assert.False(result.Succeeded);
        Assert.Equal(message, result.ErrorMessage);
        Assert.Null(result.BodyText);
        Assert.Single(factory.Requests);
    }

    [Fact]
    public async Task NetworkOversizeAndTimeoutFailButCallerCancellationPropagates()
    {
        var network = new FakeHttpClientFactory((_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("private network")));
        var failed = await new LunaKnowledgePageReader(network).ReadAsync(PageUrl, Configuration);
        Assert.False(failed.Succeeded);
        Assert.Equal("AI 联网读取服务暂时不可用。", failed.ErrorMessage);

        var huge = new FakeHttpClientFactory((_, _) => Task.FromResult(Ok(new string('x', 2 * 1024 * 1024 + 1))));
        var oversized = await new LunaKnowledgePageReader(huge).ReadAsync(PageUrl, Configuration);
        Assert.False(oversized.Succeeded);
        Assert.Equal("AI 联网读取响应超过大小限制。", oversized.ErrorMessage);

        var waiting = new FakeHttpClientFactory(async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return Ok("{}"); });
        var timedOut = await new LunaKnowledgePageReader(waiting).ReadAsync(PageUrl, Configuration with { TimeoutSeconds = 1 });
        Assert.False(timedOut.Succeeded);
        Assert.Equal("AI 联网读取网页超时。", timedOut.ErrorMessage);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new LunaKnowledgePageReader(waiting).ReadAsync(PageUrl, Configuration, cancellation.Token));
    }

    [Theory]
    [InlineData("other-provider", "https://gw.test/v1/chat/completions", false)]
    [InlineData("openai-compatible", "https://gw.test/completions", false)]
    [InlineData("openai-compatible", "https://gw.test/v1/chat/completions?x=1", false)]
    [InlineData("openai-api", "https://gw.test/v1/chat/completions", true)]
    public async Task UnsupportedGatewayConfigurationFailsWithoutCalling(string provider, string apiUrl, bool calls)
    {
        var factory = new FakeHttpClientFactory((_, _) => Task.FromResult(Ok(Reply("页面正文。", Citation(PageUrl, "t")))));

        var result = await new LunaKnowledgePageReader(factory).ReadAsync(PageUrl, Configuration with { Provider = provider, ApiUrl = apiUrl });

        Assert.Equal(calls, result.Succeeded);
        Assert.Equal(calls ? 1 : 0, factory.Requests.Count);
        if (!calls) Assert.Equal("当前识别网关不支持 AI 联网读取网页。", result.ErrorMessage);
    }

    [Theory]
    [InlineData(0, 120)]
    [InlineData(-1, 120)]
    [InlineData(30, 30)]
    [InlineData(300, 120)]
    public void TimeoutUsesConfigurationCappedAtOneHundredTwentySeconds(int configured, int expected)
        => Assert.Equal(expected, LunaKnowledgePageReader.TimeoutSeconds(configured));

    private static object Citation(string url, string title) => new { type = "url_citation", url, title, start_index = 0, end_index = 1 };

    private static object Message(string text, params object[] annotations)
        => new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text, annotations } } };

    private static string Reply(string text, params object[] annotations) => JsonSerializer.Serialize(new
    {
        output = new object[] { new { type = "web_search_call", status = "completed" }, Message(text, annotations) },
    });

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class FakeHttpClientFactory(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : IHttpClientFactory
    {
        public List<(string Uri, string Body, string? Authorization)> Requests { get; } = [];

        public HttpClient CreateClient(string name)
        {
            Assert.Equal("research", name);
            return new HttpClient(new Handler(this, responder));
        }

        private sealed class Handler(FakeHttpClientFactory owner, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
            : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                owner.Requests.Add((request.RequestUri!.AbsoluteUri, body, request.Headers.Authorization?.ToString()));
                var response = await responder(request, cancellationToken);
                response.RequestMessage = request;
                return response;
            }
        }
    }
}
