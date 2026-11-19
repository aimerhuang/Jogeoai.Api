using System.Text.Json;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Providers.Abstractions;
using Jogeoai.Providers.Cloro;
using Jogeoai.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Jogeoai.Tests;

public sealed class CloroTaskWorkerTests
{
    [Theory]
    [InlineData("chatgpt", "COMPLETED", 120, null)]
    [InlineData("chatgpt", "SUCCESS", 120, 110)]
    [InlineData("chatgpt", "COMPLETED", 20, 15)]
    [InlineData("gemini", "COMPLETED", 120, null)]
    [InlineData("gemini", "SUCCESS", 120, 110)]
    [InlineData("gemini", "COMPLETED", 20, 15)]
    [InlineData("copilot", "COMPLETED", 120, null)]
    [InlineData("copilot", "SUCCESS", 120, 110)]
    [InlineData("copilot", "COMPLETED", 20, 15)]
    [InlineData("perplexity", "COMPLETED", 120, null)]
    [InlineData("perplexity", "SUCCESS", 120, 110)]
    [InlineData("perplexity", "COMPLETED", 20, 15)]
    [InlineData("google-ai-overview", "COMPLETED", 120, null)]
    [InlineData("google-ai-overview", "SUCCESS", 120, 110)]
    [InlineData("google-ai-overview", "COMPLETED", 20, 15)]
    public async Task AcceptsExistingProviderResultAfterLocalDeadline(string engine, string status, int submittedMinutes, int? startedMinutes)
    {
        var store = new FakeStore(Lease(submittedMinutes, startedMinutes, engine));
        var client = new FakeClient { Status = status };
        await ProcessAsync(store, client);
        Assert.Equal(1, client.GetCalls);
        Assert.Equal(0, client.SubmitCalls);
        Assert.Equal("answer", store.Answer?.AnswerText);
        Assert.Equal(engine, store.Answer?.Engine);
        Assert.Equal("external-task", client.LastTaskId);
        Assert.Null(store.Failure);
        Assert.Null(store.Poll);
    }

    [Theory]
    [InlineData("chatgpt", "QUEUED", 120, null, "CLORO_POLL_TIMEOUT")]
    [InlineData("chatgpt", "PROCESSING", 20, 15, "CLORO_GENERATION_TIMEOUT")]
    [InlineData("gemini", "QUEUED", 120, null, "CLORO_POLL_TIMEOUT")]
    [InlineData("gemini", "PROCESSING", 20, 15, "CLORO_GENERATION_TIMEOUT")]
    [InlineData("copilot", "QUEUED", 120, null, "CLORO_POLL_TIMEOUT")]
    [InlineData("copilot", "PROCESSING", 20, 15, "CLORO_GENERATION_TIMEOUT")]
    [InlineData("perplexity", "QUEUED", 120, null, "CLORO_POLL_TIMEOUT")]
    [InlineData("perplexity", "PROCESSING", 20, 15, "CLORO_GENERATION_TIMEOUT")]
    [InlineData("google-ai-overview", "QUEUED", 120, null, "CLORO_POLL_TIMEOUT")]
    [InlineData("google-ai-overview", "PROCESSING", 20, 15, "CLORO_GENERATION_TIMEOUT")]
    public async Task UnfinishedProviderTaskStillTerminatesAfterDeadline(string engine, string status, int submittedMinutes,
        int? startedMinutes, string code)
    {
        var store = new FakeStore(Lease(submittedMinutes, startedMinutes, engine));
        var client = new FakeClient { Status = status };
        await ProcessAsync(store, client);
        Assert.Equal(1, client.GetCalls);
        Assert.Equal(0, client.SubmitCalls);
        Assert.Equal(code, store.Failure);
        Assert.False(store.Retryable);
        Assert.Null(store.Answer);
        Assert.Null(store.Poll);
    }

    [Theory]
    [InlineData("chatgpt")]
    [InlineData("gemini")]
    [InlineData("copilot")]
    [InlineData("perplexity")]
    [InlineData("google-ai-overview")]
    public async Task ActualProviderFailureIsPreservedEvenAfterLocalDeadline(string engine)
    {
        var store = new FakeStore(Lease(120, 110, engine));
        var client = new FakeClient { Status = "FAILED" };
        await ProcessAsync(store, client);
        Assert.Equal(1, client.GetCalls);
        Assert.Equal("CLORO_TASK_FAILED", store.Failure);
        Assert.False(store.Retryable);
        Assert.Null(store.Answer);
    }

