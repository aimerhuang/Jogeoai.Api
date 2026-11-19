using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Jogeoai.Api;
using Jogeoai.Api.Controllers;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Jogeoai.Providers.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using static Jogeoai.Tests.AppCreditSqlTests;
using static Jogeoai.Tests.AppCycleRunSqlTests;
using static Jogeoai.Tests.AppLeaseSqlTests;
using static Jogeoai.Tests.TeamMonitoringConfigSqlTests;

namespace Jogeoai.Tests;

/// <summary>
/// 隔离库（已执行 001–049、库名含 _test）上的 App 渠道统计与记录接口（implement 阶段 7，results.md 8.2 / 8.3）：
/// 给一个已有 API 样本的品牌写入 App 样本后，默认口径的每个入口与写入前相同；App 口径只由 App 样本算出；混合筛选被拒绝；
/// 引用域名汇总的 channel；带 URL 的 App 引用进入文章监控、被引次数和引用发现，并能追溯到提问和回答；Run 列表与摘要。
/// 未设置 JOGEOAI_APP_CHANNEL_TEST_CONNECTION 时跳过，绝不使用业务库 jogeoai。只用自己新建的租户、节点和绑定，
/// 结束时删除它们写下的全部行并核对整库每张表的行数。与迁移用例同一个集合：它们会执行 DDL，不能并行。
/// </summary>
[Collection(AppChannelSqlCollection.Name)]
public sealed partial class AppAnalyticsSqlTests
{
    private const string Brand = "Acmebook";
    private const string Competitor = "Betaledger";
    private const string OwnDomain = "acmebook.test";
    private const string AppProvider = AppChannelDefinition.Provider;
    private const string AppSource = "app_collector|app|observed_surface";

    // Same as Program.cs AddJsonOptions: web defaults plus string enums.
    private static readonly JsonSerializerOptions Api = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly SiteTargetWrite ChatGpt = new("cloro", "chatgpt", "scraped", "US", "en-US");
    private static readonly string[] EvidenceClasses = ["api_response", "observed_surface", "unknown"];

    // What differs between two snapshots of the same data: the token, its times (dataCutoff is the reputation view's name
    // for the snapshot time) and the watermarks. dataUpdatedAt is the time of the latest analysis of the brand, whatever
    // its source.
    private static readonly HashSet<string> SnapshotMembers =
        ["snapshotToken", "asOf", "expiresAt", "arrivalWatermark", "analysisWatermark", "dataUpdatedAt", "snapshotExpiresAt", "dataCutoff"];

    private static SiteTargetWrite App(string engine) => new(AppProvider, engine, "app", "CN", "zh-CN", true, null, MonitoringFrequency.Weekly);

    private static (string, int) Code(CollectorStoreException exception) => (exception.Code, exception.StatusCode);

    private static string Normalized(object? value)
    {
        var node = JsonSerializer.SerializeToNode(value, Api);
        Strip(node);
        return node?.ToJsonString() ?? "null";
    }

    private static void Strip(JsonNode? node)
    {
        if (node is JsonObject item)
            foreach (var name in item.Select(member => member.Key).ToArray())
            {
                if (SnapshotMembers.Contains(name)) item.Remove(name);
                else Strip(item[name]);
            }
        else if (node is JsonArray list)
            foreach (var element in list) Strip(element);
    }

    private sealed class DevelopmentEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    /// <summary>The brand of a case, its two prompts and every reader used by the read side, all on real stores.</summary>
    private sealed record World(Rig Rig, Guid Site, Guid[] Prompts, DateTimeOffset From, DateTimeOffset To, AnalysisStore Analyzer,
        AnalyticsQueryStore Analytics, ContentStore Content, ArticleMonitoringQuery Monitoring, TeamSummaryQuery Team, CreditStore Credits)
    {
        public Guid Tenant => Rig.Tenant;
        public string Cs => Rig.Cs;

        /// <summary>The "all sources" default of the monitoring, dashboard, reputation and prompt routes.</summary>
        public AnalyticsFilterRequest All => new(From, To, "UTC", AnalyticsSourceDefaults.Providers, CaptureModes: AnalyticsSourceDefaults.CaptureModes,
            EvidenceClasses: AnalyticsSourceDefaults.EvidenceClasses);

        /// <summary>What the frontend sends for the App view: captureMode=app&amp;provider=app_collector.</summary>
        public AnalyticsFilterRequest AppView => new(From, To, "UTC", [AppProvider], CaptureModes: [AppChannelDefinition.CaptureMode],
            EvidenceClasses: EvidenceClasses);

        /// <summary>No source at all: the store default of the legacy routes, and the window of the citation summary.</summary>
        public AnalyticsFilterRequest Window => new(From, To, "UTC");
    }

