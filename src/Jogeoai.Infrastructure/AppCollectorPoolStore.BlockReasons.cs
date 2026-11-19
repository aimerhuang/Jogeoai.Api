using Jogeoai.Domain;
using MySqlConnector;
using static Jogeoai.Infrastructure.DomesticCollectorStore;

namespace Jogeoai.Infrastructure;

/// <summary>
/// collector_tasks.last_block_reason（implement 5.6c）：等待中的任务最近一次“因为绑定或平台的原因领不到”的原因，
/// 截止处理按它判断未采集的原因码；为空时按容量不足处理。只有三种情况写入：
/// <list type="bullet">
/// <item>平台被暂停 → platform_paused；</item>
/// <item>平台的 App 入口停用 → platform_unavailable；</item>
/// <item>请求领取的绑定已停止，并且该平台此刻没有任何可用的绑定（状态为 active 且 15 分钟内上报或领取过）→ binding_stopped。
/// 还有别的绑定可用时不写：这些任务等的是容量。</item>
/// </list>
/// 一次领取走到“选任务”这一步，说明这个平台此刻没有上述阻塞，之前写下的这三种原因随即清空。
/// 间隔、每日上限、冷却、设备占用、没有任务都不写入。第四种取值 credit_insufficient 在选任务时按租户写入（implement 6.2），
/// 不随这三种一起清空。每次最多处理 <see cref="BlockReasonBatch"/> 条，跳过被别的事务锁住的任务。
/// </summary>
public sealed partial class AppCollectorPoolStore
{
    /// <summary>一次领取请求最多写入或清空 last_block_reason 的任务数；其余的留给之后的请求。</summary>
    internal const int BlockReasonBatch = 500;

    private static readonly string OwnBlockReasons =
        $"'{AppTaskBlockReasons.BindingStopped}', '{AppTaskBlockReasons.PlatformPaused}', '{AppTaskBlockReasons.PlatformUnavailable}'";

    private async Task StampBindingStoppedAsync(MySqlConnection c, MySqlTransaction t, string platformCode, CancellationToken ct)
    {
        var usable = await QuerySingleAsync(c, t, """
            SELECT 1 FROM collector_app_bindings
            WHERE platform_code = @platform AND status = 'active' AND last_seen_at > UTC_TIMESTAMP(6) - INTERVAL @offline MINUTE LIMIT 1;
            """, ct, ("@platform", platformCode), ("@offline", AppPoolRules.BindingOfflineMinutes));
        if (usable is null) await StampWaitingTasksAsync(c, t, platformCode, AppTaskBlockReasons.BindingStopped, ct);
    }

    // The tasks a lease could have returned if the block did not exist, back-off included; closed tenants are never touched.
    private async Task StampWaitingTasksAsync(MySqlConnection c, MySqlTransaction t, string platformCode, string reason, CancellationToken ct)
    {
        var tenants = OpenTenants();
        if (tenants.Count == 0) return;
        var ids = await QueryRowsAsync(c, t, $"""
            SELECT BIN_TO_UUID(id) FROM collector_tasks
            WHERE channel = '{AppChannel}' AND platform_code = @platform AND tenant_id IN ({TenantList(tenants)})
              AND deadline_at > UTC_TIMESTAMP(6) AND {LeasableStatus}
              AND (last_block_reason IS NULL OR last_block_reason <> @reason)
            ORDER BY fair_rank, created_at, id
            LIMIT {BlockReasonBatch}
            FOR UPDATE SKIP LOCKED;
            """, ct, [("@platform", platformCode), ("@reason", reason), .. TenantParameters(tenants)]);
        await SetBlockReasonAsync(c, t, ids, reason, ct);
    }

