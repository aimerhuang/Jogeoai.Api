namespace Jogeoai.Domain;

/// <summary>一条提问的平台范围输入。</summary>
/// <param name="PromptId">提问标识。</param>
/// <param name="TargetKeys">自定义平台范围选中的目标键；为空（null）表示 all，即品牌全部启用且可监控的目标。空列表表示自定义但未选中任何目标。</param>
public sealed record MonitoringPromptScope(Guid PromptId, IReadOnlyList<string>? TargetKeys = null);

/// <summary>每日监控的一个检测对（提问 × 目标）；每个检测对产生采样数个检测。</summary>
/// <param name="PromptId">提问标识。</param>
/// <param name="TargetIndex">目标在输入目标列表中的位置。</param>
/// <param name="TargetKey">目标键。</param>
public sealed record MonitoringCheckPair(Guid PromptId, int TargetIndex, string TargetKey);

/// <summary>一个「提问 × 目标键」组合，与目标在某个列表中的位置无关；用于立即采集与当天去重。</summary>
/// <param name="PromptId">提问标识。</param>
/// <param name="TargetKey">采集目标键（<see cref="CollectionIdentity.TargetKey(CloroVisibilityTarget)"/>）。</param>
public sealed record PromptTargetPair(Guid PromptId, string TargetKey);

/// <summary>
/// 每日监控检测矩阵的纯函数。检测数 = Σ 提问 |有效目标| × 采样数：all 提问取全部目标，
/// custom 提问取其范围 ∩ 当前目标（按 <see cref="CollectionIdentity.TargetKey(CloroVisibilityTarget)"/> 匹配），交集为空的提问不产生检测，
/// 也不回退为全部。计划、运行展开、预留与账单都使用这里的结果，保证预估与实际预留一致。
/// </summary>
public static class MonitoringMatrix
{
    /// <summary>每个品牌自动监控最多的问题数。</summary>
    public const int MaxPrompts = 200;
    /// <summary>每轮最多的目标数。</summary>
    public const int MaxTargets = 20;
    /// <summary>每轮最多的检测数。</summary>
    public const int MaxChecks = 1000;

    /// <summary>按提问平台范围展开检测对；顺序为提问输入顺序 × 目标输入顺序。</summary>
    public static IReadOnlyList<MonitoringCheckPair> Expand(IReadOnlyList<MonitoringPromptScope> prompts,
        IReadOnlyList<CloroVisibilityTarget> targets)
    {
        var keys = targets.Select(CollectionIdentity.TargetKey).ToArray();
        var pairs = new List<MonitoringCheckPair>();
        foreach (var prompt in prompts)
        {
            var allowed = prompt.TargetKeys is null ? null : prompt.TargetKeys.ToHashSet(StringComparer.Ordinal);
            for (var i = 0; i < keys.Length; i++)
                if (allowed is null || allowed.Contains(keys[i])) pairs.Add(new(prompt.PromptId, i, keys[i]));
        }
        return pairs;
    }

    /// <summary>按冻结选择展开检测对：不在 <paramref name="promptTargets"/> 中的提问取全部目标；顺序为提问输入顺序 × 目标输入顺序。</summary>
    public static IReadOnlyList<MonitoringCheckPair> Expand(IReadOnlyList<Guid> promptIds, IReadOnlyList<CloroVisibilityTarget> targets,
        IReadOnlyList<CollectionPromptTargets>? promptTargets)
    {
        var scopes = ScopeMap(promptTargets);
        return Expand(promptIds.Select(id => new MonitoringPromptScope(id, scopes?.GetValueOrDefault(id))).ToArray(), targets);
    }

    /// <summary>按提问平台范围计算一轮检测数。</summary>
    public static long CountChecks(IReadOnlyList<MonitoringPromptScope> prompts, IReadOnlyList<CloroVisibilityTarget> targets, int sampleCount)
        => (long)Expand(prompts, targets).Count * Math.Max(0, sampleCount);

    /// <summary>按冻结选择计算一轮检测数；没有自定义范围时等于问题数 × 目标数 × 采样数。</summary>
    public static long CountChecks(CollectionScheduleSelection selection)
        => selection.PromptTargets is not { Count: > 0 }
            ? (long)selection.PromptIds.Count * selection.Targets.Count * Math.Max(0, selection.SampleCount)
            : (long)Expand(selection.PromptIds, selection.Targets, selection.PromptTargets).Count * Math.Max(0, selection.SampleCount);

