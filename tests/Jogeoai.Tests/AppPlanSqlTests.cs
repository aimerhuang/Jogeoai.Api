using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using MySqlConnector;
using static Jogeoai.Tests.TeamMonitoringConfigSqlTests;

namespace Jogeoai.Tests;

/// <summary>
/// 隔离库（已执行 001–049、库名含 _test）上的 App 计划（implement 4.1 / 4.1a / 4.1b 与时隙保护，总 PRD KD-24）：只启用 App 的品牌、
/// 两个渠道都启用的品牌、只有 API 的品牌、未开放的租户；暂停的跟随；快照与版本；App 计划的时隙不进入 API 的 Run 路径。
/// 未设置 JOGEOAI_APP_CHANNEL_TEST_CONNECTION 时跳过，绝不使用业务库 jogeoai。每个用例只用自己新建的租户，结束时删除该租户的全部行，
/// 并核对整库每张表的行数与用例开始前相同。与迁移用例同一个集合：它们会执行 DDL，不能并行。
/// </summary>
[Collection(AppChannelSqlCollection.Name)]
public sealed partial class AppPlanSqlTests
{
    private const string PlanZone = "Asia/Shanghai";
    private const string MainName = PromptMonitoringIdentity.ScheduleName;
    private const string NightName = PromptMonitoringIdentity.NightScheduleName;
    private const string AppName = PromptMonitoringIdentity.AppScheduleName;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions Api = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly SiteTargetWrite ChatGpt = new("cloro", "chatgpt", "scraped", "US", "en-US");
    private static readonly SiteTargetWrite GeminiTwice = new("cloro", "gemini", "scraped", "US", "en-US", true, null, MonitoringFrequency.TwiceDaily);
    private static readonly CloroVisibilityTarget ChatGptTarget = new("cloro", "chatgpt", "en-US", "US", "{}");
    private static readonly CloroVisibilityTarget GeminiTarget = new("cloro", "gemini", "en-US", "US", "{}");
    private static readonly string ChatGptKey = CollectionIdentity.TargetKey(ChatGptTarget);

    private static SiteTargetWrite App(string engine, bool enabled = true)
        => new("app_collector", engine, "app", "CN", "zh-CN", enabled, null, MonitoringFrequency.Weekly);

    private static CloroVisibilityTarget AppTarget(string engine) => new("app_collector", engine, "zh-CN", "CN", "{}");

    private static string AppKey(string engine) => CollectionIdentity.AppTargetKey(engine);

    private static (string, int) Code(CollectorStoreException exception) => (exception.Code, exception.StatusCode);

    /// <summary>一个租户上的一组服务：<see cref="Service"/> 与 <see cref="Schedules"/> 按该租户是否开放 App 渠道构造。</summary>
    private sealed record Rig(string Cs, MySqlDatabase Database, Guid Tenant, ManagementStore Store, PromptMonitoringService Service,
        CollectionScheduleStore Schedules, CloroVisibilityStore Runs)
    {
        public async Task<SiteTargetsView> PutAsync(Guid site, params SiteTargetWrite[] targets)
            => await Store.PutSiteTargetsAsync(Tenant, site,
                new SiteTargetsWrite((await Store.GetSiteTargetsAsync(Tenant, site)).ConfigVersion, targets), markIndividual: true);
    }

    // open = the tenant is listed in Collector:AppTenantIds for every store of the rig; closed = no store gets the options.
    // The isolated database has no Cloro key: the proxy only marks catalog rows available so that a first run can start.
    private static Rig Build(string cs, Guid tenant, bool open = true)
    {
        var database = new MySqlDatabase(cs);
        var options = open ? AppSiteTargetsSqlTests.OpenFor(tenant) : null;
        var store = new ManagementStore(database, appChannel: options);
        var management = System.Reflection.DispatchProxy.Create<IManagementStore, AvailableCatalogProxy>();
        ((AvailableCatalogProxy)(object)management).Inner = store;
        var runs = new CloroVisibilityStore(database);
        var service = new PromptMonitoringService(database, management, new CollectionScheduleStore(database, appChannel: options), runs,
            appChannel: options);
        return new(cs, database, tenant, store, service, new CollectionScheduleStore(database, appChannel: options), runs);
    }

