using Jogeoai.Domain;
using Jogeoai.Infrastructure;

namespace Jogeoai.Tests;

/// <summary>App 目标与上限（只数 API）、scope、团队单元格、目录顺序、回答模式规范化（无库）。</summary>
public sealed partial class AppSiteTargetsTests
{
    private static readonly SiteTargetWrite[] AvailableApps =
        AppChannelDefinition.Engines.Where(engine => engine != "ernie").Select(engine => App(engine)).ToArray();

    // Distinct daily-monitoring targets: the Cloro engines in several regions.
    private static SiteTargetWrite[] ApiTargets(int count, bool enabled = true)
        => (from region in new[] { ("US", "en-US"), ("GB", "en-GB"), ("DE", "de-DE"), ("JP", "ja-JP"), ("FR", "fr-FR"), ("CA", "en-CA") }
            from engine in AnswerProviderDefinition.Cloro.Engines
            select new SiteTargetWrite("cloro", engine, "scraped", region.Item1, region.Item2, enabled)).Take(count).ToArray();

    private static CollectorStoreException RejectsWrite(IEnumerable<SiteTargetWrite> targets, SiteTargetsScope scope = SiteTargetsScope.All)
        => Assert.Throws<CollectorStoreException>(() => ManagementStore.ValidateSiteTargetsWrite(new SiteTargetsWrite(1, targets.ToArray(), scope)));

    [Fact]
    public void TheTwentyTargetLimitCountsApiTargetsOnly()
    {
        Assert.Equal(6, AvailableApps.Length);
        foreach (var scope in new[] { SiteTargetsScope.All, SiteTargetsScope.Monitoring })
        {
            var normalized = ManagementStore.ValidateSiteTargetsWrite(new SiteTargetsWrite(1, [.. ApiTargets(20), .. AvailableApps], scope));
            Assert.Equal((20, 6), (normalized.Count(t => t.Eligible), normalized.Count(t => t.App)));
            // The 21st API target is still rejected with the existing code and message, with or without App targets.
            foreach (var targets in new[] { ApiTargets(21), [.. ApiTargets(21), .. AvailableApps] })
            {
                var tooLarge = RejectsWrite(targets, scope);
                Assert.Equal(("MONITORING_TOO_LARGE", 400), Code(tooLarge));
                Assert.Equal("每日监控最多启用 20 个目标（同一模型的两种回答模式计为 2 个）。", tooLarge.Message);
            }
        }
        // The estimate does not throw for the 21st target; it stays a blocker of the API part.
        Assert.Equal(27, ManagementStore.ValidateSiteTargetsWrite(new SiteTargetsWrite(0, [.. ApiTargets(21), .. AvailableApps]), false).Length);
    }

    [Fact]
    public void TheRequestCapIsThirtyOtherTargetsPlusOneAppTargetPerPlatform()
    {
        var allApps = AppChannelDefinition.Engines.Select(engine => App(engine, enabled: false)).ToArray();
        Assert.Equal(7, allApps.Length);
        // 30 non-App targets (20 enabled) and every App platform fit in one request.
        Assert.Equal(37, ManagementStore.ValidateSiteTargetsWrite(new SiteTargetsWrite(1, [.. ApiTargets(20), .. ApiTargets(30, false)[20..], .. allApps])).Length);
        foreach (var targets in new[] { ApiTargets(31, false), [.. ApiTargets(31, false), .. allApps] })
        {
            var tooMany = RejectsWrite(targets);
            Assert.Equal(("INVALID_TARGETS", 400), Code(tooMany));
            Assert.Equal("目标最多 30 个。", tooMany.Message);
        }
        Assert.Equal(("INVALID_TARGETS", 400), Code(RejectsWrite([.. allApps, App("doubao")])));
        Assert.Equal(("DUPLICATE_TARGET", 400), Code(RejectsWrite([App("doubao"), App(" DOUBAO ", enabled: false)])));
        Assert.Equal(("INVALID_TARGETS", 400), Code(Assert.Throws<CollectorStoreException>(() => ManagementStore.ValidateSiteTargetsWrite(new SiteTargetsWrite(1, null!)))));
    }

    [Fact]
    public void MonitoringScopeAcceptsAppTargetsAndStillRejectsWebCollectorTargets()
    {
        var web = new SiteTargetWrite("domestic_collector", "doubao", "consumer_web", "CN", "zh-CN");
        Assert.Equal(7, ManagementStore.ValidateSiteTargetsWrite(new SiteTargetsWrite(1, [.. ApiTargets(1), .. AvailableApps], SiteTargetsScope.Monitoring)).Length);
        Assert.Single(ManagementStore.ValidateSiteTargetsWrite(new SiteTargetsWrite(1, [App()], SiteTargetsScope.Monitoring)));
        Assert.Equal(("TARGET_NOT_ALLOWED", 400), Code(RejectsWrite([App(), web], SiteTargetsScope.Monitoring)));
        Assert.Equal(2, ManagementStore.ValidateSiteTargetsWrite(new SiteTargetsWrite(1, [App(), web])).Length);
    }

