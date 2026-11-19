using System.Text.Json;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using static Jogeoai.Tests.TeamMonitoringConfigSqlTests;

namespace Jogeoai.Tests;

/// <summary>App 计划的生命周期（隔离库）：只有 API 的品牌不变、未开放的租户、暂停的跟随、App 计划自己的开关、渠道的增减、快照与版本。</summary>
public sealed partial class AppPlanSqlTests
{
    // Everything the daily (API) monitoring of one brand consists of, without ids and timestamps, so that two brands set up
    // the same way in two tenants can be compared.
    private static async Task<string> ApiOnlyStateAsync(Rig rig, Guid site, PromptMonitoringView view, SiteTargetWrite[] drafts)
    {
        var plans = await PlansAsync(rig.Cs, site);
        var runs = await RowsAsync(rig.Cs, """
            SELECT REPLACE(r.requested_by, @id, '<site>'), r.status, r.total_checks, m.estimated_credits, m.held_credits, m.status
            FROM visibility_runs r LEFT JOIN monitoring_credit_reservations m ON m.run_id = r.id
            WHERE r.site_id = UUID_TO_BIN(@site) ORDER BY r.created_at, r.id;
            """, ("@site", site.ToString()), ("@id", site.ToString("N")));
        var estimate = JsonSerializer.SerializeToNode(await rig.Service.EstimateTargetsAsync(rig.Tenant, site, new MonitoringTargetsEstimateRequest(drafts)), Api)!.AsObject();
        estimate.Remove("nextSlotAt");
        string Plan(PlanRow p) => string.Join("|", p.Name, p.Version, p.Enabled, p.Cadence, p.LocalTime, p.TimeZone, p.NextSlotAt,
            p.Selection.PromptIds.Count, p.Selection.SampleCount, JsonSerializer.Serialize(p.Selection.Targets, Web), p.Selection.PromptTargets?.Count);
        return string.Join("\n",
            "plans=" + string.Join(";", plans.Values.OrderBy(p => p.Name, StringComparer.Ordinal).Select(Plan)),
            "view=" + string.Join("|", view.PromptIds.Count, view.Schedule?.Name, view.Schedule?.Version, view.Schedule?.Enabled, view.Schedule?.LocalTime,
                view.Run?.Status, view.Run?.TotalChecks, view.AppSchedule is null ? "no-app-plan" : "app-plan"),
            "billing=" + JsonSerializer.Serialize(view.Billing, Api),
            "runs=" + string.Join(";", runs.Select(r => string.Join("|", r))),
            "targets=" + string.Join(",", await EnabledTargetsAsync(rig.Cs, site)),
            "estimate=" + estimate.ToJsonString(),
            "promptEstimate=" + JsonSerializer.Serialize(await rig.Service.EstimateAsync(rig.Tenant, site, new PromptMonitoringEstimateRequest(1)), Api),
            "footprint=" + await CollectionFootprintAsync(rig.Cs, rig.Tenant));
    }

