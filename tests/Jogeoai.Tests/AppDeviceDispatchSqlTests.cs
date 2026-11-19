using Jogeoai.Domain;
using static Jogeoai.Tests.AppCycleRunSqlTests;
using static Jogeoai.Tests.AppLeaseSqlTests;

namespace Jogeoai.Tests;

[Collection(AppChannelSqlCollection.Name)]
public sealed class AppDeviceDispatchSqlTests
{
    [AppChannelMySqlFact]
    public Task RetriedTaskKeepsItsBrandOneAnswerAndOneChargeWhileOtherDevicesRunIndependently() => PoolAsync(async (pool, seedTenant) =>
    {
        var rig = Build(pool.Cs, await seedTenant());
        var store = pool.Store(rig.Tenant);
        var brand1 = await CycleAsync(rig, "甲", 1, "doubao");
        var (node, _) = await pool.NodeAsync("mac");
        var a = await pool.BindAsync(node, "A", "doubao");
        var ak = await pool.BindAsync(node, "A", "kimi");
        var b = await pool.BindAsync(node, "B", "doubao");
        var task = (await store.LeaseAsync(new(node, a, 60))).Task!;
        Assert.Equal(brand1.Site, task.SiteId);
        var at = DateTimeOffset.UtcNow;
        var send = new AppSentCommand(node, a, task.Id, task.LeaseVersion, at);
        Assert.Equal("accepted", await store.ReportSentAsync(send));
        Assert.Equal("duplicate", await store.ReportSentAsync(send));
        var fail = new AppFailureCommand(node, a, AppFailureCode.CollectionTimeout, CollectorPhase.PostSend,
            new(task.Id, task.LeaseVersion), "sent", at);
        Assert.Equal(CollectorTaskDisposition.Requeued, (await store.ReportFailureAsync(fail)).TaskDisposition);
        Assert.Equal(AppLeaseEmptyReason.DeviceCooldown, (await store.LeaseAsync(new(node, ak, 60))).Reason);
        var retry = (await store.LeaseAsync(new(node, b, 60))).Task!;
        Assert.Equal((task.Id, task.LeaseVersion + 1, task.SiteId), (retry.Id, retry.LeaseVersion, retry.SiteId));
        Assert.Equal(CollectorTaskDisposition.Stale, (await store.ReportFailureAsync(fail with { FailureCode = AppFailureCode.LoginRequired })).TaskDisposition);
        Assert.StartsWith("active|", await pool.BindingAsync(a));
        var answerA = AppCreditSqlTests.Result(node, task) with { DeviceId = "A", SentAt = at, CompletedAt = DateTimeOffset.UtcNow };
        Assert.Equal(CollectorResultDisposition.Stale, (await store.SubmitResultAsync(answerA)).Disposition);
        var answerB = AppCreditSqlTests.Result(node, retry, "result-key-0002") with { DeviceId = "B", SentAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow };
        Assert.Equal(CollectorResultDisposition.Accepted, (await store.SubmitResultAsync(answerB)).Disposition);
        Assert.Equal(CollectorResultDisposition.Duplicate, (await store.SubmitResultAsync(answerB)).Disposition);
        Assert.Equal("990.000|0.000|10.000", await AppCreditSqlTests.AccountAsync(pool, rig.Tenant));

        var brand2 = await CycleAsync(rig, "乙", 1, "kimi");
        await pool.ExecAsync("UPDATE collector_app_device_attempts SET sent_at=UTC_TIMESTAMP(6)-INTERVAL 100 SECOND WHERE task_id=UUID_TO_BIN(@task) AND lease_version=1;",
            ("@task", task.Id.ToString()));
        Assert.Equal(brand2.Site, (await store.LeaseAsync(new(node, ak, 60))).Task!.SiteId);
    });

