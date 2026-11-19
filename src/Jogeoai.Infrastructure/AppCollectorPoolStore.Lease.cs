using Jogeoai.Domain;
using MySqlConnector;
using static Jogeoai.Infrastructure.CollectorSessionDispatch;
using static Jogeoai.Infrastructure.DomesticCollectorStore;

namespace Jogeoai.Infrastructure;

/// <summary>领取与续租（implement 5.4 / 5.5 / 5.6a / 5.7 / 6.0c / 6.2，design 6.2 / 6.4 / 第 7 节）。</summary>
public sealed partial class AppCollectorPoolStore
{
    private const string AppChannel = AppChannelDefinition.CollectorChannel;

    private sealed record BindingRow(Guid Id, string NodeId, string DeviceId, string PlatformCode, string Status, string? StopReason,
        bool CooldownDue, double CooldownRemaining);

    private sealed record PlatformRow(bool Paused);

    private const string BindingColumns = """
        BIN_TO_UUID(id), node_id, device_id, platform_code, status, stop_reason,
        (cooldown_until IS NOT NULL AND cooldown_until <= UTC_TIMESTAMP(6)),
        COALESCE(GREATEST(0, TIMESTAMPDIFF(MICROSECOND, UTC_TIMESTAMP(6), cooldown_until) / 1000000), 0)
        """;

    /// <summary>
    /// 第一把锁：绑定行。绑定不存在或不属于该节点时返回空值。领取时锁住同一台设备上的全部绑定（按唯一索引的顺序），
    /// 这样同一台设备上的两个绑定不会同时通过“设备空闲”的检查；失败上报只锁自己这一行。
    /// </summary>
    private static async Task<BindingRow?> LockBindingAsync(MySqlConnection c, MySqlTransaction t, string nodeId, Guid bindingId,
        bool wholeDevice, CancellationToken ct)
    {
        var owner = await QuerySingleAsync(c, t, "SELECT node_id, device_id FROM collector_app_bindings WHERE id = UUID_TO_BIN(@id);", ct,
            ("@id", bindingId.ToString()));
        if (owner is null || !string.Equals(owner[0], nodeId, StringComparison.Ordinal)) return null;
        var rows = await QueryRowsAsync(c, t, $"""
            SELECT {BindingColumns} FROM collector_app_bindings
            WHERE {(wholeDevice ? "node_id = @node AND device_id = @device" : "id = UUID_TO_BIN(@id)")} FOR UPDATE;
            """, ct, ("@id", bindingId.ToString()), ("@node", owner[0]), ("@device", owner[1]));
        return rows.Select(row => new BindingRow(Guid.Parse(row[0]), row[1], row[2], row[3], row[4], row[5].Length == 0 ? null : row[5],
            Flag(row[6]), ParseDouble(row[7]))).FirstOrDefault(row => row.Id == bindingId);
    }

    /// <summary>绑定不存在、属于别的节点，或者传入的其实是网页账号的标识：写审计事件，返回同一个 403。</summary>
    private static async Task<CollectorStoreException> ForbidBindingAsync(MySqlConnection c, MySqlTransaction t, string nodeId, Guid bindingId,
        string route, CancellationToken ct)
    {
        var owner = await QuerySingleAsync(c, t, "SELECT platform_code FROM collector_app_bindings WHERE id = UUID_TO_BIN(@id);", ct,
            ("@id", bindingId.ToString()));
        var web = owner is null && await QuerySingleAsync(c, t, "SELECT 1 FROM collector_session_profiles WHERE id = UUID_TO_BIN(@id);", ct,
            ("@id", bindingId.ToString())) is not null;
        await InsertEventAsync(c, t, new AppEvent("binding_forbidden", NodeActor(nodeId), nodeId, bindingId, owner?[0],
            Detail: new { route, reason = owner is not null ? "foreign_node" : web ? "web_session_profile" : "unknown_binding" }), ct);
        return new CollectorStoreException(BindingForbiddenCode, "绑定不存在或不属于该节点。", 403);
    }

