using System.Reflection;
using Jogeoai.Api;
using Jogeoai.Api.Controllers;
using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Jogeoai.Tests;

public sealed class GeminiTargetApiTests
{
    [Theory]
    [InlineData("gemini", "viewer", 403)]
    [InlineData("gemini", "owner", 200)]
    [InlineData("gemini", "member", 200)]
    [InlineData("gemini", null, 401)]
    [InlineData("copilot", "viewer", 403)]
    [InlineData("copilot", "owner", 200)]
    [InlineData("copilot", "member", 200)]
    [InlineData("copilot", null, 401)]
    [InlineData("google-ai-overview", "viewer", 403)]
    [InlineData("google-ai-overview", "owner", 200)]
    [InlineData("google-ai-overview", "member", 200)]
    [InlineData("google-ai-overview", null, 401)]
    public async Task TargetWritesReuseTenantWriterGuard(string engine, string? role, int status)
    {
        var tenant = Guid.NewGuid();
        var store = DispatchProxy.Create<IManagementStore, TargetProxy>();
        var proxy = (TargetProxy)(object)store;
        var controller = Controller(store, tenant, role);
        var result = await controller.PutTargets(Guid.NewGuid(), new(1, [new("cloro", engine, "scraped", "US", "zh-CN")]), default);
        Assert.Equal(status, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        Assert.Equal(status == 200 ? 1 : 0, proxy.Calls);
        if (status == 200)
        {
            Assert.Equal(tenant, proxy.Tenant);
            Assert.Equal(1u, proxy.Request!.ExpectedVersion);
            Assert.Equal(engine, Assert.Single(proxy.Request.Targets).Engine);
        }
    }

    [Fact]
    public async Task ForeignTenantHeaderCannotChangeTargets()
    {
        var store = DispatchProxy.Create<IManagementStore, TargetProxy>();
        var controller = Controller(store, Guid.NewGuid(), "owner");
        controller.HttpContext.Request.Headers["X-Tenant-Id"] = Guid.NewGuid().ToString();
        var result = await controller.PutTargets(Guid.NewGuid(), new(1, []), default);
        Assert.Equal(403, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal(0, ((TargetProxy)(object)store).Calls);
    }

    private static ManagementController Controller(IManagementStore store, Guid tenant, string? role)
    {
        var context = new DefaultHttpContext();
        if (role is not null) context.Items[SessionAuthenticationMiddleware.ContextItemKey] = new AuthenticatedRequestContext(
            new AuthUserView(Guid.NewGuid(), "fixture@example.test", "Fixture", "active", [new(tenant, "Tenant", role, "active")]), Guid.NewGuid(), "fixture");
        return new(store, new TenantContextResolver(new Production())) { ControllerContext = new() { HttpContext = context } };
    }

    public class TargetProxy : DispatchProxy
    {
        public int Calls;
        public Guid Tenant;
        public SiteTargetsWrite? Request;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Assert.Equal(nameof(IManagementStore.PutSiteTargetsAsync), method!.Name);
            Calls++; Tenant = (Guid)args![0]!; Request = (SiteTargetsWrite)args[2]!;
            return Task.FromResult(new SiteTargetsView(2, []));
        }
    }

    private sealed class Production : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
