using Jogeoai.Application;
using Jogeoai.Domain;
using static Jogeoai.Infrastructure.AppAdminSql;
using static Jogeoai.Infrastructure.DomesticCollectorStore;

namespace Jogeoai.Infrastructure;

/// <summary>
/// App 渠道当前的告警信息（results.md 8.4，另加连续发题前失败的绑定与待批准的绑定）。运营接口的告警视图和 Worker 的每日汇报都调用这里，所以两者同源。
/// 只读、不加锁。先从数据库取一次 UTC_TIMESTAMP(6) 作为数据截止时刻，之后的失联、停止时长和“过去 24 小时”都按它计算。
/// 只读节点、绑定、平台、入口、采集任务、领取记录、设备尝试、审计事件和容量：不读提问、租户名称或品牌名称。
/// 这里只报告，不停用绑定、不暂停平台、不改任何状态。
/// </summary>
/// <param name="database">业务数据库。</param>
/// <param name="capacity">各平台的容量视图（与运营接口的容量视图同一个查询）。</param>
public sealed class AppAlertQuery(MySqlDatabase database, IAppCapacityQuery capacity) : IAppAlertQuery
{
    private const string AppChannel = AppChannelDefinition.CollectorChannel;

    /// <inheritdoc />
    public async Task<AppAlertsView> GetAlertsAsync(CancellationToken cancellationToken = default)
    {
        var ct = cancellationToken;
        await using var c = database.CreateConnection();
        await c.OpenAsync(ct);
        await AppChannelSchema.RequireAsync(c, null, ct);
        var clock = (await QuerySingleAsync(c, null, $"""
            SELECT DATE_FORMAT(x.now, '%Y-%m-%d %H:%i:%s.%f'), {Iso("x.now")} FROM (SELECT UTC_TIMESTAMP(6) AS now) x;
            """, ct))!;
        var now = ("@now", (object)clock[0]);
        const string Now = "CAST(@now AS DATETIME(6))";

        // 1. Active nodes not seen for 15 minutes (or never seen since they were registered).
        var offline = (await QueryRowsAsync(c, null, $"""
                SELECT n.node_id, {Iso(NodeLastActivity)}, GREATEST(0, TIMESTAMPDIFF(SECOND, COALESCE({NodeLastActivity}, n.created_at), {Now}))
                FROM collector_app_nodes n
                {NodeBindingsJoin}
                WHERE n.status = 'active'
                  AND ({NodeLastActivity} IS NULL OR {NodeLastActivity} <= {Now} - INTERVAL {AppDailyReportRules.NodeOfflineMinutes} MINUTE)
                ORDER BY n.node_id;
                """, ct, now))
            .Select(row => new AppOfflineNodeAlert(row[0], ParseIsoOrNull(row[1]), Long(row[2]))).ToArray();

        // 2. Stopped bindings with their reason and how long they have been stopped. A binding waiting for the operator's
        // approval (migration 052) is stored as stopped / disabled_by_operator; it is listed in its own category (8) and
        // not here, so it is not counted twice. Before 052 there is no approval and every stopped binding is listed.
        var approval = await AppChannelSchema.HasBindingApprovalAsync(c, null, ct);
        var stopped = (await QueryRowsAsync(c, null, $"""
                SELECT BIN_TO_UUID(id), node_id, device_id, platform_code, account_label, stop_reason, {Iso("stopped_at")},
                       GREATEST(0, TIMESTAMPDIFF(SECOND, stopped_at, {Now}))
                FROM collector_app_bindings WHERE status = 'stopped'{(approval ? " AND approved_at IS NOT NULL" : "")}
                ORDER BY platform_code, node_id, device_id;
                """, ct, now))
            .Select(row => AppContractNames.TryParse<AppBindingStopReason>(row[5], out var reason)
                ? new AppStoppedBindingAlert(Guid.Parse(row[0]), row[1], row[2], row[3], row[4], reason, ParseIso(row[6]), Long(row[7]))
                : null)
            .OfType<AppStoppedBindingAlert>()
            .OrderBy(alert => PlatformOrder(alert.PlatformCode)).ThenBy(alert => alert.NodeId, StringComparer.Ordinal)
            .ThenBy(alert => alert.DeviceId, StringComparer.Ordinal).ToArray();

        // 3. Paused platforms, and platforms whose App entry was found unavailable. The seeded "content cannot be read" entry
        // (ernie) is a design decision, not a finding, and would otherwise be reported every day.
        var states = (await QueryRowsAsync(c, null, $"""
                SELECT platform_code, pause_reason, {Iso("paused_at")} FROM collector_app_platform_states WHERE paused_at IS NOT NULL;
                """, ct)).ToDictionary(row => row[0], StringComparer.Ordinal);
        var entries = (await QueryRowsAsync(c, null, $"""
                SELECT platform_code, enabled, COALESCE(unavailable_reason, '') FROM collector_platform_entries
                WHERE channel = '{AppChannel}' AND region = 'cn' ORDER BY platform_code, version DESC;
                """, ct)).GroupBy(row => row[0], StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var platforms = new List<AppPlatformAlert>();
        foreach (var engine in AppChannelDefinition.Engines)
        {
            var (available, reason) = AppCollectorPoolStore.EntryState(entries.GetValueOrDefault(engine, []));
            var unavailable = !available && reason != AppUnavailableReasons.ContentUnreadable;
            var paused = states.TryGetValue(engine, out var state);
            if (paused || unavailable)
                platforms.Add(new AppPlatformAlert(engine, paused, paused ? Text(state![1]) : null, paused ? ParseIsoOrNull(state![2]) : null,
                    !available, available ? null : reason));
        }

        // 4. Tasks determined "not collected" in the last 24 hours, by platform and reason.
        var codes = AppNotCollectedCodes.All;
        var notCollected = (await QueryRowsAsync(c, null, $"""
                SELECT task.platform_code, chk.error_code, COUNT(*)
                FROM collector_tasks task
                JOIN visibility_checks chk ON chk.id = task.check_id
                WHERE task.channel = '{AppChannel}' AND task.status = 'terminal_failed'
                  AND task.completed_at > {Now} - INTERVAL 24 HOUR AND task.completed_at <= {Now}
                  AND chk.status = 'cancelled' AND chk.error_code IN ({string.Join(", ", codes.Select((_, i) => $"@code{i}"))})
                GROUP BY task.platform_code, chk.error_code;
                """, ct, [now, .. codes.Select((code, i) => (object)($"@code{i}", code))]))
            .Select(row => new AppNotCollectedCount(row[0], row[1], Long(row[2])))
            .OrderBy(item => PlatformOrder(item.PlatformCode)).ThenBy(item => IndexOf(codes, item.ReasonCode)).ToArray();

        // 5. Leases, accepted results and failure reports of the last 24 hours (App rows are found through the bindings),
        // plus the expired leases counted as a failure (recorded as audit events, not as attempts).
        var attempts = (await QueryRowsAsync(c, null, $"""
                SELECT b.platform_code, a.event_kind, COUNT(*)
                FROM collector_app_bindings b
                JOIN collector_attempts a ON a.app_binding_id = b.id AND a.event_kind IN ('leased', 'accepted', 'failed')
                    AND a.created_at > {Now} - INTERVAL 24 HOUR AND a.created_at <= {Now}
                GROUP BY b.platform_code, a.event_kind;
                """, ct, now)).ToDictionary(row => (row[0], row[1]), row => Long(row[2]));
        var expired = (await QueryRowsAsync(c, null, $"""
                SELECT platform_code, COUNT(*) FROM collector_app_events
                WHERE event_kind = 'lease_expired' AND occurred_at > {Now} - INTERVAL 24 HOUR AND occurred_at <= {Now} AND platform_code IS NOT NULL
                GROUP BY platform_code;
                """, ct, now)).ToDictionary(row => row[0], row => Long(row[1]), StringComparer.Ordinal);
        var activity = AppChannelDefinition.Engines.Select(engine => new AppPlatformActivity(engine,
            attempts.GetValueOrDefault((engine, "leased")), attempts.GetValueOrDefault((engine, "accepted")),
            attempts.GetValueOrDefault((engine, "failed")), expired.GetValueOrDefault(engine))).ToArray();

        // 6. This week's capacity use, from the same capacity view as the operator endpoint.
        var usage = (await capacity.GetPlatformCapacitiesAsync(ct))
            .Select(item => new AppCapacityUsage(item, AppDailyReportRules.DemandPercent(item))).ToArray();

        // 7. Bindings whose most recent attempts all failed before the question was sent (an App whose screen changed keeps
        // leasing and failing; there is no breaker, so this is where it shows). Among the attempts leased in the last 24
        // hours only those with a verdict are looked at: a failure report, a send, or a lease that ran out. An attempt still
        // in flight and not yet sent is skipped, so a binding does not drop off the list each time it takes a task.
        // The attempts table arrives with migration 051; before it there is nothing to report.
        var preSend = !await AppChannelSchema.HasDeviceDispatchAsync(c, null, ct) ? [] : (await QueryRowsAsync(c, null, $"""
                SELECT BIN_TO_UUID(b.id), b.node_id, b.device_id, b.platform_code, b.account_label,
                       MAX(CASE WHEN recent.position = 1 THEN recent.failure_code END), {Iso("MAX(recent.failed_at)")}
                FROM (SELECT binding_id, send_state, failure_code, failed_at,
                             ROW_NUMBER() OVER (PARTITION BY binding_id ORDER BY leased_at DESC, task_id, lease_version DESC) AS position
                      FROM collector_app_device_attempts
                      WHERE leased_at > {Now} - INTERVAL 24 HOUR AND leased_at <= {Now}
                        AND (failed_at IS NOT NULL OR sent_at IS NOT NULL OR lease_until <= {Now})) recent
                JOIN collector_app_bindings b ON b.id = recent.binding_id
                WHERE recent.position <= {AppDailyReportRules.PreSendFailureStreak}
                GROUP BY b.id, b.node_id, b.device_id, b.platform_code, b.account_label
                HAVING COUNT(*) = {AppDailyReportRules.PreSendFailureStreak}
                   AND COALESCE(SUM(recent.send_state = 'not_sent' AND recent.failed_at IS NOT NULL), 0) = {AppDailyReportRules.PreSendFailureStreak}
                ORDER BY b.platform_code, b.node_id, b.device_id;
                """, ct, now))
            .Select(row => new AppPreSendFailureAlert(Guid.Parse(row[0]), row[1], row[2], row[3], row[4], Text(row[5]), ParseIso(row[6])))
            .OrderBy(alert => PlatformOrder(alert.PlatformCode)).ThenBy(alert => alert.NodeId, StringComparer.Ordinal)
            .ThenBy(alert => alert.DeviceId, StringComparer.Ordinal).ToArray();

        // 8. Bindings waiting for the operator's approval, by node. Their number is bounded by each node's device cap times
        // the number of platforms; an unknown one may mean a leaked node token.
        var pending = !approval ? [] : (await QueryRowsAsync(c, null, $"""
                SELECT BIN_TO_UUID(id), node_id, device_id, platform_code, account_label, {Iso("created_at")}
                FROM collector_app_bindings WHERE approved_at IS NULL
                ORDER BY node_id, device_id, platform_code;
                """, ct))
            .Select(row => new AppPendingApprovalBindingAlert(Guid.Parse(row[0]), row[1], row[2], row[3], row[4], ParseIso(row[5])))
            .OrderBy(alert => alert.NodeId, StringComparer.Ordinal).ThenBy(alert => alert.DeviceId, StringComparer.Ordinal)
            .ThenBy(alert => PlatformOrder(alert.PlatformCode)).ToArray();

        return new AppAlertsView(ParseIso(clock[1]), AppDailyReportRules.HasAlerts(offline, stopped, platforms, notCollected, usage, preSend, pending),
            offline, stopped, platforms, notCollected, activity, usage) { PreSendFailures = preSend, PendingApprovalBindings = pending };
    }

    private static int IndexOf(IReadOnlyList<string> values, string value)
    {
        for (var i = 0; i < values.Count; i++)
            if (string.Equals(values[i], value, StringComparison.Ordinal)) return i;
        return int.MaxValue;
    }
}
