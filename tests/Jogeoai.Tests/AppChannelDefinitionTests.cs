using Jogeoai.Domain;

namespace Jogeoai.Tests;

/// <summary>App 渠道的静态定义：身份、固定取值、平台列表；以及它不属于已注册的 API 回答 Provider。</summary>
public sealed partial class AppChannelDefinitionTests
{
    [Fact]
    public void IdentityAndFixedValuesArePinned()
    {
        Assert.Equal("app_collector", AppChannelDefinition.Provider);
        Assert.Equal("app", AppChannelDefinition.CaptureMode);
        Assert.Equal("observed_surface", AppChannelDefinition.EvidenceClass);
        Assert.Equal("CN", AppChannelDefinition.Country);
        Assert.Equal("zh-CN", AppChannelDefinition.Locale);
        Assert.Equal("consumer_app", AppChannelDefinition.CollectorChannel);
        Assert.Equal(AnswerMode.Fast, AppChannelDefinition.DefaultAnswerMode);
        Assert.Equal([AnswerMode.Fast], AppChannelDefinition.SupportedAnswerModes);
        Assert.Equal(MonitoringFrequency.Weekly, AppChannelDefinition.Frequency);
    }

    [Fact]
    public void PlatformsAreTheSevenKnownAppsInMatrixOrder()
    {
        Assert.Equal(
        [
            ("doubao", "豆包"), ("deepseek", "DeepSeek"), ("tencent_yuanbao", "腾讯元宝"), ("qwen", "千问"),
            ("kimi", "Kimi"), ("zhipu_qingyan", "智谱清言"), ("ernie", "文心一言"),
        ], AppChannelDefinition.Platforms.Select(p => (p.Code, p.DisplayName)));
        Assert.Equal(AppChannelDefinition.Platforms.Select(p => p.Code), AppChannelDefinition.Engines);
        Assert.Equal(7, AppChannelDefinition.Engines.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(AppChannelDefinition.Engines, engine => Assert.Equal(engine.Trim().ToLowerInvariant(), engine));
    }

    [Theory]
    [InlineData("doubao", "doubao")]
    [InlineData("DeepSeek", "deepseek")]
    [InlineData(" tencent_yuanbao ", "tencent_yuanbao")]
    [InlineData("QWEN", "qwen")]
    [InlineData("kimi", "kimi")]
    [InlineData("Zhipu_Qingyan", "zhipu_qingyan")]
    [InlineData("ernie", "ernie")] // known platform; that it is currently unavailable comes from the database, not from here
    public void KnownPlatformsAreFoundIgnoringCaseAndWhitespace(string engine, string code)
    {
        Assert.True(AppChannelDefinition.SupportsEngine(engine));
        Assert.Equal(code, AppChannelDefinition.TryGetPlatform(engine)!.Code);
        Assert.Equal($"{code}.consumer_app", AppChannelDefinition.EntryCode(engine));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("chatgpt")]
    [InlineData("gemini")]
    [InlineData("claude")]
    [InlineData("perplexity")]
    [InlineData("doubao.consumer_app")]
    [InlineData("doubao_api")]
    [InlineData("yuanbao")]
    public void OverseasAndUnknownEnginesAreNotAppPlatforms(string? engine)
    {
        Assert.False(AppChannelDefinition.SupportsEngine(engine));
        Assert.Null(AppChannelDefinition.TryGetPlatform(engine));
        Assert.Throws<ArgumentException>(() => AppChannelDefinition.EntryCode(engine!));
    }

    [Fact]
    public void OnlyFastAndWeeklyAreSupported()
    {
        foreach (var mode in Enum.GetValues<AnswerMode>())
            Assert.Equal(mode == AnswerMode.Fast, AppChannelDefinition.SupportsAnswerMode(mode));
        foreach (var frequency in MonitoringFrequencyRules.All)
            Assert.Equal(frequency == MonitoringFrequency.Weekly, AppChannelDefinition.SupportsFrequency(frequency));
        // One App target per platform, once a week.
        Assert.Equal(1m, MonitoringFrequencyRules.WeeklyRuns(AppChannelDefinition.Frequency));
    }

    [Fact]
    public void TheAppChannelIsNotARegisteredApiAnswerProvider()
    {
        Assert.False(AnswerProviderDefinition.IsRegistered("app_collector"));
        Assert.False(AnswerProviderDefinition.IsRegistered(" APP_COLLECTOR "));
        Assert.Null(AnswerProviderDefinition.TryGet(AppChannelDefinition.Provider));
        Assert.DoesNotContain(AnswerProviderDefinition.All, d => d.Provider == AppChannelDefinition.Provider);
        Assert.DoesNotContain(AnswerProviderDefinition.All, d => d.CaptureMode == AppChannelDefinition.CaptureMode);
        // The registry itself is unchanged.
        Assert.Equal(["cloro", "doubao_api", "deepseek_api", "packy_qwen", "packy_glm", "packy_kimi", "packy_yuanbao"],
            AnswerProviderDefinition.All.Select(d => d.Provider));
    }

    [Fact]
    public void EveryAvailableAppPlatformAlsoHasAnApiEngineOfTheSameCode()
    {
        // The matrix shows the App row right below the API row of the same platform; ernie has no API provider.
        var apiEngines = AnswerProviderDefinition.All.SelectMany(d => d.Engines).ToHashSet(StringComparer.Ordinal);
        foreach (var engine in AppChannelDefinition.Engines.Where(e => e != "ernie")) Assert.Contains(engine, apiEngines);
        Assert.DoesNotContain("ernie", apiEngines);
    }
}
