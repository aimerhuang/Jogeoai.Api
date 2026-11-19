using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jogeoai.Api;
using Jogeoai.Api.Controllers;
using Jogeoai.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Microsoft.OpenApi;

namespace Jogeoai.Tests;

/// <summary>文章管理页契约：只加载真实 Controller/DTO 元数据，不注册数据库、不执行业务入口。</summary>
public sealed class ContentApiDocumentationTests
{
    private const string ArticlesPath = "/api/v1/sites/{siteId}/articles";
    private const string RankingsPath = "/api/v1/sites/{siteId}/article-monitoring/domain-rankings";
    private const string BillingPath = "/api/v1/sites/{siteId}/article-monitoring/billing";
    private const string ChannelsPath = "/api/v1/sites/{siteId}/article-monitoring/recommended-channels";

    [Fact]
    public async Task RecommendedChannelsContractIsDocumentedInChinese()
    {
        var (openApi, swagger) = await LoadDocumentsAsync();
        using (openApi)
        using (swagger)
        {
            foreach (var document in new[] { openApi, swagger })
            {
                var operation = Operation(document, ChannelsPath);
                var names = operation.GetProperty("parameters").EnumerateArray()
                    .Select(parameter => parameter.GetProperty("name").GetString()).ToArray();
                Assert.Contains("engine", names);
                Assert.Contains("limit", names);
                Assert.DoesNotContain("snapshotToken", names);
                Assert.DoesNotContain("cursor", names);
                Assert.EndsWith("/ArticleRecommendedChannelList", ResponseRef(operation));
            }

            var swaggerOperation = swagger.RootElement.GetProperty("paths").GetProperty(ChannelsPath).GetProperty("get");
            AssertChinese(swaggerOperation.GetProperty("summary").GetString());
            foreach (var parameter in swaggerOperation.GetProperty("parameters").EnumerateArray())
                AssertChinese(parameter.GetProperty("description").GetString(), parameter.GetProperty("name").GetString());
            foreach (var status in new[] { "400", "401", "404" })
                Assert.True(swaggerOperation.GetProperty("responses").TryGetProperty(status, out _), status);

            var schemas = swagger.RootElement.GetProperty("components").GetProperty("schemas");
            foreach (var schema in new[] { "ArticleRecommendedChannelList", "ArticleRecommendedChannel" })
                foreach (var property in schemas.GetProperty(schema).GetProperty("properties").EnumerateObject())
                    AssertChinese(property.Value.TryGetProperty("description", out var description) ? description.GetString() : null,
                        $"{schema}.{property.Name}");
            var openSchemas = openApi.RootElement.GetProperty("components").GetProperty("schemas");
            foreach (var name in new[] { "rank", "platformKey", "name", "iconDomain", "url", "citationEvents", "domains" })
                Assert.Contains(name, openSchemas.GetProperty("ArticleRecommendedChannel").GetProperty("required")
                    .EnumerateArray().Select(item => item.GetString()));
        }
    }

