using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace Jogeoai.Tests;

/// <summary>
/// 隔离库（已执行 001–047、库名 jogeoai_answer_mode_*）上的手动采集与采集计划计费（11-27-sec-manual-run-charging）：
/// 手动采集命令预占「检测数 × 10」且重放不重复预占、余额不足不留运行；自定义计划的时隙同样预占、余额不足时隙跳过而计划保持启用；
/// 保留名称、改名锁与每站点数量上限各自的错误码；服务端写入发起人的手动采集不会让当天的每日监控主时隙被跳过。
/// 每个用例使用新的租户与品牌并在结束时只删除自己的行，不触碰共享 jogeoai 业务库；未配置 JOGEOAI_ANSWER_MODE_TEST_CONNECTION 时跳过。
/// 与立即采集用例同属一个串行集合：它们的时隙展开会领取全库最早到期的计划。
/// </summary>
[Collection(InstantCollectionSqlCollection.Name)]
public sealed class ManualRunChargingSqlTests
{
    private const string Zone = "Asia/Shanghai";
    private static readonly CloroVisibilityTarget ChatGpt = new("cloro", "chatgpt", "en-US", "US", "{}");
    private static readonly CloroVisibilityTarget Gemini = new("cloro", "gemini", "en-US", "US", "{}");
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static string ConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("JOGEOAI_ANSWER_MODE_TEST_CONNECTION")!;
        Assert.StartsWith("jogeoai_answer_mode_", new MySqlConnectionStringBuilder(connectionString).Database, StringComparison.Ordinal);
        return connectionString;
    }

    // The command VisibilityRunsController builds for the `targets` shape: server-side initiator, HTTP request hash, charged.
    private static CreateCloroVisibilityRunCommand ManualCommand(Brand brand, string key, string? requestedBy = null) => new(
        brand.Tenant, brand.Site, brand.Prompts, [ChatGpt, Gemini], key, requestedBy ?? $"user:{Guid.NewGuid():N}",
        HttpRequestHash: Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray())).ToLowerInvariant(), ChargeMonitoringCredits: true);

    // RD1: 2 prompts × 2 targets × 1 sample = 4 checks, 40 credits held; the same idempotency key never reserves twice.
    [AnswerModeMySqlFact]
    public Task ManualRunReservesChecksTimesTenAndAReplayLeavesExactlyOneReservation() => WithBrandsAsync(async (cs, brands) =>
    {
        var brand = brands[0];
        var runs = new CloroVisibilityStore(new MySqlDatabase(cs));
        var command = ManualCommand(brand, $"manual-charge-{Guid.NewGuid():N}");

        var created = await runs.CreateRunAsync(command);
        Assert.Equal(4, created.TotalChecks);
        Assert.Equal("1|40.000|40.000|held|10.000", string.Join("|", (await RowsAsync(cs, """
            SELECT COUNT(*), MAX(estimated_credits), MAX(held_credits), MAX(status), MAX(unit_price)
            FROM monitoring_credit_reservations WHERE tenant_id=UUID_TO_BIN(@tenant) AND run_id=UUID_TO_BIN(@run);
            """, ("@tenant", brand.Tenant.ToString()), ("@run", created.RunId.ToString())))[0]));
        Assert.Equal("4|40.000", string.Join("|", (await RowsAsync(cs, """
            SELECT COUNT(*), SUM(amount) FROM monitoring_credit_items
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND run_id=UUID_TO_BIN(@run) AND status='held';
            """, ("@tenant", brand.Tenant.ToString()), ("@run", created.RunId.ToString())))[0]));
        Assert.Equal("runs=1|checks=4|reservations=1|ledger=1|available=960.000|reserved=40.000", await FootprintAsync(cs, brand.Tenant));
        Assert.Equal(command.RequestedBy, (await RowsAsync(cs, "SELECT requested_by FROM visibility_runs WHERE id=UUID_TO_BIN(@run);",
            ("@run", created.RunId.ToString())))[0][0]);

        // Replay of the same key and request: the same Run, nothing reserved again.
        var replayed = await runs.CreateRunAsync(command);
        Assert.Equal(created.RunId, replayed.RunId);
        // Another member replaying the same HTTP request (their own server-side initiator) also gets the same Run.
        var byAnotherMember = await runs.CreateRunAsync(command with { RequestedBy = $"user:{Guid.NewGuid():N}" });
        Assert.Equal(created.RunId, byAnotherMember.RunId);
        Assert.Equal("runs=1|checks=4|reservations=1|ledger=1|available=960.000|reserved=40.000", await FootprintAsync(cs, brand.Tenant));
        // The same key with a different request is a conflict and reserves nothing either.
        var conflict = await Assert.ThrowsAsync<CollectorStoreException>(() => runs.CreateRunAsync(ManualCommand(brand, command.IdempotencyKey)));
        Assert.Equal(("RUN_IDEMPOTENCY_CONFLICT", 409), (conflict.Code, conflict.StatusCode));
        Assert.Equal("runs=1|checks=4|reservations=1|ledger=1|available=960.000|reserved=40.000", await FootprintAsync(cs, brand.Tenant));
    });

    // RD1: 40 credits needed, 30 available: 402 and the whole transaction rolls back (no Run, check, task or reservation).
    [AnswerModeMySqlFact]
    public Task ManualRunWithInsufficientBalanceIs402AndLeavesNoRun() => WithBrandsAsync(async (cs, brands) =>
    {
        var brand = brands[0];
        var refused = await Assert.ThrowsAsync<CollectorStoreException>(() => new CloroVisibilityStore(new MySqlDatabase(cs))
            .CreateRunAsync(ManualCommand(brand, $"manual-charge-{Guid.NewGuid():N}")));
        Assert.Equal(("CREDIT_INSUFFICIENT", 402), (refused.Code, refused.StatusCode));
        Assert.Equal("runs=0|checks=0|reservations=0|ledger=0|available=30.000|reserved=0.000", await FootprintAsync(cs, brand.Tenant));
        Assert.Equal(0, await CountAsync(cs, "SELECT COUNT(*) FROM outbox_events WHERE tenant_id=UUID_TO_BIN(@tenant);", ("@tenant", brand.Tenant.ToString())));
    }, credits: 30m);

    // RD2: a custom-name plan's slot run is charged like the monitoring plan's: 2 prompts × 2 targets = 40 credits.
    [AnswerModeMySqlFact]
    public Task CustomPlanSlotReservesCredits() => WithBrandsAsync(async (cs, brands) =>
    {
        var brand = brands[0];
        var database = new MySqlDatabase(cs);
        var schedules = new CollectionScheduleStore(database);
        var plan = await schedules.CreateAsync(new CreateCollectionScheduleCommand(brand.Tenant, brand.Site, $"custom-{Guid.NewGuid():N}",
            "daily", null, "10:00", Zone, new CollectionScheduleSelection(brand.Prompts, [ChatGpt, Gemini]), Enabled: true));
        var slot = await InsertPendingSlotAsync(cs, brand, plan, "20990105T1000");

        Assert.True(await schedules.ProcessSlotAsync(slot, new CloroVisibilityStore(database)));
        var row = Assert.Single(await RowsAsync(cs, """
            SELECT slot.status, slot.skip_code, run.requested_by, run.total_checks, reservation.held_credits, reservation.status
            FROM schedule_slots slot JOIN visibility_runs run ON run.id=slot.run_id
            LEFT JOIN monitoring_credit_reservations reservation ON reservation.run_id=run.id
            WHERE slot.id=UUID_TO_BIN(@slot);
            """, ("@slot", slot.Id.ToString())));
        Assert.Equal(new string?[] { "completed", null, $"schedule:{plan.Id:N}", "4", "40.000", "held" }, row);
        Assert.Equal("runs=1|checks=4|reservations=1|ledger=1|available=960.000|reserved=40.000", await FootprintAsync(cs, brand.Tenant));
    });

    // RD2: the custom plan goes through the same 402 handling as the monitoring plan: slot skipped, plan still enabled.
    [AnswerModeMySqlFact]
    public Task CustomPlanSlotWithInsufficientBalanceIsSkippedAndThePlanStaysEnabled() => WithBrandsAsync(async (cs, brands) =>
    {
        var brand = brands[0];
        var database = new MySqlDatabase(cs);
        var schedules = new CollectionScheduleStore(database);
        var plan = await schedules.CreateAsync(new CreateCollectionScheduleCommand(brand.Tenant, brand.Site, $"custom-{Guid.NewGuid():N}",
            "daily", null, "10:00", Zone, new CollectionScheduleSelection(brand.Prompts, [ChatGpt, Gemini]), Enabled: true));
        var slot = await InsertPendingSlotAsync(cs, brand, plan, "20990105T1000");

        Assert.False(await schedules.ProcessSlotAsync(slot, new CloroVisibilityStore(database)));
        Assert.Equal(new string?[] { "skipped", "CREDIT_INSUFFICIENT", null }, Assert.Single(await RowsAsync(cs,
            "SELECT status, skip_code, BIN_TO_UUID(run_id) FROM schedule_slots WHERE id=UUID_TO_BIN(@slot);", ("@slot", slot.Id.ToString()))));
        var after = Assert.Single((await schedules.ListAsync(brand.Tenant, brand.Site, null, 100)).Items, item => item.Id == plan.Id);
        Assert.True(after.Enabled);
        Assert.Equal((plan.NextSlotAt, plan.Version), (after.NextSlotAt, after.Version));
        Assert.Equal("runs=0|checks=0|reservations=0|ledger=0|available=30.000|reserved=0.000", await FootprintAsync(cs, brand.Tenant));
    }, credits: 30m);

    // RD3: reserved names (create and rename), the rename lock of a monitoring plan and the per-site cap on custom plans.
    [AnswerModeMySqlFact]
    public Task ReservedNamesRenameLockAndPerSiteCapEachReturnTheirCode() => WithBrandsAsync(async (cs, brands) =>
    {
        var brand = brands[0];
        var database = new MySqlDatabase(cs);
        var schedules = new CollectionScheduleStore(database, scheduleOptions: Options.Create(new CollectionScheduleOptions { MaxCustomPerSite = 3 }));
        var selection = new CollectionScheduleSelection(brand.Prompts, [ChatGpt]);
        CreateCollectionScheduleCommand Create(string name) => new(brand.Tenant, brand.Site, name, "daily", null, "10:00", Zone, selection);
        async Task<(string Code, int Status)> RefusedAsync(Func<Task> action)
        {
            var refused = await Assert.ThrowsAsync<CollectorStoreException>(action);
            return (refused.Code, refused.StatusCode);
        }
        Task<int> PlansAsync(string where = "1=1") => CountAsync(cs,
            $"SELECT COUNT(*) FROM collection_schedules WHERE site_id=UUID_TO_BIN(@site) AND {where};", ("@site", brand.Site.ToString()));

        // The public create path refuses every reserved name; nothing is inserted.
        foreach (var name in new[] { PromptMonitoringIdentity.ScheduleName, PromptMonitoringIdentity.NightScheduleName,
                     PromptMonitoringIdentity.ScheduleName.ToUpperInvariant() })
            Assert.Equal(("SCHEDULE_NAME_RESERVED", 409), await RefusedAsync(() => schedules.CreateAsync(Create(name))));
        Assert.Equal(("INVALID_SCHEDULE_NAME", 400), await RefusedAsync(() => schedules.CreateAsync(Create(PromptMonitoringIdentity.AppScheduleName))));
        Assert.Equal(0, await PlansAsync());

        // The internal path (PromptMonitoringService) still creates the monitoring plan; it cannot be renamed afterwards.
        var main = await schedules.CreateAutomaticPlanAsync(Create(PromptMonitoringIdentity.ScheduleName));
        Assert.Equal(PromptMonitoringIdentity.ScheduleName, main.Name);
        Assert.Equal(("SCHEDULE_NAME_LOCKED", 409), await RefusedAsync(() => schedules.UpdateAsync(
            new UpdateCollectionScheduleCommand(brand.Tenant, brand.Site, main.Id, main.Version, Name: "renamed-monitoring"))));
        Assert.Equal(("SCHEDULE_NAME_LOCKED", 409), await RefusedAsync(() => schedules.UpdateAsync(
            new UpdateCollectionScheduleCommand(brand.Tenant, brand.Site, main.Id, main.Version, Name: PromptMonitoringIdentity.NightScheduleName))));
        // Sending the unchanged name with another change is not a rename.
        var retimed = await schedules.UpdateAsync(new UpdateCollectionScheduleCommand(brand.Tenant, brand.Site, main.Id, main.Version,
            Name: PromptMonitoringIdentity.ScheduleName, LocalTime: "09:01"));
        Assert.Equal((PromptMonitoringIdentity.ScheduleName, "09:01", main.Version + 1), (retimed!.Name, retimed.LocalTime, retimed.Version));

        // A custom plan can be renamed, but not to a reserved name; the App plan name keeps its own 400.
        var custom = await schedules.CreateAsync(Create("custom-a"));
        foreach (var name in new[] { PromptMonitoringIdentity.ScheduleName, PromptMonitoringIdentity.NightScheduleName, "JOGEOAI:auto-prompt-monitoring:V1" })
            Assert.Equal(("SCHEDULE_NAME_RESERVED", 409), await RefusedAsync(() => schedules.UpdateAsync(
                new UpdateCollectionScheduleCommand(brand.Tenant, brand.Site, custom.Id, custom.Version, Name: name))));
        Assert.Equal(("INVALID_SCHEDULE_NAME", 400), await RefusedAsync(() => schedules.UpdateAsync(
            new UpdateCollectionScheduleCommand(brand.Tenant, brand.Site, custom.Id, custom.Version, Name: PromptMonitoringIdentity.AppScheduleName))));
        var renamed = await schedules.UpdateAsync(new UpdateCollectionScheduleCommand(brand.Tenant, brand.Site, custom.Id, custom.Version, Name: "custom-b"));
        Assert.Equal(("custom-b", custom.Version + 1), (renamed!.Name, renamed.Version));
        // Still exactly one plan answers to the monitoring name (the name column compares case-insensitively).
        Assert.Equal(1, await PlansAsync($"name='{PromptMonitoringIdentity.ScheduleName}'"));

        // Cap 3 counts custom plans only (the monitoring plan is not one of them): the fourth custom plan is refused.
        await schedules.CreateAsync(Create("custom-c"));
        await schedules.CreateAsync(Create("custom-d"));
        Assert.Equal(("SCHEDULE_LIMIT_REACHED", 409), await RefusedAsync(() => schedules.CreateAsync(Create("custom-e"))));
        Assert.Equal(4, await PlansAsync());
        Assert.Equal(0, await PlansAsync("name='custom-e'"));
        // The internal path is not capped: the night plan is created next to three custom plans.
        var night = await schedules.CreateAutomaticPlanAsync(Create(PromptMonitoringIdentity.NightScheduleName));
        Assert.Equal(PromptMonitoringIdentity.NightScheduleName, night.Name);
        Assert.Equal(5, await PlansAsync());
        // Another brand of the same deployment has its own count; the default cap is 20.
        var other = brands[1];
        var defaults = new CollectionScheduleStore(database);
        var otherSelection = new CollectionScheduleSelection(other.Prompts, [ChatGpt]);
        for (var i = 0; i < CollectionScheduleOptions.DefaultMaxCustomPerSite; i++)
            await defaults.CreateAsync(new CreateCollectionScheduleCommand(other.Tenant, other.Site, $"custom-{i:00}", "daily", null, "10:00", Zone, otherSelection));
        Assert.Equal(("SCHEDULE_LIMIT_REACHED", 409), await RefusedAsync(() => defaults.CreateAsync(
            new CreateCollectionScheduleCommand(other.Tenant, other.Site, "custom-20", "daily", null, "10:00", Zone, otherSelection))));

        // PromptMonitoringIdentity.IsReservedScheduleName is only the first pass. The store must reach the same decision as
        // MySQL comparing in the name column's own collation, also for spellings the C# fold leaves alone (a letter without
        // a Unicode decomposition). Each probe asks MySQL first, against the first brand's real main and night plan rows and
        // in the same `name = @name` form the plan lookups use, so nothing here hard-codes what a collation version
        // considers equal. Whether a given probe is equal was not checked on a database when this was written.
        Task<int> NamedAsync(Brand owner, string name) => CountAsync(cs,
            "SELECT COUNT(*) FROM collection_schedules WHERE site_id=UUID_TO_BIN(@site) AND name=@name;",
            ("@site", owner.Site.ToString()), ("@name", name));
        var probes = new[]
        {
            "JOGEOAI:AUTO-PROMPT-MONITORING:V1",                    // the control: the C# check folds case as well
            "jøgeoai:auto-prompt-monitoring:v1",               // "ø" for "o"
            "jogeoai:autø-prompt-monitøring:night:v1",    // the same in the night plan name
            "jogeoai:auto-prompt-monitoring:v1 weekly",             // differs in real characters: never reserved
        };
        Assert.Equal(1, await NamedAsync(brand, probes[0]));
        Assert.Equal(0, await NamedAsync(brand, probes[^1]));
        var probeBrand = brands[2];
        var probeSelection = new CollectionScheduleSelection(probeBrand.Prompts, [ChatGpt]);
        CreateCollectionScheduleCommand Probe(string name) => new(probeBrand.Tenant, probeBrand.Site, name, "daily", null, "10:00", Zone, probeSelection);
        var renameTarget = await defaults.CreateAsync(Probe("probe-rename"));
        foreach (var probe in probes)
        {
            var reserved = await NamedAsync(brand, probe) > 0;
            var createFailure = await Record.ExceptionAsync(() => defaults.CreateAsync(Probe(probe)));
            var renameFailure = await Record.ExceptionAsync(async () => renameTarget = (await defaults.UpdateAsync(new UpdateCollectionScheduleCommand(
                probeBrand.Tenant, probeBrand.Site, renameTarget.Id, renameTarget.Version, Name: probe)))!);
            if (reserved)
            {
                foreach (var refusal in new[] { createFailure, renameFailure })
                {
                    var refused = Assert.IsType<CollectorStoreException>(refusal);
                    Assert.Equal(("SCHEDULE_NAME_RESERVED", 409), (refused.Code, refused.StatusCode));
                }
                // Neither the insert nor the rename was kept.
                Assert.Equal(0, await NamedAsync(probeBrand, probe));
            }
            else
            {
                Assert.Null(createFailure);
                Assert.Null(renameFailure);
                Assert.Equal(2, await NamedAsync(probeBrand, probe));
            }
        }
    }, brandCount: 3);

    // RD4: HasMonitoringRunForLocalSlotAsync looks for requested_by = prompt-monitor:{site} or schedule:{plan} on the slot's
    // local day. A manual run stored with the server-side initiator is neither, so today's charged main slot still runs.
    // The control brand shows what a client-supplied "prompt-monitor:{site}" used to do: the main slot is skipped.
    [AnswerModeMySqlFact]
    public Task ManualRunWithServerSideInitiatorDoesNotSkipTodaysMainSlot() => WithBrandsAsync(async (cs, brands) =>
    {
        var database = new MySqlDatabase(cs);
        var schedules = new CollectionScheduleStore(database);
        var runs = new CloroVisibilityStore(database);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(Zone);

        // The main slot of the plan-zone local day on which the manual run was created (taken from the run itself, so the
        // test does not depend on the wall clock staying on one day).
        async Task<(CollectionScheduleView Plan, PendingCollectionSlot Slot)> MainSlotAfterAsync(Brand brand, string requestedBy)
        {
            var plan = await schedules.CreateAutomaticPlanAsync(new CreateCollectionScheduleCommand(brand.Tenant, brand.Site,
                PromptMonitoringIdentity.ScheduleName, "daily", null, PromptMonitoringIdentity.MainLocalTime, Zone,
                new CollectionScheduleSelection(brand.Prompts, [ChatGpt, Gemini]), Enabled: true));
            var manualRun = await runs.CreateRunAsync(ManualCommand(brand, $"manual-charge-{Guid.NewGuid():N}", requestedBy));
            var key = $"{TimeZoneInfo.ConvertTime(manualRun.CreatedAt, zone).ToString("yyyyMMdd", CultureInfo.InvariantCulture)}T0901";
            return (plan, await InsertPendingSlotAsync(cs, brand, plan, key));
        }

        var manual = brands[0];
        var (plan, slot) = await MainSlotAfterAsync(manual, $"user:{Guid.NewGuid():N}");
        Assert.True(await schedules.ProcessSlotAsync(slot, runs));
        Assert.Equal(new string?[] { "completed", null, $"schedule:{plan.Id:N}", "40.000" }, Assert.Single(await RowsAsync(cs, """
            SELECT slot.status, slot.skip_code, run.requested_by, reservation.held_credits
            FROM schedule_slots slot JOIN visibility_runs run ON run.id=slot.run_id
            JOIN monitoring_credit_reservations reservation ON reservation.run_id=run.id
            WHERE slot.id=UUID_TO_BIN(@slot);
            """, ("@slot", slot.Id.ToString()))));
        // Manual run + main slot run, both charged.
        Assert.Equal("runs=2|checks=8|reservations=2|ledger=2|available=920.000|reserved=80.000", await FootprintAsync(cs, manual.Tenant));

        // The Development bridge constant is not a monitoring identity either.
        var bridge = brands[1];
        var (_, bridgeSlot) = await MainSlotAfterAsync(bridge, "dev-bridge");
        Assert.True(await schedules.ProcessSlotAsync(bridgeSlot, runs));

        var spoofed = brands[2];
        var (_, spoofedSlot) = await MainSlotAfterAsync(spoofed, PromptMonitoringIdentity.InitialRequestedBy(spoofed.Site));
        Assert.False(await schedules.ProcessSlotAsync(spoofedSlot, runs));
        Assert.Equal(new string?[] { "skipped", "INITIAL_RUN_ALREADY_EXECUTED", null }, Assert.Single(await RowsAsync(cs,
            "SELECT status, skip_code, BIN_TO_UUID(run_id) FROM schedule_slots WHERE id=UUID_TO_BIN(@slot);", ("@slot", spoofedSlot.Id.ToString()))));
    }, brandCount: 3);

    private sealed record Brand(Guid Tenant, Guid Site, Guid[] Prompts);

    // Seeds the brands, runs the body, then removes this test's own rows. A cleanup failure is reported only when the body
    // itself passed, so it never hides the assertion that failed.
    private static async Task WithBrandsAsync(Func<string, Brand[], Task> body, decimal credits = 1000m, int brandCount = 1)
    {
        var cs = ConnectionString();
        var brands = new List<Brand>();
        Exception? failure = null;
        try
        {
            for (var i = 0; i < brandCount; i++) brands.Add(await SeedAsync(cs, credits));
            await body(cs, brands.ToArray());
        }
        catch (Exception exception) { failure = exception; }
        foreach (var brand in brands)
        {
            try { await CleanupAsync(cs, brand); }
            catch (MySqlException) when (failure is not null) { }
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    // Tenant + active brand (zone Asia/Shanghai) + two enabled prompts; cloro chatgpt and gemini enabled; credits as given.
    private static async Task<Brand> SeedAsync(string cs, decimal credits)
    {
        var tenant = Guid.NewGuid();
        var site = Guid.NewGuid();
        await ExecAsync(cs, """
            INSERT INTO tenants (id,name,status,created_at,updated_at) VALUES (UUID_TO_BIN(@tenant),'manual-charging-test','active',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
            INSERT INTO sites (id,tenant_id,host,display_name,status,created_at,updated_at)
              VALUES (UUID_TO_BIN(@site),UUID_TO_BIN(@tenant),@host,'Charging','active',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
            INSERT INTO site_brand_settings (site_id,tenant_id,time_zone,created_at,updated_at)
              VALUES (UUID_TO_BIN(@site),UUID_TO_BIN(@tenant),@zone,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
            INSERT INTO site_targets (id,tenant_id,site_id,provider,engine,capture_mode,country,locale,target_key,enabled,created_at,updated_at)
              VALUES (UUID_TO_BIN(@chatgpt),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),'cloro','chatgpt','scraped','US','en-US',@chatgptKey,TRUE,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6)),
                     (UUID_TO_BIN(@gemini),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),'cloro','gemini','scraped','US','en-US',@geminiKey,TRUE,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
            INSERT INTO credit_accounts (tenant_id,available_credits,lifetime_granted,updated_at) VALUES (UUID_TO_BIN(@tenant),@credits,@credits,UTC_TIMESTAMP(6));
            """, ("@tenant", tenant.ToString()), ("@site", site.ToString()), ("@host", $"charging-{site:N}.test"), ("@zone", Zone),
            ("@chatgpt", Guid.NewGuid().ToString()), ("@gemini", Guid.NewGuid().ToString()),
            ("@chatgptKey", CollectionIdentity.TargetKey(ChatGpt)), ("@geminiKey", CollectionIdentity.TargetKey(Gemini)), ("@credits", credits));
        var prompts = new[] { Guid.NewGuid(), Guid.NewGuid() };
        foreach (var prompt in prompts)
            await ExecAsync(cs, """
                INSERT INTO prompts (id,site_id,content,locale,enabled,created_at,updated_at)
                  VALUES (UUID_TO_BIN(@prompt),UUID_TO_BIN(@site),@content,'zh-CN',TRUE,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
                INSERT INTO prompt_versions (id,site_id,prompt_id,version,content,locale,branded_status,branded_source,created_at)
                  VALUES (UUID_TO_BIN(@version),UUID_TO_BIN(@site),UUID_TO_BIN(@prompt),1,@content,'zh-CN','unbranded','system',UTC_TIMESTAMP(6));
                UPDATE prompts SET current_version_id=UUID_TO_BIN(@version) WHERE id=UUID_TO_BIN(@prompt);
                """, ("@prompt", prompt.ToString()), ("@site", site.ToString()), ("@version", Guid.NewGuid().ToString()),
                ("@content", $"计费测试提问 {prompt:N}？"));
        return new Brand(tenant, site, prompts.Order().ToArray());
    }

    // The slot row a materialized due plan would have left; built directly so no other plan of the database is claimed.
    private static async Task<PendingCollectionSlot> InsertPendingSlotAsync(string cs, Brand brand, CollectionScheduleView plan, string localKey)
    {
        var slot = Guid.NewGuid();
        await ExecAsync(cs, """
            INSERT INTO schedule_slots (id,schedule_id,local_slot_key,selection_snapshot,status,missed_slots,created_at)
            VALUES (UUID_TO_BIN(@id),UUID_TO_BIN(@schedule),@key,@selection,'pending',0,UTC_TIMESTAMP(6));
            """, ("@id", slot.ToString()), ("@schedule", plan.Id.ToString()), ("@key", localKey),
            ("@selection", JsonSerializer.Serialize(plan.Selection, Web)));
        return new PendingCollectionSlot(slot, plan.Id, brand.Tenant, brand.Site, localKey, plan.Selection, 0);
    }

    private static async Task<string> FootprintAsync(string cs, Guid tenant)
    {
        var arg = ("@tenant", (object?)tenant.ToString());
        var account = (await RowsAsync(cs, "SELECT available_credits, reserved_credits FROM credit_accounts WHERE tenant_id=UUID_TO_BIN(@tenant);", arg))[0];
        return string.Join("|",
            "runs=" + await CountAsync(cs, "SELECT COUNT(*) FROM visibility_runs WHERE tenant_id=UUID_TO_BIN(@tenant);", arg),
            "checks=" + await CountAsync(cs, "SELECT COUNT(*) FROM visibility_checks WHERE tenant_id=UUID_TO_BIN(@tenant);", arg),
            "reservations=" + await CountAsync(cs, "SELECT COUNT(*) FROM monitoring_credit_reservations WHERE tenant_id=UUID_TO_BIN(@tenant);", arg),
            "ledger=" + await CountAsync(cs, "SELECT COUNT(*) FROM credit_ledger WHERE tenant_id=UUID_TO_BIN(@tenant);", arg),
            "available=" + account[0], "reserved=" + account[1]);
    }

    // Only this test's tenant: children first. No provider task of these runs can be leased afterwards (they are deleted).
    private static async Task CleanupAsync(string cs, Brand brand)
    {
        foreach (var sql in new[]
                 {
                     "DELETE FROM credit_ledger WHERE tenant_id=UUID_TO_BIN(@tenant);",
                     "DELETE FROM monitoring_credit_items WHERE tenant_id=UUID_TO_BIN(@tenant);",
                     "DELETE FROM monitoring_credit_reservations WHERE tenant_id=UUID_TO_BIN(@tenant);",
                     "DELETE s FROM schedule_slots s JOIN collection_schedules p ON p.id=s.schedule_id WHERE p.tenant_id=UUID_TO_BIN(@tenant);",
                     "DELETE FROM collection_schedules WHERE tenant_id=UUID_TO_BIN(@tenant);",
                     "DELETE t FROM provider_tasks t JOIN visibility_runs r ON r.id=t.run_id WHERE r.tenant_id=UUID_TO_BIN(@tenant);",
                     "DELETE FROM visibility_checks WHERE tenant_id=UUID_TO_BIN(@tenant);",
                     "DELETE e FROM run_engines e JOIN visibility_runs r ON r.id=e.run_id WHERE r.tenant_id=UUID_TO_BIN(@tenant);",
                     "DELETE FROM outbox_events WHERE tenant_id=UUID_TO_BIN(@tenant);",
                     "DELETE FROM visibility_runs WHERE tenant_id=UUID_TO_BIN(@tenant);",
                     "UPDATE prompts SET current_version_id=NULL WHERE site_id=UUID_TO_BIN(@site);",
                     "DELETE FROM prompt_versions WHERE site_id=UUID_TO_BIN(@site);",
                     "DELETE FROM prompts WHERE site_id=UUID_TO_BIN(@site);",
                     "DELETE FROM site_targets WHERE tenant_id=UUID_TO_BIN(@tenant);",
                     "DELETE FROM site_brand_settings WHERE tenant_id=UUID_TO_BIN(@tenant);",
                     "DELETE FROM credit_accounts WHERE tenant_id=UUID_TO_BIN(@tenant);",
                     "DELETE FROM sites WHERE tenant_id=UUID_TO_BIN(@tenant);",
                     "DELETE FROM tenants WHERE id=UUID_TO_BIN(@tenant);",
                 })
            await ExecAsync(cs, sql, ("@tenant", brand.Tenant.ToString()), ("@site", brand.Site.ToString()));
    }

    private static async Task<int> CountAsync(string cs, string sql, params (string Name, object? Value)[] args)
        => int.Parse((await RowsAsync(cs, sql, args))[0][0]!, CultureInfo.InvariantCulture);

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
                row[i] = reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
            rows.Add(row);
        }
        return rows;
    }
}
