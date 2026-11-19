using Jogeoai.Domain;

namespace Jogeoai.Tests;

/// <summary>
/// 立即采集的集合运算（不连库）：新建提问取全部有效单元格，新增模型只取差集，当天已采集的组合被剔除，
/// 停用提问被丢弃，运行选择与幂等键稳定且与顺序无关。
/// </summary>
public sealed class InstantCollectionPlannerTests
{
    private static readonly CloroVisibilityTarget ChatGpt = new("cloro", "chatgpt", "zh-CN", "US", "{}");
    private static readonly CloroVisibilityTarget Gemini = new("cloro", "gemini", "zh-CN", "US", "{}");
    private static readonly CloroVisibilityTarget DeepSeekFast = new("deepseek_api", "deepseek", "zh-CN", "CN", "{}");
    private static readonly CloroVisibilityTarget DeepSeekDeep = DeepSeekFast with { AnswerMode = "deep" };
    private static readonly CloroVisibilityTarget[] Targets =
        new[] { ChatGpt, Gemini, DeepSeekFast, DeepSeekDeep }.OrderBy(CollectionIdentity.TargetKey, StringComparer.Ordinal).ToArray();
    private static readonly string[] Keys = Targets.Select(CollectionIdentity.TargetKey).ToArray();
    private static string Key(CloroVisibilityTarget target) => CollectionIdentity.TargetKey(target);
    private static Guid Id(int n) => Guid.Parse($"00000000-0000-4000-8000-{n:D12}");

    private static PromptView Prompt(Guid id, string scope = "all", IReadOnlyList<string>? keys = null, bool enabled = true,
        DateTimeOffset? archivedAt = null)
        => new(id, "问题", 1, "zh-CN", enabled, [], "unbranded", "system", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            archivedAt, 1, null, scope, keys);

    private static PromptTargetPair Pair(int prompt, CloroVisibilityTarget target) => new(Id(prompt), Key(target));

    [Fact]
    public void NewPromptsTakeEveryEffectiveCellOfTheirScope()
    {
        MonitoringPromptScope[] enabled = [new(Id(1)), new(Id(2), [Key(Gemini), Key(DeepSeekDeep), "f".PadLeft(64, 'f')]), new(Id(3))];
        var pairs = InstantCollectionPlanner.Plan([new(Id(2)), new(Id(1))], enabled, Targets);
        // Id(1) all → every cell (fast and deep are separate cells); Id(2) custom → its keys ∩ enabled cells; Id(3) not requested.
        var expected = Keys.Select(k => new PromptTargetPair(Id(1), k))
            .Concat(new[] { Gemini, DeepSeekDeep }.Select(t => Pair(2, t)))
            .OrderBy(p => p.PromptId).ThenBy(p => p.TargetKey, StringComparer.Ordinal);
        Assert.Equal(expected, pairs);
    }

    [Fact]
    public void PromptsNoLongerEnabledOrWithoutEffectiveCellsAreDropped()
    {
        MonitoringPromptScope[] enabled = [new(Id(1)), new(Id(2), [Key(new CloroVisibilityTarget("cloro", "copilot", "zh-CN", "US"))]), new(Id(3), [])];
        // Id(4) is not in the enabled list (disabled or archived after the write): dropped, never widened.
        Assert.Empty(InstantCollectionPlanner.Plan([new(Id(2)), new(Id(3)), new(Id(4))], enabled, Targets));
        Assert.Equal(Keys.Length, InstantCollectionPlanner.Plan([new(Id(1)), new(Id(4)), new(Id(1))], enabled, Targets).Count);
        Assert.Empty(InstantCollectionPlanner.Plan([new(Id(1))], enabled, []));
    }

    [Fact]
    public void RequestedKeysAreIntersectedWithTheCurrentEffectiveCells()
    {
        MonitoringPromptScope[] enabled = [new(Id(1), [Key(ChatGpt), Key(Gemini)])];
        // Gemini is requested and still in scope; DeepSeek deep is requested but no longer in the prompt's scope.
        Assert.Equal([Pair(1, Gemini)], InstantCollectionPlanner.Plan([new(Id(1), [Key(Gemini), Key(DeepSeekDeep)])], enabled, Targets));
        // A requested key whose cell was disabled meanwhile is not collected.
        Assert.Empty(InstantCollectionPlanner.Plan([new(Id(1), [Key(Gemini)])], enabled, [ChatGpt]));
    }

