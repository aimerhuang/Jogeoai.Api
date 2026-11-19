using System.Text.Json.Serialization;

namespace Jogeoai.Domain;

/// <summary>单条回答对本品牌的口碑判定状态。</summary>
public enum ReputationSentimentState
{
    /// <summary>正面：回答对本品牌整体态度正面。</summary>
    [JsonStringEnumMemberName("positive")]
    Positive,
    /// <summary>中性：回答对本品牌只做客观描述或态度中立。</summary>
    [JsonStringEnumMemberName("neutral")]
    Neutral,
    /// <summary>负面：回答对本品牌整体态度负面。</summary>
    [JsonStringEnumMemberName("negative")]
    Negative,
    /// <summary>未知：已判定但无法给出可信结论（低置信度、输出非法、依据片段不在原文、与提及分析不一致等），不归入中性。</summary>
    [JsonStringEnumMemberName("unknown")]
    Unknown,
    /// <summary>未关联：现有提及分析确认回答未提及本品牌，不进入口碑分母，也不调用情感判定。</summary>
    [JsonStringEnumMemberName("not_mentioned")]
    NotMentioned,
    /// <summary>待判定：已提及本品牌但情感判定尚未完成（含未入队、排队、处理中、可重试失败）。</summary>
    [JsonStringEnumMemberName("pending")]
    Pending,
    /// <summary>判定失败：重试耗尽仍未得到判定结果。</summary>
    [JsonStringEnumMemberName("failed")]
    Failed,
    /// <summary>不适用：采集失败、无回答或提及分析尚未完成/缺少配置，不属于有效回答。</summary>
    [JsonStringEnumMemberName("not_applicable")]
    NotApplicable,
}

/// <summary>口碑健康度等级；阈值固定为 ≥80 / 65 / 50 / 35，按取整后的健康度判定。</summary>
public enum ReputationHealthLevel
{
    /// <summary>口碑优秀：健康度 ≥ 80。</summary>
    [JsonStringEnumMemberName("excellent")]
    Excellent,
    /// <summary>口碑良好：健康度 65–79。</summary>
    [JsonStringEnumMemberName("good")]
    Good,
    /// <summary>口碑一般：健康度 50–64。</summary>
    [JsonStringEnumMemberName("fair")]
    Fair,
    /// <summary>存在风险：健康度 35–49。</summary>
    [JsonStringEnumMemberName("risk")]
    Risk,
    /// <summary>高风险：健康度 &lt; 35。</summary>
    [JsonStringEnumMemberName("high_risk")]
    HighRisk,
}

/// <summary>口碑页数据视图。</summary>
public enum ReputationView
{
    /// <summary>范围数据：按所选时间范围聚合，趋势覆盖范围内每一个当地日期。</summary>
    [JsonStringEnumMemberName("range")]
    Range,
    /// <summary>当日数据：只看一个当地自然日，趋势为空，改由平台分布展示。</summary>
    [JsonStringEnumMemberName("day")]
    Day,
}

/// <summary>问题 × 平台热力图单元格状态。</summary>
public enum ReputationCellState
{
    /// <summary>已判定样本数达到最低样本数，健康度可直接比较。</summary>
    [JsonStringEnumMemberName("ok")]
    Ok,
    /// <summary>已判定样本数大于 0 但低于最低样本数（样本不足）。</summary>
    [JsonStringEnumMemberName("low_sample")]
    LowSample,
    /// <summary>该组合没有已提及本品牌的有效回答（无数据）。</summary>
    [JsonStringEnumMemberName("no_data")]
    NoData,
    /// <summary>有已提及本品牌的有效回答，但全部未判定（待判定、判定失败或未知）。</summary>
    [JsonStringEnumMemberName("pending")]
    Pending,
}

/// <summary>印象词的极性。</summary>
public enum ReputationTermPolarity
{
    /// <summary>正面描述。</summary>
    [JsonStringEnumMemberName("positive")]
    Positive,
    /// <summary>中性描述。</summary>
    [JsonStringEnumMemberName("neutral")]
    Neutral,
    /// <summary>负面描述。</summary>
    [JsonStringEnumMemberName("negative")]
    Negative,
}

