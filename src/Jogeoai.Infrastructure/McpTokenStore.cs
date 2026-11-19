using System.Text.Json;
using Jogeoai.Application;
using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

/// <summary>MCP 只读访问令牌（迁移 044）的显式 SQL 实现；缺表（MySQL 1146）统一映射为 503 MCP_SCHEMA_NOT_MIGRATED。</summary>
public sealed class McpTokenStore(MySqlDatabase database) : IMcpTokenStore
{
    internal const string NotMigratedCode = "MCP_SCHEMA_NOT_MIGRATED";
    private const int ListCap = 200;

    /// <inheritdoc />
    public async Task<McpTokenView> CreateAsync(McpTokenCreate request, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            // 锁住租户行，使同一租户的并发创建串行化，上限检查与插入在同一事务内完成。
            await using (var fence = new MySqlCommand(
                "SELECT id FROM tenants WHERE id = UUID_TO_BIN(@tenant) FOR UPDATE;", connection, transaction))
            {
                fence.Parameters.AddWithValue("@tenant", request.TenantId.ToString());
                if (await fence.ExecuteScalarAsync(cancellationToken) is null)
                    throw new CollectorStoreException("RESOURCE_NOT_FOUND", "租户不存在。", 404);
            }
            await using (var count = new MySqlCommand("""
                SELECT COUNT(*) FROM mcp_access_tokens
                WHERE tenant_id = UUID_TO_BIN(@tenant) AND revoked_at IS NULL AND expires_at > @now;
                """, connection, transaction))
            {
                count.Parameters.AddWithValue("@tenant", request.TenantId.ToString());
                count.Parameters.AddWithValue("@now", Db(request.CreatedAt));
                if (Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken)) >= McpTokenCodec.MaxActiveTokensPerTenant)
                    throw new CollectorStoreException("MCP_TOKEN_LIMIT_REACHED",
                        $"每个团队最多同时启用 {McpTokenCodec.MaxActiveTokensPerTenant} 个 MCP 令牌，请先吊销不用的令牌。", 409);
            }
            await using (var insert = new MySqlCommand("""
                INSERT INTO mcp_access_tokens (id, tenant_id, name, token_prefix, token_hash, site_ids,
                    created_by_user_id, created_at, expires_at)
                VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), @name, @prefix, @hash, @sites,
                    UUID_TO_BIN(@user), @created, @expires);
                """, connection, transaction))
            {
                insert.Parameters.AddWithValue("@id", request.TokenId.ToString());
                insert.Parameters.AddWithValue("@tenant", request.TenantId.ToString());
                insert.Parameters.AddWithValue("@name", request.Name);
                insert.Parameters.AddWithValue("@prefix", request.TokenPrefix);
                insert.Parameters.AddWithValue("@hash", request.TokenHash);
                insert.Parameters.AddWithValue("@sites", request.SiteIds is null
                    ? DBNull.Value : JsonSerializer.Serialize(request.SiteIds.Select(id => id.ToString())));
                insert.Parameters.AddWithValue("@user", request.CreatedByUserId.ToString());
                insert.Parameters.AddWithValue("@created", Db(request.CreatedAt));
                insert.Parameters.AddWithValue("@expires", Db(request.ExpiresAt));
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return new McpTokenView(request.TokenId, request.Name, request.TokenPrefix, request.SiteIds,
                request.CreatedByUserId, Truncate(request.CreatedAt), Truncate(request.ExpiresAt), null, null, "active");
        }
        catch (MySqlException exception) when (IsMissingSchema(exception))
        {
            throw NotMigrated();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<McpTokenView>> ListAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            // 启用令牌排在前面，保证上限内的启用令牌一定出现在列表中；其余按创建时间倒序，最多返回 200 条。
            await using var command = new MySqlCommand($"""
                SELECT {Columns}, UTC_TIMESTAMP(6)
                FROM mcp_access_tokens
                WHERE tenant_id = UUID_TO_BIN(@tenant)
                ORDER BY (revoked_at IS NULL AND expires_at > UTC_TIMESTAMP(6)) DESC, created_at DESC, id
                LIMIT {ListCap};
                """, connection);
            command.Parameters.AddWithValue("@tenant", tenantId.ToString());
            var items = new List<McpTokenView>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                items.Add(ReadView(reader, Utc(reader.GetDateTime(9))));
            return items;
        }
        catch (MySqlException exception) when (IsMissingSchema(exception))
        {
            throw NotMigrated();
        }
    }

    /// <inheritdoc />
    public async Task<McpTokenView?> RevokeAsync(Guid tenantId, Guid tokenId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using (var update = new MySqlCommand("""
                UPDATE mcp_access_tokens SET revoked_at = UTC_TIMESTAMP(6)
                WHERE tenant_id = UUID_TO_BIN(@tenant) AND id = UUID_TO_BIN(@id) AND revoked_at IS NULL;
                """, connection))
            {
                update.Parameters.AddWithValue("@tenant", tenantId.ToString());
                update.Parameters.AddWithValue("@id", tokenId.ToString());
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
            await using var select = new MySqlCommand($"""
                SELECT {Columns}, UTC_TIMESTAMP(6)
                FROM mcp_access_tokens
                WHERE tenant_id = UUID_TO_BIN(@tenant) AND id = UUID_TO_BIN(@id);
                """, connection);
            select.Parameters.AddWithValue("@tenant", tenantId.ToString());
            select.Parameters.AddWithValue("@id", tokenId.ToString());
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadView(reader, Utc(reader.GetDateTime(9))) : null;
        }
        catch (MySqlException exception) when (IsMissingSchema(exception))
        {
            throw NotMigrated();
        }
    }

    /// <inheritdoc />
    public async Task<McpTokenPrincipal?> FindActiveByHashAsync(string tokenHash, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(tokenHash) || tokenHash.Length != 64) return null;
        try
        {
            await using var connection = database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = new MySqlCommand("""
                SELECT BIN_TO_UUID(m.id), BIN_TO_UUID(m.tenant_id), m.token_hash, m.site_ids, m.expires_at, m.last_used_at
                FROM mcp_access_tokens m JOIN tenants t ON t.id = m.tenant_id
                WHERE m.token_hash = @hash AND m.revoked_at IS NULL AND m.expires_at > UTC_TIMESTAMP(6)
                  AND t.status = 'active' AND t.deleted_at IS NULL;
                """, connection);
            command.Parameters.AddWithValue("@hash", tokenHash);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            return new McpTokenPrincipal(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                reader.GetString(2), reader.IsDBNull(3) ? null : ParseSiteIds(reader.GetString(3)),
                Utc(reader.GetDateTime(4)), reader.IsDBNull(5) ? null : Utc(reader.GetDateTime(5)));
        }
        catch (MySqlException exception) when (IsMissingSchema(exception))
        {
            throw NotMigrated();
        }
    }

    /// <inheritdoc />
    public async Task TouchLastUsedAsync(Guid tokenId, DateTimeOffset usedAt, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = new MySqlCommand("""
                UPDATE mcp_access_tokens SET last_used_at = @used
                WHERE id = UUID_TO_BIN(@id) AND (last_used_at IS NULL OR last_used_at < @threshold);
                """, connection);
            command.Parameters.AddWithValue("@id", tokenId.ToString());
            command.Parameters.AddWithValue("@used", Db(usedAt));
            command.Parameters.AddWithValue("@threshold", Db(usedAt - McpTokenCodec.LastUsedTouchInterval));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (MySqlException exception) when (IsMissingSchema(exception))
        {
            throw NotMigrated();
        }
    }

    internal static IReadOnlyList<Guid>? ParseSiteIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        var values = JsonSerializer.Deserialize<string[]>(json) ?? [];
        return values.Select(Guid.Parse).ToArray();
    }

    private const string Columns = """
        BIN_TO_UUID(id), name, token_prefix, site_ids, BIN_TO_UUID(created_by_user_id),
        created_at, expires_at, revoked_at, last_used_at
        """;

    private static McpTokenView ReadView(MySqlDataReader reader, DateTimeOffset now)
    {
        var expires = Utc(reader.GetDateTime(6));
        DateTimeOffset? revoked = reader.IsDBNull(7) ? null : Utc(reader.GetDateTime(7));
        return new McpTokenView(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : ParseSiteIds(reader.GetString(3)), Guid.Parse(reader.GetString(4)),
            Utc(reader.GetDateTime(5)), expires, revoked, reader.IsDBNull(8) ? null : Utc(reader.GetDateTime(8)),
            McpTokenCodec.Status(expires, revoked, now));
    }

    private static bool IsMissingSchema(MySqlException exception) => exception.Number == 1146;

    private static CollectorStoreException NotMigrated()
        => new(NotMigratedCode, "MCP 访问令牌所需迁移 044 尚未部署。", 503);

    private static DateTime Db(DateTimeOffset value) => Truncate(value).UtcDateTime;

    // DATETIME(6) 只保留微秒；返回值与库内一致。
    private static DateTimeOffset Truncate(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
