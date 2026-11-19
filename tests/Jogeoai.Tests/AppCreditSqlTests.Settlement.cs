using System.Globalization;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using static Jogeoai.Tests.AppCycleRunSqlTests;
using static Jogeoai.Tests.AppLeaseSqlTests;
using static Jogeoai.Tests.TeamMonitoringConfigSqlTests;

namespace Jogeoai.Tests;

/// <summary>
/// 结算与释放（implement 6.1 / 6.3 / 6.4）：App 的结算、释放与同一个库里真实的 API 监控路径逐列对照；
/// 重试用尽和到截止时点释放；每条流水都能追溯到目标键和 Run。
/// </summary>
public sealed partial class AppCreditSqlTests
{
    // What a capture or a release leaves behind for one check, without the reserve row (the API reserves per run, the App
    // per check): item status | amount | captured_at set | released_at set | for each ledger row: type / amount / names the
    // item's reservation / reference is monitoring:<type>:<check> / no actor / no article reservation.
    private static Task<string> SettlementAsync(Pool pool, Guid check)
        => pool.RowAsync("""
            SELECT i.status, i.amount, i.captured_at IS NOT NULL, i.released_at IS NOT NULL,
                   (SELECT GROUP_CONCAT(CONCAT_WS('/', l.entry_type, l.amount, l.monitoring_reservation_id = i.reservation_id,
                               l.external_reference = CONCAT('monitoring:', l.entry_type, ':', BIN_TO_UUID(i.check_id)),
                               l.actor_user_id IS NULL, l.reservation_id IS NULL) ORDER BY l.created_at SEPARATOR ',')
                    FROM credit_ledger l
                    WHERE l.tenant_id = i.tenant_id AND l.monitoring_check_id = i.check_id AND l.entry_type <> 'reserve')
            FROM monitoring_credit_items i WHERE i.check_id = UUID_TO_BIN(@check);
            """, ("@check", check.ToString()));

