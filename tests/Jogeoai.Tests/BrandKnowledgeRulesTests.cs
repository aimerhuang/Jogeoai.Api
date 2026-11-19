using System.Text.Json;
using System.Text.Json.Serialization;
using Jogeoai.Domain;

namespace Jogeoai.Tests;

/// <summary>品牌知识条目：维度元数据、标题规范化与 title_key、长度校验、Markdown 模板、网址识别候选标准化与查重。</summary>
public sealed class BrandKnowledgeRulesTests
{
    private const string PageText = "优联科技成立于 2008 年，\n专注工业物联网网关。\n\n核心产品 3188DH 支持 4G 全网通，工作温度 -40~85℃。";

    [Fact]
    public void DimensionsHaveFixedOrderKeysAndChineseLabels()
    {
        Assert.Equal(["brand_intro", "selling_point", "audience", "differentiator", "product_service", "faq"],
            BrandKnowledgeRules.Dimensions.Select(item => item.Key).ToArray());
        Assert.Equal(["品牌介绍", "核心卖点", "目标人群", "差异化优势", "产品/服务", "常见问题"],
            BrandKnowledgeRules.Dimensions.Select(item => item.Label).ToArray());
        Assert.Equal([1, 2, 3, 4, 5, 6], BrandKnowledgeRules.Dimensions.Select(item => item.Order).ToArray());
        Assert.Equal(Enum.GetValues<BrandKnowledgeDimension>(), BrandKnowledgeRules.Dimensions.Select(item => item.Dimension).ToArray());
        Assert.All(BrandKnowledgeRules.Dimensions, item => Assert.False(string.IsNullOrWhiteSpace(item.Hint)));
    }

