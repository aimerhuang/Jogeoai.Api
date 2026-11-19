using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using MySqlConnector;

namespace Jogeoai.Tests;

/// <summary>账号池隔离库用例；未设置 JOGEOAI_COLLECTOR_POOL_TEST_CONNECTION 时跳过。</summary>
public sealed class CollectorPoolMySqlFactAttribute : FactAttribute
{
    public CollectorPoolMySqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JOGEOAI_COLLECTOR_POOL_TEST_CONNECTION")))
            Skip = "需提供已执行 001–035 迁移、库名含 _test 的独立测试库 JOGEOAI_COLLECTOR_POOL_TEST_CONNECTION。";
    }
}

/// <summary>
/// 采集器账号池（迁移 035）对照参考设计 AC-1～AC-7、AC-11 与 eligibility 场景。每个用例使用新租户，平台闸门按租户隔离；
/// 时间推进通过回拨数据库中的时间戳完成，不依赖 sleep。
/// </summary>
public sealed class CollectorSessionPoolSqlTests
{
    [CollectorPoolMySqlFact]
    public async Task Ac1TwoOnlineProfilesRotateLeastRecentlyUsedWithinQuotaAndInterval()
    {
        var f = await PoolFixture.CreateAsync(6);
        await f.PlatformAsync(minInterval: 0);
        var a = await f.RegisterAsync("doubao-a", hourly: 2, interval: 600);
        var b = await f.RegisterAsync("doubao-b", hourly: 2, interval: 600);
        await f.EligibilityAsync(a);
        await f.EligibilityAsync(b); // 两个账号都在线
        // 都未领取过时按 UUID 字符串排序，较小者先领。
        var (first, second) = string.CompareOrdinal(a.ToString(), b.ToString()) < 0 ? (a, b) : (b, a);

        Assert.Empty(await f.LeaseAsync(second));
        var leased = Assert.Single(await f.LeaseAsync(first));
        Assert.Equal(first, leased.SessionProfileId);
        Assert.Empty(await f.LeaseAsync(first)); // 最小间隔且 second 更久未用
        Assert.Equal(second, Assert.Single(await f.LeaseAsync(second)).SessionProfileId);
        Assert.Empty(await f.LeaseAsync(first));
        Assert.Empty(await f.LeaseAsync(second));

        await f.AgeProfilesAsync(601);
        Assert.Empty(await f.LeaseAsync(second)); // first 更久未用，轮到 first
        Assert.Single(await f.LeaseAsync(first));
        Assert.Single(await f.LeaseAsync(second));

        // 每个账号 1 小时内已领取 2 次（= hourly_quota），间隔过去后也不再派发。
        await f.AgeProfilesAsync(601);
        Assert.Empty(await f.LeaseAsync(first));
        Assert.Empty(await f.LeaseAsync(second));
        Assert.Equal(2, await f.LeasedCountAsync(first));
        Assert.Equal(2, await f.LeasedCountAsync(second));
    }

    [CollectorPoolMySqlFact]
    public async Task Ac2PlatformIntervalAndDailyCapApplyRegardlessOfProfileCount()
    {
        var f = await PoolFixture.CreateAsync(5);
        await f.PlatformAsync(minInterval: 120, dailyCap: 3);
        var a = await f.RegisterAsync("doubao-a");
        var b = await f.RegisterAsync("doubao-b");
        var c = await f.RegisterAsync("doubao-c");

        Assert.Single(await f.LeaseAsync(a));
        Assert.Empty(await f.LeaseAsync(b)); // 平台最小间隔
        var waiting = await f.EligibilityAsync(b);
        Assert.False(waiting.Eligible);
        Assert.InRange(waiting.RetryAfterSeconds, 100, 120);

        await f.AgePlatformAsync(121);
        Assert.Single(await f.LeaseAsync(b));
        await f.AgePlatformAsync(121);
        Assert.Single(await f.LeaseAsync(c));
        await f.AgePlatformAsync(121);
        // 当日（北京时间）已派发 3 次 = daily_cap：账号再多也不派发。
        Assert.Empty(await f.LeaseAsync(a));
        Assert.Empty(await f.LeaseAsync(b));
        var capped = await f.EligibilityAsync(a);
        Assert.False(capped.Eligible);
        Assert.InRange(capped.RetryAfterSeconds, 30, 120);
        Assert.Equal(3, await f.ScalarAsync<long>("SELECT COUNT(*) FROM collector_attempts WHERE tenant_id = UUID_TO_BIN(@tenant) AND event_kind = 'leased';"));
    }

