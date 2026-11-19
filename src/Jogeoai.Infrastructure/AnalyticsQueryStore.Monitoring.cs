using System.Data;
using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

public sealed partial class AnalyticsQueryStore
{
    internal sealed record MonitoringStoredAnswer(Guid TenantId, Guid SiteId, AnalyticsCheckFact Fact,
        string? Answer, IReadOnlyList<string> WebQueries, string? Country, string? Locale,
        string? ErrorCode, string? ErrorMessage, DateTimeOffset CreatedAt,
        long? ArrivalId, DateTimeOffset? ArrivedAt, ResultQualityView? ResultQuality = null, string? AnswerMode = null,
        string? ResultMetadata = null);

    internal static string? NormalizeMonitoringSearch(string? search)
    {
        search = search?.Trim();
        if (string.IsNullOrEmpty(search)) return null;
        if (search.Length > 200)
            throw Error("INVALID_FILTER", "问题搜索最多二百字符。", 400);
        return search.ToUpperInvariant();
    }

    internal static string? NormalizeMonitoringMention(string? mention)
    {
        mention = mention?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(mention)) return null;
        if (mention is not ("mentioned" or "not_mentioned" or "unknown"))
            throw Error("INVALID_FILTER", "提及筛选仅支持 mentioned、not_mentioned 或 unknown。", 400);
        return mention;
    }

    /// <summary>被引域名筛选：与域名榜 domain 相同的规范化主机键；空白表示不筛选，非法返回 INVALID_FILTER。</summary>
    internal static string? NormalizeMonitoringCitedDomain(string? citedDomain)
    {
        if (string.IsNullOrWhiteSpace(citedDomain)) return null;
        return CitationDomainRules.CanonicalHost(citedDomain.Trim(), alreadyCanonical: true)
            ?? throw Error("INVALID_FILTER", "citedDomain 必须是有效的规范化主机域名。", 400);
    }

    internal static string MonitoringRecordScope(string? normalizedSearch, string? normalizedMention = null,
        string? normalizedCitedDomain = null, ReputationSentimentState? sentiment = null, string? termKey = null,
        ReputationTermPolarity? termPolarity = null)
    {
        // 不传提及、被引域名、口碑状态、印象词或词极性筛选时保留原有游标作用域，兼容已有列表和搜索游标。
        var scope = normalizedSearch is null ? "monitoring-records" : $"monitoring-records:search:{Hash(normalizedSearch)}";
        scope = normalizedMention is null ? scope : $"{scope}:mention:{normalizedMention}";
        scope = normalizedCitedDomain is null ? scope : $"{scope}:citedDomain:{normalizedCitedDomain}";
        scope = sentiment is null ? scope : $"{scope}:sentiment:{ReputationRules.StateName(sentiment.Value)}";
        scope = termKey is null ? scope : $"{scope}:term:{Hash(termKey)}";
        return termPolarity is null ? scope : $"{scope}:termPolarity:{ReputationRules.PolarityName(termPolarity.Value)}";
    }

    internal static string MonitoringMentionState(string collectionStatus, bool answerPresent,
        string? analysisStatus, bool configurationMissing, bool brandMentioned)
        => collectionStatus != "succeeded" || !answerPresent ? "not_applicable"
            : analysisStatus != "completed" || configurationMissing ? "unknown"
            : brandMentioned ? "mentioned" : "not_mentioned";

    internal static string MonitoringMentionState(AnalyticsCheckFact fact, Guid brandId)
        => MonitoringMentionState(fact.Status, fact.AnswerPresent, fact.AnalysisStatus,
            fact.ConfigurationMissing, fact.MentionedSubjects.Contains(brandId));

    private static bool MatchesMonitoringMention(string state, string? normalizedMention)
        => normalizedMention is null || state == normalizedMention;

    internal static IEnumerable<AnalyticsCheckFact> FilterMonitoringRecords(
        IEnumerable<AnalyticsCheckFact> facts, string? normalizedSearch,
        string? normalizedMention = null, Guid brandId = default)
        => facts.Where(f => MatchesMonitoringSearch(f.PromptSnapshot, normalizedSearch)
                && MatchesMonitoringMention(MonitoringMentionState(f, brandId), normalizedMention))
            .OrderByDescending(f => f.CapturedAt).ThenByDescending(f => f.CheckId);

    private static bool MatchesMonitoringSearch(string? promptSnapshot, string? normalizedSearch)
        => normalizedSearch is null
            || (promptSnapshot?.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase) ?? false);

    internal static void RequireMonitoringSnapshot(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw Error("SNAPSHOT_REQUIRED", "回答详情必须使用列表快照。", 400);
    }

    internal static void ValidateMonitoringRecordSnapshot(MonitoringStoredAnswer row,
        Guid tenantId, Guid siteId, DateTimeOffset asOf, long arrivalWatermark)
    {
        if (row.TenantId != tenantId || row.SiteId != siteId)
            throw Error("RESOURCE_NOT_FOUND", "检测记录不存在。", 404);
        if (row.CreatedAt > asOf || row.ArrivalId > arrivalWatermark || row.ArrivedAt > asOf
            || row.Fact.CapturedAt > asOf
            || row.ArrivalId is null && (row.Fact.Status is "succeeded" or "failed" or "cancelled"
                || row.Fact.CapturedAt is not null))
            throw Error("SNAPSHOT_REFRESH_REQUIRED", "记录已在快照后入库或完成，请同时刷新列表和详情。", 409);
    }

    internal static bool MonitoringMatchesListFilters(AnalyticsCheckFact fact, AnalyticsFilter filter,
        string? normalizedSearch)
        => fact.CapturedAt is not null && fact.CapturedAt >= filter.From && fact.CapturedAt < filter.To
            && MatchesFilter(fact, filter) && MatchesMonitoringSearch(fact.PromptSnapshot, normalizedSearch);

    /// <summary>
    /// 由详情读取到的分析证据与引用合成单条事实，再用与列表相同的纯函数计算排名与曝光分数。
    /// 首次偏移取各主体证据的最小 OffsetStart（与 check_subject_presence.first_offset 的写入口径一致），出现次数为证据条数，
    /// 与列表读取 mention_evidence 的统计口径一致；
    /// 尚未到达的记录不计算，两项指标均为 ANALYSIS_NOT_READY。
    /// </summary>
    internal static CheckBrandMetrics MonitoringBrandMetrics(MonitoringStoredAnswer row,
        IReadOnlyList<AnalysisCitationView> citations, CheckAnalysisView? analysis,
        SubjectConfiguration configuration)
    {
        if (row.ArrivalId is null) return DashboardAnalyticsProjector.UnavailableCheckBrandMetrics("ANALYSIS_NOT_READY");
        var ownDomains = OwnCitationDomains(configuration);
        var fact = row.Fact with
        {
            AnalysisStatus = analysis?.Status ?? "pending",
            ConfigurationMissing = analysis?.ConfigurationMissing ?? true,
            MentionedSubjects = analysis?.Mentions.Select(m => m.SubjectId).Distinct().ToArray() ?? [],
            OwnCitation = citations.Any(c => IsOwnCitationUrl(c.Url, ownDomains, configuration.IncludeSubdomains))
        };
        var stats = analysis?.Mentions.GroupBy(m => m.SubjectId)
            .ToDictionary(g => g.Key, g => new SubjectStats(g.Min(m => m.OffsetStart), g.Count()));
        return DashboardAnalyticsProjector.CheckBrandMetrics(fact, configuration, stats);
    }

    internal static MonitoringAnswerDetailView BuildMonitoringAnswerDetail(MonitoringStoredAnswer row,
        IReadOnlyList<AnalysisCitationView> citations, CheckAnalysisView? analysis,
        AnalyticsFilter filter, string? normalizedSearch, string? normalizedMention = null,
        CheckBrandMetrics? brandMetrics = null)
    {
        var fact = row.Fact;
        // 未到达的记录只展示真实采集状态，不能泄露并发完成后的正文、引用或分析。
        var arrived = row.ArrivalId is not null;
        var mentionState = MonitoringMentionState(fact.Status, arrived && fact.AnswerPresent,
            analysis?.Status, analysis?.ConfigurationMissing ?? true,
            analysis?.Mentions.Any(m => m.SubjectType == "brand") ?? false);
        // app 块与没有 URL 的引用来自同一份回答元数据；与正文、引用一样，未到达的记录不返回。
        var (app, sourcesWithoutUrl) = arrived ? AppAnswerMetadataCodec.Decode(row.ResultMetadata) : (null, null);
        return new MonitoringAnswerDetailView(row.SiteId, fact.CheckId, fact.RunId, fact.PromptId,
            fact.PromptSnapshot ?? "", fact.Provider, fact.Engine, fact.ModelVersion,
            row.Country, row.Locale, fact.Status, arrived && fact.AnswerPresent,
            arrived && row.Answer is not null ? new VisibilityAnswer(row.Answer) : null,
            arrived ? row.WebQueries : [], fact.QueryAvailability, arrived ? citations : [],
            fact.CapturedAt, fact.DurationMs, row.ErrorCode, row.ErrorMessage,
            arrived ? analysis : null, arrived && MonitoringMatchesListFilters(fact, filter, normalizedSearch)
                && MatchesMonitoringMention(mentionState, normalizedMention), mentionState,
            ResultQuality: arrived ? row.ResultQuality : null,
            ModelVersionUnavailable: ModelVersionUnavailable(fact) || arrived && row.ResultQuality?.ModelVersionProvided == false,
            BrandMetrics: brandMetrics, AnswerMode: AnswerModeSchema.ParseStored(row.AnswerMode),
            CaptureMode: fact.CaptureMode, App: app, SourcesWithoutUrl: sourcesWithoutUrl);
    }

    /// <inheritdoc />
    public Task<MonitoringAnswerDetailView?> MonitoringAnswerDetailAsync(Guid tenantId, Guid siteId,
        Guid checkId, string? snapshotToken, string? search, CancellationToken cancellationToken = default)
        => MonitoringAnswerDetailAsync(tenantId, siteId, checkId, snapshotToken, search, null, cancellationToken);

    /// <inheritdoc />
    public async Task<MonitoringAnswerDetailView?> MonitoringAnswerDetailAsync(Guid tenantId, Guid siteId,
        Guid checkId, string? snapshotToken, string? search, string? mention,
        CancellationToken cancellationToken = default)
    {
        RequireMonitoringSnapshot(snapshotToken);
        search = NormalizeMonitoringSearch(search);
        mention = NormalizeMonitoringMention(mention);
        var snapshot = await ResolveSnapshotAsync(tenantId, siteId, new AnalyticsFilterRequest(),
            snapshotToken, cancellationToken);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await GeminiResultSchema.RequireAsync(connection, null, cancellationToken);
        // 与列表行读取同一列；047 未执行时为空（历史）。
        var answerModeMigrated = await AnswerModeSchema.IsMigratedAsync(connection, null, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await using var command = new MySqlCommand($"""
            SELECT BIN_TO_UUID(c.tenant_id), BIN_TO_UUID(c.site_id), BIN_TO_UUID(c.run_id),
                   BIN_TO_UUID(c.prompt_id), BIN_TO_UUID(c.prompt_version_id), BIN_TO_UUID(c.run_target_id),
                   c.sample_index, c.prompt_snapshot, c.provider, c.engine, c.model_version,
                   c.country, c.locale, c.capture_mode, c.evidence_class, c.status, c.answer,
                   COALESCE(c.answer_present, c.answer IS NOT NULL AND TRIM(c.answer) <> ''),
                   c.captured_at, c.duration_ms, c.search_queries, c.query_availability,
                   c.prompt_tags_snapshot, c.branded_status_snapshot, c.snapshot_missing,
                   c.error_code, c.error_message, c.created_at, ar.id, ar.arrived_at, c.result_metadata,
                   {(answerModeMigrated ? "c.answer_mode" : "NULL")}
            FROM visibility_checks c
            LEFT JOIN analytics_check_arrivals ar ON ar.check_id = c.id
              AND ar.tenant_id = c.tenant_id AND ar.site_id = c.site_id
            WHERE c.tenant_id = UUID_TO_BIN(@tenant) AND c.site_id = UUID_TO_BIN(@site)
              AND c.id = UUID_TO_BIN(@check);
            """, connection, transaction);
        command.Parameters.AddWithValue("@tenant", tenantId.ToString());
        command.Parameters.AddWithValue("@site", siteId.ToString());
        command.Parameters.AddWithValue("@check", checkId.ToString());
        MonitoringStoredAnswer row;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return null;
            var country = NullString(reader, 11);
            var locale = NullString(reader, 12);
            var fact = new AnalyticsCheckFact(checkId, Guid.Parse(reader.GetString(2)),
                Guid.Parse(reader.GetString(3)), GuidOrNull(reader, 4), GuidOrNull(reader, 5),
                reader.GetInt32(6), reader.IsDBNull(18) ? null : Utc(reader.GetDateTime(18)),
                reader.GetString(15), reader.GetBoolean(17), "pending", false, reader.GetBoolean(24),
                false, [], NullString(reader, 8) ?? "unknown", reader.GetString(9),
                country ?? "unknown", locale ?? "unknown", NullString(reader, 13) ?? "unknown",
                NullString(reader, 14) ?? "unknown", ParseArray(NullString(reader, 22)),
                NullString(reader, 23), reader.GetString(7), reader.GetString(21), NullString(reader, 10),
                reader.IsDBNull(19) ? null : reader.GetInt32(19));
            row = new MonitoringStoredAnswer(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                fact, NullString(reader, 16), ParseArray(NullString(reader, 20)), country, locale,
                NullString(reader, 25), NullString(reader, 26), Utc(reader.GetDateTime(27)),
                reader.IsDBNull(28) ? null : reader.GetInt64(28),
                reader.IsDBNull(29) ? null : Utc(reader.GetDateTime(29)), ResultQualityCodec.Decode(NullString(reader, 30)),
                NullString(reader, 31), NullString(reader, 30));
        }
        ValidateMonitoringRecordSnapshot(row, tenantId, siteId, snapshot.AsOf, snapshot.ArrivalWatermark);
        var citations = new List<AnalysisCitationView>();
        if (row.ArrivalId is not null)
        {
            await using var cited = new MySqlCommand("""
                SELECT ci.normalized_url, ci.source_domain, ci.title, ci.position
                FROM citations ci JOIN visibility_checks c ON c.id = ci.check_id
                WHERE c.tenant_id = UUID_TO_BIN(@tenant) AND c.site_id = UUID_TO_BIN(@site)
                  AND ci.check_id = UUID_TO_BIN(@check) AND ci.created_at <= @asOf
                ORDER BY ci.position, ci.id;
                """, connection, transaction);
            cited.Parameters.AddWithValue("@tenant", tenantId.ToString());
            cited.Parameters.AddWithValue("@site", siteId.ToString());
            cited.Parameters.AddWithValue("@check", checkId.ToString());
            cited.Parameters.AddWithValue("@asOf", snapshot.AsOf.UtcDateTime);
            await using var reader = await cited.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                citations.Add(new AnalysisCitationView(reader.GetString(0), NullString(reader, 1),
                    NullString(reader, 2), reader.GetInt32(3)));
        }
        await transaction.CommitAsync(cancellationToken);
        var analysis = row.ArrivalId is null ? null
            : await analysisStore.GetCheckAnalysisAsync(tenantId, checkId, snapshot.ReleaseId,
                snapshot.AnalysisWatermark, cancellationToken);
        var configuration = SubjectConfiguration.Parse(siteId, snapshot.SubjectConfigVersion, snapshot.SubjectSnapshot);
        var detail = BuildMonitoringAnswerDetail(row, citations, analysis, snapshot.Filter, search, mention,
            MonitoringBrandMetrics(row, citations, analysis, configuration));
        // 情感判定与列表使用同一状态归并；038 未执行时为空，详情其余内容不受影响。
        return detail with
        {
            ReputationSentiment = await DetailReputationSentimentAsync(snapshot, configuration, checkId,
                detail.MentionState, cancellationToken)
        };
    }
}
