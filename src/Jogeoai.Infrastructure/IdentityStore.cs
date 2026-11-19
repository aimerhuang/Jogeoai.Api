using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Jogeoai.Application;
using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

/// <summary>使用 MySQL 持久化用户、成员关系和服务端会话。</summary>
public sealed partial class IdentityStore(MySqlDatabase database, IRegistrationEmailSender emailSender) : IIdentityStore
{
    /// <summary>浏览器会话默认有效期。</summary>
    public static readonly TimeSpan DefaultSessionLifetime = TimeSpan.FromDays(30);

    private const int PasswordIterations = 210_000;
    private const int PasswordSaltBytes = 16;
    private const int PasswordHashBytes = 32;

    /// <inheritdoc />
    public async Task<AuthSessionView> RegisterAsync(RegisterRequest request, SessionMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        var email = NormalizeEmail(request.Email);
        var password = ValidatePassword(request.Password);
        var displayName = NormalizeDisplayName(request.DisplayName, email);
        ValidateCodeFormat(request.EmailCode);
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await ConsumeRegistrationCodeAsync(connection, transaction, email, request.EmailCode, cancellationToken);
            await ExecuteAsync(connection, transaction, """
                INSERT INTO auth_users (id, email, password_hash, display_name, status, email_verified_at, created_at, updated_at)
                VALUES (UUID_TO_BIN(@id), @email, @password_hash, @display_name, 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                """, cancellationToken,
                ("@id", userId.ToString()), ("@email", email), ("@password_hash", HashPassword(password)),
                ("@display_name", displayName));
            await ExecuteAsync(connection, transaction, """
                INSERT INTO tenants (id, name, status, created_at, updated_at)
                VALUES (UUID_TO_BIN(@id), @name, 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                """, cancellationToken, ("@id", tenantId.ToString()), ("@name", displayName));
            await ExecuteAsync(connection, transaction,
                "INSERT INTO tenant_onboarding (tenant_id) VALUES (UUID_TO_BIN(@tenant));",
                cancellationToken, ("@tenant", tenantId.ToString()));
            await ExecuteAsync(connection, transaction, """
                INSERT INTO tenant_members (tenant_id, user_id, role, status, created_at, updated_at)
                VALUES (UUID_TO_BIN(@tenant), UUID_TO_BIN(@user), 'owner', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                """, cancellationToken, ("@tenant", tenantId.ToString()), ("@user", userId.ToString()));

            // 按免费体验版套餐（024 目录 free_trial.signup_credits）赠送一次性积分，并在同一事务写入订阅记录与事件。
            var signupCredits = await ReadSignupCreditsAsync(connection, transaction, cancellationToken);
            await ExecuteAsync(connection, transaction, """
                INSERT INTO credit_accounts (tenant_id, available_credits, reserved_credits, lifetime_granted, lifetime_captured, version, updated_at)
                VALUES (UUID_TO_BIN(@tenant), @credits, 0, @credits, 0, 1, UTC_TIMESTAMP(6));
                """, cancellationToken, ("@tenant", tenantId.ToString()), ("@credits", signupCredits));

            if (signupCredits > 0)
            {
                var ledgerId = Guid.NewGuid();
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO credit_ledger (id, tenant_id, entry_type, amount, balance_after, reservation_id, article_version_id, actor_user_id, external_reference, reason, created_at)
                    VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), 'grant', @credits, @credits, NULL, NULL, UUID_TO_BIN(@user), @ref, '新用户注册赠送', UTC_TIMESTAMP(6));
                    """, cancellationToken,
                    ("@id", ledgerId.ToString()),
                    ("@tenant", tenantId.ToString()),
                    ("@credits", signupCredits),
                    ("@user", userId.ToString()),
                    ("@ref", $"registration-{userId:N}"));
            }

            var subscriptionId = Guid.NewGuid();
            await ExecuteAsync(connection, transaction, """
                INSERT INTO tenant_subscriptions (id, tenant_id, plan_code, source, started_at, expires_at, period_index, period_started_at,
                    period_ends_at, period_quota, period_captured_baseline, version, created_at, updated_at)
                VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), 'free_trial', 'registration', UTC_TIMESTAMP(6), NULL, 0, NULL, NULL, 0, 0, 0,
                    UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                """, cancellationToken, ("@id", subscriptionId.ToString()), ("@tenant", tenantId.ToString()));
            await ExecuteAsync(connection, transaction, """
                INSERT INTO subscription_events (id, tenant_id, subscription_id, event_type, from_plan_code, to_plan_code, actor_user_id,
                    plan_code_id, period_index, credits_expired, credits_granted, reason, created_at)
                VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), UUID_TO_BIN(@subscription), 'registration', NULL, 'free_trial', UUID_TO_BIN(@user),
                    NULL, NULL, NULL, @credits, '新用户注册，默认免费体验版', UTC_TIMESTAMP(6));
                """, cancellationToken, ("@id", Guid.NewGuid().ToString()), ("@tenant", tenantId.ToString()),
                ("@subscription", subscriptionId.ToString()), ("@user", userId.ToString()), ("@credits", signupCredits));

            var session = await CreateSessionAsync(connection, transaction, userId, metadata, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            var user = new AuthUserView(userId, email, displayName, "active",
                [new TenantMembershipView(tenantId, displayName, "owner", "active")]);
            return new AuthSessionView(session.Token, session.ExpiresAt, user);
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw new CollectorStoreException("EMAIL_ALREADY_REGISTERED", "邮箱已注册。", 409);
        }
        catch (MySqlException exception) when (IsMissingAuthSchema(exception))
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw MissingAuthSchema();
        }
        catch
        {
            if (transaction.Connection is not null) await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<AuthSessionView?> LoginAsync(LoginRequest request, SessionMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        var email = NormalizeEmail(request.Email);
        var password = request.Password ?? string.Empty;
        if (password.Length > 256) return null;

        try
        {
            await using var connection = database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            var row = await ReadUserAsync(connection, null, email, cancellationToken);
            if (!CheckCredentials(row, password, VerifyPassword)) return null;
            var memberships = await LoadMembershipsAsync(connection, null, row.UserId, cancellationToken);
            if (memberships.Count == 0) return null;
            var session = await CreateSessionAsync(connection, null, row.UserId, metadata, cancellationToken);
            var user = new AuthUserView(row.UserId, row.Email, row.DisplayName, row.Status, memberships, row.IsSystemAdmin);
            return new AuthSessionView(session.Token, session.ExpiresAt, user);
        }
        catch (MySqlException exception) when (IsMissingAuthSchema(exception))
        {
            throw MissingAuthSchema();
        }
    }

    /// <inheritdoc />
    public async Task RevokeSessionAsync(string sessionToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionToken)) return;
        try
        {
            await using var connection = database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = new MySqlCommand("""
                UPDATE auth_sessions
                SET revoked_at = COALESCE(revoked_at, UTC_TIMESTAMP(6))
                WHERE token_hash = @token_hash;
                """, connection);
            command.Parameters.AddWithValue("@token_hash", TokenHash(sessionToken));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (MySqlException exception) when (IsMissingAuthSchema(exception))
        {
            throw MissingAuthSchema();
        }
    }

    /// <inheritdoc />
    public async Task<AuthenticatedRequestContext?> ValidateSessionAsync(string sessionToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionToken)) return null;
        try
        {
            await using var connection = database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            var row = await ReadSessionAsync(connection, sessionToken, cancellationToken);
            if (row is null || row.ExpiresAt <= DateTimeOffset.UtcNow || row.RevokedAt is not null
                || row.UserStatus != "active") return null;

            await using (var update = new MySqlCommand("""
                UPDATE auth_sessions
                SET last_seen_at = UTC_TIMESTAMP(6)
                WHERE id = UUID_TO_BIN(@id) AND revoked_at IS NULL;
                """, connection))
            {
                update.Parameters.AddWithValue("@id", row.SessionId.ToString());
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            var memberships = await LoadMembershipsAsync(connection, null, row.UserId, cancellationToken);
            if (memberships.Count == 0) return null;
            var user = new AuthUserView(row.UserId, row.Email, row.DisplayName, row.UserStatus, memberships, row.IsSystemAdmin);
            return new AuthenticatedRequestContext(user, row.SessionId, sessionToken);
        }
        catch (MySqlException exception) when (IsMissingAuthSchema(exception))
        {
            throw MissingAuthSchema();
        }
    }

    /// <summary>
    /// 校验凭据并消除账号是否存在的计时差：用户不存在或状态非 active 时，仍对静态假散列 <see cref="DummyPasswordHash"/>
    /// 以相同迭代次数调用一次 <paramref name="verify"/>，然后返回 false；其余情况与原逻辑一致。
    /// </summary>
    /// <param name="row">按规范化邮箱读到的用户行，可为空。</param>
    /// <param name="password">提交的密码。</param>
    /// <param name="verify">密码校验函数（密码, 编码后的散列）。</param>
    internal static bool CheckCredentials([NotNullWhen(true)] UserRow? row, string password, Func<string, string, bool> verify)
    {
        if (row is null || row.Status != "active")
        {
            _ = verify(password, DummyPasswordHash);
            return false;
        }
        return verify(password, row.PasswordHash);
    }

    /// <summary>
    /// 不对应任何账号的静态假散列，格式与迭代次数（210000）和真实散列一致，供未知或停用账号执行等价成本的 PBKDF2。
    /// 盐与散列值是固定的随机字节，任何密码都不会与之匹配。
    /// </summary>
    internal static readonly string DummyPasswordHash = string.Join('$', "pbkdf2-sha256", PasswordIterations,
        "q3sB0k2fVYx1Zq3cS7c3xA==", "J1h7mZ0bS0n2y0F6S3m3Qy9mW8b3N2h4dDVxR2c1V0k=");

    /// <summary>
    /// 规范化登录邮箱（去首尾空白、NFKC、转小写并校验格式）；格式无效时抛出 400 <c>INVALID_EMAIL</c>。
    /// 注册、登录与登录频控的账号计数主体共用此规则。
    /// </summary>
    /// <param name="value">原始邮箱。</param>
    public static string NormalizeEmail(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new CollectorStoreException("INVALID_EMAIL", "邮箱不能为空。", 400);
        var email = value.Trim().Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var at = email.IndexOf('@');
        if (email.Length is < 3 or > 320 || email.Count(c => c == '@') != 1 || at <= 0 || at == email.Length - 1
            || email.Any(char.IsWhiteSpace) || email.Any(char.IsControl) || email.EndsWith('.')
            || !email[(at + 1)..].Contains('.') || !System.Net.Mail.MailAddress.TryCreate(email, out var address)
            || address.Address != email)
            throw new CollectorStoreException("INVALID_EMAIL", "请输入有效的邮箱。", 400);
        return email;
    }

    private static string ValidatePassword(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length is < 8 or > 128)
            throw new CollectorStoreException("INVALID_PASSWORD", "密码长度必须为 8–128 个字符。", 400);
        return value;
    }

    private static string NormalizeDisplayName(string? value, string email)
    {
        var displayName = string.IsNullOrWhiteSpace(value) ? email[..email.IndexOf('@')] : value.Trim();
        displayName = displayName.Normalize(NormalizationForm.FormKC);
        if (displayName.Length is < 1 or > 200)
            throw new CollectorStoreException("INVALID_DISPLAY_NAME", "显示名称长度必须为 1–200 个字符。", 400);
        return displayName;
    }

    private static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(PasswordSaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, PasswordIterations,
            HashAlgorithmName.SHA256, PasswordHashBytes);
        return string.Join('$', "pbkdf2-sha256", PasswordIterations,
            Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    private static bool VerifyPassword(string password, string encoded)
    {
        try
        {
            var parts = encoded.Split('$', StringSplitOptions.None);
            if (parts.Length != 4 || parts[0] != "pbkdf2-sha256"
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations)
                || iterations < 100_000 || iterations > 2_000_000)
                return false;
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
        catch (ArgumentException) { return false; }
    }

    private static string CreateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static string TokenHash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static async Task<(string Token, DateTimeOffset ExpiresAt)> CreateSessionAsync(MySqlConnection connection,
        MySqlTransaction? transaction, Guid userId, SessionMetadata metadata, CancellationToken cancellationToken)
    {
        var token = CreateToken();
        var expiresAt = DateTimeOffset.UtcNow.Add(DefaultSessionLifetime);
        await ExecuteAsync(connection, transaction, """
            INSERT INTO auth_sessions (id, user_id, token_hash, expires_at, last_seen_at, created_at, created_ip, user_agent)
            VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@user), @token_hash, @expires_at, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6), @ip, @agent);
            """, cancellationToken,
            ("@id", Guid.NewGuid().ToString()), ("@user", userId.ToString()), ("@token_hash", TokenHash(token)),
            ("@expires_at", expiresAt.UtcDateTime), ("@ip", Limit(metadata.IpAddress, 64)), ("@agent", Limit(metadata.UserAgent, 512)));
        return (token, expiresAt);
    }

    /// <summary>读取免费体验版的一次性赠送积分；目录缺少 free_trial 时与迁移缺失同样返回 503。</summary>
    private static async Task<decimal> ReadSignupCreditsAsync(MySqlConnection connection, MySqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand("SELECT signup_credits FROM subscription_plans WHERE code = 'free_trial';", connection, transaction);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null or DBNull)
            throw new CollectorStoreException("AUTH_SCHEMA_NOT_MIGRATED", "套餐目录缺少 free_trial，请先执行迁移 024。", 503);
        return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(MySqlConnection connection, MySqlTransaction? transaction, string sql,
        CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = new MySqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<UserRow?> ReadUserAsync(MySqlConnection connection, MySqlTransaction? transaction,
        string email, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand("""
            SELECT BIN_TO_UUID(id), email, password_hash, display_name, status, COALESCE(is_system_admin, FALSE)
            FROM auth_users WHERE email = @email LIMIT 1;
            """, connection, transaction);
        command.Parameters.AddWithValue("@email", email);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new UserRow(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.GetBoolean(5));
    }

    private static async Task<IReadOnlyList<TenantMembershipView>> LoadMembershipsAsync(MySqlConnection connection,
        MySqlTransaction? transaction, Guid userId, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand("""
            SELECT BIN_TO_UUID(m.tenant_id), t.name, m.role, m.status
            FROM tenant_members m JOIN tenants t ON t.id = m.tenant_id
            WHERE m.user_id = UUID_TO_BIN(@user) AND m.status = 'active'
              AND t.status = 'active' AND t.deleted_at IS NULL
            ORDER BY m.created_at, m.tenant_id;
            """, connection, transaction);
        command.Parameters.AddWithValue("@user", userId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var memberships = new List<TenantMembershipView>();
        while (await reader.ReadAsync(cancellationToken))
            memberships.Add(new TenantMembershipView(Guid.Parse(reader.GetString(0)), reader.GetString(1),
                reader.GetString(2), reader.GetString(3)));
        return memberships;
    }

    private static async Task<SessionRow?> ReadSessionAsync(MySqlConnection connection, string token,
        CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand("""
            SELECT BIN_TO_UUID(s.id), BIN_TO_UUID(s.user_id), s.expires_at, s.revoked_at,
                   u.email, u.display_name, u.status, COALESCE(u.is_system_admin, FALSE)
            FROM auth_sessions s JOIN auth_users u ON u.id = s.user_id
            WHERE s.token_hash = @token_hash LIMIT 1;
            """, connection);
        command.Parameters.AddWithValue("@token_hash", TokenHash(token));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var expires = new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc));
        DateTimeOffset? revoked = reader.IsDBNull(3)
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc));
        return new SessionRow(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), expires, revoked,
            reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6), reader.GetBoolean(7));
    }

    /// <summary>登录校验读取的用户行。</summary>
    internal sealed record UserRow(Guid UserId, string Email, string PasswordHash, string? DisplayName, string Status, bool IsSystemAdmin);

    private sealed record SessionRow(Guid SessionId, Guid UserId, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt,
        string Email, string? DisplayName, string UserStatus, bool IsSystemAdmin);

    private static string? Limit(string? value, int length) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, length)];

    private static bool IsMissingAuthSchema(MySqlException exception) => exception.Number is 1054 or 1146;

    private static CollectorStoreException MissingAuthSchema() =>
        new("AUTH_SCHEMA_NOT_MIGRATED", "鉴权数据库迁移尚未执行，请先部署迁移 008–015 与 024。", 503);
}