    [Fact]
    public void AddedTargetsCustomToCustom()
    {
        var before = Prompt(Id(1), "custom", [Key(ChatGpt)]);
        var after = Prompt(Id(1), "custom", [Key(ChatGpt), Key(Gemini), Key(DeepSeekDeep)]);
        Assert.Equal(new[] { Key(Gemini), Key(DeepSeekDeep) }.Order(StringComparer.Ordinal),
            InstantCollectionPlanner.AddedTargetKeys(before, after, Keys));
    }

    [Fact]
    public void AddedTargetsCustomToAllAddsEveryOtherEnabledCell()
    {
        var before = Prompt(Id(1), "custom", [Key(ChatGpt)]);
        Assert.Equal(Keys.Where(k => k != Key(ChatGpt)).Order(StringComparer.Ordinal),
            InstantCollectionPlanner.AddedTargetKeys(before, Prompt(Id(1)), Keys));
    }

    [Fact]
    public void AddedTargetsAllToCustomOrOnlyRemovingAddsNothing()
    {
        Assert.Empty(InstantCollectionPlanner.AddedTargetKeys(Prompt(Id(1)), Prompt(Id(1), "custom", [Key(ChatGpt)]), Keys));
        Assert.Empty(InstantCollectionPlanner.AddedTargetKeys(Prompt(Id(1), "custom", [Key(ChatGpt), Key(Gemini)]),
            Prompt(Id(1), "custom", [Key(Gemini)]), Keys));
        Assert.Empty(InstantCollectionPlanner.AddedTargetKeys(Prompt(Id(1)), Prompt(Id(1)), Keys));
    }

    [Fact]
    public void AddedTargetsIgnoreDisabledCellsAndInactivePrompts()
    {
        var before = Prompt(Id(1), "custom", [Key(ChatGpt)]);
        // A previously chosen but now disabled cell is neither "before" nor "after": re-selecting it adds nothing.
        var after = Prompt(Id(1), "custom", [Key(ChatGpt), Key(DeepSeekDeep)]);
        var enabledWithoutDeep = Keys.Where(k => k != Key(DeepSeekDeep)).ToArray();
        Assert.Empty(InstantCollectionPlanner.AddedTargetKeys(before, after, enabledWithoutDeep));
        Assert.Equal([Key(DeepSeekDeep)], InstantCollectionPlanner.AddedTargetKeys(before, after, Keys));
        // Disabled or archived on either side → nothing (re-enabling a prompt is not an instant trigger).
        Assert.Empty(InstantCollectionPlanner.AddedTargetKeys(before with { Enabled = false }, after, Keys));
        Assert.Empty(InstantCollectionPlanner.AddedTargetKeys(before, after with { Enabled = false }, Keys));
        Assert.Empty(InstantCollectionPlanner.AddedTargetKeys(before, after with { ArchivedAt = DateTimeOffset.UnixEpoch }, Keys));
        Assert.Empty(InstantCollectionPlanner.AddedTargetKeys(before, Prompt(Id(2), "custom", [Key(ChatGpt), Key(Gemini)]), Keys));
    }

    [Fact]
    public void SameDayDedupRemovesAlreadyCollectedPairs()
    {
        var wanted = InstantCollectionPlanner.Plan([new(Id(1)), new(Id(2), [Key(Gemini)])], [new(Id(1)), new(Id(2))], Targets);
        var remaining = InstantCollectionPlanner.RemoveCollected(wanted, [Pair(1, ChatGpt), Pair(1, DeepSeekDeep), Pair(9, Gemini)]);
        Assert.Equal(new[] { Pair(1, Gemini), Pair(1, DeepSeekFast), Pair(2, Gemini) }
            .OrderBy(p => p.PromptId).ThenBy(p => p.TargetKey, StringComparer.Ordinal), remaining);
        Assert.Empty(InstantCollectionPlanner.RemoveCollected(remaining, remaining));
        Assert.Equal(remaining, InstantCollectionPlanner.RemoveCollected(remaining, []));
    }

