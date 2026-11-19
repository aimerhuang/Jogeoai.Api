using System.Data;
using System.Globalization;
using Jogeoai.Application;
using Jogeoai.Domain;
using MySqlConnector;
using static Jogeoai.Infrastructure.CollectorSessionDispatch;
using static Jogeoai.Infrastructure.DomesticCollectorStore;

namespace Jogeoai.Infrastructure;

/// <summary>采集器账号池的 MySQL 实现（迁移 035）。所有写路径使用 READ COMMITTED，并按账号 → 平台 → 任务加锁。</summary>
public sealed class CollectorSessionPoolStore(MySqlDatabase database) : ICollectorSessionPoolStore
{
    private const string FailureMessage = "Collector reported a classified failure";

    internal static readonly IReadOnlyDictionary<string, CollectorSessionProfileStatus> Statuses = new Dictionary<string, CollectorSessionProfileStatus>
    {
        ["active"] = CollectorSessionProfileStatus.Active,
        ["cooling"] = CollectorSessionProfileStatus.Cooling,
        ["needs_verification"] = CollectorSessionProfileStatus.NeedsVerification,
        ["expired"] = CollectorSessionProfileStatus.Expired,
        ["disabled"] = CollectorSessionProfileStatus.Disabled,
    };

    internal static string StatusCode(CollectorSessionProfileStatus status) => Statuses.First(pair => pair.Value == status).Key;

    internal static string FailureCodeValue(CollectorFailureCode code) => code switch
    {
        CollectorFailureCode.LoginRequired => "login_required",
        CollectorFailureCode.RegionRestricted => "region_restricted",
        CollectorFailureCode.ChallengeRequired => "challenge_required",
        CollectorFailureCode.RateLimited => "rate_limited",
        CollectorFailureCode.PageContractChanged => "page_contract_changed",
        CollectorFailureCode.PageContractUnverified => "page_contract_unverified",
        CollectorFailureCode.CollectionTimeout => "collection_timeout",
        _ => "browser_failed",
    };

    internal static string PhaseValue(CollectorPhase phase) => phase switch
    {
        CollectorPhase.Probe => "probe",
        CollectorPhase.PreSend => "pre_send",
        _ => "post_send",
    };

    private static string EventKindValue(CollectorSessionEventKind kind) => kind switch
    {
        CollectorSessionEventKind.ChallengeWaitStarted => "challenge_wait_started",
        CollectorSessionEventKind.ChallengeCleared => "challenge_cleared",
        _ => "verified",
    };

    /// <inheritdoc />
    public Task<CollectorEligibilityResult> CheckEligibilityAsync(CollectorEligibilityCommand command, CancellationToken cancellationToken = default)
        => InTransactionAsync(async (c, t, ct) =>
        {
            var region = RegionCode(command.Region);
            var (profile, blocker) = await EvaluateAsync(c, t, command.TenantId, command.NodeId, region, command.SessionProfileId,
                [command.PlatformCode], leaseMode: false, ct);
            var taskParameters = new object[]
            {
                ("@tenant_id", command.TenantId.ToString()), ("@region", profile.Region), ("@platform", profile.PlatformCode),
            };
            if (blocker is null)
            {
                var available = await QuerySingleAsync(c, t, $"SELECT 1 FROM collector_tasks WHERE {LeasableTaskPredicate} LIMIT 1;", ct, taskParameters);
                if (available is not null) return new CollectorEligibilityResult(true, RetryMinSeconds);
                // 最早可领取时刻：退避截止或未过期租约的到期；队列为空时为 null（按上限 120 秒）。
                var next = await QuerySingleAsync(c, t, """
                    SELECT TIMESTAMPDIFF(MICROSECOND, UTC_TIMESTAMP(6), MIN(GREATEST(next_attempt_at,
                               CASE WHEN status IN ('leased', 'collecting') THEN lease_until ELSE next_attempt_at END))) / 1000000
                    FROM collector_tasks
                    WHERE tenant_id = UUID_TO_BIN(@tenant_id) AND region = @region AND platform_code = @platform
                      AND channel = 'consumer_web'
                      AND status IN ('pending', 'retryable_failed', 'leased', 'collecting');
                    """, ct, taskParameters);
                return new CollectorEligibilityResult(false, ClampRetry(next is null || next[0] == "" ? null : ParseDouble(next[0])));
            }
            return blocker.Kind switch
            {
                BlockerKind.OperatorStop => new CollectorEligibilityResult(false, RetryMaxSeconds, blocker.Status switch
                {
                    "needs_verification" => CollectorEligibilityStopReason.NeedsVerification,
                    "expired" => CollectorEligibilityStopReason.Expired,
                    _ => CollectorEligibilityStopReason.Disabled,
                }),
                BlockerKind.Profile => new CollectorEligibilityResult(false, ClampRetry(await ProfileBlockerSecondsAsync(c, t, command.TenantId, profile, ct))),
                // 更早的在线账号马上会领取；稍后再查。
                BlockerKind.Fairness => new CollectorEligibilityResult(false, RetryMinSeconds),
                // 只有运维能解除熔断。
                BlockerKind.PlatformPaused => new CollectorEligibilityResult(false, RetryMaxSeconds),
                _ => new CollectorEligibilityResult(false, ClampRetry(Math.Max(blocker.IntervalRemainingSeconds, blocker.SecondsUntilCapReset ?? 0))),
            };
        }, cancellationToken);

