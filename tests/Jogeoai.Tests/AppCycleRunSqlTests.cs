using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using static Jogeoai.Tests.TeamMonitoringConfigSqlTests;

namespace Jogeoai.Tests;

/// <summary>
/// 隔离库（已执行 001–049、库名含 _test）上的 App 周期 Run（implement 4.3–4.7）：到期时隙展开成 Run、检测和 consumer_app 采集任务；
/// 幂等；提问范围与提问检查；平台不可用；截止后的未采集；与网页渠道的隔离；人工排除与 appEligibility；App Run 的读取。
/// 未设置 JOGEOAI_APP_CHANNEL_TEST_CONNECTION 时跳过，绝不使用业务库 jogeoai。每个用例只用自己新建的租户，结束时删除该租户的全部行，
/// 并核对整库每张表的行数与用例开始前相同。与迁移用例同一个集合：它们会执行 DDL，不能并行。
/// 时隙用 2000 年的日期：排在库里其他到期计划之前，而且截止时点（时隙 + 7 天）早已过去，截止处理可以直接验证。
/// </summary>
[Collection(AppChannelSqlCollection.Name)]
public sealed partial class AppCycleRunSqlTests
{
    private const string PlanZone = "Asia/Shanghai";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions Api = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly SiteTargetWrite ChatGpt = new("cloro", "chatgpt", "scraped", "US", "en-US");
    private static readonly string ChatGptKey = CollectionIdentity.TargetKey(new CloroVisibilityTarget("cloro", "chatgpt", "en-US", "US", "{}"));

    private static SiteTargetWrite App(string engine) => new("app_collector", engine, "app", "CN", "zh-CN", true, null, MonitoringFrequency.Weekly);

    private static string AppKey(string engine) => CollectionIdentity.AppTargetKey(engine);

    private static (string, int) Code(CollectorStoreException exception) => (exception.Code, exception.StatusCode);

    private static DateTimeOffset Utc(int day, int hour, int minute) => new(2000, 1, day, hour, minute, 0, TimeSpan.Zero);

    /// <summary>一个租户上的一组服务（AppLeaseSqlTests 也用它造周期 Run）；<paramref name="Open"/> 为 false 时所有 Store 都拿不到 Collector:AppTenantIds。</summary>
    internal sealed record Rig(string Cs, MySqlDatabase Database, Guid Tenant, bool Open, ManagementStore Store, PromptMonitoringService Service,
        CollectionScheduleStore Schedules, CloroVisibilityStore Runs, AppCollectionStore App)
    {
        public async Task<SiteTargetsView> PutAsync(Guid site, params SiteTargetWrite[] targets)
            => await Store.PutSiteTargetsAsync(Tenant, site,
                new SiteTargetsWrite((await Store.GetSiteTargetsAsync(Tenant, site)).ConfigVersion, targets), markIndividual: true);
    }

    // The isolated database has no Cloro key: the proxy only marks catalog rows available so that an API first run can start.
    internal static Rig Build(string cs, Guid tenant, bool open = true)
    {
        var database = new MySqlDatabase(cs);
        var options = open ? AppSiteTargetsSqlTests.OpenFor(tenant) : null;
        var store = new ManagementStore(database, appChannel: options);
        var management = System.Reflection.DispatchProxy.Create<IManagementStore, AvailableCatalogProxy>();
        ((AvailableCatalogProxy)(object)management).Inner = store;
        var runs = new CloroVisibilityStore(database);
        var service = new PromptMonitoringService(database, management, new CollectionScheduleStore(database, appChannel: options), runs,
            appChannel: options);
        return new(cs, database, tenant, open, store, service, new CollectionScheduleStore(database, appChannel: options), runs,
            new AppCollectionStore(database));
    }

    internal static async Task<long> CountAsync(string cs, string sql, params (string Name, object? Value)[] args)
        => long.Parse((await RowsAsync(cs, sql, args))[0][0]!, CultureInfo.InvariantCulture);

    internal static async Task<string> RowAsync(string cs, string sql, params (string Name, object? Value)[] args)
        => string.Join("|", Assert.Single(await RowsAsync(cs, sql, args)).Select(value => value ?? "∅"));

