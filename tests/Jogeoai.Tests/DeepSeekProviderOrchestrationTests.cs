using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;

namespace Jogeoai.Tests;

/// <summary>DeepSeek API 接入目录、目标固定 CN/zh-CN、计划时隙网关、错误码白名单与分析默认来源的定向测试；同时锁定豆包文案不变。</summary>
public sealed class DeepSeekProviderOrchestrationTests
{
    // Stores are constructed without a reachable database: every assertion below fails during validation, before a connection opens.
    private static readonly MySqlDatabase Offline = new("Server=127.0.0.1;Port=1;Database=none;User ID=none;Password=none;Connection Timeout=1");

    [Fact]
    public async Task CatalogRowIsUnavailableUntilSettingsAreEnabledWithACredential()
    {
        var definition = AnswerProviderDefinition.DeepSeekApi;
        Assert.Equal("unavailable", await ManagementStore.ResolveApiProviderAvailabilityAsync(null, definition, default));
        Assert.Equal("unavailable", await ManagementStore.ResolveApiProviderAvailabilityAsync(new FakeSettings(), definition, default));
        Assert.Equal("unavailable", await ManagementStore.ResolveApiProviderAvailabilityAsync(new FakeSettings
        {
            Failure = new CollectorStoreException("ANSWER_PROVIDER_SCHEMA_NOT_MIGRATED", "025", 503),
        }, definition, default));
        Assert.Equal("available", await ManagementStore.ResolveApiProviderAvailabilityAsync(new FakeSettings("deepseek_api"), definition, default));

        var row = ManagementStore.ApiProviderCatalogRow(definition, "deepseek", "available");
        Assert.Equal(("deepseek_api", "deepseek", "DeepSeek", "api", "api_response", "CN", "zh-CN"),
            (row.Provider, row.Engine, row.DisplayName, row.CaptureMode, row.EvidenceClass, row.Country, row.Locale));
        Assert.Equal(("supported", "conditional", "conditional"), (row.AnswerCapability, row.CitationCapability, row.SearchQueryCapability));
    }

    [Fact]
    public void MonitoringSelectionIncludesEnabledDeepSeekApiTargetsButNotTheDomesticDeepSeekCollector()
    {
        var cloro = new CollectionTargetView(Guid.NewGuid(), "cloro", "chatgpt", "chatgpt", "scraped", "observed_surface", "US", "zh-CN",
            true, "available", "supported", "conditional", "conditional");
        var deepSeek = cloro with { TargetId = Guid.NewGuid(), Provider = "deepseek_api", Engine = "deepseek", CaptureMode = "api", Country = "CN" };
        var domestic = cloro with { TargetId = Guid.NewGuid(), Provider = "domestic_collector", Engine = "deepseek", CaptureMode = "consumer_web" };
        var selected = PromptMonitoringService.SelectMonitoringTargets([cloro, deepSeek, domestic, deepSeek with { Enabled = false, TargetId = Guid.NewGuid() }]);
        Assert.Equal(new[] { cloro.TargetId, deepSeek.TargetId }, selected.Select(t => t.TargetId));
    }

    [Fact]
    public void DeepSeekErrorCodesAreSafeWithChineseMessagesAndDoubaoMessagesAreUnchanged()
    {
        foreach (var code in DeepSeekErrorCodes.All)
        {
            Assert.Equal(code, CloroVisibilityStore.SafeErrorCode(code));
            var message = CloroVisibilityStore.SafeErrorMessage(code);
            Assert.StartsWith("DeepSeek", message);
            Assert.DoesNotContain("Cloro", message);
        }
        Assert.Equal("DeepSeek服务尚未配置或未启用。", CloroVisibilityStore.SafeErrorMessage(DeepSeekErrorCodes.NotConfigured));
        Assert.Equal("DeepSeek 账户余额不足。", CloroVisibilityStore.SafeErrorMessage(DeepSeekErrorCodes.InsufficientBalance));
        Assert.Equal("INVALID_PROVIDER_RESPONSE", CloroVisibilityStore.SafeErrorCode("DEEPSEEK_SOMETHING_ELSE"));
        Assert.Equal("豆包服务尚未配置或未启用。", CloroVisibilityStore.SafeErrorMessage(DoubaoErrorCodes.NotConfigured));
        Assert.Equal("方舟请求超时。", CloroVisibilityStore.SafeErrorMessage(DoubaoErrorCodes.Timeout));
        Assert.Empty(DeepSeekErrorCodes.All.Intersect(DoubaoErrorCodes.All));
    }

