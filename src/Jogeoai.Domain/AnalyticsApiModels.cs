namespace Jogeoai.Domain;

/// <summary>统一分析筛选；省略时采用站点时区及最近 30 个当地自然日。</summary>
public sealed record AnalyticsFilterRequest(DateTimeOffset? From = null, DateTimeOffset? To = null,
    string? TimeZone = null, IReadOnlyList<string>? Providers = null, IReadOnlyList<string>? Engines = null,
    IReadOnlyList<string>? Countries = null, IReadOnlyList<string>? Locales = null,
    IReadOnlyList<string>? CaptureModes = null, IReadOnlyList<string>? EvidenceClasses = null,
    IReadOnlyList<Guid>? PromptIds = null, IReadOnlyList<Guid>? PromptVersionIds = null,
    IReadOnlyList<string>? Tags = null, string? TagMode = null, string? BrandedStatus = null,
    string? AggregationMode = null, IReadOnlyList<string>? ModelVersions = null,
    bool? ModelVersionUnavailable = null, IReadOnlyList<Guid>? SubjectIds = null,
    IReadOnlyList<string>? Statuses = null);

/// <summary>已规范化并冻结到快照的所有筛选条件。</summary>
public sealed record AnalyticsFilter(DateTimeOffset From, DateTimeOffset To, string TimeZone,
    IReadOnlyList<string> Providers, IReadOnlyList<string> Engines, IReadOnlyList<string> Countries,
    IReadOnlyList<string> Locales, IReadOnlyList<string> CaptureModes, IReadOnlyList<string> EvidenceClasses,
    IReadOnlyList<Guid> PromptIds, IReadOnlyList<Guid> PromptVersionIds, IReadOnlyList<string> Tags,
    string TagMode, string? BrandedStatus, string AggregationMode,
    IReadOnlyList<string> ModelVersions, bool? ModelVersionUnavailable,
    IReadOnlyList<Guid> SubjectIds, IReadOnlyList<string> Statuses);

/// <summary>每个分析响应共享的版本、筛选、时效和质量水位。</summary>
public sealed record AnalyticsMeta(string SnapshotToken, AnalyticsFilter Filters, string MetricVersion,
    int AnalysisVersion, int SubjectConfigVersion, bool ConfigurationOutdated,
    DateTimeOffset AsOf, DateTimeOffset ExpiresAt, DateTimeOffset? LastCapturedAt,
    DateTimeOffset? DataUpdatedAt, long ArrivalWatermark, long AnalysisWatermark,
    long PendingAnalysisChecks, long FailedAnalysisChecks, long MissingCapturedAtChecks);

/// <summary>与前一个等长窗口相比的比率百分点差。</summary>
public sealed record RatioComparison(MetricRatio Current, MetricRatio Previous,
    double? PercentagePointDifference, string? UnavailableReason);

/// <summary>Overview 页面所需的指标、趋势、引擎分布和质量状态。</summary>
public sealed record OverviewView(AnalyticsMeta Meta, CoreMetrics Metrics,
    IReadOnlyList<AnalyticsDayPoint> Trend, IReadOnlyDictionary<string, CoreMetrics> EngineDistribution,
    RatioComparison VisibilityComparison, RatioComparison ShareOfVoiceComparison,
    RatioComparison OwnCitationComparison,
    IReadOnlyDictionary<string, VoiceProjection>? SourceGroups = null,
    IReadOnlyDictionary<string, IReadOnlyList<AnalyticsDayPoint>>? SourceTrends = null);

/// <summary>按维度的可见性切片。</summary>
public sealed record VisibilitySlice(string Dimension, string Value, CoreMetrics Metrics);

/// <summary>实际观测或最近观测的可见性页面。</summary>
public sealed record VisibilityView(AnalyticsMeta Meta, CoreMetrics Metrics,
    IReadOnlyList<AnalyticsDayPoint> Trend, IReadOnlyList<VisibilitySlice> Slices,
    CoreMetrics ObservedWindowTotals,
    IReadOnlyDictionary<string, VoiceProjection>? SourceGroups = null,
    IReadOnlyDictionary<string, IReadOnlyList<AnalyticsDayPoint>>? SourceTrends = null);

/// <summary>一份分母支撑卡片、环图、趋势和全榜的声量页面。</summary>
public sealed record ShareOfVoiceView(AnalyticsMeta Meta, CoreMetrics Metrics,
    IReadOnlyList<SubjectShareEntry> Entries, IReadOnlyList<SubjectShareEntry> TopEntries,
    MetricRatio Others, IReadOnlyList<AnalyticsDayPoint> Trend, CoreMetrics ObservedWindowTotals,
    IReadOnlyDictionary<string, VoiceProjection>? SourceGroups = null,
    IReadOnlyDictionary<string, IReadOnlyList<AnalyticsDayPoint>>? SourceTrends = null);

