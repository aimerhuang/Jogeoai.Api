namespace Jogeoai.Domain;

/// <summary>口碑聚合的候选平台（来自已配置采集目标），不对外序列化。</summary>
/// <param name="Provider">采集来源。</param>
/// <param name="Engine">回答引擎。</param>
/// <param name="CaptureMode">采集模式。</param>
public sealed record ReputationPlatformCandidate(string Provider, string Engine, string CaptureMode);

/// <summary>
/// 口碑页各区块的纯函数投影：KPI、环比、趋势、平台、问题、热力图、印象词和质量全部由同一组 <see cref="ReputationFact"/>
/// 与同一个 <see cref="ReputationRules.Score(long, long, long, long, long, long)"/> 计算；回答列表的 sentiment / term / termPolarity 筛选使用
/// <see cref="MatchesRecordFilter"/>，因此任一区块的计数（含情感因子的分极性回答数）都等于同筛选下回答列表的条数。
/// </summary>
public static class ReputationProjector
{
    /// <summary>回答列表 sentiment / term / termPolarity 筛选与聚合共用的唯一成员判定。</summary>
    /// <param name="fact">回答事实。</param>
    /// <param name="state">状态筛选（回答整体判定）；为空表示不限。</param>
    /// <param name="termKey">规范化词键筛选；为空表示不限。</param>
    /// <param name="termPolarity">词在该回答中的极性，只与 <paramref name="termKey"/> 一起生效；为空表示不限。</param>
    public static bool MatchesRecordFilter(ReputationFact fact, ReputationSentimentState? state, string? termKey,
        ReputationTermPolarity? termPolarity = null)
        => (state is null || fact.State == state.Value)
           && (termKey is null || fact.Terms.Any(t => string.Equals(t.TermKey, termKey, StringComparison.Ordinal)
               && (termPolarity is null || t.Polarity == termPolarity.Value)));

