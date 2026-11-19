using Jogeoai.Domain;

namespace Jogeoai.Tests;

public sealed class DashboardAnalyticsTests
{
    /// <summary>按 (主体, 首次偏移, 出现次数) 构造单个 Check 的主体统计。</summary>
    private static IReadOnlyDictionary<Guid, SubjectStats> Stats(params (Guid Subject, int Offset, int Count)[] values)
        => values.ToDictionary(v => v.Subject, v => new SubjectStats(v.Offset, v.Count));

    /// <summary>每个主体出现 1 次的简写。</summary>
    private static IReadOnlyDictionary<Guid, SubjectStats> Offsets(params (Guid Subject, int Offset)[] values)
        => values.ToDictionary(v => v.Subject, v => new SubjectStats(v.Offset, 1));

    [Fact]
    public void AggregateExposure_IsUnavailableWithoutCompetitorBaseline()
    {
        var f = new CheckMetricsFixture();
        var brandOnly = new SubjectConfiguration(f.Brand, 1, [new AnalysisSubject(f.Brand, "brand", "Acme")], [], true);
        var fact = f.Fact([f.Brand], ownCitation: true);

        var score = DashboardAnalyticsProjector.AggregateExposure([fact], brandOnly,
            new Dictionary<Guid, IReadOnlyDictionary<Guid, SubjectStats>> { [fact.CheckId] = Offsets((f.Brand, 0)) });

        Assert.Null(score.Value);
        Assert.Equal("NO_COMPETITOR_BASELINE", score.EligibilityReason);
        Assert.Equal("exposure-v2", score.ScoreVersion);
    }

    [Fact]
    public void Ranking_RequiresTwoSubjectsAndUsesFirstOffsets()
    {
        var brand = Guid.NewGuid();
        var competitor = Guid.NewGuid();
        var facts = new[]
        {
            new AnalyticsCheckFact(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, null, 1,
                DateTimeOffset.UtcNow, "succeeded", true, "completed", false, false, false,
                [brand, competitor], "cloro", "chatgpt", "US", "en-US", "scraped", "observed_surface", [], null)
        };
        var offsets = new Dictionary<Guid, IReadOnlyDictionary<Guid, SubjectStats>>
        {
            [facts[0].CheckId] = Offsets((competitor, 4), (brand, 10))
        };

        var rank = DashboardAnalyticsProjector.Ranking(facts, brand, offsets);

        Assert.Equal(2, rank.Value);
        Assert.Equal(1, rank.Denominator);
    }

    [Fact]
    public void Percentile_InterpolatesAndEmptyIsNull()
    {
        Assert.Equal(25, DashboardAnalyticsProjector.Percentile([10, 20, 30, 40], .5));
        Assert.Null(DashboardAnalyticsProjector.Percentile([], .95));
    }

    [Fact]
    public void SubjectTrends_SkipsDaysWithoutEligibleChecksAndUsesEachDaysCounts()
    {
        var subjects = new SubjectTrendFixture();

        var trends = DashboardAnalyticsProjector.SubjectTrends(subjects.Days, subjects.Configuration,
            [], subjects.Offsets);

        // Brand first, then competitors in configuration order (Gamma is configured before Beta).
        Assert.Equal([subjects.Brand, subjects.Gamma, subjects.Beta], trends.Select(t => t.SubjectId));
        Assert.Equal([true, false, false], trends.Select(t => t.IsBrand));
        Assert.Equal(["Acme", "Gamma", "Beta"], trends.Select(t => t.Name));
        Assert.All(trends, trend =>
            Assert.Equal([subjects.FirstDay, subjects.ThirdDay], trend.Points.Select(p => p.Date)));

        var brand = trends[0].Points;
        Assert.Equal(MetricRatio.Of(1, 2), brand[0].MentionRate);
        Assert.Equal(MetricRatio.Of(1, 1), brand[1].MentionRate);
        Assert.Equal([2L, 1L], brand.Select(p => p.SampleCount));
        // Each day ranks with that day's Check offsets: behind Beta on day one, first on day three.
        Assert.Equal(2, brand[0].AverageRanking.Value);
        Assert.Equal(1, brand[1].AverageRanking.Value);

        var beta = trends[2].Points;
        Assert.Equal(MetricRatio.Of(2, 2), beta[0].MentionRate);
        Assert.Equal(1, beta[0].AverageRanking.Value);
        Assert.Equal(1, beta[0].AverageRanking.Denominator);

        var gamma = trends[1].Points;
        Assert.Equal(MetricRatio.Of(0, 2), gamma[0].MentionRate);
        Assert.Equal(0, gamma[0].MentionRate.Value);
        Assert.Null(gamma[0].AverageRanking.Value);
        Assert.Equal("INSUFFICIENT_SUBJECT_ORDER", gamma[0].AverageRanking.UnavailableReason);
        Assert.Equal(MetricRatio.Of(1, 1), gamma[1].MentionRate);
        Assert.Equal(3, gamma[1].AverageRanking.Value);
    }

