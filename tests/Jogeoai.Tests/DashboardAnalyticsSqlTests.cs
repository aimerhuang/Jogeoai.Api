using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using MySqlConnector;

namespace Jogeoai.Tests;

/// <summary>Dashboard SQL tests run against an isolated, fully migrated database; never the business database.</summary>
public sealed class DashboardMySqlFactAttribute : FactAttribute
{
    public DashboardMySqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JOGEOAI_DASHBOARD_TEST_CONNECTION")))
            Skip = "需提供已执行全部迁移的独立测试库 JOGEOAI_DASHBOARD_TEST_CONNECTION；不能使用业务库。";
    }
}

public sealed class DashboardAnalyticsSqlTests
{
    [DashboardMySqlFact]
    public async Task MonitoringSummaryAndBrandDashboard_PreserveDenominatorsAndQualityReasons()
    {
        var connectionString = ConnectionString();
        var database = new MySqlDatabase(connectionString);
        var management = new ManagementStore(database);
        var analyzer = new AnalysisStore(database);
        var analytics = new AnalyticsQueryStore(database, analyzer);
        var fixture = await CreateFixtureAsync(connectionString, management, analyzer,
            includeCompetitor: true, includeTag: true, includeGroup: true);

        try
        {
            var summary = await analytics.MonitoringSummaryAsync(fixture.Tenant, fixture.Site,
                new AnalyticsFilterRequest(), null);
        Assert.Equal(5, summary.TotalChecks);
        Assert.Equal(5, summary.TodayChecks);
        Assert.Equal(3, summary.ActiveModelCount);
        Assert.Equal(2, summary.BrandVisibility.Numerator);
        Assert.Equal(2, summary.BrandVisibility.Denominator);
        Assert.Equal(1, summary.Quality.NoAnswerChecks);
        Assert.Equal(1, summary.Quality.FailedChecks);
        Assert.Equal(5, summary.Quality.ModelVersionUnavailableChecks);
        Assert.Equal("collectionElapsedMs", summary.Latency.MetricName);
        Assert.Equal(2, summary.Latency.SampleCount);
        Assert.Equal("previous_denominator_zero_or_not_ready", summary.Comparison.UnavailableReason);
            Assert.Equal(1, summary.Meta.MissingCapturedAtChecks);
            var dashboard = await analytics.BrandDashboardAsync(fixture.Tenant, fixture.Site,
            new AnalyticsFilterRequest(), null);
        Assert.Equal(2, dashboard.Metrics.BrandMentionRate.Numerator);
        Assert.Equal(2, dashboard.Metrics.BrandMentionRate.Denominator);
        Assert.Equal(2, dashboard.Metrics.ShareOfVoice.Numerator);
        Assert.Equal(4, dashboard.Metrics.ShareOfVoice.Denominator);
        Assert.Equal(2, dashboard.Metrics.AverageRanking.Denominator);
        // exposure-v2：提及 2/2，名次分 (1 + 1/2) / 2，声量按次数 2/4，自有引用 1/2 → 40 + 22.5 + 10 + 5 = 77.5。
        Assert.Equal(77.5, dashboard.Metrics.Exposure.Value!.Value, 6);
        Assert.Equal("exposure-v2", dashboard.Metrics.Exposure.ScoreVersion);
        Assert.Equal(MetricRatio.Of(2, 4), dashboard.Metrics.Exposure.ShareOfVoice);
        // groups[] is keyed by the prompt's current prompt group, not by capture-time tags (prd B7).
        var group = Assert.Single(dashboard.Groups);
        Assert.Equal(fixture.Group, group.GroupId);
        Assert.Equal("priority group", group.Name);
        Assert.Equal([fixture.Prompt], group.PromptIds);
        Assert.Equal(1, dashboard.Quality.GroupCount);
        Assert.Equal(1, dashboard.Quality.TagCount);
        Assert.Null(dashboard.Quality.GroupUnavailableReason);
        Assert.Equal("en-US", (await analytics.CheckDetailAsync(fixture.Tenant, fixture.Checks[0], null))!.Locale);

            // Subject trends share the dashboard snapshot and local-day split; the
            // fixture captures every Check on one day, so each series has one point
            // whose ratios equal the window values.
            Assert.Equal([fixture.Site, fixture.Competitor], dashboard.SubjectTrends.Select(t => t.SubjectId));
            Assert.True(dashboard.SubjectTrends[0].IsBrand);
            var sampledDates = dashboard.Trend.Where(p => p.SampleCount > 0).Select(p => p.Date).ToArray();
            Assert.Single(sampledDates);
            Assert.All(dashboard.SubjectTrends, t => Assert.Equal(sampledDates, t.Points.Select(p => p.Date)));
            var brandPoint = Assert.Single(dashboard.SubjectTrends[0].Points);
            Assert.Equal(dashboard.Metrics.BrandMentionRate, brandPoint.MentionRate);
            Assert.Equal(dashboard.Metrics.EligibleChecks, brandPoint.SampleCount);
            var competitorWindow = Assert.Single(dashboard.Competitors);
            var competitorPoint = Assert.Single(dashboard.SubjectTrends[1].Points);
            Assert.Equal(competitorWindow.MentionRate, competitorPoint.MentionRate);
            Assert.Equal(competitorWindow.AverageRanking, competitorPoint.AverageRanking);

        // Subject selection changes the projection context, but must not remove Checks from N.
        var selected = await analytics.BrandDashboardAsync(fixture.Tenant, fixture.Site,
            new AnalyticsFilterRequest(SubjectIds: [fixture.Competitor]), null);
        Assert.Equal(dashboard.Metrics.EligibleChecks, selected.Metrics.EligibleChecks);
            Assert.Equal(dashboard.Metrics.BrandMentionRate.Denominator,
                selected.Metrics.BrandMentionRate.Denominator);
            Assert.Equal([fixture.Site, fixture.Competitor], selected.SubjectTrends.Select(t => t.SubjectId));
            Assert.Equal(competitorPoint.MentionRate, Assert.Single(selected.SubjectTrends[1].Points).MentionRate);
        }
        finally
        {
            await CleanupFixtureAsync(connectionString, fixture);
        }
    }

