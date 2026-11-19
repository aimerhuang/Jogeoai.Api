using System.Net;
using System.Text.Json;
using Jogeoai.Providers.Abstractions;
using Jogeoai.Providers.Cloro;

using static Jogeoai.Tests.CloroAdapterTestFixture;

namespace Jogeoai.Tests;

public sealed class CloroAdapterKeyPoolTests
{
    private const string KeyA = "cloro-key-aaaa-1111";
    private const string KeyB = "cloro-key-bbbb-2222";
    private const string KeyC = "cloro-key-cccc-3333";
    private const string KeyD = "cloro-key-dddd-4444";
    private static readonly CloroTaskRequest Request = new("chatgpt", "Which product?", "en-US", "US", Guid.NewGuid());

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, """{"error":{"code":"INSUFFICIENT_CREDITS","message":"KEY-SECRET-TEXT"}}""", CloroKeyRejection.InsufficientCredits, "INSUFFICIENT_CREDITS")]
    [InlineData(HttpStatusCode.Forbidden, """{"code":"insufficient_credits"}""", CloroKeyRejection.InsufficientCredits, "INSUFFICIENT_CREDITS")]
    [InlineData(HttpStatusCode.Forbidden, """{"error":{"code":"SOMETHING_ELSE"}}""", CloroKeyRejection.Forbidden, "HTTP_403")]
    // A 403 stays a definite key rejection even when the body carries the model-unavailable code.
    [InlineData(HttpStatusCode.Forbidden, """{"error":{"code":"MODEL_UNAVAILABLE"}}""", CloroKeyRejection.Forbidden, "MODEL_UNAVAILABLE")]
    [InlineData(HttpStatusCode.TooManyRequests, """{"error":{"code":"CONCURRENCY_LIMIT_EXCEEDED"}}""", CloroKeyRejection.ConcurrencyLimited, "CONCURRENCY_LIMIT_EXCEEDED")]
    [InlineData(HttpStatusCode.TooManyRequests, """{"error":{"code":"QUEUE_LIMIT_EXCEEDED"}}""", CloroKeyRejection.QueueFull, "QUEUE_LIMIT_EXCEEDED")]
    [InlineData(HttpStatusCode.TooManyRequests, """{"error":{"code":"RATE_LIMIT_EXCEEDED"}}""", CloroKeyRejection.RateLimited, "RATE_LIMIT_EXCEEDED")]
    [InlineData(HttpStatusCode.TooManyRequests, "not json: provider text", CloroKeyRejection.RateLimited, "HTTP_429")]
    [InlineData(HttpStatusCode.Unauthorized, """{"error":{"code":"INVALID_API_KEY"}}""", CloroKeyRejection.Unauthorized, "INVALID_API_KEY")]
    [InlineData(HttpStatusCode.Unauthorized, "", CloroKeyRejection.Unauthorized, "HTTP_401")]
    // Envelope observed from the live Cloro API on 2026-10-11; the message text must not be retained.
    [InlineData(HttpStatusCode.Unauthorized, """{"success":false,"error":{"code":"INVALID_API_KEY_FORMAT","message":"Invalid API key format","timestamp":"2026-10-11T02:03:01.842Z"}}""",
        CloroKeyRejection.Unauthorized, "INVALID_API_KEY_FORMAT")]
    [InlineData(HttpStatusCode.Forbidden, """{"success":false,"error":{"code":"INSUFFICIENT_CREDITS","message":"Insufficient credits"}}""",
        CloroKeyRejection.InsufficientCredits, "INSUFFICIENT_CREDITS")]
    public async Task DefiniteRejectionMarksAccountAndSwitchesWithinTheSameLease(HttpStatusCode status, string body,
        CloroKeyRejection expected, string expectedCode)
    {
        var pool = new InMemoryCloroKeyPool();
        var a = pool.Add("account-a", KeyA, priority: 1);
        var b = pool.Add("account-b", KeyB, priority: 2);
        var handler = new FakeHandler(request => request.Headers.Authorization?.Parameter == KeyA
            ? Status(status, body) : Json("""{"task":{"id":"task-on-b"}}"""));

        var submitted = await Create(handler, pool).SubmitAsync(Request);

        Assert.Equal("task-on-b", submitted.ExternalTaskId);
        Assert.Equal(b.KeyId, submitted.KeyId);
        Assert.Equal([KeyA, KeyB], handler.Authorizations);
        var rejection = Assert.Single(pool.Rejections);
        Assert.Equal((a.KeyId, Request.ProviderTaskId, expected, expectedCode), rejection);
        Assert.Equal((b.KeyId, Request.ProviderTaskId, "task-on-b"), Assert.Single(pool.Used));
        // Only allowlisted codes survive; provider text never reaches the stored code.
        Assert.DoesNotContain("SECRET", rejection.Code, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task UnknownSubmissionOutcomeNeverSwitchesKeys(HttpStatusCode status)
    {
        var pool = new InMemoryCloroKeyPool();
        pool.Add("account-a", KeyA, 1);
        pool.Add("account-b", KeyB, 2);
        var handler = new FakeHandler(_ => Status(status, """{"error":{"code":"INSUFFICIENT_CREDITS"}}"""));

        var exception = await Assert.ThrowsAsync<CloroProviderException>(() => Create(handler, pool).SubmitAsync(Request));

        Assert.Equal("CLORO_SUBMISSION_UNKNOWN", exception.Code);
        Assert.True(exception.SubmissionUnknown);
        Assert.False(exception.Retryable);
        Assert.Equal([KeyA], handler.Authorizations);
        Assert.Empty(pool.Rejections);
        Assert.Empty(pool.Used);
    }

    [Fact]
    public async Task NetworkFailureTimeoutAndUnparseableSuccessNeverSwitchKeys()
    {
        foreach (var responder in new Func<HttpRequestMessage, HttpResponseMessage>[]
        {
            _ => throw new HttpRequestException("connection reset"),
            _ => Json("{ not json"),
            _ => Json("""{"status":"QUEUED"}"""),
        })
        {
            var pool = new InMemoryCloroKeyPool();
            pool.Add("account-a", KeyA, 1);
            pool.Add("account-b", KeyB, 2);
            var handler = new FakeHandler(responder);
            var exception = await Assert.ThrowsAsync<CloroProviderException>(() => Create(handler, pool).SubmitAsync(Request));
            Assert.Equal("CLORO_SUBMISSION_UNKNOWN", exception.Code);
            Assert.Single(handler.Authorizations);
            Assert.Empty(pool.Rejections);
        }

        var slowPool = new InMemoryCloroKeyPool();
        slowPool.Add("account-a", KeyA, 1);
        slowPool.Add("account-b", KeyB, 2);
        var slow = new SlowHandler();
        var adapter = new CloroAdapter(new HttpClient(slow), new StaticSettings(), slowPool,
            Microsoft.Extensions.Options.Options.Create(new CloroAdapterOptions { RequestTimeout = TimeSpan.FromMilliseconds(50) }),
            Microsoft.Extensions.Options.Options.Create(new CloroKeyPoolOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CloroAdapter>.Instance);
        var timeout = await Assert.ThrowsAsync<CloroProviderException>(() => adapter.SubmitAsync(Request));
        Assert.Equal("CLORO_SUBMISSION_UNKNOWN", timeout.Code);
        Assert.Equal(1, slow.Calls);
        Assert.Empty(slowPool.Rejections);
    }

    [Fact]
    public async Task CancellationAfterARejectionNeverSubmitsWithAnotherAccount()
    {
        var pool = new InMemoryCloroKeyPool();
        pool.Add("account-a", KeyA, 1);
        pool.Add("account-b", KeyB, 2);
        using var stopping = new CancellationTokenSource();
        pool.OnRejected = stopping.Cancel;
        var handler = new FakeHandler(_ => Status(HttpStatusCode.Forbidden, """{"error":{"code":"INSUFFICIENT_CREDITS"}}"""));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(handler, pool).SubmitAsync(Request, stopping.Token));

        Assert.Equal([KeyA], handler.Authorizations);
        Assert.Single(pool.Rejections);
        Assert.Empty(pool.Used);
    }

    [Fact]
    public async Task OtherClientErrorsKeepExistingTerminalBehaviourWithoutSwitching()
    {
        var pool = new InMemoryCloroKeyPool();
        pool.Add("account-a", KeyA, 1);
        pool.Add("account-b", KeyB, 2);
        var handler = new FakeHandler(_ => Status(HttpStatusCode.BadRequest, "{}"));
        var exception = await Assert.ThrowsAsync<CloroProviderException>(() => Create(handler, pool).SubmitAsync(Request));
        Assert.Equal("CLORO_SUBMIT_FAILED", exception.Code);
        Assert.False(exception.Retryable);
        Assert.Single(handler.Authorizations);
        Assert.Empty(pool.Rejections);
    }

    [Theory]
    // Live Perplexity rejection observed 2026-10-16 (message text must not be retained).
    [InlineData(HttpStatusCode.BadRequest, """{"success":false,"error":{"code":"MODEL_UNAVAILABLE","message":"KEY-SECRET-TEXT provider outage"}}""")]
    [InlineData(HttpStatusCode.BadRequest, """{"code":"model_unavailable"}""")]
    [InlineData(HttpStatusCode.NotFound, """{"error":{"code":"MODEL_UNAVAILABLE"}}""")]
    public async Task ModelUnavailableIsTerminalWithoutSwitchingAccounts(HttpStatusCode status, string body)
    {
        var pool = new InMemoryCloroKeyPool();
        pool.Add("account-a", KeyA, 1);
        pool.Add("account-b", KeyB, 2);
        var handler = new FakeHandler(_ => Status(status, body));
        var request = Request with { Engine = "perplexity" };

        var exception = await Assert.ThrowsAsync<CloroProviderException>(() => Create(handler, pool).SubmitAsync(request));

        Assert.Equal("CLORO_MODEL_UNAVAILABLE", exception.Code);
        Assert.False(exception.Retryable);
        Assert.False(exception.SubmissionUnknown);
        Assert.DoesNotContain("SECRET", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([KeyA], handler.Authorizations);
        Assert.Empty(pool.Rejections);
        Assert.Empty(pool.Used);
    }

    [Theory]
    [InlineData("""{"success":false,"error":{"code":"INVALID_REQUEST","message":"bad payload"}}""")]
    [InlineData("""{"error":{"code":"INSUFFICIENT_CREDITS"}}""")]
    [InlineData("not json: MODEL_UNAVAILABLE")]
    [InlineData("")]
    public async Task OtherBadRequestBodiesStaySubmitFailedWithoutSwitching(string body)
    {
        var pool = new InMemoryCloroKeyPool();
        pool.Add("account-a", KeyA, 1);
        pool.Add("account-b", KeyB, 2);
        var handler = new FakeHandler(_ => Status(HttpStatusCode.BadRequest, body));

        var exception = await Assert.ThrowsAsync<CloroProviderException>(() => Create(handler, pool).SubmitAsync(Request));

        Assert.Equal("CLORO_SUBMIT_FAILED", exception.Code);
        Assert.False(exception.Retryable);
        Assert.False(exception.SubmissionUnknown);
        Assert.Equal([KeyA], handler.Authorizations);
        Assert.Empty(pool.Rejections);
    }

    [Fact]
    public async Task AccountsOnlyCoolingAfter429LeaveThePoolBusyNotExhausted()
    {
        var pool = new InMemoryCloroKeyPool { NextAvailableAt = DateTimeOffset.UtcNow.AddSeconds(30) };
        pool.Add("account-a", KeyA, 1);
        pool.Add("account-b", KeyB, 2, status: "exhausted");
        var handler = new FakeHandler(_ => Status(HttpStatusCode.TooManyRequests, """{"error":{"code":"CONCURRENCY_LIMIT_EXCEEDED"}}"""));

        var exception = await Assert.ThrowsAsync<CloroProviderException>(() => Create(handler, pool).SubmitAsync(Request));

        Assert.Equal("CLORO_KEY_POOL_BUSY", exception.Code);
        Assert.True(exception.Retryable);
        Assert.False(exception.SubmissionUnknown);
        Assert.Equal(pool.NextAvailableAt, exception.NextAttemptAt);
        Assert.Single(handler.Authorizations);
    }

    [Fact]
    public async Task EveryAccountRejectedReportsPoolExhaustedWithRecoveryTime()
    {
        var pool = new InMemoryCloroKeyPool { NextAvailableAt = DateTimeOffset.UtcNow.AddMinutes(30) };
        pool.Add("account-a", KeyA, 1);
        pool.Add("account-b", KeyB, 2);
        var handler = new FakeHandler(_ => Status(HttpStatusCode.Forbidden, """{"error":{"code":"INSUFFICIENT_CREDITS"}}"""));

        var exception = await Assert.ThrowsAsync<CloroProviderException>(() => Create(handler, pool).SubmitAsync(Request));

        Assert.Equal("CLORO_KEY_POOL_EXHAUSTED", exception.Code);
        Assert.True(exception.Retryable);
        Assert.False(exception.SubmissionUnknown);
        Assert.Equal(pool.NextAvailableAt, exception.NextAttemptAt);
        Assert.Equal(2, pool.Rejections.Count);
        Assert.All(pool.Rejections, r => Assert.Equal(CloroKeyRejection.InsufficientCredits, r.Rejection));
    }

    [Fact]
    public async Task AttemptLimitStopsSwitchingAndStaysRetryable()
    {
        var pool = new InMemoryCloroKeyPool();
        foreach (var (label, key, priority) in new[] { ("a", KeyA, 1), ("b", KeyB, 2), ("c", KeyC, 3), ("d", KeyD, 4) })
            pool.Add(label, key, priority);
        var handler = new FakeHandler(_ => Status(HttpStatusCode.TooManyRequests, """{"error":{"code":"CONCURRENCY_LIMIT_EXCEEDED"}}"""));

        var exception = await Assert.ThrowsAsync<CloroProviderException>(() => Create(handler, pool).SubmitAsync(Request));

        Assert.Equal("CLORO_KEY_SWITCH_LIMIT", exception.Code);
        Assert.True(exception.Retryable);
        Assert.Equal([KeyA, KeyB, KeyC], handler.Authorizations);
    }

    [Fact]
    public async Task PoolWithoutEnabledKeysIsNotConfigured()
    {
        var pool = new InMemoryCloroKeyPool();
        pool.Add("account-a", KeyA, enabled: false);
        var handler = new FakeHandler(_ => Json("""{"task":{"id":"never"}}"""));
        var exception = await Assert.ThrowsAsync<CloroProviderException>(() => Create(handler, pool).SubmitAsync(Request));
        Assert.Equal("CLORO_NOT_CONFIGURED", exception.Code);
        Assert.False(exception.Retryable);
        Assert.Empty(handler.Authorizations);
    }

    [Fact]
    public async Task UnreadablePoolBeforeAnyRequestIsRetryableNotUnknown()
    {
        var pool = new InMemoryCloroKeyPool { PickFailure = new InvalidOperationException("db down") };
        pool.Add("account-a", KeyA);
        var handler = new FakeHandler(_ => Json("""{"task":{"id":"never"}}"""));
        var exception = await Assert.ThrowsAsync<CloroProviderException>(() => Create(handler, pool).SubmitAsync(Request));
        Assert.Equal("CLORO_KEY_POOL_UNAVAILABLE", exception.Code);
        Assert.True(exception.Retryable);
        Assert.False(exception.SubmissionUnknown);
        Assert.Empty(handler.Authorizations);
    }

    [Fact]
    public async Task PollingUsesTheSubmittingKeyEvenWhenItIsDisabled()
    {
        var pool = new InMemoryCloroKeyPool();
        pool.Add("default", KeyB);
        var a = pool.Add("account-a", KeyA, enabled: false, status: "exhausted");
        var handler = new FakeHandler(_ => Json("""{"status":"QUEUED"}"""));
        var adapter = Create(handler, pool);

        await adapter.GetAsync("task-1", a.KeyId);
        await adapter.GetAsync("legacy-task");

        Assert.Equal([KeyA, KeyB], handler.Authorizations);
        Assert.EndsWith("/v1/async/task/task-1", handler.Uris[0]!.AbsolutePath);
    }

    [Fact]
    public async Task PollUnauthorizedMarksKeyInvalidAndFailsTask()
    {
        var pool = new InMemoryCloroKeyPool();
        var a = pool.Add("account-a", KeyA);
        var handler = new FakeHandler(_ => Status(HttpStatusCode.Unauthorized, $"bad key {KeyA}"));
        var exception = await Assert.ThrowsAsync<CloroProviderException>(() => Create(handler, pool).GetAsync("task-1", a.KeyId));
        Assert.Equal("CLORO_KEY_INVALID", exception.Code);
        Assert.False(exception.Retryable);
        Assert.Equal(CloroKeyRejection.Unauthorized, Assert.Single(pool.Rejections).Rejection);
        Assert.DoesNotContain(KeyA, exception.Message);
    }

    [Theory]
    [InlineData("""{"status":"FAILED","error":{"code":"INSUFFICIENT_CREDITS","message":"top up"}}""", "INSUFFICIENT_CREDITS")]
    [InlineData("""{"task":{"status":"FAILED","error":"insufficient_credits"}}""", "INSUFFICIENT_CREDITS")]
    [InlineData("""{"status":"FAILED","error":{"code":"MODEL_UNAVAILABLE","message":"provider says no"}}""", null)]
    public async Task FailedPollExtractsOnlyAllowlistedFailureCode(string body, string? expected)
    {
        var snapshot = await Create(new FakeHandler(_ => Json(body))).GetAsync("task-1");
        Assert.Equal("FAILED", snapshot.Status);
        Assert.Equal(expected, snapshot.ProviderFailureCode);
        Assert.DoesNotContain("provider says no", snapshot.Payload.GetRawText());
        Assert.DoesNotContain("top up", snapshot.Payload.GetRawText());
    }

    [Fact]
    public async Task CreditsAreReadFromDerivedEndpointWithTheGivenKey()
    {
        var handler = new FakeHandler(_ => Json("""{"remaining":42.5,"perCycle":"500","cycleResetsAt":"2026-11-01T00:00:00Z"}"""));
        var credits = await Create(handler).GetCreditsAsync(new CloroKeyLease(Guid.NewGuid(), "account-a", KeyA, "1111"));
        Assert.Equal(42.5m, credits.Remaining);
        Assert.Equal(500m, credits.PerCycle);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), credits.CycleResetsAt);
        Assert.Equal("https://cloro.test/v1/credits", handler.Uris.Single()!.AbsoluteUri);
        Assert.Equal(KeyA, handler.Authorizations.Single());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "CLORO_KEY_INVALID", false)]
    [InlineData(HttpStatusCode.TooManyRequests, "CLORO_RATE_LIMITED", true)]
    [InlineData(HttpStatusCode.InternalServerError, "CLORO_CREDITS_FAILED", true)]
    public async Task CreditsFailuresUseStableCodes(HttpStatusCode status, string code, bool retryable)
    {
        var handler = new FakeHandler(_ => Status(status, $"echo {KeyA}"));
        var exception = await Assert.ThrowsAsync<CloroProviderException>(() =>
            Create(handler).GetCreditsAsync(new CloroKeyLease(Guid.NewGuid(), "account-a", KeyA, "1111")));
        Assert.Equal(code, exception.Code);
        Assert.Equal(retryable, exception.Retryable);
        Assert.DoesNotContain(KeyA, exception.Message);
    }

    [Fact]
    public async Task CreditsUrlOptionOverridesDerivedEndpointAndMissingRemainingIsInvalid()
    {
        var handler = new FakeHandler(_ => Json("""{"data":{"perCycle":500}}"""));
        var exception = await Assert.ThrowsAsync<CloroProviderException>(() =>
            Create(handler, DefaultPool(), new CloroKeyPoolOptions { CreditsUrl = "https://billing.cloro.test/credits" })
                .GetCreditsAsync(new CloroKeyLease(Guid.NewGuid(), "account-a", KeyA, "1111")));
        Assert.Equal("INVALID_PROVIDER_RESPONSE", exception.Code);
        Assert.Equal("https://billing.cloro.test/credits", handler.Uris.Single()!.AbsoluteUri);
    }

    [Fact]
    public async Task DiagnosticRunReportsAccountLabelAndHintOnly()
    {
        var pool = new InMemoryCloroKeyPool();
        pool.Add("account-a", KeyA, 1);
        pool.Add("account-b", KeyB, 2);
        var handler = new FakeHandler(request => request.Method == HttpMethod.Post
            ? request.Headers.Authorization?.Parameter == KeyA
                ? Status(HttpStatusCode.Forbidden, """{"error":{"code":"INSUFFICIENT_CREDITS"}}""")
                : Json("""{"task":{"id":"diag-1"}}""")
            : Json("""{"status":"COMPLETED","response":{"markdown":"answer"}}"""));

        var result = await Create(handler, pool).RunAsync(new CloroTestRequest(CloroModel.ChatGpt, "Which product?"));

        Assert.Equal("account-b", result.AccountLabel);
        Assert.Equal("2222", result.KeyHint);
        Assert.Equal(KeyB, handler.Authorizations.Last());
        Assert.DoesNotContain(KeyB, JsonSerializer.Serialize(result));
    }

    [Fact]
    public void LeaseNeverPrintsOrSerializesTheKey()
    {
        var lease = new CloroKeyLease(Guid.NewGuid(), "account-a", KeyA, "1111");
        Assert.DoesNotContain(KeyA, lease.ToString());
        Assert.Contains("1111", lease.ToString());
        Assert.DoesNotContain(KeyA, JsonSerializer.Serialize(lease));
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        internal int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            return Json("""{"task":{"id":"late"}}""");
        }
    }

    private sealed class StaticSettings : ICloroSettingsStore
    {
        public Task<CloroConnectionSettings?> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<CloroConnectionSettings?>(new("", "https://cloro.test/v1/async/task", true));
        public Task SaveAsync(CloroConnectionSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
