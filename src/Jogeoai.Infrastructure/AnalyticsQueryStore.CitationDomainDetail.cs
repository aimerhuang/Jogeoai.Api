using System.Text.Json;
using System.Text.RegularExpressions;
using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

public sealed partial class AnalyticsQueryStore
{
    private const int CitationHashBatchSize = 500;
    private const int CitationUrlSearchMaxLength = 100;
    private static readonly Regex CitationEnginePattern = new("^[a-z0-9][a-z0-9_.-]{0,63}$", RegexOptions.CultureInvariant);

    /// <summary>Active and all-status publication-link URL hashes of the site (url-v2, see content_references).</summary>
    private sealed record PublicationLinkHashes(HashSet<string> All, HashSet<string> Active);

    /// <inheritdoc />
    public Task<CitationDomainDetailView> CitationDomainDetailAsync(Guid tenantId, Guid siteId,
        string? snapshotToken, string? domain, string? engine, CancellationToken cancellationToken = default)
        => CitationDomainDetailAsync(tenantId, siteId, snapshotToken, domain, engine, null, cancellationToken);

    /// <inheritdoc />
    public async Task<CitationDomainDetailView> CitationDomainDetailAsync(Guid tenantId, Guid siteId,
        string? snapshotToken, string? domain, string? engine, string? channel, CancellationToken cancellationToken = default)
    {
        RequireMonitoringSnapshot(snapshotToken);
        // Same key space as domain rows, evidence and /urls: the emitted canonical host.
        var host = CitationDomainRules.CanonicalHost(domain, alreadyCanonical: true)
            ?? throw Error("INVALID_FILTER", "domain 必须是有效的规范化主机域名。", 400);
        var engineKey = NormalizeCitationEngine(engine);
        // Collected inside the single streaming projection, so "all" is exactly the domain row's events.
        var counted = new List<CitationDomainCountedEvent>();
        var data = await PrepareCitationDomainsAsync(tenantId, siteId, new(), snapshotToken,
            CitationChannelSources.Parse(channel), cancellationToken, item => { if (item.Host == host) counted.Add(item); });
        RequireSnapshotEngine(engineKey, data.Base.Snapshot.Filter);
        var links = await LoadPublicationLinkHashesAsync(data.Base.Snapshot, cancellationToken);
        var hashes = counted.Select(item => item.Event.Url).Distinct(StringComparer.Ordinal)
            .Select(CitationDomainAccumulator.UrlHash);
        var own = await LoadOwnCitationPairsAsync(data.Base.Snapshot, hashes, links, cancellationToken);
        var filter = data.Base.Snapshot.Filter;
        return CitationDomainProjector.Detail(data.Meta, host, engineKey, counted, own, filter.From, filter.To,
            data.Base.Zone);
    }

    /// <inheritdoc />
    public Task<CitationUrlSummaryPage> CitationDomainSummaryUrlsAsync(Guid tenantId, Guid siteId,
        string? snapshotToken, string? domain, string? engine, string? q, bool? mine, string? cursor, int limit,
        CancellationToken cancellationToken = default)
        => CitationDomainSummaryUrlsAsync(tenantId, siteId, snapshotToken, domain, engine, q, mine, cursor, limit, null,
            cancellationToken);