    [Fact]
    public void TeamCellsAcceptAppCellsOutsideTheTwentyCellLimit()
    {
        var api = AnswerProviderDefinition.All.SelectMany(d => d.Engines.SelectMany(e => d.SupportedAnswerModes.Select(m => new TeamMonitoringCellWrite(d.Provider, e, m)))).ToArray();
        var apps = AvailableApps.Select(a => new TeamMonitoringCellWrite("app_collector", a.Engine, null, MonitoringFrequency.Weekly)).ToArray();
        var cells = ManagementStore.ValidateTeamCells([.. apps, .. api]);
        Assert.Equal(api.Length + 6, cells.Length);
        Assert.True(cells.Length > MonitoringMatrix.MaxTargets);
        var app = cells.Where(c => c.Provider == "app_collector").ToArray();
        Assert.All(app, c => Assert.Equal((null, AnswerMode.Fast, MonitoringFrequency.Weekly), (c.StoredMode, c.Mode, c.Frequency)));
        Assert.Equal("app_collector|doubao||weekly", app[0].Signature);
        Assert.Equal("app_collector|kimi||weekly", Assert.Single(ManagementStore.ValidateTeamCells([new(" APP_COLLECTOR ", " Kimi ", AnswerMode.Fast, MonitoringFrequency.Weekly)])).Signature);

        // 21 API cells are still too many (the count is checked before duplicates), with or without App cells.
        var twentyOne = Enumerable.Repeat(new TeamMonitoringCellWrite("cloro", "chatgpt"), 21).ToArray();
        foreach (var draft in new[] { twentyOne, [.. twentyOne, .. apps] })
        {
            var tooLarge = Assert.Throws<CollectorStoreException>(() => ManagementStore.ValidateTeamCells(draft));
            Assert.Equal(("MONITORING_TOO_LARGE", 400), Code(tooLarge));
            Assert.Equal("团队默认监控配置最多 20 个单元格（同一模型的两种回答模式计为 2 个）。", tooLarge.Message);
        }
        (TeamMonitoringCellWrite Cell, string Code)[] invalid =
        [
            (new("app_collector", "doubao"), MonitoringFrequencyRules.FrequencyInvalid),
            (new("app_collector", "doubao", null, MonitoringFrequency.TwiceDaily), MonitoringFrequencyRules.FrequencyInvalid),
            (new("app_collector", "doubao", AnswerMode.Deep, MonitoringFrequency.Weekly), AnswerModeRules.AnswerModeUnsupported),
            (new("app_collector", "chatgpt", null, MonitoringFrequency.Weekly), "TARGET_NOT_ALLOWED"),
            (new("domestic_collector", "doubao"), "TARGET_NOT_ALLOWED"),
        ];
        foreach (var (cell, code) in invalid)
            Assert.Equal((code, 400), Code(Assert.Throws<CollectorStoreException>(() => ManagementStore.ValidateTeamCells([cell]))));
        Assert.Equal(("DUPLICATE_TARGET", 400), Code(Assert.Throws<CollectorStoreException>(() => ManagementStore.ValidateTeamCells([apps[0], apps[0]]))));
        Assert.Equal(("INVALID_TARGETS", 400), Code(Assert.Throws<CollectorStoreException>(() =>
            ManagementStore.ValidateTeamCells(Enumerable.Repeat(apps[0], 8).ToArray()))));
    }

    [Fact]
    public void AppCellsMaterializeOnTheFixedSurfaceAndSkipUnavailablePlatforms()
    {
        var cells = ManagementStore.ValidateTeamCells([
            new("cloro", "chatgpt"), new("app_collector", "doubao", null, MonitoringFrequency.Weekly),
            new("app_collector", "ernie", null, MonitoringFrequency.Weekly)]);
        // The brand's own region and locale (and an existing API row of the same platform) never reach an App target.
        var existing = new ManagementStore.ExistingTargetRow[] { new("doubao_api", "doubao", "CN", "zh-CN", true), new("cloro", "chatgpt", "JP", "ja-JP", true) };
        var targets = ManagementStore.MaterializeCells(cells, existing, "en-GB", null);
        Assert.Equal(new SiteTargetWrite("app_collector", "doubao", "app", "CN", "zh-CN", true, AnswerMode.Fast, MonitoringFrequency.Weekly), targets[1]);
        Assert.Equal(new SiteTargetWrite("app_collector", "ernie", "app", "CN", "zh-CN", true, AnswerMode.Fast, MonitoringFrequency.Weekly), targets[2]);
        Assert.Equal(("JP", "ja-JP"), (targets[0].Country, targets[0].Locale));
        var normalized = ManagementStore.ValidateSiteTargetsWrite(new SiteTargetsWrite(1, targets, SiteTargetsScope.Monitoring));
        Assert.Equal(CollectionIdentity.AppTargetKey("doubao"), normalized[1].Key);
        // A platform that became unavailable after the default was saved is not handed to brands.
        var withCatalog = ManagementStore.MaterializeCells(cells, existing, "en-GB", AppCatalog());
        Assert.Equal(["chatgpt", "doubao"], withCatalog.Select(t => t.Engine));
        Assert.Equal(targets[..2], withCatalog);
        // 3.9 B: the same holds for a brand that has other App targets enabled, but a brand that already has this one keeps it.
        Assert.Equal(targets[..2], ManagementStore.MaterializeCells(cells, existing, "en-GB", AppCatalog(), new HashSet<string>(["doubao"])));
        Assert.Equal(targets, ManagementStore.MaterializeCells(cells, existing, "en-GB", AppCatalog(), new HashSet<string>(["ernie"])));
    }