    [CollectorPoolMySqlFact]
    public async Task Ac3ChallengeReportReleasesLeaseParksProfileAndWritesEvidence()
    {
        var f = await PoolFixture.CreateAsync(3);
        await f.PlatformAsync(minInterval: 0);
        var a = await f.RegisterAsync("doubao-a");
        var b = await f.RegisterAsync("doubao-b");
        var task = Assert.Single(await f.LeaseAsync(a));

        var result = await f.FailAsync(a, CollectorFailureCode.ChallengeRequired, CollectorPhase.PreSend, task);
        Assert.Equal(new CollectorFailureResult(CollectorSessionProfileStatus.NeedsVerification, CollectorTaskDisposition.Requeued, false), result);

        var row = await f.RowAsync("""
            SELECT status, lease_owner IS NULL, lease_until IS NULL, failure_count, last_error_code,
                   next_attempt_at > UTC_TIMESTAMP(6) + INTERVAL 1790 SECOND
            FROM collector_tasks WHERE id = UUID_TO_BIN(@id);
            """, ("@id", task.Id.ToString()));
        Assert.Equal(["retryable_failed", "1", "1", "1", "challenge_required", "1"], row);
        Assert.Equal(["failed", "challenge_required", a.ToString(), task.LeaseVersion.ToString()], await f.RowAsync("""
            SELECT event_kind, error_code, BIN_TO_UUID(session_profile_id), lease_version FROM collector_attempts
            WHERE collector_task_id = UUID_TO_BIN(@id) AND event_kind = 'failed';
            """, ("@id", task.Id.ToString())));
        Assert.Equal(["failure_reported", "challenge_required", "pre_send", "active", "needs_verification"], await f.RowAsync("""
            SELECT event_kind, failure_code, phase, from_status, to_status FROM collector_session_health_events
            WHERE session_profile_id = UUID_TO_BIN(@id) AND event_kind = 'failure_reported';
            """, ("@id", a.ToString())));
        Assert.Equal((1, "needs_verification"), (await f.ScalarAsync<int>(
            "SELECT consecutive_challenges FROM collector_session_profiles WHERE id = UUID_TO_BIN(@id);", ("@id", a.ToString())),
            await f.ScalarAsync<string>("SELECT status FROM collector_session_profiles WHERE id = UUID_TO_BIN(@id);", ("@id", a.ToString()))));

        Assert.Empty(await f.LeaseAsync(a));
        Assert.Equal(new CollectorEligibilityResult(false, 120, CollectorEligibilityStopReason.NeedsVerification), await f.EligibilityAsync(a));
        var other = Assert.Single(await f.LeaseAsync(b));
        Assert.NotEqual(task.Id, other.Id);
        Assert.Equal(b, other.SessionProfileId);
    }

    [CollectorPoolMySqlFact]
    public async Task Ac4BackoffBlocksEveryProfileAndThirdFailureTerminatesTask()
    {
        var f = await PoolFixture.CreateAsync(1);
        await f.PlatformAsync(minInterval: 0);
        var a = await f.RegisterAsync("doubao-a");
        var b = await f.RegisterAsync("doubao-b");
        var task = Assert.Single(await f.LeaseAsync(a));
        Assert.Equal(CollectorTaskDisposition.Requeued, (await f.FailAsync(a, CollectorFailureCode.CollectionTimeout, CollectorPhase.PostSend, task)).TaskDisposition);

        Assert.Empty(await f.LeaseAsync(a));
        Assert.Empty(await f.LeaseAsync(b));
        Assert.Equal(new CollectorEligibilityResult(false, 120), await f.EligibilityAsync(b)); // 退避 1800 秒，上限 120
        await f.ExecAsync("UPDATE collector_tasks SET next_attempt_at = UTC_TIMESTAMP(6) + INTERVAL 45 SECOND WHERE id = UUID_TO_BIN(@id);",
            ("@id", task.Id.ToString()));
        Assert.InRange((await f.EligibilityAsync(b)).RetryAfterSeconds, 44, 46);

        await f.ReleaseBackoffAsync(task.Id);
        var second = Assert.Single(await f.LeaseAsync(b)); // b 未领取过，先于 a
        Assert.Equal(task.Id, second.Id);
        Assert.Equal(CollectorTaskDisposition.Requeued, (await f.FailAsync(b, CollectorFailureCode.BrowserFailed, CollectorPhase.PreSend, second)).TaskDisposition);
        await f.ReleaseBackoffAsync(task.Id);
        var third = Assert.Single(await f.LeaseAsync(a));
        var final = await f.FailAsync(a, CollectorFailureCode.PageContractChanged, CollectorPhase.PreSend, third);
        Assert.Equal(new CollectorFailureResult(CollectorSessionProfileStatus.Active, CollectorTaskDisposition.Failed, false), final);

        Assert.Equal(["terminal_failed", "3"], await f.RowAsync("SELECT status, failure_count FROM collector_tasks WHERE id = UUID_TO_BIN(@id);",
            ("@id", task.Id.ToString())));
        Assert.Equal("failed", await f.ScalarAsync<string>("SELECT r.status FROM visibility_runs r JOIN collector_tasks t ON t.run_id = r.id WHERE t.id = UUID_TO_BIN(@id);",
            ("@id", task.Id.ToString())));
        await f.ReleaseBackoffAsync(task.Id);
        Assert.Empty(await f.LeaseAsync(a));
        Assert.Empty(await f.LeaseAsync(b));
    }

