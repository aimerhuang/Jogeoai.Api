using Jogeoai.Domain;
using MySqlConnector;
using static Jogeoai.Infrastructure.CollectorSessionDispatch;
using static Jogeoai.Infrastructure.DomesticCollectorStore;

namespace Jogeoai.Infrastructure;

/// <summary>
/// 失败上报的处理矩阵（implement 5.7，contract.md 第 9 节）与平台的恢复。任务的去向不取决于发送状态：当前租约的失败只结束这一次尝试，
/// 任务回到队列，可由另一台设备领取（同一设备不再领取它）；没有失败次数上限，任务只受“每台设备尝试一次”和截止时点约束。
/// 只有需要人工验证、App 拒绝模拟器和频率受限使任务终态并记为未采集。需要登录仍停止绑定，但任务照常回到队列。
/// </summary>
public sealed partial class AppCollectorPoolStore
{
    private const string FailureMessage = "App collector reported a classified failure";

    private sealed record ReportedTaskRow(Guid Id, Guid TenantId, Guid RunId, Guid CheckId, string Status, int LeaseVersion, string LeaseOwner,
        Guid? LeaseBindingId, int FailureCount, string? LastBlockReason);

    /// <inheritdoc />
    public Task<AppFailureResult> ReportFailureAsync(AppFailureCommand command, CancellationToken cancellationToken = default)
        => InTransactionAsync(async (c, t, ct) =>
        {
            // Locks: binding → platform → task (→ credit account → check → run when the task becomes terminal).
            var binding = await LockBindingAsync(c, t, command.NodeId, command.BindingId, wholeDevice: false, ct);
            if (binding is null) return Reject<AppFailureResult>(await ForbidBindingAsync(c, t, command.NodeId, command.BindingId, "failures", ct));
            var platform = await LockPlatformAsync(c, t, binding.PlatformCode, ct);
            AppBindingStopReason? current = AppContractNames.TryParse<AppBindingStopReason>(binding.StopReason, out var held) ? held : null;
            ReportedTaskRow? task = null;
            if (command.Task is { } reported)
            {
                var row = await QuerySingleAsync(c, t, """
                    SELECT BIN_TO_UUID(id), BIN_TO_UUID(tenant_id), BIN_TO_UUID(run_id), BIN_TO_UUID(check_id), status, lease_version,
                           lease_owner, BIN_TO_UUID(lease_app_binding_id), failure_count, last_block_reason, channel, platform_code,
                           lease_until > UTC_TIMESTAMP(6)
                    FROM collector_tasks WHERE id = UUID_TO_BIN(@id) FOR UPDATE;
                    """, ct, ("@id", reported.TaskId.ToString()))
                    ?? throw new CollectorStoreException("TASK_NOT_FOUND", "采集任务不存在。", 404);
                // A web task, or a task of another platform, is never touched by an App binding.
                if (row[10] != AppChannel || row[11] != binding.PlatformCode)
                {
                    await InsertEventAsync(c, t, new AppEvent("binding_forbidden", NodeActor(command.NodeId), command.NodeId, binding.Id,
                        binding.PlatformCode, reported.TaskId, new { route = "failures", reason = "task_not_for_binding" }), ct);
                    return Reject<AppFailureResult>(new CollectorStoreException(BindingForbiddenCode, "该任务不属于这个绑定的平台或渠道。", 403));
                }
                task = new ReportedTaskRow(Guid.Parse(row[0]), Guid.Parse(row[1]), Guid.Parse(row[2]), Guid.Parse(row[3]), row[4], Int(row[5]),
                    row[6], row[7].Length == 0 ? null : Guid.Parse(row[7]), Int(row[8]), row[9].Length == 0 ? null : row[9]);
                if (!(task.Status is "leased" or "collecting") || task.LeaseVersion != reported.LeaseVersion
                    || task.LeaseOwner != command.NodeId || task.LeaseBindingId != binding.Id || !Flag(row[12]))
                {
                    await InsertEventAsync(c, t, new AppEvent("failure_stale", NodeActor(command.NodeId), command.NodeId, binding.Id,
                        binding.PlatformCode, reported.TaskId, new { reported.LeaseVersion, failureCode = AppContractNames.ToStorage(command.FailureCode) }), ct);
                    return Done(new AppFailureResult(current is null ? AppBindingStatus.Active : AppBindingStatus.Stopped, current,
                        CollectorTaskDisposition.Stale, platform.Paused, !await EntryEnabledAsync(c, t, binding.PlatformCode, ct), null));
                }
            }
            var code = AppContractNames.ToStorage(command.FailureCode);
            var phase = AppContractNames.ToStorage(command.Phase);

            // Binding: stop (operator stops and stops that need an operator are kept), or cool down until the Beijing day ends.
            var safety = command.FailureCode is AppFailureCode.LoginRequired or AppFailureCode.ChallengeRequired
                or AppFailureCode.EmulatorRejected or AppFailureCode.RateLimited;
            var next = safety && AppFailureRules.BindingStopReason(command.FailureCode) is { } stop ? AppPoolRules.NextStopReason(current, stop) : current;
            var reason = next is { } value ? AppContractNames.ToStorage(value) : null;
            var cooling = command.FailureCode == AppFailureCode.RateLimited;
            if (safety) await ExecuteAsync(c, t, $"""
                UPDATE collector_app_bindings
                SET status = @status, stop_reason = @reason,
                    stopped_at = CASE WHEN @reason IS NULL THEN NULL ELSE COALESCE(stopped_at, UTC_TIMESTAMP(6)) END,
                    cooldown_until = CASE WHEN @cooling THEN {ShanghaiDayStartSql} + INTERVAL 1 DAY ELSE cooldown_until END,
                    last_seen_at = UTC_TIMESTAMP(6), updated_at = UTC_TIMESTAMP(6)
                WHERE id = UUID_TO_BIN(@id);
                """, ct, ("@status", next is null ? "active" : "stopped"), ("@reason", reason), ("@cooling", cooling), ("@id", binding.Id.ToString()));

            // A current failure ends just this attempt and the task goes back to the queue for another device, whatever the send
            // state (not sent, sent, unknown) and also after a login screen. Only a verification screen, an App that refuses the
            // emulator and a rate limit close the task: the question is not asked again elsewhere.
            CollectorTaskDisposition? disposition = null;
            if (task is not null && command.Task is { } reportedTask)
            {
                // The send state is validated and kept on the attempt row: it drives the device cooldown and the audit trail,
                // not the task's fate. A report without one (an old node) is recorded as unknown.
                var state = command.SendState ?? "unknown";
                if (state is not ("not_sent" or "sent" or "unknown") || (state == "sent") != command.SentAt.HasValue
                    || state == "not_sent" && command.Phase != CollectorPhase.PreSend
                    || command.SendState is "sent" or "unknown" && command.Phase != CollectorPhase.PostSend)
                    throw new CollectorStoreException("INVALID_FAILURE", "发送状态、阶段和发送时间不一致。", 400);
                if (command.SentAt is { } sent && await RecordSendAsync(c, t,
                    new AppSentCommand(command.NodeId, binding.Id, task.Id, reportedTask.LeaseVersion, sent), true, ct) == "stale")
                    throw new CollectorStoreException("ATTEMPT_NOT_FOUND", "没有对应的设备尝试。", 409);
                var updated = await ExecuteAsync(c, t, """
                    UPDATE collector_app_device_attempts SET failed_at = UTC_TIMESTAMP(6), failure_code = @code, send_state = @state
                    WHERE task_id = UUID_TO_BIN(@id) AND lease_version = @version AND binding_id = UUID_TO_BIN(@binding)
                      AND failed_at IS NULL AND (@state <> 'not_sent' OR sent_at IS NULL);
                    """, ct, ("@id", task.Id.ToString()), ("@version", reportedTask.LeaseVersion), ("@binding", binding.Id.ToString()),
                    ("@code", code), ("@state", state));
                if (updated == 0) throw new CollectorStoreException("ATTEMPT_CONFLICT", "设备尝试不存在或与已发送事实不符。", 409);
                var terminal = command.FailureCode is AppFailureCode.ChallengeRequired or AppFailureCode.EmulatorRejected
                    or AppFailureCode.RateLimited;
                await ExecuteAsync(c, t, """
                    UPDATE collector_tasks SET status = @status, lease_owner = NULL, lease_until = NULL, failure_count = failure_count + 1,
                        last_error_code = @code, last_error_message = @message, next_attempt_at = UTC_TIMESTAMP(6), updated_at = UTC_TIMESTAMP(6)
                    WHERE id = UUID_TO_BIN(@id);
                    """, ct, ("@status", terminal ? "terminal_failed" : "retryable_failed"), ("@code", code),
                    ("@message", FailureMessage), ("@id", task.Id.ToString()));
                if (terminal)
                    await AppCollectionStore.MarkNotCollectedAsync(c, t, task.Id, task.TenantId, task.CheckId,
                        AppNotCollectedRules.Resolve(task.FailureCount + 1, task.LastBlockReason, code), ct);
                await AppCollectionStore.UpdateRunStatusAsync(c, t, task.RunId, ct);
                disposition = terminal ? CollectorTaskDisposition.Failed : CollectorTaskDisposition.Requeued;
                await ExecuteAsync(c, t, """
                    INSERT INTO collector_attempts (id, tenant_id, collector_task_id, lease_version, node_id, region, event_kind,
                        error_code, app_binding_id, created_at)
                    SELECT UUID_TO_BIN(@attempt), tenant_id, id, @version, @node, region, 'failed', @code, UUID_TO_BIN(@binding), UTC_TIMESTAMP(6)
                    FROM collector_tasks WHERE id = UUID_TO_BIN(@id);
                    """, ct, ("@attempt", Guid.NewGuid().ToString()), ("@version", reportedTask.LeaseVersion), ("@node", command.NodeId),
                    ("@code", code), ("@binding", binding.Id.ToString()), ("@id", task.Id.ToString()));
            }
            await InsertEventAsync(c, t, new AppEvent("failure_reported", NodeActor(command.NodeId), command.NodeId, binding.Id, binding.PlatformCode,
                task?.Id, new
                {
                    failureCode = code, phase, leaseVersion = command.Task?.LeaseVersion, fromStopReason = binding.StopReason, toStopReason = reason,
                    cooldown = cooling, taskDisposition = disposition is { } d ? AppContractNames.ToStorage(d) : null,
                }), ct);

            // Platform: the verification breaker, and an App that refuses the emulator.
            var paused = platform.Paused;
            if (!paused && AppPoolRules.CountsTowardVerificationBreaker(command.FailureCode))
            {
                var bindings = await QuerySingleAsync(c, t, """
                    SELECT COUNT(DISTINCT e.binding_id) FROM collector_app_events e
                    WHERE e.platform_code = @platform AND e.event_kind = 'failure_reported'
                      AND e.occurred_at > UTC_TIMESTAMP(6) - INTERVAL @window MINUTE
                      AND JSON_UNQUOTE(JSON_EXTRACT(e.detail, '$.failureCode')) IN ('login_required', 'challenge_required')
                      AND e.occurred_at > COALESCE((SELECT MAX(r.occurred_at) FROM collector_app_events r
                                                    WHERE r.platform_code = @platform AND r.event_kind = 'platform_resumed'), '1000-01-01');
                    """, ct, ("@platform", binding.PlatformCode), ("@window", AppPoolRules.VerificationBreakerWindowMinutes));
                if (Int(bindings![0]) >= AppPoolRules.VerificationBreakerBindingThreshold)
                {
                    await ExecuteAsync(c, t, """
                        UPDATE collector_app_platform_states SET paused_at = UTC_TIMESTAMP(6), pause_reason = @reason, updated_at = UTC_TIMESTAMP(6)
                        WHERE platform_code = @platform;
                        """, ct, ("@reason", AppPoolRules.VerificationBreakerPauseReason), ("@platform", binding.PlatformCode));
                    await InsertEventAsync(c, t, new AppEvent("platform_paused", NodeActor(command.NodeId), command.NodeId, binding.Id,
                        binding.PlatformCode, Detail: new { reason = AppPoolRules.VerificationBreakerPauseReason }), ct);
                    paused = true;
                }
            }
            // implement 6.0b: one device's verdict does not stop the platform for everyone. The entry becomes unavailable only
            // when every binding of the platform that the operator has not disabled is stopped with emulator_rejected (a
            // platform with a single such binding qualifies on its one report). Reports of one platform are serialized by
            // the platform row lock, so the second of two concurrent reports sees the first one's binding.
            var unavailable = !await EntryEnabledAsync(c, t, binding.PlatformCode, ct);
            if (!unavailable && command.FailureCode == AppFailureCode.EmulatorRejected && await AllBindingsRejectedAsync(c, t, binding.PlatformCode, ct))
            {
                await ExecuteAsync(c, t, $"""
                    UPDATE collector_platform_entries SET enabled = FALSE, unavailable_reason = @reason
                    WHERE platform_code = @platform AND channel = '{AppChannel}' AND region = 'cn' AND enabled = TRUE;
                    """, ct, ("@reason", AppUnavailableReasons.EmulatorRejected), ("@platform", binding.PlatformCode));
                await InsertEventAsync(c, t, new AppEvent("platform_unavailable", NodeActor(command.NodeId), command.NodeId, binding.Id,
                    binding.PlatformCode, Detail: new { reason = AppUnavailableReasons.EmulatorRejected }), ct);
                unavailable = true;
            }
            // implement 6.0a: the collector learns the end of a cooldown from this response (rate_limited, or one still running).
            var cooldown = await QuerySingleAsync(c, t, $"""
                SELECT {Iso("CASE WHEN cooldown_until > UTC_TIMESTAMP(6) THEN cooldown_until ELSE NULL END")}
                FROM collector_app_bindings WHERE id = UUID_TO_BIN(@id);
                """, ct, ("@id", binding.Id.ToString()));
            return Done(new AppFailureResult(next is null ? AppBindingStatus.Active : AppBindingStatus.Stopped, next, disposition, paused, unavailable,
                cooldown is null ? null : ParseIsoOrNull(cooldown[0])));
        }, cancellationToken);

