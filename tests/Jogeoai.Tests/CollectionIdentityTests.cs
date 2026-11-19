using System.Reflection;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;

namespace Jogeoai.Tests;

public sealed class CollectionIdentityTests
{
    [Theory]
    [InlineData("chatgpt", "US", "en-US")]
    [InlineData("chatgpt", null, "zh-CN")]
    [InlineData("perplexity", "US", null)]
    [InlineData("copilot", "GB", "en-GB")]
    [InlineData("gemini", null, null)]
    [InlineData("google-ai-mode", "DE", "de-DE")]
    [InlineData("google-ai-overview", "US", "en-US")]
    public void CloroTargetKeyIsByteIdenticalToTheHistoricalForm(string engine, string? country, string? locale)
    {
        var target = new CloroVisibilityTarget("cloro", engine, locale, country, "{}");
        var expected = CollectionIdentity.TargetKey("cloro", engine, "scraped", country ?? "US", locale);
        Assert.Equal(expected, CollectionIdentity.TargetKey(target));
        // Provider casing is normalized by ValidateAndNormalize; the key must not depend on it either.
        Assert.Equal(expected, CollectionIdentity.TargetKey(target with { Provider = "Cloro" }));
    }

    [Fact]
    public void CloroChatGptKeyMatchesThePreChangeSha256()
    {
        // sha256("cloro|chatgpt|scraped|US|en-US") computed before TargetKey derived captureMode from the registry.
        Assert.Equal("62caa4dc99c4903cf26a7c67eb7f5f10e8e6f8ba58e4d3915ff8daba5c1c6451",
            CollectionIdentity.TargetKey(new CloroVisibilityTarget("cloro", "chatgpt", "en-US", "US")));
        // sha256("cloro|gemini|scraped|US|") — null locale still serializes as an empty segment.
        Assert.Equal("ea58c2e3957968d4a5dc1c6e507531aa9c4ed9a70e218f9f051a4e53e19e9070",
            CollectionIdentity.TargetKey(new CloroVisibilityTarget("cloro", "gemini")));
    }

    [Fact]
    public void DoubaoTargetKeyUsesApiCaptureModeAndChineseDefaults()
    {
        var expected = CollectionIdentity.TargetKey("doubao_api", "doubao", "api", "CN", "zh-CN");
        Assert.Equal(expected, CollectionIdentity.TargetKey(new CloroVisibilityTarget("doubao_api", "doubao", "zh-CN", "CN", "{}")));
        // Country falls back to the provider default (CN), never to the Cloro US default.
        Assert.Equal(expected, CollectionIdentity.TargetKey(new CloroVisibilityTarget("doubao_api", "doubao", "zh-CN")));
        Assert.NotEqual(CollectionIdentity.TargetKey(new CloroVisibilityTarget("cloro", "doubao", "zh-CN", "CN")), expected);
    }

    [Fact]
    public void UnknownProviderKeepsLegacyScrapedShape()
    {
        var expected = CollectionIdentity.TargetKey("domestic_collector", "doubao", "scraped", "US", "zh-CN");
        Assert.Equal(expected, CollectionIdentity.TargetKey(new CloroVisibilityTarget("domestic_collector", "doubao", "zh-CN")));
    }

