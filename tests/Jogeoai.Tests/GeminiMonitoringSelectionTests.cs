using Jogeoai.Domain;
using Jogeoai.Infrastructure;

namespace Jogeoai.Tests;

public sealed class GeminiMonitoringSelectionTests
{
    private static readonly CloroVisibilityTarget ChatGpt = new("cloro", "chatgpt", "zh-CN", "US", "{}");
    private static readonly DateTimeOffset Enabled = DateTimeOffset.Parse("2026-10-08T02:00:00Z"); // 10:00 Shanghai
    private static readonly Guid Prompt = Guid.NewGuid();

    [Theory]
    [InlineData("gemini")]
    [InlineData("copilot")]
    [InlineData("google-ai-overview")]
    public void ActivationUsesNominalSlotNotRecoveryTimeAndDoesNotRewriteFrozenSelection(string engine)
    {
        var added = new CloroVisibilityTarget("cloro", engine, "zh-CN", "US", "{}");
        var old = new CollectionScheduleSelection([Prompt], [ChatGpt], 2);
        var targets = new[] { new AutomaticMonitoringSelection.Target(Guid.NewGuid(), ChatGpt, null), new(Guid.NewGuid(), added, Enabled) };
        var zone = CollectionScheduleCalendar.ParseTimeZone("Asia/Shanghai");
        var window = CollectionScheduleCalendar.DueWindow("daily", null, new TimeOnly(9, 0), zone,
            Enabled.AddDays(-3).AddHours(-1), Enabled.AddHours(5));
        var missed = AutomaticMonitoringSelection.Select([Prompt], targets, old, window.LatestDueAt);
        Assert.Single(missed.Targets);
        Assert.Equal("chatgpt", missed.Targets[0].Engine);
        var next = AutomaticMonitoringSelection.Select([Prompt], targets, old, window.NextSlotAt);
        Assert.Equal(2, next.Targets.Count);
        Assert.Equal(2, next.SampleCount);
        Assert.Single(old.Targets);
        Assert.Single(missed.Targets);
        Assert.Equal(2, AutomaticMonitoringSelection.Select([Prompt], targets, old, Enabled).Targets.Count);
    }

    [Theory]
    [InlineData("gemini")]
    [InlineData("copilot")]
    [InlineData("google-ai-overview")]
    public void LegacyNullOnlyPreservesPreviousEligibilityAndDisablingRemovesFutureTarget(string engine)
    {
        var added = new CloroVisibilityTarget("cloro", engine, "zh-CN", "US", "{}");
        var old = new CollectionScheduleSelection([Prompt], [ChatGpt]);
        var targets = new[] { new AutomaticMonitoringSelection.Target(Guid.NewGuid(), ChatGpt, null), new(Guid.NewGuid(), added, null) };
        Assert.Single(AutomaticMonitoringSelection.Select([Prompt], targets, old, Enabled).Targets);
        Assert.Equal(2, AutomaticMonitoringSelection.Select([Prompt], targets,
            old with { Targets = [ChatGpt, added] }, Enabled).Targets.Count);
        Assert.Empty(AutomaticMonitoringSelection.Select([Prompt], [], old, Enabled).Targets);
        var reenabled = new[] { targets[0], targets[1] with { EnabledAt = Enabled.AddDays(1) } };
        Assert.Single(AutomaticMonitoringSelection.Select([Prompt], reenabled,
            old with { Targets = [ChatGpt, added] }, Enabled).Targets);
    }

    [Theory]
    [InlineData(0, 1, 1, "MONITORING_SELECTION_EMPTY")]
    [InlineData(2, 0, 1, "MONITORING_SELECTION_EMPTY")]
    [InlineData(201, 1, 1, "MONITORING_TOO_LARGE")]
    [InlineData(1, 21, 1, "MONITORING_TOO_LARGE")]
    [InlineData(200, 2, 3, "MONITORING_TOO_LARGE")]
    [InlineData(2, 1, 1, null)]
    public void InvalidSelectionsCannotBecomeSmallerPaidBatch(int promptCount, int targetCount, int samples, string? code)
    {
        var selection = new CollectionScheduleSelection(Enumerable.Range(0, promptCount).Select(_ => Guid.NewGuid()).ToArray(),
            Enumerable.Repeat(ChatGpt, targetCount).ToArray(), samples);
        Assert.Equal(code, AutomaticMonitoringSelection.InvalidReason(selection));
    }

    [Fact]
    public void CustomScopeOnANewlyEnabledTargetJoinsOnlyFutureSlots()
    {
        var gemini = new CloroVisibilityTarget("cloro", "gemini", "zh-CN", "US", "{}");
        var custom = Guid.Parse("00000000-0000-4000-8000-000000000002");
        var old = new CollectionScheduleSelection([Prompt], [ChatGpt]);
        var targets = new[] { new AutomaticMonitoringSelection.Target(Guid.NewGuid(), ChatGpt, null), new(Guid.NewGuid(), gemini, Enabled) };
        MonitoringPromptScope[] scopes = [new(Prompt), new(custom, [CollectionIdentity.TargetKey(gemini)])];

        // Before Gemini's activation the custom prompt has no eligible target: it is dropped, not widened to ChatGPT.
        var before = AutomaticMonitoringSelection.Select(scopes, targets, old, Enabled.AddHours(-1));
        Assert.Equal([Prompt], before.PromptIds);
        Assert.Equal([ChatGpt], before.Targets);
        Assert.Null(before.PromptTargets);
        Assert.Null(AutomaticMonitoringSelection.InvalidReason(before));

        var after = AutomaticMonitoringSelection.Select(scopes, targets, old, Enabled);
        Assert.Equal(new[] { Prompt, custom }.Order(), after.PromptIds);
        Assert.Equal(2, after.Targets.Count);
        var only = Assert.Single(after.PromptTargets!);
        Assert.Equal((custom, CollectionIdentity.TargetKey(gemini)), (only.PromptId, Assert.Single(only.TargetKeys)));
        Assert.Equal(3, MonitoringMatrix.CountChecks(after));
        Assert.NotEqual(CollectionIdentity.SelectionHash(before), CollectionIdentity.SelectionHash(after));
    }

