using System.Text.Json;
using System.Text.Json.Serialization;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace Jogeoai.Tests;

/// <summary>
/// App 目标的保存校验（design 3.4 的错误矩阵，逐行）、目录行与渠道推导、回答模式规范化与目录顺序（纯函数部分，不访问数据库）。
/// 上限、团队单元格、预估与手动采集见 AppSiteTargetsTests.Limits.cs。
/// </summary>
public sealed partial class AppSiteTargetsTests
{
    private static readonly JsonSerializerOptions Api = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static SiteTargetWrite App(string engine = "doubao", bool enabled = true, AnswerMode? mode = null,
        MonitoringFrequency frequency = MonitoringFrequency.Weekly, string capture = "app", string country = "CN", string locale = "zh-CN")
        => new("app_collector", engine, capture, country, locale, enabled, mode, frequency);

    private static (string, int) Code(CollectorStoreException exception) => (exception.Code, exception.StatusCode);

    private static CollectorStoreException Rejects(SiteTargetWrite target)
        => Assert.Throws<CollectorStoreException>(() => ManagementStore.NormalizeTarget(target));

    // The catalog's App segment as the store builds it: six usable platforms and ernie unavailable with its seeded reason.
    // alsoErnie keeps ernie unavailable (as seeded) next to another unavailable platform.
    private static IReadOnlyList<CollectionTargetView> AppCatalog(string? unavailable = "ernie", string? reason = AppUnavailableReasons.ContentUnreadable,
        bool alsoErnie = false)
        => AppChannelDefinition.Platforms.Select(platform => ManagementStore.AppCatalogRow(platform,
            platform.Code == unavailable ? new ManagementStore.AppEntryState(false, reason)
            : alsoErnie && platform.Code == "ernie" ? new ManagementStore.AppEntryState(false, AppUnavailableReasons.ContentUnreadable)
            : new ManagementStore.AppEntryState(true, null))).ToArray();

    [Fact]
    public void AValidAppTargetIsNormalizedToItsFixedSurfaceAndKey()
    {
        foreach (var platform in AppChannelDefinition.Platforms)
        {
            var normalized = ManagementStore.NormalizeTarget(App(platform.Code));
            Assert.Equal(new SiteTargetWrite("app_collector", platform.Code, "app", "CN", "zh-CN", true, null, MonitoringFrequency.Weekly), normalized.Write);
            Assert.Null(normalized.StoredMode);
            Assert.Equal(CollectionIdentity.AppTargetKey(platform.Code), normalized.Key);
            Assert.Equal(CollectionIdentity.TargetKey("app_collector", platform.Code, "app", "CN", "zh-CN"), normalized.Key);
            // An App target joins the weekly App plan, never the daily one: it takes none of the 20 daily-monitoring targets.
            Assert.True(normalized.App);
            Assert.False(normalized.Eligible);
        }
        // fast is the only mode and folds away; provider, engine, country and locale are canonicalized before the key is computed.
        var loose = ManagementStore.NormalizeTarget(new SiteTargetWrite(" APP_Collector ", " DouBao ", " APP ", "cn", "zh-cn", false,
            AnswerMode.Fast, MonitoringFrequency.Weekly));
        Assert.Equal(new SiteTargetWrite("app_collector", "doubao", "app", "CN", "zh-CN", false, null, MonitoringFrequency.Weekly), loose.Write);
        Assert.Equal(CollectionIdentity.AppTargetKey("doubao"), loose.Key);
        Assert.Equal(ManagementStore.MonitoringTargetKey("app_collector", "doubao", "app", "CN", "zh-CN"), loose.Key);
        // The App key differs from the API and the web target of the same platform.
        Assert.NotEqual(ManagementStore.NormalizeTarget(new SiteTargetWrite("doubao_api", "doubao", "api", "CN", "zh-CN")).Key, loose.Key);
        Assert.NotEqual(ManagementStore.NormalizeTarget(new SiteTargetWrite("domestic_collector", "doubao", "consumer_web", "CN", "zh-CN")).Key, loose.Key);
    }

