using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using MySqlConnector;

namespace Jogeoai.Tests.Mcp;

/// <summary>MCP 令牌 SQL 测试只连接已执行 001–044 的独立测试库（库名须含 _test）；未配置时跳过，绝不使用业务库 jogeoai。</summary>
public sealed class McpTokenMySqlFactAttribute : FactAttribute
{
    public McpTokenMySqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JOGEOAI_MCP_TEST_CONNECTION")))
            Skip = "需提供已执行 001–044 迁移的独立测试库 JOGEOAI_MCP_TEST_CONNECTION（库名含 _test）；不能使用业务库 jogeoai。";
    }
}

/// <summary>每个用例自建随机 UUID 的租户与用户，结束时只删除本用例租户的令牌、用户与租户。</summary>
[Collection("MCP isolated SQL")]
public sealed class McpTokenStoreSqlTests
{
    [McpTokenMySqlFact]
    public async Task CreateListRevokeAndFindActiveRoundTrip()
    {
        await using var f = await Fixture.CreateAsync();
        var site = Guid.NewGuid();
        var (token, created) = await f.CreateAsync("Grok Bot", [site]);

        Assert.Equal("active", created.Status);
        var principal = await f.Store.FindActiveByHashAsync(McpTokenCodec.Hash(token));
        Assert.NotNull(principal);
        Assert.Equal(f.Tenant, principal!.TenantId);
        Assert.Equal(new[] { site }, principal.SiteIds);
        Assert.Null(await f.Store.FindActiveByHashAsync(McpTokenCodec.Hash(McpTokenCodec.Generate())));

        var listed = Assert.Single(await f.Store.ListAsync(f.Tenant));
        Assert.Equal(created.Id, listed.Id);
        Assert.Equal(token[..12], listed.TokenPrefix);
        Assert.Empty(await f.Store.ListAsync(Guid.NewGuid()));

        Assert.Null(await f.Store.RevokeAsync(Guid.NewGuid(), created.Id));
        var revoked = await f.Store.RevokeAsync(f.Tenant, created.Id);
        Assert.Equal("revoked", revoked!.Status);
        var again = await f.Store.RevokeAsync(f.Tenant, created.Id);
        Assert.Equal(revoked.RevokedAt, again!.RevokedAt);
        Assert.Null(await f.Store.FindActiveByHashAsync(McpTokenCodec.Hash(token)));
    }

    [McpTokenMySqlFact]
    public async Task ActiveLimitIsTwentyPerTenantAndExpiredTokensDoNotCount()
    {
        await using var f = await Fixture.CreateAsync();
        for (var index = 0; index < McpTokenCodec.MaxActiveTokensPerTenant; index++) await f.CreateAsync($"bot {index}");
        var error = await Assert.ThrowsAsync<CollectorStoreException>(() => f.CreateAsync("one too many"));
        Assert.Equal("MCP_TOKEN_LIMIT_REACHED", error.Code);
        Assert.Equal(409, error.StatusCode);

        await f.ExecAsync("UPDATE mcp_access_tokens SET expires_at = created_at + INTERVAL 1 SECOND WHERE tenant_id = UUID_TO_BIN(@tenant) LIMIT 1;");
        await Task.Delay(1100);
        var (token, _) = await f.CreateAsync("after expiry");
        Assert.NotNull(await f.Store.FindActiveByHashAsync(McpTokenCodec.Hash(token)));
        Assert.Contains(await f.Store.ListAsync(f.Tenant), item => item.Status == "expired");
    }

    [McpTokenMySqlFact]
    public async Task LastUsedIsThrottledToOncePerMinute()
    {
        await using var f = await Fixture.CreateAsync();
        var (token, created) = await f.CreateAsync("bot");
        var first = DateTimeOffset.UtcNow;
        await f.Store.TouchLastUsedAsync(created.Id, first);
        await f.Store.TouchLastUsedAsync(created.Id, first.AddSeconds(30));
        var principal = await f.Store.FindActiveByHashAsync(McpTokenCodec.Hash(token));
        Assert.Equal(first.ToUnixTimeMilliseconds(), principal!.LastUsedAt!.Value.ToUnixTimeMilliseconds());
        await f.Store.TouchLastUsedAsync(created.Id, first.AddSeconds(61));
        principal = await f.Store.FindActiveByHashAsync(McpTokenCodec.Hash(token));
        Assert.Equal(first.AddSeconds(61).ToUnixTimeMilliseconds(), principal!.LastUsedAt!.Value.ToUnixTimeMilliseconds());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string connectionString;

        private Fixture(string connection)
        {
            connectionString = connection;
            Store = new McpTokenStore(new MySqlDatabase(connection));
        }

        public Guid Tenant { get; } = Guid.NewGuid();
        public Guid User { get; } = Guid.NewGuid();
        public McpTokenStore Store { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var value = Environment.GetEnvironmentVariable("JOGEOAI_MCP_TEST_CONNECTION");
            Assert.False(string.IsNullOrWhiteSpace(value), "未设置 JOGEOAI_MCP_TEST_CONNECTION；SQL 测试不会静默通过。");
            var builder = new MySqlConnectionStringBuilder(value!);
            Assert.False(string.IsNullOrWhiteSpace(builder.Database), "必须显式指定独立测试库。");
            Assert.NotEqual("jogeoai", builder.Database, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("_test", builder.Database, StringComparison.OrdinalIgnoreCase);
            var fixture = new Fixture(value!);
            await fixture.ExecAsync("""
                INSERT INTO tenants (id, name, status, created_at, updated_at)
                VALUES (UUID_TO_BIN(@tenant), 'mcp token fixture', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                INSERT INTO auth_users (id, email, password_hash, status, created_at, updated_at)
                VALUES (UUID_TO_BIN(@user), @email, 'not-a-login', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                """);
            return fixture;
        }

        public async Task<(string Token, McpTokenView View)> CreateAsync(string name, IReadOnlyList<Guid>? siteIds = null)
        {
            var token = McpTokenCodec.Generate();
            var now = DateTimeOffset.UtcNow;
            var view = await Store.CreateAsync(new McpTokenCreate(Guid.NewGuid(), Tenant, name, McpTokenCodec.Hash(token),
                McpTokenCodec.DisplayPrefix(token), siteIds, User, now, now.AddDays(30)));
            return (token, view);
        }

        public async Task ExecAsync(string sql)
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new MySqlCommand(sql, connection);
            command.Parameters.AddWithValue("@tenant", Tenant.ToString());
            command.Parameters.AddWithValue("@user", User.ToString());
            command.Parameters.AddWithValue("@email", $"mcp-{User:N}@example.test");
            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
            => await ExecAsync("""
                DELETE FROM mcp_access_tokens WHERE tenant_id = UUID_TO_BIN(@tenant);
                DELETE FROM auth_users WHERE id = UUID_TO_BIN(@user);
                DELETE FROM tenants WHERE id = UUID_TO_BIN(@tenant);
                """);
    }
}
