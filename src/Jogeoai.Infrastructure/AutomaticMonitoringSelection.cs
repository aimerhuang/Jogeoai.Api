using System.Data;
using System.Text.Json;
using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

internal static class AutomaticMonitoringSelection
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    // Frequency / Anchor are read for the slot-level frequency filter (047); before 047 every target is daily without anchor.
    internal sealed record Target(Guid Id, CloroVisibilityTarget Value, DateTimeOffset? EnabledAt,
        MonitoringFrequency Frequency = MonitoringFrequency.Daily, DateOnly? Anchor = null);

    internal static CollectionScheduleSelection Select(IReadOnlyList<Guid> prompts, IReadOnlyList<Target> targets,
        CollectionScheduleSelection previous, DateTimeOffset? nominalSlot = null)
        => Select(prompts.Select(id => new MonitoringPromptScope(id)).ToArray(), targets, previous, nominalSlot);

    // Custom prompts get scope ∩ eligible targets; prompts left without targets are dropped (never widened to all).
    // All-'all' input yields exactly the historical cartesian selection without PromptTargets.
    internal static CollectionScheduleSelection Select(IReadOnlyList<MonitoringPromptScope> prompts, IReadOnlyList<Target> targets,
        CollectionScheduleSelection previous, DateTimeOffset? nominalSlot = null)
    {
        var oldKeys = previous.Targets.Select(CollectionIdentity.TargetKey).ToHashSet(StringComparer.Ordinal);
        var eligible = targets.Where(t => nominalSlot is null
                || t.EnabledAt is { } at && at <= nominalSlot
                || t.EnabledAt is null && oldKeys.Contains(CollectionIdentity.TargetKey(t.Value)))
            .Select(t => t.Value).OrderBy(CollectionIdentity.TargetKey, StringComparer.Ordinal).ToArray();
        // Prompts() reads at most 201 rows as the "over 200" signal. Dropping prompts from a truncated list could make
        // an oversized brand look valid, so keep the full cartesian shape and let InvalidReason report MONITORING_TOO_LARGE.
        if (prompts.Count > MonitoringMatrix.MaxPrompts)
            return new(prompts.Select(p => p.PromptId).Order().ToArray(), eligible, previous.SampleCount);
        return MonitoringMatrix.BuildSelection(prompts, eligible, previous.SampleCount);
    }

    // Legacy (null) activation times only preserve membership while the target stays in both the old and the new plan.
    // A legacy target dropped from the union (custom scopes no longer use it) would otherwise keep null outside the plan,
    // and the next materialization of this plan without a preceding reconcile would skip the slot as activation-pending.
    internal static IReadOnlyList<Target> ActivationCandidates(IReadOnlyList<Target> targets, CollectionScheduleSelection old,
        CollectionScheduleSelection selection)
    {
        var oldKeys = old.Targets.Select(CollectionIdentity.TargetKey).ToHashSet(StringComparer.Ordinal);
        var newKeys = selection.Targets.Select(CollectionIdentity.TargetKey).ToHashSet(StringComparer.Ordinal);
        return targets.Where(t => t.EnabledAt is null
            && !(oldKeys.Contains(CollectionIdentity.TargetKey(t.Value)) && newKeys.Contains(CollectionIdentity.TargetKey(t.Value)))).ToArray();
    }

    /// <summary>The slot has no cell due by frequency (weekly / every_other_day off-days); nothing is submitted or reserved.</summary>
    internal const string NotDueCode = "MONITORING_NOT_DUE";

    /// <summary>Every prompt × cell of a main slot was already instantly collected that local day; nothing is submitted or reserved.</summary>
    internal const string AlreadyCollectedCode = "MONITORING_ALREADY_COLLECTED";

    /// <summary>
    /// Prompt × target-key pairs that already have a pending, running or succeeded check in a run of this site whose
    /// requested_by is one of <paramref name="requestedBy"/> and that was created on <paramref name="localDate"/> in
    /// <paramref name="zone"/>. Failed checks do not count, so the pair can be collected again the same day.
    /// </summary>
    internal static async Task<IReadOnlyList<PromptTargetPair>> CollectedPairsAsync(MySqlConnection connection, MySqlTransaction? tx,
        Guid tenant, Guid site, IReadOnlyList<string> requestedBy, DateOnly localDate, TimeZoneInfo zone, CancellationToken ct)
    {
        if (requestedBy.Count == 0) return [];
        var startUtc = LocalDayStartUtc(localDate, zone);
        var endUtc = LocalDayStartUtc(localDate.AddDays(1), zone);
        await using var query = Scoped(connection, tx, $"""
            SELECT DISTINCT BIN_TO_UUID(c.prompt_id), e.target_key
            FROM visibility_runs r
            JOIN visibility_checks c ON c.run_id = r.id
            JOIN run_engines e ON e.id = c.run_target_id
            WHERE r.tenant_id=UUID_TO_BIN(@tenant) AND r.site_id=UUID_TO_BIN(@site)
              AND r.requested_by IN ({string.Join(", ", requestedBy.Select((_, i) => $"@by{i}"))})
              AND r.created_at >= @start AND r.created_at < @end
              AND c.status IN ('pending', 'running', 'succeeded');
            """, tenant, site);
        for (var i = 0; i < requestedBy.Count; i++) query.Parameters.AddWithValue($"@by{i}", requestedBy[i]);
        query.Parameters.AddWithValue("@start", startUtc);
        query.Parameters.AddWithValue("@end", endUtc);
        var pairs = new List<PromptTargetPair>();
        await using var reader = await query.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) pairs.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1)));
        return pairs;
    }

    /// <summary>
    /// UTC instant at which <paramref name="date"/> begins in <paramref name="zone"/>. A DST jump at midnight
    /// (e.g. America/Santiago, Africa/Cairo) makes 00:00 invalid, where ConvertTimeToUtc would throw and block slot
    /// materialization; the day then begins at the first valid local minute.
    /// </summary>
    internal static DateTime LocalDayStartUtc(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    /// <summary>
    /// One materialized automatic-monitoring slot: <see cref="Current"/> is the plan snapshot written back to the schedule,
    /// <see cref="Selected"/> the frozen slot selection, <see cref="SkipCode"/> non-null when the slot is skipped.
    /// </summary>
    internal sealed record SlotPlan(CollectionScheduleSelection Current, CollectionScheduleSelection Selected, string? SkipCode);

    /// <summary>
    /// Plans a slot of the 09:01 main or the 00:05 night schedule. <paramref name="reference"/> supplies the known (legacy
    /// activation) target keys and the sample count: the main plan's own snapshot for both kinds, so a night slot follows the
    /// main plan's membership. The main plan snapshot keeps every configured cell; the night plan only twice_daily cells.
    /// The slot itself only takes cells due on <paramref name="slotLocalDate"/> (the slot's original local date). When the
    /// frequency filter alone empties an otherwise valid slot, it is skipped as <see cref="NotDueCode"/>.
    /// </summary>
    internal static SlotPlan PlanSlot(MonitoringSlotKind kind, CollectionScheduleSelection reference,
        IReadOnlyList<MonitoringPromptScope> prompts, IReadOnlyList<Target> targets, DateOnly slotLocalDate, DateTimeOffset nominalSlot)
    {
        var planTargets = kind == MonitoringSlotKind.Night
            ? targets.Where(t => t.Frequency == MonitoringFrequency.TwiceDaily).ToArray()
            : targets.ToArray();
        var knownKeys = reference.Targets.Select(CollectionIdentity.TargetKey).ToHashSet(StringComparer.Ordinal);
        var pendingActivation = planTargets.Any(t => t.EnabledAt is null && !knownKeys.Contains(CollectionIdentity.TargetKey(t.Value)));
        var current = Select(prompts,
            planTargets.Where(t => t.EnabledAt is not null || knownKeys.Contains(CollectionIdentity.TargetKey(t.Value))).ToArray(), reference);
        var due = planTargets.Where(t => MonitoringFrequencyRules.IsDue(t.Frequency, t.Anchor, slotLocalDate, kind)).ToArray();
        var selected = Select(prompts, due, reference, nominalSlot);
        var skip = pendingActivation ? "MONITORING_ACTIVATION_PENDING" : InvalidReason(current);
        if (skip is null)
        {
            skip = InvalidReason(selected);
            if (skip == "MONITORING_SELECTION_EMPTY" && due.Length < planTargets.Length
                && InvalidReason(Select(prompts, planTargets, reference, nominalSlot)) is null)
                skip = NotDueCode;
        }
        return new(current, selected, skip);
    }

    /// <summary>The slot key's original local date (yyyyMMdd prefix of <see cref="CollectionScheduleCalendar.LocalSlotKey"/>).</summary>
    internal static DateOnly SlotLocalDate(string localSlotKey)
        => DateOnly.ParseExact(localSlotKey[..8], "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The part of a frozen plan selection that is due in a slot: plan targets not due on <paramref name="slotLocalDate"/> are
    /// removed and custom prompt scopes intersected (prompts left without a target are dropped). Plan targets that are no
    /// longer enabled count as due (they would only leave the plan at the next reconcile).
    /// </summary>
    internal static CollectionScheduleSelection DueSelection(CollectionScheduleSelection plan, IReadOnlyList<Target> targets,
        DateOnly slotLocalDate, MonitoringSlotKind kind)
    {
        var notDue = targets.Where(t => !MonitoringFrequencyRules.IsDue(t.Frequency, t.Anchor, slotLocalDate, kind))
            .Select(t => CollectionIdentity.TargetKey(t.Value)).ToHashSet(StringComparer.Ordinal);
        if (notDue.Count == 0 || !plan.Targets.Any(t => notDue.Contains(CollectionIdentity.TargetKey(t)))) return plan;
        var scopes = plan.PromptTargets?.ToDictionary(p => p.PromptId, p => (IReadOnlyList<string>)p.TargetKeys);
        var remaining = plan.Targets.Where(t => !notDue.Contains(CollectionIdentity.TargetKey(t))).ToArray();
        if (remaining.Length == 0) return new(plan.PromptIds, [], plan.SampleCount);
        return MonitoringMatrix.BuildSelection(
            plan.PromptIds.Select(id => new MonitoringPromptScope(id, scopes?.GetValueOrDefault(id))).ToArray(), remaining, plan.SampleCount);
    }

    internal static string? InvalidReason(CollectionScheduleSelection selection)
    {
        if (selection.PromptIds.Count == 0 || selection.Targets.Count == 0) return "MONITORING_SELECTION_EMPTY";
        if (selection.PromptIds.Count > MonitoringMatrix.MaxPrompts || selection.Targets.Count > MonitoringMatrix.MaxTargets
            || selection.SampleCount is < 1 or > 10)
            return "MONITORING_TOO_LARGE";
        // The 1000 limit counts prompt × effective target × sample pairs (equal to P × T × S without custom scopes).
        var checks = MonitoringMatrix.CountChecks(selection);
        if (checks == 0) return "MONITORING_SELECTION_EMPTY";
        return checks > MonitoringMatrix.MaxChecks ? "MONITORING_TOO_LARGE" : null;
    }

    // Enabled, nonarchived prompts with their target scope. Before 042 every prompt is 'all' (old reads keep working).
    internal static async Task<IReadOnlyList<MonitoringPromptScope>> Prompts(MySqlConnection connection, MySqlTransaction? tx,
        Guid tenant, Guid site, CancellationToken ct)
    {
        var migrated = await PromptTargetScopeSchema.IsMigratedAsync(connection, tx, ct);
        await using var query = Scoped(connection, tx, $"""
            SELECT BIN_TO_UUID(p.id), {(migrated ? "p.target_scope" : "'all'")} FROM prompts p JOIN sites s ON s.id=p.site_id
            WHERE s.tenant_id=UUID_TO_BIN(@tenant) AND p.site_id=UUID_TO_BIN(@site)
              AND p.enabled=TRUE AND p.archived_at IS NULL ORDER BY p.id LIMIT 201;
            """, tenant, site);
        var rows = new List<(Guid Id, bool Custom)>();
        await using (var reader = await query.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                rows.Add((Guid.Parse(reader.GetString(0)), string.Equals(reader.GetString(1), "custom", StringComparison.Ordinal)));
        if (!rows.Any(r => r.Custom)) return rows.Select(r => new MonitoringPromptScope(r.Id)).ToArray();
        var keys = rows.Where(r => r.Custom).ToDictionary(r => r.Id, _ => new List<string>());
        await using (var scopes = Scoped(connection, tx, """
            SELECT BIN_TO_UUID(pts.prompt_id), pts.target_key FROM prompt_target_scopes pts
            JOIN prompts p ON p.id=pts.prompt_id AND p.site_id=pts.site_id
            WHERE pts.tenant_id=UUID_TO_BIN(@tenant) AND pts.site_id=UUID_TO_BIN(@site)
              AND p.enabled=TRUE AND p.archived_at IS NULL AND p.target_scope='custom'
            ORDER BY pts.prompt_id, pts.target_key;
            """, tenant, site))
        await using (var reader = await scopes.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                if (keys.TryGetValue(Guid.Parse(reader.GetString(0)), out var list)) list.Add(reader.GetString(1));
        return rows.Select(r => new MonitoringPromptScope(r.Id, r.Custom ? keys[r.Id] : null)).ToArray();
    }

    internal static async Task<IReadOnlyList<Target>> Targets(MySqlConnection connection, MySqlTransaction? tx,
        Guid tenant, Guid site, bool forUpdate, CancellationToken ct)
    {
        // Every registered API answer provider (cloro, doubao_api, deepseek_api) takes part in automatic monitoring.
        // The stored non-default answer mode is part of the target (and its key); NULL is the provider default.
        var migrated = await AnswerModeSchema.IsMigratedAsync(connection, tx, ct);
        await using var query = Scoped(connection, tx, $"""
            SELECT BIN_TO_UUID(id), provider, engine, locale, country, monitoring_enabled_at,
                   {(migrated ? "answer_mode, frequency, DATE_FORMAT(frequency_anchor, '%Y-%m-%d')" : "NULL, 'daily', NULL")}
            FROM site_targets WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site)
              AND enabled=TRUE AND provider IN ({string.Join(", ", AnswerProviderDefinition.All.Select((_, i) => $"@provider{i}"))}) ORDER BY id
            """ + (forUpdate ? " FOR UPDATE;" : ";"), tenant, site);
        for (var i = 0; i < AnswerProviderDefinition.All.Count; i++)
            query.Parameters.AddWithValue($"@provider{i}", AnswerProviderDefinition.All[i].Provider);
        var result = new List<Target>();
        await using var reader = await query.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(new(Guid.Parse(reader.GetString(0)),
            StoredTarget(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.IsDBNull(6) ? null : reader.GetString(6)),
            reader.IsDBNull(5) ? null : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc)),
            MonitoringFrequencyRules.TryParse(reader.IsDBNull(7) ? null : reader.GetString(7), out var frequency) ? frequency : MonitoringFrequency.Daily,
            reader.IsDBNull(8) ? null : DateOnly.ParseExact(reader.GetString(8), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)));
        return result;
    }

    // Folds the default mode to null (byte-identical keys and hashes). A stored mode the provider no longer supports is
    // kept verbatim so the run creation reports ANSWER_MODE_UNSUPPORTED instead of silently collecting another mode.
    internal static CloroVisibilityTarget StoredTarget(string provider, string engine, string locale, string country, string? answerMode)
    {
        var target = new CloroVisibilityTarget(provider, engine, locale, country, "{}", answerMode);
        return AnswerModeRules.TryNormalize(target, out var normalized) ? normalized : target;
    }

    // The coordinator owns Site -> Target -> Schedule locks. No Run, slot or credit is created here.
    // Also reconciles the 00:05 night plan (create / enable / disable) after the main plan in the same transaction.
    internal static async Task ReconcileAsync(MySqlDatabase database, Guid tenant, Guid site, CancellationToken ct)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(ct);
        await GeminiResultSchema.RequireAsync(connection, null, ct);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        if (!await LockActiveSiteAsync(connection, tx, tenant, site, ct)) return;
        var targets = await Targets(connection, tx, tenant, site, true, ct);
        if (await MainPlanAsync(connection, tx, tenant, site, ct) is not { } main) return;
        var prompts = await Prompts(connection, tx, tenant, site, ct);
        var old = main.Selection;
        // Select without a nominal slot ignores activation times, so it can run before the boundaries are written.
        var selection = Select(prompts, targets, old);
        // Already locked targets: adding an unknown legacy target, or dropping a legacy member from the plan
        // (no prompt scope uses it any more), gets a forward-only boundary, never an invented historical
        // activation. Members that stay in the plan keep null.
        foreach (var target in ActivationCandidates(targets, old, selection))
        {
            await using var activate = new MySqlCommand("""
                UPDATE site_targets SET monitoring_enabled_at=UTC_TIMESTAMP(6) WHERE id=UUID_TO_BIN(@id)
                  AND monitoring_enabled_at IS NULL;
                """, connection, tx);
            activate.Parameters.AddWithValue("@id", target.Id.ToString());
            await activate.ExecuteNonQueryAsync(ct);
        }
        if (CollectionIdentity.SelectionHash(old) != CollectionIdentity.SelectionHash(selection))
        {
            await using var update = new MySqlCommand("""
                UPDATE collection_schedules SET selection_snapshot=@selection, version=version+1, updated_at=UTC_TIMESTAMP(6)
                WHERE id=UUID_TO_BIN(@id);
                """, connection, tx);
            update.Parameters.AddWithValue("@id", main.Id.ToString());
            update.Parameters.AddWithValue("@selection", JsonSerializer.Serialize(selection, Json));
            await update.ExecuteNonQueryAsync(ct);
        }
        await ReconcileNightAsync(connection, tx, tenant, site, prompts, targets, main with { Selection = selection }, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Night-plan-only reconcile, called right after a site-target PUT commits (the lazy <see cref="ReconcileAsync"/> before
    /// each slot and on prompt-monitoring GET/POST covers a failure here). Does nothing without the main plan, i.e. before
    /// daily monitoring has been set up for the brand. Never touches the main plan, activation boundaries, runs or credits.
    /// </summary>
    internal static async Task ReconcileNightScheduleAsync(MySqlDatabase database, Guid tenant, Guid site, CancellationToken ct)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        if (!await LockActiveSiteAsync(connection, tx, tenant, site, ct)) return;
        var targets = await Targets(connection, tx, tenant, site, false, ct);
        if (await MainPlanAsync(connection, tx, tenant, site, ct) is not { } main) return;
        await ReconcileNightAsync(connection, tx, tenant, site, await Prompts(connection, tx, tenant, site, ct), targets, main, ct);
        await tx.CommitAsync(ct);
    }

    private sealed record MainPlan(Guid Id, CollectionScheduleSelection Selection, bool Enabled, string TimeZone);

    /// <summary>
    /// Time zone of the brand's 09:01 main plan, or null before daily monitoring exists. Slot local dates (and therefore
    /// <see cref="MonitoringFrequencyRules.IsDue"/>) use the plan's zone, which does not follow later brand time-zone edits,
    /// so frequency anchors and target estimates must use the same zone. Plain read, no lock.
    /// </summary>
    internal static async Task<string?> MainPlanTimeZoneAsync(MySqlConnection connection, MySqlTransaction? tx, Guid tenant, Guid site,
        CancellationToken ct)
    {
        await using var plan = Scoped(connection, tx, """
            SELECT time_zone FROM collection_schedules
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND name=@name LIMIT 1;
            """, tenant, site);
        plan.Parameters.AddWithValue("@name", PromptMonitoringIdentity.ScheduleName);
        return await plan.ExecuteScalarAsync(ct) as string;
    }

    private static async Task<bool> LockActiveSiteAsync(MySqlConnection connection, MySqlTransaction tx, Guid tenant, Guid site, CancellationToken ct)
    {
        await using var siteLock = Scoped(connection, tx, """
            SELECT id FROM sites WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@site)
              AND status='active' FOR UPDATE;
            """, tenant, site);
        return await siteLock.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task<MainPlan?> MainPlanAsync(MySqlConnection connection, MySqlTransaction tx, Guid tenant, Guid site, CancellationToken ct)
    {
        await using var plan = Scoped(connection, tx, """
            SELECT BIN_TO_UUID(id), selection_snapshot, enabled, time_zone FROM collection_schedules
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND name=@name FOR UPDATE;
            """, tenant, site);
        plan.Parameters.AddWithValue("@name", PromptMonitoringIdentity.ScheduleName);
        await using var reader = await plan.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new(Guid.Parse(reader.GetString(0)), JsonSerializer.Deserialize<CollectionScheduleSelection>(reader.GetString(1), Json)!,
            reader.GetBoolean(2), reader.GetString(3));
    }

    /// <summary>
    /// The 00:05 night plan exists only for twice_daily cells: created (daily, brand time zone of the main plan) or enabled
    /// while the main plan is enabled and at least one enabled monitoring target is twice_daily, disabled otherwise. Enabling
    /// always starts at the next 00:05 after now (no initial or catch-up run). Before 047 every target reads as daily, so the
    /// night plan is never created. The snapshot is the main-plan selection restricted to twice_daily cells.
    /// </summary>
    private static async Task ReconcileNightAsync(MySqlConnection connection, MySqlTransaction tx, Guid tenant, Guid site,
        IReadOnlyList<MonitoringPromptScope> prompts, IReadOnlyList<Target> targets, MainPlan main, CancellationToken ct)
    {
        var twiceDaily = targets.Where(t => t.Frequency == MonitoringFrequency.TwiceDaily).ToArray();
        var wanted = main.Enabled && twiceDaily.Length > 0;
        Guid? nightId = null;
        var enabled = false;
        string? zone = null;
        CollectionScheduleSelection? old = null;
        await using (var query = Scoped(connection, tx, """
            SELECT BIN_TO_UUID(id), enabled, time_zone, selection_snapshot FROM collection_schedules
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND name=@name ORDER BY created_at, id LIMIT 1 FOR UPDATE;
            """, tenant, site))
        {
            query.Parameters.AddWithValue("@name", PromptMonitoringIdentity.NightScheduleName);
            await using var reader = await query.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                nightId = Guid.Parse(reader.GetString(0));
                enabled = reader.GetBoolean(1);
                zone = reader.GetString(2);
                old = JsonSerializer.Deserialize<CollectionScheduleSelection>(reader.GetString(3), Json);
            }
        }
        if (nightId is null && !wanted) return;
        var now = DateTimeOffset.UtcNow;
        if (!wanted)
        {
            if (!enabled) return;
            await using var disable = new MySqlCommand("""
                UPDATE collection_schedules SET enabled=FALSE, next_slot_at=NULL, version=version+1, updated_at=@now WHERE id=UUID_TO_BIN(@id);
                """, connection, tx);
            disable.Parameters.AddWithValue("@id", nightId!.Value.ToString());
            disable.Parameters.AddWithValue("@now", now.UtcDateTime);
            await disable.ExecuteNonQueryAsync(ct);
            return;
        }
        var selection = Select(prompts, twiceDaily, main.Selection);
        var json = JsonSerializer.Serialize(selection, Json);
        var next = CollectionScheduleCalendar.NextAfter("daily", null,
            CollectionScheduleCalendar.ParseLocalTime(PromptMonitoringIdentity.NightLocalTime),
            CollectionScheduleCalendar.ParseTimeZone(main.TimeZone), now);
        if (nightId is null)
        {
            await using var insert = new MySqlCommand("""
                INSERT INTO collection_schedules
                    (id, tenant_id, site_id, name, cadence, day_of_week, local_time, time_zone,
                     selection_snapshot, enabled, next_slot_at, missed_slots, created_at, updated_at)
                VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), @name, 'daily', NULL,
                        @local, @zone, @selection, TRUE, @next, 0, @now, @now);
                """, connection, tx);
            insert.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
            insert.Parameters.AddWithValue("@tenant", tenant.ToString());
            insert.Parameters.AddWithValue("@site", site.ToString());
            insert.Parameters.AddWithValue("@name", PromptMonitoringIdentity.NightScheduleName);
            insert.Parameters.AddWithValue("@local", PromptMonitoringIdentity.NightLocalTime);
            insert.Parameters.AddWithValue("@zone", main.TimeZone);
            insert.Parameters.AddWithValue("@selection", json);
            insert.Parameters.AddWithValue("@next", next.UtcDateTime);
            insert.Parameters.AddWithValue("@now", now.UtcDateTime);
            await insert.ExecuteNonQueryAsync(ct);
            return;
        }
        // An enabled plan keeps its pending next slot unless the brand time zone moved; a re-enabled plan starts at the next 00:05.
        var retime = !enabled || !string.Equals(zone, main.TimeZone, StringComparison.Ordinal);
        if (!retime && old is not null && CollectionIdentity.SelectionHash(old) == CollectionIdentity.SelectionHash(selection)) return;
        await using var update = new MySqlCommand($"""
            UPDATE collection_schedules SET enabled=TRUE, time_zone=@zone, selection_snapshot=@selection,
                {(retime ? "next_slot_at=@next, " : "")}version=version+1, updated_at=@now WHERE id=UUID_TO_BIN(@id);
            """, connection, tx);
        update.Parameters.AddWithValue("@id", nightId.Value.ToString());
        update.Parameters.AddWithValue("@zone", main.TimeZone);
        update.Parameters.AddWithValue("@selection", json);
        update.Parameters.AddWithValue("@next", next.UtcDateTime);
        update.Parameters.AddWithValue("@now", now.UtcDateTime);
        await update.ExecuteNonQueryAsync(ct);
    }

    private static MySqlCommand Scoped(MySqlConnection connection, MySqlTransaction? tx, string sql, Guid tenant, Guid site)
    {
        var command = new MySqlCommand(sql, connection, tx);
        command.Parameters.AddWithValue("@tenant", tenant.ToString());
        command.Parameters.AddWithValue("@site", site.ToString());
        return command;
    }
}
