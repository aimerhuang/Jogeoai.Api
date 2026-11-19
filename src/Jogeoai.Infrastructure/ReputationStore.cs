using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jogeoai.Application;
using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

/// <summary>
/// 口碑情感工作项、判定事实与补录任务的 MySQL 实现。只读写 038 新增的四张表；对旧表只做一致性读取与 site_analysis_state 栅栏加锁，
/// 不修改采集、提及分析或发布状态。
/// </summary>
public sealed class ReputationStore(MySqlDatabase database) : IReputationStore
{
    private sealed record ClaimRow(Guid ReleaseId, Guid CheckId, Guid TenantId, Guid SiteId, int LeaseVersion,
        int AttemptCount, int ConfigVersion, string SubjectSnapshot, string Answer, bool BrandMentioned);

    /// <inheritdoc />
    public Task<bool> SchemaReadyAsync(CancellationToken cancellationToken = default)
        => ReputationSchema.ExistsAsync(database, cancellationToken);

    // 只登记有效回答：采集成功、回答非空、提及分析完成且配置完整；分析版本为当前已发布版本或构建中版本。
    internal const string EligibleAnalysisSql = """
        FROM check_analysis a
        JOIN analysis_releases r ON r.id = a.release_id AND r.site_id = a.site_id
        JOIN site_analysis_state s ON s.site_id = a.site_id
        JOIN visibility_checks c ON c.id = a.check_id AND c.tenant_id = a.tenant_id AND c.site_id = a.site_id
        WHERE a.status = 'completed' AND a.configuration_missing = FALSE
          AND (r.id = s.published_release_id OR r.status = 'building')
          AND c.status = 'succeeded' AND COALESCE(c.answer_present, c.answer IS NOT NULL AND TRIM(c.answer) <> '')
          AND NOT EXISTS (SELECT 1 FROM reputation_check_work w WHERE w.release_id = a.release_id AND w.check_id = a.check_id)
        """;

    /// <inheritdoc />
    public async Task<int> EnqueueRecentAsync(DateTimeOffset completedSince, int limit, int maxAttempts,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 5000) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        // READ COMMITTED：INSERT ... SELECT 对来源表做一致性读，不加锁，不阻塞提及分析写入。
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await using (var expire = new MySqlCommand(ExpireExhaustedSql, connection, transaction))
        {
            expire.Parameters.AddWithValue("@max", maxAttempts);
            await expire.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var insert = new MySqlCommand($"""
            INSERT IGNORE INTO reputation_check_work (release_id, check_id, tenant_id, site_id, status, created_at, updated_at)
            SELECT a.release_id, a.check_id, a.tenant_id, a.site_id, 'pending', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)
            {EligibleAnalysisSql}
              AND a.completed_at >= @since
            ORDER BY a.completed_at, a.check_id
            LIMIT @limit;
            """, connection, transaction);
        insert.Parameters.AddWithValue("@since", completedSince.UtcDateTime);
        insert.Parameters.AddWithValue("@limit", limit);
        var inserted = await insert.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return inserted;
    }

    // 尝试次数已达上限、不会再被领取的工作项记为 failed 终态：租约过期的 processing（Worker 在最后一次尝试中退出），
    // 以及上限调低后遗留的 retry。否则它们会永久显示为待判定。
    // MySQL 单表 UPDATE 按从左到右使用已赋的新值，error_code 必须在 status 之前赋值。
    private const string ExpireExhaustedSql = """
        UPDATE reputation_check_work
        SET error_code = CASE WHEN status = 'processing' THEN 'LEASE_EXPIRED' ELSE COALESCE(error_code, 'RETRY_EXHAUSTED') END,
            status = 'failed', lease_owner = NULL, lease_until = NULL, updated_at = UTC_TIMESTAMP(6)
        WHERE attempt_count >= @max
          AND ((status = 'processing' AND lease_until < UTC_TIMESTAMP(6)) OR status = 'retry');
        """;

