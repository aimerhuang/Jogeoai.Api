using System.Globalization;
using Jogeoai.Domain;
using MySqlConnector;
using static Jogeoai.Infrastructure.DomesticCollectorStore;

namespace Jogeoai.Infrastructure;

/// <summary>
/// App 检测的按次计费（design 第 7 节，总 PRD KD-23）：领取时为这一个检测预留一次的积分，结果被接受时结算，
/// 任务终态或到截止时点时释放。结算、释放和预留汇总用的是 API 监控的同一套方法（<see cref="CloroVisibilityStore"/>）。
/// <para>
/// 加锁顺序：调用方已经持有任务行的锁（绑定 → 平台 → 任务），这里再锁租户的 credit_accounts 行，之后才是检测、
/// site_analysis_state 和 Run。API 路径同样先锁账户行再锁 site_analysis_state，所以两条路径不会在这两把锁上互相等待；
/// App 的 Run、检测、计费项与 API 的不是同一批行。
/// </para>
/// </summary>
internal static class AppCreditReservations
{
    /// <summary>预留流水（credit_ledger.reason）的说明。</summary>
    internal const string ReserveReason = "App 采集预留";

    /// <summary>结算流水（credit_ledger.reason）的说明。</summary>
    internal const string CaptureReason = "App 采集已提交";

    /// <summary>预留流水的 external_reference 前缀，后接检测标识；API 的整轮预留是 monitoring:reserve:&lt;runId&gt;。</summary>
    internal const string ReserveReferencePrefix = "monitoring:reserve:check:";

    internal enum Outcome
    {
        /// <summary>这个检测已经有一条 held 的计费项（失败后重新排队、租约过期后再次领取）：不重复预留。</summary>
        AlreadyHeld,

        /// <summary>刚刚预留了一次的积分。</summary>
        Reserved,

        /// <summary>可用积分不够一次检测：这个任务这一次不领取。</summary>
        Insufficient,

        /// <summary>这个检测的计费项已经结算或释放过，不能再预留（不应出现）。</summary>
        Closed,
    }

    /// <summary>
    /// 为一个即将被领取的检测预留一次的积分。余额条件只有一条：可用积分不少于单价，不为 API 监控留保护额（先到先用）。
    /// 余额先不加锁读一次，不够就直接返回，这样余额不足的租户的账户行不会被这次领取一直锁到提交。
    /// </summary>
    internal static async Task<Outcome> ReserveAsync(MySqlConnection c, MySqlTransaction t, Guid tenantId, Guid runId, Guid checkId,
        CancellationToken ct)
    {
        var (tenant, check, run) = (("@tenant", (object)tenantId.ToString()), ("@check", (object)checkId.ToString()), ("@run", (object)runId.ToString()));
        var item = await QuerySingleAsync(c, t, """
            SELECT status FROM monitoring_credit_items
            WHERE tenant_id = UUID_TO_BIN(@tenant) AND check_id = UUID_TO_BIN(@check) FOR UPDATE;
            """, ct, tenant, check);
        if (item is not null) return item[0] == "held" ? Outcome.AlreadyHeld : Outcome.Closed;

        var price = PromptMonitoringPricing.UnitPrice;
        const string balanceSql = "SELECT available_credits FROM credit_accounts WHERE tenant_id = UUID_TO_BIN(@tenant)";
        var glance = await QuerySingleAsync(c, t, balanceSql + ";", ct, tenant);
        if (glance is null)
            await ExecuteAsync(c, t, "INSERT IGNORE INTO credit_accounts (tenant_id, updated_at) VALUES (UUID_TO_BIN(@tenant), UTC_TIMESTAMP(6));", ct, tenant);
        else if (Amount(glance[0]) < price) return Outcome.Insufficient;
        var account = await QuerySingleAsync(c, t, balanceSql + " FOR UPDATE;", ct, tenant);
        if (account is null || Amount(account[0]) < price) return Outcome.Insufficient;

        await ExecuteAsync(c, t, """
            UPDATE credit_accounts
            SET available_credits = available_credits - @amount, reserved_credits = reserved_credits + @amount,
                version = version + 1, updated_at = UTC_TIMESTAMP(6)
            WHERE tenant_id = UUID_TO_BIN(@tenant);
            """, ct, tenant, ("@amount", price));
        // One reservation per run, created by the run's first lease. Leases of one tenant are serialized by the account lock.
        var reservation = await QuerySingleAsync(c, t, """
            SELECT BIN_TO_UUID(id) FROM monitoring_credit_reservations
            WHERE tenant_id = UUID_TO_BIN(@tenant) AND run_id = UUID_TO_BIN(@run) FOR UPDATE;
            """, ct, tenant, run);
        var reservationId = reservation is null ? Guid.NewGuid() : Guid.Parse(reservation[0]);
        var id = ("@reservation", (object)reservationId.ToString());
        if (reservation is null)
            // estimated_credits is the whole cycle (total checks × unit price); held / captured / released follow the items.
            await ExecuteAsync(c, t, """
                INSERT INTO monitoring_credit_reservations
                    (id, tenant_id, site_id, run_id, unit_price, estimated_credits, held_credits, captured_credits, released_credits, status, created_at)
                SELECT UUID_TO_BIN(@reservation), tenant_id, site_id, id, @unit, GREATEST(total_checks, 1) * @unit, 0, 0, 0, 'held', UTC_TIMESTAMP(6)
                FROM visibility_runs WHERE tenant_id = UUID_TO_BIN(@tenant) AND id = UUID_TO_BIN(@run);
                """, ct, tenant, run, id, ("@unit", price));
        await ExecuteAsync(c, t, """
            INSERT INTO monitoring_credit_items (id, tenant_id, site_id, reservation_id, run_id, check_id, amount, status, created_at)
            SELECT UUID_TO_BIN(@item), tenant_id, site_id, id, run_id, UUID_TO_BIN(@check), @amount, 'held', UTC_TIMESTAMP(6)
            FROM monitoring_credit_reservations WHERE tenant_id = UUID_TO_BIN(@tenant) AND id = UUID_TO_BIN(@reservation);
            INSERT INTO credit_ledger
                (id, tenant_id, entry_type, amount, balance_after, monitoring_reservation_id, monitoring_check_id,
                 external_reference, reason, created_at)
            SELECT UUID_TO_BIN(@ledger), tenant_id, 'reserve', -@amount, available_credits, UUID_TO_BIN(@reservation), UUID_TO_BIN(@check),
                   CONCAT(@prefix, @check), @reason, UTC_TIMESTAMP(6)
            FROM credit_accounts WHERE tenant_id = UUID_TO_BIN(@tenant);
            """, ct, tenant, check, id, ("@item", Guid.NewGuid().ToString()), ("@ledger", Guid.NewGuid().ToString()), ("@amount", price),
            ("@prefix", ReserveReferencePrefix), ("@reason", ReserveReason));
        await CloroVisibilityStore.RefreshMonitoringReservationAsync(c, t, tenantId, reservationId, ct);
        return Outcome.Reserved;
    }