    // 4.1b: a brand with only API targets. Pinned values, and the same brand in an open tenant and in a tenant without the
    // App channel (where every App branch is switched off) end in the same state.
    [AppChannelMySqlFact]
    public Task AnApiOnlyBrandIsUnaffectedAndNeverGetsAnAppPlan() => AppSiteTargetsSqlTests.IsolatedAsync(async (cs, seedTenant) =>
    {
        SiteTargetWrite[] drafts = [ChatGpt, GeminiTwice];
        var states = new List<string>();
        foreach (var open in new[] { false, true })
        {
            var rig = Build(cs, await seedTenant(), open);
            var site = await SeedBrandAsync(cs, rig.Tenant, "只有 API", follows: false, locale: "zh-CN", created: "2026-06-03");
            await rig.PutAsync(site, drafts);
            var prompts = await SeedPromptsAsync(cs, site, 3);
            await ScopeAsync(cs, rig.Tenant, site, prompts[2], ChatGptKey);

            var started = await rig.Service.EnsureAsync(rig.Tenant, site);
            // Pinned: the main plan row, its snapshot and selection hash, the night plan, the initial run and its reservation.
            var plans = await PlansAsync(cs, site);
            Assert.Equal(new[] { NightName, MainName }.Order(StringComparer.Ordinal), plans.Keys.Order(StringComparer.Ordinal));
            var main = plans[MainName];
            Assert.Equal((1, true, "daily", "09:01", PlanZone), (main.Version, main.Enabled, main.Cadence, main.LocalTime, main.TimeZone));
            var expectedJson = Snapshot(prompts, [ChatGptTarget, GeminiTarget], new CollectionPromptTargets(prompts[2], [ChatGptKey]));
            var expected = JsonSerializer.Deserialize<CollectionScheduleSelection>(expectedJson, Web)!;
            AssertSameJson(expectedJson, main.Snapshot);
            Assert.Equal(CollectionIdentity.SelectionHash(expected), CollectionIdentity.SelectionHash(main.Selection));
            Assert.Equal(CollectionIdentity.SelectionHash(expected), CollectionIdentity.SelectionHash(started.Schedule!.Selection));
            AssertSameJson(Snapshot(prompts[..2], [GeminiTarget]), plans[NightName].Snapshot);
            Assert.Equal((true, 1, "00:05"), (plans[NightName].Enabled, plans[NightName].Version, plans[NightName].LocalTime));
            // 2 'all' prompts × 2 cells + the custom prompt × ChatGPT = 5 checks, 50 credits reserved.
            Assert.Equal((5, prompts.Length), (started.Run!.TotalChecks, started.PromptIds.Count));
            Assert.Equal("runs=1|checks=5|providerTasks=5|collectorTasks=0|reservations=1|ledger=1|balance=950.000", await CollectionFootprintAsync(cs, rig.Tenant));
            Assert.Equal((5L, 50m, 2, 1), (started.Billing!.CheckCount, started.Billing.EstimatedCredits, started.Billing.TargetCount, started.Billing.SampleCount));
            Assert.Null(started.AppSchedule);

            // Every reconcile moment — POST, GET, a target save, a prompt-scope read — leaves the plans byte-identical and
            // adds no App plan, in the open tenant as in the closed one.
            await rig.Service.EnsureAsync(rig.Tenant, site);
            var read = await rig.Service.GetAsync(rig.Tenant, site);
            Assert.Null(read.AppSchedule);
            Assert.Equal(plans, await PlansAsync(cs, site));
            await rig.PutAsync(site, drafts);
            Assert.Equal(plans, await PlansAsync(cs, site));
            Assert.Equal(0, await CountAsync(cs, "SELECT COUNT(*) FROM collection_schedules WHERE tenant_id = UUID_TO_BIN(@tenant) AND name = @name;",
                ("@tenant", rig.Tenant.ToString()), ("@name", AppName)));
            // Instant collection of a new prompt: the two API cells, as before.
            var created = await rig.Store.CreatePromptAsync(rig.Tenant, site, new PromptWrite("只有 API 的品牌的新提问：哪个翻译工具好用？", "zh-CN"));
            var instant = Assert.IsType<InstantCollectionView>(await rig.Service.CollectNewPromptsAsync(rig.Tenant, site, [created.PromptId]));
            Assert.Equal((InstantCollectionStatus.Started, 2), (instant.Status, instant.CheckCount));
            states.Add(await ApiOnlyStateAsync(rig, site, await rig.Service.GetAsync(rig.Tenant, site), drafts));
        }
        Assert.Equal(states[0], states[1]);
        Assert.Contains("no-app-plan", states[1], StringComparison.Ordinal);
        Assert.DoesNotContain("app_collector", states[1], StringComparison.Ordinal);
    });

