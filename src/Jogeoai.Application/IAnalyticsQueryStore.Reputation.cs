using Jogeoai.Domain;

namespace Jogeoai.Application;

public partial interface IAnalyticsQueryStore
{
    /// <summary>
    /// 读取品牌口碑聚合：KPI、环比、趋势、平台、问题、热力图、印象词和质量来自同一快照；
    /// 038 未执行时抛出 503 REPUTATION_SCHEMA_NOT_MIGRATED。
    /// </summary>
    /// <param name="tenantId">当前租户标识。</param>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="request">统一分析筛选。</param>
    /// <param name="snapshotToken">复用的分析快照令牌。</param>
    /// <param name="view">range 或 day；空白按 range。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<ReputationSummaryView> ReputationAsync(Guid tenantId, Guid siteId, AnalyticsFilterRequest request,
        string? snapshotToken, string? view, CancellationToken cancellationToken = default);

    /// <summary>在分页前同时应用历史问题、品牌提及、被引域名、口碑状态、印象词与词极性筛选；游标与总数都绑定这六项。</summary>
    /// <param name="tenantId">当前租户标识。</param>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="request">列表筛选条件。</param>
    /// <param name="snapshotToken">复用的列表快照令牌。</param>
    /// <param name="cursor">分页游标。</param>
    /// <param name="limit">每页数量。</param>
    /// <param name="search">历史问题搜索。</param>
    /// <param name="mention">品牌提及筛选。</param>
    /// <param name="citedDomain">被引域名筛选。</param>
    /// <param name="sentiment">口碑状态筛选：positive、neutral、negative、unknown、not_mentioned、pending、failed、not_applicable；空白表示全部。</param>
    /// <param name="term">印象词筛选，按与印象词相同的规范化词键精确匹配；空白表示全部。</param>
    /// <param name="termPolarity">词极性筛选：positive、neutral、negative，只保留该词以此极性出现的回答；只能与 term 一起使用，空白表示不限。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<MonitoringRecordPage> MonitoringRecordsAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, string? snapshotToken, string? cursor, int limit, string? search,
        string? mention, string? citedDomain, string? sentiment, string? term, string? termPolarity,
        CancellationToken cancellationToken = default);
}
