using Jogeoai.Domain;
using MySqlConnector;
using static Jogeoai.Infrastructure.DomesticCollectorStore;

namespace Jogeoai.Infrastructure;

public sealed partial class AppCollectorPoolStore
{
    /// <inheritdoc />
    public Task<string> ReportSentAsync(AppSentCommand command, CancellationToken cancellationToken = default)
        => InTransactionAsync(async (c, t, ct) =>
        {
            var binding = await LockBindingAsync(c, t, command.NodeId, command.BindingId, wholeDevice: false, ct);
            if (binding is null) return Reject<string>(await ForbidBindingAsync(c, t, command.NodeId, command.BindingId, "sent", ct));
            var live = await QuerySingleAsync(c, t, """
                SELECT 1 FROM collector_tasks WHERE id = UUID_TO_BIN(@task) AND lease_version = @version
                AND lease_owner = @node AND lease_app_binding_id = UUID_TO_BIN(@binding) AND status IN ('leased','collecting')
                AND lease_until > UTC_TIMESTAMP(6) FOR UPDATE;
                """, ct, ("@task", command.TaskId.ToString()), ("@version", command.LeaseVersion), ("@node", command.NodeId),
                ("@binding", command.BindingId.ToString()));
            var disposition = await RecordSendAsync(c, t, command, live is not null, ct);
            if (disposition == "stale")
                await InsertEventAsync(c, t, new AppEvent("sent_stale", NodeActor(command.NodeId), command.NodeId, binding.Id,
                    binding.PlatformCode, command.TaskId, new { command.LeaseVersion }), ct);
            return Done(disposition);
        }, cancellationToken);

    // The caller has locked the binding and task; all device leases lock every binding of the device.
    private static async Task<string> RecordSendAsync(MySqlConnection c, MySqlTransaction t, AppSentCommand command, bool live, CancellationToken ct)
    {
        object[] args = [("@task", command.TaskId.ToString()), ("@version", command.LeaseVersion), ("@node", command.NodeId),
            ("@binding", command.BindingId.ToString()), ("@sent", command.SentAt.UtcDateTime)];
        const string owner = "task_id = UUID_TO_BIN(@task) AND lease_version = @version AND binding_id = UUID_TO_BIN(@binding) AND node_id = @node";
        var row = await QuerySingleAsync(c, t, $"""
            SELECT sent_at IS NOT NULL, sent_at = @sent,
                   @sent BETWEEN leased_at - INTERVAL 1000 MICROSECOND AND LEAST(lease_until, UTC_TIMESTAMP(6) + INTERVAL 1 SECOND), send_state
            FROM collector_app_device_attempts WHERE {owner} FOR UPDATE;
            """, ct, args);
        if (row is null) return "stale";
        if (Flag(row[0]) && Flag(row[1])) return "duplicate";
        if (row[3] == "not_sent" || Flag(row[0]))
            throw new CollectorStoreException("INVALID_SENT_AT", "发送报告与已有回执冲突，请人工核对。", 409);
        if (!live) return "stale";
        if (!Flag(row[2]))
            throw new CollectorStoreException("INVALID_SENT_AT", "发送时刻不属于该租约或与已登记的事实不符。", 409);
        await ExecuteAsync(c, t, $"UPDATE collector_app_device_attempts SET sent_at = @sent, send_state = 'sent' WHERE {owner};", ct, args);
        return "accepted";
    }
}
