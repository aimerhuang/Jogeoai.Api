using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Jogeoai.Application;
using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

/// <summary>品牌知识条目的 MySQL 实现（依赖 043 迁移）：列表计数、50 条上限、重名与乐观锁、研究与写稿读取、检索投影同步租约。</summary>
public sealed class BrandKnowledgeItemStore(MySqlDatabase database) : IBrandKnowledgeItemStore
{
    /// <summary>同步最大尝试次数；用尽后停止自动重试，直到条目再次被编辑或删除。</summary>
    public const int MaxSyncAttempts = 5;
    /// <summary>同步重试的初始退避秒数（按尝试次数指数增长）。</summary>
    public const int SyncBackoffBaseSeconds = 60;
    /// <summary>同步重试的最长退避秒数（30 分钟）。</summary>
    public const int SyncBackoffMaxSeconds = 1800;
    /// <summary>品牌研究最多读取的条目数。</summary>
    public const int ResearchMaxItems = 12;
    /// <summary>品牌研究单条内容的字数上限。</summary>
    public const int ResearchMaxItemCharacters = 1200;
    /// <summary>品牌研究内容合计的字数上限。</summary>
    public const int ResearchMaxTotalCharacters = 8000;

    private const string DimensionOrder = "FIELD(i.dimension,'brand_intro','selling_point','audience','differentiator','product_service','faq')";

    private const string ItemColumns = """
        BIN_TO_UUID(i.id), BIN_TO_UUID(i.site_id), i.dimension, i.title, i.content, i.source_kind, i.source_url, i.source_excerpt,
        i.sync_status, i.content_version, BIN_TO_UUID(i.created_by), BIN_TO_UUID(i.updated_by), i.created_at, i.updated_at
        """;

    /// <inheritdoc />
    public async Task<BrandKnowledgeItemList> ListAsync(Guid tenantId, Guid siteId, BrandKnowledgeDimension? dimension = null,
        CancellationToken cancellationToken = default)
    {
        var dimensionKey = dimension is null ? null : BrandKnowledgeRules.ToKey(dimension.Value);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await AssertSiteAsync(connection, null, tenantId, siteId, lockRow: false, cancellationToken);
        var items = new List<BrandKnowledgeItemView>();
        await using (var command = new MySqlCommand($"""
            SELECT {ItemColumns}
            FROM brand_knowledge_items i
            WHERE i.tenant_id=UUID_TO_BIN(@tenant) AND i.site_id=UUID_TO_BIN(@site) AND i.deleted_at IS NULL
              AND (@dimension IS NULL OR i.dimension=@dimension)
            ORDER BY {DimensionOrder}, i.created_at, i.id
            LIMIT 500;
            """, connection))
        {
            command.Parameters.AddWithValue("@tenant", tenantId.ToString());
            command.Parameters.AddWithValue("@site", siteId.ToString());
            command.Parameters.AddWithValue("@dimension", dimensionKey ?? (object)DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) items.Add(ReadItem(reader));
        }
        var counts = new Dictionary<BrandKnowledgeDimension, int>();
        await using (var command = new MySqlCommand("""
            SELECT dimension, COUNT(*) FROM brand_knowledge_items
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND deleted_at IS NULL
            GROUP BY dimension;
            """, connection))
        {
            command.Parameters.AddWithValue("@tenant", tenantId.ToString());
            command.Parameters.AddWithValue("@site", siteId.ToString());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                if (BrandKnowledgeRules.TryParseDimension(reader.GetString(0), out var parsed))
                    counts[parsed] = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
        }
        var complete = BrandKnowledgeRules.CompleteCounts(counts);
        return new BrandKnowledgeItemList(items, complete, complete.Values.Sum(), BrandKnowledgeRules.MaxItemsPerSite);
    }

