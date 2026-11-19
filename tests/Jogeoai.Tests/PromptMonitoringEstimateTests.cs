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

/// <summary>预估接口：与每日监控同一检测矩阵、Shortfall 不阻断、规模 / 套餐 / 平台阻断项、viewer 403（无库）。</summary>
public sealed class PromptMonitoringEstimateTests
{
    private static readonly CloroVisibilityTarget ChatGpt = new("cloro", "chatgpt", "en-US", "US", "{}");
    private static readonly CloroVisibilityTarget Gemini = new("cloro", "gemini", "en-US", "US", "{}");
    private static readonly string GeminiKey = CollectionIdentity.TargetKey(Gemini);
    private static readonly CloroVisibilityTarget[] Targets = [ChatGpt, Gemini];

    private static PromptMonitoringScale.Snapshot Snapshot(int allPrompts, int geminiOnlyPrompts, int sampleCount,
        IReadOnlyList<CloroVisibilityTarget>? targets = null)
    {
        var prompts = Enumerable.Range(0, allPrompts).Select(_ => new MonitoringPromptScope(Guid.NewGuid()))
            .Concat(Enumerable.Range(0, geminiOnlyPrompts).Select(_ => new MonitoringPromptScope(Guid.NewGuid(), [GeminiKey])))
            .ToArray();
        return new(prompts, prompts.Length, (targets ?? Targets).OrderBy(CollectionIdentity.TargetKey, StringComparer.Ordinal).ToArray(), sampleCount);
    }

    [Fact]
    public void AllScopeAddsPromptsTimesTargetsTimesSamples()
    {
        // 2 all × 2 targets + 1 Gemini-only × 1, × 2 samples = 10 current checks; 4 new 'all' prompts add 4 × 2 × 2 = 16.
        var estimate = PromptMonitoringScale.Estimate(Snapshot(2, 1, 2), 4, null, 10_000m, null, 3);
        Assert.Equal((3, 10L, 100m), (estimate.CurrentPromptCount, estimate.CurrentChecksPerRound, estimate.CurrentCreditsPerRound));
        Assert.Equal((16L, 160m), (estimate.AddedChecksPerRound, estimate.AddedCreditsPerRound));
        Assert.Equal((26L, 260m), (estimate.TotalChecksPerRound, estimate.TotalCreditsPerRound));
        Assert.Equal((PromptMonitoringPricing.UnitPrice, 2), (estimate.UnitPrice, estimate.SampleCount));
        Assert.Equal(0m, estimate.Shortfall);
        Assert.Empty(estimate.Blockers);
    }

    [Fact]
    public void CustomScopeAddsOnlyTheSelectedPlatformsAndMatchesTheDailyPlanReservation()
    {
        var snapshot = Snapshot(2, 1, 2);
        var estimate = PromptMonitoringScale.Estimate(snapshot, 4, [GeminiKey], 10_000m, null, 3);
        Assert.Equal(8L, estimate.AddedChecksPerRound);
        // What the daily plan freezes for the same prompts (current + new) and reserves per round.
        var all = PromptMonitoringScale.WithAdded(snapshot, 4, [GeminiKey]);
        var plan = MonitoringMatrix.BuildSelection(all, snapshot.Targets, snapshot.SampleCount);
        Assert.Equal(MonitoringMatrix.CountChecks(plan), estimate.TotalChecksPerRound);
        Assert.Equal(PromptMonitoringPricing.Estimate(MonitoringMatrix.CountChecks(plan)), estimate.TotalCreditsPerRound);
        Assert.Equal(18L, estimate.TotalChecksPerRound);
    }

    [Fact]
    public void ShortfallIsReportedButNeverBlocks()
    {
        var estimate = PromptMonitoringScale.Estimate(Snapshot(2, 1, 2), 4, null, 100m, null, 3);
        Assert.Equal(100m, estimate.AvailableCredits);
        Assert.Equal(160m, estimate.Shortfall);
        Assert.Empty(estimate.Blockers);
    }

    [Fact]
    public void MoreThanTwoHundredPromptsPerBrandBlocks()
    {
        var snapshot = Snapshot(199, 0, 1, [ChatGpt]);
        Assert.Empty(PromptMonitoringScale.Estimate(snapshot, 1, null, 0m, null, 199).Blockers);
        Assert.Equal(["MONITORING_TOO_LARGE"], PromptMonitoringScale.Estimate(snapshot, 2, null, 0m, null, 199).Blockers);
    }

    [Fact]
    public void MoreThanOneThousandChecksPerRoundBlocksButNarrowScopesFit()
    {
        // 1 + 49 prompts × 2 targets × 10 samples = 1000 fits; the 50th new 'all' prompt makes 1020.
        var snapshot = Snapshot(1, 0, 10);
        Assert.Empty(PromptMonitoringScale.Estimate(snapshot, 49, null, 0m, null, 1).Blockers);
        var tooLarge = PromptMonitoringScale.Estimate(snapshot, 50, null, 0m, null, 1);
        Assert.Equal(1020L, tooLarge.TotalChecksPerRound);
        Assert.Equal(["MONITORING_TOO_LARGE"], tooLarge.Blockers);
        // The same 50 prompts on one platform only: 20 + 500 checks.
        Assert.Empty(PromptMonitoringScale.Estimate(snapshot, 50, [GeminiKey], 0m, null, 1).Blockers);
    }

