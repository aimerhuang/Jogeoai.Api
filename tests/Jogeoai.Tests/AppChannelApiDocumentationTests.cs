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

/// <summary>
/// App 渠道（任务 11-16-app-channel-monitoring-api，implement 9.4）新增或改动的公开接口在 OpenAPI 里存在并带中文说明。
/// 只加载真实 Controller 与 DTO 的元数据，不启动 Program、不注册数据库、不执行业务入口。
/// 设置 JOGEOAI_APP_CHANNEL_OPENAPI_OUTPUT（文件路径）时，把完整的 /openapi/v1.json 写到该路径，
/// 同目录下另写一份 Swashbuckle 文档（文件名加 .swagger），供前端类型生成脚本合并字符串枚举与说明。
/// </summary>
public sealed class AppChannelApiDocumentationTests(ITestOutputHelper output)
{
    private const string CollectorPrefix = "/internal/collector/app/";
    private const string AdminPrefix = "/api/v1/admin/collector-app/";
    private const string StatusPath = "/api/v1/sites/{siteId}/app-collection/status";

    private static readonly (string Path, string Method)[] CollectorOperations =
    [
        (CollectorPrefix + "bindings/report", "post"), (CollectorPrefix + "leases", "post"), (CollectorPrefix + "heartbeats", "post"),
        (CollectorPrefix + "sent", "post"), (CollectorPrefix + "failures", "post"), (CollectorPrefix + "results", "post")
    ];

    private static readonly (string Path, string Method)[] AdminOperations =
    [
        (AdminPrefix + "nodes", "get"), (AdminPrefix + "nodes", "post"), (AdminPrefix + "nodes/{nodeId}/token", "post"),
        (AdminPrefix + "nodes/{nodeId}/disable", "post"), (AdminPrefix + "nodes/{nodeId}/resume", "post"),
        (AdminPrefix + "nodes/{nodeId}/max-devices", "put"),
        (AdminPrefix + "bindings", "get"), (AdminPrefix + "bindings/{bindingId}/disable", "post"),
        (AdminPrefix + "bindings/{bindingId}/resume", "post"), (AdminPrefix + "bindings/{bindingId}/limits", "put"),
        (AdminPrefix + "platforms", "get"), (AdminPrefix + "platforms/{platformCode}/pause", "post"),
        (AdminPrefix + "platforms/{platformCode}/resume", "post"), (AdminPrefix + "platforms/{platformCode}/limits", "put"),
        (AdminPrefix + "platforms/{platformCode}/entry/restore", "post"), (AdminPrefix + "capacity", "get"),
        (AdminPrefix + "tasks/{taskId}/attempts", "get"),
        (AdminPrefix + "prompt-exclusions", "post"), (AdminPrefix + "prompt-exclusions/revoke", "post"),
        (AdminPrefix + "events", "get"), (AdminPrefix + "alerts", "get"), (AdminPrefix + "daily-reports", "get"),
        (AdminPrefix + "daily-reports/{reportDate}", "get")
    ];

