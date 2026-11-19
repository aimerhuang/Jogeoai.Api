using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

public sealed partial class AnalyticsQueryStore
{
    private const string CitationProjectionKind = "citation-domain-summary";
    private sealed record CitationPrepared(Prepared Base, CitationDomainMeta Meta, CitationDomainProjection Projection);
    private static readonly JsonSerializerOptions CitationEnumJson = new()
    { Converters = { new JsonStringEnumConverter() } };

    /// <summary>
    /// 引用域名汇总一族写死的来源集合，按渠道二选一（results.md 8.2）。调用方不能自行指定 Provider 或采集模式；
    /// 汇总按它创建快照，其余入口按它校验快照里冻结的筛选。
    /// </summary>
    internal sealed record CitationChannelSources(CollectionChannel Channel, IReadOnlyList<string> Providers,
        IReadOnlyList<string> CaptureModes)
    {
        // API 口径：与增加 channel 参数之前写死的集合相同（八个默认 Provider、api 与 scraped）。
        private static readonly CitationChannelSources Api = new(CollectionChannel.Api, DefaultProviders, ["api", "scraped"]);
        // App 口径：只有 App 渠道的回答。
        private static readonly CitationChannelSources App = new(CollectionChannel.App, [AppChannelDefinition.Provider],
            [AppChannelDefinition.CaptureMode]);

        /// <summary>解析 channel 参数（忽略首尾空白与大小写）：省略或空白为 api；只接受 api 与 app。</summary>
        internal static CitationChannelSources Parse(string? channel)
        {
            if (string.IsNullOrWhiteSpace(channel)) return Api;
            return AppContractNames.TryParse<CollectionChannel>(channel, out var parsed) && parsed is CollectionChannel.Api or CollectionChannel.App
                ? parsed == CollectionChannel.App ? App : Api
                : throw Error("INVALID_FILTER", "channel 仅支持 api 或 app。", 400);
        }
    }

