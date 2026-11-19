using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;

namespace Jogeoai.Tests;

/// <summary>第 1 步创作准备与提交前校验：纯函数规则 + 接口替身，不连接数据库、不调用模型。</summary>
public sealed class ArticleStudioPrepareTests
{
    [Theory]
    [InlineData(0, 0, 0, ArticleOptimizationGoal.Mention, ArticleStudioGoalBasis.NoData)]
    [InlineData(6, 0, 3, ArticleOptimizationGoal.Mention, ArticleStudioGoalBasis.RecentData)]
    [InlineData(6, 2, 5, ArticleOptimizationGoal.Ranking, ArticleStudioGoalBasis.RecentData)]
    [InlineData(6, 5, 2, ArticleOptimizationGoal.Defend, ArticleStudioGoalBasis.RecentData)]
    [InlineData(6, 3, 3, ArticleOptimizationGoal.Defend, ArticleStudioGoalBasis.RecentData)]
    [InlineData(6, 4, 0, ArticleOptimizationGoal.Defend, ArticleStudioGoalBasis.RecentData)]
    public void GoalFollowsRecentMentionsAndDefaultsToMentionWithoutData(long eligible, long brand, long topCompetitor,
        ArticleOptimizationGoal goal, ArticleStudioGoalBasis basis)
        => Assert.Equal((goal, basis), ArticleStudioRules.DeriveGoal(eligible, brand, topCompetitor));

    [Fact]
    public void GoalMapsToExistingWritingStrategiesAndLocaleToSupportedLanguage()
    {
        Assert.Equal("answer_guide", ArticleStudioRules.StrategyFor(ArticleOptimizationGoal.Mention));
        Assert.Equal("product_selection", ArticleStudioRules.StrategyFor(ArticleOptimizationGoal.Ranking));
        Assert.Equal("pain_comparison", ArticleStudioRules.StrategyFor(ArticleOptimizationGoal.Defend));
        Assert.All(new[] { ArticleOptimizationGoal.Mention, ArticleOptimizationGoal.Ranking, ArticleOptimizationGoal.Defend },
            goal => Assert.True(ArticleWritingRules.IsSupportedStrategy(ArticleStudioRules.StrategyFor(goal))));
        Assert.Equal("en-US", ArticleStudioRules.LanguageFor("en-GB"));
        Assert.Equal("zh-CN", ArticleStudioRules.LanguageFor("zh-TW"));
        Assert.Equal("zh-CN", ArticleStudioRules.LanguageFor(null));
        Assert.Equal(100m, ArticleGenerationPricing.UnitPrice);
    }

    [Fact]
    public void CompetitorsUseOnlyConfiguredMentionedSubjectsTopEight()
    {
        Assert.Equal(new ArticleStudioCompetitorsView(false, ArticleStudioCompetitorReason.NoneConfigured, 0, []),
            ArticleStudioRules.SelectCompetitors(new Dictionary<Guid, string>(), new Dictionary<Guid, long> { [Guid.NewGuid()] = 9 }));

        var quiet = Guid.NewGuid();
        var noData = ArticleStudioRules.SelectCompetitors(new Dictionary<Guid, string> { [quiet] = "沉默竞品" },
            new Dictionary<Guid, long> { [quiet] = 0, [Guid.NewGuid()] = 7 });
        Assert.False(noData.Available);
        Assert.Equal(ArticleStudioCompetitorReason.NoRecentData, noData.Reason);
        Assert.Equal(1, noData.ConfiguredCount);

        var configured = Enumerable.Range(1, 10).ToDictionary(_ => Guid.NewGuid(), index => $"竞品{index:00}");
        var mentions = configured.Keys.Select((id, index) => (id, index)).ToDictionary(item => item.id, item => (long)item.index);
        var unconfigured = Guid.NewGuid();
        mentions[unconfigured] = 100;
        var top = ArticleStudioRules.SelectCompetitors(configured, mentions);
        Assert.True(top.Available);
        Assert.Null(top.Reason);
        Assert.Equal(8, top.Items.Count);
        Assert.DoesNotContain(top.Items, item => item.SubjectId == unconfigured);
        Assert.Equal(Enumerable.Range(1, 8), top.Items.Select(item => item.Rank));
        Assert.Equal(top.Items.Select(item => item.Mentions).OrderByDescending(value => value), top.Items.Select(item => item.Mentions));
        Assert.All(top.Items, item => Assert.True(item.Mentions > 0));
    }

