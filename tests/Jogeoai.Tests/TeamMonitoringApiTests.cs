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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;

namespace Jogeoai.Tests;

/// <summary>
/// 团队默认监控配置接口（无库）：角色矩阵（D5 / PRD A-6）——GET 所有角色 200、canEdit 只对 owner / admin 为 true；PUT 与 estimate 只允许
/// owner / admin，其他角色 403 且零 Store 调用；follow-default 沿用品牌目标写权限（viewer 403、member 可写）；外租户 403 TENANT_FORBIDDEN；
/// Store 错误码原样返回（A-7 / A-10）；Swagger 中新接口与 DTO 为中文说明。
/// </summary>
public sealed class TeamMonitoringApiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly TeamMonitoringConfigWrite Write = new(3, [new("deepseek_api", "deepseek", AnswerMode.Deep, MonitoringFrequency.TwiceDaily)]);
    private static readonly TeamMonitoringEstimateRequest EstimateRequest = new([new("cloro", "chatgpt")]);

    [Theory]
    [InlineData("owner", true)]
    [InlineData("admin", true)]
    [InlineData("member", false)]
    [InlineData("viewer", false)]
    public async Task EveryRoleReadsTheConfigAndOnlyOwnersAndAdminsCanEdit(string role, bool canEdit)
    {
        var f = new Fixture(role);
        var view = Assert.IsType<TeamMonitoringConfigView>(Assert.IsType<OkObjectResult>(await f.Team.MonitoringConfig(default)).Value);
        Assert.Equal(canEdit, view.CanEdit);
        var call = Assert.Single(f.Management.Calls);
        Assert.Equal((nameof(IManagementStore.GetTeamMonitoringConfigAsync), f.TenantId), (call.Method, (Guid)call.Args[0]!));
    }

    [Theory]
    [InlineData("member")]
    [InlineData("viewer")]
    public async Task MembersAndViewersCannotSaveOrEstimateTheTeamDefault(string role)
    {
        var f = new Fixture(role);
        AssertError(await f.Team.PutMonitoringConfig(Write, default), 403, "CONTENT_FORBIDDEN");
        AssertError(await f.Team.EstimateMonitoringConfig(EstimateRequest, default), 403, "CONTENT_FORBIDDEN");
        Assert.Empty(f.Management.Calls);
        Assert.Empty(f.Monitoring.Calls);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("admin")]
    public async Task OwnersAndAdminsSaveAndEstimate(string role)
    {
        var f = new Fixture(role);
        var saved = Assert.IsType<TeamMonitoringConfigView>(Assert.IsType<OkObjectResult>(await f.Team.PutMonitoringConfig(Write, default)).Value);
        Assert.True(saved.CanEdit);
        var put = Assert.Single(f.Management.Calls);
        Assert.Equal((nameof(IManagementStore.PutTeamMonitoringConfigAsync), f.TenantId, f.UserId, (object)Write),
            (put.Method, (Guid)put.Args[0]!, (Guid)put.Args[1]!, put.Args[2]!));
        Assert.IsType<TeamMonitoringEstimateView>(Assert.IsType<OkObjectResult>(await f.Team.EstimateMonitoringConfig(EstimateRequest, default)).Value);
        var estimate = Assert.Single(f.Monitoring.Calls);
        Assert.Equal((nameof(IPromptMonitoringService.EstimateTeamMonitoringAsync), f.TenantId, (object)EstimateRequest),
            (estimate.Method, (Guid)estimate.Args[0]!, estimate.Args[1]!));
    }

    [Fact]
    public async Task AnonymousAndForeignTenantRequestsAreRejectedBeforeAnyCall()
    {
        var anonymous = new Fixture(null);
        AssertError(await anonymous.Team.MonitoringConfig(default), 401, "AUTHENTICATION_REQUIRED");
        AssertError(await anonymous.Team.PutMonitoringConfig(Write, default), 401, "AUTHENTICATION_REQUIRED");
        AssertError(await anonymous.Team.EstimateMonitoringConfig(EstimateRequest, default), 401, "AUTHENTICATION_REQUIRED");

        var foreign = new Fixture("owner");
        foreign.Context.Request.Headers["X-Tenant-Id"] = Guid.NewGuid().ToString();
        AssertError(await foreign.Team.MonitoringConfig(default), 403, "TENANT_FORBIDDEN");
        AssertError(await foreign.Team.PutMonitoringConfig(Write, default), 403, "TENANT_FORBIDDEN");
        AssertError(await foreign.Team.EstimateMonitoringConfig(EstimateRequest, default), 403, "TENANT_FORBIDDEN");
        AssertError(await foreign.Brands.FollowTeamDefault(Guid.NewGuid(), new FollowTeamDefaultRequest(1), default), 403, "TENANT_FORBIDDEN");
        Assert.Empty(anonymous.Management.Calls.Concat(foreign.Management.Calls));
        Assert.Empty(anonymous.Monitoring.Calls.Concat(foreign.Monitoring.Calls));
    }

    [Theory]
    [InlineData("viewer", 403)]
    [InlineData("member", 200)]
    [InlineData("admin", 200)]
    [InlineData("owner", 200)]
    public async Task FollowDefaultUsesTheBrandTargetWriteGuard(string role, int status)
    {
        var f = new Fixture(role);
        var site = Guid.NewGuid();
        var result = await f.Brands.FollowTeamDefault(site, new FollowTeamDefaultRequest(7), default);
        Assert.Equal(status, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        if (status == 403)
        {
            AssertError(result, 403, "CONTENT_FORBIDDEN");
            Assert.Empty(f.Management.Calls);
            return;
        }
        Assert.True(Assert.IsType<SiteTargetsView>(Assert.IsType<OkObjectResult>(result).Value).FollowsTeamDefault);
        var call = Assert.Single(f.Management.Calls);
        Assert.Equal((nameof(IManagementStore.FollowTeamDefaultAsync), f.TenantId, site, 7u),
            (call.Method, (Guid)call.Args[0]!, (Guid)call.Args[1]!, ((FollowTeamDefaultRequest)call.Args[2]!).ExpectedVersion));
    }

    [Fact]
    public async Task BrandTargetSaveIsMarkedAsAnIndividualConfiguration()
    {
        var f = new Fixture("member");
        Assert.IsType<OkObjectResult>(await f.Brands.PutTargets(Guid.NewGuid(), new SiteTargetsWrite(1, [new("cloro", "chatgpt")]), default));
        var call = Assert.Single(f.Management.Calls);
        Assert.Equal(nameof(IManagementStore.PutSiteTargetsAsync), call.Method);
        Assert.Equal(true, call.Args[4]);
    }

    [Theory]
    [InlineData("CONFIG_VERSION_CONFLICT", 409)]
    [InlineData("MONITORING_TARGETS_REQUIRED", 400)]
    [InlineData("DUPLICATE_TARGET", 400)]
    [InlineData("ANSWER_MODE_UNSUPPORTED", 400)]
    [InlineData("FREQUENCY_INVALID", 400)]
    [InlineData("MONITORING_TOO_LARGE", 400)]
    [InlineData("TARGET_NOT_ALLOWED", 400)]
    [InlineData("TEAM_MONITORING_SCHEMA_NOT_MIGRATED", 503)]
    public async Task StoreErrorsKeepTheirStableCodes(string code, int status)
    {
        var f = new Fixture("owner") { Failure = new CollectorStoreException(code, "错误", status) };
        AssertError(await f.Team.PutMonitoringConfig(Write, default), status, code);
        AssertError(await f.Team.EstimateMonitoringConfig(EstimateRequest, default), status, code);
        AssertError(await f.Team.MonitoringConfig(default), status, code);
        AssertError(await f.Brands.FollowTeamDefault(Guid.NewGuid(), new FollowTeamDefaultRequest(1), default), status, code);
    }

    [Fact]
    public async Task TeamMonitoringRoutesAreDocumentedInChinese()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(TeamController).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production,
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddControllers().AddApplicationPart(typeof(TeamController).Assembly)
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
                ("/api/v1/team/monitoring-config", "get"), ("/api/v1/team/monitoring-config", "put"),
                ("/api/v1/team/monitoring-config/estimate", "post"), ("/api/v1/sites/{siteId}/targets/follow-default", "post"),
                ("/api/v1/sites/{siteId}/targets", "put"),
            })
            {
                var operation = paths.GetProperty(path).GetProperty(method);
                Assert.True(HasChinese(operation.GetProperty("summary").GetString()), $"{method} {path}");
                Assert.True(operation.GetProperty("responses").TryGetProperty("200", out _), $"{method} {path} 缺少 200");
            }
            var put = paths.GetProperty("/api/v1/team/monitoring-config").GetProperty("put").GetProperty("responses");
            foreach (var status in new[] { "400", "403", "409", "503" })
                Assert.True(HasChinese(put.GetProperty(status).GetProperty("description").GetString()), $"PUT {status}");
            Assert.Contains("单独配置", paths.GetProperty("/api/v1/sites/{siteId}/targets").GetProperty("put").GetProperty("summary").GetString(),
                StringComparison.Ordinal);

            var schemas = swagger.RootElement.GetProperty("components").GetProperty("schemas");
            foreach (var (schema, property) in new[]
            {
                ("TeamMonitoringConfigView", "configVersion"), ("TeamMonitoringConfigView", "canEdit"), ("TeamMonitoringConfigView", "cells"),
                ("TeamMonitoringConfigView", "brands"), ("TeamMonitoringCellView", "provider"), ("TeamMonitoringCellView", "displayName"),
                ("TeamMonitoringCellView", "answerMode"), ("TeamMonitoringCellView", "frequency"), ("TeamMonitoringBrandView", "followsTeamDefault"),
                ("TeamMonitoringBrandView", "monitoringStarted"), ("TeamMonitoringBrandView", "targetConfigVersion"), ("TeamMonitoringBrandView", "cells"),
                ("TeamMonitoringConfigWrite", "expectedVersion"), ("TeamMonitoringConfigWrite", "cells"), ("TeamMonitoringCellWrite", "answerMode"),
                ("TeamMonitoringCellWrite", "frequency"), ("TeamMonitoringEstimateRequest", "cells"), ("TeamMonitoringEstimateView", "shortfall"),
                ("TeamMonitoringEstimateView", "nextSlotCredits"), ("TeamMonitoringEstimateView", "brands"), ("TeamMonitoringBrandEstimate", "blockers"),
                ("TeamMonitoringBrandEstimate", "weeklyCredits"), ("FollowTeamDefaultRequest", "expectedVersion"), ("SiteTargetsView", "followsTeamDefault"),
            })
            {
                var properties = schemas.GetProperty(schema).GetProperty("properties");
                Assert.True(properties.TryGetProperty(property, out var description), $"{schema}.{property} 缺失");
                Assert.True(HasChinese(FindDescription(description)), $"{schema}.{property} 缺少中文说明");
            }
        }
        finally { await app.StopAsync(); }
    }

    private static string? FindDescription(JsonElement property)
    {
        if (property.TryGetProperty("description", out var description)) return description.GetString();
        if (property.TryGetProperty("allOf", out var allOf))
            foreach (var item in allOf.EnumerateArray())
                if (item.TryGetProperty("description", out var nested)) return nested.GetString();
        return null;
    }

    private static bool HasChinese(string? value) => value?.Any(character => character is >= '一' and <= '鿿') == true;

    private static void AssertError(IActionResult result, int status, string code)
    {
        var response = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, JsonSerializer.SerializeToElement(response.Value, Json).GetProperty("error").GetProperty("code").GetString());
    }

    private sealed class Fixture
    {
        internal TeamController Team { get; }
        internal ManagementController Brands { get; }
        internal McpStoreProxy Management { get; }
        internal McpStoreProxy Monitoring { get; }
        internal DefaultHttpContext Context { get; } = new();
        internal Guid TenantId { get; } = Guid.NewGuid();
        internal Guid UserId { get; } = Guid.NewGuid();
        internal CollectorStoreException? Failure { get; init; }

        internal Fixture(string? role)
        {
            if (role is not null)
                Context.Items[SessionAuthenticationMiddleware.ContextItemKey] = new AuthenticatedRequestContext(
                    new(UserId, "user@example.test", "User", "active", [new TenantMembershipView(TenantId, "团队", role, "active")]),
                    Guid.NewGuid(), "session");
            var resolver = new TenantContextResolver(new TestEnvironment());
            var (management, managementProxy) = McpStoreProxy.Create<IManagementStore>();
            var (monitoring, monitoringProxy) = McpStoreProxy.Create<IPromptMonitoringService>();
            (Management, Monitoring) = (managementProxy, monitoringProxy);
            var config = new TeamMonitoringConfigView(3, false, [new("cloro", "chatgpt", "chatgpt", AnswerMode.Fast, MonitoringFrequency.Daily)], []);
            managementProxy.Handlers[nameof(IManagementStore.GetTeamMonitoringConfigAsync)] = _ => Result(config);
            managementProxy.Handlers[nameof(IManagementStore.PutTeamMonitoringConfigAsync)] = _ => Result(config with { ConfigVersion = 4 });
            managementProxy.Handlers[nameof(IManagementStore.FollowTeamDefaultAsync)] = _ => Result(new SiteTargetsView(8, [], true));
            managementProxy.Handlers[nameof(IManagementStore.PutSiteTargetsAsync)] = _ => Result(new SiteTargetsView(2, []));
            monitoringProxy.Handlers[nameof(IPromptMonitoringService.EstimateTeamMonitoringAsync)] =
                _ => Result(new TeamMonitoringEstimateView(10m, 100m, 0m, 0m, 0m, 0m, []));
            var summary = McpStoreProxy.Create<ITeamSummaryQuery>().Store;
            var context = new ControllerContext { HttpContext = Context };
            Team = new TeamController(summary, resolver, management, monitoring) { ControllerContext = context };
            Brands = new ManagementController(management, resolver) { ControllerContext = context };
        }

        private Task<T> Result<T>(T value) => Failure is null ? Task.FromResult(value) : Task.FromException<T>(Failure);
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