    [Fact]
    public void SubjectTrends_KeepsBrandAndAppliesCompetitorSelection()
    {
        var subjects = new SubjectTrendFixture();

        var selected = DashboardAnalyticsProjector.SubjectTrends(subjects.Days, subjects.Configuration,
            [subjects.Beta], subjects.Offsets);
        var brandOnly = DashboardAnalyticsProjector.SubjectTrends(subjects.Days, subjects.Configuration,
            [subjects.Brand], subjects.Offsets);

        Assert.Equal([subjects.Brand, subjects.Beta], selected.Select(t => t.SubjectId));
        Assert.Equal(MetricRatio.Of(2, 2), selected[1].Points[0].MentionRate);
        Assert.Equal([subjects.Brand], brandOnly.Select(t => t.SubjectId));
    }

    [Fact]
    public void CheckBrandMetrics_RanksByFirstOffsetAndScoresTheSingleAnswer()
    {
        var f = new CheckMetricsFixture();

        var first = DashboardAnalyticsProjector.CheckBrandMetrics(f.Fact([f.Brand, f.Beta], ownCitation: true),
            f.Configuration, Offsets((f.Brand, 2), (f.Beta, 9)));
        Assert.Equal(1, first.Ranking.Value);
        Assert.Equal((1L, 1L), (first.Ranking.Numerator, first.Ranking.Denominator));
        Assert.Null(first.Ranking.UnavailableReason);
        Assert.Equal("check-subject-first-offset-v1", first.Ranking.MetricVersion);
        // 40 × 1 + 30 × 1 + 20 × (1/2) + 10 × 1 = 90
        Assert.Equal(90, first.Exposure.Value!.Value, 6);
        Assert.Equal("exposure-v2", first.Exposure.ScoreVersion);
        Assert.Equal((.4, .3, .2, .1), (first.Exposure.MentionRateWeight, first.Exposure.PositionWeight,
            first.Exposure.ShareOfVoiceWeight, first.Exposure.OwnCitationWeight));
        Assert.Null(first.Exposure.EligibilityReason);
        Assert.Equal(MetricRatio.Of(1, 1), first.Exposure.BrandMentionRate);
        Assert.Equal(MetricMean.Of(1, 1), first.Exposure.PositionScore);
        Assert.Equal(MetricRatio.Of(1, 2), first.Exposure.ShareOfVoice);
        Assert.Equal(MetricRatio.Of(1, 1), first.Exposure.OwnExternalCitationRate);

        var second = DashboardAnalyticsProjector.CheckBrandMetrics(f.Fact([f.Brand, f.Beta, f.Gamma]),
            f.Configuration, Offsets((f.Beta, 1), (f.Brand, 7), (f.Gamma, 30)));
        Assert.Equal(2, second.Ranking.Value);
        // 40 × 1 + 30 × (1/2) + 20 × (1/3) + 10 × 0 = 61.67
        Assert.Equal(40 + 15 + 20d / 3, second.Exposure.Value!.Value, 6);
        Assert.Equal(.5, second.Exposure.PositionScore.Value);
        Assert.Equal(MetricRatio.Of(1, 3), second.Exposure.ShareOfVoice);
        Assert.Equal(MetricRatio.Of(0, 1), second.Exposure.OwnExternalCitationRate);
    }

    [Fact]
    public void CheckBrandMetrics_BrandOnlyAnswerRanksFirstUnlikeDashboardAverage()
    {
        var f = new CheckMetricsFixture();
        var fact = f.Fact([f.Brand]);
        var offsets = Offsets((f.Brand, 3));

        var metrics = DashboardAnalyticsProjector.CheckBrandMetrics(fact, f.Configuration, offsets);

        Assert.Equal(1, metrics.Ranking.Value);
        // 40 + 30 × 1 + 20 × 1 + 0 = 90
        Assert.Equal(90, metrics.Exposure.Value!.Value, 6);
        Assert.Equal(MetricRatio.Of(1, 1), metrics.Exposure.ShareOfVoice);
        // 仪表盘平均排名仍跳过只有本品牌的回答（prd D3 只改单条口径，不改 Ranking()）。
        var dashboard = DashboardAnalyticsProjector.Ranking([fact], f.Brand,
            new Dictionary<Guid, IReadOnlyDictionary<Guid, SubjectStats>> { [fact.CheckId] = offsets });
        Assert.Null(dashboard.Value);
        Assert.Equal("INSUFFICIENT_SUBJECT_ORDER", dashboard.UnavailableReason);
    }

    [Theory]
    [InlineData(false, 0.0)]
    [InlineData(true, 10.0)]
    public void CheckBrandMetrics_BrandNotMentionedHasNoRankButScoresZeroOrOwnCitation(bool own, double expected)
    {
        var f = new CheckMetricsFixture();

        var metrics = DashboardAnalyticsProjector.CheckBrandMetrics(f.Fact([f.Beta], own), f.Configuration,
            Stats((f.Beta, 0, 2)));

        Assert.Null(metrics.Ranking.Value);
        Assert.Equal("BRAND_NOT_MENTIONED", metrics.Ranking.UnavailableReason);
        Assert.Equal(expected, metrics.Exposure.Value!.Value, 6);
        Assert.Null(metrics.Exposure.EligibilityReason);
        Assert.Equal(MetricRatio.Of(0, 1), metrics.Exposure.BrandMentionRate);
        // 未提及本品牌时名次分记 0，并计入分母。
        Assert.Equal(MetricMean.Of(0, 1), metrics.Exposure.PositionScore);
        Assert.Equal(MetricRatio.Of(0, 2), metrics.Exposure.ShareOfVoice);
    }

