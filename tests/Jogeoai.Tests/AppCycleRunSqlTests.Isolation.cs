using System.Globalization;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using static Jogeoai.Tests.TeamMonitoringConfigSqlTests;

namespace Jogeoai.Tests;

/// <summary>
/// 与现有路径的隔离（implement 4.3a / 4.7）：网页采集器的两种领取、预检和过期回收都看不到 App 任务；App 的截止处理不碰网页任务；
/// 同一轮里主计划的 API Run 与没有 App 的品牌完全相同。
/// </summary>
public sealed partial class AppCycleRunSqlTests
{
    // status | attempt_count | lease_version | lease_owner of the run's tasks, ordered by prompt creation.
    private static async Task<string[]> TaskStatesAsync(string cs, Guid run)
        => (await RowsAsync(cs, """
                SELECT CONCAT_WS('|', t.status, t.attempt_count, t.lease_version, COALESCE(t.lease_owner, '-'))
                FROM collector_tasks t JOIN visibility_checks c ON c.id = t.check_id JOIN prompts p ON p.id = c.prompt_id
                WHERE t.run_id = UUID_TO_BIN(@run) ORDER BY p.created_at, p.id;
                """, ("@run", run.ToString()))).Select(row => row[0]!).ToArray();

    // 4.3a: a web collector can never lease, count or recover an App task — with the legacy lease, with the account-pool
    // lease and its eligibility probe, and in the expired-lease recovery — and still gets its web tasks. The App deadline
    // pass never touches a web task.
    [AppChannelMySqlFact]
    public Task WebLeasesEligibilityAndRecoveryNeverSeeAnAppTaskAndStillServeWebTasks() => AppSiteTargetsSqlTests.IsolatedAsync(async (cs, seedTenant) =>
    {
        var rig = Build(cs, await seedTenant());
        var site = await SeedAppBrandAsync(rig, "渠道隔离", "doubao");
        var first = await SeedPromptAsync(cs, site, "哪个记账工具好用？", 1);
        var second = await SeedPromptAsync(cs, site, "预算管理应用怎么选？", 2);
        var appRun = (await RunSlotAsync(rig, await StartAsync(rig, site), 3)).RunId!.Value;
        string[] untouched = ["pending|0|0|-", "pending|0|0|-"];
        Assert.Equal(untouched, await TaskStatesAsync(cs, appRun));

        var web = new DomesticCollectorStore(rig.Database);
        var pool = new CollectorSessionPoolStore(rig.Database);
        CollectorLeaseCommand Lease(Guid? profile = null) => new(rig.Tenant, "web-node", CollectorRegion.Cn, ["doubao"], profile is null ? 5 : 1, 60, profile);

        // Legacy lease (no account registered): only App tasks exist for the platform, so there is nothing to lease.
        Assert.Empty(await web.LeaseAsync(Lease()));
        Assert.Equal(untouched, await TaskStatesAsync(cs, appRun));

        // With web tasks next to them the legacy lease returns exactly the web tasks.
        var webRun = (await web.CreateRunAsync(new CreateVisibilityRunCommand(rig.Tenant, site, [first, second],
            [new CollectorPlatformSelection("doubao", "doubao.consumer_web")], $"web-isolation-{Guid.NewGuid():N}"))).RunId;
        var leased = await web.LeaseAsync(Lease());
        Assert.Equal(2, leased.Count);
        Assert.All(leased, task => Assert.Equal((webRun, "consumer_web", "doubao.consumer_web"), (task.RunId, task.Channel, task.PlatformEntryCode)));
        Assert.Empty(await web.LeaseAsync(Lease()));
        Assert.Equal(untouched, await TaskStatesAsync(cs, appRun));

        // Account-pool lease. The profile is inserted directly: registering it would write a health event, and that table
        // is append-only (it could not be cleaned up afterwards).
        var profile = Guid.NewGuid();
        await ExecAsync(cs, """
            INSERT INTO collector_session_profiles (id, tenant_id, platform_code, region, label, status, created_at, updated_at)
            VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), 'doubao', 'cn', 'web-a', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
            """, ("@id", profile.ToString()), ("@tenant", rig.Tenant.ToString()));
        // Both web tasks are under a live lease; the two App tasks are pending and due. The profile is not blocked, yet it
        // has nothing to lease, and the retry hint is the web leases' expiry (about 60 s) — a pending App task would make
        // it the 30 s minimum.
        Assert.Empty(await web.LeaseAsync(Lease(profile)));
        var waiting = await pool.CheckEligibilityAsync(new CollectorEligibilityCommand(rig.Tenant, "web-node", CollectorRegion.Cn, profile, "doubao"));
        Assert.False(waiting.Eligible);
        Assert.Null(waiting.StopReason);
        Assert.InRange(waiting.RetryAfterSeconds, 50, 60);
        Assert.Equal(untouched, await TaskStatesAsync(cs, appRun));

        // Expired leases: the web tasks, and one App task made to look like an expired App lease (what phase 5 will
        // produce). The web recovery takes the two web tasks only and aggregates the web run only.
        await ExecAsync(cs, "UPDATE collector_tasks SET lease_until = UTC_TIMESTAMP(6) - INTERVAL 1 MINUTE WHERE run_id = UUID_TO_BIN(@run);", ("@run", webRun.ToString()));
        await TaskOfAsync(cs, appRun, first, """
            t.status = 'leased', t.lease_owner = 'app-node', t.lease_version = 1, t.attempt_count = 1,
            t.lease_until = UTC_TIMESTAMP(6) - INTERVAL 1 MINUTE
            """);
        Assert.Equal(2, await web.RecoverExpiredTasksAsync());
        Assert.Equal(["retryable_failed|1|1|-", "retryable_failed|1|1|-"], await TaskStatesAsync(cs, webRun));
        Assert.Equal(["leased|1|1|app-node", "pending|0|0|-"], await TaskStatesAsync(cs, appRun));
        Assert.Equal("∅|∅", await RowAsync(cs, """
            SELECT t.last_error_code, t.last_error_message FROM collector_tasks t JOIN visibility_checks c ON c.id = t.check_id
            WHERE t.run_id = UUID_TO_BIN(@run) AND c.prompt_id = UUID_TO_BIN(@prompt);
            """, ("@run", appRun.ToString()), ("@prompt", first.ToString())));
        // The web run was aggregated from its tasks as before; the App run was not touched (still queued, no counters).
        Assert.Equal("failed|2|0|2|1|1", await RunAsync(cs, webRun));
        Assert.Equal("queued|2|0|0|0|0", await RunAsync(cs, appRun));
        Assert.Equal(0, await web.RecoverExpiredTasksAsync());

        // Now a web task is leasable again: the probe says so and the account-pool lease returns it, not an App task.
        Assert.True((await pool.CheckEligibilityAsync(new CollectorEligibilityCommand(rig.Tenant, "web-node", CollectorRegion.Cn, profile, "doubao"))).Eligible);
        var pooled = Assert.Single(await web.LeaseAsync(Lease(profile)));
        Assert.Equal((webRun, "consumer_web", (Guid?)profile), (pooled.RunId, pooled.Channel, pooled.SessionProfileId));
        Assert.Equal(["leased|1|1|app-node", "pending|0|0|-"], await TaskStatesAsync(cs, appRun));

        // The App deadline pass: the web tasks get a past deadline to prove the channel is what protects them. It takes
        // the two App tasks (one never leased, one with an expired lease) and leaves every web row as it is.
        await ExecAsync(cs, "UPDATE collector_tasks SET deadline_at = '2000-01-01 00:00:00' WHERE run_id = UUID_TO_BIN(@run);", ("@run", webRun.ToString()));
        var webTasks = await TaskStatesAsync(cs, webRun);
        var webChecks = await RowAsync(cs, "SELECT GROUP_CONCAT(status ORDER BY id), GROUP_CONCAT(COALESCE(error_code, '-') ORDER BY id) FROM visibility_checks WHERE run_id = UUID_TO_BIN(@run);",
            ("@run", webRun.ToString()));
        Assert.Equal(2, await rig.App.ExpireOverdueTasksAsync());
        Assert.Equal(webTasks, await TaskStatesAsync(cs, webRun));
        Assert.Equal("pending,pending|-,-", webChecks);
        Assert.Equal(webChecks, await RowAsync(cs, "SELECT GROUP_CONCAT(status ORDER BY id), GROUP_CONCAT(COALESCE(error_code, '-') ORDER BY id) FROM visibility_checks WHERE run_id = UUID_TO_BIN(@run);",
            ("@run", webRun.ToString())));
        Assert.Equal("failed|2|0|2|1|1", await RunAsync(cs, webRun));
        Assert.Equal("cancelled|2|0|0|1|1", await RunAsync(cs, appRun));
        Assert.Equal(2, await CountAsync(cs, "SELECT COUNT(*) FROM visibility_checks WHERE run_id = UUID_TO_BIN(@run) AND status = 'cancelled' AND error_code = 'APP_NOT_COLLECTED_CAPACITY';",
            ("@run", appRun.ToString())));
        // No health event was written for the tenant (the append-only table must stay clean for the row-count check).
        Assert.Equal(0, await CountAsync(cs, "SELECT COUNT(*) FROM collector_session_health_events WHERE tenant_id = UUID_TO_BIN(@tenant);", ("@tenant", rig.Tenant.ToString())));
    });

