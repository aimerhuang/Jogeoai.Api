using System.Text.Json;
using Jogeoai.Api;
using Jogeoai.Api.Controllers;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Jogeoai.Tests;

/// <summary>POST /sites/{siteId}/targets/estimate：每周 / 下一轮检测与积分、平台范围交集、锚点推算、阻断项、校验与 viewer 403（无库）。</summary>
public sealed class MonitoringTargetsEstimateTests
{
    private static readonly TimeZoneInfo Shanghai = CollectionScheduleCalendar.ParseTimeZone("Asia/Shanghai");
    private static readonly DateOnly Monday = new(2026, 11, 9);
    // 2026-11-09 10:00 Asia/Shanghai: the next 00:05 (11-10) comes before the next 09:01 (11-10).
    private static readonly DateTimeOffset AfterMain = new(2026, 11, 9, 2, 0, 0, TimeSpan.Zero);
    // 2026-11-09 08:00 Asia/Shanghai: today's 09:01 comes before the next 00:05.
    private static readonly DateTimeOffset BeforeMain = new(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);
    private static readonly SiteTargetWrite ChatGpt = new("cloro", "chatgpt", "scraped", "US", "en-US");
    private static readonly SiteTargetWrite DeepSeek = new("deepseek_api", "deepseek", "api", "CN", "zh-CN");
    private static readonly SiteTargetWrite Doubao = new("doubao_api", "doubao", "api", "CN", "zh-CN");
    private static readonly SiteTargetWrite Domestic = new("domestic_collector", "doubao", "consumer_web", "CN", "zh-CN");
    private static readonly string DeepKey = CollectionIdentity.TargetKey("deepseek_api", "deepseek", "api", "CN", "zh-CN", "deep");
    private static readonly string ChatGptKey = CollectionIdentity.TargetKey(new CloroVisibilityTarget("cloro", "chatgpt", "en-US", "US"));

    private static IReadOnlyList<PromptMonitoringScale.EstimateCell> Cells(DateOnly today, params SiteTargetWrite[] drafts)
        => PromptMonitoringScale.ProjectCells(ManagementStore.ValidateSiteTargetsWrite(new SiteTargetsWrite(0, drafts), false), [], today);

    private static PromptMonitoringScale.Snapshot Snapshot(int sampleCount, params MonitoringPromptScope[] prompts)
        => new(prompts, prompts.Length, [], sampleCount);

    private static MonitoringPromptScope All() => new(Guid.NewGuid());

    [Fact]
    public void WeeklyMathUsesFourteenSevenThreeAndAHalfAndOne()
    {
        var cells = Cells(Monday,
            ChatGpt with { Frequency = MonitoringFrequency.TwiceDaily }, Doubao,
            DeepSeek with { Frequency = MonitoringFrequency.EveryOtherDay },
            DeepSeek with { AnswerMode = AnswerMode.Deep, Frequency = MonitoringFrequency.Weekly });
        var estimate = PromptMonitoringScale.EstimateTargets(Snapshot(1, All(), All()), cells, new HashSet<string>(), AfterMain, Shanghai, 10_000m);
        // 2 prompts × (14 + 7 + 3.5 + 1) = 51 checks a week, 510 credits.
        Assert.Equal((2, 51m, 510m), (estimate.PromptCount, estimate.WeeklyChecks, estimate.WeeklyCredits));
        Assert.Equal((PromptMonitoringPricing.UnitPrice, 1), (estimate.UnitPrice, estimate.SampleCount));
        Assert.Equal(4, estimate.Cells.Count);
        Assert.Equal(new[] { 14m, 7m, 3.5m, 1m }.Order(), estimate.Cells.Select(c => c.WeeklyRuns).Order());
        Assert.All(estimate.Cells, c => Assert.Equal(2, c.PromptCount));
        var deep = Assert.Single(estimate.Cells, c => c.TargetKey == DeepKey);
        Assert.Equal(("deepseek", AnswerMode.Deep, MonitoringFrequency.Weekly), (deep.Engine, deep.AnswerMode, deep.Frequency));
        // Omitted modes resolve to the provider default (Doubao defaults to deep).
        Assert.Equal(AnswerMode.Deep, Assert.Single(estimate.Cells, c => c.Engine == "doubao").AnswerMode);
        Assert.Empty(estimate.Blockers);

        // Samples multiply every count.
        var sampled = PromptMonitoringScale.EstimateTargets(Snapshot(2, All(), All()), cells, new HashSet<string>(), AfterMain, Shanghai, 10_000m);
        Assert.Equal(102m, sampled.WeeklyChecks);
    }

