using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jogeoai.Api;
using Jogeoai.Api.Controllers;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;

namespace Jogeoai.Tests;

/// <summary>无库 API 测试：管理员 401 / 403 矩阵、viewer 兑换 403、PLAN_LIMIT_EXCEEDED details、Swagger 中文。</summary>
public sealed class SubscriptionApiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Theory]
    [InlineData(false, 401, "AUTHENTICATION_REQUIRED")]
    [InlineData(true, 403, "ADMIN_REQUIRED")]
    public async Task EveryAdminEndpointRequiresSystemAdministratorBeforeTouchingTheStore(bool authenticated, int status, string code)
    {
        var f = new Fixture(authenticated, admin: false);
        var id = Guid.NewGuid();
        var results = new List<IActionResult?>
        {
            (await f.Admin.CreateBatch(new("basic", 3, 5), default)).Result,
            (await f.Admin.ListCodes(null, null, null, null, 50, default)).Result,
            (await f.Admin.RevokeCode(id, default)).Result,
            (await f.Admin.GetTenantSubscription(id, default)).Result,
            (await f.Admin.AssignTenantSubscription(id, new("basic", 1, null, "test"), default)).Result,
            (await f.Admin.UpdatePlan("basic", new(Recommended: true), default)).Result,
        };
        foreach (var result in results) AssertError(result!, status, code);
        Assert.Empty(f.Store.Calls);
    }

    [Fact]
    public async Task AdminBatchReturnsPlaintextCodesOnlyInTheResponse()
    {
        var f = new Fixture();
        var created = Assert.IsType<ObjectResult>((await f.Admin.CreateBatch(new("basic", 3, 2, null, "活动"), default)).Result);
        Assert.Equal(201, created.StatusCode);
        var result = Assert.IsType<PlanCodeBatchResult>(created.Value);
        Assert.Equal(2, result.Codes.Count);
        Assert.All(result.Codes, item => Assert.StartsWith("JG-", item));
        Assert.Equal(("CreateCodeBatchAsync", f.UserId), (Assert.Single(f.Store.Calls).Method, f.Store.LastActor));
    }

    [Fact]
    public async Task AnonymousAndViewerAreRejectedWhileAnyMemberCanReadPlansAndSubscription()
    {
        var anonymous = new Fixture(authenticated: false);
        AssertError(await anonymous.Tenant.Plans(default), 401, "AUTHENTICATION_REQUIRED");
        AssertError(await anonymous.Tenant.Current(default), 401, "AUTHENTICATION_REQUIRED");
        AssertError(await anonymous.Tenant.Redeem(new("JG-ABCD-EFGH-JKLM-NPQR"), default), 401, "AUTHENTICATION_REQUIRED");
        Assert.Empty(anonymous.Store.Calls);

        var viewer = new Fixture(role: "viewer");
        Assert.IsType<OkObjectResult>(await viewer.Tenant.Plans(default));
        Assert.IsType<OkObjectResult>(await viewer.Tenant.Current(default));
        AssertError(await viewer.Tenant.Redeem(new("JG-ABCD-EFGH-JKLM-NPQR"), default), 403, "CONTENT_FORBIDDEN");
        Assert.DoesNotContain(viewer.Store.Calls, call => call.Method == "RedeemAsync");

        // FR-20：只有 owner / admin 可兑换，member 也被拒绝。
        var member = new Fixture(role: "member");
        AssertError(await member.Tenant.Redeem(new("JG-ABCD-EFGH-JKLM-NPQR"), default), 403, "CONTENT_FORBIDDEN");
        Assert.DoesNotContain(member.Store.Calls, call => call.Method == "RedeemAsync");
        var admin = new Fixture(role: "admin");
        Assert.IsType<OkObjectResult>(await admin.Tenant.Redeem(new("JG-ABCD-EFGH-JKLM-NPQR"), default));

        var owner = new Fixture(role: "owner");
        owner.Context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.9");
        Assert.IsType<OkObjectResult>(await owner.Tenant.Redeem(new(" jg-abcd-efgh-jklm-npqr "), default));
        var redeem = Assert.Single(owner.Store.Calls, call => call.Method == "RedeemAsync");
        Assert.Equal(owner.TenantId, redeem.TenantId);
        Assert.Equal(owner.UserId, owner.Store.LastActor);
        Assert.Equal(" jg-abcd-efgh-jklm-npqr ", owner.Store.LastCode);
        Assert.Equal("203.0.113.9", owner.Store.LastIp);
    }

    [Theory]
    [InlineData("PLAN_CODE_INVALID", 404)]
    [InlineData("PLAN_CODE_USED", 409)]
    [InlineData("PLAN_DOWNGRADE_NOT_ALLOWED", 409)]
    [InlineData("PLAN_REDEEM_RATE_LIMITED", 429)]
    [InlineData("SUBSCRIPTION_SCHEMA_NOT_MIGRATED", 503)]
    public async Task StoreErrorsKeepTheirStableCodeAndStatus(string code, int status)
    {
        var f = new Fixture(role: "admin");
        f.Store.Failure = new CollectorStoreException(code, "message", status);
        AssertError(await f.Tenant.Redeem(new("JG-ABCD-EFGH-JKLM-NPQR"), default), status, code);
    }

    [Fact]
    public async Task PlanLimitExceededCarriesStructuredDetailsFromEveryMappedController()
    {
        var exception = new PlanLimitExceededException("free_trial", "免费体验版", PlanLimitKind.Brands, 1, 1);
        var f = new Fixture();
        f.Store.Failure = exception;
        AssertLimitDetails(await f.Tenant.Current(default));

        var management = new ManagementController(ManagementProxy.Create(exception), f.Resolver)
            { ControllerContext = new() { HttpContext = f.Context } };
        AssertLimitDetails(await management.CreateSite(new("品牌", "example.test"), default));

        var onboarding = new OnboardingController(ManagementProxy.Create(exception), null!, f.Resolver)
            { ControllerContext = new() { HttpContext = f.Context } };
        AssertLimitDetails((await onboarding.CreateBrand(new("品牌", "example.test"), default)).Result!);

        // 管理员接口同样输出 details（例如指派时的下游守卫）。
        AssertLimitDetails((await f.Admin.AssignTenantSubscription(Guid.NewGuid(), new("basic", 1, null, "test"), default)).Result!);
    }

    [Fact]
    public async Task SubscriptionRoutesAreDocumentedInChineseInSwagger()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(SubscriptionController).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production,
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddControllers().AddApplicationPart(typeof(SubscriptionController).Assembly)
            .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(options =>
        {
            options.OperationFilter<AnalyticsOpenApiOperationFilter>();
            options.UseAllOfToExtendReferenceSchemas();
            options.SwaggerDoc("v1", new OpenApiInfo { Title = "Jogeoai API", Version = "v1" });
            foreach (var assembly in new[] { "Jogeoai.Api", "Jogeoai.Domain", "Jogeoai.Application", "Jogeoai.Providers.Abstractions" })
                options.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, assembly + ".xml"),
                    includeControllerXmlComments: assembly == "Jogeoai.Api");
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
            foreach (var (path, method) in new[]
            {
                ("/api/v1/plans", "get"), ("/api/v1/subscription", "get"), ("/api/v1/subscription/redeem", "post"),
                ("/api/v1/admin/plan-codes/batches", "post"), ("/api/v1/admin/plan-codes", "get"),
                ("/api/v1/admin/plan-codes/{codeId}/revoke", "post"), ("/api/v1/admin/tenants/{tenantId}/subscription", "get"),
                ("/api/v1/admin/tenants/{tenantId}/subscription", "post"), ("/api/v1/admin/plans/{code}", "put"),
            })
            {
                var operation = paths.GetProperty(path).GetProperty(method);
                Assert.True(HasChinese(operation.GetProperty("summary").GetString()), $"{method} {path}");
            }
            var schemas = swagger.RootElement.GetProperty("components").GetProperty("schemas");
            foreach (var (schema, property) in new[]
            {
                ("SubscriptionPlanView", "isCurrent"), ("SubscriptionPlanView", "monthlyCredits"), ("PlanLimitsView", "maxPrompts"),
                ("TenantSubscriptionView", "expiresAt"), ("SubscriptionPeriodView", "used"), ("PlanUsageView", "competitors"),
                ("PlanCodeBatchResult", "codes"), ("PlanCodeView", "codeHint"), ("AssignSubscriptionRequest", "reason"),
                ("SubscriptionPlanUpdate", "limits"), ("RedeemPlanCodeRequest", "code"),
            })
            {
                var description = schemas.GetProperty(schema).GetProperty("properties").GetProperty(property);
                Assert.True(HasChinese(FindDescription(description)), $"{schema}.{property}");
            }
            foreach (var enumSchema in new[] { "SubscriptionSource", "PlanCodeStatus", "SubscriptionEventType" })
                Assert.True(HasChinese(schemas.GetProperty(enumSchema).GetProperty("description").GetString()), enumSchema);
        }
        finally { await app.StopAsync(); }
    }

    private static string? FindDescription(JsonElement property) =>
        property.TryGetProperty("description", out var description) ? description.GetString() : null;

    private static bool HasChinese(string? value) => value?.Any(character => character is >= '一' and <= '鿿') == true;

    private static void AssertError(IActionResult result, int status, string code)
    {
        var response = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, JsonSerializer.SerializeToElement(response.Value, Json).GetProperty("error").GetProperty("code").GetString());
    }

    private static void AssertLimitDetails(IActionResult result)
    {
        var response = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(409, response.StatusCode);
        var error = JsonSerializer.SerializeToElement(response.Value, Json).GetProperty("error");
        Assert.Equal("PLAN_LIMIT_EXCEEDED", error.GetProperty("code").GetString());
        Assert.True(HasChinese(error.GetProperty("message").GetString()));
        var details = error.GetProperty("details");
        Assert.Equal("free_trial", details.GetProperty("planCode").GetString());
        Assert.Equal("brands", details.GetProperty("limit").GetString());
        Assert.Equal(1, details.GetProperty("limitValue").GetInt32());
        Assert.Equal(1, details.GetProperty("currentValue").GetInt32());
    }

    private sealed class Fixture
    {
        internal FakeSubscriptionStore Store { get; } = new();
        internal SubscriptionController Tenant { get; }
        internal SubscriptionAdminController Admin { get; }
        internal DefaultHttpContext Context { get; } = new();
        internal TenantContextResolver Resolver { get; } = new(new TestEnvironment { EnvironmentName = Environments.Production });
        internal Guid TenantId { get; } = Guid.NewGuid();
        internal Guid UserId { get; } = Guid.NewGuid();

        internal Fixture(bool authenticated = true, bool admin = true, string role = "owner")
        {
            if (authenticated)
                Context.Items[SessionAuthenticationMiddleware.ContextItemKey] = new AuthenticatedRequestContext(
                    new(UserId, "user@example.test", "User", "active", [new TenantMembershipView(TenantId, "团队", role, "active")]),
                    Guid.NewGuid(), "session");
            var research = DispatchProxy.Create<IResearchStore, ResearchProxy>();
            ((ResearchProxy)(object)research).Admin = admin;
            Tenant = new SubscriptionController(Store, Resolver) { ControllerContext = new() { HttpContext = Context } };
            Admin = new SubscriptionAdminController(Store, research) { ControllerContext = new() { HttpContext = Context } };
        }
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    public class ResearchProxy : DispatchProxy
    {
        public bool Admin { get; set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            nameof(IResearchStore.IsSystemAdministratorAsync) => Task.FromResult(Admin),
            _ => throw new InvalidOperationException($"Unexpected research store call: {targetMethod?.Name}"),
        };
    }

    public class ManagementProxy : DispatchProxy
    {
        public Exception? Failure { get; set; }
        internal static IManagementStore Create(Exception failure)
        {
            var proxy = DispatchProxy.Create<IManagementStore, ManagementProxy>();
            ((ManagementProxy)(object)proxy).Failure = failure;
            return proxy;
        }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            nameof(IManagementStore.CreateBrandAsync) => Task.FromException<BrandView>(Failure!),
            _ => throw new InvalidOperationException($"Unexpected management store call: {targetMethod?.Name}"),
        };
    }

    private sealed class FakeSubscriptionStore : ISubscriptionStore
    {
        private static readonly SubscriptionPlanView Plan = new("free_trial", "免费体验版", "新注册默认", 0, false,
            new PlanLimitsView(1, 1, 20, 2), 500, 0, 1, 3, new PlanFeaturesView(true, true, true, false), true, true);
        private static readonly PlanUsageView Usage = new(new(0, 1), new(1, 1), new(0, 20), [], 500, 0, 500, 0);

        internal List<(string Method, Guid TenantId)> Calls { get; } = [];
        internal Exception? Failure { get; set; }
        internal Guid? LastActor { get; private set; }
        internal string? LastCode { get; private set; }
        internal string? LastIp { get; private set; }

        private T Record<T>(string method, Guid tenantId, T value)
        {
            Calls.Add((method, tenantId));
            if (Failure is not null) throw Failure;
            return value;
        }

        private TenantSubscriptionView View(Guid tenantId) => new(Guid.NewGuid(), tenantId, Plan, SubscriptionSource.Registration,
            DateTimeOffset.UtcNow, null, null, Usage);

        public Task<IReadOnlyList<SubscriptionPlanView>> ListPlansAsync(Guid? tenantId, CancellationToken cancellationToken = default)
            => Task.FromResult(Record<IReadOnlyList<SubscriptionPlanView>>(nameof(ListPlansAsync), tenantId ?? Guid.Empty, [Plan]));
        public Task<TenantSubscriptionView> GetAsync(Guid tenantId, CancellationToken cancellationToken = default)
            => Task.FromResult(Record(nameof(GetAsync), tenantId, View(tenantId)));
        public Task<TenantSubscriptionView> RedeemAsync(Guid tenantId, Guid userId, string code, string clientIp, CancellationToken cancellationToken = default)
        {
            LastActor = userId; LastCode = code; LastIp = clientIp;
            return Task.FromResult(Record(nameof(RedeemAsync), tenantId, View(tenantId)));
        }
        public Task<TenantSubscriptionView> AssignAsync(Guid tenantId, Guid actorUserId, AssignSubscriptionRequest request, CancellationToken cancellationToken = default)
        {
            LastActor = actorUserId;
            return Task.FromResult(Record(nameof(AssignAsync), tenantId, View(tenantId)));
        }
        public Task<AdminTenantSubscriptionView> GetForAdminAsync(Guid tenantId, CancellationToken cancellationToken = default)
            => Task.FromResult(Record(nameof(GetForAdminAsync), tenantId, new AdminTenantSubscriptionView(View(tenantId), [])));
        public Task<SubscriptionPlanView> UpdatePlanAsync(string code, SubscriptionPlanUpdate update, Guid actorUserId, CancellationToken cancellationToken = default)
        {
            LastActor = actorUserId;
            return Task.FromResult(Record(nameof(UpdatePlanAsync), Guid.Empty, Plan));
        }
        public Task<PlanCodeBatchResult> CreateCodeBatchAsync(PlanCodeBatchRequest request, Guid actorUserId, CancellationToken cancellationToken = default)
        {
            LastActor = actorUserId;
            var codes = Enumerable.Range(0, request.Quantity).Select(_ => PlanCodeFormat.Generate()).ToArray();
            return Task.FromResult(Record(nameof(CreateCodeBatchAsync), Guid.Empty, new PlanCodeBatchResult(
                new PlanCodeBatchView(Guid.NewGuid(), request.PlanCode, request.Months, request.Quantity, request.ExpiresAt, request.Note,
                    actorUserId, DateTimeOffset.UtcNow), codes)));
        }
        public Task<PlanCodePage> ListCodesAsync(Guid? batchId, string? status, string? planCode, string? cursor, int limit, CancellationToken cancellationToken = default)
            => Task.FromResult(Record(nameof(ListCodesAsync), Guid.Empty, new PlanCodePage([], null)));
        public Task<PlanCodeView> RevokeCodeAsync(Guid codeId, Guid actorUserId, CancellationToken cancellationToken = default)
        {
            LastActor = actorUserId;
            return Task.FromResult(Record(nameof(RevokeCodeAsync), Guid.Empty, new PlanCodeView(codeId, Guid.NewGuid(), "basic", 3, "NPQR",
                PlanCodeStatus.Revoked, null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
        }
        public Task<int> RolloverDuePeriodsAsync(DateTime utcNow, int batchSize, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<int> DowngradeExpiredAsync(DateTime utcNow, int batchSize, CancellationToken cancellationToken = default) => Task.FromResult(0);
    }
}
