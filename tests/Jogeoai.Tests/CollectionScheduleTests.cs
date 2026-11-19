using System.Collections.Concurrent;
using System.Text.Json;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Jogeoai.Providers.Abstractions;
using Jogeoai.Worker;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jogeoai.Tests;

public sealed class CollectionScheduleTests
{
    [Fact]
    public async Task SlotIsSkippedForThePoolOnlyWhenEveryEnabledKeyIsOutOfCreditsOrInvalid()
    {
        var exhausted = new InMemoryCloroKeyPool();
        exhausted.Add("account-a", "key-aaaa-1111", status: "exhausted");
        exhausted.Add("account-b", "key-bbbb-2222", status: "invalid");
        Assert.True(await CollectionScheduleStore.IsKeyPoolExhaustedAsync(exhausted, default));

        // A 429 cooldown passes within seconds; skipping would lose the whole day's slot.
        var busy = new InMemoryCloroKeyPool();
        busy.Add("account-a", "key-aaaa-1111", status: "exhausted");
        busy.Add("account-b", "key-bbbb-2222", status: "cooling");
        Assert.False(await CollectionScheduleStore.IsKeyPoolExhaustedAsync(busy, default));

        var available = new InMemoryCloroKeyPool();
        available.Add("account-a", "key-aaaa-1111", status: "exhausted");
        available.Add("account-b", "key-bbbb-2222");
        Assert.False(await CollectionScheduleStore.IsKeyPoolExhaustedAsync(available, default));

        // No enabled key is a configuration problem for the task path, and an unreadable or
        // unmigrated pool is advisory only: neither may skip the slot as exhausted.
        var disabled = new InMemoryCloroKeyPool();
        disabled.Add("account-a", "key-aaaa-1111", enabled: false);
        Assert.False(await CollectionScheduleStore.IsKeyPoolExhaustedAsync(disabled, default));
        Assert.False(await CollectionScheduleStore.IsKeyPoolExhaustedAsync(null, default));
        var unmigrated = new InMemoryCloroKeyPool
        {
            AvailabilityFailure = new CollectorStoreException("CLORO_KEY_POOL_SCHEMA_NOT_MIGRATED", "022", 503),
        };
        Assert.False(await CollectionScheduleStore.IsKeyPoolExhaustedAsync(unmigrated, default));
    }

    [Fact]
    public async Task ExhaustedCloroPoolSkipsOnlySelectionsThatContainACloroTarget()
    {
        var exhausted = new InMemoryCloroKeyPool();
        exhausted.Add("account-a", "key-aaaa-1111", status: "exhausted");
        var configured = new FakeProviderSettings(Configured: true);
        CloroVisibilityTarget cloro = new("cloro", "chatgpt", "zh-CN", "US", "{}"), doubao = new("doubao_api", "doubao", "zh-CN", "CN", "{}");

        var skip = await CollectionScheduleStore.EvaluateProviderAvailabilityAsync([cloro, doubao], exhausted, configured, default);
        Assert.Equal("CLORO_KEY_POOL_EXHAUSTED", skip?.Code);
        // A Doubao-only plan does not depend on the Cloro key pool at all.
        Assert.Null(await CollectionScheduleStore.EvaluateProviderAvailabilityAsync([doubao], exhausted, configured, default));
        Assert.Null(await CollectionScheduleStore.EvaluateProviderAvailabilityAsync([doubao], null, configured, default));
        Assert.Equal(0, configured.CloroLookups);
    }

