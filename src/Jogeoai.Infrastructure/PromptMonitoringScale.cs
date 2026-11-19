using System.Globalization;
using System.Text.Json;
using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

/// <summary>
/// 新增提问前的每日监控规模口径：与 <see cref="AutomaticMonitoringSelection"/> 相同的启用提问（含平台范围）、
/// 启用且可监控的目标和计划采样数，经 <see cref="MonitoringMatrix"/> 计数。批量创建的 200 / 1000 校验与预估接口共用这里，
/// 保证预估、创建拦截与下一轮实际预留一致。只读，不加锁；调用方负责事务与锁顺序。
/// </summary>
internal static class PromptMonitoringScale
{
    internal const string TargetInvalidCode = "PROMPT_TARGET_INVALID";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>品牌当前的监控规模输入。</summary>
    /// <param name="Prompts">启用、未归档提问及其平台范围（最多读取 201 条，超过 200 已由 <paramref name="EnabledPromptCount"/> 判定）。</param>
    /// <param name="EnabledPromptCount">品牌启用且未归档的提问数。</param>
    /// <param name="Targets">启用且可参与每日监控的目标，按目标键排序（与计划冻结顺序一致）。</param>
    /// <param name="SampleCount">自动监控计划的采样数；尚无计划时为首轮默认值 1。</param>
    internal sealed record Snapshot(IReadOnlyList<MonitoringPromptScope> Prompts, int EnabledPromptCount,
        IReadOnlyList<CloroVisibilityTarget> Targets, int SampleCount);

    internal static async Task<IReadOnlyList<CloroVisibilityTarget>> EligibleTargetsAsync(MySqlConnection connection,
        MySqlTransaction transaction, Guid tenantId, Guid siteId, CancellationToken ct)
        => (await AutomaticMonitoringSelection.Targets(connection, transaction, tenantId, siteId, false, ct))
            .Select(t => t.Value).OrderBy(CollectionIdentity.TargetKey, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// 校验提问平台范围：1–20 个不重复的目标键，且都是品牌当前启用、可监控的目标；返回按序数排序的副本。
    /// 不满足时抛 400 PROMPT_TARGET_INVALID。空列表视为无效（恢复 all 由调用方单独处理）。
    /// <paramref name="appOpen"/> 为 true（租户已开放 App 渠道）时，范围还可以包含该品牌已启用的 App 目标键（总 PRD FR-C2）；
    /// App 目标键只被存下来，每日监控的计划、预估和计费按目标键取交集时不会用到它们。
    /// </summary>
    internal static async Task<IReadOnlyList<string>> ValidateTargetKeysAsync(MySqlConnection connection, MySqlTransaction transaction,
        Guid tenantId, Guid siteId, IReadOnlyList<string> keys, CancellationToken ct, bool appOpen = false)
    {
        CheckShape(keys);
        var eligible = (await EligibleTargetsAsync(connection, transaction, tenantId, siteId, ct))
            .Select(CollectionIdentity.TargetKey).ToHashSet(StringComparer.Ordinal);
        // App target keys are only read when a key is not an API target, so a scope made of API keys runs no extra query.
        if (appOpen && keys.Any(key => !eligible.Contains(key)))
            eligible.UnionWith(await EnabledAppTargetKeysAsync(connection, transaction, tenantId, siteId, ct));
        return ValidateTargetKeys(keys, eligible);
    }

    // Stored keys of the brand's enabled App targets (site_targets.target_key = CollectionIdentity.AppTargetKey(engine)).
    private static async Task<IReadOnlyList<string>> EnabledAppTargetKeysAsync(MySqlConnection connection, MySqlTransaction transaction,
        Guid tenantId, Guid siteId, CancellationToken ct)
    {
        await using var query = new MySqlCommand("""
            SELECT target_key FROM site_targets
            WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site) AND provider = @provider AND enabled = TRUE;
            """, connection, transaction);
        query.Parameters.AddWithValue("@tenant", tenantId.ToString());
        query.Parameters.AddWithValue("@site", siteId.ToString());
        query.Parameters.AddWithValue("@provider", AppChannelDefinition.Provider);
        var keys = new List<string>();
        await using var reader = await query.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) keys.Add(reader.GetString(0));
        return keys;
    }

    /// <summary><see cref="ValidateTargetKeysAsync"/> 的纯函数部分：按给定的可用目标键集合校验并排序。</summary>
    internal static IReadOnlyList<string> ValidateTargetKeys(IReadOnlyList<string> keys, IReadOnlySet<string> eligible)
    {
        CheckShape(keys);
        if (keys.Any(key => !eligible.Contains(key)))
            throw Invalid("所选 AI 平台须为品牌当前启用且可用于每日监控的平台。");
        return keys.Order(StringComparer.Ordinal).ToArray();
    }