    // A tenant the App channel is not open for: stored App rows and a stored App plan are neither used nor touched.
    [AppChannelMySqlFact]
    public Task AClosedTenantWithStoredAppRowsGetsNoAppPlanAndBehavesAsBeforeTheAppChannel() => AppSiteTargetsSqlTests.IsolatedAsync(async (cs, seedTenant) =>
    {
        var tenant = await seedTenant();
        var (open, closed) = (Build(cs, tenant), Build(cs, tenant, open: false));
        var mixed = await SeedBrandAsync(cs, tenant, "关闭·两个渠道", follows: false, locale: "zh-CN", created: "2026-06-04");
        var appOnly = await SeedBrandAsync(cs, tenant, "关闭·只有 App", follows: false, locale: "en-GB", created: "2026-06-05");
        // The rows are stored while the tenant is open; then the tenant is closed (the closed rig).
        await open.PutAsync(mixed, ChatGpt, App("doubao"));
        await open.PutAsync(appOnly, App("doubao"), App("kimi"));
        var prompts = await SeedPromptsAsync(cs, mixed, 1);
        await SeedPromptsAsync(cs, appOnly, 1);

        var started = await closed.Service.EnsureAsync(tenant, mixed);
        Assert.Null(started.AppSchedule);
        var main = Assert.Single(await PlansAsync(cs, mixed)).Value;
        Assert.Equal(MainName, main.Name);
        AssertSameJson(Snapshot(prompts, [ChatGptTarget]), main.Snapshot);
        // A target save and a read in the closed tenant: still the main plan alone, the stored App row untouched.
        await closed.PutAsync(mixed, ChatGpt);
        Assert.Null((await closed.Service.GetAsync(tenant, mixed)).AppSchedule);
        Assert.Equal(main, Assert.Single(await PlansAsync(cs, mixed)).Value);
        Assert.Equal(["app_collector:doubao", "cloro:chatgpt"], await EnabledTargetsAsync(cs, mixed));

        // The brand whose only stored targets are App rows reads as a brand without targets, exactly as before this task:
        // the first start writes the ChatGPT baseline and creates the main plan. No App plan.
        var baseline = await closed.Service.EnsureAsync(tenant, appOnly);
        Assert.Equal((MainName, null), (baseline.Schedule!.Name, baseline.AppSchedule));
        Assert.Equal(MainName, Assert.Single(await PlansAsync(cs, appOnly)).Key);
        Assert.Equal(["app_collector:doubao", "app_collector:kimi", "cloro:chatgpt"], await EnabledTargetsAsync(cs, appOnly));
        // The estimate of an App-only draft is refused for the closed tenant, as before.
        var refused = await Assert.ThrowsAsync<CollectorStoreException>(() => closed.Service.EstimateTargetsAsync(tenant, appOnly,
            new MonitoringTargetsEstimateRequest([App("doubao")])));
        Assert.Equal(("TARGET_NOT_ALLOWED", 400), Code(refused));

        // An App plan stored while the tenant was open is invisible to the closed tenant and is not reconciled by it.
        await open.Service.GetAsync(tenant, mixed);
        var stored = (await PlansAsync(cs, mixed))[AppName];
        var closedView = await closed.Service.GetAsync(tenant, mixed);
        Assert.Equal((main.Id, null), (closedView.Schedule!.Id, closedView.AppSchedule));
        await closed.Schedules.UpdateAsync(new UpdateCollectionScheduleCommand(tenant, mixed, main.Id, main.Version, Enabled: false));
        await closed.Service.GetAsync(tenant, mixed);
        await closed.Service.EnsureAsync(tenant, mixed);
        await closed.PutAsync(mixed, ChatGpt);
        Assert.Equal(stored, (await PlansAsync(cs, mixed))[AppName]);
    });

