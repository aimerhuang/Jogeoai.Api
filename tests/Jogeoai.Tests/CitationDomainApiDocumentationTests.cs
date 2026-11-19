using System.Text.Json;
using System.Text.Json.Serialization;
using Jogeoai.Api;
using Jogeoai.Api.Controllers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;
using Xunit.Abstractions;

namespace Jogeoai.Tests;

/// <summary>仅加载真实 Controller 元数据，不执行业务入口、不注册数据库的文档宿主。</summary>
public sealed class CitationDomainApiDocumentationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RealControllerDocumentsExposeSummaryContractsAndCanBeExportedWithoutDatabase()
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
        builder.Services.AddOpenApi();
        builder.Services.AddSwaggerGen(options =>
        {
            options.OperationFilter<AnalyticsOpenApiOperationFilter>();
            options.UseAllOfToExtendReferenceSchemas();
            options.SwaggerDoc("v1", new OpenApiInfo { Title = "Jogeoai API", Version = "v1" });
            foreach (var assembly in new[]
            {
                "Jogeoai.Api", "Jogeoai.Domain", "Jogeoai.Application", "Jogeoai.Providers.Abstractions"
            })
                options.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, assembly + ".xml"),
                    includeControllerXmlComments: assembly == "Jogeoai.Api");
        });
        await using var app = builder.Build();
        app.MapOpenApi();
        app.UseSwagger();
        app.MapControllers();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            var openApiText = await client.GetStringAsync("/openapi/v1.json");
            var swaggerText = await client.GetStringAsync("/swagger/v1/swagger.json");
            using var openApi = JsonDocument.Parse(openApiText);
            using var swagger = JsonDocument.Parse(swaggerText);
            const string prefix = "/api/v1/sites/{siteId}/analytics/citation-domain-summary";
            foreach (var suffix in new[] { "", "/domains", "/evidence", "/urls", "/domain-detail", "/url-prompts" })
            {
                var operation = swagger.RootElement.GetProperty("paths").GetProperty(prefix + suffix).GetProperty("get");
                Assert.True(openApi.RootElement.GetProperty("paths").GetProperty(prefix + suffix).TryGetProperty("get", out _));
                Assert.True(operation.TryGetProperty("summary", out var summary));
                Assert.Contains(summary.GetString()!, character => character is >= '\u4e00' and <= '\u9fff');
                Assert.True(operation.GetProperty("responses").TryGetProperty("200", out _));
            }

            var domains = swagger.RootElement.GetProperty("paths").GetProperty(prefix + "/domains").GetProperty("get");
            foreach (var name in new[] { "snapshotToken", "q", "websiteCategory", "ownership", "cursor", "limit" })
                Assert.Contains(domains.GetProperty("parameters").EnumerateArray(), parameter => parameter.GetProperty("name").GetString() == name);
            var evidence = swagger.RootElement.GetProperty("paths").GetProperty(prefix + "/evidence").GetProperty("get");
            Assert.Contains(evidence.GetProperty("parameters").EnumerateArray(), parameter => parameter.GetProperty("name").GetString() == "domain");
            var urls = swagger.RootElement.GetProperty("paths").GetProperty(prefix + "/urls").GetProperty("get");
            foreach (var name in new[] { "snapshotToken", "domain", "cursor", "limit" })
            {
                var parameter = urls.GetProperty("parameters").EnumerateArray().Single(p => p.GetProperty("name").GetString() == name);
                Assert.Contains(parameter.GetProperty("description").GetString()!, character => character is >= '\u4e00' and <= '\u9fff');
                Assert.Equal(name == "snapshotToken", parameter.TryGetProperty("required", out var required) && required.GetBoolean());
            }
            foreach (var name in new[] { "engine", "q", "mine" })
            {
                var parameter = urls.GetProperty("parameters").EnumerateArray().Single(p => p.GetProperty("name").GetString() == name);
                Assert.Contains("domain", parameter.GetProperty("description").GetString()!);
                Assert.Contains(parameter.GetProperty("description").GetString()!, character => character is >= '\u4e00' and <= '\u9fff');
                Assert.False(parameter.TryGetProperty("required", out var required) && required.GetBoolean());
            }
            var domainDetail = swagger.RootElement.GetProperty("paths").GetProperty(prefix + "/domain-detail").GetProperty("get");
            foreach (var name in new[] { "snapshotToken", "domain", "engine" })
            {
                var parameter = domainDetail.GetProperty("parameters").EnumerateArray().Single(p => p.GetProperty("name").GetString() == name);
                Assert.Contains(parameter.GetProperty("description").GetString()!, character => character is >= '\u4e00' and <= '\u9fff');
                Assert.Equal(name != "engine", parameter.TryGetProperty("required", out var required) && required.GetBoolean());
            }
            Assert.DoesNotContain(domainDetail.GetProperty("parameters").EnumerateArray(), p => p.GetProperty("name").GetString() is "cursor" or "limit" or "q");
            var urlPrompts = swagger.RootElement.GetProperty("paths").GetProperty(prefix + "/url-prompts").GetProperty("get");
            foreach (var name in new[] { "snapshotToken", "urlHash", "engine" })
            {
                var parameter = urlPrompts.GetProperty("parameters").EnumerateArray().Single(p => p.GetProperty("name").GetString() == name);
                Assert.Contains(parameter.GetProperty("description").GetString()!, character => character is >= '一' and <= '鿿');
                Assert.Equal(name != "engine", parameter.TryGetProperty("required", out var required) && required.GetBoolean());
            }
            // 三个原有参数加 App 渠道任务（11-16）新增的可选 channel。
            Assert.Equal(4, urlPrompts.GetProperty("parameters").EnumerateArray().Count(p => p.GetProperty("in").GetString() == "query"));
            // 引用域名汇总一族的六个入口都有可选的 channel：枚举只有 api 与 app，带中文说明。
            foreach (var suffix in new[] { "", "/domains", "/evidence", "/urls", "/domain-detail", "/url-prompts" })
            {
                var channel = swagger.RootElement.GetProperty("paths").GetProperty(prefix + suffix).GetProperty("get")
                    .GetProperty("parameters").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "channel");
                Assert.False(channel.TryGetProperty("required", out var channelRequired) && channelRequired.GetBoolean());
                Assert.Contains(channel.GetProperty("description").GetString()!, character => character is >= '一' and <= '鿿');
                Assert.Equal(["api", "app"], channel.GetProperty("schema").GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
            }
            foreach (var status in new[] { "400", "404", "409", "503" })
                Assert.True(urlPrompts.GetProperty("responses").TryGetProperty(status, out _), status);
            // 旧 analytics/citations/domains/{domain} 不得被新 domain-detail 的参数说明覆盖（两者曾同名 action）。
            var legacyDomain = swagger.RootElement.GetProperty("paths").GetProperty("/api/v1/sites/{siteId}/analytics/citations/domains/{domain}")
                .GetProperty("get").GetProperty("parameters").EnumerateArray().ToArray();
            Assert.DoesNotContain(legacyDomain, p => p.GetProperty("name").GetString() is "snapshotToken" or "engine"
                && p.GetProperty("in").GetString() == "query" && p.GetProperty("description").GetString()!.Contains("只影响逐日趋势"));
            Assert.Single(legacyDomain, p => p.GetProperty("name").GetString() == "domain");
            var records = swagger.RootElement.GetProperty("paths").GetProperty("/api/v1/sites/{siteId}/monitoring/records").GetProperty("get");
            Assert.Contains(records.GetProperty("parameters").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "citedDomain")
                .GetProperty("description").GetString()!, character => character is >= '\u4e00' and <= '\u9fff');
            Assert.Contains("五十", urls.GetProperty("parameters").EnumerateArray()
                .Single(p => p.GetProperty("name").GetString() == "limit").GetProperty("description").GetString());
            var detail = swagger.RootElement.GetProperty("paths").GetProperty("/api/v1/sites/{siteId}/monitoring/records/{checkId}").GetProperty("get");
            Assert.Contains(detail.GetProperty("parameters").EnumerateArray(), parameter => parameter.GetProperty("name").GetString() == "citationProjection");
            Assert.Contains(detail.GetProperty("parameters").EnumerateArray(), parameter => parameter.GetProperty("name").GetString() == "citedDomain");

            var schemas = openApi.RootElement.GetProperty("components").GetProperty("schemas");
            var requiredMetrics = schemas.GetProperty("CitationDomainMetrics").GetProperty("required")
                .EnumerateArray().Select(item => item.GetString()).ToArray();
            Assert.Contains("eligibleAnswers", requiredMetrics);
            Assert.Contains("citationEvents", requiredMetrics);
            Assert.Contains("distinctDomains", requiredMetrics);
            Assert.Contains("distinctUrls", requiredMetrics);
            string[] Required(string schema) => schemas.GetProperty(schema).GetProperty("required")
                .EnumerateArray().Select(item => item.GetString()!).ToArray();
            Assert.Contains("rank", Required("CitationDomainSummaryRow"));
            Assert.Contains("previousRank", Required("CitationDomainSummaryRow"));
            Assert.Contains("previousFrom", Required("CitationDomainMeta"));
            Assert.Contains("previousTo", Required("CitationDomainMeta"));
            foreach (var name in new[] { "rank", "url", "urlHash", "domain", "title", "citationEvents", "eventShare", "answerCount",
                         "isMine", "myCitationEvents", "promptCount", "domainRank" })
                Assert.Contains(name, Required("CitationUrlSummaryRow"));
            var domainRankSchema = schemas.GetProperty("CitationUrlSummaryRow").GetProperty("properties").GetProperty("domainRank");
            output.WriteLine("domainRank schema: " + domainRankSchema.GetRawText());
            Assert.Contains("null", domainRankSchema.GetRawText());
            foreach (var name in new[] { "meta", "domain", "engine", "citationEvents", "myCitationEvents", "engines", "trend" })
                Assert.Contains(name, Required("CitationDomainDetailView"));
            foreach (var name in new[] { "engine", "citationEvents", "myCitationEvents" })
                Assert.Contains(name, Required("CitationDomainEngineRow"));
            foreach (var name in new[] { "date", "citationEvents", "myCitationEvents" })
                Assert.Contains(name, Required("CitationDomainTrendPoint"));
            foreach (var name in new[] { "meta", "items", "nextCursor", "filteredUrlCount" })
                Assert.Contains(name, Required("CitationUrlSummaryPage"));
            foreach (var name in new[] { "meta", "urlHash", "url", "title", "engine", "citationEvents", "groupCount", "promptCount", "groups" })
                Assert.Contains(name, Required("CitationUrlPromptBreakdown"));
            foreach (var name in new[] { "groupId", "name", "citationEvents", "prompts" })
                Assert.Contains(name, Required("CitationUrlPromptGroup"));
            foreach (var name in new[] { "promptId", "prompt", "citationEvents" })
                Assert.Contains(name, Required("CitationUrlPromptRow"));
            foreach (var name in new[] { "previousPercentagePointDifference", "previousAverageRankingDifference", "previousExposureDifference", "groupId", "name" })
                Assert.Contains(name, Required("BrandDashboardGroup"));
            Assert.Contains("groupCount", Required("DashboardQuality"));
            Assert.Contains("tagCount", Required("DashboardQuality"));
            Assert.True(schemas.GetProperty("MonitoringAnswerDetailView").GetProperty("properties").TryGetProperty("citationAttribution", out _));
            var swaggerSchemas = swagger.RootElement.GetProperty("components").GetProperty("schemas");
            foreach (var schema in new[] { "MonitoringRecordView", "MonitoringAnswerDetailView", "CloroVisibilityCheckView" })
            {
                Assert.True(schemas.GetProperty(schema).GetProperty("properties").TryGetProperty("resultQuality", out _));
                var qualityProperty = swaggerSchemas.GetProperty(schema).GetProperty("properties").GetProperty("resultQuality");
                Assert.Contains(qualityProperty.GetProperty("description").GetString()!, character => character is >= '\u4e00' and <= '\u9fff');
            }
            foreach (var (schema, property) in new[]
            {
                ("CitationDomainSummaryRow", "rank"), ("CitationDomainSummaryRow", "previousRank"),
                ("CitationDomainMeta", "previousFrom"), ("CitationDomainMeta", "previousTo"),
                ("CitationDomainMetrics", "distinctUrls"),
                ("BrandDashboardGroup", "previousPercentagePointDifference"),
                ("BrandDashboardGroup", "previousAverageRankingDifference"),
                ("BrandDashboardGroup", "previousExposureDifference"),
                ("BrandDashboardGroup", "groupId"), ("BrandDashboardGroup", "name"),
                ("DashboardQuality", "groupCount"), ("DashboardQuality", "groupUnavailableReason")
            })
                Assert.Contains(swaggerSchemas.GetProperty(schema).GetProperty("properties").GetProperty(property)
                    .GetProperty("description").GetString()!, character => character is >= '\u4e00' and <= '\u9fff');
            foreach (var property in new[] { "name", "previousPercentagePointDifference", "previousAverageRankingDifference", "previousExposureDifference" })
            {
                var description = swaggerSchemas.GetProperty("BrandDashboardGroup").GetProperty("properties")
                    .GetProperty(property).GetProperty("description").GetString()!;
                Assert.Contains("提问分组", description);
                Assert.DoesNotContain("标签", description);
            }
            foreach (var schema in new[] { "ResultQualityView", "CitationQualityView", "CitationUrlSummaryRow", "CitationUrlSummaryPage",
                         "CitationDomainDetailView", "CitationDomainEngineRow", "CitationDomainTrendPoint",
                         "CitationUrlPromptBreakdown", "CitationUrlPromptGroup", "CitationUrlPromptRow" })
                foreach (var property in swaggerSchemas.GetProperty(schema).GetProperty("properties").EnumerateObject())
                    Assert.Contains(property.Value.GetProperty("description").GetString()!, character => character is >= '\u4e00' and <= '\u9fff');
            Assert.Contains(swaggerSchemas.GetProperty("MonitoringAnswerDetailView").GetProperty("properties")
                .GetProperty("modelVersionUnavailable").GetProperty("description").GetString()!, character => character is >= '\u4e00' and <= '\u9fff');
            var categories = swaggerSchemas.GetProperty("CitationWebsiteCategory").GetProperty("enum")
                .EnumerateArray().Select(item => item.GetString()).ToArray();
            Assert.Equal(11, categories.Length);
            Assert.Contains("government_nonprofit", categories);
            Assert.Contains("unclassified", categories);
            var ownership = swaggerSchemas.GetProperty("CitationOwnership").GetProperty("enum")
                .EnumerateArray().Select(item => item.GetString()).ToArray();
            Assert.Equal(new[] { "owned", "non_owned", "unknown" }, ownership);

            var destination = Environment.GetEnvironmentVariable("JOGEOAI_CITATION_OPENAPI_OUTPUT");
            if (!string.IsNullOrWhiteSpace(destination))
            {
                var fullPath = Path.GetFullPath(destination);
                Directory.CreateDirectory(fullPath);
                await File.WriteAllTextAsync(Path.Combine(fullPath, "openapi.json"), openApiText);
                await File.WriteAllTextAsync(Path.Combine(fullPath, "swagger.json"), swaggerText);
                output.WriteLine($"Exported real Controller documents to {fullPath}; no database was registered or accessed.");
            }
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
