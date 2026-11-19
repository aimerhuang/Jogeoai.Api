using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

public sealed partial class AnalyticsQueryStore
{
    /// <summary>快照内可见的一条情感判定事实（classified_at ≤ 快照 as_of）。</summary>
    internal sealed record StoredReputationSentiment(ReputationSentimentState State, double? Confidence,
        string RationaleCode, string ClassifierVersion, string? ModelVersion, int? EvidenceStart, int? EvidenceEnd,
        DateTimeOffset ClassifiedAt);

    /// <summary>一个快照窗口内的情感事实、重试耗尽的工作项与印象词。</summary>
    internal sealed record ReputationStates(IReadOnlyDictionary<Guid, StoredReputationSentiment> Sentiments,
        IReadOnlyDictionary<Guid, string?> FailedWork, IReadOnlyDictionary<Guid, IReadOnlyList<ReputationFactTerm>> Terms)
    {
        internal static readonly ReputationStates Empty = new(new Dictionary<Guid, StoredReputationSentiment>(),
            new Dictionary<Guid, string?>(), new Dictionary<Guid, IReadOnlyList<ReputationFactTerm>>());
    }

    /// <inheritdoc />
    public async Task<ReputationSummaryView> ReputationAsync(Guid tenantId, Guid siteId, AnalyticsFilterRequest request,
        string? snapshotToken, string? view, CancellationToken cancellationToken = default)
    {
        var parsedView = ParseReputationView(view);
        if (!await ReputationSchema.ExistsAsync(database, cancellationToken)) throw ReputationSchema.Missing();
        var data = await PrepareAsync(tenantId, siteId, request, snapshotToken, cancellationToken);
        var filter = data.Snapshot.Filter;
        var localFrom = LocalDate(filter.From, data.Zone);
        var localTo = LocalDate(filter.To.AddTicks(-1), data.Zone).AddDays(1);
        if (parsedView == ReputationView.Day && localTo.DayNumber - localFrom.DayNumber != 1)
            throw Error("INVALID_FILTER", "当日数据（view=day）的时间范围必须恰好是一个当地自然日。", 400);
        // 前一个等长窗口（当日数据即前一日），与概览环比同一定义；前期事实按快照令牌缓存。
        var previousFilter = filter with { From = filter.From - (filter.To - filter.From), To = filter.From };
        var previousFacts = await CachedPreviousFactsAsync(data, previousFilter, cancellationToken);
        var states = await LoadReputationStatesAsync(data.Snapshot, data.Configuration.BrandId, previousFilter.From,
            filter.To, null, includeTerms: true, cancellationToken);
        var current = BuildReputationFacts(data.Facts, data.Configuration, data.Zone, states);
        var previous = BuildReputationFacts(previousFacts, data.Configuration, data.Zone, states);
        var targets = await LoadTargetIdentitiesAsync(data.Snapshot, cancellationToken, alignCaptureModeWithAnswers: true);
        var candidates = targets.Where(t => t.Enabled)
            .Select(t => new ReputationPlatformCandidate(t.Provider, t.Engine, t.CaptureMode)).ToArray();
        var today = LocalDate(data.Snapshot.AsOf, data.Zone);
        var meta = new ReputationMeta(data.Snapshot.Token, data.Snapshot.AsOf, data.Snapshot.ExpiresAt, filter,
            filter.TimeZone, parsedView, localFrom, localTo, data.Snapshot.ReleaseId, data.Snapshot.AsOf,
            data.Meta.LastCapturedAt, today >= localFrom && today < localTo, ReputationRules.MinSampleSize,
            ReputationRules.PartialCoverageThreshold, ReputationRules.HealthVersion, ReputationRules.ClassifierVersion,
            ReputationRules.LevelThresholds, data.Meta.PendingAnalysisChecks, data.Meta.FailedAnalysisChecks);
        return ReputationProjector.Build(meta, current, previous, candidates, previousFilter.From, previousFilter.To);
    }

