using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using MySqlConnector;
using Xunit.Abstractions;

namespace Jogeoai.Tests;

/// <summary>必须显式连接一次性隔离库；绝不回退业务库或缺设置时假通过。</summary>
public sealed class CitationDomainSqlTests(ITestOutputHelper output)
{
    [Fact]
    public async Task BoundedProjectionCostRecordsActualColdAndWarmQueriesWithoutCapacityClaims()
    {
        const int checks = 200, urlsPerCheck = 5, domainCount = 100;
        var f = await Fixture.CreateAsync();
        var ids = new List<Guid>(checks);
        for (var i = 0; i < checks; i++)
            ids.Add(await f.CheckAsync(i % 2 == 0 ? "cloro" : "domestic_collector",
                urls: Enumerable.Range(0, urlsPerCheck)
                    .Select(j => $"https://source-{i % domainCount:D3}.test/path-{j}").ToArray()));
        await f.CompleteAsync(ids.ToArray());
        f.Store = new AnalyticsQueryStore(f.Database, f.Analyzer);

        async Task<T> Measure<T>(string label, Func<Task<T>> query)
        {
            var allocation = GC.GetTotalAllocatedBytes(precise: true);
            var stopwatch = Stopwatch.StartNew();
            var result = await query();
            stopwatch.Stop();
            output.WriteLine($"{label}: {stopwatch.Elapsed.TotalMilliseconds:F2} ms; process managed allocations {GC.GetTotalAllocatedBytes(precise: true) - allocation} bytes.");
            return result;
        }

        var summary = await Measure("Cold summary", () => f.SummaryAsync());
        Assert.Equal(checks, summary.Metrics.EligibleAnswers);
        Assert.Equal(checks * urlsPerCheck, summary.Metrics.CitationEvents);
        Assert.Equal(domainCount, summary.Metrics.DistinctDomains);
        var token = summary.Meta.SnapshotToken;
        for (var i = 0; i < 3; i++)
        {
            var warm = await Measure($"Warm summary {i + 1}", () => f.SummaryAsync(token));
            Assert.Equal(summary.Metrics, warm.Metrics);
            var domains = await Measure($"Warm domains {i + 1}", () => f.Store.CitationDomainSummaryDomainsAsync(
                f.Tenant, f.Site, token, null, null, null, null, 20));
            Assert.Equal(20, domains.Items.Count);
            Assert.Equal(domainCount, domains.FilteredDomainCount);
            var evidence = await Measure($"Warm evidence {i + 1}", () => f.Store.CitationDomainSummaryEvidenceAsync(
                f.Tenant, f.Site, token, "source-000.test", null, 20));
            Assert.Equal(10, evidence.Items.Count);
        }
        output.WriteLine($"Bounded fixture: {checks} Checks, {checks * urlsPerCheck} events, {domainCount} domains, one reader; {RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}, CPUs={Environment.ProcessorCount}.");
        output.WriteLine("Observations only, not a production P95, peak-memory or concurrency capacity result. MySQL buffers may already be warm; cold means a new query-store instance. Fixture data retained pending cleanup approval.");
    }

    [Fact]
    public void UrlTitleSqlIsParameterizedAndScopedToTenantSiteWindowReleaseAndWatermarks()
    {
        var sql = AnalyticsQueryStore.CitationUrlTitleSql(3);
        foreach (var fence in new[]
        {
            "c.tenant_id = UUID_TO_BIN(@tenant)", "c.site_id = UUID_TO_BIN(@site)",
            "c.captured_at >= @from", "c.captured_at < @to", "a.release_id = UUID_TO_BIN(@release)",
            "e.release_id = a.release_id", "ar.id <= @arrival", "a.completed_event_id <= @analysis",
            "e.url_version = @urlVersion", "e.url_hash IN (@h0, @h1, @h2)", "e.title IS NOT NULL"
        })
            Assert.Contains(fence, sql);
        Assert.DoesNotContain("@h3", sql);
        Assert.DoesNotContain("'http", sql);
    }

    [Fact]
    public async Task RanksPreviousWindowRanksAndUrlBoardShareOneSnapshotAndPopulation()
    {
        var f = await Fixture.CreateAsync();
        // Previous equal-length window: other.test ranks first, source.test second.
        var previousCaptured = f.Now.AddDays(-15);
        var previousOne = await f.CheckAsync(urls: ["https://source.test/p", "https://other.test/1"], captured: previousCaptured);
        var previousTwo = await f.CheckAsync("domestic_collector", urls: ["https://other.test/2"], captured: previousCaptured);
        var first = await f.CheckAsync(urls: ["https://brand.test/a", "https://source.test/u", "https://www.brand.test/a"]);
        var second = await f.CheckAsync("domestic_collector", urls: ["https://source.test/u", "https://source.test/v", "https://example.com/x"]);
        await f.CompleteAsync(previousOne, previousTwo, first, second);

        var summary = await f.SummaryAsync();
        var token = summary.Meta.SnapshotToken;
        Assert.Equal(summary.Meta.Filters.From, summary.Meta.PreviousTo);
        Assert.Equal(summary.Meta.Filters.From - (summary.Meta.Filters.To - summary.Meta.Filters.From), summary.Meta.PreviousFrom);
        Assert.Equal(4, summary.Metrics.CitationEvents);
        Assert.Equal(3, summary.Metrics.DistinctUrls);

        var domains = await f.Store.CitationDomainSummaryDomainsAsync(f.Tenant, f.Site, token, null, null, null, null, 5);
        Assert.Equal(new[] { "source.test", "brand.test" }, domains.Items.Select(d => d.Domain));
        Assert.Equal(new[] { 1, 2 }, domains.Items.Select(d => d.Rank));
        Assert.Equal(new int?[] { 2, null }, domains.Items.Select(d => d.PreviousRank));
        var searched = Assert.Single((await f.Store.CitationDomainSummaryDomainsAsync(f.Tenant, f.Site, token, "brand", null, null, null, 5)).Items);
        Assert.Equal(2, searched.Rank); // global rank is not renumbered after filtering
        var evidence = await f.Store.CitationDomainSummaryEvidenceAsync(f.Tenant, f.Site, token, "source.test", null, 5);
        Assert.Equal(2, evidence.DomainTotals!.PreviousRank);

        var all = await f.Store.CitationDomainSummaryUrlsAsync(f.Tenant, f.Site, token, null, null, 5);
        Assert.Equal(token, all.Meta.SnapshotToken);
        Assert.Equal(summary.Metrics.DistinctUrls, all.FilteredUrlCount);
        Assert.Equal(new[] { 1, 2, 3 }, all.Items.Select(u => u.Rank));
        Assert.Equal("https://source.test/u", all.Items[0].Url);
        Assert.Equal(2, all.Items[0].CitationEvents);
        Assert.Equal(2, all.Items[0].AnswerCount);
        Assert.Equal(.5, all.Items[0].EventShare.Value);
        Assert.All(all.Items, u => Assert.Equal(Hash(u.Url), u.UrlHash));
        Assert.All(all.Items, u => Assert.Equal("Stored title", u.Title));
        Assert.DoesNotContain(all.Items, u => u.Url.Contains("example.com") || u.Url.Contains("other.test"));

        var firstPage = await f.Store.CitationDomainSummaryUrlsAsync(f.Tenant, f.Site, token, null, null, 1);
        Assert.Equal(all.Items[0], Assert.Single(firstPage.Items));
        Assert.NotNull(firstPage.NextCursor);
        var secondPage = await f.Store.CitationDomainSummaryUrlsAsync(f.Tenant, f.Site, token, null, firstPage.NextCursor, 1);
        Assert.Equal(all.Items[1], Assert.Single(secondPage.Items));
        await ErrorAsync(() => f.Store.CitationDomainSummaryUrlsAsync(f.Tenant, f.Site, token, "source.test", firstPage.NextCursor, 1), "INVALID_CURSOR");
        var domainCursor = (await f.Store.CitationDomainSummaryDomainsAsync(f.Tenant, f.Site, token, null, null, null, null, 1)).NextCursor;
        await ErrorAsync(() => f.Store.CitationDomainSummaryUrlsAsync(f.Tenant, f.Site, token, null, domainCursor, 1), "INVALID_CURSOR");

        var brand = await f.Store.CitationDomainSummaryUrlsAsync(f.Tenant, f.Site, token, "brand.test", null, 5);
        var brandUrl = Assert.Single(brand.Items);
        Assert.Equal("https://brand.test/a", brandUrl.Url);
        Assert.Equal(1, brand.FilteredUrlCount);
        Assert.Equal(all.Items.Single(u => u.Url == brandUrl.Url).Rank, brandUrl.Rank);
        Assert.Empty((await f.Store.CitationDomainSummaryUrlsAsync(f.Tenant, f.Site, token, "missing.test", null, 5)).Items);

        var other = await Fixture.CreateAsync();
        await ErrorAsync(() => f.Store.CitationDomainSummaryUrlsAsync(other.Tenant, f.Site, token, null, null, 5), "RESOURCE_NOT_FOUND");
        await f.ExecAsync("UPDATE analytics_snapshots SET expires_at=UTC_TIMESTAMP(6)-INTERVAL 1 SECOND WHERE token_hash=@hash", ("@hash", Hash(token)));
        f.Store = new AnalyticsQueryStore(f.Database, f.Analyzer);
        await ErrorAsync(() => f.Store.CitationDomainSummaryUrlsAsync(f.Tenant, f.Site, token, null, null, 5), "SNAPSHOT_EXPIRED");
        output.WriteLine("Executed SQL ranking checks: global/previous domain ranks, distinct URLs, URL board paging, domain filter, titles, cursor and expiry fences.");
    }

