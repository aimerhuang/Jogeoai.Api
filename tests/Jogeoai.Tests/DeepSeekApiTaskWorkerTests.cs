using System.Text.Json;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Providers.Abstractions;
using Jogeoai.Providers.Domestic;
using Jogeoai.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Jogeoai.Tests;

/// <summary>DeepSeek Worker 复用 SynchronousAnswerTaskWorker：同步完成、重试、余额不足延后与终态。</summary>
public sealed class DeepSeekApiTaskWorkerTests
{
    [Fact]
    public async Task SuccessfulCallCompletesSynchronouslyWithDeepSeekSnapshotAndResponseId()
    {
        var store = new FakeStore(Lease());
        var client = new FakeClient();
        await ProcessAsync(store, client);
        Assert.Equal(1, client.Calls);
        Assert.True(store.Submitting);
        Assert.Equal(store.LeasedTask.TaskId, client.LastRequest?.ProviderTaskId);
        Assert.Equal(("市政管道检测有哪些厂家？", "zh-CN", "CN"), (client.LastRequest?.Prompt, client.LastRequest?.Locale, client.LastRequest?.Country));
        Assert.Equal(("deepseek_api", "deepseek", "api", "api_response"), (store.Completed?.Answer.Provider, store.Completed?.Answer.Engine,
            store.Completed?.Answer.CaptureMode, store.Completed?.Answer.EvidenceClass));
        Assert.Equal("8f7780e8-a960-4d6e-9e2a-409868bdfe9c", store.Completed?.ExternalId);
        Assert.Equal("deepseek_api", store.LeasedProvider);
        Assert.Equal("deepseek_api", store.RecoveredProvider);
        Assert.Null(store.Failure);
        Assert.Null(store.Deferred);
    }

    [Theory]
    [InlineData("fast")]
    [InlineData("deep")]
    public async Task LeaseAnswerModeReachesTheClientRequest(string mode)
    {
        var store = new FakeStore(Lease() with { AnswerMode = mode });
        var client = new FakeClient();
        await ProcessAsync(store, client);
        Assert.Equal(mode, client.LastRequest?.AnswerMode);
        Assert.NotNull(store.Completed);
    }

    [Fact]
    public async Task MissingResponseIdFallsBackToTheLocalTaskId()
    {
        var store = new FakeStore(Lease());
        await ProcessAsync(store, new FakeClient { ResponseId = null });
        Assert.Equal($"local:{store.LeasedTask.TaskId:N}", store.Completed?.ExternalId);
    }

    [Theory]
    [InlineData(1, true, 30)]
    [InlineData(2, true, 60)]
    [InlineData(3, true, 120)]
    [InlineData(4, true, 120)]
    [InlineData(5, false, 120)]
    public async Task RateLimitRetriesWithGrowingBackoffUntilTheAttemptLimit(int attempt, bool retryable, int backoffSeconds)
    {
        var store = new FakeStore(Lease() with { AttemptCount = attempt });
        await ProcessAsync(store, new FakeClient { Failure = new DeepSeekProviderException(DeepSeekErrorCodes.RateLimited, "limited", true) });
        Assert.Equal(DeepSeekErrorCodes.RateLimited, store.Failure);
        Assert.Equal(retryable, store.Retryable);
        Assert.Equal(TimeSpan.FromSeconds(backoffSeconds), store.RetryAfter);
        Assert.Null(store.Completed);
    }

    [Theory]
    [InlineData(DeepSeekErrorCodes.Timeout, 1, true)]
    [InlineData(DeepSeekErrorCodes.Timeout, 2, true)]
    [InlineData(DeepSeekErrorCodes.Timeout, 3, false)]
    [InlineData(DeepSeekErrorCodes.RequestFailed, 1, true)]
    [InlineData(DeepSeekErrorCodes.RequestFailed, 3, false)]
    public async Task TransientFailuresRetryWithTheDefaultDelayUntilTheAttemptLimit(string code, int attempt, bool retryable)
    {
        var store = new FakeStore(Lease() with { AttemptCount = attempt });
        await ProcessAsync(store, new FakeClient { Failure = new DeepSeekProviderException(code, "transient", true) });
        Assert.Equal(code, store.Failure);
        Assert.Equal(retryable, store.Retryable);
        Assert.Null(store.RetryAfter);
    }

    [Theory]
    [InlineData(DeepSeekErrorCodes.AuthFailed)]
    [InlineData(DeepSeekErrorCodes.NotConfigured)]
    [InlineData(DeepSeekErrorCodes.InvalidRequest)]
    [InlineData(DeepSeekErrorCodes.InvalidResponse)]
    public async Task TerminalProviderErrorsFailWithoutRetry(string code)
    {
        var store = new FakeStore(Lease());
        await ProcessAsync(store, new FakeClient { Failure = new DeepSeekProviderException(code, "terminal", false) });
        Assert.Equal(code, store.Failure);
        Assert.False(store.Retryable);
        Assert.Null(store.Deferred);
        Assert.Null(store.Completed);
    }