    // Pause is one switch: with a main plan the App plan follows it at every reconcile moment.
    [AppChannelMySqlFact]
    public Task TheAppPlanFollowsThePauseOfTheMainPlan() => AppSiteTargetsSqlTests.IsolatedAsync(async (cs, seedTenant) =>
    {
        var rig = Build(cs, await seedTenant());
        var site = await SeedBrandAsync(cs, rig.Tenant, "暂停跟随", follows: false, locale: "zh-CN", created: "2026-06-06");
        await rig.PutAsync(site, ChatGpt, App("doubao"));
        await SeedPromptsAsync(cs, site, 1);
        var started = await rig.Service.EnsureAsync(rig.Tenant, site);
        var (mainId, appId) = (started.Schedule!.Id, started.AppSchedule!.Id);
        var app = (await PlansAsync(cs, site))[AppName];

        // With a main plan the App plan cannot be paused on its own: the request is refused and nothing changes.
        var refused = await Assert.ThrowsAsync<CollectorStoreException>(() => rig.Schedules.UpdateAsync(
            new UpdateCollectionScheduleCommand(rig.Tenant, site, appId, app.Version, Enabled: false)));
        Assert.Equal(("APP_SCHEDULE_FOLLOWS_MAIN", 409), Code(refused));
        Assert.Equal(app, (await PlansAsync(cs, site))[AppName]);

        async Task<int> MainVersionAsync() => (await PlansAsync(cs, site))[MainName].Version;
        async Task SetMainAsync(bool enabled)
            => await rig.Schedules.UpdateAsync(new UpdateCollectionScheduleCommand(rig.Tenant, site, mainId, await MainVersionAsync(), Enabled: enabled));

        // Pausing the main plan through the collection-schedules API does not reconcile by itself; the next reconcile
        // moment does. Each of the three moments is checked: GET, POST, a target save.
        foreach (var reconcile in new Func<Task>[]
        {
            () => rig.Service.GetAsync(rig.Tenant, site),
            () => rig.Service.EnsureAsync(rig.Tenant, site),
            () => rig.PutAsync(site, ChatGpt, App("doubao")),
        })
        {
            var before = (await PlansAsync(cs, site))[AppName];
            Assert.True(before.Enabled);
            await SetMainAsync(false);
            Assert.Equal(before, (await PlansAsync(cs, site))[AppName]);
            await reconcile();
            var paused = (await PlansAsync(cs, site))[AppName];
            Assert.Equal((false, null, before.Version + 1, before.Snapshot), (paused.Enabled, paused.NextSlotAt, paused.Version, paused.Snapshot));
            // Reconciling again while paused changes nothing.
            await rig.Service.GetAsync(rig.Tenant, site);
            Assert.Equal(paused, (await PlansAsync(cs, site))[AppName]);
            Assert.False((await rig.Service.GetAsync(rig.Tenant, site)).AppSchedule!.Enabled);

            // Resume: enabled again, starting at the next 09:11.
            await SetMainAsync(true);
            Assert.False((await PlansAsync(cs, site))[AppName].Enabled);
            await reconcile();
            var resumed = (await PlansAsync(cs, site))[AppName];
            Assert.Equal((true, NextAppSlot(), paused.Version + 1, before.Snapshot, appId),
                (resumed.Enabled, resumed.NextSlotAt, resumed.Version, resumed.Snapshot, resumed.Id));
        }
        // No run beyond the API initial run was created by any of it.
        Assert.StartsWith("runs=1|checks=1|providerTasks=1|collectorTasks=0|reservations=1|", await CollectionFootprintAsync(cs, rig.Tenant), StringComparison.Ordinal);
    });

