using Jogeoai.Domain;

namespace Jogeoai.Tests;

/// <summary>产品信息字段校验、识别结果合并（人工优先）、核验门禁与 Markdown 模板。</summary>
public sealed class KnowledgeProductRulesTests
{
    [Fact]
    public void NormalizeTrimsAndEnforcesLengthLimits()
    {
        var normalized = KnowledgeProductRules.Normalize(new KnowledgeProductWrite("  X100  ", "  介绍 ", "", null, null,
            [new ProductSpecItem(" 电压 ", " 220V "), new ProductSpecItem("", "")], " https://example.com/p/1 ", ["A", "a", " b "]), false);

        Assert.Equal("X100", normalized.Name);
        Assert.Equal("介绍", normalized.Introduction);
        Assert.Null(normalized.Advantages);
        Assert.Equal("https://example.com/p/1", normalized.ProductUrl);
        var spec = Assert.Single(normalized.Specs!);
        Assert.Equal(("电压", "220V"), (spec.Name, spec.Value));
        Assert.Equal(["A", "b"], normalized.Tags);
    }

    [Theory]
    [InlineData("", "介绍", 0, "产品名")]
    [InlineData("名名名名名名名名名名名名名名名名名名名名名名名名名名", "介绍", 0, "产品名")]
    [InlineData("X100", null, 0, "产品介绍不能为空")]
    [InlineData("X100", "超长", 1001, "产品介绍不能超过")]
    public void NormalizeRejectsInvalidNameAndIntroduction(string name, string? introduction, int padIntroduction, string messageFragment)
    {
        var text = padIntroduction > 0 ? new string('介', padIntroduction) : introduction;
        var exception = Assert.Throws<CollectorStoreException>(() =>
            KnowledgeProductRules.Normalize(new KnowledgeProductWrite(name, text), requireIntroduction: true));
        Assert.Equal("PRODUCT_VALIDATION", exception.Code);
        Assert.Equal(400, exception.StatusCode);
        Assert.Contains(messageFragment, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeRejectsSpecTagAndUrlLimits()
    {
        var tooManySpecs = Enumerable.Range(1, 51).Select(i => new ProductSpecItem($"p{i}", "v")).ToArray();
        Assert.Equal("PRODUCT_VALIDATION", Assert.Throws<CollectorStoreException>(() =>
            KnowledgeProductRules.Normalize(new KnowledgeProductWrite("X", Specs: tooManySpecs), false)).Code);
        Assert.Equal("PRODUCT_VALIDATION", Assert.Throws<CollectorStoreException>(() =>
            KnowledgeProductRules.Normalize(new KnowledgeProductWrite("X", Specs: [new ProductSpecItem(new string('n', 31), "v")]), false)).Code);
        Assert.Equal("PRODUCT_VALIDATION", Assert.Throws<CollectorStoreException>(() =>
            KnowledgeProductRules.Normalize(new KnowledgeProductWrite("X", Specs: [new ProductSpecItem("n", new string('v', 201))]), false)).Code);
        Assert.Equal("PRODUCT_VALIDATION", Assert.Throws<CollectorStoreException>(() =>
            KnowledgeProductRules.Normalize(new KnowledgeProductWrite("X", Tags: Enumerable.Range(1, 11).Select(i => $"t{i}").ToArray()), false)).Code);
        Assert.Equal("PRODUCT_VALIDATION", Assert.Throws<CollectorStoreException>(() =>
            KnowledgeProductRules.Normalize(new KnowledgeProductWrite("X", ProductUrl: "ftp://example.com/x"), false)).Code);
        Assert.Equal("PRODUCT_VALIDATION", Assert.Throws<CollectorStoreException>(() =>
            KnowledgeProductRules.Normalize(new KnowledgeProductWrite("X", ProductUrl: "https://user:pw@example.com/x"), false)).Code);
    }

    [Fact]
    public void MergeKeepsManualFieldsAndFillsEmptyOnesFromAi()
    {
        var current = new KnowledgeProductWrite("X100", "人工写的介绍", null, null, null, [new ProductSpecItem("颜色", "黑")]);
        var sources = KnowledgeProductRules.ApplyManualEdit(null, current, null);
        var result = new ProductParseResult(
            new KnowledgeProductWrite("模型改名", "模型介绍", "模型优势", null, null, [new ProductSpecItem("电压", "220V")]),
            new Dictionary<string, ProductFieldSource>
            {
                ["name"] = Ai(true), ["introduction"] = Ai(true), ["advantages"] = Ai(true), ["scenarios"] = Ai(false), ["specs"] = Ai(true)
            }, "gpt-6-luna", "openai-compatible", 10, false, "hash");

        var (fields, merged) = KnowledgeProductRules.Merge(current, sources, result);

        Assert.Equal("X100", fields.Name);
        Assert.Equal("人工写的介绍", fields.Introduction);
        Assert.Equal("manual", merged["introduction"].Origin);
        Assert.Equal("模型优势", fields.Advantages);
        Assert.Equal("ai", merged["advantages"].Origin);
        Assert.False(merged["advantages"].Verified);
        Assert.Null(fields.Scenarios);
        Assert.False(merged["scenarios"].Found);
        Assert.Equal("颜色", Assert.Single(fields.Specs!).Name);
        Assert.Equal("manual", merged["specs"].Origin);
    }

    [Fact]
    public void MergeOverwritesEarlierAiFieldsWithNewAiResult()
    {
        var current = new KnowledgeProductWrite("X100", "旧的 AI 介绍");
        var sources = new Dictionary<string, ProductFieldSource> { ["introduction"] = Ai(true) };
        var result = new ProductParseResult(new KnowledgeProductWrite("X100", "新的 AI 介绍"),
            new Dictionary<string, ProductFieldSource> { ["introduction"] = Ai(true) }, "gpt-6-luna", "openai-compatible", 1, false, "h");

        var (fields, merged) = KnowledgeProductRules.Merge(current, sources, result);

        Assert.Equal("新的 AI 介绍", fields.Introduction);
        Assert.Equal("ai", merged["introduction"].Origin);
    }

    [Fact]
    public void VerifyRequiresEveryFoundAiFieldToBeCheckedAndSkipsNotFoundOnes()
    {
        var fields = new KnowledgeProductWrite("X100", "介绍", "优势", null, null, [new ProductSpecItem("电压", "220V")]);
        var sources = new Dictionary<string, ProductFieldSource>
        {
            ["name"] = new("manual", true, true, false, []),
            ["introduction"] = Ai(true), ["advantages"] = Ai(true), ["cases"] = Ai(false), ["specs"] = Ai(true)
        };

        var exception = Assert.Throws<CollectorStoreException>(() => KnowledgeProductRules.Verify(fields, sources, ["introduction"]));
        Assert.Equal("PRODUCT_FIELDS_UNVERIFIED", exception.Code);
        Assert.Equal(409, exception.StatusCode);
        Assert.Contains("advantages", exception.Message, StringComparison.Ordinal);
        Assert.Contains("specs", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("cases", exception.Message, StringComparison.Ordinal);

        var verified = KnowledgeProductRules.Verify(fields, sources, ["introduction", "advantages", "specs"]);
        Assert.All(verified.Values, source => Assert.True(source.Verified));
        Assert.False(verified["cases"].Found);
    }

    [Fact]
    public void ManualEditMarksChangedFieldsAsManualAndVerified()
    {
        var previous = new KnowledgeProductWrite("X100", "AI 介绍", "AI 优势");
        var sources = new Dictionary<string, ProductFieldSource> { ["introduction"] = Ai(true), ["advantages"] = Ai(true) };

        var edited = KnowledgeProductRules.ApplyManualEdit(previous, new KnowledgeProductWrite("X100", "人工改过的介绍", "AI 优势"), sources);

        Assert.Equal("manual", edited["introduction"].Origin);
        Assert.True(edited["introduction"].Verified);
        Assert.Equal("ai", edited["advantages"].Origin);
        Assert.False(edited["advantages"].Verified);
        Assert.Equal("manual", edited["name"].Origin);
    }

    [Fact]
    public void MarkdownTemplateContainsBrandSectionsSpecTableAndProductId()
    {
        var productId = Guid.NewGuid();
        var product = new KnowledgeProductView(productId, Guid.NewGuid(), Guid.NewGuid(), "X100 智能插座", KnowledgeProductStatus.Approved,
            "file", "介绍正文", "优势正文", null, "案例正文", [new ProductSpecItem("额定电压", "220V"), new ProductSpecItem("接口|类型", "Type-C")],
            "https://example.com/x100", ["插座"], new Dictionary<string, ProductFieldSource>(), Guid.NewGuid(), null, null, null,
            Guid.NewGuid(), Guid.NewGuid(), new DateTimeOffset(2026, 10, 19, 3, 0, 0, TimeSpan.Zero), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var markdown = KnowledgeProductRules.RenderMarkdown(product, "优联科技");

        Assert.StartsWith("# X100 智能插座", markdown, StringComparison.Ordinal);
        Assert.Contains("- 关联品牌：优联科技", markdown, StringComparison.Ordinal);
        Assert.Contains(productId.ToString(), markdown, StringComparison.Ordinal);
        Assert.Contains("## 产品介绍\n\n介绍正文", markdown, StringComparison.Ordinal);
        Assert.Contains("## 客户案例", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("## 适用场景与人群", markdown, StringComparison.Ordinal);
        Assert.Contains("| 额定电压 | 220V |", markdown, StringComparison.Ordinal);
        Assert.Contains("| 接口\\|类型 | Type-C |", markdown, StringComparison.Ordinal);
        Assert.Contains("- 录入方式：素材库文件识别", markdown, StringComparison.Ordinal);
        Assert.Contains("2026-10-19 03:00 UTC", markdown, StringComparison.Ordinal);
    }

    private static ProductFieldSource Ai(bool found) => new("ai", found, false, false, found ? [new ProductFieldEvidence("引用", "page:1")] : []);
}
