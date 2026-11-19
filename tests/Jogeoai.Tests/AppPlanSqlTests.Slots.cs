using System.Text.Json;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using static Jogeoai.Tests.TeamMonitoringConfigSqlTests;

namespace Jogeoai.Tests;

/// <summary>
/// App 计划的时隙保护（隔离库）：App 计划的时隙在物化时只会被跳过或保持待处理；由未开放 App 渠道的进程处理时不产生 Run、检测、Provider 任务、
/// 采集任务或积分预留。到期时隙展开成周期 Run（阶段 4B）见 AppCycleRunSqlTests；这里只核对它不进入 API 的 Run 路径、不预留积分，
/// 同一个库里用户自建的计划和主计划照常处理。时隙用 2000 年的日期，排在库里其他到期计划之前。
/// </summary>
public sealed partial class AppPlanSqlTests
{
    private sealed record SlotRow(string Status, string? SkipCode, string? SkipMessage, string? RunId, string Snapshot);

    private static async Task<SlotRow> SlotAsync(string cs, Guid schedule, string key)
    {
        var row = Assert.Single(await RowsAsync(cs, """
            SELECT status, skip_code, skip_message, BIN_TO_UUID(run_id), CAST(selection_snapshot AS CHAR) FROM schedule_slots
            WHERE schedule_id = UUID_TO_BIN(@schedule) AND local_slot_key = @key;
            """, ("@schedule", schedule.ToString()), ("@key", key)));
        return new(row[0]!, row[1], row[2], row[3], row[4]!);
    }

    private static Task ParkSlotsAsync(string cs, Guid site)
        => ExecAsync(cs, """
            UPDATE schedule_slots s JOIN collection_schedules p ON p.id = s.schedule_id SET s.status = 'skipped', s.skip_code = 'TEST_PARKED'
            WHERE p.site_id = UUID_TO_BIN(@site) AND s.status = 'pending';
            """, ("@site", site.ToString()));

    private static Task DueAtAsync(string cs, Guid schedule, string utc)
        => ExecAsync(cs, "UPDATE collection_schedules SET next_slot_at = @at WHERE id = UUID_TO_BIN(@id);", ("@id", schedule.ToString()), ("@at", utc));

    private static Task AnchorAsync(string cs, Guid site, string engine, string date)
        => ExecAsync(cs, "UPDATE site_targets SET frequency_anchor = @date WHERE site_id = UUID_TO_BIN(@site) AND provider = 'app_collector' AND engine = @engine;",
            ("@site", site.ToString()), ("@engine", engine), ("@date", date));

    private static DateTimeOffset Utc(int day, int hour, int minute) => new(2000, 1, day, hour, minute, 0, TimeSpan.Zero);