    [CollectorPoolMySqlFact]
    public async Task Ac5BreakerPausesPlatformAfterTwoProfilesChallengedAndOperatorResumes()
    {
        var f = await PoolFixture.CreateAsync(4);
        await f.PlatformAsync(minInterval: 0);
        var a = await f.RegisterAsync("doubao-a");
        var b = await f.RegisterAsync("doubao-b");
        var c = await f.RegisterAsync("doubao-c");
        var t1 = Assert.Single(await f.LeaseAsync(a));
        Assert.False((await f.FailAsync(a, CollectorFailureCode.ChallengeRequired, CollectorPhase.PreSend, t1)).PlatformPaused);
        var t2 = Assert.Single(await f.LeaseAsync(b));
        Assert.True((await f.FailAsync(b, CollectorFailureCode.ChallengeRequired, CollectorPhase.PostSend, t2)).PlatformPaused);

        Assert.Equal("challenge_breaker", (await f.Pool.ListPlatformStatesAsync(f.Tenant)).Single().PauseReason);
        Assert.Equal(1, await f.ScalarAsync<long>("SELECT COUNT(*) FROM collector_session_health_events WHERE tenant_id = UUID_TO_BIN(@tenant) AND event_kind = 'breaker_tripped';"));
        Assert.Empty(await f.LeaseAsync(c));
        Assert.Equal(new CollectorEligibilityResult(false, 120), await f.EligibilityAsync(c));

        Assert.True(await f.Pool.ResumePlatformAsync(f.Tenant, "cn", "doubao", null));
        Assert.False(await f.Pool.ResumePlatformAsync(f.Tenant, "cn", "doubao", null));
        Assert.Null((await f.Pool.ListPlatformStatesAsync(f.Tenant)).Single().PausedAt);
        var t3 = Assert.Single(await f.LeaseAsync(c));
        // 熔断计数从最近一次 breaker_reset 起算：解除后单个账号再遇验证码不会立刻重新熔断。
        Assert.False((await f.FailAsync(c, CollectorFailureCode.ChallengeRequired, CollectorPhase.PreSend, t3)).PlatformPaused);
    }

    [CollectorPoolMySqlFact]
    public async Task Ac6AcceptedSampleCarriesLeaseProfileAndForgedProfilesAreForbidden()
    {
        var f = await PoolFixture.CreateAsync(2);
        await f.PlatformAsync(minInterval: 0);
        var a = await f.RegisterAsync("doubao-a");
        var task = Assert.Single(await f.LeaseAsync(a));
        var result = await f.Store.SubmitResultAsync(new CollectorResultCommand(f.Tenant, task.Id, PoolFixture.Node(a), CollectorRegion.Cn,
            task.LeaseVersion, $"pool-result-{task.Id:N}", "CN", "pool-rev", "豆包回答正文", [], DateTimeOffset.UtcNow));
        Assert.Equal(CollectorResultDisposition.Accepted, result.Disposition);
        Assert.Equal(a.ToString(), await f.ScalarAsync<string>("SELECT BIN_TO_UUID(session_profile_id) FROM visibility_checks WHERE id = UUID_TO_BIN(@id);",
            ("@id", task.CheckId.ToString())));
        Assert.Equal(a.ToString(), await f.ScalarAsync<string>("""
            SELECT BIN_TO_UUID(session_profile_id) FROM collector_attempts WHERE collector_task_id = UUID_TO_BIN(@id) AND event_kind = 'accepted';
            """, ("@id", task.Id.ToString())));

        var other = await PoolFixture.CreateAsync(1);
        var foreign = await other.RegisterAsync("doubao-foreign");
        await AssertForbidden(() => f.LeaseAsync(Guid.NewGuid()));
        await AssertForbidden(() => f.LeaseAsync(foreign)); // 跨租户
        await AssertForbidden(() => f.Store.LeaseAsync(new CollectorLeaseCommand(f.Tenant, "pool-node", CollectorRegion.Cn, ["kimi"], 1, 300, a)));
        await AssertForbidden(() => f.Store.LeaseAsync(new CollectorLeaseCommand(f.Tenant, "pool-node", CollectorRegion.Overseas, ["doubao"], 1, 300, a)));
        await AssertForbidden(() => f.Pool.CheckEligibilityAsync(new CollectorEligibilityCommand(f.Tenant, "pool-node", CollectorRegion.Cn, a, "kimi")));
        await AssertForbidden(() => f.FailAsync(foreign, CollectorFailureCode.BrowserFailed, CollectorPhase.Probe, null));
        var missing = await Assert.ThrowsAsync<CollectorStoreException>(() => f.Pool.ReportFailureAsync(new CollectorFailureCommand(f.Tenant,
            "pool-node", CollectorRegion.Cn, a, CollectorFailureCode.BrowserFailed, CollectorPhase.PreSend, new CollectorReportedTask(Guid.NewGuid(), 1))));
        Assert.Equal(("TASK_NOT_FOUND", 404), (missing.Code, missing.StatusCode));
    }

