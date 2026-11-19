namespace Jogeoai.Domain;

/// <summary>模型监控摘要。</summary>
/// <param name="Meta">统一快照元数据。</param><param name="TotalChecks">区间检测数。</param>
/// <param name="TodayChecks">当地今日检测数。</param><param name="ActiveModelCount">启用模型数。</param>
/// <param name="BrandVisibility">品牌可见率。</param><param name="Latency">采集耗时统计。</param>
/// <param name="TotalProviderRetries">Provider 重试总数。</param><param name="StatusCounts">模型状态计数。</param>
/// <param name="Comparison">前一等长窗口比较。</param><param name="Quality">质量信息。</param>
public sealed record MonitoringSummaryView(AnalyticsMeta Meta, long TotalChecks, long TodayChecks,
    long ActiveModelCount, MetricRatio BrandVisibility, LatencySummary Latency,
    long? TotalProviderRetries, IReadOnlyDictionary<string, long> StatusCounts,
    MonitoringComparison Comparison, MonitoringQuality Quality);

/// <summary>采集任务耗时统计；不是模型生成延迟。</summary>
/// <param name="AverageMs">平均耗时。</param><param name="P50Ms">P50 耗时。</param><param name="P95Ms">P95 耗时。</param>
/// <param name="SampleCount">耗时样本数。</param><param name="MetricName">指标名称。</param><param name="MetricVersion">指标版本。</param>
public sealed record LatencySummary(double? AverageMs, double? P50Ms, double? P95Ms, long SampleCount,
    string MetricName = "collectionElapsedMs", string MetricVersion = "collection-elapsed-v1");

/// <summary>监控指标的前期等长窗口比较。</summary>
/// <param name="PreviousBrandVisibility">前期品牌可见率。</param><param name="VisibilityPercentagePointDifference">可见率百分点变化。</param>
/// <param name="PreviousLatency">前期采集耗时。</param><param name="UnavailableReason">不可用原因。</param>
public sealed record MonitoringComparison(MetricRatio PreviousBrandVisibility,
    double? VisibilityPercentagePointDifference, LatencySummary PreviousLatency,
    string? UnavailableReason);

/// <summary>监控数据质量元数据。</summary>
/// <param name="FailedChecks">失败检测数。</param><param name="NoAnswerChecks">无回答检测数。</param>
/// <param name="PendingAnalysisChecks">待分析检测数。</param><param name="FailedAnalysisChecks">分析失败检测数。</param>
/// <param name="MissingCapturedAtChecks">缺少采集时间的检测数。</param><param name="ModelVersionUnavailableChecks">缺少具体模型版本的检测数。</param>
/// <param name="QueryAvailability">搜索词可用性计数。</param><param name="RuleVersion">状态规则版本。</param><param name="MinimumHealthySamples">健康状态最低样本数。</param>
public sealed record MonitoringQuality(long FailedChecks, long NoAnswerChecks,
    long PendingAnalysisChecks, long FailedAnalysisChecks, long MissingCapturedAtChecks,
    long ModelVersionUnavailableChecks, IReadOnlyDictionary<string, long> QueryAvailability,
    string RuleVersion, int MinimumHealthySamples);

/// <summary>单个模型的监控卡片。</summary>
/// <param name="DisplayName">显示名称。</param><param name="Provider">Provider。</param><param name="Engine">引擎。</param>
/// <param name="ModelVersion">实际模型版本。</param><param name="ModelVersionUnavailable">是否缺少具体版本。</param>
/// <param name="Country">国家。</param><param name="Locale">语言地区。</param><param name="CaptureMode">采集模式。</param>
/// <param name="TodayChecks">今日检测数。</param><param name="RangeChecks">区间检测数。</param><param name="BrandVisibility">品牌可见率。</param>
/// <param name="Latency">采集耗时统计。</param><param name="SuccessfulChecks">成功检测数。</param><param name="FailedChecks">失败检测数。</param>
/// <param name="NoAnswerChecks">无回答检测数。</param><param name="ProviderRetries">Provider 重试数。</param><param name="LastCapturedAt">最后采集时间。</param>
/// <param name="Status">模型状态。</param><param name="StatusReason">状态原因。</param><param name="StatusRuleVersion">状态规则版本。</param>
/// <param name="ConfigurationPath">配置入口。</param><param name="ContributionCheckCount">贡献检测数。</param>
public sealed record MonitoringModelView(string DisplayName, string Provider, string Engine,
    string? ModelVersion, bool ModelVersionUnavailable, string Country, string Locale,
    string CaptureMode, long TodayChecks, long RangeChecks, MetricRatio BrandVisibility,
    LatencySummary Latency, long SuccessfulChecks, long FailedChecks, long NoAnswerChecks,
    long? ProviderRetries, DateTimeOffset? LastCapturedAt, string Status, string StatusReason,
    string StatusRuleVersion, string ConfigurationPath, long ContributionCheckCount);

