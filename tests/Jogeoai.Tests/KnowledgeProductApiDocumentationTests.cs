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

namespace Jogeoai.Tests;

/// <summary>产品信息接口契约：只加载真实 Controller/DTO 元数据，不注册数据库，不执行业务入口；所有说明必须是中文。</summary>
public sealed class KnowledgeProductApiDocumentationTests
{
    private const string ProductsPath = "/api/v1/sites/{siteId}/knowledge/products";

    private static readonly string[] ExpectedOperations =
    [
        $"GET {ProductsPath}", $"POST {ProductsPath}",
        $"GET {ProductsPath}/{{productId}}", $"PATCH {ProductsPath}/{{productId}}",
        $"POST {ProductsPath}/{{productId}}/parse",
        $"GET {ProductsPath}/{{productId}}/parse-jobs/{{jobId}}",
        $"POST {ProductsPath}/{{productId}}/parse-jobs/{{jobId}}/cancel",
        $"POST {ProductsPath}/{{productId}}/approve", $"POST {ProductsPath}/{{productId}}/reject",
        $"POST {ProductsPath}/{{productId}}/archive", $"POST {ProductsPath}/{{productId}}/sync-knowledge"
    ];

    private static readonly string[] DocumentedSchemas =
    [
        "KnowledgeProductView", "ProductParseJobView", "KnowledgeProductListPage",
        "KnowledgeProductWrite", "ProductParseRequest", "ProductApproveRequest", "ProductSpecItem",
        "ProductFieldSource", "ProductFieldEvidence", "KnowledgeProductOption"
    ];

    [Fact]
    public async Task ProductRoutesAndSchemasAreDocumentedInChinese()
    {
        var (openApi, swagger) = await LoadDocumentsAsync();
        using (openApi)
        using (swagger)
        {
            var found = new List<string>();
            foreach (var path in swagger.RootElement.GetProperty("paths").EnumerateObject())
            {
                if (!path.Name.StartsWith(ProductsPath, StringComparison.Ordinal)) continue;
                foreach (var operation in path.Value.EnumerateObject())
                {
                    found.Add($"{operation.Name.ToUpperInvariant()} {path.Name}");
                    AssertChinese(operation.Value.GetProperty("summary").GetString(), $"{operation.Name} {path.Name}");
                    if (operation.Value.TryGetProperty("parameters", out var parameters))
                        foreach (var parameter in parameters.EnumerateArray())
                            AssertChinese(parameter.TryGetProperty("description", out var description) ? description.GetString() : null,
                                $"{path.Name} {parameter.GetProperty("name").GetString()}");
                }
            }
            Assert.Equal(ExpectedOperations.Order(StringComparer.Ordinal), found.Order(StringComparer.Ordinal));

            // 列表接口的状态筛选按 JSON 名称公开，游标与每页数量存在。
            var list = swagger.RootElement.GetProperty("paths").GetProperty(ProductsPath).GetProperty("get");
            var parameterNames = list.GetProperty("parameters").EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToArray();
            foreach (var name in new[] { "status", "sourceKind", "q", "cursor", "limit" }) Assert.Contains(name, parameterNames);
            Assert.EndsWith("/KnowledgeProductListPage", ResponseRef(list, "200"));
            Assert.EndsWith("/ProductParseJobView", ResponseRef(swagger.RootElement.GetProperty("paths")
                .GetProperty($"{ProductsPath}/{{productId}}/parse").GetProperty("post"), "202"));

            var schemas = swagger.RootElement.GetProperty("components").GetProperty("schemas");
            foreach (var schema in DocumentedSchemas)
            {
                Assert.True(schemas.TryGetProperty(schema, out var definition), $"缺少 schema {schema}");
                foreach (var property in definition.GetProperty("properties").EnumerateObject())
                    AssertChinese(property.Value.TryGetProperty("description", out var description) ? description.GetString() : null,
                        $"{schema}.{property.Name}");
            }
            Assert.Equal(["draft", "parsing", "pending_review", "approved", "parse_failed", "archived"],
                schemas.GetProperty("KnowledgeProductStatus").GetProperty("enum").EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray());
            Assert.Equal(["file", "url", "completion"],
                schemas.GetProperty("ProductParseJobKind").GetProperty("enum").EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray());
            var options = schemas.GetProperty("ArticleGenerationOptionsView").GetProperty("properties");
            AssertChinese(options.GetProperty("products").GetProperty("description").GetString(), "ArticleGenerationOptionsView.products");
            AssertChinese(schemas.GetProperty("KnowledgeDocumentView").GetProperty("properties").GetProperty("sourceKind")
                .GetProperty("description").GetString(), "KnowledgeDocumentView.sourceKind");
            AssertChinese(schemas.GetProperty("KnowledgeVersionView").GetProperty("properties").GetProperty("contentKind")
                .GetProperty("description").GetString(), "KnowledgeVersionView.contentKind");

            // 前端类型从 /openapi/v1.json 生成：新 schema 与路由必须同时出现在该文档中。
            var openSchemas = openApi.RootElement.GetProperty("components").GetProperty("schemas");
            foreach (var schema in new[] { "KnowledgeProductView", "ProductParseJobView", "KnowledgeProductListPage" })
                Assert.True(openSchemas.TryGetProperty(schema, out _), $"openapi 缺少 {schema}");
            var required = openSchemas.GetProperty("KnowledgeProductListPage").GetProperty("required").EnumerateArray().Select(item => item.GetString()).ToArray();
            foreach (var name in new[] { "items", "nextCursor", "totalCount", "pendingReviewCount" }) Assert.Contains(name, required);
            // knowledge/links 已于 2026-11 下线（C1 统一授权基线），两份文档都不得再出现。
            Assert.False(openApi.RootElement.GetProperty("paths").TryGetProperty("/api/v1/sites/{siteId}/knowledge/links", out _));
            Assert.False(swagger.RootElement.GetProperty("paths").TryGetProperty("/api/v1/sites/{siteId}/knowledge/links", out _));
        }
    }

    private static string ResponseRef(JsonElement operation, string status)
    {
        var schema = operation.GetProperty("responses").GetProperty(status).GetProperty("content")
            .EnumerateObject().First(content => content.Name.Contains("json", StringComparison.Ordinal)).Value.GetProperty("schema");
        return schema.GetProperty("$ref").GetString()!;
    }

    private static void AssertChinese(string? text, string subject)
        => Assert.True(text is not null && text.Any(character => character is >= '一' and <= '鿿'),
            $"{subject} 缺少中文说明：{text ?? "<null>"}");

    // 产品信息接口已标记 [Obsolete]（由品牌知识库替代）；本契约测试仍需引用它来加载程序集。
#pragma warning disable CS0618
    private static async Task<(JsonDocument OpenApi, JsonDocument Swagger)> LoadDocumentsAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(KnowledgeProductController).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddControllers().AddApplicationPart(typeof(KnowledgeProductController).Assembly)
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
            return (JsonDocument.Parse(openApiText), JsonDocument.Parse(swaggerText));
        }
        finally
        {
            await app.StopAsync();
        }
    }
#pragma warning restore CS0618
}
