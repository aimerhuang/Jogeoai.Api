using Jogeoai.Domain;

namespace Jogeoai.Tests;

/// <summary>
/// App 即时时隙的纯规则（11-27-app-first-cycle-now）：即时时隙键的识别、名义时刻、按平台锚点计算的首个周期截止时点。
/// 不需要数据库。
/// </summary>
public sealed class AppInstantSlotRulesTests
{
    private static readonly TimeZoneInfo Shanghai = CollectionScheduleCalendar.ParseTimeZone("Asia/Shanghai");

    // 2026-11-27 is a Friday.
    private static readonly DateOnly Friday = new(2026, 11, 27);

    [Theory]
    [InlineData("20261127T154201", true)]
    [InlineData("20261127T091100", true)]
    [InlineData("20261127T156001", false)]
    [InlineData("20261127T1542", true)]
    [InlineData("20261127T0000", true)]
    [InlineData("20261127T0910", true)]
    [InlineData("20261127T0911", false)]
    [InlineData("20261127T091", false)]
    [InlineData("2026112T15420", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyAWellFormedKeyOffThe0911MinuteIsAnInstantSlot(string? key, bool expected)
        => Assert.Equal(expected, AppCycleRules.IsInstantSlotKey(key));

    [Fact]
    public void AnInstantKeyIsTheLocalSecondAndNeverEqualsARegularKey()
    {
        var key = AppCycleRules.InstantSlotKey(new DateTime(2026, 11, 27, 9, 11, 0));
        Assert.Equal("20261127T091100", key);
        Assert.NotEqual(AppCycleCalendar.SlotKey(Friday), key);
        Assert.Equal(new DateTimeOffset(2026, 11, 27, 7, 42, 5, TimeSpan.Zero), AppCycleRules.NominalUtc("20261127T154205", Shanghai));
    }

    [Fact]
    public void TheNominalMomentIsTheKeysLocalMinuteInThePlanZone()
    {
        Assert.Equal(new DateTimeOffset(2026, 11, 27, 7, 42, 0, TimeSpan.Zero), AppCycleRules.NominalUtc("20261127T1542", Shanghai));
        // A regular key: its nominal moment is the moment its previous cycle ends (that cycle's deadline).
        Assert.Equal(AppCycleRules.DeadlineUtc("20261120T0911", Shanghai), AppCycleRules.NominalUtc("20261127T0911", Shanghai));
    }

    [Fact]
    public void ATargetAnchoredOnTheInstantDayRunsItsFirstCycleToTheSameWeekdayNextWeek()
        => Assert.Equal(AppCycleCalendar.SlotUtc(Friday.AddDays(7), Shanghai), AppCycleRules.InstantDeadlineUtc(Friday, Friday, Shanghai));

    [Fact]
    public void ANewPromptOnAnExistingTargetIsCoveredUpToThatTargetsNextRegularSlot()
    {
        // Anchored on a Tuesday: the next regular slot after Friday is the coming Tuesday.
        var tuesday = new DateOnly(2026, 11, 10);
        Assert.Equal(AppCycleCalendar.SlotUtc(new DateOnly(2026, 12, 1), Shanghai), AppCycleRules.InstantDeadlineUtc(Friday, tuesday, Shanghai));
        // Anchored on a Saturday: tomorrow's regular slot; the instant cycle never runs past it.
        var saturday = new DateOnly(2026, 11, 14);
        Assert.Equal(AppCycleCalendar.SlotUtc(Friday.AddDays(1), Shanghai), AppCycleRules.InstantDeadlineUtc(Friday, saturday, Shanghai));
    }

    [Fact]
    public void AnAnchorInTheFutureIsItsOwnFirstRegularSlotAndNoAnchorMeansSevenDays()
    {
        // Anchors written before this change (enabling day + 1) are in the future on the enabling day.
        Assert.Equal(AppCycleCalendar.SlotUtc(Friday.AddDays(1), Shanghai), AppCycleRules.InstantDeadlineUtc(Friday, Friday.AddDays(1), Shanghai));
        Assert.Equal(AppCycleCalendar.SlotUtc(Friday.AddDays(10), Shanghai), AppCycleRules.InstantDeadlineUtc(Friday, Friday.AddDays(10), Shanghai));
        Assert.Equal(AppCycleCalendar.SlotUtc(Friday.AddDays(7), Shanghai), AppCycleRules.InstantDeadlineUtc(Friday, null, Shanghai));
    }

    [Fact]
    public void ARegularCycleEndsExactlyAtTheNextRegularSlotSoItNeverCoversThatSlot()
    {
        // The coverage check is deadline > slot moment: equal means not covered, so steady weekly cycles are unchanged.
        var deadline = AppCycleRules.DeadlineUtc("20261120T0911", Shanghai);
        Assert.False(deadline > AppCycleRules.NominalUtc("20261127T0911", Shanghai));
        // An instant cycle started before 09:11 on its anchor day covers that day's regular slot.
        Assert.True(AppCycleRules.InstantDeadlineUtc(Friday, Friday, Shanghai) > AppCycleRules.NominalUtc("20261127T0911", Shanghai));
        // ...and ends exactly at next week's regular slot.
        Assert.False(AppCycleRules.InstantDeadlineUtc(Friday, Friday, Shanghai) > AppCycleRules.NominalUtc("20261204T0911", Shanghai));
    }
}
