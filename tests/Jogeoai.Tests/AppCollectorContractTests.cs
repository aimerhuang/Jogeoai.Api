using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jogeoai.Api.Controllers;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace Jogeoai.Tests;

/// <summary>
/// App 采集节点接口的契约（不连数据库，contract.md 第 9 节）：节点令牌鉴权的各种结果、请求校验、每个响应的 JSON 字段名与
/// 可空字段的形状（可空字段总是出现、值为 null，不省略）、retryAfterSeconds 的取值范围、枚举的传输字符串。
/// </summary>
public sealed class AppCollectorContractTests
{
    private const string NodeToken = "app-node-contract-token";
    private const string WebToken = "web-collector-shared-token";

    // Same as Program.cs AddJsonOptions: web defaults plus string enums.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static readonly Guid Id = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTimeOffset At = new(2026, 11, 17, 1, 11, 0, 123, TimeSpan.Zero);

    [Fact]
    public void LeaseResponseAlwaysCarriesTaskReasonAndRetryAndTheTaskHasTheContractFields()
    {
        Assert.Equal("""{"task":null,"reason":"binding_interval","retryAfterSeconds":120}""",
            JsonSerializer.Serialize(new AppLeaseResponse(null, AppLeaseEmptyReason.BindingInterval, 120), Json));
        var task = new AppTaskResponse(Id, Id, Id, Id, Id, "doubao", "doubao.consumer_app", Id, "哪个记账工具好用？", "cn", "zh-CN", "consumer_app",
            1, "2026-11-24T01:11:00.000Z", 2, "2026-11-17T01:13:00.123Z", Id);
        var body = JsonSerializer.SerializeToNode(new AppLeaseResponse(task, null, null), Json)!.AsObject();
        Assert.Equal(["task", "reason", "retryAfterSeconds"], body.Select(member => member.Key));
        Assert.Null(body["reason"]);
        Assert.Null(body["retryAfterSeconds"]);
        Assert.Equal(["id", "tenantId", "runId", "checkId", "brandProjectId", "platformCode", "platformEntryCode", "questionVersionId", "questionText",
            "region", "language", "channel", "promptRuleVersion", "deadlineAt", "leaseVersion", "leaseExpiresAt", "bindingId"],
            body["task"]!.AsObject().Select(member => member.Key));
        Assert.Equal(("consumer_app", "cn", "zh-CN", 1, "2026-11-24T01:11:00.000Z", Id.ToString()),
            ((string)body["task"]!["channel"]!, (string)body["task"]!["region"]!, (string)body["task"]!["language"]!,
                (int)body["task"]!["promptRuleVersion"]!, (string)body["task"]!["deadlineAt"]!, (string)body["task"]!["bindingId"]!));
        // The three reasons added in phase 5 and the full list, in the contract's order.
        Assert.Equal(["no_task", "binding_stopped", "binding_cooldown", "binding_interval", "binding_quota", "device_busy", "device_offline", "device_cooldown", "platform_interval",
            "platform_daily_cap", "platform_paused", "platform_unavailable", "credit_insufficient"],
            Enum.GetValues<AppLeaseEmptyReason>().Select(value => JsonSerializer.Serialize(value, Json).Trim('"')));
    }