    /// <summary>冷却结束、账号最小间隔、以及足够多的领取记录滑出每小时 / 每日配额窗口，三者中最晚的那个。</summary>
    private static async Task<double> ProfileBlockerSecondsAsync(MySqlConnection c, MySqlTransaction t, Guid tenantId, ProfileRow profile,
        CancellationToken ct)
    {
        var parameters = new object[] { ("@tenant_id", tenantId.ToString()), ("@id", profile.Id.ToString()) };
        var own = await QuerySingleAsync(c, t, """
            SELECT GREATEST(0,
                CASE WHEN status = 'cooling' AND cooldown_until > UTC_TIMESTAMP(6)
                     THEN TIMESTAMPDIFF(MICROSECOND, UTC_TIMESTAMP(6), cooldown_until) / 1000000 ELSE 0 END,
                CASE WHEN last_leased_at IS NULL THEN 0
                     ELSE TIMESTAMPDIFF(MICROSECOND, UTC_TIMESTAMP(6), last_leased_at + INTERVAL min_interval_seconds SECOND) / 1000000 END)
            FROM collector_session_profiles WHERE tenant_id = UUID_TO_BIN(@tenant_id) AND id = UUID_TO_BIN(@id);
            """, ct, parameters);
        var seconds = own is null ? 0 : ParseDouble(own[0]);
        foreach (var (quota, hours) in new[] { (profile.HourlyQuota, 1), (profile.DailyQuota, 24) })
        {
            // 第 quota 新的领取记录滑出窗口时，配额恢复一个名额。
            var slide = await QuerySingleAsync(c, t, $"""
                SELECT TIMESTAMPDIFF(MICROSECOND, UTC_TIMESTAMP(6), created_at + INTERVAL {hours} HOUR) / 1000000
                FROM collector_attempts
                WHERE tenant_id = UUID_TO_BIN(@tenant_id) AND session_profile_id = UUID_TO_BIN(@id) AND event_kind = 'leased'
                  AND created_at > UTC_TIMESTAMP(6) - INTERVAL {hours} HOUR
                ORDER BY created_at DESC
                LIMIT 1 OFFSET {quota - 1};
                """, ct, parameters);
            if (slide is not null) seconds = Math.Max(seconds, ParseDouble(slide[0]));
        }
        return seconds;
    }