    private static async Task<bool> AllBindingsRejectedAsync(MySqlConnection c, MySqlTransaction t, string platformCode, CancellationToken ct)
    {
        var row = await QuerySingleAsync(c, t, """
            SELECT COUNT(*), COALESCE(SUM(status = 'stopped' AND stop_reason = @rejected), 0) FROM collector_app_bindings
            WHERE platform_code = @platform AND (stop_reason IS NULL OR stop_reason <> @operator);
            """, ct, ("@platform", platformCode), ("@rejected", AppContractNames.ToStorage(AppBindingStopReason.EmulatorRejected)),
            ("@operator", AppContractNames.ToStorage(AppBindingStopReason.DisabledByOperator)));
        return row is not null && Int(row[0]) > 0 && Int(row[0]) == Int(row[1]);
    }

    /// <inheritdoc />
    public Task<bool> ResumePlatformAsync(string platformCode, Guid? operatorUserId, CancellationToken cancellationToken = default)
    {
        var platform = AppChannelDefinition.TryGetPlatform(platformCode)
            ?? throw new CollectorStoreException("INVALID_PLATFORM", "platformCode 不是 App 渠道的平台。", 400);
        return InTransactionAsync(async (c, t, ct) =>
        {
            if (!(await LockPlatformAsync(c, t, platform.Code, ct)).Paused) return Done(false);
            await ExecuteAsync(c, t, """
                UPDATE collector_app_platform_states SET paused_at = NULL, pause_reason = NULL, updated_at = UTC_TIMESTAMP(6)
                WHERE platform_code = @platform;
                """, ct, ("@platform", platform.Code));
            await InsertEventAsync(c, t, new AppEvent("platform_resumed", Operator(operatorUserId), PlatformCode: platform.Code), ct);
            return Done(true);
        }, cancellationToken);
    }
}
