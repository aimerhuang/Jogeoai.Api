using System.Globalization;
using Jogeoai.Domain;
using MySqlConnector;
using static Jogeoai.Infrastructure.DomesticCollectorStore;

namespace Jogeoai.Infrastructure;

/// <summary>
/// 账号池派发判定（参考 Ai-visiblility-collector session-profiles.ts，设计 §3 / §12.2 / §12.4），由带账号的租约和
/// eligibility 预检共用。加锁顺序固定为账号 → 平台 → 任务；调用方使用 READ COMMITTED，平台锁之后的计数读取最新已提交数据。
/// 时间一律取数据库 UTC_TIMESTAMP(6)，秒数在 SQL 内以微秒计算，不经过 C# 的时间解析。
/// </summary>
internal static class CollectorSessionDispatch
{
    /// <summary>账号最近一次领取 / 预检后视为在线的秒数。</summary>
    internal const int OnlineWindowSeconds = 300;
    internal const int RetryMinSeconds = 30;
    internal const int RetryMaxSeconds = 120;

    // collector_platform_dispatch_states 的列默认值（迁移 035）；只读预检不建行，缺行时按这些值判断。
    private const int DefaultPlatformMinIntervalSeconds = 120;
    private const int DefaultPlatformDailyCap = 120;
    internal const int DefaultProbeFailureOfflineMinutes = 15;

    // 北京时间自然日的起点（UTC）；中国无夏令时，不依赖 MySQL 时区表。
    internal const string ShanghaiDayStartSql = "(TIMESTAMP(DATE(UTC_TIMESTAMP(6) + INTERVAL 8 HOUR)) - INTERVAL 8 HOUR)";

    internal static readonly string[] OperatorStopStatuses = ["needs_verification", "expired", "disabled"];

    internal sealed record ProfileRow(
        Guid Id, string PlatformCode, string Region, string Status, int HourlyQuota, int DailyQuota, bool CooldownDue);

    internal sealed record PlatformState(
        int MinIntervalSeconds, int DailyCap, bool Paused, int BreakerProfileThreshold, int BreakerWindowMinutes,
        int RetryBackoffSeconds, int MaxTaskFailures, int RateLimitCooldownBaseSeconds, int RateLimitCooldownMaxSeconds,
        int ProbeFailureOfflineMinutes);

    internal enum BlockerKind { OperatorStop, Profile, Fairness, PlatformPaused, PlatformGate }

    internal sealed record Blocker(BlockerKind Kind, string? Status = null, double IntervalRemainingSeconds = 0, double? SecondsUntilCapReset = null);

    internal sealed record Evaluation(ProfileRow Profile, Blocker? Blocker);

    internal static CollectorStoreException Forbidden()
        => new("SESSION_PROFILE_FORBIDDEN", "Session profile is not authorized for this tenant, region or platform.", 403);

    internal static string RegionCode(CollectorRegion region) => region == CollectorRegion.Cn ? "cn" : "overseas";

    internal static double ParseDouble(string value)
        => string.IsNullOrEmpty(value) ? 0 : double.Parse(value, CultureInfo.InvariantCulture);

    internal static bool ParseBool(string value) => value is "1" or "True" or "true";

    /// <summary>第一把锁：账号行。账号不存在、跨租户或跨区域一律 403，不区分原因。</summary>
    internal static async Task<ProfileRow> LockProfileAsync(MySqlConnection c, MySqlTransaction t, Guid tenantId, Guid profileId,
        string region, CancellationToken ct)
    {
        var row = await QuerySingleAsync(c, t, """
            SELECT BIN_TO_UUID(id), platform_code, region, status, hourly_quota, daily_quota,
                   (status = 'cooling' AND cooldown_until <= UTC_TIMESTAMP(6))
            FROM collector_session_profiles
            WHERE tenant_id = UUID_TO_BIN(@tenant_id) AND id = UUID_TO_BIN(@id)
            FOR UPDATE;
            """, ct, ("@tenant_id", tenantId.ToString()), ("@id", profileId.ToString()));
        if (row is null || !string.Equals(row[2], region, StringComparison.Ordinal)) throw Forbidden();
        return new ProfileRow(Guid.Parse(row[0]), row[1], row[2], row[3], int.Parse(row[4], CultureInfo.InvariantCulture),
            int.Parse(row[5], CultureInfo.InvariantCulture), ParseBool(row[6]));
    }

