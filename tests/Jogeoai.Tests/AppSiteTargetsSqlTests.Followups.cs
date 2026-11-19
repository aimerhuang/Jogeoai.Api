using System.Text.Json;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using static Jogeoai.Tests.TeamMonitoringConfigSqlTests;

namespace Jogeoai.Tests;

/// <summary>
/// 阶段 3 复核后补的两项（implement 3.9，隔离库）：未开放 App 渠道的租户（Collector:AppTenantIds）看不到也保存不了 App 目标；
/// 之前已启用、现在平台不可用的 App 目标在保存时保留，只拒绝新启用。提问范围里的 App 目标键见 AppSiteTargetsSqlTests.Scopes.cs。
/// </summary>
public sealed partial class AppSiteTargetsSqlTests
{
    private const string NotInCatalog = "目标不在 Provider 目录中。";
    private static readonly TeamMonitoringCellWrite ChatGptCell = new("cloro", "chatgpt");
    private static readonly TeamMonitoringCellWrite GeminiCell = new("cloro", "gemini");

    private static TeamMonitoringCellWrite AppCell(string engine) => new("app_collector", engine, null, MonitoringFrequency.Weekly);

    private static string Json(object value) => JsonSerializer.Serialize(value, Api);

    // Enabled rows by provider and engine (a following brand's Cloro rows take the brand's locale, so their keys differ).
    private static string[] EnabledEngines(Dictionary<string, TargetRow> rows)
        => rows.Values.Where(r => r.Enabled).Select(r => $"{r.Provider}:{r.Engine}").Order(StringComparer.Ordinal).ToArray();

    private static async Task<string> TeamCellRowsAsync(string cs, Guid tenant)
        => string.Join(";", (await RowsAsync(cs, """
                SELECT provider, engine, answer_mode, frequency FROM tenant_monitoring_default_cells
                WHERE tenant_id = UUID_TO_BIN(@tenant) ORDER BY provider, engine, answer_mode;
                """, ("@tenant", tenant.ToString()))).Select(r => string.Join("|", r)));

    private static async Task<CollectorStoreException> RefusedAsync(Func<Task> action, string code, string? message = null)
    {
        var exception = await Assert.ThrowsAsync<CollectorStoreException>(action);
        Assert.Equal((code, 400), Code(exception));
        if (message is not null) Assert.Contains(message, exception.Message, StringComparison.Ordinal);
        return exception;
    }

