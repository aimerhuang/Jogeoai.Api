using Jogeoai.Domain;

namespace Jogeoai.Tests;

public sealed class DetailedAnalyticsRulesTests
{
    [Fact]
    public void Queries_PreserveOriginalDeduplicateAndExcludePromptAndSentinel()
    {
        var events = DetailedAnalyticsRules.Queries(
            [" best tools ", " Tool   Comparison ", "tool comparison", "unavailable", "pricing guide"],
            "Best Tools");
        Assert.Equal(4, events.Count);
        Assert.Equal([false, true, false, true], events.Select(e => e.Included));
        Assert.Equal([1, 2, 4, 5], events.Select(e => e.Position));
        Assert.Equal(" Tool   Comparison ", events[1].Original);
        Assert.Equal("same_as_prompt", events[0].ExclusionReason);
        Assert.Equal("unavailable_sentinel", events[2].ExclusionReason);
    }

    [Fact]
    public void Terms_ExplainEnglishAndChineseAndUnsupportedLocale()
    {
        var english = DetailedAnalyticsRules.CompareTerms("tool comparison", "best tools", "en-US")!;
        Assert.Contains(english, t => t.Text == "COMPARISON" && t.Relation == "added");
        Assert.Contains(english, t => t.Text == "BEST" && t.Relation == "removed");
        var chinese = DetailedAnalyticsRules.CompareTerms("品牌对比", "品牌工具", "zh-CN")!;
        Assert.Contains(chinese, t => t.Text == "品牌" && t.Relation == "retained");
        Assert.Contains(chinese, t => t.Text == "对比" && t.Relation == "added");
        var mixed = DetailedAnalyticsRules.CompareTerms("AI品牌对比", "AI品牌工具", "zh-CN")!;
        Assert.Contains(mixed, t => t.Text == "AI" && t.Relation == "retained");
        Assert.Null(DetailedAnalyticsRules.CompareTerms("vergleich", "werkzeug", "de-DE"));
    }

    [Fact]
    public void CitationRules_KeepBusinessQueryAndSeparateGoogle()
    {
        var brand = Guid.NewGuid();
        var config = new SubjectConfiguration(brand, 1,
            [new AnalysisSubject(brand, "brand", "Acme")],
            [new AnalysisTerm(brand, "brand", "domain", "a.test")], true);
        var url = DetailedAnalyticsRules.NormalizeUrl("https://www.a.test/product?id=42#details");
        Assert.Equal("https://a.test/product?id=42", url);
        Assert.Equal("https://a.test/product?id=42", DetailedAnalyticsRules.NormalizeUrl(
            "https://a.test/product?id=42&utm_source=chatgpt&fbclid=123"));
        var citation = DetailedAnalyticsRules.Classify(url!, "Acme product", 2, config);
        Assert.Equal("brand", citation.SourceCategory);
        Assert.Equal("product", citation.PageType);
        Assert.Equal("configured_domain", citation.RuleBasis);
        var google = DetailedAnalyticsRules.GoogleInternal("https://www.google.com/search?q=best+tools", null, 1, config);
        Assert.NotNull(google);
        Assert.Equal("best tools", google.Query);
        Assert.Equal("search", google.Kind);
        Assert.Equal("unavailable", DetailedAnalyticsRules.GoogleInternal("https://google.com/shopping", null, 1, config)!.Availability);
        Assert.Null(DetailedAnalyticsRules.GoogleInternal("https://google.evil.com/search?q=x", null, 1, config));
        Assert.Equal("other", DetailedAnalyticsRules.Classify("https://github.com.evil.test/guide", null, 1, config).SourceCategory);
        Assert.Equal("editorial", DetailedAnalyticsRules.Classify("https://www.reuters.com/world/story", null, 1, config).SourceCategory);
        Assert.Equal("pr", DetailedAnalyticsRules.Classify("https://example.org/press-release/new", null, 1, config).SourceCategory);
    }

    [Fact]
    public void UrlNormalization_IsVersionedAndKeepsBusinessParameters()
    {
        const string input = "https://www.a.test/product?id=42&ref=partner&utm_source=chatgpt&gclid=abc#details";

        Assert.Equal("https://a.test/product?id=42&ref=partner&utm_source=chatgpt&gclid=abc", 
            DetailedAnalyticsRules.NormalizeUrl(input, DetailedAnalyticsRules.UrlV1));
        Assert.Equal("https://a.test/product?id=42&ref=partner",
            DetailedAnalyticsRules.NormalizeUrl(input, DetailedAnalyticsRules.UrlV2));
        var config = new SubjectConfiguration(Guid.NewGuid(), 1, [], [], true);
        Assert.Contains("utm_source=chatgpt", DetailedAnalyticsRules.Classify(
            input, null, 1, config, DetailedAnalyticsRules.UrlV1).Url);
        Assert.Throws<ArgumentException>(() => DetailedAnalyticsRules.NormalizeUrl(input, "url-v9"));
    }

    [Fact]
    public void CitationDomainSubdomainSettingControlsConfiguredOwnership()
    {
        var brand = Guid.NewGuid();
        var config = new SubjectConfiguration(brand, 1,
            [new AnalysisSubject(brand, "brand", "Acme")],
            [new AnalysisTerm(brand, "brand", "domain", "a.test")], true, false);

        Assert.Equal("other", DetailedAnalyticsRules.Classify("https://blog.a.test/article", null, 1, config).SourceCategory);
        Assert.Equal("brand", DetailedAnalyticsRules.Classify("https://a.test/article", null, 1, config).SourceCategory);
    }

    [Theory]
    [InlineData("search?q=tools", "search")]
    [InlineData("shop", "shopping")]
    [InlineData("docs", "doc")]
    [InlineData("forum", "forum")]
    [InlineData("video", "video")]
    [InlineData("blog", "article")]
    [InlineData("news", "article")]
    public void CitationRules_ClassifyRootPagePaths(string path, string expectedPageType)
    {
        var brand = Guid.NewGuid();
        var config = new SubjectConfiguration(brand, 1,
            [new AnalysisSubject(brand, "brand", "Example")], [], true);

        var citation = DetailedAnalyticsRules.Classify($"https://example.org/{path}", null, 1, config);

        Assert.Equal(expectedPageType, citation.PageType);
    }
}
