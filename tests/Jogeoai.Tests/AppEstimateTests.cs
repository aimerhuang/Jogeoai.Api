using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;

namespace Jogeoai.Tests;

/// <summary>
/// implement 8.1 / design 6.5（无库）：App 平台容量的公式与边界、按排队顺序的份额、App 周期日历，以及预估的 App 部分
/// （渠道分项、App 单元格、排除的提问、预计首次结果时间）；没有 App 单元格时预估原样返回。
/// </summary>
public sealed class AppEstimateTests
{
    private static readonly JsonSerializerOptions Api = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly TimeZoneInfo Shanghai = CollectionScheduleCalendar.ParseTimeZone("Asia/Shanghai");
    private static readonly DateOnly Monday = new(2026, 11, 9);
    // 2026-11-09 08:00 Asia/Shanghai.
    private static readonly DateTimeOffset BeforeMain = new(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);
    private static readonly SiteTargetWrite ChatGpt = new("cloro", "chatgpt", "scraped", "US", "en-US");
    private static readonly string ChatGptKey = CollectionIdentity.TargetKey(new CloroVisibilityTarget("cloro", "chatgpt", "en-US", "US"));
    private static readonly string DoubaoKey = CollectionIdentity.AppTargetKey("doubao");

    // A week of one device: 604 800 seconds. taskSeconds × samples are the successful task durations of the platform.
    private const decimal OneDevice = AppCapacityRules.DeviceSecondsPerWeek;

    private static AppPlatformCapacityInput Input(string platform = "doubao", int bindings = 0, bool available = true, int platformInterval = 30,
        int? platformCap = null, decimal? deviceSeconds = null, double taskSeconds = 0, int samples = 0, long demand = 0)
        => new(platform, available, Enumerable.Range(0, bindings).Select(_ => new AppBindingRate(300)).ToArray(), platformInterval, platformCap,
            deviceSeconds ?? OneDevice * bindings, Enumerable.Repeat(taskSeconds, samples).ToArray(), demand);

    private static AppPoolBinding Binding(string node, string device, string platform, bool active = true, bool nodeActive = true)
        => new(node, device, platform, 300, null, active, nodeActive);

    private static AppPlatformCapacity Capacity(string platform, IEnumerable<AppPoolBinding> bindings, double taskSeconds, int samples = 20)
        => AppCapacityRules.Compute(AppCapacityRules.Inputs([platform], bindings, new Dictionary<string, (int, int?)>(),
            bindings.Select(b => b.Platform).ToHashSet(), new Dictionary<string, IReadOnlyList<double>> { [platform] = Enumerable.Repeat(taskSeconds, samples).ToArray() },
            new Dictionary<string, long>())[0]);

    // ---- design 6.5: the capacity formula and its edges ----

    [Fact]
    public void WithoutABindingThereIsNoCapacityAndTheWholeDemandIsExpectedUncollected()
    {
        var capacity = AppCapacityRules.Compute(Input(demand: 48));
        Assert.Equal(new AppPlatformCapacity("doubao", true, 0, 0, null, 0, 48, 48), capacity);
        // A measurement does not create capacity without a binding: the smaller of the two is taken.
        Assert.Equal(0, AppCapacityRules.Compute(Input(deviceSeconds: OneDevice, taskSeconds: 60, samples: 20, demand: 48)).WeeklyCapacity);
    }

    [Fact]
    public void DeviceTimeDeterminesCapacityRegardlessOfLegacyBindingAndPlatformLimits()
    {
        var one = AppCapacityRules.Compute(Input(bindings: 1, demand: 48));
        Assert.Equal((1, 6048L, (long?)null, 6048L, 48L, 0L),
            (one.ActiveBindings, one.RateLimitWeekly, one.MeasuredWeekly, one.WeeklyCapacity, one.WeeklyDemand, one.ExpectedUncollected));
        Assert.Equal(12096, AppCapacityRules.Compute(Input(bindings: 2, platformCap: 1)).WeeklyCapacity);
        Assert.Equal(6048, AppCapacityRules.Compute(Input(bindings: 11, deviceSeconds: OneDevice, platformInterval: 9999)).RateLimitWeekly);
    }

