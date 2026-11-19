using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using static Jogeoai.Tests.AppCycleRunSqlTests;

namespace Jogeoai.Tests;

public sealed partial class AppLeaseSqlTests
{
    private static AppFailureResult Result(AppBindingStopReason? stop, CollectorTaskDisposition? disposition, bool paused = false, bool unavailable = false)
        => new(stop is null ? AppBindingStatus.Active : AppBindingStatus.Stopped, stop, disposition, paused, unavailable);

    [AppChannelMySqlFact]
    public Task OrdinaryFailuresReleaseOnlyTheirAttemptEvenAfterThreeFailures() => PoolAsync(async (pool, seedTenant) =>
    {
        var rig = Build(pool.Cs, await seedTenant());
        var store = pool.Store(rig.Tenant);
        var (node, _) = await pool.NodeAsync("mac");
        var (foreign, _) = await pool.NodeAsync("mac");
        await CycleAsync(rig, "普通失败", 1, "doubao");
        Guid? taskId = null;
        Guid? check = null;
        var version = 0;
        var codes = new[] { AppFailureCode.ContractChanged, AppFailureCode.AppCrashed, AppFailureCode.CollectionTimeout };
        foreach (var code in codes)
        {
            var binding = await pool.BindAsync(node, $"AVD_{++version}", "doubao");
            var task = (await store.LeaseAsync(new AppLeaseCommand(node, binding, 120))).Task!;
            taskId ??= task.Id;
            check ??= task.CheckId;
            Assert.Equal((taskId.Value, check.Value, version), (task.Id, task.CheckId, task.LeaseVersion));
            var sent = code == AppFailureCode.CollectionTimeout;
            var report = new AppFailureCommand(node, binding, code, sent ? CollectorPhase.PostSend : CollectorPhase.PreSend,
                new(task.Id, task.LeaseVersion), sent ? "sent" : "not_sent", sent ? DateTimeOffset.UtcNow : null);
            Assert.Equal(("APP_BINDING_FORBIDDEN", 403), Code(await Assert.ThrowsAsync<CollectorStoreException>(
                () => store.ReportFailureAsync(report with { NodeId = foreign }))));
            Assert.Equal(Result(null, CollectorTaskDisposition.Requeued), await store.ReportFailureAsync(report));
            Assert.Equal(Result(null, CollectorTaskDisposition.Stale), await store.ReportFailureAsync(report));
            Assert.Equal("active|-|0|0", await pool.BindingAsync(binding));
            Assert.Equal("held|10.000|0|0", await AppCreditSqlTests.ItemAsync(pool, task.CheckId));
            // Same device cannot pick up this task again. A confirmed send additionally cools down this VM only.
            var empty = await store.LeaseAsync(new AppLeaseCommand(node, binding, 120));
            Assert.Null(empty.Task);
            if (sent) Assert.Equal(AppLeaseEmptyReason.DeviceCooldown, empty.Reason);
        }
        var fourth = await pool.BindAsync(node, "AVD_4", "doubao");
        var next = (await store.LeaseAsync(new AppLeaseCommand(node, fourth, 120))).Task!;
        Assert.Equal((taskId!.Value, check!.Value, 4), (next.Id, next.CheckId, next.LeaseVersion));
        Assert.Equal("1|1|1", await AppCreditSqlTests.CreditRowsAsync(pool, rig.Tenant));
        Assert.Equal(3, await pool.CountAsync("SELECT COUNT(*) FROM collector_app_events WHERE task_id = UUID_TO_BIN(@id) AND event_kind = 'failure_reported';",
            ("@id", next.Id.ToString())));
        var wrongPlatform = await pool.BindAsync(node, "AVD_5", "kimi");
        var wrong = new AppFailureCommand(node, wrongPlatform, AppFailureCode.AppCrashed, CollectorPhase.PreSend,
            new(next.Id, next.LeaseVersion), "not_sent");
        Assert.Equal(("APP_BINDING_FORBIDDEN", 403), Code(await Assert.ThrowsAsync<CollectorStoreException>(() => store.ReportFailureAsync(wrong))));
        Assert.Equal(("TASK_NOT_FOUND", 404), Code(await Assert.ThrowsAsync<CollectorStoreException>(
            () => store.ReportFailureAsync(wrong with { Task = new(Guid.NewGuid(), 1) }))));
    });

