using Jogeoai.Domain;
using static Jogeoai.Tests.AppCycleRunSqlTests;

namespace Jogeoai.Tests;

/// <summary>App 拒绝模拟器对平台的影响；租约过期而没有任何回报视同一次失败的尝试，任务由另一台设备接手，没有次数上限。</summary>
public sealed partial class AppLeaseSqlTests
{
    // 6.0b: the App entry is disabled only when every binding of the platform that the operator has not disabled is
    // stopped with emulator_rejected.
    [AppChannelMySqlFact]
    public Task EmulatorRejectedDisablesTheEntryOnlyWhenEveryBindingNotDisabledByTheOperatorReportedIt() => PoolAsync(async (pool, _) =>
    {
        var store = pool.Store();
        var (nodeA, _) = await pool.NodeAsync("mac");
        var (nodeB, _) = await pool.NodeAsync("mac");
        Task<AppFailureResult> ReportAsync(string node, Guid binding, AppFailureCode code = AppFailureCode.EmulatorRejected)
            => store.ReportFailureAsync(new AppFailureCommand(node, binding, code, CollectorPhase.Probe, null));
        Task<string> EntryAsync(string platform) => pool.RowAsync("""
            SELECT enabled + 0, COALESCE(unavailable_reason, '-'),
                   (SELECT COUNT(*) FROM collector_app_events WHERE event_kind = 'platform_unavailable' AND platform_code = @platform)
            FROM collector_platform_entries WHERE platform_code = @platform AND channel = 'consumer_app';
            """, ("@platform", platform));
        Task DisableByOperatorAsync(Guid binding) => pool.ExecAsync("""
            UPDATE collector_app_bindings SET status = 'stopped', stop_reason = 'disabled_by_operator', stopped_at = UTC_TIMESTAMP(6) WHERE id = UUID_TO_BIN(@id);
            """, ("@id", binding.ToString()));

        // One binding on the platform: its one report is every binding.
        var only = await pool.BindAsync(nodeA, "AVD_1", "kimi");
        Assert.Equal(Result(AppBindingStopReason.EmulatorRejected, null, unavailable: true), await ReportAsync(nodeA, only));
        Assert.Equal($"0|{AppUnavailableReasons.EmulatorRejected}|1", await EntryAsync("kimi"));

        // Three bindings: one disabled by the operator (not counted), one stopped for another reason (counted, and it has
        // not reported the rejection). The first report stops its binding and leaves the platform available.
        var (x, y, z) = (await pool.BindAsync(nodeA, "AVD_1", "deepseek"), await pool.BindAsync(nodeB, "AVD_B", "deepseek"), await pool.BindAsync(nodeA, "AVD_2", "deepseek"));
        await DisableByOperatorAsync(y);
        Assert.Equal(Result(AppBindingStopReason.LoginRequired, null), await ReportAsync(nodeA, z, AppFailureCode.LoginRequired));
        Assert.Equal(Result(AppBindingStopReason.EmulatorRejected, null), await ReportAsync(nodeA, x));
        Assert.Equal("stopped|emulator_rejected|1|0", await pool.BindingAsync(x));
        Assert.Equal("1|-|0", await EntryAsync("deepseek"));
        // The same binding reporting again changes nothing.
        Assert.Equal(Result(AppBindingStopReason.EmulatorRejected, null), await ReportAsync(nodeA, x));
        Assert.Equal("1|-|0", await EntryAsync("deepseek"));
        // The last counted binding reports it: now the entry is unavailable, with one event; the response says so.
        Assert.Equal(Result(AppBindingStopReason.EmulatorRejected, null, unavailable: true), await ReportAsync(nodeA, z));
        Assert.Equal($"0|{AppUnavailableReasons.EmulatorRejected}|1", await EntryAsync("deepseek"));
        Assert.Equal("stopped|disabled_by_operator|1|0", await pool.BindingAsync(y));
        // Reports after that keep saying unavailable and write no second event.
        Assert.Equal(Result(AppBindingStopReason.EmulatorRejected, null, unavailable: true), await ReportAsync(nodeA, x));
        Assert.Equal($"0|{AppUnavailableReasons.EmulatorRejected}|1", await EntryAsync("deepseek"));

        // A report from a binding the operator disabled keeps it disabled and does not count towards the rule.
        var (p, o) = (await pool.BindAsync(nodeA, "AVD_1", "qwen"), await pool.BindAsync(nodeB, "AVD_B", "qwen"));
        await DisableByOperatorAsync(o);
        Assert.Equal(Result(AppBindingStopReason.DisabledByOperator, null), await ReportAsync(nodeB, o));
        Assert.Equal("1|-|0", await EntryAsync("qwen"));
        Assert.Equal(Result(AppBindingStopReason.EmulatorRejected, null, unavailable: true), await ReportAsync(nodeA, p));
        Assert.Equal($"0|{AppUnavailableReasons.EmulatorRejected}|1", await EntryAsync("qwen"));
        // The web entries of the three platforms were never touched.
        Assert.Equal(3, await pool.CountAsync("""
            SELECT COUNT(*) FROM collector_platform_entries
            WHERE channel = 'consumer_web' AND platform_code IN ('kimi', 'deepseek', 'qwen') AND unavailable_reason IS NULL;
            """));
    });

