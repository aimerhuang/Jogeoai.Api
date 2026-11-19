using System.Reflection;
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
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;

namespace Jogeoai.Tests;

/// <summary>无库 API 测试：/team/summary 与 /credits/usage 的角色矩阵、参数解析，以及 Swagger 中文与新字段。</summary>
public sealed class TeamApiDocumentationTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task ViewerIsRejectedFromUsageButCanReadTheTeamSummary()
    {
        var viewer = new Fixture(role: "viewer");
        AssertError(await viewer.Credits.Usage(null, null, null, default), 403, "CONTENT_FORBIDDEN");
        Assert.Empty(viewer.UsageCalls);
        var summary = Assert.IsType<OkObjectResult>(await viewer.Team.Summary(null, null, null, null, null, null, default));
        Assert.IsType<TeamSummaryView>(summary.Value);
        var request = Assert.Single(viewer.SummaryCalls);
        Assert.Equal((viewer.TenantId, viewer.UserId), (request.TenantId, request.UserId));

        var anonymous = new Fixture(authenticated: false);
        AssertError(await anonymous.Credits.Usage(null, null, null, default), 401, "AUTHENTICATION_REQUIRED");
        AssertError(await anonymous.Team.Summary(null, null, null, null, null, null, default), 401, "AUTHENTICATION_REQUIRED");
        Assert.Empty(anonymous.UsageCalls);
        Assert.Empty(anonymous.SummaryCalls);
    }

    [Fact]
    public async Task MemberScopeIsForwardedAndDatesAreParsed()
    {
        var member = new Fixture(role: "member");
        Assert.IsType<OkObjectResult>(await member.Credits.Usage("2026-10-01", "2026-10-20", " Asia/Shanghai ", default));
        var call = Assert.Single(member.UsageCalls);
        Assert.Equal(member.UserId, call.Actor);
        Assert.Equal(new CreditUsageRequest(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 20), "Asia/Shanghai"), call.Request);

        var owner = new Fixture(role: "owner");
        Assert.IsType<OkObjectResult>(await owner.Credits.Usage(null, null, null, default));
        Assert.Null(Assert.Single(owner.UsageCalls).Actor);
        var admin = new Fixture(role: "admin");
        Assert.IsType<OkObjectResult>(await admin.Credits.Usage(null, null, null, default));
        Assert.Null(Assert.Single(admin.UsageCalls).Actor);

        AssertError(await owner.Credits.Usage("2026/10/01", null, null, default), 400, "INVALID_TIME_RANGE");
        AssertError(await owner.Team.Summary("2026-10-01", null, null, null, null, null, default), 400, "INVALID_TIME_RANGE");
        Assert.Single(owner.UsageCalls);
        Assert.Empty(owner.SummaryCalls);

        var siteId = Guid.NewGuid();
        Assert.IsType<OkObjectResult>(await owner.Team.Summary("2026-10-01T00:00:00+08:00", "2026-10-08T00:00:00Z", "UTC", "cloro", "chatgpt", siteId, default));
        var summary = Assert.Single(owner.SummaryCalls).Request;
        Assert.Equal(DateTimeOffset.Parse("2026-09-30T16:00:00Z"), summary.From);
        Assert.Equal(DateTimeOffset.Parse("2026-10-08T00:00:00Z"), summary.To);
        Assert.Equal(("UTC", "cloro", "chatgpt", siteId), (summary.TimeZone, summary.Provider, summary.Engine, summary.SiteId));
    }

    [Fact]
    public async Task StoreErrorsKeepTheirStableCodes()
    {
        var f = new Fixture();
        f.Failure = new CollectorStoreException("INVALID_TIME_ZONE", "timeZone 必须是有效的 IANA 时区。", 400);
        AssertError(await f.Credits.Usage(null, null, "Mars/Olympus", default), 400, "INVALID_TIME_ZONE");
        AssertError(await f.Team.Summary(null, null, "Mars/Olympus", null, null, null, default), 400, "INVALID_TIME_ZONE");
    }

    [Fact]
    public async Task TeamAndUsageRoutesAreDocumentedInChineseWithNewSchemas()
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
            foreach (var (path, parameters) in new[]
            {
                ("/api/v1/team/summary", new[] { "from", "to", "timeZone", "provider", "engine", "siteId" }),
                ("/api/v1/credits/usage", new[] { "from", "to", "timeZone" }),
            })
            {
                var operation = paths.GetProperty(path).GetProperty("get");
                Assert.True(HasChinese(operation.GetProperty("summary").GetString()), $"get {path}");
                var declared = operation.GetProperty("parameters").EnumerateArray()
                    .ToDictionary(p => p.GetProperty("name").GetString()!, p => p);
                foreach (var name in parameters)
                {
                    Assert.True(declared.ContainsKey(name), $"{path} 缺少参数 {name}");
                    Assert.True(HasChinese(FindDescription(declared[name])), $"{path}?{name} 缺少中文说明");
                }
            }
            var schemas = swagger.RootElement.GetProperty("components").GetProperty("schemas");
            var recognition = paths.GetProperty("/api/v1/brands/recognize").GetProperty("post");
            Assert.True(HasChinese(recognition.GetProperty("summary").GetString()));
            Assert.True(recognition.GetProperty("responses").TryGetProperty("200", out _));
            foreach (var (schema, property) in new[]
            {
                ("TeamSummaryView", "brands"), ("TeamSummaryView", "enabledEngines"), ("TeamSummaryView", "totals"),
                ("TeamBrandRow", "mentionRate"), ("TeamBrandRow", "industryRank"), ("TeamBrandRow", "enabledPromptCount"),
                ("TeamBrandRow", "enabledTargets"), ("PeriodMetric", "difference"), ("PeriodMetric", "unavailableReason"),
                ("TeamBrandArticles", "cited"), ("TeamSummaryTotals", "enabledEngineCount"), ("TeamSummaryRange", "previousFrom"),
                ("CreditUsageView", "days"), ("CreditUsageView", "latest"), ("CreditUsageView", "activeDays"),
                ("CreditUsageDay", "items"), ("CreditUsageItem", "count"), ("CreditUsageLatest", "occurredAt"),
                ("BrandView", "remark"), ("BrandView", "description"), ("BrandPatch", "remark"), ("BrandWrite", "description"),
                ("BrandWrite", "creationRequestId"), ("BrandWrite", "primaryDomain"), ("BrandView", "primaryDomain"),
                ("BrandPatch", "primaryDomain"), ("TeamBrandRow", "primaryDomain"),
                ("BrandRecognitionRequest", "description"), ("BrandRecognitionRequest", "url"), ("BrandRecognitionRequest", "name"), ("BrandRecognitionRequest", "aliases"),
                ("BrandRecognitionView", "name"), ("BrandRecognitionView", "description"), ("BrandRecognitionView", "primaryDomain"),
                ("BrandRecognitionView", "aliases"), ("BrandRecognitionView", "warnings"), ("BrandRecognitionView", "provider"), ("BrandRecognitionView", "model"),
                ("BrandRecognitionView", "sources"), ("BrandRecognitionSource", "title"), ("BrandRecognitionSource", "url"),
            })
            {
                var properties = schemas.GetProperty(schema).GetProperty("properties");
                Assert.True(properties.TryGetProperty(property, out var description), $"{schema}.{property} 缺失");
                Assert.True(HasChinese(FindDescription(description)), $"{schema}.{property} 缺少中文说明");
            }
            var ledgerTypes = schemas.GetProperty("CreditLedgerEntryType").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToArray();
            Assert.Contains("expire", ledgerTypes);
            var usageTypes = schemas.GetProperty("CreditUsageType").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToArray();
            Assert.Equal(["article_generation", "prompt_monitoring", "article_monitoring", "other"], usageTypes);
            Assert.True(HasChinese(schemas.GetProperty("CreditUsageType").GetProperty("description").GetString()));
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
        internal CreditsController Credits { get; }
        internal TeamController Team { get; }
        internal DefaultHttpContext Context { get; } = new();
        internal Guid TenantId { get; } = Guid.NewGuid();
        internal Guid UserId { get; } = Guid.NewGuid();
        internal Exception? Failure { get; set; }
        internal List<(Guid Tenant, Guid? Actor, CreditUsageRequest Request)> UsageCalls { get; } = [];
        internal List<(Guid TenantId, Guid UserId, TeamSummaryRequest Request)> SummaryCalls { get; } = [];

        internal Fixture(bool authenticated = true, string role = "owner")
        {
            if (authenticated)
                Context.Items[SessionAuthenticationMiddleware.ContextItemKey] = new AuthenticatedRequestContext(
                    new(UserId, "user@example.test", "User", "active", [new TenantMembershipView(TenantId, "团队", role, "active")]),
                    Guid.NewGuid(), "session");
            var resolver = new TenantContextResolver(new TestEnvironment());
            var credits = DispatchProxy.Create<ICreditStore, HandlerProxy>();
            ((HandlerProxy)(object)credits).Handler = (method, args) =>
            {
                Assert.Equal(nameof(ICreditStore.UsageAsync), method);
                UsageCalls.Add(((Guid)args![0]!, (Guid?)args[1], (CreditUsageRequest)args[2]!));
                if (Failure is not null) return Task.FromException<CreditUsageView>(Failure);
                return Task.FromResult(new CreditUsageView(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 20), "UTC", [], [], 0, 0, null));
            };
            var summary = DispatchProxy.Create<ITeamSummaryQuery, HandlerProxy>();
            ((HandlerProxy)(object)summary).Handler = (method, args) =>
            {
                Assert.Equal(nameof(ITeamSummaryQuery.SummaryAsync), method);
                SummaryCalls.Add(((Guid)args![0]!, (Guid)args[1]!, (TeamSummaryRequest)args[2]!));
                if (Failure is not null) return Task.FromException<TeamSummaryView>(Failure);
                var now = DateTimeOffset.UtcNow;
                return Task.FromResult(new TeamSummaryView(new TeamSummaryRange(now, now, now, now, "UTC"), [], [], new TeamSummaryTotals(0, 0, 0, 0)));
            };
            var research = DispatchProxy.Create<IResearchStore, HandlerProxy>();
            Credits = new CreditsController(credits, research, resolver) { ControllerContext = new() { HttpContext = Context } };
            Team = new TeamController(summary, resolver) { ControllerContext = new() { HttpContext = Context } };
        }
    }

    public class HandlerProxy : DispatchProxy
    {
        public Func<string, object?[]?, object?> Handler { get; set; } = (method, _) => throw new InvalidOperationException($"Unexpected call: {method}");
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!.Name, args);
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