    [Fact]
    public async Task UnconfiguredDoubaoSkipsTheSlotAsTargetUnavailableBeforeAnyReservation()
    {
        CloroVisibilityTarget cloro = new("cloro", "chatgpt", "zh-CN", "US", "{}"), doubao = new("doubao_api", "doubao", "zh-CN", "CN", "{}");
        var healthy = new InMemoryCloroKeyPool();
        healthy.Add("account-a", "key-aaaa-1111");

        var unconfigured = await CollectionScheduleStore.EvaluateProviderAvailabilityAsync([cloro, doubao], healthy, new FakeProviderSettings(Configured: false), default);
        Assert.Equal("TARGET_UNAVAILABLE", unconfigured?.Code);
        Assert.Contains("豆包", unconfigured?.Message);
        var unmigrated = new FakeProviderSettings(Configured: true)
        {
            Failure = new CollectorStoreException("ANSWER_PROVIDER_SCHEMA_NOT_MIGRATED", "025", 503),
        };
        Assert.Equal("TARGET_UNAVAILABLE", (await CollectionScheduleStore.EvaluateProviderAvailabilityAsync([doubao], healthy, unmigrated, default))?.Code);
        // Cloro-only plans never consult the Doubao settings, and a missing settings store is advisory only.
        Assert.Null(await CollectionScheduleStore.EvaluateProviderAvailabilityAsync([cloro], healthy, new FakeProviderSettings(Configured: false), default));
        Assert.Null(await CollectionScheduleStore.EvaluateProviderAvailabilityAsync([doubao], healthy, null, default));
    }

    [Fact]
    public void SpringGap_UsesNextValidLocalMinuteButOriginalSlotKey()
    {
        var zone = CollectionScheduleCalendar.ParseTimeZone("America/New_York");
        var time = CollectionScheduleCalendar.ParseLocalTime("02:30");
        var due = CollectionScheduleCalendar.NextAfter("daily", null, time, zone,
            new DateTimeOffset(2026, 3, 8, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero), due);
        var window = CollectionScheduleCalendar.DueWindow("daily", null, time, zone, due, due);
        Assert.Equal("20260308T0230", window.LocalSlotKey);
        Assert.Equal(new DateTimeOffset(2026, 3, 9, 6, 30, 0, TimeSpan.Zero), window.NextSlotAt);
    }