    [CollectorPoolMySqlFact]
    public async Task Ac7TenantWithoutProfilesLeasesAsBeforeAndRegisteredPlatformsStopLegacyDispatch()
    {
        var f = await PoolFixture.CreateAsync(4);
        var legacy = await f.Store.LeaseAsync(new CollectorLeaseCommand(f.Tenant, "legacy-node", CollectorRegion.Cn, ["doubao"], 3, 300));
        Assert.Equal(3, legacy.Count);
        Assert.All(legacy, task => Assert.Null(task.SessionProfileId));
        Assert.Equal(0, await f.ScalarAsync<long>("SELECT COUNT(*) FROM collector_attempts WHERE tenant_id = UUID_TO_BIN(@tenant) AND session_profile_id IS NOT NULL;"));
        Assert.Equal(0, await f.ScalarAsync<long>("SELECT COUNT(*) FROM collector_platform_dispatch_states WHERE tenant_id = UUID_TO_BIN(@tenant);"));
        Assert.Equal(CollectorResultDisposition.Accepted, (await f.Store.SubmitResultAsync(new CollectorResultCommand(f.Tenant, legacy[0].Id,
            "legacy-node", CollectorRegion.Cn, legacy[0].LeaseVersion, "legacy-result-0001", "CN", "legacy-rev", "回答", [], DateTimeOffset.UtcNow))).Disposition);
        Assert.Null(await f.ScalarAsync<string?>("SELECT BIN_TO_UUID(session_profile_id) FROM visibility_checks WHERE id = UUID_TO_BIN(@id);",
            ("@id", legacy[0].CheckId.ToString())));

        // 登记账号后（即使随后停用）不带账号的租约不再派发该平台，避免回落到不受配额约束的派发。
        var a = await f.RegisterAsync("doubao-a");
        await f.Pool.SetProfileStatusAsync(f.Tenant, a, CollectorSessionProfileStatus.Disabled, null);
        Assert.Empty(await f.Store.LeaseAsync(new CollectorLeaseCommand(f.Tenant, "legacy-node", CollectorRegion.Cn, ["doubao"], 3, 300)));
        Assert.Equal(new CollectorEligibilityResult(false, 120, CollectorEligibilityStopReason.Disabled), await f.EligibilityAsync(a));
    }

    [CollectorPoolMySqlFact]
    public async Task Ac11VerifiedProbeRestoresActiveAndClearsChallengeStreakButKeepsDisabled()
    {
        var f = await PoolFixture.CreateAsync(2);
        await f.PlatformAsync(minInterval: 0);
        var a = await f.RegisterAsync("doubao-a");
        var task = Assert.Single(await f.LeaseAsync(a));
        Assert.Equal(CollectorSessionProfileStatus.Active, (await f.EventAsync(a, CollectorSessionEventKind.ChallengeWaitStarted, CollectorPhase.PreSend, task)).SessionProfileStatus);
        await f.FailAsync(a, CollectorFailureCode.ChallengeRequired, CollectorPhase.PreSend, task);

        var metrics = (await f.Pool.ListProfileMetricsAsync(f.Tenant, "doubao", "cn")).Single();
        Assert.Equal((CollectorSessionProfileStatus.NeedsVerification, 1, 1, 1, 1.0), (metrics.Status, metrics.ConsecutiveChallenges,
            metrics.LeasedLastHour, metrics.ChallengesLast24h, metrics.ChallengeRateLast24h ?? -1)); // 超时的一次等待只计一次
        Assert.NotNull(metrics.LastChallengeAt);
        Assert.EndsWith("Z", metrics.LastLeasedAt);

        Assert.Equal(CollectorSessionProfileStatus.Active, (await f.EventAsync(a, CollectorSessionEventKind.Verified, CollectorPhase.Probe, null)).SessionProfileStatus);
        Assert.Equal(["active", "0"], await f.RowAsync("SELECT status, consecutive_challenges FROM collector_session_profiles WHERE id = UUID_TO_BIN(@id);",
            ("@id", a.ToString())));
        Assert.True((await f.EligibilityAsync(a)).Eligible);

        await f.Pool.SetProfileStatusAsync(f.Tenant, a, CollectorSessionProfileStatus.Disabled, null);
        Assert.Equal(CollectorSessionProfileStatus.Disabled, (await f.EventAsync(a, CollectorSessionEventKind.Verified, CollectorPhase.Probe, null)).SessionProfileStatus);
        Assert.Equal(["profile_registered", "challenge_wait_started", "failure_reported", "verified", "status_changed_by_operator", "verified"],
            (await f.RowsAsync("SELECT event_kind FROM collector_session_health_events WHERE session_profile_id = UUID_TO_BIN(@id) ORDER BY created_at, event_kind;",
                ("@id", a.ToString()))).Select(row => row[0]).ToArray());
    }