    [Fact]
    public async Task AppChannelRoutesAndFieldsAreDocumentedInChineseAndCanBeExportedWithoutDatabase()
    {
        var (openApiText, swaggerText) = await LoadDocumentsAsync();
        using var openApi = JsonDocument.Parse(openApiText);
        using var swagger = JsonDocument.Parse(swaggerText);
        var swaggerPaths = swagger.RootElement.GetProperty("paths");
        var openApiPaths = openApi.RootElement.GetProperty("paths");
        var schemas = swagger.RootElement.GetProperty("components").GetProperty("schemas");
        var missing = new List<string>();

        // 1. 新增路由：两份文档里都存在；Swashbuckle 文档里有中文摘要、中文参数说明、中文请求体与 200/201 响应的成员说明。
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, method) in CollectorOperations.Concat(AdminOperations).Append((StatusPath, "get")))
        {
            Assert.True(openApiPaths.TryGetProperty(path, out var openApiPath) && openApiPath.TryGetProperty(method, out _),
                $"{method.ToUpperInvariant()} {path} missing from /openapi/v1.json");
            Assert.True(swaggerPaths.TryGetProperty(path, out var swaggerPath) && swaggerPath.TryGetProperty(method, out _),
                $"{method.ToUpperInvariant()} {path} missing from swagger.json");
            var operation = swaggerPath.GetProperty(method);
            var label = $"{method.ToUpperInvariant()} {path}";
            if (!IsChinese(operation.TryGetProperty("summary", out var summary) ? summary.GetString() : null)) missing.Add(label + " summary");
            if (operation.TryGetProperty("parameters", out var parameters))
                foreach (var parameter in parameters.EnumerateArray())
                    if (!IsChinese(parameter.TryGetProperty("description", out var description) ? description.GetString() : null))
                        missing.Add($"{label} parameter {parameter.GetProperty("name").GetString()}");
            if (operation.TryGetProperty("requestBody", out var body))
                foreach (var reference in References(body)) CheckSchema(schemas, reference, visited, missing);
            foreach (var status in new[] { "200", "201" })
                if (operation.GetProperty("responses").TryGetProperty(status, out var response))
                    foreach (var reference in References(response)) CheckSchema(schemas, reference, visited, missing);
        }

        // 2. 现有接口上新增的字段与参数。
        void Property(string schema, string property)
        {
            if (!schemas.TryGetProperty(schema, out var definition) || !definition.GetProperty("properties").TryGetProperty(property, out var value))
            {
                missing.Add($"{schema}.{property} (absent)");
                return;
            }
            if (!IsChinese(Description(value))) missing.Add($"{schema}.{property}");
            foreach (var reference in References(value)) CheckSchema(schemas, reference, visited, missing);
        }

        Property("MonitoringTargetsEstimateView", "channels");   // POST /sites/{siteId}/targets/estimate
        Property("TeamMonitoringBrandEstimate", "channels");     // POST /team/monitoring-config/estimate（每个品牌行）
        foreach (var property in new[] { "captureMode", "channel", "errorCode" }) Property("MonitoringRecordView", property);
        foreach (var property in new[] { "captureMode", "channel", "app", "sourcesWithoutUrl" }) Property("MonitoringAnswerDetailView", property);
        Property("CreditUsageItem", "channel");                  // GET /credits/usage

        // 两个预估接口都返回带 channels 的类型。
        Assert.EndsWith("/MonitoringTargetsEstimateView", ResponseRef(swaggerPaths, "/api/v1/sites/{siteId}/targets/estimate", "post"));
        Assert.Contains("TeamMonitoringBrandEstimate", schemas.GetRawText());

        // 引用域名汇总一族的 channel 参数：可选，只有 api 与 app，带中文说明。
        const string domainSummary = "/api/v1/sites/{siteId}/analytics/citation-domain-summary";
        foreach (var suffix in new[] { "", "/domains", "/evidence", "/urls", "/domain-detail", "/url-prompts" })
        {
            var channel = swaggerPaths.GetProperty(domainSummary + suffix).GetProperty("get").GetProperty("parameters").EnumerateArray()
                .SingleOrDefault(parameter => parameter.GetProperty("name").GetString() == "channel");
            if (channel.ValueKind != JsonValueKind.Object) { missing.Add($"GET {domainSummary}{suffix} parameter channel (absent)"); continue; }
            Assert.False(channel.TryGetProperty("required", out var required) && required.GetBoolean());
            Assert.Equal(["api", "app"], channel.GetProperty("schema").GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
            if (!IsChinese(Description(channel))) missing.Add($"GET {domainSummary}{suffix} parameter channel");
        }

        // 渠道枚举的取值。
        Assert.Equal(["api", "app", "web"], EnumValues(schemas, "CollectionChannel"));

        foreach (var item in missing) output.WriteLine("Missing Chinese documentation: " + item);
        Assert.Empty(missing);

        var appPaths = openApiPaths.EnumerateObject().Count(path => path.Name.StartsWith(CollectorPrefix, StringComparison.Ordinal)
            || path.Name.StartsWith(AdminPrefix, StringComparison.Ordinal) || path.Name == StatusPath);
        output.WriteLine($"App channel paths in /openapi/v1.json: {appPaths}; schemas checked: {visited.Count}.");

        var destination = Environment.GetEnvironmentVariable("JOGEOAI_APP_CHANNEL_OPENAPI_OUTPUT");
        if (!string.IsNullOrWhiteSpace(destination))
        {
            var fullPath = Path.GetFullPath(destination);
            var directory = Path.GetDirectoryName(fullPath)!;
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(fullPath, openApiText);
            var swaggerPath = Path.Combine(directory, Path.GetFileNameWithoutExtension(fullPath) + ".swagger.json");
            await File.WriteAllTextAsync(swaggerPath, swaggerText);
            output.WriteLine($"Exported {fullPath} and {swaggerPath}; no database was registered or accessed.");
        }
    }

    // Walks one schema and every schema it references. Each property of an object schema needs a Chinese description.
    private static void CheckSchema(JsonElement schemas, string name, HashSet<string> visited, List<string> missing)
    {
        if (!visited.Add(name) || !schemas.TryGetProperty(name, out var schema)) return;
        if (schema.TryGetProperty("properties", out var properties))
            foreach (var property in properties.EnumerateObject())
            {
                if (!IsChinese(Description(property.Value))) missing.Add($"{name}.{property.Name}");
                foreach (var reference in References(property.Value)) CheckSchema(schemas, reference, visited, missing);
            }
        foreach (var keyword in new[] { "allOf", "oneOf", "anyOf" })
            if (schema.TryGetProperty(keyword, out var parts))
                foreach (var part in parts.EnumerateArray())
                    foreach (var reference in References(part)) CheckSchema(schemas, reference, visited, missing);
    }

    private static string? Description(JsonElement element)
    {
        if (element.TryGetProperty("description", out var description)) return description.GetString();
        // 引用类型的成员说明可能出现在 allOf 的包装里。
        if (element.TryGetProperty("allOf", out var parts))
            foreach (var part in parts.EnumerateArray())
                if (part.TryGetProperty("description", out var inner)) return inner.GetString();
        return null;
    }

    private static IEnumerable<string> References(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name == "$ref" && property.Value.GetString() is { } reference)
                        yield return reference[(reference.LastIndexOf('/') + 1)..];
                    else
                        foreach (var nested in References(property.Value)) yield return nested;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    foreach (var nested in References(item)) yield return nested;
                break;
        }
    }

    private static string ResponseRef(JsonElement paths, string path, string method)
        => paths.GetProperty(path).GetProperty(method).GetProperty("responses").GetProperty("200").GetProperty("content")
            .EnumerateObject().First().Value.GetProperty("schema").GetProperty("$ref").GetString()!;

    private static string?[] EnumValues(JsonElement schemas, string name)
        => schemas.GetProperty(name).GetProperty("enum").EnumerateArray().Select(value => value.GetString()).ToArray();

    private static bool IsChinese(string? text) => text is not null && text.Any(character => character is >= '一' and <= '鿿');

    private static async Task<(string OpenApi, string Swagger)> LoadDocumentsAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(AppCollectorController).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddControllers().AddApplicationPart(typeof(AppCollectorController).Assembly)
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
            return (await client.GetStringAsync("/openapi/v1.json"), await client.GetStringAsync("/swagger/v1/swagger.json"));
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