    /// <summary>第二把锁：平台行（049 为每个平台建了一行；缺行时按列默认值补建）。</summary>
    private static async Task<PlatformRow> LockPlatformAsync(MySqlConnection c, MySqlTransaction t, string platformCode, CancellationToken ct)
    {
        const string sql = """
            SELECT paused_at IS NOT NULL
            FROM collector_app_platform_states WHERE platform_code = @platform FOR UPDATE;
            """;
        var row = await QuerySingleAsync(c, t, sql, ct, ("@platform", platformCode));
        if (row is null)
        {
            await ExecuteAsync(c, t, """
                INSERT INTO collector_app_platform_states (platform_code, created_at, updated_at)
                VALUES (@platform, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)) ON DUPLICATE KEY UPDATE platform_code = platform_code;
                """, ct, ("@platform", platformCode));
            row = await QuerySingleAsync(c, t, sql, ct, ("@platform", platformCode))
                ?? throw new CollectorStoreException("APP_PLATFORM_STATE_MISSING", "平台闸门状态不存在。", 500);
        }
        return new PlatformRow(Flag(row[0]));
    }

    private static async Task<bool> EntryEnabledAsync(MySqlConnection c, MySqlTransaction t, string platformCode, CancellationToken ct)
        => await QuerySingleAsync(c, t, $"""
            SELECT 1 FROM collector_platform_entries
            WHERE platform_code = @platform AND channel = '{AppChannel}' AND region = 'cn' AND enabled = TRUE LIMIT 1;
            """, ct, ("@platform", platformCode)) is not null;

    private static Outcome<AppLeaseResult> Empty(AppLeaseEmptyReason reason, double? seconds)
        => Done(new AppLeaseResult(null, reason, AppPoolRules.ClampRetry(seconds)));