    [Fact]
    public void RegistryExposesBothProviders()
    {
        Assert.Equal(["cloro", "doubao_api", "deepseek_api", "packy_qwen", "packy_glm", "packy_kimi", "packy_yuanbao"], AnswerProviderDefinition.All.Select(d => d.Provider));
        var cloro = AnswerProviderDefinition.TryGet(" CLORO ")!;
        Assert.Equal(("scraped", "observed_surface", "cloro-v2", "cloro_call", "cloro:", "US", "en-US"),
            (cloro.CaptureMode, cloro.EvidenceClass, cloro.ParserVersion, cloro.UsageEntryType, cloro.UsageReferencePrefix, cloro.DefaultCountry, cloro.DefaultLocale));
        Assert.Equal(["chatgpt", "perplexity", "copilot", "gemini", "google-ai-mode", "google-ai-overview"], cloro.Engines);
        var doubao = AnswerProviderDefinition.TryGet("doubao_api")!;
        Assert.Equal(("api", "api_response", "doubao-api-v1", "doubao_api_call", "doubao:", "CN", "zh-CN"),
            (doubao.CaptureMode, doubao.EvidenceClass, doubao.ParserVersion, doubao.UsageEntryType, doubao.UsageReferencePrefix, doubao.DefaultCountry, doubao.DefaultLocale));
        Assert.Equal(["doubao"], doubao.Engines);
        Assert.True(doubao.SupportsEngine("Doubao"));
        Assert.False(doubao.SupportsEngine("chatgpt"));
        var deepSeek = AnswerProviderDefinition.TryGet("DeepSeek_API")!;
        Assert.Equal(("api", "api_response", "deepseek-api-v1", "deepseek_api_call", "deepseek:", "CN", "zh-CN"),
            (deepSeek.CaptureMode, deepSeek.EvidenceClass, deepSeek.ParserVersion, deepSeek.UsageEntryType, deepSeek.UsageReferencePrefix, deepSeek.DefaultCountry, deepSeek.DefaultLocale));
        Assert.Equal(["deepseek"], deepSeek.Engines);
        // Registry-driven gates: display name, pinned CN/zh-CN surface and the settings-backed "not configured" code.
        Assert.Equal(("Cloro", false, (string?)null), (cloro.DisplayName, cloro.PinnedSurface, cloro.NotConfiguredCode));
        Assert.Equal(("豆包", true, "DOUBAO_NOT_CONFIGURED"), (doubao.DisplayName, doubao.PinnedSurface, doubao.NotConfiguredCode));
        Assert.Equal(("DeepSeek", true, "DEEPSEEK_NOT_CONFIGURED"), (deepSeek.DisplayName, deepSeek.PinnedSurface, deepSeek.NotConfiguredCode));
        Assert.False(AnswerProviderDefinition.IsRegistered("domestic_collector"));
        Assert.False(AnswerProviderDefinition.IsRegistered(null));
        var task = Guid.NewGuid();
        Assert.Equal($"cloro:{task}", cloro.UsageReference(task));
        Assert.Equal($"deepseek:{task}", deepSeek.UsageReference(task));
    }

    [Fact]
    public void DeepSeekTargetKeyUsesTheApiCaptureMode()
    {
        var key = CollectionIdentity.TargetKey(new CloroVisibilityTarget("deepseek_api", "deepseek", "zh-CN", "CN"));
        Assert.Equal(CollectionIdentity.TargetKey("deepseek_api", "deepseek", "api", "CN", "zh-CN"), key);
        Assert.NotEqual(CollectionIdentity.TargetKey("domestic_collector", "deepseek", "consumer_web", "CN", "zh-CN"), key);
    }

    // Fixed inputs whose hashes were computed with the code before per-prompt target scopes (042) existed.
    private static readonly Guid PinSite = Guid.Parse("6f1c2a4e-9b3d-4c5e-8f7a-1b2c3d4e5f60");
    private static readonly Guid PinPromptA = Guid.Parse("11111111-2222-4333-8444-555555555555");
    private static readonly Guid PinPromptB = Guid.Parse("0aaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");
    private static readonly CloroVisibilityTarget[] PinTargets =
    [
        new("cloro", "gemini", "zh-CN", "US", "{}"),
        new("cloro", "chatgpt", "zh-CN", "US", "{}"),
        new("doubao_api", "doubao", "zh-CN", "CN", "{}"),
    ];

    [Fact]
    public void SelectionHashWithoutCustomScopesIsByteIdenticalToThePreScopeValue()
    {
        Assert.Equal("fcd7ef15ab4030b5ec0749b1760c38f0e4c6771e5803446bad732d7f048d8f8d", CollectionIdentity.SelectionHash([PinPromptA, PinPromptB], PinTargets, 1));
        Assert.Equal("ed3dba7860a3676f4ca5ebc5879ed1193a773631853e165db00bbcef80912089", CollectionIdentity.SelectionHash([PinPromptB, PinPromptA], PinTargets.Reverse().ToArray(), 2));
    }

    [Fact]
    public void InitialRunKeyWithoutCustomScopesIsByteIdenticalToThePreScopeValue()
    {
        var created = DateTimeOffset.Parse("2026-11-03T00:00:00Z");
        PromptView Prompt(Guid id, int version) => new(id, "问题", version, "zh-CN", true, [], "unbranded", "system", created, created);
        var selection = new CollectionScheduleSelection([PinPromptA, PinPromptB], PinTargets);
        Assert.Equal("prompt-monitor:6f1c2a4e9b3d4c5e8f7a1b2c3d4e5f60:e22da626d0cbff3454d4394d754f1d33828962c8c3feb9311e77eab948c7a4f7", PromptMonitoringIdentity.InitialRunKey(PinSite, 3, [Prompt(PinPromptA, 2), Prompt(PinPromptB, 1)], selection));
    }

