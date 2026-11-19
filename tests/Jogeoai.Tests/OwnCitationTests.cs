using Jogeoai.Domain;
using Jogeoai.Infrastructure;

namespace Jogeoai.Tests;

/// <summary>列表事实与回答详情共用的自有引用判定；行为必须与抽取前的 LoadFactsAsync 一致。</summary>
public sealed class OwnCitationTests
{
    private static readonly Guid Brand = Guid.NewGuid();
    private static readonly Guid Competitor = Guid.NewGuid();

    [Fact]
    public void OwnCitationDomains_ReadOnlyBrandDomainTermsFromTheFrozenConfiguration()
    {
        var configuration = new SubjectConfiguration(Brand, 3,
            [new AnalysisSubject(Brand, "brand", "Acme"), new AnalysisSubject(Competitor, "competitor", "Beta")],
            [
                new AnalysisTerm(Brand, "brand", "name", "Acme"),
                new AnalysisTerm(Brand, "brand", "alias", "acme.test"),
                new AnalysisTerm(Brand, "brand", "domain", "acme.test"),
                new AnalysisTerm(Brand, "brand", "domain", "acme-shop.test"),
                new AnalysisTerm(Competitor, "competitor", "domain", "beta.test")
            ], true);

        Assert.Equal(["acme.test", "acme-shop.test"], AnalyticsQueryStore.OwnCitationDomains(configuration));
        Assert.Empty(AnalyticsQueryStore.OwnCitationDomains(configuration with { Terms = [] }));
    }

    [Theory]
    [InlineData("https://acme.test/source", true, true)]
    [InlineData("http://ACME.test/", true, true)]
    [InlineData("https://acme.test:8443/path?q=1", true, true)]
    [InlineData("https://acme.test/source", false, true)]
    [InlineData("https://blog.acme.test/post", true, true)]
    [InlineData("https://blog.acme.test/post", false, false)]
    [InlineData("https://notacme.test/", true, false)]
    [InlineData("https://acme.test.evil.example/", true, false)]
    [InlineData("https://beta.test/", true, false)]
    [InlineData("ftp://acme.test/file", true, false)]
    [InlineData("mailto:hello@acme.test", true, false)]
    [InlineData("acme.test/relative", true, false)]
    [InlineData("not a url", true, false)]
    public void IsOwnCitationUrl_MatchesHttpHostsWithTheSubdomainSwitch(string url, bool includeSubdomains, bool expected)
        => Assert.Equal(expected, AnalyticsQueryStore.IsOwnCitationUrl(url, ["acme.test"], includeSubdomains));

    [Fact]
    public void IsOwnCitationUrl_ComparesInternationalizedHostsByPunycode()
    {
        var punycode = new Uri("https://例.测试/").IdnHost;
        Assert.StartsWith("xn--", punycode);
        Assert.True(AnalyticsQueryStore.IsOwnCitationUrl("https://例.测试/path", [punycode], false));
        Assert.True(AnalyticsQueryStore.IsOwnCitationUrl("https://" + punycode + "/path", [punycode], false));
        Assert.True(AnalyticsQueryStore.IsOwnCitationUrl("https://www.例.测试/", [punycode], true));
        Assert.False(AnalyticsQueryStore.IsOwnCitationUrl("https://www.例.测试/", [punycode], false));
        // 判定始终按 IdnHost 的 punycode 比较；配置词项若保存为 Unicode 形式不会命中（与抽取前一致）。
        Assert.False(AnalyticsQueryStore.IsOwnCitationUrl("https://例.测试/path", ["例.测试"], true));
    }

    [Fact]
    public void IsOwnCitationUrl_WithoutOwnDomainsNeverMatches()
        => Assert.False(AnalyticsQueryStore.IsOwnCitationUrl("https://acme.test/", [], true));
}
