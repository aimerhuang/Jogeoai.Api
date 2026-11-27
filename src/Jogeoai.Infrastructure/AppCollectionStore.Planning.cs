using System.Globalization;
using Jogeoai.Domain;
using MySqlConnector;
using static Jogeoai.Infrastructure.DomesticCollectorStore;

namespace Jogeoai.Infrastructure;

public sealed partial class AppCollectionStore
{
    /// <summary>
    /// The prompt × platform pairs one App slot would collect, and the rows needed to write them. <see cref="Skip"/> is set
    /// when nothing is left; the other members are then incomplete.
    /// </summary>
    internal sealed record PairPlan(CloroVisibilityTarget[] Targets, MonitoringCheckPair[] Pairs, Dictionary<Guid, string[]> Prompts,
        Guid[] CreationOrder, List<(Guid PromptId, string Reason)> Excluded, string[] Unavailable, Dictionary<string, Guid> Entries,
        string[]? Brand, CycleOutcome? Skip);

    /// <summary>
    /// Plans the pairs of a frozen App selection; shared by the cycle run (<see cref="CreateCycleRunAsync"/>) and by the
    /// instant-slot decision (<see cref="AppMonitoringPlan"/>), so a pair that one of them drops is dropped by the other too
    /// and an instant slot is never inserted for pairs that its expansion would skip. In order:
    /// <list type="number">
    /// <item>more than 200 prompts → MONITORING_TOO_LARGE; no prompt or no known App platform → MONITORING_SELECTION_EMPTY;
    /// no brand settings → RESOURCE_NOT_FOUND;</item>
    /// <item>prompts no longer enabled, failing <see cref="AppPromptRules"/> on their current text, or carrying an active
    /// operator exclusion produce no pair; nothing left → MONITORING_SELECTION_EMPTY;</item>
    /// <item>platforms without an enabled consumer_app entry produce no pair; nothing left → APP_PLATFORM_UNAVAILABLE;</item>
    /// <item>pairs already covered at <paramref name="coveredAtUtc"/> (<see cref="CoveredPairsAsync"/>) produce no pair;
    /// nothing left → MONITORING_ALREADY_COLLECTED.</item>
    /// </list>
    /// </summary>
    internal static async Task<PairPlan> PlanPairsAsync(MySqlConnection c, MySqlTransaction t, Guid tenantId, Guid siteId,
        CollectionScheduleSelection selection, DateTimeOffset coveredAtUtc, CancellationToken ct)
    {
        var (tenant, site) = (tenantId.ToString(), siteId.ToString());
        PairPlan Stop(CycleOutcome outcome) => new([], [], [], [], [], [], [], null, outcome);
        if (selection.PromptIds.Count > MonitoringMatrix.MaxPrompts) return Stop(Skip(AppMonitoringPlan.TooLargeCode));
        // Only App targets of known platforms; anything else in a frozen App snapshot is ignored, never sent to an API run.
        var targets = selection.Targets
            .Where(target => target is not null && MonitoringProviders.IsApp(target.Provider) && AppChannelDefinition.SupportsEngine(target.Engine))
            .Select(target => AppChannelDefinition.TryGetPlatform(target.Engine)!.Code).Distinct(StringComparer.Ordinal)
            .Select(engine => AutomaticMonitoringSelection.StoredTarget(AppChannelDefinition.Provider, engine,
                AppChannelDefinition.Locale, AppChannelDefinition.Country, null))
            .ToArray();
        if (selection.PromptIds.Count == 0 || targets.Length == 0) return Stop(Skip(AppMonitoringPlan.SelectionEmptyCode));

        var brand = await QuerySingleAsync(c, t, """
            SELECT subject_config_version, target_config_version FROM site_brand_settings
            WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site) FOR SHARE;
            """, ct, ("@tenant", tenant), ("@site", site));
        if (brand is null) return Stop(new(null, "RESOURCE_NOT_FOUND", "品牌配置不存在，App 时隙未提交。"));

        // Current rows of the frozen prompts, in creation order (the order fair_rank follows). Same snapshot columns as an API check.
        var parameters = new List<object> { ("@tenant", tenant), ("@site", site) };
        parameters.AddRange(selection.PromptIds.Select((id, i) => (object)($"@p{i}", id.ToString())));
        var rows = await QueryRowsAsync(c, t, $"""
            SELECT BIN_TO_UUID(p.id), p.content, BIN_TO_UUID(p.current_version_id),
                   COALESCE(v.branded_status, 'unbranded'), COALESCE(v.branded_source, 'system'),
                   COALESCE((SELECT JSON_ARRAYAGG(pvt.tag_name_snapshot) FROM prompt_version_tags pvt
                             WHERE pvt.prompt_version_id = p.current_version_id), JSON_ARRAY())
            FROM prompts p JOIN sites s ON s.id = p.site_id
            LEFT JOIN prompt_versions v ON v.id = p.current_version_id
            WHERE s.tenant_id = UUID_TO_BIN(@tenant) AND s.id = UUID_TO_BIN(@site)
              AND p.archived_at IS NULL AND p.enabled = TRUE
              AND p.id IN ({string.Join(',', selection.PromptIds.Select((_, i) => $"UUID_TO_BIN(@p{i})"))})
            ORDER BY p.created_at, p.id
            FOR SHARE;
            """, ct, parameters.ToArray());
        var prompts = rows.ToDictionary(row => Guid.Parse(row[0]));
        var creationOrder = rows.Select(row => Guid.Parse(row[0])).ToArray();

        // Prompt check (design 5): the operator exclusion wins, then the rule on the prompt's current text.
        var operatorExcluded = await ActiveExclusionsAsync(c, t, tenantId, siteId, ct);
        var excluded = new List<(Guid PromptId, string Reason)>();
        foreach (var id in selection.PromptIds)
        {
            if (!prompts.TryGetValue(id, out var row)) { excluded.Add((id, PromptUnavailableReason)); continue; }
            var eligibility = AppPromptRules.Eligibility(row[1], operatorExcluded.Contains(id));
            if (!eligibility.Eligible) excluded.Add((id, eligibility.ReasonCode!));
        }
        var rejected = excluded.Select(item => item.PromptId).ToHashSet();
        var pairs = MonitoringMatrix.Expand(selection.PromptIds, targets, selection.PromptTargets)
            .Where(pair => !rejected.Contains(pair.PromptId)).ToArray();
        if (pairs.Length == 0)
            return Stop(new(null, AppMonitoringPlan.SelectionEmptyCode, AppMonitoringPlan.SkipMessage(AppMonitoringPlan.SelectionEmptyCode)
                + (excluded.Count == 0 ? "" : $"其中 {excluded.Count} 条提问不进入 App 渠道（{ReasonSummary(excluded)}）。")));

        var entries = await EnabledEntriesAsync(c, t, ct);
        var unavailable = pairs.Select(pair => targets[pair.TargetIndex].Engine).Distinct(StringComparer.Ordinal)
            .Where(engine => !entries.ContainsKey(engine)).ToArray();
        pairs = pairs.Where(pair => entries.ContainsKey(targets[pair.TargetIndex].Engine)).ToArray();
        if (pairs.Length == 0) return Stop(Skip(AppMonitoringPlan.PlatformUnavailableCode));

        var covered = await CoveredPairsAsync(c, t, tenantId, siteId, coveredAtUtc, ct);
        if (covered.Count > 0)
        {
            pairs = pairs.Where(pair => !covered.Contains((pair.PromptId, targets[pair.TargetIndex].Engine))).ToArray();
            if (pairs.Length == 0) return Stop(Skip(AutomaticMonitoringSelection.AlreadyCollectedCode));
        }
        return new(targets, pairs, prompts, creationOrder, excluded, unavailable, entries, brand, null);
    }