    /// <summary>第二把锁：平台行；首次使用时按默认值建行。</summary>
    internal static async Task<PlatformState> LockPlatformAsync(MySqlConnection c, MySqlTransaction t, Guid tenantId, string region,
        string platformCode, CancellationToken ct)
    {
        // 不用 INSERT IGNORE：它会把外键错误降级为警告。
        await ExecuteAsync(c, t, """
            INSERT INTO collector_platform_dispatch_states (tenant_id, region, platform_code, created_at, updated_at)
            VALUES (UUID_TO_BIN(@tenant_id), @region, @platform, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE tenant_id = tenant_id;
            """, ct, ("@tenant_id", tenantId.ToString()), ("@region", region), ("@platform", platformCode));
        var row = await QuerySingleAsync(c, t, PlatformSelectSql + " FOR UPDATE;", ct,
            ("@tenant_id", tenantId.ToString()), ("@region", region), ("@platform", platformCode))
            ?? throw new CollectorStoreException("PLATFORM_DISPATCH_STATE_MISSING", "Platform dispatch state was not found.", 500);
        return ParsePlatform(row);
    }

    private const string PlatformSelectSql = """
        SELECT min_interval_seconds, daily_cap, paused_at IS NOT NULL, breaker_profile_threshold, breaker_window_minutes,
               retry_backoff_seconds, max_task_failures, rate_limit_cooldown_base_seconds, rate_limit_cooldown_max_seconds,
               probe_failure_offline_minutes
        FROM collector_platform_dispatch_states
        WHERE tenant_id = UUID_TO_BIN(@tenant_id) AND region = @region AND platform_code = @platform
        """;

    private static PlatformState ParsePlatform(string[] row)
    {
        int I(int index) => int.Parse(row[index], CultureInfo.InvariantCulture);
        return new PlatformState(I(0), I(1), ParseBool(row[2]), I(3), I(4), I(5), I(6), I(7), I(8), I(9));
    }

    /// <summary>只读预检用：不建行、不加锁；缺行时使用迁移默认值。</summary>
    private static async Task<PlatformState> ReadPlatformAsync(MySqlConnection c, MySqlTransaction t, Guid tenantId, string region,
        string platformCode, CancellationToken ct)
    {
        var row = await QuerySingleAsync(c, t, PlatformSelectSql + ";", ct,
            ("@tenant_id", tenantId.ToString()), ("@region", region), ("@platform", platformCode));
        return row is null
            ? new PlatformState(DefaultPlatformMinIntervalSeconds, DefaultPlatformDailyCap, false, 2, 60, 1800, 3, 1800, 28800,
                DefaultProbeFailureOfflineMinutes)
            : ParsePlatform(row);
    }

