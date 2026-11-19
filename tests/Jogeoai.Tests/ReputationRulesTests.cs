using Jogeoai.Domain;

namespace Jogeoai.Tests;

/// <summary>口碑口径纯函数：健康度、等级、显示占比、状态归并与分类结果校验。</summary>
public sealed class ReputationRulesTests
{
    private const string Answer = "Acme 的售后服务响应很快，价格偏高，但整体性价比不错。豆包推荐 Acme。";

    private static ReputationClassificationInput Input(double threshold = 0.6)
        => new("Acme", ["艾克米"], ["acme.test", "Beta"], Answer, [], threshold);

    [Theory]
    [InlineData(2, 1, 1, 62.5)]
    [InlineData(1, 0, 0, 100)]
    [InlineData(0, 3, 0, 50)]
    [InlineData(0, 0, 4, 0)]
    [InlineData(5, 3, 2, 65)]
    public void HealthIsPositiveTimes100PlusNeutralTimes50OverClassifiedCount(long p, long n, long neg, double expected)
    {
        var score = ReputationRules.Score(p, n, neg);
        Assert.Equal(expected, score.Health!.Value, 9);
        Assert.Equal(p + n + neg, score.Classified);
        Assert.Equal(MetricRatio.Of(p, p + n + neg), score.PositiveRate);
    }

    [Fact]
    public void CoverageIsNotMultipliedIntoHealthButFlagsPartialCoverage()
    {
        // 3 正面 + 7 待判定：健康度仍是 100，覆盖率 30% → 部分覆盖。
        var score = ReputationRules.Score(3, 0, 0, unknown: 2, pending: 4, failed: 1);
        Assert.Equal(100, score.Health);
        Assert.Equal(10, score.Mentioned);
        Assert.Equal(MetricRatio.Of(3, 10), score.Coverage);
        Assert.True(score.PartialCoverage);
        Assert.False(score.LowSample);
        Assert.Null(score.UnavailableReason);
        Assert.False(ReputationRules.Score(8, 0, 0, unknown: 2).PartialCoverage); // 80% 恰好不算部分覆盖
    }

