using System.Reflection;
using System.Text.Json;
using Jogeoai.Api;
using Jogeoai.Api.Controllers;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Jogeoai.Tests;

public sealed class RecommendedChannelTests
{
    private static CitationDomainSummaryRow Row(string domain, long events,
        CitationOwnership ownership = CitationOwnership.NonOwned)
        => new(domain, CitationWebsiteCategory.Other, new CitationCategoryBasis("rule", "说明"), ownership, null,
            events, CitationDomainRatio.Of(events, 100), events, CitationDomainRatio.Of(events, 50), 1, null);

    private static CitationDomainTypeRule Media(string domain, string? key = null, string? name = null,
        string? url = null, bool subdomains = true)
        => new(domain, subdomains, CitationDomainSiteType.MediaPlatform, key, name, url);

    private static readonly CitationDomainTypeIndex Index = new(
    [
        Media("douyin.com", "douyin", "抖音", "https://creator.douyin.com/"),
        Media("iesdouyin.com", "douyin", "抖音", "https://creator.douyin.com/"),
        Media("zhihu.com", "zhihu", "知乎", "https://zhuanlan.zhihu.com/write"),
        Media("baike.baidu.com", "baike", "百度百科", "https://baike.baidu.com/"),
        Media("mswmag.com", name: "MSW Magazine"),
        Media("trade-news.cn"),
        Media("exact-only.cn", subdomains: false),
        new("zy-cam.com", true, CitationDomainSiteType.BrandSite, null, null, null),
        new("made-in-china.com", true, CitationDomainSiteType.EcommerceB2b, null, null, null),
        new("b2b.baidu.com", true, CitationDomainSiteType.EcommerceB2b, null, null, null),
        new("epa.gov", true, CitationDomainSiteType.Other, null, null, null),
        Media("a.cn"), Media("b.cn"), Media("c.cn"), Media("d.cn"),
        Media("portal.cn", "portal", "门户"),
        new("shop.portal.cn", false, CitationDomainSiteType.EcommerceB2b, null, null, null),
    ]);

    [Theory]
    [InlineData("zhihu.com", "zhihu.com")]
    [InlineData("zhuanlan.zhihu.com", "zhihu.com")]
    [InlineData("ZHIHU.COM", "zhihu.com")]
    [InlineData("m.baike.baidu.com", "baike.baidu.com")]
    [InlineData("b2b.baidu.com", "b2b.baidu.com")]
    [InlineData("jiutaicctv.en.made-in-china.com", "made-in-china.com")]
    [InlineData("exact-only.cn", "exact-only.cn")]
    [InlineData("shop.portal.cn", "shop.portal.cn")]
    [InlineData("a.shop.portal.cn", "portal.cn")]
    public void IndexPrefersExactThenLongestSubdomainRule(string domain, string rule)
        => Assert.Equal(rule, Index.Match(domain)?.Domain);

    [Theory]
    [InlineData("baidu.com")]
    [InlineData("www.baidu.com")]
    [InlineData("notzhihu.com")]
    [InlineData("zhihu.com.evil.cn")]
    [InlineData("m.exact-only.cn")]
    public void IndexDoesNotMatchLookalikesParentsOrExactOnlyChildren(string domain)
        => Assert.Null(Index.Match(domain));

    [Fact]
    public void ExactOnlyRuleOfAnotherTypeIsExcludedButItsChildrenFallBackToTheParentPlatform()
    {
        var items = ArticleMonitoringQuery.RankRecommendedChannels(
            [Row("shop.portal.cn", 9), Row("a.shop.portal.cn", 2), Row("news.portal.cn", 1)], Index, 5);

        var only = Assert.Single(items);
        Assert.Equal(("portal", "门户", "portal.cn", 3L), (only.PlatformKey, only.Name, only.IconDomain, only.CitationEvents));
        Assert.Equal("https://portal.cn", only.Url);
        Assert.Equal(["a.shop.portal.cn", "news.portal.cn"], only.Domains);
    }