    /// <inheritdoc />
    public Task<CollectorFailureResult> ReportFailureAsync(CollectorFailureCommand command, CancellationToken cancellationToken = default)
        => InTransactionAsync(async (c, t, ct) =>
        {
            var tenant = command.TenantId.ToString();
            var profile = await LockProfileAsync(c, t, command.TenantId, command.SessionProfileId, RegionCode(command.Region), ct);
            var platform = await LockPlatformAsync(c, t, command.TenantId, profile.Region, profile.PlatformCode, ct);
            var task = command.Task is { } reported ? await LockReportedTaskAsync(c, t, command.TenantId, reported.TaskId, profile, ct) : null;
            var failureCode = FailureCodeValue(command.FailureCode);

            // 运维停用的账号保持停用；事件仍记录这次失败。限流不会把需要人工处理的账号降级为自动到期的冷却。
            var keep = profile.Status == "disabled";
            var toStatus = profile.Status;
            if (!keep && command.FailureCode == CollectorFailureCode.ChallengeRequired) toStatus = "needs_verification";
            else if (!keep && command.FailureCode == CollectorFailureCode.LoginRequired) toStatus = "expired";
            else if (command.FailureCode == CollectorFailureCode.RateLimited && profile.Status is "active" or "cooling") toStatus = "cooling";

            if (command.FailureCode == CollectorFailureCode.RateLimited && toStatus == "cooling")
            {
                var streak = await RateLimitStreakAsync(c, t, command.TenantId, profile.Id, ct) + 1;
                var cooldown = (long)Math.Min(platform.RateLimitCooldownBaseSeconds * Math.Pow(2, Math.Min(streak - 1, 20)),
                    platform.RateLimitCooldownMaxSeconds);
                await ExecuteAsync(c, t, """
                    UPDATE collector_session_profiles
                    SET cooldown_until = UTC_TIMESTAMP(6) + INTERVAL @seconds SECOND, status = 'cooling', updated_at = UTC_TIMESTAMP(6)
                    WHERE tenant_id = UUID_TO_BIN(@tenant_id) AND id = UUID_TO_BIN(@id);
                    """, ct, ("@seconds", cooldown), ("@tenant_id", tenant), ("@id", profile.Id.ToString()));
            }
            else
            {
                await ExecuteAsync(c, t, """
                    UPDATE collector_session_profiles
                    SET cooldown_until = CASE WHEN @status = 'cooling' THEN cooldown_until ELSE NULL END,
                        status = @status,
                        consecutive_challenges = consecutive_challenges + @increment,
                        updated_at = UTC_TIMESTAMP(6)
                    WHERE tenant_id = UUID_TO_BIN(@tenant_id) AND id = UUID_TO_BIN(@id);
                    """, ct, ("@status", toStatus), ("@increment", command.FailureCode == CollectorFailureCode.ChallengeRequired ? 1 : 0),
                    ("@tenant_id", tenant), ("@id", profile.Id.ToString()));
            }
            if (command.Phase == CollectorPhase.Probe)
            {
                // §12.4：在下一次领取或窗口结束前，不再作为其他账号的公平竞争者。
                await ExecuteAsync(c, t, """
                    UPDATE collector_session_profiles SET last_probe_failed_at = UTC_TIMESTAMP(6), updated_at = UTC_TIMESTAMP(6)
                    WHERE tenant_id = UUID_TO_BIN(@tenant_id) AND id = UUID_TO_BIN(@id);
                    """, ct, ("@tenant_id", tenant), ("@id", profile.Id.ToString()));
            }
            await InsertHealthEventAsync(c, t, new HealthEvent(command.TenantId, profile.Region, profile.PlatformCode, profile.Id,
                "failure_reported", profile.Status, toStatus, failureCode, PhaseValue(command.Phase), command.Task?.TaskId,
                command.Task?.LeaseVersion, command.NodeId), ct);

            CollectorTaskDisposition? disposition = null;
            if (task is not null && command.Task is { } reportedTask)
            {
                var current = task.Status is "leased" or "collecting" && task.LeaseVersion == reportedTask.LeaseVersion
                    && task.LeaseOwner == command.NodeId && task.LeaseProfileId == profile.Id;
                if (current)
                {
                    var exhausted = task.FailureCount + 1 >= platform.MaxTaskFailures;
                    await ExecuteAsync(c, t, """
                        UPDATE collector_tasks
                        SET status = @status, lease_owner = NULL, lease_until = NULL, failure_count = failure_count + 1,
                            last_error_code = @code, last_error_message = @message,
                            next_attempt_at = CASE WHEN @exhausted THEN next_attempt_at ELSE UTC_TIMESTAMP(6) + INTERVAL @backoff SECOND END,
                            updated_at = UTC_TIMESTAMP(6)
                        WHERE tenant_id = UUID_TO_BIN(@tenant_id) AND id = UUID_TO_BIN(@id);
                        """, ct, ("@status", exhausted ? "terminal_failed" : "retryable_failed"), ("@code", failureCode),
                        ("@message", FailureMessage), ("@exhausted", exhausted), ("@backoff", platform.RetryBackoffSeconds),
                        ("@tenant_id", tenant), ("@id", task.Id.ToString()));
                    await UpdateRunStatusAsync(c, t, command.TenantId, task.Id, ct);
                    disposition = exhausted ? CollectorTaskDisposition.Failed : CollectorTaskDisposition.Requeued;
                }
                else disposition = CollectorTaskDisposition.Stale;
                await ExecuteAsync(c, t, """
                    INSERT INTO collector_attempts (id, tenant_id, collector_task_id, lease_version, node_id, region, event_kind,
                        error_code, session_profile_id, created_at)
                    VALUES (UUID_TO_BIN(@attempt_id), UUID_TO_BIN(@tenant_id), UUID_TO_BIN(@task_id), @version, @node_id, @region, 'failed',
                        @code, UUID_TO_BIN(@profile_id), UTC_TIMESTAMP(6));
                    """, ct, ("@attempt_id", Guid.NewGuid().ToString()), ("@tenant_id", tenant), ("@task_id", task.Id.ToString()),
                    ("@version", reportedTask.LeaseVersion), ("@node_id", command.NodeId), ("@region", profile.Region),
                    ("@code", failureCode), ("@profile_id", profile.Id.ToString()));
            }

            var paused = platform.Paused;
            if (command.FailureCode == CollectorFailureCode.ChallengeRequired && !paused)
            {
                var challenged = await QuerySingleAsync(c, t, """
                    SELECT COUNT(DISTINCT e.session_profile_id)
                    FROM collector_session_health_events e
                    WHERE e.tenant_id = UUID_TO_BIN(@tenant_id) AND e.region = @region AND e.platform_code = @platform
                      AND e.event_kind = 'failure_reported' AND e.failure_code = 'challenge_required'
                      AND e.created_at > UTC_TIMESTAMP(6) - INTERVAL @window MINUTE
                      AND e.created_at > COALESCE((
                          SELECT MAX(r.created_at) FROM collector_session_health_events r
                          WHERE r.tenant_id = UUID_TO_BIN(@tenant_id) AND r.region = @region AND r.platform_code = @platform
                            AND r.event_kind = 'breaker_reset'), '1000-01-01');
                    """, ct, ("@tenant_id", tenant), ("@region", profile.Region), ("@platform", profile.PlatformCode),
                    ("@window", platform.BreakerWindowMinutes));
                if (challenged is not null && int.Parse(challenged[0], CultureInfo.InvariantCulture) >= platform.BreakerProfileThreshold)
                {
                    await ExecuteAsync(c, t, """
                        UPDATE collector_platform_dispatch_states
                        SET paused_at = UTC_TIMESTAMP(6), pause_reason = 'challenge_breaker', updated_at = UTC_TIMESTAMP(6)
                        WHERE tenant_id = UUID_TO_BIN(@tenant_id) AND region = @region AND platform_code = @platform;
                        """, ct, ("@tenant_id", tenant), ("@region", profile.Region), ("@platform", profile.PlatformCode));
                    await InsertHealthEventAsync(c, t, new HealthEvent(command.TenantId, profile.Region, profile.PlatformCode, profile.Id,
                        "breaker_tripped", toStatus, toStatus, NodeId: command.NodeId), ct);
                    paused = true;
                }
            }
            return new CollectorFailureResult(Statuses[toStatus], disposition, paused);
        }, cancellationToken);