    [Fact]
    public async Task ZeroKnowledgeHitsReturnsHitCountZeroAndNoCompetitorsReportsNoneConfigured()
    {
        var site = Guid.NewGuid(); var prompt = Guid.NewGuid(); var withoutRagPreview = Guid.NewGuid();
        var previews = new List<ArticleMaterialPreviewRequest>();
        var materials = StudioFakes.Of<IArticleMaterialSelectionService>((method, args) =>
        {
            var request = (ArticleMaterialPreviewRequest)args![3]!;
            previews.Add(request);
            if (!request.WithoutRag)
                throw new CollectorStoreException("KNOWLEDGE_CONTEXT_EMPTY", "未找到可用资料。", 422);
            return Task.FromResult(new ArticleMaterialPreviewView(withoutRagPreview, null, "hash", new ArticleKnowledgeSelection(), [], [], [], 0, 0,
                DateTimeOffset.UtcNow.AddMinutes(15), true, null, [prompt], "zh-CN"));
        });
        var service = Service(site, prompt, materials, competitors: [],
            analytics: (method, _) => method switch
            {
                nameof(IAnalyticsQueryStore.ShareOfVoiceAsync) => throw new CollectorStoreException("RESOURCE_NOT_FOUND", "工作区不存在。", 404),
                nameof(IAnalyticsQueryStore.TopCitedUrlsForPromptAsync) => throw new CollectorStoreException("ANALYSIS_VERSION_NOT_READY", "未就绪", 503),
                _ => throw new InvalidOperationException(method)
            });

        var view = await service.PrepareAsync(Guid.NewGuid(), site, Guid.NewGuid(), canWrite: true, prompt);

        var selected = Assert.IsType<ArticleStudioSelectedPromptView>(view.Selected);
        Assert.Equal(0, selected.Knowledge!.HitCount);
        Assert.True(selected.Knowledge.WithoutRag);
        Assert.Equal(withoutRagPreview, selected.Knowledge.MaterialPreviewId);
        Assert.Equal([false, true], previews.Select(item => item.WithoutRag));
        Assert.Equal(ArticleOptimizationGoal.Mention, selected.OptimizationGoal);
        Assert.Equal(ArticleStudioGoalBasis.NoData, selected.GoalBasis);
        Assert.False(selected.Competitors.Available);
        Assert.Equal(ArticleStudioCompetitorReason.NoneConfigured, selected.Competitors.Reason);
        Assert.Equal(0, selected.EngineCount);
        Assert.Equal(100m, view.UnitPrice);
        Assert.Equal(5000, view.RequirementsMaxLength);
        Assert.True(view.ProviderConfigured);
        Assert.Equal("分组甲", Assert.Single(view.Prompts).GroupName);
    }