/// <summary>健康度等级阈值，供前端映射文案与颜色。</summary>
/// <param name="Level">等级。</param>
/// <param name="MinHealth">进入该等级的最低健康度（含），按取整后的健康度比较。</param>
public sealed record ReputationLevelThreshold(ReputationHealthLevel Level, int MinHealth);

/// <summary>正 / 中 / 负三项显示占比（百分数，1 位小数），三项之和固定为 100.0；舍入差额计入数量最大的一项。</summary>
/// <param name="Positive">正面占比显示值（百分数）。</param>
/// <param name="Neutral">中性占比显示值（百分数）。</param>
/// <param name="Negative">负面占比显示值（百分数）。</param>
public sealed record ReputationDisplayPercents(double Positive, double Neutral, double Negative);

/// <summary>
/// 任一维度（整体、日期、平台、问题、问题 × 平台）的口碑计数与健康度，全页共用同一计算函数（reputation-health-v1）。
/// 健康度 = 正面占比 × 100 + 中性占比 × 50 + 负面占比 × 0，按回答数合并计算，取值 0–100；覆盖率不乘入分数。
/// </summary>
/// <param name="Positive">正面回答数。</param>
/// <param name="Neutral">中性回答数。</param>
/// <param name="Negative">负面回答数。</param>
/// <param name="Classified">已判定回答数（口碑样本数）= 正面 + 中性 + 负面，是三项占比与健康度的分母。</param>
/// <param name="PositiveRate">正面占比（未舍入，0–1）；样本为 0 时取值为空。</param>
/// <param name="NeutralRate">中性占比（未舍入，0–1）；样本为 0 时取值为空。</param>
/// <param name="NegativeRate">负面占比（未舍入，0–1）；样本为 0 时取值为空。</param>
/// <param name="DisplayPercents">三项显示占比（1 位小数、和为 100.0）；样本为 0 时为空。</param>
/// <param name="Health">健康度（未舍入，0–100）；样本为 0 时为空（暂无样本），不以 0 代替。</param>
/// <param name="Level">按取整后健康度判定的等级；健康度为空时为空。</param>
/// <param name="LowSample">样本不足：已判定回答数大于 0 且低于最低样本数（默认 3）；样本不足的维度不参与最好 / 最差高亮，排名置后。</param>
/// <param name="Unknown">判定为未知的回答数（不归入中性）。</param>
/// <param name="Pending">已提及本品牌但尚未判定的回答数。</param>
/// <param name="Failed">判定重试耗尽的回答数。</param>
/// <param name="Mentioned">已提及本品牌的有效回答数 = 已判定 + 未知 + 待判定 + 判定失败。</param>
/// <param name="Coverage">情感覆盖率 = 已判定 ÷ 已提及本品牌的有效回答；分母为 0 时取值为空。</param>
/// <param name="PartialCoverage">部分覆盖：覆盖率低于阈值（默认 80%）；只作质量标记，不影响健康度。</param>
/// <param name="UnavailableReason">健康度不可用原因：NO_MENTIONED_ANSWERS 没有已提及本品牌的有效回答；NO_CLASSIFIED_SAMPLE 已提及但尚无判定样本；可用时为空。</param>
public sealed record ReputationScore(long Positive, long Neutral, long Negative, long Classified,
    MetricRatio PositiveRate, MetricRatio NeutralRate, MetricRatio NegativeRate,
    ReputationDisplayPercents? DisplayPercents, double? Health, ReputationHealthLevel? Level, bool LowSample,
    long Unknown, long Pending, long Failed, long Mentioned, MetricRatio Coverage, bool PartialCoverage,
    string? UnavailableReason);

