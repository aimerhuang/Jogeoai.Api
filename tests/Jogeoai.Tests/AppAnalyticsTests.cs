using System.Text.Json;
using System.Text.Json.Serialization;
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

/// <summary>
/// App 渠道的统计与记录接口（implement 阶段 7）里不需要数据库的部分：筛选白名单与“app 不能混用”、两套默认集合不变、
/// 渠道标识的推导与序列化、回答详情的 app 块（不含账号代号）、引用域名汇总的 channel 参数、积分用量的渠道、
/// Run 摘要的已完成检测数，以及三处不提供 App 口径的地方排除已启用的 App 目标。
/// </summary>
public sealed class AppAnalyticsTests
{
    // Same as Program.cs AddJsonOptions: web defaults plus string enums.
    private static readonly JsonSerializerOptions Api = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-17T04:00:00Z");
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Site = Guid.NewGuid();
    private static readonly string[] EightProviders =
        ["cloro", "doubao_api", "deepseek_api", "packy_qwen", "packy_glm", "packy_kimi", "packy_yuanbao", "domestic_collector"];

    private static (string, int) Code(CollectorStoreException exception) => (exception.Code, exception.StatusCode);

    private static AnalyticsFilter Normalize(AnalyticsFilterRequest request) => AnalyticsQueryStore.NormalizeFilter(request, "UTC", Now);

    // 7.1: app and app_collector can be asked for explicitly; app together with any other capture mode is refused.
    [Fact]
    public void TheFilterAcceptsTheAppSourceExplicitlyAndRefusesAppMixedWithAnotherCaptureMode()
    {
        var app = Normalize(new AnalyticsFilterRequest(Providers: ["app_collector"], CaptureModes: ["app"]));
        Assert.Equal(["app_collector"], app.Providers);
        Assert.Equal(["app"], app.CaptureModes);
        // Trimmed, lower-cased and de-duplicated like every other value.
        Assert.Equal(["app"], Normalize(new AnalyticsFilterRequest(Providers: ["app_collector"], CaptureModes: [" APP ", "app"])).CaptureModes);

        foreach (var modes in new[] { new[] { "app", "api" }, ["scraped", "app"], ["api", "scraped", "app"], ["APP", " Scraped "] })
        {
            var mixed = Assert.Throws<CollectorStoreException>(() => Normalize(new AnalyticsFilterRequest(
                Providers: ["app_collector"], CaptureModes: modes)));
            Assert.Equal(("INVALID_FILTER", 400), Code(mixed));
            Assert.Contains("app", mixed.Message);
        }
        // Whatever the providers are, including the full default set.
        Assert.Equal(("INVALID_FILTER", 400), Code(Assert.Throws<CollectorStoreException>(() => Normalize(new AnalyticsFilterRequest(
            Providers: AnalyticsSourceDefaults.Providers, CaptureModes: [.. AnalyticsSourceDefaults.CaptureModes, "app"])))));

        // Still refused: an unknown capture mode, and the collector-side channel name.
        foreach (var unknown in new[] { "consumer_app", "consumer_web", "web", "apps" })
            Assert.Equal(("INVALID_FILTER", 400), Code(Assert.Throws<CollectorStoreException>(() => Normalize(new AnalyticsFilterRequest(
                Providers: ["app_collector"], CaptureModes: [unknown])))));
        Assert.Equal(("INVALID_FILTER", 400), Code(Assert.Throws<CollectorStoreException>(() => Normalize(new AnalyticsFilterRequest(
            Providers: ["app"], CaptureModes: ["app"])))));

        // The existing combinations are accepted as before.
        Assert.Equal(["api", "scraped"], Normalize(new AnalyticsFilterRequest(Providers: EightProviders, CaptureModes: ["scraped", "api"])).CaptureModes);
        AnalyticsQueryStore.RequireSingleChannelModes([]);
        AnalyticsQueryStore.RequireSingleChannelModes(["app"]);
        AnalyticsQueryStore.RequireSingleChannelModes(["api", "scraped"]);
    }

    // 7.1: neither default set gained the App source, so a request that names no source never reads an App sample.
    [Fact]
    public void BothDefaultSourceSetsAreUnchangedAndDoNotContainTheAppSource()
    {
        // The store default (legacy analytics routes).
        var legacy = Normalize(new AnalyticsFilterRequest());
        Assert.Equal(["cloro"], legacy.Providers);
        Assert.Equal(["scraped"], legacy.CaptureModes);
        Assert.Equal(["observed_surface"], legacy.EvidenceClasses);

        // The "all sources" default (monitoring, dashboard, reputation, prompts, MCP).
        Assert.Equal(EightProviders, AnalyticsSourceDefaults.Providers);
        Assert.Equal(["api", "scraped"], AnalyticsSourceDefaults.CaptureModes);
        Assert.Equal(["api_response", "observed_surface", "unknown"], AnalyticsSourceDefaults.EvidenceClasses);
        Assert.Equal(EightProviders, AnalyticsQueryStore.DefaultProviders);
        var all = AnalyticsQueryStore.AllSourcesRequest(Now.AddDays(-7), Now, null);
        Assert.Equal(EightProviders, all.Providers);
        Assert.Equal(["api", "scraped"], all.CaptureModes);
        Assert.DoesNotContain(AppChannelDefinition.Provider, AnalyticsSourceDefaults.Providers);
        Assert.DoesNotContain(AppChannelDefinition.CaptureMode, AnalyticsSourceDefaults.CaptureModes);
    }

