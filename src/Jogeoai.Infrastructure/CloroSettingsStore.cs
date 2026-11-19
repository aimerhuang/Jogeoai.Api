using MySqlConnector;
using Jogeoai.Providers.Abstractions;

namespace Jogeoai.Infrastructure;

public sealed class CloroSettingsStore(MySqlDatabase database) : ICloroSettingsStore
{
    public async Task<CloroConnectionSettings?> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        const string sql = """
            SELECT api_key, api_url, enabled, updated_at
            FROM cloro_provider_settings
            WHERE id = 1
            LIMIT 1;
            """;
        await using var command = new MySqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        var updatedAt = reader.IsDBNull(3)
            ? (DateTimeOffset?)null
            : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc));
        // 022 makes the singleton key nullable; it is kept only for rollback and never used for calls.
        return new CloroConnectionSettings(reader.IsDBNull(0) ? "" : reader.GetString(0), reader.GetString(1),
            reader.GetBoolean(2), updatedAt);
    }

    public async Task SaveAsync(CloroConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.ApiUrl);

        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        // An empty key keeps the stored rollback value instead of clearing it.
        const string sql = """
            INSERT INTO cloro_provider_settings (id, api_key, api_url, enabled, created_at, updated_at)
            VALUES (1, @api_key, @api_url, @enabled, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE
                api_key = COALESCE(VALUES(api_key), api_key),
                api_url = VALUES(api_url),
                enabled = VALUES(enabled),
                updated_at = UTC_TIMESTAMP(6);
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.Add("@api_key", MySqlDbType.Text).Value =
            string.IsNullOrWhiteSpace(settings.ApiKey) ? DBNull.Value : settings.ApiKey;
        command.Parameters.Add("@api_url", MySqlDbType.VarChar).Value = settings.ApiUrl;
        command.Parameters.Add("@enabled", MySqlDbType.Bit).Value = settings.Enabled;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
