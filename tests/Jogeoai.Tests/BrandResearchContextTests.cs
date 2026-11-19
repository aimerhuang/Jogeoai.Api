using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;

namespace Jogeoai.Tests;

public sealed class BrandResearchContextTests
{
    [Fact]
    public void ChangedCorrectionMarketLanguageWebsiteOrStrategyInvalidatesProfile()
    {
        var request = BrandResearchProviderTests.Request;
        var context = BrandResearchProviderTests.Context() with
        {
            BusinessDescription = "提供管道摄像头", Profile = new("v1", "管道检查", new("品牌", "web-1", "品牌原文"),
                new("设备", "web-1", "产品原文"), [], [], [], [], [], [], []),
        };
        Assert.Same(context.Profile, ResearchContextRules.Create(request, context).Profile);
        foreach (var changed in new[] { request with { BusinessDescription = "只做维修服务" }, request with { Market = "US" },
            request with { Language = "en-US" }, request with { Website = "https://other.example/" }, request with { BrandName = "另一个品牌" } })
            Assert.Null(ResearchContextRules.Create(changed, context).Profile);
        Assert.Null(ResearchContextRules.Create(request, context with { StrategyVersion = "old" }).Profile);
        Assert.Null(ResearchContextRules.Create(request, context with { Evidence = [context.Evidence[0] with { FetchedAt = DateTimeOffset.UtcNow.AddDays(-2) }] }).Profile);
    }
    [Fact]
    public void RefreshKeepsCorrectionsSourcesExclusionsAndFeedback()
    {
        var context = BrandResearchProviderTests.Context() with { BusinessDescription = "排水管检查", ExcludedQuestions = ["ＡＢＣ 怎么选？"] };
        var next = ResearchContextRules.Create(BrandResearchProviderTests.Request with
        { ExcludedQuestions = ["abc 怎么选?", "新的问题"], Feedback = [new("泛泛的问题", "generic")] }, context);
        Assert.Equal(2, next.ExcludedQuestions.Count);
        Assert.Equal("排水管检查", next.BusinessDescription);
        Assert.Equal("user", next.Evidence.Last().Id);
        Assert.Single(next.Feedback);
        Assert.NotEqual(ResearchContextRules.ProfileVersion(context), ResearchContextRules.ProfileVersion(next));
    }

    internal static BrandKnowledgeResearchItem Item(string title, string content, BrandKnowledgeDimension dimension = BrandKnowledgeDimension.BrandIntro,
        Guid? id = null) => new(id ?? Guid.NewGuid(), dimension, title, content, false, new DateTimeOffset(2026, 11, 3, 1, 2, 3, TimeSpan.Zero));

    [Fact]
    public void KnowledgeItemsBecomeUserOwnedEvidenceWithStableIdTitleHashAndUpdateTime()
    {
        var item = Item("公司介绍", "研发工业检测设备与管道摄像头。", BrandKnowledgeDimension.ProductService);
        var evidence = Assert.Single(ResearchContextRules.KnowledgeEvidence([item]));
        Assert.Equal($"knowledge:{item.ItemId:N}", evidence.Id);
        Assert.Null(evidence.Url);
        Assert.Equal("品牌知识·产品/服务·公司介绍", evidence.Title);
        Assert.Equal("公司介绍\n研发工业检测设备与管道摄像头。", evidence.Excerpt);
        Assert.Equal(ResearchContextRules.Hash(evidence.Excerpt), evidence.ContentHash);
        Assert.Equal(item.UpdatedAt, evidence.FetchedAt);
        Assert.True(ResearchContextRules.IsKnowledgeEvidence(evidence));
        Assert.Throws<ArgumentException>(() => ResearchContextRules.Create(BrandResearchProviderTests.Request, null, null,
            [evidence with { Id = "web-9" }]));
    }