    [Fact]
    public void FewerThanTwentyTaskDurationsAreNotUsed()
    {
        foreach (var samples in new[] { 0, 1, 19 })
        {
            var capacity = AppCapacityRules.Compute(Input(bindings: 1, deviceSeconds: OneDevice, taskSeconds: 8640, samples: samples, demand: 100));
            Assert.Equal(((long?)null, 6048L, 0L, samples), (capacity.MeasuredWeekly, capacity.WeeklyCapacity, capacity.ExpectedUncollected,
                capacity.TaskDurationSamples));
        }
        // The median is reported even below 20 samples; from 20 samples on it is used (604 800 / 8640 = 70).
        Assert.Equal(8640d, AppCapacityRules.Compute(Input(bindings: 1, deviceSeconds: OneDevice, taskSeconds: 8640, samples: 19)).MedianTaskSeconds);
        Assert.Equal(70, AppCapacityRules.Compute(Input(bindings: 1, deviceSeconds: OneDevice, taskSeconds: 8640, samples: 20)).MeasuredWeekly);
        // Durations that are not positive are not samples.
        Assert.Null(AppCapacityRules.MeasuredWeekly(OneDevice, [.. Enumerable.Repeat(60d, 19), 0d, -5d]));
        // The median of an even count is the mean of the middle two; the order of the samples does not matter.
        Assert.Equal(25d, AppCapacityRules.Median([40, 10, 30, 20]));
        Assert.Equal(20d, AppCapacityRules.Median([30, 10, 20]));
        Assert.Null(AppCapacityRules.Median([]));
        // ⌊604 800 / 7⌋ = 86 400: rounded down.
        Assert.Equal(86400, AppCapacityRules.MeasuredWeekly(OneDevice, [.. Enumerable.Repeat(7d, 20)]));
        Assert.Equal(86399, AppCapacityRules.MeasuredWeekly(OneDevice, [.. Enumerable.Repeat(7.00001d, 20)]));
    }

    [Fact]
    public void DeviceTimeIsAWeekPerDeviceSplitEquallyOverItsPlatforms()
    {
        // One device on one platform: the whole week. 604 800 s / 60 s = 10 080 measured; the rate limit (2016) is smaller.
        var one = Capacity("doubao", [Binding("node-a", "AVD_1", "doubao")], 60);
        Assert.Equal((6048L, (long?)10080L, 6048L, 60d, 20), (one.RateLimitWeekly, one.MeasuredWeekly, one.WeeklyCapacity, one.MedianTaskSeconds!.Value,
            one.TaskDurationSamples));
        // One device shared by three platforms: a third of the week each (201 600 s); 201 600 / 60 = 3360 on doubao.
        AppPoolBinding[] shared = [Binding("node-a", "AVD_1", "doubao"), Binding("node-a", "AVD_1", "kimi"), Binding("node-a", "AVD_1", "qwen")];
        Assert.Equal(new Dictionary<string, decimal> { ["doubao"] = 201600m, ["kimi"] = 201600m, ["qwen"] = 201600m },
            AppCapacityRules.DeviceSecondsByPlatform(shared));
        Assert.Equal(3360, Capacity("doubao", shared, 60).MeasuredWeekly);
        // Two devices on one platform (the same device name on two nodes is two devices): two weeks.
        AppPoolBinding[] two = [Binding("node-a", "AVD_1", "doubao"), Binding("node-b", "AVD_1", "doubao")];
        Assert.Equal(1209600m, AppCapacityRules.DeviceSecondsByPlatform(two)["doubao"]);
        Assert.Equal(20160, Capacity("doubao", two, 60).MeasuredWeekly);
        // A device whose other binding is stopped gives this platform its whole week.
        Assert.Equal(604800m, AppCapacityRules.DeviceSecondsByPlatform([Binding("node-a", "AVD_1", "doubao"), Binding("node-a", "AVD_1", "kimi", active: false)])["doubao"]);
    }

