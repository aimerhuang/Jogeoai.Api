using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

public sealed partial class AnalyticsQueryStore
{
    private sealed record CitationChangeAggregate(string Kind, string Key, string EvidenceKey,
        long CurrentEvents, long PreviousEvents, string? CurrentTitle, string? PreviousTitle);

    private sealed class CitationChangeAggregateBuilder(string key, string evidenceKey)
    {
        private CitationFact? currentTitle;
        private CitationFact? previousTitle;
        public long CurrentEvents { get; private set; }
        public long PreviousEvents { get; private set; }

        public void Add(CitationFact fact, bool current, bool trackTitle)
        {
            if (current) CurrentEvents++;
            else PreviousEvents++;
            if (!trackTitle || string.IsNullOrWhiteSpace(fact.Title)) return;
            if (current)
            {
                if (NewerTitle(fact, currentTitle)) currentTitle = fact;
            }
            else if (NewerTitle(fact, previousTitle)) previousTitle = fact;
        }

        public CitationChangeAggregate Build(string kind)
            => new(kind, key, evidenceKey, CurrentEvents, PreviousEvents,
                currentTitle?.Title, previousTitle?.Title);

        private static bool NewerTitle(CitationFact candidate, CitationFact? previous)
            => previous is null || candidate.Check.CapturedAt > previous.Check.CapturedAt
                || candidate.Check.CapturedAt == previous.Check.CapturedAt
                    && candidate.Check.CheckId.CompareTo(previous.Check.CheckId) < 0;
    }

    private sealed class CitationChangeIds
    {
        private readonly HashSet<Guid> seen = [];
        private readonly List<Guid> ids = [];
        public IReadOnlyList<Guid> InlineIds => ids.Take(100).ToArray();
        public bool Truncated => ids.Count > 100;

        public void Add(Guid id)
        {
            if (ids.Count <= 100 && seen.Add(id)) ids.Add(id);
        }
    }