    private sealed record ReportedTaskRow(Guid Id, string Status, int LeaseVersion, string LeaseOwner, Guid? LeaseProfileId, int FailureCount);

    /// <summary>第三把锁：上报的任务。任务不存在 404；不属于该账号的区域或平台 403。</summary>
    private static async Task<ReportedTaskRow> LockReportedTaskAsync(MySqlConnection c, MySqlTransaction t, Guid tenantId, Guid taskId,
        ProfileRow profile, CancellationToken ct)
    {
        var row = await QuerySingleAsync(c, t, """
            SELECT BIN_TO_UUID(id), status, region, platform_code, lease_version, lease_owner, BIN_TO_UUID(lease_session_profile_id), failure_count
            FROM collector_tasks WHERE tenant_id = UUID_TO_BIN(@tenant_id) AND id = UUID_TO_BIN(@id) FOR UPDATE;
            """, ct, ("@tenant_id", tenantId.ToString()), ("@id", taskId.ToString()))
            ?? throw new CollectorStoreException("TASK_NOT_FOUND", "Collector task was not found.", 404);
        if (row[2] != profile.Region || row[3] != profile.PlatformCode) throw Forbidden();
        return new ReportedTaskRow(Guid.Parse(row[0]), row[1], int.Parse(row[4], CultureInfo.InvariantCulture), row[5],
            row[6] == "" ? null : Guid.Parse(row[6]), int.Parse(row[7], CultureInfo.InvariantCulture));
    }

