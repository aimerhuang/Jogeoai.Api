using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using static Jogeoai.Tests.TeamMonitoringConfigSqlTests;

namespace Jogeoai.Tests;

/// <summary>scope 的两种取值与团队默认单元格里的 App 单元格（隔离库）。</summary>
public sealed partial class AppSiteTargetsSqlTests
{
    private static string Cell(TeamMonitoringCellView cell)
        => $"{cell.Provider}|{cell.Engine}|{AnswerModeRules.ToStorage(cell.AnswerMode)}|{MonitoringFrequencyRules.ToStorage(cell.Frequency)}";

    private static string[] EnabledKeys(Dictionary<string, TargetRow> rows)
        => rows.Where(r => r.Value.Enabled).Select(r => r.Key).Order(StringComparer.Ordinal).ToArray();

    // 3.4: scope=monitoring replaces API rows and App rows and leaves the web collector rows alone; scope=all replaces everything.
    [AppChannelMySqlFact]
    public Task MonitoringScopeReplacesApiAndAppRowsAndAllScopeReplacesEverything() => IsolatedAsync(async (cs, seedTenant) =>
    {
        var tenant = await seedTenant();
        var site = await SeedBrandAsync(cs, tenant, "scope", follows: false, locale: "zh-CN", created: "2026-05-02");
        await SeedTargetAsync(cs, tenant, site, "domestic_collector", "doubao", "CN", "zh-CN", enabled: true);
        var store = OpenStore(cs, tenant);
        var (doubao, kimi) = (CollectionIdentity.AppTargetKey("doubao"), CollectionIdentity.AppTargetKey("kimi"));
        var view = await store.GetSiteTargetsAsync(tenant, site);
        view = await store.PutSiteTargetsAsync(tenant, site, new SiteTargetsWrite(view.ConfigVersion,
            [ChatGpt, Gemini, App("doubao"), App("kimi")], SiteTargetsScope.Monitoring), markIndividual: true);
        var web = (await TargetRowsAsync(cs, site))[WebKey];
        Assert.Equal(new[] { ChatGptKey, GeminiKey, doubao, kimi, WebKey }.Order(StringComparer.Ordinal), EnabledKeys(await TargetRowsAsync(cs, site)));

        // monitoring: the omitted API row and the omitted App row are disabled; the web row is not touched at all.
        view = await store.PutSiteTargetsAsync(tenant, site, new SiteTargetsWrite(view.ConfigVersion, [ChatGpt, App("kimi")], SiteTargetsScope.Monitoring),
            markIndividual: true);
        var rows = await TargetRowsAsync(cs, site);
        Assert.Equal(new[] { ChatGptKey, kimi, WebKey }.Order(StringComparer.Ordinal), EnabledKeys(rows));
        Assert.Equal(5, rows.Count);
        Assert.Equal(web, rows[WebKey]);
        // A monitoring-scope request made of App targets only is valid and still leaves the web row alone.
        view = await store.PutSiteTargetsAsync(tenant, site, new SiteTargetsWrite(view.ConfigVersion, [App("doubao")], SiteTargetsScope.Monitoring),
            markIndividual: true);
        rows = await TargetRowsAsync(cs, site);
        Assert.Equal(new[] { doubao, WebKey }.Order(StringComparer.Ordinal), EnabledKeys(rows));
        Assert.Equal(web, rows[WebKey]);
        var rejected = await Assert.ThrowsAsync<CollectorStoreException>(() => store.PutSiteTargetsAsync(tenant, site, new SiteTargetsWrite(view.ConfigVersion,
            [App("doubao"), new SiteTargetWrite("domestic_collector", "doubao", "consumer_web", "CN", "zh-CN")], SiteTargetsScope.Monitoring), markIndividual: true));
        Assert.Equal(("TARGET_NOT_ALLOWED", 400), Code(rejected));

        // all (unchanged): every row that is not in the request is disabled, the web row and the App rows included.
        view = await store.PutSiteTargetsAsync(tenant, site, new SiteTargetsWrite(view.ConfigVersion, [Gemini]), markIndividual: true);
        rows = await TargetRowsAsync(cs, site);
        Assert.Equal([GeminiKey], EnabledKeys(rows));
        Assert.Equal(5, rows.Count);
        Assert.Equal(["cloro:chatgpt", "cloro:gemini", "domestic_collector:doubao", "app_collector:doubao", "app_collector:kimi"],
            view.Items.Select(i => $"{i.Provider}:{i.Engine}"));
        Assert.Equal([CollectionChannel.Api, CollectionChannel.Api, CollectionChannel.Web, CollectionChannel.App, CollectionChannel.App],
            view.Items.Select(i => i.Channel!.Value));
    });

