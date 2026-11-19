using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jogeoai.Application;
using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

/// <summary>国内 Collector 控制面和可见性结果的 MySQL 实现。</summary>
public sealed class DomesticCollectorStore(MySqlDatabase database) : IVisibilityCollectorStore
{
    private const int MaxAnswerBytes = 1_048_576;
    private const int MaxMetadataBytes = 1_048_576;
    private const int MaxCitationTitleLength = 512; // citations.title VARCHAR(512)
    private const int MaxSearchQueries = 32;
    private const int MaxSearchQueryLength = 500;
    private const int MaxSearchQueriesBytes = 65_536; // ck_visibility_checks_search_size

    // 关键词按 UTF-8 原文序列化，长度估算与 MySQL JSON 列的文本表示一致（非 ASCII 不转义为 \uXXXX）。
    private static readonly JsonSerializerOptions SearchQueriesJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <inheritdoc />
    public async Task<Guid?> GetExistingRunByKeyAsync(CreateVisibilityRunCommand expected, CancellationToken cancellationToken = default)
    {
        ValidateCreateCommand(expected);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var row = await QuerySingleAsync(connection, null, """
            SELECT BIN_TO_UUID(id), collector_request_hash FROM visibility_runs
            WHERE tenant_id = UUID_TO_BIN(@tenant) AND idempotency_key = @key;
            """, cancellationToken, ("@tenant", expected.TenantId.ToString()), ("@key", expected.IdempotencyKey));
        if (row is null) return null;
        if (row[1] != ComputeRunRequestHash(expected))
            throw new CollectorStoreException("RUN_IDEMPOTENCY_CONFLICT", "幂等键已用于不同的运行请求。", 409);
        return Guid.Parse(row[0]);
    }

    /// <inheritdoc />
    public async Task<VisibilityRunCreated> CreateRunAsync(CreateVisibilityRunCommand command, CancellationToken cancellationToken = default)
    {
        ValidateCreateCommand(command);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Serialize tenant-scoped idempotency across domestic and Cloro Run
        // creators. Without the tenant lock, concurrent providers could both
        // miss the existing key and surface a raw duplicate-key error.
        await QuerySingleAsync(connection, transaction,
            "SELECT id FROM tenants WHERE id = UUID_TO_BIN(@tenant_id) FOR UPDATE;",
            cancellationToken, ("@tenant_id", command.TenantId.ToString()));

        // Any registered API answer provider (cloro, doubao_api, deepseek_api) owns its run through CloroVisibilityStore;
        // only runs without such engines belong to the domestic collector.
        var registered = AnswerProviderDefinition.All.Select((definition, index) => ($"@provider{index}", (object?)definition.Provider)).ToArray();
        var existing = await QuerySingleAsync(connection, transaction, $"""
            SELECT BIN_TO_UUID(id), status, total_checks, created_at, collector_request_hash, http_request_hash,
                   CASE WHEN EXISTS (SELECT 1 FROM run_engines re WHERE re.run_id = visibility_runs.id
                                     AND re.provider IN ({string.Join(", ", registered.Select(p => p.Item1))}))
                        THEN 'api_provider' ELSE 'domestic_collector' END AS run_provider
            FROM visibility_runs
            WHERE tenant_id = UUID_TO_BIN(@tenant_id) AND idempotency_key = @idempotency_key
            LIMIT 1;
            """, cancellationToken, [("@tenant_id", command.TenantId.ToString()), ("@idempotency_key", command.IdempotencyKey), .. registered]);
        if (existing is not null)
        {
            if (!string.Equals(existing[6], "domestic_collector", StringComparison.Ordinal))
                throw new CollectorStoreException("RUN_IDEMPOTENCY_CONFLICT", "The idempotency key is already used by a different provider run.", 409);
            var requestHash = ComputeRunRequestHash(command);
            if (command.HttpRequestHash is not null && !string.IsNullOrEmpty(existing[5])
                ? existing[5] != command.HttpRequestHash : existing[4] != requestHash)
                throw new CollectorStoreException("RUN_IDEMPOTENCY_CONFLICT", "The idempotency key is already used by a different run request.", 409);
            await transaction.CommitAsync(cancellationToken);
            return new VisibilityRunCreated(
                Guid.Parse(existing[0]), existing[1], int.Parse(existing[2]), ToUtc(existing[3]));
        }

        var promptParameters = new List<object>
        {
            ("@tenant_id", command.TenantId.ToString()),
            ("@site_id", command.SiteId.ToString()),
        };
        promptParameters.AddRange(command.PromptIds.Select((id, i) => (object)($"@prompt{i}", id.ToString())));
        var promptRows = await QueryRowsAsync(connection, transaction, $"""
            SELECT BIN_TO_UUID(p.id), v.content, BIN_TO_UUID(v.id), v.branded_status, v.branded_source,
                   COALESCE((SELECT JSON_ARRAYAGG(pvt.tag_name_snapshot) FROM prompt_version_tags pvt
                             WHERE pvt.prompt_version_id = v.id), JSON_ARRAY()), b.subject_config_version
            FROM prompts p
            JOIN sites s ON s.id = p.site_id AND s.tenant_id = UUID_TO_BIN(@tenant_id)
            JOIN prompt_versions v ON v.id = p.current_version_id AND v.prompt_id = p.id
            JOIN site_brand_settings b ON b.site_id = s.id AND b.tenant_id = s.tenant_id
            WHERE p.site_id = UUID_TO_BIN(@site_id)
              AND p.id IN ({string.Join(",", command.PromptIds.Select((_, i) => $"UUID_TO_BIN(@prompt{i})"))});
            """, cancellationToken, promptParameters.ToArray());
        if (promptRows.Count != command.PromptIds.Count)
            throw new CollectorStoreException("PROMPT_NOT_FOUND", "One or more prompts do not belong to the site.", 404);

        var platformRows = new Dictionary<string, (string EntryCode, Guid EntryId, int Version)>();
        foreach (var platform in command.Platforms)
        {
            var row = await QuerySingleAsync(connection, transaction, """
                SELECT e.entry_code, BIN_TO_UUID(e.id), e.version
                FROM collector_platform_entries e
                JOIN collector_platforms p ON p.code = e.platform_code
                WHERE e.entry_code = @entry_code
                  AND e.platform_code = @platform_code
                  AND e.region = 'cn' AND e.channel = 'consumer_web'
                  AND e.enabled = TRUE AND p.enabled = TRUE
                ORDER BY e.version DESC
                LIMIT 1;
                """, cancellationToken,
                ("@entry_code", platform.EntryCode), ("@platform_code", platform.PlatformCode));
            if (row is null)
                throw new CollectorStoreException("PLATFORM_NOT_AVAILABLE", $"Platform entry '{platform.EntryCode}' is unavailable.", 409);
            platformRows[platform.EntryCode] = (row[0], Guid.Parse(row[1]), int.Parse(row[2]));
        }

        var runId = Guid.NewGuid();
        await ExecuteAsync(connection, transaction, """
            INSERT INTO visibility_runs
                (id, tenant_id, site_id, status, idempotency_key, requested_by,
                 requested_at, total_checks, collector_request_hash, http_request_hash, created_at, updated_at)
            VALUES
                (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant_id), UUID_TO_BIN(@site_id),
                 'queued', @idempotency_key, @requested_by, UTC_TIMESTAMP(6),
                 @total_checks, @request_hash, @http_hash, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
            """, cancellationToken,
            ("@id", runId.ToString()), ("@tenant_id", command.TenantId.ToString()),
            ("@site_id", command.SiteId.ToString()), ("@idempotency_key", command.IdempotencyKey),
            ("@requested_by", (object?)command.RequestedBy ?? DBNull.Value),
            ("@total_checks", command.PromptIds.Count * command.Platforms.Count),
            ("@request_hash", ComputeRunRequestHash(command)), ("@http_hash", command.HttpRequestHash));

        var runTargets = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var platform in command.Platforms)
        {
            var targetId = Guid.NewGuid();
            var targetKey = CollectionIdentity.TargetKey("domestic_collector", platform.PlatformCode, platform.Channel, "CN", platform.Language);
            await ExecuteAsync(connection, transaction, """
                INSERT INTO run_engines (id, run_id, engine, provider, request_options, sort_order, target_key,
                    capture_mode, country, locale, created_at)
                VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@run_id), @engine, 'domestic_collector', JSON_OBJECT(), @sort_order,
                    @target_key, @capture_mode, 'CN', @locale, UTC_TIMESTAMP(6));
                """, cancellationToken,
                ("@id", targetId.ToString()), ("@run_id", runId.ToString()), ("@engine", platform.PlatformCode),
                ("@sort_order", runTargets.Count), ("@target_key", targetKey),
                ("@capture_mode", platform.Channel), ("@locale", platform.Language));
            runTargets[platform.EntryCode] = targetId;
        }

