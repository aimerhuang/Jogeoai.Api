using System.Text.Json;
using Jogeoai.Api;
using Jogeoai.Api.Controllers;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Jogeoai.Providers.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using static Jogeoai.Tests.AppCycleRunSqlTests;
using static Jogeoai.Tests.AppLeaseSqlTests;
using static Jogeoai.Tests.TeamMonitoringConfigSqlTests;

namespace Jogeoai.Tests;

/// <summary>
/// 三处文章相关功能不筛选来源（7.4，results.md 8.2）：带 URL 的 App 引用进入文章监控、文章列表的被引次数和引用发现，
/// 并能从证据行追溯到提问和回答；以及 Run 列表按 app_collector 筛选、Run 摘要把未采集算作已完成（7.5a）。
/// </summary>
public sealed partial class AppAnalyticsSqlTests
{
    // auth_users has no tenant column, so the tenant clean-up does not remove these rows: the case removes them itself.
    private static async Task<(Guid User, Guid Owner)> SeedUsersAsync(string cs, Guid tenant)
    {
        var (user, owner) = (Guid.NewGuid(), Guid.NewGuid());
        await ExecAsync(cs, """
            INSERT INTO auth_users (id, email, password_hash, status, created_at, updated_at)
            VALUES (UUID_TO_BIN(@user), @email, 'not-a-login', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)),
                   (UUID_TO_BIN(@owner), @ownerEmail, 'not-a-login', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
            INSERT INTO tenant_members (tenant_id, user_id, role, status, created_at, updated_at)
            VALUES (UUID_TO_BIN(@tenant), UUID_TO_BIN(@owner), 'owner', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)),
                   (UUID_TO_BIN(@tenant), UUID_TO_BIN(@user), 'member', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
            """, ("@tenant", tenant.ToString()), ("@user", user.ToString()), ("@owner", owner.ToString()),
            ("@email", $"app-analytics-{user:N}@example.test"), ("@ownerEmail", $"app-analytics-{owner:N}@example.test"));
        return (user, owner);
    }

    private static Task DeleteUsersAsync(string cs, params Guid[] users)
        => ExecAsync(cs, $"""
            SET SESSION foreign_key_checks = 0;
            DELETE FROM auth_users WHERE id IN ({string.Join(",", users.Select(user => $"UUID_TO_BIN('{user}')"))});
            SET SESSION foreign_key_checks = 1;
            """);

    private static async Task<Guid> SeedTrackedArticleAsync(World w, Guid user, string title)
    {
        var article = Guid.NewGuid();
        await ExecAsync(w.Cs, """
            INSERT INTO content_articles (id,tenant_id,site_id,created_by,source_kind,status,title,language,monitor_status,created_at,updated_at)
            VALUES (UUID_TO_BIN(@article),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@user),'ai_generated','active',@title,'zh-CN','active',
                    UTC_TIMESTAMP(6) - INTERVAL 3 DAY, UTC_TIMESTAMP(6) - INTERVAL 3 DAY);
            """, ("@article", article.ToString()), ("@tenant", w.Tenant.ToString()), ("@site", w.Site.ToString()), ("@user", user.ToString()), ("@title", title));
        return article;
    }