    [Theory]
    [InlineData(false, 0.0)]
    [InlineData(true, 10.0)]
    public void CheckBrandMetrics_NoSubjectMentionedScoresZeroInsteadOfUnavailable(bool own, double expected)
    {
        var f = new CheckMetricsFixture();
        var fact = f.Fact([], own);

        var metrics = DashboardAnalyticsProjector.CheckBrandMetrics(fact, f.Configuration, null);

        Assert.Equal("BRAND_NOT_MENTIONED", metrics.Ranking.UnavailableReason);
        Assert.NotNull(metrics.Exposure.Value);
        Assert.Equal(expected, metrics.Exposure.Value!.Value, 6);
        Assert.Null(metrics.Exposure.EligibilityReason);
        // 声量组成如实为 0/0（prd D4），计分时按 0 计；聚合口径对同样输入也按 0 计分，不再不可用。
        Assert.Equal(MetricRatio.Of(0, 0), metrics.Exposure.ShareOfVoice);
        Assert.Null(metrics.Exposure.ShareOfVoice.Value);
        var aggregate = DashboardAnalyticsProjector.AggregateExposure([fact], f.Configuration,
            new Dictionary<Guid, IReadOnlyDictionary<Guid, SubjectStats>>());
        Assert.Null(aggregate.EligibilityReason);
        Assert.Equal(expected, aggregate.Value!.Value, 6);
    }

    [Fact]
    public void CheckBrandMetrics_V2AcceptanceScenarios()
    {
        var f = new CheckMetricsFixture();

        // 截图场景：3 个主体，本品牌排第 1，出现 3/6 次，无自有引用 → 40 + 30 + 10 + 0 = 80。
        var first = DashboardAnalyticsProjector.CheckBrandMetrics(f.Fact([f.Brand, f.Beta, f.Gamma]),
            f.Configuration, Stats((f.Brand, 0, 3), (f.Beta, 5, 2), (f.Gamma, 9, 1)));
        Assert.Equal(1, first.Ranking.Value);
        Assert.Equal(MetricRatio.Of(3, 6), first.Exposure.ShareOfVoice);
        Assert.Equal(80, first.Exposure.Value!.Value, 6);

        // 排第 3，出现 1/6 次 → 40 + 30 × (1/3) + 20 × (1/6) = 53.33。
        var third = DashboardAnalyticsProjector.CheckBrandMetrics(f.Fact([f.Brand, f.Beta, f.Gamma]),
            f.Configuration, Stats((f.Beta, 0, 3), (f.Gamma, 2, 2), (f.Brand, 9, 1)));
        Assert.Equal(3, third.Ranking.Value);
        Assert.Equal(MetricRatio.Of(1, 6), third.Exposure.ShareOfVoice);
        Assert.Equal(40 + 10 + 20d / 6, third.Exposure.Value!.Value, 6);
        Assert.Equal(53.33, third.Exposure.Value!.Value, 2);

        // 只有本品牌 + 自有引用 → 100。
        var only = DashboardAnalyticsProjector.CheckBrandMetrics(f.Fact([f.Brand], ownCitation: true),
            f.Configuration, Stats((f.Brand, 4, 2)));
        Assert.Equal(100, only.Exposure.Value!.Value, 6);
    }

    [Fact]
    public void CheckBrandMetrics_WithoutCompetitorsExposureIsUnavailableButRankStillComputed()
    {
        var f = new CheckMetricsFixture();
        var brandOnly = new SubjectConfiguration(f.Brand, 1, [new AnalysisSubject(f.Brand, "brand", "Acme")], [], true);

        var metrics = DashboardAnalyticsProjector.CheckBrandMetrics(f.Fact([f.Brand], ownCitation: true), brandOnly,
            Offsets((f.Brand, 0)));

        Assert.Equal(1, metrics.Ranking.Value);
        Assert.Null(metrics.Exposure.Value);
        Assert.Equal("NO_COMPETITOR_BASELINE", metrics.Exposure.EligibilityReason);
        var empty = DashboardAnalyticsProjector.CheckBrandMetrics(f.Fact([]), brandOnly, null);
        Assert.Null(empty.Exposure.Value);
        Assert.Equal("NO_COMPETITOR_BASELINE", empty.Exposure.EligibilityReason);
    }

    [Fact]
    public void CheckBrandMetrics_MissingBrandOffsetMakesRankAndExposureUnavailable()
    {
        var f = new CheckMetricsFixture();
        var fact = f.Fact([f.Brand, f.Beta]);

        foreach (var offsets in new[] { null, Offsets((f.Beta, 1)) })
        {
            var metrics = DashboardAnalyticsProjector.CheckBrandMetrics(fact, f.Configuration, offsets);
            Assert.Null(metrics.Ranking.Value);
            Assert.Equal("SUBJECT_ORDER_UNAVAILABLE", metrics.Ranking.UnavailableReason);
            // exposure-v2 不再猜测名次：已提及但缺少出现位置时曝光分不可用。
            Assert.Null(metrics.Exposure.Value);
            Assert.Equal("SUBJECT_ORDER_UNAVAILABLE", metrics.Exposure.EligibilityReason);
            Assert.Equal(MetricMean.Of(0, 0), metrics.Exposure.PositionScore);
            Assert.Null(metrics.Exposure.PositionScore.Value);
        }
    }

