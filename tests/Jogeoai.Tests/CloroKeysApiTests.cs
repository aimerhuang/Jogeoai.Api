using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jogeoai.Api;
using Jogeoai.Api.Controllers;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Providers.Abstractions;
using Jogeoai.Providers.Cloro;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.OpenApi;

namespace Jogeoai.Tests;

public sealed class CloroKeysApiTests
{
    private const string NewKey = "cloro-key-new-9876";

    [Theory]
    [InlineData(false, 401, "AUTHENTICATION_REQUIRED")]
    [InlineData(true, 403, "ADMIN_REQUIRED")]
    public async Task EveryKeyPoolAndSettingsEndpointRequiresSystemAdministrator(bool authenticated, int status, string code)
    {
        var f = new Fixture(authenticated, admin: false);
        var id = Guid.NewGuid();
        var results = new List<IActionResult?>
        {
            (await f.Keys.List(false, default)).Result,
            (await f.Keys.Create(new("account", NewKey), default)).Result,
            (await f.Keys.Update(id, new UpdateCloroKeyRequest { Priority = 1 }, default)).Result,
            await f.Keys.Archive(id, default),
            (await f.Keys.RefreshCredits(id, default)).Result,
            (await f.Keys.RefreshAllCredits(default)).Result,
            (await f.Keys.Pool(default)).Result,
            (await f.Cloro.GetSettings(default)).Result,
            (await f.Cloro.SaveSettings(new(NewKey, "https://api.cloro.dev/v1/async/task"), default)).Result,
            (await f.Cloro.Test(new CloroTestRequest(CloroModel.ChatGpt, "prompt"), default)).Result,
        };
        foreach (var result in results) AssertError(result!, status, code);
        Assert.Empty(f.Store.Calls);
        Assert.Empty(f.Credits.Calls);
        Assert.Empty(f.Settings.Calls);
        Assert.Equal(0, f.Adapter.Calls);
    }

    [Fact]
    public async Task CreateValidatesWithCloroAppliesCreditsAndNeverReturnsTheKey()
    {
        var f = new Fixture();
        var created = Assert.IsType<ObjectResult>((await f.Keys.Create(new("  account-b ", $"  {NewKey} ", 5), default)).Result);
        Assert.Equal(201, created.StatusCode);
        Assert.Equal(new CreateCloroKeyCommand("account-b", NewKey, 5, true), f.Store.Created);
        Assert.Single(f.Credits.Calls);
        Assert.Equal(f.Store.CreatedId, Assert.Single(f.Pool.Applied).KeyId);
        var json = JsonSerializer.Serialize(created.Value);
        Assert.DoesNotContain(NewKey, json);
        Assert.Contains("9876", json);
    }

    [Fact]
    public async Task DuplicateKeyIsRejectedBeforeAnyCloroCall()
    {
        var f = new Fixture();
        f.Store.Exists = true;
        AssertError((await f.Keys.Create(new("account-b", NewKey), default)).Result!, 409, "CLORO_KEY_DUPLICATE");
        Assert.Empty(f.Credits.Calls);
        Assert.Null(f.Store.Created);
    }

    [Fact]
    public async Task StoreDuplicateRaceStillMapsToConflict()
    {
        var f = new Fixture();
        f.Store.CreateFailure = new CollectorStoreException("CLORO_KEY_DUPLICATE", "duplicate", 409);
        AssertError((await f.Keys.Create(new("account-b", NewKey, SkipValidation: true), default)).Result!, 409, "CLORO_KEY_DUPLICATE");
    }

    [Theory]
    [InlineData("CLORO_KEY_INVALID", false, 400, "CLORO_KEY_INVALID")]
    [InlineData("CLORO_CREDITS_FAILED", true, 502, "CLORO_KEY_VALIDATION_UNAVAILABLE")]
    [InlineData("CLORO_NOT_CONFIGURED", false, 503, "CLORO_NOT_CONFIGURED")]
    public async Task ValidationFailureDoesNotSaveAndDoesNotEchoTheKey(string providerCode, bool retryable, int status, string code)
    {
        var f = new Fixture { CreditsFailure = new CloroProviderException(providerCode, "failure", retryable) };
        var result = (await f.Keys.Create(new("account-b", NewKey), default)).Result!;
        AssertError(result, status, code);
        Assert.Null(f.Store.Created);
        Assert.DoesNotContain(NewKey, JsonSerializer.Serialize(((ObjectResult)result).Value));
    }