    [CollectorPoolMySqlFact]
    public async Task EligibilityIsReadOnlyAndReportsEachBlockerWithBoundedRetry()
    {
        var f = await PoolFixture.CreateAsync(2);
        var a = await f.RegisterAsync("doubao-a", interval: 600);
        Assert.Equal(new CollectorEligibilityResult(true, 30), await f.EligibilityAsync(a));
        Assert.Equal(0, await f.ScalarAsync<long>("SELECT COUNT(*) FROM collector_attempts WHERE tenant_id = UUID_TO_BIN(@tenant);"));
        Assert.Equal(0, await f.ScalarAsync<long>("SELECT COUNT(*) FROM collector_platform_dispatch_states WHERE tenant_id = UUID_TO_BIN(@tenant);"));
        Assert.Equal(2, await f.ScalarAsync<long>("SELECT COUNT(*) FROM collector_tasks WHERE tenant_id = UUID_TO_BIN(@tenant) AND status = 'pending';"));
        Assert.Equal(1, await f.ScalarAsync<long>("SELECT last_seen_at > UTC_TIMESTAMP(6) - INTERVAL 5 SECOND FROM collector_session_profiles WHERE id = UUID_TO_BIN(@id);",
            ("@id", a.ToString())));

        // 账号最小间隔：剩余约 50 秒。
        await f.PlatformAsync(minInterval: 0);
        Assert.Single(await f.LeaseAsync(a));
        Assert.Equal(new CollectorEligibilityResult(false, 120), await f.EligibilityAsync(a));
        await f.ExecAsync("UPDATE collector_session_profiles SET last_leased_at = UTC_TIMESTAMP(6) - INTERVAL 550 SECOND WHERE id = UUID_TO_BIN(@id);", ("@id", a.ToString()));
        Assert.InRange((await f.EligibilityAsync(a)).RetryAfterSeconds, 49, 51);

        // 限流冷却：首轮 30 分钟（上限 120），之后翻倍并受平台上限约束。
        var probe = await f.FailAsync(a, CollectorFailureCode.RateLimited, CollectorPhase.Probe, null);
        Assert.Equal(new CollectorFailureResult(CollectorSessionProfileStatus.Cooling, null, false), probe);
        Assert.InRange(await f.CooldownSecondsAsync(a), 1795, 1800);
        Assert.Equal(new CollectorEligibilityResult(false, 120), await f.EligibilityAsync(a));
        await f.PlatformAsync(minInterval: 0, cooldownMax: 3000);
        await f.FailAsync(a, CollectorFailureCode.RateLimited, CollectorPhase.Probe, null);
        Assert.InRange(await f.CooldownSecondsAsync(a), 2995, 3000);

        // 冷却到期：领取时恢复 active 并写 cooldown_expired。
        await f.ExecAsync("""
            UPDATE collector_session_profiles SET cooldown_until = UTC_TIMESTAMP(6) - INTERVAL 1 SECOND,
                last_leased_at = UTC_TIMESTAMP(6) - INTERVAL 1 DAY WHERE id = UUID_TO_BIN(@id);
            """, ("@id", a.ToString()));
        Assert.True((await f.EligibilityAsync(a)).Eligible);
        Assert.Single(await f.LeaseAsync(a));
        Assert.Equal("active", await f.ScalarAsync<string>("SELECT status FROM collector_session_profiles WHERE id = UUID_TO_BIN(@id);", ("@id", a.ToString())));
        Assert.Equal(1, await f.ScalarAsync<long>("SELECT COUNT(*) FROM collector_session_health_events WHERE session_profile_id = UUID_TO_BIN(@id) AND event_kind = 'cooldown_expired';",
            ("@id", a.ToString())));

        // 队列为空：按上限 120 秒；登录失效：stopReason expired。
        await f.ExecAsync("UPDATE collector_session_profiles SET last_leased_at = UTC_TIMESTAMP(6) - INTERVAL 1 DAY WHERE id = UUID_TO_BIN(@id);", ("@id", a.ToString()));
        await f.ExecAsync("UPDATE collector_tasks SET status = 'succeeded', lease_owner = NULL, lease_until = NULL WHERE tenant_id = UUID_TO_BIN(@tenant);");
        Assert.Equal(new CollectorEligibilityResult(false, 120), await f.EligibilityAsync(a));
        Assert.Equal(CollectorSessionProfileStatus.Expired, (await f.FailAsync(a, CollectorFailureCode.LoginRequired, CollectorPhase.Probe, null)).SessionProfileStatus);
        Assert.Equal(new CollectorEligibilityResult(false, 120, CollectorEligibilityStopReason.Expired), await f.EligibilityAsync(a));
    }