    // 3.9 A (design 6.1): a tenant that is not in Collector:AppTenantIds gets no App row from the catalog, the site targets or
    // the team default, and every save or estimate that names an App target is refused like a target outside the catalog.
    // Rows written while the tenant was open stay exactly as stored and are visible again once the tenant is listed.
    [AppChannelMySqlFact]
    public Task TenantsOutsideTheAppListNeverSeeOrSaveAppTargets() => IsolatedAsync(async (cs, seedTenant) =>
    {
        var tenant = await seedTenant();
        var listed = await seedTenant();
        var database = new MySqlDatabase(cs);
        var open = OpenStore(database, tenant, listed);
        var closed = OpenStore(database, listed);
        var service = Service(database, closed, listed);
        var site = await SeedBrandAsync(cs, tenant, "未开放", follows: true, locale: "zh-CN", created: "2026-05-09");
        await SeedPromptAsync(cs, site);

        // Written while the tenant was listed: two API targets, an enabled and a disabled App target, a default with an App cell.
        var written = await open.PutSiteTargetsAsync(tenant, site, new SiteTargetsWrite((await open.GetSiteTargetsAsync(tenant, site)).ConfigVersion,
            [ChatGpt, Gemini, App("doubao"), App("kimi", enabled: false)]));
        await open.PutTeamMonitoringConfigAsync(tenant, Guid.NewGuid(), new TeamMonitoringConfigWrite(0, [ChatGptCell, GeminiCell, AppCell("doubao")]));
        var openView = await open.GetSiteTargetsAsync(tenant, site);
        Assert.Equal(["cloro:chatgpt", "cloro:gemini", "app_collector:doubao", "app_collector:kimi"], openView.Items.Select(i => $"{i.Provider}:{i.Engine}"));
        var rows = await TargetRowsAsync(cs, site);
        var cells = await TeamCellRowsAsync(cs, tenant);
        Assert.Contains("app_collector|doubao", cells, StringComparison.Ordinal);

        // Catalog: no App row for the tenant; the listed tenant still gets all seven through the same store.
        Assert.DoesNotContain(await closed.GetTargetCatalogAsync(tenantId: tenant), c => c.Provider == "app_collector");
        Assert.Equal(7, (await closed.GetTargetCatalogAsync(tenantId: listed)).Count(c => c.Provider == "app_collector"));

        // Site targets: the stored App rows are not returned; the other rows and the version are what the open view has.
        foreach (var store in new[] { closed, OpenStore(database), new ManagementStore(database) })
        {
            var view = await store.GetSiteTargetsAsync(tenant, site);
            Assert.Equal(Json(openView with { Items = openView.Items.Where(i => i.Provider != "app_collector").ToArray() }), Json(view));
            Assert.Equal(2, view.Items.Count);
        }

        // Team default: neither the default's cells nor the brand's cells carry an App cell.
        var team = await closed.GetTeamMonitoringConfigAsync(tenant);
        Assert.Equal(["cloro|chatgpt|fast|daily", "cloro|gemini|fast|daily"], team.Cells.Select(Cell));
        Assert.Equal(team.Cells.Select(Cell), team.Brands.Single(b => b.SiteId == site).Cells.Select(Cell));
        Assert.Contains("app_collector|doubao|fast|weekly", (await open.GetTeamMonitoringConfigAsync(tenant)).Cells.Select(Cell));
        // First monitoring of a following brand would not be handed an App target either.
        var fresh = await SeedBrandAsync(cs, tenant, "新品牌", follows: true, locale: "zh-CN", created: "2026-05-10");
        Assert.DoesNotContain((await closed.MaterializeTeamDefaultAsync(tenant, fresh))!, t => t.Provider == "app_collector");
        Assert.Contains((await open.MaterializeTeamDefaultAsync(tenant, fresh))!, t => t.Provider == "app_collector");

        // Every write path refuses an App target with the catalog error — enabled, disabled, already stored or not.
        var version = openView.ConfigVersion;
        foreach (var target in new[] { App("doubao"), App("doubao", enabled: false), App("kimi"), App("ernie", enabled: false) })
        {
            await RefusedAsync(() => closed.PutSiteTargetsAsync(tenant, site, new SiteTargetsWrite(version, [ChatGpt, target]), markIndividual: true),
                "TARGET_NOT_ALLOWED", NotInCatalog);
            await RefusedAsync(() => closed.PutSiteTargetsAsync(tenant, site, new SiteTargetsWrite(version, [target], SiteTargetsScope.Monitoring), markIndividual: true),
                "TARGET_NOT_ALLOWED", NotInCatalog);
            await RefusedAsync(() => service.EstimateTargetsAsync(tenant, site, new MonitoringTargetsEstimateRequest([ChatGpt, target])),
                "TARGET_NOT_ALLOWED", NotInCatalog);
        }
        await RefusedAsync(() => closed.PutTeamMonitoringConfigAsync(tenant, Guid.NewGuid(), new TeamMonitoringConfigWrite(1, [ChatGptCell, AppCell("doubao")])),
            "TARGET_NOT_ALLOWED", NotInCatalog);
        await RefusedAsync(() => closed.MaterializeTeamCellsForFollowersAsync(tenant, [ChatGptCell, AppCell("doubao")]), "TARGET_NOT_ALLOWED", NotInCatalog);
        await RefusedAsync(() => service.EstimateTeamMonitoringAsync(tenant, new TeamMonitoringEstimateRequest([ChatGptCell, AppCell("kimi")])),
            "TARGET_NOT_ALLOWED", NotInCatalog);
        // A prompt scope cannot name an App target key for this tenant.
        var doubaoKey = CollectionIdentity.AppTargetKey("doubao");
        await RefusedAsync(() => closed.CreatePromptAsync(tenant, site, new PromptWrite("未开放租户的 App 范围提问？", "zh-CN", TargetKeys: [ChatGptKey, doubaoKey])),
            PromptMonitoringScale.TargetInvalidCode);
        await RefusedAsync(() => service.EstimateAsync(tenant, site, new PromptMonitoringEstimateRequest(1, [doubaoKey])), PromptMonitoringScale.TargetInvalidCode);
        Assert.Equal(rows, await TargetRowsAsync(cs, site));
        Assert.Equal(cells, await TeamCellRowsAsync(cs, tenant));
        Assert.Equal(version, (await closed.GetSiteTargetsAsync(tenant, site)).ConfigVersion);
        Assert.Equal("1", (await RowsAsync(cs, "SELECT COUNT(*) FROM prompts WHERE site_id = UUID_TO_BIN(@site);", ("@site", site.ToString())))[0][0]);

        // The estimates without App targets still work, and saves of the other targets leave the stored App rows and the stored
        // App cell exactly as they are (scope=all and the team default included).
        Assert.Equal(1, (await service.EstimateTargetsAsync(tenant, site, new MonitoringTargetsEstimateRequest([ChatGpt]))).PromptCount);
        var saved = await closed.PutSiteTargetsAsync(tenant, site, new SiteTargetsWrite(version, [ChatGpt]));
        Assert.Equal(["cloro:chatgpt", "cloro:gemini"], saved.Items.Select(i => $"{i.Provider}:{i.Engine}"));
        var after = await TargetRowsAsync(cs, site);
        Assert.False(after[GeminiKey].Enabled);
        foreach (var engine in new[] { "doubao", "kimi" })
            Assert.Equal(rows[CollectionIdentity.AppTargetKey(engine)], after[CollectionIdentity.AppTargetKey(engine)]);
        var savedTeam = await closed.PutTeamMonitoringConfigAsync(tenant, Guid.NewGuid(), new TeamMonitoringConfigWrite(1, [GeminiCell]));
        Assert.Equal(["cloro|gemini|fast|daily"], savedTeam.Cells.Select(Cell));
        Assert.Equal("app_collector|doubao||weekly;cloro|gemini||daily", await TeamCellRowsAsync(cs, tenant));
        after = await TargetRowsAsync(cs, site);
        Assert.Equal((false, true), (after[ChatGptKey].Enabled, after[GeminiKey].Enabled));
        foreach (var engine in new[] { "doubao", "kimi" })
            Assert.Equal(rows[CollectionIdentity.AppTargetKey(engine)], after[CollectionIdentity.AppTargetKey(engine)]);

        // Listed again: the same rows are back in the views.
        Assert.Equal(["cloro:chatgpt", "cloro:gemini", "app_collector:doubao", "app_collector:kimi"],
            (await open.GetSiteTargetsAsync(tenant, site)).Items.Select(i => $"{i.Provider}:{i.Engine}"));
        Assert.Equal(["cloro|gemini|fast|daily", "app_collector|doubao|fast|weekly"], (await open.GetTeamMonitoringConfigAsync(tenant)).Cells.Select(Cell));
        Assert.Equal(written.Items.Single(i => i.Engine == "doubao" && i.Provider == "app_collector").TargetId,
            (await open.GetSiteTargetsAsync(tenant, site)).Items.Single(i => i.Engine == "doubao" && i.Provider == "app_collector").TargetId);
    });

