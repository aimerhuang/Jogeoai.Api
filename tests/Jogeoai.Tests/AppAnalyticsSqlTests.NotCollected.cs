using Jogeoai.Domain;
using static Jogeoai.Tests.AppCycleRunSqlTests;
using static Jogeoai.Tests.AppLeaseSqlTests;
using static Jogeoai.Tests.TeamMonitoringConfigSqlTests;

namespace Jogeoai.Tests;

/// <summary>
/// 未采集的 App 检测（周期截止、第三次计数失败、第三次租约过期都经过 AppCollectionStore.MarkNotCollectedAsync）：
/// 它有采集时间（判定为未采集的时刻），所以默认口径的各项（含 missingCapturedAtChecks）与文章监控的质量不变，
/// App 口径的回答记录按 status=cancelled 能筛出它们并带原因码，详情可读。
/// </summary>
public sealed partial class AppAnalyticsSqlTests
{
    [AppChannelMySqlFact]
    public Task NotCollectedAppChecksLeaveTheDefaultViewUnchangedAndAreListedByStatusInTheAppView() => PoolAsync(async (pool, seedTenant) =>
    {
        var (w, appRun) = await SeedWorldAsync(pool, await seedTenant());
        var (t, s, a) = (w.Tenant, w.Site, w.Analytics);
        await SeedApiSamplesAsync(w);

        // One of the four App checks is collected; the other three stay open until the cycle ends.
        var store = pool.Store(t);
        var (node, _) = await pool.NodeAsync("mac");
        var binding = await pool.BindAsync(node, "AI_Apps_4KB", "doubao");
        var collected = await CollectAsync(pool, store, node, binding, "Acmebook 是首选。",
            new AppResultCitation("博客", Brand, $"https://{OwnDomain}/blog/app-only", "cited"));
        await AnalyzeAsync(w);

        async Task<(SortedDictionary<string, string> Views, string Article, long MissingCapturedAt, string Quality)> DefaultAsync()
        {
            var views = await DefaultViewsAsync(w);
            var article = await w.Monitoring.SummaryAsync(t, s, null, w.From, w.To, null);
            return (views, Normalized(article), (await a.MonitoringSummaryAsync(t, s, w.All, null)).Meta.MissingCapturedAtChecks, article.Quality);
        }
        var before = await DefaultAsync();
        Assert.Equal((0L, "READY"), (before.MissingCapturedAt, before.Quality));

        // The cycle ends. The collected answer was sent within its lease (device-v1 rejects a send time outside it), so its
        // capture time is just before this moment, while the three not-collected checks get a later one.
        var cycleEnded = DateTime.UtcNow;
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        await ExecAsync(w.Cs, "UPDATE collector_tasks SET deadline_at = UTC_TIMESTAMP(6) - INTERVAL 1 MINUTE WHERE run_id = UUID_TO_BIN(@run) AND status = 'pending';",
            ("@run", appRun.ToString()));
        Assert.Equal(3, await w.Rig.App.ExpireOverdueTasksAsync());
        await AnalyzeAsync(w);
        var cancelled = (await RowsAsync(w.Cs, "SELECT BIN_TO_UUID(id), error_code FROM visibility_checks WHERE run_id = UUID_TO_BIN(@run) AND status = 'cancelled';",
            ("@run", appRun.ToString()))).ToDictionary(row => Guid.Parse(row[0]!), row => row[1]!);
        Assert.Equal(3, cancelled.Count);
        Assert.All(cancelled.Values, code => Assert.StartsWith("APP_NOT_COLLECTED", code));

        // Every property is checked and reported together, so that one run shows which of them hold.
        var problems = new List<string>();
        var after = await DefaultAsync();
        // (1) Every default view, including meta.missingCapturedAtChecks.
        foreach (var (entry, value) in before.Views)
            if (value != after.Views[entry]) problems.Add($"(1) {entry} changed");
        if (after.MissingCapturedAt != 0) problems.Add($"(1) default missingCapturedAtChecks = {after.MissingCapturedAt}, expected 0");
        // (2) Article monitoring: same summary, same quality.
        if (after.Quality != before.Quality) problems.Add($"(2) article monitoring quality = {after.Quality}, expected {before.Quality}");
        if (after.Article != before.Article) problems.Add("(2) article monitoring summary changed");
        // (3) The App view lists them by status, with their reason codes.
        var listed = await a.MonitoringRecordsAsync(t, s, w.AppView with { Statuses = ["cancelled"] }, null, null, 100);
        var rows = listed.Items.ToDictionary(row => row.CheckId);
        if (listed.TotalCount != 3 || !rows.Keys.ToHashSet().SetEquals(cancelled.Keys))
            problems.Add($"(3) App view status=cancelled lists {listed.TotalCount} records, expected the 3 not collected");
        foreach (var (check, code) in cancelled)
            if (rows.TryGetValue(check, out var row) && (row.ErrorCode, row.CheckStatus, row.Channel, row.CaptureMode, row.AnswerPresent)
                    != (code, "cancelled", CollectionChannel.App, "app", false))
                problems.Add($"(3) record {check}: {row.ErrorCode}|{row.CheckStatus}|{row.Channel}|{row.CaptureMode}|{row.AnswerPresent}");
        var all = await a.MonitoringRecordsAsync(t, s, w.AppView, null, null, 100);
        if (all.TotalCount != 4) problems.Add($"(3) App view lists {all.TotalCount} records, expected 4");
        // (4) The detail of each, under the token of that list.
        foreach (var (check, code) in cancelled)
        {
            var detail = await a.MonitoringAnswerDetailAsync(t, s, check, listed.Meta.SnapshotToken, null);
            if (detail is null) { problems.Add($"(4) detail of {check} not found"); continue; }
            if ((detail.Status, detail.ErrorCode, detail.Channel, detail.Answer, detail.App, detail.MatchesListFilters)
                != ("cancelled", code, CollectionChannel.App, (VisibilityAnswer?)null, (AppAnswerView?)null, true))
                problems.Add($"(4) detail of {check}: {detail.Status}|{detail.ErrorCode}|{detail.Channel}|answer={detail.Answer is not null}|"
                    + $"app={detail.App is not null}|matches={detail.MatchesListFilters}");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));

        // The capture time is the moment the check was determined not collected, and the arrival row carries the same time.
        Assert.Equal(0, await CountAsync(w.Cs, """
            SELECT COUNT(*) FROM visibility_checks c LEFT JOIN analytics_check_arrivals ar ON ar.check_id = c.id
            WHERE c.run_id = UUID_TO_BIN(@run) AND c.status = 'cancelled'
              AND (c.captured_at IS NULL OR c.captured_at < c.created_at OR ar.captured_at IS NULL OR ar.captured_at <> c.captured_at);
            """, ("@run", appRun.ToString())));
        // The collected check keeps the time of its answer.
        Assert.Equal(1, await CountAsync(w.Cs, "SELECT COUNT(*) FROM visibility_checks WHERE id = UUID_TO_BIN(@id) AND captured_at < @ended;",
            ("@id", collected.CheckId.ToString()), ("@ended", cycleEnded)));
    });
}