    [Fact]
    public async Task OwnedUrlMarkAndUnmarkContractsAreDocumentedInChinese()
    {
        const string markPath = "/api/v1/sites/{siteId}/content/owned-urls";
        const string unmarkPath = markPath + "/{urlHash}";
        var (openApi, swagger) = await LoadDocumentsAsync();
        using (openApi)
        using (swagger)
        {
            foreach (var document in new[] { openApi, swagger })
            {
                var paths = document.RootElement.GetProperty("paths");
                var mark = paths.GetProperty(markPath).GetProperty("post");
                var unmark = paths.GetProperty(unmarkPath).GetProperty("delete");
                Assert.EndsWith("/OwnedUrlMarkResult", ResponseRef(mark));
                Assert.EndsWith("/OwnedUrlMarkResult", ResponseRef(unmark));
                // Swagger wraps described references in allOf (UseAllOfToExtendReferenceSchemas); OpenAPI keeps a plain $ref.
                var body = mark.GetProperty("requestBody").GetProperty("content")
                    .EnumerateObject().First(content => content.Name.Contains("json", StringComparison.Ordinal)).Value.GetProperty("schema");
                if (body.TryGetProperty("allOf", out var allOf)) body = allOf[0];
                Assert.EndsWith("/OwnedUrlMarkRequest", body.GetProperty("$ref").GetString());
                Assert.Contains(unmark.GetProperty("parameters").EnumerateArray(),
                    p => p.GetProperty("name").GetString() == "urlHash" && p.GetProperty("in").GetString() == "path");
                foreach (var operation in new[] { mark, unmark })
                    foreach (var status in new[] { "400", "401", "403", "404" })
                        Assert.True(operation.GetProperty("responses").TryGetProperty(status, out _), status);
            }

            var swaggerPaths = swagger.RootElement.GetProperty("paths");
            AssertChinese(swaggerPaths.GetProperty(markPath).GetProperty("post").GetProperty("summary").GetString(), "mark");
            var unmarkOperation = swaggerPaths.GetProperty(unmarkPath).GetProperty("delete");
            AssertChinese(unmarkOperation.GetProperty("summary").GetString(), "unmark");
            AssertChinese(unmarkOperation.GetProperty("parameters").EnumerateArray()
                .Single(p => p.GetProperty("name").GetString() == "urlHash").GetProperty("description").GetString(), "urlHash");
            var schemas = swagger.RootElement.GetProperty("components").GetProperty("schemas");
            foreach (var schema in new[] { "OwnedUrlMarkRequest", "OwnedUrlMarkResult" })
                foreach (var property in schemas.GetProperty(schema).GetProperty("properties").EnumerateObject())
                    AssertChinese(property.Value.TryGetProperty("description", out var description) ? description.GetString() : null,
                        $"{schema}.{property.Name}");
            Assert.Equal(["created", "reactivated", "unchanged", "disabled"], schemas.GetProperty("OwnedUrlMarkOutcome")
                .GetProperty("enum").EnumerateArray().Select(item => item.GetString()!).ToArray());
            var openSchemas = openApi.RootElement.GetProperty("components").GetProperty("schemas");
            foreach (var name in new[] { "urlHash", "url", "isMine", "articleId", "outcome" })
                Assert.Contains(name, openSchemas.GetProperty("OwnedUrlMarkResult").GetProperty("required")
                    .EnumerateArray().Select(item => item.GetString()));
            Assert.Contains("url", openSchemas.GetProperty("OwnedUrlMarkRequest").GetProperty("required")
                .EnumerateArray().Select(item => item.GetString()));
        }
    }

    [Fact]
    public async Task ProductSelectionStrategyAndLengthContractAreDocumentedInChinese()
    {
        var (openApi, swagger) = await LoadDocumentsAsync();
        using (openApi)
        using (swagger)
        {
            var properties = swagger.RootElement.GetProperty("components").GetProperty("schemas")
                .GetProperty("ArticleGenerationPreviewRequest").GetProperty("properties");
            Assert.Contains("product_selection", properties.GetProperty("strategy").GetProperty("description").GetString());
            Assert.Contains("2000–3000", properties.GetProperty("lengthProfile").GetProperty("description").GetString());
            Assert.Contains("product_selection", ArticleWritingRules.Strategies);
            Assert.Equal(4, ArticleWritingRules.Strategies.Count);
        }
    }

