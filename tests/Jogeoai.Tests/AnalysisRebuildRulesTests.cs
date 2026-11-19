using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Jogeoai.Tests;

public sealed class AnalysisRebuildRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(60);

    [Fact]
    public void ConfigChangedAndSettledRebuilds()
    {
        var decision = AnalysisRebuildRules.Decide(5, 4, MentionMatcher.Version, MentionMatcher.Version,
            Now.AddSeconds(-60), Now, Settle);
        Assert.Equal(new AnalysisRebuildDecision(true, AnalysisRebuildRules.SiteConfigChanged), decision);
        // 缺少修订时间时视为已过等待时间。
        Assert.True(AnalysisRebuildRules.Decide(5, 4, MentionMatcher.Version, MentionMatcher.Version,
            null, Now, Settle).Rebuild);
    }

    [Fact]
    public void ConfigChangedWithinSettleWaits()
    {
        var decision = AnalysisRebuildRules.Decide(5, 4, MentionMatcher.Version, MentionMatcher.Version,
            Now.AddSeconds(-59), Now, Settle);
        Assert.Equal(new AnalysisRebuildDecision(false, AnalysisRebuildRules.WaitingForSettle), decision);
    }

    [Fact]
    public void ExtractorChangedRebuildsImmediatelyEvenWithinSettle()
    {
        Assert.Equal(new AnalysisRebuildDecision(true, AnalysisRebuildRules.ExtractorVersionChanged),
            AnalysisRebuildRules.Decide(5, 4, "mention-v1", MentionMatcher.Version, Now.AddSeconds(-1), Now, Settle));
        Assert.Equal(new AnalysisRebuildDecision(true, AnalysisRebuildRules.ExtractorVersionChanged),
            AnalysisRebuildRules.Decide(4, 4, "mention-v1", MentionMatcher.Version, Now, Now, Settle));
    }

    [Fact]
    public void EqualVersionsSkip()
    {
        Assert.Equal(new AnalysisRebuildDecision(false, AnalysisRebuildRules.UpToDate),
            AnalysisRebuildRules.Decide(4, 4, MentionMatcher.Version, MentionMatcher.Version,
                Now.AddDays(-1), Now, Settle));
    }

    [Fact]
    public void IdempotencyKeyFormatAndLength()
    {
        var site = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        var key = AnalysisRebuildRules.IdempotencyKey(site, 12, "mention-v2");
        Assert.Equal("auto-rebuild:0f8fad5b-d9cb-469f-a165-70867728950e:cfg12:mention-v2", key);
        // 最大配置版本与 analysis_releases.extractor_version 的列宽（32）下仍不超过 191。
        Assert.True(AnalysisRebuildRules.IdempotencyKey(site, int.MaxValue, new string('x', 32)).Length <= 191);
    }

    [Fact]
    public void OptionsCarryDocumentedDefaults()
    {
        var options = new AnalysisRebuildOptions();
        Assert.True(options.Enabled);
        Assert.Equal(30, options.IntervalSeconds);
        Assert.Equal(60, options.SettleSeconds);
        Assert.Equal(20, options.BatchSize);
    }

    [Fact]
    public async Task WorkerStartsOnlySettledOrExtractorRebuildsAndToleratesConflicts()
    {
        var tenant = Guid.NewGuid();
        var settled = new AnalysisRebuildCandidate(Guid.NewGuid(), tenant, 5, 4, MentionMatcher.Version, Now.AddMinutes(-2));
        var waiting = new AnalysisRebuildCandidate(Guid.NewGuid(), tenant, 7, 6, MentionMatcher.Version, Now.AddSeconds(-10));
        var extractor = new AnalysisRebuildCandidate(Guid.NewGuid(), tenant, 3, 3, "mention-v1", Now);
        var busy = new AnalysisRebuildCandidate(Guid.NewGuid(), tenant, 9, 8, MentionMatcher.Version, null);
        var broken = new AnalysisRebuildCandidate(Guid.NewGuid(), tenant, 2, 1, MentionMatcher.Version, null);
        var store = new FakeAnalysisStore([settled, waiting, extractor, busy, broken]);
        store.Errors[busy.SiteId] = new CollectorStoreException("ANALYSIS_VERSION_NOT_READY", "busy", 409);
        store.Errors[broken.SiteId] = new InvalidOperationException("boom");
        var worker = new AnalysisRebuildWorker(store,
            Options.Create(new AnalysisRebuildOptions { SettleSeconds = 60, BatchSize = 7 }),
            NullLogger<AnalysisRebuildWorker>.Instance);

        var result = await worker.RunPassAsync(Now);

        Assert.Equal(new AnalysisRebuildPassResult(5, 2, 1, 2), result);
        Assert.Equal(7, store.LastLimit);
        Assert.DoesNotContain(store.Calls, c => c.SiteId == waiting.SiteId);
        var started = store.Calls.Where(c => c.SiteId == settled.SiteId || c.SiteId == extractor.SiteId).ToList();
        Assert.Equal(2, started.Count);
        Assert.Contains(started, c => c.Key == AnalysisRebuildRules.IdempotencyKey(settled.SiteId, 5, MentionMatcher.Version));
        Assert.Contains(started, c => c.Key == AnalysisRebuildRules.IdempotencyKey(extractor.SiteId, 3, MentionMatcher.Version));
        Assert.All(store.Calls, c => Assert.Equal(new AnalysisJobRequest(), c.Request));
    }

    [Fact]
    public async Task FailedAutoJobIsNotCountedAsStarted()
    {
        var candidate = new AnalysisRebuildCandidate(Guid.NewGuid(), Guid.NewGuid(), 5, 4, MentionMatcher.Version, null);
        var store = new FakeAnalysisStore([candidate]) { JobStatus = "failed" };
        var worker = new AnalysisRebuildWorker(store, Options.Create(new AnalysisRebuildOptions()),
            NullLogger<AnalysisRebuildWorker>.Instance);

        Assert.Equal(new AnalysisRebuildPassResult(1, 0, 0, 1), await worker.RunPassAsync(Now));
        Assert.Equal(new AnalysisRebuildPassResult(1, 0, 0, 1), await worker.RunPassAsync(Now));
        Assert.Equal(20, store.LastLimit);
    }

    private sealed class FakeAnalysisStore(IReadOnlyList<AnalysisRebuildCandidate> candidates) : IAnalysisStore
    {
        public Dictionary<Guid, Exception> Errors { get; } = [];
        public List<(Guid TenantId, Guid SiteId, string Key, AnalysisJobRequest Request)> Calls { get; } = [];
        public string JobStatus { get; init; } = "pending";
        public int LastLimit { get; private set; }

        public Task<IReadOnlyList<AnalysisRebuildCandidate>> FindSitesNeedingRebuildAsync(int limit,
            CancellationToken cancellationToken = default)
        {
            LastLimit = limit;
            return Task.FromResult(candidates);
        }

        public Task<AnalysisJobView> CreateJobAsync(Guid tenantId, Guid siteId, string idempotencyKey,
            AnalysisJobRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add((tenantId, siteId, idempotencyKey, request));
            if (Errors.TryGetValue(siteId, out var error)) throw error;
            return Task.FromResult(new AnalysisJobView(Guid.NewGuid(), siteId, Guid.NewGuid(), 2, 5, JobStatus,
                10, 0, 0, null, Now, null, request, "site_config_changed"));
        }

        public Task<int> ProcessPendingAsync(string workerId, int limit = 10, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AnalysisJobView?> GetJobAsync(Guid tenantId, Guid siteId, Guid jobId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CheckAnalysisView?> GetCheckAnalysisAsync(Guid tenantId, Guid checkId, Guid? releaseId = null,
            long? analysisWatermark = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