    [Fact]
    public void RunSelectionIsCartesianOnlyWhenEveryPromptTakesEveryUsedCell()
    {
        // Two prompts on {ChatGPT, Gemini}: cartesian over the used cells, no per-prompt keys, unused cells left out.
        var cartesian = InstantCollectionPlanner.BuildRun([Pair(2, Gemini), Pair(1, ChatGpt), Pair(1, Gemini), Pair(2, ChatGpt)], Targets, 2);
        Assert.Equal([Id(1), Id(2)], cartesian.PromptIds);
        Assert.Equal(Targets.Where(t => t == ChatGpt || t == Gemini), cartesian.Targets);
        Assert.Null(cartesian.PromptTargets);
        Assert.Equal(8, MonitoringMatrix.CountChecks(cartesian));

        // One prompt × one added cell plus a new prompt on two cells: per-prompt keys, exactly the requested pairs.
        var scoped = InstantCollectionPlanner.BuildRun([Pair(1, DeepSeekDeep), Pair(2, ChatGpt), Pair(2, Gemini)], Targets, 1);
        Assert.Equal([Id(1), Id(2)], scoped.PromptIds);
        Assert.Equal(3, scoped.Targets.Count);
        Assert.Equal([Key(DeepSeekDeep)], scoped.PromptTargets![0].TargetKeys);
        Assert.Equal(new[] { Key(ChatGpt), Key(Gemini) }.Order(StringComparer.Ordinal), scoped.PromptTargets[1].TargetKeys);
        Assert.Equal(3, MonitoringMatrix.CountChecks(scoped));
        Assert.Equal(
            new[] { Pair(1, DeepSeekDeep), Pair(2, ChatGpt), Pair(2, Gemini) }.OrderBy(p => p.PromptId).ThenBy(p => p.TargetKey, StringComparer.Ordinal),
            MonitoringMatrix.Expand(scoped.PromptIds, scoped.Targets, scoped.PromptTargets).Select(p => new PromptTargetPair(p.PromptId, p.TargetKey))
                .OrderBy(p => p.PromptId).ThenBy(p => p.TargetKey, StringComparer.Ordinal));
    }

    [Fact]
    public void InstantRunKeyIsStableOrderIndependentAndFitsTheColumns()
    {
        var site = Guid.NewGuid();
        var day = new DateOnly(2026, 11, 11);
        PromptTargetPair[] pairs = [Pair(1, ChatGpt), Pair(2, Gemini), Pair(1, DeepSeekDeep)];
        var key = PromptMonitoringIdentity.InstantRunKey(site, day, pairs, 1);
        Assert.Equal(key, PromptMonitoringIdentity.InstantRunKey(site, day, Enumerable.Reverse(pairs), 1));
        Assert.Equal(key, PromptMonitoringIdentity.InstantRunKey(site, day, [.. pairs, pairs[0]], 1));
        Assert.StartsWith($"prompt-instant:{site:N}:20261111:", key, StringComparison.Ordinal);
        Assert.Equal(121, key.Length);
        Assert.Equal(47, PromptMonitoringIdentity.InstantRequestedBy(site).Length);
        Assert.Equal($"prompt-monitor:{site:N}", PromptMonitoringIdentity.InitialRequestedBy(site));

        Assert.NotEqual(key, PromptMonitoringIdentity.InstantRunKey(site, day.AddDays(1), pairs, 1));
        Assert.NotEqual(key, PromptMonitoringIdentity.InstantRunKey(site, day, pairs, 2));
        Assert.NotEqual(key, PromptMonitoringIdentity.InstantRunKey(site, day, pairs[..2], 1));
        Assert.NotEqual(key, PromptMonitoringIdentity.InstantRunKey(Guid.NewGuid(), day, pairs, 1));
    }

    [Fact]
    public void SameDayWindowSurvivesADstJumpAtLocalMidnight()
    {
        // Chile springs forward at 24:00, so one local midnight a year does not exist; the same-day window used by the
        // instant dedup and the 09:01 slot exclusion must start at the first valid minute instead of throwing.
        var zone = Jogeoai.Domain.CollectionScheduleCalendar.ParseTimeZone("America/Santiago");
        var day = Enumerable.Range(0, 366).Select(i => new DateOnly(2026, 1, 1).AddDays(i))
            .First(d => zone.IsInvalidTime(d.ToDateTime(TimeOnly.MinValue)));
        var start = Jogeoai.Infrastructure.AutomaticMonitoringSelection.LocalDayStartUtc(day, zone);
        Assert.Equal(TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(new TimeOnly(1, 0)), zone), start);
        Assert.Equal(DateTimeKind.Utc, start.Kind);

        var shanghai = Jogeoai.Domain.CollectionScheduleCalendar.ParseTimeZone("Asia/Shanghai");
        Assert.Equal(new DateTime(2026, 11, 10, 16, 0, 0, DateTimeKind.Utc),
            Jogeoai.Infrastructure.AutomaticMonitoringSelection.LocalDayStartUtc(new DateOnly(2026, 11, 11), shanghai));
    }
}
