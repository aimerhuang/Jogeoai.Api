using System.Reflection;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;

namespace Jogeoai.Tests;

/// <summary>团队汇总（B1）的无库单测：环比规则、单品牌失败隔离、引擎去重、默认范围、行业排名恒空。</summary>
public sealed class TeamSummaryTests
{
    private static readonly TimeZoneInfo Shanghai = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");

    private static CoreMetrics Core(long mentions, long eligible, long sovNumerator = 0, long sovDenominator = 0, long own = 0)
        => new(1, eligible, 1, eligible, eligible, 0, 0, 0, 0, 0, 0, MetricRatio.Of(mentions, eligible),
            MetricRatio.Of(sovNumerator, sovDenominator), MetricRatio.Of(0, eligible), MetricRatio.Of(own, eligible), mentions, null);

    private static ExposureScore Exposure(double? value, string? reason = null)
        => new(value, DashboardAnalyticsProjector.ExposureVersion, DashboardAnalyticsProjector.ExposureMentionWeight,
            DashboardAnalyticsProjector.ExposurePositionWeight, DashboardAnalyticsProjector.ExposureShareOfVoiceWeight,
            DashboardAnalyticsProjector.ExposureOwnCitationWeight, MetricRatio.Of(0, 0), MetricMean.Of(0, 0),
            MetricRatio.Of(0, 0), MetricRatio.Of(0, 0), reason);

    private static BrandView Brand(string name, DateTimeOffset created, string status = "active", string zone = "Asia/Shanghai")
        => new(Guid.NewGuid(), name, $"{name.ToLowerInvariant()}.test", [], [], "CN", "zh-CN", zone, status, 3, created, created);

    [Fact]
    public void DifferenceFollowsEachMetricRule()
    {
        var metrics = new BrandPeriodMetrics(Core(5, 10), Core(2, 8),
            new RankingMetric(1.5, 4, 4, null), new RankingMetric(2.0, 3, 3, null),
            Exposure(60), Exposure(70), 10, 8);

        var mention = TeamSummaryProjector.MentionRate(metrics);
        Assert.Equal((50d, 25d, 25d), (mention.Current, mention.Previous, mention.Difference));
        Assert.Null(mention.UnavailableReason);
        var exposure = TeamSummaryProjector.Exposure(metrics);
        Assert.Equal((60d, 70d, -10d), (exposure.Current, exposure.Previous, exposure.Difference));
        var ranking = TeamSummaryProjector.AverageRanking(metrics);
        Assert.Equal((1.5, 2.0, -0.5), (ranking.Current, ranking.Previous, ranking.Difference));
        var prompts = TeamSummaryProjector.MonitoredPrompts(metrics);
        Assert.Equal((10d, 8d, 2d), (prompts.Current, prompts.Previous, prompts.Difference));
    }

    [Fact]
    public void DifferenceIsNullWhenEitherSideIsUnavailable()
    {
        var metrics = new BrandPeriodMetrics(Core(0, 0), Core(2, 8),
            new RankingMetric(null, 0, 0, "INSUFFICIENT_SUBJECT_ORDER"), new RankingMetric(2.0, 3, 3, null),
            Exposure(null, "NO_COMPETITOR_BASELINE"), Exposure(70), 0, null);

        var mention = TeamSummaryProjector.MentionRate(metrics);
        Assert.Null(mention.Current);
        Assert.Equal(25d, mention.Previous);
        Assert.Null(mention.Difference);
        Assert.Equal("NO_ELIGIBLE_CHECKS", mention.UnavailableReason);
        var exposure = TeamSummaryProjector.Exposure(metrics);
        Assert.Null(exposure.Difference);
        Assert.Equal("NO_COMPETITOR_BASELINE", exposure.UnavailableReason);
        var ranking = TeamSummaryProjector.AverageRanking(metrics);
        Assert.Null(ranking.Difference);
        Assert.Equal("INSUFFICIENT_SUBJECT_ORDER", ranking.UnavailableReason);
        var prompts = TeamSummaryProjector.MonitoredPrompts(metrics);
        Assert.Equal(0d, prompts.Current);
        Assert.Null(prompts.Previous);
        Assert.Null(prompts.Difference);
    }

