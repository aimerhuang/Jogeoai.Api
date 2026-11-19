using System.Text.Json;
using System.Text.Json.Serialization;
using Jogeoai.Api;
using Jogeoai.Api.Controllers;
using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;

namespace Jogeoai.Tests;

/// <summary>无库 API 测试：/site-icons/{domain} 的登录要求、状态码、响应头与 Swagger 中文说明。</summary>
public sealed class SiteIconControllerTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task AnonymousRequestsGet401WithoutLookup()
    {
        var f = new Fixture(authenticated: false);

        var result = await f.Controller.Get("example.com", default);

        AssertError(result, 401, "AUTHENTICATION_REQUIRED");
        Assert.Empty(f.Service.Calls);
        AssertSecurityHeaders(f.Context);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("metadata.google.internal")]
    [InlineData("bad_host.com")]
    [InlineData("例子.com")]
    public async Task InvalidDomainsGet400WithoutLookup(string domain)
    {
        var f = new Fixture();

        AssertError(await f.Controller.Get(domain, default), 400, "INVALID_SITE_DOMAIN");
        Assert.Empty(f.Service.Calls);
        AssertSecurityHeaders(f.Context);
    }

    [Fact]
    public async Task FoundIconReturnsBytesWithPrivateWeekCache()
    {
        var f = new Fixture();
        var bytes = SiteIconTestImages.Png(32);
        f.Service.Result = new SiteIconRecord("example.com", SiteIconStatus.Found, "image/png", bytes, "html",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30));

        var result = Assert.IsType<FileContentResult>(await f.Controller.Get("Example.COM.", default));

        Assert.Equal("image/png", result.ContentType);
        Assert.Equal(bytes, result.FileContents);
        Assert.Equal("example.com", Assert.Single(f.Service.Calls));
        Assert.Equal("private, max-age=604800", f.Context.Response.Headers.CacheControl.ToString());
        AssertSecurityHeaders(f.Context);
    }

    [Fact]
    public async Task MissingIconReturns404WithPrivateNoCache()
    {
        var f = new Fixture();
        f.Service.Result = new SiteIconRecord("example.com", SiteIconStatus.Missing, null, null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(7));

        AssertError(await f.Controller.Get("example.com", default), 404, "SITE_ICON_NOT_FOUND");
        Assert.Equal("private, no-cache", f.Context.Response.Headers.CacheControl.ToString());
        AssertSecurityHeaders(f.Context);
    }

    [Fact]
    public async Task ServiceValidationErrorsKeepTheirStableCode()
    {
        var f = new Fixture();
        f.Service.Failure = new CollectorStoreException("INVALID_SITE_DOMAIN", "站点域名不合法。", 400);

        AssertError(await f.Controller.Get("example.com", default), 400, "INVALID_SITE_DOMAIN");
    }

    [Fact]
    public async Task RouteIsDocumentedInChineseWithImageResponse()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(SiteIconsController).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production,
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddControllers().AddApplicationPart(typeof(SiteIconsController).Assembly)
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
            var operation = swagger.RootElement.GetProperty("paths").GetProperty("/api/v1/site-icons/{domain}").GetProperty("get");
            Assert.True(HasChinese(operation.GetProperty("summary").GetString()));
            Assert.True(HasChinese(operation.GetProperty("description").GetString()));
            var parameter = Assert.Single(operation.GetProperty("parameters").EnumerateArray());
            Assert.Equal("domain", parameter.GetProperty("name").GetString());
            Assert.True(HasChinese(parameter.GetProperty("description").GetString()));
            var responses = operation.GetProperty("responses");
            var content = responses.GetProperty("200").GetProperty("content");
            foreach (var type in new[] { "image/png", "image/x-icon", "image/gif", "image/jpeg", "image/webp", "image/bmp" })
                Assert.True(content.TryGetProperty(type, out _), $"200 缺少 {type}");
            Assert.Equal("binary", content.GetProperty("image/png").GetProperty("schema").GetProperty("format").GetString());
            foreach (var status in new[] { "200", "400", "401", "404" })
                Assert.True(HasChinese(responses.GetProperty(status).GetProperty("description").GetString()), $"{status} 缺少中文说明");
        }
        finally { await app.StopAsync(); }
    }

    private static void AssertSecurityHeaders(HttpContext context)
    {
        Assert.Equal("nosniff", context.Response.Headers.XContentTypeOptions.ToString());
        Assert.Equal("default-src 'none'", context.Response.Headers.ContentSecurityPolicy.ToString());
    }

    private static void AssertError(IActionResult result, int status, string code)
    {
        var response = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, JsonSerializer.SerializeToElement(response.Value, Json).GetProperty("error").GetProperty("code").GetString());
    }

    private static bool HasChinese(string? value) => value?.Any(character => character is >= '一' and <= '鿿') == true;

    private sealed class Fixture
    {
        public DefaultHttpContext Context { get; } = new();
        public FakeService Service { get; } = new();
        public SiteIconsController Controller { get; }

        public Fixture(bool authenticated = true)
        {
            if (authenticated)
                Context.Items[SessionAuthenticationMiddleware.ContextItemKey] = new AuthenticatedRequestContext(
                    new(Guid.NewGuid(), "user@example.test", "User", "active", [new TenantMembershipView(Guid.NewGuid(), "团队", "viewer", "active")]),
                    Guid.NewGuid(), "session");
            Controller = new SiteIconsController(Service) { ControllerContext = new() { HttpContext = Context } };
        }
    }

    private sealed class FakeService : ISiteIconService
    {
        public List<string> Calls { get; } = [];
        public SiteIconRecord? Result { get; set; }
        public Exception? Failure { get; set; }

        public Task<SiteIconRecord> GetAsync(string domain, CancellationToken cancellationToken = default)
        {
            Calls.Add(domain);
            if (Failure is not null) return Task.FromException<SiteIconRecord>(Failure);
            return Task.FromResult(Result ?? throw new InvalidOperationException("No result configured."));
        }
    }
}