    [Fact]
    public async Task MultiSourceMentionsAreSummedForGoalAndTopCompetitorsWithNoRecentDataReason()
    {
        var site = Guid.NewGuid(); var prompt = Guid.NewGuid();
        var brand = Guid.NewGuid(); var rival = Guid.NewGuid(); var other = Guid.NewGuid();
        var service = Service(site, prompt, Hits(prompt, 3), competitors: [Competitor(rival, "竞品甲"), Competitor(other, "竞品乙")],
            analytics: (method, args) => method switch
            {
                // Two sources: brand 1+1=2, rival 2+1=3, other 0 → brand mentioned but not first → ranking.
                nameof(IAnalyticsQueryStore.ShareOfVoiceAsync) => Task.FromResult(StudioFakes.Sov(
                    StudioFakes.Group(4, (brand, "本品牌", true, 1), (rival, "竞品甲", false, 2), (other, "竞品乙", false, 0)),
                    StudioFakes.Group(3, (brand, "本品牌", true, 1), (rival, "竞品甲", false, 1)))),
                nameof(IAnalyticsQueryStore.TopCitedUrlsForPromptAsync) => Task.FromResult(new PromptCitedSourcesView(3, 1, [])),
                _ => throw new InvalidOperationException(method)
            });

        var view = await service.PrepareAsync(Guid.NewGuid(), site, Guid.NewGuid(), canWrite: true, prompt);
        var selected = view.Selected!;
        Assert.Equal(ArticleOptimizationGoal.Ranking, selected.OptimizationGoal);
        Assert.Equal(2, selected.BrandMentions);
        Assert.Equal(7, selected.EligibleChecks);
        Assert.Equal(3, selected.Knowledge!.HitCount);
        Assert.False(selected.Knowledge.WithoutRag);
        var competitor = Assert.Single(selected.Competitors.Items);
        Assert.Equal((rival, 3L, 1), (competitor.SubjectId, competitor.Mentions, competitor.Rank));
        Assert.Equal(3, selected.EngineCount);

        var silent = Service(site, prompt, Hits(prompt, 1), competitors: [Competitor(other, "竞品乙")],
            analytics: (method, _) => method == nameof(IAnalyticsQueryStore.ShareOfVoiceAsync)
                ? Task.FromResult(StudioFakes.Sov())
                : Task.FromResult(new PromptCitedSourcesView(0, 0, [])));
        var empty = (await silent.PrepareAsync(Guid.NewGuid(), site, Guid.NewGuid(), canWrite: false, prompt)).Selected!;
        Assert.Equal(ArticleStudioCompetitorReason.NoRecentData, empty.Competitors.Reason);
        Assert.Equal(ArticleStudioGoalBasis.NoData, empty.GoalBasis);
        Assert.Null(empty.Knowledge); // read-only role: no material preview is created
    }

    [Fact]
    public async Task PrepareWithoutPromptOnlyListsPromptsPriceAndBalance()
    {
        var site = Guid.NewGuid(); var prompt = Guid.NewGuid();
        var service = Service(site, prompt, StudioFakes.Unused<IArticleMaterialSelectionService>(), [],
            (method, _) => throw new InvalidOperationException(method));
        var view = await service.PrepareAsync(Guid.NewGuid(), site, Guid.NewGuid(), canWrite: true, null);
        Assert.Null(view.Selected);
        Assert.Equal(250m, view.AvailableCredits);
        Assert.Single(view.Prompts);
    }

