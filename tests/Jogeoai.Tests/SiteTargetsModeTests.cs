using System.Text.Json;
using System.Text.Json.Serialization;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;

namespace Jogeoai.Tests;

/// <summary>站点目标的回答模式 / 采集频率契约（纯函数部分，不访问数据库）：校验错误码、默认模式折叠、目标键与 scope 语义。</summary>
public sealed class SiteTargetsModeTests
{
    private static readonly JsonSerializerOptions Api = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static SiteTargetWrite DeepSeek(AnswerMode? mode = null, MonitoringFrequency frequency = MonitoringFrequency.Daily, bool enabled = true)
        => new("deepseek_api", "deepseek", "api", "CN", "zh-CN", enabled, mode, frequency);

    private static SiteTargetWrite Doubao(AnswerMode? mode = null)
        => new("doubao_api", "doubao", "api", "CN", "zh-CN", true, mode);

    private static SiteTargetWrite Domestic(bool enabled = true, AnswerMode? mode = null)
        => new("domestic_collector", "doubao", "consumer_web", "CN", "zh-CN", enabled, mode);

    private static CollectorStoreException Rejects(SiteTargetsWrite request)
        => Assert.Throws<CollectorStoreException>(() => ManagementStore.ValidateSiteTargetsWrite(request));

    [Fact]
    public void DefaultModesStoreNullAndKeepTheLegacyKeyWhileOtherModesAppendTheModeSegment()
    {
        var legacy = CollectionIdentity.TargetKey("deepseek_api", "deepseek", "api", "CN", "zh-CN");
        foreach (var write in new[] { DeepSeek(), DeepSeek(AnswerMode.Fast) })
        {
            var normalized = ManagementStore.NormalizeTarget(write);
            Assert.Null(normalized.StoredMode);
            Assert.Null(normalized.Write.AnswerMode);
            Assert.Equal(legacy, normalized.Key);
            Assert.True(normalized.Eligible);
        }
        var deep = ManagementStore.NormalizeTarget(DeepSeek(AnswerMode.Deep, MonitoringFrequency.Weekly));
        Assert.Equal(("deep", AnswerMode.Deep, MonitoringFrequency.Weekly), (deep.StoredMode, deep.Write.AnswerMode, deep.Write.Frequency));
        Assert.Equal(CollectionIdentity.TargetKey(new CloroVisibilityTarget("deepseek_api", "deepseek", "zh-CN", "CN", "{}", "deep")), deep.Key);
        Assert.NotEqual(legacy, deep.Key);

        // Doubao defaults to deep (its pre-mode behaviour): deep folds to null, fast is the non-default mode.
        var doubaoLegacy = CollectionIdentity.TargetKey("doubao_api", "doubao", "api", "CN", "zh-CN");
        Assert.Equal((null, doubaoLegacy), (ManagementStore.NormalizeTarget(Doubao(AnswerMode.Deep)).StoredMode, ManagementStore.NormalizeTarget(Doubao(AnswerMode.Deep)).Key));
        Assert.Equal("fast", ManagementStore.NormalizeTarget(Doubao(AnswerMode.Fast)).StoredMode);

        // Domestic web collector rows keep their raw five-part key and are not monitoring-eligible.
        var domestic = ManagementStore.NormalizeTarget(Domestic(mode: AnswerMode.Fast));
        Assert.Equal((null, false), (domestic.StoredMode, domestic.Eligible));
        Assert.Equal(CollectionIdentity.TargetKey("domestic_collector", "doubao", "consumer_web", "CN", "zh-CN"), domestic.Key);
    }

    [Theory]
    [InlineData("cloro", "chatgpt", "scraped", "US", "en-US", AnswerMode.Deep)]
    [InlineData("packy_glm", "zhipu_qingyan", "api", "CN", "zh-CN", AnswerMode.Deep)]
    [InlineData("packy_yuanbao", "tencent_yuanbao", "api", "CN", "zh-CN", AnswerMode.Fast)]
    [InlineData("domestic_collector", "doubao", "consumer_web", "CN", "zh-CN", AnswerMode.Deep)]
    [InlineData("deepseek_api", "deepseek", "api", "CN", "zh-CN", (AnswerMode)7)]
    public void UnsupportedModesAreRejected(string provider, string engine, string capture, string country, string locale, AnswerMode mode)
    {
        var error = Rejects(new SiteTargetsWrite(1, [new SiteTargetWrite(provider, engine, capture, country, locale, true, mode)]));
        Assert.Equal(("ANSWER_MODE_UNSUPPORTED", 400), (error.Code, error.StatusCode));
    }

