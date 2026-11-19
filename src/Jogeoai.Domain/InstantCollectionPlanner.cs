namespace Jogeoai.Domain;

/// <summary>一条提问的立即采集请求。</summary>
/// <param name="PromptId">提问标识。</param>
/// <param name="TargetKeys">只采这些目标键（仍与提问当前的有效单元格取交集）；为空（null）表示该提问的全部有效单元格。</param>
public sealed record InstantCollectionRequest(Guid PromptId, IReadOnlyList<string>? TargetKeys = null);

/// <summary>
/// 立即采集的集合运算（纯函数，不连库）：新建提问取全部有效单元格，平台范围变更只取新增的有效单元格，
/// 再剔除当天已有监控检测的「提问 × 单元格」，最后生成运行的提问、目标与逐条提问的目标键。
/// 单元格 = 品牌已启用且可参与每日监控的目标（不做采集频率过滤）。
/// </summary>
public static class InstantCollectionPlanner
{
    private const string CustomScope = "custom";

    /// <summary>
    /// 提问的有效单元格：custom 提问取所选目标键 ∩ 已启用单元格，all 提问取全部已启用单元格；结果保持 <paramref name="enabledKeys"/> 的顺序。
    /// </summary>
    /// <param name="prompt">提问当前视图。</param>
    /// <param name="enabledKeys">品牌已启用且可监控的单元格目标键。</param>
    public static IReadOnlyList<string> EffectiveKeys(PromptView prompt, IReadOnlyList<string> enabledKeys)
    {
        if (!string.Equals(prompt.TargetScope, CustomScope, StringComparison.Ordinal)) return enabledKeys.ToArray();
        var selected = (prompt.TargetKeys ?? []).ToHashSet(StringComparer.Ordinal);
        return enabledKeys.Where(selected.Contains).ToArray();
    }

    /// <summary>
    /// 平台范围变更后新增的有效单元格：有效(after) − 有效(before)，按序数排序。保存前或保存后的提问不是启用、未归档状态时返回空。
    /// </summary>
    /// <param name="before">保存前的提问。</param>
    /// <param name="after">保存后的提问。</param>
    /// <param name="enabledKeys">品牌当前已启用且可监控的单元格目标键（两边使用同一份）。</param>
    public static IReadOnlyList<string> AddedTargetKeys(PromptView before, PromptView after, IReadOnlyList<string> enabledKeys)
    {
        if (!IsActive(before) || !IsActive(after) || before.PromptId != after.PromptId) return [];
        var old = EffectiveKeys(before, enabledKeys).ToHashSet(StringComparer.Ordinal);
        return EffectiveKeys(after, enabledKeys).Where(k => !old.Contains(k)).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// 本次要采的「提问 × 单元格」：请求中的提问已不是启用状态（不在 <paramref name="enabledPrompts"/> 中）时丢弃；
    /// 每条提问取它当前的有效单元格（平台范围 ∩ <paramref name="targets"/>），请求带目标键时再与之取交集。按提问标识、目标键排序。
    /// </summary>
    /// <param name="requests">立即采集请求。</param>
    /// <param name="enabledPrompts">品牌当前启用、未归档的提问及其平台范围。</param>
    /// <param name="targets">品牌当前已启用且可监控的单元格。</param>
    public static IReadOnlyList<PromptTargetPair> Plan(IReadOnlyList<InstantCollectionRequest> requests,
        IReadOnlyList<MonitoringPromptScope> enabledPrompts, IReadOnlyList<CloroVisibilityTarget> targets)
    {
        var scopes = enabledPrompts.GroupBy(p => p.PromptId).ToDictionary(g => g.Key, g => g.First());
        var keys = targets.Select(CollectionIdentity.TargetKey).Distinct(StringComparer.Ordinal).ToArray();
        var pairs = new HashSet<PromptTargetPair>();
        foreach (var request in requests)
        {
            if (request is null || !scopes.TryGetValue(request.PromptId, out var scope)) continue;
            var allowed = scope.TargetKeys?.ToHashSet(StringComparer.Ordinal);
            var wanted = request.TargetKeys?.ToHashSet(StringComparer.Ordinal);
            foreach (var key in keys)
                if ((allowed is null || allowed.Contains(key)) && (wanted is null || wanted.Contains(key)))
                    pairs.Add(new(request.PromptId, key));
        }
        return Sort(pairs);
    }

    /// <summary>从要采的组合中去掉当天已有监控检测（排队、进行中或成功）的组合。</summary>
    /// <param name="pairs">要采的「提问 × 单元格」。</param>
    /// <param name="collected">当天已采集的「提问 × 目标键」。</param>
    public static IReadOnlyList<PromptTargetPair> RemoveCollected(IReadOnlyList<PromptTargetPair> pairs, IEnumerable<PromptTargetPair> collected)
    {
        var done = collected.ToHashSet();
        return Sort(pairs.Where(p => !done.Contains(p)).Distinct());
    }

    /// <summary>
    /// 由要采的组合生成运行选择：提问按标识排序，Targets 为用到的单元格（保持 <paramref name="targets"/> 的顺序）。
    /// 每条提问都取全部这些单元格时 PromptTargets 为空（全笛卡尔积），否则逐条提问列出目标键。
    /// </summary>
    /// <param name="pairs">要采的「提问 × 单元格」，目标键必须属于 <paramref name="targets"/>。</param>
    /// <param name="targets">品牌当前已启用且可监控的单元格。</param>
    /// <param name="sampleCount">采样数（主计划快照）。</param>
    public static CollectionScheduleSelection BuildRun(IReadOnlyList<PromptTargetPair> pairs, IReadOnlyList<CloroVisibilityTarget> targets,
        int sampleCount)
    {
        var scopes = pairs.GroupBy(p => p.PromptId)
            .Select(g => new MonitoringPromptScope(g.Key, g.Select(p => p.TargetKey).Distinct(StringComparer.Ordinal).ToArray()))
            .ToArray();
        var selection = MonitoringMatrix.BuildSelection(scopes, targets, sampleCount);
        return selection.PromptTargets is { Count: > 0 } custom && custom.All(p => p.TargetKeys.Count == selection.Targets.Count)
            ? selection with { PromptTargets = null }
            : selection;
    }

    private static bool IsActive(PromptView prompt) => prompt.Enabled && prompt.ArchivedAt is null;

    private static PromptTargetPair[] Sort(IEnumerable<PromptTargetPair> pairs)
        => pairs.OrderBy(p => p.PromptId).ThenBy(p => p.TargetKey, StringComparer.Ordinal).ToArray();
}
