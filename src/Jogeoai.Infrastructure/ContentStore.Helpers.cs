using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

public sealed partial class ContentStore
{
    private sealed record MonitorWindow(DateTimeOffset From, DateTimeOffset? To);

    private sealed record CitationWindow(DateTimeOffset FromUtc, DateTimeOffset ToUtc, TimeZoneInfo Zone);

    // GROUP_CONCAT's SEPARATOR must be a literal.  Embed the real U+001F
    // character: a raw C# string would send the six characters "\u001f",
    // which MySQL turns into "u001f" and ToArticle could never split.
    private const string TagSeparator = "\u001f";

    /// <summary>prompt_snapshot 是 Web 默认（camelCase）序列化的 PromptView；列表 SQL 按这些固定路径读取摘要。</summary>
    internal const string PromptIdJsonPath = "$.promptId";
    /// <summary>prompt_snapshot 中提问内容的 JSON 路径。</summary>
    internal const string PromptContentJsonPath = "$.content";
    /// <summary>prompt_snapshot 中提问语言地区的 JSON 路径。</summary>
    internal const string PromptLocaleJsonPath = "$.locale";
    /// <summary>prompt_snapshot 中提问分组的 JSON 路径。</summary>
    internal const string PromptGroupIdJsonPath = "$.groupId";

    /// <summary>外层文章行 a 的有效（未停用）发布链接数。</summary>
    private const string ArticleActiveLinksSql =
        "(SELECT COUNT(*) FROM content_publication_links p WHERE p.tenant_id=a.tenant_id AND p.article_id=a.id AND p.status='active')";

    /// <summary>外层文章行 a 是否存在有效（未停用）发布链接。</summary>
    private const string ArticleHasActiveLinkSql =
        "EXISTS(SELECT 1 FROM content_publication_links hp WHERE hp.tenant_id=a.tenant_id AND hp.article_id=a.id AND hp.status='active')";

    /// <summary>外层文章行 a 是否存在归属官网域名的有效发布链接。</summary>
    private const string ArticleOwnedSql =
        "EXISTS(SELECT 1 FROM content_publication_links op WHERE op.tenant_id=a.tenant_id AND op.article_id=a.id AND op.status='active' AND op.ownership_match=1)";

    /// <summary>阶段派生表达式；判断顺序必须与 <see cref="ArticleStageRules.Derive"/> 完全一致。</summary>
    internal const string ArticleStageSql =
        "CASE WHEN a.status='archived' THEN 'archived'"
        + " WHEN a.current_version_id IS NULL AND lt.status IN ('pending','running') THEN 'generating'"
        + " WHEN a.current_version_id IS NULL AND lt.status IN ('failed','cancelled','stale') THEN 'generation_failed'"
        + " WHEN " + ArticleActiveLinksSql + "=0 THEN 'pending_publish'"
        + " WHEN a.monitor_status='active' THEN 'tracking'"
        + " ELSE 'publishing' END";

    /// <summary>列表、计数与详情共用的 FROM/JOIN：当前版本、最新一次生成任务（LATERAL）及其积分预占、最早的自动发现链接（LATERAL）。</summary>
    private const string ArticleJoinsSql = """
        FROM content_articles a
        LEFT JOIN content_article_versions v ON v.tenant_id=a.tenant_id AND v.id=a.current_version_id
        LEFT JOIN LATERAL (
            SELECT t.id,t.batch_id,t.status,t.attempt_count,t.last_error_code,t.last_error_message,
                   t.created_at,t.updated_at,t.prompt_snapshot,t.reservation_id
            FROM article_generation_tasks t
            WHERE t.tenant_id=a.tenant_id AND t.site_id=a.site_id AND t.article_id=a.id
            ORDER BY t.created_at DESC, t.id DESC
            LIMIT 1
        ) AS lt ON TRUE
        LEFT JOIN credit_reservations lr ON lr.tenant_id=a.tenant_id AND lr.id=lt.reservation_id
        LEFT JOIN LATERAL (
            SELECT dp.discovered_check_id,dp.discovered_at,dc.provider,dc.engine,dc.capture_mode
            FROM content_publication_links dp
            LEFT JOIN visibility_checks dc ON dc.tenant_id=dp.tenant_id AND dc.site_id=dp.site_id AND dc.id=dp.discovered_check_id
            WHERE dp.tenant_id=a.tenant_id AND dp.site_id=a.site_id AND dp.article_id=a.id AND dp.discovered_check_id IS NOT NULL
            ORDER BY dp.discovered_at, dp.id
            LIMIT 1
        ) AS dl ON TRUE
        """;

