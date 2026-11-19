using System.Text.Json.Serialization;

namespace Jogeoai.Domain;

/// <summary>积分用量操作类型。</summary>
public enum CreditUsageType
{
    /// <summary>AI 文章生成：关联文章预占的结算流水。</summary>
    [JsonStringEnumMemberName("article_generation")] ArticleGeneration,
    /// <summary>AI 回答采集：关联每日监控计费项的结算流水。</summary>
    [JsonStringEnumMemberName("prompt_monitoring")] PromptMonitoring,
    /// <summary>文章链接监控：关联每日链接监控轮次的结算流水。</summary>
    [JsonStringEnumMemberName("article_monitoring")] ArticleMonitoring,
    /// <summary>智能微调：关联微调操作（article_refine_operations，迁移 041）的结算流水，每次 40 积分。</summary>
    [JsonStringEnumMemberName("article_refine")] ArticleRefine,
    /// <summary>其他结算流水；当前没有生产写入路径，仅作兜底。</summary>
    [JsonStringEnumMemberName("other")] Other
}

/// <summary>积分用量查询参数；省略时按团队时区取最近 30 个自然日。</summary>
/// <param name="From">起始自然日（含）；默认结束日前 29 天。</param>
/// <param name="To">结束自然日（含）；默认团队时区的今天。</param>
/// <param name="TimeZone">IANA 时区；省略时取最早创建的启用品牌时区，没有品牌时为 Asia/Shanghai。</param>
public sealed record CreditUsageRequest(DateOnly? From = null, DateOnly? To = null, string? TimeZone = null);

/// <summary>某操作类型的积分用量。</summary>
/// <param name="Type">操作类型。</param>
/// <param name="Credits">结算积分合计。</param>
/// <param name="Count">结算笔数：文章类为篇数，提问监控为检测条数，文章链接监控为轮次数，智能微调为次数。</param>
/// <param name="Channel">
/// 采集渠道，只有提问监控（prompt_monitoring）有值：api 或 app，由检测的 Provider 推导（与回答记录同一规则）。
/// 同一类型在两个渠道都有消耗时各占一条；其他类型为空。
/// </param>
public sealed record CreditUsageItem(CreditUsageType Type, decimal Credits, int Count, CollectionChannel? Channel = null);

/// <summary>某自然日的积分用量。</summary>
/// <param name="Date">团队时区的自然日。</param>
/// <param name="Items">按类型拆分的用量，按积分降序。</param>
/// <param name="Total">当日积分合计。</param>
public sealed record CreditUsageDay(DateOnly Date, IReadOnlyList<CreditUsageItem> Items, decimal Total);

/// <summary>最近一笔结算，不受查询区间限制。</summary>
/// <param name="Type">操作类型。</param>
/// <param name="Credits">结算积分。</param>
/// <param name="OccurredAt">结算 UTC 时间。</param>
/// <param name="Channel">采集渠道，只有提问监控有值（api 或 app）；其他类型为空。</param>
public sealed record CreditUsageLatest(CreditUsageType Type, decimal Credits, DateTimeOffset OccurredAt,
    CollectionChannel? Channel = null);

/// <summary>按自然日与操作类型聚合的积分用量。</summary>
/// <param name="From">起始自然日（含）。</param>
/// <param name="To">结束自然日（含）。</param>
/// <param name="TimeZone">用于切分自然日的 IANA 时区。</param>
/// <param name="Days">只含有消耗的日期，按日期升序。</param>
/// <param name="Totals">区间内各类型合计，按积分降序；不含积分为 0 的类型。</param>
/// <param name="TotalCredits">区间内积分合计。</param>
/// <param name="ActiveDays">活跃日数：当日结算合计大于 0 的自然日数。</param>
/// <param name="Latest">最近一笔结算；没有结算时为空。</param>
public sealed record CreditUsageView(DateOnly From, DateOnly To, string TimeZone,
    IReadOnlyList<CreditUsageDay> Days, IReadOnlyList<CreditUsageItem> Totals,
    decimal TotalCredits, int ActiveDays, CreditUsageLatest? Latest);

/// <summary>一条 capture 流水的用量事实；金额来自预占或监控计费项，不是流水本身的 amount。</summary>
/// <param name="OccurredAt">流水 UTC 时间。</param>
/// <param name="IsArticle">是否关联文章预占。</param>
/// <param name="IsMonitoring">是否关联监控计费项。</param>
/// <param name="Credits">结算积分。</param>
/// <param name="IsArticleMonitoring">是否关联文章链接监控轮次。</param>
/// <param name="IsArticleRefine">是否关联智能微调操作。</param>
/// <param name="MonitoringProvider">关联的监控检测的 Provider，用来推导渠道；不关联监控检测或检测已不存在时为空。</param>
public sealed record CreditUsageRecord(DateTimeOffset OccurredAt, bool IsArticle, bool IsMonitoring, decimal Credits,
    bool IsArticleMonitoring = false, bool IsArticleRefine = false, string? MonitoringProvider = null);