/// <summary>口碑页元数据：快照、时区、范围、视图、发布版本和口径版本。</summary>
/// <param name="SnapshotToken">十五分钟有效的分析快照令牌；口碑各区块与回答下钻复用。</param>
/// <param name="AsOf">快照时间（UTC），晚于该时间写入的情感判定不计入本快照。</param>
/// <param name="ExpiresAt">快照过期时间（UTC）。</param>
/// <param name="Filters">冻结到快照的统一筛选条件。</param>
/// <param name="TimeZone">按日统计使用的 IANA 时区。</param>
/// <param name="View">数据视图：range 范围数据或 day 当日数据。</param>
/// <param name="LocalFrom">包含的起始当地日期。</param>
/// <param name="LocalTo">不包含的结束当地日期。</param>
/// <param name="ReleaseId">快照绑定的已发布分析版本标识；情感事实只读取该版本。</param>
/// <param name="DataCutoff">数据截止时间（UTC，等于快照时间）。</param>
/// <param name="LastCapturedAt">窗口内最后一次实际采集时间；没有记录时为空。</param>
/// <param name="InProgress">窗口包含快照当地今天，采集与分析可能仍在进行。</param>
/// <param name="MinSampleSize">最低样本数，低于该值标记样本不足（默认 3）。</param>
/// <param name="PartialCoverageThreshold">部分覆盖阈值（0–1，默认 0.8）。</param>
/// <param name="HealthVersion">健康度口径版本，当前为 reputation-health-v1。</param>
/// <param name="ClassifierVersion">情感判定口径版本，当前为 reputation-sentiment-v1。</param>
/// <param name="LevelThresholds">健康度等级阈值，由高到低排列。</param>
/// <param name="AnalysisPendingChecks">提及分析尚未完成的回答数（不属于有效回答）。</param>
/// <param name="AnalysisFailedChecks">提及分析失败的回答数（不属于有效回答）。</param>
public sealed record ReputationMeta(string SnapshotToken, DateTimeOffset AsOf, DateTimeOffset ExpiresAt,
    AnalyticsFilter Filters, string TimeZone, ReputationView View, DateOnly LocalFrom, DateOnly LocalTo,
    Guid ReleaseId, DateTimeOffset DataCutoff, DateTimeOffset? LastCapturedAt, bool InProgress,
    int MinSampleSize, double PartialCoverageThreshold, string HealthVersion, string ClassifierVersion,
    IReadOnlyList<ReputationLevelThreshold> LevelThresholds, long AnalysisPendingChecks, long AnalysisFailedChecks);

/// <summary>顶部口碑 KPI。</summary>
/// <param name="Score">整体口碑计数、占比、健康度与质量标记。</param>
/// <param name="EligibleAnswers">有效回答数（采集成功、回答非空、提及分析完成且配置完整）。</param>
/// <param name="NotMentioned">未关联（未提及本品牌）的有效回答数，不进入分母。</param>
/// <param name="UnavailableReason">健康度不可用原因：NO_ELIGIBLE_ANSWERS 没有有效回答；其余同 Score.UnavailableReason；可用时为空。</param>
public sealed record ReputationMetrics(ReputationScore Score, long EligibleAnswers, long NotMentioned,
    string? UnavailableReason);

/// <summary>与前一个等长窗口（当日数据为前一日）的口碑比较，形态参照 RatioComparison。</summary>
/// <param name="PreviousFrom">前一窗口起点（UTC，包含）。</param>
/// <param name="PreviousTo">前一窗口终点（UTC，不包含），等于当前窗口起点。</param>
/// <param name="Previous">前一窗口的口碑计数与健康度。</param>
/// <param name="HealthDifference">健康度分差（当前 − 前期，未舍入）；不可用时为空。</param>
/// <param name="PositiveRatePointDifference">正面占比百分点差（（当前 − 前期）× 100）；不可用时为空。</param>
/// <param name="NegativeRatePointDifference">负面占比百分点差（（当前 − 前期）× 100）；不可用时为空。</param>
/// <param name="UnavailableReason">不可用原因：INSUFFICIENT_SAMPLE 前一窗口已判定样本低于最低样本数（显示「对比数据不足」）；NO_CURRENT_SAMPLE 当前窗口没有判定样本；可用时为空。</param>
public sealed record ReputationComparison(DateTimeOffset PreviousFrom, DateTimeOffset PreviousTo,
    ReputationScore Previous, double? HealthDifference, double? PositiveRatePointDifference,
    double? NegativeRatePointDifference, string? UnavailableReason);

