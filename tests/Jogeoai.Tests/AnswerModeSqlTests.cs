using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using MySqlConnector;

namespace Jogeoai.Tests;

/// <summary>
/// 隔离库（已执行 001–047、库名 jogeoai_answer_mode_*）上的站点目标回答模式 / 采集频率读写、同一引擎 fast + deep 的 Run 展开与租约。
/// 每个用例使用新的租户与品牌，不触碰共享 jogeoai 业务库；未配置 JOGEOAI_ANSWER_MODE_TEST_CONNECTION 时跳过。用例保留其测试行。
/// </summary>
public sealed class AnswerModeSqlTests
{
    private const string Zone = "Asia/Shanghai";
    private static readonly SiteTargetWrite ChatGpt = new("cloro", "chatgpt", "scraped", "US", "en-US");
    private static readonly SiteTargetWrite DeepSeekFast = new("deepseek_api", "deepseek", "api", "CN", "zh-CN");
    private static readonly SiteTargetWrite DeepSeekDeep = DeepSeekFast with { AnswerMode = AnswerMode.Deep };
    private static readonly SiteTargetWrite Doubao = new("doubao_api", "doubao", "api", "CN", "zh-CN");
    private static readonly string DeepSeekLegacyKey = CollectionIdentity.TargetKey("deepseek_api", "deepseek", "api", "CN", "zh-CN");
    private static readonly string DeepSeekDeepKey = CollectionIdentity.TargetKey("deepseek_api", "deepseek", "api", "CN", "zh-CN", "deep");
    private static readonly string DomesticKey = CollectionIdentity.TargetKey("domestic_collector", "doubao", "consumer_web", "CN", "zh-CN");
    private static readonly CloroVisibilityTarget DeepSeekTarget = new("deepseek_api", "deepseek", "zh-CN", "CN", "{}");

