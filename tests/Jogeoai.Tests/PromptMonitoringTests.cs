using Jogeoai.Api;
using Jogeoai.Api.Controllers;
using Jogeoai.Application;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jogeoai.Tests;

public sealed class PromptMonitoringTests
{
    [Theory]
    [InlineData(5, 1, 1, 50)]
    [InlineData(5, 2, 1, 100)]
    [InlineData(5, 3, 1, 150)]
    [InlineData(5, 3, 3, 450)]
    public void MonitoringPricingScalesByPromptTargetAndSample(int prompts, int targets, int samples, decimal expected)
    {
        Assert.Equal(expected, PromptMonitoringPricing.Estimate(prompts, targets, samples));
    }

    [Fact]
    public void CopilotAddsFiftyCreditsToFivePromptSingleSampleMonitoring()
    {
        var prompts = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        var original = new CollectionScheduleSelection(prompts,
            [new("cloro", "chatgpt"), new("cloro", "gemini")]);
        var enabled = original with { Targets = [.. original.Targets, new("cloro", "copilot")] };
        Assert.Equal(new[] { "chatgpt", "gemini", "copilot" }, enabled.Targets.Select(t => t.Engine));
        Assert.Equal(100m, PromptMonitoringPricing.Estimate(original.PromptIds.Count, original.Targets.Count, original.SampleCount));
        Assert.Equal(150m, PromptMonitoringPricing.Estimate(enabled.PromptIds.Count, enabled.Targets.Count, enabled.SampleCount));
    }

    [Fact]
    public void ManualCloroRunDoesNotOptIntoPromptMonitoringBilling()
    {
        var command = new CreateCloroVisibilityRunCommand(Guid.NewGuid(), Guid.NewGuid(),
            [Guid.NewGuid()], [new CloroVisibilityTarget("cloro", "copilot")], "manual-run-key");

        Assert.False(command.ChargeMonitoringCredits);
        Assert.Null(command.PromptTargetKeys);
        Assert.True((command with { ChargeMonitoringCredits = true }).ChargeMonitoringCredits);
    }

    private static readonly CloroVisibilityTarget ChatGpt = new("cloro", "chatgpt", "zh-CN", "US", "{}");
    private static readonly CloroVisibilityTarget Gemini = new("cloro", "gemini", "zh-CN", "US", "{}");

