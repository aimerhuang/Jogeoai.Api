using System.Globalization;
using System.Security.Cryptography;
using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

public sealed partial class IdentityStore
{
    /// <inheritdoc />
    public async Task<RegistrationCodeSentView> SendRegistrationCodeAsync(SendRegistrationCodeRequest request,
        CancellationToken cancellationToken = default)
    {
        var email = NormalizeEmail(request.Email);
        emailSender.EnsureConfigured();
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        var hash = HashPassword(code);
        var challenge = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddMinutes(10);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        try
        {
            await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
            {
                if (await ReadUserAsync(connection, transaction, email, cancellationToken) is not null)
                    throw new CollectorStoreException("EMAIL_ALREADY_REGISTERED", "邮箱已注册，请直接登录。", 409);
                // 首次插入和重复发送共用同一行锁，多个 API 实例不能绕过邮箱发送间隔。
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO auth_email_verifications
                        (email, challenge_id, code_hash, expires_at, sent_at, window_started_at, send_count)
                    VALUES (@email, UUID_TO_BIN(@challenge), '', @now, '2000-01-01', @now, 0)
                    ON DUPLICATE KEY UPDATE email = VALUES(email);
                    """, cancellationToken, ("@email", email), ("@challenge", challenge), ("@now", now.UtcDateTime));
                await using var read = new MySqlCommand("""
                    SELECT sent_at, window_started_at, send_count FROM auth_email_verifications WHERE email = @email FOR UPDATE;
                    """, connection, transaction);
                read.Parameters.AddWithValue("@email", email);
                DateTime sentAt, window;
                int count;
                await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
                {
                    await reader.ReadAsync(cancellationToken);
                    sentAt = reader.GetDateTime(0);
                    window = reader.GetDateTime(1);
                    count = reader.GetInt32(2);
                }
                if (now.UtcDateTime < sentAt.AddSeconds(60))
                    throw new CollectorStoreException("EMAIL_CODE_RATE_LIMITED", "发送太频繁，请等待 60 秒后重试。", 429);
                if (now.UtcDateTime < window.AddHours(1) && count >= 10)
                    throw new CollectorStoreException("EMAIL_CODE_RATE_LIMITED", "此邮箱发送次数已达上限，请一小时后重试。", 429);
                if (now.UtcDateTime >= window.AddHours(1)) { window = now.UtcDateTime; count = 0; }
                await ExecuteAsync(connection, transaction, """
                    UPDATE auth_email_verifications SET challenge_id = UUID_TO_BIN(@challenge), code_hash = @hash,
                        expires_at = @expires, sent_at = @now, delivered = FALSE, attempts = 0, consumed_at = NULL,
                        window_started_at = @window, send_count = @count WHERE email = @email;
                    """, cancellationToken, ("@challenge", challenge), ("@hash", hash), ("@expires", expiresAt.UtcDateTime),
                    ("@now", now.UtcDateTime), ("@window", window), ("@count", count + 1), ("@email", email));
                await transaction.CommitAsync(cancellationToken);
            }
            await emailSender.SendCodeAsync(email, code, cancellationToken);
            // 网络发送失败时保留未投递状态。已接受的邮件需可验证，即使客户端随后断开。
            await ExecuteAsync(connection, null, """
                UPDATE auth_email_verifications SET delivered = TRUE
                WHERE email = @email AND challenge_id = UUID_TO_BIN(@challenge);
                """, CancellationToken.None, ("@email", email), ("@challenge", challenge));
            return new RegistrationCodeSentView(expiresAt, 60);
        }
        catch (MySqlException exception) when (IsMissingAuthSchema(exception)) { throw MissingAuthSchema(); }
    }

    private static void ValidateCodeFormat(string? code)
    {
        if (code is null || code.Length != 6 || code.Any(c => c is < '0' or > '9'))
            throw new CollectorStoreException("INVALID_EMAIL_CODE", "请输入 6 位邮件验证码。", 400);
    }

    private static async Task ConsumeRegistrationCodeAsync(MySqlConnection connection, MySqlTransaction transaction,
        string email, string code, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand("""
            SELECT code_hash, expires_at, attempts, consumed_at, delivered
            FROM auth_email_verifications WHERE email = @email FOR UPDATE;
            """, connection, transaction);
        command.Parameters.AddWithValue("@email", email);
        string? hash = null;
        DateTime expires = default;
        var attempts = 0;
        var eligible = false;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                hash = reader.GetString(0);
                expires = reader.GetDateTime(1);
                attempts = reader.GetInt32(2);
                eligible = reader.IsDBNull(3) && reader.GetBoolean(4);
            }
        }
        if (!eligible || expires <= DateTime.UtcNow || attempts >= 5 || hash is null)
            throw new CollectorStoreException("EMAIL_CODE_EXPIRED", "验证码无效、已过期或尝试次数过多，请重新获取。", 400);
        if (!VerifyPassword(code, hash))
        {
            await ExecuteAsync(connection, transaction,
                "UPDATE auth_email_verifications SET attempts = attempts + 1 WHERE email = @email;",
                cancellationToken, ("@email", email));
            // 错误次数必须提交，不能随注册失败回滚。
            await transaction.CommitAsync(cancellationToken);
            throw new CollectorStoreException("INVALID_EMAIL_CODE", "验证码不正确，请检查邮件。", 400);
        }
        await ExecuteAsync(connection, transaction,
            "UPDATE auth_email_verifications SET consumed_at = UTC_TIMESTAMP(6) WHERE email = @email;",
            cancellationToken, ("@email", email));
    }
}
