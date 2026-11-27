using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using static Jogeoai.Tests.AppCycleRunSqlTests;
using static Jogeoai.Tests.TeamMonitoringConfigSqlTests;

namespace Jogeoai.Tests;

/// <summary>
/// 隔离库（已执行 001–049、库名含 _test）上的 GET /sites/{siteId}/app-collection/status（implement 8.2）：当前周期与最近一个已结束周期的
/// 计数与库里检测的状态一致，未采集原因分布与 error_code 一致；被排除的提问有 Run 的周期取自 selection_snapshot 的 $.app，
/// 没有 Run 的周期（时隙被跳过或尚未生成）按当前的提问现算；预计首次结果时间；未开放的团队、暂停的计划、跨团队与不存在的品牌。
/// 未设置 JOGEOAI_APP_CHANNEL_TEST_CONNECTION 时跳过，绝不使用业务库 jogeoai。时隙用 2000 年的日期，“现在”由用例传入。
/// </summary>
[Collection(AppChannelSqlCollection.Name)]
public sealed class AppCollectionStatusSqlTests
{
    private static DateTimeOffset At(int day, int hour = 0, int minute = 0) => new(2000, 1, day, hour, minute, 0, TimeSpan.Zero);

    private static Task SetCheckAsync(string cs, Guid run, Guid prompt, string engine, string status, string? errorCode = null)
        => ExecAsync(cs, """
            UPDATE visibility_checks SET status = @status, error_code = @code,
                captured_at = CASE WHEN @status IN ('succeeded', 'cancelled') THEN UTC_TIMESTAMP(6) ELSE captured_at END
            WHERE run_id = UUID_TO_BIN(@run) AND prompt_id = UUID_TO_BIN(@prompt) AND engine = @engine;
            """, ("@status", status), ("@code", errorCode), ("@run", run.ToString()), ("@prompt", prompt.ToString()), ("@engine", engine));

