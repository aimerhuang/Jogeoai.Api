using System.Globalization;
using System.Text.Json;
using Jogeoai.Application;
using Jogeoai.Domain;
using MySqlConnector;
using static Jogeoai.Infrastructure.DomesticCollectorStore;

namespace Jogeoai.Infrastructure;

/// <summary>
/// App 采集渠道的周期 Run（design 4.2 / 4.3）：按每周时隙展开「提问 × 到期的 App 目标」，每个检测配一条 consumer_app 采集任务；
/// 截止时把未成功的检测置为未采集；提问的人工排除；App Run 的读取。
/// 这里不领取任务、不接收结果（见 <see cref="AppCollectorPoolStore"/>）；创建周期 Run 时不写任何积分记录，
/// 检测记为未采集时释放它在领取时预留的积分。不创建 provider_tasks，所以 API Worker 不会处理这些检测。
/// </summary>
/// <param name="database">业务数据库。</param>
public sealed partial class AppCollectionStore(MySqlDatabase database) : IAppCollectionStore
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>Audit reason of a frozen prompt that is no longer enabled (or was archived) when its cycle run is created.</summary>
    internal const string PromptUnavailableReason = "PROMPT_UNAVAILABLE";

    /// <summary>The due App slot being processed: its plan, its local slot key and the selection frozen at materialization.</summary>
    internal sealed record CycleSlot(Guid TenantId, Guid SiteId, Guid ScheduleId, string LocalSlotKey, string TimeZone,
        CollectionScheduleSelection Selection);

    /// <summary>How the slot ends: completed with <see cref="RunId"/>, or skipped with a code and a message.</summary>
    internal sealed record CycleOutcome(Guid? RunId, string? SkipCode, string? SkipMessage);

    /// <summary>The tenant-scoped idempotency key of a slot's cycle run; same form as every other schedule run.</summary>
    internal static string CycleRunKey(Guid scheduleId, string localSlotKey) => $"schedule:{scheduleId:N}:{localSlotKey}";

    /// <summary>
    /// Creates the weekly cycle run of one due App slot inside the caller's slot transaction (the slot row is locked, so
    /// one slot is never expanded twice concurrently). In order:
    /// <list type="number">
    /// <item>A run with the slot's idempotency key already exists → it is returned (linked), whatever happened since.</item>
    /// <item>Tenant not open → APP_CHANNEL_NOT_OPEN; plan or brand not active → SCHEDULE_DISABLED.</item>
    /// <item>Frozen prompts × frozen due App targets are expanded with the frozen custom scopes (never widened). Prompts
    /// that fail <see cref="AppPromptRules"/> on their current text, carry an active operator exclusion, or are no longer
    /// enabled produce no check. Nothing left → MONITORING_SELECTION_EMPTY.</item>
    /// <item>Platforms without an enabled consumer_app entry produce no check. Nothing left → APP_PLATFORM_UNAVAILABLE.</item>
    /// <item>One run, one run target per collected platform, one pending check and one pending consumer_app task per pair.</item>
    /// </list>
    /// No provider task, no credit reservation, no credit item and no ledger entry is written. The excluded prompts and
    /// their reasons are stored in the run's <c>selection_snapshot</c> under <c>$.app</c>.
    /// </summary>
    internal static async Task<CycleOutcome> CreateCycleRunAsync(MySqlConnection c, MySqlTransaction t, CycleSlot slot,
        bool open, bool active, CancellationToken ct)
    {
        var (tenant, site) = (slot.TenantId.ToString(), slot.SiteId.ToString());
        var key = CycleRunKey(slot.ScheduleId, slot.LocalSlotKey);
        var existing = await QuerySingleAsync(c, t,
            "SELECT BIN_TO_UUID(id) FROM visibility_runs WHERE tenant_id = UUID_TO_BIN(@tenant) AND idempotency_key = @key;",
            ct, ("@tenant", tenant), ("@key", key));
        if (existing is not null) return new(Guid.Parse(existing[0]), null, null);
        if (!open) return Skip(AppMonitoringPlan.NotOpenCode);
        if (!active) return Skip(AppMonitoringPlan.MainPausedCode);
        await AppChannelSchema.RequireAsync(c, t, ct);

        var selection = slot.Selection;
        if (selection.PromptIds.Count > MonitoringMatrix.MaxPrompts) return Skip(AppMonitoringPlan.TooLargeCode);
        // Only App targets of known platforms; anything else in a frozen App snapshot is ignored, never sent to an API run.
        var targets = selection.Targets
            .Where(target => target is not null && MonitoringProviders.IsApp(target.Provider) && AppChannelDefinition.SupportsEngine(target.Engine))
            .Select(target => AppChannelDefinition.TryGetPlatform(target.Engine)!.Code).Distinct(StringComparer.Ordinal)
            .Select(engine => AutomaticMonitoringSelection.StoredTarget(AppChannelDefinition.Provider, engine,
                AppChannelDefinition.Locale, AppChannelDefinition.Country, null))
            .ToArray();
        if (selection.PromptIds.Count == 0 || targets.Length == 0) return Skip(AppMonitoringPlan.SelectionEmptyCode);

        var brand = await QuerySingleAsync(c, t, """
            SELECT subject_config_version, target_config_version FROM site_brand_settings
            WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site) FOR SHARE;
            """, ct, ("@tenant", tenant), ("@site", site));
        if (brand is null) return new(null, "RESOURCE_NOT_FOUND", "品牌配置不存在，App 时隙未提交。");

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
        var operatorExcluded = await ActiveExclusionsAsync(c, t, slot.TenantId, slot.SiteId, ct);
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
            return new(null, AppMonitoringPlan.SelectionEmptyCode, AppMonitoringPlan.SkipMessage(AppMonitoringPlan.SelectionEmptyCode)
                + (excluded.Count == 0 ? "" : $"其中 {excluded.Count} 条提问不进入 App 渠道（{ReasonSummary(excluded)}）。"));

        var entries = await EnabledEntriesAsync(c, t, ct);
        var unavailable = pairs.Select(pair => targets[pair.TargetIndex].Engine).Distinct(StringComparer.Ordinal)
            .Where(engine => !entries.ContainsKey(engine)).ToArray();
        pairs = pairs.Where(pair => entries.ContainsKey(targets[pair.TargetIndex].Engine)).ToArray();
        if (pairs.Length == 0) return Skip(AppMonitoringPlan.PlatformUnavailableCode);

        var runId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var deadline = AppCycleRules.DeadlineUtc(slot.LocalSlotKey, CollectionScheduleCalendar.ParseTimeZone(slot.TimeZone));
        var configVersion = int.Parse(brand[0], CultureInfo.InvariantCulture);
        var ranks = AppCycleRules.FairRanks(creationOrder, pairs.Select(pair => (pair.PromptId, targets[pair.TargetIndex].Engine)).ToArray());
        var audit = JsonSerializer.Serialize(new
        {
            selection.PromptIds,
            selection.Targets,
            selection.SampleCount,
            SubjectConfigVersion = configVersion,
            selection.PromptTargets,
            App = new
            {
                SlotKey = slot.LocalSlotKey,
                DeadlineAt = deadline,
                PromptRuleVersion = AppPromptRules.RuleVersion,
                Checks = pairs.Length,
                ExcludedPromptCount = excluded.Count,
                ExcludedByReason = excluded.GroupBy(item => item.Reason, StringComparer.Ordinal)
                    .OrderBy(group => group.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count()),
                ExcludedPrompts = excluded.Select(item => new { item.PromptId, ReasonCode = item.Reason }).ToArray(),
                UnavailablePlatforms = unavailable,
            },
        }, Web);
        await ExecuteAsync(c, t, """
            INSERT INTO visibility_runs (id, tenant_id, site_id, status, idempotency_key, requested_by, requested_at,
                total_checks, request_hash, subject_config_version, target_config_version, selection_snapshot, created_at, updated_at)
            VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), 'queued', @key, @by, @now, @total, @hash,
                @version, @target_version, @selection, @now, @now);
            """, ct, ("@id", runId.ToString()), ("@tenant", tenant), ("@site", site), ("@key", key),
            ("@by", $"schedule:{slot.ScheduleId:N}"), ("@now", now), ("@total", pairs.Length),
            ("@hash", CollectionIdentity.SelectionHash(selection)), ("@version", configVersion),
            ("@target_version", int.Parse(brand[1], CultureInfo.InvariantCulture)), ("@selection", audit));

        // One run target per platform that is actually collected, in the frozen target order.
        var runTargets = new Dictionary<int, Guid>();
        foreach (var index in pairs.Select(pair => pair.TargetIndex).Distinct().Order())
        {
            var targetId = Guid.NewGuid();
            await ExecuteAsync(c, t, """
                INSERT INTO run_engines (id, run_id, engine, provider, request_options, sort_order, target_key,
                    capture_mode, country, locale, created_at)
                VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@run), @engine, @provider, JSON_OBJECT(), @sort, @target_key,
                    @capture, @country, @locale, @now);
                """, ct, ("@id", targetId.ToString()), ("@run", runId.ToString()), ("@engine", targets[index].Engine),
                ("@provider", AppChannelDefinition.Provider), ("@sort", runTargets.Count),
                ("@target_key", CollectionIdentity.AppTargetKey(targets[index].Engine)), ("@capture", AppChannelDefinition.CaptureMode),
                ("@country", AppChannelDefinition.Country), ("@locale", AppChannelDefinition.Locale), ("@now", now));
            runTargets[index] = targetId;
        }

        for (var i = 0; i < pairs.Length; i++)
        {
            var engine = targets[pairs[i].TargetIndex].Engine;
            var prompt = prompts[pairs[i].PromptId];
            var checkId = Guid.NewGuid();
            await ExecuteAsync(c, t, """
                INSERT INTO visibility_checks (id, tenant_id, site_id, run_id, prompt_id, engine, provider, status, prompt_snapshot,
                    capture_mode, evidence_class, locale, country, answer_present, run_target_id, sample_index,
                    prompt_version_id, prompt_tags_snapshot, branded_status_snapshot, branded_source_snapshot, subject_config_version,
                    snapshot_missing, answer_mode, created_at, updated_at)
                VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), UUID_TO_BIN(@run), UUID_TO_BIN(@prompt),
                    @engine, @provider, 'pending', @snapshot, @capture, @evidence, @locale, @country, FALSE,
                    UUID_TO_BIN(@target), 1, UUID_TO_BIN(@version_id), @tags, @branded, @source, @config_version,
                    FALSE, @answer_mode, @now, @now);
                """, ct, ("@id", checkId.ToString()), ("@tenant", tenant), ("@site", site), ("@run", runId.ToString()),
                ("@prompt", prompt[0]), ("@engine", engine), ("@provider", AppChannelDefinition.Provider), ("@snapshot", prompt[1]),
                ("@capture", AppChannelDefinition.CaptureMode), ("@evidence", AppChannelDefinition.EvidenceClass),
                ("@locale", AppChannelDefinition.Locale), ("@country", AppChannelDefinition.Country),
                ("@target", runTargets[pairs[i].TargetIndex].ToString()), ("@version_id", prompt[2].Length == 0 ? null : prompt[2]),
                ("@tags", prompt[5]), ("@branded", prompt[3]), ("@source", prompt[4]), ("@config_version", configVersion),
                ("@answer_mode", AnswerModeRules.ToStorage(AppChannelDefinition.DefaultAnswerMode)), ("@now", now));
            // The question is the prompt text as it is: no prefix, no rewrite, no trimming.
            await ExecuteAsync(c, t, """
                INSERT INTO collector_tasks (id, tenant_id, run_id, check_id, platform_entry_id, platform_code, region, language,
                    channel, question_snapshot, status, next_attempt_at, deadline_at, fair_rank, prompt_rule_version, created_at, updated_at)
                VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), UUID_TO_BIN(@run), UUID_TO_BIN(@check), UUID_TO_BIN(@entry),
                    @platform, 'cn', @language, @channel, @question, 'pending', @now, @deadline, @rank, @rule, @now, @now);
                """, ct, ("@id", Guid.NewGuid().ToString()), ("@tenant", tenant), ("@run", runId.ToString()), ("@check", checkId.ToString()),
                ("@entry", entries[engine].ToString()), ("@platform", engine), ("@language", AppChannelDefinition.Locale),
                ("@channel", AppChannelDefinition.CollectorChannel), ("@question", prompt[1]), ("@now", now),
                ("@deadline", deadline.UtcDateTime), ("@rank", ranks[i]), ("@rule", AppPromptRules.RuleVersion));
        }
        return new(runId, null, null);
    }

    /// <summary>
    /// Recomputes an App cycle run from its checks (design 4.3, <see cref="AppCycleRules.RunStatus"/>). The run row is locked
    /// first and the counts are read by a later statement, so under READ COMMITTED two transactions finishing checks of
    /// the same run cannot both store a stale status. <c>failed_checks</c> counts failed checks only, never cancelled ones.
    /// </summary>
    internal static async Task UpdateRunStatusAsync(MySqlConnection c, MySqlTransaction t, Guid runId, CancellationToken ct)
    {
        if (await QuerySingleAsync(c, t, "SELECT 1 FROM visibility_runs WHERE id = UUID_TO_BIN(@run) FOR UPDATE;", ct,
                ("@run", runId.ToString())) is null) return;
        var counts = await QuerySingleAsync(c, t, """
            SELECT COUNT(*), COALESCE(SUM(status = 'pending'), 0), COALESCE(SUM(status = 'running'), 0),
                   COALESCE(SUM(status = 'succeeded'), 0), COALESCE(SUM(status = 'failed'), 0), COALESCE(SUM(status = 'cancelled'), 0)
            FROM visibility_checks WHERE run_id = UUID_TO_BIN(@run);
            """, ct, ("@run", runId.ToString()));
        int Count(int index) => int.Parse(counts![index], CultureInfo.InvariantCulture);
        var total = Count(0);
        if (total == 0) return;
        var open = Count(1) + Count(2);
        await ExecuteAsync(c, t, """
            UPDATE visibility_runs
            SET status = @status, succeeded_checks = @succeeded, failed_checks = @failed,
                started_at = CASE WHEN @touched THEN COALESCE(started_at, UTC_TIMESTAMP(6)) ELSE started_at END,
                completed_at = CASE WHEN @open = 0 THEN COALESCE(completed_at, UTC_TIMESTAMP(6)) ELSE NULL END,
                updated_at = UTC_TIMESTAMP(6)
            WHERE id = UUID_TO_BIN(@run);
            """, ct, ("@status", AppCycleRules.RunStatus(total, open, Count(3), Count(4), Count(5))), ("@succeeded", Count(3)),
            ("@failed", Count(4)), ("@touched", Count(1) < total), ("@open", open), ("@run", runId.ToString()));
    }

    /// <summary>Prompts of the brand with an active (not revoked) operator exclusion from the App channel.</summary>
    internal static async Task<HashSet<Guid>> ActiveExclusionsAsync(MySqlConnection c, MySqlTransaction? t, Guid tenantId, Guid siteId,
        CancellationToken ct)
        => (await QueryRowsAsync(c, t, """
                SELECT BIN_TO_UUID(prompt_id) FROM prompt_app_exclusions
                WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site) AND revoked_at IS NULL;
                """, ct, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString())))
            .Select(row => Guid.Parse(row[0])).ToHashSet();

    // The enabled consumer_app entry of each platform (highest enabled version). collector_platforms.enabled is the web
    // channel's switch and is not read.
    private static async Task<Dictionary<string, Guid>> EnabledEntriesAsync(MySqlConnection c, MySqlTransaction t, CancellationToken ct)
    {
        var entries = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var row in await QueryRowsAsync(c, t, """
            SELECT platform_code, BIN_TO_UUID(id) FROM collector_platform_entries
            WHERE channel = 'consumer_app' AND region = 'cn' AND enabled = TRUE ORDER BY platform_code, version DESC;
            """, ct))
            entries.TryAdd(row[0], Guid.Parse(row[1]));
        return entries;
    }

    private static CycleOutcome Skip(string code) => new(null, code, AppMonitoringPlan.SkipMessage(code));

    private static string ReasonSummary(IEnumerable<(Guid PromptId, string Reason)> excluded)
        => string.Join("，", excluded.GroupBy(item => item.Reason, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{group.Key} × {group.Count()}"));
}