    [Fact]
    public void CustomPromptScopesIntersectTheEnabledCells()
    {
        var deepOnly = new MonitoringPromptScope(Guid.NewGuid(), [DeepKey]);
        var disabledOnly = new MonitoringPromptScope(Guid.NewGuid(), [CollectionIdentity.TargetKey("cloro", "gemini", "scraped", "US", "en-US")]);
        var cells = Cells(Monday, ChatGpt, DeepSeek with { AnswerMode = AnswerMode.Deep });
        var estimate = PromptMonitoringScale.EstimateTargets(Snapshot(1, All(), deepOnly, disabledOnly), cells, new HashSet<string>(),
            BeforeMain, Shanghai, 10_000m);
        Assert.Equal(3, estimate.PromptCount);
        Assert.Equal(1, Assert.Single(estimate.Cells, c => c.TargetKey == ChatGptKey).PromptCount);
        Assert.Equal(2, Assert.Single(estimate.Cells, c => c.TargetKey == DeepKey).PromptCount);
        // (1 + 2) pairs × 7 = 21 a week; the next 09:01 slot has all three pairs. The out-of-scope prompt never widens to all.
        Assert.Equal(21m, estimate.WeeklyChecks);
        Assert.Equal((3L, 30m), (estimate.NextSlotChecks, estimate.NextSlotCredits));
        Assert.Empty(estimate.Blockers);

        var none = PromptMonitoringScale.EstimateTargets(Snapshot(1, disabledOnly), cells, new HashSet<string>(), BeforeMain, Shanghai, 0m);
        Assert.Equal(["MONITORING_TARGETS_REQUIRED"], none.Blockers);
        Assert.Equal(0m, none.WeeklyChecks);
    }

