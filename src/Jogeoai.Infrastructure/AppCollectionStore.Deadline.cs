using System.Data;
using System.Globalization;
using Jogeoai.Domain;
using MySqlConnector;
using static Jogeoai.Infrastructure.DomesticCollectorStore;

namespace Jogeoai.Infrastructure;

/// <summary>截止处理（design 4.3）：周期结束仍未成功的 App 检测置为未采集。</summary>
public sealed partial class AppCollectionStore
{
    /// <summary>Tasks handled per transaction.</summary>
    internal const int DeadlineBatchSize = 50;

    // One worker pass handles at most this many batches; the rest waits for the next pass (30 s later).
    private const int MaxDeadlineBatches = 20;

    /// <inheritdoc />
    public async Task<int> ExpireOverdueTasksAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        // Before 049 there is neither a deadline column nor an App task.
        if (!await AppChannelSchema.IsMigratedAsync(connection, null, cancellationToken)) return 0;
        var expired = 0;
        for (var batch = 0; batch < MaxDeadlineBatches; batch++)
        {
            var handled = await ExpireBatchAsync(connection, cancellationToken);
            expired += handled;
            if (handled < DeadlineBatchSize) break;
        }
        return expired;
    }

    // One batch in one READ COMMITTED transaction. Lock order: task → the tenant's credit account (the reservation is
    // released) → check (and the brand's analysis state, inside AnalysisEnqueue) → run, runs in id order.
    private static async Task<int> ExpireBatchAsync(MySqlConnection c, CancellationToken ct)
    {
        await using var t = await c.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        // The App claim index starts with the channel, so only App tasks are read and locked; a web task is never touched.
        // A task whose lease has not expired is left for a later pass, as is a task another worker is handling (SKIP LOCKED).
        // The check must still be open: an overdue task is handled exactly once.
        var tasks = await QueryRowsAsync(c, t, $"""
            SELECT BIN_TO_UUID(task.id), BIN_TO_UUID(task.tenant_id), BIN_TO_UUID(task.run_id), BIN_TO_UUID(task.check_id),
                   task.failure_count, task.last_block_reason, task.last_error_code
            FROM collector_tasks task FORCE INDEX (ix_collector_tasks_app_claim)
            WHERE task.channel = '{AppChannelDefinition.CollectorChannel}'
              AND task.deadline_at <= UTC_TIMESTAMP(6)
              AND (task.status IN ('pending', 'retryable_failed', 'terminal_failed')
                   OR (task.status IN ('leased', 'collecting') AND task.lease_until <= UTC_TIMESTAMP(6)))
              AND EXISTS (SELECT 1 FROM visibility_checks c WHERE c.id = task.check_id AND c.status IN ('pending', 'running'))
            ORDER BY task.deadline_at, task.id
            LIMIT @limit
            FOR UPDATE OF task SKIP LOCKED;
            """, ct, ("@limit", DeadlineBatchSize));
        var cancelled = 0;
        var runs = new SortedSet<string>(StringComparer.Ordinal);
        // Tenant by tenant: each task locks its tenant's credit account, so two passes take the accounts in the same order.
        foreach (var task in tasks.OrderBy(task => task[1], StringComparer.Ordinal))
        {
            var code = AppNotCollectedRules.Resolve(int.Parse(task[4], CultureInfo.InvariantCulture),
                task[5].Length == 0 ? null : task[5], task[6].Length == 0 ? null : task[6]);
            if (!await MarkNotCollectedAsync(c, t, Guid.Parse(task[0]), Guid.Parse(task[1]), Guid.Parse(task[3]), code, ct)) continue;
            runs.Add(task[2]);
            cancelled++;
        }
        foreach (var run in runs) await UpdateRunStatusAsync(c, t, Guid.Parse(run), ct);
        await t.CommitAsync(ct);
        return cancelled;
    }

    /// <summary>
    /// Makes one App task terminal and its still-open check "not collected" (cancelled with <paramref name="code"/>), and queues
    /// the check for analysis in the same transaction. Shared by the deadline pass and by a failure report that closes the
    /// task (a verification screen, an emulator rejection, a rate limit). The caller holds the task lock and updates the run
    /// afterwards. False when the check was no longer open.
    /// </summary>
    internal static async Task<bool> MarkNotCollectedAsync(MySqlConnection c, MySqlTransaction t, Guid taskId, Guid tenantId, Guid checkId,
        string code, CancellationToken ct)
    {
        // implement 6.4: a terminal task gives back the credits reserved for its check, in this transaction. The tenant's
        // account row is locked first (before the check and the analysis state), whether or not there is anything to release.
        await AppCreditReservations.ReleaseAsync(c, t, tenantId, checkId, AppNotCollectedRules.Message(code), ct);
        // The last error code and block reason stay on the task: they are the evidence for the reason code.
        await ExecuteAsync(c, t, """
            UPDATE collector_tasks
            SET status = 'terminal_failed', lease_owner = NULL, lease_until = NULL,
                completed_at = COALESCE(completed_at, UTC_TIMESTAMP(6)), updated_at = UTC_TIMESTAMP(6)
            WHERE id = UUID_TO_BIN(@id);
            """, ct, ("@id", taskId.ToString()));
        // captured_at is the moment the check was determined not collected (implement 7 follow-up A): without it the check
        // would fall outside every time window (so it could not be listed by status) and would count as "missing capture time"
        // in every analytics view of the brand, whatever its source. Written before the analysis queue, so the arrival row
        // carries the same time.
        var changed = await ExecuteAsync(c, t, """
            UPDATE visibility_checks
            SET status = 'cancelled', error_code = @code, error_message = @message,
                captured_at = COALESCE(captured_at, UTC_TIMESTAMP(6)), updated_at = UTC_TIMESTAMP(6)
            WHERE id = UUID_TO_BIN(@check) AND tenant_id = UUID_TO_BIN(@tenant) AND status IN ('pending', 'running');
            """, ct, ("@code", code), ("@message", AppNotCollectedRules.Message(code)), ("@check", checkId.ToString()), ("@tenant", tenantId.ToString()));
        if (changed == 0) return false;
        // Same transaction (design 4.3): without the arrival row the answer detail would report "needs refresh" forever.
        await AnalysisEnqueue.QueueAsync(c, t, checkId, ct);
        return true;
    }
}