    /// <inheritdoc />
    public Task<AppLeaseResult> LeaseAsync(AppLeaseCommand command, CancellationToken cancellationToken = default)
    {
        if (command.LeaseSeconds is < AppPoolRules.MinLeaseSeconds or > AppPoolRules.MaxLeaseSeconds)
            throw new CollectorStoreException("INVALID_LEASE", "leaseSeconds 必须在 5 到 900 之间。", 400);
        return InTransactionAsync(async (c, t, ct) =>
        {
            // 1. Binding of this node; every binding of its device is locked.
            await AppChannelSchema.RequireDeviceDispatchAsync(c, t, ct);
            var binding = await LockBindingAsync(c, t, command.NodeId, command.BindingId, wholeDevice: true, ct);
            if (binding is null) return Reject<AppLeaseResult>(await ForbidBindingAsync(c, t, command.NodeId, command.BindingId, "leases", ct));
            var id = ("@id", (object)binding.Id.ToString());

            // 2. Seen now; an elapsed cooldown ends.
            await ExecuteAsync(c, t, """
                UPDATE collector_app_bindings
                SET last_seen_at = UTC_TIMESTAMP(6), cooldown_until = CASE WHEN @due THEN NULL ELSE cooldown_until END,
                    updated_at = CASE WHEN @due THEN UTC_TIMESTAMP(6) ELSE updated_at END
                WHERE id = UUID_TO_BIN(@id);
                """, ct, id, ("@due", binding.CooldownDue));
            if (binding.CooldownDue)
                await InsertEventAsync(c, t, new AppEvent("cooldown_expired", NodeActor(command.NodeId), command.NodeId, binding.Id, binding.PlatformCode), ct);

            // 3. Stopped, then cooling.
            if (binding.Status != "active")
            {
                await StampBindingStoppedAsync(c, t, binding.PlatformCode, ct);
                return Empty(AppLeaseEmptyReason.BindingStopped, null);
            }
            if (!binding.CooldownDue && binding.CooldownRemaining > 0) return Empty(AppLeaseEmptyReason.BindingCooldown, binding.CooldownRemaining);

            // 4. One task per device at a time: an unexpired lease held by any binding of the device.
            var busy = await QuerySingleAsync(c, t, $"""
                SELECT COALESCE(MAX(TIMESTAMPDIFF(MICROSECOND, UTC_TIMESTAMP(6), task.lease_until)) / 1000000, 0)
                FROM collector_app_bindings b
                JOIN collector_tasks task ON task.channel = '{AppChannel}' AND task.platform_code = b.platform_code
                    AND task.status IN ('leased', 'collecting') AND task.lease_app_binding_id = b.id
                WHERE b.node_id = @node AND b.device_id = @device AND task.lease_until > UTC_TIMESTAMP(6);
                """, ct, ("@node", binding.NodeId), ("@device", binding.DeviceId));
            if (busy is not null && ParseDouble(busy[0]) > 0) return Empty(AppLeaseEmptyReason.DeviceBusy, ParseDouble(busy[0]));

            var remaining = await QuerySingleAsync(c, t, $"""
                SELECT TIMESTAMPDIFF(MICROSECOND, UTC_TIMESTAMP(6), MAX({LastPossibleSend}) + INTERVAL @seconds SECOND) / 1000000
                FROM collector_app_device_attempts WHERE node_id = @node AND device_id = @device
                  AND lease_until > UTC_TIMESTAMP(6) - INTERVAL @seconds SECOND;
                """, ct, ("@node", binding.NodeId), ("@device", binding.DeviceId), ("@seconds", AppPoolRules.DeviceCooldownSeconds));
            if (remaining is { Length: > 0 } && remaining[0].Length > 0 && ParseDouble(remaining[0]) > 0)
                return Done(new AppLeaseResult(null, AppLeaseEmptyReason.DeviceCooldown, (int)Math.Ceiling(ParseDouble(remaining[0]))));

            // 6. Platform row: paused, entry disabled.
            var platform = await LockPlatformAsync(c, t, binding.PlatformCode, ct);
            if (platform.Paused)
            {
                await StampWaitingTasksAsync(c, t, binding.PlatformCode, AppTaskBlockReasons.PlatformPaused, ct);
                return Empty(AppLeaseEmptyReason.PlatformPaused, null);
            }
            if (!await EntryEnabledAsync(c, t, binding.PlatformCode, ct))
            {
                await StampWaitingTasksAsync(c, t, binding.PlatformCode, AppTaskBlockReasons.PlatformUnavailable, ct);
                return Empty(AppLeaseEmptyReason.PlatformUnavailable, null);
            }

            // 8. No binding- or platform-level condition blocks this platform now: older block reasons are stale.
            await ClearBlockReasonsAsync(c, t, binding.PlatformCode, ct);
            var taskId = await PickLeasableTaskAsync(c, t, command.NodeId, binding.DeviceId, binding.PlatformCode, ct);
            if (taskId.Id is null)
                return taskId.CreditBlocked
                    ? Empty(AppLeaseEmptyReason.CreditInsufficient, AppPoolRules.CreditInsufficientRetrySeconds)
                    : Empty(AppLeaseEmptyReason.NoTask, AppPoolRules.NoTaskRetrySeconds);

            // 9. The lease.
            var task = ("@task", (object)taskId.Id.Value.ToString());
            await ExecuteAsync(c, t, """
                UPDATE collector_tasks
                SET status = 'leased', lease_version = lease_version + 1, lease_owner = @owner,
                    lease_until = UTC_TIMESTAMP(6) + INTERVAL @seconds SECOND, lease_app_binding_id = UUID_TO_BIN(@id),
                    attempt_count = attempt_count + 1, started_at = COALESCE(started_at, UTC_TIMESTAMP(6)),
                    last_block_reason = NULL, updated_at = UTC_TIMESTAMP(6)
                WHERE id = UUID_TO_BIN(@task);
                """, ct, task, id, ("@owner", command.NodeId), ("@seconds", command.LeaseSeconds));
            await ExecuteAsync(c, t, """
                INSERT INTO collector_attempts (id, tenant_id, collector_task_id, lease_version, node_id, region, event_kind, app_binding_id, created_at)
                SELECT UUID_TO_BIN(@attempt), tenant_id, id, lease_version, @node, region, 'leased', lease_app_binding_id, UTC_TIMESTAMP(6)
                FROM collector_tasks WHERE id = UUID_TO_BIN(@task);
                """, ct, task, ("@attempt", Guid.NewGuid().ToString()), ("@node", command.NodeId));
            await ExecuteAsync(c, t, """
                UPDATE collector_app_bindings SET last_leased_at = UTC_TIMESTAMP(6), updated_at = UTC_TIMESTAMP(6) WHERE id = UUID_TO_BIN(@id);
                """, ct, id);
            await ExecuteAsync(c, t, """
                UPDATE collector_app_platform_states SET last_dispatched_at = UTC_TIMESTAMP(6), updated_at = UTC_TIMESTAMP(6)
                WHERE platform_code = @platform;
                """, ct, ("@platform", binding.PlatformCode));
            var leased = await ReadLeasedTaskAsync(c, t, taskId.Id.Value, binding.Id, ct);
            await ExecuteAsync(c, t, """
                INSERT INTO collector_app_device_attempts(task_id, lease_version, binding_id, node_id, device_id, leased_at, lease_until)
                SELECT id, lease_version, lease_app_binding_id, lease_owner, @device, UTC_TIMESTAMP(6), lease_until
                FROM collector_tasks WHERE id = UUID_TO_BIN(@task);
                """, ct, task, ("@device", binding.DeviceId));
            await AppCollectionStore.UpdateRunStatusAsync(c, t, leased.RunId, ct);
            return Done(new AppLeaseResult(leased, null, null));
        }, cancellationToken);
    }