    // 3.6: App cells are validated, stored, handed to following brands on the fixed surface and read back; the 20-cell limit
    // counts API cells only. A team without a saved default keeps the historical baseline.
    [AppChannelMySqlFact]
    public Task TeamDefaultWithAppCellsSyncsFollowingBrandsOnTheFixedAppSurface() => IsolatedAsync(async (cs, seedTenant) =>
    {
        var tenant = await seedTenant();
        var follower = await SeedBrandAsync(cs, tenant, "跟随", follows: true, locale: "en-GB", created: "2026-05-03");
        var individual = await SeedBrandAsync(cs, tenant, "单独", follows: false, locale: "zh-CN", created: "2026-05-04");
        await SeedTargetAsync(cs, tenant, individual, "cloro", "copilot", "US", "en-US", enabled: true, anchor: "2026-01-01");
        var store = OpenStore(cs, tenant);
        var tomorrow = Day(Today().AddDays(1));

        // Before any save: the virtual baseline, without App cells; first monitoring of this brand is not materialized.
        var initial = await store.GetTeamMonitoringConfigAsync(tenant);
        Assert.Equal((0u, "cloro|chatgpt|fast|daily"), (initial.ConfigVersion, string.Join(";", initial.Cells.Select(Cell))));
        Assert.Null(await store.MaterializeTeamDefaultAsync(tenant, follower));

        // Every API cell of the registry (16) plus six App cells: more than 20 cells in total, accepted.
        var api = AnswerProviderDefinition.All.SelectMany(d => d.Engines.SelectMany(e => d.SupportedAnswerModes.Select(m => new TeamMonitoringCellWrite(d.Provider, e, m)))).ToArray();
        var apps = AvailableApps.Select(a => new TeamMonitoringCellWrite("app_collector", a.Engine, null, MonitoringFrequency.Weekly)).ToArray();
        var saved = await store.PutTeamMonitoringConfigAsync(tenant, Guid.NewGuid(), new TeamMonitoringConfigWrite(0, [.. apps, .. api]));
        Assert.Equal((1u, api.Length + 6), (saved.ConfigVersion, saved.Cells.Count));
        Assert.True(saved.Cells.Count > MonitoringMatrix.MaxTargets);
        // App cells are listed after every API cell, in platform order, with the App display names.
        Assert.Equal(AvailableApps.Select(a => $"app_collector|{a.Engine}|fast|weekly"), saved.Cells.Skip(api.Length).Select(Cell));
        Assert.DoesNotContain(saved.Cells.Take(api.Length), c => c.Provider == "app_collector");
        Assert.Equal("千问", saved.Cells.Single(c => c.Provider == "app_collector" && c.Engine == "qwen").DisplayName);

        // The follower has every cell; its App rows are CN / zh-CN / app (not the brand's en-GB), weekly, anchored tomorrow.
        var rows = await TargetRowsAsync(cs, follower);
        Assert.Equal(api.Length + 6, rows.Count(r => r.Value.Enabled));
        foreach (var engine in AvailableApps.Select(a => a.Engine))
        {
            var row = rows[CollectionIdentity.AppTargetKey(engine)];
            Assert.Equal(new TargetRow("app_collector", engine, "app", "CN", "zh-CN", true, null, "weekly", tomorrow, true, row.UpdatedAt), row);
        }
        Assert.Equal("en-GB", rows.Values.Single(r => r.Provider == "cloro" && r.Engine == "chatgpt").Locale);
        Assert.Equal(saved.Cells.Select(Cell), saved.Brands.Single(b => b.SiteId == follower).Cells.Select(Cell));
        // The individual brand is not touched.
        Assert.Equal(["cloro|copilot|fast|daily"], saved.Brands.Single(b => b.SiteId == individual).Cells.Select(Cell));
        Assert.Single(await TargetRowsAsync(cs, individual));
        var read = await store.GetTeamMonitoringConfigAsync(tenant);
        Assert.Equal(saved.Cells.Select(Cell), read.Cells.Select(Cell));
        Assert.Equal(saved.Cells.Select(Cell), read.Brands.Single(b => b.SiteId == follower).Cells.Select(Cell));

        // A smaller default: omitted App cells are disabled on the follower, the kept one keeps its anchor.
        await ExecAsync(cs, "UPDATE site_targets SET frequency_anchor = '2026-01-05' WHERE site_id = UUID_TO_BIN(@site) AND provider = 'app_collector';",
            ("@site", follower.ToString()));
        saved = await store.PutTeamMonitoringConfigAsync(tenant, Guid.NewGuid(), new TeamMonitoringConfigWrite(1,
            [new("app_collector", "doubao", AnswerMode.Fast, MonitoringFrequency.Weekly), new("cloro", "chatgpt")]));
        Assert.Equal(["cloro|chatgpt|fast|daily", "app_collector|doubao|fast|weekly"], saved.Cells.Select(Cell));
        rows = await TargetRowsAsync(cs, follower);
        Assert.Equal(2, rows.Count(r => r.Value.Enabled));
        Assert.Equal((true, "2026-01-05"), (rows[CollectionIdentity.AppTargetKey("doubao")].Enabled, rows[CollectionIdentity.AppTargetKey("doubao")].Anchor));
        Assert.False(rows[CollectionIdentity.AppTargetKey("kimi")].Enabled);

        // Restoring the default on the individual brand hands it the App cell too.
        var targets = await store.GetSiteTargetsAsync(tenant, individual);
        targets = await store.FollowTeamDefaultAsync(tenant, individual, new FollowTeamDefaultRequest(targets.ConfigVersion));
        Assert.True(targets.FollowsTeamDefault);
        Assert.Equal(["cloro:chatgpt", "app_collector:doubao"], targets.Items.Where(i => i.Enabled).Select(i => $"{i.Provider}:{i.Engine}"));
        Assert.Equal(tomorrow, (await TargetRowsAsync(cs, individual))[CollectionIdentity.AppTargetKey("doubao")].Anchor);
        // The follower materialization used for estimates keeps the fixed App surface as well.
        var materialized = (await store.MaterializeTeamCellsForFollowersAsync(tenant, [new("app_collector", "kimi", null, MonitoringFrequency.Weekly)]))
            .Single(b => b.SiteId == follower);
        Assert.Equal(new SiteTargetWrite("app_collector", "kimi", "app", "CN", "zh-CN", true, AnswerMode.Fast, MonitoringFrequency.Weekly), Assert.Single(materialized.Targets));

        // Rejected saves keep the stored default: unavailable platform (with the reason), non-weekly frequency, 21 API cells.
        (TeamMonitoringCellWrite[] Cells, string Code)[] invalid =
        [
            ([new("cloro", "chatgpt"), new("app_collector", "ernie", null, MonitoringFrequency.Weekly)], "TARGET_NOT_ALLOWED"),
            ([new("app_collector", "doubao")], MonitoringFrequencyRules.FrequencyInvalid),
            ([.. Enumerable.Repeat(new TeamMonitoringCellWrite("cloro", "chatgpt"), 21), .. apps], "MONITORING_TOO_LARGE"),
        ];
        foreach (var (cells, code) in invalid)
        {
            var rejected = await Assert.ThrowsAsync<CollectorStoreException>(() => store.PutTeamMonitoringConfigAsync(tenant, Guid.NewGuid(),
                new TeamMonitoringConfigWrite(2, cells)));
            Assert.Equal((code, 400), Code(rejected));
        }
        Assert.Equal(2u, (await store.GetTeamMonitoringConfigAsync(tenant)).ConfigVersion);

        // Another team that never saved a default is still on the baseline, and nothing is materialized for its brands.
        var other = await seedTenant();
        var baseline = await SeedBrandAsync(cs, other, "基线", follows: true, locale: "zh-CN", created: "2026-05-05");
        Assert.Null(await store.MaterializeTeamDefaultAsync(other, baseline));
        Assert.Equal("cloro|chatgpt|fast|daily", string.Join(";", (await store.GetTeamMonitoringConfigAsync(other)).Cells.Select(Cell)));
    });
}