    internal static ReputationView ParseReputationView(string? view) => view?.Trim().ToLowerInvariant() switch
    {
        null or "" or "range" => ReputationView.Range,
        "day" => ReputationView.Day,
        _ => throw Error("INVALID_FILTER", "view 仅支持 range 或 day。", 400)
    };

    /// <summary>回答列表的口碑状态筛选；空白表示不筛选，非法值返回 INVALID_FILTER。</summary>
    internal static ReputationSentimentState? NormalizeReputationSentiment(string? sentiment)
    {
        if (string.IsNullOrWhiteSpace(sentiment)) return null;
        return ReputationRules.ParseState(sentiment)
            ?? throw Error("INVALID_FILTER",
                "sentiment 仅支持 positive、neutral、negative、unknown、not_mentioned、pending、failed 或 not_applicable。", 400);
    }

    /// <summary>回答列表的印象词筛选：与印象词相同的规范化词键；空白表示不筛选，超过 64 字符返回 INVALID_FILTER。</summary>
    internal static string? NormalizeReputationTerm(string? term)
    {
        if (string.IsNullOrWhiteSpace(term)) return null;
        var key = ReputationRules.NormalizeTerm(term);
        if (key.Length is 0 or > 64) throw Error("INVALID_FILTER", "term 规范化后必须为 1–64 个字符。", 400);
        return key;
    }

    /// <summary>回答列表的词极性筛选：空白表示不筛选；取值须为 positive、neutral、negative，且只能与 term 一起使用，否则返回 INVALID_FILTER。</summary>
    internal static ReputationTermPolarity? NormalizeReputationTermPolarity(string? termPolarity, string? termKey)
    {
        if (string.IsNullOrWhiteSpace(termPolarity)) return null;
        var polarity = ReputationRules.ParsePolarity(termPolarity)
            ?? throw Error("INVALID_FILTER", "termPolarity 仅支持 positive、neutral 或 negative。", 400);
        if (termKey is null) throw Error("INVALID_FILTER", "termPolarity 只能与 term 一起使用。", 400);
        return polarity;
    }

    /// <summary>
    /// 由 Check 事实与情感事实得到口碑事实；有效回答资格与 AnalyticsProjector 完全一致，是否提及本品牌只看现有提及分析。
    /// 聚合与回答列表都调用本函数，因此任一计数都可以在回答列表中逐条复核。
    /// </summary>
    internal static IReadOnlyList<ReputationFact> BuildReputationFacts(IReadOnlyList<AnalyticsCheckFact> facts,
        SubjectConfiguration configuration, TimeZoneInfo zone, ReputationStates states)
    {
        var result = new ReputationFact[facts.Count];
        for (var i = 0; i < facts.Count; i++)
        {
            var fact = facts[i];
            var (state, stored, failedCode) = ResolveReputation(fact, configuration.BrandId, states);
            var code = state switch
            {
                ReputationSentimentState.Failed => failedCode,
                ReputationSentimentState.Unknown when stored?.State == ReputationSentimentState.NotMentioned
                    => ReputationRules.MentionDisagree,
                _ when stored is not null && state == stored.State => stored.RationaleCode,
                _ => null
            };
            IReadOnlyList<ReputationFactTerm> terms = ReputationRules.IsClassified(state)
                && states.Terms.TryGetValue(fact.CheckId, out var found) ? found : [];
            result[i] = new ReputationFact(fact.CheckId, fact.PromptId, fact.PromptSnapshot ?? "", fact.CapturedAt,
                fact.CapturedAt is null ? null : LocalDate(fact.CapturedAt.Value, zone), fact.Provider, fact.Engine,
                fact.CaptureMode, state, code, terms);
        }
        return result;
    }

    private static (ReputationSentimentState State, StoredReputationSentiment? Stored, string? FailedCode) ResolveReputation(
        AnalyticsCheckFact fact, Guid brandId, ReputationStates states)
    {
        var eligible = fact.Status == "succeeded" && fact.AnswerPresent && fact.AnalysisStatus == "completed"
            && !fact.ConfigurationMissing;
        states.Sentiments.TryGetValue(fact.CheckId, out var stored);
        var failed = states.FailedWork.TryGetValue(fact.CheckId, out var failedCode);
        return (ReputationRules.ResolveState(eligible, fact.MentionedSubjects.Contains(brandId), stored?.State, failed),
            stored, failedCode);
    }