    private IReadOnlyList<Guid> OpenTenants()
        => (appChannel?.Value.AppTenantIds ?? []).Select(value => Guid.TryParse(value, out var tenant) ? tenant : Guid.Empty)
            .Where(tenant => tenant != Guid.Empty).Distinct().ToArray();

    // design 6.4: only App tasks of this platform, only tenants the channel is open for, not past the deadline. A lease that
    // ran out with no result and no failure report counts as that attempt's failure, so the task is leasable again (5.6a:
    // the web recovery skips App tasks); the device that held it is kept away by its attempt row, see NextCandidateAsync.
    private const string LeasableStatus =
        "(status IN ('pending', 'retryable_failed') OR (status IN ('leased', 'collecting') AND lease_until <= UTC_TIMESTAMP(6)))";
    // An unresolved device could have sent until its lease ended; this is a fence, not a fabricated sent_at. The row of an
    // expired lease stays unresolved after the task moved on, so that device's cooldown keeps running from the old lease_until.
    private const string LastPossibleSend = "CASE WHEN sent_at IS NOT NULL THEN sent_at WHEN send_state = 'unknown' THEN failed_at WHEN send_state IS NULL THEN lease_until END";

    private const string HeldItemExists = """
        EXISTS (SELECT 1 FROM monitoring_credit_items held
                WHERE held.tenant_id = task.tenant_id AND held.check_id = task.check_id AND held.status = 'held')
        """;

    // The tenant cannot pay for one more check right now. Read without a lock; the reservation checks again under the lock.
    private const string TenantLacksCredit =
        "COALESCE((SELECT account.available_credits FROM credit_accounts account WHERE account.tenant_id = task.tenant_id), 0) < @price";

    private sealed record Candidate(Guid Id, Guid TenantId, Guid RunId, Guid CheckId, bool LeaseExpired, int FailureCount, string? LeaseOwner,
        Guid? LeaseBindingId, int LeaseVersion);

