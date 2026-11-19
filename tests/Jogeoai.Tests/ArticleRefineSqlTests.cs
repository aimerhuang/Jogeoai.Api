using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;

namespace Jogeoai.Tests;

/// <summary>
/// 智能微调计费的 SQL 回归（AC-B3）：隔离库（名称含 _test、已执行 001–041，JOGEOAI_CONTENT_TEST_CONNECTION）+ 假 Provider，
/// 不调用 DeepSeek。断言预占 / 结算 / 释放的账户、流水与 credits/usage 口径；未提供连接时跳过。
/// </summary>
public sealed class ArticleRefineSqlTests
{
    private const string Body = "## 选型\n\n第一段原文。\n\n第二段原文。";

    [ContentMySqlFact]
    public async Task SuccessCapturesFortyOnceReplayIsFreeAndUsageShowsArticleRefine()
    {
        var f = await EditorSqlFixture.CreateAsync();
        var (article, generated) = await f.ArticleAsync(Body);
        var provider = new ScriptedProvider(_ => new ArticleRefineModelOutput("微调标题", "## 选型\n\n改写后的正文。", null));
        var service = Service(f, provider);

        var result = await service.RefineAsync(f.Tenant, f.Site, article, f.User,
            new ArticleRefineRequest(ArticleRefineScope.Whole, null, "更口语化", generated, 0), "refine-sql-1");
        Assert.Equal((ArticleVersionKind.Refined, 2, 0, false, 40m), (result.VersionKind, result.VersionNo, result.EditRevision, result.Replayed, result.ChargedCredits));
        Assert.Equal(["960", "0", "40"], await f.AccountAsync());
        var detail = (await f.Store.GetArticleAsync(f.Tenant, f.Site, article))!;
        Assert.Equal((result.VersionId, ArticleVersionKind.Refined, "微调标题", "## 选型\n\n改写后的正文。"),
            (detail.CurrentVersion!.VersionId, detail.CurrentVersion.VersionKind, detail.CurrentVersion.Title, detail.CurrentVersion.BodyMarkdown));
        Assert.Equal("合成摘要", detail.CurrentVersion.Summary);

        var replay = await service.RefineAsync(f.Tenant, f.Site, article, f.User,
            new ArticleRefineRequest(ArticleRefineScope.Whole, null, "更口语化", generated, 0), "refine-sql-1");
        Assert.True(replay.Replayed);
        Assert.Equal(result.VersionId, replay.VersionId);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(["960", "0", "40"], await f.AccountAsync());
        Assert.Equal(1, await f.CountAsync("SELECT COUNT(*) FROM credit_ledger WHERE tenant_id=UUID_TO_BIN(@tenant) AND entry_type='reserve' AND refine_operation_id IS NOT NULL;"));
        Assert.Equal(1, await f.CountAsync("SELECT COUNT(*) FROM credit_ledger WHERE tenant_id=UUID_TO_BIN(@tenant) AND entry_type='capture' AND refine_operation_id IS NOT NULL AND amount=0;"));

        var usage = await new CreditStore(f.Database).UsageAsync(f.Tenant, null, new CreditUsageRequest(TimeZone: "UTC"));
        var refine = Assert.Single(usage.Totals);
        Assert.Equal((CreditUsageType.ArticleRefine, 40m, 1), (refine.Type, refine.Credits, refine.Count));
        Assert.Equal(CreditUsageType.ArticleRefine, usage.Latest!.Type);
        var member = await new CreditStore(f.Database).UsageAsync(f.Tenant, f.User, new CreditUsageRequest(TimeZone: "UTC"));
        Assert.Equal(40m, member.TotalCredits);
    }