    // 3.9 B (design 3.4): once a platform's App entry is switched off, a target that was enabled before the save is accepted
    // and stays enabled (shown as unavailable with the reason); only newly enabling one is refused. The same rule holds for the
    // team default's cells and for both estimates; brands that do not have the target yet are still not handed it.
    [AppChannelMySqlFact]
    public Task AnEnabledAppTargetIsKeptWhenItsPlatformBecomesUnavailableAndOnlyNewlyEnablingIsRefused() => IsolatedAsync(async (cs, seedTenant) =>
    {
        var tenant = await seedTenant();
        var database = new MySqlDatabase(cs);
        var store = OpenStore(database, tenant);
        var service = Service(database, store, tenant);
        var (kimi, doubao) = (CollectionIdentity.AppTargetKey("kimi"), CollectionIdentity.AppTargetKey("doubao"));
        var brand = await SeedBrandAsync(cs, tenant, "已启用 Kimi", follows: false, locale: "zh-CN", created: "2026-05-11");
        var plain = await SeedBrandAsync(cs, tenant, "没有 Kimi", follows: false, locale: "zh-CN", created: "2026-05-12");
        var follower = await SeedBrandAsync(cs, tenant, "跟随", follows: true, locale: "zh-CN", created: "2026-05-13");
        await SeedPromptAsync(cs, brand);
        await SeedPromptAsync(cs, follower);

        // While kimi is usable: the brand and (through the team default) the follower enable it.
        var view = await store.PutSiteTargetsAsync(tenant, brand, new SiteTargetsWrite((await store.GetSiteTargetsAsync(tenant, brand)).ConfigVersion,
            [ChatGpt, App("kimi"), App("doubao")]), markIndividual: true);
        var plainView = await store.PutSiteTargetsAsync(tenant, plain, new SiteTargetsWrite((await store.GetSiteTargetsAsync(tenant, plain)).ConfigVersion,
            [ChatGpt, App("doubao")]), markIndividual: true);
        await store.PutTeamMonitoringConfigAsync(tenant, Guid.NewGuid(), new TeamMonitoringConfigWrite(0, [ChatGptCell, AppCell("kimi")]));
        await ExecAsync(cs, "UPDATE site_targets SET frequency_anchor = '2026-01-05' WHERE tenant_id = UUID_TO_BIN(@tenant) AND provider = 'app_collector';",
            ("@tenant", tenant.ToString()));
        // A brand that starts following after that save has no kimi target.
        var late = await SeedBrandAsync(cs, tenant, "后加入", follows: true, locale: "zh-CN", created: "2026-05-14");

        try
        {
            await ExecAsync(cs, "UPDATE collector_platform_entries SET enabled = FALSE, unavailable_reason = 'APP_EMULATOR_REJECTED' WHERE channel = 'consumer_app' AND platform_code = 'kimi';");
            var catalogRow = (await store.GetTargetCatalogAsync(tenantId: tenant)).Single(c => c.Provider == "app_collector" && c.Engine == "kimi");
            Assert.Equal(("unavailable", "APP_EMULATOR_REJECTED"), (catalogRow.Availability, catalogRow.UnavailableReason?.Code));

            // The user changes an API target (chatgpt → gemini) with the kimi row still in the request: saved, kimi stays enabled.
            view = await store.PutSiteTargetsAsync(tenant, brand, new SiteTargetsWrite(view.ConfigVersion, [Gemini, App("kimi"), App("doubao")]), markIndividual: true);
            var rows = await TargetRowsAsync(cs, brand);
            Assert.Equal(new[] { GeminiKey, kimi, doubao }.Order(StringComparer.Ordinal), EnabledKeys(rows));
            Assert.Equal((true, "2026-01-05", "weekly"), (rows[kimi].Enabled, rows[kimi].Anchor, rows[kimi].Frequency));
            var item = view.Items.Single(i => i.TargetKey == kimi);
            Assert.Equal((true, "unavailable", "APP_EMULATOR_REJECTED", "App 拒绝在模拟器上运行"),
                (item.Enabled, item.Availability, item.UnavailableReason?.Code, item.UnavailableReason?.Message));
            var usable = view.Items.Single(i => i.TargetKey == doubao);
            Assert.Equal(("available", null), (usable.Availability, usable.UnavailableReason));
            // Monitoring scope behaves the same.
            view = await store.PutSiteTargetsAsync(tenant, brand, new SiteTargetsWrite(view.ConfigVersion, [Gemini, ChatGpt, App("kimi"), App("doubao")],
                SiteTargetsScope.Monitoring), markIndividual: true);
            Assert.True((await TargetRowsAsync(cs, brand))[kimi].Enabled);

            // Newly enabling another unavailable platform in the same request is refused, and nothing is written.
            rows = await TargetRowsAsync(cs, brand);
            var ernie = await RefusedAsync(() => store.PutSiteTargetsAsync(tenant, brand, new SiteTargetsWrite(view.ConfigVersion,
                [Gemini, App("kimi"), App("doubao"), App("ernie")]), markIndividual: true), "TARGET_NOT_ALLOWED", "文心一言");
            Assert.Contains("App 内容无法读取", ernie.Message, StringComparison.Ordinal);
            // So is newly enabling kimi on a brand that does not have it.
            await RefusedAsync(() => store.PutSiteTargetsAsync(tenant, plain, new SiteTargetsWrite(plainView.ConfigVersion, [ChatGpt, App("doubao"), App("kimi")]),
                markIndividual: true), "TARGET_NOT_ALLOWED", "App 拒绝在模拟器上运行");
            Assert.Equal(rows, await TargetRowsAsync(cs, brand));
            Assert.Equal(view.ConfigVersion, (await store.GetSiteTargetsAsync(tenant, brand)).ConfigVersion);
            Assert.DoesNotContain(kimi, (await TargetRowsAsync(cs, plain)).Keys);

            // Estimates: the already-enabled target does not fail the estimate; a newly enabled one does.
            var estimate = await service.EstimateTargetsAsync(tenant, brand, new MonitoringTargetsEstimateRequest([Gemini, App("kimi"), App("doubao")]));
            // implement 8.1: the API cells are unchanged; both App cells are listed after them, and the unavailable platform has
            // no capacity, so nothing is expected to be collected there.
            Assert.Equal([GeminiKey], estimate.Cells.Where(c => c.Channel == CollectionChannel.Api).Select(c => c.TargetKey));
            Assert.Equal([doubao, kimi], estimate.Cells.Where(c => c.Channel == CollectionChannel.App).Select(c => c.TargetKey));
            var kimiPlatform = estimate.Channels!.App.Platforms.Single(p => p.Engine == "kimi");
            Assert.Equal((0L, kimiPlatform.WeeklyDemand), (kimiPlatform.WeeklyCapacity, kimiPlatform.ExpectedUncollected));
            await RefusedAsync(() => service.EstimateTargetsAsync(tenant, brand, new MonitoringTargetsEstimateRequest([Gemini, App("kimi"), App("ernie")])),
                "TARGET_NOT_ALLOWED", "文心一言");
            await RefusedAsync(() => service.EstimateTargetsAsync(tenant, plain, new MonitoringTargetsEstimateRequest([ChatGpt, App("kimi")])),
                "TARGET_NOT_ALLOWED", "App 拒绝在模拟器上运行");

            // Team default: the kimi cell is already saved, so a save that keeps it is accepted. The follower keeps its target;
            // the brand that never had it is not handed it.
            var team = await store.PutTeamMonitoringConfigAsync(tenant, Guid.NewGuid(), new TeamMonitoringConfigWrite(1, [ChatGptCell, GeminiCell, AppCell("kimi"), AppCell("doubao")]));
            Assert.Equal(["cloro|chatgpt|fast|daily", "cloro|gemini|fast|daily", "app_collector|doubao|fast|weekly", "app_collector|kimi|fast|weekly"], team.Cells.Select(Cell));
            var followerRows = await TargetRowsAsync(cs, follower);
            Assert.Equal(["app_collector:doubao", "app_collector:kimi", "cloro:chatgpt", "cloro:gemini"], EnabledEngines(followerRows));
            Assert.Equal("2026-01-05", followerRows[kimi].Anchor);
            Assert.Equal(["app_collector:doubao", "cloro:chatgpt", "cloro:gemini"], EnabledEngines(await TargetRowsAsync(cs, late)));
            // A new cell on an unavailable platform is refused; the saved default is unchanged.
            await RefusedAsync(() => store.PutTeamMonitoringConfigAsync(tenant, Guid.NewGuid(), new TeamMonitoringConfigWrite(2, [ChatGptCell, AppCell("kimi"), AppCell("ernie")])),
                "TARGET_NOT_ALLOWED", "文心一言");
            Assert.Equal(2u, (await store.GetTeamMonitoringConfigAsync(tenant)).ConfigVersion);
            // Team estimate: accepted with the saved kimi cell; only the follower that has kimi keeps it in its draft.
            var drafts = await store.MaterializeTeamCellsForFollowersAsync(tenant, [ChatGptCell, AppCell("kimi")]);
            Assert.Equal(["chatgpt", "kimi"], drafts.Single(b => b.SiteId == follower).Targets.Select(t => t.Engine));
            Assert.Equal(["chatgpt"], drafts.Single(b => b.SiteId == late).Targets.Select(t => t.Engine));
            var teamEstimate = await service.EstimateTeamMonitoringAsync(tenant, new TeamMonitoringEstimateRequest([ChatGptCell, AppCell("kimi")]));
            Assert.Equal(new[] { follower, late }, teamEstimate.Brands.Select(b => b.SiteId));
            await RefusedAsync(() => service.EstimateTeamMonitoringAsync(tenant, new TeamMonitoringEstimateRequest([ChatGptCell, AppCell("ernie")])),
                "TARGET_NOT_ALLOWED", "文心一言");

            // Once the cell is removed from the default (the follower's target is disabled with it), adding it back is new.
            await store.PutTeamMonitoringConfigAsync(tenant, Guid.NewGuid(), new TeamMonitoringConfigWrite(2, [ChatGptCell, AppCell("doubao")]));
            Assert.False((await TargetRowsAsync(cs, follower))[kimi].Enabled);
            await RefusedAsync(() => store.PutTeamMonitoringConfigAsync(tenant, Guid.NewGuid(), new TeamMonitoringConfigWrite(3, [ChatGptCell, AppCell("doubao"), AppCell("kimi")])),
                "TARGET_NOT_ALLOWED", "App 拒绝在模拟器上运行");

            // The same on the brand: disabling is accepted (shown as disabled with the reason), enabling again is refused.
            view = await store.PutSiteTargetsAsync(tenant, brand, new SiteTargetsWrite(view.ConfigVersion, [Gemini, App("kimi", enabled: false), App("doubao")]), markIndividual: true);
            item = view.Items.Single(i => i.TargetKey == kimi);
            Assert.Equal((false, "disabled", "APP_EMULATOR_REJECTED"), (item.Enabled, item.Availability, item.UnavailableReason?.Code));
            await RefusedAsync(() => store.PutSiteTargetsAsync(tenant, brand, new SiteTargetsWrite(view.ConfigVersion, [Gemini, App("kimi"), App("doubao")]), markIndividual: true),
                "TARGET_NOT_ALLOWED", "App 拒绝在模拟器上运行");
        }
        finally
        {
            await ExecAsync(cs, "UPDATE collector_platform_entries SET enabled = TRUE, unavailable_reason = NULL WHERE channel = 'consumer_app' AND platform_code = 'kimi';");
        }
        // With the entry back, enabling works again.
        view = await store.PutSiteTargetsAsync(tenant, brand, new SiteTargetsWrite(view.ConfigVersion, [Gemini, App("kimi"), App("doubao")]), markIndividual: true);
        Assert.Equal("available", view.Items.Single(i => i.TargetKey == kimi).Availability);
    });
}
