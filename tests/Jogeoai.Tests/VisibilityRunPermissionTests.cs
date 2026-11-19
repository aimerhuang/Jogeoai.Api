using System.Reflection;
using System.Text.Json;
using Jogeoai.Api;
using Jogeoai.Api.Controllers;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Providers.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Jogeoai.Tests;

public sealed class VisibilityRunPermissionTests
{
    [Theory]
    [InlineData("viewer", 403, "CONTENT_FORBIDDEN")]
    [InlineData(null, 401, "AUTHENTICATION_REQUIRED")]
    public async Task ManualRunRequiresWritePermissionBeforeAnyStoreOrProviderSettingsAccess(string? role, int status, string code)
    {
        foreach (var selection in new[] { "copilot", "managed", "preview", "domestic" })
        {
            var f = new Fixture(role);
            var request = selection switch
            {
                "managed" => Request(f.Site) with { Targets = null, TargetIds = [Guid.NewGuid()] },
                "preview" => Request(f.Site) with { Targets = null, PromptIds = null, PreviewId = Guid.NewGuid() },
                "domestic" => Request(f.Site) with { Targets = null, Platforms = [new("deepseek", "web")] },
                _ => Request(f.Site)
            };
            AssertError((await f.Controller.Create(request, default)).Result!, status, code);
            Assert.Empty(f.Calls);
        }
    }

    [Theory]
    [InlineData("owner", "chatgpt")]
    [InlineData("member", "chatgpt")]
    [InlineData("owner", "gemini")]
    [InlineData("member", "gemini")]
    [InlineData("owner", "copilot")]
    [InlineData("member", "copilot")]
    public async Task WriterCreatesOriginalScopedManualRunWithoutAutomaticCreditCharge(string role, string engine)
    {
        var f = new Fixture(role);
        var request = Request(f.Site, engine);
        var accepted = Assert.IsType<AcceptedAtActionResult>((await f.Controller.Create(request, default)).Result);
        Assert.Same(f.Created, accepted.Value);
        Assert.Equal(f.Tenant, f.Command!.TenantId);
        Assert.Equal(f.Site, f.Command.SiteId);
        Assert.Equal(request.PromptIds, f.Command.PromptIds);
        Assert.Equal(engine, Assert.Single(f.Command.Targets).Engine);
        Assert.False(f.Command.ChargeMonitoringCredits);
        Assert.Equal(1, f.Command.SampleCount);
    }

    [Fact]
    public async Task ReplayKeepsSameRunAfterProviderDisabledAndChangedRequestStillConflicts()
    {
        var f = new Fixture("owner");
        var request = Request(f.Site);
        await f.Controller.Create(request, default);
        f.Replay = new(f.Created, f.Site, f.Command!.HttpRequestHash, null, null);
        f.SettingsEnabled = false;
        f.Calls.Clear();
        var replay = Assert.IsType<AcceptedAtActionResult>((await f.Controller.Create(request, default)).Result);
        Assert.Same(f.Created, replay.Value);
        Assert.Equal(new[] { nameof(IVisibilityRunQueryStore.FindReplayAsync) }, f.Calls);
        f.Calls.Clear();
        AssertError((await f.Controller.Create(request with { PromptIds = [Guid.NewGuid()] }, default)).Result!,
            409, "RUN_IDEMPOTENCY_CONFLICT");
        Assert.DoesNotContain(nameof(ICloroVisibilityStore.CreateRunAsync), f.Calls);
        Assert.DoesNotContain(nameof(ICloroSettingsStore.GetAsync), f.Calls);
    }

    [Fact]
    public async Task ViewerCannotUseExistingIdempotencyKeyToEnterWritePath()
    {
        var f = new Fixture("viewer") { Replay = new(new(Guid.NewGuid(), "pending", 1, DateTimeOffset.UtcNow),
            Guid.NewGuid(), "hash", null, null) };
        AssertError((await f.Controller.Create(Request(f.Site), default)).Result!, 403, "CONTENT_FORBIDDEN");
        Assert.Empty(f.Calls);
    }

