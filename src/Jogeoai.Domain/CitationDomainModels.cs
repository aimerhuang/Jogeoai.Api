using System.Text.Json.Serialization;

namespace Jogeoai.Domain;

/// <summary>网站类型，与当前品牌归属相互独立。</summary>
public enum CitationWebsiteCategory
{
    /// <summary>企业或品牌官网。</summary>
    [JsonStringEnumMemberName("corporate")] Corporate,
    /// <summary>新闻或媒体。</summary>
    [JsonStringEnumMemberName("news_media")] NewsMedia,
    /// <summary>社区或论坛。</summary>
    [JsonStringEnumMemberName("community_forum")] CommunityForum,
    /// <summary>百科或参考。</summary>
    [JsonStringEnumMemberName("reference")] Reference,
    /// <summary>政府或非营利组织。</summary>
    [JsonStringEnumMemberName("government_nonprofit")] GovernmentNonprofit,
    /// <summary>博客或个人站点。</summary>
    [JsonStringEnumMemberName("blog_personal")] BlogPersonal,
    /// <summary>教育。</summary>
    [JsonStringEnumMemberName("education")] Education,
    /// <summary>视频。</summary>
    [JsonStringEnumMemberName("video")] Video,
    /// <summary>社交媒体。</summary>
    [JsonStringEnumMemberName("social_media")] SocialMedia,
    /// <summary>已识别的其他类型。</summary>
    [JsonStringEnumMemberName("other")] Other,
    /// <summary>依据不足，未分类。</summary>
    [JsonStringEnumMemberName("unclassified")] Unclassified
}

/// <summary>仅相对于当前监控品牌的归属。</summary>
public enum CitationOwnership
{
    /// <summary>冻结配置证明是当前品牌自有。</summary>
    [JsonStringEnumMemberName("owned")] Owned,
    /// <summary>配置可用，但不匹配当前品牌。</summary>
    [JsonStringEnumMemberName("non_owned")] NonOwned,
    /// <summary>缺少可靠配置或引用依据。</summary>
    [JsonStringEnumMemberName("unknown")] Unknown
}

/// <summary>保留分子、分母和不可用原因的引用比率。</summary>
/// <param name="Numerator">分子。</param>
/// <param name="Denominator">分母。</param>
/// <param name="Value">零到一的比率；分母为零时为空。</param>
/// <param name="UnavailableReason">不可用原因；可计算时为空。</param>
public sealed record CitationDomainRatio(long Numerator, long Denominator, double? Value, string? UnavailableReason)
{
    /// <summary>按已知分母创建比率，不把零分母显示为零。</summary>
    public static CitationDomainRatio Of(long numerator, long denominator)
        => new(numerator, denominator, denominator == 0 ? null : (double)numerator / denominator,
            denominator == 0 ? "ZERO_DENOMINATOR" : null);
}

/// <summary>新投影专属快照及规则版本，不改写旧指标版本。</summary>
/// <param name="SnapshotToken">同一母集的快照令牌。</param>
/// <param name="Filters">实际冻结的时间、模型和采集来源范围。</param>
/// <param name="AsOf">快照创建时间。</param>
/// <param name="ExpiresAt">快照到期时间。</param>
/// <param name="DataUpdatedAt">当前水位内最近分析完成时间。</param>
/// <param name="ReleaseId">不可变发布标识。</param>
/// <param name="AnalysisVersion">发布序号。</param>
/// <param name="SubjectConfigVersion">冻结品牌配置版本。</param>
/// <param name="ConfigurationOutdated">当前配置是否已不同于冻结配置。</param>
/// <param name="ProjectionVersion">汇总投影版本。</param>
/// <param name="WebsiteCategoryRulesVersion">网站分类规则版本。</param>
/// <param name="OwnershipRulesVersion">归属与主机规范化版本。</param>
/// <param name="PlaceholderRulesVersion">有限占位排除规则版本。</param>
/// <param name="UrlNormalizationVersion">发布使用的 URL 规范化版本。</param>
/// <param name="ArrivalWatermark">到达水位。</param>
/// <param name="AnalysisWatermark">分析完成水位。</param>
/// <param name="ScopeDescription">实际观测样本口径说明。</param>
/// <param name="PreviousFrom">「较上期」比较窗口的开始时间（含），即当前窗口之前紧邻的等长窗口。</param>
/// <param name="PreviousTo">「较上期」比较窗口的结束时间（不含），等于当前窗口开始时间。</param>
public sealed record CitationDomainMeta(string SnapshotToken, AnalyticsFilter Filters,
    DateTimeOffset AsOf, DateTimeOffset ExpiresAt, DateTimeOffset? DataUpdatedAt,
    Guid ReleaseId, int AnalysisVersion, int SubjectConfigVersion, bool ConfigurationOutdated,
    string ProjectionVersion, string WebsiteCategoryRulesVersion, string OwnershipRulesVersion,
    string PlaceholderRulesVersion, string UrlNormalizationVersion, long ArrivalWatermark,
    long AnalysisWatermark, string ScopeDescription, DateTimeOffset PreviousFrom, DateTimeOffset PreviousTo);

