using System.Data;
using System.Globalization;
using System.Text.Json;
using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

/// <summary>
/// The App channel's weekly plan (design 4.1, KD-24): one <c>collection_schedules</c> row per brand, named
/// <see cref="PromptMonitoringIdentity.AppScheduleName"/>, daily at <see cref="PromptMonitoringIdentity.AppLocalTime"/>.
/// It is independent of the 09:01 main plan: a brand may have either plan or both. Its snapshot holds App cells only and
/// is written with its own SQL — it never goes through <c>CollectionScheduleStore.Normalize</c>, which refuses providers
/// outside the API registry. Nothing here creates a run, a slot, a collector task or a credit reservation: a due slot is
/// left pending and <see cref="AppCollectionStore.CreateCycleRunAsync"/> expands it into the weekly cycle run.
/// A tenant the App channel is not open for (Collector:AppTenantIds) is never read or written by this class.
/// </summary>
internal static class AppMonitoringPlan
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly CollectionScheduleSelection Empty = new([], [], 1);

    /// <summary>Slot skip code: the tenant is not listed in Collector:AppTenantIds.</summary>
    internal const string NotOpenCode = "APP_CHANNEL_NOT_OPEN";

    /// <summary>
    /// Slot skip code: every platform that still has a prompt to collect lost its enabled consumer_app entry between the
    /// materialization of the slot and the creation of its cycle run, so nothing can be collected.
    /// </summary>
    internal const string PlatformUnavailableCode = "APP_PLATFORM_UNAVAILABLE";

    /// <summary>Slot skip code: the brand's main plan is paused and the App plan has not been reconciled to follow it yet.</summary>
    internal const string MainPausedCode = "SCHEDULE_DISABLED";

    internal const string TooLargeCode = "MONITORING_TOO_LARGE";
    internal const string SelectionEmptyCode = "MONITORING_SELECTION_EMPTY";

    /// <summary>A planned App slot: the frozen slot selection and, when the slot is not submitted, the skip code.</summary>
    internal sealed record SlotPlan(CollectionScheduleSelection Selected, string? SkipCode);

    /// <summary>
    /// The App plan snapshot: enabled prompts × enabled App targets. An 'all' prompt takes every App target, a custom prompt
    /// its scope ∩ App targets, and a custom prompt without an enabled App target in its scope is not part of the snapshot
    /// (never widened to all). API target keys in a scope do not match anything here.
    /// </summary>
    internal static CollectionScheduleSelection Snapshot(IReadOnlyList<MonitoringPromptScope> prompts,
        IReadOnlyList<AutomaticMonitoringSelection.Target> appTargets)
        => AutomaticMonitoringSelection.Select(prompts, appTargets, Empty);

    /// <summary>
    /// Pause is one brand-level switch: with a main plan the App plan follows it, without one the App plan's own flag is
    /// the switch and is left as it is.
    /// </summary>
    /// <param name="mainEnabled">The main plan's enabled flag; null when the brand has no main plan.</param>
    /// <param name="appEnabled">The App plan's current enabled flag.</param>
    internal static bool MirroredEnabled(bool? mainEnabled, bool appEnabled) => mainEnabled ?? appEnabled;

    /// <summary>
    /// Decides one App slot. Skipped, in this order: tenant not open → <see cref="NotOpenCode"/>; main plan paused →
    /// <see cref="MainPausedCode"/>; no App target due on <paramref name="slotLocalDate"/> → MONITORING_NOT_DUE; more than
    /// 200 prompts → MONITORING_TOO_LARGE; no prompt × due target pair → MONITORING_SELECTION_EMPTY. Otherwise the slot is
    /// left pending with the due selection frozen. The 20-target and 1000-check limits of the API round do not apply.
    /// </summary>
    internal static SlotPlan PlanSlot(bool open, bool? mainEnabled, IReadOnlyList<MonitoringPromptScope> prompts,
        IReadOnlyList<AutomaticMonitoringSelection.Target> appTargets, DateOnly slotLocalDate)
    {
        var due = appTargets.Where(t => MonitoringFrequencyRules.IsDue(t.Frequency, t.Anchor, slotLocalDate, MonitoringSlotKind.App)).ToArray();
        var selected = Snapshot(prompts, due);
        var skip = !open ? NotOpenCode
            : mainEnabled == false ? MainPausedCode
            : due.Length == 0 ? AutomaticMonitoringSelection.NotDueCode
            : prompts.Count > MonitoringMatrix.MaxPrompts ? TooLargeCode
            : selected.PromptIds.Count == 0 ? SelectionEmptyCode
            : null;
        return new(selected, skip);
    }

    /// <summary>The message stored with a skipped App slot.</summary>
    internal static string SkipMessage(string code) => code switch
    {
        NotOpenCode => "App 渠道尚未对该团队开放，本时隙未提交，也未预留积分。",
        MainPausedCode => "品牌监控已暂停，App 时隙未提交，也未预留积分。",
        AutomaticMonitoringSelection.NotDueCode => "本时隙没有到期的 App 目标（1次/周），未提交，也未预留积分。",
        TooLargeCode => "品牌启用的提问超过 200 个，App 时隙未提交。",
        PlatformUnavailableCode => "到期的 App 平台当前都不可用，本时隙未提交，也未预留积分。",
        _ => "没有可在 App 渠道采集的提问与目标，本时隙未提交。",
    };

    /// <summary>
    /// Plans a due App plan slot inside the materialization transaction (the App plan row is already locked). Plain reads
    /// only. A tenant that is not open is skipped without reading anything; its slot freezes the stored plan snapshot.
    /// </summary>
    internal static async Task<(string SelectionJson, string? SkipCode)> PlanSlotAsync(MySqlConnection connection, MySqlTransaction tx,
        Guid tenant, Guid site, bool open, string planSelectionJson, string localSlotKey, CancellationToken ct)
    {
        if (!open) return (planSelectionJson, NotOpenCode);
        var main = await MainPlanAsync(connection, tx, tenant, site, ct);
        var targets = await TargetsAsync(connection, tx, tenant, site, ct);
        var prompts = await AutomaticMonitoringSelection.Prompts(connection, tx, tenant, site, ct);
        var plan = PlanSlot(true, main?.Enabled, prompts, targets, AutomaticMonitoringSelection.SlotLocalDate(localSlotKey));
        return (JsonSerializer.Serialize(plan.Selected, Json), plan.SkipCode);
    }

    /// <summary>The brand's enabled App targets (known platforms only) with their weekly anchor; empty before 047.</summary>
    internal static async Task<IReadOnlyList<AutomaticMonitoringSelection.Target>> TargetsAsync(MySqlConnection connection,
        MySqlTransaction? tx, Guid tenant, Guid site, CancellationToken ct)
    {
        // App targets are weekly, which cannot be stored before 047.
        if (!await AnswerModeSchema.IsMigratedAsync(connection, tx, ct)) return [];
        await using var query = Scoped(connection, tx, """
            SELECT BIN_TO_UUID(id), engine, monitoring_enabled_at, frequency, DATE_FORMAT(frequency_anchor, '%Y-%m-%d')
            FROM site_targets WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site)
              AND enabled=TRUE AND provider=@provider ORDER BY id;
            """, tenant, site);
        query.Parameters.AddWithValue("@provider", AppChannelDefinition.Provider);
        var result = new List<AutomaticMonitoringSelection.Target>();
        await using var reader = await query.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var engine = reader.GetString(1);
            if (!AppChannelDefinition.SupportsEngine(engine)) continue;
            result.Add(new(Guid.Parse(reader.GetString(0)),
                AutomaticMonitoringSelection.StoredTarget(AppChannelDefinition.Provider, engine, AppChannelDefinition.Locale,
                    AppChannelDefinition.Country, null),
                reader.IsDBNull(2) ? null : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc)),
                MonitoringFrequencyRules.TryParse(reader.IsDBNull(3) ? null : reader.GetString(3), out var frequency)
                    ? frequency : AppChannelDefinition.Frequency,
                reader.IsDBNull(4) ? null : DateOnly.ParseExact(reader.GetString(4), "yyyy-MM-dd", CultureInfo.InvariantCulture)));
        }
        return result;
    }

    /// <summary>
    /// Reconciles the App plan of one brand, at the moments the night plan is reconciled (after a site-target save, lazily
    /// on prompt-monitoring GET/POST and before main / night slots) and when monitoring is started.
    /// <list type="bullet">
    /// <item>Missing plan: created when the brand has an enabled App target and either monitoring is being started
    /// (<paramref name="starting"/>) or the brand already monitors (its main plan exists). Created enabled like the main
    /// plan (enabled without one), in the main plan's zone (brand zone without one), first slot at the next 09:11.</item>
    /// <item>Existing plan: the snapshot is refreshed, and the version only moves when something changed. With a main plan
    /// the enabled flag and the zone follow it; without one they are left alone. A brand without enabled App targets keeps
    /// its plan and its flag (its slots simply have nothing due).</item>
    /// </list>
    /// Lock order: site → App plan row. Does nothing for a tenant the App channel is not open for.
    /// </summary>
    internal static async Task ReconcileAsync(MySqlDatabase database, Guid tenant, Guid site, bool open, bool starting, CancellationToken ct)
    {
        if (!open) return;
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        string? brandZone;
        await using (var siteLock = Scoped(connection, tx, """
            SELECT COALESCE(b.time_zone, '') FROM sites s
            LEFT JOIN site_brand_settings b ON b.site_id = s.id AND b.tenant_id = s.tenant_id
            WHERE s.tenant_id=UUID_TO_BIN(@tenant) AND s.id=UUID_TO_BIN(@site) AND s.status='active' FOR UPDATE OF s;
            """, tenant, site))
            brandZone = await siteLock.ExecuteScalarAsync(ct) as string;
        if (brandZone is null) return;
        var targets = await TargetsAsync(connection, tx, tenant, site, ct);
        var main = await MainPlanAsync(connection, tx, tenant, site, ct);
        Guid? appId = null;
        var enabled = false;
        var zone = "";
        CollectionScheduleSelection? old = null;
        await using (var query = Scoped(connection, tx, """
            SELECT BIN_TO_UUID(id), enabled, time_zone, selection_snapshot FROM collection_schedules
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND name=@name ORDER BY created_at, id LIMIT 1 FOR UPDATE;
            """, tenant, site))
        {
            query.Parameters.AddWithValue("@name", PromptMonitoringIdentity.AppScheduleName);
            await using var reader = await query.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                appId = Guid.Parse(reader.GetString(0));
                enabled = reader.GetBoolean(1);
                zone = reader.GetString(2);
                old = JsonSerializer.Deserialize<CollectionScheduleSelection>(reader.GetString(3), Json);
            }
        }
        if (appId is null && (targets.Count == 0 || !(starting || main is not null))) return;
        var selection = Snapshot(await AutomaticMonitoringSelection.Prompts(connection, tx, tenant, site, ct), targets);
        var json = JsonSerializer.Serialize(selection, Json);
        var now = DateTimeOffset.UtcNow;
        if (appId is null)
        {
            var planZone = main?.TimeZone ?? ValidZone(brandZone);
            var start = main?.Enabled ?? true;
            await using var insert = new MySqlCommand("""
                INSERT INTO collection_schedules
                    (id, tenant_id, site_id, name, cadence, day_of_week, local_time, time_zone,
                     selection_snapshot, enabled, next_slot_at, missed_slots, created_at, updated_at)
                VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), @name, 'daily', NULL,
                        @local, @zone, @selection, @enabled, @next, 0, @now, @now);
                """, connection, tx);
            insert.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
            insert.Parameters.AddWithValue("@tenant", tenant.ToString());
            insert.Parameters.AddWithValue("@site", site.ToString());
            insert.Parameters.AddWithValue("@name", PromptMonitoringIdentity.AppScheduleName);
            insert.Parameters.AddWithValue("@local", PromptMonitoringIdentity.AppLocalTime);
            insert.Parameters.AddWithValue("@zone", planZone);
            insert.Parameters.AddWithValue("@selection", json);
            insert.Parameters.AddWithValue("@enabled", start);
            insert.Parameters.AddWithValue("@next", start ? NextSlot(planZone, now).UtcDateTime : DBNull.Value);
            insert.Parameters.AddWithValue("@now", now.UtcDateTime);
            await insert.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
            return;
        }
        var wanted = MirroredEnabled(main?.Enabled, enabled);
        // The zone only follows a main plan (anchors use the main plan's zone once it exists); without one it stays.
        var newZone = main?.TimeZone ?? zone;
        var zoneChanged = !string.Equals(zone, newZone, StringComparison.Ordinal);
        if (wanted == enabled && !zoneChanged && old is not null
            && CollectionIdentity.SelectionHash(old) == CollectionIdentity.SelectionHash(selection)) return;
        // Same next-slot rule as a schedule update: none while paused, the next 09:11 when resumed or re-zoned, else unchanged.
        var retime = wanted && (!enabled || zoneChanged);
        await using var update = new MySqlCommand($"""
            UPDATE collection_schedules SET enabled=@enabled, time_zone=@zone, selection_snapshot=@selection,
                {(!wanted ? "next_slot_at=NULL, " : retime ? "next_slot_at=@next, " : "")}version=version+1, updated_at=@now
            WHERE id=UUID_TO_BIN(@id);
            """, connection, tx);
        update.Parameters.AddWithValue("@id", appId.Value.ToString());
        update.Parameters.AddWithValue("@enabled", wanted);
        update.Parameters.AddWithValue("@zone", newZone);
        update.Parameters.AddWithValue("@selection", json);
        if (retime) update.Parameters.AddWithValue("@next", NextSlot(newZone, now).UtcDateTime);
        update.Parameters.AddWithValue("@now", now.UtcDateTime);
        await update.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    private sealed record MainPlan(bool Enabled, string TimeZone);

    // Plain read: every caller either holds the site lock (which serializes schedule updates) or only needs the latest
    // committed flag.
    private static async Task<MainPlan?> MainPlanAsync(MySqlConnection connection, MySqlTransaction? tx, Guid tenant, Guid site,
        CancellationToken ct)
    {
        await using var plan = Scoped(connection, tx, """
            SELECT enabled, time_zone FROM collection_schedules
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND name=@name ORDER BY created_at, id LIMIT 1;
            """, tenant, site);
        plan.Parameters.AddWithValue("@name", PromptMonitoringIdentity.ScheduleName);
        await using var reader = await plan.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new(reader.GetBoolean(0), reader.GetString(1)) : null;
    }

    private static DateTimeOffset NextSlot(string zone, DateTimeOffset now)
        => CollectionScheduleCalendar.NextAfter("daily", null,
            CollectionScheduleCalendar.ParseLocalTime(PromptMonitoringIdentity.AppLocalTime),
            CollectionScheduleCalendar.ParseTimeZone(zone), now);

    // Same fallback as the frequency anchors (ManagementStore.BrandLocalDate): an invalid brand zone reads as Asia/Shanghai.
    private static string ValidZone(string? timeZone)
    {
        try { return CollectionScheduleCalendar.ParseTimeZone(timeZone ?? "").Id; }
        catch (CollectorStoreException) { return CollectionScheduleCalendar.ParseTimeZone("Asia/Shanghai").Id; }
    }

    private static MySqlCommand Scoped(MySqlConnection connection, MySqlTransaction? tx, string sql, Guid tenant, Guid site)
    {
        var command = new MySqlCommand(sql, connection, tx);
        command.Parameters.AddWithValue("@tenant", tenant.ToString());
        command.Parameters.AddWithValue("@site", site.ToString());
        return command;
    }
}