    private static MonitoringRecordView Record(string provider, string? captureMode, string? errorCode = null, string status = "succeeded")
        => new(Guid.NewGuid(), Now, "doubao", provider, "doubao", null, true, "CN", "zh-CN", Guid.NewGuid().ToString(), "提问", "", false, true, false,
            "not_mentioned", status, "completed", null, null, 0, "unknown", "/api/v1/visibility-checks/x", CaptureMode: captureMode, ErrorCode: errorCode);

    // 7.2: one derivation for every view. The capture mode alone cannot tell the channel: a domestic web answer is stored
    // as "scraped", the same as a Cloro answer.
    [Theory]
    [InlineData("app_collector", "app", "app")]
    [InlineData("domestic_collector", "scraped", "web")]
    [InlineData("cloro", "scraped", "api")]
    [InlineData("doubao_api", "api", "api")]
    [InlineData("packy_kimi", "api", "api")]
    [InlineData("unknown", "unknown", "api")]
    public void EveryViewDerivesTheChannelFromTheProviderWithTheSameRule(string provider, string captureMode, string channel)
    {
        var expected = MonitoringProviders.Channel(provider);
        Assert.Equal(channel, AppContractNames.ToStorage(expected));

        var row = Record(provider, captureMode);
        Assert.Equal(expected, row.Channel);
        var json = JsonSerializer.SerializeToNode(row, Api)!;
        Assert.Equal((captureMode, channel), (json["captureMode"]!.GetValue<string>(), json["channel"]!.GetValue<string>()));
        Assert.Null(json["errorCode"]);

        var detail = AnalyticsQueryStore.BuildMonitoringAnswerDetail(Stored(provider, captureMode, null), [], null, Filter, null);
        Assert.Equal((expected, captureMode), (detail.Channel, detail.CaptureMode));
        var detailJson = JsonSerializer.SerializeToNode(detail, Api)!;
        Assert.Equal((captureMode, channel), (detailJson["captureMode"]!.GetValue<string>(), detailJson["channel"]!.GetValue<string>()));

        var evidence = new ArticleMonitoringEvidenceView(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "提问", provider, "doubao",
            "zh-CN", "CN", Now, "https://a.test/x", 1, null, null, "/api/v1/visibility-checks/x", captureMode);
        Assert.Equal(expected, evidence.Channel);
        var evidenceJson = JsonSerializer.SerializeToNode(evidence, Api)!;
        Assert.Equal((captureMode, channel), (evidenceJson["captureMode"]!.GetValue<string>(), evidenceJson["channel"]!.GetValue<string>()));

        var discovery = new ArticleDiscoverySummary(Guid.NewGuid(), Now, provider, "doubao", captureMode);
        Assert.Equal(expected, discovery.Channel);
        var discoveryJson = JsonSerializer.SerializeToNode(discovery, Api)!;
        Assert.Equal((captureMode, channel), (discoveryJson["captureMode"]!.GetValue<string>(), discoveryJson["channel"]!.GetValue<string>()));
    }

    // 7.2: a check that was not collected is listed with its reason code.
    [Fact]
    public void ARecordRowCarriesTheNotCollectedReasonCode()
    {
        var json = JsonSerializer.SerializeToNode(Record("app_collector", "app", "APP_NOT_COLLECTED_DEADLINE", "cancelled"), Api)!;
        Assert.Equal(("cancelled", "APP_NOT_COLLECTED_DEADLINE", "app"),
            (json["checkStatus"]!.GetValue<string>(), json["errorCode"]!.GetValue<string>(), json["channel"]!.GetValue<string>()));
    }

    private static AnalyticsFilter Filter => new(Now.AddDays(-30), Now, "UTC", ["app_collector"], [], [], [], ["app"],
        ["observed_surface", "api_response", "unknown"], [], [], [], "any", null, "observed", [], null, [], []);

    private static AnalyticsQueryStore.MonitoringStoredAnswer Stored(string provider, string captureMode, string? metadata)
        => new(Tenant, Site, new AnalyticsCheckFact(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, null, 1, Now.AddHours(-1), "succeeded", true,
                "pending", false, false, false, [], provider, "doubao", "CN", "zh-CN", captureMode, "observed_surface", [], null, "哪个记账工具好用？"),
            "推荐随手记。", [], "CN", "zh-CN", null, null, Now.AddHours(-2), 7, Now.AddHours(-1), ResultMetadata: metadata);

    private static AppResultCommand Command(params AppResultCitation[] citations)
        => new("node-1", Guid.NewGuid(), 1, "result-key-0001", "collector-rev-1", Now.AddMinutes(-2), Now.AddMinutes(-1), "推荐随手记。", citations,
            citations.Length > 0 ? AppCitationsStatus.Read : AppCitationsStatus.Absent, null, AppVisibleState.On, AppVisibleState.Off, ["记账 工具"],
            "REQUEST_DEVICE", "15", "arm64-v8a", "com.larus.nova", "15.7.0", new string('a', 64));

    // The stored row holds the App block under "app", as the intake's JSON_SET writes it.
    private static string StoredMetadata(AppResultCommand command, string accountLabel, int budget = 16_000)
        => "{\"app\": " + AppCollectorPoolStore.AppMetadata(command, "AI_Apps_4KB", accountLabel, 3, budget).Metadata + "}";

