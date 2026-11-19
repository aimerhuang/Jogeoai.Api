using System.Globalization;
using System.Text.Json;
using Jogeoai.Application;
using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

/// <summary>使用 MySQL 行锁维护租户积分余额、预占和不可变账本。</summary>
public sealed partial class CreditStore(MySqlDatabase database) : ICreditStore
{
    // The actor scope is part of the opaque cursor.  Without it, a member's
    // cursor could be replayed against the tenant-wide view (or another
    // member's view) and silently skip rows after the scope changes.
    private sealed record LedgerCursor(long CreatedTicks, Guid EntryId, Guid? ActorUserId = null);

    /// <inheritdoc />
    public async Task<CreditAccountView> GetAccountAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await EnsureAccountAsync(connection, null, tenantId, cancellationToken);
        var row = await RowAsync(connection, null, """
            SELECT available_credits, reserved_credits, lifetime_granted, lifetime_captured, version, updated_at
            FROM credit_accounts WHERE tenant_id=UUID_TO_BIN(@tenant);
            """, cancellationToken, ("@tenant", tenantId.ToString()));
        return row is null ? new CreditAccountView(0, 0, 0, 0, 0, DateTimeOffset.UtcNow) : ToAccount(row);
    }

    /// <inheritdoc />
    public async Task<CreditLedgerPage> ListLedgerAsync(Guid tenantId, Guid? actorUserId, string? cursor, int limit,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 100);
        var parsedCursor = ParseLedgerCursor(cursor);
        if (parsedCursor?.ActorUserId is not null && parsedCursor.ActorUserId != actorUserId)
            throw new CollectorStoreException("INVALID_CURSOR", "账本游标与当前查看范围不匹配。", 400);
        var createdAfter = parsedCursor is null ? DateTimeOffset.MinValue : new DateTimeOffset(parsedCursor.CreatedTicks, TimeSpan.Zero);
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        var sql = """
            SELECT BIN_TO_UUID(l.id), l.entry_type, l.amount, l.balance_after, BIN_TO_UUID(l.reservation_id),
                   BIN_TO_UUID(l.article_version_id), BIN_TO_UUID(l.actor_user_id), l.external_reference, l.reason, l.created_at
            FROM credit_ledger l WHERE l.tenant_id=UUID_TO_BIN(@tenant)
            """;
        sql += parsedCursor is null
            ? ""
            : parsedCursor.EntryId == Guid.Empty
                // Numeric cursors were emitted by the first implementation. Keep
                // accepting them, but use the id tie-breaker for all new cursors.
                ? " AND l.created_at > @after"
                : " AND (l.created_at > @after OR (l.created_at = @after AND l.id > UUID_TO_BIN(@after_id)))";
        if (actorUserId is not null)
            sql += " AND (l.actor_user_id=UUID_TO_BIN(@actor) OR EXISTS (SELECT 1 FROM credit_reservations r JOIN article_generation_batches b ON b.tenant_id=r.tenant_id AND b.id=r.batch_id WHERE r.tenant_id=l.tenant_id AND r.id=l.reservation_id AND b.requested_by=UUID_TO_BIN(@actor)))";
        sql += " ORDER BY l.created_at, l.id LIMIT @limit;";
        var rows = await RowsAsync(connection, null, sql, cancellationToken,
            ("@tenant", tenantId.ToString()), ("@actor", actorUserId?.ToString()), ("@after", createdAfter.UtcDateTime),
            ("@after_id", parsedCursor?.EntryId == Guid.Empty ? null : parsedCursor?.EntryId.ToString()), ("@limit", limit + 1));
        var items = rows.Take(limit).Select(ToLedger).ToArray();
        var next = rows.Count > limit && items.Length > 0
            ? EncodeLedgerCursor(new LedgerCursor(items[^1].CreatedAt.UtcTicks, items[^1].EntryId, actorUserId))
            : null;
        return new CreditLedgerPage(items, next);
    }

    /// <inheritdoc />
    public async Task<decimal> ReserveAsync(Guid tenantId, Guid batchId, Guid taskId, decimal amount,
        string idempotencyKey, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        if (amount <= 0 || amount > 1_000_000) throw new CollectorStoreException("INVALID_CREDIT_AMOUNT", "积分数量无效。", 400);
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 191)
            throw new CollectorStoreException("INVALID_IDEMPOTENCY_KEY", "幂等键无效。", 400);
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await EnsureAccountAsync(connection, transaction, tenantId, cancellationToken);
        var existing = await RowAsync(connection, transaction, """
            SELECT amount, status, BIN_TO_UUID(batch_id), BIN_TO_UUID(task_id)
            FROM credit_reservations WHERE tenant_id=UUID_TO_BIN(@tenant) AND idempotency_key=@key FOR UPDATE;
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@key", idempotencyKey));
        if (existing is not null)
        {
            var previous = decimal.Parse(existing[0], CultureInfo.InvariantCulture);
            if (previous != amount || !string.Equals(existing[2], batchId.ToString(), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(existing[3], taskId.ToString(), StringComparison.OrdinalIgnoreCase))
                throw new CollectorStoreException("IDEMPOTENCY_CONFLICT", "积分预占幂等键已用于其他任务或不同数量。", 409);
            await transaction.CommitAsync(cancellationToken);
            return previous;
        }
        var task = await RowAsync(connection, transaction, """
            SELECT reservation_id FROM article_generation_tasks
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND batch_id=UUID_TO_BIN(@batch) AND id=UUID_TO_BIN(@task)
            FOR UPDATE;
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@batch", batchId.ToString()), ("@task", taskId.ToString()));
        if (task is null)
            throw new CollectorStoreException("RESOURCE_NOT_FOUND", "积分预占任务不存在。", 404);
        if (!string.IsNullOrWhiteSpace(task[0]))
            throw new CollectorStoreException("IDEMPOTENCY_CONFLICT", "该生成任务已经存在积分预占。", 409);
        var account = await RowAsync(connection, transaction, """
            SELECT available_credits FROM credit_accounts WHERE tenant_id=UUID_TO_BIN(@tenant) FOR UPDATE;
            """, cancellationToken, ("@tenant", tenantId.ToString()));
        var available = decimal.Parse(account![0], CultureInfo.InvariantCulture);
        if (available < amount) throw new CollectorStoreException("CREDIT_INSUFFICIENT", "积分余额不足。", 402);
        var reservationId = Guid.NewGuid();
        await ExecAsync(connection, transaction, """
            UPDATE credit_accounts SET available_credits=available_credits-@amount,
                reserved_credits=reserved_credits+@amount, version=version+1, updated_at=UTC_TIMESTAMP(6)
            WHERE tenant_id=UUID_TO_BIN(@tenant);
            INSERT INTO credit_reservations (id,tenant_id,batch_id,task_id,amount,status,idempotency_key,created_at)
            VALUES (UUID_TO_BIN(@id),UUID_TO_BIN(@tenant),UUID_TO_BIN(@batch),UUID_TO_BIN(@task),@amount,'held',@key,UTC_TIMESTAMP(6));
            INSERT INTO credit_ledger (id,tenant_id,entry_type,amount,balance_after,reservation_id,actor_user_id,external_reference,created_at)
            SELECT UUID_TO_BIN(UUID()), tenant_id, 'reserve', -@amount, available_credits,
                   UUID_TO_BIN(@id), UUID_TO_BIN(@actor), CONCAT('reserve:', @id), UTC_TIMESTAMP(6)
            FROM credit_accounts WHERE tenant_id=UUID_TO_BIN(@tenant);
            UPDATE article_generation_tasks SET reservation_id=UUID_TO_BIN(@id)
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND batch_id=UUID_TO_BIN(@batch) AND id=UUID_TO_BIN(@task) AND reservation_id IS NULL;
            """, cancellationToken, ("@amount", amount), ("@id", reservationId.ToString()), ("@tenant", tenantId.ToString()),
            ("@batch", batchId.ToString()), ("@task", taskId.ToString()), ("@key", idempotencyKey), ("@actor", actorUserId.ToString()));
        await transaction.CommitAsync(cancellationToken);
        return amount;
    }

    /// <inheritdoc />
    public async Task<bool> CaptureAsync(Guid tenantId, Guid reservationId, Guid articleVersionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var row = await RowAsync(connection, transaction, """
            SELECT amount, status FROM credit_reservations WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@id) FOR UPDATE;
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@id", reservationId.ToString()));
        if (row is null) { await transaction.CommitAsync(cancellationToken); return false; }
        if (row[1].Equals("captured", StringComparison.OrdinalIgnoreCase)) { await transaction.CommitAsync(cancellationToken); return true; }
        if (!row[1].Equals("held", StringComparison.OrdinalIgnoreCase)) { await transaction.CommitAsync(cancellationToken); return false; }
        var amount = decimal.Parse(row[0], CultureInfo.InvariantCulture);
        var account = await RowAsync(connection, transaction,
            "SELECT reserved_credits FROM credit_accounts WHERE tenant_id=UUID_TO_BIN(@tenant) FOR UPDATE;",
            cancellationToken, ("@tenant", tenantId.ToString()));
        if (account is null || decimal.Parse(account[0], CultureInfo.InvariantCulture) < amount)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }
        await ExecAsync(connection, transaction, """
            UPDATE credit_accounts SET reserved_credits=reserved_credits-@amount,
                lifetime_captured=lifetime_captured+@amount, version=version+1, updated_at=UTC_TIMESTAMP(6)
            WHERE tenant_id=UUID_TO_BIN(@tenant);
            UPDATE credit_reservations SET status='captured',captured_at=UTC_TIMESTAMP(6)
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@id) AND status='held';
            INSERT IGNORE INTO credit_ledger (id,tenant_id,entry_type,amount,balance_after,reservation_id,article_version_id,external_reference,created_at)
            SELECT UUID_TO_BIN(UUID()), tenant_id, 'capture', 0, available_credits, UUID_TO_BIN(@id), UUID_TO_BIN(@version),
                   CONCAT('capture:', @id), UTC_TIMESTAMP(6) FROM credit_accounts WHERE tenant_id=UUID_TO_BIN(@tenant);
            """, cancellationToken, ("@amount", amount), ("@tenant", tenantId.ToString()), ("@id", reservationId.ToString()), ("@version", articleVersionId.ToString()));
        await transaction.CommitAsync(cancellationToken); return true;
    }

    /// <inheritdoc />
    public async Task<bool> ReleaseAsync(Guid tenantId, Guid reservationId, string reason,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var row = await RowAsync(connection, transaction, """
            SELECT amount, status FROM credit_reservations WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@id) FOR UPDATE;
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@id", reservationId.ToString()));
        if (row is null) { await transaction.CommitAsync(cancellationToken); return false; }
        if (row[1].Equals("released", StringComparison.OrdinalIgnoreCase)) { await transaction.CommitAsync(cancellationToken); return true; }
        if (!row[1].Equals("held", StringComparison.OrdinalIgnoreCase)) { await transaction.CommitAsync(cancellationToken); return false; }
        var amount = decimal.Parse(row[0], CultureInfo.InvariantCulture);
        var account = await RowAsync(connection, transaction,
            "SELECT reserved_credits FROM credit_accounts WHERE tenant_id=UUID_TO_BIN(@tenant) FOR UPDATE;",
            cancellationToken, ("@tenant", tenantId.ToString()));
        if (account is null || decimal.Parse(account[0], CultureInfo.InvariantCulture) < amount)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }
        reason ??= string.Empty;
        await ExecAsync(connection, transaction, """
            UPDATE credit_accounts SET available_credits=available_credits+@amount,
                reserved_credits=reserved_credits-@amount, version=version+1, updated_at=UTC_TIMESTAMP(6)
            WHERE tenant_id=UUID_TO_BIN(@tenant);
            UPDATE credit_reservations SET status='released',released_at=UTC_TIMESTAMP(6),reason=@reason
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@id) AND status='held';
            INSERT IGNORE INTO credit_ledger (id,tenant_id,entry_type,amount,balance_after,reservation_id,external_reference,reason,created_at)
            SELECT UUID_TO_BIN(UUID()), tenant_id, 'release', @amount, available_credits, UUID_TO_BIN(@id), CONCAT('release:', @id), @reason, UTC_TIMESTAMP(6)
            FROM credit_accounts WHERE tenant_id=UUID_TO_BIN(@tenant);
            """, cancellationToken, ("@amount", amount), ("@tenant", tenantId.ToString()), ("@id", reservationId.ToString()),
            ("@reason", reason.Length > 500 ? reason[..500] : reason));
        await transaction.CommitAsync(cancellationToken); return true;
    }

    /// <inheritdoc />
    public async Task<CreditLedgerEntryView> GrantAsync(Guid tenantId, decimal amount, string externalReference,
        Guid actorUserId, string reason, CancellationToken cancellationToken = default)
    {
        if (amount <= 0 || amount > 10_000_000) throw new CollectorStoreException("INVALID_CREDIT_AMOUNT", "积分数量无效。", 400);
        if (string.IsNullOrWhiteSpace(externalReference) || externalReference.Length > 191)
            throw new CollectorStoreException("INVALID_EXTERNAL_REFERENCE", "外部流水号无效。", 400);
        reason ??= string.Empty;
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await EnsureAccountAsync(connection, transaction, tenantId, cancellationToken);
        var existing = await RowAsync(connection, transaction, "SELECT BIN_TO_UUID(id),entry_type,amount,balance_after,BIN_TO_UUID(reservation_id),BIN_TO_UUID(article_version_id),BIN_TO_UUID(actor_user_id),external_reference,reason,created_at FROM credit_ledger WHERE tenant_id=UUID_TO_BIN(@tenant) AND external_reference=@reference FOR UPDATE;", cancellationToken,
            ("@tenant", tenantId.ToString()), ("@reference", externalReference));
        if (existing is not null)
        {
            var previousAmount = decimal.Parse(existing[2], CultureInfo.InvariantCulture);
            if (!string.Equals(existing[1], "grant", StringComparison.OrdinalIgnoreCase) || previousAmount != amount)
                throw new CollectorStoreException("IDEMPOTENCY_CONFLICT", "外部流水号已用于不同的积分入账。", 409);
            await transaction.CommitAsync(cancellationToken);
            return ToLedger(existing);
        }
        await ExecAsync(connection, transaction, """
            UPDATE credit_accounts SET available_credits=available_credits+@amount,lifetime_granted=lifetime_granted+@amount,
                version=version+1,updated_at=UTC_TIMESTAMP(6) WHERE tenant_id=UUID_TO_BIN(@tenant);
            INSERT INTO credit_ledger (id,tenant_id,entry_type,amount,balance_after,actor_user_id,external_reference,reason,created_at)
            SELECT UUID_TO_BIN(@id), tenant_id, 'grant', @amount, available_credits, UUID_TO_BIN(@actor), @reference, @reason, UTC_TIMESTAMP(6)
            FROM credit_accounts WHERE tenant_id=UUID_TO_BIN(@tenant);
            """, cancellationToken, ("@amount", amount), ("@tenant", tenantId.ToString()), ("@id", Guid.NewGuid().ToString()),
            ("@actor", actorUserId.ToString()), ("@reference", externalReference), ("@reason", reason.Length > 500 ? reason[..500] : reason));
        var result = await RowAsync(connection, transaction, "SELECT BIN_TO_UUID(id),entry_type,amount,balance_after,BIN_TO_UUID(reservation_id),BIN_TO_UUID(article_version_id),BIN_TO_UUID(actor_user_id),external_reference,reason,created_at FROM credit_ledger WHERE tenant_id=UUID_TO_BIN(@tenant) AND external_reference=@reference;", cancellationToken,
            ("@tenant", tenantId.ToString()), ("@reference", externalReference));
        await transaction.CommitAsync(cancellationToken); return ToLedger(result!);
    }

    private static async Task EnsureAccountAsync(MySqlConnection connection, MySqlTransaction? transaction, Guid tenantId, CancellationToken cancellationToken)
    {
        await ExecAsync(connection, transaction, "INSERT IGNORE INTO credit_accounts (tenant_id,updated_at) VALUES (UUID_TO_BIN(@tenant),UTC_TIMESTAMP(6));", cancellationToken,
            ("@tenant", tenantId.ToString()));
    }

    private static CreditAccountView ToAccount(string[] row) => new(decimal.Parse(row[0], CultureInfo.InvariantCulture), decimal.Parse(row[1], CultureInfo.InvariantCulture),
        decimal.Parse(row[2], CultureInfo.InvariantCulture), decimal.Parse(row[3], CultureInfo.InvariantCulture), long.Parse(row[4], CultureInfo.InvariantCulture), ParseDate(row[5]));

    private static CreditLedgerEntryView ToLedger(string[] row)
    {
        var type = Enum.TryParse<CreditLedgerEntryType>(row[1], true, out var value) ? value : CreditLedgerEntryType.Reversal;
        return new CreditLedgerEntryView(Guid.Parse(row[0]), type, decimal.Parse(row[2], CultureInfo.InvariantCulture), decimal.Parse(row[3], CultureInfo.InvariantCulture),
            ParseGuid(row[4]), ParseGuid(row[5]), ParseGuid(row[6]), row.Length > 7 ? row[7] : string.Empty, row.Length > 8 ? Null(row[8]) : null,
            ParseDate(row.Length > 9 ? row[9] : row[^1]));
    }

    private static Guid? ParseGuid(string value) => Guid.TryParse(value, out var id) && id != Guid.Empty ? id : null;
    private static string? Null(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static DateTimeOffset ParseDate(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static LedgerCursor? ParseLedgerCursor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            // Numeric cursors were accepted before the id tie-breaker was added.
            if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var legacyTicks)
                && legacyTicks >= 0)
                return new LedgerCursor(legacyTicks, Guid.Empty);
            if (value.Length > 1024) throw new FormatException();
            var cursor = JsonSerializer.Deserialize<LedgerCursor>(Convert.FromBase64String(value));
            if (cursor is null || cursor.CreatedTicks < 0 || cursor.EntryId == Guid.Empty)
                throw new FormatException();
            _ = new DateTimeOffset(cursor.CreatedTicks, TimeSpan.Zero);
            return cursor;
        }
        catch (Exception)
        {
            throw new CollectorStoreException("INVALID_CURSOR", "账本游标无效。", 400);
        }
    }

    private static string EncodeLedgerCursor(LedgerCursor cursor)
        => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(cursor));

    private static async Task<int> ExecAsync(MySqlConnection connection, MySqlTransaction? transaction, string sql,
        CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = new MySqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<string[]>> RowsAsync(MySqlConnection connection, MySqlTransaction? transaction, string sql,
        CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = new MySqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<string[]>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new string[reader.FieldCount];
            for (var i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? string.Empty : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? string.Empty;
            result.Add(row);
        }
        return result;
    }

    private static async Task<string[]?> RowAsync(MySqlConnection connection, MySqlTransaction? transaction, string sql,
        CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
        => (await RowsAsync(connection, transaction, sql, cancellationToken, parameters)).FirstOrDefault();
}
