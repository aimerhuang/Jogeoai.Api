using Jogeoai.Domain;
using MySqlConnector;
using static Jogeoai.Infrastructure.DomesticCollectorStore;

namespace Jogeoai.Infrastructure;

/// <summary>提问的人工排除（design 5）：prompt_app_exclusions 只追加和撤销，不删除，本身就是审计记录。</summary>
public sealed partial class AppCollectionStore
{
    /// <summary>A prompt already has an active exclusion (409).</summary>
    internal const string AlreadyExcludedCode = "APP_PROMPT_ALREADY_EXCLUDED";

    /// <summary>A prompt has no active exclusion to revoke (404).</summary>
    internal const string ExclusionNotFoundCode = "APP_PROMPT_EXCLUSION_NOT_FOUND";

    private const int MaxReasonLength = 500;

    /// <inheritdoc />
    public async Task<AppPromptExclusionView> ExcludePromptAsync(Guid tenantId, Guid siteId, Guid promptId, string reason, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        reason = reason?.Trim() ?? "";
        if (reason.Length is < 1 or > MaxReasonLength)
            throw new CollectorStoreException("INVALID_EXCLUSION_REASON", $"排除原因必须为 1–{MaxReasonLength} 个字符。", 400);
        if (actorUserId == Guid.Empty) throw new CollectorStoreException("INVALID_ACTOR", "操作人不能为空。", 400);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await AppChannelSchema.RequireAsync(connection, null, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await RequirePromptAsync(connection, transaction, tenantId, siteId, promptId, cancellationToken);
        var id = Guid.NewGuid();
        try
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO prompt_app_exclusions (id, tenant_id, site_id, prompt_id, reason, created_by, created_at)
                VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), UUID_TO_BIN(@prompt), @reason, UUID_TO_BIN(@actor), UTC_TIMESTAMP(6));
                """, cancellationToken, ("@id", id.ToString()), ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()),
                ("@prompt", promptId.ToString()), ("@reason", reason), ("@actor", actorUserId.ToString()));
        }
        // uq_prompt_app_exclusions_active: at most one active exclusion per prompt.
        catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            throw new CollectorStoreException(AlreadyExcludedCode, "该提问已被排除出 App 渠道；如需修改原因，请先撤销再重新排除。", 409);
        }
        var view = await ReadExclusionAsync(connection, transaction, id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return view;
    }

    /// <inheritdoc />
    public async Task<AppPromptExclusionView> RevokePromptExclusionAsync(Guid tenantId, Guid siteId, Guid promptId, Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        if (actorUserId == Guid.Empty) throw new CollectorStoreException("INVALID_ACTOR", "操作人不能为空。", 400);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await AppChannelSchema.RequireAsync(connection, null, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var active = await QuerySingleAsync(connection, transaction, """
            SELECT BIN_TO_UUID(id) FROM prompt_app_exclusions
            WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site) AND prompt_id = UUID_TO_BIN(@prompt)
              AND revoked_at IS NULL FOR UPDATE;
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@prompt", promptId.ToString()));
        if (active is null) throw new CollectorStoreException(ExclusionNotFoundCode, "该提问没有生效中的 App 渠道排除。", 404);
        // The row is kept: revoking only records who revoked it and when.
        await ExecuteAsync(connection, transaction, """
            UPDATE prompt_app_exclusions SET revoked_by = UUID_TO_BIN(@actor), revoked_at = UTC_TIMESTAMP(6)
            WHERE id = UUID_TO_BIN(@id) AND revoked_at IS NULL;
            """, cancellationToken, ("@actor", actorUserId.ToString()), ("@id", active[0]));
        var view = await ReadExclusionAsync(connection, transaction, Guid.Parse(active[0]), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return view;
    }

    private static async Task RequirePromptAsync(MySqlConnection c, MySqlTransaction t, Guid tenantId, Guid siteId, Guid promptId,
        CancellationToken ct)
    {
        var prompt = await QuerySingleAsync(c, t, """
            SELECT 1 FROM prompts p JOIN sites s ON s.id = p.site_id
            WHERE s.tenant_id = UUID_TO_BIN(@tenant) AND p.site_id = UUID_TO_BIN(@site) AND p.id = UUID_TO_BIN(@prompt);
            """, ct, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@prompt", promptId.ToString()));
        if (prompt is null) throw new CollectorStoreException("RESOURCE_NOT_FOUND", "提问不存在或不属于该品牌。", 404);
    }

    private static async Task<AppPromptExclusionView> ReadExclusionAsync(MySqlConnection c, MySqlTransaction t, Guid id, CancellationToken ct)
    {
        await using var command = new MySqlCommand("""
            SELECT BIN_TO_UUID(id), BIN_TO_UUID(site_id), BIN_TO_UUID(prompt_id), reason, BIN_TO_UUID(created_by), created_at,
                   BIN_TO_UUID(revoked_by), revoked_at
            FROM prompt_app_exclusions WHERE id = UUID_TO_BIN(@id);
            """, c, t);
        command.Parameters.AddWithValue("@id", id.ToString());
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("排除记录不存在。");
        return new AppPromptExclusionView(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)),
            reader.GetString(3), Guid.Parse(reader.GetString(4)), Utc(reader.GetDateTime(5)),
            reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6)), reader.IsDBNull(7) ? null : Utc(reader.GetDateTime(7)));
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
