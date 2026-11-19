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

public sealed class DeepSeekProviderControllerTests
{
    private const string Credential = "sk-deepseek-fixture-8765";
    private const string ApiUrl = "https://api.deepseek.com/anthropic/v1/messages";

    private static SaveAnswerProviderSettingsRequest ValidSave(string? credential = Credential, AnswerProviderOptions? options = null) =>
        new(ApiUrl, "deepseek-flash", credential, true, 240, options);

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
    public async Task GetWithoutARowReturnsTheDeepSeekDefaults()
    {
        var f = new Fixture();
        var view = Assert.IsType<AnswerProviderSettingsView>(Assert.IsType<OkObjectResult>((await f.Controller.GetSettings(default)).Result).Value);
        Assert.Equal(("deepseek_api", false, false, DeepSeekDefaults.ApiUrl, "deepseek-flash", 240),
            (view.Provider, view.Enabled, view.Configured, view.ApiUrl, view.Model, view.TimeoutSeconds));
        Assert.Equal(("web_search", 5, "disabled"), (view.Options.RequestMode, view.Options.MaxToolCalls, view.Options.Thinking));
        Assert.Empty(view.Options.Sources);
        Assert.Null(view.Options.ReasoningEffort);
        Assert.Null(view.CredentialHint);
        Assert.Equal(["settings:deepseek_api"], f.Settings.Providers);
    }

    [Fact]
    public async Task PutSavesNormalizedDeepSeekOptionsAndKeepsTheCredentialWhenOmitted()
    {
        var f = new Fixture();
        var saved = Assert.IsType<AnswerProviderSettingsView>(Assert.IsType<OkObjectResult>((await f.Controller.SaveSettings(
            ValidSave(options: new() { Sources = [], MaxToolCalls = 7, Thinking = " Enabled " }), default)).Result).Value);
        Assert.True(saved.Configured);
        Assert.Equal("****8765", saved.CredentialHint);
        Assert.Equal(("web_search", 7, "enabled"), (saved.Options.RequestMode, saved.Options.MaxToolCalls, saved.Options.Thinking));
        Assert.DoesNotContain(Credential, JsonSerializer.Serialize(saved));
        Assert.Equal(Credential, f.Settings.StoredCredential);
        Assert.Equal("""{"requestMode":"web_search","sources":[],"maxToolCalls":7,"reasoningEffort":null,"thinking":"enabled"}""",
            f.Settings.StoredOptionsJson);

        var again = Assert.IsType<AnswerProviderSettingsView>(Assert.IsType<OkObjectResult>(
            (await f.Controller.SaveSettings(ValidSave(credential: null) with { Enabled = false }, default)).Result).Value);
        Assert.Equal(Credential, f.Settings.StoredCredential);
        Assert.False(again.Configured);
        // Omitted options: sources become [], thinking defaults to disabled.
        Assert.Empty(again.Options.Sources);
        Assert.Equal("disabled", again.Options.Thinking);
        Assert.All(f.Settings.Providers, provider => Assert.EndsWith(":deepseek_api", provider));
    }