/// <summary>情感趋势中的一个当地日期。</summary>
/// <param name="Date">当地日期；范围内每一个日期都有一个点，无样本日期健康度为空。</param>
/// <param name="Score">该日的口碑计数与健康度。</param>
public sealed record ReputationTrendPoint(DateOnly Date, ReputationScore Score);

/// <summary>单个平台的逐日口碑序列（趋势大图中的平台分线）。</summary>
/// <param name="PlatformKey">平台身份键 provider|engine|captureMode。</param>
/// <param name="Points">与整体趋势相同日期集合的逐日点。</param>
public sealed record ReputationPlatformTrend(string PlatformKey, IReadOnlyList<ReputationTrendPoint> Points);

/// <summary>有数据平台的口碑卡片；平台身份 = provider + engine + captureMode，同一引擎的网页与 APP 分别聚合。</summary>
/// <param name="PlatformKey">平台身份键 provider|engine|captureMode，热力图列与此一致。</param>
/// <param name="Provider">采集来源。</param>
/// <param name="Engine">回答引擎。</param>
/// <param name="CaptureMode">采集模式：scraped 页面抓取、api 官方接口。</param>
/// <param name="DisplayName">显示名称，与模型监控卡片 displayName 同一函数生成。</param>
/// <param name="Score">该平台的口碑计数与健康度（与 KPI 同一 0–100 公式）。</param>
/// <param name="HealthDifference">与整体健康度的差值（平台 − 整体）；任一侧为空时为空。</param>
/// <param name="IsBest">是否为健康度最高的平台；只在至少两个非样本不足平台之间比较，最高与最低相同时均为 false。</param>
/// <param name="IsWorst">是否为健康度最低的平台；规则同 IsBest。</param>
public sealed record ReputationPlatformView(string PlatformKey, string Provider, string Engine, string CaptureMode,
    string DisplayName, ReputationScore Score, double? HealthDifference, bool IsBest, bool IsWorst);

/// <summary>当前筛选下没有判定样本的平台（折叠展示，不以 0 分卡片出现）。</summary>
/// <param name="PlatformKey">平台身份键 provider|engine|captureMode。</param>
/// <param name="Provider">采集来源。</param>
/// <param name="Engine">回答引擎。</param>
/// <param name="CaptureMode">采集模式。</param>
/// <param name="DisplayName">显示名称，与模型监控卡片 displayName 同一函数生成。</param>
/// <param name="Reason">无数据原因：NO_ANSWERS 窗口内没有有效回答；NO_MENTIONS 有效回答均未提及本品牌；NOT_CLASSIFIED 已提及但全部未判定。</param>
public sealed record ReputationPlatformWithoutData(string PlatformKey, string Provider, string Engine,
    string CaptureMode, string DisplayName, string Reason);

/// <summary>监测问题口碑排名的一行；默认按健康度升序（风险优先），同值按已判定回答数降序，样本不足的问题置后。</summary>
/// <param name="PromptId">监测问题稳定标识。</param>
/// <param name="PromptText">问题全文（窗口内最近一次采集的问题快照）。</param>
/// <param name="Score">该问题的口碑计数与健康度（与 KPI 同一 0–100 公式）。</param>
public sealed record ReputationQuestionView(Guid PromptId, string PromptText, ReputationScore Score);

/// <summary>当前筛选下有有效回答但没有判定样本的监测问题。</summary>
/// <param name="PromptId">监测问题稳定标识。</param>
/// <param name="PromptText">问题全文。</param>
/// <param name="Reason">原因：NO_MENTIONS 有效回答均未提及本品牌；NOT_CLASSIFIED 已提及但全部未判定。</param>
public sealed record ReputationQuestionWithoutData(Guid PromptId, string PromptText, string Reason);