    [Theory]
    [InlineData("chatgpt", false, 1, null, "CLORO_RATE_LIMITED", true)]
    [InlineData("chatgpt", false, 120, null, "CLORO_POLL_TIMEOUT", false)]
    [InlineData("chatgpt", false, 20, 15, "CLORO_GENERATION_TIMEOUT", false)]
    [InlineData("chatgpt", true, 1, null, "CLORO_POLL_FAILED", true)]
    [InlineData("chatgpt", true, 120, null, "CLORO_POLL_TIMEOUT", false)]
    [InlineData("chatgpt", true, 20, 15, "CLORO_GENERATION_TIMEOUT", false)]
    [InlineData("gemini", false, 1, null, "CLORO_RATE_LIMITED", true)]
    [InlineData("gemini", false, 120, null, "CLORO_POLL_TIMEOUT", false)]
    [InlineData("gemini", false, 20, 15, "CLORO_GENERATION_TIMEOUT", false)]
    [InlineData("gemini", true, 1, null, "CLORO_POLL_FAILED", true)]
    [InlineData("gemini", true, 120, null, "CLORO_POLL_TIMEOUT", false)]
    [InlineData("gemini", true, 20, 15, "CLORO_GENERATION_TIMEOUT", false)]
    [InlineData("copilot", false, 1, null, "CLORO_RATE_LIMITED", true)]
    [InlineData("copilot", false, 120, null, "CLORO_POLL_TIMEOUT", false)]
    [InlineData("copilot", false, 20, 15, "CLORO_GENERATION_TIMEOUT", false)]
    [InlineData("copilot", true, 1, null, "CLORO_POLL_FAILED", true)]
    [InlineData("copilot", true, 120, null, "CLORO_POLL_TIMEOUT", false)]
    [InlineData("copilot", true, 20, 15, "CLORO_GENERATION_TIMEOUT", false)]
    [InlineData("perplexity", false, 1, null, "CLORO_RATE_LIMITED", true)]
    [InlineData("perplexity", false, 120, null, "CLORO_POLL_TIMEOUT", false)]
    [InlineData("perplexity", false, 20, 15, "CLORO_GENERATION_TIMEOUT", false)]
    [InlineData("perplexity", true, 1, null, "CLORO_POLL_FAILED", true)]
    [InlineData("perplexity", true, 120, null, "CLORO_POLL_TIMEOUT", false)]
    [InlineData("perplexity", true, 20, 15, "CLORO_GENERATION_TIMEOUT", false)]
    [InlineData("google-ai-overview", false, 1, null, "CLORO_RATE_LIMITED", true)]
    [InlineData("google-ai-overview", false, 120, null, "CLORO_POLL_TIMEOUT", false)]
    [InlineData("google-ai-overview", false, 20, 15, "CLORO_GENERATION_TIMEOUT", false)]
    [InlineData("google-ai-overview", true, 1, null, "CLORO_POLL_FAILED", true)]
    [InlineData("google-ai-overview", true, 120, null, "CLORO_POLL_TIMEOUT", false)]
    [InlineData("google-ai-overview", true, 20, 15, "CLORO_GENERATION_TIMEOUT", false)]
    public async Task TransientPollErrorsCannotExtendTheDeadlineForever(string engine, bool networkError, int submittedMinutes,
        int? startedMinutes, string code, bool retryable)
    {
        var store = new FakeStore(Lease(submittedMinutes, startedMinutes, engine));
        var client = new FakeClient { PollFailure = networkError ? new HttpRequestException("unavailable")
            : new CloroProviderException("CLORO_RATE_LIMITED", "rate limited", true) };
        await ProcessAsync(store, client);
        Assert.Equal(1, client.GetCalls);
        Assert.Equal(0, client.SubmitCalls);
        Assert.Equal(code, store.Failure);
        Assert.Equal(retryable, store.Retryable);
    }

