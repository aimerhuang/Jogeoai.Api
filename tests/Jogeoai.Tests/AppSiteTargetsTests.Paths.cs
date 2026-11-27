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

/// <summary>App 目标不进入 API 的预估、每日监控选择、立即采集与手动采集；站点目标里的 App 行；提问平台范围的键数上限（无库）。</summary>
public sealed partial class AppSiteTargetsTests
{
    private static readonly TimeZoneInfo Shanghai = CollectionScheduleCalendar.ParseTimeZone("Asia/Shanghai");
    private static readonly DateOnly Monday = new(2026, 11, 9);
    private static readonly DateTimeOffset BeforeMain = new(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);
    private static readonly SiteTargetWrite ChatGpt = new("cloro", "chatgpt", "scraped", "US", "en-US");
    private static readonly SiteTargetWrite DeepSeekWeekly = new("deepseek_api", "deepseek", "api", "CN", "zh-CN", true, AnswerMode.Deep, MonitoringFrequency.Weekly);
    private static readonly string ChatGptKey = CollectionIdentity.TargetKey(new CloroVisibilityTarget("cloro", "chatgpt", "en-US", "US"));

    private static IReadOnlyList<PromptMonitoringScale.EstimateCell> Cells(params SiteTargetWrite[] drafts)
        => PromptMonitoringScale.ProjectCells(ManagementStore.ValidateSiteTargetsWrite(new SiteTargetsWrite(0, drafts), false), [], Monday);

    private static string EstimateJson(PromptMonitoringScale.Snapshot snapshot, params SiteTargetWrite[] drafts)
        => JsonSerializer.Serialize(PromptMonitoringScale.EstimateTargets(snapshot, Cells(drafts), new HashSet<string>(), BeforeMain, Shanghai, 55m), Api);

    [Fact]
    public void AppDraftsLeaveEveryApiEstimateFigureAndBlockerUnchanged()
    {
        // Three prompts: all, scoped to ChatGPT + an App key, scoped to an App key only (no API pair either way).
        MonitoringPromptScope[] prompts =
        [
            new(Guid.NewGuid()), new(Guid.NewGuid(), [ChatGptKey, CollectionIdentity.AppTargetKey("doubao")]),
            new(Guid.NewGuid(), [CollectionIdentity.AppTargetKey("kimi")]),
        ];
        var snapshot = new PromptMonitoringScale.Snapshot(prompts, prompts.Length, [], 2);
        Assert.Equal(Cells(ChatGpt, DeepSeekWeekly).Select(c => c.Key), Cells([ChatGpt, .. AvailableApps, DeepSeekWeekly]).Select(c => c.Key));
        var api = EstimateJson(snapshot, ChatGpt, DeepSeekWeekly);
        Assert.Equal(api, EstimateJson(snapshot, [.. AvailableApps, ChatGpt, DeepSeekWeekly]));
        Assert.Equal(api, EstimateJson(snapshot, [ChatGpt, DeepSeekWeekly, App("ernie", enabled: false)]));
        var estimate = PromptMonitoringScale.EstimateTargets(snapshot, Cells([ChatGpt, DeepSeekWeekly, .. AvailableApps]), new HashSet<string>(),
            BeforeMain, Shanghai, 55m);
        // ChatGPT: 2 prompts × 2 samples × 7; DeepSeek deep weekly: 1 prompt × 2 samples × 1. No App cell, no App check.
        Assert.Equal((30m, 300m, 2), (estimate.WeeklyChecks, estimate.WeeklyCredits, estimate.Cells.Count));
        Assert.DoesNotContain(estimate.Cells, c => AppChannelDefinition.Engines.Any(e => CollectionIdentity.AppTargetKey(e) == c.TargetKey));
        Assert.Empty(estimate.Blockers);
        Assert.NotEqual(MonitoringSlotKind.App, estimate.NextSlotKind);

        // 20 API cells plus six App cells stay inside the limits; the 21st API cell is the blocker, App cells never are.
        var twenty = PromptMonitoringScale.EstimateTargets(snapshot, Cells([.. ApiTargets(20), .. AvailableApps]), new HashSet<string>(), BeforeMain, Shanghai, 0m);
        Assert.DoesNotContain("MONITORING_TOO_LARGE", twenty.Blockers);
        Assert.Equal(20, twenty.Cells.Count);
        var twentyOne = PromptMonitoringScale.EstimateTargets(snapshot, Cells([.. ApiTargets(21), .. AvailableApps]), new HashSet<string>(), BeforeMain, Shanghai, 0m);
        Assert.Contains("MONITORING_TOO_LARGE", twentyOne.Blockers);
        // A draft with App targets only has no API cell: exactly the estimate of an empty API draft.
        Assert.Equal(EstimateJson(snapshot), EstimateJson(snapshot, AvailableApps));
        Assert.Contains("MONITORING_TARGETS_REQUIRED", PromptMonitoringScale.EstimateTargets(snapshot, Cells(AvailableApps), new HashSet<string>(),
            BeforeMain, Shanghai, 0m).Blockers);
    }

