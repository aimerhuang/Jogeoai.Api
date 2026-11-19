using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using MySqlConnector;
using static Jogeoai.Tests.TeamMonitoringConfigSqlTests;

namespace Jogeoai.Tests;

/// <summary>兼容性（design 第 1、12 节）：没有 App 目标的品牌不变；有 App 目标的品牌其主计划、凌晨计划、账单视图与 API 预估不变；首轮、立即采集、手动采集不含 App 目标（隔离库）。</summary>
public sealed partial class AppSiteTargetsSqlTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions Api = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly SiteTargetWrite DeepSeekTwice = new("deepseek_api", "deepseek", "api", "CN", "zh-CN", true, null, MonitoringFrequency.TwiceDaily);
    private static readonly string DeepSeekKey = CollectionIdentity.TargetKey("deepseek_api", "deepseek", "api", "CN", "zh-CN");
    private static readonly string[] NewFields = ["channel", "fixedFrequency", "unavailableReason"];

    private static PromptMonitoringService Service(MySqlDatabase database, IManagementStore management, params Guid[] openTenants)
        => new(database, management, new CollectionScheduleStore(database), new CloroVisibilityStore(database), appChannel: OpenFor(openTenants));

    private static CloroVisibilityTarget Target(SiteTargetWrite write) => new(write.Provider, write.Engine, write.Locale, write.Country, "{}");

    private static async Task<Guid[]> PromptIdsAsync(string cs, Guid site)
        => (await RowsAsync(cs, "SELECT BIN_TO_UUID(id) FROM prompts WHERE site_id = UUID_TO_BIN(@site) ORDER BY id;", ("@site", site.ToString())))
            .Select(r => Guid.Parse(r[0]!)).ToArray();

    private static Task CreateMainPlanAsync(MySqlDatabase database, Guid tenant, Guid site, Guid[] prompts, params SiteTargetWrite[] targets)
        => new CollectionScheduleStore(database).CreateAsync(new CreateCollectionScheduleCommand(tenant, site, PromptMonitoringIdentity.ScheduleName,
            "daily", null, PromptMonitoringIdentity.MainLocalTime, Zone, new CollectionScheduleSelection(prompts, targets.Select(Target).ToArray()), Enabled: true));

    // The site-target response with the three new fields removed from every item (optionally only the non-App items, without the version).
    private static string Legacy(SiteTargetsView view, bool apiRowsOnly = false)
    {
        var node = JsonSerializer.SerializeToNode(view, Api)!.AsObject();
        var items = node["items"]!.AsArray();
        for (var i = items.Count - 1; i >= 0; i--)
        {
            var item = items[i]!.AsObject();
            if (apiRowsOnly && item["provider"]!.GetValue<string>() == "app_collector") { items.RemoveAt(i); continue; }
            foreach (var field in NewFields) Assert.True(item.Remove(field), field);
            Assert.Equal(20, item.Count);
        }
        if (apiRowsOnly) node.Remove("configVersion");
        return node.ToJsonString();
    }

    // Everything the daily (API) monitoring of one brand is made of: both plans as stored after a reconcile, the selection hash,
    // the eligible targets, a planned main and night slot, the billing view, and the two estimates. The App plan (phase 4A) is
    // a separate schedule of the brand and is not part of the daily monitoring, so it is left out of the plan rows.
    private static async Task<string> ApiStateAsync(string cs, MySqlDatabase database, PromptMonitoringService service, Guid tenant, Guid site,
        SiteTargetWrite[] drafts)
    {
        await AutomaticMonitoringSelection.ReconcileAsync(database, tenant, site, default);
        var plans = await RowsAsync(cs, """
            SELECT name, version, enabled, CAST(selection_snapshot AS CHAR), local_time, time_zone, next_slot_at
            FROM collection_schedules WHERE site_id = UUID_TO_BIN(@site) AND name <> @app ORDER BY name;
            """, ("@site", site.ToString()), ("@app", PromptMonitoringIdentity.AppScheduleName));
        var main = JsonSerializer.Deserialize<CollectionScheduleSelection>(plans.Single(p => p[0] == PromptMonitoringIdentity.ScheduleName)[3]!, Web)!;
        await using var connection = new MySqlConnection(cs);
        await connection.OpenAsync();
        var targets = await AutomaticMonitoringSelection.Targets(connection, null, tenant, site, false, default);
        var prompts = await AutomaticMonitoringSelection.Prompts(connection, null, tenant, site, default);
        var slotDate = Today().AddDays(1);
        var nominal = new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var estimate = JsonSerializer.SerializeToNode(await service.EstimateTargetsAsync(tenant, site, new MonitoringTargetsEstimateRequest(drafts)), Api)!.AsObject();
        estimate.Remove("nextSlotAt");
        ApiPart(estimate);
        return string.Join("\n",
            "plans=" + string.Join(";", plans.Select(p => string.Join("|", p))),
            "hash=" + CollectionIdentity.SelectionHash(main),
            "targets=" + JsonSerializer.Serialize(targets, Api),
            "main=" + JsonSerializer.Serialize(AutomaticMonitoringSelection.PlanSlot(MonitoringSlotKind.Main, main, prompts, targets, slotDate, nominal), Web),
            "night=" + JsonSerializer.Serialize(AutomaticMonitoringSelection.PlanSlot(MonitoringSlotKind.Night, main, prompts, targets, slotDate, nominal), Web),
            "billing=" + JsonSerializer.Serialize((await service.GetAsync(tenant, site)).Billing, Api),
            "estimate=" + estimate.ToJsonString(),
            "promptEstimate=" + JsonSerializer.Serialize(await service.EstimateAsync(tenant, site, new PromptMonitoringEstimateRequest(1)), Api));
    }

    // implement 8.1: a draft with App cells adds the App part to the estimate (channels, App cells, App checks in the weekly
    // totals). This reduces such an estimate to its API part — channels.api's weekly figures, the API cells without their channel
    // — so that it can be compared with the estimate of the API cells alone. An estimate without channels is left as it is.
    private static void ApiPart(JsonObject estimate)
    {
        if (estimate["channels"] is not JsonObject channels) return;
        var api = channels["api"]!.AsObject();
        estimate["weeklyChecks"] = api["weeklyChecks"]!.DeepClone();
        estimate["weeklyCredits"] = api["weeklyCredits"]!.DeepClone();
        var cells = estimate["cells"]!.AsArray();
        for (var i = cells.Count - 1; i >= 0; i--)
        {
            var cell = cells[i]!.AsObject();
            if (cell["channel"]!.GetValue<string>() == "app") cells.RemoveAt(i);
            else Assert.Equal("api", cell["channel"]!.GetValue<string>());
            cell.Remove("channel");
        }
        Assert.Equal(api["targets"]!.GetValue<int>(), cells.Count);
        estimate.Remove("channels");
    }

    [AppChannelMySqlFact]
    public Task BrandsWithoutAppTargetsAreUnchangedAndAppTargetsNeverReachTheDailyPlansOrTheApiEstimate() => IsolatedAsync(async (cs, seedTenant) =>
    {
        var tenant = await seedTenant();
        var database = new MySqlDatabase(cs);
        var store = OpenStore(database, tenant);
        var service = Service(database, store, tenant);

        // Brand A never gets an App target: rows written the pre-App way, straight into the table.
        var plain = await SeedBrandAsync(cs, tenant, "无 App", follows: false, locale: "zh-CN", created: "2026-05-06");
        await SeedTargetAsync(cs, tenant, plain, "cloro", "chatgpt", "US", "en-US", enabled: true, anchor: "2026-01-01");
        await SeedTargetAsync(cs, tenant, plain, "cloro", "gemini", "US", "en-US", enabled: false);
        await SeedTargetAsync(cs, tenant, plain, "deepseek_api", "deepseek", "CN", "zh-CN", enabled: true);
        await SeedTargetAsync(cs, tenant, plain, "domestic_collector", "doubao", "CN", "zh-CN", enabled: true);
        await SeedPromptAsync(cs, plain);
        SiteTargetWrite[] plainDrafts = [ChatGpt, DeepSeekTwice with { Frequency = MonitoringFrequency.Daily }];
        await CreateMainPlanAsync(database, tenant, plain, await PromptIdsAsync(cs, plain), plainDrafts);

        // Brand B: two daily cells and a twice_daily cell (so it also has the night plan), one 'all' and one custom prompt.
        var brand = await SeedBrandAsync(cs, tenant, "有 App", follows: false, locale: "zh-CN", created: "2026-05-07");
        SiteTargetWrite[] apiDrafts = [ChatGpt, Gemini, DeepSeekTwice];
        var saved = await store.PutSiteTargetsAsync(tenant, brand, new SiteTargetsWrite((await store.GetSiteTargetsAsync(tenant, brand)).ConfigVersion, apiDrafts),
            markIndividual: true);
        await SeedPromptAsync(cs, brand);
        await SeedPromptAsync(cs, brand);
        var prompts = await PromptIdsAsync(cs, brand);
        await ExecAsync(cs, """
            UPDATE prompts SET target_scope = 'custom' WHERE id = UUID_TO_BIN(@prompt);
            INSERT INTO prompt_target_scopes (tenant_id, site_id, prompt_id, target_key, created_at)
            VALUES (UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), UUID_TO_BIN(@prompt), @key, UTC_TIMESTAMP(6));
            """, ("@tenant", tenant.ToString()), ("@site", brand.ToString()), ("@prompt", prompts[1].ToString()), ("@key", GeminiKey));
        await CreateMainPlanAsync(database, tenant, brand, prompts, apiDrafts);

        var plainBefore = await ApiStateAsync(cs, database, service, tenant, plain, plainDrafts);
        var before = await ApiStateAsync(cs, database, service, tenant, brand, apiDrafts);
        Assert.Equal(before, await ApiStateAsync(cs, database, service, tenant, brand, apiDrafts));
        Assert.Contains(PromptMonitoringIdentity.NightScheduleName, before, StringComparison.Ordinal);
        Assert.Contains("promptTargets", before, StringComparison.Ordinal);

        // Brand A's response: every pre-App field has the value the pre-App projection gave it; only the three fields are new.
        var plainView = await store.GetSiteTargetsAsync(tenant, plain);
        var ids = (await RowsAsync(cs, "SELECT target_key, BIN_TO_UUID(id) FROM site_targets WHERE site_id = UUID_TO_BIN(@site);", ("@site", plain.ToString())))
            .ToDictionary(r => r[0]!, r => Guid.Parse(r[1]!));
        AnswerMode[] fast = [AnswerMode.Fast], both = [AnswerMode.Fast, AnswerMode.Deep];
        CollectionTargetView[] expected =
        [
            new(ids[ChatGptKey], "cloro", "chatgpt", "chatgpt", "scraped", "observed_surface", "US", "en-US", true, "available", "supported", "conditional",
                "conditional", ChatGptKey, true, fast, AnswerMode.Fast, AnswerMode.Fast, MonitoringFrequency.Daily, new DateOnly(2026, 1, 1)),
            new(ids[GeminiKey], "cloro", "gemini", "gemini", "scraped", "observed_surface", "US", "en-US", false, "disabled", "supported", "conditional",
                "conditional", GeminiKey, true, fast, AnswerMode.Fast, AnswerMode.Fast, MonitoringFrequency.Daily, null),
            new(ids[DeepSeekKey], "deepseek_api", "deepseek", "DeepSeek", "api", "api_response", "CN", "zh-CN", true, "available", "supported", "conditional",
                "conditional", DeepSeekKey, true, both, AnswerMode.Fast, AnswerMode.Fast, MonitoringFrequency.Daily, null),
            new(ids[WebKey], "domestic_collector", "doubao", "豆包", "consumer_web", "observed_surface", "CN", "zh-CN", true, "available", "supported", "conditional",
                "unknown", WebKey, false, fast, AnswerMode.Fast, AnswerMode.Fast, MonitoringFrequency.Daily, null),
        ];
        // The hand-built rows carry no new field values; Legacy() removes the three (null) members from both sides.
        Assert.Equal(Legacy(new SiteTargetsView(plainView.ConfigVersion, expected, false)), Legacy(plainView));
        Assert.Equal([CollectionChannel.Api, CollectionChannel.Api, CollectionChannel.Api, CollectionChannel.Web], plainView.Items.Select(i => i.Channel!.Value));
        Assert.All(plainView.Items, i => Assert.True(i.FixedFrequency is null && i.UnavailableReason is null));
        Assert.Equal(new[] { ChatGptKey, GeminiKey, DeepSeekKey, WebKey }, plainView.Items.Select(i => i.TargetKey));
        var plainLegacy = Legacy(plainView);

        // Brand B gains six App targets (and, as later phases will allow, a prompt scope that names an App key).
        var apiRowsBefore = Legacy(saved, apiRowsOnly: true);
        saved = await store.PutSiteTargetsAsync(tenant, brand, new SiteTargetsWrite(saved.ConfigVersion, [.. apiDrafts, .. AvailableApps],
            SiteTargetsScope.Monitoring), markIndividual: true);
        Assert.Equal(6, saved.Items.Count(i => i.Channel == CollectionChannel.App && i.Enabled));
        await ExecAsync(cs, """
            INSERT INTO prompt_target_scopes (tenant_id, site_id, prompt_id, target_key, created_at)
            VALUES (UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), UUID_TO_BIN(@prompt), @key, UTC_TIMESTAMP(6));
            """, ("@tenant", tenant.ToString()), ("@site", brand.ToString()), ("@prompt", prompts[1].ToString()), ("@key", CollectionIdentity.AppTargetKey("doubao")));

        // Plans (snapshot, version, next slot), selection hash, eligible targets, planned slots, billing and both estimates:
        // identical, with the App targets in the draft or not.
        var after = await ApiStateAsync(cs, database, service, tenant, brand, [.. AvailableApps, .. apiDrafts]);
        Assert.Equal(before, after);
        // implement 8.1: the API part above is unchanged, and the App part is there next to it (ApiPart strips it).
        Assert.Equal(6, (await service.EstimateTargetsAsync(tenant, brand, new MonitoringTargetsEstimateRequest([.. AvailableApps, .. apiDrafts])))
            .Channels!.App.Targets);
        Assert.Equal(before, await ApiStateAsync(cs, database, service, tenant, brand, apiDrafts));
        Assert.DoesNotContain("app_collector", after, StringComparison.Ordinal);
        Assert.DoesNotContain(CollectionIdentity.AppTargetKey("doubao"), after, StringComparison.Ordinal);
        Assert.Equal(apiRowsBefore, Legacy(saved, apiRowsOnly: true));
        // Phase 4A: the brand already monitors, so saving App targets gives it the App plan as its third schedule; the brand
        // without App targets has none. The daily plans compared above are the other two.
        Assert.Equal("3", (await RowsAsync(cs, "SELECT COUNT(*) FROM collection_schedules WHERE site_id = UUID_TO_BIN(@site);", ("@site", brand.ToString())))[0][0]);
        Assert.Equal("1", (await RowsAsync(cs, "SELECT COUNT(*) FROM collection_schedules WHERE tenant_id = UUID_TO_BIN(@tenant) AND name = @name;",
            ("@tenant", tenant.ToString()), ("@name", PromptMonitoringIdentity.AppScheduleName)))[0][0]);

        // An unavailable App platform in the draft is refused like on save; the team estimate ignores App cells as well.
        var refused = await Assert.ThrowsAsync<CollectorStoreException>(() => service.EstimateTargetsAsync(tenant, brand,
            new MonitoringTargetsEstimateRequest([ChatGpt, App("ernie")])));
        Assert.Equal(("TARGET_NOT_ALLOWED", 400), Code(refused));

        // Brand A is untouched by all of it, and by a team default that contains App cells (it is an individual brand).
        await store.PutTeamMonitoringConfigAsync(tenant, Guid.NewGuid(), new TeamMonitoringConfigWrite(0,
            [new("cloro", "chatgpt"), new("app_collector", "doubao", null, MonitoringFrequency.Weekly)]));
        Assert.Equal(plainBefore, await ApiStateAsync(cs, database, service, tenant, plain, plainDrafts));
        Assert.Equal(plainLegacy, Legacy(await store.GetSiteTargetsAsync(tenant, plain)));
        Assert.Equal(before, await ApiStateAsync(cs, database, service, tenant, brand, apiDrafts));
    });

    [AppChannelMySqlFact]
    public Task InitialInstantAndManualRunsNeverContainAppTargets() => IsolatedAsync(async (cs, seedTenant) =>
    {
        var tenant = await seedTenant();
        var site = await SeedBrandAsync(cs, tenant, "采集入口", follows: false, locale: "zh-CN", created: "2026-05-08");
        var database = new MySqlDatabase(cs);
        var store = OpenStore(database, tenant);
        // The isolated database has no Cloro key; the proxy only marks catalog rows available so the first run can start.
        var management = DispatchProxy.Create<IManagementStore, AvailableCatalogProxy>();
        ((AvailableCatalogProxy)(object)management).Inner = store;
        var service = Service(database, management, tenant);
        await SeedPromptAsync(cs, site);
        var view = await store.PutSiteTargetsAsync(tenant, site, new SiteTargetsWrite((await store.GetSiteTargetsAsync(tenant, site)).ConfigVersion,
            [ChatGpt, Gemini, App("doubao"), App("kimi")]), markIndividual: true);
        var appId = view.Items.Single(i => i.Engine == "doubao" && i.Channel == CollectionChannel.App).TargetId!.Value;
        var chatGptId = view.Items.Single(i => i.Engine == "chatgpt").TargetId!.Value;
        var prompt = (await PromptIdsAsync(cs, site))[0];

        async Task<string> ScalarAsync(string sql) => (await RowsAsync(cs, sql, ("@tenant", tenant.ToString()), ("@site", site.ToString())))[0][0]!;

        // First monitoring: the plan and the initial run cover the two API cells only.
        var started = await service.EnsureAsync(tenant, site);
        Assert.Equal(["cloro", "cloro"], started.Schedule!.Selection.Targets.Select(t => t.Provider));
        Assert.Equal("2", await ScalarAsync("SELECT total_checks FROM visibility_runs WHERE site_id = UUID_TO_BIN(@site);"));

        // Instant collection for a new prompt: again the two API cells.
        var created = await store.CreatePromptAsync(tenant, site, new PromptWrite("App 渠道之外的新提问：哪个写作工具好用？", "zh-CN"));
        var instant = Assert.IsType<InstantCollectionView>(await service.CollectNewPromptsAsync(tenant, site, [created.PromptId]));
        Assert.Equal((InstantCollectionStatus.Started, 2), (instant.Status, instant.CheckCount));
        // A scope that gains an App key adds no cell to collect.
        var current = (await store.GetPromptAsync(tenant, site, created.PromptId))!;
        Assert.Null(await service.CollectAddedTargetsAsync(tenant, site, current with { TargetScope = "custom", TargetKeys = [ChatGptKey] },
            current with { TargetScope = "custom", TargetKeys = [ChatGptKey, CollectionIdentity.AppTargetKey("doubao")] }));

        Assert.Equal("2", await ScalarAsync("SELECT COUNT(*) FROM visibility_runs WHERE tenant_id = UUID_TO_BIN(@tenant);"));
        Assert.Equal("4", await ScalarAsync("SELECT COUNT(*) FROM visibility_checks WHERE tenant_id = UUID_TO_BIN(@tenant);"));
        Assert.Equal("0", await ScalarAsync("SELECT COUNT(*) FROM visibility_checks WHERE tenant_id = UUID_TO_BIN(@tenant) AND (provider = 'app_collector' OR capture_mode = 'app');"));
        Assert.Equal("0", await ScalarAsync("""
            SELECT COUNT(*) FROM run_engines e JOIN visibility_runs r ON r.id = e.run_id
            WHERE r.tenant_id = UUID_TO_BIN(@tenant) AND (e.provider = 'app_collector' OR e.capture_mode = 'app');
            """));
        Assert.Equal("0", await ScalarAsync("SELECT COUNT(*) FROM collector_tasks WHERE tenant_id = UUID_TO_BIN(@tenant);"));
        // Phase 4A: the App plan is the one schedule that names App targets; the daily plans never do.
        Assert.Equal("0", await ScalarAsync($"SELECT COUNT(*) FROM collection_schedules WHERE site_id = UUID_TO_BIN(@site) AND name <> '{PromptMonitoringIdentity.AppScheduleName}' AND CAST(selection_snapshot AS CHAR) LIKE '%app_collector%';"));
        Assert.Equal("1", await ScalarAsync($"SELECT COUNT(*) FROM collection_schedules WHERE site_id = UUID_TO_BIN(@site) AND name = '{PromptMonitoringIdentity.AppScheduleName}';"));

        // Manual runs: an App target id is refused (alone or next to an API target); an API target id still resolves.
        foreach (var targetIds in new[] { new[] { appId }, new[] { chatGptId, appId } })
        {
            var refused = await Assert.ThrowsAsync<CollectorStoreException>(() => store.ResolveRunAsync(tenant, site,
                new CollectionSelection([prompt], null, targetIds), null));
            Assert.Equal((AppChannelDefinition.TargetNotAllowedCode, 400), Code(refused));
            var preview = await Assert.ThrowsAsync<CollectorStoreException>(() => store.CreatePreviewAsync(tenant, site,
                new CollectionSelection([prompt], null, targetIds)));
            Assert.Equal((AppChannelDefinition.TargetNotAllowedCode, 400), Code(preview));
        }
        Assert.Equal("0", await ScalarAsync("SELECT COUNT(*) FROM collection_previews WHERE tenant_id = UUID_TO_BIN(@tenant);"));
        var resolved = await store.ResolveRunAsync(tenant, site, new CollectionSelection([prompt], null, [chatGptId]), null);
        Assert.Equal(("cloro", "chatgpt"), (Assert.Single(resolved.Targets).Provider, resolved.Targets[0].Engine));

        // A preview snapshot that names an App target (it cannot be created through the API) is refused as well.
        var frozen = await store.CreatePreviewAsync(tenant, site, new CollectionSelection([prompt], null, [chatGptId]));
        await ExecAsync(cs, "UPDATE collection_previews SET selection_snapshot = JSON_SET(selection_snapshot, '$.targets[0].provider', 'app_collector') WHERE id = UUID_TO_BIN(@id);",
            ("@id", frozen.PreviewId.ToString()));
        var byPreview = await Assert.ThrowsAsync<CollectorStoreException>(() => store.ResolveRunAsync(tenant, site,
            new CollectionSelection(null, null, null), frozen.PreviewId));
        Assert.Equal((AppChannelDefinition.TargetNotAllowedCode, 400), Code(byPreview));
    });
}
