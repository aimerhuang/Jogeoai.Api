using Jogeoai.Domain;

namespace Jogeoai.Tests;

/// <summary>哪些 Provider 可以参与定时监控：已注册的 API 回答 Provider 与 App 渠道。</summary>
public sealed class MonitoringProvidersTests
{
    [Theory]
    [InlineData("app_collector")]
    [InlineData("APP_COLLECTOR")]
    [InlineData("App_Collector")]
    [InlineData("  app_collector  ")]
    public void AppProviderIsRecognizedIgnoringCaseAndSurroundingWhitespace(string provider)
    {
        Assert.True(MonitoringProviders.IsApp(provider));
        Assert.True(MonitoringProviders.IsEligible(provider));
        // The App channel is deliberately not a registered API answer provider.
        Assert.False(AnswerProviderDefinition.IsRegistered(provider));
        Assert.Null(AnswerProviderDefinition.TryGet(provider));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("app")]
    [InlineData("consumer_app")]
    [InlineData("app_collector_v2")]
    [InlineData("app collector")]
    [InlineData("app-collector")]
    [InlineData("domestic_collector")]
    [InlineData("unknown")]
    public void OtherValuesAreNeitherAppNorEligible(string? provider)
    {
        Assert.False(MonitoringProviders.IsApp(provider));
        Assert.False(MonitoringProviders.IsEligible(provider));
    }

    [Fact]
    public void EveryRegisteredApiProviderIsEligibleAndNotApp()
    {
        Assert.NotEmpty(AnswerProviderDefinition.All);
        foreach (var definition in AnswerProviderDefinition.All)
            foreach (var provider in new[] { definition.Provider, definition.Provider.ToUpperInvariant(), $" {definition.Provider} " })
            {
                Assert.True(MonitoringProviders.IsEligible(provider), provider);
                Assert.False(MonitoringProviders.IsApp(provider), provider);
            }
    }

    [Fact]
    public void EligibilityIsExactlyRegisteredOrApp()
    {
        string?[] candidates =
        [
            null, "", " ", "cloro", "Cloro", "doubao_api", "deepseek_api", "packy_qwen", "packy_glm", "packy_kimi", "packy_yuanbao",
            "app_collector", " APP_COLLECTOR ", "domestic_collector", "consumer_web", "app", "x",
        ];
        foreach (var provider in candidates)
            Assert.Equal(AnswerProviderDefinition.IsRegistered(provider) || MonitoringProviders.IsApp(provider),
                MonitoringProviders.IsEligible(provider));
        // Tabs and line breaks around the value are trimmed like spaces (same as AnswerProviderDefinition.TryGet).
        Assert.True(MonitoringProviders.IsApp("\tapp_collector\r\n"));
        Assert.True(AnswerProviderDefinition.IsRegistered("\tcloro\r\n"));
        // Domestic web collection stays out of scheduled monitoring.
        Assert.False(MonitoringProviders.IsEligible("domestic_collector"));
    }
}
