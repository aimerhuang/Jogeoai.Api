using Jogeoai.Domain;
using Jogeoai.Infrastructure;

namespace Jogeoai.Tests;

/// <summary>每日监控检测矩阵：all 提问取全部目标，custom 提问取范围 ∩ 目标，交集为空的提问被剔除，1000 上限按检测对计算。</summary>
public sealed class MonitoringMatrixTests
{
    private static readonly CloroVisibilityTarget ChatGpt = new("cloro", "chatgpt", "zh-CN", "US", "{}");
    private static readonly CloroVisibilityTarget Gemini = new("cloro", "gemini", "zh-CN", "US", "{}");
    private static readonly CloroVisibilityTarget Doubao = new("doubao_api", "doubao", "zh-CN", "CN", "{}");
    private static readonly CloroVisibilityTarget[] Targets = [ChatGpt, Gemini, Doubao];
    private static string Key(CloroVisibilityTarget target) => CollectionIdentity.TargetKey(target);
    private static Guid Id(int n) => Guid.Parse($"00000000-0000-4000-8000-{n:D12}");

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(5, 3, 1)]
    [InlineData(5, 3, 3)]
    [InlineData(200, 5, 1)]
    public void AllScopePromptsEqualTheCartesianProduct(int prompts, int targets, int samples)
    {
        var scopes = Enumerable.Range(1, prompts).Select(i => new MonitoringPromptScope(Id(i))).ToArray();
        var list = Enumerable.Range(0, targets).Select(i => new CloroVisibilityTarget("cloro", "chatgpt", $"en-A{(char)('A' + i)}", "US", "{}")).ToArray();
        Assert.Equal((long)prompts * targets * samples, MonitoringMatrix.CountChecks(scopes, list, samples));
        var selection = MonitoringMatrix.BuildSelection(scopes, list, samples);
        Assert.Null(selection.PromptTargets);
        Assert.Equal(scopes.Select(s => s.PromptId).Order(), selection.PromptIds);
        Assert.Equal(list, selection.Targets);
        Assert.Equal((long)prompts * targets * samples, MonitoringMatrix.CountChecks(selection));
        Assert.Equal(PromptMonitoringPricing.Estimate(prompts, targets, samples), PromptMonitoringPricing.Estimate(MonitoringMatrix.CountChecks(selection)));
    }

    [Fact]
    public void AllScopeSelectionIsIdenticalToTheHistoricalSelection()
    {
        var ids = new[] { Id(3), Id(1), Id(2) };
        var built = MonitoringMatrix.BuildSelection(ids.Select(id => new MonitoringPromptScope(id)).ToArray(), Targets, 2);
        var legacy = new CollectionScheduleSelection(ids.Order().ToArray(), Targets, 2);
        Assert.Equal(CollectionIdentity.SelectionHash(legacy), CollectionIdentity.SelectionHash(built));
        Assert.Equal(legacy.PromptIds, built.PromptIds);
        Assert.Equal(legacy.Targets, built.Targets);
        Assert.Null(built.PromptTargets);
    }

    [Fact]
    public void CustomScopesCountTheIntersectionAndListOnlyCustomPrompts()
    {
        MonitoringPromptScope[] scopes =
        [
            new(Id(1)),                                   // all → 3 targets
            new(Id(2), [Key(Gemini)]),                    // custom → 1
            new(Id(3), [Key(ChatGpt), Key(Doubao), "f".PadLeft(64, 'f')]), // unknown key ignored → 2
        ];
        Assert.Equal((3 + 1 + 2) * 2L, MonitoringMatrix.CountChecks(scopes, Targets, 2));
        var selection = MonitoringMatrix.BuildSelection(scopes, Targets, 2);
        Assert.Equal([Id(1), Id(2), Id(3)], selection.PromptIds);
        Assert.Equal(Targets, selection.Targets);
        var custom = Assert.IsAssignableFrom<IReadOnlyList<CollectionPromptTargets>>(selection.PromptTargets);
        Assert.Equal([Id(2), Id(3)], custom.Select(c => c.PromptId));
        Assert.Equal([Key(Gemini)], custom[0].TargetKeys);
        Assert.Equal(new[] { Key(ChatGpt), Key(Doubao) }.Order(StringComparer.Ordinal), custom[1].TargetKeys);
        Assert.Equal(12, MonitoringMatrix.CountChecks(selection));
        Assert.Equal(120m, PromptMonitoringPricing.Estimate(MonitoringMatrix.CountChecks(selection)));
    }

    [Fact]
    public void PromptsWithAnEmptyIntersectionAreDroppedAndNeverWidenedToAll()
    {
        MonitoringPromptScope[] scopes =
        [
            new(Id(1), [Key(Gemini)]),
            new(Id(2), [Key(new CloroVisibilityTarget("cloro", "copilot", "zh-CN", "US"))]), // platform not enabled
            new(Id(3), []),                                                                    // custom without rows
        ];
        var selection = MonitoringMatrix.BuildSelection(scopes, Targets, 1);
        Assert.Equal([Id(1)], selection.PromptIds);
        // Targets is the union of effective targets, so unused targets are not part of the Run.
        Assert.Equal([Gemini], selection.Targets);
        Assert.Equal(1, MonitoringMatrix.CountChecks(selection));
        Assert.Equal(0, MonitoringMatrix.CountChecks([scopes[1], scopes[2]], Targets, 1));

        var none = MonitoringMatrix.BuildSelection([scopes[1], scopes[2]], Targets, 1);
        Assert.Empty(none.PromptIds);
        Assert.Equal(Targets, none.Targets);
        Assert.Equal("MONITORING_SELECTION_EMPTY", AutomaticMonitoringSelection.InvalidReason(none));
    }

    [Fact]
    public void FastAndDeepTargetsOfOneEngineAreSeparatePairs()
    {
        var fast = new CloroVisibilityTarget("deepseek_api", "deepseek", "zh-CN", "CN", "{}");
        var deep = fast with { AnswerMode = "deep" };
        CloroVisibilityTarget[] targets = [ChatGpt, fast, deep];
        Assert.Equal(3, targets.Select(Key).Distinct(StringComparer.Ordinal).Count());
        MonitoringPromptScope[] scopes = [new(Id(1)), new(Id(2), [Key(deep)]), new(Id(3), [Key(fast), Key(deep)])];
        var pairs = MonitoringMatrix.Expand(scopes, targets);
        Assert.Equal(
            [(Id(1), 0), (Id(1), 1), (Id(1), 2), (Id(2), 2), (Id(3), 1), (Id(3), 2)],
            pairs.Select(p => (p.PromptId, p.TargetIndex)));
        Assert.Equal((3 + 1 + 2) * 2L, MonitoringMatrix.CountChecks(scopes, targets, 2));
        var selection = MonitoringMatrix.BuildSelection(scopes, targets, 1);
        Assert.Equal(targets, selection.Targets);
        Assert.Equal(6, MonitoringMatrix.CountChecks(selection));
        Assert.Equal([Key(deep)], selection.PromptTargets![0].TargetKeys);
        // All-scope prompts are a plain cartesian product over both modes.
        Assert.Equal(4L, MonitoringMatrix.CountChecks([new(Id(1)), new(Id(2))], [fast, deep], 1));
        Assert.Equal(4L, MonitoringMatrix.CountChecks(MonitoringMatrix.BuildSelection([new(Id(1)), new(Id(2))], [fast, deep], 1)));
    }

    [Fact]
    public void ExpandKeepsPromptMajorTargetMinorOrder()
    {
        var pairs = MonitoringMatrix.Expand([Id(2), Id(1)], Targets, [new(Id(1), [Key(Doubao), Key(ChatGpt)])]);
        Assert.Equal(
            [(Id(2), 0), (Id(2), 1), (Id(2), 2), (Id(1), 0), (Id(1), 2)],
            pairs.Select(p => (p.PromptId, p.TargetIndex)));
        Assert.All(pairs, p => Assert.Equal(Key(Targets[p.TargetIndex]), p.TargetKey));
        Assert.Equal(6, MonitoringMatrix.Expand([Id(2), Id(1)], Targets, null).Count);
    }

    [Theory]
    [InlineData(100, 10, 1, false, null)]                       // 1000 cartesian checks is the limit
    [InlineData(101, 10, 1, false, "MONITORING_TOO_LARGE")]
    [InlineData(200, 10, 1, true, null)]                        // 2000 cartesian, but custom → 200 × 1 × 1
    [InlineData(200, 20, 6, true, "MONITORING_TOO_LARGE")]      // custom 200 × 1 × 6 = 1200
    [InlineData(200, 20, 5, true, null)]                        // custom 200 × 1 × 5 = 1000
    public void TheThousandLimitCountsCheckPairs(int prompts, int targetCount, int samples, bool custom, string? expected)
    {
        var targets = Enumerable.Range(0, targetCount).Select(i => new CloroVisibilityTarget("cloro", "chatgpt", $"en-A{(char)('A' + i)}", "US", "{}")).ToArray();
        var scopes = Enumerable.Range(1, prompts).Select(i => new MonitoringPromptScope(Id(i), custom ? [Key(targets[0])] : null)).ToArray();
        var selection = MonitoringMatrix.BuildSelection(scopes, targets, samples);
        Assert.Equal(expected, AutomaticMonitoringSelection.InvalidReason(selection));
        Assert.Equal(expected is null, MonitoringMatrix.CountChecks(selection) <= MonitoringMatrix.MaxChecks);
    }

    [Fact]
    public void NormalizationRejectsKeysOutsideTheRunAndNormalizesOrder()
    {
        IReadOnlyList<Guid> ids = [Id(1), Id(2)];
        Assert.True(MonitoringMatrix.TryNormalizePromptTargets(null, ids, Targets, out var none));
        Assert.Null(none);
        Assert.True(MonitoringMatrix.TryNormalizePromptTargets([], ids, Targets, out none));
        Assert.Null(none);

        Assert.True(MonitoringMatrix.TryNormalizePromptTargets(
            [new(Id(2), [Key(Gemini), Key(ChatGpt)]), new(Id(1), [Key(Doubao)])], ids, Targets, out var normalized));
        Assert.Equal([Id(1), Id(2)], normalized!.Select(p => p.PromptId));
        Assert.Equal(new[] { Key(Gemini), Key(ChatGpt) }.Order(StringComparer.Ordinal), normalized[1].TargetKeys);

        Assert.False(MonitoringMatrix.TryNormalizePromptTargets([new(Id(1), [Key(new CloroVisibilityTarget("cloro", "copilot"))])], ids, Targets, out _));
        Assert.False(MonitoringMatrix.TryNormalizePromptTargets([new(Id(9), [Key(Gemini)])], ids, Targets, out _));
        Assert.False(MonitoringMatrix.TryNormalizePromptTargets([new(Id(1), [])], ids, Targets, out _));
        Assert.False(MonitoringMatrix.TryNormalizePromptTargets([new(Id(1), [Key(Gemini), Key(Gemini)])], ids, Targets, out _));
        Assert.False(MonitoringMatrix.TryNormalizePromptTargets([new(Id(1), [Key(Gemini)]), new(Id(1), [Key(ChatGpt)])], ids, Targets, out _));
    }

    [Fact]
    public void WithoutNothingToRemoveReturnsTheSameSelectionAndHash()
    {
        var legacy = MonitoringMatrix.BuildSelection([new(Id(2)), new(Id(1))], Targets, 2);
        Assert.Same(legacy, MonitoringMatrix.Without(legacy, []));
        // Pairs of other prompts, or keys outside a prompt's effective targets, do not touch the selection either.
        var copilot = Key(new CloroVisibilityTarget("cloro", "copilot", "zh-CN", "US"));
        var same = MonitoringMatrix.Without(legacy, [new PromptTargetPair(Id(9), Key(Gemini)), new PromptTargetPair(Id(1), copilot)]);
        Assert.Same(legacy, same);
        Assert.Equal(CollectionIdentity.SelectionHash(legacy), CollectionIdentity.SelectionHash(same));
        Assert.Null(same.PromptTargets);

        var scoped = MonitoringMatrix.BuildSelection([new(Id(1)), new(Id(2), [Key(Gemini)])], Targets, 1);
        Assert.Same(scoped, MonitoringMatrix.Without(scoped, [new PromptTargetPair(Id(2), Key(ChatGpt))]));
    }

    [Fact]
    public void WithoutNarrowsOnlyTheAffectedPromptsAndKeepsOthersOnTheirScope()
    {
        // Id(1) all, Id(2) custom {Gemini, Doubao}, Id(3) all; Id(1)×ChatGPT and Id(2)×Gemini were already collected.
        var selection = MonitoringMatrix.BuildSelection([new(Id(1)), new(Id(2), [Key(Gemini), Key(Doubao)]), new(Id(3))], Targets, 2);
        var result = MonitoringMatrix.Without(selection, [new PromptTargetPair(Id(1), Key(ChatGpt)), new PromptTargetPair(Id(2), Key(Gemini))]);

        Assert.Equal([Id(1), Id(2), Id(3)], result.PromptIds);
        Assert.Equal(Targets, result.Targets);
        var custom = result.PromptTargets!;
        // Id(3) stays 'all' (not listed); Id(1) becomes {Gemini, Doubao}; Id(2) keeps only Doubao.
        Assert.Equal([Id(1), Id(2)], custom.Select(c => c.PromptId));
        Assert.Equal(new[] { Key(Gemini), Key(Doubao) }.Order(StringComparer.Ordinal), custom[0].TargetKeys);
        Assert.Equal([Key(Doubao)], custom[1].TargetKeys);
        Assert.Equal((2 + 1 + 3) * 2L, MonitoringMatrix.CountChecks(result));
        Assert.Equal(2, result.SampleCount);
        Assert.Null(AutomaticMonitoringSelection.InvalidReason(result));
    }

    [Fact]
    public void WithoutDropsPromptsWithNothingLeftAndCanEmptyTheSelection()
    {
        var selection = MonitoringMatrix.BuildSelection([new(Id(1), [Key(Gemini)]), new(Id(2))], [ChatGpt, Gemini], 1);
        var result = MonitoringMatrix.Without(selection, [new PromptTargetPair(Id(1), Key(Gemini))]);
        Assert.Equal([Id(2)], result.PromptIds);
        Assert.Null(result.PromptTargets);
        Assert.Equal([ChatGpt, Gemini], result.Targets);
        Assert.Equal(2, MonitoringMatrix.CountChecks(result));

        var all = MonitoringMatrix.Without(selection, [new PromptTargetPair(Id(1), Key(Gemini)),
            new PromptTargetPair(Id(2), Key(ChatGpt)), new PromptTargetPair(Id(2), Key(Gemini))]);
        Assert.Empty(all.PromptIds);
        Assert.Equal(0, MonitoringMatrix.CountChecks(all));
        Assert.Equal("MONITORING_SELECTION_EMPTY", AutomaticMonitoringSelection.InvalidReason(all));
    }

    [Fact]
    public void WithoutShrinksTheTargetUnionWhenACellIsNoLongerUsed()
    {
        var selection = MonitoringMatrix.BuildSelection([new(Id(1), [Key(ChatGpt), Key(Doubao)]), new(Id(2), [Key(Doubao)])], Targets, 1);
        Assert.Equal([ChatGpt, Doubao], selection.Targets);
        var result = MonitoringMatrix.Without(selection, [new PromptTargetPair(Id(1), Key(Doubao)), new PromptTargetPair(Id(2), Key(Doubao))]);
        Assert.Equal([Id(1)], result.PromptIds);
        Assert.Equal([ChatGpt], result.Targets);
        Assert.Equal([Key(ChatGpt)], Assert.Single(result.PromptTargets!).TargetKeys);
    }

    [Fact]
    public void CheckCountPricingOverloadMatchesTheLegacySignature()
    {
        Assert.Equal(PromptMonitoringPricing.Estimate(5, 3, 3), PromptMonitoringPricing.Estimate(45L));
        Assert.Equal(0m, PromptMonitoringPricing.Estimate(-1L));
        Assert.Equal(10m, PromptMonitoringPricing.UnitPrice);
    }
}