    /// <summary>列表与详情共用的列清单；<see cref="ToArticle"/> 按此顺序读取，共 <see cref="ArticleColumnCount"/> 列。</summary>
    private const string ArticleColumnsSql =
        "BIN_TO_UUID(a.id),BIN_TO_UUID(a.tenant_id),BIN_TO_UUID(a.site_id),BIN_TO_UUID(a.created_by),"
        + "a.source_kind,a.status,a.title,a.language,a.monitor_status,"
        + "COALESCE(v.version_no,0),a.created_at,a.updated_at,a.archived_at,"
        + ArticleActiveLinksSql + ","
        + "COALESCE((SELECT GROUP_CONCAT(tg.name ORDER BY tg.name SEPARATOR '" + TagSeparator + "') FROM content_article_tags at JOIN tags tg ON tg.tenant_id=at.tenant_id AND tg.site_id=at.site_id AND tg.id=at.tag_id WHERE at.tenant_id=a.tenant_id AND at.site_id=a.site_id AND at.article_id=a.id),''),"
        + ArticleStageSql + ","
        + "BIN_TO_UUID(lt.id),BIN_TO_UUID(lt.batch_id),lt.status,lt.attempt_count,lt.last_error_code,lt.last_error_message,"
        + "lt.created_at,lt.updated_at,COALESCE(lr.status,''),"
        + "JSON_UNQUOTE(JSON_EXTRACT(lt.prompt_snapshot,'" + PromptIdJsonPath + "')),"
        + "JSON_UNQUOTE(JSON_EXTRACT(lt.prompt_snapshot,'" + PromptContentJsonPath + "')),"
        + "JSON_UNQUOTE(JSON_EXTRACT(lt.prompt_snapshot,'" + PromptLocaleJsonPath + "')),"
        + "JSON_UNQUOTE(JSON_EXTRACT(lt.prompt_snapshot,'" + PromptGroupIdJsonPath + "')),"
        + ArticleOwnedSql + ","
        + "BIN_TO_UUID(dl.discovered_check_id),dl.discovered_at,COALESCE(dl.provider,''),COALESCE(dl.engine,''),COALESCE(dl.capture_mode,'')";

    /// <summary><see cref="ArticleColumnsSql"/> 的列数；列表额外追加的排序列从此索引开始。</summary>
    private const int ArticleColumnCount = 35;

    /// <summary>按被引时间排序时，没有被引时间的文章使用的排序下限（排在最后）；真实观测时间不会早于该值。</summary>
    private const string CitationSortFloorSql = "CAST('1000-01-01 00:00:00' AS DATETIME(6))";