    [Theory]
    [InlineData("chatgpt", "QUEUED")]
    [InlineData("chatgpt", "PROCESSING")]
    [InlineData("gemini", "QUEUED")]
    [InlineData("gemini", "PROCESSING")]
    [InlineData("copilot", "QUEUED")]
    [InlineData("copilot", "PROCESSING")]
    [InlineData("perplexity", "QUEUED")]
    [InlineData("perplexity", "PROCESSING")]
    [InlineData("google-ai-overview", "QUEUED")]
    [InlineData("google-ai-overview", "PROCESSING")]
    public async Task UnfinishedTaskWithinDeadlineContinuesPolling(string engine, string status)
    {
        var store = new FakeStore(Lease(1, null, engine));
        var client = new FakeClient { Status = status };
        await ProcessAsync(store, client);
        Assert.Equal(status, store.Poll?.Status);
        Assert.Null(store.Answer);
        Assert.Null(store.Failure);
    }

    [Theory]
    [InlineData("chatgpt")]
    [InlineData("gemini")]
    [InlineData("copilot")]
    [InlineData("perplexity")]
    [InlineData("google-ai-overview")]
    public async Task UnsubmittedTaskStillUsesTheNormalSubmissionPath(string engine)
    {
        var store = new FakeStore(Lease(120, null, engine) with { ExternalTaskId = null, SubmittedAt = null });
        var client = new FakeClient();
        await ProcessAsync(store, client);
        Assert.Equal(1, client.SubmitCalls);
        Assert.Equal(0, client.GetCalls);
        Assert.True(store.Submitting);
        Assert.Equal("new-external-task", store.Submission?.ExternalTaskId);
    }

    [Theory]
    [InlineData("chatgpt")]
    [InlineData("gemini")]
    [InlineData("copilot")]
    [InlineData("perplexity")]
    [InlineData("google-ai-overview")]
    public async Task UnknownSubmissionIsTerminalAndNeverAutomaticallyRetried(string engine)
    {
        var store = new FakeStore(Lease(1, null, engine) with { ExternalTaskId = null, SubmittedAt = null });
        var client = new FakeClient { SubmitFailure = new CloroProviderException("CLORO_SUBMISSION_UNKNOWN", "unknown", false, true) };
        await ProcessAsync(store, client);
        Assert.Equal(1, client.SubmitCalls);
        Assert.Equal(0, client.GetCalls);
        Assert.Equal("CLORO_SUBMISSION_UNKNOWN", store.Failure);
        Assert.False(store.Retryable);
        Assert.Null(store.Answer);
    }

    [Theory]
    [InlineData("perplexity")]
    [InlineData("google-ai-overview")]
    public async Task ModelUnavailableSubmissionIsTerminalAndNeverRetried(string engine)
    {
        var store = new FakeStore(Lease(1, null, engine) with { ExternalTaskId = null, SubmittedAt = null });
        var client = new FakeClient { SubmitFailure = new CloroProviderException("CLORO_MODEL_UNAVAILABLE", "unavailable", false) };
        await ProcessAsync(store, client);
        Assert.Equal(1, client.SubmitCalls);
        Assert.Equal(0, client.GetCalls);
        Assert.Equal("CLORO_MODEL_UNAVAILABLE", store.Failure);
        Assert.False(store.Retryable);
        Assert.Null(store.Answer);
    }

    [Theory]
    [InlineData("chatgpt")]
    [InlineData("gemini")]
    [InlineData("copilot")]
    [InlineData("perplexity")]
    [InlineData("google-ai-overview")]
    public async Task PersistenceDatabaseFailureDoesNotOverwriteRecoverableTaskWithProviderFailure(string engine)
    {
        var store = new FakeStore(Lease(1, null, engine) ) { CompletionFailure = new FixtureDatabaseFailure() };
        var client = new FakeClient();
        await ProcessAsync(store, client);
        Assert.Equal(1, client.GetCalls);
        Assert.Equal(0, client.SubmitCalls);
        Assert.Null(store.Failure);
        Assert.Null(store.Answer);
    }

