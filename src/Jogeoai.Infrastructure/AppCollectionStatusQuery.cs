using System.Data;
using System.Globalization;
using System.Text.Json;
using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.Extensions.Options;
using MySqlConnector;
using static Jogeoai.Infrastructure.DomesticCollectorStore;

namespace Jogeoai.Infrastructure;

/// <summary>
/// GET /sites/{siteId}/app-collection/status (implement 8.2): per enabled App target, the cycle that contains now and the one
/// before it, with the counts of the checks of the cycle run (planned, collected, in progress, not collected by reason code)
/// and the prompts that did not enter the App channel. A cycle that has a run reads its excluded prompts from the run's
/// <c>selection_snapshot</c> (<c>$.app</c>); a cycle without one (slot skipped, not materialized yet, or the target was not in
/// the run) computes them from the current prompts. One REPEATABLE READ snapshot, no lock, no write.
/// </summary>
/// <param name="database">业务数据库。</param>
/// <param name="appChannel">App 渠道的开放范围（Collector:AppTenantIds）；不提供时不对任何租户开放。</param>
public sealed class AppCollectionStatusQuery(MySqlDatabase database, IOptions<AppChannelOptions>? appChannel = null) : IAppCollectionStatusQuery
{
    private const string Timestamp = "%Y-%m-%d %H:%i:%s.%f";

    /// <inheritdoc />
    public Task<AppCollectionStatusView> GetAsync(Guid tenantId, Guid siteId, CancellationToken cancellationToken = default)
        => GetAsync(tenantId, siteId, DateTimeOffset.UtcNow, cancellationToken);