    [Fact]
    public async Task InsufficientBalanceDefersTenMinutesAndKeepsTheReservation()
    {
        var store = new FakeStore(Lease());
        var before = DateTimeOffset.UtcNow;
        await ProcessAsync(store, new FakeClient { Failure = new DeepSeekProviderException(DeepSeekErrorCodes.InsufficientBalance, "balance", true, 10) });
        Assert.Null(store.Failure);
        Assert.Equal(DeepSeekErrorCodes.InsufficientBalance, store.Deferred?.Code);
        Assert.True(store.Deferred?.CreditsExhausted);
        Assert.InRange(store.Deferred!.Value.Next, before.AddMinutes(10), DateTimeOffset.UtcNow.AddMinutes(10).AddSeconds(1));
    }

    [Fact]
    public async Task InsufficientBalanceBeyondMaximumWaitFailsTerminallySoTheReservationIsReleased()
    {
        var store = new FakeStore(Lease() with { PoolExhaustedSince = DateTimeOffset.UtcNow.AddHours(-2).AddMinutes(-1) });
        await ProcessAsync(store, new FakeClient { Failure = new DeepSeekProviderException(DeepSeekErrorCodes.InsufficientBalance, "balance", true, 10) });
        Assert.Null(store.Deferred);
        Assert.Equal(DeepSeekErrorCodes.InsufficientBalance, store.Failure);
        Assert.False(store.Retryable);
    }

    [Fact]
    public async Task StaleLeaseIsIgnoredWithoutFailingTheTask()
    {
        var store = new FakeStore(Lease()) { CompletionFailure = new CollectorStoreException("STALE_LEASE", "fenced", 409) };
        await ProcessAsync(store, new FakeClient());
        Assert.Null(store.Failure);
        Assert.Null(store.Deferred);
    }

    [Fact]
    public async Task UnexpectedExceptionIsRecordedAsRetryableDeepSeekRequestFailure()
    {
        var store = new FakeStore(Lease());
        await ProcessAsync(store, new FakeClient { Failure = new InvalidOperationException("boom") });
        Assert.Equal(DeepSeekErrorCodes.RequestFailed, store.Failure);
        Assert.True(store.Retryable);
    }

    [Fact]
    public void WorkerOptionsShareTheDoubaoDefaults()
    {
        var deepSeek = new DeepSeekWorkerOptions();
        var doubao = new DoubaoWorkerOptions();
        Assert.Equal((true, 2, 2, 300, 3, 5, TimeSpan.FromHours(2)), (deepSeek.Enabled, deepSeek.BatchSize, deepSeek.MaxConcurrency,
            deepSeek.LeaseSeconds, deepSeek.MaxRetryAttempts, deepSeek.RateLimitMaxAttempts, deepSeek.MaxDeferWait));
        Assert.Equal(doubao.RateLimitBackoff, deepSeek.RateLimitBackoff);
        Assert.IsAssignableFrom<ApiAnswerWorkerOptions>(doubao);
    }

    private static CloroTaskLease Lease()
    {
        var now = DateTimeOffset.UtcNow;
        return new CloroTaskLease(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "市政管道检测有哪些厂家？", "deepseek", "zh-CN", "CN", CloroTaskStates.Leased, null,
            1, now.AddMinutes(5), 1, now.AddMinutes(-1), null, null);
    }