    [Fact]
    public void PlanLimitBlocksOnlyWhenTheTenantWouldExceedIt()
    {
        var snapshot = Snapshot(2, 0, 1);
        Assert.Empty(PromptMonitoringScale.Estimate(snapshot, 1, null, 0m, 20, 19).Blockers);
        Assert.Equal(["PLAN_LIMIT_EXCEEDED"], PromptMonitoringScale.Estimate(snapshot, 2, null, 0m, 20, 19).Blockers);
        Assert.Empty(PromptMonitoringScale.Estimate(snapshot, 150, null, 0m, null, 5000).Blockers);
        // Nothing to create: limits that are already reached do not block an empty selection.
        Assert.Empty(PromptMonitoringScale.Estimate(snapshot, 0, null, 0m, 20, 25).Blockers);
    }

    [Fact]
    public void MissingPlatformsBlock()
    {
        var noTargets = PromptMonitoringScale.Estimate(Snapshot(2, 0, 1, []), 3, null, 0m, null, 2);
        Assert.Equal(["MONITORING_TARGETS_REQUIRED"], noTargets.Blockers);
        Assert.Equal(0L, noTargets.TotalChecksPerRound);
        var emptyScope = PromptMonitoringScale.Estimate(Snapshot(2, 0, 1), 3, [], 0m, null, 2);
        Assert.Equal(["MONITORING_TARGETS_REQUIRED"], emptyScope.Blockers);
        Assert.Equal(0L, emptyScope.AddedChecksPerRound);
    }

    [Fact]
    public void ResponseUsesTheFrontendFieldNames()
    {
        var json = JsonSerializer.SerializeToElement(PromptMonitoringScale.Estimate(Snapshot(1, 0, 1), 1, null, 5m, null, 1),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        foreach (var name in new[] { "unitPrice", "sampleCount", "currentPromptCount", "currentChecksPerRound", "currentCreditsPerRound",
                     "addedChecksPerRound", "addedCreditsPerRound", "totalChecksPerRound", "totalCreditsPerRound", "availableCredits",
                     "shortfall", "blockers" })
            Assert.True(json.TryGetProperty(name, out _), name);
        Assert.Equal(12, json.EnumerateObject().Count());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(201)]
    public async Task NewPromptCountOutsideZeroToTwoHundredIsRejected(int count)
    {
        var service = new PromptMonitoringService(null!, null!, null!, null!);
        var error = await Assert.ThrowsAsync<CollectorStoreException>(() =>
            service.EstimateAsync(Guid.NewGuid(), Guid.NewGuid(), new PromptMonitoringEstimateRequest(count)));
        Assert.Equal(("INVALID_ESTIMATE", 400), (error.Code, error.StatusCode));
    }

    [Fact]
    public async Task ViewerGetsForbiddenWithoutReachingTheService()
    {
        var service = new StubEstimate();
        var result = await Controller(service, "viewer", out _).Estimate(Guid.NewGuid(), new PromptMonitoringEstimateRequest(3), default);
        Assert.Equal(403, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        Assert.Null(service.Last);
    }

    [Fact]
    public async Task MemberReceivesTheServiceEstimateAndErrorsKeepTheirStatus()
    {
        var service = new StubEstimate();
        var site = Guid.NewGuid();
        var controller = Controller(service, "member", out var tenant);
        var request = new PromptMonitoringEstimateRequest(3, ["k"]);
        var ok = Assert.IsType<OkObjectResult>((await controller.Estimate(site, request, default)).Result);
        Assert.IsType<PromptMonitoringEstimateView>(ok.Value);
        Assert.Equal((tenant, site, request), service.Last);
        service.Failure = new CollectorStoreException("PROMPT_TARGET_INVALID", "平台无效", 400);
        Assert.Equal(400, Assert.IsType<ObjectResult>((await controller.Estimate(site, request, default)).Result).StatusCode);
    }

    private static PromptMonitoringController Controller(StubEstimate service, string role, out Guid tenant)
    {
        tenant = Guid.NewGuid();
        var context = new DefaultHttpContext();
        context.Items[SessionAuthenticationMiddleware.ContextItemKey] = new AuthenticatedRequestContext(
            new AuthUserView(Guid.NewGuid(), "qa@example.test", "QA", "active", [new(tenant, "QA", role, "active")]), Guid.NewGuid(), "test-only");
        return new PromptMonitoringController(service, new TenantContextResolver(new TestEnvironment()))
            { ControllerContext = new ControllerContext { HttpContext = context } };
    }

    private sealed class StubEstimate : IPromptMonitoringService
    {
        public Task<TeamMonitoringEstimateView> EstimateTeamMonitoringAsync(Guid tenantId, TeamMonitoringEstimateRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public (Guid Tenant, Guid Site, PromptMonitoringEstimateRequest Request)? Last;
        public CollectorStoreException? Failure;
        public Task<PromptMonitoringView> EnsureAsync(Guid tenantId, Guid siteId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PromptMonitoringView> GetAsync(Guid tenantId, Guid siteId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PromptMonitoringEstimateView> EstimateAsync(Guid tenantId, Guid siteId, PromptMonitoringEstimateRequest request,
            CancellationToken cancellationToken = default)
        {
            Last = (tenantId, siteId, request);
            if (Failure is not null) throw Failure;
            return Task.FromResult(new PromptMonitoringEstimateView(10m, 1, 0, 0, 0, 3, 30, 3, 30, 100, 0, []));
        }
        public Task<MonitoringTargetsEstimateView> EstimateTargetsAsync(Guid tenantId, Guid siteId, MonitoringTargetsEstimateRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
