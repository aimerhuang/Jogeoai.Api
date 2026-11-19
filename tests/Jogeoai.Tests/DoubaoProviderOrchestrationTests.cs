using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;

namespace Jogeoai.Tests;

/// <summary>豆包接入目录、监控选择、错误码白名单与分析默认 Provider 集合的定向测试。</summary>
public sealed class DoubaoProviderOrchestrationTests
{
    [Fact]
    public async Task CatalogRowIsUnavailableUntilSettingsAreEnabledWithACredential()
    {
        var definition = AnswerProviderDefinition.DoubaoApi;
        Assert.Equal("unavailable", await ManagementStore.ResolveApiProviderAvailabilityAsync(null, definition, default));
        Assert.Equal("unavailable", await ManagementStore.ResolveApiProviderAvailabilityAsync(new FakeSettings(null), definition, default));
        Assert.Equal("unavailable", await ManagementStore.ResolveApiProviderAvailabilityAsync(new FakeSettings(null)
        {
            Failure = new CollectorStoreException("ANSWER_PROVIDER_SCHEMA_NOT_MIGRATED", "025", 503),
        }, definition, default));
        Assert.Equal("available", await ManagementStore.ResolveApiProviderAvailabilityAsync(new FakeSettings(Configuration()), definition, default));

        var row = ManagementStore.ApiProviderCatalogRow(definition, "doubao", "available");
        Assert.Equal(("doubao_api", "doubao", "豆包", "api", "api_response", "CN", "zh-CN"),
            (row.Provider, row.Engine, row.DisplayName, row.CaptureMode, row.EvidenceClass, row.Country, row.Locale));
        Assert.True(row.Enabled);
        Assert.Equal(("supported", "conditional", "conditional"), (row.AnswerCapability, row.CitationCapability, row.SearchQueryCapability));
    }

    [Fact]
    public void MonitoringSelectionIncludesEnabledDoubaoTargetsButNeverDomesticOrDisabledOnes()
    {
        var cloro = new CollectionTargetView(Guid.NewGuid(), "cloro", "chatgpt", "chatgpt", "scraped", "observed_surface", "US", "zh-CN",
            true, "available", "supported", "conditional", "conditional");
        var doubao = cloro with { TargetId = Guid.NewGuid(), Provider = "doubao_api", Engine = "doubao", CaptureMode = "api", Country = "CN" };
        var domestic = cloro with { TargetId = Guid.NewGuid(), Provider = "domestic_collector", Engine = "deepseek", CaptureMode = "consumer_web" };
        var selected = PromptMonitoringService.SelectMonitoringTargets([cloro, doubao, domestic, doubao with { Enabled = false, TargetId = Guid.NewGuid() }]);
        Assert.Equal(new[] { cloro.TargetId, doubao.TargetId }, selected.Select(t => t.TargetId));
    }

    [Fact]
    public void DoubaoErrorCodesAreSafeWithChineseMessages()
    {
        foreach (var code in DoubaoErrorCodes.All)
        {
            Assert.Equal(code, CloroVisibilityStore.SafeErrorCode(code));
            var message = CloroVisibilityStore.SafeErrorMessage(code);
            Assert.NotEqual("Cloro 响应不符合采集数据契约。", message);
            Assert.DoesNotContain("Cloro", message);
        }
        Assert.Equal("豆包服务尚未配置或未启用。", CloroVisibilityStore.SafeErrorMessage(DoubaoErrorCodes.NotConfigured));
        Assert.Equal("问题触发平台内容审核。", CloroVisibilityStore.SafeErrorMessage(DoubaoErrorCodes.InputFiltered));
        Assert.Equal("INVALID_PROVIDER_RESPONSE", CloroVisibilityStore.SafeErrorCode("DOUBAO_SOMETHING_ELSE"));
    }

    [Fact]
    public void UsageSummaryKeepsNumericTokenAndSearchCountsOnly()
    {
        var raw = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            id = "resp_1",
            usage = new
            {
                input_tokens = 12, output_tokens = 34, total_tokens = 46,
                output_tokens_details = new { reasoning_tokens = 20 },
                tool_usage = new { web_search = 2 },
                tool_usage_details = new { web_search = new { doubao = 2 } },
                api_key = "must-not-copy", note = "text", nested = new { deeper = new { deepest = new { too_deep = 1 } } },
            },
        });
        var summary = CloroVisibilityStore.UsageSummary(raw);
        Assert.NotNull(summary);
        Assert.Equal(12, summary!["input_tokens"]);
        Assert.Equal(20, summary["output_tokens_details.reasoning_tokens"]);
        Assert.Equal(2, summary["tool_usage_details.web_search.doubao"]);
        Assert.DoesNotContain("api_key", summary.Keys);
        Assert.DoesNotContain("note", summary.Keys);
        Assert.DoesNotContain("nested.deeper.deepest.too_deep", summary.Keys);
        Assert.Null(CloroVisibilityStore.UsageSummary(System.Text.Json.JsonSerializer.SerializeToElement(new { id = "resp_2" })));
    }

    [Fact]
    public void AnalyticsDefaultProviderPopulationCoversEveryRegisteredProviderAndTheDomesticCollector()
    {
        Assert.Equal(new[] { "cloro", "doubao_api", "deepseek_api", "packy_qwen", "packy_glm", "packy_kimi", "packy_yuanbao", "domestic_collector" }, AnalyticsQueryStore.DefaultProviders);
        Assert.All(AnswerProviderDefinition.All, definition => Assert.Contains(definition.Provider, AnalyticsQueryStore.DefaultProviders));
    }

    private static AnswerProviderConfiguration Configuration() =>
        new("doubao_api", "https://ark.test/api/v3/responses", "model", "secret-fixture", 180, new());

    private sealed class FakeSettings(AnswerProviderConfiguration? configuration) : IAnswerProviderSettingsStore
    {
        public CollectorStoreException? Failure { get; init; }
        public Task<AnswerProviderSettingsView?> GetAsync(string provider, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AnswerProviderConfiguration?> GetConfigurationAsync(string provider, CancellationToken cancellationToken = default)
        {
            Assert.Equal("doubao_api", provider);
            if (Failure is not null) throw Failure;
            return Task.FromResult(configuration);
        }
        public Task<AnswerProviderSettingsView> SaveAsync(string provider, SaveAnswerProviderSettingsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