    [Fact]
    public async Task SubmitRejectsLongRequirementsMissingKeyAndPromptBeforeAnyStoreCall()
    {
        var service = new ArticleStudioService(StudioFakes.Unused<IManagementStore>(), StudioFakes.Unused<ICreditStore>(),
            StudioFakes.Unused<IResearchStore>(), StudioFakes.Unused<IArticleMaterialSelectionService>(), StudioFakes.Unused<IContentStore>(),
            StudioFakes.Unused<IArticleStudioStore>(), StudioFakes.Unused<IAnalyticsQueryStore>(), StudioFakes.Unused<IKnowledgeRetrievalService>());
        var prompt = Guid.NewGuid();
        var tooLong = await Assert.ThrowsAsync<CollectorStoreException>(() => service.SubmitAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            new ArticleStudioGenerationRequest(prompt, Requirements: new string('要', 5001)), "key"));
        Assert.Equal(("INVALID_GENERATION_REQUEST", 400), (tooLong.Code, tooLong.StatusCode));
        Assert.Equal("IDEMPOTENCY_KEY_REQUIRED", (await Assert.ThrowsAsync<CollectorStoreException>(() => service.SubmitAsync(Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), new ArticleStudioGenerationRequest(prompt), " "))).Code);
        Assert.Equal("INVALID_PROMPT_SELECTION", (await Assert.ThrowsAsync<CollectorStoreException>(() => service.SubmitAsync(Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), new ArticleStudioGenerationRequest(Guid.Empty), "key"))).Code);
    }

    [Fact]
    public async Task SubmitReplayWithSameKeyReturnsStoredAcceptanceAndDifferentRequestConflicts()
    {
        var site = Guid.NewGuid(); var prompt = Guid.NewGuid();
        var request = new ArticleStudioGenerationRequest(prompt, Requirements: "保留五千字以内的要求");
        var accepted = new ArticleStudioGenerationAccepted(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 100m, false,
            ArticleOptimizationGoal.Mention, false, true, "/poll");
        var store = StudioFakes.Of<IArticleStudioStore>((method, _) => method == nameof(IArticleStudioStore.FindStudioSubmissionAsync)
            ? Task.FromResult<(ArticleStudioGenerationAccepted, string)?>((accepted, ArticleStudioService.RequestHash(site, request)))
            : throw new InvalidOperationException(method));
        var service = new ArticleStudioService(StudioFakes.Unused<IManagementStore>(), StudioFakes.Unused<ICreditStore>(),
            StudioFakes.Unused<IResearchStore>(), StudioFakes.Unused<IArticleMaterialSelectionService>(), StudioFakes.Unused<IContentStore>(),
            store, StudioFakes.Unused<IAnalyticsQueryStore>(), StudioFakes.Unused<IKnowledgeRetrievalService>());

        Assert.Same(accepted, await service.SubmitAsync(Guid.NewGuid(), site, Guid.NewGuid(), request, "same-key"));
        Assert.Equal("IDEMPOTENCY_CONFLICT", (await Assert.ThrowsAsync<CollectorStoreException>(() => service.SubmitAsync(Guid.NewGuid(), site,
            Guid.NewGuid(), request with { IncludeCompetitors = true }, "same-key"))).Code);
    }

    private static CompetitorView Competitor(Guid id, string name)
        => new(id, name, [], [$"{name}.example"], "active", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static IArticleMaterialSelectionService Hits(Guid prompt, int count)
        => StudioFakes.Of<IArticleMaterialSelectionService>((_, _) => Task.FromResult(new ArticleMaterialPreviewView(Guid.NewGuid(), Guid.NewGuid(),
            "hash", new ArticleKnowledgeSelection(), [], [], [], count, 100, DateTimeOffset.UtcNow.AddMinutes(15), false, null, [prompt], "zh-CN")));

    private static ArticleStudioService Service(Guid site, Guid prompt, IArticleMaterialSelectionService materials,
        IReadOnlyList<CompetitorView> competitors, Func<string, object?[]?, object?> analytics)
    {
        var group = Guid.NewGuid();
        var management = StudioFakes.Of<IManagementStore>((method, _) => method switch
        {
            nameof(IManagementStore.GetBrandAsync) => Task.FromResult<BrandView?>(new BrandView(site, "本品牌", null, [], [], "CN", "zh-CN",
                "Asia/Shanghai", "active", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)),
            nameof(IManagementStore.ListPromptsAsync) => Task.FromResult(new CursorPage<PromptView>([StudioFakes.Prompt(prompt, groupId: group)], null)),
            nameof(IManagementStore.ListPromptGroupsAsync) => Task.FromResult(new PromptGroupPage(
                [new PromptGroupView(group, "分组甲", 1, 1, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)], null, new PromptGroupCounts(0, 0))),
            nameof(IManagementStore.GetPromptAsync) => Task.FromResult<PromptView?>(StudioFakes.Prompt(prompt, groupId: group)),
            nameof(IManagementStore.ListCompetitorsAsync) => Task.FromResult(new CursorPage<CompetitorView>(competitors, null)),
            _ => throw new InvalidOperationException(method)
        });
        var credits = StudioFakes.Of<ICreditStore>((_, _) => Task.FromResult(new CreditAccountView(250, 0, 250, 0, 1, DateTimeOffset.UtcNow)));
        var research = StudioFakes.Of<IResearchStore>((_, _) => Task.FromResult<IReadOnlyList<ResearchProviderSettingsView>>(
            [new ResearchProviderSettingsView(ArticleGenerationProvider.ProviderName, "deepseek-flash", "https://api.deepseek.com/chat/completions",
                true, 100, false, 600, true, "****abcd", DateTimeOffset.UtcNow)]));
        var studio = StudioFakes.Of<IArticleStudioStore>((method, _) => method == nameof(IArticleStudioStore.GetLatestStudioArticleAsync)
            ? Task.FromResult<ArticleStudioExistingArticleView?>(null) : throw new InvalidOperationException(method));
        return new ArticleStudioService(management, credits, research, materials, StudioFakes.Unused<IContentStore>(), studio,
            StudioFakes.Of<IAnalyticsQueryStore>(analytics), StudioFakes.Unused<IKnowledgeRetrievalService>(),
            clock: new SteppingClock(new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero)));
    }
}
