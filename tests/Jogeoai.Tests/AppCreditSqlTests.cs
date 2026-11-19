using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using static Jogeoai.Tests.AppCycleRunSqlTests;
using static Jogeoai.Tests.AppLeaseSqlTests;
using static Jogeoai.Tests.TeamMonitoringConfigSqlTests;

namespace Jogeoai.Tests;

/// <summary>
/// 隔离库（已执行 001–049、库名含 _test）上的 App 按次计费（implement 6.1 / 6.2 / 6.4，design 第 7 节）：领取时预留一次的积分、
/// 余额不足时不领取、再次领取不重复预留、结果被接受时结算、任务终态与截止时释放，以及与 API 监控路径的结算、释放逐列对照。
/// 未设置 JOGEOAI_APP_CHANNEL_TEST_CONNECTION 时跳过，绝不使用业务库 jogeoai。每个用例只用自己新建的租户、节点和绑定，
/// 结束时删除它们写下的全部行并核对整库每张表的行数。时间推进靠改写数据库里的时间戳，不用 sleep。
/// </summary>
[Collection(AppChannelSqlCollection.Name)]
public sealed partial class AppCreditSqlTests
{
    internal const string Sha = "ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12";

    private static (string, int) Code(CollectorStoreException exception) => (exception.Code, exception.StatusCode);

    // available | reserved | lifetime captured
    internal static Task<string> AccountAsync(Pool pool, Guid tenant)
        => pool.RowAsync("SELECT available_credits, reserved_credits, lifetime_captured FROM credit_accounts WHERE tenant_id = UUID_TO_BIN(@tenant);",
            ("@tenant", tenant.ToString()));

    internal static Task SetBalanceAsync(Pool pool, Guid tenant, decimal available)
        => pool.ExecAsync("UPDATE credit_accounts SET available_credits = @amount WHERE tenant_id = UUID_TO_BIN(@tenant);",
            ("@amount", available), ("@tenant", tenant.ToString()));

    // reservations | items | ledger rows of the tenant
    internal static Task<string> CreditRowsAsync(Pool pool, Guid tenant)
        => pool.RowAsync("""
            SELECT (SELECT COUNT(*) FROM monitoring_credit_reservations WHERE tenant_id = UUID_TO_BIN(@tenant)),
                   (SELECT COUNT(*) FROM monitoring_credit_items WHERE tenant_id = UUID_TO_BIN(@tenant)),
                   (SELECT COUNT(*) FROM credit_ledger WHERE tenant_id = UUID_TO_BIN(@tenant));
            """, ("@tenant", tenant.ToString()));

    // estimated | held | captured | released | status | unit price | captured_at set | released_at set
    internal static Task<string> ReservationAsync(Pool pool, Guid run)
        => pool.RowAsync("""
            SELECT estimated_credits, held_credits, captured_credits, released_credits, status, unit_price, captured_at IS NOT NULL, released_at IS NOT NULL
            FROM monitoring_credit_reservations WHERE run_id = UUID_TO_BIN(@run);
            """, ("@run", run.ToString()));

    // status | amount | captured_at set | released_at set of the check's credit item; "none" when it has none.
    internal static async Task<string> ItemAsync(Pool pool, Guid check)
    {
        var rows = await RowsAsync(pool.Cs, """
            SELECT status, amount, captured_at IS NOT NULL, released_at IS NOT NULL FROM monitoring_credit_items WHERE check_id = UUID_TO_BIN(@check);
            """, ("@check", check.ToString()));
        return rows.Count == 0 ? "none" : string.Join("|", Assert.Single(rows));
    }

    // The ledger rows that name the check, oldest first: type | amount | balance after | reason | reference | names the check's reservation.
    internal static async Task<string[]> LedgerAsync(Pool pool, Guid check)
        => (await RowsAsync(pool.Cs, """
            SELECT CONCAT_WS('|', l.entry_type, l.amount, l.balance_after, l.reason, l.external_reference,
                       l.monitoring_reservation_id = (SELECT i.reservation_id FROM monitoring_credit_items i WHERE i.check_id = l.monitoring_check_id))
            FROM credit_ledger l WHERE l.monitoring_check_id = UUID_TO_BIN(@check) ORDER BY l.created_at, l.entry_type DESC;
            """, ("@check", check.ToString()))).Select(row => row[0]!).ToArray();

