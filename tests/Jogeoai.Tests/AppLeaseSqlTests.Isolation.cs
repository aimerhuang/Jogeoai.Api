using System.Text.Json;
using System.Text.Json.Serialization;
using Jogeoai.Api.Controllers;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using static Jogeoai.Tests.AppCycleRunSqlTests;
using static Jogeoai.Tests.TeamMonitoringConfigSqlTests;

namespace Jogeoai.Tests;

/// <summary>与网页渠道的隔离（implement 5.6，design 6.3；总 PRD AC-C4、AC-C10）以及节点的停用（AC-C11d）。</summary>
public sealed partial class AppLeaseSqlTests
{
    private static readonly JsonSerializerOptions Api = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    // A web account never gets an App task and an App binding never gets a web task; each side's identifier is refused by
    // the other with an audit event; a lease of one channel never counts against the other channel's limits.
    [AppChannelMySqlFact]
    public Task WebAndAppLeasesNeverCrossAndTheirLimitsAreCountedSeparately() => PoolAsync(async (pool, seedTenant) =>
    {
        var rig = Build(pool.Cs, await seedTenant());
        var store = pool.Store(rig.Tenant);
        var (node, _) = await pool.NodeAsync("mac");
        var cycle = await CycleAsync(rig, "隔离", 3, "doubao");
        var binding = await pool.BindAsync(node, "AI_Apps_4KB", "doubao");
        var web = new DomesticCollectorStore(pool.Database);
        var webRun = (await web.CreateRunAsync(new CreateVisibilityRunCommand(rig.Tenant, cycle.Site, cycle.Prompts,
            [new CollectorPlatformSelection("doubao", "doubao.consumer_web")], $"web-isolation-{Guid.NewGuid():N}"))).RunId;
        // A web account and its platform gate, inserted directly: registering through the store writes a health event, and
        // that table rejects deletes. The web platform allows one lease per Beijing day.
        var profile = Guid.NewGuid();
        await pool.ExecAsync("""
            INSERT INTO collector_session_profiles (id, tenant_id, platform_code, region, label, status, hourly_quota, daily_quota, min_interval_seconds, created_at, updated_at)
            VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), 'doubao', 'cn', 'web-a', 'active', 100, 100, 0, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
            INSERT INTO collector_platform_dispatch_states (tenant_id, region, platform_code, min_interval_seconds, daily_cap, created_at, updated_at)
            VALUES (UUID_TO_BIN(@tenant), 'cn', 'doubao', 0, 1, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
            """, ("@id", profile.ToString()), ("@tenant", rig.Tenant.ToString()));
        CollectorLeaseCommand Web(Guid sessionProfile) => new(rig.Tenant, "web-node", CollectorRegion.Cn, ["doubao"], 1, 60, sessionProfile);
        Task<AppLeaseResult> AppAsync() => store.LeaseAsync(new AppLeaseCommand(node, binding, 60));
        Task<string> AppGateAsync() => pool.RowAsync("SELECT last_dispatched_at FROM collector_app_platform_states WHERE platform_code = 'doubao';");
        Task<string> WebGateAsync() => pool.RowAsync("""
            SELECT COALESCE(last_dispatched_at, '-') FROM collector_platform_dispatch_states WHERE tenant_id = UUID_TO_BIN(@tenant) AND platform_code = 'doubao';
            """, ("@tenant", rig.Tenant.ToString()));

        // The App binding leases an App task; the web gate is not touched.
        var app = (await AppAsync()).Task!;
        Assert.Equal((cycle.Run, "consumer_app", "doubao.consumer_app"), (app.RunId, app.Channel, app.PlatformEntryCode));
        Assert.Equal("-", await WebGateAsync());
        var appGate = await AppGateAsync();

        // 5.6: that App lease is a 'leased' attempt of the same tenant, region and platform today. It does not use up the web
        // platform's daily cap of one: the web account still gets its lease — a web task — and only then is the cap reached.
        var first = Assert.Single(await web.LeaseAsync(Web(profile)));
        Assert.Equal((webRun, "consumer_web", "doubao.consumer_web", (Guid?)profile), (first.RunId, first.Channel, first.PlatformEntryCode, first.SessionProfileId));
        Assert.Empty(await web.LeaseAsync(Web(profile)));
        // Without the cap the web account drains the web tasks and then gets nothing, although two App tasks are waiting.
        await pool.ExecAsync("UPDATE collector_platform_dispatch_states SET daily_cap = 100 WHERE tenant_id = UUID_TO_BIN(@tenant);", ("@tenant", rig.Tenant.ToString()));
        for (var i = 0; i < 2; i++) Assert.Equal(webRun, Assert.Single(await web.LeaseAsync(Web(profile))).RunId);
        Assert.Empty(await web.LeaseAsync(Web(profile)));
        Assert.Equal("2|0", await pool.RowAsync("""
            SELECT COALESCE(SUM(status = 'pending'), 0), COALESCE(SUM(lease_session_profile_id IS NOT NULL), 0) FROM collector_tasks WHERE run_id = UUID_TO_BIN(@run);
            """, ("@run", cycle.Run.ToString())));
        // The three web leases did not move the App platform gate.
        Assert.Equal(appGate, await AppGateAsync());

        // device-v1 ignores the legacy App daily cap; the separate web cap above still applied.
        await pool.ExecAsync("UPDATE collector_app_platform_states SET daily_cap = 2 WHERE platform_code = 'doubao';");
        await pool.FinishAsync(app.Id);
        var secondApp = (await AppAsync()).Task!;
        Assert.Equal((cycle.Run, "consumer_app"), (secondApp.RunId, secondApp.Channel));
        await pool.FinishAsync(secondApp.Id);
        // The App binding drains the App tasks and then gets nothing, although the web tasks' leases are there to expire.
        await pool.ExecAsync("UPDATE collector_tasks SET lease_until = UTC_TIMESTAMP(6) - INTERVAL 1 SECOND WHERE run_id = UUID_TO_BIN(@run);", ("@run", webRun.ToString()));
        var thirdApp = (await AppAsync()).Task!;
        Assert.Equal((cycle.Run, "consumer_app"), (thirdApp.RunId, thirdApp.Channel));
        await pool.FinishAsync(thirdApp.Id);
        AssertEmpty(await AppAsync(), AppLeaseEmptyReason.NoTask, 60, 60);
        Assert.Equal("3|0", await pool.RowAsync("""
            SELECT COUNT(*), COALESCE(SUM(lease_app_binding_id IS NOT NULL), 0) FROM collector_tasks WHERE run_id = UUID_TO_BIN(@run);
            """, ("@run", webRun.ToString())));

        // A web account's id used as bindingId: refused, with an audit event that says what it was.
        Assert.Equal(("APP_BINDING_FORBIDDEN", 403), Code(await Assert.ThrowsAsync<CollectorStoreException>(()
            => store.LeaseAsync(new AppLeaseCommand(node, profile, 60)))));
        Assert.Equal("web_session_profile", (await pool.LastEventAsync("binding_forbidden", profile)).GetProperty("reason").GetString());
        // An App binding's id used as sessionProfileId: the same 403 as any unknown account, plus an audit event.
        Assert.Equal(("SESSION_PROFILE_FORBIDDEN", 403), Code(await Assert.ThrowsAsync<CollectorStoreException>(() => web.LeaseAsync(Web(binding)))));
        var crossed = await pool.LastEventAsync("binding_forbidden", binding);
        Assert.Equal(("app_binding_as_web_session_profile", "internal/collector/leases", "web-node"), (crossed.GetProperty("reason").GetString(),
            crossed.GetProperty("route").GetString(), crossed.GetProperty("webNodeId").GetString()));
        Assert.Equal("web-collector:web-node|∅|doubao", await pool.RowAsync(
            "SELECT actor, node_id, platform_code FROM collector_app_events WHERE event_kind = 'binding_forbidden' AND binding_id = UUID_TO_BIN(@id);",
            ("@id", binding.ToString())));
        // An id that is neither is refused without an App event.
        var events = await pool.CountAsync("SELECT COUNT(*) FROM collector_app_events;");
        Assert.Equal(("SESSION_PROFILE_FORBIDDEN", 403), Code(await Assert.ThrowsAsync<CollectorStoreException>(() => web.LeaseAsync(Web(Guid.NewGuid())))));
        Assert.Equal(events, await pool.CountAsync("SELECT COUNT(*) FROM collector_app_events;"));

        // An App node cannot renew or fail a web task, even with the web lease's owner and version.
        var webTask = Guid.Parse((await RowsAsync(pool.Cs, "SELECT BIN_TO_UUID(id) FROM collector_tasks WHERE run_id = UUID_TO_BIN(@run) LIMIT 1;",
            ("@run", webRun.ToString())))[0][0]!);
        var before = await TaskAsync(pool.Cs, webTask);
        await pool.ExecAsync("UPDATE collector_tasks SET lease_until = UTC_TIMESTAMP(6) + INTERVAL 1 HOUR WHERE id = UUID_TO_BIN(@id);", ("@id", webTask.ToString()));
        Assert.Equal(("STALE_LEASE", 409), Code(await Assert.ThrowsAsync<CollectorStoreException>(()
            => store.HeartbeatAsync(new AppHeartbeatCommand("web-node", webTask, 1, 60)))));
        Assert.Equal(("APP_BINDING_FORBIDDEN", 403), Code(await Assert.ThrowsAsync<CollectorStoreException>(() => store.ReportFailureAsync(
            new AppFailureCommand(node, binding, AppFailureCode.AppCrashed, CollectorPhase.PostSend, new CollectorReportedTask(webTask, 1), "unknown")))));
        Assert.Equal(before, await TaskAsync(pool.Cs, webTask));
        Assert.Equal("active|-|0|0", await pool.BindingAsync(binding));
        // No web health event was written for the tenant (that table cannot be cleaned up).
        Assert.Equal(0, await pool.CountAsync("SELECT COUNT(*) FROM collector_session_health_events WHERE tenant_id = UUID_TO_BIN(@tenant);", ("@tenant", rig.Tenant.ToString())));
    });

