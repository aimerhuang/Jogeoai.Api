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

/// <summary>文章创作接口契约：只加载真实 Controller/DTO 元数据，不注册数据库、不执行业务入口。</summary>
public sealed class ArticleStudioApiDocumentationTests
{
    private const string PreparePath = "/api/v1/sites/{siteId}/article-studio/prepare";
    private const string GenerationsPath = "/api/v1/sites/{siteId}/article-studio/generations";
    private const string GenerationPath = "/api/v1/sites/{siteId}/article-studio/generations/{taskId}";

    private static readonly string[] StudioSchemas =
    [
        "ArticleStudioPrepareView", "ArticleStudioPromptOption", "ArticleStudioSelectedPromptView", "ArticleStudioKnowledgeView",
        "ArticleStudioCompetitorsView", "ArticleStudioCompetitorView", "ArticleStudioExistingArticleView",
        "ArticleStudioGenerationRequest", "ArticleStudioGenerationAccepted", "ArticleStudioGenerationView",
        "ArticleStudioStageView", "ArticleStudioStageDetail", "ArticleStudioSourceView"
    ];

    [Fact]
    public async Task StudioEndpointsAndSchemasAreDocumentedInChinese()
    {
        var (openApi, swagger) = await LoadDocumentsAsync();
        using (openApi)
        using (swagger)
        {
            foreach (var document in new[] { openApi, swagger })
            {
                var paths = document.RootElement.GetProperty("paths");
                var prepare = paths.GetProperty(PreparePath).GetProperty("get");
                Assert.Contains(prepare.GetProperty("parameters").EnumerateArray(), parameter => parameter.GetProperty("name").GetString() == "promptId");
                Assert.EndsWith("/ArticleStudioPrepareView", ResponseRef(prepare, "200"));
                Assert.EndsWith("/ArticleStudioGenerationAccepted", ResponseRef(paths.GetProperty(GenerationsPath).GetProperty("post"), "202"));
                Assert.EndsWith("/ArticleStudioGenerationView", ResponseRef(paths.GetProperty(GenerationPath).GetProperty("get"), "200"));
            }

            var swaggerPaths = swagger.RootElement.GetProperty("paths");
            foreach (var (path, verb) in new[] { (PreparePath, "get"), (GenerationsPath, "post"), (GenerationPath, "get") })
            {
                var operation = swaggerPaths.GetProperty(path).GetProperty(verb);
                AssertChinese(operation.GetProperty("summary").GetString(), $"{verb} {path}");
                foreach (var parameter in operation.GetProperty("parameters").EnumerateArray())
                    AssertChinese(parameter.GetProperty("description").GetString(), parameter.GetProperty("name").GetString());
            }
            var post = swaggerPaths.GetProperty(GenerationsPath).GetProperty("post");
            foreach (var status in new[] { "400", "402", "409", "503" })
                Assert.True(post.GetProperty("responses").TryGetProperty(status, out _), status);

            var schemas = swagger.RootElement.GetProperty("components").GetProperty("schemas");
            foreach (var schema in StudioSchemas)
                foreach (var property in schemas.GetProperty(schema).GetProperty("properties").EnumerateObject())
                    AssertChinese(property.Value.TryGetProperty("description", out var description) ? description.GetString() : null,
                        $"{schema}.{property.Name}");
            Assert.Equal(["mention", "ranking", "defend"], EnumValues(schemas, "ArticleOptimizationGoal"));
            Assert.Equal(["citations", "fetch", "structure", "web_research", "requirements", "compose"], EnumValues(schemas, "ArticleStudioStageKey"));
            Assert.Equal(["pending", "running", "succeeded", "skipped", "failed"], EnumValues(schemas, "ArticleStudioStageStatus"));
            Assert.Equal(["pending", "fetched", "blocked", "failed", "skipped"], EnumValues(schemas, "ArticleStudioFetchStatus"));
            Assert.Equal(["citation", "web_search"], EnumValues(schemas, "ArticleStudioSourceKind"));
            Assert.Equal(["none_configured", "no_recent_data"], EnumValues(schemas, "ArticleStudioCompetitorReason"));

            var openSchemas = openApi.RootElement.GetProperty("components").GetProperty("schemas");
            foreach (var name in new[] { "prompts", "selected", "unitPrice", "availableCredits", "providerConfigured", "requirementsMaxLength" })
                Assert.Contains(name, Required(openSchemas, "ArticleStudioPrepareView"));
            foreach (var name in new[] { "batchId", "taskId", "articleId", "reservedCredits", "regeneratedAsNew", "pollPath" })
                Assert.Contains(name, Required(openSchemas, "ArticleStudioGenerationAccepted"));
            foreach (var name in new[] { "stages", "sources", "fetchedCount", "hitCount", "versionId", "errorCode" })
                Assert.Contains(name, Required(openSchemas, "ArticleStudioGenerationView"));
            Assert.Contains("promptId", Required(openSchemas, "ArticleStudioGenerationRequest"));
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
            ApplicationName = typeof(ArticleStudioController).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddControllers().AddApplicationPart(typeof(ArticleStudioController).Assembly)
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