    [Fact]
    public async Task ViewerReadAccessRemainsScopedAndForeignTenantCannotWrite()
    {
        var f = new Fixture("viewer");
        Assert.IsType<OkObjectResult>((await f.Controller.Get(f.Created.RunId, default)).Result);
        Assert.Equal(new[] { nameof(ICloroVisibilityStore.GetRunProviderAsync), nameof(ICloroVisibilityStore.GetRunAsync) }, f.Calls);
        f.Calls.Clear();
        f.Controller.Request.Headers["X-Tenant-Id"] = Guid.NewGuid().ToString();
        AssertError((await f.Controller.Create(Request(f.Site), default)).Result!, 403, "TENANT_FORBIDDEN");
        Assert.Empty(f.Calls);
    }

    [Fact]
    public async Task DevelopmentTenantHeaderBridgeRetainsExistingWriteCompatibility()
    {
        var f = new Fixture(null, Environments.Development);
        f.Controller.Request.Headers["X-Tenant-Id"] = f.Tenant.ToString();
        Assert.IsType<AcceptedAtActionResult>((await f.Controller.Create(Request(f.Site), default)).Result);
        Assert.Equal(f.Tenant, f.Command!.TenantId);
    }

    [Fact]
    public async Task ExhaustedKeyPoolRejectsNewCloroRunWithEarliestRecoveryAndCreatesNothing()
    {
        var recovery = DateTimeOffset.UtcNow.AddHours(3);
        var f = new Fixture("owner") { Availability = new(2, 0, recovery) };
        var result = Assert.IsAssignableFrom<ObjectResult>((await f.Controller.Create(Request(f.Site), default)).Result);
        Assert.Equal(503, result.StatusCode);
        var error = JsonSerializer.SerializeToElement(result.Value).GetProperty("error");
        Assert.Equal("CLORO_KEY_POOL_EXHAUSTED", error.GetProperty("code").GetString());
        Assert.Equal(recovery, error.GetProperty("nextAvailableAt").GetDateTimeOffset());
        Assert.DoesNotContain(nameof(ICloroVisibilityStore.CreateRunAsync), f.Calls);
    }