    [Fact]
    public void DefaultRangeIsSevenLocalDaysAndPreviousPeriodIsAdjacentAndEqual()
    {
        var now = DateTimeOffset.Parse("2026-10-20T10:00:00Z");
        var range = TeamSummaryProjector.Range(null, null, "Asia/Shanghai", Shanghai, now);
        // 上海 10-20 的今天 − 6 = 10-14 00:00 (+08:00) = 10-13 16:00Z。
        Assert.Equal(DateTimeOffset.Parse("2026-10-13T16:00:00Z"), range.From);
        Assert.Equal(now, range.To);
        Assert.Equal(range.From, range.PreviousTo);
        Assert.Equal(range.From - (range.To - range.From), range.PreviousFrom);
        Assert.Equal("Asia/Shanghai", range.TimeZone);

        var explicitRange = TeamSummaryProjector.Range(DateTimeOffset.Parse("2026-10-01T00:00:00+08:00"),
            DateTimeOffset.Parse("2026-10-15T00:00:00+08:00"), "Asia/Shanghai", Shanghai, now);
        Assert.Equal(DateTimeOffset.Parse("2026-09-16T16:00:00Z"), explicitRange.PreviousFrom);

        var reversed = Assert.Throws<CollectorStoreException>(() => TeamSummaryProjector.Range(now, now.AddDays(-1), "UTC", TimeZoneInfo.Utc, now));
        Assert.Equal(("INVALID_TIME_RANGE", 400), (reversed.Code, reversed.StatusCode));
        var overlong = Assert.Throws<CollectorStoreException>(() => TeamSummaryProjector.Range(now.AddDays(-400), now, "UTC", TimeZoneInfo.Utc, now));
        Assert.Equal("INVALID_TIME_RANGE", overlong.Code);
    }

    [Fact]
    public void ArticlesDropArchivedFromTotalAndEnginesAreDedupedByFirstAppearance()
    {
        var page = new ArticleListPage([], null, 12, 0, new Dictionary<string, long>
            { ["generating"] = 1, ["generation_failed"] = 0, ["pending_publish"] = 2, ["publishing"] = 3, ["tracking"] = 4, ["archived"] = 2 });
        Assert.Equal(new TeamBrandArticles(10, 4, 3), TeamSummaryProjector.Articles(page, 3));
        Assert.Equal(new TeamBrandArticles(5, 0, null), TeamSummaryProjector.Articles(new ArticleListPage([], null, 5, 0, new Dictionary<string, long>()), null));

        var rows = new[]
        {
            Row("A", [new("cloro", "chatgpt", "chatgpt"), new("domestic_collector", "doubao", "豆包")]),
            Row("B", [new("domestic_collector", "doubao", "豆包"), new("cloro", "gemini", "gemini"), new("cloro", "chatgpt", "ChatGPT")]),
        };
        var engines = TeamSummaryProjector.EnabledEngines(rows);
        Assert.Equal(["cloro:chatgpt", "domestic_collector:doubao", "cloro:gemini"], engines.Select(e => $"{e.Provider}:{e.Engine}"));
        Assert.Equal("chatgpt", engines[0].DisplayName);
        var view = TeamSummaryProjector.View(new TeamSummaryRange(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "UTC"), rows);
        Assert.Equal(new TeamSummaryTotals(2, 3, 4, 6), view.Totals);
    }

    private static TeamBrandRow Row(string name, IReadOnlyList<TeamBrandTarget> targets)
        => TeamSummaryProjector.Row(Brand(name, DateTimeOffset.UtcNow), null, "ANALYTICS_UNAVAILABLE", 1, 2, 3,
            new TeamBrandArticles(0, 0, null), targets);

    [Fact]
    public async Task OneFailingBrandDoesNotFailTheSummaryAndIndustryRanksStayNull()
    {
        var created = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var healthy = Brand("Healthy", created);
        var broken = Brand("Broken", created.AddDays(1));
        var archived = Brand("Archived", created.AddDays(2), status: "archived");
        var f = new Fixture([broken, healthy, archived]);
        f.Metrics[healthy.SiteId] = new BrandPeriodMetrics(Core(5, 10), Core(2, 8), new RankingMetric(1.5, 4, 4, null),
            new RankingMetric(2.0, 3, 3, null), Exposure(60), Exposure(70), 10, 8);
        f.Failures[broken.SiteId] = new CollectorStoreException("RESOURCE_NOT_FOUND", "工作区不存在。", 404);

        var view = await f.Query.SummaryAsync(f.TenantId, Guid.NewGuid(), new TeamSummaryRequest());

        Assert.Equal(["Healthy", "Broken"], view.Brands.Select(b => b.Name));
        var ok = view.Brands[0];
        Assert.Equal(50d, ok.MentionRate.Current);
        Assert.Equal(25d, ok.MentionRate.Difference);
        Assert.Equal(-10d, ok.Exposure.Difference);
        Assert.Equal((7, 2, 1), (ok.EnabledPromptCount, ok.PromptGroupCount, ok.CompetitorCount));
        Assert.Equal(new TeamBrandArticles(10, 4, 3), ok.Articles);
        Assert.Equal(["cloro:chatgpt", "domestic_collector:doubao"], ok.EnabledTargets.Select(t => $"{t.Provider}:{t.Engine}"));
        var failed = view.Brands[1];
        foreach (var metric in new[] { failed.MentionRate, failed.Exposure, failed.AverageRanking, failed.MonitoredPrompts })
        {
            Assert.Null(metric.Current);
            Assert.Null(metric.Difference);
            Assert.Equal("RESOURCE_NOT_FOUND", metric.UnavailableReason);
        }
        // 失败品牌的计数仍然来自各自来源。
        Assert.Equal(7, failed.EnabledPromptCount);
        Assert.All(view.Brands, b => Assert.Null(b.IndustryRank));
        Assert.All(view.Brands, b => Assert.Null(b.ExposureIndustryRank));
        Assert.All(view.Brands, b => Assert.Null(b.MentionIndustryRank));
        Assert.All(view.Brands, b => Assert.Null(b.RankingIndustryRank));
        Assert.Equal(new TeamSummaryTotals(2, 2, 4, 2), view.Totals);
        Assert.Equal("Asia/Shanghai", view.Range.TimeZone);
        // 默认筛选与品牌概览一致：三个 Provider、api+scraped、三类证据。
        var filter = Assert.Single(f.Filters.Values.Distinct());
        Assert.Equal(["cloro", "doubao_api", "deepseek_api", "packy_qwen", "packy_glm", "packy_kimi", "packy_yuanbao", "domestic_collector"], filter.Providers);
        Assert.Equal(["api", "scraped"], filter.CaptureModes);
        Assert.Equal(["api_response", "observed_surface", "unknown"], filter.EvidenceClasses);
        Assert.Null(filter.Engines);
        Assert.Equal("Asia/Shanghai", filter.TimeZone);
    }

