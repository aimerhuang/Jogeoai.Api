using System.Globalization;
using System.Text.Json;
using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

public sealed partial class ManagementStore
{
    private sealed record PromptGroupCursor(DateTimeOffset SnapshotAt, DateTimeOffset LastAt, Guid LastId, string Scope);

    private static string NormalizeGroupName(string? value) => PromptGroupNames.Name(value);

    private static string NormalizeGroupKey(string name) => PromptGroupNames.Key(name);

    private static async Task EnsurePromptGroup(MySqlConnection connection, MySqlTransaction? transaction,
        Guid tenantId, Guid siteId, Guid groupId, CancellationToken cancellationToken)
    {
        if (groupId == Guid.Empty) throw Error("INVALID_GROUP", "分组标识无效。", 400);
        var row = await Row(connection, transaction, """
            SELECT id FROM prompt_groups
            WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site) AND id = UUID_TO_BIN(@group);
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@group", groupId.ToString()));
        if (row is null) throw Error("RESOURCE_NOT_FOUND", "提问分组不存在。", 404);
    }

    private static Task<int> InsertPromptGroup(MySqlConnection connection, MySqlTransaction transaction, Guid tenantId, Guid siteId,
        Guid id, string name, string normalized, CancellationToken cancellationToken)
        => Exec(connection, transaction, """
            INSERT INTO prompt_groups (id, tenant_id, site_id, name, normalized_name, version, created_at, updated_at)
            VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), @name, @normalized, 1,
                UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
            """, cancellationToken, ("@id", id.ToString()), ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()),
            ("@name", name), ("@normalized", normalized));

    /// <summary>按 normalized_name（与唯一约束同一规则）查找品牌内已有分组。</summary>
    private static async Task<Guid?> FindPromptGroupByKey(MySqlConnection connection, MySqlTransaction? transaction,
        Guid tenantId, Guid siteId, string normalized, CancellationToken cancellationToken)
    {
        var row = await Row(connection, transaction, """
            SELECT BIN_TO_UUID(id) FROM prompt_groups
            WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site) AND normalized_name = @normalized;
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@normalized", normalized));
        return row is null ? null : Guid.Parse(row[0]);
    }

    private static async Task<PromptGroupView?> ReadPromptGroup(MySqlConnection connection, MySqlTransaction? transaction,
        Guid tenantId, Guid siteId, Guid groupId, CancellationToken cancellationToken)
    {
        var row = await Row(connection, transaction, """
            SELECT BIN_TO_UUID(g.id), g.name, g.version,
                   COALESCE(SUM(p.id IS NOT NULL AND p.archived_at IS NULL), 0),
                   COALESCE(SUM(p.id IS NOT NULL AND p.archived_at IS NULL AND p.enabled = TRUE), 0),
                   g.created_at, g.updated_at
            FROM prompt_groups g
            LEFT JOIN prompts p ON p.site_id = g.site_id AND p.group_id = g.id
            WHERE g.tenant_id = UUID_TO_BIN(@tenant) AND g.site_id = UUID_TO_BIN(@site) AND g.id = UUID_TO_BIN(@group)
            GROUP BY g.id, g.name, g.version, g.created_at, g.updated_at;
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@group", groupId.ToString()));
        if (row is null) return null;
        return new PromptGroupView(Guid.Parse(row[0]), row[1], uint.Parse(row[2], CultureInfo.InvariantCulture),
            int.Parse(row[3], CultureInfo.InvariantCulture), int.Parse(row[4], CultureInfo.InvariantCulture), Utc(row[5]), Utc(row[6]));
    }

    public async Task<int> CountPromptGroupsAsync(Guid tenantId, Guid siteId, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await EnsureSiteExists(connection, null, tenantId, siteId, cancellationToken);
        var row = await Row(connection, null,
            "SELECT COUNT(*) FROM prompt_groups WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);",
            cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()));
        return int.Parse(row![0], CultureInfo.InvariantCulture);
    }

    public async Task<PromptGroupView> CreatePromptGroupAsync(Guid tenantId, Guid siteId, PromptGroupWrite request,
        CancellationToken cancellationToken = default)
    {
        var name = NormalizeGroupName(request.Name);
        var normalized = NormalizeGroupKey(name);
        var id = Guid.NewGuid();
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var site = await LockSite(connection, transaction, tenantId, siteId, cancellationToken);
        if (site[2] != "active") throw Error("RESOURCE_NOT_FOUND", "工作区已归档。", 404);
        try
        {
            await InsertPromptGroup(connection, transaction, tenantId, siteId, id, name, normalized, cancellationToken);
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        { throw Error("PROMPT_GROUP_CONFLICT", "同名提问分组已存在。", 409); }
        var result = (await ReadPromptGroup(connection, transaction, tenantId, siteId, id, cancellationToken))!;
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<PromptGroupView> UpdatePromptGroupAsync(Guid tenantId, Guid siteId, Guid groupId,
        PromptGroupPatch request, CancellationToken cancellationToken = default)
    {
        if (request.ExpectedVersion is null) throw Error("CONFIG_VERSION_REQUIRED", "请提供 expectedVersion。", 400);
        var name = NormalizeGroupName(request.Name);
        var normalized = NormalizeGroupKey(name);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var site = await LockSite(connection, transaction, tenantId, siteId, cancellationToken);
        if (site[2] != "active") throw Error("RESOURCE_NOT_FOUND", "工作区已归档。", 404);
        var row = await Row(connection, transaction, """
            SELECT name, version FROM prompt_groups
            WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site) AND id = UUID_TO_BIN(@group) FOR UPDATE;
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@group", groupId.ToString()));
        if (row is null) throw Error("RESOURCE_NOT_FOUND", "提问分组不存在。", 404);
        if (request.ExpectedVersion.Value != uint.Parse(row[1], CultureInfo.InvariantCulture))
            throw Error("CONFIG_VERSION_CONFLICT", "分组配置版本已变化。", 409);
        try
        {
            await Exec(connection, transaction, """
                UPDATE prompt_groups SET name = @name, normalized_name = @normalized, version = version + 1,
                    updated_at = UTC_TIMESTAMP(6)
                WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site) AND id = UUID_TO_BIN(@group);
                """, cancellationToken, ("@name", name), ("@normalized", normalized),
                ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@group", groupId.ToString()));
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        { throw Error("PROMPT_GROUP_CONFLICT", "同名提问分组已存在。", 409); }
        var result = (await ReadPromptGroup(connection, transaction, tenantId, siteId, groupId, cancellationToken))!;
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<PromptGroupPage> ListPromptGroupsAsync(Guid tenantId, Guid siteId, string? search, Guid? tagId,
        string? cursor, int limit, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100) throw Error("INVALID_LIMIT", "limit 必须在 1–100。", 400);
        search = search?.Trim();
        if (search?.Length > 200) throw Error("INVALID_SEARCH", "搜索词过长。", 400);
        var scope = Hash(JsonSerializer.Serialize(new { tenantId, siteId, search, tagId }));
        PromptGroupCursor? parsed = null;
        if (cursor is not null)
        {
            try
            {
                if (cursor.Length > 2048) throw new FormatException();
                parsed = JsonSerializer.Deserialize<PromptGroupCursor>(Convert.FromBase64String(cursor));
                if (parsed is null || parsed.Scope != scope || parsed.LastId == Guid.Empty || parsed.LastAt > parsed.SnapshotAt
                    || parsed.SnapshotAt > DateTimeOffset.UtcNow.AddMinutes(1)) throw new FormatException();
            }
            catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
            { throw Error("INVALID_CURSOR", "游标与当前筛选条件不匹配。", 400); }
        }
        var snapshotAt = parsed?.SnapshotAt ?? DateTimeOffset.UtcNow;
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await EnsureSiteExists(connection, null, tenantId, siteId, cancellationToken);
        var filter = "p.archived_at IS NULL";
        var parameters = new List<(string Name, object? Value)>
        {
            ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@snapshot", snapshotAt.UtcDateTime),
            ("@limit", limit + 1)
        };
        if (search is not null)
        {
            filter += " AND v.content LIKE @search ESCAPE '='";
            parameters.Add(("@search", "%" + EscapePromptSearch(search) + "%"));
        }
        if (tagId is not null)
        {
            filter += " AND EXISTS (SELECT 1 FROM prompt_version_tags x WHERE x.prompt_version_id = v.id AND x.tag_id = UUID_TO_BIN(@tag))";
            parameters.Add(("@tag", tagId.ToString()));
        }
        var sql = $"""
            SELECT BIN_TO_UUID(g.id), g.name, g.version,
                   COALESCE(SUM(({filter}) AND p.id IS NOT NULL), 0),
                   COALESCE(SUM(({filter}) AND p.enabled = TRUE AND p.id IS NOT NULL), 0),
                   g.created_at, g.updated_at
            FROM prompt_groups g
            LEFT JOIN prompts p ON p.site_id = g.site_id AND p.group_id = g.id
                AND p.created_at <= @snapshot
            LEFT JOIN prompt_versions v ON v.id = p.current_version_id
            WHERE g.tenant_id = UUID_TO_BIN(@tenant) AND g.site_id = UUID_TO_BIN(@site)
              AND g.created_at <= @snapshot
            """;
        if (parsed is not null)
        {
            sql += " AND (g.created_at > @last OR (g.created_at = @last AND g.id > UUID_TO_BIN(@lastId)))";
            parameters.Add(("@last", parsed.LastAt.UtcDateTime));
            parameters.Add(("@lastId", parsed.LastId.ToString()));
        }
        sql += " GROUP BY g.id, g.name, g.version, g.created_at, g.updated_at ORDER BY g.created_at, g.id LIMIT @limit;";
        var rows = await Rows(connection, null, sql, cancellationToken, parameters.ToArray());
        var items = rows.Take(limit).Select(row => new PromptGroupView(Guid.Parse(row[0]), row[1], uint.Parse(row[2]),
            int.Parse(row[3]), int.Parse(row[4]), Utc(row[5]), Utc(row[6]))).ToArray();
        string? nextCursor = null;
        if (rows.Count > limit)
        {
            var last = rows[limit - 1];
            nextCursor = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new PromptGroupCursor(snapshotAt,
                Utc(last[5]), Guid.Parse(last[0]), scope)));
        }
        var ungrouped = await ReadUngroupedPromptCounts(connection, tenantId, siteId, search, tagId, snapshotAt, cancellationToken);
        return new PromptGroupPage(items, nextCursor, ungrouped);
    }

    private static async Task<PromptGroupCounts> ReadUngroupedPromptCounts(MySqlConnection connection, Guid tenantId, Guid siteId,
        string? search, Guid? tagId, DateTimeOffset snapshotAt, CancellationToken cancellationToken)
    {
        var where = "s.tenant_id = UUID_TO_BIN(@tenant) AND p.site_id = UUID_TO_BIN(@site) AND p.archived_at IS NULL AND p.group_id IS NULL AND p.created_at <= @snapshot";
        var parameters = new List<(string Name, object? Value)> { ("@site", siteId.ToString()), ("@tenant", tenantId.ToString()), ("@snapshot", snapshotAt.UtcDateTime) };
        if (search is not null)
        {
            where += " AND v.content LIKE @search ESCAPE '='";
            parameters.Add(("@search", "%" + EscapePromptSearch(search) + "%"));
        }
        if (tagId is not null)
        {
            where += " AND EXISTS (SELECT 1 FROM prompt_version_tags x WHERE x.prompt_version_id = v.id AND x.tag_id = UUID_TO_BIN(@tag))";
            parameters.Add(("@tag", tagId.ToString()));
        }
        var row = await Row(connection, null, $"""
            SELECT COUNT(*), COALESCE(SUM(p.enabled = TRUE), 0)
            FROM prompts p JOIN prompt_versions v ON v.id = p.current_version_id
            JOIN sites s ON s.id = p.site_id
            WHERE {where};
            """, cancellationToken, parameters.ToArray());
        return new PromptGroupCounts(int.Parse(row![0], CultureInfo.InvariantCulture), int.Parse(row[1], CultureInfo.InvariantCulture));
    }
}