    // The counts the status must report, read straight from the checks: planned | collected | in progress | not collected | reasons.
    private static async Task<string> CountsInDatabaseAsync(string cs, Guid run, string engine)
    {
        var rows = (await RowsAsync(cs, """
                SELECT status, COALESCE(error_code, ''), COUNT(*) FROM visibility_checks
                WHERE run_id = UUID_TO_BIN(@run) AND engine = @engine AND provider = 'app_collector' GROUP BY status, error_code;
                """, ("@run", run.ToString()), ("@engine", engine)))
            .Select(row => (Status: row[0]!, Code: row[1]!, Count: int.Parse(row[2]!, System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        var reasons = rows.Where(row => row.Status == "cancelled").GroupBy(row => row.Code, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{group.Key}={group.Sum(row => row.Count)}");
        return string.Join("|", rows.Sum(row => row.Count), rows.Where(row => row.Status == "succeeded").Sum(row => row.Count),
            rows.Where(row => row.Status is "pending" or "running").Sum(row => row.Count), rows.Where(row => row.Status == "cancelled").Sum(row => row.Count),
            string.Join(",", reasons));
    }

    private static string Counts(AppCollectionCycleStatus cycle)
        => string.Join("|", cycle.Planned, cycle.Collected, cycle.InProgress, cycle.NotCollected,
            string.Join(",", cycle.NotCollectedByReason.Select(reason => $"{reason.ReasonCode}={reason.Count}")));

    private static string Excluded(AppCollectionCycleStatus cycle)
        => string.Join(",", cycle.ExcludedPrompts.Select(reason => $"{reason.ReasonCode}={reason.Count}"));

    [AppChannelMySqlFact]
    public Task TheCyclesCountTheChecksOfTheRunAndTheExcludedPromptsComeFromTheRunOrAreComputedWithoutOne()
        => AppSiteTargetsSqlTests.IsolatedAsync(async (cs, seedTenant) =>
    {
        var rig = Build(cs, await seedTenant());
        var status = new AppCollectionStatusQuery(rig.Database, AppSiteTargetsSqlTests.OpenFor(rig.Tenant));
        var site = await SeedAppBrandAsync(rig, "采集状态", "doubao", "kimi");
        var (p1, p2, p3) = (await SeedPromptAsync(cs, site, "哪个记账工具好用？", 1), await SeedPromptAsync(cs, site, "预算管理应用怎么选？", 2),
            await SeedPromptAsync(cs, site, "家庭理财软件推荐？", 3));
        await SeedPromptAsync(cs, site, new string('长', AppPromptRules.MaxLength + 1), 4);
        var excluded = await SeedPromptAsync(cs, site, "被运营排除的提问", 5);
        var doubaoTooLong = await SeedPromptAsync(cs, site, new string('豆', AppPromptRules.MaxLength + 1), 6);
        await ScopeAsync(cs, rig.Tenant, site, doubaoTooLong, CollectionIdentity.AppTargetKey("doubao"));
        await rig.App.ExcludePromptAsync(rig.Tenant, site, excluded, "测试排除", Guid.NewGuid());
        var plan = await StartAsync(rig, site);

        // Nothing has run yet (now is before the first slot, Monday 2000-01-03 09:11): no current cycle, the first one is next.
        var before = await status.GetAsync(rig.Tenant, site, At(3, 1, 10), default);
        Assert.True(before.PlanActive);
        Assert.Equal(["doubao", "kimi"], before.Targets.Select(target => target.Engine));
        Assert.All(before.Targets, target => Assert.Equal(((AppCollectionCycleStatus?)null, (AppCollectionCycleStatus?)null, (DateTimeOffset?)At(3, 1, 11)),
            (target.CurrentCycle, target.LastCompletedCycle, target.ExpectedFirstResultAt)));
        var doubaoTarget = before.Targets[0];
        Assert.Equal(("豆包", CollectionIdentity.AppTargetKey("doubao"), "available", (DateOnly?)new DateOnly(2000, 1, 3)),
            (doubaoTarget.DisplayName, doubaoTarget.TargetKey, doubaoTarget.Availability, doubaoTarget.FrequencyAnchor));

        var run = (await RunSlotAsync(rig, plan, 3)).RunId!.Value;
        // doubao: collected, not collected (capacity), still pending. kimi: not collected twice for credit, once for the binding.
        await SetCheckAsync(cs, run, p1, "doubao", "succeeded");
        await SetCheckAsync(cs, run, p2, "doubao", "cancelled", AppNotCollectedCodes.Capacity);
        await SetCheckAsync(cs, run, p1, "kimi", "cancelled", AppNotCollectedCodes.Credit);
        await SetCheckAsync(cs, run, p2, "kimi", "cancelled", AppNotCollectedCodes.Credit);
        await SetCheckAsync(cs, run, p3, "kimi", "cancelled", AppNotCollectedCodes.Binding);

        var during = await status.GetAsync(rig.Tenant, site, At(5), default);
        var (doubao, kimi) = (during.Targets[0], during.Targets[1]);
        foreach (var target in during.Targets)
        {
            var cycle = target.CurrentCycle!;
            Assert.Equal((At(3, 1, 11), At(10, 1, 11), true, (string?)null), (cycle.StartsAt, cycle.EndsAt, cycle.RunCreated, cycle.SkipCode));
            Assert.Equal(await CountsInDatabaseAsync(cs, run, target.Engine), Counts(cycle));
            Assert.Null(target.LastCompletedCycle);
        }
        Assert.Equal("3|1|1|1|APP_NOT_COLLECTED_CAPACITY=1", Counts(doubao.CurrentCycle!));
        Assert.Equal("3|0|0|3|APP_NOT_COLLECTED_BINDING=1,APP_NOT_COLLECTED_CREDIT=2", Counts(kimi.CurrentCycle!));
        // Excluded prompts as recorded when the run was expanded: the doubao-only prompt only on doubao.
        Assert.Equal("EXCLUDED_BY_OPERATOR=1,TOO_LONG=2", Excluded(doubao.CurrentCycle!));
        Assert.Equal("EXCLUDED_BY_OPERATOR=1,TOO_LONG=1", Excluded(kimi.CurrentCycle!));
        // doubao already has an answer; kimi has none and nothing in progress: its first result is expected from the next cycle.
        Assert.Equal(((DateTimeOffset?)null, (DateTimeOffset?)At(10, 1, 11)), (doubao.ExpectedFirstResultAt, kimi.ExpectedFirstResultAt));

        // The exclusion is revoked: the run's record does not change, a cycle without a run is computed from the current prompts.
        await rig.App.RevokePromptExclusionAsync(rig.Tenant, site, excluded, Guid.NewGuid());
        Assert.Equal("EXCLUDED_BY_OPERATOR=1,TOO_LONG=2", Excluded((await status.GetAsync(rig.Tenant, site, At(5), default)).Targets[0].CurrentCycle!));
        // The next week's slot is skipped (materialized by a worker the tenant is not open for): no run.
        Assert.Null(await MaterializeAsync(Build(cs, rig.Tenant, open: false), plan, 10));
        var after = await status.GetAsync(rig.Tenant, site, At(12), default);
        foreach (var target in after.Targets)
        {
            var current = target.CurrentCycle!;
            Assert.Equal((At(10, 1, 11), At(17, 1, 11), false, (string?)AppMonitoringPlan.NotOpenCode, "0|0|0|0|"),
                (current.StartsAt, current.EndsAt, current.RunCreated, current.SkipCode, Counts(current)));
            // The previous cycle is now the last completed one, with the same counts and the excluded prompts of its run.
            var last = target.LastCompletedCycle!;
            Assert.Equal((At(3, 1, 11), At(10, 1, 11), true), (last.StartsAt, last.EndsAt, last.RunCreated));
            Assert.Equal(await CountsInDatabaseAsync(cs, run, target.Engine), Counts(last));
            Assert.Equal(Excluded(target.Engine == "doubao" ? doubao.CurrentCycle! : kimi.CurrentCycle!), Excluded(last));
        }
        Assert.Equal("TOO_LONG=2", Excluded(after.Targets[0].CurrentCycle!));
        Assert.Equal("TOO_LONG=1", Excluded(after.Targets[1].CurrentCycle!));
        Assert.Equal(((DateTimeOffset?)null, (DateTimeOffset?)At(17, 1, 11)), (after.Targets[0].ExpectedFirstResultAt, after.Targets[1].ExpectedFirstResultAt));
        // A cycle with work in progress and no answer yet: the first result is expected in that cycle.
        await SetCheckAsync(cs, run, p3, "doubao", "pending");
        await ExecAsync(cs, "UPDATE visibility_checks SET status = 'pending', captured_at = NULL WHERE run_id = UUID_TO_BIN(@run) AND engine = 'kimi' AND prompt_id = UUID_TO_BIN(@prompt);",
            ("@run", run.ToString()), ("@prompt", p1.ToString()));
        Assert.Equal(At(3, 1, 11), (await status.GetAsync(rig.Tenant, site, At(5), default)).Targets[1].ExpectedFirstResultAt);

        // A paused plan: still the cycles, no expected first result.
        await ExecAsync(cs, "UPDATE collection_schedules SET enabled = FALSE, next_slot_at = NULL WHERE id = UUID_TO_BIN(@plan);", ("@plan", plan.ToString()));
        var paused = await status.GetAsync(rig.Tenant, site, At(12), default);
        Assert.False(paused.PlanActive);
        Assert.All(paused.Targets, target => Assert.Null(target.ExpectedFirstResultAt));
        Assert.Equal(Counts(after.Targets[0].LastCompletedCycle!), Counts(paused.Targets[0].LastCompletedCycle!));

        // A tenant the App channel is not open for (here: no list at all) sees no App target, as in the catalog and site targets.
        var closed = await new AppCollectionStatusQuery(rig.Database).GetAsync(rig.Tenant, site, At(5), default);
        Assert.Equal((false, 0), (closed.PlanActive, closed.Targets.Count));
        var notListed = await new AppCollectionStatusQuery(rig.Database, AppSiteTargetsSqlTests.OpenFor(Guid.NewGuid())).GetAsync(rig.Tenant, site, At(5), default);
        Assert.Equal((false, 0), (notListed.PlanActive, notListed.Targets.Count));
        // Another tenant's brand, an unknown brand and an archived brand: 404, the same for all three.
        var other = await seedTenant();
        foreach (var (tenant, brand) in new[] { (other, site), (rig.Tenant, Guid.NewGuid()) })
        {
            var missing = await Assert.ThrowsAsync<CollectorStoreException>(() => new AppCollectionStatusQuery(rig.Database, AppSiteTargetsSqlTests.OpenFor(rig.Tenant, other))
                .GetAsync(tenant, brand, At(5), default));
            Assert.Equal(("RESOURCE_NOT_FOUND", 404), (missing.Code, missing.StatusCode));
        }
        await ExecAsync(cs, "UPDATE sites SET status = 'archived' WHERE id = UUID_TO_BIN(@site);", ("@site", site.ToString()));
        Assert.Equal(404, (await Assert.ThrowsAsync<CollectorStoreException>(() => status.GetAsync(rig.Tenant, site, At(5), default))).StatusCode);
    });

    [AppChannelMySqlFact]
    public Task ABrandWithoutAppTargetsOrPlanHasAnEmptyListAndTargetsWithoutAPlanHaveNoExpectedResult()
        => AppSiteTargetsSqlTests.IsolatedAsync(async (cs, seedTenant) =>
    {
        var rig = Build(cs, await seedTenant());
        var status = new AppCollectionStatusQuery(rig.Database, AppSiteTargetsSqlTests.OpenFor(rig.Tenant));
        var plain = await SeedBrandAsync(cs, rig.Tenant, "没有 App 目标", follows: false, locale: "zh-CN", created: "2026-07-06");
        Assert.Equal((false, 0), ((await status.GetAsync(rig.Tenant, plain)).PlanActive, (await status.GetAsync(rig.Tenant, plain)).Targets.Count));
        // App targets saved, monitoring never started: no App plan, so no cycle and nothing expected; the anchor is the enabling day.
        var site = await SeedAppBrandAsync(rig, "未开启监控", "qwen");
        var view = await status.GetAsync(rig.Tenant, site);
        var target = Assert.Single(view.Targets);
        Assert.False(view.PlanActive);
        Assert.Equal(("qwen", (AppCollectionCycleStatus?)null, (DateTimeOffset?)null), (target.Engine, target.CurrentCycle, target.ExpectedFirstResultAt));
        Assert.Equal(ManagementStore.BrandLocalDate("Asia/Shanghai", DateTimeOffset.UtcNow), target.FrequencyAnchor);
    });
}
