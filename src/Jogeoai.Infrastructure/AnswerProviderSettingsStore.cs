using Jogeoai.Application;
using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

/// <summary>answer_provider_settings 的读写；每次读写前检查迁移 025 是否已部署。</summary>
public sealed class AnswerProviderSettingsStore(MySqlDatabase database) : IAnswerProviderSettingsStore
{
    private const string SelectSql = """
        SELECT provider, api_url, model, credential, credential_hint, enabled, timeout_seconds, options, updated_at
        FROM answer_provider_settings
        WHERE provider = @provider
        LIMIT 1;
        """;

    public async Task<AnswerProviderSettingsView?> GetAsync(string provider, CancellationToken cancellationToken = default)
    {
        var definition = AnswerProviderSettingsRules.RequireProvider(provider);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await AnswerProviderSchema.RequireAsync(connection, null, cancellationToken);
        var row = await ReadRowAsync(connection, definition.Provider, cancellationToken);
        return row is null ? null : ToView(row);
    }

    public async Task<AnswerProviderConfiguration?> GetConfigurationAsync(string provider, CancellationToken cancellationToken = default)
    {
        var definition = AnswerProviderSettingsRules.RequireProvider(provider);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await AnswerProviderSchema.RequireAsync(connection, null, cancellationToken);
        var row = await ReadRowAsync(connection, definition.Provider, cancellationToken);
        // Same gate as ResearchStore.GetProviderConfigurationAsync: enabled AND credential IS NOT NULL.
        if (row is null || !row.Enabled || string.IsNullOrEmpty(row.Credential)) return null;
        return new AnswerProviderConfiguration(row.Provider, row.ApiUrl, row.Model, row.Credential, row.TimeoutSeconds,
            AnswerProviderOptions.Parse(row.OptionsJson));
    }

    public async Task<AnswerProviderSettingsView> SaveAsync(string provider, SaveAnswerProviderSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalized = AnswerProviderSettingsRules.Normalize(provider, request);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await AnswerProviderSchema.RequireAsync(connection, null, cancellationToken);
        // An omitted credential keeps the stored value (and its hint) instead of clearing it.
        await using (var command = new MySqlCommand("""
            INSERT INTO answer_provider_settings
                (provider, api_url, model, credential, credential_hint, enabled, timeout_seconds, options, created_at, updated_at)
            VALUES (@provider, @url, @model, @credential, @hint, @enabled, @timeout, @options, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE api_url = VALUES(api_url), model = VALUES(model),
                credential = COALESCE(VALUES(credential), credential),
                credential_hint = COALESCE(VALUES(credential_hint), credential_hint),
                enabled = VALUES(enabled), timeout_seconds = VALUES(timeout_seconds), options = VALUES(options),
                updated_at = UTC_TIMESTAMP(6);
            """, connection))
        {
            command.Parameters.AddWithValue("@provider", normalized.Provider);
            command.Parameters.AddWithValue("@url", normalized.ApiUrl);
            command.Parameters.AddWithValue("@model", normalized.Model);
            command.Parameters.Add("@credential", MySqlDbType.Text).Value = (object?)normalized.Credential ?? DBNull.Value;
            command.Parameters.Add("@hint", MySqlDbType.VarChar).Value = (object?)normalized.CredentialHint ?? DBNull.Value;
            command.Parameters.AddWithValue("@enabled", normalized.Enabled);
            command.Parameters.AddWithValue("@timeout", normalized.TimeoutSeconds);
            command.Parameters.Add("@options", MySqlDbType.JSON).Value = normalized.OptionsJson;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        var row = await ReadRowAsync(connection, normalized.Provider, cancellationToken)
            ?? throw new CollectorStoreException("ANSWER_PROVIDER_SAVE_FAILED", "设置保存后无法读回。", 500);
        return ToView(row);
    }

    private static async Task<Row?> ReadRowAsync(MySqlConnection connection, string provider, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(SelectSql, connection);
        command.Parameters.AddWithValue("@provider", provider);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new Row(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetBoolean(5),
            Convert.ToInt32(reader.GetValue(6)),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(8), DateTimeKind.Utc)));
    }

    private static AnswerProviderSettingsView ToView(Row row) => new(row.Provider, row.ApiUrl, row.Model, row.Enabled,
        row.TimeoutSeconds, AnswerProviderOptions.Parse(row.OptionsJson), row.CredentialHint,
        row.Enabled && !string.IsNullOrEmpty(row.Credential), row.UpdatedAt);

    // Credential never leaves this class except through GetConfigurationAsync.
    private sealed record Row(string Provider, string ApiUrl, string Model, string? Credential, string? CredentialHint,
        bool Enabled, int TimeoutSeconds, string? OptionsJson, DateTimeOffset? UpdatedAt);
}
