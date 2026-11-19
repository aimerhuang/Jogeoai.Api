using Jogeoai.Domain;

namespace Jogeoai.Infrastructure;

public sealed partial class AnalyticsQueryStore
{
    /// <inheritdoc />
    public async Task<FanOutView> FanOutAsync(Guid tenantId, Guid siteId, AnalyticsFilterRequest request,
        string? snapshotToken, CancellationToken cancellationToken = default)
    {
        var data = await PrepareDetailedAsync(tenantId, siteId, request, snapshotToken, cancellationToken);
        var queries = await LoadQueriesAsync(data.Snapshot, data.Facts, cancellationToken);
        var metrics = QueryMetrics(data.Facts, queries, data.Configuration.BrandId);
        var engines = data.Facts.GroupBy(f => f.Engine).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new FanOutEngineView(g.Key, QueryMetrics(g.ToArray(),
                queries.Where(q => q.Check.Engine == g.Key).ToArray(), data.Configuration.BrandId))).ToArray();
        return new FanOutView(data.Meta, metrics, engines, DetailedAnalyticsRules.QueryVersion,
            DetailedAnalyticsRules.TokenizerVersion);
    }

    /// <inheritdoc />
    public async Task<AnalyticsPage<FanOutQueryRow>> FanOutQueriesAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, string? snapshotToken, string? cursor, int limit,
        CancellationToken cancellationToken = default)
    {
        ValidateLimit(limit);
        var data = await PrepareDetailedAsync(tenantId, siteId, request, snapshotToken, cancellationToken);
        var offset = DecodeCursor(cursor, data.Snapshot, "fan-out-queries", "events");
        var queries = await LoadQueriesAsync(data.Snapshot, data.Facts, cancellationToken);
        var rows = BuildQueryRows(queries, data.Configuration.BrandId, siteId, data.Snapshot.Token);
        return Page(data.Meta, rows, offset, limit, data.Snapshot, "fan-out-queries", "events");
    }

    /// <inheritdoc />
    public async Task<FanOutQueryDetail?> FanOutQueryDetailAsync(Guid tenantId, Guid siteId,
        string queryHash, AnalyticsFilterRequest request, string? snapshotToken,
        CancellationToken cancellationToken = default)
    {
        ValidateDetailHash(queryHash);
        var data = await PrepareDetailedAsync(tenantId, siteId, request, snapshotToken, cancellationToken);
        var all = await LoadQueriesAsync(data.Snapshot, data.Facts, cancellationToken);
        var matches = all.Where(q => q.Included && q.Hash == queryHash).ToArray();
        if (matches.Length == 0) return null;
        var row = BuildQueryRows(matches, data.Configuration.BrandId, siteId, data.Snapshot.Token).Single();
        var variants = matches.GroupBy(q => (q.Check.PromptId, q.Check.PromptVersionId,
            q.Check.PromptSnapshot)).OrderByDescending(g => g.Count()).ThenBy(g => g.Key.PromptId)
            .Select(g => new FanOutPromptVariant(g.Key.PromptId, g.Key.PromptVersionId,
                g.Key.PromptSnapshot ?? "", g.Select(q => q.Check.CheckId).Distinct().LongCount(),
                g.Select(q => q.Check.CheckId).Distinct().Take(100).ToArray(),
                g.Select(q => q.Check.CheckId).Distinct().Skip(100).Any())).ToArray();
        var allTerms = await LoadTermsAsync(data.Snapshot, data.Facts, cancellationToken);
        var terms = allTerms.Where(t => t.QueryHash == queryHash && matches.Any(q => q.Check.CheckId == t.CheckId))
            .GroupBy(t => (t.Term, t.Relation)).OrderBy(g => g.Key.Term, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Relation, StringComparer.Ordinal)
            .Select(g => new QueryTerm(g.Key.Term, g.Key.Relation)).ToArray();
        var ids = matches.Select(q => q.Check.CheckId).Distinct().ToArray();
        var supported = matches.Any(q => DetailedAnalyticsRules.Terms(q.Original, q.Check.Locale) is not null);
        return new FanOutQueryDetail(data.Meta, row, variants, ids.Take(100).ToArray(), ids.Length > 100,
            supported ? "available" : "unavailable", DetailedAnalyticsRules.TokenizerVersion, terms);
    }

    /// <inheritdoc />
    public async Task<FanOutTermsView> FanOutTermsAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, string? snapshotToken, string? cursor, int limit,
        CancellationToken cancellationToken = default)
    {
        ValidateLimit(limit);
        var data = await PrepareDetailedAsync(tenantId, siteId, request, snapshotToken, cancellationToken);
        var offset = DecodeCursor(cursor, data.Snapshot, "fan-out-terms", "frequency");
        var queries = (await LoadQueriesAsync(data.Snapshot, data.Facts, cancellationToken))
            .Where(q => q.Included).ToArray();
        var keys = queries.Select(q => (q.Check.CheckId, q.Hash)).ToHashSet();
        var terms = (await LoadTermsAsync(data.Snapshot, data.Facts, cancellationToken))
            .Where(t => keys.Contains((t.CheckId, t.QueryHash))).ToArray();
        var lookup = queries.ToDictionary(q => (q.Check.CheckId, q.Hash));
        var rows = terms.GroupBy(t => (t.Term, t.Relation)).Select(g =>
        {
            var checks = g.Select(t => lookup[(t.CheckId, t.QueryHash)].Check).ToArray();
            return new FanOutTermRow(g.Key.Term, g.Key.Relation, g.LongCount(),
                checks.Select(f => f.CheckId).Distinct().LongCount(),
                checks.Select(f => f.PromptId).Distinct().LongCount(), DetailedAnalyticsRules.TokenizerVersion);
        }).OrderByDescending(t => t.QueryEvents).ThenBy(t => t.Term, StringComparer.Ordinal)
            .ThenBy(t => t.Relation, StringComparer.Ordinal).ToArray();
        var page = Page(data.Meta, rows, offset, limit, data.Snapshot, "fan-out-terms", "frequency");
        var unsupported = queries.Where(q => DetailedAnalyticsRules.Terms(q.Original, q.Check.Locale) is null)
            .Select(q => q.Check.CheckId).Distinct().LongCount();
        return new FanOutTermsView(data.Meta, DetailedAnalyticsRules.TokenizerVersion,
            unsupported, page.Items, page.NextCursor);
    }

    /// <inheritdoc />
    public async Task<AnalyticsPage<AnalyticsEvidenceRow>> FanOutEvidenceAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, string? snapshotToken, string? cursor, int limit,
        string? queryHash, Guid? promptId, CancellationToken cancellationToken = default)
    {
        ValidateLimit(limit);
        ValidateDetailHash(queryHash);
        var data = await PrepareDetailedAsync(tenantId, siteId, request, snapshotToken, cancellationToken);
        if (promptId is not null)
            await ValidatePromptIdsAsync(tenantId, siteId, [promptId.Value], [], cancellationToken);
        var scope = $"{queryHash ?? "all"}|{promptId?.ToString() ?? "all"}";
        var offset = DecodeCursor(cursor, data.Snapshot, "fan-out-evidence", scope);
        var ids = (await LoadQueriesAsync(data.Snapshot, data.Facts, cancellationToken))
            .Where(q => q.Included && (queryHash is null || q.Hash == queryHash))
            .Select(q => q.Check.CheckId).ToHashSet();
        var rows = data.Facts.Where(f => ids.Contains(f.CheckId)
                && (promptId is null || f.PromptId == promptId.Value))
            .OrderByDescending(f => f.CapturedAt).ThenBy(f => f.CheckId).Select(Evidence).ToArray();
        return Page(data.Meta, rows, offset, limit, data.Snapshot, "fan-out-evidence", scope);
    }

    private static FanOutQueryRow[] BuildQueryRows(IReadOnlyList<QueryFact> queries, Guid brandId,
        Guid siteId, string token)
        => queries.Where(q => q.Included).GroupBy(q => q.Hash).Select(g =>
        {
            var ordered = g.OrderBy(q => q.Check.CapturedAt).ThenBy(q => q.Check.CheckId).ToArray();
            var analyzed = ordered.Where(q => q.Check.AnswerPresent && q.Check.AnalysisStatus == "completed"
                && !q.Check.ConfigurationMissing).ToArray();
            return new FanOutQueryRow(g.Key, ordered[0].Original, g.LongCount(),
                g.Select(q => q.Check.CheckId).Distinct().LongCount(),
                g.Select(q => q.Check.PromptId).Distinct().LongCount(),
                MetricRatio.Of(analyzed.Count(q => q.Check.MentionedSubjects.Contains(brandId)),
                    analyzed.Length),
                ordered.Select(q => q.Check.CapturedAt).Min(),
                ordered.Select(q => q.Check.CapturedAt).Max(), QueryEvidencePath(siteId, g.Key, token));
        }).OrderByDescending(q => q.Events).ThenBy(q => q.QueryHash, StringComparer.Ordinal).ToArray();
}
