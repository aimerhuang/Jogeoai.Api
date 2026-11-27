using System.Globalization;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using static Jogeoai.Tests.TeamMonitoringConfigSqlTests;

namespace Jogeoai.Tests;

/// <summary>
/// App 即时时隙（11-27-app-first-cycle-now）：新启用的 App 平台、新提问在保存后立即建任务，首个周期截止在下一个常规 09:11；
/// 已覆盖的「提问 × 平台」不重复；常规时隙照常继续；暂停的计划不插入。时刻由用例按今天的真实日期驱动。
/// </summary>
public sealed partial class AppCycleRunSqlTests
{
    private static readonly TimeZoneInfo Shanghai = CollectionScheduleCalendar.ParseTimeZone(PlanZone);

    private static DateOnly PlanToday() => ManagementStore.BrandLocalDate(PlanZone, DateTimeOffset.UtcNow);

    private static string Sql(DateTimeOffset utc) => utc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    // Slots of the plan: key | status | skip code | run, in key order.
    private static async Task<string[]> PlanSlotsAsync(string cs, Guid plan)
        => (await RowsAsync(cs, """
                SELECT CONCAT_WS('|', local_slot_key, status, COALESCE(skip_code, '-'), IF(run_id IS NULL, '-', 'run'))
                FROM schedule_slots WHERE schedule_id = UUID_TO_BIN(@plan) ORDER BY local_slot_key;
                """, ("@plan", plan.ToString()))).Select(row => row[0]!).ToArray();

    // prompt | platform | deadline (UTC) of every App task of a run, in prompt then platform order.
    private static async Task<string[]> TaskDeadlinesAsync(string cs, Guid run)
        => (await RowsAsync(cs, """
                SELECT CONCAT_WS('|', BIN_TO_UUID(c.prompt_id), t.platform_code, DATE_FORMAT(t.deadline_at, '%Y-%m-%d %H:%i:%s'))
                FROM collector_tasks t JOIN visibility_checks c ON c.id = t.check_id
                WHERE t.run_id = UUID_TO_BIN(@run) ORDER BY BIN_TO_UUID(c.prompt_id), t.platform_code;
                """, ("@run", run.ToString()))).Select(row => row[0]!).ToArray();

    private static string[] Expected(DateTimeOffset deadline, params (Guid Prompt, string Platform)[] pairs)
        => pairs.OrderBy(pair => pair.Prompt.ToString()).ThenBy(pair => pair.Platform, StringComparer.Ordinal)
            .Select(pair => $"{pair.Prompt}|{pair.Platform}|{Sql(deadline)}").ToArray();

    // The one pending instant slot of the plan, processed by the Worker; returns its run.
    private static async Task<Guid> ProcessInstantAsync(Rig rig, Guid plan)
    {
        var pending = Assert.Single(await rig.Schedules.GetPendingSlotsAsync(100), slot => slot.ScheduleId == plan);
        Assert.True(AppTestSlots.IsAppInstant(pending));
        Assert.True(await rig.Schedules.ProcessSlotAsync(pending, rig.Runs));
        return Guid.Parse((await RowsAsync(rig.Cs, "SELECT BIN_TO_UUID(run_id) FROM schedule_slots WHERE id = UUID_TO_BIN(@id);",
            ("@id", pending.Id.ToString())))[0][0]!);
    }

    // Materializes and processes the plan's regular 09:11 slot of a local date (driven at 09:12 of that day).
    private static async Task<string> RegularSlotAsync(Rig rig, Guid plan, DateOnly date)
    {
        var slotAt = AppCycleCalendar.SlotUtc(date, Shanghai);
        await ExecAsync(rig.Cs, "UPDATE collection_schedules SET next_slot_at = @at WHERE id = UUID_TO_BIN(@id);",
            ("@id", plan.ToString()), ("@at", Sql(slotAt)));
        Assert.Equal(1, await rig.Schedules.MaterializeDueSlotsAsync(slotAt.AddMinutes(1), 1));
        var pending = Assert.Single(await rig.Schedules.GetPendingSlotsAsync(100), slot => slot.ScheduleId == plan);
        Assert.Equal(AppCycleCalendar.SlotKey(date), pending.LocalSlotKey);
        await rig.Schedules.ProcessSlotAsync(pending, rig.Runs);
        return Assert.Single((await PlanSlotsAsync(rig.Cs, plan)).Where(slot => slot.StartsWith(AppCycleCalendar.SlotKey(date) + "|", StringComparison.Ordinal)));
    }