    [Theory]
    [InlineData(1, "da71d55b00576d4ec4c4857f5ab88b118425e775166f2ff32920c8c9862b9b6b")]
    [InlineData(2, "66705c64073761c14d7371dfde8dd489e3d0fc1dbcec0c73f297f7d141c1f03a")]
    public void RunRequestHashWithoutCustomScopesIsByteIdenticalToThePreScopeValue(int samples, string expected)
    {
        var command = new CreateCloroVisibilityRunCommand(Guid.Parse("99999999-8888-4777-8666-555555555555"), PinSite,
            [PinPromptA, PinPromptB], PinTargets, "schedule:pin:20261103T0900", "schedule:pin", samples,
            RequireEnabledTargets: true, ChargeMonitoringCredits: true);
        Assert.Equal(expected, RunRequestHash(command));
    }

    [Fact]
    public void EmptyOrNullScopesKeepTheLegacyHashesAndCustomScopesChangeThem()
    {
        var legacy = new CollectionScheduleSelection([PinPromptA, PinPromptB], PinTargets);
        Assert.Equal(CollectionIdentity.SelectionHash([PinPromptA, PinPromptB], PinTargets, 1), CollectionIdentity.SelectionHash(legacy));
        Assert.Equal(CollectionIdentity.SelectionHash(legacy), CollectionIdentity.SelectionHash(legacy with { PromptTargets = [] }));

        var gemini = CollectionIdentity.TargetKey(PinTargets[0]);
        var chatgpt = CollectionIdentity.TargetKey(PinTargets[1]);
        var scoped = legacy with { PromptTargets = [new(PinPromptA, [gemini, chatgpt])] };
        Assert.NotEqual(CollectionIdentity.SelectionHash(legacy), CollectionIdentity.SelectionHash(scoped));
        // Order-insensitive: prompt order, scope order and key order do not change the hash.
        var reordered = new CollectionScheduleSelection([PinPromptB, PinPromptA], PinTargets.Reverse().ToArray(), 1,
            [new(PinPromptA, [chatgpt, gemini])]);
        Assert.Equal(CollectionIdentity.SelectionHash(scoped), CollectionIdentity.SelectionHash(reordered));
        Assert.NotEqual(CollectionIdentity.SelectionHash(scoped),
            CollectionIdentity.SelectionHash(scoped with { PromptTargets = [new(PinPromptA, [gemini])] }));
    }

    [Fact]
    public void RunRequestHashAddsScopesOnlyWhenCustomScopesExist()
    {
        var command = new CreateCloroVisibilityRunCommand(Guid.Parse("99999999-8888-4777-8666-555555555555"), PinSite,
            [PinPromptA, PinPromptB], PinTargets, "schedule:pin:20261103T0900", "schedule:pin", 1,
            RequireEnabledTargets: true, ChargeMonitoringCredits: true);
        // An empty list normalizes to "no custom scopes" and keeps the pinned pre-042 hash.
        Assert.Equal("da71d55b00576d4ec4c4857f5ab88b118425e775166f2ff32920c8c9862b9b6b",
            RunRequestHash(command with { PromptTargetKeys = [] }));
        var gemini = CollectionIdentity.TargetKey(PinTargets[0]);
        var chatgpt = CollectionIdentity.TargetKey(PinTargets[1]);
        var scoped = RunRequestHash(command with { PromptTargetKeys = [new(PinPromptB, [gemini, chatgpt])] });
        Assert.NotEqual("da71d55b00576d4ec4c4857f5ab88b118425e775166f2ff32920c8c9862b9b6b", scoped);
        Assert.Equal(scoped, RunRequestHash(command with { PromptTargetKeys = [new(PinPromptB, [chatgpt, gemini])] }));
        Assert.NotEqual(scoped, RunRequestHash(command with { PromptTargetKeys = [new(PinPromptB, [gemini])] }));
    }