    [Fact]
    public async Task SkipValidationSavesWithUnknownBalanceWithoutCallingCloro()
    {
        var f = new Fixture { CreditsFailure = new CloroProviderException("CLORO_CREDITS_FAILED", "down", true) };
        var created = Assert.IsType<ObjectResult>((await f.Keys.Create(new("account-b", NewKey, SkipValidation: true), default)).Result);
        Assert.Equal(201, created.StatusCode);
        Assert.Empty(f.Credits.Calls);
        Assert.Empty(f.Pool.Applied);
    }

    [Theory]
    [InlineData("apiKey", "CLORO_KEY_IMMUTABLE")]
    [InlineData("somethingElse", "INVALID_CLORO_KEY")]
    public async Task PatchRejectsKeyReplacementAndUnknownFields(string field, string code)
    {
        var f = new Fixture();
        var body = JsonSerializer.Deserialize<UpdateCloroKeyRequest>($$"""{"priority":1,"{{field}}":"x"}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        AssertError((await f.Keys.Update(Guid.NewGuid(), body, default)).Result!, 400, code);
        Assert.DoesNotContain(nameof(ICloroKeyAdminStore.UpdateAsync), f.Store.Calls);
    }

    [Fact]
    public async Task ArchiveWithInFlightTasksReturnsConflictAndCount()
    {
        var f = new Fixture();
        f.Store.ArchiveResult = new CloroKeyArchiveResult(false, 3);
        var result = Assert.IsAssignableFrom<ObjectResult>(await f.Keys.Archive(Guid.NewGuid(), default));
        Assert.Equal(409, result.StatusCode);
        var error = JsonSerializer.SerializeToElement(result.Value).GetProperty("error");
        Assert.Equal("CLORO_KEY_IN_USE", error.GetProperty("code").GetString());
        Assert.Equal(3, error.GetProperty("inFlightTaskCount").GetInt32());

        f.Store.ArchiveResult = new CloroKeyArchiveResult(true, 0);
        Assert.IsType<NoContentResult>(await f.Keys.Archive(Guid.NewGuid(), default));
        f.Store.ArchiveResult = null;
        AssertError(await f.Keys.Archive(Guid.NewGuid(), default), 404, "RESOURCE_NOT_FOUND");
    }

    [Fact]
    public async Task PoolSummaryAndRefreshUseTheStoreAndRefresher()
    {
        var f = new Fixture();
        var summary = new CloroKeyPoolSummary(3, 1, 1, 0, 1, 40, DateTimeOffset.UtcNow.AddDays(2), null, CloroKeyPoolStatus.Low);
        f.Store.Summary = summary;
        Assert.Same(summary, Assert.IsType<OkObjectResult>((await f.Keys.Pool(default)).Result).Value);

        var key = f.Pool.Add("account-a", "cloro-key-aaaa-1111");
        f.Store.Views[key.KeyId] = View(key.KeyId, "account-a", "1111");
        var refreshed = Assert.IsType<OkObjectResult>((await f.Keys.RefreshCredits(key.KeyId, default)).Result);
        Assert.IsType<CloroKeyView>(refreshed.Value);
        Assert.Contains(key.KeyId, f.Credits.Calls);
        Assert.Contains(f.Pool.Applied, item => item.KeyId == key.KeyId);
        AssertError((await f.Keys.RefreshCredits(Guid.NewGuid(), default)).Result!, 404, "RESOURCE_NOT_FOUND");
    }

    [Fact]
    public async Task SettingsCompatibilityWritesDefaultKeyAndReportsPoolConfiguration()
    {
        var f = new Fixture();
        f.Pool.Add("default", "cloro-key-dflt-0000");
        var saved = Assert.IsType<OkObjectResult>((await f.Cloro.SaveSettings(
            new($" {NewKey} ", "https://api.cloro.dev/v1/async/task/"), default)).Result);
        Assert.Equal(NewKey, f.Store.DefaultKey);
        Assert.Equal(NewKey, f.Settings.Saved?.ApiKey);
        Assert.Equal("https://api.cloro.dev/v1/async/task", f.Settings.Saved?.ApiUrl);
        var response = Assert.IsType<CloroSettingsResponse>(saved.Value);
        Assert.True(response.Configured);
        Assert.Equal(1, response.EnabledKeys);
        Assert.DoesNotContain(NewKey, JsonSerializer.Serialize(response));

        f.Store.DefaultKey = null;
        var urlOnly = Assert.IsType<OkObjectResult>((await f.Cloro.SaveSettings(
            new(null, "https://api.cloro.dev/v1/async/task", false), default)).Result);
        Assert.Null(f.Store.DefaultKey);
        Assert.Equal("", f.Settings.Saved?.ApiKey);
        Assert.False(Assert.IsType<CloroSettingsResponse>(urlOnly.Value).Configured);

        f.Store.UpsertFailure = new CollectorStoreException("CLORO_KEY_IN_USE", "in flight", 409);
        AssertError((await f.Cloro.SaveSettings(new(NewKey, "https://api.cloro.dev/v1/async/task"), default)).Result!,
            409, "CLORO_KEY_IN_USE");
    }

    [Fact]
    public async Task KeyPoolRoutesAreDocumentedInChineseInSwagger()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(CloroKeysController).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production,
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddControllers().AddApplicationPart(typeof(CloroKeysController).Assembly)
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
            foreach (var (path, method) in new[]
            {
                ("/api/v1/providers/cloro/keys", "get"), ("/api/v1/providers/cloro/keys", "post"),
                ("/api/v1/providers/cloro/keys/{id}", "patch"), ("/api/v1/providers/cloro/keys/{id}", "delete"),
                ("/api/v1/providers/cloro/keys/{id}/refresh-credits", "post"),
                ("/api/v1/providers/cloro/keys/refresh-credits", "post"), ("/api/v1/providers/cloro/pool", "get"),
                ("/api/v1/providers/cloro/settings", "get"), ("/api/v1/providers/cloro/test", "post"),
            })
            {
                var operation = paths.GetProperty(path).GetProperty(method);
                Assert.True(HasChinese(operation.GetProperty("summary").GetString()), $"{method} {path}");
            }
            var schemas = swagger.RootElement.GetProperty("components").GetProperty("schemas");
            foreach (var (schema, property) in new[]
            {
                ("CloroKeyView", "keyHint"), ("CloroKeyView", "inFlightTaskCount"), ("CloroKeyPoolSummary", "nextAvailableAt"),
                ("CreateCloroKeyRequest", "skipValidation"), ("UpdateCloroKeyRequest", "enabled"), ("CloroTestResult", "keyHint"),
            })
            {
                var description = schemas.GetProperty(schema).GetProperty("properties").GetProperty(property);
                Assert.True(HasChinese(FindDescription(description)), $"{schema}.{property}");
            }
            foreach (var enumSchema in new[] { "CloroKeyStatus", "CloroKeyPoolStatus" })
                Assert.True(HasChinese(schemas.GetProperty(enumSchema).GetProperty("description").GetString()), enumSchema);
        }
        finally { await app.StopAsync(); }
    }

    private static string? FindDescription(JsonElement property) =>
        property.TryGetProperty("description", out var description) ? description.GetString()
        : property.TryGetProperty("allOf", out _) && property.TryGetProperty("description", out description) ? description.GetString()
        : null;

    private static bool HasChinese(string? value) => value?.Any(character => character is >= '一' and <= '鿿') == true;

    private static void AssertError(IActionResult result, int status, string code)
    {
        var response = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, JsonSerializer.SerializeToElement(response.Value).GetProperty("error").GetProperty("code").GetString());
    }

    private static CloroKeyView View(Guid id, string label, string hint) => new(id, label, hint, 100, true, CloroKeyStatus.Unknown,
        true, null, null, null, null, null, null, null, null, 0, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class Fixture
    {
        internal FakeAdminStore Store { get; } = new();
        internal InMemoryCloroKeyPool Pool { get; } = new();
        internal FakeCreditsClient Credits { get; } = new();
        internal RecordingSettings Settings { get; } = new();
        internal RecordingAdapter Adapter { get; } = new();
        internal CloroKeysController Keys { get; }
        internal CloroController Cloro { get; }
        internal CloroProviderException? CreditsFailure
        {
            set => Credits.Failure = value;
        }

        internal Fixture(bool authenticated = true, bool admin = true)
        {
            var context = new DefaultHttpContext();
            if (authenticated)
                context.Items[SessionAuthenticationMiddleware.ContextItemKey] = new AuthenticatedRequestContext(
                    new(Guid.NewGuid(), "admin@example.test", "Admin", "active", []), Guid.NewGuid(), "session");
            var research = DispatchProxy.Create<IResearchStore, ResearchProxy>();
            ((ResearchProxy)(object)research).Admin = admin;
            var refresher = new CloroKeyCreditsRefresher(Pool, Credits, NullLogger<CloroKeyCreditsRefresher>.Instance);
            Keys = new CloroKeysController(Store, Pool, Credits, refresher, research)
                { ControllerContext = new() { HttpContext = context } };
            Cloro = new CloroController(Settings, Adapter, Pool, Store, research)
                { ControllerContext = new() { HttpContext = context } };
        }
    }

    private sealed class FakeCreditsClient : ICloroCreditsClient
    {
        internal List<Guid> Calls { get; } = [];
        internal CloroProviderException? Failure { get; set; }
        public Task<CloroCreditsSnapshot> GetCreditsAsync(CloroKeyLease key, CancellationToken cancellationToken = default)
        {
            Calls.Add(key.KeyId);
            if (Failure is not null) throw Failure;
            return Task.FromResult(new CloroCreditsSnapshot(120, 500, DateTimeOffset.UtcNow.AddDays(9), DateTimeOffset.UtcNow));
        }
    }

    private sealed class FakeAdminStore : ICloroKeyAdminStore
    {
        internal List<string> Calls { get; } = [];
        internal bool Exists { get; set; }
        internal CreateCloroKeyCommand? Created { get; private set; }
        internal Guid CreatedId { get; } = Guid.NewGuid();
        internal Exception? CreateFailure { get; set; }
        internal Exception? UpsertFailure { get; set; }
        internal string? DefaultKey { get; set; }
        internal CloroKeyArchiveResult? ArchiveResult { get; set; } = new(true, 0);
        internal CloroKeyPoolSummary? Summary { get; set; }
        internal Dictionary<Guid, CloroKeyView> Views { get; } = [];

        public Task<IReadOnlyList<CloroKeyView>> ListAsync(bool includeArchived, CancellationToken cancellationToken = default)
        {
            Calls.Add(nameof(ListAsync));
            return Task.FromResult<IReadOnlyList<CloroKeyView>>(Views.Values.ToArray());
        }
        public Task<CloroKeyView?> GetAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Calls.Add(nameof(GetAsync));
            return Task.FromResult(Views.TryGetValue(id, out var view) ? view : null);
        }
        public Task<bool> ExistsAsync(string accountLabel, string apiKey, CancellationToken cancellationToken = default)
        {
            Calls.Add(nameof(ExistsAsync));
            return Task.FromResult(Exists);
        }
        public Task<CloroKeyView> CreateAsync(CreateCloroKeyCommand command, CancellationToken cancellationToken = default)
        {
            Calls.Add(nameof(CreateAsync));
            if (CreateFailure is not null) throw CreateFailure;
            Created = command;
            var view = View(CreatedId, command.AccountLabel, CloroKeyFormat.Hint(command.ApiKey));
            Views[CreatedId] = view;
            return Task.FromResult(view);
        }
        public Task<CloroKeyView?> UpdateAsync(UpdateCloroKeyCommand command, CancellationToken cancellationToken = default)
        {
            Calls.Add(nameof(UpdateAsync));
            return Task.FromResult<CloroKeyView?>(null);
        }
        public Task<CloroKeyArchiveResult?> ArchiveAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Calls.Add(nameof(ArchiveAsync));
            return Task.FromResult(ArchiveResult);
        }
        public Task<CloroKeyPoolSummary> GetSummaryAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add(nameof(GetSummaryAsync));
            return Task.FromResult(Summary!);
        }
        public Task UpsertDefaultKeyAsync(string apiKey, CancellationToken cancellationToken = default)
        {
            Calls.Add(nameof(UpsertDefaultKeyAsync));
            if (UpsertFailure is not null) throw UpsertFailure;
            DefaultKey = apiKey;
            return Task.CompletedTask;
        }

    }

    private sealed class RecordingSettings : ICloroSettingsStore
    {
        internal List<string> Calls { get; } = [];
        internal CloroConnectionSettings? Saved { get; private set; }
        public Task<CloroConnectionSettings?> GetAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add(nameof(GetAsync));
            return Task.FromResult(Saved);
        }
        public Task SaveAsync(CloroConnectionSettings settings, CancellationToken cancellationToken = default)
        {
            Calls.Add(nameof(SaveAsync));
            Saved = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingAdapter : ICloroAdapter
    {
        internal int Calls { get; private set; }
        public Task<CloroTestResult> RunAsync(CloroTestRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Provider must not be called in permission tests.");
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