/// <summary>问题 × 平台热力图单元格；分值为该组合下所有已判定回答合并计算的健康度，不是每日值的平均。</summary>
/// <param name="PlatformKey">列的平台身份键。</param>
/// <param name="State">单元格状态：ok、low_sample、no_data、pending。</param>
/// <param name="Score">该组合的口碑计数与健康度。</param>
public sealed record ReputationHeatmapCell(string PlatformKey, ReputationCellState State, ReputationScore Score);

/// <summary>热力图的一行（一个监测问题）。</summary>
/// <param name="PromptId">监测问题标识。</param>
/// <param name="PromptText">问题全文。</param>
/// <param name="Cells">按列顺序排列的单元格，与 Columns 一一对应。</param>
public sealed record ReputationHeatmapRow(Guid PromptId, string PromptText, IReadOnlyList<ReputationHeatmapCell> Cells);

/// <summary>问题 × 平台热力图；列集合与顺序与 platforms 完全一致，行与 questions 顺序一致。</summary>
/// <param name="Columns">列的平台身份键，与 platforms[].platformKey 相同且同序。</param>
/// <param name="Rows">每个有判定样本的监测问题一行。</param>
public sealed record ReputationHeatmap(IReadOnlyList<string> Columns, IReadOnlyList<ReputationHeatmapRow> Rows);

/// <summary>高频印象词；只来自已判定（正 / 中 / 负）回答，且必须能在回答原文中找到。</summary>
/// <param name="Term">显示用短语。</param>
/// <param name="TermKey">规范化词键（NFKC、去首尾空白、合并空白、小写），作为回答列表 term 筛选参数。</param>
/// <param name="DominantPolarity">主要极性：贡献回答数最多的极性，同数时按出现次数，仍相同时取负面 → 中性 → 正面（风险优先）。</param>
/// <param name="AnswerCount">贡献回答数（包含该短语的不同回答），用于排序与字号分档。</param>
/// <param name="Occurrences">在这些回答原文中的总出现次数。</param>
/// <param name="PositiveAnswers">以正面极性出现的回答数。</param>
/// <param name="NeutralAnswers">以中性极性出现的回答数。</param>
/// <param name="NegativeAnswers">以负面极性出现的回答数。</param>
public sealed record ReputationTermView(string Term, string TermKey, ReputationTermPolarity DominantPolarity,
    long AnswerCount, long Occurrences, long PositiveAnswers, long NeutralAnswers, long NegativeAnswers);

/// <summary>情感因子：按主要极性分组的印象词，每个词只归入其主要极性一组，每组最多 10 个。</summary>
/// <param name="Positive">主要极性为正面的印象词，按正面回答数降序。</param>
/// <param name="Neutral">主要极性为中性的印象词，按中性回答数降序。</param>
/// <param name="Negative">主要极性为负面的印象词，按负面回答数降序。</param>
public sealed record ReputationTermGroups(IReadOnlyList<ReputationTermView> Positive,
    IReadOnlyList<ReputationTermView> Neutral, IReadOnlyList<ReputationTermView> Negative);

/// <summary>口碑数据质量：分母、未判定数量与覆盖率。</summary>
/// <param name="EligibleAnswers">有效回答数。</param>
/// <param name="MentionedAnswers">已提及本品牌的有效回答数。</param>
/// <param name="NotMentionedAnswers">未关联（未提及本品牌）的有效回答数。</param>
/// <param name="ClassifiedAnswers">已判定（正 / 中 / 负）回答数。</param>
/// <param name="UnknownAnswers">判定为未知的回答数。</param>
/// <param name="PendingAnswers">待判定回答数。</param>
/// <param name="FailedAnswers">判定失败（重试耗尽）回答数。</param>
/// <param name="CoverageRate">情感覆盖率 = 已判定 ÷ 已提及本品牌的有效回答。</param>
/// <param name="PartialCoverage">覆盖率是否低于部分覆盖阈值。</param>
/// <param name="UnknownReasons">未知判定的原因码计数，例如 LOW_CONFIDENCE、INVALID_OUTPUT、EVIDENCE_NOT_FOUND、MENTION_DISAGREE、CLASSIFIER_UNKNOWN。</param>
/// <param name="ClassifierVersion">情感判定口径版本。</param>
/// <param name="QuestionsWithData">有判定样本的监测问题数。</param>
/// <param name="PlatformsWithData">有判定样本的平台数。</param>
public sealed record ReputationQuality(long EligibleAnswers, long MentionedAnswers, long NotMentionedAnswers,
    long ClassifiedAnswers, long UnknownAnswers, long PendingAnswers, long FailedAnswers, MetricRatio CoverageRate,
    bool PartialCoverage, IReadOnlyDictionary<string, long> UnknownReasons, string ClassifierVersion,
    long QuestionsWithData, long PlatformsWithData);

