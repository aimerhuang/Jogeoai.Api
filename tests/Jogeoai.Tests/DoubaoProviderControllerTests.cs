using System.Reflection;
using System.Text.Json;
using Jogeoai.Api;
using Jogeoai.Api.Controllers;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Providers.Abstractions;
using Jogeoai.Providers.Domestic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;

namespace Jogeoai.Tests;

public sealed class DoubaoProviderControllerTests
{
    private const string Credential = "ark-secret-key-4321";
    private const string ApiUrl = "https://ark.cn-beijing.volces.com/api/v3/responses";

    private static SaveAnswerProviderSettingsRequest ValidSave(string? credential = Credential, AnswerProviderOptions? options = null) =>
        new(ApiUrl, "doubao-seed-2-1-pro-260915", credential, true, 120, options);

    [Theory]
    [InlineData(false, 401, "AUTHENTICATION_REQUIRED")]
    [InlineData(true, 403, "ADMIN_REQUIRED")]
    public async Task EveryEndpointRequiresSystemAdministrator(bool authenticated, int status, string code)
    {
        var f = new Fixture(authenticated, admin: false);
        var results = new List<IActionResult?>
        {
            (await f.Controller.GetSettings(default)).Result,
            (await f.Controller.SaveSettings(ValidSave(), default)).Result,
            (await f.Controller.Test(new("问题"), default)).Result,
        };
        foreach (var result in results) AssertError(result!, status, code);
        Assert.Empty(f.Settings.Calls);
        Assert.Equal(0, f.Client.Calls);
    }

    [Fact]
    public async Task GetReturnsTheViewWithoutTheCredential()
    {
        var f = new Fixture();
        await f.Settings.SaveAsync("doubao_api", ValidSave(), default);
        var ok = Assert.IsType<OkObjectResult>((await f.Controller.GetSettings(default)).Result);
        var view = Assert.IsType<AnswerProviderSettingsView>(ok.Value);
        Assert.Equal(("doubao_api", ApiUrl, true, 120, true, "****4321"), (view.Provider, view.ApiUrl, view.Enabled, view.TimeoutSeconds, view.Configured, view.CredentialHint));
        Assert.Equal(["doubao"], view.Options.Sources);
        Assert.DoesNotContain(Credential, JsonSerializer.Serialize(view));
    }

    [Fact]
    public async Task GetWithoutARowReturnsTheUnconfiguredDefaults()
    {
        var f = new Fixture();
        var view = Assert.IsType<AnswerProviderSettingsView>(Assert.IsType<OkObjectResult>((await f.Controller.GetSettings(default)).Result).Value);
        Assert.Equal((false, false, DoubaoDefaults.ApiUrl, DoubaoDefaults.Model, 180), (view.Enabled, view.Configured, view.ApiUrl, view.Model, view.TimeoutSeconds));
        Assert.Null(view.CredentialHint);
    }

    [Fact]
    public async Task PutSavesAndKeepsTheCredentialWhenOmitted()
    {
        var f = new Fixture();
        var saved = Assert.IsType<OkObjectResult>((await f.Controller.SaveSettings(
            ValidSave(options: new() { ReasoningEffort = "Minimal", MaxToolCalls = 4 }), default)).Result);
        var view = Assert.IsType<AnswerProviderSettingsView>(saved.Value);
        Assert.True(view.Configured);
        Assert.Equal("****4321", view.CredentialHint);
        Assert.Equal(("web_search", 4, "minimal"), (view.Options.RequestMode, view.Options.MaxToolCalls, view.Options.ReasoningEffort));
        Assert.DoesNotContain(Credential, JsonSerializer.Serialize(view));
        Assert.Equal(Credential, f.Settings.StoredCredential);

        var again = Assert.IsType<AnswerProviderSettingsView>(Assert.IsType<OkObjectResult>(
            (await f.Controller.SaveSettings(ValidSave(credential: "  ") with { Enabled = false }, default)).Result).Value);
        Assert.Equal(Credential, f.Settings.StoredCredential);
        Assert.Equal("****4321", again.CredentialHint);
        Assert.False(again.Enabled);
        Assert.False(again.Configured);
    }