    [Fact]
    public void AppCellsSortAfterEveryApiProviderInPlatformOrder()
    {
        var last = AnswerProviderDefinition.All[^1];
        (string Provider, string Engine, AnswerMode Mode)[] cells =
        [
            ("app_collector", "zhipu_qingyan", AnswerMode.Fast), ("app_collector", "doubao", AnswerMode.Fast), (last.Provider, last.Engines[0], last.DefaultAnswerMode),
            ("app_collector", "tencent_yuanbao", AnswerMode.Fast), ("cloro", "gemini", AnswerMode.Fast), ("app_collector", "ernie", AnswerMode.Fast),
            ("app_collector", "kimi", AnswerMode.Fast), ("deepseek_api", "deepseek", AnswerMode.Deep), ("app_collector", "qwen", AnswerMode.Fast),
            ("app_collector", "deepseek", AnswerMode.Fast), ("cloro", "chatgpt", AnswerMode.Fast),
        ];
        var ordered = cells.OrderBy(c => TeamMonitoringRules.CatalogOrder(c.Provider, c.Engine, c.Mode)).Select(c => $"{c.Provider}:{c.Engine}").ToArray();
        Assert.Equal(["cloro:chatgpt", "cloro:gemini", "deepseek_api:deepseek", $"{last.Provider}:{last.Engines[0]}",
            .. AppChannelDefinition.Engines.Select(engine => $"app_collector:{engine}")], ordered);
        // Existing providers keep their keys; an unknown provider still sorts after everything, the App segment included.
        Assert.Equal((0, 0, 0), TeamMonitoringRules.CatalogOrder("cloro", "chatgpt", AnswerMode.Fast));
        Assert.Equal((AnswerProviderDefinition.All.Count, 0, 0), TeamMonitoringRules.CatalogOrder("app_collector", "doubao", AnswerMode.Fast));
        Assert.Equal((int.MaxValue, int.MaxValue, 0), TeamMonitoringRules.CatalogOrder("domestic_collector", "doubao", AnswerMode.Fast));
    }

    [Fact]
    public void AnswerModeNormalizationFoldsFastAndRejectsOtherModesForAppTargets()
    {
        static CloroVisibilityTarget Target(string provider, string engine, string? mode) => new(provider, engine, "zh-CN", "CN", "{}", mode);
        foreach (var mode in new[] { null, "", "  ", "fast", " FAST " })
        {
            Assert.True(AnswerModeRules.TryNormalize(Target("app_collector", "doubao", mode), out var normalized));
            Assert.Null(normalized.AnswerMode);
            Assert.Equal(CollectionIdentity.AppTargetKey("doubao"), CollectionIdentity.TargetKey(normalized));
        }
        foreach (var mode in new[] { "deep", "DEEP", "thinking" })
        {
            var target = Target(" APP_COLLECTOR ", "doubao", mode);
            Assert.False(AnswerModeRules.TryNormalize(target, out var unchanged));
            Assert.Same(target, unchanged);
            var exception = Assert.Throws<CollectorStoreException>(() => AnswerModeRules.Normalize(target));
            Assert.Equal((AnswerModeRules.AnswerModeUnsupported, 400), Code(exception));
            Assert.Equal($"App 采集 不支持回答模式 {mode}。", exception.Message);
        }
        // Existing providers are unchanged: registered ones fold their default, unregistered ones keep a lower-cased mode.
        Assert.Equal("deep", AnswerModeRules.Normalize(Target("deepseek_api", "deepseek", " Deep ")).AnswerMode);
        Assert.Null(AnswerModeRules.Normalize(Target("doubao_api", "doubao", "deep")).AnswerMode);
        Assert.Equal("deep", AnswerModeRules.Normalize(Target("domestic_collector", "doubao", " DEEP ")).AnswerMode);
        Assert.Equal("DeepSeek 不支持回答模式 x。", AnswerModeRules.Unsupported("deepseek_api", "x").Message.Replace(AnswerProviderDefinition.TryGet("deepseek_api")!.DisplayName, "DeepSeek"));
        Assert.Equal("mystery 不支持回答模式 x。", AnswerModeRules.Unsupported("mystery", "x").Message);
    }
}