    // The three decisions of an App slot — tenant not open, nothing due, something due — and the paused main plan.
    // Materialization never creates anything, and a due slot processed by a worker the tenant is not open for is consumed
    // without a run, a check, a provider task, a collector task, a reservation or a ledger entry.
    [AppChannelMySqlFact]
    public Task AnAppSlotIsSkippedOrLeftPendingAndNeverCreatesARunOrAReservation() => AppSiteTargetsSqlTests.IsolatedAsync(async (cs, seedTenant) =>
    {
        var rig = Build(cs, await seedTenant());
        var closed = Build(cs, rig.Tenant, open: false);
        var site = await SeedBrandAsync(cs, rig.Tenant, "App 时隙", follows: false, locale: "zh-CN", created: "2026-06-10");
        await rig.PutAsync(site, App("doubao"), App("kimi"));
        var prompts = await SeedPromptsAsync(cs, site, 2);
        await ScopeAsync(cs, rig.Tenant, site, prompts[1], AppKey("kimi"));
        var plan = (await rig.Service.EnsureAsync(rig.Tenant, site)).AppSchedule!;
        // doubao's weekly cycle starts on Monday 2000-01-03, kimi's on Tuesday 2000-01-04. 09:11 Asia/Shanghai = 01:11 UTC.
        await AnchorAsync(cs, site, "doubao", "2000-01-03");
        await AnchorAsync(cs, site, "kimi", "2000-01-04");
        await DueAtAsync(cs, plan.Id, "2000-01-03 01:11:00");
        var row = (await PlansAsync(cs, site))[AppName];

        async Task<PlanRow> PlanAfterSlotAsync(string nextSlot)
        {
            // Materialization only moves the next slot: the plan snapshot, its version and its flag are untouched.
            var after = (await PlansAsync(cs, site))[AppName];
            Assert.Equal(row with { NextSlotAt = nextSlot, UpdatedAt = after.UpdatedAt }, after);
            Assert.Equal(NoCollection, await CollectionFootprintAsync(cs, rig.Tenant));
            return after;
        }

        // Monday, open tenant, doubao due: the slot is left pending with the due pairs frozen ('all' prompt × doubao; the
        // prompt scoped to kimi has nothing due). Processed by a store without the App options it is skipped as not open.
        Assert.Equal(1, await rig.Schedules.MaterializeDueSlotsAsync(Utc(3, 1, 12), 1));
        var monday = await SlotAsync(cs, plan.Id, "20000103T0911");
        Assert.Equal(("pending", null, null), (monday.Status, monday.SkipCode, monday.RunId));
        AssertSameJson(Snapshot([prompts[0]], [AppTarget("doubao")]), monday.Snapshot);
        await PlanAfterSlotAsync("2000-01-04 01:11:00");
        var pending = Assert.Single(await rig.Schedules.GetPendingSlotsAsync(100), s => s.ScheduleId == plan.Id);
        Assert.False(await closed.Schedules.ProcessSlotAsync(pending, closed.Runs));
        monday = await SlotAsync(cs, plan.Id, "20000103T0911");
        Assert.Equal(("skipped", "APP_CHANNEL_NOT_OPEN", null), (monday.Status, monday.SkipCode, monday.RunId));
        Assert.False(string.IsNullOrWhiteSpace(monday.SkipMessage));
        // A second pass finds nothing to process, with the open store or a store without the App options.
        Assert.DoesNotContain(await rig.Schedules.GetPendingSlotsAsync(100), s => s.ScheduleId == plan.Id);
        Assert.False(await closed.Schedules.ProcessSlotAsync(pending, closed.Runs));
        Assert.False(await rig.Schedules.ProcessSlotAsync(pending, rig.Runs));
        await PlanAfterSlotAsync("2000-01-04 01:11:00");

        // Tuesday: kimi is due, for both prompts (the 'all' prompt and the one scoped to it).
        Assert.Equal(1, await rig.Schedules.MaterializeDueSlotsAsync(Utc(4, 1, 12), 1));
        var tuesday = await SlotAsync(cs, plan.Id, "20000104T0911");
        Assert.Equal(("pending", null), (tuesday.Status, tuesday.SkipCode));
        AssertSameJson(Snapshot(prompts, [AppTarget("kimi")], new CollectionPromptTargets(prompts[1], [AppKey("kimi")])), tuesday.Snapshot);
        // A pending App slot processed by a store without the App options is consumed the same way, never as an API run.
        Assert.False(await closed.Schedules.ProcessSlotAsync(Assert.Single(await closed.Schedules.GetPendingSlotsAsync(100), s => s.ScheduleId == plan.Id), closed.Runs));
        Assert.Equal(("skipped", "APP_CHANNEL_NOT_OPEN"), ((await SlotAsync(cs, plan.Id, "20000104T0911")).Status, (await SlotAsync(cs, plan.Id, "20000104T0911")).SkipCode));
        await PlanAfterSlotAsync("2000-01-05 01:11:00");

        // Wednesday: no App target is due.
        Assert.Equal(1, await rig.Schedules.MaterializeDueSlotsAsync(Utc(5, 1, 12), 1));
        var wednesday = await SlotAsync(cs, plan.Id, "20000105T0911");
        Assert.Equal(("skipped", "MONITORING_NOT_DUE", null), (wednesday.Status, wednesday.SkipCode, wednesday.RunId));
        Assert.Contains("App", wednesday.SkipMessage, StringComparison.Ordinal);
        await PlanAfterSlotAsync("2000-01-06 01:11:00");

        // The tenant is not open (the Worker has no Collector:AppTenantIds entry for it): skipped before anything is read,
        // also on a day a target would be due (next Monday). The slot freezes the stored plan snapshot.
        await DueAtAsync(cs, plan.Id, "2000-01-10 01:11:00");
        Assert.Equal(1, await closed.Schedules.MaterializeDueSlotsAsync(Utc(10, 1, 12), 1));
        var notOpen = await SlotAsync(cs, plan.Id, "20000110T0911");
        Assert.Equal(("skipped", "APP_CHANNEL_NOT_OPEN", null), (notOpen.Status, notOpen.SkipCode, notOpen.RunId));
        AssertSameJson(row.Snapshot, notOpen.Snapshot);
        Assert.Contains("开放", notOpen.SkipMessage, StringComparison.Ordinal);
        await PlanAfterSlotAsync("2000-01-11 01:11:00");

        // Every prompt scoped away from the due target: nothing to collect, skipped as an empty selection.
        await ScopeAsync(cs, rig.Tenant, site, prompts[0], AppKey("kimi"));
        await DueAtAsync(cs, plan.Id, "2000-01-17 01:11:00");
        Assert.Equal(1, await rig.Schedules.MaterializeDueSlotsAsync(Utc(17, 1, 12), 1));
        Assert.Equal(("skipped", "MONITORING_SELECTION_EMPTY"), ((await SlotAsync(cs, plan.Id, "20000117T0911")).Status, (await SlotAsync(cs, plan.Id, "20000117T0911")).SkipCode));

        // Five slots, none linked to a run; nothing was collected or reserved at any point.
        Assert.Equal(5, await CountAsync(cs, "SELECT COUNT(*) FROM schedule_slots WHERE schedule_id = UUID_TO_BIN(@id) AND run_id IS NULL AND status = 'skipped';",
            ("@id", plan.Id.ToString())));
        Assert.Equal(NoCollection, await CollectionFootprintAsync(cs, rig.Tenant));
    });