    [Theory]
    [InlineData("ftp://ark.example/api", null, null)]
    [InlineData("https://user:pw@ark.example/api", null, null)]
    [InlineData("https://ark.example/api?x=1", null, null)]
    [InlineData("relative/path", null, null)]
    [InlineData(ApiUrl, "chat", null)]
    [InlineData(ApiUrl, null, "extreme")]
    public async Task PutRejectsBadUrlRequestModeAndReasoningEffort(string url, string? requestMode, string? effort)
    {
        var f = new Fixture();
        var options = new AnswerProviderOptions { RequestMode = requestMode ?? "web_search", ReasoningEffort = effort };
        var request = ValidSave(options: options) with { ApiUrl = url };
        AssertError((await f.Controller.SaveSettings(request, default)).Result!, 400, "INVALID_PROVIDER_SETTINGS");
        Assert.Null(f.Settings.StoredCredential);
    }

    private const string MessagesUrl = "https://ark.cn-beijing.volces.com/api/compatible/v1/messages";

    [Theory]
    [InlineData(null, """{"requestMode":"messages","sources":["doubao"],"maxToolCalls":3,"reasoningEffort":null}""")]
    [InlineData(" Enabled ", """{"requestMode":"messages","sources":["doubao"],"maxToolCalls":3,"reasoningEffort":null,"thinking":"enabled"}""")]
    [InlineData("disabled", """{"requestMode":"messages","sources":["doubao"],"maxToolCalls":3,"reasoningEffort":null,"thinking":"disabled"}""")]
    public async Task PutAcceptsMessagesModeWithOptionalThinking(string? thinking, string json)
    {
        var f = new Fixture();
        var options = new AnswerProviderOptions { RequestMode = " Messages ", Thinking = thinking };
        var request = ValidSave(options: options) with { ApiUrl = MessagesUrl + "/" };
        var view = Assert.IsType<AnswerProviderSettingsView>(Assert.IsType<OkObjectResult>((await f.Controller.SaveSettings(request, default)).Result).Value);
        Assert.Equal((MessagesUrl, "messages", thinking?.Trim().ToLowerInvariant()), (view.ApiUrl, view.Options.RequestMode, view.Options.Thinking));
        Assert.Equal(json, AnswerProviderSettingsRules.Normalize("doubao_api", request).OptionsJson);
    }

    [Theory]
    [InlineData("messages", MessagesUrl, null, "minimal", null, "options.reasoningEffort 必须为空（messages 模式请使用 thinking 开关）。")]
    [InlineData("messages", MessagesUrl, "auto", null, null, "options.thinking 必须为空、disabled 或 enabled。")]
    [InlineData("messages", MessagesUrl, null, null, "bing", "options.sources 必须为 [\"doubao\"]（messages 模式固定使用豆包搜索）。")]
    [InlineData("messages", ApiUrl, null, null, null, "options.requestMode 为 messages 时 apiUrl 路径必须以 /messages 结尾。")]
    [InlineData("messages", "https://ark.cn-beijing.volces.com/api/compatible/v1/messages-beta", null, null, null, "options.requestMode 为 messages 时 apiUrl 路径必须以 /messages 结尾。")]
    [InlineData("web_search", ApiUrl, "enabled", null, null, "options.thinking 仅适用于 DeepSeek，豆包请使用 reasoningEffort。")]
    [InlineData("web_search", MessagesUrl, null, null, null, "options.requestMode 为 web_search 时 apiUrl 不能是 /messages 端点。")]
    [InlineData("web_search", MessagesUrl + "/", null, "low", null, "options.requestMode 为 web_search 时 apiUrl 不能是 /messages 端点。")]
    [InlineData("doubao_app", ApiUrl, "disabled", null, null, "options.thinking 仅适用于 DeepSeek，豆包请使用 reasoningEffort。")]
    [InlineData("chat", ApiUrl, null, null, null, "options.requestMode 必须为 web_search、messages 或 doubao_app。")]
    public async Task PutRejectsInconsistentRequestModeOptions(string mode, string url, string? thinking, string? effort, string? extraSource, string message)
    {
        var f = new Fixture();
        var options = new AnswerProviderOptions
        {
            RequestMode = mode, Thinking = thinking, ReasoningEffort = effort, Sources = extraSource is null ? ["doubao"] : ["doubao", extraSource],
        };
        var request = ValidSave(options: options) with { ApiUrl = url };
        AssertError((await f.Controller.SaveSettings(request, default)).Result!, 400, "INVALID_PROVIDER_SETTINGS");
        Assert.Null(f.Settings.StoredCredential);
        var exception = Assert.Throws<CollectorStoreException>(() => AnswerProviderSettingsRules.Normalize("doubao_api", request));
        Assert.Equal((AnswerProviderSettingsRules.InvalidSettings, 400, message), (exception.Code, exception.StatusCode, exception.Message));
    }

