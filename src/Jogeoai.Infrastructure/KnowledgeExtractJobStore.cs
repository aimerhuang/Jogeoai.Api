using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jogeoai.Application;
using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

/// <summary>
/// 网址识别任务的 MySQL 实现（依赖 043 迁移的 brand_knowledge_extract_jobs）：惰性过期、与产品识别共用的每日频控、
/// 每品牌单任务（uq_bkej_active）、Worker 租约与心跳取消检测、24 小时候选保留，以及保存/放弃时的状态检查。网页正文从不落库。
/// </summary>
public sealed class KnowledgeExtractJobStore(MySqlDatabase database) : IKnowledgeExtractJobStore
{
    /// <summary>每租户每日（UTC）识别次数上限（与产品识别合计）。</summary>
    public const int DailyLimit = BrandKnowledgeRules.DailyExtractLimit;
    /// <summary>识别任务最大尝试次数。</summary>
    public const int MaxAttempts = 3;
    /// <summary>网址最大字符数。</summary>
    public const int MaxUrlLength = 2000;
    /// <summary>写入 candidates_json 的序列化字节预算；低于表约束 262 144，为 MySQL 规范化 JSON 时增加的空白留出余量。</summary>
    public const int CandidatesJsonBudget = 250_000;

    private const int MaxPageTitleCharacters = 500;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    private const string JobColumns = """
        BIN_TO_UUID(j.id), j.status, j.step, j.url, j.page_title, j.candidates_json, j.truncated, j.error_code, j.error_message,
        j.created_at, j.completed_at, j.expires_at
        """;

    private const string ExpireSql = """
        UPDATE brand_knowledge_extract_jobs SET status='discarded', resolved_at=UTC_TIMESTAMP(6), updated_at=UTC_TIMESTAMP(6)
        WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND status='done' AND resolved_at IS NULL
          AND expires_at <= UTC_TIMESTAMP(6)
        """;

    /// <inheritdoc />
    public async Task<KnowledgeExtractJobView> EnqueueAsync(Guid tenantId, Guid siteId, Guid userId, KnowledgeExtractRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (url, urlHash) = NormalizeUrl(request.Url);
        var dimensionsJson = SerializeDimensions(request.Dimensions);
        var jobId = Guid.NewGuid();

        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // 与产品识别入队相同：先锁租户行串行化两类任务的频控计数，再校验品牌。
        await using (var lockTenant = new MySqlCommand("SELECT id FROM tenants WHERE id=UUID_TO_BIN(@tenant) FOR UPDATE;", connection, transaction))
        {
            lockTenant.Parameters.AddWithValue("@tenant", tenantId.ToString());
            if (await lockTenant.ExecuteScalarAsync(cancellationToken) is null) throw Error("RESOURCE_NOT_FOUND", "租户不存在。", 404);
        }
        await AssertSiteAsync(connection, transaction, tenantId, siteId, cancellationToken);
        await ExecuteAsync(connection, transaction, ExpireSql + ";", cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()));

