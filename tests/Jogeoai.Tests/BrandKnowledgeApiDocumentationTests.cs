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

/// <summary>品牌知识库接口契约：只加载真实 Controller/DTO 元数据，不注册数据库，不执行业务入口；所有说明必须是中文，旧产品与文件上传接口标记为不再推荐，链接资料接口已下线。</summary>
public sealed class BrandKnowledgeApiDocumentationTests
{
    private const string Root = "/api/v1/sites/{siteId}/knowledge";
    private const string ItemsPath = Root + "/items";
    private const string JobsPath = Root + "/extract-jobs";

    private static readonly string[] ExpectedOperations =
    [
        $"GET {ItemsPath}", $"POST {ItemsPath}",
        $"PATCH {ItemsPath}/{{itemId}}", $"DELETE {ItemsPath}/{{itemId}}",
        $"POST {JobsPath}", $"GET {JobsPath}/current", $"GET {JobsPath}/{{jobId}}",
        $"POST {JobsPath}/{{jobId}}/cancel", $"POST {JobsPath}/{{jobId}}/save", $"POST {JobsPath}/{{jobId}}/discard"
    ];

    private static readonly string[] DocumentedSchemas =
    [
        "BrandKnowledgeItemView", "BrandKnowledgeItemList", "BrandKnowledgeItemWrite", "BrandKnowledgeItemUpdate",
        "KnowledgeExtractRequest", "KnowledgeExtractCandidate", "KnowledgeExtractJobView", "KnowledgeExtractSaveItem",
        "KnowledgeExtractSaveRequest"
    ];

    [Fact]
    public async Task BrandKnowledgeRoutesSchemasAndEnumAreDocumentedInChinese()
    {
        var (openApi, swagger) = await LoadDocumentsAsync();
        using (openApi)
        using (swagger)
        {
            var paths = swagger.RootElement.GetProperty("paths");
            var found = new List<string>();
            foreach (var path in paths.EnumerateObject())
            {
                if (!path.Name.StartsWith(ItemsPath, StringComparison.Ordinal) && !path.Name.StartsWith(JobsPath, StringComparison.Ordinal)) continue;
                foreach (var operation in path.Value.EnumerateObject())
                {
                    var subject = $"{operation.Name.ToUpperInvariant()} {path.Name}";
                    found.Add(subject);
                    AssertChinese(operation.Value.GetProperty("summary").GetString(), subject);
                    Assert.False(operation.Value.TryGetProperty("deprecated", out var deprecated) && deprecated.GetBoolean(), $"{subject} 不应标记为不再推荐");
                    if (operation.Value.TryGetProperty("parameters", out var parameters))
                        foreach (var parameter in parameters.EnumerateArray())
                            AssertChinese(parameter.TryGetProperty("description", out var description) ? description.GetString() : null,
                                $"{subject} {parameter.GetProperty("name").GetString()}");
                    if (operation.Value.TryGetProperty("requestBody", out var body))
                        AssertChinese(body.TryGetProperty("description", out var description) ? description.GetString() : null, $"{subject} body");
                    foreach (var response in operation.Value.GetProperty("responses").EnumerateObject())
                        AssertChinese(response.Value.TryGetProperty("description", out var description) ? description.GetString() : null,
                            $"{subject} {response.Name}");
                }
            }
            Assert.Equal(ExpectedOperations.Order(StringComparer.Ordinal), found.Order(StringComparer.Ordinal));

            // 成功响应的状态码与类型。
            var list = paths.GetProperty(ItemsPath).GetProperty("get");
            Assert.Contains("dimension", list.GetProperty("parameters").EnumerateArray().Select(p => p.GetProperty("name").GetString()));
            Assert.EndsWith("/BrandKnowledgeItemList", ResponseRef(list, "200"));
            Assert.EndsWith("/BrandKnowledgeItemView", ResponseRef(paths.GetProperty(ItemsPath).GetProperty("post"), "201"));
            Assert.EndsWith("/BrandKnowledgeItemView", ResponseRef(paths.GetProperty($"{ItemsPath}/{{itemId}}").GetProperty("patch"), "200"));
            Assert.True(paths.GetProperty($"{ItemsPath}/{{itemId}}").GetProperty("delete").GetProperty("responses").TryGetProperty("204", out _));
            Assert.EndsWith("/KnowledgeExtractJobView", ResponseRef(paths.GetProperty(JobsPath).GetProperty("post"), "202"));
            var current = paths.GetProperty($"{JobsPath}/current").GetProperty("get");
            Assert.EndsWith("/KnowledgeExtractJobView", ResponseRef(current, "200"));
            Assert.True(current.GetProperty("responses").TryGetProperty("204", out _));
            var save = paths.GetProperty($"{JobsPath}/{{jobId}}/save").GetProperty("post");
            var saveSchema = ResponseSchema(save, "201");
            Assert.Equal("array", saveSchema.GetProperty("type").GetString());
            Assert.EndsWith("/BrandKnowledgeItemView", saveSchema.GetProperty("items").GetProperty("$ref").GetString());
            foreach (var status in new[] { "400", "409", "410" })
                Assert.True(save.GetProperty("responses").TryGetProperty(status, out _), $"save 缺少 {status} 说明");

            var schemas = swagger.RootElement.GetProperty("components").GetProperty("schemas");
            foreach (var schema in DocumentedSchemas)
            {
                Assert.True(schemas.TryGetProperty(schema, out var definition), $"缺少 schema {schema}");
                foreach (var property in definition.GetProperty("properties").EnumerateObject())
                    AssertChinese(property.Value.TryGetProperty("description", out var description) ? description.GetString() : null,
                        $"{schema}.{property.Name}");
            }
            Assert.Equal(["brand_intro", "selling_point", "audience", "differentiator", "product_service", "faq"],
                schemas.GetProperty("BrandKnowledgeDimension").GetProperty("enum").EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray());

            // 前端类型从 /openapi/v1.json 生成：新 schema 与路由必须同时出现在该文档中。
            var openPaths = openApi.RootElement.GetProperty("paths");
            foreach (var operation in ExpectedOperations)
            {
                var (method, path) = (operation[..operation.IndexOf(' ')].ToLowerInvariant(), operation[(operation.IndexOf(' ') + 1)..]);
                Assert.True(openPaths.TryGetProperty(path, out var item) && item.TryGetProperty(method, out _), $"openapi 缺少 {operation}");
            }
            var openSchemas = openApi.RootElement.GetProperty("components").GetProperty("schemas");
            foreach (var schema in DocumentedSchemas)
                Assert.True(openSchemas.TryGetProperty(schema, out _), $"openapi 缺少 {schema}");
            var required = openSchemas.GetProperty("BrandKnowledgeItemList").GetProperty("required").EnumerateArray().Select(item => item.GetString()).ToArray();
            foreach (var name in new[] { "items", "counts", "total", "limit" }) Assert.Contains(name, required);
        }
    }