    /// <summary>生成完整口碑响应。</summary>
    /// <param name="meta">快照与范围元数据（含视图与当地日期范围）。</param>
    /// <param name="current">当前窗口内全部回答事实（含不适用记录）。</param>
    /// <param name="previous">前一个等长窗口的回答事实。</param>
    /// <param name="configured">已配置且符合筛选的平台；没有数据的进入 platformsWithoutData。</param>
    /// <param name="previousFrom">前一窗口起点。</param>
    /// <param name="previousTo">前一窗口终点。</param>
    public static ReputationSummaryView Build(ReputationMeta meta, IReadOnlyList<ReputationFact> current,
        IReadOnlyList<ReputationFact> previous, IReadOnlyList<ReputationPlatformCandidate> configured,
        DateTimeOffset previousFrom, DateTimeOffset previousTo)
    {
        var score = ReputationRules.Score(current);
        var eligible = current.LongCount(f => f.State != ReputationSentimentState.NotApplicable);
        var notMentioned = current.LongCount(f => f.State == ReputationSentimentState.NotMentioned);
        var metrics = new ReputationMetrics(score, eligible, notMentioned,
            eligible == 0 ? "NO_ELIGIBLE_ANSWERS" : score.UnavailableReason);
        var comparison = Compare(score, ReputationRules.Score(previous), previousFrom, previousTo);

        var (platforms, withoutData) = Platforms(current, configured, score);
        var dates = new List<DateOnly>();
        if (meta.View == ReputationView.Range)
            for (var date = meta.LocalFrom; date < meta.LocalTo; date = date.AddDays(1)) dates.Add(date);
        var trend = Trend(current, dates);
        var byPlatform = current.GroupBy(f => f.PlatformKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        IReadOnlyList<ReputationPlatformTrend> platformTrends = dates.Count == 0 ? [] : platforms.Select(p => new ReputationPlatformTrend(p.PlatformKey,
            Trend(byPlatform.TryGetValue(p.PlatformKey, out var rows) ? rows : [], dates))).ToArray();

        var (questions, questionsWithoutData) = Questions(current);
        var cells = current.GroupBy(f => (f.PromptId, f.PlatformKey))
            .ToDictionary(g => g.Key, g => ReputationRules.Score(g));
        var heatmap = new ReputationHeatmap(platforms.Select(p => p.PlatformKey).ToArray(),
            questions.Select(q => new ReputationHeatmapRow(q.PromptId, q.PromptText, platforms.Select(p =>
            {
                var cell = cells.TryGetValue((q.PromptId, p.PlatformKey), out var found) ? found : ReputationRules.Score(0, 0, 0);
                return new ReputationHeatmapCell(p.PlatformKey, CellState(cell), cell);
            }).ToArray())).ToArray());

        var unknownReasons = current.Where(f => f.State == ReputationSentimentState.Unknown)
            .GroupBy(f => f.RationaleCode ?? "UNSPECIFIED", StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.LongCount(), StringComparer.Ordinal);
        var quality = new ReputationQuality(eligible, score.Mentioned, notMentioned, score.Classified, score.Unknown,
            score.Pending, score.Failed, score.Coverage, score.PartialCoverage, unknownReasons,
            ReputationRules.ClassifierVersion, questions.Count, platforms.Count);
        return new ReputationSummaryView(meta, metrics, comparison, trend, platformTrends, platforms, withoutData,
            questions, questionsWithoutData, heatmap, Terms(current), quality);
    }

    /// <summary>环比：前一窗口已判定样本低于最低样本数时返回 INSUFFICIENT_SAMPLE，不给出差值。</summary>
    public static ReputationComparison Compare(ReputationScore current, ReputationScore previous,
        DateTimeOffset previousFrom, DateTimeOffset previousTo)
    {
        if (previous.Classified < ReputationRules.MinSampleSize)
            return new ReputationComparison(previousFrom, previousTo, previous, null, null, null, "INSUFFICIENT_SAMPLE");
        if (current.Health is null)
            return new ReputationComparison(previousFrom, previousTo, previous, null, null, null, "NO_CURRENT_SAMPLE");
        return new ReputationComparison(previousFrom, previousTo, previous, current.Health - previous.Health,
            (current.PositiveRate.Value - previous.PositiveRate.Value) * 100,
            (current.NegativeRate.Value - previous.NegativeRate.Value) * 100, null);
    }

    /// <summary>逐日趋势：给定日期集合中的每一个日期都有一个点，无样本日期健康度为空。</summary>
    public static IReadOnlyList<ReputationTrendPoint> Trend(IReadOnlyList<ReputationFact> facts, IReadOnlyList<DateOnly> dates)
    {
        if (dates.Count == 0) return [];
        var byDate = facts.Where(f => f.LocalDate is not null).GroupBy(f => f.LocalDate!.Value)
            .ToDictionary(g => g.Key, g => g.ToArray());
        return dates.Select(date => new ReputationTrendPoint(date,
            ReputationRules.Score(byDate.TryGetValue(date, out var day) ? day : []))).ToArray();
    }

    /// <summary>热力图单元格状态。</summary>
    public static ReputationCellState CellState(ReputationScore score)
        => score.Mentioned == 0 ? ReputationCellState.NoData
            : score.Classified == 0 ? ReputationCellState.Pending
            : score.LowSample ? ReputationCellState.LowSample : ReputationCellState.Ok;

    private static (IReadOnlyList<ReputationPlatformView>, IReadOnlyList<ReputationPlatformWithoutData>) Platforms(
        IReadOnlyList<ReputationFact> facts, IReadOnlyList<ReputationPlatformCandidate> configured, ReputationScore overall)
    {
        var identities = new Dictionary<string, ReputationPlatformCandidate>(StringComparer.Ordinal);
        foreach (var fact in facts)
            identities.TryAdd(fact.PlatformKey, new ReputationPlatformCandidate(fact.Provider, fact.Engine, fact.CaptureMode));
        foreach (var candidate in configured)
            identities.TryAdd(PlatformIdentity.Key(candidate.Provider, candidate.Engine, candidate.CaptureMode), candidate);
        var grouped = facts.GroupBy(f => f.PlatformKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var withData = new List<(string Key, ReputationPlatformCandidate Identity, ReputationScore Score)>();
        var without = new List<ReputationPlatformWithoutData>();
        foreach (var (key, identity) in identities.OrderBy(i => i.Key, StringComparer.Ordinal))
        {
            var rows = grouped.TryGetValue(key, out var found) ? found : [];
            var score = ReputationRules.Score(rows);
            if (score.Classified > 0) { withData.Add((key, identity, score)); continue; }
            var eligible = rows.Any(f => f.State != ReputationSentimentState.NotApplicable);
            without.Add(new ReputationPlatformWithoutData(key, identity.Provider, identity.Engine, identity.CaptureMode,
                PlatformIdentity.DisplayName(identity.Provider, identity.Engine, identity.CaptureMode),
                !eligible ? "NO_ANSWERS" : score.Mentioned == 0 ? "NO_MENTIONS" : "NOT_CLASSIFIED"));
        }
        var comparable = withData.Where(p => !p.Score.LowSample).Select(p => p.Score.Health!.Value).ToArray();
        var highlight = comparable.Length >= 2 && comparable.Max() > comparable.Min();
        var best = highlight ? comparable.Max() : double.NaN;
        var worst = highlight ? comparable.Min() : double.NaN;
        var platforms = withData.OrderByDescending(p => p.Score.Classified).ThenBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new ReputationPlatformView(p.Key, p.Identity.Provider, p.Identity.Engine, p.Identity.CaptureMode,
                PlatformIdentity.DisplayName(p.Identity.Provider, p.Identity.Engine, p.Identity.CaptureMode), p.Score,
                p.Score.Health - overall.Health,
                highlight && !p.Score.LowSample && p.Score.Health == best,
                highlight && !p.Score.LowSample && p.Score.Health == worst)).ToArray();
        return (platforms, without);
    }

    private static (IReadOnlyList<ReputationQuestionView>, IReadOnlyList<ReputationQuestionWithoutData>) Questions(
        IReadOnlyList<ReputationFact> facts)
    {
        var withData = new List<ReputationQuestionView>();
        var without = new List<ReputationQuestionWithoutData>();
        foreach (var group in facts.Where(f => f.State != ReputationSentimentState.NotApplicable).GroupBy(f => f.PromptId))
        {
            var text = group.OrderByDescending(f => f.CapturedAt).ThenByDescending(f => f.CheckId)
                .Select(f => f.PromptText).FirstOrDefault() ?? "";
            var score = ReputationRules.Score(group);
            if (score.Classified > 0) withData.Add(new ReputationQuestionView(group.Key, text, score));
            else without.Add(new ReputationQuestionWithoutData(group.Key, text,
                score.Mentioned == 0 ? "NO_MENTIONS" : "NOT_CLASSIFIED"));
        }
        // 风险优先：样本不足置后；健康度升序；同值按已判定回答数降序；最后按标识稳定排序。
        return (withData.OrderBy(q => q.Score.LowSample).ThenBy(q => q.Score.Health)
                .ThenByDescending(q => q.Score.Classified).ThenBy(q => q.PromptId).ToArray(),
            without.OrderBy(q => q.PromptText, StringComparer.Ordinal).ThenBy(q => q.PromptId).ToArray());
    }

    private static ReputationTermGroups Terms(IReadOnlyList<ReputationFact> facts)
    {
        var entries = facts.Where(f => ReputationRules.IsClassified(f.State))
            .SelectMany(f => f.Terms.Select(t => (f.CheckId, Term: t)))
            .GroupBy(e => e.Term.TermKey, StringComparer.Ordinal)
            .Select(g =>
            {
                long Answers(ReputationTermPolarity p) => g.Where(e => e.Term.Polarity == p).Select(e => e.CheckId).Distinct().LongCount();
                long Occurrences(ReputationTermPolarity p) => g.Where(e => e.Term.Polarity == p).Sum(e => (long)e.Term.Occurrences);
                var positive = Answers(ReputationTermPolarity.Positive);
                var neutral = Answers(ReputationTermPolarity.Neutral);
                var negative = Answers(ReputationTermPolarity.Negative);
                // 同一回答内同一词键的多种极性只计一次出现次数（取最大值），避免把同一处原文重复累计。
                var occurrences = g.GroupBy(e => e.CheckId).Sum(c => (long)c.Max(e => e.Term.Occurrences));
                return new ReputationTermView(g.Select(e => e.Term.TermText).Order(StringComparer.Ordinal).First(), g.Key,
                    ReputationRules.DominantPolarity(positive, neutral, negative, Occurrences(ReputationTermPolarity.Positive),
                        Occurrences(ReputationTermPolarity.Neutral), Occurrences(ReputationTermPolarity.Negative)),
                    g.Select(e => e.CheckId).Distinct().LongCount(), occurrences, positive, neutral, negative);
            });
        var views = entries.ToArray();
        // 每个词只归入主要极性一组；先分组再截断，回答数少的负面词不会被大量正面词挤掉。
        IReadOnlyList<ReputationTermView> Top(ReputationTermPolarity polarity, Func<ReputationTermView, long> answers)
            => views.Where(t => t.DominantPolarity == polarity).OrderByDescending(answers)
                .ThenByDescending(t => t.AnswerCount).ThenByDescending(t => t.Occurrences)
                .ThenBy(t => t.TermKey, StringComparer.Ordinal).Take(ReputationRules.TopTermsPerPolarity).ToArray();
        return new ReputationTermGroups(Top(ReputationTermPolarity.Positive, t => t.PositiveAnswers),
            Top(ReputationTermPolarity.Neutral, t => t.NeutralAnswers),
            Top(ReputationTermPolarity.Negative, t => t.NegativeAnswers));
    }
}
