using System.Text.Json;
using System.Text.Json.Serialization;
using Jogeoai.Api;
using Jogeoai.Api.Controllers;
using Jogeoai.Api.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;

namespace Jogeoai.Tests.Mcp;

/// <summary>令牌接口契约：路由、状态码与 DTO 说明均为中文；/mcp 端点不进入 Swagger / OpenAPI（前端类型不受影响）。只加载元数据，不连接数据库。</summary>
public sealed class McpTokensApiDocumentationTests
{
    private const string Root = "/api/v1/mcp-tokens";
    private static readonly string[] ExpectedOperations = [$"GET {Root}", $"POST {Root}", $"DELETE {Root}/{{id}}"];
    private static readonly string[] DocumentedSchemas = ["McpTokenCreateRequest", "McpTokenCreatedView", "McpTokenListView", "McpTokenView"];

    [Fact]
    public async Task TokenRoutesAndSchemasAreDocumentedInChineseAndMcpIsExcluded()
    {
        var (openApi, swagger) = await LoadDocumentsAsync();
        using (openApi)
        using (swagger)
        {
            var paths = swagger.RootElement.GetProperty("paths");
            var found = new List<string>();
            foreach (var path in paths.EnumerateObject().Where(path => path.Name.StartsWith(Root, StringComparison.Ordinal)))
                foreach (var operation in path.Value.EnumerateObject())
                {
                    var subject = $"{operation.Name.ToUpperInvariant()} {path.Name}";
                    found.Add(subject);
                    AssertChinese(operation.Value.GetProperty("summary").GetString(), subject);
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
            Assert.Equal(ExpectedOperations.Order(StringComparer.Ordinal), found.Order(StringComparer.Ordinal));

            var post = paths.GetProperty(Root).GetProperty("post");
            Assert.EndsWith("/McpTokenCreatedView", ResponseRef(post, "201"));
            foreach (var status in new[] { "400", "401", "403", "409", "503" })
                Assert.True(post.GetProperty("responses").TryGetProperty(status, out _), $"POST 缺少 {status} 说明");
            Assert.EndsWith("/McpTokenListView", ResponseRef(paths.GetProperty(Root).GetProperty("get"), "200"));
            var delete = paths.GetProperty($"{Root}/{{id}}").GetProperty("delete");
            Assert.True(delete.GetProperty("responses").TryGetProperty("204", out _));
            Assert.True(delete.GetProperty("responses").TryGetProperty("404", out _));

            var schemas = swagger.RootElement.GetProperty("components").GetProperty("schemas");
            foreach (var schema in DocumentedSchemas)
            {
                Assert.True(schemas.TryGetProperty(schema, out var definition), $"缺少 schema {schema}");
                foreach (var property in definition.GetProperty("properties").EnumerateObject())
                {
                    AssertChinese(property.Value.TryGetProperty("description", out var description) ? description.GetString() : null,
                        $"{schema}.{property.Name}");
                    Assert.DoesNotContain("hash", property.Name, StringComparison.OrdinalIgnoreCase);
                }
            }
            Assert.False(schemas.GetProperty("McpTokenView").GetProperty("properties").TryGetProperty("token", out _));

            foreach (var document in new[] { swagger, openApi })
                Assert.DoesNotContain(document.RootElement.GetProperty("paths").EnumerateObject(),
                    path => path.Name.StartsWith(McpTokenAuthenticationMiddleware.Path + "/", StringComparison.Ordinal)
                        || path.Name == McpTokenAuthenticationMiddleware.Path);
            var openPaths = openApi.RootElement.GetProperty("paths");
            foreach (var operation in ExpectedOperations)
            {
                var (method, path) = (operation[..operation.IndexOf(' ')].ToLowerInvariant(), operation[(operation.IndexOf(' ') + 1)..]);
                Assert.True(openPaths.TryGetProperty(path, out var item) && item.TryGetProperty(method, out _), $"openapi 缺少 {operation}");
            }
        }
    }

    private static string ResponseRef(JsonElement operation, string status)
        => operation.GetProperty("responses").GetProperty(status).GetProperty("content")
            .EnumerateObject().First(content => content.Name.Contains("json", StringComparison.Ordinal)).Value
            .GetProperty("schema").GetProperty("$ref").GetString()!;

    private static void AssertChinese(string? text, string subject)
        => Assert.True(text is not null && text.Any(character => character is >= '一' and <= '鿿'),
            $"{subject} 缺少中文说明：{text ?? "<null>"}");

    private static async Task<(JsonDocument OpenApi, JsonDocument Swagger)> LoadDocumentsAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(McpTokensController).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddControllers().AddApplicationPart(typeof(McpTokensController).Assembly)
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
        builder.Services.AddRateLimiter(options => options.AddMcpTokenPolicy());
        builder.Services.AddJogeoaiMcp();
        await using var app = builder.Build();
        app.MapOpenApi();
        app.UseSwagger();
        app.UseRateLimiter();
        app.MapControllers();
        app.MapJogeoaiMcp();
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