    public static TheoryData<string, string> RegisteredTargets()
    {
        var data = new TheoryData<string, string>();
        foreach (var definition in AnswerProviderDefinition.All)
            foreach (var engine in definition.Engines) data.Add(definition.Provider, engine);
        return data;
    }

    [Theory]
    [MemberData(nameof(RegisteredTargets))]
    public void DefaultAnswerModeKeyIsByteIdenticalToTheLegacyKey(string provider, string engine)
    {
        var definition = AnswerProviderDefinition.TryGet(provider)!;
        var legacy = CollectionIdentity.TargetKey(provider, engine, definition.CaptureMode, definition.DefaultCountry, definition.DefaultLocale);
        var target = new CloroVisibilityTarget(provider, engine, definition.DefaultLocale, definition.DefaultCountry, "{}");
        Assert.Equal(legacy, CollectionIdentity.TargetKey(target));
        var defaultMode = AnswerModeRules.ToStorage(definition.DefaultAnswerMode);
        // Null, the default mode in any casing/whitespace, and a blank value all collapse to the legacy key.
        foreach (var mode in new[] { defaultMode, defaultMode.ToUpperInvariant(), $" {defaultMode} ", "", "  " })
            Assert.Equal(legacy, CollectionIdentity.TargetKey(target with { AnswerMode = mode }));
        foreach (var other in Enum.GetValues<AnswerMode>().Where(m => m != definition.DefaultAnswerMode))
        {
            var key = CollectionIdentity.TargetKey(target with { AnswerMode = AnswerModeRules.ToStorage(other) });
            Assert.NotEqual(legacy, key);
            Assert.Equal(CollectionIdentity.TargetKey(provider, engine, definition.CaptureMode, definition.DefaultCountry,
                definition.DefaultLocale, AnswerModeRules.ToStorage(other)), key);
        }
    }