/// <summary>Prompt 的单个版本在单一来源（provider|captureMode|evidenceClass）下、筛选窗口内的表现；同一 Prompt 跨版本或来源返回多行，汇总时按分子与分母分别相加。</summary>
public sealed record PromptPerformanceView(Guid PromptId, string? PromptSnapshot, long CheckCount,
    long EligibleChecks, long BrandMentions, MetricRatio Visibility,
    MetricRatio AnyCompetitorMentionRate, MetricRatio OwnCitationRate,
    DateTimeOffset? LastCapturedAt, long MissingSnapshotChecks, Guid? PromptVersionId = null,
    string? Source = null);

/// <summary>Prompt 历史版本保存的标签名称快照。</summary>
/// <param name="TagId">标签稳定标识。</param>
/// <param name="Name">该版本创建时的标签名称。</param>
public sealed record PromptVersionTagView(Guid TagId, string Name);

/// <summary>从不可变版本表读取的问题正文、语言和分类。</summary>
/// <param name="VersionId">历史版本标识。</param>
/// <param name="Version">版本序号。</param>
/// <param name="Content">当时的问题正文。</param>
/// <param name="Locale">当时的语言标识。</param>
/// <param name="BrandedStatus">当时的品牌词分类。</param>
/// <param name="BrandedSource">分类来源。</param>
/// <param name="Tags">当时的标签名称快照。</param>
/// <param name="CreatedAt">版本创建 UTC 时间。</param>
public sealed record PromptVersionHistoryView(Guid VersionId, int Version, string Content, string Locale,
    string BrandedStatus, string BrandedSource, IReadOnlyList<PromptVersionTagView> Tags,
    DateTimeOffset CreatedAt);

/// <summary>同一来源及引擎的 Prompt Check 指标。</summary>
/// <param name="Source">Provider、采集模式和证据类别。</param>
/// <param name="Engine">回答引擎。</param>
/// <param name="Metrics">该组 Check 的指标。</param>
public sealed record PromptEnginePerformanceView(string Source, string Engine, CoreMetrics Metrics);

/// <summary>Prompt 在一次 Run 中的 Check 摘要。</summary>
/// <param name="RunId">运行标识。</param>
/// <param name="Source">Provider、采集模式和证据类别。</param>
/// <param name="Metrics">该 Run 的指标。</param>
/// <param name="LastCapturedAt">最后一次实际采集时间。</param>
public sealed record PromptRunHistoryView(Guid RunId, string Source, CoreMetrics Metrics,
    DateTimeOffset? LastCapturedAt);

/// <summary>Provider 返回的原始搜索词及覆盖 Check 数。</summary>
/// <param name="Query">原始搜索词。</param>
/// <param name="CheckCount">返回此搜索词的不同 Check 数。</param>
public sealed record PromptSearchQuerySummaryView(string Query, long CheckCount);

/// <summary>Prompt 已入库引用域名及对应引用和 Check 数。</summary>
/// <param name="Domain">引用域名。</param>
/// <param name="CitationCount">引用条数。</param>
/// <param name="CheckCount">有该域名引用的不同 Check 数。</param>
public sealed record PromptCitationDomainSummaryView(string Domain, long CitationCount, long CheckCount);

/// <summary>一个来源下的搜索词与已入库引用摘要，完整证据可进入 Check 历史。</summary>
/// <param name="Source">Provider、采集模式和证据类别。</param>
/// <param name="ChecksWithSearchQueries">有实际搜索词的 Check 数。</param>
/// <param name="DistinctSearchQueries">不同原始搜索词总数。</param>
/// <param name="SearchQueries">最多二十条高频原始搜索词。</param>
/// <param name="ChecksWithCitations">有已入库引用的 Check 数。</param>
/// <param name="CitationCount">已入库引用总条数。</param>
/// <param name="DistinctCitationDomains">不同引用域名总数。</param>
/// <param name="CitationDomains">最多二十个高频引用域名。</param>
public sealed record PromptEvidenceSummaryView(string Source, long ChecksWithSearchQueries,
    long DistinctSearchQueries, IReadOnlyList<PromptSearchQuerySummaryView> SearchQueries,
    long ChecksWithCitations, long CitationCount, long DistinctCitationDomains,
    IReadOnlyList<PromptCitationDomainSummaryView> CitationDomains);

