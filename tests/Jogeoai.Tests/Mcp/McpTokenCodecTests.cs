using System.Text.RegularExpressions;
using Jogeoai.Application;

namespace Jogeoai.Tests.Mcp;

/// <summary>令牌生成、格式校验、哈希、定长比较、状态与节流规则（纯函数），以及 044 迁移 / 回滚的 SQL 文本约束。</summary>
public sealed class McpTokenCodecTests
{
    [Fact]
    public void GeneratedTokensHavePrefixFixedLengthBase64UrlAndAreUnique()
    {
        var tokens = Enumerable.Range(0, 200).Select(_ => McpTokenCodec.Generate()).ToArray();

        Assert.All(tokens, token =>
        {
            Assert.StartsWith("jgm_", token, StringComparison.Ordinal);
            Assert.Equal(McpTokenCodec.TokenLength, token.Length);
            Assert.Matches("^jgm_[A-Za-z0-9_-]{43}$", token);
            Assert.True(McpTokenCodec.IsWellFormed(token));
        });
        Assert.Equal(tokens.Length, tokens.Distinct(StringComparer.Ordinal).Count());
    }

    public static TheoryData<string?> MalformedTokens()
    {
        var body = new string('A', 43);
        return new TheoryData<string?>
        {
            null, "", "jgm_",
            "jgx_" + body, "JGM_" + body,
            "jgm_" + body[..42], "jgm_" + body + "A",
            "jgm_" + body[..42] + "+", "jgm_" + body[..42] + "=", "jgm_" + body[..42] + " ",
            // 会话令牌是 64 位 hex，不以 jgm_ 开头，格式校验即拒绝，不会进入 MCP 令牌查表。
            McpTokenCodec.Hash("session"),
        };
    }

    [Fact]
    public void WellFormedBodyOfExactLengthIsAccepted()
        => Assert.True(McpTokenCodec.IsWellFormed("jgm_" + new string('A', 42) + "_"));

    [Theory]
    [MemberData(nameof(MalformedTokens))]
    public void MalformedTokensAreRejectedWithoutLookup(string? value)
        => Assert.False(McpTokenCodec.IsWellFormed(value));

    [Fact]
    public void HashIsStableLowercaseSha256Hex()
    {
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", McpTokenCodec.Hash("abc"));
        var token = McpTokenCodec.Generate();
        Assert.Equal(McpTokenCodec.Hash(token), McpTokenCodec.Hash(token));
        Assert.Matches("^[0-9a-f]{64}$", McpTokenCodec.Hash(token));
        Assert.NotEqual(McpTokenCodec.Hash(token), McpTokenCodec.Hash(McpTokenCodec.Generate()));
        Assert.DoesNotContain(token[4..], McpTokenCodec.Hash(token), StringComparison.Ordinal);
    }

    [Fact]
    public void DisplayPrefixIsTwelveCharactersIncludingJgm()
    {
        var token = McpTokenCodec.Generate();
        var prefix = McpTokenCodec.DisplayPrefix(token);
        Assert.Equal(12, prefix.Length);
        Assert.StartsWith("jgm_", prefix, StringComparison.Ordinal);
        Assert.Equal(token[..12], prefix);
    }

    [Fact]
    public void FixedTimeEqualsComparesWholeValue()
    {
        var hash = McpTokenCodec.Hash("one");
        Assert.True(McpTokenCodec.FixedTimeEquals(hash, new string(hash.AsSpan())));
        Assert.False(McpTokenCodec.FixedTimeEquals(hash, McpTokenCodec.Hash("two")));
        Assert.False(McpTokenCodec.FixedTimeEquals(hash, hash[..^1] + (hash[^1] == '0' ? '1' : '0')));
        Assert.False(McpTokenCodec.FixedTimeEquals(hash, hash[..63]));
    }

    [Fact]
    public void StatusAndLastUsedThrottle()
    {
        var now = new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal("active", McpTokenCodec.Status(now.AddDays(1), null, now));
        Assert.Equal("expired", McpTokenCodec.Status(now, null, now));
        Assert.Equal("revoked", McpTokenCodec.Status(now.AddDays(1), now.AddMinutes(-1), now));

        Assert.True(McpTokenCodec.ShouldTouchLastUsed(null, now));
        Assert.False(McpTokenCodec.ShouldTouchLastUsed(now.AddSeconds(-60), now));
        Assert.True(McpTokenCodec.ShouldTouchLastUsed(now.AddSeconds(-61), now));
    }

    private static string Load(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../db/migrations", relative));
        Assert.True(File.Exists(path), $"找不到 migration：{path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void Migration044IsAdditiveAndReplaySafe()
    {
        var sql = Load("044_mcp_access_tokens.sql");
        Assert.Contains("CREATE TABLE IF NOT EXISTS mcp_access_tokens", sql, StringComparison.Ordinal);
        Assert.Contains("id BINARY(16) NOT NULL PRIMARY KEY", sql, StringComparison.Ordinal);
        Assert.Contains("tenant_id BINARY(16) NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("token_hash CHAR(64) COLLATE utf8mb4_0900_bin NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("token_prefix VARCHAR(12)", sql, StringComparison.Ordinal);
        Assert.Contains("site_ids JSON NULL", sql, StringComparison.Ordinal);
        Assert.Contains("UNIQUE KEY uq_mcp_access_tokens_hash (token_hash)", sql, StringComparison.Ordinal);
        Assert.Contains("KEY ix_mcp_access_tokens_tenant_revoked (tenant_id, revoked_at)", sql, StringComparison.Ordinal);
        Assert.Contains("FOREIGN KEY (tenant_id) REFERENCES tenants (id)", sql, StringComparison.Ordinal);
        Assert.Contains("expires_at <= created_at + INTERVAL 366 DAY", sql, StringComparison.Ordinal);
        Assert.Contains("ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci", sql, StringComparison.Ordinal);
        foreach (var column in new[] { "created_at", "expires_at", "revoked_at", "last_used_at" })
            Assert.Matches($@"{column} DATETIME\(6\)", sql);

        var code = Regex.Replace(sql, @"--[^\r\n]*", "");
        Assert.DoesNotContain("DROP TABLE", code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER TABLE", code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE FROM", code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ENUM(", code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\\", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Rollback044RefusesActiveTokensBeforeDropping()
    {
        var sql = Load("rollback/044_mcp_access_tokens_rollback.sql");
        Assert.Contains("CALL jogeoai_044_mcp_access_tokens_rollback();", sql, StringComparison.Ordinal);
        var refuse = sql.IndexOf("WHERE revoked_at IS NULL AND expires_at > UTC_TIMESTAMP(6)", StringComparison.Ordinal);
        var drop = sql.IndexOf("DROP TABLE IF EXISTS mcp_access_tokens", StringComparison.Ordinal);
        Assert.True(refuse > 0);
        Assert.True(drop > refuse);
        Assert.Contains("SIGNAL SQLSTATE '45000'", sql, StringComparison.Ordinal);
    }
}
