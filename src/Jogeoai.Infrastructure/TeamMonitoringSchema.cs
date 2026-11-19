using System.Collections.Concurrent;
using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

/// <summary>
/// 048 团队默认监控配置的结构探测。未迁移时：团队接口返回 503 <see cref="TeamMonitoringRules.SchemaNotMigratedCode"/>；
/// 品牌目标的读写保持现状（followsTeamDefault 为 false，保存品牌目标不写跟随标记），首次开启监控走原有基线逻辑。
/// </summary>
internal static class TeamMonitoringSchema
{
    // Only a positive probe is cached (same rule as AnswerModeSchema): an unmigrated database is re-probed until 048 is applied.
    private static readonly ConcurrentDictionary<string, bool> Migrated = new(StringComparer.Ordinal);

    // Fully migrated = both tables exist and the follow flag is already NOT NULL (step 3 of 048 finished).
    internal static async Task<bool> IsMigratedAsync(MySqlConnection connection, MySqlTransaction? transaction, CancellationToken ct)
    {
        var key = $"{connection.DataSource}/{connection.Database}";
        if (Migrated.ContainsKey(key)) return true;
        await using var command = new MySqlCommand("""
            SELECT (SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE()
                      AND table_name IN ('tenant_monitoring_defaults', 'tenant_monitoring_default_cells'))
                 + (SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE()
                      AND table_name = 'site_brand_settings' AND column_name = 'monitoring_follows_default' AND is_nullable = 'NO');
            """, connection, transaction);
        var migrated = Convert.ToInt32(await command.ExecuteScalarAsync(ct)) == 3;
        if (migrated) Migrated.TryAdd(key, true);
        return migrated;
    }

    /// <summary>团队接口在读写前调用；未执行 048 时返回 503，不做任何写入。</summary>
    internal static async Task RequireAsync(MySqlConnection connection, MySqlTransaction? transaction, CancellationToken ct)
    {
        if (!await IsMigratedAsync(connection, transaction, ct)) throw Missing();
    }

    internal static CollectorStoreException Missing()
        => new(TeamMonitoringRules.SchemaNotMigratedCode, "团队默认监控配置所需的迁移 048 尚未部署。", 503);
}
