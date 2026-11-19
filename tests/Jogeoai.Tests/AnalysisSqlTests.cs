using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using MySqlConnector;

namespace Jogeoai.Tests;

public sealed class AnalysisSqlTests
{
    [Fact]
    public void ExtractorVersionChangeForcesFullRebuildWithoutReuse()
    {
        // 已发布版本由 mention-v1 产生：无论是否带范围都不复用旧提及事实。
        Assert.Equal("extractor_version_changed",
            AnalysisStore.EffectiveScope(true, 3, 3, DetailedAnalyticsRules.UrlV2, "mention-v1"));
        Assert.Equal("extractor_version_changed",
            AnalysisStore.EffectiveScope(false, 4, 3, DetailedAnalyticsRules.UrlV2, "mention-v1"));
        Assert.False(AnalysisStore.CanReuseOutsideScope(true, 3, 3, "detail-v2", DetailedAnalyticsRules.UrlV2, "mention-v1"));
        // URL 版本变化仍优先；版本一致时保持原有判定。
        Assert.Equal("url_normalization_version_changed",
            AnalysisStore.EffectiveScope(true, 3, 3, DetailedAnalyticsRules.UrlV1, "mention-v1"));
        Assert.Equal("site_config_changed",
            AnalysisStore.EffectiveScope(true, 4, 3, DetailedAnalyticsRules.UrlV2, MentionMatcher.Version));
        Assert.Equal("requested_scope_with_reuse",
            AnalysisStore.EffectiveScope(true, 3, 3, DetailedAnalyticsRules.UrlV2, MentionMatcher.Version));
        Assert.Equal("site", AnalysisStore.EffectiveScope(false, 3, 3, DetailedAnalyticsRules.UrlV2, MentionMatcher.Version));
        Assert.True(AnalysisStore.CanReuseOutsideScope(true, 3, 3, "detail-v2", DetailedAnalyticsRules.UrlV2, MentionMatcher.Version));
        Assert.False(AnalysisStore.CanReuseOutsideScope(false, 3, 3, "detail-v2", DetailedAnalyticsRules.UrlV2, MentionMatcher.Version));
    }

    [Fact]
    public async Task SnapshotWatermarksWaitForCaptureAndAnalysisTransactions()
    {
        var connectionString = Environment.GetEnvironmentVariable("JOGEOAI_STAGE2_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        Assert.Equal("jogeoai_stage2_verified_20260926", new MySqlConnectionStringBuilder(connectionString).Database);
        var database = new MySqlDatabase(connectionString);
        var management = new ManagementStore(database);
        var analyzer = new AnalysisStore(database);
        var analytics = new AnalyticsQueryStore(database, analyzer);
        var tenant = Guid.NewGuid();
        await Execute(connectionString, "INSERT INTO tenants (id, name, status, created_at, updated_at) VALUES (UUID_TO_BIN(@tenant), 'watermark', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));", ("@tenant", tenant.ToString()));
        var brand = await management.CreateBrandAsync(tenant, new BrandWrite("Watermark", $"{Guid.NewGuid():N}.water.test", Description: "测试品牌提供设备与服务。"));
        var prompt = await management.CreatePromptAsync(tenant, brand.SiteId, new PromptWrite("watermark prompt"));
        var run = Guid.NewGuid();
        var target = Guid.NewGuid();
        await SeedRun(connectionString, tenant, brand.SiteId, run, target);
        _ = await analytics.OverviewAsync(tenant, brand.SiteId, new AnalyticsFilterRequest(), null);
        Task<OverviewView>? waitingArrival = null;
        var check = await SeedCheck(connectionString, tenant, brand.SiteId, run, target,
            prompt.PromptId, 1, "Watermark answer", [], beforeCommit: async () =>
            {
                waitingArrival = analytics.OverviewAsync(tenant, brand.SiteId, new AnalyticsFilterRequest(), null);
                await Task.Delay(100);
                Assert.False(waitingArrival.IsCompleted);
            });
        var afterArrival = await waitingArrival!;
        Assert.Equal(1, afterArrival.Metrics.SuccessfulChecks);
        Assert.Equal(0, afterArrival.Metrics.EligibleChecks);

        var release = (await analyzer.GetCheckAnalysisAsync(tenant, check))!.ReleaseId;
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var state = new MySqlCommand("SELECT site_id FROM site_analysis_state WHERE site_id=UUID_TO_BIN(@site) FOR UPDATE;", connection, transaction))
        {
            state.Parameters.AddWithValue("@site", brand.SiteId.ToString());
            Assert.NotNull(await state.ExecuteScalarAsync());
        }
        await using var eventCommand = new MySqlCommand("""
            INSERT INTO analysis_events (release_id, check_id, completed_at)
            VALUES (UUID_TO_BIN(@release), UUID_TO_BIN(@check), UTC_TIMESTAMP(6));
            """, connection, transaction);
        eventCommand.Parameters.AddWithValue("@release", release.ToString());
        eventCommand.Parameters.AddWithValue("@check", check.ToString());
        await eventCommand.ExecuteNonQueryAsync();
        await using var complete = new MySqlCommand("""
            INSERT INTO check_analysis (release_id, check_id, tenant_id, site_id, status,
                configuration_missing, completed_event_id, completed_at)
            VALUES (UUID_TO_BIN(@release), UUID_TO_BIN(@check), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site),
                'completed', FALSE, @event, UTC_TIMESTAMP(6));
            UPDATE analysis_check_work SET status='completed' WHERE release_id=UUID_TO_BIN(@release)
                AND check_id=UUID_TO_BIN(@check);
            """, connection, transaction);
        complete.Parameters.AddWithValue("@release", release.ToString());
        complete.Parameters.AddWithValue("@check", check.ToString());
        complete.Parameters.AddWithValue("@tenant", tenant.ToString());
        complete.Parameters.AddWithValue("@site", brand.SiteId.ToString());
        complete.Parameters.AddWithValue("@event", eventCommand.LastInsertedId);
        await complete.ExecuteNonQueryAsync();
        var waitingAnalysis = analytics.OverviewAsync(tenant, brand.SiteId, new AnalyticsFilterRequest(), null);
        await Task.Delay(100);
        Assert.False(waitingAnalysis.IsCompleted);
        await transaction.CommitAsync();
        Assert.Equal(1, (await waitingAnalysis).Metrics.EligibleChecks);
        Assert.Equal(0, (await analytics.OverviewAsync(tenant, brand.SiteId,
            new AnalyticsFilterRequest(), afterArrival.Meta.SnapshotToken)).Metrics.EligibleChecks);
    }

