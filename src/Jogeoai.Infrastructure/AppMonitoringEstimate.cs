using Jogeoai.Domain;
using MySqlConnector;
using static Jogeoai.Infrastructure.DomesticCollectorStore;

namespace Jogeoai.Infrastructure;

/// <summary>
/// The App part of the target estimate (implement 8.1, contract.md section 10): the App cells, the per-channel breakdown, the
/// excluded prompts, the platforms' capacity and the expected first result. <see cref="Combine"/> is pure; the brand's prompts
/// are read by <see cref="BrandPromptsAsync"/> and the capacity by <see cref="AppCapacityQuery.ReadAsync"/>. A draft without an
/// enabled App cell gets the API estimate back unchanged (the same instance), so its response is byte-identical.
/// </summary>
internal static class AppMonitoringEstimate
{
    /// <summary>One enabled prompt of the brand with its platform scope and its App check result (operator exclusion first).</summary>
    internal sealed record BrandPrompt(MonitoringPromptScope Scope, AppPromptEligibility Eligibility);

    /// <summary>
    /// The brand's enabled prompts as the App plan reads them (<see cref="AutomaticMonitoringSelection.Prompts"/>, at most 201).
    /// <see cref="TooLarge"/>: more than 200 enabled prompts, so every App slot of the brand is skipped (MONITORING_TOO_LARGE).
    /// </summary>
    internal sealed record BrandPrompts(IReadOnlyList<BrandPrompt> Prompts, bool TooLarge);

    /// <summary>A draft App cell: its platform and the anchor it will have once saved (kept when already enabled, else tomorrow).</summary>
    internal sealed record AppCell(string Engine, DateOnly Anchor);

