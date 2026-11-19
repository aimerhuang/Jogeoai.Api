using System.Net;
using System.Reflection;
using System.Text.Json;
using Jogeoai.Api;
using Jogeoai.Api.Controllers;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Jogeoai.Tests;

public sealed class BrandRecognitionTests
{
    private const string Source = "优联（Ulinktek）研发工业检测设备，官网是 ulinktek.test。";
    private static ResearchProviderConfiguration Configuration => new("openai-compatible", "not-luna", "https://llm.test/completions", "test-only", true, 30, 1);
    private static string ValidOutput => JsonSerializer.Serialize(new
    {
        name = new { value = "优联", evidence = Source },
        description = new { value = "研发工业检测设备", evidence = Source },
        primaryDomain = new { value = "ulinktek.test", evidence = Source },
        aliases = new[] { new { value = "Ulinktek", evidence = Source } },
    });

    [Fact]
    public void EvidenceBackedFieldsAreReturnedAndUnsupportedSuggestionsStayUnknown()
    {
        var result = LunaBrandRecognitionProvider.Normalize(ValidOutput, Source);
        Assert.Equal(("优联", "ulinktek.test", "gpt-6-luna"), (result.Name, result.PrimaryDomain, result.Model));
        Assert.Equal(["Ulinktek"], result.Aliases);
        result = LunaBrandRecognitionProvider.Normalize(ValidOutput, "无关资料");
        Assert.Null(result.Name); Assert.Null(result.Description); Assert.Null(result.PrimaryDomain);
        Assert.Empty(result.Aliases); Assert.NotEmpty(result.Warnings);
        var source = "第三方目录列出了 优联，页面网址 directory.test。";
        var thirdParty = JsonSerializer.Serialize(new
        {
            name = new { value = "优联", evidence = source }, description = (object?)null,
            primaryDomain = new { value = "directory.test", evidence = source }, aliases = Array.Empty<object>(),
        });
        Assert.Null(LunaBrandRecognitionProvider.Normalize(thirdParty, source).PrimaryDomain);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"choices\":[]}")]
    public void InvalidOutputsHaveSafeStableErrors(string raw)
        => Assert.Equal("BRAND_RECOGNITION_INVALID_OUTPUT", Assert.Throws<CollectorStoreException>(() => LunaBrandRecognitionProvider.Normalize(raw, Source)).Code);

    [Fact]
    public async Task ProviderUsesFixedLunaNoSearchAndRepairsOnlyOnce()
    {
        var client = new FakeClients((number, _) => Task.FromResult(Reply(number == 1 ? "{}" : ValidOutput)));
        var provider = new LunaBrandRecognitionProvider(client);
        var result = await provider.RecognizeAsync(Source, false, Configuration);
        Assert.Equal("优联", result.Name); Assert.Equal(2, client.Bodies.Count);
        foreach (var body in client.Bodies)
        {
            using var json = JsonDocument.Parse(body);
            Assert.Equal("gpt-6-luna", json.RootElement.GetProperty("model").GetString());
            Assert.False(json.RootElement.TryGetProperty("tools", out _));
            Assert.False(json.RootElement.TryGetProperty("web_search", out _));
            Assert.DoesNotContain("test-only", body);
        }
        var alwaysInvalid = new FakeClients((_, _) => Task.FromResult(Reply("{}")));
        var error = await Assert.ThrowsAsync<CollectorStoreException>(() => new LunaBrandRecognitionProvider(alwaysInvalid).RecognizeAsync(Source, false, Configuration));
        Assert.Equal("BRAND_RECOGNITION_INVALID_OUTPUT", error.Code);
        Assert.Equal(2, alwaysInvalid.Bodies.Count);
    }

    [Fact]
    public async Task OversizedAndGatewayErrorsDoNotLeakRawDataOrRetry()
    {
        var large = new FakeClients((_, _) => Task.FromResult(Reply(new string('x', 2 * 1024 * 1024 + 1))));
        Assert.Equal("BRAND_RECOGNITION_INVALID_OUTPUT", (await Assert.ThrowsAsync<CollectorStoreException>(() =>
            new LunaBrandRecognitionProvider(large).RecognizeAsync(Source, false, Configuration))).Code);
        Assert.Single(large.Bodies);
        var failure = new FakeClients((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("private raw response test-only") }));
        var error = await Assert.ThrowsAsync<CollectorStoreException>(() => new LunaBrandRecognitionProvider(failure).RecognizeAsync(Source, false, Configuration));
        Assert.Equal("BRAND_RECOGNITION_UNAVAILABLE", error.Code);
        Assert.DoesNotContain("private", error.Message); Assert.DoesNotContain("test-only", error.Message);
        Assert.Single(failure.Bodies);
    }

    [Fact]
    public async Task NetworkAndStreamFailuresMapToUnavailableWithoutRawMessage()
    {
        foreach (var exception in new Exception[] { new HttpRequestException("private network details"), new IOException("private stream details") })
        {
            var client = new FakeClients((_, _) => Task.FromException<HttpResponseMessage>(exception));
            var error = await Assert.ThrowsAsync<CollectorStoreException>(() => new LunaBrandRecognitionProvider(client).RecognizeAsync(Source, false, Configuration));
            Assert.Equal("BRAND_RECOGNITION_UNAVAILABLE", error.Code);
            Assert.DoesNotContain("private", error.Message);
            Assert.Single(client.Bodies);
        }
    }

    [Theory]
    [InlineData("http://127.0.0.1/", false)]
    [InlineData("http://169.254.169.254/", false)]
    [InlineData("http://8.8.8.8/", true)]
    public async Task RealSourceReaderRejectsPrivateAddressAndPrivateRedirectBeforeModel(string url, bool redirect)
    {
        var requests = 0;
        var client = new SourceClients(() =>
        {
            requests++;
            return new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("http://127.0.0.1/private") } };
        });
        var reader = new ArticleSourceReader(client);
        var read = await reader.ReadAsync(url);
        Assert.Equal("unreadable", read.ReadStatus);
        Assert.Equal("SOURCE_PRIVATE_HOST", read.ErrorCode);
        Assert.Equal(redirect ? 1 : 0, requests);
    }

    [Fact]
    public async Task TimeoutIsMappedButCallerCancellationPropagates()
    {
        var waiting = new FakeClients(async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return Reply("{}"); });
        Assert.Equal("BRAND_RECOGNITION_TIMEOUT", (await Assert.ThrowsAsync<CollectorStoreException>(() =>
            new LunaBrandRecognitionProvider(waiting).RecognizeAsync(Source, false, Configuration with { TimeoutSeconds = 1 }))).Code);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LunaBrandRecognitionProvider(waiting)
            .RecognizeAsync(Source, false, Configuration, cancellation.Token));
    }

    [Fact]
    public async Task TextBypassesReaderUrlUsesReaderAndUnknownConfigurationDoesNotRead()
    {
        var fixture = new ServiceFixture();
        await fixture.Service.RecognizeAsync(new(Source));
        Assert.Equal(0, fixture.ReaderCalls); Assert.Equal(Source, fixture.Source);
        await fixture.Service.RecognizeAsync(new(Url: "https://www.directory.test/path"));
        Assert.Equal("https://www.directory.test/path", fixture.Url);
        Assert.DoesNotContain("directory.test", fixture.Source!);
        Assert.Equal(1, fixture.ReaderCalls);
        fixture.Configured = false;
        Assert.Equal("BRAND_RECOGNITION_NOT_CONFIGURED", (await Assert.ThrowsAsync<CollectorStoreException>(() => fixture.Service.RecognizeAsync(new(Url: "https://www.directory.test/path")))).Code);
        Assert.Equal(1, fixture.ReaderCalls);
    }

    [Theory]
    [InlineData("SOURCE_PRIVATE_HOST")]
    [InlineData("TOO_MANY_REDIRECTS")]
    [InlineData("SOURCE_TOO_LARGE")]
    [InlineData("SOURCE_EMPTY")]
    public async Task UnreadableSourcesWithoutOtherMaterialNeverCallModel(string code)
    {
        var fixture = new ServiceFixture { ReaderError = code };
        var error = await Assert.ThrowsAsync<CollectorStoreException>(() => fixture.Service.RecognizeAsync(new(Url: "https://directory.test/", Name: "柯丽尔")));
        Assert.Equal("BRAND_SOURCE_UNAVAILABLE", error.Code);
        Assert.Null(fixture.Source);
        Assert.DoesNotContain(code, error.Message);
    }

    [Fact]
    public async Task UnreadablePageFallsBackToSearchSummaryWithSourcesAndWarnings()
    {
        var sources = new[] { new BrandRecognitionSource("巨鼎医疗", "https://www.clearofchina.com/") };
        var fixture = new ServiceFixture { ReaderError = "SOURCE_PRIVATE_HOST", Found = new("柯丽尔是巨鼎医疗旗下品牌。官网：clearofchina.com", sources, null) };
        var result = await fixture.Service.RecognizeAsync(new(Url: "https://www.clearofchina.com", Name: " 柯丽尔 ", Aliases: ["clear", "CLEAR", " ", "巨鼎医疗"]));
        Assert.Equal(("柯丽尔", "https://www.clearofchina.com/"), (fixture.Query!.Name, fixture.Query.Url));
        Assert.Equal(["clear", "巨鼎医疗"], fixture.Query.Aliases);
        Assert.Equal("【用户填写】\n品牌名称：柯丽尔\n品牌别名：clear、巨鼎医疗\n\n【联网搜索摘要】\n柯丽尔是巨鼎医疗旗下品牌。官网：clearofchina.com", fixture.Source);
        Assert.Equal(sources, result.Sources);
        Assert.Equal(["资料网页未能读取，已使用其他资料识别。", "资料不足"], result.Warnings);
    }

    [Fact]
    public async Task NameOnlySearchesDescriptionOnlyDoesNotAndSearchFailureKeepsDescription()
    {
        var fixture = new ServiceFixture { Found = new("柯丽尔是医学影像品牌。", [new("来源", "https://a.test/")], null) };
        await fixture.Service.RecognizeAsync(new(Name: "柯丽尔"));
        Assert.Equal("柯丽尔", fixture.Query!.Name);
        Assert.Null(fixture.Query.Url); Assert.Equal(0, fixture.ReaderCalls);
        Assert.Contains("【联网搜索摘要】", fixture.Source);
        fixture = new ServiceFixture();
        await fixture.Service.RecognizeAsync(new(Source));
        Assert.Null(fixture.Query); Assert.Equal(Source, fixture.Source);
        var result = await fixture.Service.RecognizeAsync(new(Source, Name: "优联"));
        Assert.Equal("品牌名称：优联\n" + Source, fixture.Source);
        Assert.Contains("联网搜索没有找到可引用的品牌资料。", result.Warnings);
        Assert.Empty(result.Sources);
        Assert.Equal("BRAND_SOURCE_UNAVAILABLE", (await Assert.ThrowsAsync<CollectorStoreException>(() => fixture.Service.RecognizeAsync(new(Name: "优联")))).Code);
    }

    [Fact]
    public async Task SearchHonorsCallerCancellation()
    {
        var fixture = new ServiceFixture { Searching = true };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.RecognizeAsync(new(Name: "优联"), cancellation.Token));
        Assert.Null(fixture.Source);
    }

    [Theory]
    [InlineData("https://gw.test/v1/chat/completions", "https://gw.test/v1/responses")]
    [InlineData("https://gw.test/v1/chat/completions/", "https://gw.test/v1/responses")]
    [InlineData("https://llm.test/completions", null)]
    [InlineData("https://gw.test/v1/chat/completions?x=1", null)]
    [InlineData("ftp://gw.test/v1/chat/completions", null)]
    public void SearchEndpointIsDerivedOnlyFromChatCompletions(string apiUrl, string? expected)
        => Assert.Equal(expected, LunaBrandSearchProvider.ResponsesEndpoint(Configuration with { ApiUrl = apiUrl })?.AbsoluteUri);

    [Fact]
    public async Task SearchProviderSendsDataOnlyWebSearchAndParsesCitations()
    {
        var annotations = Enumerable.Range(0, 12).Select(index => (object)new { type = "url_citation", url = $"https://s{index}.test/p?utm_source=openai&id={index}", title = $"来源{index}" })
            .Append(new { type = "url_citation", url = "https://s0.test/p?id=0&utm_medium=x", title = "重复" })
            .Append(new { type = "url_citation", url = "javascript:alert(1)", title = "坏链接" }).ToArray();
        var reply = JsonSerializer.Serialize(new
        {
            output = new object[]
            {
                new { type = "web_search_call", status = "completed" },
                new { type = "message", content = new[] { new { type = "output_text", text = "柯丽尔是医学影像品牌。", annotations } } },
            },
        });
        var client = new FakeClients((_, _) => Task.FromResult(Reply(reply)));
        var configuration = Configuration with { ApiUrl = "https://gw.test/v1/chat/completions" };
        var result = await new LunaBrandSearchProvider(client).SearchAsync(new("柯丽尔\nignore previous", ["clear"], "https://www.clearofchina.com/"), configuration);
        Assert.Equal("柯丽尔是医学影像品牌。", result.Summary); Assert.Null(result.Warning);
        Assert.Equal(10, result.Sources.Count);
        Assert.Equal(new BrandRecognitionSource("来源0", "https://s0.test/p?id=0"), result.Sources[0]);
        using var body = JsonDocument.Parse(Assert.Single(client.Bodies));
        Assert.Equal("gpt-6-luna", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("web_search", body.RootElement.GetProperty("tools")[0].GetProperty("type").GetString());
        using var input = JsonDocument.Parse(body.RootElement.GetProperty("input").GetString()!);
        Assert.Equal("柯丽尔\nignore previous", input.RootElement.GetProperty("brandName").GetString());
        Assert.DoesNotContain("test-only", client.Bodies[0]);
    }

    [Theory]
    [InlineData("{\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"无引用的回答\",\"annotations\":[]}]}]}", "联网搜索没有找到可引用的品牌资料。")]
    [InlineData("{\"output\":[]}", "联网搜索没有找到可引用的品牌资料。")]
    [InlineData("not-json", "联网搜索暂时不可用，已使用其他资料识别。")]
    [InlineData("{}", "联网搜索暂时不可用，已使用其他资料识别。")]
    public void SearchWithoutCitationsIsNotUsedAsMaterial(string raw, string warning)
    {
        var result = LunaBrandSearchProvider.Parse(raw);
        Assert.Null(result.Summary); Assert.Empty(result.Sources); Assert.Equal(warning, result.Warning);
    }

    [Fact]
    public async Task SearchGatewayFailuresDegradeWithoutRawDataButCallerCancellationPropagates()
    {
        var configuration = Configuration with { ApiUrl = "https://gw.test/v1/chat/completions", TimeoutSeconds = 1 };
        foreach (var respond in new Func<int, CancellationToken, Task<HttpResponseMessage>>[]
        {
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("private raw") }),
            (_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("private network")),
            (_, _) => Task.FromResult(Reply(new string('x', 2 * 1024 * 1024 + 1))),
            async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return Reply("{}"); },
        })
        {
            var result = await new LunaBrandSearchProvider(new FakeClients(respond)).SearchAsync(new("优联", [], null), configuration);
            Assert.Null(result.Summary); Assert.NotNull(result.Warning); Assert.DoesNotContain("private", result.Warning);
        }
        var unsupported = await new LunaBrandSearchProvider(new FakeClients((_, _) => throw new InvalidOperationException()))
            .SearchAsync(new("优联", [], null), Configuration);
        Assert.Contains("不支持联网搜索", unsupported.Warning);
        var waiting = new FakeClients(async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return Reply("{}"); });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LunaBrandSearchProvider(waiting)
            .SearchAsync(new("优联", [], null), configuration with { TimeoutSeconds = 30 }, cancellation.Token));
    }

    [Fact]
    public async Task InputValidationAndSourceTruncationAreBounded()
    {
        var fixture = new ServiceFixture { Body = new string('文', 61_000) };
        foreach (var request in new[] { new BrandRecognitionRequest(), new(new string('文', 4001)), new(Url: "file:///private/file"), new(Url: new string('x', 2001)),
            new(Name: new string('文', 201)), new(Name: "优联", Aliases: Enumerable.Repeat("别名", 21).ToArray()), new(Name: "优联", Aliases: [new string('文', 201)]),
            new(Aliases: ["只有别名"]) })
            Assert.Equal("INVALID_BRAND_RECOGNITION_REQUEST", (await Assert.ThrowsAsync<CollectorStoreException>(() => fixture.Service.RecognizeAsync(request))).Code);
        Assert.Equal(0, fixture.SettingsCalls);
        await fixture.Service.RecognizeAsync(new(Url: "https://directory.test/"));
        Assert.True(fixture.Truncated); Assert.Equal(60_000, fixture.Source!.Length);
    }

    [Theory]
    [InlineData("owner", 200)]
    [InlineData("admin", 200)]
    [InlineData("member", 200)]
    [InlineData("viewer", 403)]
    [InlineData("unknown", 403)]
    [InlineData(null, 401)]
    public async Task RecognitionChecksRoleBeforeWorkAndBrandsRejectViewer(string? role, int expectedStatus)
    {
        var context = Context(role);
        var fixture = new ServiceFixture();
        var resolver = new TenantContextResolver(new TestEnvironment());
        var controller = new BrandRecognitionController(fixture.Service, resolver) { ControllerContext = new() { HttpContext = context } };
        var result = await controller.Recognize(new(Source), default);
        Assert.Equal(expectedStatus, Assert.IsAssignableFrom<ObjectResult>(result.Result).StatusCode);
        Assert.Equal(expectedStatus == 200 ? 1 : 0, fixture.SettingsCalls);
        if (role != "viewer") return;
        var store = DispatchProxy.Create<IManagementStore, TeamApiDocumentationTests.HandlerProxy>();
        var progress = DispatchProxy.Create<IOnboardingProgressStore, TeamApiDocumentationTests.HandlerProxy>();
        var management = new ManagementController(store, resolver) { ControllerContext = new() { HttpContext = context } };
        Assert.Equal(403, Assert.IsType<ObjectResult>(await management.CreateSite(new("品牌", Description: Source), default)).StatusCode);
        Assert.Equal(403, Assert.IsType<ObjectResult>(await management.PatchSite(Guid.NewGuid(), new(ExpectedVersion: 1), default)).StatusCode);
        var onboarding = new OnboardingController(store, progress, resolver) { ControllerContext = new() { HttpContext = context } };
        Assert.Equal(403, Assert.IsType<ObjectResult>((await onboarding.CreateBrand(new("品牌", Description: Source), default)).Result).StatusCode);
    }

    [Fact]
    public async Task TenantHeaderCannotGrantAccessOrRotateAuthenticatedRateLimitPartition()
    {
        var context = Context("member");
        var key = BrandRecognitionRateLimit.PartitionKey(context);
        context.Request.Headers["X-Tenant-Id"] = Guid.NewGuid().ToString();
        Assert.Equal(key, BrandRecognitionRateLimit.PartitionKey(context));
        var fixture = new ServiceFixture();
        var controller = new BrandRecognitionController(fixture.Service, new(new TestEnvironment())) { ControllerContext = new() { HttpContext = context } };
        Assert.Equal(403, Assert.IsType<ObjectResult>((await controller.Recognize(new(Source), default)).Result).StatusCode);
        Assert.Equal(0, fixture.SettingsCalls);
    }

    [Fact]
    public void RealRecognitionPolicyRejectsSeventhRequestWithoutQueueing()
    {
        var partition = BrandRecognitionRateLimit.Partition(Context("member"));
        using var limiter = partition.Factory(partition.PartitionKey);
        for (var index = 0; index < 6; index++)
        {
            using var lease = limiter.AttemptAcquire();
            Assert.True(lease.IsAcquired);
        }
        using var rejected = limiter.AttemptAcquire();
        Assert.False(rejected.IsAcquired);
    }

    private static DefaultHttpContext Context(string? role)
    {
        var context = new DefaultHttpContext();
        if (role is not null)
            context.Items[SessionAuthenticationMiddleware.ContextItemKey] = new AuthenticatedRequestContext(
                new(Guid.NewGuid(), "test@example.test", "测试", "active", [new TenantMembershipView(Guid.NewGuid(), "团队", role, "active")]), Guid.NewGuid(), "test-session");
        return context;
    }
    private static HttpResponseMessage Reply(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private sealed class SourceClients(Func<HttpResponseMessage> response) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(response));
        private sealed class Handler(Func<HttpResponseMessage> response) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(response());
        }
    }
    private sealed class FakeClients(Func<int, CancellationToken, Task<HttpResponseMessage>> respond) : IHttpClientFactory
    {
        public List<string> Bodies { get; } = [];
        private Task<HttpResponseMessage> Respond(int number, CancellationToken cancellationToken) => respond(number, cancellationToken);
        public HttpClient CreateClient(string name) => new(new Handler(this)) { Timeout = Timeout.InfiniteTimeSpan };
        private sealed class Handler(FakeClients owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
                return await owner.Respond(owner.Bodies.Count, cancellationToken);
            }
        }
    }
    private sealed class ServiceFixture : IArticleSourceReader, IBrandRecognitionProvider, IBrandSearchProvider
    {
        public BrandRecognitionService Service { get; }
        public int SettingsCalls { get; private set; }
        public int ReaderCalls { get; private set; }
        public bool Configured { get; set; } = true;
        public string? ReaderError { get; set; }
        public string Body { get; set; } = BrandRecognitionTests.Source;
        public string? Source { get; private set; }
        public string? Url { get; private set; }
        public bool Truncated { get; private set; }
        public BrandSearchQuery? Query { get; private set; }
        public BrandSearchResult Found { get; set; } = new(null, [], "联网搜索没有找到可引用的品牌资料。");
        public bool Searching { get; set; }
        public ServiceFixture()
        {
            var settings = DispatchProxy.Create<IResearchStore, TeamApiDocumentationTests.HandlerProxy>();
            ((TeamApiDocumentationTests.HandlerProxy)(object)settings).Handler = (method, args) =>
            {
                Assert.Equal(nameof(IResearchStore.GetProviderConfigurationAsync), method);
                Assert.Equal("openai-compatible", args![0]);
                SettingsCalls++;
                return Task.FromResult<ResearchProviderConfiguration?>(Configured ? Configuration : null);
            };
            Service = new(settings, this, this, this);
        }
        public Task<ArticleSourceReadResult> ReadAsync(string url, CancellationToken cancellationToken = default)
        {
            ReaderCalls++; Url = url;
            return Task.FromResult(new ArticleSourceReadResult(url, url, "hash", ReaderError is null ? "ready" : "unreadable", null,
                ReaderError is null ? Body : null, null, [], ReaderError, null));
        }
        public Task<BrandRecognitionView> RecognizeAsync(string sourceText, bool truncated, ResearchProviderConfiguration configuration, CancellationToken cancellationToken = default)
        {
            Source = sourceText; Truncated = truncated;
            return Task.FromResult(new BrandRecognitionView("优联", "介绍", null, [], ["资料不足"], "openai-compatible", "gpt-6-luna", []));
        }
        public async Task<BrandSearchResult> SearchAsync(BrandSearchQuery query, ResearchProviderConfiguration configuration, CancellationToken cancellationToken = default)
        {
            Query = query;
            if (Searching) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Found;
        }
    }
    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
