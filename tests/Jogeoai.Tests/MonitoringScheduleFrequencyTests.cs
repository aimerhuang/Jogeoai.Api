using Jogeoai.Domain;
using Jogeoai.Infrastructure;

namespace Jogeoai.Tests;

/// <summary>每日监控时隙按采集频率过滤（09:01 主计划 / 00:05 凌晨计划）的纯函数部分；SQL 端到端见 AnswerModeSqlTests。</summary>
public sealed class MonitoringScheduleFrequencyTests
{
    private static readonly DateTimeOffset Activated = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Nominal = new(2026, 11, 10, 1, 1, 0, TimeSpan.Zero);
    private static readonly DateOnly Anchor = new(2026, 11, 9); // Monday
    private static readonly CloroVisibilityTarget ChatGpt = new("cloro", "chatgpt", "en-US", "US", "{}");
    private static readonly CloroVisibilityTarget DeepSeekFast = new("deepseek_api", "deepseek", "zh-CN", "CN", "{}");
    private static readonly CloroVisibilityTarget DeepSeekDeep = DeepSeekFast with { AnswerMode = "deep" };

    private static AutomaticMonitoringSelection.Target Cell(CloroVisibilityTarget target, MonitoringFrequency frequency,
        DateOnly? anchor = null, DateTimeOffset? enabledAt = null)
        => new(Guid.NewGuid(), target, enabledAt ?? Activated, frequency, anchor ?? Anchor);

    private static CollectionScheduleSelection Reference(params CloroVisibilityTarget[] targets) => new([], targets, 1);

    [Fact]
    public void ScheduleNamesMapToSlotKinds()
    {
        Assert.Equal(MonitoringSlotKind.Main, PromptMonitoringIdentity.SlotKind(PromptMonitoringIdentity.ScheduleName));
        Assert.Equal(MonitoringSlotKind.Night, PromptMonitoringIdentity.SlotKind(PromptMonitoringIdentity.NightScheduleName));
        Assert.Null(PromptMonitoringIdentity.SlotKind("weekly report"));
        Assert.True(PromptMonitoringIdentity.IsAutomaticSchedule(PromptMonitoringIdentity.NightScheduleName));
        Assert.False(PromptMonitoringIdentity.IsAutomaticSchedule(null));
        Assert.Equal(new DateOnly(2026, 11, 10), AutomaticMonitoringSelection.SlotLocalDate("20261110T0901"));
    }

    [Fact]
    public void MainSlotKeepsEveryConfiguredCellInThePlanButOnlyDueCellsInTheSlot()
    {
        var prompts = new[] { new MonitoringPromptScope(Guid.NewGuid()), new MonitoringPromptScope(Guid.NewGuid()) };
        var targets = new[]
        {
            Cell(ChatGpt, MonitoringFrequency.Daily), Cell(DeepSeekFast, MonitoringFrequency.Weekly),
            Cell(DeepSeekDeep, MonitoringFrequency.EveryOtherDay), Cell(new("cloro", "gemini", "en-US", "US", "{}"), MonitoringFrequency.TwiceDaily),
        };
        // Tuesday after a Monday anchor: weekly and every_other_day are off.
        var tuesday = AutomaticMonitoringSelection.PlanSlot(MonitoringSlotKind.Main, Reference(), prompts, targets, Anchor.AddDays(1), Nominal);
        Assert.Null(tuesday.SkipCode);
        Assert.Equal(4, tuesday.Current.Targets.Count);
        Assert.Equal(new[] { "chatgpt", "gemini" }, tuesday.Selected.Targets.Select(t => t.Engine).Order());
        Assert.Equal(4, MonitoringMatrix.CountChecks(tuesday.Selected));

        // Two days later every_other_day is due again; a week later weekly is due as well.
        var thursday = AutomaticMonitoringSelection.PlanSlot(MonitoringSlotKind.Main, Reference(), prompts, targets, Anchor.AddDays(2), Nominal);
        Assert.Equal(3, thursday.Selected.Targets.Count);
        Assert.Contains(thursday.Selected.Targets, t => t.AnswerMode == "deep");
        var nextMonday = AutomaticMonitoringSelection.PlanSlot(MonitoringSlotKind.Main, Reference(), prompts, targets, Anchor.AddDays(7), Nominal);
        Assert.Equal(3, nextMonday.Selected.Targets.Count);
        Assert.Contains(nextMonday.Selected.Targets, t => t.Engine == "deepseek" && t.AnswerMode is null);
    }

