using Jogeoai.Domain;

namespace Jogeoai.Tests;

/// <summary>口碑投影：平台三元组、趋势补齐日期、环比、热力图列集合、问题排序、情感因子分组与回答列表筛选一致性。</summary>
public sealed class ReputationProjectorTests
{
    private static readonly DateOnly Day1 = new(2026, 10, 24);
    private static readonly Guid PromptA = Guid.Parse("00000000-0000-4000-8000-00000000000a");
    private static readonly Guid PromptB = Guid.Parse("00000000-0000-4000-8000-00000000000b");

    private static ReputationFact Fact(ReputationSentimentState state, string provider = "doubao_api", string engine = "doubao",
        string mode = "api", Guid? prompt = null, int day = 0, params ReputationFactTerm[] terms)
        => new(Guid.NewGuid(), prompt ?? PromptA, prompt == PromptB ? "问题 B" : "问题 A",
            new DateTimeOffset(Day1.AddDays(day).ToDateTime(new TimeOnly(10, 0)), TimeSpan.Zero), Day1.AddDays(day),
            provider, engine, mode, state, state == ReputationSentimentState.Unknown ? "LOW_CONFIDENCE" : null, terms);

    private static ReputationMeta Meta(ReputationView view = ReputationView.Range, int days = 7)
        => new("token", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null!, "UTC", view, Day1, Day1.AddDays(days),
            Guid.NewGuid(), DateTimeOffset.UnixEpoch, null, false, ReputationRules.MinSampleSize,
            ReputationRules.PartialCoverageThreshold, ReputationRules.HealthVersion, ReputationRules.ClassifierVersion,
            ReputationRules.LevelThresholds, 0, 0);

    private static ReputationSummaryView Build(IReadOnlyList<ReputationFact> current, IReadOnlyList<ReputationFact>? previous = null,
        IReadOnlyList<ReputationPlatformCandidate>? configured = null, ReputationMeta? meta = null)
        => ReputationProjector.Build(meta ?? Meta(), current, previous ?? [], configured ?? [],
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(7));

    private const ReputationSentimentState P = ReputationSentimentState.Positive;
    private const ReputationSentimentState N = ReputationSentimentState.Neutral;
    private const ReputationSentimentState Neg = ReputationSentimentState.Negative;

    [Fact]
    public void WebAndAppOfSameEngineAreSeparatePlatformsAndHeatmapColumnsMatch()
    {
        var facts = new[]
        {
            Fact(P, "doubao_api", "doubao", "api"), Fact(P, "doubao_api", "doubao", "api"), Fact(N, "doubao_api", "doubao", "api"),
            Fact(Neg, "domestic_collector", "doubao", "scraped"),
        };
        var view = Build(facts, configured: [new("cloro", "chatgpt", "scraped")]);

        Assert.Equal(new[] { "doubao_api|doubao|api", "domestic_collector|doubao|scraped" },
            view.Platforms.Select(p => p.PlatformKey));
        Assert.Equal(view.Platforms.Select(p => p.PlatformKey), view.Heatmap.Columns);
        Assert.All(view.Heatmap.Rows, row => Assert.Equal(view.Heatmap.Columns, row.Cells.Select(c => c.PlatformKey)));
        Assert.Equal(83.333333, view.Platforms[0].Score.Health!.Value, 5);
        Assert.Equal(0, view.Platforms[1].Score.Health);
        Assert.Equal("doubao", view.Platforms[0].DisplayName);
        Assert.Equal(PlatformIdentity.DisplayName("doubao_api", "doubao", "api"), view.Platforms[0].DisplayName);
        var without = Assert.Single(view.PlatformsWithoutData);
        Assert.Equal("cloro|chatgpt|scraped", without.PlatformKey);
        Assert.Equal("NO_ANSWERS", without.Reason);
        Assert.Equal(2, view.Quality.PlatformsWithData);
    }

