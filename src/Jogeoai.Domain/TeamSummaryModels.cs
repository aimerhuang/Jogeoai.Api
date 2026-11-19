namespace Jogeoai.Domain;

/// <summary>团队级跨品牌汇总的查询参数。</summary>
/// <param name="From">起点（含）：带时区的 ISO 时间；默认最近 7 个当地自然日的起点。</param>
/// <param name="To">终点（不含）：带时区的 ISO 时间；默认当前时间。</param>
/// <param name="TimeZone">IANA 时区；省略时取最早创建的启用品牌时区，没有品牌时为 Asia/Shanghai。</param>
/// <param name="Provider">可选 Provider（cloro、doubao_api、deepseek_api、packy_qwen、packy_glm、packy_kimi、packy_yuanbao、domestic_collector）；省略时统计全部。</param>
/// <param name="Engine">可选引擎；省略时统计全部。</param>
/// <param name="SiteId">可选品牌工作区标识；省略时返回全部启用品牌。</param>
public sealed record TeamSummaryRequest(DateTimeOffset? From = null, DateTimeOffset? To = null, string? TimeZone = null,
    string? Provider = null, string? Engine = null, Guid? SiteId = null);

/// <summary>本期与上一期的指标及差值；任一侧不可用时差值为空。</summary>
/// <param name="Current">本期值。</param>
/// <param name="Previous">上一期值。</param>
/// <param name="Difference">本期减上一期；提及率为百分点，排名为名次差（负数表示改善）。</param>
/// <param name="UnavailableReason">本期值不可用的原因。</param>
public sealed record PeriodMetric(double? Current, double? Previous, double? Difference, string? UnavailableReason);

/// <summary>品牌文章计数。</summary>
/// <param name="Total">未归档文章数。</param>
/// <param name="Tracking">追踪中的文章数。</param>
/// <param name="Cited">最近 30 天被引用的去重文章数；没有合格检测或分析不可用时为空。</param>
public sealed record TeamBrandArticles(long Total, long Tracking, long? Cited);

/// <summary>品牌已启用的监控目标。</summary>
/// <param name="Provider">采集 Provider。</param>
/// <param name="Engine">引擎。</param>
/// <param name="DisplayName">目录中的展示名称。</param>
public sealed record TeamBrandTarget(string Provider, string Engine, string DisplayName);

/// <summary>团队汇总中的一行品牌。</summary>
/// <param name="SiteId">品牌工作区标识。</param>
/// <param name="Name">品牌名称。</param>
/// <param name="PrimaryDomain">主域名；无官网时为 null。</param>
/// <param name="MentionRate">品牌提及率（0–100）；差值为百分点。</param>
/// <param name="Exposure">曝光效果分数（0–100）。</param>
/// <param name="AverageRanking">平均排名（名次）；差值为本期减上一期，负数表示改善。</param>
/// <param name="MonitoredPrompts">区间内有合格回答的去重提问数。</param>
/// <param name="EnabledPromptCount">当前启用的提问数。</param>
/// <param name="PromptGroupCount">提问分组数。</param>
/// <param name="CompetitorCount">启用的竞品数。</param>
/// <param name="Articles">文章计数。</param>
/// <param name="EnabledTargets">已启用的监控目标。</param>
/// <param name="IndustryRank">行业综合排名；暂未提供，恒为空。</param>
/// <param name="ExposureIndustryRank">行业曝光排名；暂未提供，恒为空。</param>
/// <param name="MentionIndustryRank">行业提及排名；暂未提供，恒为空。</param>
/// <param name="RankingIndustryRank">行业平均排名名次；暂未提供，恒为空。</param>
public sealed record TeamBrandRow(Guid SiteId, string Name, string? PrimaryDomain,
    PeriodMetric MentionRate, PeriodMetric Exposure, PeriodMetric AverageRanking, PeriodMetric MonitoredPrompts,
    int EnabledPromptCount, int PromptGroupCount, int CompetitorCount, TeamBrandArticles Articles,
    IReadOnlyList<TeamBrandTarget> EnabledTargets,
    int? IndustryRank = null, int? ExposureIndustryRank = null, int? MentionIndustryRank = null, int? RankingIndustryRank = null);

