using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jogeoai.Application;
using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

/// <summary>在已发布分析快照下，将真实 Citation 事件关联到文章发布链接。</summary>
public sealed class ArticleMonitoringQuery(IAnalyticsQueryStore analytics, MySqlDatabase database,
    ICitationDomainTypeStore? domainTypes = null) : IArticleMonitoringQuery
{
    private readonly ICitationDomainTypeStore domainTypeStore = domainTypes ?? new CitationDomainTypeStore(database);

    private sealed record SnapshotInfo(Guid ReleaseId, long ArrivalWatermark, long AnalysisWatermark,
        DateTimeOffset AsOf, DateTimeOffset ExpiresAt, CitationsView Citations, TimeZoneInfo Zone,
        string UrlVersion);

    private sealed record PublicationLink(Guid ArticleId, Guid LinkId, string UrlHash, string Url,
        string Status, DateTimeOffset CreatedAt, DateTimeOffset? DisabledAt, string Domain);

    private sealed record MonitoringRow(Guid ArticleId, Guid PublicationLinkId, Guid CheckId, Guid PromptId,
        string PromptSnapshot, string Provider, string Engine, string? Locale, string? Country,
        DateTimeOffset CapturedAt, string UrlHash, string Url, int Position, string? Title,
        string CaptureMode, string EvidenceClass, string Domain);

    internal sealed record MonitoringCursor(string SnapshotToken, string Scope, long CapturedTicks,
        Guid CheckId, Guid PublicationLinkId, string? UrlHash = null, string? UrlVersion = null,
        string? PublicationScope = null, string? ScopeVersion = null);

    /// <inheritdoc />
    public async Task<ArticleMonitoringSummaryView> SummaryAsync(Guid tenantId, Guid siteId, Guid? articleId,
        DateTimeOffset from, DateTimeOffset to, string? snapshotToken, CancellationToken cancellationToken = default)
    {
        if (articleId is not null)
            await AssertArticleAsync(tenantId, siteId, articleId.Value, cancellationToken);
        if (snapshotToken is not null)
        {
            // A token is the authority for its original window. This also
            // makes a summary request with omitted from/to parameters stable
            // while the controller's rolling default advances.
            var restored = await analytics.CitationsAsync(tenantId, siteId,
                new AnalyticsFilterRequest(), snapshotToken, cancellationToken);
            from = restored.Meta.Filters.From;
            to = restored.Meta.Filters.To;
        }
        var snapshot = await PrepareSnapshotAsync(tenantId, siteId, from, to, snapshotToken, cancellationToken);
        from = snapshot.Citations.Meta.Filters.From;
        to = snapshot.Citations.Meta.Filters.To;
        var publications = await LoadPublicationLinksAsync(tenantId, siteId, articleId, cancellationToken);
        var rows = await LoadMonitoringRowsAsync(tenantId, siteId, articleId, snapshot, from, to, cancellationToken);
        var events = Deduplicate(rows);
        var eligible = snapshot.Citations.EligibleChecks;
        var quality = snapshot.Citations.Meta.PendingAnalysisChecks > 0
            ? "ANALYSIS_PENDING"
            : eligible == 0 ? "NO_ELIGIBLE_CHECKS"
            : snapshot.Citations.Meta.MissingCapturedAtChecks > 0 ? "MISSING_CAPTURED_AT" : "READY";
        long? citationEvents = eligible == 0 ? null : events.LongCount();
        long? articleCitationCount = eligible == 0 ? null : articleId is null
            ? events.Select(row => (row.ArticleId, row.CheckId)).Distinct().LongCount()
            : events.Select(row => row.CheckId).Distinct().LongCount();
        var trend = events.GroupBy(row => DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(row.CapturedAt, snapshot.Zone).DateTime))
            .Select(group => new ArticleTrendPoint(group.Key, group.LongCount()))
            .OrderBy(point => point.Date).ToArray();
        var sourceGroups = events.GroupBy(SourceKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.LongCount(), StringComparer.Ordinal);
        var domains = events.GroupBy(row => row.Domain, StringComparer.Ordinal)
            .Select(group =>
            {
                var registered = publications.Where(link => link.Domain == group.Key && link.Status == "active").ToArray();
                return new ArticleDomainRankingView(group.Key, group.LongCount(),
                    registered.Select(link => link.ArticleId).Distinct().LongCount(), registered.LongCount());
            })
            .OrderByDescending(item => item.CitationEvents).ThenBy(item => item.Domain, StringComparer.Ordinal).ToArray();
        return new ArticleMonitoringSummaryView(siteId, articleId, from, to, quality,
            snapshot.Citations.Meta.SnapshotToken, snapshot.Citations.Meta.ExpiresAt,
            eligible, citationEvents, articleCitationCount,
            publications.Select(link => link.ArticleId).Distinct().LongCount(),
            publications.Count(link => link.Status == "active"), trend, sourceGroups, domains,
            eligible == 0 ? null : events.Select(row => row.ArticleId).Distinct().LongCount());
    }

    /// <inheritdoc />
    public async Task<CursorPage<ArticleMonitoringEvidenceView>> EvidenceAsync(Guid tenantId, Guid siteId,
        Guid? articleId, DateTimeOffset from, DateTimeOffset to, string? cursor, int limit,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 100);
        if (articleId is not null)
            await AssertArticleAsync(tenantId, siteId, articleId.Value, cancellationToken);
        // The first page creates a snapshot. Subsequent pages must restore the
        // exact snapshot embedded in the opaque cursor; creating a new snapshot
        // here could move the arrival/analysis watermarks between pages.
        var snapshotToken = ReadSnapshotToken(cursor);
        if (snapshotToken is not null)
        {
            // The controller's omitted from/to defaults are evaluated again on
            // every HTTP request. Restore the immutable range from the cursor's
            // snapshot so a delayed next-page request cannot mismatch merely
            // because the current time moved forward.
            var restored = await analytics.CitationsAsync(tenantId, siteId,
                new AnalyticsFilterRequest(), snapshotToken, cancellationToken);
            from = restored.Meta.Filters.From;
            to = restored.Meta.Filters.To;
        }
        var snapshot = await PrepareSnapshotAsync(tenantId, siteId, from, to, snapshotToken, cancellationToken);
        from = snapshot.Citations.Meta.Filters.From;
        to = snapshot.Citations.Meta.Filters.To;
        var scope = articleId?.ToString("N") ?? "all";
        var publications = await LoadPublicationLinksAsync(tenantId, siteId, articleId, cancellationToken);
        var publicationScope = PublicationScope(articleId, publications);
        var scopeVersion = await LoadMonitorScopeVersionAsync(tenantId, siteId, articleId, cancellationToken);
        var decoded = DecodeCursor(cursor, snapshot.Citations.Meta.SnapshotToken, scope, snapshot.UrlVersion,
            publicationScope, scopeVersion);
        var rows = Deduplicate(await LoadMonitoringRowsAsync(tenantId, siteId, articleId, snapshot, from, to, cancellationToken))
            .OrderByDescending(row => row.CapturedAt)
            .ThenByDescending(row => row.CheckId)
            .ThenByDescending(row => row.PublicationLinkId)
            .ThenByDescending(row => row.UrlHash, StringComparer.Ordinal)
            .Where(row => decoded is null || IsAfterCursor(row, decoded))
            .Take(limit + 1).ToArray();
        var hasNext = rows.Length > limit;
        var pageRows = rows.Take(limit).Select(row => new ArticleMonitoringEvidenceView(
            row.ArticleId, row.PublicationLinkId, row.CheckId, row.PromptId, row.PromptSnapshot,
            row.Provider, row.Engine, row.Locale, row.Country, row.CapturedAt, row.Url,
            row.Position, row.Title, null,
            $"/api/v1/visibility-checks/{row.CheckId}?snapshotToken={Uri.EscapeDataString(snapshot.Citations.Meta.SnapshotToken)}",
            Null(row.CaptureMode)))
            .ToArray();
        string? next = null;
        if (hasNext && pageRows.Length > 0)
        {
            var last = rows[pageRows.Length - 1];
            next = EncodeCursor(new MonitoringCursor(snapshot.Citations.Meta.SnapshotToken, scope,
                last.CapturedAt.UtcTicks, last.CheckId, last.PublicationLinkId, last.UrlHash, snapshot.UrlVersion,
                publicationScope, scopeVersion));
        }
        return new CursorPage<ArticleMonitoringEvidenceView>(pageRows, next);
    }

    /// <inheritdoc />
    public async Task<ArticleDomainRankingPage> DomainRankingsAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, string? snapshotToken, string? q, string? cursor, int limit,
        CancellationToken cancellationToken = default)
    {
        var token = string.IsNullOrWhiteSpace(snapshotToken) ? null : snapshotToken.Trim();
        if (token is null)
        {
            // Freeze a new citation-domain-summary snapshot.  Only time window,
            // zone and engines vary; the store applies its required full-source
            // defaults (016 rejects anything else with INVALID_FILTER) and, when
            // from/to are omitted, the same brand-time-zone 30-local-day window as
            // GET analytics/citation-domain-summary.
            var summary = await analytics.CitationDomainSummaryAsync(tenantId, siteId,
                new AnalyticsFilterRequest(From: request.From, To: request.To, TimeZone: request.TimeZone,
                    Engines: request.Engines), null, cancellationToken);
            token = summary.Meta.SnapshotToken;
        }
        // Rows, cursor and total come from the same frozen projection as the
        // overview's domain module; the cursor is already bound to token + q.
        var page = await analytics.CitationDomainSummaryDomainsAsync(tenantId, siteId, token, q, null, null,
            cursor, limit, cancellationToken);
        var publications = await LoadPublicationLinksAsync(tenantId, siteId, null, cancellationToken);
        var registered = RegisteredByCanonicalHost(publications.Select(link => (link.ArticleId, link.Url, link.Status)));
        var items = page.Items.Select(row =>
        {
            registered.TryGetValue(row.Domain, out var counts);
            return new ArticleDomainRankingRow(row.Domain, row.WebsiteCategory, row.Ownership, row.CitationEvents,
                row.EventShare, row.AnswerCount, counts.Articles, counts.Links);
        }).ToArray();
        return new ArticleDomainRankingPage(page.Meta, items, page.NextCursor, page.FilteredDomainCount);
    }

    private const int RecommendedChannelPageSize = 100;
    private const int RecommendedChannelMaxPages = 50;

    /// <inheritdoc />
    public async Task<ArticleRecommendedChannelList> RecommendedChannelsAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, int limit, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 20) throw new CollectorStoreException("INVALID_LIMIT", "limit 必须在 1–20。", 400);
        var summary = await analytics.CitationDomainSummaryAsync(tenantId, siteId,
            new AnalyticsFilterRequest(From: request.From, To: request.To, TimeZone: request.TimeZone,
                Engines: request.Engines), null, cancellationToken);
        var token = summary.Meta.SnapshotToken;
        // Merging subdomains needs the complete domain set, so walk every page
        // of the frozen projection (already cached in memory by the store).
        var rows = new List<CitationDomainSummaryRow>();
        string? cursor = null;
        for (var pages = 0; pages < RecommendedChannelMaxPages; pages++)
        {
            var page = await analytics.CitationDomainSummaryDomainsAsync(tenantId, siteId, token, null, null, null,
                cursor, RecommendedChannelPageSize, cancellationToken);
            rows.AddRange(page.Items);
            cursor = page.NextCursor;
            if (cursor is null) break;
        }
        var index = await domainTypeStore.LoadAsync(cancellationToken);
        return new ArticleRecommendedChannelList(summary.Meta, RankRecommendedChannels(rows, index, limit));
    }

    /// <summary>
    /// 只保留字典类型为媒体平台、归属非自有且有引用的域名；按平台标识（为空时按命中的规则域名）合并，
    /// 按引用事件降序、名称序数升序取前 limit 个。品牌官网、电商B2B、其他与未分类都不返回。
    /// </summary>
    internal static IReadOnlyList<ArticleRecommendedChannel> RankRecommendedChannels(
        IEnumerable<CitationDomainSummaryRow> rows, CitationDomainTypeIndex index, int limit)
        => rows
            .Where(row => row.Ownership != CitationOwnership.Owned && row.CitationEvents > 0)
            .Select(row => (Row: row, Rule: index.Match(row.Domain)))
            .Where(item => item.Rule?.SiteType == CitationDomainSiteType.MediaPlatform)
            .GroupBy(item => item.Rule!.PlatformKey is { Length: > 0 } key ? "platform:" + key : "rule:" + item.Rule.Domain,
                StringComparer.Ordinal)
            .Select(group =>
            {
                var domains = group.OrderByDescending(item => item.Row.CitationEvents)
                    .ThenBy(item => item.Row.Domain, StringComparer.Ordinal).ToArray();
                // 图标与回退链接取引用最多的规则域名；名称、发布入口取组内任一非空值（同平台的规则取值一致）。
                var rules = domains.Select(item => item.Rule!).ToArray();
                var top = rules[0];
                var name = rules.Select(rule => rule.PlatformName).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                var url = rules.Select(rule => rule.PublishUrl).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                return (Key: top.PlatformKey is { Length: > 0 } key ? key : null, Name: name ?? top.Domain,
                    IconDomain: top.Domain, Url: url ?? $"https://{top.Domain}",
                    Events: domains.Sum(item => item.Row.CitationEvents),
                    Domains: domains.Select(item => item.Row.Domain).ToArray());
            })
            .OrderByDescending(item => item.Events)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .Take(limit)
            .Select((item, rank) => new ArticleRecommendedChannel(rank + 1, item.Key, item.Name,
                item.IconDomain, item.Url, item.Events, item.Domains))
            .ToArray();

    /// <summary>
    /// 按 canonical-host-ownership-v1 主机键对有效发布链接分组。链接 URL 已经过发布规范化
    /// （与引用事件同一规范化器，已去除一个前导 www.），因此按已规范化处理，避免 www.www 二次剥离。
    /// </summary>
    internal static IReadOnlyDictionary<string, (long Articles, long Links)> RegisteredByCanonicalHost(
        IEnumerable<(Guid ArticleId, string Url, string Status)> links)
        => links.Where(link => string.Equals(link.Status, "active", StringComparison.OrdinalIgnoreCase))
            .Select(link => (link.ArticleId, Host: CitationDomainRules.HostFromUrl(link.Url, alreadyNormalized: true)))
            .Where(link => link.Host is not null)
            .GroupBy(link => link.Host!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => (group.Select(link => link.ArticleId).Distinct().LongCount(), group.LongCount()),
                StringComparer.Ordinal);

    private async Task<SnapshotInfo> PrepareSnapshotAsync(Guid tenantId, Guid siteId,
        DateTimeOffset from, DateTimeOffset to, string? snapshotToken, CancellationToken cancellationToken)
    {
        if (to <= from) throw new CollectorStoreException("INVALID_FILTER", "监控时间范围无效。", 400);
        // Empty source lists intentionally request all configured providers and
        // capture modes.  The analytics store still validates the token's
        // release, filters, and expiry before we read its watermarks below.
        var request = new AnalyticsFilterRequest(from, to, Providers: [], CaptureModes: [], EvidenceClasses: [],
            AggregationMode: "observed");
        var citations = await analytics.CitationsAsync(tenantId, siteId, request, snapshotToken, cancellationToken);
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        var row = await ReadRowAsync(connection, """
            SELECT BIN_TO_UUID(release_id),arrival_watermark,analysis_watermark,as_of,expires_at
            FROM analytics_snapshots
            WHERE token_hash=@hash AND tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site)
              AND expires_at>UTC_TIMESTAMP(6);
            """, cancellationToken, ("@hash", Hash(citations.Meta.SnapshotToken)),
            ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()));
        if (row is null) throw new CollectorStoreException("SNAPSHOT_EXPIRED", "分析快照已过期，请重新查询。", 409);
        var zone = ResolveTimeZone(citations.Meta.Filters.TimeZone);
        return new SnapshotInfo(Guid.Parse(row[0]), long.Parse(row[1], CultureInfo.InvariantCulture),
            long.Parse(row[2], CultureInfo.InvariantCulture), ParseDate(row[3]), ParseDate(row[4]), citations, zone,
            string.IsNullOrWhiteSpace(citations.UrlVersion) ? DetailedAnalyticsRules.UrlV1 : citations.UrlVersion);
    }

    private async Task<IReadOnlyList<PublicationLink>> LoadPublicationLinksAsync(Guid tenantId, Guid siteId,
        Guid? articleId, CancellationToken cancellationToken)
    {
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        var rows = await ReadRowsAsync(connection, """
            SELECT BIN_TO_UUID(p.article_id),BIN_TO_UUID(p.id),r.url_hash,r.url_normalized,p.status,
                   p.created_at,p.disabled_at
            FROM content_publication_links p
            JOIN content_references r ON r.tenant_id=p.tenant_id AND r.id=p.reference_id
            WHERE p.tenant_id=UUID_TO_BIN(@tenant) AND p.site_id=UUID_TO_BIN(@site)
              AND (@article IS NULL OR p.article_id=UUID_TO_BIN(@article));
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()),
            ("@article", articleId?.ToString()));
        return rows.Select(row =>
        {
            var url = row[3];
            var domain = Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed.IdnHost.ToLowerInvariant() : string.Empty;
            return new PublicationLink(Guid.Parse(row[0]), Guid.Parse(row[1]), row[2], url, row[4],
                ParseDate(row[5]), string.IsNullOrWhiteSpace(row[6]) ? null : ParseDate(row[6]), domain);
        }).ToArray();
    }

    private async Task AssertArticleAsync(Guid tenantId, Guid siteId, Guid articleId,
        CancellationToken cancellationToken)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            SELECT 1 FROM content_articles
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND id=UUID_TO_BIN(@article)
            LIMIT 1;
            """, connection);
        command.Parameters.AddWithValue("@tenant", tenantId.ToString());
        command.Parameters.AddWithValue("@site", siteId.ToString());
        command.Parameters.AddWithValue("@article", articleId.ToString());
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
            throw new CollectorStoreException("RESOURCE_NOT_FOUND", "文章不存在。", 404);
    }

    /// <summary>
    /// 「我的引用」唯一门槛（FROM … WHERE，不含结尾分号）：引用事件的 url_hash 命中本品牌发布链接的
    /// content_references.url_hash，且发布时间不晚于采集时间、停用后不计、满足文章监控范围窗口，
    /// 同一已发布 release、到达与分析水位、时间窗口内。内容管理文章监控与引用域名详情共用，口径只有一份。
    /// 参数：@tenant @site @release @arrival @analysis @from @to；别名 p r c arrival analysis ce rel article monitor_scope。
    /// </summary>
    internal const string OwnCitationGateSql = """
            FROM content_publication_links p
            JOIN content_references r ON r.tenant_id=p.tenant_id AND r.id=p.reference_id
            JOIN visibility_checks c ON c.tenant_id=p.tenant_id AND c.site_id=p.site_id
            JOIN analytics_check_arrivals arrival ON arrival.tenant_id=c.tenant_id
              AND arrival.site_id=c.site_id AND arrival.check_id=c.id
            JOIN check_analysis analysis ON analysis.tenant_id=c.tenant_id AND analysis.site_id=c.site_id
              AND analysis.check_id=c.id AND analysis.release_id=UUID_TO_BIN(@release)
              AND analysis.completed_event_id IS NOT NULL AND analysis.completed_event_id<=@analysis
            JOIN citation_events ce ON ce.release_id=UUID_TO_BIN(@release)
              AND ce.check_id=c.id AND ce.url_hash=r.url_hash
            JOIN analysis_releases rel ON rel.id=ce.release_id AND rel.tenant_id=c.tenant_id
              AND rel.site_id=c.site_id AND rel.status='published'
            JOIN content_articles article ON article.tenant_id=p.tenant_id AND article.site_id=p.site_id
              AND article.id=p.article_id
            LEFT JOIN content_monitor_scopes monitor_scope ON monitor_scope.tenant_id=article.tenant_id
              AND monitor_scope.site_id=article.site_id AND monitor_scope.article_id=article.id
            WHERE p.tenant_id=UUID_TO_BIN(@tenant) AND p.site_id=UUID_TO_BIN(@site)
              AND arrival.id<=@arrival AND c.captured_at IS NOT NULL
              AND c.captured_at>=@from AND c.captured_at<@to
              AND COALESCE(p.published_at,p.created_at)<=c.captured_at
              AND (p.status='active' OR p.disabled_at IS NULL OR c.captured_at<p.disabled_at)
              AND (
                    (monitor_scope.id IS NULL AND article.monitor_status='active')
                    OR (monitor_scope.id IS NOT NULL AND monitor_scope.filter_snapshot IS NULL
                        AND article.monitor_status='active'
                        AND (monitor_scope.enabled_at IS NULL OR c.captured_at>=monitor_scope.enabled_at))
                    OR (monitor_scope.id IS NOT NULL AND EXISTS (
                        SELECT 1
                        FROM JSON_TABLE(
                            COALESCE(JSON_EXTRACT(monitor_scope.filter_snapshot,'$.activeWindows'), JSON_ARRAY()),
                            '$[*]' COLUMNS (
                                window_from DATETIME(6) PATH '$.from',
                                window_to DATETIME(6) PATH '$.to' NULL ON EMPTY
                            )
                        ) AS monitor_window
                        WHERE c.captured_at>=monitor_window.window_from
                          AND (monitor_window.window_to IS NULL OR c.captured_at<monitor_window.window_to)
                    ))
              )
            """;

    private async Task<IReadOnlyList<MonitoringRow>> LoadMonitoringRowsAsync(Guid tenantId, Guid siteId,
        Guid? articleId, SnapshotInfo snapshot, DateTimeOffset from, DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        var rows = await ReadRowsAsync(connection, """
            SELECT DISTINCT BIN_TO_UUID(p.article_id),BIN_TO_UUID(p.id),BIN_TO_UUID(c.id),BIN_TO_UUID(c.prompt_id),
                   c.prompt_snapshot,c.provider,c.engine,c.locale,c.country,c.captured_at,
                   ce.url_hash,ce.normalized_url,ce.first_position,ce.title,c.capture_mode,c.evidence_class,ce.domain
            """ + "\n" + OwnCitationGateSql + "\n" + """
              AND (@article IS NULL OR p.article_id=UUID_TO_BIN(@article))
            ORDER BY c.captured_at DESC,BIN_TO_UUID(c.id) DESC,BIN_TO_UUID(p.id) DESC;
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()),
            ("@article", articleId?.ToString()), ("@release", snapshot.ReleaseId.ToString()),
            ("@arrival", snapshot.ArrivalWatermark), ("@analysis", snapshot.AnalysisWatermark),
            ("@from", from.UtcDateTime), ("@to", to.UtcDateTime));
        return rows.Select(row => new MonitoringRow(Guid.Parse(row[0]), Guid.Parse(row[1]), Guid.Parse(row[2]),
            Guid.Parse(row[3]), row[4], row[5], row[6], Null(row[7]), Null(row[8]), ParseDate(row[9]), row[10],
            row[11], int.Parse(row[12], CultureInfo.InvariantCulture), Null(row[13]), row[14], row[15], row[16])).ToArray();
    }

    private static IReadOnlyList<MonitoringRow> Deduplicate(IReadOnlyList<MonitoringRow> rows)
        => rows.GroupBy(row => (row.ArticleId, row.CheckId, row.UrlHash))
            .Select(group => group.OrderBy(item => item.Position).First()).ToArray();

    private static string SourceKey(MonitoringRow row)
        => $"{row.Provider}|{row.CaptureMode}|{row.EvidenceClass}";

    private static bool IsAfterCursor(MonitoringRow row, MonitoringCursor cursor)
    {
        var ticks = row.CapturedAt.UtcTicks;
        return ticks < cursor.CapturedTicks
            || ticks == cursor.CapturedTicks && (row.CheckId.CompareTo(cursor.CheckId) < 0
                || row.CheckId == cursor.CheckId && (row.PublicationLinkId.CompareTo(cursor.PublicationLinkId) < 0
                    || row.PublicationLinkId == cursor.PublicationLinkId
                        && string.CompareOrdinal(row.UrlHash, cursor.UrlHash ?? string.Empty) < 0));
    }

    internal static MonitoringCursor? DecodeCursor(string? value, string snapshotToken, string scope,
        string? urlVersion = null, string? publicationScope = null, string? scopeVersion = null)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var cursor = JsonSerializer.Deserialize<MonitoringCursor>(Convert.FromBase64String(value));
            if (cursor is null || !string.Equals(cursor.SnapshotToken, snapshotToken, StringComparison.Ordinal)
                || cursor.Scope != scope || cursor.CapturedTicks < 0
                || urlVersion is not null && !string.Equals(cursor.UrlVersion, urlVersion, StringComparison.Ordinal)
                || publicationScope is not null
                    && !string.Equals(cursor.PublicationScope, publicationScope, StringComparison.Ordinal)
                || scopeVersion is not null
                    && !string.Equals(cursor.ScopeVersion, scopeVersion, StringComparison.Ordinal))
                throw new FormatException();
            return cursor;
        }
        catch (Exception) { throw new CollectorStoreException("INVALID_CURSOR", "文章监控游标无效。", 400); }
    }

    internal static string EncodeCursor(MonitoringCursor cursor)
        => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(cursor));

    /// <summary>从文章监控游标中提取用于恢复快照的 token。</summary>
    internal static string? ReadSnapshotToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            if (value.Length > 4096) throw new FormatException();
            var cursor = JsonSerializer.Deserialize<MonitoringCursor>(Convert.FromBase64String(value));
            if (cursor is null || cursor.SnapshotToken.Length is < 20 or > 200)
                throw new FormatException();
            return cursor.SnapshotToken;
        }
        catch (Exception) { throw new CollectorStoreException("INVALID_CURSOR", "文章监控游标无效。", 400); }
    }

    private static TimeZoneInfo ResolveTimeZone(string zone)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(zone); }
        catch { return TimeZoneInfo.Utc; }
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string PublicationScope(Guid? articleId, IReadOnlyList<PublicationLink> links)
        => Hash(JsonSerializer.Serialize(new
        {
            ArticleId = articleId,
            Links = links.OrderBy(link => link.ArticleId).ThenBy(link => link.LinkId)
                .Select(link => new { link.ArticleId, link.LinkId, link.UrlHash, link.Status, link.CreatedAt, link.DisabledAt })
        }));

    private async Task<string> LoadMonitorScopeVersionAsync(Guid tenantId, Guid siteId, Guid? articleId,
        CancellationToken cancellationToken)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var rows = await ReadRowsAsync(connection, """
            SELECT BIN_TO_UUID(a.id),a.monitor_status,COALESCE(s.version,0),
                   COALESCE(DATE_FORMAT(s.enabled_at,'%Y-%m-%d %H:%i:%s.%f'),''),
                   COALESCE(DATE_FORMAT(s.disabled_at,'%Y-%m-%d %H:%i:%s.%f'),'')
            FROM content_articles a
            LEFT JOIN content_monitor_scopes s ON s.tenant_id=a.tenant_id AND s.site_id=a.site_id
              AND s.article_id=a.id
            WHERE a.tenant_id=UUID_TO_BIN(@tenant) AND a.site_id=UUID_TO_BIN(@site)
              AND (@article IS NULL OR a.id=UUID_TO_BIN(@article))
            ORDER BY a.id;
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()),
            ("@article", articleId?.ToString()));
        return Hash(JsonSerializer.Serialize(rows));
    }

    private static string? Null(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static DateTimeOffset ParseDate(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static async Task<List<string[]>> ReadRowsAsync(MySqlConnection connection, string sql,
        CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = new MySqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<string[]>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new string[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
                row[i] = reader.IsDBNull(i) ? string.Empty : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? string.Empty;
            rows.Add(row);
        }
        return rows;
    }

    private static async Task<string[]?> ReadRowAsync(MySqlConnection connection, string sql,
        CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
        => (await ReadRowsAsync(connection, sql, cancellationToken, parameters)).FirstOrDefault();
}