    [Fact]
    public void EndpointCheckAppliesToDoubaoOnly()
    {
        // doubao_app is a placeholder and is not tied to an endpoint; DeepSeek always uses a /messages endpoint in web_search mode.
        Assert.Equal("doubao_app", AnswerProviderSettingsRules.Normalize("doubao_api",
            ValidSave(options: new() { RequestMode = "doubao_app" }) with { ApiUrl = MessagesUrl }).Options.RequestMode);
        Assert.Equal("web_search", AnswerProviderSettingsRules.Normalize("deepseek_api",
            new SaveAnswerProviderSettingsRequest("https://api.deepseek.com/anthropic/v1/messages", "deepseek-chat")).Options.RequestMode);
        Assert.Throws<CollectorStoreException>(() => AnswerProviderSettingsRules.Normalize("deepseek_api",
            new SaveAnswerProviderSettingsRequest("https://api.deepseek.com/anthropic/v1/messages", "deepseek-chat",
                Options: new() { RequestMode = "messages", Sources = [] })));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(601)]
    public async Task PutRejectsTimeoutOutsideFiveToSixHundredSeconds(int timeout)
    {
        var f = new Fixture();
        AssertError((await f.Controller.SaveSettings(ValidSave() with { TimeoutSeconds = timeout }, default)).Result!, 400, "INVALID_PROVIDER_SETTINGS");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public async Task PutRejectsMaxToolCallsOutsideOneToTen(int maxToolCalls)
    {
        var f = new Fixture();
        AssertError((await f.Controller.SaveSettings(ValidSave(options: new() { MaxToolCalls = maxToolCalls }), default)).Result!, 400, "INVALID_PROVIDER_SETTINGS");
    }

    [Fact]
    public async Task MissingMigrationSurfacesAsServiceUnavailable()
    {
        var f = new Fixture();
        f.Settings.Failure = new CollectorStoreException("ANSWER_PROVIDER_SCHEMA_NOT_MIGRATED", "missing", 503);
        AssertError((await f.Controller.GetSettings(default)).Result!, 503, "ANSWER_PROVIDER_SCHEMA_NOT_MIGRATED");
        AssertError((await f.Controller.SaveSettings(ValidSave(), default)).Result!, 503, "ANSWER_PROVIDER_SCHEMA_NOT_MIGRATED");
    }

    [Fact]
    public async Task TestReturnsTheNormalizedAnswerAndUsageWithoutTheCredential()
    {
        var f = new Fixture();
        var ok = Assert.IsType<OkObjectResult>((await f.Controller.Test(new("杭州有哪些咖啡馆？", "zh-CN"), default)).Result);
        var result = Assert.IsType<DoubaoTestResult>(ok.Value);
        Assert.Equal(("doubao_api", "doubao", "doubao-seed-2-1-pro-260915", true), (result.Provider, result.Engine, result.ModelVersion, result.ModelVersionProvided));
        Assert.Equal("回答正文", result.TextContent);
        Assert.Equal(["查询一"], result.WebQueries);
        Assert.Equal("available", result.QueryAvailability);
        Assert.Equal("https://example.com/a", Assert.Single(result.Citations).Url);
        Assert.Equal((600L, 2L), (result.Usage!.TotalTokens, result.Usage.DoubaoSearchCalls));
        Assert.NotNull(result.ResultQuality);
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(Credential, json);
        Assert.DoesNotContain("ENC-SECRET", json);
        var request = Assert.Single(f.Client.Requests);
        Assert.Equal(("杭州有哪些咖啡馆？", "zh-CN", "CN", null), (request.Prompt, request.Locale, request.Country, request.ProviderTaskId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task TestRejectsBlankPromptBeforeCallingTheProvider(string prompt)
    {
        var f = new Fixture();
        AssertError((await f.Controller.Test(new(prompt), default)).Result!, 400, "INVALID_TEST_REQUEST");
        Assert.Equal(0, f.Client.Calls);
    }

    [Theory]
    [InlineData(DoubaoErrorCodes.NotConfigured, 503)]
    [InlineData(DoubaoErrorCodes.QuotaExceeded, 503)]
    [InlineData(DoubaoErrorCodes.RateLimited, 503)]
    [InlineData(DoubaoErrorCodes.Timeout, 504)]
    [InlineData(DoubaoErrorCodes.InputFiltered, 400)]
    [InlineData(DoubaoErrorCodes.InvalidRequest, 400)]
    [InlineData(DoubaoErrorCodes.AuthFailed, 502)]
    [InlineData(DoubaoErrorCodes.ServiceNotOpen, 502)]
    [InlineData(DoubaoErrorCodes.RequestFailed, 502)]
    [InlineData(DoubaoErrorCodes.InvalidResponse, 502)]
    public async Task TestMapsProviderErrorsToTheStandardEnvelope(string code, int status)
    {
        var f = new Fixture();
        f.Client.Failure = new DoubaoProviderException(code, "安全消息", code is DoubaoErrorCodes.Timeout or DoubaoErrorCodes.QuotaExceeded,
            code == DoubaoErrorCodes.QuotaExceeded ? 10 : null);
        var result = (await f.Controller.Test(new("问题"), default)).Result!;
        AssertError(result, status, code);
        var error = JsonSerializer.SerializeToElement(((ObjectResult)result).Value).GetProperty("error");
        Assert.Equal("安全消息", error.GetProperty("message").GetString());
        Assert.Equal(code is DoubaoErrorCodes.Timeout or DoubaoErrorCodes.QuotaExceeded, error.GetProperty("retryable").GetBoolean());
    }

    [Fact]
    public async Task DoubaoRoutesAreDocumentedInChineseInSwagger()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(DoubaoProviderController).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production,
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddControllers().AddApplicationPart(typeof(DoubaoProviderController).Assembly);
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(options =>
        {
            options.UseAllOfToExtendReferenceSchemas();
            options.SwaggerDoc("v1", new OpenApiInfo { Title = "Jogeoai API", Version = "v1" });
            foreach (var assembly in new[] { "Jogeoai.Api", "Jogeoai.Domain", "Jogeoai.Application", "Jogeoai.Providers.Abstractions", "Jogeoai.Providers.Domestic" })
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
            foreach (var (path, method) in new[]
            {
                ("/api/v1/providers/doubao/settings", "get"), ("/api/v1/providers/doubao/settings", "put"),
                ("/api/v1/providers/doubao/test", "post"),
            })
                Assert.True(HasChinese(paths.GetProperty(path).GetProperty(method).GetProperty("summary").GetString()), $"{method} {path}");
            var schemas = swagger.RootElement.GetProperty("components").GetProperty("schemas");
            foreach (var (schema, property) in new[]
            {
                ("AnswerProviderSettingsView", "credentialHint"), ("AnswerProviderSettingsView", "configured"),
                ("SaveAnswerProviderSettingsRequest", "credential"), ("AnswerProviderOptions", "maxToolCalls"),
                ("AnswerProviderOptions", "reasoningEffort"), ("DoubaoTestRequest", "prompt"), ("DoubaoTestResult", "usage"),
                ("DoubaoTestResult", "queryAvailability"), ("DoubaoUsageSummary", "doubaoSearchCalls"),
            })
            {
                var description = schemas.GetProperty(schema).GetProperty("properties").GetProperty(property);
                Assert.True(HasChinese(FindDescription(description)), $"{schema}.{property}");
            }
            // The credential never appears in any response schema.
            Assert.False(schemas.GetProperty("AnswerProviderSettingsView").GetProperty("properties").TryGetProperty("credential", out _));
            Assert.False(schemas.TryGetProperty("AnswerProviderConfiguration", out _));
        }
        finally { await app.StopAsync(); }
    }

    private static string? FindDescription(JsonElement property) =>
        property.TryGetProperty("description", out var description) ? description.GetString() : null;

    private static bool HasChinese(string? value) => value?.Any(character => character is >= '\u4e00' and <= '\u9fff') == true;

    private static void AssertError(IActionResult result, int status, string code)
    {
        var response = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, JsonSerializer.SerializeToElement(response.Value).GetProperty("error").GetProperty("code").GetString());
    }

    private sealed class Fixture
    {
        internal FakeSettingsStore Settings { get; } = new();
        internal FakeClient Client { get; } = new();
        internal DoubaoProviderController Controller { get; }

        internal Fixture(bool authenticated = true, bool admin = true)
        {
            var context = new DefaultHttpContext();
            if (authenticated)
                context.Items[SessionAuthenticationMiddleware.ContextItemKey] = new AuthenticatedRequestContext(
                    new(Guid.NewGuid(), "admin@example.test", "Admin", "active", []), Guid.NewGuid(), "session");
            var research = DispatchProxy.Create<IResearchStore, ResearchProxy>();
            ((ResearchProxy)(object)research).Admin = admin;
            Controller = new DoubaoProviderController(Settings, Client, research) { ControllerContext = new() { HttpContext = context } };
        }
    }

    // In-memory store that applies the real validation rules so controller tests exercise them end to end.
    private sealed class FakeSettingsStore : IAnswerProviderSettingsStore
    {
        private NormalizedAnswerProviderSettings? row;
        internal List<string> Calls { get; } = [];
        internal CollectorStoreException? Failure { get; set; }
        internal string? StoredCredential => row?.Credential;

        public Task<AnswerProviderSettingsView?> GetAsync(string provider, CancellationToken cancellationToken = default)
        {
            Calls.Add(nameof(GetAsync));
            if (Failure is not null) throw Failure;
            AnswerProviderSettingsRules.RequireProvider(provider);
            return Task.FromResult(row is null ? null : View(row));
        }

        public Task<AnswerProviderConfiguration?> GetConfigurationAsync(string provider, CancellationToken cancellationToken = default)
        {
            Calls.Add(nameof(GetConfigurationAsync));
            return Task.FromResult(row is { Enabled: true, Credential: not null }
                ? new AnswerProviderConfiguration(row.Provider, row.ApiUrl, row.Model, row.Credential, row.TimeoutSeconds, row.Options) : null);
        }

        public Task<AnswerProviderSettingsView> SaveAsync(string provider, SaveAnswerProviderSettingsRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add(nameof(SaveAsync));
            if (Failure is not null) throw Failure;
            var normalized = AnswerProviderSettingsRules.Normalize(provider, request);
            // COALESCE(VALUES(credential), credential)
            row = normalized with { Credential = normalized.Credential ?? row?.Credential, CredentialHint = normalized.CredentialHint ?? row?.CredentialHint };
            return Task.FromResult(View(row));
        }

        private static AnswerProviderSettingsView View(NormalizedAnswerProviderSettings r) => new(r.Provider, r.ApiUrl, r.Model, r.Enabled,
            r.TimeoutSeconds, r.Options, r.CredentialHint, r.Enabled && r.Credential is not null, DateTimeOffset.UtcNow);
    }

    private sealed class FakeClient : IDoubaoAnswerClient
    {
        internal int Calls { get; private set; }
        internal List<DoubaoAnswerRequest> Requests { get; } = [];
        internal DoubaoProviderException? Failure { get; set; }

        public Task<CloroAnswerSnapshot> AskAsync(DoubaoAnswerRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            Requests.Add(request);
            if (Failure is not null) throw Failure;
            var raw = JsonSerializer.SerializeToElement(new
            {
                id = "resp_1", model = "doubao-seed-2-1-pro-260915",
                usage = new { input_tokens = 100, output_tokens = 500, total_tokens = 600, tool_usage_details = new { web_search = new { doubao = 2 } } },
            });
            return Task.FromResult(new CloroAnswerSnapshot("doubao_api", "doubao", "api", "api_response", "doubao-seed-2-1-pro-260915",
                "回答正文", true, [new("https://example.com/a", "example.com", "标题", 1)], ["查询一"], raw, DateTimeOffset.UtcNow,
                "doubao-api-v1", "available", new(false, true, new("available", "complete", 1, 1, 0, 0, 0, []))));
        }
    }

    public class ResearchProxy : DispatchProxy
    {
        public bool Admin { get; set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            nameof(IResearchStore.IsSystemAdministratorAsync) => Task.FromResult(Admin),
            _ => throw new InvalidOperationException($"Unexpected research store call: {targetMethod?.Name}"),
        };
    }
}
