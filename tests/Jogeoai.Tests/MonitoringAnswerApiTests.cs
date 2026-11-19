using System.Reflection;
using System.Text.Json;
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

public sealed class MonitoringAnswerApiTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Site = Guid.NewGuid();
    private static readonly Guid Check = Guid.NewGuid();

    [Fact]
    public async Task DetailForwardsScopedIdentitySnapshotSearchAndCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var detail = new MonitoringAnswerDetailView(Site, Check, Guid.NewGuid(), Guid.NewGuid(),
            "Historical question", "domestic_collector", "deepseek", null, null, null,
            "running", false, null, [], "unknown", [], null, null, null, null, null, false);
        var controller = Controller("?snapshotToken=list-snapshot&search=%20History%20&mention=%20Mentioned%20", (method, args) =>
        {
            Assert.Equal(nameof(IAnalyticsQueryStore.MonitoringAnswerDetailAsync), method.Name);
            Assert.Equal(new object?[] { Tenant, Site, Check, "list-snapshot", " History ", " Mentioned ", cancellation.Token }, args);
            return Task.FromResult<MonitoringAnswerDetailView?>(detail);
        });

        var result = Assert.IsType<ObjectResult>(await controller.MonitoringAnswerDetail(Site, Check, cancellation.Token));

        Assert.Equal(200, result.StatusCode);
        Assert.Same(detail, result.Value);
        Assert.False(detail.MatchesListFilters);
    }

    [Theory]
    [InlineData(null, null, null, null)]
    [InlineData("literal %_", "not_mentioned", null, null)]
    [InlineData("literal %_", "unknown", "copilot", null)]
    [InlineData("literal %_", "unknown", "perplexity", null)]
    [InlineData("literal %_", "unknown", "google-ai-overview", null)]
    [InlineData(null, null, "doubao", "zhihu.com")]
    public async Task RecordsForwardSearchAndPreserveDefaultProviderPopulation(string? search, string? mention, string? engine,
        string? citedDomain)
    {
        var query = "?snapshotToken=list-snapshot&cursor=next&limit=25";
        if (engine is not null) query += "&engine=" + engine;
        if (search is not null) query += "&search=" + Uri.EscapeDataString(search);
        if (mention is not null) query += "&mention=" + Uri.EscapeDataString(mention);
        if (citedDomain is not null) query += "&citedDomain=" + Uri.EscapeDataString(citedDomain);
        var page = new MonitoringRecordPage(null!, [], null, 0);
        var controller = Controller(query, (method, args) =>
        {
            Assert.Equal(nameof(IAnalyticsQueryStore.MonitoringRecordsAsync), method.Name);
            Assert.Equal(10, args.Length);
            Assert.Equal(Tenant, args[0]);
            Assert.Equal(Site, args[1]);
            var filter = Assert.IsType<AnalyticsFilterRequest>(args[2]);
            Assert.Equal(new[] { "cloro", "doubao_api", "deepseek_api", "packy_qwen", "packy_glm", "packy_kimi", "packy_yuanbao", "domestic_collector" }, filter.Providers);
            Assert.Equal(engine is null ? null : new[] { engine }, filter.Engines);
            Assert.Equal(new[] { "api", "scraped" }, filter.CaptureModes);
            Assert.Equal(new[] { "api_response", "observed_surface", "unknown" }, filter.EvidenceClasses);
            Assert.Equal("list-snapshot", args[3]);
            Assert.Equal("next", args[4]);
            Assert.Equal(25, args[5]);
            Assert.Equal(search, args[6]);
            Assert.Equal(mention, args[7]);
            Assert.Equal(citedDomain, args[8]);
            Assert.Equal(CancellationToken.None, args[9]);
            return Task.FromResult(page);
        });

        var result = Assert.IsType<ObjectResult>(await controller.MonitoringRecords(Site, CancellationToken.None));

        Assert.Equal(200, result.StatusCode);
        Assert.Same(page, result.Value);
    }

    [Fact]
    public async Task ExplicitDoubaoProviderFilterIsForwardedUnchanged()
    {
        var page = new MonitoringRecordPage(null!, [], null, 0);
        var controller = Controller("?provider=doubao_api&engine=doubao", (method, args) =>
        {
            Assert.Equal(nameof(IAnalyticsQueryStore.MonitoringRecordsAsync), method.Name);
            var filter = Assert.IsType<AnalyticsFilterRequest>(args[2]);
            Assert.Equal(new[] { "doubao_api" }, filter.Providers);
            Assert.Equal(new[] { "doubao" }, filter.Engines);
            return Task.FromResult(page);
        });

        var result = Assert.IsType<ObjectResult>(await controller.MonitoringRecords(Site, CancellationToken.None));
        Assert.Equal(200, result.StatusCode);
    }

    [Theory]
    [InlineData("engine")]
    [InlineData("status")]
    [InlineData("from")]
    [InlineData("cursor")]
    [InlineData("limit")]
    public async Task DetailRejectsListFiltersInsteadOfChangingTheSnapshot(string parameter)
    {
        var controller = Controller($"?snapshotToken=list-snapshot&{parameter}=unexpected", UnexpectedQuery);

        AssertError(await controller.MonitoringAnswerDetail(Site, Check, CancellationToken.None), 400, "INVALID_FILTER");
    }

    [Theory]
    [InlineData("search=historical")]
    [InlineData("mention=mentioned")]
    public async Task MonitoringFiltersAreNotAcceptedByUnrelatedAnalyticsActions(string query)
    {
        var controller = Controller("?" + query, UnexpectedQuery);

        AssertError(await controller.Overview(Site, CancellationToken.None), 400, "INVALID_FILTER");
        AssertError(await controller.MonitoringSummary(Site, CancellationToken.None), 400, "INVALID_FILTER");
        AssertError(await controller.MonitoringModels(Site, CancellationToken.None), 400, "INVALID_FILTER");
    }

    [Theory]
    [InlineData("SNAPSHOT_REQUIRED", 400)]
    [InlineData("SNAPSHOT_EXPIRED", 409)]
    [InlineData("SNAPSHOT_REFRESH_REQUIRED", 409)]
    [InlineData("RESOURCE_NOT_FOUND", 404)]
    public async Task DetailPreservesStoreErrorCodes(string code, int status)
    {
        var controller = Controller("?snapshotToken=list-snapshot", (_, _) =>
            Task.FromException<MonitoringAnswerDetailView?>(new CollectorStoreException(code, "说明", status)));

        AssertError(await controller.MonitoringAnswerDetail(Site, Check, CancellationToken.None), status, code);
    }

    [Fact]
    public async Task MissingScopedRecordReturnsNotFound()
    {
        var controller = Controller("?snapshotToken=list-snapshot", (_, _) => Task.FromResult<MonitoringAnswerDetailView?>(null));

        AssertError(await controller.MonitoringAnswerDetail(Site, Check, CancellationToken.None), 404, "RESOURCE_NOT_FOUND");
    }

    [Fact]
    public async Task DetailRequiresAnAuthenticatedTenantInProduction()
    {
        var controller = Controller("?snapshotToken=list-snapshot", UnexpectedQuery);
        controller.HttpContext.Items.Clear();

        AssertError(await controller.MonitoringAnswerDetail(Site, Check, CancellationToken.None), 401, "AUTHENTICATION_REQUIRED");
    }

    [Fact]
    public void SwaggerDetailDeclaresOnlyTheRequiredSnapshotAndOptionalMonitoringFilters()
    {
        var operation = Operation(nameof(AnalyticsController.MonitoringAnswerDetail));
        var parameters = operation.Parameters!.Where(p => p.In == ParameterLocation.Query).ToArray();

        // sentiment / term（口碑，2026-10-30）与 citedDomain 一样只校验、不改变详情内容。
        // termPolarity（2026-11-02 起）有意声明：详情白名单接受列表的全部口碑筛选，以便从
        // 按词极性筛选的列表进入详情（见 .trellis/spec/backend/reputation.md），只校验取值且须与 term 同传。
        Assert.Equal(new[] { "citationProjection", "citedDomain", "mention", "search", "sentiment", "snapshotToken", "term", "termPolarity" },
            parameters.Select(p => p.Name).Order());
        Assert.False(parameters.Single(p => p.Name == "sentiment").Required);
        Assert.False(parameters.Single(p => p.Name == "term").Required);
        Assert.False(parameters.Single(p => p.Name == "termPolarity").Required);
        Assert.Contains("INVALID_FILTER", parameters.Single(p => p.Name == "termPolarity").Description);
        Assert.Contains("term", parameters.Single(p => p.Name == "termPolarity").Description);
        Assert.False(parameters.Single(p => p.Name == "citedDomain").Required);
        Assert.Contains("INVALID_FILTER", parameters.Single(p => p.Name == "citedDomain").Description);
        Assert.True(parameters.Single(p => p.Name == "snapshotToken").Required);
        Assert.False(parameters.Single(p => p.Name == "search").Required);
        Assert.False(parameters.Single(p => p.Name == "mention").Required);
        Assert.Contains("INVALID_FILTER", parameters.Single(p => p.Name == "mention").Description);
        Assert.Contains("matchesListFilters", parameters.Single(p => p.Name == "mention").Description);
        Assert.Contains("SNAPSHOT_REFRESH_REQUIRED", parameters.Single(p => p.Name == "snapshotToken").Description);
        Assert.Contains("忽略大小写", parameters.Single(p => p.Name == "search").Description);
        Assert.Contains("二百", parameters.Single(p => p.Name == "search").Description);
        Assert.Contains(operation.Parameters!, p => p.Name == "siteId" && p.Description == "品牌工作区标识。");
        Assert.Contains(operation.Parameters!, p => p.Name == "checkId" && p.Description == "当前品牌下的检测标识。");
        var method = typeof(AnalyticsController).GetMethod(nameof(AnalyticsController.MonitoringAnswerDetail))!;
        Assert.Equal("monitoring/records/{checkId:guid}", method.GetCustomAttribute<HttpGetAttribute>()!.Template);
        Assert.Contains(method.GetCustomAttributes<ProducesResponseTypeAttribute>(),
            p => p.StatusCode == 200 && p.Type == typeof(MonitoringAnswerDetailView));
    }

    [Fact]
    public void SwaggerSearchIsMonitoringOnlyAndListRemainsPaged()
    {
        var records = Operation(nameof(AnalyticsController.MonitoringRecords));
        Assert.Contains(records.Parameters!, p => p.Name == "search");
        Assert.Contains(records.Parameters!, p => p.Name == "mention");
        Assert.Contains(records.Parameters!, p => p.Name == "citedDomain" && p.Description!.Contains("总数"));
        Assert.Contains(records.Parameters!, p => p.Name == "cursor");
        Assert.Contains(records.Parameters!, p => p.Name == "limit");
        foreach (var action in new[] { nameof(AnalyticsController.Overview),
                     nameof(AnalyticsController.MonitoringSummary), nameof(AnalyticsController.MonitoringModels) })
            Assert.DoesNotContain(Operation(action).Parameters!, p => p.Name is "search" or "mention");
    }

    [Fact]
    public async Task DeclaredDetailErrorsMatchActualStructuredResponses()
    {
        var method = typeof(AnalyticsController).GetMethod(nameof(AnalyticsController.MonitoringAnswerDetail))!;
        var declarations = method.GetCustomAttributes<ProducesResponseTypeAttribute>().ToArray();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        foreach (var (code, status) in new[]
        {
            ("SNAPSHOT_REQUIRED", 400), ("AUTHENTICATION_REQUIRED", 401),
            ("RESOURCE_NOT_FOUND", 404), ("SNAPSHOT_REFRESH_REQUIRED", 409)
        })
        {
            Assert.Contains(declarations,
                p => p.StatusCode == status && p.Type == typeof(MonitoringAnswerErrorResponse));
            var controller = Controller("?snapshotToken=list-snapshot", (_, _) =>
                Task.FromException<MonitoringAnswerDetailView?>(new CollectorStoreException(code, "说明", status)));
            var result = Assert.IsAssignableFrom<ObjectResult>(
                await controller.MonitoringAnswerDetail(Site, Check, CancellationToken.None));
            Assert.Equal(status, result.StatusCode);
            var actual = JsonSerializer.Serialize(result.Value, options);
            var declared = new MonitoringAnswerErrorResponse(new MonitoringAnswerError(code, "说明"));
            Assert.Equal(JsonSerializer.Serialize(declared, options), actual);
        }
    }

    private static OpenApiOperation Operation(string action)
    {
        var operation = new OpenApiOperation
        {
            Parameters =
            [
                new OpenApiParameter { Name = "siteId", In = ParameterLocation.Path, Required = true },
                new OpenApiParameter { Name = "checkId", In = ParameterLocation.Path, Required = true }
            ]
        };
        var context = new OperationFilterContext(new ApiDescription(), null!, new SchemaRepository(), new OpenApiDocument(),
            typeof(AnalyticsController).GetMethod(action)!);
        new AnalyticsOpenApiOperationFilter().Apply(operation, context);
        return operation;
    }

    private static AnalyticsController Controller(string query,
        Func<MethodInfo, object?[], object?> handler)
    {
        var queries = DispatchProxy.Create<IAnalyticsQueryStore, QueryProxy>();
        ((QueryProxy)queries).Handler = handler;
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(query);
        context.Items[SessionAuthenticationMiddleware.ContextItemKey] = new AuthenticatedRequestContext(
            new AuthUserView(Guid.NewGuid(), "member@example.test", "Member", "active",
                [new TenantMembershipView(Tenant, "Team", "owner", "active")]), Guid.NewGuid(), "test-token");
        return new AnalyticsController(queries, null!, new TenantContextResolver(new StubEnvironment()))
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    private static object? UnexpectedQuery(MethodInfo method, object?[] args)
        => throw new InvalidOperationException($"Unexpected query: {method.Name}");

    private static void AssertError(IActionResult action, int status, string code)
    {
        var result = Assert.IsAssignableFrom<ObjectResult>(action);
        Assert.Equal(status, result.StatusCode);
        var payload = JsonSerializer.SerializeToElement(result.Value);
        Assert.Equal(code, payload.GetProperty("error").GetProperty("code").GetString());
    }

    public class QueryProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Handler { get; set; } = UnexpectedQuery;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => Handler(targetMethod!, args ?? []);
    }

    private sealed class StubEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