    // A4 and the unknown send state: whatever the send state, a failure ends that attempt only and the task goes back to the
    // queue. Another device takes it; the device that failed never gets it again.
    [AppChannelMySqlFact]
    public Task PreSendFailsOnlyThisAttemptWithoutCooldownAndAnUnknownSendRequeuesForAnotherDevice() => PoolAsync(async (pool, seedTenant) =>
    {
        var rig = Build(pool.Cs, await seedTenant());
        var store = pool.Store(rig.Tenant);
        await CycleAsync(rig, "甲", 1, "doubao", "kimi");
        var (node, _) = await pool.NodeAsync("mac");
        var a = await pool.BindAsync(node, "A", "doubao");
        var ak = await pool.BindAsync(node, "A", "kimi");
        var b = await pool.BindAsync(node, "B", "doubao");
        var concurrent = await Task.WhenAll(store.LeaseAsync(new(node, a, 60)), store.LeaseAsync(new(node, ak, 60)));
        var winner = Assert.Single(concurrent, x => x.Task is not null).Task!;
        Assert.Equal(AppLeaseEmptyReason.DeviceBusy, Assert.Single(concurrent, x => x.Task is null).Reason);
        var failed = await store.ReportFailureAsync(new(node, winner.BindingId, AppFailureCode.ContractChanged, CollectorPhase.PreSend,
            new(winner.Id, winner.LeaseVersion), "not_sent"));
        Assert.Equal((CollectorTaskDisposition.Requeued, AppBindingStatus.Active), (failed.TaskDisposition!.Value, failed.BindingStatus));
        Assert.Equal(AppLeaseEmptyReason.NoTask, (await store.LeaseAsync(new(node, winner.BindingId, 60))).Reason);
        var other = (await store.LeaseAsync(new(node, winner.BindingId == a ? ak : a, 60))).Task!;
        Assert.NotEqual(winner.Id, other.Id);
        var bk = await pool.BindAsync(node, "B", "kimi");
        var retry = (await store.LeaseAsync(new(node, winner.BindingId == a ? b : bk, 60))).Task!;
        Assert.Equal((winner.Id, winner.CheckId, 2), (retry.Id, retry.CheckId, retry.LeaseVersion));

        // B cannot tell whether the question went out. The task is requeued all the same; only B's cooldown differs: it
        // runs from the report, the latest moment the question could have been sent.
        var unknown = await store.ReportFailureAsync(new(node, retry.BindingId, AppFailureCode.AppCrashed, CollectorPhase.PostSend,
            new(retry.Id, retry.LeaseVersion), "unknown"));
        Assert.Equal((CollectorTaskDisposition.Requeued, AppBindingStatus.Active), (unknown.TaskDisposition!.Value, unknown.BindingStatus));
        Assert.Equal("retryable_failed|2|-|2|2|app_crashed|-", await TaskAsync(pool.Cs, winner.Id));
        Assert.Equal("held|10.000|0|0", await AppCreditSqlTests.ItemAsync(pool, winner.CheckId));
        Assert.Equal("unknown|1|1", await pool.RowAsync("""
            SELECT send_state, sent_at IS NULL, failed_at IS NOT NULL FROM collector_app_device_attempts
            WHERE task_id = UUID_TO_BIN(@id) AND lease_version = 2;
            """, ("@id", winner.Id.ToString())));
        AssertEmpty(await store.LeaseAsync(new(node, retry.BindingId, 60)), AppLeaseEmptyReason.DeviceCooldown, 90, 100);
        // After B's cooldown the task is still waiting, but not for B: B has already attempted it.
        await pool.ExecAsync("""
            UPDATE collector_app_device_attempts SET failed_at = failed_at - INTERVAL 101 SECOND
            WHERE task_id = UUID_TO_BIN(@id) AND lease_version = 2;
            """, ("@id", winner.Id.ToString()));
        AssertEmpty(await store.LeaseAsync(new(node, retry.BindingId, 60)), AppLeaseEmptyReason.NoTask, 60, 60);
        // A third device takes the same task under a new lease; the check still has its one reservation.
        var c = await pool.BindAsync(node, "C", winner.PlatformCode);
        var third = (await store.LeaseAsync(new(node, c, 60))).Task!;
        Assert.Equal((winner.Id, winner.CheckId, 3, c), (third.Id, third.CheckId, third.LeaseVersion, third.BindingId));
        Assert.Equal(1, await pool.CountAsync("SELECT COUNT(*) FROM monitoring_credit_items WHERE check_id = UUID_TO_BIN(@check);",
            ("@check", winner.CheckId.ToString())));
        // The late report of the superseded lease changes nothing.
        Assert.Equal(CollectorTaskDisposition.Stale, (await store.ReportFailureAsync(new(node, retry.BindingId, AppFailureCode.CollectionTimeout,
            CollectorPhase.PostSend, new(retry.Id, retry.LeaseVersion), "unknown"))).TaskDisposition);
        Assert.Equal($"leased|3|{node}|3|2|app_crashed|-", await TaskAsync(pool.Cs, winner.Id));
    });

