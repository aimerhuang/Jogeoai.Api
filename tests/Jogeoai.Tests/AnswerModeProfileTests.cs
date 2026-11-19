using Jogeoai.Domain;

namespace Jogeoai.Tests;

/// <summary>回答模式能力表与思考参数覆盖：默认模式必须原样返回本机全局配置（2026-11-09 只读查询的基线）。</summary>
public sealed class AnswerModeProfileTests
{
    // Local jogeoai answer_provider_settings.options as read on 2026-11-09 (only the fields the profile may touch matter).
    private static AnswerProviderOptions Baseline(string provider) => provider switch
    {
        "deepseek_api" => new() { RequestMode = "web_search", Sources = [], MaxToolCalls = 5, Thinking = "disabled" },
        "packy_qwen" or "packy_kimi" or "packy_yuanbao" => new() { RequestMode = "web_search", Sources = [], MaxToolCalls = provider == "packy_kimi" ? 1 : 3, Thinking = "disabled" },
        "packy_glm" => new() { RequestMode = "web_search", Sources = [], MaxToolCalls = 3, Thinking = "disabled", ReasoningEffort = "low" },
        "doubao_api" => new() { RequestMode = "messages", Sources = ["doubao"], MaxToolCalls = 3 },
        "cloro" => new(),
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    private static AnswerProviderDefinition Def(string provider) => AnswerProviderDefinition.TryGet(provider)!;

    [Fact]
    public void AnswerProviderDefinitionDeclaresTheCapabilityTable()
    {
        var expected = new Dictionary<string, (AnswerMode[] Supported, AnswerMode Default)>
        {
            ["cloro"] = ([AnswerMode.Fast], AnswerMode.Fast),
            ["doubao_api"] = ([AnswerMode.Fast, AnswerMode.Deep], AnswerMode.Deep),
            ["deepseek_api"] = ([AnswerMode.Fast, AnswerMode.Deep], AnswerMode.Fast),
            ["packy_qwen"] = ([AnswerMode.Fast, AnswerMode.Deep], AnswerMode.Fast),
            ["packy_glm"] = ([AnswerMode.Fast], AnswerMode.Fast),
            ["packy_kimi"] = ([AnswerMode.Fast, AnswerMode.Deep], AnswerMode.Fast),
            ["packy_yuanbao"] = ([AnswerMode.Deep], AnswerMode.Deep),
        };
        Assert.Equal(expected.Keys.Order(), AnswerProviderDefinition.All.Select(d => d.Provider).Order());
        foreach (var definition in AnswerProviderDefinition.All)
        {
            var (supported, defaultMode) = expected[definition.Provider];
            Assert.Equal(supported, definition.SupportedAnswerModes);
            // DefaultAnswerMode is frozen: default-mode target keys omit the mode segment.
            Assert.Equal(defaultMode, definition.DefaultAnswerMode);
            Assert.Contains(definition.DefaultAnswerMode, definition.SupportedAnswerModes);
        }
    }

    [Fact]
    public void ModeStringsAreStableLowercase()
    {
        Assert.Equal("fast", AnswerModeRules.ToStorage(AnswerMode.Fast));
        Assert.Equal("deep", AnswerModeRules.ToStorage(AnswerMode.Deep));
        Assert.True(AnswerModeRules.TryParse(" Deep ", out var deep));
        Assert.Equal(AnswerMode.Deep, deep);
        Assert.False(AnswerModeRules.TryParse(null, out _));
        Assert.False(AnswerModeRules.TryParse("thinking", out _));
        // Wire names follow the JsonStringEnumMemberName convention used by the API's JsonStringEnumConverter.
        var json = new System.Text.Json.JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
        Assert.Equal("[\"fast\",\"deep\"]", System.Text.Json.JsonSerializer.Serialize(new[] { AnswerMode.Fast, AnswerMode.Deep }, json));
    }

    [Theory]
    [InlineData("cloro")]
    [InlineData("deepseek_api")]
    [InlineData("packy_qwen")]
    [InlineData("packy_kimi")]
    [InlineData("packy_glm")]
    [InlineData("doubao_api")]
    [InlineData("packy_yuanbao")]
    public void DefaultModeReturnsTheLocalBaselineUnchanged(string provider)
    {
        var definition = Def(provider);
        var baseline = Baseline(provider);
        var applied = AnswerModeProfile.Apply(definition, baseline, definition.DefaultAnswerMode);
        Assert.Equal(baseline, applied);
        Assert.Equal(baseline.ToJson(), applied.ToJson());
        // A null/blank mode string resolves to the default mode.
        Assert.Equal(baseline.ToJson(), AnswerModeProfile.Apply(definition, baseline, (string?)null).ToJson());
        Assert.Equal(baseline.ToJson(), AnswerModeProfile.Apply(definition, baseline, " ").ToJson());
    }

    [Theory]
    [InlineData("deepseek_api")]
    [InlineData("packy_qwen")]
    [InlineData("packy_kimi")]
    public void ThinkingSwitchProvidersMapFastAndDeep(string provider)
    {
        var definition = Def(provider);
        var baseline = Baseline(provider);
        var deep = AnswerModeProfile.Apply(definition, baseline, AnswerMode.Deep);
        Assert.Equal(baseline with { Thinking = "enabled" }, deep);
        Assert.Null(deep.ReasoningEffort);
        Assert.Equal((baseline.RequestMode, baseline.MaxToolCalls), (deep.RequestMode, deep.MaxToolCalls));
        // Fast always sends disabled, even if the global switch was flipped on.
        var fast = AnswerModeProfile.Apply(definition, baseline with { Thinking = "enabled" }, "FAST");
        Assert.Equal("disabled", fast.Thinking);
        Assert.Null(fast.ReasoningEffort);
    }

    [Fact]
    public void GlmFastPinsDisabledAndLowEffort()
    {
        var applied = AnswerModeProfile.Apply(Def("packy_glm"), Baseline("packy_glm") with { Thinking = "enabled", ReasoningEffort = "high" }, AnswerMode.Fast);
        Assert.Equal(Baseline("packy_glm"), applied);
    }

    [Fact]
    public void DoubaoMessagesModeMapsFastToDisabledAndDeepToModelDefault()
    {
        var definition = Def("doubao_api");
        var baseline = Baseline("doubao_api");
        var fast = AnswerModeProfile.Apply(definition, baseline, AnswerMode.Fast);
        Assert.Equal(baseline with { Thinking = "disabled" }, fast);
        Assert.Null(fast.ReasoningEffort);
        var deep = AnswerModeProfile.Apply(definition, baseline with { Thinking = "disabled" }, AnswerMode.Deep);
        Assert.Null(deep.Thinking);
        Assert.Equal(baseline.ToJson(), deep.ToJson());
    }

    [Fact]
    public void DoubaoNonMessagesModesOnlySupportTheExistingDeepBehaviour()
    {
        var definition = Def("doubao_api");
        var webSearch = new AnswerProviderOptions { RequestMode = "web_search", Sources = ["doubao"], ReasoningEffort = "medium" };
        Assert.Same(webSearch, AnswerModeProfile.Apply(definition, webSearch, AnswerMode.Deep));
        Assert.False(AnswerModeProfile.TryApply(definition, webSearch, AnswerMode.Fast, out var unchanged));
        Assert.Same(webSearch, unchanged);
        var error = Assert.Throws<CollectorStoreException>(() => AnswerModeProfile.Apply(definition, webSearch, AnswerMode.Fast));
        Assert.Equal(("ANSWER_MODE_UNSUPPORTED", 400), (error.Code, error.StatusCode));
    }

    [Theory]
    [InlineData("packy_glm", "deep")]
    [InlineData("packy_yuanbao", "fast")]
    [InlineData("cloro", "deep")]
    [InlineData("deepseek_api", "turbo")]
    public void UnsupportedModesFail(string provider, string mode)
    {
        var definition = Def(provider);
        var baseline = Baseline(provider);
        var error = Assert.Throws<CollectorStoreException>(() => AnswerModeProfile.Apply(definition, baseline, mode));
        Assert.Equal(("ANSWER_MODE_UNSUPPORTED", 400), (error.Code, error.StatusCode));
        if (AnswerModeRules.TryParse(mode, out var parsed))
        {
            Assert.False(AnswerModeProfile.TryApply(definition, baseline, parsed, out var unchanged));
            Assert.Same(baseline, unchanged);
        }
    }
}
