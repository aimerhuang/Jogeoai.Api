using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace Jogeoai.Tests;

/// <summary>登录频控 SQL 测试只连接已执行 045 的独立测试库（库名须含 _test）；未配置时跳过，绝不使用业务库 jogeoai。</summary>
public sealed class LoginThrottleMySqlFactAttribute : FactAttribute
{
    public LoginThrottleMySqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JOGEOAI_LOGIN_THROTTLE_TEST_CONNECTION")))
            Skip = "需提供已执行 045 迁移的独立测试库 JOGEOAI_LOGIN_THROTTLE_TEST_CONNECTION（库名含 _test）；不能使用业务库 jogeoai。";
    }
}

/// <summary>
/// <see cref="MySqlLoginThrottleStore"/> 的共享计数证明：两个 Store 实例（模拟两个 API 实例）共用一个库交替记录失败，
/// 阈值按合计生效；40 个并发失败计数精确且不死锁；缺表时抛出 503 AUTH_THROTTLE_SCHEMA_NOT_MIGRATED。
/// 每个用例使用随机账号哈希，并在结束时删除自己写入的行。
/// </summary>
public sealed class LoginThrottleSqlTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 1, 0, 0, 123_456 / 1000, TimeSpan.Zero);

    [LoginThrottleMySqlFact]
    public async Task TwoInstancesShareTheCountAndTheLock()
    {
        var connection = ConnectionString();
        var first = Store(connection);
        var second = Store(connection);
        var hash = NewHash();
        try
        {
            Assert.True((await first.CheckAsync(hash, T0)).IsAllowed);
            // 成功登录不产生计数行。
            await second.RecordResultAsync(hash, succeeded: true, T0);
            Assert.Null(await ReadAsync(connection, hash));

            for (var i = 1; i <= 29; i++)
                await (i % 2 == 0 ? first : second).RecordResultAsync(hash, succeeded: false, T0);
            Assert.Equal((29, (DateTime?)null), await ReadAsync(connection, hash));
            Assert.True((await first.CheckAsync(hash, T0)).IsAllowed);
            Assert.True((await second.CheckAsync(hash, T0)).IsAllowed);

            await first.RecordResultAsync(hash, succeeded: false, T0);
            var locked = await second.CheckAsync(hash, T0.AddSeconds(1));
            Assert.Equal(LoginThrottleOutcome.AccountLocked, locked.Outcome);
            Assert.Equal(59, locked.RetryAfterSeconds);
            Assert.Equal((30, (DateTime?)T0.AddMinutes(1).UtcDateTime), await ReadAsync(connection, hash));

            // 锁定到期后另一实例记录失败：按最终计数 31 锁 2 分钟。
            var later = T0.AddMinutes(1);
            Assert.True((await first.CheckAsync(hash, later)).IsAllowed);
            await second.RecordResultAsync(hash, succeeded: false, later);
            Assert.Equal(120, (await first.CheckAsync(hash, later)).RetryAfterSeconds);

            // 成功复位：计数清零、锁定清除，行保留。
            await first.RecordResultAsync(hash, succeeded: true, later.AddMinutes(5));
            Assert.Equal((0, (DateTime?)null), await ReadAsync(connection, hash));
            Assert.True((await second.CheckAsync(hash, later.AddMinutes(5))).IsAllowed);
        }
        finally
        {
            await DeleteAsync(connection, hash);
        }
    }

    [LoginThrottleMySqlFact]
    public async Task FortyConcurrentFailuresAreCountedExactlyWithoutDeadlock()
    {
        var connection = ConnectionString();
        var stores = new[] { Store(connection), Store(connection) };
        var hash = NewHash();
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 40).Select(i =>
                Task.Run(() => stores[i % 2].RecordResultAsync(hash, succeeded: false, T0))));
            var (failures, lockedUntil) = (await ReadAsync(connection, hash))!.Value;
            Assert.Equal(40, failures);
            // 第 40 次 → min(1 分钟 × 2^10, 15 分钟) = 15 分钟。
            Assert.Equal(T0.AddMinutes(15).UtcDateTime, lockedUntil);
        }
        finally
        {
            await DeleteAsync(connection, hash);
        }
    }

    [LoginThrottleMySqlFact]
    public async Task MissingTableThrowsTheSchemaNotMigratedError()
    {
        var connection = ConnectionString();
        var store = Store(connection);
        var hidden = $"auth_login_throttle_hidden_{Guid.NewGuid():N}"[..48];
        // 本类独占该测试库（环境变量专用），同类用例顺序执行；临时改名后在 finally 中恢复。
        await ExecAsync(connection, $"RENAME TABLE auth_login_throttle TO {hidden};");
        try
        {
            var check = await Assert.ThrowsAsync<CollectorStoreException>(() => store.CheckAsync(NewHash(), T0));
            Assert.Equal((LoginThrottleRules.SchemaNotMigratedCode, 503), (check.Code, check.StatusCode));
            var failure = await Assert.ThrowsAsync<CollectorStoreException>(() => store.RecordResultAsync(NewHash(), false, T0));
            Assert.Equal(LoginThrottleRules.SchemaNotMigratedCode, failure.Code);
            var success = await Assert.ThrowsAsync<CollectorStoreException>(() => store.RecordResultAsync(NewHash(), true, T0));
            Assert.Equal(LoginThrottleRules.SchemaNotMigratedCode, success.Code);
        }
        finally
        {
            await ExecAsync(connection, $"RENAME TABLE {hidden} TO auth_login_throttle;");
        }
    }

    private static MySqlLoginThrottleStore Store(string connection)
        => new(new MySqlDatabase(connection), Options.Create(new LoginThrottleOptions()));

    private static string NewHash() => LoginThrottleRules.AccountSubjectHash($"sql-{Guid.NewGuid():N}@example.test");

    private static string ConnectionString()
    {
        var value = Environment.GetEnvironmentVariable("JOGEOAI_LOGIN_THROTTLE_TEST_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(value), "未设置 JOGEOAI_LOGIN_THROTTLE_TEST_CONNECTION；SQL 测试不会静默通过。");
        var builder = new MySqlConnectionStringBuilder(value!);
        Assert.False(string.IsNullOrWhiteSpace(builder.Database), "必须显式指定独立测试库。");
        Assert.NotEqual("jogeoai", builder.Database, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("_test", builder.Database, StringComparison.OrdinalIgnoreCase);
        return value!;
    }

    private static async Task<(int Failures, DateTime? LockedUntil)?> ReadAsync(string connectionString, string hash)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand("""
            SELECT consecutive_failures, locked_until FROM auth_login_throttle
            WHERE scope = 'account' AND subject_hash = @hash;
            """, connection);
        command.Parameters.AddWithValue("@hash", hash);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return (reader.GetInt32(0), reader.IsDBNull(1) ? null : DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc));
    }

    private static Task DeleteAsync(string connectionString, string hash)
        => ExecAsync(connectionString, "DELETE FROM auth_login_throttle WHERE scope = 'account' AND subject_hash = @hash;", hash);

    private static async Task ExecAsync(string connectionString, string sql, string? hash = null)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        if (hash is not null) command.Parameters.AddWithValue("@hash", hash);
        await command.ExecuteNonQueryAsync();
    }
}