    [Fact]
    public void NextSlotIsTheNearerOfNightAndMainAndFiltersByFrequency()
    {
        var prompts = Snapshot(1, All(), All());
        var withNight = Cells(Monday, ChatGpt with { Frequency = MonitoringFrequency.TwiceDaily }, Doubao,
            DeepSeek with { Frequency = MonitoringFrequency.Weekly });
        // 10:00 local: next 00:05 (11-10) beats next 09:01 (11-10); only the twice_daily cell runs at night.
        var night = PromptMonitoringScale.EstimateTargets(prompts, withNight, new HashSet<string>(), AfterMain, Shanghai, 15m);
        Assert.Equal(MonitoringSlotKind.Night, night.NextSlotKind);
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 16, 5, 0, TimeSpan.Zero), night.NextSlotAt);
        Assert.Equal((2L, 20m, 5m), (night.NextSlotChecks, night.NextSlotCredits, night.Shortfall));

        // Without a twice_daily cell there is no night round: next 09:01 on Tuesday, where the Monday-anchored weekly cell is off.
        var noNight = Cells(Monday, ChatGpt, DeepSeek with { Frequency = MonitoringFrequency.Weekly });
        var main = PromptMonitoringScale.EstimateTargets(prompts, noNight, new HashSet<string>(), AfterMain, Shanghai, 100m);
        Assert.Equal(MonitoringSlotKind.Main, main.NextSlotKind);
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 1, 1, 0, TimeSpan.Zero), main.NextSlotAt);
        Assert.Equal((2L, 20m, 0m), (main.NextSlotChecks, main.NextSlotCredits, main.Shortfall));

        // 08:00 local: today's 09:01 comes first, and the weekly cell anchored today is due.
        var today = PromptMonitoringScale.EstimateTargets(prompts, withNight, new HashSet<string>(), BeforeMain, Shanghai, 100m);
        Assert.Equal(MonitoringSlotKind.Main, today.NextSlotKind);
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 1, 1, 0, TimeSpan.Zero), today.NextSlotAt);
        Assert.Equal(6L, today.NextSlotChecks);
    }

    [Fact]
    public void ProjectedAnchorsFollowThePutRules()
    {
        var anchor = new DateOnly(2026, 11, 2);
        var drafts = ManagementStore.ValidateSiteTargetsWrite(new SiteTargetsWrite(0,
        [
            DeepSeek with { Frequency = MonitoringFrequency.Weekly },                                   // weekly → weekly: keep
            DeepSeek with { AnswerMode = AnswerMode.Deep, Frequency = MonitoringFrequency.EveryOtherDay }, // daily → every_other_day: today
            ChatGpt with { Frequency = MonitoringFrequency.Weekly },                                    // newly enabled: today
            Doubao with { Enabled = false }, Domestic,                                                  // ignored
        ]), false);
        var existing = new[]
        {
            new AutomaticMonitoringSelection.Target(Guid.NewGuid(), new CloroVisibilityTarget("deepseek_api", "deepseek", "zh-CN", "CN", "{}"),
                DateTimeOffset.UnixEpoch, MonitoringFrequency.Weekly, anchor),
            new AutomaticMonitoringSelection.Target(Guid.NewGuid(), new CloroVisibilityTarget("deepseek_api", "deepseek", "zh-CN", "CN", "{}", "deep"),
                DateTimeOffset.UnixEpoch, MonitoringFrequency.Daily, anchor),
        };
        var cells = PromptMonitoringScale.ProjectCells(drafts, existing, Monday);
        Assert.Equal(3, cells.Count);
        Assert.Equal(anchor, Assert.Single(cells, c => c.Engine == "deepseek" && c.Mode == AnswerMode.Fast).Anchor);
        Assert.Equal(Monday, Assert.Single(cells, c => c.Key == DeepKey).Anchor);
        Assert.Equal(Monday, Assert.Single(cells, c => c.Key == ChatGptKey).Anchor);
        // The kept anchor (Monday 11-02) makes the weekly cell due on Monday 11-09 even though the draft is saved that day.
        var estimate = PromptMonitoringScale.EstimateTargets(Snapshot(1, All()), cells, new HashSet<string>(), BeforeMain, Shanghai, 0m);
        Assert.Equal(3L, estimate.NextSlotChecks);
    }

    [Fact]
    public void BlockersReportSizeMissingTargetsAndRuntimeUnsupportedModes()
    {
        var countries = new[] { "US", "GB", "DE", "FR" };
        var engines = AnswerProviderDefinition.Cloro.Engines;
        var many = engines.SelectMany(e => countries.Select(c => new PromptMonitoringScale.EstimateCell(
            new CloroVisibilityTarget("cloro", e, "en-US", c, "{}"), CollectionIdentity.TargetKey(new CloroVisibilityTarget("cloro", e, "en-US", c)),
            e, AnswerMode.Fast, MonitoringFrequency.Daily, null))).Take(21).OrderBy(c => c.Key, StringComparer.Ordinal).ToArray();
        Assert.Equal(21, many.Length);
        var tooMany = PromptMonitoringScale.EstimateTargets(Snapshot(1, All()), many, new HashSet<string>(), BeforeMain, Shanghai, 0m);
        Assert.Equal(["MONITORING_TOO_LARGE"], tooMany.Blockers);
        // 1000-check round limit uses every cell (any day may have all of them due), even if the next slot is smaller.
        var prompts = Enumerable.Range(0, 200).Select(_ => All()).ToArray();
        var big = PromptMonitoringScale.EstimateTargets(Snapshot(1, prompts), Cells(Monday, ChatGpt, Doubao, DeepSeek,
            DeepSeek with { AnswerMode = AnswerMode.Deep }, ChatGpt with { Engine = "gemini" }, ChatGpt with { Engine = "copilot", Frequency = MonitoringFrequency.Weekly }),
            new HashSet<string>(), AfterMain, Shanghai, 0m);
        Assert.Contains("MONITORING_TOO_LARGE", big.Blockers);
        Assert.Equal(1000L, big.NextSlotChecks);

        var empty = PromptMonitoringScale.EstimateTargets(Snapshot(1, All()), [], new HashSet<string>(), BeforeMain, Shanghai, 0m);
        Assert.Equal(["MONITORING_TARGETS_REQUIRED"], empty.Blockers);
        Assert.Empty(empty.Cells);

        var cells = Cells(Monday, Doubao with { AnswerMode = AnswerMode.Fast });
        var unsupported = PromptMonitoringScale.EstimateTargets(Snapshot(1, All()), cells, new HashSet<string>([cells[0].Key]), BeforeMain, Shanghai, 0m);
        Assert.Equal([AnswerModeRules.AnswerModeUnsupported], unsupported.Blockers);
        Assert.Equal(10m, unsupported.Shortfall);
    }

    [Fact]
    public void DraftValidationMatchesThePutButSizeIsNotA400()
    {
        Assert.Equal(21, ManagementStore.ValidateSiteTargetsWrite(new SiteTargetsWrite(0,
            AnswerProviderDefinition.Cloro.Engines.SelectMany(e => new[] { "US", "GB", "DE", "FR" }.Select(c => ChatGpt with { Engine = e, Country = c }))
                .Take(21).ToArray()), false).Length);
        var limit = Assert.Throws<CollectorStoreException>(() => ManagementStore.ValidateSiteTargetsWrite(new SiteTargetsWrite(0,
            AnswerProviderDefinition.Cloro.Engines.SelectMany(e => new[] { "US", "GB", "DE", "FR" }.Select(c => ChatGpt with { Engine = e, Country = c }))
                .Take(21).ToArray())));
        Assert.Equal("MONITORING_TOO_LARGE", limit.Code);
    }

    [Theory]
    [InlineData("mode", "ANSWER_MODE_UNSUPPORTED")]
    [InlineData("frequency", "FREQUENCY_INVALID")]
    [InlineData("duplicate", "DUPLICATE_TARGET")]
    [InlineData("null", "INVALID_TARGETS")]
    public async Task InvalidDraftsAreRejectedBeforeAnyRead(string kind, string code)
    {
        var service = new PromptMonitoringService(null!, null!, null!, null!);
        var request = kind switch
        {
            "mode" => new MonitoringTargetsEstimateRequest([ChatGpt with { AnswerMode = AnswerMode.Deep }]),
            "frequency" => new MonitoringTargetsEstimateRequest([ChatGpt with { Frequency = (MonitoringFrequency)99 }]),
            "duplicate" => new MonitoringTargetsEstimateRequest([DeepSeek, DeepSeek with { AnswerMode = AnswerMode.Fast }]),
            _ => null!,
        };
        var error = await Assert.ThrowsAsync<CollectorStoreException>(() => service.EstimateTargetsAsync(Guid.NewGuid(), Guid.NewGuid(), request));
        Assert.Equal((code, 400), (error.Code, error.StatusCode));
    }

    [Fact]
    public void ResponseUsesTheFrontendFieldNames()
    {
        var estimate = PromptMonitoringScale.EstimateTargets(Snapshot(1, All()), Cells(Monday, ChatGpt), new HashSet<string>(), BeforeMain, Shanghai, 5m);
        var json = JsonSerializer.SerializeToElement(estimate, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        foreach (var name in new[] { "promptCount", "weeklyChecks", "weeklyCredits", "nextSlotChecks", "nextSlotCredits", "cells",
                     "balance", "shortfall", "blockers", "unitPrice", "sampleCount", "nextSlotAt", "nextSlotKind" })
            Assert.True(json.TryGetProperty(name, out _), name);
        Assert.Equal(13, json.EnumerateObject().Count());
        var cell = json.GetProperty("cells")[0];
        foreach (var name in new[] { "targetKey", "engine", "answerMode", "frequency", "weeklyRuns", "promptCount" })
            Assert.True(cell.TryGetProperty(name, out _), name);
    }

    [Fact]
    public async Task ViewerGetsForbiddenWithoutReachingTheService()
    {
        var service = new StubEstimate();
        var result = await Controller(service, "viewer", out _).Estimate(Guid.NewGuid(), new MonitoringTargetsEstimateRequest([ChatGpt]), default);
        Assert.Equal(403, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        Assert.Null(service.Last);
    }

    [Fact]
    public async Task MemberReceivesTheServiceEstimateAndErrorsKeepTheirStatus()
    {
        var service = new StubEstimate();
        var site = Guid.NewGuid();
        var controller = Controller(service, "member", out var tenant);
        var request = new MonitoringTargetsEstimateRequest([ChatGpt]);
        var ok = Assert.IsType<OkObjectResult>((await controller.Estimate(site, request, default)).Result);
        Assert.IsType<MonitoringTargetsEstimateView>(ok.Value);
        Assert.Equal((tenant, site, request), service.Last);
        service.Failure = new CollectorStoreException("FREQUENCY_INVALID", "频率无效", 400);
        Assert.Equal(400, Assert.IsType<ObjectResult>((await controller.Estimate(site, request, default)).Result).StatusCode);
    }

    private static MonitoringTargetsEstimateController Controller(StubEstimate service, string role, out Guid tenant)
    {
        tenant = Guid.NewGuid();
        var context = new DefaultHttpContext();
        context.Items[SessionAuthenticationMiddleware.ContextItemKey] = new AuthenticatedRequestContext(
            new AuthUserView(Guid.NewGuid(), "qa@example.test", "QA", "active", [new(tenant, "QA", role, "active")]), Guid.NewGuid(), "test-only");
        return new MonitoringTargetsEstimateController(service, new TenantContextResolver(new TestEnvironment()))
            { ControllerContext = new ControllerContext { HttpContext = context } };
    }

    private sealed class StubEstimate : IPromptMonitoringService
    {
        public Task<TeamMonitoringEstimateView> EstimateTeamMonitoringAsync(Guid tenantId, TeamMonitoringEstimateRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public (Guid Tenant, Guid Site, MonitoringTargetsEstimateRequest Request)? Last;
        public CollectorStoreException? Failure;
        public Task<PromptMonitoringView> EnsureAsync(Guid tenantId, Guid siteId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PromptMonitoringView> GetAsync(Guid tenantId, Guid siteId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PromptMonitoringEstimateView> EstimateAsync(Guid tenantId, Guid siteId, PromptMonitoringEstimateRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MonitoringTargetsEstimateView> EstimateTargetsAsync(Guid tenantId, Guid siteId, MonitoringTargetsEstimateRequest request,
            CancellationToken cancellationToken = default)
        {
            Last = (tenantId, siteId, request);
            if (Failure is not null) throw Failure;
            return Task.FromResult(new MonitoringTargetsEstimateView(1, 7m, 70m, 1, 10m, [], 100m, 0m, [], 10m, 1,
                DateTimeOffset.UnixEpoch, MonitoringSlotKind.Main));
        }
        public Task<InstantCollectionView?> CollectNewPromptsAsync(Guid tenantId, Guid siteId, IReadOnlyList<Guid> promptIds,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<InstantCollectionView?> CollectAddedTargetsAsync(Guid tenantId, Guid siteId, PromptView before, PromptView after,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