    [Fact]
    public void ZeroSamplesGiveNullHealthNotZero()
    {
        var empty = ReputationRules.Score(0, 0, 0);
        Assert.Null(empty.Health);
        Assert.Null(empty.Level);
        Assert.Null(empty.DisplayPercents);
        Assert.Null(empty.PositiveRate.Value);
        Assert.False(empty.LowSample);
        Assert.False(empty.PartialCoverage);
        Assert.Equal("NO_MENTIONED_ANSWERS", empty.UnavailableReason);
        var pendingOnly = ReputationRules.Score(0, 0, 0, pending: 2);
        Assert.Null(pendingOnly.Health);
        Assert.Equal("NO_CLASSIFIED_SAMPLE", pendingOnly.UnavailableReason);
        Assert.True(pendingOnly.PartialCoverage);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public void LowSampleBelowThree(long classified, bool expected)
        => Assert.Equal(expected, ReputationRules.Score(classified, 0, 0).LowSample);

    [Theory]
    [InlineData(100, ReputationHealthLevel.Excellent)]
    [InlineData(80, ReputationHealthLevel.Excellent)]
    [InlineData(79.5, ReputationHealthLevel.Excellent)] // 取整后 80，与整数显示一致
    [InlineData(79.4, ReputationHealthLevel.Good)]
    [InlineData(65, ReputationHealthLevel.Good)]
    [InlineData(64.4, ReputationHealthLevel.Fair)]
    [InlineData(50, ReputationHealthLevel.Fair)]
    [InlineData(49, ReputationHealthLevel.Risk)]
    [InlineData(35, ReputationHealthLevel.Risk)]
    [InlineData(34.4, ReputationHealthLevel.HighRisk)]
    [InlineData(0, ReputationHealthLevel.HighRisk)]
    public void LevelThresholdsAre80_65_50_35(double health, ReputationHealthLevel expected)
        => Assert.Equal(expected, ReputationRules.Level(health));

    [Fact]
    public void LevelThresholdsAreReturnedHighToLow()
        => Assert.Equal(new[] { 80, 65, 50, 35, 0 }, ReputationRules.LevelThresholds.Select(t => t.MinHealth));

    [Theory]
    [InlineData(1, 1, 1, 33.4, 33.3, 33.3)]
    [InlineData(2, 1, 0, 66.7, 33.3, 0)]
    [InlineData(1, 2, 4, 14.3, 28.6, 57.1)]
    [InlineData(1, 1, 4, 16.7, 16.7, 66.6)] // 16.7 + 16.7 + 66.7 = 100.1，差额 −0.1 计入数量最大的负面
    public void DisplayPercentsAlwaysSumTo100(long p, long n, long neg, double ep, double en, double eneg)
    {
        var display = ReputationRules.DisplayPercents(p, n, neg)!;
        Assert.Equal(new[] { ep, en, eneg }, new[] { display.Positive, display.Neutral, display.Negative });
        Assert.Equal(1000, (long)Math.Round(display.Positive * 10) + (long)Math.Round(display.Neutral * 10)
            + (long)Math.Round(display.Negative * 10));
    }

    [Theory]
    [InlineData(false, true, ReputationSentimentState.Positive, false, ReputationSentimentState.NotApplicable)]
    [InlineData(true, false, ReputationSentimentState.Negative, false, ReputationSentimentState.NotMentioned)]
    [InlineData(true, false, null, true, ReputationSentimentState.NotMentioned)]
    [InlineData(true, true, ReputationSentimentState.Positive, false, ReputationSentimentState.Positive)]
    [InlineData(true, true, ReputationSentimentState.Unknown, true, ReputationSentimentState.Unknown)]
    [InlineData(true, true, ReputationSentimentState.NotMentioned, false, ReputationSentimentState.Unknown)]
    [InlineData(true, true, null, true, ReputationSentimentState.Failed)]
    [InlineData(true, true, null, false, ReputationSentimentState.Pending)]
    public void ResolveStateUsesMentionAnalysisForNotMentioned(bool eligible, bool mentioned,
        ReputationSentimentState? stored, bool failed, ReputationSentimentState expected)
        => Assert.Equal(expected, ReputationRules.ResolveState(eligible, mentioned, stored, failed));

    [Fact]
    public void EvaluateAcceptsValidOutputWithOriginalOffsetsAndFilteredTerms()
    {
        var output = new ReputationClassifierOutput("positive", 0.9, "praise", "售后服务响应很快",
        [
            new("售后服务响应很快", "positive"), new("价格偏高", "negative"), new("性价比不错", "positive"),
            new("Acme", "positive"),            // 品牌名
            new("Acme 的售后", "positive"),     // 包含品牌名
            new("豆包", "neutral"),             // 平台名
            new("推荐", "positive"),            // 停用词
            new("质量可靠", "positive"),        // 原文没有
            new("价格偏高", "negative"),        // 重复
            new("性价比不错", "bogus")          // 极性非法
        ]);

        var result = ReputationRules.Evaluate(output, Input(), "gpt-6-luna");

        Assert.Equal(ReputationSentimentState.Positive, result.State);
        Assert.Equal("PRAISE", result.RationaleCode);
        Assert.Equal(Answer.IndexOf("售后服务响应很快", StringComparison.Ordinal), result.EvidenceStart);
        Assert.Equal(result.EvidenceStart + "售后服务响应很快".Length, result.EvidenceEnd);
        Assert.Equal(new[] { "售后服务响应很快", "价格偏高", "性价比不错" }, result.Terms.Select(t => t.TermText));
        Assert.All(result.Terms, t => Assert.Equal(1, t.Occurrences));
        Assert.Equal(ReputationRules.ClassifierVersion, result.ClassifierVersion);
        Assert.Equal("gpt-6-luna", result.ModelVersion);
    }

    [Fact]
    public void EvaluateNeverReturnsNotMentioned()
    {
        var result = ReputationRules.Evaluate(new("not_mentioned", 0.95, null, null, []), Input(), "m");
        Assert.Equal(ReputationSentimentState.Unknown, result.State);
        Assert.Equal(ReputationRules.MentionDisagree, result.RationaleCode);
        Assert.Empty(result.Terms);
    }

    [Theory]
    // 模型去掉加粗标记
    [InlineData("推荐 **优联 4188XD（23mm 自水平）** 作为首选", "优联 4188XD（23mm 自水平） 作为首选", "优联 4188XD（23mm 自水平）** 作为首选")]
    // 模型把表格行连成一句、空白不同
    [InlineData("| 第二梯队 | 优联科技 | 推杆式、QV |\n", "第二梯队 优联科技 推杆式、QV", "第二梯队 | 优联科技 | 推杆式、QV")]
    // 跨行、多空格、行内代码
    [InlineData("配套 `ulinkCamera` App，\n  支持拍照、录像", "配套 ulinkCamera App，支持拍照、录像", "配套 `ulinkCamera` App，\n  支持拍照、录像")]
    public void EvidenceMatchToleratesBoldTablePipesWhitespaceAndInlineCode(string answer, string quote, string expected)
    {
        var span = ReputationRules.FindEvidence(answer, quote);
        Assert.NotNull(span);
        Assert.Equal(expected, answer[span.Value.Start..span.Value.End]);
    }

    [Fact]
    public void TolerantEvidenceMatchRejectsShortOrAbsentQuotes()
    {
        Assert.Null(ReputationRules.FindEvidence("**优联** 很好", "优 联很"));
        Assert.Null(ReputationRules.FindEvidence("**优联科技** 售后很好", "优联科技售后很差"));
        Assert.Equal((0, 4), ReputationRules.FindEvidence("优联科技", "优联科技"));
    }

    [Fact]
    public void EvaluateAcceptsFormattedEvidenceAndCountsFormattedTerms()
    {
        const string answer = "国产推荐：**优联 Ulinktek**、西森姆，**价格 友好**、售后方便。";
        var input = new ReputationClassificationInput("优联", [], [], answer, [], 0.6);
        var result = ReputationRules.Evaluate(new("positive", 0.95, "RECOMMENDATION", "优联 Ulinktek、西森姆，价格友好",
            [new("价格友好", "positive")]), input, "m");
        Assert.Equal(ReputationSentimentState.Positive, result.State);
        // 片段从第一个实际字符开始，不含前导格式标记。
        Assert.Equal("优联 Ulinktek**、西森姆，**价格 友好", answer[result.EvidenceStart!.Value..result.EvidenceEnd!.Value]);
        Assert.Null(result.ModelQuote);
        Assert.Equal(1, Assert.Single(result.Terms).Occurrences);
    }

    [Fact]
    public void UnknownResultsKeepTheModelQuoteBounded()
    {
        var missing = ReputationRules.Evaluate(new("negative", 0.9, "PRAISE", "这句话不在原文里", []), Input(), "m");
        Assert.Equal(ReputationRules.EvidenceNotFound, missing.RationaleCode);
        Assert.Equal("这句话不在原文里", missing.ModelQuote);
        var longQuote = new string('长', 500);
        var bounded = ReputationRules.Evaluate(new("unknown", 0.9, "PRAISE", longQuote, []), Input(), "m");
        Assert.Equal(ReputationRules.MaxModelQuoteLength, bounded.ModelQuote!.Length);
    }

    [Theory]
    [InlineData("great", 0.9, "售后服务响应很快", ReputationRules.InvalidOutput)]
    [InlineData("positive", null, "售后服务响应很快", ReputationRules.InvalidOutput)]
    [InlineData("positive", 1.5, "售后服务响应很快", ReputationRules.InvalidOutput)]
    [InlineData("positive", 0.59, "售后服务响应很快", ReputationRules.LowConfidence)]
    [InlineData("negative", 0.9, "这句话不在原文里", ReputationRules.EvidenceNotFound)]
    [InlineData("neutral", 0.9, null, ReputationRules.EvidenceNotFound)]
    [InlineData("unknown", 0.9, "售后服务响应很快", ReputationRules.ClassifierUnknown)]
    public void EvaluateMapsUntrustworthyOutputToUnknownWithReason(string state, double? confidence, string? evidence,
        string expectedCode)
    {
        var result = ReputationRules.Evaluate(new(state, confidence, "PRAISE", evidence, [new("价格偏高", "negative")]),
            Input(), "m");
        Assert.Equal(ReputationSentimentState.Unknown, result.State);
        Assert.Equal(expectedCode, result.RationaleCode);
        Assert.Empty(result.Terms);
        Assert.Null(result.EvidenceStart);
    }

    [Fact]
    public void EvaluateNullOutputIsInvalid()
        => Assert.Equal(ReputationRules.InvalidOutput, ReputationRules.Evaluate(null, Input(), "m").RationaleCode);

    [Fact]
    public void UnknownRationaleCodeFoldsToOther()
        => Assert.Equal(ReputationRules.OtherRationale,
            ReputationRules.Evaluate(new("neutral", 0.8, "made-up", "价格偏高", []), Input(), "m").RationaleCode);

    [Fact]
    public void TermNormalizationIsSharedWithRecordFilter()
    {
        Assert.Equal("great value", ReputationRules.NormalizeTerm("  Great \t VALUE "));
        Assert.Equal("abc", ReputationRules.NormalizeTerm("ＡＢＣ")); // NFKC 全角
        Assert.Equal(ReputationRules.TermHash("great value"), ReputationRules.TermHash(ReputationRules.NormalizeTerm("great  value")));
    }

    [Fact]
    public void InputHashIgnoresAliasOrderButNotAnswer()
    {
        var a = ReputationRules.InputHash("Acme", ["b", "a"], "answer");
        Assert.Equal(a, ReputationRules.InputHash("Acme", ["a", "b", " a "], "answer"));
        Assert.NotEqual(a, ReputationRules.InputHash("Acme", ["a", "b"], "answer!"));
        Assert.Equal(64, a.Length);
    }

    [Fact]
    public void InputHashChangesWhenExcludedTermsOrConfidenceThresholdChange()
    {
        var a = ReputationRules.InputHash("Acme", ["a"], "answer", ["Rival", "豆包"], 0.6);
        Assert.Equal(a, ReputationRules.InputHash("Acme", ["a"], "answer", ["豆包", " Rival "], 0.6));
        Assert.NotEqual(a, ReputationRules.InputHash("Acme", ["a"], "answer", ["Rival"], 0.6));
        Assert.NotEqual(a, ReputationRules.InputHash("Acme", ["a"], "answer", ["Rival", "豆包"], 0.7));
    }

    [Fact]
    public void LatinExcludedNamesNeedWordBoundariesWhileCjkNamesMatchAsSubstrings()
    {
        const string answer = "Acme offers strong research tools and a solid toolbox; Arc browser is a rival. Acme的售后很好，小米手机一般。";
        var input = new ReputationClassificationInput("Acme", ["Box"], ["Arc", "小米"], answer, [], 0.6);
        var terms = ReputationRules.ValidateTerms(
        [
            new("research tools", "positive"), new("solid toolbox", "positive"), new("Arc browser", "neutral"),
            new("Acme的售后", "positive"), new("小米手机", "neutral"), new("strong", "positive")
        ], input);
        // "arc" / "box" 只按词命中：research、toolbox 保留；"arc browser"、含品牌名或中文竞品名的短语被排除。
        Assert.Equal(new[] { "research tools", "solid toolbox", "strong" }, terms.Select(t => t.TermKey));
    }

    [Fact]
    public void DominantPolarityPrefersAnswersThenOccurrencesThenRisk()
    {
        Assert.Equal(ReputationTermPolarity.Positive, ReputationRules.DominantPolarity(3, 1, 1, 3, 1, 1));
        Assert.Equal(ReputationTermPolarity.Neutral, ReputationRules.DominantPolarity(2, 2, 0, 2, 5, 0));
        Assert.Equal(ReputationTermPolarity.Negative, ReputationRules.DominantPolarity(1, 0, 1, 1, 0, 1));
    }
}