    [CollectorPoolMySqlFact]
    public async Task EligibilityFairnessWaitsForOlderOnlineProfileUnlessItsProbeRecentlyFailed()
    {
        var f = await PoolFixture.CreateAsync(3);
        await f.PlatformAsync(minInterval: 0);
        var a = await f.RegisterAsync("doubao-a");
        var b = await f.RegisterAsync("doubao-b");
        var (older, younger) = string.CompareOrdinal(a.ToString(), b.ToString()) < 0 ? (a, b) : (b, a);
        await f.EligibilityAsync(older);
        Assert.Equal(new CollectorEligibilityResult(false, 30), await f.EligibilityAsync(younger));

        // §12.4：较老账号 probe 失败且之后未领取，退出其他账号的公平队列；自身仍可领取。
        Assert.Equal(CollectorSessionProfileStatus.Active,
            (await f.FailAsync(older, CollectorFailureCode.PageContractChanged, CollectorPhase.Probe, null)).SessionProfileStatus);
        Assert.Equal(new CollectorEligibilityResult(true, 30), await f.EligibilityAsync(younger));
        Assert.True((await f.Pool.ListProfileMetricsAsync(f.Tenant, null, null)).Single(m => m.Id == older).ExcludedFromFairness);
        Assert.Single(await f.LeaseAsync(older));
        Assert.False((await f.Pool.ListProfileMetricsAsync(f.Tenant, null, null)).Single(m => m.Id == older).ExcludedFromFairness);

        // 超出在线窗口的账号不阻塞其他账号。
        var c = await f.RegisterAsync("doubao-c");
        await f.EligibilityAsync(c);
        await f.ExecAsync("UPDATE collector_session_profiles SET last_seen_at = UTC_TIMESTAMP(6) - INTERVAL 301 SECOND WHERE id = UUID_TO_BIN(@id);", ("@id", c.ToString()));
        Assert.True((await f.EligibilityAsync(younger)).Eligible);
    }

    [CollectorPoolMySqlFact]
    public async Task StaleFailureReportOnlyRecordsProfileEffectAndChallengeEventsKeepStatus()
    {
        var f = await PoolFixture.CreateAsync(1);
        await f.PlatformAsync(minInterval: 0);
        var a = await f.RegisterAsync("doubao-a");
        var task = Assert.Single(await f.LeaseAsync(a));
        Assert.Equal(CollectorSessionProfileStatus.Active, (await f.EventAsync(a, CollectorSessionEventKind.ChallengeCleared, CollectorPhase.PostSend, task)).SessionProfileStatus);
        var stale = await f.Pool.ReportFailureAsync(new CollectorFailureCommand(f.Tenant, PoolFixture.Node(a), CollectorRegion.Cn, a,
            CollectorFailureCode.RateLimited, CollectorPhase.PostSend, new CollectorReportedTask(task.Id, task.LeaseVersion + 1)));
        Assert.Equal(new CollectorFailureResult(CollectorSessionProfileStatus.Cooling, CollectorTaskDisposition.Stale, false), stale);
        Assert.Equal(["leased", "0"], await f.RowAsync("SELECT status, failure_count FROM collector_tasks WHERE id = UUID_TO_BIN(@id);", ("@id", task.Id.ToString())));
    }