    /// <summary>
    /// 由提问平台范围和本轮目标生成冻结选择。全部为 all 时与原来的全笛卡尔积选择完全相同（不带 PromptTargets）。
    /// 存在 custom 提问时：交集为空的提问被剔除；Targets 为各提问有效目标的并集（保持输入顺序）；PromptTargets 只列 custom 提问。
    /// 没有任何提问留下时保留全部目标，交由规模校验判定为空选择。
    /// </summary>
    public static CollectionScheduleSelection BuildSelection(IReadOnlyList<MonitoringPromptScope> prompts,
        IReadOnlyList<CloroVisibilityTarget> targets, int sampleCount)
    {
        var ordered = prompts.OrderBy(p => p.PromptId).ToArray();
        if (ordered.All(p => p.TargetKeys is null))
            return new(ordered.Select(p => p.PromptId).ToArray(), targets.ToArray(), sampleCount);
        var pairs = Expand(ordered, targets);
        var covered = pairs.Select(p => p.PromptId).ToHashSet();
        var promptIds = ordered.Where(p => covered.Contains(p.PromptId)).Select(p => p.PromptId).ToArray();
        var custom = ordered.Where(p => p.TargetKeys is not null).Select(p => p.PromptId).ToHashSet();
        var promptTargets = pairs.Where(p => custom.Contains(p.PromptId))
            .GroupBy(p => p.PromptId)
            .Select(g => new CollectionPromptTargets(g.Key, g.Select(p => p.TargetKey).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray()))
            .OrderBy(p => p.PromptId).ToArray();
        var used = pairs.Select(p => p.TargetIndex).ToHashSet();
        var union = promptIds.Length == 0 ? targets.ToArray() : targets.Where((_, i) => used.Contains(i)).ToArray();
        return new(promptIds, union, sampleCount, promptTargets.Length == 0 ? null : promptTargets);
    }

    /// <summary>
    /// 从冻结选择中去掉指定的「提问 × 目标键」（例如当天已立即采集过的组合）。只有被去掉目标的提问改为剩余目标的自定义范围，
    /// 其余提问保持原范围（all 仍为 all）；没有剩余目标的提问被剔除，再按 <see cref="BuildSelection"/> 重建。
    /// 没有任何组合命中时原样返回同一个选择，冻结快照与选择哈希逐字节不变。
    /// </summary>
    /// <param name="selection">冻结选择。</param>
    /// <param name="pairs">要去掉的「提问 × 目标键」。</param>
    public static CollectionScheduleSelection Without(CollectionScheduleSelection selection, IEnumerable<PromptTargetPair> pairs)
    {
        var removed = pairs.GroupBy(p => p.PromptId)
            .ToDictionary(g => g.Key, g => g.Select(p => p.TargetKey).ToHashSet(StringComparer.Ordinal));
        if (removed.Count == 0) return selection;
        var scopes = ScopeMap(selection.PromptTargets);
        var keys = selection.Targets.Select(CollectionIdentity.TargetKey).ToArray();
        var affected = false;
        var prompts = new List<MonitoringPromptScope>(selection.PromptIds.Count);
        foreach (var id in selection.PromptIds)
        {
            var scope = scopes?.GetValueOrDefault(id);
            if (removed.TryGetValue(id, out var gone))
            {
                var allowed = scope?.ToHashSet(StringComparer.Ordinal);
                var effective = keys.Where(k => allowed is null || allowed.Contains(k)).ToArray();
                if (effective.Any(gone.Contains))
                {
                    affected = true;
                    prompts.Add(new(id, effective.Where(k => !gone.Contains(k)).ToArray()));
                    continue;
                }
            }
            prompts.Add(new(id, scope));
        }
        return affected ? BuildSelection(prompts, selection.Targets, selection.SampleCount) : selection;
    }

    /// <summary>
    /// 校验并规范化冻结的自定义平台范围：每个提问必须在 <paramref name="promptIds"/> 中且不重复，目标键非空、
    /// 都属于 <paramref name="targets"/>。规范化后按提问与目标键排序；空输入返回 null（全笛卡尔积）。
    /// </summary>
    public static bool TryNormalizePromptTargets(IReadOnlyList<CollectionPromptTargets>? promptTargets, IReadOnlyList<Guid> promptIds,
        IReadOnlyList<CloroVisibilityTarget> targets, out IReadOnlyList<CollectionPromptTargets>? normalized)
    {
        normalized = null;
        if (promptTargets is not { Count: > 0 }) return true;
        var prompts = promptIds.ToHashSet();
        var keys = targets.Select(CollectionIdentity.TargetKey).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<Guid>();
        var result = new List<CollectionPromptTargets>();
        foreach (var entry in promptTargets)
        {
            if (entry is null || !prompts.Contains(entry.PromptId) || !seen.Add(entry.PromptId)
                || entry.TargetKeys is not { Count: > 0 } || entry.TargetKeys.Any(key => key is null || !keys.Contains(key))
                || entry.TargetKeys.Distinct(StringComparer.Ordinal).Count() != entry.TargetKeys.Count)
                return false;
            result.Add(new(entry.PromptId, entry.TargetKeys.Order(StringComparer.Ordinal).ToArray()));
        }
        normalized = result.OrderBy(p => p.PromptId).ToArray();
        return true;
    }

    private static Dictionary<Guid, IReadOnlyList<string>>? ScopeMap(IReadOnlyList<CollectionPromptTargets>? promptTargets)
        => promptTargets is not { Count: > 0 } ? null
            : promptTargets.Where(p => p is not null).GroupBy(p => p.PromptId)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.SelectMany(p => p.TargetKeys ?? []).Distinct(StringComparer.Ordinal).ToArray());
}
