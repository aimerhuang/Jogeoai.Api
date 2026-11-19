using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Providers.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

/// <summary>Cloro 账号 key 池的 MySQL 实现；明文 key 只在选 key/轮询/刷新时读出，任何视图只返回 key_hint。</summary>
public sealed class CloroKeyPoolStore(MySqlDatabase database, IOptions<CloroKeyPoolOptions> options) : ICloroKeyPool, ICloroKeyAdminStore
{
    private const int DuplicateEntry = 1062;
    private const string InFlightStates = "'leased','queued','running','retryable_failed'";

    // Cooldown applies to every status; an exhausted account is selectable again only after a cycle
    // reset that happened after it was last confirmed exhausted. Requires @threshold.
    internal static string Selectable(string a) => $"""
        ({a}.enabled = TRUE AND {a}.archived_at IS NULL
         AND ({a}.cooldown_until IS NULL OR {a}.cooldown_until <= UTC_TIMESTAMP(6))
         AND (({a}.status IN ('unknown','active','cooling')
                AND ({a}.credits_remaining IS NULL OR {a}.credits_remaining > @threshold))
              OR ({a}.status = 'exhausted' AND {a}.cycle_resets_at IS NOT NULL AND {a}.cycle_resets_at <= UTC_TIMESTAMP(6)
                AND ({a}.exhausted_at IS NULL OR {a}.exhausted_at < {a}.cycle_resets_at))))
        """;

    // Waits only on a 429 cooldown with credits left; exhaustion, invalid keys and 403s are not capacity.
    private static string CapacityCooling(string a) => $"""
        ({a}.status IN ('unknown','active','cooling') AND {a}.cooldown_until > UTC_TIMESTAMP(6)
         AND {a}.last_error_code IN ('CONCURRENCY_LIMIT_EXCEEDED','RATE_LIMIT_EXCEEDED','QUEUE_LIMIT_EXCEEDED','HTTP_429')
         AND ({a}.credits_remaining IS NULL OR {a}.credits_remaining > @threshold))
        """;

    private static string RecoverAt(string a) => $"""
        CASE WHEN {a}.status = 'invalid' THEN NULL
             WHEN {a}.status = 'exhausted' THEN IF({a}.cycle_resets_at > UTC_TIMESTAMP(6),
                  GREATEST({a}.cycle_resets_at, COALESCE({a}.cooldown_until, {a}.cycle_resets_at)), NULL)
             WHEN {a}.cooldown_until > UTC_TIMESTAMP(6) THEN {a}.cooldown_until
             ELSE NULL END
        """;

    // Submitted before the pool existed (or by an older release): no cloro_key_id, polled through the default label.
    private static readonly string UnassignedInFlight = $"""
        SELECT COUNT(*) FROM provider_tasks t WHERE t.cloro_key_id IS NULL AND t.provider = 'cloro'
          AND t.state IN ({InFlightStates}) AND t.external_task_id IS NOT NULL
        """;

    // Unassigned tasks count against the default account, so its key cannot be replaced or archived under them.
    private static string InFlightCount(string a) => $"""
        ((SELECT COUNT(*) FROM provider_tasks t WHERE t.cloro_key_id = {a}.id AND t.provider = 'cloro'
            AND t.state IN ({InFlightStates}) AND t.external_task_id IS NOT NULL)
         + IF({a}.account_label = '{CloroKeyFormat.DefaultAccountLabel}', ({UnassignedInFlight}), 0))
        """;

    private static readonly string ViewProjection = $"""
        SELECT BIN_TO_UUID(k.id), k.account_label, k.key_hint, k.priority, k.enabled, k.status,
               IF({Selectable("k")}, 1, 0), k.credits_remaining, k.credits_per_cycle, k.cycle_resets_at,
               k.credits_checked_at, k.cooldown_until, k.last_used_at, k.last_error_code, k.last_error_at,
               {InFlightCount("k")},
               k.archived_at, k.created_at, k.updated_at
        FROM cloro_api_keys k
        """;

    private decimal Threshold => options.Value.LowBalanceThreshold;

