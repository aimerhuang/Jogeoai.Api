namespace Jogeoai.Domain;

/// <summary>保留分子分母及零分母 null 语义的比率。</summary>
public sealed record MetricRatio(long Numerator, long Denominator, double? Value)
{
    /// <summary>按有效分母计算 0–1 比率。</summary>
    public static MetricRatio Of(long numerator, long denominator)
        => new(numerator, denominator, denominator == 0 ? null : (double)numerator / denominator);
}

/// <summary>统一筛选后的 Check 级分析事实；同主体只出现一次。</summary>
public sealed record AnalyticsCheckFact(Guid CheckId, Guid RunId, Guid PromptId, Guid? PromptVersionId,
    Guid? TargetId, int SampleIndex, DateTimeOffset? CapturedAt, string Status, bool AnswerPresent,
    string AnalysisStatus, bool ConfigurationMissing, bool SnapshotMissing, bool OwnCitation,
    IReadOnlyList<Guid> MentionedSubjects, string Provider, string Engine, string Country, string Locale,
    string CaptureMode, string EvidenceClass, IReadOnlyList<string> Tags, string? BrandedStatus,
    string? PromptSnapshot = null, string QueryAvailability = "unknown",
    string? ModelVersion = null, int? DurationMs = null, long ProviderRetries = 0,
    long CitationCount = 0, string? PromptLocale = null);

/// <summary>同一有效 Check 集合的核心计数和比率。</summary>
public sealed record CoreMetrics(long RunCount, long CheckCount, long PromptCount, long SuccessfulChecks,
    long EligibleChecks, long FailedChecks, long NoAnswerChecks, long AnalysisPendingChecks,
    long AnalysisFailedChecks, long MissingSnapshotChecks, long ConfigurationMissingChecks,
    MetricRatio Visibility, MetricRatio ShareOfVoice, MetricRatio AnyCompetitorMentionRate,
    MetricRatio OwnCitationRate, long TotalMentions, DateTimeOffset? LastCapturedAt);

/// <summary>声量榜的一条主体，含零命中主体。</summary>
public sealed record SubjectShareEntry(Guid SubjectId, string Name, bool IsBrand, long Mentions,
    MetricRatio Share, long PromptCount, int Rank);

/// <summary>同一窗口完整主体声量投影。</summary>
public sealed record VoiceProjection(CoreMetrics Metrics, IReadOnlyList<SubjectShareEntry> Entries);

/// <summary>每日实际或最近观测的品牌指标。</summary>
public sealed record AnalyticsDayPoint(DateOnly Date, MetricRatio Visibility, MetricRatio ShareOfVoice,
    long EligibleChecks, long ObservedChecks, long CarriedChecks, bool IsCarriedForward,
    DateOnly? EarliestSourceDate, DateOnly? LatestSourceDate);

/// <summary>分析指标的纯函数投影；各 API 共用一套 C/N/M 定义。</summary>
public static class AnalyticsProjector
{
    /// <summary>由 Check 级事实计算 KPI 与全主体声量。</summary>
    public static VoiceProjection Compute(IReadOnlyList<AnalyticsCheckFact> facts,
        SubjectConfiguration configuration)
    {
        var eligible = facts.Where(f => f.Status == "succeeded" && f.AnswerPresent
            && f.AnalysisStatus == "completed" && !f.ConfigurationMissing).ToArray();
        var subjects = configuration.Subjects;
        var brand = subjects.FirstOrDefault(s => s.Type == "brand");
        var activeIds = subjects.Select(s => s.Id).ToHashSet();
        var counts = subjects.ToDictionary(s => s.Id,
            s => (Mentions: (long)eligible.Count(f => f.MentionedSubjects.Contains(s.Id)),
                Prompts: (long)eligible.Where(f => f.MentionedSubjects.Contains(s.Id)).Select(f => f.PromptId).Distinct().Count()));
        var totalMentions = counts.Values.Sum(v => v.Mentions);
        var brandMentions = brand is null ? 0 : counts[brand.Id].Mentions;
        var anyCompetitor = eligible.Count(f => f.MentionedSubjects.Any(id => activeIds.Contains(id)
            && subjects.Any(s => s.Id == id && s.Type == "competitor")));
        var n = eligible.Length;
        var metrics = new CoreMetrics(facts.Select(f => f.RunId).Distinct().Count(), facts.Count,
            facts.Select(f => f.PromptId).Distinct().Count(), facts.Count(f => f.Status == "succeeded"),
            n, facts.Count(f => f.Status == "failed"),
            facts.Count(f => f.Status == "succeeded" && !f.AnswerPresent),
            facts.Count(f => f.Status == "succeeded" && f.AnswerPresent
                && f.AnalysisStatus is "pending" or "processing"),
            facts.Count(f => f.Status == "succeeded" && f.AnswerPresent && f.AnalysisStatus == "failed"),
            facts.Count(f => f.SnapshotMissing), facts.Count(f => f.ConfigurationMissing),
            MetricRatio.Of(brandMentions, n), MetricRatio.Of(brandMentions, totalMentions),
            MetricRatio.Of(anyCompetitor, n), MetricRatio.Of(eligible.Count(f => f.OwnCitation), n),
            totalMentions, facts.Select(f => f.CapturedAt).Max());
        var entries = subjects.OrderByDescending(s => counts[s.Id].Mentions).ThenBy(s => s.Id)
            .Select((s, index) => new SubjectShareEntry(s.Id, s.Name, s.Type == "brand",
                counts[s.Id].Mentions, MetricRatio.Of(counts[s.Id].Mentions, totalMentions),
                counts[s.Id].Prompts, index + 1)).ToArray();
        return new VoiceProjection(metrics, entries);
    }