    [Fact]
    public void UndefinedFrequencyIsRejected()
    {
        var error = Rejects(new SiteTargetsWrite(1, [DeepSeek(frequency: (MonitoringFrequency)9)]));
        Assert.Equal(("FREQUENCY_INVALID", 400), (error.Code, error.StatusCode));
    }

    [Fact]
    public void DuplicatesAreDetectedOnTheModeAwareKey()
    {
        // Omitted and explicit default mode are the same target.
        var error = Rejects(new SiteTargetsWrite(1, [DeepSeek(), DeepSeek(AnswerMode.Fast)]));
        Assert.Equal(("DUPLICATE_TARGET", 400), (error.Code, error.StatusCode));
        Assert.Equal(("DUPLICATE_TARGET", 400), (Rejects(new SiteTargetsWrite(1, [DeepSeek(AnswerMode.Deep), DeepSeek(AnswerMode.Deep)])).Code, 400));
        // Fast and deep of one engine are two targets.
        var both = ManagementStore.ValidateSiteTargetsWrite(new SiteTargetsWrite(1, [DeepSeek(), DeepSeek(AnswerMode.Deep)]));
        Assert.Equal(2, both.Select(t => t.Key).Distinct().Count());
    }

    [Fact]
    public void EnabledMonitoringTargetsAreCappedAtTheMatrixLimit()
    {
        // 21 distinct monitoring targets: 6 Cloro engines × 3 countries + 3 DeepSeek / Qwen cells.
        var cloro = AnswerProviderDefinition.Cloro.Engines
            .SelectMany(engine => new[] { "US", "GB", "DE" }.Select(country => new SiteTargetWrite("cloro", engine, "scraped", country, "en-US")))
            .ToList();
        var targets = cloro.Concat([DeepSeek(), DeepSeek(AnswerMode.Deep), new SiteTargetWrite("packy_qwen", "qwen", "api", "CN", "zh-CN")]).ToList();
        Assert.Equal(21, targets.Count);
        var error = Rejects(new SiteTargetsWrite(1, targets));
        Assert.Equal(("MONITORING_TOO_LARGE", 400), (error.Code, error.StatusCode));
        Assert.Equal(("MONITORING_TOO_LARGE", 400), (Rejects(new SiteTargetsWrite(1, targets, SiteTargetsScope.Monitoring)).Code, 400));

        // A disabled cell and enabled domestic web collector targets do not count.
        targets[^1] = targets[^1] with { Enabled = false };
        targets.Add(Domestic());
        Assert.Equal(22, ManagementStore.ValidateSiteTargetsWrite(new SiteTargetsWrite(1, targets)).Length);
    }

    [Fact]
    public void MonitoringScopeOnlyAcceptsMonitoringTargets()
    {
        var error = Rejects(new SiteTargetsWrite(1, [DeepSeek(), Domestic()], SiteTargetsScope.Monitoring));
        Assert.Equal(("TARGET_NOT_ALLOWED", 400), (error.Code, error.StatusCode));
        Assert.Equal("INVALID_TARGETS", Rejects(new SiteTargetsWrite(1, [DeepSeek()], (SiteTargetsScope)5)).Code);
        Assert.Equal("INVALID_TARGETS", Rejects(new SiteTargetsWrite(1, null!)).Code);
        Assert.Single(ManagementStore.ValidateSiteTargetsWrite(new SiteTargetsWrite(1, [DeepSeek(AnswerMode.Deep)], SiteTargetsScope.Monitoring)));
    }