    [Fact]
    public void AllDimensionsUseTheSameHealthFunction()
    {
        var facts = new[] { Fact(P), Fact(N), Fact(Neg), Fact(P, prompt: PromptB), Fact(N, prompt: PromptB) };
        var view = Build(facts);
        var expected = ReputationRules.Score(2, 2, 1).Health;
        Assert.Equal(expected, view.Metrics.Score.Health);
        Assert.Equal(expected, view.Platforms.Single().Score.Health);
        Assert.Equal(expected, view.Trend.Single(t => t.Date == Day1).Score.Health);
        // 问题与单元格按回答数合并计算，而不是对子维度求平均。
        Assert.Equal(50, view.Questions.Single(q => q.PromptId == PromptA).Score.Health);
        Assert.Equal(75, view.Questions.Single(q => q.PromptId == PromptB).Score.Health);
        Assert.Equal(50, view.Heatmap.Rows.Single(r => r.PromptId == PromptA).Cells.Single().Score.Health);
        Assert.All(new[] { view.Metrics.Score.Health, view.Platforms[0].Score.Health }, h => Assert.InRange(h!.Value, 0, 100));
    }

    [Fact]
    public void RangeTrendCoversEveryLocalDateWithNullHealthForEmptyDays()
    {
        var view = Build([Fact(P, day: 0), Fact(Neg, day: 3)]);
        Assert.Equal(7, view.Trend.Count);
        Assert.Equal(Enumerable.Range(0, 7).Select(d => Day1.AddDays(d)), view.Trend.Select(t => t.Date));
        Assert.Equal(100, view.Trend[0].Score.Health);
        Assert.Null(view.Trend[1].Score.Health);
        Assert.Equal(0, view.Trend[3].Score.Health);
        Assert.Null(view.Trend[6].Score.Health);
        var platformTrend = Assert.Single(view.PlatformTrends);
        Assert.Equal(view.Trend.Select(t => t.Date), platformTrend.Points.Select(p => p.Date));
    }

    [Fact]
    public void DayViewReturnsEmptyTrend()
    {
        var view = Build([Fact(P), Fact(N)], meta: Meta(ReputationView.Day, 1));
        Assert.Empty(view.Trend);
        Assert.Empty(view.PlatformTrends);
        Assert.Single(view.Platforms);
    }

    [Fact]
    public void ComparisonNeedsThreePreviousSamples()
    {
        var current = new[] { Fact(P), Fact(P), Fact(Neg) };
        var insufficient = Build(current, previous: [Fact(P), Fact(Neg), Fact(ReputationSentimentState.Unknown)]);
        Assert.Equal("INSUFFICIENT_SAMPLE", insufficient.Comparison.UnavailableReason);
        Assert.Null(insufficient.Comparison.HealthDifference);
        Assert.Null(insufficient.Comparison.PositiveRatePointDifference);

        var enough = Build(current, previous: [Fact(P), Fact(Neg), Fact(Neg), Fact(Neg)]);
        Assert.Null(enough.Comparison.UnavailableReason);
        Assert.Equal(200d / 3 - 25, enough.Comparison.HealthDifference!.Value, 9);
        Assert.Equal((2d / 3 - 0.25) * 100, enough.Comparison.PositiveRatePointDifference!.Value, 9);
        Assert.Equal((1d / 3 - 0.75) * 100, enough.Comparison.NegativeRatePointDifference!.Value, 9);

        var noCurrent = Build([Fact(ReputationSentimentState.Pending)], previous: [Fact(P), Fact(P), Fact(P)]);
        Assert.Equal("NO_CURRENT_SAMPLE", noCurrent.Comparison.UnavailableReason);
    }

    [Fact]
    public void QuestionsAreRiskFirstWithLowSampleLast()
    {
        var c = Guid.Parse("00000000-0000-4000-8000-00000000000c");
        var facts = new List<ReputationFact>
        {
            Fact(P, prompt: PromptA), Fact(P, prompt: PromptA), Fact(P, prompt: PromptA),
            Fact(Neg, prompt: PromptB), Fact(N, prompt: PromptB), Fact(Neg, prompt: PromptB),
            Fact(Neg, prompt: c), // 只有 1 条：样本不足，即使 0 分也排在最后
        };
        var view = Build(facts);
        Assert.Equal(new[] { PromptB, PromptA, c }, view.Questions.Select(q => q.PromptId));
        Assert.True(view.Questions[2].Score.LowSample);
        Assert.Equal(ReputationCellState.LowSample, view.Heatmap.Rows[2].Cells.Single().State);
        Assert.Equal(ReputationCellState.Ok, view.Heatmap.Rows[0].Cells.Single().State);
    }

