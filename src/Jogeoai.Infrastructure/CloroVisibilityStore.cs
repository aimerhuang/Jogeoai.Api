using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Providers.Abstractions;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

/// <summary>API 回答 Provider（Cloro、豆包、DeepSeek）共享的 Visibility Run、租约和采集证据的 MySQL 实现；类名沿用 Cloro。</summary>
public sealed class CloroVisibilityStore(MySqlDatabase database) : ICloroVisibilityStore
{
    private readonly Guid? consumptionTenantId;
    private readonly Guid? consumptionRunId;

    /// <summary>为显式运维采集限定租约领取与过期恢复范围；普通构造保持全局后台消费行为。</summary>
    /// <param name="database">业务数据库。</param>
    /// <param name="tenantId">必须提供的租户标识。</param>
    /// <param name="runId">必须提供的运行标识。</param>
    public CloroVisibilityStore(MySqlDatabase database, Guid tenantId, Guid runId) : this(database)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("必须指定消费租户。", nameof(tenantId));
        if (runId == Guid.Empty) throw new ArgumentException("必须指定消费运行。", nameof(runId));
        consumptionTenantId = tenantId;
        consumptionRunId = runId;
    }

    private const int MaxAnswerBytes = 1_048_576;
    private const int MaxRawBytes = 1_048_576;
    private static readonly HashSet<string> CheckStatuses = ["pending", "running", "succeeded", "failed", "cancelled"];
    private static readonly AnswerProviderDefinition CloroDefinition = AnswerProviderDefinition.Cloro;
    // Registered provider identifiers are code constants validated to [a-z0-9_] before being quoted into SQL.
    private static readonly string RegisteredProviderList = string.Join(", ", AnswerProviderDefinition.All
        .Select(definition => definition.Provider.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_')
            ? $"'{definition.Provider}'" : throw new InvalidOperationException("Provider 标识只允许小写字母、数字和下划线。")));
    // The answer mode is projected last (index 34); before 047 it reads as NULL (historical).
    private static string CheckProjection(bool answerModeMigrated) => $"""
        SELECT BIN_TO_UUID(c.id), BIN_TO_UUID(c.run_id), BIN_TO_UUID(c.prompt_id), c.prompt_snapshot,
               c.provider, c.engine, c.capture_mode, c.evidence_class, c.locale, c.country, c.status,
               c.answer_present, c.answer, c.search_queries, c.model_version, c.captured_at, c.duration_ms,
               c.raw_response_ref, c.raw_response_sha256, c.parser_version, c.error_code, c.error_message,
               c.created_at, t.state, t.external_task_id, BIN_TO_UUID(c.run_target_id), c.sample_index,
               BIN_TO_UUID(c.prompt_version_id), c.prompt_tags_snapshot, c.branded_status_snapshot,
               c.subject_config_version, c.snapshot_missing, c.query_availability, c.result_metadata,
               {(answerModeMigrated ? "c.answer_mode" : "NULL")}
        FROM visibility_checks c
        JOIN visibility_runs r ON r.id = c.run_id AND r.tenant_id = c.tenant_id
        LEFT JOIN provider_tasks t ON t.check_id = c.id AND t.run_id = c.run_id AND t.provider = c.provider
        WHERE r.tenant_id = UUID_TO_BIN(@tenant) AND c.provider IN ({RegisteredProviderList})
        """;

    /// <inheritdoc />
    public async Task<Guid?> GetExistingRunByKeyAsync(CreateCloroVisibilityRunCommand expected, CancellationToken cancellationToken = default)
    {
        expected = ValidateAndNormalize(expected);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var row = await QuerySingleAsync(connection, null, """
            SELECT BIN_TO_UUID(id), request_hash FROM visibility_runs
            WHERE tenant_id = UUID_TO_BIN(@tenant) AND idempotency_key = @key;
            """, cancellationToken, ("@tenant", expected.TenantId.ToString()), ("@key", expected.IdempotencyKey));
        if (row is null) return null;
        if (row[1] != ComputeRequestHash(expected))
            throw Error("RUN_IDEMPOTENCY_CONFLICT", "幂等键已用于不同的运行请求。", 409);
        return Guid.Parse(row[0]);
    }

    /// <inheritdoc />
    public async Task<VisibilityRunCreated> CreateRunAsync(CreateCloroVisibilityRunCommand command, CancellationToken cancellationToken = default)
    {
        command = ValidateAndNormalize(command);
        var requestHash = ComputeRequestHash(command);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        // Tenant lock serializes idempotency, including concurrent requests for different sites.
        var tenant = await QuerySingleAsync(connection, transaction,
            "SELECT status FROM tenants WHERE id = UUID_TO_BIN(@tenant) AND deleted_at IS NULL FOR UPDATE;",
            cancellationToken, ("@tenant", command.TenantId.ToString()));
        if (tenant is null || tenant[0] != "active") throw Error("RESOURCE_NOT_FOUND", "租户不存在或未启用。", 404);
        var existing = await QuerySingleAsync(connection, transaction, """
            SELECT BIN_TO_UUID(id), status, total_checks, created_at, request_hash, succeeded_checks, failed_checks, http_request_hash
            FROM visibility_runs WHERE tenant_id = UUID_TO_BIN(@tenant) AND idempotency_key = @key;
            """, cancellationToken, ("@tenant", command.TenantId.ToString()), ("@key", command.IdempotencyKey));
        if (existing is not null)
        {
            if (command.HttpRequestHash is not null && !string.IsNullOrEmpty(existing[7])
                ? existing[7] != command.HttpRequestHash : existing[4] != requestHash)
                throw Error("RUN_IDEMPOTENCY_CONFLICT", "幂等键已用于不同的运行请求。", 409);
            await transaction.CommitAsync(cancellationToken);
            return new VisibilityRunCreated(Guid.Parse(existing[0]), existing[1], int.Parse(existing[2]), ToUtc(existing[3]), int.Parse(existing[5]), int.Parse(existing[6]));
        }

        await GeminiResultSchema.RequireAsync(connection, transaction, cancellationToken);
        // Before 047 a run can only use default modes: checks have no column to carry another mode to the Worker.
        var answerModeMigrated = await AnswerModeSchema.IsMigratedAsync(connection, transaction, cancellationToken);
        if (!answerModeMigrated && command.Targets.Any(t => t.AnswerMode is not null)) throw AnswerModeSchema.Missing();
        var parameters = new List<(string, object?)> { ("@tenant", command.TenantId.ToString()), ("@site", command.SiteId.ToString()) };
        parameters.AddRange(command.PromptIds.Select((id, i) => ($"@p{i}", (object?)id.ToString())));
        var prompts = await QueryRowsAsync(connection, transaction, $"""
            SELECT BIN_TO_UUID(p.id), p.content, p.locale, BIN_TO_UUID(p.current_version_id),
                   COALESCE(v.branded_status, 'unbranded'), COALESCE(v.branded_source, 'system'),
                   COALESCE((SELECT JSON_ARRAYAGG(pvt.tag_name_snapshot) FROM prompt_version_tags pvt
                             WHERE pvt.prompt_version_id = p.current_version_id), JSON_ARRAY())
            FROM prompts p JOIN sites s ON s.id = p.site_id
            LEFT JOIN prompt_versions v ON v.id = p.current_version_id
            WHERE s.tenant_id = UUID_TO_BIN(@tenant) AND s.id = UUID_TO_BIN(@site) AND s.status = 'active'
              AND p.archived_at IS NULL AND p.enabled = TRUE
              AND p.id IN ({string.Join(',', command.PromptIds.Select((_, i) => $"UUID_TO_BIN(@p{i})"))})
            FOR SHARE;
            """, cancellationToken, parameters.ToArray());
        if (prompts.Count != command.PromptIds.Count) throw Error("RESOURCE_NOT_FOUND", "站点或 Prompt 不存在、未启用或不属于当前租户。", 404);
        foreach (var prompt in prompts) ValidateLocale(prompt[2]);
        var brand = await QuerySingleAsync(connection, transaction,
            "SELECT subject_config_version, target_config_version FROM site_brand_settings WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site) FOR SHARE;",
            cancellationToken, ("@tenant", command.TenantId.ToString()), ("@site", command.SiteId.ToString()));
        if (brand is null) throw Error("RESOURCE_NOT_FOUND", "品牌配置不存在。", 404);
        var configVersion = int.Parse(brand[0], CultureInfo.InvariantCulture);
        var targetConfigVersion = int.Parse(brand[1], CultureInfo.InvariantCulture);
        if (command.RequireEnabledTargets)
        {
            foreach (var target in command.Targets)
            {
                var enabled = await QuerySingleAsync(connection, transaction, """
                    SELECT enabled FROM site_targets WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site)
                    AND target_key = @target_key FOR SHARE;
                    """, cancellationToken, ("@tenant", command.TenantId.ToString()), ("@site", command.SiteId.ToString()),
                    ("@target_key", CollectionIdentity.TargetKey(target)));
                if (enabled is null || !ReadBool(enabled[0]))
                    throw Error("TARGET_NOT_ENABLED", "所选 Target 已停用，请重新预览。", 409);
            }
        }
        if (command.PreviewId is { } previewId)
        {
            var preview = await QuerySingleAsync(connection, transaction, """
                SELECT selection_hash, subject_config_version, target_config_version, expires_at, selection_snapshot FROM collection_previews
                WHERE id = UUID_TO_BIN(@preview) AND tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site)
                FOR SHARE;
                """, cancellationToken, ("@preview", previewId.ToString()), ("@tenant", command.TenantId.ToString()), ("@site", command.SiteId.ToString()));
            var selectionHash = CollectionIdentity.SelectionHash(command.PromptIds, command.Targets, command.SampleCount);
            if (preview is null || preview[0] != selectionHash || int.Parse(preview[1], CultureInfo.InvariantCulture) != configVersion
                || int.Parse(preview[2], CultureInfo.InvariantCulture) != targetConfigVersion || ToUtc(preview[3]) <= DateTimeOffset.UtcNow)
                throw Error("PREVIEW_EXPIRED", "采集预览已过期或配置已变化，请重新预览。", 409);
            using var snapshot = JsonDocument.Parse(preview[4]);
            var frozenVersions = snapshot.RootElement.GetProperty("promptVersions").EnumerateArray()
                .ToDictionary(item => Guid.Parse(item.GetProperty("promptId").GetString()!),
                    item => Guid.Parse(item.GetProperty("versionId").GetString()!));
            if (frozenVersions.Count != prompts.Count || prompts.Any(prompt => !frozenVersions.TryGetValue(Guid.Parse(prompt[0]), out var version)
                || !string.Equals(version.ToString(), prompt[3], StringComparison.OrdinalIgnoreCase)))
                throw Error("PREVIEW_EXPIRED", "Prompt 已变更，请重新预览。", 409);
        }
        var runId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        // One expansion drives total_checks, the reservation amount and the per-check credit items, so they always agree.
        // Without PromptTargetKeys this is every prompt row × every target, in the historical order.
        var promptRows = prompts.ToDictionary(p => Guid.Parse(p[0]));
        var pairs = MonitoringMatrix.Expand(prompts.Select(p => Guid.Parse(p[0])).ToArray(), command.Targets, command.PromptTargetKeys);
        var total = pairs.Count * command.SampleCount;
        var unitPrice = PromptMonitoringPricing.UnitPrice;
        var estimatedCredits = PromptMonitoringPricing.Estimate((long)total);
        await ExecuteAsync(connection, transaction, """
            INSERT INTO visibility_runs (id, tenant_id, site_id, status, idempotency_key, requested_by, requested_at,
                total_checks, request_hash, http_request_hash, subject_config_version, target_config_version, collection_preview_id, selection_snapshot, created_at, updated_at)
            VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), 'queued', @key, @by, @now, @total, @hash,
                @http_hash, @version, @target_version, UUID_TO_BIN(@preview), @selection, @now, @now);
            """, cancellationToken, ("@id", runId.ToString()), ("@tenant", command.TenantId.ToString()), ("@site", command.SiteId.ToString()),
            ("@key", command.IdempotencyKey), ("@by", command.RequestedBy), ("@now", createdAt.UtcDateTime), ("@total", total), ("@hash", requestHash),
            ("@http_hash", command.HttpRequestHash),
            ("@version", configVersion), ("@target_version", targetConfigVersion), ("@preview", command.PreviewId?.ToString()),
            ("@selection", command.PromptTargetKeys is { Count: > 0 } auditTargets
                ? JsonSerializer.Serialize(new { command.PromptIds, command.Targets, command.SampleCount, subjectConfigVersion = configVersion, promptTargets = auditTargets })
                : JsonSerializer.Serialize(new { command.PromptIds, command.Targets, command.SampleCount, subjectConfigVersion = configVersion })));
        // The monitoring reservation references the Run, so create the Run row
        // first inside the same transaction. A failed balance check still rolls
        // the whole transaction back and leaves no visible Run.
        if (command.ChargeMonitoringCredits)
            await ReserveMonitoringCreditsAsync(connection, transaction, command.TenantId, command.SiteId,
                runId, estimatedCredits, unitPrice, createdAt, cancellationToken);
        var runTargetIds = new List<Guid>();
        for (var i = 0; i < command.Targets.Count; i++)
        {
            var target = command.Targets[i];
            var targetId = Guid.NewGuid();
            runTargetIds.Add(targetId);
            var definition = RequireDefinition(target.Provider);
            await ExecuteAsync(connection, transaction, """
                INSERT INTO run_engines (id, run_id, engine, provider, request_options, sort_order, target_key,
                    capture_mode, country, locale, created_at)
                VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@run), @engine, @provider, @options, @sort, @target_key,
                    @capture, @country, @locale, @now);
                """, cancellationToken, ("@id", targetId.ToString()), ("@run", runId.ToString()), ("@engine", target.Engine),
                ("@provider", definition.Provider), ("@capture", definition.CaptureMode),
                ("@options", JsonSerializer.Serialize(new { locale = target.Locale, country = target.Country, options = new { } })),
                ("@sort", i), ("@target_key", CollectionIdentity.TargetKey(target)), ("@country", target.Country),
                ("@locale", target.Locale), ("@now", createdAt.UtcDateTime));
        }
        foreach (var pair in pairs)
        for (var sampleIndex = 1; sampleIndex <= command.SampleCount; sampleIndex++)
        {
            var prompt = promptRows[pair.PromptId];
            var targetIndex = pair.TargetIndex;
            var target = command.Targets[targetIndex];
            var definition = RequireDefinition(target.Provider);
            var checkId = Guid.NewGuid();
            var locale = target.Locale ?? prompt[2];
            var request = JsonSerializer.Serialize(new { engine = target.Engine, country = target.Country, locale, promptSha256 = Sha256(prompt[1]) });
            // New checks always record the resolved, non-null mode (target mode, or the provider default).
            await ExecuteAsync(connection, transaction, $"""
                INSERT INTO visibility_checks (id, tenant_id, site_id, run_id, prompt_id, engine, provider, status, prompt_snapshot,
                    capture_mode, evidence_class, locale, country, answer_present, parser_version, run_target_id, sample_index,
                    prompt_version_id, prompt_tags_snapshot, branded_status_snapshot, branded_source_snapshot, subject_config_version,
                    snapshot_missing, {(answerModeMigrated ? "answer_mode, " : "")}created_at, updated_at)
                VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), UUID_TO_BIN(@run), UUID_TO_BIN(@prompt),
                    @engine, @provider, 'pending', @snapshot, @capture, @evidence, @locale, @country, FALSE, @parser,
                    UUID_TO_BIN(@target), @sample, UUID_TO_BIN(@version_id), @tags, @branded, @source, @config_version,
                    FALSE, {(answerModeMigrated ? "@answer_mode, " : "")}@now, @now);
                """, cancellationToken, ("@answer_mode", AnswerModeSchema.ResolveStorage(definition, target.AnswerMode)), ("@id", checkId.ToString()), ("@tenant", command.TenantId.ToString()), ("@site", command.SiteId.ToString()),
                ("@run", runId.ToString()), ("@prompt", prompt[0]), ("@engine", target.Engine), ("@snapshot", prompt[1]),
                ("@provider", definition.Provider), ("@capture", definition.CaptureMode), ("@evidence", definition.EvidenceClass),
                ("@parser", definition.ParserVersion),
                ("@locale", locale), ("@country", target.Country), ("@now", createdAt.UtcDateTime),
                ("@target", runTargetIds[targetIndex].ToString()), ("@sample", sampleIndex), ("@version_id", prompt[3]),
                ("@tags", prompt[6]), ("@branded", prompt[4]), ("@source", prompt[5]), ("@config_version", configVersion));
            await ExecuteAsync(connection, transaction, """
                INSERT INTO provider_tasks (id, run_id, check_id, provider, operation, state, request_hash,
                    attempt_count, next_attempt_at, provider_request, created_at, updated_at)
                VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@run), UUID_TO_BIN(@check), @provider, @operation, 'pending',
                    @hash, 0, @now, @request, @now, @now);
                """, cancellationToken, ("@id", Guid.NewGuid().ToString()), ("@run", runId.ToString()), ("@check", checkId.ToString()),
                ("@provider", definition.Provider), ("@operation", TaskOperation(definition)),
                ("@hash", Sha256(request)), ("@request", request), ("@now", createdAt.UtcDateTime));
            if (command.ChargeMonitoringCredits)
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO monitoring_credit_items
                        (id, tenant_id, site_id, reservation_id, run_id, check_id, amount, status, created_at)
                    SELECT UUID_TO_BIN(@id), tenant_id, site_id, id, UUID_TO_BIN(@run), UUID_TO_BIN(@check),
                           @amount, 'held', @now
                    FROM monitoring_credit_reservations
                    WHERE tenant_id = UUID_TO_BIN(@tenant) AND run_id = UUID_TO_BIN(@run);
                    """, cancellationToken, ("@id", Guid.NewGuid().ToString()), ("@tenant", command.TenantId.ToString()),
                    ("@run", runId.ToString()), ("@check", checkId.ToString()), ("@amount", unitPrice),
                    ("@now", createdAt.UtcDateTime));
        }
        // No outbox consumer exists in this repository. Cloro-only runs keep the historical event name and payload;
        // a run containing any other API provider (doubao_api) is published as visibility.run.created with the same
        // dedupe key shape so a later retry of the same run never emits a second event.
        var providers = command.Targets.Select(t => RequireDefinition(t.Provider).Provider).Distinct(StringComparer.Ordinal).ToArray();
        var cloroOnly = providers.Length == 1 && providers[0] == CloroDefinition.Provider;
        await ExecuteAsync(connection, transaction, """
            INSERT INTO outbox_events (id, tenant_id, event_type, aggregate_type, aggregate_id, dedupe_key, payload, status,
                attempt_count, available_at, created_at, updated_at)
            VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), @event, 'visibility_run', UUID_TO_BIN(@run),
                @dedupe, @payload, 'pending', 0, @now, @now, @now);
            """, cancellationToken, ("@id", Guid.NewGuid().ToString()), ("@tenant", command.TenantId.ToString()), ("@run", runId.ToString()),
            ("@event", cloroOnly ? "cloro.run.created" : "visibility.run.created"),
            ("@dedupe", $"cloro-run:{runId}"), ("@payload", cloroOnly
                ? JsonSerializer.Serialize(new { runId, provider = "cloro", totalChecks = total })
                : JsonSerializer.Serialize(new { runId, provider = providers.Length == 1 ? providers[0] : "mixed", providers, totalChecks = total })),
            ("@now", createdAt.UtcDateTime));
        await transaction.CommitAsync(cancellationToken);
        return new VisibilityRunCreated(runId, "queued", total, createdAt);
    }

    /// <inheritdoc />
    public async Task<VisibilityRunSummary?> GetRunAsync(Guid tenantId, Guid runId, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var row = await QuerySingleAsync(connection, null, """
            SELECT BIN_TO_UUID(id), BIN_TO_UUID(site_id), status, total_checks, succeeded_checks, failed_checks,
                   created_at, started_at, completed_at, error_code, error_message
            FROM visibility_runs WHERE tenant_id = UUID_TO_BIN(@tenant) AND id = UUID_TO_BIN(@run);
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@run", runId.ToString()));
        return row is null ? null : new VisibilityRunSummary(Guid.Parse(row[0]), Guid.Parse(row[1]), row[2], int.Parse(row[3]), int.Parse(row[4]),
            int.Parse(row[5]), ToUtc(row[6]), NullableUtc(row[7]), NullableUtc(row[8]), NullIfEmpty(row[9]), NullIfEmpty(row[10]));
    }

    /// <inheritdoc />
    public async Task<string?> GetRunProviderAsync(Guid tenantId, Guid runId, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var row = await QuerySingleAsync(connection, null, """
            SELECT e.provider FROM run_engines e JOIN visibility_runs r ON r.id = e.run_id
            WHERE r.tenant_id = UUID_TO_BIN(@tenant) AND r.id = UUID_TO_BIN(@run) ORDER BY e.sort_order LIMIT 1;
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@run", runId.ToString()));
        return row is null ? null : row[0];
    }

    /// <inheritdoc />
    public async Task<CursorPage<CloroVisibilityCheckView>> GetChecksAsync(Guid tenantId, Guid runId, string? cursor, int limit, string? engine, string? status, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100) throw Error("INVALID_LIMIT", "limit 必须在 1 到 100 之间。", 400);
        engine = NullIfEmpty(engine?.Trim().ToLowerInvariant() ?? "");
        status = NullIfEmpty(status?.Trim().ToLowerInvariant() ?? "");
        if (engine is not null && !AnswerProviderDefinition.All.Any(definition => definition.SupportsEngine(engine)))
            throw Error("UNSUPPORTED_ENGINE", "引擎不受支持。", 400);
        if (status is not null && !CheckStatuses.Contains(status)) throw Error("INVALID_STATUS", "检查状态无效。", 400);
        var scope = Sha256(JsonSerializer.Serialize(new { tenantId, runId, provider = "cloro", engine, status }));
        var decoded = DecodeCursor(cursor, scope);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await GeminiResultSchema.RequireAsync(connection, null, cancellationToken);
        var sql = new StringBuilder(CheckProjection(await AnswerModeSchema.IsMigratedAsync(connection, null, cancellationToken)))
            .Append(" AND c.run_id = UUID_TO_BIN(@run)");
        var parameters = new List<(string, object?)> { ("@tenant", tenantId.ToString()), ("@run", runId.ToString()), ("@limit", limit + 1) };
        if (engine is not null) { sql.Append(" AND c.engine = @engine"); parameters.Add(("@engine", engine)); }
        if (status is not null) { sql.Append(" AND c.status = @status"); parameters.Add(("@status", status)); }
        if (decoded is not null)
        {
            sql.Append(" AND (c.created_at > @created OR (c.created_at = @created AND c.id > UUID_TO_BIN(@id)))");
            parameters.Add(("@created", decoded.CreatedAt.UtcDateTime));
            parameters.Add(("@id", decoded.Id.ToString()));
        }
        sql.Append(" ORDER BY c.created_at, c.id LIMIT @limit;");
        var rows = await QueryRowsAsync(connection, null, sql.ToString(), cancellationToken, parameters.ToArray());
        var items = new List<CloroVisibilityCheckView>();
        foreach (var row in rows.Take(limit)) items.Add(await ReadViewAsync(connection, row, cancellationToken));
        var next = rows.Count > limit ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new CheckCursor(ToUtc(rows[limit - 1][22]), Guid.Parse(rows[limit - 1][0]), scope))) : null;
        return new CursorPage<CloroVisibilityCheckView>(items, next);
    }

    /// <inheritdoc />
    public async Task<CloroVisibilityCheckView?> GetCheckAsync(Guid tenantId, Guid checkId, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await GeminiResultSchema.RequireAsync(connection, null, cancellationToken);
        var row = await QuerySingleAsync(connection, null,
            CheckProjection(await AnswerModeSchema.IsMigratedAsync(connection, null, cancellationToken)) + " AND c.id = UUID_TO_BIN(@id);", cancellationToken,
            ("@tenant", tenantId.ToString()), ("@id", checkId.ToString()));
        return row is null ? null : await ReadViewAsync(connection, row, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CloroTaskLease>> LeaseTasksAsync(string workerId, int limit = 1, int leaseSeconds = 90,
        string provider = "cloro", CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workerId) || workerId.Length > 128 || limit is < 1 or > 10 || leaseSeconds is < 10 or > 900)
            throw new ArgumentException("任务租约参数无效。");
        var definition = RequireDefinition(provider);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await GeminiResultSchema.RequireAsync(connection, null, cancellationToken);
        // Cloro leases nothing until 022 exists, so no task can enter submitting without a key pool.
        // Synchronous API providers (doubao_api) do not use the pool and are leased without it.
        if (definition.Provider == CloroDefinition.Provider)
            await CloroKeyPoolSchema.RequireAsync(connection, null, cancellationToken);
        // Historical checks (NULL, or any check before 047) are collected with the provider default mode.
        var answerModeMigrated = await AnswerModeSchema.IsMigratedAsync(connection, null, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var rows = await QueryRowsAsync(connection, transaction, $"""
            SELECT BIN_TO_UUID(t.id), BIN_TO_UUID(c.tenant_id), BIN_TO_UUID(t.run_id), BIN_TO_UUID(t.check_id),
                   c.prompt_snapshot, c.engine, c.locale, c.country, t.state, t.external_task_id,
                   t.attempt_count, t.lease_version, t.created_at, t.submitted_at, t.started_at,
                   BIN_TO_UUID(t.cloro_key_id), t.credit_resubmit_count, t.pool_exhausted_since,
                   {(answerModeMigrated ? "c.answer_mode" : "NULL")}
            FROM provider_tasks t JOIN visibility_checks c ON c.id = t.check_id AND c.run_id = t.run_id
            WHERE t.provider = @provider AND t.state IN ('pending','retryable_failed','queued','running')
              AND t.next_attempt_at <= UTC_TIMESTAMP(6) AND t.lease_owner IS NULL AND t.lease_until IS NULL
              AND c.status IN ('pending','running')
              AND (@scopeRun IS NULL OR (t.run_id = UUID_TO_BIN(@scopeRun) AND c.tenant_id = UUID_TO_BIN(@scopeTenant)))
            ORDER BY t.next_attempt_at, t.id LIMIT @limit FOR UPDATE SKIP LOCKED;
            """, cancellationToken, ("@provider", definition.Provider), ("@limit", limit), ("@scopeRun", consumptionRunId?.ToString()),
            ("@scopeTenant", consumptionTenantId?.ToString()));
        var result = new List<CloroTaskLease>();
        foreach (var row in rows)
        {
            var until = DateTimeOffset.UtcNow.AddSeconds(leaseSeconds);
            await ExecuteAsync(connection, transaction, """
                UPDATE provider_tasks SET state = 'leased', lease_owner = @worker, lease_until = @until,
                    lease_version = lease_version + 1, attempt_count = attempt_count + 1, updated_at = UTC_TIMESTAMP(6)
                WHERE id = UUID_TO_BIN(@id);
                """, cancellationToken, ("@worker", workerId), ("@until", until.UtcDateTime), ("@id", row[0]));
            result.Add(new CloroTaskLease(Guid.Parse(row[0]), Guid.Parse(row[1]), Guid.Parse(row[2]), Guid.Parse(row[3]), row[4], row[5], row[6], row[7],
                row[8], NullIfEmpty(row[9]), int.Parse(row[10]) + 1, until, int.Parse(row[11]) + 1, ToUtc(row[12]), NullableUtc(row[13]), NullableUtc(row[14]),
                Guid.TryParse(row[15], out var keyId) ? keyId : null, int.Parse(row[16], CultureInfo.InvariantCulture), NullableUtc(row[17]),
                AnswerModeSchema.ResolveStorage(definition, NullIfEmpty(row[18]))));
        }
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    /// <inheritdoc />
    public async Task MarkSubmittingAsync(CloroTaskLease task, string workerId, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var row = await LockTaskAsync(connection, transaction, task, workerId, cancellationToken);
        if (!string.IsNullOrEmpty(row[1]) || row[0] != CloroTaskStates.Leased) throw StaleLease();
        await ExecuteAsync(connection, transaction, "UPDATE provider_tasks SET state = 'submitting', updated_at = UTC_TIMESTAMP(6) WHERE id = UUID_TO_BIN(@id);",
            cancellationToken, ("@id", task.TaskId.ToString()));
        await MarkRunningAsync(connection, transaction, task, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task RecordSubmissionAsync(CloroTaskLease task, string workerId, CloroSubmittedTask submitted, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(submitted.ExternalTaskId) || submitted.ExternalTaskId.Length > 255) throw Error("INVALID_PROVIDER_RESPONSE", "外部任务标识无效。", 502);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var row = await LockTaskAsync(connection, transaction, task, workerId, cancellationToken);
        if (row[0] != CloroTaskStates.Submitting || !string.IsNullOrEmpty(row[1])) throw StaleLease();
        await ExecuteAsync(connection, transaction, """
            UPDATE provider_tasks SET external_task_id = @external, state = 'queued', submitted_at = @submitted,
                cloro_key_id = UUID_TO_BIN(@key), pool_exhausted_since = NULL,
                lease_owner = NULL, lease_until = NULL, next_attempt_at = UTC_TIMESTAMP(6),
                last_error_code = NULL, last_error_message = NULL, updated_at = UTC_TIMESTAMP(6)
            WHERE id = UUID_TO_BIN(@id);
            """, cancellationToken, ("@external", submitted.ExternalTaskId), ("@submitted", submitted.SubmittedAt.UtcDateTime),
            ("@key", submitted.KeyId?.ToString()), ("@id", task.TaskId.ToString()));
        // After a credits resubmission the item is already captured; this call is then a no-op.
        await CaptureMonitoringItemAsync(connection, transaction, task, cancellationToken);
        await InsertUsageAsync(connection, transaction, task, CloroDefinition, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task RecordPollAsync(CloroTaskLease task, string workerId, CloroTaskStatusSnapshot snapshot, TimeSpan delay, CancellationToken cancellationToken = default)
    {
        var running = snapshot.Status is "RUNNING" or "PROCESSING" or "IN_PROGRESS";
        var response = BoundProviderResponse(snapshot.Payload.GetRawText());
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var row = await LockTaskAsync(connection, transaction, task, workerId, cancellationToken);
        if (row[1] != snapshot.ExternalTaskId || string.IsNullOrEmpty(row[1])) throw StaleLease();
        await ExecuteAsync(connection, transaction, """
            UPDATE provider_tasks SET state = @state, last_polled_at = UTC_TIMESTAMP(6),
                started_at = CASE WHEN @running THEN COALESCE(started_at, UTC_TIMESTAMP(6)) ELSE started_at END,
                provider_response = @response, lease_owner = NULL, lease_until = NULL, next_attempt_at = @next,
                last_error_code = NULL, last_error_message = NULL, updated_at = UTC_TIMESTAMP(6)
            WHERE id = UUID_TO_BIN(@id);
            """, cancellationToken, ("@state", running ? CloroTaskStates.Running : CloroTaskStates.Queued), ("@running", running),
            ("@response", response),
            ("@next", DateTime.UtcNow.AddMilliseconds(Math.Clamp(delay.TotalMilliseconds, 100, 60_000))), ("@id", task.TaskId.ToString()));
        await MarkRunningAsync(connection, transaction, task, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task CompleteTaskAsync(CloroTaskLease task, string workerId, CloroAnswerSnapshot answer, int durationMs, CancellationToken cancellationToken = default)
    {
        var definition = AnswerProviderDefinition.TryGet(answer.Provider) ?? CloroDefinition;
        var prepared = PrepareAnswer(answer);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await GeminiResultSchema.RequireAsync(connection, null, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var row = await LockTaskAsync(connection, transaction, task, workerId, cancellationToken);
        if (string.IsNullOrEmpty(row[1]) || row[0] != CloroTaskStates.Leased) throw StaleLease();
        await WriteCompletionAsync(connection, transaction, task, answer, prepared, durationMs, definition,
            "JSON_OBJECT('status', 'succeeded')", null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task CompleteSynchronousTaskAsync(CloroTaskLease task, string workerId, CloroAnswerSnapshot answer, long durationMs,
        string externalId, CancellationToken cancellationToken = default)
    {
        var definition = AnswerProviderDefinition.TryGet(answer.Provider)
            ?? throw Error("INVALID_PROVIDER_RESPONSE", "回答的 Provider 未注册。", 502);
        if (definition.Provider == CloroDefinition.Provider)
            throw Error("INVALID_TASK_STATE", "Cloro 任务必须经过提交与轮询，不能同步完成。", 409);
        if (string.IsNullOrWhiteSpace(externalId) || externalId.Length > 255) throw Error("INVALID_PROVIDER_RESPONSE", "外部任务标识无效。", 502);
        var prepared = PrepareAnswer(answer);
        // The usage summary (tokens, search calls) belongs to the task's provider response, not to result_metadata:
        // ResultQualityCodec is strict and must stay byte-compatible with the existing quality shape.
        var providerResponse = BoundProviderResponse(JsonSerializer.Serialize(new
        {
            status = "succeeded",
            externalId,
            usage = UsageSummary(answer.RawOutput),
        }));
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await GeminiResultSchema.RequireAsync(connection, null, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var row = await LockTaskAsync(connection, transaction, task, workerId, cancellationToken);
        // A synchronous call has no external ID before it completes; leased or submitting are the only valid stages.
        if (!string.IsNullOrEmpty(row[1]) || row[0] is not (CloroTaskStates.Leased or CloroTaskStates.Submitting)) throw StaleLease();
        await ExecuteAsync(connection, transaction, """
            UPDATE provider_tasks SET external_task_id = @external, submitted_at = COALESCE(submitted_at, UTC_TIMESTAMP(6)),
                started_at = COALESCE(started_at, UTC_TIMESTAMP(6)), updated_at = UTC_TIMESTAMP(6)
            WHERE id = UUID_TO_BIN(@id);
            """, cancellationToken, ("@external", externalId), ("@id", task.TaskId.ToString()));
        // Same credit and usage semantics as RecordSubmissionAsync: the call was made, so the monitoring item is captured.
        await CaptureMonitoringItemAsync(connection, transaction, task, cancellationToken);
        await WriteCompletionAsync(connection, transaction, task, answer, prepared, (int)Math.Clamp(durationMs, 0, int.MaxValue),
            definition, "CAST(@response AS JSON)", providerResponse, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    // Numeric leaves of the provider's usage object (input_tokens, output_tokens_details.reasoning_tokens, tool_usage.web_search, …).
    // Only numbers under lower-case snake_case keys are copied, so the summary can hold no credential or response text; note that
    // SanitizeProviderJson would drop every "*tokens" key, which is why the usage is projected here instead of sanitized.
    internal static IReadOnlyDictionary<string, double>? UsageSummary(JsonElement rawOutput)
    {
        if (rawOutput.ValueKind != JsonValueKind.Object || !rawOutput.TryGetProperty("usage", out var usage)
            || usage.ValueKind != JsonValueKind.Object) return null;
        var summary = new SortedDictionary<string, double>(StringComparer.Ordinal);
        Collect(usage, "", 0);
        return summary.Count == 0 ? null : summary;

        void Collect(JsonElement element, string prefix, int depth)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (summary.Count >= 32) return;
                var name = property.Name;
                if (name.Length is < 1 or > 64 || !name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_')) continue;
                var key = prefix.Length == 0 ? name : $"{prefix}.{name}";
                if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDouble(out var number)
                    && double.IsFinite(number)) summary[key] = number;
                else if (property.Value.ValueKind == JsonValueKind.Object && depth < 2) Collect(property.Value, key, depth + 1);
            }
        }
    }

    private sealed record PreparedAnswer(string Text, string? Metadata, string? RawStore, string RawHash, string Queries);

    private static PreparedAnswer PrepareAnswer(CloroAnswerSnapshot answer)
    {
        var text = TruncateUtf8(answer.AnswerText, MaxAnswerBytes);
        var metadata = ResultQualityCodec.Encode(answer, text);
        var raw = SanitizeProviderJson(answer.RawOutput);
        var rawHash = Sha256(raw);
        var rawStore = Encoding.UTF8.GetByteCount(raw) <= MaxRawBytes ? raw : null;
        return new PreparedAnswer(text, metadata, rawStore, rawHash, JsonSerializer.Serialize(answer.WebQueries));
    }

    // Shared by the Cloro poll completion and the synchronous completion: result columns, citations, usage,
    // task terminal state, run aggregate and the analysis work item, all inside the caller's transaction.
    private static async Task WriteCompletionAsync(MySqlConnection connection, MySqlTransaction transaction, CloroTaskLease task,
        CloroAnswerSnapshot answer, PreparedAnswer prepared, int durationMs, AnswerProviderDefinition definition,
        string providerResponseSql, string? providerResponse, CancellationToken cancellationToken)
    {
        var (text, metadata, rawStore, rawHash, queries) = prepared;
        // MySQL's JSON representation includes whitespace; check the stored representation as well.
        await ExecuteAsync(connection, transaction, """
            UPDATE visibility_checks SET status = 'succeeded', model_version = @model, answer_present = @present,
                answer = @answer, search_queries = @queries, query_availability = @queryAvailability, result_metadata = @metadata,
                raw_response = CASE WHEN OCTET_LENGTH(CAST(@raw AS JSON)) <= 1048576 THEN @raw ELSE NULL END,
                raw_response_ref = CASE WHEN @raw IS NULL OR OCTET_LENGTH(CAST(@raw AS JSON)) > 1048576 THEN @ref ELSE NULL END,
                raw_response_sha256 = @hash, parser_version = @parser, result_hash = @hash,
                duration_ms = @duration, captured_at = @captured, error_code = NULL, error_message = NULL, updated_at = UTC_TIMESTAMP(6)
            WHERE id = UUID_TO_BIN(@check);
            """, cancellationToken, ("@model", answer.ModelVersion), ("@present", !string.IsNullOrWhiteSpace(text)), ("@answer", text), ("@queries", queries),
            ("@metadata", metadata), ("@queryAvailability", answer.QueryAvailability), ("@raw", rawStore), ("@ref", $"sha256:{rawHash}"), ("@hash", rawHash), ("@parser", answer.ParserVersion),
            ("@duration", Math.Max(0, durationMs)), ("@captured", answer.CapturedAt.UtcDateTime), ("@check", task.CheckId.ToString()));
        foreach (var citation in answer.Citations)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO citations (id, check_id, normalized_url, title, source_domain, position, created_at)
                VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@check), @url, @title, @domain, @position, UTC_TIMESTAMP(6));
                """, cancellationToken, ("@id", Guid.NewGuid().ToString()), ("@check", task.CheckId.ToString()), ("@url", citation.Url),
                ("@title", citation.Title), ("@domain", citation.Domain), ("@position", citation.CitationIndex));
        }
        await InsertUsageAsync(connection, transaction, task, definition, cancellationToken);
        // providerResponseSql is one of two code constants (JSON_OBJECT literal or CAST(@response AS JSON)); never caller input.
        await ExecuteAsync(connection, transaction, $"""
            UPDATE provider_tasks SET state = 'succeeded', completed_at = UTC_TIMESTAMP(6), last_polled_at = UTC_TIMESTAMP(6),
                provider_response = {providerResponseSql}, lease_owner = NULL, lease_until = NULL,
                last_error_code = NULL, last_error_message = NULL, updated_at = UTC_TIMESTAMP(6) WHERE id = UUID_TO_BIN(@id);
            """, cancellationToken, providerResponse is null
                ? [("@id", task.TaskId.ToString())]
                : [("@id", task.TaskId.ToString()), ("@response", providerResponse)]);
        await UpdateRunStatusAsync(connection, transaction, task.RunId, cancellationToken);
        await AnalysisEnqueue.QueueAsync(connection, transaction, task.CheckId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task FailTaskAsync(CloroTaskLease task, string workerId, string errorCode, bool retryable, TimeSpan? retryAfter = null,
        CancellationToken cancellationToken = default)
    {
        // The historical fixed delay stays the default; providers with their own backoff pass retryAfter (1 s – 24 h).
        var retrySeconds = retryAfter is { } delay ? (int)Math.Clamp(delay.TotalSeconds, 1, 86_400) : 10;
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var locked = await LockTaskAsync(connection, transaction, task, workerId, cancellationToken);
        var unknown = errorCode == "CLORO_SUBMISSION_UNKNOWN";
        retryable = retryable && !unknown;
        var state = retryable ? CloroTaskStates.RetryableFailed : unknown ? CloroTaskStates.SubmissionUnknown : CloroTaskStates.TerminalFailed;
        // A submission timeout can still have created a billable external task.
        // Record the attempted call before moving the task to manual review; the
        // usage reference is idempotent with the successful-submit path.
        if (unknown)
        {
            await InsertUsageAsync(connection, transaction, task, CloroDefinition, cancellationToken);
            await MarkMonitoringItemUnknownAsync(connection, transaction, task.TenantId, task.CheckId,
                SafeErrorMessage(errorCode), cancellationToken);
        }
        else if (!retryable && string.IsNullOrWhiteSpace(locked[1]))
            await ReleaseMonitoringItemAsync(connection, transaction, task, SafeErrorMessage(errorCode), cancellationToken);
        await ExecuteAsync(connection, transaction, """
            UPDATE provider_tasks SET state = @state, last_error_code = @code, last_error_message = @message,
                next_attempt_at = TIMESTAMPADD(SECOND, @retrySeconds, UTC_TIMESTAMP(6)), lease_owner = NULL, lease_until = NULL,
                completed_at = CASE WHEN @retry THEN NULL ELSE UTC_TIMESTAMP(6) END, updated_at = UTC_TIMESTAMP(6)
            WHERE id = UUID_TO_BIN(@id);
            UPDATE visibility_checks SET status = CASE WHEN @retry THEN 'running' ELSE 'failed' END,
                error_code = @code, error_message = @message, updated_at = UTC_TIMESTAMP(6) WHERE id = UUID_TO_BIN(@check);
            """, cancellationToken, ("@state", state), ("@code", SafeErrorCode(errorCode)), ("@message", SafeErrorMessage(errorCode)),
            ("@retrySeconds", retrySeconds), ("@retry", retryable), ("@id", task.TaskId.ToString()), ("@check", task.CheckId.ToString()));
        await UpdateRunStatusAsync(connection, transaction, task.RunId, cancellationToken);
        if (!retryable)
            await AnalysisEnqueue.QueueAsync(connection, transaction, task.CheckId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeferTaskAsync(CloroTaskLease task, string workerId, string errorCode, DateTimeOffset nextAttemptAt,
        bool creditsExhausted, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var locked = await LockTaskAsync(connection, transaction, task, workerId, cancellationToken);
        // Only a task that never reached Cloro can wait for the pool; an external ID must keep polling.
        if (!string.IsNullOrEmpty(locked[1])) throw Error("INVALID_TASK_STATE", "已提交的 Cloro 任务不能因 key 池耗尽而延后。", 409);
        // The monitoring reservation stays held; FailTaskAsync releases it once the maximum wait has passed.
        // A busy or unreadable pool leaves the exhaustion clock untouched.
        await ExecuteAsync(connection, transaction, """
            UPDATE provider_tasks SET state = 'retryable_failed', last_error_code = @code, last_error_message = @message,
                next_attempt_at = GREATEST(@next, UTC_TIMESTAMP(6)),
                pool_exhausted_since = IF(@exhausted, COALESCE(pool_exhausted_since, UTC_TIMESTAMP(6)), pool_exhausted_since),
                lease_owner = NULL, lease_until = NULL, completed_at = NULL, updated_at = UTC_TIMESTAMP(6)
            WHERE id = UUID_TO_BIN(@id);
            UPDATE visibility_checks SET status = 'running', error_code = @code, error_message = @message,
                updated_at = UTC_TIMESTAMP(6) WHERE id = UUID_TO_BIN(@check);
            """, cancellationToken, ("@code", SafeErrorCode(errorCode)), ("@message", SafeErrorMessage(errorCode)),
            ("@next", nextAttemptAt.UtcDateTime), ("@exhausted", creditsExhausted), ("@id", task.TaskId.ToString()),
            ("@check", task.CheckId.ToString()));
        await UpdateRunStatusAsync(connection, transaction, task.RunId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> ResetForResubmissionAsync(CloroTaskLease task, string workerId, int maxResubmits,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var locked = await LockTaskAsync(connection, transaction, task, workerId, cancellationToken);
        if (string.IsNullOrEmpty(locked[1]) || locked[0] != CloroTaskStates.Leased) throw StaleLease();
        // Keep the old external ID in the audit trail before clearing it. Without a key row to attach
        // it to (pre-pool task and no default account) the task is not resubmitted.
        var audited = await ExecuteAsync(connection, transaction, """
            INSERT INTO cloro_key_events (id, key_id, provider_task_id, event_type, error_code, external_task_id,
                credits_remaining, created_at)
            SELECT UUID_TO_BIN(@event), k.id, t.id, 'resubmitted', 'INSUFFICIENT_CREDITS', t.external_task_id,
                   k.credits_remaining, UTC_TIMESTAMP(6)
            FROM provider_tasks t
            JOIN cloro_api_keys k ON k.id = COALESCE(t.cloro_key_id,
                (SELECT d.id FROM cloro_api_keys d WHERE d.account_label = @default))
            WHERE t.id = UUID_TO_BIN(@id) AND t.external_task_id IS NOT NULL AND t.credit_resubmit_count < @max;
            """, cancellationToken, ("@event", Guid.NewGuid().ToString()), ("@default", CloroKeyFormat.DefaultAccountLabel),
            ("@id", task.TaskId.ToString()), ("@max", Math.Max(0, maxResubmits)));
        if (audited != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }
        // Cloro does not bill FAILED tasks. The monitoring item stays captured and the usage reference
        // cloro:{taskId} is idempotent, so the next RecordSubmissionAsync charges nothing again.
        await ExecuteAsync(connection, transaction, """
            UPDATE provider_tasks SET external_task_id = NULL, submitted_at = NULL, started_at = NULL, cloro_key_id = NULL,
                credit_resubmit_count = credit_resubmit_count + 1, state = 'pending', provider_response = NULL,
                last_polled_at = NULL, last_error_code = NULL, last_error_message = NULL, next_attempt_at = UTC_TIMESTAMP(6),
                lease_owner = NULL, lease_until = NULL, completed_at = NULL, updated_at = UTC_TIMESTAMP(6)
            WHERE id = UUID_TO_BIN(@id);
            UPDATE visibility_checks SET status = 'running', error_code = NULL, error_message = NULL,
                updated_at = UTC_TIMESTAMP(6) WHERE id = UUID_TO_BIN(@check);
            """, cancellationToken, ("@id", task.TaskId.ToString()), ("@check", task.CheckId.ToString()));
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<int> RecoverExpiredTasksAsync(string provider = "cloro", CancellationToken cancellationToken = default)
    {
        var definition = RequireDefinition(provider);
        // Only Cloro has a "submitted but unconfirmed" stage. A synchronous provider whose lease expired mid-call simply
        // goes back to pending; if the late response still arrives, its fenced lease makes the completion a no-op.
        var canBeUnknown = definition.Provider == CloroDefinition.Provider;
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        // Read candidates first, then lock each Run before its task, just like completion/failure.
        var candidates = await QueryRowsAsync(connection, null, """
            SELECT BIN_TO_UUID(id), BIN_TO_UUID(run_id) FROM provider_tasks
            WHERE provider = @provider AND state IN ('leased','submitting','queued','running') AND lease_until < UTC_TIMESTAMP(6)
              AND (@scopeRun IS NULL OR (run_id = UUID_TO_BIN(@scopeRun) AND EXISTS
                  (SELECT 1 FROM visibility_runs r WHERE r.id = provider_tasks.run_id AND r.tenant_id = UUID_TO_BIN(@scopeTenant))))
            ORDER BY run_id, id LIMIT 100;
            """, cancellationToken, ("@provider", definition.Provider), ("@scopeRun", consumptionRunId?.ToString()),
            ("@scopeTenant", consumptionTenantId?.ToString()));
        var recovered = 0;
        foreach (var candidate in candidates)
        {
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            await QuerySingleAsync(connection, transaction, "SELECT id FROM visibility_runs WHERE id = UUID_TO_BIN(@run) FOR UPDATE;", cancellationToken, ("@run", candidate[1]));
            var task = await QuerySingleAsync(connection, transaction, """
                SELECT t.state, t.external_task_id, BIN_TO_UUID(t.check_id), BIN_TO_UUID(r.tenant_id)
                FROM provider_tasks t JOIN visibility_runs r ON r.id = t.run_id
                WHERE t.id = UUID_TO_BIN(@id) AND t.lease_until < UTC_TIMESTAMP(6)
                  AND t.state IN ('leased','submitting','queued','running') FOR UPDATE;
                """, cancellationToken, ("@id", candidate[0]));
            if (task is not null)
            {
                var unknown = canBeUnknown && task[0] == CloroTaskStates.Submitting && string.IsNullOrEmpty(task[1]);
                await ExecuteAsync(connection, transaction, """
                    UPDATE provider_tasks SET state = @state, lease_owner = NULL, lease_until = NULL,
                        last_error_code = @code, last_error_message = @message,
                        completed_at = CASE WHEN @unknown THEN UTC_TIMESTAMP(6) ELSE completed_at END,
                        next_attempt_at = UTC_TIMESTAMP(6), updated_at = UTC_TIMESTAMP(6) WHERE id = UUID_TO_BIN(@id);
                    """, cancellationToken, ("@state", unknown ? CloroTaskStates.SubmissionUnknown : string.IsNullOrEmpty(task[1]) ? CloroTaskStates.Pending : CloroTaskStates.Queued),
                    ("@code", unknown ? "CLORO_SUBMISSION_UNKNOWN" : null), ("@message", unknown ? SafeErrorMessage("CLORO_SUBMISSION_UNKNOWN") : null),
                    ("@unknown", unknown), ("@id", candidate[0]));
                if (unknown)
                {
                    await InsertUsageAsync(connection, transaction, Guid.Parse(task[3]), Guid.Parse(candidate[1]), Guid.Parse(candidate[0]),
                        CloroDefinition, cancellationToken);
                    await MarkMonitoringItemUnknownAsync(connection, transaction, Guid.Parse(task[3]),
                        Guid.Parse(task[2]), SafeErrorMessage("CLORO_SUBMISSION_UNKNOWN"), cancellationToken);
                    await ExecuteAsync(connection, transaction, """
                        UPDATE visibility_checks SET status = 'failed', error_code = 'CLORO_SUBMISSION_UNKNOWN',
                            error_message = @message, updated_at = UTC_TIMESTAMP(6) WHERE id = UUID_TO_BIN(@check);
                        """, cancellationToken, ("@message", SafeErrorMessage("CLORO_SUBMISSION_UNKNOWN")), ("@check", task[2]));
                    await UpdateRunStatusAsync(connection, transaction, Guid.Parse(candidate[1]), cancellationToken);
                    await AnalysisEnqueue.QueueAsync(connection, transaction, Guid.Parse(task[2]), cancellationToken);
                }
                recovered++;
            }
            await transaction.CommitAsync(cancellationToken);
        }
        return recovered;
    }

    /// <inheritdoc />
    public async Task<int> RecoverTimedOutTasksAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        if (runId == Guid.Empty) throw new ArgumentException("必须指定要修复的运行。", nameof(runId));
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        // Match the run -> task lock order used by normal task completion.
        var run = await QuerySingleAsync(connection, transaction,
            "SELECT status FROM visibility_runs WHERE id = UUID_TO_BIN(@run) FOR UPDATE;",
            cancellationToken, ("@run", runId.ToString()));
        if (run is null || run[0] == "cancelled")
        {
            await transaction.CommitAsync(cancellationToken);
            return 0;
        }
        var candidates = await QueryRowsAsync(connection, transaction, """
            SELECT BIN_TO_UUID(t.id), BIN_TO_UUID(t.check_id)
            FROM provider_tasks t
            JOIN visibility_checks c ON c.id = t.check_id AND c.run_id = t.run_id
            WHERE t.run_id = UUID_TO_BIN(@run) AND t.provider = @provider AND t.state = 'terminal_failed'
              AND t.external_task_id IS NOT NULL AND TRIM(t.external_task_id) <> ''
              AND t.lease_owner IS NULL AND t.lease_until IS NULL
              AND t.last_error_code IN ('CLORO_POLL_TIMEOUT', 'CLORO_GENERATION_TIMEOUT')
              AND c.status = 'failed' AND c.error_code = t.last_error_code
            ORDER BY t.id FOR UPDATE;
            """, cancellationToken, ("@run", runId.ToString()), ("@provider", CloroDefinition.Provider));
        foreach (var candidate in candidates)
        {
            await ExecuteAsync(connection, transaction, """
                UPDATE provider_tasks SET state = 'queued', next_attempt_at = UTC_TIMESTAMP(6), completed_at = NULL,
                    last_error_code = NULL, last_error_message = NULL, updated_at = UTC_TIMESTAMP(6)
                WHERE id = UUID_TO_BIN(@task);
                UPDATE visibility_checks SET status = 'running', error_code = NULL, error_message = NULL,
                    updated_at = UTC_TIMESTAMP(6) WHERE id = UUID_TO_BIN(@check);
                """, cancellationToken, ("@task", candidate[0]), ("@check", candidate[1]));
        }
        if (candidates.Count > 0) await UpdateRunStatusAsync(connection, transaction, runId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return candidates.Count;
    }

    private static async Task<string[]> LockTaskAsync(MySqlConnection c, MySqlTransaction tx, CloroTaskLease task, string workerId, CancellationToken ct)
    {
        await QuerySingleAsync(c, tx, "SELECT id FROM visibility_runs WHERE id = UUID_TO_BIN(@run) FOR UPDATE;", ct, ("@run", task.RunId.ToString()));
        // Fenced by task id, run, check, lease owner and lease version; the provider is implied by the leased task.
        var row = await QuerySingleAsync(c, tx, """
            SELECT state, external_task_id FROM provider_tasks WHERE id = UUID_TO_BIN(@id) AND run_id = UUID_TO_BIN(@run)
              AND check_id = UUID_TO_BIN(@check) AND lease_owner = @worker AND lease_version = @version
              AND lease_until > UTC_TIMESTAMP(6) AND state IN ('leased','submitting') FOR UPDATE;
            """, ct, ("@id", task.TaskId.ToString()), ("@run", task.RunId.ToString()), ("@check", task.CheckId.ToString()),
            ("@worker", workerId), ("@version", task.LeaseVersion));
        return row ?? throw StaleLease();
    }

    private static async Task MarkRunningAsync(MySqlConnection c, MySqlTransaction tx, CloroTaskLease task, CancellationToken ct)
    {
        await ExecuteAsync(c, tx, """
            UPDATE visibility_checks SET status = 'running', updated_at = UTC_TIMESTAMP(6) WHERE id = UUID_TO_BIN(@check);
            UPDATE visibility_runs SET status = 'running', started_at = COALESCE(started_at, UTC_TIMESTAMP(6)), updated_at = UTC_TIMESTAMP(6)
            WHERE id = UUID_TO_BIN(@run);
            """, ct, ("@check", task.CheckId.ToString()), ("@run", task.RunId.ToString()));
    }

    private static Task<int> InsertUsageAsync(MySqlConnection c, MySqlTransaction tx, CloroTaskLease task,
        AnswerProviderDefinition definition, CancellationToken ct) =>
        InsertUsageAsync(c, tx, task.TenantId, task.RunId, task.TaskId, definition, ct);

    // entry_type and reference come from the provider definition (cloro_call / cloro:{taskId}, doubao_api_call / doubao:{taskId},
    // deepseek_api_call / deepseek:{taskId});
    // the (tenant_id, reference) key keeps the insert idempotent across retries.
    private static Task<int> InsertUsageAsync(MySqlConnection c, MySqlTransaction tx, Guid tenantId, Guid runId, Guid taskId,
        AnswerProviderDefinition definition, CancellationToken ct) => ExecuteAsync(c, tx, """
        INSERT INTO usage_ledger (id, tenant_id, run_id, provider_task_id, entry_type, units, amount, currency, cost_status, reference, created_at)
        VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), UUID_TO_BIN(@run), UUID_TO_BIN(@task), @entry, 1, 0, 'USD', 'unknown', @reference, UTC_TIMESTAMP(6))
        ON DUPLICATE KEY UPDATE reference = reference;
        """, ct, ("@id", Guid.NewGuid().ToString()), ("@tenant", tenantId.ToString()), ("@run", runId.ToString()),
        ("@task", taskId.ToString()), ("@entry", definition.UsageEntryType), ("@reference", definition.UsageReference(taskId)));

    private static AnswerProviderDefinition RequireDefinition(string? provider) =>
        AnswerProviderDefinition.TryGet(provider) ?? throw Error("TARGET_NOT_ALLOWED", "targets 只支持已注册的 API 回答 Provider（cloro、doubao_api、deepseek_api、packy_qwen、packy_glm、packy_kimi、packy_yuanbao）。", 400);

    private static string UnsupportedEngineMessage(AnswerProviderDefinition definition) =>
        definition.Provider == CloroDefinition.Provider
            ? "Cloro 引擎不受支持。"
            : $"{definition.DisplayName}只支持 {string.Join(" / ", definition.Engines)} 引擎。";

    // Cloro submits then polls an external task; synchronous API providers invoke and complete in one call.
    private static string TaskOperation(AnswerProviderDefinition definition) =>
        definition.Provider == CloroDefinition.Provider ? "submit_poll" : "invoke";

    private static async Task ReserveMonitoringCreditsAsync(MySqlConnection connection, MySqlTransaction transaction,
        Guid tenantId, Guid siteId, Guid runId, decimal amount, decimal unitPrice, DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction,
            "INSERT IGNORE INTO credit_accounts (tenant_id, updated_at) VALUES (UUID_TO_BIN(@tenant), UTC_TIMESTAMP(6));",
            cancellationToken, ("@tenant", tenantId.ToString()));
        var account = await QuerySingleAsync(connection, transaction, """
            SELECT available_credits FROM credit_accounts
            WHERE tenant_id = UUID_TO_BIN(@tenant) FOR UPDATE;
            """, cancellationToken, ("@tenant", tenantId.ToString()));
        var available = account is null ? 0m : decimal.Parse(account[0], CultureInfo.InvariantCulture);
        if (available < amount)
            throw Error("CREDIT_INSUFFICIENT",
                $"本轮预计需要 {amount:0.###} 积分，当前可用 {available:0.###} 积分。", 402);

        await ExecuteAsync(connection, transaction, """
            UPDATE credit_accounts
            SET available_credits = available_credits - @amount,
                reserved_credits = reserved_credits + @amount,
                version = version + 1, updated_at = UTC_TIMESTAMP(6)
            WHERE tenant_id = UUID_TO_BIN(@tenant);
            INSERT INTO monitoring_credit_reservations
                (id, tenant_id, site_id, run_id, unit_price, estimated_credits, held_credits,
                 captured_credits, released_credits, status, created_at)
            VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), UUID_TO_BIN(@run),
                    @unit, @amount, @amount, 0, 0, 'held', @created);
            INSERT INTO credit_ledger
                (id, tenant_id, entry_type, amount, balance_after, monitoring_reservation_id,
                 external_reference, reason, created_at)
            SELECT UUID_TO_BIN(@ledger), tenant_id, 'reserve', -@amount, available_credits,
                   UUID_TO_BIN(@id), CONCAT('monitoring:reserve:', @run),
                   '每日监控预留', @created
            FROM credit_accounts WHERE tenant_id = UUID_TO_BIN(@tenant);
            """, cancellationToken, ("@amount", amount), ("@unit", unitPrice),
            ("@id", Guid.NewGuid().ToString()), ("@ledger", Guid.NewGuid().ToString()),
            ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@run", runId.ToString()),
            ("@created", createdAt.UtcDateTime));
    }

    private static Task CaptureMonitoringItemAsync(MySqlConnection connection, MySqlTransaction transaction,
        CloroTaskLease task, CancellationToken cancellationToken)
        => CaptureMonitoringItemAsync(connection, transaction, task.TenantId, task.CheckId, "每日监控已提交", cancellationToken);

    // Shared with the App channel (AppCreditReservations): only the tenant and the check identify the item.
    internal static async Task CaptureMonitoringItemAsync(MySqlConnection connection, MySqlTransaction transaction,
        Guid tenantId, Guid checkId, string reason, CancellationToken cancellationToken)
    {
        var item = await QuerySingleAsync(connection, transaction, """
            SELECT BIN_TO_UUID(reservation_id), amount, status
            FROM monitoring_credit_items
            WHERE tenant_id = UUID_TO_BIN(@tenant) AND check_id = UUID_TO_BIN(@check) FOR UPDATE;
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@check", checkId.ToString()));
        if (item is null || item[2] == "captured") return;
        if (item[2] != "held") return;
        var amount = decimal.Parse(item[1], CultureInfo.InvariantCulture);
        var account = await QuerySingleAsync(connection, transaction, """
            SELECT reserved_credits FROM credit_accounts
            WHERE tenant_id = UUID_TO_BIN(@tenant) FOR UPDATE;
            """, cancellationToken, ("@tenant", tenantId.ToString()));
        if (account is null || decimal.Parse(account[0], CultureInfo.InvariantCulture) < amount)
            throw Error("CREDIT_RESERVATION_INVALID", "监控积分预留状态无效，请联系管理员对账。", 409);
        await ExecuteAsync(connection, transaction, """
            UPDATE credit_accounts SET reserved_credits = reserved_credits - @amount,
                lifetime_captured = lifetime_captured + @amount, version = version + 1,
                updated_at = UTC_TIMESTAMP(6)
            WHERE tenant_id = UUID_TO_BIN(@tenant);
            UPDATE monitoring_credit_items SET status = 'captured', captured_at = UTC_TIMESTAMP(6)
            WHERE tenant_id = UUID_TO_BIN(@tenant) AND check_id = UUID_TO_BIN(@check) AND status = 'held';
            INSERT INTO credit_ledger
                (id, tenant_id, entry_type, amount, balance_after, monitoring_reservation_id,
                 monitoring_check_id, external_reference, reason, created_at)
            SELECT UUID_TO_BIN(@ledger), tenant_id, 'capture', 0, available_credits,
                   UUID_TO_BIN(@reservation), UUID_TO_BIN(@check),
                   CONCAT('monitoring:capture:', @check), @reason, UTC_TIMESTAMP(6)
            FROM credit_accounts WHERE tenant_id = UUID_TO_BIN(@tenant);
            """, cancellationToken, ("@amount", amount), ("@tenant", tenantId.ToString()),
            ("@check", checkId.ToString()), ("@reservation", item[0]),
            ("@ledger", Guid.NewGuid().ToString()), ("@reason", reason));
        await RefreshMonitoringReservationAsync(connection, transaction, tenantId, Guid.Parse(item[0]), cancellationToken);
    }

    private static Task ReleaseMonitoringItemAsync(MySqlConnection connection, MySqlTransaction transaction,
        CloroTaskLease task, string reason, CancellationToken cancellationToken)
        => ReleaseMonitoringItemAsync(connection, transaction, task.TenantId, task.CheckId, reason, cancellationToken);

    // Shared with the App channel (AppCreditReservations): only the tenant and the check identify the item.
    internal static async Task ReleaseMonitoringItemAsync(MySqlConnection connection, MySqlTransaction transaction,
        Guid tenantId, Guid checkId, string reason, CancellationToken cancellationToken)
    {
        var item = await QuerySingleAsync(connection, transaction, """
            SELECT BIN_TO_UUID(reservation_id), amount, status
            FROM monitoring_credit_items
            WHERE tenant_id = UUID_TO_BIN(@tenant) AND check_id = UUID_TO_BIN(@check) FOR UPDATE;
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@check", checkId.ToString()));
        if (item is null || item[2] == "released" || item[2] == "captured") return;
        var amount = decimal.Parse(item[1], CultureInfo.InvariantCulture);
        var account = await QuerySingleAsync(connection, transaction, """
            SELECT reserved_credits FROM credit_accounts
            WHERE tenant_id = UUID_TO_BIN(@tenant) FOR UPDATE;
            """, cancellationToken, ("@tenant", tenantId.ToString()));
        if (account is null || decimal.Parse(account[0], CultureInfo.InvariantCulture) < amount)
            throw Error("CREDIT_RESERVATION_INVALID", "监控积分预留状态无效，请联系管理员对账。", 409);
        await ExecuteAsync(connection, transaction, """
            UPDATE credit_accounts SET available_credits = available_credits + @amount,
                reserved_credits = reserved_credits - @amount, version = version + 1,
                updated_at = UTC_TIMESTAMP(6)
            WHERE tenant_id = UUID_TO_BIN(@tenant);
            UPDATE monitoring_credit_items SET status = 'released', released_at = UTC_TIMESTAMP(6)
            WHERE tenant_id = UUID_TO_BIN(@tenant) AND check_id = UUID_TO_BIN(@check) AND status IN ('held','unknown');
            INSERT INTO credit_ledger
                (id, tenant_id, entry_type, amount, balance_after, monitoring_reservation_id,
                 monitoring_check_id, external_reference, reason, created_at)
            SELECT UUID_TO_BIN(@ledger), tenant_id, 'release', @amount, available_credits,
                   UUID_TO_BIN(@reservation), UUID_TO_BIN(@check),
                   CONCAT('monitoring:release:', @check), @reason, UTC_TIMESTAMP(6)
            FROM credit_accounts WHERE tenant_id = UUID_TO_BIN(@tenant);
            """, cancellationToken, ("@amount", amount), ("@tenant", tenantId.ToString()),
            ("@check", checkId.ToString()), ("@reservation", item[0]),
            ("@ledger", Guid.NewGuid().ToString()), ("@reason", reason.Length > 500 ? reason[..500] : reason));
        await RefreshMonitoringReservationAsync(connection, transaction, tenantId, Guid.Parse(item[0]), cancellationToken);
    }

    private static async Task MarkMonitoringItemUnknownAsync(MySqlConnection connection, MySqlTransaction transaction,
        Guid tenantId, Guid checkId, string reason, CancellationToken cancellationToken)
    {
        var item = await QuerySingleAsync(connection, transaction, """
            SELECT BIN_TO_UUID(reservation_id), status
            FROM monitoring_credit_items
            WHERE tenant_id = UUID_TO_BIN(@tenant) AND check_id = UUID_TO_BIN(@check) FOR UPDATE;
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@check", checkId.ToString()));
        if (item is null || item[1] is "captured" or "released") return;

        var safeReason = reason.Length > 500 ? reason[..500] : reason;
        await ExecuteAsync(connection, transaction, """
            UPDATE monitoring_credit_items
            SET status = 'unknown'
            WHERE tenant_id = UUID_TO_BIN(@tenant) AND check_id = UUID_TO_BIN(@check)
              AND status = 'held';
            UPDATE monitoring_credit_reservations
            SET status = 'unknown', reason = @reason
            WHERE tenant_id = UUID_TO_BIN(@tenant) AND id = UUID_TO_BIN(@reservation)
              AND status NOT IN ('captured', 'released');
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@check", checkId.ToString()),
            ("@reservation", item[0]), ("@reason", safeReason));
        await RefreshMonitoringReservationAsync(connection, transaction, tenantId, Guid.Parse(item[0]), cancellationToken);
    }

    internal static async Task RefreshMonitoringReservationAsync(MySqlConnection connection, MySqlTransaction transaction,
        Guid tenantId, Guid reservationId, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, """
            UPDATE monitoring_credit_reservations r
            JOIN (
                SELECT reservation_id,
                    COALESCE(SUM(CASE WHEN status IN ('held','unknown') THEN amount ELSE 0 END), 0) held,
                    COALESCE(SUM(CASE WHEN status = 'captured' THEN amount ELSE 0 END), 0) captured,
                    COALESCE(SUM(CASE WHEN status = 'released' THEN amount ELSE 0 END), 0) released,
                    COUNT(*) total,
                    SUM(status = 'captured') captured_count,
                    SUM(status = 'released') released_count,
                    SUM(status = 'unknown') unknown_count
                FROM monitoring_credit_items
                WHERE tenant_id = UUID_TO_BIN(@tenant) AND reservation_id = UUID_TO_BIN(@reservation)
                GROUP BY reservation_id
            ) x ON x.reservation_id = r.id
            SET r.held_credits = x.held, r.captured_credits = x.captured, r.released_credits = x.released,
                r.status = CASE WHEN x.unknown_count > 0 THEN 'unknown'
                                WHEN x.held > 0 AND x.captured_count > 0 THEN 'partial'
                                WHEN x.held > 0 THEN 'held'
                                WHEN x.captured = x.total * r.unit_price THEN 'captured'
                                WHEN x.released = x.total * r.unit_price THEN 'released'
                                ELSE 'partial' END,
                r.captured_at = CASE WHEN x.held = 0 THEN COALESCE(r.captured_at, UTC_TIMESTAMP(6)) ELSE r.captured_at END,
                r.released_at = CASE WHEN x.held = 0 THEN COALESCE(r.released_at, UTC_TIMESTAMP(6)) ELSE r.released_at END
            WHERE r.tenant_id = UUID_TO_BIN(@tenant) AND r.id = UUID_TO_BIN(@reservation);
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@reservation", reservationId.ToString()));
    }

    private static async Task UpdateRunStatusAsync(MySqlConnection c, MySqlTransaction tx, Guid runId, CancellationToken ct)
    {
        await ExecuteAsync(c, tx, """
            UPDATE visibility_runs r JOIN (
                SELECT run_id, COUNT(*) total, SUM(status = 'succeeded') succeeded, SUM(status = 'failed') failed
                FROM visibility_checks WHERE run_id = UUID_TO_BIN(@run) GROUP BY run_id
            ) x ON x.run_id = r.id
            SET r.status = CASE WHEN x.succeeded + x.failed < x.total THEN 'running'
                                WHEN x.succeeded = x.total THEN 'succeeded' WHEN x.failed = x.total THEN 'failed' ELSE 'partial_failed' END,
                r.succeeded_checks = x.succeeded, r.failed_checks = x.failed, r.started_at = COALESCE(r.started_at, UTC_TIMESTAMP(6)),
                r.completed_at = CASE WHEN x.succeeded + x.failed = x.total THEN UTC_TIMESTAMP(6) ELSE NULL END,
                r.updated_at = UTC_TIMESTAMP(6) WHERE r.id = UUID_TO_BIN(@run);
            UPDATE visibility_runs r SET
                error_code = (SELECT c.error_code FROM visibility_checks c WHERE c.run_id = r.id AND c.status = 'failed' ORDER BY c.created_at, c.id LIMIT 1),
                error_message = (SELECT c.error_message FROM visibility_checks c WHERE c.run_id = r.id AND c.status = 'failed' ORDER BY c.created_at, c.id LIMIT 1)
            WHERE r.id = UUID_TO_BIN(@run);
            """, ct, ("@run", runId.ToString()));
    }

    private static async Task<CloroVisibilityCheckView> ReadViewAsync(MySqlConnection connection, string[] row, CancellationToken ct)
    {
        var citations = await QueryRowsAsync(connection, null, """
            SELECT BIN_TO_UUID(id), normalized_url, source_domain, title, position FROM citations
            WHERE check_id = UUID_TO_BIN(@id) ORDER BY position, id;
            """, ct, ("@id", row[0]));
        return new CloroVisibilityCheckView(Guid.Parse(row[0]), Guid.Parse(row[1]), Guid.Parse(row[2]), row[3], row[4], row[5], row[6], row[7], row[8], row[9],
            row[10], ReadBool(row[11]), row[10] == "succeeded" ? new VisibilityAnswer(row[12]) : null,
            string.IsNullOrEmpty(row[13]) ? [] : JsonSerializer.Deserialize<string[]>(row[13]) ?? [], NullIfEmpty(row[14]),
            citations.Select(c => new CloroCitationView(Guid.Parse(c[0]), c[1], NullIfEmpty(c[2]), NullIfEmpty(c[3]), int.Parse(c[4]))).ToArray(),
            NullableUtc(row[15]), int.TryParse(row[16], out var duration) ? duration : null, NullIfEmpty(row[17]), NullIfEmpty(row[18]),
            NullIfEmpty(row[19]), NullIfEmpty(row[20]), NullIfEmpty(row[21]), NullIfEmpty(row[23]), NullIfEmpty(row[24]),
            Guid.TryParse(row[25], out var targetId) ? targetId : null, int.TryParse(row[26], out var sample) ? sample : 1,
            Guid.TryParse(row[27], out var promptVersionId) ? promptVersionId : null,
            string.IsNullOrEmpty(row[28]) ? null : JsonSerializer.Deserialize<string[]>(row[28]),
            NullIfEmpty(row[29]), int.TryParse(row[30], out var configVersion) ? configVersion : null,
            ReadBool(row[31]), QueryAvailability: row[32], ResultQuality: ResultQualityCodec.Decode(row[33]),
            AnswerMode: AnswerModeSchema.ParseStored(NullIfEmpty(row[34])));
    }

    private static CreateCloroVisibilityRunCommand ValidateAndNormalize(CreateCloroVisibilityRunCommand command)
    {
        if (command.TenantId == Guid.Empty || command.SiteId == Guid.Empty || command.PromptIds is not { Count: > 0 and <= 200 }
            || command.Targets is not { Count: > 0 and <= 20 } || command.PromptIds.Any(id => id == Guid.Empty)
            || command.PromptIds.Count != command.PromptIds.Distinct().Count())
            throw Error("INVALID_RUN", "必须提供租户、站点、1–200 个不同的 Prompt 和 1–20 个目标。", 400);
        // Without custom scopes the pair count is the full P × T product; scoped runs are re-checked after normalization.
        if (command.SampleCount is < 1 or > 10
            || command.PromptTargetKeys is not { Count: > 0 } && command.PromptIds.Count * command.Targets.Count * command.SampleCount > 1000)
            throw Error("RUN_TOO_LARGE", "sampleCount 必须为 1–10，且一批最多 1000 个 Check。", 400);
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey) || command.IdempotencyKey.Length is < 8 or > 191)
            throw Error("INVALID_IDEMPOTENCY_KEY", "幂等键长度必须为 8–191。", 400);
        if (command.RequestedBy?.Length > 128) throw Error("INVALID_RUN", "requestedBy 最长为 128。", 400);
        if (command.HttpRequestHash is not null && (command.HttpRequestHash.Length != 64
            || command.HttpRequestHash.Any(c => !char.IsAsciiHexDigit(c))))
            throw Error("INVALID_RUN", "HTTP 请求摘要无效。", 400);
        var targets = new List<CloroVisibilityTarget>();
        foreach (var target in command.Targets)
        {
            if (target is null) throw Error("TARGET_NOT_ALLOWED", "targets 只支持已注册的 API 回答 Provider（cloro、doubao_api、deepseek_api、packy_qwen、packy_glm、packy_kimi、packy_yuanbao）。", 400);
            var definition = RequireDefinition(target.Provider);
            var engine = target.Engine?.Trim().ToLowerInvariant() ?? "";
            if (!definition.SupportsEngine(engine)) throw Error("UNSUPPORTED_ENGINE", UnsupportedEngineMessage(definition), 400);
            var country = (target.Country ?? definition.DefaultCountry).ToUpperInvariant();
            try
            {
                if (!Regex.IsMatch(country, "^[A-Z]{2}$", RegexOptions.CultureInvariant) || new RegionInfo(country).TwoLetterISORegionName != country)
                    throw new ArgumentException();
            }
            catch (ArgumentException) { throw Error("INVALID_COUNTRY", "country 必须为两位 ISO 国家代码。", 400); }
            var locale = target.Locale is null ? null : ValidateLocale(target.Locale);
            if (definition.PinnedSurface)
            {
                // Pinned providers (doubao_api, deepseek_api) are only meaningful for the Chinese consumer surface:
                // the target is pinned to the definition's DefaultCountry / DefaultLocale (CN / zh-CN).
                locale ??= definition.DefaultLocale;
                if (country != definition.DefaultCountry || !string.Equals(locale, definition.DefaultLocale, StringComparison.Ordinal))
                    throw Error("TARGET_NOT_ALLOWED", $"{definition.DisplayName}目标仅支持 {definition.DefaultCountry}/{definition.DefaultLocale}。", 400);
            }
            if (target.OptionsJson is not null)
            {
                try
                {
                    using var options = JsonDocument.Parse(target.OptionsJson);
                    if (options.RootElement.ValueKind != JsonValueKind.Object || options.RootElement.EnumerateObject().Any()) throw new JsonException();
                }
                catch (JsonException) { throw Error("INVALID_OPTIONS", "本期 options 只允许空对象。", 400); }
            }
            // The answer mode is kept (default folded to null so keys and hashes are unchanged); an invalid or unsupported
            // mode is rejected with 400 ANSWER_MODE_UNSUPPORTED.
            targets.Add(AnswerModeRules.Normalize(new CloroVisibilityTarget(definition.Provider, engine, locale, country, "{}", target.AnswerMode)));
        }
        if (targets.Select(CollectionIdentity.TargetKey).Distinct().Count() != targets.Count) throw Error("DUPLICATE_TARGET", "同一目标不可重复选择。", 400);
        // Cloro-only runs keep the historical engine/country/locale order (and therefore their request hash); the mode is
        // the last sort key, so runs with default modes only keep their byte-identical order and request hash.
        var normalized = command with { PromptIds = command.PromptIds.Order().ToArray(), Targets = targets.OrderBy(t => t.Provider, StringComparer.Ordinal)
            .ThenBy(t => t.Engine, StringComparer.Ordinal).ThenBy(t => t.Country, StringComparer.Ordinal).ThenBy(t => t.Locale, StringComparer.Ordinal)
            .ThenBy(t => t.AnswerMode ?? "", StringComparer.Ordinal).ToArray() };
        // Per-prompt targets (daily monitoring only): every key must be one of this Run's normalized targets, and the
        // 1000-check limit counts the allowed prompt × target × sample pairs. Empty lists normalize to null (cartesian).
        if (!MonitoringMatrix.TryNormalizePromptTargets(command.PromptTargetKeys, normalized.PromptIds, normalized.Targets, out var promptTargets))
            throw Error("INVALID_RUN", "promptTargetKeys 只能包含本次运行中的 Prompt，且每个 Prompt 的目标必须是 targets 的非空、不重复子集。", 400);
        normalized = normalized with { PromptTargetKeys = promptTargets };
        if (promptTargets is not null && MonitoringMatrix.CountChecks(
                new CollectionScheduleSelection(normalized.PromptIds, normalized.Targets, normalized.SampleCount, promptTargets)) > MonitoringMatrix.MaxChecks)
            throw Error("RUN_TOO_LARGE", "sampleCount 必须为 1–10，且一批最多 1000 个 Check。", 400);
        return normalized;
    }

    private static string ValidateLocale(string locale)
    {
        if (locale.Length is < 2 or > 16 || !Regex.IsMatch(locale, "^[A-Za-z]{2,8}(-[A-Za-z0-9]{1,8})*$", RegexOptions.CultureInvariant))
            throw Error("INVALID_LOCALE", "locale 必须是 2–16 字符的 BCP-47 标识。", 400);
        try
        {
            var normalized = CultureInfo.GetCultureInfo(locale).Name;
            if (string.IsNullOrEmpty(normalized)) throw new CultureNotFoundException();
            return normalized;
        }
        catch (CultureNotFoundException) { throw Error("INVALID_LOCALE", "locale 无效。", 400); }
    }

    // Only a Run with custom per-prompt targets adds them to the hash; manual and historical requests keep their exact hash.
    private static string ComputeRequestHash(CreateCloroVisibilityRunCommand command) => command.PromptTargetKeys is { Count: > 0 } promptTargets
        ? Sha256(JsonSerializer.Serialize(new { command.SiteId, command.PromptIds, command.Targets, command.RequestedBy, command.SampleCount,
            PromptTargets = promptTargets.OrderBy(p => p.PromptId)
                .Select(p => new { p.PromptId, TargetKeys = p.TargetKeys.Order(StringComparer.Ordinal).ToArray() }).ToArray() }))
        : command.SampleCount == 1
        ? Sha256(JsonSerializer.Serialize(new { command.SiteId, command.PromptIds, command.Targets, command.RequestedBy }))
        : Sha256(JsonSerializer.Serialize(new { command.SiteId, command.PromptIds, command.Targets, command.RequestedBy, command.SampleCount }));
    private static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;
    private static bool ReadBool(string value) => value == "1" || bool.TryParse(value, out var result) && result;
    private static DateTimeOffset ToUtc(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    private static DateTimeOffset? NullableUtc(string value) => string.IsNullOrEmpty(value) ? null : ToUtc(value);
    private static CollectorStoreException Error(string code, string message, int status) => new(code, message, status);
    private static CollectorStoreException StaleLease() => Error("STALE_LEASE", "任务租约已失效。", 409);
    private sealed record CheckCursor(DateTimeOffset CreatedAt, Guid Id, string Scope);

    private static CheckCursor? DecodeCursor(string? value, string scope)
    {
        if (value is null) return null;
        try
        {
            if (value.Length > 2048) throw new FormatException();
            var decoded = JsonSerializer.Deserialize<CheckCursor>(Convert.FromBase64String(value));
            if (decoded is null || decoded.Scope != scope || decoded.Id == Guid.Empty || decoded.CreatedAt.Year < 1000) throw new FormatException();
            return decoded;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        { throw Error("INVALID_CURSOR", "游标无效或不属于当前租户、运行及筛选条件。", 400); }
    }

    internal static string TruncateUtf8(string value, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes) return value;
        var result = new StringBuilder();
        var used = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (used + rune.Utf8SequenceLength > maxBytes) break;
            result.Append(rune.ToString());
            used += rune.Utf8SequenceLength;
        }
        return result.ToString();
    }

    private static string BoundProviderResponse(string value)
    {
        if (Encoding.UTF8.GetByteCount(value) <= MaxRawBytes) return value;
        var hash = Sha256(value);
        return JsonSerializer.Serialize(new { truncated = true, sha256 = hash });
    }

    private static string SanitizeProviderJson(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return "{\"redacted\":true}";
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.Default }))
            WriteSafeProviderJson(writer, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteSafeProviderJson(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject())
                {
                    var key = string.Concat(property.Name.Where(char.IsLetterOrDigit)).ToLowerInvariant();
                    if (key.Contains("authorization", StringComparison.Ordinal) || key.Contains("apikey", StringComparison.Ordinal)
                        || key.Contains("token", StringComparison.Ordinal) || key.Contains("cookie", StringComparison.Ordinal)
                        || key.Contains("profile", StringComparison.Ordinal) || key.Contains("screenshot", StringComparison.Ordinal)
                        || key.Contains("html", StringComparison.Ordinal) || key.Contains("password", StringComparison.Ordinal)
                        || key.Contains("secret", StringComparison.Ordinal) || key.Contains("header", StringComparison.Ordinal)
                        || key.Contains("error", StringComparison.Ordinal) || key.Contains("stack", StringComparison.Ordinal)) continue;
                    writer.WritePropertyName(property.Name);
                    WriteSafeProviderJson(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteSafeProviderJson(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                var text = value.GetString() ?? string.Empty;
                writer.WriteStringValue(text.Contains("Bearer ", StringComparison.OrdinalIgnoreCase) ? "[redacted]" : text);
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    internal static string SafeErrorCode(string code) => code switch
    {
        "CLORO_NOT_CONFIGURED" or "UNSUPPORTED_ENGINE" or "CLORO_SUBMIT_FAILED" or "CLORO_SUBMISSION_UNKNOWN" or
        "CLORO_TASK_NOT_FOUND" or "CLORO_RATE_LIMITED" or "CLORO_POLL_FAILED" or "CLORO_POLL_TIMEOUT" or
        "CLORO_GENERATION_TIMEOUT" or "CLORO_TASK_FAILED" or "INVALID_PROVIDER_RESPONSE" or
        "CLORO_KEY_POOL_EXHAUSTED" or "CLORO_KEY_POOL_BUSY" or "CLORO_KEY_SWITCH_LIMIT" or "CLORO_KEY_INVALID" or
        "CLORO_KEY_POOL_UNAVAILABLE" or "CLORO_MODEL_UNAVAILABLE" => code,
        _ when DoubaoErrorCodes.All.Contains(code, StringComparer.Ordinal) => code,
        _ when DeepSeekErrorCodes.All.Contains(code, StringComparer.Ordinal) => code,
        _ when PackyErrorCodes.All.Contains(code, StringComparer.Ordinal) => code,
        _ => "INVALID_PROVIDER_RESPONSE"
    };
    internal static string SafeErrorMessage(string code) => SafeErrorCode(code) switch
    {
        "CLORO_NOT_CONFIGURED" => "Cloro 未配置、未启用或配置无效。",
        "UNSUPPORTED_ENGINE" => "Cloro 引擎不受支持。",
        "CLORO_SUBMISSION_UNKNOWN" => "提交结果未知，已停止自动提交，请核对 Cloro 外部任务。",
        "CLORO_SUBMIT_FAILED" => "Cloro 拒绝了提交请求。",
        "CLORO_MODEL_UNAVAILABLE" => "Cloro 暂时无法访问该模型。",
        "CLORO_TASK_NOT_FOUND" => "Cloro 外部任务不存在。",
        "CLORO_RATE_LIMITED" => "Cloro 暂时限制请求频率。",
        "CLORO_POLL_FAILED" => "Cloro 任务状态暂时不可用。",
        "CLORO_POLL_TIMEOUT" => "Cloro 任务超过总等待时限。",
        "CLORO_GENERATION_TIMEOUT" => "Cloro 回答生成超时。",
        "CLORO_TASK_FAILED" => "Cloro 任务执行失败。",
        "CLORO_KEY_POOL_EXHAUSTED" => "Cloro 积分池已耗尽，任务等待账号恢复后提交。",
        "CLORO_KEY_POOL_BUSY" => "Cloro 账号并发已满，冷却结束后自动提交。",
        "CLORO_KEY_SWITCH_LIMIT" => "本次尝试的 Cloro 账号都拒绝了提交，稍后自动重试。",
        "CLORO_KEY_INVALID" => "提交该任务的 Cloro API Key 已失效，请管理员处理。",
        "CLORO_KEY_POOL_UNAVAILABLE" => "Cloro key 池暂时不可读，稍后自动重试。",
        DoubaoErrorCodes.NotConfigured => "豆包服务尚未配置或未启用。",
        DoubaoErrorCodes.AuthFailed => "方舟凭据无效或账号状态异常。",
        DoubaoErrorCodes.ServiceNotOpen => "方舟模型或豆包搜索服务未开通。",
        DoubaoErrorCodes.QuotaExceeded => "方舟配额不足或账号欠费。",
        DoubaoErrorCodes.RateLimited => "方舟请求被限流。",
        DoubaoErrorCodes.InputFiltered => "问题触发平台内容审核。",
        DoubaoErrorCodes.InvalidRequest => "方舟请求参数不被接受。",
        DoubaoErrorCodes.RequestFailed => "方舟请求失败。",
        DoubaoErrorCodes.Timeout => "方舟请求超时。",
        DoubaoErrorCodes.InvalidResponse => "方舟响应不符合采集数据契约。",
        DeepSeekErrorCodes.NotConfigured => "DeepSeek服务尚未配置或未启用。",
        DeepSeekErrorCodes.AuthFailed => "DeepSeek 凭据无效或无权访问。",
        DeepSeekErrorCodes.InsufficientBalance => "DeepSeek 账户余额不足。",
        DeepSeekErrorCodes.RateLimited => "DeepSeek 请求被限流或服务过载。",
        DeepSeekErrorCodes.InvalidRequest => "DeepSeek 请求参数不被接受。",
        DeepSeekErrorCodes.RequestFailed => "DeepSeek 请求失败。",
        DeepSeekErrorCodes.Timeout => "DeepSeek 请求超时。",
        DeepSeekErrorCodes.InvalidResponse => "DeepSeek 响应不符合采集数据契约。",
        PackyErrorCodes.NotConfigured => "PackyAPI 服务尚未配置或未启用。",
        PackyErrorCodes.AuthFailed => "PackyAPI 凭据无效或无权访问。",
        PackyErrorCodes.InsufficientBalance => "PackyAPI 账户余额不足。",
        PackyErrorCodes.RateLimited => "PackyAPI 请求被限流或上游过载。",
        PackyErrorCodes.InvalidRequest => "PackyAPI 请求参数不被接受或分组没有可用渠道。",
        PackyErrorCodes.RequestFailed => "PackyAPI 请求失败。",
        PackyErrorCodes.Timeout => "PackyAPI 请求超时。",
        PackyErrorCodes.InvalidResponse => "PackyAPI 回答被截断或响应不符合采集数据契约。",
        PackyErrorCodes.IncompleteAnswer => "PackyAPI 搜索后没有生成完整回答。",
        _ => "Cloro 响应不符合采集数据契约。"
    };

    private static async Task<int> ExecuteAsync(MySqlConnection c, MySqlTransaction? tx, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var command = new MySqlCommand(sql, c, tx);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(ct);
    }
    private static async Task<List<string[]>> QueryRowsAsync(MySqlConnection c, MySqlTransaction? tx, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var command = new MySqlCommand(sql, c, tx);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<string[]>();
        while (await reader.ReadAsync(ct))
        {
            var values = new string[reader.FieldCount];
            for (var i = 0; i < values.Length; i++)
                values[i] = reader.IsDBNull(i) ? "" : reader.GetValue(i) is DateTime date
                    ? DateTime.SpecifyKind(date, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture)
                    : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? "";
            rows.Add(values);
        }
        return rows;
    }
    private static async Task<string[]?> QuerySingleAsync(MySqlConnection c, MySqlTransaction? tx, string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
        => (await QueryRowsAsync(c, tx, sql, ct, parameters)).FirstOrDefault();
}