    // Starting monitoring queues the brand's App targets at once; the first cycle runs to the same weekday's 09:11 next week.
    // Re-saving and reading queue nothing more; that day's regular slot finds every pair covered; next week's slot collects.
    [AppChannelMySqlFact]
    public Task ANewlyEnabledAppTargetIsQueuedAtOnceAndThenFollowsItsWeekday() => AppSiteTargetsSqlTests.IsolatedAsync(async (cs, seedTenant) =>
    {
        var rig = Build(cs, await seedTenant());
        var site = await SeedAppBrandAsync(rig, "即时首轮", "doubao");
        var first = await SeedPromptAsync(cs, site, "哪个记账工具好用？", 1);
        var second = await SeedPromptAsync(cs, site, "预算管理应用怎么选？", 2);
        // Saving targets before monitoring is started does not create a plan or a slot.
        Assert.Equal(Footprint(0, 0), await FootprintAsync(cs, rig.Tenant));

        var plan = (await rig.Service.EnsureAsync(rig.Tenant, site)).AppSchedule!.Id;
        var today = PlanToday();
        var run = await ProcessInstantAsync(rig, plan);
        var firstDeadline = AppCycleCalendar.SlotUtc(today.AddDays(AppCycleRules.CycleDays), Shanghai);
        Assert.Equal(Expected(firstDeadline, (first, "doubao"), (second, "doubao")), await TaskDeadlinesAsync(cs, run));
        Assert.True((await RunAuditAsync(cs, run)).GetProperty("instant").GetBoolean());
        Assert.Equal(Footprint(1, 2), await FootprintAsync(cs, rig.Tenant));

        // Idempotent: reading, starting again and re-saving the same targets insert no further slot.
        await rig.Service.GetAsync(rig.Tenant, site);
        await rig.Service.EnsureAsync(rig.Tenant, site);
        await rig.PutAsync(site, App("doubao"));
        await rig.Service.SyncAppPlanAsync(rig.Tenant, site);
        Assert.Single(await PlanSlotsAsync(cs, plan));

        // The target is anchored today: today's regular slot is due by frequency, but its pairs are already covered.
        Assert.Equal(today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), (await RowsAsync(cs,
            "SELECT DATE_FORMAT(frequency_anchor, '%Y-%m-%d') FROM site_targets WHERE site_id = UUID_TO_BIN(@site) AND provider = 'app_collector';",
            ("@site", site.ToString())))[0][0]);
        Assert.Equal($"{AppCycleCalendar.SlotKey(today)}|skipped|{AutomaticMonitoringSelection.AlreadyCollectedCode}|-",
            await RegularSlotAsync(rig, plan, today));
        Assert.Equal(Footprint(1, 2), await FootprintAsync(cs, rig.Tenant));

        // Next week's regular slot: the instant cycle ended exactly there, so the weekly cycle collects every pair again.
        Assert.Equal($"{AppCycleCalendar.SlotKey(today.AddDays(7))}|completed|-|run", await RegularSlotAsync(rig, plan, today.AddDays(7)));
        Assert.Equal(Footprint(2, 4), await FootprintAsync(cs, rig.Tenant));
    });

    // Within a running cycle, a new prompt and a newly enabled platform are queued at once for their own pairs only; the
    // pairs the cycle already covers are not queued again.
    [AppChannelMySqlFact]
    public Task ANewPromptAndANewPlatformJoinTheCurrentCycleOnlyForTheirOwnPairs() => AppSiteTargetsSqlTests.IsolatedAsync(async (cs, seedTenant) =>
    {
        var rig = Build(cs, await seedTenant());
        var site = await SeedAppBrandAsync(rig, "周中新增", "doubao");
        var first = await SeedPromptAsync(cs, site, "哪个记账工具好用？", 1);
        var plan = (await rig.Service.EnsureAsync(rig.Tenant, site)).AppSchedule!.Id;
        await ProcessInstantAsync(rig, plan);
        // Move doubao's anchor to tomorrow's weekday: a new prompt is covered up to tomorrow's regular slot only.
        var today = PlanToday();
        await ExecAsync(cs, "UPDATE site_targets SET frequency_anchor = @anchor WHERE site_id = UUID_TO_BIN(@site) AND provider = 'app_collector';",
            ("@site", site.ToString()), ("@anchor", today.AddDays(-6).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));

        // A new prompt (instant keys are per second).
        await Task.Delay(1100);
        var second = await SeedPromptAsync(cs, site, "预算管理应用怎么选？", 2);
        await rig.Service.SyncAppPlanAsync(rig.Tenant, site);
        var run = await ProcessInstantAsync(rig, plan);
        Assert.Equal(Expected(AppCycleCalendar.SlotUtc(today.AddDays(1), Shanghai), (second, "doubao")), await TaskDeadlinesAsync(cs, run));

        // A newly enabled platform: both prompts on kimi, nothing again on doubao. Its own anchor is today.
        await Task.Delay(1100);
        await rig.PutAsync(site, App("doubao"), App("kimi"));
        run = await ProcessInstantAsync(rig, plan);
        Assert.Equal(Expected(AppCycleCalendar.SlotUtc(today.AddDays(7), Shanghai), (first, "kimi"), (second, "kimi")),
            await TaskDeadlinesAsync(cs, run));
        Assert.Equal(Footprint(3, 4), await FootprintAsync(cs, rig.Tenant));

        // Everything is covered now: a further sync inserts nothing.
        await Task.Delay(1100);
        await rig.Service.SyncAppPlanAsync(rig.Tenant, site);
        Assert.DoesNotContain(await rig.Schedules.GetPendingSlotsAsync(100), slot => slot.ScheduleId == plan);
    });

    // A change saved while the instant slot is still pending is not lost: the Worker reconciles again right after the slot.
    [AppChannelMySqlFact]
    public Task AChangeSavedWhileASlotIsPendingIsQueuedRightAfterIt() => AppSiteTargetsSqlTests.IsolatedAsync(async (cs, seedTenant) =>
    {
        var rig = Build(cs, await seedTenant());
        var site = await SeedAppBrandAsync(rig, "排队中新增", "doubao");
        var first = await SeedPromptAsync(cs, site, "哪个记账工具好用？", 1);
        var plan = (await rig.Service.EnsureAsync(rig.Tenant, site)).AppSchedule!.Id;
        // The first instant slot is pending; a prompt created now gets no second slot yet.
        await Task.Delay(1100);
        var second = await SeedPromptAsync(cs, site, "预算管理应用怎么选？", 2);
        await rig.Service.SyncAppPlanAsync(rig.Tenant, site);
        Assert.Single(await PlanSlotsAsync(cs, plan));

        // The first slot expands its frozen selection (the first prompt only), then the Worker inserts the follow-up slot.
        var today = PlanToday();
        var run = await ProcessInstantAsync(rig, plan);
        var deadline = AppCycleCalendar.SlotUtc(today.AddDays(AppCycleRules.CycleDays), Shanghai);
        Assert.Equal(Expected(deadline, (first, "doubao")), await TaskDeadlinesAsync(cs, run));
        run = await ProcessInstantAsync(rig, plan);
        Assert.Equal(Expected(deadline, (second, "doubao")), await TaskDeadlinesAsync(cs, run));
        Assert.Equal(Footprint(2, 2), await FootprintAsync(cs, rig.Tenant));
        Assert.DoesNotContain(await rig.Schedules.GetPendingSlotsAsync(100), slot => slot.ScheduleId == plan);
    });

    // A Worker whose tenant list does not include the tenant skips the slot as not open; the API then stops inserting slots
    // that would only be skipped again, until a slot of the plan is expanded.
    [AppChannelMySqlFact]
    public Task AfterANotOpenSkipNoFurtherInstantSlotIsInserted() => AppSiteTargetsSqlTests.IsolatedAsync(async (cs, seedTenant) =>
    {
        var tenant = await seedTenant();
        var rig = Build(cs, tenant);
        var site = await SeedAppBrandAsync(rig, "名单不一致", "doubao");
        await SeedPromptAsync(cs, site, "哪个记账工具好用？", 1);
        var plan = (await rig.Service.EnsureAsync(rig.Tenant, site)).AppSchedule!.Id;
        var closedWorker = Build(cs, tenant, open: false);
        var pending = Assert.Single(await closedWorker.Schedules.GetPendingSlotsAsync(100), slot => slot.ScheduleId == plan);
        Assert.False(await closedWorker.Schedules.ProcessSlotAsync(pending, closedWorker.Runs));
        Assert.EndsWith($"|skipped|{AppMonitoringPlan.NotOpenCode}|-", Assert.Single(await PlanSlotsAsync(cs, plan)), StringComparison.Ordinal);

        await Task.Delay(1100);
        await rig.Service.GetAsync(rig.Tenant, site);
        await rig.Service.SyncAppPlanAsync(rig.Tenant, site);
        Assert.Single(await PlanSlotsAsync(cs, plan));
        Assert.Equal(Footprint(0, 0), await FootprintAsync(cs, rig.Tenant));
    });

    // A paused brand gets no instant slot; neither does a brand whose regular slot is already due (the Worker takes that).
    [AppChannelMySqlFact]
    public Task APausedPlanOrADueRegularSlotGetsNoInstantSlot() => AppSiteTargetsSqlTests.IsolatedAsync(async (cs, seedTenant) =>
    {
        var rig = Build(cs, await seedTenant());
        var site = await SeedAppBrandAsync(rig, "暂停", "doubao");
        await SeedPromptAsync(cs, site, "哪个记账工具好用？", 1);
        var plan = (await rig.Service.EnsureAsync(rig.Tenant, site)).AppSchedule!.Id;
        await ProcessInstantAsync(rig, plan);

        await Task.Delay(1100);
        await ExecAsync(cs, "UPDATE collection_schedules SET enabled = FALSE, next_slot_at = NULL WHERE id = UUID_TO_BIN(@id);", ("@id", plan.ToString()));
        await SeedPromptAsync(cs, site, "暂停期间新增的提问", 2);
        await rig.Service.SyncAppPlanAsync(rig.Tenant, site);
        Assert.Single(await PlanSlotsAsync(cs, plan));

        await ExecAsync(cs, "UPDATE collection_schedules SET enabled = TRUE, next_slot_at = @at WHERE id = UUID_TO_BIN(@id);",
            ("@id", plan.ToString()), ("@at", Sql(DateTimeOffset.UtcNow.AddMinutes(-1))));
        await rig.Service.SyncAppPlanAsync(rig.Tenant, site);
        Assert.Single(await PlanSlotsAsync(cs, plan));
    });
}