    private sealed record PlanRow(Guid Id, string Name, int Version, bool Enabled, string Cadence, string LocalTime, string TimeZone,
        string Snapshot, string? NextSlotAt, string UpdatedAt)
    {
        public CollectionScheduleSelection Selection => JsonSerializer.Deserialize<CollectionScheduleSelection>(Snapshot, Web)!;
    }

    private static async Task<Dictionary<string, PlanRow>> PlansAsync(string cs, Guid site)
        => (await RowsAsync(cs, """
                SELECT BIN_TO_UUID(id), name, version, enabled, cadence, TIME_FORMAT(local_time, '%H:%i'), time_zone,
                       CAST(selection_snapshot AS CHAR), DATE_FORMAT(next_slot_at, '%Y-%m-%d %H:%i:%s'), DATE_FORMAT(updated_at, '%Y-%m-%d %H:%i:%s.%f')
                FROM collection_schedules WHERE site_id = UUID_TO_BIN(@site);
                """, ("@site", site.ToString())))
            .ToDictionary(r => r[1]!, r => new PlanRow(Guid.Parse(r[0]!), r[1]!, int.Parse(r[2]!, CultureInfo.InvariantCulture), r[3] is "1" or "True",
                r[4]!, r[5]!, r[6]!, r[7]!, r[8], r[9]!));

    private static async Task<long> CountAsync(string cs, string sql, params (string Name, object? Value)[] args)
        => long.Parse((await RowsAsync(cs, sql, args))[0][0]!, CultureInfo.InvariantCulture);

    // Everything a collection would leave behind for the tenant: runs, checks, provider tasks, collector tasks,
    // credit reservations and ledger entries, and the balance.
    private static async Task<string> CollectionFootprintAsync(string cs, Guid tenant)
    {
        var arg = ("@tenant", (object?)tenant.ToString());
        return string.Join("|",
            "runs=" + await CountAsync(cs, "SELECT COUNT(*) FROM visibility_runs WHERE tenant_id = UUID_TO_BIN(@tenant);", arg),
            "checks=" + await CountAsync(cs, "SELECT COUNT(*) FROM visibility_checks WHERE tenant_id = UUID_TO_BIN(@tenant);", arg),
            "providerTasks=" + await CountAsync(cs, "SELECT COUNT(*) FROM provider_tasks t JOIN visibility_runs r ON r.id = t.run_id WHERE r.tenant_id = UUID_TO_BIN(@tenant);", arg),
            "collectorTasks=" + await CountAsync(cs, "SELECT COUNT(*) FROM collector_tasks WHERE tenant_id = UUID_TO_BIN(@tenant);", arg),
            "reservations=" + await CountAsync(cs, "SELECT COUNT(*) FROM monitoring_credit_reservations WHERE tenant_id = UUID_TO_BIN(@tenant);", arg),
            "ledger=" + await CountAsync(cs, "SELECT COUNT(*) FROM credit_ledger WHERE tenant_id = UUID_TO_BIN(@tenant);", arg),
            "balance=" + (await RowsAsync(cs, "SELECT available_credits FROM credit_accounts WHERE tenant_id = UUID_TO_BIN(@tenant);", arg))[0][0]);
    }

    private const string NoCollection = "runs=0|checks=0|providerTasks=0|collectorTasks=0|reservations=0|ledger=0|balance=1000.000";

    private static async Task<string[]> EnabledTargetsAsync(string cs, Guid site)
        => (await RowsAsync(cs, "SELECT CONCAT(provider, ':', engine) FROM site_targets WHERE site_id = UUID_TO_BIN(@site) AND enabled = TRUE ORDER BY 1;",
            ("@site", site.ToString()))).Select(r => r[0]!).ToArray();

    private static async Task<Guid[]> PromptIdsAsync(string cs, Guid site)
        => (await RowsAsync(cs, "SELECT BIN_TO_UUID(id) FROM prompts WHERE site_id = UUID_TO_BIN(@site) ORDER BY id;", ("@site", site.ToString())))
            .Select(r => Guid.Parse(r[0]!)).ToArray();

    // Seeds enabled 'all' prompts and returns their ids in Guid order (the order every selection uses).
    private static async Task<Guid[]> SeedPromptsAsync(string cs, Guid site, int count)
    {
        for (var i = 0; i < count; i++) await SeedPromptAsync(cs, site);
        return (await PromptIdsAsync(cs, site)).Order().ToArray();
    }

