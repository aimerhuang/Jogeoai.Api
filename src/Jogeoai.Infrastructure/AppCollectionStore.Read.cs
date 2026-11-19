using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

/// <summary>App 周期 Run 的检测读取（implement 4.4a）。视图与 API 检测相同，Provider 为 app_collector。</summary>
public sealed partial class AppCollectionStore
{
    private static readonly HashSet<string> CheckStatuses = ["pending", "running", "succeeded", "failed", "cancelled"];

    private sealed record CheckCursor(DateTimeOffset CreatedAt, Guid Id, string Scope);

    /// <inheritdoc />
    public async Task<CursorPage<CloroVisibilityCheckView>> GetChecksAsync(Guid tenantId, Guid runId, string? cursor, int limit,
        string? engine, string? status, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100) throw Bad("INVALID_LIMIT", "limit 必须在 1 到 100 之间。");
        engine = string.IsNullOrWhiteSpace(engine) ? null : engine.Trim().ToLowerInvariant();
        status = string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToLowerInvariant();
        if (engine is not null && !AppChannelDefinition.SupportsEngine(engine)) throw Bad("UNSUPPORTED_ENGINE", "引擎不受支持。");
        if (status is not null && !CheckStatuses.Contains(status)) throw Bad("INVALID_STATUS", "检查状态无效。");
        var scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { tenantId, runId, provider = AppChannelDefinition.Provider, engine, status })))).ToLowerInvariant();
        CheckCursor? position = null;
        if (cursor is not null)
        {
            try
            {
                if (cursor.Length > 2048) throw new FormatException();
                position = JsonSerializer.Deserialize<CheckCursor>(Convert.FromBase64String(cursor));
                if (position is null || position.Scope != scope || position.Id == Guid.Empty) throw new FormatException();
            }
            catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
            { throw Bad("INVALID_CURSOR", "游标与当前筛选条件不匹配。"); }
        }
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        // Only columns of the check itself. The node, the binding and the account label are not part of this view.
        var sql = new StringBuilder("""
            SELECT BIN_TO_UUID(c.id), BIN_TO_UUID(c.run_id), BIN_TO_UUID(c.prompt_id), c.prompt_snapshot, c.provider, c.engine,
                   c.capture_mode, c.evidence_class, c.locale, c.country, c.status, c.answer_present, c.answer, c.search_queries,
                   c.model_version, c.captured_at, c.duration_ms, c.error_code, c.error_message, c.created_at,
                   BIN_TO_UUID(c.run_target_id), c.sample_index, BIN_TO_UUID(c.prompt_version_id), c.prompt_tags_snapshot,
                   c.branded_status_snapshot, c.subject_config_version, c.snapshot_missing, c.query_availability, c.answer_mode
            FROM visibility_checks c
            JOIN visibility_runs r ON r.id = c.run_id AND r.tenant_id = c.tenant_id
            WHERE r.tenant_id = UUID_TO_BIN(@tenant) AND c.run_id = UUID_TO_BIN(@run) AND c.provider = @provider
            """);
        if (engine is not null) sql.Append(" AND c.engine = @engine");
        if (status is not null) sql.Append(" AND c.status = @status");
        if (position is not null) sql.Append(" AND (c.created_at > @created OR (c.created_at = @created AND c.id > UUID_TO_BIN(@id)))");
        sql.Append(" ORDER BY c.created_at, c.id LIMIT @limit;");
        var rows = new List<(CloroVisibilityCheckView View, DateTimeOffset CreatedAt)>();
        await using (var command = new MySqlCommand(sql.ToString(), connection))
        {
            command.Parameters.AddWithValue("@tenant", tenantId.ToString());
            command.Parameters.AddWithValue("@run", runId.ToString());
            command.Parameters.AddWithValue("@provider", AppChannelDefinition.Provider);
            command.Parameters.AddWithValue("@limit", limit + 1);
            if (engine is not null) command.Parameters.AddWithValue("@engine", engine);
            if (status is not null) command.Parameters.AddWithValue("@status", status);
            if (position is not null)
            {
                command.Parameters.AddWithValue("@created", position.CreatedAt.UtcDateTime);
                command.Parameters.AddWithValue("@id", position.Id.ToString());
            }
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) rows.Add((ReadCheck(reader), Utc(reader.GetDateTime(19))));
        }
        var items = new List<CloroVisibilityCheckView>();
        foreach (var (view, _) in rows.Take(limit))
            items.Add(view with { Citations = await ReadCitationsAsync(connection, view.CheckId, cancellationToken) });
        var next = rows.Count > limit
            ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new CheckCursor(rows[limit - 1].CreatedAt, rows[limit - 1].View.CheckId, scope)))
            : null;
        return new CursorPage<CloroVisibilityCheckView>(items, next);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, int>> CountNotCollectedAsync(Guid tenantId, IReadOnlyList<Guid> runIds,
        CancellationToken cancellationToken = default)
    {
        if (runIds.Count == 0) return new Dictionary<Guid, int>();
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        return await CountNotCollectedAsync(connection, tenantId, runIds, cancellationToken);
    }

    /// <summary>
    /// 未采集检测数的唯一读取处（Run 摘要与 Run 列表共用）：按 Run 统计 Provider 为 app_collector、状态为 cancelled 的检测。
    /// 走 ix_visibility_checks_run_status (run_id, status)。
    /// </summary>
    internal static async Task<IReadOnlyDictionary<Guid, int>> CountNotCollectedAsync(MySqlConnection connection, Guid tenantId,
        IReadOnlyList<Guid> runIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, int>();
        if (runIds.Count == 0) return result;
        var ids = string.Join(",", runIds.Select((_, i) => $"UUID_TO_BIN(@run{i})"));
        await using var command = new MySqlCommand($"""
            SELECT BIN_TO_UUID(c.run_id), COUNT(*) FROM visibility_checks c
            WHERE c.tenant_id = UUID_TO_BIN(@tenant) AND c.run_id IN ({ids}) AND c.status = 'cancelled' AND c.provider = @provider
            GROUP BY c.run_id;
            """, connection);
        command.Parameters.AddWithValue("@tenant", tenantId.ToString());
        command.Parameters.AddWithValue("@provider", AppChannelDefinition.Provider);
        for (var i = 0; i < runIds.Count; i++) command.Parameters.AddWithValue($"@run{i}", runIds[i].ToString());
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result[Guid.Parse(reader.GetString(0))] = Convert.ToInt32(reader.GetValue(1));
        return result;
    }

    // Provider task state, external task id, raw response references and parser version do not exist for an App check.
    private static CloroVisibilityCheckView ReadCheck(MySqlDataReader reader)
    {
        string? Text(int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
        var status = reader.GetString(10);
        return new CloroVisibilityCheckView(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)),
            reader.GetString(3), Text(4) ?? AppChannelDefinition.Provider, reader.GetString(5), Text(6) ?? AppChannelDefinition.CaptureMode,
            Text(7) ?? AppChannelDefinition.EvidenceClass, Text(8) ?? AppChannelDefinition.Locale, Text(9) ?? AppChannelDefinition.Country,
            status, !reader.IsDBNull(11) && reader.GetBoolean(11),
            status == "succeeded" && Text(12) is { } answer ? new VisibilityAnswer(answer) : null,
            Text(13) is { Length: > 0 } queries ? JsonSerializer.Deserialize<string[]>(queries) ?? [] : [], Text(14), [],
            reader.IsDBNull(15) ? null : Utc(reader.GetDateTime(15)), reader.IsDBNull(16) ? null : Convert.ToInt32(reader.GetValue(16)),
            null, null, null, Text(17), Text(18), null, null,
            reader.IsDBNull(20) ? null : Guid.Parse(reader.GetString(20)), Convert.ToInt32(reader.GetValue(21)),
            reader.IsDBNull(22) ? null : Guid.Parse(reader.GetString(22)),
            Text(23) is { Length: > 0 } tags ? JsonSerializer.Deserialize<string[]>(tags) : null, Text(24),
            reader.IsDBNull(25) ? null : Convert.ToInt32(reader.GetValue(25)), !reader.IsDBNull(26) && reader.GetBoolean(26),
            QueryAvailability: Text(27) ?? "unknown", AnswerMode: AnswerModeSchema.ParseStored(Text(28)));
    }

    private static async Task<IReadOnlyList<CloroCitationView>> ReadCitationsAsync(MySqlConnection connection, Guid checkId, CancellationToken ct)
    {
        await using var command = new MySqlCommand("""
            SELECT BIN_TO_UUID(id), normalized_url, source_domain, title, position FROM citations
            WHERE check_id = UUID_TO_BIN(@id) ORDER BY position, id;
            """, connection);
        command.Parameters.AddWithValue("@id", checkId.ToString());
        var citations = new List<CloroCitationView>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            citations.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), Convert.ToInt32(reader.GetValue(4))));
        return citations;
    }

    private static CollectorStoreException Bad(string code, string message) => new(code, message, 400);
}