    /// <inheritdoc />
    public async Task<int> ExpireExhaustedAsync(int maxAttempts, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(ExpireExhaustedSql, connection);
        command.Parameters.AddWithValue("@max", maxAttempts);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ReputationWorkLease?> ClaimAsync(string workerId, bool includeMentioned, TimeSpan lease,
        int maxAttempts, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workerId) || workerId.Length > 128) throw new ArgumentException("口碑 Worker 标识无效。");
        ClaimRow? row = null;
        await using (var connection = database.CreateConnection())
        {
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            var mentionedFilter = includeMentioned ? "" : """
                  AND NOT EXISTS (SELECT 1 FROM check_subject_presence p
                                  WHERE p.release_id = w.release_id AND p.check_id = w.check_id AND p.subject_type = 'brand')
                """;
            // FOR UPDATE OF w：只锁口碑工作项，不锁 analysis_releases / visibility_checks，避免与提及分析领取互相跳过。
            await using var select = new MySqlCommand($"""
                SELECT BIN_TO_UUID(w.release_id), BIN_TO_UUID(w.check_id), BIN_TO_UUID(w.tenant_id), BIN_TO_UUID(w.site_id),
                       w.lease_version, w.attempt_count, r.subject_config_version, r.subject_snapshot, COALESCE(c.answer, ''),
                       EXISTS (SELECT 1 FROM check_subject_presence p
                               WHERE p.release_id = w.release_id AND p.check_id = w.check_id AND p.subject_type = 'brand')
                FROM reputation_check_work w
                JOIN analysis_releases r ON r.id = w.release_id AND r.site_id = w.site_id
                JOIN visibility_checks c ON c.id = w.check_id AND c.tenant_id = w.tenant_id AND c.site_id = w.site_id
                WHERE (w.status = 'pending'
                       OR (w.status = 'retry' AND w.attempt_count < @max AND w.updated_at < UTC_TIMESTAMP(6) - INTERVAL 30 SECOND)
                       OR (w.status = 'processing' AND w.lease_until < UTC_TIMESTAMP(6) AND w.attempt_count < @max))
                {mentionedFilter}
                ORDER BY w.created_at, w.check_id LIMIT 1 FOR UPDATE OF w SKIP LOCKED;
                """, connection, transaction);
            select.Parameters.AddWithValue("@max", maxAttempts);
            await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
            {
                if (await reader.ReadAsync(cancellationToken))
                    row = new ClaimRow(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                        Guid.Parse(reader.GetString(2)), Guid.Parse(reader.GetString(3)), reader.GetInt32(4) + 1,
                        reader.GetInt32(5) + 1, reader.GetInt32(6), reader.GetString(7), reader.GetString(8),
                        reader.GetBoolean(9));
            }
            if (row is null) return null;
            await using var update = new MySqlCommand("""
                UPDATE reputation_check_work SET status = 'processing', lease_owner = @worker, lease_version = @version,
                    lease_until = UTC_TIMESTAMP(6) + INTERVAL @seconds SECOND, attempt_count = attempt_count + 1,
                    error_code = NULL, updated_at = UTC_TIMESTAMP(6)
                WHERE release_id = UUID_TO_BIN(@release) AND check_id = UUID_TO_BIN(@check);
                """, connection, transaction);
            update.Parameters.AddWithValue("@worker", workerId);
            update.Parameters.AddWithValue("@version", row.LeaseVersion);
            update.Parameters.AddWithValue("@seconds", Math.Max(10, (int)lease.TotalSeconds));
            update.Parameters.AddWithValue("@release", row.ReleaseId.ToString());
            update.Parameters.AddWithValue("@check", row.CheckId.ToString());
            await update.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        var configuration = SubjectConfiguration.Parse(row.SiteId, row.ConfigVersion, row.SubjectSnapshot);
        var brand = configuration.Subjects.FirstOrDefault(s => s.Type == "brand");
        var brandTerms = configuration.Terms.Where(t => t.SubjectId == configuration.BrandId).ToArray();
        var aliases = brandTerms.Where(t => !t.Kind.Contains("domain", StringComparison.Ordinal)).Select(t => t.Key)
            .Distinct(StringComparer.Ordinal).ToArray();
        var excluded = brandTerms.Where(t => t.Kind.Contains("domain", StringComparison.Ordinal)).Select(t => t.Key)
            .Concat(configuration.Subjects.Where(s => s.Type == "competitor").Select(s => s.Name))
            .Concat(configuration.Terms.Where(t => t.SubjectId != configuration.BrandId).Select(t => t.Key))
            .Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.Ordinal).ToArray();
        var mentioned = row.BrandMentioned && brand is not null;
        var snippets = mentioned ? await LoadMentionSnippetsAsync(row, configuration.BrandId, cancellationToken) : [];
        return new ReputationWorkLease(row.ReleaseId, row.CheckId, row.TenantId, row.SiteId, row.LeaseVersion,
            row.AttemptCount, configuration.BrandId, brand?.Name ?? "", aliases, excluded, row.Answer, mentioned, snippets);
    }

    private async Task<IReadOnlyList<string>> LoadMentionSnippetsAsync(ClaimRow row, Guid brandId,
        CancellationToken cancellationToken)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            SELECT snippet FROM mention_evidence
            WHERE release_id = UUID_TO_BIN(@release) AND check_id = UUID_TO_BIN(@check) AND subject_id = UUID_TO_BIN(@brand)
            ORDER BY offset_start LIMIT 5;
            """, connection);
        command.Parameters.AddWithValue("@release", row.ReleaseId.ToString());
        command.Parameters.AddWithValue("@check", row.CheckId.ToString());
        command.Parameters.AddWithValue("@brand", brandId.ToString());
        var snippets = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) snippets.Add(reader.GetString(0));
        return snippets;
    }

    /// <inheritdoc />
    public async Task<bool> TryReuseAsync(ReputationWorkLease lease, string workerId, string inputHash,
        CancellationToken cancellationToken = default)
    {
        ReputationClassification? reused = null;
        Guid sourceRelease;
        await using (var connection = database.CreateConnection())
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new MySqlCommand("""
                SELECT BIN_TO_UUID(release_id), state, confidence, rationale_code, model_version, evidence_start, evidence_end
                FROM reputation_check_sentiments
                WHERE check_id = UUID_TO_BIN(@check) AND brand_subject_id = UUID_TO_BIN(@brand) AND input_hash = @hash
                  AND classifier_version = @version AND release_id <> UUID_TO_BIN(@release)
                  AND tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site)
                  AND state IN ('positive','neutral','negative','unknown') AND rationale_code NOT IN (@invalid, @evidence)
                ORDER BY classified_at DESC LIMIT 1;
                """, connection);
            command.Parameters.AddWithValue("@check", lease.CheckId.ToString());
            command.Parameters.AddWithValue("@brand", lease.BrandSubjectId.ToString());
            command.Parameters.AddWithValue("@hash", inputHash);
            command.Parameters.AddWithValue("@version", ReputationRules.ClassifierVersion);
            command.Parameters.AddWithValue("@release", lease.ReleaseId.ToString());
            command.Parameters.AddWithValue("@tenant", lease.TenantId.ToString());
            command.Parameters.AddWithValue("@site", lease.SiteId.ToString());
            command.Parameters.AddWithValue("@invalid", ReputationRules.InvalidOutput);
            // 依据未找到的 unknown 依赖当时的匹配规则（2026-10-30 起格式容错），不复用，换版本时重新判定。
            command.Parameters.AddWithValue("@evidence", ReputationRules.EvidenceNotFound);
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken)) return false;
                sourceRelease = Guid.Parse(reader.GetString(0));
                reused = new ReputationClassification(ReputationRules.ParseState(reader.GetString(1))!.Value,
                    reader.IsDBNull(2) ? null : (double)reader.GetDecimal(2), reader.GetString(3),
                    reader.IsDBNull(5) ? null : reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetInt32(6), [],
                    ReputationRules.ClassifierVersion, reader.IsDBNull(4) ? null : reader.GetString(4));
            }
            var terms = new List<ReputationFactTerm>();
            await using (var termCommand = new MySqlCommand("""
                SELECT term_key, term_text, polarity, occurrence_count FROM reputation_term_facts
                WHERE release_id = UUID_TO_BIN(@source) AND check_id = UUID_TO_BIN(@check) AND brand_subject_id = UUID_TO_BIN(@brand)
                ORDER BY term_hash, polarity;
                """, connection))
            {
                termCommand.Parameters.AddWithValue("@source", sourceRelease.ToString());
                termCommand.Parameters.AddWithValue("@check", lease.CheckId.ToString());
                termCommand.Parameters.AddWithValue("@brand", lease.BrandSubjectId.ToString());
                await using var reader = await termCommand.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    if (ReputationRules.ParsePolarity(reader.GetString(2)) is { } polarity)
                        terms.Add(new ReputationFactTerm(reader.GetString(0), reader.GetString(1), polarity, reader.GetInt32(3)));
            }
            reused = reused with { Terms = terms };
        }
        return await CompleteAsync(lease, workerId, reused, inputHash, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> CompleteAsync(ReputationWorkLease lease, string workerId,
        ReputationClassification classification, string inputHash, CancellationToken cancellationToken = default)
    {
        if (classification.State is ReputationSentimentState.Pending or ReputationSentimentState.Failed
            or ReputationSentimentState.NotApplicable)
            throw new ArgumentException("判定事实只能是 positive、neutral、negative、unknown 或 not_mentioned。");
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        // 与快照创建、提及分析完成共用的栅栏：classified_at 在持锁后取值，快照 as_of 与判定写入有确定先后。
        await using (var fence = new MySqlCommand(
            "SELECT site_id FROM site_analysis_state WHERE site_id = UUID_TO_BIN(@site) FOR UPDATE;", connection, transaction))
        {
            fence.Parameters.AddWithValue("@site", lease.SiteId.ToString());
            if (await fence.ExecuteScalarAsync(cancellationToken) is null)
                throw new InvalidOperationException("分析工作区不存在。");
        }
        await using (var guard = new MySqlCommand("""
            SELECT lease_version, lease_owner, status FROM reputation_check_work
            WHERE release_id = UUID_TO_BIN(@release) AND check_id = UUID_TO_BIN(@check) FOR UPDATE;
            """, connection, transaction))
        {
            AddKeys(guard, lease);
            await using var reader = await guard.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || reader.GetInt32(0) != lease.LeaseVersion
                || reader.IsDBNull(1) || reader.GetString(1) != workerId || reader.GetString(2) != "processing")
                return false;
        }
        await using (var deleteTerms = new MySqlCommand("""
            DELETE FROM reputation_term_facts
            WHERE release_id = UUID_TO_BIN(@release) AND check_id = UUID_TO_BIN(@check) AND brand_subject_id = UUID_TO_BIN(@brand);
            """, connection, transaction))
        {
            AddKeys(deleteTerms, lease);
            await deleteTerms.ExecuteNonQueryAsync(cancellationToken);
        }
        // 039 已执行时同时写入 unknown 的模型原始依据片段；未执行时不写该列，判定照常完成。
        var hasQuoteColumn = await ReputationSchema.ModelQuoteColumnExistsAsync(connection, transaction, cancellationToken);
        await using (var upsert = new MySqlCommand(hasQuoteColumn ? """
            INSERT INTO reputation_check_sentiments (release_id, check_id, brand_subject_id, tenant_id, site_id, state,
                confidence, rationale_code, classifier_version, model_version, evidence_start, evidence_end, model_quote,
                input_hash, classified_at)
            VALUES (UUID_TO_BIN(@release), UUID_TO_BIN(@check), UUID_TO_BIN(@brand), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site),
                @state, @confidence, @rationale, @classifier, @model, @start, @end, @quote, @hash, UTC_TIMESTAMP(6)) AS incoming
            ON DUPLICATE KEY UPDATE state = incoming.state, confidence = incoming.confidence,
                rationale_code = incoming.rationale_code, classifier_version = incoming.classifier_version,
                model_version = incoming.model_version, evidence_start = incoming.evidence_start,
                evidence_end = incoming.evidence_end, model_quote = incoming.model_quote,
                input_hash = incoming.input_hash, classified_at = incoming.classified_at;
            """ : """
            INSERT INTO reputation_check_sentiments (release_id, check_id, brand_subject_id, tenant_id, site_id, state,
                confidence, rationale_code, classifier_version, model_version, evidence_start, evidence_end, input_hash,
                classified_at)
            VALUES (UUID_TO_BIN(@release), UUID_TO_BIN(@check), UUID_TO_BIN(@brand), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site),
                @state, @confidence, @rationale, @classifier, @model, @start, @end, @hash, UTC_TIMESTAMP(6)) AS incoming
            ON DUPLICATE KEY UPDATE state = incoming.state, confidence = incoming.confidence,
                rationale_code = incoming.rationale_code, classifier_version = incoming.classifier_version,
                model_version = incoming.model_version, evidence_start = incoming.evidence_start,
                evidence_end = incoming.evidence_end, input_hash = incoming.input_hash, classified_at = incoming.classified_at;
            """, connection, transaction))
        {
            AddKeys(upsert, lease);
            upsert.Parameters.AddWithValue("@state", ReputationRules.StateName(classification.State));
            upsert.Parameters.AddWithValue("@confidence", classification.Confidence is { } c
                ? Math.Round((decimal)c, 4, MidpointRounding.AwayFromZero) : DBNull.Value);
            upsert.Parameters.AddWithValue("@rationale", Limit(classification.RationaleCode, 64));
            upsert.Parameters.AddWithValue("@classifier", Limit(classification.ClassifierVersion, 64));
            upsert.Parameters.AddWithValue("@model", classification.ModelVersion is null ? DBNull.Value : Limit(classification.ModelVersion, 128));
            var hasEvidence = classification.EvidenceStart is >= 0 && classification.EvidenceEnd > classification.EvidenceStart;
            upsert.Parameters.AddWithValue("@start", hasEvidence ? classification.EvidenceStart!.Value : DBNull.Value);
            upsert.Parameters.AddWithValue("@end", hasEvidence ? classification.EvidenceEnd!.Value : DBNull.Value);
            upsert.Parameters.AddWithValue("@hash", inputHash);
            if (hasQuoteColumn)
                upsert.Parameters.AddWithValue("@quote", classification.State == ReputationSentimentState.Unknown
                    && classification.ModelQuote is { Length: > 0 } quote ? Limit(quote, ReputationRules.MaxModelQuoteLength) : DBNull.Value);
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }
        // 只有已判定（正 / 中 / 负）的回答保留印象词，聚合与回答列表 term 筛选因此口径一致。
        if (ReputationRules.IsClassified(classification.State))
        {
            foreach (var term in classification.Terms.Where(t => t.TermKey.Length is > 0 and <= 64
                         && t.TermText.Length is > 0 and <= 64 && t.Occurrences > 0)
                         .DistinctBy(t => (t.TermKey, t.Polarity)))
            {
                await using var insertTerm = new MySqlCommand("""
                    INSERT INTO reputation_term_facts (release_id, check_id, brand_subject_id, tenant_id, site_id,
                        term_hash, term_key, term_text, polarity, occurrence_count)
                    VALUES (UUID_TO_BIN(@release), UUID_TO_BIN(@check), UUID_TO_BIN(@brand), UUID_TO_BIN(@tenant),
                        UUID_TO_BIN(@site), @hash, @key, @text, @polarity, @count);
                    """, connection, transaction);
                AddKeys(insertTerm, lease);
                insertTerm.Parameters.AddWithValue("@hash", ReputationRules.TermHash(term.TermKey));
                insertTerm.Parameters.AddWithValue("@key", term.TermKey);
                insertTerm.Parameters.AddWithValue("@text", term.TermText);
                insertTerm.Parameters.AddWithValue("@polarity", ReputationRules.PolarityName(term.Polarity));
                insertTerm.Parameters.AddWithValue("@count", term.Occurrences);
                await insertTerm.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        await using (var done = new MySqlCommand("""
            UPDATE reputation_check_work SET status = 'completed', lease_owner = NULL, lease_until = NULL,
                error_code = NULL, updated_at = UTC_TIMESTAMP(6)
            WHERE release_id = UUID_TO_BIN(@release) AND check_id = UUID_TO_BIN(@check);
            """, connection, transaction))
        {
            AddKeys(done, lease);
            await done.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(ReputationWorkLease lease, string workerId, string errorCode,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            UPDATE reputation_check_work SET status = 'pending', error_code = @code,
                attempt_count = GREATEST(CAST(attempt_count AS SIGNED) - 1, 0), lease_owner = NULL, lease_until = NULL,
                updated_at = UTC_TIMESTAMP(6)
            WHERE release_id = UUID_TO_BIN(@release) AND check_id = UUID_TO_BIN(@check)
              AND lease_owner = @worker AND lease_version = @version AND status = 'processing';
            """, connection);
        AddKeys(command, lease);
        command.Parameters.AddWithValue("@code", Limit(errorCode, 64));
        command.Parameters.AddWithValue("@worker", workerId);
        command.Parameters.AddWithValue("@version", lease.LeaseVersion);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task FailAsync(ReputationWorkLease lease, string workerId, string errorCode, bool terminal,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            UPDATE reputation_check_work SET status = @status, error_code = @code, lease_owner = NULL, lease_until = NULL,
                updated_at = UTC_TIMESTAMP(6)
            WHERE release_id = UUID_TO_BIN(@release) AND check_id = UUID_TO_BIN(@check)
              AND lease_owner = @worker AND lease_version = @version AND status = 'processing';
            """, connection);
        AddKeys(command, lease);
        command.Parameters.AddWithValue("@status", terminal ? "failed" : "retry");
        command.Parameters.AddWithValue("@code", Limit(errorCode, 64));
        command.Parameters.AddWithValue("@worker", workerId);
        command.Parameters.AddWithValue("@version", lease.LeaseVersion);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ReputationJobView> CreateJobAsync(Guid tenantId, Guid siteId, string idempotencyKey,
        ReputationJobRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 191)
            throw Error("IDEMPOTENCY_KEY_REQUIRED", "请提供长度不超过 191 的 Idempotency-Key。", 400);
        if (request.From is not null && request.To is not null && request.From >= request.To)
            throw Error("INVALID_TIME_RANGE", "from 必须早于 to。", 400);
        if (!await SchemaReadyAsync(cancellationToken)) throw ReputationSchema.Missing();
        var from = request.From?.ToUniversalTime();
        var to = request.To?.ToUniversalTime();
        var scopeJson = JsonSerializer.Serialize(new { siteId, from, to });
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scopeJson))).ToLowerInvariant();
        Guid jobId;
        await using (var connection = database.CreateConnection())
        {
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            await using (var site = new MySqlCommand(
                "SELECT id FROM sites WHERE tenant_id = UUID_TO_BIN(@tenant) AND id = UUID_TO_BIN(@site);", connection, transaction))
            {
                site.Parameters.AddWithValue("@tenant", tenantId.ToString());
                site.Parameters.AddWithValue("@site", siteId.ToString());
                if (await site.ExecuteScalarAsync(cancellationToken) is null)
                    throw Error("RESOURCE_NOT_FOUND", "工作区不存在。", 404);
            }
            await using (var prior = new MySqlCommand("""
                SELECT BIN_TO_UUID(id), request_hash FROM reputation_analysis_jobs
                WHERE tenant_id = UUID_TO_BIN(@tenant) AND idempotency_key = @key;
                """, connection, transaction))
            {
                prior.Parameters.AddWithValue("@tenant", tenantId.ToString());
                prior.Parameters.AddWithValue("@key", idempotencyKey);
                await using var reader = await prior.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    if (reader.GetString(1) != hash)
                        throw Error("REPUTATION_IDEMPOTENCY_CONFLICT", "幂等键已用于不同的口碑补录请求。", 409);
                    var existing = Guid.Parse(reader.GetString(0));
                    await reader.DisposeAsync();
                    await transaction.CommitAsync(cancellationToken);
                    return (await GetJobAsync(tenantId, siteId, existing, cancellationToken))
                        ?? throw Error("RESOURCE_NOT_FOUND", "口碑补录任务不存在。", 404);
                }
            }
            Guid releaseId;
            await using (var state = new MySqlCommand("""
                SELECT BIN_TO_UUID(published_release_id) FROM site_analysis_state
                WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);
                """, connection, transaction))
            {
                state.Parameters.AddWithValue("@tenant", tenantId.ToString());
                state.Parameters.AddWithValue("@site", siteId.ToString());
                if (await state.ExecuteScalarAsync(cancellationToken) is not string release)
                    throw Error("ANALYSIS_VERSION_NOT_READY", "该工作区尚无已发布的提及分析版本。", 409);
                releaseId = Guid.Parse(release);
            }
            jobId = Guid.NewGuid();
            var scope = (from is null ? "" : " AND c.captured_at >= @from") + (to is null ? "" : " AND c.captured_at < @to");
            await using var insert = new MySqlCommand($"""
                INSERT INTO reputation_analysis_jobs (id, tenant_id, site_id, release_id, kind, idempotency_key,
                    request_hash, scope_json, total_checks, created_at, updated_at)
                VALUES (UUID_TO_BIN(@job), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), UUID_TO_BIN(@release),
                    'manual_backfill', @key, @hash, @scope, 0, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                INSERT IGNORE INTO reputation_check_work (release_id, check_id, tenant_id, site_id, status, created_at, updated_at)
                SELECT a.release_id, a.check_id, a.tenant_id, a.site_id, 'pending', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)
                {EligibleAnalysisSql}
                  AND a.release_id = UUID_TO_BIN(@release) AND a.tenant_id = UUID_TO_BIN(@tenant)
                  AND a.site_id = UUID_TO_BIN(@site){scope};
                UPDATE reputation_analysis_jobs SET total_checks = (
                    SELECT COUNT(*) FROM reputation_check_work w JOIN visibility_checks c ON c.id = w.check_id
                    WHERE w.release_id = UUID_TO_BIN(@release) AND w.site_id = UUID_TO_BIN(@site){scope})
                WHERE id = UUID_TO_BIN(@job);
                """, connection, transaction);
            insert.Parameters.AddWithValue("@job", jobId.ToString());
            insert.Parameters.AddWithValue("@tenant", tenantId.ToString());
            insert.Parameters.AddWithValue("@site", siteId.ToString());
            insert.Parameters.AddWithValue("@release", releaseId.ToString());
            insert.Parameters.AddWithValue("@key", idempotencyKey);
            insert.Parameters.AddWithValue("@hash", hash);
            insert.Parameters.AddWithValue("@scope", scopeJson);
            if (from is not null) insert.Parameters.AddWithValue("@from", from.Value.UtcDateTime);
            if (to is not null) insert.Parameters.AddWithValue("@to", to.Value.UtcDateTime);
            try { await insert.ExecuteNonQueryAsync(cancellationToken); }
            catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
            {
                // 并发的同幂等键请求：回滚后按已存在的任务返回（请求不同则报冲突）。
                await transaction.RollbackAsync(cancellationToken);
                return await CreateJobAsync(tenantId, siteId, idempotencyKey, request, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        return (await GetJobAsync(tenantId, siteId, jobId, cancellationToken))!;
    }

    /// <inheritdoc />
    public async Task<ReputationJobView?> GetJobAsync(Guid tenantId, Guid siteId, Guid jobId,
        CancellationToken cancellationToken = default)
    {
        if (!await SchemaReadyAsync(cancellationToken)) throw ReputationSchema.Missing();
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        Guid releaseId;
        DateTimeOffset? from, to;
        DateTimeOffset createdAt;
        long total;
        await using (var job = new MySqlCommand("""
            SELECT BIN_TO_UUID(release_id), scope_json, total_checks, created_at FROM reputation_analysis_jobs
            WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site) AND id = UUID_TO_BIN(@job);
            """, connection))
        {
            job.Parameters.AddWithValue("@tenant", tenantId.ToString());
            job.Parameters.AddWithValue("@site", siteId.ToString());
            job.Parameters.AddWithValue("@job", jobId.ToString());
            await using var reader = await job.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            releaseId = Guid.Parse(reader.GetString(0));
            using var scope = JsonDocument.Parse(reader.GetString(1));
            from = ReadTime(scope.RootElement, "from");
            to = ReadTime(scope.RootElement, "to");
            total = reader.GetInt64(2);
            createdAt = new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc));
        }
        var window = (from is null ? "" : " AND c.captured_at >= @from") + (to is null ? "" : " AND c.captured_at < @to");
        await using var progress = new MySqlCommand($"""
            SELECT COALESCE(SUM(w.status = 'completed'), 0), COALESCE(SUM(w.status IN ('pending','processing','retry')), 0),
                   COALESCE(SUM(w.status = 'failed'), 0), COALESCE(SUM(s.state = 'unknown'), 0)
            FROM reputation_check_work w
            JOIN visibility_checks c ON c.id = w.check_id
            LEFT JOIN reputation_check_sentiments s ON s.release_id = w.release_id AND s.check_id = w.check_id
            WHERE w.release_id = UUID_TO_BIN(@release) AND w.site_id = UUID_TO_BIN(@site)
              AND w.tenant_id = UUID_TO_BIN(@tenant){window};
            """, connection);
        progress.Parameters.AddWithValue("@release", releaseId.ToString());
        progress.Parameters.AddWithValue("@site", siteId.ToString());
        progress.Parameters.AddWithValue("@tenant", tenantId.ToString());
        if (from is not null) progress.Parameters.AddWithValue("@from", from.Value.UtcDateTime);
        if (to is not null) progress.Parameters.AddWithValue("@to", to.Value.UtcDateTime);
        await using var progressReader = await progress.ExecuteReaderAsync(cancellationToken);
        await progressReader.ReadAsync(cancellationToken);
        var completed = Convert.ToInt64(progressReader.GetValue(0));
        var pending = Convert.ToInt64(progressReader.GetValue(1));
        var failed = Convert.ToInt64(progressReader.GetValue(2));
        var unknown = Convert.ToInt64(progressReader.GetValue(3));
        return new ReputationJobView(jobId, siteId, releaseId, from, to, pending > 0 ? "processing" : "completed",
            total, completed, pending, failed, unknown, createdAt);
    }

    private static DateTimeOffset? ReadTime(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetDateTimeOffset() : null;

    private static void AddKeys(MySqlCommand command, ReputationWorkLease lease)
    {
        command.Parameters.AddWithValue("@release", lease.ReleaseId.ToString());
        command.Parameters.AddWithValue("@check", lease.CheckId.ToString());
        if (command.CommandText.Contains("@brand", StringComparison.Ordinal))
            command.Parameters.AddWithValue("@brand", lease.BrandSubjectId.ToString());
        if (command.CommandText.Contains("@tenant", StringComparison.Ordinal))
            command.Parameters.AddWithValue("@tenant", lease.TenantId.ToString());
        if (command.CommandText.Contains("@site", StringComparison.Ordinal))
            command.Parameters.AddWithValue("@site", lease.SiteId.ToString());
    }

    private static string Limit(string value, int max) => value.Length <= max ? value : value[..max];
    private static CollectorStoreException Error(string code, string message, int status) => new(code, message, status);
}
