using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Providers.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

/// <summary>计划、当地时隙和可恢复 Run 关联的 MySQL 实现。</summary>
/// <param name="database">业务数据库。</param>
/// <param name="keyPool">可选 Cloro key 池；提供时，含 Cloro 目标且池耗尽的时隙在预留积分前跳过。</param>
/// <param name="providerSettings">可选 API 回答 Provider 设置店；提供时，含豆包或 DeepSeek API 目标但对应 Provider 未配置的时隙按 TARGET_UNAVAILABLE 跳过。</param>
/// <param name="appChannel">可选的 App 渠道开放范围（Collector:AppTenantIds）；不提供时 App 渠道不对任何租户开放，App 计划的时隙按 APP_CHANNEL_NOT_OPEN 跳过。</param>
public sealed class CollectionScheduleStore(MySqlDatabase database, ICloroKeyPool? keyPool = null,
    IAnswerProviderSettingsStore? providerSettings = null, IOptions<AppChannelOptions>? appChannel = null) : ICollectionScheduleStore
{
    private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web);

    // Same switch as ManagementStore: closed for everyone unless the tenant is listed in Collector:AppTenantIds.
    private bool AppOpen(Guid tenantId) => appChannel?.Value.IsOpen(tenantId) == true;

    /// <inheritdoc />
    public async Task<CollectionScheduleView> CreateAsync(CreateCollectionScheduleCommand command, CancellationToken cancellationToken = default)
    {
        if (command.TenantId == Guid.Empty || command.SiteId == Guid.Empty) throw Bad("INVALID_SCHEDULE", "租户和站点不能为空。");
        var definition = Normalize(command.Name, command.Cadence, command.DayOfWeek, command.LocalTime,
            command.TimeZone, command.Selection, command.Enabled);
        var now = DateTimeOffset.UtcNow;
        var next = definition.Enabled ? Next(definition, now) : (DateTimeOffset?)null;
        var id = Guid.NewGuid();
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        if (PromptMonitoringIdentity.IsAutomaticSchedule(definition.Name))
            await GeminiResultSchema.RequireAsync(connection, null, cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        if (!await LockActiveSiteAsync(connection, tx, command.TenantId, command.SiteId, cancellationToken))
            throw new CollectorStoreException("RESOURCE_NOT_FOUND", "租户或站点不存在或未启用。", 404);
        await EnsureSelectionOwnerAsync(connection, tx, command.TenantId, command.SiteId, definition.Selection, cancellationToken);
        if (!await AreTargetsEnabledAsync(connection, tx, command.TenantId, command.SiteId,
                definition.Selection.Targets, cancellationToken))
            throw new CollectorStoreException("TARGET_NOT_ENABLED", "所选 Target 不存在、未启用或不属于当前站点。", 404);
        await using (var sql = Command(connection, tx, """
            INSERT INTO collection_schedules
                (id, tenant_id, site_id, name, cadence, day_of_week, local_time, time_zone,
                 selection_snapshot, enabled, next_slot_at, missed_slots, created_at, updated_at)
            VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), @name, @cadence, @day,
                    @local, @zone, @selection, @enabled, @next, 0, @now, @now);
            """))
        {
            sql.Parameters.AddWithValue("@id", id.ToString());
            sql.Parameters.AddWithValue("@tenant", command.TenantId.ToString());
            sql.Parameters.AddWithValue("@site", command.SiteId.ToString());
            BindDefinition(sql, definition, next);
            sql.Parameters.AddWithValue("@now", now.UtcDateTime);
            await sql.ExecuteNonQueryAsync(cancellationToken);
        }
        await tx.CommitAsync(cancellationToken);
        return new CollectionScheduleView(id, command.SiteId, definition.Name, definition.Cadence,
            definition.DayOfWeek, definition.LocalTime, definition.TimeZone, definition.Selection,
            definition.Enabled, next, 0, 1, now, now);
    }

    /// <inheritdoc />
    public async Task<CursorPage<CollectionScheduleView>> ListAsync(Guid tenantId, Guid siteId,
        string? cursor, int limit, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100) throw Bad("INVALID_LIMIT", "limit 必须在 1 到 100 之间。");
        var position = DecodeCursor(cursor, tenantId, siteId);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await EnsureSiteAsync(connection, null, tenantId, siteId, cancellationToken);
        var query = """
            SELECT BIN_TO_UUID(id), name, cadence, day_of_week, TIME_FORMAT(local_time, '%H:%i'), time_zone,
                   selection_snapshot, enabled, next_slot_at, missed_slots, version, created_at, updated_at
            FROM collection_schedules
            WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site)
            """;
        if (position is not null)
            query += " AND (created_at > @created OR (created_at = @created AND id > UUID_TO_BIN(@id)))";
        query += " ORDER BY created_at, id LIMIT @limit;";
        await using var sql = Command(connection, null, query);
        sql.Parameters.AddWithValue("@tenant", tenantId.ToString());
        sql.Parameters.AddWithValue("@site", siteId.ToString());
        sql.Parameters.AddWithValue("@limit", limit + 1);
        if (position is not null)
        {
            sql.Parameters.AddWithValue("@created", position.CreatedAt.UtcDateTime);
            sql.Parameters.AddWithValue("@id", position.Id.ToString());
        }
        var items = new List<CollectionScheduleView>();
        await using var reader = await sql.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) items.Add(ReadView(reader, siteId));
        if (items.Count <= limit) return new CursorPage<CollectionScheduleView>(items, null);
        var last = items[limit - 1];
        var next = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
            new ScheduleCursor(last.CreatedAt, last.Id, tenantId, siteId)));
        return new CursorPage<CollectionScheduleView>(items.Take(limit).ToArray(), next);
    }

    /// <inheritdoc />
    public async Task<CollectionScheduleView?> UpdateAsync(UpdateCollectionScheduleCommand command, CancellationToken cancellationToken = default)
    {
        if (command.TenantId == Guid.Empty || command.SiteId == Guid.Empty || command.ScheduleId == Guid.Empty)
            throw Bad("INVALID_SCHEDULE", "租户、站点和计划标识不能为空。");
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        if (!await LockActiveSiteAsync(connection, tx, command.TenantId, command.SiteId, cancellationToken)) return null;
        CollectionScheduleView? old;
        await using (var query = Command(connection, tx, """
            SELECT BIN_TO_UUID(id), name, cadence, day_of_week, TIME_FORMAT(local_time, '%H:%i'), time_zone,
                   selection_snapshot, enabled, next_slot_at, missed_slots, version, created_at, updated_at
            FROM collection_schedules WHERE id = UUID_TO_BIN(@id) AND tenant_id = UUID_TO_BIN(@tenant)
              AND site_id = UUID_TO_BIN(@site) FOR UPDATE;
            """))
        {
            query.Parameters.AddWithValue("@id", command.ScheduleId.ToString());
            query.Parameters.AddWithValue("@tenant", command.TenantId.ToString());
            query.Parameters.AddWithValue("@site", command.SiteId.ToString());
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            old = await reader.ReadAsync(cancellationToken) ? ReadView(reader, command.SiteId) : null;
        }
        if (old is null) return null;
        if (command.ExpectedVersion <= 0) throw Bad("INVALID_SCHEDULE_VERSION", "expectedVersion 必须是正整数。");
        if (command.ExpectedVersion != old.Version)
            throw new CollectorStoreException("SCHEDULE_VERSION_CONFLICT", "计划版本已变化，请刷新后重试。", 409);
        // App plan (design 4.1): its snapshot holds App targets, which Normalize below refuses. Only its pause switch is editable.
        if (PromptMonitoringIdentity.IsAppSchedule(old.Name))
            return await UpdateAppPlanAsync(connection, tx, command, old, cancellationToken);
        var cadence = command.Cadence?.Trim().ToLowerInvariant();
        var definition = Normalize(command.Name ?? old.Name, command.Cadence ?? old.Cadence,
            cadence == "daily" ? null : command.DayOfWeek ?? old.DayOfWeek,
            command.LocalTime ?? old.LocalTime, command.TimeZone ?? old.TimeZone,
            command.Selection ?? old.Selection, command.Enabled ?? old.Enabled);
        // A stale Prompt or Target must not prevent an operator from pausing a plan.
        if (definition.Enabled || command.Selection is not null)
        {
            await EnsureSelectionOwnerAsync(connection, tx, command.TenantId, command.SiteId, definition.Selection, cancellationToken);
            if (!await AreTargetsEnabledAsync(connection, tx, command.TenantId, command.SiteId,
                    definition.Selection.Targets, cancellationToken))
                throw new CollectorStoreException("TARGET_NOT_ENABLED", "所选 Target 不存在、未启用或不属于当前站点。", 404);
        }
        var now = DateTimeOffset.UtcNow;
        var timingChanged = definition.Cadence != old.Cadence || definition.DayOfWeek != old.DayOfWeek
            || definition.LocalTime != old.LocalTime || definition.TimeZone != old.TimeZone;
        var next = !definition.Enabled ? null : !old.Enabled || timingChanged ? Next(definition, now) : old.NextSlotAt;
        await using (var sql = Command(connection, tx, """
            UPDATE collection_schedules SET name=@name, cadence=@cadence, day_of_week=@day,
                local_time=@local, time_zone=@zone, selection_snapshot=@selection, enabled=@enabled,
                next_slot_at=@next, version=version+1, updated_at=@now
            WHERE id=UUID_TO_BIN(@id) AND tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site)
              AND version=@expected;
            """))
        {
            BindDefinition(sql, definition, next);
            sql.Parameters.AddWithValue("@now", now.UtcDateTime);
            sql.Parameters.AddWithValue("@id", command.ScheduleId.ToString());
            sql.Parameters.AddWithValue("@tenant", command.TenantId.ToString());
            sql.Parameters.AddWithValue("@site", command.SiteId.ToString());
            sql.Parameters.AddWithValue("@expected", command.ExpectedVersion);
            if (await sql.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new CollectorStoreException("SCHEDULE_VERSION_CONFLICT", "计划版本已变化，请刷新后重试。", 409);
        }
        await tx.CommitAsync(cancellationToken);
        return new CollectionScheduleView(old.Id, old.SiteId, definition.Name, definition.Cadence,
            definition.DayOfWeek, definition.LocalTime, definition.TimeZone, definition.Selection,
            definition.Enabled, next, old.MissedSlots, old.Version + 1, old.CreatedAt, now);
    }

    /// <inheritdoc />
    public async Task<int> MaterializeDueSlotsAsync(DateTimeOffset nowUtc, int limit, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        var materialized = 0;
        for (var i = 0; i < limit; i++)
        {
            await using var connection = database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await GeminiResultSchema.RequireAsync(connection, null, cancellationToken);
            // Coordinate legacy activation before taking any Schedule lock.
            // A later concurrent configuration is re-read atomically below.
            Guid? autoTenant = null, autoSite = null;
            await using (var candidate = Command(connection, null, """
                SELECT BIN_TO_UUID(tenant_id), BIN_TO_UUID(site_id) FROM collection_schedules
                WHERE enabled=TRUE AND next_slot_at <= @now AND name IN (@name, @night) ORDER BY next_slot_at,id LIMIT 1;
                """))
            {
                candidate.Parameters.AddWithValue("@now", nowUtc.UtcDateTime);
                candidate.Parameters.AddWithValue("@name", PromptMonitoringIdentity.ScheduleName);
                candidate.Parameters.AddWithValue("@night", PromptMonitoringIdentity.NightScheduleName);
                await using var reader = await candidate.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                { autoTenant = Guid.Parse(reader.GetString(0)); autoSite = Guid.Parse(reader.GetString(1)); }
            }
            if (autoTenant is { } tenant && autoSite is { } site)
            {
                await AutomaticMonitoringSelection.ReconcileAsync(database, tenant, site, cancellationToken);
                // The App plan follows at the same lazy moment (snapshot, pause mirroring, missing plan). Best effort and a
                // no-op for a tenant the App channel is not open for: it must never hold up a main or night slot.
                if (AppOpen(tenant))
                    try { await AppMonitoringPlan.ReconcileAsync(database, tenant, site, true, false, cancellationToken); }
                    catch (Exception exception) when (exception is CollectorStoreException or DbException) { }
            }
            await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            DueSchedule? due;
            await using (var query = Command(connection, tx, """
                SELECT BIN_TO_UUID(id), BIN_TO_UUID(tenant_id), BIN_TO_UUID(site_id), cadence, day_of_week,
                       TIME_FORMAT(local_time, '%H:%i'), time_zone, selection_snapshot, next_slot_at, name
                FROM collection_schedules WHERE enabled=TRUE AND next_slot_at <= @now
                ORDER BY next_slot_at, id LIMIT 1 FOR UPDATE SKIP LOCKED;
                """))
            {
                query.Parameters.AddWithValue("@now", nowUtc.UtcDateTime);
                await using var reader = await query.ExecuteReaderAsync(cancellationToken);
                due = await reader.ReadAsync(cancellationToken) ? new DueSchedule(
                    Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)), reader.GetString(3),
                    reader.IsDBNull(4) ? null : Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture), reader.GetString(5), reader.GetString(6),
                    reader.GetString(7), Utc(reader.GetDateTime(8)), reader.GetString(9)) : null;
            }
            if (due is null) return materialized;
            if (!await IsSiteActiveAsync(connection, tx, due.TenantId, due.SiteId, cancellationToken))
            {
                await using var disable = Command(connection, tx,
                    "UPDATE collection_schedules SET enabled=FALSE, next_slot_at=NULL, version=version+1, updated_at=@now WHERE id=UUID_TO_BIN(@id);");
                disable.Parameters.AddWithValue("@now", nowUtc.UtcDateTime);
                disable.Parameters.AddWithValue("@id", due.Id.ToString());
                await disable.ExecuteNonQueryAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
                continue;
            }
            var window = CollectionScheduleCalendar.DueWindow(due.Cadence, due.DayOfWeek,
                CollectionScheduleCalendar.ParseLocalTime(due.LocalTime),
                CollectionScheduleCalendar.ParseTimeZone(due.TimeZone), due.NextSlotAt, nowUtc);
            var slotSelection = due.SelectionJson;
            var planSelection = due.SelectionJson;
            string? skip = null;
            if (PromptMonitoringIdentity.SlotKind(due.Name) is { } kind)
            {
                var previous = ReadSelection(due.SelectionJson);
                // A night slot follows the main plan's membership (legacy activation keys) and sample count.
                var reference = kind == MonitoringSlotKind.Night
                    ? await MainPlanSelectionAsync(connection, tx, due.TenantId, due.SiteId, cancellationToken) ?? previous
                    : previous;
                var targets = await AutomaticMonitoringSelection.Targets(connection, tx, due.TenantId, due.SiteId, false, cancellationToken);
                var prompts = await AutomaticMonitoringSelection.Prompts(connection, tx, due.TenantId, due.SiteId, cancellationToken);
                // Frequency is decided by the slot's original local date (brand time zone), not by the materialization time.
                var slotDate = AutomaticMonitoringSelection.SlotLocalDate(window.LocalSlotKey);
                var plan = AutomaticMonitoringSelection.PlanSlot(kind, reference, prompts, targets, slotDate, window.LatestDueAt);
                skip = plan.SkipCode;
                planSelection = JsonSerializer.Serialize(plan.Current, SnapshotJson);
                var selected = plan.Selected;
                // Main slot only: prompt × cell pairs already instantly collected on the slot's local day (pending, running or
                // succeeded checks) are removed from this slot's frozen request. Without such pairs the selection object is
                // returned unchanged, so the frozen snapshot stays byte-identical. The plan snapshot (Current) is untouched.
                if (kind == MonitoringSlotKind.Main && skip is null)
                {
                    var collected = await AutomaticMonitoringSelection.CollectedPairsAsync(connection, tx, due.TenantId, due.SiteId,
                        [PromptMonitoringIdentity.InstantRequestedBy(due.SiteId)], slotDate,
                        CollectionScheduleCalendar.ParseTimeZone(due.TimeZone), cancellationToken);
                    if (collected.Count > 0)
                    {
                        var remaining = MonitoringMatrix.Without(selected, collected);
                        if (MonitoringMatrix.CountChecks(remaining) == 0) skip = AutomaticMonitoringSelection.AlreadyCollectedCode;
                        else selected = remaining;
                    }
                }
                slotSelection = JsonSerializer.Serialize(selected, SnapshotJson);
            }
            // App plan slot (design 4.1): planned by its own rules and never by the main / night branch above. A skipped
            // slot is final; a due slot stays pending for ProcessSlotAsync, which never builds an API run for it.
            string? appSkipMessage = null;
            if (PromptMonitoringIdentity.IsAppSchedule(due.Name))
            {
                (slotSelection, skip) = await AppMonitoringPlan.PlanSlotAsync(connection, tx, due.TenantId, due.SiteId,
                    AppOpen(due.TenantId), due.SelectionJson, window.LocalSlotKey, cancellationToken);
                appSkipMessage = skip is null ? null : AppMonitoringPlan.SkipMessage(skip);
            }
            await using (var insert = Command(connection, tx, """
                INSERT INTO schedule_slots
                    (id, schedule_id, local_slot_key, selection_snapshot, run_id, missed_slots, created_at, status, skip_code, skip_message)
                VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@schedule), @key, @selection, NULL, @missed, @now,
                    IF(@skip IS NULL, 'pending', 'skipped'), @skip, @message);
                """))
            {
                insert.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
                insert.Parameters.AddWithValue("@schedule", due.Id.ToString());
                insert.Parameters.AddWithValue("@key", window.LocalSlotKey);
                insert.Parameters.AddWithValue("@selection", slotSelection);
                insert.Parameters.AddWithValue("@skip", skip);
                insert.Parameters.AddWithValue("@message", appSkipMessage ?? skip switch
                {
                    null => null,
                    AutomaticMonitoringSelection.NotDueCode => "本时隙没有按采集频率到期的监控目标，未提交，也未预留积分。",
                    AutomaticMonitoringSelection.AlreadyCollectedCode => "本时隙的提问与模型当天已立即采集，未重复提交，也未预留积分。",
                    _ => "自动监控选择为空、超限或启用边界待同步，本时隙未提交。",
                });
                insert.Parameters.AddWithValue("@missed", window.MissedSlots);
                insert.Parameters.AddWithValue("@now", nowUtc.UtcDateTime);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var update = Command(connection, tx, """
                UPDATE collection_schedules SET next_slot_at=@next, missed_slots=missed_slots+@missed,
                    selection_snapshot=@selection, version=version+@changed, updated_at=@now WHERE id=UUID_TO_BIN(@id);
                """))
            {
                update.Parameters.AddWithValue("@next", window.NextSlotAt.UtcDateTime);
                update.Parameters.AddWithValue("@missed", window.MissedSlots);
                update.Parameters.AddWithValue("@now", nowUtc.UtcDateTime);
                update.Parameters.AddWithValue("@id", due.Id.ToString());
                update.Parameters.AddWithValue("@selection", planSelection);
                update.Parameters.AddWithValue("@changed", JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(planSelection),
                    JsonSerializer.Deserialize<JsonElement>(due.SelectionJson)) ? 0 : 1);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
            await tx.CommitAsync(cancellationToken);
            materialized++;
        }
        return materialized;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PendingCollectionSlot>> GetPendingSlotsAsync(int limit, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var sql = Command(connection, null, """
            SELECT BIN_TO_UUID(slot.id), BIN_TO_UUID(slot.schedule_id), BIN_TO_UUID(plan.tenant_id),
                   BIN_TO_UUID(plan.site_id), slot.local_slot_key, slot.selection_snapshot, slot.missed_slots
            FROM schedule_slots slot JOIN collection_schedules plan ON plan.id=slot.schedule_id
            WHERE slot.status='pending' AND slot.run_id IS NULL ORDER BY slot.created_at, slot.id LIMIT @limit;
            """);
        sql.Parameters.AddWithValue("@limit", limit);
        var result = new List<PendingCollectionSlot>();
        await using var reader = await sql.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new PendingCollectionSlot(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                Guid.Parse(reader.GetString(2)), Guid.Parse(reader.GetString(3)), reader.GetString(4),
                ReadSelection(reader.GetString(5)), Convert.ToInt32(reader.GetValue(6), CultureInfo.InvariantCulture)));
        }
        return result;
    }

    /// <inheritdoc />
    public async Task<bool> ProcessSlotAsync(PendingCollectionSlot slot, ICloroVisibilityStore runs, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        // Acquire Site before Schedule so ordered activation reconciliation cannot deadlock
        // with slot execution. Archived sites still need reconciliation of a committed Run.
        await using (var siteLock = Command(connection, tx, """
            SELECT s.id FROM sites s JOIN collection_schedules p ON p.site_id=s.id
            WHERE p.id=UUID_TO_BIN(@schedule) AND s.tenant_id=p.tenant_id FOR SHARE OF s;
            """))
        {
            siteLock.Parameters.AddWithValue("@schedule", slot.ScheduleId.ToString());
            await siteLock.ExecuteScalarAsync(cancellationToken);
        }
        Guid scheduleId;
        string keyPart;
        CollectionScheduleSelection selection;
        await using (var lockSlot = Command(connection, tx, """
            SELECT BIN_TO_UUID(schedule_id), local_slot_key, selection_snapshot
            FROM schedule_slots WHERE id=UUID_TO_BIN(@slot) AND status='pending' AND run_id IS NULL
            FOR UPDATE SKIP LOCKED;
            """))
        {
            lockSlot.Parameters.AddWithValue("@slot", slot.Id.ToString());
            await using var reader = await lockSlot.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return false;
            scheduleId = Guid.Parse(reader.GetString(0));
            keyPart = reader.GetString(1);
            selection = ReadSelection(reader.GetString(2));
        }
        Guid tenantId;
        Guid siteId;
        string scheduleName;
        string timeZone;
        bool enabled;
        await using (var lockSchedule = Command(connection, tx, """
            SELECT BIN_TO_UUID(tenant_id), BIN_TO_UUID(site_id), name, time_zone, enabled
            FROM collection_schedules WHERE id=UUID_TO_BIN(@schedule) FOR SHARE;
            """))
        {
            lockSchedule.Parameters.AddWithValue("@schedule", scheduleId.ToString());
            await using var reader = await lockSchedule.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("计划时隙失去计划。");
            tenantId = Guid.Parse(reader.GetString(0));
            siteId = Guid.Parse(reader.GetString(1));
            scheduleName = reader.GetString(2);
            timeZone = reader.GetString(3);
            enabled = reader.GetBoolean(4);
        }
        // App plan slot (design 4.1 / 4.2 / 4.4): it never reaches SlotRunCommand, CreateRunAsync or Normalize below — no API
        // run, no provider task, no reservation. The weekly cycle run (checks + consumer_app collector tasks) is created in
        // this transaction, so the run and the completed slot commit together.
        if (PromptMonitoringIdentity.IsAppSchedule(scheduleName))
        {
            var cycle = await AppCollectionStore.CreateCycleRunAsync(connection, tx,
                new AppCollectionStore.CycleSlot(tenantId, siteId, scheduleId, keyPart, timeZone, selection), AppOpen(tenantId),
                enabled && await IsSiteActiveForSlotAsync(connection, tx, siteId, cancellationToken), cancellationToken);
            await FinishSlotAsync(connection, tx, slot.Id, cycle.RunId is null ? "skipped" : "completed", cycle.RunId, cancellationToken,
                cycle.SkipCode, cycle.SkipMessage);
            await tx.CommitAsync(cancellationToken);
            return cycle.RunId is not null;
        }
        var automatic = PromptMonitoringIdentity.IsAutomaticSchedule(scheduleName);
        var monitoringLock = automatic
            ? await TryAcquireMonitoringLockAsync(connection, tx, siteId, cancellationToken)
            : false;
        if (automatic && !monitoringLock)
        {
            await tx.RollbackAsync(cancellationToken);
            return false;
        }
        var command = SlotRunCommand(tenantId, siteId, scheduleId, scheduleName, keyPart, selection);
        try
        {
            // A committed Run is linked even if the plan, site, or tenant was disabled after a crash.
            var existing = await runs.GetExistingRunByKeyAsync(command, cancellationToken);
            if (existing is not null)
            {
                await FinishSlotAsync(connection, tx, slot.Id, "completed", existing, cancellationToken);
                await tx.CommitAsync(cancellationToken);
                return true;
            }
            if (!enabled || !await IsSiteActiveForSlotAsync(connection, tx, siteId, cancellationToken)
                || !await AreTargetsEnabledAsync(connection, tx, tenantId, siteId, selection.Targets, cancellationToken))
            {
                await FinishSlotAsync(connection, tx, slot.Id, "skipped", null, cancellationToken,
                    "SCHEDULE_DISABLED", "监控计划、品牌或目标已停用。");
                await tx.CommitAsync(cancellationToken);
                return false;
            }
            // The first prompt-monitoring run is charged for the local day in
            // which the plan is created. If the regular 09:01 slot for that
            // same local day was materialized before or after the first run,
            // consume the slot without creating a second billable Run.
            // Main plan only: the 00:05 night round is an extra round for
            // twice_daily cells, never an initial run, and a night run does not
            // count here (HasMonitoringRunForLocalSlotAsync only looks at the
            // initial run and this main plan's own runs).
            if (scheduleName == PromptMonitoringIdentity.ScheduleName
                && await HasMonitoringRunForLocalSlotAsync(connection, tx, tenantId, siteId, scheduleId,
                    keyPart, timeZone, cancellationToken))
            {
                await FinishSlotAsync(connection, tx, slot.Id, "skipped", null, cancellationToken,
                    "INITIAL_RUN_ALREADY_EXECUTED", "本地日期已执行首轮监控，本时隙不重复执行。");
                await tx.CommitAsync(cancellationToken);
                return false;
            }
            // Skip before CreateRunAsync so an exhausted Cloro pool or an unconfigured settings-backed provider (Doubao, DeepSeek) never reserves user credits.
            if (await EvaluateProviderAvailabilityAsync(selection.Targets, keyPool, providerSettings, cancellationToken) is { } unavailable)
            {
                await FinishSlotAsync(connection, tx, slot.Id, "skipped", null, cancellationToken, unavailable.Code, unavailable.Message);
                await tx.CommitAsync(cancellationToken);
                return false;
            }
            // The slot lock serializes workers; Run creation is separately tenant-idempotent.
            // If either transaction is interrupted, the pending slot retries its frozen request.
            var run = await runs.CreateRunAsync(command, cancellationToken);
            await FinishSlotAsync(connection, tx, slot.Id, "completed", run.RunId, cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return true;
        }
        catch (CollectorStoreException exception) when (exception.StatusCode == 402)
        {
            await FinishSlotAsync(connection, tx, slot.Id, "skipped", null, cancellationToken,
                exception.Code, exception.Message);
            await tx.CommitAsync(cancellationToken);
            return false;
        }
        catch (CollectorStoreException exception) when (exception.StatusCode is 400 or 404 or 409)
        {
            // Invalid or stale selections and idempotency conflicts cannot succeed by retrying.
            await FinishSlotAsync(connection, tx, slot.Id, "skipped", null, cancellationToken,
                exception.Code, exception.Message);
            await tx.CommitAsync(cancellationToken);
            throw;
        }
        finally
        {
            if (monitoringLock) await ReleaseMonitoringLockAsync(connection, tx, siteId);
        }
    }

    // The App plan is maintained by AppMonitoringPlan; through this API only its enabled flag can change, and only for a
    // brand without a main plan (with one, pause is the main plan's switch and the App plan follows it at the next reconcile).
    private static async Task<CollectionScheduleView> UpdateAppPlanAsync(MySqlConnection connection, MySqlTransaction tx,
        UpdateCollectionScheduleCommand command, CollectionScheduleView old, CancellationToken ct)
    {
        if (command.Name is not null || command.Cadence is not null || command.DayOfWeek is not null || command.LocalTime is not null
            || command.TimeZone is not null || command.Selection is not null)
            throw Bad("INVALID_SCHEDULE", "App 采集计划由系统维护，只能修改启用状态（enabled）。");
        var enabled = command.Enabled ?? old.Enabled;
        if (enabled != old.Enabled && await MainPlanSelectionAsync(connection, tx, command.TenantId, command.SiteId, ct) is not null)
            throw new CollectorStoreException("APP_SCHEDULE_FOLLOWS_MAIN", "品牌已有每日监控计划，App 采集计划的启停跟随每日监控计划，请暂停或恢复每日监控计划。", 409);
        var now = DateTimeOffset.UtcNow;
        var next = !enabled ? null : !old.Enabled
            ? CollectionScheduleCalendar.NextAfter(old.Cadence, old.DayOfWeek, CollectionScheduleCalendar.ParseLocalTime(old.LocalTime),
                CollectionScheduleCalendar.ParseTimeZone(old.TimeZone), now)
            : old.NextSlotAt;
        await using (var sql = Command(connection, tx, """
            UPDATE collection_schedules SET enabled=@enabled, next_slot_at=@next, version=version+1, updated_at=@now
            WHERE id=UUID_TO_BIN(@id) AND tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND version=@expected;
            """))
        {
            sql.Parameters.AddWithValue("@enabled", enabled);
            sql.Parameters.AddWithValue("@next", next is null ? DBNull.Value : next.Value.UtcDateTime);
            sql.Parameters.AddWithValue("@now", now.UtcDateTime);
            sql.Parameters.AddWithValue("@id", command.ScheduleId.ToString());
            sql.Parameters.AddWithValue("@tenant", command.TenantId.ToString());
            sql.Parameters.AddWithValue("@site", command.SiteId.ToString());
            sql.Parameters.AddWithValue("@expected", command.ExpectedVersion);
            if (await sql.ExecuteNonQueryAsync(ct) != 1)
                throw new CollectorStoreException("SCHEDULE_VERSION_CONFLICT", "计划版本已变化，请刷新后重试。", 409);
        }
        await tx.CommitAsync(ct);
        return old with { Enabled = enabled, NextSlotAt = next, Version = old.Version + 1, UpdatedAt = now };
    }

    // The frozen slot request: automatic monitoring passes its per-prompt targets so the Run expands only allowed pairs.
    // Custom (non-monitoring) plans stay a full cartesian product and are never charged.
    internal static CreateCloroVisibilityRunCommand SlotRunCommand(Guid tenantId, Guid siteId, Guid scheduleId, string scheduleName,
        string localSlotKey, CollectionScheduleSelection selection)
    {
        var monitoring = PromptMonitoringIdentity.IsAutomaticSchedule(scheduleName);
        return new CreateCloroVisibilityRunCommand(tenantId, siteId, selection.PromptIds, selection.Targets,
            $"schedule:{scheduleId:N}:{localSlotKey}", $"schedule:{scheduleId:N}", selection.SampleCount, RequireEnabledTargets: true,
            ChargeMonitoringCredits: monitoring, PromptTargetKeys: monitoring && selection.PromptTargets is { Count: > 0 } ? selection.PromptTargets : null);
    }

    // Advisory only: an unreadable or unmigrated pool must not block schedules; the task path still handles it.
    // A pool that is only cooling down after 429s does not skip the slot either.
    // Per-provider pre-flight for a slot: the Cloro pool rule applies only when the selection contains a Cloro target;
    // every settings-backed target (NotConfiguredCode set: doubao_api, deepseek_api) requires an enabled settings row
    // with a credential (an unmigrated 025 counts as unconfigured). Providers are checked in registry order.
    // The instant collection (PromptMonitoringService) reuses this pre-flight with its own subject in the message.
    internal static async Task<(string Code, string Message)?> EvaluateProviderAvailabilityAsync(IReadOnlyList<CloroVisibilityTarget> targets,
        ICloroKeyPool? keyPool, IAnswerProviderSettingsStore? providerSettings, CancellationToken ct, string subject = "本时隙")
    {
        var cloro = AnswerProviderDefinition.Cloro.Provider;
        if (targets.Any(t => string.Equals(t.Provider, cloro, StringComparison.OrdinalIgnoreCase)) && await IsKeyPoolExhaustedAsync(keyPool, ct))
            return ("CLORO_KEY_POOL_EXHAUSTED", $"Cloro 积分池已耗尽，{subject}未提交，也未预留积分。");
        foreach (var definition in AnswerProviderDefinition.All.Where(d => d.NotConfiguredCode is not null))
        {
            if (targets.Any(t => string.Equals(t.Provider, definition.Provider, StringComparison.OrdinalIgnoreCase))
                && !await IsApiProviderConfiguredAsync(providerSettings, definition.Provider, ct))
                return ("TARGET_UNAVAILABLE", $"{definition.DisplayName}服务尚未配置或未启用，{subject}未提交，也未预留积分。");
        }
        return null;
    }

    // A missing settings store is advisory only (like a missing key pool); an unmigrated or unconfigured row is not configured.
    internal static async Task<bool> IsApiProviderConfiguredAsync(IAnswerProviderSettingsStore? providerSettings, string provider, CancellationToken ct)
    {
        if (providerSettings is null) return true;
        try { return await providerSettings.GetConfigurationAsync(provider, ct) is not null; }
        catch (CollectorStoreException exception) when (exception.Code == AnswerProviderSchema.NotMigratedCode) { return false; }
    }

    internal static async Task<bool> IsKeyPoolExhaustedAsync(ICloroKeyPool? keyPool, CancellationToken ct)
    {
        if (keyPool is null) return false;
        try
        {
            var availability = await keyPool.GetAvailabilityAsync(ct);
            return availability.CreditsExhausted;
        }
        catch (Exception exception) when (exception is CollectorStoreException or DbException) { return false; }
    }

    private static async Task<bool> TryAcquireMonitoringLockAsync(MySqlConnection connection, MySqlTransaction tx,
        Guid siteId, CancellationToken ct)
    {
        // Ensure holds this named lock before reconciling Site/Target/Schedule.
        // Never wait while holding our Site/Slot/Schedule locks: yield the pending
        // slot so initialization can finish, then retry on a later worker pass.
        // GET_LOCK is session-scoped and survives rollback; tx is passed only
        // because MySqlConnector requires commands to carry the active transaction.
        await using var acquire = Command(connection, tx, "SELECT GET_LOCK(@name, 0);");
        acquire.Parameters.AddWithValue("@name", $"prompt-monitor:{siteId:N}");
        return Convert.ToInt32(await acquire.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) == 1;
    }

    private static async Task ReleaseMonitoringLockAsync(MySqlConnection connection, MySqlTransaction tx, Guid siteId)
    {
        // Normal paths release after commit (tx.Connection is null); failure paths
        // release while tx is still active. Never cancel, or the session keeps the lock.
        await using var release = Command(connection, tx.Connection is null ? null : tx, "SELECT RELEASE_LOCK(@name);");
        release.Parameters.AddWithValue("@name", $"prompt-monitor:{siteId:N}");
        await release.ExecuteScalarAsync(CancellationToken.None);
    }

    private static async Task FinishSlotAsync(MySqlConnection connection, MySqlTransaction tx, Guid slotId,
        string status, Guid? runId, CancellationToken ct, string? skipCode = null, string? skipMessage = null)
    {
        await using var finish = Command(connection, tx, """
            UPDATE schedule_slots SET status=@status,
                run_id=CASE WHEN @run IS NULL THEN NULL ELSE UUID_TO_BIN(@run) END,
                skip_code=@skip_code, skip_message=@skip_message
            WHERE id=UUID_TO_BIN(@slot) AND status='pending';
            """);
        finish.Parameters.AddWithValue("@status", status);
        finish.Parameters.AddWithValue("@run", runId is null ? DBNull.Value : runId.Value.ToString());
        finish.Parameters.AddWithValue("@skip_code", skipCode ?? (object)DBNull.Value);
        finish.Parameters.AddWithValue("@skip_message", skipMessage ?? (object)DBNull.Value);
        finish.Parameters.AddWithValue("@slot", slotId.ToString());
        if (await finish.ExecuteNonQueryAsync(ct) != 1)
            throw new CollectorStoreException("SCHEDULE_SLOT_CONFLICT", "时隙已由其他 Worker 处理。", 409);
    }

    private static async Task<CollectionScheduleSelection?> MainPlanSelectionAsync(MySqlConnection connection, MySqlTransaction tx,
        Guid tenantId, Guid siteId, CancellationToken ct)
    {
        await using var query = Command(connection, tx, """
            SELECT selection_snapshot FROM collection_schedules
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND name=@name LIMIT 1;
            """);
        query.Parameters.AddWithValue("@tenant", tenantId.ToString());
        query.Parameters.AddWithValue("@site", siteId.ToString());
        query.Parameters.AddWithValue("@name", PromptMonitoringIdentity.ScheduleName);
        return await query.ExecuteScalarAsync(ct) is string json ? ReadSelection(json) : null;
    }

    private static async Task<bool> HasMonitoringRunForLocalSlotAsync(MySqlConnection connection, MySqlTransaction tx,
        Guid tenantId, Guid siteId, Guid scheduleId, string localSlotKey, string timeZone, CancellationToken ct)
    {
        if (localSlotKey.Length < 8 || !DateOnly.TryParseExact(localSlotKey[..8], "yyyyMMdd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var localDate))
            return false;

        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        await using var query = Command(connection, tx, """
            SELECT created_at FROM visibility_runs
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site)
              AND requested_by IN (@initial, @scheduled)
            ORDER BY created_at DESC LIMIT 32;
            """);
        query.Parameters.AddWithValue("@tenant", tenantId.ToString());
        query.Parameters.AddWithValue("@site", siteId.ToString());
        query.Parameters.AddWithValue("@initial", $"prompt-monitor:{siteId:N}");
        query.Parameters.AddWithValue("@scheduled", $"schedule:{scheduleId:N}");
        await using var reader = await query.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var createdUtc = Utc(reader.GetDateTime(0));
            if (TimeZoneInfo.ConvertTime(createdUtc, zone).Date == localDate.ToDateTime(TimeOnly.MinValue).Date)
                return true;
        }
        return false;
    }

    private static ScheduleDefinition Normalize(string name, string cadence, int? dayOfWeek, string localTime,
        string timeZone, CollectionScheduleSelection selection, bool enabled)
    {
        name = name?.Trim() ?? "";
        if (name.Length is < 1 or > 200) throw Bad("INVALID_SCHEDULE_NAME", "计划名称长度必须在 1 到 200 之间。");
        // The App plan is created and maintained by AppMonitoringPlan only; no plan can be created as, or renamed to, its name.
        if (PromptMonitoringIdentity.IsAppSchedule(name)) throw Bad("INVALID_SCHEDULE_NAME", "该计划名称为 App 采集计划保留，不能使用。");
        cadence = cadence?.Trim().ToLowerInvariant() ?? "";
        var time = CollectionScheduleCalendar.ParseLocalTime(localTime ?? "");
        var zone = CollectionScheduleCalendar.ParseTimeZone(timeZone ?? "");
        _ = CollectionScheduleCalendar.NextAfter(cadence, dayOfWeek, time, zone, DateTimeOffset.UtcNow);
        if (selection is null) throw Bad("INVALID_SCHEDULE_SELECTION", "计划选择不能为空。");
        var monitoring = PromptMonitoringIdentity.IsAutomaticSchedule(name);
        // Per-prompt targets belong to the automatic monitoring plans (main and night) only; custom plans stay a full cartesian product.
        var scoped = monitoring && selection.PromptTargets is { Count: > 0 };
        var validate = !(monitoring && !enabled);
        if (validate
            && (selection.PromptIds is not { Count: > 0 and <= 200 }
            || selection.Targets is not { Count: > 0 and <= 20 } || selection.SampleCount is < 1 or > 10
            || selection.PromptIds.Any(id => id == Guid.Empty)
            || selection.PromptIds.Distinct().Count() != selection.PromptIds.Count
            || !scoped && (long)selection.PromptIds.Count * selection.Targets.Count * selection.SampleCount > 1000))
            throw Bad("INVALID_SCHEDULE_SELECTION", "计划必须选择有效 Prompt、目标和 1–10 个样本，且最多 1000 个 Check。");
        var targets = new List<CloroVisibilityTarget>();
        foreach (var target in selection.Targets)
        {
            if (target is null || AnswerProviderDefinition.TryGet(target.Provider) is not { } definition)
                throw Bad("TARGET_NOT_ALLOWED", "计划只支持已注册的 API 回答 Provider（cloro、doubao_api、deepseek_api）目标。");
            var engine = target.Engine?.Trim().ToLowerInvariant() ?? "";
            if (!definition.SupportsEngine(engine))
                throw Bad("UNSUPPORTED_ENGINE", definition.Provider == "cloro"
                    ? "Cloro 引擎不受支持。" : $"{definition.DisplayName}只支持 {string.Join(" / ", definition.Engines)} 引擎。");
            var country = (target.Country ?? definition.DefaultCountry).Trim().ToUpperInvariant();
            try
            {
                if (country.Length != 2 || new RegionInfo(country).TwoLetterISORegionName != country) throw new ArgumentException();
            }
            catch (ArgumentException) { throw Bad("INVALID_COUNTRY", "country 必须为两位 ISO 国家代码。"); }
            string? locale = null;
            if (target.Locale is not null)
            {
                try { locale = CultureInfo.GetCultureInfo(target.Locale.Trim()).Name; }
                catch (CultureNotFoundException) { throw Bad("INVALID_LOCALE", "locale 无效。"); }
                if (string.IsNullOrWhiteSpace(locale) || locale.Length > 16) throw Bad("INVALID_LOCALE", "locale 无效。");
            }
            if (definition.PinnedSurface)
            {
                locale ??= definition.DefaultLocale;
                if (country != definition.DefaultCountry || !string.Equals(locale, definition.DefaultLocale, StringComparison.Ordinal))
                    throw Bad("TARGET_NOT_ALLOWED", $"{definition.DisplayName}目标仅支持 {definition.DefaultCountry}/{definition.DefaultLocale}。");
            }
            if (target.OptionsJson is not null)
            {
                try
                {
                    using var options = JsonDocument.Parse(target.OptionsJson);
                    if (options.RootElement.ValueKind != JsonValueKind.Object || options.RootElement.EnumerateObject().Any()) throw new JsonException();
                }
                catch (JsonException) { throw Bad("INVALID_OPTIONS", "本期 options 只允许空对象。"); }
            }
            // Keep the answer mode (default folded to null, unsupported → 400 ANSWER_MODE_UNSUPPORTED); fast and deep of
            // one engine are two targets with different keys.
            targets.Add(AnswerModeRules.Normalize(new CloroVisibilityTarget(definition.Provider, engine, locale, country, "{}", target.AnswerMode)));
        }
        if (targets.Select(t => $"{t.Provider}|{t.Engine}|{t.Country}|{t.Locale ?? ""}|{t.AnswerMode ?? ""}").Distinct(StringComparer.Ordinal).Count() != targets.Count)
            throw Bad("DUPLICATE_TARGET", "同一目标不可重复选择。");
        // The mode is the last sort key, so selections with default modes only keep their historical order and hash.
        var normalizedSelection = new CollectionScheduleSelection(selection.PromptIds.Order().ToArray(),
            targets.OrderBy(t => t.Provider, StringComparer.Ordinal).ThenBy(t => t.Engine, StringComparer.Ordinal)
                .ThenBy(t => t.Country, StringComparer.Ordinal).ThenBy(t => t.Locale, StringComparer.Ordinal)
                .ThenBy(t => t.AnswerMode ?? "", StringComparer.Ordinal).ToArray(), selection.SampleCount);
        if (scoped)
        {
            if (!MonitoringMatrix.TryNormalizePromptTargets(selection.PromptTargets, normalizedSelection.PromptIds,
                    normalizedSelection.Targets, out var promptTargets))
                throw Bad("INVALID_SCHEDULE_SELECTION", "自定义平台范围必须属于计划中的 Prompt 与目标。");
            normalizedSelection = normalizedSelection with { PromptTargets = promptTargets };
            if (validate && MonitoringMatrix.CountChecks(normalizedSelection) > MonitoringMatrix.MaxChecks)
                throw Bad("INVALID_SCHEDULE_SELECTION", "计划必须选择有效 Prompt、目标和 1–10 个样本，且最多 1000 个 Check。");
        }
        return new ScheduleDefinition(name, cadence, dayOfWeek, time.ToString("HH:mm", CultureInfo.InvariantCulture),
            zone.Id, normalizedSelection, enabled);
    }

    private static DateTimeOffset Next(ScheduleDefinition definition, DateTimeOffset afterUtc) =>
        CollectionScheduleCalendar.NextAfter(definition.Cadence, definition.DayOfWeek,
            CollectionScheduleCalendar.ParseLocalTime(definition.LocalTime),
            CollectionScheduleCalendar.ParseTimeZone(definition.TimeZone), afterUtc);

    private static void BindDefinition(MySqlCommand command, ScheduleDefinition definition, DateTimeOffset? next)
    {
        command.Parameters.AddWithValue("@name", definition.Name);
        command.Parameters.AddWithValue("@cadence", definition.Cadence);
        command.Parameters.AddWithValue("@day", (object?)definition.DayOfWeek ?? DBNull.Value);
        command.Parameters.AddWithValue("@local", definition.LocalTime);
        command.Parameters.AddWithValue("@zone", definition.TimeZone);
        command.Parameters.AddWithValue("@selection", JsonSerializer.Serialize(definition.Selection, SnapshotJson));
        command.Parameters.AddWithValue("@enabled", definition.Enabled);
        command.Parameters.AddWithValue("@next", next is null ? DBNull.Value : next.Value.UtcDateTime);
    }

    private static async Task EnsureSelectionOwnerAsync(MySqlConnection connection, MySqlTransaction tx,
        Guid tenantId, Guid siteId, CollectionScheduleSelection selection, CancellationToken ct)
    {
        await EnsureSiteAsync(connection, tx, tenantId, siteId, ct);
        await using var sql = Command(connection, tx, $"""
            SELECT COUNT(*) FROM prompts WHERE site_id=UUID_TO_BIN(@site) AND archived_at IS NULL AND enabled=TRUE
              AND id IN ({string.Join(',', selection.PromptIds.Select((_, index) => $"UUID_TO_BIN(@p{index})"))});
            """);
        sql.Parameters.AddWithValue("@site", siteId.ToString());
        for (var i = 0; i < selection.PromptIds.Count; i++) sql.Parameters.AddWithValue($"@p{i}", selection.PromptIds[i].ToString());
        if (Convert.ToInt32(await sql.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) != selection.PromptIds.Count)
            throw new CollectorStoreException("RESOURCE_NOT_FOUND", "所选 Prompt 不存在、未启用或不属于当前站点。", 404);
    }

    private static async Task<bool> AreTargetsEnabledAsync(MySqlConnection connection, MySqlTransaction tx,
        Guid tenantId, Guid siteId, IReadOnlyList<CloroVisibilityTarget> targets, CancellationToken ct)
    {
        var keys = targets.Select(CollectionIdentity.TargetKey).ToArray();
        await using var sql = Command(connection, tx, $"""
            SELECT target_key FROM site_targets
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND enabled=TRUE
              AND target_key IN ({string.Join(',', keys.Select((_, index) => $"@key{index}"))}) FOR SHARE;
            """);
        sql.Parameters.AddWithValue("@tenant", tenantId.ToString());
        sql.Parameters.AddWithValue("@site", siteId.ToString());
        for (var i = 0; i < keys.Length; i++) sql.Parameters.AddWithValue($"@key{i}", keys[i]);
        var found = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await sql.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) found.Add(reader.GetString(0));
        return keys.All(found.Contains);
    }

    private static async Task EnsureSiteAsync(MySqlConnection connection, MySqlTransaction? tx, Guid tenantId, Guid siteId, CancellationToken ct)
    {
        if (!await IsSiteActiveAsync(connection, tx, tenantId, siteId, ct))
            throw new CollectorStoreException("RESOURCE_NOT_FOUND", "租户或站点不存在或未启用。", 404);
    }

    private static async Task<bool> LockActiveSiteAsync(MySqlConnection connection, MySqlTransaction tx,
        Guid tenantId, Guid siteId, CancellationToken ct)
    {
        // Take the site lock before a schedule row lock. Brand archive takes those locks in that order.
        await using var sql = Command(connection, tx, """
            SELECT 1 FROM sites s JOIN tenants t ON t.id=s.tenant_id
            WHERE s.id=UUID_TO_BIN(@site) AND s.tenant_id=UUID_TO_BIN(@tenant)
              AND s.status='active' AND t.status='active' AND t.deleted_at IS NULL
            LIMIT 1 FOR SHARE;
            """);
        sql.Parameters.AddWithValue("@site", siteId.ToString());
        sql.Parameters.AddWithValue("@tenant", tenantId.ToString());
        return await sql.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task<bool> IsSiteActiveAsync(MySqlConnection connection, MySqlTransaction? tx,
        Guid tenantId, Guid siteId, CancellationToken ct)
    {
        await using var sql = Command(connection, tx, """
            SELECT 1 FROM sites s JOIN tenants t ON t.id=s.tenant_id
            WHERE s.id=UUID_TO_BIN(@site) AND s.tenant_id=UUID_TO_BIN(@tenant)
              AND s.status='active' AND t.status='active' AND t.deleted_at IS NULL LIMIT 1;
            """);
        sql.Parameters.AddWithValue("@site", siteId.ToString());
        sql.Parameters.AddWithValue("@tenant", tenantId.ToString());
        return await sql.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task<bool> IsSiteActiveForSlotAsync(MySqlConnection connection, MySqlTransaction tx,
        Guid siteId, CancellationToken ct)
    {
        await using var sql = Command(connection, tx,
            "SELECT status FROM sites WHERE id=UUID_TO_BIN(@site) FOR SHARE;");
        sql.Parameters.AddWithValue("@site", siteId.ToString());
        return string.Equals((string?)await sql.ExecuteScalarAsync(ct), "active", StringComparison.Ordinal);
    }

    private static CollectionScheduleView ReadView(MySqlDataReader reader, Guid siteId) => new(
        Guid.Parse(reader.GetString(0)), siteId, reader.GetString(1), reader.GetString(2),
        reader.IsDBNull(3) ? null : Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture), reader.GetString(4), reader.GetString(5),
        ReadSelection(reader.GetString(6)), reader.GetBoolean(7),
        reader.IsDBNull(8) ? null : Utc(reader.GetDateTime(8)),
        Convert.ToInt32(reader.GetValue(9), CultureInfo.InvariantCulture),
        Convert.ToInt32(reader.GetValue(10), CultureInfo.InvariantCulture),
        Utc(reader.GetDateTime(11)), Utc(reader.GetDateTime(12)));

    private static CollectionScheduleSelection ReadSelection(string json) =>
        JsonSerializer.Deserialize<CollectionScheduleSelection>(json, SnapshotJson)
        ?? throw new InvalidOperationException("计划选择快照为空。");

    private static ScheduleCursor? DecodeCursor(string? value, Guid tenantId, Guid siteId)
    {
        if (value is null) return null;
        try
        {
            if (value.Length > 2048) throw new FormatException();
            var decoded = JsonSerializer.Deserialize<ScheduleCursor>(Convert.FromBase64String(value));
            if (decoded is null || decoded.TenantId != tenantId || decoded.SiteId != siteId
                || decoded.Id == Guid.Empty || decoded.CreatedAt.Year < 1000)
                throw new FormatException();
            return decoded;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
        {
            throw Bad("INVALID_CURSOR", "游标无效或不属于当前租户和站点。");
        }
    }

    private static MySqlCommand Command(MySqlConnection connection, MySqlTransaction? tx, string sql) => new(sql, connection, tx);
    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    private static CollectorStoreException Bad(string code, string message) => new(code, message, 400);

    private sealed record ScheduleDefinition(string Name, string Cadence, int? DayOfWeek, string LocalTime,
        string TimeZone, CollectionScheduleSelection Selection, bool Enabled);
    private sealed record DueSchedule(Guid Id, Guid TenantId, Guid SiteId, string Cadence, int? DayOfWeek,
        string LocalTime, string TimeZone, string SelectionJson, DateTimeOffset NextSlotAt, string Name);
    private sealed record ScheduleCursor(DateTimeOffset CreatedAt, Guid Id, Guid TenantId, Guid SiteId);
}
