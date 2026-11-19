using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jogeoai.Application;
using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

/// <summary>统一时间、来源、发布版本及水位的核心指标查询。</summary>
public sealed partial class AnalyticsQueryStore(MySqlDatabase database, IAnalysisStore analysisStore) : IAnalyticsQueryStore
{
    private sealed record Snapshot(string Token, Guid TenantId, Guid SiteId, Guid ReleaseId,
        int AnalysisVersion, int SubjectConfigVersion, int CurrentConfigVersion, string SubjectSnapshot,
        AnalyticsFilter Filter, long ArrivalWatermark, long AnalysisWatermark,
        DateTimeOffset AsOf, DateTimeOffset ExpiresAt);
    private sealed record Prepared(Snapshot Snapshot, SubjectConfiguration Configuration,
        IReadOnlyList<AnalyticsCheckFact> Facts, TimeZoneInfo Zone, AnalyticsMeta Meta);
    private sealed record PageCursor(string SnapshotHash, string Kind, string Scope, int Offset);

    // 可以显式筛选的 Provider：八个默认来源加 App 渠道。App 只在请求明确指定时出现，不在任何默认集合里（DefaultProviders 不含它）。
    private static readonly HashSet<string> Providers = ["cloro", "doubao_api", "deepseek_api", "packy_qwen", "packy_glm", "packy_kimi", "packy_yuanbao", "domestic_collector", AppChannelDefinition.Provider];
    // Default population of the detailed/dashboard views: every registered API answer provider plus the domestic collector.
    internal static readonly string[] DefaultProviders = ["cloro", "doubao_api", "deepseek_api", "packy_qwen", "packy_glm", "packy_kimi", "packy_yuanbao", "domestic_collector"];
    private static readonly HashSet<string> Modes = ["api", "scraped", AppChannelDefinition.CaptureMode];
    private static readonly HashSet<string> EvidenceClasses = ["observed_surface", "api_response", "unknown"];

    /// <inheritdoc />
    public async Task<OverviewView> OverviewAsync(Guid tenantId, Guid siteId, AnalyticsFilterRequest request,
        string? snapshotToken, CancellationToken cancellationToken = default)
    {
        var data = await PrepareAsync(tenantId, siteId, request, snapshotToken, cancellationToken);
        RequireObserved(data.Snapshot.Filter);
        var sourceGroups = SourceGroups(data.Facts, data.Configuration);
        var projection = SingleSourceProjection(sourceGroups, data.Configuration);
        var previousFilter = data.Snapshot.Filter with
        {
            From = data.Snapshot.Filter.From - (data.Snapshot.Filter.To - data.Snapshot.Filter.From),
            To = data.Snapshot.Filter.From
        };
        var previous = await CachedPreviousMetricsAsync(data, previousFilter, cancellationToken);
        var engines = data.Facts.GroupBy(f => f.Engine).ToDictionary(g => g.Key,
            g => AnalyticsProjector.Compute(g.ToArray(), data.Configuration).Metrics);
        var from = LocalDate(data.Snapshot.Filter.From, data.Zone);
        var to = LocalDate(data.Snapshot.Filter.To.AddTicks(-1), data.Zone).AddDays(1);
        var sourceTrends = SourceTrends(data.Facts, data.Configuration, from, to, data.Zone, "observed");
        return new OverviewView(data.Meta, projection.Metrics,
            sourceGroups.Count > 1 ? [] : AnalyticsProjector.ObservedTrend(data.Facts, data.Configuration, from, to, data.Zone),
            sourceGroups.Count > 1 ? new Dictionary<string, CoreMetrics>() : engines,
            Compare(projection.Metrics.Visibility, previous.Visibility),
            Compare(projection.Metrics.ShareOfVoice, previous.ShareOfVoice),
            Compare(projection.Metrics.OwnCitationRate, previous.OwnCitationRate), sourceGroups, sourceTrends);
    }

