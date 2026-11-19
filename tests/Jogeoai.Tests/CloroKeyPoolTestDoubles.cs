using System.Collections.Concurrent;
using Jogeoai.Providers.Abstractions;

namespace Jogeoai.Tests;

/// <summary>In-memory key pool: priority order, exclusion and status are enough for adapter/worker tests.</summary>
internal sealed class InMemoryCloroKeyPool : ICloroKeyPool
{
    internal sealed record Entry(CloroKeyLease Lease, int Priority)
    {
        public string Status { get; set; } = "unknown";
        public bool Enabled { get; set; } = true;
        public decimal? Remaining { get; set; }
    }

    private readonly List<Entry> entries = [];
    internal List<(Guid KeyId, Guid? TaskId, CloroKeyRejection Rejection, string Code)> Rejections { get; } = [];
    internal List<(Guid KeyId, Guid? TaskId, string ExternalTaskId)> Used { get; } = [];
    internal ConcurrentQueue<(Guid KeyId, CloroCreditsSnapshot Credits)> Applied { get; } = new();
    internal List<(Guid KeyId, string Code)> CreditFailures { get; } = [];
    internal DateTimeOffset? NextAvailableAt { get; set; }
    internal Exception? PickFailure { get; set; }
    internal Action? OnRejected { get; set; }
    internal Exception? AvailabilityFailure { get; set; }

    internal CloroKeyLease Add(string label, string apiKey, int priority = 100, string status = "unknown", bool enabled = true)
    {
        var lease = new CloroKeyLease(Guid.NewGuid(), label, apiKey, apiKey[^4..]);
        entries.Add(new Entry(lease, priority) { Status = status, Enabled = enabled });
        return lease;
    }

    internal Entry Find(Guid keyId) => entries.Single(e => e.Lease.KeyId == keyId);

    private bool Selectable(Entry entry) => entry.Enabled && entry.Status is "unknown" or "active";

    public Task<CloroKeyLease?> PickAsync(IReadOnlyCollection<Guid> excluded, CancellationToken cancellationToken = default)
    {
        if (PickFailure is not null) throw PickFailure;
        return Task.FromResult(entries.Where(e => Selectable(e) && !excluded.Contains(e.Lease.KeyId))
            .OrderBy(e => e.Priority).Select(e => e.Lease).FirstOrDefault());
    }

    public Task<CloroKeyLease?> GetAsync(Guid? keyId, CancellationToken cancellationToken = default) =>
        Task.FromResult(keyId is { } id ? entries.FirstOrDefault(e => e.Lease.KeyId == id)?.Lease
            : entries.FirstOrDefault(e => e.Lease.AccountLabel == "default")?.Lease);

    public Task<CloroKeyPoolAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default) =>
        AvailabilityFailure is not null ? Task.FromException<CloroKeyPoolAvailability>(AvailabilityFailure)
            : Task.FromResult(new CloroKeyPoolAvailability(entries.Count(e => e.Enabled), entries.Count(Selectable), NextAvailableAt,
                entries.Count(e => e.Enabled && e.Status == "cooling")));

    public Task MarkUsedAsync(Guid keyId, Guid? providerTaskId, string externalTaskId, CancellationToken cancellationToken = default)
    {
        Used.Add((keyId, providerTaskId, externalTaskId));
        return Task.CompletedTask;
    }

    public Task MarkRejectedAsync(Guid keyId, Guid? providerTaskId, CloroKeyRejection rejection, string errorCode,
        CancellationToken cancellationToken = default)
    {
        Rejections.Add((keyId, providerTaskId, rejection, errorCode));
        Find(keyId).Status = rejection switch
        {
            CloroKeyRejection.InsufficientCredits => "exhausted",
            CloroKeyRejection.Unauthorized => "invalid",
            _ => "cooling",
        };
        OnRejected?.Invoke();
        return Task.CompletedTask;
    }

    public Task ApplyCreditsAsync(Guid keyId, CloroCreditsSnapshot credits, CancellationToken cancellationToken = default)
    {
        Applied.Enqueue((keyId, credits));
        if (entries.FirstOrDefault(e => e.Lease.KeyId == keyId) is { } entry)
        {
            entry.Remaining = credits.Remaining;
            if (credits.Remaining <= 0) entry.Status = "exhausted";
        }
        return Task.CompletedTask;
    }

    public Task MarkCreditsFailedAsync(Guid keyId, string errorCode, CancellationToken cancellationToken = default)
    {
        CreditFailures.Add((keyId, errorCode));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CloroKeyLease>> ListRefreshDueAsync(TimeSpan interval, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CloroKeyLease>>(entries.Where(e => e.Enabled).Select(e => e.Lease).ToArray());
}

/// <summary>Credits client returning scripted results per key.</summary>
internal sealed class FakeCreditsClient : ICloroCreditsClient
{
    internal Dictionary<Guid, Func<CloroCreditsSnapshot>> Results { get; } = [];
    internal List<Guid> Calls { get; } = [];

    public Task<CloroCreditsSnapshot> GetCreditsAsync(CloroKeyLease key, CancellationToken cancellationToken = default)
    {
        Calls.Add(key.KeyId);
        return Task.FromResult(Results.TryGetValue(key.KeyId, out var result)
            ? result()
            : new CloroCreditsSnapshot(100, 500, DateTimeOffset.UtcNow.AddDays(10), DateTimeOffset.UtcNow));
    }
}