    [Fact]
    public void BindingReportFailureAndHeartbeatResponsesWriteNullableMembersAsNull()
    {
        var active = new AppBindingResponse("AI_Apps_4KB", "doubao", Id, AppBindingStatus.Active, null, null, 300, null);
        var stopped = new AppBindingResponse("AI_Apps_4KB_2", "kimi", Id, AppBindingStatus.Stopped, AppBindingStopReason.LoginRequired,
            "2026-11-17T16:00:00.000Z", 300, 40);
        Assert.Equal("{\"bindings\":[{\"deviceId\":\"AI_Apps_4KB\",\"platformCode\":\"doubao\",\"bindingId\":\"" + Id + "\",\"status\":\"active\","
            + "\"stopReason\":null,\"cooldownUntil\":null,\"minIntervalSeconds\":300,\"dailyQuota\":null},"
            + "{\"deviceId\":\"AI_Apps_4KB_2\",\"platformCode\":\"kimi\",\"bindingId\":\"" + Id + "\",\"status\":\"stopped\","
            + "\"stopReason\":\"login_required\",\"cooldownUntil\":\"2026-11-17T16:00:00.000Z\",\"minIntervalSeconds\":300,\"dailyQuota\":40}],"
            + "\"rejected\":[{\"deviceId\":\"AI_Apps_4KB\",\"platformCode\":\"qwen\",\"code\":\"APP_ACCOUNT_ALREADY_BOUND\",\"message\":\"m\"}],\"dispatchProtocol\":\"device-v1\"}",
            JsonSerializer.Serialize(new AppBindingReportResponse([active, stopped],
                [new AppBindingRejection("AI_Apps_4KB", "qwen", "APP_ACCOUNT_ALREADY_BOUND", "m")]), Json));
        Assert.Equal("""{"bindings":[],"rejected":[],"dispatchProtocol":"device-v1"}""", JsonSerializer.Serialize(new AppBindingReportResponse([], []), Json));

        // implement 6.0a: cooldownUntil is always written, null when the binding is not cooling down.
        Assert.Equal("""{"bindingStatus":"active","stopReason":null,"cooldownUntil":null,"taskDisposition":null,"platformPaused":false,"platformUnavailable":false}""",
            JsonSerializer.Serialize(new AppFailureResponse(AppBindingStatus.Active, null, null, null, false, false), Json));
        Assert.Equal("""{"bindingStatus":"stopped","stopReason":"emulator_rejected","cooldownUntil":"2026-11-17T16:00:00.000Z","taskDisposition":"requeued","platformPaused":true,"platformUnavailable":true}""",
            JsonSerializer.Serialize(new AppFailureResponse(AppBindingStatus.Stopped, AppBindingStopReason.EmulatorRejected,
                "2026-11-17T16:00:00.000Z", CollectorTaskDisposition.Requeued, true, true), Json));
        Assert.Equal(["requeued", "failed", "stale"], Enum.GetValues<CollectorTaskDisposition>().Select(value => JsonSerializer.Serialize(value, Json).Trim('"')));
        Assert.Equal("""{"leaseExpiresAt":"2026-11-17T01:11:00.123Z"}""", JsonSerializer.Serialize(new CollectorHeartbeatResponse("2026-11-17T01:11:00.123Z"), Json));
    }

    [Fact]
    public void RequestsBindFromTheContractFieldNames()
    {
        var lease = JsonSerializer.Deserialize<AppLeaseRequest>($$"""{"nodeId":"mac-1","bindingId":"{{Id}}","leaseSeconds":300}""", Json)!;
        Assert.Equal(("mac-1", Id.ToString(), 300), (lease.NodeId, lease.BindingId, lease.LeaseSeconds));
        var heartbeat = JsonSerializer.Deserialize<AppHeartbeatRequest>($$"""{"nodeId":"mac-1","taskId":"{{Id}}","leaseVersion":3,"extendSeconds":90}""", Json)!;
        Assert.Equal((Id.ToString(), 3, 90), (heartbeat.TaskId, heartbeat.LeaseVersion, heartbeat.ExtendSeconds));
        var failure = JsonSerializer.Deserialize<AppFailureRequest>(
            $$$"""{"nodeId":"mac-1","bindingId":"{{{Id}}}","failureCode":"app_crashed","phase":"post_send","task":{"taskId":"{{{Id}}}","leaseVersion":2}}""", Json)!;
        Assert.Equal(("app_crashed", "post_send", Id.ToString(), 2), (failure.FailureCode, failure.Phase, failure.Task!.TaskId, failure.Task.LeaseVersion));
        var report = JsonSerializer.Deserialize<AppBindingReportRequest>("""
            {"nodeId":"mac-1","collectorBuildRevision":"rev-1","bindings":[{"deviceId":"AI_Apps_4KB","platformCode":"doubao","accountLabel":"doubao-a",
              "appPackage":"com.larus.nova","reviewedAppVersion":"15.7.0","installedAppVersion":"15.7.0","androidVersion":"15","health":"ok"}]}
            """, Json)!;
        Assert.Equal(new AppBindingReport("AI_Apps_4KB", "doubao", "doubao-a", "com.larus.nova", "15.7.0", "15.7.0", "15", "ok"), Assert.Single(report.Bindings!));
    }

