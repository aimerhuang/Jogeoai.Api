using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

public sealed partial class ManagementStore
{
    // An explicit escape character keeps literal searches independent of MySQL's backslash SQL mode.
    private static string EscapePromptSearch(string value) => value.Replace("=", "==", StringComparison.Ordinal)
        .Replace("%", "=%", StringComparison.Ordinal).Replace("_", "=_", StringComparison.Ordinal);

    public async Task<TagView> CreateTagAsync(Guid tenantId, Guid siteId, TagWrite request, CancellationToken cancellationToken = default)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length is < 1 or > 100) throw Error("INVALID_TAG", "标签名称长度必须为 1–100。", 400);
        var status = ValidateStatus(request.Status);
        var id = Guid.NewGuid();
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var site = await LockSite(connection, transaction, tenantId, siteId, cancellationToken);
        if (site[2] != "active") throw Error("RESOURCE_NOT_FOUND", "工作区已归档。", 404);
        try
        {
            await Exec(connection, transaction, """
                INSERT INTO tags (id, tenant_id, site_id, name, normalized_name, status, created_at, updated_at)
                VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), @name, @normalized, @status,
                    UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                """, cancellationToken, ("@id", id.ToString()), ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()),
                ("@name", name), ("@normalized", NormalizeText(name)), ("@status", status));
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        { throw Error("TAG_CONFLICT", "同名标签已存在。", 409); }
        await transaction.CommitAsync(cancellationToken);
        return new TagView(id, name, status, 1);
    }

    public async Task<CursorPage<TagView>> ListTagsAsync(Guid tenantId, Guid siteId, string? cursor, int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await EnsureSiteExists(connection, null, tenantId, siteId, cancellationToken);
        var (ids, next) = await ListIdsAsync(connection, "tags", tenantId, siteId, cursor, limit, cancellationToken);
        var result = new List<TagView>();
        foreach (var id in ids)
        {
            var row = await Row(connection, null,
                "SELECT name, status, version FROM tags WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site) AND id = UUID_TO_BIN(@id);",
                cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@id", id.ToString()));
            if (row is not null) result.Add(new TagView(id, row[0], row[1], uint.Parse(row[2])));
        }
        return new CursorPage<TagView>(result, next);
    }

    public async Task<TagView> UpdateTagAsync(Guid tenantId, Guid siteId, Guid tagId, TagPatch request, CancellationToken cancellationToken = default)
    {
        if (request.ExpectedVersion is null) throw Error("CONFIG_VERSION_REQUIRED", "请提供 expectedVersion。", 400);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var site = await LockSite(connection, transaction, tenantId, siteId, cancellationToken);
        if (site[2] != "active") throw Error("RESOURCE_NOT_FOUND", "工作区已归档。", 404);
        var row = await Row(connection, transaction, "SELECT name, status, version FROM tags WHERE site_id = UUID_TO_BIN(@site) AND id = UUID_TO_BIN(@id) FOR UPDATE;",
            cancellationToken, ("@site", siteId.ToString()), ("@id", tagId.ToString()));
        if (row is null) throw Error("RESOURCE_NOT_FOUND", "标签不存在。", 404);
        if (request.ExpectedVersion.Value != uint.Parse(row[2])) throw Error("CONFIG_VERSION_CONFLICT", "标签配置版本已变化。", 409);
        var name = request.Name?.Trim() ?? row[0];
        if (name.Length is < 1 or > 100) throw Error("INVALID_TAG", "标签名称长度必须为 1–100。", 400);
        var status = ValidateStatus(request.Status ?? row[1]);
        try
        {
            await Exec(connection, transaction, """
                UPDATE tags SET name = @name, normalized_name = @normalized, status = @status, version = version + 1,
                    updated_at = UTC_TIMESTAMP(6) WHERE id = UUID_TO_BIN(@id);
                """, cancellationToken, ("@name", name), ("@normalized", NormalizeText(name)), ("@status", status), ("@id", tagId.ToString()));
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        { throw Error("TAG_CONFLICT", "同名标签已存在。", 409); }
        await transaction.CommitAsync(cancellationToken);
        return new TagView(tagId, name, status, request.ExpectedVersion.Value + 1);
    }

    private static async Task<IReadOnlyList<TagView>> ResolveTags(MySqlConnection connection, MySqlTransaction? transaction,
        Guid siteId, IReadOnlyList<Guid>? ids, CancellationToken cancellationToken)
    {
        if (ids is null || ids.Count == 0) return [];
        if (ids.Count > 50 || ids.Any(id => id == Guid.Empty) || ids.Distinct().Count() != ids.Count)
            throw Error("INVALID_TAGS", "标签最多 50 个且不能重复。", 400);
        var result = new List<TagView>();
        foreach (var id in ids)
        {
            var row = await Row(connection, transaction, "SELECT name, status FROM tags WHERE site_id = UUID_TO_BIN(@site) AND id = UUID_TO_BIN(@id);",
                cancellationToken, ("@site", siteId.ToString()), ("@id", id.ToString()));
            if (row is null || row[1] != "active") throw Error("RESOURCE_NOT_FOUND", "标签不存在或已归档。", 404);
            result.Add(new TagView(id, row[0], row[1]));
        }
        return result;
    }

    private static string NormalizePromptContent(string content) =>
        string.Join(' ', content.Normalize(System.Text.NormalizationForm.FormKC).Trim()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
    private static string ContentKey(string content, string locale) => NormalizePromptContent(content) + "|" + locale.ToLowerInvariant();

    private static async Task<Guid?> FindDuplicate(MySqlConnection connection, MySqlTransaction transaction, Guid siteId,
        string content, string locale, Guid? exclude, CancellationToken cancellationToken)
    {
        var existing = await Rows(connection, transaction, """
            SELECT BIN_TO_UUID(id), content, locale FROM prompts WHERE site_id = UUID_TO_BIN(@site) AND archived_at IS NULL;
            """, cancellationToken, ("@site", siteId.ToString()));
        var key = ContentKey(content, locale);
        var match = existing.FirstOrDefault(row => (!exclude.HasValue || Guid.Parse(row[0]) != exclude.Value)
            && ContentKey(row[1], row[2]) == key);
        return match is null ? null : Guid.Parse(match[0]);
    }

    private static async Task<(string Status, string Source)> ClassifyBrand(MySqlConnection connection, MySqlTransaction transaction,
        Guid siteId, string content, string? status, string? source, CancellationToken cancellationToken)
    {
        if (status is not null)
        {
            if (status is not ("branded" or "unbranded")) throw Error("INVALID_BRANDED_STATUS", "brandedStatus 无效。", 400);
            if (source is not (null or "manual")) throw Error("INVALID_BRANDED_SOURCE", "人工分类来源必须为 manual。", 400);
            return (status, "manual");
        }
        if (source is not null) throw Error("INVALID_BRANDED_SOURCE", "未指定分类时不能单独指定来源。", 400);
        var terms = await Rows(connection, transaction, """
            SELECT normalized_key FROM subject_terms WHERE site_id = UUID_TO_BIN(@site)
              AND subject_type = 'brand' AND namespace_key = 'text';
            """, cancellationToken, ("@site", siteId.ToString()));
        var normalized = NormalizePromptContent(content);
        var matched = terms.Any(term =>
        {
            var value = term[0];
            if (value.Any(c => c > 127)) return normalized.Contains(value, StringComparison.Ordinal);
            return Regex.IsMatch(normalized, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(value)}(?![\p{{L}}\p{{N}}])", RegexOptions.CultureInvariant);
        });
        return (matched ? "branded" : "unbranded", "system");
    }

    // creationSource / targetScope need migration 042 (callers run PromptTargetScopeSchema.RequireAsync first);
    // targetScope must already be validated (PromptMonitoringScale.ValidateTargetKeysAsync); null keeps 'all'.
    private static async Task<Guid> InsertPrompt(MySqlConnection connection, MySqlTransaction transaction,
        Guid tenantId, Guid siteId, PromptWrite request, CancellationToken cancellationToken,
        string? creationSource = null, IReadOnlyList<string>? targetScope = null)
    {
        var content = request.Content?.Trim() ?? "";
        if (content.Length is < 1 or > 10000) throw Error("INVALID_PROMPT", "Prompt 内容长度必须为 1–10000。", 400);
        var locale = ValidateLocale(request.Locale);
        if (request.GroupId is not null)
            await EnsurePromptGroup(connection, transaction, tenantId, siteId, request.GroupId.Value, cancellationToken);
        if (await FindDuplicate(connection, transaction, siteId, content, locale, null, cancellationToken) is not null)
            throw Error("PROMPT_DUPLICATE", "相同内容与语言的 Prompt 已存在。", 409);
        var tags = await ResolveTags(connection, transaction, siteId, request.TagIds, cancellationToken);
        var branded = await ClassifyBrand(connection, transaction, siteId, content, request.BrandedStatus, request.BrandedSource, cancellationToken);
        var id = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        await Exec(connection, transaction, """
            INSERT INTO prompts (id, site_id, group_id, content, locale, enabled, current_version_id, created_at, updated_at)
            VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@site), UUID_TO_BIN(@group), @content, @locale, @enabled, NULL, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
            """, cancellationToken, ("@id", id.ToString()), ("@site", siteId.ToString()),
            ("@group", request.GroupId?.ToString()), ("@content", content), ("@locale", locale), ("@enabled", request.Enabled));
        await Exec(connection, transaction, """
            INSERT INTO prompt_versions (id, site_id, prompt_id, version, content, locale, branded_status, branded_source, created_at)
            VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@site), UUID_TO_BIN(@prompt), 1, @content, @locale, @branded, @source, UTC_TIMESTAMP(6));
            """, cancellationToken, ("@id", versionId.ToString()), ("@site", siteId.ToString()),
            ("@prompt", id.ToString()), ("@content", content), ("@locale", locale),
            ("@branded", branded.Status), ("@source", branded.Source));
        foreach (var tag in tags)
            await Exec(connection, transaction, """
                INSERT INTO prompt_version_tags (prompt_version_id, tag_id, tag_name_snapshot)
                VALUES (UUID_TO_BIN(@version), UUID_TO_BIN(@tag), @name);
                """, cancellationToken, ("@version", versionId.ToString()), ("@tag", tag.TagId.ToString()), ("@name", tag.Name));
        await Exec(connection, transaction, "UPDATE prompts SET current_version_id = UUID_TO_BIN(@version) WHERE id = UUID_TO_BIN(@prompt);",
            cancellationToken, ("@version", versionId.ToString()), ("@prompt", id.ToString()));
        if (creationSource is not null)
            await Exec(connection, transaction, "UPDATE prompts SET creation_source = @source WHERE id = UUID_TO_BIN(@prompt);",
                cancellationToken, ("@source", creationSource), ("@prompt", id.ToString()));
        if (targetScope is not null)
            await WritePromptTargetScope(connection, transaction, tenantId, siteId, id, targetScope, cancellationToken);
        return id;
    }

    /// <summary>替换提问平台范围：null 恢复为 all 并清空范围行；非空写为 custom。不改 config_version，由调用方决定。</summary>
    private static async Task WritePromptTargetScope(MySqlConnection connection, MySqlTransaction transaction,
        Guid tenantId, Guid siteId, Guid promptId, IReadOnlyList<string>? keys, CancellationToken cancellationToken)
    {
        await Exec(connection, transaction,
            "DELETE FROM prompt_target_scopes WHERE site_id = UUID_TO_BIN(@site) AND prompt_id = UUID_TO_BIN(@prompt);",
            cancellationToken, ("@site", siteId.ToString()), ("@prompt", promptId.ToString()));
        if (keys is { Count: > 0 })
        {
            var parameters = new List<(string, object?)> { ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@prompt", promptId.ToString()) };
            var values = new List<string>();
            for (var i = 0; i < keys.Count; i++)
            {
                values.Add($"(UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), UUID_TO_BIN(@prompt), @key{i}, UTC_TIMESTAMP(6))");
                parameters.Add(($"@key{i}", keys[i]));
            }
            await Exec(connection, transaction,
                $"INSERT INTO prompt_target_scopes (tenant_id, site_id, prompt_id, target_key, created_at) VALUES {string.Join(", ", values)};",
                cancellationToken, parameters.ToArray());
        }
        await Exec(connection, transaction, "UPDATE prompts SET target_scope = @scope WHERE id = UUID_TO_BIN(@prompt);",
            cancellationToken, ("@scope", keys is { Count: > 0 } ? "custom" : "all"), ("@prompt", promptId.ToString()));
    }

    public async Task<PromptView> CreatePromptAsync(Guid tenantId, Guid siteId, PromptWrite request, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // 提问上限按租户计数：先取租户级锁再锁品牌（锁顺序 tenants -> sites）。
        await LockTenant(connection, transaction, tenantId, cancellationToken);
        var site = await LockSite(connection, transaction, tenantId, siteId, cancellationToken);
        if (site[2] != "active") throw Error("RESOURCE_NOT_FOUND", "工作区已归档。", 404);
        IReadOnlyList<string>? scope = null;
        if (request.TargetKeys is not null)
        {
            // Only scope writes depend on 042; requests without targetKeys keep working on an unmigrated database.
            await PromptTargetScopeSchema.RequireAsync(connection, transaction, cancellationToken);
            scope = await PromptMonitoringScale.ValidateTargetKeysAsync(connection, transaction, tenantId, siteId, request.TargetKeys, cancellationToken,
                AppOpen(tenantId));
        }
        if (request.Enabled)
            await SubscriptionLimitGuard.EnsurePromptsAsync(connection, transaction, tenantId, 1, cancellationToken);
        if (!request.Enabled)
        {
            var enabled = await Row(connection, transaction, """
                SELECT COUNT(*) FROM prompts
                WHERE site_id = UUID_TO_BIN(@site) AND archived_at IS NULL AND enabled = TRUE;
                """, cancellationToken, ("@site", siteId.ToString()));
            if (enabled is not null && int.Parse(enabled[0], CultureInfo.InvariantCulture) == 0)
                throw Error("LAST_ENABLED_PROMPT", "至少保留 1 个启用问题；请先创建启用问题。", 409);
        }
        var id = await InsertPrompt(connection, transaction, tenantId, siteId, request, cancellationToken, targetScope: scope);
        await transaction.CommitAsync(cancellationToken);
        return (await GetPromptAsync(tenantId, siteId, id, cancellationToken))!;
    }

    private static async Task<PromptView> ReadPrompt(MySqlConnection connection, MySqlTransaction? transaction, string[] row,
        CancellationToken cancellationToken)
    {
        var versionId = Guid.Parse(row[8]);
        var tags = await Rows(connection, transaction, """
            SELECT BIN_TO_UUID(t.id), pvt.tag_name_snapshot, t.status, t.version FROM prompt_version_tags pvt
            JOIN tags t ON t.id = pvt.tag_id WHERE pvt.prompt_version_id = UUID_TO_BIN(@version)
            ORDER BY pvt.tag_name_snapshot, t.id;
            """, cancellationToken, ("@version", versionId.ToString()));
        var groupId = string.IsNullOrEmpty(row[13]) ? (Guid?)null : Guid.Parse(row[13]);
        var targetScope = row[14] == "custom" ? "custom" : "all";
        IReadOnlyList<string>? targetKeys = null;
        if (targetScope == "custom")
            targetKeys = (await Rows(connection, transaction, """
                SELECT target_key FROM prompt_target_scopes WHERE site_id = UUID_TO_BIN(@site) AND prompt_id = UUID_TO_BIN(@prompt)
                ORDER BY target_key;
                """, cancellationToken, ("@site", row[7]), ("@prompt", row[0]))).Select(r => r[0]).ToArray();
        return new PromptView(Guid.Parse(row[0]), row[1], int.Parse(row[2]), row[3], Bool(row[4]),
            tags.Select(t => new TagView(Guid.Parse(t[0]), t[1], t[2], uint.Parse(t[3]))).ToArray(), row[5], row[6],
            Utc(row[9]), Utc(row[10]), NullableUtc(row[11]), uint.Parse(row[12]), groupId,
            targetScope, targetKeys, string.IsNullOrEmpty(row[15]) ? null : row[15]);
    }

    // Columns 14–15 come from 042; before it every prompt reads as 'all' without a creation source.
    private static string PromptProjection(bool targetScopeMigrated) => $"""
        SELECT BIN_TO_UUID(p.id), v.content, v.version, v.locale, p.enabled, v.branded_status, v.branded_source,
               BIN_TO_UUID(p.site_id), BIN_TO_UUID(v.id), p.created_at, p.updated_at, p.archived_at, p.config_version,
               BIN_TO_UUID(p.group_id), {(targetScopeMigrated ? "p.target_scope, p.creation_source" : "'all', NULL")}
        FROM prompts p JOIN prompt_versions v ON v.id = p.current_version_id
        JOIN sites s ON s.id = p.site_id
        WHERE s.tenant_id = UUID_TO_BIN(@tenant) AND p.site_id = UUID_TO_BIN(@site)
        """;

    private static async Task<string> PromptProjectionAsync(MySqlConnection connection, MySqlTransaction? transaction,
        CancellationToken cancellationToken)
        => PromptProjection(await PromptTargetScopeSchema.IsMigratedAsync(connection, transaction, cancellationToken));

    public async Task<PromptView?> GetPromptAsync(Guid tenantId, Guid siteId, Guid promptId, CancellationToken cancellationToken = default,
        bool includeAppEligibility = false)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var row = await Row(connection, null, await PromptProjectionAsync(connection, null, cancellationToken) + " AND p.id = UUID_TO_BIN(@id);", cancellationToken,
            ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@id", promptId.ToString()));
        if (row is null) return null;
        var appExcluded = includeAppEligibility ? await AppExcludedPromptsAsync(connection, tenantId, siteId, cancellationToken) : null;
        return WithAppEligibility(await ReadPrompt(connection, null, row, cancellationToken), appExcluded);
    }

    // Prompts with an active operator exclusion from the App channel; null when the prompt views of this tenant carry no
    // appEligibility at all (the App channel is not open for it, or 049 is not applied), so their JSON stays as it was.
    // Only the two prompt-management reads ask for it (includeAppEligibility); every internal caller gets the plain view.
    private async Task<HashSet<Guid>?> AppExcludedPromptsAsync(MySqlConnection connection, Guid tenantId, Guid siteId,
        CancellationToken cancellationToken)
        => AppOpen(tenantId) && await AppChannelSchema.IsMigratedAsync(connection, null, cancellationToken)
            ? await AppCollectionStore.ActiveExclusionsAsync(connection, null, tenantId, siteId, cancellationToken)
            : null;

    // The same function the weekly cycle run uses to decide whether the prompt gets an App task.
    private static PromptView WithAppEligibility(PromptView prompt, HashSet<Guid>? appExcluded)
        => appExcluded is null ? prompt
            : prompt with { AppEligibility = AppPromptRules.Eligibility(prompt.Content, appExcluded.Contains(prompt.PromptId)) };

    public async Task<PromptSummaryView> SummarizePromptsAsync(Guid tenantId, Guid siteId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await EnsureSiteExists(connection, null, tenantId, siteId, cancellationToken);
        var row = await Row(connection, null, """
            SELECT
                COALESCE(SUM(archived_at IS NULL), 0),
                COALESCE(SUM(archived_at IS NULL AND enabled = TRUE), 0),
                COALESCE(SUM(archived_at IS NOT NULL), 0)
            FROM prompts WHERE site_id = UUID_TO_BIN(@site);
            """, cancellationToken, ("@site", siteId.ToString()));
        return new PromptSummaryView(
            int.Parse(row![0]),
            int.Parse(row[1]),
            int.Parse(row[2]));
    }

    private sealed record PromptCursor(DateTimeOffset SnapshotAt, DateTimeOffset LastAt, Guid LastId, string Scope);

    public async Task<CursorPage<PromptView>> ListPromptsAsync(Guid tenantId, Guid siteId, string? search, Guid? tagId,
        string? cursor, int limit, CancellationToken cancellationToken = default, Guid? groupId = null, bool ungrouped = false,
        bool includeAppEligibility = false)
    {
        if (limit is < 1 or > 100) throw Error("INVALID_LIMIT", "limit 必须在 1–100。", 400);
        if (groupId is not null && ungrouped) throw Error("INVALID_GROUP_FILTER", "groupId 与 ungrouped 不能同时指定。", 400);
        search = search?.Trim();
        if (search?.Length > 200) throw Error("INVALID_SEARCH", "搜索词过长。", 400);
        var scope = Hash(JsonSerializer.Serialize(new { tenantId, siteId, search, tagId, groupId, ungrouped }));
        PromptCursor? parsed = null;
        if (cursor is not null)
        {
            try
            {
                parsed = JsonSerializer.Deserialize<PromptCursor>(Convert.FromBase64String(cursor));
                if (parsed is null || parsed.Scope != scope) throw new FormatException();
            }
            catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
            { throw Error("INVALID_CURSOR", "游标与当前筛选条件不匹配。", 400); }
        }
        var snapshotAt = parsed?.SnapshotAt ?? DateTimeOffset.UtcNow;
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await EnsureSiteExists(connection, null, tenantId, siteId, cancellationToken);
        if (groupId is not null)
            await EnsurePromptGroup(connection, null, tenantId, siteId, groupId.Value, cancellationToken);
        // Archived Prompts are historical resources and must not re-enter the
        // active management list. Keep the projection itself inclusive for
        // GetPromptAsync, which is used by history-aware callers, and apply the
        // active-list boundary here.
        var sql = await PromptProjectionAsync(connection, null, cancellationToken) + " AND p.archived_at IS NULL AND p.created_at <= @snapshot";
        var parameters = new List<(string, object?)> { ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()),
            ("@snapshot", snapshotAt.UtcDateTime), ("@limit", limit + 1) };
        if (search is not null) { sql += " AND v.content LIKE @search ESCAPE '='"; parameters.Add(("@search", "%" + EscapePromptSearch(search) + "%")); }
        if (tagId is not null)
        {
            sql += " AND EXISTS (SELECT 1 FROM prompt_version_tags x WHERE x.prompt_version_id = v.id AND x.tag_id = UUID_TO_BIN(@tag))";
            parameters.Add(("@tag", tagId.ToString()));
        }
        if (groupId is not null)
        {
            sql += " AND p.group_id = UUID_TO_BIN(@group)";
            parameters.Add(("@group", groupId.Value.ToString()));
        }
        else if (ungrouped) sql += " AND p.group_id IS NULL";
        if (parsed is not null)
        {
            sql += " AND (p.created_at > @last OR (p.created_at = @last AND p.id > UUID_TO_BIN(@lastId)))";
            parameters.Add(("@last", parsed.LastAt.UtcDateTime)); parameters.Add(("@lastId", parsed.LastId.ToString()));
        }
        sql += " ORDER BY p.created_at, p.id LIMIT @limit;";
        var rows = await Rows(connection, null, sql, cancellationToken, parameters.ToArray());
        var result = new List<PromptView>();
        var appExcluded = includeAppEligibility ? await AppExcludedPromptsAsync(connection, tenantId, siteId, cancellationToken) : null;
        foreach (var row in rows.Take(limit))
            result.Add(WithAppEligibility(await ReadPrompt(connection, null, row, cancellationToken), appExcluded));
        string? next = null;
        if (rows.Count > limit)
        {
            var last = rows[limit - 1];
            next = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new PromptCursor(snapshotAt, Utc(last[9]), Guid.Parse(last[0]), scope)));
        }
        return new CursorPage<PromptView>(result, next);
    }

    public async Task<PromptView> UpdatePromptAsync(Guid tenantId, Guid siteId, Guid promptId, PromptPatch request,
        CancellationToken cancellationToken = default)
    {
        if (request.ExpectedVersion is null) throw Error("CONFIG_VERSION_REQUIRED", "请提供 expectedVersion。", 400);
        if (request.GroupId is not null && request.ClearGroup)
            throw Error("INVALID_GROUP", "groupId 与 clearGroup 不能同时指定。", 400);
        if (request.ResetTargetScope == true && request.TargetKeys is { Count: > 0 })
            throw Error(PromptMonitoringScale.TargetInvalidCode, "resetTargetScope 与非空 targetKeys 不能同时指定。", 400);
        // Omitted targetKeys keep the scope; an empty list or resetTargetScope=true restores 'all'.
        var scopeChange = request.ResetTargetScope == true || request.TargetKeys is not null;
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // 停用 -> 启用会重新占用提问名额，先取租户级锁再锁品牌。
        await LockTenant(connection, transaction, tenantId, cancellationToken);
        var site = await LockSite(connection, transaction, tenantId, siteId, cancellationToken);
        if (site[2] != "active") throw Error("RESOURCE_NOT_FOUND", "工作区已归档。", 404);
        var row = await Row(connection, transaction, await PromptProjectionAsync(connection, transaction, cancellationToken)
            + " AND p.id = UUID_TO_BIN(@id) FOR UPDATE;", cancellationToken,
            ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@id", promptId.ToString()));
        if (row is null) throw Error("RESOURCE_NOT_FOUND", "Prompt 不存在。", 404);
        if (!string.IsNullOrWhiteSpace(row[11]))
            throw Error("RESOURCE_NOT_FOUND", "Prompt 已删除。", 404);
        if (request.ExpectedVersion.Value != int.Parse(row[12])) throw Error("CONFIG_VERSION_CONFLICT", "Prompt 配置版本已变化。", 409);
        IReadOnlyList<string>? newScope = null;
        if (scopeChange)
        {
            await PromptTargetScopeSchema.RequireAsync(connection, transaction, cancellationToken);
            if (request.TargetKeys is { Count: > 0 } keys)
                newScope = await PromptMonitoringScale.ValidateTargetKeysAsync(connection, transaction, tenantId, siteId, keys, cancellationToken,
                    AppOpen(tenantId));
        }
        if (request.Enabled == true && !request.Archive && !Bool(row[4]))
            await SubscriptionLimitGuard.EnsurePromptsAsync(connection, transaction, tenantId, 1, cancellationToken);
        var currentGroupId = string.IsNullOrEmpty(row[13]) ? (Guid?)null : Guid.Parse(row[13]);
        var groupId = request.ClearGroup ? null : request.GroupId ?? currentGroupId;
        if (groupId is not null)
            await EnsurePromptGroup(connection, transaction, tenantId, siteId, groupId.Value, cancellationToken);
        if (Bool(row[4]) && (request.Archive || request.Enabled == false))
        {
            var enabledCount = await Row(connection, transaction, """
                SELECT COUNT(*) FROM prompts
                WHERE site_id = UUID_TO_BIN(@site) AND archived_at IS NULL AND enabled = TRUE;
                """, cancellationToken, ("@site", siteId.ToString()));
            if (enabledCount is not null && int.Parse(enabledCount[0]) <= 1)
                throw Error("LAST_ENABLED_PROMPT", "至少保留 1 个启用问题；如需暂停采集，请暂停监控计划。", 409);
        }
        var content = request.Content?.Trim() ?? row[1];
        if (content.Length is < 1 or > 10000) throw Error("INVALID_PROMPT", "Prompt 内容长度必须为 1–10000。", 400);
        var locale = ValidateLocale(request.Locale ?? row[3]);
        if (await FindDuplicate(connection, transaction, siteId, content, locale, promptId, cancellationToken) is not null)
            throw Error("PROMPT_DUPLICATE", "相同内容与语言的 Prompt 已存在。", 409);
        var oldTagRows = await Rows(connection, transaction,
            "SELECT BIN_TO_UUID(tag_id) FROM prompt_version_tags WHERE prompt_version_id = UUID_TO_BIN(@version);",
            cancellationToken, ("@version", row[8]));
        var tagIds = request.TagIds ?? oldTagRows.Select(r => Guid.Parse(r[0])).ToArray();
        var tags = await ResolveTags(connection, transaction, siteId, tagIds, cancellationToken);
        var branded = request.BrandedStatus is null && request.Content is null
            ? (Status: row[5], Source: row[6])
            : await ClassifyBrand(connection, transaction, siteId, content, request.BrandedStatus, request.BrandedSource, cancellationToken);
        var changed = content != row[1] || locale != row[3] || branded.Status != row[5] || branded.Source != row[6]
            || !tagIds.Order().SequenceEqual(oldTagRows.Select(r => Guid.Parse(r[0])).Order());
        var versionId = Guid.Parse(row[8]);
        if (changed)
        {
            versionId = Guid.NewGuid();
            await Exec(connection, transaction, """
                INSERT INTO prompt_versions (id, site_id, prompt_id, version, content, locale, branded_status, branded_source, created_at)
                VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@site), UUID_TO_BIN(@prompt), @version, @content, @locale, @branded, @source, UTC_TIMESTAMP(6));
                """, cancellationToken, ("@id", versionId.ToString()), ("@site", siteId.ToString()),
                ("@prompt", promptId.ToString()), ("@version", int.Parse(row[2]) + 1),
                ("@content", content), ("@locale", locale), ("@branded", branded.Status), ("@source", branded.Source));
            foreach (var tag in tags)
                await Exec(connection, transaction, """
                    INSERT INTO prompt_version_tags (prompt_version_id, tag_id, tag_name_snapshot)
                    VALUES (UUID_TO_BIN(@version), UUID_TO_BIN(@tag), @name);
                    """, cancellationToken, ("@version", versionId.ToString()), ("@tag", tag.TagId.ToString()), ("@name", tag.Name));
        }
        await Exec(connection, transaction, """
            UPDATE prompts SET group_id = UUID_TO_BIN(@group), content = @content, locale = @locale, enabled = @enabled, config_version = config_version + 1,
                current_version_id = UUID_TO_BIN(@version), archived_at = IF(@archive, COALESCE(archived_at, UTC_TIMESTAMP(6)), archived_at),
                updated_at = UTC_TIMESTAMP(6) WHERE id = UUID_TO_BIN(@id);
            """, cancellationToken, ("@content", content), ("@locale", locale),
            ("@enabled", request.Archive ? false : request.Enabled ?? Bool(row[4])), ("@version", versionId.ToString()),
            ("@archive", request.Archive), ("@group", groupId?.ToString()), ("@id", promptId.ToString()));
        // Scope changes ride on the config_version bump above; they never create a new prompt version.
        if (scopeChange)
            await WritePromptTargetScope(connection, transaction, tenantId, siteId, promptId, newScope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await GetPromptAsync(tenantId, siteId, promptId, cancellationToken))!;
    }

    private static readonly string[] CreationSources = ["ai_recommend", "keyword_expand", "batch_import", "manual"];

    /// <summary>导入行的新增字段解析结果：已有分组、待新建分组（显示名与规范化键）、人工品牌词分类与创建来源。</summary>
    private sealed record ImportRowFields(Guid? ExistingGroupId, string? NewGroupName, string? NewGroupKey,
        string? BrandedStatus, string? Source)
    {
        public string Resolution => ExistingGroupId is not null ? "existing" : NewGroupKey is not null ? "create" : "none";
    }

    // Validates the optional wizard fields of one row; preview turns the exception into a row error, import fails the batch.
    private static async Task<ImportRowFields> ResolveImportRow(MySqlConnection connection, MySqlTransaction transaction,
        Guid tenantId, Guid siteId, PromptImportRow row, CancellationToken cancellationToken)
    {
        if (row.GroupId is not null && row.GroupName is not null)
            throw Error("INVALID_GROUP", "groupId 与 groupName 不能同时指定。", 400);
        if (row.BrandedStatus is not (null or "branded" or "unbranded"))
            throw Error("INVALID_BRANDED_STATUS", "brandedStatus 无效。", 400);
        if (row.Source is not null && !CreationSources.Contains(row.Source, StringComparer.Ordinal))
            throw Error("INVALID_CREATION_SOURCE", "source 必须为 ai_recommend、keyword_expand、batch_import 或 manual。", 400);
        if (row.GroupId is { } groupId)
        {
            await EnsurePromptGroup(connection, transaction, tenantId, siteId, groupId, cancellationToken);
            return new(groupId, null, null, row.BrandedStatus, row.Source);
        }
        if (row.GroupName is not null)
        {
            var name = NormalizeGroupName(row.GroupName);
            var key = NormalizeGroupKey(name);
            var existing = await FindPromptGroupByKey(connection, transaction, tenantId, siteId, key, cancellationToken);
            return existing is not null
                ? new(existing, null, null, row.BrandedStatus, row.Source)
                : new(null, name, key, row.BrandedStatus, row.Source);
        }
        return new(null, null, null, row.BrandedStatus, row.Source);
    }

    /// <summary>导入幂等哈希：新增行字段为 null 时不参与序列化，旧客户端请求的哈希与改动前逐字节相同；请求级平台范围仅在提供时纳入。</summary>
    internal static string ImportRequestHash(IReadOnlyList<PromptImportRow> rows, IReadOnlyList<string>? targetKeys)
        => Hash(targetKeys is null ? JsonSerializer.Serialize(rows) : JsonSerializer.Serialize(new { Rows = rows, TargetKeys = targetKeys }));

    public async Task<PromptImportPreviewView> PreviewImportAsync(Guid tenantId, Guid siteId, IReadOnlyList<PromptImportRow> rows,
        CancellationToken cancellationToken = default)
    {
        if (rows is null || rows.Count is < 1 or > 200) throw Error("INVALID_IMPORT", "每批导入必须为 1–200 行。", 400);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var site = await LockSite(connection, transaction, tenantId, siteId, cancellationToken);
        if (site[2] != "active") throw Error("RESOURCE_NOT_FOUND", "工作区已归档。", 404);
        var results = new List<PromptImportRowResult>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row is null || string.IsNullOrWhiteSpace(row.Content) || row.Content.Length > 10000)
            { results.Add(new PromptImportRowResult(i + 1, "INVALID_PROMPT", null)); continue; }
            string locale;
            ImportRowFields fields;
            try
            {
                locale = ValidateLocale(row.Locale);
                await ResolveTags(connection, transaction, siteId, row.TagIds, cancellationToken);
                fields = await ResolveImportRow(connection, transaction, tenantId, siteId, row, cancellationToken);
            }
            catch (CollectorStoreException exception) { results.Add(new PromptImportRowResult(i + 1, exception.Code, null)); continue; }
            var key = ContentKey(row.Content, locale);
            if (!seen.Add(key))
            {
                duplicates++;
                results.Add(new PromptImportRowResult(i + 1, "DUPLICATE_IN_BATCH", null, fields.Resolution, fields.ExistingGroupId));
                continue;
            }
            var existing = await FindDuplicate(connection, transaction, siteId, row.Content, locale, null, cancellationToken);
            if (existing is not null) duplicates++;
            results.Add(new PromptImportRowResult(i + 1, existing is null ? null : "PROMPT_DUPLICATE", existing,
                fields.Resolution, fields.ExistingGroupId));
        }
        await transaction.CommitAsync(cancellationToken);
        return new PromptImportPreviewView(results, results.Count(r => r.ErrorCode is null), duplicates);
    }

    public async Task<PromptImportResult> ImportAsync(Guid tenantId, Guid siteId, IReadOnlyList<PromptImportRow> rows,
        string idempotencyKey, CancellationToken cancellationToken = default, IReadOnlyList<string>? targetKeys = null)
    {
        if (rows is null || rows.Count is < 1 or > 200 || rows.Any(row => row is null))
            throw Error("INVALID_IMPORT", "每批导入必须为 1–200 行。", 400);
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length is < 8 or > 191)
            throw Error("INVALID_IDEMPOTENCY_KEY", "幂等键长度必须为 8–191。", 400);
        var hash = ImportRequestHash(rows, targetKeys);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // 导入按实际新建行数一次性校验提问上限，先取租户级锁再锁品牌。
        await LockTenant(connection, transaction, tenantId, cancellationToken);
        var site = await LockSite(connection, transaction, tenantId, siteId, cancellationToken);
        if (site[2] != "active") throw Error("RESOURCE_NOT_FOUND", "工作区已归档。", 404);
        // Platform scope and creation source need 042; old-style imports without them keep working before the migration.
        if (targetKeys is not null || rows.Any(row => row.Source is not null))
            await PromptTargetScopeSchema.RequireAsync(connection, transaction, cancellationToken);
        var existing = await Row(connection, transaction, "SELECT request_hash, result FROM prompt_imports WHERE tenant_id = UUID_TO_BIN(@tenant) AND idempotency_key = @key;",
            cancellationToken, ("@tenant", tenantId.ToString()), ("@key", idempotencyKey));
        if (existing is not null)
        {
            if (existing[0] != hash) throw Error("IMPORT_IDEMPOTENCY_CONFLICT", "幂等键已用于不同导入。", 409);
            await transaction.CommitAsync(cancellationToken);
            return JsonSerializer.Deserialize<PromptImportResult>(existing[1])!;
        }
        var scope = targetKeys is null ? null
            : await PromptMonitoringScale.ValidateTargetKeysAsync(connection, transaction, tenantId, siteId, targetKeys, cancellationToken,
                AppOpen(tenantId));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicateCount = 0;
        // 第一遍只确定本批实际要新建的行（批内去重 + 已有重复）并解析分组，据此一次性校验套餐与监控规模，再写入。
        var planned = new List<(PromptImportRow? Row, string Locale, Guid? Existing, ImportRowFields? Fields)>();
        foreach (var row in rows)
        {
            var locale = ValidateLocale(row.Locale);
            if (string.IsNullOrWhiteSpace(row.Content) || row.Content.Length > 10000)
                throw Error("INVALID_IMPORT", "导入含无效 Prompt，请先调用预览。", 400);
            var fields = await ResolveImportRow(connection, transaction, tenantId, siteId, row, cancellationToken);
            var key = ContentKey(row.Content, locale);
            if (!seen.Add(key)) { duplicateCount++; continue; }
            var duplicate = await FindDuplicate(connection, transaction, siteId, row.Content, locale, null, cancellationToken);
            if (duplicate is not null) { duplicateCount++; planned.Add((null, locale, duplicate.Value, null)); continue; }
            planned.Add((row, locale, null, fields));
        }
        var newCount = planned.Count(item => item.Row is not null);
        await SubscriptionLimitGuard.EnsurePromptsAsync(connection, transaction, tenantId, newCount, cancellationToken);
        await PromptMonitoringScale.EnsureCapacityAsync(connection, transaction, tenantId, siteId, newCount, scope, cancellationToken);
        // Groups are created only for rows that are really inserted, once per normalized name, inside this transaction:
        // any later failure rolls them back together with the prompts.
        var createdGroups = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var fields in planned.Where(item => item.Row is not null).Select(item => item.Fields!))
        {
            if (fields.NewGroupKey is null || createdGroups.ContainsKey(fields.NewGroupKey)) continue;
            var groupId = Guid.NewGuid();
            await InsertPromptGroup(connection, transaction, tenantId, siteId, groupId, fields.NewGroupName!, fields.NewGroupKey, cancellationToken);
            createdGroups[fields.NewGroupKey] = groupId;
        }
        var resultIds = new List<Guid>();
        // Only the prompts inserted by this request (never the existing prompts of duplicate rows) trigger the instant collection.
        var createdIds = new List<Guid>();
        foreach (var (row, locale, existingId, fields) in planned)
        {
            if (existingId is { } duplicateId) { resultIds.Add(duplicateId); continue; }
            var duplicate = await FindDuplicate(connection, transaction, siteId, row!.Content, locale, null, cancellationToken);
            if (duplicate is not null) { duplicateCount++; resultIds.Add(duplicate.Value); continue; }
            var groupId = fields!.ExistingGroupId ?? (fields.NewGroupKey is null ? null : createdGroups[fields.NewGroupKey]);
            var promptId = await InsertPrompt(connection, transaction, tenantId, siteId,
                new PromptWrite(row.Content, locale, true, row.TagIds, fields.BrandedStatus,
                    fields.BrandedStatus is null ? null : "manual", groupId), cancellationToken, fields.Source, scope);
            resultIds.Add(promptId);
            createdIds.Add(promptId);
        }
        // Stored with the idempotent result, so a replay returns the same CreatedPromptIds (older rows read back as null).
        var result = new PromptImportResult(createdIds.Count, duplicateCount, resultIds, createdIds);
        await Exec(connection, transaction, """
            INSERT INTO prompt_imports (id, tenant_id, site_id, idempotency_key, request_hash, result, created_at)
            VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), @key, @hash, @result, UTC_TIMESTAMP(6));
            """, cancellationToken, ("@id", Guid.NewGuid().ToString()), ("@tenant", tenantId.ToString()),
            ("@site", siteId.ToString()), ("@key", idempotencyKey), ("@hash", hash),
            ("@result", JsonSerializer.Serialize(result)));
        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}