    [Theory]
    [InlineData("chatgpt")]
    [InlineData("gemini")]
    [InlineData("copilot")]
    [InlineData("perplexity")]
    [InlineData("google-ai-overview")]
    public async Task SubmissionReceiptDatabaseFailureKeepsDurableSubmittingStageForUnknownRecovery(string engine)
    {
        var store = new FakeStore(Lease(1, null, engine) with { ExternalTaskId = null, SubmittedAt = null })
            { SubmissionFailure = new FixtureDatabaseFailure() };
        var client = new FakeClient();
        await ProcessAsync(store, client);
        Assert.True(store.Submitting);
        Assert.Null(store.Submission);
        Assert.Null(store.Failure); // DB state, not an invented Provider outcome, owns recovery.
        Assert.Equal(1, client.SubmitCalls);
        Assert.Equal(0, client.GetCalls);
    }

    [Fact]
    public async Task SubmissionPassesTaskIdAndRecordsSubmittingKey()
    {
        var keyId = Guid.NewGuid();
        var store = new FakeStore(Lease(1, null, "chatgpt") with { ExternalTaskId = null, SubmittedAt = null });
        var client = new FakeClient { SubmittedKeyId = keyId };
        await ProcessAsync(store, client);
        Assert.Equal(store.LeasedTask.TaskId, client.LastRequest?.ProviderTaskId);
        Assert.Equal(keyId, store.Submission?.KeyId);
    }

    [Fact]
    public async Task PollingUsesTheKeyRecordedAtSubmission()
    {
        var keyId = Guid.NewGuid();
        var store = new FakeStore(Lease(1, null, "chatgpt") with { CloroKeyId = keyId });
        var client = new FakeClient { Status = "QUEUED" };
        await ProcessAsync(store, client);
        Assert.Equal(keyId, client.LastKeyId);
    }

    [Theory]
    [InlineData("CLORO_KEY_POOL_EXHAUSTED", true)]
    [InlineData("CLORO_KEY_POOL_BUSY", false)]
    [InlineData("CLORO_KEY_POOL_UNAVAILABLE", false)]
    public async Task ExhaustedPoolDefersUnsubmittedTaskAndKeepsReservation(string code, bool countsTowardMaximumWait)
    {
        var recovery = DateTimeOffset.UtcNow.AddMinutes(3);
        var store = new FakeStore(Lease(1, null, "chatgpt") with { ExternalTaskId = null, SubmittedAt = null });
        var client = new FakeClient { SubmitFailure = new CloroProviderException(code, "pool", true, nextAttemptAt: recovery) };
        await ProcessAsync(store, client);
        Assert.Null(store.Failure);
        Assert.Equal(code, store.Deferred?.Code);
        Assert.Equal(recovery, store.Deferred?.Next);
        Assert.Equal(countsTowardMaximumWait, store.Deferred?.CreditsExhausted);
    }

    [Fact]
    public async Task ExhaustedPoolDeferralIsCappedByRetryInterval()
    {
        var store = new FakeStore(Lease(1, null, "chatgpt") with { ExternalTaskId = null, SubmittedAt = null });
        var client = new FakeClient
        {
            SubmitFailure = new CloroProviderException("CLORO_KEY_POOL_EXHAUSTED", "pool", true,
                nextAttemptAt: DateTimeOffset.UtcNow.AddDays(5)),
        };
        var before = DateTimeOffset.UtcNow;
        await ProcessAsync(store, client);
        Assert.InRange(store.Deferred!.Value.Next, before.AddMinutes(9), DateTimeOffset.UtcNow.AddMinutes(10).AddSeconds(1));
    }

    [Fact]
    public async Task ExhaustedPoolBeyondMaximumWaitFailsTerminallySoReservationIsReleased()
    {
        var store = new FakeStore(Lease(1, null, "chatgpt") with
        {
            ExternalTaskId = null, SubmittedAt = null, PoolExhaustedSince = DateTimeOffset.UtcNow.AddHours(-2).AddMinutes(-1),
        });
        var client = new FakeClient { SubmitFailure = new CloroProviderException("CLORO_KEY_POOL_EXHAUSTED", "pool", true) };
        await ProcessAsync(store, client);
        Assert.Null(store.Deferred);
        Assert.Equal("CLORO_KEY_POOL_EXHAUSTED", store.Failure);
        Assert.False(store.Retryable);
    }

