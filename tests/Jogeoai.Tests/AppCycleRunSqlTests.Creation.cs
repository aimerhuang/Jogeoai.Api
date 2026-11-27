using System.Text.Json;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using static Jogeoai.Tests.TeamMonitoringConfigSqlTests;

namespace Jogeoai.Tests;

/// <summary>周期 Run 的创建（implement 4.3 / 4.4）：展开、列值、幂等、提问范围、提问检查与人工排除、平台不可用。</summary>
public sealed partial class AppCycleRunSqlTests
{
    private const string NoCredits = "providerTasks=0|collectorTasks={0}|reservations=0|items=0|ledger=0|balance=1000.000";

    private static string Footprint(int runs, int checks) => $"runs={runs}|checks={checks}|" + string.Format(NoCredits, checks);

    // A due slot of an open tenant: one run, one run target per platform, one pending check and one consumer_app task per
    // prompt × platform pair, honouring the custom scopes. Nothing for the API workers, nothing for the credit tables.
    [AppChannelMySqlFact]
    public Task ADueSlotBecomesOneRunWithTheScopedChecksAndTasksAndAReplayLinksIt() => AppSiteTargetsSqlTests.IsolatedAsync(async (cs, seedTenant) =>
    {
        var rig = Build(cs, await seedTenant());
        var site = await SeedAppBrandAsync(rig, "周期 Run", "doubao", "kimi");
        // Created in this order: kimiOnly, apiOnly, all, elsewhere.
        var all = await SeedPromptAsync(cs, site, "  哪个记账工具好用？", 3);
        var kimiOnly = await SeedPromptAsync(cs, site, "预算管理应用怎么选？", 1);
        var apiOnly = await SeedPromptAsync(cs, site, "只在 API 渠道采集的提问", 2);
        var elsewhere = await SeedPromptAsync(cs, site, "范围里的 App 平台没有启用", 4);
        await ScopeAsync(cs, rig.Tenant, site, kimiOnly, AppKey("kimi"));
        await ScopeAsync(cs, rig.Tenant, site, apiOnly, ChatGptKey);
        await ScopeAsync(cs, rig.Tenant, site, elsewhere, AppKey("qwen"));
        var plan = await StartAsync(rig, site);
        Assert.Equal(Footprint(0, 0), await FootprintAsync(cs, rig.Tenant));

        var slot = await RunSlotAsync(rig, plan, 3);
        Assert.Equal(("completed", null, null), (slot.Status, slot.SkipCode, slot.SkipMessage));
        var run = slot.RunId!.Value;

        // 'all' is collected on both platforms, the prompt scoped to kimi only there, the prompt scoped to an API target and
        // the prompt scoped to a platform the brand has not enabled not at all (an empty intersection is never widened).
        // fair_rank is the prompt's ordinal within the platform in creation order.
        Assert.Equal([Pair(all, "doubao", 1), Pair(kimiOnly, "kimi", 1), Pair(all, "kimi", 2)], await PairsAsync(cs, run));
        Assert.Equal("queued|3|0|0|0|0", await RunAsync(cs, run));
        Assert.Equal($"schedule:{plan:N}|schedule:{plan:N}:20000103T0911", await RowAsync(cs,
            "SELECT requested_by, idempotency_key FROM visibility_runs WHERE id = UUID_TO_BIN(@run);", ("@run", run.ToString())));

        var targets = await RowsAsync(cs, """
            SELECT CONCAT_WS('|', engine, provider, capture_mode, country, locale, target_key), sort_order FROM run_engines
            WHERE run_id = UUID_TO_BIN(@run) ORDER BY engine;
            """, ("@run", run.ToString()));
        Assert.Equal([$"doubao|app_collector|app|CN|zh-CN|{AppKey("doubao")}", $"kimi|app_collector|app|CN|zh-CN|{AppKey("kimi")}"], targets.Select(r => r[0]!));
        Assert.Equal(["0", "1"], targets.Select(r => r[1]!).Order());

        var configVersion = (await RowsAsync(cs, "SELECT subject_config_version FROM site_brand_settings WHERE site_id = UUID_TO_BIN(@site);",
            ("@site", site.ToString())))[0][0];
        Assert.Equal($"pending|app_collector|app|observed_surface|fast|CN|zh-CN|1|False|False|unbranded|system|{configVersion}|3", await RowAsync(cs, """
            SELECT status, provider, capture_mode, evidence_class, answer_mode, country, locale, sample_index, answer_present, snapshot_missing,
                   branded_status_snapshot, branded_source_snapshot, subject_config_version, COUNT(*)
            FROM visibility_checks WHERE run_id = UUID_TO_BIN(@run) GROUP BY 1,2,3,4,5,6,7,8,9,10,11,12,13;
            """, ("@run", run.ToString())));
        // Prompt snapshots as on an API check: the prompt text as it is (not trimmed), its current version and tags.
        Assert.Equal(3, await CountAsync(cs, """
            SELECT COUNT(*) FROM visibility_checks c JOIN prompts p ON p.id = c.prompt_id
            JOIN run_engines e ON e.id = c.run_target_id AND e.run_id = c.run_id
            WHERE c.run_id = UUID_TO_BIN(@run) AND c.tenant_id = UUID_TO_BIN(@tenant) AND c.site_id = UUID_TO_BIN(@site)
              AND c.prompt_snapshot = p.content AND c.prompt_version_id = p.current_version_id AND e.engine = c.engine
              AND JSON_LENGTH(c.prompt_tags_snapshot) = 0 AND c.answer IS NULL AND c.collector_task_id IS NULL
              AND c.app_binding_id IS NULL AND c.error_code IS NULL;
            """, ("@run", run.ToString()), ("@tenant", rig.Tenant.ToString()), ("@site", site.ToString())));
        Assert.Equal("  哪个记账工具好用？", (await RowsAsync(cs,
            "SELECT question_snapshot FROM collector_tasks WHERE run_id = UUID_TO_BIN(@run) AND platform_code = 'doubao';", ("@run", run.ToString())))[0][0]);
        // One task per check: the App channel, the platform's enabled consumer_app entry, a 7-day deadline, the rule version.
        Assert.Equal(3, await CountAsync(cs, """
            SELECT COUNT(*) FROM collector_tasks t JOIN visibility_checks c ON c.id = t.check_id
            JOIN collector_platform_entries e ON e.id = t.platform_entry_id
            WHERE t.run_id = UUID_TO_BIN(@run) AND t.tenant_id = UUID_TO_BIN(@tenant) AND t.channel = 'consumer_app'
              AND t.region = 'cn' AND t.language = 'zh-CN' AND t.platform_code = c.engine AND t.question_snapshot = c.prompt_snapshot
              AND t.status = 'pending' AND t.attempt_count = 0 AND t.failure_count = 0 AND t.lease_version = 0
              AND t.lease_owner IS NULL AND t.lease_until IS NULL AND t.lease_app_binding_id IS NULL AND t.lease_session_profile_id IS NULL
              AND t.last_block_reason IS NULL AND t.last_error_code IS NULL AND t.completed_at IS NULL
              AND t.next_attempt_at <= UTC_TIMESTAMP(6) AND t.next_attempt_at = t.created_at
              AND t.deadline_at = '2000-01-10 01:11:00' AND t.prompt_rule_version = @rule
              AND e.entry_code = CONCAT(t.platform_code, '.consumer_app') AND e.channel = 'consumer_app' AND e.region = 'cn' AND e.enabled = TRUE;
            """, ("@run", run.ToString()), ("@tenant", rig.Tenant.ToString()), ("@rule", AppPromptRules.RuleVersion)));

        // No provider task, no reservation, no credit item, no ledger entry, the balance untouched; no outbox event and no
        // analysis arrival either.
        Assert.Equal(Footprint(1, 3), await FootprintAsync(cs, rig.Tenant));
        Assert.Equal(0, await CountAsync(cs, """
            SELECT (SELECT COUNT(*) FROM outbox_events WHERE tenant_id = UUID_TO_BIN(@tenant))
                 + (SELECT COUNT(*) FROM analytics_check_arrivals WHERE tenant_id = UUID_TO_BIN(@tenant))
                 + (SELECT COUNT(*) FROM collector_attempts WHERE tenant_id = UUID_TO_BIN(@tenant));
            """, ("@tenant", rig.Tenant.ToString())));
        var audit = await RunAuditAsync(cs, run);
        Assert.Equal(("20000103T0911", 3, 0, AppPromptRules.RuleVersion), (audit.GetProperty("slotKey").GetString(), audit.GetProperty("checks").GetInt32(),
            audit.GetProperty("excludedPromptCount").GetInt32(), audit.GetProperty("promptRuleVersion").GetInt32()));
        Assert.Equal(Utc(10, 1, 11), audit.GetProperty("deadlineAt").GetDateTimeOffset());

        // Replay of the same slot (as after a crash between the run and the slot): the existing run is linked, by the open
        // worker and by a worker the tenant is not open for. Never a second run.
        foreach (var worker in new[] { rig, Build(cs, rig.Tenant, open: false) })
        {
            await ExecAsync(cs, "UPDATE schedule_slots SET status = 'pending', run_id = NULL WHERE id = UUID_TO_BIN(@slot);", ("@slot", slot.Id.ToString()));
            var pending = Assert.Single((await worker.Schedules.GetPendingSlotsAsync(100)).Regular(), s => s.Id == slot.Id);
            Assert.True(await worker.Schedules.ProcessSlotAsync(pending, worker.Runs));
            Assert.Equal(slot, await SlotAsync(cs, plan, "20000103T0911"));
            Assert.Equal(Footprint(1, 3), await FootprintAsync(cs, rig.Tenant));
        }

        // The next cycle, a week later, is its own run with its own key and deadline.
        var second = await RunSlotAsync(rig, plan, 10);
        Assert.NotEqual(run, second.RunId!.Value);
        Assert.Equal($"schedule:{plan:N}:20000110T0911|3", await RowAsync(cs,
            "SELECT idempotency_key, total_checks FROM visibility_runs WHERE id = UUID_TO_BIN(@run);", ("@run", second.RunId.Value.ToString())));
        Assert.Equal(3, await CountAsync(cs, "SELECT COUNT(*) FROM collector_tasks WHERE run_id = UUID_TO_BIN(@run) AND deadline_at = '2000-01-17 01:11:00';",
            ("@run", second.RunId.Value.ToString())));
        Assert.Equal(Footprint(2, 6), await FootprintAsync(cs, rig.Tenant));

        // Unchanged from 4A: a day without a due target and a tenant the worker is not open for are skipped at
        // materialization, and nothing is created for them.
        Assert.Null(await MaterializeAsync(rig, plan, 11));
        Assert.Equal(("skipped", "MONITORING_NOT_DUE", null), await SkippedAsync(cs, plan, "20000111T0911"));
        Assert.Null(await MaterializeAsync(Build(cs, rig.Tenant, open: false), plan, 17));
        Assert.Equal(("skipped", "APP_CHANNEL_NOT_OPEN", null), await SkippedAsync(cs, plan, "20000117T0911"));
        Assert.Equal(Footprint(2, 6), await FootprintAsync(cs, rig.Tenant));

        // A plan paused after its slot was frozen: the slot is consumed as disabled, without a run.
        var paused = await MaterializeAsync(rig, plan, 24);
        await ExecAsync(cs, "UPDATE collection_schedules SET enabled = FALSE WHERE id = UUID_TO_BIN(@id);", ("@id", plan.ToString()));
        Assert.False(await rig.Schedules.ProcessSlotAsync(paused!, rig.Runs));
        Assert.Equal(("skipped", "SCHEDULE_DISABLED", null), await SkippedAsync(cs, plan, "20000124T0911"));
        Assert.Equal(Footprint(2, 6), await FootprintAsync(cs, rig.Tenant));
    });