    [Fact]
    public async Task LegacyV1AtomicCompletionIncludesZeroAndEventAnswersWithOrWithoutNewProof()
    {
        var f = await Fixture.CreateAsync();
        var provenZero = await f.CheckAsync();
        await f.ExecAsync("""
            UPDATE analysis_releases r JOIN site_analysis_state s ON s.published_release_id=r.id
            SET r.detail_version='detail-v1', r.url_normalization_version='url-v1'
            WHERE s.site_id=UUID_TO_BIN(@site) AND s.tenant_id=UUID_TO_BIN(@tenant);
            """, ("@site", f.Site), ("@tenant", f.Tenant));
        var provenEvent = await f.CheckAsync(urls: ["https://brand.test/proven"]);
        await f.CompleteAsync(provenZero, provenEvent);
        var legacyZero = await f.CheckAsync();
        var legacyEvent = await f.CheckAsync(urls: ["https://brand.test/historical"]);
        // Emulate the previous v1 atomic contract without inserting/backfilling
        // the new marker. These URLs are deliberately already canonical.
        await f.CoreOnlyAsync(legacyZero, legacyV1Facts: true);
        await f.CoreOnlyAsync(legacyEvent, legacyV1Facts: true);
        var inconsistent = await f.CheckAsync(urls: ["https://www.www.brand.test/nested"]);
        await f.CoreOnlyAsync(inconsistent, legacyV1Facts: true);
        // Reproduce the historical writer's first-pass hash / second-pass URL
        // mismatch only on this newly created fixture row, never on real history.
        await f.ExecAsync("""
            UPDATE citation_events SET url_hash=LOWER(SHA2('https://www.brand.test/nested',256)),
                normalized_url='https://brand.test/nested', domain='brand.test'
            WHERE check_id=UUID_TO_BIN(@check);
            """, ("@check", inconsistent));
        var pending = await f.CheckAsync(urls: ["https://brand.test/pending"]);
        var empty = await f.CheckAsync(answer: "", urls: ["https://ignored.test/empty"]);
        await f.CoreOnlyAsync(empty, legacyV1Facts: true);

        var summary = await f.SummaryAsync();
        Assert.Equal(4, summary.Metrics.EligibleAnswers);
        Assert.Equal(2, summary.Metrics.CitationEvents);
        Assert.Equal(2, summary.Quality.AnalysisPending);
        Assert.Equal(1, summary.Quality.NoAnswer);
        Assert.Contains(summary.Quality.Limitations, text => text.StartsWith("1 条历史 detail-v1", StringComparison.Ordinal));
        Assert.Equal(2, await f.CountAsync("check_detailed_completions"));
        var page = await f.Store.CitationDomainSummaryDomainsAsync(f.Tenant, f.Site,
            summary.Meta.SnapshotToken, null, null, null, null, 20);
        var row = Assert.Single(page.Items);
        Assert.Equal(2, row.AnswerCount);
        Assert.Equal(.5, row.AnswerCoverage.Value);
        foreach (var check in new[] { provenZero, provenEvent, legacyZero, legacyEvent })
        {
            var detail = (await f.Store.MonitoringAnswerDetailAsync(f.Tenant, f.Site, check,
                summary.Meta.SnapshotToken, null, null, "domain-summary-v1"))!;
            Assert.Equal("available", detail.CitationAttribution!.Availability);
        }
        var pendingDetail = (await f.Store.MonitoringAnswerDetailAsync(f.Tenant, f.Site, pending,
            summary.Meta.SnapshotToken, null, null, "domain-summary-v1"))!;
        Assert.Equal("unavailable", pendingDetail.CitationAttribution!.Availability);
        var inconsistentDetail = (await f.Store.MonitoringAnswerDetailAsync(f.Tenant, f.Site, inconsistent,
            summary.Meta.SnapshotToken, null, null, "domain-summary-v1"))!;
        Assert.Equal("unavailable", inconsistentDetail.CitationAttribution!.Availability);
        Assert.Equal("DETAILED_FACT_INTEGRITY_UNPROVEN", inconsistentDetail.CitationAttribution.UnavailableReason);
        Assert.Equal("https://www.www.brand.test/nested", Assert.Single(inconsistentDetail.Citations).Url);
        Assert.Equal(CitationOwnership.Unknown, Assert.Single(inconsistentDetail.CitationAttribution.Items).Ownership);
        Assert.Equal(1, await f.ScalarLongAsync("SELECT COUNT(*) FROM citation_events WHERE check_id=UUID_TO_BIN(@check) AND normalized_url='https://brand.test/nested'", ("@check", inconsistent)));
        Assert.Equal(2, await f.CountAsync("check_detailed_completions"));
        output.WriteLine("Executed legacy v1 SQL compatibility: zero/event answers included with and without new proof; pending/empty and historical nested-www hash/URL inconsistency excluded; raw details remain readable and reads do not backfill/repair facts.");
    }

