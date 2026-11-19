using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

internal static class AnswerProviderSchema
{
    internal const string NotMigratedCode = "ANSWER_PROVIDER_SCHEMA_NOT_MIGRATED";

    // Same rule as GeminiResultSchema / CloroKeyPoolSchema: check before reads/writes instead of guessing from SQL errors.
    internal static async Task RequireAsync(MySqlConnection connection, MySqlTransaction? transaction, CancellationToken ct)
    {
        if (!await ExistsAsync(connection, transaction, ct))
            throw new CollectorStoreException(NotMigratedCode, "API 回答 Provider 设置所需迁移 025 尚未部署。", 503);
    }

    internal static async Task<bool> ExistsAsync(MySqlConnection connection, MySqlTransaction? transaction, CancellationToken ct)
    {
        await using var command = new MySqlCommand("""
            SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE()
              AND table_name = 'answer_provider_settings'
              AND column_name IN ('provider', 'api_url', 'model', 'credential', 'credential_hint', 'enabled', 'timeout_seconds', 'options');
            """, connection, transaction);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct)) == 8;
    }
}