    /// <summary>A result that passes every check; with citations its status is read, without them absent.</summary>
    internal static AppResultCommand Result(string node, AppLeasedTask task, string key = "result-key-0001", string answer = "推荐随手记和鲨鱼记账。",
        params AppResultCitation[] citations)
    {
        var now = DateTimeOffset.UtcNow;
        return new(node, task.Id, task.LeaseVersion, key, "collector-rev-1", now, now, answer, citations,
            citations.Length > 0 ? AppCitationsStatus.Read : AppCitationsStatus.Absent, null, AppVisibleState.On, AppVisibleState.Off, ["记账 工具"],
            "AI_Apps_4KB", "15", "arm64-v8a", "com.example.app", "9.9.9", Sha);
    }

    internal static Task ExpireLeaseAsync(Pool pool, Guid task)
        => pool.ExecAsync("UPDATE collector_tasks SET lease_until = UTC_TIMESTAMP(6) - INTERVAL 1 SECOND WHERE id = UUID_TO_BIN(@id);", ("@id", task.ToString()));

    // 6.2: the first lease of a check reserves 10 credits and writes the item, the ledger row and (once per run) the
    // reservation; leasing the same check again after a failure or an expired lease reserves nothing.
    [AppChannelMySqlFact]
    public Task ALeaseReservesTenCreditsOncePerCheckAndEveryRowIsTraceable() => PoolAsync(async (pool, seedTenant) =>
    {
        var rig = Build(pool.Cs, await seedTenant());
        var store = pool.Store(rig.Tenant);
        var (nodeA, _) = await pool.NodeAsync("mac");
        var (nodeB, _) = await pool.NodeAsync("mac");
        var cycle = await CycleAsync(rig, "预留", 2, "doubao");
        var (a, b, c) = (await pool.BindAsync(nodeA, "AVD_1", "doubao"), await pool.BindAsync(nodeB, "AVD_B", "doubao"), await pool.BindAsync(nodeA, "AVD_2", "doubao"));

        // Creating the cycle run wrote no credit row (design 4.2 step 6).
        Assert.Equal("0|0|0", await CreditRowsAsync(pool, rig.Tenant));
        Assert.Equal("1000.000|0.000|0.000", await AccountAsync(pool, rig.Tenant));

        var task = (await store.LeaseAsync(new AppLeaseCommand(nodeA, a, 60))).Task!;
        Assert.Equal(cycle.Prompts[0], task.PromptId);
        Assert.Equal("990.000|10.000|0.000", await AccountAsync(pool, rig.Tenant));
        Assert.Equal("1|1|1", await CreditRowsAsync(pool, rig.Tenant));
        // The reservation is the run's: the estimate is the whole cycle (2 checks), held follows the leased checks.
        Assert.Equal("20.000|10.000|0.000|0.000|held|10.000|0|0", await ReservationAsync(pool, cycle.Run));
        Assert.Equal($"{rig.Tenant}|{cycle.Site}", await pool.RowAsync(
            "SELECT BIN_TO_UUID(tenant_id), BIN_TO_UUID(site_id) FROM monitoring_credit_reservations WHERE run_id = UUID_TO_BIN(@run);", ("@run", cycle.Run.ToString())));
        Assert.Equal("held|10.000|0|0", await ItemAsync(pool, task.CheckId));
        Assert.Equal($"{rig.Tenant}|{cycle.Site}|{cycle.Run}|1", await pool.RowAsync("""
            SELECT BIN_TO_UUID(i.tenant_id), BIN_TO_UUID(i.site_id), BIN_TO_UUID(i.run_id),
                   i.reservation_id = (SELECT r.id FROM monitoring_credit_reservations r WHERE r.run_id = i.run_id)
            FROM monitoring_credit_items i WHERE i.check_id = UUID_TO_BIN(@check);
            """, ("@check", task.CheckId.ToString())));
        Assert.Equal([$"reserve|-10.000|990.000|{AppCreditReservations.ReserveReason}|{AppCreditReservations.ReserveReferencePrefix}{task.CheckId}|1"],
            await LedgerAsync(pool, task.CheckId));
        // Ledger → item → check → target key, and ledger → reservation → run → slot.
        Assert.Equal($"{CollectionIdentity.AppTargetKey("doubao")}|20000103T0911|10.000|{cycle.Run}|app_collector|app", await pool.RowAsync("""
            SELECT e.target_key, s.local_slot_key, i.amount, BIN_TO_UUID(run.id), chk.provider, chk.capture_mode
            FROM credit_ledger l
            JOIN monitoring_credit_items i ON i.tenant_id = l.tenant_id AND i.check_id = l.monitoring_check_id
            JOIN visibility_checks chk ON chk.id = i.check_id
            JOIN run_engines e ON e.id = chk.run_target_id
            JOIN monitoring_credit_reservations r ON r.tenant_id = l.tenant_id AND r.id = l.monitoring_reservation_id
            JOIN visibility_runs run ON run.id = r.run_id
            JOIN schedule_slots s ON s.run_id = run.id
            WHERE l.tenant_id = UUID_TO_BIN(@tenant) AND l.entry_type = 'reserve';
            """, ("@tenant", rig.Tenant.ToString())));

        // A failure requeues the task; another binding leases the same check: no second reservation.
        await store.ReportFailureAsync(new AppFailureCommand(nodeA, a, AppFailureCode.ContractChanged, CollectorPhase.PreSend,
            new CollectorReportedTask(task.Id, task.LeaseVersion), "not_sent"));
        Assert.Equal("held|10.000|0|0", await ItemAsync(pool, task.CheckId));
        await pool.AgePlatformAsync("doubao");
        var second = (await store.LeaseAsync(new AppLeaseCommand(nodeB, b, 60))).Task!;
        Assert.Equal((task.Id, 2), (second.Id, second.LeaseVersion));
        Assert.Equal("990.000|10.000|0.000|1|1|1", await AccountAsync(pool, rig.Tenant) + "|" + await CreditRowsAsync(pool, rig.Tenant));

        // A second ordinary pre-send failure, then another device: still one reservation.
        await store.ReportFailureAsync(new AppFailureCommand(nodeB, b, AppFailureCode.AppCrashed, CollectorPhase.PreSend,
            new CollectorReportedTask(second.Id, second.LeaseVersion), "not_sent"));
        var third = (await store.LeaseAsync(new AppLeaseCommand(nodeA, c, 60))).Task!;
        Assert.Equal((task.Id, 3), (third.Id, third.LeaseVersion));
        Assert.Equal($"leased|3|{nodeA}|3|2|app_crashed|-", await TaskAsync(pool.Cs, task.Id));
        Assert.Equal("990.000|10.000|0.000|1|1|1", await AccountAsync(pool, rig.Tenant) + "|" + await CreditRowsAsync(pool, rig.Tenant));
        Assert.Equal("20.000|10.000|0.000|0.000|held|10.000|0|0", await ReservationAsync(pool, cycle.Run));

        // The second check of the run joins the run's one reservation.
        await pool.FinishAsync(task.Id);
        await pool.AgePlatformAsync("doubao");
        await pool.AgeBindingAsync(c);
        var next = (await store.LeaseAsync(new AppLeaseCommand(nodeA, c, 60))).Task!;
        Assert.Equal(cycle.Prompts[1], next.PromptId);
        Assert.Equal("980.000|20.000|0.000|1|2|2", await AccountAsync(pool, rig.Tenant) + "|" + await CreditRowsAsync(pool, rig.Tenant));
        Assert.Equal("20.000|20.000|0.000|0.000|held|10.000|0|0", await ReservationAsync(pool, cycle.Run));
        Assert.Equal([$"reserve|-10.000|980.000|{AppCreditReservations.ReserveReason}|{AppCreditReservations.ReserveReferencePrefix}{next.CheckId}|1"],
            await LedgerAsync(pool, next.CheckId));
        // The price is the monitoring unit price; no protection floor exists anywhere in the reservation.
        Assert.Equal(10m, PromptMonitoringPricing.UnitPrice);
    });
}
