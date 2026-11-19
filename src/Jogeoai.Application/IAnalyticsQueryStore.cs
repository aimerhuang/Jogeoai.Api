using Jogeoai.Domain;

namespace Jogeoai.Application;

/// <summary>统一筛选、快照和 Check 级指标查询。</summary>
public partial interface IAnalyticsQueryStore
{
    /// <summary>读取模型监控摘要。</summary>
    Task<MonitoringSummaryView> MonitoringSummaryAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, string? snapshotToken, CancellationToken cancellationToken = default);

    /// <summary>读取模型监控卡片。</summary>
    Task<IReadOnlyList<MonitoringModelView>> MonitoringModelsAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, string? snapshotToken, CancellationToken cancellationToken = default);

    /// <summary>分页读取最近检测记录；每行附单条回答的品牌排名与曝光分数，并返回同一快照与筛选下的记录总数。</summary>
    Task<MonitoringRecordPage> MonitoringRecordsAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, string? snapshotToken, string? cursor, int limit,
        CancellationToken cancellationToken = default);

    /// <summary>在分页前按历史问题快照进行字面子串搜索，游标绑定规范化搜索值。</summary>
    /// <param name="tenantId">当前租户标识。</param>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="request">列表筛选条件。</param>
    /// <param name="snapshotToken">复用的列表快照令牌。</param>
    /// <param name="cursor">绑定快照与搜索的分页游标。</param>
    /// <param name="limit">每页数量。</param>
    /// <param name="search">去首尾空白、忽略大小写的历史问题搜索，最多二百字符。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<MonitoringRecordPage> MonitoringRecordsAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, string? snapshotToken, string? cursor, int limit, string? search,
        CancellationToken cancellationToken = default);

    /// <summary>在分页前同时应用历史问题与品牌提及筛选；游标绑定规范化搜索和提及状态。</summary>
    /// <param name="tenantId">当前租户标识。</param>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="request">列表筛选条件。</param>
    /// <param name="snapshotToken">复用的列表快照令牌。</param>
    /// <param name="cursor">绑定快照、搜索与提及状态的分页游标。</param>
    /// <param name="limit">每页数量。</param>
    /// <param name="search">去首尾空白、忽略大小写的历史问题搜索，最多二百字符。</param>
    /// <param name="mention">品牌提及筛选：mentioned、not_mentioned、unknown；空白表示全部。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<MonitoringRecordPage> MonitoringRecordsAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, string? snapshotToken, string? cursor, int limit, string? search,
        string? mention, CancellationToken cancellationToken = default);

    /// <summary>在分页前同时应用历史问题、品牌提及与被引域名筛选；游标与总数都绑定这三项。</summary>
    /// <param name="tenantId">当前租户标识。</param>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="request">列表筛选条件。</param>
    /// <param name="snapshotToken">复用的列表快照令牌。</param>
    /// <param name="cursor">绑定快照、搜索、提及状态与被引域名的分页游标。</param>
    /// <param name="limit">每页数量。</param>
    /// <param name="search">去首尾空白、忽略大小写的历史问题搜索，最多二百字符。</param>
    /// <param name="mention">品牌提及筛选：mentioned、not_mentioned、unknown；空白表示全部。</param>
    /// <param name="citedDomain">被引域名（规范化主机，取自域名榜 domain）：只保留至少有一条有效引用事件属于该域名的回答，口径与引用域名汇总相同；空白表示全部，非法返回 INVALID_FILTER。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<MonitoringRecordPage> MonitoringRecordsAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, string? snapshotToken, string? cursor, int limit, string? search,
        string? mention, string? citedDomain, CancellationToken cancellationToken = default);

    /// <summary>读取当前租户和品牌下的规范化回答；必须复用列表快照，筛选仅决定列表成员资格。</summary>
    /// <param name="tenantId">当前租户标识。</param>
    /// <param name="siteId">当前品牌工作区标识。</param>
    /// <param name="checkId">检测标识。</param>
    /// <param name="snapshotToken">必填的列表快照令牌。</param>
    /// <param name="search">与列表相同的历史问题搜索条件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<MonitoringAnswerDetailView?> MonitoringAnswerDetailAsync(Guid tenantId, Guid siteId,
        Guid checkId, string? snapshotToken, string? search, CancellationToken cancellationToken = default);

    /// <summary>按列表快照读取完整回答与提及证据；提及筛选仅影响列表成员标志，不改变访问权限。</summary>
    /// <param name="tenantId">当前租户标识。</param>
    /// <param name="siteId">当前品牌工作区标识。</param>
    /// <param name="checkId">检测标识。</param>
    /// <param name="snapshotToken">必填的列表快照令牌。</param>
    /// <param name="search">与列表相同的历史问题搜索条件。</param>
    /// <param name="mention">与列表相同的提及状态：mentioned、not_mentioned、unknown；空白表示全部。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<MonitoringAnswerDetailView?> MonitoringAnswerDetailAsync(Guid tenantId, Guid siteId,
        Guid checkId, string? snapshotToken, string? search, string? mention,
        CancellationToken cancellationToken = default);

    /// <summary>读取品牌概览聚合。</summary>
    Task<BrandDashboardView> BrandDashboardAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, string? snapshotToken, CancellationToken cancellationToken = default);

    /// <summary>分页读取品牌概览分组。</summary>
    Task<AnalyticsPage<BrandDashboardGroup>> BrandDashboardBreakdownsAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, string? snapshotToken, string? cursor, int limit,
        CancellationToken cancellationToken = default);

    /// <summary>读取概览。</summary>
    Task<OverviewView> OverviewAsync(Guid tenantId, Guid siteId, AnalyticsFilterRequest request,
        string? snapshotToken, CancellationToken cancellationToken = default);

    /// <summary>读取可见性及切片。</summary>
    Task<VisibilityView> VisibilityAsync(Guid tenantId, Guid siteId, AnalyticsFilterRequest request,
        string? snapshotToken, CancellationToken cancellationToken = default);

    /// <summary>读取完整主体声量和环图 Top N。</summary>
    Task<ShareOfVoiceView> ShareOfVoiceAsync(Guid tenantId, Guid siteId, AnalyticsFilterRequest request,
        string? snapshotToken, int topN, CancellationToken cancellationToken = default);

    /// <summary>按稳定游标读取完整主体榜单。</summary>
    Task<AnalyticsPage<SubjectShareEntry>> LeaderboardAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, string? snapshotToken, string? cursor, int limit,
        CancellationToken cancellationToken = default);

    /// <summary>按稳定游标读取 Prompt 表现。</summary>
    Task<AnalyticsPage<PromptPerformanceView>> PromptsAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, string? snapshotToken, string? cursor, int limit,
        string? sort, CancellationToken cancellationToken = default);

    /// <summary>从不可变历史版本及同一分析快照读取 Prompt 聚合详情。</summary>
    Task<PromptDetailView> PromptDetailAsync(Guid tenantId, Guid siteId, Guid promptId,
        AnalyticsFilterRequest request, string? snapshotToken, CancellationToken cancellationToken = default);

    /// <summary>按主体或 Prompt 筛选贡献 Check。</summary>
    Task<AnalyticsPage<AnalyticsEvidenceRow>> EvidenceAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, string? snapshotToken, string? cursor, int limit,
        Guid? subjectId, Guid? promptId, CancellationToken cancellationToken = default);

    /// <summary>读取跨 Provider 回答及已发布分析证据。</summary>
    Task<AnalyticsCheckDetail?> CheckDetailAsync(Guid tenantId, Guid checkId,
        string? snapshotToken, CancellationToken cancellationToken = default);

    /// <summary>搜索词总体指标和引擎比较。</summary>
    Task<FanOutView> FanOutAsync(Guid tenantId, Guid siteId, AnalyticsFilterRequest request,
        string? snapshotToken, CancellationToken cancellationToken = default);

    /// <summary>按统一快照分页列出实际搜索词。</summary>
    Task<AnalyticsPage<FanOutQueryRow>> FanOutQueriesAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, string? snapshotToken, string? cursor, int limit,
        CancellationToken cancellationToken = default);

    /// <summary>搜索词详情、Prompt 变体及词项。</summary>
    Task<FanOutQueryDetail?> FanOutQueryDetailAsync(Guid tenantId, Guid siteId, string queryHash,
        AnalyticsFilterRequest request, string? snapshotToken, CancellationToken cancellationToken = default);

    /// <summary>分页列出中英文词项；无法分词语言返回可用性计数。</summary>
    Task<FanOutTermsView> FanOutTermsAsync(Guid tenantId, Guid siteId, AnalyticsFilterRequest request,
        string? snapshotToken, string? cursor, int limit, CancellationToken cancellationToken = default);

    /// <summary>按搜索词或 Prompt 下钻贡献 Check。</summary>
    Task<AnalyticsPage<AnalyticsEvidenceRow>> FanOutEvidenceAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, string? snapshotToken, string? cursor, int limit,
        string? queryHash, Guid? promptId, CancellationToken cancellationToken = default);

    /// <summary>外部引用总量、分类分布和每日趋势。</summary>
    Task<CitationsView> CitationsAsync(Guid tenantId, Guid siteId, AnalyticsFilterRequest request,
        string? snapshotToken, CancellationToken cancellationToken = default);

    /// <summary>分页列出引用 URL 榜单，可限定单个域名。</summary>
    Task<AnalyticsPage<CitationUrlRow>> CitationUrlsAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, string? snapshotToken, string? cursor, int limit, string? domain = null,
        CancellationToken cancellationToken = default);

    /// <summary>分页列出引用域名榜单。</summary>
    Task<AnalyticsPage<CitationDomainRow>> CitationDomainsAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, string? snapshotToken, string? cursor, int limit,
        CancellationToken cancellationToken = default);

    /// <summary>查看引用 URL 和原始位置证据。</summary>
    Task<CitationUrlDetail?> CitationUrlDetailAsync(Guid tenantId, Guid siteId, string urlHash,
        AnalyticsFilterRequest request, string? snapshotToken, CancellationToken cancellationToken = default);

    /// <summary>查看域名下的 URL 和原始位置证据。</summary>
    Task<CitationDomainDetail?> CitationDomainDetailAsync(Guid tenantId, Guid siteId, string domain,
        AnalyticsFilterRequest request, string? snapshotToken, CancellationToken cancellationToken = default);

    /// <summary>比较前后等长窗口的 URL、域名及标题变化。</summary>
    Task<CitationChangesView> CitationChangesAsync(Guid tenantId, Guid siteId, AnalyticsFilterRequest request,
        string? snapshotToken, int minimumEvents, double declineThreshold, string? cursor = null, int limit = 50,
        CancellationToken cancellationToken = default);

    /// <summary>分页列出仅引用竞品的 Prompt。</summary>
    Task<AnalyticsPage<CompetitorCitationGapRow>> CompetitorCitationGapsAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, string? snapshotToken, string? cursor, int limit,
        CancellationToken cancellationToken = default);

    /// <summary>分页读取已解析的 Google 内部搜索和购物链接。</summary>
    Task<AnalyticsPage<GoogleInternalRow>> GoogleInternalAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, string? snapshotToken, string? cursor, int limit,
        CancellationToken cancellationToken = default);

    /// <summary>按引用 URL、域名或 Prompt 下钻贡献 Check。</summary>
    Task<AnalyticsPage<AnalyticsEvidenceRow>> CitationEvidenceAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, string? snapshotToken, string? cursor, int limit,
        string? urlHash, string? domain, Guid? promptId, CancellationToken cancellationToken = default);
}