    [Fact]
    public void BindingsOnADisabledNodeCountForNeitherTheRateLimitNorTheDeviceTime()
    {
        AppPoolBinding[] bindings = [Binding("node-a", "AVD_1", "doubao"), Binding("node-off", "AVD_2", "doubao", nodeActive: false),
            Binding("node-off", "AVD_2", "kimi", nodeActive: false)];
        var capacity = Capacity("doubao", bindings, 60);
        Assert.Equal((1, 6048L, (long?)10080L), (capacity.ActiveBindings, capacity.RateLimitWeekly, capacity.MeasuredWeekly));
        Assert.False(AppCapacityRules.DeviceSecondsByPlatform(bindings).ContainsKey("kimi"));
        var off = Capacity("kimi", bindings, 60);
        Assert.Equal((0, 0L, (long?)0L, 0L), (off.ActiveBindings, off.RateLimitWeekly, off.MeasuredWeekly, off.WeeklyCapacity));
        // A missing legacy gate has no platform interval or daily cap.
        var defaults = AppCapacityRules.Inputs(["doubao"], bindings, new Dictionary<string, (int, int?)>(), new HashSet<string>(),
            new Dictionary<string, IReadOnlyList<double>>(), new Dictionary<string, long>())[0];
        Assert.Equal((0, (int?)null, false), (defaults.PlatformIntervalSeconds, defaults.PlatformDailyCap, defaults.Available));
    }

    [Fact]
    public void AMeasurementBelowTheRateLimitIsTheCapacityAndOneAboveItIsNot()
    {
        // One device, 2.4 hours a task: 70 a week measured, 288 a day allowed — the devices are the bottleneck.
        var below = AppCapacityRules.Compute(Input(bindings: 1, deviceSeconds: OneDevice, taskSeconds: 8640, samples: 20, demand: 100));
        Assert.Equal((6048L, (long?)70L, 70L, 100L, 30L),
            (below.RateLimitWeekly, below.MeasuredWeekly, below.WeeklyCapacity, below.WeeklyDemand, below.ExpectedUncollected));
        var above = AppCapacityRules.Compute(Input(bindings: 1, deviceSeconds: OneDevice, taskSeconds: 60, samples: 20));
        Assert.Equal((10080L, 6048L), (above.MeasuredWeekly!.Value, above.WeeklyCapacity));
    }

    [Fact]
    public void AnUnavailablePlatformHasNoCapacity()
    {
        var capacity = AppCapacityRules.Compute(Input(bindings: 2, available: false, demand: 5));
        Assert.Equal((false, 0L, 0L, 5L), (capacity.Available, capacity.RateLimitWeekly, capacity.WeeklyCapacity, capacity.ExpectedUncollected));
    }

    [Fact]
    public void CapacityIsSharedInQueueOrderSoALargeBrandOnlyWaitsWithItsOwnTail()
    {
        Assert.Equal([3L, 5L], AppCapacityRules.AllocateByFairRank([3, 5], 100));
        Assert.Equal([0L, 0L], AppCapacityRules.AllocateByFairRank([3, 5], 0));
        // 14 a week for 10 + 10 + 2: the small brand complete, the large ones their first six prompts each.
        Assert.Equal([6L, 6L, 2L], AppCapacityRules.AllocateByFairRank([10, 10, 2], 14));
        // 14 a week for three brands of 10: ranks 1–4 of every brand (12); who gets the two checks of rank 5 depends on creation
        // times, so they are not counted for anyone.
        Assert.Equal([4L, 4L, 4L], AppCapacityRules.AllocateByFairRank([10, 10, 10], 14));
        Assert.Equal([0L, 6L], AppCapacityRules.AllocateByFairRank([-1, 6], 6));

        // One brand's share: the other brands' demand plus the brand's own (the draft in an estimate).
        var input = Input(bindings: 1, deviceSeconds: OneDevice, taskSeconds: 43200, samples: 20, demand: 999);
        var share = AppCapacityRules.ForBrand(input, [10, 10], 10);
        Assert.Equal((14L, 30L, 16L, 4L), (share.Capacity.WeeklyCapacity, share.Capacity.WeeklyDemand, share.Capacity.ExpectedUncollected, share.Scheduled));
        Assert.Equal(2, AppCapacityRules.ForBrand(input, [10, 10], 2).Scheduled);
        Assert.Equal(0, AppCapacityRules.ForBrand(input with { Available = false }, [], 2).Scheduled);
    }