    [DashboardMySqlFact]
    public async Task MonitoringModelsAndRecords_ExposeStatusesLocalesAndStableSnapshotCursor()
    {
        var connectionString = ConnectionString();
        var database = new MySqlDatabase(connectionString);
        var management = new ManagementStore(database);
        var analyzer = new AnalysisStore(database);
        var analytics = new AnalyticsQueryStore(database, analyzer);
        var fixture = await CreateFixtureAsync(connectionString, management, analyzer,
            includeCompetitor: true, includeTag: false, includeGroup: false);

        try
        {
            var models = await analytics.MonitoringModelsAsync(fixture.Tenant, fixture.Site,
            new AnalyticsFilterRequest(), null);
        Assert.Contains(models, model => model.Status == "insufficient_data");
        Assert.Contains(models, model => model.Status == "running");
        Assert.Contains(models, model => model.Status == "idle");
        Assert.Contains(models, model => model.Status == "disabled");
        Assert.All(models.Where(model => model.ModelVersion is null), model =>
            Assert.True(model.ModelVersionUnavailable));
        var filtered = await analytics.MonitoringModelsAsync(fixture.Tenant, fixture.Site,
            new AnalyticsFilterRequest(Engines: ["perplexity"]), null);
        Assert.Single(filtered);
        Assert.Equal("perplexity", filtered[0].Engine);

        var first = await analytics.MonitoringRecordsAsync(fixture.Tenant, fixture.Site,
            new AnalyticsFilterRequest(), null, null, 1);
        Assert.Single(first.Items);
        Assert.NotNull(first.NextCursor);
        Assert.Equal("/api/v1/visibility-checks/" + first.Items[0].CheckId, first.Items[0].DetailsPath);
        Assert.Equal("en-US", first.Items[0].Locale);
        var second = await analytics.MonitoringRecordsAsync(fixture.Tenant, fixture.Site,
            new AnalyticsFilterRequest(), first.Meta.SnapshotToken, first.NextCursor, 1);
        Assert.Single(second.Items);
        Assert.NotEqual(first.Items[0].CheckId, second.Items[0].CheckId);
        Assert.Equal(first.Meta.SnapshotToken, second.Meta.SnapshotToken);

        await SeedCheckAsync(connectionString, fixture.Tenant, fixture.Site, fixture.Run,
            fixture.Target, fixture.Prompt, 7, "Acme and Beta", "succeeded", 300);
        var stable = await analytics.MonitoringRecordsAsync(fixture.Tenant, fixture.Site,
            new AnalyticsFilterRequest(), first.Meta.SnapshotToken, first.NextCursor, 1);
        Assert.Equal(second.Items[0].CheckId, stable.Items[0].CheckId);

        // 总数在分页前、同一快照与筛选下计算：翻页不变，快照后入库的记录不计入（prd B3）。
        var all = await analytics.MonitoringRecordsAsync(fixture.Tenant, fixture.Site,
            new AnalyticsFilterRequest(), first.Meta.SnapshotToken, null, 50);
        Assert.Null(all.NextCursor);
        Assert.Equal(all.Items.Count, all.TotalCount);
        Assert.Equal(all.TotalCount, first.TotalCount);
        Assert.Equal(all.TotalCount, second.TotalCount);
        Assert.Equal(all.TotalCount, stable.TotalCount);
        var mentioned = await analytics.MonitoringRecordsAsync(fixture.Tenant, fixture.Site,
            new AnalyticsFilterRequest(), first.Meta.SnapshotToken, null, 1, null, "mentioned");
        Assert.Equal(all.Items.LongCount(i => i.MentionState == "mentioned"), mentioned.TotalCount);
        Assert.Equal(2, mentioned.TotalCount);
        Assert.Single(mentioned.Items);
        Assert.True(mentioned.TotalCount < all.TotalCount);
        var searched = await analytics.MonitoringRecordsAsync(fixture.Tenant, fixture.Site,
            new AnalyticsFilterRequest(), first.Meta.SnapshotToken, null, 50, "no such prompt");
        Assert.Equal(0, searched.TotalCount);
        Assert.Empty(searched.Items);

        // 单条指标：列表行与详情在同一快照下一致（prd B2），且符合单条口径（B1、D3）。
        foreach (var item in all.Items)
        {
            Assert.NotNull(item.BrandMetrics);
            var answer = await analytics.MonitoringAnswerDetailAsync(fixture.Tenant, fixture.Site, item.CheckId,
                first.Meta.SnapshotToken, null);
            Assert.NotNull(answer);
            Assert.Equal(item.BrandMetrics, answer!.BrandMetrics);
        }
        // "Acme is ahead of Beta" 引用 acme.test：第 1 名，40 + 30 + 20 × (1/2) + 10 = 90。
        var ahead = all.Items.Single(i => i.CheckId == fixture.Checks[0]);
        Assert.Equal(1, ahead.BrandMetrics!.Ranking.Value);
        Assert.Equal(90, ahead.BrandMetrics.Exposure.Value!.Value, 6);
        // "Beta leads while Acme follows" 引用 beta.test：第 2 名，40 + 30 × (1/2) + 20 × (1/2) = 65。
        var follows = all.Items.Single(i => i.CheckId == fixture.Checks[1]);
        Assert.Equal(2, follows.BrandMetrics!.Ranking.Value);
        Assert.Equal(65, follows.BrandMetrics.Exposure.Value!.Value, 6);
        Assert.Equal("NO_ANSWER", all.Items.Single(i => i.CheckId == fixture.Checks[2]).BrandMetrics!.Ranking.UnavailableReason);
        Assert.Equal("CHECK_NOT_SUCCEEDED", all.Items.Single(i => i.CheckId == fixture.Checks[3]).BrandMetrics!.Exposure.EligibilityReason);

        var detail = await analytics.CheckDetailAsync(fixture.Tenant, fixture.Checks[0], null);
        Assert.Equal("Acme is ahead of Beta", detail!.Answer);
            Assert.Single(detail.Citations);
        }
        finally
        {
            await CleanupFixtureAsync(connectionString, fixture);
        }
    }

