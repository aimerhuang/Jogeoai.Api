using System.Text.Json;
using Jogeoai.Api.Controllers;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using static Jogeoai.Tests.AppCycleRunSqlTests;
using static Jogeoai.Tests.AppDailyReportSqlTests;
using static Jogeoai.Tests.AppLeaseSqlTests;

namespace Jogeoai.Tests;

/// <summary>
/// 隔离库（已执行 001–052、库名含 _test）上的 App 运营写操作（implement 8.3 / 8.5），经过真实的 Store 和控制器：运营停用与恢复绑定后的领取；
/// 运营停用不被采集器的 ok 上报覆盖；调整绑定和平台的限速后领取按新值；暂停与恢复平台；恢复被判定不可用的入口；每个写操作都有一条
/// actor 为运营人员的审计事件；令牌只出现在登记和轮换的响应里；审计事件的筛选与稳定分页。未设置 JOGEOAI_APP_CHANNEL_TEST_CONNECTION 时跳过。
/// 时间推进靠改写数据库时间戳，不用 sleep；PoolAsync 清理节点、绑定、事件并恢复平台闸门与 App 入口，再核对整库行数。
/// </summary>
[Collection(AppChannelSqlCollection.Name)]
public sealed class AppAdminSqlTests
{
    private static void AssertError(IActionResult? result, int status, string code)
    {
        var response = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, JsonSerializer.SerializeToElement(response.Value).GetProperty("error").GetProperty("code").GetString());
    }

    private static string Actor(Ops ops) => $"operator:{ops.UserId:N}";

    private static async Task<string[]> OperatorEventsAsync(AppLeaseSqlTests.Pool pool, Ops ops)
        => (await TeamMonitoringConfigSqlTests.RowsAsync(pool.Cs,
                "SELECT event_kind FROM collector_app_events WHERE actor = @actor ORDER BY occurred_at, id;", ("@actor", Actor(ops))))
            .Select(row => row[0]!).ToArray();

    [AppChannelMySqlFact]
    public Task AnOperatorStopOutlivesEveryCollectorReportAndLeasingResumesOnlyWhenTheOperatorResumes() => PoolAsync(async (pool, seedTenant) =>
    {
        var rig = Build(pool.Cs, await seedTenant());
        var ops = BuildOps(pool, rig.Tenant);
        var (node, _) = await pool.NodeAsync("mac");
        await CycleAsync(rig, "停用", 2, "doubao");
        var label = $"doubao-{Guid.NewGuid():N}";
        var binding = await pool.BindAsync(node, "AVD_A", "doubao", label);
        Task<AppLeaseResult> LeaseAsync() => ops.Pool.LeaseAsync(new AppLeaseCommand(node, binding, 120));
        Task<AppBindingReportResult> ReportAsync(string health) => pool.Store().ReportBindingsAsync(new AppBindingReportCommand(node, "test-rev",
            [new AppBindingReport("AVD_A", "doubao", label, "pkg.doubao", "1.0", "1.0", "15", health)]));

        var disabled = Ok(await ops.Controller.DisableBinding(binding, default));
        Assert.True(disabled.Changed);
        Assert.Equal((AppBindingStatus.Stopped, AppBindingStopReason.DisabledByOperator), (disabled.Binding.Status, disabled.Binding.StopReason));
        Assert.NotNull(disabled.Binding.StoppedAt);
        AssertEmpty(await LeaseAsync(), AppLeaseEmptyReason.BindingStopped, 600, 600);
        Assert.False(Ok(await ops.Controller.DisableBinding(binding, default)).Changed);

        // The collector cannot lift an operator stop: neither by reporting ok, nor by reporting a reason of its own.
        Assert.Equal(AppBindingStopReason.DisabledByOperator, Assert.Single((await ReportAsync("ok")).Bindings).StopReason);
        Assert.Equal(AppBindingStopReason.DisabledByOperator, Assert.Single((await ReportAsync("device_offline")).Bindings).StopReason);
        Assert.Equal(AppBindingStopReason.DisabledByOperator, Assert.Single((await ReportAsync("ok")).Bindings).StopReason);
        var failure = await ops.Pool.ReportFailureAsync(new AppFailureCommand(node, binding, AppFailureCode.LoginRequired, CollectorPhase.Probe, null));
        Assert.Equal((AppBindingStatus.Stopped, AppBindingStopReason.DisabledByOperator), (failure.BindingStatus, failure.StopReason));
        AssertEmpty(await LeaseAsync(), AppLeaseEmptyReason.BindingStopped, 600, 600);
        Assert.Equal("stopped|disabled_by_operator|1|0", await pool.BindingAsync(binding));

        // Only the operator resumes it; the next lease gets a task.
        var resumed = Ok(await ops.Controller.ResumeBinding(binding, default));
        Assert.Equal((true, AppBindingStatus.Active, (AppBindingStopReason?)null), (resumed.Changed, resumed.Binding.Status, resumed.Binding.StopReason));
        Assert.NotNull((await LeaseAsync()).Task);
        Assert.False(Ok(await ops.Controller.ResumeBinding(binding, default)).Changed);
        AssertError((await ops.Controller.DisableBinding(Guid.NewGuid(), default)).Result, 404, "APP_BINDING_NOT_FOUND");

        Assert.Equal(["binding_stopped", "binding_resumed"], await OperatorEventsAsync(pool, ops));
        var stop = await pool.LastEventAsync("binding_stopped", binding);
        Assert.Equal((null, "disabled_by_operator", "operator"), (stop.GetProperty("from").GetString(), stop.GetProperty("to").GetString(),
            stop.GetProperty("source").GetString()));
    });

    [AppChannelMySqlFact]
    public Task RetiredLimitsAreReadOnlyAndAnActualSendIsVisibleAcrossBindingsOfTheDevice() => PoolAsync(async (pool, seedTenant) =>
    {
        var rig = Build(pool.Cs, await seedTenant());
        var ops = BuildOps(pool, rig.Tenant);
        var (node, _) = await pool.NodeAsync("mac");
        await CycleAsync(rig, "限速", 5, "doubao");
        var binding = await pool.BindAsync(node, "AVD_A", "doubao");
        var second = await pool.BindAsync(node, "AVD_A", "kimi");
        await pool.ExecAsync("UPDATE collector_app_bindings SET min_interval_seconds=120,daily_quota=1 WHERE id=UUID_TO_BIN(@id);",
            ("@id", binding.ToString()));
        AssertError((await ops.Controller.SetBindingLimits(binding, new(300, 4), default)).Result, 409, "APP_LEGACY_LIMITS_RETIRED");
        AssertError((await ops.Controller.SetPlatformLimits("doubao", new(200, 3), default)).Result, 409, "APP_LEGACY_LIMITS_RETIRED");
        var before = await ops.Pool.GetBindingAsync(binding);
        Assert.Equal((100, (int?)null, (DateTimeOffset?)null), (before.MinIntervalSeconds, before.DailyQuota, before.CooldownUntil));
        var platform = Ok(await ops.Controller.ListPlatforms(default)).Single(p => p.PlatformCode == "doubao");
        Assert.Equal((0, (int?)null), (platform.MinIntervalSeconds, platform.DailyCap));
        var task = (await ops.Pool.LeaseAsync(new AppLeaseCommand(node, binding, 120))).Task!;
        var sent = DateTimeOffset.UtcNow;
        Assert.Equal("accepted", await ops.Pool.ReportSentAsync(new(node, binding, task.Id, task.LeaseVersion, sent)));
        Assert.InRange((await ops.Pool.GetBindingAsync(second)).CooldownUntil!.Value, sent.AddSeconds(99), sent.AddSeconds(101));

        // Out of range: 400 and nothing changes.
        foreach (var (interval, quota) in new (int?, int?)[] { (59, null), (86_401, null), (300, 0), (300, 10_001), (null, null) })
            AssertError((await ops.Controller.SetBindingLimits(binding, new(interval, quota), default)).Result, 400, "INVALID_APP_LIMITS");
        foreach (var (interval, cap) in new (int?, int?)[] { (9, null), (86_401, null), (30, 0), (30, 100_001), (null, 5) })
            AssertError((await ops.Controller.SetPlatformLimits("doubao", new(interval, cap), default)).Result, 400, "INVALID_APP_LIMITS");
        AssertError((await ops.Controller.SetPlatformLimits("chatgpt", new(30), default)).Result, 400, "INVALID_PLATFORM");
        Assert.Equal("120|1", await pool.RowAsync("SELECT min_interval_seconds, daily_quota FROM collector_app_bindings WHERE id = UUID_TO_BIN(@id);",
            ("@id", binding.ToString())));
        Assert.Empty(await OperatorEventsAsync(pool, ops));
    });

    [AppChannelMySqlFact]
    public Task APausedPlatformIsLeasableAgainOnlyAfterTheOperatorResumesIt() => PoolAsync(async (pool, seedTenant) =>
    {
        var rig = Build(pool.Cs, await seedTenant());
        var ops = BuildOps(pool, rig.Tenant);
        var (node, _) = await pool.NodeAsync("mac");
        await CycleAsync(rig, "暂停", 1, "doubao");
        var binding = await pool.BindAsync(node, "AVD_A", "doubao");

        var paused = Ok(await ops.Controller.PausePlatform("doubao", default));
        Assert.Equal((true, true, AppAdminRules.OperatorPauseReason), (paused.Changed, paused.Platform.Paused, paused.Platform.PauseReason));
        Assert.False(Ok(await ops.Controller.PausePlatform("DOUBAO", default)).Changed);
        AssertEmpty(await ops.Pool.LeaseAsync(new AppLeaseCommand(node, binding, 120)), AppLeaseEmptyReason.PlatformPaused, 600, 600);
        var resumed = Ok(await ops.Controller.ResumePlatform("doubao", default));
        Assert.Equal((true, false), (resumed.Changed, resumed.Platform.Paused));
        Assert.NotNull((await ops.Pool.LeaseAsync(new AppLeaseCommand(node, binding, 120))).Task);

        // A platform the verification breaker paused keeps that reason when an operator also asks for a pause.
        await pool.ExecAsync("UPDATE collector_app_platform_states SET paused_at = UTC_TIMESTAMP(6), pause_reason = 'verification_breaker' WHERE platform_code = 'kimi';");
        var kimi = Ok(await ops.Controller.PausePlatform("kimi", default));
        Assert.Equal((false, "verification_breaker"), (kimi.Changed, kimi.Platform.PauseReason));
        AssertError((await ops.Controller.PausePlatform("chatgpt", default)).Result, 400, "INVALID_PLATFORM");
        Assert.Equal(["platform_paused", "platform_resumed"], await OperatorEventsAsync(pool, ops));
    });

    [AppChannelMySqlFact]
    public Task RestoringAnEntryTheEmulatorVerdictDisabledMakesThePlatformLeasableAgain() => PoolAsync(async (pool, seedTenant) =>
    {
        var rig = Build(pool.Cs, await seedTenant());
        var ops = BuildOps(pool, rig.Tenant);
        var (node, _) = await pool.NodeAsync("mac");
        await CycleAsync(rig, "入口", 1, "doubao");
        var binding = await pool.BindAsync(node, "AVD_A", "doubao");
        Task<AppLeaseResult> LeaseAsync() => ops.Pool.LeaseAsync(new AppLeaseCommand(node, binding, 120));

        var verdict = await ops.Pool.ReportFailureAsync(new AppFailureCommand(node, binding, AppFailureCode.EmulatorRejected, CollectorPhase.Probe, null));
        Assert.True(verdict.PlatformUnavailable);
        var state = Ok(await ops.Controller.ListPlatforms(default)).Single(item => item.PlatformCode == "doubao");
        Assert.Equal((false, AppUnavailableReasons.EmulatorRejected, true), (state.EntryAvailable, state.UnavailableReason, state.EntryRestorable));
        var ernie = Ok(await ops.Controller.ListPlatforms(default)).Single(item => item.PlatformCode == "ernie");
        Assert.Equal((false, AppUnavailableReasons.ContentUnreadable, false), (ernie.EntryAvailable, ernie.UnavailableReason, ernie.EntryRestorable));
        // The alert view reports the verdict, not the seeded ernie entry.
        Assert.Equal(["doubao"], (await ops.Alerts.GetAlertsAsync()).Platforms.Select(item => item.PlatformCode));

        // Resuming the binding does not bring the entry back (contract 9.5).
        Assert.True(Ok(await ops.Controller.ResumeBinding(binding, default)).Changed);
        AssertEmpty(await LeaseAsync(), AppLeaseEmptyReason.PlatformUnavailable, 600, 600);

        AssertError((await ops.Controller.RestorePlatformEntry("ernie", default)).Result, 409, "APP_ENTRY_NOT_RESTORABLE");
        AssertError((await ops.Controller.RestorePlatformEntry("chatgpt", default)).Result, 400, "INVALID_PLATFORM");
        var restored = Ok(await ops.Controller.RestorePlatformEntry("doubao", default));
        Assert.Equal((true, true, (string?)null), (restored.Changed, restored.Platform.EntryAvailable, restored.Platform.UnavailableReason));
        Assert.False(Ok(await ops.Controller.RestorePlatformEntry("doubao", default)).Changed);
        Assert.Equal("0|APP_CONTENT_UNREADABLE", await pool.RowAsync(
            "SELECT enabled + 0, unavailable_reason FROM collector_platform_entries WHERE platform_code = 'ernie' AND channel = 'consumer_app';"));
        Assert.NotNull((await LeaseAsync()).Task);

        Assert.Equal(["binding_resumed", "platform_entry_restored"], await OperatorEventsAsync(pool, ops));
        Assert.Equal(AppUnavailableReasons.EmulatorRejected, JsonSerializer.Deserialize<JsonElement>(await pool.RowAsync(
            "SELECT CAST(detail AS CHAR) FROM collector_app_events WHERE event_kind = 'platform_entry_restored' AND actor = @actor;", ("@actor", Actor(ops))))
            .GetProperty("from").GetString());
    });

    // Migration 052, RF2: the device cap is set per node through the admin route, never below what the node has registered;
    // each actual change leaves one operator event, and the cap applies to the node's next bindings report.
    [AppChannelMySqlFact]
    public Task TheDeviceCapIsSetThroughTheAdminRouteAndAppliesToTheNextReport() => PoolAsync(async (pool, _) =>
    {
        var ops = BuildOps(pool);
        var c = ops.Controller;
        var (node, _) = await pool.NodeAsync("mac", defaultDeviceCap: true);
        await pool.BindAsync(node, "AVD_A", "doubao");
        await pool.BindAsync(node, "AVD_B", "doubao");
        Assert.Equal(AppPoolRules.DefaultMaxDevicesPerNode, Ok(await c.ListNodes(default)).Single(item => item.NodeId == node).MaxDevices);

        foreach (var bad in new int?[] { null, 0, 65 })
            AssertError((await c.SetNodeMaxDevices(node, new(bad), default)).Result, 400, "INVALID_APP_LIMITS");
        AssertError((await c.SetNodeMaxDevices(node, new(1), default)).Result, 409, "APP_NODE_MAX_DEVICES_BELOW_REGISTERED");
        AssertError((await c.SetNodeMaxDevices("missing-node", new(8), default)).Result, 404, "APP_NODE_NOT_FOUND");
        var same = Ok(await c.SetNodeMaxDevices(node, new(4), default));
        Assert.Equal((false, 4), (same.Changed, same.Node.MaxDevices));
        Assert.Empty(await OperatorEventsAsync(pool, ops));

        var raised = Ok(await c.SetNodeMaxDevices(node, new(8), default));
        Assert.Equal((true, 8), (raised.Changed, raised.Node.MaxDevices));
        // Exactly the number of registered devices is allowed.
        var lowered = Ok(await c.SetNodeMaxDevices(node, new(2), default));
        Assert.Equal((true, 2), (lowered.Changed, lowered.Node.MaxDevices));
        Assert.Equal(["node_max_devices_changed", "node_max_devices_changed"], await OperatorEventsAsync(pool, ops));

        // The node's next report: a third device is over the cap, another App on a registered device is not.
        var report = await ops.Pool.ReportBindingsAsync(new AppBindingReportCommand(node, "test-rev",
        [
            new AppBindingReport("AVD_C", "doubao", $"doubao-{Guid.NewGuid():N}", "pkg.doubao", "1.0", "1.0", "15", "ok"),
            new AppBindingReport("AVD_A", "kimi", $"kimi-{Guid.NewGuid():N}", "pkg.kimi", "1.0", "1.0", "15", "ok"),
        ]));
        Assert.Equal(("AVD_C", AppPoolRules.DeviceLimitExceededCode), (Assert.Single(report.Rejected).DeviceId, Assert.Single(report.Rejected).Code));
        var added = Assert.Single(report.Bindings);
        Assert.Equal(("AVD_A", "kimi", AppBindingStatus.Stopped, (AppBindingStopReason?)AppBindingStopReason.DisabledByOperator),
            (added.DeviceId, added.PlatformCode, added.Status, added.StopReason));
        // The new binding shows as waiting for approval in the operator's list; the two approved ones do not.
        var listed = Ok(await c.ListBindings(node, null, null, default));
        Assert.Equal([added.BindingId], listed.Where(item => item.ApprovalPending).Select(item => item.Id));
        Assert.All(listed.Where(item => !item.ApprovalPending), item => Assert.NotNull(item.ApprovedAt));
        // Approving it through the resume route.
        var approved = Ok(await c.ResumeBinding(added.BindingId, default));
        Assert.Equal((true, false, AppBindingStatus.Active), (approved.Changed, approved.Binding.ApprovalPending, approved.Binding.Status));
        Assert.NotNull(approved.Binding.ApprovedAt);
    });

    // PRD A10: by task id an operator sees every attempt in lease order — device, account label, whether and when the question
    // was sent, why it failed — and which attempt produced the formal answer. Read-only; no question or answer text.
    [AppChannelMySqlFact]
    public Task TheAttemptHistoryOfATaskListsEveryLeaseWithItsDeviceAccountSendStateAndOutcome() => PoolAsync(async (pool, seedTenant) =>
    {
        var rig = Build(pool.Cs, await seedTenant());
        var ops = BuildOps(pool, rig.Tenant);
        var (node, _) = await pool.NodeAsync("mac");
        var cycle = await CycleAsync(rig, "追溯", 1, "doubao", "kimi");
        var labels = new[] { $"doubao-a-{Guid.NewGuid():N}", $"doubao-b-{Guid.NewGuid():N}", $"doubao-c-{Guid.NewGuid():N}" };
        var (a, b, c) = (await pool.BindAsync(node, "AVD_A", "doubao", labels[0]), await pool.BindAsync(node, "AVD_B", "doubao", labels[1]),
            await pool.BindAsync(node, "AVD_C", "doubao", labels[2]));
        var taskId = await TaskOfAsync(pool.Cs, cycle.Run, cycle.Prompts[0], "doubao");
        Task<AppLeaseResult> LeaseAsync(Guid binding) => ops.Pool.LeaseAsync(new AppLeaseCommand(node, binding, 120));

        // Never leased: the task is there, with no attempt.
        var fresh = Ok(await ops.Controller.TaskAttempts(taskId, default));
        Assert.Equal((taskId, "doubao", "pending", null, null, 0, null, null), (fresh.TaskId, fresh.PlatformCode, fresh.Status, fresh.Outcome,
            fresh.NotCollectedCode, fresh.LeaseVersion, fresh.LastErrorCode, fresh.CompletedAt));
        Assert.NotNull(fresh.DeadlineAt);
        Assert.Empty(fresh.Attempts);

        // Lease 1: device A fails before sending. Lease 2: device B goes silent and its lease runs out. Lease 3: device C
        // sends and its answer is accepted; B's answer arrives afterwards and is stale.
        var first = (await LeaseAsync(a)).Task!;
        Assert.Equal(taskId, first.Id);
        await ops.Pool.ReportFailureAsync(new AppFailureCommand(node, a, AppFailureCode.ContractChanged, CollectorPhase.PreSend,
            new CollectorReportedTask(first.Id, first.LeaseVersion), "not_sent"));
        var second = (await LeaseAsync(b)).Task!;
        await pool.ExecAsync("""
            UPDATE collector_tasks SET lease_until = UTC_TIMESTAMP(6) - INTERVAL 1 SECOND WHERE id = UUID_TO_BIN(@id);
            UPDATE collector_app_device_attempts SET lease_until = UTC_TIMESTAMP(6) - INTERVAL 1 SECOND WHERE task_id = UUID_TO_BIN(@id) AND lease_version = 2;
            """, ("@id", taskId.ToString()));
        var third = (await LeaseAsync(c)).Task!;
        Assert.Equal((taskId, 2, 3), (second.Id, second.LeaseVersion, third.LeaseVersion));
        var open = Ok(await ops.Controller.TaskAttempts(taskId, default));
        Assert.Equal(("leased", null, 3, "lease_expired"), (open.Status, open.Outcome, open.LeaseVersion, open.LastErrorCode));
        Assert.All(open.Attempts, attempt => Assert.False(attempt.Accepted));
        const string answer = "追溯用例的回答：推荐随手记。";
        Assert.Equal(CollectorResultDisposition.Accepted, (await ops.Pool.SubmitResultAsync(
            AppCreditSqlTests.Result(node, third, "attempts-key-accepted", answer) with { DeviceId = "AVD_C" })).Disposition);
        Assert.Equal(CollectorResultDisposition.Stale, (await ops.Pool.SubmitResultAsync(
            AppCreditSqlTests.Result(node, second, "attempts-key-late", "迟到的另一条回答。") with { DeviceId = "AVD_B" })).Disposition);

        var view = Ok(await ops.Controller.TaskAttempts(taskId, default));
        Assert.Equal((taskId, "doubao", "succeeded", AppTaskAttemptsView.OutcomeCollected, null, 3, "lease_expired"), (view.TaskId, view.PlatformCode,
            view.Status, view.Outcome, view.NotCollectedCode, view.LeaseVersion, view.LastErrorCode));
        Assert.NotNull(view.CompletedAt);
        Assert.Equal(fresh.DeadlineAt, view.DeadlineAt);
        Assert.Equal([1, 2, 3], view.Attempts.Select(attempt => attempt.LeaseVersion));
        Assert.Equal([("AVD_A", a, labels[0]), ("AVD_B", b, labels[1]), ("AVD_C", c, labels[2])],
            view.Attempts.Select(attempt => (attempt.DeviceId, attempt.BindingId, attempt.AccountLabel!)));
        Assert.All(view.Attempts, attempt => Assert.Equal((node, "doubao"), (attempt.NodeId, attempt.PlatformCode)));
        Assert.Equal(["not_sent", null, "sent"], view.Attempts.Select(attempt => attempt.SendState));
        Assert.Equal(["contract_changed", null, null], view.Attempts.Select(attempt => attempt.FailureCode));
        Assert.Equal([true, false, false], view.Attempts.Select(attempt => attempt.FailedAt is not null));
        Assert.Equal([false, false, true], view.Attempts.Select(attempt => attempt.SentAt is not null));
        // Only the attempt whose answer became the formal one is marked; the late answer of lease 2 is not.
        Assert.Equal([false, false, true], view.Attempts.Select(attempt => attempt.Accepted));
        Assert.All(view.Attempts, attempt => Assert.True(attempt.LeasedAt <= attempt.LeaseUntil || attempt.LeaseVersion == 2));
        Assert.True(view.Attempts[1].LeaseUntil < DateTimeOffset.UtcNow);
        // Identifiers, codes and times only: neither the question nor either answer.
        var json = JsonSerializer.Serialize(view, ApiJson);
        foreach (var text in new[] { "追溯", "哪个记账工具好用", answer, "迟到的另一条回答" }) Assert.DoesNotContain(text, json);
        // The read wrote nothing.
        Assert.Empty(await OperatorEventsAsync(pool, ops));

        // A task closed by a verification screen: not collected, with the reason code and the one attempt that reported it.
        var onKimi = await pool.BindAsync(node, "AVD_A", "kimi");
        var challenged = (await LeaseAsync(onKimi)).Task!;
        await ops.Pool.ReportFailureAsync(new AppFailureCommand(node, onKimi, AppFailureCode.ChallengeRequired, CollectorPhase.PreSend,
            new CollectorReportedTask(challenged.Id, challenged.LeaseVersion), "not_sent"));
        var closed = Ok(await ops.Controller.TaskAttempts(challenged.Id, default));
        Assert.Equal(("kimi", "terminal_failed", AppTaskAttemptsView.OutcomeNotCollected, AppNotCollectedCodes.Failed, 1, "challenge_required"),
            (closed.PlatformCode, closed.Status, closed.Outcome, closed.NotCollectedCode, closed.LeaseVersion, closed.LastErrorCode));
        Assert.NotNull(closed.CompletedAt);
        var only = Assert.Single(closed.Attempts);
        Assert.Equal((1, "AVD_A", onKimi, "kimi", "not_sent", "challenge_required", false),
            (only.LeaseVersion, only.DeviceId, only.BindingId, only.PlatformCode, only.SendState, only.FailureCode, only.Accepted));

        // Unknown task, and a web task of the same tenant: both 404, the route does not say which.
        AssertError((await ops.Controller.TaskAttempts(Guid.NewGuid(), default)).Result, 404, "APP_TASK_NOT_FOUND");
        var webRun = (await new DomesticCollectorStore(pool.Database).CreateRunAsync(new CreateVisibilityRunCommand(rig.Tenant, cycle.Site, cycle.Prompts,
            [new CollectorPlatformSelection("doubao", "doubao.consumer_web")], $"web-attempts-{Guid.NewGuid():N}"))).RunId;
        var webTask = Guid.Parse((await TeamMonitoringConfigSqlTests.RowsAsync(pool.Cs,
            "SELECT BIN_TO_UUID(id) FROM collector_tasks WHERE run_id = UUID_TO_BIN(@run) LIMIT 1;", ("@run", webRun.ToString())))[0][0]!);
        AssertError((await ops.Controller.TaskAttempts(webTask, default)).Result, 404, "APP_TASK_NOT_FOUND");
    });

    [AppChannelMySqlFact]
    public Task EveryOperatorWriteLeavesOneEventTheTokenIsOnlyInItsOwnResponsesAndEventsPageStably() => PoolAsync(async (pool, seedTenant) =>
    {
        var rig = Build(pool.Cs, await seedTenant());
        var ops = BuildOps(pool, rig.Tenant);
        var c = ops.Controller;
        var nodeId = $"ops-node-{Guid.NewGuid():N}";
        pool.Track(nodeId);

        var registered = Assert.IsType<ObjectResult>((await c.RegisterNode(new(nodeId), default)).Result);
        Assert.Equal(201, registered.StatusCode);
        var token = ((AppNodeCredential)registered.Value!).Token;
        AssertError((await c.RegisterNode(new(nodeId), default)).Result, 409, "APP_NODE_EXISTS");
        AssertError((await c.RegisterNode(new("  "), default)).Result, 400, "INVALID_APP_NODE");
        var rotated = Ok(await c.RotateNodeToken(nodeId, default)).Token;
        Assert.NotEqual(token, rotated);
        Assert.Equal((true, AppNodeStatus.Disabled), (Ok(await c.DisableNode(nodeId, default)).Changed, Ok(await c.ListNodes(default)).Single(n => n.NodeId == nodeId).Status));
        Assert.False(Ok(await c.DisableNode(nodeId, default)).Changed);
        Assert.True(Ok(await c.ResumeNode(nodeId, default)).Changed);
        AssertError((await c.ResumeNode("missing-node", default)).Result, 404, "APP_NODE_NOT_FOUND");

        var binding = await pool.BindAsync(nodeId, "AVD_A", "doubao");
        Assert.True(Ok(await c.DisableBinding(binding, default)).Changed);
        Assert.Equal([binding], Ok(await c.ListBindings(nodeId, "doubao", "stopped", default)).Select(item => item.Id));
        Assert.Empty(Ok(await c.ListBindings(nodeId, null, "active", default)));
        AssertError((await c.ListBindings(null, "chatgpt", null, default)).Result, 400, "INVALID_PLATFORM");
        Assert.True(Ok(await c.ResumeBinding(binding, default)).Changed);
        AssertError((await c.SetBindingLimits(binding, new(600, 5), default)).Result, 409, "APP_LEGACY_LIMITS_RETIRED");
        Assert.True(Ok(await c.PausePlatform("kimi", default)).Changed);
        Assert.True(Ok(await c.ResumePlatform("kimi", default)).Changed);
        AssertError((await c.SetPlatformLimits("kimi", new(60, 100), default)).Result, 409, "APP_LEGACY_LIMITS_RETIRED");
        await ops.Pool.ReportFailureAsync(new AppFailureCommand(nodeId, binding, AppFailureCode.EmulatorRejected, CollectorPhase.Probe, null));
        Assert.True(Ok(await c.RestorePlatformEntry("doubao", default)).Changed);

        // Excluding and revoking on behalf of a tenant: the exclusion row carries the operator, the event carries ids only.
        var site = await SeedAppBrandAsync(rig, "运营排除", "doubao");
        var prompt = await SeedPromptAsync(pool.Cs, site, "运营排除的提问原文", 1);
        const string reason = "运营排除原因-独特";
        var excluded = Assert.IsType<ObjectResult>((await c.ExcludePrompt(new(rig.Tenant, site, prompt, reason), default)).Result);
        Assert.Equal((201, ops.UserId), (excluded.StatusCode, ((AppPromptExclusionView)excluded.Value!).CreatedBy));
        AssertError((await c.ExcludePrompt(new(rig.Tenant, site, prompt, reason), default)).Result, 409, "APP_PROMPT_ALREADY_EXCLUDED");
        AssertError((await c.ExcludePrompt(new(Guid.NewGuid(), site, prompt, reason), default)).Result, 404, "RESOURCE_NOT_FOUND");
        Assert.Equal(ops.UserId, Ok(await c.RevokePromptExclusion(new(rig.Tenant, site, prompt), default)).RevokedBy);
        AssertError((await c.RevokePromptExclusion(new(rig.Tenant, site, prompt), default)).Result, 404, "APP_PROMPT_EXCLUSION_NOT_FOUND");

        // One event per change, none for the no-ops and the refusals.
        Assert.Equal(["node_registered", "node_token_rotated", "node_status_changed", "node_status_changed", "binding_stopped", "binding_resumed",
            "platform_paused", "platform_resumed", "platform_entry_restored", "prompt_excluded",
            "prompt_exclusion_revoked"], await OperatorEventsAsync(pool, ops));
        var exclusionEvent = await pool.RowAsync("SELECT CAST(detail AS CHAR) FROM collector_app_events WHERE event_kind = 'prompt_excluded' AND actor = @actor;",
            ("@actor", Actor(ops)));
        Assert.Contains(prompt.ToString(), exclusionEvent);
        Assert.DoesNotContain(reason, exclusionEvent);
        Assert.DoesNotContain("运营排除的提问原文", exclusionEvent);

        // Events: the node's events in pages of two are the whole list once, newest first; filters and bad input.
        var all = Ok(await c.Events(null, null, nodeId, null, null, null, null, 200, default)).Items;
        Assert.True(all.Count >= 8);
        var paged = new List<AppEventView>();
        string? cursor = null;
        do
        {
            var page = Ok(await c.Events(null, null, nodeId, null, null, null, cursor, 2, default));
            Assert.InRange(page.Items.Count, 1, 2);
            paged.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(all.Select(item => item.Id), paged.Select(item => item.Id));
        Assert.Equal(all.OrderByDescending(item => item.OccurredAt).Select(item => item.OccurredAt), all.Select(item => item.OccurredAt));
        var kimiPause = Assert.Single(Ok(await c.Events(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1), null, null, "kimi",
            "platform_paused", null, null, default)).Items, item => item.Actor == Actor(ops));
        Assert.Equal("operator_paused", kimiPause.Detail!.Value.GetProperty("reason").GetString());
        Assert.Contains(Ok(await c.Events(null, null, null, binding, null, null, null, null, default)).Items, item => item.EventKind == "binding_resumed");
        AssertError((await c.Events(null, null, null, null, "chatgpt", null, null, null, default)).Result, 400, "INVALID_FILTER");
        AssertError((await c.Events(null, null, null, null, null, null, "not-a-cursor", null, default)).Result, 400, "INVALID_CURSOR");
        AssertError((await c.Events(null, null, null, null, null, null, null, 0, default)).Result, 400, "INVALID_FILTER");
        AssertError((await c.Events(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(-1), null, null, null, null, null, null, default)).Result,
            400, "INVALID_FILTER");

        // The token is in the register and rotate responses only: not in any read, event or column (only its hash is stored).
        var reads = string.Join("\n", JsonSerializer.Serialize(Ok(await c.ListNodes(default)), ApiJson), JsonSerializer.Serialize(all, ApiJson),
            JsonSerializer.Serialize(Ok(await c.ListBindings(nodeId, null, null, default)), ApiJson), JsonSerializer.Serialize(Ok(await c.Alerts(default)), ApiJson),
            JsonSerializer.Serialize(Ok(await c.ListPlatforms(default)), ApiJson));
        foreach (var secret in new[] { token, rotated })
        {
            Assert.DoesNotContain(secret, reads);
            Assert.Equal(0, await pool.CountAsync("""
                SELECT (SELECT COUNT(*) FROM collector_app_events WHERE CAST(detail AS CHAR) LIKE CONCAT('%', @secret, '%') OR actor LIKE CONCAT('%', @secret, '%'))
                     + (SELECT COUNT(*) FROM collector_app_nodes WHERE token_hash = @secret OR node_id = @secret);
                """, ("@secret", secret)));
        }
        Assert.Equal(AppCollectorPoolStore.HashToken(rotated), await pool.RowAsync("SELECT token_hash FROM collector_app_nodes WHERE node_id = @node;", ("@node", nodeId)));
        Assert.Equal(nodeId, (await ops.Pool.AuthenticateNodeAsync(rotated))!.NodeId);
        Assert.Null(await ops.Pool.AuthenticateNodeAsync(token));
    });
}
