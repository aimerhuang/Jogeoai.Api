using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jogeoai.Application;
using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

/// <summary>发布版本、幂等工作项和可重试分析的 MySQL 实现。</summary>
public sealed partial class AnalysisStore(MySqlDatabase database) : IAnalysisStore
{
    private sealed record Lease(Guid ReleaseId, Guid CheckId, Guid TenantId, Guid SiteId, int Version,
        int ConfigVersion, string Snapshot, string Answer, string DetailVersion, string UrlVersion, int LeaseVersion);

    /// <inheritdoc />
    public async Task<int> ProcessPendingAsync(string workerId, int limit = 10, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workerId) || workerId.Length > 128 || limit is < 1 or > 100)
            throw new ArgumentException("分析 Worker 参数无效。");
        var processed = 0;
        for (var i = 0; i < limit; i++)
        {
            var lease = await ClaimAsync(workerId, cancellationToken);
            if (lease is null) break;
            try
            {
                var config = SubjectConfiguration.Parse(lease.SiteId, lease.ConfigVersion, lease.Snapshot);
                var matches = MentionMatcher.Match(lease.Answer, config);
                await CompleteAsync(lease, workerId, !config.IsComplete, matches, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await FailAsync(lease, workerId, "ANALYSIS_ERROR", cancellationToken);
            }
            processed++;
            await RefreshJobAsync(lease.SiteId, lease.ReleaseId, cancellationToken);
        }
        return processed;
    }

    private async Task<Lease?> ClaimAsync(string workerId, CancellationToken cancellationToken)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await using var command = new MySqlCommand("""
            SELECT BIN_TO_UUID(w.release_id), BIN_TO_UUID(w.check_id), BIN_TO_UUID(w.tenant_id),
                   BIN_TO_UUID(w.site_id), r.version, r.subject_config_version, r.subject_snapshot,
                   COALESCE(c.answer, ''), r.detail_version, r.url_normalization_version, w.lease_version
            FROM analysis_check_work w JOIN analysis_releases r ON r.id = w.release_id
            JOIN visibility_checks c ON c.id = w.check_id
            WHERE r.status IN ('published','building')
              AND ((w.status = 'pending') OR (w.status = 'failed' AND w.attempt_count < 5
                    AND w.updated_at < UTC_TIMESTAMP(6) - INTERVAL 5 SECOND)
                   OR (w.status = 'processing' AND w.lease_until < UTC_TIMESTAMP(6)))
            ORDER BY w.created_at, w.check_id LIMIT 1 FOR UPDATE SKIP LOCKED;
            """, connection, transaction);
        Lease? lease = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
                lease = new Lease(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                    Guid.Parse(reader.GetString(2)), Guid.Parse(reader.GetString(3)), reader.GetInt32(4),
                    reader.GetInt32(5), reader.GetString(6), reader.GetString(7), reader.GetString(8),
                    reader.GetString(9), reader.GetInt32(10) + 1);
        }
        if (lease is null) return null;
        await using var update = new MySqlCommand("""
            UPDATE analysis_check_work SET status = 'processing', lease_owner = @worker,
                lease_version = @version, lease_until = UTC_TIMESTAMP(6) + INTERVAL 90 SECOND,
                attempt_count = attempt_count + 1, error_code = NULL, updated_at = UTC_TIMESTAMP(6)
            WHERE release_id = UUID_TO_BIN(@release) AND check_id = UUID_TO_BIN(@check);
            """, connection, transaction);
        update.Parameters.AddWithValue("@worker", workerId);
        update.Parameters.AddWithValue("@version", lease.LeaseVersion);
        update.Parameters.AddWithValue("@release", lease.ReleaseId.ToString());
        update.Parameters.AddWithValue("@check", lease.CheckId.ToString());
        await update.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return lease;
    }

    private async Task CompleteAsync(Lease lease, string workerId, bool configMissing,
        IReadOnlyList<MentionMatch> matches, CancellationToken cancellationToken)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        // Snapshot creation, capture enqueueing, and analysis event allocation share this
        // row lock. An event with an ID below a snapshot watermark is therefore committed.
        await using (var fence = new MySqlCommand("SELECT site_id FROM site_analysis_state WHERE site_id = UUID_TO_BIN(@site) FOR UPDATE;", connection, transaction))
        {
            fence.Parameters.AddWithValue("@site", lease.SiteId.ToString());
            if (await fence.ExecuteScalarAsync(cancellationToken) is null)
                throw new InvalidOperationException("分析工作区不存在。");
        }
        await using var guard = new MySqlCommand("""
            SELECT lease_version, lease_owner, status FROM analysis_check_work
            WHERE release_id = UUID_TO_BIN(@release) AND check_id = UUID_TO_BIN(@check) FOR UPDATE;
            """, connection, transaction);
        guard.Parameters.AddWithValue("@release", lease.ReleaseId.ToString());
        guard.Parameters.AddWithValue("@check", lease.CheckId.ToString());
        await using (var reader = await guard.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken) || reader.GetInt32(0) != lease.LeaseVersion
                || reader.GetString(1) != workerId || reader.GetString(2) != "processing") return;
        }
        await using var eventCommand = new MySqlCommand("""
            INSERT INTO analysis_events (release_id, check_id, completed_at)
            VALUES (UUID_TO_BIN(@release), UUID_TO_BIN(@check), UTC_TIMESTAMP(6));
            """, connection, transaction);
        eventCommand.Parameters.AddWithValue("@release", lease.ReleaseId.ToString());
        eventCommand.Parameters.AddWithValue("@check", lease.CheckId.ToString());
        await eventCommand.ExecuteNonQueryAsync(cancellationToken);
        var eventId = eventCommand.LastInsertedId;
        await using var fact = new MySqlCommand("""
            INSERT INTO check_analysis (release_id, check_id, tenant_id, site_id, status,
                configuration_missing, completed_event_id, completed_at)
            VALUES (UUID_TO_BIN(@release), UUID_TO_BIN(@check), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site),
                'completed', @missing, @event, UTC_TIMESTAMP(6));
            """, connection, transaction);
        AddIds(fact, lease);
        fact.Parameters.AddWithValue("@missing", configMissing);
        fact.Parameters.AddWithValue("@event", eventId);
        await fact.ExecuteNonQueryAsync(cancellationToken);
        foreach (var group in matches.GroupBy(m => m.SubjectId))
        {
            var first = group.MinBy(m => m.OffsetStart)!;
            await using var presence = new MySqlCommand("""
                INSERT INTO check_subject_presence (release_id, check_id, subject_id, subject_type, first_offset)
                VALUES (UUID_TO_BIN(@release), UUID_TO_BIN(@check), UUID_TO_BIN(@subject), @type, @offset);
                """, connection, transaction);
            AddIds(presence, lease);
            presence.Parameters.AddWithValue("@subject", group.Key.ToString());
            presence.Parameters.AddWithValue("@type", first.SubjectType);
            presence.Parameters.AddWithValue("@offset", first.OffsetStart);
            await presence.ExecuteNonQueryAsync(cancellationToken);
            foreach (var match in group)
            {
                await using var evidence = new MySqlCommand("""
                    INSERT INTO mention_evidence (id, release_id, check_id, subject_id, subject_type,
                        matched_text, snippet, match_basis, offset_start, offset_end, extractor_version,
                        subject_config_version, created_at)
                    VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@release), UUID_TO_BIN(@check), UUID_TO_BIN(@subject),
                        @type, @text, @snippet, @basis, @start, @end, @version, @config, UTC_TIMESTAMP(6));
                    """, connection, transaction);
                AddIds(evidence, lease);
                evidence.Parameters.AddWithValue("@id", Guid.NewGuid().ToString());
                evidence.Parameters.AddWithValue("@subject", match.SubjectId.ToString());
                evidence.Parameters.AddWithValue("@type", match.SubjectType);
                evidence.Parameters.AddWithValue("@text", match.MatchedText.Length <= 512 ? match.MatchedText : match.MatchedText[..512]);
                evidence.Parameters.AddWithValue("@snippet", match.Snippet.Length <= 600 ? match.Snippet : match.Snippet[..600]);
                evidence.Parameters.AddWithValue("@basis", match.Basis);
                evidence.Parameters.AddWithValue("@start", match.OffsetStart);
                evidence.Parameters.AddWithValue("@end", match.OffsetEnd);
                evidence.Parameters.AddWithValue("@version", MentionMatcher.Version);
                evidence.Parameters.AddWithValue("@config", lease.ConfigVersion);
                await evidence.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        if (lease.DetailVersion is "detail-v1" or "detail-v2")
        {
            await WriteDetailedFactsAsync(connection, transaction, lease, configMissing, cancellationToken);
            // Facts, zero-event completion proof and the core completion event
            // share one transaction. A failed write must not publish a false zero.
            await using var detailDone = new MySqlCommand("""
                INSERT INTO check_detailed_completions (release_id, check_id, tenant_id, site_id,
                    completed_event_id, detail_version, url_normalization_version, completion_version, completed_at)
                VALUES (UUID_TO_BIN(@release), UUID_TO_BIN(@check), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site),
                    @event, @detailVersion, @urlVersion, 'detailed-completion-v1', UTC_TIMESTAMP(6));
                """, connection, transaction);
            AddIds(detailDone, lease);
            detailDone.Parameters.AddWithValue("@event", eventId);
            detailDone.Parameters.AddWithValue("@detailVersion", lease.DetailVersion);
            detailDone.Parameters.AddWithValue("@urlVersion", lease.UrlVersion);
            await detailDone.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var done = new MySqlCommand("""
            UPDATE analysis_check_work SET status = 'completed', lease_owner = NULL, lease_until = NULL,
                updated_at = UTC_TIMESTAMP(6) WHERE release_id = UUID_TO_BIN(@release) AND check_id = UUID_TO_BIN(@check);
            """, connection, transaction);
        AddIds(done, lease);
        await done.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task FailAsync(Lease lease, string workerId, string code, CancellationToken cancellationToken)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            UPDATE analysis_check_work SET status = 'failed', error_code = @code,
                lease_owner = NULL, lease_until = NULL, updated_at = UTC_TIMESTAMP(6)
            WHERE release_id = UUID_TO_BIN(@release) AND check_id = UUID_TO_BIN(@check)
              AND lease_owner = @worker AND lease_version = @version AND status = 'processing';
            """, connection);
        AddIds(command, lease);
        command.Parameters.AddWithValue("@worker", workerId);
        command.Parameters.AddWithValue("@version", lease.LeaseVersion);
        command.Parameters.AddWithValue("@code", code);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task RefreshJobAsync(Guid siteId, Guid releaseId, CancellationToken cancellationToken)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await using var state = new MySqlCommand("SELECT published_release_id FROM site_analysis_state WHERE site_id = UUID_TO_BIN(@site) FOR UPDATE;", connection, transaction);
        state.Parameters.AddWithValue("@site", siteId.ToString());
        await state.ExecuteScalarAsync(cancellationToken);
        await using var summary = new MySqlCommand("""
            SELECT COUNT(*), SUM(status = 'completed'), SUM(status = 'failed' AND attempt_count >= 5)
            FROM analysis_check_work WHERE release_id = UUID_TO_BIN(@release);
            """, connection, transaction);
        summary.Parameters.AddWithValue("@release", releaseId.ToString());
        int total, completed, failed;
        await using (var reader = await summary.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            total = reader.GetInt32(0);
            completed = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
            failed = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
        }
        await using var job = new MySqlCommand("""
            UPDATE analysis_jobs SET total_checks = @total, completed_checks = @completed, failed_checks = @failed,
                status = CASE WHEN @failed > 0 THEN 'failed'
                              WHEN @total = @completed THEN 'completed' ELSE 'processing' END,
                error_summary = CASE WHEN @failed > 0 THEN '部分回答分析重试耗尽；旧发布版本保持可读。' ELSE NULL END,
                updated_at = UTC_TIMESTAMP(6)
            WHERE release_id = UUID_TO_BIN(@release) AND status NOT IN ('published','failed');
            """, connection, transaction);
        job.Parameters.AddWithValue("@total", total);
        job.Parameters.AddWithValue("@completed", completed);
        job.Parameters.AddWithValue("@failed", failed);
        job.Parameters.AddWithValue("@release", releaseId.ToString());
        await job.ExecuteNonQueryAsync(cancellationToken);
        if (failed > 0)
        {
            await using var failRelease = new MySqlCommand("UPDATE analysis_releases SET status = 'failed' WHERE id = UUID_TO_BIN(@release) AND status = 'building';", connection, transaction);
            failRelease.Parameters.AddWithValue("@release", releaseId.ToString());
            await failRelease.ExecuteNonQueryAsync(cancellationToken);
        }
        else if (total == completed)
        {
            // The state row serializes publication with capture completion enqueueing.
            await using var publish = new MySqlCommand("""
                UPDATE site_analysis_state s JOIN analysis_releases r ON r.site_id = s.site_id
                SET s.published_release_id = r.id, s.updated_at = UTC_TIMESTAMP(6),
                    r.status = 'published', r.published_at = UTC_TIMESTAMP(6)
                WHERE s.site_id = UUID_TO_BIN(@site) AND r.id = UUID_TO_BIN(@release) AND r.status = 'building';
                UPDATE analysis_jobs SET status = 'published', published_at = UTC_TIMESTAMP(6),
                    updated_at = UTC_TIMESTAMP(6) WHERE release_id = UUID_TO_BIN(@release) AND status = 'completed';
                """, connection, transaction);
            publish.Parameters.AddWithValue("@site", siteId.ToString());
            publish.Parameters.AddWithValue("@release", releaseId.ToString());
            await publish.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<AnalysisJobView> CreateJobAsync(Guid tenantId, Guid siteId, string idempotencyKey,
        AnalysisJobRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 191)
            throw Error("IDEMPOTENCY_KEY_REQUIRED", "请提供长度不超过 191 的 Idempotency-Key。", 400);
        if (request.From >= request.To) throw Error("INVALID_TIME_RANGE", "from 必须早于 to。", 400);
        var promptIds = (request.PromptIds ?? []).Distinct().Order().ToArray();
        var normalized = JsonSerializer.Serialize(new
        {
            siteId,
            from = request.From?.ToUniversalTime(),
            to = request.To?.ToUniversalTime(),
            promptIds
        });
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await AnalysisEnqueue.EnsureSiteStateAsync(connection, transaction, tenantId, siteId, cancellationToken);
        await using var state = new MySqlCommand("""
            SELECT BIN_TO_UUID(s.published_release_id), b.subject_config_version,
                   r.snapshot, a.subject_config_version, a.detail_version, a.url_normalization_version,
                   a.extractor_version
            FROM site_analysis_state s JOIN site_brand_settings b ON b.site_id = s.site_id
            JOIN subject_config_revisions r ON r.site_id = b.site_id AND r.version = b.subject_config_version
            JOIN analysis_releases a ON a.id = s.published_release_id
            WHERE s.tenant_id = UUID_TO_BIN(@tenant) AND s.site_id = UUID_TO_BIN(@site) FOR UPDATE;
            """, connection, transaction);
        state.Parameters.AddWithValue("@tenant", tenantId.ToString());
        state.Parameters.AddWithValue("@site", siteId.ToString());
        Guid publishedId;
        int configVersion;
        string snapshot;
        int oldConfigVersion;
        string oldDetailVersion;
        string oldUrlVersion;
        string oldExtractorVersion;
        await using (var reader = await state.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) throw Error("RESOURCE_NOT_FOUND", "工作区不存在。", 404);
            publishedId = Guid.Parse(reader.GetString(0));
            configVersion = reader.GetInt32(1);
            snapshot = reader.GetString(2);
            oldConfigVersion = reader.GetInt32(3);
            oldDetailVersion = reader.GetString(4);
            oldUrlVersion = reader.GetString(5);
            oldExtractorVersion = reader.GetString(6);
        }
        await using var prior = new MySqlCommand("""
            SELECT BIN_TO_UUID(id), request_hash FROM analysis_jobs
            WHERE tenant_id = UUID_TO_BIN(@tenant) AND idempotency_key = @key;
            """, connection, transaction);
        prior.Parameters.AddWithValue("@tenant", tenantId.ToString());
        prior.Parameters.AddWithValue("@key", idempotencyKey);
        Guid? priorId = null;
        await using (var reader = await prior.ExecuteReaderAsync(cancellationToken))
            if (await reader.ReadAsync(cancellationToken))
            {
                priorId = Guid.Parse(reader.GetString(0));
                if (reader.GetString(1) != hash) throw Error("ANALYSIS_IDEMPOTENCY_CONFLICT", "幂等键已用于不同的重算请求。", 409);
            }
        if (priorId is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return (await GetJobAsync(tenantId, siteId, priorId.Value, cancellationToken))!;
        }
        foreach (var promptId in promptIds)
        {
            await using var owner = new MySqlCommand("""
                SELECT id FROM prompts WHERE site_id = UUID_TO_BIN(@site) AND id = UUID_TO_BIN(@prompt);
                """, connection, transaction);
            owner.Parameters.AddWithValue("@site", siteId.ToString());
            owner.Parameters.AddWithValue("@prompt", promptId.ToString());
            if (await owner.ExecuteScalarAsync(cancellationToken) is null)
                throw Error("RESOURCE_NOT_FOUND", "Prompt 不存在。", 404);
        }
        await using var current = new MySqlCommand("SELECT COUNT(*), COALESCE(MAX(version), 0) FROM analysis_releases WHERE site_id = UUID_TO_BIN(@site) AND status = 'building';", connection, transaction);
        current.Parameters.AddWithValue("@site", siteId.ToString());
        var building = Convert.ToInt32(await current.ExecuteScalarAsync(cancellationToken));
        if (building > 0) throw Error("ANALYSIS_VERSION_NOT_READY", "该工作区已有待发布重算。", 409);
        await using var nextVersion = new MySqlCommand("SELECT COALESCE(MAX(version), 0) + 1 FROM analysis_releases WHERE site_id = UUID_TO_BIN(@site);", connection, transaction);
        nextVersion.Parameters.AddWithValue("@site", siteId.ToString());
        var version = Convert.ToInt32(await nextVersion.ExecuteScalarAsync(cancellationToken));
        var releaseId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var hasScope = request.From is not null || request.To is not null || promptIds.Length > 0;
        var effectiveScope = EffectiveScope(hasScope, configVersion, oldConfigVersion, oldUrlVersion, oldExtractorVersion);
        await using var insert = new MySqlCommand("""
            INSERT INTO analysis_releases (id, tenant_id, site_id, version, subject_config_version,
                subject_snapshot, extractor_version, metric_version, detail_version, url_normalization_version, status, created_at)
            VALUES (UUID_TO_BIN(@release), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), @version, @config,
                @snapshot, @extractor, 'mentions-v1', 'detail-v2', 'url-v2', 'building', UTC_TIMESTAMP(6));
            INSERT INTO analysis_jobs (id, tenant_id, site_id, release_id, idempotency_key, request_hash,
                scope_json, status, created_at, updated_at)
            VALUES (UUID_TO_BIN(@job), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), UUID_TO_BIN(@release),
                @key, @hash, @scope, 'pending', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
            INSERT IGNORE INTO analysis_check_work (release_id, check_id, tenant_id, site_id, created_at, updated_at)
            SELECT UUID_TO_BIN(@release), c.id, c.tenant_id, c.site_id, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)
            FROM visibility_checks c WHERE c.site_id = UUID_TO_BIN(@site) AND c.tenant_id = UUID_TO_BIN(@tenant)
              AND c.status = 'succeeded';
            UPDATE analysis_jobs SET total_checks = (SELECT COUNT(*) FROM analysis_check_work WHERE release_id = UUID_TO_BIN(@release))
            WHERE id = UUID_TO_BIN(@job);
            """, connection, transaction);
        insert.Parameters.AddWithValue("@release", releaseId.ToString());
        insert.Parameters.AddWithValue("@tenant", tenantId.ToString());
        insert.Parameters.AddWithValue("@site", siteId.ToString());
        insert.Parameters.AddWithValue("@version", version);
        insert.Parameters.AddWithValue("@config", configVersion);
        insert.Parameters.AddWithValue("@snapshot", snapshot);
        insert.Parameters.AddWithValue("@extractor", MentionMatcher.Version);
        insert.Parameters.AddWithValue("@job", jobId.ToString());
        insert.Parameters.AddWithValue("@key", idempotencyKey);
        insert.Parameters.AddWithValue("@hash", hash);
        insert.Parameters.AddWithValue("@scope", JsonSerializer.Serialize(new
        {
            requested = request,
            effective = effectiveScope,
            previousReleaseId = publishedId
        }));
        await insert.ExecuteNonQueryAsync(cancellationToken);
        if (CanReuseOutsideScope(hasScope, configVersion, oldConfigVersion, oldDetailVersion, oldUrlVersion,
                oldExtractorVersion))
            await CopyOutsideScopeAsync(connection, transaction, releaseId, publishedId,
                request.From, request.To, promptIds, cancellationToken);
        await using (var updateReuse = new MySqlCommand("""
            UPDATE analysis_jobs SET reused_checks = (SELECT COUNT(*) FROM analysis_check_work
                WHERE release_id = UUID_TO_BIN(@release) AND status = 'completed'),
                completed_checks = (SELECT COUNT(*) FROM analysis_check_work
                WHERE release_id = UUID_TO_BIN(@release) AND status = 'completed')
            WHERE id = UUID_TO_BIN(@job);
            """, connection, transaction))
        {
            updateReuse.Parameters.AddWithValue("@release", releaseId.ToString());
            updateReuse.Parameters.AddWithValue("@job", jobId.ToString());
            await updateReuse.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        await RefreshJobAsync(siteId, releaseId, cancellationToken);
        return (await GetJobAsync(tenantId, siteId, jobId, cancellationToken))!;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AnalysisRebuildCandidate>> FindSitesNeedingRebuildAsync(int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0) return [];
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        // 只读检测；是否已过合并等待时间由 AnalysisRebuildRules.Decide 判断。datetime(6) 列存 UTC。
        await using var command = new MySqlCommand("""
            SELECT BIN_TO_UUID(s.site_id), BIN_TO_UUID(b.tenant_id), b.subject_config_version,
                   r.subject_config_version, r.extractor_version,
                   (SELECT MAX(v.created_at) FROM subject_config_revisions v WHERE v.site_id = s.site_id)
            FROM site_analysis_state s
            JOIN analysis_releases r ON r.id = s.published_release_id
            JOIN site_brand_settings b ON b.site_id = s.site_id
            WHERE (b.subject_config_version <> r.subject_config_version OR r.extractor_version <> @extractor)
              AND NOT EXISTS (SELECT 1 FROM analysis_releases x WHERE x.site_id = s.site_id AND x.status = 'building')
            -- 当前版本的自动作业已失败的站点排在最后（仍返回以便 Worker 记录一次告警），
            -- 避免失败站点累积后占满批次、其他站点永远轮不到。
            ORDER BY EXISTS (SELECT 1 FROM analysis_jobs j WHERE j.tenant_id = b.tenant_id AND j.site_id = s.site_id
                    AND j.status = 'failed'
                    AND j.idempotency_key = CONCAT('auto-rebuild:', BIN_TO_UUID(s.site_id), ':cfg',
                        b.subject_config_version, ':', @extractor)),
                s.site_id
            LIMIT @limit;
            """, connection);
        command.Parameters.AddWithValue("@extractor", MentionMatcher.Version);
        command.Parameters.AddWithValue("@limit", limit);
        var candidates = new List<AnalysisRebuildCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            candidates.Add(new AnalysisRebuildCandidate(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                reader.GetInt32(2), reader.GetInt32(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : Utc(reader.GetDateTime(5))));
        return candidates;
    }

    /// <summary>
    /// 新重算作业的实际范围：URL 规范化版本变化优先，其次识别器版本变化（如 mention-v1 → mention-v2），再次主体配置变化；
    /// 三者均未变化时，带范围的请求才会复用范围外的已发布事实。
    /// </summary>
    internal static string EffectiveScope(bool hasScope, int configVersion, int oldConfigVersion,
        string oldUrlVersion, string oldExtractorVersion)
        => oldUrlVersion != DetailedAnalyticsRules.UrlV2 ? "url_normalization_version_changed"
            : oldExtractorVersion != MentionMatcher.Version ? "extractor_version_changed"
            : configVersion != oldConfigVersion ? "site_config_changed"
            : hasScope ? "requested_scope_with_reuse" : "site";

    /// <summary>只有请求带范围且已发布版本的配置、明细、URL 与识别器版本都与当前一致时，才复制范围外的事实。</summary>
    internal static bool CanReuseOutsideScope(bool hasScope, int configVersion, int oldConfigVersion,
        string oldDetailVersion, string oldUrlVersion, string oldExtractorVersion)
        => hasScope && configVersion == oldConfigVersion && oldDetailVersion == "detail-v2"
            && oldUrlVersion == DetailedAnalyticsRules.UrlV2 && oldExtractorVersion == MentionMatcher.Version;

    private static async Task CopyOutsideScopeAsync(MySqlConnection connection, MySqlTransaction transaction,
        Guid releaseId, Guid previousReleaseId, DateTimeOffset? from, DateTimeOffset? to,
        IReadOnlyList<Guid> promptIds, CancellationToken cancellationToken)
    {
        var parts = new List<string>();
        if (from is not null) parts.Add("c.captured_at >= @from");
        if (to is not null) parts.Add("c.captured_at < @to");
        if (promptIds.Count > 0)
            parts.Add("c.prompt_id IN (" + string.Join(',', promptIds.Select((_, i) => $"UUID_TO_BIN(@p{i})")) + ")");
        var inScope = string.Join(" AND ", parts);
        // Prior completed facts are immutable. A filtered rebuild copies only
        // out-of-scope facts; incomplete prior work is processed in the new release.
        await using var copy = new MySqlCommand($"""
            UPDATE analysis_check_work w
            JOIN visibility_checks c ON c.id = w.check_id
            JOIN check_analysis old ON old.check_id = w.check_id AND old.release_id = UUID_TO_BIN(@old)
                AND old.status = 'completed'
            JOIN check_detailed_completions proof ON proof.release_id = old.release_id
                AND proof.check_id = old.check_id AND proof.tenant_id = old.tenant_id AND proof.site_id = old.site_id
                AND proof.completed_event_id = old.completed_event_id AND proof.detail_version = 'detail-v2'
                AND proof.url_normalization_version = 'url-v2' AND proof.completion_version = 'detailed-completion-v1'
            SET w.status = 'completed', w.updated_at = UTC_TIMESTAMP(6)
            WHERE w.release_id = UUID_TO_BIN(@release) AND NOT ({inScope});
            INSERT INTO analysis_events (release_id, check_id, completed_at)
            SELECT UUID_TO_BIN(@release), w.check_id, UTC_TIMESTAMP(6) FROM analysis_check_work w
            WHERE w.release_id = UUID_TO_BIN(@release) AND w.status = 'completed';
            INSERT INTO check_analysis (release_id, check_id, tenant_id, site_id, status,
                configuration_missing, completed_event_id, completed_at)
            SELECT UUID_TO_BIN(@release), old.check_id, old.tenant_id, old.site_id, 'completed',
                   old.configuration_missing, event.id, UTC_TIMESTAMP(6)
            FROM analysis_check_work w JOIN check_analysis old ON old.check_id = w.check_id
                AND old.release_id = UUID_TO_BIN(@old)
            JOIN analysis_events event ON event.release_id = w.release_id AND event.check_id = w.check_id
            WHERE w.release_id = UUID_TO_BIN(@release) AND w.status = 'completed';
            INSERT INTO check_subject_presence (release_id, check_id, subject_id, subject_type, first_offset)
            SELECT UUID_TO_BIN(@release), p.check_id, p.subject_id, p.subject_type, p.first_offset
            FROM check_subject_presence p JOIN analysis_check_work w ON w.check_id = p.check_id
            WHERE p.release_id = UUID_TO_BIN(@old) AND w.release_id = UUID_TO_BIN(@release)
              AND w.status = 'completed';
            INSERT INTO mention_evidence (id, release_id, check_id, subject_id, subject_type,
                matched_text, snippet, match_basis, offset_start, offset_end, extractor_version,
                subject_config_version, created_at)
            SELECT UUID_TO_BIN(UUID()), UUID_TO_BIN(@release), e.check_id, e.subject_id, e.subject_type,
                   e.matched_text, e.snippet, e.match_basis, e.offset_start, e.offset_end,
                   e.extractor_version, e.subject_config_version, UTC_TIMESTAMP(6)
            FROM mention_evidence e JOIN analysis_check_work w ON w.check_id = e.check_id
            WHERE e.release_id = UUID_TO_BIN(@old) AND w.release_id = UUID_TO_BIN(@release)
              AND w.status = 'completed';
            INSERT INTO check_query_events (release_id, check_id, query_hash, query_key, original_query,
                first_position, included, exclusion_reason, normalization_version)
            SELECT UUID_TO_BIN(@release), q.check_id, q.query_hash, q.query_key, q.original_query,
                q.first_position, q.included, q.exclusion_reason, q.normalization_version
            FROM check_query_events q JOIN analysis_check_work w ON w.check_id = q.check_id
            WHERE q.release_id = UUID_TO_BIN(@old) AND w.release_id = UUID_TO_BIN(@release)
              AND w.status = 'completed';
            INSERT INTO query_term_facts (release_id, check_id, query_hash, term_hash, term, relation, tokenizer_version)
            SELECT UUID_TO_BIN(@release), t.check_id, t.query_hash, t.term_hash, t.term, t.relation,
                t.tokenizer_version FROM query_term_facts t JOIN analysis_check_work w ON w.check_id = t.check_id
            WHERE t.release_id = UUID_TO_BIN(@old) AND w.release_id = UUID_TO_BIN(@release)
              AND w.status = 'completed';
            INSERT INTO citation_events (release_id, check_id, url_hash, normalized_url, domain, title,
                first_position, url_version, normalization_status)
            SELECT UUID_TO_BIN(@release), e.check_id, e.url_hash, e.normalized_url, e.domain,
                e.title, e.first_position, e.url_version, e.normalization_status
            FROM citation_events e JOIN analysis_check_work w ON w.check_id = e.check_id
            WHERE e.release_id = UUID_TO_BIN(@old) AND w.release_id = UUID_TO_BIN(@release)
              AND w.status = 'completed';
            INSERT INTO citation_classifications (release_id, check_id, url_hash, source_category,
                page_type, ownership, subject_id, rule_version, rule_basis)
            SELECT UUID_TO_BIN(@release), c.check_id, c.url_hash, c.source_category,
                c.page_type, c.ownership, c.subject_id, c.rule_version, c.rule_basis
            FROM citation_classifications c JOIN analysis_check_work w ON w.check_id = c.check_id
            WHERE c.release_id = UUID_TO_BIN(@old) AND w.release_id = UUID_TO_BIN(@release)
              AND w.status = 'completed';
            INSERT INTO google_internal_events (release_id, check_id, url_hash, normalized_url,
                kind, search_query, product, availability, ownership, subject_id, first_position, rule_version)
            SELECT UUID_TO_BIN(@release), g.check_id, g.url_hash, g.normalized_url,
                g.kind, g.search_query, g.product, g.availability, g.ownership, g.subject_id,
                g.first_position, g.rule_version
            FROM google_internal_events g JOIN analysis_check_work w ON w.check_id = g.check_id
            WHERE g.release_id = UUID_TO_BIN(@old) AND w.release_id = UUID_TO_BIN(@release)
              AND w.status = 'completed';
            INSERT INTO check_detailed_completions (release_id, check_id, tenant_id, site_id,
                completed_event_id, detail_version, url_normalization_version, completion_version, completed_at)
            SELECT a.release_id, a.check_id, a.tenant_id, a.site_id, a.completed_event_id,
                   proof.detail_version, proof.url_normalization_version, proof.completion_version, UTC_TIMESTAMP(6)
            FROM check_analysis a JOIN check_detailed_completions proof ON proof.check_id = a.check_id
                AND proof.release_id = UUID_TO_BIN(@old) AND proof.tenant_id = a.tenant_id AND proof.site_id = a.site_id
            WHERE a.release_id = UUID_TO_BIN(@release) AND a.status = 'completed';
            """, connection, transaction);
        copy.Parameters.AddWithValue("@release", releaseId.ToString());
        copy.Parameters.AddWithValue("@old", previousReleaseId.ToString());
        if (from is not null) copy.Parameters.AddWithValue("@from", from.Value.UtcDateTime);
        if (to is not null) copy.Parameters.AddWithValue("@to", to.Value.UtcDateTime);
        for (var i = 0; i < promptIds.Count; i++) copy.Parameters.AddWithValue($"@p{i}", promptIds[i].ToString());
        await copy.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<AnalysisJobView?> GetJobAsync(Guid tenantId, Guid siteId, Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            SELECT BIN_TO_UUID(j.release_id), r.version, r.subject_config_version, j.status,
                   j.total_checks, j.completed_checks, j.failed_checks, j.error_summary,
                   j.created_at, j.published_at, j.scope_json, j.reused_checks
            FROM analysis_jobs j JOIN analysis_releases r ON r.id = j.release_id
            WHERE j.tenant_id = UUID_TO_BIN(@tenant) AND j.site_id = UUID_TO_BIN(@site) AND j.id = UUID_TO_BIN(@job);
            """, connection);
        command.Parameters.AddWithValue("@tenant", tenantId.ToString());
        command.Parameters.AddWithValue("@site", siteId.ToString());
        command.Parameters.AddWithValue("@job", jobId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        using var scope = JsonDocument.Parse(reader.GetString(10));
        var requested = scope.RootElement.TryGetProperty("requested", out var requestedElement)
            ? requestedElement.Deserialize<AnalysisJobRequest>() : null;
        var effective = scope.RootElement.TryGetProperty("effective", out var effectiveElement)
            ? effectiveElement.GetString() ?? "site" : "site";
        return new AnalysisJobView(jobId, siteId, Guid.Parse(reader.GetString(0)), reader.GetInt32(1),
            reader.GetInt32(2), reader.GetString(3), reader.GetInt32(4), reader.GetInt32(5),
            reader.GetInt32(6), reader.IsDBNull(7) ? null : reader.GetString(7),
            Utc(reader.GetDateTime(8)), reader.IsDBNull(9) ? null : Utc(reader.GetDateTime(9)),
            requested, effective, reader.GetInt32(11));
    }

    /// <inheritdoc />
    public async Task<CheckAnalysisView?> GetCheckAnalysisAsync(Guid tenantId, Guid checkId,
        Guid? releaseId = null, long? analysisWatermark = null, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            SELECT BIN_TO_UUID(c.site_id), BIN_TO_UUID(r.id), r.version, r.subject_config_version,
                   b.subject_config_version, w.status, a.configuration_missing, a.completed_event_id,
                   c.status, COALESCE(c.answer_present, c.answer IS NOT NULL AND TRIM(c.answer) <> '')
            FROM visibility_checks c JOIN site_analysis_state s ON s.site_id = c.site_id
            JOIN analysis_releases r ON r.id = COALESCE(UUID_TO_BIN(@release), s.published_release_id)
                AND r.site_id = c.site_id
            JOIN site_brand_settings b ON b.site_id = c.site_id
            LEFT JOIN analysis_check_work w ON w.release_id = r.id AND w.check_id = c.id
            LEFT JOIN check_analysis a ON a.release_id = r.id AND a.check_id = c.id
            WHERE c.tenant_id = UUID_TO_BIN(@tenant) AND c.id = UUID_TO_BIN(@check);
            """, connection);
        command.Parameters.AddWithValue("@tenant", tenantId.ToString());
        command.Parameters.AddWithValue("@check", checkId.ToString());
        command.Parameters.AddWithValue("@release", releaseId?.ToString());
        Guid actualRelease;
        int version, configVersion;
        bool outdated, missing;
        string status;
        long? completedEvent;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return null;
            actualRelease = Guid.Parse(reader.GetString(1));
            version = reader.GetInt32(2);
            configVersion = reader.GetInt32(3);
            outdated = configVersion < reader.GetInt32(4);
            status = reader.IsDBNull(5) ? "pending" : reader.GetString(5);
            if (reader.GetString(8) != "succeeded" || !reader.GetBoolean(9)) status = "not_applicable";
            missing = !reader.IsDBNull(6) && reader.GetBoolean(6);
            completedEvent = reader.IsDBNull(7) ? null : reader.GetInt64(7);
        }
        if (analysisWatermark is not null && completedEvent > analysisWatermark) status = "pending";
        var mentions = new List<MentionEvidenceView>();
        if (status == "completed")
        {
            await using var evidence = new MySqlCommand("""
                SELECT BIN_TO_UUID(subject_id), subject_type, matched_text, snippet, match_basis,
                       offset_start, offset_end, extractor_version, subject_config_version
                FROM mention_evidence WHERE release_id = UUID_TO_BIN(@release) AND check_id = UUID_TO_BIN(@check)
                ORDER BY offset_start, subject_id;
                """, connection);
            evidence.Parameters.AddWithValue("@release", actualRelease.ToString());
            evidence.Parameters.AddWithValue("@check", checkId.ToString());
            await using var reader = await evidence.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                mentions.Add(new MentionEvidenceView(Guid.Parse(reader.GetString(0)), reader.GetString(1),
                    reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt32(5),
                    reader.GetInt32(6), reader.GetString(7), reader.GetInt32(8)));
        }
        return new CheckAnalysisView(checkId, actualRelease, version, configVersion, status,
            outdated, missing, mentions);
    }

    private static void AddIds(MySqlCommand command, Lease lease)
    {
        command.Parameters.AddWithValue("@release", lease.ReleaseId.ToString());
        command.Parameters.AddWithValue("@check", lease.CheckId.ToString());
        if (command.CommandText.Contains("@tenant", StringComparison.Ordinal))
            command.Parameters.AddWithValue("@tenant", lease.TenantId.ToString());
        if (command.CommandText.Contains("@site", StringComparison.Ordinal))
            command.Parameters.AddWithValue("@site", lease.SiteId.ToString());
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    private static CollectorStoreException Error(string code, string message, int status) => new(code, message, status);
}
