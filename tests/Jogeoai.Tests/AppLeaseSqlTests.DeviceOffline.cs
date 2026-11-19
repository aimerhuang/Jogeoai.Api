using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using static Jogeoai.Tests.AppCreditSqlTests;
using static Jogeoai.Tests.AppCycleRunSqlTests;

namespace Jogeoai.Tests;

public sealed partial class AppLeaseSqlTests
{
    [AppChannelMySqlFact]
    public Task OfflineFailureRequeuesOnlyTheTaskAndDeviceHealthIsReportedSeparately() => PoolAsync(async (pool, seedTenant) =>
    {
        var rig = Build(pool.Cs, await seedTenant());
        var store = pool.Store(rig.Tenant);
        var (node, _) = await pool.NodeAsync("mac");
        var cycle = await CycleAsync(rig, "离线", 2, "doubao");
        var label = $"doubao-{Guid.NewGuid():N}";
        var a = await pool.BindAsync(node, "AVD_A", "doubao", label);
        var b = await pool.BindAsync(node, "AVD_B", "doubao");
        Task<AppLeaseResult> Lease(Guid binding) => store.LeaseAsync(new AppLeaseCommand(node, binding, 120));
        async Task<AppBindingState> Health(string health) => Assert.Single((await store.ReportBindingsAsync(new AppBindingReportCommand(
            node, "test-rev", [Report("AVD_A", "doubao", label, health)]))).Bindings);

        var task = (await Lease(a)).Task!;
        var report = new AppFailureCommand(node, a, AppFailureCode.DeviceOffline, CollectorPhase.PreSend, new(task.Id, task.LeaseVersion), "not_sent");
        Assert.Equal(Result(null, CollectorTaskDisposition.Requeued), await store.ReportFailureAsync(report));
        Assert.Equal("active|-|0|0", await pool.BindingAsync(a));
        Assert.Equal("held|10.000|0|0", await ItemAsync(pool, task.CheckId));
        Assert.Equal("not_sent|1|1", await pool.RowAsync("""
            SELECT send_state, sent_at IS NULL, failed_at IS NOT NULL FROM collector_app_device_attempts
            WHERE task_id = UUID_TO_BIN(@id) AND lease_version = 1;
            """, ("@id", task.Id.ToString())));
        // An actual offline observation is distinct from a failed attempt. B needs neither a global wait nor A to come back.
        Assert.Equal(AppBindingStopReason.DeviceOffline, (await Health("device_offline")).StopReason);
        Assert.Equal(AppLeaseEmptyReason.BindingStopped, (await Lease(a)).Reason);
        var taken = (await Lease(b)).Task!;
        Assert.Equal((task.Id, task.CheckId, b, 2), (taken.Id, taken.CheckId, taken.BindingId, taken.LeaseVersion));
        Assert.Equal(CollectorTaskDisposition.Stale, (await store.ReportFailureAsync(report)).TaskDisposition);
        Assert.Equal("held|10.000|0|0", await ItemAsync(pool, task.CheckId));

        // After reconnection, A may work on another question even while B still owns the failed question.
        Assert.Equal(AppBindingStatus.Active, (await Health("ok")).Status);
        var other = (await Lease(a)).Task!;
        Assert.Equal(cycle.Prompts[1], other.PromptId);
        Assert.NotEqual(task.Id, other.Id);

        // A login stop is never cleared by offline/online health reports. The task it was reported on is not closed: it goes
        // back to the queue with its reservation, like after any other failed attempt.
        Assert.Equal(Result(AppBindingStopReason.LoginRequired, CollectorTaskDisposition.Requeued),
            await store.ReportFailureAsync(new AppFailureCommand(node, a, AppFailureCode.LoginRequired, CollectorPhase.PreSend,
                new(other.Id, other.LeaseVersion), "not_sent")));
        Assert.Equal(AppBindingStopReason.LoginRequired, (await Health("device_offline")).StopReason);
        Assert.Equal(AppBindingStopReason.LoginRequired, (await Health("ok")).StopReason);
        Assert.Equal("held|10.000|0|0", await ItemAsync(pool, other.CheckId));
        Assert.Equal("retryable_failed|1|-|1|1|login_required|-", await TaskAsync(pool.Cs, other.Id));
    });
}