    /// <summary>回答列表行的情感判定：与聚合同一状态归并。</summary>
    internal static ReputationSentimentView RecordReputationSentiment(AnalyticsCheckFact fact, Guid brandId,
        ReputationStates states)
    {
        var (state, stored, failedCode) = ResolveReputation(fact, brandId, states);
        return ReputationSentimentView(state, stored, failedCode);
    }

    /// <summary>回答列表行与详情中的情感判定视图；只有当前状态来自判定事实时才附带置信度、版本与依据偏移。</summary>
    internal static ReputationSentimentView ReputationSentimentView(ReputationSentimentState state,
        StoredReputationSentiment? stored, string? failedCode)
    {
        var fromStored = stored is not null && (stored.State == state
            || state == ReputationSentimentState.Unknown && stored.State == ReputationSentimentState.NotMentioned);
        if (!fromStored)
            return new ReputationSentimentView(state, null, state == ReputationSentimentState.Failed ? failedCode : null,
                null, null, null, null, null);
        return new ReputationSentimentView(state, stored!.Confidence,
            stored.State == state ? stored.RationaleCode : ReputationRules.MentionDisagree, stored.ClassifierVersion,
            stored.ModelVersion, stored.EvidenceStart, stored.EvidenceEnd, stored.ClassifiedAt);
    }