    [Theory]
    [InlineData("CLORO_KEY_POOL_BUSY")]
    [InlineData("CLORO_KEY_POOL_UNAVAILABLE")]
    public async Task BusyOrUnreadablePoolNeverFailsTheTaskForMaximumWait(string code)
    {
        // Only running out of credits counts toward the 2h maximum; a 429 cooldown or read failure keeps waiting.
        var store = new FakeStore(Lease(1, null, "chatgpt") with
        {
            ExternalTaskId = null, SubmittedAt = null, PoolExhaustedSince = DateTimeOffset.UtcNow.AddHours(-3),
        });
        var recovery = DateTimeOffset.UtcNow.AddSeconds(30);
        var client = new FakeClient { SubmitFailure = new CloroProviderException(code, "pool", true, nextAttemptAt: recovery) };
        await ProcessAsync(store, client);
        Assert.Null(store.Failure);
        Assert.Equal(recovery, store.Deferred?.Next);
        Assert.False(store.Deferred?.CreditsExhausted);
    }

    [Fact]
    public async Task SwitchLimitStaysRetryableForTheNextLease()
    {
        var store = new FakeStore(Lease(1, null, "chatgpt") with { ExternalTaskId = null, SubmittedAt = null });
        var client = new FakeClient { SubmitFailure = new CloroProviderException("CLORO_KEY_SWITCH_LIMIT", "limit", true) };
        await ProcessAsync(store, client);
        Assert.Equal("CLORO_KEY_SWITCH_LIMIT", store.Failure);
        Assert.True(store.Retryable);
        Assert.Null(store.Deferred);
    }

    [Fact]
    public async Task QueuedTaskFailedForCreditsIsResubmittedAndAccountMarkedExhausted()
    {
        var pool = new InMemoryCloroKeyPool();
        var key = pool.Add("account-a", "key-aaaa-1111");
        var store = new FakeStore(Lease(1, null, "chatgpt") with { CloroKeyId = key.KeyId });
        var client = new FakeClient { Status = "FAILED", ProviderFailureCode = "INSUFFICIENT_CREDITS" };
        await ProcessAsync(store, client, pool);
        Assert.Equal(1, store.ResetCalls);
        Assert.Equal(2, store.ResetMax);
        Assert.Null(store.Failure);
        var rejection = Assert.Single(pool.Rejections);
        Assert.Equal(key.KeyId, rejection.KeyId);
        Assert.Equal(CloroKeyRejection.InsufficientCredits, rejection.Rejection);
        Assert.Equal(store.LeasedTask.TaskId, rejection.TaskId);
    }

    [Fact]
    public async Task FailedTaskWithoutCodeIsResubmittedOnlyWhenRefreshedBalanceIsExhausted()
    {
        var pool = new InMemoryCloroKeyPool();
        var key = pool.Add("account-a", "key-aaaa-1111");
        var credits = new FakeCreditsClient();
        credits.Results[key.KeyId] = () => new CloroCreditsSnapshot(0, 500, DateTimeOffset.UtcNow.AddDays(3), DateTimeOffset.UtcNow);
        var store = new FakeStore(Lease(1, null, "chatgpt") with { CloroKeyId = key.KeyId });
        await ProcessAsync(store, new FakeClient { Status = "FAILED" }, pool, credits);
        Assert.Equal(1, store.ResetCalls);
        Assert.Null(store.Failure);
        Assert.Equal("exhausted", pool.Find(key.KeyId).Status);

        var healthy = new FakeStore(Lease(1, null, "chatgpt") with { CloroKeyId = key.KeyId });
        credits.Results[key.KeyId] = () => new CloroCreditsSnapshot(80, 500, DateTimeOffset.UtcNow.AddDays(3), DateTimeOffset.UtcNow);
        await ProcessAsync(healthy, new FakeClient { Status = "FAILED" }, pool, credits);
        Assert.Equal(0, healthy.ResetCalls);
        Assert.Equal("CLORO_TASK_FAILED", healthy.Failure);
        Assert.False(healthy.Retryable);
    }