/// <summary>最近检测记录。</summary>
/// <param name="CheckId">检测标识。</param><param name="CapturedAt">采集时间。</param><param name="DisplayName">显示名称。</param>
/// <param name="Provider">Provider。</param><param name="Engine">引擎。</param><param name="ModelVersion">模型版本。</param>
/// <param name="ModelVersionUnavailable">是否缺少具体版本。</param><param name="Country">国家。</param><param name="Locale">语言地区。</param>
/// <param name="PromptId">Prompt 标识。</param><param name="PromptSnapshot">Prompt 快照。</param><param name="AnswerSummary">回答摘要。</param>
/// <param name="AnswerSummaryTruncated">摘要是否截断。</param><param name="AnswerPresent">是否有回答。</param><param name="BrandMentioned">是否提及品牌。</param>
/// <param name="VisibilityResult">可见性结果。</param><param name="CheckStatus">检测状态。</param><param name="AnalysisStatus">分析状态。</param>
/// <param name="CollectionElapsedMs">采集耗时。</param><param name="ProviderRetries">Provider 重试数。</param><param name="CitationCount">引用数。</param>
/// <param name="QueryAvailability">搜索词可用性。</param><param name="DetailsPath">详情入口。</param>
/// <param name="MentionState">快照内的品牌提及状态：mentioned 已提及、not_mentioned 已确认未提及、unknown 待确认、not_applicable 无可分析回答。</param>
/// <param name="ResultQuality">当前快照中的回答及来源质量；历史未知为空。</param>
/// <param name="BrandMetrics">本品牌在这一条回答中的排名与曝光分数（单条回答口径，与详情同一快照下取值一致）；不可用时各指标值为空并附原因代码，不以 0 代替。</param>
/// <param name="ReputationSentiment">本条回答对本品牌的口碑判定（与口碑聚合同一快照、同一状态归并）；口碑迁移 038 未部署时为空。</param>
/// <param name="AnswerMode">本条回答实际使用的回答模式（快速回答 / 深度思考），与详情取自同一列 visibility_checks.answer_mode；迁移 047 之前的历史回答为空（显示为「历史」）。</param>
/// <param name="CaptureMode">本条回答保存的采集模式（api、scraped 或 app）；没有保存时为 unknown。渠道不能只由它判断，见 <see cref="Channel"/>。</param>
/// <param name="ErrorCode">安全的采集错误代码；App 渠道未采集的检测（状态 cancelled）在这里给出未采集原因码。没有错误时为空。</param>
public sealed record MonitoringRecordView(Guid CheckId, DateTimeOffset? CapturedAt,
    string DisplayName, string Provider, string Engine, string? ModelVersion,
    bool ModelVersionUnavailable, string Country, string Locale, string PromptId, string PromptSnapshot,
    string AnswerSummary, bool AnswerSummaryTruncated, bool AnswerPresent, bool BrandMentioned,
    string VisibilityResult, string CheckStatus, string AnalysisStatus, int? CollectionElapsedMs,
    long? ProviderRetries, long CitationCount, string QueryAvailability, string DetailsPath,
    string MentionState = "unknown", ResultQualityView? ResultQuality = null,
    CheckBrandMetrics? BrandMetrics = null, ReputationSentimentView? ReputationSentiment = null,
    AnswerMode? AnswerMode = null, string? CaptureMode = null, string? ErrorCode = null)
{
    /// <summary>采集渠道，由 Provider 推导（全部接口同一规则）：app_collector 为 app，domestic_collector 为 web，其余为 api。</summary>
    public CollectionChannel Channel => MonitoringProviders.Channel(Provider);
}