    [Fact]
    public async Task PromptHistorySnapshotWaitsForVersionCommitAndFreezesOldToken()
    {
        var connectionString = Environment.GetEnvironmentVariable("JOGEOAI_STAGE2_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        Assert.Equal("jogeoai_stage2_verified_20260926", new MySqlConnectionStringBuilder(connectionString).Database);
        var database = new MySqlDatabase(connectionString);
        var management = new ManagementStore(database);
        var analytics = new AnalyticsQueryStore(database, new AnalysisStore(database));
        var tenant = Guid.NewGuid();
        await Execute(connectionString, """
            INSERT INTO tenants (id, name, status, created_at, updated_at)
            VALUES (UUID_TO_BIN(@tenant), 'prompt-version-fence', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
            """, ("@tenant", tenant.ToString()));
        var brand = await management.CreateBrandAsync(tenant,
            new BrandWrite("Version Fence", $"{Guid.NewGuid():N}.fence.test", Description: "测试品牌提供设备与服务。"));
        var prompt = await management.CreatePromptAsync(tenant, brand.SiteId, new PromptWrite("first version"));
        var before = await analytics.PromptDetailAsync(tenant, brand.SiteId, prompt.PromptId,
            new AnalyticsFilterRequest(), null);
        Assert.Single(before.Versions);

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var fence = new MySqlCommand("""
            SELECT id FROM sites WHERE tenant_id = UUID_TO_BIN(@tenant) AND id = UUID_TO_BIN(@site)
            FOR UPDATE;
            """, connection, transaction))
        {
            fence.Parameters.AddWithValue("@tenant", tenant.ToString());
            fence.Parameters.AddWithValue("@site", brand.SiteId.ToString());
            Assert.NotNull(await fence.ExecuteScalarAsync());
        }
        var version = Guid.NewGuid();
        await using (var update = new MySqlCommand("""
            INSERT INTO prompt_versions (id, site_id, prompt_id, version, content, locale,
                branded_status, branded_source, created_at)
            VALUES (UUID_TO_BIN(@version), UUID_TO_BIN(@site), UUID_TO_BIN(@prompt), 2,
                'second version', 'en-US', 'unbranded', 'system', UTC_TIMESTAMP(6));
            UPDATE prompts SET content = 'second version', current_version_id = UUID_TO_BIN(@version),
                config_version = config_version + 1, updated_at = UTC_TIMESTAMP(6)
            WHERE id = UUID_TO_BIN(@prompt);
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("@version", version.ToString());
            update.Parameters.AddWithValue("@site", brand.SiteId.ToString());
            update.Parameters.AddWithValue("@prompt", prompt.PromptId.ToString());
            await update.ExecuteNonQueryAsync();
        }
        var waiting = analytics.PromptDetailAsync(tenant, brand.SiteId, prompt.PromptId,
            new AnalyticsFilterRequest(), null);
        await Task.Delay(100);
        Assert.False(waiting.IsCompleted);
        await transaction.CommitAsync();
        var after = await waiting;
        Assert.Equal(2, after.Versions.Count);
        Assert.Equal("second version", after.Versions[1].Content);
        Assert.Single((await analytics.PromptDetailAsync(tenant, brand.SiteId, prompt.PromptId,
            new AnalyticsFilterRequest(), before.Meta.SnapshotToken)).Versions);
    }

    [Fact]
    public async Task UpgradedLegacyRevisionStaysUnknownAfterAnalysis()
    {
        var connectionString = Environment.GetEnvironmentVariable("JOGEOAI_STAGE2_UPGRADE_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        Assert.Equal("jogeoai_stage2_upgrade_20260926", new MySqlConnectionStringBuilder(connectionString).Database);
        var database = new MySqlDatabase(connectionString);
        var analyzer = new AnalysisStore(database);
        Assert.InRange(await analyzer.ProcessPendingAsync("legacy-worker", 10), 0, 1);
        var tenant = Guid.Parse("22222222-2222-4222-8222-222222222222");
        var site = Guid.Parse("33333333-3333-4333-8333-333333333333");
        var check = Guid.Parse("99999999-9999-4999-8999-999999999999");
        var analysis = (await analyzer.GetCheckAnalysisAsync(tenant, check))!;
        Assert.Equal("completed", analysis.Status);
        Assert.True(analysis.ConfigurationMissing);
        Assert.Empty(analysis.Mentions);
        var metrics = await new AnalyticsQueryStore(database, analyzer).OverviewAsync(tenant, site,
            new AnalyticsFilterRequest(), null);
        Assert.Equal(1, metrics.Metrics.SuccessfulChecks);
        Assert.Equal(0, metrics.Metrics.EligibleChecks);
        Assert.Equal(1, metrics.Metrics.ConfigurationMissingChecks);
        Assert.Null(metrics.Metrics.Visibility.Value);
    }

    [Fact]
    public async Task IsolatedMySql_FindsSitesNeedingRebuildUntilAutoJobPublishes()
    {
        var connectionString = Environment.GetEnvironmentVariable("JOGEOAI_STAGE2_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        Assert.Equal("jogeoai_stage2_verified_20260926", new MySqlConnectionStringBuilder(connectionString).Database);
        var database = new MySqlDatabase(connectionString);
        var management = new ManagementStore(database);
        var analyzer = new AnalysisStore(database);
        var analytics = new AnalyticsQueryStore(database, analyzer);
        var tenant = Guid.NewGuid();
        await Execute(connectionString, "INSERT INTO tenants (id, name, status, created_at, updated_at) VALUES (UUID_TO_BIN(@tenant), 'auto-rebuild', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));", ("@tenant", tenant.ToString()));
        var brand = await management.CreateBrandAsync(tenant, new BrandWrite("Rebuild", $"{Guid.NewGuid():N}.rebuild.test", Description: "测试品牌提供设备与服务。"));
        async Task<AnalysisRebuildCandidate?> Find()
            => (await analyzer.FindSitesNeedingRebuildAsync(100000)).SingleOrDefault(c => c.SiteId == brand.SiteId);

        // 尚未建立分析状态的站点不处理；建立后配置与识别器一致，也不是候选。
        Assert.Null(await Find());
        _ = await analytics.OverviewAsync(tenant, brand.SiteId, new AnalyticsFilterRequest(), null);
        Assert.Null(await Find());

        // 新增竞品递增识别配置版本：成为候选，并带回最新修订时间（UTC）。
        await management.CreateCompetitorAsync(tenant, brand.SiteId, new CompetitorWrite("Rival"));
        var candidate = await Find();
        Assert.NotNull(candidate);
        Assert.Equal(tenant, candidate.TenantId);
        Assert.Equal(candidate.ReleaseConfigVersion + 1, candidate.LatestConfigVersion);
        Assert.Equal(MentionMatcher.Version, candidate.ReleaseExtractorVersion);
        Assert.NotNull(candidate.LatestRevisionCreatedAt);
        Assert.InRange(DateTimeOffset.UtcNow - candidate.LatestRevisionCreatedAt.Value,
            TimeSpan.FromMinutes(-5), TimeSpan.FromMinutes(5));

        // 有进行中的重算时不返回。
        await Execute(connectionString, """
            INSERT INTO analysis_releases (id, tenant_id, site_id, version, subject_config_version, subject_snapshot,
                extractor_version, metric_version, detail_version, url_normalization_version, status, created_at)
            SELECT UUID_TO_BIN(UUID()), tenant_id, site_id, 99, subject_config_version, subject_snapshot,
                extractor_version, metric_version, detail_version, url_normalization_version, 'building', UTC_TIMESTAMP(6)
            FROM analysis_releases WHERE site_id = UUID_TO_BIN(@site) AND version = 1;
            """, ("@site", brand.SiteId.ToString()));
        Assert.Null(await Find());
        await Execute(connectionString, "UPDATE analysis_releases SET status = 'failed' WHERE site_id = UUID_TO_BIN(@site) AND version = 99;",
            ("@site", brand.SiteId.ToString()));
        Assert.NotNull(await Find());

        // 自动作业（无回答时立即发布）后不再是候选；同一幂等键返回同一个作业。
        var key = AnalysisRebuildRules.IdempotencyKey(brand.SiteId, candidate.LatestConfigVersion, MentionMatcher.Version);
        var job = await analyzer.CreateJobAsync(tenant, brand.SiteId, key, new AnalysisJobRequest());
        Assert.Equal("site_config_changed", job.EffectiveScope);
        Assert.Equal("published", job.Status);
        Assert.Equal(job.JobId, (await analyzer.CreateJobAsync(tenant, brand.SiteId, key, new AnalysisJobRequest())).JobId);
        Assert.Null(await Find());

        // 已发布版本的识别器版本落后时也是候选。
        await Execute(connectionString, """
            UPDATE analysis_releases r JOIN site_analysis_state s ON s.published_release_id = r.id
            SET r.extractor_version = 'mention-v1' WHERE s.site_id = UUID_TO_BIN(@site);
            """, ("@site", brand.SiteId.ToString()));
        var stale = await Find();
        Assert.NotNull(stale);
        Assert.Equal("mention-v1", stale.ReleaseExtractorVersion);
        Assert.Equal(stale.LatestConfigVersion, stale.ReleaseConfigVersion);
    }

    [Fact]
    public async Task IsolatedMySql_AnalyzesPublishesReusesScopeAndFreezesSnapshot()
    {
        var connectionString = Environment.GetEnvironmentVariable("JOGEOAI_STAGE2_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        Assert.Equal("jogeoai_stage2_verified_20260926", new MySqlConnectionStringBuilder(connectionString).Database);
        var database = new MySqlDatabase(connectionString);
        var management = new ManagementStore(database);
        var analyzer = new AnalysisStore(database);
        var analytics = new AnalyticsQueryStore(database, analyzer);
        var tenant = Guid.NewGuid();
        await Execute(connectionString, "INSERT INTO tenants (id, name, status, created_at, updated_at) VALUES (UUID_TO_BIN(@tenant), 'stage2', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));", ("@tenant", tenant.ToString()));
        var brand = await management.CreateBrandAsync(tenant, new BrandWrite("Acme", $"{Guid.NewGuid():N}.a.test",
            AdditionalDomains: ["a.test"], Description: "测试品牌提供设备与服务。"));
        var b = await management.CreateCompetitorAsync(tenant, brand.SiteId, new CompetitorWrite("Beta"));
        var c = await management.CreateCompetitorAsync(tenant, brand.SiteId, new CompetitorWrite("Gamma"));
        var p1 = await management.CreatePromptAsync(tenant, brand.SiteId, new PromptWrite("best tools"));
        var p2 = await management.CreatePromptAsync(tenant, brand.SiteId, new PromptWrite("tool pricing"));
        var run = Guid.NewGuid();
        var target = Guid.NewGuid();
        await SeedRun(connectionString, tenant, brand.SiteId, run, target);
        var checks = new[]
        {
            await SeedCheck(connectionString, tenant, brand.SiteId, run, target, p1.PromptId, 1,
                "Acme Acme Acme Beta", ["https://a.test/x", "https://media.test/z"]),
            await SeedCheck(connectionString, tenant, brand.SiteId, run, target, p1.PromptId, 2,
                "Acme Beta", ["https://b.test/y"]),
            await SeedCheck(connectionString, tenant, brand.SiteId, run, target, p2.PromptId, 1,
                "Acme Gamma", ["https://a.test/x"]),
            await SeedCheck(connectionString, tenant, brand.SiteId, run, target, p2.PromptId, 2,
                "There are useful tools.", [])
        };
        await Execute(connectionString, """
            UPDATE visibility_checks SET search_queries = JSON_ARRAY('Acme tools', 'Beta compare')
            WHERE id = UUID_TO_BIN(@check);
            """, ("@check", checks[0].ToString()));
        await Execute(connectionString, """
            UPDATE citations SET source_domain = 'a.test' WHERE check_id = UUID_TO_BIN(@check);
            """, ("@check", checks[1].ToString()));
        await ProcessUntilAsync(analyzer, async () =>
        {
            foreach (var check in checks)
                if ((await analyzer.GetCheckAnalysisAsync(tenant, check))?.Status != "completed") return false;
            return true;
        });
        var overview = await analytics.OverviewAsync(tenant, brand.SiteId, new AnalyticsFilterRequest(), null);
        Assert.Equal(4, overview.Metrics.EligibleChecks);
        Assert.Equal(0.75, overview.Metrics.Visibility.Value);
        Assert.Equal(0.5, overview.Metrics.ShareOfVoice.Value);
        Assert.Equal(0.5, overview.Metrics.OwnCitationRate.Value);
        var regional = await analytics.OverviewAsync(tenant, brand.SiteId,
            new AnalyticsFilterRequest(Countries: ["us"], Locales: ["EN-us"]), null);
        Assert.Equal(4, regional.Metrics.EligibleChecks);
        Assert.Equal(0.75, regional.Metrics.Visibility.Value);
        Assert.NotNull(await analytics.CheckDetailAsync(tenant, checks[0], regional.Meta.SnapshotToken));
        var promptDetail = await analytics.PromptDetailAsync(tenant, brand.SiteId, p1.PromptId,
            new AnalyticsFilterRequest(), overview.Meta.SnapshotToken);
        Assert.Single(promptDetail.Versions);
        Assert.Equal("best tools", promptDetail.Versions[0].Content);
        Assert.Equal(2, promptDetail.Metrics.EligibleChecks);
        Assert.Equal(2, promptDetail.Metrics.Visibility.Numerator);
        Assert.Single(promptDetail.EnginePerformance);
        Assert.Equal("chatgpt", promptDetail.EnginePerformance[0].Engine);
        Assert.Equal(1, promptDetail.RunCount);
        Assert.Single(promptDetail.RecentRuns);
        Assert.Contains(overview.Meta.SnapshotToken, promptDetail.CheckHistoryPath);
        Assert.Equal(1, promptDetail.EvidenceBySource[0].ChecksWithSearchQueries);
        Assert.Equal(2, promptDetail.EvidenceBySource[0].DistinctSearchQueries);
        Assert.Equal(2, promptDetail.EvidenceBySource[0].ChecksWithCitations);
        Assert.Equal(3, promptDetail.EvidenceBySource[0].CitationCount);
        Assert.Contains(promptDetail.EvidenceBySource[0].CitationDomains,
            d => d.Domain == "a.test" && d.CitationCount == 1);
        Assert.Contains(promptDetail.CompetitorGapsBySource.Values.Single(),
            gap => gap.SubjectId == b.CompetitorId && gap.CompetitorMentions == 2
                && gap.MentionDifference == 0);
        var wrongDetail = await Assert.ThrowsAsync<CollectorStoreException>(() =>
            analytics.PromptDetailAsync(tenant, brand.SiteId, Guid.NewGuid(), new AnalyticsFilterRequest(), null));
        Assert.Equal(404, wrongDetail.StatusCode);
        var crossTenantDetail = await Assert.ThrowsAsync<CollectorStoreException>(() =>
            analytics.PromptDetailAsync(Guid.NewGuid(), brand.SiteId, p1.PromptId,
                new AnalyticsFilterRequest(), null));
        Assert.Equal(404, crossTenantDetail.StatusCode);
        var filteredDetail = await Assert.ThrowsAsync<CollectorStoreException>(() =>
            analytics.PromptDetailAsync(tenant, brand.SiteId, p1.PromptId,
                new AnalyticsFilterRequest(PromptIds: [p2.PromptId]), null));
        Assert.Equal(404, filteredDetail.StatusCode);

        var tag = await management.CreateTagAsync(tenant, brand.SiteId, new TagWrite("historical"));
        _ = await management.UpdatePromptAsync(tenant, brand.SiteId, p1.PromptId,
            new PromptPatch(Content: "best tools revised", TagIds: [tag.TagId],
                ExpectedVersion: (int)p1.ConfigVersion));
        var frozenPrompt = await analytics.PromptDetailAsync(tenant, brand.SiteId, p1.PromptId,
            new AnalyticsFilterRequest(), overview.Meta.SnapshotToken);
        Assert.Single(frozenPrompt.Versions);
        var currentPrompt = await analytics.PromptDetailAsync(tenant, brand.SiteId, p1.PromptId,
            new AnalyticsFilterRequest(), null);
        Assert.Equal(2, currentPrompt.Versions.Count);
        Assert.Equal("best tools", currentPrompt.Versions[0].Content);
        Assert.Equal("best tools revised", currentPrompt.Versions[1].Content);
        Assert.Equal("historical", currentPrompt.Versions[1].Tags.Single().Name);
        Assert.Equal(2, currentPrompt.Metrics.CheckCount);
        _ = await management.UpdateTagAsync(tenant, brand.SiteId, tag.TagId,
            new TagPatch(Name: "renamed", ExpectedVersion: tag.Version));
        Assert.Equal("historical", (await analytics.PromptDetailAsync(tenant, brand.SiteId, p1.PromptId,
            new AnalyticsFilterRequest(), null)).Versions[1].Tags.Single().Name);
        var voice = await analytics.ShareOfVoiceAsync(tenant, brand.SiteId, new AnalyticsFilterRequest(),
            overview.Meta.SnapshotToken, 1);
        Assert.Equal([3L, 2L, 1L], voice.Entries.Select(e => e.Mentions));
        Assert.Equal(3, voice.Metrics.Visibility.Numerator);
        Assert.Equal(3, voice.TopEntries[0].Mentions);
        Assert.Equal(3, voice.Others.Numerator);
        Assert.Equal(6, voice.Others.Denominator);
        var page1 = await analytics.EvidenceAsync(tenant, brand.SiteId, new AnalyticsFilterRequest(),
            overview.Meta.SnapshotToken, null, 2, brand.SiteId, null);
        Assert.Equal(2, page1.Items.Count);
        Assert.NotNull(page1.NextCursor);
        var page2 = await analytics.EvidenceAsync(tenant, brand.SiteId, new AnalyticsFilterRequest(),
            overview.Meta.SnapshotToken, page1.NextCursor, 2, brand.SiteId, null);
        Assert.Single(page2.Items);
        Assert.Empty(page1.Items.Select(i => i.CheckId).Intersect(page2.Items.Select(i => i.CheckId)));
        var invalidCursor = await Assert.ThrowsAsync<CollectorStoreException>(() => analytics.EvidenceAsync(
            tenant, brand.SiteId, new AnalyticsFilterRequest(), overview.Meta.SnapshotToken,
            page1.NextCursor, 2, b.CompetitorId, null));
        Assert.Equal("INVALID_CURSOR", invalidCursor.Code);
        Assert.Null(await analytics.CheckDetailAsync(Guid.NewGuid(), checks[0], null));
        var detail = (await analytics.CheckDetailAsync(tenant, checks[0], overview.Meta.SnapshotToken))!;
        Assert.Equal(1, detail.SampleIndex);
        Assert.Equal("best tools", detail.PromptSnapshot);
        Assert.Equal(4, detail.Analysis!.Mentions.Count);
        Assert.Equal(0, detail.Analysis.Mentions[0].OffsetStart);
        Assert.Equal(2, detail.Citations.Count);

        var scoped = await analyzer.CreateJobAsync(tenant, brand.SiteId, "scoped-" + Guid.NewGuid(),
            new AnalysisJobRequest(PromptIds: [p1.PromptId]));
        Assert.Equal("requested_scope_with_reuse", scoped.EffectiveScope);
        Assert.Equal(2, scoped.ReusedChecks);
        await ProcessUntilAsync(analyzer, async () =>
            (await analyzer.GetJobAsync(tenant, brand.SiteId, scoped.JobId))?.Status == "published");
        Assert.Equal("published", (await analyzer.GetJobAsync(tenant, brand.SiteId, scoped.JobId))!.Status);
        var stable = await analytics.OverviewAsync(tenant, brand.SiteId, new AnalyticsFilterRequest(),
            overview.Meta.SnapshotToken);
        Assert.Equal(4, stable.Metrics.EligibleChecks);

        brand = await management.UpdateBrandAsync(tenant, brand.SiteId,
            new BrandPatch(Aliases: ["Acme Inc"], ExpectedVersion: brand.SubjectConfigVersion + 2));
        var outdated = await analytics.OverviewAsync(tenant, brand.SiteId, new AnalyticsFilterRequest(), null);
        Assert.True(outdated.Meta.ConfigurationOutdated);
        Assert.False((await analytics.OverviewAsync(tenant, brand.SiteId,
            new AnalyticsFilterRequest(), overview.Meta.SnapshotToken)).Meta.ConfigurationOutdated);
        var rebuild = await analyzer.CreateJobAsync(tenant, brand.SiteId, "changed-" + Guid.NewGuid(),
            new AnalysisJobRequest(PromptIds: [p1.PromptId]));
        Assert.Equal("site_config_changed", rebuild.EffectiveScope);
        Assert.Equal(0, rebuild.ReusedChecks);
        await ProcessUntilAsync(analyzer, async () =>
            (await analyzer.GetJobAsync(tenant, brand.SiteId, rebuild.JobId))?.Status == "published");
        Assert.Equal("published", (await analyzer.GetJobAsync(tenant, brand.SiteId, rebuild.JobId))!.Status);

        var added = await SeedCheck(connectionString, tenant, brand.SiteId, run, target, p1.PromptId, 3,
            "No named brand here.", []);
        await ProcessUntilAsync(analyzer, async () =>
            (await analyzer.GetCheckAnalysisAsync(tenant, added))?.Status == "completed");
        Assert.Equal(4, (await analytics.OverviewAsync(tenant, brand.SiteId, new AnalyticsFilterRequest(),
            overview.Meta.SnapshotToken)).Metrics.EligibleChecks);
        Assert.Equal(5, (await analytics.OverviewAsync(tenant, brand.SiteId,
            new AnalyticsFilterRequest(), null)).Metrics.EligibleChecks);
        Assert.NotNull(await analytics.CheckDetailAsync(tenant, added, null));
        var wrongPrompt = await Assert.ThrowsAsync<CollectorStoreException>(() => analytics.PromptsAsync(
            tenant, brand.SiteId, new AnalyticsFilterRequest(PromptIds: [Guid.NewGuid()]),
            null, null, 50, null));
        Assert.Equal(404, wrongPrompt.StatusCode);

        await Execute(connectionString, """
            UPDATE analytics_snapshots SET expires_at = UTC_TIMESTAMP(6) - INTERVAL 1 SECOND
            WHERE token_hash = SHA2(@token, 256);
            """, ("@token", overview.Meta.SnapshotToken));
        var expired = await Assert.ThrowsAsync<CollectorStoreException>(() => analytics.OverviewAsync(
            tenant, brand.SiteId, new AnalyticsFilterRequest(), overview.Meta.SnapshotToken));
        Assert.Equal("SNAPSHOT_EXPIRED", expired.Code);

        var other = await management.CreateBrandAsync(tenant, new BrandWrite("Other", $"{Guid.NewGuid():N}.other.test", Description: "测试品牌提供设备与服务。"));
        var unqueued = await SeedCheck(connectionString, tenant, brand.SiteId, run, target, p1.PromptId, 4,
            "Cross-site FK probe.", [], queue: false);
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var cross = new MySqlCommand("""
            INSERT INTO analytics_check_arrivals (check_id, tenant_id, site_id, arrived_at)
            VALUES (UUID_TO_BIN(@check), UUID_TO_BIN(@tenant), UUID_TO_BIN(@other), UTC_TIMESTAMP(6));
            """, connection);
        cross.Parameters.AddWithValue("@check", unqueued.ToString());
        cross.Parameters.AddWithValue("@tenant", tenant.ToString());
        cross.Parameters.AddWithValue("@other", other.SiteId.ToString());
        var foreignKey = await Assert.ThrowsAsync<MySqlException>(() => cross.ExecuteNonQueryAsync());
        Assert.Equal(1452, foreignKey.Number);
    }

    private static async Task SeedRun(string connectionString, Guid tenant, Guid site, Guid run, Guid target)
    {
        await Execute(connectionString, """
            INSERT INTO visibility_runs (id, tenant_id, site_id, status, idempotency_key,
                requested_at, total_checks, succeeded_checks, created_at, updated_at)
            VALUES (UUID_TO_BIN(@run), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), 'succeeded', @key,
                UTC_TIMESTAMP(6), 5, 5, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
            INSERT INTO run_engines (id, run_id, engine, provider, target_key, capture_mode,
                country, locale, created_at)
            VALUES (UUID_TO_BIN(@target), UUID_TO_BIN(@run), 'chatgpt', 'cloro', @targetKey,
                'scraped', 'US', 'en-US', UTC_TIMESTAMP(6));
            """, ("@run", run.ToString()), ("@tenant", tenant.ToString()), ("@site", site.ToString()),
            ("@key", Guid.NewGuid().ToString()), ("@target", target.ToString()),
            ("@targetKey", new string('a', 64)));
    }

    private static async Task ProcessUntilAsync(AnalysisStore analyzer, Func<Task<bool>> completed)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (await completed()) return;
            if (await analyzer.ProcessPendingAsync("test-worker", 100) == 0) break;
        }
        Assert.True(await completed(), "本次 Check 或重算任务未完成。");
    }

    private static async Task<Guid> SeedCheck(string connectionString, Guid tenant, Guid site, Guid run,
        Guid target, Guid prompt, int sample, string answer, IReadOnlyList<string> citations,
        bool queue = true, Func<Task>? beforeCommit = null)
    {
        var check = Guid.NewGuid();
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var insert = new MySqlCommand("""
            INSERT INTO visibility_checks (id, tenant_id, site_id, run_id, prompt_id, engine,
                provider, status, prompt_snapshot, answer, answer_present, captured_at,
                capture_mode, evidence_class, country, locale, run_target_id, sample_index,
                prompt_version_id, prompt_tags_snapshot, branded_status_snapshot,
                subject_config_version, created_at, updated_at)
            SELECT UUID_TO_BIN(@check), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), UUID_TO_BIN(@run),
                   p.id, 'chatgpt', 'cloro', 'succeeded', p.content, @answer, TRUE,
                   UTC_TIMESTAMP(6), 'scraped', 'observed_surface', 'US', 'en-US',
                   UUID_TO_BIN(@target), @sample, p.current_version_id, JSON_ARRAY(), 'unbranded',
                   b.subject_config_version, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)
            FROM prompts p JOIN site_brand_settings b ON b.site_id = p.site_id
            WHERE p.id = UUID_TO_BIN(@prompt);
            """, connection, transaction);
        insert.Parameters.AddWithValue("@check", check.ToString());
        insert.Parameters.AddWithValue("@tenant", tenant.ToString());
        insert.Parameters.AddWithValue("@site", site.ToString());
        insert.Parameters.AddWithValue("@run", run.ToString());
        insert.Parameters.AddWithValue("@target", target.ToString());
        insert.Parameters.AddWithValue("@sample", sample);
        insert.Parameters.AddWithValue("@answer", answer);
        insert.Parameters.AddWithValue("@prompt", prompt.ToString());
        await insert.ExecuteNonQueryAsync();
        for (var i = 0; i < citations.Count; i++)
        {
            var uri = new Uri(citations[i]);
            await using var cited = new MySqlCommand("""
                INSERT INTO citations (id, check_id, normalized_url, source_domain, position, created_at)
                VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@check), @url, @domain, @position, UTC_TIMESTAMP(6));
                """, connection, transaction);
            cited.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
            cited.Parameters.AddWithValue("@check", check.ToString());
            cited.Parameters.AddWithValue("@url", citations[i]);
            cited.Parameters.AddWithValue("@domain", uri.Host);
            cited.Parameters.AddWithValue("@position", i + 1);
            await cited.ExecuteNonQueryAsync();
        }
        if (queue) await AnalysisEnqueue.QueueAsync(connection, transaction, check, CancellationToken.None);
        if (beforeCommit is not null) await beforeCommit();
        await transaction.CommitAsync();
        return check;
    }

    private static async Task Execute(string connectionString, string sql, params (string Name, object Value)[] values)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }
}