    [Fact]
    public async Task CreditResubmissionStopsAtTheConfiguredLimit()
    {
        var pool = new InMemoryCloroKeyPool();
        var key = pool.Add("account-a", "key-aaaa-1111");
        var store = new FakeStore(Lease(1, null, "chatgpt") with { CloroKeyId = key.KeyId, CreditResubmitCount = 2 });
        await ProcessAsync(store, new FakeClient { Status = "FAILED", ProviderFailureCode = "INSUFFICIENT_CREDITS" }, pool);
        Assert.Equal(0, store.ResetCalls);
        Assert.Empty(pool.Rejections);
        Assert.Equal("CLORO_TASK_FAILED", store.Failure);
        Assert.False(store.Retryable);
    }

    [Fact]
    public async Task ResubmissionRejectedByStoreFallsBackToTerminalFailure()
    {
        var pool = new InMemoryCloroKeyPool();
        var key = pool.Add("account-a", "key-aaaa-1111");
        var store = new FakeStore(Lease(1, null, "chatgpt") with { CloroKeyId = key.KeyId }) { ResetResult = false };
        await ProcessAsync(store, new FakeClient { Status = "FAILED", ProviderFailureCode = "INSUFFICIENT_CREDITS" }, pool);
        Assert.Equal(1, store.ResetCalls);
        Assert.Equal("CLORO_TASK_FAILED", store.Failure);
    }

    private sealed class FixtureDatabaseFailure : System.Data.Common.DbException;

    private static CloroTaskLease Lease(int submittedMinutes, int? startedMinutes, string engine)
    {
        var now = DateTimeOffset.UtcNow;
        return new CloroTaskLease(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "Which products should I compare?", engine, "en-US", "US", CloroTaskStates.Running, "external-task",
            2, now.AddMinutes(1), 2, now.AddMinutes(-submittedMinutes), now.AddMinutes(-submittedMinutes),
            startedMinutes is { } minutes ? now.AddMinutes(-minutes) : null);
    }