    [Fact]
    public void OldClientsDeserializeToTheOldBehaviourAndNewEnumsUseStorageNames()
    {
        var legacy = JsonSerializer.Deserialize<SiteTargetsWrite>(
            """{"expectedVersion":3,"targets":[{"provider":"deepseek_api","engine":"deepseek","captureMode":"api","country":"CN","locale":"zh-CN","enabled":true}]}""", Api)!;
        Assert.Equal(SiteTargetsScope.All, legacy.Scope);
        Assert.Equal((null, MonitoringFrequency.Daily), (legacy.Targets[0].AnswerMode, legacy.Targets[0].Frequency));

        var modern = JsonSerializer.Deserialize<SiteTargetsWrite>(
            """{"expectedVersion":3,"scope":"monitoring","targets":[{"provider":"deepseek_api","engine":"deepseek","captureMode":"api","country":"CN","locale":"zh-CN","answerMode":"deep","frequency":"every_other_day"}]}""", Api)!;
        Assert.Equal(SiteTargetsScope.Monitoring, modern.Scope);
        Assert.Equal((AnswerMode.Deep, MonitoringFrequency.EveryOtherDay), (modern.Targets[0].AnswerMode, modern.Targets[0].Frequency));

        // Unknown strings fail model binding (generic 400) before the store; numeric out-of-range values reach the store checks.
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SiteTargetWrite>("""{"provider":"cloro","engine":"chatgpt","frequency":"hourly"}""", Api));

        var view = ManagementStore.ApiProviderCatalogRow(AnswerProviderDefinition.DoubaoApi, "doubao", "available")
            with { AnswerMode = AnswerMode.Fast, Frequency = MonitoringFrequency.TwiceDaily, FrequencyAnchor = new DateOnly(2026, 11, 9) };
        var json = JsonSerializer.Serialize(view, Api);
        Assert.Contains("\"supportedAnswerModes\":[\"fast\",\"deep\"]", json);
        Assert.Contains("\"defaultAnswerMode\":\"deep\"", json);
        Assert.Contains("\"answerMode\":\"fast\"", json);
        Assert.Contains("\"frequency\":\"twice_daily\"", json);
        Assert.Contains("\"frequencyAnchor\":\"2026-11-09\"", json);
    }

    [Fact]
    public void CatalogCapabilitiesFollowTheProviderRegistry()
    {
        foreach (var definition in AnswerProviderDefinition.All)
            foreach (var engine in definition.Engines)
            {
                var row = ManagementStore.ApiProviderCatalogRow(definition, engine, "available");
                Assert.Equal(definition.SupportedAnswerModes, row.SupportedAnswerModes);
                Assert.Equal(definition.DefaultAnswerMode, row.DefaultAnswerMode);
                Assert.Null(row.AnswerMode);
                Assert.Null(row.Frequency);
            }
        var (supported, defaultMode) = ManagementStore.AnswerModeCapabilities("domestic_collector");
        Assert.Equal([AnswerMode.Fast], supported);
        Assert.Equal(AnswerMode.Fast, defaultMode);
        Assert.Equal([AnswerMode.Deep], ManagementStore.AnswerModeCapabilities("packy_yuanbao").Supported);
    }

    [Fact]
    public void MonitoringTargetsFoldTheResolvedDefaultSoPlanHashesStayUnchanged()
    {
        var doubao = ManagementStore.ApiProviderCatalogRow(AnswerProviderDefinition.DoubaoApi, "doubao", "available");
        Assert.Equal(new CloroVisibilityTarget("doubao_api", "doubao", "zh-CN", "CN", "{}"),
            ManagementStore.MonitoringTarget(doubao with { AnswerMode = AnswerMode.Deep }));
        Assert.Equal(new CloroVisibilityTarget("doubao_api", "doubao", "zh-CN", "CN", "{}", "fast"),
            ManagementStore.MonitoringTarget(doubao with { AnswerMode = AnswerMode.Fast }));
        Assert.Equal(new CloroVisibilityTarget("doubao_api", "doubao", "zh-CN", "CN", "{}"), ManagementStore.MonitoringTarget(doubao));
        // The view key and the monitoring target key agree for both modes.
        Assert.Equal(ManagementStore.MonitoringTargetKey("doubao_api", "doubao", "api", "CN", "zh-CN", "fast"),
            CollectionIdentity.TargetKey(ManagementStore.MonitoringTarget(doubao with { AnswerMode = AnswerMode.Fast })));
    }

    [Theory]
    [InlineData("Asia/Shanghai", "2026-11-09T15:59:00Z", "2026-11-09")]
    [InlineData("Asia/Shanghai", "2026-11-09T16:00:00Z", "2026-11-10")]
    [InlineData("America/New_York", "2026-11-09T03:00:00Z", "2026-11-08")]
    [InlineData("not-a-zone", "2026-11-09T16:00:00Z", "2026-11-10")]
    public void AnchorsUseTheBrandLocalDate(string zone, string utc, string expected)
        => Assert.Equal(DateOnly.Parse(expected), ManagementStore.BrandLocalDate(zone, DateTimeOffset.Parse(utc)));
}