    /// <inheritdoc />
    public Task<CitationDomainSummaryView> CitationDomainSummaryAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, string? snapshotToken, CancellationToken cancellationToken = default)
        => CitationDomainSummaryAsync(tenantId, siteId, request, snapshotToken, null, cancellationToken);

    /// <inheritdoc />
    public async Task<CitationDomainSummaryView> CitationDomainSummaryAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, string? snapshotToken, string? channel, CancellationToken cancellationToken = default)
    {
        var sources = CitationChannelSources.Parse(channel);
        // Explicitly independent from the legacy cloro-only analytics defaults.
        var data = await PrepareCitationDomainsAsync(tenantId, siteId, request with
        {
            Providers = sources.Providers,
            CaptureModes = sources.CaptureModes,
            EvidenceClasses = ["api_response", "observed_surface", "unknown"],
            AggregationMode = "observed"
        }, snapshotToken, sources, cancellationToken);
        return new(data.Meta, data.Projection.Metrics, data.Projection.Categories, data.Projection.Quality);
    }

    /// <inheritdoc />
    public Task<CitationDomainSummaryPage> CitationDomainSummaryDomainsAsync(Guid tenantId, Guid siteId,
        string? snapshotToken, string? q, string? websiteCategory, string? ownership,
        string? cursor, int limit, CancellationToken cancellationToken = default)
        => CitationDomainSummaryDomainsAsync(tenantId, siteId, snapshotToken, q, websiteCategory, ownership, cursor, limit, null,
            cancellationToken);

    /// <inheritdoc />
    public async Task<CitationDomainSummaryPage> CitationDomainSummaryDomainsAsync(Guid tenantId, Guid siteId,
        string? snapshotToken, string? q, string? websiteCategory, string? ownership,
        string? cursor, int limit, string? channel, CancellationToken cancellationToken = default)
    {
        RequireMonitoringSnapshot(snapshotToken);
        ValidateLimit(limit);
        var filter = NormalizeCitationDomainFilter(q, websiteCategory, ownership);
        var data = await PrepareCitationDomainsAsync(tenantId, siteId, new(), snapshotToken,
            CitationChannelSources.Parse(channel), cancellationToken);
        var scope = CitationScope(data.Meta, JsonSerializer.Serialize(filter));
        var offset = DecodeCursor(cursor, data.Base.Snapshot, "citation-summary-domains", scope);
        var rows = data.Projection.Domains.Where(r =>
            (filter.Search is null || r.Domain.Contains(filter.Search, StringComparison.OrdinalIgnoreCase))
            && (filter.Category is null || r.WebsiteCategory == filter.Category)
            && (filter.Ownership is null || r.Ownership == filter.Ownership)).ToArray();
        var page = Page(data.Base.Meta, rows, offset, limit, data.Base.Snapshot, "citation-summary-domains", scope);
        var previousRanks = await CachedCitationPreviousRanksAsync(data, cancellationToken);
        return new(data.Meta, page.Items.Select(row => WithPreviousRank(row, previousRanks)).ToArray(),
            page.NextCursor, rows.LongLength);
    }

    /// <inheritdoc />
    public Task<CitationUrlSummaryPage> CitationDomainSummaryUrlsAsync(Guid tenantId, Guid siteId,
        string? snapshotToken, string? domain, string? cursor, int limit, CancellationToken cancellationToken = default)
        => CitationDomainSummaryUrlsAsync(tenantId, siteId, snapshotToken, domain, null, null, null, cursor, limit,
            cancellationToken);

    /// <inheritdoc />
    public Task<CitationDomainEvidencePage> CitationDomainSummaryEvidenceAsync(Guid tenantId, Guid siteId,
        string? snapshotToken, string domain, string? cursor, int limit, CancellationToken cancellationToken = default)
        => CitationDomainSummaryEvidenceAsync(tenantId, siteId, snapshotToken, domain, cursor, limit, null, cancellationToken);

    /// <inheritdoc />
    public async Task<CitationDomainEvidencePage> CitationDomainSummaryEvidenceAsync(Guid tenantId, Guid siteId,
        string? snapshotToken, string domain, string? cursor, int limit, string? channel,
        CancellationToken cancellationToken = default)
    {
        RequireMonitoringSnapshot(snapshotToken);
        ValidateLimit(limit);
        // Input is the canonical key emitted by the row, not a fresh raw host.
        var host = CitationDomainRules.CanonicalHost(domain, alreadyCanonical: true);
        if (host is null) throw Error("INVALID_FILTER", "domain 必须是有效的规范化主机域名。", 400);
        var data = await PrepareCitationDomainsAsync(tenantId, siteId, new(), snapshotToken,
            CitationChannelSources.Parse(channel), cancellationToken);
        var scope = CitationScope(data.Meta, host);
        var offset = DecodeCursor(cursor, data.Base.Snapshot, "citation-summary-evidence", scope);
        var totals = data.Projection.Domains.FirstOrDefault(d => d.Domain == host);
        var page = new List<CitationDomainEvidenceItem>(limit + 1);
        var eligible = data.Base.Facts.Where(f => CitationDomainRules.IsEligible(f, data.Base.Configuration))
            .ToDictionary(f => f.CheckId);
        var skipped = 0;
        // Published event keys are already unique by (check, normalized URL).
        // Evidence reader has its own stable URL/check ordering; it never feeds
        // the grouped-by-check accumulator used for the summary.
        await foreach (var item in ReadCitationDomainEventsAsync(data.Base.Snapshot, true, cancellationToken))
        {
            if (!eligible.TryGetValue(item.CheckId, out var fact)
                || CitationDomainRules.HostFromUrl(item.Url, alreadyNormalized: true) != host
                || CitationDomainRules.IsPlaceholder(host)
                || DetailedAnalyticsRules.GoogleInternal(item.Url, null, item.FirstPosition, data.Base.Configuration) is not null) continue;
            if (skipped++ < offset) continue;
            var prompt = fact.PromptSnapshot ?? "";
            page.Add(new(item.CheckId, fact.PromptId, prompt.Length > 200 ? prompt[..200] : prompt,
                fact.Engine, fact.CapturedAt, item.Url, item.FirstPosition));
            if (page.Count > limit) break;
        }
        var next = page.Count > limit ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
            new PageCursor(Hash(data.Base.Snapshot.Token), "citation-summary-evidence", scope, offset + limit))) : null;
        if (totals is not null) totals = WithPreviousRank(totals, await CachedCitationPreviousRanksAsync(data, cancellationToken));
        return new(data.Meta, page.Take(limit).ToArray(), next, totals);
    }

    internal sealed record CitationTableFilter(string? Search, CitationWebsiteCategory? Category, CitationOwnership? Ownership);

    internal static CitationTableFilter NormalizeCitationDomainFilter(string? q, string? category, string? ownership)
    {
        var search = string.IsNullOrWhiteSpace(q) ? null : q.Trim().ToUpperInvariant();
        if (search?.Length > 200) throw Error("INVALID_FILTER", "域名搜索最多二百 UTF-16 字符。", 400);
        return new(search, ParseCitationEnum<CitationWebsiteCategory>(category), ParseCitationEnum<CitationOwnership>(ownership));
    }

    private static T? ParseCitationEnum<T>(string? value) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().ToLowerInvariant();
        foreach (var member in Enum.GetValues<T>())
            if (JsonSerializer.Serialize(member, CitationEnumJson).Trim('"') == normalized) return member;
        throw Error("INVALID_FILTER", "不支持的网站分类或归属筛选。", 400);
    }

    private static string CitationScope(CitationDomainMeta meta, string value)
        => Hash(JsonSerializer.Serialize(new
        {
            meta.ReleaseId,
            meta.ArrivalWatermark,
            meta.AnalysisWatermark,
            meta.ProjectionVersion,
            meta.WebsiteCategoryRulesVersion,
            meta.OwnershipRulesVersion,
            meta.PlaceholderRulesVersion,
            meta.UrlNormalizationVersion,
            Value = value
        }));

    /// <param name="sources">所选渠道写死的来源集合；快照里冻结的 Provider 与采集模式必须与它一致。</param>
    /// <param name="onCounted">可选：同一次流式投影中每个计入有效外部事件的回调，与域名榜共用同一判定。</param>
    private async Task<CitationPrepared> PrepareCitationDomainsAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, string? token, CitationChannelSources sources, CancellationToken cancellationToken,
        Action<CitationDomainCountedEvent>? onCounted = null)
    {
        var data = await PrepareDetailedAsync(tenantId, siteId, request, token, cancellationToken);
        var filter = data.Snapshot.Filter;
        if (!filter.Providers.Order().SequenceEqual(sources.Providers.Order())
            || !filter.CaptureModes.Order().SequenceEqual(sources.CaptureModes.Order())
            || !filter.EvidenceClasses.Order().SequenceEqual(new[] { "api_response", "observed_surface", "unknown" })
            || filter.PromptIds.Count > 0 || filter.PromptVersionIds.Count > 0 || filter.Tags.Count > 0
            || filter.Countries.Count > 0 || filter.Locales.Count > 0 || filter.SubjectIds.Count > 0
            || filter.Statuses.Count > 0 || filter.ModelVersions.Count > 0 || filter.ModelVersionUnavailable is not null
            || filter.BrandedStatus is not null)
            throw Error("INVALID_FILTER", sources.Channel == CollectionChannel.App
                ? "域名汇总快照只接受 App 渠道下的时间与模型范围；快照的渠道与 channel 参数必须一致。"
                : "域名汇总快照只接受全来源下的时间与模型范围。", 400);
        var meta = await BindCitationProjectionAsync(data.Snapshot, data.Meta.DataUpdatedAt, cancellationToken);
        var (facts, projection, integrityUnproven) = await ProjectCitationDomainsAsync(data.Snapshot, data.Facts,
            data.Configuration, meta.UrlNormalizationVersion, cancellationToken, onCounted);
        data = data with { Facts = facts };
        if (integrityUnproven > 0)
            projection = projection with
            {
                Quality = projection.Quality with
                {
                    Limitations = [.. projection.Quality.Limitations,
                    $"{integrityUnproven} 条历史 detail-v1 回答的引用事件键与已存规范化 URL 不一致，无法证明域名口径；整条回答计入分析未完成，不按零引用统计，原始回答仍可阅读。"]
                }
            };
        return new(data, meta, projection);
    }

    /// <summary>One window's population S projection; current and previous windows share it verbatim.</summary>
    private async Task<(IReadOnlyList<AnalyticsCheckFact> Facts, CitationDomainProjection Projection, long IntegrityUnproven)>
        ProjectCitationDomainsAsync(Snapshot snapshot, IReadOnlyList<AnalyticsCheckFact> coreFacts,
            SubjectConfiguration configuration, string urlVersion, CancellationToken cancellationToken,
            Action<CitationDomainCountedEvent>? onCounted = null)
    {
        var completed = await LoadDetailedCompletionChecksAsync(snapshot, null, cancellationToken);
        var integrityUnproven = coreFacts.LongCount(f => CitationDomainRules.IsEligible(f, configuration)
            && completed.IntegrityUnproven.Contains(f.CheckId));
        var facts = MarkUnprovenDetailedPending(coreFacts, completed);
        var accumulator = new CitationDomainAccumulator(facts, configuration, urlVersion, onCounted);
        // ORDER BY e.check_id below is part of the accumulator's bounded-memory contract.
        await foreach (var item in ReadCitationDomainEventsAsync(snapshot, false, cancellationToken))
            accumulator.Add(item);
        return (facts, accumulator.Complete(), integrityUnproven);
    }

    /// <summary>
    /// Never change cached legacy core facts. The new population independently marks core-complete but
    /// unproven detailed answers as pending (shared by the summary projection and records citedDomain).
    /// </summary>
    private static AnalyticsCheckFact[] MarkUnprovenDetailedPending(IEnumerable<AnalyticsCheckFact> coreFacts,
        DetailedCompletionEvidence completed)
        => coreFacts.Select(f => f.AnalysisStatus == "completed" && !completed.Completed.Contains(f.CheckId)
            ? f with { AnalysisStatus = "pending" } : f).ToArray();

    private static CitationDomainSummaryRow WithPreviousRank(CitationDomainSummaryRow row,
        IReadOnlyDictionary<string, int> previousRanks)
        => row with { PreviousRank = previousRanks.TryGetValue(row.Domain, out var rank) ? rank : null };

    /// <summary>
    /// Previous equal-length window ranks under the same frozen token (sources, engine, release,
    /// watermarks, configuration). Only the domain → rank map is kept; previous metrics, quality and
    /// categories never leak into the current response.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, int>> ComputeCitationPreviousRanksAsync(CitationPrepared data)
    {
        var previousFilter = data.Base.Snapshot.Filter with { From = data.Meta.PreviousFrom, To = data.Meta.PreviousTo };
        var previousFacts = await CachedPreviousFactsAsync(data.Base, previousFilter, CancellationToken.None);
        var (_, projection, _) = await ProjectCitationDomainsAsync(data.Base.Snapshot with { Filter = previousFilter },
            previousFacts, data.Base.Configuration, data.Meta.UrlNormalizationVersion, CancellationToken.None);
        return projection.Domains.ToDictionary(d => d.Domain, d => d.Rank, StringComparer.Ordinal);
    }

    /// <summary>Titles for one page only, from the same tenant/site/window/release/watermark scope.</summary>
    internal static string CitationUrlTitleSql(int hashCount) => $"""
            SELECT e.url_hash, e.normalized_url, e.title, c.captured_at, BIN_TO_UUID(c.id)
            FROM visibility_checks c
            JOIN analytics_check_arrivals ar ON ar.check_id = c.id
            JOIN check_analysis a ON a.check_id = c.id AND a.release_id = UUID_TO_BIN(@release)
            JOIN citation_events e ON e.check_id = c.id AND e.release_id = a.release_id
            WHERE c.tenant_id = UUID_TO_BIN(@tenant) AND c.site_id = UUID_TO_BIN(@site)
              AND c.captured_at >= @from AND c.captured_at < @to
              AND ar.id <= @arrival AND a.completed_event_id <= @analysis
              AND e.url_version = @urlVersion
              AND e.url_hash IN ({string.Join(", ", Enumerable.Range(0, hashCount).Select(i => $"@h{i}"))})
              AND e.title IS NOT NULL AND TRIM(e.title) <> '';
            """;

    private async Task<IReadOnlyDictionary<string, string>> LoadCitationUrlTitlesAsync(Snapshot snapshot,
        string urlVersion, IReadOnlyList<CitationUrlSummaryRow> rows, IReadOnlySet<Guid> eligible,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return new Dictionary<string, string>(StringComparer.Ordinal);
        var urls = rows.ToDictionary(r => r.UrlHash, r => r.Url, StringComparer.Ordinal);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(CitationUrlTitleSql(rows.Count), connection);
        AddDetailFilter(command, snapshot);
        command.Parameters.AddWithValue("@urlVersion", urlVersion);
        for (var i = 0; i < rows.Count; i++) command.Parameters.AddWithValue($"@h{i}", rows[i].UrlHash);
        var candidates = new List<(string Hash, string Title, DateTimeOffset? CapturedAt, Guid CheckId)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var hash = reader.GetString(0);
            var check = Guid.Parse(reader.GetString(4));
            // Only answers in population S, and only the exact published URL behind the hash.
            if (!eligible.Contains(check) || !urls.TryGetValue(hash, out var url) || reader.GetString(1) != url) continue;
            candidates.Add((hash, reader.GetString(2), reader.IsDBNull(3) ? null : Utc(reader.GetDateTime(3)), check));
        }
        // Same latest-first semantics as the legacy citation URL rows (LatestTitle).
        return candidates.GroupBy(c => c.Hash, StringComparer.Ordinal).ToDictionary(g => g.Key,
            g => g.OrderByDescending(c => c.CapturedAt).ThenBy(c => c.CheckId).First().Title, StringComparer.Ordinal);
    }

    private async IAsyncEnumerable<CitationDomainEvent> ReadCitationDomainEventsAsync(Snapshot snapshot,
        bool evidenceOrder, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            SELECT BIN_TO_UUID(e.check_id), e.normalized_url, e.first_position
            FROM visibility_checks c
            JOIN analytics_check_arrivals ar ON ar.check_id = c.id
            JOIN check_analysis a ON a.check_id = c.id AND a.release_id = UUID_TO_BIN(@release)
            JOIN citation_events e ON e.check_id = c.id AND e.release_id = a.release_id
            WHERE c.tenant_id = UUID_TO_BIN(@tenant) AND c.site_id = UUID_TO_BIN(@site)
              AND c.captured_at >= @from AND c.captured_at < @to
              AND ar.id <= @arrival AND a.completed_event_id <= @analysis
              AND c.status = 'succeeded' AND a.configuration_missing = FALSE
              AND COALESCE(c.answer_present, c.answer IS NOT NULL AND TRIM(c.answer) <> '') = TRUE
            """ + (evidenceOrder ? " ORDER BY e.normalized_url, e.check_id;" : " ORDER BY e.check_id, e.normalized_url;"), connection);
        AddDetailFilter(command, snapshot);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            yield return new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetInt32(2));
    }

    private async Task<CitationDomainMeta> BindCitationProjectionAsync(Snapshot snapshot,
        DateTimeOffset? updatedAt, CancellationToken cancellationToken)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        string urlVersion;
        int currentConfig;
        await using (var release = new MySqlCommand("""
            SELECT r.detail_version, r.url_normalization_version, b.subject_config_version
            FROM analysis_releases r JOIN site_brand_settings b ON b.site_id = r.site_id
            WHERE r.id = UUID_TO_BIN(@release) AND r.tenant_id = UUID_TO_BIN(@tenant)
              AND r.site_id = UUID_TO_BIN(@site);
            """, connection))
        {
            AddProjectionIds(release, snapshot);
            await using var reader = await release.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || reader.GetString(0) is not ("detail-v1" or "detail-v2"))
                throw Error("ANALYSIS_VERSION_NOT_READY", "所需详细分析版本尚未发布。", 503);
            urlVersion = reader.GetString(1);
            currentConfig = reader.GetInt32(2);
            if (urlVersion is not (DetailedAnalyticsRules.UrlV1 or DetailedAnalyticsRules.UrlV2))
                throw Error("ANALYSIS_VERSION_NOT_READY", "当前发布的 URL 规范化版本尚不受支持。", 503);
        }
        try
        {
            await using var insert = new MySqlCommand("""
                INSERT IGNORE INTO analytics_snapshot_projections
                    (token_hash, projection_kind, tenant_id, site_id, projection_version,
                     category_rules_version, ownership_rules_version, placeholder_rules_version,
                     url_normalization_version, created_at, expires_at)
                SELECT s.token_hash, @kind, s.tenant_id, s.site_id, @projection, @category, @ownership,
                       @placeholder, @url, s.as_of, s.expires_at
                FROM analytics_snapshots s WHERE s.token_hash = @hash
                  AND s.tenant_id = UUID_TO_BIN(@tenant) AND s.site_id = UUID_TO_BIN(@site)
                  AND s.expires_at > UTC_TIMESTAMP(6);
                """, connection);
            AddProjectionIds(insert, snapshot);
            insert.Parameters.AddWithValue("@hash", Hash(snapshot.Token));
            insert.Parameters.AddWithValue("@kind", CitationProjectionKind);
            insert.Parameters.AddWithValue("@projection", CitationDomainRules.ProjectionVersion);
            insert.Parameters.AddWithValue("@category", CitationDomainRules.CategoryVersion);
            insert.Parameters.AddWithValue("@ownership", CitationDomainRules.OwnershipVersion);
            insert.Parameters.AddWithValue("@placeholder", CitationDomainRules.PlaceholderVersion);
            insert.Parameters.AddWithValue("@url", urlVersion);
            await insert.ExecuteNonQueryAsync(cancellationToken);
            await using var stored = new MySqlCommand("""
                SELECT projection_version, category_rules_version, ownership_rules_version,
                       placeholder_rules_version, url_normalization_version
                FROM analytics_snapshot_projections WHERE token_hash = @hash AND projection_kind = @kind
                  AND tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site)
                  AND expires_at > UTC_TIMESTAMP(6);
                """, connection);
            AddProjectionIds(stored, snapshot);
            stored.Parameters.AddWithValue("@hash", Hash(snapshot.Token));
            stored.Parameters.AddWithValue("@kind", CitationProjectionKind);
            await using var reader = await stored.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw Error("SNAPSHOT_EXPIRED", "快照已过期，请整体刷新。", 409);
            if (reader.GetString(0) != CitationDomainRules.ProjectionVersion
                || reader.GetString(1) != CitationDomainRules.CategoryVersion
                || reader.GetString(2) != CitationDomainRules.OwnershipVersion
                || reader.GetString(3) != CitationDomainRules.PlaceholderVersion || reader.GetString(4) != urlVersion)
                throw Error("SNAPSHOT_REFRESH_REQUIRED", "当前部署不支持此快照绑定的引用规则，请整体刷新。", 409);
            return new(snapshot.Token, snapshot.Filter, snapshot.AsOf, snapshot.ExpiresAt, updatedAt,
                snapshot.ReleaseId, snapshot.AnalysisVersion, snapshot.SubjectConfigVersion,
                currentConfig != snapshot.SubjectConfigVersion, reader.GetString(0), reader.GetString(1),
                reader.GetString(2), reader.GetString(3), reader.GetString(4), snapshot.ArrivalWatermark,
                snapshot.AnalysisWatermark, "所选采集样本内实际观测，实际来源范围见筛选元数据；不代表跨来源均衡曝光。",
                snapshot.Filter.From - (snapshot.Filter.To - snapshot.Filter.From), snapshot.Filter.From);
        }
        catch (MySqlException exception) when (exception.Number == 1146)
        { throw Error("CITATION_PROJECTION_SCHEMA_NOT_MIGRATED", "引用投影能力尚未部署，请联系管理员。", 503); }
    }

    private static void AddProjectionIds(MySqlCommand command, Snapshot snapshot)
    {
        command.Parameters.AddWithValue("@tenant", snapshot.TenantId.ToString());
        command.Parameters.AddWithValue("@site", snapshot.SiteId.ToString());
        command.Parameters.AddWithValue("@release", snapshot.ReleaseId.ToString());
    }

    private sealed record DetailedCompletionEvidence(HashSet<Guid> Completed, HashSet<Guid> IntegrityUnproven);

    private async Task<DetailedCompletionEvidence> LoadDetailedCompletionChecksAsync(Snapshot snapshot, Guid? checkId,
        CancellationToken cancellationToken)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            SELECT BIN_TO_UUID(a.check_id),
                (r.detail_version <> 'detail-v1' OR NOT EXISTS (
                    SELECT 1 FROM citation_events e
                    WHERE e.release_id = a.release_id AND e.check_id = a.check_id
                      AND e.url_hash <> LOWER(SHA2(e.normalized_url, 256)))) AS integrity_proven
            FROM check_analysis a
            JOIN analysis_releases r ON r.id = a.release_id AND r.site_id = a.site_id AND r.tenant_id = a.tenant_id
            JOIN visibility_checks c ON c.id = a.check_id AND c.site_id = a.site_id AND c.tenant_id = a.tenant_id
            JOIN analytics_check_arrivals ar ON ar.check_id = c.id
            LEFT JOIN check_detailed_completions p ON p.release_id = a.release_id AND p.check_id = a.check_id
                AND p.tenant_id = a.tenant_id AND p.site_id = a.site_id
                AND p.completed_event_id = a.completed_event_id
            WHERE a.release_id = UUID_TO_BIN(@release) AND a.tenant_id = UUID_TO_BIN(@tenant)
                AND a.site_id = UUID_TO_BIN(@site) AND a.status = 'completed'
                AND a.completed_event_id <= @analysis AND a.completed_at <= @asOf AND ar.id <= @arrival
                AND (r.detail_version = 'detail-v1' OR
                    (r.detail_version = 'detail-v2' AND p.completed_at <= @asOf
                     AND p.completion_version = 'detailed-completion-v1'
                     AND p.detail_version = r.detail_version AND p.url_normalization_version = r.url_normalization_version))
            """ + (checkId is null ? " AND c.captured_at >= @from AND c.captured_at < @to;"
                : " AND a.check_id = UUID_TO_BIN(@check);"), connection);
        // Legacy detail-v1 always wrote all detailed facts in its core completion
        // transaction, including the zero-event case. Only v2 needs the new proof:
        // historical v2 core completion could previously skip that writer entirely.
        AddDetailFilter(command, snapshot);
        command.Parameters.AddWithValue("@asOf", snapshot.AsOf.UtcDateTime);
        if (checkId is not null) command.Parameters.AddWithValue("@check", checkId.Value.ToString());
        try
        {
            var completed = new HashSet<Guid>();
            var integrityUnproven = new HashSet<Guid>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = Guid.Parse(reader.GetString(0));
                if (reader.GetBoolean(1)) completed.Add(id);
                else integrityUnproven.Add(id);
            }
            return new(completed, integrityUnproven);
        }
        catch (MySqlException exception) when (exception.Number == 1146)
        { throw Error("CITATION_PROJECTION_SCHEMA_NOT_MIGRATED", "详细引用完成证明尚未部署，请联系管理员。", 503); }
    }

    /// <inheritdoc />
    public async Task<MonitoringAnswerDetailView?> MonitoringAnswerDetailAsync(Guid tenantId, Guid siteId,
        Guid checkId, string? snapshotToken, string? search, string? mention, string citationProjection,
        CancellationToken cancellationToken = default)
    {
        if (citationProjection != CitationDomainRules.ProjectionVersion)
            throw Error("INVALID_FILTER", "不支持的引用归属投影。", 400);
        var detail = await MonitoringAnswerDetailAsync(tenantId, siteId, checkId, snapshotToken, search, mention, cancellationToken);
        if (detail is null) return null;
        var snapshot = await ResolveSnapshotAsync(tenantId, siteId, new(), snapshotToken, cancellationToken);
        var configuration = SubjectConfiguration.Parse(siteId, snapshot.SubjectConfigVersion, snapshot.SubjectSnapshot);
        try
        {
            var meta = await BindCitationProjectionAsync(snapshot, null, cancellationToken);
            var complete = await LoadDetailedCompletionChecksAsync(snapshot, checkId, cancellationToken);
            var available = detail.Status == "succeeded" && detail.AnswerPresent
                && detail.Analysis is { Status: "completed", ConfigurationMissing: false } && configuration.IsComplete
                && complete.Completed.Contains(checkId);
            var reason = !configuration.IsComplete || detail.Analysis?.ConfigurationMissing == true
                ? "CONFIGURATION_UNAVAILABLE" : complete.IntegrityUnproven.Contains(checkId)
                ? "DETAILED_FACT_INTEGRITY_UNPROVEN" : !complete.Completed.Contains(checkId)
                ? "DETAILED_ANALYSIS_NOT_READY" : "ANALYSIS_NOT_READY";
            return detail with
            {
                CitationAttribution = new(meta, available ? "available" : "unavailable",
                CitationDomainRules.Attribute(detail.Citations, configuration, available),
                available ? null : reason)
            };
        }
        catch (CollectorStoreException exception) when (exception.Code is "ANALYSIS_VERSION_NOT_READY" or "CITATION_PROJECTION_SCHEMA_NOT_MIGRATED")
        {
            // Additive capability failure must not hide a readable historical answer.
            return detail with
            {
                CitationAttribution = new(null, "unavailable",
                CitationDomainRules.Attribute(detail.Citations, configuration, false), exception.Code)
            };
        }
    }
}