    [Fact]
    public void AggregateExposure_MergesComponentsByCountsAndOccurrences()
    {
        var f = new CheckMetricsFixture();
        var first = f.Fact([f.Brand, f.Beta], ownCitation: true);
        var second = f.Fact([f.Brand, f.Beta]);
        var notMentioned = f.Fact([f.Beta]);
        var withoutOffset = f.Fact([f.Brand]);
        var failed = f.Fact([f.Brand]) with { Status = "failed" };
        var stats = new Dictionary<Guid, IReadOnlyDictionary<Guid, SubjectStats>>
        {
            [first.CheckId] = Stats((f.Brand, 0, 3), (f.Beta, 5, 3)),
            [second.CheckId] = Stats((f.Beta, 0, 1), (f.Brand, 5, 1)),
            [notMentioned.CheckId] = Stats((f.Beta, 0, 2)),
            [failed.CheckId] = Stats((f.Brand, 0, 9))
        };

        var score = DashboardAnalyticsProjector.AggregateExposure(
            [first, second, notMentioned, withoutOffset, failed], f.Configuration, stats);

        // 失败的回答不计；4 条合格回答中 3 条提及本品牌，1 条有自有引用。
        Assert.Equal(MetricRatio.Of(3, 4), score.BrandMentionRate);
        Assert.Equal(MetricRatio.Of(1, 4), score.OwnExternalCitationRate);
        // 名次分 = (1 + 1/2 + 0) / 3；已提及但缺少位置的回答不计入分母。
        Assert.Equal(1.5, score.PositionScore.Total, 9);
        Assert.Equal(3, score.PositionScore.Count);
        Assert.Equal(.5, score.PositionScore.Value!.Value, 9);
        // 声量按出现次数合并：(3 + 1 + 0 + 1) / (6 + 2 + 2 + 1)；缺少统计的已提及主体按 1 次计。
        Assert.Equal(MetricRatio.Of(5, 11), score.ShareOfVoice);
        Assert.Equal(40 * .75 + 30 * .5 + 20 * 5d / 11 + 10 * .25, score.Value!.Value, 6);
        Assert.Null(score.EligibilityReason);
        Assert.Equal("exposure-v2", score.ScoreVersion);

        var empty = DashboardAnalyticsProjector.AggregateExposure([failed], f.Configuration, stats);
        Assert.Null(empty.Value);
        Assert.Equal("ZERO_REQUIRED_DENOMINATOR", empty.EligibilityReason);
        var unordered = DashboardAnalyticsProjector.AggregateExposure([withoutOffset], f.Configuration, stats);
        Assert.Null(unordered.Value);
        Assert.Equal("SUBJECT_ORDER_UNAVAILABLE", unordered.EligibilityReason);
    }

    [Fact]
    public void PlacementRates_CountFirstAndTopThreeOverEligibleChecks()
    {
        var f = new CheckMetricsFixture();
        var delta = Guid.NewGuid();
        var configuration = f.Configuration with
        {
            Subjects = [.. f.Configuration.Subjects, new AnalysisSubject(delta, "competitor", "Delta")]
        };
        var first = f.Fact([f.Brand, f.Beta]);
        var brandOnly = f.Fact([f.Brand]);
        var third = f.Fact([f.Brand, f.Beta, f.Gamma]);
        var fourth = f.Fact([f.Brand, f.Beta, f.Gamma, delta]);
        var notMentioned = f.Fact([f.Beta]);
        var withoutOffset = f.Fact([f.Brand, f.Beta]);
        var failed = f.Fact([f.Brand]) with { Status = "failed" };
        var stats = new Dictionary<Guid, IReadOnlyDictionary<Guid, SubjectStats>>
        {
            [first.CheckId] = Offsets((f.Brand, 0), (f.Beta, 5)),
            [brandOnly.CheckId] = Offsets((f.Brand, 0)),
            [third.CheckId] = Offsets((f.Beta, 0), (f.Gamma, 3), (f.Brand, 9)),
            [fourth.CheckId] = Offsets((f.Beta, 0), (f.Gamma, 3), (delta, 6), (f.Brand, 9)),
            [notMentioned.CheckId] = Offsets((f.Beta, 0)),
            [withoutOffset.CheckId] = Offsets((f.Beta, 0)),
            [failed.CheckId] = Offsets((f.Brand, 0))
        };

        var (firstRate, topThree) = DashboardAnalyticsProjector.PlacementRates(
            [first, brandOnly, third, fourth, notMentioned, withoutOffset, failed], configuration, stats);

        // 失败的回答不计；6 条合格回答。只提及本品牌记第 1 名；缺少本品牌位置的回答只计入分母。
        Assert.Equal(MetricRatio.Of(2, 6), firstRate);
        Assert.Equal(MetricRatio.Of(3, 6), topThree);

        var (emptyFirst, emptyTopThree) = DashboardAnalyticsProjector.PlacementRates([failed], configuration, stats);
        Assert.Equal(MetricRatio.Of(0, 0), emptyFirst);
        Assert.Null(emptyFirst.Value);
        Assert.Null(emptyTopThree.Value);
    }