    [Fact]
    public void NonDefaultModeKeyAppendsTheModeSegment()
    {
        // sha256("deepseek_api|deepseek|api|CN|zh-CN|deep") — the mode is appended after the locale segment.
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("deepseek_api|deepseek|api|CN|zh-CN|deep"))).ToLowerInvariant();
        Assert.Equal(expected, CollectionIdentity.TargetKey(new CloroVisibilityTarget("deepseek_api", "deepseek", "zh-CN", "CN", "{}", "Deep")));
        // Doubao and Yuanbao default to deep: deep and null share the legacy key, fast gets the segment.
        var doubao = new CloroVisibilityTarget("doubao_api", "doubao", "zh-CN", "CN", "{}");
        Assert.Equal(CollectionIdentity.TargetKey(doubao), CollectionIdentity.TargetKey(doubao with { AnswerMode = "deep" }));
        Assert.Equal(CollectionIdentity.TargetKey("doubao_api", "doubao", "api", "CN", "zh-CN", "fast"),
            CollectionIdentity.TargetKey(doubao with { AnswerMode = "fast" }));
        var yuanbao = new CloroVisibilityTarget("packy_yuanbao", "tencent_yuanbao", "zh-CN", "CN");
        Assert.Equal(CollectionIdentity.TargetKey("packy_yuanbao", "tencent_yuanbao", "api", "CN", "zh-CN"),
            CollectionIdentity.TargetKey(yuanbao with { AnswerMode = "deep" }));
        // The pinned pre-mode Cloro hash is unchanged for fast.
        Assert.Equal("62caa4dc99c4903cf26a7c67eb7f5f10e8e6f8ba58e4d3915ff8daba5c1c6451",
            CollectionIdentity.TargetKey(new CloroVisibilityTarget("cloro", "chatgpt", "en-US", "US", null, "fast")));
        // The 6-argument overload with a null mode equals the 5-argument overload.
        Assert.Equal(CollectionIdentity.TargetKey("cloro", "chatgpt", "scraped", "US", "en-US"),
            CollectionIdentity.TargetKey("cloro", "chatgpt", "scraped", "US", "en-US", null));
    }

    [Fact]
    public void UnknownProviderKeepsTheFallbackAndAppendsAnyMode()
    {
        var legacy = CollectionIdentity.TargetKey("domestic_collector", "doubao", "scraped", "US", "zh-CN");
        var target = new CloroVisibilityTarget("domestic_collector", "doubao", "zh-CN");
        Assert.Equal(legacy, CollectionIdentity.TargetKey(target with { AnswerMode = null }));
        Assert.Equal(CollectionIdentity.TargetKey("domestic_collector", "doubao", "scraped", "US", "zh-CN", "fast"),
            CollectionIdentity.TargetKey(target with { AnswerMode = " FAST " }));
    }

    [Fact]
    public void SelectionAndRequestHashesAreUnchangedForDefaultModeTargets()
    {
        // Default modes written explicitly must not move the pinned pre-mode hashes (normalization folds them to null).
        CloroVisibilityTarget[] explicitDefaults =
        [
            PinTargets[0] with { AnswerMode = "fast" },
            PinTargets[1] with { AnswerMode = "FAST" },
            PinTargets[2] with { AnswerMode = "deep" },
        ];
        Assert.Equal("fcd7ef15ab4030b5ec0749b1760c38f0e4c6771e5803446bad732d7f048d8f8d", CollectionIdentity.SelectionHash([PinPromptA, PinPromptB], explicitDefaults, 1));
        var normalized = explicitDefaults.Select(AnswerModeRules.Normalize).ToArray();
        Assert.Equal(PinTargets, normalized);
        // A null AnswerMode is not serialized, so the run request hash keeps its pre-mode value.
        Assert.DoesNotContain("AnswerMode", System.Text.Json.JsonSerializer.Serialize(PinTargets[0]));
        var command = new CreateCloroVisibilityRunCommand(Guid.Parse("99999999-8888-4777-8666-555555555555"), PinSite,
            [PinPromptA, PinPromptB], normalized, "schedule:pin:20261103T0900", "schedule:pin", 1,
            RequireEnabledTargets: true, ChargeMonitoringCredits: true);
        Assert.Equal("da71d55b00576d4ec4c4857f5ab88b118425e775166f2ff32920c8c9862b9b6b", RunRequestHash(command));

        CloroVisibilityTarget[] withDeep = [.. PinTargets, new("deepseek_api", "deepseek", "zh-CN", "CN", "{}", "deep")];
        Assert.NotEqual(CollectionIdentity.SelectionHash([PinPromptA, PinPromptB], [.. PinTargets, new("deepseek_api", "deepseek", "zh-CN", "CN", "{}")], 1),
            CollectionIdentity.SelectionHash([PinPromptA, PinPromptB], withDeep, 1));
    }

    [Fact]
    public void NormalizationFoldsDefaultsAndRejectsUnsupportedModes()
    {
        var deepSeek = new CloroVisibilityTarget("deepseek_api", "deepseek", "zh-CN", "CN", "{}");
        Assert.True(AnswerModeRules.TryNormalize(deepSeek with { AnswerMode = " Fast " }, out var folded));
        Assert.Null(folded.AnswerMode);
        Assert.True(AnswerModeRules.TryNormalize(deepSeek with { AnswerMode = " DEEP " }, out var deep));
        Assert.Equal("deep", deep.AnswerMode);
        Assert.True(AnswerModeRules.TryNormalize(deepSeek with { AnswerMode = "  " }, out var blank));
        Assert.Null(blank.AnswerMode);
        Assert.False(AnswerModeRules.TryNormalize(deepSeek with { AnswerMode = "turbo" }, out _));
        Assert.False(AnswerModeRules.TryNormalize(new CloroVisibilityTarget("packy_glm", "zhipu_qingyan", AnswerMode: "deep"), out _));
        Assert.False(AnswerModeRules.TryNormalize(new CloroVisibilityTarget("packy_yuanbao", "tencent_yuanbao", AnswerMode: "fast"), out _));
        Assert.False(AnswerModeRules.TryNormalize(new CloroVisibilityTarget("cloro", "chatgpt", AnswerMode: "deep"), out _));
        Assert.True(AnswerModeRules.TryNormalize(new CloroVisibilityTarget("doubao_api", "doubao", AnswerMode: "deep"), out var doubao));
        Assert.Null(doubao.AnswerMode);
        var error = Assert.Throws<CollectorStoreException>(() => AnswerModeRules.Normalize(new CloroVisibilityTarget("cloro", "chatgpt", AnswerMode: "deep")));
        Assert.Equal(("ANSWER_MODE_UNSUPPORTED", 400), (error.Code, error.StatusCode));
    }

    [Fact]
    public void RunNormalizationKeepsTheAnswerModeAndOnlyNonDefaultModesChangeTheRequestHash()
    {
        var command = new CreateCloroVisibilityRunCommand(Guid.Parse("99999999-8888-4777-8666-555555555555"), PinSite,
            [PinPromptA, PinPromptB], PinTargets, "schedule:pin:20261103T0900", "schedule:pin", 1,
            RequireEnabledTargets: true, ChargeMonitoringCredits: true);
        // Default-mode-only runs keep the pinned pre-mode hash, whether the default is omitted or written explicitly
        // (doubao defaults to deep) and in any order.
        Assert.Equal("da71d55b00576d4ec4c4857f5ab88b118425e775166f2ff32920c8c9862b9b6b", RunRequestHash(command));
        Assert.Equal("da71d55b00576d4ec4c4857f5ab88b118425e775166f2ff32920c8c9862b9b6b", RunRequestHash(command with
        {
            Targets = [PinTargets[2] with { AnswerMode = " Deep " }, PinTargets[1] with { AnswerMode = "fast" }, PinTargets[0]],
        }));

        var deepSeek = new CloroVisibilityTarget("deepseek_api", "deepseek", "zh-CN", "CN", "{}");
        var withDefault = RunRequestHash(command with { Targets = [.. PinTargets, deepSeek] });
        Assert.Equal(withDefault, RunRequestHash(command with { Targets = [.. PinTargets, deepSeek with { AnswerMode = "FAST" }] }));
        var withDeep = RunRequestHash(command with { Targets = [.. PinTargets, deepSeek with { AnswerMode = "deep" }] });
        Assert.NotEqual(withDefault, withDeep);
        var both = RunRequestHash(command with { Targets = [.. PinTargets, deepSeek with { AnswerMode = "deep" }, deepSeek] });
        Assert.NotEqual(withDefault, both);
        Assert.NotEqual(withDeep, both);
        // Input order does not matter: the mode is the last sort key (default before deep).
        Assert.Equal(both, RunRequestHash(command with { Targets = [deepSeek, .. PinTargets, deepSeek with { AnswerMode = "deep" }] }));

        var normalized = Normalize(command with { Targets = [deepSeek with { AnswerMode = "deep" }, deepSeek, .. PinTargets] });
        var deepSeekTargets = normalized.Targets.Where(t => t.Provider == "deepseek_api").ToArray();
        Assert.Equal(new string?[] { null, "deep" }, deepSeekTargets.Select(t => t.AnswerMode));
        Assert.NotEqual(CollectionIdentity.TargetKey(deepSeekTargets[0]), CollectionIdentity.TargetKey(deepSeekTargets[1]));
        // Doubao fast is a non-default mode and survives normalization; an explicit deep folds to null.
        var doubao = Normalize(command with { Targets = [PinTargets[2] with { AnswerMode = "fast" }, PinTargets[2]] })
            .Targets.Select(t => t.AnswerMode).ToArray();
        Assert.Equal(new string?[] { null, "fast" }, doubao);
    }

    [Theory]
    [InlineData("cloro", "chatgpt", "US", "en-US", "deep")]
    [InlineData("packy_glm", "zhipu_qingyan", "CN", "zh-CN", "deep")]
    [InlineData("packy_yuanbao", "tencent_yuanbao", "CN", "zh-CN", "fast")]
    [InlineData("deepseek_api", "deepseek", "CN", "zh-CN", "turbo")]
    public void RunNormalizationRejectsUnsupportedAnswerModes(string provider, string engine, string country, string locale, string mode)
    {
        var command = new CreateCloroVisibilityRunCommand(Guid.NewGuid(), PinSite, [PinPromptA],
            [new CloroVisibilityTarget(provider, engine, locale, country, "{}", mode)], "manual:mode-check", "tester");
        var error = Assert.IsType<CollectorStoreException>(Assert.Throws<TargetInvocationException>(() => Normalize(command)).InnerException);
        Assert.Equal(("ANSWER_MODE_UNSUPPORTED", 400), (error.Code, error.StatusCode));
    }

    // ---- App channel (11-16-app-channel-monitoring-api) ----

    public static TheoryData<string> AppPlatforms()
    {
        var data = new TheoryData<string>();
        foreach (var engine in AppChannelDefinition.Engines) data.Add(engine);
        return data;
    }

    [Fact]
    public void AppTargetKeyIsTheShaOfTheFixedFiveSegments()
    {
        // sha256("app_collector|doubao|app|CN|zh-CN") and sha256("app_collector|zhipu_qingyan|app|CN|zh-CN"), computed with shasum.
        Assert.Equal("bd767c92be7accfd978acf8e745a1d6d0eed0ca5c749cc707b6251e063f08bbe", CollectionIdentity.AppTargetKey("doubao"));
        Assert.Equal("9345a80892e06ed57960b6c6ad524e6be56c813748aad784e31be953c3b9fa96", CollectionIdentity.AppTargetKey("zhipu_qingyan"));
        Assert.Equal(CollectionIdentity.AppTargetKey("doubao"), CollectionIdentity.TargetKey("app_collector", "doubao", "app", "CN", "zh-CN"));
        // No mode segment: the 6-argument form with a null mode is the same key, an explicit segment is a different one.
        Assert.Equal(CollectionIdentity.AppTargetKey("doubao"), CollectionIdentity.TargetKey("app_collector", "doubao", "app", "CN", "zh-CN", null));
        Assert.NotEqual(CollectionIdentity.AppTargetKey("doubao"), CollectionIdentity.TargetKey("app_collector", "doubao", "app", "CN", "zh-CN", "fast"));
    }

    [Theory]
    [MemberData(nameof(AppPlatforms))]
    public void AppTargetKeyFromATargetIgnoresCountryLocaleAndMode(string engine)
    {
        var expected = CollectionIdentity.TargetKey("app_collector", engine, "app", "CN", "zh-CN");
        Assert.Equal(expected, CollectionIdentity.AppTargetKey(engine));
        foreach (var provider in new[] { "app_collector", "APP_Collector", "  app_collector  " })
            foreach (var (locale, country) in new (string?, string?)[] { (null, null), ("zh-CN", "CN"), ("en-US", "US"), ("de-DE", null), (null, "JP"), ("", "") })
                foreach (var mode in new string?[] { null, "", "  ", "fast", " FAST ", "deep", "turbo" })
                    Assert.Equal(expected, CollectionIdentity.TargetKey(new CloroVisibilityTarget(provider, engine, locale, country, "{}", mode)));
        // Before the App branch an unregistered provider fell back to "scraped" / "US"; an App target must never get that key.
        Assert.NotEqual(CollectionIdentity.TargetKey("app_collector", engine, "scraped", "US", "zh-CN"),
            CollectionIdentity.TargetKey(new CloroVisibilityTarget("app_collector", engine, "zh-CN")));
    }

    [Fact]
    public void AppTargetKeysDifferFromEveryExistingTargetKey()
    {
        var appKeys = AppChannelDefinition.Engines.ToDictionary(engine => engine, CollectionIdentity.AppTargetKey);
        Assert.Equal(appKeys.Count, appKeys.Values.Distinct(StringComparer.Ordinal).Count());

        var existing = new HashSet<string>(StringComparer.Ordinal);
        var modes = new string?[] { null }.Concat(Enum.GetValues<AnswerMode>().Select(m => (string?)AnswerModeRules.ToStorage(m))).ToArray();
        foreach (var definition in AnswerProviderDefinition.All)
            // Every registered engine, plus every App platform code under that provider (same-platform API targets included).
            foreach (var engine in definition.Engines.Concat(AppChannelDefinition.Engines).Distinct())
                foreach (var mode in modes)
                {
                    existing.Add(CollectionIdentity.TargetKey(new CloroVisibilityTarget(definition.Provider, engine,
                        definition.DefaultLocale, definition.DefaultCountry, "{}", mode)));
                    existing.Add(CollectionIdentity.TargetKey(new CloroVisibilityTarget(definition.Provider, engine, "zh-CN", "CN", "{}", mode)));
                    existing.Add(CollectionIdentity.TargetKey(new CloroVisibilityTarget(definition.Provider, engine, null, null, null, mode)));
                }
        foreach (var engine in AppChannelDefinition.Engines)
        {
            // domestic_collector: the stored site-target key (consumer_web / CN / zh-CN) and the legacy fallback shape.
            existing.Add(CollectionIdentity.TargetKey("domestic_collector", engine, "consumer_web", "CN", "zh-CN"));
            existing.Add(CollectionIdentity.TargetKey(new CloroVisibilityTarget("domestic_collector", engine, "zh-CN")));
            existing.Add(CollectionIdentity.TargetKey(new CloroVisibilityTarget("domestic_collector", engine, "zh-CN", "CN")));
            existing.Add(CollectionIdentity.TargetKey(new CloroVisibilityTarget("domestic_collector", engine, "zh-CN", "CN", "{}", "fast")));
        }
        foreach (var (engine, key) in appKeys) Assert.False(existing.Contains(key), engine);
    }

    [Fact]
    public void AppBranchLeavesEveryOtherProviderOnItsOldKey()
    {
        // The fallback for unregistered providers is untouched: domestic_collector and unknown providers keep "scraped" / "US".
        Assert.Equal(CollectionIdentity.TargetKey("domestic_collector", "doubao", "scraped", "US", "zh-CN"),
            CollectionIdentity.TargetKey(new CloroVisibilityTarget("domestic_collector", "doubao", "zh-CN")));
        Assert.Equal(CollectionIdentity.TargetKey("some_provider", "doubao", "scraped", "JP", "ja-JP", "deep"),
            CollectionIdentity.TargetKey(new CloroVisibilityTarget("some_provider", "doubao", "ja-JP", "JP", null, " Deep ")));
        // A provider that merely contains the App name is not the App provider.
        Assert.Equal(CollectionIdentity.TargetKey("app_collector_v2", "doubao", "scraped", "US", "zh-CN"),
            CollectionIdentity.TargetKey(new CloroVisibilityTarget("app_collector_v2", "doubao", "zh-CN")));
        // Pinned pre-change values, repeated here next to the App branch.
        Assert.Equal("62caa4dc99c4903cf26a7c67eb7f5f10e8e6f8ba58e4d3915ff8daba5c1c6451",
            CollectionIdentity.TargetKey(new CloroVisibilityTarget("cloro", "chatgpt", "en-US", "US")));
        Assert.Equal("ea58c2e3957968d4a5dc1c6e507531aa9c4ed9a70e218f9f051a4e53e19e9070",
            CollectionIdentity.TargetKey(new CloroVisibilityTarget("cloro", "gemini")));
        Assert.Equal("fcd7ef15ab4030b5ec0749b1760c38f0e4c6771e5803446bad732d7f048d8f8d", CollectionIdentity.SelectionHash([PinPromptA, PinPromptB], PinTargets, 1));
        Assert.Equal("ed3dba7860a3676f4ca5ebc5879ed1193a773631853e165db00bbcef80912089", CollectionIdentity.SelectionHash([PinPromptB, PinPromptA], PinTargets.Reverse().ToArray(), 2));
    }

    [Fact]
    public void SelectionHashOfAppTargetsDependsOnlyOnThePlatform()
    {
        CloroVisibilityTarget[] app = [new("app_collector", "doubao", "zh-CN", "CN"), new("app_collector", "kimi", "zh-CN", "CN")];
        var hash = CollectionIdentity.SelectionHash([PinPromptA, PinPromptB], app, 1);
        CloroVisibilityTarget[] sameTargets = [new("App_Collector", "kimi", "en-US", "US", "{}", "fast"), new("app_collector", "doubao")];
        Assert.Equal(hash, CollectionIdentity.SelectionHash([PinPromptB, PinPromptA], sameTargets, 1));
        Assert.NotEqual(hash, CollectionIdentity.SelectionHash([PinPromptA, PinPromptB], [app[0]], 1));
        // Same platforms on the API providers are a different selection.
        CloroVisibilityTarget[] api = [new("doubao_api", "doubao", "zh-CN", "CN"), new("packy_kimi", "kimi", "zh-CN", "CN")];
        Assert.NotEqual(hash, CollectionIdentity.SelectionHash([PinPromptA, PinPromptB], api, 1));
    }

    private static CreateCloroVisibilityRunCommand Normalize(CreateCloroVisibilityRunCommand command)
        => (CreateCloroVisibilityRunCommand)typeof(CloroVisibilityStore)
            .GetMethod("ValidateAndNormalize", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [command])!;

    internal static string RunRequestHash(CreateCloroVisibilityRunCommand command)
    {
        var type = typeof(CloroVisibilityStore);
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        var normalized = type.GetMethod("ValidateAndNormalize", flags)!.Invoke(null, [command]);
        return (string)type.GetMethod("ComputeRequestHash", flags)!.Invoke(null, [normalized])!;
    }
}