    /// <summary>
    /// 读取快照绑定版本中的情感事实：只计 classified_at ≤ as_of 的判定与 updated_at ≤ as_of 的重试耗尽工作项；
    /// <paramref name="from"/> / <paramref name="to"/> 按采集时间限定窗口，<paramref name="checkIds"/> 不为空时只读这些 Check。
    /// </summary>
    private async Task<ReputationStates> LoadReputationStatesAsync(Snapshot snapshot, Guid brandId,
        DateTimeOffset? from, DateTimeOffset? to, IReadOnlyList<Guid>? checkIds, bool includeTerms,
        CancellationToken cancellationToken)
    {
        if (checkIds is { Count: 0 }) return ReputationStates.Empty;
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var window = (from is null ? "" : " AND c.captured_at >= @from") + (to is null ? "" : " AND c.captured_at < @to")
            + (checkIds is null ? "" : $" AND c.id IN ({string.Join(",", checkIds.Select((_, i) => $"UUID_TO_BIN(@id{i})"))})");
        void Bind(MySqlCommand command)
        {
            command.Parameters.AddWithValue("@tenant", snapshot.TenantId.ToString());
            command.Parameters.AddWithValue("@site", snapshot.SiteId.ToString());
            command.Parameters.AddWithValue("@release", snapshot.ReleaseId.ToString());
            command.Parameters.AddWithValue("@brand", brandId.ToString());
            command.Parameters.AddWithValue("@asOf", snapshot.AsOf.UtcDateTime);
            if (from is not null) command.Parameters.AddWithValue("@from", from.Value.UtcDateTime);
            if (to is not null) command.Parameters.AddWithValue("@to", to.Value.UtcDateTime);
            for (var i = 0; i < (checkIds?.Count ?? 0); i++) command.Parameters.AddWithValue($"@id{i}", checkIds![i].ToString());
        }

        var sentiments = new Dictionary<Guid, StoredReputationSentiment>();
        await using (var command = new MySqlCommand($"""
            SELECT BIN_TO_UUID(s.check_id), s.state, s.confidence, s.rationale_code, s.classifier_version, s.model_version,
                   s.evidence_start, s.evidence_end, s.classified_at
            FROM reputation_check_sentiments s
            JOIN visibility_checks c ON c.id = s.check_id AND c.tenant_id = s.tenant_id AND c.site_id = s.site_id
            WHERE s.tenant_id = UUID_TO_BIN(@tenant) AND s.site_id = UUID_TO_BIN(@site)
              AND s.release_id = UUID_TO_BIN(@release) AND s.brand_subject_id = UUID_TO_BIN(@brand)
              AND s.classified_at <= @asOf{window};
            """, connection))
        {
            Bind(command);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (ReputationRules.ParseState(reader.GetString(1)) is not { } state) continue;
                sentiments[Guid.Parse(reader.GetString(0))] = new StoredReputationSentiment(state,
                    reader.IsDBNull(2) ? null : (double)reader.GetDecimal(2), reader.GetString(3), reader.GetString(4),
                    NullString(reader, 5), reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    reader.IsDBNull(7) ? null : reader.GetInt32(7), Utc(reader.GetDateTime(8)));
            }
        }
        var failed = new Dictionary<Guid, string?>();
        await using (var command = new MySqlCommand($"""
            SELECT BIN_TO_UUID(w.check_id), w.error_code
            FROM reputation_check_work w
            JOIN visibility_checks c ON c.id = w.check_id AND c.tenant_id = w.tenant_id AND c.site_id = w.site_id
            WHERE w.tenant_id = UUID_TO_BIN(@tenant) AND w.site_id = UUID_TO_BIN(@site)
              AND w.release_id = UUID_TO_BIN(@release) AND w.status = 'failed' AND w.updated_at <= @asOf{window};
            """, connection))
        {
            Bind(command);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                failed[Guid.Parse(reader.GetString(0))] = NullString(reader, 1);
        }
        var terms = new Dictionary<Guid, IReadOnlyList<ReputationFactTerm>>();
        if (includeTerms)
        {
            await using var command = new MySqlCommand($"""
                SELECT BIN_TO_UUID(t.check_id), t.term_key, t.term_text, t.polarity, t.occurrence_count
                FROM reputation_term_facts t
                JOIN reputation_check_sentiments s ON s.release_id = t.release_id AND s.check_id = t.check_id
                  AND s.brand_subject_id = t.brand_subject_id
                JOIN visibility_checks c ON c.id = t.check_id AND c.tenant_id = t.tenant_id AND c.site_id = t.site_id
                WHERE t.tenant_id = UUID_TO_BIN(@tenant) AND t.site_id = UUID_TO_BIN(@site)
                  AND t.release_id = UUID_TO_BIN(@release) AND t.brand_subject_id = UUID_TO_BIN(@brand)
                  AND s.classified_at <= @asOf{window}
                ORDER BY t.check_id, t.term_hash, t.polarity;
                """, connection);
            Bind(command);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (ReputationRules.ParsePolarity(reader.GetString(3)) is not { } polarity) continue;
                var check = Guid.Parse(reader.GetString(0));
                if (!terms.TryGetValue(check, out var list)) terms[check] = list = new List<ReputationFactTerm>();
                ((List<ReputationFactTerm>)list).Add(new ReputationFactTerm(reader.GetString(1), reader.GetString(2),
                    polarity, reader.GetInt32(4)));
            }
        }
        return new ReputationStates(sentiments, failed, terms);
    }

    /// <summary>为回答详情计算情感判定；038 未执行时返回空，不影响详情其余内容。</summary>
    private async Task<ReputationSentimentView?> DetailReputationSentimentAsync(Snapshot snapshot,
        SubjectConfiguration configuration, Guid checkId, string mentionState, CancellationToken cancellationToken)
    {
        if (!await ReputationSchema.ExistsAsync(database, cancellationToken)) return null;
        var eligible = mentionState is "mentioned" or "not_mentioned";
        var states = eligible
            ? await LoadReputationStatesAsync(snapshot, configuration.BrandId, null, null, [checkId], false, cancellationToken)
            : ReputationStates.Empty;
        states.Sentiments.TryGetValue(checkId, out var stored);
        var isFailed = states.FailedWork.TryGetValue(checkId, out var failedCode);
        var state = ReputationRules.ResolveState(eligible, mentionState == "mentioned", stored?.State, isFailed);
        return ReputationSentimentView(state, stored, failedCode);
    }
}