    // Matrix row 1: unknown platform, wrong capture mode, wrong country or locale.
    [Theory]
    [InlineData("chatgpt", "app", "CN", "zh-CN")]
    [InlineData("", "app", "CN", "zh-CN")]
    [InlineData("doubao.consumer_app", "app", "CN", "zh-CN")]
    [InlineData("doubao", "api", "CN", "zh-CN")]
    [InlineData("doubao", "consumer_web", "CN", "zh-CN")]
    [InlineData("doubao", "consumer_app", "CN", "zh-CN")]
    [InlineData("doubao", "scraped", "CN", "zh-CN")]
    [InlineData("doubao", "app", "US", "zh-CN")]
    [InlineData("doubao", "app", "CN", "en-US")]
    [InlineData("doubao", "app", "CN", "zh-TW")]
    [InlineData("doubao", "app", "US", "en-US")]
    public void WrongPlatformCaptureModeOrRegionIsNotAllowed(string engine, string capture, string country, string locale)
    {
        var exception = Rejects(App(engine, capture: capture, country: country, locale: locale));
        Assert.Equal(("TARGET_NOT_ALLOWED", 400), Code(exception));
        Assert.Contains("App", exception.Message, StringComparison.Ordinal);
    }

    // Matrix row 1 with the record defaults: an omitted capture mode / country / locale reads as scraped / US / en-US.
    [Fact]
    public void OmittedSurfaceFieldsAreNotRewrittenToTheAppSurface()
    {
        var omitted = JsonSerializer.Deserialize<SiteTargetWrite>("""{"provider":"app_collector","engine":"doubao","frequency":"weekly"}""", Api)!;
        Assert.Equal(("scraped", "US", "en-US"), (omitted.CaptureMode, omitted.Country, omitted.Locale));
        Assert.Equal(("TARGET_NOT_ALLOWED", 400), Code(Rejects(omitted)));
    }

    // Matrix row 2: only fast.
    [Theory]
    [InlineData(AnswerMode.Deep)]
    [InlineData((AnswerMode)7)]
    public void OtherAnswerModesAreUnsupported(AnswerMode mode)
    {
        var exception = Rejects(App(mode: mode));
        Assert.Equal((AnswerModeRules.AnswerModeUnsupported, 400), Code(exception));
        Assert.StartsWith(AppChannelDefinition.DisplayName, exception.Message, StringComparison.Ordinal);
    }

    // Matrix row 3: an explicit non-weekly frequency is rejected, never rewritten — for enabled and disabled targets alike.
    [Theory]
    [InlineData(MonitoringFrequency.Daily)]
    [InlineData(MonitoringFrequency.TwiceDaily)]
    [InlineData(MonitoringFrequency.EveryOtherDay)]
    [InlineData((MonitoringFrequency)9)]
    public void OtherFrequenciesAreInvalid(MonitoringFrequency frequency)
    {
        foreach (var enabled in new[] { true, false })
        {
            var exception = Rejects(App(frequency: frequency, enabled: enabled));
            Assert.Equal((MonitoringFrequencyRules.FrequencyInvalid, 400), Code(exception));
            Assert.Contains("weekly", exception.Message, StringComparison.Ordinal);
        }
    }

    // Matrix row 4 as implemented: SiteTargetWrite.Frequency defaults to daily, so an omitted frequency cannot be told from an
    // explicit daily. Both are rejected (nothing is rewritten); the client sends weekly, as the catalog's fixedFrequency says.
    [Fact]
    public void AnOmittedFrequencyReadsAsDailyAndIsRejectedLikeAnExplicitDaily()
    {
        static SiteTargetWrite Parse(string extra) => JsonSerializer.Deserialize<SiteTargetWrite>(
            "{\"provider\":\"app_collector\",\"engine\":\"doubao\",\"captureMode\":\"app\",\"country\":\"CN\",\"locale\":\"zh-CN\"" + extra + "}", Api)!;
        var omitted = Parse("");
        var daily = Parse(",\"frequency\":\"daily\"");
        Assert.Equal(daily, omitted);
        Assert.Equal((MonitoringFrequencyRules.FrequencyInvalid, 400), Code(Rejects(omitted)));
        var weekly = Parse(",\"frequency\":\"weekly\",\"answerMode\":\"fast\"");
        Assert.Equal(MonitoringFrequency.Weekly, ManagementStore.NormalizeTarget(weekly).Write.Frequency);
    }