    // Without a main plan the App plan's own flag is the switch; no reconcile flips it. Removing every App target leaves
    // the plan and its flag alone. Only the flag is editable through the collection-schedules API.
    [AppChannelMySqlFact]
    public Task AnAppOnlyBrandPausesThroughItsOwnFlagAndReconcileNeverFlipsIt() => AppSiteTargetsSqlTests.IsolatedAsync(async (cs, seedTenant) =>
    {
        var rig = Build(cs, await seedTenant());
        var site = await SeedBrandAsync(cs, rig.Tenant, "App 自己的开关", follows: false, locale: "zh-CN", created: "2026-06-07");
        await rig.PutAsync(site, App("doubao"));
        var prompts = await SeedPromptsAsync(cs, site, 1);
        var plan = (await rig.Service.EnsureAsync(rig.Tenant, site)).AppSchedule!;

        // Everything but the flag is refused; a stale version is a conflict. Nothing changes.
        var row = (await PlansAsync(cs, site))[AppName];
        foreach (var command in new[]
        {
            new UpdateCollectionScheduleCommand(rig.Tenant, site, plan.Id, plan.Version, Name: "renamed"),
            new UpdateCollectionScheduleCommand(rig.Tenant, site, plan.Id, plan.Version, LocalTime: "10:00"),
            new UpdateCollectionScheduleCommand(rig.Tenant, site, plan.Id, plan.Version, Cadence: "weekly", DayOfWeek: 1),
            new UpdateCollectionScheduleCommand(rig.Tenant, site, plan.Id, plan.Version, TimeZone: "UTC"),
            new UpdateCollectionScheduleCommand(rig.Tenant, site, plan.Id, plan.Version, Selection: new(prompts, [ChatGptTarget])),
        })
            Assert.Equal(("INVALID_SCHEDULE", 400), Code(await Assert.ThrowsAsync<CollectorStoreException>(() => rig.Schedules.UpdateAsync(command))));
        Assert.Equal(("SCHEDULE_VERSION_CONFLICT", 409), Code(await Assert.ThrowsAsync<CollectorStoreException>(() => rig.Schedules.UpdateAsync(
            new UpdateCollectionScheduleCommand(rig.Tenant, site, plan.Id, plan.Version + 5, Enabled: false)))));
        // The name is reserved: no plan can be created with it or renamed to it.
        var selection = new CollectionScheduleSelection(prompts, [ChatGptTarget]);
        Assert.Equal(("INVALID_SCHEDULE_NAME", 400), Code(await Assert.ThrowsAsync<CollectorStoreException>(() => rig.Schedules.CreateAsync(
            new CreateCollectionScheduleCommand(rig.Tenant, site, AppName, "daily", null, "09:11", PlanZone, selection)))));
        Assert.Equal(row, Assert.Single(await PlansAsync(cs, site)).Value);

        // Pause through the existing endpoint's store call.
        var paused = (await rig.Schedules.UpdateAsync(new UpdateCollectionScheduleCommand(rig.Tenant, site, plan.Id, plan.Version, Enabled: false)))!;
        Assert.Equal((false, null, plan.Version + 1, AppName), (paused.Enabled, paused.NextSlotAt, paused.Version, paused.Name));
        var stored = (await PlansAsync(cs, site))[AppName];
        Assert.Equal((false, null, 2, row.Snapshot), (stored.Enabled, stored.NextSlotAt, stored.Version, stored.Snapshot));

        // GET and POST: the flag survives and nothing is rewritten.
        Assert.False((await rig.Service.GetAsync(rig.Tenant, site)).AppSchedule!.Enabled);
        Assert.False((await rig.Service.EnsureAsync(rig.Tenant, site)).AppSchedule!.Enabled);
        Assert.Equal(stored, Assert.Single(await PlansAsync(cs, site)).Value);
        // A target save that changes the snapshot: the snapshot follows, the flag and the missing next slot stay.
        await rig.PutAsync(site, App("doubao"), App("kimi"));
        var widened = Assert.Single(await PlansAsync(cs, site)).Value;
        Assert.Equal((false, null, 3), (widened.Enabled, widened.NextSlotAt, widened.Version));
        AssertSameJson(Snapshot(prompts, [AppTarget("doubao"), AppTarget("kimi")]), widened.Snapshot);

        // Resume: the next 09:11.
        var resumed = (await rig.Schedules.UpdateAsync(new UpdateCollectionScheduleCommand(rig.Tenant, site, plan.Id, widened.Version, Enabled: true)))!;
        Assert.True(resumed.Enabled);
        Assert.Equal((true, NextAppSlot(), 4), ((await PlansAsync(cs, site))[AppName].Enabled, (await PlansAsync(cs, site))[AppName].NextSlotAt, resumed.Version));

        // Removing every App target: the plan stays, enabled as it was, with an empty target list — once.
        await rig.PutAsync(site, App("doubao", enabled: false), App("kimi", enabled: false));
        var emptied = Assert.Single(await PlansAsync(cs, site)).Value;
        Assert.Equal((true, NextAppSlot(), 5, plan.Id), (emptied.Enabled, emptied.NextSlotAt, emptied.Version, emptied.Id));
        Assert.Empty(emptied.Selection.Targets);
        await rig.Service.GetAsync(rig.Tenant, site);
        var again = await rig.Service.EnsureAsync(rig.Tenant, site);
        Assert.Equal((null, plan.Id, true), (again.Schedule, again.AppSchedule!.Id, again.AppSchedule.Enabled));
        Assert.Equal(emptied, Assert.Single(await PlansAsync(cs, site)).Value);
        // Still no baseline target and no main plan for the brand that started with the App channel alone.
        Assert.Empty(await EnabledTargetsAsync(cs, site));
        // The same while paused: the flag stays off.
        await rig.Schedules.UpdateAsync(new UpdateCollectionScheduleCommand(rig.Tenant, site, plan.Id, emptied.Version, Enabled: false));
        await rig.PutAsync(site, App("doubao"));
        await rig.Service.GetAsync(rig.Tenant, site);
        var pausedAgain = Assert.Single(await PlansAsync(cs, site)).Value;
        Assert.Equal((false, null), (pausedAgain.Enabled, pausedAgain.NextSlotAt));
        AssertSameJson(Snapshot(prompts, [AppTarget("doubao")]), pausedAgain.Snapshot);

        // A paused App-only brand that enables an API target gets its main plan paused as well, without an initial run.
        await rig.PutAsync(site, App("doubao"), ChatGpt);
        var both = await rig.Service.EnsureAsync(rig.Tenant, site);
        Assert.Equal((false, false, null), (both.Schedule!.Enabled, both.AppSchedule!.Enabled, both.Run));
        Assert.Equal(NoCollection, await CollectionFootprintAsync(cs, rig.Tenant));
    });