/// <summary>当前品牌及列表快照范围内的规范化回答详情，不包含原始响应或调试数据。</summary>
/// <param name="SiteId">品牌工作区标识。</param>
/// <param name="CheckId">检测标识。</param>
/// <param name="RunId">采集运行标识。</param>
/// <param name="PromptId">历史问题的稳定标识。</param>
/// <param name="PromptSnapshot">本次采集保存的完整问题快照。</param>
/// <param name="Provider">实际采集来源。</param>
/// <param name="Engine">实际采集引擎。</param>
/// <param name="ModelVersion">实际模型版本；未提供时为空。</param>
/// <param name="Country">保存的国家代码；未提供时为空。</param>
/// <param name="Locale">保存的语言地区；未提供时为空。</param>
/// <param name="Status">真实采集状态，未完成记录不计入共享分析列表。</param>
/// <param name="AnswerPresent">是否保存了非空回答。</param>
/// <param name="Answer">完整规范化回答；未保存回答时为空。</param>
/// <param name="WebQueries">来源实际提供的搜索词，不从回答推断。</param>
/// <param name="QueryAvailability">搜索词可用性：available、empty、not_exposed 或 unknown。</param>
/// <param name="Citations">按原始位置排列的公共引用，保留重复链接。</param>
/// <param name="CapturedAt">实际采集时间；未提供时为空。</param>
/// <param name="DurationMs">保存的采集耗时毫秒数；未提供时为空，零值有效。</param>
/// <param name="ErrorCode">安全的采集错误代码。</param>
/// <param name="ErrorMessage">安全的采集错误说明。</param>
/// <param name="Analysis">同一发布版本及分析水位下的分析结果。</param>
/// <param name="MatchesListFilters">是否匹配当前快照的列表筛选、搜索和提及状态；不是访问权限判据。</param>
/// <param name="MentionState">与分析证据一致的品牌提及状态：mentioned、not_mentioned、unknown 或 not_applicable；缺少配置或未完成分析不等于未提及。</param>
/// <param name="CitationAttribution">请求新投影时提供的冻结归属；不改写原始引用数组。</param>
/// <param name="ResultQuality">当前快照中的正文和来源保留质量；历史未知为空。</param>
/// <param name="ModelVersionUnavailable">是否缺少具体模型版本；引擎名称回退不视为具体版本。</param>
/// <param name="BrandMetrics">本品牌在这一条回答中的排名与曝光分数，与列表行在同一快照下取值一致；记录尚未到达或分析未完成时两项均不可用（ANALYSIS_NOT_READY）。</param>
/// <param name="ReputationSentiment">口碑判定：状态、置信度、版本、原因码与判定依据片段的原文偏移（用于高亮），与列表行同一快照下取值一致；口碑迁移 038 未部署时为空。</param>
/// <param name="AnswerMode">本条回答实际使用的回答模式（快速回答 / 深度思考），与列表行取值一致；迁移 047 之前的历史回答为空（显示为「历史」）。</param>
/// <param name="CaptureMode">本条回答保存的采集模式（api、scraped 或 app），与列表行取值一致；没有保存时为 unknown。渠道不能只由它判断，见 <see cref="Channel"/>。</param>
/// <param name="App">App 回答的采集环境与引用读取情况（不含账号代号）；不是 App 回答、或记录尚未到达快照时为空。</param>
/// <param name="SourcesWithoutUrl">
/// App 回答里没有可用 URL 的引用来源，按界面上的顺序排列；它们不在 citations 里，也不进入任何引用统计。
/// 因元数据 16 KB 上限被截断时 app.sourcesWithoutUrlTruncated 为 true。不是 App 回答、或记录尚未到达快照时为空。
/// </param>
public sealed record MonitoringAnswerDetailView(Guid SiteId, Guid CheckId, Guid RunId,
    Guid PromptId, string PromptSnapshot, string Provider, string Engine, string? ModelVersion,
    string? Country, string? Locale, string Status, bool AnswerPresent, VisibilityAnswer? Answer,
    IReadOnlyList<string> WebQueries, string QueryAvailability,
    IReadOnlyList<AnalysisCitationView> Citations, DateTimeOffset? CapturedAt, int? DurationMs,
    string? ErrorCode, string? ErrorMessage, CheckAnalysisView? Analysis, bool MatchesListFilters,
    string MentionState = "unknown", CitationAttributionView? CitationAttribution = null,
    ResultQualityView? ResultQuality = null, bool ModelVersionUnavailable = true,
    CheckBrandMetrics? BrandMetrics = null, ReputationSentimentView? ReputationSentiment = null,
    AnswerMode? AnswerMode = null, string? CaptureMode = null, AppAnswerView? App = null,
    IReadOnlyList<AppSourceWithoutUrlView>? SourcesWithoutUrl = null)
{
    /// <summary>采集渠道，由 Provider 推导（全部接口同一规则）：app_collector 为 app，domestic_collector 为 web，其余为 api。</summary>
    public CollectionChannel Channel => MonitoringProviders.Channel(Provider);
}

