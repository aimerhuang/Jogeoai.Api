using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Microsoft.Extensions.Options;
using MySqlConnector;
using static Jogeoai.Tests.AppCycleRunSqlTests;

namespace Jogeoai.Tests;

/// <summary>
/// 绑定审批、每节点设备上限、未批准绑定的隔离与心跳总时长上限（Trellis 11-27-sec-app-node-binding-approval，迁移 052，RF1–RF4）。
/// 这里的用例专门针对“批准之前”的状态，所以用 <c>Pool.BindAsync(approve: false)</c> 和 <c>Pool.NodeAsync(defaultDeviceCap: true)</c>，
/// 其余用例由这两个辅助方法默认批准绑定、放开设备上限。
/// </summary>
public sealed partial class AppLeaseSqlTests
{
    private static Task<string> ApprovedAtAsync(Pool pool, Guid binding)
        => pool.RowAsync("SELECT DATE_FORMAT(approved_at, '%Y-%m-%d %H:%i:%s.%f') FROM collector_app_bindings WHERE id = UUID_TO_BIN(@id);",
            ("@id", binding.ToString()));

    // RF1. A reported binding is stored stopped / disabled_by_operator and unapproved; nothing the node reports changes that;
    // it cannot lease; the operator's resume approves it, once; a later operator stop and resume keeps the approval time.
    [AppChannelMySqlFact]
    public Task ANewBindingWaitsForApprovalAndTheOperatorsResumeApprovesItOnce() => PoolAsync(async (pool, seedTenant) =>
    {
        var rig = Build(pool.Cs, await seedTenant());
        var store = pool.Store(rig.Tenant);
        var (node, _) = await pool.NodeAsync("mac");
        var cycle = await CycleAsync(rig, "待批准", 1, "doubao");
        var waiting = await TaskOfAsync(pool.Cs, cycle.Run, cycle.Prompts[0], "doubao");
        var label = $"doubao-{Guid.NewGuid():N}";
        var binding = await pool.BindAsync(node, "AVD_A", "doubao", label, approve: false);
        async Task<AppBindingState> HealthAsync(string health)
            => Assert.Single((await store.ReportBindingsAsync(new AppBindingReportCommand(node, "rev", [Report("AVD_A", "doubao", label, health)]))).Bindings);

        Assert.Equal("stopped|disabled_by_operator|0", await pool.ApprovalAsync(binding));
        Assert.Equal("stopped|disabled_by_operator|1|0", await pool.BindingAsync(binding));
        Assert.Equal(["binding_registered"], await pool.EventsAsync(binding));
        var registered = await pool.LastEventAsync("binding_registered", binding);
        Assert.Equal((true, "disabled_by_operator"), (registered.GetProperty("approvalPending").GetBoolean(), registered.GetProperty("stopReason").GetString()));
        var view = await store.GetBindingAsync(binding);
        Assert.Equal((true, (DateTimeOffset?)null, AppBindingStatus.Stopped, (AppBindingStopReason?)AppBindingStopReason.DisabledByOperator),
            (view.ApprovalPending, view.ApprovedAt, view.Status, view.StopReason));

        // Whatever the node reports afterwards, the binding stays as it is, and no event is written.
        foreach (var health in new[] { "ok", "login_required", "device_offline", "challenge_required", "contract_changed", "ok" })
        {
            var state = await HealthAsync(health);
            Assert.Equal((binding, AppBindingStatus.Stopped, (AppBindingStopReason?)AppBindingStopReason.DisabledByOperator), (state.BindingId, state.Status, state.StopReason));
            Assert.Equal("stopped|disabled_by_operator|0", await pool.ApprovalAsync(binding));
        }
        Assert.Equal(["binding_registered"], await pool.EventsAsync(binding));

        // It cannot lease. No other binding of the platform is usable, and still the waiting task is not marked
        // "binding stopped": a binding nobody approved does not write the reason a task was not collected.
        AssertEmpty(await store.LeaseAsync(new AppLeaseCommand(node, binding, 120)), AppLeaseEmptyReason.BindingStopped, 600, 600);
        Assert.Equal("pending|0|-|0|0|-|-", await TaskAsync(pool.Cs, waiting));

        // The operator's resume is the approval.
        Assert.True(await store.ResumeBindingAsync(binding, Guid.NewGuid()));
        Assert.Equal("active|-|1", await pool.ApprovalAsync(binding));
        var approvedAt = await ApprovedAtAsync(pool, binding);
        Assert.NotEqual("∅", approvedAt);
        Assert.True((await pool.LastEventAsync("binding_resumed", binding)).GetProperty("approved").GetBoolean());
        view = await store.GetBindingAsync(binding);
        Assert.Equal((false, AppBindingStatus.Active), (view.ApprovalPending, view.Status));
        Assert.InRange(view.ApprovedAt!.Value, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.False(await store.ResumeBindingAsync(binding, null));
        var leased = (await store.LeaseAsync(new AppLeaseCommand(node, binding, 120))).Task!;
        Assert.Equal((waiting, binding), (leased.Id, leased.BindingId));
        await pool.FinishAsync(leased.Id);

        // Approved once: an operator stop is an operator stop (not "pending"), and resuming it keeps the approval time.
        Assert.True(await store.DisableBindingAsync(binding, null));
        Assert.Equal("stopped|disabled_by_operator|1", await pool.ApprovalAsync(binding));
        Assert.False((await store.GetBindingAsync(binding)).ApprovalPending);
        Assert.Equal(AppBindingStopReason.DisabledByOperator, (await HealthAsync("ok")).StopReason);
        Assert.True(await store.ResumeBindingAsync(binding, null));
        Assert.Equal("active|-|1", await pool.ApprovalAsync(binding));
        Assert.Equal(approvedAt, await ApprovedAtAsync(pool, binding));
        Assert.False((await pool.LastEventAsync("binding_resumed", binding)).GetProperty("approved").GetBoolean());
        // And its own health reports work as they always did.
        Assert.Equal(AppBindingStopReason.LoginRequired, (await HealthAsync("login_required")).StopReason);
        Assert.True(await store.ResumeBindingAsync(binding, null));
        Assert.Equal(approvedAt, await ApprovedAtAsync(pool, binding));
        Assert.Equal(["binding_registered", "binding_resumed", "binding_stopped", "binding_resumed", "binding_stopped", "binding_resumed"],
            await pool.EventsAsync(binding));
    });

    // RF2. A node registers at most max_devices distinct devices (4 by default). An over-cap binding comes back in rejected[]
    // with device_limit_exceeded and leaves no row and no event; another platform on a registered device is not a new device;
    // the operator raises the cap, never below what is registered.
    [AppChannelMySqlFact]
    public Task TheFifthDeviceIsRejectedUntilTheOperatorRaisesTheNodesCap() => PoolAsync(async (pool, _) =>
    {
        var store = pool.Store();
        var (node, _) = await pool.NodeAsync("mac", defaultDeviceCap: true);
        var (other, _) = await pool.NodeAsync("mac", defaultDeviceCap: true);
        var suffix = Guid.NewGuid().ToString("N");
        AppBindingReport On(string device, string platform) => Report(device, platform, $"{platform}-{device}-{suffix}");
        Task<AppBindingReportResult> ReportAsync(string onNode, params AppBindingReport[] bindings)
            => store.ReportBindingsAsync(new AppBindingReportCommand(onNode, "rev", bindings));
        Task<string> RegisteredAsync(string onNode) => pool.RowAsync(
            "SELECT COUNT(*), COUNT(DISTINCT device_id) FROM collector_app_bindings WHERE node_id = @node;", ("@node", onNode));
        Task<long> NodeEventsAsync(string kind) => pool.CountAsync(
            "SELECT COUNT(*) FROM collector_app_events WHERE node_id = @node AND event_kind = @kind;", ("@node", node), ("@kind", kind));

        Assert.Equal("4", await pool.RowAsync("SELECT max_devices FROM collector_app_nodes WHERE node_id = @node;", ("@node", node)));
        Assert.Equal(AppPoolRules.DefaultMaxDevicesPerNode, (await store.GetNodeAsync(node)).MaxDevices);

        // One report with five devices: four are registered (waiting for approval), the fifth is rejected.
        var first = await ReportAsync(node, On("D1", "doubao"), On("D2", "doubao"), On("D3", "doubao"), On("D4", "doubao"), On("D5", "doubao"));
        Assert.Equal(["D1", "D2", "D3", "D4"], first.Bindings.Select(binding => binding.DeviceId));
        var rejected = Assert.Single(first.Rejected);
        Assert.Equal(("D5", "doubao", "device_limit_exceeded"), (rejected.DeviceId, rejected.PlatformCode, rejected.Code));
        Assert.Equal("4|4", await RegisteredAsync(node));
        Assert.Equal((4L, 0L), (await NodeEventsAsync("binding_registered"), await NodeEventsAsync("binding_rejected")));

        // Another App on a registered device is not a new device (also when the name differs only in case: the device
        // column and its unique key are case-insensitive). The fifth device is rejected again, and nothing is stored for it.
        var second = await ReportAsync(node, On("D1", "kimi"), On("d2", "qwen"), On("D5", "kimi"), On("D6", "doubao"));
        Assert.Equal(["D1|kimi", "d2|qwen"], second.Bindings.Select(binding => $"{binding.DeviceId}|{binding.PlatformCode}"));
        Assert.Equal(["D5|device_limit_exceeded", "D6|device_limit_exceeded"], second.Rejected.Select(item => $"{item.DeviceId}|{item.Code}"));
        Assert.Equal("6|4", await RegisteredAsync(node));
        Assert.Equal(0, await pool.CountAsync("SELECT COUNT(*) FROM collector_app_bindings WHERE node_id = @node AND device_id IN ('D5', 'D6');", ("@node", node)));
        Assert.Equal(0, await NodeEventsAsync("binding_rejected"));

        // The cap: range, unknown node, not below the registered devices, no event for a refusal or for the same value.
        foreach (var bad in new[] { 0, 65, -1 })
            Assert.Equal(("INVALID_APP_LIMITS", 400), Code(await Assert.ThrowsAsync<CollectorStoreException>(() => store.SetNodeMaxDevicesAsync(node, bad, null))));
        Assert.Equal(("APP_NODE_NOT_FOUND", 404), Code(await Assert.ThrowsAsync<CollectorStoreException>(() => store.SetNodeMaxDevicesAsync("missing-node", 8, null))));
        foreach (var low in new[] { 1, 3 })
            Assert.Equal(("APP_NODE_MAX_DEVICES_BELOW_REGISTERED", 409), Code(await Assert.ThrowsAsync<CollectorStoreException>(()
                => store.SetNodeMaxDevicesAsync(node, low, null))));
        Assert.False(await store.SetNodeMaxDevicesAsync(node, 4, null));
        Assert.Equal(0, await NodeEventsAsync("node_max_devices_changed"));
        Assert.Equal(4, (await store.GetNodeAsync(node)).MaxDevices);

        // Raised to five: the fifth device registers, the sixth is still rejected.
        var operatorId = Guid.NewGuid();
        Assert.True(await store.SetNodeMaxDevicesAsync(node, 5, operatorId));
        Assert.Equal(5, (await store.ListNodesAsync()).Single(item => item.NodeId == node).MaxDevices);
        var change = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(await pool.RowAsync(
            "SELECT CAST(detail AS CHAR) FROM collector_app_events WHERE node_id = @node AND event_kind = 'node_max_devices_changed';", ("@node", node)));
        Assert.Equal((4, 5, 4), (change.GetProperty("from").GetInt32(), change.GetProperty("to").GetInt32(), change.GetProperty("registeredDevices").GetInt32()));
        Assert.Equal($"operator:{operatorId:N}", await pool.RowAsync(
            "SELECT actor FROM collector_app_events WHERE node_id = @node AND event_kind = 'node_max_devices_changed';", ("@node", node)));
        var third = await ReportAsync(node, On("D5", "doubao"), On("D6", "doubao"));
        Assert.Equal("D5", Assert.Single(third.Bindings).DeviceId);
        Assert.Equal(("D6", "device_limit_exceeded"), (Assert.Single(third.Rejected).DeviceId, Assert.Single(third.Rejected).Code));
        Assert.Equal("7|5", await RegisteredAsync(node));
        Assert.Equal(("APP_NODE_MAX_DEVICES_BELOW_REGISTERED", 409), Code(await Assert.ThrowsAsync<CollectorStoreException>(()
            => store.SetNodeMaxDevicesAsync(node, 4, null))));

        // Every one of them still waits for approval, and the cap is per node: the other node has its own four.
        Assert.Equal(7, await pool.CountAsync(
            "SELECT COUNT(*) FROM collector_app_bindings WHERE node_id = @node AND status = 'stopped' AND stop_reason = 'disabled_by_operator' AND approved_at IS NULL;",
            ("@node", node)));
        var elsewhere = await ReportAsync(other, On("D1", "deepseek"), On("D2", "deepseek"), On("D3", "deepseek"), On("D4", "deepseek"), On("D5", "deepseek"));
        Assert.Equal((4, "D5"), (elsewhere.Bindings.Count, Assert.Single(elsewhere.Rejected).DeviceId));
        Assert.Equal("4|4", await RegisteredAsync(other));
    });

    // RF2 under concurrency. Three devices are registered and the cap is four. Two reports of the same node arrive together,
    // each with a different new device: both wait behind the node row, and exactly one device is registered.
    [AppChannelMySqlFact]
    public Task TwoConcurrentReportsOfOneNodeCannotPassTheDeviceCap() => PoolAsync(async (pool, _) =>
    {
        var store = pool.Store();
        var (node, _) = await pool.NodeAsync("mac", defaultDeviceCap: true);
        var suffix = Guid.NewGuid().ToString("N");
        AppBindingReportCommand With(params string[] devices)
            => new(node, "rev", devices.Select(device => Report(device, "doubao", $"doubao-{device}-{suffix}")).ToArray());
        Assert.Equal(3, (await store.ReportBindingsAsync(With("D1", "D2", "D3"))).Bindings.Count);

        for (var round = 0; round < 3; round++)
        {
            Task<AppBindingReportResult>[] running;
            await using (var holder = new MySqlConnection(pool.Cs))
            {
                await holder.OpenAsync();
                await using var transaction = await holder.BeginTransactionAsync();
                await using (var command = new MySqlCommand($"SELECT 1 FROM collector_app_nodes WHERE node_id = '{node}' FOR UPDATE;", holder, transaction))
                    await command.ExecuteNonQueryAsync();
                running = [Task.Run(() => store.ReportBindingsAsync(With("D4"))), Task.Run(() => store.ReportBindingsAsync(With("D5")))];
                // Both reports start by stamping the node row, which the holder has locked.
                await BlockedAsync(pool, "%UPDATE collector_app_nodes%SET last_seen_at%", 2);
                Assert.All(running, task => Assert.False(task.IsCompleted));
                await transaction.CommitAsync();
            }
            var results = await Task.WhenAll(running);
            Assert.Equal(1, results.Sum(result => result.Bindings.Count));
            Assert.Equal("device_limit_exceeded", Assert.Single(results.SelectMany(result => result.Rejected)).Code);
            Assert.Equal("4|4", await pool.RowAsync("SELECT COUNT(*), COUNT(DISTINCT device_id) FROM collector_app_bindings WHERE node_id = @node;",
                ("@node", node)));
            // Back to three devices for the next round.
            await pool.ExecAsync("DELETE FROM collector_app_bindings WHERE node_id = @node AND device_id IN ('D4', 'D5');", ("@node", node));
        }
    });

    // RF3. The failure reports of a binding the operator has not approved change nothing: no event, no binding or task or
    // platform change, and no verification breaker, also when an event of such a binding already exists. Two approved
    // bindings still trip the breaker, and "every binding rejected the emulator" looks at approved bindings only.
    [AppChannelMySqlFact]
    public Task AnUnapprovedBindingsFailureReportsChangeNothingAndOnlyApprovedBindingsTripTheBreaker() => PoolAsync(async (pool, seedTenant) =>
    {
        var rig = Build(pool.Cs, await seedTenant());
        var store = pool.Store(rig.Tenant);
        var (node, _) = await pool.NodeAsync("mac");
        var cycle = await CycleAsync(rig, "未批准", 1, "doubao", "kimi");
        var kimiTask = await TaskOfAsync(pool.Cs, cycle.Run, cycle.Prompts[0], "kimi");
        var (a, b) = (await pool.BindAsync(node, "AVD_A", "doubao"), await pool.BindAsync(node, "AVD_B", "doubao"));
        var (p1, p2) = (await pool.BindAsync(node, "AVD_P1", "doubao", approve: false), await pool.BindAsync(node, "AVD_P2", "doubao", approve: false));
        var task = (await store.LeaseAsync(new AppLeaseCommand(node, a, 120))).Task!;
        Task<AppFailureResult> ProbeAsync(Guid binding, AppFailureCode code)
            => store.ReportFailureAsync(new AppFailureCommand(node, binding, code, CollectorPhase.Probe, null));
        Task<string> PlatformAsync(string platform) => pool.RowAsync("""
            SELECT s.paused_at IS NOT NULL, (SELECT COALESCE(MAX(e.enabled + 0), 0) FROM collector_platform_entries e
                                             WHERE e.platform_code = s.platform_code AND e.channel = 'consumer_app')
            FROM collector_app_platform_states s WHERE s.platform_code = @platform;
            """, ("@platform", platform));
        var events = await pool.CountAsync("SELECT COUNT(*) FROM collector_app_events;");
        var seen = await pool.RowAsync("SELECT DATE_FORMAT(last_seen_at, '%Y-%m-%d %H:%i:%s.%f'), updated_at FROM collector_app_bindings WHERE id = UUID_TO_BIN(@id);",
            ("@id", p1.ToString()));

        // Every code that would stop a binding, pause the platform, disable the entry or start a cooldown: nothing happens.
        var ignored = new AppFailureResult(AppBindingStatus.Stopped, AppBindingStopReason.DisabledByOperator, null, false, false);
        foreach (var code in new[] { AppFailureCode.LoginRequired, AppFailureCode.ChallengeRequired, AppFailureCode.EmulatorRejected, AppFailureCode.RateLimited })
        {
            Assert.Equal(ignored, await ProbeAsync(p1, code));
            Assert.Equal(ignored, await ProbeAsync(p2, code));
        }
        // With a task (another binding's current lease, or a task that does not exist): the answer of a stale report, and
        // the task is not even looked up.
        var stale = ignored with { TaskDisposition = CollectorTaskDisposition.Stale };
        Assert.Equal(stale, await store.ReportFailureAsync(new AppFailureCommand(node, p1, AppFailureCode.ChallengeRequired, CollectorPhase.PreSend,
            new CollectorReportedTask(task.Id, task.LeaseVersion), "not_sent")));
        Assert.Equal(stale, await store.ReportFailureAsync(new AppFailureCommand(node, p1, AppFailureCode.CollectionTimeout, CollectorPhase.PostSend,
            new CollectorReportedTask(Guid.NewGuid(), 1), "unknown")));
        Assert.Equal(events, await pool.CountAsync("SELECT COUNT(*) FROM collector_app_events;"));
        Assert.Equal("0|1", await PlatformAsync("doubao"));
        Assert.Equal("stopped|disabled_by_operator|1|0", await pool.BindingAsync(p1));
        Assert.Equal("stopped|disabled_by_operator|0", await pool.ApprovalAsync(p2));
        Assert.Equal(seen, await pool.RowAsync("SELECT DATE_FORMAT(last_seen_at, '%Y-%m-%d %H:%i:%s.%f'), updated_at FROM collector_app_bindings WHERE id = UUID_TO_BIN(@id);",
            ("@id", p1.ToString())));
        Assert.Equal($"leased|1|{node}|1|0|-|-", await TaskAsync(pool.Cs, task.Id));
        Assert.Equal(1, await pool.CountAsync("SELECT COUNT(*) FROM collector_app_device_attempts WHERE task_id = UUID_TO_BIN(@id) AND failed_at IS NULL;",
            ("@id", task.Id.ToString())));
        // A foreign node still gets the usual refusal for a binding that is not its own.
        var (foreign, _) = await pool.NodeAsync("mac");
        Assert.Equal(("APP_BINDING_FORBIDDEN", 403), Code(await Assert.ThrowsAsync<CollectorStoreException>(()
            => store.ReportFailureAsync(new AppFailureCommand(foreign, p1, AppFailureCode.LoginRequired, CollectorPhase.Probe, null)))));

        // The breaker counts approved bindings only, whatever events exist: an event of an unapproved binding (as an
        // older build could have written) plus one approved report is still one binding.
        await pool.ExecAsync("""
            INSERT INTO collector_app_events (id, occurred_at, event_kind, node_id, binding_id, platform_code, actor, detail)
            VALUES (UUID_TO_BIN(UUID()), UTC_TIMESTAMP(6), 'failure_reported', @node, UUID_TO_BIN(@binding), 'doubao', 'node:synthetic',
                    JSON_OBJECT('failureCode', 'login_required'));
            """, ("@node", node), ("@binding", p2.ToString()));
        Assert.False((await ProbeAsync(b, AppFailureCode.LoginRequired)).PlatformPaused);
        Assert.Equal("0|1", await PlatformAsync("doubao"));
        // The second approved binding reports one: the platform pauses, as before.
        var tripped = await store.ReportFailureAsync(new AppFailureCommand(node, a, AppFailureCode.ChallengeRequired, CollectorPhase.PreSend,
            new CollectorReportedTask(task.Id, task.LeaseVersion), "not_sent"));
        Assert.Equal((true, CollectorTaskDisposition.Failed, AppBindingStopReason.ChallengeRequired), (tripped.PlatformPaused, tripped.TaskDisposition!.Value,
            tripped.StopReason!.Value));
        Assert.Equal("1|1", await PlatformAsync("doubao"));

        // "Every binding of the platform rejected the emulator" looks at approved bindings only. k2 has the shape an older
        // build leaves behind when it registers a binding after 052: active, never approved.
        var k1 = await pool.BindAsync(node, "AVD_A", "kimi");
        var k2Label = $"kimi-{Guid.NewGuid():N}";
        var k2 = await pool.BindAsync(node, "AVD_P1", "kimi", k2Label, approve: false);
        await pool.ExecAsync("UPDATE collector_app_bindings SET status = 'active', stop_reason = NULL, stopped_at = NULL WHERE id = UUID_TO_BIN(@id);",
            ("@id", k2.ToString()));
        Assert.Equal("active|-|0", await pool.ApprovalAsync(k2));
        // Approval, not the status column, lets a binding work: it does not lease and its report is ignored. Its lease
        // request writes nothing at all: it is not "seen now" (asking for work must not keep such a binding looking alive),
        // it leaves no event, and the waiting kimi task is not marked.
        Task<string> SeenAsync(Guid binding) => pool.RowAsync(
            "SELECT DATE_FORMAT(last_seen_at, '%Y-%m-%d %H:%i:%s.%f'), DATE_FORMAT(updated_at, '%Y-%m-%d %H:%i:%s.%f') FROM collector_app_bindings WHERE id = UUID_TO_BIN(@id);",
            ("@id", binding.ToString()));
        var (k2Seen, k2Events) = (await SeenAsync(k2), await pool.EventsAsync(k2));
        AssertEmpty(await store.LeaseAsync(new AppLeaseCommand(node, k2, 120)), AppLeaseEmptyReason.BindingStopped, 600, 600);
        Assert.Equal(k2Seen, await SeenAsync(k2));
        Assert.Equal(k2Events, await pool.EventsAsync(k2));
        Assert.Equal("pending|0|-|0|0|-|-", await TaskAsync(pool.Cs, kimiTask));
        Assert.Equal(new AppFailureResult(AppBindingStatus.Active, null, null, false, false), await ProbeAsync(k2, AppFailureCode.LoginRequired));
        Assert.True((await ProbeAsync(k1, AppFailureCode.EmulatorRejected)).PlatformUnavailable);
        Assert.Equal("0|0", await PlatformAsync("kimi"));
        // k1 is stopped now. k2 still says "active" and reported a moment ago, but a binding nobody approved is not a usable
        // binding: the platform has none left, so the stopped binding's request marks the waiting task.
        AssertEmpty(await store.LeaseAsync(new AppLeaseCommand(node, k1, 120)), AppLeaseEmptyReason.BindingStopped, 600, 600);
        Assert.Equal("pending|0|-|0|0|-|binding_stopped", await TaskAsync(pool.Cs, kimiTask));
        // Its next bindings report puts it back to "waiting for approval" (one event), and the operator can then approve it.
        var back = Assert.Single((await store.ReportBindingsAsync(new AppBindingReportCommand(node, "rev", [Report("AVD_P1", "kimi", k2Label)]))).Bindings);
        Assert.Equal((k2, AppBindingStatus.Stopped, (AppBindingStopReason?)AppBindingStopReason.DisabledByOperator), (back.BindingId, back.Status, back.StopReason));
        Assert.Equal("approval_required", (await pool.LastEventAsync("binding_stopped", k2)).GetProperty("source").GetString());
        Assert.Equal("stopped|disabled_by_operator|0", await pool.ApprovalAsync(k2));
        Assert.True(await store.ResumeBindingAsync(k2, null));
        Assert.Equal("active|-|1", await pool.ApprovalAsync(k2));
    });

    // The operator can approve an "active, never approved" binding directly as well (resume is approval even when the
    // status already says active).
    [AppChannelMySqlFact]
    public Task ResumingAnActiveButNeverApprovedBindingApprovesIt() => PoolAsync(async (pool, _) =>
    {
        var store = pool.Store();
        var (node, _) = await pool.NodeAsync("mac");
        var binding = await pool.BindAsync(node, "AVD_A", "doubao", approve: false);
        await pool.ExecAsync("UPDATE collector_app_bindings SET status = 'active', stop_reason = NULL, stopped_at = NULL WHERE id = UUID_TO_BIN(@id);",
            ("@id", binding.ToString()));
        Assert.True((await store.GetBindingAsync(binding)).ApprovalPending);
        Assert.True(await store.ResumeBindingAsync(binding, null));
        Assert.Equal("active|-|1", await pool.ApprovalAsync(binding));
        Assert.False(await store.ResumeBindingAsync(binding, null));
    });

    // RF4. A heartbeat extends a lease to the earliest of: now + the extension, the moment it was leased + the total cap
    // (AppChannel:MaxLeaseTotalSeconds, 1800 by default), and the task's deadline. Past the cap or the deadline it answers
    // like an expired lease and leaves lease_until alone, so the lease runs out and the deadline pass takes the task.
    [AppChannelMySqlFact]
    public Task AHeartbeatCannotExtendALeasePastTheTotalCapOrTheTaskDeadline() => PoolAsync(async (pool, seedTenant) =>
    {
        var rig = Build(pool.Cs, await seedTenant());
        var store = pool.Store(rig.Tenant);
        var (node, _) = await pool.NodeAsync("mac");
        await CycleAsync(rig, "总时长", 1, "doubao");
        var binding = await pool.BindAsync(node, "AVD_A", "doubao");
        var task = (await store.LeaseAsync(new AppLeaseCommand(node, binding, 60))).Task!;
        var id = ("@id", (object?)task.Id.ToString());
        Task<DateTimeOffset> BeatAsync(AppCollectorPoolStore with, int seconds) => with.HeartbeatAsync(new AppHeartbeatCommand(node, task.Id, 1, seconds));
        // Time moves by rewriting the moment the lease was taken.
        Task LeasedAgoAsync(int seconds) => pool.ExecAsync(
            "UPDATE collector_app_device_attempts SET leased_at = UTC_TIMESTAMP(6) - INTERVAL @s SECOND WHERE task_id = UUID_TO_BIN(@id);", ("@s", seconds), id);
        Task<string> LeaseAsync() => pool.RowAsync("""
            SELECT t.status, DATE_FORMAT(t.lease_until, '%Y-%m-%d %H:%i:%s.%f'), a.lease_until = t.lease_until,
                   t.lease_until = a.leased_at + INTERVAL 1800 SECOND, t.lease_until = t.deadline_at,
                   (SELECT COUNT(*) FROM collector_attempts h WHERE h.collector_task_id = t.id AND h.event_kind = 'heartbeat')
            FROM collector_tasks t JOIN collector_app_device_attempts a ON a.task_id = t.id AND a.lease_version = t.lease_version
            WHERE t.id = UUID_TO_BIN(@id);
            """, id);

        // Just leased: the full extension, as before.
        Assert.InRange(await BeatAsync(store, 900), DateTimeOffset.UtcNow.AddSeconds(880), DateTimeOffset.UtcNow.AddSeconds(905));
        Assert.EndsWith("|1|0|0|1", await LeaseAsync());

        // Leased 1000 seconds ago: 900 more would pass the cap, so the lease ends exactly 1800 seconds after it was taken.
        await LeasedAgoAsync(1000);
        Assert.InRange(await BeatAsync(store, 900), DateTimeOffset.UtcNow.AddSeconds(780), DateTimeOffset.UtcNow.AddSeconds(805));
        var capped = await LeaseAsync();
        Assert.StartsWith("collecting|", capped);
        Assert.EndsWith("|1|1|0|2", capped);
        // A short extension well inside the cap is not affected by it.
        Assert.InRange(await BeatAsync(store, 60), DateTimeOffset.UtcNow.AddSeconds(45), DateTimeOffset.UtcNow.AddSeconds(65));
        Assert.EndsWith("|1|0|0|3", await LeaseAsync());

        // Leased 1801 seconds ago: refused like an expired lease, although lease_until is still in the future. Nothing changes.
        await LeasedAgoAsync(1801);
        var before = await LeaseAsync();
        Assert.Equal(("STALE_LEASE", 409), Code(await Assert.ThrowsAsync<CollectorStoreException>(() => BeatAsync(store, 60))));
        Assert.Equal(before, await LeaseAsync());
        Assert.Equal(1, await pool.CountAsync("SELECT COUNT(*) FROM collector_tasks WHERE id = UUID_TO_BIN(@id) AND lease_until > UTC_TIMESTAMP(6);", id));
        // The cap is configuration: with 3600 seconds the same lease can still be extended.
        var longer = new AppCollectorPoolStore(pool.Database, Options.Create(new AppChannelOptions
            { AppTenantIds = [rig.Tenant.ToString()], MaxLeaseTotalSeconds = 3600 }));
        Assert.InRange(await BeatAsync(longer, 60), DateTimeOffset.UtcNow.AddSeconds(45), DateTimeOffset.UtcNow.AddSeconds(65));
        Assert.Equal(("STALE_LEASE", 409), Code(await Assert.ThrowsAsync<CollectorStoreException>(() => BeatAsync(store, 60))));

        // The deadline: 30 seconds away, so a 300-second extension ends at the deadline.
        await LeasedAgoAsync(0);
        await pool.ExecAsync("UPDATE collector_tasks SET deadline_at = UTC_TIMESTAMP(6) + INTERVAL 30 SECOND WHERE id = UUID_TO_BIN(@id);", id);
        Assert.InRange(await BeatAsync(store, 300), DateTimeOffset.UtcNow.AddSeconds(15), DateTimeOffset.UtcNow.AddSeconds(31));
        Assert.Contains("|1|0|1|", await LeaseAsync());
        // Past the deadline the lease is not extended any more; it keeps the end it has.
        await pool.ExecAsync("UPDATE collector_tasks SET deadline_at = UTC_TIMESTAMP(6) - INTERVAL 1 SECOND WHERE id = UUID_TO_BIN(@id);", id);
        before = await LeaseAsync();
        Assert.Equal(("STALE_LEASE", 409), Code(await Assert.ThrowsAsync<CollectorStoreException>(() => BeatAsync(store, 300))));
        Assert.Equal(before, await LeaseAsync());

        // The deadline pass waits for the lease to run out, then closes the task: a capped lease cannot keep it open.
        Assert.Equal(0, await rig.App.ExpireOverdueTasksAsync());
        await pool.ExecAsync("""
            UPDATE collector_tasks SET lease_until = UTC_TIMESTAMP(6) - INTERVAL 1 SECOND WHERE id = UUID_TO_BIN(@id);
            UPDATE collector_app_device_attempts SET lease_until = UTC_TIMESTAMP(6) - INTERVAL 1 SECOND WHERE task_id = UUID_TO_BIN(@id);
            """, id);
        Assert.Equal(1, await rig.App.ExpireOverdueTasksAsync());
        Assert.StartsWith("terminal_failed|1|-|", await TaskAsync(pool.Cs, task.Id));
        Assert.Equal(("STALE_LEASE", 409), Code(await Assert.ThrowsAsync<CollectorStoreException>(() => BeatAsync(store, 60))));
    });
}