    [Fact]
    public void AllDailyCellsProduceTheHistoricalSelection()
    {
        var prompts = new[] { new MonitoringPromptScope(Guid.NewGuid()) };
        var targets = new[] { Cell(ChatGpt, MonitoringFrequency.Daily), Cell(DeepSeekFast, MonitoringFrequency.Daily) };
        var plan = AutomaticMonitoringSelection.PlanSlot(MonitoringSlotKind.Main, Reference(), prompts, targets, Anchor, Nominal);
        var historical = AutomaticMonitoringSelection.Select(prompts, targets, Reference(), Nominal);
        Assert.Equal(CollectionIdentity.SelectionHash(historical), CollectionIdentity.SelectionHash(plan.Selected));
        Assert.Equal(CollectionIdentity.SelectionHash(plan.Current), CollectionIdentity.SelectionHash(plan.Selected));
    }

    [Fact]
    public void SlotWithNoDueCellIsSkippedAsNotDueWhileEmptyOrPendingKeepTheirCodes()
    {
        var prompts = new[] { new MonitoringPromptScope(Guid.NewGuid()) };
        var weekly = new[] { Cell(DeepSeekFast, MonitoringFrequency.Weekly) };
        var off = AutomaticMonitoringSelection.PlanSlot(MonitoringSlotKind.Main, Reference(), prompts, weekly, Anchor.AddDays(3), Nominal);
        Assert.Equal(AutomaticMonitoringSelection.NotDueCode, off.SkipCode);
        Assert.Single(off.Current.Targets);
        Assert.Null(AutomaticMonitoringSelection.PlanSlot(MonitoringSlotKind.Main, Reference(), prompts, weekly, Anchor.AddDays(7), Nominal).SkipCode);
        // Before the anchor nothing is due either.
        Assert.Equal(AutomaticMonitoringSelection.NotDueCode,
            AutomaticMonitoringSelection.PlanSlot(MonitoringSlotKind.Main, Reference(), prompts, weekly, Anchor.AddDays(-7), Nominal).SkipCode);

        // Nothing configured stays MONITORING_SELECTION_EMPTY; a cell enabled after the nominal slot is not "not due".
        Assert.Equal("MONITORING_SELECTION_EMPTY",
            AutomaticMonitoringSelection.PlanSlot(MonitoringSlotKind.Main, Reference(), prompts, [], Anchor, Nominal).SkipCode);
        var late = new[] { Cell(DeepSeekFast, MonitoringFrequency.Daily, enabledAt: Nominal.AddHours(1)),
            Cell(ChatGpt, MonitoringFrequency.Weekly, enabledAt: Nominal.AddHours(1)) };
        Assert.Equal("MONITORING_SELECTION_EMPTY",
            AutomaticMonitoringSelection.PlanSlot(MonitoringSlotKind.Main, Reference(), prompts, late, Anchor.AddDays(1), Nominal).SkipCode);
        // An activated weekly cell on its off-day next to a not-yet-active daily cell: nothing could run because of the frequency.
        Assert.Equal(AutomaticMonitoringSelection.NotDueCode, AutomaticMonitoringSelection.PlanSlot(MonitoringSlotKind.Main, Reference(), prompts,
            [late[0], Cell(ChatGpt, MonitoringFrequency.Weekly)], Anchor.AddDays(1), Nominal).SkipCode);
        var legacy = new[] { new AutomaticMonitoringSelection.Target(Guid.NewGuid(), ChatGpt, null, MonitoringFrequency.Weekly, Anchor) };
        Assert.Equal("MONITORING_ACTIVATION_PENDING",
            AutomaticMonitoringSelection.PlanSlot(MonitoringSlotKind.Main, Reference(), prompts, legacy, Anchor, Nominal).SkipCode);
    }