        var total = 0;
        foreach (var prompt in promptRows)
        {
            var promptId = Guid.Parse(prompt[0]);
            foreach (var platform in command.Platforms)
            {
                var entry = platformRows[platform.EntryCode];
                var checkId = Guid.NewGuid();
                var taskId = Guid.NewGuid();
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO visibility_checks
                        (id, tenant_id, site_id, run_id, prompt_id, engine, provider, status,
                         prompt_snapshot, run_target_id, sample_index, prompt_version_id, prompt_tags_snapshot,
                         branded_status_snapshot, branded_source_snapshot, subject_config_version, snapshot_missing,
                         created_at, updated_at)
                    VALUES
                        (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant_id), UUID_TO_BIN(@site_id), UUID_TO_BIN(@run_id),
                         UUID_TO_BIN(@prompt_id), @engine, 'domestic_collector', 'pending', @prompt,
                         UUID_TO_BIN(@target_id), 1, UUID_TO_BIN(@version_id), @tags, @branded, @source,
                         @config_version, FALSE, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                    """, cancellationToken,
                    ("@id", checkId.ToString()), ("@tenant_id", command.TenantId.ToString()),
                    ("@site_id", command.SiteId.ToString()), ("@run_id", runId.ToString()),
                    ("@prompt_id", promptId.ToString()), ("@engine", platform.PlatformCode), ("@prompt", prompt[1]),
                    ("@target_id", runTargets[platform.EntryCode].ToString()), ("@version_id", prompt[2]),
                    ("@branded", prompt[3]), ("@source", prompt[4]), ("@tags", prompt[5]),
                    ("@config_version", int.Parse(prompt[6])));
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO collector_tasks
                        (id, tenant_id, run_id, check_id, platform_entry_id, platform_code, region,
                         language, channel, question_snapshot, status, next_attempt_at, created_at, updated_at)
                    VALUES
                        (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant_id), UUID_TO_BIN(@run_id), UUID_TO_BIN(@check_id),
                         UUID_TO_BIN(@entry_id), @platform_code, 'cn', @language, @channel, @question,
                         'pending', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                    """, cancellationToken,
                    ("@id", taskId.ToString()), ("@tenant_id", command.TenantId.ToString()),
                    ("@run_id", runId.ToString()), ("@check_id", checkId.ToString()),
                    ("@entry_id", entry.EntryId.ToString()), ("@platform_code", platform.PlatformCode),
                    ("@language", platform.Language), ("@channel", platform.Channel), ("@question", prompt[1]));
                total++;
            }
        }
        await ExecuteAsync(connection, transaction, """
            INSERT INTO outbox_events
                (id, tenant_id, event_type, aggregate_type, aggregate_id, dedupe_key, payload,
                 status, available_at, created_at, updated_at)
            VALUES
                (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant_id), 'collector.tasks.created', 'visibility_run',
                 UUID_TO_BIN(@run_id), @dedupe_key, JSON_OBJECT('runId', @run_id), 'pending',
                 UTC_TIMESTAMP(6), UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
            """, cancellationToken,
            ("@id", Guid.NewGuid().ToString()), ("@tenant_id", command.TenantId.ToString()),
            ("@run_id", runId.ToString()), ("@dedupe_key", $"collector-run:{runId}"));
        await transaction.CommitAsync(cancellationToken);
        return new VisibilityRunCreated(runId, "queued", total, DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LeasedCollectorTask>> LeaseAsync(CollectorLeaseCommand command, CancellationToken cancellationToken = default)
    {
        ValidateLeaseCommand(command);
        if (command.SessionProfileId is { } profileId) return await LeaseForProfileAsync(command, profileId, cancellationToken);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // 登记了账号的平台不再走无账号派发（含停用账号），避免账号池平台回落到不受配额约束的派发。
        if (await CollectorSessionDispatch.TenantUsesProfilesAsync(connection, transaction, command, cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return [];
        }
        var placeholders = string.Join(",", command.SupportedPlatformCodes.Select((_, i) => $"@platform{i}"));
        var leaseParameters = new List<object>
        {
            ("@tenant_id", command.TenantId.ToString()), ("@region", RegionCode(command.Region)), ("@capacity", command.Capacity),
        };
        leaseParameters.AddRange(command.SupportedPlatformCodes.Select((value, i) => (object)($"@platform{i}", value)));
        var candidates = await QueryRowsAsync(connection, transaction, $"""
            SELECT BIN_TO_UUID(id)
            FROM collector_tasks
            WHERE tenant_id = UUID_TO_BIN(@tenant_id)
              AND region = @region
              AND platform_code IN ({placeholders})
              AND channel = 'consumer_web'
              AND next_attempt_at <= UTC_TIMESTAMP(6)
              AND (status IN ('pending', 'retryable_failed') OR (status IN ('leased', 'collecting') AND lease_until <= UTC_TIMESTAMP(6)))
            ORDER BY next_attempt_at, created_at, id
            LIMIT @capacity
            FOR UPDATE SKIP LOCKED;
            """, cancellationToken, leaseParameters.ToArray());
        var ids = candidates.Select(row => row[0]).ToArray();
        foreach (var id in ids)
        {
            await ExecuteAsync(connection, transaction, """
                UPDATE collector_tasks
                SET status = 'leased', lease_version = lease_version + 1, lease_owner = @owner,
                    lease_until = UTC_TIMESTAMP(6) + INTERVAL @seconds SECOND, lease_session_profile_id = NULL,
                    attempt_count = attempt_count + 1, started_at = COALESCE(started_at, UTC_TIMESTAMP(6)),
                    updated_at = UTC_TIMESTAMP(6)
                WHERE id = UUID_TO_BIN(@id);
                """, cancellationToken, ("@owner", command.NodeId), ("@seconds", command.LeaseSeconds), ("@id", id));
            await ExecuteAsync(connection, transaction, """
                INSERT INTO collector_attempts (id, tenant_id, collector_task_id, lease_version, node_id, region, event_kind, created_at)
                SELECT UUID_TO_BIN(@attempt_id), tenant_id, id, lease_version, @node_id, region, 'leased', UTC_TIMESTAMP(6)
                FROM collector_tasks WHERE id = UUID_TO_BIN(@id);
                """, cancellationToken, ("@attempt_id", Guid.NewGuid().ToString()), ("@node_id", command.NodeId), ("@id", id));
        }
        var result = await ReadLeasedTasksAsync(connection, transaction, ids, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    /// <summary>账号池租约（迁移 035）：READ COMMITTED，账号 → 平台 → 任务加锁，capacity 固定为 1；死锁整体重试。</summary>
    private async Task<IReadOnlyList<LeasedCollectorTask>> LeaseForProfileAsync(CollectorLeaseCommand command, Guid profileId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var connection = database.CreateConnection();
                await connection.OpenAsync(cancellationToken);
                await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
                var taskId = await CollectorSessionDispatch.LeaseForProfileAsync(connection, transaction, command, profileId, cancellationToken);
                var result = taskId is { } id
                    ? await ReadLeasedTasksAsync(connection, transaction, [id.ToString()], cancellationToken)
                    : [];
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
            catch (MySqlException exception) when (exception.ErrorCode == MySqlErrorCode.LockDeadlock && attempt < 3)
            {
            }
            // App 渠道隔离（design 6.3）：sessionProfileId 是 App 绑定的标识时留下审计事件；响应仍是同一个 403。
            catch (CollectorStoreException exception) when (exception.Code == "SESSION_PROFILE_FORBIDDEN")
            {
                await AppCollectorPoolStore.AuditBindingUsedAsWebProfileAsync(database, profileId, command.NodeId, cancellationToken);
                throw;
            }
        }
    }

    private static async Task<IReadOnlyList<LeasedCollectorTask>> ReadLeasedTasksAsync(MySqlConnection connection, MySqlTransaction transaction,
        IEnumerable<string> ids, CancellationToken cancellationToken)
    {
        var result = new List<LeasedCollectorTask>();
        foreach (var id in ids)
        {
            var row = await QuerySingleAsync(connection, transaction, """
                SELECT BIN_TO_UUID(t.id), BIN_TO_UUID(t.tenant_id), BIN_TO_UUID(t.run_id), BIN_TO_UUID(t.check_id),
                       BIN_TO_UUID(r.site_id), t.platform_code, e.entry_code, BIN_TO_UUID(e.id), e.version,
                       BIN_TO_UUID(c.prompt_id), t.question_snapshot, t.region, t.language, t.channel,
                       t.lease_version, t.lease_until, BIN_TO_UUID(t.lease_session_profile_id)
                FROM collector_tasks t
                JOIN collector_platform_entries e ON e.id = t.platform_entry_id
                JOIN visibility_runs r ON r.id = t.run_id
                JOIN visibility_checks c ON c.id = t.check_id
                WHERE t.id = UUID_TO_BIN(@id);
                """, cancellationToken, ("@id", id));
            if (row is null) continue;
            result.Add(new LeasedCollectorTask(Guid.Parse(row[0]), Guid.Parse(row[1]), Guid.Parse(row[2]), Guid.Parse(row[3]),
                Guid.Parse(row[4]), row[5], row[6], Guid.Parse(row[7]), int.Parse(row[8]), Guid.Parse(row[9]), row[10],
                ParseRegion(row[11]), row[12], row[13], int.Parse(row[14]), ToUtc(row[15]),
                row[16] == "" ? null : Guid.Parse(row[16])));
        }
        return result;
    }

    /// <inheritdoc />
    public async Task<DateTimeOffset> HeartbeatAsync(CollectorHeartbeatCommand command, CancellationToken cancellationToken = default)
    {
        if (command.ExtendSeconds is < 5 or > 900) throw new CollectorStoreException("INVALID_LEASE", "extendSeconds must be between 5 and 900.", 400);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var affected = await ExecuteAsync(connection, transaction, """
            UPDATE collector_tasks
            SET lease_until = UTC_TIMESTAMP(6) + INTERVAL @seconds SECOND, status = 'collecting', updated_at = UTC_TIMESTAMP(6)
            WHERE id = UUID_TO_BIN(@id) AND tenant_id = UUID_TO_BIN(@tenant_id) AND region = @region
              AND lease_owner = @owner AND lease_version = @version AND status IN ('leased', 'collecting')
              AND lease_until > UTC_TIMESTAMP(6);
            """, cancellationToken, ("@seconds", command.ExtendSeconds), ("@id", command.TaskId.ToString()),
            ("@tenant_id", command.TenantId.ToString()), ("@region", RegionCode(command.Region)), ("@owner", command.NodeId), ("@version", command.LeaseVersion));
        if (affected == 0) throw new CollectorStoreException("STALE_LEASE", "The collector task lease is no longer current.", 409);
        var expiry = await QuerySingleAsync(connection, transaction, "SELECT lease_until FROM collector_tasks WHERE id = UUID_TO_BIN(@id);", cancellationToken, ("@id", command.TaskId.ToString()));
        await ExecuteAsync(connection, transaction, """
            INSERT INTO collector_attempts (id, tenant_id, collector_task_id, lease_version, node_id, region, event_kind, created_at)
            VALUES (UUID_TO_BIN(@attempt_id), UUID_TO_BIN(@tenant_id), UUID_TO_BIN(@task_id), @version, @node_id, @region, 'heartbeat', UTC_TIMESTAMP(6));
            """, cancellationToken, ("@attempt_id", Guid.NewGuid().ToString()), ("@tenant_id", command.TenantId.ToString()),
            ("@task_id", command.TaskId.ToString()), ("@version", command.LeaseVersion), ("@node_id", command.NodeId), ("@region", RegionCode(command.Region)));
        await transaction.CommitAsync(cancellationToken);
        return expiry is null ? DateTimeOffset.UtcNow.AddSeconds(command.ExtendSeconds) : ToUtc(expiry[0]);
    }

    /// <inheritdoc />
    public async Task<CollectorResultResponse> SubmitResultAsync(CollectorResultCommand command, CancellationToken cancellationToken = default)
    {
        command = ValidateResultCommand(command);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var task = await QuerySingleAsync(connection, transaction, """
            SELECT BIN_TO_UUID(id), BIN_TO_UUID(check_id), status, lease_owner, lease_version, lease_until, region,
                   BIN_TO_UUID(lease_session_profile_id)
            FROM collector_tasks WHERE id = UUID_TO_BIN(@id) AND tenant_id = UUID_TO_BIN(@tenant_id) FOR UPDATE;
            """, cancellationToken, ("@id", command.TaskId.ToString()), ("@tenant_id", command.TenantId.ToString()));
        if (task is null) throw new CollectorStoreException("TASK_NOT_FOUND", "Collector task was not found.", 404);
        if (!string.Equals(task[6], RegionCode(command.Region), StringComparison.Ordinal))
            throw new CollectorStoreException("REGION_MISMATCH", "Collector region does not match the task.", 409);
        var prior = await QuerySingleAsync(connection, transaction, """
            SELECT disposition, BIN_TO_UUID((SELECT check_id FROM collector_tasks WHERE id = UUID_TO_BIN(@task_id))), payload_hash
            FROM collector_result_submissions WHERE collector_task_id = UUID_TO_BIN(@task_id) AND idempotency_key = @key LIMIT 1;
            """, cancellationToken, ("@task_id", command.TaskId.ToString()), ("@key", command.IdempotencyKey));
        if (prior is not null)
        {
            var priorPayloadHash = ComputePayloadHash(command.AnswerText, command.Citations);
            if (!string.Equals(prior[2], priorPayloadHash, StringComparison.Ordinal))
                throw new CollectorStoreException("RESULT_IDEMPOTENCY_CONFLICT", "The idempotency key was already used for a different result.", 409);
            await InsertAttemptAsync(connection, transaction, command, "duplicate", null, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new CollectorResultResponse(CollectorResultDisposition.Duplicate, Guid.Parse(prior[1]));
        }

        var payloadHash = ComputePayloadHash(command.AnswerText, command.Citations);
        var stale = !string.Equals(task[3], command.NodeId, StringComparison.Ordinal)
            || int.Parse(task[4]) != command.LeaseVersion
            // MySQL 返回的 lease_until 是不带时区的 UTC 字符串；不能用 ToUniversalTime()（会按本机时区再减 8 小时，把有效租约误判为过期）。
            || DateTime.SpecifyKind(DateTime.Parse(task[5], System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc) <= DateTime.UtcNow
            || !string.Equals(task[2], "leased", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(task[2], "collecting", StringComparison.OrdinalIgnoreCase);
        await InsertSubmissionAsync(connection, transaction, command, payloadHash, stale ? "stale" : "accepted", cancellationToken);
        if (stale)
        {
            await InsertAttemptAsync(connection, transaction, command, "stale", payloadHash, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new CollectorResultResponse(CollectorResultDisposition.Stale, null);
        }

        var checkId = Guid.Parse(task[1]);
        // 样本的账号取自控制面租约记录，不信任请求体；无账号租约为 NULL。
        var sessionProfileId = task[7] == "" ? null : task[7];
        // 未提交关键词（旧 Collector 或解析失败）时两列保持原值（默认 unknown）；提交了才写 available / empty。
        var queries = command.SearchQueries;
        await ExecuteAsync(connection, transaction, """
            UPDATE visibility_checks
            SET status = 'succeeded', answer = @answer, answer_present = TRUE,
                capture_mode = COALESCE(capture_mode, 'scraped'), evidence_class = COALESCE(evidence_class, 'observed_surface'),
                search_queries = COALESCE(CAST(@queries AS JSON), search_queries),
                query_availability = COALESCE(@query_availability, query_availability),
                collector_task_id = UUID_TO_BIN(@task_id),
                collector_node_id = @node_id, collector_region = @region, egress_country = @egress,
                collector_build_revision = @revision, captured_at = @completed_at, result_hash = @hash,
                session_profile_id = UUID_TO_BIN(@session_profile_id), updated_at = UTC_TIMESTAMP(6)
            WHERE id = UUID_TO_BIN(@check_id) AND tenant_id = UUID_TO_BIN(@tenant_id);
            """, cancellationToken, ("@answer", command.AnswerText), ("@task_id", command.TaskId.ToString()),
            ("@queries", queries is null ? null : SerializeSearchQueries(queries)),
            ("@query_availability", queries is null ? null : queries.Count > 0 ? "available" : "empty"),
            ("@node_id", command.NodeId), ("@region", RegionCode(command.Region)), ("@egress", command.EgressCountry),
            ("@revision", command.CollectorBuildRevision), ("@completed_at", command.CompletedAt.UtcDateTime),
            ("@hash", payloadHash), ("@check_id", checkId.ToString()), ("@tenant_id", command.TenantId.ToString()),
            ("@session_profile_id", sessionProfileId));
        var position = 1;
        foreach (var citation in command.Citations)
        {
            var normalized = NormalizeUrl(citation.Url);
            if (normalized is null) continue;
            await ExecuteAsync(connection, transaction, """
                INSERT INTO citations (id, check_id, normalized_url, title, source_domain, position, created_at)
                VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@check_id), @url, @title, @domain, @position, UTC_TIMESTAMP(6))
                ON DUPLICATE KEY UPDATE title = COALESCE(VALUES(title), title);
                """, cancellationToken, ("@id", Guid.NewGuid().ToString()), ("@check_id", checkId.ToString()),
                ("@url", normalized.Value.Url), ("@title", (object?)citation.Title ?? DBNull.Value),
                ("@domain", normalized.Value.Domain), ("@position", citation.Position > 0 ? citation.Position : position++));
        }
        await ExecuteAsync(connection, transaction, """
            UPDATE collector_tasks SET status = 'succeeded', lease_owner = NULL, lease_until = NULL,
                completed_at = UTC_TIMESTAMP(6), updated_at = UTC_TIMESTAMP(6)
            WHERE id = UUID_TO_BIN(@id) AND tenant_id = UUID_TO_BIN(@tenant_id);
            """, cancellationToken, ("@id", command.TaskId.ToString()), ("@tenant_id", command.TenantId.ToString()));
        await InsertAttemptAsync(connection, transaction, command, "accepted", payloadHash, cancellationToken, sessionProfileId);
        await UpdateRunStatusAsync(connection, transaction, command.TenantId, Guid.Parse(task[0]), cancellationToken);
        await AnalysisEnqueue.QueueAsync(connection, transaction, checkId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CollectorResultResponse(CollectorResultDisposition.Accepted, checkId);
    }

    /// <inheritdoc />
    public async Task<VisibilityRunSummary?> GetRunAsync(Guid tenantId, Guid runId, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var row = await QuerySingleAsync(connection, null, """
            SELECT BIN_TO_UUID(id), BIN_TO_UUID(site_id), status, total_checks, succeeded_checks, failed_checks,
                   created_at, started_at, completed_at, error_code, error_message
            FROM visibility_runs WHERE tenant_id = UUID_TO_BIN(@tenant_id) AND id = UUID_TO_BIN(@run_id);
            """, cancellationToken, ("@tenant_id", tenantId.ToString()), ("@run_id", runId.ToString()));
        return row is null ? null : new VisibilityRunSummary(Guid.Parse(row[0]), Guid.Parse(row[1]), row[2], int.Parse(row[3]),
            int.Parse(row[4]), int.Parse(row[5]), ToUtc(row[6]), NullableUtc(row[7]), NullableUtc(row[8]), row[9], row[10]);
    }

    /// <inheritdoc />
    public async Task<CursorPage<VisibilityCheckView>> GetChecksAsync(Guid tenantId, Guid runId, string? cursor, int limit, string? status, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 100);
        var cursorParts = DecodeCursor(cursor);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var rows = await QueryRowsAsync(connection, null, """
            SELECT BIN_TO_UUID(c.id), BIN_TO_UUID(c.prompt_id), c.prompt_snapshot, c.engine, c.status, c.answer,
                   c.collector_node_id, c.collector_region, c.egress_country, c.collector_build_revision,
                   c.captured_at, c.error_code, c.error_message, c.created_at,
                   BIN_TO_UUID(c.prompt_version_id), c.prompt_tags_snapshot, c.branded_status_snapshot,
                   c.branded_source_snapshot, c.subject_config_version, c.snapshot_missing
            FROM visibility_checks c
            WHERE c.tenant_id = UUID_TO_BIN(@tenant_id) AND c.run_id = UUID_TO_BIN(@run_id)
              AND (@cursor_created IS NULL OR c.created_at > @cursor_created
                   OR (c.created_at = @cursor_created AND c.id > UUID_TO_BIN(@cursor_id)))
              AND (@status IS NULL OR c.status = @status)
            ORDER BY c.created_at, c.id
            LIMIT @limit;
            """, cancellationToken, ("@tenant_id", tenantId.ToString()), ("@run_id", runId.ToString()),
            ("@cursor_created", cursorParts is null ? (object)DBNull.Value : cursorParts.Value.CreatedAt.UtcDateTime),
            ("@cursor_id", cursorParts is null ? (object)DBNull.Value : cursorParts.Value.Id.ToString()),
            ("@status", (object?)status ?? DBNull.Value), ("@limit", limit + 1));
        var hasNext = rows.Count > limit;
        if (hasNext) rows.RemoveAt(rows.Count - 1);
        var items = new List<VisibilityCheckView>();
        foreach (var row in rows)
        {
            var citations = await QueryRowsAsync(connection, null, """
                SELECT BIN_TO_UUID(id), normalized_url, title, source_domain, position
                FROM citations WHERE check_id = UUID_TO_BIN(@check_id) ORDER BY position, id;
                """, cancellationToken, ("@check_id", row[0]));
            items.Add(new VisibilityCheckView(Guid.Parse(row[0]), Guid.Parse(row[1]), row[2], row[3], row[4], row[5], row[6], row[7], row[8], row[9], NullableUtc(row[10]),
                citations.Select(c => new VisibilityCitation(Guid.Parse(c[0]), c[1], c[2], c[3], int.Parse(c[4]))).ToArray(), row[11], row[12],
                Guid.TryParse(row[14], out var versionId) ? versionId : null,
                string.IsNullOrEmpty(row[15]) ? null : JsonSerializer.Deserialize<string[]>(row[15]),
                string.IsNullOrEmpty(row[16]) ? null : row[16], string.IsNullOrEmpty(row[17]) ? null : row[17],
                uint.TryParse(row[18], out var configVersion) ? configVersion : null,
                row[19] == "1" || bool.TryParse(row[19], out var missing) && missing));
        }
        var nextCursor = hasNext && items.Count > 0
            ? EncodeCursor(rows[^1][13], items[^1].CheckId)
            : null;
        return new CursorPage<VisibilityCheckView>(items, nextCursor);
    }

    /// <inheritdoc />
    public async Task<int> RecoverExpiredTasksAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var recovered = await ExecuteAsync(connection, transaction, """
            UPDATE collector_tasks
            SET status = CASE WHEN attempt_count >= 3 THEN 'terminal_failed' ELSE 'retryable_failed' END,
                lease_owner = NULL, lease_until = NULL,
                last_error_code = 'lease_expired', last_error_message = 'Collector lease expired',
                next_attempt_at = UTC_TIMESTAMP(6), updated_at = UTC_TIMESTAMP(6)
            WHERE status IN ('leased', 'collecting') AND lease_until < UTC_TIMESTAMP(6) AND channel = 'consumer_web';
            """, cancellationToken);
        await ExecuteAsync(connection, transaction, """
            UPDATE visibility_runs r
            JOIN (
                SELECT run_id,
                       COUNT(*) AS total,
                       SUM(status = 'succeeded') AS succeeded,
                       SUM(status IN ('retryable_failed', 'terminal_failed')) AS failed
                FROM collector_tasks
                WHERE channel = 'consumer_web'
                GROUP BY run_id
            ) x ON x.run_id = r.id
            SET r.status = CASE
                    WHEN x.succeeded = x.total THEN 'succeeded'
                    WHEN x.failed > 0 AND x.succeeded > 0 THEN 'partial_failed'
                    WHEN x.failed = x.total THEN 'failed'
                    ELSE 'running'
                END,
                r.succeeded_checks = x.succeeded,
                r.failed_checks = x.failed,
                r.started_at = CASE WHEN x.succeeded + x.failed > 0 THEN COALESCE(r.started_at, UTC_TIMESTAMP(6)) ELSE r.started_at END,
                r.completed_at = CASE WHEN x.succeeded + x.failed = x.total THEN COALESCE(r.completed_at, UTC_TIMESTAMP(6)) ELSE NULL END,
                r.updated_at = UTC_TIMESTAMP(6)
            WHERE r.status IN ('queued', 'running');
            """, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return recovered;
    }

    private static void ValidateCreateCommand(CreateVisibilityRunCommand command)
    {
        if (command.TenantId == Guid.Empty || command.SiteId == Guid.Empty || command.PromptIds is null || command.Platforms is null
            || command.PromptIds.Count == 0 || command.Platforms.Count == 0)
            throw new CollectorStoreException("INVALID_RUN", "Tenant, site, prompt and platform selections are required.", 400);
        if (command.PromptIds.Count != command.PromptIds.Distinct().Count())
            throw new CollectorStoreException("INVALID_RUN", "Prompt IDs must be unique.", 400);
        if (command.Platforms.Count != command.Platforms.Select(p => p.EntryCode).Distinct(StringComparer.Ordinal).Count())
            throw new CollectorStoreException("INVALID_PLATFORM_SELECTION", "Platform entries must be unique.", 400);
        if (command.IdempotencyKey is not { Length: >= 8 and <= 191 })
            throw new CollectorStoreException("INVALID_IDEMPOTENCY_KEY", "Idempotency key must be between 8 and 191 characters.", 400);
        if (command.HttpRequestHash is not null && (command.HttpRequestHash.Length != 64
            || command.HttpRequestHash.Any(c => !char.IsAsciiHexDigit(c))))
            throw new CollectorStoreException("INVALID_RUN", "HTTP 请求摘要无效。", 400);
        if (command.Platforms.Any(p => p.Language != "zh-CN" || p.Channel != "consumer_web"))
            throw new CollectorStoreException("INVALID_PLATFORM_SELECTION", "Domestic collection requires zh-CN consumer_web.", 400);
    }

    private static void ValidateLeaseCommand(CollectorLeaseCommand command)
    {
        if (command.TenantId == Guid.Empty || string.IsNullOrWhiteSpace(command.NodeId) || command.SupportedPlatformCodes is null
            || command.SupportedPlatformCodes.Count == 0)
            throw new CollectorStoreException("INVALID_LEASE", "Tenant, node and supported platforms are required.", 400);
        if (command.Capacity is < 1 or > 50 || command.LeaseSeconds is < 5 or > 900)
            throw new CollectorStoreException("INVALID_LEASE", "capacity or leaseSeconds is outside the allowed range.", 400);
    }

    /// <summary>校验结果提交并返回规范化后的命令：标题去除首尾空白、空白标题视为 null，关键词去除首尾空白。</summary>
    internal static CollectorResultCommand ValidateResultCommand(CollectorResultCommand command)
    {
        if (command.TaskId == Guid.Empty || string.IsNullOrWhiteSpace(command.NodeId) || string.IsNullOrWhiteSpace(command.AnswerText)
            || command.Citations is null)
            throw new CollectorStoreException("INVALID_RESULT", "Task, node and answer are required.", 400);
        if (Encoding.UTF8.GetByteCount(command.AnswerText) > MaxAnswerBytes || command.Citations.Count > 100)
            throw new CollectorStoreException("INVALID_RESULT", "Answer or citation payload exceeds the limit.", 400);
        if (command.IdempotencyKey is not { Length: >= 8 and <= 191 })
            throw new CollectorStoreException("INVALID_RESULT", "Idempotency key must be between 8 and 191 characters.", 400);
        if (string.IsNullOrWhiteSpace(command.CollectorBuildRevision) || command.CollectorBuildRevision.Length > 128)
            throw new CollectorStoreException("INVALID_RESULT", "Collector build revision is invalid.", 400);
        if (command.NodeId.Length > 128)
            throw new CollectorStoreException("INVALID_RESULT", "Collector node ID is invalid.", 400);
        if (command.CompletedAt.Offset != TimeSpan.Zero)
            throw new CollectorStoreException("INVALID_RESULT", "completedAt must be a UTC timestamp.", 400);
        if (!string.Equals(command.EgressCountry, "CN", StringComparison.OrdinalIgnoreCase))
            throw new CollectorStoreException("EGRESS_COUNTRY_MISMATCH", "Domestic Collector results must have CN egress.", 409);
        if (command.ProviderMetadataJson is not null && Encoding.UTF8.GetByteCount(command.ProviderMetadataJson) > MaxMetadataBytes)
            throw new CollectorStoreException("INVALID_RESULT", "Provider metadata exceeds the limit.", 400);
        if (command.ProviderMetadataJson is not null)
        {
            try { using var _ = JsonDocument.Parse(command.ProviderMetadataJson); }
            catch (JsonException) { throw new CollectorStoreException("INVALID_RESULT", "Provider metadata must be valid JSON.", 400); }
        }
        var citations = command.Citations.Select(citation =>
        {
            var title = citation.Title?.Trim();
            if (title is { Length: > MaxCitationTitleLength })
                throw new CollectorStoreException("INVALID_RESULT", $"Citation titles must be at most {MaxCitationTitleLength} characters.", 400);
            return citation with { Title = string.IsNullOrEmpty(title) ? null : title };
        }).ToArray();
        return command with { Citations = citations, SearchQueries = NormalizeSearchQueries(command.SearchQueries) };
    }

    private static string[]? NormalizeSearchQueries(IReadOnlyList<string>? queries)
    {
        if (queries is null) return null;
        if (queries.Count > MaxSearchQueries)
            throw new CollectorStoreException("INVALID_RESULT", $"At most {MaxSearchQueries} search queries are allowed.", 400);
        var normalized = new string[queries.Count];
        for (var i = 0; i < queries.Count; i++)
        {
            var query = queries[i]?.Trim();
            if (string.IsNullOrEmpty(query) || query.Length > MaxSearchQueryLength)
                throw new CollectorStoreException("INVALID_RESULT", $"Each search query must be 1-{MaxSearchQueryLength} non-blank characters.", 400);
            normalized[i] = query;
        }
        // MySQL 以 ", " 分隔数组元素，多出 n-1 个字节；按存储后的表示检查，避免触发列约束而返回 500。
        if (Encoding.UTF8.GetByteCount(SerializeSearchQueries(normalized)) + Math.Max(0, normalized.Length - 1) > MaxSearchQueriesBytes)
            throw new CollectorStoreException("INVALID_RESULT", "Search queries exceed the storage limit.", 400);
        return normalized;
    }

    private static string SerializeSearchQueries(IReadOnlyList<string> queries) => JsonSerializer.Serialize(queries, SearchQueriesJson);

    private static string ComputePayloadHash(string answer, IReadOnlyList<CollectorCitationInput> citations)
    {
        var payload = JsonSerializer.Serialize(new { answerText = answer, citations = citations.Select(c => c.Url).Order(StringComparer.Ordinal) });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private static string ComputeRunRequestHash(CreateVisibilityRunCommand command)
    {
        var payload = JsonSerializer.Serialize(new
        {
            command.SiteId,
            PromptIds = command.PromptIds,
            Platforms = command.Platforms,
            command.RequestedBy,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private static (string Url, string Domain)? NormalizeUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)) return null;
        var host = uri.Host.TrimEnd('.').ToLowerInvariant();
        if (host is "localhost" || host.EndsWith(".local", StringComparison.Ordinal) || host.EndsWith(".invalid", StringComparison.Ordinal) || host.Contains(':')) return null;
        if (uri.AbsoluteUri.Length > 750) return null;
        if (!host.Contains('.', StringComparison.Ordinal) || System.Net.IPAddress.TryParse(host, out _)) return null;
        if (host is "localhost" or "0.0.0.0" || host.EndsWith(".local", StringComparison.Ordinal)
            || host.EndsWith(".localhost", StringComparison.Ordinal) || host.EndsWith(".internal", StringComparison.Ordinal)
            || host.EndsWith(".intranet", StringComparison.Ordinal) || host.EndsWith(".invalid", StringComparison.Ordinal)) return null;
        var url = uri.AbsoluteUri;
        return (url, host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host);
    }

    private static async Task InsertSubmissionAsync(MySqlConnection c, MySqlTransaction t, CollectorResultCommand command, string hash, string disposition, CancellationToken ct)
        => await ExecuteAsync(c, t, """
            INSERT INTO collector_result_submissions
                (id, tenant_id, collector_task_id, lease_version, node_id, region, idempotency_key, payload_hash,
                 disposition, answer_text, citations, egress_country, collector_build_revision, provider_metadata, completed_at, received_at)
            VALUES
                (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant_id), UUID_TO_BIN(@task_id), @version, @node_id, @region,
                 @key, @hash, @disposition, @answer, @citations, @egress, @revision, @metadata, @completed, UTC_TIMESTAMP(6));
            """, ct, ("@id", Guid.NewGuid().ToString()), ("@tenant_id", command.TenantId.ToString()), ("@task_id", command.TaskId.ToString()),
            ("@version", command.LeaseVersion), ("@node_id", command.NodeId), ("@region", RegionCode(command.Region)), ("@key", command.IdempotencyKey),
            ("@hash", hash), ("@disposition", disposition), ("@answer", command.AnswerText), ("@citations", JsonSerializer.Serialize(command.Citations)),
            ("@egress", command.EgressCountry.ToUpperInvariant()), ("@revision", command.CollectorBuildRevision), ("@metadata", (object?)command.ProviderMetadataJson ?? DBNull.Value), ("@completed", command.CompletedAt.UtcDateTime));

    private static async Task InsertAttemptAsync(MySqlConnection c, MySqlTransaction t, CollectorResultCommand command, string eventKind, string? hash, CancellationToken ct,
        string? sessionProfileId = null)
        => await ExecuteAsync(c, t, """
            INSERT INTO collector_attempts (id, tenant_id, collector_task_id, lease_version, node_id, region, event_kind, idempotency_key, payload_hash, session_profile_id, created_at)
            VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant_id), UUID_TO_BIN(@task_id), @version, @node_id, @region, @event, @key, @hash, UUID_TO_BIN(@session_profile_id), UTC_TIMESTAMP(6));
            """, ct, ("@id", Guid.NewGuid().ToString()), ("@tenant_id", command.TenantId.ToString()), ("@task_id", command.TaskId.ToString()),
            ("@version", command.LeaseVersion), ("@node_id", command.NodeId), ("@region", RegionCode(command.Region)), ("@event", eventKind), ("@key", command.IdempotencyKey), ("@hash", (object?)hash ?? DBNull.Value),
            ("@session_profile_id", sessionProfileId));

    // 汇总只扫描本任务所属运行的任务：多表 UPDATE 会对读取到的 collector_tasks 行加锁，扫描全表会让不同运行（甚至不同租户）
    // 的结果提交与失败上报互相死锁（账号池测试并发时复现）。结果与按全表分组后再 JOIN 完全相同。
    internal static async Task UpdateRunStatusAsync(MySqlConnection c, MySqlTransaction t, Guid tenantId, Guid taskId, CancellationToken ct)
        => await ExecuteAsync(c, t, """
            UPDATE visibility_runs r
            JOIN collector_tasks ct ON ct.run_id = r.id
            JOIN (SELECT run_id, COUNT(*) total, SUM(status = 'succeeded') succeeded, SUM(status IN ('retryable_failed','terminal_failed')) failed
                  FROM collector_tasks
                  WHERE run_id = (SELECT owner.run_id FROM collector_tasks owner WHERE owner.id = UUID_TO_BIN(@task_id))
                  GROUP BY run_id) x ON x.run_id = r.id
            SET r.status = CASE WHEN x.succeeded = x.total THEN 'succeeded' WHEN x.failed > 0 AND x.succeeded > 0 THEN 'partial_failed' WHEN x.failed = x.total THEN 'failed' ELSE 'running' END,
                r.succeeded_checks = x.succeeded, r.failed_checks = x.failed, r.started_at = COALESCE(r.started_at, UTC_TIMESTAMP(6)),
                r.completed_at = CASE WHEN x.succeeded + x.failed = x.total THEN UTC_TIMESTAMP(6) ELSE r.completed_at END, r.updated_at = UTC_TIMESTAMP(6)
            WHERE r.tenant_id = UUID_TO_BIN(@tenant_id) AND ct.id = UUID_TO_BIN(@task_id);
            """, ct, ("@tenant_id", tenantId.ToString()), ("@task_id", taskId.ToString()));

    private static string RegionCode(CollectorRegion region) => region == CollectorRegion.Cn ? "cn" : "overseas";
    private static CollectorRegion ParseRegion(string value) => value == "cn" ? CollectorRegion.Cn : CollectorRegion.Overseas;
    private static DateTimeOffset ToUtc(string value) => new(DateTime.SpecifyKind(DateTime.Parse(value), DateTimeKind.Utc));
    private static DateTimeOffset? NullableUtc(string value) => string.IsNullOrEmpty(value) ? null : ToUtc(value);

    private static (DateTimeOffset CreatedAt, Guid Id)? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try
        {
            var value = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|', 2);
            if (value.Length != 2 || !DateTimeOffset.TryParse(value[0], out var createdAt) || !Guid.TryParse(value[1], out var id))
                throw new FormatException();
            return (createdAt.ToUniversalTime(), id);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or OverflowException)
        {
            throw new CollectorStoreException("INVALID_CURSOR", "The checks cursor is invalid.", 400);
        }
    }

    private static string EncodeCursor(string createdAt, Guid id)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ToUtc(createdAt):O}|{id}"));

    internal static async Task<int> ExecuteAsync(MySqlConnection c, MySqlTransaction? t, string sql, CancellationToken ct, params object[] parameters)
    {
        await using var command = new MySqlCommand(sql, c, t);
        AddParameters(command, parameters);
        return await command.ExecuteNonQueryAsync(ct);
    }

    internal static async Task<List<string[]>> QueryRowsAsync(MySqlConnection c, MySqlTransaction? t, string sql, CancellationToken ct, params object[] parameters)
    {
        await using var command = new MySqlCommand(sql, c, t);
        AddParameters(command, parameters);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<string[]>();
        while (await reader.ReadAsync(ct))
        {
            var values = new string[reader.FieldCount];
            for (var i = 0; i < values.Length; i++) values[i] = reader.IsDBNull(i) ? "" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture) ?? "";
            rows.Add(values);
        }
        return rows;
    }

    internal static async Task<string[]?> QuerySingleAsync(MySqlConnection c, MySqlTransaction? t, string sql, CancellationToken ct, params object[] parameters)
        => (await QueryRowsAsync(c, t, sql, ct, parameters)).FirstOrDefault();

    private static void AddParameters(MySqlCommand command, IEnumerable<object> parameters)
    {
        foreach (var value in parameters)
        {
            if (value is not System.Runtime.CompilerServices.ITuple tuple || tuple.Length != 2 || tuple[0] is not string name)
                throw new ArgumentException("SQL parameters must be name/value tuples.");
            command.Parameters.AddWithValue(name, tuple[1] ?? DBNull.Value);
        }
    }
}