    private static async Task<(string, string?, Guid?)> SkippedAsync(string cs, Guid plan, string key)
    {
        var slot = await SlotAsync(cs, plan, key);
        Assert.False(slot.SkipCode is not null && string.IsNullOrWhiteSpace(slot.SkipMessage));
        return (slot.Status, slot.SkipCode, slot.RunId);
    }

    // Prompt check (design 5): a prompt that fails the rule on its current text, is excluded by an operator, or is no
    // longer enabled produces no check and no task; the reasons are stored on the run. With nothing left the slot is
    // skipped; after the text is fixed and the exclusion revoked the next cycle collects the prompts again.
    [AppChannelMySqlFact]
    public Task RejectedExcludedAndInactivePromptsGetNoRowsAndTheirReasonsAreRecorded() => AppSiteTargetsSqlTests.IsolatedAsync(async (cs, seedTenant) =>
    {
        var rig = Build(cs, await seedTenant());
        var site = await SeedAppBrandAsync(rig, "提问检查", "doubao");
        var ok = await SeedPromptAsync(cs, site, "哪个记账工具好用？", 1);
        var url = await SeedPromptAsync(cs, site, "https://example.com 这个网站上的记账工具怎么样？", 2);
        var tooLong = await SeedPromptAsync(cs, site, new string('长', AppPromptRules.MaxLength + 1), 3);
        var excluded = await SeedPromptAsync(cs, site, "预算管理应用怎么选？", 4);
        var disabledLater = await SeedPromptAsync(cs, site, "冻结之后被停用的提问", 5);
        var excludedAndBad = await SeedPromptAsync(cs, site, "第一行\n第二行", 6);
        var plan = await StartAsync(rig, site);
        var actor = Guid.NewGuid();
        await rig.App.ExcludePromptAsync(rig.Tenant, site, excluded, "涉及竞品比较，暂不在 App 渠道提问", actor);
        await rig.App.ExcludePromptAsync(rig.Tenant, site, excludedAndBad, "多行文本", actor);

        // The slot freezes all six prompts: the check happens when the run is created, on the prompt's text at that moment.
        var pending = await MaterializeAsync(rig, plan, 3);
        Assert.Equal(6, pending!.Selection.PromptIds.Count);
        await ExecAsync(cs, "UPDATE prompts SET enabled = FALSE WHERE id = UUID_TO_BIN(@id);", ("@id", disabledLater.ToString()));
        Assert.True(await rig.Schedules.ProcessSlotAsync(pending, rig.Runs));
        var run = (await SlotAsync(cs, plan, "20000103T0911")).RunId!.Value;
        Assert.Equal([Pair(ok, "doubao", 1)], await PairsAsync(cs, run));
        Assert.Equal("queued|1|0|0|0|0", await RunAsync(cs, run));
        Assert.Equal(Footprint(1, 1), await FootprintAsync(cs, rig.Tenant));
        // Queryable per run: $.app.excludedPromptCount, $.app.excludedByReason and $.app.excludedPrompts.
        var audit = await RunAuditAsync(cs, run);
        Assert.Equal(5, audit.GetProperty("excludedPromptCount").GetInt32());
        Assert.Equal(new Dictionary<string, int>
        {
            ["CONTAINS_URL"] = 1, ["EXCLUDED_BY_OPERATOR"] = 2, ["PROMPT_UNAVAILABLE"] = 1, ["TOO_LONG"] = 1,
        }, audit.GetProperty("excludedByReason").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32()));
        Assert.Equal(new Dictionary<Guid, string>
        {
            [url] = "CONTAINS_URL", [tooLong] = "TOO_LONG", [excluded] = "EXCLUDED_BY_OPERATOR", [disabledLater] = "PROMPT_UNAVAILABLE",
            [excludedAndBad] = "EXCLUDED_BY_OPERATOR",
        }, audit.GetProperty("excludedPrompts").EnumerateArray()
            .ToDictionary(p => p.GetProperty("promptId").GetGuid(), p => p.GetProperty("reasonCode").GetString()!));
        Assert.Equal("5|1", await RowAsync(cs, """
            SELECT JSON_EXTRACT(selection_snapshot, '$.app.excludedPromptCount'), JSON_EXTRACT(selection_snapshot, '$.app.excludedByReason.TOO_LONG')
            FROM visibility_runs WHERE id = UUID_TO_BIN(@run);
            """, ("@run", run.ToString())));

        // The remaining prompt is excluded as well: nothing is left, the slot is skipped and no run is created.
        await rig.App.ExcludePromptAsync(rig.Tenant, site, ok, "临时排除", actor);
        var empty = await RunSlotAsync(rig, plan, 10);
        Assert.Equal(("skipped", "MONITORING_SELECTION_EMPTY", null), (empty.Status, empty.SkipCode, empty.RunId));
        Assert.Contains("5 条提问不进入 App 渠道", empty.SkipMessage, StringComparison.Ordinal);
        Assert.Contains("EXCLUDED_BY_OPERATOR × 3", empty.SkipMessage, StringComparison.Ordinal);
        Assert.Equal(Footprint(1, 1), await FootprintAsync(cs, rig.Tenant));

        // Two exclusions are revoked and the URL is removed from the text: the next cycle collects these prompts again.
        await rig.App.RevokePromptExclusionAsync(rig.Tenant, site, ok, actor);
        await rig.App.RevokePromptExclusionAsync(rig.Tenant, site, excluded, actor);
        await ExecAsync(cs, """
            UPDATE prompts SET content = '这个网站上的记账工具怎么样？' WHERE id = UUID_TO_BIN(@id);
            UPDATE prompt_versions SET content = '这个网站上的记账工具怎么样？' WHERE prompt_id = UUID_TO_BIN(@id);
            """, ("@id", url.ToString()));
        var third = (await RunSlotAsync(rig, plan, 17)).RunId!.Value;
        Assert.Equal([Pair(ok, "doubao", 1), Pair(url, "doubao", 2), Pair(excluded, "doubao", 3)], await PairsAsync(cs, third));
        Assert.Equal("这个网站上的记账工具怎么样？", (await RowsAsync(cs, """
            SELECT t.question_snapshot FROM collector_tasks t JOIN visibility_checks c ON c.id = t.check_id
            WHERE t.run_id = UUID_TO_BIN(@run) AND c.prompt_id = UUID_TO_BIN(@prompt);
            """, ("@run", third.ToString()), ("@prompt", url.ToString())))[0][0]);
        Assert.Equal(2, (await RunAuditAsync(cs, third)).GetProperty("excludedPromptCount").GetInt32());
        Assert.Equal(Footprint(2, 4), await FootprintAsync(cs, rig.Tenant));
    });

    // A platform that lost its enabled consumer_app entry after the slot was frozen produces no check; when no due platform
    // is left the slot is skipped as APP_PLATFORM_UNAVAILABLE.
    [AppChannelMySqlFact]
    public Task APlatformWithoutAnEnabledAppEntryProducesNoChecks() => AppSiteTargetsSqlTests.IsolatedAsync(async (cs, seedTenant) =>
    {
        var rig = Build(cs, await seedTenant());
        var site = await SeedAppBrandAsync(rig, "平台不可用", "doubao", "kimi");
        var first = await SeedPromptAsync(cs, site, "哪个记账工具好用？", 1);
        var second = await SeedPromptAsync(cs, site, "预算管理应用怎么选？", 2);
        var plan = await StartAsync(rig, site);
        Task EntryAsync(string platform, bool enabled) => ExecAsync(cs,
            "UPDATE collector_platform_entries SET enabled = @enabled, unavailable_reason = @reason WHERE entry_code = @code;",
            ("@code", $"{platform}.consumer_app"), ("@enabled", enabled), ("@reason", enabled ? null : AppUnavailableReasons.EmulatorRejected));
        var entries = await RowsAsync(cs, "SELECT entry_code, enabled, unavailable_reason FROM collector_platform_entries ORDER BY entry_code;");
        try
        {
            var pending = await MaterializeAsync(rig, plan, 3);
            Assert.Equal(2, pending!.Selection.Targets.Count);
            await EntryAsync("kimi", false);
            Assert.True(await rig.Schedules.ProcessSlotAsync(pending, rig.Runs));
            var run = (await SlotAsync(cs, plan, "20000103T0911")).RunId!.Value;
            Assert.Equal([Pair(first, "doubao", 1), Pair(second, "doubao", 2)], await PairsAsync(cs, run));
            Assert.Equal("doubao", await RowAsync(cs, "SELECT GROUP_CONCAT(engine) FROM run_engines WHERE run_id = UUID_TO_BIN(@run);", ("@run", run.ToString())));
            Assert.Equal(["kimi"], (await RunAuditAsync(cs, run)).GetProperty("unavailablePlatforms").EnumerateArray().Select(e => e.GetString()!));
            Assert.Equal(Footprint(1, 2), await FootprintAsync(cs, rig.Tenant));

            // Both due platforms are unavailable: nothing can be collected in this cycle.
            pending = await MaterializeAsync(rig, plan, 10);
            await EntryAsync("doubao", false);
            Assert.False(await rig.Schedules.ProcessSlotAsync(pending!, rig.Runs));
            var skipped = await SlotAsync(cs, plan, "20000110T0911");
            Assert.Equal(("skipped", "APP_PLATFORM_UNAVAILABLE", null), (skipped.Status, skipped.SkipCode, skipped.RunId));
            Assert.Contains("不可用", skipped.SkipMessage, StringComparison.Ordinal);
            Assert.Equal(Footprint(1, 2), await FootprintAsync(cs, rig.Tenant));

            // Back in service: the following cycle collects on both platforms again.
            await EntryAsync("doubao", true);
            await EntryAsync("kimi", true);
            Assert.Equal(4, (await PairsAsync(cs, (await RunSlotAsync(rig, plan, 17)).RunId!.Value)).Length);
        }
        finally
        {
            await EntryAsync("doubao", true);
            await EntryAsync("kimi", true);
        }
        Assert.Equal(entries.Select(r => string.Join("|", r)),
            (await RowsAsync(cs, "SELECT entry_code, enabled, unavailable_reason FROM collector_platform_entries ORDER BY entry_code;")).Select(r => string.Join("|", r)));
    });
}