    // The real run controller over the real stores; only the stores a read never uses are fakes that fail when called.
    private static VisibilityRunsController RunsController(World w)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Tenant-Id"] = w.Tenant.ToString();
        return new VisibilityRunsController(new DomesticCollectorStore(w.Rig.Database), w.Rig.Runs, StudioFakes.Unused<ICloroSettingsStore>(),
            StudioFakes.Unused<ICloroKeyPool>(), StudioFakes.Unused<IAnswerProviderSettingsStore>(), StudioFakes.Unused<IManagementStore>(),
            new VisibilityRunQueryStore(w.Rig.Database), new TenantContextResolver(new DevelopmentEnvironment()), w.Rig.App,
            new DevelopmentEnvironment(), StudioFakes.Unused<IResearchStore>())
            { ControllerContext = new ControllerContext { HttpContext = context } };
    }

    private static async Task<IReadOnlyList<VisibilityRunSummary>> RunsAsync(World w, string? provider)
        => Assert.IsType<CursorPage<VisibilityRunSummary>>(Assert.IsType<OkObjectResult>(
            (await RunsController(w).ListSiteRuns(w.Site, null, provider, null, null, null)).Result).Value).Items;

    private static async Task<VisibilityRunSummary> RunAsync(World w, Guid run)
        => Assert.IsType<VisibilityRunSummary>(Assert.IsType<OkObjectResult>((await RunsController(w).Get(run, default)).Result).Value);

    // 7.4 and 7.5a.
    [AppChannelMySqlFact]
    public Task AppCitationsWithUrlReachTheArticleFeaturesAndAreTraceableAndAppRunsAreListedWithNotCollectedChecks() => PoolAsync(async (pool, seedTenant) =>
    {
        var (w, appRun) = await SeedWorldAsync(pool, await seedTenant());
        var (t, s, a) = (w.Tenant, w.Site, w.Analytics);
        var (user, owner) = await SeedUsersAsync(w.Cs, t);
        try
        {
            var api = await SeedApiSamplesAsync(w);
            // An article of the brand whose published link no API answer cites.
            var tracked = await SeedTrackedArticleAsync(w, user, "App 才引用的文章");
            var appOnly = $"https://{OwnDomain}/blog/app-only";
            await w.Content.AddPublicationLinkAsync(t, s, tracked, user, new ArticlePublicationLinkWrite(appOnly, PublishedAt: DateTimeOffset.UtcNow.AddDays(-2)));
            var none = await w.Monitoring.SummaryAsync(t, s, null, w.From, w.To, null);
            Assert.Equal(((long?)0, (long?)0), (none.CitationEvents, none.CitedArticleCount));
            Assert.Empty(none.SourceGroups);

            var store = pool.Store(t);
            var (node, _) = await pool.NodeAsync("mac");
            var label = $"doubao-acct-{Guid.NewGuid():N}";
            var binding = await pool.BindAsync(node, "AI_Apps_4KB", "doubao", label);
            const string answer = "Acmebook 是首选，Betaledger 其次。";
            var first = await CollectAsync(pool, store, node, binding, answer, new AppResultCitation("Acmebook 博客", Brand, appOnly, "cited"),
                new AppResultCitation("知乎回答", "知乎", null, "cited"));
            var second = await CollectAsync(pool, store, node, binding, "Acmebook 更适合小团队。",
                new AppResultCitation("发现页", Brand, $"https://{OwnDomain}/blog/discovered", "cited"),
                // A source without a usable URL names the same page: it is not a citation and discovers nothing.
                new AppResultCitation("没有链接的自有页面", Brand, null, "cited"));
            await AnalyzeAsync(w);

            // ---- Article monitoring: the App answer counts, as its own source group. ----
            var summary = await w.Monitoring.SummaryAsync(t, s, null, w.From, w.To, null);
            Assert.Equal(((long?)1, (long?)1, (long?)1), (summary.CitationEvents, summary.ArticleCitationCount, summary.CitedArticleCount));
            Assert.Equal([$"{AppSource}=1"], summary.SourceGroups.Select(group => $"{group.Key}={group.Value}"));
            var evidence = Assert.Single((await w.Monitoring.EvidenceAsync(t, s, tracked, w.From, w.To, null, 20)).Items);
            Assert.Equal((tracked, first.CheckId, w.Prompts[0], AppProvider, "doubao", "app", CollectionChannel.App, appOnly),
                (evidence.ArticleId, evidence.CheckId, evidence.PromptId, evidence.Provider, evidence.Engine, evidence.CaptureMode, evidence.Channel, evidence.Url));
            var evidenceJson = JsonSerializer.SerializeToNode(evidence, Api)!;
            Assert.Equal(("app", "app"), (evidenceJson["captureMode"]!.GetValue<string>(), evidenceJson["channel"]!.GetValue<string>()));
            // From the evidence row to the question and the answer.
            var traced = (await a.MonitoringAnswerDetailAsync(t, s, evidence.CheckId, summary.SnapshotToken, null))!;
            Assert.Equal((evidence.PromptSnapshot, "哪个记账工具好用？", answer, CollectionChannel.App),
                (traced.PromptSnapshot, traced.PromptSnapshot, traced.Answer!.Text, traced.Channel));
            Assert.Contains(traced.Citations, citation => citation.Url == appOnly);
            Assert.Equal([new AppSourceWithoutUrlView("知乎回答", "知乎", "cited")], traced.SourcesWithoutUrl);
            Assert.DoesNotContain(label, JsonSerializer.Serialize(traced, Api));

            // ---- The article list: the citation count includes the App answer. ----
            var listed = (await w.Content.ListArticlesAsync(t, s, new ArticleListFilter())).Items.Single(article => article.ArticleId == tracked);
            Assert.Equal((long?)1, listed.CitationSummary.CitationCount);
            Assert.Equal(["doubao"], listed.CitationSummary.Engines);

            // ---- Citation discovery: an unregistered page of the brand cited by an App answer becomes a tracked article. ----
            var links = new ArticleLinkMonitoringStore(w.Rig.Database, w.Rig.Store, StudioFakes.Unused<IArticleSourceReader>(),
                NullLogger<ArticleLinkMonitoringStore>.Instance);
            var pass = await links.DiscoverAsync(t, s, DateTimeOffset.UtcNow);
            Assert.Null(pass.SkipReason);
            // /guide (cited by the Cloro answer) and /blog/discovered (cited by the App answer) are new; /blog/app-only is registered.
            Assert.Equal((2, 1), (pass.Created, pass.SkippedExisting));
            var discovered = (await w.Content.ListArticlesAsync(t, s, new ArticleListFilter(SourceKind: ArticleSourceKind.CitationDiscovered))).Items;
            Assert.Equal(2, discovered.Count);
            var fromApp = discovered.Single(article => article.Discovery!.CheckId == second.CheckId);
            Assert.Equal((AppProvider, "doubao", "app", CollectionChannel.App, ArticleMonitorStatus.Active, owner),
                (fromApp.Discovery!.Provider, fromApp.Discovery.Engine, fromApp.Discovery.CaptureMode, fromApp.Discovery.Channel, fromApp.MonitorStatus, fromApp.CreatedBy));
            var fromApi = discovered.Single(article => article.Discovery!.CheckId == api[0]);
            Assert.Equal(("cloro", "chatgpt", "scraped", CollectionChannel.Api),
                (fromApi.Discovery!.Provider, fromApi.Discovery.Engine, fromApi.Discovery.CaptureMode, fromApi.Discovery.Channel));
            var discoveryJson = JsonSerializer.SerializeToNode(fromApp.Discovery, Api)!;
            Assert.Equal(("app", "app"), (discoveryJson["captureMode"]!.GetValue<string>(), discoveryJson["channel"]!.GetValue<string>()));
            // The new article keeps the check that first cited the link, and the detail returns it.
            var link = Assert.Single((await w.Content.GetArticleAsync(t, s, fromApp.ArticleId))!.PublicationLinks);
            Assert.Equal(((Guid?)second.CheckId, $"https://{OwnDomain}/blog/discovered"), (link.DiscoveredCheckId, link.UrlNormalized));
            Assert.Equal(second.CheckId, (await w.Content.GetArticleAsync(t, s, fromApp.ArticleId))!.Article.Discovery!.CheckId);
            // That check is the App answer to the second prompt.
            var source = (await a.MonitoringAnswerDetailAsync(t, s, link.DiscoveredCheckId!.Value, summary.SnapshotToken, null))!;
            Assert.Equal((w.Prompts[1], "Acmebook 更适合小团队。", CollectionChannel.App), (source.PromptId, source.Answer!.Text, source.Channel));
            // A second pass creates nothing.
            Assert.Equal(0, (await links.DiscoverAsync(t, s, DateTimeOffset.UtcNow)).Created);

            // ---- 7.5a: the run list accepts the App provider. ----
            var appRuns = await RunsAsync(w, AppProvider);
            var open = Assert.Single(appRuns);
            Assert.Equal((appRun, AppProvider, "running", 4, 2, 0, 0, 2),
                (open.RunId, open.Provider, open.Status, open.TotalChecks, open.SucceededChecks, open.FailedChecks, open.CancelledChecks, open.CollectionCompletedChecks));
            Assert.Equal([appRun], (await RunsAsync(w, " APP_Collector ")).Select(run => run.RunId));
            // The other providers list what they listed before, and the unfiltered list has every run.
            Assert.Equal(new[] { api[0], api[1] }.Order(), (await RunsAsync(w, "cloro")).Select(run => CheckOfRun(w, run.RunId)).Order());
            Assert.Equal(1, (await RunsAsync(w, "doubao_api")).Count);
            Assert.Equal(1, (await RunsAsync(w, "domestic_collector")).Count);
            Assert.Equal(5, (await RunsAsync(w, null)).Count);
            Assert.All((await RunsAsync(w, null)).Where(run => run.Provider != AppProvider), run => Assert.Equal(0, run.CancelledChecks));
            var refused = Assert.IsType<ObjectResult>((await RunsController(w).ListSiteRuns(s, null, "app", null, null, null)).Result);
            Assert.Equal((400, "INVALID_PROVIDER"), (refused.StatusCode,
                JsonSerializer.SerializeToElement(refused.Value).GetProperty("error").GetProperty("code").GetString()));

            // The cycle ends: the two kimi checks were never collected.
            await ExecAsync(w.Cs, "UPDATE collector_tasks SET deadline_at = UTC_TIMESTAMP(6) - INTERVAL 1 MINUTE WHERE run_id = UUID_TO_BIN(@run) AND status = 'pending';",
                ("@run", appRun.ToString()));
            Assert.Equal(2, await w.Rig.App.ExpireOverdueTasksAsync());
            var closed = Assert.Single(await RunsAsync(w, AppProvider));
            // Not collected is not failed, and collection is finished for all four checks.
            Assert.Equal((appRun, "partial_failed", 4, 2, 0, 2, 4),
                (closed.RunId, closed.Status, closed.TotalChecks, closed.SucceededChecks, closed.FailedChecks, closed.CancelledChecks, closed.CollectionCompletedChecks));
            var summaryOfRun = await RunAsync(w, appRun);
            Assert.Equal((AppProvider, "partial_failed", 4, 2, 0, 2, 4), (summaryOfRun.Provider, summaryOfRun.Status, summaryOfRun.TotalChecks,
                summaryOfRun.SucceededChecks, summaryOfRun.FailedChecks, summaryOfRun.CancelledChecks, summaryOfRun.CollectionCompletedChecks));
            var notCollected = await w.Rig.App.CountNotCollectedAsync(t, [appRun, Guid.NewGuid()]);
            Assert.Equal([(appRun, 2)], notCollected.Select(pair => (pair.Key, pair.Value)));
            Assert.Empty(await w.Rig.App.CountNotCollectedAsync(Guid.NewGuid(), [appRun]));
            // A run of another provider reports what it reported before.
            var cloroRun = (await RunsAsync(w, "cloro")).First(run => run.Status == "failed");
            Assert.Equal((0, 1, 1), (cloroRun.CancelledChecks, cloroRun.FailedChecks, cloroRun.CollectionCompletedChecks));
            Assert.Equal((0, 1), ((await RunAsync(w, cloroRun.RunId)).CancelledChecks, (await RunAsync(w, cloroRun.RunId)).CollectionCompletedChecks));

            // ---- 7.2: a check that was not collected is readable by id, with its reason code and no answer. ----
            var cancelled = Guid.Parse((await RowsAsync(w.Cs, "SELECT BIN_TO_UUID(id) FROM visibility_checks WHERE run_id = UUID_TO_BIN(@run) AND status = 'cancelled' LIMIT 1;",
                ("@run", appRun.ToString())))[0][0]!);
            var token = (await a.MonitoringRecordsAsync(t, s, w.AppView, null, null, 20)).Meta.SnapshotToken;
            var missing = (await a.MonitoringAnswerDetailAsync(t, s, cancelled, token, null))!;
            Assert.Equal(("cancelled", "kimi", "app", CollectionChannel.App, false), (missing.Status, missing.Engine, missing.CaptureMode, missing.Channel, missing.AnswerPresent));
            Assert.StartsWith("APP_NOT_COLLECTED", missing.ErrorCode);
            Assert.Null(missing.Answer);
            Assert.Null(missing.App);
        }
        finally { await DeleteUsersAsync(w.Cs, user, owner); }
    });

    // The single check of a seeded run (the fixture writes one check per run).
    private static Guid CheckOfRun(World w, Guid run)
        => Guid.Parse(RowsAsync(w.Cs, "SELECT BIN_TO_UUID(id) FROM visibility_checks WHERE run_id = UUID_TO_BIN(@run);", ("@run", run.ToString()))
            .GetAwaiter().GetResult()[0][0]!);
}