    // The analysis queue is global. A case only processes work when nothing of another tenant is waiting, so that it
    // never analyses (and so changes the row counts of) someone else's checks.
    private static async Task AnalyzeAsync(World world)
    {
        Assert.Equal(0, await CountAsync(world.Cs, """
            SELECT COUNT(*) FROM analysis_check_work WHERE tenant_id <> UUID_TO_BIN(@tenant) AND status IN ('pending', 'processing', 'failed');
            """, ("@tenant", world.Tenant.ToString())));
        for (var pass = 0; pass < 10 && await world.Analyzer.ProcessPendingAsync("app-analytics-test", 100) > 0; pass++) { }
        Assert.Equal(0, await CountAsync(world.Cs, """
            SELECT COUNT(*) FROM analysis_check_work WHERE tenant_id = UUID_TO_BIN(@tenant) AND status <> 'completed';
            """, ("@tenant", world.Tenant.ToString())));
    }

    // A brand with a competitor, two prompts and the App targets doubao and kimi; its weekly App run exists (four open
    // checks) before any API target is saved, so that no API first run is started by the case.
    private static async Task<(World World, Guid AppRun)> SeedWorldAsync(Pool pool, Guid tenant)
    {
        var rig = Build(pool.Cs, tenant);
        var site = (await rig.Store.CreateBrandAsync(tenant, new BrandWrite(Brand, OwnDomain, DefaultCountry: "CN", DefaultLocale: "zh-CN",
            TimeZone: "UTC", IncludeSubdomains: false, Description: "测试品牌提供记账工具。"))).SiteId;
        await rig.Store.CreateCompetitorAsync(tenant, site, new CompetitorWrite(Competitor, Domains: ["betaledger.test"]));
        Guid[] prompts = [await SeedPromptAsync(pool.Cs, site, "哪个记账工具好用？", 1), await SeedPromptAsync(pool.Cs, site, "小团队用什么记账软件？", 2)];
        await rig.PutAsync(site, App("doubao"), App("kimi"));
        var appRun = (await RunSlotAsync(rig, await StartAsync(rig, site), 3)).RunId!.Value;
        await ExecAsync(pool.Cs, "UPDATE collector_tasks SET deadline_at = UTC_TIMESTAMP(6) + INTERVAL 6 DAY WHERE run_id = UUID_TO_BIN(@run);",
            ("@run", appRun.ToString()));
        await rig.PutAsync(site, ChatGpt, App("doubao"), App("kimi"));
        // Only the App plan exists: saving an API target afterwards starts no API run.
        Assert.Equal(1, await CountAsync(pool.Cs, "SELECT COUNT(*) FROM visibility_runs WHERE tenant_id = UUID_TO_BIN(@tenant);", ("@tenant", tenant.ToString())));

        var database = rig.Database;
        var analyzer = new AnalysisStore(database);
        var analytics = new AnalyticsQueryStore(database, analyzer);
        var credits = new CreditStore(database);
        var research = StudioFakes.Of<IResearchStore>((method, _) => method == nameof(IResearchStore.ListProviderSettingsAsync)
            ? Task.FromResult<IReadOnlyList<ResearchProviderSettingsView>>([]) : throw new InvalidOperationException(method));
        var knowledge = StudioFakes.Of<IKnowledgeStore>((method, _) => method switch
        {
            nameof(IKnowledgeStore.ListFoldersAsync) => Task.FromResult<IReadOnlyList<KnowledgeFolderView>>([]),
            nameof(IKnowledgeStore.ListDocumentsAsync) => Task.FromResult(new CursorPage<KnowledgeDocumentView>([], null)),
            _ => throw new InvalidOperationException(method),
        });
        var content = new ContentStore(database, rig.Store, credits, research, null!, knowledge, null!);
        var monitoring = new ArticleMonitoringQuery(analytics, database);
        var now = DateTimeOffset.UtcNow;
        return (new World(rig, site, prompts, now.AddDays(-2), now.AddMinutes(30), analyzer, analytics, content, monitoring,
            new TeamSummaryQuery(rig.Store, analytics, content, monitoring), credits), appRun);
    }

