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
/// 账号池接口契约（不连数据库）：请求校验与 refine 约束、鉴权、以及与参考 Collector zod schema 对齐的 JSON 形状
/// （stopReason 省略而非 null、taskDisposition 必须存在、枚举为小写下划线、legacy 租约的 sessionProfileId 为 null）。
/// </summary>
public sealed class CollectorSessionPoolContractTests
{
    private const string ServiceToken = "collector-pool-contract-token";

    // 与 Program.cs 的 AddJsonOptions 一致：Web 默认值 + 字符串枚举。
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public void ResponsesSerializeWithReferenceFieldNamesAndOptionality()
    {
        Assert.Equal("""{"eligible":false,"retryAfterSeconds":120}""",
            JsonSerializer.Serialize(new CollectorEligibilityResult(false, 120), Json));
        Assert.Equal("""{"eligible":false,"retryAfterSeconds":120,"stopReason":"needs_verification"}""",
            JsonSerializer.Serialize(new CollectorEligibilityResult(false, 120, CollectorEligibilityStopReason.NeedsVerification), Json));
        Assert.Equal("""{"sessionProfileStatus":"needs_verification","taskDisposition":null,"platformPaused":true}""",
            JsonSerializer.Serialize(new CollectorFailureResult(CollectorSessionProfileStatus.NeedsVerification, null, true), Json));
        Assert.Equal("""{"sessionProfileStatus":"cooling","taskDisposition":"requeued","platformPaused":false}""",
            JsonSerializer.Serialize(new CollectorFailureResult(CollectorSessionProfileStatus.Cooling, CollectorTaskDisposition.Requeued, false), Json));
        Assert.Equal("""{"sessionProfileStatus":"active"}""",
            JsonSerializer.Serialize(new CollectorSessionEventResult(CollectorSessionProfileStatus.Active), Json));
        Assert.Equal(["active", "cooling", "needs_verification", "expired", "disabled"],
            Enum.GetValues<CollectorSessionProfileStatus>().Select(value => JsonSerializer.Serialize(value, Json).Trim('"')));
        Assert.Equal(["requeued", "failed", "stale"], Enum.GetValues<CollectorTaskDisposition>().Select(value => JsonSerializer.Serialize(value, Json).Trim('"')));
    }