    /// <summary>按站点当地日期计算实际观测；空日返回 null 比率。</summary>
    public static IReadOnlyList<AnalyticsDayPoint> ObservedTrend(IReadOnlyList<AnalyticsCheckFact> facts,
        SubjectConfiguration configuration, DateOnly from, DateOnly to, TimeZoneInfo zone)
    {
        var points = new List<AnalyticsDayPoint>();
        for (var date = from; date < to; date = date.AddDays(1))
        {
            var dayFacts = facts.Where(f => f.CapturedAt is not null
                && DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(f.CapturedAt.Value, zone).DateTime) == date).ToArray();
            var metrics = Compute(dayFacts, configuration).Metrics;
            points.Add(new AnalyticsDayPoint(date, metrics.Visibility, metrics.ShareOfVoice,
                metrics.EligibleChecks, dayFacts.LongLength, 0, false,
                dayFacts.Length == 0 ? null : date, dayFacts.Length == 0 ? null : date));
        }
        return points;
    }

    /// <summary>按 Prompt 版本和 Target 整组替换主体向量，返回每日点及末日贡献事实。</summary>
    public static (IReadOnlyList<AnalyticsDayPoint> Trend, IReadOnlyList<AnalyticsCheckFact> FinalFacts)
        LatestObservation(IReadOnlyList<AnalyticsCheckFact> facts, SubjectConfiguration configuration,
            DateOnly from, DateOnly to, TimeZoneInfo zone)
    {
        var state = new Dictionary<(Guid PromptVersion, Guid Target), (DateOnly Source, AnalyticsCheckFact[] Facts)>();
        var points = new List<AnalyticsDayPoint>();
        for (var date = from; date < to; date = date.AddDays(1))
        {
            var observed = facts.Where(f => f.CapturedAt is not null
                && DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(f.CapturedAt.Value, zone).DateTime) == date
                && f.Status == "succeeded" && f.AnswerPresent && f.AnalysisStatus == "completed"
                && !f.ConfigurationMissing).ToArray();
            foreach (var group in observed.GroupBy(f => (f.PromptVersionId ?? f.PromptId, f.TargetId ?? Guid.Empty)))
                state[group.Key] = (date, group.ToArray());
            var current = state.Values.SelectMany(v => v.Facts).ToArray();
            var metrics = Compute(current, configuration).Metrics;
            var carried = state.Values.Where(v => v.Source != date).Sum(v => v.Facts.LongLength);
            points.Add(new AnalyticsDayPoint(date, metrics.Visibility, metrics.ShareOfVoice,
                metrics.EligibleChecks, observed.LongLength, carried, carried > 0,
                state.Count == 0 ? null : state.Values.Min(v => v.Source),
                state.Count == 0 ? null : state.Values.Max(v => v.Source)));
        }
        return (points, state.Values.SelectMany(v => v.Facts).ToArray());
    }
}
