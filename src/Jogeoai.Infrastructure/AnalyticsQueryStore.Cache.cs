using Jogeoai.Domain;

namespace Jogeoai.Infrastructure;

public sealed partial class AnalyticsQueryStore
{
    // A snapshot token identifies immutable release, filter, and arrival/analysis
    // watermarks. Reusing its facts avoids reading the same 108k-row window for
    // every chart request on a dashboard. Keep the cache small because one
    // large window can contain many Check-level facts.
    private const int MaximumCachedSnapshots = 2;
    private readonly object preparedCacheGate = new();
    private readonly Dictionary<string, Prepared> preparedCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<CoreMetrics>> previousMetricsCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<IReadOnlyList<AnalyticsCheckFact>>> previousFactCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<IReadOnlyList<CitationFact>>> previousCitationFactCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<IReadOnlyList<CitationChangeAggregate>>> citationChangeAggregateCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<IReadOnlyList<string>>> citationUrlRankCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<IReadOnlyList<QueryFact>>> queryFactCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<IReadOnlyList<CitationFact>>> citationFactCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<IReadOnlyList<GoogleFact>>> googleFactCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<IReadOnlyList<TermFact>>> termFactCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<IReadOnlyDictionary<string, int>>> citationPreviousRankCache = new(StringComparer.Ordinal);

    private Prepared? CachedPrepared(Guid tenantId, Guid siteId, AnalyticsFilterRequest request,
        string? token)
    {
        if (token is null) return null;
        Prepared? cached;
        lock (preparedCacheGate)
        {
            if (!preparedCache.TryGetValue(token, out cached)) return null;
            if (cached.Snapshot.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                preparedCache.Remove(token);
                ForgetDerived(token);
                return null;
            }
        }
        if (cached.Snapshot.TenantId != tenantId || cached.Snapshot.SiteId != siteId) return null;
        if (!MatchesExplicitFilter(request, cached.Snapshot.Filter))
            throw Error("INVALID_FILTER", "筛选与查询快照不匹配。", 400);
        return cached;
    }