    // A scope may name up to 20 API targets plus one App target per platform (App targets do not take any of the 20).
    internal static int MaxScopeTargetKeys => MonitoringMatrix.MaxTargets + AppChannelDefinition.Platforms.Count;

    private static void CheckShape(IReadOnlyList<string> keys)
    {
        if (keys is null || keys.Count < 1 || keys.Count > MaxScopeTargetKeys || keys.Any(string.IsNullOrWhiteSpace)
            || keys.Distinct(StringComparer.Ordinal).Count() != keys.Count)
            throw Invalid("AI 平台范围必须为 1–20 个不重复的平台。");
    }

    internal static async Task<Snapshot> ReadAsync(MySqlConnection connection, MySqlTransaction transaction,
        Guid tenantId, Guid siteId, CancellationToken ct)
    {
        var prompts = await AutomaticMonitoringSelection.Prompts(connection, transaction, tenantId, siteId, ct);
        await using var count = new MySqlCommand("""
            SELECT COUNT(*) FROM prompts p JOIN sites s ON s.id = p.site_id
            WHERE s.tenant_id = UUID_TO_BIN(@tenant) AND p.site_id = UUID_TO_BIN(@site)
              AND p.enabled = TRUE AND p.archived_at IS NULL;
            """, connection, transaction);
        count.Parameters.AddWithValue("@tenant", tenantId.ToString());
        count.Parameters.AddWithValue("@site", siteId.ToString());
        var enabled = Convert.ToInt32(await count.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        var targets = await EligibleTargetsAsync(connection, transaction, tenantId, siteId, ct);
        await using var plan = new MySqlCommand("""
            SELECT selection_snapshot FROM collection_schedules
            WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site) AND name = @name;
            """, connection, transaction);
        plan.Parameters.AddWithValue("@tenant", tenantId.ToString());
        plan.Parameters.AddWithValue("@site", siteId.ToString());
        plan.Parameters.AddWithValue("@name", PromptMonitoringIdentity.ScheduleName);
        var snapshot = await plan.ExecuteScalarAsync(ct) as string;
        // PromptMonitoringService creates the plan with one sample; reconcile keeps the plan's own sample count afterwards.
        var sampleCount = snapshot is null ? 1 : JsonSerializer.Deserialize<CollectionScheduleSelection>(snapshot, Json)?.SampleCount ?? 1;
        return new(prompts, enabled, targets, sampleCount);
    }

    /// <summary>当前提问加上 <paramref name="added"/> 条使用 <paramref name="scope"/>（null 为 all）的假想新提问。</summary>
    internal static IReadOnlyList<MonitoringPromptScope> WithAdded(Snapshot snapshot, int added, IReadOnlyList<string>? scope)
        => [.. snapshot.Prompts, .. Enumerable.Range(0, Math.Max(0, added)).Select(_ => new MonitoringPromptScope(Guid.NewGuid(), scope))];

    /// <summary>新增后的规模是否超过每日监控上限（200 个问题、20 个目标或每轮 1000 次检测）。</summary>
    internal static bool TooLarge(Snapshot snapshot, int added, IReadOnlyList<string>? scope)
    {
        if (snapshot.EnabledPromptCount + added > MonitoringMatrix.MaxPrompts) return true;
        var prompts = WithAdded(snapshot, added, scope);
        if (MonitoringMatrix.CountChecks(prompts, snapshot.Targets, snapshot.SampleCount) > MonitoringMatrix.MaxChecks) return true;
        return MonitoringMatrix.BuildSelection(prompts, snapshot.Targets, snapshot.SampleCount).Targets.Count > MonitoringMatrix.MaxTargets;
    }

    /// <summary>批量创建前调用：新增 <paramref name="added"/> 条启用提问后超过每日监控规模时抛 400 MONITORING_TOO_LARGE。</summary>
    internal static async Task EnsureCapacityAsync(MySqlConnection connection, MySqlTransaction transaction, Guid tenantId, Guid siteId,
        int added, IReadOnlyList<string>? scope, CancellationToken ct)
    {
        if (added <= 0) return;
        var snapshot = await ReadAsync(connection, transaction, tenantId, siteId, ct);
        if (snapshot.EnabledPromptCount + added > MonitoringMatrix.MaxPrompts)
            throw new CollectorStoreException("MONITORING_TOO_LARGE",
                $"自动监控每个品牌最多支持 {MonitoringMatrix.MaxPrompts} 个问题；当前 {snapshot.EnabledPromptCount} 个，本次新增 {added} 个。", 400);
        if (TooLarge(snapshot, added, scope))
            throw new CollectorStoreException("MONITORING_TOO_LARGE",
                $"创建后每轮检测将超过 {MonitoringMatrix.MaxChecks} 次（或目标超过 {MonitoringMatrix.MaxTargets} 个），请减少问题或平台。", 400);
    }

    /// <summary>
    /// 预估接口的纯计算：当前与新增后的每轮检测、积分、差额和阻断项。<paramref name="scope"/> 为 null 表示新增提问使用 all，
    /// 空列表表示没有选中平台（新增部分为 0 次检测，并报告 MONITORING_TARGETS_REQUIRED）。余额不足只体现在 Shortfall。
    /// <paramref name="appTargetCount"/> 为品牌已启用的 App 目标数（租户未开放 App 渠道时为 0）：没有 API 目标但有 App 目标的品牌
    /// 不报告 MONITORING_TARGETS_REQUIRED（总 PRD KD-24）；检测数与积分仍只算 API。
    /// </summary>
    internal static PromptMonitoringEstimateView Estimate(Snapshot snapshot, int added, IReadOnlyList<string>? scope,
        decimal availableCredits, int? planLimit, int planCurrent, int appTargetCount = 0)
    {
        var current = MonitoringMatrix.CountChecks(snapshot.Prompts, snapshot.Targets, snapshot.SampleCount);
        var total = MonitoringMatrix.CountChecks(WithAdded(snapshot, added, scope), snapshot.Targets, snapshot.SampleCount);
        var totalCredits = PromptMonitoringPricing.Estimate(total);
        var blockers = new List<string>();
        if (added > 0 && TooLarge(snapshot, added, scope)) blockers.Add("MONITORING_TOO_LARGE");
        if (added > 0 && planLimit is { } limit && planCurrent + added > limit) blockers.Add("PLAN_LIMIT_EXCEEDED");
        if (snapshot.Targets.Count == 0 && appTargetCount == 0 || added > 0 && scope is { Count: 0 }) blockers.Add("MONITORING_TARGETS_REQUIRED");
        return new PromptMonitoringEstimateView(PromptMonitoringPricing.UnitPrice, snapshot.SampleCount,
            snapshot.EnabledPromptCount, current, PromptMonitoringPricing.Estimate(current),
            total - current, PromptMonitoringPricing.Estimate(total - current),
            total, totalCredits, availableCredits, Math.Max(0m, totalCredits - availableCredits), blockers);
    }

    /// <summary>目标预估中的一个已启用可监控单元格；<paramref name="Anchor"/> 为保存后将生效的频率锚点。</summary>
    internal sealed record EstimateCell(CloroVisibilityTarget Target, string Key, string Engine, AnswerMode Mode,
        MonitoringFrequency Frequency, DateOnly? Anchor);

    /// <summary>
    /// 把草稿中已启用、可监控的目标投影为单元格，并按 PUT 的锚点规则推算锚点：目标原本未启用（或不存在）时为今天；
    /// 已启用且频率改为每2天1次 / 1次/周时为今天；否则保留现有锚点。
    /// </summary>
    internal static IReadOnlyList<EstimateCell> ProjectCells(IReadOnlyList<ManagementStore.NormalizedSiteTarget> drafts,
        IReadOnlyList<AutomaticMonitoringSelection.Target> enabledTargets, DateOnly today)
    {
        var existing = new Dictionary<string, AutomaticMonitoringSelection.Target>(StringComparer.Ordinal);
        foreach (var target in enabledTargets) existing.TryAdd(CollectionIdentity.TargetKey(target.Value), target);
        var cells = new List<EstimateCell>();
        foreach (var draft in drafts.Where(d => d.Eligible && d.Write.Enabled))
        {
            var write = draft.Write;
            var definition = AnswerProviderDefinition.TryGet(write.Provider)!;
            var target = AnswerModeRules.Normalize(new CloroVisibilityTarget(write.Provider, write.Engine, write.Locale, write.Country, "{}", draft.StoredMode));
            var key = CollectionIdentity.TargetKey(target);
            DateOnly? anchor = today;
            if (existing.TryGetValue(key, out var current)
                && !(write.Frequency is MonitoringFrequency.EveryOtherDay or MonitoringFrequency.Weekly && write.Frequency != current.Frequency))
                anchor = current.Anchor;
            cells.Add(new(target, key, write.Engine, write.AnswerMode ?? definition.DefaultAnswerMode, write.Frequency, anchor));
        }
        return cells.OrderBy(c => c.Key, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// 目标预估的纯计算：每周检测 = Σ 提问 × 有效单元格（提问平台范围取交集）× 采样数 × 每周轮数；下一轮取品牌时区下一个 09:01
    /// 与（存在 2次/天 单元格时）下一个 00:05 中较近者，只计到期单元格。Shortfall 按下一轮积分计算，只提示，不阻断。
    /// <paramref name="appTargets"/> 为草稿中已启用的 App 目标（租户未开放 App 渠道时没有）：它们不进入任何数字，只影响
    /// MONITORING_TARGETS_REQUIRED——草稿里有 App 单元格、且没有提问或至少一条提问的平台范围与它们有交集（即 App 渠道会采集）时
    /// 不报告（总 PRD KD-24）。没有 App 目标时该阻断项的判定与原来完全相同。
    /// </summary>
    internal static MonitoringTargetsEstimateView EstimateTargets(Snapshot snapshot, IReadOnlyList<EstimateCell> cells,
        IReadOnlySet<string> unsupportedKeys, DateTimeOffset nowUtc, TimeZoneInfo zone, decimal availableCredits,
        IReadOnlyList<CloroVisibilityTarget>? appTargets = null)
    {
        var sample = snapshot.SampleCount;
        var targets = cells.Select(c => c.Target).ToArray();
        var pairs = MonitoringMatrix.Expand(snapshot.Prompts, targets);
        var perKey = pairs.GroupBy(p => p.TargetKey, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var cellViews = cells.Select(c => new MonitoringTargetCellEstimate(c.Key, c.Engine, c.Mode, c.Frequency,
            MonitoringFrequencyRules.WeeklyRuns(c.Frequency), perKey.GetValueOrDefault(c.Key))).ToArray();
        var weeklyChecks = cellViews.Sum(c => c.PromptCount * (decimal)Math.Max(0, sample) * c.WeeklyRuns);
        var main = CollectionScheduleCalendar.NextAfter("daily", null,
            CollectionScheduleCalendar.ParseLocalTime(PromptMonitoringIdentity.MainLocalTime), zone, nowUtc);
        var (kind, slotAt) = (MonitoringSlotKind.Main, main);
        if (cells.Any(c => c.Frequency == MonitoringFrequency.TwiceDaily))
        {
            var night = CollectionScheduleCalendar.NextAfter("daily", null,
                CollectionScheduleCalendar.ParseLocalTime(PromptMonitoringIdentity.NightLocalTime), zone, nowUtc);
            if (night < main) (kind, slotAt) = (MonitoringSlotKind.Night, night);
        }
        var slotDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(slotAt, zone).DateTime);
        var due = cells.Where(c => MonitoringFrequencyRules.IsDue(c.Frequency, c.Anchor, slotDate, kind)).Select(c => c.Target).ToArray();
        var nextChecks = MonitoringMatrix.CountChecks(snapshot.Prompts, due, sample);
        var nextCredits = PromptMonitoringPricing.Estimate(nextChecks);
        var blockers = new List<string>();
        // Any slot may have every cell due, so the round limits use the full matrix.
        if (cells.Count > MonitoringMatrix.MaxTargets || snapshot.EnabledPromptCount > MonitoringMatrix.MaxPrompts
            || (long)pairs.Count * sample > MonitoringMatrix.MaxChecks)
            blockers.Add("MONITORING_TOO_LARGE");
        // "Nothing would be collected": no API cell (or no prompt on one) — unless the draft's App cells would be collected.
        var appCollects = appTargets is { Count: > 0 }
            && (snapshot.Prompts.Count == 0 || MonitoringMatrix.Expand(snapshot.Prompts, appTargets).Count > 0);
        if (!appCollects && (cells.Count == 0 || snapshot.Prompts.Count > 0 && pairs.Count == 0)) blockers.Add("MONITORING_TARGETS_REQUIRED");
        if (cells.Any(c => unsupportedKeys.Contains(c.Key))) blockers.Add(AnswerModeRules.AnswerModeUnsupported);
        return new MonitoringTargetsEstimateView(snapshot.EnabledPromptCount, weeklyChecks, weeklyChecks * PromptMonitoringPricing.UnitPrice,
            nextChecks, nextCredits, cellViews, availableCredits, Math.Max(0m, nextCredits - availableCredits), blockers,
            PromptMonitoringPricing.UnitPrice, sample, slotAt, kind);
    }

    private static CollectorStoreException Invalid(string message) => new(TargetInvalidCode, message, 400);
}