/// <summary>冻结品牌域名的归属依据。</summary>
/// <param name="BrandId">命中的当前品牌标识。</param>
/// <param name="BrandName">冻结品牌名称。</param>
/// <param name="ConfiguredDomain">规范化配置域名。</param>
/// <param name="DomainKind">primary_domain 或 additional_domain。</param>
/// <param name="IncludeSubdomains">冻结的普通子域名开关。</param>
/// <param name="SubjectConfigVersion">冻结配置版本。</param>
public sealed record CitationOwnershipBasis(Guid BrandId, string BrandName, string ConfiguredDomain,
    string DomainKind, bool IncludeSubdomains, int SubjectConfigVersion);

/// <summary>可追溯的网站类别依据。</summary>
/// <param name="RuleId">当前版本中稳定的规则标识。</param>
/// <param name="Description">中文规则说明，不代表人工认证。</param>
public sealed record CitationCategoryBasis(string RuleId, string Description);

/// <summary>同一完整母集的指标。</summary>
/// <param name="EligibleAnswers">合格回答数，包含零引用回答。</param>
/// <param name="CitationEvents">有效外部引用事件数。</param>
/// <param name="DistinctDomains">完整集合的不同规范化域名数。</param>
/// <param name="OwnedCitationEvents">已确认自有事件数。</param>
/// <param name="UnknownOwnershipEvents">归属待确认事件数。</param>
/// <param name="OwnedEventShare">已确认自有事件除以全部有效事件。</param>
/// <param name="DistinctUrls">完整集合有效外部引用事件中不同规范化 URL 的数量（排除非法、Google 内部与占位链接，同一回答内同一 URL 只计一次）。</param>
public sealed record CitationDomainMetrics(long EligibleAnswers, long CitationEvents, long DistinctDomains,
    long OwnedCitationEvents, long UnknownOwnershipEvents, CitationDomainRatio OwnedEventShare, long DistinctUrls);

/// <summary>完整十一类别中的一项，零值也返回。</summary>
/// <param name="WebsiteCategory">网站类别。</param>
/// <param name="CitationEvents">该类别有效事件数。</param>
/// <param name="EventShare">占全部有效事件的比例。</param>
public sealed record CitationCategoryDistribution(CitationWebsiteCategory WebsiteCategory,
    long CitationEvents, CitationDomainRatio EventShare);

/// <summary>互斥的回答排除计数及事件排除说明。</summary>
/// <param name="CandidateAnswers">快照时间、来源和模型范围内候选回答数。</param>
/// <param name="CollectionNotSucceeded">采集未成功数量。</param>
/// <param name="NoAnswer">采集成功但无非空回答数量。</param>
/// <param name="ConfigurationUnavailable">回答存在但配置不可用数量。</param>
/// <param name="AnalysisFailed">前述条件满足但分析失败数量。</param>
/// <param name="AnalysisPending">前述条件满足但分析尚未完成数量。</param>
/// <param name="ExcludedPlaceholderEvents">去重后排除的有限占位事件数。</param>
/// <param name="ExcludedInvalidEvents">非法事件排除数；历史事实不支持精确计数时为空。</param>
/// <param name="ExcludedGoogleInternalEvents">内部链接排除数；未加载内部事实时为空。</param>
/// <param name="Limitations">无法从历史事实重建的计数及口径限制说明。</param>
public sealed record CitationDomainQuality(long CandidateAnswers, long CollectionNotSucceeded,
    long NoAnswer, long ConfigurationUnavailable, long AnalysisFailed, long AnalysisPending,
    long ExcludedPlaceholderEvents, long? ExcludedInvalidEvents, long? ExcludedGoogleInternalEvents,
    IReadOnlyList<string> Limitations);

