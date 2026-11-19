using System.Globalization;

namespace Jogeoai.Domain;

/// <summary>按 IANA 时区计算每日或每周计划的唯一当地时隙。</summary>
public static class CollectionScheduleCalendar
{
    /// <summary>解析 IANA 时区；UTC 也可直接使用。</summary>
    public static TimeZoneInfo ParseTimeZone(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 100
            || value != "UTC" && (!value.Contains('/', StringComparison.Ordinal) || value.Contains("..", StringComparison.Ordinal)))
            throw new CollectorStoreException("INVALID_TIME_ZONE", "timeZone 必须是有效的 IANA 时区。", 400);
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(value);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new CollectorStoreException("INVALID_TIME_ZONE", "timeZone 必须是有效的 IANA 时区。", 400);
        }
    }

    /// <summary>解析 HH:mm 当地时刻。</summary>
    public static TimeOnly ParseLocalTime(string value)
    {
        if (!TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            throw new CollectorStoreException("INVALID_LOCAL_TIME", "localTime 必须是 HH:mm 格式。", 400);
        return time;
    }

    /// <summary>返回指定 UTC 时刻之后的第一个计划时隙。</summary>
    public static DateTimeOffset NextAfter(string cadence, int? dayOfWeek, TimeOnly localTime,
        TimeZoneInfo zone, DateTimeOffset afterUtc)
    {
        ValidateCadence(cadence, dayOfWeek);
        var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(afterUtc, zone).DateTime);
        for (var i = 0; i < 367; i++)
        {
            var candidateDate = localDate.AddDays(i);
            if (cadence == "weekly" && (int)candidateDate.DayOfWeek != dayOfWeek) continue;
            var utc = ResolveSlot(candidateDate, localTime, zone);
            if (utc > afterUtc.ToUniversalTime()) return utc;
        }
        throw new InvalidOperationException("无法计算计划的下一个时隙。");
    }

    /// <summary>合并多个到期时隙，仅补最近一个并计算较早时隙数量。</summary>
    public static CollectionDueWindow DueWindow(string cadence, int? dayOfWeek, TimeOnly localTime,
        TimeZoneInfo zone, DateTimeOffset firstDueUtc, DateTimeOffset nowUtc)
    {
        if (firstDueUtc > nowUtc) throw new ArgumentOutOfRangeException(nameof(firstDueUtc));
        var cursor = firstDueUtc.ToUniversalTime();
        var count = 0;
        DateTimeOffset latest = cursor;
        while (cursor <= nowUtc)
        {
            if (++count > 100_000) throw new InvalidOperationException("计划积压时隙过多。");
            latest = cursor;
            cursor = NextAfter(cadence, dayOfWeek, localTime, zone, cursor);
        }
        var originalDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(latest, zone).DateTime);
        return new CollectionDueWindow(latest, cursor, count - 1, LocalSlotKey(originalDate, localTime));
    }

    /// <summary>当地原始日期及时刻的稳定时隙键；DST 顺延后仍使用原始计划时刻。</summary>
    public static string LocalSlotKey(DateOnly date, TimeOnly localTime) =>
        string.Create(CultureInfo.InvariantCulture, $"{date:yyyyMMdd}T{localTime:HHmm}");

    private static DateTimeOffset ResolveSlot(DateOnly date, TimeOnly localTime, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(localTime, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
            if (DateOnly.FromDateTime(local) != date)
                throw new InvalidOperationException("计划时刻后的当日没有有效当地时间。");
        }
        // Ambiguous wall clock: select the earlier UTC instant. The local key is shared by both offsets.
        var offset = zone.IsAmbiguousTime(local)
            ? zone.GetAmbiguousTimeOffsets(local).Max()
            : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    private static void ValidateCadence(string cadence, int? dayOfWeek)
    {
        if (cadence is not ("daily" or "weekly") || cadence == "weekly" && dayOfWeek is not (>= 0 and <= 6)
            || cadence == "daily" && dayOfWeek is not null)
            throw new CollectorStoreException("INVALID_CADENCE", "cadence 必须是 daily 或带星期 0–6 的 weekly。", 400);
    }
}

/// <summary>到期时隙合并结果。</summary>
/// <param name="LatestDueAt">本轮补跑的最近 UTC 时隙。</param>
/// <param name="NextSlotAt">未来的下次 UTC 时隙。</param>
/// <param name="MissedSlots">跳过的较早时隙数量。</param>
/// <param name="LocalSlotKey">原始当地日期及时刻的稳定键。</param>
public sealed record CollectionDueWindow(DateTimeOffset LatestDueAt, DateTimeOffset NextSlotAt,
    int MissedSlots, string LocalSlotKey);
