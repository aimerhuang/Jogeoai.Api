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

/// <summary>文章编辑器接口契约：只加载真实 Controller/DTO 元数据，不注册数据库、不执行业务入口。</summary>
public sealed class ArticleEditorApiDocumentationTests
{
    private const string DraftPath = "/api/v1/sites/{siteId}/articles/{articleId}/draft";
    private const string RefinePath = "/api/v1/sites/{siteId}/articles/{articleId}/refine";
    private const string ExportPath = "/api/v1/sites/{siteId}/articles/{articleId}/export";
    private const string UploadPath = "/api/v1/sites/{siteId}/content/images";
    private const string ImagePath = "/api/v1/content/images/{imageId}";

    private static readonly string[] EditorSchemas =
        ["ArticleDraftRequest", "ArticleDraftResult", "ArticleRefineRequest", "ArticleRefineResult", "ContentImageUploadResult"];

    [Fact]
    public async Task EditorEndpointsAndSchemasAreDocumentedInChinese()
    {
        var (openApi, swagger) = await LoadDocumentsAsync();
        using (openApi)
        using (swagger)
        {
            foreach (var document in new[] { openApi, swagger })
            {
                var paths = document.RootElement.GetProperty("paths");
                Assert.EndsWith("/ArticleDraftResult", ResponseRef(paths.GetProperty(DraftPath).GetProperty("put"), "200"));
                Assert.EndsWith("/ArticleRefineResult", ResponseRef(paths.GetProperty(RefinePath).GetProperty("post"), "200"));
                Assert.EndsWith("/ContentImageUploadResult", ResponseRef(paths.GetProperty(UploadPath).GetProperty("post"), "201"));
                Assert.True(paths.GetProperty(ImagePath).TryGetProperty("get", out _));
                var export = paths.GetProperty(ExportPath).GetProperty("get");
                Assert.Contains(export.GetProperty("parameters").EnumerateArray(), parameter => parameter.GetProperty("name").GetString() == "format");
                var exportContent = export.GetProperty("responses").GetProperty("200").GetProperty("content");
                Assert.True(exportContent.TryGetProperty("text/markdown", out _));
                Assert.True(exportContent.TryGetProperty("application/vnd.openxmlformats-officedocument.wordprocessingml.document", out _));
            }

            var swaggerPaths = swagger.RootElement.GetProperty("paths");
            foreach (var (path, verb) in new[] { (DraftPath, "put"), (RefinePath, "post"), (ExportPath, "get"), (UploadPath, "post"), (ImagePath, "get") })
            {
                var operation = swaggerPaths.GetProperty(path).GetProperty(verb);
                AssertChinese(operation.GetProperty("summary").GetString(), $"{verb} {path}");
                foreach (var parameter in operation.TryGetProperty("parameters", out var parameters) ? parameters.EnumerateArray().ToArray() : [])
                    AssertChinese(parameter.GetProperty("description").GetString(), parameter.GetProperty("name").GetString());
            }
            foreach (var status in new[] { "400", "402", "409", "422", "502", "503", "504" })
                Assert.True(swaggerPaths.GetProperty(RefinePath).GetProperty("post").GetProperty("responses").TryGetProperty(status, out _), $"refine {status}");
            foreach (var status in new[] { "400", "409", "503" })
                Assert.True(swaggerPaths.GetProperty(DraftPath).GetProperty("put").GetProperty("responses").TryGetProperty(status, out _), $"draft {status}");
            foreach (var status in new[] { "413", "415" })
                Assert.True(swaggerPaths.GetProperty(UploadPath).GetProperty("post").GetProperty("responses").TryGetProperty(status, out _), $"upload {status}");
            Assert.True(swaggerPaths.GetProperty(UploadPath).GetProperty("post").GetProperty("requestBody").GetProperty("content").TryGetProperty("multipart/form-data", out _));

            var schemas = swagger.RootElement.GetProperty("components").GetProperty("schemas");
            foreach (var schema in EditorSchemas)
                foreach (var property in schemas.GetProperty(schema).GetProperty("properties").EnumerateObject())
                    AssertChinese(property.Value.TryGetProperty("description", out var description) ? description.GetString() : null,
                        $"{schema}.{property.Name}");
            AssertChinese(schemas.GetProperty("ArticleVersionView").GetProperty("properties").GetProperty("editRevision").GetProperty("description").GetString(),
                "ArticleVersionView.editRevision");
            Assert.Equal(["whole", "selection"], EnumValues(schemas, "ArticleRefineScope"));
            Assert.Equal(["markdown", "docx"], EnumValues(schemas, "ArticleExportFormat"));
            Assert.Contains("refined", EnumValues(schemas, "ArticleVersionKind"));

            var openSchemas = openApi.RootElement.GetProperty("components").GetProperty("schemas");
            foreach (var name in new[] { "title", "bodyMarkdown", "expectedRevision" })
                Assert.Contains(name, Required(openSchemas, "ArticleDraftRequest"));
            foreach (var name in new[] { "versionId", "editRevision", "createdNewVersion", "savedAt" })
                Assert.Contains(name, Required(openSchemas, "ArticleDraftResult"));
            foreach (var name in new[] { "scope", "instruction", "baseVersionId", "expectedRevision" })
                Assert.Contains(name, Required(openSchemas, "ArticleRefineRequest"));
            foreach (var name in new[] { "operationId", "versionId", "versionKind", "bodyMarkdown", "chargedCredits", "replayed" })
                Assert.Contains(name, Required(openSchemas, "ArticleRefineResult"));
            foreach (var name in new[] { "imageId", "url", "contentType", "byteSize" })
                Assert.Contains(name, Required(openSchemas, "ContentImageUploadResult"));
        }
    }

    [Fact]
    public async Task CreditUsageTypeExposesArticleRefine()
    {
        var (openApi, swagger) = await LoadDocumentsAsync();
        using (openApi)
        using (swagger)
        {
            var schemas = swagger.RootElement.GetProperty("components").GetProperty("schemas");
            Assert.Contains("article_refine", EnumValues(schemas, "CreditUsageType"));
        }
    }

    private static string[] EnumValues(JsonElement schemas, string name)
        => schemas.GetProperty(name).GetProperty("enum").EnumerateArray().Select(item => item.GetString()!).ToArray();

    private static string[] Required(JsonElement schemas, string name)
        => schemas.GetProperty(name).GetProperty("required").EnumerateArray().Select(item => item.GetString()!).ToArray();

    private static string ResponseRef(JsonElement operation, string status)
    {
        var schema = operation.GetProperty("responses").GetProperty(status).GetProperty("content")
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
            return (JsonDocument.Parse(openApiText), JsonDocument.Parse(swaggerText));
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