    private void RememberPrepared(Prepared prepared)
    {
        lock (preparedCacheGate)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var expired in preparedCache.Where(entry => entry.Value.Snapshot.ExpiresAt <= now)
                         .Select(entry => entry.Key).ToArray())
            {
                preparedCache.Remove(expired);
                ForgetDerived(expired);
            }
            if (preparedCache.Count >= MaximumCachedSnapshots)
            {
                var oldest = preparedCache.MinBy(entry => entry.Value.Snapshot.AsOf).Key;
                preparedCache.Remove(oldest);
                ForgetDerived(oldest);
            }
            preparedCache[prepared.Snapshot.Token] = prepared;
        }
    }

    private async Task<CoreMetrics> CachedPreviousMetricsAsync(Prepared prepared,
        AnalyticsFilter previousFilter, CancellationToken cancellationToken)
    {
        Task<CoreMetrics>? work = null;
        lock (preparedCacheGate)
        {
            // Same liveness guard as previous facts: never re-add an entry for an evicted token.
            if (preparedCache.ContainsKey(prepared.Snapshot.Token)
                && !previousMetricsCache.TryGetValue(prepared.Snapshot.Token, out work))
            {
                work = ComputePreviousMetricsAsync(prepared, previousFilter);
                previousMetricsCache[prepared.Snapshot.Token] = work;
            }
        }
        if (work is null)
            return await ComputePreviousMetricsAsync(prepared, previousFilter).WaitAsync(cancellationToken);
        try { return await work.WaitAsync(cancellationToken); }
        catch
        {
            if (work.IsFaulted || work.IsCanceled)
                lock (preparedCacheGate)
                    if (previousMetricsCache.TryGetValue(prepared.Snapshot.Token, out var current)
                        && ReferenceEquals(current, work)) previousMetricsCache.Remove(prepared.Snapshot.Token);
            throw;
        }
    }

    private async Task<CoreMetrics> ComputePreviousMetricsAsync(Prepared prepared,
        AnalyticsFilter previousFilter)
    {
        var facts = await CachedPreviousFactsAsync(prepared, previousFilter, CancellationToken.None);
        return AnalyticsProjector.Compute(facts, prepared.Configuration).Metrics;
    }

    private async Task<IReadOnlyList<AnalyticsCheckFact>> CachedPreviousFactsAsync(Prepared prepared,
        AnalyticsFilter previousFilter, CancellationToken cancellationToken)
    {
        Task<IReadOnlyList<AnalyticsCheckFact>>? work = null;
        lock (preparedCacheGate)
        {
            // Memoize only while the token is still a live prepared snapshot. If it was
            // evicted mid-request, ForgetDerived already ran and a late entry would never be removed.
            if (preparedCache.ContainsKey(prepared.Snapshot.Token)
                && !previousFactCache.TryGetValue(prepared.Snapshot.Token, out work))
            {
                work = LoadFactsAsync(prepared.Snapshot with { Filter = previousFilter },
                    prepared.Configuration, CancellationToken.None);
                previousFactCache[prepared.Snapshot.Token] = work;
            }
        }
        if (work is null)
            return await LoadFactsAsync(prepared.Snapshot with { Filter = previousFilter },
                prepared.Configuration, cancellationToken);
        try { return await work.WaitAsync(cancellationToken); }
        catch
        {
            if (work.IsFaulted || work.IsCanceled)
                lock (preparedCacheGate)
                    if (previousFactCache.TryGetValue(prepared.Snapshot.Token, out var current)
                        && ReferenceEquals(current, work)) previousFactCache.Remove(prepared.Snapshot.Token);
            throw;
        }
    }

    private async Task<IReadOnlyList<CitationFact>> CachedPreviousCitationsAsync(Prepared prepared,
        AnalyticsFilter previousFilter, CancellationToken cancellationToken)
    {
        Task<IReadOnlyList<CitationFact>>? work = null;
        lock (preparedCacheGate)
        {
            if (preparedCache.ContainsKey(prepared.Snapshot.Token)
                && !previousCitationFactCache.TryGetValue(prepared.Snapshot.Token, out work))
            {
                work = ComputePreviousCitationsAsync(prepared, previousFilter);
                previousCitationFactCache[prepared.Snapshot.Token] = work;
            }
        }
        if (work is null)
            return await ComputePreviousCitationsAsync(prepared, previousFilter).WaitAsync(cancellationToken);
        try { return await work.WaitAsync(cancellationToken); }
        catch
        {
            if (work.IsFaulted || work.IsCanceled)
                lock (preparedCacheGate)
                    if (previousCitationFactCache.TryGetValue(prepared.Snapshot.Token, out var current)
                        && ReferenceEquals(current, work)) previousCitationFactCache.Remove(prepared.Snapshot.Token);
            throw;
        }
    }

    private async Task<IReadOnlyList<CitationFact>> ComputePreviousCitationsAsync(Prepared prepared,
        AnalyticsFilter previousFilter)
    {
        var facts = await CachedPreviousFactsAsync(prepared, previousFilter, CancellationToken.None);
        return await LoadCitationsAsync(prepared.Snapshot with { Filter = previousFilter }, facts,
            CancellationToken.None);
    }

    private async Task<IReadOnlyList<CitationChangeAggregate>> CachedChangeAggregatesAsync(Prepared prepared,
        AnalyticsFilter previousFilter, CancellationToken cancellationToken)
    {
        Task<IReadOnlyList<CitationChangeAggregate>>? work = null;
        lock (preparedCacheGate)
        {
            if (preparedCache.ContainsKey(prepared.Snapshot.Token)
                && !citationChangeAggregateCache.TryGetValue(prepared.Snapshot.Token, out work))
            {
                work = ComputeChangeAggregatesAsync(prepared, previousFilter);
                citationChangeAggregateCache[prepared.Snapshot.Token] = work;
            }
        }
        if (work is null)
            return await ComputeChangeAggregatesAsync(prepared, previousFilter).WaitAsync(cancellationToken);
        try { return await work.WaitAsync(cancellationToken); }
        catch
        {
            if (work.IsFaulted || work.IsCanceled)
                lock (preparedCacheGate)
                    if (citationChangeAggregateCache.TryGetValue(prepared.Snapshot.Token, out var current)
                        && ReferenceEquals(current, work)) citationChangeAggregateCache.Remove(prepared.Snapshot.Token);
            throw;
        }
    }

    private async Task<IReadOnlyList<string>> CachedCitationUrlRanksAsync(Snapshot snapshot,
        IReadOnlyList<CitationFact> citations, CancellationToken cancellationToken)
    {
        Task<IReadOnlyList<string>>? work = null;
        lock (preparedCacheGate)
        {
            if (preparedCache.ContainsKey(snapshot.Token)
                && !citationUrlRankCache.TryGetValue(snapshot.Token, out work))
            {
                work = Task.Run(() => RankCitationUrls(citations));
                citationUrlRankCache[snapshot.Token] = work;
            }
        }
        if (work is null) return await Task.Run(() => RankCitationUrls(citations), cancellationToken);
        try { return await work.WaitAsync(cancellationToken); }
        catch
        {
            if (work.IsFaulted || work.IsCanceled)
                lock (preparedCacheGate)
                    if (citationUrlRankCache.TryGetValue(snapshot.Token, out var current)
                        && ReferenceEquals(current, work)) citationUrlRankCache.Remove(snapshot.Token);
            throw;
        }
    }

    private static IReadOnlyList<string> RankCitationUrls(IReadOnlyList<CitationFact> citations)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var citation in citations)
        {
            counts.TryGetValue(citation.Hash, out var count);
            counts[citation.Hash] = count + 1;
        }
        return counts.OrderByDescending(entry => entry.Value)
            .ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => entry.Key).ToArray();
    }

    private void ForgetDerived(string token)
    {
        previousMetricsCache.Remove(token);
        previousFactCache.Remove(token);
        previousCitationFactCache.Remove(token);
        citationChangeAggregateCache.Remove(token);
        citationUrlRankCache.Remove(token);
        queryFactCache.Remove(token);
        citationFactCache.Remove(token);
        googleFactCache.Remove(token);
        termFactCache.Remove(token);
        citationPreviousRankCache.Remove(token);
    }

    // Keyed by token alone: a token freezes its filter, so its previous window
    // (From - (To - From), From) and therefore this map can never differ.
    private async Task<IReadOnlyDictionary<string, int>> CachedCitationPreviousRanksAsync(CitationPrepared data,
        CancellationToken cancellationToken)
    {
        var token = data.Base.Snapshot.Token;
        Task<IReadOnlyDictionary<string, int>>? work = null;
        lock (preparedCacheGate)
        {
            // Same liveness guard as previous facts: never re-add an entry for an evicted token.
            if (preparedCache.ContainsKey(token) && !citationPreviousRankCache.TryGetValue(token, out work))
            {
                work = ComputeCitationPreviousRanksAsync(data);
                citationPreviousRankCache[token] = work;
            }
        }
        if (work is null) return await ComputeCitationPreviousRanksAsync(data).WaitAsync(cancellationToken);
        try { return await work.WaitAsync(cancellationToken); }
        catch
        {
            if (work.IsFaulted || work.IsCanceled)
                lock (preparedCacheGate)
                    if (citationPreviousRankCache.TryGetValue(token, out var current)
                        && ReferenceEquals(current, work)) citationPreviousRankCache.Remove(token);
            throw;
        }
    }

    private async Task<IReadOnlyList<T>> CachedDetailedFactsAsync<T>(
        Dictionary<string, Task<IReadOnlyList<T>>> cache, Snapshot snapshot,
        Func<CancellationToken, Task<IReadOnlyList<T>>> load, CancellationToken cancellationToken)
    {
        // Changes compare a second time window under the same token. Its facts
        // must never replace the canonical window's cached list.
        Task<IReadOnlyList<T>>? work = null;
        lock (preparedCacheGate)
        {
            if (preparedCache.TryGetValue(snapshot.Token, out var prepared)
                && prepared.Snapshot.Filter.From == snapshot.Filter.From
                && prepared.Snapshot.Filter.To == snapshot.Filter.To)
            {
                if (!cache.TryGetValue(snapshot.Token, out work))
                {
                    work = load(CancellationToken.None);
                    cache[snapshot.Token] = work;
                }
            }
        }
        if (work is null) return await load(cancellationToken);
        try { return await work.WaitAsync(cancellationToken); }
        catch
        {
            if (work.IsFaulted || work.IsCanceled)
                lock (preparedCacheGate)
                    if (cache.TryGetValue(snapshot.Token, out var current)
                        && ReferenceEquals(current, work)) cache.Remove(snapshot.Token);
            throw;
        }
    }
}
