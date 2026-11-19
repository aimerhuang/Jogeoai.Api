using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jogeoai.Api;
using Jogeoai.Api.Controllers;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
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

/// <summary>口碑接口：参数转发、全部来源默认值、回答列表/详情新筛选与 OpenAPI 中文说明；不访问数据库。</summary>
public sealed class ReputationApiTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Site = Guid.NewGuid();

    [Fact]
    public async Task ReputationForwardsViewSnapshotAndAllSourceDefaults()
    {
        var controller = Controller("?view=day&snapshotToken=snap&from=2026-10-29T00:00:00%2B08:00&to=2026-10-30T00:00:00%2B08:00",
            (method, args) =>
            {
                Assert.Equal(nameof(IAnalyticsQueryStore.ReputationAsync), method.Name);
                Assert.Equal(Tenant, args[0]);
                Assert.Equal(Site, args[1]);
                var filter = Assert.IsType<AnalyticsFilterRequest>(args[2]);
                Assert.Equal(new[] { "cloro", "doubao_api", "deepseek_api", "packy_qwen", "packy_glm", "packy_kimi", "packy_yuanbao", "domestic_collector" },
                    filter.Providers);
                Assert.Equal(new[] { "api", "scraped" }, filter.CaptureModes);
                Assert.Equal(new[] { "api_response", "observed_surface", "unknown" }, filter.EvidenceClasses);
                Assert.Equal("snap", args[3]);
                Assert.Equal("day", args[4]);
                return Task.FromResult<ReputationSummaryView>(null!);
            });
        var result = await controller.Reputation(Site, CancellationToken.None);
        // 空结果按既有 Execute 约定为 404；这里只验证参数转发。
        Assert.IsAssignableFrom<ObjectResult>(result);
    }

    [Fact]
    public async Task ReputationRejectsUnknownQueryKeys()
        => AssertError(await Controller("?sentiment=positive", Unexpected).Reputation(Site, CancellationToken.None),
            400, "INVALID_FILTER");

    [Fact]
    public async Task RecordsWithSentimentOrTermUseTheReputationOverload()
    {
        var page = new MonitoringRecordPage(null!, [], null, 0);
        var controller = Controller("?snapshotToken=snap&sentiment=negative&term=%E4%BB%B7%E6%A0%BC%E9%AB%98&termPolarity=negative",
            (method, args) =>
            {
                Assert.Equal(nameof(IAnalyticsQueryStore.MonitoringRecordsAsync), method.Name);
                Assert.Equal(13, args.Length);
                Assert.Equal("negative", args[9]);
                Assert.Equal("价格高", args[10]);
                Assert.Equal("negative", args[11]);
                return Task.FromResult(page);
            });
        var result = Assert.IsType<ObjectResult>(await controller.MonitoringRecords(Site, CancellationToken.None));
        Assert.Same(page, result.Value);
    }

    [Fact]
    public async Task LoneTermPolarityStillReachesTheReputationOverloadSoItIsNotSilentlyIgnored()
    {
        var page = new MonitoringRecordPage(null!, [], null, 0);
        var controller = Controller("?snapshotToken=snap&termPolarity=positive", (_, args) =>
        {
            Assert.Equal(13, args.Length);
            Assert.Null(args[10]);
            Assert.Equal("positive", args[11]);
            return Task.FromResult(page);
        });
        Assert.IsType<ObjectResult>(await controller.MonitoringRecords(Site, CancellationToken.None));
    }

    [Fact]
    public void TermPolarityRequiresTermAndASupportedValue()
    {
        Assert.Null(AnalyticsQueryStore.NormalizeReputationTermPolarity(null, null));
        Assert.Null(AnalyticsQueryStore.NormalizeReputationTermPolarity("  ", "价格"));
        Assert.Equal(ReputationTermPolarity.Negative, AnalyticsQueryStore.NormalizeReputationTermPolarity(" NEGATIVE ", "价格"));
        foreach (var (value, term) in new[] { ("positive", (string?)null), ("great", "价格"), ("unknown", "价格") })
        {
            var error = Assert.Throws<CollectorStoreException>(() => AnalyticsQueryStore.NormalizeReputationTermPolarity(value, term));
            Assert.Equal(("INVALID_FILTER", 400), (error.Code, error.StatusCode));
        }
        // 词极性进入游标作用域；不传时作用域与只带 term 时相同。
        var termOnly = AnalyticsQueryStore.MonitoringRecordScope(null, null, null, null, "价格");
        Assert.Equal(termOnly, AnalyticsQueryStore.MonitoringRecordScope(null, null, null, null, "价格", null));
        Assert.NotEqual(AnalyticsQueryStore.MonitoringRecordScope(null, null, null, null, "价格", ReputationTermPolarity.Negative),
            AnalyticsQueryStore.MonitoringRecordScope(null, null, null, null, "价格", ReputationTermPolarity.Positive));
    }

    [Fact]
    public async Task RecordsWithoutReputationFiltersKeepTheOriginalOverload()
    {
        var page = new MonitoringRecordPage(null!, [], null, 0);
        var controller = Controller("?snapshotToken=snap", (method, args) =>
        {
            Assert.Equal(10, args.Length);
            return Task.FromResult(page);
        });
        Assert.IsType<ObjectResult>(await controller.MonitoringRecords(Site, CancellationToken.None));
    }

    [Fact]
    public async Task DetailAcceptsAndValidatesListReputationFilters()
    {
        var detail = new MonitoringAnswerDetailView(Site, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "q", "cloro",
            "chatgpt", null, null, null, "succeeded", false, null, [], "unknown", [], null, null, null, null, null, false);
        var ok = Controller("?snapshotToken=snap&sentiment=positive&term=x", (_, _) => Task.FromResult<MonitoringAnswerDetailView?>(detail));
        Assert.Equal(200, Assert.IsType<ObjectResult>(await ok.MonitoringAnswerDetail(Site, detail.CheckId, CancellationToken.None)).StatusCode);
        AssertError(await Controller("?snapshotToken=snap&sentiment=great", Unexpected)
            .MonitoringAnswerDetail(Site, detail.CheckId, CancellationToken.None), 400, "INVALID_FILTER");
        var polarity = Controller("?snapshotToken=snap&term=x&termPolarity=negative",
            (_, _) => Task.FromResult<MonitoringAnswerDetailView?>(detail));
        Assert.Equal(200, Assert.IsType<ObjectResult>(await polarity.MonitoringAnswerDetail(Site, detail.CheckId, CancellationToken.None)).StatusCode);
        foreach (var query in new[] { "?snapshotToken=snap&termPolarity=negative", "?snapshotToken=snap&term=x&termPolarity=mixed" })
            AssertError(await Controller(query, Unexpected).MonitoringAnswerDetail(Site, detail.CheckId, CancellationToken.None),
                400, "INVALID_FILTER");
    }

    [Fact]
    public async Task OpenApiDocumentsReputationRouteParametersAndChineseSchemas()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(AnalyticsController).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddControllers().AddApplicationPart(typeof(AnalyticsController).Assembly)
            .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(options =>
        {
            options.OperationFilter<AnalyticsOpenApiOperationFilter>();
            options.UseAllOfToExtendReferenceSchemas();
            options.SwaggerDoc("v1", new OpenApiInfo { Title = "Jogeoai API", Version = "v1" });
            foreach (var assembly in new[] { "Jogeoai.Api", "Jogeoai.Domain", "Jogeoai.Application" })
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
            var reputation = paths.GetProperty("/api/v1/sites/{siteId}/analytics/reputation").GetProperty("get");
            Assert.True(HasChinese(reputation.GetProperty("summary").GetString()));
            var parameters = reputation.GetProperty("parameters").EnumerateArray().ToArray();
            foreach (var name in new[] { "view", "from", "to", "timeZone", "provider", "engine", "captureMode", "promptId", "snapshotToken" })
                Assert.True(HasChinese(parameters.Single(p => p.GetProperty("name").GetString() == name)
                    .GetProperty("description").GetString()), name);
            Assert.Contains("默认全部", parameters.Single(p => p.GetProperty("name").GetString() == "provider")
                .GetProperty("description").GetString());
            var records = paths.GetProperty("/api/v1/sites/{siteId}/monitoring/records").GetProperty("get")
                .GetProperty("parameters").EnumerateArray().ToArray();
            foreach (var name in new[] { "sentiment", "term", "termPolarity" })
                Assert.True(HasChinese(records.Single(p => p.GetProperty("name").GetString() == name)
                    .GetProperty("description").GetString()), name);
            Assert.True(paths.TryGetProperty("/api/v1/sites/{siteId}/reputation-jobs", out var jobs));
            Assert.True(HasChinese(jobs.GetProperty("post").GetProperty("summary").GetString()));
            Assert.Contains(jobs.GetProperty("post").GetProperty("parameters").EnumerateArray(),
                p => p.GetProperty("name").GetString() == "Idempotency-Key");

            var schemas = swagger.RootElement.GetProperty("components").GetProperty("schemas");
            foreach (var schemaName in new[]
                     {
                         "ReputationSummaryView", "ReputationMeta", "ReputationMetrics", "ReputationScore", "ReputationComparison",
                         "ReputationTrendPoint", "ReputationPlatformView", "ReputationPlatformWithoutData", "ReputationQuestionView",
                         "ReputationHeatmap", "ReputationHeatmapCell", "ReputationTermView", "ReputationQuality",
                         "ReputationSentimentView", "ReputationJobView"
                     })
            {
                var schema = schemas.GetProperty(schemaName);
                Assert.True(HasChinese(schema.GetProperty("description").GetString()), schemaName);
                foreach (var property in schema.GetProperty("properties").EnumerateObject())
                {
                    var description = property.Value.TryGetProperty("description", out var d) ? d.GetString() : null;
                    Assert.True(HasChinese(description), $"{schemaName}.{property.Name}");
                }
            }
            var sentiment = schemas.GetProperty("MonitoringRecordView").GetProperty("properties").GetProperty("reputationSentiment");
            Assert.True(HasChinese(sentiment.GetProperty("description").GetString()));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static bool HasChinese(string? text) => text is not null && text.Any(c => c is >= '一' and <= '鿿');

    private static AnalyticsController Controller(string query, Func<MethodInfo, object?[], object?> handler)
    {
        var queries = DispatchProxy.Create<IAnalyticsQueryStore, MonitoringAnswerApiTests.QueryProxy>();
        ((MonitoringAnswerApiTests.QueryProxy)queries).Handler = handler;
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

    private static object? Unexpected(MethodInfo method, object?[] args)
        => throw new InvalidOperationException($"Unexpected query: {method.Name}");

    private static void AssertError(IActionResult action, int status, string code)
    {
        var result = Assert.IsAssignableFrom<ObjectResult>(action);
        Assert.Equal(status, result.StatusCode);
        Assert.Equal(code, JsonSerializer.SerializeToElement(result.Value).GetProperty("error").GetProperty("code").GetString());
    }

    private sealed class StubEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