    [Fact]
    public void PromptScopesMayNameTwentyApiTargetsPlusOneAppTargetPerPlatform()
    {
        Assert.Equal(27, PromptMonitoringScale.MaxScopeTargetKeys);
        var keys = Enumerable.Range(0, 28).Select(i => CollectionIdentity.TargetKey("cloro", "chatgpt", "scraped", "US", $"x-{i:00}")).ToArray();
        var eligible = keys.ToHashSet(StringComparer.Ordinal);
        Assert.Equal(27, PromptMonitoringScale.ValidateTargetKeys(keys[..27], eligible).Count);
        Assert.Equal(21, PromptMonitoringScale.ValidateTargetKeys(keys[..21], eligible).Count);
        foreach (var invalid in new[] { keys, [], [keys[0], keys[0]] })
        {
            var exception = Assert.Throws<CollectorStoreException>(() => PromptMonitoringScale.ValidateTargetKeys(invalid, eligible));
            Assert.Equal((PromptMonitoringScale.TargetInvalidCode, 400), Code(exception));
        }
        // A scope that names an App key adds nothing to the API round of the prompt estimate.
        var chatGpt = new CloroVisibilityTarget("cloro", "chatgpt", "en-US", "US", "{}");
        var snapshot = new PromptMonitoringScale.Snapshot([new(Guid.NewGuid())], 1, [chatGpt], 1);
        var estimate = PromptMonitoringScale.Estimate(snapshot, 2, [CollectionIdentity.AppTargetKey("doubao")], 100m, null, 1);
        Assert.Equal((1L, 0L, 1L), (estimate.CurrentChecksPerRound, estimate.AddedChecksPerRound, estimate.TotalChecksPerRound));
    }

    [Fact]
    public void DailyMonitoringAndInstantSelectionsNeverContainAppTargets()
    {
        CollectionTargetView Row(string provider, string engine, string capture)
            => ManagementStore.WithChannel(new CollectionTargetView(Guid.NewGuid(), provider, engine, engine, capture, "observed_surface", "CN", "zh-CN", true,
                "available", "supported", "conditional", "unknown"), new Dictionary<string, ManagementStore.AppEntryState> { ["doubao"] = new(true, null) });
        var app = Row("app_collector", "doubao", "app");
        Assert.True(app.MonitoringEligible);
        var selected = PromptMonitoringService.SelectMonitoringTargets([app, Row("doubao_api", "doubao", "api"), Row("domestic_collector", "doubao", "consumer_web")]);
        Assert.Equal("doubao_api", Assert.Single(selected).Provider);

        // Instant collection plans against the enabled API cells: an App key in the prompt scope or in the request selects nothing.
        var prompt = Guid.NewGuid();
        var appKey = CollectionIdentity.AppTargetKey("doubao");
        var chatGpt = new CloroVisibilityTarget("cloro", "chatgpt", "en-US", "US", "{}");
        var pairs = InstantCollectionPlanner.Plan([new InstantCollectionRequest(prompt, [appKey, ChatGptKey])],
            [new MonitoringPromptScope(prompt, [appKey, ChatGptKey])], [chatGpt]);
        Assert.Equal(new PromptTargetPair(prompt, ChatGptKey), Assert.Single(pairs));
        Assert.Empty(InstantCollectionPlanner.Plan([new InstantCollectionRequest(prompt, [appKey])], [new MonitoringPromptScope(prompt)], [chatGpt]));
    }

