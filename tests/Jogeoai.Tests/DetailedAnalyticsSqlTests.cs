using System.Diagnostics;
using System.Text.Json;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Jogeoai.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Xunit.Abstractions;

namespace Jogeoai.Tests;

public sealed class DetailedAnalyticsSqlTests(ITestOutputHelper output)
{
    [Fact]
    public async Task AnalysisWorker_PublishesNewCheckWithinSixtySeconds()
    {
        var connectionString = Environment.GetEnvironmentVariable("JOGEOAI_STAGE3_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        Assert.Equal("jogeoai_stage3_verified_20260927", new MySqlConnectionStringBuilder(connectionString).Database);
        var database = new MySqlDatabase(connectionString);
        var management = new ManagementStore(database);
        var analyzer = new AnalysisStore(database);
        var analytics = new AnalyticsQueryStore(database, analyzer);
        var tenant = Guid.NewGuid();
        await Execute(connectionString, """
            INSERT INTO tenants (id, name, status, created_at, updated_at)
            VALUES (UUID_TO_BIN(@tenant), 'analysis latency', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
            """, ("@tenant", tenant.ToString()));
        var brand = await management.CreateBrandAsync(tenant,
            new BrandWrite("Latency Brand", $"{Guid.NewGuid():N}.latency.test", Description: "测试品牌提供设备与服务。"));
        var prompt = await management.CreatePromptAsync(tenant, brand.SiteId,
            new PromptWrite("latency prompt"));
        var run = Guid.NewGuid();
        var target = Guid.NewGuid();
        await SeedRun(connectionString, tenant, brand.SiteId, run, target);

        using var worker = new AnalysisWorker(analyzer, NullLogger<AnalysisWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var check = await SeedCheck(connectionString, tenant, brand.SiteId, run, target,
                prompt.PromptId, 1, "Latency Brand answer", ["actual search"], "available", []);
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(60))
            {
                if ((await analyzer.GetCheckAnalysisAsync(tenant, check))?.Status == "completed")
                {
                    var visible = await analytics.FanOutAsync(tenant, brand.SiteId,
                        new AnalyticsFilterRequest(), null);
                    if (visible.Metrics.QueryEvents == 1 && visible.Metrics.PendingDetailedChecks == 0)
                    {
                        output.WriteLine($"AnalysisWorker 新 Check 到可查询细分分析耗时：{watch.Elapsed.TotalSeconds:F3} 秒。");
                        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60));
                        return;
                    }
                }
                await Task.Delay(100);
            }
            Assert.Fail($"AnalysisWorker 在 60 秒内未将 Check {check} 发布为可查询分析。");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task SucceededWithoutAnswer_StillDerivesObservedQueryAndCitation()
    {
        var connectionString = Environment.GetEnvironmentVariable("JOGEOAI_STAGE3_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        Assert.Equal("jogeoai_stage3_verified_20260927", new MySqlConnectionStringBuilder(connectionString).Database);
        var database = new MySqlDatabase(connectionString);
        var management = new ManagementStore(database);
        var analyzer = new AnalysisStore(database);
        var analytics = new AnalyticsQueryStore(database, analyzer);
        var tenant = Guid.NewGuid();
        await Execute(connectionString, """
            INSERT INTO tenants (id, name, status, created_at, updated_at)
            VALUES (UUID_TO_BIN(@tenant), 'no-answer', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
            """, ("@tenant", tenant.ToString()));
        var brand = await management.CreateBrandAsync(tenant,
            new BrandWrite("No Answer Brand", $"{Guid.NewGuid():N}.brand.test", Description: "测试品牌提供设备与服务。"));
        var prompt = await management.CreatePromptAsync(tenant, brand.SiteId, new PromptWrite("original prompt"));
        var run = Guid.NewGuid();
        var target = Guid.NewGuid();
        await SeedRun(connectionString, tenant, brand.SiteId, run, target);
        var check = await SeedCheck(connectionString, tenant, brand.SiteId, run, target, prompt.PromptId, 1,
            "", ["actual query"], "available", [("https://source.test/item", "Source")]);
        var pendingFan = await analytics.FanOutAsync(tenant, brand.SiteId,
            new AnalyticsFilterRequest(), null);
        Assert.Equal(1, pendingFan.Metrics.PendingDetailedChecks);
        var pendingCitations = await analytics.CitationsAsync(tenant, brand.SiteId,
            new AnalyticsFilterRequest(), pendingFan.Meta.SnapshotToken);
        Assert.Equal(1, pendingCitations.PendingDetailedChecks);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await analyzer.ProcessPendingAsync("no-answer-test", 100);
            if (Assert.IsType<long>(await Scalar(connectionString, """
                SELECT COUNT(*) FROM analysis_check_work w
                WHERE w.check_id=UUID_TO_BIN(@check) AND w.status='completed';
                """, ("@check", check.ToString()))) > 0) break;
        }
        var fan = await analytics.FanOutAsync(tenant, brand.SiteId, new AnalyticsFilterRequest(), null);
        Assert.Equal(1, fan.Metrics.SuccessfulChecks);
        Assert.Equal(0, fan.Metrics.EligibleChecks);
        Assert.Equal(1, fan.Metrics.QueryEvents);
        Assert.Equal(1, fan.Metrics.QueriesPerSuccessfulCheck.Value);
        Assert.Null(fan.Metrics.AssociatedBrandMentionRate.Value);
        var queries = await analytics.FanOutQueriesAsync(tenant, brand.SiteId,
            new AnalyticsFilterRequest(), fan.Meta.SnapshotToken, null, 50);
        Assert.Single(queries.Items);
        Assert.Null(queries.Items[0].AssociatedBrandMentionRate.Value);
        var citations = await analytics.CitationsAsync(tenant, brand.SiteId,
            new AnalyticsFilterRequest(), fan.Meta.SnapshotToken);
        Assert.Equal(1, citations.ExternalCitationEvents);
        Assert.Equal(0, (await analytics.OverviewAsync(tenant, brand.SiteId,
            new AnalyticsFilterRequest(), fan.Meta.SnapshotToken)).Metrics.EligibleChecks);
    }

