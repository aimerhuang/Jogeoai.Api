using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Jogeoai.Application;
using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

/// <summary>产品信息实体、识别任务租约与核验入库的 MySQL 实现（依赖 023 迁移；每日频控与已迁移判断依赖 043 迁移）。</summary>
public sealed class KnowledgeProductStore(MySqlDatabase database, IKnowledgeStore knowledge, IManagementStore? management = null)
    : IKnowledgeProductStore
{
    /// <summary>每租户每日（UTC）识别次数上限。</summary>
    public const int DailyParseLimit = KnowledgeProductRules.DailyParseLimit;
    /// <summary>识别任务最大尝试次数。</summary>
    public const int MaxAttempts = 3;
    /// <summary>租约多次过期后在领取时放弃任务所写入的错误码。</summary>
    public const string ParseInterruptedCode = "PRODUCT_PARSE_INTERRUPTED";
    private const string ParseInterruptedMessage = "识别多次中断，请重新发起。";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> SourceKinds = ["manual", "file", "url", "completion"];
    private static readonly HashSet<string> ParseableExtensions = [".pdf", ".docx", ".md", ".markdown", ".txt", ".jpg", ".jpeg", ".png"];

    private const string ProductColumns = """
        BIN_TO_UUID(p.id), BIN_TO_UUID(p.tenant_id), BIN_TO_UUID(p.site_id), p.name, p.status, p.source_kind,
        p.introduction, p.advantages, p.scenarios, p.cases, p.specs_json, p.product_url, p.field_sources_json,
        BIN_TO_UUID(p.source_document_id), BIN_TO_UUID(p.source_version_id), BIN_TO_UUID(p.knowledge_document_id),
        p.approved_at, BIN_TO_UUID(p.approved_by), BIN_TO_UUID(p.created_by), p.created_at, p.updated_at,
        (SELECT JSON_ARRAYAGG(t.tag) FROM knowledge_product_tags t WHERE t.tenant_id=p.tenant_id AND t.product_id=p.id),
        BIN_TO_UUID(j.id), j.job_kind, j.status, j.step, j.attempt_count, j.error_code, j.error_message, j.model_name,
        j.image_count, j.truncated, j.duration_ms, j.created_at, j.started_at, j.completed_at
        """;

    private const string ProductFrom = """
        FROM knowledge_products p
        LEFT JOIN LATERAL (
            SELECT lj.* FROM knowledge_product_parse_jobs lj
            WHERE lj.tenant_id=p.tenant_id AND lj.product_id=p.id
            ORDER BY lj.created_at DESC, lj.id DESC LIMIT 1
        ) j ON TRUE
        """;

    private const string JobColumns = """
        BIN_TO_UUID(j.id), BIN_TO_UUID(j.product_id), j.job_kind, j.status, j.step, j.attempt_count, j.error_code, j.error_message,
        j.model_name, j.image_count, j.truncated, j.duration_ms, j.created_at, j.started_at, j.completed_at
        """;

    /// <inheritdoc />
    public async Task<KnowledgeProductListPage> ListAsync(Guid tenantId, Guid siteId, KnowledgeProductListFilter filter,
        string? cursor = null, int limit = 20, CancellationToken cancellationToken = default)
    {
        filter = NormalizeFilter(filter);
        limit = Math.Clamp(limit, 1, 100);
        var status = filter.Status is null ? null : ToDb(filter.Status.Value);
        var contextHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            tenantId, siteId, status, filter.SourceKind, filter.Q
        }))).ToLowerInvariant();
        var (cursorTimestamp, cursorId) = KnowledgeStore.ParseCursor(cursor, contextHash);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await AssertSiteAsync(connection, null, tenantId, siteId, cancellationToken);
        const string baseWhere = """
            WHERE p.tenant_id=UUID_TO_BIN(@tenant) AND p.site_id=UUID_TO_BIN(@site)
              AND (@source IS NULL OR p.source_kind=@source)
              AND (@q IS NULL OR p.name LIKE CONCAT('%', @q, '%'))
            """;
        var items = new List<KnowledgeProductView>();
        await using (var command = new MySqlCommand($"""
            SELECT {ProductColumns}
            {ProductFrom}
            {baseWhere}
              AND (@status IS NULL OR p.status=@status)
              AND (@cursor IS NULL OR p.updated_at < @cursor
                   OR (p.updated_at = @cursor AND (@cursor_id IS NULL OR p.id < UUID_TO_BIN(@cursor_id))))
            ORDER BY p.updated_at DESC, p.id DESC
            LIMIT @limit;
            """, connection))
        {
            BindFilter(command, tenantId, siteId, status, filter);
            command.Parameters.AddWithValue("@cursor", cursorTimestamp ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@cursor_id", cursorId?.ToString() ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@limit", limit + 1);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) items.Add(ReadProduct(reader));
        }
        var hasNext = items.Count > limit;
        if (hasNext) items.RemoveAt(limit);
        var nextCursor = hasNext && items.Count > 0
            ? KnowledgeStore.EncodeCursor(items[^1].UpdatedAt.UtcDateTime, items[^1].ProductId, contextHash) : null;
        long total, pending;
        await using (var command = new MySqlCommand($"""
            SELECT COUNT(*) FROM knowledge_products p {baseWhere} AND (@status IS NULL OR p.status=@status);
            """, connection))
        {
            BindFilter(command, tenantId, siteId, status, filter);
            total = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }
        await using (var command = new MySqlCommand($"""
            SELECT COUNT(*) FROM knowledge_products p {baseWhere} AND p.status='pending_review';
            """, connection))
        {
            BindFilter(command, tenantId, siteId, null, filter);
            pending = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }
        return new KnowledgeProductListPage(items, nextCursor, total, pending);
    }

    /// <inheritdoc />
    public async Task<KnowledgeProductView?> GetAsync(Guid tenantId, Guid siteId, Guid productId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        return await GetAsync(connection, null, tenantId, siteId, productId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<KnowledgeProductOption>> ListApprovedAsync(Guid tenantId, Guid siteId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            SELECT BIN_TO_UUID(id), name FROM knowledge_products
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND status='approved'
            ORDER BY name LIMIT 500;
            """, connection);
        command.Parameters.AddWithValue("@tenant", tenantId.ToString());
        command.Parameters.AddWithValue("@site", siteId.ToString());
        var result = new List<KnowledgeProductOption>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new KnowledgeProductOption(Guid.Parse(reader.GetString(0)), reader.GetString(1)));
        return result;
    }

    /// <inheritdoc />
    public async Task<KnowledgeProductView> CreateAsync(Guid tenantId, Guid siteId, Guid userId, KnowledgeProductWrite request,
        CancellationToken cancellationToken = default)
    {
        var fields = KnowledgeProductRules.Normalize(request, requireIntroduction: false);
        var sources = KnowledgeProductRules.ApplyManualEdit(null, fields, null);
        var productId = Guid.NewGuid();
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await AssertSiteAsync(connection, null, tenantId, siteId, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO knowledge_products
                    (id, tenant_id, site_id, name, status, source_kind, introduction, advantages, scenarios, cases, specs_json,
                     product_url, field_sources_json, created_by, updated_by, created_at, updated_at)
                VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), @name, 'draft', 'manual', @introduction,
                        @advantages, @scenarios, @cases, @specs, @url, @sources, UUID_TO_BIN(@user), UUID_TO_BIN(@user),
                        UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                """, cancellationToken, ("@id", productId.ToString()), ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()),
                ("@name", fields.Name), ("@introduction", fields.Introduction), ("@advantages", fields.Advantages),
                ("@scenarios", fields.Scenarios), ("@cases", fields.Cases), ("@specs", SerializeSpecs(fields.Specs)),
                ("@url", fields.ProductUrl), ("@sources", JsonSerializer.Serialize(sources, JsonOptions)), ("@user", userId.ToString()));
            await ReplaceTagsAsync(connection, transaction, tenantId, productId, fields.Tags ?? [], cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            throw Error("PRODUCT_NAME_CONFLICT", "同品牌下已存在同名产品。", 409);
        }
        return (await GetAsync(connection, null, tenantId, siteId, productId, cancellationToken))!;
    }

    /// <inheritdoc />
    public async Task<KnowledgeProductView?> UpdateDraftAsync(Guid tenantId, Guid siteId, Guid productId, Guid userId,
        KnowledgeProductWrite request, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var resync = false;
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            var current = await LockProductAsync(connection, transaction, tenantId, siteId, productId, cancellationToken);
            if (current is null) { await transaction.RollbackAsync(CancellationToken.None); return null; }
            if (current.Status == "parsing") throw Error("PRODUCT_PARSE_IN_PROGRESS", "识别进行中，请等待完成或取消识别后再保存。", 409);
            if (current.Status == "archived") throw Error("PRODUCT_STATE_CONFLICT", "已归档的产品不能修改。", 409);
            var approved = current.Status == "approved";
            var fields = KnowledgeProductRules.Normalize(request, requireIntroduction: approved);
            var sources = KnowledgeProductRules.ApplyManualEdit(current.Fields, fields, current.Sources);
            var nextStatus = approved ? "approved"
                : current.Status is "draft" or "parse_failed" && fields.Introduction is not null ? "pending_review" : current.Status;
            try
            {
                await ExecuteAsync(connection, transaction, """
                    UPDATE knowledge_products SET name=@name, introduction=@introduction, advantages=@advantages, scenarios=@scenarios,
                        cases=@cases, specs_json=@specs, product_url=@url, field_sources_json=@sources, status=@status,
                        updated_by=UUID_TO_BIN(@user), updated_at=UTC_TIMESTAMP(6)
                    WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@id);
                    """, cancellationToken, ("@name", fields.Name), ("@introduction", fields.Introduction), ("@advantages", fields.Advantages),
                    ("@scenarios", fields.Scenarios), ("@cases", fields.Cases), ("@specs", SerializeSpecs(fields.Specs)), ("@url", fields.ProductUrl),
                    ("@sources", JsonSerializer.Serialize(sources, JsonOptions)), ("@status", nextStatus), ("@user", userId.ToString()),
                    ("@tenant", tenantId.ToString()), ("@id", productId.ToString()));
                if (request.Tags is not null)
                    await ReplaceTagsAsync(connection, transaction, tenantId, productId, fields.Tags ?? [], cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (MySqlException exception) when (exception.Number == 1062)
            {
                throw Error("PRODUCT_NAME_CONFLICT", "同品牌下已存在同名产品。", 409);
            }
            resync = approved;
        }
        // 已入库产品的编辑直接生效，并在事务外重新同步知识库；失败时 knowledge_document_id 保持原值，可通过 sync-knowledge 重试。
        if (resync) await TrySyncKnowledgeAsync(connection, tenantId, siteId, productId, cancellationToken);
        return await GetAsync(connection, null, tenantId, siteId, productId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ProductParseJobView> EnqueueParseAsync(Guid tenantId, Guid siteId, Guid productId, Guid userId,
        ProductParseRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var kind = ToDb(request.Kind);
        string? productUrl = null;
        if (request.Kind == ProductParseJobKind.Url)
        {
            if (string.IsNullOrWhiteSpace(request.Url)) throw Error("PRODUCT_VALIDATION", "网址识别需要提供产品页面网址。", 400);
            productUrl = KnowledgeProductRules.Normalize(new KnowledgeProductWrite("x", ProductUrl: request.Url), false).ProductUrl;
            if (request.DocumentId is null) throw Error("PRODUCT_PARSE_SOURCE_UNSUPPORTED", "网址尚未读取为链接资料。", 400);
        }
        if (request.Kind == ProductParseJobKind.File && request.DocumentId is null)
            throw Error("PRODUCT_PARSE_SOURCE_UNSUPPORTED", "文件识别需要指定素材库资料。", 400);

        var jobId = Guid.NewGuid();
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // 锁租户行以串行化频控计数，再锁产品行校验状态。
        await using (var lockTenant = new MySqlCommand("SELECT id FROM tenants WHERE id=UUID_TO_BIN(@tenant) FOR UPDATE;", connection, transaction))
        {
            lockTenant.Parameters.AddWithValue("@tenant", tenantId.ToString());
            if (await lockTenant.ExecuteScalarAsync(cancellationToken) is null) throw Error("RESOURCE_NOT_FOUND", "租户不存在。", 404);
        }
        var product = await LockProductAsync(connection, transaction, tenantId, siteId, productId, cancellationToken)
            ?? throw Error("PRODUCT_NOT_FOUND", "产品信息不存在。", 404);
        if (product.Status is not ("draft" or "pending_review" or "parse_failed" or "approved"))
            throw Error(product.Status == "parsing" ? "PRODUCT_PARSE_IN_PROGRESS" : "PRODUCT_STATE_CONFLICT",
                product.Status == "parsing" ? "该产品已有进行中的识别任务。" : "当前状态不能发起识别。", 409);

        Guid? sourceDocumentId = null;
        Guid? sourceVersionId = null;
        if (request.DocumentId is Guid documentId)
        {
            var source = await ReadSourceAsync(connection, transaction, tenantId, siteId, documentId, cancellationToken)
                ?? throw Error("PRODUCT_PARSE_SOURCE_UNSUPPORTED", "来源资料不存在或不属于当前品牌。", 400);
            if (source.DocumentStatus == "disabled") throw Error("PRODUCT_PARSE_SOURCE_UNSUPPORTED", "来源资料已停用。", 400);
            if (source.VersionId is null || source.VersionStatus is "failed" or "disabled")
                throw Error("PRODUCT_PARSE_SOURCE_UNSUPPORTED", "来源文件没有可用版本，请重新上传。", 400);
            if (!ParseableExtensions.Contains(source.Extension ?? string.Empty))
                throw Error("PRODUCT_PARSE_SOURCE_UNSUPPORTED", "来源文件格式不支持识别。", 400);
            sourceDocumentId = documentId;
            sourceVersionId = source.VersionId;
        }

        var today = await CountTodayAsync(connection, transaction, tenantId, cancellationToken);
        if (today >= DailyParseLimit)
            throw Error("PRODUCT_PARSE_RATE_LIMITED", $"今日识别次数已达上限（{DailyParseLimit} 次，与网址识别共用），请明天再试。", 429);
        try
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO knowledge_product_parse_jobs
                    (id, tenant_id, site_id, product_id, job_kind, source_document_id, source_version_id, status, step,
                     next_attempt_at, requested_by, created_at, updated_at)
                VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), UUID_TO_BIN(@product), @kind,
                        UUID_TO_BIN(@document), UUID_TO_BIN(@version), 'pending', 'read_source', UTC_TIMESTAMP(6),
                        UUID_TO_BIN(@user), UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                """, cancellationToken, ("@id", jobId.ToString()), ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()),
                ("@product", productId.ToString()), ("@kind", kind), ("@document", sourceDocumentId?.ToString()),
                ("@version", sourceVersionId?.ToString()), ("@user", userId.ToString()));
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            throw Error("PRODUCT_PARSE_IN_PROGRESS", "该产品已有进行中的识别任务。", 409);
        }
        await ExecuteAsync(connection, transaction, """
            UPDATE knowledge_products SET status='parsing', source_kind=@kind,
                source_document_id=COALESCE(UUID_TO_BIN(@document), source_document_id),
                source_version_id=CASE WHEN @document IS NULL THEN source_version_id ELSE UUID_TO_BIN(@version) END,
                product_url=COALESCE(@url, product_url), updated_by=UUID_TO_BIN(@user), updated_at=UTC_TIMESTAMP(6)
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@product);
            """, cancellationToken, ("@kind", kind), ("@document", sourceDocumentId?.ToString()), ("@version", sourceVersionId?.ToString()),
            ("@url", productUrl), ("@user", userId.ToString()), ("@tenant", tenantId.ToString()), ("@product", productId.ToString()));
        await transaction.CommitAsync(cancellationToken);
        return (await GetJobAsync(tenantId, siteId, productId, jobId, cancellationToken))!;
    }

    /// <inheritdoc />
    public async Task<ProductParseJobView?> GetJobAsync(Guid tenantId, Guid siteId, Guid productId, Guid jobId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand($"""
            SELECT {JobColumns} FROM knowledge_product_parse_jobs j
            WHERE j.tenant_id=UUID_TO_BIN(@tenant) AND j.site_id=UUID_TO_BIN(@site) AND j.product_id=UUID_TO_BIN(@product)
              AND j.id=UUID_TO_BIN(@job) LIMIT 1;
            """, connection);
        command.Parameters.AddWithValue("@tenant", tenantId.ToString());
        command.Parameters.AddWithValue("@site", siteId.ToString());
        command.Parameters.AddWithValue("@product", productId.ToString());
        command.Parameters.AddWithValue("@job", jobId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadJob(reader, 0) : null;
    }

    /// <inheritdoc />
    public async Task<ProductParseJobView?> CancelJobAsync(Guid tenantId, Guid siteId, Guid productId, Guid jobId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        string? status;
        await using (var command = new MySqlCommand("""
            SELECT status FROM knowledge_product_parse_jobs
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND product_id=UUID_TO_BIN(@product) AND id=UUID_TO_BIN(@job)
            FOR UPDATE;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("@tenant", tenantId.ToString());
            command.Parameters.AddWithValue("@site", siteId.ToString());
            command.Parameters.AddWithValue("@product", productId.ToString());
            command.Parameters.AddWithValue("@job", jobId.ToString());
            status = await command.ExecuteScalarAsync(cancellationToken) as string;
        }
        if (status is null) { await transaction.RollbackAsync(CancellationToken.None); return null; }
        if (status != "pending") throw Error("PRODUCT_STATE_CONFLICT", "只能取消尚未开始的识别任务。", 409);
        await ExecuteAsync(connection, transaction, """
            UPDATE knowledge_product_parse_jobs SET status='cancelled', lease_owner=NULL, lease_until=NULL,
                completed_at=UTC_TIMESTAMP(6), updated_at=UTC_TIMESTAMP(6)
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@job) AND status='pending';
            UPDATE knowledge_products SET status=CASE WHEN approved_at IS NOT NULL THEN 'approved' ELSE 'draft' END, updated_at=UTC_TIMESTAMP(6)
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@product) AND status='parsing';
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@job", jobId.ToString()), ("@product", productId.ToString()));
        await transaction.CommitAsync(cancellationToken);
        return await GetJobAsync(tenantId, siteId, productId, jobId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> CountParseJobsTodayAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        return await CountTodayAsync(connection, null, tenantId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> IsMigratedAsync(Guid tenantId, Guid productId, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        // 不过滤 deleted_at：条目被删除后产品仍视为已迁移，旧接口不能再改写已被接管的文档。
        await using var command = new MySqlCommand("""
            SELECT 1 FROM brand_knowledge_items
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND legacy_product_id=UUID_TO_BIN(@product) LIMIT 1;
            """, connection);
        command.Parameters.AddWithValue("@tenant", tenantId.ToString());
        command.Parameters.AddWithValue("@product", productId.ToString());
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    /// <inheritdoc />
    public async Task<ProductParseJobLease?> ClaimParseJobAsync(string workerId, TimeSpan lease,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            SELECT BIN_TO_UUID(j.id), BIN_TO_UUID(j.tenant_id), BIN_TO_UUID(j.site_id), BIN_TO_UUID(j.product_id), j.job_kind,
                   BIN_TO_UUID(j.source_document_id), BIN_TO_UUID(j.source_version_id), v.object_key, v.extension,
                   BIN_TO_UUID(j.requested_by), j.attempt_count, j.status
            FROM knowledge_product_parse_jobs j
            JOIN knowledge_products p ON p.tenant_id=j.tenant_id AND p.id=j.product_id
            LEFT JOIN knowledge_document_versions v ON v.tenant_id=j.tenant_id AND v.id=j.source_version_id
            WHERE ((j.status='pending' AND j.next_attempt_at <= UTC_TIMESTAMP(6))
                OR (j.status='running' AND j.lease_until < UTC_TIMESTAMP(6)))
              AND p.status <> 'archived'
            ORDER BY j.next_attempt_at, j.created_at, j.id
            LIMIT 1 FOR UPDATE OF j SKIP LOCKED;
            """, connection, transaction);
        ProductParseJobLease? job = null;
        var status = string.Empty;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                job = new ProductParseJobLease(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)),
                    Guid.Parse(reader.GetString(3)), ParseKind(reader.GetString(4)),
                    reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5)), reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6)),
                    reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8),
                    Guid.Parse(reader.GetString(9)), reader.GetInt32(10), workerId);
                status = reader.GetString(11);
            }
        }
        if (job is null) { await transaction.RollbackAsync(CancellationToken.None); return null; }
        // 此处的 AttemptCount 是库里的值（本次领取尚未加一），即已经开始过的执行次数。
        if (status == "running" && job.AttemptCount >= MaxAttempts)
        {
            // running 且被选中说明租约已过期（Worker 被杀或心跳中断）。次数用尽后在同一事务内记为失败并让出队列，
            // 否则重启后的 Worker 会无限重领同一个任务。与 FailParseAsync 的失败分支写同一组状态，不带租约持有者条件；
            // active_key 是按 status 生成的列，任务失败后自动变为 NULL，产品可以再次发起识别。
            await ExecuteAsync(connection, transaction, """
                UPDATE knowledge_product_parse_jobs SET status='failed', error_code=@code, error_message=@message,
                    lease_owner=NULL, lease_until=NULL, completed_at=UTC_TIMESTAMP(6), updated_at=UTC_TIMESTAMP(6)
                WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@job) AND status='running';
                UPDATE knowledge_products SET status='parse_failed', updated_at=UTC_TIMESTAMP(6)
                WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@product) AND status='parsing';
                """, cancellationToken, ("@code", ParseInterruptedCode), ("@message", ParseInterruptedMessage),
                ("@tenant", job.TenantId.ToString()), ("@job", job.JobId.ToString()), ("@product", job.ProductId.ToString()));
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        await ExecuteAsync(connection, transaction, """
            UPDATE knowledge_product_parse_jobs SET status='running', lease_owner=@owner,
                lease_until=DATE_ADD(UTC_TIMESTAMP(6), INTERVAL @seconds SECOND), attempt_count=attempt_count+1,
                started_at=COALESCE(started_at, UTC_TIMESTAMP(6)), error_code=NULL, error_message=NULL, updated_at=UTC_TIMESTAMP(6)
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@id);
            """, cancellationToken, ("@owner", workerId), ("@seconds", Math.Max(5, (int)lease.TotalSeconds)),
            ("@tenant", job.TenantId.ToString()), ("@id", job.JobId.ToString()));
        await transaction.CommitAsync(cancellationToken);
        return job with { AttemptCount = job.AttemptCount + 1 };
    }

    /// <inheritdoc />
    public async Task<bool> HeartbeatAsync(ProductParseJobLease lease, TimeSpan extend, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            UPDATE knowledge_product_parse_jobs SET lease_until=DATE_ADD(UTC_TIMESTAMP(6), INTERVAL @seconds SECOND), updated_at=UTC_TIMESTAMP(6)
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@job) AND status='running' AND lease_owner=@owner;
            """, connection);
        command.Parameters.AddWithValue("@seconds", Math.Max(5, (int)extend.TotalSeconds));
        command.Parameters.AddWithValue("@tenant", lease.TenantId.ToString());
        command.Parameters.AddWithValue("@job", lease.JobId.ToString());
        command.Parameters.AddWithValue("@owner", lease.LeaseOwner);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <inheritdoc />
    public async Task MarkStepAsync(ProductParseJobLease lease, string step, string? inputSummaryJson, int imageCount,
        CancellationToken cancellationToken = default)
    {
        if (step is not ("read_source" or "model" or "finalize")) throw new ArgumentOutOfRangeException(nameof(step));
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            UPDATE knowledge_product_parse_jobs SET step=@step, input_summary_json=COALESCE(@summary, input_summary_json),
                image_count=GREATEST(image_count, @images), updated_at=UTC_TIMESTAMP(6)
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@job) AND status='running' AND lease_owner=@owner
              AND (lease_until IS NULL OR lease_until > UTC_TIMESTAMP(6));
            """, connection);
        command.Parameters.AddWithValue("@step", step);
        command.Parameters.AddWithValue("@summary", inputSummaryJson is null ? DBNull.Value : inputSummaryJson);
        command.Parameters.AddWithValue("@images", imageCount);
        command.Parameters.AddWithValue("@tenant", lease.TenantId.ToString());
        command.Parameters.AddWithValue("@job", lease.JobId.ToString());
        command.Parameters.AddWithValue("@owner", lease.LeaseOwner);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            throw Error("PRODUCT_PARSE_STALE_LEASE", "识别任务租约已失效。", 409);
    }

    /// <inheritdoc />
    public async Task CompleteParseAsync(ProductParseJobLease lease, ProductParseResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await EnsureLeaseAsync(connection, transaction, lease, cancellationToken);
        var current = await LockProductAsync(connection, transaction, lease.TenantId, lease.SiteId, lease.ProductId, cancellationToken)
            ?? throw Error("PRODUCT_NOT_FOUND", "产品信息不存在。", 404);
        if (current.Status == "archived") throw Error("PRODUCT_STATE_CONFLICT", "产品已归档，识别结果不再写入。", 409);
        var (fields, sources) = KnowledgeProductRules.Merge(current.Fields, current.Sources, result);
        await ExecuteAsync(connection, transaction, """
            UPDATE knowledge_products SET introduction=@introduction, advantages=@advantages, scenarios=@scenarios, cases=@cases,
                specs_json=@specs, field_sources_json=@sources, status='pending_review', updated_by=UUID_TO_BIN(@user), updated_at=UTC_TIMESTAMP(6)
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@product);
            UPDATE knowledge_product_parse_jobs SET status='done', step='finalize', model_name=@model, provider=@provider, duration_ms=@duration,
                truncated=@truncated, lease_owner=NULL, lease_until=NULL, completed_at=UTC_TIMESTAMP(6), updated_at=UTC_TIMESTAMP(6)
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@job) AND status='running' AND lease_owner=@owner;
            """, cancellationToken, ("@introduction", fields.Introduction), ("@advantages", fields.Advantages), ("@scenarios", fields.Scenarios),
            ("@cases", fields.Cases), ("@specs", SerializeSpecs(fields.Specs)), ("@sources", JsonSerializer.Serialize(sources, JsonOptions)),
            ("@user", lease.RequestedBy.ToString()), ("@tenant", lease.TenantId.ToString()), ("@product", lease.ProductId.ToString()),
            ("@model", result.ModelName), ("@provider", result.Provider), ("@duration", result.DurationMs), ("@truncated", result.Truncated),
            ("@job", lease.JobId.ToString()), ("@owner", lease.LeaseOwner));
        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task FailParseAsync(ProductParseJobLease lease, string errorCode, string errorMessage, bool retryable,
        CancellationToken cancellationToken = default)
    {
        errorMessage = errorMessage.Length > 1000 ? errorMessage[..1000] : errorMessage;
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await EnsureLeaseAsync(connection, transaction, lease, cancellationToken);
        var status = retryable && lease.AttemptCount < MaxAttempts ? "pending" : "failed";
        var delay = Math.Min(3600, 30 * (1 << Math.Clamp(lease.AttemptCount - 1, 0, 6)));
        await ExecuteAsync(connection, transaction, """
            UPDATE knowledge_product_parse_jobs SET status=@status, error_code=@code, error_message=@message,
                next_attempt_at=DATE_ADD(UTC_TIMESTAMP(6), INTERVAL @delay SECOND), lease_owner=NULL, lease_until=NULL,
                completed_at=CASE WHEN @status='failed' THEN UTC_TIMESTAMP(6) ELSE NULL END, updated_at=UTC_TIMESTAMP(6)
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@job) AND status='running' AND lease_owner=@owner;
            UPDATE knowledge_products SET status=CASE WHEN @status='failed' THEN 'parse_failed' ELSE status END, updated_at=UTC_TIMESTAMP(6)
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@product) AND status='parsing';
            """, cancellationToken, ("@status", status), ("@code", errorCode), ("@message", errorMessage), ("@delay", delay),
            ("@tenant", lease.TenantId.ToString()), ("@job", lease.JobId.ToString()), ("@owner", lease.LeaseOwner),
            ("@product", lease.ProductId.ToString()));
        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<KnowledgeProductView?> ApproveAsync(Guid tenantId, Guid siteId, Guid productId, Guid userId,
        ProductApproveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            var current = await LockProductAsync(connection, transaction, tenantId, siteId, productId, cancellationToken);
            if (current is null) { await transaction.RollbackAsync(CancellationToken.None); return null; }
            if (current.Status == "parsing") throw Error("PRODUCT_PARSE_IN_PROGRESS", "识别进行中，请等待完成或取消识别后再入库。", 409);
            if (current.Status == "archived") throw Error("PRODUCT_STATE_CONFLICT", "已归档的产品不能入库。", 409);
            var fields = KnowledgeProductRules.Normalize(request.Fields, requireIntroduction: true);
            // 用户在核验视图中修改过的字段视为人工填写；其余 AI 字段必须逐个核对。
            var edited = KnowledgeProductRules.ApplyManualEdit(current.Fields, fields, current.Sources);
            var sources = KnowledgeProductRules.Verify(fields, edited, request.VerifiedFields);
            try
            {
                await ExecuteAsync(connection, transaction, """
                    UPDATE knowledge_products SET name=@name, introduction=@introduction, advantages=@advantages, scenarios=@scenarios,
                        cases=@cases, specs_json=@specs, product_url=@url, field_sources_json=@sources, status='approved',
                        approved_at=UTC_TIMESTAMP(6), approved_by=UUID_TO_BIN(@user), updated_by=UUID_TO_BIN(@user), updated_at=UTC_TIMESTAMP(6)
                    WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@id);
                    """, cancellationToken, ("@name", fields.Name), ("@introduction", fields.Introduction), ("@advantages", fields.Advantages),
                    ("@scenarios", fields.Scenarios), ("@cases", fields.Cases), ("@specs", SerializeSpecs(fields.Specs)), ("@url", fields.ProductUrl),
                    ("@sources", JsonSerializer.Serialize(sources, JsonOptions)), ("@user", userId.ToString()),
                    ("@tenant", tenantId.ToString()), ("@id", productId.ToString()));
                if (request.Fields.Tags is not null)
                    await ReplaceTagsAsync(connection, transaction, tenantId, productId, fields.Tags ?? [], cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (MySqlException exception) when (exception.Number == 1062)
            {
                throw Error("PRODUCT_NAME_CONFLICT", "同品牌下已存在同名产品。", 409);
            }
        }
        // 事务外写知识库：模型或对象存储故障不回滚人工核验结果；失败时 knowledge_document_id 为空，可通过 sync-knowledge 重试。
        await TrySyncKnowledgeAsync(connection, tenantId, siteId, productId, cancellationToken);
        return await GetAsync(connection, null, tenantId, siteId, productId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<KnowledgeProductView?> RejectAsync(Guid tenantId, Guid siteId, Guid productId, Guid userId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var current = await LockProductAsync(connection, transaction, tenantId, siteId, productId, cancellationToken);
        if (current is null) { await transaction.RollbackAsync(CancellationToken.None); return null; }
        if (current.Status != "pending_review") throw Error("PRODUCT_STATE_CONFLICT", "只有待核验的产品可以驳回。", 409);
        await ExecuteAsync(connection, transaction, """
            UPDATE knowledge_products SET status='draft', updated_by=UUID_TO_BIN(@user), updated_at=UTC_TIMESTAMP(6)
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@id);
            """, cancellationToken, ("@user", userId.ToString()), ("@tenant", tenantId.ToString()), ("@id", productId.ToString()));
        await transaction.CommitAsync(cancellationToken);
        return await GetAsync(connection, null, tenantId, siteId, productId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<KnowledgeProductView?> ArchiveAsync(Guid tenantId, Guid siteId, Guid productId, Guid userId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        Guid? knowledgeDocumentId;
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            var current = await LockProductAsync(connection, transaction, tenantId, siteId, productId, cancellationToken);
            if (current is null) { await transaction.RollbackAsync(CancellationToken.None); return null; }
            knowledgeDocumentId = current.KnowledgeDocumentId;
            if (current.Status != "archived")
            {
                await ExecuteAsync(connection, transaction, """
                    UPDATE knowledge_product_parse_jobs SET status='cancelled', lease_owner=NULL, lease_until=NULL,
                        completed_at=UTC_TIMESTAMP(6), updated_at=UTC_TIMESTAMP(6)
                    WHERE tenant_id=UUID_TO_BIN(@tenant) AND product_id=UUID_TO_BIN(@id) AND status IN ('pending','running');
                    UPDATE knowledge_products SET status='archived', archived_at=UTC_TIMESTAMP(6), updated_by=UUID_TO_BIN(@user), updated_at=UTC_TIMESTAMP(6)
                    WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@id);
                    """, cancellationToken, ("@user", userId.ToString()), ("@tenant", tenantId.ToString()), ("@id", productId.ToString()));
            }
            await transaction.CommitAsync(cancellationToken);
        }
        if (knowledgeDocumentId is Guid documentId)
        {
            try { await knowledge.DisableDocumentAsync(tenantId, documentId, cancellationToken); }
            catch (CollectorStoreException) { /* 资料已停用或不存在时归档仍然成立。 */ }
        }
        return await GetAsync(connection, null, tenantId, siteId, productId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<KnowledgeProductView?> SyncKnowledgeAsync(Guid tenantId, Guid siteId, Guid productId, Guid userId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var product = await GetAsync(connection, null, tenantId, siteId, productId, cancellationToken);
        if (product is null) return null;
        if (product.Status != KnowledgeProductStatus.Approved) throw Error("PRODUCT_STATE_CONFLICT", "只有已入库的产品可以同步知识库。", 409);
        await SyncKnowledgeCoreAsync(connection, product, cancellationToken);
        return await GetAsync(connection, null, tenantId, siteId, productId, cancellationToken);
    }

    private async Task TrySyncKnowledgeAsync(MySqlConnection connection, Guid tenantId, Guid siteId, Guid productId,
        CancellationToken cancellationToken)
    {
        var product = await GetAsync(connection, null, tenantId, siteId, productId, cancellationToken);
        if (product is null || product.Status != KnowledgeProductStatus.Approved) return;
        try { await SyncKnowledgeCoreAsync(connection, product, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // 入库已提交；知识库同步失败由 knowledge_document_id 为空暴露，用户可通过 sync-knowledge 重试。
        }
    }

    private async Task SyncKnowledgeCoreAsync(MySqlConnection connection, KnowledgeProductView product, CancellationToken cancellationToken)
    {
        var brandName = string.Empty;
        if (management is not null)
            brandName = (await management.GetBrandAsync(product.TenantId, product.SiteId, cancellationToken))?.Name ?? string.Empty;
        var markdown = KnowledgeProductRules.RenderMarkdown(product, brandName);
        var document = await knowledge.UploadProductDocumentAsync(product.TenantId, product.SiteId, product.ProductId, product.Name,
            markdown, product.Tags, product.KnowledgeDocumentId, cancellationToken);
        await ExecuteAsync(connection, null, """
            UPDATE knowledge_products SET knowledge_document_id=UUID_TO_BIN(@document), updated_at=UTC_TIMESTAMP(6)
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@id) AND status='approved';
            """, cancellationToken, ("@document", document.DocumentId.ToString()), ("@tenant", product.TenantId.ToString()),
            ("@id", product.ProductId.ToString()));
    }

    // ---- 行读取与辅助 ----

    private sealed record ProductRow(string Status, KnowledgeProductWrite Fields, IReadOnlyDictionary<string, ProductFieldSource> Sources,
        Guid? KnowledgeDocumentId);

    private sealed record SourceRow(string DocumentStatus, Guid? VersionId, string? VersionStatus, string? Extension);

    private static async Task<KnowledgeProductView?> GetAsync(MySqlConnection connection, MySqlTransaction? transaction, Guid tenantId,
        Guid siteId, Guid productId, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand($"""
            SELECT {ProductColumns}
            {ProductFrom}
            WHERE p.tenant_id=UUID_TO_BIN(@tenant) AND p.site_id=UUID_TO_BIN(@site) AND p.id=UUID_TO_BIN(@id) LIMIT 1;
            """, connection, transaction);
        command.Parameters.AddWithValue("@tenant", tenantId.ToString());
        command.Parameters.AddWithValue("@site", siteId.ToString());
        command.Parameters.AddWithValue("@id", productId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProduct(reader) : null;
    }

    private static async Task<ProductRow?> LockProductAsync(MySqlConnection connection, MySqlTransaction transaction, Guid tenantId,
        Guid siteId, Guid productId, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand("""
            SELECT status, name, introduction, advantages, scenarios, cases, specs_json, product_url, field_sources_json,
                   BIN_TO_UUID(knowledge_document_id)
            FROM knowledge_products
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND id=UUID_TO_BIN(@id)
            FOR UPDATE;
            """, connection, transaction);
        command.Parameters.AddWithValue("@tenant", tenantId.ToString());
        command.Parameters.AddWithValue("@site", siteId.ToString());
        command.Parameters.AddWithValue("@id", productId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var fields = new KnowledgeProductWrite(reader.GetString(1), NullableString(reader, 2), NullableString(reader, 3),
            NullableString(reader, 4), NullableString(reader, 5), DeserializeSpecs(NullableString(reader, 6)), NullableString(reader, 7), null);
        return new ProductRow(reader.GetString(0), fields, DeserializeSources(NullableString(reader, 8)),
            reader.IsDBNull(9) ? null : Guid.Parse(reader.GetString(9)));
    }

    private static async Task<SourceRow?> ReadSourceAsync(MySqlConnection connection, MySqlTransaction transaction, Guid tenantId,
        Guid siteId, Guid documentId, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand("""
            SELECT d.status, BIN_TO_UUID(v.id), v.status, v.extension
            FROM knowledge_documents d
            LEFT JOIN knowledge_document_versions v ON v.tenant_id=d.tenant_id AND v.id=COALESCE(d.current_version_id,
                (SELECT lv.id FROM knowledge_document_versions lv WHERE lv.tenant_id=d.tenant_id AND lv.document_id=d.id
                 ORDER BY lv.version_number DESC LIMIT 1))
            WHERE d.tenant_id=UUID_TO_BIN(@tenant) AND d.id=UUID_TO_BIN(@document)
              AND (d.site_id IS NULL OR d.site_id=UUID_TO_BIN(@site))
            LIMIT 1;
            """, connection, transaction);
        command.Parameters.AddWithValue("@tenant", tenantId.ToString());
        command.Parameters.AddWithValue("@document", documentId.ToString());
        command.Parameters.AddWithValue("@site", siteId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new SourceRow(reader.GetString(0), reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)),
            NullableString(reader, 2), NullableString(reader, 3));
    }

    private static async Task<int> CountTodayAsync(MySqlConnection connection, MySqlTransaction? transaction, Guid tenantId,
        CancellationToken cancellationToken)
    {
        // 与网址识别共用每日额度（PRD D08）：当日产品识别任务数 + 当日网址识别任务数（043 的 brand_knowledge_extract_jobs），
        // 与 KnowledgeExtractJobStore 同一口径（created_at >= UTC_DATE()），两侧入队都先锁租户行再计数。
        await using var command = new MySqlCommand("""
            SELECT (SELECT COUNT(*) FROM knowledge_product_parse_jobs WHERE tenant_id=UUID_TO_BIN(@tenant) AND created_at >= UTC_DATE())
                 + (SELECT COUNT(*) FROM brand_knowledge_extract_jobs WHERE tenant_id=UUID_TO_BIN(@tenant) AND created_at >= UTC_DATE());
            """, connection, transaction);
        command.Parameters.AddWithValue("@tenant", tenantId.ToString());
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task EnsureLeaseAsync(MySqlConnection connection, MySqlTransaction transaction, ProductParseJobLease lease,
        CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand("""
            SELECT status, lease_owner, lease_until FROM knowledge_product_parse_jobs
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@job) FOR UPDATE;
            """, connection, transaction);
        command.Parameters.AddWithValue("@tenant", lease.TenantId.ToString());
        command.Parameters.AddWithValue("@job", lease.JobId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)
            || !string.Equals(reader.GetString(0), "running", StringComparison.Ordinal)
            || !string.Equals(reader.IsDBNull(1) ? null : reader.GetString(1), lease.LeaseOwner, StringComparison.Ordinal)
            || reader.IsDBNull(2) || reader.GetDateTime(2) <= DateTime.UtcNow)
            throw Error("PRODUCT_PARSE_STALE_LEASE", "识别任务租约已失效，请重试任务。", 409);
    }

    private static async Task AssertSiteAsync(MySqlConnection connection, MySqlTransaction? transaction, Guid tenantId, Guid siteId,
        CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(
            "SELECT 1 FROM sites WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@site) AND status='active';", connection, transaction);
        command.Parameters.AddWithValue("@tenant", tenantId.ToString());
        command.Parameters.AddWithValue("@site", siteId.ToString());
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
            throw Error("RESOURCE_NOT_FOUND", "品牌不存在。", 404);
    }

    private static async Task ReplaceTagsAsync(MySqlConnection connection, MySqlTransaction transaction, Guid tenantId, Guid productId,
        IReadOnlyList<string> tags, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, "DELETE FROM knowledge_product_tags WHERE tenant_id=UUID_TO_BIN(@tenant) AND product_id=UUID_TO_BIN(@product);",
            cancellationToken, ("@tenant", tenantId.ToString()), ("@product", productId.ToString()));
        foreach (var tag in tags)
            await ExecuteAsync(connection, transaction, """
                INSERT INTO knowledge_product_tags (tenant_id, product_id, tag, created_at)
                VALUES (UUID_TO_BIN(@tenant), UUID_TO_BIN(@product), @tag, UTC_TIMESTAMP(6));
                """, cancellationToken, ("@tenant", tenantId.ToString()), ("@product", productId.ToString()), ("@tag", tag));
    }

    private static void BindFilter(MySqlCommand command, Guid tenantId, Guid siteId, string? status, KnowledgeProductListFilter filter)
    {
        command.Parameters.AddWithValue("@tenant", tenantId.ToString());
        command.Parameters.AddWithValue("@site", siteId.ToString());
        command.Parameters.AddWithValue("@status", status ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@source", filter.SourceKind ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@q", filter.Q is null ? DBNull.Value : EscapeLike(filter.Q));
    }

    /// <summary>规范化列表筛选：来源取值受限，搜索词去空白并限长。</summary>
    internal static KnowledgeProductListFilter NormalizeFilter(KnowledgeProductListFilter? filter)
    {
        filter ??= new KnowledgeProductListFilter();
        var source = string.IsNullOrWhiteSpace(filter.SourceKind) ? null : filter.SourceKind.Trim().ToLowerInvariant();
        if (source is not null && !SourceKinds.Contains(source)) throw Error("PRODUCT_VALIDATION", "来源筛选无效。", 400);
        var q = string.IsNullOrWhiteSpace(filter.Q) ? null : filter.Q.Trim();
        if (q is { Length: > 100 }) q = q[..100];
        return filter with { SourceKind = source, Q = q };
    }

    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private static KnowledgeProductView ReadProduct(MySqlDataReader reader)
    {
        var tags = new List<string>();
        if (!reader.IsDBNull(21))
        {
            try
            {
                using var array = JsonDocument.Parse(reader.GetString(21));
                foreach (var item in array.RootElement.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String) tags.Add(item.GetString()!);
            }
            catch (JsonException) { /* 标签聚合损坏时按无标签处理。 */ }
        }
        tags.Sort(StringComparer.Ordinal);
        var latestJob = reader.IsDBNull(22) ? null : ReadLatestJob(reader, Guid.Parse(reader.GetString(0)), 22);
        return new KnowledgeProductView(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)),
            reader.GetString(3), ParseStatus(reader.GetString(4)), reader.GetString(5), NullableString(reader, 6), NullableString(reader, 7),
            NullableString(reader, 8), NullableString(reader, 9), DeserializeSpecs(NullableString(reader, 10)), NullableString(reader, 11), tags,
            DeserializeSources(NullableString(reader, 12)), NullableGuid(reader, 13), NullableGuid(reader, 14), NullableGuid(reader, 15),
            latestJob, NullableGuid(reader, 18)!.Value, NullableGuid(reader, 17), NullableUtc(reader, 16), Utc(reader, 19), Utc(reader, 20));
    }

    private static ProductParseJobView ReadLatestJob(MySqlDataReader reader, Guid productId, int offset)
        => new(Guid.Parse(reader.GetString(offset)), productId, ParseKind(reader.GetString(offset + 1)), reader.GetString(offset + 2),
            reader.GetString(offset + 3), reader.GetInt32(offset + 4), NullableString(reader, offset + 5), NullableString(reader, offset + 6),
            NullableString(reader, offset + 7), reader.GetInt32(offset + 8), reader.GetBoolean(offset + 9),
            reader.IsDBNull(offset + 10) ? null : reader.GetInt64(offset + 10), Utc(reader, offset + 11), NullableUtc(reader, offset + 12),
            NullableUtc(reader, offset + 13));

    private static ProductParseJobView ReadJob(MySqlDataReader reader, int offset)
        => new(Guid.Parse(reader.GetString(offset)), Guid.Parse(reader.GetString(offset + 1)), ParseKind(reader.GetString(offset + 2)),
            reader.GetString(offset + 3), reader.GetString(offset + 4), reader.GetInt32(offset + 5), NullableString(reader, offset + 6),
            NullableString(reader, offset + 7), NullableString(reader, offset + 8), reader.GetInt32(offset + 9), reader.GetBoolean(offset + 10),
            reader.IsDBNull(offset + 11) ? null : reader.GetInt64(offset + 11), Utc(reader, offset + 12), NullableUtc(reader, offset + 13),
            NullableUtc(reader, offset + 14));

    /// <summary>状态枚举与数据库取值的映射。</summary>
    internal static string ToDb(KnowledgeProductStatus status) => status switch
    {
        KnowledgeProductStatus.Draft => "draft",
        KnowledgeProductStatus.Parsing => "parsing",
        KnowledgeProductStatus.PendingReview => "pending_review",
        KnowledgeProductStatus.Approved => "approved",
        KnowledgeProductStatus.ParseFailed => "parse_failed",
        KnowledgeProductStatus.Archived => "archived",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    internal static KnowledgeProductStatus ParseStatus(string value) => value switch
    {
        "draft" => KnowledgeProductStatus.Draft,
        "parsing" => KnowledgeProductStatus.Parsing,
        "pending_review" => KnowledgeProductStatus.PendingReview,
        "approved" => KnowledgeProductStatus.Approved,
        "parse_failed" => KnowledgeProductStatus.ParseFailed,
        "archived" => KnowledgeProductStatus.Archived,
        _ => KnowledgeProductStatus.Draft
    };

    internal static string ToDb(ProductParseJobKind kind) => kind switch
    {
        ProductParseJobKind.File => "file",
        ProductParseJobKind.Url => "url",
        ProductParseJobKind.Completion => "completion",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    internal static ProductParseJobKind ParseKind(string value) => value switch
    {
        "url" => ProductParseJobKind.Url,
        "completion" => ProductParseJobKind.Completion,
        _ => ProductParseJobKind.File
    };

    private static string? SerializeSpecs(IReadOnlyList<ProductSpecItem>? specs)
        => specs is { Count: > 0 } ? JsonSerializer.Serialize(specs, JsonOptions) : null;

    private static IReadOnlyList<ProductSpecItem> DeserializeSpecs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<ProductSpecItem>>(json, JsonOptions) ?? []; }
        catch (JsonException) { return []; }
    }

    private static IReadOnlyDictionary<string, ProductFieldSource> DeserializeSources(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, ProductFieldSource>();
        try { return JsonSerializer.Deserialize<Dictionary<string, ProductFieldSource>>(json, JsonOptions) ?? new Dictionary<string, ProductFieldSource>(); }
        catch (JsonException) { return new Dictionary<string, ProductFieldSource>(); }
    }

    private static string? NullableString(MySqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static Guid? NullableGuid(MySqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : Guid.Parse(reader.GetString(ordinal));
    private static DateTimeOffset Utc(MySqlDataReader reader, int ordinal) => new(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));
    private static DateTimeOffset? NullableUtc(MySqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : Utc(reader, ordinal);

    private static async Task ExecuteAsync(MySqlConnection connection, MySqlTransaction? transaction, string sql,
        CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = new MySqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static CollectorStoreException Error(string code, string message, int status) => new(code, message, status);
}