    /// <summary>
    /// implement 6.2：这个平台上等待中的任务里，租户的可用积分不够一次检测、检测又没有预留的，都记下 credit_insufficient
    /// （已有预留的任务不需要新的积分，不记）。这个取值不在 <see cref="OwnBlockReasons"/> 里：之后的领取走到“选任务”时不会把它清掉，
    /// 只有任务真的被领取时才清空。余额不加锁读取；每次最多处理 <see cref="BlockReasonBatch"/> 条。
    /// </summary>
    private static async Task StampCreditBlockedAsync(MySqlConnection c, MySqlTransaction t, string platformCode, IReadOnlyList<Guid> tenants,
        CancellationToken ct)
    {
        var ids = await QueryRowsAsync(c, t, $"""
            SELECT BIN_TO_UUID(task.id) FROM collector_tasks task
            WHERE channel = '{AppChannel}' AND platform_code = @platform AND tenant_id IN ({TenantList(tenants)})
              AND deadline_at > UTC_TIMESTAMP(6) AND {LeasableStatus}
              AND (last_block_reason IS NULL OR last_block_reason <> @reason)
              AND NOT {HeldItemExists} AND {TenantLacksCredit}
            ORDER BY fair_rank, created_at, id
            LIMIT {BlockReasonBatch}
            FOR UPDATE OF task SKIP LOCKED;
            """, ct, [("@platform", platformCode), ("@reason", AppTaskBlockReasons.CreditInsufficient),
                ("@price", PromptMonitoringPricing.UnitPrice), .. TenantParameters(tenants)]);
        await SetBlockReasonAsync(c, t, ids, AppTaskBlockReasons.CreditInsufficient, ct);
    }

    private static async Task ClearBlockReasonsAsync(MySqlConnection c, MySqlTransaction t, string platformCode, CancellationToken ct)
    {
        var ids = await QueryRowsAsync(c, t, $"""
            SELECT BIN_TO_UUID(id) FROM collector_tasks
            WHERE channel = '{AppChannel}' AND platform_code = @platform
              AND status IN ('pending', 'retryable_failed', 'leased', 'collecting') AND last_block_reason IN ({OwnBlockReasons})
            LIMIT {BlockReasonBatch}
            FOR UPDATE SKIP LOCKED;
            """, ct, ("@platform", platformCode));
        await SetBlockReasonAsync(c, t, ids, null, ct);
    }

    private static async Task SetBlockReasonAsync(MySqlConnection c, MySqlTransaction t, List<string[]> ids, string? reason, CancellationToken ct)
    {
        if (ids.Count == 0) return;
        await ExecuteAsync(c, t, $"""
            UPDATE collector_tasks SET last_block_reason = @reason, updated_at = UTC_TIMESTAMP(6)
            WHERE id IN ({string.Join(", ", ids.Select((_, i) => $"UUID_TO_BIN(@id{i})"))});
            """, ct, [("@reason", reason), .. ids.Select((row, i) => (object)($"@id{i}", row[0]))]);
    }

    /// <summary>
    /// design 6.3：网页领取请求里的 sessionProfileId 其实是一个 App 绑定的标识时留下审计事件。网页领取本身已经返回 403
    /// （它的事务已回滚），所以这里用单独的连接写入；未执行 049 的库上什么都不做，写入失败也不影响那个 403。
    /// </summary>
    internal static async Task AuditBindingUsedAsWebProfileAsync(MySqlDatabase database, Guid sessionProfileId, string nodeId, CancellationToken ct)
    {
        try
        {
            await using var connection = database.CreateConnection();
            await connection.OpenAsync(ct);
            if (!await AppChannelSchema.IsMigratedAsync(connection, null, ct)) return;
            var binding = await QuerySingleAsync(connection, null,
                "SELECT platform_code FROM collector_app_bindings WHERE id = UUID_TO_BIN(@id);", ct, ("@id", sessionProfileId.ToString()));
            if (binding is null) return;
            var node = string.IsNullOrWhiteSpace(nodeId) ? "unknown" : nodeId.Trim()[..Math.Min(nodeId.Trim().Length, 100)];
            await InsertEventAsync(connection, null, new AppEvent("binding_forbidden", $"web-collector:{node}", null, sessionProfileId, binding[0],
                Detail: new { route = "internal/collector/leases", reason = "app_binding_as_web_session_profile", webNodeId = node }), ct);
        }
        // The audit must never turn the web collector's 403 into a 500.
        catch (MySqlException)
        {
        }
    }
}
