using System.Text.Json;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using MySqlConnector;

namespace Jogeoai.Tests;

/// <summary>文章管理 SQL 测试只连接已执行 001–022 的独立测试库；未配置时跳过，绝不使用业务库。</summary>
public sealed class ContentMySqlFactAttribute : FactAttribute
{
    public ContentMySqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JOGEOAI_CONTENT_TEST_CONNECTION")))
            Skip = "需提供已执行 001–022 迁移的独立测试库 JOGEOAI_CONTENT_TEST_CONNECTION；不能使用业务库 jogeoai。";
    }
}

public sealed class ContentStoreSqlTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [ContentMySqlFact]
    public async Task ListStagesCountsFiltersCursorAndDetailAgreeWithDerivedRule()
    {
        var f = await Fixture.CreateAsync();
        try
        {
            var start = DateTimeOffset.UtcNow.AddHours(-1);
            var generating = await f.ArticleAsync("生成中", start.AddMinutes(1));
            await f.TaskAsync(generating, "pending", "held", null, start.AddMinutes(1), withoutRag: true);
            var failed = await f.ArticleAsync("生成失败", start.AddMinutes(2));
            await f.TaskAsync(failed, "failed", "released", "ARTICLE_PROVIDER_FAILED", start.AddMinutes(2), withoutRag: true);
            var pending = await f.ArticleAsync("待发布", start.AddMinutes(3), withVersion: true);
            await f.TaskAsync(pending, "succeeded", "captured", null, start.AddMinutes(3), withoutRag: true);
            var publishing = await f.ArticleAsync("发布中", start.AddMinutes(4), withVersion: true);
            var tracking = await f.ArticleAsync("已追踪", start.AddMinutes(5), withVersion: true, language: "en-US", monitor: "active");
            var archived = await f.ArticleAsync("已归档", start.AddMinutes(6), withVersion: true, status: "archived");
            var tagA = await f.Management.CreateTagAsync(f.Tenant, f.Site, new TagWrite("内容标签A"));
            var tagB = await f.Management.CreateTagAsync(f.Tenant, f.Site, new TagWrite("内容标签B"));
            await f.TagAsync(pending, tagA.TagId);
            await f.TagAsync(pending, tagB.TagId);

            var owned = await f.Store.AddPublicationLinkAsync(f.Tenant, f.Site, publishing, f.User,
                new ArticlePublicationLinkWrite("https://www.acme-content.test/news/1"));
            var thirdParty = await f.Store.AddPublicationLinkAsync(f.Tenant, f.Site, tracking, f.User,
                new ArticlePublicationLinkWrite("https://zhihu.com/p/123"));
            Assert.True(owned.OwnershipMatch);
            Assert.False(thirdParty.OwnershipMatch);

            var all = await f.Store.ListArticlesAsync(f.Tenant, f.Site, new ArticleListFilter(Limit: 50));
            var expected = new Dictionary<Guid, ArticleStage>
            {
                [generating] = ArticleStage.Generating, [failed] = ArticleStage.GenerationFailed,
                [pending] = ArticleStage.PendingPublish, [publishing] = ArticleStage.Publishing,
                [tracking] = ArticleStage.Tracking, [archived] = ArticleStage.Archived
            };
            Assert.Equal(6, all.Items.Count);
            foreach (var item in all.Items)
            {
                Assert.Equal(expected[item.ArticleId], item.Stage);
                Assert.Equal(item.Stage, ArticleStageRules.Derive(item.Status == ArticleStatus.Archived, item.CurrentVersionNo > 0,
                    item.Generation?.Status, item.PublicationLinkCount, item.MonitorStatus));
            }
            Assert.Equal(ArticleStageRules.Keys.Order(StringComparer.Ordinal), all.StageCounts.Keys.Order(StringComparer.Ordinal));
            Assert.All(ArticleStageRules.Keys, key => Assert.Equal(1, all.StageCounts[key]));
            Assert.Equal(6, all.TotalCount);
            Assert.Equal(2, all.ActiveLinkCount);
            Assert.True(all.Items.Single(i => i.ArticleId == publishing).OwnedPublication);
            Assert.False(all.Items.Single(i => i.ArticleId == tracking).OwnedPublication);
            Assert.Equal(["内容标签A", "内容标签B"], all.Items.Single(i => i.ArticleId == pending).Tags);
            var generatingRow = all.Items.Single(i => i.ArticleId == generating);
            Assert.Equal(f.Prompt.PromptId, generatingRow.Prompt!.PromptId);
            Assert.Equal(f.Prompt.Content, generatingRow.Prompt.Content);
            Assert.Equal(CreditReservationStatus.Held, generatingRow.Generation!.ReservationStatus);
            Assert.Null(all.Items.Single(i => i.ArticleId == publishing).Generation);

            foreach (var stage in Enum.GetValues<ArticleStage>())
            {
                var filtered = await f.Store.ListArticlesAsync(f.Tenant, f.Site, new ArticleListFilter(Stage: stage));
                Assert.Equal(stage, Assert.Single(filtered.Items).Stage);
                Assert.Equal(1, filtered.TotalCount);
                Assert.Equal(all.StageCounts, filtered.StageCounts);
            }
            var active = await f.Store.ListArticlesAsync(f.Tenant, f.Site, new ArticleListFilter(Status: ArticleStatus.Active));
            Assert.Equal(5, active.TotalCount);
            Assert.Equal(1, active.StageCounts["archived"]);
            Assert.DoesNotContain(active.Items, item => item.Status == ArticleStatus.Archived);
            var english = await f.Store.ListArticlesAsync(f.Tenant, f.Site, new ArticleListFilter(Language: "en-US"));
            Assert.Equal(tracking, Assert.Single(english.Items).ArticleId);
            Assert.Equal(1, english.TotalCount);
            var ownedOnly = await f.Store.ListArticlesAsync(f.Tenant, f.Site, new ArticleListFilter(OwnedOnly: true));
            Assert.Equal(publishing, Assert.Single(ownedOnly.Items).ArticleId);
            Assert.Equal(1, ownedOnly.ActiveLinkCount);
            var invalidLanguage = await Assert.ThrowsAsync<CollectorStoreException>(() =>
                f.Store.ListArticlesAsync(f.Tenant, f.Site, new ArticleListFilter(Language: "und")));
            Assert.Equal("INVALID_FILTER", invalidLanguage.Code);

            var seen = new List<Guid>();
            string? cursor = null;
            do
            {
                var page = await f.Store.ListArticlesAsync(f.Tenant, f.Site, new ArticleListFilter(Cursor: cursor, Limit: 2));
                Assert.Equal(6, page.TotalCount);
                seen.AddRange(page.Items.Select(item => item.ArticleId));
                cursor = page.NextCursor;
                if (cursor is not null)
                {
                    var scoped = await Assert.ThrowsAsync<CollectorStoreException>(() => f.Store.ListArticlesAsync(f.Tenant, f.Site,
                        new ArticleListFilter(Cursor: cursor, Limit: 2, Stage: ArticleStage.Tracking)));
                    Assert.Equal("INVALID_CURSOR", scoped.Code);
                }
            } while (cursor is not null);
            Assert.Equal(new[] { archived, tracking, publishing, pending, failed, generating }, seen);

            var detail = (await f.Store.GetArticleAsync(f.Tenant, f.Site, generating))!;
            Assert.Equal(ArticleStage.Generating, detail.Article.Stage);
            Assert.Equal("held", detail.CreditStatus["reservationStatus"]);
            var basis = detail.Generation!;
            Assert.Equal(ArticleGenerationTaskStatus.Pending, basis.Status);
            Assert.Equal(f.Prompt.PromptId, basis.Prompt!.PromptId);
            Assert.Equal("chatgpt", Assert.Single(basis.Targets).Engine);
            Assert.Equal("https://example.org/guide", Assert.Single(basis.References).Url);
            Assert.True(basis.WithoutRag);
            Assert.Equal(0, basis.KnowledgeHitCount);
            Assert.Equal(["关键词"], basis.Keywords);
            var failedDetail = (await f.Store.GetArticleAsync(f.Tenant, f.Site, failed))!;
            Assert.Equal("ARTICLE_PROVIDER_FAILED", failedDetail.Generation!.ErrorCode);
            Assert.Equal("released", failedDetail.CreditStatus["reservationStatus"]);
            var withoutTask = (await f.Store.GetArticleAsync(f.Tenant, f.Site, publishing))!;
            Assert.Null(withoutTask.Generation);
            Assert.Null(withoutTask.CreditStatus["reservationStatus"]);
            Assert.True(Assert.Single(withoutTask.PublicationLinks).OwnershipMatch);
        }
        finally
        {
            await f.CleanupAsync();
        }
    }

    [ContentMySqlFact]
    public async Task PublicationLinksComputeOwnershipUnlessClientSpecifiesIt()
    {
        var f = await Fixture.CreateAsync();
        try
        {
            var article = await f.ArticleAsync("归属", DateTimeOffset.UtcNow.AddMinutes(-5), withVersion: true);
            var primary = await f.Store.AddPublicationLinkAsync(f.Tenant, f.Site, article, f.User,
                new ArticlePublicationLinkWrite("https://acme-content.test/a"));
            var additional = await f.Store.AddPublicationLinkAsync(f.Tenant, f.Site, article, f.User,
                new ArticlePublicationLinkWrite("https://www.acme-extra.test/b"));
            var subdomain = await f.Store.AddPublicationLinkAsync(f.Tenant, f.Site, article, f.User,
                new ArticlePublicationLinkWrite("https://blog.acme-content.test/c"));
            var explicitValue = await f.Store.AddPublicationLinkAsync(f.Tenant, f.Site, article, f.User,
                new ArticlePublicationLinkWrite("https://acme-content.test/d", OwnershipMatch: false));
            Assert.True(primary.OwnershipMatch);
            Assert.True(additional.OwnershipMatch);
            Assert.False(subdomain.OwnershipMatch);
            Assert.False(explicitValue.OwnershipMatch);

            await f.Store.DisablePublicationLinkAsync(f.Tenant, f.Site, article, explicitValue.LinkId, f.User);
            var reenabled = await f.Store.AddPublicationLinkAsync(f.Tenant, f.Site, article, f.User,
                new ArticlePublicationLinkWrite("https://acme-content.test/d"));
            Assert.Equal(explicitValue.LinkId, reenabled.LinkId);
            Assert.True(reenabled.OwnershipMatch);
        }
        finally
        {
            await f.CleanupAsync();
        }
    }

    [ContentMySqlFact]
    public async Task DomainRankingsWithoutEligibleAnswersReturnEmptyBoard()
    {
        var f = await Fixture.CreateAsync();
        try
        {
            var article = await f.ArticleAsync("排行", DateTimeOffset.UtcNow.AddMinutes(-5), withVersion: true);
            await f.Store.AddPublicationLinkAsync(f.Tenant, f.Site, article, f.User,
                new ArticlePublicationLinkWrite("https://zhihu.com/p/456"));
            var first = await f.Monitoring.DomainRankingsAsync(f.Tenant, f.Site, new AnalyticsFilterRequest(), null, null, null, 6);
            Assert.Empty(first.Items);
            Assert.Equal(0, first.TotalDomainCount);
            Assert.Null(first.NextCursor);
            Assert.False(string.IsNullOrWhiteSpace(first.Meta.SnapshotToken));
            var reused = await f.Monitoring.DomainRankingsAsync(f.Tenant, f.Site, new AnalyticsFilterRequest(),
                first.Meta.SnapshotToken, null, null, 6);
            Assert.Equal(first.Meta.SnapshotToken, reused.Meta.SnapshotToken);
            Assert.Empty(reused.Items);
        }
        finally
        {
            await f.CleanupAsync();
        }
    }

    [ContentMySqlFact]
    public async Task IdenticalRequestAfterSubmittedBatchMovesToNextRoundHash()
    {
        var f = await Fixture.CreateAsync();
        try
        {
            // 不使用知识库时，相同写作要求的摘要恒定；批次占用后重新预览必须得到下一轮摘要，
            // 否则「重新生成」会因 uq_article_generation_batches_request 永久冲突。
            var baseHash = new string('a', 64);
            var request = new ArticleGenerationPreviewRequest([f.Prompt.PromptId], [Guid.NewGuid()], WithoutRag: true);
            ArticleGenerationPreviewView Preview() => new(Guid.NewGuid(), baseHash, request.PromptIds, request.TargetIds,
                1, 100, 100, 500, DateTimeOffset.UtcNow.AddMinutes(15), [], [], null, true);

            var first = await f.Store.SaveGenerationPreviewAsync(f.Tenant, f.Site, f.User, request, Preview());
            Assert.Equal(baseHash, first.RequestHash);
            var again = await f.Store.SaveGenerationPreviewAsync(f.Tenant, f.Site, f.User, request, Preview());
            Assert.Equal(first.PreviewId, again.PreviewId);

            await f.BatchAsync(baseHash);
            var round1 = await f.Store.SaveGenerationPreviewAsync(f.Tenant, f.Site, f.User, request, Preview());
            Assert.Equal(ContentStore.RequestRoundHash(baseHash, 1), round1.RequestHash);
            Assert.NotEqual(first.PreviewId, round1.PreviewId);
            // 同一轮的并发预览共享同一摘要与预览，提交时的重复提交保护仍然有效。
            var round1Again = await f.Store.SaveGenerationPreviewAsync(f.Tenant, f.Site, f.User, request, Preview());
            Assert.Equal(round1.PreviewId, round1Again.PreviewId);
            Assert.Equal(round1.RequestHash, round1Again.RequestHash);
            var stored = await f.Store.GetGenerationPreviewAsync(f.Tenant, f.Site, round1.PreviewId);
            Assert.NotNull(stored);
            Assert.Equal(round1.RequestHash, stored!.RequestHash);
            Assert.Equal(round1.PreviewId, stored.Preview.PreviewId);
        }
        finally
        {
            await f.CleanupAsync();
        }
    }

    [ContentMySqlFact]
    public async Task BatchFinishesAsPartialWhenTaskFailsAfterSiblingSucceeded()
    {
        var f = await Fixture.CreateAsync();
        try
        {
            // 两篇的批次：第一篇已成功，第二篇最终失败。旧逻辑只比较 failed+1>=total，批次永远停在 running。
            var (batch, failingTask, failingArticle) = await f.TwoTaskBatchWithOneSucceededAsync();
            var workItem = new ArticleGenerationWorkItem(failingTask, batch, failingArticle, f.Tenant, f.Site, f.User,
                JsonSerializer.Serialize(f.Prompt, Web), "[]", null, null, null, null, "zh-CN", "answer_guide", "medium",
                "professional", null, [], null, null, null, true, 1, "worker-test", 100);
            await f.Store.FailGenerationTaskAsync(workItem, "ARTICLE_GENERATION_FAILED", "生成失败。", retryable: false);

            var row = await f.RowAsync(
                "SELECT status, completed_at IS NOT NULL, failed_tasks, succeeded_tasks FROM article_generation_batches WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@id);",
                batch);
            Assert.Equal(["partial", "1", "1", "1"], row);
            var account = await f.RowAsync(
                "SELECT CAST(reserved_credits AS SIGNED), CAST(available_credits AS SIGNED) FROM credit_accounts WHERE tenant_id=UUID_TO_BIN(@tenant) AND @id IS NOT NULL;",
                batch);
            Assert.Equal(["0", "100"], account);
        }
        finally
        {
            await f.CleanupAsync();
        }
    }

    [ContentMySqlFact]
    public async Task CancelledSiblingIsCountedSoRunningTaskStillFinishesBatch()
    {
        var f = await Fixture.CreateAsync();
        try
        {
            // 一篇运行中、一篇排队时取消：取消的任务计入 failed_tasks，运行中的任务结束时批次按计数正常终态化；
            // 结束判断只用批次计数，不读兄弟任务行（避免与取消/并发完成形成锁环）。
            var (batch, runningTask, runningArticle) = await f.RunningAndPendingBatchAsync();
            var cancelled = await f.Store.CancelGenerationAsync(f.Tenant, f.Site, f.User, batch);
            Assert.NotNull(cancelled);
            Assert.Equal(ArticleGenerationBatchStatus.Running, cancelled!.Status);
            Assert.Equal(1, cancelled.FailedTasks);
            Assert.Null(cancelled.CompletedAt);

            var workItem = new ArticleGenerationWorkItem(runningTask, batch, runningArticle, f.Tenant, f.Site, f.User,
                JsonSerializer.Serialize(f.Prompt, Web), "[]", null, null, null, null, "zh-CN", "answer_guide", "medium",
                "professional", null, [], null, null, null, true, 1, "worker-test", 100);
            await f.Store.FailGenerationTaskAsync(workItem, "ARTICLE_GENERATION_FAILED", "生成失败。", retryable: false);

            var row = await f.RowAsync(
                "SELECT status, completed_at IS NOT NULL, failed_tasks, succeeded_tasks FROM article_generation_batches WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@id);",
                batch);
            Assert.Equal(["failed", "1", "2", "0"], row);
            var account = await f.RowAsync(
                "SELECT CAST(reserved_credits AS SIGNED), CAST(available_credits AS SIGNED) FROM credit_accounts WHERE tenant_id=UUID_TO_BIN(@tenant) AND @id IS NOT NULL;",
                batch);
            Assert.Equal(["0", "200"], account);
        }
        finally
        {
            await f.CleanupAsync();
        }
    }

    [ContentMySqlFact]
    public async Task CitationTableFieldsFiltersCountsAndSortsFollowWindowEngineAndOwnership()
    {
        var f = await Fixture.CreateAsync();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var start = now.AddHours(-6);
            var published = now.AddDays(-60);
            // A：外部链接发布更早、官网链接发布更晚、停用的官网链接最早 —— 主链接必须是有效的官网链接。
            var multi = await f.ArticleAsync("官网多链接", start.AddMinutes(1), withVersion: true, monitor: "active");
            var disabledOwned = await f.Store.AddPublicationLinkAsync(f.Tenant, f.Site, multi, f.User,
                new ArticlePublicationLinkWrite("https://acme-content.test/old", PublishedAt: now.AddDays(-70)));
            await f.Store.DisablePublicationLinkAsync(f.Tenant, f.Site, multi, disabledOwned.LinkId, f.User);
            var multiExternal = await f.Store.AddPublicationLinkAsync(f.Tenant, f.Site, multi, f.User,
                new ArticlePublicationLinkWrite("https://zhihu.com/p/multi", PublishedAt: published));
            var multiOwned = await f.Store.AddPublicationLinkAsync(f.Tenant, f.Site, multi, f.User,
                new ArticlePublicationLinkWrite("https://acme-content.test/news/multi", PublishedAt: now.AddDays(-50)));
            // B：只有外部链接且被豆包引用；C：只有外部链接、没有引用；D：待发布；E：生成中（D、E 没有链接）。
            var external = await f.ArticleAsync("外部被引", start.AddMinutes(2), withVersion: true, monitor: "active");
            var externalLink = await f.Store.AddPublicationLinkAsync(f.Tenant, f.Site, external, f.User,
                new ArticlePublicationLinkWrite("https://zhihu.com/p/external", PublishedAt: published));
            var quiet = await f.ArticleAsync("外部未引", start.AddMinutes(3), withVersion: true, monitor: "active");
            await f.Store.AddPublicationLinkAsync(f.Tenant, f.Site, quiet, f.User,
                new ArticlePublicationLinkWrite("https://zhihu.com/p/quiet", PublishedAt: published));
            var pending = await f.ArticleAsync("待发布", start.AddMinutes(4), withVersion: true);
            var generating = await f.ArticleAsync("生成中", start.AddMinutes(5));
            await f.TaskAsync(generating, "pending", "held", null, start.AddMinutes(5), withoutRag: true);

            var lastA = now.AddHours(-1);
            var firstA = now.AddDays(-10);
            var lastB = now.AddHours(-3);
            await f.CheckAsync(lastA, "chatgpt", multiOwned.UrlHash);
            await f.CheckAsync(firstA, "doubao", multiOwned.UrlHash);
            await f.CheckAsync(now.AddHours(-2), "chatgpt", multiExternal.UrlHash);
            await f.CheckAsync(lastB, "doubao", externalLink.UrlHash);
            // 停用链接从未被引用：不参与主链接，也不贡献引用。

            // AC1 主发布链接。
            var all = await f.Store.ListArticlesAsync(f.Tenant, f.Site, new ArticleListFilter(Limit: 50));
            Assert.Equal(5, all.TotalCount);
            var rowA = all.Items.Single(item => item.ArticleId == multi);
            Assert.Equal(multiOwned.LinkId, rowA.PrimaryLink!.LinkId);
            Assert.Equal(multiOwned.UrlOriginal, rowA.PrimaryLink.Url);
            Assert.True(rowA.PrimaryLink.OwnershipMatch);
            AssertClose(now.AddDays(-50), rowA.PrimaryLink.PublishedAt!.Value);
            var rowB = all.Items.Single(item => item.ArticleId == external);
            Assert.Equal(externalLink.LinkId, rowB.PrimaryLink!.LinkId);
            Assert.False(rowB.PrimaryLink.OwnershipMatch);
            Assert.Null(all.Items.Single(item => item.ArticleId == pending).PrimaryLink);
            Assert.Null(all.Items.Single(item => item.ArticleId == generating).PrimaryLink);
            Assert.Null((await f.Store.GetArticleAsync(f.Tenant, f.Site, multi))!.Article.PrimaryLink);

            // AC2 区间：默认 30 天含 10 天前的豆包观测，7 天不含。
            Assert.Equal(3, rowA.CitationSummary.CitationCount);
            Assert.Equal(["chatgpt", "doubao"], rowA.CitationSummary.Engines!);
            AssertClose(firstA, rowA.CitationSummary.FirstObservedAt!.Value);
            AssertClose(lastA, rowA.CitationSummary.LastObservedAt!.Value);
            Assert.Equal(1, rowB.CitationSummary.CitationCount);
            Assert.Equal(["doubao"], rowB.CitationSummary.Engines!);
            var quietRow = all.Items.Single(item => item.ArticleId == quiet);
            Assert.Equal(0, quietRow.CitationSummary.CitationCount ?? 0);
            Assert.Empty(quietRow.CitationSummary.Engines!);
            Assert.Null(quietRow.CitationSummary.FirstObservedAt);
            var thirty = await f.Store.ListArticlesAsync(f.Tenant, f.Site, new ArticleListFilter(Limit: 50, CitationDays: 30));
            Assert.Equal(rowA.CitationSummary.CitationCount, thirty.Items.Single(item => item.ArticleId == multi).CitationSummary.CitationCount);
            var week = await f.Store.ListArticlesAsync(f.Tenant, f.Site, new ArticleListFilter(Limit: 50, CitationDays: 7));
            var weekA = week.Items.Single(item => item.ArticleId == multi).CitationSummary;
            Assert.Equal(2, weekA.CitationCount);
            Assert.Equal(["chatgpt"], weekA.Engines!);
            AssertClose(now.AddHours(-2), weekA.FirstObservedAt!.Value);
            AssertClose(lastA, weekA.LastObservedAt!.Value);
            foreach (var days in new[] { 0, 14, 365 })
                Assert.Equal("INVALID_FILTER", (await Assert.ThrowsAsync<CollectorStoreException>(() =>
                    f.Store.ListArticlesAsync(f.Tenant, f.Site, new ArticleListFilter(CitationDays: days)))).Code);

            // AC3 被引计数与 cited 筛选一致；未被引用包含没有链接的文章。
            Assert.Equal(new ArticleCitedCounts(5, 2, 3), all.CitedCounts);
            var cited = await f.Store.ListArticlesAsync(f.Tenant, f.Site, new ArticleListFilter(Limit: 50, Cited: true));
            Assert.Equal(new[] { multi, external }.Order(), cited.Items.Select(item => item.ArticleId).Order());
            Assert.Equal(2, cited.TotalCount);
            Assert.Equal(all.CitedCounts, cited.CitedCounts);
            var uncited = await f.Store.ListArticlesAsync(f.Tenant, f.Site, new ArticleListFilter(Limit: 50, Cited: false));
            Assert.Equal(new[] { quiet, pending, generating }.Order(), uncited.Items.Select(item => item.ArticleId).Order());
            Assert.Equal(3, uncited.TotalCount);
            Assert.Equal(1, uncited.StageCounts["pending_publish"]);
            Assert.Equal(1, uncited.StageCounts["generating"]);
            Assert.Equal(new ArticleCitedCounts(5, 2, 3), week.CitedCounts);

            // AC4 引用平台收窄全部引用口径。
            var doubao = await f.Store.ListArticlesAsync(f.Tenant, f.Site, new ArticleListFilter(Limit: 50, CitationEngine: "Doubao"));
            Assert.Equal(5, doubao.TotalCount);
            var doubaoA = doubao.Items.Single(item => item.ArticleId == multi).CitationSummary;
            Assert.Equal(1, doubaoA.CitationCount);
            Assert.Equal(["doubao"], doubaoA.Engines!);
            AssertClose(firstA, doubaoA.FirstObservedAt!.Value);
            AssertClose(firstA, doubaoA.LastObservedAt!.Value);
            Assert.Equal(new ArticleCitedCounts(5, 2, 3), doubao.CitedCounts);
            var chatgpt = await f.Store.ListArticlesAsync(f.Tenant, f.Site, new ArticleListFilter(Limit: 50, CitationEngine: "chatgpt"));
            Assert.Equal(0, chatgpt.Items.Single(item => item.ArticleId == external).CitationSummary.CitationCount ?? 0);
            Assert.Equal(new ArticleCitedCounts(5, 1, 4), chatgpt.CitedCounts);
            var chatgptUncited = await f.Store.ListArticlesAsync(f.Tenant, f.Site,
                new ArticleListFilter(Limit: 50, CitationEngine: "chatgpt", Cited: false));
            Assert.Contains(chatgptUncited.Items, item => item.ArticleId == external);
            Assert.Equal(4, chatgptUncited.TotalCount);
            var doubaoWeek = await f.Store.ListArticlesAsync(f.Tenant, f.Site,
                new ArticleListFilter(Limit: 50, CitationEngine: "doubao", CitationDays: 7, Cited: true));
            Assert.Equal(external, Assert.Single(doubaoWeek.Items).ArticleId);

            // AC5 域名归属。
            var externalOnly = await f.Store.ListArticlesAsync(f.Tenant, f.Site,
                new ArticleListFilter(Limit: 50, Ownership: ArticleOwnershipFilter.External));
            Assert.Equal(new[] { external, quiet }.Order(), externalOnly.Items.Select(item => item.ArticleId).Order());
            Assert.Equal(new ArticleCitedCounts(2, 1, 1), externalOnly.CitedCounts);
            Assert.Equal(2, externalOnly.ActiveLinkCount);
            var ownedOnly = await f.Store.ListArticlesAsync(f.Tenant, f.Site,
                new ArticleListFilter(Limit: 50, Ownership: ArticleOwnershipFilter.Owned));
            Assert.Equal(multi, Assert.Single(ownedOnly.Items).ArticleId);
            var legacyOwned = await f.Store.ListArticlesAsync(f.Tenant, f.Site, new ArticleListFilter(Limit: 50, OwnedOnly: true));
            Assert.Equal(multi, Assert.Single(legacyOwned.Items).ArticleId);
            var conflict = await Assert.ThrowsAsync<CollectorStoreException>(() => f.Store.ListArticlesAsync(f.Tenant, f.Site,
                new ArticleListFilter(OwnedOnly: true, Ownership: ArticleOwnershipFilter.External)));
            Assert.Equal("INVALID_FILTER", conflict.Code);

            // AC6 按被引时间排序：时间降序、无被引在后、跨页无重复无遗漏；筛选变化后旧游标 400。
            var lastOrder = await PageAllAsync(f, new ArticleListFilter(Sort: "last_cited_desc", Limit: 2));
            Assert.Equal(new[] { multi, external }, lastOrder.Take(2).ToArray());
            Assert.Equal(new[] { quiet, pending, generating }.Order(), lastOrder.Skip(2).Order());
            var firstOrder = await PageAllAsync(f, new ArticleListFilter(Sort: "first_cited_desc", Limit: 2));
            Assert.Equal(new[] { external, multi }, firstOrder.Take(2).ToArray());
            Assert.Equal(new[] { quiet, pending, generating }.Order(), firstOrder.Skip(2).Order());
            var doubaoLast = await PageAllAsync(f, new ArticleListFilter(Sort: "last_cited_desc", Limit: 1, CitationEngine: "doubao"));
            Assert.Equal(new[] { external, multi }, doubaoLast.Take(2).ToArray());
            var citedLast = await PageAllAsync(f, new ArticleListFilter(Sort: "last_cited_desc", Limit: 1, Cited: true));
            Assert.Equal(new[] { multi, external }, citedLast.ToArray());

            var firstPage = await f.Store.ListArticlesAsync(f.Tenant, f.Site, new ArticleListFilter(Sort: "last_cited_desc", Limit: 2));
            Assert.NotNull(firstPage.NextCursor);
            foreach (var changed in new[]
                     {
                         new ArticleListFilter(Sort: "last_cited_desc", Limit: 2, Cursor: firstPage.NextCursor, Cited: false),
                         new ArticleListFilter(Sort: "last_cited_desc", Limit: 2, Cursor: firstPage.NextCursor, CitationDays: 7),
                         new ArticleListFilter(Sort: "last_cited_desc", Limit: 2, Cursor: firstPage.NextCursor, CitationEngine: "chatgpt"),
                         new ArticleListFilter(Sort: "last_cited_desc", Limit: 2, Cursor: firstPage.NextCursor, Ownership: ArticleOwnershipFilter.External),
                         new ArticleListFilter(Sort: "first_cited_desc", Limit: 2, Cursor: firstPage.NextCursor),
                         new ArticleListFilter(Sort: "last_cited_desc", Limit: 2, Cursor: "bm90LWEtY3Vyc29y")
                     })
                Assert.Equal("INVALID_CURSOR", (await Assert.ThrowsAsync<CollectorStoreException>(() =>
                    f.Store.ListArticlesAsync(f.Tenant, f.Site, changed))).Code);
        }
        finally
        {
            await f.CleanupAsync();
        }

        // 行内引用摘要沿用既有读取方式（秒级），发布时间为微秒级；统一按 1 秒容差比较。
        static void AssertClose(DateTimeOffset expected, DateTimeOffset actual)
            => Assert.True((expected - actual).Duration() < TimeSpan.FromSeconds(1), $"期望 {expected:O}，实际 {actual:O}");
    }

    private static async Task<List<Guid>> PageAllAsync(Fixture f, ArticleListFilter filter)
    {
        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await f.Store.ListArticlesAsync(f.Tenant, f.Site, filter with { Cursor = cursor });
            seen.AddRange(page.Items.Select(item => item.ArticleId));
            cursor = page.NextCursor;
            Assert.True(++pages <= 20, "分页未终止。");
        } while (cursor is not null);
        Assert.Equal(seen.Count, seen.Distinct().Count());
        return seen;
    }

    [ContentMySqlFact]
    public async Task OwnedUrlMarkCreatesUnchangedReactivatesAndUnmarkArchivesOnlyImportedArticles()
    {
        var f = await Fixture.CreateAsync();
        try
        {
            const string raw = "https://www.zhihu.com/question/1?utm_source=feed&a=1#answer";
            var (normalized, hash) = ContentStore.OwnedUrlKey(raw);
            Assert.Equal(CitationDomainAccumulator.UrlHash(DetailedAnalyticsRules.NormalizeUrl(raw)!), hash);

            var created = await f.Store.MarkOwnedUrlAsync(f.Tenant, f.Site, f.User, raw, "  知乎问题  ");
            Assert.Equal((hash, normalized, true, OwnedUrlMarkOutcome.Created), (created.UrlHash, created.Url, created.IsMine, created.Outcome));
            var articleId = created.ArticleId!.Value;
            var detail = (await f.Store.GetArticleAsync(f.Tenant, f.Site, articleId))!;
            Assert.Equal((ArticleSourceKind.PublishedImport, ArticleStatus.Active, ArticleMonitorStatus.Active, "知乎问题"),
                (detail.Article.SourceKind, detail.Article.Status, detail.Article.MonitorStatus, detail.Article.Title));
            Assert.Null(detail.CurrentVersion);
            var link = Assert.Single(detail.PublicationLinks);
            Assert.Equal((ArticlePublicationStatus.Active, hash, normalized, ContentStore.OwnedUrlReadStatus, (bool?)false),
                (link.Status, link.UrlHash, link.UrlNormalized, link.ReadStatus, link.OwnershipMatch));
            Assert.Equal(raw, link.UrlOriginal);
            var imports = await f.Store.ListArticlesAsync(f.Tenant, f.Site, new ArticleListFilter(SourceKind: ArticleSourceKind.PublishedImport));
            Assert.Contains(imports.Items, item => item.ArticleId == articleId);

            var again = await f.Store.MarkOwnedUrlAsync(f.Tenant, f.Site, f.User, normalized, "其他标题");
            Assert.Equal((OwnedUrlMarkOutcome.Unchanged, (Guid?)articleId, true), (again.Outcome, again.ArticleId, again.IsMine));
            Assert.Single((await f.Store.GetArticleAsync(f.Tenant, f.Site, articleId))!.PublicationLinks);

            var removed = await f.Store.UnmarkOwnedUrlAsync(f.Tenant, f.Site, f.User, hash.ToUpperInvariant());
            Assert.Equal((OwnedUrlMarkOutcome.Disabled, false, (Guid?)articleId, (string?)normalized),
                (removed.Outcome, removed.IsMine, removed.ArticleId, removed.Url));
            var archived = (await f.Store.GetArticleAsync(f.Tenant, f.Site, articleId))!;
            Assert.Equal(ArticleStatus.Archived, archived.Article.Status);
            Assert.NotNull(archived.Article.ArchivedAt);
            Assert.Equal(ArticlePublicationStatus.Disabled, Assert.Single(archived.PublicationLinks).Status);
            Assert.NotNull(archived.PublicationLinks[0].DisabledAt);

            var removedAgain = await f.Store.UnmarkOwnedUrlAsync(f.Tenant, f.Site, f.User, hash);
            Assert.Equal((OwnedUrlMarkOutcome.Unchanged, false, (string?)normalized), (removedAgain.Outcome, removedAgain.IsMine, removedAgain.Url));
            var unknown = await f.Store.UnmarkOwnedUrlAsync(f.Tenant, f.Site, f.User, new string('0', 64));
            Assert.Equal((OwnedUrlMarkOutcome.Unchanged, (string?)null, (Guid?)null), (unknown.Outcome, unknown.Url, unknown.ArticleId));

            var reactivated = await f.Store.MarkOwnedUrlAsync(f.Tenant, f.Site, f.User, raw, null);
            Assert.Equal((OwnedUrlMarkOutcome.Reactivated, (Guid?)articleId, true), (reactivated.Outcome, reactivated.ArticleId, reactivated.IsMine));
            var restored = (await f.Store.GetArticleAsync(f.Tenant, f.Site, articleId))!;
            Assert.Equal((ArticleStatus.Active, (DateTimeOffset?)null), (restored.Article.Status, restored.Article.ArchivedAt));
            var restoredLink = Assert.Single(restored.PublicationLinks);
            Assert.Equal((ArticlePublicationStatus.Active, (DateTimeOffset?)null), (restoredLink.Status, restoredLink.DisabledAt));
            Assert.Equal("知乎问题", restored.Article.Title);

            // A generated article only loses its link on unmark; marking again reactivates the same link.
            var generated = await f.ArticleAsync("生成文章", DateTimeOffset.UtcNow.AddMinutes(-5), withVersion: true);
            var generatedLink = await f.Store.AddPublicationLinkAsync(f.Tenant, f.Site, generated, f.User,
                new ArticlePublicationLinkWrite("https://www.acme-content.test/news/9"));
            var generatedMark = await f.Store.MarkOwnedUrlAsync(f.Tenant, f.Site, f.User, generatedLink.UrlNormalized, null);
            Assert.Equal((OwnedUrlMarkOutcome.Unchanged, (Guid?)generated), (generatedMark.Outcome, generatedMark.ArticleId));
            var generatedUnmark = await f.Store.UnmarkOwnedUrlAsync(f.Tenant, f.Site, f.User, generatedLink.UrlHash);
            Assert.Equal((OwnedUrlMarkOutcome.Disabled, (Guid?)generated), (generatedUnmark.Outcome, generatedUnmark.ArticleId));
            var generatedDetail = (await f.Store.GetArticleAsync(f.Tenant, f.Site, generated))!;
            Assert.Equal(ArticleStatus.Active, generatedDetail.Article.Status);
            Assert.Equal(ArticlePublicationStatus.Disabled, Assert.Single(generatedDetail.PublicationLinks).Status);
            var generatedRemark = await f.Store.MarkOwnedUrlAsync(f.Tenant, f.Site, f.User, generatedLink.UrlNormalized, null);
            Assert.Equal((OwnedUrlMarkOutcome.Reactivated, (Guid?)generated), (generatedRemark.Outcome, generatedRemark.ArticleId));
            var generatedAfter = (await f.Store.GetArticleAsync(f.Tenant, f.Site, generated))!;
            Assert.Equal(ArticleSourceKind.AiGenerated, generatedAfter.Article.SourceKind);
            Assert.Equal((ArticlePublicationStatus.Active, (bool?)true),
                (Assert.Single(generatedAfter.PublicationLinks).Status, generatedAfter.PublicationLinks[0].OwnershipMatch));

            // Brand-domain URLs are owned by the server-side rule; untitled marks fall back to the URL.
            var official = await f.Store.MarkOwnedUrlAsync(f.Tenant, f.Site, f.User, "https://acme-content.test/about", null);
            var officialDetail = (await f.Store.GetArticleAsync(f.Tenant, f.Site, official.ArticleId!.Value))!;
            Assert.Equal(OwnedUrlMarkOutcome.Created, official.Outcome);
            Assert.True(Assert.Single(officialDetail.PublicationLinks).OwnershipMatch);
            Assert.Equal(official.Url, officialDetail.Article.Title);

            var foreign = await Assert.ThrowsAsync<CollectorStoreException>(() =>
                f.Store.MarkOwnedUrlAsync(f.Tenant, Guid.NewGuid(), f.User, raw, null));
            Assert.Equal("RESOURCE_NOT_FOUND", foreign.Code);
        }
        finally
        {
            await f.CleanupAsync();
        }
    }

    [ContentMySqlFact]
    public async Task ImportConfirmKeepsItsArticleReferenceAndLinkRowsAfterSharingTheInsertHelper()
    {
        var f = await Fixture.CreateAsync();
        try
        {
            var (readableUrl, readableHash) = ContentStore.OwnedUrlKey("https://news.example.org/a");
            var (blockedUrl, blockedHash) = ContentStore.OwnedUrlKey("https://acme-content.test/blocked");
            var preview = new ArticleImportPreviewView(Guid.NewGuid(), new string('c', 64),
            [
                new ArticleImportPreviewItem("https://news.example.org/a", readableUrl, readableHash, "可读标题", "正文段落", "ready", null, null, false, null),
                new ArticleImportPreviewItem("https://acme-content.test/blocked", blockedUrl, blockedHash, null, null, "blocked", "SOURCE_BLOCKED", "拒绝", false, null)
            ], DateTimeOffset.UtcNow.AddMinutes(10));
            await f.PreviewAsync(preview);
            var imported = await f.Store.ConfirmImportAsync(f.Tenant, f.Site, f.User,
                new ArticleImportConfirmRequest(preview.PreviewId, preview.RequestHash, ["知乎"],
                    [new ArticleImportTitleOverride(blockedHash, "手填标题")]), $"confirm-{preview.PreviewId:N}");
            Assert.Equal(2, imported.Count);
            var readable = (await f.Store.GetArticleAsync(f.Tenant, f.Site, imported[0].ArticleId))!;
            Assert.Equal((ArticleSourceKind.PublishedImport, ArticleMonitorStatus.Active, "可读标题", 1),
                (readable.Article.SourceKind, readable.Article.MonitorStatus, readable.Article.Title, readable.Article.CurrentVersionNo));
            Assert.Equal("正文段落", readable.CurrentVersion!.BodyMarkdown);
            var readableLink = Assert.Single(readable.PublicationLinks);
            Assert.Equal((readableHash, "ready", "知乎", readable.CurrentVersion.VersionId, (bool?)false),
                (readableLink.UrlHash, readableLink.ReadStatus, readableLink.Channel, readableLink.ArticleVersionId, readableLink.OwnershipMatch));
            var blocked = (await f.Store.GetArticleAsync(f.Tenant, f.Site, imported[1].ArticleId))!;
            Assert.Equal(("手填标题", 0), (blocked.Article.Title, blocked.Article.CurrentVersionNo));
            var blockedLink = Assert.Single(blocked.PublicationLinks);
            Assert.Equal((blockedHash, "blocked", (Guid?)null, (bool?)true),
                (blockedLink.UrlHash, blockedLink.ReadStatus, blockedLink.ArticleVersionId, blockedLink.OwnershipMatch));
            // The imported URL is now "mine": marking it is a no-op on the same article.
            var mark = await f.Store.MarkOwnedUrlAsync(f.Tenant, f.Site, f.User, readableUrl, null);
            Assert.Equal((OwnedUrlMarkOutcome.Unchanged, (Guid?)readable.Article.ArticleId), (mark.Outcome, mark.ArticleId));
        }
        finally
        {
            await f.CleanupAsync();
        }
    }

    private sealed class Fixture
    {
        private readonly string connectionString;
        private readonly Guid run = Guid.NewGuid();
        private readonly Guid release = Guid.NewGuid();
        private readonly Dictionary<string, Guid> targets = new(StringComparer.Ordinal);
        private int sample;

        private Fixture(string connection)
        {
            connectionString = connection;
            var database = new MySqlDatabase(connection);
            Management = new ManagementStore(database);
            var analyzer = new AnalysisStore(database);
            // List/detail/publication-link paths only use the database and the
            // management store; the generation dependencies are never touched.
            Store = new ContentStore(database, Management, null!, null!, null!, null!, null!);
            Monitoring = new ArticleMonitoringQuery(new AnalyticsQueryStore(database, analyzer), database);
        }

        public Guid Tenant { get; } = Guid.NewGuid();
        public Guid User { get; } = Guid.NewGuid();
        public Guid Site { get; private set; }
        public PromptView Prompt { get; private set; } = null!;
        public ManagementStore Management { get; }
        public ContentStore Store { get; }
        public ArticleMonitoringQuery Monitoring { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var value = Environment.GetEnvironmentVariable("JOGEOAI_CONTENT_TEST_CONNECTION");
            Assert.False(string.IsNullOrWhiteSpace(value), "未设置 JOGEOAI_CONTENT_TEST_CONNECTION；SQL 测试不会静默通过。");
            var builder = new MySqlConnectionStringBuilder(value!);
            Assert.False(string.IsNullOrWhiteSpace(builder.Database), "必须显式指定独立测试库。");
            Assert.NotEqual("jogeoai", builder.Database, StringComparer.OrdinalIgnoreCase);
            var fixture = new Fixture(value!);
            await fixture.ExecAsync("""
                INSERT INTO tenants (id, name, status, created_at, updated_at)
                VALUES (UUID_TO_BIN(@tenant), 'content articles fixture', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                INSERT INTO auth_users (id, email, password_hash, status, created_at, updated_at)
                VALUES (UUID_TO_BIN(@user), @email, 'not-a-login', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                INSERT INTO credit_accounts (tenant_id, updated_at) VALUES (UUID_TO_BIN(@tenant), UTC_TIMESTAMP(6));
                """, ("@tenant", fixture.Tenant.ToString()), ("@user", fixture.User.ToString()),
                ("@email", $"content-{fixture.User:N}@example.test"));
            var brand = await fixture.Management.CreateBrandAsync(fixture.Tenant, new BrandWrite("Acme Content",
                "acme-content.test", AdditionalDomains: ["www.acme-extra.test"], TimeZone: "UTC", IncludeSubdomains: false, Description: "测试品牌提供设备与服务。"));
            fixture.Site = brand.SiteId;
            fixture.Prompt = await fixture.Management.CreatePromptAsync(fixture.Tenant, fixture.Site,
                new PromptWrite("哪款扫地机器人适合养宠家庭？", "zh-CN"));
            return fixture;
        }

        public async Task<Guid> ArticleAsync(string title, DateTimeOffset created, bool withVersion = false,
            string language = "zh-CN", string status = "active", string monitor = "inactive")
        {
            var article = Guid.NewGuid();
            await ExecAsync("""
                INSERT INTO content_articles (id,tenant_id,site_id,created_by,source_kind,status,title,language,monitor_status,created_at,updated_at,archived_at)
                VALUES (UUID_TO_BIN(@article),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@user),'ai_generated',@status,@title,@language,@monitor,@created,@created,
                        CASE WHEN @status='archived' THEN @created ELSE NULL END);
                """, ("@article", article.ToString()), ("@tenant", Tenant.ToString()), ("@site", Site.ToString()),
                ("@user", User.ToString()), ("@status", status), ("@title", title), ("@language", language),
                ("@monitor", monitor), ("@created", created.UtcDateTime));
            if (withVersion)
            {
                var version = Guid.NewGuid();
                await ExecAsync("""
                    INSERT INTO content_article_versions (id,tenant_id,site_id,article_id,version_no,version_kind,title,body_markdown,content_length,language,created_by,created_at)
                    VALUES (UUID_TO_BIN(@version),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@article),1,'generated',@title,'正文',2,@language,UUID_TO_BIN(@user),@created);
                    UPDATE content_articles SET current_version_id=UUID_TO_BIN(@version) WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@article);
                    """, ("@version", version.ToString()), ("@tenant", Tenant.ToString()), ("@site", Site.ToString()),
                    ("@article", article.ToString()), ("@title", title), ("@language", language), ("@user", User.ToString()),
                    ("@created", created.UtcDateTime));
            }
            return article;
        }

        public async Task TaskAsync(Guid article, string status, string reservationStatus, string? errorCode,
            DateTimeOffset created, bool withoutRag)
        {
            var batch = Guid.NewGuid();
            var task = Guid.NewGuid();
            var reservation = Guid.NewGuid();
            var targets = new[]
            {
                new CollectionTargetView(Guid.NewGuid(), "cloro", "chatgpt", "ChatGPT", "scraped", "observed_surface",
                    "US", "en-US", true, "available", "full", "full", "full")
            };
            var references = ContentStore.SerializeReferenceSnapshot(
                [new ContentStore.ReferenceSnapshotItem("https://example.org/guide", "ready", "范文", "正文")]);
            await ExecAsync("""
                INSERT INTO article_generation_batches (id,tenant_id,site_id,requested_by,idempotency_key,request_hash,status,total_tasks,request_snapshot,preview_expires_at,created_at)
                VALUES (UUID_TO_BIN(@batch),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@user),@key,@hash,'queued',1,JSON_OBJECT(),@created,@created);
                INSERT INTO article_generation_tasks (id,tenant_id,site_id,batch_id,article_id,prompt_id,prompt_snapshot,target_snapshot,reference_snapshot,language,strategy_key,length_profile,style,keywords_json,without_rag,status,next_attempt_at,last_error_code,last_error_message,reservation_id,created_at,updated_at)
                VALUES (UUID_TO_BIN(@task),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@batch),UUID_TO_BIN(@article),UUID_TO_BIN(@prompt),@prompt_snapshot,@target_snapshot,@references,'zh-CN','answer_guide','medium','professional',JSON_ARRAY('关键词'),@without_rag,@status,@created,@error,@error_message,UUID_TO_BIN(@reservation),@created,@created);
                INSERT INTO credit_reservations (id,tenant_id,batch_id,task_id,amount,status,idempotency_key,created_at)
                VALUES (UUID_TO_BIN(@reservation),UUID_TO_BIN(@tenant),UUID_TO_BIN(@batch),UUID_TO_BIN(@task),100,@reservation_status,CONCAT('task:',@task),@created);
                """, ("@batch", batch.ToString()), ("@tenant", Tenant.ToString()), ("@site", Site.ToString()), ("@user", User.ToString()),
                ("@key", $"content-test-{batch:N}"), ("@hash", $"{Guid.NewGuid():N}{Guid.NewGuid():N}"), ("@created", created.UtcDateTime),
                ("@task", task.ToString()), ("@article", article.ToString()), ("@prompt", Prompt.PromptId.ToString()),
                ("@prompt_snapshot", JsonSerializer.Serialize(Prompt, Web)), ("@target_snapshot", JsonSerializer.Serialize(targets, Web)),
                ("@references", references), ("@without_rag", withoutRag), ("@status", status), ("@error", errorCode),
                ("@error_message", errorCode is null ? null : "生成服务返回错误。"), ("@reservation", reservation.ToString()),
                ("@reservation_status", reservationStatus));
        }

        /// <summary>两篇的运行中批次：第一篇已成功（已结算），第二篇由 worker-test 持有租约、预占 100 积分。</summary>
        public async Task<(Guid Batch, Guid FailingTask, Guid FailingArticle)> TwoTaskBatchWithOneSucceededAsync()
        {
            var batch = Guid.NewGuid();
            var done = await ArticleAsync("已成功", DateTimeOffset.UtcNow.AddMinutes(-3), withVersion: true);
            var failing = await ArticleAsync("将失败", DateTimeOffset.UtcNow.AddMinutes(-3));
            var doneTask = Guid.NewGuid();
            var failingTask = Guid.NewGuid();
            var reservation = Guid.NewGuid();
            await ExecAsync("""
                UPDATE credit_accounts SET reserved_credits=100 WHERE tenant_id=UUID_TO_BIN(@tenant);
                INSERT INTO article_generation_batches (id,tenant_id,site_id,requested_by,idempotency_key,request_hash,status,total_tasks,succeeded_tasks,reserved_credits,captured_credits,request_snapshot,preview_expires_at,created_at)
                VALUES (UUID_TO_BIN(@batch),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@user),@key,@hash,'running',2,1,100,100,JSON_OBJECT(),UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
                INSERT INTO article_generation_tasks (id,tenant_id,site_id,batch_id,article_id,prompt_id,prompt_snapshot,target_snapshot,language,strategy_key,length_profile,style,without_rag,status,attempt_count,next_attempt_at,created_at,updated_at)
                VALUES (UUID_TO_BIN(@done_task),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@batch),UUID_TO_BIN(@done),UUID_TO_BIN(@prompt),@prompt_snapshot,JSON_ARRAY(),'zh-CN','answer_guide','medium','professional',TRUE,'succeeded',1,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
                INSERT INTO article_generation_tasks (id,tenant_id,site_id,batch_id,article_id,prompt_id,prompt_snapshot,target_snapshot,language,strategy_key,length_profile,style,without_rag,status,attempt_count,next_attempt_at,lease_owner,lease_until,reservation_id,created_at,updated_at)
                VALUES (UUID_TO_BIN(@failing_task),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@batch),UUID_TO_BIN(@failing),NULL,@prompt_snapshot,JSON_ARRAY(),'zh-CN','answer_guide','medium','professional',TRUE,'running',1,UTC_TIMESTAMP(6),'worker-test',DATE_ADD(UTC_TIMESTAMP(6),INTERVAL 10 MINUTE),UUID_TO_BIN(@reservation),UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
                INSERT INTO credit_reservations (id,tenant_id,batch_id,task_id,amount,status,idempotency_key,created_at)
                VALUES (UUID_TO_BIN(@reservation),UUID_TO_BIN(@tenant),UUID_TO_BIN(@batch),UUID_TO_BIN(@failing_task),100,'held',CONCAT('task:',@failing_task),UTC_TIMESTAMP(6));
                """, ("@batch", batch.ToString()), ("@tenant", Tenant.ToString()), ("@site", Site.ToString()),
                ("@user", User.ToString()), ("@key", $"content-partial-{batch:N}"), ("@hash", $"{Guid.NewGuid():N}{Guid.NewGuid():N}"),
                ("@reservation", reservation.ToString()), ("@failing_task", failingTask.ToString()), ("@done_task", doneTask.ToString()),
                ("@done", done.ToString()), ("@failing", failing.ToString()), ("@prompt", Prompt.PromptId.ToString()),
                ("@prompt_snapshot", JsonSerializer.Serialize(Prompt, Web)));
            return (batch, failingTask, failing);
        }

        /// <summary>两篇的运行中批次：一篇由 worker-test 持有租约，一篇排队；各预占 100 积分。</summary>
        public async Task<(Guid Batch, Guid RunningTask, Guid RunningArticle)> RunningAndPendingBatchAsync()
        {
            var batch = Guid.NewGuid();
            var running = await ArticleAsync("运行中", DateTimeOffset.UtcNow.AddMinutes(-3));
            var queued = await ArticleAsync("排队中", DateTimeOffset.UtcNow.AddMinutes(-3));
            var runningTask = Guid.NewGuid();
            var queuedTask = Guid.NewGuid();
            var runningReservation = Guid.NewGuid();
            var queuedReservation = Guid.NewGuid();
            await ExecAsync("""
                UPDATE credit_accounts SET reserved_credits=200 WHERE tenant_id=UUID_TO_BIN(@tenant);
                INSERT INTO article_generation_batches (id,tenant_id,site_id,requested_by,idempotency_key,request_hash,status,total_tasks,reserved_credits,request_snapshot,preview_expires_at,created_at)
                VALUES (UUID_TO_BIN(@batch),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@user),@key,@hash,'running',2,200,JSON_OBJECT(),UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
                INSERT INTO article_generation_tasks (id,tenant_id,site_id,batch_id,article_id,prompt_id,prompt_snapshot,target_snapshot,language,strategy_key,length_profile,style,without_rag,status,attempt_count,next_attempt_at,lease_owner,lease_until,reservation_id,created_at,updated_at)
                VALUES (UUID_TO_BIN(@running_task),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@batch),UUID_TO_BIN(@running),UUID_TO_BIN(@prompt),@prompt_snapshot,JSON_ARRAY(),'zh-CN','answer_guide','medium','professional',TRUE,'running',1,UTC_TIMESTAMP(6),'worker-test',DATE_ADD(UTC_TIMESTAMP(6),INTERVAL 10 MINUTE),UUID_TO_BIN(@running_reservation),UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
                INSERT INTO article_generation_tasks (id,tenant_id,site_id,batch_id,article_id,prompt_id,prompt_snapshot,target_snapshot,language,strategy_key,length_profile,style,without_rag,status,attempt_count,next_attempt_at,reservation_id,created_at,updated_at)
                VALUES (UUID_TO_BIN(@queued_task),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@batch),UUID_TO_BIN(@queued),NULL,@prompt_snapshot,JSON_ARRAY(),'zh-CN','answer_guide','medium','professional',TRUE,'pending',0,DATE_ADD(UTC_TIMESTAMP(6),INTERVAL 1 HOUR),UUID_TO_BIN(@queued_reservation),UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
                INSERT INTO credit_reservations (id,tenant_id,batch_id,task_id,amount,status,idempotency_key,created_at)
                VALUES (UUID_TO_BIN(@running_reservation),UUID_TO_BIN(@tenant),UUID_TO_BIN(@batch),UUID_TO_BIN(@running_task),100,'held',CONCAT('task:',@running_task),UTC_TIMESTAMP(6)),
                       (UUID_TO_BIN(@queued_reservation),UUID_TO_BIN(@tenant),UUID_TO_BIN(@batch),UUID_TO_BIN(@queued_task),100,'held',CONCAT('task:',@queued_task),UTC_TIMESTAMP(6));
                """, ("@batch", batch.ToString()), ("@tenant", Tenant.ToString()), ("@site", Site.ToString()),
                ("@user", User.ToString()), ("@key", $"content-cancel-{batch:N}"), ("@hash", $"{Guid.NewGuid():N}{Guid.NewGuid():N}"),
                ("@running", running.ToString()), ("@queued", queued.ToString()), ("@running_task", runningTask.ToString()),
                ("@queued_task", queuedTask.ToString()), ("@running_reservation", runningReservation.ToString()),
                ("@queued_reservation", queuedReservation.ToString()), ("@prompt", Prompt.PromptId.ToString()),
                ("@prompt_snapshot", JsonSerializer.Serialize(Prompt, Web)));
            return (batch, runningTask, running);
        }

        /// <summary>读取一行（@tenant 自动绑定，@id 为调用方传入的标识）。</summary>
        public async Task<string[]> RowAsync(string sql, Guid id)
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new MySqlCommand(sql, connection);
            command.Parameters.AddWithValue("@tenant", Tenant.ToString());
            command.Parameters.AddWithValue("@id", id.ToString());
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync(), "期望读取到一行。");
            return Enumerable.Range(0, reader.FieldCount)
                .Select(index => Convert.ToString(reader.GetValue(index), System.Globalization.CultureInfo.InvariantCulture) ?? "")
                .ToArray();
        }

        /// <summary>插入一个占用指定请求摘要的空批次（只验证摘要占用，不需要任务与预占）。</summary>
        public Task BatchAsync(string requestHash)
            => ExecAsync("""
                INSERT INTO article_generation_batches (id,tenant_id,site_id,requested_by,idempotency_key,request_hash,status,total_tasks,request_snapshot,preview_expires_at,created_at)
                VALUES (UUID_TO_BIN(UUID()),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@user),@key,@hash,'failed',1,JSON_OBJECT(),UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
                """, ("@tenant", Tenant.ToString()), ("@site", Site.ToString()), ("@user", User.ToString()),
                ("@key", $"content-round-{Guid.NewGuid():N}"), ("@hash", requestHash));

        /// <summary>直接写入一条未确认的导入预览（不经网页读取）。</summary>
        public Task PreviewAsync(ArticleImportPreviewView preview)
            => ExecAsync("""
                INSERT INTO article_import_previews (id,tenant_id,site_id,created_by,request_hash,response_json,expires_at,created_at)
                VALUES (UUID_TO_BIN(@id),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@user),@hash,@response,@expires,UTC_TIMESTAMP(6));
                """, ("@id", preview.PreviewId.ToString()), ("@tenant", Tenant.ToString()), ("@site", Site.ToString()),
                ("@user", User.ToString()), ("@hash", preview.RequestHash), ("@response", JsonSerializer.Serialize(preview, Web)),
                ("@expires", preview.ExpiresAt.UtcDateTime));

        public Task TagAsync(Guid article, Guid tag)
            => ExecAsync("""
                INSERT INTO content_article_tags (tenant_id,site_id,article_id,tag_id,created_at)
                VALUES (UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@article),UUID_TO_BIN(@tag),UTC_TIMESTAMP(6));
                """, ("@tenant", Tenant.ToString()), ("@site", Site.ToString()), ("@article", article.ToString()), ("@tag", tag.ToString()));

        /// <summary>插入一条已分析完成（已发布分析版本）的回答，并对给定 url_hash 写入引用事实；首次调用时建立运行与分析版本。</summary>
        public async Task<Guid> CheckAsync(DateTimeOffset captured, string engine, params string[] urlHashes)
        {
            if (targets.Count == 0)
            {
                targets["chatgpt"] = Guid.NewGuid();
                targets["doubao"] = Guid.NewGuid();
                await ExecAsync("""
                    INSERT INTO visibility_runs (id, tenant_id, site_id, status, idempotency_key, requested_at, total_checks, succeeded_checks, created_at, updated_at)
                    VALUES (UUID_TO_BIN(@run), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), 'succeeded', @key, UTC_TIMESTAMP(6), 0, 0, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                    INSERT INTO run_engines (id, run_id, engine, provider, target_key, capture_mode, country, locale, created_at)
                    VALUES (UUID_TO_BIN(@chatgpt), UUID_TO_BIN(@run), 'chatgpt', 'cloro', @chatgptKey, 'scraped', 'US', 'en-US', UTC_TIMESTAMP(6)),
                           (UUID_TO_BIN(@doubao), UUID_TO_BIN(@run), 'doubao', 'cloro', @doubaoKey, 'scraped', 'CN', 'zh-CN', UTC_TIMESTAMP(6));
                    INSERT INTO analysis_releases (id, tenant_id, site_id, version, subject_config_version, subject_snapshot, extractor_version, metric_version, status, created_at, published_at)
                    VALUES (UUID_TO_BIN(@release), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), 1, 1, JSON_OBJECT('brand','Acme Content'), 'test', 'test', 'published', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                    """, ("@run", run.ToString()), ("@tenant", Tenant.ToString()), ("@site", Site.ToString()),
                    ("@key", Guid.NewGuid().ToString()), ("@release", release.ToString()),
                    ("@chatgpt", targets["chatgpt"].ToString()), ("@chatgptKey", new string('c', 64)),
                    ("@doubao", targets["doubao"].ToString()), ("@doubaoKey", new string('d', 64)));
            }
            var check = Guid.NewGuid();
            await ExecAsync("""
                INSERT INTO visibility_checks (id, tenant_id, site_id, run_id, prompt_id, engine, provider, status, prompt_snapshot, captured_at, run_target_id, sample_index, created_at, updated_at)
                VALUES (UUID_TO_BIN(@check), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), UUID_TO_BIN(@run), UUID_TO_BIN(@prompt), @engine, 'cloro', 'succeeded', 'snapshot', @captured, UUID_TO_BIN(@target), @sample, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                INSERT INTO analytics_check_arrivals (check_id, tenant_id, site_id, captured_at, arrived_at)
                VALUES (UUID_TO_BIN(@check), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), @captured, UTC_TIMESTAMP(6));
                INSERT INTO analysis_check_work (release_id, check_id, tenant_id, site_id, status, created_at, updated_at)
                VALUES (UUID_TO_BIN(@release), UUID_TO_BIN(@check), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), 'completed', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                INSERT INTO analysis_events (release_id, check_id, completed_at) VALUES (UUID_TO_BIN(@release), UUID_TO_BIN(@check), UTC_TIMESTAMP(6));
                INSERT INTO check_analysis (release_id, check_id, tenant_id, site_id, status, configuration_missing, completed_event_id, completed_at)
                SELECT UUID_TO_BIN(@release), UUID_TO_BIN(@check), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), 'completed', FALSE, e.id, UTC_TIMESTAMP(6)
                FROM analysis_events e WHERE e.release_id=UUID_TO_BIN(@release) AND e.check_id=UUID_TO_BIN(@check);
                """, ("@check", check.ToString()), ("@tenant", Tenant.ToString()), ("@site", Site.ToString()), ("@run", run.ToString()),
                ("@prompt", Prompt.PromptId.ToString()), ("@engine", engine), ("@captured", captured.UtcDateTime),
                ("@target", targets[engine].ToString()), ("@sample", ++sample), ("@release", release.ToString()));
            var position = 0;
            foreach (var hash in urlHashes)
                await ExecAsync("""
                    INSERT INTO citation_events (release_id, check_id, url_hash, normalized_url, domain, title, first_position, url_version, normalization_status)
                    VALUES (UUID_TO_BIN(@release), UUID_TO_BIN(@check), @hash, CONCAT('https://cited.test/', @hash), 'cited.test', NULL, @position, 'url-v1', 'complete');
                    """, ("@release", release.ToString()), ("@check", check.ToString()), ("@hash", hash), ("@position", ++position));
            return check;
        }

        /// <summary>只删除本夹具租户的内容数据；品牌、提问等管理数据留在独立测试库中待统一清理。</summary>
        public async Task CleanupAsync()
        {
            foreach (var sql in new[]
                     {
                         "DELETE FROM analytics_snapshot_projections WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM analytics_snapshots WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM content_publication_links WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM content_article_references WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM content_references WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM content_article_tags WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM content_monitor_scopes WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM article_monitoring_round_items WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM credit_ledger WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM article_monitoring_rounds WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM content_citation_discovery_state WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM credit_reservations WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM article_generation_tasks WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM article_generation_batches WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM article_generation_previews WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM article_import_previews WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "UPDATE content_articles SET current_version_id=NULL WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM content_article_versions WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM content_articles WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE c FROM citation_events c JOIN check_analysis a ON a.release_id=c.release_id AND a.check_id=c.check_id WHERE a.tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM check_analysis WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE e FROM analysis_events e JOIN analysis_check_work w ON w.release_id=e.release_id AND w.check_id=e.check_id WHERE w.tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM analysis_check_work WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM analysis_releases WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM analytics_check_arrivals WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM visibility_checks WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE e FROM run_engines e JOIN visibility_runs r ON r.id=e.run_id WHERE r.tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM visibility_runs WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM credit_accounts WHERE tenant_id=UUID_TO_BIN(@tenant);"
                     })
                await ExecAsync(sql, ("@tenant", Tenant.ToString()));
        }

        private async Task ExecAsync(string sql, params (string Name, object? Value)[] parameters)
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new MySqlCommand(sql, connection);
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            await command.ExecuteNonQueryAsync();
        }
    }
}
