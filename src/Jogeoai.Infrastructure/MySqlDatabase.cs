using MySqlConnector;

namespace Jogeoai.Infrastructure;

/// <summary>Checks the configured application database using the application's credentials.</summary>
public sealed class MySqlDatabase(string connectionString)
{
    private const string CloroSettingsTableSql = """
        CREATE TABLE IF NOT EXISTS cloro_provider_settings (
            id TINYINT UNSIGNED NOT NULL PRIMARY KEY,
            api_key TEXT NOT NULL,
            api_url VARCHAR(2048) NOT NULL,
            enabled BOOLEAN NOT NULL DEFAULT TRUE,
            created_at DATETIME(6) NOT NULL,
            updated_at DATETIME(6) NOT NULL,
            CONSTRAINT ck_cloro_provider_settings_singleton CHECK (id = 1)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
        """;

    public async Task CheckConnectionAsync(CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("SELECT 1", connection);
        await command.ExecuteScalarAsync(cancellationToken);
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(CloroSettingsTableSql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal MySqlConnection CreateConnection() => new(connectionString);
}