    [Fact]
    public async Task UpgradedLegacyRows_StayUnknownAndPublishDetailVersionAtomically()
    {
        var connectionString = Environment.GetEnvironmentVariable("JOGEOAI_STAGE3_UPGRADE_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        Assert.Equal("jogeoai_stage3_upgrade_20260927", new MySqlConnectionStringBuilder(connectionString).Database);
        var tenant = Guid.Parse("22222222-2222-4222-8222-222222222222");
        var site = Guid.Parse("33333333-3333-4333-8333-333333333333");
        var check = Guid.Parse("99999999-9999-4999-8999-999999999999");
        var database = new MySqlDatabase(connectionString);
        var analyzer = new AnalysisStore(database);
        var analytics = new AnalyticsQueryStore(database, analyzer);
        await ProcessUntilAsync(analyzer, async () =>
        {
            var analysis = await analyzer.GetCheckAnalysisAsync(tenant, check);
            if (analysis?.Status != "completed") return false;
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new MySqlCommand("""
                SELECT r.detail_version FROM site_analysis_state s
                JOIN analysis_releases r ON r.id = s.published_release_id
                WHERE s.site_id = UUID_TO_BIN(@site);
                """, connection);
            command.Parameters.AddWithValue("@site", site.ToString());
            return (string?)await command.ExecuteScalarAsync() == "detail-v1";
        });
        var detail = (await analytics.CheckDetailAsync(tenant, check, null))!;
        Assert.Equal("unknown", detail.QueryAvailability);
        Assert.Empty(detail.SearchQueries);
        var fan = await analytics.FanOutAsync(tenant, site, new AnalyticsFilterRequest(), null);
        Assert.Equal(0, fan.Metrics.QueryEvents);
        Assert.Equal(1, fan.Metrics.UnknownChecks);
        var citations = await analytics.CitationsAsync(tenant, site,
            new AnalyticsFilterRequest(), fan.Meta.SnapshotToken);
        Assert.Equal(1, citations.ExternalCitationEvents);
        Assert.Equal(0, citations.OwnCitationRate.Numerator);
        Assert.Equal(0, citations.OwnCitationRate.Denominator);
        Assert.Equal(1, citations.OwnEventShare.Denominator);
        Assert.Equal(0, citations.EligibleChecks);
    }

    [Fact]
    public async Task FixedFixture_UsesPublishedFactsAndDistinctExternalCitationEvents()
    {
        var connectionString = Environment.GetEnvironmentVariable("JOGEOAI_STAGE3_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        Assert.Equal("jogeoai_stage3_verified_20260927", new MySqlConnectionStringBuilder(connectionString).Database);
        var database = new MySqlDatabase(connectionString);
        var management = new ManagementStore(database);
        var analyzer = new AnalysisStore(database);
        var analytics = new AnalyticsQueryStore(database, analyzer);
        var tenant = Guid.NewGuid();
        await Execute(connectionString, """
            INSERT INTO tenants (id, name, status, created_at, updated_at)
            VALUES (UUID_TO_BIN(@tenant), 'stage3 fixture', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
            """, ("@tenant", tenant.ToString()));
        var brand = await management.CreateBrandAsync(tenant,
            new BrandWrite("Acme", $"{Guid.NewGuid():N}.brand.test", AdditionalDomains: ["a.test"], Description: "测试品牌提供设备与服务。"));
        _ = await management.CreateCompetitorAsync(tenant, brand.SiteId,
            new CompetitorWrite("Beta", Domains: ["b.test"]));
        var p1 = await management.CreatePromptAsync(tenant, brand.SiteId, new PromptWrite("best tools"));
        var p2 = await management.CreatePromptAsync(tenant, brand.SiteId, new PromptWrite("tool pricing"));
        var run = Guid.NewGuid();
        var target = Guid.NewGuid();
        await SeedRun(connectionString, tenant, brand.SiteId, run, target);
        var c1 = await SeedCheck(connectionString, tenant, brand.SiteId, run, target, p1.PromptId, 1,
            "Acme Acme Acme Beta", ["best tools", "tool comparison", "tool comparison"], "available",
            [("https://a.test/x", "Own title"), ("https://a.test/x", "Own title"),
                ("https://media.test/z", "Media article")]);
        var c2 = await SeedCheck(connectionString, tenant, brand.SiteId, run, target, p1.PromptId, 2,
            "Acme Beta", ["tool comparison"], "available",
            [("https://b.test/y", "Competitor product"), ("https://www.google.com/search?q=best+tools", null)]);
        var c3 = await SeedCheck(connectionString, tenant, brand.SiteId, run, target, p2.PromptId, 1,
            "Acme", ["pricing guide"], "available", [("https://a.test/x", "Own title")]);
        var c4 = await SeedCheck(connectionString, tenant, brand.SiteId, run, target, p2.PromptId, 2,
            "No named brand", [], "unknown", []);
        await ProcessUntilAsync(analyzer, async () =>
            (await analyzer.GetCheckAnalysisAsync(tenant, c4))?.Status == "completed");

        var fan = await analytics.FanOutAsync(tenant, brand.SiteId, new AnalyticsFilterRequest(), null);
        Assert.Equal(3, fan.Metrics.QueryEvents);
        Assert.Equal(3, fan.Metrics.ChecksWithExpansion);
        Assert.Equal(4, fan.Metrics.SuccessfulChecks);
        Assert.Equal(4, fan.Metrics.EligibleChecks);
        Assert.Equal(2, fan.Metrics.DistinctQueries);
        Assert.Equal(1, fan.Metrics.QueriesPerExpandedCheck.Value);
        Assert.Equal(0.75, fan.Metrics.QueriesPerSuccessfulCheck.Value);
        Assert.Equal(1, fan.Metrics.AssociatedBrandMentionRate.Value);
        Assert.Equal(1, fan.Metrics.UnknownChecks);
        Assert.Equal(0.75, fan.Metrics.ExposedQueryRate.Value);
        Assert.Equal(0, fan.Metrics.PendingDetailedChecks);
        var queries = await analytics.FanOutQueriesAsync(tenant, brand.SiteId,
            new AnalyticsFilterRequest(), fan.Meta.SnapshotToken, null, 1);
        Assert.Single(queries.Items);
        Assert.Equal("tool comparison", queries.Items[0].Query);
        Assert.Equal(2, queries.Items[0].CheckCount);
        Assert.Equal(1, queries.Items[0].PromptCount);
        Assert.NotNull(queries.NextCursor);
        var query2 = await analytics.FanOutQueriesAsync(tenant, brand.SiteId,
            new AnalyticsFilterRequest(), fan.Meta.SnapshotToken, queries.NextCursor, 1);
        Assert.Single(query2.Items);
        Assert.NotEqual(queries.Items[0].QueryHash, query2.Items[0].QueryHash);
        var detail = (await analytics.FanOutQueryDetailAsync(tenant, brand.SiteId,
            queries.Items[0].QueryHash, new AnalyticsFilterRequest(), fan.Meta.SnapshotToken))!;
        Assert.Equal(2, detail.Prompts[0].CheckCount);
        Assert.Contains(detail.Terms, t => t.Text == "COMPARISON" && t.Relation == "added");
        var evidence = await analytics.FanOutEvidenceAsync(tenant, brand.SiteId,
            new AnalyticsFilterRequest(), fan.Meta.SnapshotToken, null, 50,
            queries.Items[0].QueryHash, null);
        Assert.Equal(new[] { c1, c2 }.Order(), evidence.Items.Select(i => i.CheckId).Order());
        var invalidCursor = await Assert.ThrowsAsync<CollectorStoreException>(() =>
            analytics.FanOutQueriesAsync(tenant, brand.SiteId,
                new AnalyticsFilterRequest(Engines: ["perplexity"]), fan.Meta.SnapshotToken,
                queries.NextCursor, 1));
        Assert.Equal("INVALID_FILTER", invalidCursor.Code);
        var unsupported = await Assert.ThrowsAsync<CollectorStoreException>(() =>
            analytics.FanOutAsync(tenant, brand.SiteId,
                new AnalyticsFilterRequest(AggregationMode: "latest_observation"), null));
        Assert.Equal("INVALID_FILTER", unsupported.Code);

        var citation = await analytics.CitationsAsync(tenant, brand.SiteId,
            new AnalyticsFilterRequest(), fan.Meta.SnapshotToken);
        Assert.Equal(4, citation.ExternalCitationEvents);
        Assert.Equal(0.5, citation.OwnCitationRate.Value);
        Assert.Equal(1, citation.GoogleInternalEvents);
        var urls = await analytics.CitationUrlsAsync(tenant, brand.SiteId,
            new AnalyticsFilterRequest(), fan.Meta.SnapshotToken, null, 50);
        Assert.Equal(3, urls.Items.Count);
        Assert.Equal(2, urls.Items.Single(u => u.Domain == "a.test").CitationEvents);
        Assert.Equal(0.5, urls.Items.Single(u => u.Domain == "a.test").Share.Value);
        var domains = await analytics.CitationDomainsAsync(tenant, brand.SiteId,
            new AnalyticsFilterRequest(), fan.Meta.SnapshotToken, null, 50);
        Assert.Equal(2, domains.Items.Single(d => d.Domain == "a.test").CheckCount);
        var ownUrl = urls.Items.Single(u => u.Domain == "a.test");
        var urlDetail = (await analytics.CitationUrlDetailAsync(tenant, brand.SiteId, ownUrl.UrlHash,
            new AnalyticsFilterRequest(), fan.Meta.SnapshotToken))!;
        Assert.Equal([1, 2], urlDetail.Evidence.Single(e => e.CheckId == c1).Positions);
        var google = await analytics.GoogleInternalAsync(tenant, brand.SiteId,
            new AnalyticsFilterRequest(), fan.Meta.SnapshotToken, null, 50);
        Assert.Single(google.Items);
        Assert.Equal("best tools", google.Items[0].Query);
        var gaps = await analytics.CompetitorCitationGapsAsync(tenant, brand.SiteId,
            new AnalyticsFilterRequest(), fan.Meta.SnapshotToken, null, 50);
        Assert.Empty(gaps.Items);
        var wrongTenant = await Assert.ThrowsAsync<CollectorStoreException>(() =>
            analytics.CitationsAsync(Guid.NewGuid(), brand.SiteId, new AnalyticsFilterRequest(), null));
        Assert.Equal(404, wrongTenant.StatusCode);
        var wrongTenantWithToken = await Assert.ThrowsAsync<CollectorStoreException>(() =>
            analytics.CitationsAsync(Guid.NewGuid(), brand.SiteId,
                new AnalyticsFilterRequest(), fan.Meta.SnapshotToken));
        Assert.Equal("RESOURCE_NOT_FOUND", wrongTenantWithToken.Code);
        Assert.Equal(404, wrongTenantWithToken.StatusCode);
        Assert.Null(await analytics.CitationUrlDetailAsync(tenant, brand.SiteId, new string('a', 64),
            new AnalyticsFilterRequest(), fan.Meta.SnapshotToken));
        Assert.Equal("unknown", (await analytics.CheckDetailAsync(tenant, c4, fan.Meta.SnapshotToken))!.QueryAvailability);

        var failed = await SeedCheck(connectionString, tenant, brand.SiteId, run, target, p1.PromptId, 3,
            null, [], "unknown", [], "failed");
        var noAnswer = await SeedCheck(connectionString, tenant, brand.SiteId, run, target, p2.PromptId, 3,
            "", [], "empty", []);
        var after = await analytics.FanOutAsync(tenant, brand.SiteId, new AnalyticsFilterRequest(), null);
        Assert.Equal(5, after.Metrics.SuccessfulChecks);
        Assert.Equal(4, after.Metrics.EligibleChecks);
        Assert.Equal(0.6, after.Metrics.QueriesPerSuccessfulCheck.Value);
        Assert.Equal(3, after.Metrics.QueryEvents);
        // A third token evicts the first token's in-memory facts; its database
        // snapshot must still retain the original watermarks and cursor page.
        _ = await analytics.FanOutAsync(tenant, brand.SiteId, new AnalyticsFilterRequest(), null);
        Assert.Equal(4, (await analytics.FanOutAsync(tenant, brand.SiteId,
            new AnalyticsFilterRequest(), fan.Meta.SnapshotToken)).Metrics.SuccessfulChecks);
        var oldNextPage = await analytics.FanOutQueriesAsync(tenant, brand.SiteId,
            new AnalyticsFilterRequest(), fan.Meta.SnapshotToken, queries.NextCursor, 1);
        Assert.Equal(query2.Items[0].QueryHash, Assert.Single(oldNextPage.Items).QueryHash);
        var p3 = await management.CreatePromptAsync(tenant, brand.SiteId, new PromptWrite("competitor only"));
        var c7 = await SeedCheck(connectionString, tenant, brand.SiteId, run, target, p3.PromptId, 1,
            "Beta", [], "empty", [("https://b.test/new", "Beta product")]);
        await ProcessUntilAsync(analyzer, async () =>
            (await analyzer.GetCheckAnalysisAsync(tenant, c7))?.Status == "completed");
        var gapAfter = await analytics.CompetitorCitationGapsAsync(tenant, brand.SiteId,
            new AnalyticsFilterRequest(), null, null, 50);
        Assert.Single(gapAfter.Items);
        Assert.Equal(p3.PromptId, gapAfter.Items[0].PromptId);
        Assert.Contains(c7, gapAfter.Items[0].CompetitorCheckIds);
        var prior = await SeedCheck(connectionString, tenant, brand.SiteId, run, target, p1.PromptId, 4,
            "Acme", [], "unknown", [("https://a.test/x", "Old title"),
                ("https://gone.test/z", "Gone title")], captured: DateTimeOffset.UtcNow.AddDays(-3));
        await ProcessUntilAsync(analyzer, async () =>
            (await analyzer.GetCheckAnalysisAsync(tenant, prior))?.Status == "completed");
        var changes = await analytics.CitationChangesAsync(tenant, brand.SiteId,
            new AnalyticsFilterRequest(From: DateTimeOffset.UtcNow.AddDays(-2),
                To: DateTimeOffset.UtcNow.AddHours(1)), null, 1, 0.5);
        Assert.Contains(changes.Changes, c => c.Kind == "url" && c.Key == "https://a.test/x"
            && c.Change == "title_changed" && c.PreviousTitle == "Old title" && c.CurrentTitle == "Own title");
        Assert.Contains(changes.Changes, c => c.Kind == "domain" && c.Key == "gone.test"
            && c.Change == "disappeared" && c.CurrentEvents == 0);
        Assert.Contains(changes.Changes, c => c.Kind == "url" && c.Change == "new"
            && c.PreviousEvents == 0 && c.RelativeChange is null);
        Assert.NotEqual(Guid.Empty, failed);
        Assert.NotEqual(Guid.Empty, noAnswer);
        Assert.NotEqual(Guid.Empty, c3);
    }

    [Fact]
    public async Task CitationChanges_EvidencePaginatesBothWindowsWithFrozenFilterAndWatermarks()
    {
        var connectionString = Environment.GetEnvironmentVariable("JOGEOAI_STAGE3_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        Assert.Equal("jogeoai_stage3_verified_20260927", new MySqlConnectionStringBuilder(connectionString).Database);
        var database = new MySqlDatabase(connectionString);
        var management = new ManagementStore(database);
        var analyzer = new AnalysisStore(database);
        var analytics = new AnalyticsQueryStore(database, analyzer);
        var tenant = Guid.NewGuid();
        await Execute(connectionString, """
            INSERT INTO tenants (id, name, status, created_at, updated_at)
            VALUES (UUID_TO_BIN(@tenant), 'change evidence', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
            """, ("@tenant", tenant.ToString()));
        var brand = await management.CreateBrandAsync(tenant,
            new BrandWrite("Change Evidence", $"{Guid.NewGuid():N}.brand.test", Description: "测试品牌提供设备与服务。"));
        var tracked = await management.CreatePromptAsync(tenant, brand.SiteId,
            new PromptWrite("tracked reference"));
        var excluded = await management.CreatePromptAsync(tenant, brand.SiteId,
            new PromptWrite("excluded reference"));
        var run = Guid.NewGuid();
        var target = Guid.NewGuid();
        await SeedRun(connectionString, tenant, brand.SiteId, run, target);
        var now = DateTimeOffset.UtcNow;
        const string url = "https://changes.test/item?id=1";
        var priorIds = new List<Guid>();
        for (var sample = 1; sample <= 101; sample++)
            priorIds.Add(await SeedCheck(connectionString, tenant, brand.SiteId, run, target,
                tracked.PromptId, sample, "", [], "empty", [(url, "Before")],
                captured: now.AddDays(-3)));
        var currentIds = new List<Guid>();
        for (var sample = 102; sample <= 103; sample++)
            currentIds.Add(await SeedCheck(connectionString, tenant, brand.SiteId, run, target,
                tracked.PromptId, sample, "", [], "empty", [(url, "After")],
                captured: now.AddMinutes(-30)));
        var excludedId = await SeedCheck(connectionString, tenant, brand.SiteId, run, target,
            excluded.PromptId, 1, "", [], "empty", [(url, "Excluded")],
            captured: now.AddDays(-3));
        await ProcessUntilAsync(analyzer, async () => Assert.IsType<long>(await Scalar(connectionString, """
            SELECT COUNT(*) FROM analysis_check_work
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site)
              AND status='completed';
            """, ("@tenant", tenant.ToString()), ("@site", brand.SiteId.ToString()))) == 104);

        var filter = new AnalyticsFilterRequest(From: now.AddDays(-2), To: now.AddHours(1),
            PromptIds: [tracked.PromptId]);
        var changes = await analytics.CitationChangesAsync(tenant, brand.SiteId, filter, null, 1, 0.5);
        var row = Assert.Single(changes.Changes, c => c.Kind == "url" && c.Key == url);
        Assert.Equal(2, row.CurrentEvents);
        Assert.Equal(101, row.PreviousEvents);
        Assert.Equal(100, row.CheckIds.Count);
        Assert.True(row.CheckIdsTruncated);
        Assert.StartsWith($"/api/v1/sites/{brand.SiteId}/analytics/citations/evidence?urlHash=",
            row.CurrentEvidencePath);
        Assert.StartsWith($"/api/v1/sites/{brand.SiteId}/analytics/citations/evidence?urlHash=",
            row.PreviousEvidencePath);
        var currentToken = EvidenceParameter(row.CurrentEvidencePath, "snapshotToken");
        var previousToken = EvidenceParameter(row.PreviousEvidencePath, "snapshotToken");
        var urlHash = EvidenceParameter(row.CurrentEvidencePath, "urlHash");
        Assert.Equal(changes.Meta.SnapshotToken, currentToken);
        Assert.NotEqual(currentToken, previousToken);
        Assert.Equal(urlHash, EvidenceParameter(row.PreviousEvidencePath, "urlHash"));
        var domainRow = Assert.Single(changes.Changes,
            c => c.Kind == "domain" && c.Key == "changes.test");
        Assert.True(domainRow.CheckIdsTruncated);
        Assert.Equal("changes.test", EvidenceParameter(domainRow.CurrentEvidencePath, "domain"));
        Assert.Equal("changes.test", EvidenceParameter(domainRow.PreviousEvidencePath, "domain"));
        Assert.Equal(currentToken, EvidenceParameter(domainRow.CurrentEvidencePath, "snapshotToken"));
        Assert.Equal(previousToken, EvidenceParameter(domainRow.PreviousEvidencePath, "snapshotToken"));
        var firstChangePage = await analytics.CitationChangesAsync(tenant, brand.SiteId,
            new AnalyticsFilterRequest(), currentToken, 1, 0.5, null, 1);
        Assert.Equal("domain", Assert.Single(firstChangePage.Changes).Kind);
        Assert.NotNull(firstChangePage.NextCursor);
        var secondChangePage = await analytics.CitationChangesAsync(tenant, brand.SiteId,
            new AnalyticsFilterRequest(), currentToken, 1, 0.5, firstChangePage.NextCursor, 1);
        Assert.Equal("url", Assert.Single(secondChangePage.Changes).Kind);
        Assert.Null(secondChangePage.NextCursor);
        Assert.Equal("INVALID_CURSOR", (await Assert.ThrowsAsync<CollectorStoreException>(() =>
            analytics.CitationChangesAsync(tenant, brand.SiteId,
                new AnalyticsFilterRequest(), currentToken, 1, 0.25, firstChangePage.NextCursor, 1))).Code);
        Assert.Equal("INVALID_LIMIT", (await Assert.ThrowsAsync<CollectorStoreException>(() =>
            analytics.CitationChangesAsync(tenant, brand.SiteId,
                new AnalyticsFilterRequest(), currentToken, 1, 0.5, null, 101))).Code);
        Assert.Equal(1, Assert.IsType<long>(await Scalar(connectionString, """
            SELECT COUNT(*) FROM analytics_snapshots p JOIN analytics_snapshots c
            WHERE p.token_hash=SHA2(@previous, 256) AND c.token_hash=SHA2(@current, 256)
              AND p.tenant_id=c.tenant_id AND p.site_id=c.site_id AND p.release_id=c.release_id
              AND p.subject_config_version=c.subject_config_version
              AND p.current_subject_config_version=c.current_subject_config_version
              AND p.arrival_watermark=c.arrival_watermark
              AND p.analysis_watermark=c.analysis_watermark
              AND p.as_of=c.as_of AND p.expires_at=c.expires_at
              AND p.filter_hash<>c.filter_hash;
            """, ("@previous", previousToken), ("@current", currentToken))));

        var previousPage = await analytics.CitationEvidenceAsync(tenant, brand.SiteId,
            new AnalyticsFilterRequest(), previousToken, null, 100, urlHash, null, null);
        Assert.Equal(100, previousPage.Items.Count);
        Assert.NotNull(previousPage.NextCursor);
        var previousLastPage = await analytics.CitationEvidenceAsync(tenant, brand.SiteId,
            new AnalyticsFilterRequest(), previousToken, previousPage.NextCursor, 100, urlHash, null, null);
        Assert.Single(previousLastPage.Items);
        Assert.Null(previousLastPage.NextCursor);
        Assert.Equal(priorIds.Order(), previousPage.Items.Concat(previousLastPage.Items)
            .Select(e => e.CheckId).Order());
        var currentPage = await analytics.CitationEvidenceAsync(tenant, brand.SiteId,
            new AnalyticsFilterRequest(), currentToken, null, 100, urlHash, null, null);
        Assert.Equal(currentIds.Order(), currentPage.Items.Select(e => e.CheckId).Order());
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            analytics.CitationChangesAsync(tenant, brand.SiteId,
                new AnalyticsFilterRequest(), currentToken, 1, 0.5)));
        Assert.All(concurrent, view =>
        {
            var change = Assert.Single(view.Changes, c => c.Kind == "url" && c.Key == url);
            Assert.Equal(2, change.CurrentEvents);
            Assert.Equal(101, change.PreviousEvents);
        });
        Assert.Equal(2, (await analytics.CitationsAsync(tenant, brand.SiteId,
            new AnalyticsFilterRequest(), currentToken)).ExternalCitationEvents);
        Assert.NotNull(await analytics.CheckDetailAsync(tenant, priorIds[0], previousToken));
        Assert.NotNull(await analytics.CheckDetailAsync(tenant, currentIds[0], currentToken));
        Assert.Equal(404, (await Assert.ThrowsAsync<CollectorStoreException>(() =>
            analytics.CheckDetailAsync(tenant, currentIds[0], previousToken))).StatusCode);
        Assert.Equal(404, (await Assert.ThrowsAsync<CollectorStoreException>(() =>
            analytics.CheckDetailAsync(tenant, excludedId, previousToken))).StatusCode);
        Assert.Equal("INVALID_FILTER", (await Assert.ThrowsAsync<CollectorStoreException>(() =>
            analytics.CitationEvidenceAsync(tenant, brand.SiteId,
                new AnalyticsFilterRequest(PromptIds: [excluded.PromptId]), previousToken,
                null, 100, urlHash, null, null))).Code);
        Assert.Equal(404, (await Assert.ThrowsAsync<CollectorStoreException>(() =>
            analytics.CitationEvidenceAsync(Guid.NewGuid(), brand.SiteId,
                new AnalyticsFilterRequest(), previousToken, null, 100, urlHash, null, null))).StatusCode);

        await Execute(connectionString, """
            UPDATE analytics_snapshots SET expires_at=UTC_TIMESTAMP(6) - INTERVAL 1 SECOND
            WHERE token_hash=SHA2(@token, 256);
            """, ("@token", previousToken));
        var freshReader = new AnalyticsQueryStore(database, analyzer);
        Assert.Equal("SNAPSHOT_EXPIRED", (await Assert.ThrowsAsync<CollectorStoreException>(() =>
            freshReader.CitationEvidenceAsync(tenant, brand.SiteId,
                new AnalyticsFilterRequest(), previousToken, null, 100, urlHash, null, null))).Code);
    }

    [Fact]
    public async Task Citations_DistinguishesOwnCheckRateFromOwnEventShare()
    {
        var connectionString = Environment.GetEnvironmentVariable("JOGEOAI_STAGE3_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        Assert.Equal("jogeoai_stage3_verified_20260927", new MySqlConnectionStringBuilder(connectionString).Database);
        var database = new MySqlDatabase(connectionString);
        var management = new ManagementStore(database);
        var analyzer = new AnalysisStore(database);
        var analytics = new AnalyticsQueryStore(database, analyzer);
        var tenant = Guid.NewGuid();
        await Execute(connectionString, """
            INSERT INTO tenants (id, name, status, created_at, updated_at)
            VALUES (UUID_TO_BIN(@tenant), 'own rate', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
            """, ("@tenant", tenant.ToString()));
        var brand = await management.CreateBrandAsync(tenant,
            new BrandWrite("Own Rate", $"{Guid.NewGuid():N}.brand.test", AdditionalDomains: ["own-rate.test"], Description: "测试品牌提供设备与服务。"));
        var prompt = await management.CreatePromptAsync(tenant, brand.SiteId, new PromptWrite("compare sources"));
        var run = Guid.NewGuid();
        var target = Guid.NewGuid();
        await SeedRun(connectionString, tenant, brand.SiteId, run, target);
        await SeedCheck(connectionString, tenant, brand.SiteId, run, target, prompt.PromptId, 1,
            "Own Rate", [], "empty", [("https://own-rate.test/one", "Own one"),
                ("https://own-rate.test/two", "Own two")]);
        await SeedCheck(connectionString, tenant, brand.SiteId, run, target, prompt.PromptId, 2,
            "Another answer", [], "empty", [("https://outside.test/three", "Other")]);
        await ProcessUntilAsync(analyzer, async () => Assert.IsType<long>(await Scalar(connectionString, """
            SELECT COUNT(*) FROM analysis_check_work
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site)
              AND status='completed';
            """, ("@tenant", tenant.ToString()), ("@site", brand.SiteId.ToString()))) == 2);
        var citations = await analytics.CitationsAsync(tenant, brand.SiteId,
            new AnalyticsFilterRequest(), null);
        Assert.Equal(3, citations.ExternalCitationEvents);
        Assert.Equal(2, citations.SuccessfulChecks);
        Assert.Equal(2, citations.EligibleChecks);
        Assert.Equal(new MetricRatio(1, 2, 0.5), citations.OwnCitationRate);
        Assert.Equal(new MetricRatio(2, 3, 2d / 3), citations.OwnEventShare);
        var overview = await analytics.OverviewAsync(tenant, brand.SiteId,
            new AnalyticsFilterRequest(), citations.Meta.SnapshotToken);
        Assert.Equal(overview.Metrics.OwnCitationRate, citations.OwnCitationRate);
    }

    [Fact]
    public async Task CitationDomainDetail_ExposesEveryUrlThroughDomainBoundPagination()
    {
        var connectionString = Environment.GetEnvironmentVariable("JOGEOAI_STAGE3_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        Assert.Equal("jogeoai_stage3_verified_20260927", new MySqlConnectionStringBuilder(connectionString).Database);
        var database = new MySqlDatabase(connectionString);
        var management = new ManagementStore(database);
        var analyzer = new AnalysisStore(database);
        var analytics = new AnalyticsQueryStore(database, analyzer);
        var tenant = Guid.NewGuid();
        await Execute(connectionString, """
            INSERT INTO tenants (id, name, status, created_at, updated_at)
            VALUES (UUID_TO_BIN(@tenant), 'domain URL pagination', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
            """, ("@tenant", tenant.ToString()));
        var brand = await management.CreateBrandAsync(tenant,
            new BrandWrite("Domain Pagination", $"{Guid.NewGuid():N}.brand.test", Description: "测试品牌提供设备与服务。"));
        var prompt = await management.CreatePromptAsync(tenant, brand.SiteId,
            new PromptWrite("which pages are cited"));
        var run = Guid.NewGuid();
        var target = Guid.NewGuid();
        await SeedRun(connectionString, tenant, brand.SiteId, run, target);
        var sourceUrls = Enumerable.Range(1, 101)
            .Select(index => (Url: $"https://many.test/item/{index}", Title: (string?)$"Item {index}"))
            .Append((Url: "https://outside.test/item", Title: (string?)"Outside"))
            .ToArray();
        var check = await SeedCheck(connectionString, tenant, brand.SiteId, run, target,
            prompt.PromptId, 1, "Domain Pagination answer", [], "empty", sourceUrls);
        await ProcessUntilAsync(analyzer, async () =>
            (await analyzer.GetCheckAnalysisAsync(tenant, check))?.Status == "completed");

        var detail = (await analytics.CitationDomainDetailAsync(tenant, brand.SiteId,
            "many.test", new AnalyticsFilterRequest(), null))!;
        Assert.Equal(101, detail.Domain.CitationEvents);
        Assert.Equal(100, detail.Urls.Count);
        Assert.True(detail.HasMoreUrls);
        Assert.Equal("many.test", EvidenceParameter(detail.UrlListPath, "domain"));
        Assert.Equal(detail.Meta.SnapshotToken, EvidenceParameter(detail.UrlListPath, "snapshotToken"));

        var hashes = new List<string>();
        string? cursor = null;
        var pageSizes = new List<int>();
        for (var pageNumber = 0; pageNumber < 3; pageNumber++)
        {
            var page = await analytics.CitationUrlsAsync(tenant, brand.SiteId,
                new AnalyticsFilterRequest(), detail.Meta.SnapshotToken, cursor, 50, "many.test");
            pageSizes.Add(page.Items.Count);
            Assert.All(page.Items, item =>
            {
                Assert.Equal("many.test", item.Domain);
                Assert.Equal(102, item.Share.Denominator);
            });
            hashes.AddRange(page.Items.Select(item => item.UrlHash));
            cursor = page.NextCursor;
        }
        Assert.Equal([50, 50, 1], pageSizes);
        Assert.Null(cursor);
        Assert.Equal(101, hashes.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(detail.Urls.Select(item => item.UrlHash), hashes.Take(100));
        var allHashes = new List<string>();
        cursor = null;
        for (var pageNumber = 0; pageNumber < 3; pageNumber++)
        {
            var page = await analytics.CitationUrlsAsync(tenant, brand.SiteId,
                new AnalyticsFilterRequest(), detail.Meta.SnapshotToken, cursor, 50);
            allHashes.AddRange(page.Items.Select(item => item.UrlHash));
            cursor = page.NextCursor;
        }
        Assert.Null(cursor);
        Assert.Equal(102, allHashes.Distinct(StringComparer.Ordinal).Count());
        var firstPage = await analytics.CitationUrlsAsync(tenant, brand.SiteId,
            new AnalyticsFilterRequest(), detail.Meta.SnapshotToken, null, 50, "many.test");
        Assert.Equal("INVALID_CURSOR", (await Assert.ThrowsAsync<CollectorStoreException>(() =>
            analytics.CitationUrlsAsync(tenant, brand.SiteId,
                new AnalyticsFilterRequest(), detail.Meta.SnapshotToken,
                firstPage.NextCursor, 50, "outside.test"))).Code);
    }

    private static string EvidenceParameter(string path, string name)
    {
        var query = new Uri("http://localhost" + path).Query.TrimStart('?');
        var pair = query.Split('&').Single(part => part.StartsWith(name + '=', StringComparison.Ordinal));
        return Uri.UnescapeDataString(pair[(name.Length + 1)..]);
    }

    private static async Task SeedRun(string connectionString, Guid tenant, Guid site, Guid run, Guid target)
        => await Execute(connectionString, """
            INSERT INTO visibility_runs (id, tenant_id, site_id, status, idempotency_key,
                requested_at, total_checks, succeeded_checks, created_at, updated_at)
            VALUES (UUID_TO_BIN(@run), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), 'succeeded', @key,
                UTC_TIMESTAMP(6), 6, 5, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
            INSERT INTO run_engines (id, run_id, engine, provider, target_key, capture_mode,
                country, locale, created_at)
            VALUES (UUID_TO_BIN(@target), UUID_TO_BIN(@run), 'chatgpt', 'cloro', @targetKey,
                'scraped', 'US', 'en-US', UTC_TIMESTAMP(6));
            """, ("@run", run.ToString()), ("@tenant", tenant.ToString()), ("@site", site.ToString()),
            ("@key", Guid.NewGuid().ToString()), ("@target", target.ToString()),
            ("@targetKey", new string('b', 64)));

    private static async Task<Guid> SeedCheck(string connectionString, Guid tenant, Guid site,
        Guid run, Guid target, Guid prompt, int sample, string? answer, IReadOnlyList<string> queries,
        string availability, IReadOnlyList<(string Url, string? Title)> citations, string status = "succeeded",
        DateTimeOffset? captured = null)
    {
        var id = Guid.NewGuid();
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var insert = new MySqlCommand("""
            INSERT INTO visibility_checks (id, tenant_id, site_id, run_id, prompt_id, engine,
                provider, status, prompt_snapshot, answer, answer_present, search_queries,
                query_availability, captured_at, capture_mode, evidence_class, country, locale,
                run_target_id, sample_index, prompt_version_id, prompt_tags_snapshot,
                branded_status_snapshot, subject_config_version, created_at, updated_at)
            SELECT UUID_TO_BIN(@check), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), UUID_TO_BIN(@run),
                   p.id, 'chatgpt', 'cloro', @status, p.content, @answer, @answerPresent,
                   @queries, @availability, COALESCE(@captured, UTC_TIMESTAMP(6)), 'scraped', 'observed_surface',
                   'US', 'en-US', UUID_TO_BIN(@target), @sample, p.current_version_id,
                   JSON_ARRAY(), 'unbranded', b.subject_config_version, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)
            FROM prompts p JOIN site_brand_settings b ON b.site_id = p.site_id
            WHERE p.id = UUID_TO_BIN(@prompt);
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("@check", id.ToString());
            insert.Parameters.AddWithValue("@tenant", tenant.ToString());
            insert.Parameters.AddWithValue("@site", site.ToString());
            insert.Parameters.AddWithValue("@run", run.ToString());
            insert.Parameters.AddWithValue("@status", status);
            insert.Parameters.AddWithValue("@answer", answer);
            insert.Parameters.AddWithValue("@answerPresent", !string.IsNullOrWhiteSpace(answer));
            insert.Parameters.AddWithValue("@queries", JsonSerializer.Serialize(queries));
            insert.Parameters.AddWithValue("@availability", availability);
            insert.Parameters.AddWithValue("@captured", captured?.UtcDateTime);
            insert.Parameters.AddWithValue("@target", target.ToString());
            insert.Parameters.AddWithValue("@sample", sample);
            insert.Parameters.AddWithValue("@prompt", prompt.ToString());
            await insert.ExecuteNonQueryAsync();
        }
        for (var i = 0; i < citations.Count; i++)
        {
            await using var insert = new MySqlCommand("""
                INSERT INTO citations (id, check_id, normalized_url, title, source_domain, position, created_at)
                VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@check), @url, @title, @domain, @position, UTC_TIMESTAMP(6));
                """, connection, transaction);
            insert.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
            insert.Parameters.AddWithValue("@check", id.ToString());
            insert.Parameters.AddWithValue("@url", citations[i].Url);
            insert.Parameters.AddWithValue("@title", citations[i].Title);
            insert.Parameters.AddWithValue("@domain", new Uri(citations[i].Url).Host);
            insert.Parameters.AddWithValue("@position", i + 1);
            await insert.ExecuteNonQueryAsync();
        }
        await AnalysisEnqueue.QueueAsync(connection, transaction, id, CancellationToken.None);
        await transaction.CommitAsync();
        return id;
    }

    private static async Task ProcessUntilAsync(AnalysisStore analyzer, Func<Task<bool>> completed)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (await completed()) return;
            await analyzer.ProcessPendingAsync("stage3-test", 100);
        }
        Assert.True(await completed(), "第三阶段事实未在限定批次内发布。");
    }

    private static async Task Execute(string connectionString, string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> Scalar(string connectionString, string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return await command.ExecuteScalarAsync();
    }
}