    [Fact]
    public async Task ProvenPopulationPreservesZeroCitationAnswersAndAllSourcesFiltersEvidenceAndRawDetail()
    {
        var f = await Fixture.CreateAsync();
        var first = await f.CheckAsync(urls: ["https://www.brand.test/a", "https://brand.test/a", "https://brand.test/b",
            "https://reddit.com/thread", "https://example.com/placeholder", "https://www.google.com/search?q=brand"]);
        var domestic = await f.CheckAsync("domestic_collector", urls: ["https://brand.test/a", "https://source.test/u", "https://source.test/v"]);
        var zero = await f.CheckAsync();
        var empty = await f.CheckAsync(answer: "", urls: ["https://ignored.test/empty"]);
        await f.CompleteAsync(first, domestic, zero, empty);
        await f.CheckAsync(answer: null, status: "failed");
        var legacy = await f.CheckAsync(urls: ["https://unproven.test/a"]);
        await f.CoreOnlyAsync(legacy);
        await f.CheckAsync(urls: ["https://pending.test/a"]);

        var summary = await f.SummaryAsync();
        var token = summary.Meta.SnapshotToken;
        Assert.Equal(3, summary.Metrics.EligibleAnswers);
        Assert.Equal(6, summary.Metrics.CitationEvents);
        Assert.Equal(3, summary.Metrics.DistinctDomains);
        Assert.Equal(3, summary.Metrics.OwnedCitationEvents);
        Assert.Equal(.5, summary.Metrics.OwnedEventShare.Value);
        Assert.Equal(7, summary.Quality.CandidateAnswers);
        Assert.Equal(1, summary.Quality.CollectionNotSucceeded);
        Assert.Equal(1, summary.Quality.NoAnswer);
        Assert.Equal(2, summary.Quality.AnalysisPending);
        Assert.Equal(1, summary.Quality.ExcludedPlaceholderEvents);
        Assert.Equal(11, summary.CategoryDistribution.Count);
        Assert.Equal(6, summary.CategoryDistribution.Sum(c => c.CitationEvents));
        Assert.Equal(new[] { "cloro", "domestic_collector" }, summary.Meta.Filters.Providers);
        Assert.Equal(4, await f.CountAsync("check_detailed_completions"));
        Assert.Equal(1, await f.ScalarLongAsync("SELECT COUNT(*) FROM check_detailed_completions WHERE check_id=UUID_TO_BIN(@id)", ("@id", zero)));

        var firstPage = await f.Store.CitationDomainSummaryDomainsAsync(f.Tenant, f.Site, token, null, null, null, null, 1);
        Assert.Equal("brand.test", firstPage.Items[0].Domain);
        Assert.Equal(2, firstPage.Items[0].AnswerCount);
        Assert.Equal(2d / 3, firstPage.Items[0].AnswerCoverage.Value);
        Assert.Equal(3, firstPage.FilteredDomainCount);
        Assert.NotNull(firstPage.NextCursor);
        var search = await f.Store.CitationDomainSummaryDomainsAsync(f.Tenant, f.Site, token, " SOURCE ", null, null, null, 1);
        Assert.Equal("source.test", Assert.Single(search.Items).Domain);
        Assert.Equal(1, search.FilteredDomainCount);
        Assert.Equal(6, search.Items[0].EventShare.Denominator);
        Assert.Empty((await f.Store.CitationDomainSummaryDomainsAsync(f.Tenant, f.Site, token, "%_", null, null, null, 1)).Items);
        Assert.Equal("reddit.com", Assert.Single((await f.Store.CitationDomainSummaryDomainsAsync(f.Tenant, f.Site, token, null, "community_forum", null, null, 1)).Items).Domain);
        var nonOwned = await f.Store.CitationDomainSummaryDomainsAsync(f.Tenant, f.Site, token, null, null, "non_owned", null, 1);
        Assert.Equal(2, nonOwned.FilteredDomainCount);
        await ErrorAsync(() => f.Store.CitationDomainSummaryDomainsAsync(f.Tenant, f.Site, token, "source", null, null, firstPage.NextCursor, 1), "INVALID_CURSOR");

        var evidence = new List<CitationDomainEvidenceItem>();
        string? cursor = null;
        do
        {
            var page = await f.Store.CitationDomainSummaryEvidenceAsync(f.Tenant, f.Site, token, "brand.test", cursor, 1);
            Assert.Equal(token, page.Meta.SnapshotToken);
            Assert.Equal(3, page.DomainTotals!.CitationEvents);
            evidence.AddRange(page.Items);
            cursor = page.NextCursor;
            if (cursor is not null)
                await ErrorAsync(() => f.Store.CitationDomainSummaryEvidenceAsync(f.Tenant, f.Site, token, "source.test", cursor, 1), "INVALID_CURSOR");
        } while (cursor is not null);
        Assert.Equal(3, evidence.Count);
        Assert.Equal(2, evidence.Select(e => e.CheckId).Distinct().Count());
        Assert.Equal(3, evidence.Select(e => (e.CheckId, e.NormalizedUrl)).Distinct().Count());
        var detail = (await f.Store.MonitoringAnswerDetailAsync(f.Tenant, f.Site, first, token, null, null, "domain-summary-v1"))!;
        var original = (await f.Store.MonitoringAnswerDetailAsync(f.Tenant, f.Site, first, token, null))!;
        Assert.Equal(original.Citations, detail.Citations);
        Assert.Equal(original.Answer, detail.Answer);
        Assert.Equal(6, detail.Citations.Count);
        Assert.Equal("available", detail.CitationAttribution!.Availability);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, detail.CitationAttribution.Items.Select(i => i.RawCitationIndex));
        Assert.All(detail.CitationAttribution.Items.Take(3), i => Assert.Equal(CitationOwnership.Owned, i.Ownership));
        var old = (await f.Store.MonitoringAnswerDetailAsync(f.Tenant, f.Site, legacy, token, null, null, "domain-summary-v1"))!;
        Assert.Equal("completed", old.Analysis!.Status);
        Assert.Equal("unavailable", old.CitationAttribution!.Availability);
        Assert.Equal("DETAILED_ANALYSIS_NOT_READY", old.CitationAttribution.UnavailableReason);
        Assert.All(old.CitationAttribution.Items, i => Assert.Equal(CitationOwnership.Unknown, i.Ownership));