/// <summary>品牌口碑聚合响应：KPI、环比、趋势、平台、问题、热力图、情感因子和质量来自同一筛选与同一快照。</summary>
/// <param name="Meta">快照、范围、视图与口径版本。</param>
/// <param name="Metrics">顶部口碑 KPI。</param>
/// <param name="Comparison">与前一个等长窗口的比较。</param>
/// <param name="Trend">范围数据时为范围内每一个当地日期的点（无样本日期健康度为空）；当日数据时为空数组。</param>
/// <param name="PlatformTrends">各有数据平台的逐日序列（与 Trend 相同日期）；当日数据时为空数组。</param>
/// <param name="Platforms">有判定样本的平台卡片，按已判定回答数降序、平台键升序排列。</param>
/// <param name="PlatformsWithoutData">已配置或出现过但没有判定样本的平台。</param>
/// <param name="Questions">有判定样本的监测问题，默认风险优先排序。</param>
/// <param name="QuestionsWithoutData">有有效回答但没有判定样本的监测问题。</param>
/// <param name="Heatmap">问题 × 平台热力图，列集合与 Platforms 一致。</param>
/// <param name="TermGroups">情感因子：按主要极性分组的印象词，每组最多 10 个；组内按该极性回答数、贡献回答数、出现次数降序，再按词键升序。</param>
/// <param name="Quality">数据质量与覆盖率。</param>
public sealed record ReputationSummaryView(ReputationMeta Meta, ReputationMetrics Metrics,
    ReputationComparison Comparison, IReadOnlyList<ReputationTrendPoint> Trend,
    IReadOnlyList<ReputationPlatformTrend> PlatformTrends, IReadOnlyList<ReputationPlatformView> Platforms,
    IReadOnlyList<ReputationPlatformWithoutData> PlatformsWithoutData, IReadOnlyList<ReputationQuestionView> Questions,
    IReadOnlyList<ReputationQuestionWithoutData> QuestionsWithoutData, ReputationHeatmap Heatmap,
    ReputationTermGroups TermGroups, ReputationQuality Quality);

/// <summary>回答列表行与详情中的情感判定；不包含模型原始解释或原始响应。</summary>
/// <param name="State">口碑判定状态。</param>
/// <param name="Confidence">分类器置信度（0–1）；未判定或无置信度时为空。</param>
/// <param name="RationaleCode">判定依据或未知原因的有界机器码；待判定、未关联、不适用时为空（判定失败时为最后一次错误码）。</param>
/// <param name="ClassifierVersion">情感判定口径版本；尚无判定事实时为空。</param>
/// <param name="ModelVersion">实际调用的模型；未调用模型（未关联、复用）时可能为空。</param>
/// <param name="EvidenceStart">判定依据片段在回答原文中的 UTF-16 起始偏移（含）；没有可回溯片段时为空。</param>
/// <param name="EvidenceEnd">判定依据片段在回答原文中的 UTF-16 结束偏移（不含）。</param>
/// <param name="ClassifiedAt">判定写入时间（UTC）。</param>
public sealed record ReputationSentimentView(ReputationSentimentState State, double? Confidence,
    string? RationaleCode, string? ClassifierVersion, string? ModelVersion, int? EvidenceStart, int? EvidenceEnd,
    DateTimeOffset? ClassifiedAt);