    [Fact]
    public async Task UnconfiguredDeepSeekSkipsTheSlotWithoutReservationAndDoubaoWordingIsUnchanged()
    {
        CloroVisibilityTarget deepSeek = new("deepseek_api", "deepseek", "zh-CN", "CN", "{}"), doubao = new("doubao_api", "doubao", "zh-CN", "CN", "{}");

        var skip = await CollectionScheduleStore.EvaluateProviderAvailabilityAsync([deepSeek], null, new FakeSettings("doubao_api"), default);
        Assert.Equal(("TARGET_UNAVAILABLE", "DeepSeek服务尚未配置或未启用，本时隙未提交，也未预留积分。"), (skip?.Code, skip?.Message));
        var doubaoSkip = await CollectionScheduleStore.EvaluateProviderAvailabilityAsync([doubao, deepSeek], null, new FakeSettings(), default);
        Assert.Equal(("TARGET_UNAVAILABLE", "豆包服务尚未配置或未启用，本时隙未提交，也未预留积分。"), (doubaoSkip?.Code, doubaoSkip?.Message));
        var unmigrated = new FakeSettings("deepseek_api") { Failure = new CollectorStoreException("ANSWER_PROVIDER_SCHEMA_NOT_MIGRATED", "025", 503) };
        Assert.Equal("TARGET_UNAVAILABLE", (await CollectionScheduleStore.EvaluateProviderAvailabilityAsync([deepSeek], null, unmigrated, default))?.Code);

        var both = new FakeSettings("doubao_api", "deepseek_api");
        Assert.Null(await CollectionScheduleStore.EvaluateProviderAvailabilityAsync([doubao, deepSeek], null, both, default));
        // A Doubao-only plan never consults the DeepSeek settings.
        var doubaoOnly = new FakeSettings("doubao_api");
        Assert.Null(await CollectionScheduleStore.EvaluateProviderAvailabilityAsync([doubao], null, doubaoOnly, default));
        Assert.Equal(["doubao_api"], doubaoOnly.Lookups);
        Assert.Null(await CollectionScheduleStore.EvaluateProviderAvailabilityAsync([deepSeek], null, null, default));
    }

    [Theory]
    [InlineData("deepseek_api", "deepseek", "US", "zh-CN", "TARGET_NOT_ALLOWED", "DeepSeek目标仅支持 CN/zh-CN。")]
    [InlineData("deepseek_api", "deepseek", "CN", "en-US", "TARGET_NOT_ALLOWED", "DeepSeek目标仅支持 CN/zh-CN。")]
    [InlineData("deepseek_api", "doubao", "CN", "zh-CN", "UNSUPPORTED_ENGINE", "DeepSeek只支持 deepseek 引擎。")]
    [InlineData("doubao_api", "doubao", "US", "zh-CN", "TARGET_NOT_ALLOWED", "豆包目标仅支持 CN/zh-CN。")]
    [InlineData("doubao_api", "deepseek", "CN", "zh-CN", "UNSUPPORTED_ENGINE", "豆包只支持 doubao 引擎。")]
    [InlineData("cloro", "doubao", "US", "en-US", "UNSUPPORTED_ENGINE", "Cloro 引擎不受支持。")]
    public async Task RunAndScheduleValidationPinTheSurfaceWithRegistryMessages(string provider, string engine, string country, string locale,
        string code, string message)
    {
        var target = new CloroVisibilityTarget(provider, engine, locale, country);
        var run = await Assert.ThrowsAsync<CollectorStoreException>(() => new CloroVisibilityStore(Offline).CreateRunAsync(
            new(Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], [target], "idempotency-fixture-1")));
        Assert.Equal((code, message, 400), (run.Code, run.Message, run.StatusCode));