/// <summary>汇总采用的时间范围。</summary>
/// <param name="From">本期起点（含）。</param>
/// <param name="To">本期终点（不含）。</param>
/// <param name="PreviousFrom">上一期起点（含）。</param>
/// <param name="PreviousTo">上一期终点（不含），等于本期起点。</param>
/// <param name="TimeZone">用于切分自然日的 IANA 时区。</param>
public sealed record TeamSummaryRange(DateTimeOffset From, DateTimeOffset To, DateTimeOffset PreviousFrom, DateTimeOffset PreviousTo, string TimeZone);

/// <summary>团队合计。</summary>
/// <param name="Brands">启用品牌数。</param>
/// <param name="EnabledEngineCount">所有品牌启用目标按 Provider+引擎去重后的数量。</param>
/// <param name="PromptGroupCount">提问分组合计。</param>
/// <param name="CompetitorCount">启用竞品合计。</param>
public sealed record TeamSummaryTotals(int Brands, int EnabledEngineCount, int PromptGroupCount, int CompetitorCount);

/// <summary>团队级跨品牌汇总。</summary>
/// <param name="Range">采用的时间范围。</param>
/// <param name="Brands">按创建时间升序的启用品牌行。</param>
/// <param name="EnabledEngines">所有品牌启用目标按 Provider+引擎去重，顺序按首次出现。</param>
/// <param name="Totals">团队合计。</param>
public sealed record TeamSummaryView(TeamSummaryRange Range, IReadOnlyList<TeamBrandRow> Brands,
    IReadOnlyList<TeamBrandTarget> EnabledEngines, TeamSummaryTotals Totals);

/// <summary>单品牌本期与上一期的分析指标（供团队汇总使用，不直接序列化）。</summary>
/// <param name="Current">本期核心指标。</param>
/// <param name="Previous">上一期核心指标。</param>
/// <param name="Ranking">本期平均排名。</param>
/// <param name="PreviousRanking">上一期平均排名。</param>
/// <param name="Exposure">本期曝光分数。</param>
/// <param name="PreviousExposure">上一期曝光分数。</param>
/// <param name="MonitoredPrompts">本期有合格回答的去重提问数。</param>
/// <param name="PreviousMonitoredPrompts">上一期有合格回答的去重提问数。</param>
public sealed record BrandPeriodMetrics(CoreMetrics Current, CoreMetrics? Previous, RankingMetric Ranking, RankingMetric? PreviousRanking,
    ExposureScore Exposure, ExposureScore? PreviousExposure, int MonitoredPrompts, int? PreviousMonitoredPrompts);

/// <summary>团队汇总的纯函数：时间范围、环比差值、行组装与引擎去重。</summary>
public static class TeamSummaryProjector
{
    /// <summary>没有品牌时区时采用的默认时区。</summary>
    public const string DefaultTimeZone = "Asia/Shanghai";
    /// <summary>默认统计的当地自然日数。</summary>
    public const int DefaultDays = 7;
    /// <summary>本期跨度上限（当地自然日）。</summary>
    public const int MaxDays = 366;