/// <summary>首页域名汇总，不受域名表筛选影响。</summary>
/// <param name="Meta">快照与规则版本。</param>
/// <param name="Metrics">完整母集指标。</param>
/// <param name="CategoryDistribution">完整十一类分布。</param>
/// <param name="Quality">排除计数及限制。</param>
public sealed record CitationDomainSummaryView(CitationDomainMeta Meta, CitationDomainMetrics Metrics,
    IReadOnlyList<CitationCategoryDistribution> CategoryDistribution, CitationDomainQuality Quality);

/// <summary>域名榜单行，覆盖率与事件占比使用各自真实分母。</summary>
/// <param name="Domain">规范化 ASCII 主机域名。</param>
/// <param name="WebsiteCategory">冻结规则得出的网站类型。</param>
/// <param name="CategoryBasis">网站类别依据。</param>
/// <param name="Ownership">当前品牌归属。</param>
/// <param name="OwnershipBasis">已确认自有的配置依据；未命中时为空。</param>
/// <param name="CitationEvents">该域名有效事件数。</param>
/// <param name="EventShare">该域名事件占全部事件比例。</param>
/// <param name="AnswerCount">至少引用该域名一次的不同合格回答数。</param>
/// <param name="AnswerCoverage">覆盖回答数除以全部合格回答数。</param>
/// <param name="Rank">在完整母集中的名次，从 1 开始；按引用事件降序、再按域名升序排列，筛选后不重新编号。</param>
/// <param name="PreviousRank">同一规则在前一等长窗口、相同模型范围下的名次；前期没有该域名或未计算时为空。</param>
public sealed record CitationDomainSummaryRow(string Domain, CitationWebsiteCategory WebsiteCategory,
    CitationCategoryBasis CategoryBasis, CitationOwnership Ownership, CitationOwnershipBasis? OwnershipBasis,
    long CitationEvents, CitationDomainRatio EventShare, long AnswerCount, CitationDomainRatio AnswerCoverage,
    int Rank, int? PreviousRank);

/// <summary>服务端筛选、稳定排序后的域名分页。</summary>
/// <param name="Meta">同一母集快照。</param>
/// <param name="Items">本页行。</param>
/// <param name="NextCursor">绑定快照与筛选的下一页游标。</param>
/// <param name="FilteredDomainCount">分页前筛选后的完整域名数。</param>
public sealed record CitationDomainSummaryPage(CitationDomainMeta Meta,
    IReadOnlyList<CitationDomainSummaryRow> Items, string? NextCursor, long FilteredDomainCount);

/// <summary>引用文章（URL）榜单行，与域名汇总共用同一快照与完整母集。</summary>
/// <param name="Rank">在完整母集中的名次，从 1 开始；按引用事件降序、再按 URL 摘要升序排列。传 engine 时按该平台的事件在完整母集中重新排定；domain、q、mine 筛选后都不重新编号。</param>
/// <param name="Url">发布版本规范化后的公共 URL。</param>
/// <param name="UrlHash">规范化 URL 的 SHA-256 十六进制小写摘要，与详细分析事件键一致。</param>
/// <param name="Domain">该 URL 的规范化 ASCII 主机域名，与域名榜的 domain 相同。</param>
/// <param name="Title">同一快照窗口内最近一次观测到的非空标题；未观测到时为空，前端回退显示 URL。</param>
/// <param name="CitationEvents">该 URL 的有效事件数（同一回答内只计一次）；传 engine 时只统计该平台。</param>
/// <param name="EventShare">该 URL 事件占全部有效事件的比例；传 engine 时分子分母都只统计该平台。</param>
/// <param name="AnswerCount">至少引用该 URL 一次的不同合格回答数；传 engine 时只统计该平台。</param>
/// <param name="IsMine">该 URL 的规范化摘要命中本品牌当前有效（status=active）的内容管理发布链接，即「我的文章」；与窗口内是否有合格引用无关。</param>
/// <param name="MyCitationEvents">「我的引用次数」：同一回答与 URL 摘要命中本品牌发布链接，且满足文章监控相同的发布时间、停用窗口和监控范围门槛的有效事件数；传 engine 时只统计该平台。</param>
/// <param name="PromptCount">引用该 URL 的合格回答中不同提问的数量；传 engine 时只统计该平台。</param>
/// <param name="DomainRank">域名内名次：传 domain 时为该 URL 在该域名全部 URL 中的位置（从 1 开始连续），排序同 rank（引用事件降序、URL 摘要升序），在 engine 过滤之后、q 与 mine 过滤之前排定，q、mine 不重新编号；未传 domain 时为空。</param>
public sealed record CitationUrlSummaryRow(int Rank, string Url, string UrlHash, string Domain, string? Title,
    long CitationEvents, CitationDomainRatio EventShare, long AnswerCount, bool IsMine, long MyCitationEvents,
    long PromptCount, long? DomainRank);

