using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jogeoai.Api;
using Jogeoai.Api.Controllers;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Tests.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;

namespace Jogeoai.Tests;

/// <summary>
/// App 账号池运营接口（/api/v1/admin/collector-app，implement 8.3 / 8.5）不连数据库的部分：每个路由对未登录 401、对非系统管理员 403，
/// 而且在授权失败时不调用任何 Store；节点令牌只出现在登记与轮换的响应里；请求校验在调用 Store 之前；Swagger 的中文说明。
/// </summary>
public sealed class AppAdminApiTests
{
    private const string Token = "app-node-token-c1a9f0e2b7d34d5e9a0f1b2c3d4e5f60";

    /// <summary>调用控制器的每一个 action（不管参数是否合法），返回每个 action 的名字和结果。</summary>
    private static async Task<List<(string Action, IActionResult Result)>> CallEveryActionAsync(AppCollectorAdminController c)
    {
        var binding = Guid.NewGuid();
        var results = new List<(string, IActionResult)>
        {
            (nameof(c.ListNodes), (await c.ListNodes(default)).Result!),
            (nameof(c.RegisterNode), (await c.RegisterNode(new("node-a"), default)).Result!),
            (nameof(c.RotateNodeToken), (await c.RotateNodeToken("node-a", default)).Result!),
            (nameof(c.DisableNode), (await c.DisableNode("node-a", default)).Result!),
            (nameof(c.ResumeNode), (await c.ResumeNode("node-a", default)).Result!),
            (nameof(c.SetNodeMaxDevices), (await c.SetNodeMaxDevices("node-a", new(6), default)).Result!),
            (nameof(c.ListBindings), (await c.ListBindings(null, null, null, default)).Result!),
            (nameof(c.DisableBinding), (await c.DisableBinding(binding, default)).Result!),
            (nameof(c.ResumeBinding), (await c.ResumeBinding(binding, default)).Result!),
            (nameof(c.SetBindingLimits), (await c.SetBindingLimits(binding, new(300), default)).Result!),
            (nameof(c.ListPlatforms), (await c.ListPlatforms(default)).Result!),
            (nameof(c.PausePlatform), (await c.PausePlatform("doubao", default)).Result!),
            (nameof(c.ResumePlatform), (await c.ResumePlatform("doubao", default)).Result!),
            (nameof(c.SetPlatformLimits), (await c.SetPlatformLimits("doubao", new(30), default)).Result!),
            (nameof(c.RestorePlatformEntry), (await c.RestorePlatformEntry("doubao", default)).Result!),
            (nameof(c.Capacity), (await c.Capacity(default)).Result!),
            (nameof(c.TaskAttempts), (await c.TaskAttempts(Guid.NewGuid(), default)).Result!),
            (nameof(c.ExcludePrompt), (await c.ExcludePrompt(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "原因"), default)).Result!),
            (nameof(c.RevokePromptExclusion), (await c.RevokePromptExclusion(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), default)).Result!),
            (nameof(c.Events), (await c.Events(null, null, null, null, null, null, null, null, default)).Result!),
            (nameof(c.Alerts), (await c.Alerts(default)).Result!),
            (nameof(c.DailyReports), (await c.DailyReports(null, null, default)).Result!),
            (nameof(c.DailyReport), (await c.DailyReport("2026-11-17", default)).Result!),
        };
        return results;
    }

    private static string[] RouteActions()
        => typeof(AppCollectorAdminController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttributes<HttpMethodAttribute>().Any()).Select(method => method.Name).Order().ToArray();

    [Theory]
    [InlineData(false, 401, "AUTHENTICATION_REQUIRED")]
    [InlineData(true, 403, "ADMIN_REQUIRED")]
    public async Task EveryRouteRejectsAnonymousAndNonAdministratorsBeforeAnyStoreCall(bool authenticated, int status, string code)
    {
        var f = new Fixture(authenticated, admin: false);
        var results = await CallEveryActionAsync(f.Controller);
        // Every route of the controller is in the list above: a new route without the check cannot slip past this test.
        Assert.Equal(RouteActions(), results.Select(result => result.Action).Order().ToArray());
        foreach (var (action, result) in results) AssertError(result, status, code, action);
        Assert.Empty(f.AllStoreCalls());
    }

    [Fact]
    public void TheControllerIsNotAnonymousAndUsesTheAdminRoutePrefix()
    {
        var type = typeof(AppCollectorAdminController);
        Assert.Null(type.GetCustomAttribute<Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute>());
        Assert.DoesNotContain(type.GetMethods(), method => method.GetCustomAttribute<Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute>() is not null);
        Assert.Equal("api/v1/admin/collector-app", type.GetCustomAttribute<RouteAttribute>()!.Template);
    }

    [Fact]
    public async Task TheNodeTokenAppearsOnlyInTheRegisterAndRotateResponses()
    {
        var f = new Fixture();
        var registered = Assert.IsType<ObjectResult>((await f.Controller.RegisterNode(new("node-a"), default)).Result);
        Assert.Equal(201, registered.StatusCode);
        Assert.Contains(Token, Json(registered.Value));
        var rotated = Assert.IsType<OkObjectResult>((await f.Controller.RotateNodeToken("node-a", default)).Result);
        Assert.Contains(Token, Json(rotated.Value));

        // Every other response (with the fakes returning data) carries no token.
        f.Admin.Handlers[nameof(IAppCollectorAdminStore.RecordPromptExclusionEventAsync)] = _ => Task.CompletedTask;
        foreach (var (action, result) in await CallEveryActionAsync(f.Controller))
        {
            if (action is nameof(f.Controller.RegisterNode) or nameof(f.Controller.RotateNodeToken)) continue;
            Assert.True(result is ObjectResult { StatusCode: >= 200 and < 300 } or OkObjectResult, $"{action} 没有成功：{Json((result as ObjectResult)?.Value)}");
            Assert.DoesNotContain(Token, Json(((ObjectResult)result).Value));
        }
        Assert.DoesNotContain(f.Logs, line => line.Contains(Token, StringComparison.Ordinal));

        // Structurally: among all documented response types, only the credential has a token member.
        var types = typeof(AppCollectorAdminController).GetMethods()
            .SelectMany(method => method.GetCustomAttributes<ProducesResponseTypeAttribute>()).Select(attribute => attribute.Type)
            .Where(type => type != typeof(void)).Distinct().ToArray();
        var withToken = types.Where(type => MembersOf(type).Any(name => name.Contains("token", StringComparison.OrdinalIgnoreCase))).ToArray();
        Assert.Equal([typeof(AppNodeCredential)], withToken);
    }

    [Fact]
    public async Task InvalidInputIsRejectedBeforeAnyStoreCall()
    {
        var f = new Fixture();
        AssertError((await f.Controller.ListBindings(null, null, "cooling", default)).Result!, 400, "INVALID_FILTER");
        AssertError((await f.Controller.SetBindingLimits(Guid.NewGuid(), new(null), default)).Result!, 400, "INVALID_APP_LIMITS");
        AssertError((await f.Controller.SetPlatformLimits("doubao", new(null, 5), default)).Result!, 400, "INVALID_APP_LIMITS");
        AssertError((await f.Controller.DailyReport("2026/11/17", default)).Result!, 400, "INVALID_FILTER");
        AssertError((await f.Controller.ExcludePrompt(null!, default)).Result!, 400, "INVALID_REQUEST");
        // The device cap: missing, null, 0, 65 and negative values never reach a store.
        AssertError((await f.Controller.SetNodeMaxDevices("node-a", null!, default)).Result!, 400, "INVALID_APP_LIMITS");
        foreach (var value in new int?[] { null, 0, 65, -1, int.MaxValue })
            AssertError((await f.Controller.SetNodeMaxDevices("node-a", new(value), default)).Result!, 400, "INVALID_APP_LIMITS");
        Assert.Empty(f.AllStoreCalls());
        // The store's own errors keep their shape.
        f.Reports.Handlers[nameof(IAppDailyReportStore.GetAsync)] = _ => Task.FromResult<AppDailyReportView?>(null);
        AssertError((await f.Controller.DailyReport("2026-11-17", default)).Result!, 404, "APP_DAILY_REPORT_NOT_FOUND");
        f.Admin.Handlers[nameof(IAppCollectorAdminStore.ListNodesAsync)] = _ => throw new CollectorStoreException("APP_CHANNEL_SCHEMA_NOT_MIGRATED", "x", 503);
        AssertError((await f.Controller.ListNodes(default)).Result!, 503, "APP_CHANNEL_SCHEMA_NOT_MIGRATED");
    }

    // RF2: the device cap is set per node by a system administrator. The store's refusals keep their shape: lowering the
    // cap below the devices already registered is a 409 and the node is not read back.
    [Fact]
    public async Task TheDeviceCapIsPassedToTheStoreAndItsConflictsKeepTheirShape()
    {
        var f = new Fixture();
        foreach (var value in new[] { 1, 6, 64 })
        {
            var change = Assert.IsType<AppNodeChange>(Assert.IsType<OkObjectResult>(
                (await f.Controller.SetNodeMaxDevices("node-a", new(value), default)).Result).Value);
            Assert.True(change.Changed);
            Assert.Equal("node-a", change.Node.NodeId);
            var call = f.Admin.Calls.Last(item => item.Method == nameof(IAppCollectorAdminStore.SetNodeMaxDevicesAsync));
            Assert.Equal(("node-a", value, (Guid?)f.UserId), ((string)call.Args[0]!, (int)call.Args[1]!, (Guid?)call.Args[2]));
        }
        // Only the two admin-store calls per request: the write, then the read of the node.
        Assert.Equal([nameof(IAppCollectorAdminStore.SetNodeMaxDevicesAsync), nameof(IAppCollectorAdminStore.GetNodeAsync)],
            f.AllStoreCalls().Distinct());
        var json = JsonSerializer.SerializeToNode(Assert.IsType<OkObjectResult>(
            (await f.Controller.SetNodeMaxDevices("node-a", new(6), default)).Result).Value, AppCycleRunApi)!;
        Assert.Equal((true, 6), ((bool)json["changed"]!, (int)json["node"]!["maxDevices"]!));

        var reads = f.Admin.Count(nameof(IAppCollectorAdminStore.GetNodeAsync));
        f.Admin.Handlers[nameof(IAppCollectorAdminStore.SetNodeMaxDevicesAsync)] = _
            => throw new CollectorStoreException("APP_NODE_MAX_DEVICES_BELOW_REGISTERED", "x", 409);
        AssertError((await f.Controller.SetNodeMaxDevices("node-a", new(1), default)).Result!, 409, "APP_NODE_MAX_DEVICES_BELOW_REGISTERED");
        Assert.Equal(reads, f.Admin.Count(nameof(IAppCollectorAdminStore.GetNodeAsync)));
        f.Admin.Handlers[nameof(IAppCollectorAdminStore.SetNodeMaxDevicesAsync)] = _ => throw new CollectorStoreException("APP_NODE_NOT_FOUND", "x", 404);
        AssertError((await f.Controller.SetNodeMaxDevices("missing", new(4), default)).Result!, 404, "APP_NODE_NOT_FOUND");
        f.Admin.Handlers[nameof(IAppCollectorAdminStore.SetNodeMaxDevicesAsync)] = _
            => throw new CollectorStoreException("APP_CHANNEL_SCHEMA_NOT_MIGRATED", "x", 503);
        AssertError((await f.Controller.SetNodeMaxDevices("node-a", new(4), default)).Result!, 503, "APP_CHANNEL_SCHEMA_NOT_MIGRATED");

        var route = typeof(AppCollectorAdminController).GetMethod(nameof(AppCollectorAdminController.SetNodeMaxDevices))!;
        Assert.Equal("nodes/{nodeId}/max-devices", Assert.Single(route.GetCustomAttributes<HttpPutAttribute>()).Template);
    }

    // PRD A10: the attempt history is read by task id; an unknown or non-App task is a 404 in the usual error shape, and the
    // response has no member that could carry the question or the answer.
    [Fact]
    public async Task TheAttemptHistoryIsReadByTaskIdAndCarriesNoQuestionOrAnswerText()
    {
        var f = new Fixture();
        var taskId = Guid.NewGuid();
        var view = Assert.IsType<AppTaskAttemptsView>(Assert.IsType<OkObjectResult>((await f.Controller.TaskAttempts(taskId, default)).Result).Value);
        Assert.Equal(taskId, Assert.Single(f.Admin.Calls, call => call.Method == nameof(IAppCollectorAdminStore.GetTaskAttemptsAsync)).Args[0]);
        Assert.Equal([1, 2], view.Attempts.Select(attempt => attempt.LeaseVersion));
        var json = JsonSerializer.SerializeToNode(view, AppCycleRunApi)!;
        Assert.Equal(("succeeded", "collected", null, 2), ((string)json["status"]!, (string)json["outcome"]!, (string?)json["notCollectedCode"],
            (int)json["leaseVersion"]!));
        // A failure before the send on the first device, then the accepted answer on the second one.
        var (first, second) = (json["attempts"]![0]!, json["attempts"]![1]!);
        Assert.Equal(("node-a", "AI_Apps_4KB", "doubao", "doubao-a", "not_sent", null, "contract_changed", false),
            ((string)first["nodeId"]!, (string)first["deviceId"]!, (string)first["platformCode"]!, (string)first["accountLabel"]!,
                (string)first["sendState"]!, (string?)first["sentAt"], (string)first["failureCode"]!, (bool)first["accepted"]!));
        Assert.Equal(("AI_Apps_4KB_2", "sent", null, null, true), ((string)second["deviceId"]!, (string)second["sendState"]!,
            (string?)second["failureCode"], (string?)second["failedAt"], (bool)second["accepted"]!));
        Assert.NotNull((string?)second["sentAt"]);
        Assert.DoesNotContain(MembersOf(typeof(AppTaskAttemptsView)), name => name.Contains("question", StringComparison.OrdinalIgnoreCase)
            || name.Contains("answer", StringComparison.OrdinalIgnoreCase) || name.Contains("prompt", StringComparison.OrdinalIgnoreCase)
            || name.Contains("text", StringComparison.OrdinalIgnoreCase));

        f.Admin.Handlers[nameof(IAppCollectorAdminStore.GetTaskAttemptsAsync)] = _ => throw new CollectorStoreException("APP_TASK_NOT_FOUND", "x", 404);
        AssertError((await f.Controller.TaskAttempts(taskId, default)).Result!, 404, "APP_TASK_NOT_FOUND");
        // The route takes a UUID only and is a read.
        var route = typeof(AppCollectorAdminController).GetMethod(nameof(AppCollectorAdminController.TaskAttempts))!;
        Assert.Equal("tasks/{taskId:guid}/attempts", Assert.Single(route.GetCustomAttributes<HttpGetAttribute>()).Template);
    }

    // No automatic breaker: a binding that keeps failing before the send is only listed. The streak makes the report an
    // alert and gets its own section; a report stored before this item existed reads back with an empty list.
    [Fact]
    public void ABindingFailingBeforeTheSendIsReportedAsAnAlertAndOlderReportsStillRead()
    {
        Assert.Equal(3, AppDailyReportRules.PreSendFailureStreak);
        var now = new DateTimeOffset(2026, 11, 18, 2, 30, 0, TimeSpan.Zero);
        var quiet = new AppAlertsView(now, false, [], [], [], [], [], []);
        Assert.Empty(quiet.PreSendFailures);
        Assert.False(AppDailyReportRules.HasAlerts([], [], [], [], [], []));
        Assert.Contains("七、连续发题前失败的绑定（最近 3 次尝试都在发题前失败；只提示，不自动停用）\n  无",
            AppDailyReportRules.Render(new DateOnly(2026, 11, 18), quiet).ReplaceLineEndings("\n"));

        var streak = new AppPreSendFailureAlert(Guid.NewGuid(), "node-a", "AI_Apps_4KB", "doubao", "doubao-a", "contract_changed", now.AddMinutes(-5));
        Assert.True(AppDailyReportRules.HasAlerts([], [], [], [], [], [streak]));
        var body = AppDailyReportRules.Render(new DateOnly(2026, 11, 18), quiet with { HasAlerts = true, PreSendFailures = [streak] });
        Assert.Contains("结论：有告警（连续发题前失败的绑定 1 条）。", body);
        Assert.Contains("豆包（doubao） node-a / AI_Apps_4KB / doubao-a：最近一次 contract_changed，2026-11-18T02:25:00Z", body);

        // The stored summary of an older report has no such member; the API shape always has the array.
        var stored = JsonSerializer.Serialize(quiet, AppCycleRunApi);
        Assert.Contains("\"preSendFailures\":[]", stored);
        var older = JsonSerializer.Deserialize<AppAlertsView>(stored.Replace(",\"preSendFailures\":[]", ""), AppCycleRunApi)!;
        Assert.Empty(older.PreSendFailures);
        var round = JsonSerializer.Deserialize<AppAlertsView>(JsonSerializer.Serialize(quiet with { PreSendFailures = [streak] }, AppCycleRunApi), AppCycleRunApi)!;
        Assert.Equal(streak, Assert.Single(round.PreSendFailures));
    }

    [Fact]
    public async Task AnExclusionStandsWhenItsAuditEventFailsAndTheLogHoldsNoReason()
    {
        var f = new Fixture();
        f.Admin.Handlers[nameof(IAppCollectorAdminStore.RecordPromptExclusionEventAsync)] = _ => throw new InvalidOperationException("db down");
        const string reason = "独特的排除原因-7f3a";
        var result = Assert.IsType<ObjectResult>((await f.Controller.ExcludePrompt(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), reason), default)).Result);
        Assert.Equal(201, result.StatusCode);
        var log = Assert.Single(f.Logs);
        Assert.DoesNotContain(reason, log);
        Assert.Equal(f.UserId, Assert.Single(f.Collection.Calls).Args[4]);
    }

    [Fact]
    public async Task AdminRoutesAreDocumentedInChineseInSwagger()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(AppCollectorAdminController).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production,
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddControllers().AddApplicationPart(typeof(AppCollectorAdminController).Assembly)
            .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(options =>
        {
            options.OperationFilter<AnalyticsOpenApiOperationFilter>();
            options.UseAllOfToExtendReferenceSchemas();
            options.SwaggerDoc("v1", new OpenApiInfo { Title = "Jogeoai API", Version = "v1" });
            foreach (var assembly in new[] { "Jogeoai.Api", "Jogeoai.Domain", "Jogeoai.Application", "Jogeoai.Providers.Abstractions" })
                options.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, assembly + ".xml"), includeControllerXmlComments: assembly == "Jogeoai.Api");
        });
        await using var app = builder.Build();
        app.UseSwagger();
        app.MapControllers();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using var swagger = JsonDocument.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"));
            var paths = swagger.RootElement.GetProperty("paths");
            const string root = "/api/v1/admin/collector-app";
            var expected = new[]
            {
                ("/nodes", "get"), ("/nodes", "post"), ("/nodes/{nodeId}/token", "post"), ("/nodes/{nodeId}/disable", "post"),
                ("/nodes/{nodeId}/resume", "post"), ("/nodes/{nodeId}/max-devices", "put"), ("/bindings", "get"), ("/bindings/{bindingId}/disable", "post"),
                ("/bindings/{bindingId}/resume", "post"), ("/bindings/{bindingId}/limits", "put"), ("/platforms", "get"),
                ("/platforms/{platformCode}/pause", "post"), ("/platforms/{platformCode}/resume", "post"),
                ("/platforms/{platformCode}/limits", "put"), ("/platforms/{platformCode}/entry/restore", "post"), ("/capacity", "get"),
                ("/tasks/{taskId}/attempts", "get"), ("/prompt-exclusions", "post"), ("/prompt-exclusions/revoke", "post"), ("/events", "get"), ("/alerts", "get"),
                ("/daily-reports", "get"), ("/daily-reports/{reportDate}", "get"),
            };
            Assert.Equal(RouteActions().Length, expected.Length);
            foreach (var (path, method) in expected)
            {
                var operation = paths.GetProperty(root + path).GetProperty(method);
                Assert.True(HasChinese(operation.GetProperty("summary").GetString()), $"{method} {path}");
                if (operation.TryGetProperty("parameters", out var parameters))
                    foreach (var parameter in parameters.EnumerateArray())
                        Assert.True(HasChinese(parameter.GetProperty("description").GetString()), $"{method} {path} {parameter.GetProperty("name")}");
            }
            var schemas = swagger.RootElement.GetProperty("components").GetProperty("schemas");
            foreach (var (schema, property) in new[]
            {
                ("AppNodeCredential", "token"), ("AppNodeView", "offline"), ("AppNodeView", "lastActivityAt"), ("AppNodeChange", "changed"),
                ("AppBindingAdminView", "stopReason"), ("AppBindingAdminView", "stoppedSeconds"), ("AppBindingAdminView", "dailyQuota"),
                ("AppPlatformStateView", "entryRestorable"), ("AppPlatformStateView", "unavailableReason"),
                ("AppPlatformCapacityQueueView", "queue"), ("AppPlatformQueueView", "waiting"), ("AppEventView", "actor"),
                ("AppAlertsView", "offlineNodes"), ("AppAlertsView", "hasAlerts"), ("AppStoppedBindingAlert", "stoppedSeconds"),
                ("AppNotCollectedCount", "reasonCode"), ("AppPlatformActivity", "leaseExpirations"), ("AppCapacityUsage", "demandPercent"),
                ("AppDailyReportView", "body"), ("AppDailyReportItem", "deliveryStatus"), ("AppBindingLimitsRequest", "dailyQuota"),
                ("AppPlatformLimitsRequest", "dailyCap"), ("AppPromptExclusionRequest", "reason"), ("RegisterAppNodeRequest", "nodeId"),
                ("AppTaskAttemptsView", "outcome"), ("AppTaskAttemptsView", "attempts"), ("AppTaskAttemptView", "sendState"),
                ("AppTaskAttemptView", "accepted"), ("AppAlertsView", "preSendFailures"), ("AppPreSendFailureAlert", "lastFailureCode"),
                ("AppNodeView", "maxDevices"), ("AppNodeMaxDevicesRequest", "maxDevices"), ("AppBindingAdminView", "approvalPending"),
                ("AppBindingAdminView", "approvedAt"), ("AppAlertsView", "pendingApprovalBindings"),
                ("AppPendingApprovalBindingAlert", "registeredAt"), ("AppPendingApprovalBindingAlert", "accountLabel"),
            })
            {
                var description = schemas.GetProperty(schema).GetProperty("properties").GetProperty(property);
                Assert.True(HasChinese(FindDescription(description)), $"{schema}.{property}");
            }
            foreach (var enumSchema in new[] { "AppDailyReportDeliveryStatus", "AppBindingStopReason", "AppNodeStatus" })
                Assert.True(HasChinese(schemas.GetProperty(enumSchema).GetProperty("description").GetString()), enumSchema);
        }
        finally { await app.StopAsync(); }
    }

    private static IEnumerable<string> MembersOf(Type type, HashSet<Type>? seen = null)
    {
        seen ??= [];
        if (!seen.Add(type) || type.IsPrimitive || type == typeof(string) || type.IsEnum) yield break;
        if (type.IsGenericType)
            foreach (var argument in type.GetGenericArguments())
                foreach (var name in MembersOf(argument, seen)) yield return name;
        if (type.Namespace?.StartsWith("Jogeoai", StringComparison.Ordinal) != true) yield break;
        foreach (var property in type.GetProperties())
        {
            yield return property.Name;
            foreach (var name in MembersOf(property.PropertyType, seen)) yield return name;
        }
    }

    private static string Json(object? value) => JsonSerializer.Serialize(value, AppCycleRunApi);

    private static readonly JsonSerializerOptions AppCycleRunApi = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static string? FindDescription(JsonElement property)
        => property.TryGetProperty("description", out var description) ? description.GetString() : null;

    private static bool HasChinese(string? value) => value?.Any(character => character is >= '一' and <= '鿿') == true;

    private static void AssertError(IActionResult result, int status, string code, string? action = null)
    {
        var response = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.True(status == response.StatusCode, $"{action}: 期望 {status}，实际 {response.StatusCode}");
        Assert.Equal(code, JsonSerializer.SerializeToElement(response.Value).GetProperty("error").GetProperty("code").GetString());
    }

    private sealed class Fixture
    {
        internal McpStoreProxy Pool { get; }
        internal McpStoreProxy Admin { get; }
        internal McpStoreProxy Collection { get; }
        internal McpStoreProxy Capacity { get; }
        internal McpStoreProxy Alerts { get; }
        internal McpStoreProxy Reports { get; }
        internal List<string> Logs { get; } = [];
        internal Guid UserId { get; } = Guid.NewGuid();
        internal AppCollectorAdminController Controller { get; }

        internal Fixture(bool authenticated = true, bool admin = true)
        {
            var context = new DefaultHttpContext();
            if (authenticated)
                context.Items[SessionAuthenticationMiddleware.ContextItemKey] = new AuthenticatedRequestContext(
                    new(UserId, "ops@example.test", "Ops", "active", []), Guid.NewGuid(), "session");
            var research = DispatchProxy.Create<IResearchStore, CloroKeysApiTests.ResearchProxy>();
            ((CloroKeysApiTests.ResearchProxy)(object)research).Admin = admin;
            IAppCollectorPoolStore pool; IAppCollectorAdminStore adminStore; IAppCollectionStore collection;
            IAppCapacityQuery capacity; IAppAlertQuery alerts; IAppDailyReportStore reports;
            (pool, Pool) = McpStoreProxy.Create<IAppCollectorPoolStore>();
            (adminStore, Admin) = McpStoreProxy.Create<IAppCollectorAdminStore>();
            (collection, Collection) = McpStoreProxy.Create<IAppCollectionStore>();
            (capacity, Capacity) = McpStoreProxy.Create<IAppCapacityQuery>();
            (alerts, Alerts) = McpStoreProxy.Create<IAppAlertQuery>();
            (reports, Reports) = McpStoreProxy.Create<IAppDailyReportStore>();
            Seed();
            Controller = new AppCollectorAdminController(pool, adminStore, collection, capacity, alerts, reports, research,
                new ListLogger<AppCollectorAdminController>(Logs)) { ControllerContext = new() { HttpContext = context } };
        }

        internal IEnumerable<string> AllStoreCalls()
            => new[] { Pool, Admin, Collection, Capacity, Alerts, Reports }.SelectMany(proxy => proxy.Calls.Select(call => call.Method));

        // Every store answers with plausible data, so that the "no token in other responses" check sees real payloads.
        private void Seed()
        {
            var now = DateTimeOffset.UtcNow;
            var node = new AppNodeView("node-a", AppNodeStatus.Active, now, now, false, "rev", 1, 1, now, now) { MaxDevices = 6 };
            var binding = new AppBindingAdminView(Guid.NewGuid(), "node-a", "AI_Apps_4KB", "doubao", "doubao-a", "com.larus.nova", "1", "1", "15",
                AppBindingStatus.Active, null, null, null, null, false, now, now, now, 300, null, now, now);
            var platforms = AppChannelDefinition.Platforms.Select(p => new AppPlatformStateView(p.Code, p.DisplayName, 30, null, null, false, null,
                null, true, null, false)).ToArray();
            var capacity = AppChannelDefinition.Engines.Select(e => new AppPlatformCapacity(e, true, 1, 2016, null, 2016, 0, 0)).ToArray();
            var alerts = new AppAlertsView(now, false, [], [], [], [], [], capacity.Select(c => new AppCapacityUsage(c, 0)).ToArray());
            var exclusion = new AppPromptExclusionView(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "原因", UserId, now);
            Pool.Handlers[nameof(IAppCollectorPoolStore.RegisterNodeAsync)] = args => Task.FromResult(new AppNodeCredential((string)args[0]!, Token));
            Pool.Handlers[nameof(IAppCollectorPoolStore.RotateNodeTokenAsync)] = args => Task.FromResult(new AppNodeCredential((string)args[0]!, Token));
            Pool.Handlers[nameof(IAppCollectorPoolStore.SetNodeStatusAsync)] = _ => Task.CompletedTask;
            Pool.Handlers[nameof(IAppCollectorPoolStore.ResumeBindingAsync)] = _ => Task.FromResult(true);
            Pool.Handlers[nameof(IAppCollectorPoolStore.ResumePlatformAsync)] = _ => Task.FromResult(true);
            Admin.Handlers[nameof(IAppCollectorAdminStore.ListNodesAsync)] = _ => Task.FromResult<IReadOnlyList<AppNodeView>>([node]);
            Admin.Handlers[nameof(IAppCollectorAdminStore.GetNodeAsync)] = _ => Task.FromResult(node);
            Admin.Handlers[nameof(IAppCollectorAdminStore.SetNodeMaxDevicesAsync)] = _ => Task.FromResult(true);
            Admin.Handlers[nameof(IAppCollectorAdminStore.ListBindingsAsync)] = _ => Task.FromResult<IReadOnlyList<AppBindingAdminView>>([binding]);
            Admin.Handlers[nameof(IAppCollectorAdminStore.GetBindingAsync)] = _ => Task.FromResult(binding);
            Admin.Handlers[nameof(IAppCollectorAdminStore.DisableBindingAsync)] = _ => Task.FromResult(true);
            Admin.Handlers[nameof(IAppCollectorAdminStore.SetBindingLimitsAsync)] = _ => Task.FromResult(true);
            Admin.Handlers[nameof(IAppCollectorAdminStore.ListPlatformsAsync)] = _ => Task.FromResult<IReadOnlyList<AppPlatformStateView>>(platforms);
            Admin.Handlers[nameof(IAppCollectorAdminStore.PausePlatformAsync)] = _ => Task.FromResult(true);
            Admin.Handlers[nameof(IAppCollectorAdminStore.SetPlatformLimitsAsync)] = _ => Task.FromResult(true);
            Admin.Handlers[nameof(IAppCollectorAdminStore.RestorePlatformEntryAsync)] = _ => Task.FromResult(true);
            Admin.Handlers[nameof(IAppCollectorAdminStore.GetQueueAsync)] = _ => Task.FromResult<IReadOnlyList<AppPlatformQueueView>>(
                AppChannelDefinition.Engines.Select(e => new AppPlatformQueueView(e, 1, 0)).ToArray());
            Admin.Handlers[nameof(IAppCollectorAdminStore.GetTaskAttemptsAsync)] = args => Task.FromResult(new AppTaskAttemptsView((Guid)args[0]!,
                "doubao", "succeeded", AppTaskAttemptsView.OutcomeCollected, null, 2, "contract_changed", now.AddDays(6), now,
                [
                    new AppTaskAttemptView(1, "node-a", "AI_Apps_4KB", binding.Id, "doubao", "doubao-a", now.AddMinutes(-10), now.AddMinutes(-8),
                        "not_sent", null, "contract_changed", now.AddMinutes(-9), false),
                    new AppTaskAttemptView(2, "node-a", "AI_Apps_4KB_2", Guid.NewGuid(), "doubao", "doubao-b", now.AddMinutes(-5), now.AddMinutes(-3),
                        "sent", now.AddMinutes(-4), null, null, true),
                ]));
            Admin.Handlers[nameof(IAppCollectorAdminStore.ListEventsAsync)] = _ => Task.FromResult(new CursorPage<AppEventView>(
                [new AppEventView(Guid.NewGuid(), now, "node_token_rotated", "node-a", null, null, null, "operator", null)], null));
            Collection.Handlers[nameof(IAppCollectionStore.ExcludePromptAsync)] = _ => Task.FromResult(exclusion);
            Collection.Handlers[nameof(IAppCollectionStore.RevokePromptExclusionAsync)] = _ => Task.FromResult(exclusion with { RevokedAt = now, RevokedBy = UserId });
            Capacity.Handlers[nameof(IAppCapacityQuery.GetPlatformCapacitiesAsync)] = _ => Task.FromResult<IReadOnlyList<AppPlatformCapacity>>(capacity);
            Alerts.Handlers[nameof(IAppAlertQuery.GetAlertsAsync)] = _ => Task.FromResult(alerts);
            Reports.Handlers[nameof(IAppDailyReportStore.ListAsync)] = _ => Task.FromResult(new CursorPage<AppDailyReportItem>([], null));
            Reports.Handlers[nameof(IAppDailyReportStore.GetAsync)] = _ => Task.FromResult<AppDailyReportView?>(new AppDailyReportView(Guid.NewGuid(),
                new DateOnly(2026, 11, 17), now, false, AppDailyReportDeliveryStatus.NotConfigured, null, null, alerts, "结论：无告警。"));
        }
    }

    private sealed class ListLogger<T>(List<string> lines) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => lines.Add(formatter(state, exception) + (state is IEnumerable values ? " " + string.Join(",", values.Cast<object>()) : ""));
    }
}