    /// <summary>
    /// 设计 §3 第 2–5 步。lease 模式刷新在线时间、把到期的冷却恢复为 active（写 cooldown_expired）并锁平台行；
    /// eligibility 模式只刷新 last_seen_at。选取任务（第 6 步）由调用方完成。
    /// </summary>
    internal static async Task<Evaluation> EvaluateAsync(MySqlConnection c, MySqlTransaction t, Guid tenantId, string nodeId,
        string region, Guid profileId, IReadOnlyCollection<string> platformCodes, bool leaseMode, CancellationToken ct)
    {
        var profile = await LockProfileAsync(c, t, tenantId, profileId, region, ct);
        if (!platformCodes.Contains(profile.PlatformCode, StringComparer.Ordinal)) throw Forbidden();
        if (leaseMode && profile.CooldownDue)
        {
            await ExecuteAsync(c, t, """
                UPDATE collector_session_profiles
                SET last_seen_at = UTC_TIMESTAMP(6), status = 'active', cooldown_until = NULL, updated_at = UTC_TIMESTAMP(6)
                WHERE tenant_id = UUID_TO_BIN(@tenant_id) AND id = UUID_TO_BIN(@id);
                """, ct, ("@tenant_id", tenantId.ToString()), ("@id", profile.Id.ToString()));
            await InsertHealthEventAsync(c, t, new HealthEvent(tenantId, profile.Region, profile.PlatformCode, profile.Id,
                "cooldown_expired", "cooling", "active", NodeId: nodeId), ct);
            profile = profile with { Status = "active", CooldownDue = false };
        }
        else
        {
            await ExecuteAsync(c, t, """
                UPDATE collector_session_profiles SET last_seen_at = UTC_TIMESTAMP(6)
                WHERE tenant_id = UUID_TO_BIN(@tenant_id) AND id = UUID_TO_BIN(@id);
                """, ct, ("@tenant_id", tenantId.ToString()), ("@id", profile.Id.ToString()));
        }

        var platform = leaseMode
            ? await LockPlatformAsync(c, t, tenantId, profile.Region, profile.PlatformCode, ct)
            : await ReadPlatformAsync(c, t, tenantId, profile.Region, profile.PlatformCode, ct);

        if (OperatorStopStatuses.Contains(profile.Status, StringComparer.Ordinal))
            return new Evaluation(profile, new Blocker(BlockerKind.OperatorStop, profile.Status));

        // 公平比较在平台锁之后读取，READ COMMITTED 下能看到先排队者刚提交的 last_leased_at 与领取记录。
        var rows = await QueryRowsAsync(c, t, """
            SELECT BIN_TO_UUID(p.id),
                   COALESCE(TIMESTAMPDIFF(MICROSECOND, '1970-01-01', p.last_leased_at), -1),
                   ((p.status = 'active' OR (p.status = 'cooling' AND p.cooldown_until <= UTC_TIMESTAMP(6)))
                    AND (SELECT COUNT(*) FROM collector_attempts a
                         WHERE a.tenant_id = p.tenant_id AND a.session_profile_id = p.id AND a.event_kind = 'leased'
                           AND a.created_at > UTC_TIMESTAMP(6) - INTERVAL 1 HOUR) < p.hourly_quota
                    AND (SELECT COUNT(*) FROM collector_attempts a
                         WHERE a.tenant_id = p.tenant_id AND a.session_profile_id = p.id AND a.event_kind = 'leased'
                           AND a.created_at > UTC_TIMESTAMP(6) - INTERVAL 24 HOUR) < p.daily_quota
                    AND (p.last_leased_at IS NULL
                         OR p.last_leased_at + INTERVAL p.min_interval_seconds SECOND <= UTC_TIMESTAMP(6))),
                   (p.last_seen_at IS NOT NULL
                    AND p.last_seen_at > UTC_TIMESTAMP(6) - INTERVAL @online SECOND
                    -- §12.4：近期 probe 失败且之后未再领取的账号，不算其他账号的在线竞争者。
                    AND NOT (p.last_probe_failed_at IS NOT NULL
                             AND p.last_probe_failed_at > UTC_TIMESTAMP(6) - INTERVAL @offline MINUTE
                             AND (p.last_leased_at IS NULL OR p.last_leased_at < p.last_probe_failed_at)))
            FROM collector_session_profiles p
            WHERE p.tenant_id = UUID_TO_BIN(@tenant_id) AND p.region = @region AND p.platform_code = @platform;
            """, ct, ("@tenant_id", tenantId.ToString()), ("@region", profile.Region), ("@platform", profile.PlatformCode),
            ("@online", OnlineWindowSeconds), ("@offline", platform.ProbeFailureOfflineMinutes));
        var candidates = rows.Select(row => (Id: Guid.Parse(row[0]), LastLeased: long.Parse(row[1], CultureInfo.InvariantCulture),
            Eligible: ParseBool(row[2]), Online: ParseBool(row[3]))).ToArray();
        var self = candidates.FirstOrDefault(row => row.Id == profile.Id);
        if (self.Id == Guid.Empty || !self.Eligible) return new Evaluation(profile, new Blocker(BlockerKind.Profile));
        // online 只约束竞争者；规则从不阻塞请求方自身。未领取过（-1）的账号最先；相同时按 UUID 字符串排序，与参考一致。
        static bool Older((Guid Id, long LastLeased, bool Eligible, bool Online) left, (Guid Id, long LastLeased, bool Eligible, bool Online) right)
            => left.LastLeased != right.LastLeased
                ? left.LastLeased < right.LastLeased
                : string.CompareOrdinal(left.Id.ToString(), right.Id.ToString()) < 0;
        if (candidates.Any(row => row.Id != self.Id && row.Eligible && row.Online && Older(row, self)))
            return new Evaluation(profile, new Blocker(BlockerKind.Fairness));

        if (platform.Paused) return new Evaluation(profile, new Blocker(BlockerKind.PlatformPaused));
        var gate = await QuerySingleAsync(c, t, $"""
            SELECT COALESCE(GREATEST(0, TIMESTAMPDIFF(MICROSECOND, UTC_TIMESTAMP(6),
                       (SELECT s.last_dispatched_at FROM collector_platform_dispatch_states s
                        WHERE s.tenant_id = UUID_TO_BIN(@tenant_id) AND s.region = @region AND s.platform_code = @platform)
                       + INTERVAL @min_interval SECOND) / 1000000), 0),
                   (SELECT COUNT(*) FROM collector_attempts a
                    JOIN collector_tasks task ON task.id = a.collector_task_id
                    WHERE a.tenant_id = UUID_TO_BIN(@tenant_id) AND a.region = @region AND a.event_kind = 'leased'
                      AND task.platform_code = @platform AND task.channel = 'consumer_web' AND a.created_at >= {ShanghaiDayStartSql}),
                   TIMESTAMPDIFF(MICROSECOND, UTC_TIMESTAMP(6), {ShanghaiDayStartSql} + INTERVAL 1 DAY) / 1000000;
            """, ct, ("@tenant_id", tenantId.ToString()), ("@region", profile.Region), ("@platform", profile.PlatformCode),
            ("@min_interval", platform.MinIntervalSeconds))
            ?? throw new CollectorStoreException("PLATFORM_DISPATCH_STATE_MISSING", "Platform dispatch state was not found.", 500);
        var intervalRemaining = ParseDouble(gate[0]);
        var capReached = long.Parse(gate[1], CultureInfo.InvariantCulture) >= platform.DailyCap;
        if (intervalRemaining > 0 || capReached)
            return new Evaluation(profile, new Blocker(BlockerKind.PlatformGate, null, intervalRemaining,
                capReached ? ParseDouble(gate[2]) : null));
        return new Evaluation(profile, null);
    }