        var onlyDomestic = await f.Store.CitationDomainSummaryAsync(f.Tenant, f.Site, f.Filter with { Engines = ["doubao"] }, null);
        Assert.Equal(1, onlyDomestic.Metrics.EligibleAnswers);
        Assert.Equal(3, onlyDomestic.Metrics.CitationEvents);
        // Old core KPI still includes core-complete legacy rows; no shared denominator change.
        var legacyOverview = await f.Store.OverviewAsync(f.Tenant, f.Site, new(), token);
        Assert.Equal(4, legacyOverview.SourceGroups!.Values.Sum(group => group.Metrics.EligibleChecks));
        Assert.Equal(0, legacyOverview.Metrics.EligibleChecks); // legacy mixed-source top-level remains unavailable
        output.WriteLine("Executed SQL population: 7 candidates, 3 eligible incl zero-citation, 6 events; provider/filter/evidence/detail assertions passed.");
    }

    [Fact]
    public async Task SnapshotsFreezeArrivalAnalysisRulesAndConfigurationAcrossPublicationAndTenantBoundaries()
    {
        var f = await Fixture.CreateAsync();
        var original = await f.CheckAsync(urls: ["https://brand.test/a", "https://source.test/b"]);
        await f.CompleteAsync(original);
        var pending = await f.CheckAsync(urls: ["https://brand.test/pending"]);
        var before = await f.SummaryAsync();
        var token = before.Meta.SnapshotToken;
        await f.CompleteAsync(pending);
        var late = await f.CheckAsync(urls: ["https://source.test/late"]);
        await f.CompleteAsync(late);
        var stale = await f.SummaryAsync(token);
        Assert.Equal(before.Metrics, stale.Metrics);
        Assert.Equal(1, stale.Quality.AnalysisPending);
        // A cache miss must not let later arrival/analysis proof leak into the old token.
        f.Store = new AnalyticsQueryStore(f.Database, f.Analyzer);
        Assert.Equal(before.Metrics, (await f.SummaryAsync(token)).Metrics);
        await ErrorAsync(() => f.Store.MonitoringAnswerDetailAsync(f.Tenant, f.Site, late, token, null, null, "domain-summary-v1"), "SNAPSHOT_REFRESH_REQUIRED");
        var fresh = await f.SummaryAsync();
        Assert.Equal(3, fresh.Metrics.EligibleAnswers);
        Assert.Equal(4, fresh.Metrics.CitationEvents);
        Assert.NotEqual(before.Meta.AnalysisWatermark, fresh.Meta.AnalysisWatermark);

        var currentBrand = (await f.Management.GetBrandAsync(f.Tenant, f.Site))!;
        await f.Management.UpdateBrandAsync(f.Tenant, f.Site,
            new BrandPatch(PrimaryDomain: "source.test", ExpectedVersion: currentBrand.SubjectConfigVersion, AdditionalDomains: []));
        var outdated = await f.SummaryAsync(token);
        Assert.True(outdated.Meta.ConfigurationOutdated);
        Assert.Equal(before.Metrics, outdated.Metrics);
        Assert.Equal(before.Meta.SubjectConfigVersion, outdated.Meta.SubjectConfigVersion);
        var ownedOld = await f.Store.CitationDomainSummaryDomainsAsync(f.Tenant, f.Site, token, null, null, "owned", null, 20);
        Assert.Equal("brand.test", Assert.Single(ownedOld.Items).Domain);
        var job = await f.Analyzer.CreateJobAsync(f.Tenant, f.Site, Guid.NewGuid().ToString(), new AnalysisJobRequest());
        await f.PublishAsync(job.JobId);
        var changed = await f.SummaryAsync();
        Assert.False(changed.Meta.ConfigurationOutdated);
        Assert.NotEqual(before.Meta.ReleaseId, changed.Meta.ReleaseId);
        var ownedNew = await f.Store.CitationDomainSummaryDomainsAsync(f.Tenant, f.Site, changed.Meta.SnapshotToken, null, null, "owned", null, 20);
        Assert.Equal("source.test", Assert.Single(ownedNew.Items).Domain);
        Assert.Equal(before.Metrics, (await f.SummaryAsync(token)).Metrics);
        Assert.Equal(before.Meta.ReleaseId, (await f.Store.MonitoringAnswerDetailAsync(f.Tenant, f.Site, original, token, null, null, "domain-summary-v1"))!.CitationAttribution!.Meta!.ReleaseId);

        var other = await Fixture.CreateAsync();
        await ErrorAsync(() => f.Store.CitationDomainSummaryDomainsAsync(other.Tenant, f.Site, token, null, null, null, null, 20), "RESOURCE_NOT_FOUND");
        await ErrorAsync(() => f.Store.CitationDomainSummaryDomainsAsync(f.Tenant, other.Site, token, null, null, null, null, 20), "RESOURCE_NOT_FOUND");
        await ErrorAsync(() => other.Store.CitationDomainSummaryDomainsAsync(other.Tenant, other.Site, token, null, null, null, null, 20), "SNAPSHOT_EXPIRED");
        await ErrorAsync(() => f.Store.CitationDomainSummaryAsync(f.Tenant, f.Site, f.Filter with { Engines = ["doubao"] }, token), "INVALID_FILTER");

        // All repeated requests bound exactly one version record, including raw-detail use.
        Assert.Equal(1, await f.ScalarLongAsync("SELECT COUNT(*) FROM analytics_snapshot_projections WHERE token_hash=@hash", ("@hash", Hash(token))));
        await f.ExecAsync("UPDATE analytics_snapshot_projections SET category_rules_version='unsupported-fixture' WHERE token_hash=@hash", ("@hash", Hash(token)));
        await ErrorAsync(() => f.Store.CitationDomainSummaryDomainsAsync(f.Tenant, f.Site, token, null, null, null, null, 20), "SNAPSHOT_REFRESH_REQUIRED");
        await f.ExecAsync("UPDATE analytics_snapshots SET expires_at=UTC_TIMESTAMP(6)-INTERVAL 1 SECOND WHERE token_hash=@hash", ("@hash", Hash(fresh.Meta.SnapshotToken)));
        f.Store = new AnalyticsQueryStore(f.Database, f.Analyzer);
        await ErrorAsync(() => f.SummaryAsync(fresh.Meta.SnapshotToken), "SNAPSHOT_EXPIRED");
        output.WriteLine("Executed SQL snapshot checks: arrival/analysis fences, cache-miss stability, frozen configuration, new publication, tenant boundaries, single binding and unsupported/expired tokens.");
    }

    [Fact]
    public async Task ExplicitScopedRebuildReusesOnlyProvenOutsideScopeAndPreservesOldUnprovenSnapshot()
    {
        var f = await Fixture.CreateAsync();
        var inside = await f.CheckAsync(urls: ["https://brand.test/inside"], captured: f.Now.AddDays(-1));
        var reusableZero = await f.CheckAsync(captured: f.Now.AddDays(-5));
        var reusable = await f.CheckAsync(urls: ["https://www.www.brand.test/nested"], captured: f.Now.AddDays(-5));
        await f.CompleteAsync(inside, reusableZero, reusable);
        var unproven = await f.CheckAsync(urls: ["https://brand.test/legacy"], captured: f.Now.AddDays(-5));
        await f.CoreOnlyAsync(unproven);
        var old = await f.SummaryAsync();
        Assert.Equal(3, old.Metrics.EligibleAnswers);
        Assert.Equal(1, old.Quality.AnalysisPending);
        var nested = (await f.Store.CitationDomainSummaryDomainsAsync(f.Tenant, f.Site, old.Meta.SnapshotToken, "www.brand", null, null, null, 20)).Items;
        Assert.Equal("www.brand.test", Assert.Single(nested).Domain);
        Assert.Equal(CitationOwnership.NonOwned, nested[0].Ownership);
        var nestedDetail = (await f.Store.MonitoringAnswerDetailAsync(f.Tenant, f.Site, reusable, old.Meta.SnapshotToken, null, null, "domain-summary-v1"))!;
        Assert.Equal(nested[0].Domain, nestedDetail.CitationAttribution!.Items[0].CanonicalHost);
        Assert.Equal(nested[0].Ownership, nestedDetail.CitationAttribution.Items[0].Ownership);
        Assert.Equal(0, await f.ScalarLongAsync("SELECT COUNT(*) FROM citation_events WHERE release_id=UUID_TO_BIN(@release) AND url_hash<>LOWER(SHA2(normalized_url,256))", ("@release", old.Meta.ReleaseId)));

        var job = await f.Analyzer.CreateJobAsync(f.Tenant, f.Site, Guid.NewGuid().ToString(), new AnalysisJobRequest(From: f.Now.AddDays(-2), To: f.Now));
        Assert.Equal(2, job.ReusedChecks);
        Assert.Equal(2, await f.ScalarLongAsync("SELECT COUNT(*) FROM check_detailed_completions WHERE release_id=UUID_TO_BIN(@release)", ("@release", job.ReleaseId)));
        Assert.Equal(2, await f.ScalarLongAsync("SELECT COUNT(*) FROM analysis_check_work WHERE release_id=UUID_TO_BIN(@release) AND status='pending'", ("@release", job.ReleaseId)));
        await f.PublishAsync(job.JobId);
        var next = await f.SummaryAsync();
        Assert.Equal(4, next.Metrics.EligibleAnswers);
        Assert.Equal(3, next.Metrics.CitationEvents);
        Assert.Equal(0, next.Quality.AnalysisPending);
        Assert.Equal(4, await f.ScalarLongAsync("SELECT COUNT(*) FROM check_detailed_completions WHERE release_id=UUID_TO_BIN(@release)", ("@release", next.Meta.ReleaseId)));
        var oldAgain = await f.SummaryAsync(old.Meta.SnapshotToken);
        Assert.Equal(old.Metrics, oldAgain.Metrics);
        Assert.Equal(1, oldAgain.Quality.AnalysisPending);
        Assert.Equal(0, await f.ScalarLongAsync("SELECT COUNT(*) FROM check_detailed_completions WHERE release_id=UUID_TO_BIN(@release) AND check_id=UUID_TO_BIN(@check)", ("@release", old.Meta.ReleaseId), ("@check", unproven)));
        output.WriteLine("Executed SQL scoped reuse: 2 proven outside-scope Checks reused incl zero-citation; requested and unproven Checks processed only in explicit new release; old snapshot unchanged.");
    }

    [Fact]
    public async Task MarkerFailureRollsBackAllFactsAndRetryDeliveryDoesNotDuplicateProof()
    {
        var f = await Fixture.CreateAsync();
        var check = await f.CheckAsync(urls: ["https://brand.test/atomic"]);
        var trigger = "citation_test_marker_failure_" + check.ToString("N");
        // Deliberate test-only fault at the final marker INSERT. Exact Check ID
        // limits the trigger to this fixture; retained until cleanup is approved.
        await f.ExecAsync($"""
            CREATE TRIGGER `{trigger}` BEFORE INSERT ON check_detailed_completions
            FOR EACH ROW BEGIN
                IF NEW.check_id=UUID_TO_BIN('{check}') THEN
                    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='isolated citation marker fault';
                END IF;
            END;
            """);
        await f.Analyzer.ProcessPendingAsync("citation-atomic-test", 100);
        Assert.Equal(0, await f.ScalarLongAsync("SELECT COUNT(*) FROM check_analysis WHERE check_id=UUID_TO_BIN(@check)", ("@check", check)));
        Assert.Equal(0, await f.ScalarLongAsync("SELECT COUNT(*) FROM analysis_events WHERE check_id=UUID_TO_BIN(@check)", ("@check", check)));
        Assert.Equal(0, await f.ScalarLongAsync("SELECT COUNT(*) FROM citation_events WHERE check_id=UUID_TO_BIN(@check)", ("@check", check)));
        Assert.Equal(0, await f.ScalarLongAsync("SELECT COUNT(*) FROM check_query_events WHERE check_id=UUID_TO_BIN(@check)", ("@check", check)));
        Assert.Equal(0, await f.ScalarLongAsync("SELECT COUNT(*) FROM check_detailed_completions WHERE check_id=UUID_TO_BIN(@check)", ("@check", check)));
        var failed = await f.SummaryAsync();
        Assert.Equal(0, failed.Metrics.EligibleAnswers);
        Assert.Equal(1, failed.Quality.AnalysisFailed);
        var good = await f.CheckAsync(urls: ["https://brand.test/good"]);
        await f.CompleteAsync(good);
        await f.Analyzer.ProcessPendingAsync("citation-duplicate-delivery", 100);
        Assert.Equal(1, await f.ScalarLongAsync("SELECT COUNT(*) FROM check_detailed_completions WHERE check_id=UUID_TO_BIN(@check)", ("@check", good)));
        Assert.Equal(1, await f.ScalarLongAsync("SELECT COUNT(*) FROM citation_events WHERE check_id=UUID_TO_BIN(@check)", ("@check", good)));
        output.WriteLine("Executed SQL rollback after final proof failure: core/detail/events all absent; successful repeated worker delivery leaves one proof. Retained exact test trigger: " + trigger);
    }

    [Fact]
    public void OwnCitationGateIsOneSharedSqlFragmentWithArticleMonitoringFences()
    {
        var gate = ArticleMonitoringQuery.OwnCitationGateSql;
        foreach (var fence in new[]
        {
            "FROM content_publication_links p", "ce.url_hash=r.url_hash", "rel.status='published'",
            "COALESCE(p.published_at,p.created_at)<=c.captured_at",
            "(p.status='active' OR p.disabled_at IS NULL OR c.captured_at<p.disabled_at)",
            "article.monitor_status='active'", "monitor_scope.enabled_at", "'$.activeWindows'",
            "arrival.id<=@arrival", "analysis.completed_event_id<=@analysis", "c.captured_at>=@from AND c.captured_at<@to",
            "p.tenant_id=UUID_TO_BIN(@tenant) AND p.site_id=UUID_TO_BIN(@site)"
        })
            Assert.Contains(fence, gate);
        Assert.DoesNotContain("@article", gate);
        Assert.DoesNotContain(";", gate);
        var pairs = AnalyticsQueryStore.OwnCitationPairSql(2);
        Assert.StartsWith("SELECT DISTINCT BIN_TO_UUID(c.id), ce.url_hash\n" + gate + "\n", pairs);
        Assert.EndsWith("AND ce.url_hash IN (@h0, @h1);", pairs);
        var checks = AnalyticsQueryStore.CitationUrlCheckSql(2);
        foreach (var fence in new[] { "c.tenant_id = UUID_TO_BIN(@tenant)", "a.release_id = UUID_TO_BIN(@release)", "ar.id <= @arrival",
                     "a.completed_event_id <= @analysis", "c.captured_at >= @from", "e.url_hash IN (@h0, @h1)" })
            Assert.Contains(fence, checks);
    }

    [Fact]
    public async Task DomainDetailUrlsAndRecordsCitedDomainShareDomainRowCountsAndTheArticleMonitoringGate()
    {
        var f = await Fixture.CreateAsync();
        var otherPrompt = await f.PromptAsync("Second citation question");
        var c1 = await f.CheckAsync(urls: ["https://brand.test/mine", "https://brand.test/other", "https://source.test/a"], captured: f.Now.AddDays(-2));
        var c2 = await f.CheckAsync("domestic_collector", urls: ["https://brand.test/mine", "https://www.brand.test/other", "https://brand.test/inactive"],
            captured: f.Now.AddDays(-1), promptId: otherPrompt);
        var c3 = await f.CheckAsync(urls: ["https://brand.test/mine"], captured: f.Now.AddDays(-5));          // before publication
        var c4 = await f.CheckAsync("domestic_collector", urls: ["https://brand.test/disabled"], captured: f.Now.AddHours(-1)); // after disabling
        var c5 = await f.CheckAsync(urls: ["https://brand.test/disabled"], captured: f.Now.AddDays(-4));      // before disabling
        var c6 = await f.CheckAsync(urls: ["https://brand.test/scoped"], captured: f.Now.AddHours(-2));       // outside monitor window
        var c7 = await f.CheckAsync(urls: ["https://brand.test/scoped"], captured: f.Now.AddDays(-4));        // inside monitor window
        await f.CompleteAsync(c1, c2, c3, c4, c5, c6, c7);
        var pending = await f.CheckAsync(urls: ["https://brand.test/mine"], captured: f.Now.AddHours(-3));    // not analyzed: excluded everywhere
        await f.PublicationAsync("https://brand.test/mine", f.Now.AddDays(-3));
        await f.PublicationAsync("https://brand.test/disabled", f.Now.AddDays(-6), status: "disabled", disabledAt: f.Now.AddHours(-3));
        await f.PublicationAsync("https://brand.test/scoped", f.Now.AddDays(-9), window: (f.Now.AddDays(-9), f.Now.AddDays(-3)));
        await f.PublicationAsync("https://brand.test/inactive", f.Now.AddDays(-9), monitor: "inactive");

        var summary = await f.SummaryAsync();
        var token = summary.Meta.SnapshotToken;
        var domains = await f.Store.CitationDomainSummaryDomainsAsync(f.Tenant, f.Site, token, null, null, null, null, 20);
        var brandRow = domains.Items.Single(d => d.Domain == "brand.test");
        Assert.Equal(10, brandRow.CitationEvents);
        Assert.Equal(7, brandRow.AnswerCount);

        // B1: "all" equals the domain row; Σengines = Σtrend = total; "mine" follows the article-monitoring gate.
        var detail = await f.Store.CitationDomainDetailAsync(f.Tenant, f.Site, token, "brand.test", null);
        Assert.Equal(token, detail.Meta.SnapshotToken);
        Assert.Equal(brandRow.CitationEvents, detail.CitationEvents);
        Assert.Equal(4, detail.MyCitationEvents); // c1, c2 mine; c5 disabled-before; c7 scoped-inside
        Assert.Equal(new[] { ("chatgpt", 6L, 3L), ("doubao", 4L, 1L) }, detail.Engines.Select(e => (e.Engine, e.CitationEvents, e.MyCitationEvents)));
        Assert.Equal(detail.CitationEvents, detail.Trend.Sum(t => t.CitationEvents));
        Assert.Equal(detail.MyCitationEvents, detail.Trend.Sum(t => t.MyCitationEvents));
        var firstDay = DateOnly.FromDateTime(summary.Meta.Filters.From.UtcDateTime);
        var lastDay = DateOnly.FromDateTime(summary.Meta.Filters.To.AddTicks(-1).UtcDateTime);
        Assert.Equal(lastDay.DayNumber - firstDay.DayNumber + 1, detail.Trend.Count);
        Assert.Equal(firstDay, detail.Trend[0].Date);
        Assert.Contains(detail.Trend, t => t.CitationEvents == 0);
        // The content-management article monitoring (same shared gate SQL) reports the same "my" citation events.
        var articleMonitoring = await new ArticleMonitoringQuery(f.Store, f.Database).SummaryAsync(f.Tenant, f.Site, null,
            summary.Meta.Filters.From, summary.Meta.Filters.To, null);
        Assert.Equal(detail.MyCitationEvents, articleMonitoring.CitationEvents);
        var doubaoDetail = await f.Store.CitationDomainDetailAsync(f.Tenant, f.Site, token, "BRAND.test", "Doubao");
        Assert.Equal("doubao", doubaoDetail.Engine);
        Assert.Equal(detail.Engines, doubaoDetail.Engines);
        Assert.Equal(detail.CitationEvents, doubaoDetail.CitationEvents);
        Assert.Equal(4, doubaoDetail.Trend.Sum(t => t.CitationEvents));
        Assert.Equal(1, doubaoDetail.Trend.Sum(t => t.MyCitationEvents));
        var missing = await f.Store.CitationDomainDetailAsync(f.Tenant, f.Site, token, "missing.test", null);
        Assert.Equal(0, missing.CitationEvents);
        Assert.Empty(missing.Engines);
        Assert.Equal(detail.Trend.Count, missing.Trend.Count);
        Assert.All(missing.Trend, t => Assert.Equal(0, t.CitationEvents));
        foreach (var row in domains.Items)
            Assert.Equal(row.CitationEvents, (await f.Store.CitationDomainDetailAsync(f.Tenant, f.Site, token, row.Domain, null)).CitationEvents);

        // B2: legacy /urls unchanged in rows, ranks and count; new fields filled for page rows.
        var all = await f.Store.CitationDomainSummaryUrlsAsync(f.Tenant, f.Site, token, null, null, 50);
        Assert.Equal(summary.Metrics.DistinctUrls, all.FilteredUrlCount);
        Assert.Equal(Enumerable.Range(1, all.Items.Count), all.Items.Select(u => u.Rank));
        var brand = await f.Store.CitationDomainSummaryUrlsAsync(f.Tenant, f.Site, token, "brand.test", null, 50);
        Assert.Equal(5, brand.FilteredUrlCount);
        Assert.Equal(all.Items.Where(u => u.Domain == "brand.test").Select(u => u.Rank), brand.Items.Select(u => u.Rank));
        Assert.All(all.Items, u => Assert.Null(u.DomainRank));
        Assert.Equal(new long?[] { 1, 2, 3, 4, 5 }, brand.Items.Select(u => u.DomainRank));
        Assert.Equal("https://brand.test/mine", brand.Items[0].Url);
        var byUrl = brand.Items.ToDictionary(u => u.Url);
        Assert.Equal((true, 2L, 2L, 3L), (byUrl["https://brand.test/mine"].IsMine, byUrl["https://brand.test/mine"].MyCitationEvents,
            byUrl["https://brand.test/mine"].PromptCount, byUrl["https://brand.test/mine"].CitationEvents));
        Assert.Equal((false, 0L), (byUrl["https://brand.test/other"].IsMine, byUrl["https://brand.test/other"].MyCitationEvents));
        Assert.Equal((false, 1L), (byUrl["https://brand.test/disabled"].IsMine, byUrl["https://brand.test/disabled"].MyCitationEvents));
        Assert.Equal((true, 1L), (byUrl["https://brand.test/scoped"].IsMine, byUrl["https://brand.test/scoped"].MyCitationEvents));
        Assert.Equal((true, 0L), (byUrl["https://brand.test/inactive"].IsMine, byUrl["https://brand.test/inactive"].MyCitationEvents));
        Assert.Equal(detail.MyCitationEvents, brand.Items.Sum(u => u.MyCitationEvents));
        Assert.All(brand.Items, u => Assert.Equal("Stored title", u.Title));
        Assert.Equal(all.Items.Single(u => u.Url == "https://brand.test/mine"), byUrl["https://brand.test/mine"] with { DomainRank = null });

        var mine = await f.Store.CitationDomainSummaryUrlsAsync(f.Tenant, f.Site, token, "brand.test", null, null, true, null, 50);
        Assert.Equal(3, mine.FilteredUrlCount);
        Assert.All(mine.Items, u => Assert.True(u.IsMine));
        Assert.Equal(brand.Items.Where(u => u.IsMine).Select(u => u.Rank), mine.Items.Select(u => u.Rank));
        Assert.Equal(brand.Items.Where(u => u.IsMine).Select(u => u.DomainRank), mine.Items.Select(u => u.DomainRank));
        var searched = await f.Store.CitationDomainSummaryUrlsAsync(f.Tenant, f.Site, token, "brand.test", null, " SCOPED ", null, null, 50);
        Assert.Equal(byUrl["https://brand.test/scoped"], Assert.Single(searched.Items));
        Assert.Equal(1, searched.FilteredUrlCount);
        var byTitle = await f.Store.CitationDomainSummaryUrlsAsync(f.Tenant, f.Site, token, "brand.test", null, "stored tit", null, null, 50);
        Assert.Equal(5, byTitle.FilteredUrlCount);
        Assert.Empty((await f.Store.CitationDomainSummaryUrlsAsync(f.Tenant, f.Site, token, "brand.test", null, "%_", null, null, 50)).Items);

        var doubao = await f.Store.CitationDomainSummaryUrlsAsync(f.Tenant, f.Site, token, "brand.test", "doubao", null, null, null, 50);
        Assert.Equal(new[] { "https://brand.test/disabled", "https://brand.test/inactive", "https://brand.test/mine", "https://brand.test/other" },
            doubao.Items.Select(u => u.Url).Order(StringComparer.Ordinal));
        Assert.All(doubao.Items, u => Assert.Equal((1L, 1L, 1L), (u.CitationEvents, u.AnswerCount, u.PromptCount)));
        Assert.Equal(1, doubao.Items.Single(u => u.Url == "https://brand.test/mine").MyCitationEvents);
        Assert.Equal(0, doubao.Items.Single(u => u.Url == "https://brand.test/disabled").MyCitationEvents);
        Assert.Equal(doubao.Items.Select(u => u.Rank).Order(), doubao.Items.Select(u => u.Rank));
        Assert.Equal(new long?[] { 1, 2, 3, 4 }, doubao.Items.Select(u => u.DomainRank)); // recomputed for the engine
        Assert.Equal(doubao.Items.OrderBy(u => u.UrlHash, StringComparer.Ordinal).Select(u => u.Url), doubao.Items.Select(u => u.Url));
        Assert.All(doubao.Items, u => Assert.Equal(doubaoDetail.Engines.Single(e => e.Engine == "doubao").CitationEvents, u.EventShare.Denominator));
        Assert.Empty((await f.Store.CitationDomainSummaryUrlsAsync(f.Tenant, f.Site, token, "brand.test", "gemini", null, null, null, 50)).Items);

        var mineFirst = await f.Store.CitationDomainSummaryUrlsAsync(f.Tenant, f.Site, token, "brand.test", null, null, true, null, 2);
        Assert.Equal(2, mineFirst.Items.Count);
        var mineSecond = await f.Store.CitationDomainSummaryUrlsAsync(f.Tenant, f.Site, token, "brand.test", null, null, true, mineFirst.NextCursor, 2);
        Assert.Equal(mine.Items.Skip(2), mineSecond.Items);
        await ErrorAsync(() => f.Store.CitationDomainSummaryUrlsAsync(f.Tenant, f.Site, token, "brand.test", mineFirst.NextCursor, 2), "INVALID_CURSOR");
        await ErrorAsync(() => f.Store.CitationDomainSummaryUrlsAsync(f.Tenant, f.Site, token, "brand.test", "doubao", null, true, mineFirst.NextCursor, 2), "INVALID_CURSOR");
        await ErrorAsync(() => f.Store.CitationDomainSummaryUrlsAsync(f.Tenant, f.Site, token, "brand.test", null, "mine", true, mineFirst.NextCursor, 2), "INVALID_CURSOR");
        var legacyFirst = await f.Store.CitationDomainSummaryUrlsAsync(f.Tenant, f.Site, token, "brand.test", null, 2);
        await ErrorAsync(() => f.Store.CitationDomainSummaryUrlsAsync(f.Tenant, f.Site, token, "brand.test", null, null, true, legacyFirst.NextCursor, 2), "INVALID_CURSOR");
        await ErrorAsync(() => f.Store.CitationDomainSummaryUrlsAsync(f.Tenant, f.Site, token, null, null, "mine", null, null, 2), "INVALID_FILTER");

        // B5: a snapshot frozen to one engine rejects any other engine sub-filter.
        var chatgptOnly = await f.Store.CitationDomainSummaryAsync(f.Tenant, f.Site, f.Filter with { Engines = ["chatgpt"] }, null);
        await ErrorAsync(() => f.Store.CitationDomainDetailAsync(f.Tenant, f.Site, chatgptOnly.Meta.SnapshotToken, "brand.test", "doubao"), "INVALID_FILTER");
        await ErrorAsync(() => f.Store.CitationDomainSummaryUrlsAsync(f.Tenant, f.Site, chatgptOnly.Meta.SnapshotToken, "brand.test", "doubao", null, null, null, 5), "INVALID_FILTER");
        Assert.Equal(6, (await f.Store.CitationDomainDetailAsync(f.Tenant, f.Site, chatgptOnly.Meta.SnapshotToken, "brand.test", "chatgpt")).CitationEvents);

        // B3: records filtered by cited domain total = domain row answerCount; pending answer excluded.
        var everything = await f.Store.MonitoringRecordsAsync(f.Tenant, f.Site, f.AllSourcesFilter, null, null, 50, null, null, null);
        Assert.Contains(everything.Items, r => r.CheckId == pending);
        var cited = await f.Store.MonitoringRecordsAsync(f.Tenant, f.Site, f.AllSourcesFilter, everything.Meta.SnapshotToken, null, 50, null, null, "brand.test");
        Assert.Equal(brandRow.AnswerCount, cited.TotalCount);
        Assert.Equal(new[] { c1, c2, c3, c4, c5, c6, c7 }.Order(), cited.Items.Select(r => r.CheckId).Order());
        var source = await f.Store.MonitoringRecordsAsync(f.Tenant, f.Site, f.AllSourcesFilter, everything.Meta.SnapshotToken, null, 50, null, null, "source.test");
        Assert.Equal(c1, Assert.Single(source.Items).CheckId);
        Assert.Equal(domains.Items.Single(d => d.Domain == "source.test").AnswerCount, source.TotalCount);
        var citedFirst = await f.Store.MonitoringRecordsAsync(f.Tenant, f.Site, f.AllSourcesFilter, everything.Meta.SnapshotToken, null, 3, null, null, "brand.test");
        Assert.Equal(7, citedFirst.TotalCount);
        var citedSecond = await f.Store.MonitoringRecordsAsync(f.Tenant, f.Site, f.AllSourcesFilter, everything.Meta.SnapshotToken, citedFirst.NextCursor, 3, null, null, "brand.test");
        Assert.Equal(7, citedSecond.TotalCount);
        Assert.Equal(cited.Items.Skip(3).Take(3).Select(r => r.CheckId), citedSecond.Items.Select(r => r.CheckId));
        await ErrorAsync(() => f.Store.MonitoringRecordsAsync(f.Tenant, f.Site, f.AllSourcesFilter, everything.Meta.SnapshotToken, citedFirst.NextCursor, 3, null, null, "source.test"), "INVALID_CURSOR");
        await ErrorAsync(() => f.Store.MonitoringRecordsAsync(f.Tenant, f.Site, f.AllSourcesFilter, everything.Meta.SnapshotToken, citedFirst.NextCursor, 3, null, null, null), "INVALID_CURSOR");
        Assert.Equal(0, (await f.Store.MonitoringRecordsAsync(f.Tenant, f.Site, f.AllSourcesFilter, everything.Meta.SnapshotToken, null, 3, null, null, "missing.test")).TotalCount);
        output.WriteLine("Executed SQL domain detail: total=row 10, mine 4 via shared gate (publication time, disable window, monitor scope, inactive monitor), engines/trend sums, URL engine/q/mine/cursor fences, engine-frozen snapshot, records citedDomain total=answerCount 7.");
    }

    private static async Task ErrorAsync<T>(Func<Task<T>> call, string code)
        => Assert.Equal(code, (await Assert.ThrowsAsync<CollectorStoreException>(call)).Code);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class Fixture
    {
        private readonly string connectionString;
        private int sample;
        private Guid prompt;
        private readonly Guid run = Guid.NewGuid();
        private readonly Guid cloroTarget = Guid.NewGuid();
        private readonly Guid domesticTarget = Guid.NewGuid();
        public Guid Tenant { get; } = Guid.NewGuid();
        public Guid Site { get; private set; }
        public DateTimeOffset Now { get; } = DateTimeOffset.UtcNow;
        public MySqlDatabase Database { get; }
        public ManagementStore Management { get; }
        public AnalysisStore Analyzer { get; }
        public AnalyticsQueryStore Store { get; set; }
        public AnalyticsFilterRequest Filter => new(From: Now.AddDays(-10), To: Now.AddMinutes(1), TimeZone: "UTC");

        private Fixture(string connection)
        {
            connectionString = connection;
            Database = new(connection); Management = new(Database); Analyzer = new(Database); Store = new(Database, Analyzer);
        }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = Environment.GetEnvironmentVariable("JOGEOAI_CITATION_TEST_CONNECTION");
            Assert.False(string.IsNullOrWhiteSpace(connection), "JOGEOAI_CITATION_TEST_CONNECTION is required; SQL tests never silently return.");
            var builder = new MySqlConnectionStringBuilder(connection!);
            Assert.Equal("127.0.0.1", builder.Server);
            Assert.Equal(33316u, builder.Port);
            Assert.Equal("jogeoai_citation_test_20260930", builder.Database);
            var fixture = new Fixture(connection!);
            await fixture.ExecAsync("""
                INSERT INTO tenants(id,name,status,created_at,updated_at)
                VALUES(UUID_TO_BIN(@tenant),'isolated citation contract','active',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
                """, ("@tenant", fixture.Tenant));
            var brand = await fixture.Management.CreateBrandAsync(fixture.Tenant,
                new BrandWrite("Citation Contract", "brand.test", AdditionalDomains: ["extra.test"], IncludeSubdomains: false, Description: "测试品牌提供设备与服务。"));
            fixture.Site = brand.SiteId;
            fixture.prompt = (await fixture.Management.CreatePromptAsync(fixture.Tenant, fixture.Site, new PromptWrite("Historical citation question"))).PromptId;
            await fixture.ExecAsync("""
                INSERT INTO visibility_runs(id,tenant_id,site_id,status,idempotency_key,requested_at,total_checks,succeeded_checks,created_at,updated_at)
                VALUES(UUID_TO_BIN(@run),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),'succeeded',@key,UTC_TIMESTAMP(6),0,0,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
                INSERT INTO run_engines(id,run_id,engine,provider,target_key,capture_mode,country,locale,created_at)
                VALUES(UUID_TO_BIN(@cloro),UUID_TO_BIN(@run),'chatgpt','cloro',@cloroKey,'scraped','US','en-US',UTC_TIMESTAMP(6)),
                      (UUID_TO_BIN(@domestic),UUID_TO_BIN(@run),'doubao','domestic_collector',@domesticKey,'scraped','CN','zh-CN',UTC_TIMESTAMP(6));
                """, ("@run", fixture.run), ("@tenant", fixture.Tenant), ("@site", fixture.Site), ("@key", Guid.NewGuid().ToString()),
                ("@cloro", fixture.cloroTarget), ("@domestic", fixture.domesticTarget), ("@cloroKey", new string('c', 64)), ("@domesticKey", new string('d', 64)));
            return fixture;
        }

        public async Task<Guid> CheckAsync(string provider = "cloro", string? answer = "Citation Contract complete answer",
            string[]? urls = null, string status = "succeeded", DateTimeOffset? captured = null, Guid? promptId = null)
        {
            var check = Guid.NewGuid();
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var insert = new MySqlCommand("""
                INSERT INTO visibility_checks(id,tenant_id,site_id,run_id,prompt_id,engine,provider,status,prompt_snapshot,
                    answer,answer_present,search_queries,query_availability,captured_at,capture_mode,evidence_class,country,locale,
                    run_target_id,sample_index,prompt_version_id,prompt_tags_snapshot,branded_status_snapshot,subject_config_version,created_at,updated_at)
                SELECT UUID_TO_BIN(@check),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@run),p.id,@engine,@provider,@status,p.content,
                    @answer,@present,JSON_ARRAY('actual query'),'available',@captured,'scraped','observed_surface',@country,@locale,
                    UUID_TO_BIN(@target),@sample,p.current_version_id,JSON_ARRAY(),'unbranded',b.subject_config_version,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6)
                FROM prompts p JOIN site_brand_settings b ON b.site_id=p.site_id WHERE p.id=UUID_TO_BIN(@prompt);
                """, connection, transaction))
            {
                Add(insert, ("@check", check), ("@tenant", Tenant), ("@site", Site), ("@run", run), ("@prompt", promptId ?? prompt),
                    ("@engine", provider == "cloro" ? "chatgpt" : "doubao"), ("@provider", provider), ("@status", status),
                    ("@answer", answer), ("@present", !string.IsNullOrWhiteSpace(answer)), ("@captured", (captured ?? Now.AddHours(-1)).UtcDateTime),
                    ("@country", provider == "cloro" ? "US" : "CN"), ("@locale", provider == "cloro" ? "en-US" : "zh-CN"),
                    ("@target", provider == "cloro" ? cloroTarget : domesticTarget), ("@sample", ++sample));
                await insert.ExecuteNonQueryAsync();
            }
            for (var i = 0; i < (urls?.Length ?? 0); i++)
            {
                await using var insert = new MySqlCommand("""
                    INSERT INTO citations(id,check_id,normalized_url,source_domain,title,position,created_at)
                    VALUES(UUID_TO_BIN(@id),UUID_TO_BIN(@check),@url,@domain,'Stored title',@position,UTC_TIMESTAMP(6));
                    """, connection, transaction);
                Add(insert, ("@id", Guid.NewGuid()), ("@check", check), ("@url", urls![i]), ("@domain", new Uri(urls[i]).Host), ("@position", i + 1));
                await insert.ExecuteNonQueryAsync();
            }
            await AnalysisEnqueue.QueueAsync(connection, transaction, check, default);
            await transaction.CommitAsync();
            return check;
        }

        public Task CoreOnlyAsync(Guid check, bool legacyV1Facts = false) => ExecAsync("""
            START TRANSACTION;
            INSERT INTO analysis_events(release_id,check_id,completed_at)
            SELECT release_id,check_id,UTC_TIMESTAMP(6) FROM analysis_check_work WHERE check_id=UUID_TO_BIN(@check);
            INSERT INTO check_analysis(release_id,check_id,tenant_id,site_id,status,configuration_missing,completed_event_id,completed_at)
            SELECT w.release_id,w.check_id,w.tenant_id,w.site_id,'completed',FALSE,e.id,UTC_TIMESTAMP(6)
            FROM analysis_check_work w JOIN analysis_events e ON e.release_id=w.release_id AND e.check_id=w.check_id
            WHERE w.check_id=UUID_TO_BIN(@check);
            UPDATE analysis_check_work SET status='completed' WHERE check_id=UUID_TO_BIN(@check);
            """ + (legacyV1Facts ? """
            INSERT INTO citation_events(release_id,check_id,url_hash,normalized_url,domain,title,first_position,url_version,normalization_status)
            SELECT w.release_id,c.check_id,LOWER(SHA2(c.normalized_url,256)),c.normalized_url,c.source_domain,c.title,c.position,'url-v1','complete'
            FROM citations c JOIN analysis_check_work w ON w.check_id=c.check_id
            JOIN analysis_releases r ON r.id=w.release_id AND r.detail_version='detail-v1'
            WHERE c.check_id=UUID_TO_BIN(@check);
            """ : "") + " COMMIT;", ("@check", check));

        public async Task CompleteAsync(params Guid[] checks)
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                await Analyzer.ProcessPendingAsync("citation-contract-test", 100);
                var proofs = await Task.WhenAll(checks.Select(id => ScalarLongAsync(
                    "SELECT COUNT(*) FROM check_detailed_completions WHERE tenant_id=UUID_TO_BIN(@tenant) AND check_id=UUID_TO_BIN(@check)",
                    ("@tenant", Tenant), ("@check", id))));
                if (proofs.All(count => count == 1)) return;
            }
            Assert.Fail("The explicitly isolated fixture did not complete its real analysis work in twenty batches.");
        }
        public async Task PublishAsync(Guid job)
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                await Analyzer.ProcessPendingAsync("citation-contract-rebuild", 100);
                if ((await Analyzer.GetJobAsync(Tenant, Site, job))?.Status == "published") return;
            }
            Assert.Fail("The explicitly requested isolated analysis release did not publish.");
        }
        public async Task<Guid> PromptAsync(string text)
            => (await Management.CreatePromptAsync(Tenant, Site, new PromptWrite(text))).PromptId;

        private Guid? author;

        /// <summary>登记一条品牌发布链接（文章 + publication 引用 + 链接），可选停用时间与监控范围窗口。</summary>
        public async Task PublicationAsync(string url, DateTimeOffset publishedAt, string status = "active",
            DateTimeOffset? disabledAt = null, string monitor = "active", (DateTimeOffset From, DateTimeOffset To)? window = null)
        {
            if (author is null)
            {
                author = Guid.NewGuid();
                await ExecAsync("""
                    INSERT INTO auth_users (id, email, password_hash, status, created_at, updated_at)
                    VALUES (UUID_TO_BIN(@id), @email, 'not-a-login', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                    """, ("@id", author.Value), ("@email", $"citation-{author:N}@example.test"));
            }
            var article = Guid.NewGuid();
            var reference = Guid.NewGuid();
            await ExecAsync("""
                INSERT INTO content_articles (id,tenant_id,site_id,created_by,source_kind,status,title,language,monitor_status,created_at,updated_at)
                VALUES (UUID_TO_BIN(@article),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@author),'ai_generated','active',@title,'zh-CN',@monitor,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
                INSERT INTO content_references (id,tenant_id,site_id,url_original,url_normalized,url_hash,kind,created_at,updated_at)
                VALUES (UUID_TO_BIN(@reference),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),@url,@url,@hash,'publication',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
                INSERT INTO content_publication_links (id,tenant_id,site_id,article_id,reference_id,published_at,status,created_by,created_at,disabled_at)
                VALUES (UUID_TO_BIN(@link),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@article),UUID_TO_BIN(@reference),@published,@status,UUID_TO_BIN(@author),@published,@disabled);
                """, ("@article", article), ("@reference", reference), ("@link", Guid.NewGuid()), ("@tenant", Tenant), ("@site", Site),
                ("@author", author.Value), ("@title", "Publication " + url), ("@monitor", monitor), ("@url", url), ("@hash", Hash(url)),
                ("@published", publishedAt.UtcDateTime), ("@status", status), ("@disabled", disabledAt?.UtcDateTime));
            if (window is { } range)
                await ExecAsync("""
                    INSERT INTO content_monitor_scopes (id,tenant_id,site_id,article_id,mode,filter_snapshot,enabled_at,created_at,updated_at)
                    VALUES (UUID_TO_BIN(@id),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@article),'custom',
                            JSON_OBJECT('activeWindows', JSON_ARRAY(JSON_OBJECT('from', @from, 'to', @to))),@from,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
                    """, ("@id", Guid.NewGuid()), ("@tenant", Tenant), ("@site", Site), ("@article", article),
                    ("@from", range.From.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.ffffff")), ("@to", range.To.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.ffffff")));
        }

        public AnalyticsFilterRequest AllSourcesFilter => Filter with
        {
            Providers = ["cloro", "doubao_api", "deepseek_api", "packy_qwen", "packy_glm", "packy_kimi", "packy_yuanbao", "domestic_collector"], CaptureModes = ["api", "scraped"],
            EvidenceClasses = ["api_response", "observed_surface", "unknown"]
        };

        public Task<CitationDomainSummaryView> SummaryAsync(string? token = null)
            => Store.CitationDomainSummaryAsync(Tenant, Site, Filter, token);
        public Task<long> CountAsync(string table) => ScalarLongAsync($"SELECT COUNT(*) FROM `{table}` WHERE site_id=UUID_TO_BIN(@site)", ("@site", Site));
        public async Task ExecAsync(string sql, params (string Key, object? Value)[] args)
        {
            await using var connection = new MySqlConnection(connectionString); await connection.OpenAsync();
            await using var command = new MySqlCommand(sql, connection); Add(command, args); await command.ExecuteNonQueryAsync();
        }
        public async Task<long> ScalarLongAsync(string sql, params (string Key, object? Value)[] args)
        {
            await using var connection = new MySqlConnection(connectionString); await connection.OpenAsync();
            await using var command = new MySqlCommand(sql, connection); Add(command, args); return Convert.ToInt64(await command.ExecuteScalarAsync());
        }
        private static void Add(MySqlCommand command, params (string Key, object? Value)[] args)
        { foreach (var (key, value) in args) command.Parameters.AddWithValue(key, value is Guid id ? id.ToString() : value); }
    }
}
