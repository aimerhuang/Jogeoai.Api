using Jogeoai.Application;
using Jogeoai.Domain;

namespace Jogeoai.Infrastructure;

public sealed partial class AnalyticsQueryStore
{
    /// <summary>全来源口径：全部 Provider、两种采集模式与三类证据（与 AnalyticsController.AllSourcesFilter 相同）。</summary>
    internal static AnalyticsFilterRequest AllSourcesRequest(DateTimeOffset from, DateTimeOffset to, IReadOnlyList<Guid>? promptIds)
        => new(From: from, To: to, Providers: DefaultProviders, CaptureModes: ["api", "scraped"],
            EvidenceClasses: ["api_response", "observed_surface", "unknown"], PromptIds: promptIds);

    /// <inheritdoc />
    public async Task<PromptCitedSourcesView> TopCitedUrlsForPromptAsync(Guid tenantId, Guid siteId, Guid promptId,
        DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken cancellationToken = default)
    {
        ValidateLimit(limit);
        var data = await PrepareDetailedAsync(tenantId, siteId, AllSourcesRequest(from, to, [promptId]), null, cancellationToken);
        var citations = (await LoadCitationsAsync(data.Snapshot, data.Facts, cancellationToken))
            .Where(citation => citation.Check.PromptId == promptId).ToArray();
        return BuildPromptCitedSources(citations.Select(citation => new PromptCitationFact(citation.Hash, citation.Url,
            citation.Domain, citation.Title, citation.Check.Engine, citation.Check.CheckId, citation.Check.CapturedAt,
            citation.Ownership)).ToArray(), limit);
    }

    /// <summary>按 url_hash 聚合引用事件；被引次数按（回答，URL）去重，标题取最近一次非空值。</summary>
    internal static PromptCitedSourcesView BuildPromptCitedSources(IReadOnlyList<PromptCitationFact> facts, int limit)
    {
        var rows = facts.GroupBy(fact => fact.Hash, StringComparer.Ordinal).Select(group =>
        {
            var latestTitle = group.Where(fact => !string.IsNullOrWhiteSpace(fact.Title))
                .OrderByDescending(fact => fact.CapturedAt).ThenBy(fact => fact.CheckId).Select(fact => fact.Title).FirstOrDefault();
            var ownership = group.GroupBy(fact => fact.Ownership, StringComparer.Ordinal)
                .OrderByDescending(item => item.Count()).ThenBy(item => item.Key, StringComparer.Ordinal).First().Key;
            var first = group.OrderBy(fact => fact.Url, StringComparer.Ordinal).First();
            return new PromptCitedUrlRow(first.Url, first.Domain, latestTitle,
                group.Select(fact => fact.Engine).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                group.Select(fact => fact.CheckId).Distinct().Count(), ownership);
        }).OrderByDescending(row => row.CitationCount).ThenBy(row => row.Url, StringComparer.Ordinal).ToArray();
        var engines = facts.Select(fact => fact.Engine).Distinct(StringComparer.Ordinal).Count();
        return new PromptCitedSourcesView(engines, rows.Length, rows.Take(limit).ToArray());
    }

    /// <summary>单个提问的引用事件（纯函数聚合输入）。</summary>
    internal sealed record PromptCitationFact(string Hash, string Url, string Domain, string? Title, string Engine,
        Guid CheckId, DateTimeOffset? CapturedAt, string Ownership);
}
