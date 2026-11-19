using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

/// <summary>
/// 登录失败频控（迁移 045 <c>auth_login_throttle</c>）的显式 SQL 实现，多个 API 实例共享计数。
/// 检查为只读查询；失败记录在一个短事务内完成（原子递增 + 设置锁定截止时间）；缺表（MySQL 1146）统一映射为
/// 503 <c>AUTH_THROTTLE_SCHEMA_NOT_MIGRATED</c>。
/// </summary>
public sealed class MySqlLoginThrottleStore(MySqlDatabase database, IOptions<LoginThrottleOptions> options) : ILoginThrottleStore
{
    private const int MaxDeadlockAttempts = 3;

    /// <inheritdoc />
    public async Task<LoginThrottleDecision> CheckAsync(string accountHash, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ValidateHash(accountHash);
        try
        {
            await using var connection = database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = new MySqlCommand("""
                SELECT locked_until FROM auth_login_throttle
                WHERE scope = @scope AND subject_hash = @hash;
                """, connection);
            command.Parameters.AddWithValue("@scope", LoginThrottleRules.AccountScope);
            command.Parameters.AddWithValue("@hash", accountHash);
            var value = await command.ExecuteScalarAsync(cancellationToken);
            if (value is null or DBNull) return LoginThrottleDecision.Allowed;
            return LoginThrottleRules.Evaluate(new LoginThrottleState(0, Utc((DateTime)value)), now);
        }
        catch (MySqlException exception) when (IsMissingSchema(exception))
        {
            throw NotMigrated();
        }
    }

    /// <inheritdoc />
    public async Task RecordResultAsync(string accountHash, bool succeeded, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ValidateHash(accountHash);
        try
        {
            if (succeeded)
            {
                await ResetAsync(accountHash, now, cancellationToken);
                return;
            }
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await RecordFailureAsync(accountHash, now, cancellationToken);
                    return;
                }
                catch (MySqlException exception) when (IsRetryable(exception) && attempt < MaxDeadlockAttempts)
                {
                    // 整个事务已回滚，本次失败尚未计入，重试不会重复计数。
                }
            }
        }
        catch (MySqlException exception) when (IsMissingSchema(exception))
        {
            throw NotMigrated();
        }
    }

    private async Task RecordFailureAsync(string accountHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // 单条原子 upsert：并发失败按主键行锁串行，计数不会丢失；行锁保持到提交，期间不做任何密码计算。
        await using (var upsert = new MySqlCommand("""
            INSERT INTO auth_login_throttle (scope, subject_hash, consecutive_failures, locked_until, updated_at)
            VALUES (@scope, @hash, 1, NULL, @now)
            ON DUPLICATE KEY UPDATE consecutive_failures = consecutive_failures + 1, updated_at = @now;
            """, connection, transaction))
        {
            upsert.Parameters.AddWithValue("@scope", LoginThrottleRules.AccountScope);
            upsert.Parameters.AddWithValue("@hash", accountHash);
            upsert.Parameters.AddWithValue("@now", Db(now));
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }

        int failures;
        DateTimeOffset? current;
        await using (var read = new MySqlCommand("""
            SELECT consecutive_failures, locked_until FROM auth_login_throttle
            WHERE scope = @scope AND subject_hash = @hash FOR UPDATE;
            """, connection, transaction))
        {
            read.Parameters.AddWithValue("@scope", LoginThrottleRules.AccountScope);
            read.Parameters.AddWithValue("@hash", accountHash);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("auth_login_throttle row disappeared inside its own transaction.");
            failures = reader.GetInt32(0);
            current = reader.IsDBNull(1) ? null : Utc(reader.GetDateTime(1));
        }

        var lockedUntil = LoginThrottleRules.LockedUntilAfterFailure(failures, now, options.Value);
        if (lockedUntil is not null || current is not null)
        {
            await using var update = new MySqlCommand("""
                UPDATE auth_login_throttle SET locked_until = @locked
                WHERE scope = @scope AND subject_hash = @hash;
                """, connection, transaction);
            update.Parameters.AddWithValue("@locked", lockedUntil is { } value ? Db(value) : DBNull.Value);
            update.Parameters.AddWithValue("@scope", LoginThrottleRules.AccountScope);
            update.Parameters.AddWithValue("@hash", accountHash);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task ResetAsync(string accountHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        // 只更新已有且非零的行；没有计数行时不插入（成功登录不产生计数）。
        await using var command = new MySqlCommand("""
            UPDATE auth_login_throttle
            SET consecutive_failures = 0, locked_until = NULL, updated_at = @now
            WHERE scope = @scope AND subject_hash = @hash
              AND (consecutive_failures <> 0 OR locked_until IS NOT NULL);
            """, connection);
        command.Parameters.AddWithValue("@now", Db(now));
        command.Parameters.AddWithValue("@scope", LoginThrottleRules.AccountScope);
        command.Parameters.AddWithValue("@hash", accountHash);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void ValidateHash(string accountHash)
    {
        if (accountHash is not { Length: 64 } || !accountHash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new ArgumentException("Account hash must be 64 lowercase hex characters.", nameof(accountHash));
    }

    private static bool IsMissingSchema(MySqlException exception) => exception.Number == 1146;

    // 1213 死锁、1205 锁等待超时：事务整体回滚，可安全重试。
    private static bool IsRetryable(MySqlException exception) => exception.Number is 1213 or 1205;

    private static CollectorStoreException NotMigrated()
        => new(LoginThrottleRules.SchemaNotMigratedCode, "登录频控所需迁移 045 尚未部署，暂时无法登录。", 503);

    private static DateTime Db(DateTimeOffset value)
    {
        // DATETIME(6) 只保留微秒。
        var utc = value.ToUniversalTime();
        return new DateTime(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc);
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
