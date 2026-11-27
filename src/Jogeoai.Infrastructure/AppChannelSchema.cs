using System.Collections.Concurrent;
using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

/// <summary>
/// 049 App 采集渠道的结构探测。未迁移时：目录不返回 App 行，现有功能不受影响；
/// 只有 App 相关的写入和领取返回 503 <see cref="NotMigratedCode"/>。
/// </summary>
internal static class AppChannelSchema
{
    /// <summary>迁移 049 未执行时返回的稳定错误码。</summary>
    internal const string NotMigratedCode = "APP_CHANNEL_SCHEMA_NOT_MIGRATED";

    // 6 new tables + 8 new columns + 4 widened CHECK constraints (db/migrations/049_app_channel.sql).
    private const int ExpectedObjects = 18;

    // Migrations are forward-only while a process runs (049 rollback refuses once app data exists and requires the old build),
    // so only a positive probe is cached; an unmigrated database is re-probed until 049 is applied.
    private static readonly ConcurrentDictionary<string, bool> Migrated = new(StringComparer.Ordinal);

    /// <summary>049 是否已在当前库执行；肯定的结果按「数据源 / 库名」缓存，否定的结果每次重新探测。</summary>
    internal static async Task<bool> IsMigratedAsync(MySqlConnection connection, MySqlTransaction? transaction, CancellationToken ct)
    {
        var key = $"{connection.DataSource}/{connection.Database}";
        if (Migrated.ContainsKey(key)) return true;
        var migrated = await ProbeAsync(connection, transaction, ct);
        if (migrated) Migrated.TryAdd(key, true);
        return migrated;
    }