    // KD-24: a channel can be added or removed later without affecting the other one.
    [AppChannelMySqlFact]
    public Task AddingAndRemovingTheApiChannelLeavesTheAppPlanWorking() => AppSiteTargetsSqlTests.IsolatedAsync(async (cs, seedTenant) =>
    {
        var rig = Build(cs, await seedTenant());
        var site = await SeedBrandAsync(cs, rig.Tenant, "渠道增减", follows: false, locale: "zh-CN", created: "2026-06-08");
        await rig.PutAsync(site, App("doubao"));
        var prompts = await SeedPromptsAsync(cs, site, 2);
        var app = (await rig.Service.EnsureAsync(rig.Tenant, site)).AppSchedule!;
        var appRow = (await PlansAsync(cs, site))[AppName];

        // The brand enables an API target. Neither the target save nor a read creates the main plan...
        await rig.PutAsync(site, App("doubao"), ChatGpt);
        Assert.Equal(AppName, Assert.Single(await PlansAsync(cs, site)).Key);
        var read = await rig.Service.GetAsync(rig.Tenant, site);
        Assert.Equal((null, app.Id), (read.Schedule, read.AppSchedule!.Id));
        Assert.Equal(AppName, Assert.Single(await PlansAsync(cs, site)).Key);
        Assert.Equal(NoCollection, await CollectionFootprintAsync(cs, rig.Tenant));
        // ...the next POST prompt-monitoring does: the main plan with its initial run, exactly as a first start. The brand
        // keeps its own targets (no baseline, no team default) and the App plan row is the same row, unchanged.
        var started = await rig.Service.EnsureAsync(rig.Tenant, site);
        var plans = await PlansAsync(cs, site);
        Assert.Equal(new[] { AppName, MainName }.Order(StringComparer.Ordinal), plans.Keys.Order(StringComparer.Ordinal));
        Assert.Equal((1, true, "09:01"), (plans[MainName].Version, plans[MainName].Enabled, plans[MainName].LocalTime));
        AssertSameJson(Snapshot(prompts, [ChatGptTarget]), plans[MainName].Snapshot);
        Assert.Equal(appRow, plans[AppName]);
        Assert.Equal((plans[MainName].Id, app.Id, 2), (started.Schedule!.Id, started.AppSchedule!.Id, started.Run!.TotalChecks));
        Assert.Equal("runs=1|checks=2|providerTasks=2|collectorTasks=0|reservations=1|ledger=1|balance=980.000", await CollectionFootprintAsync(cs, rig.Tenant));
        Assert.Equal(["app_collector:doubao", "cloro:chatgpt"], await EnabledTargetsAsync(cs, site));

        // All API targets are switched off again: the App plan is the same enabled row with the same snapshot, and its
        // due slot is still planned (the main plan keeps its existing behaviour: an empty selection that skips its slots).
        await rig.PutAsync(site, App("doubao"), ChatGpt with { Enabled = false });
        await rig.Service.GetAsync(rig.Tenant, site);
        plans = await PlansAsync(cs, site);
        Assert.Equal(appRow, plans[AppName]);
        Assert.Empty(plans[MainName].Selection.Targets);
        await ExecAsync(cs, "UPDATE site_targets SET frequency_anchor = '2000-01-03' WHERE site_id = UUID_TO_BIN(@site) AND provider = 'app_collector';",
            ("@site", site.ToString()));
        await ExecAsync(cs, "UPDATE collection_schedules SET next_slot_at = '2000-01-03 01:11:00' WHERE id = UUID_TO_BIN(@id);", ("@id", app.Id.ToString()));
        Assert.Equal(1, await rig.Schedules.MaterializeDueSlotsAsync(new DateTimeOffset(2000, 1, 3, 1, 12, 0, TimeSpan.Zero), 1));
        var slot = await SlotAsync(cs, app.Id, "20000103T0911");
        Assert.Equal(("pending", null), (slot.Status, slot.SkipCode));
        AssertSameJson(Snapshot(prompts, [AppTarget("doubao")]), slot.Snapshot);
        await ParkSlotsAsync(cs, site);
        Assert.StartsWith("runs=1|checks=2|", await CollectionFootprintAsync(cs, rig.Tenant), StringComparison.Ordinal);
    });