    // A lease that ran out with no result and no failure report counts as that attempt's failure when another device asks
    // for work: the task moves on under the next lease version, each time to a device that has not attempted it. There is
    // no cap on how often that happens; a task is bounded by "each device attempts it once" and by its deadline.
    [AppChannelMySqlFact]
    public Task AnExpiredLeaseCountsAsAFailedAttemptEachTimeWithoutACapUntilTheDeadline() => PoolAsync(async (pool, seedTenant) =>
    {
        var rig = Build(pool.Cs, await seedTenant());
        var store = pool.Store(rig.Tenant);
        var (nodeA, _) = await pool.NodeAsync("mac");
        var (nodeB, _) = await pool.NodeAsync("mac");
        var cycle = await CycleAsync(rig, "过期", 2, "doubao");
        var (a, b) = (await pool.BindAsync(nodeA, "AVD_1", "doubao"), await pool.BindAsync(nodeB, "AVD_B", "doubao"));
        var task = (await store.LeaseAsync(new AppLeaseCommand(nodeA, a, 60))).Task!;
        Assert.Equal(cycle.Prompts[0], task.PromptId);
        // A lease that is still running is not an expired lease: nothing is counted while the device is busy.
        AssertEmpty(await store.LeaseAsync(new AppLeaseCommand(nodeA, a, 60)), AppLeaseEmptyReason.DeviceBusy, 30, 60);
        Assert.Equal($"leased|1|{nodeA}|1|0|-|-", await TaskAsync(pool.Cs, task.Id));

        // The current lease ran out 101 seconds ago with no report: long enough for the silent device's own cooldown to be
        // over too, so that what keeps a device away from the task below is its attempt and not its cooldown.
        Task ExpireAsync() => pool.ExecAsync("""
            UPDATE collector_tasks SET lease_until = UTC_TIMESTAMP(6) - INTERVAL 101 SECOND WHERE id = UUID_TO_BIN(@id);
            UPDATE collector_app_device_attempts SET lease_until = UTC_TIMESTAMP(6) - INTERVAL 101 SECOND WHERE task_id = UUID_TO_BIN(@id);
            """, ("@id", task.Id.ToString()));
        Task<AppLeaseResult> LeaseAsync(string node, Guid binding) => store.LeaseAsync(new AppLeaseCommand(node, binding, 60));

        // First expiry: another device takes the task. One failure is counted; the check keeps its one reservation.
        await ExpireAsync();
        var second = (await LeaseAsync(nodeB, b)).Task!;
        Assert.Equal((task.Id, task.CheckId, 2), (second.Id, second.CheckId, second.LeaseVersion));
        Assert.Equal($"leased|2|{nodeB}|2|1|lease_expired|-", await TaskAsync(pool.Cs, task.Id));
        Assert.Equal("990.000|10.000|0.000", await AppCreditSqlTests.AccountAsync(pool, rig.Tenant));

        // Second expiry. The first device asks: it has attempted this task, so it gets the next prompt, and the expired
        // lease is left as it is for a device that has not.
        await ExpireAsync();
        var next = (await LeaseAsync(nodeA, a)).Task!;
        Assert.Equal((cycle.Prompts[1], 1), (next.PromptId, next.LeaseVersion));
        Assert.NotEqual(task.Id, next.Id);
        Assert.Equal($"leased|2|{nodeB}|2|1|lease_expired|-", await TaskAsync(pool.Cs, task.Id));
        var c = await pool.BindAsync(nodeA, "AVD_2", "doubao");
        var third = (await LeaseAsync(nodeA, c)).Task!;
        Assert.Equal((task.Id, 3), (third.Id, third.LeaseVersion));
        Assert.Equal($"leased|3|{nodeA}|3|2|lease_expired|-", await TaskAsync(pool.Cs, task.Id));

        // Third expiry: the task is not closed by the count. A fourth device takes it.
        await ExpireAsync();
        var d = await pool.BindAsync(nodeB, "AVD_C", "doubao");
        var fourth = (await LeaseAsync(nodeB, d)).Task!;
        Assert.Equal((task.Id, task.CheckId, 4), (fourth.Id, fourth.CheckId, fourth.LeaseVersion));
        Assert.Equal($"leased|4|{nodeB}|4|3|lease_expired|-", await TaskAsync(pool.Cs, task.Id));
        Assert.Equal("pending|held|10.000|0|0", await pool.RowAsync("SELECT status FROM visibility_checks WHERE id = UUID_TO_BIN(@id);",
            ("@id", task.CheckId.ToString())) + "|" + await AppCreditSqlTests.ItemAsync(pool, task.CheckId));
        Assert.Equal("980.000|20.000|0.000", await AppCreditSqlTests.AccountAsync(pool, rig.Tenant));
        // Each expiry is an event under the node and the binding that held the lease, written by the node that found it.
        Assert.Equal($"{nodeA}/{a}/1/1/requeued/node:{nodeB},{nodeB}/{b}/2/2/requeued/node:{nodeA},{nodeA}/{c}/3/3/requeued/node:{nodeB}", await pool.RowAsync("""
            SELECT GROUP_CONCAT(CONCAT_WS('/', node_id, BIN_TO_UUID(binding_id), JSON_EXTRACT(detail, '$.leaseVersion'), JSON_EXTRACT(detail, '$.failureCount'),
                       JSON_UNQUOTE(JSON_EXTRACT(detail, '$.taskDisposition')), actor) ORDER BY occurred_at SEPARATOR ',')
            FROM collector_app_events WHERE event_kind = 'lease_expired' AND task_id = UUID_TO_BIN(@id);
            """, ("@id", task.Id.ToString())));
        // The leases themselves are the four attempts, one device each; no failure report was invented for the expiries.
        Assert.Equal("4|0", await pool.RowAsync("""
            SELECT COALESCE(SUM(event_kind = 'leased'), 0), COALESCE(SUM(event_kind = 'failed'), 0) FROM collector_attempts WHERE collector_task_id = UUID_TO_BIN(@id);
            """, ("@id", task.Id.ToString())));
        Assert.Equal("4|4", await pool.RowAsync("""
            SELECT COUNT(*), COUNT(DISTINCT node_id, device_id) FROM collector_app_device_attempts WHERE task_id = UUID_TO_BIN(@id);
            """, ("@id", task.Id.ToString())));

        // Fourth expiry: every device has attempted the task, so it waits. Nothing is settled before the deadline.
        await ExpireAsync();
        foreach (var (node, binding) in new[] { (nodeB, b), (nodeA, c), (nodeB, d) })
            AssertEmpty(await LeaseAsync(node, binding), AppLeaseEmptyReason.NoTask, 60, 60);
        Assert.Equal($"leased|4|{nodeB}|4|3|lease_expired|-", await TaskAsync(pool.Cs, task.Id));
        Assert.Equal(0, await rig.App.ExpireOverdueTasksAsync());
        Assert.Equal("held|10.000|0|0", await AppCreditSqlTests.ItemAsync(pool, task.CheckId));

        // At the task's deadline the check is "not collected" and the reservation is released, exactly once.
        await pool.ExecAsync("UPDATE collector_tasks SET deadline_at = UTC_TIMESTAMP(6) - INTERVAL 1 SECOND WHERE id = UUID_TO_BIN(@id);", ("@id", task.Id.ToString()));
        Assert.Equal(1, await rig.App.ExpireOverdueTasksAsync());
        Assert.Equal("terminal_failed|4|-|4|3|lease_expired|-", await TaskAsync(pool.Cs, task.Id));
        Assert.Equal($"cancelled|{AppNotCollectedCodes.Failed}|{AppNotCollectedRules.Message(AppNotCollectedCodes.Failed)}|1|1", await pool.RowAsync("""
            SELECT c.status, c.error_code, c.error_message, (SELECT COUNT(*) FROM analytics_check_arrivals arrived WHERE arrived.check_id = c.id),
                   (SELECT t.completed_at IS NOT NULL AND t.lease_until IS NULL FROM collector_tasks t WHERE t.check_id = c.id)
            FROM visibility_checks c WHERE c.id = UUID_TO_BIN(@id);
            """, ("@id", task.CheckId.ToString())));
        Assert.Equal("running|2|0|0|1|0", await RunAsync(pool.Cs, cycle.Run));
        Assert.Equal("released|10.000|0|1", await AppCreditSqlTests.ItemAsync(pool, task.CheckId));
        Assert.Equal("held|10.000|0|0", await AppCreditSqlTests.ItemAsync(pool, next.CheckId));
        Assert.Equal("990.000|10.000|0.000", await AppCreditSqlTests.AccountAsync(pool, rig.Tenant));
        Assert.Equal(0, await rig.App.ExpireOverdueTasksAsync());
        // The last holder's late answer is stale: the check stays "not collected" and nothing is charged.
        Assert.Equal(new CollectorResultResponse(CollectorResultDisposition.Stale, null),
            await store.SubmitResultAsync(AppCreditSqlTests.Result(nodeB, fourth) with { DeviceId = "AVD_C" }));
        Assert.Equal("cancelled", await pool.RowAsync("SELECT status FROM visibility_checks WHERE id = UUID_TO_BIN(@id);", ("@id", task.CheckId.ToString())));
        Assert.Equal("990.000|10.000|0.000", await AppCreditSqlTests.AccountAsync(pool, rig.Tenant));
    });
}
