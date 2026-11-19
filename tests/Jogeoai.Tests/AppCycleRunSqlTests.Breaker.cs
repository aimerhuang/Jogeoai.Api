using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using static Jogeoai.Tests.TeamMonitoringConfigSqlTests;

namespace Jogeoai.Tests;

/// <summary>
/// 总 PRD AC-C10 后半句的 API 一侧（隔离库 jogeoai_app_channel_test）：同一平台（doubao）App 的验证熔断把 App 平台暂停后，
/// 一个同时启用了 API 目标和 doubao App 目标的品牌，主计划的时隙照常创建 Run、检测和 provider_tasks，API 任务照常可以领取；
/// App 一侧的周期 Run 也照常创建，只是领取被 platform_paused 挡住。账号池的全局行由 <see cref="AppLeaseSqlTests.PoolAsync"/> 恢复。
/// </summary>
public sealed partial class AppCycleRunSqlTests
{
    [AppChannelMySqlFact]
    public Task AnAppVerificationBreakerLeavesTheMainPlanSlotCreatingItsApiRunChecksAndProviderTasks() => AppLeaseSqlTests.PoolAsync(async (pool, seedTenant) =>
    {
        var cs = pool.Cs;
        var rig = Build(cs, await seedTenant());
        var site = await SeedBrandAsync(cs, rig.Tenant, "熔断", follows: false, locale: "zh-CN", created: "2026-06-21");
        await rig.PutAsync(site, ChatGpt, App("doubao"));
        await SeedPromptAsync(cs, site, "哪个记账工具好用？", 1);
        await SeedPromptAsync(cs, site, "预算管理应用怎么选？", 2);
        var started = await rig.Service.EnsureAsync(rig.Tenant, site);
        var (main, app) = (started.Schedule!.Id, started.AppSchedule!.Id);
        await ExecAsync(cs, "UPDATE site_targets SET monitoring_enabled_at = '1999-01-01' WHERE site_id = UUID_TO_BIN(@site);", ("@site", site.ToString()));
        await AnchorAppTargetsAsync(cs, site);
        await ExecAsync(cs, "UPDATE collection_schedules SET next_slot_at = '2000-01-03 01:01:00' WHERE id = UUID_TO_BIN(@id);", ("@id", main.ToString()));
        await ExecAsync(cs, "UPDATE collection_schedules SET next_slot_at = '2000-01-03 01:11:00' WHERE id = UUID_TO_BIN(@id);", ("@id", app.ToString()));

        // ---- The App verification breaker on doubao: two bindings on two nodes report a challenge within 60 minutes.
        var store = pool.Store(rig.Tenant);
        var (nodeA, _) = await pool.NodeAsync("mac");
        var (nodeB, _) = await pool.NodeAsync("mac");
        var (a, b, c) = (await pool.BindAsync(nodeA, "AVD_1", "doubao"), await pool.BindAsync(nodeB, "AVD_B", "doubao"), await pool.BindAsync(nodeA, "AVD_2", "doubao"));
        Assert.False((await store.ReportFailureAsync(new AppFailureCommand(nodeA, a, AppFailureCode.ChallengeRequired, CollectorPhase.Probe, null))).PlatformPaused);
        Assert.True((await store.ReportFailureAsync(new AppFailureCommand(nodeB, b, AppFailureCode.ChallengeRequired, CollectorPhase.Probe, null))).PlatformPaused);
        Assert.Equal($"1|{AppPoolRules.VerificationBreakerPauseReason}", await RowAsync(cs,
            "SELECT paused_at IS NOT NULL, pause_reason FROM collector_app_platform_states WHERE platform_code = 'doubao';"));

        // ---- One worker pass over the brand's two due slots.
        Assert.True(await rig.Schedules.MaterializeDueSlotsAsync(Utc(3, 3, 0), 10) >= 2);
        var slots = (await rig.Schedules.GetPendingSlotsAsync(100)).Where(slot => slot.ScheduleId == main || slot.ScheduleId == app).ToArray();
        Assert.Equal(2, slots.Length);
        foreach (var slot in slots) Assert.True(await rig.Schedules.ProcessSlotAsync(slot, rig.Runs));

        // The main slot made its API run as usual: 2 checks on Cloro ChatGPT, 2 provider tasks, no collector task, 20 credits reserved.
        var mainSlot = await SlotAsync(cs, main, "20000103T0901");
        Assert.Equal(("completed", (string?)null), (mainSlot.Status, mainSlot.SkipCode));
        var apiRun = mainSlot.RunId!.Value;
        Assert.Equal("2|2|0|20.000|cloro:chatgpt:scraped", await RowAsync(cs, """
            SELECT r.total_checks,
                   (SELECT COUNT(*) FROM provider_tasks t WHERE t.run_id = r.id),
                   (SELECT COUNT(*) FROM collector_tasks t WHERE t.run_id = r.id),
                   (SELECT m.estimated_credits FROM monitoring_credit_reservations m WHERE m.run_id = r.id),
                   (SELECT GROUP_CONCAT(DISTINCT CONCAT_WS(':', c.provider, c.engine, c.capture_mode)) FROM visibility_checks c WHERE c.run_id = r.id)
            FROM visibility_runs r WHERE r.id = UUID_TO_BIN(@run);
            """, ("@run", apiRun.ToString())));
        // Its provider tasks are leasable by the API worker right away.
        var leased = await new CloroVisibilityStore(rig.Database, rig.Tenant, apiRun).LeaseTasksAsync("breaker-test", 5, 60, "cloro");
        Assert.Equal(2, leased.Count);
        Assert.All(leased, task => Assert.Equal(apiRun, task.RunId));

        // The App cycle run is created too; only its leases are held back by the paused platform.
        var appSlot = await SlotAsync(cs, app, "20000103T0911");
        Assert.Equal(2, (await PairsAsync(cs, appSlot.RunId!.Value)).Length);
        await pool.AgePlatformAsync("doubao");
        AppLeaseSqlTests.AssertEmpty(await store.LeaseAsync(new AppLeaseCommand(nodeA, c, 60)), AppLeaseEmptyReason.PlatformPaused, 600, 600);
        Assert.Equal(0, await CountAsync(cs, "SELECT COUNT(*) FROM collector_tasks WHERE run_id = UUID_TO_BIN(@run) AND status <> 'pending';",
            ("@run", appSlot.RunId!.Value.ToString())));
        await ParkSlotsAsync(cs, site);
    });
}