    [Fact]
    public async Task SiteFilterProviderAndEngineAreValidatedAndForwarded()
    {
        var brand = Brand("Only", DateTimeOffset.UtcNow, zone: "UTC");
        var other = Brand("Other", DateTimeOffset.UtcNow);
        var f = new Fixture([brand, other]);
        f.Metrics[brand.SiteId] = new BrandPeriodMetrics(Core(1, 1), null, new RankingMetric(null, 0, 0, "INSUFFICIENT_SUBJECT_ORDER"),
            null, Exposure(null, "NO_COMPETITOR_BASELINE"), null, 1, null);
        f.Metrics[other.SiteId] = f.Metrics[brand.SiteId];

        var view = await f.Query.SummaryAsync(f.TenantId, Guid.NewGuid(),
            new TeamSummaryRequest(Provider: "Cloro", Engine: "ChatGPT", SiteId: brand.SiteId));
        var row = Assert.Single(view.Brands);
        Assert.Equal(brand.SiteId, row.SiteId);
        Assert.Equal("UTC", view.Range.TimeZone);
        var filter = f.Filters[brand.SiteId];
        Assert.Equal(["cloro"], filter.Providers);
        Assert.Equal(["chatgpt"], filter.Engines);

        var unknownProvider = await Assert.ThrowsAsync<CollectorStoreException>(() => f.Query.SummaryAsync(f.TenantId, Guid.NewGuid(),
            new TeamSummaryRequest(Provider: "bing")));
        Assert.Equal(("INVALID_FILTER", 400), (unknownProvider.Code, unknownProvider.StatusCode));
        var missing = await Assert.ThrowsAsync<CollectorStoreException>(() => f.Query.SummaryAsync(f.TenantId, Guid.NewGuid(),
            new TeamSummaryRequest(SiteId: Guid.NewGuid())));
        Assert.Equal(404, missing.StatusCode);
        var badZone = await Assert.ThrowsAsync<CollectorStoreException>(() => f.Query.SummaryAsync(f.TenantId, Guid.NewGuid(),
            new TeamSummaryRequest(TimeZone: "Mars/Olympus")));
        Assert.Equal("INVALID_TIME_ZONE", badZone.Code);
    }

