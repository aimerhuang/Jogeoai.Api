using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using static Jogeoai.Tests.AppCycleRunSqlTests;
using static Jogeoai.Tests.TeamMonitoringConfigSqlTests;

namespace Jogeoai.Tests;

/// <summary>
/// 隔离库（已执行 001–049、库名含 _test）上的预估 App 部分与容量读取（implement 8.1，design 6.5）：
/// 没有 App 单元格的品牌与团队预估与改动前逐字节相同（开放与未开放的团队一样）；有 App 单元格时 API 部分的数字不变，
/// App 部分按通过检查的提问、全部开放团队的需求、绑定与实测产能计算；未开放的团队不计入需求，也不能预估 App 单元格。
/// 未设置 JOGEOAI_APP_CHANNEL_TEST_CONNECTION 时跳过，绝不使用业务库 jogeoai。用例只用自己新建的租户、节点和绑定，结束时全部删除。
/// </summary>
[Collection(AppChannelSqlCollection.Name)]
public sealed class AppEstimateSqlTests
{
    private const string Zone = "Asia/Shanghai";
    private static readonly JsonSerializerOptions Api = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly SiteTargetWrite ChatGpt = new("cloro", "chatgpt", "scraped", "US", "en-US");
    private static readonly SiteTargetWrite DeepSeek = new("deepseek_api", "deepseek", "api", "CN", "zh-CN");
    private static readonly string ChatGptKey = CollectionIdentity.TargetKey(new CloroVisibilityTarget("cloro", "chatgpt", "en-US", "US"));
    private static readonly string DeepSeekKey = CollectionIdentity.TargetKey(new CloroVisibilityTarget("deepseek_api", "deepseek", "zh-CN", "CN"));

    private static SiteTargetWrite App(string engine) => new("app_collector", engine, "app", "CN", "zh-CN", true, null, MonitoringFrequency.Weekly);

    private static PromptMonitoringService Service(MySqlDatabase database, IManagementStore management, params Guid[] openTenants)
        => new(database, management, new CollectionScheduleStore(database), new CloroVisibilityStore(database),
            appChannel: AppSiteTargetsSqlTests.OpenFor(openTenants));

    private static string Json(object value, params string[] remove)
    {
        var node = JsonSerializer.SerializeToNode(value, Api)!.AsObject();
        foreach (var name in remove) node.Remove(name);
        return node.ToJsonString();
    }

    // The pre-App members of the target estimate and of its cells, in order (MonitoringTargetsEstimateTests pins the same 13).
    private static readonly string[] EstimateMembers = ["promptCount", "weeklyChecks", "weeklyCredits", "nextSlotChecks", "nextSlotCredits", "cells",
        "balance", "shortfall", "blockers", "unitPrice", "sampleCount", "nextSlotAt", "nextSlotKind"];
    private static readonly string[] CellMembers = ["targetKey", "engine", "answerMode", "frequency", "weeklyRuns", "promptCount"];
    private static readonly string[] TeamMembers = ["unitPrice", "balance", "weeklyChecks", "weeklyCredits", "nextSlotCredits", "shortfall", "brands"];
    private static readonly string[] TeamBrandMembers = ["siteId", "displayName", "promptCount", "weeklyChecks", "weeklyCredits", "nextSlotChecks",
        "nextSlotCredits", "blockers"];

    // 2 prompts × (ChatGPT + DeepSeek) daily: 28 a week; the next slot is a 09:01 main slot with every daily cell due (4 checks).
    private static string PinnedEstimate(decimal balance) => $$"""
        {"promptCount":2,"weeklyChecks":28,"weeklyCredits":280,"nextSlotChecks":4,"nextSlotCredits":40,"cells":[{"targetKey":"{{ChatGptKey}}","engine":"chatgpt","answerMode":"fast","frequency":"daily","weeklyRuns":7,"promptCount":2},{"targetKey":"{{DeepSeekKey}}","engine":"deepseek","answerMode":"fast","frequency":"daily","weeklyRuns":7,"promptCount":2}],"balance":{{balance.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"shortfall":0,"blockers":[],"unitPrice":10,"sampleCount":1,"nextSlotKind":"main"}
        """;

