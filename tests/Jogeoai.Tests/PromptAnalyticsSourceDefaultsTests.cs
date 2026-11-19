using System.Reflection;
using Jogeoai.Api;
using Jogeoai.Api.Controllers;
using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Jogeoai.Tests;

/// <summary>analytics/prompts 未显式筛选来源时与监控、品牌概览同一全来源母集。</summary>
public sealed class PromptAnalyticsSourceDefaultsTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Site = Guid.NewGuid();

    [Fact]
    public async Task PromptsDefaultToEveryProviderCaptureModeAndEvidenceClass()
    {
        var page = new AnalyticsPage<PromptPerformanceView>(null!, [], null);
        var controller = Controller("?snapshotToken=prompt-snapshot&cursor=next&limit=100&sort=visibility&engine=doubao",
            (method, args) =>
            {
                Assert.Equal(nameof(IAnalyticsQueryStore.PromptsAsync), method.Name);
                Assert.Equal(Tenant, args[0]);
                Assert.Equal(Site, args[1]);
                var filter = Assert.IsType<AnalyticsFilterRequest>(args[2]);
                Assert.Equal(new[] { "cloro", "doubao_api", "deepseek_api", "packy_qwen", "packy_glm", "packy_kimi", "packy_yuanbao", "domestic_collector" }, filter.Providers);
                Assert.Equal(new[] { "api", "scraped" }, filter.CaptureModes);
                Assert.Equal(new[] { "api_response", "observed_surface", "unknown" }, filter.EvidenceClasses);
                Assert.Equal(new[] { "doubao" }, filter.Engines);
                Assert.Equal(new object?[] { "prompt-snapshot", "next", 100, "visibility" }, args[3..7]);
                return Task.FromResult(page);
            });

        var result = Assert.IsType<ObjectResult>(await controller.Prompts(Site, CancellationToken.None));

        Assert.Equal(200, result.StatusCode);
        Assert.Same(page, result.Value);
    }

    [Theory]
    // 单一 Provider 时采集模式与证据类别仍取全集，豆包 API 的 api/api_response 不会被旧的 scraped 默认排除。
    [InlineData("?provider=doubao_api", new[] { "doubao_api" }, new[] { "api", "scraped" },
        new[] { "api_response", "observed_surface", "unknown" })]
    [InlineData("?provider=domestic_collector&captureMode=scraped", new[] { "domestic_collector" }, new[] { "scraped" },
        new[] { "api_response", "observed_surface", "unknown" })]
    [InlineData("?provider=cloro&captureMode=scraped&evidenceClass=observed_surface", new[] { "cloro" },
        new[] { "scraped" }, new[] { "observed_surface" })]
    public async Task ExplicitSourceFiltersAreForwardedUnchanged(string query, string[] providers,
        string[] modes, string[] classes)
    {
        var controller = Controller(query, (method, args) =>
        {
            Assert.Equal(nameof(IAnalyticsQueryStore.PromptsAsync), method.Name);
            var filter = Assert.IsType<AnalyticsFilterRequest>(args[2]);
            Assert.Equal(providers, filter.Providers);
            Assert.Equal(modes, filter.CaptureModes);
            Assert.Equal(classes, filter.EvidenceClasses);
            return Task.FromResult(new AnalyticsPage<PromptPerformanceView>(null!, [], null));
        });

        var result = Assert.IsType<ObjectResult>(await controller.Prompts(Site, CancellationToken.None));
        Assert.Equal(200, result.StatusCode);
    }

    [Fact]
    public async Task LegacyOverviewKeepsTheStoreCloroDefault()
    {
        AnalyticsFilterRequest? forwarded = null;
        var controller = Controller("", (method, args) =>
        {
            Assert.Equal(nameof(IAnalyticsQueryStore.OverviewAsync), method.Name);
            forwarded = Assert.IsType<AnalyticsFilterRequest>(args[2]);
            return Task.FromResult<OverviewView>(null!);
        });

        await controller.Overview(Site, CancellationToken.None);

        Assert.NotNull(forwarded);
        Assert.Null(forwarded.Providers);
        Assert.Null(forwarded.CaptureModes);
        Assert.Null(forwarded.EvidenceClasses);
    }

    [Theory]
    [InlineData(nameof(AnalyticsController.Prompts), true)]
    [InlineData(nameof(AnalyticsController.BrandDashboard), true)]
    [InlineData(nameof(AnalyticsController.MonitoringRecords), true)]
    [InlineData(nameof(AnalyticsController.Overview), false)]
    [InlineData(nameof(AnalyticsController.PromptDetail), false)]
    public void SwaggerDescribesTheActualSourceDefaults(string action, bool allSources)
    {
        var operation = new OpenApiOperation
        {
            Parameters = [new OpenApiParameter { Name = "siteId", In = ParameterLocation.Path, Required = true }]
        };
        new AnalyticsOpenApiOperationFilter().Apply(operation, new OperationFilterContext(new ApiDescription(), null!,
            new SchemaRepository(), new OpenApiDocument(), typeof(AnalyticsController).GetMethod(action)!));
        string Description(string name) => operation.Parameters!.Single(p => p.Name == name).Description!;

        if (allSources)
        {
            Assert.Contains("默认全部八个", Description("provider"));
            Assert.Contains("deepseek_api", Description("provider"));
            Assert.Contains("默认 api 与 scraped", Description("captureMode"));
            Assert.Contains("默认 api_response、observed_surface 与 unknown", Description("evidenceClass"));
        }
        else
        {
            Assert.Contains("默认 cloro", Description("provider"));
            Assert.Contains("默认 scraped", Description("captureMode"));
            Assert.Contains("默认 observed_surface", Description("evidenceClass"));
        }
    }

    private static AnalyticsController Controller(string query, Func<MethodInfo, object?[], object?> handler)
    {
        var queries = DispatchProxy.Create<IAnalyticsQueryStore, MonitoringAnswerApiTests.QueryProxy>();
        ((MonitoringAnswerApiTests.QueryProxy)queries).Handler = handler;
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(query);
        context.Items[SessionAuthenticationMiddleware.ContextItemKey] = new AuthenticatedRequestContext(
            new AuthUserView(Guid.NewGuid(), "member@fixture.test", "Member", "active",
                [new TenantMembershipView(Tenant, "Team", "owner", "active")]), Guid.NewGuid(), "fixture-token");
        return new(queries, null!, new TenantContextResolver(new TestEnvironment()))
        { ControllerContext = new ControllerContext { HttpContext = context } };
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