    [Fact]
    public async Task EveryActionRejectsAMissingUnknownOrWebTokenBeforeAnyStoreWork()
    {
        var f = new Fixture();
        Func<Task<IActionResult?>>[] actions =
        [
            async () => (await f.Controller.ReportBindings(new AppBindingReportRequest(f.Node, "rev", []), default)).Result,
            async () => (await f.Controller.Lease(new AppLeaseRequest(f.Node, Id.ToString(), 60, "device-v1", true), default)).Result,
            async () => (await f.Controller.Sent(new(f.Node, Id.ToString(), Id.ToString(), 2, At), default)).Result,
            async () => (await f.Controller.Heartbeat(new AppHeartbeatRequest(f.Node, Id.ToString(), 1), default)).Result,
            async () => (await f.Controller.Failure(new AppFailureRequest(f.Node, Id.ToString(), "login_required", "probe"), default)).Result,
            async () => (await f.Controller.Result(AppResultContractTests.Request(f.Node), default)).Result,
        ];
        foreach (var header in new[] { "", "Bearer ", NodeToken, "Basic " + NodeToken, "Bearer unknown-token", "Bearer " + WebToken })
        {
            f.Authorization = header;
            foreach (var action in actions) AssertError(await action(), 401, "UNAUTHORIZED");
        }
        // Without a bearer token the store is not even asked; with one, only the token lookup ran.
        Assert.Equal(["AuthenticateNodeAsync:unknown-token", "AuthenticateNodeAsync:" + WebToken],
            f.Calls.Select(call => $"{call.Method}:{call.Args[0]}").Distinct());

        f.Calls.Clear();
        f.Authorization = "Bearer " + NodeToken;
        f.Status = AppNodeStatus.Disabled;
        foreach (var action in actions) AssertError(await action(), 403, "APP_NODE_DISABLED");
        f.Status = AppNodeStatus.Active;
        f.Node = "another-node";
        foreach (var action in actions) AssertError(await action(), 403, "APP_NODE_MISMATCH");
        f.Node = "MAC-1"; // the comparison is exact
        foreach (var action in actions) AssertError(await action(), 403, "APP_NODE_MISMATCH");
        f.Node = null;
        foreach (var action in actions) AssertError(await action(), 400, "INVALID_REQUEST");
        Assert.All(f.Calls, call => Assert.Equal(nameof(IAppCollectorPoolStore.AuthenticateNodeAsync), call.Method));

        f.Node = "mac-1";
        foreach (var action in actions) Assert.IsType<OkObjectResult>(await action());
    }

