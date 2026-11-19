using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;

namespace Jogeoai.Tests;

/// <summary>积分用量聚合（B2）与 expire 流水映射（B4）的无库单测。</summary>
public sealed class CreditUsageTests
{
    private static readonly TimeZoneInfo Shanghai = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static CreditUsageRecord Article(string utc, decimal credits = 100) => new(DateTimeOffset.Parse(utc), true, false, credits);
    private static CreditUsageRecord Monitoring(string utc, decimal credits = 10) => new(DateTimeOffset.Parse(utc), false, true, credits);
    private static CreditUsageRecord Other(string utc, decimal credits) => new(DateTimeOffset.Parse(utc), false, false, credits);

    [Fact]
    public void BucketsByLocalDayAcrossTheUtcBoundaryInEastEight()
    {
        // 17:30Z 是上海 10-20 01:30；15:00Z 是上海 10-19 23:00。
        var view = CreditUsageAggregator.Build(new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 20), "Asia/Shanghai", Shanghai,
            [Article("2026-10-19T17:30:00Z"), Monitoring("2026-10-19T15:00:00Z"), Monitoring("2026-10-19T15:10:00Z")], null);

        Assert.Equal([new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 20)], view.Days.Select(d => d.Date));
        var first = view.Days[0];
        Assert.Equal(20m, first.Total);
        var monitoring = Assert.Single(first.Items);
        Assert.Equal((CreditUsageType.PromptMonitoring, 20m, 2), (monitoring.Type, monitoring.Credits, monitoring.Count));
        Assert.Equal(100m, view.Days[1].Total);
        Assert.Equal(2, view.ActiveDays);
        Assert.Equal(120m, view.TotalCredits);
    }

    [Fact]
    public void UtcWindowCoversWholeLocalDays()
    {
        var (from, to) = CreditUsageAggregator.UtcWindow(new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 20), Shanghai);
        Assert.Equal(DateTimeOffset.Parse("2026-10-18T16:00:00Z"), from);
        Assert.Equal(DateTimeOffset.Parse("2026-10-20T16:00:00Z"), to);
        Assert.Equal(new DateOnly(2026, 10, 20), CreditUsageAggregator.LocalDate(DateTimeOffset.Parse("2026-10-19T17:30:00Z"), Shanghai));
    }

    [Fact]
    public void TypeIsArticleFirstThenMonitoringThenOther()
    {
        Assert.Equal(CreditUsageType.ArticleGeneration, CreditUsageAggregator.TypeOf(new(DateTimeOffset.UtcNow, true, true, 1)));
        Assert.Equal(CreditUsageType.PromptMonitoring, CreditUsageAggregator.TypeOf(new(DateTimeOffset.UtcNow, false, true, 1)));
        Assert.Equal(CreditUsageType.Other, CreditUsageAggregator.TypeOf(new(DateTimeOffset.UtcNow, false, false, 1)));
    }

    [Fact]
    public void TotalsAreDescendingAndDropZeroCreditTypesAndZeroDays()
    {
        var view = CreditUsageAggregator.Build(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), "UTC", TimeZoneInfo.Utc,
            [
                Monitoring("2026-10-02T01:00:00Z"), Monitoring("2026-10-02T02:00:00Z"), Monitoring("2026-10-03T02:00:00Z"),
                Article("2026-10-03T03:00:00Z", 25),
                // 关联缺失的 capture 行金额为 0：不计入条数，也不产生活跃日。
                Other("2026-10-05T00:00:00Z", 0), Article("2026-10-06T00:00:00Z", 0),
                // 区间外的行不计入。
                Article("2026-11-01T00:00:00Z"),
            ], null);

        Assert.Equal([CreditUsageType.PromptMonitoring, CreditUsageType.ArticleGeneration], view.Totals.Select(t => t.Type));
        Assert.Equal([30m, 25m], view.Totals.Select(t => t.Credits));
        Assert.Equal([3, 1], view.Totals.Select(t => t.Count));
        Assert.Equal(55m, view.TotalCredits);
        Assert.Equal(2, view.ActiveDays);
        Assert.Equal([new DateOnly(2026, 10, 2), new DateOnly(2026, 10, 3)], view.Days.Select(d => d.Date));
        Assert.Equal([CreditUsageType.ArticleGeneration, CreditUsageType.PromptMonitoring], view.Days[1].Items.Select(i => i.Type));
        Assert.Null(view.Latest);
    }

    [Fact]
    public void LatestIsCarriedEvenWhenOutsideTheWindow()
    {
        var latest = Monitoring("2026-12-01T05:00:00Z");
        var view = CreditUsageAggregator.Build(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 2), "UTC", TimeZoneInfo.Utc, [], latest);
        Assert.Empty(view.Days);
        Assert.Equal(0, view.ActiveDays);
        Assert.NotNull(view.Latest);
        Assert.Equal((CreditUsageType.PromptMonitoring, 10m, latest.OccurredAt), (view.Latest!.Type, view.Latest.Credits, view.Latest.OccurredAt));
    }

    [Fact]
    public void RangeValidationRejectsReversedOrOverlongWindows()
    {
        CreditUsageAggregator.ValidateRange(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1));
        CreditUsageAggregator.ValidateRange(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1));
        var reversed = Assert.Throws<CollectorStoreException>(() => CreditUsageAggregator.ValidateRange(new DateOnly(2026, 1, 2), new DateOnly(2026, 1, 1)));
        Assert.Equal(("INVALID_TIME_RANGE", 400), (reversed.Code, reversed.StatusCode));
        var overlong = Assert.Throws<CollectorStoreException>(() => CreditUsageAggregator.ValidateRange(new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 2)));
        Assert.Equal("INVALID_TIME_RANGE", overlong.Code);
    }

    [Fact]
    public void ExpireLedgerRowsMapToTheExpireEntryType()
    {
        var toLedger = typeof(CreditStore).GetMethod("ToLedger", BindingFlags.NonPublic | BindingFlags.Static)!;
        var entry = (CreditLedgerEntryView)toLedger.Invoke(null, [new[]
        {
            Guid.NewGuid().ToString(), "expire", "-500.000", "0.000", "", "", "", "plan-expire:x:y:1", "第 1 周期结束，清零可用积分", "2026-10-20 00:00:00.000000",
        }])!;
        Assert.Equal(CreditLedgerEntryType.Expire, entry.EntryType);
        Assert.Equal(-500m, entry.Amount);
        Assert.Contains("\"entryType\":\"expire\"", JsonSerializer.Serialize(entry, Json));
        // 未知值仍回退为 reversal，不抛异常。
        var unknown = (CreditLedgerEntryView)toLedger.Invoke(null, [new[]
        {
            Guid.NewGuid().ToString(), "mystery", "1", "1", "", "", "", "ref", "", "2026-10-20 00:00:00.000000",
        }])!;
        Assert.Equal(CreditLedgerEntryType.Reversal, unknown.EntryType);
    }
}