    private static async Task ProcessAsync(FakeStore store, FakeClient client)
    {
        using var worker = new DeepSeekApiTaskWorker(NullLogger<DeepSeekApiTaskWorker>.Instance, store, client,
            Options.Create(new DeepSeekWorkerOptions()));
        await worker.StartAsync(CancellationToken.None);
        try { await store.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    private sealed class FakeClient : IDeepSeekAnswerClient
    {
        public int Calls { get; private set; }
        public DeepSeekAnswerRequest? LastRequest { get; private set; }
        public Exception? Failure { get; init; }
        public string? ResponseId { get; init; } = "8f7780e8-a960-4d6e-9e2a-409868bdfe9c";

        public Task<CloroAnswerSnapshot> AskAsync(DeepSeekAnswerRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastRequest = request;
            if (Failure is not null) throw Failure;
            var raw = ResponseId is null
                ? JsonSerializer.SerializeToElement(new { model = "deepseek-flash", content = Array.Empty<object>() })
                : JsonSerializer.SerializeToElement(new { id = ResponseId, model = "deepseek-flash", content = Array.Empty<object>(),
                    usage = new { input_tokens = 12, output_tokens = 34, server_tool_use = new { web_search_requests = 3 } } });
            var definition = AnswerProviderDefinition.DeepSeekApi;
            return Task.FromResult(new CloroAnswerSnapshot(definition.Provider, "deepseek", definition.CaptureMode, definition.EvidenceClass,
                "deepseek-flash", "DeepSeek 回答", true, [], ["管道检测 厂家"], raw, DateTimeOffset.UtcNow,
                ParserVersion: definition.ParserVersion, QueryAvailability: "available"));
        }
    }

    // Shared with PackyApiTaskWorkerTests (Packy leases 600 seconds by default).
    internal sealed class FakeStore(CloroTaskLease task) : ICloroVisibilityStore
    {
        private int leases;
        public int ExpectedLeaseSeconds { get; init; } = 300;
        public CloroTaskLease LeasedTask => task;
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? LeasedProvider { get; private set; }
        public string? RecoveredProvider { get; private set; }
        public bool Submitting { get; private set; }
        public (CloroAnswerSnapshot Answer, long DurationMs, string ExternalId)? Completed { get; private set; }
        public string? Failure { get; private set; }
        public bool Retryable { get; private set; }
        public TimeSpan? RetryAfter { get; private set; }
        public (string Code, DateTimeOffset Next, bool CreditsExhausted)? Deferred { get; private set; }
        public Exception? CompletionFailure { get; init; }

        public Task<VisibilityRunCreated> CreateRunAsync(CreateCloroVisibilityRunCommand command, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<Guid?> GetExistingRunByKeyAsync(CreateCloroVisibilityRunCommand expected, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<VisibilityRunSummary?> GetRunAsync(Guid tenantId, Guid runId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<string?> GetRunProviderAsync(Guid tenantId, Guid runId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<CursorPage<CloroVisibilityCheckView>> GetChecksAsync(Guid tenantId, Guid runId, string? cursor, int limit,
            string? engine, string? status, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CloroVisibilityCheckView?> GetCheckAsync(Guid tenantId, Guid checkId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<CloroTaskLease>> LeaseTasksAsync(string workerId, int limit = 1, int leaseSeconds = 90,
            string provider = "cloro", CancellationToken cancellationToken = default)
        {
            LeasedProvider = provider;
            Assert.Equal(2, limit);
            Assert.Equal(ExpectedLeaseSeconds, leaseSeconds);
            if (Interlocked.Exchange(ref leases, 1) == 0) return Task.FromResult<IReadOnlyList<CloroTaskLease>>([task]);
            return Task.FromResult<IReadOnlyList<CloroTaskLease>>([]);
        }
        public Task MarkSubmittingAsync(CloroTaskLease task, string workerId, CancellationToken cancellationToken = default)
        {
            Submitting = true;
            return Task.CompletedTask;
        }
        public Task RecordSubmissionAsync(CloroTaskLease task, string workerId, CloroSubmittedTask submitted,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RecordPollAsync(CloroTaskLease task, string workerId, CloroTaskStatusSnapshot snapshot, TimeSpan delay,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CompleteTaskAsync(CloroTaskLease task, string workerId, CloroAnswerSnapshot answer, int durationMs,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CompleteSynchronousTaskAsync(CloroTaskLease task, string workerId, CloroAnswerSnapshot answer, long durationMs,
            string externalId, CancellationToken cancellationToken = default)
        {
            if (CompletionFailure is not null)
            {
                Finished.TrySetResult();
                throw CompletionFailure;
            }
            Completed = (answer, durationMs, externalId);
            Finished.TrySetResult();
            return Task.CompletedTask;
        }
        public Task FailTaskAsync(CloroTaskLease task, string workerId, string errorCode, bool retryable, TimeSpan? retryAfter = null,
            CancellationToken cancellationToken = default)
        {
            Failure = errorCode;
            Retryable = retryable;
            RetryAfter = retryAfter;
            Finished.TrySetResult();
            return Task.CompletedTask;
        }
        public Task DeferTaskAsync(CloroTaskLease task, string workerId, string errorCode, DateTimeOffset nextAttemptAt,
            bool creditsExhausted, CancellationToken cancellationToken = default)
        {
            Deferred = (errorCode, nextAttemptAt, creditsExhausted);
            Finished.TrySetResult();
            return Task.CompletedTask;
        }
        public Task<bool> ResetForResubmissionAsync(CloroTaskLease task, string workerId, int maxResubmits,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> RecoverExpiredTasksAsync(string provider = "cloro", CancellationToken cancellationToken = default)
        {
            RecoveredProvider = provider;
            return Task.FromResult(0);
        }
        public Task<int> RecoverTimedOutTasksAsync(Guid runId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
