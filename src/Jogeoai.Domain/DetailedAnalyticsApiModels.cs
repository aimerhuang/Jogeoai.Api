namespace Jogeoai.Domain;

/// <summary>Fan-Out 计数和可用率；比率均为 0–1，零分母为 null。</summary>
/// <param name="QueryEvents">去重后的扩展搜索词事件数 Q。</param>
/// <param name="ChecksWithExpansion">有扩展搜索词的 Check 数 F。</param>
/// <param name="SuccessfulChecks">成功采集的 Check 数 C。</param>
/// <param name="EligibleChecks">成功、有回答且分析完成的 Check 数 N。</param>
/// <param name="DistinctQueries">不同扩展搜索词数。</param>
/// <param name="QueriesPerExpandedCheck">Q 除以 F。</param>
/// <param name="QueriesPerSuccessfulCheck">Q 除以 C。</param>
/// <param name="ExpansionRate">F 除以 C。</param>
/// <param name="ExposedQueryRate">搜索词可用状态为 available 的 Check 数除以 C。</param>
/// <param name="AssociatedBrandMentionRate">关联回答中品牌命中的比例；不表示因果关系。</param>
/// <param name="AvailableChecks">返回非空搜索词的 Check 数。</param>
/// <param name="EmptyChecks">明确返回空搜索词数组的 Check 数。</param>
/// <param name="NotExposedChecks">未暴露搜索词字段的 Check 数。</param>
/// <param name="UnknownChecks">搜索词可用状态未知的 Check 数。</param>
/// <param name="PendingDetailedChecks">细分分析仍待完成的 Check 数。</param>
public sealed record FanOutMetrics(long QueryEvents, long ChecksWithExpansion, long SuccessfulChecks,
    long EligibleChecks, long DistinctQueries, MetricRatio QueriesPerExpandedCheck, MetricRatio QueriesPerSuccessfulCheck,
    MetricRatio ExpansionRate, MetricRatio ExposedQueryRate, MetricRatio AssociatedBrandMentionRate,
    long AvailableChecks, long EmptyChecks, long NotExposedChecks, long UnknownChecks,
    long PendingDetailedChecks);

/// <summary>按引擎计算的搜索词事件与可用性。</summary>
/// <param name="Engine">采集引擎。</param>
/// <param name="Metrics">该引擎的 Fan-Out 指标。</param>
public sealed record FanOutEngineView(string Engine, FanOutMetrics Metrics);

/// <summary>搜索词页面的总体指标和引擎比较。</summary>
/// <param name="Meta">统一筛选、版本和快照水位。</param>
/// <param name="Metrics">全部匹配 Check 的 Fan-Out 指标。</param>
/// <param name="Engines">按引擎拆分的指标。</param>
/// <param name="QueryVersion">搜索词规范化规则版本。</param>
/// <param name="TokenizerVersion">词项分词规则版本。</param>
public sealed record FanOutView(AnalyticsMeta Meta, FanOutMetrics Metrics,
    IReadOnlyList<FanOutEngineView> Engines, string QueryVersion, string TokenizerVersion);

/// <summary>一个去重搜索词的覆盖数和下钻入口。</summary>
/// <param name="QueryHash">规范化搜索词的 SHA-256 摘要。</param>
/// <param name="Query">首次观测到的原始搜索词。</param>
/// <param name="Events">关联的 Check 搜索词事件数。</param>
/// <param name="CheckCount">覆盖的不同 Check 数。</param>
/// <param name="PromptCount">覆盖的不同 Prompt 数。</param>
/// <param name="AssociatedBrandMentionRate">关联有效回答的品牌提及率。</param>
/// <param name="FirstSeen">首次观测的采集时间。</param>
/// <param name="LastSeen">最近观测的采集时间。</param>
/// <param name="EvidencePath">携带快照令牌的贡献 Check 下钻路径。</param>
public sealed record FanOutQueryRow(string QueryHash, string Query, long Events, long CheckCount,
    long PromptCount, MetricRatio AssociatedBrandMentionRate, DateTimeOffset? FirstSeen,
    DateTimeOffset? LastSeen, string EvidencePath);

/// <summary>某 Prompt 对一个搜索词的贡献。</summary>
/// <param name="PromptId">Prompt 稳定标识。</param>
/// <param name="PromptVersionId">采集时的 Prompt 版本标识。</param>
/// <param name="PromptSnapshot">采集时的问题正文。</param>
/// <param name="CheckCount">贡献的不同 Check 数。</param>
/// <param name="RecentCheckIds">最多一百个贡献 Check 标识。</param>
/// <param name="HasMoreChecks">是否还有未列出的 Check。</param>
public sealed record FanOutPromptVariant(Guid PromptId, Guid? PromptVersionId, string PromptSnapshot,
    long CheckCount, IReadOnlyList<Guid> RecentCheckIds, bool HasMoreChecks);