/// <summary>站点级口碑判定补录请求：把当前已发布分析版本中的有效回答登记为待判定；不会立即调用模型。</summary>
/// <param name="From">可选的采集时间起点（含，带时区 ISO 时间）；省略表示不限。</param>
/// <param name="To">可选的采集时间终点（不含）；省略表示不限。</param>
public sealed record ReputationJobRequest(DateTimeOffset? From = null, DateTimeOffset? To = null);

/// <summary>站点级口碑判定补录任务进度；计数按当前读取时的工作项实时统计。</summary>
/// <param name="JobId">任务标识。</param>
/// <param name="SiteId">品牌工作区标识。</param>
/// <param name="ReleaseId">登记时的已发布分析版本标识。</param>
/// <param name="From">采集时间起点（含）；不限时为空。</param>
/// <param name="To">采集时间终点（不含）；不限时为空。</param>
/// <param name="Status">任务状态：processing 仍有待判定工作项；completed 全部完成（可能含判定失败）。</param>
/// <param name="TotalChecks">范围内登记的工作项数。</param>
/// <param name="CompletedChecks">已完成工作项数（含未关联与未知）。</param>
/// <param name="PendingChecks">待处理工作项数（含处理中与可重试）。</param>
/// <param name="FailedChecks">重试耗尽的工作项数。</param>
/// <param name="UnknownChecks">判定为未知的回答数。</param>
/// <param name="CreatedAt">任务创建时间（UTC）。</param>
public sealed record ReputationJobView(Guid JobId, Guid SiteId, Guid ReleaseId, DateTimeOffset? From,
    DateTimeOffset? To, string Status, long TotalChecks, long CompletedChecks, long PendingChecks,
    long FailedChecks, long UnknownChecks, DateTimeOffset CreatedAt);

/// <summary>口碑聚合使用的单条回答事实（不对外序列化）。</summary>
/// <param name="CheckId">检测标识。</param>
/// <param name="PromptId">监测问题标识。</param>
/// <param name="PromptText">问题快照。</param>
/// <param name="CapturedAt">采集时间。</param>
/// <param name="LocalDate">按快照时区换算的采集当地日期。</param>
/// <param name="Provider">采集来源。</param>
/// <param name="Engine">回答引擎。</param>
/// <param name="CaptureMode">采集模式。</param>
/// <param name="State">由 ReputationRules.ResolveState 得到的唯一判定状态。</param>
/// <param name="RationaleCode">判定原因码。</param>
/// <param name="Terms">该回答已校验的印象词。</param>
public sealed record ReputationFact(Guid CheckId, Guid PromptId, string PromptText, DateTimeOffset? CapturedAt,
    DateOnly? LocalDate, string Provider, string Engine, string CaptureMode, ReputationSentimentState State,
    string? RationaleCode, IReadOnlyList<ReputationFactTerm> Terms)
{
    /// <summary>平台身份键 provider|engine|captureMode。</summary>
    public string PlatformKey => PlatformIdentity.Key(Provider, Engine, CaptureMode);
}

/// <summary>单条回答中的一个印象词事实（不对外序列化）。</summary>
/// <param name="TermKey">规范化词键。</param>
/// <param name="TermText">回答原文中的短语。</param>
/// <param name="Polarity">极性。</param>
/// <param name="Occurrences">该回答原文中的出现次数。</param>
public sealed record ReputationFactTerm(string TermKey, string TermText, ReputationTermPolarity Polarity, int Occurrences);

/// <summary>平台身份与显示名称的唯一实现：平台 = provider + engine + captureMode。</summary>
public static class PlatformIdentity
{
    /// <summary>平台身份键 provider|engine|captureMode。</summary>
    public static string Key(string provider, string engine, string captureMode)
        => string.Join('|', provider, engine, captureMode);

    /// <summary>平台显示名称；模型监控卡片（monitoring/models）与口碑平台卡片共用，当前为引擎名，网页与 APP 由 captureMode 区分。</summary>
    public static string DisplayName(string provider, string engine, string captureMode) => engine;
}
