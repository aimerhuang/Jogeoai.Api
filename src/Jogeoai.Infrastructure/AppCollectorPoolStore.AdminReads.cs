using System.Globalization;
using System.Text;
using System.Text.Json;
using Jogeoai.Domain;
using MySqlConnector;
using static Jogeoai.Infrastructure.DomesticCollectorStore;

namespace Jogeoai.Infrastructure;

/// <summary>运营的读取（implement 8.3）：节点、绑定、平台、队列、审计事件。只读，不加锁，时间取数据库的 UTC_TIMESTAMP(6)。</summary>
public sealed partial class AppCollectorPoolStore
{
    private async Task<MySqlConnection> OpenMigratedAsync(CancellationToken ct)
    {
        var connection = database.CreateConnection();
        try
        {
            await connection.OpenAsync(ct);
            await AppChannelSchema.RequireAsync(connection, null, ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static string NodeSelect(string where) => $"""
        SELECT n.node_id, n.status, {Iso("n.last_seen_at")}, {Iso(AppAdminSql.NodeLastActivity)},
               n.status = 'active' AND ({AppAdminSql.NodeLastActivity} IS NULL
                   OR {AppAdminSql.NodeLastActivity} <= UTC_TIMESTAMP(6) - INTERVAL {AppDailyReportRules.NodeOfflineMinutes} MINUTE),
               n.collector_build_revision, COALESCE(b.total, 0), COALESCE(b.active, 0), {Iso("n.created_at")}, {Iso("n.updated_at")}
        FROM collector_app_nodes n
        {AppAdminSql.NodeBindingsJoin}
        {where}
        ORDER BY n.node_id;
        """;

    private static AppNodeView NodeView(string[] row)
        => new(row[0], row[1] == "active" ? AppNodeStatus.Active : AppNodeStatus.Disabled, ParseIsoOrNull(row[2]), ParseIsoOrNull(row[3]),
            Flag(row[4]), AppAdminSql.Text(row[5]), Int(row[6]), Int(row[7]), ParseIso(row[8]), ParseIso(row[9]));

    /// <inheritdoc />
    public async Task<IReadOnlyList<AppNodeView>> ListNodesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenMigratedAsync(cancellationToken);
        return (await QueryRowsAsync(connection, null, NodeSelect(""), cancellationToken)).Select(NodeView).ToArray();
    }

    /// <inheritdoc />
    public async Task<AppNodeView> GetNodeAsync(string nodeId, CancellationToken cancellationToken = default)
    {
        nodeId = RequireNodeId(nodeId);
        await using var connection = await OpenMigratedAsync(cancellationToken);
        var row = await QuerySingleAsync(connection, null, NodeSelect("WHERE n.node_id = @node"), cancellationToken, ("@node", nodeId))
            ?? throw new CollectorStoreException("APP_NODE_NOT_FOUND", "采集节点不存在。", 404);
        return NodeView(row);
    }

    private static string BindingSelect(string where) => $"""
        SELECT BIN_TO_UUID(id), node_id, device_id, platform_code, account_label, app_package, reviewed_app_version, installed_app_version,
               android_version, status, stop_reason, {Iso("stopped_at")},
               CASE WHEN stopped_at IS NULL THEN NULL ELSE GREATEST(0, TIMESTAMPDIFF(SECOND, stopped_at, UTC_TIMESTAMP(6))) END,
               {Iso("NULLIF(GREATEST(UTC_TIMESTAMP(6), IFNULL(cooldown_until, UTC_TIMESTAMP(6)), IFNULL(fence.until_at, UTC_TIMESTAMP(6))), UTC_TIMESTAMP(6))")},
               last_seen_at IS NULL OR last_seen_at <= UTC_TIMESTAMP(6) - INTERVAL {AppPoolRules.BindingOfflineMinutes} MINUTE,
               {Iso("last_seen_at")}, {Iso("last_leased_at")}, {Iso("last_success_at")}, {AppPoolRules.DeviceCooldownSeconds}, NULL,
               {Iso("created_at")}, {Iso("updated_at")}
        FROM collector_app_bindings
        LEFT JOIN (SELECT node_id AS fence_node, device_id AS fence_device, MAX({LastPossibleSend}) + INTERVAL {AppPoolRules.DeviceCooldownSeconds} SECOND AS until_at
                   FROM collector_app_device_attempts
                   WHERE lease_until > UTC_TIMESTAMP(6) - INTERVAL {AppPoolRules.DeviceCooldownSeconds} SECOND
                   GROUP BY node_id, device_id) fence
          ON fence.fence_node = node_id AND fence.fence_device = device_id
        {where}
        ORDER BY platform_code, node_id, device_id;
        """;

    private static AppBindingAdminView BindingView(string[] row)
        => new(Guid.Parse(row[0]), row[1], row[2], row[3], row[4], row[5], AppAdminSql.Text(row[6]), AppAdminSql.Text(row[7]),
            AppAdminSql.Text(row[8]), row[9] == "active" ? AppBindingStatus.Active : AppBindingStatus.Stopped,
            AppContractNames.TryParse<AppBindingStopReason>(row[10], out var reason) ? reason : null, ParseIsoOrNull(row[11]),
            row[12].Length == 0 ? null : AppAdminSql.Long(row[12]), ParseIsoOrNull(row[13]), Flag(row[14]), ParseIsoOrNull(row[15]),
            ParseIsoOrNull(row[16]), ParseIsoOrNull(row[17]), Int(row[18]), AppAdminSql.IntOrNull(row[19]), ParseIso(row[20]), ParseIso(row[21]));

    /// <inheritdoc />
    public async Task<IReadOnlyList<AppBindingAdminView>> ListBindingsAsync(string? nodeId, string? platformCode, AppBindingStatus? status,
        CancellationToken cancellationToken = default)
    {
        var conditions = new List<string>();
        var parameters = new List<object>();
        if (!string.IsNullOrWhiteSpace(nodeId))
        {
            conditions.Add("node_id = @node");
            parameters.Add(("@node", RequireNodeId(nodeId)));
        }
        if (!string.IsNullOrWhiteSpace(platformCode))
        {
            conditions.Add("platform_code = @platform");
            parameters.Add(("@platform", AppAdminSql.RequirePlatform(platformCode)));
        }
        if (status is { } value)
        {
            conditions.Add("status = @status");
            parameters.Add(("@status", AppContractNames.ToStorage(value)));
        }
        await using var connection = await OpenMigratedAsync(cancellationToken);
        await AppChannelSchema.RequireDeviceDispatchAsync(connection, null, cancellationToken);
        var where = conditions.Count == 0 ? "" : "WHERE " + string.Join(" AND ", conditions);
        return (await QueryRowsAsync(connection, null, BindingSelect(where), cancellationToken, [.. parameters])).Select(BindingView).ToArray();
    }

    /// <inheritdoc />
    public async Task<AppBindingAdminView> GetBindingAsync(Guid bindingId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenMigratedAsync(cancellationToken);
        await AppChannelSchema.RequireDeviceDispatchAsync(connection, null, cancellationToken);
        var row = await QuerySingleAsync(connection, null, BindingSelect("WHERE id = UUID_TO_BIN(@id)"), cancellationToken,
                ("@id", bindingId.ToString()))
            ?? throw new CollectorStoreException("APP_BINDING_NOT_FOUND", "绑定不存在。", 404);
        return BindingView(row);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AppPlatformStateView>> ListPlatformsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenMigratedAsync(cancellationToken);
        var states = (await QueryRowsAsync(connection, null, $"""
                SELECT platform_code, 0, NULL, {Iso("last_dispatched_at")}, paused_at IS NOT NULL, {Iso("paused_at")}, pause_reason
                FROM collector_app_platform_states;
                """, cancellationToken))
            .ToDictionary(row => row[0], StringComparer.Ordinal);
        var entries = (await QueryRowsAsync(connection, null, $"""
                SELECT platform_code, enabled, COALESCE(unavailable_reason, '') FROM collector_platform_entries
                WHERE channel = '{AppChannel}' AND region = 'cn' ORDER BY platform_code, version DESC;
                """, cancellationToken))
            .GroupBy(row => row[0], StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var result = new List<AppPlatformStateView>();
        foreach (var platform in AppChannelDefinition.Platforms)
        {
            var (available, reason) = EntryState(entries.GetValueOrDefault(platform.Code, []));
            // A missing gate row reads as the column defaults (the lease path creates it the same way).
            if (states.TryGetValue(platform.Code, out var state))
                result.Add(new AppPlatformStateView(platform.Code, platform.DisplayName, Int(state[1]), AppAdminSql.IntOrNull(state[2]),
                    ParseIsoOrNull(state[3]), Flag(state[4]), ParseIsoOrNull(state[5]), AppAdminSql.Text(state[6]), available, reason,
                    !available && reason == AppUnavailableReasons.EmulatorRejected));
            else
                result.Add(new AppPlatformStateView(platform.Code, platform.DisplayName, 0, null, null, false, null, null, available, reason,
                    !available && reason == AppUnavailableReasons.EmulatorRejected));
        }
        return result;
    }

    /// <summary>
    /// 入口是否可用（任一 consumer_app 入口启用，与领取路径的判断相同）；不可用时的原因取版本最高的那条停用入口的原因。
    /// 行按版本倒序：platform_code | enabled | unavailable_reason。
    /// </summary>
    internal static (bool Available, string? Reason) EntryState(IReadOnlyList<string[]> entries)
    {
        if (entries.Any(entry => Flag(entry[1]))) return (true, null);
        return (false, entries.Select(entry => AppAdminSql.Text(entry[2])).FirstOrDefault());
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AppPlatformQueueView>> GetQueueAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenMigratedAsync(cancellationToken);
        // Waiting: what a lease could still return (backoff included, any tenant). In progress: an unexpired lease, whatever the deadline.
        var rows = (await QueryRowsAsync(connection, null, $"""
                SELECT platform_code,
                       COALESCE(SUM(deadline_at > UTC_TIMESTAMP(6) AND {LeasableStatus}), 0),
                       COALESCE(SUM(status IN ('leased', 'collecting') AND lease_until > UTC_TIMESTAMP(6)), 0)
                FROM collector_tasks
                WHERE channel = '{AppChannel}' AND status IN ('pending', 'retryable_failed', 'leased', 'collecting')
                GROUP BY platform_code;
                """, cancellationToken))
            .ToDictionary(row => row[0], StringComparer.Ordinal);
        return AppChannelDefinition.Engines.Select(engine => rows.TryGetValue(engine, out var row)
            ? new AppPlatformQueueView(engine, AppAdminSql.Long(row[1]), AppAdminSql.Long(row[2]))
            : new AppPlatformQueueView(engine, 0, 0)).ToArray();
    }

    /// <inheritdoc />
    public async Task<AppTaskAttemptsView> GetTaskAttemptsAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenMigratedAsync(cancellationToken);
        await AppChannelSchema.RequireDeviceDispatchAsync(connection, null, cancellationToken);
        var id = ("@id", (object)taskId.ToString());
        // The check gives the reason of a task that was not collected; neither the question nor the answer is read.
        var task = await QuerySingleAsync(connection, null, $"""
            SELECT task.platform_code, task.status, task.lease_version, task.last_error_code, {Iso("task.deadline_at")},
                   {Iso("task.completed_at")}, chk.status, chk.error_code
            FROM collector_tasks task
            LEFT JOIN visibility_checks chk ON chk.id = task.check_id
            WHERE task.id = UUID_TO_BIN(@id) AND task.channel = '{AppChannel}';
            """, cancellationToken, id);
        // A web task is not an App task: this route does not say whether it exists.
        if (task is null) throw new CollectorStoreException("APP_TASK_NOT_FOUND", "App 采集任务不存在。", 404);
        // One row per lease. The accepted result is found through its submission, which carries the lease version it was
        // submitted under; a late or repeated submission of another lease is stored as stale or duplicate, never accepted.
        var attempts = (await QueryRowsAsync(connection, null, $"""
                SELECT a.lease_version, a.node_id, a.device_id, BIN_TO_UUID(a.binding_id), COALESCE(b.platform_code, @platform), b.account_label,
                       {Iso("a.leased_at")}, {Iso("a.lease_until")}, a.send_state, {Iso("a.sent_at")}, a.failure_code, {Iso("a.failed_at")},
                       EXISTS (SELECT 1 FROM collector_result_submissions s
                               WHERE s.collector_task_id = a.task_id AND s.lease_version = a.lease_version AND s.disposition = 'accepted')
                FROM collector_app_device_attempts a
                LEFT JOIN collector_app_bindings b ON b.id = a.binding_id
                WHERE a.task_id = UUID_TO_BIN(@id)
                ORDER BY a.lease_version;
                """, cancellationToken, id, ("@platform", task[0])))
            .Select(row => new AppTaskAttemptView(Int(row[0]), row[1], row[2], Guid.Parse(row[3]), row[4], AppAdminSql.Text(row[5]),
                ParseIso(row[6]), ParseIso(row[7]), AppAdminSql.Text(row[8]), ParseIsoOrNull(row[9]), AppAdminSql.Text(row[10]),
                ParseIsoOrNull(row[11]), Flag(row[12]))).ToArray();
        var outcome = task[1] switch
        {
            "succeeded" => AppTaskAttemptsView.OutcomeCollected,
            "terminal_failed" => AppTaskAttemptsView.OutcomeNotCollected,
            _ => null,
        };
        var reason = outcome == AppTaskAttemptsView.OutcomeNotCollected && task[6] == "cancelled" && AppNotCollectedCodes.IsNotCollected(task[7])
            ? task[7]
            : null;
        return new AppTaskAttemptsView(taskId, task[0], task[1], outcome, reason, Int(task[2]), AppAdminSql.Text(task[3]), ParseIsoOrNull(task[4]),
            ParseIsoOrNull(task[5]), attempts);
    }

    /// <inheritdoc />
    public async Task<CursorPage<AppEventView>> ListEventsAsync(AppEventQuery query, string? cursor, int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        static CollectorStoreException Invalid(string message) => new("INVALID_FILTER", message, 400);
        if (limit is < 1 or > AppAdminRules.MaxEventPageSize) throw Invalid($"limit 必须在 1–{AppAdminRules.MaxEventPageSize} 之间。");
        if (query.From is { } from && query.To is { } to && from >= to) throw Invalid("from 必须早于 to。");
        var conditions = new List<string>();
        var parameters = new List<object>();
        if (query.From is { } start)
        {
            conditions.Add("occurred_at >= @from");
            parameters.Add(("@from", start.UtcDateTime));
        }
        if (query.To is { } end)
        {
            conditions.Add("occurred_at < @to");
            parameters.Add(("@to", end.UtcDateTime));
        }
        if (!string.IsNullOrWhiteSpace(query.NodeId))
        {
            if (query.NodeId.Trim().Length > 128) throw Invalid("nodeId 最多 128 个字符。");
            conditions.Add("node_id = @node");
            parameters.Add(("@node", query.NodeId.Trim()));
        }
        if (query.BindingId is { } binding)
        {
            conditions.Add("binding_id = UUID_TO_BIN(@binding)");
            parameters.Add(("@binding", binding.ToString()));
        }
        if (!string.IsNullOrWhiteSpace(query.PlatformCode))
        {
            var platform = AppChannelDefinition.TryGetPlatform(query.PlatformCode)?.Code ?? throw Invalid("platformCode 不是 App 渠道的平台。");
            conditions.Add("platform_code = @platform");
            parameters.Add(("@platform", platform));
        }
        if (!string.IsNullOrWhiteSpace(query.EventKind))
        {
            if (query.EventKind.Trim().Length > 48) throw Invalid("eventKind 最多 48 个字符。");
            conditions.Add("event_kind = @kind");
            parameters.Add(("@kind", query.EventKind.Trim()));
        }
        if (DecodeEventCursor(cursor) is { } position)
        {
            // Strictly after the last row of the previous page in (occurred_at DESC, id DESC) order.
            conditions.Add("(occurred_at < @cursorAt OR (occurred_at = @cursorAt AND id < UUID_TO_BIN(@cursorId)))");
            parameters.Add(("@cursorAt", position.At));
            parameters.Add(("@cursorId", position.Id.ToString()));
        }
        await using var connection = await OpenMigratedAsync(cancellationToken);
        var rows = await QueryRowsAsync(connection, null, $"""
            SELECT BIN_TO_UUID(id), {Iso("occurred_at")}, event_kind, node_id, BIN_TO_UUID(binding_id), platform_code, BIN_TO_UUID(task_id), actor,
                   CAST(detail AS CHAR), DATE_FORMAT(occurred_at, '%Y-%m-%d %H:%i:%s.%f')
            FROM collector_app_events
            {(conditions.Count == 0 ? "" : "WHERE " + string.Join(" AND ", conditions))}
            ORDER BY occurred_at DESC, id DESC
            LIMIT {limit + 1};
            """, cancellationToken, [.. parameters]);
        var items = rows.Take(limit).Select(row => new AppEventView(Guid.Parse(row[0]), ParseIso(row[1]), row[2], AppAdminSql.Text(row[3]),
            row[4].Length == 0 ? null : Guid.Parse(row[4]), AppAdminSql.Text(row[5]), row[6].Length == 0 ? null : Guid.Parse(row[6]), row[7],
            Detail(row[8]))).ToArray();
        var next = rows.Count > limit ? EncodeEventCursor(rows[limit - 1][9], items[^1].Id) : null;
        return new CursorPage<AppEventView>(items, next);
    }

    private static JsonElement? Detail(string json)
    {
        if (json.Length == 0) return null;
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    // The cursor carries the full microsecond time of the last row (the response time has milliseconds only) and its id.
    private static string EncodeEventCursor(string occurredAt, Guid id)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{occurredAt}|{id}"));

    private static (DateTime At, Guid Id)? DecodeEventCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|', 2);
            if (parts.Length == 2
                && DateTime.TryParseExact(parts[0], "yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
                && Guid.TryParse(parts[1], out var id))
                return (at, id);
        }
        catch (FormatException)
        {
        }
        throw new CollectorStoreException("INVALID_CURSOR", "游标无效。", 400);
    }
}
