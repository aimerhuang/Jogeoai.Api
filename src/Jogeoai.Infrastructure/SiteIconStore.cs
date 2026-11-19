using Jogeoai.Application;
using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

/// <summary><c>site_icons</c> 的显式 SQL 读写（迁移 028、029）；全局表，不带 tenant_id。</summary>
public sealed class SiteIconStore(MySqlDatabase database) : ISiteIconStore
{
    private const string SelectSql = """
        SELECT domain, status, content_type, icon_bytes, source, fetched_at, expires_at
        FROM site_icons
        WHERE domain = @domain
        LIMIT 1;
        """;

    private const string UpsertSql = """
        INSERT INTO site_icons (domain, status, content_type, icon_bytes, source, fetched_at, expires_at)
        VALUES (@domain, @status, @contentType, @bytes, @source, @fetchedAt, @expiresAt)
        ON DUPLICATE KEY UPDATE status = VALUES(status), content_type = VALUES(content_type),
            icon_bytes = VALUES(icon_bytes), source = VALUES(source),
            fetched_at = VALUES(fetched_at), expires_at = VALUES(expires_at);
        """;

    /// <inheritdoc />
    public async Task<SiteIconRecord?> GetAsync(string domain, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(SelectSql, connection);
        command.Parameters.AddWithValue("@domain", domain);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new SiteIconRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : (byte[])reader.GetValue(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            Utc(reader.GetDateTime(5)),
            Utc(reader.GetDateTime(6)));
    }

    /// <inheritdoc />
    public async Task SaveAsync(SiteIconRecord record, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(UpsertSql, connection);
        command.Parameters.AddWithValue("@domain", record.Domain);
        command.Parameters.AddWithValue("@status", record.Status);
        command.Parameters.Add("@contentType", MySqlDbType.VarChar).Value = (object?)record.ContentType ?? DBNull.Value;
        command.Parameters.Add("@bytes", MySqlDbType.MediumBlob).Value = (object?)record.Bytes ?? DBNull.Value;
        command.Parameters.Add("@source", MySqlDbType.VarChar).Value = (object?)record.Source ?? DBNull.Value;
        command.Parameters.AddWithValue("@fetchedAt", record.FetchedAt.UtcDateTime);
        command.Parameters.AddWithValue("@expiresAt", record.ExpiresAt.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
