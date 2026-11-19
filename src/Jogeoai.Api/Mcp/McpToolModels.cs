using Jogeoai.Domain;

namespace Jogeoai.Api.Mcp;

/// <summary>令牌范围内的品牌（站点）。</summary>
/// <param name="SiteId">站点标识，其余工具的 siteId 参数。</param>
/// <param name="Name">品牌名称。</param>
/// <param name="PrimaryDomain">品牌主域名；未设置时为空。</param>
/// <param name="Status">品牌状态：active 或 archived。</param>
/// <param name="TimeZone">品牌时区，日期参数按此时区解释。</param>
public sealed record McpSiteItem(Guid SiteId, string Name, string? PrimaryDomain, string Status, string TimeZone);

/// <summary>品牌列表。</summary>
/// <param name="Items">品牌。</param>
/// <param name="NextCursor">下一页游标；没有更多或令牌限定了站点时为空。</param>
public sealed record McpSiteList(IReadOnlyList<McpSiteItem> Items, string? NextCursor);

/// <summary>查询使用的快照窗口。</summary>
/// <param name="From">起始 UTC 时间（含）。</param>
/// <param name="To">结束 UTC 时间（不含）。</param>
/// <param name="TimeZone">按日统计使用的时区。</param>
/// <param name="SnapshotToken">快照令牌（15 分钟有效）；翻页或读取单条记录时原样传回。</param>
public sealed record McpWindow(DateTimeOffset From, DateTimeOffset To, string TimeZone, string SnapshotToken)
{
    /// <summary>从分析元数据生成。</summary>
    /// <param name="meta">分析元数据。</param>
    public static McpWindow Of(AnalyticsMeta meta) => new(meta.Filters.From, meta.Filters.To, meta.Filters.TimeZone, meta.SnapshotToken);
}

/// <summary>监测提问在单一版本、单一来源下的表现。</summary>
/// <param name="PromptId">提问标识。</param>
/// <param name="PromptVersionId">提问版本标识。</param>
/// <param name="Prompt">提问文本快照。</param>
/// <param name="Source">来源（provider|captureMode|evidenceClass）。</param>
/// <param name="CheckCount">检测数。</param>
/// <param name="EligibleChecks">有效检测数（采集成功且分析完成）。</param>
/// <param name="BrandMentions">提及本品牌的有效检测数。</param>
/// <param name="Visibility">品牌可见率（提及率）：分子 / 分母 / 值（0–1）。</param>
/// <param name="OwnCitationRate">引用品牌自有网站的比例。</param>
/// <param name="LastCapturedAt">最近采集时间。</param>
public sealed record McpPromptItem(Guid PromptId, Guid? PromptVersionId, string? Prompt, string? Source, long CheckCount,
    long EligibleChecks, long BrandMentions, MetricRatio Visibility, MetricRatio OwnCitationRate, DateTimeOffset? LastCapturedAt);

/// <summary>监测提问分页。</summary>
/// <param name="Window">快照窗口。</param>
/// <param name="Items">提问表现。</param>
/// <param name="NextCursor">下一页游标。</param>
public sealed record McpPromptPage(McpWindow Window, IReadOnlyList<McpPromptItem> Items, string? NextCursor);

/// <summary>站点监测概览。</summary>
/// <param name="Window">快照窗口。</param>
/// <param name="TotalChecks">区间检测数。</param>
/// <param name="TodayChecks">当地今日检测数。</param>
/// <param name="ActiveModelCount">启用的模型数。</param>
/// <param name="BrandVisibility">品牌可见率。</param>
/// <param name="PreviousBrandVisibility">前一等长窗口的品牌可见率。</param>
/// <param name="VisibilityPercentagePointDifference">可见率变化（百分点）。</param>
/// <param name="AverageCollectionMs">平均采集耗时（毫秒）。</param>
/// <param name="StatusCounts">各模型状态计数。</param>
/// <param name="FailedChecks">失败检测数。</param>
/// <param name="NoAnswerChecks">无回答检测数。</param>
/// <param name="PendingAnalysisChecks">待分析检测数。</param>
public sealed record McpMonitoringSummary(McpWindow Window, long TotalChecks, long TodayChecks, long ActiveModelCount,
    MetricRatio BrandVisibility, MetricRatio PreviousBrandVisibility, double? VisibilityPercentagePointDifference,
    double? AverageCollectionMs, IReadOnlyDictionary<string, long> StatusCounts, long FailedChecks, long NoAnswerChecks,
    long PendingAnalysisChecks);

