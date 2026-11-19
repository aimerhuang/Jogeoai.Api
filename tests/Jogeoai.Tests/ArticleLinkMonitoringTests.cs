using System.Text.Json;
using System.Text.Json.Serialization;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Jogeoai.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Jogeoai.Tests;

/// <summary>文章链接监控（迁移 027）的无库单测：日历换算、单价、来源枚举、迁移文本、用量归类与 Worker 调度。</summary>
public sealed class ArticleLinkMonitoringTests
{
    private static readonly TimeZoneInfo Shanghai = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public void LocalDateAndNextMidnightFollowTheBrandTimeZone()
    {
        // 2026-10-19 17:30Z 是上海 10-20 01:30；本地日 10-20 的次日 00:00 是 10-20 16:00Z。
        var now = DateTimeOffset.Parse("2026-10-19T17:30:00Z");
        Assert.Equal(new DateOnly(2026, 10, 20), ArticleMonitoringCalendar.LocalDate(now, Shanghai));
        Assert.Equal(new DateOnly(2026, 10, 19), ArticleMonitoringCalendar.LocalDate(now, TimeZoneInfo.Utc));
        Assert.Equal(DateTimeOffset.Parse("2026-10-20T16:00:00Z"),
            ArticleMonitoringCalendar.NextLocalMidnightUtc(new DateOnly(2026, 10, 20), Shanghai));
        // 回溯 30 个本地自然日：上海 10-20 往前 29 天 = 09-21 00:00 = 09-20 16:00Z。
        Assert.Equal(DateTimeOffset.Parse("2026-09-20T16:00:00Z"), ArticleMonitoringCalendar.BackfillFromUtc(now, Shanghai));
        Assert.Equal(TimeZoneInfo.Utc, ArticleMonitoringCalendar.ResolveZone("Not/AZone"));
        Assert.Equal(TimeZoneInfo.Utc, ArticleMonitoringCalendar.ResolveZone(null));
        Assert.Equal(Shanghai.Id, ArticleMonitoringCalendar.ResolveZone("Asia/Shanghai").Id);
    }

    [Fact]
    public void DiscoveryUsesTheStoredBrandClassificationNotTheApiEnumName()
    {
        // citation_classifications.ownership 存 'brand' / 'competitor' / 'third_party' / 'unknown'（DetailedAnalyticsRules.Classify）；
        // 2026-10-20 本机实查后发现按 'owned' 过滤永远匹配不到。
        Assert.Equal("brand", ArticleLinkMonitoringStore.OwnedClassification);
        var brand = Guid.NewGuid();
        var config = new SubjectConfiguration(brand, 1, [new AnalysisSubject(brand, "brand", "Acme")],
            [new AnalysisTerm(brand, "brand", "domain", "a.test")], true);
        var owned = DetailedAnalyticsRules.Classify("https://www.a.test/post", "Acme post", 1, config);
        Assert.Equal(ArticleLinkMonitoringStore.OwnedClassification, owned.Ownership);
        var other = DetailedAnalyticsRules.Classify("https://zhihu.com/p/1", null, 2, config);
        Assert.NotEqual(ArticleLinkMonitoringStore.OwnedClassification, other.Ownership);
    }

    [Fact]
    public void UnitPriceIsTenCreditsPerLinkPerDay()
    {
        Assert.Equal(10m, ArticleMonitoringPricing.UnitPrice);
        Assert.Equal(30m, ArticleMonitoringPricing.Estimate(3));
        Assert.Equal(0m, ArticleMonitoringPricing.Estimate(-1));
        Assert.Equal(30, ArticleMonitoringPricing.BackfillDays);
    }

    [Fact]
    public void CitationDiscoveredSourceKindRoundTripsThroughJsonAndSql()
    {
        Assert.Equal("\"citation_discovered\"", JsonSerializer.Serialize(ArticleSourceKind.CitationDiscovered, Json));
        Assert.Equal(ArticleSourceKind.CitationDiscovered, JsonSerializer.Deserialize<ArticleSourceKind>("\"citation_discovered\"", Json));
        foreach (var kind in Enum.GetValues<ArticleSourceKind>())
            Assert.Equal(kind, ContentStore.ParseSourceKind(ContentStore.SourceKindValue(kind)));
        Assert.Equal("citation_discovered", ContentStore.SourceKindValue(ArticleSourceKind.CitationDiscovered));
        Assert.Equal(ArticleSourceKind.AiGenerated, ContentStore.ParseSourceKind("unknown"));
    }

