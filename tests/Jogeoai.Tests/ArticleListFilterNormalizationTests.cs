using Jogeoai.Domain;
using Jogeoai.Infrastructure;

namespace Jogeoai.Tests;

/// <summary>文章列表筛选的规范化与校验（不连数据库）：统计区间、引用平台、域名归属与排序白名单。</summary>
public sealed class ArticleListFilterNormalizationTests
{
    [Fact]
    public void DefaultsKeepPreviousBehaviour()
    {
        var filter = ContentStore.NormalizeFilter(new ArticleListFilter());
        Assert.Equal(30, filter.CitationDays);
        Assert.Null(filter.Cited);
        Assert.Null(filter.CitationEngine);
        Assert.Null(filter.Ownership);
        Assert.False(filter.OwnedOnly);
        Assert.Equal("created_desc", filter.Sort);
        Assert.Equal(20, filter.Limit);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(30)]
    [InlineData(90)]
    public void SupportedCitationDaysAreAccepted(int days)
        => Assert.Equal(days, ContentStore.NormalizeFilter(new ArticleListFilter(CitationDays: days)).CitationDays);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(14)]
    [InlineData(-30)]
    [InlineData(365)]
    public void OtherCitationDaysAreRejected(int days)
    {
        var error = Assert.Throws<CollectorStoreException>(() => ContentStore.NormalizeFilter(new ArticleListFilter(CitationDays: days)));
        Assert.Equal("INVALID_FILTER", error.Code);
        Assert.Equal(400, error.StatusCode);
    }

    [Fact]
    public void OwnedOnlyIsTheCompatibleSpellingOfOwned()
    {
        var legacy = ContentStore.NormalizeFilter(new ArticleListFilter(OwnedOnly: true));
        var explicitOwned = ContentStore.NormalizeFilter(new ArticleListFilter(Ownership: ArticleOwnershipFilter.Owned));
        var both = ContentStore.NormalizeFilter(new ArticleListFilter(OwnedOnly: true, Ownership: ArticleOwnershipFilter.Owned));
        foreach (var filter in new[] { legacy, explicitOwned, both })
        {
            Assert.Equal(ArticleOwnershipFilter.Owned, filter.Ownership);
            Assert.True(filter.OwnedOnly);
        }
        // Equal normalized filters produce equal cursor scopes.
        Assert.Equal(legacy, explicitOwned);
    }

    [Fact]
    public void ExternalOwnershipStandsAloneAndConflictsWithOwnedOnly()
    {
        var external = ContentStore.NormalizeFilter(new ArticleListFilter(Ownership: ArticleOwnershipFilter.External));
        Assert.Equal(ArticleOwnershipFilter.External, external.Ownership);
        Assert.False(external.OwnedOnly);

        var error = Assert.Throws<CollectorStoreException>(() => ContentStore.NormalizeFilter(
            new ArticleListFilter(OwnedOnly: true, Ownership: ArticleOwnershipFilter.External)));
        Assert.Equal("INVALID_FILTER", error.Code);
    }

    [Theory]
    [InlineData(" ChatGPT ", "chatgpt")]
    [InlineData("doubao", "doubao")]
    [InlineData("   ", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void CitationEngineIsTrimmedAndLowercased(string? input, string? expected)
        => Assert.Equal(expected, ContentStore.NormalizeFilter(new ArticleListFilter(CitationEngine: input)).CitationEngine);

    [Fact]
    public void OverlongCitationEngineIsRejected()
    {
        var error = Assert.Throws<CollectorStoreException>(() => ContentStore.NormalizeFilter(
            new ArticleListFilter(CitationEngine: new string('e', 65))));
        Assert.Equal("INVALID_FILTER", error.Code);
        Assert.Equal(new string('e', 64), ContentStore.NormalizeFilter(new ArticleListFilter(CitationEngine: new string('E', 64))).CitationEngine);
    }

    [Theory]
    [InlineData("created_desc")]
    [InlineData("updated_desc")]
    [InlineData("citation_desc")]
    [InlineData("last_cited_desc")]
    [InlineData("FIRST_CITED_DESC")]
    public void SupportedSortsAreAcceptedCaseInsensitively(string sort)
        => Assert.Equal(sort.ToLowerInvariant(), ContentStore.NormalizeFilter(new ArticleListFilter(Sort: sort)).Sort);

    [Theory]
    [InlineData("last_cited_asc")]
    [InlineData("cited_desc")]
    [InlineData("first_cited")]
    public void UnknownSortsAreRejected(string sort)
    {
        var error = Assert.Throws<CollectorStoreException>(() => ContentStore.NormalizeFilter(new ArticleListFilter(Sort: sort)));
        Assert.Equal("INVALID_FILTER", error.Code);
    }

    [Fact]
    public void CitedSwitchPassesThrough()
    {
        Assert.True(ContentStore.NormalizeFilter(new ArticleListFilter(Cited: true)).Cited);
        Assert.False(ContentStore.NormalizeFilter(new ArticleListFilter(Cited: false)).Cited);
    }
}