    [Fact]
    public void HeatmapDistinguishesNoDataPendingAndLowSample()
    {
        var facts = new[]
        {
            Fact(P, "cloro", "chatgpt", "scraped", PromptA), Fact(P, "cloro", "chatgpt", "scraped", PromptA),
            Fact(P, "cloro", "chatgpt", "scraped", PromptA),
            Fact(ReputationSentimentState.Pending, "doubao_api", "doubao", "api", PromptA),
            Fact(N, "doubao_api", "doubao", "api", PromptB),
        };
        var view = Build(facts);
        var rowA = view.Heatmap.Rows.Single(r => r.PromptId == PromptA);
        var rowB = view.Heatmap.Rows.Single(r => r.PromptId == PromptB);
        Assert.Equal(ReputationCellState.Ok, rowA.Cells.Single(c => c.PlatformKey == "cloro|chatgpt|scraped").State);
        Assert.Equal(ReputationCellState.Pending, rowA.Cells.Single(c => c.PlatformKey == "doubao_api|doubao|api").State);
        Assert.Equal(ReputationCellState.NoData, rowB.Cells.Single(c => c.PlatformKey == "cloro|chatgpt|scraped").State);
        Assert.Equal(ReputationCellState.LowSample, rowB.Cells.Single(c => c.PlatformKey == "doubao_api|doubao|api").State);
        Assert.Null(rowB.Cells.Single(c => c.PlatformKey == "cloro|chatgpt|scraped").Score.Health);
    }

    [Fact]
    public void PlatformWithoutClassifiedSamplesIsFoldedWithReason()
    {
        var facts = new[]
        {
            Fact(P, "cloro", "chatgpt", "scraped"),
            Fact(ReputationSentimentState.NotMentioned, "cloro", "perplexity", "scraped"),
            Fact(ReputationSentimentState.Failed, "cloro", "copilot", "scraped"),
            Fact(ReputationSentimentState.NotApplicable, "cloro", "gemini", "scraped"),
        };
        var view = Build(facts);
        Assert.Equal(new[] { "cloro|chatgpt|scraped" }, view.Platforms.Select(p => p.PlatformKey));
        Assert.Equal(new Dictionary<string, string>
        {
            ["cloro|copilot|scraped"] = "NOT_CLASSIFIED",
            ["cloro|gemini|scraped"] = "NO_ANSWERS",
            ["cloro|perplexity|scraped"] = "NO_MENTIONS"
        }, view.PlatformsWithoutData.ToDictionary(p => p.PlatformKey, p => p.Reason));
    }

    [Fact]
    public void BestAndWorstExcludeLowSamplePlatforms()
    {
        var facts = new List<ReputationFact>();
        facts.AddRange(Enumerable.Range(0, 3).Select(_ => Fact(P, "cloro", "chatgpt", "scraped")));
        facts.AddRange(Enumerable.Range(0, 3).Select(_ => Fact(N, "cloro", "copilot", "scraped")));
        facts.Add(Fact(Neg, "cloro", "gemini", "scraped"));
        var view = Build(facts);
        Assert.True(view.Platforms.Single(p => p.Engine == "chatgpt").IsBest);
        Assert.True(view.Platforms.Single(p => p.Engine == "copilot").IsWorst);
        var gemini = view.Platforms.Single(p => p.Engine == "gemini");
        Assert.True(gemini.Score.LowSample);
        Assert.False(gemini.IsBest || gemini.IsWorst);
        Assert.Equal(gemini.Score.Health - view.Metrics.Score.Health, gemini.HealthDifference);
    }