    [Fact]
    public void NightSlotOnlyTakesTwiceDailyCellsAndFollowsTheMainPlanMembership()
    {
        var all = new MonitoringPromptScope(Guid.NewGuid());
        var deepOnly = new MonitoringPromptScope(Guid.NewGuid(), [CollectionIdentity.TargetKey(DeepSeekDeep)]);
        var chatOnly = new MonitoringPromptScope(Guid.NewGuid(), [CollectionIdentity.TargetKey(ChatGpt)]);
        // The deep cell is a legacy member (no activation time) of the main plan; the night slot still treats it as known.
        var targets = new[]
        {
            Cell(ChatGpt, MonitoringFrequency.Daily), Cell(DeepSeekFast, MonitoringFrequency.Weekly),
            new AutomaticMonitoringSelection.Target(Guid.NewGuid(), DeepSeekDeep, null, MonitoringFrequency.TwiceDaily, null),
        };
        var main = new CollectionScheduleSelection([], [ChatGpt, DeepSeekFast, DeepSeekDeep], 2);
        var night = AutomaticMonitoringSelection.PlanSlot(MonitoringSlotKind.Night, main, [all, deepOnly, chatOnly], targets, Anchor, Nominal);
        Assert.Null(night.SkipCode);
        Assert.Equal(DeepSeekDeep, Assert.Single(night.Selected.Targets));
        Assert.Equal(DeepSeekDeep, Assert.Single(night.Current.Targets));
        // Custom scopes intersect: the ChatGPT-only prompt has no night cell and is dropped; the main plan's sample count applies.
        Assert.Equal(new[] { all.PromptId, deepOnly.PromptId }.Order(), night.Selected.PromptIds);
        Assert.Equal(2, night.Selected.SampleCount);
        Assert.Equal(4, MonitoringMatrix.CountChecks(night.Selected));

        // Without twice_daily cells the night slot is empty (the reconcile disables the night plan in that case).
        var none = AutomaticMonitoringSelection.PlanSlot(MonitoringSlotKind.Night, main, [all], targets.Take(2).ToArray(), Anchor, Nominal);
        Assert.Equal("MONITORING_SELECTION_EMPTY", none.SkipCode);
    }

    [Fact]
    public void DueSelectionFiltersThePlanForTheNextMainSlot()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var weeklyKey = CollectionIdentity.TargetKey(DeepSeekFast);
        var plan = new CollectionScheduleSelection([a, b], [ChatGpt, DeepSeekFast], 1, [new(b, [weeklyKey])]);
        var targets = new[] { Cell(ChatGpt, MonitoringFrequency.Daily), Cell(DeepSeekFast, MonitoringFrequency.Weekly) };
        Assert.Same(plan, AutomaticMonitoringSelection.DueSelection(plan, targets, Anchor, MonitoringSlotKind.Main));
        var tuesday = AutomaticMonitoringSelection.DueSelection(plan, targets, Anchor.AddDays(1), MonitoringSlotKind.Main);
        // Prompt b only used the weekly cell and drops out on its off-day.
        Assert.Equal([a], tuesday.PromptIds);
        Assert.Equal([ChatGpt], tuesday.Targets);
        Assert.Equal(1, MonitoringMatrix.CountChecks(tuesday));
        var nothing = AutomaticMonitoringSelection.DueSelection(plan with { Targets = [DeepSeekFast], PromptTargets = null }, targets,
            Anchor.AddDays(1), MonitoringSlotKind.Main);
        Assert.Empty(nothing.Targets);
        Assert.Equal(0, PromptMonitoringService.BillingCheckCount(nothing, 2));
    }

    [Fact]
    public void NightSlotRunsAreChargedAutomaticMonitoringRunsOfTheirOwnPlan()
    {
        var tenant = Guid.NewGuid(); var site = Guid.NewGuid(); var night = Guid.NewGuid();
        var key = CollectionIdentity.TargetKey(DeepSeekDeep);
        var prompt = Guid.NewGuid();
        var selection = new CollectionScheduleSelection([prompt], [DeepSeekDeep], 1, [new(prompt, [key])]);
        var command = CollectionScheduleStore.SlotRunCommand(tenant, site, night, PromptMonitoringIdentity.NightScheduleName, "20261110T0005", selection);
        Assert.True(command.ChargeMonitoringCredits);
        Assert.True(command.RequireEnabledTargets);
        Assert.Same(selection.PromptTargets, command.PromptTargetKeys);
        // The night plan's own requester: the main plan's "already ran today" guard never sees night runs.
        Assert.Equal($"schedule:{night:N}", command.RequestedBy);
        Assert.Equal($"schedule:{night:N}:20261110T0005", command.IdempotencyKey);
    }
}
