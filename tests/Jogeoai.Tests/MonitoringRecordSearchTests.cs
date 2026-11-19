using System.Text;
using System.Text.Json;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;

namespace Jogeoai.Tests;

public sealed class MonitoringRecordSearchTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    public void EmptySearchPreservesDefaultScopeAndPopulation(string? search)
    {
        var normalized = AnalyticsQueryStore.NormalizeMonitoringSearch(search);
        Assert.Null(normalized);
        Assert.Equal("monitoring-records", AnalyticsQueryStore.MonitoringRecordScope(normalized));
        var facts = new[] { Fact("old", 1), Fact(null, 3), Fact("new", 2) };
        Assert.Equal(new[] { facts[1], facts[2], facts[0] },
            AnalyticsQueryStore.FilterMonitoringRecords(facts, normalized));
    }

    [Fact]
    public void SearchUsesLiteralHistoricalPromptBeforePaging()
    {
        var facts = Enumerable.Range(0, 125)
            .Select(i => Fact(i >= 75 ? $"Historical PROMPT %_ [{i}]" : "edited current text", i)).ToArray();
        var normalized = AnalyticsQueryStore.NormalizeMonitoringSearch("  prompt %_ [ ");
        var matches = AnalyticsQueryStore.FilterMonitoringRecords(facts, normalized).ToArray();
        var firstPage = matches.Take(20).ToArray();
        var secondPage = matches.Skip(20).Take(20).ToArray();
        Assert.Equal(50, matches.Length);
        Assert.Equal(facts[124], firstPage[0]);
        Assert.Equal(facts[104], secondPage[0]);
        Assert.Empty(AnalyticsQueryStore.FilterMonitoringRecords(facts,
            AnalyticsQueryStore.NormalizeMonitoringSearch(".*")));
        Assert.Equal(75, AnalyticsQueryStore.FilterMonitoringRecords(facts,
            AnalyticsQueryStore.NormalizeMonitoringSearch("edited current")).Count());
    }

    [Theory]
    [InlineData("中国", "推荐中国品牌")]
    [InlineData("cAfÉ", "Best CAFÉ brands")]
    [InlineData("ＡＩ", "历史ＡＩ记录")]
    public void SearchSupportsUnicodeWithoutWildcardExpansion(string search, string prompt)
        => Assert.Single(AnalyticsQueryStore.FilterMonitoringRecords([Fact(prompt, 0)],
            AnalyticsQueryStore.NormalizeMonitoringSearch(search)));

    [Fact]
    public void SearchLimitIsAppliedAfterTrimming()
    {
        Assert.Equal(new string('A', 200), AnalyticsQueryStore.NormalizeMonitoringSearch($" {new string('a', 200)} "));
        var error = Assert.Throws<CollectorStoreException>(() =>
            AnalyticsQueryStore.NormalizeMonitoringSearch(new string('a', 201)));
        Assert.Equal("INVALID_FILTER", error.Code);
        Assert.Equal(400, error.StatusCode);
    }

    [Fact]
    public void CursorBindsNormalizedSearchAndOriginalSnapshot()
    {
        const string token = "fixed-snapshot-token-for-pure-tests";
        var scope = AnalyticsQueryStore.MonitoringRecordScope(AnalyticsQueryStore.NormalizeMonitoringSearch("  Cafe "));
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
        var cursor = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
        {
            SnapshotHash = hash, Kind = "monitoring-records", Scope = scope, Offset = 20
        }));
        Assert.Equal(20, AnalyticsQueryStore.DecodeDashboardCursor(cursor, token, "monitoring-records",
            AnalyticsQueryStore.MonitoringRecordScope(AnalyticsQueryStore.NormalizeMonitoringSearch("cAFE"))));
        foreach (var differentScope in new[] { "monitoring-records",
                     AnalyticsQueryStore.MonitoringRecordScope(AnalyticsQueryStore.NormalizeMonitoringSearch("other")) })
        {
            var error = Assert.Throws<CollectorStoreException>(() =>
                AnalyticsQueryStore.DecodeDashboardCursor(cursor, token, "monitoring-records", differentScope));
            Assert.Equal("INVALID_CURSOR", error.Code);
        }
        Assert.Throws<CollectorStoreException>(() =>
            AnalyticsQueryStore.DecodeDashboardCursor(cursor, token + "-new", "monitoring-records", scope));
        Assert.Throws<CollectorStoreException>(() =>
            AnalyticsQueryStore.DecodeDashboardCursor(cursor, token, "another-list", scope));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(" \t\r\n ", null)]
    [InlineData(" Mentioned ", "mentioned")]
    [InlineData(" NOT_MENTIONED ", "not_mentioned")]
    [InlineData("UnKnOwN", "unknown")]
    public void MentionNormalizationPreservesNoFilterCompatibility(string? input, string? expected)
    {
        var normalized = AnalyticsQueryStore.NormalizeMonitoringMention(input);
        Assert.Equal(expected, normalized);
        if (normalized is null)
        {
            foreach (var search in new string?[] { null, "HISTORICAL" })
                Assert.Equal(AnalyticsQueryStore.MonitoringRecordScope(search),
                    AnalyticsQueryStore.MonitoringRecordScope(search, normalized));
        }
    }

    [Theory]
    [InlineData("not_applicable")]
    [InlineData("all")]
    [InlineData("branded")]
    [InlineData("mentioned,unknown")]
    public async Task InvalidMentionFailsBeforeAnyListOrDetailDatabaseAccess(string mention)
    {
        var store = new AnalyticsQueryStore(new MySqlDatabase("Server=127.0.0.1;Port=1;Database=not_used;ConnectionTimeout=1"), null!);
        var listError = await Assert.ThrowsAsync<CollectorStoreException>(() =>
            store.MonitoringRecordsAsync(Guid.NewGuid(), Guid.NewGuid(), new AnalyticsFilterRequest(),
                null, null, 50, null, mention));
        var detailError = await Assert.ThrowsAsync<CollectorStoreException>(() =>
            store.MonitoringAnswerDetailAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                "snapshot", null, mention));
        foreach (var error in new[] { listError, detailError })
        {
            Assert.Equal("INVALID_FILTER", error.Code);
            Assert.Equal(400, error.StatusCode);
        }
    }

    [Fact]
    public void MentionAndSearchFilterBeforePagingAndKeepStableOrdering()
    {
        var brand = Guid.NewGuid();
        var facts = Enumerable.Range(0, 125).Select(i => Fact(i % 2 == 0 ? "matching history" : "other", i)
            with { MentionedSubjects = i < 60 ? [brand] : [] }).ToArray();
        var matches = AnalyticsQueryStore.FilterMonitoringRecords(facts,
            AnalyticsQueryStore.NormalizeMonitoringSearch(" MATCHING "), "mentioned", brand).ToArray();
        Assert.Equal(30, matches.Length);
        Assert.Equal(facts[58], matches.Take(20).First());
        Assert.Equal(facts[18], matches.Skip(20).Take(20).First());
        Assert.DoesNotContain(facts[58], facts.OrderByDescending(f => f.CapturedAt).Take(50));
        Assert.Equal(Enumerable.Range(0, 30).Reverse().Select(i => facts[i * 2].CheckId),
            matches.Select(f => f.CheckId));
    }

    [Fact]
    public void CursorBindsMentionAndSearchButEquivalentNormalizedValuesRemainValid()
    {
        const string token = "same-monitoring-snapshot";
        var search = AnalyticsQueryStore.NormalizeMonitoringSearch(" historical ");
        var scope = AnalyticsQueryStore.MonitoringRecordScope(search,
            AnalyticsQueryStore.NormalizeMonitoringMention(" Mentioned "));
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
        var cursor = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
        {
            SnapshotHash = hash, Kind = "monitoring-records", Scope = scope, Offset = 20
        }));
        Assert.Equal(20, AnalyticsQueryStore.DecodeDashboardCursor(cursor, token, "monitoring-records",
            AnalyticsQueryStore.MonitoringRecordScope(AnalyticsQueryStore.NormalizeMonitoringSearch("HISTORICAL"),
                AnalyticsQueryStore.NormalizeMonitoringMention("mentioned"))));
        foreach (var otherScope in new[]
        {
            AnalyticsQueryStore.MonitoringRecordScope(search),
            AnalyticsQueryStore.MonitoringRecordScope(search, "not_mentioned"),
            AnalyticsQueryStore.MonitoringRecordScope(search, "unknown"),
            AnalyticsQueryStore.MonitoringRecordScope("OTHER", "mentioned")
        })
        {
            var error = Assert.Throws<CollectorStoreException>(() =>
                AnalyticsQueryStore.DecodeDashboardCursor(cursor, token, "monitoring-records", otherScope));
            Assert.Equal("INVALID_CURSOR", error.Code);
        }
        var tied = new[] { Fact("same", 1) with { CheckId = Guid.Parse("00000000-0000-0000-0000-000000000001") },
            Fact("same", 1) with { CheckId = Guid.Parse("00000000-0000-0000-0000-000000000002") } };
        Assert.Equal(tied.Reverse(), AnalyticsQueryStore.FilterMonitoringRecords(tied, null, "not_mentioned", Guid.NewGuid()));
    }

    private static AnalyticsCheckFact Fact(string? prompt, int order)
        => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, null, 1,
            DateTimeOffset.Parse("2026-09-29T00:00:00Z").AddMinutes(order), "succeeded", true,
            "completed", false, false, false, [], "cloro", "chatgpt", "US", "en-US",
            "scraped", "observed_surface", [], null, prompt);
}
