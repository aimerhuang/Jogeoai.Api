using System.Text.Json;
using Jogeoai.Api;
using Jogeoai.Api.Controllers;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Tests.Mcp;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Jogeoai.Tests;

/// <summary>
/// 内容自有标记接口：viewer → 403 CONTENT_FORBIDDEN 且 Store 零调用；同租户 member → 原样转发并返回 200；
/// 外租户 X-Tenant-Id → 403 TENANT_FORBIDDEN；Store 业务错误按原状态码返回。直接构造 Controller，不连数据库。
/// </summary>
public sealed class ContentOwnedUrlsApiTests
{
    private static readonly string Hash = CitationDomainAccumulator.UrlHash("https://zhihu.com/q/1");

    public static TheoryData<string> Actions => new() { "mark", "unmark" };

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task ViewerIsForbiddenBeforeAnyStoreCall(string action)
    {
        var fixture = new Fixture("viewer");
        AssertError(await fixture.InvokeAsync(action), StatusCodes.Status403Forbidden, "CONTENT_FORBIDDEN");
        Assert.Empty(fixture.Proxy.Calls);
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task ForeignTenantHeaderIsRejectedBeforeAnyStoreCall(string action)
    {
        var fixture = new Fixture("member");
        fixture.Context.Request.Headers["X-Tenant-Id"] = Guid.NewGuid().ToString();
        AssertError(await fixture.InvokeAsync(action), StatusCodes.Status403Forbidden, "TENANT_FORBIDDEN");
        Assert.Empty(fixture.Proxy.Calls);
    }

    [Theory]
    [InlineData("member")]
    [InlineData("admin")]
    [InlineData("owner")]
    public async Task WritersMarkAndUnmarkWithTheirTenantAndUser(string role)
    {
        var fixture = new Fixture(role);
        var article = Guid.NewGuid();
        fixture.Proxy.Handlers[nameof(IContentStore.MarkOwnedUrlAsync)] = _ => Task.FromResult(
            new OwnedUrlMarkResult(Hash, "https://zhihu.com/q/1", true, article, OwnedUrlMarkOutcome.Created));
        fixture.Proxy.Handlers[nameof(IContentStore.UnmarkOwnedUrlAsync)] = _ => Task.FromResult(
            new OwnedUrlMarkResult(Hash, "https://zhihu.com/q/1", false, article, OwnedUrlMarkOutcome.Disabled));

        var marked = Assert.IsType<OkObjectResult>(await fixture.InvokeAsync("mark"));
        Assert.Equal(OwnedUrlMarkOutcome.Created, Assert.IsType<OwnedUrlMarkResult>(marked.Value).Outcome);
        var unmarked = Assert.IsType<OkObjectResult>(await fixture.InvokeAsync("unmark"));
        Assert.Equal(OwnedUrlMarkOutcome.Disabled, Assert.IsType<OwnedUrlMarkResult>(unmarked.Value).Outcome);

        var calls = fixture.Proxy.Calls.ToArray();
        Assert.Equal(new object?[] { fixture.Tenant, fixture.Site, fixture.User, "https://www.zhihu.com/q/1?utm_source=x", "问题页", CancellationToken.None },
            calls[0].Args);
        Assert.Equal(new object?[] { fixture.Tenant, fixture.Site, fixture.User, Hash, CancellationToken.None }, calls[1].Args);
        // Serialized result uses the documented camelCase fields and string outcome.
        var json = JsonSerializer.SerializeToElement(marked.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
        Assert.Equal("created", json.GetProperty("outcome").GetString());
        Assert.Equal(Hash, json.GetProperty("urlHash").GetString());
        Assert.True(json.GetProperty("isMine").GetBoolean());
    }

    [Theory]
    [InlineData("INVALID_SOURCE_URL", 400)]
    [InlineData("INVALID_URL_HASH", 400)]
    [InlineData("RESOURCE_NOT_FOUND", 404)]
    public async Task StoreErrorsKeepTheirCodeAndStatus(string code, int status)
    {
        var fixture = new Fixture("member");
        fixture.Proxy.Handlers[nameof(IContentStore.MarkOwnedUrlAsync)] = _ =>
            Task.FromException<OwnedUrlMarkResult>(new CollectorStoreException(code, "说明", status));
        fixture.Proxy.Handlers[nameof(IContentStore.UnmarkOwnedUrlAsync)] = _ =>
            Task.FromException<OwnedUrlMarkResult>(new CollectorStoreException(code, "说明", status));
        AssertError(await fixture.InvokeAsync("mark"), status, code);
        AssertError(await fixture.InvokeAsync("unmark"), status, code);
    }

    private static void AssertError(IActionResult result, int status, string code)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(status, objectResult.StatusCode);
        Assert.Equal(code, JsonSerializer.SerializeToElement(objectResult.Value).GetProperty("error").GetProperty("code").GetString());
    }

    private sealed class Fixture
    {
        private readonly IContentStore store;

        internal Fixture(string role)
        {
            (store, Proxy) = McpStoreProxy.Create<IContentStore>();
            Context.Items[SessionAuthenticationMiddleware.ContextItemKey] = new AuthenticatedRequestContext(
                new(User, "owned@example.test", "Owned", "active", [new TenantMembershipView(Tenant, "Team", role, "active")]),
                Guid.NewGuid(), "owned-session");
        }

        internal McpStoreProxy Proxy { get; }
        internal Guid Tenant { get; } = Guid.NewGuid();
        internal Guid Site { get; } = Guid.NewGuid();
        internal Guid User { get; } = Guid.NewGuid();
        internal DefaultHttpContext Context { get; } = new();

        internal Task<IActionResult> InvokeAsync(string action)
        {
            var controller = new ContentOwnedUrlsController(store, new TenantContextResolver(new TestEnvironment()))
            { ControllerContext = new ControllerContext { HttpContext = Context } };
            return action == "mark"
                ? controller.Mark(Site, new OwnedUrlMarkRequest("https://www.zhihu.com/q/1?utm_source=x", "问题页"), CancellationToken.None)
                : controller.Unmark(Site, Hash, CancellationToken.None);
        }
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