    [Theory]
    [InlineData("failed", true, "completed", false, "CHECK_NOT_SUCCEEDED")]
    [InlineData("pending", true, "completed", false, "CHECK_NOT_SUCCEEDED")]
    [InlineData("succeeded", false, "completed", false, "NO_ANSWER")]
    [InlineData("succeeded", true, "pending", false, "ANALYSIS_NOT_READY")]
    [InlineData("succeeded", true, "failed", false, "ANALYSIS_NOT_READY")]
    [InlineData("succeeded", true, "completed", true, "CONFIGURATION_MISSING")]
    public void CheckBrandMetrics_IneligibleChecksCarryTheSameReasonOnBothMetrics(string status, bool answerPresent,
        string analysisStatus, bool configurationMissing, string reason)
    {
        var f = new CheckMetricsFixture();
        var fact = f.Fact([f.Brand, f.Beta], ownCitation: true) with
        {
            Status = status, AnswerPresent = answerPresent, AnalysisStatus = analysisStatus,
            ConfigurationMissing = configurationMissing
        };

        var metrics = DashboardAnalyticsProjector.CheckBrandMetrics(fact, f.Configuration,
            Offsets((f.Brand, 0), (f.Beta, 5)));

        Assert.Null(metrics.Ranking.Value);
        Assert.Equal(reason, metrics.Ranking.UnavailableReason);
        Assert.Equal("check-subject-first-offset-v1", metrics.Ranking.MetricVersion);
        Assert.Null(metrics.Exposure.Value);
        Assert.Equal(reason, metrics.Exposure.EligibilityReason);
        Assert.Equal(metrics, DashboardAnalyticsProjector.UnavailableCheckBrandMetrics(reason));
    }

    [Fact]
    public void CheckBrandMetrics_IgnoresMentionsOfSubjectsOutsideTheFrozenConfiguration()
    {
        var f = new CheckMetricsFixture();
        var stranger = Guid.NewGuid();

        var metrics = DashboardAnalyticsProjector.CheckBrandMetrics(f.Fact([stranger, f.Brand]), f.Configuration,
            Stats((stranger, 0, 5), (f.Brand, 8, 1)));

        Assert.Equal(1, metrics.Ranking.Value);
        Assert.Equal(MetricRatio.Of(1, 1), metrics.Exposure.ShareOfVoice);
        Assert.Equal(90, metrics.Exposure.Value!.Value, 6);
    }

    [Fact]
    public void Groups_CompareThreeMetricsWithPreviousWindowSamePromptGroup()
    {
        var f = new GroupFixture();
        var pricing = f.Group("Pricing", f.T0);
        var first = f.Prompt(pricing);
        var second = f.Prompt(pricing);
        var current = new[]
        {
            f.Fact(first, [f.Brand, f.Beta], (f.Brand, 1), (f.Beta, 5)),
            f.Fact(second, [f.Beta], (f.Beta, 2))
        };
        var previous = new[] { f.Fact(first, [f.Beta, f.Brand], (f.Beta, 1), (f.Brand, 9)) };

        var group = Assert.Single(f.Groups(current, previous));
        var before = Assert.Single(f.Groups(previous, []));

        Assert.Equal(pricing, group.GroupId);
        Assert.Equal("Pricing", group.Name);
        // Both prompts of the group contribute to one row.
        Assert.Equal(new[] { first, second }.Order(), group.PromptIds.Order());
        Assert.Equal(MetricRatio.Of(1, 2), group.BrandMentionRate);
        // Same KPI convention: (current - previous) x 100 percentage points.
        Assert.Equal(-50, group.PreviousPercentagePointDifference!.Value, 9);
        // Rank 1 now versus 2 before: negative means the brand moved up.
        Assert.Equal(1, group.AverageRanking.Value);
        Assert.Equal(-1, group.PreviousAverageRankingDifference!.Value, 9);
        Assert.NotNull(group.Exposure.Value);
        Assert.NotNull(before.Exposure.Value);
        Assert.Equal(group.Exposure.Value!.Value - before.Exposure.Value!.Value, group.PreviousExposureDifference!.Value, 9);
        // Previous-window group itself has no earlier window, so every comparison stays unavailable.
        Assert.Null(before.PreviousPercentagePointDifference);
        Assert.Null(before.PreviousAverageRankingDifference);
        Assert.Null(before.PreviousExposureDifference);
    }