/// <summary>服务端筛选、稳定排序后的引用文章（URL）分页。</summary>
/// <param name="Meta">同一母集快照。</param>
/// <param name="Items">本页行。</param>
/// <param name="NextCursor">绑定快照与 domain、engine、q、mine 筛选的下一页游标；没有更多时为空。</param>
/// <param name="FilteredUrlCount">分页前全部筛选（domain、engine、q、mine）之后的完整 URL 数；未传任何筛选时等于汇总的不同 URL 数。</param>
public sealed record CitationUrlSummaryPage(CitationDomainMeta Meta,
    IReadOnlyList<CitationUrlSummaryRow> Items, string? NextCursor, long FilteredUrlCount);

/// <summary>单个引用域名在同一快照内的平台分布行。</summary>
/// <param name="Engine">实际引擎代码（小写）。</param>
/// <param name="CitationEvents">该平台上该域名的有效事件数，口径与域名榜 citationEvents 相同。</param>
/// <param name="MyCitationEvents">该平台上满足门槛的「我的引用次数」。</param>
public sealed record CitationDomainEngineRow(string Engine, long CitationEvents, long MyCitationEvents);

/// <summary>单个引用域名按快照时区逐日的引用趋势点；窗口内每天一行，无数据补零。</summary>
/// <param name="Date">快照时区下的本地日期。</param>
/// <param name="CitationEvents">当日有效事件数（传 engine 时只统计该平台）。</param>
/// <param name="MyCitationEvents">当日满足门槛的「我的引用次数」（传 engine 时只统计该平台）。</param>
public sealed record CitationDomainTrendPoint(DateOnly Date, long CitationEvents, long MyCitationEvents);

/// <summary>单个引用域名在快照内的详情：总数、平台分布与逐日趋势，全部来自与域名榜相同的快照和母集。</summary>
/// <param name="Meta">同一母集快照与规则版本；时间窗口见 filters.from/to/timeZone。</param>
/// <param name="Domain">规范化 ASCII 主机域名（与域名榜 domain 相同）。</param>
/// <param name="Engine">请求的平台过滤（小写）；为空表示全部平台。只影响 trend。</param>
/// <param name="CitationEvents">该域名全部平台的有效事件数，等于域名榜该行 citationEvents；不受 engine 影响，快照内没有该域名时为 0。</param>
/// <param name="MyCitationEvents">该域名全部平台满足门槛的「我的引用次数」；不受 engine 影响。</param>
/// <param name="Engines">有引用的各平台分布，按 citationEvents 降序、engine 升序；不受 engine 影响，没有引用时为空。</param>
/// <param name="Trend">按快照时区逐日的趋势，窗口内每天一行、无数据补零；传 engine 时只统计该平台。</param>
public sealed record CitationDomainDetailView(CitationDomainMeta Meta, string Domain, string? Engine,
    long CitationEvents, long MyCitationEvents, IReadOnlyList<CitationDomainEngineRow> Engines,
    IReadOnlyList<CitationDomainTrendPoint> Trend);

/// <summary>一个真实的回答与规范化 URL 贡献事件。</summary>
/// <param name="CheckId">贡献回答标识。</param>
/// <param name="PromptId">历史问题标识。</param>
/// <param name="PromptSnapshot">已保存的历史问题摘要，不超过二百字符。</param>
/// <param name="Engine">实际引擎代码。</param>
/// <param name="CapturedAt">实际采集时间。</param>
/// <param name="NormalizedUrl">发布规范化后的公共 URL。</param>
/// <param name="FirstPosition">事实能证明的首个原始位置，完整位置见原详情。</param>
public sealed record CitationDomainEvidenceItem(Guid CheckId, Guid PromptId, string PromptSnapshot,
    string Engine, DateTimeOffset? CapturedAt, string NormalizedUrl, int FirstPosition);