    /// <summary>
    /// 第 8 步的选任务（design 6.2 第 7 步、6.4，implement 6.0 / 6.2）。
    /// <list type="number">
    /// <item>余额不足：租户的可用积分不够一次检测、检测又没有预留的任务这一次不领取。它们先被整批记下 credit_insufficient
    /// （<see cref="StampCreditBlockedAsync"/>），选任务的查询直接越过它们，所以余额不足的租户再多也不会挡住后面的任务。</item>
    /// <item>其余候选按排队顺序逐条加锁查看，最多 <see cref="AppPoolRules.MaxLeaseCandidates"/> 条：上一次租约已过期、既没有结果也没有
    /// 失败上报的，先按一次失败的尝试记下（<see cref="RecordExpiredLeaseAsync"/>），再和其他候选一样处理，没有次数上限；
    /// 检测已有 held 的预留则直接领取，不重复预留；否则预留一次的积分并领取。加锁后才发现余额已被别的事务用掉的，按余额不足处理，继续看下一条。</item>
    /// </list>
    /// 返回被选中的任务；一条也没有选中时，<c>CreditBlocked</c> 表示此刻有任务只因为余额不足而没有被领取。
    /// </summary>
    private async Task<(Guid? Id, bool CreditBlocked)> PickLeasableTaskAsync(MySqlConnection c, MySqlTransaction t, string nodeId, string deviceId, string platformCode,
        CancellationToken ct)
    {
        var tenants = OpenTenants();
        if (tenants.Count == 0) return (null, false);
        await StampCreditBlockedAsync(c, t, platformCode, tenants, ct);
        var (broke, closed) = (new List<Guid>(), new List<Guid>());
        for (var examined = 0; examined < AppPoolRules.MaxLeaseCandidates; examined++)
        {
            if (await NextCandidateAsync(c, t, nodeId, deviceId, platformCode, tenants, broke, closed, ct) is not { } candidate) break;
            if (candidate.LeaseExpired) await RecordExpiredLeaseAsync(c, t, nodeId, platformCode, candidate, ct);
            switch (await AppCreditReservations.ReserveAsync(c, t, candidate.TenantId, candidate.RunId, candidate.CheckId, ct))
            {
                case AppCreditReservations.Outcome.Insufficient:
                    // Another transaction used the credits between the pick and the account lock.
                    broke.Add(candidate.TenantId);
                    await SetBlockReasonAsync(c, t, [[candidate.Id.ToString()]], AppTaskBlockReasons.CreditInsufficient, ct);
                    continue;
                case AppCreditReservations.Outcome.Closed:
                    // The check's credit item was already captured or released although its task is still leasable.
                    closed.Add(candidate.Id);
                    await InsertEventAsync(c, t, new AppEvent("credit_item_closed", NodeActor(nodeId), nodeId, PlatformCode: platformCode,
                        TaskId: candidate.Id, Detail: new { checkId = candidate.CheckId }), ct);
                    continue;
                default:
                    return (candidate.Id, false);
            }
        }
        var blocked = await QuerySingleAsync(c, t, $"""
            SELECT 1 FROM collector_tasks task
            WHERE channel = '{AppChannel}' AND platform_code = @platform AND tenant_id IN ({TenantList(tenants)})
              AND deadline_at > UTC_TIMESTAMP(6) AND next_attempt_at <= UTC_TIMESTAMP(6) AND {LeasableStatus}
              AND NOT {HeldItemExists} AND {TenantLacksCredit}
            LIMIT 1;
            """, ct, [("@platform", platformCode), ("@price", PromptMonitoringPricing.UnitPrice), .. TenantParameters(tenants)]);
        return (null, blocked is not null || broke.Count > 0);
    }