    [Fact]
    public void Groups_GroupAbsentInPreviousWindowReturnsNullNotZero()
    {
        var f = new GroupFixture();
        var launch = f.Prompt(f.Group("Launch", f.T0));
        var pricing = f.Prompt(f.Group("Pricing", f.T0.AddMinutes(1)));
        var current = new[]
        {
            f.Fact(launch, [f.Brand, f.Beta], (f.Brand, 1), (f.Beta, 5)),
            f.Fact(pricing, [f.Brand, f.Beta], (f.Brand, 1), (f.Beta, 5))
        };
        var previous = new[] { f.Fact(pricing, [f.Brand, f.Beta], (f.Brand, 1), (f.Beta, 5)) };

        var groups = f.Groups(current, previous);

        var launchGroup = Assert.Single(groups, g => g.Name == "Launch");
        Assert.Null(launchGroup.PreviousPercentagePointDifference);
        Assert.Null(launchGroup.PreviousAverageRankingDifference);
        Assert.Null(launchGroup.PreviousExposureDifference);
        var pricingGroup = Assert.Single(groups, g => g.Name == "Pricing");
        // Unchanged values are a real 0, distinct from the unavailable null above.
        Assert.Equal(0, pricingGroup.PreviousPercentagePointDifference);
        Assert.Equal(0, pricingGroup.PreviousAverageRankingDifference);
        Assert.Equal(0, pricingGroup.PreviousExposureDifference);
    }

    [Fact]
    public void Groups_PreviousZeroDenominatorMakesAllDifferencesNull()
    {
        var f = new GroupFixture();
        var prompt = f.Prompt(f.Group("Pricing", f.T0));
        var current = new[] { f.Fact(prompt, [f.Brand, f.Beta], (f.Brand, 1), (f.Beta, 5)) };
        var previous = new[] { f.Fact(prompt, [f.Brand], "failed") };

        var group = Assert.Single(f.Groups(current, previous));

        Assert.Null(group.PreviousPercentagePointDifference);
        Assert.Null(group.PreviousAverageRankingDifference);
        Assert.Null(group.PreviousExposureDifference);
    }

    [Fact]
    public void Groups_UnavailableRankingOnlyNullsTheRankingDifference()
    {
        var f = new GroupFixture();
        var prompt = f.Prompt(f.Group("Pricing", f.T0));
        // Brand-only answer now: the dashboard average ranking needs two ordered subjects.
        var current = new[] { f.Fact(prompt, [f.Brand], (f.Brand, 1)) };
        var previous = new[] { f.Fact(prompt, [f.Brand, f.Beta], (f.Brand, 1), (f.Beta, 5)) };

        var group = Assert.Single(f.Groups(current, previous));

        Assert.Equal("INSUFFICIENT_SUBJECT_ORDER", group.AverageRanking.UnavailableReason);
        Assert.Null(group.PreviousAverageRankingDifference);
        Assert.Equal(0, group.PreviousPercentagePointDifference);
        Assert.NotNull(group.PreviousExposureDifference);
    }

    [Fact]
    public void Groups_UngroupedAndUnknownPromptsShareOneUngroupedBucketMatchedNullToNull()
    {
        var f = new GroupFixture();
        var grouped = f.Prompt(f.Group("Pricing", f.T0));
        var ungrouped = f.Prompt(null);
        // A prompt missing from the map (e.g. deleted since capture) also counts as ungrouped.
        var unknown = Guid.NewGuid();
        var current = new[]
        {
            f.Fact(ungrouped, [f.Brand, f.Beta], (f.Brand, 1), (f.Beta, 5)),
            f.Fact(unknown, [f.Beta], (f.Beta, 1)),
            f.Fact(grouped, [f.Brand, f.Beta], (f.Brand, 1), (f.Beta, 5))
        };
        var previous = new[] { f.Fact(unknown, [f.Brand, f.Beta], (f.Brand, 1), (f.Beta, 5)) };

        var groups = f.Groups(current, previous);

        Assert.Equal(2, groups.Count);
        var bucket = groups[^1];
        Assert.Null(bucket.GroupId);
        Assert.Equal(DashboardAnalyticsProjector.UngroupedName, bucket.Name);
        Assert.Equal("未分组", bucket.Name);
        Assert.Equal(new[] { ungrouped, unknown }.Order(), bucket.PromptIds.Order());
        Assert.Equal(MetricRatio.Of(1, 2), bucket.BrandMentionRate);
        // Previous ungrouped bucket (1/1) is matched by the null key: (0.5 - 1) x 100.
        Assert.Equal(-50, bucket.PreviousPercentagePointDifference!.Value, 9);
        Assert.Equal(0, bucket.PreviousAverageRankingDifference);
        // The grouped prompt had no previous facts, so its comparison stays unavailable.
        Assert.Null(groups[0].PreviousPercentagePointDifference);
    }

    [Fact]
    public void Groups_OrderByGroupCreatedAtThenIdUngroupedLastAndOmitGroupsWithoutCurrentFacts()
    {
        var f = new GroupFixture();
        var later = f.Prompt(f.Group("Later", f.T0.AddDays(1)));
        var secondId = Guid.Parse("20000000-0000-0000-0000-000000000000");
        var firstId = Guid.Parse("10000000-0000-0000-0000-000000000000");
        var tieB = f.Prompt(f.Group("Tie B", f.T0, secondId));
        var tieA = f.Prompt(f.Group("Tie A", f.T0, firstId));
        var quiet = f.Prompt(f.Group("Quiet", f.T0.AddDays(-1)));
        var ungrouped = f.Prompt(null);
        var current = new[]
        {
            f.Fact(ungrouped, [f.Brand]), f.Fact(later, [f.Brand]), f.Fact(tieB, [f.Brand]), f.Fact(tieA, [f.Brand])
        };
        // The earliest group only has previous-window facts: it is not returned.
        var previous = new[] { f.Fact(quiet, [f.Brand]) };

        var groups = f.Groups(current, previous);

        Assert.Equal(["Tie A", "Tie B", "Later", "未分组"], groups.Select(g => g.Name));
        Assert.Equal(firstId, groups[0].GroupId);
        Assert.Equal(secondId, groups[1].GroupId);
        Assert.Empty(f.Groups([], previous));
    }

