using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using MySqlConnector;

namespace Jogeoai.Tests;

/// <summary>
/// 隔离库（已执行 001–047、库名 jogeoai_answer_mode_*）上的「新增提问 / 新增模型后立即采集」：只采新提问或新增模型、按每日监控计费、
/// 当天去重、余额不足不建运行、批量重放不重复采集、09:01 主时隙剔除当天已立即采集的组合、品牌无计划时建立计划与首轮。
/// 每个用例使用新的租户与品牌，不触碰共享 jogeoai 业务库；未配置 JOGEOAI_ANSWER_MODE_TEST_CONNECTION 时跳过。用例保留其测试行，
/// 但结束时停用计划、搁置时隙与 Provider 任务。时隙展开会领取全库最早到期的计划，所以本类单独串行运行（见集合定义）。
/// </summary>
[Collection(InstantCollectionSqlCollection.Name)]
public sealed class InstantCollectionSqlTests
{
    private const string Zone = "Asia/Shanghai";
    private static readonly CloroVisibilityTarget ChatGpt = new("cloro", "chatgpt", "en-US", "US", "{}");
    private static readonly CloroVisibilityTarget Gemini = new("cloro", "gemini", "en-US", "US", "{}");
    private static readonly string ChatGptKey = CollectionIdentity.TargetKey(ChatGpt);
    private static readonly string GeminiKey = CollectionIdentity.TargetKey(Gemini);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static string ConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("JOGEOAI_ANSWER_MODE_TEST_CONNECTION")!;
        Assert.StartsWith("jogeoai_answer_mode_", new MySqlConnectionStringBuilder(connectionString).Database, StringComparison.Ordinal);
        return connectionString;
    }

    // keyPool / providerSettings stay null: the pre-flight is advisory without them (covered by CollectionScheduleTests).
    private static PromptMonitoringService Service(MySqlDatabase database, IManagementStore management) =>
        new(database, management, new CollectionScheduleStore(database), new CloroVisibilityStore(database));

    private static DateOnly Today() => ManagementStore.BrandLocalDate(Zone, DateTimeOffset.UtcNow);

    // 1. A brand with a plan: a new prompt is collected alone, as a monitoring run with the normal reservation.
    [AnswerModeMySqlFact]
    public async Task NewPromptOnAnExistingPlanCollectsOnlyThatPromptWithMonitoringBilling()
    {
        var cs = ConnectionString();
        var (tenant, site, seeded) = await SeedAsync(cs);
        var database = new MySqlDatabase(cs);
        var store = new ManagementStore(database);
        var service = Service(database, store);
        await CreatePlanAsync(database, tenant, site, seeded);
        try
        {
            var created = await store.CreatePromptAsync(tenant, site, new PromptWrite("新提问：哪个写作工具好用？", "zh-CN"));
            var instant = Assert.IsType<InstantCollectionView>(await service.CollectNewPromptsAsync(tenant, site, [created.PromptId]));
            Assert.Equal((InstantCollectionStatus.Started, 2, 20m, (string?)null), (instant.Status, instant.CheckCount, instant.EstimatedCredits, instant.ReasonCode));
            var run = instant.RunId!.Value.ToString();
            var row = Assert.Single(await RowsAsync(cs, "SELECT requested_by, idempotency_key, total_checks FROM visibility_runs WHERE id=UUID_TO_BIN(@run);",
                ("@run", run)));
            Assert.Equal(PromptMonitoringIdentity.InstantRequestedBy(site), row[0]);
            Assert.StartsWith($"{PromptMonitoringIdentity.InstantRequestedBy(site)}:{Today().ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture)}:",
                row[1], StringComparison.Ordinal);
            Assert.Equal("2", row[2]);

            // Only the new prompt, on both enabled cells; the existing prompt gets no check.
            var checks = await RowsAsync(cs, """
                SELECT BIN_TO_UUID(c.prompt_id), e.target_key FROM visibility_checks c JOIN run_engines e ON e.id=c.run_target_id
                WHERE c.run_id=UUID_TO_BIN(@run) ORDER BY e.target_key;
                """, ("@run", run));
            Assert.Equal(new[] { ChatGptKey, GeminiKey }.Order(StringComparer.Ordinal), checks.Select(c => c[1]));
            Assert.All(checks, c => Assert.Equal(created.PromptId.ToString(), c[0]));
            Assert.Equal(0, await CountAsync(cs, "SELECT COUNT(*) FROM visibility_checks WHERE prompt_id=UUID_TO_BIN(@prompt);",
                ("@prompt", seeded.ToString())));

            // Same reservation path as the daily slots: 2 × 10 credits held, one item per check, one reserve ledger entry.
            Assert.Equal(new string?[] { "20", "20", "held" }, Assert.Single(await RowsAsync(cs, """
                SELECT CAST(estimated_credits AS SIGNED), CAST(held_credits AS SIGNED), status FROM monitoring_credit_reservations
                WHERE run_id=UUID_TO_BIN(@run);
                """, ("@run", run))));
            Assert.Equal(2, await CountAsync(cs, "SELECT COUNT(*) FROM monitoring_credit_items WHERE run_id=UUID_TO_BIN(@run) AND amount=10 AND status='held';",
                ("@run", run)));
            Assert.Equal(1, await CountAsync(cs, "SELECT COUNT(*) FROM credit_ledger WHERE tenant_id=UUID_TO_BIN(@tenant) AND entry_type='reserve';",
                ("@tenant", tenant.ToString())));
            Assert.Equal(980, await CountAsync(cs, "SELECT CAST(available_credits AS SIGNED) FROM credit_accounts WHERE tenant_id=UUID_TO_BIN(@tenant);",
                ("@tenant", tenant.ToString())));

            // The monitoring banner's latest run now covers instant runs.
            Assert.Equal(instant.RunId, (await service.GetAsync(tenant, site)).Run?.RunId);

            // Failed checks do not count for the same-day dedup: the identical pair set is collected again under a numbered key.
            await ExecAsync(cs, "UPDATE visibility_checks SET status='failed' WHERE run_id=UUID_TO_BIN(@run);", ("@run", run));
            var retry = Assert.IsType<InstantCollectionView>(await service.CollectNewPromptsAsync(tenant, site, [created.PromptId]));
            Assert.Equal((InstantCollectionStatus.Started, 2), (retry.Status, retry.CheckCount));
            Assert.NotEqual(instant.RunId, retry.RunId);
            Assert.EndsWith(":2", Assert.Single(await RowsAsync(cs, "SELECT idempotency_key FROM visibility_runs WHERE id=UUID_TO_BIN(@run);",
                ("@run", retry.RunId!.Value.ToString())))[0], StringComparison.Ordinal);
        }
        finally { await ParkAsync(cs, site); }
    }

    // 2 + 3. Added models are collected alone; only removing creates nothing; the same day never collects a pair twice.
    [AnswerModeMySqlFact]
    public async Task AddedModelsCollectOnlyTheNewCellAndSameDayRepeatsAreDeduplicated()
    {
        var cs = ConnectionString();
        var (tenant, site, seeded) = await SeedAsync(cs);
        var database = new MySqlDatabase(cs);
        var store = new ManagementStore(database);
        var service = Service(database, store);
        await CreatePlanAsync(database, tenant, site, seeded);
        try
        {
            var prompt = await store.CreatePromptAsync(tenant, site, new PromptWrite("只在 ChatGPT 监测", "zh-CN", TargetKeys: [ChatGptKey]));
            var first = Assert.IsType<InstantCollectionView>(await service.CollectNewPromptsAsync(tenant, site, [prompt.PromptId]));
            Assert.Equal((InstantCollectionStatus.Started, 1), (first.Status, first.CheckCount));

            // + Gemini: exactly prompt × Gemini.
            var (before, after) = await PatchAsync(store, tenant, site, prompt.PromptId, new PromptPatch(TargetKeys: [ChatGptKey, GeminiKey]));
            var added = Assert.IsType<InstantCollectionView>(await service.CollectAddedTargetsAsync(tenant, site, before, after));
            Assert.Equal((InstantCollectionStatus.Started, 1, 10m), (added.Status, added.CheckCount, added.EstimatedCredits));
            Assert.Equal(new string?[] { prompt.PromptId.ToString(), GeminiKey }, Assert.Single(await RowsAsync(cs, """
                SELECT BIN_TO_UUID(c.prompt_id), e.target_key FROM visibility_checks c JOIN run_engines e ON e.id=c.run_target_id
                WHERE c.run_id=UUID_TO_BIN(@run);
                """, ("@run", added.RunId!.Value.ToString()))));

            // − ChatGPT only: nothing to collect.
            (before, after) = await PatchAsync(store, tenant, site, prompt.PromptId, new PromptPatch(TargetKeys: [GeminiKey]));
            Assert.Null(await service.CollectAddedTargetsAsync(tenant, site, before, after));

            // ChatGPT back the same day (reset to all): its check from the first run is still pending → already collected.
            (before, after) = await PatchAsync(store, tenant, site, prompt.PromptId, new PromptPatch(ResetTargetScope: true));
            var again = Assert.IsType<InstantCollectionView>(await service.CollectAddedTargetsAsync(tenant, site, before, after));
            Assert.Equal((InstantCollectionStatus.AlreadyCollected, (Guid?)null, 0), (again.Status, again.RunId, again.CheckCount));

            // Repeating the new-prompt trigger is deduplicated too, and no run was added by any of the above.
            Assert.Equal(InstantCollectionStatus.AlreadyCollected,
                (await service.CollectNewPromptsAsync(tenant, site, [prompt.PromptId]))!.Status);
            Assert.Equal(2, await CountRunsAsync(cs, site, PromptMonitoringIdentity.InstantRequestedBy(site)));
            Assert.Equal(2, await CountAsync(cs, "SELECT COUNT(*) FROM visibility_checks WHERE prompt_id=UUID_TO_BIN(@prompt);",
                ("@prompt", prompt.PromptId.ToString())));
        }
        finally { await ParkAsync(cs, site); }
    }

    // 4. Not enough credits: the prompt exists, but there is no run, no reservation and no ledger entry.
    [AnswerModeMySqlFact]
    public async Task InsufficientCreditsCreateNoRunReservationOrLedgerEntry()
    {
        var cs = ConnectionString();
        var (tenant, site, seeded) = await SeedAsync(cs, credits: 15m);
        var database = new MySqlDatabase(cs);
        var store = new ManagementStore(database);
        var service = Service(database, store);
        await CreatePlanAsync(database, tenant, site, seeded);
        try
        {
            var created = await store.CreatePromptAsync(tenant, site, new PromptWrite("余额不足时新增", "zh-CN"));
            var instant = Assert.IsType<InstantCollectionView>(await service.CollectNewPromptsAsync(tenant, site, [created.PromptId]));
            Assert.Equal((InstantCollectionStatus.Skipped, "CREDIT_INSUFFICIENT", (Guid?)null), (instant.Status, instant.ReasonCode, instant.RunId));
            Assert.Equal((2, 20m, (decimal?)5m), (instant.CheckCount, instant.EstimatedCredits, instant.Shortfall));
            Assert.False(string.IsNullOrWhiteSpace(instant.Message));

            Assert.Equal(0, await CountAsync(cs, "SELECT COUNT(*) FROM visibility_runs WHERE site_id=UUID_TO_BIN(@site);", ("@site", site.ToString())));
            Assert.Equal(0, await CountAsync(cs, "SELECT COUNT(*) FROM monitoring_credit_reservations WHERE site_id=UUID_TO_BIN(@site);",
                ("@site", site.ToString())));
            Assert.Equal(0, await CountAsync(cs, "SELECT COUNT(*) FROM credit_ledger WHERE tenant_id=UUID_TO_BIN(@tenant);", ("@tenant", tenant.ToString())));
            Assert.Equal(15, await CountAsync(cs, "SELECT CAST(available_credits AS SIGNED) FROM credit_accounts WHERE tenant_id=UUID_TO_BIN(@tenant);",
                ("@tenant", tenant.ToString())));
            Assert.NotNull(await store.GetPromptAsync(tenant, site, created.PromptId));
        }
        finally { await ParkAsync(cs, site); }
    }

    // 5. Batch create replayed with the same Idempotency-Key: the stored CreatedPromptIds trigger again, the dedup answers.
    [AnswerModeMySqlFact]
    public async Task BatchReplayKeepsASingleInstantRun()
    {
        var cs = ConnectionString();
        var (tenant, site, seeded) = await SeedAsync(cs);
        var database = new MySqlDatabase(cs);
        var store = new ManagementStore(database);
        var service = Service(database, store);
        await CreatePlanAsync(database, tenant, site, seeded);
        try
        {
            var key = $"instant-batch-{Guid.NewGuid():N}";
            IReadOnlyList<PromptImportRow> rows = [new("批量问题一", "zh-CN"), new("批量问题二", "zh-CN"), new(SeedContent, "zh-CN")];
            var first = await store.ImportAsync(tenant, site, rows, key);
            Assert.Equal((2, 1, 3), (first.CreatedCount, first.DuplicateCount, first.PromptIds.Count));
            var created = Assert.IsAssignableFrom<IReadOnlyList<Guid>>(first.CreatedPromptIds);
            Assert.Equal(2, created.Count);
            Assert.DoesNotContain(seeded, created);
            var instant = Assert.IsType<InstantCollectionView>(await service.CollectNewPromptsAsync(tenant, site, created));
            Assert.Equal((InstantCollectionStatus.Started, 4), (instant.Status, instant.CheckCount));

            var replay = await store.ImportAsync(tenant, site, rows, key);
            Assert.Equal(created, replay.CreatedPromptIds);
            Assert.Equal(InstantCollectionStatus.AlreadyCollected, (await service.CollectNewPromptsAsync(tenant, site, replay.CreatedPromptIds!))!.Status);
            Assert.Equal(1, await CountRunsAsync(cs, site, PromptMonitoringIdentity.InstantRequestedBy(site)));

            // The stored idempotent result carries CreatedPromptIds but never the instant result.
            var stored = Assert.Single(await RowsAsync(cs, "SELECT result FROM prompt_imports WHERE tenant_id=UUID_TO_BIN(@tenant) AND idempotency_key=@key;",
                ("@tenant", tenant.ToString()), ("@key", key)))[0]!;
            Assert.Contains("CreatedPromptIds", stored, StringComparison.Ordinal);
            Assert.DoesNotContain("InstantCollection", stored, StringComparison.Ordinal);
        }
        finally { await ParkAsync(cs, site); }
    }

    // 6. The 09:01 main slot leaves out pairs instantly collected on its local day; without them it is unchanged.
    [AnswerModeMySqlFact]
    public async Task MainSlotSkipsPairsAlreadyCollectedInstantlyThatLocalDay()
    {
        var cs = ConnectionString();
        var (tenant, site, seeded) = await SeedAsync(cs);
        var database = new MySqlDatabase(cs);
        var store = new ManagementStore(database);
        var service = Service(database, store);
        var schedules = new CollectionScheduleStore(database);
        var plan = await CreatePlanAsync(database, tenant, site, seeded);
        try
        {
            var added = await store.CreatePromptAsync(tenant, site, new PromptWrite("立即采集过的新提问", "zh-CN"));
            var addedRun = (await service.CollectNewPromptsAsync(tenant, site, [added.PromptId]))!.RunId!.Value.ToString();
            // 1990 slots (Asia/Shanghai 09:01 = 01:01 UTC) keep this plan ahead of every other due plan of the isolated database.
            await ExecAsync(cs, "UPDATE site_targets SET monitoring_enabled_at='1980-01-01' WHERE site_id=UUID_TO_BIN(@site) AND enabled=TRUE;",
                ("@site", site.ToString()));
            await ExecAsync(cs, "UPDATE collection_schedules SET next_slot_at='1990-01-02 01:01:00' WHERE id=UUID_TO_BIN(@id);", ("@id", plan.Id.ToString()));

            // (a) No instant run on 1990-01-02: the frozen slot equals the plan snapshot (the pre-change behaviour).
            Assert.Equal(1, await schedules.MaterializeDueSlotsAsync(new DateTimeOffset(1990, 1, 2, 1, 2, 0, TimeSpan.Zero), 1));
            var day1 = await SlotAsync(cs, plan.Id, "19900102T0901");
            Assert.Equal(("pending", (string?)null), (day1.Status, day1.SkipCode));
            Assert.Equal(await PlanJsonAsync(cs, plan.Id), day1.Json);
            Assert.Equal(new[] { seeded, added.PromptId }.Order(), day1.Selection.PromptIds);
            Assert.Null(day1.Selection.PromptTargets);
            await ParkSlotsAsync(cs, plan.Id);

            // (b) The instant run moved to 1990-01-03 10:00 local: its pairs leave that day's slot, except a failed check.
            await ExecAsync(cs, "UPDATE visibility_runs SET created_at='1990-01-03 02:00:00' WHERE id=UUID_TO_BIN(@run);", ("@run", addedRun));
            await ExecAsync(cs, """
                UPDATE visibility_checks c JOIN run_engines e ON e.id=c.run_target_id SET c.status='failed'
                WHERE c.run_id=UUID_TO_BIN(@run) AND e.target_key=@key;
                """, ("@run", addedRun), ("@key", GeminiKey));
            Assert.Equal(1, await schedules.MaterializeDueSlotsAsync(new DateTimeOffset(1990, 1, 3, 1, 2, 0, TimeSpan.Zero), 1));
            var day2 = await SlotAsync(cs, plan.Id, "19900103T0901");
            Assert.Equal(("pending", (string?)null), (day2.Status, day2.SkipCode));
            Assert.Equal(new[] { seeded, added.PromptId }.Order(), day2.Selection.PromptIds);
            var scope = Assert.Single(day2.Selection.PromptTargets!);
            Assert.Equal((added.PromptId, GeminiKey), (scope.PromptId, Assert.Single(scope.TargetKeys)));
            Assert.Equal(3, MonitoringMatrix.CountChecks(day2.Selection));
            // Only the slot is narrowed; the plan snapshot keeps every prompt × cell.
            var planSelection = await PlanAsync(cs, plan.Id);
            Assert.Null(planSelection.PromptTargets);
            Assert.Equal((2, 2), (planSelection.PromptIds.Count, planSelection.Targets.Count));
            await ParkSlotsAsync(cs, plan.Id);

            // (c) Every prompt × cell instantly collected on 1990-01-04 → skipped, no run and no reservation.
            await ExecAsync(cs, "UPDATE visibility_checks SET status='pending' WHERE run_id=UUID_TO_BIN(@run);", ("@run", addedRun));
            var seededInstant = Assert.IsType<InstantCollectionView>(await service.CollectNewPromptsAsync(tenant, site, [seeded]));
            Assert.Equal(InstantCollectionStatus.Started, seededInstant.Status);
            await ExecAsync(cs, "UPDATE visibility_runs SET created_at='1990-01-04 02:00:00' WHERE id IN (UUID_TO_BIN(@a), UUID_TO_BIN(@b));",
                ("@a", addedRun), ("@b", seededInstant.RunId!.Value.ToString()));
            var reservations = await CountAsync(cs, "SELECT COUNT(*) FROM monitoring_credit_reservations WHERE site_id=UUID_TO_BIN(@site);",
                ("@site", site.ToString()));
            Assert.Equal(1, await schedules.MaterializeDueSlotsAsync(new DateTimeOffset(1990, 1, 4, 1, 2, 0, TimeSpan.Zero), 1));
            var day3 = await SlotAsync(cs, plan.Id, "19900104T0901");
            Assert.Equal(("skipped", AutomaticMonitoringSelection.AlreadyCollectedCode, (string?)null), (day3.Status, day3.SkipCode, day3.RunId));
            Assert.Equal("本时隙的提问与模型当天已立即采集，未重复提交，也未预留积分。", day3.SkipMessage);
            Assert.DoesNotContain(await schedules.GetPendingSlotsAsync(100), s => s.ScheduleId == plan.Id);
            Assert.Equal(0, await CountRunsAsync(cs, site, $"schedule:{plan.Id:N}"));
            Assert.Equal(reservations, await CountAsync(cs, "SELECT COUNT(*) FROM monitoring_credit_reservations WHERE site_id=UUID_TO_BIN(@site);",
                ("@site", site.ToString())));
        }
        finally { await ParkAsync(cs, site); }
    }

    // 7. No plan yet: the new prompt sets up daily monitoring and its initial run is the instant result.
    [AnswerModeMySqlFact]
    public async Task NewPromptWithoutAPlanCreatesThePlanAndReturnsTheInitialRun()
    {
        var cs = ConnectionString();
        var (tenant, site, seeded) = await SeedAsync(cs);
        var database = new MySqlDatabase(cs);
        var store = new ManagementStore(database);
        // Catalog availability of Cloro depends on the database-wide key pool; this test only needs the targets marked available.
        var management = DispatchProxy.Create<IManagementStore, AvailableCatalogProxy>();
        ((AvailableCatalogProxy)(object)management).Inner = store;
        var service = Service(database, management);
        try
        {
            Assert.Equal(0, await CountAsync(cs, "SELECT COUNT(*) FROM collection_schedules WHERE site_id=UUID_TO_BIN(@site);", ("@site", site.ToString())));
            var created = await store.CreatePromptAsync(tenant, site, new PromptWrite("品牌的第一批新提问", "zh-CN"));
            var instant = Assert.IsType<InstantCollectionView>(await service.CollectNewPromptsAsync(tenant, site, [created.PromptId]));
            // The initial run covers every enabled prompt: 2 prompts × 2 cells.
            Assert.Equal((InstantCollectionStatus.Started, 4, 40m), (instant.Status, instant.CheckCount, instant.EstimatedCredits));
            Assert.Equal(PromptMonitoringIdentity.InitialRequestedBy(site), Assert.Single(await RowsAsync(cs,
                "SELECT requested_by FROM visibility_runs WHERE id=UUID_TO_BIN(@run);", ("@run", instant.RunId!.Value.ToString())))[0]);
            Assert.Equal(1, await CountAsync(cs, "SELECT COUNT(*) FROM collection_schedules WHERE site_id=UUID_TO_BIN(@site) AND name=@name AND enabled=TRUE;",
                ("@site", site.ToString()), ("@name", PromptMonitoringIdentity.ScheduleName)));
            Assert.Equal(0, await CountRunsAsync(cs, site, PromptMonitoringIdentity.InstantRequestedBy(site)));
            Assert.Equal(new[] { seeded, created.PromptId }.Select(id => id.ToString()).Order(StringComparer.Ordinal),
                (await RowsAsync(cs, "SELECT DISTINCT BIN_TO_UUID(prompt_id) FROM visibility_checks WHERE run_id=UUID_TO_BIN(@run);",
                    ("@run", instant.RunId.Value.ToString()))).Select(r => r[0]).Order(StringComparer.Ordinal));

            // A second new prompt now goes through the instant path of the existing plan.
            var next = await store.CreatePromptAsync(tenant, site, new PromptWrite("第二条新提问", "zh-CN"));
            var second = Assert.IsType<InstantCollectionView>(await service.CollectNewPromptsAsync(tenant, site, [next.PromptId]));
            Assert.Equal((InstantCollectionStatus.Started, 2), (second.Status, second.CheckCount));
            Assert.Equal(1, await CountRunsAsync(cs, site, PromptMonitoringIdentity.InstantRequestedBy(site)));
        }
        finally { await ParkAsync(cs, site); }
    }

    public class AvailableCatalogProxy : DispatchProxy
    {
        internal IManagementStore Inner { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IManagementStore.GetTargetCatalogAsync))
                return AvailableCatalogAsync(args is { Length: > 0 } && args[0] is CancellationToken ct ? ct : default);
            try { return targetMethod.Invoke(Inner, args); }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }

        private async Task<IReadOnlyList<CollectionTargetView>> AvailableCatalogAsync(CancellationToken ct)
            => (await Inner.GetTargetCatalogAsync(ct)).Select(t => t with { Availability = "available" }).ToArray();
    }

    private static async Task<(PromptView Before, PromptView After)> PatchAsync(ManagementStore store, Guid tenant, Guid site, Guid prompt,
        PromptPatch patch)
    {
        var before = (await store.GetPromptAsync(tenant, site, prompt))!;
        var after = await store.UpdatePromptAsync(tenant, site, prompt, patch with { ExpectedVersion = (int)before.ConfigVersion });
        return (before, after);
    }

    // An enabled 09:01 main plan (next slot in the future) over the seeded prompt and both cells; no run is created.
    private static Task<CollectionScheduleView> CreatePlanAsync(MySqlDatabase database, Guid tenant, Guid site, Guid prompt)
        => new CollectionScheduleStore(database).CreateAutomaticPlanAsync(new CreateCollectionScheduleCommand(tenant, site, PromptMonitoringIdentity.ScheduleName,
            "daily", null, PromptMonitoringIdentity.MainLocalTime, Zone, new CollectionScheduleSelection([prompt], [ChatGpt, Gemini]), Enabled: true));

    private sealed record SlotRow(string Status, string? SkipCode, string? SkipMessage, string? RunId, string Json, CollectionScheduleSelection Selection);

    private static async Task<SlotRow> SlotAsync(string cs, Guid schedule, string key)
    {
        var r = Assert.Single(await RowsAsync(cs, """
            SELECT status, skip_code, skip_message, BIN_TO_UUID(run_id), selection_snapshot FROM schedule_slots
            WHERE schedule_id=UUID_TO_BIN(@id) AND local_slot_key=@key;
            """, ("@id", schedule.ToString()), ("@key", key)));
        return new(r[0]!, r[1], r[2], r[3], r[4]!, JsonSerializer.Deserialize<CollectionScheduleSelection>(r[4]!, Web)!);
    }

    private static async Task<string> PlanJsonAsync(string cs, Guid schedule)
        => (await RowsAsync(cs, "SELECT selection_snapshot FROM collection_schedules WHERE id=UUID_TO_BIN(@id);", ("@id", schedule.ToString())))[0][0]!;

    private static async Task<CollectionScheduleSelection> PlanAsync(string cs, Guid schedule)
        => JsonSerializer.Deserialize<CollectionScheduleSelection>(await PlanJsonAsync(cs, schedule), Web)!;

    private static Task ParkSlotsAsync(string cs, Guid schedule) => ExecAsync(cs,
        "UPDATE schedule_slots SET status='skipped', skip_code='TEST_PARKED' WHERE schedule_id=UUID_TO_BIN(@id) AND status='pending';",
        ("@id", schedule.ToString()));

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

    private const string SeedContent = "哪个 AI 写作工具好用？";

    // Tenant + active brand (zone Asia/Shanghai, "Seed" brand term) + one enabled 'all' prompt; cloro chatgpt and gemini
    // enabled (activation time NULL, daily); credits as given. No subscription row: free_trial (20 prompts) applies.
    private static async Task<(Guid Tenant, Guid Site, Guid Prompt)> SeedAsync(string cs, decimal credits = 1000m)
    {
        var tenant = Guid.NewGuid();
        var site = Guid.NewGuid();
        var prompt = Guid.NewGuid();
        var version = Guid.NewGuid();
        await ExecAsync(cs, """
            INSERT INTO tenants (id,name,status,created_at,updated_at) VALUES (UUID_TO_BIN(@tenant),'instant-collection-test','active',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
            INSERT INTO sites (id,tenant_id,host,display_name,status,created_at,updated_at)
              VALUES (UUID_TO_BIN(@site),UUID_TO_BIN(@tenant),@host,'Seed','active',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
            INSERT INTO site_brand_settings (site_id,tenant_id,time_zone,created_at,updated_at)
              VALUES (UUID_TO_BIN(@site),UUID_TO_BIN(@tenant),@zone,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
            INSERT INTO subject_terms (id,tenant_id,site_id,subject_type,subject_id,namespace_key,term_type,display_value,normalized_key,created_at)
              VALUES (UUID_TO_BIN(@term),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),'brand',UUID_TO_BIN(@site),'text','name','Seed','seed',UTC_TIMESTAMP(6));
            INSERT INTO site_targets (id,tenant_id,site_id,provider,engine,capture_mode,country,locale,target_key,enabled,created_at,updated_at)
              VALUES (UUID_TO_BIN(@chatgpt),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),'cloro','chatgpt','scraped','US','en-US',@chatgptKey,TRUE,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6)),
                     (UUID_TO_BIN(@gemini),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),'cloro','gemini','scraped','US','en-US',@geminiKey,TRUE,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
            INSERT INTO prompts (id,site_id,content,locale,enabled,created_at,updated_at)
              VALUES (UUID_TO_BIN(@prompt),UUID_TO_BIN(@site),@content,'zh-CN',TRUE,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
            INSERT INTO prompt_versions (id,site_id,prompt_id,version,content,locale,branded_status,branded_source,created_at)
              VALUES (UUID_TO_BIN(@version),UUID_TO_BIN(@site),UUID_TO_BIN(@prompt),1,@content,'zh-CN','unbranded','system',UTC_TIMESTAMP(6));
            UPDATE prompts SET current_version_id=UUID_TO_BIN(@version) WHERE id=UUID_TO_BIN(@prompt);
            INSERT INTO credit_accounts (tenant_id,available_credits,lifetime_granted,updated_at) VALUES (UUID_TO_BIN(@tenant),@credits,@credits,UTC_TIMESTAMP(6));
            """, ("@tenant", tenant.ToString()), ("@site", site.ToString()), ("@host", $"instant-{site:N}.test"), ("@zone", Zone),
            ("@term", Guid.NewGuid().ToString()), ("@chatgpt", Guid.NewGuid().ToString()), ("@gemini", Guid.NewGuid().ToString()),
            ("@chatgptKey", CollectionIdentity.TargetKey("cloro", "chatgpt", "scraped", "US", "en-US")),
            ("@geminiKey", CollectionIdentity.TargetKey("cloro", "gemini", "scraped", "US", "en-US")),
            ("@prompt", prompt.ToString()), ("@version", version.ToString()), ("@content", SeedContent), ("@credits", credits));
        return (tenant, site, prompt);
    }

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

/// <summary>立即采集的隔离库用例：时隙展开领取全库最早到期的计划，所以与其他并行用例隔开，单独串行运行。</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class InstantCollectionSqlCollection
{
    public const string Name = "Instant collection isolated SQL";
}