    /// <inheritdoc />
    public async Task<CitationsView> CitationsAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, string? snapshotToken, CancellationToken cancellationToken = default)
    {
        var data = await PrepareDetailedAsync(tenantId, siteId, request, snapshotToken, cancellationToken);
        var citations = await LoadCitationsAsync(data.Snapshot, data.Facts, cancellationToken);
        var google = await LoadGoogleAsync(data.Snapshot, data.Facts, cancellationToken);
        var total = citations.LongCount();
        var core = AnalyticsProjector.Compute(data.Facts, data.Configuration).Metrics;
        var multipleSources = data.Facts.Select(SourceKey).Distinct(StringComparer.Ordinal).Skip(1).Any();
        var sources = citations.GroupBy(c => c.Category).Select(g =>
            new CitationDistributionRow(g.Key, g.LongCount(), MetricRatio.Of(g.LongCount(), total)))
            .OrderByDescending(g => g.CitationEvents).ThenBy(g => g.Value, StringComparer.Ordinal).ToArray();
        var pages = citations.GroupBy(c => c.PageType).Select(g =>
            new CitationDistributionRow(g.Key, g.LongCount(), MetricRatio.Of(g.LongCount(), total)))
            .OrderByDescending(g => g.CitationEvents).ThenBy(g => g.Value, StringComparer.Ordinal).ToArray();
        var from = LocalDate(data.Snapshot.Filter.From, data.Zone);
        var to = LocalDate(data.Snapshot.Filter.To.AddTicks(-1), data.Zone).AddDays(1);
        var byDate = citations.Where(c => c.Check.CapturedAt is not null)
            .GroupBy(c => LocalDate(c.Check.CapturedAt!.Value, data.Zone))
            .ToDictionary(g => g.Key, g => (Events: g.LongCount(), Checks: g.Select(c => c.Check.CheckId).Distinct().LongCount()));
        var trend = new List<CitationDayView>();
        for (var date = from; date < to; date = date.AddDays(1))
        {
            var count = byDate.GetValueOrDefault(date);
            trend.Add(new CitationDayView(date, count.Events, count.Checks));
        }
        var urlVersion = await GetUrlVersionAsync(data.Snapshot, cancellationToken);
        return new CitationsView(data.Meta, total,
            citations.Select(c => c.Check.CheckId).Distinct().LongCount(),
            citations.Select(c => c.Check.PromptId).Distinct().LongCount(),
            multipleSources ? MetricRatio.Of(0, 0) : core.OwnCitationRate,
            sources, pages, trend, urlVersion,
            DetailedAnalyticsRules.ClassificationVersion, google.LongCount(),
            data.Facts.Count(f => f.Status == "succeeded"
                && f.AnalysisStatus is "pending" or "processing"),
            core.SuccessfulChecks, core.EligibleChecks,
            MetricRatio.Of(citations.Count(c => c.Category == "brand"), total));
    }

    /// <inheritdoc />
    public async Task<AnalyticsPage<CitationUrlRow>> CitationUrlsAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, string? snapshotToken, string? cursor, int limit, string? domain = null,
        CancellationToken cancellationToken = default)
    {
        ValidateLimit(limit);
        var data = await PrepareDetailedAsync(tenantId, siteId, request, snapshotToken, cancellationToken);
        if (domain is not null) domain = NormalizeDomainFilter(domain);
        var scope = domain is null ? "events" : $"domain:{domain}";
        var offset = DecodeCursor(cursor, data.Snapshot, "citation-urls", scope);
        var citations = await LoadCitationsAsync(data.Snapshot, data.Facts, cancellationToken);
        var rankedHashes = await CachedCitationUrlRanksAsync(data.Snapshot, citations, cancellationToken);
        HashSet<string>? domainHashes = domain is null ? null : citations
            .Where(citation => citation.Domain == domain)
            .Select(citation => citation.Hash).ToHashSet(StringComparer.Ordinal);
        var pageHashes = (domainHashes is null ? rankedHashes : rankedHashes.Where(domainHashes.Contains))
            .Skip(offset).Take(limit + 1).ToArray();
        var selected = pageHashes.Take(limit).ToHashSet(StringComparer.Ordinal);
        var byHash = BuildUrlRows(citations.Where(citation => selected.Contains(citation.Hash)).ToArray(),
            siteId, data.Snapshot.Token, citations.Count)
            .ToDictionary(row => row.UrlHash, StringComparer.Ordinal);
        var pageRows = pageHashes.Take(limit).Select(hash => byHash[hash]).ToArray();
        var next = pageHashes.Length > limit
            ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new PageCursor(
                Hash(data.Snapshot.Token), "citation-urls", scope, offset + pageRows.Length))) : null;
        return new AnalyticsPage<CitationUrlRow>(data.Meta, pageRows, next);
    }

    /// <inheritdoc />
    public async Task<AnalyticsPage<CitationDomainRow>> CitationDomainsAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, string? snapshotToken, string? cursor, int limit,
        CancellationToken cancellationToken = default)
    {
        ValidateLimit(limit);
        var data = await PrepareDetailedAsync(tenantId, siteId, request, snapshotToken, cancellationToken);
        var offset = DecodeCursor(cursor, data.Snapshot, "citation-domains", "events");
        var citations = await LoadCitationsAsync(data.Snapshot, data.Facts, cancellationToken);
        var rows = BuildDomainRows(citations, siteId, data.Snapshot.Token);
        return Page(data.Meta, rows, offset, limit, data.Snapshot, "citation-domains", "events");
    }

    /// <inheritdoc />
    public async Task<CitationUrlDetail?> CitationUrlDetailAsync(Guid tenantId, Guid siteId,
        string urlHash, AnalyticsFilterRequest request, string? snapshotToken,
        CancellationToken cancellationToken = default)
    {
        ValidateDetailHash(urlHash);
        var data = await PrepareDetailedAsync(tenantId, siteId, request, snapshotToken, cancellationToken);
        var all = await LoadCitationsAsync(data.Snapshot, data.Facts, cancellationToken);
        var citations = all.Where(c => c.Hash == urlHash).ToArray();
        if (citations.Length == 0) return null;
        var row = BuildUrlRows(citations, siteId, data.Snapshot.Token, all.Count).Single();
        var first = citations.OrderByDescending(c => c.Check.CapturedAt).ThenBy(c => c.Check.CheckId).Take(50).ToArray();
        return new CitationUrlDetail(data.Meta, row,
            await LoadCitationEvidenceAsync(first, data.Snapshot.Token,
                await GetUrlVersionAsync(data.Snapshot, cancellationToken), cancellationToken), citations.Length > first.Length);
    }

    /// <inheritdoc />
    public async Task<CitationDomainDetail?> CitationDomainDetailAsync(Guid tenantId, Guid siteId,
        string domain, AnalyticsFilterRequest request, string? snapshotToken,
        CancellationToken cancellationToken = default)
    {
        domain = NormalizeDomainFilter(domain);
        var data = await PrepareDetailedAsync(tenantId, siteId, request, snapshotToken, cancellationToken);
        var all = await LoadCitationsAsync(data.Snapshot, data.Facts, cancellationToken);
        var citations = all.Where(c => c.Domain == domain).ToArray();
        if (citations.Length == 0) return null;
        var row = BuildDomainRows(all, siteId, data.Snapshot.Token).Single(r => r.Domain == domain);
        var urls = await CitationUrlsAsync(tenantId, siteId, request,
            data.Snapshot.Token, null, 100, domain, cancellationToken);
        var first = citations.OrderByDescending(c => c.Check.CapturedAt).ThenBy(c => c.Check.CheckId).Take(50).ToArray();
        return new CitationDomainDetail(data.Meta, row, urls.Items, urls.NextCursor is not null,
            await LoadCitationEvidenceAsync(first, data.Snapshot.Token,
                await GetUrlVersionAsync(data.Snapshot, cancellationToken), cancellationToken), citations.Length > first.Length,
            $"/api/v1/sites/{siteId}/analytics/citations/urls?domain={Uri.EscapeDataString(domain)}&snapshotToken={data.Snapshot.Token}");
    }

    /// <inheritdoc />
    public async Task<CitationChangesView> CitationChangesAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, string? snapshotToken, int minimumEvents, double declineThreshold,
        string? cursor = null, int limit = 50, CancellationToken cancellationToken = default)
    {
        if (minimumEvents is < 1 or > 1000 || double.IsNaN(declineThreshold)
            || declineThreshold is < 0 or > 1)
            throw Error("INVALID_FILTER", "引用变化阈值无效。", 400);
        ValidateLimit(limit);
        var data = await PrepareDetailedAsync(tenantId, siteId, request, snapshotToken, cancellationToken);
        var from = data.Snapshot.Filter.From;
        var to = data.Snapshot.Filter.To;
        var previousFilter = data.Snapshot.Filter with { From = from - (to - from), To = from };
        var scope = $"{minimumEvents}|{declineThreshold.ToString("R", CultureInfo.InvariantCulture)}";
        var offset = DecodeCursor(cursor, data.Snapshot, "citation-changes", scope);
        var aggregates = await CachedChangeAggregatesAsync(data, previousFilter, cancellationToken);
        var selected = new List<(CitationChangeAggregate Aggregate, string Change, CitationChangeIds Ids)>();
        var skipped = 0;
        var hasMore = false;
        foreach (var aggregate in aggregates)
        {
            if (Math.Max(aggregate.CurrentEvents, aggregate.PreviousEvents) < minimumEvents) continue;
            var change = ChangeKind(aggregate, declineThreshold);
            if (change is null) continue;
            if (skipped++ < offset) continue;
            if (selected.Count == limit) { hasMore = true; break; }
            selected.Add((aggregate, change, new CitationChangeIds()));
        }
        if (selected.Count == 0)
            return new CitationChangesView(data.Meta, previousFilter.From, previousFilter.To,
                minimumEvents, declineThreshold, [], null);
        var urlIds = selected.Where(s => s.Aggregate.Kind == "url")
            .ToDictionary(s => s.Aggregate.Key, s => s.Ids, StringComparer.Ordinal);
        var domainIds = selected.Where(s => s.Aggregate.Kind == "domain")
            .ToDictionary(s => s.Aggregate.Key, s => s.Ids, StringComparer.Ordinal);
        var current = await LoadCitationsAsync(data.Snapshot, data.Facts, cancellationToken);
        var previous = await CachedPreviousCitationsAsync(data, previousFilter, cancellationToken);
        foreach (var fact in current.Concat(previous))
        {
            if (urlIds.TryGetValue(fact.Url, out var urlCollector)) urlCollector.Add(fact.Check.CheckId);
            if (domainIds.TryGetValue(fact.Domain, out var domainCollector)) domainCollector.Add(fact.Check.CheckId);
        }
        var previousEvidenceSnapshot = await CreatePreviousSnapshotAsync(data.Snapshot,
            previousFilter, cancellationToken);
        var changes = selected.Select(s => new CitationChangeRow(s.Aggregate.Kind, s.Aggregate.Key, s.Change,
            s.Aggregate.CurrentEvents, s.Aggregate.PreviousEvents,
            s.Aggregate.PreviousEvents == 0 ? null
                : (double)(s.Aggregate.CurrentEvents - s.Aggregate.PreviousEvents) / s.Aggregate.PreviousEvents,
            s.Aggregate.CurrentTitle, s.Aggregate.PreviousTitle, s.Ids.InlineIds, s.Ids.Truncated,
            CitationChangeEvidencePath(siteId, s.Aggregate.EvidenceKey,
                s.Aggregate.Kind == "domain", data.Snapshot.Token),
            CitationChangeEvidencePath(siteId, s.Aggregate.EvidenceKey,
                s.Aggregate.Kind == "domain", previousEvidenceSnapshot.Token))).ToArray();
        var nextCursor = hasMore
            ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new PageCursor(
                Hash(data.Snapshot.Token), "citation-changes", scope, offset + changes.Length))) : null;
        return new CitationChangesView(data.Meta, previousFilter.From, previousFilter.To,
            minimumEvents, declineThreshold, changes, nextCursor);
    }

    /// <inheritdoc />
    public async Task<AnalyticsPage<CompetitorCitationGapRow>> CompetitorCitationGapsAsync(Guid tenantId,
        Guid siteId, AnalyticsFilterRequest request, string? snapshotToken, string? cursor, int limit,
        CancellationToken cancellationToken = default)
    {
        ValidateLimit(limit);
        var data = await PrepareDetailedAsync(tenantId, siteId, request, snapshotToken, cancellationToken);
        var offset = DecodeCursor(cursor, data.Snapshot, "citation-gaps", "competitor-only");
        var citations = await LoadCitationsAsync(data.Snapshot, data.Facts, cancellationToken);
        var rows = citations.GroupBy(c => c.Check.PromptId).Where(g =>
                g.Any(c => c.Category == "competitor") && !g.Any(c => c.Category == "brand"))
            .Select(g => new CompetitorCitationGapRow(g.Key,
                g.OrderByDescending(c => c.Check.CapturedAt).First().Check.PromptSnapshot,
                g.LongCount(c => c.Category == "competitor"),
                g.Where(c => c.Category == "competitor").Select(c => c.Check.CheckId).Distinct().Take(100).ToArray(),
                g.Select(c => c.Check.CheckId).Distinct().Take(100).ToArray(),
                $"/api/v1/sites/{siteId}/analytics/evidence?evidencePromptId={g.Key}&snapshotToken={data.Snapshot.Token}"))
            .OrderByDescending(g => g.CompetitorCitationEvents).ThenBy(g => g.PromptId).ToArray();
        return Page(data.Meta, rows, offset, limit, data.Snapshot, "citation-gaps", "competitor-only");
    }

    /// <inheritdoc />
    public async Task<AnalyticsPage<GoogleInternalRow>> GoogleInternalAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, string? snapshotToken, string? cursor, int limit,
        CancellationToken cancellationToken = default)
    {
        ValidateLimit(limit);
        var data = await PrepareDetailedAsync(tenantId, siteId, request, snapshotToken, cancellationToken);
        var offset = DecodeCursor(cursor, data.Snapshot, "citation-google", "observed");
        var rows = (await LoadGoogleAsync(data.Snapshot, data.Facts, cancellationToken))
            .OrderByDescending(g => g.Check.CapturedAt).ThenBy(g => g.Check.CheckId).ThenBy(g => g.Position)
            .Select(g => new GoogleInternalRow(g.Check.CheckId, g.Check.PromptId, g.Url, g.Kind,
                g.Query, g.Product, g.Availability, g.Ownership, g.SubjectId, g.Position,
                CheckPath(g.Check.CheckId, data.Snapshot.Token))).ToArray();
        return Page(data.Meta, rows, offset, limit, data.Snapshot, "citation-google", "observed");
    }

    /// <inheritdoc />
    public async Task<AnalyticsPage<AnalyticsEvidenceRow>> CitationEvidenceAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, string? snapshotToken, string? cursor, int limit,
        string? urlHash, string? domain, Guid? promptId, CancellationToken cancellationToken = default)
    {
        ValidateLimit(limit);
        ValidateDetailHash(urlHash);
        if (domain is not null) domain = NormalizeDomainFilter(domain);
        var data = await PrepareDetailedAsync(tenantId, siteId, request, snapshotToken, cancellationToken);
        if (promptId is not null)
            await ValidatePromptIdsAsync(tenantId, siteId, [promptId.Value], [], cancellationToken);
        var scope = $"{urlHash ?? "all"}|{domain ?? "all"}|{promptId?.ToString() ?? "all"}";
        var offset = DecodeCursor(cursor, data.Snapshot, "citation-evidence", scope);
        var ids = (await LoadCitationsAsync(data.Snapshot, data.Facts, cancellationToken))
            .Where(c => (urlHash is null || c.Hash == urlHash) && (domain is null || c.Domain == domain)
                && (promptId is null || c.Check.PromptId == promptId.Value))
            .Select(c => c.Check.CheckId).ToHashSet();
        var rows = data.Facts.Where(f => ids.Contains(f.CheckId))
            .OrderByDescending(f => f.CapturedAt).ThenBy(f => f.CheckId).Select(Evidence).ToArray();
        return Page(data.Meta, rows, offset, limit, data.Snapshot, "citation-evidence", scope);
    }

    private static CitationUrlRow[] BuildUrlRows(IReadOnlyList<CitationFact> citations, Guid siteId, string token,
        long? shareDenominator = null)
    {
        var total = shareDenominator ?? citations.LongCount();
        return citations.GroupBy(c => c.Hash).Select(g =>
        {
            var ordered = g.OrderByDescending(c => c.Check.CapturedAt).ThenBy(c => c.Check.CheckId).ToArray();
            var latest = ordered[0];
            return new CitationUrlRow(g.Key, latest.Url, latest.Domain, LatestTitle(ordered), g.LongCount(),
                MetricRatio.Of(g.LongCount(), total), g.Select(c => c.Check.CheckId).Distinct().LongCount(),
                g.Select(c => c.Check.PromptId).Distinct().LongCount(),
                g.Select(c => c.Check.CapturedAt).Min(), g.Select(c => c.Check.CapturedAt).Max(),
                latest.Ownership, latest.SubjectId, latest.Category, latest.PageType,
                DetailedAnalyticsRules.ClassificationVersion, latest.RuleBasis,
                CitationEvidencePath(siteId, g.Key, false, token));
        }).OrderByDescending(r => r.CitationEvents).ThenBy(r => r.UrlHash, StringComparer.Ordinal).ToArray();
    }

    private static CitationDomainRow[] BuildDomainRows(IReadOnlyList<CitationFact> citations, Guid siteId, string token)
    {
        var total = citations.LongCount();
        return citations.GroupBy(c => c.Domain).Select(g =>
        {
            var latest = g.OrderByDescending(c => c.Check.CapturedAt).First();
            return new CitationDomainRow(g.Key, g.LongCount(), MetricRatio.Of(g.LongCount(), total),
                g.Select(c => c.Check.CheckId).Distinct().LongCount(),
                g.Select(c => c.Check.PromptId).Distinct().LongCount(),
                g.Select(c => c.Check.CapturedAt).Min(), g.Select(c => c.Check.CapturedAt).Max(),
                latest.Ownership, latest.Category, CitationEvidencePath(siteId, g.Key, true, token));
        }).OrderByDescending(r => r.CitationEvents).ThenBy(r => r.Domain, StringComparer.Ordinal).ToArray();
    }

    private async Task<IReadOnlyList<CitationChangeAggregate>> ComputeChangeAggregatesAsync(
        Prepared prepared, AnalyticsFilter previousFilter)
    {
        var currentTask = LoadCitationsAsync(prepared.Snapshot, prepared.Facts, CancellationToken.None);
        var previousTask = CachedPreviousCitationsAsync(prepared, previousFilter, CancellationToken.None);
        await Task.WhenAll(currentTask, previousTask);
        return BuildChangeAggregates(await currentTask, await previousTask);
    }

    private static IReadOnlyList<CitationChangeAggregate> BuildChangeAggregates(
        IReadOnlyList<CitationFact> current, IReadOnlyList<CitationFact> previous)
    {
        var urls = new Dictionary<string, CitationChangeAggregateBuilder>(StringComparer.Ordinal);
        var domains = new Dictionary<string, CitationChangeAggregateBuilder>(StringComparer.Ordinal);
        foreach (var fact in current)
        {
            AddChangeFact(urls, fact.Url, fact.Hash, fact, true, true);
            AddChangeFact(domains, fact.Domain, fact.Domain, fact, true, false);
        }
        foreach (var fact in previous)
        {
            AddChangeFact(urls, fact.Url, fact.Hash, fact, false, true);
            AddChangeFact(domains, fact.Domain, fact.Domain, fact, false, false);
        }
        return domains.Values.Select(value => value.Build("domain"))
            .Concat(urls.Values.Select(value => value.Build("url")))
            .OrderBy(value => value.Kind, StringComparer.Ordinal)
            .ThenBy(value => value.Key, StringComparer.Ordinal).ToArray();
    }

    private static void AddChangeFact(Dictionary<string, CitationChangeAggregateBuilder> buckets,
        string key, string evidenceKey, CitationFact fact, bool current, bool trackTitle)
    {
        if (!buckets.TryGetValue(key, out var bucket))
            buckets[key] = bucket = new CitationChangeAggregateBuilder(key, evidenceKey);
        bucket.Add(fact, current, trackTitle);
    }

    private static string? ChangeKind(CitationChangeAggregate aggregate, double declineThreshold)
    {
        if (aggregate.PreviousEvents == 0) return "new";
        if (aggregate.CurrentEvents == 0) return "disappeared";
        if (aggregate.CurrentTitle is not null && aggregate.PreviousTitle is not null
            && !string.Equals(aggregate.CurrentTitle, aggregate.PreviousTitle, StringComparison.Ordinal))
            return "title_changed";
        if (aggregate.CurrentEvents < aggregate.PreviousEvents
            && (double)(aggregate.PreviousEvents - aggregate.CurrentEvents) / aggregate.PreviousEvents >= declineThreshold)
            return "significant_decline";
        return aggregate.CurrentEvents != aggregate.PreviousEvents ? "count_changed" : null;
    }

    private static string? LatestTitle(IEnumerable<CitationFact> citations)
        => citations.OrderByDescending(c => c.Check.CapturedAt).ThenBy(c => c.Check.CheckId)
            .Select(c => c.Title).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));

    private async Task<Snapshot> CreatePreviousSnapshotAsync(Snapshot source,
        AnalyticsFilter previousFilter, CancellationToken cancellationToken)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var filterJson = JsonSerializer.Serialize(previousFilter);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var insert = new MySqlCommand("""
            INSERT INTO analytics_snapshots (token_hash, tenant_id, site_id, release_id,
                subject_config_version, current_subject_config_version, filter_hash, filter_json,
                arrival_watermark, analysis_watermark, as_of, expires_at)
            SELECT @newHash, s.tenant_id, s.site_id, s.release_id,
                   s.subject_config_version, s.current_subject_config_version, @filterHash, @filter,
                   s.arrival_watermark, s.analysis_watermark, s.as_of, s.expires_at
            FROM analytics_snapshots s
            WHERE s.token_hash = @sourceHash AND s.tenant_id = UUID_TO_BIN(@tenant)
              AND s.site_id = UUID_TO_BIN(@site) AND s.expires_at > UTC_TIMESTAMP(6);
            """, connection);
        insert.Parameters.AddWithValue("@newHash", Hash(token));
        insert.Parameters.AddWithValue("@sourceHash", Hash(source.Token));
        insert.Parameters.AddWithValue("@tenant", source.TenantId.ToString());
        insert.Parameters.AddWithValue("@site", source.SiteId.ToString());
        insert.Parameters.AddWithValue("@filterHash", Hash(filterJson));
        insert.Parameters.AddWithValue("@filter", filterJson);
        if (await insert.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw Error("SNAPSHOT_EXPIRED", "快照已过期，请重新查询。", 409);
        return source with { Token = token, Filter = previousFilter };
    }

    private static string CitationChangeEvidencePath(Guid siteId, string key, bool domain, string token)
        => $"/api/v1/sites/{siteId}/analytics/citations/evidence?{(domain ? "domain" : "urlHash")}={Uri.EscapeDataString(key)}&snapshotToken={token}";

    private static string NormalizeDomainFilter(string domain)
    {
        domain = domain.Trim().ToLowerInvariant();
        if (domain.Length is < 1 or > 253 || Uri.CheckHostName(domain) != UriHostNameType.Dns)
            throw Error("INVALID_FILTER", "引用域名无效。", 400);
        return domain;
    }

    private async Task<IReadOnlyList<CitationEvidenceRow>> LoadCitationEvidenceAsync(
        IReadOnlyList<CitationFact> selected, string token, string urlVersion,
        CancellationToken cancellationToken)
    {
        if (selected.Count == 0) return [];
        var checks = selected.Select(c => c.Check.CheckId).Distinct().ToArray();
        var pairs = selected.Select(c => (c.Check.CheckId, c.Url)).ToHashSet();
        var positions = new Dictionary<(Guid, string), List<int>>();
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var parameters = string.Join(',', checks.Select((_, i) => $"UUID_TO_BIN(@c{i})"));
        await using var command = new MySqlCommand($"""
            SELECT BIN_TO_UUID(check_id), normalized_url, position FROM citations
            WHERE check_id IN ({parameters}) ORDER BY check_id, position;
            """, connection);
        for (var i = 0; i < checks.Length; i++) command.Parameters.AddWithValue($"@c{i}", checks[i].ToString());
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = Guid.Parse(reader.GetString(0));
                var url = DetailedAnalyticsRules.NormalizeUrl(reader.GetString(1), urlVersion);
                if (url is null || !pairs.Contains((id, url))) continue;
                var key = (id, url);
                if (!positions.TryGetValue(key, out var list)) positions[key] = list = [];
                list.Add(reader.GetInt32(2));
            }
        return selected.Select(c => new CitationEvidenceRow(c.Check.CheckId, c.Check.PromptId,
            c.Check.RunId, c.Check.CapturedAt, c.Url, c.Title,
            positions.GetValueOrDefault((c.Check.CheckId, c.Url)) ?? [c.Position],
            CheckPath(c.Check.CheckId, token))).ToArray();
    }
}