    private static Task ScopeAsync(string cs, Guid tenant, Guid site, Guid prompt, params string[] keys)
        => ExecAsync(cs, "UPDATE prompts SET target_scope = 'custom' WHERE id = UUID_TO_BIN(@prompt);"
            + string.Concat(keys.Select((_, i) => $"""
                 INSERT INTO prompt_target_scopes (tenant_id, site_id, prompt_id, target_key, created_at)
                 VALUES (UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), UUID_TO_BIN(@prompt), @key{i}, UTC_TIMESTAMP(6));
                """)),
            [("@tenant", tenant.ToString()), ("@site", site.ToString()), ("@prompt", prompt.ToString()),
                .. keys.Select((key, i) => ($"@key{i}", (object?)key))]);

    private static string Snapshot(IEnumerable<Guid> prompts, IEnumerable<CloroVisibilityTarget> targets,
        params CollectionPromptTargets[] promptTargets)
        => JsonSerializer.Serialize(new CollectionScheduleSelection(prompts.Order().ToArray(),
            targets.OrderBy(CollectionIdentity.TargetKey, StringComparer.Ordinal).ToArray(), 1, promptTargets.Length == 0 ? null : promptTargets), Web);

    // MySQL returns the stored JSON re-serialized (key order, spacing); compare as JSON values.
    private static void AssertSameJson(string expected, string actual)
        => Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(expected), JsonSerializer.Deserialize<JsonElement>(actual)),
            $"expected {expected}\nactual   {actual}");

    private static string NextAppSlot(string zone = PlanZone)
        => CollectionScheduleCalendar.NextAfter("daily", null, CollectionScheduleCalendar.ParseLocalTime(PromptMonitoringIdentity.AppLocalTime),
            CollectionScheduleCalendar.ParseTimeZone(zone), DateTimeOffset.UtcNow).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    // The single slot of the brand's plans: key | status | frozen selection.
    private static async Task<string?[]> InstantSlotsAsync(string cs, Guid site)
        => Assert.Single(await RowsAsync(cs, """
            SELECT s.local_slot_key, s.status, CAST(s.selection_snapshot AS CHAR) FROM schedule_slots s
            JOIN collection_schedules p ON p.id = s.schedule_id WHERE p.site_id = UUID_TO_BIN(@site);
            """, ("@site", site.ToString())));

    // A brand with only App targets: POST creates the App plan and its instant slot, and nothing else.
    [AppChannelMySqlFact]
    public Task AnAppOnlyBrandStartsWithTheAppPlanAloneWithoutBaselineMainPlanRunOrReservation()
        => AppSiteTargetsSqlTests.IsolatedAsync(async (cs, seedTenant) =>
    {
        var rig = Build(cs, await seedTenant());
        var site = await SeedBrandAsync(cs, rig.Tenant, "只有 App", follows: false, locale: "zh-CN", created: "2026-06-01");
        await rig.PutAsync(site, App("doubao"), App("kimi"));
        var prompts = await SeedPromptsAsync(cs, site, 2);

        // Reading never starts monitoring.
        var idle = await rig.Service.GetAsync(rig.Tenant, site);
        Assert.Equal((null, null, "not_started"), (idle.Schedule, idle.AppSchedule, idle.Billing!.Status));
        Assert.Empty(await PlansAsync(cs, site));

        var started = await rig.Service.EnsureAsync(rig.Tenant, site);
        Assert.Null(started.Schedule);
        Assert.Null(started.Run);
        Assert.Empty(started.PromptIds);
        // Billing stays the API round: nothing started, nothing estimated.
        Assert.Equal(("not_started", 0m, 0, 2), (started.Billing!.Status, started.Billing.EstimatedCredits, started.Billing.TargetCount, started.Billing.ActivePromptCount));
        var plan = Assert.Single(await PlansAsync(cs, site)).Value;
        Assert.Equal((AppName, 1, true, "daily", "09:11", PlanZone, NextAppSlot()),
            (plan.Name, plan.Version, plan.Enabled, plan.Cadence, plan.LocalTime, plan.TimeZone, plan.NextSlotAt));
        AssertSameJson(Snapshot(prompts, [AppTarget("doubao"), AppTarget("kimi")]), plan.Snapshot);
        var view = started.AppSchedule!;
        Assert.Equal((plan.Id, AppName, true, 1, "09:11", prompts.Length, 2),
            (view.Id, view.Name, view.Enabled, view.Version, view.LocalTime, view.Selection.PromptIds.Count, view.Selection.Targets.Count));
        // No baseline target, no run, no check, no provider or collector task, no reservation, no ledger entry.
        Assert.Equal(["app_collector:doubao", "app_collector:kimi"], await EnabledTargetsAsync(cs, site));
        Assert.Equal(2, await CountAsync(cs, "SELECT COUNT(*) FROM site_targets WHERE site_id = UUID_TO_BIN(@site);", ("@site", site.ToString())));
        // 11-27-app-first-cycle-now: starting inserts one pending instant slot (the current local minute, never 09:11) with the
        // plan snapshot; the Worker expands it, so starting itself still writes no run, check, task or reservation.
        var instantSlot = await InstantSlotsAsync(cs, site);
        Assert.Equal((AppCycleRules.IsInstantSlotKey(instantSlot[0]), "pending"), (true, instantSlot[1]));
        AssertSameJson(plan.Snapshot, instantSlot[2]);
        Assert.Equal(NoCollection, await CollectionFootprintAsync(cs, rig.Tenant));

        // Idempotent: POST and GET again leave the same row (id, version, snapshot, next slot, updated_at).
        Assert.Equal(plan.Id, (await rig.Service.EnsureAsync(rig.Tenant, site)).AppSchedule!.Id);
        var read = await rig.Service.GetAsync(rig.Tenant, site);
        Assert.Equal((null, plan.Id), (read.Schedule, read.AppSchedule!.Id));
        Assert.Equal(plan, Assert.Single(await PlansAsync(cs, site)).Value);

        // Instant collection (a new prompt, a model added to a prompt): skipped with the reason it had before the App plan
        // existed, and still no main plan, baseline, run or reservation.
        var created = await rig.Store.CreatePromptAsync(rig.Tenant, site, new PromptWrite("只在 App 采集的新提问：哪个记账工具好用？", "zh-CN"));
        var instant = Assert.IsType<InstantCollectionView>(await rig.Service.CollectNewPromptsAsync(rig.Tenant, site, [created.PromptId]));
        Assert.Equal((InstantCollectionStatus.Skipped, "MONITORING_TARGETS_REQUIRED", (Guid?)null, 0), (instant.Status, instant.ReasonCode, instant.RunId, instant.CheckCount));
        Assert.Null(await rig.Service.CollectAddedTargetsAsync(rig.Tenant, site, created with { TargetScope = "custom", TargetKeys = [AppKey("kimi")] },
            created with { TargetScope = "custom", TargetKeys = [AppKey("kimi"), AppKey("doubao")] }));
        var after = Assert.Single(await PlansAsync(cs, site)).Value;
        Assert.Equal((plan.Id, 2, true, plan.NextSlotAt), (after.Id, after.Version, after.Enabled, after.NextSlotAt));
        AssertSameJson(Snapshot([.. prompts, created.PromptId], [AppTarget("doubao"), AppTarget("kimi")]), after.Snapshot);
        Assert.Equal(["app_collector:doubao", "app_collector:kimi"], await EnabledTargetsAsync(cs, site));
        Assert.Equal(NoCollection, await CollectionFootprintAsync(cs, rig.Tenant));
        // The instant slot is still pending, so no second one was inserted.
        Assert.Equal(instantSlot, await InstantSlotsAsync(cs, site));

        // Both estimates accept the App-only configuration; every API figure is zero.
        var estimate = await rig.Service.EstimateTargetsAsync(rig.Tenant, site, new MonitoringTargetsEstimateRequest([App("doubao"), App("kimi")]));
        Assert.Empty(estimate.Blockers);
        // implement 8.1: the App cells are listed (with their channel) and the App part is broken out; the API part is empty.
        Assert.Equal(0L, estimate.NextSlotChecks);
        Assert.Equal([CollectionChannel.App, CollectionChannel.App], estimate.Cells.Select(c => c.Channel));
        Assert.Equal(new ApiChannelEstimate(0, 0m, 0m), estimate.Channels!.Api);
        Assert.Equal(estimate.Channels.App.WeeklyChecks, estimate.WeeklyChecks);
        var promptEstimate = await rig.Service.EstimateAsync(rig.Tenant, site, new PromptMonitoringEstimateRequest(1));
        Assert.Empty(promptEstimate.Blockers);
        Assert.Equal(0L, promptEstimate.TotalChecksPerRound);
        // A draft without any enabled cell is still refused, App rows or not.
        Assert.Contains("MONITORING_TARGETS_REQUIRED",
            (await rig.Service.EstimateTargetsAsync(rig.Tenant, site, new MonitoringTargetsEstimateRequest([App("doubao", enabled: false)]))).Blockers);
        Assert.Equal(after, Assert.Single(await PlansAsync(cs, site)).Value);
    });

    // Both channels: the main plan, the initial run and the night plan as before, plus the App plan without a run.
    [AppChannelMySqlFact]
    public Task ABrandWithBothChannelsGetsBothPlansAndOnlyTheApiInitialRun() => AppSiteTargetsSqlTests.IsolatedAsync(async (cs, seedTenant) =>
    {
        var rig = Build(cs, await seedTenant());
        var site = await SeedBrandAsync(cs, rig.Tenant, "两个渠道", follows: false, locale: "zh-CN", created: "2026-06-02");
        await rig.PutAsync(site, ChatGpt, GeminiTwice, App("doubao"), App("qwen"));
        var prompts = await SeedPromptsAsync(cs, site, 2);

        var started = await rig.Service.EnsureAsync(rig.Tenant, site);
        var plans = await PlansAsync(cs, site);
        Assert.Equal(new[] { AppName, NightName, MainName }.Order(StringComparer.Ordinal), plans.Keys.Order(StringComparer.Ordinal));
        var (main, night, app) = (plans[MainName], plans[NightName], plans[AppName]);
        Assert.Equal((1, true, "09:01", PlanZone), (main.Version, main.Enabled, main.LocalTime, main.TimeZone));
        AssertSameJson(Snapshot(prompts, [ChatGptTarget, GeminiTarget]), main.Snapshot);
        AssertSameJson(Snapshot(prompts, [GeminiTarget]), night.Snapshot);
        Assert.Equal((1, true, "09:11", PlanZone, NextAppSlot()), (app.Version, app.Enabled, app.LocalTime, app.TimeZone, app.NextSlotAt));
        AssertSameJson(Snapshot(prompts, [AppTarget("doubao"), AppTarget("qwen")]), app.Snapshot);
        Assert.DoesNotContain("app_collector", main.Snapshot + night.Snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("cloro", app.Snapshot, StringComparison.Ordinal);

        Assert.Equal((main.Id, app.Id), (started.Schedule!.Id, started.AppSchedule!.Id));
        Assert.Equal(prompts, started.PromptIds);
        // The initial run: 2 prompts × 2 API cells, charged; nothing for the App cells.
        Assert.Equal(4, started.Run!.TotalChecks);
        Assert.Equal("runs=1|checks=4|providerTasks=4|collectorTasks=0|reservations=1|ledger=1|balance=960.000", await CollectionFootprintAsync(cs, rig.Tenant));
        Assert.Equal(1, await CountAsync(cs, "SELECT COUNT(*) FROM visibility_runs WHERE site_id = UUID_TO_BIN(@site) AND requested_by = @by AND total_checks = 4;",
            ("@site", site.ToString()), ("@by", PromptMonitoringIdentity.InitialRequestedBy(site))));
        Assert.Equal(0, await CountAsync(cs, "SELECT COUNT(*) FROM visibility_checks WHERE tenant_id = UUID_TO_BIN(@tenant) AND (provider = 'app_collector' OR capture_mode = 'app');",
            ("@tenant", rig.Tenant.ToString())));
        Assert.Equal((4L, 40m, 2), (started.Billing!.CheckCount, started.Billing.EstimatedCredits, started.Billing.TargetCount));

        // POST and GET again: the same three rows, no second run.
        await rig.Service.EnsureAsync(rig.Tenant, site);
        await rig.Service.GetAsync(rig.Tenant, site);
        Assert.Equal(plans, await PlansAsync(cs, site));
        Assert.StartsWith("runs=1|checks=4|", await CollectionFootprintAsync(cs, rig.Tenant), StringComparison.Ordinal);
    });
}