/// <summary>单条回答中本品牌的排名与曝光分数；回答记录列表行与回答详情共用同一计算入口，同一快照下取值一致。</summary>
/// <param name="Ranking">
/// 本品牌在该回答中的名次（check-subject-first-offset-v1）：按各主体在回答中的首次出现位置，名次 = 首次出现早于本品牌的其他被提及主体数 + 1，可用时分子分母均为 1。
/// 与仪表盘平均排名（subject-first-offset-v1）的差异：回答只提及本品牌、没有其他主体时记为第 1 名，而仪表盘会跳过这类回答。
/// 不可用原因：BRAND_NOT_MENTIONED 回答未提及本品牌；SUBJECT_ORDER_UNAVAILABLE 缺少首次出现位置；CHECK_NOT_SUCCEEDED、NO_ANSWER、ANALYSIS_NOT_READY、CONFIGURATION_MISSING 为采集或分析未就绪。
/// </param>
/// <param name="Exposure">
/// 该回答的曝光分数（exposure-v2，单条回答口径，0–100）：40 × 是否提及本品牌（0/1）+ 30 × 名次分（1 ÷ 名次，未提及记 0）
/// + 20 × 声量（本品牌出现次数 / 已配置主体出现次数之和）+ 10 × 是否引用品牌自有网站（0/1）。
/// 回答没有提及任何已配置主体时声量按 0 计，分数为 0（引用了自有网站则为 10），声量组成如实为 0/0。
/// 不可用原因：NO_COMPETITOR_BASELINE 未配置竞品；SUBJECT_ORDER_UNAVAILABLE 已提及本品牌但缺少出现位置；采集或分析未就绪时与排名携带相同原因代码。
/// </param>
public sealed record CheckBrandMetrics(RankingMetric Ranking, ExposureScore Exposure);

/// <summary>回答记录分页；总数为分页前、同一快照与当前筛选（含搜索、提及状态、被引域名、口碑状态和印象词）下的记录数，翻页时不变。</summary>
/// <param name="Meta">统一快照元数据。</param>
/// <param name="Items">当前页的回答记录。</param>
/// <param name="NextCursor">下一页游标；没有更多记录时为空。</param>
/// <param name="TotalCount">分页前、同一快照与当前筛选下的记录总数。</param>
public sealed record MonitoringRecordPage(AnalyticsMeta Meta, IReadOnlyList<MonitoringRecordView> Items,
    string? NextCursor, long TotalCount);