    // Everything a collection could leave behind for the tenant, including the per-check credit items.
    private static async Task<string> FootprintAsync(string cs, Guid tenant)
    {
        var arg = ("@tenant", (object?)tenant.ToString());
        return string.Join("|",
            "runs=" + await CountAsync(cs, "SELECT COUNT(*) FROM visibility_runs WHERE tenant_id = UUID_TO_BIN(@tenant);", arg),
            "checks=" + await CountAsync(cs, "SELECT COUNT(*) FROM visibility_checks WHERE tenant_id = UUID_TO_BIN(@tenant);", arg),
            "providerTasks=" + await CountAsync(cs, "SELECT COUNT(*) FROM provider_tasks t JOIN visibility_runs r ON r.id = t.run_id WHERE r.tenant_id = UUID_TO_BIN(@tenant);", arg),
            "collectorTasks=" + await CountAsync(cs, "SELECT COUNT(*) FROM collector_tasks WHERE tenant_id = UUID_TO_BIN(@tenant);", arg),
            "reservations=" + await CountAsync(cs, "SELECT COUNT(*) FROM monitoring_credit_reservations WHERE tenant_id = UUID_TO_BIN(@tenant);", arg),
            "items=" + await CountAsync(cs, "SELECT COUNT(*) FROM monitoring_credit_items WHERE tenant_id = UUID_TO_BIN(@tenant);", arg),
            "ledger=" + await CountAsync(cs, "SELECT COUNT(*) FROM credit_ledger WHERE tenant_id = UUID_TO_BIN(@tenant);", arg),
            "balance=" + (await RowsAsync(cs, "SELECT available_credits FROM credit_accounts WHERE tenant_id = UUID_TO_BIN(@tenant);", arg))[0][0]);
    }

    // Seeds one enabled 'all' prompt with the given text; the creation time is explicit so that the creation order is known.
    internal static async Task<Guid> SeedPromptAsync(string cs, Guid site, string content, int second)
    {
        var (prompt, version) = (Guid.NewGuid(), Guid.NewGuid());
        await ExecAsync(cs, """
            INSERT INTO prompts (id,site_id,content,locale,enabled,created_at,updated_at)
              VALUES (UUID_TO_BIN(@prompt),UUID_TO_BIN(@site),@content,'zh-CN',TRUE,@created,@created);
            INSERT INTO prompt_versions (id,site_id,prompt_id,version,content,locale,branded_status,branded_source,created_at)
              VALUES (UUID_TO_BIN(@version),UUID_TO_BIN(@site),UUID_TO_BIN(@prompt),1,@content,'zh-CN','unbranded','system',@created);
            UPDATE prompts SET current_version_id=UUID_TO_BIN(@version) WHERE id=UUID_TO_BIN(@prompt);
            """, ("@prompt", prompt.ToString()), ("@site", site.ToString()), ("@version", version.ToString()), ("@content", content),
            ("@created", $"2026-01-01 00:00:{second:00}"));
        return prompt;
    }

    internal static Task ScopeAsync(string cs, Guid tenant, Guid site, Guid prompt, params string[] keys)
        => ExecAsync(cs, "UPDATE prompts SET target_scope = 'custom' WHERE id = UUID_TO_BIN(@prompt);"
            + string.Concat(keys.Select((_, i) => $"""
                 INSERT INTO prompt_target_scopes (tenant_id, site_id, prompt_id, target_key, created_at)
                 VALUES (UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), UUID_TO_BIN(@prompt), @key{i}, UTC_TIMESTAMP(6));
                """)),
            [("@tenant", tenant.ToString()), ("@site", site.ToString()), ("@prompt", prompt.ToString()),
                .. keys.Select((key, i) => ($"@key{i}", (object?)key))]);

    // Every App target of the brand starts its weekly cycle on Monday 2000-01-03.
    private static Task AnchorAppTargetsAsync(string cs, Guid site)
        => ExecAsync(cs, "UPDATE site_targets SET frequency_anchor = '2000-01-03' WHERE site_id = UUID_TO_BIN(@site) AND provider = 'app_collector';",
            ("@site", site.ToString()));

    internal sealed record SlotRow(Guid Id, string Status, string? SkipCode, string? SkipMessage, Guid? RunId);

    private static async Task<SlotRow> SlotAsync(string cs, Guid schedule, string key)
    {
        var row = Assert.Single(await RowsAsync(cs, """
            SELECT BIN_TO_UUID(id), status, skip_code, skip_message, BIN_TO_UUID(run_id) FROM schedule_slots
            WHERE schedule_id = UUID_TO_BIN(@schedule) AND local_slot_key = @key;
            """, ("@schedule", schedule.ToString()), ("@key", key)));
        return new(Guid.Parse(row[0]!), row[1]!, row[2], row[3], row[4] is { } runId ? Guid.Parse(runId) : null);
    }