    // design 6.4, one row at a time. A task is a candidate when its check already holds a reservation (a requeued task needs
    // no new credit) or its tenant can pay for one check; tenants that lost their credits during this call are passed over.
    // Each device attempts a task once: any attempt row of this node and device excludes the task, whether that attempt
    // reported a failure or its lease just ran out, so a device that went silent cannot get the same task back.
    private static async Task<Candidate?> NextCandidateAsync(MySqlConnection c, MySqlTransaction t, string nodeId, string deviceId, string platformCode, IReadOnlyList<Guid> tenants,
        List<Guid> broke, List<Guid> closed, CancellationToken ct)
    {
        var row = await QuerySingleAsync(c, t, $"""
            SELECT BIN_TO_UUID(task.id), BIN_TO_UUID(task.tenant_id), BIN_TO_UUID(task.run_id), BIN_TO_UUID(task.check_id),
                   task.status IN ('leased', 'collecting'), task.failure_count, task.lease_owner,
                   BIN_TO_UUID(task.lease_app_binding_id), task.lease_version
            FROM collector_tasks task
            WHERE channel = '{AppChannel}' AND platform_code = @platform AND tenant_id IN ({TenantList(tenants)})
              AND deadline_at > UTC_TIMESTAMP(6) AND next_attempt_at <= UTC_TIMESTAMP(6) AND {LeasableStatus}
              AND NOT EXISTS (SELECT 1 FROM collector_app_device_attempts prev WHERE prev.task_id = task.id
                   AND prev.node_id = @node AND prev.device_id = @device)
              {(closed.Count == 0 ? "" : $"AND task.id NOT IN ({IdList("closed", closed)})")}
              AND ({HeldItemExists} OR (NOT {TenantLacksCredit}{(broke.Count == 0 ? "" : $" AND task.tenant_id NOT IN ({IdList("broke", broke)})")}))
            ORDER BY fair_rank, created_at, id
            LIMIT 1
            FOR UPDATE OF task SKIP LOCKED;
            """, ct, [("@node", nodeId), ("@device", deviceId), ("@platform", platformCode), ("@price", PromptMonitoringPricing.UnitPrice), .. TenantParameters(tenants),
                .. IdParameters("closed", closed), .. IdParameters("broke", broke)]);
        return row is null ? null : new Candidate(Guid.Parse(row[0]), Guid.Parse(row[1]), Guid.Parse(row[2]), Guid.Parse(row[3]), Flag(row[4]),
            Int(row[5]), row[6].Length == 0 ? null : row[6], row[7].Length == 0 ? null : Guid.Parse(row[7]), Int(row[8]));
    }

    /// <summary>
    /// 租约过期、既没有结果也没有失败上报的任务再次轮到被领取时，视同上一次尝试失败（PRD 采集约束 3）：任务先回到 retryable_failed
    /// 并记一次失败（last_error_code 为 lease_expired），所以同一次过期只记一次；随后由本次领取的设备以新的租约版本接手。
    /// 审计事件 lease_expired 记在上一次持有它的节点和绑定名下。没有次数上限。上一次的设备尝试行保持原样（没有 sent_at、
    /// send_state 为空）：那台设备的冷却继续从旧的 lease_until 起算，它迟到的发送、失败和结果回报都是 stale。
    /// </summary>
    private static async Task RecordExpiredLeaseAsync(MySqlConnection c, MySqlTransaction t, string nodeId, string platformCode,
        Candidate candidate, CancellationToken ct)
    {
        await ExecuteAsync(c, t, """
            UPDATE collector_tasks
            SET status = 'retryable_failed', lease_owner = NULL, lease_until = NULL, failure_count = failure_count + 1,
                last_error_code = @code, last_error_message = @message, updated_at = UTC_TIMESTAMP(6)
            WHERE id = UUID_TO_BIN(@id);
            """, ct, ("@code", AppPoolRules.LeaseExpiredErrorCode), ("@message", "App collector lease expired without a result or a failure report"),
            ("@id", candidate.Id.ToString()));
        await InsertEventAsync(c, t, new AppEvent("lease_expired", NodeActor(nodeId), candidate.LeaseOwner, candidate.LeaseBindingId, platformCode,
            candidate.Id, new
            {
                leaseVersion = candidate.LeaseVersion, failureCount = candidate.FailureCount + 1, detectedByNodeId = nodeId,
                taskDisposition = AppContractNames.ToStorage(CollectorTaskDisposition.Requeued),
            }), ct);
    }

    private static string TenantList(IReadOnlyList<Guid> tenants) => IdList("tenant", tenants);

    private static IEnumerable<object> TenantParameters(IReadOnlyList<Guid> tenants) => IdParameters("tenant", tenants);

    private static string IdList(string name, IReadOnlyList<Guid> ids) => string.Join(", ", ids.Select((_, i) => $"UUID_TO_BIN(@{name}{i})"));

    private static IEnumerable<object> IdParameters(string name, IReadOnlyList<Guid> ids)
        => ids.Select((id, i) => (object)($"@{name}{i}", id.ToString()));