    // The snapshot: App cells and enabled prompts with their scopes; the version moves only when the content does.
    [AppChannelMySqlFact]
    public Task TheSnapshotFollowsPromptsScopesAndAppTargetsAndOnlyVersionsOnChange() => AppSiteTargetsSqlTests.IsolatedAsync(async (cs, seedTenant) =>
    {
        var rig = Build(cs, await seedTenant());
        var site = await SeedBrandAsync(cs, rig.Tenant, "快照", follows: false, locale: "zh-CN", created: "2026-06-09");
        await rig.PutAsync(site, ChatGpt, App("doubao"), App("kimi"), App("qwen", enabled: false));
        var prompts = await SeedPromptsAsync(cs, site, 4);
        var (all, mixed, apiOnly, disabledApp) = (prompts[0], prompts[1], prompts[2], prompts[3]);
        await ScopeAsync(cs, rig.Tenant, site, mixed, ChatGptKey, AppKey("kimi"));
        await ScopeAsync(cs, rig.Tenant, site, apiOnly, ChatGptKey);
        await ScopeAsync(cs, rig.Tenant, site, disabledApp, AppKey("qwen"));
        await rig.Service.EnsureAsync(rig.Tenant, site);

        // 'all' takes both App cells, the mixed scope its App key; the API-only scope and the scope on a disabled App
        // target are not part of the App snapshot. No API key or target appears in it.
        var first = (await PlansAsync(cs, site))[AppName];
        AssertSameJson(Snapshot([all, mixed], [AppTarget("doubao"), AppTarget("kimi")], new CollectionPromptTargets(mixed, [AppKey("kimi")])), first.Snapshot);
        Assert.DoesNotContain(ChatGptKey, first.Snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("cloro", first.Snapshot, StringComparison.Ordinal);
        Assert.Equal(1, first.Version);
        // The main plan is the mirror image: the API cell only, the prompt scoped to App keys only is not in it.
        var main = (await PlansAsync(cs, site))[MainName];
        AssertSameJson(Snapshot([all, mixed, apiOnly], [ChatGptTarget], new CollectionPromptTargets(mixed, [ChatGptKey]), new CollectionPromptTargets(apiOnly, [ChatGptKey])),
            main.Snapshot);

        // Reconciling without a change (GET, POST, saving the same targets) writes nothing: version and updated_at stay.
        await rig.Service.GetAsync(rig.Tenant, site);
        await rig.Service.EnsureAsync(rig.Tenant, site);
        await rig.PutAsync(site, ChatGpt, App("doubao"), App("kimi"), App("qwen", enabled: false));
        Assert.Equal(first, (await PlansAsync(cs, site))[AppName]);

        // Enabling the third App target: one new version; the prompt scoped to it joins.
        await rig.PutAsync(site, ChatGpt, App("doubao"), App("kimi"), App("qwen"));
        var second = (await PlansAsync(cs, site))[AppName];
        Assert.Equal((2, first.Id, first.NextSlotAt, true), (second.Version, second.Id, second.NextSlotAt, second.Enabled));
        AssertSameJson(Snapshot([all, mixed, disabledApp], [AppTarget("doubao"), AppTarget("kimi"), AppTarget("qwen")],
            new CollectionPromptTargets(mixed, [AppKey("kimi")]), new CollectionPromptTargets(disabledApp, [AppKey("qwen")])), second.Snapshot);

        // A new prompt joins at the next lazy reconcile; a disabled prompt leaves.
        await SeedPromptAsync(cs, site);
        var added = (await PromptIdsAsync(cs, site)).Single(id => !prompts.Contains(id));
        await ExecAsync(cs, "UPDATE prompts SET enabled = FALSE WHERE id = UUID_TO_BIN(@prompt);", ("@prompt", all.ToString()));
        await rig.Service.GetAsync(rig.Tenant, site);
        var third = (await PlansAsync(cs, site))[AppName];
        Assert.Equal(3, third.Version);
        AssertSameJson(Snapshot([added, mixed, disabledApp], [AppTarget("doubao"), AppTarget("kimi"), AppTarget("qwen")],
            new CollectionPromptTargets(mixed, [AppKey("kimi")]), new CollectionPromptTargets(disabledApp, [AppKey("qwen")])), third.Snapshot);
        await rig.Service.GetAsync(rig.Tenant, site);
        Assert.Equal(third, (await PlansAsync(cs, site))[AppName]);
        // The initial API run is the only collection of the whole case.
        Assert.StartsWith("runs=1|", await CollectionFootprintAsync(cs, rig.Tenant), StringComparison.Ordinal);
    });
}