    [Fact]
    public void OptionsWithoutSourcesDeserializeToEmptySourcesForDeepSeekOnly()
    {
        var parsed = JsonSerializer.Deserialize<AnswerProviderOptions>("""{"maxToolCalls":4}""", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var deepSeek = AnswerProviderSettingsRules.NormalizeOptions(AnswerProviderDefinition.DeepSeekApi, parsed);
        Assert.Equal((0, 4, "disabled"), (deepSeek.Sources.Count, deepSeek.MaxToolCalls, deepSeek.Thinking));
        var doubao = AnswerProviderSettingsRules.NormalizeOptions(AnswerProviderDefinition.DoubaoApi, parsed);
        Assert.Equal(["doubao"], doubao.Sources);
        Assert.Null(doubao.Thinking);
    }

    [Fact]
    public void MigrationSeedOptionsRoundTripUnchanged()
    {
        const string seed = """{"requestMode":"web_search","sources":[],"maxToolCalls":5,"thinking":"disabled"}""";
        var parsed = AnswerProviderOptions.Parse(seed);
        Assert.Equal(("web_search", 0, 5, "disabled"), (parsed.RequestMode, parsed.Sources.Count, parsed.MaxToolCalls, parsed.Thinking));
        Assert.Equal(DeepSeekDefaults.Options().ToJson(), AnswerProviderSettingsRules.NormalizeOptions(AnswerProviderDefinition.DeepSeekApi, parsed).ToJson());
    }

    [Fact]
    public void DoubaoOptionsJsonIsByteIdenticalAndRejectsThinking()
    {
        var doubao = AnswerProviderSettingsRules.Normalize("doubao_api",
            new SaveAnswerProviderSettingsRequest("https://ark.test/api/v3/responses", "doubao-model"));
        Assert.Equal("""{"requestMode":"web_search","sources":["doubao"],"maxToolCalls":3,"reasoningEffort":null}""", doubao.OptionsJson);
        Assert.Equal("""{"requestMode":"web_search","sources":["doubao"],"maxToolCalls":3,"reasoningEffort":null}""", new AnswerProviderOptions().ToJson());
        var exception = Assert.Throws<CollectorStoreException>(() => AnswerProviderSettingsRules.Normalize("doubao_api",
            new SaveAnswerProviderSettingsRequest("https://ark.test/api/v3/responses", "doubao-model", Options: new() { Thinking = "enabled" })));
        Assert.Equal(("INVALID_PROVIDER_SETTINGS", 400), (exception.Code, exception.StatusCode));
    }

    [Theory]
    [InlineData("doubao_app", null, null, null, 5)]
    [InlineData("web_search", "doubao", null, null, 5)]
    [InlineData("web_search", null, "low", null, 5)]
    [InlineData("web_search", null, null, "auto", 5)]
    [InlineData("web_search", null, null, null, 0)]
    [InlineData("web_search", null, null, null, 11)]
    public async Task PutRejectsOptionsDeepSeekDoesNotSupport(string requestMode, string? source, string? effort, string? thinking, int maxToolCalls)
    {
        var f = new Fixture();
        var options = new AnswerProviderOptions
        {
            RequestMode = requestMode, Sources = source is null ? [] : [source], ReasoningEffort = effort, Thinking = thinking, MaxToolCalls = maxToolCalls,
        };
        AssertError((await f.Controller.SaveSettings(ValidSave(options: options), default)).Result!, 400, "INVALID_PROVIDER_SETTINGS");
        Assert.Null(f.Settings.StoredCredential);
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
        var ok = Assert.IsType<OkObjectResult>((await f.Controller.Test(new("管道摄像头厂家有哪些？", "zh-CN"), default)).Result);
        var result = Assert.IsType<DeepSeekTestResult>(ok.Value);
        Assert.Equal(("deepseek_api", "deepseek", "deepseek-flash", true), (result.Provider, result.Engine, result.ModelVersion, result.ModelVersionProvided));
        Assert.Equal("## 回答\n\n正文", result.TextContent);
        Assert.Equal(["查询一"], result.WebQueries);
        Assert.Equal("available", result.QueryAvailability);
        var citation = Assert.Single(result.Citations);
        Assert.Equal(("https://example.com/a", "example.com", "标题"), (citation.Url, citation.Domain, citation.Title));
        Assert.Equal((21691L, 384L, 1428L, 3L), (result.Usage!.InputTokens, result.Usage.CacheReadInputTokens, result.Usage.OutputTokens,
            result.Usage.WebSearchRequests));
        Assert.NotNull(result.ResultQuality);
        Assert.DoesNotContain(Credential, JsonSerializer.Serialize(result));
        var request = Assert.Single(f.Client.Requests);
        Assert.Equal(("管道摄像头厂家有哪些？", "zh-CN", "CN", null), (request.Prompt, request.Locale, request.Country, request.ProviderTaskId));
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
    [InlineData(DeepSeekErrorCodes.NotConfigured, 503)]
    [InlineData(DeepSeekErrorCodes.InsufficientBalance, 503)]
    [InlineData(DeepSeekErrorCodes.RateLimited, 503)]
    [InlineData(DeepSeekErrorCodes.Timeout, 504)]
    [InlineData(DeepSeekErrorCodes.InvalidRequest, 400)]
    [InlineData(DeepSeekErrorCodes.AuthFailed, 502)]
    [InlineData(DeepSeekErrorCodes.RequestFailed, 502)]
    [InlineData(DeepSeekErrorCodes.InvalidResponse, 502)]
    public async Task TestMapsProviderErrorsToTheStandardEnvelope(string code, int status)
    {
        var f = new Fixture();
        var deferred = code == DeepSeekErrorCodes.InsufficientBalance;
        f.Client.Failure = new DeepSeekProviderException(code, "安全消息", code is DeepSeekErrorCodes.Timeout || deferred, deferred ? 10 : null);
        var result = (await f.Controller.Test(new("问题"), default)).Result!;
        AssertError(result, status, code);
        var error = JsonSerializer.SerializeToElement(((ObjectResult)result).Value).GetProperty("error");
        Assert.Equal("安全消息", error.GetProperty("message").GetString());
        Assert.Equal(code is DeepSeekErrorCodes.Timeout || deferred, error.GetProperty("retryable").GetBoolean());
        if (deferred) Assert.Equal(10, error.GetProperty("deferMinutes").GetInt32());
    }

    [Fact]
    public async Task DeepSeekRoutesAreDocumentedInChineseInSwagger()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(DeepSeekProviderController).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production,
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddControllers().AddApplicationPart(typeof(DeepSeekProviderController).Assembly);
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
                ("/api/v1/providers/deepseek/settings", "get"), ("/api/v1/providers/deepseek/settings", "put"),
                ("/api/v1/providers/deepseek/test", "post"),
            })
                Assert.True(HasChinese(paths.GetProperty(path).GetProperty(method).GetProperty("summary").GetString()), $"{method} {path}");
            var schemas = swagger.RootElement.GetProperty("components").GetProperty("schemas");
            foreach (var (schema, property) in new[]
            {
                ("AnswerProviderOptions", "thinking"), ("AnswerProviderOptions", "sources"), ("DeepSeekTestRequest", "prompt"),
                ("DeepSeekTestResult", "citations"), ("DeepSeekTestResult", "textContent"), ("DeepSeekTestResult", "usage"),
                ("DeepSeekUsageSummary", "cacheReadInputTokens"), ("DeepSeekUsageSummary", "webSearchRequests"),
            })
            {
                var description = schemas.GetProperty(schema).GetProperty("properties").GetProperty(property);
                Assert.True(HasChinese(description.TryGetProperty("description", out var text) ? text.GetString() : null), $"{schema}.{property}");
            }
            Assert.False(schemas.GetProperty("AnswerProviderSettingsView").GetProperty("properties").TryGetProperty("credential", out _));
        }
        finally { await app.StopAsync(); }
    }

    private static bool HasChinese(string? value) => value?.Any(character => character is >= '一' and <= '鿿') == true;

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
        internal DeepSeekProviderController Controller { get; }

        internal Fixture(bool authenticated = true, bool admin = true)
        {
            var context = new DefaultHttpContext();
            if (authenticated)
                context.Items[SessionAuthenticationMiddleware.ContextItemKey] = new AuthenticatedRequestContext(
                    new(Guid.NewGuid(), "admin@example.test", "Admin", "active", []), Guid.NewGuid(), "session");
            var research = DispatchProxy.Create<IResearchStore, DoubaoProviderControllerTests.ResearchProxy>();
            ((DoubaoProviderControllerTests.ResearchProxy)(object)research).Admin = admin;
            Controller = new DeepSeekProviderController(Settings, Client, research) { ControllerContext = new() { HttpContext = context } };
        }
    }

    // In-memory store that applies the real validation rules so controller tests exercise them end to end.
    private sealed class FakeSettingsStore : IAnswerProviderSettingsStore
    {
        private NormalizedAnswerProviderSettings? row;
        internal List<string> Calls { get; } = [];
        internal List<string> Providers { get; } = [];
        internal CollectorStoreException? Failure { get; set; }
        internal string? StoredCredential => row?.Credential;
        internal string? StoredOptionsJson => row?.OptionsJson;

        public Task<AnswerProviderSettingsView?> GetAsync(string provider, CancellationToken cancellationToken = default)
        {
            Calls.Add(nameof(GetAsync));
            Providers.Add($"settings:{provider}");
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
            Providers.Add($"save:{provider}");
            if (Failure is not null) throw Failure;
            var normalized = AnswerProviderSettingsRules.Normalize(provider, request);
            // COALESCE(VALUES(credential), credential)
            row = normalized with { Credential = normalized.Credential ?? row?.Credential, CredentialHint = normalized.CredentialHint ?? row?.CredentialHint };
            return Task.FromResult(View(row));
        }

        private static AnswerProviderSettingsView View(NormalizedAnswerProviderSettings r) => new(r.Provider, r.ApiUrl, r.Model, r.Enabled,
            r.TimeoutSeconds, r.Options, r.CredentialHint, r.Enabled && r.Credential is not null, DateTimeOffset.UtcNow);
    }

    private sealed class FakeClient : IDeepSeekAnswerClient
    {
        internal int Calls { get; private set; }
        internal List<DeepSeekAnswerRequest> Requests { get; } = [];
        internal DeepSeekProviderException? Failure { get; set; }

        public Task<CloroAnswerSnapshot> AskAsync(DeepSeekAnswerRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            Requests.Add(request);
            if (Failure is not null) throw Failure;
            var raw = JsonSerializer.SerializeToElement(new
            {
                id = "msg_1", model = "deepseek-flash",
                usage = new { input_tokens = 21691, cache_read_input_tokens = 384, output_tokens = 1428, service_tier = "standard",
                    server_tool_use = new { web_search_requests = 3 } },
            });
            return Task.FromResult(new CloroAnswerSnapshot("deepseek_api", "deepseek", "api", "api_response", "deepseek-flash",
                "## 回答\n\n正文", true, [new("https://example.com/a", "example.com", "标题", 1)], ["查询一"], raw, DateTimeOffset.UtcNow,
                "deepseek-api-v1", "available", new(false, true, new("available", "complete", 1, 1, 0, 0, 0, []))));
        }
    }
}