    [Fact]
    public void BillingKeepsTheLegacyProductWithoutScopesAndUsesTheMatrixWithThem()
    {
        var prompts = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).Order().ToArray();
        var legacy = new CollectionScheduleSelection(prompts, [ChatGpt, Gemini], 2);
        Assert.Equal(20, PromptMonitoringService.BillingCheckCount(legacy, 5));
        Assert.Equal(PromptMonitoringPricing.Estimate(5, 2, 2), PromptMonitoringPricing.Estimate(PromptMonitoringService.BillingCheckCount(legacy, 5)));
        // Two custom prompts only on Gemini: 3 × 2 + 2 × 1 pairs, × 2 samples.
        var scoped = legacy with { PromptTargets = [new(prompts[0], [CollectionIdentity.TargetKey(Gemini)]),
            new(prompts[1], [CollectionIdentity.TargetKey(Gemini)])] };
        Assert.Equal(16, PromptMonitoringService.BillingCheckCount(scoped, 5));
        Assert.Equal(160m, PromptMonitoringPricing.Estimate(PromptMonitoringService.BillingCheckCount(scoped, 5)));
        var view = new PromptMonitoringBillingView(5, 2, 2, 10m, 160m, 0, 0, 0, 0, "ready", CheckCount: 16);
        Assert.Equal((5, 2, 2, 16L), (view.ActivePromptCount, view.TargetCount, view.SampleCount, view.CheckCount));
    }

    [Fact]
    public void InitialRunKeyChangesWhenPromptScopesChange()
    {
        var site = Guid.NewGuid(); var prompt = Prompt(Guid.NewGuid()); var other = Prompt(Guid.NewGuid());
        var selection = new CollectionScheduleSelection([prompt.PromptId, other.PromptId], [ChatGpt, Gemini]);
        var key = PromptMonitoringIdentity.InitialRunKey(site, 1, [prompt, other], selection);
        Assert.Equal(key, PromptMonitoringIdentity.InitialRunKey(site, 1, [prompt, other], selection with { PromptTargets = [] }));
        Assert.NotEqual(key, PromptMonitoringIdentity.InitialRunKey(site, 1, [prompt, other],
            selection with { PromptTargets = [new(other.PromptId, [CollectionIdentity.TargetKey(Gemini)])] }));
    }

    [Fact]
    public void ScopedRunValidationChecksSubsetAndCountsPairsForTheLimit()
    {
        var prompts = Enumerable.Range(0, 200).Select(_ => Guid.NewGuid()).ToArray();
        var gemini = CollectionIdentity.TargetKey(Gemini);
        var command = new CreateCloroVisibilityRunCommand(Guid.NewGuid(), Guid.NewGuid(), prompts, [ChatGpt, Gemini],
            "prompt-monitor:scoped", "prompt-monitor", 3, RequireEnabledTargets: true, ChargeMonitoringCredits: true);
        // 200 × 2 × 3 = 1200 cartesian checks are rejected ...
        Assert.Equal("RUN_TOO_LARGE", Assert.IsType<CollectorStoreException>(Validate(command)).Code);
        // ... but 100 custom prompts on Gemini only: 100 × 2 × 3 + 100 × 1 × 3 = 900 pairs are allowed.
        var scoped = command with { PromptTargetKeys = prompts.Take(100).Select(id => new CollectionPromptTargets(id, [gemini])).ToArray() };
        var normalized = Assert.IsType<CreateCloroVisibilityRunCommand>(Validate(scoped));
        Assert.Equal(100, normalized.PromptTargetKeys!.Count);
        Assert.Equal(prompts.Take(100).Order(), normalized.PromptTargetKeys.Select(p => p.PromptId));
        // 34 custom prompts leave 166 × 2 × 3 + 34 × 3 = 1098 pairs.
        Assert.Equal("RUN_TOO_LARGE", Assert.IsType<CollectorStoreException>(Validate(command with
            { PromptTargetKeys = prompts.Take(34).Select(id => new CollectionPromptTargets(id, [gemini])).ToArray() })).Code);
        // Keys must be a subset of this Run's targets and prompts.
        var copilot = CollectionIdentity.TargetKey(new CloroVisibilityTarget("cloro", "copilot", "zh-CN", "US"));
        Assert.Equal("INVALID_RUN", Assert.IsType<CollectorStoreException>(Validate(scoped with
            { PromptTargetKeys = [new(prompts[0], [copilot])] })).Code);
        Assert.Equal("INVALID_RUN", Assert.IsType<CollectorStoreException>(Validate(scoped with
            { PromptTargetKeys = [new(Guid.NewGuid(), [gemini])] })).Code);
        // An empty list is the cartesian request.
        Assert.Null(Assert.IsType<CreateCloroVisibilityRunCommand>(Validate(command with
            { SampleCount = 2, PromptIds = prompts.Take(10).ToArray(), PromptTargetKeys = [] })).PromptTargetKeys);
    }

    private static object Validate(CreateCloroVisibilityRunCommand command)
    {
        try
        {
            return typeof(CloroVisibilityStore).GetMethod("ValidateAndNormalize", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [command])!;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is CollectorStoreException store) { return store; }
    }

    [Fact]
    public void DefaultTargetUsesSupportedCatalogCountryWhileKeepingChinesePrompts()
    {
        var catalog = new[] { new CollectionTargetView(null, "cloro", "chatgpt", "ChatGPT", "scraped",
            "observed_surface", "US", "en-US", true, "available", "supported", "conditional", "conditional") };
        var target = PromptMonitoringDefaults.Target(catalog, "zh-CN");
        Assert.Equal("US", target.Country);
        Assert.Equal("zh-CN", target.Locale);
        Assert.True(target.Enabled);
        Assert.Equal("chatgpt", PromptMonitoringDefaults.Target([catalog[0] with { Engine = "copilot" }, catalog[0] with { Engine = "gemini" }, catalog[0]], "zh-CN").Engine);
        Assert.Throws<CollectorStoreException>(() => PromptMonitoringDefaults.Target([], "zh-CN"));
        Assert.Throws<CollectorStoreException>(() => PromptMonitoringDefaults.Target([catalog[0] with { Availability = "unavailable" }], "zh-CN"));
    }

    private static PromptView Prompt(Guid id, int version = 1) => new(id, "选中的问题", version, "zh-CN", true,
        [], "unbranded", "system", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Fact]
    public void RefreshAndReorderedSelectionsReuseInitialRunAcrossSessions()
    {
        var site = Guid.NewGuid();
        var first = Prompt(Guid.NewGuid()); var second = Prompt(Guid.NewGuid());
        var targets = new[] { new CloroVisibilityTarget("cloro", "chatgpt", "zh-CN", "CN"), new CloroVisibilityTarget("cloro", "gemini", "zh-CN", "CN"), new CloroVisibilityTarget("cloro", "copilot", "zh-CN", "CN") };
        var a = new CollectionScheduleSelection([first.PromptId, second.PromptId], targets);
        var b = new CollectionScheduleSelection([second.PromptId, first.PromptId], targets.Reverse().ToArray());
        Assert.Equal(PromptMonitoringIdentity.InitialRunKey(site, 1, [first, second], a),
            PromptMonitoringIdentity.InitialRunKey(site, 1, [second, first], b));
    }

    [Fact]
    public void ChangedPromptVersionBrandOrTargetRequiresNewInitialRun()
    {
        var site = Guid.NewGuid(); var prompt = Prompt(Guid.NewGuid());
        var selection = new CollectionScheduleSelection([prompt.PromptId], [new("cloro", "chatgpt", "zh-CN", "CN")]);
        var key = PromptMonitoringIdentity.InitialRunKey(site, 1, [prompt], selection);
        Assert.NotEqual(key, PromptMonitoringIdentity.InitialRunKey(site, 1, [prompt with { Version = 2 }], selection));
        Assert.NotEqual(key, PromptMonitoringIdentity.InitialRunKey(site, 2, [prompt], selection));
        Assert.NotEqual(key, PromptMonitoringIdentity.InitialRunKey(Guid.NewGuid(), 1, [prompt], selection));
        Assert.NotEqual(key, PromptMonitoringIdentity.InitialRunKey(site, 1, [prompt], selection with { Targets = [new("cloro", "gemini", "zh-CN", "CN")] }));
        Assert.InRange(key.Length, 8, 191);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductionRequiresSessionBeforeStartingOrReading(bool start)
    {
        var service = new StubMonitoring();
        var controller = Controller(service, new DefaultHttpContext());
        var result = start ? await controller.Start(Guid.NewGuid(), default) : await controller.Get(Guid.NewGuid(), default);
        Assert.Equal(401, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        Assert.Equal(0, service.Calls);
    }

    [Fact]
    public async Task AnotherTenantCannotStartMonitoring()
    {
        var context = Context(Guid.NewGuid());
        context.Request.Headers["X-Tenant-Id"] = Guid.NewGuid().ToString();
        var service = new StubMonitoring();
        var result = await Controller(service, context).Start(Guid.NewGuid(), default);
        Assert.Equal(403, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        Assert.Equal(0, service.Calls);
    }

    [Fact]
    public async Task StatusReadsDoNotStartTasksAndFailuresRemainVisible()
    {
        var tenant = Guid.NewGuid(); var site = Guid.NewGuid(); var service = new StubMonitoring();
        var controller = Controller(service, Context(tenant));
        Assert.IsType<OkObjectResult>((await controller.Get(site, default)).Result);
        Assert.Equal((tenant, site, false), service.Last);
        service.Failure = new CollectorStoreException("MONITORING_PROVIDER_UNAVAILABLE", "服务不可用", 503);
        var result = await controller.Start(site, default);
        Assert.Equal(503, Assert.IsType<ObjectResult>(result.Result).StatusCode);
    }

    [Fact]
    public async Task ViewerStartOnlyReadsExistingMonitoringState()
    {
        var tenant = Guid.NewGuid();
        var site = Guid.NewGuid();
        var service = new StubMonitoring();
        var result = await Controller(service, Context(tenant, "viewer")).Start(site, default);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal((tenant, site, false), service.Last);
    }

    // ---- Instant collection orchestration in ManagementController (design §4) ----

    private static PromptView Stored(Guid id, bool enabled = true, uint configVersion = 1, string scope = "all", IReadOnlyList<string>? keys = null)
        => new(id, "新问题", 1, "zh-CN", enabled, [], "unbranded", "system", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            ConfigVersion: configVersion, TargetScope: scope, TargetKeys: keys);

    [Fact]
    public async Task CreatePromptAttachesTheInstantCollectionResult()
    {
        var tenant = Guid.NewGuid(); var site = Guid.NewGuid(); var prompt = Stored(Guid.NewGuid());
        var (controller, store, monitoring, _) = Management(tenant);
        store.Handlers[nameof(IManagementStore.CreatePromptAsync)] = _ => Task.FromResult(prompt);

        var result = Assert.IsType<ObjectResult>(await controller.CreatePrompt(site, new PromptWrite("新问题", "zh-CN"), default));
        Assert.Equal(201, result.StatusCode);
        var view = Assert.IsType<PromptView>(result.Value);
        Assert.Same(monitoring.Instant, view.InstantCollection);
        var call = Assert.Single(monitoring.InstantCalls);
        Assert.Equal((nameof(IPromptMonitoringService.CollectNewPromptsAsync), tenant, site), (call.Method, call.Tenant, call.Site));
        Assert.Equal([prompt.PromptId], Assert.IsType<Guid[]>(call.Argument));
    }

    [Fact]
    public async Task DisabledNewPromptIsNotCollected()
    {
        var (controller, store, monitoring, _) = Management(Guid.NewGuid());
        store.Handlers[nameof(IManagementStore.CreatePromptAsync)] = _ => Task.FromResult(Stored(Guid.NewGuid(), enabled: false));
        var view = Assert.IsType<PromptView>(Assert.IsType<ObjectResult>(
            await controller.CreatePrompt(Guid.NewGuid(), new PromptWrite("停用", "zh-CN", Enabled: false), default)).Value);
        Assert.Null(view.InstantCollection);
        Assert.Empty(monitoring.InstantCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFailingInstantCollectionNeverFailsTheWrite(bool businessError)
    {
        var tenant = Guid.NewGuid(); var site = Guid.NewGuid();
        var (controller, store, monitoring, logger) = Management(tenant);
        store.Handlers[nameof(IManagementStore.CreatePromptAsync)] = _ => Task.FromResult(Stored(Guid.NewGuid()));
        monitoring.InstantFailure = businessError
            ? new CollectorStoreException("MONITORING_BUSY", "忙", 409)
            : new InvalidOperationException("boom");

        var result = Assert.IsType<ObjectResult>(await controller.CreatePrompt(site, new PromptWrite("新问题", "zh-CN"), default));
        Assert.Equal(201, result.StatusCode);
        var instant = Assert.IsType<PromptView>(result.Value).InstantCollection!;
        Assert.Equal((InstantCollectionStatus.Skipped, "INSTANT_COLLECTION_FAILED", (Guid?)null), (instant.Status, instant.ReasonCode, instant.RunId));
        Assert.False(string.IsNullOrWhiteSpace(instant.Message));
        var warning = Assert.Single(logger.Entries);
        Assert.Equal((LogLevel.Warning, 4301), (warning.Level, warning.EventId.Id));
        Assert.Same(monitoring.InstantFailure, warning.Exception);
    }

    [Fact]
    public async Task RequestCancellationIsNotTurnedIntoAFailedInstantCollection()
    {
        var (controller, store, monitoring, _) = Management(Guid.NewGuid());
        store.Handlers[nameof(IManagementStore.CreatePromptAsync)] = _ => Task.FromResult(Stored(Guid.NewGuid()));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        monitoring.InstantFailure = new OperationCanceledException(cancelled.Token);
        await Assert.ThrowsAsync<OperationCanceledException>(() => controller.CreatePrompt(Guid.NewGuid(), new PromptWrite("新问题", "zh-CN"), cancelled.Token));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BatchAndImportCollectOnlyTheTrulyCreatedPrompts(bool batch)
    {
        var tenant = Guid.NewGuid(); var site = Guid.NewGuid();
        var created = new[] { Guid.NewGuid(), Guid.NewGuid() }; var duplicate = Guid.NewGuid();
        var (controller, store, monitoring, _) = Management(tenant, "member", "batch-key-0001");
        store.Handlers[nameof(IManagementStore.ImportAsync)] = _ => Task.FromResult(
            new PromptImportResult(2, 1, [created[0], duplicate, created[1]], created));
        IReadOnlyList<PromptImportRow> rows = [new("a", "zh-CN"), new("b", "zh-CN"), new("c", "zh-CN")];

        var result = batch
            ? await controller.CreateBatch(site, new PromptBatchCreateRequest(rows), default)
            : await controller.Import(site, rows, default);
        var body = Assert.IsType<PromptImportResult>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Same(monitoring.Instant, body.InstantCollection);
        Assert.Equal(created, Assert.IsType<Guid[]>(Assert.Single(monitoring.InstantCalls).Argument));

        // Nothing created (all duplicates, or a replay of a pre-change stored result): no instant collection.
        monitoring.InstantCalls.Clear();
        foreach (var stored in new[] { new PromptImportResult(0, 3, [duplicate], []), new PromptImportResult(1, 0, [duplicate]) })
        {
            store.Handlers[nameof(IManagementStore.ImportAsync)] = _ => Task.FromResult(stored);
            var none = Assert.IsType<PromptImportResult>(Assert.IsType<OkObjectResult>(batch
                ? await controller.CreateBatch(site, new PromptBatchCreateRequest(rows), default)
                : await controller.Import(site, rows, default)).Value);
            Assert.Null(none.InstantCollection);
        }
        Assert.Empty(monitoring.InstantCalls);
    }

    [Fact]
    public async Task PatchWithAScopeChangeReadsBeforeAndCollectsAddedTargets()
    {
        var tenant = Guid.NewGuid(); var site = Guid.NewGuid(); var id = Guid.NewGuid();
        var before = Stored(id, configVersion: 4, scope: "custom", keys: ["a"]);
        var after = Stored(id, configVersion: 5, scope: "custom", keys: ["a", "b"]);
        var (controller, store, monitoring, _) = Management(tenant);
        store.Handlers[nameof(IManagementStore.GetPromptAsync)] = _ => Task.FromResult<PromptView?>(before);
        store.Handlers[nameof(IManagementStore.UpdatePromptAsync)] = _ => Task.FromResult(after);

        var view = Assert.IsType<PromptView>(Assert.IsType<OkObjectResult>(
            await controller.PatchPrompt(site, id, new PromptPatch(ExpectedVersion: 4, TargetKeys: ["a", "b"]), default)).Value);
        Assert.Same(monitoring.Instant, view.InstantCollection);
        Assert.Equal([nameof(IManagementStore.GetPromptAsync), nameof(IManagementStore.UpdatePromptAsync)], store.Calls);
        var call = Assert.Single(monitoring.InstantCalls);
        Assert.Equal((nameof(IPromptMonitoringService.CollectAddedTargetsAsync), tenant, site), (call.Method, call.Tenant, call.Site));
        Assert.Equal((before, after), Assert.IsType<(PromptView, PromptView)>(call.Argument));

        // resetTargetScope=true is a scope change too.
        monitoring.InstantCalls.Clear();
        await controller.PatchPrompt(site, id, new PromptPatch(ExpectedVersion: 4, ResetTargetScope: true), default);
        Assert.Single(monitoring.InstantCalls);
    }

    // App channel implement 5.6b: appEligibility belongs to the prompt list and the prompt view only. The two management reads
    // ask the store for it; the `before` read of a patch and an internal caller (the article studio) take the plain view.
    [Fact]
    public async Task OnlyThePromptListAndThePromptViewAskTheStoreForAppEligibility()
    {
        var tenant = Guid.NewGuid(); var site = Guid.NewGuid(); var id = Guid.NewGuid();
        var (controller, store, _, _) = Management(tenant);
        var asked = new List<(string Method, bool IncludeAppEligibility)>();
        store.Handlers[nameof(IManagementStore.ListPromptsAsync)] = args =>
        {
            asked.Add(("list", (bool)args[^1]!));
            return Task.FromResult(new CursorPage<PromptView>([Stored(id)], null));
        };
        store.Handlers[nameof(IManagementStore.GetPromptAsync)] = args =>
        {
            asked.Add(("get", (bool)args[^1]!));
            return Task.FromResult<PromptView?>(Stored(id, configVersion: 4));
        };
        store.Handlers[nameof(IManagementStore.UpdatePromptAsync)] = _ => Task.FromResult(Stored(id, configVersion: 5));

        Assert.IsType<OkObjectResult>(await controller.Prompts(site, null, null, null));
        Assert.IsType<OkObjectResult>(await controller.GetPrompt(site, id, default));
        Assert.IsType<OkObjectResult>(await controller.PatchPrompt(site, id, new PromptPatch(ExpectedVersion: 4, ResetTargetScope: true), default));
        Assert.Equal([("list", true), ("get", true), ("get", false)], asked);

        // A real internal caller: the article studio lists the brand's prompts through the same store method.
        asked.Clear();
        var management = StudioFakes.Of<IManagementStore>((method, args) =>
        {
            if (method == nameof(IManagementStore.GetBrandAsync))
                return Task.FromResult<BrandView?>(new BrandView(site, "本品牌", null, [], [], "CN", "zh-CN", "Asia/Shanghai", "active", 1,
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            if (method != nameof(IManagementStore.ListPromptsAsync)) throw new InvalidOperationException(method);
            asked.Add(("studio", (bool)args![^1]!));
            throw new CollectorStoreException("STOP_AFTER_LIST", "stop", 400);
        });
        var studio = new ArticleStudioService(management, StudioFakes.Unused<ICreditStore>(), StudioFakes.Unused<IResearchStore>(),
            StudioFakes.Unused<IArticleMaterialSelectionService>(), StudioFakes.Unused<IContentStore>(), StudioFakes.Unused<IArticleStudioStore>(),
            StudioFakes.Unused<IAnalyticsQueryStore>(), StudioFakes.Unused<IKnowledgeRetrievalService>());
        Assert.Equal("STOP_AFTER_LIST", (await Assert.ThrowsAsync<CollectorStoreException>(()
            => studio.PrepareAsync(tenant, site, Guid.NewGuid(), true, null))).Code);
        Assert.Equal([("studio", false)], asked);
    }

    [Fact]
    public async Task PatchWithoutAScopeChangeOrWithAStaleBeforeDoesNotCollect()
    {
        var id = Guid.NewGuid();
        var (controller, store, monitoring, _) = Management(Guid.NewGuid());
        store.Handlers[nameof(IManagementStore.GetPromptAsync)] = _ => Task.FromResult<PromptView?>(Stored(id, configVersion: 7));
        store.Handlers[nameof(IManagementStore.UpdatePromptAsync)] = _ => Task.FromResult(Stored(id, configVersion: 8));

        // Content-only edit: no `before` read and no collection; the response carries no instantCollection.
        var view = Assert.IsType<PromptView>(Assert.IsType<OkObjectResult>(
            await controller.PatchPrompt(Guid.NewGuid(), id, new PromptPatch("改写", ExpectedVersion: 7), default)).Value);
        Assert.Null(view.InstantCollection);
        Assert.Equal([nameof(IManagementStore.UpdatePromptAsync)], store.Calls);

        // `before` read at another version than expectedVersion is not the state the update replaced: no diff.
        await controller.PatchPrompt(Guid.NewGuid(), id, new PromptPatch(ExpectedVersion: 6, TargetKeys: ["a"]), default);
        Assert.Empty(monitoring.InstantCalls);
    }

    [Fact]
    public async Task ViewerCannotWritePromptsSoNothingIsCollected()
    {
        var (controller, store, monitoring, _) = Management(Guid.NewGuid(), "viewer");
        var result = Assert.IsType<ObjectResult>(await controller.CreatePrompt(Guid.NewGuid(), new PromptWrite("新问题", "zh-CN"), default));
        Assert.Equal(403, result.StatusCode);
        Assert.Empty(store.Calls);
        Assert.Empty(monitoring.InstantCalls);
    }

    [Fact]
    public void InstantCollectionJsonUsesSnakeCaseStatusesAndIsOmittedWhenAbsent()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        var plain = JsonSerializer.SerializeToElement(Stored(Guid.NewGuid()), options);
        Assert.False(plain.TryGetProperty("instantCollection", out _));
        Assert.False(JsonSerializer.SerializeToElement(new PromptImportResult(0, 0, []), options).TryGetProperty("instantCollection", out _));

        var view = Stored(Guid.NewGuid()) with
        {
            InstantCollection = new(InstantCollectionStatus.AlreadyCollected, null, 0, 0m, Message: "今天已采集"),
        };
        var json = JsonSerializer.SerializeToElement(view, options).GetProperty("instantCollection");
        Assert.Equal("already_collected", json.GetProperty("status").GetString());
        Assert.Equal("started", JsonSerializer.SerializeToElement(InstantCollectionStatus.Started, options).GetString());
        Assert.Equal("skipped", JsonSerializer.SerializeToElement(InstantCollectionStatus.Skipped, options).GetString());

        // The stored import result (default serializer, as written to prompt_imports) keeps CreatedPromptIds and never the
        // instant result; a row stored before this change reads back with CreatedPromptIds = null.
        var created = Guid.NewGuid();
        var stored = JsonSerializer.Serialize(new PromptImportResult(1, 0, [created], [created]));
        Assert.DoesNotContain("InstantCollection", stored, StringComparison.Ordinal);
        Assert.Equal([created], JsonSerializer.Deserialize<PromptImportResult>(stored)!.CreatedPromptIds);
        Assert.Null(JsonSerializer.Deserialize<PromptImportResult>($$"""{"CreatedCount":1,"DuplicateCount":0,"PromptIds":["{{created}}"]}""")!.CreatedPromptIds);
    }

    private static (ManagementController Controller, ManagementStoreProxy Store, StubMonitoring Monitoring, ListLogger Logger) Management(
        Guid tenant, string role = "owner", string? idempotencyKey = null)
    {
        var context = Context(tenant, role);
        if (idempotencyKey is not null) context.Request.Headers["Idempotency-Key"] = idempotencyKey;
        var store = DispatchProxy.Create<IManagementStore, ManagementStoreProxy>();
        var monitoring = new StubMonitoring();
        var logger = new ListLogger();
        var controller = new ManagementController(store, new TenantContextResolver(new TestEnvironment()), monitoring, logger)
            { ControllerContext = new ControllerContext { HttpContext = context } };
        return (controller, (ManagementStoreProxy)(object)store, monitoring, logger);
    }

    public class ManagementStoreProxy : DispatchProxy
    {
        public Dictionary<string, Func<object?[], object?>> Handlers { get; } = [];
        public List<string> Calls { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Calls.Add(targetMethod!.Name);
            return Handlers.TryGetValue(targetMethod.Name, out var handler) ? handler(args ?? [])
                : throw new InvalidOperationException($"Unexpected management store call: {targetMethod.Name}");
        }
    }

    private sealed class ListLogger : ILogger<ManagementController>
    {
        public List<(LogLevel Level, EventId EventId, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, eventId, exception));
    }

    private static DefaultHttpContext Context(Guid tenant, string role = "owner")
    {
        var context = new DefaultHttpContext();
        context.Items[SessionAuthenticationMiddleware.ContextItemKey] = new AuthenticatedRequestContext(
            new AuthUserView(Guid.NewGuid(), "qa@example.test", "QA", "active", [new(tenant, "QA", role, "active")]), Guid.NewGuid(), "test-only");
        return context;
    }
    private static PromptMonitoringController Controller(StubMonitoring service, HttpContext context) => new(service, new TenantContextResolver(new TestEnvironment()))
        { ControllerContext = new ControllerContext { HttpContext = context } };
    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
    private sealed class StubMonitoring : IPromptMonitoringService
    {
        public Task<TeamMonitoringEstimateView> EstimateTeamMonitoringAsync(Guid tenantId, TeamMonitoringEstimateRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public int Calls;
        public (Guid Tenant, Guid Site, bool Start) Last;
        public CollectorStoreException? Failure;
        public InstantCollectionView? Instant = new(InstantCollectionStatus.Started, Guid.NewGuid(), 2, 20m, Message: "已开始采集：2 次检测，约 20 积分。");
        public Exception? InstantFailure;
        public List<(string Method, Guid Tenant, Guid Site, object Argument)> InstantCalls { get; } = [];
        public Task<InstantCollectionView?> CollectNewPromptsAsync(Guid tenantId, Guid siteId, IReadOnlyList<Guid> promptIds,
            CancellationToken cancellationToken = default) => Collect(nameof(CollectNewPromptsAsync), tenantId, siteId, promptIds.ToArray());
        public Task<InstantCollectionView?> CollectAddedTargetsAsync(Guid tenantId, Guid siteId, PromptView before, PromptView after,
            CancellationToken cancellationToken = default) => Collect(nameof(CollectAddedTargetsAsync), tenantId, siteId, (before, after));
        private Task<InstantCollectionView?> Collect(string method, Guid tenantId, Guid siteId, object argument)
        {
            InstantCalls.Add((method, tenantId, siteId, argument));
            return InstantFailure is not null ? Task.FromException<InstantCollectionView?>(InstantFailure) : Task.FromResult(Instant);
        }
        public Task<PromptMonitoringView> EnsureAsync(Guid tenantId, Guid siteId, CancellationToken cancellationToken = default) => Execute(tenantId, siteId, true);
        public Task<PromptMonitoringView> GetAsync(Guid tenantId, Guid siteId, CancellationToken cancellationToken = default) => Execute(tenantId, siteId, false);
        public Task<PromptMonitoringEstimateView> EstimateAsync(Guid tenantId, Guid siteId, PromptMonitoringEstimateRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MonitoringTargetsEstimateView> EstimateTargetsAsync(Guid tenantId, Guid siteId, MonitoringTargetsEstimateRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        private Task<PromptMonitoringView> Execute(Guid tenantId, Guid siteId, bool start)
        {
            Calls++; Last = (tenantId, siteId, start);
            if (Failure is not null) throw Failure;
            return Task.FromResult(new PromptMonitoringView([], null, null));
        }
    }
}