    /// <summary>自账号上次证明健康（accepted 提交或 verified）以来的限流次数。</summary>
    private static async Task<int> RateLimitStreakAsync(MySqlConnection c, MySqlTransaction t, Guid tenantId, Guid profileId, CancellationToken ct)
    {
        var row = await QuerySingleAsync(c, t, """
            SELECT COUNT(*) FROM collector_session_health_events e
            WHERE e.tenant_id = UUID_TO_BIN(@tenant_id) AND e.session_profile_id = UUID_TO_BIN(@id)
              AND e.event_kind = 'failure_reported' AND e.failure_code = 'rate_limited'
              AND e.created_at > GREATEST(
                  COALESCE((SELECT MAX(a.created_at) FROM collector_attempts a
                            WHERE a.tenant_id = UUID_TO_BIN(@tenant_id) AND a.session_profile_id = UUID_TO_BIN(@id)
                              AND a.event_kind = 'accepted'), '1000-01-01'),
                  COALESCE((SELECT MAX(v.created_at) FROM collector_session_health_events v
                            WHERE v.tenant_id = UUID_TO_BIN(@tenant_id) AND v.session_profile_id = UUID_TO_BIN(@id)
                              AND v.event_kind = 'verified'), '1000-01-01'));
            """, ct, ("@tenant_id", tenantId.ToString()), ("@id", profileId.ToString()));
        return row is null ? 0 : int.Parse(row[0], CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public Task<CollectorSessionEventResult> RecordSessionEventAsync(CollectorSessionEventCommand command, CancellationToken cancellationToken = default)
        => InTransactionAsync(async (c, t, ct) =>
        {
            var profile = await LockProfileAsync(c, t, command.TenantId, command.SessionProfileId, RegionCode(command.Region), ct);
            if (command.Task is { } reported) await LockReportedTaskAsync(c, t, command.TenantId, reported.TaskId, profile, ct);
            var toStatus = profile.Status;
            if (command.EventKind == CollectorSessionEventKind.Verified)
            {
                // 只在运维 probe 为 ready 后上报；停用账号保持停用。
                toStatus = profile.Status == "disabled" ? "disabled" : "active";
                await ExecuteAsync(c, t, """
                    UPDATE collector_session_profiles
                    SET status = @status, cooldown_until = NULL, consecutive_challenges = 0, updated_at = UTC_TIMESTAMP(6)
                    WHERE tenant_id = UUID_TO_BIN(@tenant_id) AND id = UUID_TO_BIN(@id);
                    """, ct, ("@status", toStatus), ("@tenant_id", command.TenantId.ToString()), ("@id", profile.Id.ToString()));
            }
            await InsertHealthEventAsync(c, t, new HealthEvent(command.TenantId, profile.Region, profile.PlatformCode, profile.Id,
                EventKindValue(command.EventKind), profile.Status, toStatus, Phase: PhaseValue(command.Phase),
                TaskId: command.Task?.TaskId, LeaseVersion: command.Task?.LeaseVersion, NodeId: command.NodeId), ct);
            return new CollectorSessionEventResult(Statuses[toStatus]);
        }, cancellationToken);

    /// <inheritdoc />
    public Task<Guid> RegisterProfileAsync(RegisterCollectorSessionProfileCommand command, CancellationToken cancellationToken = default)
    {
        var label = command.Label?.Trim() ?? "";
        if (label.Length is < 1 or > 128)
            throw new CollectorStoreException("INVALID_SESSION_PROFILE", "label must be 1-128 characters.", 400);
        var hourly = command.HourlyQuota ?? 4;
        var daily = command.DailyQuota ?? 30;
        var interval = command.MinIntervalSeconds ?? 600;
        if (hourly is < 1 or > 1000 || daily is < 1 or > 10000 || interval is < 0 or > 86400)
            throw new CollectorStoreException("INVALID_SESSION_PROFILE",
                "hourlyQuota must be 1-1000, dailyQuota 1-10000 and minIntervalSeconds 0-86400.", 400);
        return InTransactionAsync(async (c, t, ct) =>
        {
            await RequireTenantAndPlatformAsync(c, t, command.TenantId, command.Region, command.PlatformCode, ct);
            var id = Guid.NewGuid();
            try
            {
                await ExecuteAsync(c, t, """
                    INSERT INTO collector_session_profiles (id, tenant_id, platform_code, region, label, hourly_quota, daily_quota,
                        min_interval_seconds, created_at, updated_at)
                    VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant_id), @platform, @region, @label, @hourly, @daily, @interval,
                        UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                    """, ct, ("@id", id.ToString()), ("@tenant_id", command.TenantId.ToString()), ("@platform", command.PlatformCode),
                    ("@region", command.Region), ("@label", label), ("@hourly", hourly), ("@daily", daily), ("@interval", interval));
            }
            catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
            {
                throw new CollectorStoreException("SESSION_PROFILE_LABEL_EXISTS", "A session profile with this label already exists for the platform.", 409);
            }
            await InsertHealthEventAsync(c, t, new HealthEvent(command.TenantId, command.Region, command.PlatformCode, id,
                "profile_registered", "active", "active", OperatorUserId: command.OperatorUserId), ct);
            return id;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task SetProfileStatusAsync(Guid tenantId, Guid sessionProfileId, CollectorSessionProfileStatus status, Guid? operatorUserId,
        CancellationToken cancellationToken = default)
    {
        if (status is not (CollectorSessionProfileStatus.Active or CollectorSessionProfileStatus.Disabled))
            throw new CollectorStoreException("INVALID_SESSION_PROFILE_STATUS", "Operators may only set active or disabled.", 400);
        return InTransactionAsync<bool>(async (c, t, ct) =>
        {
            var row = await QuerySingleAsync(c, t, """
                SELECT region, platform_code, status FROM collector_session_profiles
                WHERE tenant_id = UUID_TO_BIN(@tenant_id) AND id = UUID_TO_BIN(@id) FOR UPDATE;
                """, ct, ("@tenant_id", tenantId.ToString()), ("@id", sessionProfileId.ToString()))
                ?? throw new CollectorStoreException("SESSION_PROFILE_NOT_FOUND", "Session profile was not found.", 404);
            var value = StatusCode(status);
            await ExecuteAsync(c, t, """
                UPDATE collector_session_profiles
                SET status = @status, cooldown_until = NULL,
                    consecutive_challenges = CASE WHEN @status = 'active' THEN 0 ELSE consecutive_challenges END,
                    updated_at = UTC_TIMESTAMP(6)
                WHERE tenant_id = UUID_TO_BIN(@tenant_id) AND id = UUID_TO_BIN(@id);
                """, ct, ("@status", value), ("@tenant_id", tenantId.ToString()), ("@id", sessionProfileId.ToString()));
            await InsertHealthEventAsync(c, t, new HealthEvent(tenantId, row[0], row[1], sessionProfileId, "status_changed_by_operator",
                row[2], value, OperatorUserId: operatorUserId), ct);
            return true;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task ConfigurePlatformAsync(ConfigureCollectorPlatformDispatchCommand command, CancellationToken cancellationToken = default)
    {
        static bool Out(int? value, int min, int max) => value is { } v && (v < min || v > max);
        if (Out(command.MinIntervalSeconds, 0, 86400) || Out(command.DailyCap, 1, 100000) || Out(command.BreakerProfileThreshold, 1, 100)
            || Out(command.BreakerWindowMinutes, 1, 10080) || Out(command.RetryBackoffSeconds, 0, 604800) || Out(command.MaxTaskFailures, 1, 100)
            || Out(command.RateLimitCooldownBaseSeconds, 1, 604800) || Out(command.RateLimitCooldownMaxSeconds, 1, 604800)
            || Out(command.ProbeFailureOfflineMinutes, 1, 1440))
            throw new CollectorStoreException("INVALID_PLATFORM_DISPATCH", "A platform dispatch setting is out of range.", 400);
        return InTransactionAsync<bool>(async (c, t, ct) =>
        {
            await RequireTenantAndPlatformAsync(c, t, command.TenantId, command.Region, command.PlatformCode, ct);
            await LockPlatformAsync(c, t, command.TenantId, command.Region, command.PlatformCode, ct);
            try
            {
                await ExecuteAsync(c, t, """
                    UPDATE collector_platform_dispatch_states
                    SET min_interval_seconds = COALESCE(@min_interval, min_interval_seconds),
                        daily_cap = COALESCE(@daily_cap, daily_cap),
                        breaker_profile_threshold = COALESCE(@threshold, breaker_profile_threshold),
                        breaker_window_minutes = COALESCE(@window, breaker_window_minutes),
                        retry_backoff_seconds = COALESCE(@backoff, retry_backoff_seconds),
                        max_task_failures = COALESCE(@max_failures, max_task_failures),
                        rate_limit_cooldown_base_seconds = COALESCE(@cooldown_base, rate_limit_cooldown_base_seconds),
                        rate_limit_cooldown_max_seconds = COALESCE(@cooldown_max, rate_limit_cooldown_max_seconds),
                        probe_failure_offline_minutes = COALESCE(@probe_offline, probe_failure_offline_minutes),
                        updated_at = UTC_TIMESTAMP(6)
                    WHERE tenant_id = UUID_TO_BIN(@tenant_id) AND region = @region AND platform_code = @platform;
                    """, ct, ("@min_interval", command.MinIntervalSeconds), ("@daily_cap", command.DailyCap),
                    ("@threshold", command.BreakerProfileThreshold), ("@window", command.BreakerWindowMinutes),
                    ("@backoff", command.RetryBackoffSeconds), ("@max_failures", command.MaxTaskFailures),
                    ("@cooldown_base", command.RateLimitCooldownBaseSeconds), ("@cooldown_max", command.RateLimitCooldownMaxSeconds),
                    ("@probe_offline", command.ProbeFailureOfflineMinutes), ("@tenant_id", command.TenantId.ToString()),
                    ("@region", command.Region), ("@platform", command.PlatformCode));
            }
            catch (MySqlException exception) when (exception.Number == 3819) // ER_CHECK_CONSTRAINT_VIOLATED
            {
                throw new CollectorStoreException("INVALID_PLATFORM_DISPATCH",
                    "rateLimitCooldownMaxSeconds must be at least rateLimitCooldownBaseSeconds.", 400);
            }
            await InsertHealthEventAsync(c, t, new HealthEvent(command.TenantId, command.Region, command.PlatformCode, null,
                "platform_settings_changed", null, null, OperatorUserId: command.OperatorUserId), ct);
            return true;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> ResumePlatformAsync(Guid tenantId, string region, string platformCode, Guid? operatorUserId,
        CancellationToken cancellationToken = default)
        => InTransactionAsync(async (c, t, ct) =>
        {
            await RequireTenantAndPlatformAsync(c, t, tenantId, region, platformCode, ct);
            var state = await LockPlatformAsync(c, t, tenantId, region, platformCode, ct);
            if (!state.Paused) return false;
            await ExecuteAsync(c, t, """
                UPDATE collector_platform_dispatch_states SET paused_at = NULL, pause_reason = NULL, updated_at = UTC_TIMESTAMP(6)
                WHERE tenant_id = UUID_TO_BIN(@tenant_id) AND region = @region AND platform_code = @platform;
                """, ct, ("@tenant_id", tenantId.ToString()), ("@region", region), ("@platform", platformCode));
            await InsertHealthEventAsync(c, t, new HealthEvent(tenantId, region, platformCode, null, "breaker_reset", null, null,
                OperatorUserId: operatorUserId), ct);
            return true;
        }, cancellationToken);

    private static async Task RequireTenantAndPlatformAsync(MySqlConnection c, MySqlTransaction t, Guid tenantId, string region,
        string platformCode, CancellationToken ct)
    {
        if (await QuerySingleAsync(c, t, "SELECT 1 FROM tenants WHERE id = UUID_TO_BIN(@id);", ct, ("@id", tenantId.ToString())) is null)
            throw new CollectorStoreException("TENANT_NOT_FOUND", "Tenant was not found.", 404);
        if (await QuerySingleAsync(c, t, "SELECT 1 FROM collector_platforms WHERE code = @code AND region = @region;", ct,
                ("@code", platformCode), ("@region", region)) is null)
            throw new CollectorStoreException("INVALID_PLATFORM", "Platform code and region do not match a collector platform.", 400);
    }

    // ISO 8601 UTC，毫秒精度并以 Z 结尾，与 Collector 控制面其他时间字段一致。
    private static string Iso(string column)
        => $"CASE WHEN {column} IS NULL THEN NULL ELSE CONCAT(DATE_FORMAT({column}, '%Y-%m-%dT%H:%i:%s.'), LPAD(FLOOR(MICROSECOND({column}) / 1000), 3, '0'), 'Z') END";

    /// <inheritdoc />
    public Task<IReadOnlyList<CollectorSessionProfileMetrics>> ListProfileMetricsAsync(Guid tenantId, string? platformCode, string? region,
        CancellationToken cancellationToken = default)
        => InTransactionAsync<IReadOnlyList<CollectorSessionProfileMetrics>>(async (c, t, ct) =>
        {
            // 验证码次数：每次等待开始，加上未结束某次等待的 challenge_required 上报（一次超时的等待只计一次，probe 阶段单独计）。
            var rows = await QueryRowsAsync(c, t, $"""
                SELECT BIN_TO_UUID(p.id), p.label, p.platform_code, p.region, p.status, {Iso("p.cooldown_until")},
                       p.hourly_quota, p.daily_quota, p.min_interval_seconds, p.consecutive_challenges,
                       {Iso("p.last_leased_at")}, {Iso("p.last_seen_at")},
                       (SELECT COUNT(*) FROM collector_attempts a WHERE a.tenant_id = p.tenant_id AND a.session_profile_id = p.id
                          AND a.event_kind = 'leased' AND a.created_at > UTC_TIMESTAMP(6) - INTERVAL 1 HOUR),
                       (SELECT COUNT(*) FROM collector_attempts a WHERE a.tenant_id = p.tenant_id AND a.session_profile_id = p.id
                          AND a.event_kind = 'leased' AND a.created_at > UTC_TIMESTAMP(6) - INTERVAL 24 HOUR),
                       (SELECT COUNT(*) FROM collector_attempts a WHERE a.tenant_id = p.tenant_id AND a.session_profile_id = p.id
                          AND a.event_kind = 'accepted' AND a.created_at > UTC_TIMESTAMP(6) - INTERVAL 24 HOUR),
                       (SELECT COUNT(*) FROM collector_session_health_events e
                        WHERE e.tenant_id = p.tenant_id AND e.session_profile_id = p.id
                          AND e.created_at > UTC_TIMESTAMP(6) - INTERVAL 24 HOUR
                          AND (e.event_kind = 'challenge_wait_started'
                               OR (e.event_kind = 'failure_reported' AND e.failure_code = 'challenge_required'
                                   AND NOT COALESCE((
                                       SELECT prior.event_kind = 'challenge_wait_started'
                                       FROM collector_session_health_events prior
                                       WHERE e.collector_task_id IS NOT NULL
                                         AND prior.tenant_id = e.tenant_id AND prior.session_profile_id = e.session_profile_id
                                         AND prior.collector_task_id = e.collector_task_id AND prior.lease_version = e.lease_version
                                         AND prior.event_kind IN ('challenge_wait_started', 'challenge_cleared')
                                         AND prior.created_at <= e.created_at
                                       ORDER BY prior.created_at DESC
                                       LIMIT 1), FALSE)))),
                       {Iso("(SELECT MAX(e.created_at) FROM collector_session_health_events e WHERE e.tenant_id = p.tenant_id AND e.session_profile_id = p.id AND (e.event_kind = 'challenge_wait_started' OR (e.event_kind = 'failure_reported' AND e.failure_code = 'challenge_required')))")},
                       {Iso("p.last_probe_failed_at")},
                       (p.last_probe_failed_at IS NOT NULL
                        AND p.last_probe_failed_at > UTC_TIMESTAMP(6) - INTERVAL COALESCE((
                            SELECT s.probe_failure_offline_minutes FROM collector_platform_dispatch_states s
                            WHERE s.tenant_id = p.tenant_id AND s.region = p.region AND s.platform_code = p.platform_code),
                            {DefaultProbeFailureOfflineMinutes}) MINUTE
                        AND (p.last_leased_at IS NULL OR p.last_leased_at < p.last_probe_failed_at))
                FROM collector_session_profiles p
                WHERE p.tenant_id = UUID_TO_BIN(@tenant_id)
                  AND (@platform IS NULL OR p.platform_code = @platform)
                  AND (@region IS NULL OR p.region = @region)
                ORDER BY p.region, p.platform_code, p.label, p.id;
                """, ct, ("@tenant_id", tenantId.ToString()), ("@platform", platformCode), ("@region", region));
            return rows.Select(row =>
            {
                int I(int index) => int.Parse(row[index], CultureInfo.InvariantCulture);
                string? N(int index) => row[index] == "" ? null : row[index];
                var leasedDay = I(13);
                var challenges = I(15);
                return new CollectorSessionProfileMetrics(Guid.Parse(row[0]), row[1], row[2], row[3], Statuses[row[4]], N(5),
                    I(6), I(7), I(8), I(9), N(10), N(11), I(12), leasedDay, I(14), challenges,
                    leasedDay == 0 ? null : (double)challenges / leasedDay, N(16), N(17), ParseBool(row[18]));
            }).ToArray();
        }, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<CollectorPlatformDispatchStateView>> ListPlatformStatesAsync(Guid tenantId, CancellationToken cancellationToken = default)
        => InTransactionAsync<IReadOnlyList<CollectorPlatformDispatchStateView>>(async (c, t, ct) =>
        {
            var rows = await QueryRowsAsync(c, t, $"""
                SELECT region, platform_code, min_interval_seconds, daily_cap, {Iso("last_dispatched_at")}, {Iso("paused_at")}, pause_reason,
                       breaker_profile_threshold, breaker_window_minutes, retry_backoff_seconds, max_task_failures,
                       rate_limit_cooldown_base_seconds, rate_limit_cooldown_max_seconds, probe_failure_offline_minutes
                FROM collector_platform_dispatch_states
                WHERE tenant_id = UUID_TO_BIN(@tenant_id)
                ORDER BY region, platform_code;
                """, ct, ("@tenant_id", tenantId.ToString()));
            return rows.Select(row =>
            {
                int I(int index) => int.Parse(row[index], CultureInfo.InvariantCulture);
                string? N(int index) => row[index] == "" ? null : row[index];
                return new CollectorPlatformDispatchStateView(row[0], row[1], I(2), I(3), N(4), N(5), N(6), I(7), I(8), I(9), I(10),
                    I(11), I(12), I(13));
            }).ToArray();
        }, cancellationToken);

    /// <summary>
    /// READ COMMITTED 事务；InnoDB 死锁（1213）整体重试，最多 3 次。缺少 035 的表时返回 503，提示先执行迁移。
    /// </summary>
    private async Task<T> InTransactionAsync<T>(Func<MySqlConnection, MySqlTransaction, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var connection = database.CreateConnection();
                await connection.OpenAsync(cancellationToken);
                await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
                var result = await work(connection, transaction, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
            catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.LockDeadlock && attempt < 3)
            {
            }
            catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.NoSuchTable)
            {
                throw new CollectorStoreException("COLLECTOR_POOL_MIGRATION_REQUIRED", "Collector account pool requires migration 035.", 503);
            }
        }
    }
}
