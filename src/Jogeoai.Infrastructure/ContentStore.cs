using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jogeoai.Application;
using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

/// <summary>文章、生成快照和发布链接的 MySQL 实现。</summary>
public sealed partial class ContentStore(
    MySqlDatabase database,
    IManagementStore management,
    ICreditStore credits,
    IResearchStore research,
    IArticleSourceReader sourceReader,
    IKnowledgeStore knowledge,
    IKnowledgeRetrievalService knowledgeRetrieval,
    IArticleProductResolver? products = null) : IContentStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async Task<ArticleListPage> ListArticlesAsync(Guid tenantId, Guid siteId, ArticleListFilter filter,
        CancellationToken cancellationToken = default)
    {
        filter = NormalizeFilter(filter);
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        await AssertSiteAsync(connection, tenantId, siteId, cancellationToken);
        var citationWindow = await LoadCitationWindowAsync(connection, tenantId, siteId, filter.CitationDays, cancellationToken);
        var cursorScope = ArticleCursorScope(filter, citationWindow);
        var engineScoped = filter.CitationEngine is not null;
        // Cited judgement, cited counts and the cited-time sorts share the
        // per-row citation summary's observation rules (window, engine,
        // link lifetime, monitor scope), so a row's「已被引用」agrees with the
        // citationCount it displays.
        var observations = ArticleCitationObservationsSql(engineScoped);
        // NO_SEMIJOIN: MySQL 8.4 turns this EXISTS into a semi-join, which next to
        // the LATERAL joins in ArticleJoinsSql returns an article once per matching
        // observation (reproduced on 2026-11-04: 46 rows for 39 articles).
        var citedSql = $"EXISTS(SELECT /*+ NO_SEMIJOIN() */ 1 {observations})";
        // baseWhere carries every filter except the tab dimensions (stage and
        // status, which are equivalent for "archived"), the cited switch and the
        // cursor.  It feeds the per-stage tab counts and the cited counts; the
        // page itself adds status, stage, cited and cursor.
        var baseWhere = new StringBuilder("WHERE a.tenant_id=UUID_TO_BIN(@tenant) AND a.site_id=UUID_TO_BIN(@site)");
        if (filter.SourceKind is not null) baseWhere.Append(" AND a.source_kind=@source");
        if (filter.MonitorStatus is not null) baseWhere.Append(" AND a.monitor_status=@monitor");
        if (filter.CreatedBy is not null) baseWhere.Append(" AND a.created_by=UUID_TO_BIN(@created_by)");
        if (filter.TagId is not null) baseWhere.Append(" AND EXISTS (SELECT 1 FROM content_article_tags filter_at WHERE filter_at.tenant_id=a.tenant_id AND filter_at.article_id=a.id AND filter_at.tag_id=UUID_TO_BIN(@tag_id))");
        if (filter.PromptId is not null) baseWhere.Append(" AND EXISTS (SELECT 1 FROM article_generation_tasks filter_gt WHERE filter_gt.tenant_id=a.tenant_id AND filter_gt.site_id=a.site_id AND filter_gt.article_id=a.id AND filter_gt.prompt_id=UUID_TO_BIN(@prompt_id))");
        if (filter.TargetId is not null) baseWhere.Append(" AND EXISTS (SELECT 1 FROM article_generation_tasks filter_gt WHERE filter_gt.tenant_id=a.tenant_id AND filter_gt.site_id=a.site_id AND filter_gt.article_id=a.id AND JSON_SEARCH(filter_gt.target_snapshot,'one',@target_id) IS NOT NULL)");
        if (!string.IsNullOrWhiteSpace(filter.Query)) baseWhere.Append(" AND a.title LIKE @query");
        if (filter.CreatedFrom is not null) baseWhere.Append(" AND a.created_at>=@created_from");
        if (filter.CreatedTo is not null) baseWhere.Append(" AND a.created_at<@created_to");
        if (filter.Language is not null) baseWhere.Append(" AND a.language=@language");
        if (filter.Ownership == ArticleOwnershipFilter.Owned) baseWhere.Append(" AND ").Append(ArticleOwnedSql);
        else if (filter.Ownership == ArticleOwnershipFilter.External)
            baseWhere.Append(" AND ").Append(ArticleHasActiveLinkSql).Append(" AND NOT ").Append(ArticleOwnedSql);
        var where = new StringBuilder(baseWhere.ToString());
        if (filter.Status is not null) where.Append(" AND a.status=@status");
        if (filter.Stage is not null) where.Append(" AND (").Append(ArticleStageSql).Append(")=@stage");
        if (filter.Cited is true) where.Append(" AND ").Append(citedSql);
        else if (filter.Cited is false) where.Append(" AND NOT ").Append(citedSql);
        DateTimeOffset? cursorTime = null;
        Guid? cursorId = null;
        long? cursorCitations = null;
        DateTime? cursorCitedAt = null;
        // citation_desc counts the same observations as the row summary (incl.
        // monitor scope), so the sort agrees with the displayed citationCount
        // (unified 2026-11-04 at the user's request).
        var citationOrder = $"(SELECT COUNT(DISTINCT ce.check_id) {observations})";
        // last/first cited sorts: rows without a cited time get the floor value,
        // which sorts after every real observation (nulls last).
        var citedAtKey = filter.Sort switch
        {
            "last_cited_desc" => $"COALESCE((SELECT MAX(vc.captured_at) {observations}),{CitationSortFloorSql})",
            "first_cited_desc" => $"COALESCE((SELECT MIN(vc.captured_at) {observations}),{CitationSortFloorSql})",
            _ => null
        };
        if (!string.IsNullOrWhiteSpace(filter.Cursor))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(filter.Cursor));
                var parts = decoded.Split(':');
                if (!string.Equals(parts[0], filter.Sort, StringComparison.Ordinal)) throw new FormatException();
                if (filter.Sort == "citation_desc")
                {
                    var offset = parts.Length == 5 ? 2 : 1;
                    if (parts.Length is not (4 or 5)
                        || parts.Length == 5 && !string.Equals(parts[1], cursorScope, StringComparison.Ordinal)
                        || !long.TryParse(parts[offset], NumberStyles.Integer, CultureInfo.InvariantCulture, out var citationCount)
                        || citationCount < 0
                        || !long.TryParse(parts[offset + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
                        || !Guid.TryParse(parts[offset + 2], out var parsedId)) throw new FormatException();
                    cursorCitations = citationCount;
                    cursorTime = new DateTimeOffset(ticks, TimeSpan.Zero);
                    cursorId = parsedId;
                }
                else if (citedAtKey is not null)
                {
                    // sort:scope:{citedTicks|-1}:{updatedTicks}:{id}; always scoped.
                    if (parts.Length != 5
                        || !string.Equals(parts[1], cursorScope, StringComparison.Ordinal)
                        || !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var citedTicks)
                        || citedTicks < -1
                        || !long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
                        || !Guid.TryParse(parts[4], out var parsedId)) throw new FormatException();
                    cursorCitedAt = citedTicks == -1 ? CitationSortFloor : new DateTime(citedTicks, DateTimeKind.Utc);
                    cursorTime = new DateTimeOffset(ticks, TimeSpan.Zero);
                    cursorId = parsedId;
                }
                else
                {
                    var offset = parts.Length == 4 ? 2 : 1;
                    if (parts.Length is not (3 or 4)
                        || parts.Length == 4 && !string.Equals(parts[1], cursorScope, StringComparison.Ordinal)
                        || !long.TryParse(parts[offset], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
                        || !Guid.TryParse(parts[offset + 1], out var parsedId)) throw new FormatException();
                    cursorTime = new DateTimeOffset(ticks, TimeSpan.Zero);
                    cursorId = parsedId;
                }
            }
            catch (Exception) { throw new CollectorStoreException("INVALID_CURSOR", "文章列表游标无效。", 400); }
            if (filter.Sort == "citation_desc")
                where.Append($" AND ({citationOrder}<@cursor_citations OR ({citationOrder}=@cursor_citations AND (a.updated_at<@cursor_time OR (a.updated_at=@cursor_time AND a.id<UUID_TO_BIN(@cursor_id)))))");
            else if (citedAtKey is not null)
                where.Append($" AND ({citedAtKey}<@cursor_cited_at OR ({citedAtKey}=@cursor_cited_at AND (a.updated_at<@cursor_time OR (a.updated_at=@cursor_time AND a.id<UUID_TO_BIN(@cursor_id)))))");
            else
            {
                var column = filter.Sort == "created_desc" ? "a.created_at" : "a.updated_at";
                where.Append($" AND ({column}<@cursor_time OR ({column}=@cursor_time AND a.id<UUID_TO_BIN(@cursor_id)))");
            }
        }
        // citation_ts is the zero-padded text of the cited-time key, so its
        // string order equals time order and the floor value sorts last.
        var order = filter.Sort switch
        {
            "updated_desc" => "a.updated_at DESC, a.id DESC",
            "citation_desc" => $"{citationOrder} DESC, a.updated_at DESC, a.id DESC",
            "last_cited_desc" or "first_cited_desc" => "citation_ts DESC, a.updated_at DESC, a.id DESC",
            _ => "a.created_at DESC, a.id DESC"
        };
        (string Name, object? Value)[] parameters =
        [
            ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@status", filter.Status?.ToString().ToLowerInvariant()),
            ("@source", filter.SourceKind is null ? null : SourceKindValue(filter.SourceKind.Value)), ("@monitor", filter.MonitorStatus?.ToString().ToLowerInvariant()),
            ("@created_by", filter.CreatedBy?.ToString()), ("@query", string.IsNullOrWhiteSpace(filter.Query) ? null : $"%{filter.Query}%"),
            ("@tag_id", filter.TagId?.ToString()), ("@prompt_id", filter.PromptId?.ToString()), ("@target_id", filter.TargetId?.ToString()),
            ("@created_from", filter.CreatedFrom?.UtcDateTime), ("@created_to", filter.CreatedTo?.UtcDateTime),
            ("@language", filter.Language), ("@stage", filter.Stage is null ? null : ArticleStageRules.Key(filter.Stage.Value)),
            ("@cursor_time", cursorTime?.UtcDateTime), ("@cursor_id", cursorId?.ToString()),
            ("@cursor_citations", cursorCitations), ("@cursor_cited_at", cursorCitedAt),
            ("@citation_from", citationWindow.FromUtc.UtcDateTime), ("@citation_to", citationWindow.ToUtc.UtcDateTime),
            ("@citation_engine", filter.CitationEngine), ("@limit", filter.Limit + 1)
        ];
        // Cursor keys are read as microsecond text: the generic row reader formats
        // DATETIME values without fractions, and articles created in the same
        // second (one generation batch) would otherwise be skipped when paging.
        var cursorColumns = citedAtKey is null
            ? $",DATE_FORMAT({(filter.Sort == "created_desc" ? "a.created_at" : "a.updated_at")},'%Y-%m-%d %H:%i:%s.%f') AS cursor_time"
            : $",DATE_FORMAT({citedAtKey},'%Y-%m-%d %H:%i:%s.%f') AS citation_ts"
              + ",DATE_FORMAT(a.updated_at,'%Y-%m-%d %H:%i:%s.%f') AS cursor_updated_at";
        var sql = $"""
            SELECT {ArticleColumnsSql},
                   {citationOrder} AS citation_order{cursorColumns}
            {ArticleJoinsSql}
            {where} ORDER BY {order} LIMIT @limit;
            """;
        var rows = await RowsAsync(connection, null, sql, cancellationToken, parameters);
        var hasNext = rows.Count > filter.Limit; if (hasNext) rows.RemoveAt(filter.Limit);
        var items = rows.Select(ToArticle).ToArray();
        var lastRow = rows.LastOrDefault();
        string? next = null;
        if (hasNext && items.Length > 0)
        {
            var text = filter.Sort switch
            {
                "citation_desc" => $"{filter.Sort}:{cursorScope}:{lastRow![ArticleColumnCount]}:{ParseDate(lastRow[ArticleColumnCount + 1]).UtcTicks}:{items[^1].ArticleId:N}",
                "last_cited_desc" or "first_cited_desc" =>
                    $"{filter.Sort}:{cursorScope}:{CitedCursorTicks(lastRow![ArticleColumnCount + 1])}:{ParseDate(lastRow[ArticleColumnCount + 2]).UtcTicks}:{items[^1].ArticleId:N}",
                _ => $"{filter.Sort}:{cursorScope}:{ParseDate(lastRow![ArticleColumnCount + 1]).UtcTicks}:{items[^1].ArticleId:N}"
            };
            next = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        }
        if (items.Length > 0)
        {
            var articleIds = items.Select(item => item.ArticleId).ToArray();
            var summaries = await LoadArticleCitationSummariesAsync(connection, tenantId, siteId,
                articleIds, citationWindow, filter.CitationEngine, cancellationToken);
            var primaryLinks = await LoadPrimaryLinksAsync(connection, tenantId, siteId, articleIds, cancellationToken);
            for (var index = 0; index < items.Length; index++)
            {
                var item = items[index];
                if (summaries.TryGetValue(item.ArticleId, out var summary))
                    item = item with { CitationSummary = summary };
                if (primaryLinks.TryGetValue(item.ArticleId, out var primaryLink))
                    item = item with { PrimaryLink = primaryLink };
                items[index] = item;
            }
        }

        // Tab counts, cited counts and the summary bar use the same joins and
        // stage expression as the page rows, so a row's stage always agrees
        // with its tab count.  Cited counts ignore the cited switch; stage
        // counts and totals follow it.
        var countRows = await RowsAsync(connection, null, $"""
            SELECT s.stage, s.cited, COUNT(*), COALESCE(SUM(s.links),0)
            FROM (
                SELECT {ArticleStageSql} AS stage, {ArticleActiveLinksSql} AS links, {citedSql} AS cited
                {ArticleJoinsSql}
                {baseWhere}
            ) AS s
            GROUP BY s.stage, s.cited;
            """, cancellationToken, parameters);
        var stageCounts = ArticleStageRules.Keys.ToDictionary(key => key, _ => 0L, StringComparer.Ordinal);
        long totalCount = 0, activeLinkCount = 0, citedCount = 0, uncitedCount = 0;
        foreach (var countRow in countRows)
        {
            if (!ArticleStageRules.TryParse(countRow[0], out var stage)) continue;
            var cited = ParseBool(countRow[1]);
            var count = (long)decimal.Parse(countRow[2], CultureInfo.InvariantCulture);
            var links = (long)decimal.Parse(countRow[3], CultureInfo.InvariantCulture);
            var inScope = InListScope(stage, filter);
            if (inScope)
            {
                if (cited) citedCount += count;
                else uncitedCount += count;
            }
            if (filter.Cited is { } wantCited && wantCited != cited) continue;
            stageCounts[ArticleStageRules.Key(stage)] += count;
            if (!inScope) continue;
            totalCount += count;
            activeLinkCount += links;
        }
        return new ArticleListPage(items, next, totalCount, activeLinkCount, stageCounts,
            new ArticleCitedCounts(citedCount + uncitedCount, citedCount, uncitedCount));
    }

    /// <summary>被引时间游标键：排序下限（没有被引）编码为 -1，否则为 UTC 刻度。</summary>
    private static long CitedCursorTicks(string value)
    {
        var citedAt = ParseDate(value);
        return citedAt.UtcDateTime <= CitationSortFloor ? -1 : citedAt.UtcTicks;
    }

    /// <inheritdoc />
    public async Task<ArticleDetailView?> GetArticleAsync(Guid tenantId, Guid siteId, Guid articleId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        var articleRow = await RowAsync(connection, null, $"""
            SELECT {ArticleColumnsSql}
            {ArticleJoinsSql}
            WHERE a.tenant_id=UUID_TO_BIN(@tenant) AND a.site_id=UUID_TO_BIN(@site) AND a.id=UUID_TO_BIN(@article);
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@article", articleId.ToString()));
        if (articleRow is null) return null;
        var article = ToArticle(articleRow);
        var citationWindow = await LoadCitationWindowAsync(connection, tenantId, siteId, 30, cancellationToken);
        var summaries = await LoadArticleCitationSummariesAsync(connection, tenantId, siteId, [articleId], citationWindow, null, cancellationToken);
        if (summaries.TryGetValue(articleId, out var citationSummary))
            article = article with { CitationSummary = citationSummary };
        // edit_revision exists only after migration 041; older databases keep serving details with revision 0.
        var revisionColumn = await ArticleEditorSchema.ExistsAsync(connection, cancellationToken) ? "edit_revision" : "0";
        var versions = await RowsAsync(connection, null, $"""
            SELECT BIN_TO_UUID(id),BIN_TO_UUID(article_id),version_no,version_kind,title,summary,body_markdown,seo_title,seo_description,keywords_json,content_length,language,
                   BIN_TO_UUID(generation_batch_id),BIN_TO_UUID(knowledge_context_id),provider,model,strategy_key,strategy_version,BIN_TO_UUID(created_by),created_at,without_rag,evidence_ids_json,uncertainties_json,disclosure,{revisionColumn}
            FROM content_article_versions WHERE tenant_id=UUID_TO_BIN(@tenant) AND article_id=UUID_TO_BIN(@article) ORDER BY version_no DESC;
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@article", articleId.ToString()));
        var links = await RowsAsync(connection, null, """
            SELECT BIN_TO_UUID(p.id),BIN_TO_UUID(p.article_id),BIN_TO_UUID(p.article_version_id),r.url_original,r.url_normalized,r.url_hash,p.channel,p.published_at,p.status,p.ownership_match,r.read_status,p.created_at,p.disabled_at,BIN_TO_UUID(p.discovered_check_id),p.discovered_at
            FROM content_publication_links p JOIN content_references r ON r.tenant_id=p.tenant_id AND r.id=p.reference_id
            WHERE p.tenant_id=UUID_TO_BIN(@tenant) AND p.site_id=UUID_TO_BIN(@site) AND p.article_id=UUID_TO_BIN(@article) ORDER BY p.created_at;
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@article", articleId.ToString()));
        ArticleGenerationBasisView? generation = null;
        if (article.Generation is { } latest)
        {
            var task = await RowAsync(connection, null, """
                SELECT t.prompt_snapshot,t.target_snapshot,t.reference_snapshot,t.strategy_key,t.length_profile,t.style,
                       t.audience,t.keywords_json,t.extra_requirements,t.material_notes,t.without_rag,t.context_snapshot
                FROM article_generation_tasks t
                WHERE t.tenant_id=UUID_TO_BIN(@tenant) AND t.site_id=UUID_TO_BIN(@site)
                  AND t.article_id=UUID_TO_BIN(@article) AND t.id=UUID_TO_BIN(@task);
                """, cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()),
                ("@article", articleId.ToString()), ("@task", latest.TaskId.ToString()));
            if (task is not null)
            {
                var withoutRag = ParseBool(task[10]);
                generation = new ArticleGenerationBasisView(latest.TaskId, latest.BatchId, latest.Status,
                    ReadPromptSummary(task[0]) ?? article.Prompt, ReadTargetSummaries(task[1]), task[3], task[4], task[5],
                    Null(task[6]), DeserializeStrings(task[7]), Null(task[8]), Null(task[9]),
                    DeserializeReferenceSnapshot(task[2])
                        .Select(reference => new ArticleReferenceSummary(reference.UrlNormalized, reference.ReadStatus, reference.Title))
                        .ToArray(),
                    withoutRag, CountFrozenKnowledgeHits(task[11], withoutRag), latest.ErrorCode, latest.ErrorMessage);
            }
        }
        var current = versions.Select(ToVersion).FirstOrDefault(v => v.VersionNo == article.CurrentVersionNo);
        return new ArticleDetailView(article, current, versions.Select(ToVersion).ToArray(), links.Select(ToLink).ToArray(), [],
            new Dictionary<string, string?> { ["reservationStatus"] = Null(articleRow[24]) }, generation);
    }

    /// <inheritdoc />
    public async Task<ArticleDetailView> EditArticleAsync(Guid tenantId, Guid siteId, Guid articleId, Guid userId,
        ArticleEditRequest request, CancellationToken cancellationToken = default)
    {
        ValidateEdit(request);
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        // edit_revision exists only after migration 041; before it every version counts as revision 0.
        var revisionColumn = await ArticleEditorSchema.ExistsAsync(connection, cancellationToken) ? "cv.edit_revision" : "0";
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var row = await RowAsync(connection, transaction, $"SELECT BIN_TO_UUID(a.current_version_id),COALESCE((SELECT MAX(version_no) FROM content_article_versions v WHERE v.tenant_id=a.tenant_id AND v.article_id=a.id),0),a.status,cv.body_markdown,COALESCE({revisionColumn},0) FROM content_articles a LEFT JOIN content_article_versions cv ON cv.tenant_id=a.tenant_id AND cv.id=a.current_version_id WHERE a.tenant_id=UUID_TO_BIN(@tenant) AND a.site_id=UUID_TO_BIN(@site) AND a.id=UUID_TO_BIN(@article) FOR UPDATE;", cancellationToken,
            ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@article", articleId.ToString()));
        if (row is null) throw NotFound(); if (row[2].Equals("archived", StringComparison.OrdinalIgnoreCase)) throw new CollectorStoreException("ARTICLE_ARCHIVED", "文章已归档。", 409);
        var currentVersion = int.Parse(row[1], CultureInfo.InvariantCulture);
        // Autosave overwrites a draft in place without a new version number, so the revision is what detects a stale editor.
        if ((request.ExpectedVersionNo is not null && request.ExpectedVersionNo.Value != currentVersion)
            || (request.ExpectedRevision is not null && request.ExpectedRevision.Value != int.Parse(row[4], CultureInfo.InvariantCulture)))
            throw new CollectorStoreException("ARTICLE_VERSION_CONFLICT", "文章已被其他操作更新。", 409);
        ArticleEditSafety.Validate(request.Title, request.BodyMarkdown, string.IsNullOrEmpty(row[0]) ? null : row[3]);
        var tagIds = (request.TagIds ?? []).Where(id => id != Guid.Empty).Distinct().ToArray();
        if (tagIds.Length > 10 || (request.TagIds?.Count ?? 0) != tagIds.Length)
            throw new CollectorStoreException("INVALID_TAGS", "文章标签最多 10 个且不能重复。", 400);
        foreach (var tagId in tagIds)
        {
            var tag = await RowAsync(connection, transaction,
                "SELECT 1 FROM tags WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND id=UUID_TO_BIN(@tag) AND status='active';",
                cancellationToken, (Name: "@tenant", Value: (object?)tenantId.ToString()), (Name: "@site", Value: (object?)siteId.ToString()), (Name: "@tag", Value: (object?)tagId.ToString()));
            if (tag is null) throw new CollectorStoreException("RESOURCE_NOT_FOUND", "文章标签不存在或已归档。", 404);
        }
        var versionId = Guid.NewGuid(); var next = currentVersion + 1;
        await ExecAsync(connection, transaction, """
            INSERT INTO content_article_versions (id,tenant_id,site_id,article_id,version_no,version_kind,title,summary,body_markdown,seo_title,seo_description,keywords_json,content_length,language,created_by,created_at)
            VALUES (UUID_TO_BIN(@id),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@article),@version,'manual_edit',@title,@summary,@body,@seo_title,@seo_description,@keywords,CHAR_LENGTH(@body),@language,UUID_TO_BIN(@user),UTC_TIMESTAMP(6));
            UPDATE content_articles SET current_version_id=UUID_TO_BIN(@id),title=@title,language=@language,updated_at=UTC_TIMESTAMP(6)
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@article);
            DELETE FROM content_article_tags WHERE tenant_id=UUID_TO_BIN(@tenant) AND article_id=UUID_TO_BIN(@article);
            """, cancellationToken, ("@id", versionId.ToString()), ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()),
            ("@article", articleId.ToString()), ("@version", next), ("@title", request.Title.Trim()), ("@summary", request.Summary),
            ("@body", request.BodyMarkdown), ("@seo_title", request.SeoTitle), ("@seo_description", request.SeoDescription),
            ("@keywords", JsonSerializer.Serialize(request.Keywords ?? [])), ("@language", request.Language.Trim()), ("@user", userId.ToString()));
        foreach (var tagId in tagIds)
            await ExecAsync(connection, transaction,
                "INSERT INTO content_article_tags (tenant_id,site_id,article_id,tag_id,created_at) VALUES (UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@article),UUID_TO_BIN(@tag),UTC_TIMESTAMP(6));",
                cancellationToken, (Name: "@tenant", Value: (object?)tenantId.ToString()), (Name: "@site", Value: (object?)siteId.ToString()), (Name: "@article", Value: (object?)articleId.ToString()), (Name: "@tag", Value: (object?)tagId.ToString()));
        await transaction.CommitAsync(cancellationToken);
        return (await GetArticleAsync(tenantId, siteId, articleId, cancellationToken))!;
    }

    /// <inheritdoc />
    public async Task<ArticleView?> SetArchivedAsync(Guid tenantId, Guid siteId, Guid articleId, Guid userId,
        bool archived, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (archived)
        {
            var running = await RowAsync(connection, transaction,
                "SELECT 1 FROM article_generation_tasks WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND article_id=UUID_TO_BIN(@article) AND status IN ('pending','running') LIMIT 1 FOR UPDATE;",
                cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@article", articleId.ToString()));
            if (running is not null) throw new CollectorStoreException("ARTICLE_TASK_IN_PROGRESS", "文章存在执行中的生成任务，完成后才能归档。", 409);
        }
        var count = await ExecAsync(connection, transaction, "UPDATE content_articles SET status=@status,archived_at=CASE WHEN @status='archived' THEN COALESCE(archived_at,UTC_TIMESTAMP(6)) ELSE NULL END,updated_at=UTC_TIMESTAMP(6) WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND id=UUID_TO_BIN(@article);", cancellationToken,
            ("@status", archived ? "archived" : "active"), ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@article", articleId.ToString()));
        await transaction.CommitAsync(cancellationToken);
        if (count == 0) return null;
        var result = await GetArticleAsync(tenantId, siteId, articleId, cancellationToken); return result?.Article;
    }

    /// <inheritdoc />
    public async Task<ArticleView?> SetMonitorStatusAsync(Guid tenantId, Guid siteId, Guid articleId, Guid userId,
        ArticleMonitorStatus status, CancellationToken cancellationToken = default)
    {
        var value = status switch { ArticleMonitorStatus.Active => "active", ArticleMonitorStatus.Paused => "paused", _ => "inactive" };
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var article = await RowAsync(connection, transaction,
            "SELECT status,monitor_status FROM content_articles WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND id=UUID_TO_BIN(@article) FOR UPDATE;",
            cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@article", articleId.ToString()));
        if (article is null || !article[0].Equals("active", StringComparison.OrdinalIgnoreCase))
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var current = article[1] switch
        {
            "active" => ArticleMonitorStatus.Active,
            "paused" => ArticleMonitorStatus.Paused,
            _ => ArticleMonitorStatus.Inactive
        };
        if (current == status)
        {
            await transaction.CommitAsync(cancellationToken);
            var unchanged = await GetArticleAsync(tenantId, siteId, articleId, cancellationToken);
            return unchanged?.Article;
        }

        var scope = await RowAsync(connection, transaction,
            "SELECT BIN_TO_UUID(id),filter_snapshot,version FROM content_monitor_scopes WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND article_id=UUID_TO_BIN(@article) FOR UPDATE;",
            cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@article", articleId.ToString()));
        var now = DateTimeOffset.UtcNow;
        var transition = TransitionMonitorScope(scope?[1], status, now);
        if (scope is null)
        {
            await ExecAsync(connection, transaction, """
                INSERT INTO content_monitor_scopes
                    (id,tenant_id,site_id,article_id,mode,filter_snapshot,version,enabled_at,disabled_at,created_at,updated_at)
                VALUES
                    (UUID_TO_BIN(UUID()),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@article),
                     'all_brand_observations',@snapshot,1,@enabled_at,@disabled_at,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
                """, cancellationToken,
                ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@article", articleId.ToString()),
                ("@snapshot", transition.Snapshot), ("@enabled_at", transition.EnabledAt?.UtcDateTime),
                ("@disabled_at", transition.DisabledAt?.UtcDateTime));
        }
        else
        {
            await ExecAsync(connection, transaction, """
                UPDATE content_monitor_scopes
                SET filter_snapshot=@snapshot,version=version+1,enabled_at=@enabled_at,disabled_at=@disabled_at,
                    updated_at=UTC_TIMESTAMP(6)
                WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND article_id=UUID_TO_BIN(@article);
                """, cancellationToken,
                ("@snapshot", transition.Snapshot), ("@enabled_at", transition.EnabledAt?.UtcDateTime),
                ("@disabled_at", transition.DisabledAt?.UtcDateTime), ("@tenant", tenantId.ToString()),
                ("@site", siteId.ToString()), ("@article", articleId.ToString()));
        }
        await ExecAsync(connection, transaction,
            "UPDATE content_articles SET monitor_status=@status,updated_at=UTC_TIMESTAMP(6) WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND id=UUID_TO_BIN(@article) AND status='active';",
            cancellationToken, ("@status", value), ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@article", articleId.ToString()));
        await transaction.CommitAsync(cancellationToken);
        var result = await GetArticleAsync(tenantId, siteId, articleId, cancellationToken); return result?.Article;
    }

    /// <inheritdoc />
    public async Task<ArticlePublicationLinkView> AddPublicationLinkAsync(Guid tenantId, Guid siteId, Guid articleId,
        Guid userId, ArticlePublicationLinkWrite request, CancellationToken cancellationToken = default)
    {
        if (request.Channel?.Length > 64)
            throw new CollectorStoreException("INVALID_PUBLICATION_LINK", "发布渠道不能超过 64 个字符。", 400);
        var normalized = ArticleSourceReader.NormalizeAndValidate(request.Url);
        var hash = Hash(normalized);
        // An explicit client value still wins (older clients send it); otherwise
        // derive official-site ownership from the current brand's domains before
        // the transaction so no row lock is held while reading the brand.
        var ownership = request.OwnershipMatch
            ?? ComputeOwnership(normalized, await management.GetBrandAsync(tenantId, siteId, cancellationToken));
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var article = await RowAsync(connection, transaction, "SELECT status FROM content_articles WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND id=UUID_TO_BIN(@article) FOR UPDATE;", cancellationToken,
            ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@article", articleId.ToString()));
        if (article is null) throw NotFound();
        if (article[0].Equals("archived", StringComparison.OrdinalIgnoreCase)) throw new CollectorStoreException("ARTICLE_ARCHIVED", "文章已归档，不能新增发布链接。", 409);
        if (request.ArticleVersionId is not null)
        {
            var version = await RowAsync(connection, transaction,
                "SELECT 1 FROM content_article_versions WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND article_id=UUID_TO_BIN(@article) AND id=UUID_TO_BIN(@version);",
                cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@article", articleId.ToString()), ("@version", request.ArticleVersionId.Value.ToString()));
            if (version is null) throw new CollectorStoreException("RESOURCE_NOT_FOUND", "对应文章版本不存在。", 404);
        }
        var activeCount = await RowAsync(connection, transaction, "SELECT COUNT(*) FROM content_publication_links WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND article_id=UUID_TO_BIN(@article) AND status='active';", cancellationToken,
            ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@article", articleId.ToString()));
        var existing = await RowAsync(connection, transaction, "SELECT BIN_TO_UUID(p.id),BIN_TO_UUID(p.article_id),p.status FROM content_publication_links p JOIN content_references r ON r.tenant_id=p.tenant_id AND r.id=p.reference_id WHERE p.tenant_id=UUID_TO_BIN(@tenant) AND p.site_id=UUID_TO_BIN(@site) AND r.url_hash=@hash LIMIT 1;", cancellationToken,
            ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@hash", hash));
        if (existing is not null && !Guid.Parse(existing[1]).Equals(articleId)) throw new CollectorStoreException("DUPLICATE_PUBLICATION_URL", "发布链接已归属于其他文章。", 409);
        if (existing is null && int.Parse(activeCount![0], CultureInfo.InvariantCulture) >= 20) throw new CollectorStoreException("PUBLICATION_LINK_LIMIT", "每篇文章最多登记 20 个有效发布链接。", 400);
        var referenceId = existing is null ? Guid.NewGuid() : (await RowAsync(connection, transaction, "SELECT BIN_TO_UUID(reference_id) FROM content_publication_links WHERE id=UUID_TO_BIN(@id);", cancellationToken, ("@id", existing[0])))![0] is var referenceText ? Guid.Parse(referenceText) : Guid.NewGuid();
        if (existing is null)
            await ExecAsync(connection, transaction, "INSERT INTO content_references (id,tenant_id,site_id,url_original,url_normalized,url_hash,kind,read_status,created_at,updated_at) VALUES (UUID_TO_BIN(@id),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),@original,@normalized,@hash,'publication','pending',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));", cancellationToken,
                ("@id", referenceId.ToString()), ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@original", request.Url.Trim()), ("@normalized", normalized), ("@hash", hash));
        var linkId = existing is null ? Guid.NewGuid() : Guid.Parse(existing[0]);
        if (existing is null)
            await ExecAsync(connection, transaction, "INSERT INTO content_publication_links (id,tenant_id,site_id,article_id,article_version_id,reference_id,channel,published_at,status,ownership_match,created_by,created_at) VALUES (UUID_TO_BIN(@id),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@article),UUID_TO_BIN(@version),UUID_TO_BIN(@reference),@channel,@published,'active',@ownership,UUID_TO_BIN(@user),UTC_TIMESTAMP(6));", cancellationToken,
                ("@id", linkId.ToString()), ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@article", articleId.ToString()), ("@version", request.ArticleVersionId?.ToString()), ("@reference", referenceId.ToString()), ("@channel", request.Channel), ("@published", request.PublishedAt?.UtcDateTime), ("@ownership", ownership), ("@user", userId.ToString()));
        else if (existing[2].Equals("disabled", StringComparison.OrdinalIgnoreCase))
            await ExecAsync(connection, transaction, "UPDATE content_publication_links SET status='active',disabled_at=NULL,channel=@channel,published_at=@published,article_version_id=UUID_TO_BIN(@version),ownership_match=@ownership WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND id=UUID_TO_BIN(@id);", cancellationToken,
                ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@id", linkId.ToString()), ("@version", request.ArticleVersionId?.ToString()), ("@channel", request.Channel), ("@published", request.PublishedAt?.UtcDateTime), ("@ownership", ownership));
        await transaction.CommitAsync(cancellationToken);
        var result = await RowAsync(connection, null, "SELECT BIN_TO_UUID(p.id),BIN_TO_UUID(p.article_id),BIN_TO_UUID(p.article_version_id),r.url_original,r.url_normalized,r.url_hash,p.channel,p.published_at,p.status,p.ownership_match,r.read_status,p.created_at,p.disabled_at,BIN_TO_UUID(p.discovered_check_id),p.discovered_at FROM content_publication_links p JOIN content_references r ON r.tenant_id=p.tenant_id AND r.id=p.reference_id WHERE p.tenant_id=UUID_TO_BIN(@tenant) AND p.id=UUID_TO_BIN(@id);", cancellationToken,
            ("@tenant", tenantId.ToString()), ("@id", linkId.ToString()));
        return ToLink(result!);
    }

    /// <inheritdoc />
    public async Task<ArticlePublicationLinkView?> DisablePublicationLinkAsync(Guid tenantId, Guid siteId, Guid articleId,
        Guid linkId, Guid userId, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        await ExecAsync(connection, null, "UPDATE content_publication_links SET status='disabled',disabled_at=UTC_TIMESTAMP(6) WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND article_id=UUID_TO_BIN(@article) AND id=UUID_TO_BIN(@link);", cancellationToken,
            ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@article", articleId.ToString()), ("@link", linkId.ToString()));
        var row = await RowAsync(connection, null, "SELECT BIN_TO_UUID(p.id),BIN_TO_UUID(p.article_id),BIN_TO_UUID(p.article_version_id),r.url_original,r.url_normalized,r.url_hash,p.channel,p.published_at,p.status,p.ownership_match,r.read_status,p.created_at,p.disabled_at,BIN_TO_UUID(p.discovered_check_id),p.discovered_at FROM content_publication_links p JOIN content_references r ON r.tenant_id=p.tenant_id AND r.id=p.reference_id WHERE p.tenant_id=UUID_TO_BIN(@tenant) AND p.site_id=UUID_TO_BIN(@site) AND p.article_id=UUID_TO_BIN(@article) AND p.id=UUID_TO_BIN(@link);", cancellationToken,
            ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@article", articleId.ToString()), ("@link", linkId.ToString()));
        return row is null ? null : ToLink(row);
    }

    /// <inheritdoc />
    public async Task<ArticleImportPreviewView> PreviewImportAsync(Guid tenantId, Guid siteId, Guid userId,
        IReadOnlyList<string> urls, CancellationToken cancellationToken = default)
    {
        if (urls is null || urls.Count is < 1 or > 20) throw new CollectorStoreException("IMPORT_LIMIT", "一次最多导入 20 个链接。", 400);
        await using (var siteConnection = database.CreateConnection())
        {
            await siteConnection.OpenAsync(cancellationToken);
            await AssertSiteAsync(siteConnection, tenantId, siteId, cancellationToken);
        }
        var items = new List<ArticleImportPreviewItem>();
        foreach (var url in urls.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ArticleSourceReadResult result;
            try { result = await sourceReader.ReadAsync(url, cancellationToken); }
            catch (CollectorStoreException exception) { items.Add(new ArticleImportPreviewItem(url, null, null, null, null, "unreadable", exception.Code, exception.Message, false, null)); continue; }
            var existing = await FindArticleByUrlAsync(tenantId, siteId, result.UrlHash, cancellationToken);
            items.Add(new ArticleImportPreviewItem(result.UrlOriginal, result.UrlNormalized, result.UrlHash, result.Title, result.BodyMarkdown, result.ReadStatus, result.ErrorCode, result.ErrorMessage, existing is not null, existing));
        }
        // Include the site in the idempotency hash.  The same URL list can be
        // previewed for two brands in one tenant and must not collide on the
        // tenant-scoped unique request hash.
        var requestHash = Hash(JsonSerializer.Serialize(new { SiteId = siteId, Items = items }, JsonOptions));
        var preview = new ArticleImportPreviewView(Guid.NewGuid(), requestHash, items,
            DateTimeOffset.UtcNow.AddMinutes(15));
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        await ExecAsync(connection, null, "INSERT INTO article_import_previews (id,tenant_id,site_id,created_by,request_hash,response_json,expires_at,created_at) VALUES (UUID_TO_BIN(@id),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@user),@hash,@response,@expires,UTC_TIMESTAMP(6));", cancellationToken,
            ("@id", preview.PreviewId.ToString()), ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@user", userId.ToString()), ("@hash", preview.RequestHash), ("@response", JsonSerializer.Serialize(preview, JsonOptions)), ("@expires", preview.ExpiresAt.UtcDateTime));
        return preview;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ArticleView>> ConfirmImportAsync(Guid tenantId, Guid siteId, Guid userId,
        ArticleImportConfirmRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey)) throw new CollectorStoreException("IDEMPOTENCY_KEY_REQUIRED", "请提供 Idempotency-Key。", 400);
        if (idempotencyKey.Length > 191) throw new CollectorStoreException("INVALID_IDEMPOTENCY_KEY", "幂等键无效。", 400);
        var titleOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var overrideItem in request.TitleOverrides ?? [])
        {
            if (string.IsNullOrWhiteSpace(overrideItem.UrlHash)
                || overrideItem.UrlHash.Length != 64
                || !overrideItem.UrlHash.All(Uri.IsHexDigit)
                || string.IsNullOrWhiteSpace(overrideItem.Title)
                || overrideItem.Title.Trim().Length is < 1 or > 200
                || !titleOverrides.TryAdd(overrideItem.UrlHash, overrideItem.Title.Trim()))
                throw new CollectorStoreException("INVALID_IMPORT_TITLE", "导入标题覆盖无效或重复。", 400);
        }
        // Imported publication links get the same server-side official-site
        // ownership as manually registered links.
        var brand = await management.GetBrandAsync(tenantId, siteId, cancellationToken);
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var keyOwner = await RowAsync(connection, transaction,
            "SELECT BIN_TO_UUID(id) FROM article_import_previews WHERE tenant_id=UUID_TO_BIN(@tenant) AND confirm_idempotency_key=@key LIMIT 1 FOR UPDATE;",
            cancellationToken, ("@tenant", tenantId.ToString()), ("@key", idempotencyKey));
        if (keyOwner is not null && !Guid.Parse(keyOwner[0]).Equals(request.PreviewId))
            throw new CollectorStoreException("IDEMPOTENCY_CONFLICT", "幂等键已用于其他导入预览。", 409);
        var row = await RowAsync(connection, transaction, "SELECT response_json,request_hash,expires_at,confirm_idempotency_key,confirmed_article_ids_json FROM article_import_previews WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND id=UUID_TO_BIN(@id) FOR UPDATE;", cancellationToken,
            ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@id", request.PreviewId.ToString()));
        if (row is null) throw new CollectorStoreException("RESOURCE_NOT_FOUND", "导入预览不存在。", 404);
        if (!string.Equals(row[1], request.RequestHash, StringComparison.Ordinal)) throw new CollectorStoreException("IDEMPOTENCY_CONFLICT", "导入预览摘要不匹配。", 409);
        var alreadyConfirmed = !string.IsNullOrWhiteSpace(row[3]);
        if (alreadyConfirmed)
        {
            if (!string.Equals(row[3], idempotencyKey, StringComparison.Ordinal)) throw new CollectorStoreException("IMPORT_ALREADY_CONFIRMED", "该导入预览已经确认。", 409);
            var confirmedIds = DeserializeGuidArray(row[4]);
            await transaction.CommitAsync(cancellationToken);
            var confirmed = new List<ArticleView>();
            foreach (var articleId in confirmedIds)
            {
                var article = await GetArticleAsync(tenantId, siteId, articleId, cancellationToken);
                if (article is not null) confirmed.Add(article.Article);
            }
            return confirmed;
        }
        if (ParseDate(row[2]) <= DateTimeOffset.UtcNow) throw new CollectorStoreException("ARTICLE_PREVIEW_EXPIRED", "导入预览已过期。", 409);
        var preview = JsonSerializer.Deserialize<ArticleImportPreviewView>(row[0], JsonOptions)!;
        var articleIds = new List<Guid>();
        foreach (var item in preview.Items.Where(i => !i.Duplicate && i.UrlNormalized is not null
            && (i.ReadStatus is "ready" or "blocked" or "unreadable")))
        {
            string? overrideTitle = null;
            var hasOverride = item.UrlHash is not null && titleOverrides.TryGetValue(item.UrlHash, out overrideTitle);
            if (item.ReadStatus is "blocked" or "unreadable"
                && !hasOverride && string.IsNullOrWhiteSpace(item.Title))
                throw new CollectorStoreException("IMPORT_TITLE_REQUIRED", "不可读链接需要手动填写标题后才能导入。", 422);
            var existing = await RowAsync(connection, transaction,
                "SELECT BIN_TO_UUID(p.article_id),p.status FROM content_publication_links p JOIN content_references r ON r.tenant_id=p.tenant_id AND r.id=p.reference_id WHERE p.tenant_id=UUID_TO_BIN(@tenant) AND p.site_id=UUID_TO_BIN(@site) AND r.url_hash=@hash LIMIT 1 FOR UPDATE;",
                cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@hash", item.UrlHash!));
            if (existing is not null)
            {
                if (Guid.TryParse(existing[0], out var existingArticleId)) articleIds.Add(existingArticleId);
                continue;
            }
            var articleId = Guid.NewGuid();
            var title = hasOverride ? overrideTitle! : string.IsNullOrWhiteSpace(item.Title) ? item.UrlNormalized! : item.Title!;
            title = title.Trim(); if (title.Length > 500) title = title[..500];
            await InsertImportedArticleAsync(connection, transaction, new ImportedArticleInsert(tenantId, siteId, userId, articleId,
                title[..Math.Min(200, title.Length)], item.BodyMarkdown, item.UrlOriginal, item.UrlNormalized!, item.UrlHash!,
                item.ReadStatus, request.Channels?.FirstOrDefault(), ComputeOwnership(item.UrlNormalized!, brand), null), cancellationToken);
            articleIds.Add(articleId);
        }
        await ExecAsync(connection, transaction,
            "UPDATE article_import_previews SET confirm_idempotency_key=@key,confirmed_article_ids_json=@ids,confirmed_at=UTC_TIMESTAMP(6) WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@id) AND confirm_idempotency_key IS NULL;",
            cancellationToken, ("@key", idempotencyKey), ("@ids", JsonSerializer.Serialize(articleIds)), ("@tenant", tenantId.ToString()), ("@id", request.PreviewId.ToString()));
        await transaction.CommitAsync(cancellationToken);
        var result = new List<ArticleView>();
        foreach (var articleId in articleIds)
        {
            var article = await GetArticleAsync(tenantId, siteId, articleId, cancellationToken);
            if (article is not null) result.Add(article.Article);
        }
        return result;
    }

    /// <summary>新建一篇导入文章所需的值；<see cref="ExistingReferenceId"/> 非空时复用已有的发布引用而不再插入。</summary>
    private sealed record ImportedArticleInsert(Guid TenantId, Guid SiteId, Guid UserId, Guid ArticleId, string Title,
        string? BodyMarkdown, string UrlOriginal, string UrlNormalized, string UrlHash, string ReadStatus, string? Channel,
        bool? Ownership, Guid? ExistingReferenceId);

    /// <summary>
    /// 导入确认与自有标记共用的建文章写入：published_import 文章（追踪开启）、从现在起的监控窗口、
    /// 有正文时的 imported 版本、发布引用（未复用时）和一条有效发布链接。调用方负责事务与重复检查。
    /// </summary>
    private static async Task InsertImportedArticleAsync(MySqlConnection connection, MySqlTransaction transaction,
        ImportedArticleInsert insert, CancellationToken cancellationToken)
    {
        var body = insert.BodyMarkdown;
        var versionId = string.IsNullOrWhiteSpace(body) ? (Guid?)null : Guid.NewGuid();
        var referenceId = insert.ExistingReferenceId ?? Guid.NewGuid();
        var versionSql = versionId is null
            ? ""
            : "INSERT INTO content_article_versions (id,tenant_id,site_id,article_id,version_no,version_kind,title,body_markdown,content_length,language,created_by,created_at) VALUES (UUID_TO_BIN(@version),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@article),1,'imported',@title,@body,CHAR_LENGTH(@body),'und',UUID_TO_BIN(@user),UTC_TIMESTAMP(6)); UPDATE content_articles SET current_version_id=UUID_TO_BIN(@version),updated_at=UTC_TIMESTAMP(6) WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@article);";
        var referenceSql = insert.ExistingReferenceId is not null
            ? ""
            : "INSERT INTO content_references (id,tenant_id,site_id,url_original,url_normalized,url_hash,kind,read_status,title,created_at,updated_at) VALUES (UUID_TO_BIN(@reference),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),@original,@normalized,@hash,'publication',@read_status,@title,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));";
        await ExecAsync(connection, transaction, $"INSERT INTO content_articles (id,tenant_id,site_id,created_by,source_kind,status,title,language,monitor_status,current_version_id,created_at,updated_at) VALUES (UUID_TO_BIN(@article),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@user),'published_import','active',@title,'und','active',NULL,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6)); INSERT INTO content_monitor_scopes (id,tenant_id,site_id,article_id,mode,filter_snapshot,version,enabled_at,created_at,updated_at) VALUES (UUID_TO_BIN(UUID()),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@article),'all_brand_observations',JSON_OBJECT('activeWindows',JSON_ARRAY(JSON_OBJECT('from',DATE_FORMAT(UTC_TIMESTAMP(6),'%Y-%m-%d %H:%i:%s.%f'),'to',NULL))),1,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6),UTC_TIMESTAMP(6)); {versionSql} {referenceSql} INSERT INTO content_publication_links (id,tenant_id,site_id,article_id,article_version_id,reference_id,channel,status,ownership_match,created_by,created_at) VALUES (UUID_TO_BIN(UUID()),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@article),UUID_TO_BIN(@version),UUID_TO_BIN(@reference),@channel,'active',@ownership,UUID_TO_BIN(@user),UTC_TIMESTAMP(6));", cancellationToken,
            ("@article", insert.ArticleId.ToString()), ("@version", versionId?.ToString()), ("@reference", referenceId.ToString()),
            ("@tenant", insert.TenantId.ToString()), ("@site", insert.SiteId.ToString()), ("@user", insert.UserId.ToString()),
            ("@title", insert.Title), ("@body", body), ("@original", insert.UrlOriginal), ("@normalized", insert.UrlNormalized),
            ("@hash", insert.UrlHash), ("@read_status", insert.ReadStatus), ("@channel", insert.Channel), ("@ownership", insert.Ownership));
    }
}
