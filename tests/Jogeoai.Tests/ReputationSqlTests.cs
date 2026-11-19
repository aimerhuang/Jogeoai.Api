using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Jogeoai.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace Jogeoai.Tests;

/// <summary>
/// 口碑 SQL 测试只在已执行 001–038 的独立测试库上运行；绝不连接业务库 jogeoai。
/// 情感 Worker 的登记与领取按设计跨租户全局运行，因此该库不能同时被其他 SQL 测试类使用（否则会登记并处理其他夹具的回答）。
/// </summary>
public sealed class ReputationMySqlFactAttribute : FactAttribute
{
    public ReputationMySqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JOGEOAI_REPUTATION_TEST_CONNECTION")))
            Skip = "需提供已执行 001–038 的专用独立测试库 JOGEOAI_REPUTATION_TEST_CONNECTION；不能使用业务库或与其他 SQL 测试共用。";
    }
}

/// <summary>
/// 端到端：采集事实 → 提及分析 → 口碑工作项 → Worker（测试替身分类器，不访问网关）→ 聚合 / 回答列表 / 详情。
/// 断言同一快照下各区块计数与 monitoring/records?sentiment=&amp;term= 一致、快照栅栏、过期、跨租户与补录任务。
/// </summary>
public sealed class ReputationSqlTests
{
    [ReputationMySqlFact]
    public async Task AggregatesRecordsAndDetailShareOneSnapshotAndOneStateResolution()
    {
        var connectionString = ConnectionString();
        var database = new MySqlDatabase(connectionString);
        var management = new ManagementStore(database);
        var analyzer = new AnalysisStore(database);
        var analytics = new AnalyticsQueryStore(database, analyzer);
        var store = new ReputationStore(database);
        var fixture = await CreateFixtureAsync(connectionString, management);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var checks = new Dictionary<string, Guid>
            {
                ["positiveA"] = await SeedCheckAsync(connectionString, fixture, fixture.PromptA, 1, "Acme is excellent and reliable.", "cloro", "chatgpt", "scraped", now.AddMinutes(-30)),
                ["neutral"] = await SeedCheckAsync(connectionString, fixture, fixture.PromptA, 2, "Acme is an average option.", "doubao_api", "doubao", "api", now.AddMinutes(-29)),
                ["negative"] = await SeedCheckAsync(connectionString, fixture, fixture.PromptA, 3, "Acme is terrible and slow.", "domestic_collector", "doubao", "scraped", now.AddMinutes(-28)),
                ["notMentioned"] = await SeedCheckAsync(connectionString, fixture, fixture.PromptA, 4, "Beta is the only choice.", "cloro", "chatgpt", "scraped", now.AddMinutes(-27)),
                ["positiveB"] = await SeedCheckAsync(connectionString, fixture, fixture.PromptB, 5, "Acme support is excellent.", "cloro", "chatgpt", "scraped", now.AddMinutes(-26)),
                ["unknown"] = await SeedCheckAsync(connectionString, fixture, fixture.PromptB, 6, "Acme is unsure territory.", "cloro", "chatgpt", "scraped", now.AddMinutes(-25)),
                ["failedCapture"] = await SeedCheckAsync(connectionString, fixture, fixture.PromptB, 7, null, "cloro", "chatgpt", "scraped", now.AddMinutes(-24), "failed"),
                ["failedJudgement"] = await SeedCheckAsync(connectionString, fixture, fixture.PromptB, 8, "Acme might explode.", "cloro", "chatgpt", "scraped", now.AddMinutes(-23)),
            };
            for (var i = 0; i < 3; i++) await analyzer.ProcessPendingAsync("reputation-test", 100);

            var classifier = new KeywordClassifier();
            var worker = new ReputationSentimentWorker(store, classifier, new StaticSettings(),
                Options.Create(new ReputationSentimentWorkerOptions
                {
                    MaxAttempts = 1, BatchSize = 50, AutoEnqueueSinceUtc = DateTimeOffset.UnixEpoch
                }), NullLogger<ReputationSentimentWorker>.Instance);
            var pass = await worker.RunPassAsync();
            Assert.Equal(1, pass.NotMentioned);
            Assert.Equal(1, pass.Failed);
            Assert.Equal(5, pass.Classified);
            Assert.DoesNotContain(checks["notMentioned"], classifier.Seen);

            // 提及分析完成但尚未登记 / 判定的回答 → pending。
            checks["pending"] = await SeedCheckAsync(connectionString, fixture, fixture.PromptA, 9, "Acme is excellent again.", "cloro", "chatgpt", "scraped", now.AddMinutes(-20));
            for (var i = 0; i < 3; i++) await analyzer.ProcessPendingAsync("reputation-test", 100);

            var view = await analytics.ReputationAsync(fixture.Tenant, fixture.Site, AllSources(), null, null);
            var token = view.Meta.SnapshotToken;
            var score = view.Metrics.Score;
            Assert.Equal((2, 1, 1, 1, 1, 1), (score.Positive, score.Neutral, score.Negative, score.Unknown, score.Pending, score.Failed));
            Assert.Equal(62.5, score.Health);
            Assert.Equal(ReputationHealthLevel.Fair, score.Level);
            Assert.Equal(8, view.Metrics.EligibleAnswers);
            Assert.Equal(1, view.Metrics.NotMentioned);
            Assert.True(score.PartialCoverage); // 4 / 7
            Assert.Equal(30, view.Trend.Count);
            Assert.Equal("INSUFFICIENT_SAMPLE", view.Comparison.UnavailableReason);
            // 同一引擎 doubao 的 API 与网页抓取是两个平台；按已判定回答数降序、平台键升序。
            Assert.Equal(new[] { "cloro|chatgpt|scraped", "domestic_collector|doubao|scraped", "doubao_api|doubao|api" },
                view.Platforms.Select(p => p.PlatformKey));
            Assert.Equal(view.Platforms.Select(p => p.PlatformKey), view.Heatmap.Columns);
            Assert.Equal(new[] { "cloro|perplexity|scraped" }, view.PlatformsWithoutData.Select(p => p.PlatformKey));

            // 同一快照：每个状态的聚合计数 = 回答列表同筛选总数。
            async Task<long> Count(string? sentiment, string? term = null, string? termPolarity = null)
                => (await analytics.MonitoringRecordsAsync(fixture.Tenant, fixture.Site, new AnalyticsFilterRequest(), token,
                    null, 100, null, null, null, sentiment, term, termPolarity)).TotalCount;
            Assert.Equal(score.Positive, await Count("positive"));
            Assert.Equal(score.Neutral, await Count("neutral"));
            Assert.Equal(score.Negative, await Count("negative"));
            Assert.Equal(score.Unknown, await Count("unknown"));
            Assert.Equal(score.Pending, await Count("pending"));
            Assert.Equal(score.Failed, await Count("failed"));
            Assert.Equal(view.Metrics.NotMentioned, await Count("not_mentioned"));
            Assert.Equal(1, await Count("not_applicable"));
            var term = Assert.Single(view.TermGroups.Positive.Concat(view.TermGroups.Neutral).Concat(view.TermGroups.Negative), t => t.TermKey == "excellent");
            Assert.Equal(2, term.AnswerCount);
            Assert.Equal(term.AnswerCount, await Count(null, " EXCELLENT "));
            Assert.Equal(term.AnswerCount, await Count("positive", "excellent"));
            Assert.Equal(0, await Count("negative", "excellent"));
            Assert.Equal(term.PositiveAnswers, await Count(null, "excellent", "positive"));
            Assert.Equal(term.NegativeAnswers, await Count(null, "excellent", "negative"));
            foreach (var platform in view.Platforms)
                Assert.Equal(platform.Score.Classified, view.Heatmap.Rows.Sum(r => r.Cells.Single(c => c.PlatformKey == platform.PlatformKey).Score.Classified));

            // 列表行与详情的情感判定一致。
            var all = await analytics.MonitoringRecordsAsync(fixture.Tenant, fixture.Site, new AnalyticsFilterRequest(), token, null, 100);
            Assert.All(all.Items, item => Assert.NotNull(item.ReputationSentiment));
            foreach (var item in all.Items)
            {
                var detail = await analytics.MonitoringAnswerDetailAsync(fixture.Tenant, fixture.Site, item.CheckId, token, null);
                Assert.Equal(item.ReputationSentiment, detail!.ReputationSentiment);
            }
            var positiveRow = all.Items.Single(i => i.CheckId == checks["positiveA"]).ReputationSentiment!;
            Assert.Equal(ReputationSentimentState.Positive, positiveRow.State);
            Assert.Equal("Acme is excellent and reliable.".IndexOf("excellent", StringComparison.Ordinal), positiveRow.EvidenceStart);
            Assert.Equal(ReputationRules.LowConfidence, all.Items.Single(i => i.CheckId == checks["unknown"]).ReputationSentiment!.RationaleCode);
            Assert.Equal(ReputationClassifierException.Retryable, all.Items.Single(i => i.CheckId == checks["failedJudgement"]).ReputationSentiment!.RationaleCode);

            // 快照栅栏：快照之后写入的判定不进入旧令牌，新快照才可见。
            var second = await worker.RunPassAsync();
            Assert.Equal(1, second.Classified);
            var stale = await analytics.ReputationAsync(fixture.Tenant, fixture.Site, new AnalyticsFilterRequest(), token, null);
            Assert.Equal(1, stale.Metrics.Score.Pending);
            Assert.Equal(score.Pending, await Count("pending"));
            var fresh = await analytics.ReputationAsync(fixture.Tenant, fixture.Site, AllSources(), null, null);
            Assert.Equal(0, fresh.Metrics.Score.Pending);
            Assert.Equal(3, fresh.Metrics.Score.Positive);

            // 当日视图：from/to 必须是一个当地自然日；趋势为空。
            var today = DateOnly.FromDateTime(now.UtcDateTime);
            var dayStart = new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var day = await analytics.ReputationAsync(fixture.Tenant, fixture.Site,
                AllSources() with { From = dayStart, To = dayStart.AddDays(1) }, null, "day");
            Assert.Empty(day.Trend);
            Assert.Equal(ReputationView.Day, day.Meta.View);
            Assert.True(day.Meta.InProgress);
            var invalidDay = await Assert.ThrowsAsync<CollectorStoreException>(() =>
                analytics.ReputationAsync(fixture.Tenant, fixture.Site, new AnalyticsFilterRequest(), null, "day"));
            Assert.Equal("INVALID_FILTER", invalidDay.Code);

            var expired = await Assert.ThrowsAsync<CollectorStoreException>(() =>
                analytics.ReputationAsync(fixture.Tenant, fixture.Site, new AnalyticsFilterRequest(), "expired-token-expired-token", null));
            Assert.Equal("SNAPSHOT_EXPIRED", expired.Code);
            var crossTenant = await Assert.ThrowsAsync<CollectorStoreException>(() =>
                analytics.ReputationAsync(Guid.NewGuid(), fixture.Site, new AnalyticsFilterRequest(), null, null));
            Assert.Equal("RESOURCE_NOT_FOUND", crossTenant.Code);
            var crossTenantRecords = await Assert.ThrowsAsync<CollectorStoreException>(() =>
                analytics.MonitoringRecordsAsync(Guid.NewGuid(), fixture.Site, new AnalyticsFilterRequest(), null, null, 10,
                    null, null, null, "positive", null, null));
            Assert.Equal("RESOURCE_NOT_FOUND", crossTenantRecords.Code);
        }
        finally
        {
            await CleanupFixtureAsync(connectionString, fixture);
        }
    }

    [ReputationMySqlFact]
    public async Task BackfillJobIsIdempotentAndReusedJudgementsSkipTheClassifier()
    {
        var connectionString = ConnectionString();
        var database = new MySqlDatabase(connectionString);
        var management = new ManagementStore(database);
        var analyzer = new AnalysisStore(database);
        var store = new ReputationStore(database);
        var fixture = await CreateFixtureAsync(connectionString, management);
        try
        {
            await SeedCheckAsync(connectionString, fixture, fixture.PromptA, 1, "Acme is excellent.", "cloro", "chatgpt", "scraped", DateTimeOffset.UtcNow.AddMinutes(-5));
            await SeedCheckAsync(connectionString, fixture, fixture.PromptA, 2, "Beta only.", "cloro", "chatgpt", "scraped", DateTimeOffset.UtcNow.AddMinutes(-4));
            for (var i = 0; i < 3; i++) await analyzer.ProcessPendingAsync("reputation-test", 100);

            var job = await store.CreateJobAsync(fixture.Tenant, fixture.Site, "backfill-1", new ReputationJobRequest());
            Assert.Equal(2, job.TotalChecks);
            Assert.Equal(2, job.PendingChecks);
            Assert.Equal("processing", job.Status);
            var again = await store.CreateJobAsync(fixture.Tenant, fixture.Site, "backfill-1", new ReputationJobRequest());
            Assert.Equal(job.JobId, again.JobId);
            var conflict = await Assert.ThrowsAsync<CollectorStoreException>(() => store.CreateJobAsync(fixture.Tenant, fixture.Site,
                "backfill-1", new ReputationJobRequest(DateTimeOffset.UtcNow.AddDays(-1))));
            Assert.Equal("REPUTATION_IDEMPOTENCY_CONFLICT", conflict.Code);
            Assert.Null(await store.GetJobAsync(Guid.NewGuid(), fixture.Site, job.JobId));

            var classifier = new KeywordClassifier();
            var worker = new ReputationSentimentWorker(store, classifier, new StaticSettings(),
                Options.Create(new ReputationSentimentWorkerOptions { BatchSize = 50 }), NullLogger<ReputationSentimentWorker>.Instance);
            await worker.RunPassAsync();
            var done = await store.GetJobAsync(fixture.Tenant, fixture.Site, job.JobId);
            Assert.Equal(("completed", 2L, 0L), (done!.Status, done.CompletedChecks, done.PendingChecks));
            Assert.Single(classifier.Seen);

            // 新分析版本（全站重算）中相同输入的判定被复用，不再调用分类器。
            await analyzer.CreateJobAsync(fixture.Tenant, fixture.Site, "rebuild-1", new AnalysisJobRequest());
            for (var i = 0; i < 3; i++) await analyzer.ProcessPendingAsync("reputation-test", 100);
            var rebuildWorker = new ReputationSentimentWorker(store, classifier, new StaticSettings(),
                Options.Create(new ReputationSentimentWorkerOptions { BatchSize = 50, AutoEnqueueSinceUtc = DateTimeOffset.UnixEpoch }),
                NullLogger<ReputationSentimentWorker>.Instance);
            var pass = await rebuildWorker.RunPassAsync();
            Assert.Equal(1, pass.Reused);
            Assert.Equal(1, pass.NotMentioned);
            Assert.Single(classifier.Seen);
        }
        finally
        {
            await CleanupFixtureAsync(connectionString, fixture);
        }
    }

    [ReputationMySqlFact]
    public async Task ExpiredLeaseAtTheAttemptCapBecomesFailedWithoutAutoEnqueue()
    {
        var connectionString = ConnectionString();
        var database = new MySqlDatabase(connectionString);
        var management = new ManagementStore(database);
        var analyzer = new AnalysisStore(database);
        var analytics = new AnalyticsQueryStore(database, analyzer);
        var store = new ReputationStore(database);
        var fixture = await CreateFixtureAsync(connectionString, management);
        try
        {
            var check = await SeedCheckAsync(connectionString, fixture, fixture.PromptA, 1, "Acme is excellent.", "cloro",
                "chatgpt", "scraped", DateTimeOffset.UtcNow.AddMinutes(-5));
            for (var i = 0; i < 3; i++) await analyzer.ProcessPendingAsync("reputation-test", 100);
            await store.CreateJobAsync(fixture.Tenant, fixture.Site, "expire-1", new ReputationJobRequest());
            // Worker 在最后一次（第 1 / 1 次）尝试中退出：租约过期、尝试次数已满，不会再被领取。
            await ExecuteAsync(connectionString, """
                UPDATE reputation_check_work SET status = 'processing', attempt_count = 1, lease_owner = 'dead-worker',
                    lease_version = 1, lease_until = UTC_TIMESTAMP(6) - INTERVAL 1 MINUTE
                WHERE check_id = UUID_TO_BIN(@check);
                """, ("@check", check.ToString()));
            Assert.Null(await store.ClaimAsync("reputation-test", true, TimeSpan.FromMinutes(1), 1));

            var worker = new ReputationSentimentWorker(store, new KeywordClassifier(), new StaticSettings(),
                Options.Create(new ReputationSentimentWorkerOptions { MaxAttempts = 1 }), NullLogger<ReputationSentimentWorker>.Instance);
            await worker.RunPassAsync();

            var view = await analytics.ReputationAsync(fixture.Tenant, fixture.Site, AllSources(), null, null);
            Assert.Equal((0L, 1L), (view.Metrics.Score.Pending, view.Metrics.Score.Failed));
            var records = await analytics.MonitoringRecordsAsync(fixture.Tenant, fixture.Site, new AnalyticsFilterRequest(),
                view.Meta.SnapshotToken, null, 10, null, null, null, "failed", null, null);
            Assert.Equal("LEASE_EXPIRED", Assert.Single(records.Items).ReputationSentiment!.RationaleCode);
        }
        finally
        {
            await CleanupFixtureAsync(connectionString, fixture);
        }
    }

    /// <summary>按关键词给出确定结论的测试替身；结论仍经 ReputationRules.Evaluate 校验，与真实适配器同一出口。</summary>
    private sealed class KeywordClassifier : IReputationSentimentClassifier
    {
        public List<Guid> Seen { get; } = [];
        public string ModelName => "test-double";

        public Task<ReputationClassification> ClassifyAsync(ReputationClassificationInput input,
            ResearchProviderConfiguration configuration, CancellationToken cancellationToken = default)
        {
            Seen.Add(Guid.NewGuid());
            var answer = input.Answer;
            if (answer.Contains("explode", StringComparison.Ordinal))
                throw new ReputationClassifierException(ReputationClassifierException.Retryable, "busy", true);
            ReputationClassifierOutput output = answer switch
            {
                _ when answer.Contains("excellent", StringComparison.Ordinal) => new("positive", 0.9, "PRAISE", "excellent",
                    [new("excellent", "positive"), new("Acme", "positive")]),
                _ when answer.Contains("average", StringComparison.Ordinal) => new("neutral", 0.8, "FACTUAL_DESCRIPTION", "average option",
                    [new("average option", "neutral")]),
                _ when answer.Contains("terrible", StringComparison.Ordinal) => new("negative", 0.85, "CRITICISM", "terrible and slow",
                    [new("terrible", "negative"), new("slow", "negative")]),
                _ => new("positive", 0.3, "PRAISE", "unsure", [])
            };
            return Task.FromResult(ReputationRules.Evaluate(output, input, ModelName));
        }
    }

    // 与 AnalyticsController.AllSourcesFilter 相同的默认来源全集（Store 自身省略时只看 cloro / scraped）。
    private static AnalyticsFilterRequest AllSources() => new(
        Providers: ["cloro", "doubao_api", "deepseek_api", "packy_qwen", "packy_glm", "packy_kimi", "packy_yuanbao", "domestic_collector"],
        CaptureModes: ["api", "scraped"], EvidenceClasses: ["api_response", "observed_surface", "unknown"]);

    private sealed class StaticSettings : IReputationClassifierSettings
    {
        public Task<ResearchProviderConfiguration?> GetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<ResearchProviderConfiguration?>(new("openai-compatible", "m", "https://llm.invalid/v1", "unused", false, 30, 1));
    }

    private static string ConnectionString()
    {
        var value = Environment.GetEnvironmentVariable("JOGEOAI_REPUTATION_TEST_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(value), "未设置 JOGEOAI_REPUTATION_TEST_CONNECTION。");
        Assert.NotEqual("jogeoai", new MySqlConnectionStringBuilder(value!).Database);
        return value!;
    }

    private sealed record Fixture(Guid Tenant, Guid Site, Guid Run, Guid Target, Guid PromptA, Guid PromptB);

    private static async Task<Fixture> CreateFixtureAsync(string connectionString, ManagementStore management)
    {
        var tenant = Guid.NewGuid();
        await ExecuteAsync(connectionString, """
            INSERT INTO tenants (id, name, status, created_at, updated_at)
            VALUES (UUID_TO_BIN(@tenant), 'reputation fixture', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
            """, ("@tenant", tenant.ToString()));
        var brand = await management.CreateBrandAsync(tenant, new BrandWrite("Acme", "acme.test", TimeZone: "UTC",
            Description: "测试品牌。"));
        await management.CreateCompetitorAsync(tenant, brand.SiteId, new CompetitorWrite("Beta", Domains: ["beta.test"]));
        var promptA = await management.CreatePromptAsync(tenant, brand.SiteId, new PromptWrite("best tools A"));
        var promptB = await management.CreatePromptAsync(tenant, brand.SiteId, new PromptWrite("best tools B"));
        var run = Guid.NewGuid();
        var target = Guid.NewGuid();
        await ExecuteAsync(connectionString, """
            INSERT INTO visibility_runs (id, tenant_id, site_id, status, idempotency_key,
                requested_at, total_checks, succeeded_checks, created_at, updated_at)
            VALUES (UUID_TO_BIN(@run), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), 'succeeded', @key,
                UTC_TIMESTAMP(6), 9, 8, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
            INSERT INTO run_engines (id, run_id, engine, provider, target_key, capture_mode, country, locale, created_at)
            VALUES (UUID_TO_BIN(@target), UUID_TO_BIN(@run), 'chatgpt', 'cloro', @targetKey, 'scraped', 'US', 'en-US', UTC_TIMESTAMP(6));
            """, ("@run", run.ToString()), ("@tenant", tenant.ToString()), ("@site", brand.SiteId.ToString()),
            ("@key", Guid.NewGuid().ToString()), ("@target", target.ToString()), ("@targetKey", new string('r', 64)));
        await management.PutSiteTargetsAsync(tenant, brand.SiteId, new SiteTargetsWrite(1,
            [new SiteTargetWrite("cloro", "chatgpt", "scraped", "US", "en-US", true),
             new SiteTargetWrite("cloro", "perplexity", "scraped", "US", "en-US", true)]));
        return new Fixture(tenant, brand.SiteId, run, target, promptA.PromptId, promptB.PromptId);
    }

    private static async Task<Guid> SeedCheckAsync(string connectionString, Fixture fixture, Guid prompt, int sample,
        string? answer, string provider, string engine, string captureMode, DateTimeOffset captured, string status = "succeeded")
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
                @engine, @provider, @status, p.content, @answer, @present, @captured, @mode, @evidence,
                'US', 'en-US', UUID_TO_BIN(@target), @sample, p.current_version_id, JSON_ARRAY(), 'unbranded',
                b.subject_config_version, NULL, 100, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)
            FROM prompts p JOIN site_brand_settings b ON b.site_id = p.site_id
            WHERE p.id = UUID_TO_BIN(@prompt);
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("@check", check.ToString());
            insert.Parameters.AddWithValue("@tenant", fixture.Tenant.ToString());
            insert.Parameters.AddWithValue("@site", fixture.Site.ToString());
            insert.Parameters.AddWithValue("@run", fixture.Run.ToString());
            insert.Parameters.AddWithValue("@target", fixture.Target.ToString());
            insert.Parameters.AddWithValue("@prompt", prompt.ToString());
            insert.Parameters.AddWithValue("@engine", engine);
            insert.Parameters.AddWithValue("@provider", provider);
            insert.Parameters.AddWithValue("@status", status);
            insert.Parameters.AddWithValue("@answer", answer ?? (object)DBNull.Value);
            insert.Parameters.AddWithValue("@present", !string.IsNullOrWhiteSpace(answer));
            insert.Parameters.AddWithValue("@captured", captured.UtcDateTime);
            insert.Parameters.AddWithValue("@mode", captureMode);
            insert.Parameters.AddWithValue("@evidence", captureMode == "api" ? "api_response" : "observed_surface");
            insert.Parameters.AddWithValue("@sample", sample);
            await insert.ExecuteNonQueryAsync();
        }
        await AnalysisEnqueue.QueueAsync(connection, transaction, check, CancellationToken.None);
        await transaction.CommitAsync();
        return check;
    }

    private static async Task ExecuteAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CleanupFixtureAsync(string connectionString, Fixture fixture)
    {
        // 新租户独占的数据，按依赖顺序删除；口碑四张表先于分析表。
        var statements = new[]
        {
            "DELETE FROM reputation_term_facts WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);",
            "DELETE FROM reputation_check_sentiments WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);",
            "DELETE FROM reputation_analysis_jobs WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);",
            "DELETE FROM reputation_check_work WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);",
            "DELETE FROM analytics_snapshots WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);",
            "DELETE FROM analysis_jobs WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);",
            "DELETE q FROM query_term_facts q JOIN check_analysis a ON a.release_id = q.release_id AND a.check_id = q.check_id WHERE a.tenant_id = UUID_TO_BIN(@tenant) AND a.site_id = UUID_TO_BIN(@site);",
            "DELETE c FROM citation_classifications c JOIN check_analysis a ON a.release_id = c.release_id AND a.check_id = c.check_id WHERE a.tenant_id = UUID_TO_BIN(@tenant) AND a.site_id = UUID_TO_BIN(@site);",
            "DELETE g FROM google_internal_events g JOIN check_analysis a ON a.release_id = g.release_id AND a.check_id = g.check_id WHERE a.tenant_id = UUID_TO_BIN(@tenant) AND a.site_id = UUID_TO_BIN(@site);",
            "DELETE c FROM citation_events c JOIN check_analysis a ON a.release_id = c.release_id AND a.check_id = c.check_id WHERE a.tenant_id = UUID_TO_BIN(@tenant) AND a.site_id = UUID_TO_BIN(@site);",
            "DELETE q FROM check_query_events q JOIN check_analysis a ON a.release_id = q.release_id AND a.check_id = q.check_id WHERE a.tenant_id = UUID_TO_BIN(@tenant) AND a.site_id = UUID_TO_BIN(@site);",
            "DELETE m FROM mention_evidence m JOIN check_analysis a ON a.release_id = m.release_id AND a.check_id = m.check_id WHERE a.tenant_id = UUID_TO_BIN(@tenant) AND a.site_id = UUID_TO_BIN(@site);",
            "DELETE p FROM check_subject_presence p JOIN check_analysis a ON a.release_id = p.release_id AND a.check_id = p.check_id WHERE a.tenant_id = UUID_TO_BIN(@tenant) AND a.site_id = UUID_TO_BIN(@site);",
            "DELETE FROM check_detailed_completions WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);",
            "UPDATE check_analysis SET completed_event_id = NULL WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);",
            "DELETE FROM check_analysis WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);",
            "DELETE e FROM analysis_events e JOIN analysis_check_work w ON w.release_id = e.release_id AND w.check_id = e.check_id WHERE w.tenant_id = UUID_TO_BIN(@tenant) AND w.site_id = UUID_TO_BIN(@site);",
            "DELETE FROM analysis_check_work WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);",
            "DELETE FROM site_analysis_state WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);",
            "DELETE FROM analysis_releases WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);",
            "DELETE FROM analytics_check_arrivals WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);",
            "DELETE FROM citations WHERE check_id IN (SELECT id FROM visibility_checks WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site));",
            "DELETE FROM visibility_checks WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);",
            "DELETE FROM run_engines WHERE run_id IN (SELECT id FROM visibility_runs WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site));",
            "DELETE FROM visibility_runs WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);",
            "DELETE FROM outbox_events WHERE tenant_id = UUID_TO_BIN(@tenant);",
            "UPDATE prompts SET current_version_id = NULL WHERE site_id = UUID_TO_BIN(@site);",
            "DELETE FROM prompt_version_tags WHERE prompt_version_id IN (SELECT id FROM prompt_versions WHERE site_id = UUID_TO_BIN(@site));",
            "DELETE FROM prompt_versions WHERE site_id = UUID_TO_BIN(@site);",
            "DELETE FROM prompts WHERE site_id = UUID_TO_BIN(@site);",
            "DELETE FROM subject_config_revisions WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);",
            "DELETE FROM subject_terms WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);",
            "DELETE FROM competitors WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);",
            "DELETE FROM site_targets WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);",
            "DELETE FROM site_brand_settings WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);",
            "DELETE FROM onboarding_progress WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);",
            "DELETE FROM sites WHERE tenant_id = UUID_TO_BIN(@tenant) AND id = UUID_TO_BIN(@site);",
            "DELETE FROM tenants WHERE id = UUID_TO_BIN(@tenant);"
        };
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        foreach (var sql in statements)
        {
            await using var command = new MySqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("@tenant", fixture.Tenant.ToString());
            command.Parameters.AddWithValue("@site", fixture.Site.ToString());
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }
}
