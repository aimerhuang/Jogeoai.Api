using Jogeoai.Domain;

namespace Jogeoai.Tests;

/// <summary>采集频率：每周次数与时隙到期判定（主时隙 09:01、凌晨时隙 00:05）。</summary>
public sealed class MonitoringFrequencyTests
{
    private static DateOnly D(string value) => DateOnly.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    private static bool Main(MonitoringFrequency f, string? anchor, string slot)
        => MonitoringFrequencyRules.IsDue(f, anchor is null ? null : D(anchor), D(slot), MonitoringSlotKind.Main);

    [Fact]
    public void WeeklyRunsMatchTheEstimateTable()
    {
        Assert.Equal(14m, MonitoringFrequencyRules.WeeklyRuns(MonitoringFrequency.TwiceDaily));
        Assert.Equal(7m, MonitoringFrequencyRules.WeeklyRuns(MonitoringFrequency.Daily));
        Assert.Equal(3.5m, MonitoringFrequencyRules.WeeklyRuns(MonitoringFrequency.EveryOtherDay));
        Assert.Equal(1m, MonitoringFrequencyRules.WeeklyRuns(MonitoringFrequency.Weekly));
    }

    [Theory]
    [InlineData(MonitoringFrequency.TwiceDaily, "twice_daily")]
    [InlineData(MonitoringFrequency.Daily, "daily")]
    [InlineData(MonitoringFrequency.EveryOtherDay, "every_other_day")]
    [InlineData(MonitoringFrequency.Weekly, "weekly")]
    public void StorageStringsRoundTrip(MonitoringFrequency frequency, string value)
    {
        Assert.Equal(value, MonitoringFrequencyRules.ToStorage(frequency));
        Assert.True(MonitoringFrequencyRules.TryParse($" {value.ToUpperInvariant()} ", out var parsed));
        Assert.Equal(frequency, parsed);
        var json = new System.Text.Json.JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
        Assert.Equal($"\"{value}\"", System.Text.Json.JsonSerializer.Serialize(frequency, json));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("hourly")]
    public void UnknownFrequenciesDoNotParse(string? value) => Assert.False(MonitoringFrequencyRules.TryParse(value, out _));

    [Fact]
    public void NightSlotsOnlyIncludeTwiceDaily()
    {
        foreach (var frequency in MonitoringFrequencyRules.All)
            Assert.Equal(frequency == MonitoringFrequency.TwiceDaily,
                MonitoringFrequencyRules.IsDue(frequency, D("2026-11-09"), D("2026-11-09"), MonitoringSlotKind.Night));
        Assert.True(MonitoringFrequencyRules.IsDue(MonitoringFrequency.TwiceDaily, null, D("2026-11-01"), MonitoringSlotKind.Night));
    }

    [Fact]
    public void DailyAndTwiceDailyAreDueEveryMainSlotRegardlessOfAnchor()
    {
        foreach (var frequency in new[] { MonitoringFrequency.Daily, MonitoringFrequency.TwiceDaily })
        {
            Assert.True(Main(frequency, null, "2026-11-09"));
            Assert.True(Main(frequency, "2026-11-09", "2026-11-10"));
            Assert.True(Main(frequency, "2026-11-09", "2026-11-08")); // anchor is irrelevant
        }
    }

    [Theory]
    [InlineData("2026-11-09", "2026-11-09", true)]  // anchor day
    [InlineData("2026-11-09", "2026-11-10", false)]
    [InlineData("2026-11-09", "2026-11-11", true)]
    [InlineData("2026-11-29", "2026-12-01", true)]  // cross-month (30-day November)
    [InlineData("2026-11-30", "2026-12-01", false)]
    [InlineData("2026-12-31", "2027-01-02", true)]  // cross-year
    [InlineData("2026-12-31", "2027-01-01", false)]
    [InlineData("2028-02-28", "2028-03-01", true)]  // leap day counts as a day
    [InlineData("2026-11-09", "2026-11-07", false)] // before anchor, even on an even distance
    [InlineData("2026-11-09", "2026-11-08", false)]
    public void EveryOtherDayAlternatesFromTheAnchor(string anchor, string slot, bool due)
        => Assert.Equal(due, Main(MonitoringFrequency.EveryOtherDay, anchor, slot));