    // Δ available | Δ reserved | Δ lifetime captured over the action, and whether the action's ledger row for the check
    // carries the available balance right after it.
    private static async Task<string> DeltaAsync(Pool pool, Guid tenant, Guid check, string entryType, Func<Task> action)
    {
        decimal[] Read(string row) => row.Split('|').Select(value => decimal.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        var before = Read(await AccountAsync(pool, tenant));
        await action();
        var after = Read(await AccountAsync(pool, tenant));
        var balance = await pool.RowAsync("""
            SELECT l.balance_after = a.available_credits FROM credit_ledger l JOIN credit_accounts a ON a.tenant_id = l.tenant_id
            WHERE l.monitoring_check_id = UUID_TO_BIN(@check) AND l.entry_type = @type;
            """, ("@check", check.ToString()), ("@type", entryType));
        return string.Join("|", after.Zip(before, (now, then) => (now - then).ToString("0.###", CultureInfo.InvariantCulture))) + "|" + balance;
    }

    private static Task<string> ReasonAsync(Pool pool, Guid check, string entryType)
        => pool.RowAsync("SELECT reason FROM credit_ledger WHERE monitoring_check_id = UUID_TO_BIN(@check) AND entry_type = @type;",
            ("@check", check.ToString()), ("@type", entryType));

    [AppChannelMySqlFact]
    public Task AppCaptureAndReleaseLeaveTheSameBooksAsTheApiPathAndEveryRowIsTraceable() => PoolAsync(async (pool, seedTenant) =>
    {
        var rig = Build(pool.Cs, await seedTenant());
        var store = pool.Store(rig.Tenant);
        var (nodeA, _) = await pool.NodeAsync("mac");
        var (nodeB, _) = await pool.NodeAsync("mac");
        var (a, b) = (await pool.BindAsync(nodeA, "AVD_1", "doubao"), await pool.BindAsync(nodeB, "AVD_B", "doubao"));

        // ---- The API path, for comparison: a charged first run of two checks; one is captured, one is released.
        var apiSite = await SeedBrandAsync(pool.Cs, rig.Tenant, "API 对照", follows: false, locale: "zh-CN", created: "2026-06-20");
        await rig.PutAsync(apiSite, new SiteTargetWrite("cloro", "chatgpt", "scraped", "US", "en-US"));
        for (var i = 1; i <= 2; i++) await SeedPromptAsync(pool.Cs, apiSite, $"API 对照：第 {i} 条提问", i);
        await rig.Service.EnsureAsync(rig.Tenant, apiSite);
        var apiRun = Guid.Parse(await pool.RowAsync("SELECT BIN_TO_UUID(run_id) FROM monitoring_credit_reservations WHERE site_id = UUID_TO_BIN(@site);",
            ("@site", apiSite.ToString())));
        Assert.Equal("980.000|20.000|0.000", await AccountAsync(pool, rig.Tenant)); // 1000 − 2 × 10 reserved for the API run
        var api = new CloroVisibilityStore(pool.Database, rig.Tenant, apiRun);
        var apiTasks = await api.LeaseTasksAsync("credit-sql", 2, 60, "cloro");
        Assert.Equal(2, apiTasks.Count);
        var apiCapture = await DeltaAsync(pool, rig.Tenant, apiTasks[0].CheckId, "capture", async () =>
        {
            await api.MarkSubmittingAsync(apiTasks[0], "credit-sql");
            await api.RecordSubmissionAsync(apiTasks[0], "credit-sql", new("credit-sql-" + Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, "hash"));
        });
        var apiRelease = await DeltaAsync(pool, rig.Tenant, apiTasks[1].CheckId, "release",
            () => api.FailTaskAsync(apiTasks[1], "credit-sql", "PROVIDER_REJECTED", retryable: false));
        Assert.Equal(("0|-10|10|1", "10|-10|0|1"), (apiCapture, apiRelease));
        var (apiCaptured, apiReleased) = (await SettlementAsync(pool, apiTasks[0].CheckId), await SettlementAsync(pool, apiTasks[1].CheckId));
        Assert.Equal(("captured|10.000|1|0|capture/0.000/1/1/1/1", "released|10.000|0|1|release/10.000/1/1/1/1"), (apiCaptured, apiReleased));
        // 6.1 did not change the API path's ledger texts.
        Assert.Equal("每日监控已提交", await ReasonAsync(pool, apiTasks[0].CheckId, "capture"));

        // ---- The App path on the same tenant: three checks.
        var cycle = await CycleAsync(rig, "结算", 3, "doubao");
        Task<AppLeaseResult> LeaseAsync(string node, Guid binding) => store.LeaseAsync(new AppLeaseCommand(node, binding, 60));
        async Task<AppLeasedTask> NextAsync(string node, Guid binding)
        {
            await pool.AgePlatformAsync("doubao");
            await pool.AgeBindingAsync(binding);
            return (await LeaseAsync(node, binding)).Task!;
        }
        Task<AppFailureResult> FailAsync(string node, Guid binding, AppFailureCode code, AppLeasedTask task)
            => store.ReportFailureAsync(new AppFailureCommand(node, binding, code, CollectorPhase.PreSend, new CollectorReportedTask(task.Id, task.LeaseVersion), "not_sent"));

        // An accepted result captures the check's reservation: the same account movement and the same rows as the API capture.
        var first = (await LeaseAsync(nodeA, a)).Task!;
        Assert.Equal("980.000|10.000|10.000", await AccountAsync(pool, rig.Tenant));
        var appCapture = await DeltaAsync(pool, rig.Tenant, first.CheckId, "capture", async ()
            => Assert.Equal(new CollectorResultResponse(CollectorResultDisposition.Accepted, first.CheckId), await store.SubmitResultAsync(Result(nodeA, first) with { DeviceId = "AVD_1" })));
        Assert.Equal(apiCapture, appCapture);
        Assert.Equal(apiCaptured, await SettlementAsync(pool, first.CheckId));
        Assert.Equal(AppCreditReservations.CaptureReason, await ReasonAsync(pool, first.CheckId, "capture"));
        Assert.Equal("30.000|0.000|10.000|0.000|captured|10.000|1|1", await ReservationAsync(pool, cycle.Run));

        // Ordinary failures on two distinct devices keep a single reservation until the task's deadline.
        var second = await NextAsync(nodeB, b);
        Assert.Equal(cycle.Prompts[1], second.PromptId);
        // The run's reservation holds again: one captured, one held.
        Assert.Equal("30.000|10.000|10.000|0.000|partial|10.000|1|1", await ReservationAsync(pool, cycle.Run));
        Assert.Equal(CollectorTaskDisposition.Requeued, (await FailAsync(nodeB, b, AppFailureCode.AppCrashed, second)).TaskDisposition);
        second = await NextAsync(nodeA, a);
        Assert.Equal(CollectorTaskDisposition.Requeued, (await FailAsync(nodeA, a, AppFailureCode.AppCrashed, second)).TaskDisposition);
        Assert.Equal("held|10.000|0|0", await ItemAsync(pool, second.CheckId));
        Assert.Equal("970.000|10.000|20.000|2|4", await AccountAsync(pool, rig.Tenant) + "|" + await pool.RowAsync(
            "SELECT COUNT(*) FROM monitoring_credit_items WHERE tenant_id = UUID_TO_BIN(@tenant) AND run_id = UUID_TO_BIN(@run);",
            ("@tenant", rig.Tenant.ToString()), ("@run", cycle.Run.ToString())) + "|" + await pool.RowAsync(
            "SELECT COUNT(*) FROM monitoring_credit_items WHERE tenant_id = UUID_TO_BIN(@tenant);", ("@tenant", rig.Tenant.ToString())));
        await pool.ExecAsync("UPDATE collector_tasks SET deadline_at = UTC_TIMESTAMP(6) - INTERVAL 1 SECOND WHERE id = UUID_TO_BIN(@id);", ("@id", second.Id.ToString()));
        var appRelease = await DeltaAsync(pool, rig.Tenant, second.CheckId, "release", async ()
            => Assert.Equal(1, await rig.App.ExpireOverdueTasksAsync()));
        Assert.Equal(apiRelease, appRelease);
        Assert.Equal(apiReleased, await SettlementAsync(pool, second.CheckId));
        Assert.Equal(AppNotCollectedRules.Message(AppNotCollectedCodes.Failed), await ReasonAsync(pool, second.CheckId, "release"));
        Assert.Equal($"cancelled|{AppNotCollectedCodes.Failed}", await pool.RowAsync(
            "SELECT status, error_code FROM visibility_checks WHERE id = UUID_TO_BIN(@id);", ("@id", second.CheckId.ToString())));

        // The deadline pass releases what a leased, never finished check still holds.
        var third = await NextAsync(nodeA, a);
        Assert.Equal(cycle.Prompts[2], third.PromptId);
        Assert.Equal("held|10.000|0|0", await ItemAsync(pool, third.CheckId));
        await ExpireLeaseAsync(pool, third.Id);
        await pool.ExecAsync("UPDATE collector_tasks SET deadline_at = UTC_TIMESTAMP(6) - INTERVAL 1 SECOND WHERE id = UUID_TO_BIN(@id);", ("@id", third.Id.ToString()));
        var deadline = await DeltaAsync(pool, rig.Tenant, third.CheckId, "release", async () => Assert.Equal(1, await rig.App.ExpireOverdueTasksAsync()));
        Assert.Equal(apiRelease, deadline);
        Assert.Equal(apiReleased, await SettlementAsync(pool, third.CheckId));
        Assert.Equal(AppNotCollectedRules.Message(AppNotCollectedCodes.Capacity), await ReasonAsync(pool, third.CheckId, "release"));
        Assert.Equal(0, await rig.App.ExpireOverdueTasksAsync());

        // ---- The books at the end. Reserved 20 (API) + 30 (App); captured 10 + 10; released 10 + 20.
        Assert.Equal("980.000|0.000|20.000", await AccountAsync(pool, rig.Tenant));
        // The same mix (captured and released, nothing held) gives the same reservation state on both paths.
        Assert.Equal("30.000|0.000|10.000|20.000|partial|10.000|1|1", await ReservationAsync(pool, cycle.Run));
        Assert.Equal("20.000|0.000|10.000|10.000|partial|10.000|1|1", await ReservationAsync(pool, apiRun));
        Assert.Equal("partial_failed|3|1|0|1|1", await RunAsync(pool.Cs, cycle.Run));
        // Every ledger row that names a check leads to the check's target key and, through its reservation, to its run.
        Assert.Equal("8|8|9", await pool.RowAsync("""
            SELECT COUNT(*), COALESCE(SUM(e.target_key IS NOT NULL AND run.id = chk.run_id), 0),
                   (SELECT COUNT(*) FROM credit_ledger WHERE tenant_id = UUID_TO_BIN(@tenant))
            FROM credit_ledger l
            LEFT JOIN monitoring_credit_items i ON i.tenant_id = l.tenant_id AND i.check_id = l.monitoring_check_id
            LEFT JOIN visibility_checks chk ON chk.id = i.check_id
            LEFT JOIN run_engines e ON e.id = chk.run_target_id
            LEFT JOIN monitoring_credit_reservations r ON r.tenant_id = l.tenant_id AND r.id = l.monitoring_reservation_id
            LEFT JOIN visibility_runs run ON run.id = r.run_id
            WHERE l.tenant_id = UUID_TO_BIN(@tenant) AND l.monitoring_check_id IS NOT NULL;
            """, ("@tenant", rig.Tenant.ToString())));
        // The usage view's amount source (capture rows carry 0; the charge is the item's amount): 10 per captured check.
        Assert.Equal("2|20.000", await pool.RowAsync("""
            SELECT COUNT(*), SUM(i.amount) FROM credit_ledger l
            JOIN monitoring_credit_items i ON i.tenant_id = l.tenant_id AND i.check_id = l.monitoring_check_id
            WHERE l.tenant_id = UUID_TO_BIN(@tenant) AND l.entry_type = 'capture';
            """, ("@tenant", rig.Tenant.ToString())));
    });
}
