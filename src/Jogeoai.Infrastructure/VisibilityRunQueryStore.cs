using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jogeoai.Application;
using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

/// <summary>共用运行表的租户边界查询。</summary>
public sealed class VisibilityRunQueryStore(MySqlDatabase database) : IVisibilityRunQueryStore
{
    private static readonly HashSet<string> Statuses = ["queued", "running", "succeeded", "partial_failed", "failed", "cancelled"];
    // App 周期 Run 的 Provider 也可以用来筛选；不筛选时列表本来就包含它们。
    private static readonly HashSet<string> Providers = ["cloro", "doubao_api", "deepseek_api", "packy_qwen", "packy_glm", "packy_kimi", "packy_yuanbao", "domestic_collector", AppChannelDefinition.Provider];
    private sealed record RunCursor(DateTimeOffset SnapshotAt, DateTimeOffset LastAt, Guid LastId, string Scope);

    /// <inheritdoc />
    public async Task<VisibilityRunReplay?> FindReplayAsync(Guid tenantId, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            SELECT BIN_TO_UUID(id), BIN_TO_UUID(site_id), status, total_checks, created_at,
                   succeeded_checks, failed_checks, http_request_hash, BIN_TO_UUID(collection_preview_id), requested_by
            FROM visibility_runs WHERE tenant_id = UUID_TO_BIN(@tenant) AND idempotency_key = @key;
            """, connection);
        command.Parameters.AddWithValue("@tenant", tenantId.ToString());
        command.Parameters.AddWithValue("@key", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var run = new VisibilityRunCreated(Guid.Parse(reader.GetString(0)), reader.GetString(2), reader.GetInt32(3),
            Utc(reader.GetDateTime(4)), reader.GetInt32(5), reader.GetInt32(6));
        return new VisibilityRunReplay(run, Guid.Parse(reader.GetString(1)),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : Guid.Parse(reader.GetString(8)),
            reader.IsDBNull(9) ? null : reader.GetString(9));
    }

    /// <inheritdoc />
    public async Task<CursorPage<VisibilityRunSummary>> ListSiteRunsAsync(Guid tenantId, Guid siteId, string? status,
        string? provider, DateTimeOffset? from, DateTimeOffset? to, string? cursor, int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100) throw Error("INVALID_LIMIT", "limit 必须在 1–100。", 400);
        status = string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToLowerInvariant();
        provider = string.IsNullOrWhiteSpace(provider) ? null : provider.Trim().ToLowerInvariant();
        if (status is not null && !Statuses.Contains(status)) throw Error("INVALID_STATUS", "运行状态无效。", 400);
        if (provider is not null && !Providers.Contains(provider)) throw Error("INVALID_PROVIDER", "Provider 无效。", 400);
        if (from > to) throw Error("INVALID_TIME_RANGE", "from 不能晚于 to。", 400);
        var fromUtc = from?.ToUniversalTime();
        var toUtc = to?.ToUniversalTime();
        var scope = Hash(JsonSerializer.Serialize(new { tenantId, siteId, status, provider, fromUtc, toUtc }));
        RunCursor? position = null;
        if (cursor is not null)
        {
            try
            {
                if (cursor.Length > 2048) throw new FormatException();
                position = JsonSerializer.Deserialize<RunCursor>(Convert.FromBase64String(cursor));
                if (position is null || position.Scope != scope || position.LastId == Guid.Empty
                    || position.LastAt > position.SnapshotAt || position.SnapshotAt > DateTimeOffset.UtcNow.AddMinutes(1))
                    throw new FormatException();
            }
            catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
            { throw Error("INVALID_CURSOR", "游标与当前运行筛选条件不匹配。", 400); }
        }
        var snapshotAt = position?.SnapshotAt ?? DateTimeOffset.UtcNow;
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using (var site = new MySqlCommand("SELECT 1 FROM sites WHERE tenant_id = UUID_TO_BIN(@tenant) AND id = UUID_TO_BIN(@site);", connection))
        {
            site.Parameters.AddWithValue("@tenant", tenantId.ToString());
            site.Parameters.AddWithValue("@site", siteId.ToString());
            if (await site.ExecuteScalarAsync(cancellationToken) is null)
                throw Error("RESOURCE_NOT_FOUND", "工作区不存在。", 404);
        }
        var sql = new StringBuilder("""
            SELECT BIN_TO_UUID(r.id), r.status, r.total_checks, r.succeeded_checks, r.failed_checks,
                   r.created_at, r.started_at, r.completed_at, r.error_code, r.error_message,
                   (SELECT e.provider FROM run_engines e WHERE e.run_id = r.id ORDER BY e.sort_order LIMIT 1)
            FROM visibility_runs r
            WHERE r.tenant_id = UUID_TO_BIN(@tenant) AND r.site_id = UUID_TO_BIN(@site)
              AND r.created_at <= @snapshot
            """);
        if (status is not null) sql.Append(" AND r.status = @status");
        if (provider is not null) sql.Append(" AND EXISTS (SELECT 1 FROM run_engines e WHERE e.run_id = r.id AND e.provider = @provider)");
        if (fromUtc is not null) sql.Append(" AND r.created_at >= @from");
        if (toUtc is not null) sql.Append(" AND r.created_at <= @to");
        if (position is not null) sql.Append(" AND (r.created_at > @last OR (r.created_at = @last AND r.id > UUID_TO_BIN(@lastId)))");
        sql.Append(" ORDER BY r.created_at, r.id LIMIT @limit;");
        await using var command = new MySqlCommand(sql.ToString(), connection);
        command.Parameters.AddWithValue("@tenant", tenantId.ToString());
        command.Parameters.AddWithValue("@site", siteId.ToString());
        command.Parameters.AddWithValue("@snapshot", snapshotAt.UtcDateTime);
        command.Parameters.AddWithValue("@limit", limit + 1);
        if (status is not null) command.Parameters.AddWithValue("@status", status);
        if (provider is not null) command.Parameters.AddWithValue("@provider", provider);
        if (fromUtc is not null) command.Parameters.AddWithValue("@from", fromUtc.Value.UtcDateTime);
        if (toUtc is not null) command.Parameters.AddWithValue("@to", toUtc.Value.UtcDateTime);
        if (position is not null)
        {
            command.Parameters.AddWithValue("@last", position.LastAt.UtcDateTime);
            command.Parameters.AddWithValue("@lastId", position.LastId.ToString());
        }
        var rows = new List<(VisibilityRunSummary Summary, DateTimeOffset CreatedAt)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var createdAt = Utc(reader.GetDateTime(5));
                var summary = new VisibilityRunSummary(Guid.Parse(reader.GetString(0)), siteId, reader.GetString(1),
                    reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4), createdAt,
                    reader.IsDBNull(6) ? null : Utc(reader.GetDateTime(6)),
                    reader.IsDBNull(7) ? null : Utc(reader.GetDateTime(7)),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10));
                rows.Add((summary, createdAt));
            }
        }
        // 只有 App 周期 Run 会有未采集的检测：只为它们多读一次，其他 Run 的查询与结果不变。
        var appRuns = rows.Where(row => MonitoringProviders.IsApp(row.Summary.Provider)).Select(row => row.Summary.RunId).ToArray();
        if (appRuns.Length > 0)
        {
            var notCollected = await AppCollectionStore.CountNotCollectedAsync(connection, tenantId, appRuns, cancellationToken);
            for (var i = 0; i < rows.Count; i++)
                if (notCollected.TryGetValue(rows[i].Summary.RunId, out var cancelled))
                    rows[i] = (rows[i].Summary with { CancelledChecks = cancelled }, rows[i].CreatedAt);
        }
        var next = rows.Count > limit
            ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new RunCursor(snapshotAt,
                rows[limit - 1].CreatedAt, rows[limit - 1].Summary.RunId, scope)))
            : null;
        return new CursorPage<VisibilityRunSummary>(rows.Take(limit).Select(row => row.Summary).ToArray(), next);
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static CollectorStoreException Error(string code, string message, int status) => new(code, message, status);
}