    [ContentMySqlFact]
    public async Task FailuresReleaseWithoutChargingAndSelectionMustMatchExactlyOnce()
    {
        var f = await EditorSqlFixture.CreateAsync();
        var (article, generated) = await f.ArticleAsync(Body);

        var failing = Service(f, new ScriptedProvider(_ => throw new CollectorStoreException("ARTICLE_PROVIDER_RETRYABLE", "busy", 503)));
        var error = await Assert.ThrowsAsync<CollectorStoreException>(() => failing.RefineAsync(f.Tenant, f.Site, article, f.User,
            new ArticleRefineRequest(ArticleRefineScope.Whole, null, "改写", generated, 0), "refine-fail"));
        Assert.Equal(503, error.StatusCode);
        Assert.Equal(["1000", "0", "0"], await f.AccountAsync());
        Assert.Equal("released", await f.TextAsync("SELECT status FROM article_refine_operations WHERE tenant_id=UUID_TO_BIN(@tenant) AND idempotency_key='refine-fail';"));
        Assert.Equal(1, await f.CountAsync("SELECT COUNT(*) FROM credit_ledger WHERE tenant_id=UUID_TO_BIN(@tenant) AND entry_type='release' AND amount=40 AND refine_operation_id IS NOT NULL;"));
        // Replaying the failed key reports the same failure without a new hold.
        Assert.Equal("ARTICLE_PROVIDER_RETRYABLE", (await Assert.ThrowsAsync<CollectorStoreException>(() => failing.RefineAsync(f.Tenant, f.Site, article, f.User,
            new ArticleRefineRequest(ArticleRefineScope.Whole, null, "改写", generated, 0), "refine-fail"))).Code);

        var unused = Service(f, new ScriptedProvider(_ => throw new InvalidOperationException("must not be called")));
        foreach (var selection in new[] { "不存在的段落", "原文。" })
        {
            var missing = await Assert.ThrowsAsync<CollectorStoreException>(() => unused.RefineAsync(f.Tenant, f.Site, article, f.User,
                new ArticleRefineRequest(ArticleRefineScope.Selection, selection, "精简", generated, 0), $"refine-sel-{selection.Length}"));
            Assert.Equal((ArticleEditorErrorCodes.SelectionNotFound, 422), (missing.Code, missing.StatusCode));
        }
        Assert.Equal(1, await f.CountAsync("SELECT COUNT(*) FROM article_refine_operations WHERE tenant_id=UUID_TO_BIN(@tenant);"));

        var selected = await Service(f, new ScriptedProvider(_ => new ArticleRefineModelOutput(null, null, "第一段精简。")))
            .RefineAsync(f.Tenant, f.Site, article, f.User, new ArticleRefineRequest(ArticleRefineScope.Selection, "第一段原文。", "精简", generated, 0), "refine-sel");
        Assert.Equal("## 选型\n\n第一段精简。\n\n第二段原文。", selected.BodyMarkdown);
        Assert.Equal(["960", "0", "40"], await f.AccountAsync());

        // The article changes while the model runs: the result is discarded and the hold released.
        var current = selected.VersionId;
        var racing = Service(f, new ScriptedProvider(_ =>
        {
            f.Store.SaveDraftAsync(f.Tenant, f.Site, article, f.User, new ArticleDraftRequest("并发标题", "并发编辑", current, 0)).GetAwaiter().GetResult();
            return new ArticleRefineModelOutput(null, "过期的改写", null);
        }));
        var conflict = await Assert.ThrowsAsync<CollectorStoreException>(() => racing.RefineAsync(f.Tenant, f.Site, article, f.User,
            new ArticleRefineRequest(ArticleRefineScope.Whole, null, "改写", current, 0), "refine-race"));
        Assert.Equal((ArticleEditorErrorCodes.VersionConflict, 409), (conflict.Code, conflict.StatusCode));
        Assert.Equal(["960", "0", "40"], await f.AccountAsync());
        Assert.Equal("并发编辑", (await f.Store.GetArticleAsync(f.Tenant, f.Site, article))!.CurrentVersion!.BodyMarkdown);

        await f.ExecAsync("UPDATE credit_accounts SET available_credits=10 WHERE tenant_id=UUID_TO_BIN(@tenant);", ("@tenant", f.Tenant.ToString()));
        var draft = (await f.Store.GetArticleAsync(f.Tenant, f.Site, article))!.CurrentVersion!;
        var poor = await Assert.ThrowsAsync<CollectorStoreException>(() => unused.RefineAsync(f.Tenant, f.Site, article, f.User,
            new ArticleRefineRequest(ArticleRefineScope.Whole, null, "改写", draft.VersionId, draft.EditRevision), "refine-poor"));
        Assert.Equal(("CREDIT_INSUFFICIENT", 402), (poor.Code, poor.StatusCode));
    }

    private static ArticleRefineService Service(EditorSqlFixture f, ScriptedProvider provider)
        => new(f.Store, StudioFakes.Of<IResearchStore>((method, _) => method == nameof(IResearchStore.GetProviderConfigurationAsync)
            ? Task.FromResult<ResearchProviderConfiguration?>(new ResearchProviderConfiguration(ArticleGenerationProvider.ProviderName,
                ArticleGenerationProvider.ModelName, "https://api.deepseek.example/chat/completions", "synthetic-key", false, 120, 1))
            : throw new InvalidOperationException(method)), provider);

    private sealed class ScriptedProvider(Func<ArticleRefineModelRequest, ArticleRefineModelOutput> respond) : IArticleRefineProvider
    {
        public int Calls { get; private set; }

        public Task<ArticleRefineModelOutput> RefineAsync(ResearchProviderConfiguration configuration, ArticleRefineModelRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(respond(request));
        }
    }
}