    /// <summary>把 App 计划的下一个时隙定在 2000 年 1 月 <paramref name="day"/> 日 09:11（Asia/Shanghai）并物化；返回仍待处理的时隙。</summary>
    internal static async Task<PendingCollectionSlot?> MaterializeAsync(Rig rig, Guid schedule, int day)
    {
        await ExecAsync(rig.Cs, "UPDATE collection_schedules SET next_slot_at = @at WHERE id = UUID_TO_BIN(@id);",
            ("@id", schedule.ToString()), ("@at", $"2000-01-{day:00} 01:11:00"));
        Assert.Equal(1, await rig.Schedules.MaterializeDueSlotsAsync(Utc(day, 1, 12), 1));
        return ((await rig.Schedules.GetPendingSlotsAsync(100)).Regular()).SingleOrDefault(slot => slot.ScheduleId == schedule);
    }

    /// <summary>物化并处理一个到期时隙；返回处理后的时隙行。</summary>
    internal static async Task<SlotRow> RunSlotAsync(Rig rig, Guid schedule, int day)
    {
        var pending = await MaterializeAsync(rig, schedule, day);
        Assert.NotNull(pending);
        var created = await rig.Schedules.ProcessSlotAsync(pending, rig.Runs);
        var slot = await SlotAsync(rig.Cs, schedule, $"200001{day:00}T0911");
        Assert.Equal(slot.RunId is not null, created);
        return slot;
    }

    private static Task ParkSlotsAsync(string cs, Guid site)
        => ExecAsync(cs, """
            UPDATE schedule_slots s JOIN collection_schedules p ON p.id = s.schedule_id SET s.status = 'skipped', s.skip_code = 'TEST_PARKED'
            WHERE p.site_id = UUID_TO_BIN(@site) AND s.status = 'pending';
            """, ("@site", site.ToString()));

    // prompt | engine | check status | error code | task status | fair_rank, ordered by engine then rank.
    private static async Task<string[]> PairsAsync(string cs, Guid run)
        => (await RowsAsync(cs, """
                SELECT CONCAT_WS('|', BIN_TO_UUID(c.prompt_id), c.engine, c.status, COALESCE(c.error_code, '-'), t.status, t.fair_rank)
                FROM visibility_checks c JOIN collector_tasks t ON t.check_id = c.id
                WHERE c.run_id = UUID_TO_BIN(@run) ORDER BY c.engine, t.fair_rank, c.id;
                """, ("@run", run.ToString()))).Select(row => row[0]!).ToArray();

    private static string Pair(Guid prompt, string engine, int rank, string status = "pending", string error = "-", string task = "pending")
        => $"{prompt}|{engine}|{status}|{error}|{task}|{rank}";

    internal static Task<string> RunAsync(string cs, Guid run)
        => RowAsync(cs, """
            SELECT status, total_checks, succeeded_checks, failed_checks, started_at IS NOT NULL, completed_at IS NOT NULL
            FROM visibility_runs WHERE id = UUID_TO_BIN(@run);
            """, ("@run", run.ToString()));

    private static async Task<JsonElement> RunAuditAsync(string cs, Guid run)
        => JsonSerializer.Deserialize<JsonElement>((await RowsAsync(cs,
            "SELECT CAST(selection_snapshot AS CHAR) FROM visibility_runs WHERE id = UUID_TO_BIN(@run);", ("@run", run.ToString())))[0][0]!)
            .GetProperty("app");

    // A brand with the given App targets and no API target (no prompt yet).
    internal static async Task<Guid> SeedAppBrandAsync(Rig rig, string name, params string[] engines)
    {
        var site = await SeedBrandAsync(rig.Cs, rig.Tenant, name, follows: false, locale: "zh-CN", created: "2026-06-20");
        await rig.PutAsync(site, engines.Select(App).ToArray());
        return site;
    }

    // Starts monitoring and makes every App target of the brand due on Monday 2000-01-03; returns the App plan.
    internal static async Task<Guid> StartAsync(Rig rig, Guid site)
    {
        var plan = (await rig.Service.EnsureAsync(rig.Tenant, site)).AppSchedule!.Id;
        await AnchorAppTargetsAsync(rig.Cs, site);
        return plan;
    }
}