    [Fact]
    public void KnowledgeEvidenceReplacesPreviousAndChangedItemsInvalidateProfile()
    {
        var request = BrandResearchProviderTests.Request with { BusinessDescription = "提供管道摄像头" };
        var kept = Item("核心卖点", "推杆可达 120 米。", BrandKnowledgeDimension.SellingPoint);
        var edited = Item("公司介绍", "研发工业检测设备。");
        var previousKnowledge = ResearchContextRules.KnowledgeEvidence([edited, kept]);
        var previous = ResearchContextRules.Create(request, BrandResearchProviderTests.Context(), null, previousKnowledge) with
        {
            Profile = new("v1", "管道检查", new("品牌", "web-1", "品牌原文"), new("设备", "web-1", "产品原文"), [], [], [], [], [], [], []),
        };
        Assert.Equal(["web-1", previousKnowledge[0].Id, previousKnowledge[1].Id, "user"], previous.Evidence.Select(e => e.Id));

        // 同一批条目（顺序无关）：保留画像，且 knowledge: 证据不重复累积。
        var unchanged = ResearchContextRules.Create(request, previous, null, ResearchContextRules.KnowledgeEvidence([kept, edited]));
        Assert.Same(previous.Profile, unchanged.Profile);
        Assert.Equal(2, unchanged.Evidence.Count(ResearchContextRules.IsKnowledgeEvidence));
        Assert.Equal("user", unchanged.Evidence.Last().Id);

        // 编辑、删除、新增、维度变化都必须让画像失效，并以本次条目替换上一轮条目证据。
        var editedNow = ResearchContextRules.KnowledgeEvidence([edited with { Content = "只做排水管检测。" }, kept]);
        var next = ResearchContextRules.Create(request, previous, null, editedNow);
        Assert.Null(next.Profile);
        Assert.Equal("collecting", next.Phase);
        Assert.Equal(editedNow.Select(e => e.ContentHash), next.Evidence.Where(ResearchContextRules.IsKnowledgeEvidence).Select(e => e.ContentHash));
        Assert.Contains(next.Evidence, e => e.Id == "web-1");
        Assert.Null(ResearchContextRules.Create(request, previous, null, ResearchContextRules.KnowledgeEvidence([kept])).Profile);
        Assert.Null(ResearchContextRules.Create(request, previous, null,
            ResearchContextRules.KnowledgeEvidence([edited, kept, Item("常见问题", "支持上门吗？", BrandKnowledgeDimension.Faq)])).Profile);
        Assert.Null(ResearchContextRules.Create(request, previous, null,
            ResearchContextRules.KnowledgeEvidence([edited with { Dimension = BrandKnowledgeDimension.Differentiator }, kept])).Profile);
        var cleared = ResearchContextRules.Create(request, previous);
        Assert.Null(cleared.Profile);
        Assert.DoesNotContain(cleared.Evidence, ResearchContextRules.IsKnowledgeEvidence);
        Assert.NotEqual(ResearchContextRules.ProfileVersion(previous), ResearchContextRules.ProfileVersion(next));
    }

    [Fact]
    public void WebsitelessBrandWithOnlyKnowledgeItemsHasEvidenceAndOldStrategyIsNotReused()
    {
        var request = new StartBrandResearchRequest(null, "检测品牌", 10);
        var knowledge = ResearchContextRules.KnowledgeEvidence([Item("公司介绍", "研发工业检测设备与管道摄像头。")]);
        var context = ResearchContextRules.Create(request, null, null, knowledge);
        Assert.Null(context.Website);
        Assert.Null(context.BusinessDescription);
        Assert.Equal(knowledge, context.Evidence);
        Assert.Equal("industry-v3", ResearchContext.CurrentStrategy);
        Assert.Equal(ResearchContext.CurrentStrategy, context.StrategyVersion);
        var legacy = context with { StrategyVersion = "industry-v2", Profile = new("v1", "管道检查", new("品牌", "user", "品牌原文"),
            new("设备", "user", "产品原文"), [], [], [], [], [], [], []), ExcludedQuestions = ["旧问题"] };
        var refreshed = ResearchContextRules.Create(request, legacy, null, knowledge);
        Assert.Null(refreshed.Profile);
        Assert.Empty(refreshed.ExcludedQuestions);
    }
}