    /// <inheritdoc />
    public async Task<CitationUrlSummaryPage> CitationDomainSummaryUrlsAsync(Guid tenantId, Guid siteId,
        string? snapshotToken, string? domain, string? engine, string? q, bool? mine, string? cursor, int limit,
        string? channel, CancellationToken cancellationToken = default)
    {
        RequireMonitoringSnapshot(snapshotToken);
        ValidateLimit(limit);
        string? host = null;
        if (!string.IsNullOrWhiteSpace(domain))
        {
            // Same key space as domain rows and evidence: the emitted canonical host, not a fresh raw host.
            host = CitationDomainRules.CanonicalHost(domain, alreadyCanonical: true);
            if (host is null) throw Error("INVALID_FILTER", "domain 必须是有效的规范化主机域名。", 400);
        }
        var engineKey = NormalizeCitationEngine(engine);
        var search = NormalizeCitationUrlSearch(q);
        if (host is null && (engineKey is not null || search is not null || mine is not null))
            throw Error("INVALID_FILTER", "engine、q、mine 只能与 domain 一起使用。", 400);
        var ranking = engineKey is null ? null : new CitationUrlEngineRanking(engineKey);
        var data = await PrepareCitationDomainsAsync(tenantId, siteId, new(), snapshotToken,
            CitationChannelSources.Parse(channel), cancellationToken, ranking is null ? null : ranking.Add);
        RequireSnapshotEngine(engineKey, data.Base.Snapshot.Filter);
        var snapshot = data.Base.Snapshot;
        var scope = CitationScope(data.Meta, CitationUrlScopeValue(host, engineKey, search, mine));
        var offset = DecodeCursor(cursor, snapshot, "citation-summary-urls", scope);
        // Rank is fixed here (full population, optionally one engine); domain, q and mine never renumber it.
        // domainRank is assigned within the domain after the engine filter and before q/mine.
        IReadOnlyList<CitationUrlSummaryRow> rows = ranking?.Complete() ?? data.Projection.Urls;
        if (host is not null) rows = CitationDomainProjector.WithinDomain(rows, host);
        var eligible = CitationDomainEventGate.Eligible(data.Base.Facts, data.Base.Configuration);
        var eligibleIds = eligible.Keys.ToHashSet();
        var urlVersion = data.Meta.UrlNormalizationVersion;
        IReadOnlyDictionary<string, string>? titles = null;
        if (search is not null)
        {
            // q matches titles, so every candidate URL of the domain needs its title, not just the page.
            titles = await LoadCitationUrlTitlesBatchedAsync(snapshot, urlVersion, rows, eligibleIds, cancellationToken);
            rows = rows.Where(r => CitationDomainProjector.MatchesUrlSearch(r, titles.GetValueOrDefault(r.UrlHash), search))
                .ToArray();
        }
        var links = await LoadPublicationLinkHashesAsync(snapshot, cancellationToken);
        if (mine == true) rows = rows.Where(r => links.Active.Contains(r.UrlHash)).ToArray();
        var page = Page(data.Base.Meta, rows, offset, limit, snapshot, "citation-summary-urls", scope);
        titles ??= await LoadCitationUrlTitlesAsync(snapshot, urlVersion, page.Items, eligibleIds, cancellationToken);
        var checks = await LoadCitationUrlChecksAsync(snapshot, page.Items, eligible, cancellationToken);
        var own = await LoadOwnCitationPairsAsync(snapshot, page.Items.Select(r => r.UrlHash), links, cancellationToken);
        var items = page.Items.Select(row => CitationDomainProjector.WithUrlStats(
            row with { Title = titles.GetValueOrDefault(row.UrlHash) },
            checks.TryGetValue(row.UrlHash, out var facts) ? facts : [], own, links.Active, engineKey)).ToArray();
        return new(data.Meta, items, page.NextCursor, rows.Count);
    }

    /// <summary>Lower-case engine code; null when omitted or blank.</summary>
    internal static string? NormalizeCitationEngine(string? engine)
    {
        if (string.IsNullOrWhiteSpace(engine)) return null;
        var value = engine.Trim().ToLowerInvariant();
        if (!CitationEnginePattern.IsMatch(value))
            throw Error("INVALID_FILTER", "engine 必须是单个有效的平台代码。", 400);
        return value;
    }

    /// <summary>Trimmed literal title/URL search (1–100 UTF-16 units); null when omitted or blank.</summary>
    internal static string? NormalizeCitationUrlSearch(string? q)
    {
        if (string.IsNullOrWhiteSpace(q)) return null;
        var value = q.Trim();
        if (value.Length > CitationUrlSearchMaxLength)
            throw Error("INVALID_FILTER", "标题或 URL 搜索最多一百 UTF-16 字符。", 400);
        return value.ToUpperInvariant();
    }

