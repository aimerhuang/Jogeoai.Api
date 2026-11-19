using System.Globalization;
using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

/// <summary>
/// 文章链接自动发现与每日监控计费的 MySQL 实现（迁移 027）。
/// 发现按 analytics_check_arrivals.id 水位增量扫描已发布分析中 ownership='brand' 的引用；
/// 计费与 019 提问监控同形：轮次 = 预留、项 = 每链接，reserve 账本为负数、capture 账本金额为 0。
/// </summary>
public sealed class ArticleLinkMonitoringStore(
    MySqlDatabase database,
    IManagementStore management,
    IArticleSourceReader sourceReader,
    ILogger<ArticleLinkMonitoringStore> logger) : IArticleLinkMonitoringStore
{
    /// <summary>单批最多处理的候选 URL 数；超过时水位只推进到本批最后一个候选的首次到达。</summary>
    internal const int DiscoveryBatchSize = 200;

    /// <summary>单条标题读取的超时。</summary>
    private static readonly TimeSpan TitleReadTimeout = TimeSpan.FromSeconds(15);

    private const string ReaderVersion = "article-link-discovery-v1";

    /// <summary>标题 GROUP_CONCAT 的分隔符：真实的 U+001F 控制字符（与 ContentStore.TagSeparator 同一做法）。</summary>
    private const string TitleSeparator = "\u001f";

    /// <summary>可计费链接：追踪中且未归档的文章下未停用的发布链接（外层别名 p / a）。</summary>
    private const string BillableLinkWhereSql =
        "p.status='active' AND a.status='active' AND a.monitor_status='active'";

    private sealed record Candidate(string UrlHash, string NormalizedUrl, long FirstArrival, Guid FirstCheck, string? Title);

    private sealed record DiscoveryState(Guid? ReleaseId, long LastArrivalId, DateTimeOffset? BackfillFrom, DateTimeOffset? BackfillCompletedAt);

    /// <inheritdoc />
    public async Task<IReadOnlyList<(Guid TenantId, Guid SiteId)>> ListSitesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        var rows = await RowsAsync(connection, null,
            "SELECT BIN_TO_UUID(tenant_id), BIN_TO_UUID(id) FROM sites WHERE status='active' ORDER BY created_at, id;",
            cancellationToken);
        return rows.Select(row => (Guid.Parse(row[0]), Guid.Parse(row[1]))).ToArray();
    }

    /// <inheritdoc />
    public async Task<ArticleDiscoveryPassResult> DiscoverAsync(Guid tenantId, Guid siteId, DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        var brand = await management.GetBrandAsync(tenantId, siteId, cancellationToken);
        if (brand is null) return new ArticleDiscoveryPassResult(0, 0, 0, "BRAND_NOT_FOUND");
        var zone = ArticleMonitoringCalendar.ResolveZone(brand.TimeZone);
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        var owner = await RowAsync(connection, null, """
            SELECT BIN_TO_UUID(user_id) FROM tenant_members
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND role='owner' AND status='active'
            ORDER BY created_at, user_id LIMIT 1;
            """, cancellationToken, ("@tenant", tenantId.ToString()));
        if (owner is null)
        {
            logger.LogWarning("Citation discovery skipped site {SiteId}: tenant has no active owner.", siteId);
            return new ArticleDiscoveryPassResult(0, 0, 0, "OWNER_NOT_FOUND");
        }
        var ownerId = Guid.Parse(owner[0]);
        var releaseRow = await RowAsync(connection, null, """
            SELECT BIN_TO_UUID(id) FROM analysis_releases
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND status='published'
            ORDER BY version DESC LIMIT 1;
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()));
        if (releaseRow is null) return new ArticleDiscoveryPassResult(0, 0, 0, "RELEASE_NOT_PUBLISHED");
        var releaseId = Guid.Parse(releaseRow[0]);

        var state = await LoadOrResetStateAsync(connection, tenantId, siteId, releaseId, utcNow, zone, cancellationToken);
        var ceilingRow = await RowAsync(connection, null,
            "SELECT COALESCE(MAX(id),0) FROM analytics_check_arrivals WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site);",
            cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()));
        var ceiling = long.Parse(ceilingRow![0], CultureInfo.InvariantCulture);
        var backfillFrom = state.BackfillCompletedAt is null ? state.BackfillFrom : null;
        var candidates = await LoadCandidatesAsync(connection, tenantId, siteId, releaseId, state.LastArrivalId, ceiling,
            backfillFrom, cancellationToken);

        var created = 0; var skippedExisting = 0;
        foreach (var candidate in candidates)
        {
            string normalized;
            try { normalized = ArticleSourceReader.NormalizeAndValidate(candidate.NormalizedUrl); }
            catch (CollectorStoreException exception)
            {
                logger.LogInformation("Citation discovery ignored an unreadable URL on site {SiteId}: {Code}.", siteId, exception.Code);
                continue;
            }
            var ownership = ContentStore.ComputeOwnership(normalized, brand);
            if (ownership is not true)
                logger.LogInformation("Citation discovery found a citation classified as owned whose host no longer matches the brand domains on site {SiteId}; creating it anyway.", siteId);
            if (await CreateDiscoveredArticleAsync(connection, tenantId, siteId, ownerId, candidate, normalized, ownership, cancellationToken))
                created++;
            else skippedExisting++;
        }

        // 未截断：本次扫描到的上限之前的 URL 都已处理。截断时只推进到最后一个候选首次到达的前一个 id，
        // 使与它同一到达（同一回答引用多个品牌 URL）但被 LIMIT 截掉的 URL 下次仍能进入批次；已建文的会被去重跳过。
        // 若整批候选都首次到达于同一个 id（水位无法前进），则接受推进到该 id，避免每次都返回同一批。
        var lastFirstArrival = candidates.Count == 0 ? state.LastArrivalId : candidates[^1].FirstArrival;
        var newWatermark = candidates.Count < DiscoveryBatchSize ? ceiling
            : lastFirstArrival - 1 > state.LastArrivalId ? lastFirstArrival - 1 : lastFirstArrival;
        if (candidates.Count >= DiscoveryBatchSize && newWatermark == lastFirstArrival)
            logger.LogWarning("Citation discovery on site {SiteId} hit {BatchSize} candidates sharing arrival {ArrivalId}; later siblings of that arrival are skipped.",
                siteId, DiscoveryBatchSize, lastFirstArrival);
        var backfillDone = state.BackfillCompletedAt is not null || candidates.Count < DiscoveryBatchSize;
        await ExecAsync(connection, null, """
            UPDATE content_citation_discovery_state
            SET last_arrival_id=@watermark,
                backfill_completed_at=CASE WHEN @done=1 THEN COALESCE(backfill_completed_at,@now) ELSE backfill_completed_at END,
                last_run_at=@now, updated_at=@now
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site);
            """, cancellationToken, ("@watermark", newWatermark), ("@done", backfillDone ? 1 : 0),
            ("@now", utcNow.UtcDateTime), ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()));
        return new ArticleDiscoveryPassResult(candidates.Count, created, skippedExisting);
    }

    /// <inheritdoc />
    public async Task<int> ReadPendingTitlesAsync(Guid tenantId, Guid siteId, int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        var pending = await RowsAsync(connection, null, """
            SELECT BIN_TO_UUID(r.id), r.url_normalized, BIN_TO_UUID(p.article_id), r.title
            FROM content_publication_links p
            JOIN content_references r ON r.tenant_id=p.tenant_id AND r.id=p.reference_id
            WHERE p.tenant_id=UUID_TO_BIN(@tenant) AND p.site_id=UUID_TO_BIN(@site)
              AND p.discovered_check_id IS NOT NULL AND r.read_status='pending' AND r.last_checked_at IS NULL
            ORDER BY p.created_at, p.id LIMIT @limit;
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@limit", Math.Clamp(limit, 1, 100)));
        var processed = 0;
        foreach (var row in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TitleReadTimeout);
            ArticleSourceReadResult result;
            try { result = await sourceReader.ReadAsync(row[1], timeout.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { result = new ArticleSourceReadResult(row[1], row[1], string.Empty, "failed", null, null, null, [], "SOURCE_TIMEOUT", "网页读取超时。"); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // 读取器只吞 HttpRequestException；流式读取的 IOException 等若向上抛出，这一行会永远留在 pending 并阻塞后续读取。
                logger.LogWarning("Reading a discovered link title on site {SiteId} failed with {ExceptionType}.", siteId, exception.GetType().Name);
                result = new ArticleSourceReadResult(row[1], row[1], string.Empty, "failed", null, null, null, [], "SOURCE_UNREADABLE", "网页暂时无法读取。");
            }
            var title = Trim(result.Title, 500);
            var readStatus = result.ReadStatus is "ready" or "blocked" or "unreadable" or "failed" ? result.ReadStatus : "failed";
            await ExecAsync(connection, null, """
                UPDATE content_references
                SET read_status=@status, title=COALESCE(@title,title), error_code=@code, error_message=@message,
                    reader_version=@reader, last_checked_at=UTC_TIMESTAMP(6), updated_at=UTC_TIMESTAMP(6)
                WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@id);
                """, cancellationToken, ("@status", readStatus), ("@title", title), ("@code", result.ErrorCode),
                ("@message", Trim(result.ErrorMessage, 1000)), ("@reader", ReaderVersion),
                ("@tenant", tenantId.ToString()), ("@id", row[0]));
            if (!string.IsNullOrWhiteSpace(title))
            {
                // 只替换仍为占位的标题（建文时写入 content_references.title 的引用标题，或 URL），用户编辑过的标题不动。
                // 占位值取自上面 SELECT（在引用标题被页面标题覆盖之前读取）。
                var placeholder = string.IsNullOrWhiteSpace(row[3]) ? null : row[3];
                await ExecAsync(connection, null, """
                    UPDATE content_articles a
                    SET a.title=@title, a.updated_at=UTC_TIMESTAMP(6)
                    WHERE a.tenant_id=UUID_TO_BIN(@tenant) AND a.site_id=UUID_TO_BIN(@site) AND a.id=UUID_TO_BIN(@article)
                      AND a.source_kind='citation_discovered'
                      AND (a.title=@url OR (@placeholder IS NOT NULL AND a.title=@placeholder));
                    """, cancellationToken, ("@title", title), ("@url", row[1]), ("@placeholder", placeholder),
                    ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@article", row[2]));
            }
            processed++;
        }
        return processed;
    }

    /// <inheritdoc />
    public async Task<int> OpenRoundsAsync(DateTimeOffset utcNow, Guid? siteId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        var sites = await RowsAsync(connection, null, $"""
            SELECT BIN_TO_UUID(s.tenant_id), BIN_TO_UUID(s.id), COALESCE(b.time_zone,'UTC')
            FROM sites s
            LEFT JOIN site_brand_settings b ON b.site_id=s.id AND b.tenant_id=s.tenant_id
            WHERE s.status='active' AND (@site IS NULL OR s.id=UUID_TO_BIN(@site)) AND EXISTS (
                SELECT 1 FROM content_publication_links p
                JOIN content_articles a ON a.tenant_id=p.tenant_id AND a.site_id=p.site_id AND a.id=p.article_id
                WHERE p.tenant_id=s.tenant_id AND p.site_id=s.id AND {BillableLinkWhereSql})
            ORDER BY s.created_at, s.id;
            """, cancellationToken, ("@site", siteId?.ToString()));
        var opened = 0;
        foreach (var site in sites)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tenantId = Guid.Parse(site[0]); var currentSite = Guid.Parse(site[1]);
            var zone = ArticleMonitoringCalendar.ResolveZone(site[2]);
            var localDate = ArticleMonitoringCalendar.LocalDate(utcNow, zone);
            var existing = await RowAsync(connection, null,
                "SELECT 1 FROM article_monitoring_rounds WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND local_date=@date;",
                cancellationToken, ("@tenant", tenantId.ToString()), ("@site", currentSite.ToString()), ("@date", localDate.ToDateTime(TimeOnly.MinValue)));
            if (existing is not null) continue;
            try
            {
                if (await OpenRoundAsync(connection, tenantId, currentSite, localDate, zone.Equals(TimeZoneInfo.Utc) ? "UTC" : site[2], utcNow, cancellationToken))
                    opened++;
            }
            catch (MySqlException exception) when (exception.Number == 1062)
            {
                // 另一个实例已为同一天开轮；唯一键 uq_article_monitoring_rounds_day 保证只扣一次。
                logger.LogInformation("Article monitoring round for site {SiteId} on {LocalDate} already exists.", currentSite, localDate);
            }
        }
        return opened;
    }

    /// <inheritdoc />
    public async Task<int> SettleRoundsAsync(DateTimeOffset utcNow, Guid? siteId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        var held = await RowsAsync(connection, null, """
            SELECT BIN_TO_UUID(id), BIN_TO_UUID(tenant_id), local_date, time_zone
            FROM article_monitoring_rounds WHERE status='held' AND (@site IS NULL OR site_id=UUID_TO_BIN(@site))
            ORDER BY local_date, created_at;
            """, cancellationToken, ("@site", siteId?.ToString()));
        var settled = 0;
        foreach (var row in held)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var localDate = DateOnly.FromDateTime(DateTime.Parse(row[2], CultureInfo.InvariantCulture));
            var zone = ArticleMonitoringCalendar.ResolveZone(row[3]);
            if (utcNow < ArticleMonitoringCalendar.NextLocalMidnightUtc(localDate, zone)) continue;
            if (await SettleRoundAsync(connection, Guid.Parse(row[1]), Guid.Parse(row[0]), utcNow, cancellationToken)) settled++;
        }
        return settled;
    }

    /// <inheritdoc />
    public async Task<ArticleMonitoringBillingView> BillingAsync(Guid tenantId, Guid siteId, DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        var site = await RowAsync(connection, null, """
            SELECT COALESCE(b.time_zone,'UTC') FROM sites s
            LEFT JOIN site_brand_settings b ON b.site_id=s.id AND b.tenant_id=s.tenant_id
            WHERE s.tenant_id=UUID_TO_BIN(@tenant) AND s.id=UUID_TO_BIN(@site);
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()));
        if (site is null) throw new CollectorStoreException("RESOURCE_NOT_FOUND", "品牌不存在。", 404);
        var zone = ArticleMonitoringCalendar.ResolveZone(site[0]);
        var zoneId = zone.Equals(TimeZoneInfo.Utc) ? "UTC" : site[0];
        var billable = await RowAsync(connection, null, $"""
            SELECT COUNT(*) FROM content_publication_links p
            JOIN content_articles a ON a.tenant_id=p.tenant_id AND a.site_id=p.site_id AND a.id=p.article_id
            WHERE p.tenant_id=UUID_TO_BIN(@tenant) AND p.site_id=UUID_TO_BIN(@site) AND {BillableLinkWhereSql};
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()));
        var billableCount = int.Parse(billable![0], CultureInfo.InvariantCulture);
        var account = await RowAsync(connection, null,
            "SELECT available_credits FROM credit_accounts WHERE tenant_id=UUID_TO_BIN(@tenant);",
            cancellationToken, ("@tenant", tenantId.ToString()));
        var available = account is null ? 0m : decimal.Parse(account[0], CultureInfo.InvariantCulture);
        var rounds = (await RowsAsync(connection, null, """
            SELECT BIN_TO_UUID(id), local_date, time_zone, status, link_count, estimated_credits, held_credits,
                   captured_credits, released_credits, skip_code, skip_message, created_at, settled_at
            FROM article_monitoring_rounds
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site)
            ORDER BY local_date DESC LIMIT 7;
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString())))
            .Select(ToRound).ToArray();
        var today = ArticleMonitoringCalendar.LocalDate(utcNow, zone);
        var todayRound = rounds.FirstOrDefault(round => round.LocalDate == today);
        return new ArticleMonitoringBillingView(ArticleMonitoringPricing.UnitPrice, billableCount,
            ArticleMonitoringPricing.Estimate(billableCount), available, zoneId,
            todayRound?.Status ?? "none", todayRound, rounds);
    }

    private async Task<DiscoveryState> LoadOrResetStateAsync(MySqlConnection connection, Guid tenantId, Guid siteId,
        Guid releaseId, DateTimeOffset utcNow, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        var backfillFrom = ArticleMonitoringCalendar.BackfillFromUtc(utcNow, zone);
        await ExecAsync(connection, null, """
            INSERT IGNORE INTO content_citation_discovery_state (tenant_id, site_id, release_id, last_arrival_id, backfill_from, updated_at)
            VALUES (UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), UUID_TO_BIN(@release), 0, @from, @now);
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()),
            ("@release", releaseId.ToString()), ("@from", backfillFrom.UtcDateTime), ("@now", utcNow.UtcDateTime));
        var row = (await RowAsync(connection, null, """
            SELECT BIN_TO_UUID(release_id), last_arrival_id, backfill_from, backfill_completed_at
            FROM content_citation_discovery_state WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site);
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString())))!;
        var storedRelease = Guid.TryParse(row[0], out var parsed) ? parsed : (Guid?)null;
        if (storedRelease == releaseId)
            return new DiscoveryState(storedRelease, long.Parse(row[1], CultureInfo.InvariantCulture),
                ParseNullableDate(row[2]), ParseNullableDate(row[3]));
        // 已发布 release 变化（重新分析或品牌域名配置变化）：从回溯窗口重新扫描，已登记的 URL 会被去重跳过。
        await ExecAsync(connection, null, """
            UPDATE content_citation_discovery_state
            SET release_id=UUID_TO_BIN(@release), last_arrival_id=0, backfill_from=@from, backfill_completed_at=NULL, updated_at=@now
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site);
            """, cancellationToken, ("@release", releaseId.ToString()), ("@from", backfillFrom.UtcDateTime),
            ("@now", utcNow.UtcDateTime), ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()));
        return new DiscoveryState(releaseId, 0, backfillFrom, null);
    }

    /// <summary>
    /// citation_classifications.ownership 的存储值来自 DetailedAnalyticsRules.Classify：品牌自有域名为 'brand'，
    /// 竞品为 'competitor'，其余 'third_party'，品牌配置缺失时 'unknown'；API 枚举 CitationOwnership（owned / non_owned）只是投影口径。
    /// </summary>
    internal const string OwnedClassification = "brand";

    private static async Task<List<Candidate>> LoadCandidatesAsync(MySqlConnection connection, Guid tenantId, Guid siteId,
        Guid releaseId, long watermark, long ceiling, DateTimeOffset? backfillFrom, CancellationToken cancellationToken)
    {
        // GROUP_CONCAT 的 SEPARATOR 必须是字符串字面量（CHAR(31) 这类表达式不被接受）。这里拼入真实的 U+001F
        // 字符（普通 C# 字符串会把 \u001f 解码成控制字符）；不能写在原始字符串里，否则 MySQL 收到的是文本 "u001f"。
        var rows = await RowsAsync(connection, null, """
            SELECT ce.url_hash, ce.normalized_url, MIN(a.id),
                   SUBSTRING_INDEX(GROUP_CONCAT(BIN_TO_UUID(c.id) ORDER BY c.captured_at, c.id SEPARATOR ','), ',', 1),
                   SUBSTRING_INDEX(GROUP_CONCAT(COALESCE(ce.title,'') ORDER BY c.captured_at, c.id SEPARATOR '
            """ + TitleSeparator + """
            '), '
            """ + TitleSeparator + """
            ', 1)
            FROM citation_events ce
            JOIN citation_classifications cc ON cc.release_id=ce.release_id AND cc.check_id=ce.check_id
              AND cc.url_hash=ce.url_hash AND cc.ownership=@owned
            JOIN check_analysis an ON an.release_id=ce.release_id AND an.check_id=ce.check_id
              AND an.completed_event_id IS NOT NULL
            JOIN visibility_checks c ON c.tenant_id=an.tenant_id AND c.site_id=an.site_id AND c.id=ce.check_id
              AND c.captured_at IS NOT NULL
            JOIN analytics_check_arrivals a ON a.check_id=c.id
            WHERE ce.release_id=UUID_TO_BIN(@release) AND an.tenant_id=UUID_TO_BIN(@tenant) AND an.site_id=UUID_TO_BIN(@site)
              AND a.id>@watermark AND a.id<=@ceiling
              AND (@backfill_from IS NULL OR c.captured_at>=@backfill_from)
            GROUP BY ce.url_hash, ce.normalized_url
            ORDER BY MIN(a.id), ce.url_hash
            LIMIT @limit;
            """, cancellationToken, ("@release", releaseId.ToString()), ("@tenant", tenantId.ToString()),
            ("@site", siteId.ToString()), ("@watermark", watermark), ("@ceiling", ceiling), ("@owned", OwnedClassification),
            ("@backfill_from", backfillFrom?.UtcDateTime), ("@limit", DiscoveryBatchSize));
        return rows.Select(row => new Candidate(row[0], row[1], long.Parse(row[2], CultureInfo.InvariantCulture),
            Guid.Parse(row[3]), string.IsNullOrWhiteSpace(row[4]) ? null : row[4])).ToList();
    }

    /// <summary>为一个候选 URL 建文；该 URL 已存在任何发布引用或链接（含已停用）时返回 false。</summary>
    private static async Task<bool> CreateDiscoveredArticleAsync(MySqlConnection connection, Guid tenantId, Guid siteId,
        Guid ownerId, Candidate candidate, string normalizedUrl, bool? ownership, CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var existing = await RowAsync(connection, transaction, """
            SELECT 1 FROM content_references
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND kind='publication' AND url_hash=@hash
            LIMIT 1 FOR UPDATE;
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@hash", candidate.UrlHash));
        if (existing is not null) { await transaction.RollbackAsync(cancellationToken); return false; }
        var title = Trim(candidate.Title, 500);
        if (string.IsNullOrWhiteSpace(title)) title = normalizedUrl.Length > 500 ? normalizedUrl[..500] : normalizedUrl;
        var articleId = Guid.NewGuid(); var referenceId = Guid.NewGuid(); var linkId = Guid.NewGuid();
        await ExecAsync(connection, transaction, """
            INSERT INTO content_articles (id,tenant_id,site_id,created_by,source_kind,status,title,language,monitor_status,current_version_id,created_at,updated_at)
            VALUES (UUID_TO_BIN(@article),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@user),'citation_discovered','active',@title,'und','active',NULL,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
            INSERT INTO content_monitor_scopes (id,tenant_id,site_id,article_id,mode,filter_snapshot,version,enabled_at,created_at,updated_at)
            VALUES (UUID_TO_BIN(UUID()),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@article),'all_brand_observations',
                    JSON_OBJECT('activeWindows',JSON_ARRAY(JSON_OBJECT('from',DATE_FORMAT(UTC_TIMESTAMP(6),'%Y-%m-%d %H:%i:%s.%f'),'to',NULL))),
                    1,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
            INSERT INTO content_references (id,tenant_id,site_id,url_original,url_normalized,url_hash,kind,read_status,title,created_at,updated_at)
            VALUES (UUID_TO_BIN(@reference),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),@original,@normalized,@hash,'publication','pending',@citation_title,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
            INSERT INTO content_publication_links (id,tenant_id,site_id,article_id,article_version_id,reference_id,channel,status,ownership_match,discovered_check_id,discovered_at,created_by,created_at)
            VALUES (UUID_TO_BIN(@link),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@article),NULL,UUID_TO_BIN(@reference),NULL,'active',@ownership,UUID_TO_BIN(@check),UTC_TIMESTAMP(6),UUID_TO_BIN(@user),UTC_TIMESTAMP(6));
            """, cancellationToken, ("@article", articleId.ToString()), ("@reference", referenceId.ToString()), ("@link", linkId.ToString()),
            ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@user", ownerId.ToString()),
            ("@title", title), ("@citation_title", Trim(candidate.Title, 500)), ("@original", candidate.NormalizedUrl),
            ("@normalized", candidate.NormalizedUrl), ("@hash", candidate.UrlHash), ("@ownership", ownership),
            ("@check", candidate.FirstCheck.ToString()));
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task<bool> OpenRoundAsync(MySqlConnection connection, Guid tenantId, Guid siteId, DateOnly localDate,
        string timeZoneId, DateTimeOffset utcNow, CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecAsync(connection, transaction,
            "INSERT IGNORE INTO credit_accounts (tenant_id, updated_at) VALUES (UUID_TO_BIN(@tenant), UTC_TIMESTAMP(6));",
            cancellationToken, ("@tenant", tenantId.ToString()));
        var account = await RowAsync(connection, transaction,
            "SELECT available_credits FROM credit_accounts WHERE tenant_id=UUID_TO_BIN(@tenant) FOR UPDATE;",
            cancellationToken, ("@tenant", tenantId.ToString()));
        var available = account is null ? 0m : decimal.Parse(account[0], CultureInfo.InvariantCulture);
        var links = await RowsAsync(connection, transaction, $"""
            SELECT BIN_TO_UUID(p.id), BIN_TO_UUID(p.article_id) FROM content_publication_links p
            JOIN content_articles a ON a.tenant_id=p.tenant_id AND a.site_id=p.site_id AND a.id=p.article_id
            WHERE p.tenant_id=UUID_TO_BIN(@tenant) AND p.site_id=UUID_TO_BIN(@site) AND {BillableLinkWhereSql}
            ORDER BY p.created_at, p.id;
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()));
        if (links.Count == 0) { await transaction.RollbackAsync(cancellationToken); return false; }
        var amount = ArticleMonitoringPricing.Estimate(links.Count);
        var roundId = Guid.NewGuid();
        if (available < amount)
        {
            await ExecAsync(connection, transaction, """
                INSERT INTO article_monitoring_rounds
                    (id, tenant_id, site_id, local_date, time_zone, unit_price, link_count, estimated_credits, held_credits,
                     captured_credits, released_credits, status, skip_code, skip_message, created_at)
                VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), @date, @zone, @unit, @count, @amount, 0,
                        0, 0, 'skipped', 'CREDIT_INSUFFICIENT', @message, @now);
                """, cancellationToken, ("@id", roundId.ToString()), ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()),
                ("@date", localDate.ToDateTime(TimeOnly.MinValue)), ("@zone", timeZoneId), ("@unit", ArticleMonitoringPricing.UnitPrice),
                ("@count", links.Count), ("@amount", amount),
                ("@message", $"本轮预计需要 {amount:0.###} 积分，当前可用 {available:0.###} 积分；今日已跳过，次日重试。"),
                ("@now", utcNow.UtcDateTime));
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        await ExecAsync(connection, transaction, """
            UPDATE credit_accounts
            SET available_credits = available_credits - @amount, reserved_credits = reserved_credits + @amount,
                version = version + 1, updated_at = UTC_TIMESTAMP(6)
            WHERE tenant_id = UUID_TO_BIN(@tenant);
            INSERT INTO article_monitoring_rounds
                (id, tenant_id, site_id, local_date, time_zone, unit_price, link_count, estimated_credits, held_credits,
                 captured_credits, released_credits, status, created_at)
            VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), @date, @zone, @unit, @count, @amount, @amount,
                    0, 0, 'held', @now);
            INSERT INTO credit_ledger
                (id, tenant_id, entry_type, amount, balance_after, article_monitoring_round_id, external_reference, reason, created_at)
            SELECT UUID_TO_BIN(@ledger), tenant_id, 'reserve', -@amount, available_credits, UUID_TO_BIN(@id),
                   CONCAT('article-monitoring:reserve:', @id_text), '文章链接每日监控预留', @now
            FROM credit_accounts WHERE tenant_id = UUID_TO_BIN(@tenant);
            """, cancellationToken, ("@amount", amount), ("@tenant", tenantId.ToString()), ("@id", roundId.ToString()),
            ("@id_text", roundId.ToString("N")), ("@site", siteId.ToString()), ("@date", localDate.ToDateTime(TimeOnly.MinValue)),
            ("@zone", timeZoneId), ("@unit", ArticleMonitoringPricing.UnitPrice), ("@count", links.Count),
            ("@ledger", Guid.NewGuid().ToString()), ("@now", utcNow.UtcDateTime));
        foreach (var link in links)
        {
            await ExecAsync(connection, transaction, """
                INSERT INTO article_monitoring_round_items (id, tenant_id, site_id, round_id, article_id, publication_link_id, amount, status, created_at)
                VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), UUID_TO_BIN(@round), UUID_TO_BIN(@article), UUID_TO_BIN(@link), @amount, 'held', @now);
                """, cancellationToken, ("@id", Guid.NewGuid().ToString()), ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()),
                ("@round", roundId.ToString()), ("@article", link[1]), ("@link", link[0]), ("@amount", ArticleMonitoringPricing.UnitPrice),
                ("@now", utcNow.UtcDateTime));
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task<bool> SettleRoundAsync(MySqlConnection connection, Guid tenantId, Guid roundId, DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var round = await RowAsync(connection, transaction,
            "SELECT status, held_credits FROM article_monitoring_rounds WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@id) FOR UPDATE;",
            cancellationToken, ("@tenant", tenantId.ToString()), ("@id", roundId.ToString()));
        if (round is null || round[0] != "held") { await transaction.RollbackAsync(cancellationToken); return false; }
        var held = decimal.Parse(round[1], CultureInfo.InvariantCulture);
        var account = await RowAsync(connection, transaction,
            "SELECT reserved_credits FROM credit_accounts WHERE tenant_id=UUID_TO_BIN(@tenant) FOR UPDATE;",
            cancellationToken, ("@tenant", tenantId.ToString()));
        var reserved = account is null ? 0m : decimal.Parse(account[0], CultureInfo.InvariantCulture);
        var capture = Math.Min(held, reserved);
        var release = held - capture;
        if (release > 0)
            logger.LogError("Article monitoring round {RoundId} held {Held} credits but the account only reserves {Reserved}; recording the difference as released.",
                roundId, held, reserved);
        await ExecAsync(connection, transaction, """
            UPDATE article_monitoring_round_items SET status='captured', settled_at=@now
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND round_id=UUID_TO_BIN(@round) AND status='held';
            UPDATE credit_accounts
            SET reserved_credits = reserved_credits - @capture, lifetime_captured = lifetime_captured + @capture,
                version = version + 1, updated_at = UTC_TIMESTAMP(6)
            WHERE tenant_id = UUID_TO_BIN(@tenant);
            INSERT INTO credit_ledger
                (id, tenant_id, entry_type, amount, balance_after, article_monitoring_round_id, external_reference, reason, created_at)
            SELECT UUID_TO_BIN(@ledger), tenant_id, 'capture', 0, available_credits, UUID_TO_BIN(@round),
                   CONCAT('article-monitoring:capture:', @round_text), '文章链接每日监控结算', @now
            FROM credit_accounts WHERE tenant_id = UUID_TO_BIN(@tenant);
            UPDATE article_monitoring_rounds
            SET status=CASE WHEN @capture>0 THEN 'captured' ELSE 'released' END, held_credits=0, captured_credits=@capture,
                released_credits=@release, settled_at=@now
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@round);
            """, cancellationToken, ("@now", utcNow.UtcDateTime), ("@tenant", tenantId.ToString()), ("@round", roundId.ToString()),
            ("@round_text", roundId.ToString("N")), ("@capture", capture), ("@release", release), ("@ledger", Guid.NewGuid().ToString()));
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static ArticleMonitoringRoundView ToRound(string[] row)
        => new(Guid.Parse(row[0]), DateOnly.FromDateTime(DateTime.Parse(row[1], CultureInfo.InvariantCulture)), row[2], row[3],
            int.Parse(row[4], CultureInfo.InvariantCulture), Dec(row[5]), Dec(row[6]), Dec(row[7]), Dec(row[8]),
            string.IsNullOrWhiteSpace(row[9]) ? null : row[9], string.IsNullOrWhiteSpace(row[10]) ? null : row[10],
            ParseDate(row[11]), ParseNullableDate(row[12]));

    private static decimal Dec(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);
    private static string? Trim(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length > max ? trimmed[..max] : trimmed;
    }
    private static DateTimeOffset ParseDate(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    private static DateTimeOffset? ParseNullableDate(string value) => string.IsNullOrWhiteSpace(value) ? null : ParseDate(value);

    private static async Task<int> ExecAsync(MySqlConnection connection, MySqlTransaction? transaction, string sql,
        CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = new MySqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<string[]>> RowsAsync(MySqlConnection connection, MySqlTransaction? transaction, string sql,
        CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = new MySqlCommand(sql, connection, transaction);
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

    private static async Task<string[]?> RowAsync(MySqlConnection connection, MySqlTransaction? transaction, string sql,
        CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
        => (await RowsAsync(connection, transaction, sql, cancellationToken, parameters)).FirstOrDefault();
}
