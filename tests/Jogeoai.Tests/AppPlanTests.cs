using System.Text.Json;
using System.Text.Json.Serialization;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;

namespace Jogeoai.Tests;

/// <summary>
/// App 计划（implement 4.1 / 4.1a，总 PRD KD-24）的纯函数部分：快照的选择（含提问的自定义范围）、启停跟随主计划的判定、
/// 时隙的判定，以及预估里“至少一个单元格”对只有 App 单元格的草稿的处理。数据库端到端见 AppPlanSqlTests。
/// </summary>
public sealed class AppPlanTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions Api = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly TimeZoneInfo Shanghai = CollectionScheduleCalendar.ParseTimeZone("Asia/Shanghai");
    private static readonly DateOnly Monday = new(2026, 11, 9);
    private static readonly DateTimeOffset BeforeMain = new(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);
    private static readonly SiteTargetWrite ChatGpt = new("cloro", "chatgpt", "scraped", "US", "en-US");
    private static readonly string ChatGptKey = CollectionIdentity.TargetKey("cloro", "chatgpt", "scraped", "US", "en-US");
    private static readonly string Doubao = CollectionIdentity.AppTargetKey("doubao");
    private static readonly string Kimi = CollectionIdentity.AppTargetKey("kimi");
    private static readonly string Qwen = CollectionIdentity.AppTargetKey("qwen");

    private static AutomaticMonitoringSelection.Target AppTarget(string engine, DateOnly? anchor = null,
        MonitoringFrequency frequency = MonitoringFrequency.Weekly)
        => new(Guid.NewGuid(), AutomaticMonitoringSelection.StoredTarget("app_collector", engine, "zh-CN", "CN", null),
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), frequency, anchor);

    private static SiteTargetWrite AppDraft(string engine, bool enabled = true)
        => new("app_collector", engine, "app", "CN", "zh-CN", enabled, null, MonitoringFrequency.Weekly);

    private static MonitoringPromptScope[] Prompts(int count)
        => Enumerable.Range(0, count).Select(_ => new MonitoringPromptScope(Guid.NewGuid())).ToArray();

    [Fact]
    public void SnapshotHoldsAppCellsOnlyAndIntersectsEachPromptScopeWithTheEnabledAppTargets()
    {
        var all = new MonitoringPromptScope(Guid.NewGuid());
        var mixed = new MonitoringPromptScope(Guid.NewGuid(), [ChatGptKey, Kimi]);
        var apiOnly = new MonitoringPromptScope(Guid.NewGuid(), [ChatGptKey]);
        var disabledApp = new MonitoringPromptScope(Guid.NewGuid(), [Qwen]);
        var targets = new[] { AppTarget("kimi"), AppTarget("doubao") };

        var snapshot = AppMonitoringPlan.Snapshot([all, mixed, apiOnly, disabledApp], targets);
        // The 'all' prompt and the prompt whose scope names an enabled App target; never the API-only or the disabled-App scope.
        Assert.Equal(new[] { all.PromptId, mixed.PromptId }.Order(), snapshot.PromptIds);
        Assert.Equal(new[] { Doubao, Kimi }.Order(StringComparer.Ordinal), snapshot.Targets.Select(CollectionIdentity.TargetKey));
        Assert.All(snapshot.Targets, t => Assert.Equal(("app_collector", "CN", "zh-CN", (string?)null), (t.Provider, t.Country, t.Locale, t.AnswerMode)));
        Assert.Equal(1, snapshot.SampleCount);
        // Only the custom prompt is listed, with the App part of its scope: the API key in the scope matches nothing here.
        var scoped = Assert.Single(snapshot.PromptTargets!);
        Assert.Equal((mixed.PromptId, Kimi), (scoped.PromptId, Assert.Single(scoped.TargetKeys)));
        // all → 2 targets, mixed → kimi: three pairs.
        Assert.Equal(3, MonitoringMatrix.CountChecks(snapshot));
        Assert.DoesNotContain(ChatGptKey, JsonSerializer.Serialize(snapshot, Web), StringComparison.Ordinal);

        // The order the targets are read in does not matter: same snapshot bytes, same hash.
        var reversed = AppMonitoringPlan.Snapshot([disabledApp, apiOnly, mixed, all], targets.Reverse().ToArray());
        Assert.Equal(JsonSerializer.Serialize(snapshot, Web), JsonSerializer.Serialize(reversed, Web));
        Assert.Equal(CollectionIdentity.SelectionHash(snapshot), CollectionIdentity.SelectionHash(reversed));

        // Only 'all' prompts: the plain product, no promptTargets member at all.
        var plain = AppMonitoringPlan.Snapshot(Prompts(3), targets);
        Assert.Equal((3, 2), (plain.PromptIds.Count, plain.Targets.Count));
        Assert.Null(plain.PromptTargets);
        Assert.DoesNotContain("promptTargets", JsonSerializer.Serialize(plain, Web), StringComparison.Ordinal);

        // No enabled App target: no target, whatever the scopes are. No prompt with an App pair: no prompt.
        Assert.Empty(AppMonitoringPlan.Snapshot([all, mixed], []).Targets);
        Assert.Empty(AppMonitoringPlan.Snapshot([mixed], []).PromptIds);
        Assert.Empty(AppMonitoringPlan.Snapshot([apiOnly, disabledApp], targets).PromptIds);
        Assert.Equal(0, MonitoringMatrix.CountChecks(AppMonitoringPlan.Snapshot([], targets)));
    }

    [Theory]
    // With a main plan the App plan follows it; without one its own flag is the switch and is left alone.
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    [InlineData(null, true, true)]
    [InlineData(null, false, false)]
    public void PauseFollowsTheMainPlanAndIsTheAppPlansOwnSwitchWithoutOne(bool? mainEnabled, bool appEnabled, bool expected)
        => Assert.Equal(expected, AppMonitoringPlan.MirroredEnabled(mainEnabled, appEnabled));

    [Fact]
    public void SlotDecisionSkipsClosedPausedNotDueAndEmptySlotsAndLeavesADueSlotPending()
    {
        var prompts = Prompts(2);
        var due = AppTarget("doubao", Monday);                       // weekly, anchored on a Monday
        var otherDay = AppTarget("kimi", Monday.AddDays(1));         // anchored on a Tuesday
        var later = AppTarget("qwen", Monday.AddDays(7));            // first cycle starts next Monday
        var targets = new[] { due, otherDay, later };

        // Open tenant, something due: pending, and only the due target is frozen.
        var plan = AppMonitoringPlan.PlanSlot(true, null, prompts, targets, Monday);
        Assert.Null(plan.SkipCode);
        Assert.Equal([Doubao], plan.Selected.Targets.Select(CollectionIdentity.TargetKey));
        Assert.Equal(prompts.Select(p => p.PromptId).Order(), plan.Selected.PromptIds);
        // Same with an enabled main plan; a week later the third target has joined.
        Assert.Null(AppMonitoringPlan.PlanSlot(true, true, prompts, targets, Monday).SkipCode);
        Assert.Equal(new[] { Doubao, Qwen }.Order(StringComparer.Ordinal),
            AppMonitoringPlan.PlanSlot(true, null, prompts, targets, Monday.AddDays(7)).Selected.Targets.Select(CollectionIdentity.TargetKey));

        // Closed tenant: always APP_CHANNEL_NOT_OPEN, before anything else.
        Assert.Equal("APP_CHANNEL_NOT_OPEN", AppMonitoringPlan.PlanSlot(false, null, prompts, targets, Monday).SkipCode);
        Assert.Equal("APP_CHANNEL_NOT_OPEN", AppMonitoringPlan.PlanSlot(false, false, [], [], Monday).SkipCode);
        // Main plan paused (the App plan has not been reconciled yet): skipped as disabled.
        Assert.Equal("SCHEDULE_DISABLED", AppMonitoringPlan.PlanSlot(true, false, prompts, targets, Monday).SkipCode);

        // Nothing due: another weekday, before the anchor, no target at all, no anchor, or a frequency other than weekly.
        Assert.Equal("MONITORING_NOT_DUE", AppMonitoringPlan.PlanSlot(true, null, prompts, targets, Monday.AddDays(2)).SkipCode);
        Assert.Equal("MONITORING_NOT_DUE", AppMonitoringPlan.PlanSlot(true, null, prompts, [later], Monday).SkipCode);
        Assert.Equal("MONITORING_NOT_DUE", AppMonitoringPlan.PlanSlot(true, true, prompts, [], Monday).SkipCode);
        Assert.Equal("MONITORING_NOT_DUE", AppMonitoringPlan.PlanSlot(true, null, prompts, [AppTarget("doubao")], Monday).SkipCode);
        Assert.Equal("MONITORING_NOT_DUE",
            AppMonitoringPlan.PlanSlot(true, null, prompts, [AppTarget("doubao", Monday, MonitoringFrequency.Daily)], Monday).SkipCode);
        Assert.Empty(AppMonitoringPlan.PlanSlot(true, null, prompts, targets, Monday.AddDays(2)).Selected.Targets);

        // A due target without a prompt to collect on it: no prompt at all, or every prompt scoped elsewhere (never widened).
        Assert.Equal("MONITORING_SELECTION_EMPTY", AppMonitoringPlan.PlanSlot(true, null, [], targets, Monday).SkipCode);
        MonitoringPromptScope[] elsewhere = [new(Guid.NewGuid(), [Kimi]), new(Guid.NewGuid(), [ChatGptKey])];
        Assert.Equal("MONITORING_SELECTION_EMPTY", AppMonitoringPlan.PlanSlot(true, null, elsewhere, targets, Monday).SkipCode);
        // More than 200 prompts (the list is read truncated): refused, not cut down.
        Assert.Equal("MONITORING_TOO_LARGE", AppMonitoringPlan.PlanSlot(true, null, Prompts(201), targets, Monday).SkipCode);

        // The API round limits do not apply: 200 prompts × 7 App targets = 1400 checks is a valid App slot.
        var seven = AppChannelDefinition.Engines.Select(engine => AppTarget(engine, Monday)).ToArray();
        var large = AppMonitoringPlan.PlanSlot(true, null, Prompts(200), seven, Monday);
        Assert.Null(large.SkipCode);
        Assert.Equal(1400, MonitoringMatrix.CountChecks(large.Selected));

        // Every skip code has its own message, including the one the cycle run creation adds (phase 4B replaced the 4A
        // placeholder APP_CYCLE_PENDING with it).
        string[] codes = ["APP_CHANNEL_NOT_OPEN", "SCHEDULE_DISABLED", "MONITORING_NOT_DUE", "MONITORING_SELECTION_EMPTY", "MONITORING_TOO_LARGE",
            AppMonitoringPlan.PlatformUnavailableCode];
        Assert.Equal("APP_PLATFORM_UNAVAILABLE", AppMonitoringPlan.PlatformUnavailableCode);
        Assert.Equal(codes.Length, codes.Select(AppMonitoringPlan.SkipMessage).Distinct(StringComparer.Ordinal).Count());
        Assert.All(codes, code => Assert.False(string.IsNullOrWhiteSpace(AppMonitoringPlan.SkipMessage(code))));
    }

    [Fact]
    public void TheAppPlanNameIsNeitherAMainNorANightPlanAndCannotBeUsedForAnOrdinaryPlan()
    {
        Assert.True(PromptMonitoringIdentity.IsAppSchedule(PromptMonitoringIdentity.AppScheduleName));
        Assert.Null(PromptMonitoringIdentity.SlotKind(PromptMonitoringIdentity.AppScheduleName));
        Assert.False(PromptMonitoringIdentity.IsAutomaticSchedule(PromptMonitoringIdentity.AppScheduleName));
        // The slot processing never builds an API run command for an App slot (AppPlanSqlTests); the App cycle reserves
        // through AppCreditReservations. If one were built, it would be charged like every other slot run (charging never
        // depends on the plan name) and would not be scoped like a monitoring run.
        var command = CollectionScheduleStore.SlotRunCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), PromptMonitoringIdentity.AppScheduleName,
            "20261109T0911", new([Guid.NewGuid()], [new CloroVisibilityTarget("cloro", "chatgpt", "en-US", "US", "{}")]));
        Assert.True(command.ChargeMonitoringCredits);
        Assert.Null(command.PromptTargetKeys);
    }

    [Fact]
    public void TargetEstimateAcceptsAnAppOnlyDraftAndKeepsEveryApiRule()
    {
        IReadOnlyList<PromptMonitoringScale.EstimateCell> Cells(params SiteTargetWrite[] drafts)
            => PromptMonitoringScale.ProjectCells(ManagementStore.ValidateSiteTargetsWrite(new SiteTargetsWrite(0, drafts), false), [], Monday);
        CloroVisibilityTarget[] Apps(params string[] engines)
            => engines.Select(engine => AutomaticMonitoringSelection.StoredTarget("app_collector", engine, "zh-CN", "CN", null)).ToArray();
        MonitoringTargetsEstimateView Estimate(PromptMonitoringScale.Snapshot snapshot, SiteTargetWrite[] api, CloroVisibilityTarget[]? apps)
            => PromptMonitoringScale.EstimateTargets(snapshot, Cells(api), new HashSet<string>(), BeforeMain, Shanghai, 55m, apps);
        string Json(MonitoringTargetsEstimateView view) => JsonSerializer.Serialize(view, Api);

        MonitoringPromptScope[] prompts = [new(Guid.NewGuid()), new(Guid.NewGuid(), [ChatGptKey, Doubao]), new(Guid.NewGuid(), [Kimi])];
        var snapshot = new PromptMonitoringScale.Snapshot(prompts, prompts.Length, [], 1);

        // Without App targets (every tenant the channel is not open for): the blocker is reported exactly as before.
        Assert.Contains("MONITORING_TARGETS_REQUIRED", Estimate(snapshot, [], null).Blockers);
        Assert.Contains("MONITORING_TARGETS_REQUIRED", Estimate(snapshot, [], []).Blockers);
        Assert.Equal(Json(Estimate(snapshot, [], null)), Json(Estimate(snapshot, [], [])));

        // App cells only: a valid configuration. Every figure is the empty API estimate; only the blocker is gone.
        var appOnly = Estimate(snapshot, [], Apps("doubao", "kimi"));
        Assert.Empty(appOnly.Blockers);
        Assert.Equal(Json(Estimate(snapshot, [], null) with { Blockers = [] }), Json(appOnly));
        Assert.Equal((0m, 0m, 0L, 0), (appOnly.WeeklyChecks, appOnly.WeeklyCredits, appOnly.NextSlotChecks, appOnly.Cells.Count));
        Assert.NotEqual(MonitoringSlotKind.App, appOnly.NextSlotKind);
        // No prompt yet: still valid. Prompts that are all scoped away from the draft's App cells: nothing would be collected.
        Assert.Empty(Estimate(new([], 0, [], 1), [], Apps("doubao")).Blockers);
        MonitoringPromptScope[] elsewhere = [new(Guid.NewGuid(), [ChatGptKey]), new(Guid.NewGuid(), [Kimi])];
        Assert.Contains("MONITORING_TARGETS_REQUIRED", Estimate(new(elsewhere, 2, [], 1), [], Apps("doubao")).Blockers);

        // A draft with API cells is judged by its API cells alone, with or without App cells next to them.
        var api = Estimate(snapshot, [ChatGpt], null);
        Assert.Empty(api.Blockers);
        Assert.Equal(Json(api), Json(Estimate(snapshot, [ChatGpt], Apps("doubao", "kimi"))));
        // Both kinds of cells, every prompt scoped to the App cell: the API cells collect nothing, the App cell does — not a
        // blocker (the brand monitors through the App plan alone). Without the App cell, or with an App cell that no prompt
        // is collected on either, it is reported as before. The figures are the API figures in every case.
        MonitoringPromptScope[] appScoped = [new(Guid.NewGuid(), [Doubao])];
        var apiBlocked = Estimate(new(appScoped, 1, [], 1), [ChatGpt], null);
        Assert.Contains("MONITORING_TARGETS_REQUIRED", apiBlocked.Blockers);
        Assert.Equal(Json(apiBlocked), Json(Estimate(new(appScoped, 1, [], 1), [ChatGpt], Apps("kimi"))));
        Assert.Equal(Json(apiBlocked with { Blockers = [] }), Json(Estimate(new(appScoped, 1, [], 1), [ChatGpt], Apps("doubao"))));
        // The other blockers are untouched: 21 API cells are too large with App cells in the draft.
        var many = (from region in new[] { ("US", "en-US"), ("GB", "en-GB"), ("DE", "de-DE"), ("JP", "ja-JP") }
            from engine in AnswerProviderDefinition.Cloro.Engines
            select new SiteTargetWrite("cloro", engine, "scraped", region.Item1, region.Item2)).Take(21).ToArray();
        Assert.Equal(21, many.Length);
        Assert.Contains("MONITORING_TOO_LARGE", Estimate(snapshot, many, Apps("doubao")).Blockers);
    }

    [Fact]
    public void NewPromptEstimateDoesNotReportMissingPlatformsForABrandWithAppTargetsOnly()
    {
        var prompts = Prompts(2);
        var appOnly = new PromptMonitoringScale.Snapshot(prompts, 2, [], 1);
        // No API target and no App target: reported, with or without the new argument.
        Assert.Equal(["MONITORING_TARGETS_REQUIRED"], PromptMonitoringScale.Estimate(appOnly, 1, null, 100m, null, 0).Blockers);
        Assert.Equal(JsonSerializer.Serialize(PromptMonitoringScale.Estimate(appOnly, 1, null, 100m, null, 0), Api),
            JsonSerializer.Serialize(PromptMonitoringScale.Estimate(appOnly, 1, null, 100m, null, 0, 0), Api));
        // App targets only: not reported; the API figures stay zero.
        var estimate = PromptMonitoringScale.Estimate(appOnly, 1, null, 100m, null, 0, 2);
        Assert.Empty(estimate.Blockers);
        Assert.Equal((0L, 0L, 0m), (estimate.AddedChecksPerRound, estimate.TotalChecksPerRound, estimate.Shortfall));
        // "No platform selected" for the new prompts is still reported.
        Assert.Equal(["MONITORING_TARGETS_REQUIRED"], PromptMonitoringScale.Estimate(appOnly, 1, [], 100m, null, 0, 2).Blockers);
        // A brand with API targets: the App target count changes nothing.
        var api = new PromptMonitoringScale.Snapshot(prompts, 2, [new CloroVisibilityTarget("cloro", "chatgpt", "en-US", "US", "{}")], 1);
        Assert.Equal(JsonSerializer.Serialize(PromptMonitoringScale.Estimate(api, 1, null, 5m, 3, 3), Api),
            JsonSerializer.Serialize(PromptMonitoringScale.Estimate(api, 1, null, 5m, 3, 3, 6), Api));
    }

    [Fact]
    public void TheViewCarriesTheAppPlanAsAnAppendedOptionalMember()
    {
        // Existing callers (three positional arguments, or four with billing) still compile and leave the new member empty.
        var legacy = new PromptMonitoringView([], null, null);
        Assert.Null(legacy.AppSchedule);
        var json = JsonSerializer.SerializeToNode(legacy, Api)!.AsObject();
        Assert.Equal(["promptIds", "schedule", "run", "billing", "appSchedule"], json.Select(member => member.Key));
        // AppChannelOptions: the same switch the Worker binds (4.1c) — closed unless the tenant is listed.
        var tenant = Guid.NewGuid();
        Assert.False(new AppChannelOptions().IsOpen(tenant));
        Assert.True(new AppChannelOptions { AppTenantIds = [tenant.ToString()] }.IsOpen(tenant));
    }

    [Fact]
    public void TheNewViewMemberIsDocumentedInChineseAndTheWorkerBindsTheTenantListLikeTheApi()
    {
        // 4.1a: the Swagger description of appSchedule comes from this XML documentation.
        var xml = System.Xml.Linq.XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Jogeoai.Domain.xml"));
        var view = xml.Descendants("member").Single(m => (string?)m.Attribute("name") == "T:Jogeoai.Domain.PromptMonitoringView");
        var description = view.Elements("param").Single(p => (string?)p.Attribute("name") == "AppSchedule").Value;
        Assert.Contains(description, c => c is >= '\u4e00' and <= '\u9fff');
        Assert.Contains("App", description, StringComparison.Ordinal);

        // 4.1c: the Worker binds Collector:AppTenantIds with the same two lines as the API, before the stores are registered.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Jogeoai.slnx"))) directory = directory.Parent;
        const string configure = "builder.Services.Configure<AppChannelOptions>(options =>";
        const string bind = "options.AppTenantIds = builder.Configuration.GetSection(AppChannelOptions.TenantIdsKey).Get<string[]>() ?? []";
        foreach (var project in new[] { "Jogeoai.Api", "Jogeoai.Worker" })
        {
            var program = File.ReadAllText(Path.Combine(directory!.FullName, "src", project, "Program.cs"));
            Assert.Contains(configure, program, StringComparison.Ordinal);
            Assert.Contains(bind, program, StringComparison.Ordinal);
        }
        // In the Worker the stores that read it come from the container (no manual construction), so they receive the options.
        var worker = File.ReadAllText(Path.Combine(directory!.FullName, "src", "Jogeoai.Worker", "Program.cs"));
        Assert.Contains("builder.Services.AddSingleton<ICollectionScheduleStore, CollectionScheduleStore>();", worker, StringComparison.Ordinal);
        Assert.Contains("builder.Services.AddSingleton<IManagementStore, ManagementStore>();", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("new CollectionScheduleStore(", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("new ManagementStore(", worker, StringComparison.Ordinal);
    }
}