    /// <inheritdoc />
    public async Task<BrandKnowledgeItemView?> GetAsync(Guid tenantId, Guid siteId, Guid itemId, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        // 与列表、新增一致：品牌已归档或不属于该租户时 404 RESOURCE_NOT_FOUND（控制器的条目写权限检查经由这里）。
        await AssertSiteAsync(connection, null, tenantId, siteId, lockRow: false, cancellationToken);
        return await GetAsync(connection, null, tenantId, siteId, itemId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BrandKnowledgeItemView>> CreateManyAsync(Guid tenantId, Guid siteId, Guid userId,
        IReadOnlyList<BrandKnowledgeItemCreate> items, BrandKnowledgeItemSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(source);
        // 锁品牌行前先完成纯校验，校验失败不打开连接。
        var batch = PrepareBatch(items, source);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        IReadOnlyList<Guid> ids;
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            ids = await InsertPreparedAsync(connection, transaction, tenantId, siteId, userId, batch, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        return await ReadCreatedAsync(connection, tenantId, siteId, ids, cancellationToken);
    }

    /// <summary>
    /// 在调用方事务内批量新增条目（<see cref="CreateManyAsync"/> 与网址识别原子保存共用）：校验 → 锁品牌行 → 检查 50 条上限 →
    /// 检查重名 → 按输入顺序插入。调用方负责提交；抛出异常时调用方事务随 Dispose 回滚，任何一条都不会写入。
    /// 锁顺序：调用方若还需锁其他行（如识别任务行），必须在调用本方法之前锁定，本方法只锁品牌行。
    /// </summary>
    /// <param name="connection">已打开的连接。</param>
    /// <param name="transaction">调用方事务。</param>
    /// <param name="tenantId">租户。</param>
    /// <param name="siteId">品牌。</param>
    /// <param name="userId">创建人。</param>
    /// <param name="items">待新增条目。</param>
    /// <param name="source">来源，整批共用。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>新条目标识，顺序与输入一致。</returns>
    /// <exception cref="CollectorStoreException">KNOWLEDGE_ITEM_VALIDATION（400）、RESOURCE_NOT_FOUND（404）、KNOWLEDGE_ITEM_LIMIT（409）、KNOWLEDGE_ITEM_TITLE_CONFLICT（409）。</exception>
    internal static Task<IReadOnlyList<Guid>> InsertManyAsync(MySqlConnection connection, MySqlTransaction transaction, Guid tenantId,
        Guid siteId, Guid userId, IReadOnlyList<BrandKnowledgeItemCreate> items, BrandKnowledgeItemSource source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(source);
        return InsertPreparedAsync(connection, transaction, tenantId, siteId, userId, PrepareBatch(items, source), cancellationToken);
    }

    /// <summary>读取刚新增的条目视图（事务提交后调用），顺序与标识一致。</summary>
    /// <param name="connection">已打开的连接。</param>
    /// <param name="tenantId">租户。</param>
    /// <param name="siteId">品牌。</param>
    /// <param name="ids">条目标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    internal static async Task<IReadOnlyList<BrandKnowledgeItemView>> ReadCreatedAsync(MySqlConnection connection, Guid tenantId, Guid siteId,
        IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        var created = new List<BrandKnowledgeItemView>(ids.Count);
        foreach (var id in ids)
            created.Add((await GetAsync(connection, null, tenantId, siteId, id, cancellationToken))!);
        return created;
    }

    private sealed record PreparedRow(Guid Id, BrandKnowledgeItemWrite Write, string TitleKey, string? Excerpt);

    private sealed record PreparedBatch(IReadOnlyList<PreparedRow> Rows, string SourceKind, string? SourceUrl, string? SourceUrlHash,
        Guid? ExtractJobId);

    /// <summary>纯校验：条数、来源、逐条规范化与批内重名。</summary>
    private static PreparedBatch PrepareBatch(IReadOnlyList<BrandKnowledgeItemCreate> items, BrandKnowledgeItemSource source)
    {
        if (items.Count == 0) throw Error("KNOWLEDGE_ITEM_VALIDATION", "请至少提交一条知识。", 400);
        if (items.Count > BrandKnowledgeRules.MaxItemsPerSite) throw LimitError(0);
        var (sourceKind, sourceUrl, sourceUrlHash) = NormalizeSource(source);
        var rows = new List<PreparedRow>(items.Count);
        var batchKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item?.Write is null) throw Error("KNOWLEDGE_ITEM_VALIDATION", "知识内容不能为空。", 400);
            var write = BrandKnowledgeRules.NormalizeWrite(item.Write);
            var titleKey = BrandKnowledgeRules.TitleKey(write.Title);
            if (!batchKeys.Add(ActiveTitleKey(write.Dimension, titleKey))) throw TitleConflict(write.Dimension, write.Title);
            var excerpt = sourceKind != "url" || string.IsNullOrWhiteSpace(item.SourceExcerpt) ? null
                : TruncateRunes(item.SourceExcerpt.Trim(), BrandKnowledgeRules.MaxExcerptLength);
            rows.Add(new PreparedRow(Guid.NewGuid(), write, titleKey, excerpt));
        }
        return new PreparedBatch(rows, sourceKind, sourceUrl, sourceUrlHash, sourceKind == "url" ? source.ExtractJobId : null);
    }

