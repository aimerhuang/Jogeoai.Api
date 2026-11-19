namespace Jogeoai.Domain;

/// <summary>监控台和品牌概览共用的纯函数投影。</summary>
public static class DashboardAnalyticsProjector
{
    /// <summary>计算百分位；空样本返回 null。</summary>
    public static double? Percentile(IEnumerable<double> values, double percentile)
    {
        var ordered = values.Order().ToArray();
        if (ordered.Length == 0) return null;
        var position = (ordered.Length - 1) * percentile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        return lower == upper ? ordered[lower] : ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower);
    }

    /// <summary>计算采集耗时统计。</summary>
    public static LatencySummary Latency(IEnumerable<AnalyticsCheckFact> facts)
    {
        var values = facts.Where(f => f.Status == "succeeded" && f.DurationMs is not null)
            .Select(f => (double)f.DurationMs!.Value).ToArray();
        return new LatencySummary(values.Length == 0 ? null : values.Average(),
            Percentile(values, .5), Percentile(values, .95), values.LongLength);
    }

    /// <summary>按主体首次出现偏移计算品牌平均排名。</summary>
    /// <param name="facts">事实。</param>
    /// <param name="brandId">参与排名的主体。</param>
    /// <param name="stats">按 Check 的主体统计（首次偏移与出现次数）。</param>
    public static RankingMetric Ranking(IReadOnlyList<AnalyticsCheckFact> facts,
        Guid brandId, IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, SubjectStats>> stats)
    {
        var ranks = new List<double>();
        foreach (var fact in facts.Where(f => f.Status == "succeeded" && f.AnswerPresent
            && f.AnalysisStatus == "completed" && f.MentionedSubjects.Contains(brandId)))
        {
            if (!stats.TryGetValue(fact.CheckId, out var values)) continue;
            var distinct = values.Where(v => v.Key != brandId).ToArray();
            if (distinct.Length == 0) continue;
            if (!values.TryGetValue(brandId, out var brandStats)) continue;
            var before = distinct.Count(v => v.Value.FirstOffset < brandStats.FirstOffset);
            ranks.Add(before + 1);
        }
        return ranks.Count == 0
            ? new RankingMetric(null, 0, 0, "INSUFFICIENT_SUBJECT_ORDER")
            : new RankingMetric(ranks.Average(), ranks.Count, ranks.Count, null);
    }

    /// <summary>
    /// 由已按当地日期分好的事实计算主体逐日序列：本品牌在前，竞品按配置顺序并与竞品比较使用同一选择规则；
    /// 仅输出当日有效检测数大于 0 的日期，提及率分母为当日有效检测数。
    /// </summary>
    public static IReadOnlyList<BrandDashboardSubjectTrend> SubjectTrends(
        IReadOnlyList<(DateOnly Date, IReadOnlyList<AnalyticsCheckFact> Facts)> days,
        SubjectConfiguration configuration, IReadOnlyCollection<Guid> selectedSubjects,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, SubjectStats>> stats)
    {
        var subjects = configuration.Subjects.Where(s => s.Type == "brand")
            .Concat(configuration.Subjects.Where(s => s.Type != "brand"
                && (selectedSubjects.Count == 0 || selectedSubjects.Contains(s.Id))))
            .ToArray();
        var points = subjects.ToDictionary(s => s.Id, _ => new List<BrandDashboardSubjectTrendPoint>());
        foreach (var (date, facts) in days.OrderBy(d => d.Date))
        {
            var projection = AnalyticsProjector.Compute(facts, configuration);
            var eligible = projection.Metrics.EligibleChecks;
            if (eligible == 0) continue;
            var mentions = projection.Entries.ToDictionary(e => e.SubjectId, e => e.Mentions);
            foreach (var subject in subjects)
                points[subject.Id].Add(new BrandDashboardSubjectTrendPoint(date,
                    MetricRatio.Of(mentions[subject.Id], eligible), Ranking(facts, subject.Id, stats), eligible));
        }
        return subjects.Select(s => new BrandDashboardSubjectTrend(s.Id, s.Name, s.Type == "brand",
            points[s.Id])).ToArray();
    }

    /// <summary>未分组提问所在分组的名称。</summary>
    public const string UngroupedName = "未分组";

    /// <summary>
    /// 按提问当前所属的提问分组计算品牌概览分组，并与前一等长窗口的同一分组（按分组标识匹配，「未分组」对「未分组」）比较三项指标。
    /// 两个窗口使用同一份「提问→分组」映射和同一纯函数计算提及率、平均排名与曝光分数；任一侧值不可用或前期没有该分组的数据时差值为空，不写成 0。
    /// 只返回当前窗口有事实的分组，按分组创建时间、分组标识排序，「未分组」排在最后。
    /// </summary>
    /// <param name="facts">当前窗口事实。</param>
    /// <param name="previousFacts">前一等长窗口、相同筛选的事实。</param>
    /// <param name="configuration">快照冻结的主体配置。</param>
    /// <param name="stats">按 Check 的主体统计（首次偏移与出现次数）；需覆盖两个窗口的 Check。</param>
    /// <param name="promptGroups">提问标识到其当前所属分组的映射；缺少的提问（如已删除）或未分组的提问归入「未分组」。</param>
    public static IReadOnlyList<BrandDashboardGroup> Groups(IReadOnlyList<AnalyticsCheckFact> facts,
        IReadOnlyList<AnalyticsCheckFact> previousFacts, SubjectConfiguration configuration,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, SubjectStats>> stats,
        IReadOnlyDictionary<Guid, PromptGroupAssignment> promptGroups)
    {
        var current = GroupMetrics(facts, configuration, stats, promptGroups);
        var previous = GroupMetrics(previousFacts, configuration, stats, promptGroups);
        return current.Select(group =>
        {
            var before = previous.FirstOrDefault(p => p.GroupId == group.GroupId);
            return new BrandDashboardGroup(group.Name, group.Visibility, group.Ranking, group.Exposure,
                group.EligibleChecks,
                Difference(group.Visibility.Value, before?.Visibility.Value) * 100,
                Difference(group.Ranking.Value, before?.Ranking.Value),
                Difference(group.Exposure.Value, before?.Exposure.Value),
                group.PromptIds, group.CheckIds, group.GroupId);
        }).ToArray();
    }

    private sealed record GroupMetric(Guid? GroupId, string Name, DateTimeOffset? CreatedAt, MetricRatio Visibility,
        RankingMetric Ranking, ExposureScore Exposure, long EligibleChecks, IReadOnlyList<Guid> PromptIds,
        IReadOnlyList<Guid> CheckIds);

    private static PromptGroupAssignment GroupOf(Guid promptId,
        IReadOnlyDictionary<Guid, PromptGroupAssignment> promptGroups)
        => promptGroups.TryGetValue(promptId, out var assignment) && assignment.GroupId is not null
            ? assignment : new PromptGroupAssignment(null, null, null);

    private static IReadOnlyList<GroupMetric> GroupMetrics(IReadOnlyList<AnalyticsCheckFact> facts,
        SubjectConfiguration configuration, IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, SubjectStats>> stats,
        IReadOnlyDictionary<Guid, PromptGroupAssignment> promptGroups)
    {
        return facts.Select(f => (group: GroupOf(f.PromptId, promptGroups), f))
            .GroupBy(x => x.group.GroupId).Select(group =>
            {
                var assignment = group.First().group;
                var groupFacts = group.Select(x => x.f).DistinctBy(f => f.CheckId).ToArray();
                var metrics = AnalyticsProjector.Compute(groupFacts, configuration).Metrics;
                return new GroupMetric(group.Key,
                    group.Key is null ? UngroupedName : assignment.Name ?? "", assignment.CreatedAt,
                    metrics.Visibility, Ranking(groupFacts, configuration.BrandId, stats),
                    AggregateExposure(groupFacts, configuration, stats),
                    metrics.EligibleChecks, groupFacts.Select(f => f.PromptId).Distinct().ToArray(),
                    groupFacts.Select(f => f.CheckId).Distinct().ToArray());
            })
            // 与「用户提问」页一致：created_at、id；Guid 的 "D" 字符串序与 MySQL BINARY(16)（UUID_TO_BIN 无交换）字节序一致。
            .OrderBy(g => g.GroupId is null)
            .ThenBy(g => g.CreatedAt ?? DateTimeOffset.MaxValue)
            .ThenBy(g => g.GroupId?.ToString("D"), StringComparer.Ordinal).ToArray();
    }

    private static double? Difference(double? current, double? previous)
        => current is null || previous is null ? null : current.Value - previous.Value;

    /// <summary>曝光分数版本。exposure-v1（2026-10-22 之前）为 .5 提及 / .3 按回答数的声量 / .2 自有引用，不含名次。</summary>
    public const string ExposureVersion = "exposure-v2";
    /// <summary>曝光分中提及率的权重。</summary>
    public const double ExposureMentionWeight = .4;
    /// <summary>曝光分中名次分的权重。</summary>
    public const double ExposurePositionWeight = .3;
    /// <summary>曝光分中声量（按出现次数）的权重。</summary>
    public const double ExposureShareOfVoiceWeight = .2;
    /// <summary>曝光分中自有引用率的权重。</summary>
    public const double ExposureOwnCitationWeight = .1;
    private const string CheckRankingVersion = "check-subject-first-offset-v1";

    /// <summary>单条回答中用于曝光分的组成。Rank 为空表示未提及本品牌或缺少本品牌的出现位置。</summary>
    private readonly record struct CheckParts(bool BrandMentioned, int? Rank, long BrandOccurrences,
        long SubjectOccurrences, bool OwnCitation);

    private static bool IsEligible(AnalyticsCheckFact fact)
        => fact.Status == "succeeded" && fact.AnswerPresent && fact.AnalysisStatus == "completed"
            && !fact.ConfigurationMissing;

    /// <summary>
    /// 只计冻结配置中的主体：名次 = 首次出现早于本品牌的已提及主体数 + 1（只有本品牌时为 1）；
    /// 出现次数取证据条数，已提及主体缺少统计时按 1 次计（与“已提及”一致，避免分母偏小）。
    /// </summary>
    private static CheckParts Parts(AnalyticsCheckFact fact, Guid brandId, IReadOnlySet<Guid> configured,
        IReadOnlyDictionary<Guid, SubjectStats>? stats)
    {
        var mentioned = fact.MentionedSubjects.Where(configured.Contains).Distinct().ToArray();
        var brandMentioned = mentioned.Contains(brandId);
        int? rank = null;
        if (brandMentioned && stats is not null && stats.TryGetValue(brandId, out var brandStats))
            rank = mentioned.Count(id => id != brandId && stats.TryGetValue(id, out var other)
                && other.FirstOffset < brandStats.FirstOffset) + 1;
        return new CheckParts(brandMentioned, rank, brandMentioned ? Occurrences(stats, brandId) : 0,
            mentioned.Sum(id => (long)Occurrences(stats, id)), fact.OwnCitation);
    }

    private static int Occurrences(IReadOnlyDictionary<Guid, SubjectStats>? stats, Guid subjectId)
        => stats is not null && stats.TryGetValue(subjectId, out var value) && value.Occurrences > 0 ? value.Occurrences : 1;

    /// <summary>名次分：未提及记 0；已提及且有名次记 1 ÷ 名次；已提及但缺少出现位置时无法确定（null）。</summary>
    private static double? PositionOf(CheckParts parts)
        => !parts.BrandMentioned ? 0 : parts.Rank is int rank ? 1d / rank : null;

    /// <summary>按 exposure-v2 权重合成；有原因时分数为空。声量分母为 0 时按 0 计分，组成如实返回 0/0。</summary>
    private static ExposureScore Score(MetricRatio mentionRate, MetricMean position, MetricRatio shareOfVoice,
        MetricRatio ownCitationRate, string? reason)
    {
        double? value = reason is null
            ? (ExposureMentionWeight * mentionRate.Value!.Value + ExposurePositionWeight * position.Value!.Value
                + ExposureShareOfVoiceWeight * (shareOfVoice.Value ?? 0)
                + ExposureOwnCitationWeight * ownCitationRate.Value!.Value) * 100
            : null;
        return new ExposureScore(value, ExposureVersion, ExposureMentionWeight, ExposurePositionWeight,
            ExposureShareOfVoiceWeight, ExposureOwnCitationWeight, mentionRate, position, shareOfVoice,
            ownCitationRate, reason);
    }

    /// <summary>
    /// 计算单条回答中本品牌的排名与曝光分数；回答记录列表与详情共用这一入口。
    /// 资格判定与 <see cref="AnalyticsProjector.Compute"/> 相同，只有当前冻结配置中的主体计入被提及主体。
    /// 回答只提及本品牌时名次为 1（<see cref="Ranking"/> 会跳过这类回答）；回答未提及任何主体时声量按 0 计分。
    /// 已提及本品牌但缺少出现位置时，名次与曝光分均不可用（SUBJECT_ORDER_UNAVAILABLE）。
    /// </summary>
    /// <param name="fact">单条 Check 事实。</param>
    /// <param name="configuration">快照冻结的主体配置。</param>
    /// <param name="stats">该 Check 内各主体的首次出现偏移与出现次数；缺少本品牌时名次不可用。</param>
    public static CheckBrandMetrics CheckBrandMetrics(AnalyticsCheckFact fact, SubjectConfiguration configuration,
        IReadOnlyDictionary<Guid, SubjectStats>? stats)
    {
        var ineligible = fact.Status != "succeeded" ? "CHECK_NOT_SUCCEEDED"
            : !fact.AnswerPresent ? "NO_ANSWER"
            : fact.AnalysisStatus != "completed" ? "ANALYSIS_NOT_READY"
            : fact.ConfigurationMissing ? "CONFIGURATION_MISSING" : null;
        if (ineligible is not null) return UnavailableCheckBrandMetrics(ineligible);

        var configured = configuration.Subjects.Select(s => s.Id).ToHashSet();
        var parts = Parts(fact, configuration.BrandId, configured, stats);
        var competitorCount = configuration.Subjects.Count(s => s.Type == "competitor");

        // 与 Ranking() 不同：回答中只有本品牌时也给出名次（第 1 名），见 prd D3。
        var ranking = !parts.BrandMentioned
            ? new RankingMetric(null, 0, 0, "BRAND_NOT_MENTIONED", CheckRankingVersion)
            : parts.Rank is int rank
                ? new RankingMetric(rank, 1, 1, null, CheckRankingVersion)
                : new RankingMetric(null, 0, 0, "SUBJECT_ORDER_UNAVAILABLE", CheckRankingVersion);

        var position = PositionOf(parts) is double value ? MetricMean.Of(value, 1) : MetricMean.Of(0, 0);
        var reason = competitorCount == 0 ? "NO_COMPETITOR_BASELINE"
            : position.Value is null ? "SUBJECT_ORDER_UNAVAILABLE" : null;
        var exposure = Score(MetricRatio.Of(parts.BrandMentioned ? 1 : 0, 1), position,
            MetricRatio.Of(parts.BrandOccurrences, parts.SubjectOccurrences),
            MetricRatio.Of(parts.OwnCitation ? 1 : 0, 1), reason);
        return new CheckBrandMetrics(ranking, exposure);
    }

    /// <summary>两项单条指标均不可用并携带同一原因代码；用于资格不满足或记录尚未到达。</summary>
    public static CheckBrandMetrics UnavailableCheckBrandMetrics(string reason)
        => new(new RankingMetric(null, 0, 0, reason, CheckRankingVersion),
            Score(MetricRatio.Of(0, 0), MetricMean.Of(0, 0), MetricRatio.Of(0, 0), MetricRatio.Of(0, 0), reason));

    /// <summary>
    /// 按 exposure-v2 聚合一组事实的曝光分（概览 KPI、每日趋势、分组、团队汇总共用）：只计合格回答，
    /// 提及率 = 提及本品牌的回答数 / 合格回答数；名次分 = 各回答名次分之和（未提及记 0）/ 可确定名次的回答数；
    /// 声量 = 本品牌出现次数之和 / 已配置主体出现次数之和（分母为 0 时按 0 计分）；自有引用率 = 有自有引用的回答数 / 合格回答数。
    /// 未配置竞品 → NO_COMPETITOR_BASELINE；没有合格回答 → ZERO_REQUIRED_DENOMINATOR；
    /// 没有可确定名次的回答 → SUBJECT_ORDER_UNAVAILABLE。
    /// </summary>
    /// <param name="facts">事实；不合格的回答会被忽略。</param>
    /// <param name="configuration">快照冻结的主体配置。</param>
    /// <param name="stats">按 Check 的主体统计（首次偏移与出现次数）。</param>
    public static ExposureScore AggregateExposure(IEnumerable<AnalyticsCheckFact> facts,
        SubjectConfiguration configuration, IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, SubjectStats>> stats)
    {
        var configured = configuration.Subjects.Select(s => s.Id).ToHashSet();
        var competitorCount = configuration.Subjects.Count(s => s.Type == "competitor");
        long eligible = 0, mentioned = 0, own = 0, brandOccurrences = 0, subjectOccurrences = 0, positionCount = 0;
        double positionTotal = 0;
        foreach (var fact in facts.Where(IsEligible))
        {
            var parts = Parts(fact, configuration.BrandId, configured, stats.GetValueOrDefault(fact.CheckId));
            eligible++;
            if (parts.BrandMentioned) mentioned++;
            if (parts.OwnCitation) own++;
            brandOccurrences += parts.BrandOccurrences;
            subjectOccurrences += parts.SubjectOccurrences;
            if (PositionOf(parts) is double position)
            {
                positionTotal += position;
                positionCount++;
            }
        }
        var positionScore = MetricMean.Of(positionTotal, positionCount);
        var reason = competitorCount == 0 ? "NO_COMPETITOR_BASELINE"
            : eligible == 0 ? "ZERO_REQUIRED_DENOMINATOR"
            : positionScore.Value is null ? "SUBJECT_ORDER_UNAVAILABLE" : null;
        return Score(MetricRatio.Of(mentioned, eligible), positionScore,
            MetricRatio.Of(brandOccurrences, subjectOccurrences), MetricRatio.Of(own, eligible), reason);
    }

    /// <summary>
    /// 首位 / 前三提及率：分母为合格回答数，分子为本品牌名次 = 1 / ≤ 3 的回答数。名次与 <see cref="AggregateExposure"/> 同一口径
    /// （回答只提及本品牌时为第 1 名，与 <see cref="Ranking"/> 跳过这类回答不同）；已提及但缺少出现位置的回答只计入分母。
    /// </summary>
    /// <param name="facts">事实；不合格的回答会被忽略。</param>
    /// <param name="configuration">快照冻结的主体配置。</param>
    /// <param name="stats">按 Check 的主体统计（首次偏移与出现次数）。</param>
    public static (MetricRatio First, MetricRatio TopThree) PlacementRates(IEnumerable<AnalyticsCheckFact> facts,
        SubjectConfiguration configuration, IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, SubjectStats>> stats)
    {
        var configured = configuration.Subjects.Select(s => s.Id).ToHashSet();
        long eligible = 0, first = 0, topThree = 0;
        foreach (var fact in facts.Where(IsEligible))
        {
            eligible++;
            if (Parts(fact, configuration.BrandId, configured, stats.GetValueOrDefault(fact.CheckId)).Rank is not int rank)
                continue;
            if (rank == 1) first++;
            if (rank <= 3) topThree++;
        }
        return (MetricRatio.Of(first, eligible), MetricRatio.Of(topThree, eligible));
    }

    /// <summary>竞品对比中的曝光分占位：只为本品牌计分，竞品恒为不可用（ONLY_BRAND_SCORE），声量组成沿用竞品的提及份额。</summary>
    /// <param name="shareOfVoice">竞品的声量份额。</param>
    public static ExposureScore CompetitorExposurePlaceholder(MetricRatio shareOfVoice)
        => Score(MetricRatio.Of(0, 0), MetricMean.Of(0, 0), shareOfVoice, MetricRatio.Of(0, 0), "ONLY_BRAND_SCORE");
}
