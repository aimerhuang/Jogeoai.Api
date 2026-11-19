using Jogeoai.Domain;

namespace Jogeoai.Application;

public partial interface IAnalyticsQueryStore
{
    /// <summary>读取全来源统一合格母集的引用域名汇总。</summary>
    /// <param name="tenantId">授权租户。</param><param name="siteId">当前品牌。</param>
    /// <param name="request">时间、模型筛选；缺省来源包括国内与 Cloro。</param>
    /// <param name="snapshotToken">可选的已绑定投影快照。</param><param name="cancellationToken">取消令牌。</param>
    Task<CitationDomainSummaryView> CitationDomainSummaryAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, string? snapshotToken, CancellationToken cancellationToken = default);

    /// <summary>在全量域名中筛选、稳定排序后分页，不改变摘要分母。</summary>
    /// <param name="tenantId">授权租户。</param><param name="siteId">当前品牌。</param>
    /// <param name="snapshotToken">必填的摘要快照。</param><param name="q">最多二百字符的字面搜索。</param>
    /// <param name="websiteCategory">十一种类别之一或空。</param><param name="ownership">自有、非自有、待确认或空。</param>
    /// <param name="cursor">绑定筛选的游标。</param><param name="limit">一到一百条，默认二十。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<CitationDomainSummaryPage> CitationDomainSummaryDomainsAsync(Guid tenantId, Guid siteId,
        string? snapshotToken, string? q, string? websiteCategory, string? ownership,
        string? cursor, int limit, CancellationToken cancellationToken = default);

    /// <summary>分页返回当前域名的真实贡献事件；沿用摘要的同一母集。</summary>
    /// <param name="tenantId">授权租户。</param><param name="siteId">当前品牌。</param>
    /// <param name="snapshotToken">必填摘要快照。</param><param name="domain">规范化域名。</param>
    /// <param name="cursor">绑定域名的游标。</param><param name="limit">一到一百条，默认二十。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<CitationDomainEvidencePage> CitationDomainSummaryEvidenceAsync(Guid tenantId, Guid siteId,
        string? snapshotToken, string domain, string? cursor, int limit, CancellationToken cancellationToken = default);

    /// <summary>分页返回与域名汇总同一快照、同一完整母集的引用文章（URL）榜，可按完整域名筛选。</summary>
    /// <param name="tenantId">授权租户。</param><param name="siteId">当前品牌。</param>
    /// <param name="snapshotToken">必填摘要快照。</param><param name="domain">可选的规范化域名，只返回该完整域名下的 URL。</param>
    /// <param name="cursor">绑定快照与域名的游标。</param><param name="limit">一到一百条，默认五十。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<CitationUrlSummaryPage> CitationDomainSummaryUrlsAsync(Guid tenantId, Guid siteId,
        string? snapshotToken, string? domain, string? cursor, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// 分页返回引用文章（URL）榜，并可在指定域名内按单个平台、标题或 URL 搜索、仅我的文章筛选。
    /// 名次在平台过滤后、搜索与「仅我的」过滤前排定；行附带「我的文章」标识、我的引用次数和不同提问数。
    /// </summary>
    /// <param name="tenantId">授权租户。</param><param name="siteId">当前品牌。</param>
    /// <param name="snapshotToken">必填摘要快照。</param><param name="domain">可选的规范化域名；engine、q、mine 需与它一起传。</param>
    /// <param name="engine">可选的单个平台代码（规范化为小写）；快照限定平台时必须在其中。</param>
    /// <param name="q">可选的标题或 URL 字面包含搜索，忽略大小写，一到一百字符。</param>
    /// <param name="mine">可选；true 只返回命中本品牌有效发布链接的 URL。</param>
    /// <param name="cursor">绑定快照与全部筛选的游标。</param><param name="limit">一到一百条，默认五十。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<CitationUrlSummaryPage> CitationDomainSummaryUrlsAsync(Guid tenantId, Guid siteId,
        string? snapshotToken, string? domain, string? engine, string? q, bool? mine, string? cursor, int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 单个引用域名在同一快照内的详情：全部与我的引用次数、平台分布及按快照时区逐日补零的趋势；
    /// 全部次数与域名榜该行 citationEvents 同一口径，平台分布不受 engine 影响，趋势受其影响。
    /// </summary>
    /// <param name="tenantId">授权租户。</param><param name="siteId">当前品牌。</param>
    /// <param name="snapshotToken">必填摘要快照。</param><param name="domain">必填的规范化域名（取自域名榜 domain）。</param>
    /// <param name="engine">可选的单个平台代码，只作用于趋势；快照限定平台时必须在其中。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<CitationDomainDetailView> CitationDomainDetailAsync(Guid tenantId, Guid siteId,
        string? snapshotToken, string? domain, string? engine, CancellationToken cancellationToken = default);

    /// <summary>
    /// 单个引用 URL 在同一快照内按提问分组的问题引用明细：哪些监测问题引用了它、各多少次。
    /// 口径与引用文章榜该行 citationEvents 相同（同一回答内同一 URL 只计一次），各分组之和等于总数；
    /// 快照内没有该 URL 时返回 404 RESOURCE_NOT_FOUND。
    /// </summary>
    /// <param name="tenantId">授权租户。</param><param name="siteId">当前品牌。</param>
    /// <param name="snapshotToken">必填摘要快照。</param>
    /// <param name="urlHash">必填的 URL 摘要（取自引用文章榜 urlHash，六十四位十六进制）；格式错误返回 INVALID_FILTER。</param>
    /// <param name="engine">可选的单个平台代码（规范化为小写），只统计该平台；快照限定平台时必须在其中。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<CitationUrlPromptBreakdown> CitationUrlPromptsAsync(Guid tenantId, Guid siteId,
        string? snapshotToken, string? urlHash, string? engine, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按渠道读取引用域名汇总。channel 为 api（省略时的默认值）时来源集合与不带该参数的入口完全相同；
    /// 为 app 时只统计 App 渠道的回答（Provider app_collector、采集模式 app）。两个渠道不会出现在同一个快照里。
    /// </summary>
    /// <param name="tenantId">授权租户。</param><param name="siteId">当前品牌。</param>
    /// <param name="request">时间、模型筛选；来源集合由 channel 决定，调用方不能指定。</param>
    /// <param name="snapshotToken">可选的已绑定投影快照；它的渠道必须与 channel 一致，否则返回 INVALID_FILTER。</param>
    /// <param name="channel">api 或 app，忽略大小写；空值按 api；其他取值返回 INVALID_FILTER。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<CitationDomainSummaryView> CitationDomainSummaryAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, string? snapshotToken, string? channel, CancellationToken cancellationToken = default);

    /// <summary>按渠道分页读取域名榜；其余行为与不带 channel 的入口相同。</summary>
    /// <param name="tenantId">授权租户。</param><param name="siteId">当前品牌。</param>
    /// <param name="snapshotToken">必填的摘要快照；它的渠道必须与 channel 一致，否则返回 INVALID_FILTER。</param>
    /// <param name="q">最多二百字符的字面搜索。</param>
    /// <param name="websiteCategory">十一种类别之一或空。</param><param name="ownership">自有、非自有、待确认或空。</param>
    /// <param name="cursor">绑定筛选的游标。</param><param name="limit">一到一百条，默认二十。</param>
    /// <param name="channel">api 或 app，忽略大小写；空值按 api；其他取值返回 INVALID_FILTER。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<CitationDomainSummaryPage> CitationDomainSummaryDomainsAsync(Guid tenantId, Guid siteId,
        string? snapshotToken, string? q, string? websiteCategory, string? ownership,
        string? cursor, int limit, string? channel, CancellationToken cancellationToken = default);

    /// <summary>按渠道分页读取域名的贡献事件；其余行为与不带 channel 的入口相同。</summary>
    /// <param name="tenantId">授权租户。</param><param name="siteId">当前品牌。</param>
    /// <param name="snapshotToken">必填摘要快照；它的渠道必须与 channel 一致，否则返回 INVALID_FILTER。</param>
    /// <param name="domain">规范化域名。</param>
    /// <param name="cursor">绑定域名的游标。</param><param name="limit">一到一百条，默认二十。</param>
    /// <param name="channel">api 或 app，忽略大小写；空值按 api；其他取值返回 INVALID_FILTER。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<CitationDomainEvidencePage> CitationDomainSummaryEvidenceAsync(Guid tenantId, Guid siteId,
        string? snapshotToken, string domain, string? cursor, int limit, string? channel,
        CancellationToken cancellationToken = default);

    /// <summary>按渠道分页读取引用文章（URL）榜；其余行为与不带 channel 的入口相同。</summary>
    /// <param name="tenantId">授权租户。</param><param name="siteId">当前品牌。</param>
    /// <param name="snapshotToken">必填摘要快照；它的渠道必须与 channel 一致，否则返回 INVALID_FILTER。</param>
    /// <param name="domain">可选的规范化域名；engine、q、mine 需与它一起传。</param>
    /// <param name="engine">可选的单个平台代码（规范化为小写）；快照限定平台时必须在其中。</param>
    /// <param name="q">可选的标题或 URL 字面包含搜索，忽略大小写，一到一百字符。</param>
    /// <param name="mine">可选；true 只返回命中本品牌有效发布链接的 URL。</param>
    /// <param name="cursor">绑定快照与全部筛选的游标。</param><param name="limit">一到一百条，默认五十。</param>
    /// <param name="channel">api 或 app，忽略大小写；空值按 api；其他取值返回 INVALID_FILTER。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<CitationUrlSummaryPage> CitationDomainSummaryUrlsAsync(Guid tenantId, Guid siteId,
        string? snapshotToken, string? domain, string? engine, string? q, bool? mine, string? cursor, int limit,
        string? channel, CancellationToken cancellationToken = default);

    /// <summary>按渠道读取单个引用域名的详情；其余行为与不带 channel 的入口相同。</summary>
    /// <param name="tenantId">授权租户。</param><param name="siteId">当前品牌。</param>
    /// <param name="snapshotToken">必填摘要快照；它的渠道必须与 channel 一致，否则返回 INVALID_FILTER。</param>
    /// <param name="domain">必填的规范化域名（取自域名榜 domain）。</param>
    /// <param name="engine">可选的单个平台代码，只作用于趋势；快照限定平台时必须在其中。</param>
    /// <param name="channel">api 或 app，忽略大小写；空值按 api；其他取值返回 INVALID_FILTER。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<CitationDomainDetailView> CitationDomainDetailAsync(Guid tenantId, Guid siteId,
        string? snapshotToken, string? domain, string? engine, string? channel, CancellationToken cancellationToken = default);

    /// <summary>按渠道读取单个引用 URL 的问题引用明细；其余行为与不带 channel 的入口相同。</summary>
    /// <param name="tenantId">授权租户。</param><param name="siteId">当前品牌。</param>
    /// <param name="snapshotToken">必填摘要快照；它的渠道必须与 channel 一致，否则返回 INVALID_FILTER。</param>
    /// <param name="urlHash">必填的 URL 摘要（取自引用文章榜 urlHash，六十四位十六进制）；格式错误返回 INVALID_FILTER。</param>
    /// <param name="engine">可选的单个平台代码（规范化为小写），只统计该平台；快照限定平台时必须在其中。</param>
    /// <param name="channel">api 或 app，忽略大小写；空值按 api；其他取值返回 INVALID_FILTER。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<CitationUrlPromptBreakdown> CitationUrlPromptsAsync(Guid tenantId, Guid siteId,
        string? snapshotToken, string? urlHash, string? engine, string? channel, CancellationToken cancellationToken = default);

    /// <summary>兼容读取原回答，并按显式请求增补同快照引用归属。</summary>
    /// <param name="tenantId">授权租户。</param><param name="siteId">当前品牌。</param><param name="checkId">回答标识。</param>
    /// <param name="snapshotToken">必填快照。</param><param name="search">原列表搜索。</param><param name="mention">原提及筛选。</param>
    /// <param name="citationProjection">仅支持 domain-summary-v1。</param><param name="cancellationToken">取消令牌。</param>
    Task<MonitoringAnswerDetailView?> MonitoringAnswerDetailAsync(Guid tenantId, Guid siteId,
        Guid checkId, string? snapshotToken, string? search, string? mention, string citationProjection,
        CancellationToken cancellationToken = default);
}