    [Fact]
    public async Task PoolOnlyCoolingAfter429StillAcceptsTheRun()
    {
        var f = new Fixture("owner") { Availability = new(2, 0, DateTimeOffset.UtcNow.AddSeconds(30), CapacityCoolingKeys: 1) };
        Assert.IsType<AcceptedAtActionResult>((await f.Controller.Create(Request(f.Site), default)).Result);
        Assert.Contains(nameof(ICloroVisibilityStore.CreateRunAsync), f.Calls);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public async Task PoolWithoutEnabledKeysOrDisabledProviderIsStillNotConfigured(bool enabled, int enabledKeys)
    {
        var f = new Fixture("owner") { SettingsEnabled = enabled, Availability = new(enabledKeys, enabledKeys, null) };
        AssertError((await f.Controller.Create(Request(f.Site), default)).Result!, 503, "CLORO_NOT_CONFIGURED");
        Assert.DoesNotContain(nameof(ICloroVisibilityStore.CreateRunAsync), f.Calls);
    }

    [Fact]
    public async Task UnconfiguredDoubaoRejectsManualRunWithoutTouchingCloroOrCreatingAnything()
    {
        var f = new Fixture("owner");
        AssertError((await f.Controller.Create(DoubaoRequest(f.Site), default)).Result!, 503, "DOUBAO_NOT_CONFIGURED");
        Assert.Contains(nameof(IAnswerProviderSettingsStore.GetConfigurationAsync), f.Calls);
        Assert.DoesNotContain(nameof(ICloroSettingsStore.GetAsync), f.Calls);
        Assert.DoesNotContain(nameof(ICloroKeyPool.GetAvailabilityAsync), f.Calls);
        Assert.DoesNotContain(nameof(ICloroVisibilityStore.CreateRunAsync), f.Calls);
    }

    [Fact]
    public async Task ConfiguredDoubaoCreatesTheRunThroughTheSharedStoreEvenWhenCloroIsNotConfigured()
    {
        var f = new Fixture("owner") { DoubaoConfigured = true, SettingsEnabled = false, Availability = new(0, 0, null) };
        var accepted = Assert.IsType<AcceptedAtActionResult>((await f.Controller.Create(DoubaoRequest(f.Site), default)).Result);
        Assert.Same(f.Created, accepted.Value);
        Assert.Equal("doubao_api", Assert.Single(f.Command!.Targets).Provider);
        Assert.DoesNotContain(nameof(ICloroSettingsStore.GetAsync), f.Calls);
    }

    [Fact]
    public async Task MixedCloroAndDoubaoRunChecksBothProviders()
    {
        var f = new Fixture("owner") { DoubaoConfigured = true, Availability = new(2, 0, DateTimeOffset.UtcNow.AddHours(1)) };
        var request = DoubaoRequest(f.Site) with { Targets = [new("cloro", "chatgpt", "zh-CN", "US"), new("doubao_api", "doubao", "zh-CN", "CN")] };
        AssertError((await f.Controller.Create(request, default)).Result!, 503, "CLORO_KEY_POOL_EXHAUSTED");
        Assert.DoesNotContain(nameof(ICloroVisibilityStore.CreateRunAsync), f.Calls);
    }

    [Fact]
    public async Task UnconfiguredDeepSeekRejectsManualRunWithItsOwnCode()
    {
        var f = new Fixture("owner");
        var result = (await f.Controller.Create(DeepSeekRequest(f.Site), default)).Result!;
        AssertError(result, 503, "DEEPSEEK_NOT_CONFIGURED");
        Assert.Equal("DeepSeek服务尚未配置或未启用。",
            JsonSerializer.SerializeToElement(((ObjectResult)result).Value).GetProperty("error").GetProperty("message").GetString());
        Assert.Equal(["deepseek_api"], f.SettingsLookups);
        Assert.DoesNotContain(nameof(ICloroSettingsStore.GetAsync), f.Calls);
        Assert.DoesNotContain(nameof(ICloroVisibilityStore.CreateRunAsync), f.Calls);
    }

    [Fact]
    public async Task UnconfiguredDoubaoKeepsItsExactMessage()
    {
        var f = new Fixture("owner");
        var result = (await f.Controller.Create(DoubaoRequest(f.Site), default)).Result!;
        Assert.Equal("豆包服务尚未配置或未启用。",
            JsonSerializer.SerializeToElement(((ObjectResult)result).Value).GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task ConfiguredDeepSeekCreatesTheRunThroughTheSharedStore()
    {
        var f = new Fixture("owner") { DeepSeekConfigured = true, SettingsEnabled = false, Availability = new(0, 0, null) };
        var accepted = Assert.IsType<AcceptedAtActionResult>((await f.Controller.Create(DeepSeekRequest(f.Site), default)).Result);
        Assert.Same(f.Created, accepted.Value);
        var target = Assert.Single(f.Command!.Targets);
        Assert.Equal(("deepseek_api", "deepseek"), (target.Provider, target.Engine));
        Assert.DoesNotContain(nameof(ICloroSettingsStore.GetAsync), f.Calls);
    }

    [Theory]
    [InlineData(false, false, "DOUBAO_NOT_CONFIGURED")]
    [InlineData(true, false, "DEEPSEEK_NOT_CONFIGURED")]
    [InlineData(false, true, "DOUBAO_NOT_CONFIGURED")]
    public async Task MixedDoubaoAndDeepSeekRunsCheckEveryProviderInRegistryOrder(bool doubao, bool deepSeek, string code)
    {
        var f = new Fixture("owner") { DoubaoConfigured = doubao, DeepSeekConfigured = deepSeek };
        var request = DoubaoRequest(f.Site) with { Targets = [new("deepseek_api", "deepseek", "zh-CN", "CN"), new("doubao_api", "doubao", "zh-CN", "CN")] };
        AssertError((await f.Controller.Create(request, default)).Result!, 503, code);
        Assert.DoesNotContain(nameof(ICloroVisibilityStore.CreateRunAsync), f.Calls);
    }

    private static CreateVisibilityRunRequest DeepSeekRequest(Guid site)
        => new(site, [Guid.NewGuid()], null, Targets: [new("deepseek_api", "deepseek", "zh-CN", "CN")]);

    private static CreateVisibilityRunRequest Request(Guid site, string engine = "copilot")
        => new(site, [Guid.NewGuid()], null, Targets: [new("cloro", engine, "zh-CN", "US")]);
    private static CreateVisibilityRunRequest DoubaoRequest(Guid site)
        => new(site, [Guid.NewGuid()], null, Targets: [new("doubao_api", "doubao", "zh-CN", "CN")]);

    private static void AssertError(IActionResult result, int status, string code)
    {
        var response = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, JsonSerializer.SerializeToElement(response.Value).GetProperty("error").GetProperty("code").GetString());
    }

    private sealed class Fixture
    {
        internal Guid Tenant { get; } = Guid.NewGuid();
        internal Guid Site { get; } = Guid.NewGuid();
        internal VisibilityRunCreated Created { get; } = new(Guid.NewGuid(), "pending", 1, DateTimeOffset.UtcNow);
        internal VisibilityRunsController Controller { get; }
        internal List<string> Calls { get; } = [];
        internal CreateCloroVisibilityRunCommand? Command { get; private set; }
        internal VisibilityRunReplay? Replay { get; set; }
        internal bool SettingsEnabled { get; set; } = true;
        internal bool DoubaoConfigured { get; set; }
        internal bool DeepSeekConfigured { get; set; }
        internal List<string> SettingsLookups { get; } = [];
        internal CloroKeyPoolAvailability Availability { get; set; } = new(1, 1, null);

        internal Fixture(string? role, string environment = "Production")
        {
            var context = new DefaultHttpContext();
            context.Request.Headers["Idempotency-Key"] = "manual-permission-fixture";
            if (role is not null)
                context.Items[SessionAuthenticationMiddleware.ContextItemKey] = new AuthenticatedRequestContext(
                    new(Guid.NewGuid(), "fixture@example.test", "Fixture", "active", [new(Tenant, "Team", role, "active")]),
                    Guid.NewGuid(), "fixture");
            Controller = new(Proxy<IVisibilityCollectorStore>(), Proxy<ICloroVisibilityStore>(),
                Proxy<ICloroSettingsStore>(), Proxy<ICloroKeyPool>(), Proxy<IAnswerProviderSettingsStore>(), Proxy<IManagementStore>(),
                Proxy<IVisibilityRunQueryStore>(), new TenantContextResolver(new TestEnvironment { EnvironmentName = environment }),
                Proxy<IAppCollectionStore>())
            { ControllerContext = new() { HttpContext = context } };
        }

        private T Proxy<T>() where T : class
        {
            var proxy = DispatchProxy.Create<T, StoreProxy>();
            ((StoreProxy)(object)proxy).Handler = (method, args) =>
            {
                Calls.Add(method.Name);
                if (method.DeclaringType != typeof(ICloroSettingsStore) && method.DeclaringType != typeof(ICloroKeyPool)
                    && method.DeclaringType != typeof(IAnswerProviderSettingsStore)
                    && method.Name != nameof(ICloroVisibilityStore.CreateRunAsync))
                    Assert.Equal(Tenant, args[0]);
                switch (method.Name)
                {
                    case nameof(IVisibilityRunQueryStore.FindReplayAsync): return Task.FromResult(Replay);
                    case nameof(IAnswerProviderSettingsStore.GetConfigurationAsync):
                        var provider = Assert.IsType<string>(args[0]);
                        Assert.Contains(provider, new[] { "doubao_api", "deepseek_api" });
                        SettingsLookups.Add(provider);
                        var configured = provider == "doubao_api" ? DoubaoConfigured : DeepSeekConfigured;
                        return Task.FromResult(configured
                            ? new AnswerProviderConfiguration(provider, "https://api.test/v1", "model", "secret-fixture", 180, new())
                            : null);
                    case nameof(ICloroSettingsStore.GetAsync): return Task.FromResult<CloroConnectionSettings?>(new("", "https://cloro.test/v1/async/task", SettingsEnabled));
                    case nameof(ICloroKeyPool.GetAvailabilityAsync): return Task.FromResult(Availability);
                    case nameof(ICloroVisibilityStore.CreateRunAsync):
                        Command = Assert.IsType<CreateCloroVisibilityRunCommand>(args[0]);
                        return Task.FromResult(Created);
                    case nameof(ICloroVisibilityStore.GetRunProviderAsync): return Task.FromResult<string?>("cloro");
                    case nameof(ICloroVisibilityStore.GetRunAsync): return Task.FromResult<VisibilityRunSummary?>(new(Created.RunId, Site, "pending", 1, 0, 0, Created.CreatedAt, null, null, null, null));
                    default: throw new InvalidOperationException($"Unexpected store call: {method.Name}");
                }
            };
            return proxy;
        }
    }

    public class StoreProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args ?? []);
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