    [Fact]
    public void SiteTargetAppRowsCarryTheAppFieldsAndTheEntryState()
    {
        var anchor = new DateOnly(2026, 11, 18);
        CollectionTargetView Stored(string engine, bool enabled)
            => new(Guid.NewGuid(), "app_collector", engine, engine, "app", "observed_surface", "CN", "zh-CN", enabled, enabled ? "available" : "disabled",
                "supported", "conditional", "unknown", CollectionIdentity.AppTargetKey(engine), false, [AnswerMode.Fast], AnswerMode.Fast, AnswerMode.Fast,
                MonitoringFrequency.Weekly, anchor);
        var entries = new Dictionary<string, ManagementStore.AppEntryState>
        {
            ["qwen"] = new(true, null), ["ernie"] = new(false, AppUnavailableReasons.ContentUnreadable),
        };
        var stored = Stored("qwen", true);
        var usable = ManagementStore.WithChannel(stored, entries);
        Assert.Equal(stored with
        {
            DisplayName = "千问", SearchQueryCapability = "conditional", MonitoringEligible = true, Channel = CollectionChannel.App,
            FixedFrequency = MonitoringFrequency.Weekly,
        }, usable);
        Assert.Equal((MonitoringFrequency.Weekly, anchor, "available"), (usable.Frequency!.Value, usable.FrequencyAnchor!.Value, usable.Availability));
        Assert.Null(usable.UnavailableReason);

        // An enabled target of a platform whose App entry is disabled reads as unavailable with the reason; a disabled one stays disabled.
        var blocked = ManagementStore.WithChannel(Stored("ernie", true), entries);
        Assert.Equal(("文心一言", "unavailable", "APP_CONTENT_UNREADABLE"), (blocked.DisplayName, blocked.Availability, blocked.UnavailableReason?.Code));
        var off = ManagementStore.WithChannel(Stored("ernie", false), entries);
        Assert.Equal(("disabled", "APP_CONTENT_UNREADABLE"), (off.Availability, off.UnavailableReason?.Code));
        // Without entry data (049 not applied) an enabled App row is not reported as usable.
        var unknown = ManagementStore.WithChannel(Stored("kimi", true), null);
        Assert.Equal(("unavailable", CollectionChannel.App), (unknown.Availability, unknown.Channel!.Value));
        Assert.Null(unknown.UnavailableReason);
    }

    [Fact]
    public async Task ManualRunsRefuseAnExplicitAppTargetBeforeTouchingAnyStore()
    {
        var site = Guid.NewGuid();
        CloroTargetRequest[] targets = [new("cloro", "chatgpt"), new(" APP_COLLECTOR ", "doubao")];
        var byTarget = await RunsController().Create(new CreateVisibilityRunRequest(site, [Guid.NewGuid()], null, Targets: targets), default);
        var byPlatform = await RunsController().Create(new CreateVisibilityRunRequest(site, [Guid.NewGuid()],
            [new PlatformSelectionRequest("doubao", "doubao.consumer_app", "zh-CN", "consumer_app")]), default);
        foreach (var result in new[] { byTarget, byPlatform })
        {
            var refused = Assert.IsType<BadRequestObjectResult>(result.Result);
            var error = JsonSerializer.SerializeToElement(refused.Value).GetProperty("error");
            Assert.Equal("APP_TARGET_NOT_ALLOWED", error.GetProperty("code").GetString());
            Assert.Equal(AppChannelDefinition.TargetNotAllowedMessage, error.GetProperty("message").GetString());
        }
        Assert.Contains("每周计划", AppChannelDefinition.TargetNotAllowedMessage, StringComparison.Ordinal);
    }