        var schedule = await Assert.ThrowsAsync<CollectorStoreException>(() => new CollectionScheduleStore(Offline).CreateAsync(
            new(Guid.NewGuid(), Guid.NewGuid(), "fixture", "daily", null, "08:00", "Asia/Shanghai",
                new CollectionScheduleSelection([Guid.NewGuid()], [target]))));
        Assert.Equal((code, message, 400), (schedule.Code, schedule.Message, schedule.StatusCode));
    }

    [Theory]
    [InlineData("deepseek_api", "deepseek", "api", "US", "zh-CN", "DeepSeek目标仅支持 CN/zh-CN。")]
    [InlineData("deepseek_api", "deepseek", "consumer_web", "CN", "zh-CN", "DeepSeek目标只支持 deepseek 引擎与 api 模式。")]
    [InlineData("doubao_api", "doubao", "api", "CN", "en-US", "豆包目标仅支持 CN/zh-CN。")]
    [InlineData("doubao_api", "doubao", "scraped", "CN", "zh-CN", "豆包目标只支持 doubao 引擎与 api 模式。")]
    [InlineData("cloro", "doubao", "scraped", "US", "en-US", "Cloro 目标只支持目录中的引擎与 scraped 模式。")]
    public async Task SiteTargetsAreValidatedAgainstTheRegistry(string provider, string engine, string capture, string country, string locale, string message)
    {
        var exception = await Assert.ThrowsAsync<CollectorStoreException>(() => new ManagementStore(Offline).PutSiteTargetsAsync(
            Guid.NewGuid(), Guid.NewGuid(), new SiteTargetsWrite(1, [new SiteTargetWrite(provider, engine, capture, country, locale)])));
        Assert.Equal(("TARGET_NOT_ALLOWED", message, 400), (exception.Code, exception.Message, exception.StatusCode));
    }

    [Fact]
    public void UsageSummaryKeepsDeepSeekTokenAndSearchCounts()
    {
        var raw = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            id = "msg_1",
            usage = new
            {
                input_tokens = 21691, cache_creation_input_tokens = 0, cache_read_input_tokens = 384, output_tokens = 1428,
                service_tier = "standard", server_tool_use = new { web_search_requests = 3 },
            },
        });
        var summary = CloroVisibilityStore.UsageSummary(raw)!;
        Assert.Equal(21691, summary["input_tokens"]);
        Assert.Equal(384, summary["cache_read_input_tokens"]);
        Assert.Equal(1428, summary["output_tokens"]);
        Assert.Equal(3, summary["server_tool_use.web_search_requests"]);
        Assert.DoesNotContain("service_tier", summary.Keys);
    }

    [Fact]
    public void AnalyticsDefaultsIncludeDeepSeekApiNextToTheDomesticCollector()
    {
        Assert.Equal(new[] { "cloro", "doubao_api", "deepseek_api", "packy_qwen", "packy_glm", "packy_kimi", "packy_yuanbao", "domestic_collector" }, AnalyticsQueryStore.DefaultProviders);
        Assert.All(AnswerProviderDefinition.All, definition => Assert.Contains(definition.Provider, AnalyticsQueryStore.DefaultProviders));
    }

    private sealed class FakeSettings(params string[] configured) : IAnswerProviderSettingsStore
    {
        public CollectorStoreException? Failure { get; init; }
        public List<string> Lookups { get; } = [];
        public Task<AnswerProviderSettingsView?> GetAsync(string provider, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AnswerProviderConfiguration?> GetConfigurationAsync(string provider, CancellationToken cancellationToken = default)
        {
            Lookups.Add(provider);
            Assert.Contains(provider, new[] { "doubao_api", "deepseek_api" });
            if (Failure is not null) throw Failure;
            return Task.FromResult(configured.Contains(provider)
                ? new AnswerProviderConfiguration(provider, "https://api.test/v1", "model", "secret-fixture", 180, new())
                : null);
        }
        public Task<AnswerProviderSettingsView> SaveAsync(string provider, SaveAnswerProviderSettingsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