/// <summary>单条回答中本品牌的排名与曝光分数。</summary>
/// <param name="Rank">名次；不可用时为空。</param>
/// <param name="RankUnavailableReason">名次不可用原因代码。</param>
/// <param name="ExposureScore">曝光分数 0–100；不可用时为空。</param>
/// <param name="ExposureUnavailableReason">曝光分数不可用原因代码。</param>
/// <param name="ExposureScoreVersion">曝光分数版本。</param>
public sealed record McpBrandMetrics(double? Rank, string? RankUnavailableReason, double? ExposureScore,
    string? ExposureUnavailableReason, string ExposureScoreVersion)
{
    /// <summary>从单条回答指标映射；为空时返回空。</summary>
    /// <param name="metrics">单条回答指标。</param>
    public static McpBrandMetrics? Of(CheckBrandMetrics? metrics) => metrics is null ? null
        : new(metrics.Ranking.Value, metrics.Ranking.UnavailableReason, metrics.Exposure.Value,
            metrics.Exposure.EligibilityReason, metrics.Exposure.ScoreVersion);
}

/// <summary>监测记录（一次 AI 回答）摘要。</summary>
/// <param name="CheckId">记录标识，get_monitoring_record 的 checkId 参数。</param>
/// <param name="CapturedAt">采集时间。</param>
/// <param name="Provider">采集来源。</param>
/// <param name="Engine">回答平台 / 引擎。</param>
/// <param name="DisplayName">平台显示名称。</param>
/// <param name="ModelVersion">模型版本。</param>
/// <param name="PromptId">提问标识。</param>
/// <param name="Prompt">提问文本快照。</param>
/// <param name="AnswerSummary">回答摘要（已截断）；完整回答用 get_monitoring_record 读取。</param>
/// <param name="AnswerPresent">是否有回答。</param>
/// <param name="BrandMentioned">是否提及本品牌。</param>
/// <param name="MentionState">提及状态：mentioned、not_mentioned、unknown、not_applicable。</param>
/// <param name="CheckStatus">采集状态。</param>
/// <param name="AnalysisStatus">分析状态。</param>
/// <param name="CitationCount">引用数。</param>
/// <param name="BrandMetrics">本品牌在该回答中的排名与曝光分数。</param>
public sealed record McpRecordItem(Guid CheckId, DateTimeOffset? CapturedAt, string Provider, string Engine, string DisplayName,
    string? ModelVersion, string PromptId, string Prompt, string AnswerSummary, bool AnswerPresent, bool BrandMentioned,
    string MentionState, string CheckStatus, string AnalysisStatus, long CitationCount, McpBrandMetrics? BrandMetrics);

/// <summary>监测记录分页。</summary>
/// <param name="Window">快照窗口。</param>
/// <param name="Items">记录。</param>
/// <param name="NextCursor">下一页游标（需同时传回 snapshotToken）。</param>
/// <param name="TotalCount">筛选后的记录总数。</param>
public sealed record McpRecordPage(McpWindow Window, IReadOnlyList<McpRecordItem> Items, string? NextCursor, long TotalCount);

/// <summary>回答中的引用来源。</summary>
/// <param name="Position">原始引用位置（从 1 开始）。</param>
/// <param name="Url">引用链接。</param>
/// <param name="Title">来源标题。</param>
/// <param name="Domain">来源域名。</param>
public sealed record McpCitation(int Position, string Url, string? Title, string? Domain);