    // Matrix rows 5 and 6 need the catalog: unavailable platform → 400 with the reason, no App rows at all → 503 (049 missing).
    [Fact]
    public void CatalogRulesRejectUnavailablePlatformsAndAMissingMigration()
    {
        var catalog = AppCatalog();
        ManagementStore.EnsureAppTargetsAllowed([], [], open: true);
        ManagementStore.EnsureAppTargetsAllowed(AppChannelDefinition.Engines.Where(e => e != "ernie").Select(e => (e, true)), catalog, open: true);
        // A disabled row for an unavailable platform is fine: nothing is being enabled.
        ManagementStore.EnsureAppTargetsAllowed([("ernie", false)], catalog, open: true);

        var unavailable = Assert.Throws<CollectorStoreException>(() => ManagementStore.EnsureAppTargetsAllowed([("doubao", true), ("ernie", true)], catalog, open: true));
        Assert.Equal(("TARGET_NOT_ALLOWED", 400), Code(unavailable));
        Assert.Contains("文心一言", unavailable.Message, StringComparison.Ordinal);
        Assert.Contains("App 内容无法读取", unavailable.Message, StringComparison.Ordinal);
        var rejected = Assert.Throws<CollectorStoreException>(() => ManagementStore.EnsureAppTargetsAllowed([("kimi", true)],
            AppCatalog("kimi", AppUnavailableReasons.EmulatorRejected), open: true));
        Assert.Contains("App 拒绝在模拟器上运行", rejected.Message, StringComparison.Ordinal);
        var noReason = Assert.Throws<CollectorStoreException>(() => ManagementStore.EnsureAppTargetsAllowed([("kimi", true)], AppCatalog("kimi", null), open: true));
        Assert.Equal(("TARGET_NOT_ALLOWED", 400), Code(noReason));

        // A platform without an App entry is simply not in the catalog.
        var missing = Assert.Throws<CollectorStoreException>(() => ManagementStore.EnsureAppTargetsAllowed([("doubao", true)],
            catalog.Where(c => c.Engine != "doubao").ToArray(), open: true));
        Assert.Equal(("TARGET_NOT_ALLOWED", 400), Code(missing));

        // Before 049 the catalog has no App row: only a request that contains an App target gets the 503.
        IReadOnlyList<CollectionTargetView> pre049 =
            [new(null, "cloro", "chatgpt", "chatgpt", "scraped", "observed_surface", "US", "en-US", true, "available", "supported", "conditional", "conditional")];
        foreach (var enabled in new[] { true, false })
        {
            var schema = Assert.Throws<CollectorStoreException>(() => ManagementStore.EnsureAppTargetsAllowed([("doubao", enabled)], pre049, open: true));
            Assert.Equal((AppChannelSchema.NotMigratedCode, 503), Code(schema));
        }
        ManagementStore.EnsureAppTargetsAllowed([], pre049, open: true);
    }

    // 3.9 A (design 6.1): for a tenant the App channel is not open for, every App target is "not in the catalog" — before the
    // 503 of a missing migration and whatever the platform's state is. A request without App targets is not affected.
    [Fact]
    public void ClosedTenantsGetTargetNotAllowedForAnyAppTarget()
    {
        IReadOnlyList<CollectionTargetView> none = [];
        foreach (var catalog in new[] { AppCatalog(), none })
            foreach (var target in new[] { ("doubao", true), ("doubao", false), ("ernie", false), ("unknown", true) })
            {
                var closed = Assert.Throws<CollectorStoreException>(() => ManagementStore.EnsureAppTargetsAllowed([target], catalog, open: false,
                    new HashSet<string>(["doubao", "ernie"])));
                Assert.Equal(("TARGET_NOT_ALLOWED", 400, "目标不在 Provider 目录中。"), (closed.Code, closed.StatusCode, closed.Message));
            }
        ManagementStore.EnsureAppTargetsAllowed([], AppCatalog(), open: false);
        ManagementStore.EnsureAppTargetsAllowed([], none, open: false);
    }