    [Fact]
    public void DimensionJsonUsesSnakeCaseKeysForValuesAndDictionaryKeys()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        foreach (var item in BrandKnowledgeRules.Dimensions)
        {
            Assert.Equal($"\"{item.Key}\"", JsonSerializer.Serialize(item.Dimension, options));
            Assert.Equal(item.Dimension, JsonSerializer.Deserialize<BrandKnowledgeDimension>($"\"{item.Key}\"", options));
        }
        var counts = BrandKnowledgeRules.CompleteCounts(new Dictionary<BrandKnowledgeDimension, int> { [BrandKnowledgeDimension.SellingPoint] = 2 });
        var json = JsonSerializer.Serialize(counts, options);
        Assert.Contains("\"selling_point\":2", json, StringComparison.Ordinal);
        Assert.Contains("\"product_service\":0", json, StringComparison.Ordinal);
        Assert.Equal(6, counts.Count);
    }

    [Theory]
    [InlineData("brand_intro", BrandKnowledgeDimension.BrandIntro)]
    [InlineData(" Selling-Point ", BrandKnowledgeDimension.SellingPoint)]
    [InlineData("产品/服务", BrandKnowledgeDimension.ProductService)]
    [InlineData("FAQ", BrandKnowledgeDimension.Faq)]
    public void TryParseDimensionAcceptsKeysAndLabels(string value, BrandKnowledgeDimension expected)
    {
        Assert.True(BrandKnowledgeRules.TryParseDimension(value, out var dimension));
        Assert.Equal(expected, dimension);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("pricing")]
    [InlineData("5")]
    public void TryParseDimensionRejectsUnknownValues(string? value)
        => Assert.False(BrandKnowledgeRules.TryParseDimension(value, out _));

    [Fact]
    public void TitleNormalizationCollapsesWhitespaceAndTitleKeyFoldsCaseAndWidth()
    {
        Assert.Equal("3188DH 工业 网关", BrandKnowledgeRules.NormalizeTitle("  3188DH \t 工业　\n网关 "));
        Assert.Equal("3188dh 工业 网关", BrandKnowledgeRules.TitleKey("  3188DH \t 工业　\n网关 "));
        // 全角字母数字、全角空格转半角后与半角标题同键。
        Assert.Equal(BrandKnowledgeRules.TitleKey("3188DH 网关"), BrandKnowledgeRules.TitleKey("３１８８ＤＨ　网关"));
        Assert.NotEqual(BrandKnowledgeRules.TitleKey("3188DH"), BrandKnowledgeRules.TitleKey("3188DX"));
        Assert.Equal(string.Empty, BrandKnowledgeRules.TitleKey(null));
    }

    [Fact]
    public void NormalizeWriteTrimsAndKeepsContentLineBreaks()
    {
        var write = BrandKnowledgeRules.NormalizeWrite(new BrandKnowledgeItemWrite(BrandKnowledgeDimension.Faq,
            "  支持哪些网络？ ", "\r\n 支持 4G 全网通。\r\n\r\n也支持有线。 \n"));

        Assert.Equal(BrandKnowledgeDimension.Faq, write.Dimension);
        Assert.Equal("支持哪些网络？", write.Title);
        Assert.Equal("支持 4G 全网通。\n\n也支持有线。", write.Content);
    }

    [Fact]
    public void NormalizeAcceptsBoundaryLengthsCountedByCodePoint()
    {
        var title = new string('标', 100);
        var content = new string('文', 1999) + "😀";
        var write = BrandKnowledgeRules.NormalizeWrite(new BrandKnowledgeItemWrite(BrandKnowledgeDimension.BrandIntro, title, content));
        Assert.Equal(100, BrandKnowledgeRules.CharacterCount(write.Title));
        Assert.Equal(2000, BrandKnowledgeRules.CharacterCount(write.Content));
    }

    [Theory]
    [InlineData("", "内容", "标题长度")]
    [InlineData("   ", "内容", "标题长度")]
    [InlineData("标题", "", "内容长度")]
    [InlineData("标题", " \n ", "内容长度")]
    public void NormalizeRejectsEmptyFields(string title, string content, string fragment)
    {
        var exception = Assert.Throws<CollectorStoreException>(() =>
            BrandKnowledgeRules.NormalizeWrite(new BrandKnowledgeItemWrite(BrandKnowledgeDimension.Audience, title, content)));
        Assert.Equal("KNOWLEDGE_ITEM_VALIDATION", exception.Code);
        Assert.Equal(400, exception.StatusCode);
        Assert.Contains(fragment, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeRejectsOverLimitLengthsUndefinedDimensionAndBadVersion()
    {
        Assert.Equal("KNOWLEDGE_ITEM_VALIDATION", Assert.Throws<CollectorStoreException>(() => BrandKnowledgeRules.NormalizeWrite(
            new BrandKnowledgeItemWrite(BrandKnowledgeDimension.Faq, new string('标', 101), "内容"))).Code);
        Assert.Equal("KNOWLEDGE_ITEM_VALIDATION", Assert.Throws<CollectorStoreException>(() => BrandKnowledgeRules.NormalizeWrite(
            new BrandKnowledgeItemWrite(BrandKnowledgeDimension.Faq, "标题", new string('文', 2001)))).Code);
        Assert.Equal("KNOWLEDGE_ITEM_VALIDATION", Assert.Throws<CollectorStoreException>(() => BrandKnowledgeRules.NormalizeWrite(
            new BrandKnowledgeItemWrite((BrandKnowledgeDimension)99, "标题", "内容"))).Code);
        Assert.Equal("KNOWLEDGE_ITEM_VALIDATION", Assert.Throws<CollectorStoreException>(() => BrandKnowledgeRules.NormalizeUpdate(
            new BrandKnowledgeItemUpdate(BrandKnowledgeDimension.Faq, "标题", "内容", 0))).Code);

        var update = BrandKnowledgeRules.NormalizeUpdate(new BrandKnowledgeItemUpdate(BrandKnowledgeDimension.Faq, " 标题 ", " 内容 ", 3));
        Assert.Equal(("标题", "内容", 3), (update.Title, update.Content, update.ExpectedVersion));
    }

    [Fact]
    public void RenderMarkdownUsesFixedTemplate()
    {
        Assert.Equal("# 3188DH\n\n维度：产品/服务\n\n工业网关。\n支持 4G。\n\n来源：https://example.com/p/3188dh\n",
            BrandKnowledgeRules.RenderMarkdown(BrandKnowledgeDimension.ProductService, " 3188DH ", "工业网关。\r\n支持 4G。 ",
                " https://example.com/p/3188dh "));
        Assert.Equal("# 公司简介\n\n维度：品牌介绍\n\n成立于 2008 年。\n",
            BrandKnowledgeRules.RenderMarkdown(BrandKnowledgeDimension.BrandIntro, "公司简介", "成立于 2008 年。", null));
    }

    [Fact]
    public void NormalizeCandidatesKeepsValidCandidatesWithSequentialIds()
    {
        var result = BrandKnowledgeRules.NormalizeCandidates(
        [
            new("brand_intro", " 公司简介 ", "优联科技成立于 2008 年，专注工业物联网网关。", "优联科技成立于 2008 年， 专注工业物联网网关。"),
            new("product_service", "3188DH", "支持 4G 全网通。", "核心产品 3188DH 支持 4G 全网通")
        ], PageText, null, []);

        Assert.Equal(2, result.Candidates.Count);
        Assert.Equal(0, result.DroppedCount);
        Assert.False(result.Truncated);
        Assert.Equal(["1", "2"], result.Candidates.Select(item => item.CandidateId).ToArray());
        Assert.Equal("公司简介", result.Candidates[0].Title);
        Assert.Equal("优联科技成立于 2008 年， 专注工业物联网网关。", result.Candidates[0].Evidence);
        Assert.All(result.Candidates, item => Assert.Null(item.DuplicateOfItemId));
    }

    [Fact]
    public void NormalizeCandidatesDropsCandidatesWhoseEvidenceIsNotInPageText()
    {
        var result = BrandKnowledgeRules.NormalizeCandidates(
        [
            new("selling_point", "价格优势", "售价仅 199 元。", "售价仅 199 元"),
            new("selling_point", "温度", "工作温度宽。", null),
            new("selling_point", "很短", "很短的依据。", "全网"),
            new("selling_point", "宽温", "工作温度 -40~85℃。", "工作温度 -40~85℃")
        ], PageText, null, []);

        var kept = Assert.Single(result.Candidates);
        Assert.Equal("宽温", kept.Title);
        Assert.Equal("1", kept.CandidateId);
        Assert.Equal(3, result.DroppedCount);
    }

    [Fact]
    public void NormalizeCandidatesFiltersDimensionsAndDeduplicatesWithinBatch()
    {
        var result = BrandKnowledgeRules.NormalizeCandidates(
        [
            new("pricing", "无效维度", "内容", "核心产品 3188DH"),
            new("faq", "支持 4G 吗？", "支持。", "支持 4G 全网通"),
            new("product_service", "3188DH", "支持 4G 全网通。", "核心产品 3188DH"),
            new("product_service", " ３１８８ｄｈ ", "重复标题。", "核心产品 3188DH"),
            new("audience", "3188DH", "不同维度同名保留。", "核心产品 3188DH")
        ], PageText, [BrandKnowledgeDimension.ProductService, BrandKnowledgeDimension.Audience], []);

        Assert.Equal([BrandKnowledgeDimension.ProductService, BrandKnowledgeDimension.Audience],
            result.Candidates.Select(item => item.Dimension).ToArray());
        Assert.Equal(3, result.DroppedCount);
    }

    [Fact]
    public void NormalizeCandidatesKeepsOnlyFirstBrandIntro()
    {
        var result = BrandKnowledgeRules.NormalizeCandidates(
        [
            new("brand_intro", "无依据", "优联科技是行业龙头。", "行业龙头企业"),
            new("brand_intro", "关于优联科技", "优联科技成立于 2008 年，专注工业物联网网关。", "优联科技成立于 2008 年"),
            new("brand_intro", "优联科技位于哪里？", "优联科技专注工业物联网网关。", "专注工业物联网网关"),
            new("selling_point", "宽温", "工作温度 -40~85℃。", "工作温度 -40~85℃")
        ], PageText, null, []);

        Assert.Equal(["关于优联科技", "宽温"], result.Candidates.Select(item => item.Title).ToArray());
        Assert.Equal(2, result.DroppedCount);
    }

    [Fact]
    public void NormalizeCandidatesCapsAtThirtyAndTruncatesLongFields()
    {
        var raw = Enumerable.Range(1, 35)
            .Select(i => new KnowledgeExtractRawCandidate("faq", $"问题 {i}？", $"回答 {i}。", "专注工业物联网网关"))
            .Prepend(new KnowledgeExtractRawCandidate("brand_intro", new string('标', 120), new string('文', 2100), "专注工业物联网网关"))
            .ToArray();

        var result = BrandKnowledgeRules.NormalizeCandidates(raw, PageText, null, []);

        Assert.Equal(BrandKnowledgeRules.MaxCandidates, result.Candidates.Count);
        Assert.Equal(6, result.DroppedCount);
        Assert.True(result.Truncated);
        Assert.Equal(100, BrandKnowledgeRules.CharacterCount(result.Candidates[0].Title));
        Assert.Equal(2000, BrandKnowledgeRules.CharacterCount(result.Candidates[0].Content));
        Assert.Equal("30", result.Candidates[^1].CandidateId);
    }

    [Fact]
    public void NormalizeCandidatesMarksDuplicatesOfExistingItems()
    {
        var sameTitle = Item(BrandKnowledgeDimension.ProductService, "3188DH", "旧的产品描述。");
        var similar = Item(BrandKnowledgeDimension.BrandIntro, "关于我们", "优联科技成立于 2008 年，专注工业物联网网关。");

        var result = BrandKnowledgeRules.NormalizeCandidates(
        [
            new("product_service", "３１８８DH", "支持 4G 全网通。", "核心产品 3188DH"),
            new("brand_intro", "公司简介", "优联科技成立于2008年，专注工业物联网网关！", "优联科技成立于 2008 年"),
            new("selling_point", "宽温", "工作温度 -40~85℃。", "工作温度 -40~85℃")
        ], PageText, null, [sameTitle, similar]);

        Assert.Equal([sameTitle.ItemId, similar.ItemId, (Guid?)null], result.Candidates.Select(item => item.DuplicateOfItemId).ToArray());
    }

    [Fact]
    public void FindDuplicatePrefersSameDimensionTitleThenAnyTitleThenSimilarContent()
    {
        var otherDimension = Item(BrandKnowledgeDimension.Faq, "3188DH", "完全不同的内容甲。");
        var sameDimension = Item(BrandKnowledgeDimension.ProductService, "3188dh", "完全不同的内容乙。");

        Assert.Equal(sameDimension.ItemId, BrandKnowledgeRules.FindDuplicate(BrandKnowledgeDimension.ProductService, "3188DH", "新内容",
            [otherDimension, sameDimension]));
        Assert.Equal(otherDimension.ItemId, BrandKnowledgeRules.FindDuplicate(BrandKnowledgeDimension.Audience, "3188DH", "新内容",
            [otherDimension, sameDimension]));
        Assert.Null(BrandKnowledgeRules.FindDuplicate(BrandKnowledgeDimension.Audience, "其他", "毫不相干的一句话", [otherDimension, sameDimension]));
    }

    [Fact]
    public void ContentSimilarityUsesCharacterBigramJaccard()
    {
        Assert.Equal(1d, BrandKnowledgeRules.ContentSimilarity("支持 4G 全网通", "支持4G全网通"));
        Assert.Equal(0d, BrandKnowledgeRules.ContentSimilarity("", "支持"));
        // {ab, bc, cd} 与 {ab, bc, ce}：交集 2，并集 4。
        Assert.Equal(0.5d, BrandKnowledgeRules.ContentSimilarity("abcd", "abce"), 6);
        Assert.True(BrandKnowledgeRules.ContentSimilarity("优联科技成立于 2008 年，专注工业物联网网关。", "优联科技成立于2008年，专注工业物联网网关！")
            >= BrandKnowledgeRules.DuplicateSimilarityThreshold);
        Assert.True(BrandKnowledgeRules.ContentSimilarity("优联科技成立于 2008 年", "工作温度 -40~85℃")
            < BrandKnowledgeRules.DuplicateSimilarityThreshold);
    }

    private static BrandKnowledgeItemView Item(BrandKnowledgeDimension dimension, string title, string content)
    {
        var now = DateTimeOffset.UtcNow;
        return new BrandKnowledgeItemView(Guid.NewGuid(), Guid.NewGuid(), dimension, title, content, "manual", null, null, "synced", 1,
            Guid.NewGuid(), Guid.NewGuid(), now, now);
    }
}
