using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

internal static class GeminiResultSchema
{
    // Do not catch arbitrary SQL errors as missing schema. Check before writes/leasing.
    internal static async Task RequireAsync(MySqlConnection connection, MySqlTransaction? transaction, CancellationToken ct)
    {
        await using var command = new MySqlCommand("""
            SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE()
              AND ((table_name = 'visibility_checks' AND column_name = 'result_metadata' AND data_type = 'json')
                OR (table_name = 'site_targets' AND column_name = 'monitoring_enabled_at' AND data_type = 'datetime'));
            """, connection, transaction);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(ct)) != 2)
            throw new CollectorStoreException("GEMINI_RESULT_SCHEMA_NOT_MIGRATED", "回答质量与监控启用时间所需迁移尚未部署。", 503);
    }
}