    // implement 8.1: without an App cell both estimates are exactly what they were before the App channel — in a tenant the App
    // channel is open for (whose brand even has an enabled App target stored) and in one it is not. No channels, no channel on
    // a cell, no extra member on a team row; the values are pinned.
    [AppChannelMySqlFact]
    public Task EstimatesWithoutAnAppCellAreByteIdenticalInOpenAndClosedTenants() => AppSiteTargetsSqlTests.IsolatedAsync(async (cs, seedTenant) =>
    {
        var database = new MySqlDatabase(cs);
        var brandJson = new List<string>();
        var teamJson = new List<string>();
        foreach (var open in new[] { true, false })
        {
            var tenant = await seedTenant();
            var store = open ? AppSiteTargetsSqlTests.OpenStore(database, tenant) : new ManagementStore(database);
            var service = Service(database, store, open ? new[] { tenant } : Array.Empty<Guid>());
            var brand = await SeedBrandAsync(cs, tenant, "只有 API 的草稿", follows: false, locale: "zh-CN", created: "2026-07-01");
            await SeedPromptAsync(cs, brand);
            await SeedPromptAsync(cs, brand);
            // The open tenant's brand has an App target stored and enabled; the draft below leaves it out (as scope=all would).
            if (open)
                await store.PutSiteTargetsAsync(tenant, brand, new SiteTargetsWrite((await store.GetSiteTargetsAsync(tenant, brand)).ConfigVersion,
                    [ChatGpt, App("doubao")]), markIndividual: true);
            var estimate = await service.EstimateTargetsAsync(tenant, brand, new MonitoringTargetsEstimateRequest([ChatGpt, DeepSeek]));
            Assert.Null(estimate.Channels);
            Assert.All(estimate.Cells, cell => Assert.Null(cell.Channel));
            var node = JsonSerializer.SerializeToNode(estimate, Api)!.AsObject();
            Assert.Equal(EstimateMembers, node.Select(member => member.Key));
            Assert.All(node["cells"]!.AsArray(), cell => Assert.Equal(CellMembers, cell!.AsObject().Select(member => member.Key)));
            brandJson.Add(Json(estimate, "nextSlotAt"));

            // Team: one following brand, an API-only default.
            var follower = await SeedBrandAsync(cs, tenant, "跟随", follows: true, locale: "zh-CN", created: "2026-07-02");
            await SeedPromptAsync(cs, follower);
            var team = await service.EstimateTeamMonitoringAsync(tenant, new TeamMonitoringEstimateRequest([new("cloro", "chatgpt"), new("deepseek_api", "deepseek")]));
            var teamNode = JsonSerializer.SerializeToNode(team, Api)!.AsObject();
            Assert.Equal(TeamMembers, teamNode.Select(member => member.Key));
            var row = Assert.Single(teamNode["brands"]!.AsArray())!.AsObject();
            Assert.Equal(TeamBrandMembers, row.Select(member => member.Key));
            Assert.Null(team.Brands[0].Channels);
            row.Remove("siteId");
            teamJson.Add(teamNode.ToJsonString());
        }
        Assert.Equal(brandJson[0], brandJson[1]);
        Assert.Equal(teamJson[0], teamJson[1]);
        Assert.Equal(PinnedEstimate(1000.000m).Trim(), brandJson[0]);
        Assert.Equal("""
            {"unitPrice":10,"balance":1000.000,"weeklyChecks":14,"weeklyCredits":140,"nextSlotCredits":20,"shortfall":0,"brands":[{"displayName":"\u8DDF\u968F","promptCount":1,"weeklyChecks":14,"weeklyCredits":140,"nextSlotChecks":2,"nextSlotCredits":20,"blockers":[]}]}
            """.Trim(), teamJson[0]);
    });