    [Fact]
    public async Task CitedCountSqlFailureOnlyBlanksThatBrandsCitedField()
    {
        var brand = new BrandView(Guid.NewGuid(), "Brand", "brand.example", [], [], "CN", "zh-CN", "Asia/Shanghai", "active", 1,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var f = new Fixture([brand]);
        f.Metrics[brand.SiteId] = new BrandPeriodMetrics(Core(1, 4), null,
            new RankingMetric(2.0, 1, 1, null), null, Exposure(40), null, 3, null);
        // 真实库上曾因 SQL 保留字别名抛 MySqlException；任何非取消异常都只能影响本品牌的被引用计数。
        f.CitedFailures[brand.SiteId] = new InvalidOperationException("boom");
        var view = await f.Query.SummaryAsync(f.TenantId, Guid.NewGuid(), new TeamSummaryRequest());
        var row = Assert.Single(view.Brands);
        Assert.Null(row.Articles.Cited);
        Assert.Equal(10, row.Articles.Total);
        Assert.Equal(4, row.Articles.Tracking);
        Assert.NotNull(row.MentionRate.Current);
        Assert.Null(row.MentionRate.Previous);
        Assert.Equal(7, row.EnabledPromptCount);
    }

    [Fact]
    public async Task NoActiveBrandsYieldsAnEmptySummaryInTheDefaultZone()
    {
        var f = new Fixture([]);
        var view = await f.Query.SummaryAsync(f.TenantId, Guid.NewGuid(), new TeamSummaryRequest());
        Assert.Empty(view.Brands);
        Assert.Empty(view.EnabledEngines);
        Assert.Equal(new TeamSummaryTotals(0, 0, 0, 0), view.Totals);
        Assert.Equal("Asia/Shanghai", view.Range.TimeZone);
    }

    private sealed class Fixture
    {
        internal Guid TenantId { get; } = Guid.NewGuid();
        internal Dictionary<Guid, BrandPeriodMetrics> Metrics { get; } = [];
        internal Dictionary<Guid, Exception> Failures { get; } = [];
        internal Dictionary<Guid, Exception> CitedFailures { get; } = [];
        internal Dictionary<Guid, AnalyticsFilterRequest> Filters { get; } = [];
        internal TeamSummaryQuery Query { get; }

        internal Fixture(IReadOnlyList<BrandView> brands)
        {
            var management = Proxy<IManagementStore>((method, args) => method switch
            {
                nameof(IManagementStore.ListBrandsAsync) => Task.FromResult(new CursorPage<BrandView>(brands, null)),
                nameof(IManagementStore.SummarizePromptsAsync) => Task.FromResult(new PromptSummaryView(9, 7, 1)),
                nameof(IManagementStore.CountPromptGroupsAsync) => Task.FromResult(2),
                nameof(IManagementStore.ListCompetitorsAsync) => Task.FromResult(new CursorPage<CompetitorView>(
                [
                    new(Guid.NewGuid(), "Rival", [], [], "active", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                    new(Guid.NewGuid(), "Gone", [], [], "archived", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                ], null)),
                nameof(IManagementStore.GetSiteTargetsAsync) => Task.FromResult(new SiteTargetsView(1,
                [
                    Target("cloro", "chatgpt", "chatgpt", true), Target("cloro", "gemini", "gemini", false),
                    Target("domestic_collector", "doubao", "豆包", true),
                ])),
                _ => throw new InvalidOperationException($"Unexpected management call: {method}"),
            });
            var analytics = Proxy<IAnalyticsQueryStore>((method, args) =>
            {
                Assert.Equal(nameof(IAnalyticsQueryStore.BrandPeriodMetricsAsync), method);
                var siteId = (Guid)args![1]!;
                Filters[siteId] = (AnalyticsFilterRequest)args[2]!;
                if (Failures.TryGetValue(siteId, out var failure)) return Task.FromException<BrandPeriodMetrics>(failure);
                return Task.FromResult(Metrics[siteId]);
            });
            var content = Proxy<IContentStore>((method, args) =>
            {
                Assert.Equal(nameof(IContentStore.ListArticlesAsync), method);
                return Task.FromResult(new ArticleListPage([], null, 12, 0, new Dictionary<string, long>
                    { ["generating"] = 1, ["generation_failed"] = 0, ["pending_publish"] = 2, ["publishing"] = 3, ["tracking"] = 4, ["archived"] = 2 }));
            });
            var monitoring = Proxy<IArticleMonitoringQuery>((method, args) =>
            {
                Assert.Equal(nameof(IArticleMonitoringQuery.SummaryAsync), method);
                if (CitedFailures.TryGetValue((Guid)args![1]!, out var citedFailure))
                    return Task.FromException<ArticleMonitoringSummaryView>(citedFailure);
                var from = (DateTimeOffset)args![3]!;
                var to = (DateTimeOffset)args[4]!;
                Assert.Equal(TimeSpan.FromDays(30), to - from);
                return Task.FromResult(new ArticleMonitoringSummaryView((Guid)args[1]!, null, from, to, "READY", null, null, 5, 4, 3, 2, 2,
                    [], new Dictionary<string, long>(), [], 3));
            });
            Query = new TeamSummaryQuery(management, analytics, content, monitoring);
        }

        private static CollectionTargetView Target(string provider, string engine, string display, bool enabled)
            => new(Guid.NewGuid(), provider, engine, display, "scraped", "observed_surface", "US", "en-US", enabled, "available", "full", "full", "full");

        private static T Proxy<T>(Func<string, object?[]?, object?> handler) where T : class
        {
            var proxy = DispatchProxy.Create<T, HandlerProxy>();
            ((HandlerProxy)(object)proxy).Handler = handler;
            return proxy;
        }
    }

    public class HandlerProxy : DispatchProxy
    {
        public Func<string, object?[]?, object?> Handler { get; set; } = (_, _) => throw new InvalidOperationException();
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!.Name, args);
    }
}
