using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using static Jogeoai.Tests.AppCycleRunSqlTests;

namespace Jogeoai.Tests;

/// <summary>领取（implement 5.4 / 5.6a / 5.6c，design 6.2）：成功领取写下的内容、每一种返回空的原因及其先后、续租与过期租约。</summary>
public sealed partial class AppLeaseSqlTests
{
    internal static void AssertEmpty(AppLeaseResult result, AppLeaseEmptyReason reason, int low, int high)
    {
        Assert.Null(result.Task);
        Assert.Equal(reason, result.Reason);
        Assert.InRange(result.RetryAfterSeconds!.Value, low, high);
    }

    // Every blocker is put in place at once and removed one by one: the reason returned is always the earliest one in
    // the evaluation order of design 6.2.
    [AppChannelMySqlFact]
    public Task ALeaseWritesTheLeaseAndEveryEmptyReasonComesInTheEvaluationOrder() => PoolAsync(async (pool, seedTenant) =>
    {
        var rig = Build(pool.Cs, await seedTenant());
        var store = pool.Store(rig.Tenant);
        var (node, _) = await pool.NodeAsync("mac");
        var (otherNode, _) = await pool.NodeAsync("mac");
        var cycle = await CycleAsync(rig, "领取", 2, "doubao", "kimi");
        var doubao = await pool.BindAsync(node, "AI_Apps_4KB", "doubao");
        var kimi = await pool.BindAsync(node, "AI_Apps_4KB", "kimi"); // the same device
        var foreign = await pool.BindAsync(otherNode, "AVD_B", "doubao");
        var (first, second) = (await TaskOfAsync(pool.Cs, cycle.Run, cycle.Prompts[0], "doubao"), await TaskOfAsync(pool.Cs, cycle.Run, cycle.Prompts[1], "doubao"));
        Task<AppLeaseResult> LeaseAsync(Guid binding, int seconds = 120) => store.LeaseAsync(new AppLeaseCommand(node, binding, seconds));
        Task SetBindingAsync(string set) => pool.ExecAsync($"UPDATE collector_app_bindings SET {set} WHERE id = UUID_TO_BIN(@id);", ("@id", doubao.ToString()));
        Task SetPlatformAsync(string set) => pool.ExecAsync($"UPDATE collector_app_platform_states SET {set} WHERE platform_code = 'doubao';");

        // Step 1: the binding must exist and belong to the token's node; the refusal leaves an audit event and no lease.
        foreach (var seconds in new[] { 4, 901 })
            Assert.Equal(("INVALID_LEASE", 400), Code(await Assert.ThrowsAsync<CollectorStoreException>(() => LeaseAsync(doubao, seconds))));
        var unknown = Guid.NewGuid();
        Assert.Equal(("APP_BINDING_FORBIDDEN", 403), Code(await Assert.ThrowsAsync<CollectorStoreException>(() => LeaseAsync(unknown))));
        Assert.Equal(("APP_BINDING_FORBIDDEN", 403), Code(await Assert.ThrowsAsync<CollectorStoreException>(() => LeaseAsync(foreign))));
        Assert.Equal(("unknown_binding", "leases"), ((await pool.LastEventAsync("binding_forbidden", unknown)).GetProperty("reason").GetString(),
            (await pool.LastEventAsync("binding_forbidden", unknown)).GetProperty("route").GetString()));
        Assert.Equal("foreign_node", (await pool.LastEventAsync("binding_forbidden", foreign)).GetProperty("reason").GetString());
        Assert.Equal($"{node}|node:{node}", await pool.RowAsync(
            "SELECT node_id, actor FROM collector_app_events WHERE event_kind = 'binding_forbidden' AND binding_id = UUID_TO_BIN(@id);", ("@id", foreign.ToString())));
        Assert.Equal(0, await pool.CountAsync("SELECT COUNT(*) FROM collector_tasks WHERE run_id = UUID_TO_BIN(@run) AND status <> 'pending';", ("@run", cycle.Run.ToString())));

        // Step 9: the lease. The first prompt of the brand (fair_rank 1) on the binding's platform.
        var leased = await LeaseAsync(doubao);
        Assert.Equal((null, null), (leased.Reason, leased.RetryAfterSeconds));
        var task = leased.Task!;
        Assert.Equal((first, rig.Tenant, cycle.Run, cycle.Site, cycle.Prompts[0], "doubao", "doubao.consumer_app", "cn", "zh-CN", "consumer_app"),
            (task.Id, task.TenantId, task.RunId, task.SiteId, task.PromptId, task.PlatformCode, task.PlatformEntryCode, task.Region, task.Language, task.Channel));
        Assert.Equal(("领取：第 1 条提问，哪个记账工具好用？", AppPromptRules.RuleVersion, 1, doubao), (task.QuestionText, task.PromptRuleVersion, task.LeaseVersion, task.BindingId));
        Assert.InRange(task.LeaseExpiresAt, DateTimeOffset.UtcNow.AddSeconds(100), DateTimeOffset.UtcNow.AddSeconds(125));
        Assert.InRange(task.DeadlineAt, DateTimeOffset.UtcNow.AddDays(6).AddMinutes(-2), DateTimeOffset.UtcNow.AddDays(6).AddMinutes(2));
        Assert.Equal(task.CheckId.ToString(), await pool.RowAsync("SELECT BIN_TO_UUID(check_id) FROM collector_tasks WHERE id = UUID_TO_BIN(@id);", ("@id", first.ToString())));
        Assert.Equal($"leased|1|{node}|1|0|-|-", await TaskAsync(pool.Cs, first));
        Assert.Equal($"{doubao}|1|1|1|1", await pool.RowAsync("""
            SELECT BIN_TO_UUID(lease_app_binding_id), lease_session_profile_id IS NULL, started_at IS NOT NULL,
                   lease_until > UTC_TIMESTAMP(6) + INTERVAL 100 SECOND, lease_until <= UTC_TIMESTAMP(6) + INTERVAL 120 SECOND
            FROM collector_tasks WHERE id = UUID_TO_BIN(@id);
            """, ("@id", first.ToString())));
        Assert.Equal($"leased|{doubao}|1|{node}|1|cn|1", await pool.RowAsync("""
            SELECT event_kind, BIN_TO_UUID(app_binding_id), session_profile_id IS NULL, node_id, lease_version, region, error_code IS NULL
            FROM collector_attempts WHERE collector_task_id = UUID_TO_BIN(@id);
            """, ("@id", first.ToString())));
        Assert.Equal("1|1", await pool.RowAsync("""
            SELECT (SELECT last_leased_at IS NOT NULL AND last_seen_at IS NOT NULL FROM collector_app_bindings WHERE id = UUID_TO_BIN(@id)),
                   (SELECT last_dispatched_at IS NOT NULL FROM collector_app_platform_states WHERE platform_code = 'doubao');
            """, ("@id", doubao.ToString())));
        Assert.Equal($"{node}|AI_Apps_4KB|{doubao}|1|1", await pool.RowAsync("""
            SELECT node_id, device_id, BIN_TO_UUID(binding_id), lease_version, sent_at IS NULL
            FROM collector_app_device_attempts WHERE task_id = UUID_TO_BIN(@id);
            """, ("@id", first.ToString())));
        // The check stays pending (the web path does not mark it either); the run is running.
        Assert.Equal("running|4|0|0|0|0", await RunAsync(pool.Cs, cycle.Run));
        Assert.Equal("pending", await pool.RowAsync("SELECT status FROM visibility_checks WHERE id = UUID_TO_BIN(@id);", ("@id", task.CheckId.ToString())));
        Assert.Equal($"pending|0|-|0|0|-|-", await TaskAsync(pool.Cs, second));

        // Step 4, on another platform: the device already holds a lease.
        AssertEmpty(await LeaseAsync(kimi), AppLeaseEmptyReason.DeviceBusy, 100, 120);

        // Every blocker at once.
        await SetBindingAsync("status = 'stopped', stop_reason = 'login_required', stopped_at = UTC_TIMESTAMP(6), cooldown_until = UTC_TIMESTAMP(6) + INTERVAL 100 SECOND, daily_quota = 1");
        await SetPlatformAsync("paused_at = UTC_TIMESTAMP(6), pause_reason = 'operator', daily_cap = 1");
        await pool.ExecAsync("UPDATE collector_platform_entries SET enabled = FALSE, unavailable_reason = 'APP_EMULATOR_REJECTED' WHERE platform_code = 'doubao' AND channel = 'consumer_app';");

        // Step 3: stopped. Another usable doubao binding exists (the other node's), so the waiting task waits for capacity.
        AssertEmpty(await LeaseAsync(doubao), AppLeaseEmptyReason.BindingStopped, 600, 600);
        Assert.Equal("pending|0|-|0|0|-|-", await TaskAsync(pool.Cs, second));
        // With no usable binding left on the platform the waiting task records why; the leased task is not waiting.
        await pool.ExecAsync("UPDATE collector_app_bindings SET last_seen_at = UTC_TIMESTAMP(6) - INTERVAL 16 MINUTE WHERE id = UUID_TO_BIN(@id);", ("@id", foreign.ToString()));
        AssertEmpty(await LeaseAsync(doubao), AppLeaseEmptyReason.BindingStopped, 600, 600);
        Assert.Equal("pending|0|-|0|0|-|binding_stopped", await TaskAsync(pool.Cs, second));
        Assert.Equal($"leased|1|{node}|1|0|-|-", await TaskAsync(pool.Cs, first));

        // Step 3: cooling down.
        await SetBindingAsync("status = 'active', stop_reason = NULL, stopped_at = NULL");
        AssertEmpty(await LeaseAsync(doubao), AppLeaseEmptyReason.BindingCooldown, 90, 100);
        // Step 2 → 4: the cooldown elapsed (it ends, with an event); the device still holds the first lease.
        await SetBindingAsync("cooldown_until = UTC_TIMESTAMP(6) - INTERVAL 1 SECOND");
        AssertEmpty(await LeaseAsync(doubao), AppLeaseEmptyReason.DeviceBusy, 100, 120);
        Assert.Equal("active|-|0|0", await pool.BindingAsync(doubao));
        // binding_resumed is the operator's approval of the new binding (Pool.BindAsync).
        Assert.Equal(["binding_registered", "binding_resumed", "cooldown_expired"], await pool.EventsAsync(doubao));

        // Leasing is not sending: clearing the lease does not start a question cooldown.
        await pool.FinishAsync(first);

        // Explicit platform stops remain effective, even though legacy quotas and intervals no longer gate dispatch.
        AssertEmpty(await LeaseAsync(doubao), AppLeaseEmptyReason.PlatformPaused, 600, 600);
        Assert.Equal("pending|0|-|0|0|-|platform_paused", await TaskAsync(pool.Cs, second));
        await SetPlatformAsync("paused_at = NULL, pause_reason = NULL");
        AssertEmpty(await LeaseAsync(doubao), AppLeaseEmptyReason.PlatformUnavailable, 600, 600);
        Assert.Equal("pending|0|-|0|0|-|platform_unavailable", await TaskAsync(pool.Cs, second));

        await pool.ExecAsync("UPDATE collector_platform_entries SET enabled = TRUE, unavailable_reason = NULL WHERE platform_code = 'doubao' AND channel = 'consumer_app';");

        // Neither daily_quota=1 nor daily_cap=1 needs clearing, and no history is aged.
        await pool.ExecAsync("UPDATE collector_tasks SET next_attempt_at = UTC_TIMESTAMP(6) + INTERVAL 1 HOUR WHERE id = UUID_TO_BIN(@id);", ("@id", second.ToString()));
        AssertEmpty(await LeaseAsync(doubao), AppLeaseEmptyReason.NoTask, 60, 60);
        Assert.Equal("pending|0|-|0|0|-|-", await TaskAsync(pool.Cs, second));

        // Nothing blocks any more: the second prompt is leased; then the device is busy again for the other platform.
        await pool.ExecAsync("UPDATE collector_tasks SET next_attempt_at = UTC_TIMESTAMP(6) WHERE id = UUID_TO_BIN(@id);", ("@id", second.ToString()));
        Assert.Equal(second, (await LeaseAsync(doubao)).Task!.Id);
        AssertEmpty(await LeaseAsync(kimi), AppLeaseEmptyReason.DeviceBusy, 100, 120);
        await pool.FinishAsync(second);
        var onKimi = (await LeaseAsync(kimi)).Task!;
        Assert.Equal(("kimi", "kimi.consumer_app", cycle.Prompts[0], kimi), (onKimi.PlatformCode, onKimi.PlatformEntryCode, onKimi.PromptId, onKimi.BindingId));
        // Exactly three leases were written for the tenant, all of them App leases.
        Assert.Equal("3|3", await pool.RowAsync("""
            SELECT COUNT(*), COALESCE(SUM(app_binding_id IS NOT NULL AND session_profile_id IS NULL), 0) FROM collector_attempts
            WHERE tenant_id = UUID_TO_BIN(@tenant) AND event_kind = 'leased';
            """, ("@tenant", rig.Tenant.ToString())));
    });