    // implement 8.1 / design 6.5: App cells next to API cells. The API part is unchanged; the App part counts the prompts that
    // pass the check (operator exclusion and rule), the demand of every open tenant (not of a closed one), the bindings and the
    // measured output; the top-level weekly figures are the sum.
    [AppChannelMySqlFact]
    public Task AppCellsAddTheAppPartAndTheApiPartIsUnchanged() => AppLeaseSqlTests.PoolAsync(async (pool, seedTenant) =>
    {
        var cs = pool.Cs;
        var database = pool.Database;
        var (a, b, c) = (await seedTenant(), await seedTenant(), await seedTenant());
        var store = AppSiteTargetsSqlTests.OpenStore(database, a, b, c);
        var service = Service(database, store, a, b);

        // Tenant b (open): a brand with a doubao App target, three prompts and a real cycle run (its tasks carry the attempts below).
        var rigB = Build(cs, b);
        var (_, otherRun, _) = await AppLeaseSqlTests.CycleAsync(rigB, "其他团队", 3, "doubao");
        // Tenant c is not open: its stored App target and prompts are not demand.
        var closed = await SeedBrandAsync(cs, c, "未开放团队", follows: false, locale: "zh-CN", created: "2026-07-03");
        await store.PutSiteTargetsAsync(c, closed, new SiteTargetsWrite((await store.GetSiteTargetsAsync(c, closed)).ConfigVersion, [App("doubao")]), markIndividual: true);
        for (var i = 0; i < 4; i++) await SeedPromptAsync(cs, closed, $"未开放团队的提问 {i}", 10 + i);

        // Tenant a: 'all' × 2, 'all' too long, 'all' excluded by an operator, one scoped to kimi only.
        var brand = await SeedBrandAsync(cs, a, "两个渠道", follows: false, locale: "zh-CN", created: "2026-07-04");
        await store.PutSiteTargetsAsync(a, brand, new SiteTargetsWrite((await store.GetSiteTargetsAsync(a, brand)).ConfigVersion,
            [ChatGpt, App("doubao"), App("kimi")]), markIndividual: true);
        await SeedPromptAsync(cs, brand, "哪个记账工具好用？", 1);
        await SeedPromptAsync(cs, brand, "预算管理应用怎么选？", 2);
        await SeedPromptAsync(cs, brand, new string('长', AppPromptRules.MaxLength + 1), 3);
        var excluded = await SeedPromptAsync(cs, brand, "被运营排除的提问", 4);
        var kimiOnly = await SeedPromptAsync(cs, brand, "只在 Kimi App 采集的提问", 5);
        await ScopeAsync(cs, a, brand, kimiOnly, CollectionIdentity.AppTargetKey("kimi"));
        await new AppCollectionStore(database).ExcludePromptAsync(a, brand, excluded, "测试排除", Guid.NewGuid());

        // One device exclusively on doubao (864 sends/day at 100 seconds), none on kimi.
        var (node, _) = await pool.NodeAsync("estimate");
        var binding = await pool.BindAsync(node, "AVD_ESTIMATE", "doubao");

        SiteTargetWrite[] apiDraft = [ChatGpt];
        SiteTargetWrite[] draft = [ChatGpt, App("doubao"), App("kimi")];
        var apiOnly = await service.EstimateTargetsAsync(a, brand, new MonitoringTargetsEstimateRequest(apiDraft));
        var estimate = await service.EstimateTargetsAsync(a, brand, new MonitoringTargetsEstimateRequest(draft));

        // API part: ChatGPT takes the four 'all' prompts (the kimi-only prompt has no API pair): 28 a week, and every API member
        // of the response is what the API-only draft gives.
        Assert.Null(apiOnly.Channels);
        Assert.Equal(new ApiChannelEstimate(1, 28m, 280m), estimate.Channels!.Api);
        Assert.Equal((apiOnly.WeeklyChecks, apiOnly.WeeklyCredits), (estimate.Channels.Api.WeeklyChecks, estimate.Channels.Api.WeeklyCredits));
        Assert.Equal(Json(apiOnly with { WeeklyChecks = 0, WeeklyCredits = 0, Cells = [] }, "nextSlotAt"),
            Json(estimate with { WeeklyChecks = 0, WeeklyCredits = 0, Cells = [], Channels = null }, "nextSlotAt"));
        Assert.Equal(apiOnly.Cells.Select(cell => cell with { Channel = CollectionChannel.Api }), estimate.Cells.Take(1));

        // Doubao demand: two prompts here and three from tenant b; capacity: one full device-week.
        // kimi: the two plain prompts and the kimi-only one (3), no binding, so none of them is expected to be collected.
        Assert.Equal([(CollectionIdentity.AppTargetKey("doubao"), 2), (CollectionIdentity.AppTargetKey("kimi"), 3)],
            estimate.Cells.Skip(1).Select(cell => (cell.TargetKey, cell.PromptCount)));
        var app = estimate.Channels.App;
        Assert.Equal([new AppPlatformEstimate("doubao", 6048, 5, 0), new AppPlatformEstimate("kimi", 0, 3, 3)], app.Platforms);
        Assert.Equal((2, 2m, 20m), (app.Targets, app.WeeklyChecks, app.WeeklyCredits));
        Assert.Equal([new AppReasonCount(AppPromptRules.ReasonExcludedByOperator, 1), new AppReasonCount(AppPromptRules.ReasonTooLong, 1)],
            app.ExcludedPrompts);
        Assert.Equal((30m, 300m), (estimate.WeeklyChecks, estimate.WeeklyCredits));
        // The targets were enabled today, so their first cycle is tomorrow at 09:11 (design 3.5).
        var today = ManagementStore.BrandLocalDate(Zone, DateTimeOffset.UtcNow);
        Assert.Equal(AppCycleCalendar.SlotUtc(today.AddDays(1), CollectionScheduleCalendar.ParseTimeZone(Zone)), app.FirstResultAt);

        // The capacity view (the 8B entry point) reads the same: stored demand of the open tenants, one binding.
        var capacities = await new AppCapacityQuery(database, AppSiteTargetsSqlTests.OpenFor(a, b)).GetPlatformCapacitiesAsync();
        Assert.Equal(AppChannelDefinition.Engines, capacities.Select(capacity => capacity.Platform));
        Assert.Equal(new AppPlatformCapacity("doubao", true, 1, 6048, null, 6048, 5, 0), capacities[0]);
        Assert.Equal(new AppPlatformCapacity("kimi", true, 0, 0, null, 0, 3, 3), capacities.Single(capacity => capacity.Platform == "kimi"));
        Assert.Equal((false, 0L), (capacities[^1].Available, capacities[^1].WeeklyCapacity));
        // With the list empty nobody's demand is counted.
        Assert.All(await new AppCapacityQuery(database).GetPlatformCapacitiesAsync(), capacity => Assert.Equal(0, capacity.WeeklyDemand));

        // Measured output (design 6.5): successful task durations — 'leased' to 'accepted' of the same task and lease version —
        // over the last 14 days. One device on doubao gives 604 800 s a week.
        var task = Guid.Parse((await RowsAsync(cs, "SELECT BIN_TO_UUID(id) FROM collector_tasks WHERE run_id = UUID_TO_BIN(@run) LIMIT 1;",
            ("@run", otherRun.ToString())))[0][0]!);
        var accepted = DateTime.UtcNow.AddDays(-1);
        async Task AttemptAsync(int version, string kind, DateTime at)
            => await pool.ExecAsync("""
                INSERT INTO collector_attempts (id, tenant_id, collector_task_id, lease_version, node_id, region, event_kind, app_binding_id, created_at)
                VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), UUID_TO_BIN(@task), @version, @node, 'cn', @kind, UUID_TO_BIN(@binding), @at);
                """, ("@id", Guid.NewGuid().ToString()), ("@tenant", b.ToString()), ("@task", task.ToString()), ("@version", version), ("@node", node),
                ("@kind", kind), ("@binding", binding.ToString()), ("@at", at));
        async Task PairAsync(int version, int seconds, DateTime acceptedAt)
        {
            await AttemptAsync(version, "leased", acceptedAt.AddSeconds(-seconds));
            await AttemptAsync(version, "accepted", acceptedAt);
        }
        AppPlatformCapacity Doubao(IReadOnlyList<AppPlatformCapacity> all) => all.Single(capacity => capacity.Platform == "doubao");
        var query = new AppCapacityQuery(database, AppSiteTargetsSqlTests.OpenFor(a, b));
        // 19 samples (10 × 3000 s, 9 × 3048 s): the median (3000 s) is reported but not used.
        for (var i = 0; i < 19; i++) await PairAsync(i + 1, i < 10 ? 3000 : 3048, accepted.AddMinutes(-i));
        Assert.Equal(new AppPlatformCapacity("doubao", true, 1, 6048, null, 6048, 5, 0, 3000d, 19), Doubao(await query.GetPlatformCapacitiesAsync()));
        // Not samples: an accepted row whose lease version has no leased row (the leased row is version 40, the accepted 41),
        // and a pair whose accepted row is older than the 14-day window.
        await AttemptAsync(40, "leased", accepted.AddHours(-2));
        await AttemptAsync(41, "accepted", accepted.AddHours(-1));
        await PairAsync(42, 10, DateTime.UtcNow.AddDays(-20));
        Assert.Equal(19, Doubao(await query.GetPlatformCapacitiesAsync()).TaskDurationSamples);
        // The 20th sample: median (3000 + 3048) / 2 = 3024 s; ⌊604 800 / 3024⌋ = 200 a week, below the rate limit, so the capacity.
        await PairAsync(20, 3048, accepted.AddMinutes(-30));
        var measured = Doubao(await query.GetPlatformCapacitiesAsync());
        Assert.Equal(new AppPlatformCapacity("doubao", true, 1, 6048, 200, 200, 5, 0, 3024d, 20), measured);
        Assert.Equal(new AppPlatformEstimate("doubao", 200, 5, 0),
            (await service.EstimateTargetsAsync(a, brand, new MonitoringTargetsEstimateRequest(draft))).Channels!.App.Platforms[0]);
        // The same device also bound to kimi: its week is split, doubao keeps 302 400 s → ⌊302 400 / 3024⌋ = 100.
        var kimiBinding = await pool.BindAsync(node, "AVD_ESTIMATE", "kimi");
        var split = await query.GetPlatformCapacitiesAsync();
        Assert.Equal((3024L, (long?)100L, 100L), (Doubao(split).RateLimitWeekly, Doubao(split).MeasuredWeekly, Doubao(split).WeeklyCapacity));
        // kimi has the other half of the device but no task durations yet: only its rate limit.
        Assert.Equal((3024L, (long?)null, 3024L, 0), (split.Single(c => c.Platform == "kimi").RateLimitWeekly, split.Single(c => c.Platform == "kimi").MeasuredWeekly,
            split.Single(c => c.Platform == "kimi").WeeklyCapacity, split.Single(c => c.Platform == "kimi").TaskDurationSamples));
        await pool.ExecAsync("UPDATE collector_app_bindings SET status = 'stopped', stop_reason = 'disabled_by_operator', stopped_at = UTC_TIMESTAMP(6) WHERE id = UUID_TO_BIN(@id);",
            ("@id", kimiBinding.ToString()));
        Assert.Equal(200, Doubao(await query.GetPlatformCapacitiesAsync()).MeasuredWeekly);
        // A disabled node counts neither for the rate limit nor for the device time.
        await pool.ExecAsync("UPDATE collector_app_nodes SET status = 'disabled' WHERE node_id = @node;", ("@node", node));
        var disabled = Doubao(await query.GetPlatformCapacitiesAsync());
        Assert.Equal((0, 0L, (long?)0L, 0L, 20), (disabled.ActiveBindings, disabled.RateLimitWeekly, disabled.MeasuredWeekly, disabled.WeeklyCapacity,
            disabled.TaskDurationSamples));
        await pool.ExecAsync("UPDATE collector_app_nodes SET status = 'active' WHERE node_id = @node;", ("@node", node));

        // Team estimate: a following brand of tenant a gets the App breakdown on its row; the totals include its App part.
        var follower = await SeedBrandAsync(cs, a, "跟随", follows: true, locale: "zh-CN", created: "2026-07-05");
        await SeedPromptAsync(cs, follower, "跟随品牌的提问", 6);
        var team = await service.EstimateTeamMonitoringAsync(a, new TeamMonitoringEstimateRequest(
            [new("cloro", "chatgpt"), new("app_collector", "doubao", null, MonitoringFrequency.Weekly)]));
        var row = Assert.Single(team.Brands);
        Assert.Equal(new ApiChannelEstimate(1, 7m, 70m), row.Channels!.Api);
        Assert.Equal((1, 1m), (row.Channels.App.Targets, row.Channels.App.WeeklyChecks));
        Assert.Equal((8m, 80m), (row.WeeklyChecks, row.WeeklyCredits));
        Assert.Equal((8m, 80m), (team.WeeklyChecks, team.WeeklyCredits));
        // The row's breakdown is the brand estimate's own on the materialized draft.
        var materialized = (await store.MaterializeTeamCellsForFollowersAsync(a,
            [new("cloro", "chatgpt"), new("app_collector", "doubao", null, MonitoringFrequency.Weekly)])).Single().Targets;
        Assert.Equal(Json(row.Channels), Json((await service.EstimateTargetsAsync(a, follower, new MonitoringTargetsEstimateRequest(materialized))).Channels!));

        // A tenant the App channel is not open for cannot estimate App cells (as before); its API-only estimate has no channels.
        var refused = await Assert.ThrowsAsync<CollectorStoreException>(() => service.EstimateTargetsAsync(c, closed, new MonitoringTargetsEstimateRequest([ChatGpt, App("doubao")])));
        Assert.Equal(("TARGET_NOT_ALLOWED", 400), (refused.Code, refused.StatusCode));
        Assert.Null((await service.EstimateTargetsAsync(c, closed, new MonitoringTargetsEstimateRequest([ChatGpt]))).Channels);

        // Read-only: nothing written by the estimates.
        Assert.Equal(0, await CountAsync(cs, "SELECT COUNT(*) FROM visibility_runs WHERE tenant_id = UUID_TO_BIN(@tenant);", ("@tenant", a.ToString())));
    });
}