    [Fact]
    public void TermsGroupByDominantPolarityAndOnlyFromClassifiedAnswers()
    {
        ReputationFactTerm T(string key, ReputationTermPolarity polarity, int count = 1) => new(key, key, polarity, count);
        var facts = new[]
        {
            Fact(P, terms: [T("响应快", ReputationTermPolarity.Positive), T("价格高", ReputationTermPolarity.Negative, 5)]),
            Fact(N, terms: [T("响应快", ReputationTermPolarity.Positive)]),
            Fact(Neg, terms: [T("响应快", ReputationTermPolarity.Negative)]),
            Fact(ReputationSentimentState.Unknown, terms: [T("被忽略", ReputationTermPolarity.Positive)]),
        };
        var groups = Build(facts).TermGroups;
        // 「响应快」以负面也出现过，但只归入主要极性（正面）一组。
        var fast = Assert.Single(groups.Positive);
        Assert.Equal("响应快", fast.TermKey);
        Assert.Equal(3, fast.AnswerCount);
        Assert.Equal(2, fast.PositiveAnswers);
        Assert.Equal(1, fast.NegativeAnswers);
        Assert.Equal(ReputationTermPolarity.Positive, fast.DominantPolarity);
        var price = Assert.Single(groups.Negative);
        Assert.Equal("价格高", price.TermKey);
        Assert.Equal(5, price.Occurrences);
        Assert.Equal(1, price.AnswerCount);
        Assert.Empty(groups.Neutral);
    }

    [Fact]
    public void EachPolarityKeepsItsOwnTopTenSoRareNegativeTermsSurvive()
    {
        ReputationFactTerm T(string key, ReputationTermPolarity polarity, int count = 1) => new(key, key, polarity, count);
        var facts = new List<ReputationFact>();
        // 60 个正面词各出现在 2 条回答中，原全局 Top 50 会挤掉只出现 1 次的负面词。
        for (var i = 0; i < 60; i++)
            facts.AddRange(Enumerable.Range(0, 2).Select(_ => Fact(P, terms: [T($"好{i:00}", ReputationTermPolarity.Positive)])));
        // 正面组平分：正面回答数相同，按总回答数（含负面）决胜，再按出现次数、词键。
        facts.Add(Fact(Neg, terms: [T("好59", ReputationTermPolarity.Negative)]));
        facts.Add(Fact(P, terms: [T("好58", ReputationTermPolarity.Positive, 3)]));
        facts.Add(Fact(Neg, terms: [T("贵", ReputationTermPolarity.Negative)]));
        facts.Add(Fact(Neg, terms: [T("慢", ReputationTermPolarity.Negative, 2)]));
        facts.Add(Fact(N, terms: [T("一般", ReputationTermPolarity.Neutral)]));
        var groups = Build(facts).TermGroups;

        Assert.Equal(ReputationRules.TopTermsPerPolarity, groups.Positive.Count);
        Assert.Equal(new[] { "好58", "好59", "好00", "好01" }, groups.Positive.Take(4).Select(t => t.TermKey));
        Assert.Equal(new[] { "慢", "贵" }, groups.Negative.Select(t => t.TermKey));
        Assert.Equal("一般", Assert.Single(groups.Neutral).TermKey);
        var keys = groups.Positive.Concat(groups.Neutral).Concat(groups.Negative).Select(t => t.TermKey).ToArray();
        Assert.Equal(keys.Length, keys.Distinct().Count());
    }

    [Fact]
    public void EveryAggregateCountEqualsTheRecordFilterCount()
    {
        ReputationFactTerm T(string key) => new(key, key, ReputationTermPolarity.Positive, 1);
        var states = new[] { P, P, N, Neg, ReputationSentimentState.Unknown, ReputationSentimentState.Pending,
            ReputationSentimentState.Failed, ReputationSentimentState.NotMentioned, ReputationSentimentState.NotApplicable };
        var facts = states.Select((s, i) => Fact(s, i % 2 == 0 ? "cloro" : "doubao_api", i % 2 == 0 ? "chatgpt" : "doubao",
            i % 2 == 0 ? "scraped" : "api", i % 3 == 0 ? PromptA : PromptB, i % 4,
            ReputationRules.IsClassified(s) ? [T("口碑好")] : [])).ToArray();
        var view = Build(facts);
        long Count(ReputationSentimentState? state, string? term = null)
            => facts.LongCount(f => ReputationProjector.MatchesRecordFilter(f, state, term));
        Assert.Equal(view.Metrics.Score.Positive, Count(P));
        Assert.Equal(view.Metrics.Score.Neutral, Count(N));
        Assert.Equal(view.Metrics.Score.Negative, Count(Neg));
        Assert.Equal(view.Quality.UnknownAnswers, Count(ReputationSentimentState.Unknown));
        Assert.Equal(view.Quality.PendingAnswers, Count(ReputationSentimentState.Pending));
        Assert.Equal(view.Quality.FailedAnswers, Count(ReputationSentimentState.Failed));
        Assert.Equal(view.Quality.NotMentionedAnswers, Count(ReputationSentimentState.NotMentioned));
        Assert.Equal(view.TermGroups.Positive.Single().AnswerCount, Count(null, "口碑好"));
        Assert.Equal(view.Metrics.Score.Positive, Count(P, "口碑好"));
        Assert.Equal(8, view.Metrics.EligibleAnswers);
        Assert.Equal(view.Metrics.Score.Mentioned, view.Quality.ClassifiedAnswers + view.Quality.UnknownAnswers
            + view.Quality.PendingAnswers + view.Quality.FailedAnswers);
        Assert.Equal(1, view.Quality.UnknownReasons["LOW_CONFIDENCE"]);
    }