    [Fact]
    public async Task LinkMonitoringBillingAndDiscoveryContractsAreDocumentedInChinese()
    {
        var (openApi, swagger) = await LoadDocumentsAsync();
        using (openApi)
        using (swagger)
        {
            foreach (var document in new[] { openApi, swagger })
                Assert.EndsWith("/ArticleMonitoringBillingView", ResponseRef(Operation(document, BillingPath)));
            var operation = swagger.RootElement.GetProperty("paths").GetProperty(BillingPath).GetProperty("get");
            AssertChinese(operation.GetProperty("summary").GetString());
            Assert.True(operation.GetProperty("responses").TryGetProperty("403", out _));

            var schemas = swagger.RootElement.GetProperty("components").GetProperty("schemas");
            foreach (var schema in new[] { "ArticleMonitoringBillingView", "ArticleMonitoringRoundView", "ArticleDiscoverySummary" })
                foreach (var property in schemas.GetProperty(schema).GetProperty("properties").EnumerateObject())
                    AssertChinese(property.Value.TryGetProperty("description", out var description) ? description.GetString() : null,
                        $"{schema}.{property.Name}");
            // 必填信息来自 ASP.NET OpenAPI 文档（前端类型由它生成）；Swagger 文档只提供中文描述与字符串枚举。
            var openSchemas = openApi.RootElement.GetProperty("components").GetProperty("schemas");
            foreach (var name in new[] { "unitPrice", "billableLinkCount", "nextEstimate", "availableCredits", "todayStatus", "recentRounds" })
                Assert.Contains(name, openSchemas.GetProperty("ArticleMonitoringBillingView").GetProperty("required")
                    .EnumerateArray().Select(item => item.GetString()));
            AssertChinese(schemas.GetProperty("ArticleView").GetProperty("properties").GetProperty("discovery")
                .GetProperty("description").GetString(), "ArticleView.discovery");
            var link = schemas.GetProperty("ArticlePublicationLinkView").GetProperty("properties");
            foreach (var name in new[] { "discoveredCheckId", "discoveredAt" })
                AssertChinese(link.GetProperty(name).GetProperty("description").GetString(), $"ArticlePublicationLinkView.{name}");
            var kinds = schemas.GetProperty("ArticleSourceKind").GetProperty("enum").EnumerateArray().Select(item => item.GetString()).ToArray();
            Assert.Equal(["ai_generated", "published_import", "citation_discovered"], kinds);
            var usageTypes = schemas.GetProperty("CreditUsageType").GetProperty("enum").EnumerateArray().Select(item => item.GetString()).ToArray();
            Assert.Contains("article_monitoring", usageTypes);
        }
    }