    /// <summary>
    /// 结果被接受时结算这个检测的预留。返回 false 表示它没有 held 的计费项（不应出现）：什么都不结算，由调用方记录。
    /// 无论有没有计费项都先锁账户行，保持“账户 → 检测 / 分析状态 / Run”的顺序。
    /// </summary>
    internal static async Task<bool> CaptureAsync(MySqlConnection c, MySqlTransaction t, Guid tenantId, Guid checkId, CancellationToken ct)
    {
        await LockAccountAsync(c, t, tenantId, ct);
        var item = await QuerySingleAsync(c, t, """
            SELECT status FROM monitoring_credit_items
            WHERE tenant_id = UUID_TO_BIN(@tenant) AND check_id = UUID_TO_BIN(@check) FOR UPDATE;
            """, ct, ("@tenant", tenantId.ToString()), ("@check", checkId.ToString()));
        if (item is null || item[0] != "held") return false;
        await CloroVisibilityStore.CaptureMonitoringItemAsync(c, t, tenantId, checkId, CaptureReason, ct);
        return true;
    }

    /// <summary>
    /// 任务终态（重试用尽）或到截止时点时释放这个检测的预留；没有 held 的计费项时什么都不做。
    /// 同样先锁账户行：之后调用方要锁检测和 site_analysis_state。
    /// </summary>
    internal static async Task ReleaseAsync(MySqlConnection c, MySqlTransaction t, Guid tenantId, Guid checkId, string reason, CancellationToken ct)
    {
        await LockAccountAsync(c, t, tenantId, ct);
        await CloroVisibilityStore.ReleaseMonitoringItemAsync(c, t, tenantId, checkId, reason, ct);
    }

    private static Task LockAccountAsync(MySqlConnection c, MySqlTransaction t, Guid tenantId, CancellationToken ct)
        => QuerySingleAsync(c, t, "SELECT 1 FROM credit_accounts WHERE tenant_id = UUID_TO_BIN(@tenant) FOR UPDATE;", ct,
            ("@tenant", tenantId.ToString()));

    private static decimal Amount(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);
}