    // 3.9 A: the switch itself. An empty or missing list is closed for everyone; entries that are not GUIDs are ignored.
    [Fact]
    public void AppChannelIsOpenOnlyForListedTenants()
    {
        var tenant = Guid.NewGuid();
        Assert.Equal("Collector:AppTenantIds", AppChannelOptions.TenantIdsKey);
        Assert.Empty(new AppChannelOptions().AppTenantIds);
        Assert.False(new AppChannelOptions().IsOpen(tenant));
        Assert.False(new AppChannelOptions { AppTenantIds = null! }.IsOpen(tenant));
        Assert.False(new AppChannelOptions { AppTenantIds = ["", "not-a-guid", Guid.NewGuid().ToString()] }.IsOpen(tenant));
        var open = new AppChannelOptions { AppTenantIds = ["not-a-guid", tenant.ToString("N").ToUpperInvariant()] };
        Assert.True(open.IsOpen(tenant));
        Assert.False(open.IsOpen(Guid.NewGuid()));
        Assert.False(new AppChannelOptions { AppTenantIds = [Guid.Empty.ToString()] }.IsOpen(Guid.Empty));

        // Bound the way Program.cs binds it: the same array shape as Collector:ChinaTenantIds; a missing key is an empty list.
        static AppChannelOptions Bind(Dictionary<string, string?> values)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            return new AppChannelOptions { AppTenantIds = configuration.GetSection(AppChannelOptions.TenantIdsKey).Get<string[]>() ?? [] };
        }
        Assert.True(Bind(new() { ["Collector:AppTenantIds:0"] = Guid.NewGuid().ToString(), ["Collector:AppTenantIds:1"] = tenant.ToString() }).IsOpen(tenant));
        Assert.False(Bind(new() { ["Collector:ChinaTenantIds:0"] = tenant.ToString() }).IsOpen(tenant));
        Assert.False(Bind(new() { ["Collector:AppTenantIds"] = "" }).IsOpen(tenant));
        Assert.False(Bind([]).IsOpen(tenant));
    }

    // 3.9 B (design 3.4): only newly enabling an unavailable platform is refused; one that was enabled before the save is kept.
    [Fact]
    public void AnAppTargetEnabledBeforeTheSaveSurvivesItsPlatformBecomingUnavailable()
    {
        var catalog = AppCatalog("kimi", AppUnavailableReasons.EmulatorRejected);
        (string, bool)[] request = [("doubao", true), ("kimi", true)];
        Assert.Equal(["kimi"], ManagementStore.UnavailableAppEngines(request, catalog));
        Assert.Empty(ManagementStore.UnavailableAppEngines([("doubao", true), ("kimi", false)], catalog));
        Assert.Empty(ManagementStore.UnavailableAppEngines(request, []));

        ManagementStore.EnsureAppTargetsAllowed(request, catalog, open: true, new HashSet<string>(["kimi"]));
        foreach (var before in new IReadOnlySet<string>?[] { null, new HashSet<string>(), new HashSet<string>(["doubao"]) })
        {
            var refused = Assert.Throws<CollectorStoreException>(() => ManagementStore.EnsureAppTargetsAllowed(request, catalog, open: true, before));
            Assert.Equal(("TARGET_NOT_ALLOWED", 400), Code(refused));
            Assert.Contains("App 拒绝在模拟器上运行", refused.Message, StringComparison.Ordinal);
        }
        // Keeping kimi does not let another unavailable platform in.
        var ernie = Assert.Throws<CollectorStoreException>(() => ManagementStore.EnsureAppTargetsAllowed([.. request, ("ernie", true)],
            AppCatalog("kimi", AppUnavailableReasons.EmulatorRejected, alsoErnie: true), open: true, new HashSet<string>(["kimi"])));
        Assert.Contains("文心一言", ernie.Message, StringComparison.Ordinal);
        // Being enabled before does not bring back a platform that has no App entry at all.
        var missing = Assert.Throws<CollectorStoreException>(() => ManagementStore.EnsureAppTargetsAllowed([("kimi", true)],
            catalog.Where(c => c.Engine != "kimi").ToArray(), open: true, new HashSet<string>(["kimi"])));
        Assert.Equal(("TARGET_NOT_ALLOWED", "目标不在 Provider 目录中。"), (missing.Code, missing.Message));
    }

    [Fact]
    public void CatalogRowsCarryTheAppContractAndTheUnavailableReason()
    {
        var catalog = AppCatalog();
        Assert.Equal(AppChannelDefinition.Engines, catalog.Select(c => c.Engine));
        foreach (var row in catalog)
        {
            Assert.Null(row.TargetId);
            Assert.Equal(("app_collector", "app", "observed_surface", "CN", "zh-CN"), (row.Provider, row.CaptureMode, row.EvidenceClass, row.Country, row.Locale));
            Assert.Equal(AppChannelDefinition.TryGetPlatform(row.Engine)!.DisplayName, row.DisplayName);
            Assert.Equal(CollectionIdentity.AppTargetKey(row.Engine), row.TargetKey);
            Assert.True(row.MonitoringEligible);
            Assert.Equal([AnswerMode.Fast], row.SupportedAnswerModes);
            Assert.Equal(AnswerMode.Fast, row.DefaultAnswerMode);
            Assert.Equal((CollectionChannel.App, MonitoringFrequency.Weekly), (row.Channel, row.FixedFrequency));
            Assert.Null(row.AnswerMode);
            Assert.Null(row.Frequency);
        }
        Assert.All(catalog.Where(c => c.Engine != "ernie"), row => Assert.Equal(("available", true, null), (row.Availability, row.Enabled, row.UnavailableReason)));
        var ernie = catalog.Single(c => c.Engine == "ernie");
        Assert.Equal(("unavailable", false), (ernie.Availability, ernie.Enabled));
        Assert.Equal(new CollectionTargetUnavailableReason("APP_CONTENT_UNREADABLE", "App 内容无法读取"), ernie.UnavailableReason);

        Assert.Equal(new CollectionTargetUnavailableReason("APP_EMULATOR_REJECTED", "App 拒绝在模拟器上运行"),
            ManagementStore.AppUnavailableReason(AppUnavailableReasons.EmulatorRejected));
        Assert.Equal("OPERATOR_NOTE", ManagementStore.AppUnavailableReason("OPERATOR_NOTE")!.Code);
        Assert.Null(ManagementStore.AppUnavailableReason(null));
        Assert.Null(ManagementStore.AppUnavailableReason(""));

        var json = JsonSerializer.SerializeToElement(ernie, Api);
        Assert.Equal("app", json.GetProperty("channel").GetString());
        Assert.Equal("weekly", json.GetProperty("fixedFrequency").GetString());
        Assert.Equal("APP_CONTENT_UNREADABLE", json.GetProperty("unavailableReason").GetProperty("code").GetString());
        Assert.Equal("App 内容无法读取", json.GetProperty("unavailableReason").GetProperty("message").GetString());
        // A consumer that predates the three fields still reads a row, and a row written without them still parses.
        var legacy = JsonSerializer.Deserialize<CollectionTargetView>(
            """{"targetId":null,"provider":"cloro","engine":"chatgpt","displayName":"chatgpt","captureMode":"scraped","evidenceClass":"observed_surface","country":"US","locale":"en-US","enabled":true,"availability":"available","answerCapability":"supported","citationCapability":"conditional","searchQueryCapability":"conditional"}""", Api)!;
        Assert.Equal((null, null, null), (legacy.Channel, legacy.FixedFrequency, legacy.UnavailableReason));
    }

    // The channel comes from the provider, never from the capture mode (domestic web answers are stored as scraped, like Cloro).
    [Fact]
    public void ChannelIsDerivedFromTheProviderOnly()
    {
        foreach (var definition in AnswerProviderDefinition.All)
            Assert.Equal(CollectionChannel.Api, MonitoringProviders.Channel(definition.Provider));
        Assert.Equal(CollectionChannel.Api, MonitoringProviders.Channel(" CLORO "));
        Assert.Equal(CollectionChannel.Web, MonitoringProviders.Channel("domestic_collector"));
        Assert.Equal(CollectionChannel.Web, MonitoringProviders.Channel(" Domestic_Collector "));
        Assert.Equal(CollectionChannel.App, MonitoringProviders.Channel("app_collector"));
        Assert.Equal(CollectionChannel.App, MonitoringProviders.Channel(" APP_COLLECTOR "));

        CollectionTargetView Row(string provider, string capture, Guid? id = null)
            => new(id, provider, "doubao", "豆包", capture, "observed_surface", "CN", "zh-CN", true, "available", "supported", "conditional", "unknown");
        foreach (var capture in new[] { "scraped", "api", "app", "consumer_web" })
        {
            Assert.Equal(CollectionChannel.Web, ManagementStore.WithChannel(Row("domestic_collector", capture)).Channel);
            Assert.Equal(CollectionChannel.Api, ManagementStore.WithChannel(Row("cloro", capture)).Channel);
            Assert.Equal(CollectionChannel.Api, ManagementStore.WithChannel(Row("doubao_api", capture)).Channel);
            Assert.Equal(CollectionChannel.App, ManagementStore.WithChannel(Row("app_collector", capture)).Channel);
        }
        // Rows of the other channels only gain the channel: every existing field keeps its value.
        var web = Row("domestic_collector", "consumer_web", Guid.NewGuid());
        Assert.Equal(web with { Channel = CollectionChannel.Web }, ManagementStore.WithChannel(web));
        var api = Row("doubao_api", "api", Guid.NewGuid());
        Assert.Equal(api with { Channel = CollectionChannel.Api }, ManagementStore.WithChannel(api));
    }
}