        var today = await CountTodayAsync(connection, transaction, tenantId, cancellationToken);
        if (today >= DailyLimit)
            throw Error("KNOWLEDGE_EXTRACT_RATE_LIMITED", $"今日识别次数已达上限（{DailyLimit} 次，与产品识别共用），请明天再试。", 429);
        try
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO brand_knowledge_extract_jobs
                    (id, tenant_id, site_id, requested_by, url, url_hash, dimensions_json, status, step, next_attempt_at, created_at, updated_at)
                VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), UUID_TO_BIN(@user), @url, @hash, @dimensions,
                        'pending', 'fetch', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                """, cancellationToken, ("@id", jobId.ToString()), ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()),
                ("@user", userId.ToString()), ("@url", url), ("@hash", urlHash), ("@dimensions", dimensionsJson));
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            throw Error("KNOWLEDGE_EXTRACT_IN_PROGRESS", "该品牌已有进行中或待处理的网址识别，请先处理或取消。", 409);
        }
        await transaction.CommitAsync(cancellationToken);
        return (await GetAsync(tenantId, siteId, jobId, cancellationToken))!;
    }

    /// <inheritdoc />
    public async Task<KnowledgeExtractJobView?> GetCurrentAsync(Guid tenantId, Guid siteId, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        // 品牌不属于该租户时返回 404，而不是「没有当前任务」。
        await AssertSiteAsync(connection, null, tenantId, siteId, cancellationToken);
        await ExecuteAsync(connection, null, ExpireSql + ";", cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()));
        // 未结束任务在品牌内唯一，且总是最近创建的那条；最近一条是 24 小时内未放弃的失败任务时也返回，便于页面展示原因。
        await using var command = new MySqlCommand($"""
            SELECT {JobColumns}, j.resolved_at, j.completed_at >= DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 24 HOUR)
            FROM brand_knowledge_extract_jobs j
            WHERE j.tenant_id=UUID_TO_BIN(@tenant) AND j.site_id=UUID_TO_BIN(@site)
            ORDER BY j.created_at DESC, j.id DESC LIMIT 1;
            """, connection);
        command.Parameters.AddWithValue("@tenant", tenantId.ToString());
        command.Parameters.AddWithValue("@site", siteId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var job = ReadJob(reader);
        var unresolved = reader.IsDBNull(12);
        var recent = !reader.IsDBNull(13) && Convert.ToInt32(reader.GetValue(13), CultureInfo.InvariantCulture) == 1;
        if (!unresolved) return null;
        if (job.Status is "pending" or "running" or "done") return job;
        return job.Status == "failed" && recent ? job : null;
    }

    /// <inheritdoc />
    public async Task<KnowledgeExtractJobView?> GetAsync(Guid tenantId, Guid siteId, Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await ExpireJobAsync(connection, null, tenantId, siteId, jobId, cancellationToken);
        return await ReadViewAsync(connection, null, tenantId, siteId, jobId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<KnowledgeExtractJobView> CancelAsync(Guid tenantId, Guid siteId, Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var state = await LockStateAsync(connection, transaction, tenantId, siteId, jobId, cancellationToken) ?? throw NotFound();
        if (state.Status is "pending" or "running")
        {
            await ExecuteAsync(connection, transaction, """
                UPDATE brand_knowledge_extract_jobs SET status='cancelled', lease_owner=NULL, lease_until=NULL,
                    completed_at=UTC_TIMESTAMP(6), updated_at=UTC_TIMESTAMP(6)
                WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@job) AND status IN ('pending', 'running');
                """, cancellationToken, ("@tenant", tenantId.ToString()), ("@job", jobId.ToString()));
        }
        else if (state.Status != "cancelled")
        {
            throw Error("KNOWLEDGE_EXTRACT_STATE_CONFLICT", "识别已结束，不能取消；候选可选择保存或放弃。", 409);
        }
        var view = await ReadViewAsync(connection, transaction, tenantId, siteId, jobId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return view!;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BrandKnowledgeItemView>> SaveCandidatesAsync(Guid tenantId, Guid siteId, Guid jobId, Guid userId,
        KnowledgeExtractSaveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var submitted = request.Items ?? [];
        if (submitted.Count == 0) throw Error("KNOWLEDGE_ITEM_VALIDATION", "请至少勾选一条候选。", 400);

        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        IReadOnlyList<Guid> ids;
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            // 锁顺序固定为「任务行 → 品牌行」：先锁任务行，同一任务的并发保存在这里串行，后到者看到 saved 后返回 409。
            KnowledgeExtractJobView job;
            JobState state;
            bool expired;
            await using (var command = new MySqlCommand($"""
                SELECT {JobColumns}, j.resolved_at, j.expires_at IS NOT NULL AND j.expires_at <= UTC_TIMESTAMP(6)
                FROM brand_knowledge_extract_jobs j
                WHERE j.tenant_id=UUID_TO_BIN(@tenant) AND j.site_id=UUID_TO_BIN(@site) AND j.id=UUID_TO_BIN(@job)
                FOR UPDATE;
                """, connection, transaction))
            {
                command.Parameters.AddWithValue("@tenant", tenantId.ToString());
                command.Parameters.AddWithValue("@site", siteId.ToString());
                command.Parameters.AddWithValue("@job", jobId.ToString());
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken)) throw NotFound();
                job = ReadJob(reader);
                state = new JobState(job.Status, job.ExpiresAt, NullableUtc(reader, 12));
                expired = Convert.ToInt32(reader.GetValue(13), CultureInfo.InvariantCulture) == 1;
            }
            if (job.Status == "done" && state.ResolvedAt is null && expired)
            {
                // 惰性过期与读取接口一致：置为 discarded 并提交，再返回 410。
                await ExpireJobAsync(connection, transaction, tenantId, siteId, jobId, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                throw Error("KNOWLEDGE_EXTRACT_EXPIRED", "识别结果已超过 24 小时，请重新识别。", 410);
            }
            if (job.Status != "done" || state.ResolvedAt is not null || job.ExpiresAt is null) throw NotSavable(state);

            var candidates = new Dictionary<string, KnowledgeExtractCandidate>(StringComparer.Ordinal);
            foreach (var candidate in job.Candidates) candidates.TryAdd(candidate.CandidateId, candidate);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var creates = new List<BrandKnowledgeItemCreate>(submitted.Count);
            foreach (var item in submitted)
            {
                if (item is null || string.IsNullOrWhiteSpace(item.CandidateId) || !candidates.TryGetValue(item.CandidateId, out var candidate))
                    throw Error("KNOWLEDGE_ITEM_VALIDATION", "提交的候选不属于该识别任务，请刷新后重试。", 400);
                if (!seen.Add(item.CandidateId))
                    throw Error("KNOWLEDGE_ITEM_VALIDATION", "同一候选不能重复提交。", 400);
                // 用户可编辑维度、标题与内容：由 InsertManyAsync 按 BrandKnowledgeRules 重新校验；原文依据取任务内的候选，不接受客户端提交。
                creates.Add(new BrandKnowledgeItemCreate(new BrandKnowledgeItemWrite(item.Dimension, item.Title, item.Content), candidate.Evidence));
            }

            ids = await BrandKnowledgeItemStore.InsertManyAsync(connection, transaction, tenantId, siteId, userId, creates,
                new BrandKnowledgeItemSource("url", job.Url, jobId), cancellationToken);
            await ExecuteAsync(connection, transaction, """
                UPDATE brand_knowledge_extract_jobs SET status='saved', resolved_at=UTC_TIMESTAMP(6), updated_at=UTC_TIMESTAMP(6)
                WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@job) AND status='done' AND resolved_at IS NULL;
                """, cancellationToken, ("@tenant", tenantId.ToString()), ("@job", jobId.ToString()));
            await transaction.CommitAsync(cancellationToken);
        }
        return await BrandKnowledgeItemStore.ReadCreatedAsync(connection, tenantId, siteId, ids, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<KnowledgeExtractJobView> MarkResolvedAsync(Guid tenantId, Guid siteId, Guid jobId, string resolution,
        CancellationToken cancellationToken = default)
    {
        // 保存只走 SaveCandidatesAsync（原子写入条目并置 saved）；这里只处理放弃。
        if (resolution != "discarded") throw new ArgumentOutOfRangeException(nameof(resolution));
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExpireJobAsync(connection, transaction, tenantId, siteId, jobId, cancellationToken);
        var state = await LockStateAsync(connection, transaction, tenantId, siteId, jobId, cancellationToken) ?? throw NotFound();
        var write = state.Status switch
        {
            "done" or "failed" => true,
            "discarded" or "cancelled" => false,
            "saved" => throw Error("KNOWLEDGE_EXTRACT_NOT_READY", "识别结果已保存，不能放弃。", 409),
            _ => throw Error("KNOWLEDGE_EXTRACT_NOT_READY", "识别尚未完成，请先取消任务。", 409)
        };
        if (write)
        {
            await ExecuteAsync(connection, transaction, """
                UPDATE brand_knowledge_extract_jobs SET status='discarded', resolved_at=UTC_TIMESTAMP(6), updated_at=UTC_TIMESTAMP(6)
                WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@job);
                """, cancellationToken, ("@tenant", tenantId.ToString()), ("@job", jobId.ToString()));
        }
        var view = await ReadViewAsync(connection, transaction, tenantId, siteId, jobId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return view!;
    }

    /// <inheritdoc />
    public async Task<KnowledgeExtractJobLease?> ClaimAsync(string workerId, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        KnowledgeExtractJobLease? job = null;
        string? status = null;
        var siteActive = false;
        // LEFT JOIN：品牌已归档或已不存在的任务也要被选中，才能在下面记为失败；只锁任务行，不锁品牌行。
        await using (var command = new MySqlCommand("""
            SELECT BIN_TO_UUID(j.id), BIN_TO_UUID(j.tenant_id), BIN_TO_UUID(j.site_id), s.display_name, BIN_TO_UUID(j.requested_by),
                   j.url, j.dimensions_json, j.attempt_count, j.status, s.status = 'active'
            FROM brand_knowledge_extract_jobs j
            LEFT JOIN sites s ON s.tenant_id=j.tenant_id AND s.id=j.site_id
            WHERE (j.status='pending' AND j.next_attempt_at <= UTC_TIMESTAMP(6))
               OR (j.status='running' AND j.lease_until < UTC_TIMESTAMP(6))
            ORDER BY j.next_attempt_at, j.created_at, j.id
            LIMIT 1 FOR UPDATE OF j SKIP LOCKED;
            """, connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                job = new KnowledgeExtractJobLease(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)),
                    NullableString(reader, 3) ?? string.Empty, Guid.Parse(reader.GetString(4)), reader.GetString(5),
                    DeserializeDimensions(NullableString(reader, 6)), Convert.ToInt32(reader.GetValue(7), CultureInfo.InvariantCulture), workerId);
                status = reader.GetString(8);
                siteActive = !reader.IsDBNull(9) && Convert.ToInt64(reader.GetValue(9), CultureInfo.InvariantCulture) == 1;
            }
        }
        if (job is null) { await transaction.RollbackAsync(CancellationToken.None); return null; }
        if (!siteActive)
        {
            // 品牌已归档（或已删除）：与列表、新增等入口一致视为品牌不存在。直接记为失败而不是跳过，
            // 否则任务会一直停在 pending，反复被选中却永远不执行；品牌恢复后可重新发起识别。
            await ExecuteAsync(connection, transaction, """
                UPDATE brand_knowledge_extract_jobs SET status='failed', error_code='RESOURCE_NOT_FOUND',
                    error_message='品牌已归档或不存在，识别已停止。', lease_owner=NULL, lease_until=NULL,
                    completed_at=UTC_TIMESTAMP(6), updated_at=UTC_TIMESTAMP(6)
                WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@id);
                """, cancellationToken, ("@tenant", job.TenantId.ToString()), ("@id", job.JobId.ToString()));
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        if (status == "running" && job.AttemptCount >= MaxAttempts)
        {
            // 租约过期说明上一次执行中断（如 Worker 崩溃）；中断次数用尽后不再领取，避免无限循环。
            await ExecuteAsync(connection, transaction, """
                UPDATE brand_knowledge_extract_jobs SET status='failed', error_code='KNOWLEDGE_EXTRACT_RETRYABLE',
                    error_message='识别多次中断，请重新发起。', lease_owner=NULL, lease_until=NULL,
                    completed_at=UTC_TIMESTAMP(6), updated_at=UTC_TIMESTAMP(6)
                WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@id);
                """, cancellationToken, ("@tenant", job.TenantId.ToString()), ("@id", job.JobId.ToString()));
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        await ExecuteAsync(connection, transaction, """
            UPDATE brand_knowledge_extract_jobs SET status='running', step='fetch', lease_owner=@owner,
                lease_until=DATE_ADD(UTC_TIMESTAMP(6), INTERVAL @seconds SECOND), attempt_count=attempt_count+1,
                started_at=COALESCE(started_at, UTC_TIMESTAMP(6)), error_code=NULL, error_message=NULL, updated_at=UTC_TIMESTAMP(6)
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@id);
            """, cancellationToken, ("@owner", workerId), ("@seconds", Math.Max(5, (int)lease.TotalSeconds)),
            ("@tenant", job.TenantId.ToString()), ("@id", job.JobId.ToString()));
        await transaction.CommitAsync(cancellationToken);
        return job with { AttemptCount = job.AttemptCount + 1 };
    }

    /// <inheritdoc />
    public async Task<KnowledgeExtractHeartbeat> HeartbeatAsync(KnowledgeExtractJobLease lease, TimeSpan extend,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using (var command = new MySqlCommand("""
            UPDATE brand_knowledge_extract_jobs SET lease_until=DATE_ADD(UTC_TIMESTAMP(6), INTERVAL @seconds SECOND), updated_at=UTC_TIMESTAMP(6)
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@job) AND status='running' AND lease_owner=@owner;
            """, connection))
        {
            command.Parameters.AddWithValue("@seconds", Math.Max(5, (int)extend.TotalSeconds));
            command.Parameters.AddWithValue("@tenant", lease.TenantId.ToString());
            command.Parameters.AddWithValue("@job", lease.JobId.ToString());
            command.Parameters.AddWithValue("@owner", lease.LeaseOwner);
            if (await command.ExecuteNonQueryAsync(cancellationToken) > 0) return KnowledgeExtractHeartbeat.Alive;
        }
        await using var status = new MySqlCommand(
            "SELECT status FROM brand_knowledge_extract_jobs WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@job);", connection);
        status.Parameters.AddWithValue("@tenant", lease.TenantId.ToString());
        status.Parameters.AddWithValue("@job", lease.JobId.ToString());
        return await status.ExecuteScalarAsync(cancellationToken) as string == "cancelled"
            ? KnowledgeExtractHeartbeat.Cancelled
            : KnowledgeExtractHeartbeat.LeaseLost;
    }

    /// <inheritdoc />
    public async Task<bool> MarkStepAsync(KnowledgeExtractJobLease lease, string step, CancellationToken cancellationToken = default)
    {
        if (step is not ("fetch" or "model" or "finalize")) throw new ArgumentOutOfRangeException(nameof(step));
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            UPDATE brand_knowledge_extract_jobs SET step=@step, updated_at=UTC_TIMESTAMP(6)
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@job) AND status='running' AND lease_owner=@owner
              AND lease_until > UTC_TIMESTAMP(6);
            """, connection);
        command.Parameters.AddWithValue("@step", step);
        command.Parameters.AddWithValue("@tenant", lease.TenantId.ToString());
        command.Parameters.AddWithValue("@job", lease.JobId.ToString());
        command.Parameters.AddWithValue("@owner", lease.LeaseOwner);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <inheritdoc />
    public async Task<bool> CompleteAsync(KnowledgeExtractJobLease lease, KnowledgeExtractCompletion completion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completion);
        var (candidatesJson, dropped) = SerializeCandidates(completion.Candidates);
        var title = string.IsNullOrWhiteSpace(completion.PageTitle) ? null : TruncateRunes(completion.PageTitle.Trim(), MaxPageTitleCharacters);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        // 同一语句内的 UTC_TIMESTAMP(6) 取值相同，expires_at 恰为 completed_at + 24 小时。
        await using var command = new MySqlCommand("""
            UPDATE brand_knowledge_extract_jobs SET status='done', step='finalize', page_title=@title, page_hash=@hash,
                page_characters=@characters, truncated=@truncated, candidates_json=@candidates, model_name=@model, provider=@provider,
                duration_ms=@duration, error_code=NULL, error_message=NULL, lease_owner=NULL, lease_until=NULL,
                completed_at=UTC_TIMESTAMP(6), expires_at=DATE_ADD(UTC_TIMESTAMP(6), INTERVAL @lifetime SECOND), updated_at=UTC_TIMESTAMP(6)
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@job) AND status='running' AND lease_owner=@owner
              AND lease_until > UTC_TIMESTAMP(6);
            """, connection);
        command.Parameters.AddWithValue("@title", title ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@hash", completion.PageHash);
        command.Parameters.AddWithValue("@characters", Math.Max(0, completion.PageCharacters));
        command.Parameters.AddWithValue("@truncated", completion.Truncated || dropped > 0);
        command.Parameters.AddWithValue("@candidates", candidatesJson);
        command.Parameters.AddWithValue("@model", completion.ModelName);
        command.Parameters.AddWithValue("@provider", completion.Provider);
        command.Parameters.AddWithValue("@duration", Math.Max(0, completion.DurationMs));
        command.Parameters.AddWithValue("@lifetime", (int)BrandKnowledgeRules.CandidateLifetime.TotalSeconds);
        command.Parameters.AddWithValue("@tenant", lease.TenantId.ToString());
        command.Parameters.AddWithValue("@job", lease.JobId.ToString());
        command.Parameters.AddWithValue("@owner", lease.LeaseOwner);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <inheritdoc />
    public async Task<bool> FailAsync(KnowledgeExtractJobLease lease, string errorCode, string errorMessage, bool retryable,
        CancellationToken cancellationToken = default)
    {
        var message = TruncateRunes(errorMessage ?? string.Empty, 1000);
        var status = retryable && lease.AttemptCount < MaxAttempts ? "pending" : "failed";
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            UPDATE brand_knowledge_extract_jobs SET status=@status, error_code=@code, error_message=@message,
                next_attempt_at=DATE_ADD(UTC_TIMESTAMP(6), INTERVAL @delay SECOND), lease_owner=NULL, lease_until=NULL,
                completed_at=CASE WHEN @status='failed' THEN UTC_TIMESTAMP(6) ELSE NULL END, updated_at=UTC_TIMESTAMP(6)
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@job) AND status='running' AND lease_owner=@owner
              AND lease_until > UTC_TIMESTAMP(6);
            """, connection);
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@code", errorCode);
        command.Parameters.AddWithValue("@message", message);
        command.Parameters.AddWithValue("@delay", RetryDelaySeconds(lease.AttemptCount));
        command.Parameters.AddWithValue("@tenant", lease.TenantId.ToString());
        command.Parameters.AddWithValue("@job", lease.JobId.ToString());
        command.Parameters.AddWithValue("@owner", lease.LeaseOwner);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>重试退避秒数：30 秒起按尝试次数翻倍，最多翻倍 6 次（与产品识别相同的口径）。</summary>
    /// <param name="attemptCount">包含本次在内的尝试次数。</param>
    public static int RetryDelaySeconds(int attemptCount) => Math.Min(3600, 30 * (1 << Math.Clamp(attemptCount - 1, 0, 6)));

    /// <summary>校验并规范化识别网址：公开 http(s)、不超过 2000 字符；返回抓取用网址与去重 hash。</summary>
    /// <param name="url">用户输入的网址。</param>
    /// <exception cref="CollectorStoreException">KNOWLEDGE_ITEM_VALIDATION（400）。</exception>
    public static (string Url, string UrlHash) NormalizeUrl(string? url)
    {
        var input = url?.Trim();
        if (string.IsNullOrEmpty(input) || input.Length > MaxUrlLength) throw InvalidUrl();
        try
        {
            var fetch = ArticleSourceReader.NormalizeFetchUrl(input);
            var dedupe = ArticleSourceReader.NormalizeAndValidate(input);
            if (fetch.Length > 2048) throw InvalidUrl();
            return (fetch, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dedupe))).ToLowerInvariant());
        }
        catch (CollectorStoreException exception) when (exception.Code != "KNOWLEDGE_ITEM_VALIDATION") { throw InvalidUrl(); }
        catch (UriFormatException) { throw InvalidUrl(); }
    }

    /// <summary>序列化候选：超过 <see cref="CandidatesJsonBudget"/> 字节时从末尾丢弃候选，返回 JSON 与丢弃数。</summary>
    /// <param name="candidates">候选。</param>
    public static (string Json, int Dropped) SerializeCandidates(IReadOnlyList<KnowledgeExtractCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var list = candidates.ToList();
        var dropped = 0;
        while (true)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(list, JsonOptions);
            if (bytes.Length <= CandidatesJsonBudget || list.Count == 0) return (Encoding.UTF8.GetString(bytes), dropped);
            list.RemoveAt(list.Count - 1);
            dropped++;
        }
    }

    /// <summary>反序列化候选；内容损坏时返回空列表。</summary>
    /// <param name="json">candidates_json。</param>
    public static IReadOnlyList<KnowledgeExtractCandidate> DeserializeCandidates(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<KnowledgeExtractCandidate>>(json, JsonOptions) ?? []; }
        catch (JsonException) { return []; }
    }

    private static string? SerializeDimensions(IReadOnlyList<BrandKnowledgeDimension>? dimensions)
    {
        if (dimensions is null || dimensions.Count == 0) return null;
        if (dimensions.Any(dimension => !Enum.IsDefined(dimension)))
            throw Error("KNOWLEDGE_ITEM_VALIDATION", "请选择有效的维度。", 400);
        var keys = BrandKnowledgeRules.Dimensions.Where(item => dimensions.Contains(item.Dimension)).Select(item => item.Key).ToArray();
        return keys.Length == BrandKnowledgeRules.Dimensions.Count ? null : JsonSerializer.Serialize(keys, JsonOptions);
    }

    private static IReadOnlyList<BrandKnowledgeDimension>? DeserializeDimensions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var keys = JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? [];
            var parsed = new List<BrandKnowledgeDimension>();
            foreach (var key in keys)
                if (BrandKnowledgeRules.TryParseDimension(key, out var dimension) && !parsed.Contains(dimension)) parsed.Add(dimension);
            return parsed.Count == 0 ? null : parsed;
        }
        catch (JsonException) { return null; }
    }

    private sealed record JobState(string Status, DateTimeOffset? ExpiresAt, DateTimeOffset? ResolvedAt);

    private static async Task ExpireJobAsync(MySqlConnection connection, MySqlTransaction? transaction, Guid tenantId, Guid siteId, Guid jobId,
        CancellationToken cancellationToken)
        => await ExecuteAsync(connection, transaction, ExpireSql + " AND id=UUID_TO_BIN(@job);", cancellationToken,
            ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@job", jobId.ToString()));

    private static async Task<JobState?> LockStateAsync(MySqlConnection connection, MySqlTransaction transaction, Guid tenantId, Guid siteId,
        Guid jobId, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand("""
            SELECT status, expires_at, resolved_at FROM brand_knowledge_extract_jobs
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND id=UUID_TO_BIN(@job) FOR UPDATE;
            """, connection, transaction);
        command.Parameters.AddWithValue("@tenant", tenantId.ToString());
        command.Parameters.AddWithValue("@site", siteId.ToString());
        command.Parameters.AddWithValue("@job", jobId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new JobState(reader.GetString(0), NullableUtc(reader, 1), NullableUtc(reader, 2));
    }

    private static async Task<KnowledgeExtractJobView?> ReadViewAsync(MySqlConnection connection, MySqlTransaction? transaction, Guid tenantId,
        Guid siteId, Guid jobId, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand($"""
            SELECT {JobColumns} FROM brand_knowledge_extract_jobs j
            WHERE j.tenant_id=UUID_TO_BIN(@tenant) AND j.site_id=UUID_TO_BIN(@site) AND j.id=UUID_TO_BIN(@job) LIMIT 1;
            """, connection, transaction);
        command.Parameters.AddWithValue("@tenant", tenantId.ToString());
        command.Parameters.AddWithValue("@site", siteId.ToString());
        command.Parameters.AddWithValue("@job", jobId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadJob(reader) : null;
    }

    private static async Task<int> CountTodayAsync(MySqlConnection connection, MySqlTransaction transaction, Guid tenantId,
        CancellationToken cancellationToken)
    {
        // 与 KnowledgeProductStore 的产品识别频控同一口径：created_at >= UTC_DATE()。
        await using var command = new MySqlCommand("""
            SELECT (SELECT COUNT(*) FROM brand_knowledge_extract_jobs WHERE tenant_id=UUID_TO_BIN(@tenant) AND created_at >= UTC_DATE())
                 + (SELECT COUNT(*) FROM knowledge_product_parse_jobs WHERE tenant_id=UUID_TO_BIN(@tenant) AND created_at >= UTC_DATE());
            """, connection, transaction);
        command.Parameters.AddWithValue("@tenant", tenantId.ToString());
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task AssertSiteAsync(MySqlConnection connection, MySqlTransaction? transaction, Guid tenantId, Guid siteId,
        CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(
            "SELECT 1 FROM sites WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@site) AND status='active';", connection, transaction);
        command.Parameters.AddWithValue("@tenant", tenantId.ToString());
        command.Parameters.AddWithValue("@site", siteId.ToString());
        if (await command.ExecuteScalarAsync(cancellationToken) is null) throw Error("RESOURCE_NOT_FOUND", "品牌不存在。", 404);
    }

    private static KnowledgeExtractJobView ReadJob(MySqlDataReader reader)
        => new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3), NullableString(reader, 4),
            DeserializeCandidates(NullableString(reader, 5)), reader.GetBoolean(6), NullableString(reader, 7), NullableString(reader, 8),
            Utc(reader, 9), NullableUtc(reader, 10), NullableUtc(reader, 11));

    private static CollectorStoreException NotSavable(JobState state)
    {
        if (state.Status == "discarded" && state.ExpiresAt is not null && state.ResolvedAt is not null && state.ResolvedAt >= state.ExpiresAt)
            return Error("KNOWLEDGE_EXTRACT_EXPIRED", "识别结果已超过 24 小时，请重新识别。", 410);
        return state.Status switch
        {
            "pending" or "running" => Error("KNOWLEDGE_EXTRACT_NOT_READY", "识别尚未完成，请稍后再试。", 409),
            "saved" => Error("KNOWLEDGE_EXTRACT_NOT_READY", "识别结果已保存。", 409),
            "discarded" => Error("KNOWLEDGE_EXTRACT_NOT_READY", "识别结果已放弃，请重新识别。", 409),
            _ => Error("KNOWLEDGE_EXTRACT_NOT_READY", "识别未成功完成，没有可保存的候选。", 409)
        };
    }

    private static string TruncateRunes(string value, int maxRunes)
    {
        if (value.Length <= maxRunes) return value;
        var builder = new StringBuilder();
        var count = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (count++ >= maxRunes) break;
            builder.Append(rune.ToString());
        }
        return builder.ToString();
    }

    private static string? NullableString(MySqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static DateTimeOffset Utc(MySqlDataReader reader, int ordinal) => new(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));
    private static DateTimeOffset? NullableUtc(MySqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : Utc(reader, ordinal);

    private static async Task ExecuteAsync(MySqlConnection connection, MySqlTransaction? transaction, string sql,
        CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = new MySqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static CollectorStoreException InvalidUrl() => Error("KNOWLEDGE_ITEM_VALIDATION", "请输入公开的 http(s) 网址（不超过 2000 字符）。", 400);
    private static CollectorStoreException NotFound() => Error("KNOWLEDGE_EXTRACT_NOT_FOUND", "识别任务不存在。", 404);
    private static CollectorStoreException Error(string code, string message, int status) => new(code, message, status);
}