    /// <summary>某账号此刻可领取的任务条件；租约选取与预检共用。</summary>
    internal const string LeasableTaskPredicate = """
        tenant_id = UUID_TO_BIN(@tenant_id) AND region = @region AND platform_code = @platform
          AND channel = 'consumer_web'
          AND next_attempt_at <= UTC_TIMESTAMP(6)
          AND (status IN ('pending', 'retryable_failed') OR (status IN ('leased', 'collecting') AND lease_until <= UTC_TIMESTAMP(6)))
        """;

    /// <summary>带账号的租约（设计 §3 第 2–7 步）。返回授予的任务标识（至多 1 个）；任何“现在不行”都返回 null。</summary>
    internal static async Task<Guid?> LeaseForProfileAsync(MySqlConnection c, MySqlTransaction t, CollectorLeaseCommand command,
        Guid profileId, CancellationToken ct)
    {
        var region = RegionCode(command.Region);
        var (profile, blocker) = await EvaluateAsync(c, t, command.TenantId, command.NodeId, region, profileId,
            command.SupportedPlatformCodes.ToArray(), leaseMode: true, ct);
        if (blocker is not null) return null;
        var candidate = await QuerySingleAsync(c, t, $"""
            SELECT BIN_TO_UUID(id) FROM collector_tasks
            WHERE {LeasableTaskPredicate}
            ORDER BY next_attempt_at, created_at, id
            LIMIT 1
            FOR UPDATE SKIP LOCKED;
            """, ct, ("@tenant_id", command.TenantId.ToString()), ("@region", profile.Region), ("@platform", profile.PlatformCode));
        if (candidate is null) return null;
        var taskId = Guid.Parse(candidate[0]);
        await ExecuteAsync(c, t, """
            UPDATE collector_tasks
            SET status = 'leased', lease_version = lease_version + 1, lease_owner = @owner,
                lease_until = UTC_TIMESTAMP(6) + INTERVAL @seconds SECOND, lease_session_profile_id = UUID_TO_BIN(@profile_id),
                attempt_count = attempt_count + 1, started_at = COALESCE(started_at, UTC_TIMESTAMP(6)),
                updated_at = UTC_TIMESTAMP(6)
            WHERE id = UUID_TO_BIN(@id);
            """, ct, ("@owner", command.NodeId), ("@seconds", command.LeaseSeconds), ("@profile_id", profile.Id.ToString()),
            ("@id", taskId.ToString()));
        await ExecuteAsync(c, t, """
            INSERT INTO collector_attempts (id, tenant_id, collector_task_id, lease_version, node_id, region, event_kind, session_profile_id, created_at)
            SELECT UUID_TO_BIN(@attempt_id), tenant_id, id, lease_version, @node_id, region, 'leased', lease_session_profile_id, UTC_TIMESTAMP(6)
            FROM collector_tasks WHERE id = UUID_TO_BIN(@id);
            """, ct, ("@attempt_id", Guid.NewGuid().ToString()), ("@node_id", command.NodeId), ("@id", taskId.ToString()));
        await ExecuteAsync(c, t, """
            UPDATE collector_session_profiles SET last_leased_at = UTC_TIMESTAMP(6), updated_at = UTC_TIMESTAMP(6)
            WHERE tenant_id = UUID_TO_BIN(@tenant_id) AND id = UUID_TO_BIN(@id);
            """, ct, ("@tenant_id", command.TenantId.ToString()), ("@id", profile.Id.ToString()));
        await ExecuteAsync(c, t, """
            UPDATE collector_platform_dispatch_states SET last_dispatched_at = UTC_TIMESTAMP(6), updated_at = UTC_TIMESTAMP(6)
            WHERE tenant_id = UUID_TO_BIN(@tenant_id) AND region = @region AND platform_code = @platform;
            """, ct, ("@tenant_id", command.TenantId.ToString()), ("@region", profile.Region), ("@platform", profile.PlatformCode));
        return taskId;
    }