    /// <summary>不经缓存直接探测 049 新增的表、列和放开后的 CHECK 是否齐全；缺任何一项都视为未迁移。</summary>
    // Do not catch arbitrary SQL errors as missing schema; probe information_schema explicitly.
    internal static async Task<bool> ProbeAsync(MySqlConnection connection, MySqlTransaction? transaction, CancellationToken ct)
    {
        await using var command = new MySqlCommand("""
            SELECT (SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE()
                      AND table_name IN ('collector_app_nodes', 'collector_app_bindings', 'collector_app_platform_states',
                                         'collector_app_events', 'collector_app_daily_reports', 'prompt_app_exclusions'))
                 + (SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE()
                      AND ((table_name = 'visibility_checks' AND column_name = 'app_binding_id')
                        OR (table_name = 'collector_platform_entries' AND column_name = 'unavailable_reason')
                        OR (table_name = 'collector_attempts' AND column_name = 'app_binding_id')
                        OR (table_name = 'collector_tasks' AND column_name IN ('deadline_at', 'fair_rank',
                              'prompt_rule_version', 'lease_app_binding_id', 'last_block_reason'))))
                 + (SELECT COUNT(*) FROM information_schema.check_constraints WHERE constraint_schema = DATABASE()
                      AND ((constraint_name = 'ck_visibility_checks_capture_mode' AND check_clause LIKE '%''app%')
                        OR (constraint_name IN ('ck_collector_entries_channel', 'ck_collector_tasks_channel')
                              AND check_clause LIKE '%consumer_app%')
                        OR (constraint_name = 'ck_collector_attempts_failure_code' AND check_clause LIKE '%emulator_rejected%')));
            """, connection, transaction);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct)) == ExpectedObjects;
    }

    /// <summary>App 相关的写入和领取前调用；未执行 049 时返回 503，不做任何写入。</summary>
    internal static async Task RequireAsync(MySqlConnection connection, MySqlTransaction? transaction, CancellationToken ct)
    {
        if (!await IsMigratedAsync(connection, transaction, ct)) throw Missing();
    }

    /// <summary>迁移 049 未执行时抛出的异常：503 <see cref="NotMigratedCode"/>。</summary>
    internal static CollectorStoreException Missing()
        => new(NotMigratedCode, "App 采集渠道所需的迁移 049 尚未部署。", 503);

    private static readonly ConcurrentDictionary<string, bool> DeviceDispatchMigrated = new(StringComparer.Ordinal);

    /// <summary>App 设备级派单的写入、领取和按尝试的读取前调用；未执行 051 时返回 503，不做任何写入。</summary>
    internal static async Task RequireDeviceDispatchAsync(MySqlConnection c, MySqlTransaction? t, CancellationToken ct)
    {
        if (!await HasDeviceDispatchAsync(c, t, ct))
            throw new CollectorStoreException(NotMigratedCode, "App 设备级派单所需的迁移 051 尚未部署。", 503);
    }

    /// <summary>051 的设备尝试表是否已在当前库；肯定的结果按「数据源 / 库名」缓存，否定的结果每次重新探测。</summary>
    internal static async Task<bool> HasDeviceDispatchAsync(MySqlConnection c, MySqlTransaction? t, CancellationToken ct)
    {
        var key = $"{c.DataSource}/{c.Database}";
        if (DeviceDispatchMigrated.ContainsKey(key)) return true;
        await using var query = new MySqlCommand("""
            SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE()
              AND table_name = 'collector_app_device_attempts' AND column_name IN
              ('task_id','lease_version','binding_id','node_id','device_id','leased_at','lease_until','sent_at','failure_code','send_state','failed_at');
            """, c, t);
        if (Convert.ToInt32(await query.ExecuteScalarAsync(ct)) != 11) return false;
        DeviceDispatchMigrated.TryAdd(key, true);
        return true;
    }

    private static readonly ConcurrentDictionary<string, bool> BindingApprovalMigrated = new(StringComparer.Ordinal);

    /// <summary>
    /// 绑定审批与节点设备上限（迁移 052）用到的读写前调用：绑定上报、领取、发题回执、失败上报、绑定停用与恢复、运营的节点与绑定视图、设备上限的设置。
    /// 未执行 052 时返回 503，不做任何写入。
    /// </summary>
    internal static async Task RequireBindingApprovalAsync(MySqlConnection c, MySqlTransaction? t, CancellationToken ct)
    {
        if (!await HasBindingApprovalAsync(c, t, ct))
            throw new CollectorStoreException(NotMigratedCode, "App 绑定审批所需的迁移 052 尚未部署。", 503);
    }

    /// <summary>
    /// 052 是否已在当前库完成：两列都在，并且最后一步的 CHECK 也在。CHECK 是 052 的完成标记，它出现之前回填可能还没有做完，
    /// 这时把 approved_at 为空当作“待批准”会把全部现有绑定都算成待批准，所以只加了列的库仍按未迁移处理。
    /// 肯定的结果按「数据源 / 库名」缓存，否定的结果每次重新探测。
    /// </summary>
    internal static async Task<bool> HasBindingApprovalAsync(MySqlConnection c, MySqlTransaction? t, CancellationToken ct)
    {
        var key = $"{c.DataSource}/{c.Database}";
        if (BindingApprovalMigrated.ContainsKey(key)) return true;
        if (!await ProbeBindingApprovalAsync(c, t, ct)) return false;
        BindingApprovalMigrated.TryAdd(key, true);
        return true;
    }

    /// <summary>不经缓存直接探测 052 的两列和 CHECK 是否齐全。</summary>
    internal static async Task<bool> ProbeBindingApprovalAsync(MySqlConnection c, MySqlTransaction? t, CancellationToken ct)
    {
        await using var query = new MySqlCommand("""
            SELECT (SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE()
                      AND ((table_name = 'collector_app_bindings' AND column_name = 'approved_at')
                        OR (table_name = 'collector_app_nodes' AND column_name = 'max_devices')))
                 + (SELECT COUNT(*) FROM information_schema.check_constraints WHERE constraint_schema = DATABASE()
                      AND constraint_name = 'ck_collector_app_nodes_max_devices');
            """, c, t);
        return Convert.ToInt32(await query.ExecuteScalarAsync(ct)) == 3;
    }
}