    /// <summary>The status as of <paramref name="nowUtc"/> (tests pass their own clock).</summary>
    internal async Task<AppCollectionStatusView> GetAsync(Guid tenantId, Guid siteId, DateTimeOffset nowUtc, CancellationToken ct)
    {
        await using var c = database.CreateConnection();
        await c.OpenAsync(ct);
        await using var t = await c.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        (string, object?) tenant = ("@tenant", tenantId.ToString()), site = ("@site", siteId.ToString());
        // Same 404 as the other brand reads: a brand of another tenant, a missing brand and an archived brand look the same.
        var brand = await QuerySingleAsync(c, t, """
            SELECT COALESCE(b.time_zone, '') FROM sites s
            LEFT JOIN site_brand_settings b ON b.site_id = s.id AND b.tenant_id = s.tenant_id
            WHERE s.tenant_id = UUID_TO_BIN(@tenant) AND s.id = UUID_TO_BIN(@site) AND s.status = 'active';
            """, ct, tenant, site) ?? throw new CollectorStoreException("RESOURCE_NOT_FOUND", "品牌不存在或已归档。", 404);
        // design 6.1 / 11: like the catalog and the site targets, a tenant the App channel is not open for and a database
        // without 049 have no App rows to report.
        if (appChannel?.Value.IsOpen(tenantId) != true || !await AppChannelSchema.IsMigratedAsync(c, t, ct))
            return new AppCollectionStatusView(false, []);

        var targets = (await AppMonitoringPlan.TargetsAsync(c, t, tenantId, siteId, ct))
            .OrderBy(target => Position(target.Value.Engine)).ToArray();
        var plan = await QuerySingleAsync(c, t, """
            SELECT BIN_TO_UUID(id), enabled, time_zone FROM collection_schedules
            WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site) AND name = @name ORDER BY created_at, id LIMIT 1;
            """, ct, tenant, site, ("@name", PromptMonitoringIdentity.AppScheduleName));
        var planActive = plan is not null && plan[1] is "1" or "True";
        if (targets.Length == 0) return new AppCollectionStatusView(planActive, []);
        // The App plan's zone; before the plan exists, the zone it will be created in (the main plan's, else the brand's).
        var zone = Zone(plan?[2] ?? await AutomaticMonitoringSelection.MainPlanTimeZoneAsync(c, t, tenantId, siteId, ct) ?? brand[0]);
        var available = (await QueryRowsAsync(c, t, """
                SELECT DISTINCT platform_code FROM collector_platform_entries WHERE channel = 'consumer_app' AND region = 'cn' AND enabled = TRUE;
                """, ct)).Select(row => row[0]).ToHashSet(StringComparer.Ordinal);

        // The two cycles of each target, as local start dates.
        var cycles = targets.Select(target =>
        {
            var current = AppCycleCalendar.CurrentCycleStart(target.Anchor, nowUtc, zone);
            DateOnly? last = current is { } start && target.Anchor is { } anchor && start.AddDays(-AppCycleRules.CycleDays) >= anchor
                ? start.AddDays(-AppCycleRules.CycleDays) : null;
            return (Target: target, Current: current, Last: last);
        }).ToArray();
        var keys = cycles.SelectMany(cycle => new[] { cycle.Current, cycle.Last }).OfType<DateOnly>()
            .Select(AppCycleCalendar.SlotKey).Distinct(StringComparer.Ordinal).ToArray();

        var slots = new Dictionary<string, (string Status, string? SkipCode)>(StringComparer.Ordinal);
        var runs = new Dictionary<string, RunRecord>(StringComparer.Ordinal);
        if (plan is not null && keys.Length > 0)
        {
            var planId = Guid.Parse(plan[0]);
            object[] slotArgs = [("@schedule", planId.ToString()), .. keys.Select((key, i) => (object)($"@key{i}", key))];
            foreach (var row in await QueryRowsAsync(c, t, $"""
                SELECT local_slot_key, status, skip_code FROM schedule_slots
                WHERE schedule_id = UUID_TO_BIN(@schedule) AND local_slot_key IN ({string.Join(",", keys.Select((_, i) => $"@key{i}"))});
                """, ct, slotArgs))
                slots[row[0]] = (row[1], row[2].Length == 0 ? null : row[2]);
            // The cycle run of a slot has the slot's idempotency key (AppCollectionStore.CycleRunKey).
            object[] runArgs = [tenant, site, .. keys.Select((key, i) => (object)($"@run{i}", AppCollectionStore.CycleRunKey(planId, key)))];
            foreach (var row in await QueryRowsAsync(c, t, $"""
                SELECT idempotency_key, BIN_TO_UUID(id), CAST(selection_snapshot AS CHAR) FROM visibility_runs
                WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site)
                  AND idempotency_key IN ({string.Join(",", keys.Select((_, i) => $"@run{i}"))});
                """, ct, runArgs))
            {
                var key = keys.First(candidate => AppCollectionStore.CycleRunKey(planId, candidate) == row[0]);
                runs[key] = RunRecord.Parse(Guid.Parse(row[1]), row[2]);
            }
        }

        // Check counts of the runs: run | engine | status | error code.
        var counts = new Dictionary<(Guid Run, string Engine), List<(string Status, string ErrorCode, int Count)>>();
        if (runs.Count > 0)
        {
            var ids = runs.Values.Select(run => run.Id).Distinct().ToArray();
            object[] countArgs = [tenant, ("@provider", AppChannelDefinition.Provider), .. ids.Select((id, i) => (object)($"@id{i}", id.ToString()))];
            foreach (var row in await QueryRowsAsync(c, t, $"""
                SELECT BIN_TO_UUID(run_id), engine, status, COALESCE(error_code, ''), COUNT(*) FROM visibility_checks
                WHERE tenant_id = UUID_TO_BIN(@tenant) AND provider = @provider
                  AND run_id IN ({string.Join(",", ids.Select((_, i) => $"UUID_TO_BIN(@id{i})"))})
                GROUP BY run_id, engine, status, error_code;
                """, ct, countArgs))
            {
                var key = (Guid.Parse(row[0]), row[1]);
                if (!counts.TryGetValue(key, out var list)) counts[key] = list = [];
                list.Add((row[2], row[3], int.Parse(row[4], CultureInfo.InvariantCulture)));
            }
        }

        // The latest collected answer per platform, to tell whether a target already has its first result.
        var latestResult = (await QueryRowsAsync(c, t, $"""
                SELECT engine, DATE_FORMAT(MAX(captured_at), '{Timestamp}') FROM visibility_checks
                WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site) AND provider = @provider
                  AND status = 'succeeded' AND captured_at IS NOT NULL
                GROUP BY engine;
                """, ct, tenant, site, ("@provider", AppChannelDefinition.Provider)))
            .ToDictionary(row => row[0], row => Utc(row[1]), StringComparer.Ordinal);

        // Prompts are only read when some cycle has no run for its target.
        AppMonitoringEstimate.BrandPrompts? prompts = null;
        async Task<IReadOnlyList<AppReasonCount>> LiveExcludedAsync(string engine)
        {
            prompts ??= await AppMonitoringEstimate.BrandPromptsAsync(c, t, tenantId, siteId, ct);
            return AppMonitoringEstimate.Excluded(prompts.Prompts, [engine]);
        }

        async Task<AppCollectionCycleStatus> CycleAsync(DateOnly start, string engine)
        {
            var key = AppCycleCalendar.SlotKey(start);
            var skip = slots.TryGetValue(key, out var slot) && slot.Status == "skipped" ? slot.SkipCode : null;
            var (startsAt, endsAt) = (AppCycleCalendar.SlotUtc(start, zone), AppCycleCalendar.SlotUtc(start.AddDays(AppCycleRules.CycleDays), zone));
            if (!runs.TryGetValue(key, out var run) || !run.Engines.Contains(engine))
                return new AppCollectionCycleStatus(startsAt, endsAt, false, skip, 0, 0, 0, 0, [], await LiveExcludedAsync(engine));
            var rows = counts.GetValueOrDefault((run.Id, engine), []);
            int Sum(Func<(string Status, string ErrorCode, int Count), bool> predicate) => rows.Where(predicate).Sum(row => row.Count);
            var notCollected = rows.Where(row => row.Status == "cancelled")
                .GroupBy(row => row.ErrorCode, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new AppReasonCount(group.Key, group.Sum(row => row.Count))).ToArray();
            return new AppCollectionCycleStatus(startsAt, endsAt, true, skip, Sum(_ => true), Sum(row => row.Status == "succeeded"),
                Sum(row => row.Status is "pending" or "running"), Sum(row => row.Status == "cancelled"), notCollected, run.Excluded(engine));
        }

        var result = new List<AppTargetCollectionStatus>(cycles.Length);
        foreach (var (target, currentStart, lastStart) in cycles)
        {
            var engine = target.Value.Engine;
            var current = currentStart is { } a ? await CycleAsync(a, engine) : null;
            var last = lastStart is { } b ? await CycleAsync(b, engine) : null;
            // First result: none yet since the target was (last) enabled, and the plan is running.
            var hasResult = latestResult.TryGetValue(engine, out var latest)
                && (target.EnabledAt is not { } enabledAt || latest >= enabledAt);
            DateTimeOffset? expected = null;
            if (planActive && !hasResult)
                expected = current is { InProgress: > 0 } ? current.StartsAt
                    : AppCycleCalendar.NextCycleStart(target.Anchor, nowUtc, zone) is { } next ? AppCycleCalendar.SlotUtc(next, zone) : null;
            result.Add(new AppTargetCollectionStatus(target.Id, engine, AppChannelDefinition.TryGetPlatform(engine)?.DisplayName ?? engine,
                CollectionIdentity.AppTargetKey(engine), available.Contains(engine) ? "available" : "unavailable", target.Anchor,
                current, last, expected));
        }
        await t.CommitAsync(ct);
        return new AppCollectionStatusView(planActive, result);
    }

