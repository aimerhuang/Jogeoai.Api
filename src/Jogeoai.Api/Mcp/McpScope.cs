using System.Globalization;
using System.Text.RegularExpressions;
using ModelContextProtocol;

namespace Jogeoai.Api.Mcp;

/// <summary>工具返回给 MCP 客户端的错误；<see cref="Outcome"/> 写入审计日志，消息不含内部细节。</summary>
/// <param name="outcome">审计结果码：denied、invalid_argument、conflict、unavailable、error。</param>
/// <param name="message">返回给客户端的中文说明。</param>
public sealed class McpToolException(string outcome, string message) : McpException(message)
{
    /// <summary>审计结果码。</summary>
    public string Outcome { get; } = outcome;
}

/// <summary>令牌范围校验与工具参数解析。越权与不存在使用同一提示，不区分两者。</summary>
public static partial class McpScope
{
    /// <summary>站点越权或不存在时的统一提示。</summary>
    public const string SiteDenied = "站点不存在或无权访问。";

    /// <summary>记录越权或不存在时的统一提示。</summary>
    public const string RecordDenied = "记录不存在或无权访问。";

    /// <summary>默认查询天数（含今天）。</summary>
    public const int DefaultDays = 7;

    /// <summary>单次查询最长天数。</summary>
    public const int MaxDays = 90;

    /// <summary>令牌范围不含该站点时抛出无权限错误。</summary>
    /// <param name="caller">当前调用方。</param>
    /// <param name="siteId">请求的站点。</param>
    public static void RequireSiteInToken(McpCallerContext caller, Guid siteId)
    {
        if (!caller.AllowsSite(siteId)) throw new McpToolException("denied", SiteDenied);
    }

    /// <summary>分页条数：省略时取默认值，超出 1–max 返回参数错误。</summary>
    /// <param name="limit">请求的条数。</param>
    /// <param name="fallback">默认条数。</param>
    /// <param name="max">上限。</param>
    public static int Limit(int? limit, int fallback, int max)
    {
        var value = limit ?? fallback;
        if (value < 1 || value > max) throw new McpToolException("invalid_argument", $"limit 必须在 1–{max} 之间。");
        return value;
    }

    /// <summary>
    /// 解析查询时间范围。from / to 接受 yyyy-MM-dd（按品牌时区的自然日，to 含当天）或带时区的 ISO 时间；
    /// 省略 to 为当前时间，省略 from 为 to 所在当地日期往前共 7 天的 0 点；跨度不超过 90 个当地自然日。
    /// </summary>
    /// <param name="from">起始。</param>
    /// <param name="to">结束。</param>
    /// <param name="timeZone">品牌 IANA 时区。</param>
    /// <param name="now">当前 UTC 时间。</param>
    public static (DateTimeOffset From, DateTimeOffset To) Range(string? from, string? to, string timeZone, DateTimeOffset now)
    {
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone); }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        { zone = TimeZoneInfo.Utc; }

        DateTimeOffset start, end;
        int days;
        try
        {
            end = string.IsNullOrWhiteSpace(to) ? now : Boundary(to.Trim(), "to", zone, endOfDay: true);
            start = string.IsNullOrWhiteSpace(from)
                ? LocalMidnight(LocalDate(end.AddTicks(-1), zone).AddDays(-(DefaultDays - 1)), zone)
                : Boundary(from.Trim(), "from", zone, endOfDay: false);
            if (start >= end) throw new McpToolException("invalid_argument", "from 必须早于 to。");
            days = LocalDate(end.AddTicks(-1), zone).DayNumber - LocalDate(start, zone).DayNumber + 1;
        }
        catch (ArgumentOutOfRangeException)
        {
            // 0001-01-01、9999-12-31 等极端日期在加减一天或换算时区时越界。
            throw new McpToolException("invalid_argument", "from / to 超出可查询的日期范围。");
        }
        if (days > MaxDays) throw new McpToolException("invalid_argument", $"时间范围最长 {MaxDays} 天。");
        return (start, end);
    }

    private static DateTimeOffset Boundary(string value, string key, TimeZoneInfo zone, bool endOfDay)
    {
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return LocalMidnight(endOfDay ? day.AddDays(1) : day, zone);
        if (ZoneSuffix().IsMatch(value)
            && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant))
            return instant.ToUniversalTime();
        throw new McpToolException("invalid_argument", $"{key} 必须是 yyyy-MM-dd 日期或带时区的 ISO 时间。");
    }

    private static DateOnly LocalDate(DateTimeOffset value, TimeZoneInfo zone)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, zone).DateTime);

    // 夏令时在 0 点跳变的时区（如 America/Santiago）当地 0 点不存在，ConvertTimeToUtc 会抛异常；顺延到当日首个有效时刻。
    private static DateTimeOffset LocalMidnight(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        return new(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }

    [GeneratedRegex(@"(Z|[+-]\d{2}:\d{2})$", RegexOptions.IgnoreCase)]
    private static partial Regex ZoneSuffix();
}
