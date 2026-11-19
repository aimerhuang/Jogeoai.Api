using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Jogeoai.Infrastructure;

namespace Jogeoai.Tests;

// Derived per-token caches are only cleared by ForgetDerived when a token leaves
// preparedCache. A request that finishes after its token was evicted must not
// re-add an entry, or it would never be removed. The URL-rank cache needs no
// database, so it exercises the shared liveness guard directly.
public sealed class AnalyticsDerivedCacheTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Type StoreType = typeof(AnalyticsQueryStore);

    [Fact]
    public async Task UrlRanksAreNotCachedForTokenOutsidePreparedCache()
    {
        var store = NewStore();
        var ranks = await RankAsync(store, "gone", "b", "a", "b");
        Assert.Equal(["b", "a"], ranks);
        Assert.Empty(Cache(store, "citationUrlRankCache"));
    }

    [Fact]
    public async Task UrlRanksAreCachedWhileLiveAndNotReAddedAfterEviction()
    {
        var store = NewStore();
        MarkLive(store, "live");
        Assert.Equal(["b", "a"], await RankAsync(store, "live", "b", "a", "b"));
        Assert.True(Cache(store, "citationUrlRankCache").Contains("live"));

        // Eviction as RememberPrepared does it, then a late request for the same token.
        Cache(store, "preparedCache").Remove("live");
        StoreType.GetMethod("ForgetDerived", Private)!.Invoke(store, ["live"]);
        Assert.Equal(["a"], await RankAsync(store, "live", "a"));
        Assert.False(Cache(store, "citationUrlRankCache").Contains("live"));
    }

    private static AnalyticsQueryStore NewStore() =>
        new(new MySqlDatabase("Server=127.0.0.1;Database=unused"), null!);

    private static IDictionary Cache(AnalyticsQueryStore store, string field) =>
        (IDictionary)StoreType.GetField(field, Private)!.GetValue(store)!;

    private static void MarkLive(AnalyticsQueryStore store, string token) =>
        Cache(store, "preparedCache")[token] = RuntimeHelpers.GetUninitializedObject(Nested("Prepared"));

    private static async Task<IReadOnlyList<string>> RankAsync(AnalyticsQueryStore store, string token,
        params string[] hashes)
    {
        var snapshot = RuntimeHelpers.GetUninitializedObject(Nested("Snapshot"));
        snapshot.GetType().GetProperty("Token")!.SetValue(snapshot, token);
        var factType = Nested("CitationFact");
        var citations = Array.CreateInstance(factType, hashes.Length);
        for (var i = 0; i < hashes.Length; i++)
        {
            var fact = RuntimeHelpers.GetUninitializedObject(factType);
            factType.GetProperty("Hash")!.SetValue(fact, hashes[i]);
            citations.SetValue(fact, i);
        }
        var task = (Task<IReadOnlyList<string>>)StoreType.GetMethod("CachedCitationUrlRanksAsync", Private)!
            .Invoke(store, [snapshot, citations, CancellationToken.None])!;
        return await task;
    }

    private static Type Nested(string name) => StoreType.GetNestedType(name, BindingFlags.NonPublic)!;
}