    [Theory]
    [InlineData("2026-11-09", "2026-11-09", true)]  // anchor day (Monday)
    [InlineData("2026-11-09", "2026-11-16", true)]
    [InlineData("2026-11-09", "2026-11-10", false)]
    [InlineData("2026-11-09", "2026-11-15", false)]
    [InlineData("2026-11-26", "2026-12-03", true)]  // cross-month Thursday
    [InlineData("2026-12-28", "2027-01-04", true)]  // cross-year Monday
    [InlineData("2026-12-28", "2027-01-03", false)]
    [InlineData("2026-11-09", "2026-11-02", false)] // same weekday before the anchor
    public void WeeklyAlignsToTheAnchorWeekday(string anchor, string slot, bool due)
        => Assert.Equal(due, Main(MonitoringFrequency.Weekly, anchor, slot));

    [Fact]
    public void NullAnchorBehavesLikeDaily()
    {
        foreach (var slot in new[] { "2026-11-09", "2026-11-10", "2026-11-11" })
        {
            Assert.True(Main(MonitoringFrequency.EveryOtherDay, null, slot));
            Assert.True(Main(MonitoringFrequency.Weekly, null, slot));
        }
    }

    [Fact]
    public void EveryOtherDayHitsHalfAndWeeklyOneSeventhOfAFortnight()
    {
        var anchor = D("2026-11-09");
        var days = Enumerable.Range(0, 14).Select(anchor.AddDays).ToArray();
        Assert.Equal(7, days.Count(d => MonitoringFrequencyRules.IsDue(MonitoringFrequency.EveryOtherDay, anchor, d, MonitoringSlotKind.Main)));
        Assert.Equal(2, days.Count(d => MonitoringFrequencyRules.IsDue(MonitoringFrequency.Weekly, anchor, d, MonitoringSlotKind.Main)));
    }

    [Fact]
    public void ScheduleConstantsAreFixed()
    {
        Assert.Equal("jogeoai:auto-prompt-monitoring:v1", PromptMonitoringIdentity.ScheduleName);
        Assert.Equal("jogeoai:auto-prompt-monitoring:night:v1", PromptMonitoringIdentity.NightScheduleName);
        Assert.Equal("09:01", PromptMonitoringIdentity.MainLocalTime);
        Assert.Equal("00:05", PromptMonitoringIdentity.NightLocalTime);
    }

    // ---- App slot (09:11, weekly only; 11-16-app-channel-monitoring-api) ----

    private static bool App(MonitoringFrequency f, string? anchor, string slot)
        => MonitoringFrequencyRules.IsDue(f, anchor is null ? null : D(anchor), D(slot), MonitoringSlotKind.App);

    [Theory]
    [InlineData("2026-11-18", "2026-11-18", true)]  // anchor day (Wednesday)
    [InlineData("2026-11-18", "2026-11-17", false)] // the day before
    [InlineData("2026-11-18", "2026-11-25", true)]  // 7 days later
    [InlineData("2026-11-18", "2026-12-02", true)]  // 14 days later, cross-month
    [InlineData("2026-11-18", "2026-11-19", false)] // a different weekday
    [InlineData("2026-11-18", "2026-11-21", false)]
    [InlineData("2026-11-18", "2026-11-24", false)] // 6 days later
    [InlineData("2026-11-18", "2026-11-26", false)] // 8 days later
    [InlineData("2026-11-18", "2026-11-11", false)] // same weekday before the anchor
    [InlineData("2026-12-30", "2027-01-06", true)]  // cross-year Wednesday
    [InlineData("2026-12-30", "2027-01-05", false)]
    public void AppSlotIsDueWeeklyOnTheAnchorWeekdayFromTheAnchor(string anchor, string slot, bool due)
        => Assert.Equal(due, App(MonitoringFrequency.Weekly, anchor, slot));

    [Fact]
    public void AppSlotWithoutAnAnchorIsNeverDue()
    {
        foreach (var frequency in MonitoringFrequencyRules.All)
            foreach (var slot in new[] { "2026-11-17", "2026-11-18", "2026-11-25" })
                Assert.False(App(frequency, null, slot));
        // Unlike the main slot, where a null anchor behaves like daily.
        Assert.True(Main(MonitoringFrequency.Weekly, null, "2026-11-18"));
    }

    [Fact]
    public void AppSlotNeverIncludesNonWeeklyFrequencies()
    {
        foreach (var frequency in MonitoringFrequencyRules.All.Where(f => f != MonitoringFrequency.Weekly))
            foreach (var slot in new[] { "2026-11-18", "2026-11-19", "2026-11-20", "2026-11-25", "2026-12-02" })
                Assert.False(App(frequency, "2026-11-18", slot));
    }