    /// <inheritdoc />
    public async Task<VisibilityView> VisibilityAsync(Guid tenantId, Guid siteId, AnalyticsFilterRequest request,
        string? snapshotToken, CancellationToken cancellationToken = default)
    {
        var data = await PrepareAsync(tenantId, siteId, request, snapshotToken, cancellationToken);
        var sourceGroups = SourceGroups(data.Facts, data.Configuration);
        var observed = SingleSourceProjection(sourceGroups, data.Configuration);
        var from = LocalDate(data.Snapshot.Filter.From, data.Zone);
        var to = LocalDate(data.Snapshot.Filter.To.AddTicks(-1), data.Zone).AddDays(1);
        IReadOnlyList<AnalyticsDayPoint> trend;
        IReadOnlyList<AnalyticsCheckFact> metricFacts;
        if (data.Snapshot.Filter.AggregationMode == "latest_observation")
        {
            (trend, metricFacts) = AnalyticsProjector.LatestObservation(data.Facts, data.Configuration, from, to, data.Zone);
        }
        else
        {
            trend = AnalyticsProjector.ObservedTrend(data.Facts, data.Configuration, from, to, data.Zone);
            metricFacts = data.Facts;
        }
        var slices = new List<VisibilitySlice>();
        foreach (var group in metricFacts.GroupBy(f => f.Engine))
            slices.Add(new VisibilitySlice("engine", group.Key, AnalyticsProjector.Compute(group.ToArray(), data.Configuration).Metrics));
        foreach (var group in metricFacts.GroupBy(f => f.Country))
            slices.Add(new VisibilitySlice("country", group.Key, AnalyticsProjector.Compute(group.ToArray(), data.Configuration).Metrics));
        foreach (var group in metricFacts.GroupBy(f => f.BrandedStatus ?? "unknown"))
            slices.Add(new VisibilitySlice("brandedStatus", group.Key, AnalyticsProjector.Compute(group.ToArray(), data.Configuration).Metrics));
        foreach (var tag in metricFacts.SelectMany(f => f.Tags).Distinct(StringComparer.OrdinalIgnoreCase))
            slices.Add(new VisibilitySlice("tag", tag, AnalyticsProjector.Compute(
                metricFacts.Where(f => f.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)).ToArray(), data.Configuration).Metrics));
        var groups = SourceGroups(metricFacts, data.Configuration);
        var multi = groups.Count > 1;
        var sourceTrends = SourceTrends(data.Facts, data.Configuration, from, to, data.Zone,
            data.Snapshot.Filter.AggregationMode);
        return new VisibilityView(data.Meta, multi ? AnalyticsProjector.Compute([], data.Configuration).Metrics
            : AnalyticsProjector.Compute(metricFacts, data.Configuration).Metrics,
            multi ? [] : trend, multi ? [] : slices,
            multi ? AnalyticsProjector.Compute([], data.Configuration).Metrics : observed.Metrics,
            groups, sourceTrends);
    }

    /// <inheritdoc />
    public async Task<ShareOfVoiceView> ShareOfVoiceAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, string? snapshotToken, int topN,
        CancellationToken cancellationToken = default)
    {
        if (topN is < 1 or > 100) throw Error("INVALID_LIMIT", "topN 必须在 1–100。", 400);
        var data = await PrepareAsync(tenantId, siteId, request, snapshotToken, cancellationToken);
        var sourceGroups = SourceGroups(data.Facts, data.Configuration);
        var observed = SingleSourceProjection(sourceGroups, data.Configuration);
        var from = LocalDate(data.Snapshot.Filter.From, data.Zone);
        var to = LocalDate(data.Snapshot.Filter.To.AddTicks(-1), data.Zone).AddDays(1);
        IReadOnlyList<AnalyticsDayPoint> trend;
        IReadOnlyList<AnalyticsCheckFact> metricFacts;
        if (data.Snapshot.Filter.AggregationMode == "latest_observation")
            (trend, metricFacts) = AnalyticsProjector.LatestObservation(data.Facts, data.Configuration, from, to, data.Zone);
        else
        {
            trend = AnalyticsProjector.ObservedTrend(data.Facts, data.Configuration, from, to, data.Zone);
            metricFacts = data.Facts;
        }
        var groups = SourceGroups(metricFacts, data.Configuration);
        var multi = groups.Count > 1;
        var projection = multi ? AnalyticsProjector.Compute([], data.Configuration)
            : AnalyticsProjector.Compute(metricFacts, data.Configuration);
        var top = multi ? [] : projection.Entries.Take(topN).ToArray();
        var others = MetricRatio.Of(projection.Entries.Skip(topN).Sum(e => e.Mentions), projection.Metrics.TotalMentions);
        var sourceTrends = SourceTrends(data.Facts, data.Configuration, from, to, data.Zone,
            data.Snapshot.Filter.AggregationMode);
        return new ShareOfVoiceView(data.Meta, projection.Metrics,
            multi ? [] : projection.Entries,
            top, others, multi ? [] : trend,
            multi ? AnalyticsProjector.Compute([], data.Configuration).Metrics : observed.Metrics,
            groups, sourceTrends);
    }

    /// <inheritdoc />
    public async Task<AnalyticsPage<SubjectShareEntry>> LeaderboardAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, string? snapshotToken, string? cursor, int limit,
        CancellationToken cancellationToken = default)
    {
        ValidateLimit(limit);
        var data = await PrepareAsync(tenantId, siteId, request, snapshotToken, cancellationToken);
        if (SourceGroups(data.Facts, data.Configuration).Count > 1)
            throw Error("INVALID_FILTER", "跨来源全榜请分别选择单一来源。", 400);
        var scope = "leaderboard";
        var offset = DecodeCursor(cursor, data.Snapshot, "leaderboard", scope);
        var entries = (await ShareOfVoiceAsync(tenantId, siteId, request, data.Snapshot.Token, 100, cancellationToken)).Entries;
        return Page(data.Meta, entries, offset, limit, data.Snapshot, "leaderboard", scope);
    }

    /// <inheritdoc />
    public async Task<AnalyticsPage<PromptPerformanceView>> PromptsAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, string? snapshotToken, string? cursor, int limit,
        string? sort, CancellationToken cancellationToken = default)
    {
        ValidateLimit(limit);
        sort ??= "visibility";
        if (sort is not ("visibility" or "checkCount" or "recent"))
            throw Error("INVALID_FILTER", "Prompt 排序字段不受支持。", 400);
        var data = await PrepareAsync(tenantId, siteId, request, snapshotToken, cancellationToken);
        RequireObserved(data.Snapshot.Filter);
        var offset = DecodeCursor(cursor, data.Snapshot, "prompts", sort);
        var entries = data.Facts.GroupBy(f => (f.PromptId, f.PromptVersionId,
            Source: $"{f.Provider}|{f.CaptureMode}|{f.EvidenceClass}")).Select(group =>
        {
            var projection = AnalyticsProjector.Compute(group.ToArray(), data.Configuration).Metrics;
            var content = group.OrderByDescending(f => f.CapturedAt).Select(f => f.PromptSnapshot).FirstOrDefault();
            return new PromptPerformanceView(group.Key.PromptId, content, projection.CheckCount,
                projection.EligibleChecks, projection.Visibility.Numerator, projection.Visibility,
                projection.AnyCompetitorMentionRate, projection.OwnCitationRate,
                projection.LastCapturedAt, projection.MissingSnapshotChecks, group.Key.PromptVersionId,
                group.Key.Source);
        });
        var sorted = sort switch
        {
            "checkCount" => entries.OrderByDescending(e => e.CheckCount).ThenBy(e => e.PromptId).ToArray(),
            "recent" => entries.OrderByDescending(e => e.LastCapturedAt).ThenBy(e => e.PromptId).ToArray(),
            _ => entries.OrderByDescending(e => e.Visibility.Value).ThenBy(e => e.PromptId).ToArray()
        };
        return Page(data.Meta, sorted, offset, limit, data.Snapshot, "prompts", sort);
    }

    /// <inheritdoc />
    public async Task<PromptDetailView> PromptDetailAsync(Guid tenantId, Guid siteId, Guid promptId,
        AnalyticsFilterRequest request, string? snapshotToken, CancellationToken cancellationToken = default)
    {
        var data = await PrepareAsync(tenantId, siteId, request, snapshotToken, cancellationToken);
        RequireObserved(data.Snapshot.Filter);
        await ValidatePromptIdsAsync(tenantId, siteId, [promptId], [], cancellationToken);
        if (data.Snapshot.Filter.PromptIds.Count > 0 && !data.Snapshot.Filter.PromptIds.Contains(promptId))
            throw Error("RESOURCE_NOT_FOUND", "Prompt 不在查询快照内。", 404);
        var versions = await LoadPromptVersionsAsync(data.Snapshot, promptId, cancellationToken);
        if (versions.Count == 0 || data.Snapshot.Filter.PromptVersionIds.Count > 0
            && !versions.Any(v => data.Snapshot.Filter.PromptVersionIds.Contains(v.VersionId)))
            throw Error("RESOURCE_NOT_FOUND", "Prompt 不在查询快照内。", 404);

        var facts = data.Facts.Where(f => f.PromptId == promptId).ToArray();
        var groups = SourceGroups(facts, data.Configuration);
        var metrics = SingleSourceProjection(groups, data.Configuration).Metrics;
        var from = LocalDate(data.Snapshot.Filter.From, data.Zone);
        var to = LocalDate(data.Snapshot.Filter.To.AddTicks(-1), data.Zone).AddDays(1);
        var trends = SourceTrends(facts, data.Configuration, from, to, data.Zone, "observed");
        var engines = facts.GroupBy(f => (Source: SourceKey(f), f.Engine))
            .Select(g => new PromptEnginePerformanceView(g.Key.Source, g.Key.Engine,
                AnalyticsProjector.Compute(g.ToArray(), data.Configuration).Metrics))
            .OrderBy(e => e.Source, StringComparer.Ordinal).ThenBy(e => e.Engine, StringComparer.Ordinal).ToArray();
        var runs = facts.GroupBy(f => (f.RunId, Source: SourceKey(f)))
            .Select(g => new PromptRunHistoryView(g.Key.RunId, g.Key.Source,
                AnalyticsProjector.Compute(g.ToArray(), data.Configuration).Metrics,
                g.Select(f => f.CapturedAt).Max()))
            .OrderByDescending(r => r.LastCapturedAt).ThenBy(r => r.RunId).ThenBy(r => r.Source, StringComparer.Ordinal)
            .ToArray();
        var gaps = groups.ToDictionary(g => g.Key, g => (IReadOnlyList<PromptCompetitorGapView>)g.Value.Entries
            .Where(e => !e.IsBrand).Select(e =>
            {
                var brand = g.Value.Metrics.Visibility;
                var competitor = MetricRatio.Of(e.Mentions, g.Value.Metrics.EligibleChecks);
                return new PromptCompetitorGapView(e.SubjectId, e.Name, brand.Numerator, e.Mentions,
                    brand, competitor, brand.Numerator - e.Mentions,
                    brand.Value is null || competitor.Value is null
                        ? null : (brand.Value - competitor.Value) * 100);
            }).ToArray());
        var evidence = await LoadPromptEvidenceAsync(data.Snapshot, promptId, facts, cancellationToken);
        var historyPath = $"/api/v1/sites/{siteId}/prompts/{promptId}/checks?snapshotToken={data.Snapshot.Token}";
        return new PromptDetailView(data.Meta, promptId, versions, metrics, groups,
            groups.Count > 1 ? [] : AnalyticsProjector.ObservedTrend(facts, data.Configuration, from, to, data.Zone),
            trends, engines, runs.Take(20).ToArray(), facts.Select(f => f.RunId).Distinct().LongCount(),
            runs.Length > 20, historyPath, evidence, gaps);
    }

    private async Task<IReadOnlyList<PromptVersionHistoryView>> LoadPromptVersionsAsync(Snapshot snapshot,
        Guid promptId, CancellationToken cancellationToken)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            SELECT BIN_TO_UUID(v.id), v.version, v.content, v.locale, v.branded_status,
                   v.branded_source, v.created_at, BIN_TO_UUID(t.tag_id), t.tag_name_snapshot
            FROM prompt_versions v LEFT JOIN prompt_version_tags t ON t.prompt_version_id = v.id
            WHERE v.site_id = UUID_TO_BIN(@site) AND v.prompt_id = UUID_TO_BIN(@prompt)
              AND v.created_at <= @asOf
            ORDER BY v.version, t.tag_name_snapshot, t.tag_id;
            """, connection);
        command.Parameters.AddWithValue("@site", snapshot.SiteId.ToString());
        command.Parameters.AddWithValue("@prompt", promptId.ToString());
        command.Parameters.AddWithValue("@asOf", snapshot.AsOf.UtcDateTime);
        var versions = new List<PromptVersionHistoryView>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = Guid.Parse(reader.GetString(0));
            if (versions.Count == 0 || versions[^1].VersionId != id)
                versions.Add(new PromptVersionHistoryView(id, reader.GetInt32(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), reader.GetString(5),
                    new List<PromptVersionTagView>(), Utc(reader.GetDateTime(6))));
            if (!reader.IsDBNull(7))
                ((List<PromptVersionTagView>)versions[^1].Tags).Add(
                    new PromptVersionTagView(Guid.Parse(reader.GetString(7)), reader.GetString(8)));
        }
        return versions;
    }

    private async Task<IReadOnlyList<PromptEvidenceSummaryView>> LoadPromptEvidenceAsync(Snapshot snapshot,
        Guid promptId, IReadOnlyList<AnalyticsCheckFact> facts, CancellationToken cancellationToken)
    {
        if (facts.Count == 0) return [];
        var included = facts.Select(f => f.CheckId).ToHashSet();
        var queriesByCheck = new Dictionary<Guid, string[]>();
        var citations = new List<(Guid CheckId, string Domain)>();
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            SELECT BIN_TO_UUID(c.id), c.search_queries, ci.normalized_url
            FROM visibility_checks c JOIN analytics_check_arrivals ar ON ar.check_id = c.id
            LEFT JOIN citations ci ON ci.check_id = c.id
            WHERE c.tenant_id = UUID_TO_BIN(@tenant) AND c.site_id = UUID_TO_BIN(@site)
              AND c.prompt_id = UUID_TO_BIN(@prompt) AND ar.id <= @arrival
              AND c.captured_at >= @from AND c.captured_at < @to
            ORDER BY c.id, ci.position, ci.id;
            """, connection);
        command.Parameters.AddWithValue("@tenant", snapshot.TenantId.ToString());
        command.Parameters.AddWithValue("@site", snapshot.SiteId.ToString());
        command.Parameters.AddWithValue("@prompt", promptId.ToString());
        command.Parameters.AddWithValue("@arrival", snapshot.ArrivalWatermark);
        command.Parameters.AddWithValue("@from", snapshot.Filter.From.UtcDateTime);
        command.Parameters.AddWithValue("@to", snapshot.Filter.To.UtcDateTime);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var checkId = Guid.Parse(reader.GetString(0));
                if (!included.Contains(checkId)) continue;
                if (!queriesByCheck.ContainsKey(checkId))
                    queriesByCheck[checkId] = ParseArray(NullString(reader, 1))
                        .Where(q => !string.IsNullOrWhiteSpace(q)).Distinct(StringComparer.Ordinal).ToArray();
                if (reader.IsDBNull(2)) continue;
                var domain = Uri.TryCreate(reader.GetString(2), UriKind.Absolute, out var url)
                    && url.Scheme is "http" or "https" ? url.IdnHost.ToLowerInvariant() : "unknown";
                citations.Add((checkId, domain));
            }
        }
        return facts.GroupBy(SourceKey).OrderBy(g => g.Key, StringComparer.Ordinal).Select(group =>
        {
            var checkIds = group.Select(f => f.CheckId).ToHashSet();
            var queryGroups = checkIds.SelectMany(id => queriesByCheck.GetValueOrDefault(id) ?? [])
                .GroupBy(q => q, StringComparer.Ordinal)
                .Select(g => new PromptSearchQuerySummaryView(g.Key, g.LongCount()))
                .OrderByDescending(q => q.CheckCount).ThenBy(q => q.Query, StringComparer.Ordinal).ToArray();
            var cited = citations.Where(c => checkIds.Contains(c.CheckId)).ToArray();
            var domains = cited.GroupBy(c => c.Domain, StringComparer.Ordinal)
                .Select(g => new PromptCitationDomainSummaryView(g.Key, g.LongCount(),
                    g.Select(c => c.CheckId).Distinct().LongCount()))
                .OrderByDescending(d => d.CitationCount).ThenBy(d => d.Domain, StringComparer.Ordinal).ToArray();
            return new PromptEvidenceSummaryView(group.Key,
                checkIds.LongCount(id => queriesByCheck.TryGetValue(id, out var queries) && queries.Length > 0),
                queryGroups.LongLength, queryGroups.Take(20).ToArray(),
                cited.Select(c => c.CheckId).Distinct().LongCount(), cited.LongLength,
                domains.LongLength, domains.Take(20).ToArray());
        }).ToArray();
    }

    /// <inheritdoc />
    public async Task<AnalyticsPage<AnalyticsEvidenceRow>> EvidenceAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, string? snapshotToken, string? cursor, int limit,
        Guid? subjectId, Guid? promptId, CancellationToken cancellationToken = default)
    {
        ValidateLimit(limit);
        var data = await PrepareAsync(tenantId, siteId, request, snapshotToken, cancellationToken);
        if (subjectId is not null && data.Configuration.Subjects.All(s => s.Id != subjectId.Value))
            throw Error("RESOURCE_NOT_FOUND", "主体不存在。", 404);
        if (promptId is not null)
            await ValidatePromptIdsAsync(tenantId, siteId, [promptId.Value], [], cancellationToken);
        var scope = $"{subjectId}:{promptId}";
        var offset = DecodeCursor(cursor, data.Snapshot, "evidence", scope);
        var rows = data.Facts.Where(f => (subjectId is null || f.MentionedSubjects.Contains(subjectId.Value))
                && (promptId is null || f.PromptId == promptId.Value))
            .OrderByDescending(f => f.CapturedAt).ThenBy(f => f.CheckId)
            .Select(f => new AnalyticsEvidenceRow(f.CheckId, f.RunId, f.PromptId, f.PromptVersionId,
                f.TargetId, f.SampleIndex, f.CapturedAt, f.Provider, f.Engine, f.Status,
                f.AnalysisStatus, f.MentionedSubjects, f.SnapshotMissing)).ToArray();
        return Page(data.Meta, rows, offset, limit, data.Snapshot, "evidence", scope);
    }

    /// <inheritdoc />
    public async Task<AnalyticsCheckDetail?> CheckDetailAsync(Guid tenantId, Guid checkId,
        string? snapshotToken, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            SELECT BIN_TO_UUID(c.site_id), BIN_TO_UUID(c.run_id), BIN_TO_UUID(c.prompt_id),
                   BIN_TO_UUID(c.prompt_version_id), BIN_TO_UUID(c.run_target_id), c.sample_index,
                   c.prompt_snapshot, c.provider, c.engine, c.model_version, c.country, c.locale,
                   c.capture_mode, c.evidence_class, c.status, c.answer, c.captured_at,
                   c.search_queries, c.prompt_tags_snapshot, c.branded_status_snapshot, c.snapshot_missing,
                   c.error_code, c.error_message, c.query_availability
            FROM visibility_checks c WHERE c.tenant_id = UUID_TO_BIN(@tenant) AND c.id = UUID_TO_BIN(@check);
            """, connection);
        command.Parameters.AddWithValue("@tenant", tenantId.ToString());
        command.Parameters.AddWithValue("@check", checkId.ToString());
        Guid siteId, runId, promptId;
        Guid? versionId, targetId;
        int sample;
        string prompt, provider, engine, status, queryAvailability;
        string? model, country, locale, mode, evidenceClass, answer, branded, errorCode, errorMessage;
        DateTimeOffset? captured;
        IReadOnlyList<string> queries, tags;
        bool snapshotMissing;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return null;
            siteId = Guid.Parse(reader.GetString(0));
            runId = Guid.Parse(reader.GetString(1));
            promptId = Guid.Parse(reader.GetString(2));
            versionId = GuidOrNull(reader, 3);
            targetId = GuidOrNull(reader, 4);
            sample = reader.GetInt32(5);
            prompt = reader.GetString(6);
            provider = NullString(reader, 7) ?? "unknown";
            engine = reader.GetString(8);
            model = NullString(reader, 9);
            country = NullString(reader, 10);
            locale = NullString(reader, 11);
            mode = NullString(reader, 12);
            evidenceClass = NullString(reader, 13);
            status = reader.GetString(14);
            answer = NullString(reader, 15);
            captured = reader.IsDBNull(16) ? null : Utc(reader.GetDateTime(16));
            queries = ParseArray(NullString(reader, 17));
            tags = ParseArray(NullString(reader, 18));
            branded = NullString(reader, 19);
            snapshotMissing = reader.GetBoolean(20);
            errorCode = NullString(reader, 21);
            errorMessage = NullString(reader, 22);
            queryAvailability = reader.GetString(23);
        }
        Snapshot? snapshot = null;
        if (snapshotToken is not null)
        {
            snapshot = await ResolveSnapshotAsync(tenantId, siteId, new AnalyticsFilterRequest(), snapshotToken, cancellationToken);
            var filterFact = new AnalyticsCheckFact(checkId, runId, promptId, versionId, targetId, sample,
                captured, status, !string.IsNullOrWhiteSpace(answer), "pending", false, snapshotMissing,
                false, [], provider, engine, country ?? "unknown", locale ?? "unknown",
                mode ?? "unknown", evidenceClass ?? "unknown", tags, branded, prompt);
            if (captured is null || captured < snapshot.Filter.From || captured >= snapshot.Filter.To
                || !MatchesFilter(filterFact, snapshot.Filter))
                throw Error("RESOURCE_NOT_FOUND", "该 Check 不在查询快照内。", 404);
            await using var included = new MySqlCommand("SELECT id FROM analytics_check_arrivals WHERE check_id = UUID_TO_BIN(@check) AND id <= @watermark;", connection);
            included.Parameters.AddWithValue("@check", checkId.ToString());
            included.Parameters.AddWithValue("@watermark", snapshot.ArrivalWatermark);
            if (await included.ExecuteScalarAsync(cancellationToken) is null)
                throw Error("RESOURCE_NOT_FOUND", "该 Check 不在查询快照内。", 404);
        }
        var citations = new List<AnalysisCitationView>();
        await using (var cited = new MySqlCommand("""
            SELECT normalized_url, source_domain, title, position FROM citations
            WHERE check_id = UUID_TO_BIN(@check) ORDER BY position, id;
            """, connection))
        {
            cited.Parameters.AddWithValue("@check", checkId.ToString());
            await using var reader = await cited.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                citations.Add(new AnalysisCitationView(reader.GetString(0), NullString(reader, 1),
                    NullString(reader, 2), reader.GetInt32(3)));
        }
        var analysis = await analysisStore.GetCheckAnalysisAsync(tenantId, checkId,
            snapshot?.ReleaseId, snapshot?.AnalysisWatermark, cancellationToken);
        return new AnalyticsCheckDetail(checkId, runId, promptId, versionId, targetId, sample,
            prompt, provider, engine, model, country, locale, mode, evidenceClass, status,
            answer, captured, queries, citations, analysis, tags, branded,
            snapshotMissing, errorCode, errorMessage, queryAvailability);
    }

    private async Task<Prepared> PrepareAsync(Guid tenantId, Guid siteId, AnalyticsFilterRequest request,
        string? token, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cached = CachedPrepared(tenantId, siteId, request, token);
        if (cached is not null) return cached;
        var snapshot = await ResolveSnapshotAsync(tenantId, siteId, request, token, cancellationToken);
        await ValidatePromptIdsAsync(tenantId, siteId, snapshot.Filter.PromptIds,
            snapshot.Filter.PromptVersionIds, cancellationToken);
        var configuration = SubjectConfiguration.Parse(siteId, snapshot.SubjectConfigVersion, snapshot.SubjectSnapshot);
        if (snapshot.Filter.SubjectIds.Any(id => configuration.Subjects.All(subject => subject.Id != id)))
            throw Error("RESOURCE_NOT_FOUND", "主体不存在。", 404);
        var facts = await LoadFactsAsync(snapshot, configuration, cancellationToken);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(snapshot.Filter.TimeZone);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            SELECT (SELECT MAX(completed_at) FROM analysis_events WHERE release_id = UUID_TO_BIN(@release)
                       AND id <= @analysis),
                   (SELECT COUNT(*) FROM analytics_check_arrivals a JOIN visibility_checks c ON c.id = a.check_id
                    WHERE a.tenant_id = UUID_TO_BIN(@tenant) AND a.site_id = UUID_TO_BIN(@site)
                      AND a.id <= @arrival AND c.captured_at IS NULL)
            """, connection);
        command.Parameters.AddWithValue("@release", snapshot.ReleaseId.ToString());
        command.Parameters.AddWithValue("@analysis", snapshot.AnalysisWatermark);
        command.Parameters.AddWithValue("@tenant", tenantId.ToString());
        command.Parameters.AddWithValue("@site", siteId.ToString());
        command.Parameters.AddWithValue("@arrival", snapshot.ArrivalWatermark);
        DateTimeOffset? updated;
        long missingTime;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            updated = reader.IsDBNull(0) ? null : Utc(reader.GetDateTime(0));
            missingTime = reader.GetInt64(1);
        }
        var pending = facts.Count(f => f.Status == "succeeded" && f.AnswerPresent
            && f.AnalysisStatus is "pending" or "processing");
        var failed = facts.Count(f => f.AnalysisStatus == "failed");
        var meta = new AnalyticsMeta(snapshot.Token, snapshot.Filter, "mentions-v1",
            snapshot.AnalysisVersion, snapshot.SubjectConfigVersion,
            snapshot.SubjectConfigVersion < snapshot.CurrentConfigVersion,
            snapshot.AsOf, snapshot.ExpiresAt, facts.Select(f => f.CapturedAt).Max(),
            updated, snapshot.ArrivalWatermark, snapshot.AnalysisWatermark, pending, failed, missingTime);
        var prepared = new Prepared(snapshot, configuration, facts, zone, meta);
        RememberPrepared(prepared);
        return prepared;
    }

    private async Task<Snapshot> ResolveSnapshotAsync(Guid tenantId, Guid siteId, AnalyticsFilterRequest request,
        string? token, CancellationToken cancellationToken)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        if (token is not null)
        {
            await using var siteCheck = new MySqlCommand("""
                SELECT id FROM sites WHERE tenant_id = UUID_TO_BIN(@tenant)
                  AND id = UUID_TO_BIN(@site);
                """, connection);
            siteCheck.Parameters.AddWithValue("@tenant", tenantId.ToString());
            siteCheck.Parameters.AddWithValue("@site", siteId.ToString());
            if (await siteCheck.ExecuteScalarAsync(cancellationToken) is null)
                throw Error("RESOURCE_NOT_FOUND", "工作区不存在。", 404);
            if (token.Length is < 20 or > 200) throw Error("SNAPSHOT_EXPIRED", "快照已过期，请重新查询。", 409);
            await using var existing = new MySqlCommand("""
                SELECT BIN_TO_UUID(s.release_id), r.version, s.subject_config_version,
                       s.current_subject_config_version, r.subject_snapshot, s.filter_json,
                       s.arrival_watermark, s.analysis_watermark, s.as_of, s.expires_at
                FROM analytics_snapshots s JOIN analysis_releases r ON r.id = s.release_id
                JOIN site_brand_settings b ON b.site_id = s.site_id
                WHERE s.token_hash = @hash AND s.tenant_id = UUID_TO_BIN(@tenant)
                  AND s.site_id = UUID_TO_BIN(@site);
                """, connection);
            existing.Parameters.AddWithValue("@hash", Hash(token));
            existing.Parameters.AddWithValue("@tenant", tenantId.ToString());
            existing.Parameters.AddWithValue("@site", siteId.ToString());
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || reader.GetDateTime(9) <= DateTime.UtcNow)
                throw Error("SNAPSHOT_EXPIRED", "快照已过期，请重新查询。", 409);
            var decodedFilter = JsonSerializer.Deserialize<AnalyticsFilter>(reader.GetString(5))!;
            var filter = decodedFilter with
            {
                ModelVersions = decodedFilter.ModelVersions ?? [],
                SubjectIds = decodedFilter.SubjectIds ?? [],
                Statuses = decodedFilter.Statuses ?? []
            };
            if (!MatchesExplicitFilter(request, filter))
                throw Error("INVALID_FILTER", "筛选与查询快照不匹配。", 400);
            return new Snapshot(token, tenantId, siteId, Guid.Parse(reader.GetString(0)),
                reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetString(4),
                filter, reader.GetInt64(6), reader.GetInt64(7), Utc(reader.GetDateTime(8)), Utc(reader.GetDateTime(9)));
        }
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // Prompt 写入也先锁 sites；共享这把锁后，版本提交与快照 asOf 有确定顺序。
        await using (var siteFence = new MySqlCommand("""
            SELECT id FROM sites WHERE tenant_id = UUID_TO_BIN(@tenant) AND id = UUID_TO_BIN(@site)
            FOR UPDATE;
            """, connection, transaction))
        {
            siteFence.Parameters.AddWithValue("@tenant", tenantId.ToString());
            siteFence.Parameters.AddWithValue("@site", siteId.ToString());
            if (await siteFence.ExecuteScalarAsync(cancellationToken) is null)
                throw Error("RESOURCE_NOT_FOUND", "工作区不存在。", 404);
        }
        await AnalysisEnqueue.EnsureSiteStateAsync(connection, transaction, tenantId, siteId, cancellationToken);
        await using var state = new MySqlCommand("""
            SELECT BIN_TO_UUID(s.published_release_id), r.version, r.subject_config_version,
                   b.subject_config_version, r.subject_snapshot, b.time_zone
            FROM site_analysis_state s JOIN analysis_releases r ON r.id = s.published_release_id
            JOIN site_brand_settings b ON b.site_id = s.site_id
            WHERE s.tenant_id = UUID_TO_BIN(@tenant) AND s.site_id = UUID_TO_BIN(@site) FOR UPDATE;
            """, connection, transaction);
        state.Parameters.AddWithValue("@tenant", tenantId.ToString());
        state.Parameters.AddWithValue("@site", siteId.ToString());
        Guid releaseId;
        int analysisVersion, configVersion, currentConfigVersion;
        string subjectSnapshot, defaultZone;
        await using (var reader = await state.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) throw Error("RESOURCE_NOT_FOUND", "工作区不存在。", 404);
            releaseId = Guid.Parse(reader.GetString(0));
            analysisVersion = reader.GetInt32(1);
            configVersion = reader.GetInt32(2);
            currentConfigVersion = reader.GetInt32(3);
            subjectSnapshot = reader.GetString(4);
            defaultZone = reader.GetString(5);
        }
        await using var watermarks = new MySqlCommand("""
            SELECT (SELECT COALESCE(MAX(id), 0) FROM analytics_check_arrivals),
                   (SELECT COALESCE(MAX(id), 0) FROM analysis_events), UTC_TIMESTAMP(6);
            """, connection, transaction);
        long arrival, analysis;
        DateTimeOffset asOf;
        await using (var reader = await watermarks.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            arrival = reader.GetInt64(0);
            analysis = reader.GetInt64(1);
            asOf = Utc(reader.GetDateTime(2));
        }
        var filterNew = NormalizeFilter(request, defaultZone, asOf);
        var filterJson = JsonSerializer.Serialize(filterNew);
        var newToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var expiry = asOf.AddMinutes(15);
        await using var insert = new MySqlCommand("""
            INSERT INTO analytics_snapshots (token_hash, tenant_id, site_id, release_id,
                subject_config_version, current_subject_config_version, filter_hash, filter_json, arrival_watermark,
                analysis_watermark, as_of, expires_at)
            VALUES (@hash, UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), UUID_TO_BIN(@release),
                @config, @currentConfig, @filterHash, @filter, @arrival, @analysis, @asOf, @expires);
            """, connection, transaction);
        insert.Parameters.AddWithValue("@hash", Hash(newToken));
        insert.Parameters.AddWithValue("@tenant", tenantId.ToString());
        insert.Parameters.AddWithValue("@site", siteId.ToString());
        insert.Parameters.AddWithValue("@release", releaseId.ToString());
        insert.Parameters.AddWithValue("@config", configVersion);
        insert.Parameters.AddWithValue("@currentConfig", currentConfigVersion);
        insert.Parameters.AddWithValue("@filterHash", Hash(filterJson));
        insert.Parameters.AddWithValue("@filter", filterJson);
        insert.Parameters.AddWithValue("@arrival", arrival);
        insert.Parameters.AddWithValue("@analysis", analysis);
        insert.Parameters.AddWithValue("@asOf", asOf.UtcDateTime);
        insert.Parameters.AddWithValue("@expires", expiry.UtcDateTime);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new Snapshot(newToken, tenantId, siteId, releaseId, analysisVersion,
            configVersion, currentConfigVersion, subjectSnapshot, filterNew, arrival, analysis, asOf, expiry);
    }

    private async Task<IReadOnlyList<AnalyticsCheckFact>> LoadFactsAsync(Snapshot snapshot,
        SubjectConfiguration configuration, CancellationToken cancellationToken)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            SELECT BIN_TO_UUID(c.id), BIN_TO_UUID(c.run_id), BIN_TO_UUID(c.prompt_id),
                   BIN_TO_UUID(c.prompt_version_id), BIN_TO_UUID(c.run_target_id), c.sample_index,
                   c.captured_at, c.status, COALESCE(c.answer_present,
                       c.answer IS NOT NULL AND TRIM(c.answer) <> ''),
                   w.status, a.configuration_missing, a.completed_event_id, c.snapshot_missing,
                   c.provider, c.engine, c.country, c.locale, c.capture_mode, c.evidence_class,
                   c.prompt_tags_snapshot, c.branded_status_snapshot, c.prompt_snapshot,
                   c.query_availability, c.model_version, c.duration_ms
            FROM visibility_checks c JOIN analytics_check_arrivals ar ON ar.check_id = c.id
            LEFT JOIN analysis_check_work w ON w.check_id = c.id AND w.release_id = UUID_TO_BIN(@release)
            LEFT JOIN check_analysis a ON a.check_id = c.id AND a.release_id = UUID_TO_BIN(@release)
            WHERE c.tenant_id = UUID_TO_BIN(@tenant) AND c.site_id = UUID_TO_BIN(@site)
              AND ar.id <= @watermark AND c.captured_at >= @from AND c.captured_at < @to
            ORDER BY c.captured_at, c.id;
            """, connection);
        command.Parameters.AddWithValue("@tenant", snapshot.TenantId.ToString());
        command.Parameters.AddWithValue("@site", snapshot.SiteId.ToString());
        command.Parameters.AddWithValue("@release", snapshot.ReleaseId.ToString());
        command.Parameters.AddWithValue("@watermark", snapshot.ArrivalWatermark);
        command.Parameters.AddWithValue("@from", snapshot.Filter.From.UtcDateTime);
        command.Parameters.AddWithValue("@to", snapshot.Filter.To.UtcDateTime);
        var rows = new List<AnalyticsCheckFact>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var eventId = reader.IsDBNull(11) ? (long?)null : reader.GetInt64(11);
                var status = eventId is not null && eventId <= snapshot.AnalysisWatermark
                    ? "completed" : reader.IsDBNull(9) || reader.GetString(9) == "completed"
                        ? "pending" : reader.GetString(9);
                var row = new AnalyticsCheckFact(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                    Guid.Parse(reader.GetString(2)), GuidOrNull(reader, 3), GuidOrNull(reader, 4),
                    reader.GetInt32(5), reader.IsDBNull(6) ? null : Utc(reader.GetDateTime(6)),
                    reader.GetString(7), reader.GetBoolean(8), status,
                    !reader.IsDBNull(10) && reader.GetBoolean(10), reader.GetBoolean(12), false,
                    [], NullString(reader, 13) ?? "unknown", reader.GetString(14),
                    NullString(reader, 15) ?? "unknown", NullString(reader, 16) ?? "unknown",
                    NullString(reader, 17) ?? "unknown", NullString(reader, 18) ?? "unknown",
                    ParseArray(NullString(reader, 19)), NullString(reader, 20), NullString(reader, 21) ?? "",
                    NullString(reader, 22) ?? "unknown", NullString(reader, 23),
                    reader.IsDBNull(24) ? null : reader.GetInt32(24));
                if (MatchesFilter(row, snapshot.Filter)) rows.Add(row);
            }
        }
        if (rows.Count == 0) return rows;
        var index = rows.Select((row, i) => (row.CheckId, i)).ToDictionary(v => v.CheckId, v => v.i);
        var subjects = rows.ToDictionary(r => r.CheckId, _ => new HashSet<Guid>());
        await using (var presence = new MySqlCommand("""
            SELECT BIN_TO_UUID(p.check_id), BIN_TO_UUID(p.subject_id)
            FROM check_subject_presence p JOIN check_analysis a
              ON a.release_id = p.release_id AND a.check_id = p.check_id
            WHERE p.release_id = UUID_TO_BIN(@release) AND a.completed_event_id <= @watermark;
            """, connection))
        {
            presence.Parameters.AddWithValue("@release", snapshot.ReleaseId.ToString());
            presence.Parameters.AddWithValue("@watermark", snapshot.AnalysisWatermark);
            await using var reader = await presence.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var check = Guid.Parse(reader.GetString(0));
                if (subjects.TryGetValue(check, out var set)) set.Add(Guid.Parse(reader.GetString(1)));
            }
        }
        var ownDomains = OwnCitationDomains(configuration);
        var own = new HashSet<Guid>();
        if (ownDomains.Length > 0)
        {
            await using var citations = new MySqlCommand("""
                SELECT BIN_TO_UUID(c.check_id), c.normalized_url
                FROM citations c JOIN visibility_checks v ON v.id = c.check_id
                JOIN analytics_check_arrivals a ON a.check_id = v.id
                WHERE v.tenant_id = UUID_TO_BIN(@tenant) AND v.site_id = UUID_TO_BIN(@site)
                  AND a.id <= @watermark AND v.captured_at >= @from AND v.captured_at < @to;
                """, connection);
            citations.Parameters.AddWithValue("@tenant", snapshot.TenantId.ToString());
            citations.Parameters.AddWithValue("@site", snapshot.SiteId.ToString());
            citations.Parameters.AddWithValue("@watermark", snapshot.ArrivalWatermark);
            citations.Parameters.AddWithValue("@from", snapshot.Filter.From.UtcDateTime);
            citations.Parameters.AddWithValue("@to", snapshot.Filter.To.UtcDateTime);
            await using var reader = await citations.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var check = Guid.Parse(reader.GetString(0));
                if (!index.ContainsKey(check)) continue;
                if (IsOwnCitationUrl(reader.GetString(1), ownDomains, configuration.IncludeSubdomains)) own.Add(check);
            }
        }
        for (var i = 0; i < rows.Count; i++)
        {
            var id = rows[i].CheckId;
            rows[i] = rows[i] with { MentionedSubjects = subjects[id].ToArray(), OwnCitation = own.Contains(id) };
        }
        return rows;
    }

    /// <summary>读取冻结主体配置中本品牌的域名词项；列表事实与回答详情的自有引用判定共用。</summary>
    internal static string[] OwnCitationDomains(SubjectConfiguration configuration)
        => configuration.Terms.Where(t => t.SubjectId == configuration.BrandId
            && t.Kind.Contains("domain", StringComparison.Ordinal)).Select(t => t.Key).ToArray();

    /// <summary>
    /// 判断一条引用 URL 是否指向品牌自有域名：仅 http/https 绝对 URL 参与判定，主机按 IdnHost 小写比较，
    /// 子域名是否命中由冻结配置的开关决定。
    /// </summary>
    internal static bool IsOwnCitationUrl(string normalizedUrl, IReadOnlyCollection<string> ownDomains,
        bool includeSubdomains)
    {
        if (ownDomains.Count == 0) return false;
        if (!Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https")) return false;
        // The URL is the citation evidence; source_domain is display metadata and
        // can be stale or inconsistent in imported historical rows.
        var domain = parsed.IdnHost.ToLowerInvariant();
        // Keep citation ownership consistent with the frozen subject
        // configuration used by MentionMatcher and citation
        // classification.  A disabled subdomain flag must not count
        // blog.example.com as an owned citation for example.com.
        return ownDomains.Any(d => domain == d
            || includeSubdomains && domain.EndsWith('.' + d, StringComparison.Ordinal));
    }

    private static bool MatchesFilter(AnalyticsCheckFact fact, AnalyticsFilter filter)
        => (filter.Providers.Count == 0 || filter.Providers.Contains(fact.Provider))
        && (filter.Engines.Count == 0 || filter.Engines.Contains(fact.Engine))
        && (filter.Countries.Count == 0 || filter.Countries.Contains(fact.Country, StringComparer.OrdinalIgnoreCase))
        && (filter.Locales.Count == 0 || filter.Locales.Contains(fact.Locale, StringComparer.OrdinalIgnoreCase))
        && (filter.CaptureModes.Count == 0 || filter.CaptureModes.Contains(fact.CaptureMode))
        && (filter.EvidenceClasses.Count == 0 || filter.EvidenceClasses.Contains(fact.EvidenceClass))
        && (filter.ModelVersions.Count == 0 || filter.ModelVersions.Contains(fact.ModelVersion ?? "", StringComparer.OrdinalIgnoreCase))
        && (filter.ModelVersionUnavailable is null || filter.ModelVersionUnavailable ==
            (string.IsNullOrWhiteSpace(fact.ModelVersion) || string.Equals(fact.ModelVersion, fact.Engine, StringComparison.OrdinalIgnoreCase)))
        && (filter.Statuses.Count == 0 || filter.Statuses.Contains(fact.Status))
        && (filter.PromptIds.Count == 0 || filter.PromptIds.Contains(fact.PromptId))
        && (filter.PromptVersionIds.Count == 0 || fact.PromptVersionId is not null
            && filter.PromptVersionIds.Contains(fact.PromptVersionId.Value))
        && (filter.Tags.Count == 0 || (filter.TagMode == "all"
            ? filter.Tags.All(t => fact.Tags.Contains(t, StringComparer.OrdinalIgnoreCase))
            : filter.Tags.Any(t => fact.Tags.Contains(t, StringComparer.OrdinalIgnoreCase))))
        && (filter.BrandedStatus is null || fact.BrandedStatus == filter.BrandedStatus);

    private async Task ValidatePromptIdsAsync(Guid tenantId, Guid siteId, IReadOnlyList<Guid> promptIds,
        IReadOnlyList<Guid> versionIds, CancellationToken cancellationToken)
    {
        if (promptIds.Count == 0 && versionIds.Count == 0) return;
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        foreach (var id in promptIds)
        {
            await using var command = new MySqlCommand("""
                SELECT p.id FROM prompts p JOIN sites s ON s.id = p.site_id
                WHERE s.tenant_id = UUID_TO_BIN(@tenant) AND p.site_id = UUID_TO_BIN(@site)
                  AND p.id = UUID_TO_BIN(@id);
                """, connection);
            command.Parameters.AddWithValue("@tenant", tenantId.ToString());
            command.Parameters.AddWithValue("@site", siteId.ToString());
            command.Parameters.AddWithValue("@id", id.ToString());
            if (await command.ExecuteScalarAsync(cancellationToken) is null)
                throw Error("RESOURCE_NOT_FOUND", "Prompt 不存在。", 404);
        }
        foreach (var id in versionIds)
        {
            await using var command = new MySqlCommand("""
                SELECT v.id FROM prompt_versions v JOIN sites s ON s.id = v.site_id
                WHERE s.tenant_id = UUID_TO_BIN(@tenant) AND v.site_id = UUID_TO_BIN(@site)
                  AND v.id = UUID_TO_BIN(@id);
                """, connection);
            command.Parameters.AddWithValue("@tenant", tenantId.ToString());
            command.Parameters.AddWithValue("@site", siteId.ToString());
            command.Parameters.AddWithValue("@id", id.ToString());
            if (await command.ExecuteScalarAsync(cancellationToken) is null)
                throw Error("RESOURCE_NOT_FOUND", "Prompt 版本不存在。", 404);
        }
    }

    internal static AnalyticsFilter NormalizeFilter(AnalyticsFilterRequest request, string defaultZone,
        DateTimeOffset now)
    {
        var zoneId = request.TimeZone ?? defaultZone;
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId); }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        { throw Error("INVALID_FILTER", "时区无效。", 400); }
        var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var defaultFromLocal = localToday.AddDays(-29).ToDateTime(TimeOnly.MinValue);
        var from = request.From?.ToUniversalTime() ?? new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(defaultFromLocal, zone));
        var to = request.To?.ToUniversalTime() ?? now;
        var localFrom = LocalDate(from, zone);
        var localTo = LocalDate(to.AddTicks(-1), zone).AddDays(1);
        if (from >= to || localTo.DayNumber - localFrom.DayNumber > 366)
            throw Error("INVALID_TIME_RANGE", "时间范围必须递增且不超过 366 天。", 400);
        var providers = NormalizeStrings(request.Providers ?? ["cloro"]);
        var modes = NormalizeStrings(request.CaptureModes ?? ["scraped"]);
        var classes = NormalizeStrings(request.EvidenceClasses ?? ["observed_surface"]);
        if (providers.Any(v => !Providers.Contains(v)) || modes.Any(v => !Modes.Contains(v))
            || classes.Any(v => !EvidenceClasses.Contains(v)))
            throw Error("INVALID_FILTER", "来源或证据类别筛选不受支持。", 400);
        RequireSingleChannelModes(modes);
        var aggregation = request.AggregationMode ?? "observed";
        if (aggregation is not ("observed" or "latest_observation"))
            throw Error("INVALID_FILTER", "聚合模式不受支持。", 400);
        var tagMode = request.TagMode ?? "any";
        if (tagMode is not ("any" or "all")) throw Error("INVALID_FILTER", "标签匹配模式无效。", 400);
        var branded = request.BrandedStatus?.Trim().ToLowerInvariant();
        if (branded is not null and not ("branded" or "unbranded" or "unknown"))
            throw Error("INVALID_FILTER", "品牌词分类筛选无效。", 400);
        var countries = NormalizeStrings(request.Countries);
        if (countries.Any(v => v.Length != 2)) throw Error("INVALID_FILTER", "国家代码无效。", 400);
        return new AnalyticsFilter(from, to, zoneId, providers, NormalizeStrings(request.Engines), countries,
            NormalizeStrings(request.Locales), modes, classes,
            NormalizeGuids(request.PromptIds), NormalizeGuids(request.PromptVersionIds),
            NormalizeStrings(request.Tags), tagMode, branded, aggregation,
            NormalizeStrings(request.ModelVersions), request.ModelVersionUnavailable,
            NormalizeGuids(request.SubjectIds), NormalizeStrings(request.Statuses));
    }

    /// <summary>
    /// App 口径只能单独查询：采集模式 app 不能与其他采集模式出现在同一个筛选里，
    /// 这样不存在把 App 样本与 API / 网页样本合成一个数字的查询。
    /// </summary>
    internal static void RequireSingleChannelModes(IReadOnlyList<string> modes)
    {
        if (modes.Count > 1 && modes.Contains(AppChannelDefinition.CaptureMode))
            throw Error("INVALID_FILTER", "采集模式 app 不能与其他采集模式出现在同一个筛选里。", 400);
    }

    private static IReadOnlyList<string> NormalizeStrings(IReadOnlyList<string>? values)
        => (values ?? []).Select(v => v.Trim().ToLowerInvariant()).Where(v => v.Length > 0)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    private static IReadOnlyList<Guid> NormalizeGuids(IReadOnlyList<Guid>? values)
        => (values ?? []).Distinct().Order().ToArray();
    private static bool MatchesExplicitFilter(AnalyticsFilterRequest request, AnalyticsFilter stored)
        => (request.From is null || request.From.Value.ToUniversalTime() == stored.From)
        && (request.To is null || request.To.Value.ToUniversalTime() == stored.To)
        && (request.TimeZone is null || request.TimeZone == stored.TimeZone)
        && (request.Providers is null || NormalizeStrings(request.Providers).SequenceEqual(stored.Providers))
        && (request.Engines is null || NormalizeStrings(request.Engines).SequenceEqual(stored.Engines))
        && (request.Countries is null || NormalizeStrings(request.Countries).SequenceEqual(stored.Countries))
        && (request.Locales is null || NormalizeStrings(request.Locales).SequenceEqual(stored.Locales))
        && (request.CaptureModes is null || NormalizeStrings(request.CaptureModes).SequenceEqual(stored.CaptureModes))
        && (request.EvidenceClasses is null || NormalizeStrings(request.EvidenceClasses).SequenceEqual(stored.EvidenceClasses))
        && (request.PromptIds is null || NormalizeGuids(request.PromptIds).SequenceEqual(stored.PromptIds))
        && (request.PromptVersionIds is null || NormalizeGuids(request.PromptVersionIds).SequenceEqual(stored.PromptVersionIds))
        && (request.Tags is null || NormalizeStrings(request.Tags).SequenceEqual(stored.Tags))
        && (request.TagMode is null || request.TagMode == stored.TagMode)
        && (request.BrandedStatus is null || request.BrandedStatus == stored.BrandedStatus)
        && (request.AggregationMode is null || request.AggregationMode == stored.AggregationMode)
        && (request.ModelVersions is null || NormalizeStrings(request.ModelVersions).SequenceEqual(stored.ModelVersions))
        && (request.ModelVersionUnavailable is null || request.ModelVersionUnavailable == stored.ModelVersionUnavailable)
        && (request.SubjectIds is null || NormalizeGuids(request.SubjectIds).SequenceEqual(stored.SubjectIds))
        && (request.Statuses is null || NormalizeStrings(request.Statuses).SequenceEqual(stored.Statuses));
    private static void RequireObserved(AnalyticsFilter filter)
    {
        if (filter.AggregationMode != "observed")
            throw Error("INVALID_FILTER", "此入口仅支持 observed 聚合模式。", 400);
    }
    private static string SourceKey(AnalyticsCheckFact fact)
        => $"{fact.Provider}|{fact.CaptureMode}|{fact.EvidenceClass}";
    private static IReadOnlyDictionary<string, VoiceProjection> SourceGroups(IReadOnlyList<AnalyticsCheckFact> facts,
        SubjectConfiguration config) => facts.GroupBy(SourceKey)
        .ToDictionary(g => g.Key, g => AnalyticsProjector.Compute(g.ToArray(), config));
    private static IReadOnlyDictionary<string, IReadOnlyList<AnalyticsDayPoint>> SourceTrends(
        IReadOnlyList<AnalyticsCheckFact> facts, SubjectConfiguration config, DateOnly from, DateOnly to,
        TimeZoneInfo zone, string mode)
        => facts.GroupBy(SourceKey)
            .ToDictionary(g => g.Key, g => mode == "latest_observation"
                ? AnalyticsProjector.LatestObservation(g.ToArray(), config, from, to, zone).Trend
                : AnalyticsProjector.ObservedTrend(g.ToArray(), config, from, to, zone));
    private static VoiceProjection SingleSourceProjection(IReadOnlyDictionary<string, VoiceProjection> groups,
        SubjectConfiguration config) => groups.Count == 1 ? groups.Values.Single()
        : AnalyticsProjector.Compute([], config);
    private static RatioComparison Compare(MetricRatio current, MetricRatio previous)
        => new(current, previous, current.Value is null || previous.Value is null
            ? null : (current.Value - previous.Value) * 100,
            previous.Value is null ? "previous_denominator_zero_or_not_ready" : null);
    private static DateOnly LocalDate(DateTimeOffset timestamp, TimeZoneInfo zone)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timestamp, zone).DateTime);
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    private static Guid? GuidOrNull(MySqlDataReader reader, int index)
        => reader.IsDBNull(index) ? null : Guid.Parse(reader.GetString(index));
    private static string? NullString(MySqlDataReader reader, int index)
        => reader.IsDBNull(index) ? null : reader.GetString(index);
    private static IReadOnlyList<string> ParseArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<string[]>(json) ?? []; }
        catch (JsonException) { return []; }
    }
    private static CollectorStoreException Error(string code, string message, int status) => new(code, message, status);
    private static void ValidateLimit(int limit)
    {
        if (limit is < 1 or > 100) throw Error("INVALID_LIMIT", "limit 必须在 1–100。", 400);
    }
    private static int DecodeCursor(string? cursor, Snapshot snapshot, string kind, string scope)
    {
        if (cursor is null) return 0;
        try
        {
            if (cursor.Length > 2048) throw new FormatException();
            var value = JsonSerializer.Deserialize<PageCursor>(Convert.FromBase64String(cursor));
            if (value is null || value.SnapshotHash != Hash(snapshot.Token) || value.Kind != kind
                || value.Scope != scope || value.Offset < 0) throw new FormatException();
            return value.Offset;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
        { throw Error("INVALID_CURSOR", "游标与当前快照或筛选不匹配。", 400); }
    }
    private static AnalyticsPage<T> Page<T>(AnalyticsMeta meta, IReadOnlyList<T> items, int offset, int limit,
        Snapshot snapshot, string kind, string scope)
    {
        var selected = items.Skip(offset).Take(limit).ToArray();
        var next = offset + selected.Length < items.Count
            ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
                new PageCursor(Hash(snapshot.Token), kind, scope, offset + selected.Length))) : null;
        return new AnalyticsPage<T>(meta, selected, next);
    }
}
