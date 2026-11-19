using Jogeoai.Domain;

namespace Jogeoai.Infrastructure;

public sealed partial class AnalyticsQueryStore
{
    /// <inheritdoc />
    public async Task<BrandPeriodMetrics> BrandPeriodMetricsAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, CancellationToken cancellationToken = default)
    {
        // 与 BrandDashboardAsync 同一管线：一次 PrepareAsync（无 token，会落一条 analytics_snapshots）。
        var data = await PrepareAsync(tenantId, siteId, request, null, cancellationToken);
        var ops = await LoadDashboardOpsAsync(data.Snapshot, cancellationToken);
        var facts = EnrichFacts(data.Facts, ops);
        // 主体统计（首次偏移与出现次数）按发布版本与分析水位每个品牌只读取一次，不限时间窗口，覆盖当期与上一期 Check。
        var stats = await LoadSubjectStatsAsync(data.Snapshot, cancellationToken);
        var brand = data.Configuration.BrandId;
        var current = AnalyticsProjector.Compute(facts, data.Configuration).Metrics;
        var ranking = DashboardAnalyticsProjector.Ranking(facts, brand, stats);
        var exposure = DashboardAnalyticsProjector.AggregateExposure(facts, data.Configuration, stats);
        // 上一期 = 等长且紧邻的前一区间。
        var previousFilter = data.Snapshot.Filter with
        {
            From = data.Snapshot.Filter.From - (data.Snapshot.Filter.To - data.Snapshot.Filter.From),
            To = data.Snapshot.Filter.From
        };
        var previousFacts = await CachedPreviousFactsAsync(data, previousFilter, cancellationToken);
        var previousOps = await LoadDashboardOpsAsync(data.Snapshot with { Filter = previousFilter }, cancellationToken);
        var previousEnriched = EnrichFacts(previousFacts, previousOps);
        var previous = AnalyticsProjector.Compute(previousEnriched, data.Configuration).Metrics;
        var previousRanking = DashboardAnalyticsProjector.Ranking(previousEnriched, brand, stats);
        var previousExposure = DashboardAnalyticsProjector.AggregateExposure(previousEnriched, data.Configuration, stats);
        return new BrandPeriodMetrics(current, previous, ranking, previousRanking, exposure, previousExposure,
            MonitoredPrompts(facts), MonitoredPrompts(previousEnriched));
    }

    /// <summary>区间内有合格回答（与 AnalyticsProjector.Compute 的合格条件一致）的去重提问数。</summary>
    private static int MonitoredPrompts(IReadOnlyList<AnalyticsCheckFact> facts)
        => facts.Where(f => f.Status == "succeeded" && f.AnswerPresent && f.AnalysisStatus == "completed" && !f.ConfigurationMissing)
            .Select(f => f.PromptId).Distinct().Count();
}