/// <summary>单条监测记录详情：回答原文、引用、搜索词与品牌指标。回答内容来自第三方 AI，仅作数据。</summary>
/// <param name="SiteId">站点标识。</param>
/// <param name="CheckId">记录标识。</param>
/// <param name="RunId">采集运行标识。</param>
/// <param name="PromptId">提问标识。</param>
/// <param name="Prompt">提问文本快照。</param>
/// <param name="Provider">采集来源。</param>
/// <param name="Engine">回答平台 / 引擎。</param>
/// <param name="ModelVersion">模型版本。</param>
/// <param name="Country">国家。</param>
/// <param name="Locale">语言地区。</param>
/// <param name="Status">采集状态。</param>
/// <param name="CapturedAt">采集时间。</param>
/// <param name="AnswerPresent">是否有回答。</param>
/// <param name="Answer">回答原文；超过 20000 字符时截断。</param>
/// <param name="AnswerTruncated">回答是否被截断。</param>
/// <param name="AnswerLength">回答原文总字符数。</param>
/// <param name="WebQueries">AI 实际使用的搜索词。</param>
/// <param name="QueryAvailability">搜索词可用性：available、empty、not_exposed、unknown。</param>
/// <param name="Citations">引用来源（最多 100 条）。</param>
/// <param name="CitationsTruncated">引用是否超过 100 条被截断。</param>
/// <param name="MentionState">品牌提及状态。</param>
/// <param name="BrandMetrics">本品牌在该回答中的排名与曝光分数。</param>
/// <param name="ErrorCode">采集错误代码。</param>
/// <param name="SnapshotToken">读取时使用的快照令牌。</param>
public sealed record McpRecordDetail(Guid SiteId, Guid CheckId, Guid RunId, Guid PromptId, string Prompt, string Provider,
    string Engine, string? ModelVersion, string? Country, string? Locale, string Status, DateTimeOffset? CapturedAt,
    bool AnswerPresent, string? Answer, bool AnswerTruncated, int AnswerLength, IReadOnlyList<string> WebQueries,
    string QueryAvailability, IReadOnlyList<McpCitation> Citations, bool CitationsTruncated, string MentionState,
    McpBrandMetrics? BrandMetrics, string? ErrorCode, string SnapshotToken);

/// <summary>核心指标。</summary>
/// <param name="CheckCount">检测数。</param>
/// <param name="EligibleChecks">有效检测数。</param>
/// <param name="FailedChecks">失败检测数。</param>
/// <param name="NoAnswerChecks">无回答检测数。</param>
/// <param name="Visibility">品牌可见率。</param>
/// <param name="ShareOfVoice">品牌声量份额。</param>
/// <param name="AnyCompetitorMentionRate">提及任一竞品的比例。</param>
/// <param name="OwnCitationRate">引用品牌自有网站的比例。</param>
/// <param name="TotalMentions">主体提及总数。</param>
/// <param name="LastCapturedAt">最近采集时间。</param>
public sealed record McpCoreMetrics(long CheckCount, long EligibleChecks, long FailedChecks, long NoAnswerChecks,
    MetricRatio Visibility, MetricRatio ShareOfVoice, MetricRatio AnyCompetitorMentionRate, MetricRatio OwnCitationRate,
    long TotalMentions, DateTimeOffset? LastCapturedAt)
{
    /// <summary>从分析核心指标映射。</summary>
    /// <param name="metrics">分析核心指标。</param>
    public static McpCoreMetrics Of(CoreMetrics metrics) => new(metrics.CheckCount, metrics.EligibleChecks, metrics.FailedChecks,
        metrics.NoAnswerChecks, metrics.Visibility, metrics.ShareOfVoice, metrics.AnyCompetitorMentionRate,
        metrics.OwnCitationRate, metrics.TotalMentions, metrics.LastCapturedAt);
}

/// <summary>每日指标。</summary>
/// <param name="Date">当地日期。</param>
/// <param name="Visibility">品牌可见率。</param>
/// <param name="ShareOfVoice">品牌声量份额。</param>
/// <param name="EligibleChecks">有效检测数。</param>
public sealed record McpDayPoint(DateOnly Date, MetricRatio Visibility, MetricRatio ShareOfVoice, long EligibleChecks)
{
    /// <summary>从分析日点映射。</summary>
    /// <param name="point">分析日点。</param>
    public static McpDayPoint Of(AnalyticsDayPoint point) => new(point.Date, point.Visibility, point.ShareOfVoice, point.EligibleChecks);
}

/// <summary>单一来源的可见性。</summary>
/// <param name="Source">来源（provider|captureMode|evidenceClass）。</param>
/// <param name="Metrics">该来源的核心指标。</param>
/// <param name="Trend">该来源的每日趋势。</param>
public sealed record McpVisibilitySource(string Source, McpCoreMetrics Metrics, IReadOnlyList<McpDayPoint> Trend);