    // One finished run of a non-App provider with one check (API answers are written directly: their intake is not the subject).
    private static async Task<Guid> SeedSampleAsync(World world, Guid prompt, string provider, string engine, string capture, string evidence,
        string country, string locale, string status, string? answer, DateTimeOffset captured, params string[] citations)
    {
        var (run, target, check) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await using var connection = new MySqlConnection(world.Cs);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var insert = new MySqlCommand("""
            INSERT INTO visibility_runs (id, tenant_id, site_id, status, idempotency_key, requested_at, total_checks, succeeded_checks, failed_checks, created_at, updated_at)
            VALUES (UUID_TO_BIN(@run), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), @runStatus, @key, UTC_TIMESTAMP(6), 1, @succeeded, 1 - @succeeded,
                    UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
            INSERT INTO run_engines (id, run_id, engine, provider, target_key, capture_mode, country, locale, created_at)
            VALUES (UUID_TO_BIN(@target), UUID_TO_BIN(@run), @engine, @provider, @targetKey, @capture, @country, @locale, UTC_TIMESTAMP(6));
            INSERT INTO visibility_checks (id, tenant_id, site_id, run_id, prompt_id, engine, provider, status, prompt_snapshot, answer, answer_present,
                captured_at, capture_mode, evidence_class, country, locale, run_target_id, sample_index, prompt_version_id, prompt_tags_snapshot,
                branded_status_snapshot, subject_config_version, duration_ms, created_at, updated_at)
            SELECT UUID_TO_BIN(@check), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), UUID_TO_BIN(@run), p.id, @engine, @provider, @status, p.content,
                @answer, @present, @captured, @capture, @evidence, @country, @locale, UUID_TO_BIN(@target), 1, p.current_version_id, JSON_ARRAY(),
                'unbranded', b.subject_config_version, 1200, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)
            FROM prompts p JOIN site_brand_settings b ON b.site_id = p.site_id WHERE p.id = UUID_TO_BIN(@prompt);
            """, connection, transaction))
        {
            foreach (var (name, value) in new (string, object?)[]
            {
                ("@run", run.ToString()), ("@tenant", world.Tenant.ToString()), ("@site", world.Site.ToString()), ("@key", Guid.NewGuid().ToString()),
                ("@runStatus", status == "succeeded" ? "succeeded" : "failed"), ("@succeeded", status == "succeeded" ? 1 : 0),
                ("@target", target.ToString()), ("@engine", engine), ("@provider", provider),
                ("@targetKey", CollectionIdentity.TargetKey(provider, engine, capture, country, locale)), ("@capture", capture), ("@country", country),
                ("@locale", locale), ("@check", check.ToString()), ("@status", status), ("@answer", answer), ("@present", !string.IsNullOrWhiteSpace(answer)),
                ("@captured", captured.UtcDateTime), ("@evidence", evidence), ("@prompt", prompt.ToString()),
            })
                insert.Parameters.AddWithValue(name, value ?? DBNull.Value);
            Assert.Equal(3, await insert.ExecuteNonQueryAsync());
        }
        for (var i = 0; i < citations.Length; i++)
        {
            await using var cite = new MySqlCommand("""
                INSERT INTO citations (id, check_id, normalized_url, source_domain, position, created_at)
                VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@check), @url, @domain, @position, UTC_TIMESTAMP(6));
                """, connection, transaction);
            cite.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
            cite.Parameters.AddWithValue("@check", check.ToString());
            cite.Parameters.AddWithValue("@url", citations[i]);
            cite.Parameters.AddWithValue("@domain", new Uri(citations[i]).Host);
            cite.Parameters.AddWithValue("@position", i + 1);
            await cite.ExecuteNonQueryAsync();
        }
        await AnalysisEnqueue.QueueAsync(connection, transaction, check, CancellationToken.None);
        await transaction.CommitAsync();
        return check;
    }

    /// <summary>
    /// Four samples that are not App samples: Cloro (one answer, one failure), the domestic web collector and the Doubao API.
    /// The web and the API sample have the engine "doubao", the same as the App samples.
    /// </summary>
    private static async Task<Guid[]> SeedApiSamplesAsync(World world)
    {
        var at = DateTimeOffset.UtcNow.AddMinutes(-20);
        Guid[] checks =
        [
            await SeedSampleAsync(world, world.Prompts[0], "cloro", "chatgpt", "scraped", "observed_surface", "US", "en-US", "succeeded",
                $"{Brand} is ahead of {Competitor} for bookkeeping.", at, $"https://{OwnDomain}/guide", "https://newsdesk.test/a"),
            await SeedSampleAsync(world, world.Prompts[1], "cloro", "chatgpt", "scraped", "observed_surface", "US", "en-US", "failed", null, at.AddMinutes(1)),
            await SeedSampleAsync(world, world.Prompts[0], "domestic_collector", "doubao", "scraped", "observed_surface", "CN", "zh-CN", "succeeded",
                $"{Competitor} leads while {Brand} follows.", at.AddMinutes(2), "https://betaledger.test/x"),
            await SeedSampleAsync(world, world.Prompts[1], "doubao_api", "doubao", "api", "api_response", "CN", "zh-CN", "succeeded",
                $"Only {Competitor} is worth using.", at.AddMinutes(3), "https://newsdesk.test/a"),
        ];
        await AnalyzeAsync(world);
        return checks;
    }

    /// <summary>Leases the next doubao task of the pool's binding and submits an accepted App answer for it.</summary>
    private static async Task<AppLeasedTask> CollectAsync(Pool pool, AppCollectorPoolStore store, string node, Guid binding, string answer,
        params AppResultCitation[] citations)
    {
        await pool.AgePlatformAsync("doubao");
        await pool.AgeBindingAsync(binding);
        var task = (await store.LeaseAsync(new AppLeaseCommand(node, binding, 120))).Task!;
        Assert.Equal(new CollectorResultResponse(CollectorResultDisposition.Accepted, task.CheckId), await store.SubmitResultAsync(
            Result(node, task, $"analytics-{task.Id:N}", answer, citations) with { SentAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow }));
        return task;
    }

    /// <summary>Every entry that reads samples without being asked for the App source, serialized without the snapshot members.</summary>
    private static async Task<SortedDictionary<string, string>> DefaultViewsAsync(World w)
    {
        var (t, s, a) = (w.Tenant, w.Site, w.Analytics);
        var citation = await a.CitationDomainSummaryAsync(t, s, w.Window, null);
        return new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            // "All sources" routes.
            ["monitoring/summary"] = Normalized(await a.MonitoringSummaryAsync(t, s, w.All, null)),
            ["monitoring/models"] = Normalized(await a.MonitoringModelsAsync(t, s, w.All, null)),
            ["monitoring/records"] = Normalized(await a.MonitoringRecordsAsync(t, s, w.All, null, null, 100)),
            ["analytics/brand-dashboard"] = Normalized(await a.BrandDashboardAsync(t, s, w.All, null)),
            ["analytics/prompts"] = Normalized(await a.PromptsAsync(t, s, w.All, null, null, 100, null)),
            ["analytics/reputation"] = Normalized(await a.ReputationAsync(t, s, w.All, null, null)),
            // Legacy routes: no source in the request, the store default applies.
            ["analytics/overview"] = Normalized(await a.OverviewAsync(t, s, w.Window, null)),
            ["analytics/visibility"] = Normalized(await a.VisibilityAsync(t, s, w.Window, null)),
            ["analytics/share-of-voice"] = Normalized(await a.ShareOfVoiceAsync(t, s, w.Window, null, 10)),
            ["analytics/citations"] = Normalized(await a.CitationsAsync(t, s, w.Window, null)),
            // The citation-domain family without the channel parameter.
            ["citation-domain-summary"] = Normalized(citation),
            ["citation-domain-summary/domains"] = Normalized(await a.CitationDomainSummaryDomainsAsync(t, s, citation.Meta.SnapshotToken, null, null, null, null, 100)),
            ["citation-domain-summary/urls"] = Normalized(await a.CitationDomainSummaryUrlsAsync(t, s, citation.Meta.SnapshotToken, null, null, 100)),
            // The entries that offer no App view at all.
            ["team/summary"] = Normalized(await w.Team.SummaryAsync(t, Guid.NewGuid(), new TeamSummaryRequest(w.From, w.To, "UTC", SiteId: s))),
            ["article-studio/share-of-voice"] = Normalized(await a.ShareOfVoiceAsync(t, s,
                AnalyticsQueryStore.AllSourcesRequest(w.From, w.To, [w.Prompts[0]]), null, 100)),
            ["article-studio/cited-urls"] = Normalized(await a.TopCitedUrlsForPromptAsync(t, s, w.Prompts[0], w.From, w.To, 10)),
            ["article-generation/options.targets"] = Normalized((await w.Content.GetGenerationOptionsAsync(t, s)).Targets),
        };
    }

    // 7.7, the main case.
    [AppChannelMySqlFact]
    public Task AppSamplesLeaveEveryDefaultViewUnchangedAndAreReadOnlyWhenTheAppSourceIsNamed() => PoolAsync(async (pool, seedTenant) =>
    {
        var (w, appRun) = await SeedWorldAsync(pool, await seedTenant());
        var (t, s, a) = (w.Tenant, w.Site, w.Analytics);
        var api = await SeedApiSamplesAsync(w);

        // Before any App sample: the brand already has the App targets enabled and an open App run.
        var before = await DefaultViewsAsync(w);
        Assert.Equal(4, (await a.MonitoringSummaryAsync(t, s, w.All, null)).TotalChecks);
        Assert.Equal(0, (await a.MonitoringSummaryAsync(t, s, w.AppView, null)).TotalChecks);

        // Two App answers on doubao. The first cites a page of the brand's own site, a source without a URL and a third
        // party; the second cites another page of the brand's own site.
        var store = pool.Store(w.Tenant);
        var (node, _) = await pool.NodeAsync("mac");
        var label = $"doubao-acct-{Guid.NewGuid():N}";
        var binding = await pool.BindAsync(node, "AI_Apps_4KB", "doubao", label);
        const string firstAnswer = "Acmebook 是首选，Betaledger 其次。";
        var first = await CollectAsync(pool, store, node, binding, firstAnswer, new AppResultCitation("Acmebook 博客", Brand, $"https://{OwnDomain}/blog/app-only", "cited"),
            new AppResultCitation("知乎回答", "知乎", null, "cited"), new AppResultCitation("行业报告", null, "https://reportshub.test/2026", "more"));
        var second = await CollectAsync(pool, store, node, binding, "Acmebook 更适合小团队。",
            new AppResultCitation("发现页", Brand, $"https://{OwnDomain}/blog/discovered", "cited"));
        Assert.Equal((w.Prompts[0], w.Prompts[1]), (first.PromptId, second.PromptId));
        Guid[] app = [first.CheckId, second.CheckId];
        await AnalyzeAsync(w);

        // ---- The default view of every entry is what it was before the App samples were written. ----
        var after = await DefaultViewsAsync(w);
        Assert.Equal(before.Keys, after.Keys);
        foreach (var (entry, value) in before) Assert.True(value == after[entry], $"{entry} changed:\n before {value}\n after  {after[entry]}");
        // None of them names the App source or an App check.
        foreach (var (entry, value) in after)
        {
            Assert.False(value.Contains(AppProvider, StringComparison.Ordinal), $"{entry} names the App provider");
            foreach (var check in app) Assert.False(value.Contains(check.ToString(), StringComparison.OrdinalIgnoreCase), $"{entry} contains an App check");
        }

        // ---- The App view: only App samples. ----
        var appRecords = await a.MonitoringRecordsAsync(t, s, w.AppView, null, null, 100);
        Assert.Equal(app.Order(), appRecords.Items.Select(row => row.CheckId).Order());
        Assert.Equal(2, appRecords.TotalCount);
        Assert.All(appRecords.Items, row => Assert.Equal((AppProvider, "doubao", "app", CollectionChannel.App, "succeeded", null),
            (row.Provider, row.Engine, row.CaptureMode, row.Channel, row.CheckStatus, row.ErrorCode)));
        var firstRow = appRecords.Items.Single(row => row.CheckId == first.CheckId);
        Assert.Equal((2L, firstAnswer, AnswerMode.Fast), (firstRow.CitationCount, firstRow.AnswerSummary, firstRow.AnswerMode));
        var listJson = JsonSerializer.Serialize(appRecords, Api);
        Assert.Contains("\"channel\":\"app\"", listJson);
        Assert.Contains("\"captureMode\":\"app\"", listJson);
        Assert.DoesNotContain(label, listJson);

        // The default list: the four other samples, each with its own channel.
        var defaultRecords = await a.MonitoringRecordsAsync(t, s, w.All, null, null, 100);
        Assert.Equal(api.Order(), defaultRecords.Items.Select(row => row.CheckId).Order());
        Assert.Equal(["cloro|scraped|Api", "cloro|scraped|Api", "domestic_collector|scraped|Web", "doubao_api|api|Api"],
            defaultRecords.Items.Select(row => $"{row.Provider}|{row.CaptureMode}|{row.Channel}").Order(StringComparer.Ordinal));
        // The failed Cloro check is listed with its status; it has no error code in this fixture.
        Assert.Equal("failed", defaultRecords.Items.Single(row => row.CheckId == api[1]).CheckStatus);

        var appSummary = await a.MonitoringSummaryAsync(t, s, w.AppView, null);
        var defaultSummary = await a.MonitoringSummaryAsync(t, s, w.All, null);
        // Two App answers, both mention the brand; the default view has three answers, two of which mention it.
        Assert.Equal((2L, MetricRatio.Of(2, 2)), (appSummary.TotalChecks, appSummary.BrandVisibility));
        Assert.Equal((4L, MetricRatio.Of(2, 3)), (defaultSummary.TotalChecks, defaultSummary.BrandVisibility));
        var appDashboard = await a.BrandDashboardAsync(t, s, w.AppView, null);
        var defaultDashboard = await a.BrandDashboardAsync(t, s, w.All, null);
        Assert.Equal((2L, MetricRatio.Of(2, 2), MetricRatio.Of(2, 2)),
            (appDashboard.Metrics.EligibleChecks, appDashboard.Metrics.BrandMentionRate, appDashboard.Metrics.OwnExternalCitationRate));
        Assert.Equal((3L, MetricRatio.Of(2, 3), MetricRatio.Of(1, 3)),
            (defaultDashboard.Metrics.EligibleChecks, defaultDashboard.Metrics.BrandMentionRate, defaultDashboard.Metrics.OwnExternalCitationRate));
        // Prompt performance: one source group per prompt in the App view, and it is the App source.
        var appPrompts = await a.PromptsAsync(t, s, w.AppView, null, null, 100, null);
        Assert.Equal([AppSource, AppSource], appPrompts.Items.Select(row => row.Source));
        Assert.Equal(w.Prompts.Order(), appPrompts.Items.Select(row => row.PromptId).Order());

        // ---- 7.1: app together with another capture mode is refused by every entry that takes a source filter. ----
        foreach (var modes in new[] { new[] { "api", "app" }, ["app", "scraped"], ["api", "scraped", "app"] })
        {
            var mixed = w.AppView with { Providers = [.. AnalyticsSourceDefaults.Providers, AppProvider], CaptureModes = modes };
            foreach (var read in new Func<Task>[]
            {
                () => a.MonitoringSummaryAsync(t, s, mixed, null), () => a.MonitoringModelsAsync(t, s, mixed, null),
                () => a.MonitoringRecordsAsync(t, s, mixed, null, null, 20), () => a.BrandDashboardAsync(t, s, mixed, null),
                () => a.PromptsAsync(t, s, mixed, null, null, 20, null), () => a.ReputationAsync(t, s, mixed, null, null),
                () => a.OverviewAsync(t, s, mixed, null), () => a.VisibilityAsync(t, s, mixed, null), () => a.CitationsAsync(t, s, mixed, null),
            })
                Assert.Equal(("INVALID_FILTER", 400), Code(await Assert.ThrowsAsync<CollectorStoreException>(read)));
        }
        // A refused request writes no snapshot.
        var snapshots = await CountAsync(w.Cs, "SELECT COUNT(*) FROM analytics_snapshots WHERE tenant_id = UUID_TO_BIN(@tenant);", ("@tenant", t.ToString()));
        await Assert.ThrowsAsync<CollectorStoreException>(() => a.MonitoringSummaryAsync(t, s, w.AppView with { CaptureModes = ["app", "api"] }, null));
        Assert.Equal(snapshots, await CountAsync(w.Cs, "SELECT COUNT(*) FROM analytics_snapshots WHERE tenant_id = UUID_TO_BIN(@tenant);", ("@tenant", t.ToString())));
        // Naming only one half of the App source reads nothing, never a mixture.
        Assert.Equal(0, (await a.MonitoringSummaryAsync(t, s, w.All with { Providers = [AppProvider] }, null)).TotalChecks);
        Assert.Equal(0, (await a.MonitoringSummaryAsync(t, s, w.All with { CaptureModes = ["app"] }, null)).TotalChecks);
        // A token of the App view cannot be reused with another source.
        Assert.Equal(("INVALID_FILTER", 400), Code(await Assert.ThrowsAsync<CollectorStoreException>(()
            => a.MonitoringSummaryAsync(t, s, w.All, appSummary.Meta.SnapshotToken))));

        // ---- 7.6a: enabled App targets are model and reputation candidates in the App view only. ----
        var defaultModels = await a.MonitoringModelsAsync(t, s, w.All, null);
        Assert.DoesNotContain(defaultModels, model => model.Provider == AppProvider || model.CaptureMode == "app");
        Assert.Contains(defaultModels, model => (model.Provider, model.Engine) == ("cloro", "chatgpt"));
        var appModels = await a.MonitoringModelsAsync(t, s, w.AppView, null);
        // doubao has the two samples; kimi is enabled and has none yet.
        Assert.Equal(["doubao|2|insufficient_data", "kimi|0|idle"],
            appModels.Select(model => $"{model.Engine}|{model.RangeChecks}|{model.Status}").Order(StringComparer.Ordinal));
        Assert.All(appModels, model => Assert.Equal((AppProvider, "app", "CN", "zh-CN"), (model.Provider, model.CaptureMode, model.Country, model.Locale)));
        Assert.Equal(2, appSummary.ActiveModelCount);
        Assert.Equal(defaultModels.Count(model => model.Status != "disabled"), defaultSummary.ActiveModelCount);
        var defaultReputation = await a.ReputationAsync(t, s, w.All, null, null);
        Assert.DoesNotContain(defaultReputation.Platforms, platform => platform.Provider == AppProvider);
        Assert.DoesNotContain(AppProvider, JsonSerializer.Serialize(defaultReputation.PlatformsWithoutData, Api));
        var appReputation = JsonSerializer.Serialize(await a.ReputationAsync(t, s, w.AppView, null, null), Api);
        Assert.Contains($"{AppProvider}|doubao|app", appReputation);
        Assert.Contains($"{AppProvider}|kimi|app", appReputation);
        Assert.DoesNotContain("cloro", appReputation);

        // ---- 7.2: the detail of an App answer. ----
        var detail = (await a.MonitoringAnswerDetailAsync(t, s, first.CheckId, appRecords.Meta.SnapshotToken, null))!;
        Assert.Equal((AppProvider, "app", CollectionChannel.App, "succeeded", firstAnswer, w.Prompts[0], true),
            (detail.Provider, detail.CaptureMode, detail.Channel, detail.Status, detail.Answer!.Text, detail.PromptId, detail.MatchesListFilters));
        Assert.Equal(new AppAnswerView("AI_Apps_4KB", "15", "com.example.app", "9.9.9", AppVisibleState.On, AppVisibleState.Off, AppCitationsStatus.Read, null,
            3, 2, false, Sha, AppPromptRules.RuleVersion), detail.App);
        Assert.Equal([new AppSourceWithoutUrlView("知乎回答", "知乎", "cited")], detail.SourcesWithoutUrl);
        // Only the two sources with a URL are citations.
        Assert.Equal([$"https://{OwnDomain}/blog/app-only", "https://reportshub.test/2026"], detail.Citations.Select(citation => citation.Url));
        var detailJson = JsonSerializer.Serialize(detail, Api);
        // The label is on the stored row and not in the response.
        Assert.Equal(1, await CountAsync(w.Cs, "SELECT COUNT(*) FROM visibility_checks WHERE id = UUID_TO_BIN(@id) AND CAST(result_metadata AS CHAR) LIKE CONCAT('%', @label, '%');",
            ("@id", first.CheckId.ToString()), ("@label", label)));
        Assert.DoesNotContain(label, detailJson);
        Assert.DoesNotContain("accountLabel", detailJson, StringComparison.OrdinalIgnoreCase);
        // The list row and the detail agree; an App answer is readable under a token of the default view too (the detail
        // does not filter by source), where it does not match the list filters.
        Assert.Equal(firstRow.BrandMetrics, detail.BrandMetrics);
        var foreign = (await a.MonitoringAnswerDetailAsync(t, s, first.CheckId, defaultRecords.Meta.SnapshotToken, null))!;
        Assert.Equal((CollectionChannel.App, false, firstAnswer), (foreign.Channel, foreign.MatchesListFilters, foreign.Answer!.Text));
        // And an API answer has no App fields.
        var apiDetail = (await a.MonitoringAnswerDetailAsync(t, s, api[2], defaultRecords.Meta.SnapshotToken, null))!;
        Assert.Equal(("domestic_collector", "scraped", CollectionChannel.Web), (apiDetail.Provider, apiDetail.CaptureMode, apiDetail.Channel));
        Assert.Null(apiDetail.App);
        Assert.Null(apiDetail.SourcesWithoutUrl);

        // ---- 7.3: the citation-domain family by channel. ----
        var plain = await a.CitationDomainSummaryAsync(t, s, w.Window, null);
        var explicitApi = await a.CitationDomainSummaryAsync(t, s, w.Window, null, "api");
        // channel=api is the request without the parameter: the same frozen filter and the same numbers.
        Assert.Equal(Normalized(plain), Normalized(explicitApi));
        Assert.Equal(AnalyticsQueryStore.DefaultProviders.Order(StringComparer.Ordinal), plain.Meta.Filters.Providers);
        Assert.Equal(["api", "scraped"], plain.Meta.Filters.CaptureModes);
        Assert.Equal(JsonSerializer.Serialize(plain.Meta.Filters, Api), JsonSerializer.Serialize(explicitApi.Meta.Filters, Api));
        Assert.Equal(3, plain.Metrics.EligibleAnswers);
        var apiToken = plain.Meta.SnapshotToken;
        async Task<string[]> DomainsAsync(string token, string? channel)
            => (channel is null ? await a.CitationDomainSummaryDomainsAsync(t, s, token, null, null, null, null, 100)
                : await a.CitationDomainSummaryDomainsAsync(t, s, token, null, null, null, null, 100, channel))
                .Items.Select(row => $"{row.Domain}|{row.CitationEvents}").Order(StringComparer.Ordinal).ToArray();
        string[] apiDomains = [$"{OwnDomain}|1", "betaledger.test|1", "newsdesk.test|2"];
        Assert.Equal(apiDomains, await DomainsAsync(apiToken, null));
        Assert.Equal(apiDomains, await DomainsAsync(apiToken, "api"));
        Assert.Equal(apiDomains, await DomainsAsync(explicitApi.Meta.SnapshotToken, null));

        var appCitations = await a.CitationDomainSummaryAsync(t, s, w.Window, null, "app");
        Assert.Equal([AppProvider], appCitations.Meta.Filters.Providers);
        Assert.Equal(["app"], appCitations.Meta.Filters.CaptureModes);
        Assert.Equal(plain.Meta.Filters.EvidenceClasses, appCitations.Meta.Filters.EvidenceClasses);
        Assert.Equal((2L, 3L, 2L, 3L), (appCitations.Metrics.EligibleAnswers, appCitations.Metrics.CitationEvents, appCitations.Metrics.DistinctDomains,
            appCitations.Metrics.DistinctUrls));
        var appToken = appCitations.Meta.SnapshotToken;
        Assert.Equal([$"{OwnDomain}|2", "reportshub.test|1"], await DomainsAsync(appToken, "app"));
        var appUrls = await a.CitationDomainSummaryUrlsAsync(t, s, appToken, null, null, null, null, null, 100, "app");
        Assert.Equal([$"https://{OwnDomain}/blog/app-only", $"https://{OwnDomain}/blog/discovered", "https://reportshub.test/2026"],
            appUrls.Items.Select(row => row.Url).Order(StringComparer.Ordinal));
        var appEvidence = await a.CitationDomainSummaryEvidenceAsync(t, s, appToken, OwnDomain, null, 20, "app");
        Assert.Equal(app.Order(), appEvidence.Items.Select(item => item.CheckId).Order());
        Assert.Equal(2, (await a.CitationDomainDetailAsync(t, s, appToken, OwnDomain, null, "app")).CitationEvents);
        Assert.Equal(1, (await a.CitationUrlPromptsAsync(t, s, appToken, appUrls.Items.Single(row => row.Url.EndsWith("/app-only", StringComparison.Ordinal)).UrlHash,
            null, "app")).CitationEvents);
        // The token is compared with the chosen channel: an App token needs channel=app on every route of the family, and
        // an API token is refused with it.
        foreach (var read in new Func<Task>[]
        {
            () => a.CitationDomainSummaryAsync(t, s, w.Window, appToken), () => a.CitationDomainSummaryAsync(t, s, w.Window, appToken, "api"),
            () => a.CitationDomainSummaryDomainsAsync(t, s, appToken, null, null, null, null, 100),
            () => a.CitationDomainSummaryEvidenceAsync(t, s, appToken, OwnDomain, null, 20),
            () => a.CitationDomainSummaryUrlsAsync(t, s, appToken, null, null, 100),
            () => a.CitationDomainDetailAsync(t, s, appToken, OwnDomain, null),
            () => a.CitationDomainSummaryAsync(t, s, w.Window, apiToken, "app"),
            () => a.CitationDomainSummaryDomainsAsync(t, s, apiToken, null, null, null, null, 100, "app"),
            () => a.CitationDomainSummaryEvidenceAsync(t, s, apiToken, OwnDomain, null, 20, "app"),
            () => a.CitationDomainSummaryUrlsAsync(t, s, apiToken, null, null, null, null, null, 100, "app"),
            () => a.CitationDomainDetailAsync(t, s, apiToken, OwnDomain, null, "app"),
        })
            Assert.Equal(("INVALID_FILTER", 400), Code(await Assert.ThrowsAsync<CollectorStoreException>(read)));
        // The same App token still works with its own channel after the refusals.
        Assert.Equal(2, (await a.CitationDomainSummaryAsync(t, s, w.Window, appToken, "app")).Metrics.EligibleAnswers);
        // The records filter by cited domain follows the source of the list it is applied to.
        Assert.Equal(2, (await a.MonitoringRecordsAsync(t, s, w.AppView, null, null, 20, null, null, OwnDomain)).TotalCount);
        Assert.Equal(1, (await a.MonitoringRecordsAsync(t, s, w.All, null, null, 20, null, null, OwnDomain)).TotalCount);

        // ---- 7.5: the settled credits of the two App checks are prompt monitoring of the App channel. ----
        var usage = await w.Credits.UsageAsync(t, null, new CreditUsageRequest(TimeZone: "UTC"));
        Assert.Equal([new CreditUsageItem(CreditUsageType.PromptMonitoring, 20m, 2, CollectionChannel.App)], usage.Totals);
        Assert.Equal((CreditUsageType.PromptMonitoring, 10m, (CollectionChannel?)CollectionChannel.App), (usage.Latest!.Type, usage.Latest.Credits, usage.Latest.Channel));

        // ---- 7.6: the entries without an App view do not see the enabled App targets either. ----
        var targets = await w.Rig.Store.GetSiteTargetsAsync(t, s);
        Assert.Equal(2, targets.Items.Count(target => target.Enabled && target.Provider == AppProvider));
        var team = Assert.Single((await w.Team.SummaryAsync(t, Guid.NewGuid(), new TeamSummaryRequest(w.From, w.To, "UTC", SiteId: s))).Brands);
        Assert.Equal(["cloro|chatgpt"], team.EnabledTargets.Select(target => $"{target.Provider}|{target.Engine}"));
        Assert.Equal(MetricRatio.Of(2, 3).Value * 100, team.MentionRate.Current);
        var options = (await w.Content.GetGenerationOptionsAsync(t, s)).Targets;
        Assert.DoesNotContain(options, target => target.Provider == AppProvider);
        Assert.Contains(options, target => (target.Provider, target.Engine, target.Enabled) == ("cloro", "chatgpt", true));
        Assert.Equal(targets.Items.Count(target => target.Provider != AppProvider), options.Count);

        _ = appRun;
    });
}