    [CollectorPoolMySqlFact]
    public async Task HealthEventsAreAppendOnlyAndOperatorInputsAreValidated()
    {
        var f = await PoolFixture.CreateAsync(1);
        var a = await f.Pool.RegisterProfileAsync(new RegisterCollectorSessionProfileCommand(f.Tenant, "doubao", "cn", " doubao-a ", null, null, null, null));
        foreach (var sql in new[]
        {
            "UPDATE collector_session_health_events SET node_id = 'x' WHERE tenant_id = UUID_TO_BIN(@tenant);",
            "DELETE FROM collector_session_health_events WHERE tenant_id = UUID_TO_BIN(@tenant);",
        })
        {
            var exception = await Assert.ThrowsAsync<MySqlException>(() => f.ExecAsync(sql));
            Assert.Equal("45000", exception.SqlState);
        }
        await AssertCode("SESSION_PROFILE_LABEL_EXISTS", 409, () => f.RegisterAsync("doubao-a"));
        await AssertCode("INVALID_PLATFORM", 400, () => f.Pool.RegisterProfileAsync(new RegisterCollectorSessionProfileCommand(f.Tenant, "doubao", "overseas", "x", null, null, null, null)));
        await AssertCode("TENANT_NOT_FOUND", 404, () => f.Pool.RegisterProfileAsync(new RegisterCollectorSessionProfileCommand(Guid.NewGuid(), "doubao", "cn", "x", null, null, null, null)));
        await AssertCode("INVALID_SESSION_PROFILE", 400, () => f.Pool.RegisterProfileAsync(new RegisterCollectorSessionProfileCommand(f.Tenant, "doubao", "cn", "  ", null, null, null, null)));
        await AssertCode("INVALID_SESSION_PROFILE_STATUS", 400, () => f.Pool.SetProfileStatusAsync(f.Tenant, a, CollectorSessionProfileStatus.NeedsVerification, null));
        await AssertCode("SESSION_PROFILE_NOT_FOUND", 404, () => f.Pool.SetProfileStatusAsync(f.Tenant, Guid.NewGuid(), CollectorSessionProfileStatus.Disabled, null));
        await AssertCode("INVALID_PLATFORM_DISPATCH", 400, () => f.PlatformAsync(minInterval: 0, cooldownBase: 5000, cooldownMax: 4000));
        var metrics = (await f.Pool.ListProfileMetricsAsync(f.Tenant, null, null)).Single();
        Assert.Equal((4, 30, 600, (double?)null), (metrics.HourlyQuota, metrics.DailyQuota, metrics.MinIntervalSeconds, metrics.ChallengeRateLast24h));
    }

    private static async Task AssertForbidden(Func<Task> action) => await AssertCode("SESSION_PROFILE_FORBIDDEN", 403, action);

    private static async Task AssertCode(string code, int status, Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<CollectorStoreException>(action);
        Assert.Equal((code, status), (exception.Code, exception.StatusCode));
    }

    internal sealed class PoolFixture
    {
        private readonly string connectionString;
        private readonly ManagementStore management;

        private PoolFixture(string connection)
        {
            connectionString = connection;
            var database = new MySqlDatabase(connection);
            management = new ManagementStore(database);
            Store = new DomesticCollectorStore(database);
            Pool = new CollectorSessionPoolStore(database);
        }

        internal Guid Tenant { get; } = Guid.NewGuid();
        internal DomesticCollectorStore Store { get; }
        internal ICollectorSessionPoolStore Pool { get; }

        internal static string Node(Guid profile) => $"pool-node-{profile.ToString()[..8]}";

        internal static async Task<PoolFixture> CreateAsync(int prompts)
        {
            var value = Environment.GetEnvironmentVariable("JOGEOAI_COLLECTOR_POOL_TEST_CONNECTION");
            Assert.False(string.IsNullOrWhiteSpace(value), "未设置 JOGEOAI_COLLECTOR_POOL_TEST_CONNECTION；SQL 测试不会静默通过。");
            var builder = new MySqlConnectionStringBuilder(value!);
            Assert.Contains("_test", builder.Database, StringComparison.OrdinalIgnoreCase);
            var f = new PoolFixture(value!);
            await f.ExecAsync("""
                INSERT INTO tenants (id, name, status, created_at, updated_at)
                VALUES (UUID_TO_BIN(@tenant), 'collector pool fixture', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                """);
            var site = (await f.management.CreateBrandAsync(f.Tenant, new BrandWrite("Collector Pool", $"pool-{f.Tenant:N}.test",
                IncludeSubdomains: false, Description: "测试品牌提供设备与服务。"))).SiteId;
            var ids = new List<Guid>();
            for (var i = 1; i <= prompts; i++)
                ids.Add((await f.management.CreatePromptAsync(f.Tenant, site, new PromptWrite($"管道摄像机怎么选？账号池第 {i} 问", "zh-CN"))).PromptId);
            await f.Store.CreateRunAsync(new CreateVisibilityRunCommand(f.Tenant, site, ids,
                [new CollectorPlatformSelection("doubao", "doubao.consumer_web")], $"collector-pool-{Guid.NewGuid():N}"));
            return f;
        }

        internal Task<Guid> RegisterAsync(string label, int hourly = 100, int daily = 1000, int interval = 0)
            => Pool.RegisterProfileAsync(new RegisterCollectorSessionProfileCommand(Tenant, "doubao", "cn", label, hourly, daily, interval, null));

