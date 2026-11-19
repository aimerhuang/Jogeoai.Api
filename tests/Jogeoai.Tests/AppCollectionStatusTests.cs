using Jogeoai.Api;
using Jogeoai.Api.Controllers;
using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Jogeoai.Tests;

/// <summary>GET /sites/{siteId}/app-collection/status 的接口层（无库）：所有角色可读（含 viewer），租户来自会话，错误保留状态码。</summary>
public sealed class AppCollectionStatusTests
{
    [Theory]
    [InlineData("viewer")]
    [InlineData("member")]
    [InlineData("owner")]
    public async Task EveryRoleReadsTheStatusOfItsOwnTenant(string role)
    {
        var query = new StubStatus();
        var site = Guid.NewGuid();
        var controller = Controller(query, role, out var tenant);
        var ok = Assert.IsType<OkObjectResult>((await controller.Get(site, default)).Result);
        Assert.Same(StubStatus.View, ok.Value);
        Assert.Equal((tenant, site), query.Last);
    }

    [Fact]
    public async Task StoreErrorsKeepTheirStatusAndCode()
    {
        var query = new StubStatus { Failure = new CollectorStoreException("RESOURCE_NOT_FOUND", "品牌不存在或已归档。", 404) };
        var result = Assert.IsType<ObjectResult>((await Controller(query, "viewer", out _).Get(Guid.NewGuid(), default)).Result);
        Assert.Equal(404, result.StatusCode);
        Assert.Contains("RESOURCE_NOT_FOUND", System.Text.Json.JsonSerializer.Serialize(result.Value), StringComparison.Ordinal);
        // Without a session (production): 401 before the query is reached.
        var anonymous = new AppCollectionStatusController(query, new TenantContextResolver(new TestEnvironment()))
            { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        query.Last = null;
        Assert.Equal(401, Assert.IsType<ObjectResult>((await anonymous.Get(Guid.NewGuid(), default)).Result).StatusCode);
        Assert.Null(query.Last);
    }

    [Fact]
    public void TheRouteIsRegisteredAndDocumented()
    {
        var route = typeof(AppCollectionStatusController).GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>().Single();
        var get = typeof(AppCollectionStatusController).GetMethod(nameof(AppCollectionStatusController.Get))!
            .GetCustomAttributes(typeof(HttpGetAttribute), false).Cast<HttpGetAttribute>().Single();
        Assert.Equal("api/v1/sites/{siteId:guid}/app-collection/status", $"{route.Template}/{get.Template}");
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Jogeoai.slnx"))) directory = directory.Parent;
        var program = File.ReadAllText(Path.Combine(directory!.FullName, "src", "Jogeoai.Api", "Program.cs"));
        Assert.Contains("builder.Services.AddSingleton<IAppCollectionStatusQuery, AppCollectionStatusQuery>();", program, StringComparison.Ordinal);
        Assert.Contains("builder.Services.AddSingleton<IAppCapacityQuery, AppCapacityQuery>();", program, StringComparison.Ordinal);
        var xml = System.Xml.Linq.XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Jogeoai.Api.xml"));
        var summary = xml.Descendants("member").Single(m => ((string?)m.Attribute("name"))?.StartsWith(
            "M:Jogeoai.Api.Controllers.AppCollectionStatusController.Get(", StringComparison.Ordinal) == true).Element("summary")!.Value;
        Assert.Contains("viewer", summary, StringComparison.Ordinal);
        Assert.Contains(summary, c => c is >= '一' and <= '鿿');
    }

    private static AppCollectionStatusController Controller(StubStatus query, string role, out Guid tenant)
    {
        tenant = Guid.NewGuid();
        var context = new DefaultHttpContext();
        context.Items[SessionAuthenticationMiddleware.ContextItemKey] = new AuthenticatedRequestContext(
            new AuthUserView(Guid.NewGuid(), "qa@example.test", "QA", "active", [new(tenant, "QA", role, "active")]), Guid.NewGuid(), "test-only");
        return new AppCollectionStatusController(query, new TenantContextResolver(new TestEnvironment()))
            { ControllerContext = new ControllerContext { HttpContext = context } };
    }

    private sealed class StubStatus : IAppCollectionStatusQuery
    {
        public static readonly AppCollectionStatusView View = new(true, []);
        public (Guid Tenant, Guid Site)? Last;
        public CollectorStoreException? Failure;

        public Task<AppCollectionStatusView> GetAsync(Guid tenantId, Guid siteId, CancellationToken cancellationToken = default)
        {
            Last = (tenantId, siteId);
            if (Failure is not null) throw Failure;
            return Task.FromResult(View);
        }
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