    [DashboardMySqlFact]
    public async Task DashboardSnapshotFilterAndTenantErrors_AreExplicit()
    {
        var connectionString = ConnectionString();
        var database = new MySqlDatabase(connectionString);
        var management = new ManagementStore(database);
        var analyzer = new AnalysisStore(database);
        var analytics = new AnalyticsQueryStore(database, analyzer);
        var fixture = await CreateFixtureAsync(connectionString, management, analyzer,
            includeCompetitor: false, includeTag: false, includeGroup: false);

        try
        {
            var crossTenant = await Assert.ThrowsAsync<CollectorStoreException>(() => analytics.BrandDashboardAsync(
            Guid.NewGuid(), fixture.Site, new AnalyticsFilterRequest(), null));
        Assert.Equal("RESOURCE_NOT_FOUND", crossTenant.Code);
        var invalid = await Assert.ThrowsAsync<CollectorStoreException>(() => analytics.BrandDashboardAsync(
            fixture.Tenant, fixture.Site, new AnalyticsFilterRequest(AggregationMode: "unsupported"), null));
        Assert.Equal("INVALID_FILTER", invalid.Code);
        var expired = await Assert.ThrowsAsync<CollectorStoreException>(() => analytics.BrandDashboardAsync(
            fixture.Tenant, fixture.Site, new AnalyticsFilterRequest(), "expired-token"));
        Assert.Equal("SNAPSHOT_EXPIRED", expired.Code);

        var dashboard = await analytics.BrandDashboardAsync(fixture.Tenant, fixture.Site,
            new AnalyticsFilterRequest(), null);
        Assert.Null(dashboard.Metrics.Exposure.Value);
        Assert.Equal("NO_COMPETITOR_BASELINE", dashboard.Metrics.Exposure.EligibilityReason);
        Assert.Equal("NO_COMPETITORS_CONFIGURED", dashboard.Quality.CompetitorUnavailableReason);
        Assert.Empty(dashboard.Competitors);
            // An ungrouped prompt still yields the 「未分组」 bucket, so groups are available.
            var ungrouped = Assert.Single(dashboard.Groups);
            Assert.Null(ungrouped.GroupId);
            Assert.Equal("未分组", ungrouped.Name);
            Assert.Equal(1, dashboard.Quality.GroupCount);
            Assert.Equal(0, dashboard.Quality.TagCount);
            Assert.Null(dashboard.Quality.GroupUnavailableReason);
            // No facts in the window: no group rows and the prompt-group reason code.
            var empty = await analytics.BrandDashboardAsync(fixture.Tenant, fixture.Site,
                new AnalyticsFilterRequest(Engines: ["copilot"]), null);
            Assert.Empty(empty.Groups);
            Assert.Equal(0, empty.Quality.GroupCount);
            Assert.Equal("NO_PROMPT_GROUP_DATA", empty.Quality.GroupUnavailableReason);
        }
        finally
        {
            await CleanupFixtureAsync(connectionString, fixture);
        }
    }