    [Fact]
    public void Groups_MatchPreviousWindowByGroupIdNotByNameOrCapturedTags()
    {
        var f = new GroupFixture();
        // The map holds current names; a renamed group can share its display name with another group,
        // and old facts still carry capture-time tags. Only the group id decides the match.
        var renamed = f.Group("Pricing", f.T0);
        var other = f.Group("Pricing", f.T0.AddMinutes(1));
        var renamedPrompt = f.Prompt(renamed);
        var otherPrompt = f.Prompt(other);
        var current = new[]
        {
            f.Fact(renamedPrompt, [f.Brand, f.Beta], (f.Brand, 1), (f.Beta, 5)),
            f.Fact(otherPrompt, [f.Beta], (f.Beta, 1))
        };
        var previous = new[]
        {
            f.Fact(renamedPrompt, [f.Beta], "succeeded", ["Old name"], (f.Beta, 1)),
            f.Fact(otherPrompt, [f.Brand, f.Beta], "succeeded", ["Pricing"], (f.Brand, 1), (f.Beta, 5))
        };

        var groups = f.Groups(current, previous);

        Assert.Equal([renamed, other], groups.Select(g => g.GroupId));
        Assert.Equal(100, groups[0].PreviousPercentagePointDifference!.Value, 9);
        Assert.Equal(-100, groups[1].PreviousPercentagePointDifference!.Value, 9);
    }

    [Fact]
    public void PromptGroupMapSql_IsParameterizedAndScopesTenantThroughSites()
    {
        var sql = Jogeoai.Infrastructure.AnalyticsQueryStore.PromptGroupMapSql;

        Assert.Contains("UUID_TO_BIN(@tenant)", sql);
        Assert.Contains("UUID_TO_BIN(@site)", sql);
        Assert.Contains("JOIN sites s ON s.id = p.site_id AND s.tenant_id = UUID_TO_BIN(@tenant)", sql);
        Assert.Contains("LEFT JOIN prompt_groups g ON g.site_id = p.site_id AND g.id = p.group_id", sql);
        // prompts has no tenant_id column.
        Assert.DoesNotContain("p.tenant_id", sql);
    }

    [Fact]
    public void SubjectStatsSql_IsReleaseScopedBehindTheAnalysisWatermarkAndParameterized()
    {
        var template = Jogeoai.Infrastructure.AnalyticsQueryStore.SubjectStatsSql;
        var whole = string.Format(template, "");

        Assert.Contains("MIN(e.offset_start), COUNT(*)", whole);
        Assert.Contains("JOIN check_analysis a\n  ON a.release_id = e.release_id AND a.check_id = e.check_id",
            whole.ReplaceLineEndings("\n"));
        Assert.Contains("WHERE e.release_id = UUID_TO_BIN(@release) AND a.completed_event_id <= @watermark", whole);
        Assert.Contains("GROUP BY e.check_id, e.subject_id", whole);
        // The only placeholder is the optional page scope; it is filled with bound parameters, never values.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(template, @"\{\d+\}"));
        var paged = string.Format(template, " AND e.check_id IN (UUID_TO_BIN(@id0),UUID_TO_BIN(@id1))");
        Assert.Contains("@watermark AND e.check_id IN (UUID_TO_BIN(@id0),UUID_TO_BIN(@id1))\nGROUP BY",
            paged.ReplaceLineEndings("\n"));
    }

    private sealed class GroupFixture
    {
        private readonly Dictionary<Guid, IReadOnlyDictionary<Guid, SubjectStats>> offsets = new();
        private readonly Dictionary<Guid, PromptGroupAssignment> prompts = new();
        private readonly Dictionary<Guid, PromptGroupAssignment> groups = new();
        public Guid Brand { get; } = Guid.NewGuid();
        public Guid Beta { get; } = Guid.NewGuid();
        public DateTimeOffset T0 { get; } = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        public SubjectConfiguration Configuration { get; }

        public GroupFixture()
            => Configuration = new SubjectConfiguration(Brand, 1,
                [new AnalysisSubject(Brand, "brand", "Acme"), new AnalysisSubject(Beta, "competitor", "Beta")], [], true);

        public Guid Group(string name, DateTimeOffset createdAt, Guid? id = null)
        {
            var groupId = id ?? Guid.NewGuid();
            groups[groupId] = new PromptGroupAssignment(groupId, name, createdAt);
            return groupId;
        }