    /// <summary>租户在本区域、请求平台中登记过任何账号（含停用）时，不带账号的租约不再派发这些平台。</summary>
    internal static async Task<bool> TenantUsesProfilesAsync(MySqlConnection c, MySqlTransaction t, CollectorLeaseCommand command,
        CancellationToken ct)
    {
        var placeholders = string.Join(",", command.SupportedPlatformCodes.Select((_, i) => $"@platform{i}"));
        var parameters = new List<object> { ("@tenant_id", command.TenantId.ToString()), ("@region", RegionCode(command.Region)) };
        parameters.AddRange(command.SupportedPlatformCodes.Select((value, i) => (object)($"@platform{i}", value)));
        var row = await QuerySingleAsync(c, t, $"""
            SELECT 1 FROM collector_session_profiles
            WHERE tenant_id = UUID_TO_BIN(@tenant_id) AND region = @region AND platform_code IN ({placeholders})
            LIMIT 1;
            """, ct, parameters.ToArray());
        return row is not null;
    }

    internal static int ClampRetry(double? seconds)
        => seconds is not { } value || double.IsNaN(value) || double.IsInfinity(value)
            ? RetryMaxSeconds
            : (int)Math.Min(RetryMaxSeconds, Math.Max(RetryMinSeconds, Math.Ceiling(value)));

    /// <summary>健康事件（只追加）。</summary>
    internal sealed record HealthEvent(
        Guid TenantId, string Region, string PlatformCode, Guid? SessionProfileId, string EventKind, string? FromStatus, string? ToStatus,
        string? FailureCode = null, string? Phase = null, Guid? TaskId = null, int? LeaseVersion = null, string? NodeId = null,
        Guid? OperatorUserId = null);

    internal static Task<int> InsertHealthEventAsync(MySqlConnection c, MySqlTransaction t, HealthEvent e, CancellationToken ct)
        => ExecuteAsync(c, t, """
            INSERT INTO collector_session_health_events (
                id, tenant_id, region, platform_code, session_profile_id, event_kind, failure_code, phase,
                collector_task_id, lease_version, node_id, from_status, to_status, operator_user_id, created_at)
            VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant_id), @region, @platform, UUID_TO_BIN(@profile_id), @kind, @failure_code, @phase,
                UUID_TO_BIN(@task_id), @lease_version, @node_id, @from_status, @to_status, UUID_TO_BIN(@operator), UTC_TIMESTAMP(6));
            """, ct, ("@id", Guid.NewGuid().ToString()), ("@tenant_id", e.TenantId.ToString()), ("@region", e.Region),
            ("@platform", e.PlatformCode), ("@profile_id", e.SessionProfileId?.ToString()), ("@kind", e.EventKind),
            ("@failure_code", e.FailureCode), ("@phase", e.Phase), ("@task_id", e.TaskId?.ToString()),
            ("@lease_version", e.LeaseVersion), ("@node_id", e.NodeId), ("@from_status", e.FromStatus), ("@to_status", e.ToStatus),
            ("@operator", e.OperatorUserId?.ToString()));
}