    /// <inheritdoc />
    public async Task<CloroKeyLease?> PickAsync(IReadOnlyCollection<Guid> excluded, CancellationToken cancellationToken = default)
    {
        var parameters = new List<(string, object?)> { ("@threshold", Threshold) };
        var exclusion = new StringBuilder();
        if (excluded.Count > 0)
        {
            exclusion.Append(" AND k.id NOT IN (")
                .Append(string.Join(',', excluded.Select((_, i) => $"UUID_TO_BIN(@x{i})"))).Append(')');
            parameters.AddRange(excluded.Select((id, i) => ($"@x{i}", (object?)id.ToString())));
        }
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        // No row lock: several Workers may pick the same account; Cloro arbitrates and the loser switches.
        var row = await SingleAsync(connection, null, $"""
            SELECT BIN_TO_UUID(k.id), k.account_label, k.api_key, k.key_hint FROM cloro_api_keys k
            WHERE {Selectable("k")}{exclusion}
            ORDER BY k.priority, k.cycle_resets_at IS NULL, k.cycle_resets_at,
                     k.last_used_at IS NOT NULL, k.last_used_at, k.id
            LIMIT 1;
            """, cancellationToken, parameters.ToArray());
        return row is null ? null : Lease(row);
    }

    /// <inheritdoc />
    public async Task<CloroKeyLease?> GetAsync(Guid? keyId, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var row = keyId is { } id
            ? await SingleAsync(connection, null,
                "SELECT BIN_TO_UUID(id), account_label, api_key, key_hint FROM cloro_api_keys WHERE id = UUID_TO_BIN(@id);",
                cancellationToken, ("@id", id.ToString()))
            : await SingleAsync(connection, null,
                "SELECT BIN_TO_UUID(id), account_label, api_key, key_hint FROM cloro_api_keys WHERE account_label = @label;",
                cancellationToken, ("@label", CloroKeyFormat.DefaultAccountLabel));
        return row is null ? null : Lease(row);
    }