    // 4.1c: registered and bound the way the Worker's Program.cs does it, the schedule store receives the tenant list — the
    // listed tenant's due App slot is planned, and without the configuration key the same slot is skipped as not open.
    [AppChannelMySqlFact]
    public Task TheWorkerRegistrationHandsTheTenantListToTheScheduleStore() => AppSiteTargetsSqlTests.IsolatedAsync(async (cs, seedTenant) =>
    {
        var rig = Build(cs, await seedTenant());
        var site = await SeedBrandAsync(cs, rig.Tenant, "Worker 绑定", follows: false, locale: "zh-CN", created: "2026-06-12");
        await rig.PutAsync(site, App("doubao"));
        await SeedPromptsAsync(cs, site, 1);
        var plan = (await rig.Service.EnsureAsync(rig.Tenant, site)).AppSchedule!;
        await AnchorAsync(cs, site, "doubao", "2000-01-03");

        ICollectionScheduleStore Resolve(Dictionary<string, string?> values)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            var services = new ServiceCollection();
            services.AddSingleton(new MySqlDatabase(cs));
            services.Configure<AppChannelOptions>(options =>
                options.AppTenantIds = configuration.GetSection(AppChannelOptions.TenantIdsKey).Get<string[]>() ?? []);
            services.AddSingleton<ICollectionScheduleStore, CollectionScheduleStore>();
            return services.BuildServiceProvider().GetRequiredService<ICollectionScheduleStore>();
        }

        await DueAtAsync(cs, plan.Id, "2000-01-03 01:11:00");
        Assert.Equal(1, await Resolve(new() { ["Collector:AppTenantIds:0"] = rig.Tenant.ToString() }).MaterializeDueSlotsAsync(Utc(3, 1, 12), 1));
        Assert.Equal(("pending", null), ((await SlotAsync(cs, plan.Id, "20000103T0911")).Status, (await SlotAsync(cs, plan.Id, "20000103T0911")).SkipCode));
        await DueAtAsync(cs, plan.Id, "2000-01-10 01:11:00");
        Assert.Equal(1, await Resolve(new() { ["Collector:ChinaTenantIds:0"] = rig.Tenant.ToString() }).MaterializeDueSlotsAsync(Utc(10, 1, 12), 1));
        Assert.Equal(("skipped", "APP_CHANNEL_NOT_OPEN"), ((await SlotAsync(cs, plan.Id, "20000110T0911")).Status, (await SlotAsync(cs, plan.Id, "20000110T0911")).SkipCode));
        await ParkSlotsAsync(cs, site);
        Assert.Equal(NoCollection, await CollectionFootprintAsync(cs, rig.Tenant));
    });

    // One worker pass over a brand that has a user's own plan, the main plan and the App plan due at the same time: the
    // first two create their runs as before (the main plan charged, the custom plan not); the App slot creates its cycle
    // run without a provider task or a reservation.
    [AppChannelMySqlFact]
    public Task CustomAndMainPlansProcessAsBeforeNextToAnAppPlan() => AppSiteTargetsSqlTests.IsolatedAsync(async (cs, seedTenant) =>
    {
        var rig = Build(cs, await seedTenant());
        var site = await SeedBrandAsync(cs, rig.Tenant, "三个计划", follows: false, locale: "zh-CN", created: "2026-06-11");
        await rig.PutAsync(site, ChatGpt, App("doubao"));
        var prompts = await SeedPromptsAsync(cs, site, 2);
        var started = await rig.Service.EnsureAsync(rig.Tenant, site);
        var (mainId, appId) = (started.Schedule!.Id, started.AppSchedule!.Id);
        var custom = await rig.Schedules.CreateAsync(new CreateCollectionScheduleCommand(rig.Tenant, site, "用户自建计划", "daily", null, "10:00", PlanZone,
            new CollectionScheduleSelection([prompts[0]], [ChatGptTarget], 2), Enabled: true));
        Assert.Equal("runs=1|checks=2|providerTasks=2|collectorTasks=0|reservations=1|ledger=1|balance=980.000", await CollectionFootprintAsync(cs, rig.Tenant));

        // Backdate: targets active before the year-2000 slots; doubao's cycle starts on Monday 2000-01-03.
        await ExecAsync(cs, "UPDATE site_targets SET monitoring_enabled_at = '1999-01-01' WHERE site_id = UUID_TO_BIN(@site);", ("@site", site.ToString()));
        await AnchorAsync(cs, site, "doubao", "2000-01-03");
        await DueAtAsync(cs, mainId, "2000-01-03 01:01:00");
        await DueAtAsync(cs, appId, "2000-01-03 01:11:00");
        await DueAtAsync(cs, custom.Id, "2000-01-03 02:00:00");
        var before = await PlansAsync(cs, site);

        Assert.Equal(3, await rig.Schedules.MaterializeDueSlotsAsync(Utc(3, 3, 0), 10));
        var slots = (await rig.Schedules.GetPendingSlotsAsync(100)).Where(s => new[] { mainId, appId, custom.Id }.Contains(s.ScheduleId)).ToArray();
        Assert.Equal(3, slots.Length);
        // The frozen requests: the main slot the API cell only, the App slot the App cell only, the custom slot its own selection.
        AssertSameJson(Snapshot(prompts, [ChatGptTarget]), (await SlotAsync(cs, mainId, "20000103T0901")).Snapshot);
        AssertSameJson(Snapshot(prompts, [AppTarget("doubao")]), (await SlotAsync(cs, appId, "20000103T0911")).Snapshot);
        AssertSameJson(JsonSerializer.Serialize(custom.Selection, Web), (await SlotAsync(cs, custom.Id, "20000103T1000")).Snapshot);
        // Materialization left all three plan snapshots and versions as they were.
        var after = await PlansAsync(cs, site);
        foreach (var name in before.Keys)
            Assert.Equal((before[name].Snapshot, before[name].Version, before[name].Enabled), (after[name].Snapshot, after[name].Version, after[name].Enabled));

        var results = new Dictionary<Guid, bool>();
        foreach (var slot in slots) results[slot.ScheduleId] = await rig.Schedules.ProcessSlotAsync(slot, rig.Runs);
        Assert.Equal((true, true, true), (results[mainId], results[custom.Id], results[appId]));

        var mainSlot = await SlotAsync(cs, mainId, "20000103T0901");
        var customSlot = await SlotAsync(cs, custom.Id, "20000103T1000");
        var appSlot = await SlotAsync(cs, appId, "20000103T0911");
        Assert.Equal(("completed", null), (mainSlot.Status, mainSlot.SkipCode));
        Assert.Equal(("completed", null), (customSlot.Status, customSlot.SkipCode));
        Assert.Equal(("completed", null), (appSlot.Status, appSlot.SkipCode));
        // Main plan run: 2 prompts × ChatGPT, charged 20 credits. Custom plan run: 1 prompt × ChatGPT × 2 samples, not charged.
        async Task<string> RunAsync(string runId) => string.Join("|", (await RowsAsync(cs, """
            SELECT r.requested_by, r.total_checks, COALESCE(m.estimated_credits, 'none') FROM visibility_runs r
            LEFT JOIN monitoring_credit_reservations m ON m.run_id = r.id WHERE r.id = UUID_TO_BIN(@run);
            """, ("@run", runId)))[0]);
        Assert.Equal($"schedule:{mainId:N}|2|20.000", await RunAsync(mainSlot.RunId!));
        Assert.Equal($"schedule:{custom.Id:N}|2|none", await RunAsync(customSlot.RunId!));
        // The App cycle run: 2 prompts × doubao, no reservation.
        Assert.Equal($"schedule:{appId:N}|2|none", await RunAsync(appSlot.RunId!));
        // Initial run + main slot run + custom slot run, exactly as before: 6 API checks, 6 provider tasks, 2 reservations,
        // 2 ledger entries, 40 credits. The App cycle run adds 1 run, 2 checks and 2 collector tasks and nothing else.
        Assert.Equal("runs=4|checks=8|providerTasks=6|collectorTasks=2|reservations=2|ledger=2|balance=960.000", await CollectionFootprintAsync(cs, rig.Tenant));
        Assert.Equal("2|1|1|2", string.Join("|", (await RowsAsync(cs, """
            SELECT (SELECT COUNT(*) FROM visibility_checks WHERE tenant_id = UUID_TO_BIN(@tenant) AND (provider = 'app_collector' OR capture_mode = 'app')),
                   (SELECT COUNT(*) FROM run_engines e JOIN visibility_runs r ON r.id = e.run_id WHERE r.tenant_id = UUID_TO_BIN(@tenant) AND e.provider = 'app_collector'),
                   (SELECT COUNT(*) FROM visibility_runs WHERE tenant_id = UUID_TO_BIN(@tenant) AND requested_by = @app),
                   (SELECT COUNT(*) FROM collector_tasks WHERE tenant_id = UUID_TO_BIN(@tenant) AND channel = 'consumer_app');
            """, ("@tenant", rig.Tenant.ToString()), ("@app", $"schedule:{appId:N}")))[0]));

        // The main plan is paused through the collection-schedules API and nothing has reconciled the App plan yet: its
        // next due slot is skipped as disabled instead of being planned.
        await rig.Schedules.UpdateAsync(new UpdateCollectionScheduleCommand(rig.Tenant, site, mainId, after[MainName].Version, Enabled: false));
        await ExecAsync(cs, "UPDATE collection_schedules SET next_slot_at = '2099-01-01' WHERE id = UUID_TO_BIN(@id);", ("@id", custom.Id.ToString()));
        await DueAtAsync(cs, appId, "2000-01-10 01:11:00");
        Assert.True((await PlansAsync(cs, site))[AppName].Enabled);
        Assert.Equal(1, await rig.Schedules.MaterializeDueSlotsAsync(Utc(10, 1, 12), 10));
        var paused = await SlotAsync(cs, appId, "20000110T0911");
        Assert.Equal(("skipped", "SCHEDULE_DISABLED", null), (paused.Status, paused.SkipCode, paused.RunId));
        Assert.StartsWith("runs=4|checks=8|providerTasks=6|collectorTasks=2|reservations=2|", await CollectionFootprintAsync(cs, rig.Tenant), StringComparison.Ordinal);
        await ParkSlotsAsync(cs, site);
    });
}