    /// <summary>解析时间范围：默认最近 7 个当地自然日；上一期为等长且紧邻的前一区间。</summary>
    public static TeamSummaryRange Range(DateTimeOffset? requestedFrom, DateTimeOffset? requestedTo, string timeZone,
        TimeZoneInfo zone, DateTimeOffset now)
    {
        var to = requestedTo?.ToUniversalTime() ?? now;
        var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(to, zone).DateTime);
        var defaultFrom = localToday.AddDays(-(DefaultDays - 1)).ToDateTime(TimeOnly.MinValue);
        var from = requestedFrom?.ToUniversalTime() ?? new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(defaultFrom, zone));
        var localFrom = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from, zone).DateTime);
        var localTo = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(to.AddTicks(-1), zone).DateTime).AddDays(1);
        if (from >= to || localTo.DayNumber - localFrom.DayNumber > MaxDays)
            throw new CollectorStoreException("INVALID_TIME_RANGE", "时间范围必须递增且不超过 366 天。", 400);
        return new TeamSummaryRange(from, to, from - (to - from), from, timeZone);
    }

    /// <summary>本期减上一期；任一侧为空则为空。</summary>
    public static double? Difference(double? current, double? previous)
        => current is null || previous is null ? null : current.Value - previous.Value;

    /// <summary>指标不可用时的占位。</summary>
    public static PeriodMetric Unavailable(string reason) => new(null, null, null, reason);

    /// <summary>品牌提及率：比率乘以 100，差值为百分点。</summary>
    public static PeriodMetric MentionRate(BrandPeriodMetrics metrics)
    {
        var current = metrics.Current.Visibility.Value * 100;
        var previous = metrics.Previous?.Visibility.Value * 100;
        return new PeriodMetric(current, previous, Difference(current, previous), current is null ? "NO_ELIGIBLE_CHECKS" : null);
    }

    /// <summary>曝光效果分数（0–100）。</summary>
    public static PeriodMetric Exposure(BrandPeriodMetrics metrics)
        => new(metrics.Exposure.Value, metrics.PreviousExposure?.Value,
            Difference(metrics.Exposure.Value, metrics.PreviousExposure?.Value), metrics.Exposure.EligibilityReason);

    /// <summary>平均排名：差值为本期减上一期，负数表示改善。</summary>
    public static PeriodMetric AverageRanking(BrandPeriodMetrics metrics)
        => new(metrics.Ranking.Value, metrics.PreviousRanking?.Value,
            Difference(metrics.Ranking.Value, metrics.PreviousRanking?.Value), metrics.Ranking.UnavailableReason);

    /// <summary>区间内有合格回答的去重提问数。</summary>
    public static PeriodMetric MonitoredPrompts(BrandPeriodMetrics metrics)
    {
        double current = metrics.MonitoredPrompts;
        double? previous = metrics.PreviousMonitoredPrompts;
        return new PeriodMetric(current, previous, Difference(current, previous), null);
    }

    /// <summary>组装一行品牌；分析失败时四项指标置空并带原因，行业排名恒为空。</summary>
    public static TeamBrandRow Row(BrandView brand, BrandPeriodMetrics? metrics, string? unavailableReason,
        int enabledPromptCount, int promptGroupCount, int competitorCount, TeamBrandArticles articles,
        IReadOnlyList<TeamBrandTarget> enabledTargets)
    {
        var reason = unavailableReason ?? "ANALYTICS_UNAVAILABLE";
        return new TeamBrandRow(brand.SiteId, brand.Name, brand.PrimaryDomain,
            metrics is null ? Unavailable(reason) : MentionRate(metrics),
            metrics is null ? Unavailable(reason) : Exposure(metrics),
            metrics is null ? Unavailable(reason) : AverageRanking(metrics),
            metrics is null ? Unavailable(reason) : MonitoredPrompts(metrics),
            enabledPromptCount, promptGroupCount, competitorCount, articles, enabledTargets);
    }

    /// <summary>文章计数：总数剔除已归档，追踪中取阶段计数。</summary>
    public static TeamBrandArticles Articles(ArticleListPage page, long? cited)
        => new(page.TotalCount - (page.StageCounts.TryGetValue("archived", out var archived) ? archived : 0),
            page.StageCounts.TryGetValue("tracking", out var tracking) ? tracking : 0, cited);

    /// <summary>所有品牌的启用目标按 Provider+引擎去重，顺序按首次出现。</summary>
    public static IReadOnlyList<TeamBrandTarget> EnabledEngines(IEnumerable<TeamBrandRow> rows)
        => rows.SelectMany(r => r.EnabledTargets)
            .DistinctBy(t => (t.Provider, t.Engine))
            .ToArray();

    /// <summary>组装汇总视图与合计。</summary>
    public static TeamSummaryView View(TeamSummaryRange range, IReadOnlyList<TeamBrandRow> rows)
    {
        var engines = EnabledEngines(rows);
        return new TeamSummaryView(range, rows, engines, new TeamSummaryTotals(rows.Count, engines.Count,
            rows.Sum(r => r.PromptGroupCount), rows.Sum(r => r.CompetitorCount)));
    }
}