/// <summary>搜索词详情及其 Prompt 变体。</summary>
/// <param name="Meta">统一筛选、版本和快照水位。</param>
/// <param name="Query">该搜索词的聚合指标。</param>
/// <param name="Prompts">关联的 Prompt 及版本。</param>
/// <param name="RecentCheckIds">最多一百个贡献 Check 标识。</param>
/// <param name="HasMoreChecks">是否还有未列出的 Check。</param>
/// <param name="TokenizerAvailability">词项分析可用状态。</param>
/// <param name="TokenizerVersion">词项分词规则版本。</param>
/// <param name="Terms">搜索词相对 Prompt 的词项变化。</param>
public sealed record FanOutQueryDetail(AnalyticsMeta Meta, FanOutQueryRow Query,
    IReadOnlyList<FanOutPromptVariant> Prompts, IReadOnlyList<Guid> RecentCheckIds, bool HasMoreChecks,
    string TokenizerAvailability, string TokenizerVersion,
    IReadOnlyList<QueryTerm> Terms);

/// <summary>中英文词项的频次和相对 Prompt 的关系。</summary>
/// <param name="Term">词项文本。</param>
/// <param name="Relation">相对 Prompt 的新增、删除或保留关系。</param>
/// <param name="QueryEvents">包含该词项关系的搜索词事件数。</param>
/// <param name="CheckCount">覆盖的不同 Check 数。</param>
/// <param name="PromptCount">覆盖的不同 Prompt 数。</param>
/// <param name="TokenizerVersion">词项分词规则版本。</param>
public sealed record FanOutTermRow(string Term, string Relation, long QueryEvents,
    long CheckCount, long PromptCount, string TokenizerVersion);

/// <summary>词项列表及无法分词的 Check 数。</summary>
/// <param name="Meta">统一筛选、版本和快照水位。</param>
/// <param name="TokenizerVersion">词项分词规则版本。</param>
/// <param name="UnsupportedLocaleChecks">语言不支持词项分析的不同 Check 数。</param>
/// <param name="Items">本页词项及频次。</param>
/// <param name="NextCursor">下一页游标；末页为空。</param>
public sealed record FanOutTermsView(AnalyticsMeta Meta, string TokenizerVersion,
    long UnsupportedLocaleChecks, IReadOnlyList<FanOutTermRow> Items, string? NextCursor);

/// <summary>引用来源或页面类型的次数和占比。</summary>
/// <param name="Value">来源分类或页面类型机器值。</param>
/// <param name="CitationEvents">此类外部引用事件数。</param>
/// <param name="Share">此类事件占全部外部引用事件的比例。</param>
public sealed record CitationDistributionRow(string Value, long CitationEvents, MetricRatio Share);

/// <summary>按站点时区汇总的一日外部引用事件。</summary>
/// <param name="Date">工作区时区的本地日期。</param>
/// <param name="CitationEvents">当日外部引用事件数。</param>
/// <param name="CheckCount">当日有外部引用的不同 Check 数。</param>
public sealed record CitationDayView(DateOnly Date, long CitationEvents, long CheckCount);

/// <summary>外部引用指标、分类分布及趋势。</summary>
/// <param name="Meta">统一筛选、版本和快照水位。</param>
/// <param name="ExternalCitationEvents">外部引用事件总数。</param>
/// <param name="ChecksWithCitations">有外部引用的不同 Check 数。</param>
/// <param name="PromptsWithCitations">有外部引用的不同 Prompt 数。</param>
/// <param name="OwnCitationRate">N 中有自有外部引用的 Check 比例；多来源时无单一核心率。</param>
/// <param name="SourceDistribution">来源分类分布。</param>
/// <param name="PageTypeDistribution">页面类型分布。</param>
/// <param name="Trend">按工作区日期汇总的趋势。</param>
/// <param name="UrlVersion">URL 规范化规则版本。</param>
/// <param name="ClassificationVersion">引用分类规则版本。</param>
/// <param name="GoogleInternalEvents">单列的 Google 内部链接事件数。</param>
/// <param name="PendingDetailedChecks">细分分析仍待完成的 Check 数。</param>
/// <param name="SuccessfulChecks">成功采集的 Check 数 C。</param>
/// <param name="EligibleChecks">成功、有回答且分析完成的 Check 数 N。</param>
/// <param name="OwnEventShare">自有域名引用事件占全部外部引用事件的比例。</param>
public sealed record CitationsView(AnalyticsMeta Meta, long ExternalCitationEvents,
    long ChecksWithCitations, long PromptsWithCitations, MetricRatio OwnCitationRate,
    IReadOnlyList<CitationDistributionRow> SourceDistribution,
    IReadOnlyList<CitationDistributionRow> PageTypeDistribution,
    IReadOnlyList<CitationDayView> Trend, string UrlVersion, string ClassificationVersion,
    long GoogleInternalEvents, long PendingDetailedChecks,
    long SuccessfulChecks, long EligibleChecks, MetricRatio OwnEventShare);

