namespace Jogeoai.Domain;

/// <summary>文章链接每日监控的固定积分单价。</summary>
public static class ArticleMonitoringPricing
{
    /// <summary>每条追踪中的有效发布链接每个品牌本地自然日的积分单价。</summary>
    public const decimal UnitPrice = 10m;

    /// <summary>按可计费链接数估算一轮监控积分。</summary>
    public static decimal Estimate(int linkCount) => Math.Max(0, linkCount) * UnitPrice;

    /// <summary>自动发现的回溯窗口天数（含今天共 30 个本地自然日）。</summary>
    public const int BackfillDays = 30;
}

/// <summary>品牌时区自然日换算。</summary>
public static class ArticleMonitoringCalendar
{
    /// <summary>按时区标识解析时区；无法解析时回退 UTC。</summary>
    public static TimeZoneInfo ResolveZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId)) return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId.Trim()); }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        { return TimeZoneInfo.Utc; }
    }

    /// <summary>UTC 时刻对应的本地自然日。</summary>
    public static DateOnly LocalDate(DateTimeOffset utcNow, TimeZoneInfo zone)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow, zone).DateTime);

    /// <summary>某本地自然日的次日 00:00 对应的 UTC 时刻；轮次在此之后结算。</summary>
    public static DateTimeOffset NextLocalMidnightUtc(DateOnly localDate, TimeZoneInfo zone)
        => new(TimeZoneInfo.ConvertTimeToUtc(localDate.AddDays(1).ToDateTime(TimeOnly.MinValue), zone));

    /// <summary>自动发现首次回溯窗口的 UTC 起点：本地今天 00:00 往前 BackfillDays − 1 天。</summary>
    public static DateTimeOffset BackfillFromUtc(DateTimeOffset utcNow, TimeZoneInfo zone)
        => new(TimeZoneInfo.ConvertTimeToUtc(
            LocalDate(utcNow, zone).AddDays(-(ArticleMonitoringPricing.BackfillDays - 1)).ToDateTime(TimeOnly.MinValue), zone));
}

/// <summary>自动发现文章的来源摘要。</summary>
/// <param name="CheckId">最早引用该链接的回答 Check。</param>
/// <param name="DiscoveredAt">自动发现时间（UTC）。</param>
/// <param name="Provider">该回答的采集 Provider。</param>
/// <param name="Engine">该回答的大模型引擎。</param>
/// <param name="CaptureMode">该回答保存的采集模式（api、scraped 或 app）；没有保存时为空。</param>
public sealed record ArticleDiscoverySummary(Guid CheckId, DateTimeOffset DiscoveredAt, string Provider, string Engine,
    string? CaptureMode = null)
{
    /// <summary>该回答的采集渠道，由 Provider 推导（全部接口同一规则）：app_collector 为 app，domestic_collector 为 web，其余为 api。</summary>
    public CollectionChannel Channel => MonitoringProviders.Channel(Provider);
}

/// <summary>某一轮（品牌本地自然日）链接监控计费。</summary>
/// <param name="RoundId">轮次标识。</param>
/// <param name="LocalDate">品牌时区自然日。</param>
/// <param name="TimeZone">轮次使用的 IANA 时区。</param>
/// <param name="Status">轮次状态：held（已预占）、captured（已结算）、released（已释放）、skipped（已跳过）。</param>
/// <param name="LinkCount">开轮时的可计费链接数。</param>
/// <param name="EstimatedCredits">预计积分。</param>
/// <param name="HeldCredits">仍在预留中的积分。</param>
/// <param name="CapturedCredits">已结算积分。</param>
/// <param name="ReleasedCredits">已释放积分。</param>
/// <param name="SkipCode">跳过原因码（如 CREDIT_INSUFFICIENT）；未跳过为空。</param>
/// <param name="SkipMessage">跳过说明；未跳过为空。</param>
/// <param name="CreatedAt">开轮时间（UTC）。</param>
/// <param name="SettledAt">结算时间（UTC）；未结算为空。</param>
public sealed record ArticleMonitoringRoundView(Guid RoundId, DateOnly LocalDate, string TimeZone, string Status,
    int LinkCount, decimal EstimatedCredits, decimal HeldCredits, decimal CapturedCredits, decimal ReleasedCredits,
    string? SkipCode, string? SkipMessage, DateTimeOffset CreatedAt, DateTimeOffset? SettledAt);

/// <summary>品牌文章链接监控的计费摘要。</summary>
/// <param name="UnitPrice">每条链接每日积分单价。</param>
/// <param name="BillableLinkCount">当前可计费链接数：追踪中文章下未停用的发布链接。</param>
/// <param name="NextEstimate">按当前可计费链接数估算的下一轮积分。</param>
/// <param name="AvailableCredits">团队当前可用积分。</param>
/// <param name="TimeZone">轮次使用的品牌时区。</param>
/// <param name="TodayStatus">今日轮次状态；尚未开轮为 none。</param>
/// <param name="Today">今日轮次；尚未开轮为空。</param>
/// <param name="RecentRounds">最近 7 轮（含今日），按日期倒序。</param>
public sealed record ArticleMonitoringBillingView(decimal UnitPrice, int BillableLinkCount, decimal NextEstimate,
    decimal AvailableCredits, string TimeZone, string TodayStatus, ArticleMonitoringRoundView? Today,
    IReadOnlyList<ArticleMonitoringRoundView> RecentRounds);

/// <summary>一次发现通过的结果。</summary>
/// <param name="Scanned">本次扫描到的候选 URL 数。</param>
/// <param name="Created">新建的自动发现文章数。</param>
/// <param name="SkippedExisting">因已登记（含已停用）而跳过的 URL 数。</param>
/// <param name="SkipReason">整个品牌被跳过的原因；正常处理时为空。</param>
public sealed record ArticleDiscoveryPassResult(int Scanned, int Created, int SkippedExisting, string? SkipReason = null);

/// <summary>Worker 一轮处理的汇总。</summary>
/// <param name="Sites">处理的品牌数。</param>
/// <param name="Discovered">新建的自动发现文章数。</param>
/// <param name="TitlesRead">本轮读取标题的链接数。</param>
/// <param name="RoundsOpened">新开的计费轮次数（含跳过）。</param>
/// <param name="RoundsSettled">结算的轮次数。</param>
public sealed record ArticleLinkMonitoringPassResult(int Sites, int Discovered, int TitlesRead, int RoundsOpened, int RoundsSettled);
