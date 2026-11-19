using Jogeoai.Domain;

namespace Jogeoai.Application;

public partial interface IAnalyticsQueryStore
{
    /// <summary>读取单品牌本期与等长上一期的核心指标、平均排名、曝光分数与有合格回答的去重提问数；口径与品牌概览一致。</summary>
    Task<BrandPeriodMetrics> BrandPeriodMetricsAsync(Guid tenantId, Guid siteId, AnalyticsFilterRequest request,
        CancellationToken cancellationToken = default);
}