    /// <summary><see cref="CitationSortFloorSql"/> 对应的 UTC 时间，用于游标参数。</summary>
    private static readonly DateTime CitationSortFloor = new(1000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// 观测是否落在文章追踪范围内：没有范围记录时以文章当前追踪状态为准，否则按范围记录的启用时间或追踪时间窗。
    /// 依赖别名：外层文章 a、追踪范围 ms（LEFT JOIN，可为空）、回答 vc。列表行内引用摘要、被引判定与按被引时间排序共用此口径。
    /// </summary>
    private const string CitationMonitorScopeSql = """
        (
            (ms.id IS NULL AND a.monitor_status='active')
            OR (ms.id IS NOT NULL AND ms.filter_snapshot IS NULL AND a.monitor_status='active'
                AND (ms.enabled_at IS NULL OR vc.captured_at>=ms.enabled_at))
            OR (ms.id IS NOT NULL AND EXISTS (
                SELECT 1
                FROM JSON_TABLE(
                    COALESCE(JSON_EXTRACT(ms.filter_snapshot,'$.activeWindows'), JSON_ARRAY()),
                    '$[*]' COLUMNS (
                        window_from DATETIME(6) PATH '$.from',
                        window_to DATETIME(6) PATH '$.to' NULL ON EMPTY
                    )
                ) AS monitor_window
                WHERE vc.captured_at>=monitor_window.window_from
                  AND (monitor_window.window_to IS NULL OR vc.captured_at<monitor_window.window_to)
            ))
        )
        """;

    /// <summary>
    /// 外层文章行 a 在统计区间（@citation_from–@citation_to）内被引用的观测（FROM…WHERE 片段）：发布链接 → 引用事实 →
    /// 已发布分析版本中的回答，链接发布（未填写取登记时间）后、停用前，且在文章追踪范围内；与
    /// <see cref="LoadArticleCitationSummariesAsync"/> 同口径。<paramref name="engineScoped"/> 时只计 @citation_engine 平台。
    /// </summary>
    private static string ArticleCitationObservationsSql(bool engineScoped) => $"""
        FROM content_publication_links cop
        JOIN content_references cor ON cor.tenant_id=cop.tenant_id AND cor.id=cop.reference_id
        JOIN citation_events ce ON ce.url_hash=cor.url_hash
        JOIN visibility_checks vc ON vc.tenant_id=cop.tenant_id AND vc.site_id=cop.site_id AND vc.id=ce.check_id
            AND vc.captured_at>=@citation_from AND vc.captured_at<@citation_to
        JOIN analysis_releases ar ON ar.tenant_id=cop.tenant_id AND ar.site_id=cop.site_id
            AND ar.id=ce.release_id AND ar.status='published'
        LEFT JOIN content_monitor_scopes ms ON ms.tenant_id=cop.tenant_id AND ms.site_id=cop.site_id AND ms.article_id=cop.article_id
        WHERE cop.tenant_id=a.tenant_id AND cop.site_id=a.site_id AND cop.article_id=a.id
          AND COALESCE(cop.published_at,cop.created_at)<=vc.captured_at
          AND (cop.status='active' OR cop.disabled_at IS NULL OR vc.captured_at<cop.disabled_at){(engineScoped ? " AND vc.engine=@citation_engine" : "")}
          AND {CitationMonitorScopeSql}
        """;

    /// <summary>规范化并校验文章列表筛选；非法取值抛出 400 INVALID_FILTER。</summary>
    internal static ArticleListFilter NormalizeFilter(ArticleListFilter filter)
    {
        var query = string.IsNullOrWhiteSpace(filter.Query) ? null : filter.Query.Trim();
        if (query?.Length > 200) throw new CollectorStoreException("INVALID_FILTER", "文章搜索词不能超过 200 字。", 400);
        var sort = filter.Sort.Trim().ToLowerInvariant();
        if (sort is not ("created_desc" or "updated_desc" or "citation_desc" or "last_cited_desc" or "first_cited_desc"))
            throw new CollectorStoreException("INVALID_FILTER", "文章排序方式无效。", 400);
        var language = string.IsNullOrWhiteSpace(filter.Language) ? null : filter.Language.Trim();
        if (language is not (null or "zh-CN" or "en-US")) throw new CollectorStoreException("INVALID_FILTER", "文章语言筛选仅支持 zh-CN 或 en-US。", 400);
        if (filter.CitationDays is not (7 or 30 or 90))
            throw new CollectorStoreException("INVALID_FILTER", "引用统计区间仅支持 7、30 或 90 天。", 400);
        var engine = string.IsNullOrWhiteSpace(filter.CitationEngine) ? null : filter.CitationEngine.Trim().ToLowerInvariant();
        if (engine?.Length > 64) throw new CollectorStoreException("INVALID_FILTER", "引用平台代码不能超过 64 个字符。", 400);
        var ownership = filter.Ownership;
        if (filter.OwnedOnly)
        {
            if (ownership == ArticleOwnershipFilter.External)
                throw new CollectorStoreException("INVALID_FILTER", "仅看官网文章（ownedOnly）不能与外部域名（ownership=external）同时使用。", 400);
            ownership = ArticleOwnershipFilter.Owned;
        }
        return filter with
        {
            Query = query, Sort = sort, Limit = Math.Clamp(filter.Limit, 1, 100), Language = language,
            CitationEngine = engine, Ownership = ownership, OwnedOnly = ownership == ArticleOwnershipFilter.Owned
        };
    }

    /// <summary>列表总数口径：阶段筛选与状态筛选（archived 与阶段 archived 等价）均需命中。</summary>
    private static bool InListScope(ArticleStage stage, ArticleListFilter filter)
        => (filter.Stage is null || filter.Stage == stage)
            && (filter.Status is null || (filter.Status == ArticleStatus.Archived) == (stage == ArticleStage.Archived));

    private static void ValidateEdit(ArticleEditRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Trim().Length > 200) throw new CollectorStoreException("INVALID_ARTICLE", "文章标题不能为空且不能超过 200 字。", 400);
        if (string.IsNullOrWhiteSpace(request.BodyMarkdown) || request.BodyMarkdown.Length > 100_000) throw new CollectorStoreException("INVALID_ARTICLE", "文章正文为空或超过 100000 字符。", 400);
        if (request.Summary?.Length > 2_000 || request.SeoTitle?.Length > 200 || request.SeoDescription?.Length > 500)
            throw new CollectorStoreException("INVALID_ARTICLE", "SEO 标题或摘要超过长度限制。", 400);
        // 与生成结果同口径（ArticleGenerationProvider 保留最多 10 个关键词）；否则编辑生成稿会被迫丢弃关键词。
        if (request.Keywords is { Count: > 10 } || request.Keywords?.Any(keyword => string.IsNullOrWhiteSpace(keyword) || keyword.Trim().Length > 50) == true)
            throw new CollectorStoreException("INVALID_ARTICLE", "关键词最多 10 个且每个不能超过 50 字。", 400);
        if (request.Language is not ("zh-CN" or "en-US")) throw new CollectorStoreException("INVALID_LANGUAGE", "文章语言暂不支持。", 400);
        if (request.ExpectedRevision is < 0) throw new CollectorStoreException("INVALID_ARTICLE", "修订号无效。", 400);
        // 编辑侧安全检查与草稿自动保存共用；正文在事务内对照当前版本检查（见 EditArticleAsync），SEO 字段同样会进入发布页面。
        if (ArticleEditSafety.FindUnsafe(request.Title) is { } titleReason)
            throw new CollectorStoreException(ArticleEditorErrorCodes.ContentUnsafe, $"标题包含不允许的内容（{titleReason}）。", 400);
        foreach (var field in new[] { request.Summary, request.SeoTitle, request.SeoDescription })
            if (ArticleEditSafety.FindUnsafe(field) is { } reason)
                throw new CollectorStoreException(ArticleEditorErrorCodes.ContentUnsafe, $"摘要或 SEO 字段包含不允许的内容（{reason}）。", 400);
    }