/// <summary>品牌与一个竞品在同一来源、同一有效 Check 集合中的提及差距。</summary>
/// <param name="SubjectId">竞品主体标识。</param>
/// <param name="Name">竞品名称。</param>
/// <param name="BrandMentions">品牌被提及的 Check 数。</param>
/// <param name="CompetitorMentions">竞品被提及的 Check 数。</param>
/// <param name="BrandRate">品牌在有效 Check 中的提及率。</param>
/// <param name="CompetitorRate">竞品在有效 Check 中的提及率。</param>
/// <param name="MentionDifference">品牌减竞品的提及 Check 数。</param>
/// <param name="PercentagePointDifference">品牌减竞品的提及率百分点。</param>
public sealed record PromptCompetitorGapView(Guid SubjectId, string Name, long BrandMentions,
    long CompetitorMentions, MetricRatio BrandRate, MetricRatio CompetitorRate,
    long MentionDifference, double? PercentagePointDifference);

/// <summary>按统一快照聚合的 Prompt 版本、引擎、趋势、运行及证据摘要。</summary>
/// <param name="Meta">统一筛选、发布版本和数据水位。</param>
/// <param name="PromptId">Prompt 稳定标识。</param>
/// <param name="Versions">快照创建前的全部不可变历史版本。</param>
/// <param name="Metrics">单一来源的指标；跨来源时为空指标。</param>
/// <param name="SourceGroups">按 Provider、采集模式及证据类别拆分的指标。</param>
/// <param name="Trend">单一来源的每日实际观测趋势。</param>
/// <param name="SourceTrends">逐来源的每日实际观测趋势。</param>
/// <param name="EnginePerformance">逐来源、逐引擎的指标。</param>
/// <param name="RecentRuns">最近二十个 Run 来源切片。</param>
/// <param name="RunCount">筛选范围内的不同 Run 总数。</param>
/// <param name="HasMoreRuns">是否还有未列出的 Run 来源切片。</param>
/// <param name="CheckHistoryPath">复用当前筛选和快照令牌的跨 Run Check 历史入口。</param>
/// <param name="EvidenceBySource">逐来源的真实搜索词和引用摘要。</param>
/// <param name="CompetitorGapsBySource">逐来源的品牌与竞品提及差距。</param>
public sealed record PromptDetailView(AnalyticsMeta Meta, Guid PromptId,
    IReadOnlyList<PromptVersionHistoryView> Versions, CoreMetrics Metrics,
    IReadOnlyDictionary<string, VoiceProjection> SourceGroups, IReadOnlyList<AnalyticsDayPoint> Trend,
    IReadOnlyDictionary<string, IReadOnlyList<AnalyticsDayPoint>> SourceTrends,
    IReadOnlyList<PromptEnginePerformanceView> EnginePerformance,
    IReadOnlyList<PromptRunHistoryView> RecentRuns, long RunCount, bool HasMoreRuns,
    string CheckHistoryPath, IReadOnlyList<PromptEvidenceSummaryView> EvidenceBySource,
    IReadOnlyDictionary<string, IReadOnlyList<PromptCompetitorGapView>> CompetitorGapsBySource);

/// <summary>带稳定游标的分析列表。</summary>
public sealed record AnalyticsPage<T>(AnalyticsMeta Meta, IReadOnlyList<T> Items, string? NextCursor);

/// <summary>从指标到回答的轻量证据链接。</summary>
public sealed record AnalyticsEvidenceRow(Guid CheckId, Guid RunId, Guid PromptId,
    Guid? PromptVersionId, Guid? TargetId, int SampleIndex, DateTimeOffset? SourceCapturedAt,
    string Provider, string Engine, string Status, string AnalysisStatus,
    IReadOnlyList<Guid> MentionedSubjects, bool SnapshotMissing);

/// <summary>通用 Check 详情所含的公开引用。</summary>
/// <param name="Url">保存的完整公共引用链接。</param>
/// <param name="Domain">保存的来源域名；未提供时为空。</param>
/// <param name="Title">实际来源标题；未提供时为空。</param>
/// <param name="Position">原始引用位置，重复链接保留各自位置。</param>
public sealed record AnalysisCitationView(string Url, string? Domain, string? Title, int Position);

/// <summary>跨 Provider 的 Check 回答、分析和安全元数据。</summary>
public sealed record AnalyticsCheckDetail(Guid CheckId, Guid RunId, Guid PromptId, Guid? PromptVersionId,
    Guid? TargetId, int SampleIndex, string PromptSnapshot, string Provider, string Engine,
    string? ModelVersion, string? Country, string? Locale, string? CaptureMode, string? EvidenceClass,
    string Status, string? Answer, DateTimeOffset? CapturedAt, IReadOnlyList<string> SearchQueries,
    IReadOnlyList<AnalysisCitationView> Citations, CheckAnalysisView? Analysis,
    IReadOnlyList<string> PromptTagsSnapshot, string? BrandedStatusSnapshot,
    bool SnapshotMissing, string? ErrorCode, string? ErrorMessage,
    string QueryAvailability = "unknown");
