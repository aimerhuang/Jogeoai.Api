using System.Text.RegularExpressions;
using Jogeoai.Domain;

namespace Jogeoai.Infrastructure;

public sealed partial class AnalyticsQueryStore
{
    private static readonly Regex CitationUrlHashPattern = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    /// <inheritdoc />
    public Task<CitationUrlPromptBreakdown> CitationUrlPromptsAsync(Guid tenantId, Guid siteId,
        string? snapshotToken, string? urlHash, string? engine, CancellationToken cancellationToken = default)
        => CitationUrlPromptsAsync(tenantId, siteId, snapshotToken, urlHash, engine, null, cancellationToken);

    /// <inheritdoc />
    public async Task<CitationUrlPromptBreakdown> CitationUrlPromptsAsync(Guid tenantId, Guid siteId,
        string? snapshotToken, string? urlHash, string? engine, string? channel, CancellationToken cancellationToken = default)
    {
        RequireMonitoringSnapshot(snapshotToken);
        var hash = NormalizeCitationUrlHash(urlHash);
        var engineKey = NormalizeCitationEngine(engine);
        // Collected inside the single streaming projection, so every item is one event of the URL row.
        var counted = new List<CitationDomainCountedEvent>();
        var hashes = new Dictionary<string, bool>(StringComparer.Ordinal);
        var data = await PrepareCitationDomainsAsync(tenantId, siteId, new(), snapshotToken,
            CitationChannelSources.Parse(channel), cancellationToken, item =>
        {
            var url = item.Event.Url;
            if (!hashes.TryGetValue(url, out var matches))
                hashes[url] = matches = CitationDomainAccumulator.UrlHash(url) == hash;
            if (matches) counted.Add(item);
        });
        RequireSnapshotEngine(engineKey, data.Base.Snapshot.Filter);
        var row = data.Projection.Urls.FirstOrDefault(r => r.UrlHash == hash)
            ?? throw Error("RESOURCE_NOT_FOUND", "当前快照内没有该引用 URL。", 404);
        var snapshot = data.Base.Snapshot;
        var eligibleIds = CitationDomainEventGate.Eligible(data.Base.Facts, data.Base.Configuration).Keys.ToHashSet();
        var titles = await LoadCitationUrlTitlesAsync(snapshot, data.Meta.UrlNormalizationVersion, [row], eligibleIds,
            cancellationToken);
        var groups = await LoadPromptGroupMapAsync(snapshot, cancellationToken);
        return CitationDomainProjector.UrlPrompts(data.Meta, row.UrlHash, row.Url, titles.GetValueOrDefault(row.UrlHash),
            engineKey, counted, groups);
    }

    /// <summary>Lower-case 64-character hex URL hash (url rows' urlHash); INVALID_FILTER otherwise.</summary>
    internal static string NormalizeCitationUrlHash(string? urlHash)
    {
        var value = urlHash?.Trim().ToLowerInvariant();
        if (value is null || !CitationUrlHashPattern.IsMatch(value))
            throw Error("INVALID_FILTER", "urlHash 必须是六十四位十六进制 URL 摘要。", 400);
        return value;
    }
}