    // 7.2: the detail returns the App block written by the intake, without the account label, and the sources that have no URL.
    [Fact]
    public void TheDetailReturnsTheAppBlockWithoutTheAccountLabelAndTheSourcesWithoutUrl()
    {
        const string label = "doubao-account-label-7c1";
        var command = Command(new("记账工具横评", "少数派", "https://www.sspai.com/post/1", "cited"), new("知乎回答", "知乎", null, "cited"),
            new("公众号文章", "微信", "weixin://article/1", "more"), new("只有标题", null, null, null));
        var metadata = StoredMetadata(command, label);
        Assert.Contains(label, metadata);

        var detail = AnalyticsQueryStore.BuildMonitoringAnswerDetail(Stored("app_collector", "app", metadata), [], null, Filter, null);
        Assert.Equal(new AppAnswerView("AI_Apps_4KB", "15", "com.larus.nova", "15.7.0", AppVisibleState.On, AppVisibleState.Off, AppCitationsStatus.Read,
            null, 4, 1, false, new string('a', 64), 3), detail.App);
        Assert.Equal([new AppSourceWithoutUrlView("知乎回答", "知乎", "cited"), new AppSourceWithoutUrlView("公众号文章", "微信", "more"),
            new AppSourceWithoutUrlView("只有标题", null, null)], detail.SourcesWithoutUrl);

        var text = JsonSerializer.Serialize(detail, Api);
        var json = JsonNode(text);
        // The account label is in the stored row and nowhere in the response; neither is the request's own device name.
        Assert.DoesNotContain(label, text);
        Assert.DoesNotContain("accountLabel", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("REQUEST_DEVICE", text);
        Assert.Equal(["androidVersion", "appPackage", "appVersion", "citationsIssue", "citationsStatus", "citationsTotal", "citationsWithUrl",
                "deepThinking", "deviceId", "promptRuleVersion", "screenshotSha256", "sourcesWithoutUrlTruncated", "webSearch"],
            json.GetProperty("app").EnumerateObject().Select(member => member.Name).Order(StringComparer.Ordinal));
        Assert.Equal(("on", "off", "read", false), (json.GetProperty("app").GetProperty("webSearch").GetString(),
            json.GetProperty("app").GetProperty("deepThinking").GetString(), json.GetProperty("app").GetProperty("citationsStatus").GetString(),
            json.GetProperty("app").GetProperty("sourcesWithoutUrlTruncated").GetBoolean()));
        // title, site and list of each source, in the order they were shown; null when the App showed none.
        Assert.Equal(["title=知乎回答|site=知乎|list=cited", "title=公众号文章|site=微信|list=more", "title=只有标题|site=|list="],
            json.GetProperty("sourcesWithoutUrl").EnumerateArray().Select(source
                => string.Join("|", source.EnumerateObject().Select(member => $"{member.Name}={member.Value.GetString()}"))));

        // A record that has not arrived in the snapshot shows no body, no citations and no App block.
        var hidden = AnalyticsQueryStore.BuildMonitoringAnswerDetail(Stored("app_collector", "app", metadata) with { ArrivalId = null }, [], null, Filter, null);
        Assert.Null(hidden.App);
        Assert.Null(hidden.SourcesWithoutUrl);
        Assert.Null(hidden.Answer);
        Assert.Equal(("app", CollectionChannel.App), (hidden.CaptureMode, hidden.Channel));
    }

    private static JsonElement JsonNode(string text) => JsonSerializer.Deserialize<JsonElement>(text);

    // 7.2: sources cut off by the 16 KB limit of the metadata column are reported as truncated, not left to be inferred.
    [Fact]
    public void SourcesCutOffByTheMetadataLimitAreMarkedAsTruncated()
    {
        var command = Command(Enumerable.Range(0, 100).Select(i => new AppResultCitation($"{i:000}" + new string('长', 509), "站点", null, "cited")).ToArray());
        var detail = AnalyticsQueryStore.BuildMonitoringAnswerDetail(Stored("app_collector", "app", StoredMetadata(command, "label")), [], null, Filter, null);
        Assert.True(detail.App!.SourcesWithoutUrlTruncated);
        Assert.Equal((100, 0), (detail.App.CitationsTotal, detail.App.CitationsWithUrl));
        Assert.InRange(detail.SourcesWithoutUrl!.Count, 1, 20);
        // What is kept is the head of the list, in order.
        Assert.Equal(Enumerable.Range(0, detail.SourcesWithoutUrl.Count).Select(i => $"{i:000}"), detail.SourcesWithoutUrl.Select(source => source.Title![..3]));
        Assert.True(JsonNode(JsonSerializer.Serialize(detail, Api)).GetProperty("app").GetProperty("sourcesWithoutUrlTruncated").GetBoolean());

        // Short titles: all of them fit and nothing is marked.
        var small = Command(Enumerable.Range(0, 100).Select(i => new AppResultCitation($"来源 {i}", null, null, null)).ToArray());
        var whole = AnalyticsQueryStore.BuildMonitoringAnswerDetail(Stored("app_collector", "app", StoredMetadata(small, "label")), [], null, Filter, null);
        Assert.Equal((false, 100, 100), (whole.App!.SourcesWithoutUrlTruncated, whole.SourcesWithoutUrl!.Count, whole.App.CitationsTotal));
    }

    // 7.2: only an App block is read; any other metadata (or none) gives no App block, and a damaged one does not fail the detail.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"schemaVersion":1,"answerTruncated":false}""")]
    [InlineData("""{"app":"text"}""")]
    [InlineData("""{"app":null}""")]
    public void MetadataWithoutAnAppBlockGivesNoAppFields(string? metadata)
    {
        var (app, sources) = AppAnswerMetadataCodec.Decode(metadata);
        Assert.Null(app);
        Assert.Null(sources);
        var detail = AnalyticsQueryStore.BuildMonitoringAnswerDetail(Stored("cloro", "scraped", metadata), [], null, Filter, null);
        Assert.Null(detail.App);
        Assert.Null(detail.SourcesWithoutUrl);
        Assert.Equal(CollectionChannel.Api, detail.Channel);
        var json = JsonNode(JsonSerializer.Serialize(detail, Api));
        Assert.Equal((JsonValueKind.Null, JsonValueKind.Null), (json.GetProperty("app").ValueKind, json.GetProperty("sourcesWithoutUrl").ValueKind));
    }

    // A block written before the marker existed: truncation is told from the counts.
    [Fact]
    public void AnAppBlockWithoutTheMarkerIsReadFromTheCounts()
    {
        var (app, sources) = AppAnswerMetadataCodec.Decode("""
            {"app":{"deviceId":"AVD","webSearch":"bogus","citationsStatus":"read","citationsTotal":5,"citationsWithUrl":2,
                    "sourcesWithoutUrl":[{"title":"甲","site":null,"list":null}],"accountLabel":"secret-label"}}
            """);
        Assert.Equal((true, 5, 2, AppVisibleState.Unknown, AppVisibleState.Unknown, "AVD"),
            (app!.SourcesWithoutUrlTruncated, app.CitationsTotal, app.CitationsWithUrl, app.WebSearch, app.DeepThinking, app.DeviceId));
        Assert.Equal([new AppSourceWithoutUrlView("甲", null, null)], sources);
        Assert.False(AppAnswerMetadataCodec.Decode("""{"app":{"citationsTotal":3,"citationsWithUrl":2,"sourcesWithoutUrl":[{"title":"甲"}]}}""").App!
            .SourcesWithoutUrlTruncated);
        // An explicit marker wins over the counts.
        Assert.False(AppAnswerMetadataCodec.Decode("""{"app":{"citationsTotal":9,"citationsWithUrl":0,"sourcesWithoutUrl":[],"sourcesWithoutUrlTruncated":false}}""")
            .App!.SourcesWithoutUrlTruncated);
    }

    // 7.3: the channel selects one of two fixed source sets; "api" is the set that was hard-coded before the parameter existed.
    [Fact]
    public void TheCitationChannelSelectsOneOfTwoFixedSourceSets()
    {
        foreach (var value in new[] { null, "", "   ", "api", " API " })
        {
            var api = AnalyticsQueryStore.CitationChannelSources.Parse(value);
            Assert.Equal(CollectionChannel.Api, api.Channel);
            Assert.Equal(EightProviders, api.Providers);
            Assert.Same(AnalyticsQueryStore.DefaultProviders, api.Providers);
            Assert.Equal(["api", "scraped"], api.CaptureModes);
        }
        foreach (var value in new[] { "app", " App " })
        {
            var app = AnalyticsQueryStore.CitationChannelSources.Parse(value);
            Assert.Equal(CollectionChannel.App, app.Channel);
            Assert.Equal(["app_collector"], app.Providers);
            Assert.Equal(["app"], app.CaptureModes);
        }
        foreach (var value in new[] { "web", "all", "api,app", "consumer_app", "app_collector" })
            Assert.Equal(("INVALID_FILTER", 400), Code(Assert.Throws<CollectorStoreException>(() => AnalyticsQueryStore.CitationChannelSources.Parse(value))));
    }

    private sealed class DevelopmentEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static AnalyticsController Controller(string query, List<(string Method, object?[] Args)> calls)
    {
        var queries = StudioFakes.Of<IAnalyticsQueryStore>((method, args) =>
        {
            calls.Add((method, args ?? []));
            return method switch
            {
                nameof(IAnalyticsQueryStore.CitationDomainSummaryAsync) => Task.FromResult(new CitationDomainSummaryView(null!, null!, [], null!)),
                nameof(IAnalyticsQueryStore.CitationDomainSummaryDomainsAsync) => Task.FromResult(new CitationDomainSummaryPage(null!, [], null, 0)),
                nameof(IAnalyticsQueryStore.CitationDomainSummaryEvidenceAsync) => Task.FromResult(new CitationDomainEvidencePage(null!, [], null, null)),
                nameof(IAnalyticsQueryStore.CitationDomainSummaryUrlsAsync) => Task.FromResult(new CitationUrlSummaryPage(null!, [], null, 0)),
                nameof(IAnalyticsQueryStore.CitationDomainDetailAsync) => Task.FromResult(new CitationDomainDetailView(null!, "a.test", null, 0, 0, [], [])),
                nameof(IAnalyticsQueryStore.CitationUrlPromptsAsync) => Task.FromResult(new CitationUrlPromptBreakdown(null!, "h", "u", null, null, 0, 0, 0, [])),
                // The source-filter cases only look at the request that reached the store.
                nameof(IAnalyticsQueryStore.MonitoringSummaryAsync) => Task.FromResult<MonitoringSummaryView>(null!),
                nameof(IAnalyticsQueryStore.OverviewAsync) => Task.FromResult<OverviewView>(null!),
                _ => throw new InvalidOperationException(method),
            };
        });
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(query);
        context.Request.Headers["X-Tenant-Id"] = Tenant.ToString();
        return new(queries, null!, new TenantContextResolver(new DevelopmentEnvironment())) { ControllerContext = new ControllerContext { HttpContext = context } };
    }

    private static readonly string Hash = new('a', 64);

    // Follow-up B: an explicitly empty captureMode= means every capture mode. With a provider list that is also every
    // provider (provider=) or names the App provider, the request would merge the App channel with the others. Refused by
    // the controller's Filter(), so a route of the "all sources" family and a legacy route are both covered.
    [Theory]
    [InlineData("?captureMode=&provider=")]
    [InlineData("?captureMode=&provider=app_collector")]
    [InlineData("?captureMode=&provider=cloro,app_collector")]
    [InlineData("?captureMode=&provider=cloro&provider=APP_COLLECTOR")]
    [InlineData("?captureMode=,&provider=app_collector")]
    [InlineData("?captureMode=&provider=,")]
    public async Task AnEmptyCaptureModeWithAllProvidersOrTheAppProviderIsRefused(string query)
    {
        foreach (var action in new Func<AnalyticsController, Task<IActionResult>>[] { c => c.MonitoringSummary(Site, default), c => c.Overview(Site, default) })
        {
            var calls = new List<(string Method, object?[] Args)>();
            var result = Assert.IsAssignableFrom<ObjectResult>(await action(Controller(query, calls)));
            var error = JsonSerializer.SerializeToElement(result.Value).GetProperty("error");
            Assert.Equal((400, "INVALID_FILTER"), (result.StatusCode, error.GetProperty("code").GetString()));
            Assert.Contains("app_collector", error.GetProperty("message").GetString());
            Assert.Contains(error.GetProperty("message").GetString()!, character => character is >= '一' and <= '鿿');
            Assert.Empty(calls);
        }
    }

    // Every other combination reaches the store with the filter it reached it with before.
    [Theory]
    [InlineData("?captureMode=", null, new string[0])]
    [InlineData("?provider=", new string[0], null)]
    [InlineData("?captureMode=app&provider=app_collector", new[] { "app_collector" }, new[] { "app" })]
    [InlineData("?captureMode=&provider=cloro", new[] { "cloro" }, new string[0])]
    [InlineData("?captureMode=&provider=domestic_collector,doubao_api", new[] { "domestic_collector", "doubao_api" }, new string[0])]
    [InlineData("?captureMode=api&provider=", new string[0], new[] { "api" })]
    [InlineData("", null, null)]
    public async Task OtherSourceCombinationsReachTheStoreUnchanged(string query, string[]? providers, string[]? modes)
    {
        var calls = new List<(string Method, object?[] Args)>();
        await Controller(query, calls).Overview(Site, default);
        var legacy = Assert.IsType<AnalyticsFilterRequest>(Assert.Single(calls).Args[2]);
        Assert.Equal(providers, legacy.Providers?.ToArray());
        Assert.Equal(modes, legacy.CaptureModes?.ToArray());

        // The "all sources" routes fill an omitted key with its defaults, as before.
        calls.Clear();
        await Controller(query, calls).MonitoringSummary(Site, default);
        var all = Assert.IsType<AnalyticsFilterRequest>(Assert.Single(calls).Args[2]);
        Assert.Equal(providers ?? [.. AnalyticsSourceDefaults.Providers], all.Providers!.ToArray());
        Assert.Equal(modes ?? [.. AnalyticsSourceDefaults.CaptureModes], all.CaptureModes!.ToArray());
    }

    // 7.3: every route of the family accepts channel and passes it on; without it the call is the one made before the
    // parameter existed (same overload, same arguments), so a deployment that never sends channel is untouched.
    [Fact]
    public async Task TheCitationRoutesForwardTheChannelAndCallTheOriginalOverloadWithoutIt()
    {
        var none = CancellationToken.None;
        var routes = new (string Query, Func<AnalyticsController, Task<IActionResult>> Call, string Method, object?[] Before, int ChannelIndex)[]
        {
            ("?engine=doubao", c => c.CitationDomainSummary(Site, default), nameof(IAnalyticsQueryStore.CitationDomainSummaryAsync), [], 4),
            ("?snapshotToken=frozen&q=a", c => c.CitationDomainSummaryDomains(Site, default), nameof(IAnalyticsQueryStore.CitationDomainSummaryDomainsAsync),
                [Tenant, Site, "frozen", "a", null, null, null, 20, none], 8),
            ("?snapshotToken=frozen&domain=a.test", c => c.CitationDomainSummaryEvidence(Site, default), nameof(IAnalyticsQueryStore.CitationDomainSummaryEvidenceAsync),
                [Tenant, Site, "frozen", "a.test", null, 20, none], 6),
            ("?snapshotToken=frozen&domain=a.test", c => c.CitationDomainSummaryUrls(Site, default), nameof(IAnalyticsQueryStore.CitationDomainSummaryUrlsAsync),
                [Tenant, Site, "frozen", "a.test", null, null, null, null, 50, none], 9),
            ("?snapshotToken=frozen&domain=a.test", c => c.CitationDomainSummaryDetail(Site, default), nameof(IAnalyticsQueryStore.CitationDomainDetailAsync),
                [Tenant, Site, "frozen", "a.test", null, none], 5),
            ($"?snapshotToken=frozen&urlHash={Hash}", c => c.CitationDomainSummaryUrlPrompts(Site, default), nameof(IAnalyticsQueryStore.CitationUrlPromptsAsync),
                [Tenant, Site, "frozen", Hash, null, none], 5),
        };
        foreach (var (query, call, method, before, channelIndex) in routes)
        {
            var calls = new List<(string Method, object?[] Args)>();
            Assert.Equal(200, Assert.IsType<ObjectResult>(await call(Controller(query, calls))).StatusCode);
            var plain = Assert.Single(calls);
            Assert.Equal(method, plain.Method);
            // One argument fewer than the channel overload: the original signature.
            Assert.Equal(channelIndex + 1, plain.Args.Length);
            if (before.Length > 0) Assert.Equal(before, plain.Args);

            foreach (var channel in new[] { "app", "api", "nonsense" })
            {
                calls.Clear();
                Assert.Equal(200, Assert.IsType<ObjectResult>(await call(Controller($"{query}&channel={channel}", calls))).StatusCode);
                var forwarded = Assert.Single(calls);
                Assert.Equal((method, channelIndex + 2, channel), (forwarded.Method, forwarded.Args.Length, forwarded.Args[channelIndex]));
                // Everything before the channel is what the original call passes.
                Assert.Equal(plain.Args[..channelIndex].Select(Text), forwarded.Args[..channelIndex].Select(Text));
            }
        }
    }

    private static string Text(object? value) => value is AnalyticsFilterRequest request ? JsonSerializer.Serialize(request) : value?.ToString() ?? "∅";

    // 7.3: the summary still does not accept the legacy source overrides; the channel is the only way to choose sources.
    [Theory]
    [InlineData("?provider=app_collector")]
    [InlineData("?captureMode=app")]
    [InlineData("?channel=app&provider=app_collector")]
    [InlineData("?channel=app&captureMode=app")]
    public async Task TheCitationSummaryStillRefusesProviderAndCaptureMode(string query)
    {
        var calls = new List<(string Method, object?[] Args)>();
        var result = Assert.IsAssignableFrom<ObjectResult>(await Controller(query, calls).CitationDomainSummary(Site, default));
        Assert.Equal((400, "INVALID_FILTER"), (result.StatusCode, JsonSerializer.SerializeToElement(result.Value).GetProperty("error").GetProperty("code").GetString()));
        Assert.Empty(calls);
    }

    // 7.3: an invalid channel is refused before any database access.
    [Fact]
    public async Task AnInvalidCitationChannelIsRefusedBeforeAnyDatabaseAccess()
    {
        var store = new AnalyticsQueryStore(new MySqlDatabase("Server=127.0.0.1;Port=1;Database=not_used;ConnectionTimeout=1"), null!);
        Assert.Equal(("INVALID_FILTER", 400), Code(await Assert.ThrowsAsync<CollectorStoreException>(()
            => store.CitationDomainSummaryAsync(Tenant, Site, new AnalyticsFilterRequest(), null, "web"))));
        Assert.Equal(("INVALID_FILTER", 400), Code(await Assert.ThrowsAsync<CollectorStoreException>(()
            => store.CitationDomainSummaryDomainsAsync(Tenant, Site, "frozen-token-0123456789", null, null, null, null, 20, "web"))));
        Assert.Equal(("INVALID_FILTER", 400), Code(await Assert.ThrowsAsync<CollectorStoreException>(()
            => store.CitationDomainSummaryEvidenceAsync(Tenant, Site, "frozen-token-0123456789", "a.test", null, 20, "web"))));
        Assert.Equal(("INVALID_FILTER", 400), Code(await Assert.ThrowsAsync<CollectorStoreException>(()
            => store.CitationDomainSummaryUrlsAsync(Tenant, Site, "frozen-token-0123456789", null, null, null, null, null, 20, "web"))));
        Assert.Equal(("INVALID_FILTER", 400), Code(await Assert.ThrowsAsync<CollectorStoreException>(()
            => store.CitationDomainDetailAsync(Tenant, Site, "frozen-token-0123456789", "a.test", null, "web"))));
        Assert.Equal(("INVALID_FILTER", 400), Code(await Assert.ThrowsAsync<CollectorStoreException>(()
            => store.CitationUrlPromptsAsync(Tenant, Site, "frozen-token-0123456789", Hash, null, "web"))));
    }

    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static CreditUsageRecord Monitoring(string time, string? provider, decimal credits = 10m)
        => new(DateTimeOffset.Parse(time), false, true, credits, MonitoringProvider: provider);

    // 7.5: a monitoring entry names its channel. Without App consumption the entries are the ones returned before, plus the field.
    [Fact]
    public void CreditUsageItemsCarryTheChannelOfPromptMonitoring()
    {
        var (from, to) = (new DateOnly(2026, 11, 16), new DateOnly(2026, 11, 17));
        CreditUsageRecord[] apiOnly =
        [
            Monitoring("2026-11-16T01:00:00Z", "cloro"), Monitoring("2026-11-16T02:00:00Z", "doubao_api"),
            // The check row is gone or was never linked: still API, as before.
            Monitoring("2026-11-16T03:00:00Z", null),
            new(DateTimeOffset.Parse("2026-11-16T04:00:00Z"), true, false, 100m),
            new(DateTimeOffset.Parse("2026-11-17T04:00:00Z"), false, false, 20m, IsArticleMonitoring: true),
        ];
        var before = CreditUsageAggregator.Build(from, to, "UTC", Utc, apiOnly, apiOnly[0]);
        Assert.Equal([new CreditUsageItem(CreditUsageType.ArticleGeneration, 100m, 1), new CreditUsageItem(CreditUsageType.PromptMonitoring, 30m, 3, CollectionChannel.Api)],
            before.Days[0].Items);
        Assert.Equal([new CreditUsageItem(CreditUsageType.ArticleGeneration, 100m, 1), new CreditUsageItem(CreditUsageType.PromptMonitoring, 30m, 3, CollectionChannel.Api),
            new CreditUsageItem(CreditUsageType.ArticleMonitoring, 20m, 1)], before.Totals);
        Assert.Equal(new CreditUsageLatest(CreditUsageType.PromptMonitoring, 10m, apiOnly[0].OccurredAt, CollectionChannel.Api), before.Latest);
        // Same types, credits and counts as the entries had before the field existed.
        Assert.Equal([(CreditUsageType.ArticleGeneration, 100m, 1), (CreditUsageType.PromptMonitoring, 30m, 3), (CreditUsageType.ArticleMonitoring, 20m, 1)],
            before.Totals.Select(item => (item.Type, item.Credits, item.Count)));
        var json = JsonSerializer.SerializeToNode(before, Api)!;
        Assert.Equal(("prompt_monitoring", "api"), (json["totals"]![1]!["type"]!.GetValue<string>(), json["totals"]![1]!["channel"]!.GetValue<string>()));
        Assert.Null(json["totals"]![0]!["channel"]);
        Assert.Equal("api", json["latest"]!["channel"]!.GetValue<string>());

        // App consumption is its own entry of the same type; the totals of the range are the sum of both.
        CreditUsageRecord[] withApp = [.. apiOnly, Monitoring("2026-11-16T05:00:00Z", "app_collector"), Monitoring("2026-11-17T05:00:00Z", "app_collector")];
        var after = CreditUsageAggregator.Build(from, to, "UTC", Utc, withApp, withApp[^1]);
        Assert.Equal([new CreditUsageItem(CreditUsageType.ArticleGeneration, 100m, 1), new CreditUsageItem(CreditUsageType.PromptMonitoring, 30m, 3, CollectionChannel.Api),
            new CreditUsageItem(CreditUsageType.PromptMonitoring, 20m, 2, CollectionChannel.App), new CreditUsageItem(CreditUsageType.ArticleMonitoring, 20m, 1)],
            after.Totals);
        Assert.Equal(before.TotalCredits + 20m, after.TotalCredits);
        Assert.Equal(new CreditUsageLatest(CreditUsageType.PromptMonitoring, 10m, withApp[^1].OccurredAt, CollectionChannel.App), after.Latest);
        // The API entries are exactly the ones of the API-only range.
        Assert.Equal(before.Totals, after.Totals.Where(item => item.Channel != CollectionChannel.App));
        Assert.Equal("app", JsonSerializer.SerializeToNode(after, Api)!["totals"]![2]!["channel"]!.GetValue<string>());

        // Only prompt monitoring has a channel, whatever provider the row happens to name.
        Assert.Null(CreditUsageAggregator.ChannelOf(new(Now, true, true, 1m, MonitoringProvider: "app_collector")));
        Assert.Null(CreditUsageAggregator.ChannelOf(new(Now, false, false, 1m, IsArticleRefine: true)));
        Assert.Equal(CollectionChannel.App, CreditUsageAggregator.ChannelOf(Monitoring("2026-11-17T05:00:00Z", " App_Collector ")));
    }

    // 7.5a: a check that was not collected is finished collection; it is not a failure.
    [Fact]
    public void TheRunSummaryCountsNotCollectedChecksAsFinishedCollection()
    {
        var run = new VisibilityRunSummary(Guid.NewGuid(), Site, "partial_failed", 6, 3, 1, Now, Now, null, null, null, "app_collector", CancelledChecks: 2);
        Assert.Equal((6, 3, 1, 2, 6), (run.ExpectedChecks, run.SucceededChecks, run.FailedChecks, run.CancelledChecks, run.CollectionCompletedChecks));
        var json = JsonSerializer.SerializeToNode(run, Api)!;
        Assert.Equal((2, 6, 1), (json["cancelledChecks"]!.GetValue<int>(), json["collectionCompletedChecks"]!.GetValue<int>(), json["failedChecks"]!.GetValue<int>()));
        // A run without such checks — every API and web run — reports what it reported before.
        var api = new VisibilityRunSummary(Guid.NewGuid(), Site, "running", 6, 3, 1, Now, Now, null, null, null, "cloro");
        Assert.Equal((0, 4), (api.CancelledChecks, api.CollectionCompletedChecks));
    }

    private static CollectionTargetView Target(string provider, string engine, bool enabled = true)
        => new(Guid.NewGuid(), provider, engine, engine, MonitoringProviders.IsApp(provider) ? "app" : "scraped", "observed_surface", "CN", "zh-CN", enabled,
            "available", "full", "full", "full", Channel: MonitoringProviders.Channel(provider));

    private static readonly CollectionTargetView[] MixedTargets =
    [
        Target("app_collector", "doubao"), Target("app_collector", "kimi"), Target("cloro", "chatgpt"), Target("app_collector", "deepseek"),
        Target("doubao_api", "doubao"), Target("domestic_collector", "doubao"), Target("cloro", "gemini", enabled: false), Target("app_collector", "qwen", enabled: false),
    ];

    // 7.6: the team summary offers no App view, so a brand row lists no enabled App target.
    [Fact]
    public async Task TheTeamSummaryDoesNotListEnabledAppTargets()
    {
        var brand = new BrandView(Site, "品牌", null, [], [], "CN", "zh-CN", "Asia/Shanghai", "active", 1, Now, Now);
        AnalyticsFilterRequest? sent = null;
        var management = StudioFakes.Of<IManagementStore>((method, _) => method switch
        {
            nameof(IManagementStore.ListBrandsAsync) => Task.FromResult(new CursorPage<BrandView>([brand], null)),
            nameof(IManagementStore.SummarizePromptsAsync) => Task.FromResult(new PromptSummaryView(2, 2, 0)),
            nameof(IManagementStore.CountPromptGroupsAsync) => Task.FromResult(0),
            nameof(IManagementStore.ListCompetitorsAsync) => Task.FromResult(new CursorPage<CompetitorView>([], null)),
            nameof(IManagementStore.GetSiteTargetsAsync) => Task.FromResult(new SiteTargetsView(1, MixedTargets)),
            _ => throw new InvalidOperationException(method),
        });
        var analytics = StudioFakes.Of<IAnalyticsQueryStore>((method, args) =>
        {
            Assert.Equal(nameof(IAnalyticsQueryStore.BrandPeriodMetricsAsync), method);
            sent = (AnalyticsFilterRequest)args![2]!;
            return Task.FromException<BrandPeriodMetrics>(new CollectorStoreException("ANALYTICS_UNAVAILABLE", "x", 503));
        });
        var content = StudioFakes.Of<IContentStore>((_, _) => Task.FromResult(new ArticleListPage([], null, 0, 0, new Dictionary<string, long>())));
        // Every analytics read of the brand fails here: the row is still built, and only its target list matters to this case.
        var monitoring = StudioFakes.Of<IArticleMonitoringQuery>((_, _)
            => Task.FromException<ArticleMonitoringSummaryView>(new CollectorStoreException("ANALYTICS_UNAVAILABLE", "x", 503)));

        var view = await new TeamSummaryQuery(management, analytics, content, monitoring).SummaryAsync(Tenant, Guid.NewGuid(), new TeamSummaryRequest());
        var targets = Assert.Single(view.Brands).EnabledTargets;
        Assert.Equal(["cloro|chatgpt", "doubao_api|doubao", "domestic_collector|doubao"], targets.Select(target => $"{target.Provider}|{target.Engine}"));
        Assert.DoesNotContain(view.EnabledEngines, target => target.Provider == AppChannelDefinition.Provider);
        // The sources of the metrics are the fixed set, without the App source.
        Assert.Equal(EightProviders, sent!.Providers);
        Assert.Equal(["api", "scraped"], sent.CaptureModes);

        // The App provider is still not a provider the team summary can be filtered by.
        Assert.Equal(("INVALID_FILTER", 400), Code(await Assert.ThrowsAsync<CollectorStoreException>(() => new TeamSummaryQuery(management, analytics, content, monitoring)
            .SummaryAsync(Tenant, Guid.NewGuid(), new TeamSummaryRequest(Provider: "app_collector")))));
    }

    // 7.6: the article studio freezes the first five enabled targets of the brand; App targets do not take those places.
    [Fact]
    public async Task TheArticleStudioFreezesNoAppTarget()
    {
        var prompt = Guid.NewGuid();
        ArticleStudioSubmission? submitted = null;
        var management = StudioFakes.Of<IManagementStore>((method, _) => method switch
        {
            nameof(IManagementStore.GetPromptAsync) => Task.FromResult<PromptView?>(StudioFakes.Prompt(prompt)),
            nameof(IManagementStore.ListCompetitorsAsync) => Task.FromResult(new CursorPage<CompetitorView>([], null)),
            nameof(IManagementStore.GetSiteTargetsAsync) => Task.FromResult(new SiteTargetsView(1, MixedTargets)),
            _ => throw new InvalidOperationException(method),
        });
        var research = StudioFakes.Of<IResearchStore>((method, _) => method == nameof(IResearchStore.GetProviderConfigurationAsync)
            ? Task.FromResult<ResearchProviderConfiguration?>(new ResearchProviderConfiguration(ArticleGenerationProvider.ProviderName, "model",
                "https://api.example.test/chat", "credential", false, 600, 100))
            : throw new InvalidOperationException(method));
        var materials = StudioFakes.Of<IArticleMaterialSelectionService>((_, _) => Task.FromResult(new ArticleMaterialPreviewView(Guid.NewGuid(), null, "hash",
            new ArticleKnowledgeSelection(), [], [], [], 0, 0, DateTimeOffset.UtcNow.AddMinutes(15), true, null, [prompt], "zh-CN")));
        var studio = StudioFakes.Of<IArticleStudioStore>((method, args) =>
        {
            if (method == nameof(IArticleStudioStore.FindStudioSubmissionAsync))
                return Task.FromResult<(ArticleStudioGenerationAccepted, string)?>(null);
            Assert.Equal(nameof(IArticleStudioStore.SubmitStudioGenerationAsync), method);
            submitted = (ArticleStudioSubmission)args![3]!;
            return Task.FromResult(new ArticleStudioGenerationAccepted(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 100m, false,
                ArticleOptimizationGoal.Mention, false, true, "/poll"));
        });
        var analytics = StudioFakes.Of<IAnalyticsQueryStore>((method, args) =>
        {
            Assert.Equal(nameof(IAnalyticsQueryStore.ShareOfVoiceAsync), method);
            var request = (AnalyticsFilterRequest)args![2]!;
            Assert.Equal(EightProviders, request.Providers);
            Assert.Equal(["api", "scraped"], request.CaptureModes);
            return Task.FromResult(StudioFakes.Sov());
        });
        var service = new ArticleStudioService(management, StudioFakes.Unused<ICreditStore>(), research, materials, StudioFakes.Unused<IContentStore>(), studio,
            analytics, StudioFakes.Unused<IKnowledgeRetrievalService>(), clock: new SteppingClock(Now));

        await service.SubmitAsync(Tenant, Site, Guid.NewGuid(), new ArticleStudioGenerationRequest(prompt), "studio-key");
        var frozen = JsonSerializer.Deserialize<JsonElement>(submitted!.TargetSnapshotJson).EnumerateArray()
            .Select(target => $"{target.GetProperty("provider").GetString()}|{target.GetProperty("engine").GetString()}").ToArray();
        // The three enabled non-App targets, in the brand's order; with the App targets counted the first five would have
        // been three App targets and two others.
        Assert.Equal(["cloro|chatgpt", "doubao_api|doubao", "domestic_collector|doubao"], frozen);
        Assert.DoesNotContain(AppChannelDefinition.Provider, submitted.TargetSnapshotJson);
    }
}