    [Fact]
    public async Task LeaseWithProfileForcesCapacityOneAndMapsLeaseProfile()
    {
        var f = new Fixture();
        var profile = Guid.NewGuid();
        var result = await f.Controller.Lease(new CollectorLeaseRequest(f.Tenant.ToString(), "node-a", "cn", ["doubao"], 5, 120, profile.ToString()), default);
        var command = Assert.IsType<CollectorLeaseCommand>(Assert.Single(f.Calls).Args[0]);
        Assert.Equal((1, (Guid?)profile, 120), (command.Capacity, command.SessionProfileId, command.LeaseSeconds));
        var body = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result.Result).Value, Json);
        Assert.Equal(profile.ToString(), body.GetProperty("tasks")[0].GetProperty("sessionProfileId").GetString());

        f.Calls.Clear();
        f.LeaseProfile = null;
        var legacy = await f.Controller.Lease(new CollectorLeaseRequest(f.Tenant.ToString(), "node-a", "cn", ["doubao"], 5, 120), default);
        Assert.Equal((5, (Guid?)null), (Assert.IsType<CollectorLeaseCommand>(Assert.Single(f.Calls).Args[0]).Capacity,
            ((CollectorLeaseCommand)f.Calls[0].Args[0]!).SessionProfileId));
        var legacyTask = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(legacy.Result).Value, Json).GetProperty("tasks")[0];
        Assert.Equal(JsonValueKind.Null, legacyTask.GetProperty("sessionProfileId").ValueKind);

        AssertError((await f.Controller.Lease(new CollectorLeaseRequest(f.Tenant.ToString(), "node-a", "cn", ["doubao"], 1, 60, "not-a-uuid"), default)).Result,
            400, "INVALID_SESSION_PROFILE");
        // 参考合同先校验 capacity 1–50 再强制为 1；越界值返回 400，不被静默改写。
        AssertError((await f.Controller.Lease(new CollectorLeaseRequest(f.Tenant.ToString(), "node-a", "cn", ["doubao"], 51, 60, profile.ToString()), default)).Result,
            400, "INVALID_LEASE");
    }

    [Fact]
    public async Task FailureValidatesPhaseTaskPairingAndEnums()
    {
        var f = new Fixture();
        var task = new CollectorReportedTaskRequest(Guid.NewGuid().ToString(), 2);
        AssertError((await f.Failure("challenge_required", "probe", task)).Result, 400, "INVALID_FAILURE");
        AssertError((await f.Failure("challenge_required", "pre_send", null)).Result, 400, "INVALID_FAILURE");
        AssertError((await f.Failure("stale_lease", "pre_send", task)).Result, 400, "INVALID_FAILURE");
        AssertError((await f.Failure("challenge_required", "during_send", task)).Result, 400, "INVALID_FAILURE");
        AssertError((await f.Failure("challenge_required", "pre_send", task with { LeaseVersion = 0 })).Result, 400, "INVALID_FAILURE");
        Assert.Empty(f.Calls);

        Assert.IsType<OkObjectResult>((await f.Failure("rate_limited", "post_send", task)).Result);
        var command = Assert.IsType<CollectorFailureCommand>(Assert.Single(f.Calls).Args[0]);
        Assert.Equal((CollectorFailureCode.RateLimited, CollectorPhase.PostSend, 2), (command.FailureCode, command.Phase, command.Task!.LeaseVersion));
        Assert.IsType<OkObjectResult>((await f.Failure("login_required", "probe", null)).Result);
    }

    [Fact]
    public async Task SessionEventsRequireVerifiedOnProbeWithoutTaskAndChallengesWithTask()
    {
        var f = new Fixture();
        var task = new CollectorReportedTaskRequest(Guid.NewGuid().ToString(), 1);
        AssertError((await f.Event("verified", "probe", task)).Result, 400, "INVALID_SESSION_EVENT");
        AssertError((await f.Event("verified", "pre_send", null)).Result, 400, "INVALID_SESSION_EVENT");
        AssertError((await f.Event("challenge_wait_started", "probe", null)).Result, 400, "INVALID_SESSION_EVENT");
        AssertError((await f.Event("challenge_cleared", "post_send", null)).Result, 400, "INVALID_SESSION_EVENT");
        AssertError((await f.Event("challenge_timeout", "pre_send", task)).Result, 400, "INVALID_SESSION_EVENT");
        Assert.Empty(f.Calls);
        Assert.IsType<OkObjectResult>((await f.Event("verified", "probe", null)).Result);
        Assert.IsType<OkObjectResult>((await f.Event("challenge_wait_started", "pre_send", task)).Result);
        Assert.Equal(2, f.Calls.Count);
    }

    [Fact]
    public async Task EligibilityValidatesPlatformProfileNodeAndAuthentication()
    {
        var f = new Fixture();
        var profile = Guid.NewGuid().ToString();
        AssertError((await f.Eligibility(new(f.Tenant.ToString(), "node-a", "cn", profile, "bing"))).Result, 400, "INVALID_PLATFORM");
        AssertError((await f.Eligibility(new(f.Tenant.ToString(), "node-a", "cn", "x", "doubao"))).Result, 400, "INVALID_SESSION_PROFILE");
        AssertError((await f.Eligibility(new(f.Tenant.ToString(), new string('n', 129), "cn", profile, "doubao"))).Result, 400, "INVALID_REQUEST");
        AssertError((await f.Eligibility(new(f.Tenant.ToString(), "node-a", "overseas", profile, "doubao"))).Result, 403, "COLLECTOR_REGION_FORBIDDEN");
        AssertError((await f.Eligibility(new(Guid.NewGuid().ToString(), "node-a", "cn", profile, "doubao"))).Result, 403, "TENANT_NOT_ALLOWED");
        f.Controller.HttpContext.Request.Headers.Authorization = "Bearer wrong-token";
        AssertError((await f.Eligibility(new(f.Tenant.ToString(), "node-a", "cn", profile, "doubao"))).Result, 401, "UNAUTHORIZED");
        Assert.Empty(f.Calls);

        f.Controller.HttpContext.Request.Headers.Authorization = $"Bearer {ServiceToken}";
        var ok = Assert.IsType<OkObjectResult>((await f.Eligibility(new(f.Tenant.ToString(), "node-a", "cn", profile, "doubao"))).Result);
        Assert.Equal("""{"eligible":true,"retryAfterSeconds":30}""", JsonSerializer.Serialize(ok.Value, Json));
        f.PoolError = new CollectorStoreException("SESSION_PROFILE_FORBIDDEN", "forbidden", 403);
        AssertError((await f.Eligibility(new(f.Tenant.ToString(), "node-a", "cn", profile, "doubao"))).Result, 403, "SESSION_PROFILE_FORBIDDEN");
    }

    [Fact]
    public async Task AdminEndpointsRequireAuthenticatedSession()
    {
        var pool = DispatchProxy.Create<ICollectorSessionPoolStore, Proxy>();
        ((Proxy)(object)pool).Handler = (method, _) => throw new InvalidOperationException($"{method.Name} must not run without a session");
        var research = DispatchProxy.Create<IResearchStore, Proxy>();
        ((Proxy)(object)research).Handler = (method, _) => throw new InvalidOperationException($"{method.Name} must not run without a session");
        var controller = new CollectorPoolAdminController(pool, research) { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        var result = await controller.RegisterProfile(new RegisterCollectorSessionProfileRequest(Guid.NewGuid(), "doubao", "cn", "doubao-a"), default);
        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result.Result);
        Assert.Equal("AUTHENTICATION_REQUIRED", JsonSerializer.SerializeToElement(unauthorized.Value).GetProperty("error").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(29.2, 30)]
    [InlineData(45.01, 46)]
    [InlineData(4000.0, 120)]
    [InlineData(null, 120)]
    public void RetryAfterIsCeiledAndClampedToPollingBounds(double? seconds, int expected)
        => Assert.Equal(expected, CollectorSessionDispatch.ClampRetry(seconds));

    private static void AssertError(IActionResult? result, int status, string code)
    {
        var response = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, JsonSerializer.SerializeToElement(response.Value).GetProperty("error").GetProperty("code").GetString());
    }

    private sealed class Fixture
    {
        internal Guid Tenant { get; } = Guid.NewGuid();
        internal List<(string Method, object?[] Args)> Calls { get; } = [];
        internal Guid? LeaseProfile { get; set; } = Guid.Empty;
        internal CollectorStoreException? PoolError { get; set; }
        internal CollectorController Controller { get; }
        private readonly ICollectorSessionPoolStore pool;

        internal Fixture()
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Collector:ChinaServiceToken"] = ServiceToken,
                ["Collector:ChinaTenantIds:0"] = Tenant.ToString(),
            }).Build();
            var context = new DefaultHttpContext();
            context.Request.Headers.Authorization = $"Bearer {ServiceToken}";
            var store = DispatchProxy.Create<IVisibilityCollectorStore, Proxy>();
            ((Proxy)(object)store).Handler = (method, args) =>
            {
                Calls.Add((method.Name, args));
                var command = Assert.IsType<CollectorLeaseCommand>(args[0]);
                IReadOnlyList<LeasedCollectorTask> tasks =
                [
                    new(Guid.NewGuid(), Tenant, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "doubao", "doubao.consumer_web", Guid.NewGuid(), 1,
                        Guid.NewGuid(), "问题", CollectorRegion.Cn, "zh-CN", "consumer_web", 1, DateTimeOffset.UtcNow,
                        LeaseProfile == Guid.Empty ? command.SessionProfileId : LeaseProfile),
                ];
                return Task.FromResult(tasks);
            };
            pool = DispatchProxy.Create<ICollectorSessionPoolStore, Proxy>();
            ((Proxy)(object)pool).Handler = (method, args) =>
            {
                if (PoolError is not null) return Task.FromException<CollectorEligibilityResult>(PoolError);
                Calls.Add((method.Name, args));
                return method.Name switch
                {
                    nameof(ICollectorSessionPoolStore.CheckEligibilityAsync) => Task.FromResult(new CollectorEligibilityResult(true, 30)),
                    nameof(ICollectorSessionPoolStore.ReportFailureAsync) => Task.FromResult(
                        new CollectorFailureResult(CollectorSessionProfileStatus.Active, null, false)),
                    _ => (object)Task.FromResult(new CollectorSessionEventResult(CollectorSessionProfileStatus.Active)),
                };
            };
            Controller = new CollectorController(store, configuration,
                new MySqlDatabase("Server=127.0.0.1;Port=1;Database=not_used;ConnectionTimeout=1"))
            { ControllerContext = new() { HttpContext = context } };
        }

        internal Task<ActionResult<CollectorEligibilityResult>> Eligibility(CollectorEligibilityRequest request)
            => Controller.Eligibility(request, pool, default);

        internal Task<ActionResult<CollectorFailureResult>> Failure(string code, string phase, CollectorReportedTaskRequest? task)
            => Controller.Failure(new CollectorFailureRequest(Tenant.ToString(), "node-a", "cn", Guid.NewGuid().ToString(), code, phase, task), pool, default);

        internal Task<ActionResult<CollectorSessionEventResult>> Event(string kind, string phase, CollectorReportedTaskRequest? task)
            => Controller.SessionEvent(new CollectorSessionEventRequest(Tenant.ToString(), "node-a", "cn", Guid.NewGuid().ToString(), kind, phase, task), pool, default);
    }

    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args ?? []);
    }
}