    // A8: a verification screen closes the task, it is not asked again on another device, and the same device's other App
    // goes on. A6 / A7: a lease that ran out with no report counts as that attempt's failure: another device takes the task
    // under a new lease, the silent device never gets it back and stays in cooldown from the old lease end, and whatever it
    // reports late for the superseded lease is audit only.
    [AppChannelMySqlFact]
    public Task AVerificationVerdictClosesTheTaskAndAnUnreportedExpiredLeaseMovesToAnotherDevice() => PoolAsync(async (pool, seedTenant) =>
    {
        var rig = Build(pool.Cs, await seedTenant());
        var store = pool.Store(rig.Tenant);
        await CycleAsync(rig, "甲", 1, "doubao", "kimi");
        var (node, _) = await pool.NodeAsync("mac");
        var a = await pool.BindAsync(node, "A", "doubao");
        var b = await pool.BindAsync(node, "B", "doubao");
        var bk = await pool.BindAsync(node, "B", "kimi");
        var ak = await pool.BindAsync(node, "A", "kimi");
        var k = (await store.LeaseAsync(new(node, ak, 60))).Task!;
        var verdict = await store.ReportFailureAsync(new(node, ak, AppFailureCode.ChallengeRequired,
            CollectorPhase.PreSend, new(k.Id, k.LeaseVersion), "not_sent"));
        Assert.Equal((CollectorTaskDisposition.Failed, AppBindingStopReason.ChallengeRequired), (verdict.TaskDisposition, verdict.StopReason));
        Assert.Equal("terminal_failed|1|-|1|1|challenge_required|-", await TaskAsync(pool.Cs, k.Id));
        Assert.Equal("released|10.000|0|1", await AppCreditSqlTests.ItemAsync(pool, k.CheckId));
        AssertEmpty(await store.LeaseAsync(new(node, bk, 60)), AppLeaseEmptyReason.NoTask, 60, 60);
        Assert.Equal("stopped|challenge_required|1|0", await pool.BindingAsync(ak));
        Assert.Equal("active|-|0|0", await pool.BindingAsync(a));

        // B takes the doubao task and goes silent; its lease ran out a second ago.
        var task = (await store.LeaseAsync(new(node, b, 60))).Task!;
        await pool.ExecAsync("""
            UPDATE collector_tasks SET lease_until = UTC_TIMESTAMP(6) - INTERVAL 1 SECOND WHERE id = UUID_TO_BIN(@id);
            UPDATE collector_app_device_attempts SET lease_until = UTC_TIMESTAMP(6) - INTERVAL 1 SECOND WHERE task_id = UUID_TO_BIN(@id);
            """, ("@id", task.Id.ToString()));
        // B is not busy any more, but it may have sent until the lease ended: its cooldown runs from the old lease end.
        AssertEmpty(await store.LeaseAsync(new(node, bk, 60)), AppLeaseEmptyReason.DeviceCooldown, 90, 100);
        AssertEmpty(await store.LeaseAsync(new(node, b, 60)), AppLeaseEmptyReason.DeviceCooldown, 90, 100);

        // A takes the same task under the next lease version; the expiry is recorded as a failed attempt of B's binding.
        var retry = (await store.LeaseAsync(new(node, a, 60))).Task!;
        Assert.Equal((task.Id, task.CheckId, 2, a), (retry.Id, retry.CheckId, retry.LeaseVersion, retry.BindingId));
        Assert.Equal($"leased|2|{node}|2|1|lease_expired|-", await TaskAsync(pool.Cs, task.Id));
        var expired = await pool.LastEventAsync("lease_expired", b);
        Assert.Equal((1, 1, node, "requeued"), (expired.GetProperty("leaseVersion").GetInt32(), expired.GetProperty("failureCount").GetInt32(),
            expired.GetProperty("detectedByNodeId").GetString(), expired.GetProperty("taskDisposition").GetString()));
        Assert.Equal("held|10.000|0|0", await AppCreditSqlTests.ItemAsync(pool, task.CheckId));
        // B's attempt row is left as it was (no send report, no failure): that is what keeps B's cooldown running.
        Assert.Equal("1/B/-/1,2/A/-/1", await pool.RowAsync("""
            SELECT GROUP_CONCAT(CONCAT_WS('/', lease_version, device_id, COALESCE(send_state, '-'), failed_at IS NULL) ORDER BY lease_version SEPARATOR ',')
            FROM collector_app_device_attempts WHERE task_id = UUID_TO_BIN(@id);
            """, ("@id", task.Id.ToString())));
        AssertEmpty(await store.LeaseAsync(new(node, b, 60)), AppLeaseEmptyReason.DeviceCooldown, 90, 100);

        // Whatever B reports late for lease 1 is stale: a send, a failure, a complete answer. Lease 2 is untouched.
        Assert.Equal("stale", await store.ReportSentAsync(new AppSentCommand(node, b, task.Id, task.LeaseVersion, DateTimeOffset.UtcNow)));
        var late = await store.ReportFailureAsync(new(node, b, AppFailureCode.LoginRequired, CollectorPhase.PreSend,
            new(task.Id, task.LeaseVersion), "not_sent"));
        Assert.Equal((CollectorTaskDisposition.Stale, AppBindingStatus.Active), (late.TaskDisposition!.Value, late.BindingStatus));
        Assert.Equal(CollectorResultDisposition.Stale, (await store.SubmitResultAsync(AppCreditSqlTests.Result(node, task) with { DeviceId = "B" })).Disposition);
        Assert.Equal($"leased|2|{node}|2|1|lease_expired|-", await TaskAsync(pool.Cs, task.Id));
        Assert.Equal("pending|held|10.000|0|0", await pool.RowAsync("SELECT status FROM visibility_checks WHERE id = UUID_TO_BIN(@id);",
            ("@id", task.CheckId.ToString())) + "|" + await AppCreditSqlTests.ItemAsync(pool, task.CheckId));
        Assert.Equal("1/B/-/1,2/A/-/1", await pool.RowAsync("""
            SELECT GROUP_CONCAT(CONCAT_WS('/', lease_version, device_id, COALESCE(send_state, '-'), failed_at IS NULL) ORDER BY lease_version SEPARATOR ',')
            FROM collector_app_device_attempts WHERE task_id = UUID_TO_BIN(@id);
            """, ("@id", task.Id.ToString())));

        // A fails before sending, so the task waits again. B's cooldown is over by now, and B still does not get it: it
        // has attempted this task. A device that has not does.
        Assert.Equal(CollectorTaskDisposition.Requeued, (await store.ReportFailureAsync(new(node, a, AppFailureCode.ContractChanged,
            CollectorPhase.PreSend, new(retry.Id, retry.LeaseVersion), "not_sent"))).TaskDisposition);
        await pool.ExecAsync("""
            UPDATE collector_app_device_attempts SET lease_until = lease_until - INTERVAL 101 SECOND
            WHERE task_id = UUID_TO_BIN(@id) AND lease_version = 1;
            """, ("@id", task.Id.ToString()));
        AssertEmpty(await store.LeaseAsync(new(node, b, 60)), AppLeaseEmptyReason.NoTask, 60, 60);
        var c = await pool.BindAsync(node, "C", "doubao");
        var third = (await store.LeaseAsync(new(node, c, 60))).Task!;
        Assert.Equal((task.Id, 3, c), (third.Id, third.LeaseVersion, third.BindingId));
    });
}
