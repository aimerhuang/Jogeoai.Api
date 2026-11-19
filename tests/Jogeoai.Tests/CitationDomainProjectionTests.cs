using Jogeoai.Domain;
using Jogeoai.Infrastructure;

namespace Jogeoai.Tests;

public sealed class CitationDomainProjectionTests
{
    private static readonly Guid BrandId = Guid.NewGuid();
    private static readonly Guid CompetitorId = Guid.NewGuid();
    private static SubjectConfiguration Config(bool subdomains = false) => new(BrandId, 7,
        [new(BrandId, "brand", "品牌"), new(CompetitorId, "competitor", "竞品")],
        [new(BrandId, "brand", "primary_domain", "brand.test"), new(BrandId, "brand", "additional_domain", "extra.test"),
         new(CompetitorId, "competitor", "primary_domain", "rival.test")], true, subdomains);

    internal static AnalyticsCheckFact Fact(string provider = "cloro") => new(Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), null, null, 1, DateTimeOffset.UtcNow.AddHours(-1), "succeeded", true,
        "completed", false, false, false, [], provider, provider == "cloro" ? "chatgpt" : "doubao",
        "US", "en-US", "scraped", "observed_surface", [], null, "历史问题");

    private static CitationDomainProjection Project(IReadOnlyList<AnalyticsCheckFact> facts,
        IEnumerable<CitationDomainEvent> events, SubjectConfiguration? configuration = null)
    {
        var accumulator = new CitationDomainAccumulator(facts, configuration ?? Config(), DetailedAnalyticsRules.UrlV2);
        foreach (var item in events.OrderBy(e => e.CheckId).ThenBy(e => e.Url, StringComparer.Ordinal)) accumulator.Add(item);
        return accumulator.Complete();
    }

    private static CitationDomainEvent Event(AnalyticsCheckFact fact, string rawUrl, int position = 1)
        => new(fact.CheckId, DetailedAnalyticsRules.NormalizeUrl(rawUrl)!, position);

    [Fact]
    public void TenAnswersTwentyEventsSixOnFourAnswersHaveSeparateRatiosAndAllProviders()
    {
        var facts = Enumerable.Range(0, 10).Select(i => Fact(i % 2 == 0 ? "cloro" : "domestic_collector")).ToArray();
        var events = new List<CitationDomainEvent>();
        for (var i = 0; i < 4; i++) events.Add(Event(facts[i], "https://brand.test/a"));
        events.Add(Event(facts[0], "https://www.brand.test/b"));
        events.Add(Event(facts[1], "https://brand.test/b"));
        for (var i = 0; i < 14; i++) events.Add(Event(facts[i % 9], $"https://source.test/{i}"));
        var projection = Project(facts, events);
        Assert.Equal(10, projection.Metrics.EligibleAnswers); // tenth answer has no citations
        Assert.Equal(20, projection.Metrics.CitationEvents);
        var brand = Assert.Single(projection.Domains, d => d.Domain == "brand.test");
        Assert.Equal(6, brand.CitationEvents);
        Assert.Equal(4, brand.AnswerCount);
        Assert.Equal(0.3, brand.EventShare.Value);
        Assert.Equal(0.4, brand.AnswerCoverage.Value);
        Assert.Equal(11, projection.Categories.Count);
        Assert.Equal(projection.Metrics.CitationEvents, projection.Categories.Sum(c => c.CitationEvents));
        Assert.All(projection.Domains, d => Assert.True(d.AnswerCoverage.Value <= 1));
    }

    [Fact]
    public void OnlyUnifiedEligiblePopulationCanContributeAndQualityIsMutuallyExclusive()
    {
        var facts = new[] { Fact(), Fact() with { Status = "failed", AnswerPresent = false },
            Fact() with { AnswerPresent = false }, Fact() with { ConfigurationMissing = true },
            Fact() with { AnalysisStatus = "failed" }, Fact() with { AnalysisStatus = "pending" } };
        var result = Project(facts, facts.Select(f => Event(f, "https://source.test/a")));
        Assert.Equal(1, result.Metrics.EligibleAnswers);
        Assert.Equal(1, result.Metrics.CitationEvents);
        Assert.Equal(6, result.Quality.CandidateAnswers);
        Assert.Equal(1, result.Quality.CollectionNotSucceeded);
        Assert.Equal(1, result.Quality.NoAnswer);
        Assert.Equal(1, result.Quality.ConfigurationUnavailable);
        Assert.Equal(1, result.Quality.AnalysisFailed);
        Assert.Equal(1, result.Quality.AnalysisPending);
    }

    [Fact]
    public void PerAnswerUrlDedupPreservesProtocolPathQueryAndCrossAnswerEvents()
    {
        var first = Fact(); var second = Fact();
        var result = Project([first, second],
        [Event(first, "https://www.brand.test/a#one"), Event(first, "https://brand.test/a#two", 2),
         Event(first, "https://brand.test/a?utm_source=tracking", 3), Event(first, "http://brand.test/a"),
         Event(first, "https://brand.test/b"), Event(first, "https://brand.test/a?product=one"),
         Event(first, "https://blog.brand.test/a"), Event(second, "https://brand.test/a")]);
        Assert.Equal(6, result.Metrics.CitationEvents);
        Assert.Equal(2, result.Metrics.DistinctDomains);
        Assert.Equal(2, result.Domains[0].AnswerCount);
        // https a, http a, b, a?product=one, blog a; the second answer's https a is the same URL.
        Assert.Equal(5, result.Metrics.DistinctUrls);
        Assert.Equal(result.Metrics.DistinctUrls, result.Urls.Count);
        var shared = Assert.Single(result.Urls, u => u.Url == "https://brand.test/a");
        Assert.Equal(2, shared.CitationEvents);
        Assert.Equal(2, shared.AnswerCount);
        Assert.Equal(1, shared.Rank);
    }

    [Fact]
    public void DomainRankFollowsFullPopulationOrderAndTiesBreakByDomainAscending()
    {
        var first = Fact(); var second = Fact(); var third = Fact();
        var result = Project([first, second, third],
        [Event(first, "https://c.test/1"), Event(second, "https://c.test/2"),
         Event(first, "https://b.test/1"), Event(second, "https://b.test/1"),
         Event(first, "https://a.test/1"), Event(second, "https://a.test/2"), Event(third, "https://a.test/3"),
         Event(third, "https://d.test/1")]);
        Assert.Equal(new[] { "a.test", "b.test", "c.test", "d.test" }, result.Domains.Select(d => d.Domain));
        // b.test and c.test both have two events: the domain tie-break gives distinct positional ranks.
        Assert.Equal(new[] { 1, 2, 3, 4 }, result.Domains.Select(d => d.Rank));
        Assert.All(result.Domains, d => Assert.Null(d.PreviousRank));
    }

    [Fact]
    public void UrlRowsDeduplicatePerAnswerExcludeInvalidPlaceholderAndGoogleAndSortByEventsThenHash()
    {
        var first = Fact(); var second = Fact(); var third = Fact();
        var result = Project([first, second, third],
        [Event(first, "https://source.test/a#one"), Event(first, "https://source.test/a#two", 2),
         Event(second, "https://source.test/a"), Event(third, "https://source.test/a"),
         Event(first, "https://brand.test/x"), Event(second, "https://brand.test/x"),
         Event(first, "https://brand.test/y"), Event(third, "https://other.test/z"),
         Event(first, "https://example.com/placeholder"), Event(second, "https://www.google.com/search?q=brand"),
         new(third.CheckId, "javascript:alert(1)", 1)]);
        Assert.Equal(4, result.Metrics.DistinctUrls);
        Assert.Equal(7, result.Metrics.CitationEvents);
        Assert.Equal(1, result.Quality.ExcludedPlaceholderEvents);
        Assert.DoesNotContain(result.Urls, u => u.Url.Contains("example.com") || u.Url.Contains("google."));
        Assert.Equal(new[] { 1, 2, 3, 4 }, result.Urls.Select(u => u.Rank));
        Assert.Equal("https://source.test/a", result.Urls[0].Url);
        Assert.Equal(3, result.Urls[0].CitationEvents);
        Assert.Equal(3, result.Urls[0].AnswerCount);
        Assert.Equal(3d / 7, result.Urls[0].EventShare.Value);
        Assert.Equal(7, result.Urls[0].EventShare.Denominator);
        Assert.Equal("https://brand.test/x", result.Urls[1].Url);
        var singles = result.Urls.Skip(2).ToArray();
        Assert.All(singles, u => Assert.Equal(1, u.CitationEvents));
        Assert.Equal(singles.Select(u => u.UrlHash).Order(StringComparer.Ordinal), singles.Select(u => u.UrlHash));
        foreach (var row in result.Urls)
        {
            Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(row.Url))).ToLowerInvariant(), row.UrlHash);
            Assert.Equal(CitationDomainRules.HostFromUrl(row.Url, alreadyNormalized: true), row.Domain);
            Assert.Null(row.Title);
        }
        Assert.Equal(result.Metrics.CitationEvents, result.Urls.Sum(u => u.CitationEvents));
        var empty = Project([first], []);
        Assert.Equal(0, empty.Metrics.DistinctUrls);
        Assert.Empty(empty.Urls);
    }

    [Fact]
    public void NestedWwwIsRemovedExactlyOnceAcrossEventDomainAndOwnership()
    {
        var fact = Fact();
        var raw = "https://www.www.brand.test/a";
        var item = Event(fact, raw);
        Assert.Equal("https://www.brand.test/a", item.Url);
        var result = Project([fact], [item]);
        Assert.Equal("www.brand.test", result.Domains[0].Domain);
        Assert.Equal(CitationOwnership.NonOwned, result.Domains[0].Ownership);
        var attribution = CitationDomainRules.Attribute([new(raw, "www.www.brand.test", null, 1)], Config(), true);
        Assert.Equal(result.Domains[0].Domain, attribution[0].CanonicalHost);
        Assert.Equal(result.Domains[0].Ownership, attribution[0].Ownership);
    }

    [Fact]
    public void FinitePlaceholderExclusionDoesNotReduceAnswerDenominatorOrExcludeSimilarRealNames()
    {
        var fact = Fact();
        var result = Project([fact], [Event(fact, "https://example.com/a"), Event(fact, "https://www.example.com/a"),
            Event(fact, "https://sub.example.net/b"), Event(fact, "https://example.org/c"),
            Event(fact, "https://realexample.com/a"), Event(fact, "https://test.com/a"), Event(fact, "https://example.com.evil.test/a")]);
        Assert.Equal(3, result.Quality.ExcludedPlaceholderEvents);
        Assert.Equal(3, result.Metrics.CitationEvents);
        Assert.Equal(1, result.Metrics.EligibleAnswers);
        Assert.All(result.Domains, d => Assert.False(CitationDomainRules.IsPlaceholder(d.Domain)));
    }

    [Theory]
    [InlineData("www.BRAND.test.", false, CitationOwnership.Owned)]
    [InlineData("brand.test", false, CitationOwnership.Owned)]
    [InlineData("extra.test", false, CitationOwnership.Owned)]
    [InlineData("blog.brand.test", false, CitationOwnership.NonOwned)]
    [InlineData("blog.brand.test", true, CitationOwnership.Owned)]
    [InlineData("brand.test.evil.test", true, CitationOwnership.NonOwned)]
    [InlineData("evilbrand.test", true, CitationOwnership.NonOwned)]
    [InlineData("rival.test", true, CitationOwnership.NonOwned)]
    public void OwnershipUsesFrozenBrandBoundaryAndWwwException(string host, bool children, CitationOwnership expected)
        => Assert.Equal(expected, CitationDomainRules.Ownership(host, Config(children)).Ownership);

    [Fact]
    public void OwnershipBasisPriorityAndIdnUseOneCanonicalFunction()
    {
        var config = Config(true) with
        {
            Terms = [new(BrandId, "brand", "primary_domain", "brand.test"),
            new(BrandId, "brand", "additional_domain", "child.brand.test"),
            new(BrandId, "brand", "additional_domain", "例子.中国")]
        };
        var matched = CitationDomainRules.Ownership("deep.child.brand.test", config);
        Assert.Equal("child.brand.test", matched.Basis!.ConfiguredDomain);
        Assert.Equal("additional_domain", matched.Basis.DomainKind);
        Assert.Equal(CitationOwnership.Owned, CitationDomainRules.Ownership("www.xn--fsqu00a.xn--fiqs8s", config).Ownership);
    }

    [Fact]
    public void DomainWebsiteCategoryIsIndependentFromOwnershipAndPaths()
    {
        var config = Config() with { Terms = [new(BrandId, "brand", "primary_domain", "youtube.com"), new(BrandId, "brand", "additional_domain", "extra.test")] };
        Assert.Equal(CitationWebsiteCategory.Video, CitationDomainRules.Category("youtube.com", config).Category);
        Assert.Equal(CitationOwnership.Owned, CitationDomainRules.Ownership("youtube.com", config).Ownership);
        Assert.Equal(CitationWebsiteCategory.Unclassified, CitationDomainRules.Category("extra.test", config).Category);
        Assert.Equal(CitationWebsiteCategory.Unclassified, CitationDomainRules.Category("tenant.youtube.com", config).Category);
        Assert.Equal(CitationWebsiteCategory.Other, CitationDomainRules.Category("github.com", config).Category);
        Assert.Equal(CitationWebsiteCategory.Education, CitationDomainRules.Category("school.edu", config).Category);
        Assert.Equal(CitationWebsiteCategory.GovernmentNonprofit, CitationDomainRules.Category("agency.gov", config).Category);
        var fact = Fact();
        var result = Project([fact], [Event(fact, "https://source.test/news/story"), Event(fact, "https://source.test/video/clip")]);
        Assert.Equal(CitationWebsiteCategory.Unclassified, Assert.Single(result.Domains).WebsiteCategory);
    }

    [Fact]
    public void DetailKeepsOriginalIndicesConflictsAndUnsafeUrls()
    {
        AnalysisCitationView[] raw = [new("https://www.brand.test/a", "brand.test", "a", 1),
            new("https://brand.test/a", "brand.test", "b", 1), new("https://rival.test/a", "brand.test", null, 2),
            new("https://secret@brand.test/a", "brand.test", null, 3), new("javascript:bad", null, null, 4)];
        var before = raw.ToArray();
        var result = CitationDomainRules.Attribute(raw, Config(), true);
        Assert.Equal(before, raw);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, result.Select(i => i.RawCitationIndex));
        Assert.All(result.Take(2), i => Assert.Equal(CitationOwnership.Owned, i.Ownership));
        Assert.Equal("DECLARED_DOMAIN_MISMATCH", result[2].UnavailableReason);
        Assert.All(result.Skip(3), i => Assert.Equal(CitationOwnership.Unknown, i.Ownership));
        Assert.All(CitationDomainRules.Attribute(raw, Config(), false), i => Assert.Equal(CitationOwnership.Unknown, i.Ownership));
    }

    [Fact]
    public void ZeroUnknownAndNoAnswerRemainDistinct()
    {
        var empty = Project([], []);
        Assert.Null(empty.Metrics.OwnedEventShare.Value);
        Assert.Equal("ZERO_DENOMINATOR", empty.Metrics.OwnedEventShare.UnavailableReason);
        var fact = Fact();
        var noCitation = Project([fact], []);
        Assert.Equal(1, noCitation.Metrics.EligibleAnswers);
        Assert.Equal(0, noCitation.Metrics.CitationEvents);
        Assert.Null(noCitation.Metrics.OwnedEventShare.Value);
        var unknown = Project([fact], [Event(fact, "https://source.test/a")], Config() with { Terms = [] });
        Assert.Equal(1, unknown.Metrics.UnknownOwnershipEvents);
        Assert.Equal(0, unknown.Metrics.OwnedEventShare.Value);
        Assert.Equal(CitationOwnership.Unknown, CitationDomainRules.Ownership("brand.test", Config() with { IsComplete = false }).Ownership);
    }

    [Fact]
    public void TableFilterUsesLiteralSearchAndOnlyDeclaredEnums()
    {
        var filter = AnalyticsQueryStore.NormalizeCitationDomainFilter(" %_AbC ", "news_media", "non_owned");
        Assert.Equal("%_ABC", filter.Search);
        Assert.Equal(CitationWebsiteCategory.NewsMedia, filter.Category);
        Assert.Equal(CitationOwnership.NonOwned, filter.Ownership);
        foreach (var value in new[] { "NewsMedia", "1", "brand" })
            Assert.Equal("INVALID_FILTER", Assert.Throws<CollectorStoreException>(() =>
                AnalyticsQueryStore.NormalizeCitationDomainFilter(null, value, null)).Code);
        Assert.Throws<CollectorStoreException>(() => AnalyticsQueryStore.NormalizeCitationDomainFilter(new string('字', 201), null, null));
    }

    private static (CitationDomainProjection Projection, List<CitationDomainCountedEvent> Counted) ProjectObserved(
        IReadOnlyList<AnalyticsCheckFact> facts, IEnumerable<CitationDomainEvent> events)
    {
        var counted = new List<CitationDomainCountedEvent>();
        var accumulator = new CitationDomainAccumulator(facts, Config(), DetailedAnalyticsRules.UrlV2, counted.Add);
        foreach (var item in events.OrderBy(e => e.CheckId).ThenBy(e => e.Url, StringComparer.Ordinal)) accumulator.Add(item);
        return (accumulator.Complete(), counted);
    }

    private static readonly TimeZoneInfo Shanghai = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
    // Local window 2026-10-02 .. 2026-10-04 (Asia/Shanghai) = UTC 10-01T16:00 .. 10-04T16:00.
    private static readonly DateTimeOffset WindowFrom = DateTimeOffset.Parse("2026-10-01T16:00:00Z");
    private static readonly DateTimeOffset WindowTo = DateTimeOffset.Parse("2026-10-04T16:00:00Z");

    private static AnalyticsCheckFact At(string utc, string engine, Guid? prompt = null)
        => Fact() with { CapturedAt = DateTimeOffset.Parse(utc), Engine = engine, PromptId = prompt ?? Guid.NewGuid() };

    [Fact]
    public void DomainDetailTotalEqualsDomainRowAndEnginesAndTrendSumToIt()
    {
        var a = At("2026-10-02T15:30:00Z", "chatgpt");   // local 10-02 23:30
        var b = At("2026-10-02T16:30:00Z", "doubao");    // UTC 10-02, local 10-03 00:30
        var c = At("2026-10-01T16:00:00Z", "doubao");    // first local instant of the window
        var d = At("2026-10-04T15:59:59Z", "perplexity"); // last local day
        var pending = At("2026-10-03T01:00:00Z", "doubao") with { AnalysisStatus = "pending" };
        var facts = new[] { a, b, c, d, pending };
        var events = new List<CitationDomainEvent>
        {
            Event(a, "https://zhihu.com/q/1"), Event(a, "https://zhihu.com/q/1#dup", 2), Event(a, "https://zhihu.com/q/2"),
            Event(b, "https://www.zhihu.com/q/1"), Event(b, "https://zhuanlan.zhihu.com/p/9"),
            Event(c, "https://zhihu.com/q/3"), Event(d, "https://zhihu.com/q/1"),
            Event(pending, "https://zhihu.com/q/1"), Event(a, "https://example.com/zhihu"),
            Event(b, "https://www.google.com/search?q=zhihu")
        };
        var (projection, counted) = ProjectObserved(facts, events);
        var row = Assert.Single(projection.Domains, r => r.Domain == "zhihu.com");
        var own = new HashSet<(Guid, string)>
        {
            (a.CheckId, CitationDomainAccumulator.UrlHash("https://zhihu.com/q/1")),
            (d.CheckId, CitationDomainAccumulator.UrlHash("https://zhihu.com/q/1")),
            (pending.CheckId, CitationDomainAccumulator.UrlHash("https://zhihu.com/q/1")), // not counted: answer not eligible
            (b.CheckId, CitationDomainAccumulator.UrlHash("https://zhuanlan.zhihu.com/p/9")) // other domain key
        };
        var detail = CitationDomainProjector.Detail(null!, "zhihu.com", null, counted, own, WindowFrom, WindowTo, Shanghai);

        Assert.Equal(row.CitationEvents, detail.CitationEvents);
        Assert.Equal(5, detail.CitationEvents);
        Assert.Equal(2, detail.MyCitationEvents);
        Assert.Equal(detail.CitationEvents, detail.Engines.Sum(e => e.CitationEvents));
        Assert.Equal(detail.CitationEvents, detail.Trend.Sum(t => t.CitationEvents));
        Assert.Equal(detail.MyCitationEvents, detail.Engines.Sum(e => e.MyCitationEvents));
        Assert.Equal(detail.MyCitationEvents, detail.Trend.Sum(t => t.MyCitationEvents));
        Assert.Equal(new[] { ("chatgpt", 2L, 1L), ("doubao", 2L, 0L), ("perplexity", 1L, 1L) },
            detail.Engines.Select(e => (e.Engine, e.CitationEvents, e.MyCitationEvents)));
        Assert.Equal(new[] { new DateOnly(2026, 10, 2), new DateOnly(2026, 10, 3), new DateOnly(2026, 10, 4) },
            detail.Trend.Select(t => t.Date));
        // 10-02: a×2 + c×1; 10-03: b×1 (UTC 10-02 moves to local 10-03); 10-04: d×1.
        Assert.Equal(new long[] { 3, 1, 1 }, detail.Trend.Select(t => t.CitationEvents));
        Assert.Equal(new long[] { 1, 0, 1 }, detail.Trend.Select(t => t.MyCitationEvents));

        var doubao = CitationDomainProjector.Detail(null!, "zhihu.com", "doubao", counted, own, WindowFrom, WindowTo, Shanghai);
        Assert.Equal("doubao", doubao.Engine);
        Assert.Equal(detail.CitationEvents, doubao.CitationEvents);
        Assert.Equal(detail.Engines, doubao.Engines);
        Assert.Equal(new long[] { 1, 1, 0 }, doubao.Trend.Select(t => t.CitationEvents));
        Assert.Equal(doubao.Engines.Single(e => e.Engine == "doubao").CitationEvents, doubao.Trend.Sum(t => t.CitationEvents));

        var subdomain = CitationDomainProjector.Detail(null!, "zhuanlan.zhihu.com", null, counted, own, WindowFrom, WindowTo, Shanghai);
        Assert.Equal(projection.Domains.Single(r => r.Domain == "zhuanlan.zhihu.com").CitationEvents, subdomain.CitationEvents);
        Assert.Equal(1, subdomain.MyCitationEvents);
        // Every domain row of the board is reproduced exactly by the detail projection.
        foreach (var domainRow in projection.Domains)
            Assert.Equal(domainRow.CitationEvents, CitationDomainProjector.Detail(null!, domainRow.Domain, null, counted,
                own, WindowFrom, WindowTo, Shanghai).CitationEvents);
    }

    [Fact]
    public void DomainDetailWithoutEventsReturnsZeroTotalsEmptyEnginesAndZeroFilledDays()
    {
        var a = At("2026-10-02T15:30:00Z", "chatgpt");
        var (_, counted) = ProjectObserved([a], [Event(a, "https://other.test/1")]);
        var detail = CitationDomainProjector.Detail(null!, "zhihu.com", "doubao", counted, new HashSet<(Guid, string)>(),
            WindowFrom, WindowTo, Shanghai);
        Assert.Equal(0, detail.CitationEvents);
        Assert.Equal(0, detail.MyCitationEvents);
        Assert.Empty(detail.Engines);
        Assert.Equal(3, detail.Trend.Count);
        Assert.All(detail.Trend, t => { Assert.Equal(0, t.CitationEvents); Assert.Equal(0, t.MyCitationEvents); });
        // A UTC window ending exactly at midnight does not create an extra local day.
        var utc = CitationDomainProjector.Detail(null!, "zhihu.com", null, [], new HashSet<(Guid, string)>(),
            DateTimeOffset.Parse("2026-10-01T00:00:00Z"), DateTimeOffset.Parse("2026-10-08T00:00:00Z"), TimeZoneInfo.Utc);
        Assert.Equal(7, utc.Trend.Count);
        Assert.Equal(new DateOnly(2026, 10, 7), utc.Trend[^1].Date);
    }

    [Fact]
    public void EngineUrlRankingRanksTheFullPopulationForOneEngineWithItsOwnDenominator()
    {
        var c1 = At("2026-10-02T01:00:00Z", "chatgpt"); var c2 = At("2026-10-02T02:00:00Z", "chatgpt");
        var d1 = At("2026-10-02T03:00:00Z", "doubao"); var d2 = At("2026-10-02T04:00:00Z", "doubao");
        var ranking = new CitationUrlEngineRanking("doubao");
        var counted = new List<CitationDomainCountedEvent>();
        var accumulator = new CitationDomainAccumulator([c1, c2, d1, d2], Config(), DetailedAnalyticsRules.UrlV2,
            item => { ranking.Add(item); counted.Add(item); });
        foreach (var item in new[]
                 {
                     Event(c1, "https://a.test/top"), Event(c2, "https://a.test/top"), Event(c1, "https://b.test/x"),
                     Event(d1, "https://b.test/x"), Event(d2, "https://b.test/x"), Event(d2, "https://b.test/x#again", 2),
                     Event(d1, "https://a.test/top"), Event(d2, "https://example.com/no")
                 }.OrderBy(e => e.CheckId).ThenBy(e => e.Url, StringComparer.Ordinal)) accumulator.Add(item);
        var all = accumulator.Complete();
        Assert.Equal("https://a.test/top", all.Urls[0].Url);
        var rows = ranking.Complete();
        Assert.Equal(new[] { "https://b.test/x", "https://a.test/top" }, rows.Select(r => r.Url));
        Assert.Equal(new[] { 1, 2 }, rows.Select(r => r.Rank));
        Assert.Equal(new long[] { 2, 1 }, rows.Select(r => r.CitationEvents));
        Assert.Equal(rows.Select(r => r.CitationEvents), rows.Select(r => r.AnswerCount));
        Assert.All(rows, r => Assert.Equal(3, r.EventShare.Denominator));
        Assert.Equal(counted.Count(c => c.Fact.Engine == "doubao"), rows.Sum(r => r.CitationEvents));
        Assert.Equal("b.test", rows[0].Domain);
        Assert.Equal(CitationDomainAccumulator.UrlHash("https://b.test/x"), rows[0].UrlHash);
        Assert.Empty(new CitationUrlEngineRanking("gemini").Complete());
    }

    [Fact]
    public void UrlStatsMarkMineFromActiveLinksCountGatedMineAndDistinctPrompts()
    {
        var prompt = Guid.NewGuid();
        var a = At("2026-10-02T01:00:00Z", "chatgpt", prompt); var b = At("2026-10-02T02:00:00Z", "doubao", prompt);
        var c = At("2026-10-02T03:00:00Z", "doubao");
        const string url = "https://brand.test/guide";
        var hash = CitationDomainAccumulator.UrlHash(url);
        var row = new CitationUrlSummaryRow(4, url, hash, "brand.test", "指南", 3, CitationDomainRatio.Of(3, 9), 3, false, 0, 0, null);
        var own = new HashSet<(Guid, string)> { (a.CheckId, hash), (c.CheckId, hash), (Guid.NewGuid(), hash) };
        var active = new HashSet<string>(StringComparer.Ordinal) { hash };

        var all = CitationDomainProjector.WithUrlStats(row, [a, b, c, c], own, active, null);
        Assert.True(all.IsMine);
        Assert.Equal(2, all.MyCitationEvents);
        Assert.Equal(2, all.PromptCount);
        Assert.Equal(row with { IsMine = true, MyCitationEvents = 2, PromptCount = 2 }, all);

        var doubao = CitationDomainProjector.WithUrlStats(row, [a, b, c], own, active, "doubao");
        Assert.Equal(1, doubao.MyCitationEvents);
        Assert.Equal(2, doubao.PromptCount);

        // An active link without any gated citation in the window: still "my article", zero my citations.
        var noGated = CitationDomainProjector.WithUrlStats(row, [b], new HashSet<(Guid, string)>(), active, null);
        Assert.True(noGated.IsMine);
        Assert.Equal(0, noGated.MyCitationEvents);
        Assert.Equal(1, noGated.PromptCount);
        // Gated history of a link that is no longer active counts as my citations but not as "my article".
        var disabled = CitationDomainProjector.WithUrlStats(row, [a], own, new HashSet<string>(), null);
        Assert.False(disabled.IsMine);
        Assert.Equal(1, disabled.MyCitationEvents);
    }

    [Fact]
    public void UrlSearchIsLiteralCaseInsensitiveOverTitleOrUrl()
    {
        var row = new CitationUrlSummaryRow(1, "https://brand.test/Guide%_1", "h", "brand.test", null, 1, CitationDomainRatio.Of(1, 1), 1, false, 0, 0, null);
        Assert.True(CitationDomainProjector.MatchesUrlSearch(row, null, "GUIDE%_"));
        Assert.True(CitationDomainProjector.MatchesUrlSearch(row, "管道摄像机选购", "摄像机"));
        Assert.False(CitationDomainProjector.MatchesUrlSearch(row, "管道摄像机选购", "GUIDE_1"));
        Assert.False(CitationDomainProjector.MatchesUrlSearch(row, null, "manual"));
    }

    [Fact]
    public void SharedEventGateMatchesAccumulatorDecisionsIncludingPerAnswerDedupAndPlaceholders()
    {
        var first = Fact(); var second = Fact(); var pending = Fact() with { AnalysisStatus = "pending" };
        var events = new[]
        {
            Event(first, "https://source.test/a"), Event(first, "https://source.test/a#x", 2), Event(first, "https://example.org/p"),
            Event(first, "https://www.google.com/search?q=x"), new CitationDomainEvent(first.CheckId, "ftp://source.test/f", 1),
            Event(second, "https://source.test/a"), Event(pending, "https://source.test/a")
        }.OrderBy(e => e.CheckId).ThenBy(e => e.Url, StringComparer.Ordinal).ToArray();
        var gate = new CitationDomainEventGate(CitationDomainEventGate.Eligible([first, second, pending], Config()), Config());
        var placeholders = 0;
        var admitted = events.Select(e => { var r = gate.Admit(e, out var p); if (p) placeholders++; return r; }).Where(r => r is not null).ToArray();
        var projection = Project([first, second, pending], events);
        Assert.Equal(projection.Metrics.CitationEvents, admitted.Length);
        Assert.Equal(projection.Quality.ExcludedPlaceholderEvents, placeholders);
        Assert.All(admitted, r => Assert.Equal("source.test", r!.Host));
        Assert.DoesNotContain(admitted, r => r!.Event.CheckId == pending.CheckId);
    }

    [Fact]
    public void DomainRankIsContiguousWithinTheDomainStableUnderLaterFiltersAndRecomputedPerEngine()
    {
        var c1 = At("2026-10-02T01:00:00Z", "chatgpt"); var c2 = At("2026-10-02T02:00:00Z", "chatgpt");
        var d1 = At("2026-10-02T03:00:00Z", "doubao");
        var ranking = new CitationUrlEngineRanking("doubao");
        var accumulator = new CitationDomainAccumulator([c1, c2, d1], Config(), DetailedAnalyticsRules.UrlV2, ranking.Add);
        foreach (var item in new[]
                 {
                     Event(c1, "https://top.test/1"), Event(c2, "https://top.test/1"), Event(c1, "https://top.test/2"),
                     Event(c2, "https://top.test/3"), Event(c1, "https://brand.test/a"), Event(c2, "https://brand.test/a"),
                     Event(c1, "https://brand.test/b"), Event(d1, "https://brand.test/c"), Event(d1, "https://top.test/1")
                 }.OrderBy(e => e.CheckId).ThenBy(e => e.Url, StringComparer.Ordinal)) accumulator.Add(item);
        var all = accumulator.Complete();
        Assert.All(all.Urls, u => Assert.Null(u.DomainRank));

        var brand = CitationDomainProjector.WithinDomain(all.Urls, "brand.test");
        Assert.Equal(new long?[] { 1, 2, 3 }, brand.Select(u => u.DomainRank));
        Assert.Equal("https://brand.test/a", brand[0].Url);
        // The global rank is untouched and still has gaps across domains.
        Assert.Equal(brand.Select(u => all.Urls.Single(g => g.Url == u.Url).Rank), brand.Select(u => u.Rank));
        Assert.NotEqual(brand.Select(u => (long?)u.Rank), brand.Select(u => u.DomainRank));
        Assert.Equal(all.Urls.Where(u => u.Domain == "brand.test").Select(u => u with { DomainRank = null }),
            brand.Select(u => u with { DomainRank = null }));
        // Later q / mine filters only drop rows; the surviving rows keep their domain ranks.
        var searched = brand.Where(u => CitationDomainProjector.MatchesUrlSearch(u, null, "/C")).ToArray();
        var c = brand.Single(u => u.Url == "https://brand.test/c");
        Assert.True(c.DomainRank > 1); // b/c tie on events and order by hash; either way it is not renumbered to 1
        Assert.Equal(c, Assert.Single(searched));

        var doubao = CitationDomainProjector.WithinDomain(ranking.Complete(), "brand.test");
        Assert.Equal("https://brand.test/c", Assert.Single(doubao).Url);
        Assert.Equal(1, doubao[0].DomainRank);
        Assert.Empty(CitationDomainProjector.WithinDomain(all.Urls, "missing.test"));
    }

    private static readonly Guid GroupA = Guid.Parse("00000000-0000-0000-0000-0000000000aa");
    private static readonly Guid GroupB = Guid.Parse("00000000-0000-0000-0000-0000000000bb");

    [Fact]
    public void UrlPromptGroupsSumToTheUrlRowAndFollowTheEngineRanking()
    {
        Guid p1 = Guid.NewGuid(), p2 = Guid.NewGuid(), p3 = Guid.NewGuid(), p4 = Guid.NewGuid(), deleted = Guid.NewGuid();
        AnalyticsCheckFact Ask(string utc, string engine, Guid prompt, string text) => At(utc, engine, prompt) with { PromptSnapshot = text };
        var a1 = Ask("2026-10-02T01:00:00Z", "doubao", p1, "旧问题一");
        var a2 = Ask("2026-10-03T01:00:00Z", "doubao", p1, "问题一");
        var a3 = Ask("2026-10-02T02:00:00Z", "chatgpt", p1, "问题一（英文）");
        var b1 = Ask("2026-10-02T03:00:00Z", "doubao", p2, "问题二");
        var c1 = Ask("2026-10-02T04:00:00Z", "chatgpt", p3, "问题三");
        var c2 = Ask("2026-10-02T05:00:00Z", "chatgpt", p3, "问题三");
        var d1 = Ask("2026-10-02T06:00:00Z", "doubao", p4, "问题四");
        var x1 = Ask("2026-10-02T07:00:00Z", "doubao", deleted, "已删除问题");
        var pending = Ask("2026-10-02T08:00:00Z", "doubao", p2, "问题二") with { AnalysisStatus = "pending" };
        const string target = "https://zhihu.com/q/1";
        var facts = new[] { a1, a2, a3, b1, c1, c2, d1, x1, pending };
        var events = new List<CitationDomainEvent>
        {
            Event(a1, target), Event(a1, target + "#dup", 2), Event(a2, "https://www.zhihu.com/q/1"), Event(a3, target),
            Event(b1, target), Event(c1, target), Event(c2, target), Event(d1, target), Event(x1, target),
            Event(pending, target), Event(a1, "https://zhihu.com/q/other"), Event(b1, "https://other.test/x")
        };
        var ranking = new CitationUrlEngineRanking("doubao");
        var counted = new List<CitationDomainCountedEvent>();
        var accumulator = new CitationDomainAccumulator(facts, Config(), DetailedAnalyticsRules.UrlV2,
            item => { ranking.Add(item); counted.Add(item); });
        foreach (var item in events.OrderBy(e => e.CheckId).ThenBy(e => e.Url, StringComparer.Ordinal)) accumulator.Add(item);
        var row = Assert.Single(accumulator.Complete().Urls, u => u.Url == target);
        var groups = new Dictionary<Guid, PromptGroupAssignment>
        {
            [p1] = new(GroupB, "B 组", DateTimeOffset.UnixEpoch), [p2] = new(GroupA, "A 组", DateTimeOffset.UnixEpoch),
            [p3] = new(GroupA, "A 组", DateTimeOffset.UnixEpoch), [p4] = new(null, null, null)
        };

        var all = CitationDomainProjector.UrlPrompts(null!, row.UrlHash, row.Url, "标题", null, counted, groups);
        Assert.Equal(row.CitationEvents, all.CitationEvents);
        Assert.Equal(8, all.CitationEvents); // a1 deduplicated, pending answer excluded, other URLs ignored
        Assert.Equal(all.CitationEvents, all.Groups.Sum(g => g.CitationEvents));
        Assert.All(all.Groups, g => Assert.Equal(g.CitationEvents, g.Prompts.Sum(p => p.CitationEvents)));
        Assert.Equal(5, all.PromptCount);
        Assert.Equal(3, all.GroupCount);
        Assert.Equal(all.Groups.Count, all.GroupCount);
        // A 组 (p2 ×1 + p3 ×2) and B 组 (p1 ×3) tie on 3 events → name ascending; ungrouped (p4, deleted) is always last.
        Assert.Equal(new (Guid?, string?, long)[] { (GroupA, "A 组", 3), (GroupB, "B 组", 3), (null, null, 2) },
            all.Groups.Select(g => (g.GroupId, g.Name, g.CitationEvents)));
        Assert.Equal(new[] { p3, p2 }, all.Groups[0].Prompts.Select(p => p.PromptId));
        Assert.Equal(new long[] { 2, 1 }, all.Groups[0].Prompts.Select(p => p.CitationEvents));
        Assert.Equal("问题一", Assert.Single(all.Groups[1].Prompts).Prompt); // latest observed snapshot wins
        Assert.Equal(new[] { "问题四", "已删除问题" }.Order(StringComparer.Ordinal), all.Groups[2].Prompts.Select(p => p.Prompt));
        Assert.Equal(("标题", row.Url, row.UrlHash, (string?)null), (all.Title, all.Url, all.UrlHash, all.Engine));

        var doubaoRow = Assert.Single(ranking.Complete(), u => u.Url == target);
        var doubao = CitationDomainProjector.UrlPrompts(null!, row.UrlHash, row.Url, null, "doubao", counted, groups);
        Assert.Equal("doubao", doubao.Engine);
        Assert.Equal(doubaoRow.CitationEvents, doubao.CitationEvents);
        Assert.Equal(5, doubao.CitationEvents);
        Assert.Equal(doubao.CitationEvents, doubao.Groups.Sum(g => g.CitationEvents));
        Assert.Equal(new (Guid?, long)[] { (GroupB, 2), (GroupA, 1), (null, 2) }, doubao.Groups.Select(g => (g.GroupId, g.CitationEvents)));
        Assert.Equal("问题一", doubao.Groups[0].Prompts[0].Prompt);

        var chatgpt = CitationDomainProjector.UrlPrompts(null!, row.UrlHash, row.Url, null, "chatgpt", counted, groups);
        Assert.Equal(all.CitationEvents, chatgpt.CitationEvents + doubao.CitationEvents);
        Assert.Equal("问题一（英文）", chatgpt.Groups.Single(g => g.GroupId == GroupB).Prompts[0].Prompt);

        var none = CitationDomainProjector.UrlPrompts(null!, row.UrlHash, row.Url, null, "gemini", counted, groups);
        Assert.Equal((0L, 0, 0), (none.CitationEvents, none.GroupCount, none.PromptCount));
        Assert.Empty(none.Groups);
    }
}
