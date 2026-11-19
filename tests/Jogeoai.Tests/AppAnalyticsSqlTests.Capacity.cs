using Jogeoai.Domain;
using static Jogeoai.Tests.AppCreditSqlTests;
using static Jogeoai.Tests.AppCycleRunSqlTests;
using static Jogeoai.Tests.AppLeaseSqlTests;
using static Jogeoai.Tests.TeamMonitoringConfigSqlTests;

namespace Jogeoai.Tests;

/// <summary>
/// 总 PRD AC-C9 的“未采集（容量不足）”在 App 口径（captureMode=app&amp;provider=app_collector）下的读取：一个周期里有成功的检测，
/// 也有到截止都没有排到的检测。回答记录按 status=cancelled 列出它们并带 APP_NOT_COLLECTED_CAPACITY；监控汇总、看板、提问表现的比率分母
/// 不含它们；全部未采集时比率为 null 而不是 0；未采集的检测没有扣费（只有释放，或从未预留）。
/// </summary>
public sealed partial class AppAnalyticsSqlTests
{
    [AppChannelMySqlFact]
    public Task CapacityNotCollectedChecksAreListedWithTheirReasonLeaveEveryRateDenominatorAndAreNeverCharged() => PoolAsync(async (pool, seedTenant) =>
    {
        var (w, appRun) = await SeedWorldAsync(pool, await seedTenant());
        var (t, s, a) = (w.Tenant, w.Site, w.Analytics);

        // Four App checks: doubao × 2 prompts and kimi × 2 prompts. One doubao check is collected (it mentions the brand); the
        // other doubao check is leased once and its lease runs out; the two kimi checks are never leased (no kimi binding).
        var store = pool.Store(t);
        var (node, _) = await pool.NodeAsync("mac");
        var binding = await pool.BindAsync(node, "AI_Apps_4KB", "doubao");
        var collected = await CollectAsync(pool, store, node, binding, "Acmebook 是首选。");
        await pool.AgePlatformAsync("doubao");
        await pool.AgeBindingAsync(binding);
        var expired = (await store.LeaseAsync(new AppLeaseCommand(node, binding, 60))).Task!;
        Assert.Equal(w.Prompts[1], expired.PromptId);
        await ExpireLeaseAsync(pool, expired.Id);

        // The cycle ends: the three are "not collected (capacity)". Nothing ever blocked them but the queue.
        await ExecAsync(w.Cs, "UPDATE collector_tasks SET deadline_at = UTC_TIMESTAMP(6) - INTERVAL 1 MINUTE WHERE run_id = UUID_TO_BIN(@run) AND status <> 'succeeded';",
            ("@run", appRun.ToString()));
        Assert.Equal(3, await w.Rig.App.ExpireOverdueTasksAsync());
        await AnalyzeAsync(w);
        var cancelled = (await RowsAsync(w.Cs, "SELECT BIN_TO_UUID(id), engine FROM visibility_checks WHERE run_id = UUID_TO_BIN(@run) AND status = 'cancelled';",
            ("@run", appRun.ToString()))).Select(row => Guid.Parse(row[0]!)).ToHashSet();
        Assert.Equal(3, cancelled.Count);

        // ---- Answer records: listed by status=cancelled with the reason code.
        var listed = await a.MonitoringRecordsAsync(t, s, w.AppView with { Statuses = ["cancelled"] }, null, null, 100);
        Assert.Equal(3, listed.TotalCount);
        Assert.True(cancelled.SetEquals(listed.Items.Select(row => row.CheckId)));
        Assert.All(listed.Items, row => Assert.Equal(("cancelled", AppNotCollectedCodes.Capacity, CollectionChannel.App, false),
            (row.CheckStatus, row.ErrorCode, row.Channel, row.AnswerPresent)));
        var succeeded = await a.MonitoringRecordsAsync(t, s, w.AppView with { Statuses = ["succeeded"] }, null, null, 100);
        Assert.Equal(collected.CheckId, Assert.Single(succeeded.Items).CheckId);

        // ---- Rates: the denominators count the one answered check only; the not-collected ones are not zeros.
        var summary = await a.MonitoringSummaryAsync(t, s, w.AppView, null);
        Assert.Equal(MetricRatio.Of(1, 1), summary.BrandVisibility);
        var dashboard = await a.BrandDashboardAsync(t, s, w.AppView, null);
        Assert.Equal((1L, MetricRatio.Of(1, 1)), (dashboard.Metrics.EligibleChecks, dashboard.Metrics.BrandMentionRate));
        var overview = await a.OverviewAsync(t, s, w.AppView, null);
        Assert.Equal((4L, 1L, MetricRatio.Of(1, 1)), (overview.Metrics.CheckCount, overview.Metrics.EligibleChecks, overview.Metrics.Visibility));
        Assert.Equal(0L, overview.Metrics.FailedChecks);
        var prompts = await a.PromptsAsync(t, s, w.AppView, null, null, 100, null);
        // Prompt 1: doubao answered, kimi not collected. Prompt 2: nothing answered — checks, but no eligible one and no rate.
        var first = prompts.Items.Single(row => row.PromptId == w.Prompts[0]);
        Assert.Equal((2L, 1L, MetricRatio.Of(1, 1)), (first.CheckCount, first.EligibleChecks, first.Visibility));
        var second = prompts.Items.Single(row => row.PromptId == w.Prompts[1]);
        Assert.Equal((2L, 0L, MetricRatio.Of(0, 0)), (second.CheckCount, second.EligibleChecks, second.Visibility));

        // Every App check of kimi was not collected: the rates are null (no sample), never 0.
        var kimi = w.AppView with { Engines = ["kimi"] };
        var kimiSummary = await a.MonitoringSummaryAsync(t, s, kimi, null);
        Assert.Equal((2L, MetricRatio.Of(0, 0)), (kimiSummary.TotalChecks, kimiSummary.BrandVisibility));
        Assert.Null(kimiSummary.BrandVisibility.Value);
        var kimiDashboard = await a.BrandDashboardAsync(t, s, kimi, null);
        Assert.Equal(0L, kimiDashboard.Metrics.EligibleChecks);
        Assert.Null(kimiDashboard.Metrics.BrandMentionRate.Value);
        Assert.Null(kimiDashboard.Metrics.ShareOfVoice.Value);
        Assert.Null(kimiDashboard.Metrics.OwnExternalCitationRate.Value);
        var kimiOverview = await a.OverviewAsync(t, s, kimi, null);
        Assert.Equal((2L, 0L), (kimiOverview.Metrics.CheckCount, kimiOverview.Metrics.EligibleChecks));
        Assert.Null(kimiOverview.Metrics.Visibility.Value);

        // ---- Credits: the collected check is charged 10; the leased-and-expired one only had its reservation released; the
        // two never-leased ones never had a reservation.
        Assert.Equal("captured|10.000|1|0", await ItemAsync(pool, collected.CheckId));
        Assert.Equal("released|10.000|0|1", await ItemAsync(pool, expired.CheckId));
        foreach (var check in cancelled.Where(check => check != expired.CheckId)) Assert.Equal("none", await ItemAsync(pool, check));
        Assert.Equal(0, await CountAsync(w.Cs, """
            SELECT COUNT(*) FROM credit_ledger WHERE tenant_id = UUID_TO_BIN(@tenant) AND entry_type = 'capture'
              AND monitoring_check_id IN (SELECT id FROM visibility_checks WHERE run_id = UUID_TO_BIN(@run) AND status = 'cancelled');
            """, ("@tenant", t.ToString()), ("@run", appRun.ToString())));
        Assert.Equal("990.000|0.000|10.000", await AccountAsync(pool, t));
    });
}