    [Fact]
    public void Migration034SeedMatchesStoreParsingAndKeepsAc9Expectations()
    {
        var sql = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../db/migrations/034_citation_domain_types.sql")));
        Assert.Contains("CHECK (site_type IN ('brand_site', 'media_platform', 'ecommerce_b2b', 'other'))", sql);
        foreach (var stored in new[] { "brand_site", "media_platform", "ecommerce_b2b", "other" })
            Assert.NotNull(CitationDomainTypeRule.ParseSiteType(stored));

        const string text = "'(?:[^'\\\\]|'')*'";
        var seed = System.Text.RegularExpressions.Regex.Matches(sql,
            $@"^\s+\(({text}), ([01]), ({text}), ({text}|NULL), ({text}|NULL), ({text}|NULL), 'ai_reviewed', UTC_TIMESTAMP\(6\)\)[,\n]",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        static string? Value(string literal) => literal == "NULL" ? null : literal[1..^1].Replace("''", "'");
        var rules = seed.Select(match => new CitationDomainTypeRule(Value(match.Groups[1].Value)!,
            match.Groups[2].Value == "1",
            CitationDomainTypeRule.ParseSiteType(Value(match.Groups[3].Value)!)
                ?? throw new InvalidOperationException(match.Value),
            Value(match.Groups[4].Value), Value(match.Groups[5].Value), Value(match.Groups[6].Value))).ToArray();

        Assert.Equal(1265, rules.Length);
        Assert.Equal(rules.Length, System.Text.RegularExpressions.Regex.Matches(sql, "UTC_TIMESTAMP\\(6\\)\\)").Count);
        // 主键为 ascii_bin：种子必须已规范化，内存索引的小写化才不会合并或遮蔽库中不同的行。
        // 现有种子含一个单段主机（jtkjnkj，品牌官网，不影响推荐），因此不强制要求含点。
        Assert.All(rules, rule => Assert.Matches("^(?!www\\.)[a-z0-9-]+(\\.[a-z0-9-]+)*$", rule.Domain));
        Assert.Equal(rules.Length, rules.Select(rule => rule.Domain).Distinct(StringComparer.Ordinal).Count());
        Assert.All(rules.Where(rule => rule.SiteType != CitationDomainSiteType.MediaPlatform),
            rule => Assert.True(rule.PlatformKey is null && rule.PlatformName is null && rule.PublishUrl is null, rule.Domain));
        Assert.All(rules.Where(rule => rule.PublishUrl is not null), rule => Assert.StartsWith("https://", rule.PublishUrl));
        // 同一平台标识的规则合并为一行，名称与发布入口必须一致，否则取值依赖引用多少。
        Assert.All(rules.Where(rule => rule.PlatformKey is not null).GroupBy(rule => rule.PlatformKey),
            group => Assert.Single(group.Select(rule => (rule.PlatformName, rule.PublishUrl)).Distinct()));

        var index = new CitationDomainTypeIndex(rules);
        foreach (var brand in new[] { "zy-cam.com", "srodcn.com", "sanyipace.com" })
            Assert.Equal(CitationDomainSiteType.BrandSite, index.Match(brand)?.SiteType);
        foreach (var shop in new[] { "alibaba.com", "1688.com", "amazon.com", "b2b.baidu.com", "jiutaicctv.en.made-in-china.com" })
            Assert.Equal(CitationDomainSiteType.EcommerceB2b, index.Match(shop)?.SiteType);
        Assert.Null(index.Match("baidu.com"));
        var top = ArticleMonitoringQuery.RankRecommendedChannels(
        [
            Row("zy-cam.com", 500), Row("alibaba.com", 400), Row("iesdouyin.com", 300), Row("douyin.com", 1),
            Row("baijiahao.baidu.com", 200), Row("mp.weixin.qq.com", 150), Row("sohu.com", 100), Row("zhuanlan.zhihu.com", 50),
        ], index, 5);
        Assert.Equal(["抖音", "百家号", "微信公众号", "搜狐号", "知乎"], top.Select(item => item.Name));
        Assert.Equal("douyin", top[0].PlatformKey);
    }

    [Fact]
    public void SiteTypeParsesStoredValuesOnly()
    {
        Assert.Equal(CitationDomainSiteType.MediaPlatform, CitationDomainTypeRule.ParseSiteType("media_platform"));
        Assert.Equal(CitationDomainSiteType.EcommerceB2b, CitationDomainTypeRule.ParseSiteType("ecommerce_b2b"));
        Assert.Null(CitationDomainTypeRule.ParseSiteType("news"));
    }

    [Fact]
    public void MergesByPlatformKeyAndSumsEvents()
    {
        var items = ArticleMonitoringQuery.RankRecommendedChannels(
            [Row("zhihu.com", 3), Row("douyin.com", 6), Row("zhuanlan.zhihu.com", 4), Row("iesdouyin.com", 1)], Index, 5);

        Assert.Collection(items,
            first =>
            {
                Assert.Equal((1, "douyin", "抖音", 7L), (first.Rank, first.PlatformKey, first.Name, first.CitationEvents));
                Assert.Equal("douyin.com", first.IconDomain);
                Assert.Equal("https://creator.douyin.com/", first.Url);
                Assert.Equal(["douyin.com", "iesdouyin.com"], first.Domains);
            },
            second =>
            {
                Assert.Equal((2, "zhihu", "知乎", 7L), (second.Rank, second.PlatformKey, second.Name, second.CitationEvents));
                Assert.Equal(["zhuanlan.zhihu.com", "zhihu.com"], second.Domains);
            });
    }

    [Fact]
    public void KeepsOnlyNonOwnedMediaPlatforms()
    {
        var items = ArticleMonitoringQuery.RankRecommendedChannels(
        [
            Row("zy-cam.com", 90),                                   // 品牌官网
            Row("jiutaicctv.en.made-in-china.com", 80),              // 电商B2B（店铺子域）
            Row("b2b.baidu.com", 70),                                // 电商B2B（精确规则优先于百度其他子域）
            Row("nepis.epa.gov", 60),                                // 其他
            Row("unknown-site.com", 50),                             // 未分类
            Row("trade-news.cn", 40, CitationOwnership.Owned),       // 媒体平台但属于本品牌
            Row("mswmag.com", 5),
            Row("news.trade-news.cn", 3, CitationOwnership.Unknown), // 归属待确认保留
            Row("baike.baidu.com", 0),                               // 无引用
        ], Index, 10);

        Assert.Equal(["MSW Magazine", "trade-news.cn"], items.Select(item => item.Name));
        var fallback = items[1];
        Assert.Null(fallback.PlatformKey);
        Assert.Equal("trade-news.cn", fallback.IconDomain);
        Assert.Equal("https://trade-news.cn", fallback.Url);
        Assert.Equal(["news.trade-news.cn"], fallback.Domains);
        Assert.Equal("https://mswmag.com", items[0].Url);
    }

    [Fact]
    public void EmptyIndexRecommendsNothing()
        => Assert.Empty(ArticleMonitoringQuery.RankRecommendedChannels([Row("zhihu.com", 9)], CitationDomainTypeIndex.Empty, 5));

    [Fact]
    public void SortsTiesByNameAndAppliesLimit()
    {
        var items = ArticleMonitoringQuery.RankRecommendedChannels(
            [Row("b.cn", 2), Row("a.cn", 2), Row("c.cn", 9), Row("d.cn", 1)], Index, 3);

        Assert.Equal(["c.cn", "a.cn", "b.cn"], items.Select(item => item.Name));
        Assert.Equal([1, 2, 3], items.Select(item => item.Rank));
    }

    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Site = Guid.NewGuid();

    [Fact]
    public async Task ControllerForwardsEnginesFreezesOneSnapshotAndWalksEveryDomainPage()
    {
        var calls = new List<string>();
        var controller = Controller("?engine=doubao,qwen&engine=deepseek&limit=2", (method, args) =>
        {
            calls.Add(method.Name);
            Assert.Equal(Tenant, args[0]);
            Assert.Equal(Site, args[1]);
            switch (method.Name)
            {
                case nameof(IAnalyticsQueryStore.CitationDomainSummaryAsync):
                    var filter = Assert.IsType<AnalyticsFilterRequest>(args[2]);
                    Assert.Equal(["doubao", "qwen", "deepseek"], filter.Engines);
                    Assert.Null(args[3]);
                    return Task.FromResult(new CitationDomainSummaryView(Meta("frozen"), null!, [], null!));
                case nameof(IAnalyticsQueryStore.CitationDomainSummaryDomainsAsync):
                    Assert.Equal("frozen", args[2]);
                    Assert.Equal(100, args[7]);
                    return Task.FromResult(args[6] is null
                        ? new CitationDomainSummaryPage(Meta("frozen"), [Row("zhihu.com", 3)], "page-2", 4)
                        : new CitationDomainSummaryPage(Meta("frozen"),
                            [Row("brand.com", 50, CitationOwnership.Owned), Row("a.cn", 5), Row("zy-cam.com", 30), Row("zhuanlan.zhihu.com", 4)],
                            null, 4));
                default:
                    throw new InvalidOperationException(method.Name);
            }
        });

        var result = Assert.IsType<OkObjectResult>(await controller.RecommendedChannels(Site, ["doubao,qwen", "deepseek"], 2));
        var list = Assert.IsType<ArticleRecommendedChannelList>(result.Value);
        Assert.Equal("frozen", list.Meta.SnapshotToken);
        Assert.Equal([("知乎", 7L), ("a.cn", 5L)], list.Items.Select(item => (item.Name, item.CitationEvents)));
        Assert.Equal(
        [
            nameof(IAnalyticsQueryStore.CitationDomainSummaryAsync),
            nameof(IAnalyticsQueryStore.CitationDomainSummaryDomainsAsync),
            nameof(IAnalyticsQueryStore.CitationDomainSummaryDomainsAsync),
        ], calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public async Task ControllerRejectsLimitOutsideOneToTwentyBeforeFreezingASnapshot(int limit)
        => await AssertError(Controller("", Unexpected).RecommendedChannels(Site, null, limit), "INVALID_LIMIT", 400);

    [Fact]
    public async Task ControllerRequiresLoginAndPassesSiteNotFoundThrough()
    {
        var anonymous = Controller("", Unexpected);
        anonymous.HttpContext.Items.Clear();
        await AssertError(anonymous.RecommendedChannels(Site, null), "AUTHENTICATION_REQUIRED", 401);

        var foreign = Controller("", (_, _) => Task.FromException<CitationDomainSummaryView>(
            new CollectorStoreException("RESOURCE_NOT_FOUND", "资源不存在。", 404)));
        await AssertError(foreign.RecommendedChannels(Site, null), "RESOURCE_NOT_FOUND", 404);
    }

    private static CitationDomainMeta Meta(string token)
        => new(token, null!, default, default, null, Guid.Empty, 0, 0, false, "", "", "", "", "", 0, 0, "", default, default);

    private static ArticleMonitoringController Controller(string query, Func<MethodInfo, object?[], object?> handler)
    {
        var analytics = DispatchProxy.Create<IAnalyticsQueryStore, MonitoringAnswerApiTests.QueryProxy>();
        ((MonitoringAnswerApiTests.QueryProxy)analytics).Handler = handler;
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(query);
        context.Items[SessionAuthenticationMiddleware.ContextItemKey] = new AuthenticatedRequestContext(
            new AuthUserView(Guid.NewGuid(), "viewer@fixture.test", "Viewer", "active",
                [new TenantMembershipView(Tenant, "Team", "viewer", "active")]), Guid.NewGuid(), "fixture-token");
        return new(new ArticleMonitoringQuery(analytics, null!, new FixedDomainTypes()), null!, new TenantContextResolver(new TestEnvironment()))
        { ControllerContext = new ControllerContext { HttpContext = context } };
    }

    private static object? Unexpected(MethodInfo method, object?[] args) => throw new InvalidOperationException(method.Name);

    private static async Task AssertError(Task<IActionResult> action, string code, int status)
    {
        var result = Assert.IsAssignableFrom<ObjectResult>(await action);
        Assert.Equal(status, result.StatusCode);
        Assert.Equal(code, JsonSerializer.SerializeToElement(result.Value).GetProperty("error").GetProperty("code").GetString());
    }

    private sealed class FixedDomainTypes : ICitationDomainTypeStore
    {
        public Task<CitationDomainTypeIndex> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Index);
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