        /// <summary>Registers a prompt in the map; null means the prompt exists but is ungrouped.</summary>
        public Guid Prompt(Guid? groupId)
        {
            var id = Guid.NewGuid();
            prompts[id] = groupId is null ? new PromptGroupAssignment(null, null, null) : groups[groupId.Value];
            return id;
        }

        public IReadOnlyList<BrandDashboardGroup> Groups(IReadOnlyList<AnalyticsCheckFact> current,
            IReadOnlyList<AnalyticsCheckFact> previous)
            => DashboardAnalyticsProjector.Groups(current, previous, Configuration, offsets, prompts);

        public AnalyticsCheckFact Fact(Guid promptId, IReadOnlyList<Guid> mentioned,
            params (Guid Subject, int Offset)[] subjectOffsets)
            => Fact(promptId, mentioned, "succeeded", [], subjectOffsets);

        public AnalyticsCheckFact Fact(Guid promptId, IReadOnlyList<Guid> mentioned, string status,
            params (Guid Subject, int Offset)[] subjectOffsets)
            => Fact(promptId, mentioned, status, [], subjectOffsets);

        public AnalyticsCheckFact Fact(Guid promptId, IReadOnlyList<Guid> mentioned, string status,
            IReadOnlyList<string> tags, params (Guid Subject, int Offset)[] subjectOffsets)
        {
            var fact = new AnalyticsCheckFact(Guid.NewGuid(), Guid.NewGuid(), promptId, null, null, 1,
                DateTimeOffset.UtcNow, status, true, "completed", false, false, false, mentioned, "cloro",
                "chatgpt", "US", "en-US", "scraped", "observed_surface", tags, null);
            if (subjectOffsets.Length > 0)
                offsets[fact.CheckId] = subjectOffsets.ToDictionary(o => o.Subject, o => new SubjectStats(o.Offset, 1));
            return fact;
        }
    }

    private sealed class CheckMetricsFixture
    {
        public Guid Brand { get; } = Guid.NewGuid();
        public Guid Beta { get; } = Guid.NewGuid();
        public Guid Gamma { get; } = Guid.NewGuid();
        public SubjectConfiguration Configuration { get; }

        public CheckMetricsFixture()
            => Configuration = new SubjectConfiguration(Brand, 1,
                [new AnalysisSubject(Brand, "brand", "Acme"), new AnalysisSubject(Beta, "competitor", "Beta"),
                    new AnalysisSubject(Gamma, "competitor", "Gamma")], [], true);

        public AnalyticsCheckFact Fact(IReadOnlyList<Guid> mentioned, bool ownCitation = false)
            => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, null, 1, DateTimeOffset.UtcNow,
                "succeeded", true, "completed", false, false, ownCitation, mentioned, "cloro", "chatgpt", "US",
                "en-US", "scraped", "observed_surface", [], null);
    }

    private sealed class SubjectTrendFixture
    {
        public Guid Brand { get; } = Guid.NewGuid();
        public Guid Beta { get; } = Guid.NewGuid();
        public Guid Gamma { get; } = Guid.NewGuid();
        public DateOnly FirstDay { get; } = new(2026, 10, 1);
        public DateOnly ThirdDay { get; } = new(2026, 10, 3);
        public SubjectConfiguration Configuration { get; }
        public IReadOnlyList<(DateOnly Date, IReadOnlyList<AnalyticsCheckFact> Facts)> Days { get; }
        public IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, SubjectStats>> Offsets { get; }

        public SubjectTrendFixture()
        {
            Configuration = new SubjectConfiguration(Brand, 1,
                [new AnalysisSubject(Gamma, "competitor", "Gamma"), new AnalysisSubject(Brand, "brand", "Acme"),
                    new AnalysisSubject(Beta, "competitor", "Beta")], [], true);
            var secondDay = new DateOnly(2026, 10, 2);
            var brandAndBeta = Fact(FirstDay, [Brand, Beta]);
            var betaOnly = Fact(FirstDay, [Beta]);
            var failed = Fact(FirstDay, [Brand], status: "failed");
            var pending = Fact(secondDay, [Brand, Beta], analysisStatus: "pending");
            var everyone = Fact(ThirdDay, [Brand, Beta, Gamma]);
            Days =
            [
                (FirstDay, new[] { brandAndBeta, betaOnly, failed }),
                (secondDay, new[] { pending }),
                (ThirdDay, new[] { everyone })
            ];
            Offsets = new Dictionary<Guid, IReadOnlyDictionary<Guid, SubjectStats>>
            {
                [brandAndBeta.CheckId] = DashboardAnalyticsTests.Offsets((Beta, 4), (Brand, 10)),
                [pending.CheckId] = DashboardAnalyticsTests.Offsets((Brand, 1), (Beta, 2)),
                [everyone.CheckId] = DashboardAnalyticsTests.Offsets((Brand, 1), (Beta, 5), (Gamma, 9))
            };
        }

        private static AnalyticsCheckFact Fact(DateOnly date, IReadOnlyList<Guid> mentioned,
            string status = "succeeded", string analysisStatus = "completed")
            => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, null, 1,
                new DateTimeOffset(date.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero), status, true,
                analysisStatus, false, false, false, mentioned, "cloro", "chatgpt", "US", "en-US", "scraped",
                "observed_surface", [], null);
    }
}