    // 3.9 A: GET /collection-targets has the caller's tenant (session membership) and hands it to the store, which decides
    // whether App rows are returned. Program.cs binds Collector:AppTenantIds into the options the stores read.
    [Fact]
    public async Task TheCatalogEndpointAsksTheStoreForTheCallersTenant()
    {
        var tenant = Guid.NewGuid();
        var context = new DefaultHttpContext();
        context.Items[SessionAuthenticationMiddleware.ContextItemKey] = new AuthenticatedRequestContext(
            new AuthUserView(Guid.NewGuid(), "qa@example.test", "QA", "active", [new(tenant, "QA", "viewer", "active")]), Guid.NewGuid(), "test-only");
        object?[]? seen = null;
        var store = StudioFakes.Of<IManagementStore>((method, args) =>
        {
            Assert.Equal(nameof(IManagementStore.GetTargetCatalogAsync), method);
            seen = args;
            return Task.FromResult<IReadOnlyList<CollectionTargetView>>([]);
        });
        var controller = new ManagementController(store, new TenantContextResolver(new TestEnvironment()))
            { ControllerContext = new ControllerContext { HttpContext = context } };
        Assert.IsType<OkObjectResult>(await controller.TargetCatalog(default));
        Assert.Equal(tenant, Assert.IsType<Guid>(seen![1]));

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Jogeoai.slnx"))) directory = directory.Parent;
        var program = File.ReadAllText(Path.Combine(directory!.FullName, "src", "Jogeoai.Api", "Program.cs"));
        Assert.Contains("builder.Services.Configure<AppChannelOptions>(options =>", program, StringComparison.Ordinal);
        Assert.Contains("options.AppTenantIds = builder.Configuration.GetSection(AppChannelOptions.TenantIdsKey).Get<string[]>() ?? []", program, StringComparison.Ordinal);
    }

    // Every store but the replay lookup fails the test when called: the refusal happens before any provider or run work.
    // The caller is a system administrator: in Production the platforms shape is refused for anyone else (403 ADMIN_REQUIRED)
    // before its channel is looked at; the targets shape never asks.
    private static VisibilityRunsController RunsController()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["Idempotency-Key"] = $"app-target-{Guid.NewGuid():N}";
        context.Items[SessionAuthenticationMiddleware.ContextItemKey] = new AuthenticatedRequestContext(
            new AuthUserView(Guid.NewGuid(), "qa@example.test", "QA", "active", [new(Guid.NewGuid(), "QA", "member", "active")]), Guid.NewGuid(), "test-only");
        var replay = StudioFakes.Of<IVisibilityRunQueryStore>((method, _) => method == nameof(IVisibilityRunQueryStore.FindReplayAsync)
            ? Task.FromResult<VisibilityRunReplay?>(null)
            : throw new InvalidOperationException($"Unexpected call {method}"));
        return new VisibilityRunsController(StudioFakes.Unused<IVisibilityCollectorStore>(), StudioFakes.Unused<ICloroVisibilityStore>(),
            StudioFakes.Unused<ICloroSettingsStore>(), StudioFakes.Unused<ICloroKeyPool>(), StudioFakes.Unused<IAnswerProviderSettingsStore>(),
            StudioFakes.Unused<IManagementStore>(), replay, new TenantContextResolver(new TestEnvironment()),
            StudioFakes.Unused<IAppCollectionStore>(), new TestEnvironment(),
            StudioFakes.Of<IResearchStore>((method, _) => method == nameof(IResearchStore.IsSystemAdministratorAsync)
                ? Task.FromResult(true)
                : throw new InvalidOperationException($"Unexpected call {method}")))
            { ControllerContext = new ControllerContext { HttpContext = context } };
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