    /// <summary>
    /// What the status needs from a cycle run's <c>selection_snapshot</c>: the App targets it was expanded for, the custom
    /// scopes it froze and the prompts it excluded (<c>$.app.excludedPrompts</c>, written by AppCollectionStore.CreateCycleRunAsync).
    /// </summary>
    private sealed record RunRecord(Guid Id, IReadOnlySet<string> Engines, IReadOnlyDictionary<Guid, IReadOnlyList<string>> Scopes,
        IReadOnlyList<(Guid PromptId, string ReasonCode)> ExcludedPrompts)
    {
        public IReadOnlyList<AppReasonCount> Excluded(string engine)
            => AppMonitoringEstimate.Excluded(ExcludedPrompts.Select(item => new AppMonitoringEstimate.BrandPrompt(
                new MonitoringPromptScope(item.PromptId, Scopes.GetValueOrDefault(item.PromptId)), new AppPromptEligibility(false, item.ReasonCode))), [engine]);

        public static RunRecord Parse(Guid id, string json)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var engines = new HashSet<string>(StringComparer.Ordinal);
            if (root.TryGetProperty("targets", out var targets) && targets.ValueKind == JsonValueKind.Array)
                foreach (var target in targets.EnumerateArray())
                    if (Text(target, "provider") is { } provider && MonitoringProviders.IsApp(provider) && Text(target, "engine") is { } engine)
                        engines.Add(engine);
            var scopes = new Dictionary<Guid, IReadOnlyList<string>>();
            if (root.TryGetProperty("promptTargets", out var promptTargets) && promptTargets.ValueKind == JsonValueKind.Array)
                foreach (var item in promptTargets.EnumerateArray())
                    if (Text(item, "promptId") is { } prompt && Guid.TryParse(prompt, out var promptId)
                        && item.TryGetProperty("targetKeys", out var keys) && keys.ValueKind == JsonValueKind.Array)
                        scopes[promptId] = keys.EnumerateArray().Select(key => key.GetString() ?? "").ToArray();
            var excluded = new List<(Guid, string)>();
            if (root.TryGetProperty("app", out var app) && app.ValueKind == JsonValueKind.Object
                && app.TryGetProperty("excludedPrompts", out var items) && items.ValueKind == JsonValueKind.Array)
                foreach (var item in items.EnumerateArray())
                    if (Text(item, "promptId") is { } prompt && Guid.TryParse(prompt, out var promptId))
                        excluded.Add((promptId, Text(item, "reasonCode") ?? ""));
            return new RunRecord(id, engines, scopes, excluded);
        }

        private static string? Text(JsonElement element, string name)
            => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
    }

    private static int Position(string engine)
    {
        for (var i = 0; i < AppChannelDefinition.Engines.Count; i++)
            if (string.Equals(AppChannelDefinition.Engines[i], engine, StringComparison.Ordinal)) return i;
        return int.MaxValue;
    }

    // Same fallback as the frequency anchors: an invalid stored zone reads as Asia/Shanghai.
    private static TimeZoneInfo Zone(string? name)
    {
        try { return CollectionScheduleCalendar.ParseTimeZone(name ?? ""); }
        catch (CollectorStoreException) { return CollectionScheduleCalendar.ParseTimeZone("Asia/Shanghai"); }
    }

    private static DateTimeOffset Utc(string value)
        => new(DateTime.SpecifyKind(DateTime.ParseExact(value, "yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture), DateTimeKind.Utc));
}