/// <summary>按团队时区自然日与操作类型聚合 capture 流水的纯函数。</summary>
public static class CreditUsageAggregator
{
    /// <summary>自然日跨度上限（含两端）。</summary>
    public const int MaxDays = 366;

    /// <summary>判定流水的操作类型：智能微调（只由 refine_operation_id 标记，不与其他关联并存）优先，其次文章生成、提问监控、文章链接监控，否则其他。</summary>
    public static CreditUsageType TypeOf(CreditUsageRecord record)
        => record.IsArticleRefine ? CreditUsageType.ArticleRefine
            : record.IsArticle ? CreditUsageType.ArticleGeneration
            : record.IsMonitoring ? CreditUsageType.PromptMonitoring
            : record.IsArticleMonitoring ? CreditUsageType.ArticleMonitoring : CreditUsageType.Other;

    /// <summary>
    /// 流水的采集渠道：只有提问监控有渠道，由检测的 Provider 推导（<see cref="MonitoringProviders.Channel"/>，全部接口同一规则）；
    /// 其他类型为空。
    /// </summary>
    public static CollectionChannel? ChannelOf(CreditUsageRecord record)
        => TypeOf(record) == CreditUsageType.PromptMonitoring ? MonitoringProviders.Channel(record.MonitoringProvider) : null;

    /// <summary>把时间戳换算为指定时区的自然日。</summary>
    public static DateOnly LocalDate(DateTimeOffset timestamp, TimeZoneInfo zone)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timestamp, zone).DateTime);

    /// <summary>自然日区间 [from 00:00, to+1 00:00) 对应的 UTC 查询窗口。</summary>
    public static (DateTimeOffset FromUtc, DateTimeOffset ToUtc) UtcWindow(DateOnly from, DateOnly to, TimeZoneInfo zone)
        => (new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(from.ToDateTime(TimeOnly.MinValue), zone)),
            new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(to.AddDays(1).ToDateTime(TimeOnly.MinValue), zone)));

    /// <summary>校验自然日区间：起点不晚于终点，且跨度不超过 366 天。</summary>
    public static void ValidateRange(DateOnly from, DateOnly to)
    {
        if (from > to || to.DayNumber - from.DayNumber + 1 > MaxDays)
            throw new CollectorStoreException("INVALID_TIME_RANGE", "日期范围必须递增且不超过 366 天。", 400);
    }

    /// <summary>按自然日与类型求和；积分为 0 的流水不计入条数，没有消耗的日期不输出。</summary>
    public static CreditUsageView Build(DateOnly from, DateOnly to, string timeZone, TimeZoneInfo zone,
        IReadOnlyList<CreditUsageRecord> records, CreditUsageRecord? latest)
    {
        var days = records
            .Where(r => r.Credits > 0)
            .GroupBy(r => LocalDate(r.OccurredAt, zone))
            .Where(g => g.Key >= from && g.Key <= to)
            .OrderBy(g => g.Key)
            .Select(g => new CreditUsageDay(g.Key, Items(g), g.Sum(r => r.Credits)))
            .Where(d => d.Total > 0)
            .ToArray();
        var totals = Items(days.SelectMany(d => d.Items.Select(i => (i.Type, i.Channel, i.Credits, i.Count))));
        return new CreditUsageView(from, to, timeZone, days, totals, totals.Sum(t => t.Credits), days.Length,
            latest is null ? null : new CreditUsageLatest(TypeOf(latest), latest.Credits, latest.OccurredAt, ChannelOf(latest)));
    }

    private static IReadOnlyList<CreditUsageItem> Items(IEnumerable<CreditUsageRecord> records)
        => Items(records.Select(r => (TypeOf(r), ChannelOf(r), r.Credits, 1)));

    // 按类型与渠道分组：只有提问监控带渠道，所以没有 App 消耗时条目与按类型分组完全相同。
    private static IReadOnlyList<CreditUsageItem> Items(
        IEnumerable<(CreditUsageType Type, CollectionChannel? Channel, decimal Credits, int Count)> parts)
        => parts.GroupBy(p => (p.Type, p.Channel))
            .Select(g => new CreditUsageItem(g.Key.Type, g.Sum(p => p.Credits), g.Sum(p => p.Count), g.Key.Channel))
            .Where(i => i.Credits > 0)
            .OrderByDescending(i => i.Credits).ThenBy(i => i.Type).ThenBy(i => i.Channel)
            .ToArray();
}
