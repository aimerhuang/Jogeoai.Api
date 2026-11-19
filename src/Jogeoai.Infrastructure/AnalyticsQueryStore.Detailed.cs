using System.Globalization;
using System.Text;
using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

public sealed partial class AnalyticsQueryStore
{
    private sealed record QueryFact(AnalyticsCheckFact Check, string Hash, string Key, string Original,
        int Position, bool Included, string? ExclusionReason);
    private sealed record CitationFact(AnalyticsCheckFact Check, string Hash, string Url, string Domain,
        string? Title, int Position, string Category, string PageType, string Ownership,
        Guid? SubjectId, string RuleBasis);
    private sealed record GoogleFact(AnalyticsCheckFact Check, string Url, string Kind, string? Query,
        string? Product, string Availability, string? Ownership, Guid? SubjectId, int Position);
    private sealed record TermFact(Guid CheckId, string QueryHash, string Term, string Relation);

    private async Task<Prepared> PrepareDetailedAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, string? snapshotToken, CancellationToken cancellationToken)
    {
        var data = await PrepareAsync(tenantId, siteId, request, snapshotToken, cancellationToken);
        RequireObserved(data.Snapshot.Filter);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            SELECT detail_version FROM analysis_releases WHERE id = UUID_TO_BIN(@release)
              AND tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);
            """, connection);
        command.Parameters.AddWithValue("@release", data.Snapshot.ReleaseId.ToString());
        command.Parameters.AddWithValue("@tenant", tenantId.ToString());
        command.Parameters.AddWithValue("@site", siteId.ToString());
        if (await command.ExecuteScalarAsync(cancellationToken) is not string version
            || version is not ("detail-v1" or "detail-v2"))
            throw Error("ANALYSIS_VERSION_NOT_READY", "细分分析版本尚未发布，请稍后重试。", 503);
        return data;
    }

    private async Task<string> GetUrlVersionAsync(Snapshot snapshot, CancellationToken cancellationToken)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            SELECT url_normalization_version FROM analysis_releases
            WHERE id = UUID_TO_BIN(@release) AND tenant_id = UUID_TO_BIN(@tenant)
              AND site_id = UUID_TO_BIN(@site);
            """, connection);
        command.Parameters.AddWithValue("@release", snapshot.ReleaseId.ToString());
        command.Parameters.AddWithValue("@tenant", snapshot.TenantId.ToString());
        command.Parameters.AddWithValue("@site", snapshot.SiteId.ToString());
        return (string?)await command.ExecuteScalarAsync(cancellationToken) ?? DetailedAnalyticsRules.UrlV1;
    }

    private Task<IReadOnlyList<QueryFact>> LoadQueriesAsync(Snapshot snapshot,
        IReadOnlyList<AnalyticsCheckFact> facts, CancellationToken cancellationToken)
        => CachedDetailedFactsAsync(queryFactCache, snapshot,
            token => LoadQueriesUncachedAsync(snapshot, facts, token), cancellationToken);

    private async Task<IReadOnlyList<QueryFact>> LoadQueriesUncachedAsync(Snapshot snapshot,
        IReadOnlyList<AnalyticsCheckFact> facts, CancellationToken cancellationToken)
    {
        if (facts.Count == 0) return [];
        var included = facts.ToDictionary(f => f.CheckId);
        var rows = new List<QueryFact>();
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            SELECT BIN_TO_UUID(q.check_id), q.query_hash, q.query_key, q.original_query,
                   q.first_position, q.included, q.exclusion_reason
            FROM visibility_checks c JOIN analytics_check_arrivals ar ON ar.check_id = c.id
            JOIN check_analysis a ON a.check_id = c.id AND a.release_id = UUID_TO_BIN(@release)
            JOIN check_query_events q ON q.check_id = c.id AND q.release_id = a.release_id
            WHERE c.tenant_id = UUID_TO_BIN(@tenant) AND c.site_id = UUID_TO_BIN(@site)
              AND c.captured_at >= @from AND c.captured_at < @to
              AND ar.id <= @arrival AND a.completed_event_id <= @analysis;
            """, connection);
        AddDetailFilter(command, snapshot);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = Guid.Parse(reader.GetString(0));
            if (!included.TryGetValue(id, out var fact)) continue;
            rows.Add(new QueryFact(fact, reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt32(4), reader.GetBoolean(5), NullString(reader, 6)));
        }
        return rows;
    }

    private Task<IReadOnlyList<CitationFact>> LoadCitationsAsync(Snapshot snapshot,
        IReadOnlyList<AnalyticsCheckFact> facts, CancellationToken cancellationToken)
        => CachedDetailedFactsAsync(citationFactCache, snapshot,
            token => LoadCitationsUncachedAsync(snapshot, facts, token), cancellationToken);

    private async Task<IReadOnlyList<CitationFact>> LoadCitationsUncachedAsync(Snapshot snapshot,
        IReadOnlyList<AnalyticsCheckFact> facts, CancellationToken cancellationToken)
    {
        if (facts.Count == 0) return [];
        var included = facts.ToDictionary(f => f.CheckId);
        var rows = new List<CitationFact>();
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            SELECT BIN_TO_UUID(e.check_id), e.url_hash, e.normalized_url, e.domain, e.title,
                   e.first_position, k.source_category, k.page_type, k.ownership,
                   BIN_TO_UUID(k.subject_id), k.rule_basis
            FROM visibility_checks c JOIN analytics_check_arrivals ar ON ar.check_id = c.id
            JOIN check_analysis a ON a.check_id = c.id AND a.release_id = UUID_TO_BIN(@release)
            JOIN citation_events e ON e.check_id = c.id AND e.release_id = a.release_id
            JOIN citation_classifications k ON k.release_id = e.release_id
              AND k.check_id = e.check_id AND k.url_hash = e.url_hash
            WHERE c.tenant_id = UUID_TO_BIN(@tenant) AND c.site_id = UUID_TO_BIN(@site)
              AND c.captured_at >= @from AND c.captured_at < @to
              AND ar.id <= @arrival AND a.completed_event_id <= @analysis;
            """, connection);
        AddDetailFilter(command, snapshot);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = Guid.Parse(reader.GetString(0));
            if (!included.TryGetValue(id, out var fact)) continue;
            rows.Add(new CitationFact(fact, reader.GetString(1), reader.GetString(2), reader.GetString(3),
                NullString(reader, 4), reader.GetInt32(5), reader.GetString(6), reader.GetString(7),
                reader.GetString(8), GuidOrNull(reader, 9), reader.GetString(10)));
        }
        return rows;
    }

    private Task<IReadOnlyList<GoogleFact>> LoadGoogleAsync(Snapshot snapshot,
        IReadOnlyList<AnalyticsCheckFact> facts, CancellationToken cancellationToken)
        => CachedDetailedFactsAsync(googleFactCache, snapshot,
            token => LoadGoogleUncachedAsync(snapshot, facts, token), cancellationToken);

    private async Task<IReadOnlyList<GoogleFact>> LoadGoogleUncachedAsync(Snapshot snapshot,
        IReadOnlyList<AnalyticsCheckFact> facts, CancellationToken cancellationToken)
    {
        if (facts.Count == 0) return [];
        var included = facts.ToDictionary(f => f.CheckId);
        var rows = new List<GoogleFact>();
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            SELECT BIN_TO_UUID(g.check_id), g.normalized_url, g.kind, g.search_query,
                   g.product, g.availability, g.ownership, BIN_TO_UUID(g.subject_id), g.first_position
            FROM visibility_checks c JOIN analytics_check_arrivals ar ON ar.check_id = c.id
            JOIN check_analysis a ON a.check_id = c.id AND a.release_id = UUID_TO_BIN(@release)
            JOIN google_internal_events g ON g.check_id = c.id AND g.release_id = a.release_id
            WHERE c.tenant_id = UUID_TO_BIN(@tenant) AND c.site_id = UUID_TO_BIN(@site)
              AND c.captured_at >= @from AND c.captured_at < @to
              AND ar.id <= @arrival AND a.completed_event_id <= @analysis;
            """, connection);
        AddDetailFilter(command, snapshot);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = Guid.Parse(reader.GetString(0));
            if (!included.TryGetValue(id, out var fact)) continue;
            rows.Add(new GoogleFact(fact, reader.GetString(1), reader.GetString(2), NullString(reader, 3),
                NullString(reader, 4), reader.GetString(5), NullString(reader, 6),
                GuidOrNull(reader, 7), reader.GetInt32(8)));
        }
        return rows;
    }

    private Task<IReadOnlyList<TermFact>> LoadTermsAsync(Snapshot snapshot,
        IReadOnlyList<AnalyticsCheckFact> facts, CancellationToken cancellationToken)
        => CachedDetailedFactsAsync(termFactCache, snapshot,
            token => LoadTermsUncachedAsync(snapshot, facts, token), cancellationToken);

    private async Task<IReadOnlyList<TermFact>> LoadTermsUncachedAsync(Snapshot snapshot,
        IReadOnlyList<AnalyticsCheckFact> facts, CancellationToken cancellationToken)
    {
        if (facts.Count == 0) return [];
        var included = facts.Select(f => f.CheckId).ToHashSet();
        var rows = new List<TermFact>();
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            SELECT BIN_TO_UUID(t.check_id), t.query_hash, t.term, t.relation
            FROM visibility_checks c JOIN analytics_check_arrivals ar ON ar.check_id = c.id
            JOIN check_analysis a ON a.check_id = c.id AND a.release_id = UUID_TO_BIN(@release)
            JOIN query_term_facts t ON t.check_id = c.id AND t.release_id = a.release_id
            WHERE c.tenant_id = UUID_TO_BIN(@tenant) AND c.site_id = UUID_TO_BIN(@site)
              AND c.captured_at >= @from AND c.captured_at < @to
              AND ar.id <= @arrival AND a.completed_event_id <= @analysis;
            """, connection);
        AddDetailFilter(command, snapshot);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = Guid.Parse(reader.GetString(0));
            if (included.Contains(id)) rows.Add(new TermFact(id, reader.GetString(1),
                reader.GetString(2), reader.GetString(3)));
        }
        return rows;
    }

    private static void AddDetailFilter(MySqlCommand command, Snapshot snapshot)
    {
        command.Parameters.AddWithValue("@release", snapshot.ReleaseId.ToString());
        command.Parameters.AddWithValue("@tenant", snapshot.TenantId.ToString());
        command.Parameters.AddWithValue("@site", snapshot.SiteId.ToString());
        command.Parameters.AddWithValue("@from", snapshot.Filter.From.UtcDateTime);
        command.Parameters.AddWithValue("@to", snapshot.Filter.To.UtcDateTime);
        command.Parameters.AddWithValue("@arrival", snapshot.ArrivalWatermark);
        command.Parameters.AddWithValue("@analysis", snapshot.AnalysisWatermark);
    }

    private static void ValidateDetailHash(string? value)
    {
        if (value is null) return;
        if (value.Length != 64 || value.Any(c => !Uri.IsHexDigit(c)))
            throw Error("INVALID_FILTER", "查询标识必须是 64 位十六进制摘要。", 400);
    }

    private static AnalyticsEvidenceRow Evidence(AnalyticsCheckFact f)
        => new(f.CheckId, f.RunId, f.PromptId, f.PromptVersionId, f.TargetId, f.SampleIndex,
            f.CapturedAt, f.Provider, f.Engine, f.Status, f.AnalysisStatus,
            f.MentionedSubjects, f.SnapshotMissing);

    private static FanOutMetrics QueryMetrics(IReadOnlyList<AnalyticsCheckFact> facts,
        IReadOnlyList<QueryFact> all, Guid brandId)
    {
        var queries = all.Where(q => q.Included).ToArray();
        var c = facts.Count(f => f.Status == "succeeded");
        var n = facts.Count(f => f.Status == "succeeded" && f.AnswerPresent
            && f.AnalysisStatus == "completed" && !f.ConfigurationMissing);
        var fIds = queries.Select(q => q.Check.CheckId).ToHashSet();
        var analyzedExpanded = facts.Where(f => fIds.Contains(f.CheckId) && f.AnswerPresent
            && f.AnalysisStatus == "completed"
            && !f.ConfigurationMissing).ToArray();
        var availability = facts.Where(f => f.Status == "succeeded").ToArray();
        return new FanOutMetrics(queries.LongLength, fIds.Count, c, n,
            queries.Select(q => q.Hash).Distinct(StringComparer.Ordinal).LongCount(),
            MetricRatio.Of(queries.LongLength, fIds.Count), MetricRatio.Of(queries.LongLength, c),
            MetricRatio.Of(fIds.Count, c),
            MetricRatio.Of(availability.Count(f => f.QueryAvailability == "available"), c),
            MetricRatio.Of(analyzedExpanded.Count(f => f.MentionedSubjects.Contains(brandId)),
                analyzedExpanded.Length),
            availability.Count(f => f.QueryAvailability == "available"),
            availability.Count(f => f.QueryAvailability == "empty"),
            availability.Count(f => f.QueryAvailability == "not_exposed"),
            availability.Count(f => f.QueryAvailability == "unknown"),
            facts.Count(f => f.Status == "succeeded"
                && f.AnalysisStatus is "pending" or "processing"));
    }

    private static string QueryEvidencePath(Guid siteId, string hash, string token)
        => $"/api/v1/sites/{siteId}/analytics/evidence?queryHash={hash}&snapshotToken={token}";
    private static string CitationEvidencePath(Guid siteId, string key, bool domain, string token)
        => $"/api/v1/sites/{siteId}/analytics/evidence?{(domain ? "domain" : "urlHash")}={Uri.EscapeDataString(key)}&snapshotToken={token}";
    private static string CheckPath(Guid checkId, string token)
        => $"/api/v1/visibility-checks/{checkId}?snapshotToken={token}";
}