    // Through the controller with real tokens: a disabled node is refused on every route while another node keeps leasing;
    // a token only speaks for its own node and its own bindings; the web collector's shared token is not a node token.
    [AppChannelMySqlFact]
    public Task DisablingANodeStopsItsRequestsWhileAnotherNodeKeepsLeasing() => PoolAsync(async (pool, seedTenant) =>
    {
        var rig = Build(pool.Cs, await seedTenant());
        var store = pool.Store(rig.Tenant);
        var (nodeA, tokenA) = await pool.NodeAsync("mac");
        var (nodeB, tokenB) = await pool.NodeAsync("mac");
        await CycleAsync(rig, "节点", 2, "doubao", "kimi");
        var (a, b) = (await pool.BindAsync(nodeA, "AI_Apps_4KB", "doubao"), await pool.BindAsync(nodeB, "AVD_B", "kimi"));
        AppCollectorController With(string token)
        {
            var context = new DefaultHttpContext();
            context.Request.Headers.Authorization = "Bearer " + token;
            return new AppCollectorController(store) { ControllerContext = new() { HttpContext = context } };
        }
        static (int Status, string? Code) Outcome(IActionResult? result)
        {
            var response = Assert.IsAssignableFrom<ObjectResult>(result);
            return (response.StatusCode ?? 200, response.StatusCode is null or 200 ? null
                : JsonSerializer.SerializeToElement(response.Value).GetProperty("error").GetProperty("code").GetString());
        }
        static AppLeaseRequest Online(string node, Guid binding, int seconds = 60) => new(node, binding.ToString(), seconds, "device-v1", true);

        // Node A leases through the HTTP action; the body is the contract's lease response.
        var leased = JsonSerializer.SerializeToNode(Assert.IsType<OkObjectResult>((await With(tokenA).Lease(Online(nodeA, a, 120), default)).Result).Value, Api)!;
        var taskId = (string)leased["task"]!["id"]!;
        Assert.Equal(("doubao", "consumer_app", a.ToString(), 1), ((string)leased["task"]!["platformCode"]!, (string)leased["task"]!["channel"]!,
            (string)leased["task"]!["bindingId"]!, (int)leased["task"]!["leaseVersion"]!));
        Assert.EndsWith("Z", (string)leased["task"]!["leaseExpiresAt"]!, StringComparison.Ordinal);
        Assert.EndsWith("Z", (string)leased["task"]!["deadlineAt"]!, StringComparison.Ordinal);

        await store.SetNodeStatusAsync(nodeA, AppNodeStatus.Disabled, null);
        // Every route refuses the disabled node; its lease is not released (it expires on its own).
        Assert.Equal((403, "APP_NODE_DISABLED"), Outcome((await With(tokenA).Lease(Online(nodeA, a), default)).Result));
        Assert.Equal((403, "APP_NODE_DISABLED"), Outcome((await With(tokenA).Heartbeat(new AppHeartbeatRequest(nodeA, taskId, 1), default)).Result));
        Assert.Equal((403, "APP_NODE_DISABLED"), Outcome((await With(tokenA).ReportBindings(new AppBindingReportRequest(nodeA, "rev", []), default)).Result));
        Assert.Equal((403, "APP_NODE_DISABLED"), Outcome((await With(tokenA).Failure(new AppFailureRequest(nodeA, a.ToString(), "app_crashed", "probe"), default)).Result));
        Assert.Equal($"leased|1|{nodeA}|1|0|-|-", await TaskAsync(pool.Cs, Guid.Parse(taskId)));

        // The other node is not affected: it reports and leases.
        var report = JsonSerializer.SerializeToNode(Assert.IsType<OkObjectResult>((await With(tokenB).ReportBindings(new AppBindingReportRequest(nodeB, "rev-9",
            [new AppBindingReport("AVD_B", "kimi", await pool.RowAsync("SELECT account_label FROM collector_app_bindings WHERE id = UUID_TO_BIN(@id);", ("@id", b.ToString())),
                "pkg.kimi", "1.0", "1.0", "15", "ok")]), default)).Result).Value, Api)!;
        Assert.Equal((b.ToString(), "active", "device-v1"), ((string)report["bindings"]![0]!["bindingId"]!, (string)report["bindings"]![0]!["status"]!,
            (string)report["dispatchProtocol"]!));
        Assert.Empty(report["rejected"]!.AsArray());
        var other = JsonSerializer.SerializeToNode(Assert.IsType<OkObjectResult>((await With(tokenB).Lease(Online(nodeB, b), default)).Result).Value, Api)!;
        Assert.Equal("kimi", (string)other["task"]!["platformCode"]!);

        // A token speaks for its own node only, and a node for its own bindings only.
        Assert.Equal((403, "APP_NODE_MISMATCH"), Outcome((await With(tokenB).Lease(Online(nodeA, a), default)).Result));
        Assert.Equal((403, "APP_BINDING_FORBIDDEN"), Outcome((await With(tokenB).Lease(Online(nodeB, a), default)).Result));
        Assert.Equal("foreign_node", (await pool.LastEventAsync("binding_forbidden", a)).GetProperty("reason").GetString());
        // The web collector's shared token, an unknown token and a rotated-away token are not node tokens.
        foreach (var token in new[] { "web-collector-shared-token", Guid.NewGuid().ToString("N") })
            Assert.Equal((401, "UNAUTHORIZED"), Outcome((await With(token).Lease(Online(nodeB, b), default)).Result));
        var rotated = await store.RotateNodeTokenAsync(nodeB, null);
        Assert.Equal((401, "UNAUTHORIZED"), Outcome((await With(tokenB).Lease(Online(nodeB, b), default)).Result));
        Assert.Equal((200, null), Outcome((await With(rotated.Token).Lease(Online(nodeB, b), default)).Result));

        // Enabled again, node A carries on with the lease it still holds.
        await store.SetNodeStatusAsync(nodeA, AppNodeStatus.Active, null);
        var heartbeat = JsonSerializer.SerializeToNode(Assert.IsType<OkObjectResult>((await With(tokenA).Heartbeat(new AppHeartbeatRequest(nodeA, taskId, 1, 90), default)).Result).Value, Api)!;
        Assert.EndsWith("Z", (string)heartbeat["leaseExpiresAt"]!, StringComparison.Ordinal);
        Assert.Equal((409, "STALE_LEASE"), Outcome((await With(tokenA).Heartbeat(new AppHeartbeatRequest(nodeA, taskId, 2, 90), default)).Result));
    });
}
