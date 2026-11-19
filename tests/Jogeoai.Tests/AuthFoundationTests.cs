using Jogeoai.Api;
using Jogeoai.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Jogeoai.Tests;

public sealed class AuthFoundationTests
{
    [Fact]
    public void DevelopmentHeaderRemainsACompatibilityBridgeWithoutASession()
    {
        var tenantId = Guid.NewGuid();
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Tenant-Id"] = tenantId.ToString();

        var resolver = new TenantContextResolver(new StubHostEnvironment("Development"));

        Assert.Equal(tenantId, resolver.Resolve(context));
    }

    [Fact]
    public void AuthenticatedRequestsMustBelongToTheRequestedTenant()
    {
        var memberTenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Tenant-Id"] = otherTenant.ToString();
        context.Items[SessionAuthenticationMiddleware.ContextItemKey] = new AuthenticatedRequestContext(
            new AuthUserView(Guid.NewGuid(), "member@example.test", "Member", "active",
                [new TenantMembershipView(memberTenant, "Member team", "owner", "active")]),
            Guid.NewGuid(), "opaque-token");

        var resolver = new TenantContextResolver(new StubHostEnvironment("Production"));

        var exception = Assert.Throws<CollectorStoreException>(() => resolver.Resolve(context));
        Assert.Equal("TENANT_FORBIDDEN", exception.Code);
        Assert.Equal(403, exception.StatusCode);
    }

    [Fact]
    public void ProductionRequestsWithoutASessionFailClosed()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Tenant-Id"] = Guid.NewGuid().ToString();

        var resolver = new TenantContextResolver(new StubHostEnvironment("Production"));

        var exception = Assert.Throws<CollectorStoreException>(() => resolver.Resolve(context));
        Assert.Equal("AUTHENTICATION_REQUIRED", exception.Code);
        Assert.Equal(401, exception.StatusCode);
    }

    [Fact]
    public void ProductionRequestsWithMalformedTenantHeaderStillRequireAuthentication()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Tenant-Id"] = "not-a-guid";

        var resolver = new TenantContextResolver(new StubHostEnvironment("Production"));

        var exception = Assert.Throws<CollectorStoreException>(() => resolver.Resolve(context));
        Assert.Equal("AUTHENTICATION_REQUIRED", exception.Code);
        Assert.Equal(401, exception.StatusCode);
    }

    [Fact]
    public void BearerTokenTakesPrecedenceOverCookie()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer bearer-token";
        context.Request.Headers.Cookie = "jogeoai_session=cookie-token";

        Assert.Equal("bearer-token", SessionAuthenticationMiddleware.ReadToken(context));
    }

    private sealed class StubHostEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public string WebRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