    [Fact]
    public void AllScopePromptsKeepTheHistoricalSelectionAndHash()
    {
        var gemini = new CloroVisibilityTarget("cloro", "gemini", "zh-CN", "US", "{}");
        var old = new CollectionScheduleSelection([Prompt], [ChatGpt]);
        var targets = new[] { new AutomaticMonitoringSelection.Target(Guid.NewGuid(), gemini, Enabled), new(Guid.NewGuid(), ChatGpt, null) };
        var legacy = AutomaticMonitoringSelection.Select([Prompt], targets, old, Enabled);
        var scoped = AutomaticMonitoringSelection.Select([new MonitoringPromptScope(Prompt)], targets, old, Enabled);
        Assert.Null(scoped.PromptTargets);
        Assert.Equal(legacy.PromptIds, scoped.PromptIds);
        Assert.Equal(legacy.Targets, scoped.Targets);
        Assert.Equal(CollectionIdentity.SelectionHash(legacy.PromptIds, legacy.Targets, legacy.SampleCount), CollectionIdentity.SelectionHash(scoped));
    }

    [Fact]
    public void LegacyTargetDroppedFromTheUnionGetsAForwardOnlyBoundaryWhileMembersKeepNull()
    {
        var gemini = new CloroVisibilityTarget("cloro", "gemini", "zh-CN", "US", "{}");
        var copilot = new CloroVisibilityTarget("cloro", "copilot", "zh-CN", "US", "{}");
        var custom = Guid.Parse("00000000-0000-4000-8000-000000000003");
        var old = new CollectionScheduleSelection([custom], [ChatGpt, gemini]);
        var targets = new[]
        {
            new AutomaticMonitoringSelection.Target(Guid.NewGuid(), ChatGpt, null), // legacy member that stays
            new(Guid.NewGuid(), gemini, null), // legacy member dropped: no prompt scope uses it any more
            new(Guid.NewGuid(), copilot, null), // legacy target not yet in the plan
        };
        var selection = AutomaticMonitoringSelection.Select([new MonitoringPromptScope(custom, [CollectionIdentity.TargetKey(ChatGpt)])],
            targets, old);
        Assert.Equal([ChatGpt], selection.Targets);
        Assert.Equal(["gemini", "copilot"], AutomaticMonitoringSelection.ActivationCandidates(targets, old, selection).Select(t => t.Value.Engine));

        // Without custom scopes every eligible target stays in the plan: only unknown legacy targets are activated, as before.
        var legacy = AutomaticMonitoringSelection.Select([custom], targets, old);
        Assert.Equal(["copilot"], AutomaticMonitoringSelection.ActivationCandidates(targets, old, legacy).Select(t => t.Value.Engine));
    }

    [Fact]
    public void OversizedPromptListIsNotShrunkByDroppingCustomPrompts()
    {
        // Prompts() returns at most 201 rows; dropping empty custom prompts must not make an oversized brand valid.
        var scopes = Enumerable.Range(0, 201).Select(i => new MonitoringPromptScope(Guid.NewGuid(), i < 10 ? [] : null)).ToArray();
        var selection = AutomaticMonitoringSelection.Select(scopes,
            [new AutomaticMonitoringSelection.Target(Guid.NewGuid(), ChatGpt, Enabled)], new CollectionScheduleSelection([], []), Enabled);
        Assert.Equal(201, selection.PromptIds.Count);
        Assert.Equal("MONITORING_TOO_LARGE", AutomaticMonitoringSelection.InvalidReason(selection));
    }

    [Fact]
    public void InvalidReasonCountsCheckPairsForTheThousandLimit()
    {
        var gemini = new CloroVisibilityTarget("cloro", "gemini", "zh-CN", "US", "{}");
        var prompts = Enumerable.Range(0, 200).Select(_ => Guid.NewGuid()).Order().ToArray();
        var cartesian = new CollectionScheduleSelection(prompts, [ChatGpt, gemini], 3);
        Assert.Equal("MONITORING_TOO_LARGE", AutomaticMonitoringSelection.InvalidReason(cartesian));
        var scoped = cartesian with { PromptTargets = prompts.Take(100)
            .Select(id => new CollectionPromptTargets(id, [CollectionIdentity.TargetKey(gemini)])).ToArray() };
        Assert.Null(AutomaticMonitoringSelection.InvalidReason(scoped)); // 100 × 2 × 3 + 100 × 1 × 3 = 900
    }
}