    [Fact]
    public async Task ArticleListDetailAndDomainRankingContractsAreDocumentedInChinese()
    {
        var (openApi, swagger) = await LoadDocumentsAsync();
        using (openApi)
        using (swagger)
        {
            // Frontend types are generated from /openapi/v1.json; Swagger UI shows the descriptions.
            foreach (var document in new[] { openApi, swagger })
            {
                var list = Operation(document, ArticlesPath);
                foreach (var name in new[] { "stage", "language", "ownedOnly", "onlyMine", "sourceKind", "cursor", "limit",
                             "citationDays", "cited", "citationEngine", "ownership" })
                    Assert.Contains(list.GetProperty("parameters").EnumerateArray(), p => p.GetProperty("name").GetString() == name);
                Assert.EndsWith("/ArticleListPage", ResponseRef(list));

                var rankings = Operation(document, RankingsPath);
                foreach (var name in new[] { "from", "to", "timeZone", "engine", "snapshotToken", "q", "cursor", "limit" })
                    Assert.Contains(rankings.GetProperty("parameters").EnumerateArray(), p => p.GetProperty("name").GetString() == name);
                Assert.EndsWith("/ArticleDomainRankingPage", ResponseRef(rankings));
            }

            var openSchemas = openApi.RootElement.GetProperty("components").GetProperty("schemas");
            Assert.False(openSchemas.TryGetProperty("CursorPageOfArticleView", out _));
            var required = openSchemas.GetProperty("ArticleListPage").GetProperty("required")
                .EnumerateArray().Select(item => item.GetString()).ToArray();
            foreach (var name in new[] { "items", "nextCursor", "totalCount", "activeLinkCount", "stageCounts" })
                Assert.Contains(name, required);
            var view = openSchemas.GetProperty("ArticleView").GetProperty("properties");
            foreach (var name in new[] { "stage", "prompt", "generation", "ownedPublication" })
                Assert.True(view.TryGetProperty(name, out _), name);
            Assert.True(openSchemas.GetProperty("ArticleDetailView").GetProperty("properties").TryGetProperty("generation", out _));
            foreach (var name in new[] { "meta", "items", "nextCursor", "totalDomainCount" })
                Assert.Contains(name, openSchemas.GetProperty("ArticleDomainRankingPage").GetProperty("required")
                    .EnumerateArray().Select(item => item.GetString()));
            foreach (var name in new[] { "domain", "citationEvents", "registeredArticleCount", "registeredLinkCount" })
                Assert.Contains(name, openSchemas.GetProperty("ArticleDomainRankingRow").GetProperty("required")
                    .EnumerateArray().Select(item => item.GetString()));
            // String enum names come from Swagger: scripts/generate-api.mjs merges its
            // JsonStringEnumConverter schemas over ASP.NET OpenAPI's numeric enums.

            var swaggerPaths = swagger.RootElement.GetProperty("paths");
            foreach (var path in new[] { ArticlesPath, RankingsPath })
            {
                var operation = swaggerPaths.GetProperty(path).GetProperty("get");
                AssertChinese(operation.GetProperty("summary").GetString());
                foreach (var parameter in operation.GetProperty("parameters").EnumerateArray())
                    AssertChinese(parameter.GetProperty("description").GetString(), parameter.GetProperty("name").GetString());
            }

            var schemas = swagger.RootElement.GetProperty("components").GetProperty("schemas");
            var stages = schemas.GetProperty("ArticleStage").GetProperty("enum").EnumerateArray().Select(item => item.GetString()).ToArray();
            Assert.Equal(ArticleStageValues, stages);
            foreach (var schema in new[]
                     {
                         "ArticleListPage", "ArticlePromptSummary", "ArticleGenerationSummary", "ArticleGenerationBasisView",
                         "ArticleTargetSummary", "ArticleReferenceSummary", "ArticleDomainRankingPage", "ArticleDomainRankingRow",
                         "ArticlePrimaryLinkView", "ArticleCitedCounts"
                     })
                foreach (var property in schemas.GetProperty(schema).GetProperty("properties").EnumerateObject())
                    AssertChinese(property.Value.TryGetProperty("description", out var description) ? description.GetString() : null,
                        $"{schema}.{property.Name}");
            var articleView = schemas.GetProperty("ArticleView").GetProperty("properties");
            foreach (var name in new[] { "stage", "prompt", "generation", "ownedPublication", "primaryLink" })
                AssertChinese(articleView.GetProperty(name).GetProperty("description").GetString(), $"ArticleView.{name}");
            var citationSummary = schemas.GetProperty("ArticleCitationSummary").GetProperty("properties");
            foreach (var name in new[] { "engines", "firstObservedAt" })
                AssertChinese(citationSummary.GetProperty(name).GetProperty("description").GetString(), $"ArticleCitationSummary.{name}");
            Assert.Equal(["owned", "external"], schemas.GetProperty("ArticleOwnershipFilter").GetProperty("enum")
                .EnumerateArray().Select(item => item.GetString()!).ToArray());
            AssertChinese(schemas.GetProperty("ArticleDetailView").GetProperty("properties").GetProperty("generation")
                .GetProperty("description").GetString(), "ArticleDetailView.generation");
        }
    }