    [Fact]
    public async Task ANodeTokenDoesNotWorkOnTheWebCollectorRoutesAndTheAppControllerNeverReadsTheSharedToken()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Collector:ChinaServiceToken"] = WebToken }).Build();
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer " + NodeToken;
        var web = new CollectorController(StudioFakes.Unused<IVisibilityCollectorStore>(), configuration,
            new MySqlDatabase("Server=127.0.0.1;Port=1;Database=not_used;ConnectionTimeout=1")) { ControllerContext = new() { HttpContext = context } };
        AssertError((await web.Lease(new CollectorLeaseRequest(Guid.NewGuid().ToString(), "mac-1", "cn", ["doubao"]), default)).Result, 401, "UNAUTHORIZED");
        AssertError((await web.Heartbeat(new CollectorHeartbeatRequest(Guid.NewGuid(), Guid.NewGuid(), "mac-1", 1), default)).Result, 401, "UNAUTHORIZED");
        // The App controller depends on the node store only: there is no configuration it could read a shared token from.
        Assert.Equal([typeof(IAppCollectorPoolStore)], typeof(AppCollectorController).GetConstructors().Single().GetParameters().Select(p => p.ParameterType));
    }

    [Fact]
    public async Task LeaseHeartbeatAndReportPassTheTokensNodeAndValidatedValuesToTheStore()
    {
        var f = new Fixture();
        AssertError((await f.Controller.Lease(new AppLeaseRequest(f.Node, "not-a-uuid"), default)).Result, 400, "INVALID_BINDING");
        AssertError((await f.Controller.Lease(new AppLeaseRequest(f.Node, Guid.Empty.ToString()), default)).Result, 400, "INVALID_BINDING");
        AssertError((await f.Controller.Heartbeat(new AppHeartbeatRequest(f.Node, "x", 1), default)).Result, 400, "INVALID_LEASE");
        AssertError((await f.Controller.Heartbeat(new AppHeartbeatRequest(f.Node, Id.ToString(), 0), default)).Result, 400, "INVALID_LEASE");
        Assert.DoesNotContain(f.Calls, call => call.Method != nameof(IAppCollectorPoolStore.AuthenticateNodeAsync));

        AssertError((await f.Controller.Lease(new AppLeaseRequest(f.Node, Id.ToString()), default)).Result, 409, "DISPATCH_PROTOCOL_REQUIRED");
        var request = new AppLeaseRequest(f.Node, Id.ToString(), 300, "device-v1", true);
        var offline = Assert.IsType<OkObjectResult>((await f.Controller.Lease(request with { DeviceOnline = false }, default)).Result);
        Assert.Equal(AppLeaseEmptyReason.DeviceOffline, Assert.IsType<AppLeaseResponse>(offline.Value).Reason);
        Assert.DoesNotContain(f.Calls, call => call.Method != nameof(IAppCollectorPoolStore.AuthenticateNodeAsync));
        var empty = Assert.IsType<OkObjectResult>((await f.Controller.Lease(request, default)).Result);
        Assert.Equal(new AppLeaseCommand("mac-1", Id, 300), f.Last<AppLeaseCommand>());
        Assert.Equal("""{"task":null,"reason":"no_task","retryAfterSeconds":60}""", JsonSerializer.Serialize(empty.Value, Json));
        Assert.Equal(60, new AppLeaseRequest(f.Node, Id.ToString()).LeaseSeconds);

        f.Leased = new AppLeasedTask(Id, Id, Id, Id, Id, "doubao", "doubao.consumer_app", Id, "问题", "cn", "zh-CN", "consumer_app", 1,
            At.AddDays(7), 2, At, Id);
        var leased = JsonSerializer.SerializeToNode(Assert.IsType<OkObjectResult>((await f.Controller.Lease(
            request, default)).Result).Value, Json)!;
        // Times are ISO 8601 UTC with a trailing Z; the question id is the prompt id.
        Assert.Equal(("2026-11-24T01:11:00.123Z", "2026-11-17T01:11:00.123Z", Id.ToString(), 2),
            ((string)leased["task"]!["deadlineAt"]!, (string)leased["task"]!["leaseExpiresAt"]!, (string)leased["task"]!["questionVersionId"]!,
                (int)leased["task"]!["leaseVersion"]!));
        Assert.Null(leased["reason"]);

        var heartbeat = Assert.IsType<OkObjectResult>((await f.Controller.Heartbeat(new AppHeartbeatRequest(f.Node, Id.ToString(), 2, 120), default)).Result);
        Assert.Equal(new AppHeartbeatCommand("mac-1", Id, 2, 120), f.Last<AppHeartbeatCommand>());
        Assert.Equal("""{"leaseExpiresAt":"2026-11-17T01:11:00.123Z"}""", JsonSerializer.Serialize(heartbeat.Value, Json));

        var binding = new AppBindingReport("AI_Apps_4KB", "doubao", "doubao-a", "com.larus.nova", null, null, null, "ok");
        var report = Assert.IsType<OkObjectResult>((await f.Controller.ReportBindings(new AppBindingReportRequest(f.Node, "rev-1", [binding]), default)).Result);
        var command = f.Last<AppBindingReportCommand>();
        Assert.Equal(("mac-1", "rev-1", binding), (command.NodeId, command.CollectorBuildRevision, Assert.Single(command.Bindings)));
        Assert.Equal("""{"bindings":[],"rejected":[],"dispatchProtocol":"device-v1"}""", JsonSerializer.Serialize(report.Value, Json));

        var sent = Assert.IsType<OkObjectResult>((await f.Controller.Sent(new(f.Node, Id.ToString(), Id.ToString(), 2, At), default)).Result);
        Assert.Equal(new AppSentCommand("mac-1", Id, Id, 2, At), f.Last<AppSentCommand>());
        Assert.Equal("""{"disposition":"accepted"}""", JsonSerializer.Serialize(sent.Value, Json));

        // Store errors keep their status and code: a foreign binding, a stale lease, a database without migration 049.
        foreach (var (code, status) in new[] { ("APP_BINDING_FORBIDDEN", 403), ("STALE_LEASE", 409), ("APP_CHANNEL_SCHEMA_NOT_MIGRATED", 503) })
        {
            f.StoreError = new CollectorStoreException(code, "m", status);
            AssertError((await f.Controller.Lease(request, default)).Result, status, code);
            AssertError((await f.Controller.Heartbeat(new AppHeartbeatRequest(f.Node, Id.ToString(), 1), default)).Result, status, code);
        }
    }

    [Fact]
    public async Task FailureValidatesTheCodeThePhaseAndTheTaskPairing()
    {
        var f = new Fixture();
        var task = new CollectorReportedTaskRequest(Id.ToString(), 2);
        Task<ActionResult<AppFailureResponse>> Fail(string? code, string? phase, CollectorReportedTaskRequest? reported, string? binding = null)
            => f.Controller.Failure(new AppFailureRequest(f.Node, binding ?? Id.ToString(), code, phase, reported), default);

        AssertError((await Fail("login_required", "probe", null, "x")).Result, 400, "INVALID_BINDING");
        // Web-only codes, unknown codes, wrong case and padding are refused; so is a stop reason that is not a failure code.
        foreach (var code in new[] { "browser_failed", "page_contract_changed", "region_restricted", "LOGIN_REQUIRED", " login_required", "disabled_by_operator", "", null })
            AssertError((await Fail(code, "pre_send", task)).Result, 400, "INVALID_FAILURE");
        AssertError((await Fail("login_required", "during_send", task)).Result, 400, "INVALID_FAILURE");
        AssertError((await Fail("login_required", null, task)).Result, 400, "INVALID_FAILURE");
        AssertError((await Fail("login_required", "probe", task)).Result, 400, "INVALID_FAILURE");
        AssertError((await Fail("login_required", "pre_send", null)).Result, 400, "INVALID_FAILURE");
        AssertError((await Fail("login_required", "post_send", null)).Result, 400, "INVALID_FAILURE");
        AssertError((await Fail("login_required", "pre_send", task with { LeaseVersion = 0 })).Result, 400, "INVALID_FAILURE");
        AssertError((await Fail("login_required", "pre_send", task with { TaskId = "x" })).Result, 400, "INVALID_FAILURE");
        Assert.DoesNotContain(f.Calls, call => call.Method != nameof(IAppCollectorPoolStore.AuthenticateNodeAsync));

        foreach (var code in AppContractNames.All<AppFailureCode>())
        {
            Assert.IsType<OkObjectResult>((await Fail(code, "post_send", task)).Result);
            var command = f.Last<AppFailureCommand>();
            Assert.Equal((code, "mac-1", Id, CollectorPhase.PostSend, new CollectorReportedTask(Id, 2)),
                (AppContractNames.ToStorage(command.FailureCode), command.NodeId, command.BindingId, command.Phase, command.Task));
        }
        var probe = Assert.IsType<OkObjectResult>((await Fail("device_offline", "probe", null)).Result);
        Assert.Null(f.Last<AppFailureCommand>().Task);
        Assert.Equal("""{"bindingStatus":"active","stopReason":null,"cooldownUntil":null,"taskDisposition":null,"platformPaused":false,"platformUnavailable":false}""",
            JsonSerializer.Serialize(probe.Value, Json));
        // The store's cooldown end is written as ISO 8601 UTC with milliseconds and a trailing Z.
        f.Cooldown = At;
        var cooling = Assert.IsType<OkObjectResult>((await Fail("rate_limited", "post_send", task)).Result);
        Assert.Equal("""{"bindingStatus":"active","stopReason":null,"cooldownUntil":"2026-11-17T01:11:00.123Z","taskDisposition":null,"platformPaused":false,"platformUnavailable":false}""",
            JsonSerializer.Serialize(cooling.Value, Json));
    }

    [Theory]
    [InlineData(0.0, 30)]
    [InlineData(29.2, 30)]
    [InlineData(45.01, 46)]
    [InlineData(299.5, 300)]
    [InlineData(4000.0, 600)]
    [InlineData(null, 600)]
    [InlineData(double.NaN, 600)]
    public void RetryAfterIsCeiledAndClampedTo30To600(double? seconds, int expected)
    {
        Assert.Equal(expected, AppPoolRules.ClampRetry(seconds));
        Assert.Equal((30, 600, 60), (AppPoolRules.RetryMinSeconds, AppPoolRules.RetryMaxSeconds, AppPoolRules.NoTaskRetrySeconds));
    }

    [Fact]
    public void StopReasonsThatAnOkReportClearsAndTheOrderOfStopReasons()
    {
        Assert.Equal([AppBindingStopReason.DeviceOffline, AppBindingStopReason.ContractChanged, AppBindingStopReason.ContractUnverified, AppBindingStopReason.VersionMismatch],
            Enum.GetValues<AppBindingStopReason>().Where(AppPoolRules.ClearsOnOkReport));
        // Not stopped: the reported reason. Operator stop: kept. A stop that needs an operator is not replaced by one an ok report clears.
        Assert.Equal(AppBindingStopReason.DeviceOffline, AppPoolRules.NextStopReason(null, AppBindingStopReason.DeviceOffline));
        Assert.Equal(AppBindingStopReason.DisabledByOperator, AppPoolRules.NextStopReason(AppBindingStopReason.DisabledByOperator, AppBindingStopReason.LoginRequired));
        Assert.Equal(AppBindingStopReason.LoginRequired, AppPoolRules.NextStopReason(AppBindingStopReason.LoginRequired, AppBindingStopReason.DeviceOffline));
        Assert.Equal(AppBindingStopReason.LoginRequired, AppPoolRules.NextStopReason(AppBindingStopReason.DeviceOffline, AppBindingStopReason.LoginRequired));
        Assert.Equal(AppBindingStopReason.ChallengeRequired, AppPoolRules.NextStopReason(AppBindingStopReason.LoginRequired, AppBindingStopReason.ChallengeRequired));
        Assert.Equal(AppBindingStopReason.VersionMismatch, AppPoolRules.NextStopReason(AppBindingStopReason.DeviceOffline, AppBindingStopReason.VersionMismatch));
        Assert.Equal([AppFailureCode.LoginRequired, AppFailureCode.ChallengeRequired],
            Enum.GetValues<AppFailureCode>().Where(AppPoolRules.CountsTowardVerificationBreaker));
        Assert.Equal((60, 2, 1800, 15), (AppPoolRules.VerificationBreakerWindowMinutes, AppPoolRules.VerificationBreakerBindingThreshold,
            AppPoolRules.CountedFailureBackoffSeconds, AppPoolRules.BindingOfflineMinutes));
    }

    [Fact]
    public void TheStoredTokenIsTheLowercaseHexSha256()
        => Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", AppCollectorPoolStore.HashToken("hello"));

    internal static void AssertError(IActionResult? result, int status, string code)
    {
        var response = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, JsonSerializer.SerializeToElement(response.Value).GetProperty("error").GetProperty("code").GetString());
    }

    internal sealed class Fixture
    {
        private readonly DefaultHttpContext context = new();

        internal Fixture()
        {
            var store = DispatchProxy.Create<IAppCollectorPoolStore, Proxy>();
            ((Proxy)(object)store).Handler = (method, args) =>
            {
                Calls.Add((method.Name, args));
                if (method.Name == nameof(IAppCollectorPoolStore.AuthenticateNodeAsync))
                    return Task.FromResult((string?)args[0] == NodeToken ? new AppNodeIdentity("mac-1", Status) : null);
                return method.Name switch
                {
                    nameof(IAppCollectorPoolStore.LeaseAsync) => StoreError is null
                        ? Task.FromResult(Leased is null ? new AppLeaseResult(null, AppLeaseEmptyReason.NoTask, 60) : new AppLeaseResult(Leased, null, null))
                        : Task.FromException<AppLeaseResult>(StoreError),
                    nameof(IAppCollectorPoolStore.HeartbeatAsync) => StoreError is null
                        ? Task.FromResult(At) : Task.FromException<DateTimeOffset>(StoreError),
                    nameof(IAppCollectorPoolStore.ReportBindingsAsync) => Task.FromResult(new AppBindingReportResult([], [])),
                    nameof(IAppCollectorPoolStore.ReportSentAsync) => Task.FromResult("accepted"),
                    nameof(IAppCollectorPoolStore.ReportFailureAsync) => Task.FromResult(
                        new AppFailureResult(AppBindingStatus.Active, null, null, false, false, Cooldown)),
                    nameof(IAppCollectorPoolStore.SubmitResultAsync) => StoreError is null
                        ? Task.FromResult(Submitted) : Task.FromException<CollectorResultResponse>(StoreError),
                    _ => throw new InvalidOperationException(method.Name),
                };
            };
            Authorization = "Bearer " + NodeToken;
            Controller = new AppCollectorController(store) { ControllerContext = new() { HttpContext = context } };
        }

        internal AppCollectorController Controller { get; }
        internal List<(string Method, object?[] Args)> Calls { get; } = [];
        internal string? Node { get; set; } = "mac-1";
        internal AppNodeStatus Status { get; set; } = AppNodeStatus.Active;
        internal AppLeasedTask? Leased { get; set; }
        internal CollectorStoreException? StoreError { get; set; }
        internal DateTimeOffset? Cooldown { get; set; }
        internal CollectorResultResponse Submitted { get; set; } = new(CollectorResultDisposition.Accepted, Id);
        internal string Authorization { set => context.Request.Headers.Authorization = value; }

        internal T Last<T>() => Calls.Select(call => call.Args[0]).OfType<T>().Last();
    }

    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args ?? []);
    }
}
