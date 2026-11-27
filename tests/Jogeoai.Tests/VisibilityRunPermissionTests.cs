using System.Reflection;
using System.Text.Json;
using Jogeoai.Api;
using Jogeoai.Api.Controllers;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
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
    public async Task WriterCreatesOriginalScopedManualRunThatReservesCredits(string role, string engine)
    {
        var f = new Fixture(role);
        var request = Request(f.Site, engine);
        var accepted = Assert.IsType<AcceptedAtActionResult>((await f.Controller.Create(request, default)).Result);
        Assert.Same(f.Created, accepted.Value);
        Assert.Equal(f.Tenant, f.Command!.TenantId);
        Assert.Equal(f.Site, f.Command.SiteId);
        Assert.Equal(request.PromptIds, f.Command.PromptIds);
        Assert.Equal(engine, Assert.Single(f.Command.Targets).Engine);
        // RD1: a manual run reserves credits like daily monitoring; it is a full prompt × target product.
        Assert.True(f.Command.ChargeMonitoringCredits);
        Assert.Null(f.Command.PromptTargetKeys);
        Assert.Equal(1, f.Command.SampleCount);
    }

    // RD1 + RD4: both API-provider shapes (targets; targetIds / previewId) charge, and the stored initiator is the session
    // user whatever the client sends. The spoofed values are the ones the same-day monitoring guards look for.
    [Theory]
    [InlineData("targets", "prompt-monitor:{site}")]
    [InlineData("managed", "prompt-monitor:{site}")]
    [InlineData("preview", "prompt-monitor:{site}")]
    [InlineData("targets", "schedule:{site}")]
    [InlineData("managed", "prompt-instant:{site}")]
    [InlineData("targets", null)]
    public async Task ApiProviderShapesReserveCreditsAndStoreTheServerSideInitiator(string shape, string? clientRequestedBy)
    {
        var f = new Fixture("member");
        var spoofed = clientRequestedBy?.Replace("{site}", f.Site.ToString("N"), StringComparison.Ordinal);
        var request = shape switch
        {
            "managed" => Request(f.Site) with { Targets = null, TargetIds = [Guid.NewGuid()], RequestedBy = spoofed },
            "preview" => Request(f.Site) with { Targets = null, PromptIds = null, PreviewId = Guid.NewGuid(), RequestedBy = spoofed },
            _ => Request(f.Site) with { RequestedBy = spoofed },
        };
        Assert.IsType<AcceptedAtActionResult>((await f.Controller.Create(request, default)).Result);
        Assert.True(f.Command!.ChargeMonitoringCredits);
        Assert.Null(f.Command.PromptTargetKeys);
        Assert.Equal($"user:{f.User:N}", f.Command.RequestedBy);
        Assert.Equal(shape != "targets", f.Command.RequireEnabledTargets);
        Assert.Equal(shape == "preview" ? request.PreviewId : null, f.Command.PreviewId);
        // The API-provider shapes are open to every writer: no administrator lookup, and the web-collector store is not used.
        Assert.DoesNotContain(nameof(IResearchStore.IsSystemAdministratorAsync), f.Calls);
        Assert.Equal(new[] { $"{nameof(ICloroVisibilityStore)}.{nameof(ICloroVisibilityStore.CreateRunAsync)}" }, f.RunStoreCalls);
    }

    [Fact]
    public async Task ClientRequestedByStaysInTheHttpRequestHashSoUsedIdempotencyKeysKeepReplaying()
    {
        var site = Guid.NewGuid();
        var prompt = Guid.NewGuid();
        async Task<CreateCloroVisibilityRunCommand> SendAsync(string? requestedBy)
        {
            var f = new Fixture("owner");
            await f.Controller.Create(new CreateVisibilityRunRequest(site, [prompt], null, requestedBy,
                Targets: [new("cloro", "chatgpt", "zh-CN", "US")]), default);
            return f.Command!;
        }
        var first = await SendAsync("client-a");
        var same = await SendAsync("client-a");
        var other = await SendAsync("client-b");
        Assert.Equal(first.HttpRequestHash, same.HttpRequestHash);
        Assert.NotEqual(first.HttpRequestHash, other.HttpRequestHash);
        // Hashed, never stored: each fixture has its own session user.
        Assert.All(new[] { first, same, other }, command => Assert.StartsWith("user:", command.RequestedBy, StringComparison.Ordinal));
    }

    [Fact]
    public async Task InsufficientCreditsReturnTheStores402AndCode()
    {
        var f = new Fixture("owner") { CreateFailure = new CollectorStoreException("CREDIT_INSUFFICIENT", "本轮预计需要 10 积分，当前可用 0 积分。", 402) };
        AssertError((await f.Controller.Create(Request(f.Site), default)).Result!, 402, "CREDIT_INSUFFICIENT");
        Assert.True(f.Command!.ChargeMonitoringCredits);
    }

    // RD5: outside Development the web-collector shape needs a system administrator; refused before the replay lookup and
    // before either run store is touched, also when the idempotency key already belongs to a run.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductionPlatformsRunByANonAdministratorIsRefusedBeforeAnyRunStoreCall(bool existingRun)
    {
        var f = new Fixture("owner");
        if (existingRun) f.Replay = new(f.Created, f.Site, "hash", null, null);
        AssertError((await f.Controller.Create(PlatformsRequest(f.Site), default)).Result!, 403, "ADMIN_REQUIRED");
        Assert.Equal(new[] { nameof(IResearchStore.IsSystemAdministratorAsync) }, f.Calls);
        Assert.Empty(f.RunStoreCalls);
        Assert.Null(f.DomesticCommand);
    }

    [Fact]
    public async Task ProductionPlatformsRunByASystemAdministratorReachesTheCollectorStoreUncharged()
    {
        var f = new Fixture("member") { SystemAdministrator = true };
        var request = PlatformsRequest(f.Site) with { RequestedBy = $"prompt-monitor:{f.Site:N}" };
        var accepted = Assert.IsType<AcceptedAtActionResult>((await f.Controller.Create(request, default)).Result);
        Assert.Same(f.Created, accepted.Value);
        Assert.Equal(new[] { $"{nameof(IVisibilityCollectorStore)}.{nameof(IVisibilityCollectorStore.CreateRunAsync)}" }, f.RunStoreCalls);
        Assert.Equal((f.Tenant, f.Site, $"user:{f.User:N}"), (f.DomesticCommand!.TenantId, f.DomesticCommand.SiteId, f.DomesticCommand.RequestedBy));
        Assert.Equal("doubao.web", Assert.Single(f.DomesticCommand.Platforms).EntryCode);
        Assert.Equal(64, f.DomesticCommand.HttpRequestHash!.Length);
        Assert.Null(f.Command);
    }

    [Fact]
    public async Task ProductionAdministratorLookupFailureIsReturnedWithoutCallingARunStore()
    {
        var f = new Fixture("owner") { AdministratorFailure = new CollectorStoreException("SCHEMA_NOT_MIGRATED", "需要先执行迁移。", 503) };
        AssertError((await f.Controller.Create(PlatformsRequest(f.Site), default)).Result!, 503, "SCHEMA_NOT_MIGRATED");
        Assert.Empty(f.RunStoreCalls);
    }

    // Development is unchanged: the X-Tenant-Id bridge (no session) and a signed-in member both reach the collector store
    // without an administrator lookup. Only the stored initiator is now decided by the server.
    [Theory]
    [InlineData(null, VisibilityRunsController.DevBridgeRequestedBy)]
    [InlineData("member", null)]
    public async Task DevelopmentPlatformsRunNeedsNoAdministrator(string? role, string? expectedRequestedBy)
    {
        var f = new Fixture(role, Environments.Development);
        f.Controller.Request.Headers["X-Tenant-Id"] = f.Tenant.ToString();
        var request = PlatformsRequest(f.Site) with { RequestedBy = "operator-supplied" };
        Assert.IsType<AcceptedAtActionResult>((await f.Controller.Create(request, default)).Result);
        Assert.DoesNotContain(nameof(IResearchStore.IsSystemAdministratorAsync), f.Calls);
        Assert.Equal(new[] { $"{nameof(IVisibilityCollectorStore)}.{nameof(IVisibilityCollectorStore.CreateRunAsync)}" }, f.RunStoreCalls);
        Assert.Equal(expectedRequestedBy ?? $"user:{f.User:N}", f.DomesticCommand!.RequestedBy);
    }

    [Fact]
    public async Task DevelopmentBridgeApiProviderRunIsChargedAndWritesTheBridgeInitiator()
    {
        var f = new Fixture(null, Environments.Development);
        f.Controller.Request.Headers["X-Tenant-Id"] = f.Tenant.ToString();
        Assert.IsType<AcceptedAtActionResult>((await f.Controller.Create(Request(f.Site) with { RequestedBy = $"schedule:{Guid.NewGuid():N}" }, default)).Result);
        Assert.True(f.Command!.ChargeMonitoringCredits);
        Assert.Equal("dev-bridge", f.Command.RequestedBy);
    }

    // RD5 caps: this channel reserves no credits, so one request is bounded to 200 prompts and 20 platform entries.
    [Theory]
    [InlineData(200, 20, null)]
    [InlineData(201, 1, "INVALID_RUN")]
    [InlineData(1, 21, "INVALID_RUN")]
    public void WebCollectorRunIsCappedAt200PromptsAnd20Platforms(int prompts, int platforms, string? code)
    {
        var command = new CreateVisibilityRunCommand(Guid.NewGuid(), Guid.NewGuid(),
            Enumerable.Range(0, prompts).Select(_ => Guid.NewGuid()).ToArray(),
            Enumerable.Range(0, platforms).Select(i => new CollectorPlatformSelection("doubao", $"doubao.web.{i}", "zh-CN", "consumer_web")).ToArray(),
            "cap-fixture-key");
        if (code is null)
        {
            DomesticCollectorStore.ValidateCreateCommand(command);
            return;
        }
        var refused = Assert.Throws<CollectorStoreException>(() => DomesticCollectorStore.ValidateCreateCommand(command));
        Assert.Equal((code, 400), (refused.Code, refused.StatusCode));
        Assert.Equal((200, 20), (DomesticCollectorStore.MaxRunPrompts, DomesticCollectorStore.MaxRunPlatforms));
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

    private static CreateVisibilityRunRequest PlatformsRequest(Guid site)
        => new(site, [Guid.NewGuid()], [new PlatformSelectionRequest("doubao", "doubao.web")]);

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
        internal Guid User { get; } = Guid.NewGuid();
        internal VisibilityRunCreated Created { get; } = new(Guid.NewGuid(), "pending", 1, DateTimeOffset.UtcNow);
        internal VisibilityRunsController Controller { get; }
        internal List<string> Calls { get; } = [];
        internal CreateCloroVisibilityRunCommand? Command { get; private set; }
        internal CreateVisibilityRunCommand? DomesticCommand { get; private set; }
        /// <summary>Calls on the two run stores only, as Interface.Method.</summary>
        internal List<string> RunStoreCalls { get; } = [];
        internal bool SystemAdministrator { get; set; }
        internal CollectorStoreException? AdministratorFailure { get; set; }
        internal CollectorStoreException? CreateFailure { get; set; }
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
                    new(User, "fixture@example.test", "Fixture", "active", [new(Tenant, "Team", role, "active")]),
                    Guid.NewGuid(), "fixture");
            var host = new TestEnvironment { EnvironmentName = environment };
            Controller = new(Proxy<IVisibilityCollectorStore>(), Proxy<ICloroVisibilityStore>(),
                Proxy<ICloroSettingsStore>(), Proxy<ICloroKeyPool>(), Proxy<IAnswerProviderSettingsStore>(), Proxy<IManagementStore>(),
                Proxy<IVisibilityRunQueryStore>(), new TenantContextResolver(host),
                Proxy<IAppCollectionStore>(), host, Proxy<IResearchStore>())
            { ControllerContext = new() { HttpContext = context } };
        }

        private T Proxy<T>() where T : class
        {
            var proxy = DispatchProxy.Create<T, StoreProxy>();
            ((StoreProxy)(object)proxy).Handler = (method, args) =>
            {
                Calls.Add(method.Name);
                if (method.DeclaringType == typeof(IVisibilityCollectorStore) || method.DeclaringType == typeof(ICloroVisibilityStore))
                    RunStoreCalls.Add($"{method.DeclaringType.Name}.{method.Name}");
                if (method.DeclaringType != typeof(ICloroSettingsStore) && method.DeclaringType != typeof(ICloroKeyPool)
                    && method.DeclaringType != typeof(IAnswerProviderSettingsStore) && method.DeclaringType != typeof(IResearchStore)
                    && method.Name != nameof(ICloroVisibilityStore.CreateRunAsync))
                    Assert.Equal(Tenant, args[0]);
                switch (method.Name)
                {
                    case nameof(IResearchStore.IsSystemAdministratorAsync):
                        Assert.Equal(User, args[0]);
                        return AdministratorFailure is null ? Task.FromResult(SystemAdministrator) : Task.FromException<bool>(AdministratorFailure);
                    case nameof(IManagementStore.ResolveRunAsync):
                        return Task.FromResult<(IReadOnlyList<Guid> PromptIds, IReadOnlyList<CloroVisibilityTarget> Targets, int SampleCount)>(
                            ([Guid.NewGuid()], [new CloroVisibilityTarget("cloro", "chatgpt", "zh-CN", "US", "{}")], 1));
                    case nameof(IVisibilityCollectorStore.CreateRunAsync) when method.DeclaringType == typeof(IVisibilityCollectorStore):
                        DomesticCommand = Assert.IsType<CreateVisibilityRunCommand>(args[0]);
                        return Task.FromResult(Created);
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
                        return CreateFailure is null ? Task.FromResult(Created) : Task.FromException<VisibilityRunCreated>(CreateFailure);
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