    private static string ConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("JOGEOAI_ANSWER_MODE_TEST_CONNECTION")!;
        Assert.StartsWith("jogeoai_answer_mode_", new MySqlConnectionStringBuilder(connectionString).Database, StringComparison.Ordinal);
        return connectionString;
    }

    [AnswerModeMySqlFact]
    public async Task PutStoresModesFrequenciesAndAnchorsAndMonitoringScopeKeepsWebCollectorTargets()
    {
        var cs = ConnectionString();
        var (tenant, site, _) = await SeedAsync(cs);
        var store = new ManagementStore(new MySqlDatabase(cs));
        var before = await store.GetSiteTargetsAsync(tenant, site);
        var today = ManagementStore.BrandLocalDate(Zone, DateTimeOffset.UtcNow);

        var view = await store.PutSiteTargetsAsync(tenant, site, new SiteTargetsWrite(before.ConfigVersion,
            [DeepSeekFast with { Frequency = MonitoringFrequency.Weekly }, DeepSeekDeep, ChatGpt], SiteTargetsScope.Monitoring));
        Assert.Equal(before.ConfigVersion + 1, view.ConfigVersion);
        var rows = await TargetRowsAsync(cs, site);
        // Fast is the DeepSeek default: NULL mode and the byte-identical legacy key. Deep carries the mode segment.
        Assert.Equal((null, "weekly", today, true), rows[DeepSeekLegacyKey]);
        Assert.Equal(("deep", "daily", today, true), rows[DeepSeekDeepKey]);
        // scope=monitoring leaves the enabled domestic web collector row untouched.
        Assert.True(rows[DomesticKey].Enabled);
        var deep = Assert.Single(view.Items, i => i.TargetKey == DeepSeekDeepKey);
        Assert.Equal((AnswerMode.Deep, MonitoringFrequency.Daily, today, true), (deep.AnswerMode, deep.Frequency, deep.FrequencyAnchor, deep.MonitoringEligible));
        var fast = Assert.Single(view.Items, i => i.TargetKey == DeepSeekLegacyKey);
        Assert.Equal((AnswerMode.Fast, MonitoringFrequency.Weekly, AnswerMode.Fast), (fast.AnswerMode, fast.Frequency, fast.DefaultAnswerMode));
        Assert.Equal(new[] { AnswerMode.Fast, AnswerMode.Deep }, fast.SupportedAnswerModes);
        var domesticView = Assert.Single(view.Items, i => i.Provider == "domestic_collector");
        Assert.Equal((AnswerMode.Fast, MonitoringFrequency.Daily, false), (domesticView.AnswerMode, domesticView.Frequency, domesticView.MonitoringEligible));

        // Anchors move only when a target is newly enabled or switches to every_other_day / weekly; otherwise they are kept.
        await ExecAsync(cs, "UPDATE site_targets SET frequency_anchor='2026-01-01' WHERE site_id=UUID_TO_BIN(@site);", ("@site", site.ToString()));
        view = await store.PutSiteTargetsAsync(tenant, site, new SiteTargetsWrite(view.ConfigVersion,
            [DeepSeekFast with { Frequency = MonitoringFrequency.Weekly }, DeepSeekDeep with { Frequency = MonitoringFrequency.EveryOtherDay }, ChatGpt with { Frequency = MonitoringFrequency.TwiceDaily }],
            SiteTargetsScope.Monitoring));
        rows = await TargetRowsAsync(cs, site);
        Assert.Equal((null, "weekly", new DateOnly(2026, 1, 1), true), rows[DeepSeekLegacyKey]);
        Assert.Equal(("deep", "every_other_day", today, true), rows[DeepSeekDeepKey]);
        Assert.Equal((null, "twice_daily", new DateOnly(2026, 1, 1), true), rows[CollectionIdentity.TargetKey(new CloroVisibilityTarget("cloro", "chatgpt", "en-US", "US"))]);

        // Dropping deep disables only that cell; re-enabling it later restarts its anchor.
        view = await store.PutSiteTargetsAsync(tenant, site, new SiteTargetsWrite(view.ConfigVersion, [DeepSeekFast, ChatGpt], SiteTargetsScope.Monitoring));
        Assert.False((await TargetRowsAsync(cs, site))[DeepSeekDeepKey].Enabled);
        Assert.False(view.Items.Single(i => i.TargetKey == DeepSeekDeepKey).Enabled);
        Assert.True(view.Items.Single(i => i.TargetKey == DeepSeekLegacyKey).Enabled);

        // Validation failures and stale versions write nothing.
        var version = view.ConfigVersion;
        var unsupported = await Assert.ThrowsAsync<CollectorStoreException>(() => store.PutSiteTargetsAsync(tenant, site,
            new SiteTargetsWrite(version, [ChatGpt with { AnswerMode = AnswerMode.Deep }], SiteTargetsScope.Monitoring)));
        Assert.Equal(("ANSWER_MODE_UNSUPPORTED", 400), (unsupported.Code, unsupported.StatusCode));
        var duplicate = await Assert.ThrowsAsync<CollectorStoreException>(() => store.PutSiteTargetsAsync(tenant, site,
            new SiteTargetsWrite(version, [DeepSeekFast, DeepSeekFast with { AnswerMode = AnswerMode.Fast }])));
        Assert.Equal(("DUPLICATE_TARGET", 400), (duplicate.Code, duplicate.StatusCode));
        var conflict = await Assert.ThrowsAsync<CollectorStoreException>(() => store.PutSiteTargetsAsync(tenant, site,
            new SiteTargetsWrite(version - 1, [DeepSeekFast], SiteTargetsScope.Monitoring)));
        Assert.Equal(("CONFIG_VERSION_CONFLICT", 409), (conflict.Code, conflict.StatusCode));
        Assert.Equal(version, (await store.GetSiteTargetsAsync(tenant, site)).ConfigVersion);

        // scope=all (default, old clients) keeps the old replace semantics: the omitted web collector row is disabled.
        await store.PutSiteTargetsAsync(tenant, site, new SiteTargetsWrite(version, [DeepSeekFast, ChatGpt]));
        Assert.False((await TargetRowsAsync(cs, site))[DomesticKey].Enabled);
    }

    [AnswerModeMySqlFact]
    public async Task RunWithFastAndDeepOfOneEngineWritesTwoChecksAndLeasesCarryTheResolvedMode()
    {
        var cs = ConnectionString();
        var (tenant, site, prompt) = await SeedAsync(cs);
        var database = new MySqlDatabase(cs);
        var management = new ManagementStore(database);
        var targets = await management.GetSiteTargetsAsync(tenant, site);
        targets = await management.PutSiteTargetsAsync(tenant, site, new SiteTargetsWrite(targets.ConfigVersion,
            [DeepSeekFast, DeepSeekDeep, Doubao], SiteTargetsScope.Monitoring));

        var runs = new CloroVisibilityStore(database);
        var created = await runs.CreateRunAsync(new CreateCloroVisibilityRunCommand(tenant, site, [prompt],
            [DeepSeekTarget with { AnswerMode = "deep" }, DeepSeekTarget], $"answer-mode:{Guid.NewGuid():N}", "answer-mode-sql",
            RequireEnabledTargets: true, ChargeMonitoringCredits: true));
        Assert.Equal(2, created.TotalChecks);
        // One prompt, one engine, two run targets: fast (legacy key) and deep (mode key), each with its own check.
        var engines = await RowsAsync(cs, "SELECT target_key, BIN_TO_UUID(id) FROM run_engines WHERE run_id=UUID_TO_BIN(@run) ORDER BY sort_order;",
            ("@run", created.RunId.ToString()));
        Assert.Equal(new[] { DeepSeekLegacyKey, DeepSeekDeepKey }, engines.Select(r => r[0]));
        var checks = await RowsAsync(cs, """
            SELECT c.answer_mode, e.target_key FROM visibility_checks c JOIN run_engines e ON e.id = c.run_target_id
            WHERE c.run_id=UUID_TO_BIN(@run) ORDER BY e.sort_order;
            """, ("@run", created.RunId.ToString()));
        Assert.Equal(new[] { $"fast|{DeepSeekLegacyKey}", $"deep|{DeepSeekDeepKey}" }, checks.Select(r => $"{r[0]}|{r[1]}"));
        // Each mode is its own check and is charged separately (2 × 10 credits).
        var credits = await RowsAsync(cs, "SELECT COUNT(*), CAST(SUM(amount) AS SIGNED) FROM monitoring_credit_items WHERE run_id=UUID_TO_BIN(@run);",
            ("@run", created.RunId.ToString()));
        Assert.Equal(new string?[] { "2", "20" }, credits[0]);
        var views = (await runs.GetChecksAsync(tenant, created.RunId, null, 10, null, null)).Items;
        Assert.Equal(new AnswerMode?[] { AnswerMode.Deep, AnswerMode.Fast }.Order(), views.Select(v => v.AnswerMode).Order());
        Assert.Equal(AnswerMode.Deep, (await runs.GetCheckAsync(tenant, views.Single(v => v.AnswerMode == AnswerMode.Deep).CheckId))!.AnswerMode);

        var leases = await new CloroVisibilityStore(database, tenant, created.RunId).LeaseTasksAsync("answer-mode-sql", 10, 60, "deepseek_api");
        Assert.Equal(2, leases.Count);
        Assert.Equal(views.ToDictionary(v => v.CheckId, v => AnswerModeRules.ToStorage(v.AnswerMode!.Value)),
            leases.ToDictionary(l => l.CheckId, l => l.AnswerMode));

        // Historical checks (NULL mode) lease with the provider default: doubao defaults to deep.
        var doubaoRun = await runs.CreateRunAsync(new CreateCloroVisibilityRunCommand(tenant, site, [prompt],
            [new CloroVisibilityTarget("doubao_api", "doubao", "zh-CN", "CN", "{}")], $"answer-mode:{Guid.NewGuid():N}", "answer-mode-sql"));
        Assert.Equal("deep", (await RowsAsync(cs, "SELECT answer_mode FROM visibility_checks WHERE run_id=UUID_TO_BIN(@run);",
            ("@run", doubaoRun.RunId.ToString())))[0][0]);
        await ExecAsync(cs, "UPDATE visibility_checks SET answer_mode=NULL WHERE run_id=UUID_TO_BIN(@run);", ("@run", doubaoRun.RunId.ToString()));
        Assert.Null(Assert.Single((await runs.GetChecksAsync(tenant, doubaoRun.RunId, null, 10, null, null)).Items).AnswerMode);
        var doubaoLease = Assert.Single(await new CloroVisibilityStore(database, tenant, doubaoRun.RunId)
            .LeaseTasksAsync("answer-mode-sql", 10, 60, "doubao_api"));
        Assert.Equal("deep", doubaoLease.AnswerMode);

        // Site target ids resolve with their mode (manual runs by targetIds, previews).
        var resolved = await management.ResolveRunAsync(tenant, site,
            new CollectionSelection([prompt], null, targets.Items.Where(i => i.Provider == "deepseek_api").Select(i => i.TargetId!.Value).ToArray()), null);
        Assert.Equal(new string?[] { null, "deep" }, resolved.Targets.Select(t => t.AnswerMode));

        // A manual schedule may hold both modes of one engine; the snapshot keeps the deep target.
        var schedule = await new CollectionScheduleStore(database).CreateAsync(new CreateCollectionScheduleCommand(tenant, site,
            "answer-mode-sql", "daily", null, "10:00", Zone,
            new CollectionScheduleSelection([prompt], [DeepSeekTarget with { AnswerMode = "deep" }, DeepSeekTarget with { AnswerMode = "fast" }])));
        Assert.Equal(new string?[] { null, "deep" }, schedule.Selection.Targets.Select(t => t.AnswerMode));

        // Once deep is disabled, runs requiring enabled targets reject it by its mode-aware key.
        await management.PutSiteTargetsAsync(tenant, site, new SiteTargetsWrite(targets.ConfigVersion, [DeepSeekFast, Doubao], SiteTargetsScope.Monitoring));
        var disabled = await Assert.ThrowsAsync<CollectorStoreException>(() => runs.CreateRunAsync(new CreateCloroVisibilityRunCommand(
            tenant, site, [prompt], [DeepSeekTarget with { AnswerMode = "deep" }], $"answer-mode:{Guid.NewGuid():N}", "answer-mode-sql",
            RequireEnabledTargets: true)));
        Assert.Equal(("TARGET_NOT_ENABLED", 409), (disabled.Code, disabled.StatusCode));
        var unsupported = await Assert.ThrowsAsync<CollectorStoreException>(() => runs.CreateRunAsync(new CreateCloroVisibilityRunCommand(
            tenant, site, [prompt], [new CloroVisibilityTarget("packy_yuanbao", "tencent_yuanbao", "zh-CN", "CN", "{}", "fast")],
            $"answer-mode:{Guid.NewGuid():N}", "answer-mode-sql")));
        Assert.Equal(("ANSWER_MODE_UNSUPPORTED", 400), (unsupported.Code, unsupported.StatusCode));

        // Park the leftover tasks so no other consumer of this isolated database picks them up.
        await ExecAsync(cs, "UPDATE provider_tasks t JOIN visibility_runs r ON r.id=t.run_id SET t.next_attempt_at='2099-01-01' WHERE r.site_id=UUID_TO_BIN(@site);",
            ("@site", site.ToString()));
    }

    private static readonly CloroVisibilityTarget ChatGptTarget = new("cloro", "chatgpt", "en-US", "US", "{}");

    [AnswerModeMySqlFact]
    public async Task NightScheduleFollowsTwiceDailyCellsOnPutAndOnTheLazyReconcile()
    {
        var cs = ConnectionString();
        var (tenant, site, prompt) = await SeedAsync(cs);
        var database = new MySqlDatabase(cs);
        var store = new ManagementStore(database);
        var schedules = new CollectionScheduleStore(database);
        var view = await store.GetSiteTargetsAsync(tenant, site);
        try
        {
            // Without daily monitoring (no main plan) a twice_daily cell does not create the night plan.
            view = await store.PutSiteTargetsAsync(tenant, site, new SiteTargetsWrite(view.ConfigVersion,
                [ChatGpt, DeepSeekDeep with { Frequency = MonitoringFrequency.TwiceDaily }], SiteTargetsScope.Monitoring));
            Assert.Null(await NightAsync(cs, site));

            var main = await schedules.CreateAutomaticPlanAsync(new CreateCollectionScheduleCommand(tenant, site, PromptMonitoringIdentity.ScheduleName,
                "daily", null, PromptMonitoringIdentity.MainLocalTime, Zone, new CollectionScheduleSelection([prompt], [ChatGptTarget]), Enabled: true));
            view = await store.PutSiteTargetsAsync(tenant, site, new SiteTargetsWrite(view.ConfigVersion,
                [ChatGpt, DeepSeekDeep with { Frequency = MonitoringFrequency.TwiceDaily }], SiteTargetsScope.Monitoring));
            var night = Assert.IsType<NightRow>(await NightAsync(cs, site));
            Assert.Equal((true, "00:05", Zone, "daily"), (night.Enabled, night.LocalTime, night.TimeZone, night.Cadence));
            // Starts at the next 00:05 local time, never a catch-up slot.
            var zone = TimeZoneInfo.FindSystemTimeZoneById(Zone);
            Assert.InRange(night.NextSlotAt!.Value, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));
            Assert.Equal(new TimeSpan(0, 5, 0), TimeZoneInfo.ConvertTime(night.NextSlotAt.Value, zone).TimeOfDay);
            Assert.Equal([DeepSeekDeepKey], night.Selection.Targets.Select(CollectionIdentity.TargetKey));
            Assert.Equal([prompt], night.Selection.PromptIds);
            Assert.Equal(0, await CountRunsAsync(cs, site, $"schedule:{night.Id:N}"));
            Assert.Equal(0, await CountRunsAsync(cs, site, $"schedule:{main.Id:N}"));

            // No twice_daily cell left: disabled (kept for history).
            view = await store.PutSiteTargetsAsync(tenant, site, new SiteTargetsWrite(view.ConfigVersion,
                [ChatGpt, DeepSeekDeep], SiteTargetsScope.Monitoring));
            var disabled = Assert.IsType<NightRow>(await NightAsync(cs, site));
            Assert.Equal((night.Id, false, (DateTimeOffset?)null), (disabled.Id, disabled.Enabled, disabled.NextSlotAt));

            // Re-enabled through the lazy reconcile (prompt-monitoring GET/POST and before each slot) when the PUT path missed it.
            await ExecAsync(cs, "UPDATE site_targets SET frequency='twice_daily' WHERE site_id=UUID_TO_BIN(@site) AND target_key=@key;",
                ("@site", site.ToString()), ("@key", CollectionIdentity.TargetKey(ChatGptTarget)));
            Assert.False((await NightAsync(cs, site))!.Enabled);
            await AutomaticMonitoringSelection.ReconcileAsync(database, tenant, site, default);
            var lazy = Assert.IsType<NightRow>(await NightAsync(cs, site));
            Assert.Equal((night.Id, true), (lazy.Id, lazy.Enabled));
            Assert.Equal([CollectionIdentity.TargetKey(ChatGptTarget)], lazy.Selection.Targets.Select(CollectionIdentity.TargetKey));
            Assert.InRange(lazy.NextSlotAt!.Value, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));
            Assert.Equal(1, await CountAsync(cs, "SELECT COUNT(*) FROM collection_schedules WHERE site_id=UUID_TO_BIN(@site) AND name=@name;",
                ("@site", site.ToString()), ("@name", PromptMonitoringIdentity.NightScheduleName)));
            Assert.Equal(0, await CountRunsAsync(cs, site, $"schedule:{night.Id:N}"));
        }
        finally { await ParkAsync(cs, site); }
    }

    [AnswerModeMySqlFact]
    public async Task SlotsFilterCellsByFrequencyAndNightRunsDoNotSkipTheMainSlot()
    {
        var cs = ConnectionString();
        var (tenant, site, prompt) = await SeedAsync(cs);
        var database = new MySqlDatabase(cs);
        var store = new ManagementStore(database);
        var schedules = new CollectionScheduleStore(database);
        var runs = new CloroVisibilityStore(database);
        var weeklyFast = DeepSeekFast with { Frequency = MonitoringFrequency.Weekly };
        var view = await store.GetSiteTargetsAsync(tenant, site);
        view = await store.PutSiteTargetsAsync(tenant, site, new SiteTargetsWrite(view.ConfigVersion, [ChatGpt, weeklyFast], SiteTargetsScope.Monitoring));
        var main = await schedules.CreateAutomaticPlanAsync(new CreateCollectionScheduleCommand(tenant, site, PromptMonitoringIdentity.ScheduleName,
            "daily", null, PromptMonitoringIdentity.MainLocalTime, Zone, new CollectionScheduleSelection([prompt], [ChatGptTarget]), Enabled: true));
        try
        {
            // Year-2000 slots keep this test's plans ahead of every other due plan of the shared isolated database.
            // Monday 2000-01-03 anchors; Tuesday 2000-01-04 09:01 Asia/Shanghai is 01:01 UTC.
            await BackdateAsync(cs, site);
            await ExecAsync(cs, "UPDATE collection_schedules SET next_slot_at='2000-01-04 01:01:00' WHERE id=UUID_TO_BIN(@id);", ("@id", main.Id.ToString()));
            Assert.Equal(1, await schedules.MaterializeDueSlotsAsync(new DateTimeOffset(2000, 1, 4, 1, 2, 0, TimeSpan.Zero), 1));
            var tuesday = await SlotAsync(cs, main.Id, "20000104T0901");
            // The weekly cell is off on Tuesday: the slot freezes ChatGPT only; the plan snapshot keeps both cells.
            Assert.Equal(("pending", null), (tuesday.Status, tuesday.SkipCode));
            Assert.Equal([CollectionIdentity.TargetKey(ChatGptTarget)], tuesday.Selection.Targets.Select(CollectionIdentity.TargetKey));
            Assert.Equal(2, (await PlanAsync(cs, main.Id)).Targets.Count);
            // Not processed: a main-plan run created today would rightly make today's 09:01 slot below skip.
            await ExecAsync(cs, "UPDATE schedule_slots SET status='skipped', skip_code='TEST_PARKED' WHERE schedule_id=UUID_TO_BIN(@id) AND status='pending';",
                ("@id", main.Id.ToString()));

            // Only the weekly cell left: Wednesday has nothing due → skipped as MONITORING_NOT_DUE, no run and no reservation.
            view = await store.PutSiteTargetsAsync(tenant, site, new SiteTargetsWrite(view.ConfigVersion, [weeklyFast], SiteTargetsScope.Monitoring));
            Assert.Equal(1, await schedules.MaterializeDueSlotsAsync(new DateTimeOffset(2000, 1, 5, 1, 2, 0, TimeSpan.Zero), 1));
            var wednesday = await SlotAsync(cs, main.Id, "20000105T0901");
            Assert.Equal(("skipped", AutomaticMonitoringSelection.NotDueCode, (string?)null), (wednesday.Status, wednesday.SkipCode, wednesday.RunId));
            Assert.Contains("采集频率", wednesday.SkipMessage);
            Assert.Equal(0, await CountRunsAsync(cs, site, $"schedule:{main.Id:N}"));
            Assert.Equal(0, await CountAsync(cs, "SELECT COUNT(*) FROM monitoring_credit_reservations WHERE site_id=UUID_TO_BIN(@site);",
                ("@site", site.ToString())));

            // A twice_daily cell creates the night plan; its 00:05 slot (Wednesday 2000-01-05 = 16:05 UTC the day before)
            // only takes twice_daily cells.
            view = await store.PutSiteTargetsAsync(tenant, site, new SiteTargetsWrite(view.ConfigVersion,
                [ChatGpt, weeklyFast, DeepSeekDeep with { Frequency = MonitoringFrequency.TwiceDaily }], SiteTargetsScope.Monitoring));
            var night = Assert.IsType<NightRow>(await NightAsync(cs, site));
            await BackdateAsync(cs, site);
            await ExecAsync(cs, "UPDATE collection_schedules SET next_slot_at='2099-01-01' WHERE id=UUID_TO_BIN(@id);", ("@id", main.Id.ToString()));
            await ExecAsync(cs, "UPDATE collection_schedules SET next_slot_at='2000-01-04 16:05:00' WHERE id=UUID_TO_BIN(@id);", ("@id", night.Id.ToString()));
            Assert.Equal(1, await schedules.MaterializeDueSlotsAsync(new DateTimeOffset(2000, 1, 4, 16, 6, 0, TimeSpan.Zero), 1));
            var nightSlot = await SlotAsync(cs, night.Id, "20000105T0005");
            Assert.Equal(("pending", null), (nightSlot.Status, nightSlot.SkipCode));
            Assert.Equal([DeepSeekDeepKey], nightSlot.Selection.Targets.Select(CollectionIdentity.TargetKey));
            Assert.Equal([DeepSeekDeepKey], (await PlanAsync(cs, night.Id)).Targets.Select(CollectionIdentity.TargetKey));

            // The night run is an automatic monitoring run: one deep check, charged 10 credits.
            var pendingNight = Assert.Single(await schedules.GetPendingSlotsAsync(100), s => s.ScheduleId == night.Id && s.LocalSlotKey == "20000105T0005");
            Assert.True(await schedules.ProcessSlotAsync(pendingNight, runs));
            var nightRun = (await SlotAsync(cs, night.Id, "20000105T0005")).RunId!;
            Assert.Equal(1, await CountAsync(cs, """
                SELECT COUNT(*) FROM visibility_runs r JOIN visibility_checks c ON c.run_id=r.id
                WHERE r.id=UUID_TO_BIN(@run) AND r.total_checks=1 AND c.answer_mode='deep';
                """, ("@run", nightRun)));
            Assert.Equal(1, await CountAsync(cs, "SELECT COUNT(*) FROM monitoring_credit_reservations WHERE run_id=UUID_TO_BIN(@run) AND held_credits=@held;",
                ("@run", nightRun), ("@held", PromptMonitoringPricing.UnitPrice)));

            // The night run was created today, yet today's 09:01 main slot still runs (the guard only sees main-plan runs).
            var todayKey = $"{ManagementStore.BrandLocalDate(Zone, DateTimeOffset.UtcNow):yyyyMMdd}T0901";
            var mainSlot = await InsertPendingSlotAsync(cs, schedules, main.Id, new CollectionScheduleSelection([prompt], [ChatGptTarget]), todayKey);
            Assert.True(await schedules.ProcessSlotAsync(mainSlot, runs));
            var processed = await SlotAsync(cs, main.Id, todayKey);
            Assert.Equal(("completed", null), (processed.Status, processed.SkipCode));
            Assert.NotNull(processed.RunId);
            Assert.Equal(1, await CountRunsAsync(cs, site, $"schedule:{main.Id:N}"));
        }
        finally { await ParkAsync(cs, site); }
    }

    [AnswerModeMySqlFact]
    public async Task AnchorsUseTheMainPlanZoneAfterTheBrandZoneChanges()
    {
        // Slot local dates come from the main plan's zone, which does not follow later brand time-zone edits.
        // UTC+14 vs UTC−11: the two local dates always differ, so the anchor shows which zone was used.
        const string planZone = "Pacific/Kiritimati";
        const string brandZone = "Pacific/Pago_Pago";
        var cs = ConnectionString();
        var (tenant, site, prompt) = await SeedAsync(cs);
        var database = new MySqlDatabase(cs);
        var store = new ManagementStore(database);
        try
        {
            var view = await store.GetSiteTargetsAsync(tenant, site);
            view = await store.PutSiteTargetsAsync(tenant, site, new SiteTargetsWrite(view.ConfigVersion, [ChatGpt], SiteTargetsScope.Monitoring));
            await new CollectionScheduleStore(database).CreateAutomaticPlanAsync(new CreateCollectionScheduleCommand(tenant, site, PromptMonitoringIdentity.ScheduleName,
                "daily", null, PromptMonitoringIdentity.MainLocalTime, planZone, new CollectionScheduleSelection([prompt], [ChatGptTarget]), Enabled: true));
            await ExecAsync(cs, "UPDATE site_brand_settings SET time_zone=@zone WHERE site_id=UUID_TO_BIN(@site);",
                ("@zone", brandZone), ("@site", site.ToString()));

            var before = ManagementStore.BrandLocalDate(planZone, DateTimeOffset.UtcNow);
            await store.PutSiteTargetsAsync(tenant, site, new SiteTargetsWrite(view.ConfigVersion,
                [ChatGpt, DeepSeekFast with { Frequency = MonitoringFrequency.Weekly }], SiteTargetsScope.Monitoring));
            var after = ManagementStore.BrandLocalDate(planZone, DateTimeOffset.UtcNow);
            var anchor = (await TargetRowsAsync(cs, site))[DeepSeekLegacyKey].Anchor;
            Assert.Contains(anchor!.Value, new[] { before, after });
            Assert.NotEqual(ManagementStore.BrandLocalDate(brandZone, DateTimeOffset.UtcNow), anchor.Value);
        }
        finally { await ParkAsync(cs, site); }
    }

    private sealed record NightRow(Guid Id, bool Enabled, string LocalTime, string TimeZone, string Cadence, DateTimeOffset? NextSlotAt,
        CollectionScheduleSelection Selection);
    private sealed record SlotRow(string Status, string? SkipCode, string? SkipMessage, string? RunId, CollectionScheduleSelection Selection);
    private static readonly System.Text.Json.JsonSerializerOptions Web = new(System.Text.Json.JsonSerializerDefaults.Web);

    private static async Task<NightRow?> NightAsync(string cs, Guid site)
    {
        var rows = await RowsAsync(cs, """
            SELECT BIN_TO_UUID(id), enabled, TIME_FORMAT(local_time, '%H:%i'), time_zone, cadence,
                   DATE_FORMAT(next_slot_at, '%Y-%m-%dT%H:%i:%s'), selection_snapshot
            FROM collection_schedules WHERE site_id=UUID_TO_BIN(@site) AND name=@name;
            """, ("@site", site.ToString()), ("@name", PromptMonitoringIdentity.NightScheduleName));
        if (rows.Count == 0) return null;
        var r = Assert.Single(rows);
        return new(Guid.Parse(r[0]!), r[1] is "1" or "True", r[2]!, r[3]!, r[4]!,
            r[5] is null ? null : new DateTimeOffset(DateTime.SpecifyKind(DateTime.Parse(r[5]!, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc)),
            System.Text.Json.JsonSerializer.Deserialize<CollectionScheduleSelection>(r[6]!, Web)!);
    }

    private static async Task<CollectionScheduleSelection> PlanAsync(string cs, Guid schedule)
        => System.Text.Json.JsonSerializer.Deserialize<CollectionScheduleSelection>((await RowsAsync(cs,
            "SELECT selection_snapshot FROM collection_schedules WHERE id=UUID_TO_BIN(@id);", ("@id", schedule.ToString())))[0][0]!, Web)!;

    private static async Task<SlotRow> SlotAsync(string cs, Guid schedule, string key)
    {
        var r = Assert.Single(await RowsAsync(cs, """
            SELECT status, skip_code, skip_message, BIN_TO_UUID(run_id), selection_snapshot FROM schedule_slots
            WHERE schedule_id=UUID_TO_BIN(@id) AND local_slot_key=@key;
            """, ("@id", schedule.ToString()), ("@key", key)));
        return new(r[0]!, r[1], r[2], r[3], System.Text.Json.JsonSerializer.Deserialize<CollectionScheduleSelection>(r[4]!, Web)!);
    }

    private static async Task<PendingCollectionSlot> InsertPendingSlotAsync(string cs, CollectionScheduleStore schedules, Guid schedule,
        CollectionScheduleSelection selection, string key)
    {
        var id = Guid.NewGuid();
        await ExecAsync(cs, """
            INSERT INTO schedule_slots (id,schedule_id,local_slot_key,selection_snapshot,status,missed_slots,created_at)
            VALUES (UUID_TO_BIN(@id),UUID_TO_BIN(@schedule),@key,@selection,'pending',0,UTC_TIMESTAMP(6));
            """, ("@id", id.ToString()), ("@schedule", schedule.ToString()), ("@key", key),
            ("@selection", System.Text.Json.JsonSerializer.Serialize(selection, Web)));
        return Assert.Single(await schedules.GetPendingSlotsAsync(100), s => s.Id == id);
    }

    // Activation boundaries and Monday anchors in the past, so year-2000 slots see every cell as active.
    private static Task BackdateAsync(string cs, Guid site) => ExecAsync(cs,
        "UPDATE site_targets SET monitoring_enabled_at='2000-01-01', frequency_anchor='2000-01-03' WHERE site_id=UUID_TO_BIN(@site) AND enabled=TRUE;",
        ("@site", site.ToString()));

    private static async Task<int> CountRunsAsync(string cs, Guid site, string requestedBy)
        => await CountAsync(cs, "SELECT COUNT(*) FROM visibility_runs WHERE site_id=UUID_TO_BIN(@site) AND requested_by=@by;",
            ("@site", site.ToString()), ("@by", requestedBy));

    private static async Task<int> CountAsync(string cs, string sql, params (string Name, object? Value)[] args)
        => int.Parse((await RowsAsync(cs, sql, args))[0][0]!, System.Globalization.CultureInfo.InvariantCulture);

    // Keep rows for inspection, but never leave a due plan, a pending slot or runnable provider tasks behind.
    private static async Task ParkAsync(string cs, Guid site)
    {
        await ExecAsync(cs, "UPDATE collection_schedules SET enabled=FALSE, next_slot_at=NULL WHERE site_id=UUID_TO_BIN(@site);", ("@site", site.ToString()));
        await ExecAsync(cs, """
            UPDATE schedule_slots s JOIN collection_schedules p ON p.id=s.schedule_id SET s.status='skipped', s.skip_code='TEST_PARKED'
            WHERE p.site_id=UUID_TO_BIN(@site) AND s.status='pending';
            """, ("@site", site.ToString()));
        await ExecAsync(cs, "UPDATE provider_tasks t JOIN visibility_runs r ON r.id=t.run_id SET t.next_attempt_at='2099-01-01' WHERE r.site_id=UUID_TO_BIN(@site);",
            ("@site", site.ToString()));
    }

    private static async Task<(Guid Tenant, Guid Site, Guid Prompt)> SeedAsync(string cs)
    {
        var tenant = Guid.NewGuid();
        var site = Guid.NewGuid();
        var prompt = Guid.NewGuid();
        var version = Guid.NewGuid();
        await ExecAsync(cs, """
            INSERT INTO tenants (id,name,status,created_at,updated_at) VALUES (UUID_TO_BIN(@tenant),'answer-mode-test','active',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
            INSERT INTO sites (id,tenant_id,host,display_name,status,created_at,updated_at)
              VALUES (UUID_TO_BIN(@site),UUID_TO_BIN(@tenant),@host,'Seed','active',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
            INSERT INTO site_brand_settings (site_id,tenant_id,time_zone,created_at,updated_at)
              VALUES (UUID_TO_BIN(@site),UUID_TO_BIN(@tenant),@zone,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
            INSERT INTO site_targets (id,tenant_id,site_id,provider,engine,capture_mode,country,locale,target_key,enabled,created_at,updated_at)
              VALUES (UUID_TO_BIN(@target),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),'domestic_collector','doubao','consumer_web','CN','zh-CN',@domestic,TRUE,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
            INSERT INTO prompts (id,site_id,content,locale,enabled,created_at,updated_at)
              VALUES (UUID_TO_BIN(@prompt),UUID_TO_BIN(@site),'哪个 AI 写作工具好用？','zh-CN',TRUE,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
            INSERT INTO prompt_versions (id,site_id,prompt_id,version,content,locale,branded_status,branded_source,created_at)
              VALUES (UUID_TO_BIN(@version),UUID_TO_BIN(@site),UUID_TO_BIN(@prompt),1,'哪个 AI 写作工具好用？','zh-CN','unbranded','system',UTC_TIMESTAMP(6));
            UPDATE prompts SET current_version_id=UUID_TO_BIN(@version) WHERE id=UUID_TO_BIN(@prompt);
            INSERT INTO credit_accounts (tenant_id,available_credits,lifetime_granted,updated_at) VALUES (UUID_TO_BIN(@tenant),1000,1000,UTC_TIMESTAMP(6));
            """, ("@tenant", tenant.ToString()), ("@site", site.ToString()), ("@host", $"answer-mode-{site:N}.test"), ("@zone", Zone),
            ("@target", Guid.NewGuid().ToString()), ("@domestic", DomesticKey), ("@prompt", prompt.ToString()), ("@version", version.ToString()));
        return (tenant, site, prompt);
    }

    private static async Task<Dictionary<string, (string? Mode, string Frequency, DateOnly? Anchor, bool Enabled)>> TargetRowsAsync(string cs, Guid site)
        => (await RowsAsync(cs, """
                SELECT target_key, answer_mode, frequency, DATE_FORMAT(frequency_anchor, '%Y-%m-%d'), enabled
                FROM site_targets WHERE site_id=UUID_TO_BIN(@site);
                """, ("@site", site.ToString())))
            .ToDictionary(r => r[0]!, r => (r[1], r[2]!, r[3] is null ? (DateOnly?)null : DateOnly.Parse(r[3]!), r[4] is "1" or "True"));

    private static async Task ExecAsync(string cs, string sql, params (string Name, object? Value)[] args)
    {
        await using var connection = new MySqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<string?[]>> RowsAsync(string cs, string sql, params (string Name, object? Value)[] args)
    {
        await using var connection = new MySqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string?[]>();
        while (await reader.ReadAsync())
        {
            var row = new string?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
                row[i] = reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture);
            rows.Add(row);
        }
        return rows;
    }
}

/// <summary>需要已执行 001–047 的隔离库 JOGEOAI_ANSWER_MODE_TEST_CONNECTION（库名 jogeoai_answer_mode_*）；未配置时跳过。</summary>
public sealed class AnswerModeMySqlFactAttribute : FactAttribute
{
    public AnswerModeMySqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JOGEOAI_ANSWER_MODE_TEST_CONNECTION")))
            Skip = "需提供已执行 001–047 的独立测试库 JOGEOAI_ANSWER_MODE_TEST_CONNECTION（库名 jogeoai_answer_mode_*）。";
    }
}