    private static async Task<AppLeasedTask> ReadLeasedTaskAsync(MySqlConnection c, MySqlTransaction t, Guid taskId, Guid bindingId, CancellationToken ct)
    {
        var row = await QuerySingleAsync(c, t, $"""
            SELECT BIN_TO_UUID(task.id), BIN_TO_UUID(task.tenant_id), BIN_TO_UUID(task.run_id), BIN_TO_UUID(task.check_id),
                   BIN_TO_UUID(r.site_id), task.platform_code, e.entry_code, BIN_TO_UUID(chk.prompt_id), task.question_snapshot,
                   task.region, task.language, task.channel, COALESCE(task.prompt_rule_version, 0), {Iso("task.deadline_at")},
                   task.lease_version, {Iso("task.lease_until")}
            FROM collector_tasks task
            JOIN collector_platform_entries e ON e.id = task.platform_entry_id
            JOIN visibility_runs r ON r.id = task.run_id
            JOIN visibility_checks chk ON chk.id = task.check_id
            WHERE task.id = UUID_TO_BIN(@task);
            """, ct, ("@task", taskId.ToString())) ?? throw new InvalidOperationException("领取的任务不存在。");
        return new AppLeasedTask(Guid.Parse(row[0]), Guid.Parse(row[1]), Guid.Parse(row[2]), Guid.Parse(row[3]), Guid.Parse(row[4]),
            row[5], row[6], Guid.Parse(row[7]), row[8], row[9], row[10], row[11], Int(row[12]), ParseIso(row[13]), Int(row[14]),
            ParseIso(row[15]), bindingId);
    }

    /// <inheritdoc />
    public Task<DateTimeOffset> HeartbeatAsync(AppHeartbeatCommand command, CancellationToken cancellationToken = default)
    {
        if (command.ExtendSeconds is < AppPoolRules.MinLeaseSeconds or > AppPoolRules.MaxLeaseSeconds)
            throw new CollectorStoreException("INVALID_LEASE", "extendSeconds 必须在 5 到 900 之间。", 400);
        return InTransactionAsync(async (c, t, ct) =>
        {
            var task = ("@task", (object)command.TaskId.ToString());
            var affected = await ExecuteAsync(c, t, $"""
                UPDATE collector_tasks
                SET lease_until = UTC_TIMESTAMP(6) + INTERVAL @seconds SECOND, status = 'collecting', updated_at = UTC_TIMESTAMP(6)
                WHERE id = UUID_TO_BIN(@task) AND channel = '{AppChannel}' AND lease_owner = @owner AND lease_version = @version
                  AND status IN ('leased', 'collecting') AND lease_until > UTC_TIMESTAMP(6);
                """, ct, task, ("@seconds", command.ExtendSeconds), ("@owner", command.NodeId), ("@version", command.LeaseVersion));
            if (affected == 0) throw new CollectorStoreException("STALE_LEASE", "该任务的租约已不是当前租约。", 409);
            await ExecuteAsync(c, t, """
                UPDATE collector_app_device_attempts a JOIN collector_tasks task ON task.id = a.task_id AND task.lease_version = a.lease_version
                SET a.lease_until = task.lease_until WHERE task.id = UUID_TO_BIN(@task);
                """, ct, task);
            await ExecuteAsync(c, t, """
                INSERT INTO collector_attempts (id, tenant_id, collector_task_id, lease_version, node_id, region, event_kind, app_binding_id, created_at)
                SELECT UUID_TO_BIN(@attempt), tenant_id, id, lease_version, @node, region, 'heartbeat', lease_app_binding_id, UTC_TIMESTAMP(6)
                FROM collector_tasks WHERE id = UUID_TO_BIN(@task);
                """, ct, task, ("@attempt", Guid.NewGuid().ToString()), ("@node", command.NodeId));
            var expiry = await QuerySingleAsync(c, t, $"SELECT {Iso("lease_until")} FROM collector_tasks WHERE id = UUID_TO_BIN(@task);", ct, task);
            return Done(ParseIso(expiry![0]));
        }, cancellationToken);
    }
}