    /// <summary>
    /// Prompt × platform pairs of the brand that the current App cycle already covers at <paramref name="atUtc"/>: a consumer_app
    /// collector task of a check of this site whose deadline is later than that moment, in any status (queued, leased, done,
    /// failed or not collected). A regular cycle ends exactly at the next regular slot, so in steady state a regular slot finds
    /// nothing covered; only pairs queued by an instant slot (or a regular slot of the same cycle) are.
    /// </summary>
    internal static async Task<HashSet<(Guid PromptId, string Platform)>> CoveredPairsAsync(MySqlConnection c, MySqlTransaction? t,
        Guid tenantId, Guid siteId, DateTimeOffset atUtc, CancellationToken ct)
        => (await QueryRowsAsync(c, t, """
                SELECT DISTINCT BIN_TO_UUID(chk.prompt_id), task.platform_code
                FROM collector_tasks task JOIN visibility_checks chk ON chk.id = task.check_id
                WHERE task.tenant_id = UUID_TO_BIN(@tenant) AND task.channel = @channel AND task.deadline_at > @at
                  AND chk.tenant_id = UUID_TO_BIN(@tenant) AND chk.site_id = UUID_TO_BIN(@site) AND chk.prompt_id IS NOT NULL;
                """, ct, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()),
                ("@channel", AppChannelDefinition.CollectorChannel), ("@at", atUtc.UtcDateTime)))
            .Select(row => (Guid.Parse(row[0]), row[1])).ToHashSet();

    // Weekly anchors of the brand's enabled App targets by platform (the instant cycle runs up to each platform's next regular slot).
    private static async Task<Dictionary<string, DateOnly>> AnchorsAsync(MySqlConnection c, MySqlTransaction t, Guid tenantId, Guid siteId,
        CancellationToken ct)
    {
        var anchors = new Dictionary<string, DateOnly>(StringComparer.Ordinal);
        foreach (var target in await AppMonitoringPlan.TargetsAsync(c, t, tenantId, siteId, ct))
            if (target.Anchor is { } anchor) anchors.TryAdd(target.Value.Engine, anchor);
        return anchors;
    }
}