/// <summary>外部引用 URL 榜的一行。</summary>
/// <param name="UrlHash">规范化 URL 的 SHA-256 摘要。</param>
/// <param name="Url">规范化的公开 URL。</param>
/// <param name="Domain">URL 主机域名。</param>
/// <param name="Title">最近观测到的非空引用标题。</param>
/// <param name="CitationEvents">此 URL 的外部引用事件数。</param>
/// <param name="Share">此 URL 事件占全部外部引用事件的比例。</param>
/// <param name="CheckCount">覆盖的不同 Check 数。</param>
/// <param name="PromptCount">覆盖的不同 Prompt 数。</param>
/// <param name="FirstSeen">首次观测的采集时间。</param>
/// <param name="LastSeen">最近观测的采集时间。</param>
/// <param name="Ownership">主体归属类型。</param>
/// <param name="SubjectId">匹配的品牌或竞品标识。</param>
/// <param name="SourceCategory">来源分类机器值。</param>
/// <param name="PageType">页面类型机器值。</param>
/// <param name="RuleVersion">引用分类规则版本。</param>
/// <param name="RuleBasis">分类命中依据。</param>
/// <param name="EvidencePath">携带快照令牌的贡献 Check 下钻路径。</param>
public sealed record CitationUrlRow(string UrlHash, string Url, string Domain, string? Title,
    long CitationEvents, MetricRatio Share, long CheckCount, long PromptCount,
    DateTimeOffset? FirstSeen, DateTimeOffset? LastSeen, string Ownership, Guid? SubjectId,
    string SourceCategory, string PageType, string RuleVersion, string RuleBasis, string EvidencePath);

/// <summary>外部引用域名榜的一行。</summary>
/// <param name="Domain">URL 主机域名。</param>
/// <param name="CitationEvents">此域名的外部引用事件数。</param>
/// <param name="Share">此域名事件占全部外部引用事件的比例。</param>
/// <param name="CheckCount">覆盖的不同 Check 数。</param>
/// <param name="PromptCount">覆盖的不同 Prompt 数。</param>
/// <param name="FirstSeen">首次观测的采集时间。</param>
/// <param name="LastSeen">最近观测的采集时间。</param>
/// <param name="Ownership">最近观测事件的主体归属类型。</param>
/// <param name="SourceCategory">最近观测事件的来源分类。</param>
/// <param name="EvidencePath">携带快照令牌的贡献 Check 下钻路径。</param>
public sealed record CitationDomainRow(string Domain, long CitationEvents, MetricRatio Share,
    long CheckCount, long PromptCount, DateTimeOffset? FirstSeen, DateTimeOffset? LastSeen,
    string Ownership, string SourceCategory, string EvidencePath);

/// <summary>引用 URL 或域名详情中的贡献 Check。</summary>
/// <param name="CheckId">贡献 Check 标识。</param>
/// <param name="PromptId">关联 Prompt 标识。</param>
/// <param name="RunId">关联 Run 标识。</param>
/// <param name="CapturedAt">实际采集时间。</param>
/// <param name="Url">规范化的公开 URL。</param>
/// <param name="Title">引用事件标题。</param>
/// <param name="Positions">原始引用的全部位置。</param>
/// <param name="CheckPath">携带快照令牌的 Check 详情路径。</param>
public sealed record CitationEvidenceRow(Guid CheckId, Guid PromptId, Guid RunId,
    DateTimeOffset? CapturedAt, string Url, string? Title,
    IReadOnlyList<int> Positions, string CheckPath);

/// <summary>引用 URL 详情及原始位置证据。</summary>
/// <param name="Meta">统一筛选、版本和快照水位。</param>
/// <param name="Url">URL 榜单聚合行。</param>
/// <param name="Evidence">最多五十个贡献 Check 的原始位置证据。</param>
/// <param name="HasMoreEvidence">是否还有未列出的贡献 Check。</param>
public sealed record CitationUrlDetail(AnalyticsMeta Meta, CitationUrlRow Url,
    IReadOnlyList<CitationEvidenceRow> Evidence, bool HasMoreEvidence);