    // The safety baseline. A verification screen, an App that refuses the emulator and a rate limit close the task: the
    // question is not asked again on another device. The binding stop, the cooldown, the breaker and the entry are as before.
    [AppChannelMySqlFact]
    public Task VerificationEmulatorAndRateLimitVerdictsCloseTheTaskAndPlatformSafetyStopsArePreserved() => PoolAsync(async (pool, seedTenant) =>
    {
        var rig = Build(pool.Cs, await seedTenant());
        var store = pool.Store(rig.Tenant);
        var (node, _) = await pool.NodeAsync("mac");
        await CycleAsync(rig, "安全停止", 1, "kimi", "qwen", "deepseek");
        foreach (var (platform, code, stop) in new[]
        {
            ("kimi", AppFailureCode.RateLimited, (AppBindingStopReason?)null),
            ("qwen", AppFailureCode.EmulatorRejected, AppBindingStopReason.EmulatorRejected),
            ("deepseek", AppFailureCode.ChallengeRequired, AppBindingStopReason.ChallengeRequired),
        })
        {
            var a = await pool.BindAsync(node, "safety-a", platform);
            var b = await pool.BindAsync(node, "safety-b", platform);
            var task = (await store.LeaseAsync(new AppLeaseCommand(node, a, 120))).Task!;
            var result = await store.ReportFailureAsync(new AppFailureCommand(node, a, code, CollectorPhase.PreSend,
                new(task.Id, task.LeaseVersion), "not_sent"));
            Assert.Equal(Result(stop, CollectorTaskDisposition.Failed), result with { CooldownUntil = null });
            Assert.Equal($"terminal_failed|1|-|1|1|{AppContractNames.ToStorage(code)}|-", await TaskAsync(pool.Cs, task.Id));
            Assert.Equal("released|10.000|0|1", await AppCreditSqlTests.ItemAsync(pool, task.CheckId));
            // Another device that is free, active and not cooling down finds nothing: the task is closed, not waiting.
            AssertEmpty(await store.LeaseAsync(new AppLeaseCommand(node, b, 120)), AppLeaseEmptyReason.NoTask, 60, 60);
            if (code == AppFailureCode.RateLimited)
            {
                Assert.NotNull(result.CooldownUntil);
                Assert.Equal(TimeSpan.Zero, result.CooldownUntil.Value.ToOffset(TimeSpan.FromHours(8)).TimeOfDay);
                Assert.Equal(AppLeaseEmptyReason.BindingCooldown, (await store.LeaseAsync(new AppLeaseCommand(node, a, 120))).Reason);
            }
            else if (code == AppFailureCode.EmulatorRejected)
            {
                Assert.True((await store.ReportFailureAsync(new AppFailureCommand(node, b, code, CollectorPhase.Probe, null))).PlatformUnavailable);
            }
            else
            {
                Assert.True((await store.ReportFailureAsync(new AppFailureCommand(node, b, code, CollectorPhase.Probe, null))).PlatformPaused);
                Assert.True(await store.ResumePlatformAsync(platform, null));
                Assert.False(await store.ResumePlatformAsync(platform, null));
            }
        }
    });

    // PRD rule 3 with the safety baseline: a login screen stops this App on this device until a person has dealt with it,
    // but for the task it is an ordinary failed attempt. The task goes back to the queue and another device takes it.
    [AppChannelMySqlFact]
    public Task LoginRequiredStopsTheBindingAndTheTaskGoesToAnotherDevice() => PoolAsync(async (pool, seedTenant) =>
    {
        var rig = Build(pool.Cs, await seedTenant());
        var store = pool.Store(rig.Tenant);
        var (node, _) = await pool.NodeAsync("mac");
        var cycle = await CycleAsync(rig, "登录失效", 1, "doubao");
        var a = await pool.BindAsync(node, "login-a", "doubao");
        var b = await pool.BindAsync(node, "login-b", "doubao");
        var task = (await store.LeaseAsync(new AppLeaseCommand(node, a, 120))).Task!;
        var report = new AppFailureCommand(node, a, AppFailureCode.LoginRequired, CollectorPhase.PreSend, new(task.Id, task.LeaseVersion), "not_sent");
        Assert.Equal(Result(AppBindingStopReason.LoginRequired, CollectorTaskDisposition.Requeued), await store.ReportFailureAsync(report));
        Assert.Equal("stopped|login_required|1|0", await pool.BindingAsync(a));
        Assert.Equal("retryable_failed|1|-|1|1|login_required|-", await TaskAsync(pool.Cs, task.Id));
        // Not closed: the check is still open and keeps its one reservation.
        Assert.Equal("pending|held|10.000|0|0", await pool.RowAsync("SELECT status FROM visibility_checks WHERE id = UUID_TO_BIN(@id);",
            ("@id", task.CheckId.ToString())) + "|" + await AppCreditSqlTests.ItemAsync(pool, task.CheckId));

        // The stopped binding gets nothing until an operator resumes it; the other device takes the same task.
        AssertEmpty(await store.LeaseAsync(new AppLeaseCommand(node, a, 120)), AppLeaseEmptyReason.BindingStopped, 600, 600);
        var retry = (await store.LeaseAsync(new AppLeaseCommand(node, b, 120))).Task!;
        Assert.Equal((task.Id, task.CheckId, 2, b), (retry.Id, retry.CheckId, retry.LeaseVersion, retry.BindingId));
        Assert.Equal("1|1|1", await AppCreditSqlTests.CreditRowsAsync(pool, rig.Tenant));
        // The first device's repeated report is for a superseded lease: audit only, and its stop is not lifted or changed.
        Assert.Equal(Result(AppBindingStopReason.LoginRequired, CollectorTaskDisposition.Stale), await store.ReportFailureAsync(report));
        Assert.Equal($"leased|2|{node}|2|1|login_required|-", await TaskAsync(pool.Cs, task.Id));

        // One formal answer and one charge, on the second device.
        Assert.Equal(new CollectorResultResponse(CollectorResultDisposition.Accepted, task.CheckId),
            await store.SubmitResultAsync(AppCreditSqlTests.Result(node, retry) with { DeviceId = "login-b" }));
        Assert.Equal("captured|10.000|1|0", await AppCreditSqlTests.ItemAsync(pool, task.CheckId));
        Assert.Equal("succeeded|1|1|0|1|1", await RunAsync(pool.Cs, cycle.Run));
        Assert.Equal("login_required,-|not_sent,sent", await pool.RowAsync("""
            SELECT GROUP_CONCAT(COALESCE(failure_code, '-') ORDER BY lease_version), GROUP_CONCAT(send_state ORDER BY lease_version)
            FROM collector_app_device_attempts WHERE task_id = UUID_TO_BIN(@id);
            """, ("@id", task.Id.ToString())));

        // The verification breaker still counts the login report: a second binding reporting one pauses the platform.
        Assert.True((await store.ReportFailureAsync(new AppFailureCommand(node, b, AppFailureCode.LoginRequired, CollectorPhase.Probe, null))).PlatformPaused);
        Assert.True(await store.ResumePlatformAsync("doubao", null));
        Assert.False(await store.ResumePlatformAsync("doubao", null));
    });
}