    [Fact]
    public void EachFactorPolarityCountEqualsTheTermPolarityRecordFilterCount()
    {
        ReputationFactTerm T(string key, ReputationTermPolarity polarity) => new(key, key, polarity, 1);
        const ReputationTermPolarity Pos = ReputationTermPolarity.Positive;
        const ReputationTermPolarity Mid = ReputationTermPolarity.Neutral;
        const ReputationTermPolarity Bad = ReputationTermPolarity.Negative;
        var facts = new[]
        {
            // 「价格」主要极性为负面，但也以正面、中性出现；整体正面的回答里它可以是负面词。
            Fact(P, terms: [T("价格", Bad), T("响应快", Pos)]),
            Fact(P, terms: [T("价格", Pos)]),
            Fact(N, terms: [T("价格", Bad), T("价格", Mid)]),
            Fact(Neg, terms: [T("价格", Bad), T("响应快", Bad)]),
            Fact(N, terms: [T("响应快", Pos), T("一般", Mid)]),
            // 未判定回答不挂词条（与 BuildReputationFacts 一致），不进入任何计数。
            Fact(ReputationSentimentState.Unknown),
        };
        var view = Build(facts);
        long Count(string term, ReputationTermPolarity? polarity, ReputationSentimentState? state = null)
            => facts.LongCount(f => ReputationProjector.MatchesRecordFilter(f, state, term, polarity));

        var groups = new[] { view.TermGroups.Positive, view.TermGroups.Neutral, view.TermGroups.Negative };
        Assert.Equal(new[] { "一般", "价格", "响应快" }.Order(StringComparer.Ordinal),
            groups.SelectMany(g => g).Select(t => t.TermKey).Order(StringComparer.Ordinal));
        foreach (var factor in groups.SelectMany(g => g))
        {
            Assert.Equal(factor.PositiveAnswers, Count(factor.TermKey, Pos));
            Assert.Equal(factor.NeutralAnswers, Count(factor.TermKey, Mid));
            Assert.Equal(factor.NegativeAnswers, Count(factor.TermKey, Bad));
            Assert.Equal(factor.AnswerCount, Count(factor.TermKey, null));
        }
        var price = Assert.Single(view.TermGroups.Negative);
        Assert.Equal("价格", price.TermKey);
        Assert.Equal((1, 1, 3, 4), (price.PositiveAnswers, price.NeutralAnswers, price.NegativeAnswers, price.AnswerCount));
        // 词极性与回答整体判定相互独立：整体正面的回答中「价格」以负面出现。
        Assert.Equal(1, Count("价格", Bad, P));
        // 未带词键时词极性不生效。
        Assert.Equal(facts.Length, facts.LongCount(f => ReputationProjector.MatchesRecordFilter(f, null, null, Bad)));
    }

    [Fact]
    public void EmptyWindowReportsNoEligibleAnswers()
    {
        var view = Build([]);
        Assert.Equal("NO_ELIGIBLE_ANSWERS", view.Metrics.UnavailableReason);
        Assert.Null(view.Metrics.Score.Health);
        Assert.Equal(7, view.Trend.Count);
        Assert.All(view.Trend, t => Assert.Null(t.Score.Health));
        Assert.Empty(view.Platforms);
        Assert.Empty(view.Heatmap.Columns);
        Assert.Empty(view.TermGroups.Positive);
        Assert.Empty(view.TermGroups.Neutral);
        Assert.Empty(view.TermGroups.Negative);
    }
}