    // ---- the App cycle calendar ----

    [Fact]
    public void TheCycleCalendarFollowsTheAnchorAndTheNineElevenSlot()
    {
        var anchor = new DateOnly(2026, 11, 10);
        Assert.Equal("20261110T0911", AppCycleCalendar.SlotKey(anchor));
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 1, 11, 0, TimeSpan.Zero), AppCycleCalendar.SlotUtc(anchor, Shanghai));
        // Before the anchor's slot: no current cycle, the first one is next.
        foreach (var now in new[] { BeforeMain, new DateTimeOffset(2026, 11, 10, 1, 10, 59, TimeSpan.Zero) })
            Assert.Equal(((DateOnly?)null, (DateOnly?)anchor), (AppCycleCalendar.CurrentCycleStart(anchor, now, Shanghai),
                AppCycleCalendar.NextCycleStart(anchor, now, Shanghai)));
        var at = new DateTimeOffset(2026, 11, 10, 1, 11, 0, TimeSpan.Zero);
        Assert.Equal(((DateOnly?)anchor, (DateOnly?)anchor.AddDays(7)), (AppCycleCalendar.CurrentCycleStart(anchor, at, Shanghai),
            AppCycleCalendar.NextCycleStart(anchor, at, Shanghai)));
        // A week later, a minute before the slot, the first cycle is still the current one; a few weeks later the latest.
        Assert.Equal(anchor, AppCycleCalendar.CurrentCycleStart(anchor, new DateTimeOffset(2026, 11, 17, 1, 10, 0, TimeSpan.Zero), Shanghai));
        Assert.Equal(anchor.AddDays(14), AppCycleCalendar.CurrentCycleStart(anchor, new DateTimeOffset(2026, 11, 25, 2, 0, 0, TimeSpan.Zero), Shanghai));
        Assert.Null(AppCycleCalendar.CurrentCycleStart(null, at, Shanghai));
        Assert.Null(AppCycleCalendar.NextCycleStart(null, at, Shanghai));
        // The deadline of a cycle run is the next cycle's slot.
        Assert.Equal(AppCycleRules.DeadlineUtc(AppCycleCalendar.SlotKey(anchor), Shanghai), AppCycleCalendar.SlotUtc(anchor.AddDays(7), Shanghai));
    }

    // ---- the App part of the target estimate ----

    private static IReadOnlyList<PromptMonitoringScale.EstimateCell> Cells(params SiteTargetWrite[] drafts)
        => PromptMonitoringScale.ProjectCells(ManagementStore.ValidateSiteTargetsWrite(new SiteTargetsWrite(0, drafts), false), [], Monday);

    private static AppMonitoringEstimate.BrandPrompt Prompt(IReadOnlyList<string>? scope, string? reason = null)
        => new(new MonitoringPromptScope(Guid.NewGuid(), scope), new AppPromptEligibility(reason is null, reason));

    // all · all TOO_LONG · ChatGPT only · doubao only · all excluded by an operator · ChatGPT only TOO_LONG (not on an App cell).
    private static readonly AppMonitoringEstimate.BrandPrompt[] Mixed =
    [
        Prompt(null), Prompt(null, AppPromptRules.ReasonTooLong), Prompt([ChatGptKey]), Prompt([DoubaoKey]),
        Prompt(null, AppPromptRules.ReasonExcludedByOperator), Prompt([ChatGptKey], AppPromptRules.ReasonTooLong),
    ];

    private static MonitoringTargetsEstimateView ApiEstimate(IReadOnlyList<AppMonitoringEstimate.BrandPrompt> prompts, params SiteTargetWrite[] drafts)
        => PromptMonitoringScale.EstimateTargets(new PromptMonitoringScale.Snapshot(prompts.Select(p => p.Scope).ToArray(), prompts.Count, [], 1),
            Cells(drafts), new HashSet<string>(), BeforeMain, Shanghai, 55m);

    // doubao: one binding (2016 a week) and another brand with 5; kimi: no binding.
    private static AppBrandCapacityShare Share(string engine, long demand) => engine == "doubao"
        ? AppCapacityRules.ForBrand(Input("doubao", bindings: 1), [5], demand)
        : AppCapacityRules.ForBrand(Input(engine), [], demand);

    private static readonly AppMonitoringEstimate.AppCell[] Tomorrow = [new("kimi", Monday.AddDays(1)), new("doubao", Monday.AddDays(1))];

    [Fact]
    public void WithoutAnAppCellTheApiEstimateIsReturnedAsItIs()
    {
        var api = ApiEstimate(Mixed, ChatGpt);
        var same = AppMonitoringEstimate.Combine(api, new(Mixed, false), [], Share, BeforeMain, Shanghai);
        Assert.Same(api, same);
        var json = JsonSerializer.SerializeToNode(same, Api)!.AsObject();
        Assert.Equal(["promptCount", "weeklyChecks", "weeklyCredits", "nextSlotChecks", "nextSlotCredits", "cells", "balance", "shortfall",
            "blockers", "unitPrice", "sampleCount", "nextSlotAt", "nextSlotKind"], json.Select(member => member.Key));
        Assert.Equal(["targetKey", "engine", "answerMode", "frequency", "weeklyRuns", "promptCount"],
            json["cells"]!.AsArray()[0]!.AsObject().Select(member => member.Key));
        // The team row of such a brand is the eight members it always had.
        var row = new TeamMonitoringBrandEstimate(Guid.NewGuid(), "品牌", 1, 7m, 70m, 1, 10m, []);
        Assert.Equal(["siteId", "displayName", "promptCount", "weeklyChecks", "weeklyCredits", "nextSlotChecks", "nextSlotCredits", "blockers"],
            JsonSerializer.SerializeToNode(row, Api)!.AsObject().Select(member => member.Key));
    }

    [Fact]
    public void AppCellsAddTheAppPartAndLeaveEveryApiFigureAsItWas()
    {
        var api = ApiEstimate(Mixed, ChatGpt);
        var estimate = AppMonitoringEstimate.Combine(api, new(Mixed, false), Tomorrow, Share, BeforeMain, Shanghai);

        // API: 5 prompts on ChatGPT (every prompt but the doubao-only one) × 7 = 35 a week. Next slot, shortfall and blockers
        // stay the API figures; channels.api is the API estimate's own weekly figures.
        Assert.Equal((35m, 350m), (api.WeeklyChecks, api.WeeklyCredits));
        Assert.Equal((api.NextSlotChecks, api.NextSlotCredits, api.Shortfall, api.Blockers, api.NextSlotAt, api.NextSlotKind, api.PromptCount),
            (estimate.NextSlotChecks, estimate.NextSlotCredits, estimate.Shortfall, estimate.Blockers, estimate.NextSlotAt, estimate.NextSlotKind, estimate.PromptCount));
        Assert.Equal(new ApiChannelEstimate(1, 35m, 350m), estimate.Channels!.Api);

        // App: doubao has the 'all' prompt and the doubao-only prompt (2), kimi the 'all' prompt (1). Only doubao has capacity,
        // so 2 checks a week can be expected; kimi's prompt is expected uncollected.
        Assert.Equal([CollectionChannel.Api, CollectionChannel.App, CollectionChannel.App], estimate.Cells.Select(c => c.Channel));
        Assert.Equal(api.Cells[0] with { Channel = CollectionChannel.Api }, estimate.Cells[0]);
        Assert.Equal(new MonitoringTargetCellEstimate(DoubaoKey, "doubao", AnswerMode.Fast, MonitoringFrequency.Weekly, 1m, 2, CollectionChannel.App),
            estimate.Cells[1]);
        Assert.Equal(("kimi", 1), (estimate.Cells[2].Engine, estimate.Cells[2].PromptCount));
        var app = estimate.Channels.App;
        Assert.Equal((2, 2m, 20m), (app.Targets, app.WeeklyChecks, app.WeeklyCredits));
        Assert.Equal([new AppPlatformEstimate("doubao", 6048, 7, 0), new AppPlatformEstimate("kimi", 0, 1, 1)], app.Platforms);
        // Prompts on an App cell that do not enter the App channel; the TOO_LONG prompt scoped to ChatGPT only is not counted.
        Assert.Equal([new AppReasonCount(AppPromptRules.ReasonExcludedByOperator, 1), new AppReasonCount(AppPromptRules.ReasonTooLong, 1)],
            app.ExcludedPrompts);
        // Newly enabled targets start tomorrow: 2026-11-10 09:11 Asia/Shanghai.
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 1, 11, 0, TimeSpan.Zero), app.FirstResultAt);
        // Top level: API plus the executable App part.
        Assert.Equal((37m, 370m), (estimate.WeeklyChecks, estimate.WeeklyCredits));
    }

    [Fact]
    public void OnlyChecksThatFitTheCapacityAreCountedAndNothingScheduledMeansNoFirstResult()
    {
        AppMonitoringEstimate.BrandPrompt[] prompts = [.. Enumerable.Range(0, 10).Select(_ => Prompt(null))];
        var api = ApiEstimate(prompts);
        // 14 a week shared with two brands of 10: the brand gets its first four prompts.
        AppBrandCapacityShare Tight(string engine, long demand)
            => AppCapacityRules.ForBrand(Input(engine, bindings: 1, deviceSeconds: OneDevice, taskSeconds: 43200, samples: 20), [10, 10], demand);
        var estimate = AppMonitoringEstimate.Combine(api, new(prompts, false), [new("doubao", Monday.AddDays(1))], Tight, BeforeMain, Shanghai);
        Assert.Equal((4m, 40m), (estimate.Channels!.App.WeeklyChecks, estimate.Channels.App.WeeklyCredits));
        Assert.Equal(new AppPlatformEstimate("doubao", 14, 30, 16), Assert.Single(estimate.Channels.App.Platforms));
        Assert.Equal(10, Assert.Single(estimate.Cells).PromptCount);
        // An App-only draft: no API cell, the blocker logic is the API estimate's (tested elsewhere), the totals are the App part.
        Assert.Equal((4m, 40m, 0), (estimate.WeeklyChecks, estimate.WeeklyCredits, estimate.Channels.Api.Targets));

        // An existing anchor: a target anchored on a Monday two weeks ago has its next cycle today (08:00 now, 09:11 slot),
        // and an hour after the slot the one a week later.
        var kept = AppMonitoringEstimate.Combine(api, new(prompts, false), [new("doubao", Monday.AddDays(-14))], Share, BeforeMain, Shanghai);
        Assert.Equal(AppCycleCalendar.SlotUtc(Monday, Shanghai), kept.Channels!.App.FirstResultAt);
        var later = AppMonitoringEstimate.Combine(api, new(prompts, false), [new("doubao", Monday.AddDays(-14))], Share, BeforeMain.AddHours(2), Shanghai);
        Assert.Equal(AppCycleCalendar.SlotUtc(Monday.AddDays(7), Shanghai), later.Channels!.App.FirstResultAt);
        // Nothing expected to be collected (no binding): no first result; a brand over 200 prompts has its App slots skipped.
        var none = AppMonitoringEstimate.Combine(api, new(prompts, false), [new("kimi", Monday.AddDays(1))], Share, BeforeMain, Shanghai);
        Assert.Equal((0m, (DateTimeOffset?)null), (none.Channels!.App.WeeklyChecks, none.Channels.App.FirstResultAt));
        var tooLarge = AppMonitoringEstimate.Combine(api, new(prompts, true), [new("doubao", Monday.AddDays(1))], Share, BeforeMain, Shanghai);
        Assert.Equal((0m, 5L), (tooLarge.Channels!.App.WeeklyChecks, tooLarge.Channels.App.Platforms[0].WeeklyDemand));
    }

    [Fact]
    public void TheResponseUsesTheContractFieldNames()
    {
        var estimate = AppMonitoringEstimate.Combine(ApiEstimate(Mixed, ChatGpt), new(Mixed, false), Tomorrow, Share, BeforeMain, Shanghai);
        var json = JsonSerializer.SerializeToNode(estimate, Api)!.AsObject();
        Assert.Equal("channels", json.Last().Key);
        Assert.Equal(14, json.Count);
        var channels = json["channels"]!.AsObject();
        Assert.Equal(["targets", "weeklyChecks", "weeklyCredits"], channels["api"]!.AsObject().Select(member => member.Key));
        var app = channels["app"]!.AsObject();
        Assert.Equal(["targets", "weeklyChecks", "weeklyCredits", "excludedPrompts", "platforms", "firstResultAt"], app.Select(member => member.Key));
        Assert.Equal(["reasonCode", "count"], app["excludedPrompts"]!.AsArray()[0]!.AsObject().Select(member => member.Key));
        Assert.Equal(["engine", "weeklyCapacity", "weeklyDemand", "expectedUncollected"], app["platforms"]!.AsArray()[0]!.AsObject().Select(member => member.Key));
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 1, 11, 0, TimeSpan.Zero), app["firstResultAt"]!.GetValue<DateTimeOffset>());
        Assert.All(json["cells"]!.AsArray(), cell => Assert.Contains(cell!["channel"]!.GetValue<string>(), new[] { "api", "app" }));
        // The team row carries the same breakdown as the brand estimate.
        var row = new TeamMonitoringBrandEstimate(Guid.NewGuid(), "品牌", 1, estimate.WeeklyChecks, estimate.WeeklyCredits, 1, 10m, [], estimate.Channels);
        var rowJson = JsonSerializer.SerializeToNode(row, Api)!.AsObject();
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(estimate.Channels, Api), rowJson["channels"]));
    }

    [Fact]
    public void TheNewMembersAreDocumentedInChinese()
    {
        var xml = System.Xml.Linq.XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Jogeoai.Domain.xml"));
        string Param(string type, string name) => xml.Descendants("member").Single(m => (string?)m.Attribute("name") == $"T:Jogeoai.Domain.{type}")
            .Elements("param").Single(p => (string?)p.Attribute("name") == name).Value;
        foreach (var (type, name) in new[] { ("MonitoringTargetsEstimateView", "Channels"), ("MonitoringTargetCellEstimate", "Channel"),
                     ("TeamMonitoringBrandEstimate", "Channels"), ("AppChannelEstimate", "FirstResultAt"), ("AppPlatformEstimate", "ExpectedUncollected"),
                     ("AppCollectionCycleStatus", "NotCollectedByReason"), ("AppTargetCollectionStatus", "ExpectedFirstResultAt"),
                     ("AppPlatformCapacity", "WeeklyCapacity") })
            Assert.Contains(Param(type, name), c => c is >= '一' and <= '鿿');
    }
}