    [Theory]
    [InlineData(typeof(ArticleStage?), "generation_failed", ArticleStage.GenerationFailed)]
    [InlineData(typeof(ArticleStage?), "pending_publish", ArticleStage.PendingPublish)]
    [InlineData(typeof(ArticleStage?), "Tracking", ArticleStage.Tracking)]
    [InlineData(typeof(ArticleSourceKind?), "ai_generated", ArticleSourceKind.AiGenerated)]
    [InlineData(typeof(ArticleSourceKind?), "PublishedImport", ArticleSourceKind.PublishedImport)]
    [InlineData(typeof(ArticleSourceKind?), "citation_discovered", ArticleSourceKind.CitationDiscovered)]
    public async Task QueryEnumsBindByJsonNameAndMemberName(Type modelType, string value, object expected)
    {
        var context = BindingContext(modelType, value);
        await new JsonNameEnumModelBinder().BindModelAsync(context);
        Assert.True(context.Result.IsModelSet);
        Assert.Equal(expected, context.Result.Model);
        // Entries stay Unvalidated until MVC's validation step; only errors matter here.
        Assert.Equal(0, context.ModelState.ErrorCount);
    }

    [Fact]
    public async Task QueryEnumBinderRejectsUnknownValuesAndIgnoresEmpty()
    {
        var unknown = BindingContext(typeof(ArticleStage?), "draft");
        await new JsonNameEnumModelBinder().BindModelAsync(unknown);
        Assert.False(unknown.Result.IsModelSet);
        Assert.Equal(1, unknown.ModelState.ErrorCount);

        var empty = BindingContext(typeof(ArticleStage?), "");
        await new JsonNameEnumModelBinder().BindModelAsync(empty);
        Assert.False(empty.Result.IsModelSet);
        Assert.Equal(0, empty.ModelState.ErrorCount);
    }

    private static DefaultModelBindingContext BindingContext(Type modelType, string value) => new()
    {
        ModelMetadata = new EmptyModelMetadataProvider().GetMetadataForType(modelType),
        ModelName = "value",
        ModelState = new ModelStateDictionary(),
        ValueProvider = new QueryStringValueProvider(BindingSource.Query,
            new QueryCollection(new Dictionary<string, StringValues> { ["value"] = value }), CultureInfo.InvariantCulture)
    };

    private static readonly string[] ArticleStageValues =
        ["generating", "generation_failed", "pending_publish", "publishing", "tracking", "archived"];

    private static JsonElement Operation(JsonDocument document, string path)
        => document.RootElement.GetProperty("paths").GetProperty(path).GetProperty("get");

    private static string ResponseRef(JsonElement operation)
    {
        var schema = operation.GetProperty("responses").GetProperty("200").GetProperty("content")
            .EnumerateObject().First(content => content.Name.Contains("json", StringComparison.Ordinal))
            .Value.GetProperty("schema");
        return schema.GetProperty("$ref").GetString()!;
    }

    private static void AssertChinese(string? text, string? subject = null)
        => Assert.True(text is not null && text.Any(character => character is >= '一' and <= '鿿'),
            $"{subject ?? "summary"} 缺少中文说明：{text ?? "<null>"}");

    private static async Task<(JsonDocument OpenApi, JsonDocument Swagger)> LoadDocumentsAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(ContentController).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddControllers().AddApplicationPart(typeof(ContentController).Assembly)
            .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddOpenApi();
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
        app.MapOpenApi();
        app.UseSwagger();
        app.MapControllers();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            var openApiText = await client.GetStringAsync("/openapi/v1.json");
            var swaggerText = await client.GetStringAsync("/swagger/v1/swagger.json");
            var destination = Environment.GetEnvironmentVariable("JOGEOAI_CONTENT_OPENAPI_OUTPUT");
            if (!string.IsNullOrWhiteSpace(destination))
            {
                var fullPath = Path.GetFullPath(destination);
                Directory.CreateDirectory(fullPath);
                await File.WriteAllTextAsync(Path.Combine(fullPath, "openapi.json"), openApiText);
                await File.WriteAllTextAsync(Path.Combine(fullPath, "swagger.json"), swaggerText);
            }
            return (JsonDocument.Parse(openApiText), JsonDocument.Parse(swaggerText));
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