/// <summary>域名内的真实引用贡献，按 URL 与回答稳定排序。</summary>
/// <param name="Meta">同一母集快照。</param>
/// <param name="Items">本页事件。</param>
/// <param name="NextCursor">绑定快照及域名的游标。</param>
/// <param name="DomainTotals">该域名完整指标；不存在时为空。</param>
public sealed record CitationDomainEvidencePage(CitationDomainMeta Meta,
    IReadOnlyList<CitationDomainEvidenceItem> Items, string? NextCursor, CitationDomainSummaryRow? DomainTotals);

/// <summary>与原引用数组逐索引对齐的归属，不改写原链接或声明域名。</summary>
/// <param name="RawCitationIndex">原始引用数组中的零基索引。</param>
/// <param name="CanonicalHost">从合法 URL 解析的规范化域名。</param>
/// <param name="Ownership">冻结品牌归属。</param>
/// <param name="Basis">自有命中依据。</param>
/// <param name="UnavailableReason">不可用原因；声明域名冲突时说明按 URL 主机判断。</param>
public sealed record CitationAttributionItem(int RawCitationIndex, string? CanonicalHost,
    CitationOwnership Ownership, CitationOwnershipBasis? Basis, string? UnavailableReason);

/// <summary>原始回答引用的可选冻结归属补充。</summary>
/// <param name="Meta">投影快照；能力不可用时为空。</param>
/// <param name="Availability">available 或 unavailable。</param>
/// <param name="Items">原数组逐索引归属。</param>
/// <param name="UnavailableReason">整体不可用原因；可用时为空。</param>
public sealed record CitationAttributionView(CitationDomainMeta? Meta, string Availability,
    IReadOnlyList<CitationAttributionItem> Items, string? UnavailableReason);

/// <summary>引用某个 URL 的单个监测问题。</summary>
/// <param name="PromptId">监测问题标识。</param>
/// <param name="Prompt">该问题在引用该 URL 的回答中最近一次观测（采集时间最新）的问题快照文本；没有快照时为空字符串。</param>
/// <param name="CitationEvents">该问题下引用该 URL 的有效事件数（同一回答内同一 URL 只计一次）。</param>
public sealed record CitationUrlPromptRow(Guid PromptId, string Prompt, long CitationEvents);

/// <summary>按提问分组汇总的 URL 引用问题。</summary>
/// <param name="GroupId">提问分组标识；为空表示「未分组」（问题没有分组或已被删除）。</param>
/// <param name="Name">提问分组名称；为空表示「未分组」。</param>
/// <param name="CitationEvents">该分组下各问题有效事件数之和。</param>
/// <param name="Prompts">该分组下引用了该 URL 的问题，按 citationEvents 降序、再按问题文本与问题标识升序。</param>
public sealed record CitationUrlPromptGroup(Guid? GroupId, string? Name, long CitationEvents,
    IReadOnlyList<CitationUrlPromptRow> Prompts);

/// <summary>单个引用 URL 在同一快照内按提问分组的问题引用明细（「数据详情」）；统计口径与引用文章榜该行 citationEvents 相同。</summary>
/// <param name="Meta">同一母集快照与规则版本。</param>
/// <param name="UrlHash">规范化 URL 的 SHA-256 十六进制小写摘要（取自引用文章榜 urlHash）。</param>
/// <param name="Url">发布版本规范化后的公共 URL。</param>
/// <param name="Title">同一快照窗口内最近一次观测到的非空标题；未观测到时为空。</param>
/// <param name="Engine">请求的平台过滤（小写）；为空表示全部平台。</param>
/// <param name="CitationEvents">该 URL 的有效事件数（传 engine 时只统计该平台），等于各分组 citationEvents 之和，也等于同快照、同 engine 下引用文章榜该行的 citationEvents。</param>
/// <param name="GroupCount">分组数（含「未分组」）。</param>
/// <param name="PromptCount">引用了该 URL 的不同监测问题数。</param>
/// <param name="Groups">分组明细：按 citationEvents 降序、再按分组名称与标识升序，「未分组」始终排最后；没有引用时为空。</param>
public sealed record CitationUrlPromptBreakdown(CitationDomainMeta Meta, string UrlHash, string Url, string? Title,
    string? Engine, long CitationEvents, int GroupCount, int PromptCount, IReadOnlyList<CitationUrlPromptGroup> Groups);
