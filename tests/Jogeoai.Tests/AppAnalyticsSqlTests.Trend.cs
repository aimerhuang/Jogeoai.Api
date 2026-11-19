using System.Globalization;
using Jogeoai.Domain;
using static Jogeoai.Tests.AppCycleRunSqlTests;
using static Jogeoai.Tests.AppLeaseSqlTests;
using static Jogeoai.Tests.TeamMonitoringConfigSqlTests;

namespace Jogeoai.Tests;

/// <summary>
/// 总 PRD AC-C14d：App 口径的按日趋势（概览、可见性、声量的 trend，品牌看板的 trend）在没有 App 样本的日期上比率为 null、
/// 有效检测数为 0，不是 0%；有 App 样本的日期正常。只有未采集检测的日期也是“无样本”。同一天的 API 样本不会出现在 App 口径里。
/// 监控汇总（monitoring/summary）没有按日序列，不在这里。
/// </summary>
public sealed partial class AppAnalyticsSqlTests
{
    [AppChannelMySqlFact]
    public Task TheDailyTrendsOfTheAppViewHaveNoRateOnDaysWithoutAnAppSample() => PoolAsync(async (pool, seedTenant) =>
    {
        var (w, appRun) = await SeedWorldAsync(pool, await seedTenant());
        var (t, s, a) = (w.Tenant, w.Site, w.Analytics);
        var from = DateTimeOffset.UtcNow.AddDays(-3);
        var appView = w.AppView with { From = from };
        var defaultView = w.All with { From = from };

        // An API answer two days ago (it mentions the brand) and one App answer now; the other three App checks are not collected.
        var apiCheck = await SeedSampleAsync(w, w.Prompts[0], "cloro", "chatgpt", "scraped", "observed_surface", "US", "en-US", "succeeded",
            $"{Brand} is the best choice.", DateTimeOffset.UtcNow.AddDays(-2));
        var store = pool.Store(t);
        var (node, _) = await pool.NodeAsync("mac");
        var binding = await pool.BindAsync(node, "AI_Apps_4KB", "doubao");
        var collected = await CollectAsync(pool, store, node, binding, "Acmebook 是首选。");
        await ExecAsync(w.Cs, "UPDATE collector_tasks SET deadline_at = UTC_TIMESTAMP(6) - INTERVAL 1 MINUTE WHERE run_id = UUID_TO_BIN(@run) AND status = 'pending';",
            ("@run", appRun.ToString()));
        Assert.Equal(3, await w.Rig.App.ExpireOverdueTasksAsync());
        await AnalyzeAsync(w);
        async Task<DateOnly> DayOfAsync(Guid check) => DateOnly.ParseExact(await RowAsync(w.Cs,
            "SELECT DATE_FORMAT(captured_at, '%Y-%m-%d') FROM visibility_checks WHERE id = UUID_TO_BIN(@id);", ("@id", check.ToString())),
            "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var (appDay, apiDay) = (await DayOfAsync(collected.CheckId), await DayOfAsync(apiCheck));
        Assert.NotEqual(appDay, apiDay);

        // ---- Overview, visibility and share of voice: one point per local day of the window.
        var overview = await a.OverviewAsync(t, s, appView, null);
        var visibility = await a.VisibilityAsync(t, s, appView, null);
        var voice = await a.ShareOfVoiceAsync(t, s, appView, null, 10);
        foreach (var (name, trend) in new[] { ("overview", overview.Trend), ("visibility", visibility.Trend), ("share-of-voice", voice.Trend) })
        {
            Assert.True(trend.Count >= 4, $"{name}: {trend.Count} points");
            Assert.Contains(trend, point => point.Date == apiDay);
            foreach (var point in trend)
            {
                if (point.Date == appDay)
                {
                    // One App answer and three not-collected App checks: the rate is over the one answer.
                    Assert.Equal((MetricRatio.Of(1, 1), 1L, 4L), (point.Visibility, point.EligibleChecks, point.ObservedChecks));
                    Assert.NotNull(point.ShareOfVoice.Value);
                }
                else
                {
                    // No App sample (the API answer of apiDay is not in the App view): no rate at all, not 0.
                    Assert.True(point.Visibility.Value is null && point.ShareOfVoice.Value is null, $"{name} {point.Date}: {point.Visibility}");
                    Assert.Equal((MetricRatio.Of(0, 0), 0L, 0L, false), (point.Visibility, point.EligibleChecks, point.ObservedChecks, point.IsCarriedForward));
                }
            }
        }
        // The default view of the same window does have a rate on apiDay: the null above is the App view's, not an empty window.
        Assert.Equal(MetricRatio.Of(1, 1), (await a.OverviewAsync(t, s, defaultView, null)).Trend.Single(point => point.Date == apiDay).Visibility);

        // ---- The brand dashboard's daily trend.
        var dashboard = await a.BrandDashboardAsync(t, s, appView, null);
        Assert.Equal(overview.Trend.Select(point => point.Date), dashboard.Trend.Select(point => point.Date));
        foreach (var point in dashboard.Trend)
        {
            if (point.Date == appDay) Assert.Equal((MetricRatio.Of(1, 1), 1L), (point.BrandMentionRate, point.SampleCount));
            else Assert.Equal((MetricRatio.Of(0, 0), (double?)null, 0L), (point.BrandMentionRate, point.ShareOfVoice.Value, point.SampleCount));
        }

        // ---- kimi had only not-collected checks, all on appDay: that day has observed checks and still no rate.
        var kimi = appView with { Engines = ["kimi"] };
        var kimiTrend = (await a.OverviewAsync(t, s, kimi, null)).Trend;
        Assert.All(kimiTrend, point => Assert.Equal((MetricRatio.Of(0, 0), 0L), (point.Visibility, point.EligibleChecks)));
        Assert.Equal(2L, kimiTrend.Single(point => point.Date == appDay).ObservedChecks);
        Assert.All((await a.BrandDashboardAsync(t, s, kimi, null)).Trend, point => Assert.Equal((MetricRatio.Of(0, 0), 0L), (point.BrandMentionRate, point.SampleCount)));
    });
}
