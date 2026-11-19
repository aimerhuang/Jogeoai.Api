using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Jogeoai.Tests;

/// <summary>情感 Worker：未提及短路、Provider 未配置、复用、重试与耗尽、配置无效释放；全部使用测试替身。</summary>
public sealed class ReputationWorkerTests
{
    private static readonly ResearchProviderConfiguration Configuration =
        new("openai-compatible", "m", "https://llm.test/v1", "token", false, 30, 1);

    private static ReputationWorkLease Lease(bool mentioned, int attempt = 1) => new(Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), Guid.NewGuid(), attempt, attempt, Guid.NewGuid(), "Acme", [], [], "Acme 很好。", mentioned, []);

    private static ReputationSentimentWorker Worker(FakeStore store, FakeClassifier classifier,
        ResearchProviderConfiguration? configuration = null, int maxAttempts = 3, bool configured = true)
        => new(store, classifier, new FakeSettings(configured ? configuration ?? Configuration : null),
            Options.Create(new ReputationSentimentWorkerOptions { MaxAttempts = maxAttempts, BatchSize = 10 }),
            NullLogger<ReputationSentimentWorker>.Instance);

    [Fact]
    public async Task NotMentionedIsShortCircuitedWithoutCallingTheClassifier()
    {
        var store = new FakeStore(Lease(false));
        var classifier = new FakeClassifier();
        var result = await Worker(store, classifier).RunPassAsync();

        Assert.Equal(1, result.NotMentioned);
        Assert.Equal(0, classifier.Calls);
        var completed = Assert.Single(store.Completed);
        Assert.Equal(ReputationSentimentState.NotMentioned, completed.State);
        Assert.Equal(ReputationRules.NotMentionedCode, completed.RationaleCode);
    }

    [Fact]
    public async Task ProviderNotConfiguredClaimsOnlyNotMentionedWork()
    {
        var store = new FakeStore(Lease(false));
        var result = await Worker(store, new FakeClassifier(), configured: false).RunPassAsync();
        Assert.False(result.ProviderConfigured);
        Assert.All(store.ClaimIncludeMentioned, include => Assert.False(include));
        Assert.Equal(1, result.NotMentioned);
    }

    [Fact]
    public async Task MentionedAnswerIsClassifiedAndPersisted()
    {
        var store = new FakeStore(Lease(true));
        var classifier = new FakeClassifier
        {
            Result = new(ReputationSentimentState.Positive, 0.9, "PRAISE", 0, 4, [], ReputationRules.ClassifierVersion, "gpt-6-luna")
        };
        var result = await Worker(store, classifier).RunPassAsync();
        Assert.Equal(1, result.Classified);
        Assert.Equal(1, classifier.Calls);
        Assert.Equal(ReputationSentimentState.Positive, Assert.Single(store.Completed).State);
        Assert.Equal(64, Assert.Single(store.CompletedHashes).Length);
    }

    [Fact]
    public async Task ReusedJudgementSkipsTheClassifier()
    {
        var store = new FakeStore(Lease(true)) { Reuse = true };
        var classifier = new FakeClassifier();
        var result = await Worker(store, classifier).RunPassAsync();
        Assert.Equal(1, result.Reused);
        Assert.Equal(0, classifier.Calls);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public async Task RetryableFailuresExhaustAfterMaxAttempts(int attempt, bool terminal)
    {
        var store = new FakeStore(Lease(true, attempt));
        var classifier = new FakeClassifier
        {
            Error = new ReputationClassifierException(ReputationClassifierException.Retryable, "busy", true)
        };
        var result = await Worker(store, classifier).RunPassAsync();
        var failure = Assert.Single(store.Failures);
        Assert.Equal(terminal, failure.Terminal);
        Assert.Equal(ReputationClassifierException.Retryable, failure.Code);
        Assert.Equal(terminal ? 1 : 0, result.Failed);
        Assert.Equal(terminal ? 0 : 1, result.Retried);
        Assert.Empty(store.Completed);
    }

    [Fact]
    public async Task NonRetryableRejectionIsTerminalImmediately()
    {
        var store = new FakeStore(Lease(true, 1));
        var classifier = new FakeClassifier
        {
            Error = new ReputationClassifierException(ReputationClassifierException.Rejected, "rejected", false)
        };
        await Worker(store, classifier).RunPassAsync();
        Assert.True(Assert.Single(store.Failures).Terminal);
    }

    [Fact]
    public async Task InvalidProviderConfigurationReleasesWorkWithoutConsumingAttempts()
    {
        var store = new FakeStore(Lease(true), Lease(true), Lease(false));
        var classifier = new FakeClassifier
        {
            Error = new ReputationClassifierException(ReputationClassifierException.ProviderConfigInvalid, "invalid", false)
        };
        var result = await Worker(store, classifier).RunPassAsync();
        Assert.Equal(1, classifier.Calls);
        Assert.Equal(ReputationClassifierException.ProviderConfigInvalid, Assert.Single(store.Released));
        Assert.Empty(store.Failures);
        // 第一条释放后，本轮只再领取未提及的工作项。
        Assert.Equal(new[] { true, false, false }, store.ClaimIncludeMentioned.Take(3));
        Assert.Equal(1, result.Released);
    }

    [Fact]
    public async Task RejectedProviderCredentialsBackOffAcrossPasses()
    {
        var store = new FakeStore(Lease(true));
        var classifier = new FakeClassifier
        {
            Error = new ReputationClassifierException(ReputationClassifierException.ProviderConfigInvalid, "invalid", false)
        };
        var worker = Worker(store, classifier);
        await worker.RunPassAsync();
        var claimsBefore = store.ClaimIncludeMentioned.Count;
        var second = await worker.RunPassAsync();
        // 退避期内下一轮不再领取已提及的工作项，也不再调用网关；Provider 仍报告为已配置。
        Assert.Equal(1, classifier.Calls);
        Assert.False(store.ClaimIncludeMentioned[claimsBefore]);
        Assert.True(second.ProviderConfigured);
    }

    [Fact]
    public async Task UnexpectedExceptionIsRecordedAsRetry()
    {
        var store = new FakeStore(Lease(true));
        var classifier = new FakeClassifier { Unexpected = new InvalidOperationException("boom") };
        var result = await Worker(store, classifier).RunPassAsync();
        Assert.Equal("REPUTATION_ERROR", Assert.Single(store.Failures).Code);
        Assert.Equal(1, result.Retried);
    }

    [Fact]
    public async Task MissingSchemaKeepsTheWorkerIdle()
    {
        var store = new FakeStore(Lease(false)) { SchemaReady = false };
        var result = await Worker(store, new FakeClassifier()).RunPassAsync();
        Assert.Equal(0, result.Completed);
        Assert.Empty(store.ClaimIncludeMentioned);
    }

    [Fact]
    public void WorkerIsDisabledByDefault() => Assert.False(new ReputationSentimentWorkerOptions().Enabled);

    [Fact]
    public async Task ExhaustedLeasesAreExpiredEvenWithoutAutoEnqueue()
    {
        // 默认不自动登记：仍须收尾最后一次尝试中断的工作项，否则它们永远停在 processing、显示为待判定。
        var store = new FakeStore();
        await Worker(store, new FakeClassifier(), maxAttempts: 2).RunPassAsync();
        Assert.Equal(new[] { 2 }, store.ExpireMaxAttempts);
        Assert.Empty(store.EnqueueMaxAttempts);

        var enqueuing = new FakeStore();
        await new ReputationSentimentWorker(enqueuing, new FakeClassifier(), new FakeSettings(Configuration),
            Options.Create(new ReputationSentimentWorkerOptions { MaxAttempts = 2, AutoEnqueueSinceUtc = DateTimeOffset.UnixEpoch }),
            NullLogger<ReputationSentimentWorker>.Instance).RunPassAsync();
        Assert.Equal(new[] { 2 }, enqueuing.EnqueueMaxAttempts);
        Assert.Empty(enqueuing.ExpireMaxAttempts);
    }

    private sealed class FakeSettings(ResearchProviderConfiguration? configuration) : IReputationClassifierSettings
    {
        public Task<ResearchProviderConfiguration?> GetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(configuration);
    }

    private sealed class FakeClassifier : IReputationSentimentClassifier
    {
        public int Calls { get; private set; }
        public ReputationClassification Result { get; init; } = new(ReputationSentimentState.Neutral, 0.8, "FACTUAL_DESCRIPTION",
            null, null, [], ReputationRules.ClassifierVersion, "gpt-6-luna");
        public ReputationClassifierException? Error { get; init; }
        public Exception? Unexpected { get; init; }
        public string ModelName => "gpt-6-luna";

        public Task<ReputationClassification> ClassifyAsync(ReputationClassificationInput input,
            ResearchProviderConfiguration configuration, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Error is not null) throw Error;
            if (Unexpected is not null) throw Unexpected;
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeStore(params ReputationWorkLease[] leases) : IReputationStore
    {
        private readonly Queue<ReputationWorkLease> queue = new(leases);
        public bool SchemaReady { get; init; } = true;
        public bool Reuse { get; init; }
        public List<bool> ClaimIncludeMentioned { get; } = [];
        public List<ReputationClassification> Completed { get; } = [];
        public List<string> CompletedHashes { get; } = [];
        public List<string> Released { get; } = [];
        public List<(string Code, bool Terminal)> Failures { get; } = [];

        public Task<bool> SchemaReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(SchemaReady);
        public List<int> EnqueueMaxAttempts { get; } = [];
        public List<int> ExpireMaxAttempts { get; } = [];

        public Task<int> EnqueueRecentAsync(DateTimeOffset completedSince, int limit, int maxAttempts,
            CancellationToken cancellationToken = default)
        {
            EnqueueMaxAttempts.Add(maxAttempts);
            return Task.FromResult(0);
        }

        public Task<int> ExpireExhaustedAsync(int maxAttempts, CancellationToken cancellationToken = default)
        {
            ExpireMaxAttempts.Add(maxAttempts);
            return Task.FromResult(0);
        }

        public Task<ReputationWorkLease?> ClaimAsync(string workerId, bool includeMentioned, TimeSpan lease, int maxAttempts,
            CancellationToken cancellationToken = default)
        {
            ClaimIncludeMentioned.Add(includeMentioned);
            while (queue.Count > 0)
            {
                var next = queue.Dequeue();
                if (includeMentioned || !next.BrandMentioned) return Task.FromResult<ReputationWorkLease?>(next);
            }
            return Task.FromResult<ReputationWorkLease?>(null);
        }

        public Task<bool> TryReuseAsync(ReputationWorkLease lease, string workerId, string inputHash,
            CancellationToken cancellationToken = default) => Task.FromResult(Reuse);

        public Task<bool> CompleteAsync(ReputationWorkLease lease, string workerId, ReputationClassification classification,
            string inputHash, CancellationToken cancellationToken = default)
        {
            Completed.Add(classification);
            CompletedHashes.Add(inputHash);
            return Task.FromResult(true);
        }

        public Task ReleaseAsync(ReputationWorkLease lease, string workerId, string errorCode,
            CancellationToken cancellationToken = default)
        {
            Released.Add(errorCode);
            return Task.CompletedTask;
        }

        public Task FailAsync(ReputationWorkLease lease, string workerId, string errorCode, bool terminal,
            CancellationToken cancellationToken = default)
        {
            Failures.Add((errorCode, terminal));
            return Task.CompletedTask;
        }

        public Task<ReputationJobView> CreateJobAsync(Guid tenantId, Guid siteId, string idempotencyKey,
            ReputationJobRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ReputationJobView?> GetJobAsync(Guid tenantId, Guid siteId, Guid jobId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
