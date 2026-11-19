using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

public sealed partial class AnalyticsQueryStore
{
    // Provider tasks currently persist poll attempts and lease renewals in the
    // same counter; they cannot be reported as retry counts without an
    // operation snapshot. Keep this nullable until that snapshot is available.
    private sealed record DashboardOps(string? Answer, string PromptLocale, long CitationCount, long? ProviderRetries);
    private sealed record TargetIdentity(string Provider, string Engine, string Country, string Locale,
        string CaptureMode, bool Enabled);

    public async Task<MonitoringSummaryView> MonitoringSummaryAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, string? snapshotToken, CancellationToken cancellationToken = default)
    {
        var data = await PrepareAsync(tenantId, siteId, request, snapshotToken, cancellationToken);
        var ops = await LoadDashboardOpsAsync(data.Snapshot, cancellationToken);
        var facts = EnrichFacts(data.Facts, ops);
        var today = LocalDate(DateTimeOffset.UtcNow, data.Zone);
        var metrics = AnalyticsProjector.Compute(facts, data.Configuration).Metrics;
        var targets = await LoadTargetIdentitiesAsync(data.Snapshot, cancellationToken);
        var modelViews = BuildModelViews(facts, data.Configuration, data.Zone, data.Snapshot.AsOf, siteId, targets);
        var statuses = modelViews
            .GroupBy(m => m.Status).ToDictionary(g => g.Key, g => g.LongCount());
        var previousFilter = data.Snapshot.Filter with
        {
            From = data.Snapshot.Filter.From - (data.Snapshot.Filter.To - data.Snapshot.Filter.From),
            To = data.Snapshot.Filter.From
        };
        var previousFacts = await CachedPreviousFactsAsync(data, previousFilter, cancellationToken);
        var previousOps = await LoadDashboardOpsAsync(data.Snapshot with { Filter = previousFilter }, cancellationToken);
        var previous = AnalyticsProjector.Compute(EnrichFacts(previousFacts, previousOps), data.Configuration).Metrics;
        var latency = DashboardAnalyticsProjector.Latency(facts);
        var previousLatency = DashboardAnalyticsProjector.Latency(EnrichFacts(previousFacts, previousOps));
        var availability = facts.GroupBy(f => f.QueryAvailability ?? "unknown")
            .ToDictionary(g => g.Key, g => g.LongCount(), StringComparer.OrdinalIgnoreCase);
        var quality = MonitoringQuality(facts, data.Meta);
        return new MonitoringSummaryView(data.Meta, facts.LongCount(), facts.LongCount(f =>
            f.CapturedAt is not null && LocalDate(f.CapturedAt.Value, data.Zone) == today),
            modelViews.LongCount(m => m.Status != "disabled"), metrics.Visibility,
            latency, null, statuses,
            new MonitoringComparison(previous.Visibility,
                metrics.Visibility.Value is null || previous.Visibility.Value is null ? null
                    : (metrics.Visibility.Value - previous.Visibility.Value) * 100,
                previousLatency, previous.Visibility.Value is null ? "previous_denominator_zero_or_not_ready" : null),
            quality with { QueryAvailability = availability });
    }

    public async Task<IReadOnlyList<MonitoringModelView>> MonitoringModelsAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, string? snapshotToken, CancellationToken cancellationToken = default)
    {
        var data = await PrepareAsync(tenantId, siteId, request, snapshotToken, cancellationToken);
        var ops = await LoadDashboardOpsAsync(data.Snapshot, cancellationToken);
        var targets = await LoadTargetIdentitiesAsync(data.Snapshot, cancellationToken);
        return BuildModelViews(EnrichFacts(data.Facts, ops), data.Configuration, data.Zone, data.Snapshot.AsOf, siteId, targets);
    }

    /// <inheritdoc />
    public Task<MonitoringRecordPage> MonitoringRecordsAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, string? snapshotToken, string? cursor, int limit,
        CancellationToken cancellationToken = default)
        => MonitoringRecordsAsync(tenantId, siteId, request, snapshotToken, cursor, limit, null, cancellationToken);

    /// <inheritdoc />
    public Task<MonitoringRecordPage> MonitoringRecordsAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, string? snapshotToken, string? cursor, int limit, string? search,
        CancellationToken cancellationToken = default)
        => MonitoringRecordsAsync(tenantId, siteId, request, snapshotToken, cursor, limit, search, null, cancellationToken);

    /// <inheritdoc />
    public Task<MonitoringRecordPage> MonitoringRecordsAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, string? snapshotToken, string? cursor, int limit, string? search,
        string? mention, CancellationToken cancellationToken = default)
        => MonitoringRecordsAsync(tenantId, siteId, request, snapshotToken, cursor, limit, search, mention, null,
            cancellationToken);

    /// <inheritdoc />
    public Task<MonitoringRecordPage> MonitoringRecordsAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, string? snapshotToken, string? cursor, int limit, string? search,
        string? mention, string? citedDomain, CancellationToken cancellationToken = default)
        => MonitoringRecordsAsync(tenantId, siteId, request, snapshotToken, cursor, limit, search, mention, citedDomain,
            null, null, null, cancellationToken);

    /// <inheritdoc />
    public async Task<MonitoringRecordPage> MonitoringRecordsAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, string? snapshotToken, string? cursor, int limit, string? search,
        string? mention, string? citedDomain, string? sentiment, string? term, string? termPolarity,
        CancellationToken cancellationToken = default)
    {
        ValidateLimit(limit);
        search = NormalizeMonitoringSearch(search);
        mention = NormalizeMonitoringMention(mention);
        var cited = NormalizeMonitoringCitedDomain(citedDomain);
        var sentimentFilter = NormalizeReputationSentiment(sentiment);
        var termKey = NormalizeReputationTerm(term);
        var polarityFilter = NormalizeReputationTermPolarity(termPolarity, termKey);
        var reputationFilter = sentimentFilter is not null || termKey is not null;
        // 038 未执行时：不带口碑筛选的列表照常返回（reputationSentiment 为空）；带口碑筛选时明确返回 503。
        var reputationReady = await ReputationSchema.ExistsAsync(database, cancellationToken);
        if (reputationFilter && !reputationReady) throw ReputationSchema.Missing();
        var data = await PrepareAsync(tenantId, siteId, request, snapshotToken, cancellationToken);
        var ops = await LoadDashboardOpsAsync(data.Snapshot, cancellationToken);
        var facts = EnrichFacts(data.Facts, ops);
        var scope = MonitoringRecordScope(search, mention, cited, sentimentFilter, termKey, polarityFilter);
        var offset = DecodeDashboardCursor(cursor, data.Snapshot.Token, "monitoring-records", scope);
        // 总数在分页前、同一快照与筛选（含搜索、提及状态、被引域名、口碑状态、印象词和词极性）下计算，翻页时保持不变。
        IEnumerable<AnalyticsCheckFact> matching = FilterMonitoringRecords(facts, search, mention, data.Configuration.BrandId);
        if (cited is not null)
        {
            var citing = await LoadCitingCheckIdsAsync(data, cited, cancellationToken);
            matching = matching.Where(f => citing.Contains(f.CheckId));
        }
        ReputationStates? reputation = null;
        if (reputationFilter)
        {
            // 与 analytics/reputation 共用同一状态归并与成员判定（ReputationProjector.MatchesRecordFilter），计数逐条一致。
            reputation = await LoadReputationStatesAsync(data.Snapshot, data.Configuration.BrandId,
                data.Snapshot.Filter.From, data.Snapshot.Filter.To, null, termKey is not null, cancellationToken);
            var reputationFacts = BuildReputationFacts(data.Facts, data.Configuration, data.Zone, reputation)
                .ToDictionary(f => f.CheckId);
            matching = matching.Where(f => ReputationProjector.MatchesRecordFilter(reputationFacts[f.CheckId],
                sentimentFilter, termKey, polarityFilter));
        }
        var filtered = matching.ToArray();
        var rows = filtered
            .Select(f =>
            {
                var answer = ops.TryGetValue(f.CheckId, out var op) ? op.Answer : null;
                var summary = string.IsNullOrWhiteSpace(answer) ? "" : answer.Trim();
                var truncated = summary.Length > 240;
                if (truncated) summary = summary[..240];
                var visible = f.MentionedSubjects.Contains(data.Configuration.BrandId);
                return new MonitoringRecordView(f.CheckId, f.CapturedAt, f.Engine, f.Provider, f.Engine,
                    f.ModelVersion, ModelVersionUnavailable(f), f.Country, f.Locale, f.PromptId.ToString(),
                    f.PromptSnapshot ?? "", summary, truncated, f.AnswerPresent, visible,
                    visible ? "mentioned" : f.AnswerPresent ? "not_mentioned" : "no_answer",
                    f.Status, f.AnalysisStatus, f.DurationMs, op?.ProviderRetries, f.CitationCount,
                    f.QueryAvailability, $"/api/v1/visibility-checks/{f.CheckId}",
                    MonitoringMentionState(f, data.Configuration.BrandId), CaptureMode: f.CaptureMode);
            }).ToArray();
        var page = Page(data.Meta, rows, offset, limit, data.Snapshot, "monitoring-records", scope);
        var pageIds = page.Items.Select(row => row.CheckId).ToArray();
        var quality = await LoadResultQualityAsync(data.Snapshot, pageIds, cancellationToken);
        // 回答模式与详情读取同一列 visibility_checks.answer_mode；047 未执行或历史检测为空。
        var answerModes = await LoadAnswerModesAsync(data.Snapshot, pageIds, cancellationToken);
        // 采集错误代码与详情读取同一列 visibility_checks.error_code，只读本页。
        var errorCodes = await LoadErrorCodesAsync(data.Snapshot, pageIds, cancellationToken);
        // 只为本页记录读取主体统计（首次偏移与出现次数）；列表行与详情用同一纯函数计算单条排名与曝光分数。
        var stats = await LoadSubjectStatsAsync(data.Snapshot, pageIds, cancellationToken);
        var factsById = filtered.ToDictionary(f => f.CheckId);
        if (reputationReady && reputation is null)
            reputation = await LoadReputationStatesAsync(data.Snapshot, data.Configuration.BrandId, null, null,
                pageIds, false, cancellationToken);
        var items = page.Items.Select(row => row with
        {
            ResultQuality = quality.GetValueOrDefault(row.CheckId),
            ModelVersionUnavailable = row.ModelVersionUnavailable || quality.GetValueOrDefault(row.CheckId)?.ModelVersionProvided == false,
            BrandMetrics = DashboardAnalyticsProjector.CheckBrandMetrics(factsById[row.CheckId], data.Configuration,
                stats.GetValueOrDefault(row.CheckId)),
            ReputationSentiment = reputation is null ? null : RecordReputationSentiment(factsById[row.CheckId],
                data.Configuration.BrandId, reputation),
            AnswerMode = answerModes.GetValueOrDefault(row.CheckId),
            ErrorCode = errorCodes.GetValueOrDefault(row.CheckId)
        }).ToArray();
        return new MonitoringRecordPage(page.Meta, items, page.NextCursor, filtered.LongLength);
    }

    private async Task<Dictionary<Guid, ResultQualityView?>> LoadResultQualityAsync(Snapshot snapshot,
        IReadOnlyList<Guid> checkIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, ResultQualityView?>();
        if (checkIds.Count == 0) return result;
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(ct);
        await GeminiResultSchema.RequireAsync(connection, null, ct);
        var ids = string.Join(",", checkIds.Select((_, i) => $"UUID_TO_BIN(@id{i})"));
        await using var command = new MySqlCommand($"""
            SELECT BIN_TO_UUID(c.id), c.result_metadata
            FROM visibility_checks c JOIN analytics_check_arrivals ar ON ar.check_id = c.id
              AND ar.tenant_id = c.tenant_id AND ar.site_id = c.site_id
            WHERE c.tenant_id = UUID_TO_BIN(@tenant) AND c.site_id = UUID_TO_BIN(@site)
              AND c.id IN ({ids}) AND c.created_at <= @asOf AND c.captured_at <= @asOf
              AND ar.id <= @arrival AND ar.arrived_at <= @asOf;
            """, connection);
        command.Parameters.AddWithValue("@tenant", snapshot.TenantId.ToString());
        command.Parameters.AddWithValue("@site", snapshot.SiteId.ToString());
        command.Parameters.AddWithValue("@asOf", snapshot.AsOf.UtcDateTime);
        command.Parameters.AddWithValue("@arrival", snapshot.ArrivalWatermark);
        for (var i = 0; i < checkIds.Count; i++) command.Parameters.AddWithValue($"@id{i}", checkIds[i].ToString());
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(Guid.Parse(reader.GetString(0)), ResultQualityCodec.Decode(NullString(reader, 1)));
        return result;
    }

    // 检测创建时写入的回答模式不随分析或快照变化，只按本页检测标识读取；迁移 047 之前全部为空（历史）。
    private async Task<Dictionary<Guid, AnswerMode?>> LoadAnswerModesAsync(Snapshot snapshot,
        IReadOnlyList<Guid> checkIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, AnswerMode?>();
        if (checkIds.Count == 0) return result;
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(ct);
        if (!await AnswerModeSchema.IsMigratedAsync(connection, null, ct)) return result;
        var ids = string.Join(",", checkIds.Select((_, i) => $"UUID_TO_BIN(@id{i})"));
        await using var command = new MySqlCommand($"""
            SELECT BIN_TO_UUID(c.id), c.answer_mode FROM visibility_checks c
            WHERE c.tenant_id = UUID_TO_BIN(@tenant) AND c.site_id = UUID_TO_BIN(@site) AND c.id IN ({ids});
            """, connection);
        command.Parameters.AddWithValue("@tenant", snapshot.TenantId.ToString());
        command.Parameters.AddWithValue("@site", snapshot.SiteId.ToString());
        for (var i = 0; i < checkIds.Count; i++) command.Parameters.AddWithValue($"@id{i}", checkIds[i].ToString());
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result[Guid.Parse(reader.GetString(0))] = AnswerModeSchema.ParseStored(NullString(reader, 1));
        return result;
    }

    // 采集错误代码只按本页检测标识读取；没有错误的检测不在结果里（列表行的 errorCode 为空）。
    private async Task<Dictionary<Guid, string?>> LoadErrorCodesAsync(Snapshot snapshot,
        IReadOnlyList<Guid> checkIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, string?>();
        if (checkIds.Count == 0) return result;
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(ct);
        var ids = string.Join(",", checkIds.Select((_, i) => $"UUID_TO_BIN(@id{i})"));
        await using var command = new MySqlCommand($"""
            SELECT BIN_TO_UUID(c.id), c.error_code FROM visibility_checks c
            WHERE c.tenant_id = UUID_TO_BIN(@tenant) AND c.site_id = UUID_TO_BIN(@site) AND c.id IN ({ids})
              AND c.error_code IS NOT NULL;
            """, connection);
        command.Parameters.AddWithValue("@tenant", snapshot.TenantId.ToString());
        command.Parameters.AddWithValue("@site", snapshot.SiteId.ToString());
        for (var i = 0; i < checkIds.Count; i++) command.Parameters.AddWithValue($"@id{i}", checkIds[i].ToString());
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result[Guid.Parse(reader.GetString(0))] = reader.GetString(1);
        return result;
    }

    public async Task<BrandDashboardView> BrandDashboardAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, string? snapshotToken, CancellationToken cancellationToken = default)
    {
        var data = await PrepareAsync(tenantId, siteId, request, snapshotToken, cancellationToken);
        var ops = await LoadDashboardOpsAsync(data.Snapshot, cancellationToken);
        var facts = EnrichFacts(data.Facts, ops);
        var stats = await LoadSubjectStatsAsync(data.Snapshot, cancellationToken);
        var projection = AnalyticsProjector.Compute(facts, data.Configuration);
        var brand = data.Configuration.BrandId;
        var competitorCount = data.Configuration.Subjects.Count(s => s.Type == "competitor");
        var placement = DashboardAnalyticsProjector.PlacementRates(facts, data.Configuration, stats);
        var metrics = new BrandDashboardMetrics(projection.Metrics.Visibility,
            projection.Metrics.ShareOfVoice, projection.Metrics.OwnCitationRate,
            DashboardAnalyticsProjector.Ranking(facts, brand, stats),
            DashboardAnalyticsProjector.AggregateExposure(facts, data.Configuration, stats),
            data.Configuration.Subjects.Count, competitorCount, projection.Metrics.EligibleChecks,
            projection.Metrics.TotalMentions, placement.First, placement.TopThree);
        var from = LocalDate(data.Snapshot.Filter.From, data.Zone);
        var to = LocalDate(data.Snapshot.Filter.To.AddTicks(-1), data.Zone).AddDays(1);
        var trend = new List<BrandDashboardTrendPoint>();
        var days = new List<(DateOnly Date, IReadOnlyList<AnalyticsCheckFact> Facts)>();
        for (var date = from; date < to; date = date.AddDays(1))
        {
            var day = facts.Where(f => f.CapturedAt is not null && LocalDate(f.CapturedAt.Value, data.Zone) == date).ToArray();
            days.Add((date, day));
            var dayMetrics = AnalyticsProjector.Compute(day, data.Configuration).Metrics;
            trend.Add(new BrandDashboardTrendPoint(date, dayMetrics.Visibility, dayMetrics.ShareOfVoice,
                DashboardAnalyticsProjector.Ranking(day, brand, stats),
                DashboardAnalyticsProjector.AggregateExposure(day, data.Configuration, stats),
                dayMetrics.EligibleChecks));
        }
        var selectedSubjects = data.Snapshot.Filter.SubjectIds;
        var competitors = projection.Entries.Where(e => !e.IsBrand
            && (selectedSubjects.Count == 0 || selectedSubjects.Contains(e.SubjectId))).Select(e =>
        {
            var subject = data.Configuration.Subjects.First(s => s.Id == e.SubjectId);
            var rank = DashboardAnalyticsProjector.Ranking(facts, subject.Id, stats);
            return new BrandCompetitorComparison(subject.Id, subject.Name, false,
                MetricRatio.Of(e.Mentions, projection.Metrics.EligibleChecks), e.Share, rank,
                DashboardAnalyticsProjector.CompetitorExposurePlaceholder(e.Share),
                e.PromptCount, e.Rank, $"/api/v1/sites/{siteId}/analytics/evidence?subjectId={subject.Id}");
        }).ToArray();
        var subjectTrends = DashboardAnalyticsProjector.SubjectTrends(days, data.Configuration,
            selectedSubjects, stats);
        // 前一等长窗口与 KPI 环比同一定义；前期事实按快照 token 缓存（同一 token 的前期筛选恒定），
        // 主体统计只按发布版本与分析水位读取、不限时间窗口，因此当前 stats 已覆盖前期 Check。
        var previousFilter = data.Snapshot.Filter with
        {
            From = data.Snapshot.Filter.From - (data.Snapshot.Filter.To - data.Snapshot.Filter.From),
            To = data.Snapshot.Filter.From
        };
        var previousFacts = await CachedPreviousFactsAsync(data, previousFilter, cancellationToken);
        // 分组按提问「当前」所属的提问分组归类（不是采集时的分组），两个窗口共用同一份映射。
        var promptGroups = await LoadPromptGroupMapAsync(data.Snapshot, cancellationToken);
        var groups = DashboardAnalyticsProjector.Groups(facts, previousFacts, data.Configuration, stats,
            promptGroups);
        var urlVersion = await GetUrlVersionAsync(data.Snapshot, cancellationToken);
        var quality = new DashboardQuality(data.Meta.PendingAnalysisChecks, data.Meta.FailedAnalysisChecks,
            data.Meta.MissingCapturedAtChecks, data.Configuration.Subjects.Count, competitorCount,
            facts.SelectMany(f => f.Tags).Distinct(StringComparer.OrdinalIgnoreCase).LongCount(),
            competitorCount == 0 ? "NO_COMPETITORS_CONFIGURED" : null,
            groups.Count == 0 ? "NO_PROMPT_GROUP_DATA" : null,
            urlVersion, await HasPendingUrlNormalizationAsync(data.Snapshot, urlVersion, cancellationToken)
                ? "normalization_pending" : null,
            facts.GroupBy(f => f.QueryAvailability ?? "unknown").ToDictionary(g => g.Key, g => g.LongCount()),
            groups.Count);
        return new BrandDashboardView(data.Meta, metrics, trend, competitors, groups, quality, subjectTrends);
    }

    public async Task<AnalyticsPage<BrandDashboardGroup>> BrandDashboardBreakdownsAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, string? snapshotToken, string? cursor, int limit,
        CancellationToken cancellationToken = default)
    {
        ValidateLimit(limit);
        var dashboard = await BrandDashboardAsync(tenantId, siteId, request, snapshotToken, cancellationToken);
        var offset = DecodeDashboardCursor(cursor, dashboard.Meta.SnapshotToken,
            "brand-dashboard-breakdowns", "groups");
        var selected = dashboard.Groups.Skip(offset).Take(limit).ToArray();
        var next = offset + selected.Length < dashboard.Groups.Count
            ? Convert.ToBase64String(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                new PageCursor(Hash(dashboard.Meta.SnapshotToken), "brand-dashboard-breakdowns",
                    "groups", offset + selected.Length))) : null;
        return new AnalyticsPage<BrandDashboardGroup>(dashboard.Meta, selected, next);
    }

    internal static int DecodeDashboardCursor(string? cursor, string snapshotToken, string kind, string scope)
    {
        if (cursor is null) return 0;
        try
        {
            if (cursor.Length > 2048) throw new FormatException();
            var value = System.Text.Json.JsonSerializer.Deserialize<PageCursor>(
                Convert.FromBase64String(cursor));
            if (value is null || value.SnapshotHash != Hash(snapshotToken)
                || value.Kind != kind || value.Scope != scope || value.Offset < 0)
                throw new FormatException();
            return value.Offset;
        }
        catch (Exception exception) when (exception is FormatException or System.Text.Json.JsonException
            or ArgumentException)
        { throw Error("INVALID_CURSOR", "游标与当前快照或筛选不匹配。", 400); }
    }

    private static string ModelIdentity(AnalyticsCheckFact f)
        => string.Join('|', f.Provider, f.Engine, CanonicalModelVersion(f), f.Country, f.Locale, f.CaptureMode);

    private static string CanonicalModelVersion(AnalyticsCheckFact fact)
        => string.IsNullOrWhiteSpace(fact.ModelVersion)
            || string.Equals(fact.ModelVersion, fact.Engine, StringComparison.OrdinalIgnoreCase)
            ? "<unavailable>" : fact.ModelVersion!;

    private static bool ModelVersionUnavailable(AnalyticsCheckFact fact)
        => CanonicalModelVersion(fact) == "<unavailable>";

    private static IReadOnlyList<AnalyticsCheckFact> EnrichFacts(IReadOnlyList<AnalyticsCheckFact> facts,
        IReadOnlyDictionary<Guid, DashboardOps> ops)
        => facts.Select(f => ops.TryGetValue(f.CheckId, out var op)
            ? f with { ProviderRetries = op.ProviderRetries ?? 0, CitationCount = op.CitationCount,
                PromptLocale = op.PromptLocale }
            : f).ToArray();

    private static MonitoringQuality MonitoringQuality(IReadOnlyList<AnalyticsCheckFact> facts, AnalyticsMeta meta)
        => new(facts.LongCount(f => f.Status == "failed"), facts.LongCount(f => f.Status == "succeeded" && !f.AnswerPresent),
            meta.PendingAnalysisChecks, meta.FailedAnalysisChecks, meta.MissingCapturedAtChecks,
            facts.LongCount(ModelVersionUnavailable), new Dictionary<string, long>(),
            "model-health-v1", 5);

    private static IReadOnlyList<MonitoringModelView> BuildModelViews(IReadOnlyList<AnalyticsCheckFact> facts,
        SubjectConfiguration configuration, TimeZoneInfo zone, DateTimeOffset asOf, Guid siteId,
        IReadOnlyList<TargetIdentity>? targetIdentities = null)
    {
        var today = LocalDate(asOf, zone);
        var grouped = facts.GroupBy(ModelIdentity, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var identities = grouped.Select(group =>
        {
            var first = group.Value[0];
            var target = (targetIdentities ?? []).FirstOrDefault(t =>
                t.Provider == first.Provider && t.Engine == first.Engine
                && t.Country.Equals(first.Country, StringComparison.OrdinalIgnoreCase)
                && t.Locale.Equals(first.Locale, StringComparison.OrdinalIgnoreCase)
                && t.CaptureMode == first.CaptureMode);
            return (Key: group.Key, Provider: first.Provider, Engine: first.Engine,
                Country: first.Country, Locale: first.Locale, CaptureMode: first.CaptureMode,
                Enabled: target is null || target.Enabled);
        }).ToList();
        foreach (var target in targetIdentities ?? [])
            if (!facts.Any(f => f.Provider == target.Provider && f.Engine == target.Engine
                && f.Country == target.Country && f.Locale == target.Locale && f.CaptureMode == target.CaptureMode))
                identities.Add((string.Join('|', target.Provider, target.Engine, "<unavailable>", target.Country,
                    target.Locale, target.CaptureMode), target.Provider, target.Engine, target.Country,
                    target.Locale, target.CaptureMode, target.Enabled));
        return identities.Select(identity =>
        {
            var group = grouped.TryGetValue(identity.Key, out var rows) ? rows : [];
            var first = group.FirstOrDefault();
            var metrics = AnalyticsProjector.Compute(group, configuration).Metrics;
            var failedRate = group.Length == 0 ? 0 : group.Count(f => f.Status == "failed") / (double)group.Length;
            var status = !identity.Enabled ? "disabled" : group.Length == 0 ? "idle"
                : group.Any(f => f.Status is "pending" or "processing") ? "running"
                : failedRate >= .5 ? "degraded" : group.Length < 5 ? "insufficient_data" : "healthy";
            var reason = status switch
            {
                "disabled" => "TARGET_DISABLED",
                "idle" => "NO_RECENT_SAMPLES",
                "running" => "UNFINISHED_CHECKS",
                "degraded" => "FAILURE_RATE_THRESHOLD",
                "insufficient_data" => "SAMPLE_COUNT_BELOW_MINIMUM",
                _ => "HEALTHY_SAMPLE"
            };
            // 显示名与口碑平台卡片共用 PlatformIdentity.DisplayName（当前为引擎名）。
            return new MonitoringModelView(PlatformIdentity.DisplayName(first?.Provider ?? identity.Provider,
                    first?.Engine ?? identity.Engine, first?.CaptureMode ?? identity.CaptureMode),
                first?.Provider ?? identity.Provider,
                first?.Engine ?? identity.Engine, first?.ModelVersion,
                first is null || ModelVersionUnavailable(first), first?.Country ?? identity.Country,
                first?.Locale ?? identity.Locale, first?.CaptureMode ?? identity.CaptureMode,
                group.LongCount(f => f.CapturedAt is not null && LocalDate(f.CapturedAt.Value, zone) == today),
                group.LongCount(), metrics.Visibility, DashboardAnalyticsProjector.Latency(group),
                group.LongCount(f => f.Status == "succeeded"), group.LongCount(f => f.Status == "failed"),
                group.LongCount(f => f.Status == "succeeded" && !f.AnswerPresent), null,
                group.Select(f => f.CapturedAt).Max(), status, reason, "model-health-v1",
                $"/api/v1/sites/{siteId}/targets", group.LongCount());
        }).OrderBy(m => m.DisplayName, StringComparer.Ordinal).ToArray();
    }

    // prompts 没有 tenant_id 列：租户范围经 sites 约束（与其他 Prompt 查询相同）。
    // 读取的是提问当前所属分组，不受快照 asOf 约束；已删除的提问不在映射中，由投影归入「未分组」。
    internal const string PromptGroupMapSql = """
            SELECT BIN_TO_UUID(p.id), BIN_TO_UUID(g.id), g.name, g.created_at
            FROM prompts p
            JOIN sites s ON s.id = p.site_id AND s.tenant_id = UUID_TO_BIN(@tenant)
            LEFT JOIN prompt_groups g ON g.site_id = p.site_id AND g.id = p.group_id
            WHERE p.site_id = UUID_TO_BIN(@site);
            """;

    private async Task<IReadOnlyDictionary<Guid, PromptGroupAssignment>> LoadPromptGroupMapAsync(Snapshot snapshot,
        CancellationToken cancellationToken)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(PromptGroupMapSql, connection);
        command.Parameters.AddWithValue("@tenant", snapshot.TenantId.ToString());
        command.Parameters.AddWithValue("@site", snapshot.SiteId.ToString());
        var map = new Dictionary<Guid, PromptGroupAssignment>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            map[Guid.Parse(reader.GetString(0))] = reader.IsDBNull(1)
                ? new PromptGroupAssignment(null, null, null)
                : new PromptGroupAssignment(Guid.Parse(reader.GetString(1)), reader.GetString(2),
                    Utc(reader.GetDateTime(3)));
        return map;
    }

    // 国内采集目标在 site_targets 中登记为 consumer_web，但其回答落库为 scraped；口碑平台候选需按回答口径对齐，
    // 否则尚无回答的国内平台进不了 platformsWithoutData。模型监控卡片沿用原行为，不传该开关。
    internal static string AnswerCaptureMode(string targetCaptureMode)
        => targetCaptureMode == "consumer_web" ? "scraped" : targetCaptureMode;

    private async Task<IReadOnlyList<TargetIdentity>> LoadTargetIdentitiesAsync(Snapshot snapshot,
        CancellationToken cancellationToken, bool alignCaptureModeWithAnswers = false)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            SELECT provider, engine, country, locale, capture_mode, enabled
            FROM site_targets WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);
            """, connection);
        command.Parameters.AddWithValue("@tenant", snapshot.TenantId.ToString());
        command.Parameters.AddWithValue("@site", snapshot.SiteId.ToString());
        var targets = new List<TargetIdentity>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            targets.Add(new TargetIdentity(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), alignCaptureModeWithAnswers ? AnswerCaptureMode(reader.GetString(4)) : reader.GetString(4),
                reader.GetBoolean(5)));
        // 同一模型的快速 / 深度两种模式是两行站点目标，但模型卡片与口碑平台按 Provider + 引擎 + 地区计；合并为一行，任一启用即启用。
        targets = targets.GroupBy(t => t with { Enabled = false })
            .Select(g => g.First() with { Enabled = g.Any(t => t.Enabled) }).ToList();
        return targets.Where(target =>
            (snapshot.Filter.Providers.Count == 0 || snapshot.Filter.Providers.Contains(target.Provider))
            && (snapshot.Filter.Engines.Count == 0 || snapshot.Filter.Engines.Contains(target.Engine))
            && (snapshot.Filter.Countries.Count == 0 || snapshot.Filter.Countries.Contains(target.Country, StringComparer.OrdinalIgnoreCase))
            && (snapshot.Filter.Locales.Count == 0 || snapshot.Filter.Locales.Contains(target.Locale, StringComparer.OrdinalIgnoreCase))
            && (snapshot.Filter.CaptureModes.Count == 0 || snapshot.Filter.CaptureModes.Contains(target.CaptureMode)))
            .ToArray();
    }

    // Count citations by Check without expanding/sorting the potentially 1 MiB answer.
    // citations.id and analytics_check_arrivals.check_id are unique, so this preserves
    // the original distinct-row count, including multiple positions for the same URL.
    internal const string DashboardOpsSql = """
            SELECT BIN_TO_UUID(c.id), c.answer, p.locale,
                   (SELECT COUNT(*) FROM citations ci WHERE ci.check_id = c.id), NULL
            FROM visibility_checks c JOIN prompts p ON p.id = c.prompt_id
            JOIN analytics_check_arrivals a ON a.check_id = c.id AND a.id <= @arrival
            WHERE c.tenant_id = UUID_TO_BIN(@tenant) AND c.site_id = UUID_TO_BIN(@site)
              AND c.captured_at >= @from AND c.captured_at < @to;
            """;

    private async Task<IReadOnlyDictionary<Guid, DashboardOps>> LoadDashboardOpsAsync(Snapshot snapshot,
        CancellationToken cancellationToken)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(DashboardOpsSql, connection);
        command.Parameters.AddWithValue("@arrival", snapshot.ArrivalWatermark);
        command.Parameters.AddWithValue("@tenant", snapshot.TenantId.ToString());
        command.Parameters.AddWithValue("@site", snapshot.SiteId.ToString());
        command.Parameters.AddWithValue("@from", snapshot.Filter.From.UtcDateTime);
        command.Parameters.AddWithValue("@to", snapshot.Filter.To.UtcDateTime);
        var result = new Dictionary<Guid, DashboardOps>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result[Guid.Parse(reader.GetString(0))] = new DashboardOps(
                reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
                null);
        return result;
    }

    private Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, SubjectStats>>> LoadSubjectStatsAsync(
        Snapshot snapshot, CancellationToken cancellationToken)
        => LoadSubjectStatsCoreAsync(snapshot, null, cancellationToken);

    /// <summary>只读取指定 Check 的主体统计（回答记录列表按页调用）；空列表不访问数据库。</summary>
    private Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, SubjectStats>>> LoadSubjectStatsAsync(
        Snapshot snapshot, IReadOnlyList<Guid> checkIds, CancellationToken cancellationToken)
        => checkIds.Count == 0
            ? Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, SubjectStats>>>(
                new Dictionary<Guid, IReadOnlyDictionary<Guid, SubjectStats>>())
            : LoadSubjectStatsCoreAsync(snapshot, checkIds, cancellationToken);

    /// <summary>
    /// 按 Check、主体统计提及证据：首次偏移 = MIN(offset_start)（与 check_subject_presence.first_offset 的写入口径一致），
    /// 出现次数 = 证据条数。唯一键 (release_id, check_id, subject_id, offset_start, offset_end) 覆盖该查询。
    /// </summary>
    internal const string SubjectStatsSql = """
        SELECT BIN_TO_UUID(e.check_id), BIN_TO_UUID(e.subject_id), MIN(e.offset_start), COUNT(*)
        FROM mention_evidence e JOIN check_analysis a
          ON a.release_id = e.release_id AND a.check_id = e.check_id
        WHERE e.release_id = UUID_TO_BIN(@release) AND a.completed_event_id <= @watermark{0}
        GROUP BY e.check_id, e.subject_id;
        """;

    private async Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, SubjectStats>>> LoadSubjectStatsCoreAsync(
        Snapshot snapshot, IReadOnlyList<Guid>? checkIds, CancellationToken cancellationToken)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        // 参数化写法同 LoadResultQualityAsync。
        var scope = checkIds is null ? ""
            : $" AND e.check_id IN ({string.Join(",", checkIds.Select((_, i) => $"UUID_TO_BIN(@id{i})"))})";
        await using var command = new MySqlCommand(string.Format(SubjectStatsSql, scope), connection);
        command.Parameters.AddWithValue("@release", snapshot.ReleaseId.ToString());
        command.Parameters.AddWithValue("@watermark", snapshot.AnalysisWatermark);
        for (var i = 0; i < (checkIds?.Count ?? 0); i++)
            command.Parameters.AddWithValue($"@id{i}", checkIds![i].ToString());
        var result = new Dictionary<Guid, Dictionary<Guid, SubjectStats>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var check = Guid.Parse(reader.GetString(0));
            if (!result.TryGetValue(check, out var subjects)) result[check] = subjects = new();
            subjects[Guid.Parse(reader.GetString(1))] = new SubjectStats(reader.GetInt32(2),
                Convert.ToInt32(reader.GetInt64(3)));
        }
        return result.ToDictionary(e => e.Key, e => (IReadOnlyDictionary<Guid, SubjectStats>)e.Value);
    }

    private async Task<bool> HasPendingUrlNormalizationAsync(Snapshot snapshot, string urlVersion,
        CancellationToken cancellationToken)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            SELECT EXISTS(SELECT 1 FROM citation_events e JOIN visibility_checks c ON c.id=e.check_id
              WHERE c.tenant_id=UUID_TO_BIN(@tenant) AND c.site_id=UUID_TO_BIN(@site)
                AND e.release_id=UUID_TO_BIN(@release)
                AND (e.normalization_status = 'normalization_pending'
                     OR e.url_version <> @urlVersion));
            """, connection);
        command.Parameters.AddWithValue("@tenant", snapshot.TenantId.ToString());
        command.Parameters.AddWithValue("@site", snapshot.SiteId.ToString());
        command.Parameters.AddWithValue("@release", snapshot.ReleaseId.ToString());
        command.Parameters.AddWithValue("@urlVersion", urlVersion);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }
}