    private static void ValidateGenerationRequest(ArticleGenerationPreviewRequest request)
    {
        if (!ArticleWritingRules.IsSupportedStrategy(request.Strategy))
            throw new CollectorStoreException("INVALID_STRATEGY", "文章类型无效。", 400);
        if (!ArticleWritingRules.IsSupportedLength(request.LengthProfile))
            throw new CollectorStoreException("INVALID_LENGTH_PROFILE", "文章篇幅档位无效。", 400);
        if (request.Audience?.Length > 200)
            throw new CollectorStoreException("INVALID_GENERATION_REQUEST", "受众描述不能超过 200 字。", 400);
        if (request.ExtraRequirements?.Length > 2_000 || request.MaterialNotes?.Length > 2_000)
            throw new CollectorStoreException("INVALID_GENERATION_REQUEST", "写作补充要求或文章重点不能超过 2000 字。", 400);
        if (request.Keywords is { Count: > 5 }
            || request.Keywords?.Any(keyword => string.IsNullOrWhiteSpace(keyword) || keyword.Trim().Length > 50) == true)
            throw new CollectorStoreException("INVALID_GENERATION_REQUEST", "主关键词最多 5 个且每个不能超过 50 字。", 400);
        if (request.ReferenceUrls is { Count: > 5 })
            throw new CollectorStoreException("REFERENCE_LIMIT", "参考链接最多 5 个。", 400);
    }