    /// <summary>Reads the brand's enabled prompts, their scopes, their text and the active operator exclusions.</summary>
    internal static async Task<BrandPrompts> BrandPromptsAsync(MySqlConnection c, MySqlTransaction? t, Guid tenantId, Guid siteId,
        CancellationToken ct)
    {
        var scopes = await AutomaticMonitoringSelection.Prompts(c, t, tenantId, siteId, ct);
        var contents = (await QueryRowsAsync(c, t, """
                SELECT BIN_TO_UUID(p.id), p.content FROM prompts p JOIN sites s ON s.id = p.site_id
                WHERE s.tenant_id = UUID_TO_BIN(@tenant) AND p.site_id = UUID_TO_BIN(@site) AND p.enabled = TRUE AND p.archived_at IS NULL;
                """, ct, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString())))
            .ToDictionary(row => Guid.Parse(row[0]), row => row[1]);
        var excluded = await AppCollectionStore.ActiveExclusionsAsync(c, t, tenantId, siteId, ct);
        // Same check as the cycle run (AppCollectionStore.CreateCycleRunAsync): the operator exclusion wins, then the rule.
        return new(scopes.Select(scope => new BrandPrompt(scope, contents.TryGetValue(scope.PromptId, out var content)
                ? AppPromptRules.Eligibility(content, excluded.Contains(scope.PromptId))
                : new AppPromptEligibility(false, AppCollectionStore.PromptUnavailableReason))).ToArray(),
            scopes.Count > MonitoringMatrix.MaxPrompts);
    }

    /// <summary>Whether a prompt is collected on the platform's App target: an 'all' prompt always, a custom one when its scope names the key.</summary>
    internal static bool InScope(MonitoringPromptScope scope, string engine)
        => scope.TargetKeys is not { } keys || keys.Contains(CollectionIdentity.AppTargetKey(engine), StringComparer.Ordinal);

    /// <summary>Enabled prompts that pass the App check and are in scope of the platform (0 for a brand whose App slots are skipped as too large).</summary>
    internal static long Demand(BrandPrompts prompts, string engine)
        => prompts.TooLarge ? 0 : prompts.Prompts.Count(prompt => prompt.Eligibility.Eligible && InScope(prompt.Scope, engine));

    /// <summary>
    /// Prompts in scope of at least one of the platforms that do not enter the App channel, counted by reason code, ordered by
    /// reason code.
    /// </summary>
    internal static IReadOnlyList<AppReasonCount> Excluded(IEnumerable<BrandPrompt> prompts, IReadOnlyCollection<string> engines)
        => prompts.Where(prompt => !prompt.Eligibility.Eligible && engines.Any(engine => InScope(prompt.Scope, engine)))
            .GroupBy(prompt => prompt.Eligibility.ReasonCode ?? "", StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new AppReasonCount(group.Key, group.Count())).ToArray();

    /// <summary>
    /// Adds the App part to the API estimate (contract.md section 10):
    /// <list type="bullet">
    /// <item>every cell gets its channel; the App cells follow the API cells in catalog order (weekly, fast, one run a week,
    /// prompt count = prompts in scope that pass the App check);</item>
    /// <item><c>channels.api</c> is the API estimate's own weekly figures; <c>channels.app.weeklyChecks</c> counts only checks
    /// that pass the check and fit the platform capacity (<see cref="AppCapacityRules.ForBrand"/>);</item>
    /// <item>the top-level weekly figures are the sum of both; next slot, shortfall and blockers stay the API figures;</item>
    /// <item><c>firstResultAt</c> is the earliest next App slot (09:11 in <paramref name="zone"/>) of a cell that gets at least
    /// one check, null when no cell does.</item>
    /// </list>
    /// Without App cells the API estimate is returned as it is.
    /// </summary>
    /// <param name="api">The API estimate (<see cref="PromptMonitoringScale.EstimateTargets"/>).</param>
    /// <param name="prompts">The brand's prompts.</param>
    /// <param name="appCells">The draft's enabled App cells.</param>
    /// <param name="share">The capacity share of the brand on a platform, given the brand's demand there.</param>
    /// <param name="nowUtc">Now.</param>
    /// <param name="zone">The zone of the App plan's slots.</param>
    internal static MonitoringTargetsEstimateView Combine(MonitoringTargetsEstimateView api, BrandPrompts prompts, IReadOnlyList<AppCell> appCells,
        Func<string, long, AppBrandCapacityShare> share, DateTimeOffset nowUtc, TimeZoneInfo zone)
    {
        if (appCells.Count == 0) return api;
        var ordered = appCells.DistinctBy(cell => cell.Engine, StringComparer.Ordinal)
            .OrderBy(cell => Position(cell.Engine)).ThenBy(cell => cell.Engine, StringComparer.Ordinal).ToArray();
        var runs = MonitoringFrequencyRules.WeeklyRuns(AppChannelDefinition.Frequency);
        var cells = new List<MonitoringTargetCellEstimate>(api.Cells.Select(cell => cell with { Channel = CollectionChannel.Api }));
        var platforms = new List<AppPlatformEstimate>();
        decimal appChecks = 0;
        DateTimeOffset? firstResult = null;
        foreach (var cell in ordered)
        {
            var eligible = prompts.Prompts.Count(prompt => prompt.Eligibility.Eligible && InScope(prompt.Scope, cell.Engine));
            var platform = share(cell.Engine, Demand(prompts, cell.Engine));
            cells.Add(new MonitoringTargetCellEstimate(CollectionIdentity.AppTargetKey(cell.Engine), cell.Engine, AppChannelDefinition.DefaultAnswerMode,
                AppChannelDefinition.Frequency, runs, eligible, CollectionChannel.App));
            platforms.Add(new AppPlatformEstimate(cell.Engine, platform.Capacity.WeeklyCapacity, platform.Capacity.WeeklyDemand,
                platform.Capacity.ExpectedUncollected));
            // One sample per check: the App cycle run always uses a sample count of 1.
            appChecks += platform.Scheduled * runs;
            if (platform.Scheduled > 0 && AppCycleCalendar.NextCycleStart(cell.Anchor, nowUtc, zone) is { } next)
            {
                var slot = AppCycleCalendar.SlotUtc(next, zone);
                if (firstResult is null || slot < firstResult) firstResult = slot;
            }
        }
        var appCredits = appChecks * PromptMonitoringPricing.UnitPrice;
        var channels = new MonitoringEstimateChannels(
            new ApiChannelEstimate(api.Cells.Count, api.WeeklyChecks, api.WeeklyCredits),
            new AppChannelEstimate(ordered.Length, appChecks, appCredits,
                Excluded(prompts.Prompts, ordered.Select(cell => cell.Engine).ToArray()), platforms, firstResult));
        return api with
        {
            WeeklyChecks = api.WeeklyChecks + appChecks,
            WeeklyCredits = api.WeeklyCredits + appCredits,
            Cells = cells,
            Channels = channels,
        };
    }

    private static int Position(string engine)
    {
        for (var i = 0; i < AppChannelDefinition.Engines.Count; i++)
            if (string.Equals(AppChannelDefinition.Engines[i], engine, StringComparison.Ordinal)) return i;
        return int.MaxValue;
    }
}