    // 5.7 / 5.6a: a heartbeat extends a current lease only. A lease that ran out with no result and no failure report is not
    // touched by the web recovery or the deadline pass; it counts as that attempt's failure when another device asks for
    // work, which takes the task under the next lease version. The silent device stays in cooldown from the old lease end,
    // never gets the task back, and everything it reports late for the old lease is stale.
    [AppChannelMySqlFact]
    public Task AHeartbeatExtendsOnlyACurrentLeaseAndAnExpiredLeaseMovesToAnotherDevice() => PoolAsync(async (pool, seedTenant) =>
    {
        var rig = Build(pool.Cs, await seedTenant());
        var store = pool.Store(rig.Tenant);
        var (nodeA, _) = await pool.NodeAsync("mac");
        var (nodeB, _) = await pool.NodeAsync("mac");
        var cycle = await CycleAsync(rig, "续租", 1, "doubao");
        var (a, b) = (await pool.BindAsync(nodeA, "AI_Apps_4KB", "doubao"), await pool.BindAsync(nodeB, "AVD_B", "doubao"));
        var task = (await store.LeaseAsync(new AppLeaseCommand(nodeA, a, 60))).Task!;

        var extended = await store.HeartbeatAsync(new AppHeartbeatCommand(nodeA, task.Id, 1, 300));
        Assert.InRange(extended, DateTimeOffset.UtcNow.AddSeconds(280), DateTimeOffset.UtcNow.AddSeconds(305));
        Assert.Equal($"collecting|1|{nodeA}|1|0|-|-", await TaskAsync(pool.Cs, task.Id));
        Assert.Equal($"heartbeat|{a}|1", await pool.RowAsync("""
            SELECT event_kind, BIN_TO_UUID(app_binding_id), lease_version FROM collector_attempts
            WHERE collector_task_id = UUID_TO_BIN(@id) AND event_kind = 'heartbeat';
            """, ("@id", task.Id.ToString())));
        // Another node, another version, an unknown task, an out-of-range extension.
        foreach (var command in new[] { new AppHeartbeatCommand(nodeB, task.Id, 1, 60), new AppHeartbeatCommand(nodeA, task.Id, 2, 60),
            new AppHeartbeatCommand(nodeA, Guid.NewGuid(), 1, 60) })
            Assert.Equal(("STALE_LEASE", 409), Code(await Assert.ThrowsAsync<CollectorStoreException>(() => store.HeartbeatAsync(command))));
        Assert.Equal(("INVALID_LEASE", 400), Code(await Assert.ThrowsAsync<CollectorStoreException>(()
            => store.HeartbeatAsync(new AppHeartbeatCommand(nodeA, task.Id, 1, 4)))));

        // The lease expires. The holder's heartbeat is stale; the web recovery and the App deadline pass leave the task alone.
        await pool.ExecAsync("""
            UPDATE collector_tasks SET lease_until = UTC_TIMESTAMP(6) - INTERVAL 1 SECOND WHERE id = UUID_TO_BIN(@id);
            UPDATE collector_app_device_attempts SET lease_until = UTC_TIMESTAMP(6) - INTERVAL 1 SECOND WHERE task_id = UUID_TO_BIN(@id);
            """, ("@id", task.Id.ToString()));
        Assert.Equal(("STALE_LEASE", 409), Code(await Assert.ThrowsAsync<CollectorStoreException>(()
            => store.HeartbeatAsync(new AppHeartbeatCommand(nodeA, task.Id, 1, 60)))));
        Assert.Equal(0, await new DomesticCollectorStore(pool.Database).RecoverExpiredTasksAsync());
        Assert.Equal(0, await rig.App.ExpireOverdueTasksAsync());
        Assert.Equal($"collecting|1|{nodeA}|1|0|-|-", await TaskAsync(pool.Cs, task.Id));

        // The expired lease no longer makes the device busy, but the device could have sent until the lease ended: its
        // cooldown runs from the old lease end, and its own request does not bring the task back to it.
        AssertEmpty(await store.LeaseAsync(new AppLeaseCommand(nodeA, a, 60)), AppLeaseEmptyReason.DeviceCooldown, 90, 100);
        Assert.Equal($"collecting|1|{nodeA}|1|0|-|-", await TaskAsync(pool.Cs, task.Id));

        // Another device takes the task over with the next lease version. The expiry counts as one failed attempt, recorded
        // as an event under the binding that held the lease; the check keeps its one reservation.
        var again = (await store.LeaseAsync(new AppLeaseCommand(nodeB, b, 60))).Task!;
        Assert.Equal((task.Id, task.CheckId, 2, b), (again.Id, again.CheckId, again.LeaseVersion, again.BindingId));
        Assert.Equal($"leased|2|{nodeB}|2|1|lease_expired|-", await TaskAsync(pool.Cs, task.Id));
        var expired = await pool.LastEventAsync("lease_expired", a);
        Assert.Equal((1, 1, nodeB, "requeued"), (expired.GetProperty("leaseVersion").GetInt32(), expired.GetProperty("failureCount").GetInt32(),
            expired.GetProperty("detectedByNodeId").GetString(), expired.GetProperty("taskDisposition").GetString()));
        Assert.Equal($"{nodeA}|{task.Id}|node:{nodeB}", await pool.RowAsync(
            "SELECT node_id, BIN_TO_UUID(task_id), actor FROM collector_app_events WHERE event_kind = 'lease_expired' AND binding_id = UUID_TO_BIN(@id);",
            ("@id", a.ToString())));
        Assert.Equal("1|held|1", await pool.RowAsync("""
            SELECT COUNT(*), MIN(status), (SELECT COUNT(*) FROM credit_ledger WHERE tenant_id = UUID_TO_BIN(@tenant))
            FROM monitoring_credit_items WHERE tenant_id = UUID_TO_BIN(@tenant);
            """, ("@tenant", rig.Tenant.ToString())));
        // The old attempt row is left unresolved and the new lease has its own row.
        Assert.Equal($"1/{nodeA}/-/1,2/{nodeB}/-/1", await pool.RowAsync("""
            SELECT GROUP_CONCAT(CONCAT_WS('/', lease_version, node_id, COALESCE(send_state, '-'), failed_at IS NULL) ORDER BY lease_version SEPARATOR ',')
            FROM collector_app_device_attempts WHERE task_id = UUID_TO_BIN(@id);
            """, ("@id", task.Id.ToString())));

        // The old holder: its heartbeat, its send report, its failure report and its answer are all for a superseded lease.
        // They are kept for audit and change neither the task nor the new lease, and nothing is settled.
        Assert.Equal(("STALE_LEASE", 409), Code(await Assert.ThrowsAsync<CollectorStoreException>(()
            => store.HeartbeatAsync(new AppHeartbeatCommand(nodeA, task.Id, 1, 60)))));
        Assert.Equal("stale", await store.ReportSentAsync(new AppSentCommand(nodeA, a, task.Id, 1, DateTimeOffset.UtcNow)));
        var stale = await store.ReportFailureAsync(new AppFailureCommand(nodeA, a, AppFailureCode.CollectionTimeout, CollectorPhase.PostSend,
            new CollectorReportedTask(task.Id, 1), "unknown"));
        Assert.Equal(new AppFailureResult(AppBindingStatus.Active, null, CollectorTaskDisposition.Stale, false, false), stale);
        Assert.Equal(new CollectorResultResponse(CollectorResultDisposition.Stale, null),
            await store.SubmitResultAsync(AppCreditSqlTests.Result(nodeA, task)));
        Assert.Equal($"leased|2|{nodeB}|2|1|lease_expired|-", await TaskAsync(pool.Cs, task.Id));
        Assert.Equal("2|0", await pool.RowAsync("""
            SELECT SUM(event_kind = 'leased'), SUM(event_kind = 'failed') FROM collector_attempts
            WHERE collector_task_id = UUID_TO_BIN(@id);
            """, ("@id", task.Id.ToString())));
        Assert.Equal("pending|held|10.000|0|0", await pool.RowAsync("SELECT status FROM visibility_checks WHERE id = UUID_TO_BIN(@id);",
            ("@id", task.CheckId.ToString())) + "|" + await AppCreditSqlTests.ItemAsync(pool, task.CheckId));
        // The silent device is still cooling down from the old lease end.
        AssertEmpty(await store.LeaseAsync(new AppLeaseCommand(nodeA, a, 60)), AppLeaseEmptyReason.DeviceCooldown, 90, 100);

        // The new holder's lease is the current one.
        await store.HeartbeatAsync(new AppHeartbeatCommand(nodeB, task.Id, 2, 60));
        Assert.Equal($"collecting|2|{nodeB}|2|1|lease_expired|-", await TaskAsync(pool.Cs, task.Id));

        // The new holder fails before sending and the task waits again. The first device's cooldown is over by now; it
        // still does not get the task back, because it has attempted it.
        Assert.Equal(CollectorTaskDisposition.Requeued, (await store.ReportFailureAsync(new AppFailureCommand(nodeB, b, AppFailureCode.ContractChanged,
            CollectorPhase.PreSend, new CollectorReportedTask(task.Id, 2), "not_sent"))).TaskDisposition);
        await pool.ExecAsync("""
            UPDATE collector_app_device_attempts SET lease_until = lease_until - INTERVAL 101 SECOND
            WHERE task_id = UUID_TO_BIN(@id) AND lease_version = 1;
            """, ("@id", task.Id.ToString()));
        AssertEmpty(await store.LeaseAsync(new AppLeaseCommand(nodeA, a, 60)), AppLeaseEmptyReason.NoTask, 60, 60);
        Assert.Equal("retryable_failed|2|-|2|2|contract_changed|-", await TaskAsync(pool.Cs, task.Id));
        Assert.Equal("running|1|0|0|0|0", await RunAsync(pool.Cs, cycle.Run));
    });
}