    private static CollectorStoreException NotFound() => new("RESOURCE_NOT_FOUND", "文章不存在。", 404);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string? Null(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static Guid? ParseGuid(string? value) => Guid.TryParse(value, out var id) && id != Guid.Empty ? id : null;
    // MySqlConnector returns BOOLEAN (TINYINT(1)) columns as System.Boolean, which
    // Convert.ToString renders as "True"/"False"; integer expressions render "1"/"0".
    internal static bool ParseBool(string? value) => value is "1" or "true" or "True" or "TRUE";
    private static DateTimeOffset ParseDate(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    private static string ArticleCursorScope(ArticleListFilter filter, CitationWindow citationWindow)
        => Hash(JsonSerializer.Serialize(new
        {
            filter.Query, filter.Status, filter.SourceKind, filter.MonitorStatus, filter.CreatedBy,
            filter.TagId, filter.CreatedFrom, filter.CreatedTo, filter.Sort, filter.PromptId, filter.TargetId,
            citationWindow.FromUtc, citationWindow.ToUtc, filter.Stage, filter.Language, filter.OwnedOnly,
            filter.CitationDays, filter.Cited, filter.CitationEngine, filter.Ownership
        }));
    private static IReadOnlyList<string> DeserializeStrings(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        try { return JsonSerializer.Deserialize<string[]>(value) ?? []; } catch { return []; }
    }
    private static IReadOnlyList<Guid> DeserializeGuidArray(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        try { return JsonSerializer.Deserialize<Guid[]>(value) ?? []; } catch { return []; }
    }

    private static ArticleView ToArticle(string[] row)
    {
        var source = ParseSourceKind(row[4]);
        var status = row[5].Equals("archived", StringComparison.OrdinalIgnoreCase) ? ArticleStatus.Archived : ArticleStatus.Active;
        var monitor = row[8] switch { "active" => ArticleMonitorStatus.Active, "paused" => ArticleMonitorStatus.Paused, _ => ArticleMonitorStatus.Inactive };
        var tags = string.IsNullOrWhiteSpace(row[14]) ? [] : row[14].Split('\u001f', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var stage = ArticleStageRules.TryParse(row[15], out var parsedStage) ? parsedStage : ArticleStage.PendingPublish;
        ArticleGenerationSummary? generation = null;
        ArticlePromptSummary? prompt = null;
        if (ParseGuid(row[16]) is { } taskId)
        {
            var reservation = Enum.TryParse<CreditReservationStatus>(row[24], true, out var reservationStatus)
                ? reservationStatus : CreditReservationStatus.Held;
            generation = new ArticleGenerationSummary(taskId, ParseGuid(row[17]) ?? Guid.Empty, ParseTaskStatus(row[18]),
                int.Parse(row[19], CultureInfo.InvariantCulture), Null(row[20]), Null(row[21]), ParseDate(row[22]),
                ParseDate(row[23]), reservation);
            if (ParseGuid(row[25]) is { } promptId)
                prompt = new ArticlePromptSummary(promptId, row[26], row[27], ParseGuid(row[28]));
        }
        return new ArticleView(Guid.Parse(row[0]), Guid.Parse(row[1]), Guid.Parse(row[2]), Guid.Parse(row[3]), source, status, row[6], row[7], monitor,
            int.Parse(row[9], CultureInfo.InvariantCulture), tags, int.Parse(row[13], CultureInfo.InvariantCulture), new ArticleCitationSummary(null, [], null, "NO_ARTICLE_MONITOR_DATA", []), ParseDate(row[10]), ParseDate(row[11]), string.IsNullOrWhiteSpace(row[12]) ? null : ParseDate(row[12]),
            stage, prompt, generation, ParseBool(row[29]),
            ParseGuid(row[30]) is { } discoveredCheck
                ? new ArticleDiscoverySummary(discoveredCheck, ParseDate(row[31]), row[32], row[33], Null(row[34]))
                : null);
    }

    /// <summary>source_kind 列到枚举的映射；未知值按 AI 生成处理。</summary>
    internal static ArticleSourceKind ParseSourceKind(string value) => value switch
    {
        "published_import" => ArticleSourceKind.PublishedImport,
        "citation_discovered" => ArticleSourceKind.CitationDiscovered,
        _ => ArticleSourceKind.AiGenerated
    };

    /// <summary>枚举到 source_kind 列值的映射。</summary>
    internal static string SourceKindValue(ArticleSourceKind kind) => kind switch
    {
        ArticleSourceKind.PublishedImport => "published_import",
        ArticleSourceKind.CitationDiscovered => "citation_discovered",
        _ => "ai_generated"
    };

    private static ArticleGenerationTaskStatus ParseTaskStatus(string value)
        => Enum.TryParse<ArticleGenerationTaskStatus>(value, true, out var parsed) ? parsed : ArticleGenerationTaskStatus.Failed;

    /// <summary>从 Web 默认序列化的 PromptView 快照读取提问摘要；快照无法解析时返回空。</summary>
    internal static ArticlePromptSummary? ReadPromptSummary(string? promptSnapshot)
    {
        if (string.IsNullOrWhiteSpace(promptSnapshot)) return null;
        try
        {
            var prompt = JsonSerializer.Deserialize<PromptView>(promptSnapshot, JsonOptions);
            return prompt is null || prompt.PromptId == Guid.Empty
                ? null
                : new ArticlePromptSummary(prompt.PromptId, prompt.Content ?? string.Empty, prompt.Locale ?? string.Empty, prompt.GroupId);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>读取提交时冻结的目标平台摘要。</summary>
    internal static IReadOnlyList<ArticleTargetSummary> ReadTargetSummaries(string? targetSnapshot)
    {
        if (string.IsNullOrWhiteSpace(targetSnapshot)) return [];
        try
        {
            return (JsonSerializer.Deserialize<CollectionTargetView[]>(targetSnapshot, JsonOptions) ?? [])
                .Select(target => new ArticleTargetSummary(target.TargetId, target.Provider, target.Engine,
                    target.DisplayName, target.Country, target.Locale))
                .ToArray();
        }
        catch (JsonException) { return []; }
    }

    /// <summary>按 Worker 读取冻结素材的同一方式统计知识命中片段数：未使用知识库或没有快照为 0。</summary>
    internal static int CountFrozenKnowledgeHits(string? contextSnapshot, bool withoutRag)
    {
        if (withoutRag || string.IsNullOrWhiteSpace(contextSnapshot)) return 0;
        try
        {
            var context = JsonSerializer.Deserialize<KnowledgeContextResponse>(contextSnapshot, KnowledgeContextJsonOptions);
            return context?.Items?.Count ?? 0;
        }
        catch (JsonException) { return 0; }
        catch (NotSupportedException) { return 0; }
    }

    private static readonly JsonSerializerOptions KnowledgeContextJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>按品牌主域与附加域名（规范化后）计算发布链接是否为官网；品牌缺少可用域名或链接主机无效时返回空。</summary>
    /// <param name="normalizedUrl">已经过发布链接规范化（去除一个前导 www.）的 URL。</param>
    /// <param name="brand">当前品牌配置。</param>
    internal static bool? ComputeOwnership(string normalizedUrl, BrandView? brand)
    {
        if (brand is null) return null;
        var brandHosts = new[] { brand.PrimaryDomain }.Concat(brand.AdditionalDomains ?? [])
            .Select(domain => CitationDomainRules.CanonicalHost(domain))
            .Where(host => host is not null)
            .Select(host => host!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (brandHosts.Length == 0) return null;
        var host = CitationDomainRules.HostFromUrl(normalizedUrl, alreadyNormalized: true);
        if (host is null) return null;
        return brandHosts.Any(brandHost => host == brandHost
            || brand.IncludeSubdomains && host.EndsWith("." + brandHost, StringComparison.Ordinal));
    }

    /// <summary>站点时区近 <paramref name="days"/> 个自然日（含今天）的引用统计窗口；详情固定 30 天，列表随 citationDays。</summary>
    private static async Task<CitationWindow> LoadCitationWindowAsync(MySqlConnection connection, Guid tenantId,
        Guid siteId, int days, CancellationToken cancellationToken)
    {
        var row = await RowAsync(connection, null, "SELECT b.time_zone FROM sites s JOIN site_brand_settings b ON b.tenant_id=s.tenant_id AND b.site_id=s.id WHERE s.tenant_id=UUID_TO_BIN(@tenant) AND s.id=UUID_TO_BIN(@site) AND s.status='active';", cancellationToken,
            ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()));
        var zone = TimeZoneInfo.Utc;
        if (row is not null)
        {
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(row[0]); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }

        var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);
        var fromLocal = localToday.AddDays(-(days - 1)).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var toLocal = localToday.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new CitationWindow(
            new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(fromLocal, zone), TimeSpan.Zero),
            new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(toLocal, zone), TimeSpan.Zero), zone);
    }

    /// <summary>按文章批量读取统计窗口内的引用摘要；<paramref name="engine"/> 非空时只统计该平台的观测。没有观测的文章不在结果中。</summary>
    private static async Task<IReadOnlyDictionary<Guid, ArticleCitationSummary>> LoadArticleCitationSummariesAsync(
        MySqlConnection connection, Guid tenantId, Guid siteId, IReadOnlyList<Guid> articleIds,
        CitationWindow window, string? engine, CancellationToken cancellationToken)
    {
        if (articleIds.Count == 0) return new Dictionary<Guid, ArticleCitationSummary>();

        var placeholders = string.Join(',', articleIds.Select((_, index) => $"UUID_TO_BIN(@article{index})"));
        var parameters = new List<(string Name, object? Value)>
        {
            ("@tenant", tenantId.ToString()),
            ("@site", siteId.ToString())
        };
        parameters.AddRange(articleIds.Select((id, index) => ($"@article{index}", (object?)id.ToString())));
        var rows = await RowsAsync(connection, null, $"""
            SELECT BIN_TO_UUID(p.article_id),BIN_TO_UUID(vc.id),ce.url_hash,vc.captured_at,vc.engine
            FROM content_publication_links p
            JOIN content_references r ON r.tenant_id=p.tenant_id AND r.id=p.reference_id
            JOIN citation_events ce ON ce.url_hash=r.url_hash
            JOIN visibility_checks vc ON vc.tenant_id=p.tenant_id AND vc.site_id=p.site_id AND vc.id=ce.check_id
                AND vc.captured_at>=@citation_from AND vc.captured_at<@citation_to
            JOIN analysis_releases ar ON ar.tenant_id=p.tenant_id AND ar.site_id=p.site_id
                AND ar.id=ce.release_id AND ar.status='published'
            JOIN content_articles a ON a.tenant_id=p.tenant_id AND a.site_id=p.site_id AND a.id=p.article_id
            LEFT JOIN content_monitor_scopes ms ON ms.tenant_id=a.tenant_id AND ms.site_id=a.site_id AND ms.article_id=a.id
            WHERE p.tenant_id=UUID_TO_BIN(@tenant) AND p.site_id=UUID_TO_BIN(@site)
              AND p.article_id IN ({placeholders})
              AND vc.captured_at IS NOT NULL
              AND COALESCE(p.published_at,p.created_at)<=vc.captured_at
              AND (p.status='active' OR p.disabled_at IS NULL OR vc.captured_at<p.disabled_at)
              {(engine is null ? "" : "AND vc.engine=@citation_engine")}
              AND {CitationMonitorScopeSql};
            """, cancellationToken,
            [.. parameters, ("@citation_from", (object?)window.FromUtc.UtcDateTime), ("@citation_to", (object?)window.ToUtc.UtcDateTime),
                ("@citation_engine", engine)]);

        var observations = rows
            .Select(row => new
            {
                ArticleId = Guid.Parse(row[0]),
                CheckId = Guid.Parse(row[1]),
                UrlHash = row[2],
                CapturedAt = ParseDate(row[3]),
                Engine = row[4]
            })
            .GroupBy(item => (item.ArticleId, item.CheckId, item.UrlHash))
            .Select(group => group.OrderByDescending(item => item.CapturedAt).First())
            .ToArray();

        return observations
            .GroupBy(item => item.ArticleId)
            .ToDictionary(group => group.Key, group =>
            {
                var trend = group.GroupBy(item => DateOnly.FromDateTime(
                        TimeZoneInfo.ConvertTime(item.CapturedAt, window.Zone).DateTime))
                    .Select(day => new ArticleTrendPoint(day.Key, day.LongCount()))
                    .OrderBy(point => point.Date)
                    .ToArray();
                // 一个回答只属于一个平台，按平台去重回答数即该平台的引用次数。
                var engines = group.GroupBy(item => item.Engine, StringComparer.Ordinal)
                    .Select(byEngine => (Engine: byEngine.Key, Count: byEngine.Select(item => item.CheckId).Distinct().LongCount()))
                    .OrderByDescending(item => item.Count).ThenBy(item => item.Engine, StringComparer.Ordinal)
                    .Select(item => item.Engine)
                    .ToArray();
                return new ArticleCitationSummary(group.Select(item => item.CheckId).Distinct().LongCount(),
                    trend, group.Max(item => item.CapturedAt), "READY", engines, group.Min(item => item.CapturedAt));
            });
    }

    /// <summary>
    /// 按文章批量读取主发布链接：有效（status='active'）链接中官网域名优先，其次 COALESCE(published_at, created_at) 最早，再按链接标识；
    /// 没有有效链接的文章不在结果中。
    /// </summary>
    private static async Task<IReadOnlyDictionary<Guid, ArticlePrimaryLinkView>> LoadPrimaryLinksAsync(
        MySqlConnection connection, Guid tenantId, Guid siteId, IReadOnlyList<Guid> articleIds,
        CancellationToken cancellationToken)
    {
        if (articleIds.Count == 0) return new Dictionary<Guid, ArticlePrimaryLinkView>();
        var placeholders = string.Join(',', articleIds.Select((_, index) => $"UUID_TO_BIN(@article{index})"));
        var parameters = new List<(string Name, object? Value)>
        {
            ("@tenant", tenantId.ToString()),
            ("@site", siteId.ToString())
        };
        parameters.AddRange(articleIds.Select((id, index) => ($"@article{index}", (object?)id.ToString())));
        var rows = await RowsAsync(connection, null, $"""
            SELECT ranked.article_id, ranked.link_id, ranked.url_original, ranked.published_at, ranked.ownership_match
            FROM (
                SELECT BIN_TO_UUID(p.article_id) AS article_id, BIN_TO_UUID(p.id) AS link_id, r.url_original,
                       DATE_FORMAT(p.published_at,'%Y-%m-%d %H:%i:%s.%f') AS published_at, p.ownership_match,
                       ROW_NUMBER() OVER (PARTITION BY p.article_id
                           ORDER BY COALESCE(p.ownership_match,0) DESC, COALESCE(p.published_at,p.created_at), p.id) AS link_rank
                FROM content_publication_links p
                JOIN content_references r ON r.tenant_id=p.tenant_id AND r.id=p.reference_id
                WHERE p.tenant_id=UUID_TO_BIN(@tenant) AND p.site_id=UUID_TO_BIN(@site)
                  AND p.article_id IN ({placeholders}) AND p.status='active'
            ) AS ranked
            WHERE ranked.link_rank=1;
            """, cancellationToken, [.. parameters]);
        return rows.ToDictionary(row => Guid.Parse(row[0]), row => new ArticlePrimaryLinkView(Guid.Parse(row[1]), row[2],
            string.IsNullOrWhiteSpace(row[3]) ? null : ParseDate(row[3]),
            string.IsNullOrWhiteSpace(row[4]) ? null : ParseBool(row[4])));
    }

    private static (JsonObject Snapshot, List<MonitorWindow> Windows) ReadMonitorScopeSnapshot(string? raw)
    {
        JsonObject snapshot;
        try { snapshot = JsonNode.Parse(raw ?? "{}") as JsonObject ?? []; }
        catch (JsonException) { snapshot = []; }

        var windows = new List<MonitorWindow>();
        if (snapshot["activeWindows"] is JsonArray array)
        {
            foreach (var item in array.OfType<JsonObject>())
            {
                if (!DateTimeOffset.TryParse(JsonString(item["from"]), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var from))
                    continue;
                DateTimeOffset? to = null;
                if (DateTimeOffset.TryParse(JsonString(item["to"]), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedTo))
                    to = parsedTo;
                if (to is null || to > from) windows.Add(new MonitorWindow(from, to));
            }
        }
        return (snapshot, windows);
    }

    private static string? JsonString(JsonNode? node)
    {
        try { return node?.GetValue<string>(); }
        catch (InvalidOperationException) { return null; }
    }

    private static string SerializeMonitorScopeSnapshot(JsonObject snapshot, IReadOnlyList<MonitorWindow> windows)
    {
        var array = new JsonArray();
        foreach (var window in windows)
        {
            var item = new JsonObject
            {
                ["from"] = window.From.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture)
            };
            if (window.To is not null)
                item["to"] = window.To.Value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
            else
                item["to"] = null;
            array.Add(item);
        }
        snapshot["activeWindows"] = array;
        return snapshot.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    internal static (string Snapshot, DateTimeOffset? EnabledAt, DateTimeOffset? DisabledAt) TransitionMonitorScope(
        string? raw, ArticleMonitorStatus target, DateTimeOffset now)
    {
        var (snapshot, windows) = ReadMonitorScopeSnapshot(raw);
        var openIndex = windows.FindLastIndex(window => window.To is null);
        if (target == ArticleMonitorStatus.Active)
        {
            if (openIndex < 0) windows.Add(new MonitorWindow(now, null));
            else if (openIndex != windows.Count - 1)
            {
                var open = windows[openIndex];
                windows.RemoveAt(openIndex);
                windows.Add(open);
            }
        }
        else if (openIndex >= 0)
        {
            var open = windows[openIndex];
            windows[openIndex] = open with { To = now };
        }

        var latest = windows.LastOrDefault();
        return (SerializeMonitorScopeSnapshot(snapshot, windows),
            target == ArticleMonitorStatus.Active ? latest?.From : latest?.From,
            target == ArticleMonitorStatus.Active ? null : now);
    }

    private static async Task<decimal> ReleaseReservationInTransactionAsync(
        MySqlConnection connection, MySqlTransaction transaction, Guid tenantId, Guid reservationId,
        string reason, CancellationToken cancellationToken)
    {
        var reservation = await RowAsync(connection, transaction,
            "SELECT amount,status FROM credit_reservations WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@reservation) FOR UPDATE;",
            cancellationToken, ("@tenant", tenantId.ToString()), ("@reservation", reservationId.ToString()));
        if (reservation is null || !reservation[1].Equals("held", StringComparison.OrdinalIgnoreCase)) return 0;

        var amount = decimal.Parse(reservation[0], CultureInfo.InvariantCulture);
        var account = await RowAsync(connection, transaction,
            "SELECT reserved_credits FROM credit_accounts WHERE tenant_id=UUID_TO_BIN(@tenant) FOR UPDATE;",
            cancellationToken, ("@tenant", tenantId.ToString()));
        if (account is null || decimal.Parse(account[0], CultureInfo.InvariantCulture) < amount)
            throw new CollectorStoreException("CREDIT_RECONCILIATION_REQUIRED", "积分账户与预占流水不一致，请稍后重试。", 503);

        var safeReason = string.IsNullOrWhiteSpace(reason) ? string.Empty : reason;
        if (safeReason.Length > 500) safeReason = safeReason[..500];
        await ExecAsync(connection, transaction, """
            UPDATE credit_accounts SET available_credits=available_credits+@amount,
                reserved_credits=reserved_credits-@amount,version=version+1,updated_at=UTC_TIMESTAMP(6)
            WHERE tenant_id=UUID_TO_BIN(@tenant);
            UPDATE credit_reservations SET status='released',released_at=UTC_TIMESTAMP(6),reason=@reason
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@reservation) AND status='held';
            INSERT IGNORE INTO credit_ledger
                (id,tenant_id,entry_type,amount,balance_after,reservation_id,external_reference,reason,created_at)
            SELECT UUID_TO_BIN(UUID()),tenant_id,'release',@amount,available_credits,
                UUID_TO_BIN(@reservation),CONCAT('release:',@reservation),@reason,UTC_TIMESTAMP(6)
            FROM credit_accounts WHERE tenant_id=UUID_TO_BIN(@tenant);
            """, cancellationToken, ("@amount", amount), ("@tenant", tenantId.ToString()),
            ("@reservation", reservationId.ToString()), ("@reason", safeReason));
        return amount;
    }

    private static ArticleVersionView ToVersion(string[] row)
    {
        var kind = row[3] switch { "manual_edit" => ArticleVersionKind.ManualEdit, "imported" => ArticleVersionKind.Imported, "refined" => ArticleVersionKind.Refined, _ => ArticleVersionKind.Generated };
        // Column 24 (edit_revision) is selected as a literal 0 until migration 041 is applied.
        var revision = row.Length > 24 && int.TryParse(row[24], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedRevision) ? parsedRevision : 0;
        return new ArticleVersionView(Guid.Parse(row[0]), Guid.Parse(row[1]), int.Parse(row[2], CultureInfo.InvariantCulture), kind, row[4], Null(row[5]), row[6], Null(row[7]), Null(row[8]), DeserializeStrings(row[9]), int.Parse(row[10], CultureInfo.InvariantCulture), row[11], ParseGuid(row[12]), ParseGuid(row[13]), Null(row[14]), Null(row[15]), Null(row[16]), Null(row[17]), Guid.Parse(row[18]), ParseDate(row[19]), ParseBool(row[20]), DeserializeStrings(row[21]), DeserializeStrings(row[22]), Null(row[23]), revision);
    }

    private static ArticlePublicationLinkView ToLink(string[] row)
    {
        var status = row[8].Equals("disabled", StringComparison.OrdinalIgnoreCase) ? ArticlePublicationStatus.Disabled : ArticlePublicationStatus.Active;
        bool? ownership = string.IsNullOrWhiteSpace(row[9]) ? null : ParseBool(row[9]);
        return new ArticlePublicationLinkView(Guid.Parse(row[0]), Guid.Parse(row[1]), ParseGuid(row[2]), row[3], row[4], row[5], Null(row[6]) ?? string.Empty, string.IsNullOrWhiteSpace(row[7]) ? null : ParseDate(row[7]), status, ownership, row[10], ParseDate(row[11]), string.IsNullOrWhiteSpace(row[12]) ? null : ParseDate(row[12]),
            row.Length > 14 ? ParseGuid(row[13]) : null, row.Length > 14 && !string.IsNullOrWhiteSpace(row[14]) ? ParseDate(row[14]) : null);
    }

    private static ArticleGenerationTaskView ToTask(string[] row)
    {
        var status = Enum.TryParse<ArticleGenerationTaskStatus>(row[3], true, out var parsed) ? parsed : ArticleGenerationTaskStatus.Failed;
        var reservation = Enum.TryParse<CreditReservationStatus>(row[8], true, out var reservationStatus) ? reservationStatus : CreditReservationStatus.Held;
        return new ArticleGenerationTaskView(Guid.Parse(row[0]), Guid.Parse(row[1]), ParseGuid(row[2]), status, int.Parse(row[4], CultureInfo.InvariantCulture), ParseGuid(row[5]), Null(row[6]), Null(row[7]), reservation);
    }

    private static ArticleGenerationBatchStatus ParseBatchStatus(string value) => Enum.TryParse<ArticleGenerationBatchStatus>(value, true, out var result) ? result : ArticleGenerationBatchStatus.Failed;

    private async Task<Guid?> FindArticleByUrlAsync(Guid tenantId, Guid siteId, string urlHash, CancellationToken cancellationToken)
    {
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        var row = await RowAsync(connection, null, "SELECT BIN_TO_UUID(article_id) FROM content_publication_links p JOIN content_references r ON r.tenant_id=p.tenant_id AND r.id=p.reference_id WHERE p.tenant_id=UUID_TO_BIN(@tenant) AND p.site_id=UUID_TO_BIN(@site) AND r.url_hash=@hash LIMIT 1;", cancellationToken,
            ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@hash", urlHash));
        return row is null ? null : Guid.Parse(row[0]);
    }

    private async Task AssertSiteAsync(MySqlConnection connection, Guid tenantId, Guid siteId, CancellationToken cancellationToken)
    {
        var row = await RowAsync(connection, null, "SELECT 1 FROM sites WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@site) AND status='active';", cancellationToken,
            ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()));
        if (row is null) throw new CollectorStoreException("RESOURCE_NOT_FOUND", "工作区不存在。", 404);
    }

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
            for (var i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? string.Empty : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? string.Empty;
            rows.Add(row);
        }
        return rows;
    }

    private static async Task<string[]?> RowAsync(MySqlConnection connection, MySqlTransaction? transaction, string sql,
        CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
        => (await RowsAsync(connection, transaction, sql, cancellationToken, parameters)).FirstOrDefault();
}
