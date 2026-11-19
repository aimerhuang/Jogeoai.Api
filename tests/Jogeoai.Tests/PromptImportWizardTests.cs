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

/// <summary>批量创建（新增监测问题弹窗）的无库契约：旧请求幂等哈希不变、新增行字段、/prompts/batch 权限与传参、Swagger 中文。</summary>
public sealed class PromptImportWizardTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // Computed with the pre-change code (PromptImportRow(Content, Locale, TagIds) serialized by JsonSerializer.Serialize(rows)
    // and SHA-256 hex) on 2026-11-03 before the new row fields existed. Old clients must keep hashing to exactly this value.
    private const string LegacyHash = "5ac453cf28c3571f78843f08376a014e546dad78c0fd47544ce21b4c8953a766";

    private static IReadOnlyList<PromptImportRow> LegacyRows() =>
    [
        new PromptImportRow("2026 年跑鞋怎么选", "zh-CN"),
        new PromptImportRow("Best running shoes?", "en-US", [Guid.Parse("11111111-2222-3333-4444-555555555555")]),
    ];

    [Fact]
    public void LegacyImportRequestHashIsUnchanged()
    {
        Assert.Equal(LegacyHash, ManagementStore.ImportRequestHash(LegacyRows(), null));
        // The same body as an old client sends it (web JSON, no new fields) deserializes to rows with null new fields.
        var body = """[{"content":"2026 年跑鞋怎么选","locale":"zh-CN"},{"content":"Best running shoes?","locale":"en-US","tagIds":["11111111-2222-3333-4444-555555555555"]}]""";
        var rows = JsonSerializer.Deserialize<IReadOnlyList<PromptImportRow>>(body, Web)!;
        Assert.Equal(LegacyHash, ManagementStore.ImportRequestHash(rows, null));
        // Explicit nulls for the new fields hash exactly like omitted ones.
        var explicitNulls = JsonSerializer.Deserialize<IReadOnlyList<PromptImportRow>>(
            """[{"content":"2026 年跑鞋怎么选","locale":"zh-CN","groupId":null,"groupName":null,"brandedStatus":null,"source":null},{"content":"Best running shoes?","locale":"en-US","tagIds":["11111111-2222-3333-4444-555555555555"]}]""", Web)!;
        Assert.Equal(LegacyHash, ManagementStore.ImportRequestHash(explicitNulls, null));
    }

    [Fact]
    public void NewFieldsAndRequestScopeChangeTheHash()
    {
        var rows = LegacyRows();
        var hashes = new[]
        {
            ManagementStore.ImportRequestHash(rows, null),
            ManagementStore.ImportRequestHash([rows[0] with { GroupName = "跑鞋" }, rows[1]], null),
            ManagementStore.ImportRequestHash([rows[0] with { GroupId = Guid.NewGuid() }, rows[1]], null),
            ManagementStore.ImportRequestHash([rows[0] with { BrandedStatus = "branded" }, rows[1]], null),
            ManagementStore.ImportRequestHash([rows[0] with { Source = "batch_import" }, rows[1]], null),
            ManagementStore.ImportRequestHash(rows, ["a"]),
            ManagementStore.ImportRequestHash(rows, ["b"]),
            ManagementStore.ImportRequestHash(rows, []),
        };
        Assert.Equal(hashes.Length, hashes.Distinct().Count());
    }

    [Fact]
    public void BatchBodyBindsRowsWithWizardFieldsAndRequestScope()
    {
        var group = Guid.NewGuid();
        var request = JsonSerializer.Deserialize<PromptBatchCreateRequest>($$"""
            {"rows":[{"content":"AI 写作工具哪个好","locale":"zh-CN","groupName":"AI 写作工具","source":"keyword_expand"},
                     {"content":"X 品牌怎么样","locale":"zh-CN","groupId":"{{group}}","brandedStatus":"branded","source":"ai_recommend"}],
             "targetKeys":["k1","k2"]}
            """, Web)!;
        Assert.Equal(("AI 写作工具", "keyword_expand", (Guid?)null), (request.Rows[0].GroupName, request.Rows[0].Source, request.Rows[0].GroupId));
        Assert.Equal((group, "branded", "ai_recommend"), (request.Rows[1].GroupId!.Value, request.Rows[1].BrandedStatus, request.Rows[1].Source));
        Assert.Equal(["k1", "k2"], request.TargetKeys);
        Assert.Null(JsonSerializer.Deserialize<PromptBatchCreateRequest>("""{"rows":[{"content":"q"}]}""", Web)!.TargetKeys);
    }

    [Fact]
    public void PreviewRowsDefaultToNoGroup()
    {
        var json = JsonSerializer.SerializeToElement(new PromptImportRowResult(1, null, null), Web);
        Assert.Equal("none", json.GetProperty("groupResolution").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("groupId").ValueKind);
    }

    [Fact]
    public async Task ViewerCannotBatchCreateAndTheStoreIsNotCalled()
    {
        var (controller, proxy, _) = Controller("viewer", "batch-key-0001");
        var result = await controller.CreateBatch(Guid.NewGuid(), new PromptBatchCreateRequest([new("q")], ["k"]), default);
        Assert.Equal(403, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        Assert.Null(proxy.Args);
    }

    [Fact]
    public async Task BatchPassesRowsHeaderKeyAndScopeToTheSharedImport()
    {
        var (controller, proxy, tenant) = Controller("member", "batch-key-0002");
        var site = Guid.NewGuid();
        var rows = new[] { new PromptImportRow("q", "zh-CN", GroupName: "g", Source: "batch_import") };
        var result = await controller.CreateBatch(site, new PromptBatchCreateRequest(rows, ["k1"]), default);
        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(nameof(IManagementStore.ImportAsync), proxy.Method);
        Assert.Equal(tenant, proxy.Args![0]);
        Assert.Equal(site, proxy.Args[1]);
        Assert.Same(rows, proxy.Args[2]);
        Assert.Equal("batch-key-0002", proxy.Args[3]);
        Assert.Equal(new[] { "k1" }, Assert.IsAssignableFrom<IReadOnlyList<string>>(proxy.Args[5]));

        // The legacy /import route reaches the same method without a request scope.
        await controller.Import(site, rows, default);
        Assert.Null(proxy.Args[5]);
    }

    [Fact]
    public async Task PlanLimitFromBatchKeepsTheUpgradeDetails()
    {
        var (controller, proxy, _) = Controller("admin", "batch-key-0003");
        proxy.Failure = new PlanLimitExceededException("free_trial", "免费体验版", PlanLimitKind.Prompts, 20, 19);
        var result = Assert.IsAssignableFrom<ObjectResult>(await controller.CreateBatch(Guid.NewGuid(),
            new PromptBatchCreateRequest([new("a"), new("b")]), default));
        Assert.Equal(409, result.StatusCode);
        var error = JsonSerializer.SerializeToElement(result.Value, Web).GetProperty("error");
        Assert.Equal("PLAN_LIMIT_EXCEEDED", error.GetProperty("code").GetString());
        Assert.Equal(20, error.GetProperty("details").GetProperty("limitValue").GetInt32());
    }

    [Fact]
    public async Task WizardRoutesAndSchemasAreDocumentedInChinese()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(ManagementController).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production,
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddControllers().AddApplicationPart(typeof(ManagementController).Assembly)
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
            foreach (var path in new[]
            {
                "/api/v1/sites/{siteId}/prompts/batch", "/api/v1/sites/{siteId}/prompts/import",
                "/api/v1/sites/{siteId}/prompts/import-preview", "/api/v1/sites/{siteId}/prompt-monitoring/estimate",
                "/api/v1/sites/{siteId}/targets/estimate",
            })
                Assert.True(HasChinese(paths.GetProperty(path).GetProperty("post").GetProperty("summary").GetString()), path);
            var schemas = swagger.RootElement.GetProperty("components").GetProperty("schemas");
            foreach (var (schema, property) in new[]
            {
                ("PromptImportRow", "groupId"), ("PromptImportRow", "groupName"), ("PromptImportRow", "brandedStatus"),
                ("PromptImportRow", "source"), ("PromptBatchCreateRequest", "rows"), ("PromptBatchCreateRequest", "targetKeys"),
                ("PromptImportRowResult", "groupResolution"), ("PromptImportRowResult", "groupId"),
                ("PromptWrite", "targetKeys"), ("PromptPatch", "targetKeys"), ("PromptPatch", "resetTargetScope"),
                ("PromptView", "targetScope"), ("PromptView", "targetKeys"), ("PromptView", "creationSource"),
                // 11-11 instant collection after new prompts / added models.
                ("PromptView", "instantCollection"), ("PromptImportResult", "createdPromptIds"), ("PromptImportResult", "instantCollection"),
                ("InstantCollectionView", "status"), ("InstantCollectionView", "runId"), ("InstantCollectionView", "checkCount"),
                ("InstantCollectionView", "estimatedCredits"), ("InstantCollectionView", "reasonCode"), ("InstantCollectionView", "message"),
                ("InstantCollectionView", "shortfall"),
                ("CollectionTargetView", "targetKey"), ("CollectionTargetView", "monitoringEligible"),
                // 11-09 brand model modes: answer mode × frequency contract.
                ("CollectionTargetView", "supportedAnswerModes"), ("CollectionTargetView", "defaultAnswerMode"),
                ("CollectionTargetView", "answerMode"), ("CollectionTargetView", "frequency"), ("CollectionTargetView", "frequencyAnchor"),
                ("SiteTargetWrite", "answerMode"), ("SiteTargetWrite", "frequency"), ("SiteTargetsWrite", "scope"),
                ("CloroTargetRequest", "answerMode"), ("CloroVisibilityCheckView", "answerMode"),
                ("MonitoringRecordView", "answerMode"), ("MonitoringAnswerDetailView", "answerMode"),
                ("PromptMonitoringEstimateRequest", "newPromptCount"), ("PromptMonitoringEstimateRequest", "targetKeys"),
                ("PromptMonitoringEstimateView", "unitPrice"), ("PromptMonitoringEstimateView", "sampleCount"),
                ("PromptMonitoringEstimateView", "currentPromptCount"), ("PromptMonitoringEstimateView", "currentChecksPerRound"),
                ("PromptMonitoringEstimateView", "currentCreditsPerRound"), ("PromptMonitoringEstimateView", "addedChecksPerRound"),
                ("PromptMonitoringEstimateView", "addedCreditsPerRound"), ("PromptMonitoringEstimateView", "totalChecksPerRound"),
                ("PromptMonitoringEstimateView", "totalCreditsPerRound"), ("PromptMonitoringEstimateView", "availableCredits"),
                ("PromptMonitoringEstimateView", "shortfall"), ("PromptMonitoringEstimateView", "blockers"),
                // 11-09 brand model modes: POST /sites/{siteId}/targets/estimate.
                ("MonitoringTargetsEstimateRequest", "targets"),
                ("MonitoringTargetsEstimateView", "promptCount"), ("MonitoringTargetsEstimateView", "weeklyChecks"),
                ("MonitoringTargetsEstimateView", "weeklyCredits"), ("MonitoringTargetsEstimateView", "nextSlotChecks"),
                ("MonitoringTargetsEstimateView", "nextSlotCredits"), ("MonitoringTargetsEstimateView", "cells"),
                ("MonitoringTargetsEstimateView", "balance"), ("MonitoringTargetsEstimateView", "shortfall"),
                ("MonitoringTargetsEstimateView", "blockers"), ("MonitoringTargetsEstimateView", "unitPrice"),
                ("MonitoringTargetsEstimateView", "sampleCount"), ("MonitoringTargetsEstimateView", "nextSlotAt"),
                ("MonitoringTargetsEstimateView", "nextSlotKind"),
                ("MonitoringTargetCellEstimate", "targetKey"), ("MonitoringTargetCellEstimate", "engine"),
                ("MonitoringTargetCellEstimate", "answerMode"), ("MonitoringTargetCellEstimate", "frequency"),
                ("MonitoringTargetCellEstimate", "weeklyRuns"), ("MonitoringTargetCellEstimate", "promptCount"),
            })
            {
                var description = schemas.GetProperty(schema).GetProperty("properties").GetProperty(property);
                Assert.True(HasChinese(description.TryGetProperty("description", out var text) ? text.GetString() : null), $"{schema}.{property}");
            }
        }
        finally { await app.StopAsync(); }
    }

    private static bool HasChinese(string? value) => value?.Any(character => character is >= '一' and <= '鿿') == true;

    private static (ManagementController Controller, ImportProxy Proxy, Guid Tenant) Controller(string role, string idempotencyKey)
    {
        var tenant = Guid.NewGuid();
        var context = new DefaultHttpContext();
        context.Items[SessionAuthenticationMiddleware.ContextItemKey] = new AuthenticatedRequestContext(
            new AuthUserView(Guid.NewGuid(), "qa@example.test", "QA", "active", [new TenantMembershipView(tenant, "QA", role, "active")]),
            Guid.NewGuid(), "test-only");
        context.Request.Headers["Idempotency-Key"] = idempotencyKey;
        var store = DispatchProxy.Create<IManagementStore, ImportProxy>();
        var controller = new ManagementController(store, new TenantContextResolver(new TestEnvironment()))
            { ControllerContext = new ControllerContext { HttpContext = context } };
        return (controller, (ImportProxy)(object)store, tenant);
    }

    public class ImportProxy : DispatchProxy
    {
        public string? Method { get; private set; }
        public object?[]? Args { get; private set; }
        public CollectorStoreException? Failure { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IManagementStore.ImportAsync))
                throw new InvalidOperationException($"Unexpected management store call: {targetMethod?.Name}");
            Method = targetMethod.Name;
            Args = args;
            return Failure is not null ? Task.FromException<PromptImportResult>(Failure)
                : Task.FromResult(new PromptImportResult(1, 0, [Guid.NewGuid()]));
        }
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
