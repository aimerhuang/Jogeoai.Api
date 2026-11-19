using System.Text.Json;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;

namespace Jogeoai.Tests;

/// <summary>按提问的 AI 平台范围：键校验、PATCH 互斥规则、目标视图的 targetKey / monitoringEligible 与契约默认值（无库）。</summary>
public sealed class PromptTargetScopeTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly string ChatGpt = CollectionIdentity.TargetKey(new CloroVisibilityTarget("cloro", "chatgpt", "en-US", "US"));
    private static readonly string Gemini = CollectionIdentity.TargetKey(new CloroVisibilityTarget("cloro", "gemini", "en-US", "US"));
    private static readonly IReadOnlySet<string> Eligible = new HashSet<string>([ChatGpt, Gemini], StringComparer.Ordinal);

    [Fact]
    public void ValidKeysAreReturnedSortedAndCopied()
    {
        var input = new[] { Gemini, ChatGpt };
        var result = PromptMonitoringScale.ValidateTargetKeys(input, Eligible);
        Assert.Equal(new[] { ChatGpt, Gemini }.Order(StringComparer.Ordinal), result);
        Assert.NotSame(input, result);
    }

    public static TheoryData<string[]> InvalidKeySets()
    {
        var data = new TheoryData<string[]>
        {
            Array.Empty<string>(),                                              // 空数组在创建时无效（恢复 all 只用于 PATCH）
            new[] { ChatGpt, ChatGpt },                                         // 重复
            new[] { " " },                                                      // 空白
            new[] { CollectionIdentity.TargetKey("domestic_collector", "doubao", "consumer_web", "CN", "zh-CN") }, // 非每日监控目标
            new[] { CollectionIdentity.TargetKey(new CloroVisibilityTarget("cloro", "copilot", "en-US", "US")) },  // 未启用
            Enumerable.Range(0, 21).Select(i => $"{i:x64}").ToArray(),          // 超过 20 个
        };
        return data;
    }

    [Theory]
    [MemberData(nameof(InvalidKeySets))]
    public void InvalidKeySetsAreRejectedWithPromptTargetInvalid(string[] keys)
    {
        var error = Assert.Throws<CollectorStoreException>(() => PromptMonitoringScale.ValidateTargetKeys(keys, Eligible));
        Assert.Equal(("PROMPT_TARGET_INVALID", 400), (error.Code, error.StatusCode));
    }

    [Fact]
    public async Task ResetTogetherWithNonEmptyKeysIsRejectedBeforeTouchingTheDatabase()
    {
        // Unreachable server: the mutual-exclusion check must fail first.
        var store = new ManagementStore(new MySqlDatabase("Server=127.0.0.1;Port=1;Database=none;User ID=none;Password=none;Connect Timeout=1"));
        var error = await Assert.ThrowsAsync<CollectorStoreException>(() => store.UpdatePromptAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            new PromptPatch(ExpectedVersion: 1, TargetKeys: [ChatGpt], ResetTargetScope: true)));
        Assert.Equal(("PROMPT_TARGET_INVALID", 400), (error.Code, error.StatusCode));
    }

    [Fact]
    public void PatchDistinguishesOmittedEmptyAndResetScopes()
    {
        var omitted = JsonSerializer.Deserialize<PromptPatch>("""{"expectedVersion":3,"content":"x"}""", Web)!;
        Assert.Null(omitted.TargetKeys);
        Assert.Null(omitted.ResetTargetScope);
        var empty = JsonSerializer.Deserialize<PromptPatch>("""{"expectedVersion":3,"targetKeys":[]}""", Web)!;
        Assert.NotNull(empty.TargetKeys);
        Assert.Empty(empty.TargetKeys!);
        Assert.True(JsonSerializer.Deserialize<PromptPatch>("""{"expectedVersion":3,"resetTargetScope":true}""", Web)!.ResetTargetScope);
        var write = JsonSerializer.Deserialize<PromptWrite>($$"""{"content":"q","targetKeys":["{{Gemini}}"]}""", Web)!;
        Assert.Equal([Gemini], write.TargetKeys);
    }

    [Fact]
    public void PromptViewDefaultsToAllWithoutKeysOrSource()
    {
        var view = new PromptView(Guid.NewGuid(), "q", 1, "en-US", true, [], "unbranded", "system", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var json = JsonSerializer.SerializeToElement(view, Web);
        Assert.Equal("all", json.GetProperty("targetScope").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("targetKeys").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("creationSource").ValueKind);
    }

    [Fact]
    public void TargetViewsUseTheSameKeyAsDailyMonitoring()
    {
        // API answer providers use the monitoring key (provider definition capture mode and canonical provider name).
        foreach (var definition in AnswerProviderDefinition.All)
        {
            var engine = definition.Engines[0];
            var row = ManagementStore.ApiProviderCatalogRow(definition, engine, "available");
            Assert.Equal(CollectionIdentity.TargetKey(new CloroVisibilityTarget(definition.Provider, engine, definition.DefaultLocale,
                definition.DefaultCountry)), row.TargetKey);
            Assert.True(row.MonitoringEligible);
            Assert.Equal(row.TargetKey, ManagementStore.MonitoringTargetKey(definition.Provider, engine, definition.CaptureMode,
                definition.DefaultCountry, definition.DefaultLocale));
        }
        // Cloro keys stay byte-identical to the stored site_targets.target_key form.
        Assert.Equal(CollectionIdentity.TargetKey("cloro", "gemini", "scraped", "US", "en-US"),
            ManagementStore.MonitoringTargetKey("cloro", "gemini", "scraped", "US", "en-US"));
        Assert.Equal(CollectionIdentity.TargetKey("domestic_collector", "kimi", "consumer_web", "CN", "zh-CN"),
            ManagementStore.MonitoringTargetKey("domestic_collector", "kimi", "consumer_web", "CN", "zh-CN"));
    }
}