    [Fact]
    public void AutumnRepeatedHour_HasOneSlotAtEarlierInstant()
    {
        var zone = CollectionScheduleCalendar.ParseTimeZone("America/New_York");
        var time = CollectionScheduleCalendar.ParseLocalTime("01:30");
        var first = CollectionScheduleCalendar.NextAfter("daily", null, time, zone,
            new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), first);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 6, 30, 0, TimeSpan.Zero),
            CollectionScheduleCalendar.NextAfter("daily", null, time, zone, first));
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 6, 30, 0, TimeSpan.Zero),
            CollectionScheduleCalendar.NextAfter("daily", null, time, zone,
                new DateTimeOffset(2026, 11, 1, 6, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Outage_CatchesUpOnlyNewestDueSlotAndCountsOlderSlots()
    {
        var zone = CollectionScheduleCalendar.ParseTimeZone("UTC");
        var result = CollectionScheduleCalendar.DueWindow("daily", null,
            CollectionScheduleCalendar.ParseLocalTime("08:00"), zone,
            new DateTimeOffset(2026, 9, 20, 8, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal(new DateTimeOffset(2026, 9, 23, 8, 0, 0, TimeSpan.Zero), result.LatestDueAt);
        Assert.Equal(new DateTimeOffset(2026, 9, 24, 8, 0, 0, TimeSpan.Zero), result.NextSlotAt);
        Assert.Equal(3, result.MissedSlots);
        Assert.Equal("20260923T0800", result.LocalSlotKey);
    }

    [Fact]
    public async Task PendingSlot_RestartsAfterRunCreationWithoutCreatingSecondRun()
    {
        var schedule = new FakeSchedules { FailFirstCompletion = true };
        var runs = new FakeRuns();
        var worker = new CollectionScheduleWorker(NullLogger<CollectionScheduleWorker>.Instance, schedule, runs);

        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.ProcessOnceAsync(DateTimeOffset.UtcNow));
        Assert.Single(runs.Created);
        Assert.False(schedule.Completed);

        Assert.Equal(1, await worker.ProcessOnceAsync(DateTimeOffset.UtcNow));
        Assert.Single(runs.Created);
        Assert.True(schedule.Completed);
        Assert.Equal(1, runs.Attempts);
    }

    [Fact]
    public async Task ConcurrentWorkers_UseSameIdempotencyKeyForOneSlot()
    {
        var schedule = new FakeSchedules();
        var runs = new FakeRuns();
        var one = new CollectionScheduleWorker(NullLogger<CollectionScheduleWorker>.Instance, schedule, runs);
        var two = new CollectionScheduleWorker(NullLogger<CollectionScheduleWorker>.Instance, schedule, runs);

        await Task.WhenAll(one.ProcessOnceAsync(DateTimeOffset.UtcNow), two.ProcessOnceAsync(DateTimeOffset.UtcNow));

        Assert.Single(runs.Created);
        Assert.True(schedule.Completed);
        Assert.Equal($"schedule:{schedule.Slot.ScheduleId:N}:{schedule.Slot.LocalSlotKey}", runs.Created.Keys.Single());
    }

    [Fact]
    public async Task DisabledSchedule_DoesNotMaterializeOrCreateRun()
    {
        var schedule = new FakeSchedules { Enabled = false };
        var runs = new FakeRuns();
        var worker = new CollectionScheduleWorker(NullLogger<CollectionScheduleWorker>.Instance, schedule, runs);

        Assert.Equal(0, await worker.ProcessOnceAsync(DateTimeOffset.UtcNow));
        Assert.Empty(runs.Created);
        Assert.True(schedule.Skipped);
    }

    [Fact]
    public async Task ArchivedSite_SkipsPendingSlotWithoutCreatingRun()
    {
        var schedule = new FakeSchedules { SiteActive = false };
        var runs = new FakeRuns();
        var worker = new CollectionScheduleWorker(NullLogger<CollectionScheduleWorker>.Instance, schedule, runs);

        Assert.Equal(0, await worker.ProcessOnceAsync(DateTimeOffset.UtcNow));
        Assert.True(schedule.Skipped);
        Assert.Empty(runs.Created);
    }

    [Fact]
    public async Task DisabledAfterRunCommit_StillLinksExistingRun()
    {
        var schedule = new FakeSchedules { FailFirstCompletion = true };
        var runs = new FakeRuns();
        var worker = new CollectionScheduleWorker(NullLogger<CollectionScheduleWorker>.Instance, schedule, runs);

        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.ProcessOnceAsync(DateTimeOffset.UtcNow));
        schedule.Enabled = false;
        Assert.Equal(1, await worker.ProcessOnceAsync(DateTimeOffset.UtcNow));
        Assert.True(schedule.Completed);
        Assert.Single(runs.Created);
    }

    [Fact]
    public void MonitoringSlotPassesPromptTargetsWhileCustomPlansStayCartesian()
    {
        var tenant = Guid.NewGuid(); var site = Guid.NewGuid(); var schedule = Guid.NewGuid();
        var chatgpt = new CloroVisibilityTarget("cloro", "chatgpt", "zh-CN", "US", "{}");
        var gemini = new CloroVisibilityTarget("cloro", "gemini", "zh-CN", "US", "{}");
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var scoped = new CollectionScheduleSelection([a, b], [chatgpt, gemini], 1, [new(b, [CollectionIdentity.TargetKey(gemini)])]);

        var monitoring = CollectionScheduleStore.SlotRunCommand(tenant, site, schedule, PromptMonitoringIdentity.ScheduleName, "20261104T0900", scoped);
        Assert.Equal($"schedule:{schedule:N}:20261104T0900", monitoring.IdempotencyKey);
        Assert.Equal($"schedule:{schedule:N}", monitoring.RequestedBy);
        Assert.True(monitoring.ChargeMonitoringCredits);
        Assert.True(monitoring.RequireEnabledTargets);
        Assert.Same(scoped.PromptTargets, monitoring.PromptTargetKeys);
        Assert.Equal(3, MonitoringMatrix.Expand(monitoring.PromptIds, monitoring.Targets, monitoring.PromptTargetKeys).Count);

        var legacy = CollectionScheduleStore.SlotRunCommand(tenant, site, schedule, PromptMonitoringIdentity.ScheduleName, "20261104T0900",
            scoped with { PromptTargets = null });
        Assert.Null(legacy.PromptTargetKeys);
        Assert.Equal(monitoring with { PromptTargetKeys = null }, legacy);

        var custom = CollectionScheduleStore.SlotRunCommand(tenant, site, schedule, "weekly report", "20261104T0900", scoped);
        Assert.False(custom.ChargeMonitoringCredits);
        Assert.Null(custom.PromptTargetKeys);
    }

    [Fact]
    public void SelectionSnapshotOmitsPromptTargetsUnlessCustomScopesExist()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var prompt = Guid.Parse("00000000-0000-4000-8000-000000000001");
        var legacy = new CollectionScheduleSelection([prompt], [new CloroVisibilityTarget("cloro", "chatgpt", "zh-CN", "US", "{}")]);
        // Byte-identical to the pre-042 snapshot, so MaterializeDue does not bump plan versions after deployment.
        Assert.Equal("""{"promptIds":["00000000-0000-4000-8000-000000000001"],"targets":[{"provider":"cloro","engine":"chatgpt","locale":"zh-CN","country":"US","optionsJson":"{}"}],"sampleCount":1}""",
            JsonSerializer.Serialize(legacy, web));
        var key = CollectionIdentity.TargetKey(legacy.Targets[0]);
        var scoped = legacy with { PromptTargets = [new(prompt, [key])] };
        var json = JsonSerializer.Serialize(scoped, web);
        Assert.Contains($"\"promptTargets\":[{{\"promptId\":\"{prompt}\",\"targetKeys\":[\"{key}\"]}}]", json);
        var roundTrip = JsonSerializer.Deserialize<CollectionScheduleSelection>(json, web)!;
        Assert.Equal(CollectionIdentity.SelectionHash(scoped), CollectionIdentity.SelectionHash(roundTrip));
        // Old snapshots (and old Workers' output) without the member deserialize as the full cartesian selection.
        Assert.Null(JsonSerializer.Deserialize<CollectionScheduleSelection>(JsonSerializer.Serialize(legacy, web), web)!.PromptTargets);
    }

    private sealed class FakeSchedules : ICollectionScheduleStore
    {
        public PendingCollectionSlot Slot { get; } = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "20260926T0900", new CollectionScheduleSelection([Guid.NewGuid()], [new CloroVisibilityTarget("cloro", "chatgpt")], 2), 0);
        public bool Enabled { get; set; } = true;
        public bool SiteActive { get; set; } = true;
        public bool FailFirstCompletion { get; set; }
        public bool Completed { get; private set; }
        public bool Skipped { get; private set; }
        private readonly SemaphoreSlim gate = new(1, 1);
        public Task<int> MaterializeDueSlotsAsync(DateTimeOffset nowUtc, int limit, CancellationToken ct = default) => Task.FromResult(Enabled && SiteActive ? 1 : 0);
        public Task<IReadOnlyList<PendingCollectionSlot>> GetPendingSlotsAsync(int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PendingCollectionSlot>>(!Completed && !Skipped ? [Slot] : []);
        public async Task<bool> ProcessSlotAsync(PendingCollectionSlot slot, ICloroVisibilityStore runs, CancellationToken ct = default)
        {
            await gate.WaitAsync(ct);
            try
            {
                if (Completed || Skipped) return false;
                var command = new CreateCloroVisibilityRunCommand(slot.TenantId, slot.SiteId,
                    slot.Selection.PromptIds, slot.Selection.Targets,
                    $"schedule:{slot.ScheduleId:N}:{slot.LocalSlotKey}", $"schedule:{slot.ScheduleId:N}", slot.Selection.SampleCount);
                var existing = await runs.GetExistingRunByKeyAsync(command, ct);
                if (existing is not null)
                {
                    Completed = true;
                    return true;
                }
                if (!Enabled || !SiteActive)
                {
                    Skipped = true;
                    return false;
                }
                await runs.CreateRunAsync(command, ct);
                if (FailFirstCompletion)
                {
                    FailFirstCompletion = false;
                    throw new InvalidOperationException("simulated crash after Run commit");
                }
                Completed = true;
                return true;
            }
            finally { gate.Release(); }
        }
        public Task<CollectionScheduleView> CreateAsync(CreateCollectionScheduleCommand command, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CursorPage<CollectionScheduleView>> ListAsync(Guid tenantId, Guid siteId, string? cursor, int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CollectionScheduleView?> UpdateAsync(UpdateCollectionScheduleCommand command, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed record FakeProviderSettings(bool Configured) : IAnswerProviderSettingsStore
    {
        public CollectorStoreException? Failure { get; init; }
        public int CloroLookups { get; private set; }
        public Task<AnswerProviderSettingsView?> GetAsync(string provider, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AnswerProviderConfiguration?> GetConfigurationAsync(string provider, CancellationToken ct = default)
        {
            if (provider == "cloro") CloroLookups++;
            Assert.Equal("doubao_api", provider);
            if (Failure is not null) throw Failure;
            return Task.FromResult(Configured
                ? new AnswerProviderConfiguration("doubao_api", "https://ark.test/api/v3/responses", "model", "secret-fixture", 180, new())
                : null);
        }
        public Task<AnswerProviderSettingsView> SaveAsync(string provider, SaveAnswerProviderSettingsRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeRuns : ICloroVisibilityStore
    {
        public ConcurrentDictionary<string, VisibilityRunCreated> Created { get; } = new();
        public int Attempts;
        public Task<Guid?> GetExistingRunByKeyAsync(CreateCloroVisibilityRunCommand expected, CancellationToken ct = default) =>
            Task.FromResult<Guid?>(Created.TryGetValue(expected.IdempotencyKey, out var run) ? run.RunId : null);
        public Task<VisibilityRunCreated> CreateRunAsync(CreateCloroVisibilityRunCommand command, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Attempts);
            return Task.FromResult(Created.GetOrAdd(command.IdempotencyKey,
                _ => new VisibilityRunCreated(Guid.NewGuid(), "queued",
                    command.PromptIds.Count * command.Targets.Count * command.SampleCount, DateTimeOffset.UtcNow)));
        }
        public Task<VisibilityRunSummary?> GetRunAsync(Guid tenantId, Guid runId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetRunProviderAsync(Guid tenantId, Guid runId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CursorPage<CloroVisibilityCheckView>> GetChecksAsync(Guid tenantId, Guid runId, string? cursor, int limit, string? engine, string? status, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CloroVisibilityCheckView?> GetCheckAsync(Guid tenantId, Guid checkId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CloroTaskLease>> LeaseTasksAsync(string workerId, int limit = 1, int leaseSeconds = 90, string provider = "cloro", CancellationToken ct = default) => throw new NotSupportedException();
        public Task MarkSubmittingAsync(CloroTaskLease task, string workerId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RecordSubmissionAsync(CloroTaskLease task, string workerId, CloroSubmittedTask submitted, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RecordPollAsync(CloroTaskLease task, string workerId, CloroTaskStatusSnapshot snapshot, TimeSpan delay, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CompleteTaskAsync(CloroTaskLease task, string workerId, CloroAnswerSnapshot answer, int durationMs, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CompleteSynchronousTaskAsync(CloroTaskLease task, string workerId, CloroAnswerSnapshot answer, long durationMs, string externalId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task FailTaskAsync(CloroTaskLease task, string workerId, string errorCode, bool retryable, TimeSpan? retryAfter = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeferTaskAsync(CloroTaskLease task, string workerId, string errorCode, DateTimeOffset nextAttemptAt, bool creditsExhausted, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> ResetForResubmissionAsync(CloroTaskLease task, string workerId, int maxResubmits, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> RecoverExpiredTasksAsync(string provider = "cloro", CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> RecoverTimedOutTasksAsync(Guid runId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