/// <summary>品牌概览聚合响应。</summary>
/// <param name="Meta">统一快照元数据。</param><param name="Metrics">品牌 KPI。</param><param name="Trend">每日趋势。</param>
/// <param name="Competitors">竞品比较。</param><param name="Groups">按提问分组统计的分组指标，顺序与「用户提问」页一致，「未分组」在最后。</param><param name="Quality">质量信息。</param>
/// <param name="SubjectTrends">主体逐日序列：本品牌在前，竞品按主体配置顺序并遵循竞品选择；仅含有效检测数大于 0 的当地日期，与每日趋势同一快照和时区日界。</param>
public sealed record BrandDashboardView(AnalyticsMeta Meta, BrandDashboardMetrics Metrics,
    IReadOnlyList<BrandDashboardTrendPoint> Trend, IReadOnlyList<BrandCompetitorComparison> Competitors,
    IReadOnlyList<BrandDashboardGroup> Groups, DashboardQuality Quality,
    IReadOnlyList<BrandDashboardSubjectTrend> SubjectTrends);

/// <summary>品牌或竞品的逐日指标序列。</summary>
/// <param name="SubjectId">主体标识。</param><param name="Name">主体名称。</param><param name="IsBrand">是否本品牌。</param>
/// <param name="Points">逐日指标点，仅含有效检测数大于 0 的当地日期，按日期升序。</param>
public sealed record BrandDashboardSubjectTrend(Guid SubjectId, string Name, bool IsBrand,
    IReadOnlyList<BrandDashboardSubjectTrendPoint> Points);

/// <summary>主体在某一当地日期的指标。</summary>
/// <param name="Date">当地日期。</param><param name="MentionRate">当日提及率：该主体被提及的有效检测数 / 当日有效检测数。</param>
/// <param name="AverageRanking">当日平均排名，按当日回答中的主体首次出现位置计算。</param>
/// <param name="SampleCount">当日有效检测数。</param>
public sealed record BrandDashboardSubjectTrendPoint(DateOnly Date, MetricRatio MentionRate,
    RankingMetric AverageRanking, long SampleCount);

/// <summary>品牌概览 KPI。</summary>
/// <param name="BrandMentionRate">品牌提及率。</param><param name="ShareOfVoice">品牌声量份额。</param>
/// <param name="OwnExternalCitationRate">自有外部引用率。</param><param name="AverageRanking">平均排名。</param>
/// <param name="Exposure">曝光效果分数。</param><param name="SubjectCount">主体数。</param><param name="CompetitorCount">竞品数。</param>
/// <param name="EligibleChecks">有效检测数。</param><param name="TotalMentions">主体提及总数。</param>
/// <param name="FirstPositionRate">首位提及率：本品牌名次为第 1 的合格回答数 / 合格回答数。名次与曝光分名次分同一口径：只计已配置主体，回答只提及本品牌时为第 1 名；已提及本品牌但缺少出现位置的回答只计入分母。没有合格回答时取值为空。</param>
/// <param name="TopThreeRate">前三提及率：本品牌名次不晚于第 3 的合格回答数 / 合格回答数，名次口径同首位提及率。</param>
public sealed record BrandDashboardMetrics(MetricRatio BrandMentionRate, MetricRatio ShareOfVoice,
    MetricRatio OwnExternalCitationRate, RankingMetric AverageRanking, ExposureScore Exposure,
    long SubjectCount, long CompetitorCount, long EligibleChecks, long TotalMentions,
    MetricRatio FirstPositionRate, MetricRatio TopThreeRate);

/// <summary>平均主体出现排名。</summary>
/// <param name="Value">平均名次。</param><param name="Numerator">排名样本数。</param><param name="Denominator">排名分母。</param>
/// <param name="UnavailableReason">不可用原因。</param><param name="MetricVersion">指标版本。</param>
public sealed record RankingMetric(double? Value, long Numerator, long Denominator,
    string? UnavailableReason, string MetricVersion = "subject-first-offset-v1");

/// <summary>保留合计与样本数的平均值；样本数为 0 时取值为空。</summary>
/// <param name="Total">各样本取值之和。</param>
/// <param name="Count">样本数。</param>
/// <param name="Value">平均值 = 合计 ÷ 样本数；样本数为 0 时为空。</param>
public sealed record MetricMean(double Total, long Count, double? Value)
{
    /// <summary>按样本数计算平均值，样本数为 0 时取值为空。</summary>
    public static MetricMean Of(double total, long count) => new(total, count, count == 0 ? null : total / count);
}

