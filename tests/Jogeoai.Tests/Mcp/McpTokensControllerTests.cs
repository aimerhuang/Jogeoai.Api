using System.Text.Json;
using Jogeoai.Api;
using Jogeoai.Api.Controllers;
using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Jogeoai.Tests.Mcp;

/// <summary>
/// 无库控制器测试：令牌接口只允许当前租户 owner / admin 且必须有真实会话；member / viewer 403、无会话 401（开发环境租户桥也不放行）；
/// 跨租户站点 400；上限 409 与缺表 503 原样输出；明文只在创建响应出现，Store 只收到哈希与前缀；列表 JSON 不含明文和哈希。
/// </summary>
public sealed class McpTokensControllerTests
{
    [Theory]
    [InlineData("owner")]
    [InlineData("admin")]
    public async Task OwnerAndAdminCreateTokenAndOnlyHashReachesStore(string role)
    {
        var f = new Fixture(role);

        var result = Assert.IsType<ObjectResult>(await f.Controller.Create(new McpTokenCreateRequest(" Grok Bot ", 30), default));
        Assert.Equal(201, result.StatusCode);
        var created = Assert.IsType<McpTokenCreatedView>(result.Value);
        Assert.True(McpTokenCodec.IsWellFormed(created.Token));
        Assert.Equal(created.Token[..12], created.TokenPrefix);
        Assert.Equal("Grok Bot", created.Name);
        Assert.Null(created.SiteIds);

        var stored = Assert.Single(f.Created);
        Assert.Equal(f.Tenant, stored.TenantId);
        Assert.Equal(f.User, stored.CreatedByUserId);
        Assert.Equal(McpTokenCodec.Hash(created.Token), stored.TokenHash);
        Assert.Equal(created.TokenPrefix, stored.TokenPrefix);
        Assert.Equal(TimeSpan.FromDays(30), stored.ExpiresAt - stored.CreatedAt);
        Assert.DoesNotContain(created.Token, JsonSerializer.Serialize(stored), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("member")]
    [InlineData("viewer")]
    public async Task MemberAndViewerGet403WithoutStoreCalls(string role)
    {
        var f = new Fixture(role);

        AssertError(await f.Controller.Create(new McpTokenCreateRequest("bot", 30), default), 403, "MCP_TOKEN_FORBIDDEN");
        AssertError(await f.Controller.List(default), 403, "MCP_TOKEN_FORBIDDEN");
        AssertError(await f.Controller.Revoke(Guid.NewGuid(), default), 403, "MCP_TOKEN_FORBIDDEN");
        Assert.Empty(f.TokenProxy.Calls);
        Assert.Empty(f.ManagementProxy.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoSessionIs401EvenWithDevelopmentTenantHeader(bool development)
    {
        var f = new Fixture(null, development);
        f.Controller.HttpContext.Request.Headers["X-Tenant-Id"] = f.Tenant.ToString();

        AssertError(await f.Controller.Create(new McpTokenCreateRequest("bot", 30), default), 401, "AUTHENTICATION_REQUIRED");
        AssertError(await f.Controller.List(default), 401, "AUTHENTICATION_REQUIRED");
        AssertError(await f.Controller.Revoke(Guid.NewGuid(), default), 401, "AUTHENTICATION_REQUIRED");
        Assert.Empty(f.TokenProxy.Calls);
    }

    [Fact]
    public async Task SiteScopeMustBelongToTenant()
    {
        var f = new Fixture("owner");
        var own = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        f.OwnSites.Add(own);

        AssertError(await f.Controller.Create(new McpTokenCreateRequest("bot", 30, [own, foreign]), default), 400, "MCP_TOKEN_SITE_INVALID");
        AssertError(await f.Controller.Create(new McpTokenCreateRequest("bot", 30, []), default), 400, "VALIDATION_FAILED");
        AssertError(await f.Controller.Create(new McpTokenCreateRequest("bot", 30, [Guid.Empty]), default), 400, "MCP_TOKEN_SITE_INVALID");
        Assert.Empty(f.Created);

        var ok = Assert.IsType<ObjectResult>(await f.Controller.Create(new McpTokenCreateRequest("bot", 30, [own, own]), default));
        Assert.Equal(new[] { own }, Assert.IsType<McpTokenCreatedView>(ok.Value).SiteIds);
        Assert.All(f.ManagementProxy.Calls, call => Assert.Equal(f.Tenant, call.Args[0]));
    }

    [Theory]
    [InlineData("", 30)]
    [InlineData("   ", 30)]
    [InlineData("bot", 0)]
    [InlineData("bot", 366)]
    public async Task InvalidNameOrExpiryIs400(string name, int days)
    {
        var f = new Fixture("admin");
        AssertError(await f.Controller.Create(new McpTokenCreateRequest(name, days), default), 400, "VALIDATION_FAILED");
        Assert.Empty(f.Created);
    }

    [Fact]
    public async Task NameOverHundredCharactersIs400AndBoundariesPass()
    {
        var f = new Fixture("admin");
        AssertError(await f.Controller.Create(new McpTokenCreateRequest(new string('名', 101), 30), default), 400, "VALIDATION_FAILED");
        Assert.IsType<ObjectResult>(await f.Controller.Create(new McpTokenCreateRequest(new string('名', 100), 365), default));
        Assert.IsType<ObjectResult>(await f.Controller.Create(new McpTokenCreateRequest("x", 1), default));
    }

    [Theory]
    [InlineData("MCP_TOKEN_LIMIT_REACHED", 409)]
    [InlineData("MCP_SCHEMA_NOT_MIGRATED", 503)]
    public async Task StoreErrorsKeepTheirStatus(string code, int status)
    {
        var f = new Fixture("owner") { Failure = new CollectorStoreException(code, "测试错误。", status) };
        AssertError(await f.Controller.Create(new McpTokenCreateRequest("bot", 30), default), status, code);
        AssertError(await f.Controller.List(default), status, code);
        AssertError(await f.Controller.Revoke(Guid.NewGuid(), default), status, code);
    }

    [Fact]
    public async Task ListNeverSerializesTokenOrHashAndCountsActive()
    {
        var f = new Fixture("owner");
        var now = DateTimeOffset.UtcNow;
        f.Listed.Add(new McpTokenView(Guid.NewGuid(), "a", "jgm_abcdefgh", null, f.User, now, now.AddDays(1), null, null, "active"));
        f.Listed.Add(new McpTokenView(Guid.NewGuid(), "b", "jgm_ijklmnop", [Guid.NewGuid()], f.User, now, now.AddDays(1), now, now, "revoked"));

        var ok = Assert.IsType<OkObjectResult>(await f.Controller.List(default));
        var list = Assert.IsType<McpTokenListView>(ok.Value);
        Assert.Equal(2, list.Items.Count);
        Assert.Equal(1, list.ActiveCount);
        Assert.Equal(20, list.ActiveLimit);
        var json = JsonSerializer.Serialize(list);
        Assert.DoesNotContain("\"Token\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Hash", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(typeof(McpTokenView).GetProperties(), property =>
            property.Name.Contains("Hash", StringComparison.OrdinalIgnoreCase) || property.Name == "Token");
    }

    [Fact]
    public async Task RevokeIs204AndUnknownOrForeignTokenIs404()
    {
        var f = new Fixture("admin");
        var id = Guid.NewGuid();
        f.Revocable.Add(id);

        Assert.IsType<NoContentResult>(await f.Controller.Revoke(id, default));
        Assert.IsType<NoContentResult>(await f.Controller.Revoke(id, default));
        AssertError(await f.Controller.Revoke(Guid.NewGuid(), default), 404, "MCP_TOKEN_NOT_FOUND");
        Assert.All(f.TokenProxy.Calls, call => Assert.Equal(f.Tenant, call.Args[0]));
    }

    private static void AssertError(IActionResult result, int status, string code)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(status, objectResult.StatusCode);
        Assert.Contains($"\"code\":\"{code}\"", JsonSerializer.Serialize(objectResult.Value), StringComparison.Ordinal);
    }

    private sealed class Fixture
    {
        internal Fixture(string? role, bool development = false)
        {
            (var tokens, TokenProxy) = McpStoreProxy.Create<IMcpTokenStore>();
            (var management, ManagementProxy) = McpStoreProxy.Create<IManagementStore>();
            TokenProxy.Handlers[nameof(IMcpTokenStore.CreateAsync)] = args =>
            {
                if (Failure is not null) throw Failure;
                var request = (McpTokenCreate)args[0]!;
                Created.Add(request);
                return Task.FromResult(new McpTokenView(request.TokenId, request.Name, request.TokenPrefix, request.SiteIds,
                    request.CreatedByUserId, request.CreatedAt, request.ExpiresAt, null, null, "active"));
            };
            TokenProxy.Handlers[nameof(IMcpTokenStore.ListAsync)] = _ =>
                Failure is not null ? throw Failure : Task.FromResult<IReadOnlyList<McpTokenView>>(Listed);
            TokenProxy.Handlers[nameof(IMcpTokenStore.RevokeAsync)] = args =>
            {
                if (Failure is not null) throw Failure;
                var id = (Guid)args[1]!;
                var now = DateTimeOffset.UtcNow;
                return Task.FromResult(Revocable.Contains(id)
                    ? new McpTokenView(id, "bot", "jgm_abcdefgh", null, User, now, now.AddDays(1), now, null, "revoked")
                    : null);
            };
            ManagementProxy.Handlers[nameof(IManagementStore.GetBrandAsync)] = args =>
                Task.FromResult(OwnSites.Contains((Guid)args[1]!) ? McpFixtures.Brand((Guid)args[1]!) : null);

            var context = new DefaultHttpContext();
            if (role is not null)
                context.Items[SessionAuthenticationMiddleware.ContextItemKey] = new AuthenticatedRequestContext(
                    new(User, "fixture@example.test", "Fixture", "active", [new(Tenant, "Team", role, "active")]), Guid.NewGuid(), "fixture");
            Controller = new McpTokensController(tokens, management,
                new TenantContextResolver(new TestEnvironment(development ? Environments.Development : Environments.Production)))
            { ControllerContext = new() { HttpContext = context } };
        }

        internal Guid Tenant { get; } = Guid.NewGuid();
        internal Guid User { get; } = Guid.NewGuid();
        internal McpTokensController Controller { get; }
        internal McpStoreProxy TokenProxy { get; }
        internal McpStoreProxy ManagementProxy { get; }
        internal List<McpTokenCreate> Created { get; } = [];
        internal List<McpTokenView> Listed { get; } = [];
        internal HashSet<Guid> Revocable { get; } = [];
        internal HashSet<Guid> OwnSites { get; } = [];
        internal Exception? Failure { get; set; }
    }

    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