    /// <inheritdoc />
    public async Task<CloroKeyPoolAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await CloroKeyPoolSchema.RequireAsync(connection, null, cancellationToken);
        var row = (await SingleAsync(connection, null, $"""
            SELECT COUNT(*), COALESCE(SUM(IF({Selectable("k")}, 1, 0)), 0),
                   MIN(IF({Selectable("k")}, NULL, {RecoverAt("k")})),
                   COALESCE(SUM(IF(NOT {Selectable("k")} AND {CapacityCooling("k")}, 1, 0)), 0)
            FROM cloro_api_keys k WHERE k.enabled = TRUE AND k.archived_at IS NULL;
            """, cancellationToken, ("@threshold", Threshold)))!;
        return new CloroKeyPoolAvailability(Int(row[0]), Int(row[1]), NullableUtc(row[2]), Int(row[3]));
    }

    /// <inheritdoc />
    public async Task MarkUsedAsync(Guid keyId, Guid? providerTaskId, string externalTaskId, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await ExecuteAsync(connection, transaction, """
            UPDATE cloro_api_keys SET last_used_at = UTC_TIMESTAMP(6) WHERE id = UUID_TO_BIN(@key);
            INSERT INTO cloro_key_events (id, key_id, provider_task_id, event_type, external_task_id, created_at)
            VALUES (UUID_TO_BIN(@event), UUID_TO_BIN(@key), UUID_TO_BIN(@task), 'submitted', @external, UTC_TIMESTAMP(6));
            """, cancellationToken, ("@key", keyId.ToString()), ("@event", Guid.NewGuid().ToString()),
            ("@task", providerTaskId?.ToString()), ("@external", externalTaskId));
        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task MarkRejectedAsync(Guid keyId, Guid? providerTaskId, CloroKeyRejection rejection, string errorCode,
        CancellationToken cancellationToken = default)
    {
        var code = SafeCode(errorCode);
        var update = rejection switch
        {
            // credits_remaining is kept: a later refresh must see a real top-up or reset before recovering.
            CloroKeyRejection.InsufficientCredits => """
                UPDATE cloro_api_keys SET exhausted_at = UTC_TIMESTAMP(6), credits_checked_at = NULL,
                    status = 'exhausted', last_error_code = @code, last_error_at = UTC_TIMESTAMP(6), updated_at = UTC_TIMESTAMP(6)
                WHERE id = UUID_TO_BIN(@key);
                """,
            // Stamping checked_at re-checks an invalid key on the regular interval, not every refresh pass.
            CloroKeyRejection.Unauthorized => """
                UPDATE cloro_api_keys SET cooldown_until = NULL, status = 'invalid', credits_checked_at = UTC_TIMESTAMP(6),
                    last_error_code = @code, last_error_at = UTC_TIMESTAMP(6), updated_at = UTC_TIMESTAMP(6)
                WHERE id = UUID_TO_BIN(@key);
                """,
            // Never downgrade an exhausted/invalid account to cooling; the cooldown still applies to it.
            _ => """
                UPDATE cloro_api_keys SET
                    cooldown_until = GREATEST(COALESCE(cooldown_until, UTC_TIMESTAMP(6)),
                        DATE_ADD(UTC_TIMESTAMP(6), INTERVAL @micros MICROSECOND)),
                    status = CASE WHEN status IN ('unknown','active') THEN 'cooling' ELSE status END,
                    last_error_code = @code, last_error_at = UTC_TIMESTAMP(6), updated_at = UTC_TIMESTAMP(6)
                WHERE id = UUID_TO_BIN(@key);
                """,
        };
        var cooldown = rejection switch
        {
            CloroKeyRejection.ConcurrencyLimited => options.Value.ConcurrencyCooldown,
            CloroKeyRejection.RateLimited => options.Value.RateLimitCooldown,
            CloroKeyRejection.QueueFull => options.Value.QueueFullCooldown,
            CloroKeyRejection.Forbidden => options.Value.ForbiddenCooldown,
            _ => TimeSpan.Zero,
        };
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await ExecuteAsync(connection, transaction, update, cancellationToken, ("@key", keyId.ToString()), ("@code", code),
            ("@micros", (long)Math.Clamp(cooldown.TotalMicroseconds, 1_000_000, TimeSpan.FromDays(1).TotalMicroseconds)));
        await InsertEventAsync(connection, transaction, keyId, providerTaskId, "rejected", code, null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task ApplyCreditsAsync(Guid keyId, CloroCreditsSnapshot credits, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var row = await SingleAsync(connection, transaction, """
            SELECT status, IF(cooldown_until > UTC_TIMESTAMP(6), 1, 0), credits_remaining, cycle_resets_at
            FROM cloro_api_keys WHERE id = UUID_TO_BIN(@key) FOR UPDATE;
            """, cancellationToken, ("@key", keyId.ToString()));
        if (row is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }
        var current = CloroKeyStatusRules.Parse(row[0]);
        var next = CloroKeyStatusRules.AfterCreditsRefresh(current, row[1] == "1", NullableDecimal(row[2]),
            NullableUtc(row[3]), credits.Remaining, credits.CycleResetsAt, Threshold);
        await ExecuteAsync(connection, transaction, """
            UPDATE cloro_api_keys SET credits_remaining = COALESCE(@remaining, credits_remaining),
                credits_per_cycle = COALESCE(@per, credits_per_cycle), cycle_resets_at = COALESCE(@resets, cycle_resets_at),
                credits_checked_at = UTC_TIMESTAMP(6),
                exhausted_at = CASE WHEN @status = 'exhausted' THEN UTC_TIMESTAMP(6) ELSE exhausted_at END,
                status = @status, updated_at = UTC_TIMESTAMP(6)
            WHERE id = UUID_TO_BIN(@key);
            """, cancellationToken, ("@remaining", credits.Remaining), ("@per", credits.PerCycle),
            ("@resets", credits.CycleResetsAt?.UtcDateTime), ("@status", CloroKeyStatusRules.ToStorage(next)),
            ("@key", keyId.ToString()));
        await InsertEventAsync(connection, transaction, keyId, null, "credits_refreshed", null, null, cancellationToken);
        if (next != current)
            await InsertEventAsync(connection, transaction, keyId, null, "status_changed",
                CloroKeyStatusRules.ToStorage(next), null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task MarkCreditsFailedAsync(Guid keyId, string errorCode, CancellationToken cancellationToken = default)
    {
        var code = SafeCode(errorCode);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        // Setting checked_at backs off to the regular interval instead of retrying every pass.
        await ExecuteAsync(connection, transaction, """
            UPDATE cloro_api_keys SET credits_checked_at = UTC_TIMESTAMP(6), last_error_code = @code,
                last_error_at = UTC_TIMESTAMP(6), updated_at = UTC_TIMESTAMP(6)
            WHERE id = UUID_TO_BIN(@key);
            """, cancellationToken, ("@key", keyId.ToString()), ("@code", code));
        await InsertEventAsync(connection, transaction, keyId, null, "credits_refreshed", code, null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CloroKeyLease>> ListRefreshDueAsync(TimeSpan interval, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await CloroKeyPoolSchema.RequireAsync(connection, null, cancellationToken);
        var rows = await RowsAsync(connection, null, """
            SELECT BIN_TO_UUID(id), account_label, api_key, key_hint FROM cloro_api_keys
            WHERE enabled = TRUE AND archived_at IS NULL
              AND (credits_checked_at IS NULL
                   OR credits_checked_at <= DATE_SUB(UTC_TIMESTAMP(6), INTERVAL @micros MICROSECOND))
            ORDER BY credits_checked_at IS NOT NULL, credits_checked_at, id LIMIT 100;
            """, cancellationToken, ("@micros", (long)Math.Max(0, interval.TotalMicroseconds)));
        return rows.Select(Lease).ToArray();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CloroKeyView>> ListAsync(bool includeArchived, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await CloroKeyPoolSchema.RequireAsync(connection, null, cancellationToken);
        var rows = await RowsAsync(connection, null, ViewProjection + """

            WHERE (@all OR k.archived_at IS NULL)
            ORDER BY k.archived_at IS NOT NULL, k.priority, k.account_label, k.id;
            """, cancellationToken, ("@threshold", Threshold), ("@all", includeArchived));
        return rows.Select(View).ToArray();
    }

    /// <inheritdoc />
    async Task<CloroKeyView?> ICloroKeyAdminStore.GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await CloroKeyPoolSchema.RequireAsync(connection, null, cancellationToken);
        return await ReadViewAsync(connection, null, id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string accountLabel, string apiKey, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await CloroKeyPoolSchema.RequireAsync(connection, null, cancellationToken);
        return await SingleAsync(connection, null,
            "SELECT 1 FROM cloro_api_keys WHERE account_label = @label OR api_key_sha256 = @sha LIMIT 1;",
            cancellationToken, ("@label", accountLabel), ("@sha", Sha256(apiKey))) is not null;
    }

    /// <inheritdoc />
    public async Task<CloroKeyView> CreateAsync(CreateCloroKeyCommand command, CancellationToken cancellationToken = default)
    {
        var label = CloroKeyFormat.NormalizeLabel(command.AccountLabel);
        var key = CloroKeyFormat.NormalizeApiKey(command.ApiKey);
        var priority = CloroKeyFormat.ValidatePriority(command.Priority);
        var id = Guid.NewGuid();
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await CloroKeyPoolSchema.RequireAsync(connection, null, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO cloro_api_keys (id, account_label, api_key, api_key_sha256, key_hint, priority, enabled,
                    status, created_at, updated_at)
                VALUES (UUID_TO_BIN(@id), @label, @key, @sha, @hint, @priority, @enabled, 'unknown',
                    UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                """, cancellationToken, ("@id", id.ToString()), ("@label", label), ("@key", key), ("@sha", Sha256(key)),
                ("@hint", CloroKeyFormat.Hint(key)), ("@priority", priority), ("@enabled", command.Enabled));
        }
        catch (MySqlException exception) when (exception.Number == DuplicateEntry) { throw Duplicate(); }
        await InsertEventAsync(connection, transaction, id, null, "admin_changed", "CREATED", null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await ReadViewAsync(connection, null, id, cancellationToken))!;
    }

    /// <inheritdoc />
    public async Task<CloroKeyView?> UpdateAsync(UpdateCloroKeyCommand command, CancellationToken cancellationToken = default)
    {
        var label = command.AccountLabel is null ? null : CloroKeyFormat.NormalizeLabel(command.AccountLabel);
        if (command.Priority is { } priority) CloroKeyFormat.ValidatePriority(priority);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await CloroKeyPoolSchema.RequireAsync(connection, null, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var row = await SingleAsync(connection, transaction,
            "SELECT status, account_label = @default FROM cloro_api_keys WHERE id = UUID_TO_BIN(@id) AND archived_at IS NULL FOR UPDATE;",
            cancellationToken, ("@id", command.Id.ToString()), ("@default", CloroKeyFormat.DefaultAccountLabel));
        if (row is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }
        // Renaming default would leave unassigned in-flight tasks without a key to poll with.
        if (row[1] == "1" && label is not null
            && !string.Equals(label, CloroKeyFormat.DefaultAccountLabel, StringComparison.OrdinalIgnoreCase))
        {
            var unassigned = Int((await SingleAsync(connection, transaction, UnassignedInFlight + ";", cancellationToken))![0]);
            if (unassigned > 0)
                throw new CollectorStoreException("CLORO_KEY_IN_USE",
                    $"仍有 {unassigned} 个未记录 key 的在途任务通过 default 账号轮询，待其结束后再改名。", 409);
        }
        // Re-enabling an invalid key asks the refresh Worker to validate it again.
        var reactivate = command.Enabled == true && row[0] == "invalid";
        try
        {
            await ExecuteAsync(connection, transaction, """
                UPDATE cloro_api_keys SET account_label = COALESCE(@label, account_label),
                    priority = COALESCE(@priority, priority), enabled = COALESCE(@enabled, enabled),
                    credits_checked_at = CASE WHEN @reactivate THEN NULL ELSE credits_checked_at END,
                    status = CASE WHEN @reactivate THEN 'unknown' ELSE status END, updated_at = UTC_TIMESTAMP(6)
                WHERE id = UUID_TO_BIN(@id);
                """, cancellationToken, ("@label", label), ("@priority", command.Priority), ("@enabled", command.Enabled),
                ("@reactivate", reactivate), ("@id", command.Id.ToString()));
        }
        catch (MySqlException exception) when (exception.Number == DuplicateEntry) { throw Duplicate(); }
        await InsertEventAsync(connection, transaction, command.Id, null, "admin_changed", "UPDATED", null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await ReadViewAsync(connection, null, command.Id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<CloroKeyArchiveResult?> ArchiveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await CloroKeyPoolSchema.RequireAsync(connection, null, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var row = await SingleAsync(connection, transaction,
            "SELECT archived_at FROM cloro_api_keys WHERE id = UUID_TO_BIN(@id) FOR UPDATE;",
            cancellationToken, ("@id", id.ToString()));
        if (row is null || !string.IsNullOrEmpty(row[0]))
        {
            await transaction.RollbackAsync(cancellationToken);
            return row is null ? null : new CloroKeyArchiveResult(true, 0);
        }
        var inFlight = await InFlightAsync(connection, transaction, id, cancellationToken);
        if (inFlight > 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new CloroKeyArchiveResult(false, inFlight);
        }
        await ExecuteAsync(connection, transaction, """
            UPDATE cloro_api_keys SET archived_at = UTC_TIMESTAMP(6), enabled = FALSE, updated_at = UTC_TIMESTAMP(6)
            WHERE id = UUID_TO_BIN(@id);
            """, cancellationToken, ("@id", id.ToString()));
        await InsertEventAsync(connection, transaction, id, null, "admin_changed", "ARCHIVED", null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CloroKeyArchiveResult(true, 0);
    }

    /// <inheritdoc />
    public async Task<CloroKeyPoolSummary> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await CloroKeyPoolSchema.RequireAsync(connection, null, cancellationToken);
        var row = (await SingleAsync(connection, null, $"""
            SELECT COUNT(*),
                   COALESCE(SUM(x.sel), 0),
                   COALESCE(SUM(x.enabled = TRUE AND x.sel = 0 AND x.status = 'exhausted'), 0),
                   COALESCE(SUM(x.enabled = TRUE AND x.sel = 0 AND x.status NOT IN ('exhausted','invalid')
                       AND x.cooldown_until > UTC_TIMESTAMP(6)), 0),
                   COALESCE(SUM(x.enabled = TRUE AND x.status = 'invalid'), 0),
                   SUM(IF(x.sel = 1, x.credits_remaining, NULL)),
                   COALESCE(SUM(x.sel = 1 AND x.credits_remaining IS NULL), 0),
                   MIN(IF(x.enabled = TRUE AND x.cycle_resets_at > UTC_TIMESTAMP(6), x.cycle_resets_at, NULL)),
                   MIN(IF(x.enabled = TRUE AND x.sel = 0, {RecoverAt("x")}, NULL))
            FROM (SELECT k.enabled, k.status, k.cooldown_until, k.credits_remaining, k.cycle_resets_at,
                         IF({Selectable("k")}, 1, 0) sel
                  FROM cloro_api_keys k WHERE k.archived_at IS NULL) x;
            """, cancellationToken, ("@threshold", Threshold)))!;
        var available = Int(row[1]);
        var known = NullableDecimal(row[5]);
        // A pool with unknown balances is not reported as low until every available account has been checked.
        var status = available == 0 ? CloroKeyPoolStatus.Exhausted
            : Int(row[6]) == 0 && (known ?? 0) < options.Value.PoolLowBalanceWarning ? CloroKeyPoolStatus.Low
            : CloroKeyPoolStatus.Healthy;
        return new CloroKeyPoolSummary(Int(row[0]), available, Int(row[2]), Int(row[3]), Int(row[4]), known,
            NullableUtc(row[7]), NullableUtc(row[8]), status);
    }

    /// <inheritdoc />
    public async Task UpsertDefaultKeyAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        var key = CloroKeyFormat.NormalizeApiKey(apiKey);
        var sha = Sha256(key);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await CloroKeyPoolSchema.RequireAsync(connection, null, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var row = await SingleAsync(connection, transaction,
            "SELECT BIN_TO_UUID(id), api_key_sha256, archived_at FROM cloro_api_keys WHERE account_label = @label FOR UPDATE;",
            cancellationToken, ("@label", CloroKeyFormat.DefaultAccountLabel));
        try
        {
            if (row is null)
            {
                var id = Guid.NewGuid();
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO cloro_api_keys (id, account_label, api_key, api_key_sha256, key_hint, priority, enabled,
                        status, created_at, updated_at)
                    VALUES (UUID_TO_BIN(@id), @label, @key, @sha, @hint, 100, TRUE, 'unknown', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                    """, cancellationToken, ("@id", id.ToString()), ("@label", CloroKeyFormat.DefaultAccountLabel),
                    ("@key", key), ("@sha", sha), ("@hint", CloroKeyFormat.Hint(key)));
                await InsertEventAsync(connection, transaction, id, null, "admin_changed", "CREATED", null, cancellationToken);
            }
            else if (row[1] != sha || !string.IsNullOrEmpty(row[2]))
            {
                var id = Guid.Parse(row[0]);
                var inFlight = await InFlightAsync(connection, transaction, id, cancellationToken);
                if (inFlight > 0)
                    throw new CollectorStoreException("CLORO_KEY_IN_USE",
                        $"default 账号仍有 {inFlight} 个在途任务，请新增账号后停用 default，待任务结束再替换。", 409);
                await ExecuteAsync(connection, transaction, """
                    UPDATE cloro_api_keys SET api_key = @key, api_key_sha256 = @sha, key_hint = @hint, status = 'unknown',
                        credits_remaining = NULL, credits_per_cycle = NULL, cycle_resets_at = NULL, credits_checked_at = NULL,
                        cooldown_until = NULL, exhausted_at = NULL, last_error_code = NULL, last_error_at = NULL,
                        archived_at = NULL, enabled = TRUE, updated_at = UTC_TIMESTAMP(6)
                    WHERE id = UUID_TO_BIN(@id);
                    """, cancellationToken, ("@key", key), ("@sha", sha), ("@hint", CloroKeyFormat.Hint(key)), ("@id", row[0]));
                await InsertEventAsync(connection, transaction, id, null, "admin_changed", "KEY_REPLACED", null, cancellationToken);
            }
        }
        catch (MySqlException exception) when (exception.Number == DuplicateEntry) { throw Duplicate(); }
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<CloroKeyView?> ReadViewAsync(MySqlConnection connection, MySqlTransaction? transaction, Guid id,
        CancellationToken cancellationToken)
    {
        var row = await SingleAsync(connection, transaction, ViewProjection + " WHERE k.id = UUID_TO_BIN(@id);",
            cancellationToken, ("@threshold", Threshold), ("@id", id.ToString()));
        return row is null ? null : View(row);
    }

    private static async Task<int> InFlightAsync(MySqlConnection connection, MySqlTransaction transaction, Guid id,
        CancellationToken cancellationToken)
    {
        var row = await SingleAsync(connection, transaction,
            $"SELECT {InFlightCount("k")} FROM cloro_api_keys k WHERE k.id = UUID_TO_BIN(@id);",
            cancellationToken, ("@id", id.ToString()));
        return row is null ? 0 : Int(row[0]);
    }

    private static Task<int> InsertEventAsync(MySqlConnection connection, MySqlTransaction transaction, Guid keyId,
        Guid? providerTaskId, string eventType, string? code, string? externalTaskId, CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, """
            INSERT INTO cloro_key_events (id, key_id, provider_task_id, event_type, error_code, external_task_id,
                credits_remaining, created_at)
            SELECT UUID_TO_BIN(@event), id, UUID_TO_BIN(@task), @type, @code, @external, credits_remaining, UTC_TIMESTAMP(6)
            FROM cloro_api_keys WHERE id = UUID_TO_BIN(@key);
            """, cancellationToken, ("@event", Guid.NewGuid().ToString()), ("@task", providerTaskId?.ToString()),
            ("@type", eventType), ("@code", code), ("@external", externalTaskId), ("@key", keyId.ToString()));

    private static CloroKeyLease Lease(string[] row) => new(Guid.Parse(row[0]), row[1], row[2], row[3]);

    private static CloroKeyView View(string[] row) => new(Guid.Parse(row[0]), row[1], row[2], Int(row[3]), Bool(row[4]),
        CloroKeyStatusRules.Parse(row[5]), row[6] == "1", NullableDecimal(row[7]), NullableDecimal(row[8]), NullableUtc(row[9]),
        NullableUtc(row[10]), NullableUtc(row[11]), NullableUtc(row[12]), Null(row[13]), NullableUtc(row[14]), Int(row[15]),
        NullableUtc(row[16]), Utc(row[17]), Utc(row[18]));

    // Stored codes are already stable identifiers; bound them defensively so no free text is persisted.
    private static string SafeCode(string code) =>
        code is { Length: > 0 and <= 64 } && code.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_')
            ? code : "UNKNOWN_ERROR";

    internal static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static CollectorStoreException Duplicate() => new("CLORO_KEY_DUPLICATE", "账号名称或 API Key 已存在。", 409);
    private static int Int(string value) => int.Parse(value, CultureInfo.InvariantCulture);
    private static bool Bool(string value) => value == "1" || bool.TryParse(value, out var result) && result;
    private static string? Null(string value) => string.IsNullOrEmpty(value) ? null : value;
    private static decimal? NullableDecimal(string value) =>
        string.IsNullOrEmpty(value) ? null : decimal.Parse(value, CultureInfo.InvariantCulture);
    private static DateTimeOffset Utc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    private static DateTimeOffset? NullableUtc(string value) => string.IsNullOrEmpty(value) ? null : Utc(value);

    private static async Task<int> ExecuteAsync(MySqlConnection c, MySqlTransaction? tx, string sql, CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = new MySqlCommand(sql, c, tx);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<List<string[]>> RowsAsync(MySqlConnection c, MySqlTransaction? tx, string sql, CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = new MySqlCommand(sql, c, tx);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<string[]>();
        while (await reader.ReadAsync(ct))
        {
            var values = new string[reader.FieldCount];
            for (var i = 0; i < values.Length; i++)
                values[i] = reader.IsDBNull(i) ? "" : reader.GetValue(i) is DateTime date
                    ? DateTime.SpecifyKind(date, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture)
                    : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? "";
            rows.Add(values);
        }
        return rows;
    }

    private static async Task<string[]?> SingleAsync(MySqlConnection c, MySqlTransaction? tx, string sql, CancellationToken ct,
        params (string Name, object? Value)[] parameters) => (await RowsAsync(c, tx, sql, ct, parameters)).FirstOrDefault();
}