        internal Task PlatformAsync(int? minInterval = null, int? dailyCap = null, int? cooldownBase = null, int? cooldownMax = null)
            => Pool.ConfigurePlatformAsync(new ConfigureCollectorPlatformDispatchCommand(Tenant, "cn", "doubao", minInterval, dailyCap ?? 1000,
                null, null, null, null, cooldownBase, cooldownMax, null, null));

        internal Task<IReadOnlyList<LeasedCollectorTask>> LeaseAsync(Guid profile)
            => Store.LeaseAsync(new CollectorLeaseCommand(Tenant, Node(profile), CollectorRegion.Cn, ["doubao"], 5, 300, profile));

        internal Task<CollectorEligibilityResult> EligibilityAsync(Guid profile)
            => Pool.CheckEligibilityAsync(new CollectorEligibilityCommand(Tenant, Node(profile), CollectorRegion.Cn, profile, "doubao"));

        internal Task<CollectorFailureResult> FailAsync(Guid profile, CollectorFailureCode code, CollectorPhase phase, LeasedCollectorTask? task)
            => Pool.ReportFailureAsync(new CollectorFailureCommand(Tenant, Node(profile), CollectorRegion.Cn, profile, code, phase,
                task is null ? null : new CollectorReportedTask(task.Id, task.LeaseVersion)));

        internal Task<CollectorSessionEventResult> EventAsync(Guid profile, CollectorSessionEventKind kind, CollectorPhase phase, LeasedCollectorTask? task)
            => Pool.RecordSessionEventAsync(new CollectorSessionEventCommand(Tenant, Node(profile), CollectorRegion.Cn, profile, kind, phase,
                task is null ? null : new CollectorReportedTask(task.Id, task.LeaseVersion)));

        internal Task AgeProfilesAsync(int seconds)
            => ExecAsync("UPDATE collector_session_profiles SET last_leased_at = last_leased_at - INTERVAL @s SECOND WHERE tenant_id = UUID_TO_BIN(@tenant);",
                ("@s", seconds));

        internal Task AgePlatformAsync(int seconds)
            => ExecAsync("UPDATE collector_platform_dispatch_states SET last_dispatched_at = last_dispatched_at - INTERVAL @s SECOND WHERE tenant_id = UUID_TO_BIN(@tenant);",
                ("@s", seconds));

        internal Task ReleaseBackoffAsync(Guid taskId)
            => ExecAsync("UPDATE collector_tasks SET next_attempt_at = UTC_TIMESTAMP(6) - INTERVAL 1 SECOND WHERE id = UUID_TO_BIN(@id);", ("@id", taskId.ToString()));

        internal Task<long> LeasedCountAsync(Guid profile)
            => ScalarAsync<long>("SELECT COUNT(*) FROM collector_attempts WHERE session_profile_id = UUID_TO_BIN(@id) AND event_kind = 'leased';",
                ("@id", profile.ToString()));

        internal Task<long> CooldownSecondsAsync(Guid profile)
            => ScalarAsync<long>("SELECT TIMESTAMPDIFF(SECOND, UTC_TIMESTAMP(6), cooldown_until) FROM collector_session_profiles WHERE id = UUID_TO_BIN(@id);",
                ("@id", profile.ToString()));

        internal async Task ExecAsync(string sql, params (string Name, object? Value)[] parameters)
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = Command(connection, sql, parameters);
            await command.ExecuteNonQueryAsync();
        }

        internal async Task<T> ScalarAsync<T>(string sql, params (string Name, object? Value)[] parameters)
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = Command(connection, sql, parameters);
            var value = await command.ExecuteScalarAsync();
            return value is null or DBNull ? default! : (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T),
                System.Globalization.CultureInfo.InvariantCulture);
        }

        internal async Task<string[]> RowAsync(string sql, params (string Name, object? Value)[] parameters)
            => Assert.Single(await RowsAsync(sql, parameters));

        internal async Task<List<string[]>> RowsAsync(string sql, params (string Name, object? Value)[] parameters)
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = Command(connection, sql, parameters);
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<string[]>();
            while (await reader.ReadAsync())
            {
                var values = new string[reader.FieldCount];
                for (var i = 0; i < values.Length; i++)
                    values[i] = reader.IsDBNull(i) ? "" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture) ?? "";
                rows.Add(values);
            }
            return rows;
        }

        private MySqlCommand Command(MySqlConnection connection, string sql, (string Name, object? Value)[] parameters)
        {
            var command = new MySqlCommand(sql, connection);
            command.Parameters.AddWithValue("@tenant", Tenant.ToString());
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            return command;
        }
    }
}