/// <summary>引用域名详情及 URL 榜和原始位置证据。</summary>
/// <param name="Meta">统一筛选、版本和快照水位。</param>
/// <param name="Domain">域名榜单聚合行。</param>
/// <param name="Urls">最多一百个此域名的 URL 聚合行。</param>
/// <param name="HasMoreUrls">是否还有未列出的 URL。</param>
/// <param name="Evidence">最多五十个贡献 Check 的原始位置证据。</param>
/// <param name="HasMoreEvidence">是否还有未列出的贡献 Check。</param>
/// <param name="UrlListPath">携带相同快照和域名筛选的完整 URL 分页入口。</param>
public sealed record CitationDomainDetail(AnalyticsMeta Meta, CitationDomainRow Domain,
    IReadOnlyList<CitationUrlRow> Urls, bool HasMoreUrls,
    IReadOnlyList<CitationEvidenceRow> Evidence, bool HasMoreEvidence, string UrlListPath);

/// <summary>等长窗口中 URL 或域名的变化；零基数比率为 null。</summary>
/// <param name="Kind">变化对象类型：URL 或域名。</param>
/// <param name="Key">规范化 URL 或域名。</param>
/// <param name="Change">新增、消失、标题变化或次数变化类型。</param>
/// <param name="CurrentEvents">当前窗口的外部引用事件数。</param>
/// <param name="PreviousEvents">前一窗口的外部引用事件数。</param>
/// <param name="RelativeChange">相对前期的变化率；前期零基数为空。</param>
/// <param name="CurrentTitle">当前窗口最近非空标题。</param>
/// <param name="PreviousTitle">前一窗口最近非空标题。</param>
/// <param name="CheckIds">最多一百个前后窗口的贡献 Check 标识。</param>
/// <param name="CheckIdsTruncated">前后窗口的贡献 Check 超过一百个时为真。</param>
/// <param name="CurrentEvidencePath">当前窗口全部贡献 Check 的分页证据路径。</param>
/// <param name="PreviousEvidencePath">前一窗口全部贡献 Check 的分页证据路径。</param>
public sealed record CitationChangeRow(string Kind, string Key, string Change,
    long CurrentEvents, long PreviousEvents, double? RelativeChange,
    string? CurrentTitle, string? PreviousTitle, IReadOnlyList<Guid> CheckIds,
    bool CheckIdsTruncated, string CurrentEvidencePath, string PreviousEvidencePath);

/// <summary>引用变化、阈值与可回看的 Check。</summary>
/// <param name="Meta">统一筛选、版本和快照水位。</param>
/// <param name="PreviousFrom">前一等长窗口的包含起点。</param>
/// <param name="PreviousTo">前一等长窗口的不包含终点。</param>
/// <param name="MinimumEvents">显示变化的最少引用事件阈值。</param>
/// <param name="DeclineThreshold">显著下降比例阈值。</param>
/// <param name="Changes">URL 与域名变化行。</param>
/// <param name="NextCursor">下一页游标；没有更多变化时为空。</param>
public sealed record CitationChangesView(AnalyticsMeta Meta, DateTimeOffset PreviousFrom,
    DateTimeOffset PreviousTo, int MinimumEvents, double DeclineThreshold,
    IReadOnlyList<CitationChangeRow> Changes, string? NextCursor);

/// <summary>窗口内引用竞品且没有引用自有域名的 Prompt。</summary>
/// <param name="PromptId">Prompt 稳定标识。</param>
/// <param name="PromptSnapshot">采集时的问题快照。</param>
/// <param name="CompetitorCitationEvents">竞品外部引用事件数。</param>
/// <param name="CompetitorCheckIds">最多一百个有竞品引用的 Check 标识。</param>
/// <param name="CheckIds">最多一百个此 Prompt 的外部引用 Check 标识。</param>
/// <param name="EvidencePath">携带快照令牌的贡献 Check 下钻路径。</param>
public sealed record CompetitorCitationGapRow(Guid PromptId, string? PromptSnapshot,
    long CompetitorCitationEvents, IReadOnlyList<Guid> CompetitorCheckIds,
    IReadOnlyList<Guid> CheckIds, string EvidencePath);

/// <summary>Google 内部搜索或购物链接的解析结果。</summary>
/// <param name="CheckId">贡献 Check 标识。</param>
/// <param name="PromptId">关联 Prompt 标识。</param>
/// <param name="Url">规范化的 Google 内部 URL。</param>
/// <param name="Kind">搜索或购物链接类型。</param>
/// <param name="Query">从链接解析到的搜索词。</param>
/// <param name="Product">仅从已有标题取得的商品名称。</param>
/// <param name="Availability">可解析字段的可用状态。</param>
/// <param name="Ownership">由已冻结配置判定的主体归属。</param>
/// <param name="SubjectId">匹配的品牌或竞品标识。</param>
/// <param name="FirstPosition">此链接在原始引用中的首个位置。</param>
/// <param name="CheckPath">携带快照令牌的 Check 详情路径。</param>
public sealed record GoogleInternalRow(Guid CheckId, Guid PromptId, string Url, string Kind,
    string? Query, string? Product, string Availability, string? Ownership,
    Guid? SubjectId, int FirstPosition, string CheckPath);