/// <summary>
/// 可解释的曝光效果分数（exposure-v2，0–100）：40 × 提及率 + 30 × 名次分 + 20 × 声量（按出现次数）+ 10 × 自有引用率。
/// 单条回答与聚合（概览、趋势、分组、团队汇总）使用同一组权重；聚合时各组成分别合并后再加权，因此聚合分数不严格等于单条分数的平均值。
/// exposure-v1（2026-10-22 之前）为 50 × 提及率 + 30 × 声量（按提及的回答数）+ 20 × 自有引用率，不含名次。
/// </summary>
/// <param name="Value">0–100 分数；不可用时为空，原因见资格原因。</param>
/// <param name="ScoreVersion">分数版本，当前为 exposure-v2。</param>
/// <param name="MentionRateWeight">提及率权重，0.4。</param>
/// <param name="PositionWeight">名次分权重，0.3。</param>
/// <param name="ShareOfVoiceWeight">声量权重，0.2。</param>
/// <param name="OwnCitationWeight">自有引用权重，0.1。</param>
/// <param name="BrandMentionRate">提及率组成：提及本品牌的合格回答数 / 合格回答数（单条回答为 0/1 或 1/1）。</param>
/// <param name="PositionScore">名次分组成：各合格回答的 1 ÷ 名次（未提及本品牌记 0）之和 / 可确定名次的合格回答数；名次 = 首次出现早于本品牌的已提及主体数 + 1，只有本品牌时为第 1 名；已提及本品牌但缺少出现位置的回答不计入。</param>
/// <param name="ShareOfVoice">声量组成（按出现次数）：本品牌出现次数 / 已配置主体（本品牌与竞品）出现次数之和；分母为 0 时如实返回 0/0，计分时按 0 计。</param>
/// <param name="OwnExternalCitationRate">自有引用组成：引用了本品牌域名的合格回答数 / 合格回答数。</param>
/// <param name="EligibilityReason">不可用原因：NO_COMPETITOR_BASELINE 未配置竞品；ZERO_REQUIRED_DENOMINATOR 没有合格回答；SUBJECT_ORDER_UNAVAILABLE 已提及本品牌但缺少出现位置、无法确定名次；ONLY_BRAND_SCORE 竞品不计算曝光分；CHECK_NOT_SUCCEEDED、NO_ANSWER、ANALYSIS_NOT_READY、CONFIGURATION_MISSING 为单条回答的采集或分析未就绪。</param>
public sealed record ExposureScore(double? Value, string ScoreVersion,
    double MentionRateWeight, double PositionWeight, double ShareOfVoiceWeight, double OwnCitationWeight,
    MetricRatio BrandMentionRate, MetricMean PositionScore, MetricRatio ShareOfVoice, MetricRatio OwnExternalCitationRate,
    string? EligibilityReason);

/// <summary>单个 Check 内某主体的首次出现位置与出现次数，由 mention_evidence 统计；不对外序列化。</summary>
/// <param name="FirstOffset">首次出现的原文 UTF-16 偏移（证据 offset_start 的最小值）。</param>
/// <param name="Occurrences">出现次数（证据条数）。</param>
public readonly record struct SubjectStats(int FirstOffset, int Occurrences);

/// <summary>品牌概览趋势点。</summary>
/// <param name="Date">当地日期。</param><param name="BrandMentionRate">品牌提及率。</param><param name="ShareOfVoice">声量份额。</param>
/// <param name="AverageRanking">平均排名。</param><param name="Exposure">曝光分数。</param><param name="SampleCount">样本数。</param>
public sealed record BrandDashboardTrendPoint(DateOnly Date, MetricRatio BrandMentionRate,
    MetricRatio ShareOfVoice, RankingMetric AverageRanking, ExposureScore Exposure, long SampleCount);

/// <summary>品牌与竞品对比。</summary>
/// <param name="SubjectId">主体标识。</param><param name="Name">主体名称。</param><param name="IsBrand">是否品牌。</param>
/// <param name="MentionRate">提及率。</param><param name="ShareOfVoice">声量份额。</param><param name="AverageRanking">平均排名。</param>
/// <param name="Exposure">曝光分数。</param><param name="SampleCount">样本数。</param><param name="Rank">声量排名。</param><param name="DetailsPath">证据入口。</param>
public sealed record BrandCompetitorComparison(Guid SubjectId, string Name, bool IsBrand,
    MetricRatio MentionRate, MetricRatio ShareOfVoice, RankingMetric AverageRanking,
    ExposureScore Exposure, long SampleCount, int Rank, string DetailsPath);