    /// <summary>
    /// Cursor scope value of the URL board. Without engine/q/mine it is exactly the historical
    /// "domain:{host}" so cursors issued before this change keep working.
    /// </summary>
    internal static string CitationUrlScopeValue(string? host, string? engine, string? search, bool? mine)
        => engine is null && search is null && mine is null ? "domain:" + host
            : "urls:" + JsonSerializer.Serialize(new { Domain = host, Engine = engine, Q = search, Mine = mine });

    /// <summary>A snapshot frozen to some engines only accepts one of them as a sub-filter.</summary>
    private static void RequireSnapshotEngine(string? engine, AnalyticsFilter filter)
    {
        if (engine is not null && filter.Engines.Count > 0 && !filter.Engines.Contains(engine, StringComparer.Ordinal))
            throw Error("INVALID_FILTER", "engine 不在当前快照的平台范围内。", 400);
    }

    private async Task<PublicationLinkHashes> LoadPublicationLinkHashesAsync(Snapshot snapshot,
        CancellationToken cancellationToken)
    {
        var all = new HashSet<string>(StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            SELECT r.url_hash, p.status = 'active'
            FROM content_publication_links p
            JOIN content_references r ON r.tenant_id = p.tenant_id AND r.id = p.reference_id
            WHERE p.tenant_id = UUID_TO_BIN(@tenant) AND p.site_id = UUID_TO_BIN(@site);
            """, connection);
        command.Parameters.AddWithValue("@tenant", snapshot.TenantId.ToString());
        command.Parameters.AddWithValue("@site", snapshot.SiteId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var hash = reader.GetString(0);
            all.Add(hash);
            if (reader.GetBoolean(1)) active.Add(hash);
        }
        return new(all, active);
    }

    /// <summary>
    /// (check, url_hash) pairs passing the shared article-monitoring gate
    /// (<see cref="ArticleMonitoringQuery.OwnCitationGateSql"/>), limited to the given hashes that belong to a
    /// publication link at all, in batches of 500. The caller intersects with its counted events.
    /// </summary>
    private async Task<HashSet<(Guid CheckId, string UrlHash)>> LoadOwnCitationPairsAsync(Snapshot snapshot,
        IEnumerable<string> urlHashes, PublicationLinkHashes links, CancellationToken cancellationToken)
    {
        var result = new HashSet<(Guid CheckId, string UrlHash)>();
        var candidates = urlHashes.Where(links.All.Contains).Distinct(StringComparer.Ordinal).ToArray();
        if (candidates.Length == 0) return result;
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        foreach (var batch in candidates.Chunk(CitationHashBatchSize))
        {
            await using var command = new MySqlCommand(OwnCitationPairSql(batch.Length), connection);
            AddDetailFilter(command, snapshot);
            for (var i = 0; i < batch.Length; i++) command.Parameters.AddWithValue($"@h{i}", batch[i]);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                result.Add((Guid.Parse(reader.GetString(0)), reader.GetString(1)));
        }
        return result;
    }

    internal static string OwnCitationPairSql(int hashCount)
        => "SELECT DISTINCT BIN_TO_UUID(c.id), ce.url_hash\n" + ArticleMonitoringQuery.OwnCitationGateSql + "\n"
            + $"  AND ce.url_hash IN ({string.Join(", ", Enumerable.Range(0, hashCount).Select(i => $"@h{i}"))});";

    /// <summary>Contributing checks of page URLs, with the same window/release/watermark scope as the event reader.</summary>
    internal static string CitationUrlCheckSql(int hashCount) => $"""
            SELECT e.url_hash, e.normalized_url, BIN_TO_UUID(c.id)
            FROM visibility_checks c
            JOIN analytics_check_arrivals ar ON ar.check_id = c.id
            JOIN check_analysis a ON a.check_id = c.id AND a.release_id = UUID_TO_BIN(@release)
            JOIN citation_events e ON e.check_id = c.id AND e.release_id = a.release_id
            WHERE c.tenant_id = UUID_TO_BIN(@tenant) AND c.site_id = UUID_TO_BIN(@site)
              AND c.captured_at >= @from AND c.captured_at < @to
              AND ar.id <= @arrival AND a.completed_event_id <= @analysis
              AND c.status = 'succeeded' AND a.configuration_missing = FALSE
              AND COALESCE(c.answer_present, c.answer IS NOT NULL AND TRIM(c.answer) <> '') = TRUE
              AND e.url_hash IN ({string.Join(", ", Enumerable.Range(0, hashCount).Select(i => $"@h{i}"))});
            """;

    /// <summary>Population-S answers behind each page URL (exact published URL behind the hash only).</summary>
    private async Task<Dictionary<string, List<AnalyticsCheckFact>>> LoadCitationUrlChecksAsync(Snapshot snapshot,
        IReadOnlyList<CitationUrlSummaryRow> rows, IReadOnlyDictionary<Guid, AnalyticsCheckFact> eligible,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, List<AnalyticsCheckFact>>(StringComparer.Ordinal);
        if (rows.Count == 0) return result;
        var urls = rows.ToDictionary(r => r.UrlHash, r => r.Url, StringComparer.Ordinal);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(CitationUrlCheckSql(rows.Count), connection);
        AddDetailFilter(command, snapshot);
        for (var i = 0; i < rows.Count; i++) command.Parameters.AddWithValue($"@h{i}", rows[i].UrlHash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var hash = reader.GetString(0);
            if (!urls.TryGetValue(hash, out var url) || reader.GetString(1) != url
                || !eligible.TryGetValue(Guid.Parse(reader.GetString(2)), out var fact)) continue;
            if (!result.TryGetValue(hash, out var facts)) result[hash] = facts = [];
            facts.Add(fact);
        }
        return result;
    }

    private async Task<IReadOnlyDictionary<string, string>> LoadCitationUrlTitlesBatchedAsync(Snapshot snapshot,
        string urlVersion, IReadOnlyList<CitationUrlSummaryRow> rows, IReadOnlySet<Guid> eligible,
        CancellationToken cancellationToken)
    {
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var batch in rows.Chunk(CitationHashBatchSize))
            foreach (var (hash, title) in await LoadCitationUrlTitlesAsync(snapshot, urlVersion, batch, eligible, cancellationToken))
                titles[hash] = title;
        return titles;
    }

    /// <summary>
    /// Checks of the records snapshot with at least one counted citation event whose domain key equals
    /// <paramref name="host"/>: same eligibility, detailed-completion proof, per-answer URL dedup and
    /// exclusions as the citation-domain projection (shared <see cref="CitationDomainEventGate"/>).
    /// </summary>
    private async Task<HashSet<Guid>> LoadCitingCheckIdsAsync(Prepared data, string host,
        CancellationToken cancellationToken)
    {
        // Same release/rule binding and 503/409 error codes as /domains; also rejects unsupported releases.
        await BindCitationProjectionAsync(data.Snapshot, data.Meta.DataUpdatedAt, cancellationToken);
        var completed = await LoadDetailedCompletionChecksAsync(data.Snapshot, null, cancellationToken);
        var facts = MarkUnprovenDetailedPending(data.Facts, completed);
        var gate = new CitationDomainEventGate(CitationDomainEventGate.Eligible(facts, data.Configuration), data.Configuration);
        var result = new HashSet<Guid>();
        // ORDER BY check_id keeps the gate's per-answer dedup contract.
        await foreach (var item in ReadCitationDomainEventsAsync(data.Snapshot, false, cancellationToken))
            if (gate.Admit(item, out _)?.Host == host) result.Add(item.CheckId);
        return result;
    }
}