    [Fact]
    public void Migration027AddsSourceKindRoundsItemsStateAndLedgerLink()
    {
        var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "db", "migrations", "027_content_link_monitoring.sql"));
        Assert.Contains("CHECK (source_kind IN ('ai_generated','published_import','citation_discovered'))", sql, StringComparison.Ordinal);
        Assert.Contains("check_clause NOT LIKE '%citation_discovered%'", sql, StringComparison.Ordinal);
        Assert.Contains("discovered_check_id BINARY(16) NULL", sql, StringComparison.Ordinal);
        Assert.Contains("fk_content_publication_discovered_check", sql, StringComparison.Ordinal);
        Assert.Contains("content_citation_discovery_state", sql, StringComparison.Ordinal);
        Assert.Contains("release_id BINARY(16) NULL", sql, StringComparison.Ordinal);
        Assert.Contains("uq_article_monitoring_rounds_day (tenant_id, site_id, local_date)", sql, StringComparison.Ordinal);
        Assert.Contains("uq_article_monitoring_items_link (tenant_id, round_id, publication_link_id)", sql, StringComparison.Ordinal);
        Assert.Contains("status IN ('held','captured','released','skipped')", sql, StringComparison.Ordinal);
        Assert.Contains("fk_article_monitoring_items_link FOREIGN KEY (tenant_id, site_id, article_id, publication_link_id)", sql, StringComparison.Ordinal);
        Assert.Contains("article_monitoring_round_id BINARY(16) NULL", sql, StringComparison.Ordinal);
        Assert.Contains("fk_credit_ledger_article_round", sql, StringComparison.Ordinal);
        // 账本类型不新增；reserve / capture 复用。
        Assert.DoesNotContain("ck_credit_ledger_type", sql, StringComparison.Ordinal);
        Assert.Contains("unit_price DECIMAL(18,3) NOT NULL DEFAULT 10", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void UsageClassifiesArticleMonitoringCapturesAfterArticleAndPromptMonitoring()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(CreditUsageType.ArticleMonitoring, CreditUsageAggregator.TypeOf(new(now, false, false, 30, IsArticleMonitoring: true)));
        Assert.Equal(CreditUsageType.PromptMonitoring, CreditUsageAggregator.TypeOf(new(now, false, true, 10, IsArticleMonitoring: true)));
        Assert.Equal(CreditUsageType.Other, CreditUsageAggregator.TypeOf(new(now, false, false, 1)));
        Assert.Equal("\"article_monitoring\"", JsonSerializer.Serialize(CreditUsageType.ArticleMonitoring, Json));
        var view = CreditUsageAggregator.Build(new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 20), "UTC", TimeZoneInfo.Utc,
            [new(DateTimeOffset.Parse("2026-10-20T00:05:00Z"), false, false, 30, IsArticleMonitoring: true)], null);
        var item = Assert.Single(Assert.Single(view.Days).Items);
        Assert.Equal((CreditUsageType.ArticleMonitoring, 30m, 1), (item.Type, item.Credits, item.Count));
    }

    [Fact]
    public async Task WorkerPassContinuesPastAFailingSiteAndStillOpensAndSettlesRounds()
    {
        var failing = (Guid.NewGuid(), Guid.NewGuid());
        var healthy = (Guid.NewGuid(), Guid.NewGuid());
        var store = new FakeStore([failing, healthy]) { FailDiscoveryFor = failing.Item2 };
        var worker = new ArticleLinkMonitoringWorker(NullLogger<ArticleLinkMonitoringWorker>.Instance, store,
            Options.Create(new ArticleLinkMonitoringOptions { TitleReadLimit = 7 }));
        var result = await worker.RunPassAsync(DateTimeOffset.Parse("2026-10-20T01:00:00Z"));
        Assert.Equal(new ArticleLinkMonitoringPassResult(2, 2, 7, 1, 1), result);
        Assert.Equal([healthy.Item2], store.TitlesReadFor);
        Assert.Equal(7, store.TitleLimit);
        Assert.Equal(1, store.OpenCalls);
        Assert.Equal(1, store.SettleCalls);
    }

    private sealed class FakeStore(IReadOnlyList<(Guid TenantId, Guid SiteId)> sites) : IArticleLinkMonitoringStore
    {
        public Guid? FailDiscoveryFor { get; init; }
        public List<Guid> TitlesReadFor { get; } = [];
        public int TitleLimit { get; private set; }
        public int OpenCalls { get; private set; }
        public int SettleCalls { get; private set; }

        public Task<IReadOnlyList<(Guid TenantId, Guid SiteId)>> ListSitesAsync(CancellationToken cancellationToken = default) => Task.FromResult(sites);

        public Task<ArticleDiscoveryPassResult> DiscoverAsync(Guid tenantId, Guid siteId, DateTimeOffset utcNow, CancellationToken cancellationToken = default)
            => siteId == FailDiscoveryFor
                ? throw new InvalidOperationException("boom")
                : Task.FromResult(new ArticleDiscoveryPassResult(3, 2, 1));

        public Task<int> ReadPendingTitlesAsync(Guid tenantId, Guid siteId, int limit, CancellationToken cancellationToken = default)
        {
            TitlesReadFor.Add(siteId); TitleLimit = limit; return Task.FromResult(limit);
        }

        public Task<int> OpenRoundsAsync(DateTimeOffset utcNow, Guid? siteId = null, CancellationToken cancellationToken = default) { Assert.Null(siteId); OpenCalls++; return Task.FromResult(1); }
        public Task<int> SettleRoundsAsync(DateTimeOffset utcNow, Guid? siteId = null, CancellationToken cancellationToken = default) { Assert.Null(siteId); SettleCalls++; return Task.FromResult(1); }
        public Task<ArticleMonitoringBillingView> BillingAsync(Guid tenantId, Guid siteId, DateTimeOffset utcNow, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