    [Fact]
    public void AppSlotHitsOncePerWeek()
    {
        var anchor = D("2026-11-18");
        var days = Enumerable.Range(-7, 28).Select(anchor.AddDays).ToArray();
        Assert.Equal([anchor, anchor.AddDays(7), anchor.AddDays(14)],
            days.Where(d => MonitoringFrequencyRules.IsDue(MonitoringFrequency.Weekly, anchor, d, MonitoringSlotKind.App)));
    }

    [Fact]
    public void AddingTheAppSlotLeavesMainAndNightUntouched()
    {
        // Independent restatement of the documented main / night rules over a fortnight around the anchor.
        var anchor = D("2026-11-18");
        foreach (var day in Enumerable.Range(-7, 21).Select(anchor.AddDays))
            foreach (var frequency in MonitoringFrequencyRules.All)
            {
                var main = frequency switch
                {
                    MonitoringFrequency.Daily or MonitoringFrequency.TwiceDaily => true,
                    MonitoringFrequency.EveryOtherDay => day >= anchor && (day.DayNumber - anchor.DayNumber) % 2 == 0,
                    _ => day >= anchor && day.DayOfWeek == anchor.DayOfWeek,
                };
                Assert.Equal(main, MonitoringFrequencyRules.IsDue(frequency, anchor, day, MonitoringSlotKind.Main));
                Assert.True(MonitoringFrequencyRules.IsDue(frequency, null, day, MonitoringSlotKind.Main));
                Assert.Equal(frequency == MonitoringFrequency.TwiceDaily,
                    MonitoringFrequencyRules.IsDue(frequency, anchor, day, MonitoringSlotKind.Night));
            }
        // Undefined values are still rejected for every kind, including the new one.
        Assert.Throws<ArgumentOutOfRangeException>(() => MonitoringFrequencyRules.IsDue(MonitoringFrequency.Weekly, anchor, anchor, (MonitoringSlotKind)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => MonitoringFrequencyRules.IsDue((MonitoringFrequency)99, anchor, anchor, MonitoringSlotKind.App));
    }

    [Fact]
    public void AppScheduleConstantsAreFixedAndStayOutOfTheMainAndNightHelpers()
    {
        Assert.Equal("jogeoai:auto-prompt-monitoring:app:v1", PromptMonitoringIdentity.AppScheduleName);
        Assert.Equal("09:11", PromptMonitoringIdentity.AppLocalTime);
        Assert.True(PromptMonitoringIdentity.IsAppSchedule(PromptMonitoringIdentity.AppScheduleName));
        foreach (var name in new[] { PromptMonitoringIdentity.ScheduleName, PromptMonitoringIdentity.NightScheduleName, null, "",
                     "JOGEOAI:AUTO-PROMPT-MONITORING:APP:V1", " jogeoai:auto-prompt-monitoring:app:v1", "weekly report" })
            Assert.False(PromptMonitoringIdentity.IsAppSchedule(name));
        // The App plan has its own slot path: the existing helpers must keep treating its name like any non-automatic plan.
        Assert.Null(PromptMonitoringIdentity.SlotKind(PromptMonitoringIdentity.AppScheduleName));
        Assert.False(PromptMonitoringIdentity.IsAutomaticSchedule(PromptMonitoringIdentity.AppScheduleName));
        Assert.Equal(MonitoringSlotKind.Main, PromptMonitoringIdentity.SlotKind(PromptMonitoringIdentity.ScheduleName));
        Assert.Equal(MonitoringSlotKind.Night, PromptMonitoringIdentity.SlotKind(PromptMonitoringIdentity.NightScheduleName));
        Assert.True(PromptMonitoringIdentity.IsAutomaticSchedule(PromptMonitoringIdentity.ScheduleName));
        Assert.True(PromptMonitoringIdentity.IsAutomaticSchedule(PromptMonitoringIdentity.NightScheduleName));
        // Existing members keep their numeric values and wire names; the new member is appended.
        Assert.Equal((0, 1, 2), ((int)MonitoringSlotKind.Main, (int)MonitoringSlotKind.Night, (int)MonitoringSlotKind.App));
        var json = new System.Text.Json.JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
        Assert.Equal("[\"main\",\"night\",\"app\"]", System.Text.Json.JsonSerializer.Serialize(Enum.GetValues<MonitoringSlotKind>(), json));
    }
}