    private static async Task<IReadOnlyList<Guid>> InsertPreparedAsync(MySqlConnection connection, MySqlTransaction transaction, Guid tenantId,
        Guid siteId, Guid userId, PreparedBatch batch, CancellationToken cancellationToken)
    {
        var rows = batch.Rows;
        try
        {
            // 锁品牌行串行化同品牌的新增，再读取计数：锁之前不做一致性读，计数总能看到已提交的并发新增。
            await AssertSiteAsync(connection, transaction, tenantId, siteId, lockRow: true, cancellationToken);
            int active;
            await using (var count = new MySqlCommand("""
                SELECT COUNT(*) FROM brand_knowledge_items
                WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND deleted_at IS NULL;
                """, connection, transaction))
            {
                count.Parameters.AddWithValue("@tenant", tenantId.ToString());
                count.Parameters.AddWithValue("@site", siteId.ToString());
                active = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            }
            if (active + rows.Count > BrandKnowledgeRules.MaxItemsPerSite) throw LimitError(active);
            await AssertNoTitleConflictAsync(connection, transaction, tenantId, siteId,
                rows.Select(row => (row.Write.Dimension, row.Write.Title, row.TitleKey)).ToArray(), null, cancellationToken);
            for (var index = 0; index < rows.Count; index++)
            {
                var row = rows[index];
                // 同批条目按输入顺序递增 1 微秒，保证「同维度按创建时间正序」与输入顺序一致。
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO brand_knowledge_items
                        (id, tenant_id, site_id, dimension, title, title_key, content, source_kind, source_url, source_url_hash,
                         source_excerpt, extract_job_id, content_version, synced_version, sync_status, sync_attempts, sync_next_at,
                         created_by, updated_by, created_at, updated_at)
                    VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), @dimension, @title, @title_key, @content,
                            @source_kind, @source_url, @source_url_hash, @excerpt, UUID_TO_BIN(@job), 1, 0, 'pending', 0, UTC_TIMESTAMP(6),
                            UUID_TO_BIN(@user), UUID_TO_BIN(@user),
                            DATE_ADD(UTC_TIMESTAMP(6), INTERVAL @offset MICROSECOND), DATE_ADD(UTC_TIMESTAMP(6), INTERVAL @offset MICROSECOND));
                    """, cancellationToken, ("@id", row.Id.ToString()), ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()),
                    ("@dimension", BrandKnowledgeRules.ToKey(row.Write.Dimension)), ("@title", row.Write.Title), ("@title_key", row.TitleKey),
                    ("@content", row.Write.Content), ("@source_kind", batch.SourceKind), ("@source_url", batch.SourceUrl),
                    ("@source_url_hash", batch.SourceUrlHash), ("@excerpt", row.Excerpt),
                    ("@job", batch.ExtractJobId?.ToString()), ("@user", userId.ToString()), ("@offset", index));
            }
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            throw Error("KNOWLEDGE_ITEM_TITLE_CONFLICT", "同一维度下已存在同名知识，请修改标题后再保存。", 409);
        }
        catch (MySqlException exception) when (exception.Number == 1452)
        {
            throw Error("KNOWLEDGE_ITEM_VALIDATION", "关联的识别任务或创建人不存在。", 400);
        }
        return rows.Select(row => row.Id).ToArray();
    }

    /// <inheritdoc />
    public async Task<BrandKnowledgeItemView> UpdateAsync(Guid tenantId, Guid siteId, Guid itemId, Guid userId,
        BrandKnowledgeItemUpdate update, CancellationToken cancellationToken = default)
    {
        var normalized = BrandKnowledgeRules.NormalizeUpdate(update);
        var dimensionKey = BrandKnowledgeRules.ToKey(normalized.Dimension);
        var titleKey = BrandKnowledgeRules.TitleKey(normalized.Title);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            try
            {
                // 品牌已归档时拒绝编辑，与列表、新增相同口径（404 RESOURCE_NOT_FOUND）。
                await AssertSiteAsync(connection, transaction, tenantId, siteId, lockRow: false, cancellationToken);
                string currentDimension, currentTitle, currentContent;
                int currentVersion;
                await using (var command = new MySqlCommand("""
                    SELECT dimension, title, content, content_version FROM brand_knowledge_items
                    WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND id=UUID_TO_BIN(@id) AND deleted_at IS NULL
                    FOR UPDATE;
                    """, connection, transaction))
                {
                    command.Parameters.AddWithValue("@tenant", tenantId.ToString());
                    command.Parameters.AddWithValue("@site", siteId.ToString());
                    command.Parameters.AddWithValue("@id", itemId.ToString());
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    if (!await reader.ReadAsync(cancellationToken)) throw NotFound();
                    currentDimension = reader.GetString(0);
                    currentTitle = reader.GetString(1);
                    currentContent = reader.GetString(2);
                    currentVersion = Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture);
                }
                if (currentVersion != normalized.ExpectedVersion)
                    throw Error("KNOWLEDGE_ITEM_STALE", "该知识已被其他人修改，请刷新后重试。", 409);
                if (string.Equals(currentDimension, dimensionKey, StringComparison.Ordinal)
                    && string.Equals(currentTitle, normalized.Title, StringComparison.Ordinal)
                    && string.Equals(currentContent, normalized.Content, StringComparison.Ordinal))
                {
                    // 内容未变化：不升版本、不触发同步。
                    await transaction.RollbackAsync(CancellationToken.None);
                }
                else
                {
                    await AssertNoTitleConflictAsync(connection, transaction, tenantId, siteId,
                        [(normalized.Dimension, normalized.Title, titleKey)], itemId, cancellationToken);
                    await ExecuteAsync(connection, transaction, """
                        UPDATE brand_knowledge_items SET dimension=@dimension, title=@title, title_key=@title_key, content=@content,
                            content_version=content_version+1, sync_status='pending', sync_attempts=0, sync_error=NULL,
                            sync_next_at=UTC_TIMESTAMP(6), updated_by=UUID_TO_BIN(@user), updated_at=UTC_TIMESTAMP(6)
                        WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@id) AND deleted_at IS NULL AND content_version=@expected;
                        """, cancellationToken, ("@dimension", dimensionKey), ("@title", normalized.Title), ("@title_key", titleKey),
                        ("@content", normalized.Content), ("@user", userId.ToString()), ("@tenant", tenantId.ToString()),
                        ("@id", itemId.ToString()), ("@expected", normalized.ExpectedVersion));
                    await transaction.CommitAsync(cancellationToken);
                }
            }
            catch (MySqlException exception) when (exception.Number == 1062)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw Error("KNOWLEDGE_ITEM_TITLE_CONFLICT", "同一维度下已存在同名知识，请修改标题后再保存。", 409);
            }
        }
        return await GetAsync(connection, null, tenantId, siteId, itemId, cancellationToken) ?? throw NotFound();
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid tenantId, Guid siteId, Guid itemId, Guid userId, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        // 品牌已归档时拒绝删除，与列表、新增相同口径（404 RESOURCE_NOT_FOUND）。
        await AssertSiteAsync(connection, null, tenantId, siteId, lockRow: false, cancellationToken);
        await using var command = new MySqlCommand("""
            UPDATE brand_knowledge_items SET deleted_at=UTC_TIMESTAMP(6), deleted_by=UUID_TO_BIN(@user), updated_by=UUID_TO_BIN(@user),
                updated_at=UTC_TIMESTAMP(6), sync_status='pending', sync_attempts=0, sync_error=NULL, sync_next_at=UTC_TIMESTAMP(6)
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND id=UUID_TO_BIN(@id) AND deleted_at IS NULL;
            """, connection);
        command.Parameters.AddWithValue("@user", userId.ToString());
        command.Parameters.AddWithValue("@tenant", tenantId.ToString());
        command.Parameters.AddWithValue("@site", siteId.ToString());
        command.Parameters.AddWithValue("@id", itemId.ToString());
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) throw NotFound();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BrandKnowledgeResearchItem>> ListForResearchAsync(Guid tenantId, Guid siteId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand($"""
            SELECT BIN_TO_UUID(i.id), i.dimension, i.title, i.content, i.updated_at
            FROM brand_knowledge_items i
            WHERE i.tenant_id=UUID_TO_BIN(@tenant) AND i.site_id=UUID_TO_BIN(@site) AND i.deleted_at IS NULL
            ORDER BY {DimensionOrder}, i.updated_at DESC, i.id
            LIMIT @limit;
            """, connection);
        command.Parameters.AddWithValue("@tenant", tenantId.ToString());
        command.Parameters.AddWithValue("@site", siteId.ToString());
        command.Parameters.AddWithValue("@limit", ResearchMaxItems);
        var rows = new List<BrandKnowledgeResearchItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!BrandKnowledgeRules.TryParseDimension(reader.GetString(1), out var dimension)) continue;
            rows.Add(new BrandKnowledgeResearchItem(Guid.Parse(reader.GetString(0)), dimension, reader.GetString(2), reader.GetString(3),
                false, Utc(reader, 4)));
        }
        return ApplyResearchBudget(rows);
    }

    /// <summary>
    /// 按研究预算裁剪已排好序的条目：最多 12 条、单条内容不超过 1200 字、内容合计不超过 8000 字；
    /// 最后一条在剩余额度不足时截断到剩余额度（剩余不足 100 字时不再加入）。字数按 Unicode 码点计。
    /// </summary>
    /// <param name="ordered">已按维度优先级与更新时间排序的条目。</param>
    public static IReadOnlyList<BrandKnowledgeResearchItem> ApplyResearchBudget(IReadOnlyList<BrandKnowledgeResearchItem> ordered)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        var result = new List<BrandKnowledgeResearchItem>();
        var remaining = ResearchMaxTotalCharacters;
        foreach (var item in ordered)
        {
            if (result.Count >= ResearchMaxItems) break;
            var limit = Math.Min(ResearchMaxItemCharacters, remaining);
            if (limit < 100) break;
            var length = BrandKnowledgeRules.CharacterCount(item.Content);
            var content = length > limit ? TruncateRunes(item.Content, limit) : item.Content;
            result.Add(item with { Content = content, Truncated = item.Truncated || length > limit });
            remaining -= Math.Min(length, limit);
        }
        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<KnowledgeProductOption>> ListProductOptionsAsync(Guid tenantId, Guid siteId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            SELECT BIN_TO_UUID(id), title FROM brand_knowledge_items
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND dimension='product_service'
              AND deleted_at IS NULL AND knowledge_document_id IS NOT NULL
            ORDER BY created_at, id LIMIT 500;
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
    public async Task<ArticleProductRef?> GetProductRefAsync(Guid tenantId, Guid siteId, Guid itemId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            SELECT title, BIN_TO_UUID(knowledge_document_id) FROM brand_knowledge_items
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND id=UUID_TO_BIN(@id)
              AND dimension='product_service' AND deleted_at IS NULL AND knowledge_document_id IS NOT NULL
            LIMIT 1;
            """, connection);
        command.Parameters.AddWithValue("@tenant", tenantId.ToString());
        command.Parameters.AddWithValue("@site", siteId.ToString());
        command.Parameters.AddWithValue("@id", itemId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new ArticleProductRef(itemId, tenantId, siteId, reader.GetString(0), Guid.Parse(reader.GetString(1)));
    }

    /// <inheritdoc />
    public async Task<BrandKnowledgeSyncLease?> ClaimSyncAsync(string workerId, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        BrandKnowledgeSyncLease? claimed = null;
        await using (var command = new MySqlCommand("""
            SELECT BIN_TO_UUID(i.id), BIN_TO_UUID(i.tenant_id), BIN_TO_UUID(i.site_id), i.dimension, i.title, i.content, i.source_url,
                   i.content_version, i.synced_version, BIN_TO_UUID(i.knowledge_document_id), i.deleted_at IS NOT NULL, i.sync_attempts
            FROM brand_knowledge_items i
            WHERE i.sync_status IN ('pending','failed') AND i.sync_next_at <= UTC_TIMESTAMP(6) AND i.sync_attempts < @max
              AND (i.sync_lease_until IS NULL OR i.sync_lease_until < UTC_TIMESTAMP(6))
            ORDER BY i.sync_next_at, i.id
            LIMIT 1 FOR UPDATE SKIP LOCKED;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("@max", MaxSyncAttempts);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken) && BrandKnowledgeRules.TryParseDimension(reader.GetString(3), out var dimension))
                claimed = new BrandKnowledgeSyncLease(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                    Guid.Parse(reader.GetString(2)), dimension, reader.GetString(4), reader.GetString(5), NullableString(reader, 6),
                    Convert.ToInt32(reader.GetValue(7), CultureInfo.InvariantCulture), Convert.ToInt32(reader.GetValue(8), CultureInfo.InvariantCulture),
                    reader.IsDBNull(9) ? null : Guid.Parse(reader.GetString(9)), Convert.ToInt64(reader.GetValue(10), CultureInfo.InvariantCulture) != 0,
                    Convert.ToInt32(reader.GetValue(11), CultureInfo.InvariantCulture) + 1, workerId);
        }
        if (claimed is null) { await transaction.RollbackAsync(CancellationToken.None); return null; }
        await ExecuteAsync(connection, transaction, """
            UPDATE brand_knowledge_items SET sync_lease_owner=@owner, sync_lease_until=DATE_ADD(UTC_TIMESTAMP(6), INTERVAL @seconds SECOND),
                sync_attempts=sync_attempts+1
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@id);
            """, cancellationToken, ("@owner", workerId), ("@seconds", Math.Max(5, (int)lease.TotalSeconds)),
            ("@tenant", claimed.TenantId.ToString()), ("@id", claimed.ItemId.ToString()));
        await transaction.CommitAsync(cancellationToken);
        return claimed;
    }

    /// <inheritdoc />
    public async Task<BrandKnowledgeSyncOutcome> CompleteSyncAsync(BrandKnowledgeSyncLease lease, Guid? documentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        int version;
        bool deleted;
        await using (var command = new MySqlCommand("""
            SELECT content_version, deleted_at IS NOT NULL FROM brand_knowledge_items
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@id) AND sync_lease_owner=@owner
            FOR UPDATE;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("@tenant", lease.TenantId.ToString());
            command.Parameters.AddWithValue("@id", lease.ItemId.ToString());
            command.Parameters.AddWithValue("@owner", lease.LeaseOwner);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await reader.DisposeAsync();
                // 租约已被接管：只在条目尚无文档时补记本次新建的文档，避免下一轮再建一份重复文档。
                if (documentId is Guid orphan)
                    await ExecuteAsync(connection, transaction, """
                        UPDATE brand_knowledge_items SET knowledge_document_id=UUID_TO_BIN(@document)
                        WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@id) AND knowledge_document_id IS NULL;
                        """, cancellationToken, ("@document", orphan.ToString()), ("@tenant", lease.TenantId.ToString()),
                        ("@id", lease.ItemId.ToString()));
                await transaction.CommitAsync(cancellationToken);
                return BrandKnowledgeSyncOutcome.LeaseLost;
            }
            version = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
            deleted = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture) != 0;
        }
        var current = version == lease.ContentVersion && deleted == lease.Deleted;
        await ExecuteAsync(connection, transaction, """
            UPDATE brand_knowledge_items SET knowledge_document_id=COALESCE(UUID_TO_BIN(@document), knowledge_document_id),
                synced_version=CASE WHEN @current=1 THEN @version ELSE synced_version END,
                sync_status=CASE WHEN @current=1 THEN 'synced' ELSE 'pending' END,
                sync_attempts=0, sync_error=NULL, sync_next_at=UTC_TIMESTAMP(6), sync_lease_owner=NULL, sync_lease_until=NULL
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@id) AND sync_lease_owner=@owner;
            """, cancellationToken, ("@document", documentId?.ToString()), ("@current", current ? 1 : 0), ("@version", lease.ContentVersion),
            ("@tenant", lease.TenantId.ToString()), ("@id", lease.ItemId.ToString()), ("@owner", lease.LeaseOwner));
        await transaction.CommitAsync(cancellationToken);
        return current ? BrandKnowledgeSyncOutcome.Synced : BrandKnowledgeSyncOutcome.StillPending;
    }

    /// <inheritdoc />
    public async Task FailSyncAsync(BrandKnowledgeSyncLease lease, string errorCode, string errorMessage, bool retryable,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var message = TruncateRunes($"{errorCode}: {errorMessage}", 500);
        var delay = SyncBackoffSeconds(lease.AttemptCount);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        // 领取后内容或删除状态又有变化：新内容值得立即重试，改回 pending 并保留编辑时清零的尝试次数。
        await ExecuteAsync(connection, null, """
            UPDATE brand_knowledge_items SET
                sync_status=CASE WHEN content_version=@version AND (deleted_at IS NOT NULL)=@deleted THEN 'failed' ELSE 'pending' END,
                sync_attempts=CASE WHEN content_version=@version AND (deleted_at IS NOT NULL)=@deleted AND @retryable=0 THEN @max
                                   ELSE sync_attempts END,
                sync_next_at=CASE WHEN content_version=@version AND (deleted_at IS NOT NULL)=@deleted
                                  THEN DATE_ADD(UTC_TIMESTAMP(6), INTERVAL @delay SECOND) ELSE UTC_TIMESTAMP(6) END,
                sync_error=@message, sync_lease_owner=NULL, sync_lease_until=NULL
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@id) AND sync_lease_owner=@owner;
            """, cancellationToken, ("@version", lease.ContentVersion), ("@deleted", lease.Deleted ? 1 : 0), ("@retryable", retryable ? 1 : 0),
            ("@max", MaxSyncAttempts), ("@delay", delay), ("@message", message), ("@tenant", lease.TenantId.ToString()),
            ("@id", lease.ItemId.ToString()), ("@owner", lease.LeaseOwner));
    }

    /// <summary>第 n 次尝试失败后的退避秒数：60 × 2^(n−1)，最长 1800 秒。</summary>
    /// <param name="attemptCount">包含本次在内的尝试次数（从 1 开始）。</param>
    public static int SyncBackoffSeconds(int attemptCount)
        => (int)Math.Min(SyncBackoffMaxSeconds, SyncBackoffBaseSeconds * Math.Pow(2, Math.Clamp(attemptCount - 1, 0, 10)));

    // ---- 辅助 ----

    private static async Task<BrandKnowledgeItemView?> GetAsync(MySqlConnection connection, MySqlTransaction? transaction, Guid tenantId,
        Guid siteId, Guid itemId, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand($"""
            SELECT {ItemColumns}
            FROM brand_knowledge_items i
            WHERE i.tenant_id=UUID_TO_BIN(@tenant) AND i.site_id=UUID_TO_BIN(@site) AND i.id=UUID_TO_BIN(@id) AND i.deleted_at IS NULL
            LIMIT 1;
            """, connection, transaction);
        command.Parameters.AddWithValue("@tenant", tenantId.ToString());
        command.Parameters.AddWithValue("@site", siteId.ToString());
        command.Parameters.AddWithValue("@id", itemId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadItem(reader) : null;
    }

    private static async Task AssertSiteAsync(MySqlConnection connection, MySqlTransaction? transaction, Guid tenantId, Guid siteId,
        bool lockRow, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(
            "SELECT 1 FROM sites WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@site) AND status='active'"
            + (lockRow ? " FOR UPDATE;" : ";"), connection, transaction);
        command.Parameters.AddWithValue("@tenant", tenantId.ToString());
        command.Parameters.AddWithValue("@site", siteId.ToString());
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
            throw Error("RESOURCE_NOT_FOUND", "品牌不存在。", 404);
    }

    /// <summary>在事务内检查待写入标题是否与同品牌同维度的未删除条目重名，命中时返回带维度与标题的冲突错误。</summary>
    private static async Task AssertNoTitleConflictAsync(MySqlConnection connection, MySqlTransaction transaction, Guid tenantId, Guid siteId,
        IReadOnlyList<(BrandKnowledgeDimension Dimension, string Title, string TitleKey)> candidates, Guid? excludeItemId,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0) return;
        var builder = new StringBuilder("""
            SELECT active_title_key FROM brand_knowledge_items
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND deleted_at IS NULL
              AND (@exclude IS NULL OR id <> UUID_TO_BIN(@exclude)) AND active_title_key IN (
            """);
        await using var command = new MySqlCommand { Connection = connection, Transaction = transaction };
        for (var index = 0; index < candidates.Count; index++)
        {
            builder.Append(index == 0 ? "@k" : ", @k").Append(index.ToString(CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("@k" + index.ToString(CultureInfo.InvariantCulture),
                ActiveTitleKey(candidates[index].Dimension, candidates[index].TitleKey));
        }
        builder.Append(") LIMIT 1;");
        command.CommandText = builder.ToString();
        command.Parameters.AddWithValue("@tenant", tenantId.ToString());
        command.Parameters.AddWithValue("@site", siteId.ToString());
        command.Parameters.AddWithValue("@exclude", excludeItemId?.ToString() ?? (object)DBNull.Value);
        if (await command.ExecuteScalarAsync(cancellationToken) is string hit)
        {
            var conflict = candidates.First(candidate => ActiveTitleKey(candidate.Dimension, candidate.TitleKey) == hit);
            throw TitleConflict(conflict.Dimension, conflict.Title);
        }
    }

    private static (string Kind, string? Url, string? UrlHash) NormalizeSource(BrandKnowledgeItemSource source)
    {
        var kind = (source.Kind ?? string.Empty).Trim().ToLowerInvariant();
        if (kind == "manual") return ("manual", null, null);
        if (kind != "url") throw Error("KNOWLEDGE_ITEM_VALIDATION", "知识来源无效。", 400);
        var url = source.Url?.Trim();
        if (string.IsNullOrEmpty(url) || url.Length > 2048 || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
            throw Error("KNOWLEDGE_ITEM_VALIDATION", "网址识别来源需要有效的 http(s) 网址。", 400);
        return ("url", url, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant());
    }

    private static string ActiveTitleKey(BrandKnowledgeDimension dimension, string titleKey)
        => BrandKnowledgeRules.ToKey(dimension) + ":" + titleKey;

    private static BrandKnowledgeItemView ReadItem(MySqlDataReader reader)
    {
        BrandKnowledgeRules.TryParseDimension(reader.GetString(2), out var dimension);
        return new BrandKnowledgeItemView(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), dimension,
            reader.GetString(3), reader.GetString(4), reader.GetString(5), NullableString(reader, 6), NullableString(reader, 7),
            reader.GetString(8), Convert.ToInt32(reader.GetValue(9), CultureInfo.InvariantCulture), Guid.Parse(reader.GetString(10)),
            Guid.Parse(reader.GetString(11)), Utc(reader, 12), Utc(reader, 13));
    }

    private static string TruncateRunes(string value, int maxRunes)
    {
        if (BrandKnowledgeRules.CharacterCount(value) <= maxRunes) return value;
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

    private static async Task ExecuteAsync(MySqlConnection connection, MySqlTransaction? transaction, string sql,
        CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = new MySqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static CollectorStoreException NotFound() => Error("KNOWLEDGE_ITEM_NOT_FOUND", "知识不存在或已删除。", 404);

    private static CollectorStoreException LimitError(int active)
        => Error("KNOWLEDGE_ITEM_LIMIT",
            $"每个品牌最多 {BrandKnowledgeRules.MaxItemsPerSite} 条知识，当前已有 {active} 条，剩余 {Math.Max(0, BrandKnowledgeRules.MaxItemsPerSite - active)} 条。", 409);

    private static CollectorStoreException TitleConflict(BrandKnowledgeDimension dimension, string title)
        => Error("KNOWLEDGE_ITEM_TITLE_CONFLICT", $"「{BrandKnowledgeRules.Describe(dimension).Label}」维度下已存在标题为「{title}」的知识。", 409);

    private static CollectorStoreException Error(string code, string message, int status) => new(code, message, status);
}