    private static string ConnectionString()
    {
        var value = Environment.GetEnvironmentVariable("JOGEOAI_DASHBOARD_TEST_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(value), "未设置 JOGEOAI_DASHBOARD_TEST_CONNECTION。");
        var builder = new MySqlConnectionStringBuilder(value!);
        Assert.NotEqual("jogeoai", builder.Database);
        return value!;
    }

    private sealed record Fixture(Guid Tenant, Guid Site, Guid Run, Guid Target, Guid Prompt,
        Guid Competitor, Guid[] Checks, Guid? Group);

    private static async Task<Fixture> CreateFixtureAsync(string connectionString, ManagementStore management,
        AnalysisStore analyzer, bool includeCompetitor, bool includeTag, bool includeGroup)
    {
        var tenant = Guid.NewGuid();
        await ExecuteAsync(connectionString, """
            INSERT INTO tenants (id, name, status, created_at, updated_at)
            VALUES (UUID_TO_BIN(@tenant), 'dashboard fixture', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
            """, ("@tenant", tenant.ToString()));
        var brand = await management.CreateBrandAsync(tenant, new BrandWrite("Acme",
            "acme.test", TimeZone: "UTC", Description: "测试品牌提供设备与服务。"));
        var competitor = includeCompetitor
            ? (await management.CreateCompetitorAsync(tenant, brand.SiteId,
                new CompetitorWrite("Beta", Domains: ["beta.test"]))).CompetitorId
            : Guid.Empty;
        var tag = includeTag ? await management.CreateTagAsync(tenant, brand.SiteId, new TagWrite("priority")) : null;
        var group = includeGroup
            ? (await management.CreatePromptGroupAsync(tenant, brand.SiteId, new PromptGroupWrite("priority group"))).GroupId
            : (Guid?)null;
        var prompt = await management.CreatePromptAsync(tenant, brand.SiteId,
            new PromptWrite("best tools", TagIds: tag is null ? null : [tag.TagId], GroupId: group));
        var run = Guid.NewGuid();
        var target = Guid.NewGuid();
        await SeedRunAsync(connectionString, tenant, brand.SiteId, run, target);
        var now = DateTimeOffset.UtcNow;
        var checks = new[]
        {
            await SeedCheckAsync(connectionString, tenant, brand.SiteId, run, target, prompt.PromptId, 1,
                "Acme is ahead of Beta", "succeeded", 100, now.AddMinutes(-4)),
            await SeedCheckAsync(connectionString, tenant, brand.SiteId, run, target, prompt.PromptId, 2,
                "Beta leads while Acme follows", "succeeded", 200, now.AddMinutes(-3)),
            await SeedCheckAsync(connectionString, tenant, brand.SiteId, run, target, prompt.PromptId, 3,
                null, "succeeded", null, now.AddMinutes(-2)),
            await SeedCheckAsync(connectionString, tenant, brand.SiteId, run, target, prompt.PromptId, 4,
                null, "failed", null, now.AddMinutes(-1)),
        };
        // This pending row makes the model status running while remaining outside N.
        await SeedCheckAsync(connectionString, tenant, brand.SiteId, run, target, prompt.PromptId, 5,
            "pending Acme", "pending", null, now.AddSeconds(-10), queue: false);
        await SeedCheckAsync(connectionString, tenant, brand.SiteId, run, target, prompt.PromptId, 6,
            "missing capture", "succeeded", 150, captured: null, queue: false, missingCaptured: true);
        // Give the fixture a second model identity with fewer than the healthy
        // sample threshold so the model-state projection covers both states.
        await ExecuteAsync(connectionString, """
            UPDATE visibility_checks SET engine = 'perplexity'
            WHERE run_id = UUID_TO_BIN(@run) AND sample_index = 1;
            """, ("@run", run.ToString()));
        await ExecuteAsync(connectionString, """
            INSERT IGNORE INTO analytics_check_arrivals (check_id, tenant_id, site_id, captured_at, arrived_at)
            SELECT c.id, c.tenant_id, c.site_id, c.captured_at, UTC_TIMESTAMP(6)
            FROM visibility_checks c
            WHERE c.run_id = UUID_TO_BIN(@run) AND c.sample_index IN (5, 6);
            """, ("@run", run.ToString()));
        if (includeTag)
            await ExecuteAsync(connectionString, "UPDATE visibility_checks SET prompt_tags_snapshot = JSON_ARRAY('priority') WHERE run_id = UUID_TO_BIN(@run);",
                ("@run", run.ToString()));
        for (var attempt = 0; attempt < 5; attempt++)
            await analyzer.ProcessPendingAsync("dashboard-test", 100);
        await management.PutSiteTargetsAsync(tenant, brand.SiteId, new SiteTargetsWrite(1,
             [new SiteTargetWrite("cloro", "chatgpt", "scraped", "US", "en-US", true),
             new SiteTargetWrite("cloro", "perplexity", "scraped", "US", "en-US", true),
             new SiteTargetWrite("cloro", "copilot", "scraped", "US", "en-US", true),
             new SiteTargetWrite("cloro", "gemini", "scraped", "US", "en-US", false)]));
        return new Fixture(tenant, brand.SiteId, run, target, prompt.PromptId, competitor, checks, group);
    }

    private static async Task SeedRunAsync(string connectionString, Guid tenant, Guid site, Guid run, Guid target)
        => await ExecuteAsync(connectionString, """
            INSERT INTO visibility_runs (id, tenant_id, site_id, status, idempotency_key,
                requested_at, total_checks, succeeded_checks, created_at, updated_at)
            VALUES (UUID_TO_BIN(@run), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), 'succeeded', @key,
                UTC_TIMESTAMP(6), 5, 4, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
            INSERT INTO run_engines (id, run_id, engine, provider, target_key, capture_mode,
                country, locale, created_at)
            VALUES (UUID_TO_BIN(@target), UUID_TO_BIN(@run), 'chatgpt', 'cloro', @targetKey,
                'scraped', 'US', 'en-US', UTC_TIMESTAMP(6));
            """, ("@run", run.ToString()), ("@tenant", tenant.ToString()), ("@site", site.ToString()),
            ("@key", Guid.NewGuid().ToString()), ("@target", target.ToString()),
            ("@targetKey", new string('d', 64)));

    private static async Task<Guid> SeedCheckAsync(string connectionString, Guid tenant, Guid site,
        Guid run, Guid target, Guid prompt, int sample, string? answer, string status, int? duration,
        DateTimeOffset? captured = null, bool queue = true, bool missingCaptured = false)
    {
        var check = Guid.NewGuid();
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var insert = new MySqlCommand("""
            INSERT INTO visibility_checks (id, tenant_id, site_id, run_id, prompt_id, engine, provider,
                status, prompt_snapshot, answer, answer_present, captured_at, capture_mode, evidence_class,
                country, locale, run_target_id, sample_index, prompt_version_id, prompt_tags_snapshot,
                branded_status_snapshot, subject_config_version, model_version, duration_ms, created_at, updated_at)
            SELECT UUID_TO_BIN(@check), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), UUID_TO_BIN(@run), p.id,
                'chatgpt', 'cloro', @status, p.content, @answer, @present, @captured,
                'scraped', 'observed_surface', 'US', 'en-US', UUID_TO_BIN(@target), @sample,
                p.current_version_id, JSON_ARRAY(), 'unbranded', b.subject_config_version, NULL, @duration,
                UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)
            FROM prompts p JOIN site_brand_settings b ON b.site_id = p.site_id
            WHERE p.id = UUID_TO_BIN(@prompt);
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("@check", check.ToString());
            insert.Parameters.AddWithValue("@tenant", tenant.ToString());
            insert.Parameters.AddWithValue("@site", site.ToString());
            insert.Parameters.AddWithValue("@run", run.ToString());
            insert.Parameters.AddWithValue("@prompt", prompt.ToString());
            insert.Parameters.AddWithValue("@target", target.ToString());
            insert.Parameters.AddWithValue("@sample", sample);
            insert.Parameters.AddWithValue("@answer", answer ?? (object)DBNull.Value);
            insert.Parameters.AddWithValue("@present", !string.IsNullOrWhiteSpace(answer));
            insert.Parameters.AddWithValue("@status", status);
            insert.Parameters.AddWithValue("@captured", missingCaptured
                ? DBNull.Value : captured?.UtcDateTime ?? DateTime.UtcNow);
            insert.Parameters.AddWithValue("@duration", duration ?? (object)DBNull.Value);
            await insert.ExecuteNonQueryAsync();
        }
        await using (var citation = new MySqlCommand("""
            INSERT INTO citations (id, check_id, normalized_url, source_domain, position, created_at)
            VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@check), @url, @domain, 1, UTC_TIMESTAMP(6));
            """, connection, transaction))
        {
            citation.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
            citation.Parameters.AddWithValue("@check", check.ToString());
            citation.Parameters.AddWithValue("@url", sample == 1 ? "https://acme.test/source" : "https://beta.test/source");
            citation.Parameters.AddWithValue("@domain", sample == 1 ? "acme.test" : "beta.test");
            if (answer is not null) await citation.ExecuteNonQueryAsync();
        }
        if (queue) await AnalysisEnqueue.QueueAsync(connection, transaction, check, CancellationToken.None);
        await transaction.CommitAsync();
        return check;
    }

    private static async Task ExecuteAsync(string connectionString, string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CleanupFixtureAsync(string connectionString, Fixture fixture)
    {
        // The fixture owns a fresh tenant, so every predicate below is scoped to
        // that tenant/site. Keep the dependency order explicit: this is shared
        // jogeoai_app, and cleanup must never use a broad table truncate.
        var statements = new[]
        {
            "DELETE FROM analytics_snapshots WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);",
            "DELETE FROM analysis_jobs WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);",
            "DELETE q FROM query_term_facts q JOIN check_query_events e ON e.release_id = q.release_id AND e.check_id = q.check_id AND e.query_hash = q.query_hash JOIN check_analysis a ON a.release_id = e.release_id AND a.check_id = e.check_id WHERE a.tenant_id = UUID_TO_BIN(@tenant) AND a.site_id = UUID_TO_BIN(@site);",
            "DELETE c FROM citation_classifications c JOIN check_analysis a ON a.release_id = c.release_id AND a.check_id = c.check_id WHERE a.tenant_id = UUID_TO_BIN(@tenant) AND a.site_id = UUID_TO_BIN(@site);",
            "DELETE g FROM google_internal_events g JOIN check_analysis a ON a.release_id = g.release_id AND a.check_id = g.check_id WHERE a.tenant_id = UUID_TO_BIN(@tenant) AND a.site_id = UUID_TO_BIN(@site);",
            "DELETE c FROM citation_events c JOIN check_analysis a ON a.release_id = c.release_id AND a.check_id = c.check_id WHERE a.tenant_id = UUID_TO_BIN(@tenant) AND a.site_id = UUID_TO_BIN(@site);",
            "DELETE q FROM check_query_events q JOIN check_analysis a ON a.release_id = q.release_id AND a.check_id = q.check_id WHERE a.tenant_id = UUID_TO_BIN(@tenant) AND a.site_id = UUID_TO_BIN(@site);",
            "DELETE m FROM mention_evidence m JOIN check_analysis a ON a.release_id = m.release_id AND a.check_id = m.check_id WHERE a.tenant_id = UUID_TO_BIN(@tenant) AND a.site_id = UUID_TO_BIN(@site);",
            "DELETE p FROM check_subject_presence p JOIN check_analysis a ON a.release_id = p.release_id AND a.check_id = p.check_id WHERE a.tenant_id = UUID_TO_BIN(@tenant) AND a.site_id = UUID_TO_BIN(@site);",
            // Migration 017 completions reference check_analysis, analysis_events and releases.
            "DELETE FROM check_detailed_completions WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);",
            "UPDATE check_analysis SET completed_event_id = NULL WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);",
            "DELETE FROM check_analysis WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);",
            "DELETE e FROM analysis_events e JOIN analysis_check_work w ON w.release_id = e.release_id AND w.check_id = e.check_id WHERE w.tenant_id = UUID_TO_BIN(@tenant) AND w.site_id = UUID_TO_BIN(@site);",
            "DELETE FROM analysis_check_work WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);",
            "DELETE FROM site_analysis_state WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);",
            "DELETE FROM analysis_releases WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);",
            "DELETE FROM analytics_check_arrivals WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);",
            "DELETE FROM usage_ledger WHERE tenant_id = UUID_TO_BIN(@tenant);",
            "DELETE FROM collector_result_submissions WHERE tenant_id = UUID_TO_BIN(@tenant);",
            "DELETE FROM collector_attempts WHERE tenant_id = UUID_TO_BIN(@tenant);",
            "DELETE FROM collector_tasks WHERE tenant_id = UUID_TO_BIN(@tenant);",
            "DELETE FROM provider_tasks WHERE run_id IN (SELECT id FROM visibility_runs WHERE tenant_id = UUID_TO_BIN(@tenant));",
            "DELETE FROM citations WHERE check_id IN (SELECT id FROM visibility_checks WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site));",
            "DELETE FROM mentions WHERE check_id IN (SELECT id FROM visibility_checks WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site));",
            "DELETE FROM schedule_slots WHERE schedule_id IN (SELECT id FROM collection_schedules WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site));",
            "DELETE FROM collection_schedules WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);",
            "DELETE FROM collection_previews WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);",
            "DELETE FROM prompt_imports WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);",
            "DELETE FROM visibility_checks WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);",
            "DELETE FROM run_engines WHERE run_id IN (SELECT id FROM visibility_runs WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site));",
            "DELETE FROM visibility_runs WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);",
            "DELETE FROM outbox_events WHERE tenant_id = UUID_TO_BIN(@tenant);",
            "UPDATE prompts SET current_version_id = NULL WHERE site_id = UUID_TO_BIN(@site);",
            "DELETE FROM prompt_version_tags WHERE prompt_version_id IN (SELECT id FROM prompt_versions WHERE site_id = UUID_TO_BIN(@site));",
            "DELETE FROM prompt_versions WHERE site_id = UUID_TO_BIN(@site);",
            "DELETE FROM prompts WHERE site_id = UUID_TO_BIN(@site);",
            "DELETE FROM prompt_groups WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);",
            "DELETE FROM subject_config_revisions WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);",
            "DELETE FROM subject_terms WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);",
            "DELETE FROM competitors WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);",
            "DELETE FROM tags WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);",
            "DELETE FROM site_targets WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);",
            "DELETE FROM site_brand_settings WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);",
            "DELETE FROM onboarding_progress WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);",
            "DELETE FROM brand_research_jobs WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);",
            "DELETE FROM tenant_members WHERE tenant_id = UUID_TO_BIN(@tenant);",
            "DELETE FROM sites WHERE tenant_id = UUID_TO_BIN(@tenant) AND id = UUID_TO_BIN(@site);",
            "DELETE FROM tenants WHERE id = UUID_TO_BIN(@tenant);"
        };

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            foreach (var sql in statements)
            {
                await using var command = new MySqlCommand(sql, connection, transaction);
                command.Parameters.AddWithValue("@tenant", fixture.Tenant.ToString());
                command.Parameters.AddWithValue("@site", fixture.Site.ToString());
                await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }
}