    private static async Task ProcessAsync(FakeStore store, FakeClient client, InMemoryCloroKeyPool? pool = null,
        FakeCreditsClient? credits = null, CloroKeyPoolOptions? poolOptions = null)
    {
        pool ??= CloroAdapterTestFixture.DefaultPool();
        var refresher = new CloroKeyCreditsRefresher(pool, credits ?? new FakeCreditsClient(), NullLogger<CloroKeyCreditsRefresher>.Instance);
        using var worker = new CloroTaskWorker(NullLogger<CloroTaskWorker>.Instance, store, client,
            Options.Create(new CloroAdapterOptions()), pool, refresher, Options.Create(poolOptions ?? new CloroKeyPoolOptions()));
        await worker.StartAsync(CancellationToken.None);
        try { await store.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { await worker.StopAsync(CancellationToken.None); }
        Assert.Equal(0, store.TimeoutRecoveryCalls);
    }

    private sealed class FakeClient : ICloroTaskClient
    {
        public int GetCalls { get; private set; }
        public string? LastTaskId { get; private set; }
        public Guid? LastKeyId { get; private set; }
        public CloroTaskRequest? LastRequest { get; private set; }
        public string? ProviderFailureCode { get; init; }
        public int SubmitCalls { get; private set; }
        public string Status { get; init; } = "COMPLETED";
        public Exception? PollFailure { get; init; }
        public Exception? SubmitFailure { get; init; }
        public Guid? SubmittedKeyId { get; init; }

        public Task<CloroSubmittedTask> SubmitAsync(CloroTaskRequest request, CancellationToken cancellationToken = default)
        {
            SubmitCalls++;
            LastRequest = request;
            if (SubmitFailure is not null) throw SubmitFailure;
            return Task.FromResult(new CloroSubmittedTask("new-external-task", DateTimeOffset.UtcNow, "hash", SubmittedKeyId));
        }

        public Task<CloroTaskStatusSnapshot> GetAsync(string externalTaskId, Guid? keyId = null, CancellationToken cancellationToken = default)
        {
            GetCalls++; LastTaskId = externalTaskId; LastKeyId = keyId;
            if (PollFailure is not null) throw PollFailure;
            var payload = JsonSerializer.SerializeToElement(new { status = Status, response = new { markdown = "answer" } });
            return Task.FromResult(new CloroTaskStatusSnapshot(externalTaskId, Status, payload,
                Status == "FAILED" ? "CLORO_TASK_FAILED" : null, ProviderFailureCode: ProviderFailureCode));
        }

        public CloroAnswerSnapshot Normalize(CloroTaskStatusSnapshot completed, string requestedEngine) =>
            new("cloro", requestedEngine, "scraped", "observed_surface", "gpt-test", "answer", true,
                [], [], completed.Payload, DateTimeOffset.UtcNow);
    }

    private sealed class FakeStore(CloroTaskLease task) : ICloroVisibilityStore
    {
        private int leases;
        public CloroTaskLease LeasedTask => task;
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CloroAnswerSnapshot? Answer { get; private set; }
        public string? Failure { get; private set; }
        public bool Retryable { get; private set; }
        public bool Submitting { get; private set; }
        public CloroSubmittedTask? Submission { get; private set; }
        public CloroTaskStatusSnapshot? Poll { get; private set; }
        public int TimeoutRecoveryCalls { get; private set; }
        public Exception? CompletionFailure { get; init; }
        public Exception? SubmissionFailure { get; init; }
        public (string Code, DateTimeOffset Next, bool CreditsExhausted)? Deferred { get; private set; }
        public int ResetCalls { get; private set; }
        public int? ResetMax { get; private set; }
        public bool ResetResult { get; init; } = true;

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
            Assert.Equal("cloro", provider);
            if (Interlocked.Exchange(ref leases, 1) == 0) return Task.FromResult<IReadOnlyList<CloroTaskLease>>([task]);
            return Task.FromResult<IReadOnlyList<CloroTaskLease>>([]);
        }
        public Task MarkSubmittingAsync(CloroTaskLease task, string workerId, CancellationToken cancellationToken = default)
        {
            Submitting = true;
            return Task.CompletedTask;
        }
        public Task RecordSubmissionAsync(CloroTaskLease task, string workerId, CloroSubmittedTask submitted,
            CancellationToken cancellationToken = default)
        {
            if (SubmissionFailure is not null)
            {
                Completed.TrySetResult();
                throw SubmissionFailure;
            }
            Submission = submitted;
            Completed.TrySetResult();
            return Task.CompletedTask;
        }
        public Task RecordPollAsync(CloroTaskLease task, string workerId, CloroTaskStatusSnapshot snapshot, TimeSpan delay,
            CancellationToken cancellationToken = default)
        {
            Poll = snapshot;
            Completed.TrySetResult();
            return Task.CompletedTask;
        }
        public Task CompleteTaskAsync(CloroTaskLease task, string workerId, CloroAnswerSnapshot answer, int durationMs,
            CancellationToken cancellationToken = default)
        {
            if (CompletionFailure is not null)
            {
                Completed.TrySetResult();
                throw CompletionFailure;
            }
            Answer = answer;
            Completed.TrySetResult();
            return Task.CompletedTask;
        }
        public Task CompleteSynchronousTaskAsync(CloroTaskLease task, string workerId, CloroAnswerSnapshot answer, long durationMs,
            string externalId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task FailTaskAsync(CloroTaskLease task, string workerId, string errorCode, bool retryable, TimeSpan? retryAfter = null,
            CancellationToken cancellationToken = default)
        {
            Assert.Null(retryAfter);
            Failure = errorCode;
            Retryable = retryable;
            Completed.TrySetResult();
            return Task.CompletedTask;
        }
        public Task DeferTaskAsync(CloroTaskLease task, string workerId, string errorCode, DateTimeOffset nextAttemptAt,
            bool creditsExhausted, CancellationToken cancellationToken = default)
        {
            Deferred = (errorCode, nextAttemptAt, creditsExhausted);
            Completed.TrySetResult();
            return Task.CompletedTask;
        }
        public Task<bool> ResetForResubmissionAsync(CloroTaskLease task, string workerId, int maxResubmits,
            CancellationToken cancellationToken = default)
        {
            ResetCalls++;
            ResetMax = maxResubmits;
            Completed.TrySetResult();
            return Task.FromResult(ResetResult);
        }
        public Task<int> RecoverExpiredTasksAsync(string provider = "cloro", CancellationToken cancellationToken = default)
        {
            Assert.Equal("cloro", provider);
            return Task.FromResult(0);
        }
        public Task<int> RecoverTimedOutTasksAsync(Guid runId, CancellationToken cancellationToken = default)
        {
            TimeoutRecoveryCalls++;
            return Task.FromResult(0);
        }
    }
}