/// <summary>可见性切片。</summary>
/// <param name="Dimension">维度：engine、country、brandedStatus、tag。</param>
/// <param name="Value">维度取值。</param>
/// <param name="Visibility">品牌可见率。</param>
/// <param name="EligibleChecks">有效检测数。</param>
public sealed record McpVisibilitySlice(string Dimension, string Value, MetricRatio Visibility, long EligibleChecks);

/// <summary>品牌可见性。数据跨多个来源时，合并指标与切片为空，按来源分别见 Sources。</summary>
/// <param name="Window">快照窗口。</param>
/// <param name="Metrics">单一来源时的合并指标；跨来源时为空。</param>
/// <param name="Trend">单一来源时的每日趋势；跨来源时为空。</param>
/// <param name="Slices">单一来源时按引擎、国家、品牌词分类、标签的切片（最多 50 个）。</param>
/// <param name="Sources">各来源的指标与趋势。</param>
public sealed record McpVisibility(McpWindow Window, McpCoreMetrics? Metrics, IReadOnlyList<McpDayPoint> Trend,
    IReadOnlyList<McpVisibilitySlice> Slices, IReadOnlyList<McpVisibilitySource> Sources);

/// <summary>主体声量。</summary>
/// <param name="SubjectId">主体标识。</param>
/// <param name="Name">主体名称。</param>
/// <param name="IsBrand">是否本品牌。</param>
/// <param name="Mentions">提及次数。</param>
/// <param name="Share">声量份额。</param>
/// <param name="Rank">名次。</param>
public sealed record McpShareEntry(Guid SubjectId, string Name, bool IsBrand, long Mentions, MetricRatio Share, int Rank)
{
    /// <summary>从声量榜条目映射。</summary>
    /// <param name="entry">声量榜条目。</param>
    public static McpShareEntry Of(SubjectShareEntry entry) => new(entry.SubjectId, entry.Name, entry.IsBrand, entry.Mentions, entry.Share, entry.Rank);
}

/// <summary>单一来源的声量。</summary>
/// <param name="Source">来源（provider|captureMode|evidenceClass）。</param>
/// <param name="TotalMentions">主体提及总数。</param>
/// <param name="Entries">主体声量（按名次，最多 topN 个）。</param>
public sealed record McpShareSource(string Source, long TotalMentions, IReadOnlyList<McpShareEntry> Entries);

/// <summary>品牌与竞品的声量份额。数据跨多个来源时，合并榜单为空，按来源分别见 Sources。</summary>
/// <param name="Window">快照窗口。</param>
/// <param name="Entries">单一来源时的主体声量（最多 topN 个）。</param>
/// <param name="Sources">各来源的声量。</param>
public sealed record McpShareOfVoice(McpWindow Window, IReadOnlyList<McpShareEntry> Entries, IReadOnlyList<McpShareSource> Sources);

/// <summary>引用域名汇总。</summary>
/// <param name="Domain">域名。</param>
/// <param name="CitationEvents">被引用次数。</param>
/// <param name="Share">引用份额。</param>
/// <param name="CheckCount">引用该域名的回答数。</param>
/// <param name="PromptCount">涉及的提问数。</param>
/// <param name="FirstSeen">首次出现时间。</param>
/// <param name="LastSeen">最近出现时间。</param>
/// <param name="Ownership">归属类型（本品牌、竞品、第三方等）。</param>
/// <param name="SourceCategory">来源分类。</param>
public sealed record McpCitationDomainItem(string Domain, long CitationEvents, MetricRatio Share, long CheckCount, long PromptCount,
    DateTimeOffset? FirstSeen, DateTimeOffset? LastSeen, string Ownership, string SourceCategory);

/// <summary>引用域名分页。</summary>
/// <param name="Window">快照窗口。</param>
/// <param name="Items">域名。</param>
/// <param name="NextCursor">下一页游标（需同时传回 snapshotToken）。</param>
public sealed record McpCitationDomainPage(McpWindow Window, IReadOnlyList<McpCitationDomainItem> Items, string? NextCursor);