    [Fact]
    public async Task LegacyProductAndFileUploadOperationsAreDeprecatedAndLinkRouteIsRemoved()
    {
        var (openApi, swagger) = await LoadDocumentsAsync();
        using (openApi)
        using (swagger)
        {
            foreach (var (document, name) in new[] { (swagger, "swagger"), (openApi, "openapi") })
            {
                var paths = document.RootElement.GetProperty("paths");
                var products = paths.EnumerateObject().Where(path => path.Name.StartsWith(Root + "/products", StringComparison.Ordinal)).ToArray();
                Assert.NotEmpty(products);
                foreach (var path in products)
                    foreach (var operation in path.Value.EnumerateObject().Where(operation => IsHttpMethod(operation.Name)))
                        Assert.True(IsDeprecated(operation.Value), $"{name} {operation.Name} {path.Name} 未标记为不再推荐");
                foreach (var upload in new[] { "/api/v1/knowledge/documents", Root + "/documents" })
                {
                    var item = paths.GetProperty(upload);
                    Assert.True(IsDeprecated(item.GetProperty("post")), $"{name} POST {upload} 未标记为不再推荐");
                    Assert.False(IsDeprecated(item.GetProperty("get")), $"{name} GET {upload} 读接口不应标记为不再推荐");
                }
                // 链接资料入口已被「从网址识别」替代，并于 2026-11 下线（C1 统一授权基线：先抓取后鉴权）。
                Assert.False(paths.TryGetProperty(Root + "/links", out _), $"{name} 仍发布已下线的 POST {Root}/links");
                // 资料读接口与停用、重试等仍保留（条目投影资料由 Store 拒绝改写），不标记为不再推荐。
                foreach (var (path, method) in new[]
                         {
                             ("/api/v1/knowledge/documents/{documentId}", "get"), (Root + "/documents/{documentId}", "patch"),
                             (Root + "/documents/{documentId}/disable", "post"), (Root + "/documents/{documentId}/retry", "post")
                         })
                    Assert.False(IsDeprecated(paths.GetProperty(path).GetProperty(method)), $"{name} {method} {path} 不应标记为不再推荐");
            }
        }
    }

    private static bool IsHttpMethod(string name) => name is "get" or "post" or "patch" or "put" or "delete";

    private static bool IsDeprecated(JsonElement operation)
        => operation.TryGetProperty("deprecated", out var deprecated) && deprecated.ValueKind == JsonValueKind.True;

    private static JsonElement ResponseSchema(JsonElement operation, string status)
        => operation.GetProperty("responses").GetProperty(status).GetProperty("content")
            .EnumerateObject().First(content => content.Name.Contains("json", StringComparison.Ordinal)).Value.GetProperty("schema");

    private static string ResponseRef(JsonElement operation, string status) => ResponseSchema(operation, status).GetProperty("$ref").GetString()!;

    private static void AssertChinese(string? text, string subject)
        => Assert.True(text is not null && text.Any(character => character is >= '一' and <= '鿿'),
            $"{subject} 缺少中文说明：{text ?? "<null>"}");

    private static async Task<(JsonDocument OpenApi, JsonDocument Swagger)> LoadDocumentsAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(BrandKnowledgeController).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddControllers().AddApplicationPart(typeof(BrandKnowledgeController).Assembly)
            .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddOpenApi(options => options.AddOperationTransformer<ObsoleteOpenApiOperationTransformer>());
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
}
