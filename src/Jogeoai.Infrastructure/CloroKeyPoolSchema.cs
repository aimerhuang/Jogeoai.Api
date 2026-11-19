using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

internal static class CloroKeyPoolSchema
{
    internal const string NotMigratedCode = "CLORO_KEY_POOL_SCHEMA_NOT_MIGRATED";

    // Same rule as GeminiResultSchema: check before leasing/writes instead of guessing from arbitrary SQL errors.
    internal static async Task RequireAsync(MySqlConnection connection, MySqlTransaction? transaction, CancellationToken ct)
    {
        if (!await ExistsAsync(connection, transaction, ct))
            throw new CollectorStoreException(NotMigratedCode, "Cloro key 池所需迁移 022 尚未部署。", 503);
    }

    internal static async Task<bool> ExistsAsync(MySqlConnection connection, MySqlTransaction? transaction, CancellationToken ct)
    {
        await using var command = new MySqlCommand("""
            SELECT (SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE()
                      AND table_name IN ('cloro_api_keys', 'cloro_key_events'))
                 + (SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE()
                      AND table_name = 'provider_tasks'
                      AND column_name IN ('cloro_key_id', 'credit_resubmit_count', 'pool_exhausted_since'));
            """, connection, transaction);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct)) == 5;
    }
}