    private static Dictionary<string, decimal> Parse(string footprint)
        => footprint.Split('|').Select(part => part.Split('=')).ToDictionary(part => part[0], part => decimal.Parse(part[1], CultureInfo.InvariantCulture));

    // One worker pass over a brand with both channels and a twin brand (another tenant, App channel not open, the same API
    // target and prompts): the main slot, its API run, the reservation and the charge are identical; the App cycle run adds
    // one run, its checks and its collector tasks and nothing else.
    [AppChannelMySqlFact]
    public Task TheMainPlanOfABrandWithAppTargetsCreatesItsApiRunExactlyAsBefore() => AppSiteTargetsSqlTests.IsolatedAsync(async (cs, seedTenant) =>
    {
        var open = Build(cs, await seedTenant());
        var closed = Build(cs, await seedTenant(), open: false);

        async Task<(Guid Site, Guid Main, Guid? App)> BrandAsync(Rig rig, params SiteTargetWrite[] targets)
        {
            var site = await SeedBrandAsync(cs, rig.Tenant, "同一轮", follows: false, locale: "zh-CN", created: "2026-06-21");
            await rig.PutAsync(site, targets);
            await SeedPromptAsync(cs, site, "哪个记账工具好用？", 1);
            await SeedPromptAsync(cs, site, "预算管理应用怎么选？", 2);
            var started = await rig.Service.EnsureAsync(rig.Tenant, site);
            // Targets active before the year-2000 slots; the App target's cycle starts on Monday 2000-01-03.
            await ExecAsync(cs, "UPDATE site_targets SET monitoring_enabled_at = '1999-01-01' WHERE site_id = UUID_TO_BIN(@site);", ("@site", site.ToString()));
            await AnchorAppTargetsAsync(cs, site);
            await ExecAsync(cs, "UPDATE collection_schedules SET next_slot_at = '2000-01-03 01:01:00' WHERE id = UUID_TO_BIN(@id);", ("@id", started.Schedule!.Id.ToString()));
            return (site, started.Schedule.Id, started.AppSchedule?.Id);
        }

        var both = await BrandAsync(open, ChatGpt, App("doubao"));
        var apiOnly = await BrandAsync(closed, ChatGpt);
        Assert.Null(apiOnly.App);
        await ExecAsync(cs, "UPDATE collection_schedules SET next_slot_at = '2000-01-03 01:11:00' WHERE id = UUID_TO_BIN(@id);", ("@id", both.App!.Value.ToString()));
        // After the first run both tenants look the same.
        Assert.Equal(await FootprintAsync(cs, closed.Tenant), await FootprintAsync(cs, open.Tenant));

        // The pass of one worker (the open tenant is the only one in its Collector:AppTenantIds).
        Assert.Equal(3, await open.Schedules.MaterializeDueSlotsAsync(Utc(3, 3, 0), 10));
        var slots = (await open.Schedules.GetPendingSlotsAsync(100))
            .Where(slot => slot.ScheduleId == both.Main || slot.ScheduleId == both.App || slot.ScheduleId == apiOnly.Main).ToArray();
        Assert.Equal(3, slots.Length);
        foreach (var slot in slots) Assert.True(await open.Schedules.ProcessSlotAsync(slot, open.Runs));

        // The main plan's slot and run, described without tenant-specific ids.
        Task<string> MainAsync(Guid schedule) => RowAsync(cs, """
            SELECT s.status, COALESCE(s.skip_code, '-'), JSON_EXTRACT(s.selection_snapshot, '$.targets'), JSON_LENGTH(s.selection_snapshot, '$.promptIds'),
                   JSON_EXTRACT(s.selection_snapshot, '$.sampleCount'), JSON_CONTAINS_PATH(s.selection_snapshot, 'one', '$.promptTargets'),
                   r.status, r.total_checks, r.requested_by = CONCAT('schedule:', REPLACE(BIN_TO_UUID(s.schedule_id), '-', '')),
                   (SELECT COUNT(*) FROM provider_tasks t WHERE t.run_id = r.id),
                   (SELECT COUNT(*) FROM collector_tasks t WHERE t.run_id = r.id),
                   (SELECT CONCAT_WS('/', m.estimated_credits, m.status) FROM monitoring_credit_reservations m WHERE m.run_id = r.id),
                   (SELECT COUNT(*) FROM monitoring_credit_items i WHERE i.run_id = r.id),
                   (SELECT GROUP_CONCAT(DISTINCT CONCAT_WS(':', c.provider, c.engine, c.capture_mode, c.status, c.country, c.locale)) FROM visibility_checks c WHERE c.run_id = r.id),
                   (SELECT GROUP_CONCAT(CONCAT_WS(':', e.provider, e.engine, e.capture_mode, e.target_key) ORDER BY e.sort_order) FROM run_engines e WHERE e.run_id = r.id)
            FROM schedule_slots s JOIN visibility_runs r ON r.id = s.run_id
            WHERE s.schedule_id = UUID_TO_BIN(@schedule) AND s.local_slot_key = '20000103T0901';
            """, ("@schedule", schedule.ToString()));
        var expected = await MainAsync(apiOnly.Main);
        Assert.StartsWith("completed|-|", expected, StringComparison.Ordinal);
        Assert.Contains("|2|1|2|0|20.000/", expected, StringComparison.Ordinal);
        Assert.DoesNotContain("app", expected, StringComparison.Ordinal);
        Assert.Equal(expected, await MainAsync(both.Main));
        // The main plan row itself: version, flag and targets are the same.
        Task<string> PlanAsync(Guid schedule) => RowAsync(cs, """
            SELECT version, enabled, TIME_FORMAT(local_time, '%H:%i'), JSON_EXTRACT(selection_snapshot, '$.targets'), JSON_LENGTH(selection_snapshot, '$.promptIds')
            FROM collection_schedules WHERE id = UUID_TO_BIN(@id);
            """, ("@id", schedule.ToString()));
        Assert.Equal(await PlanAsync(apiOnly.Main), await PlanAsync(both.Main));

        // Tenant totals: the brand with App targets has one more run with two checks and two collector tasks. Provider
        // tasks, reservations, credit items, ledger entries and the balance are the same in both tenants.
        var (with, without) = (Parse(await FootprintAsync(cs, open.Tenant)), Parse(await FootprintAsync(cs, closed.Tenant)));
        Assert.Equal(new Dictionary<string, decimal>
        {
            ["runs"] = 1, ["checks"] = 2, ["providerTasks"] = 0, ["collectorTasks"] = 2, ["reservations"] = 0, ["items"] = 0, ["ledger"] = 0, ["balance"] = 0,
        }, with.ToDictionary(pair => pair.Key, pair => pair.Value - without[pair.Key]));
        Assert.Equal((2m, 4m, 960m), (without["runs"], without["providerTasks"], without["balance"]));
        // The extra run is the App cycle run.
        var appRun = (await SlotAsync(cs, both.App.Value, "20000103T0911")).RunId!.Value;
        Assert.Equal("queued|2|0|0|0|0", await RunAsync(cs, appRun));
        Assert.Equal(2, (await PairsAsync(cs, appRun)).Length);
        foreach (var site in new[] { both.Site, apiOnly.Site }) await ParkSlotsAsync(cs, site);
    });
}