/// <summary>按提问当前所属分组（「用户提问」页的分组）统计的品牌概览分组。</summary>
/// <param name="Name">提问分组名称；未分组的提问归入「未分组」。</param><param name="BrandMentionRate">品牌提及率。</param><param name="AverageRanking">平均排名。</param>
/// <param name="Exposure">曝光分数。</param><param name="EligibleChecks">有效检测数。</param>
/// <param name="PreviousPercentagePointDifference">品牌提及率较前一等长窗口（相同筛选、同一提问分组）的百分点变化，即（当前 − 前期）× 100；任一侧分母为零或前期没有该提问分组的数据时为空。</param>
/// <param name="PreviousAverageRankingDifference">平均排名较前一等长窗口（同一提问分组）的差值（当前 − 前期），正数表示名次变差；任一侧排名不可用或前期没有该提问分组的数据时为空。</param>
/// <param name="PreviousExposureDifference">曝光效果分数较前一等长窗口（同一提问分组）的差值（当前 − 前期）；任一侧分数不可用或前期没有该提问分组的数据时为空。</param>
/// <param name="PromptIds">贡献 Prompt 标识。</param><param name="ContributionCheckIds">贡献检测标识。</param>
/// <param name="GroupId">提问分组标识；「未分组」为空。按提问当前所属分组归类，不是采集时的分组。</param>
public sealed record BrandDashboardGroup(string Name, MetricRatio BrandMentionRate,
    RankingMetric AverageRanking, ExposureScore Exposure, long EligibleChecks,
    double? PreviousPercentagePointDifference, double? PreviousAverageRankingDifference,
    double? PreviousExposureDifference, IReadOnlyList<Guid> PromptIds,
    IReadOnlyList<Guid> ContributionCheckIds, Guid? GroupId);

/// <summary>提问当前所属的分组，用于品牌概览按提问分组统计；不对外序列化。</summary>
/// <param name="GroupId">提问分组标识；提问未分组时为空。</param>
/// <param name="Name">提问分组名称；未分组时为空。</param>
/// <param name="CreatedAt">分组创建时间，用于与「用户提问」页一致的排序；未分组时为空。</param>
public sealed record PromptGroupAssignment(Guid? GroupId, string? Name, DateTimeOffset? CreatedAt);

/// <summary>品牌概览质量信息。</summary>
/// <param name="PendingAnalysisChecks">待分析检测数。</param><param name="FailedAnalysisChecks">分析失败检测数。</param>
/// <param name="MissingCapturedAtChecks">缺少采集时间的检测数。</param><param name="SubjectCount">主体数。</param><param name="CompetitorCount">竞品数。</param>
/// <param name="TagCount">当前窗口事实中出现的 Prompt 标签数（忽略大小写去重）。</param><param name="CompetitorUnavailableReason">竞品不可用原因。</param>
/// <param name="GroupUnavailableReason">分组不可用原因；当前窗口没有任何提问分组数据时为 NO_PROMPT_GROUP_DATA。</param>
/// <param name="UrlNormalizationVersion">URL 规范化版本。</param><param name="UrlNormalizationStatus">URL 规范化状态。</param><param name="QueryAvailability">搜索词可用性计数。</param>
/// <param name="GroupCount">本次结果的提问分组数（含「未分组」），即分组列表的总条数，可用于判断分组分页是否还有下一页。</param>
public sealed record DashboardQuality(long PendingAnalysisChecks, long FailedAnalysisChecks,
    long MissingCapturedAtChecks, long SubjectCount, long CompetitorCount, long TagCount,
    string? CompetitorUnavailableReason, string? GroupUnavailableReason,
    string UrlNormalizationVersion, string? UrlNormalizationStatus,
    IReadOnlyDictionary<string, long> QueryAvailability, long GroupCount);
