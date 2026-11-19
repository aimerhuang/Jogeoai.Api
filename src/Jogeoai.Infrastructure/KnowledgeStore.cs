using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jogeoai.Application;
using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

/// <summary>知识库元数据、摄取任务和证据上下文的 MySQL 实现。</summary>
public sealed class KnowledgeStore(MySqlDatabase database, IObjectStorage objectStorage,
    IEmbeddingProvider embeddingProvider, IVectorIndex vectorIndex)
    : IKnowledgeStore, IKnowledgeRetrievalService
{
    private const long MaxFileBytes = 100L * 1024 * 1024;
    private const long QuotaBytes = 1L * 1024 * 1024 * 1024;
    private const int MaxTitleLength = 500;
    private const int MaxFolderNameLength = 200;
    private static readonly HashSet<string> SupportedExtensions =
        [".pdf", ".docx", ".md", ".markdown", ".txt", ".jpg", ".jpeg", ".png"];

    /// <inheritdoc />
    public async Task<KnowledgeFolderView> CreateFolderAsync(Guid tenantId, Guid? siteId, KnowledgeFolderWrite request,
        CancellationToken cancellationToken = default)
    {
        var name = NormalizeFolderName(request.Name);
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        if (siteId is not null) await AssertSiteAsync(connection, tenantId, siteId.Value, cancellationToken);
        if (request.ParentId is not null) await AssertFolderAsync(connection, tenantId, siteId, request.ParentId.Value, cancellationToken);
        var id = Guid.NewGuid();
        try
        {
            await ExecuteAsync(connection, null, "INSERT INTO knowledge_folders (id,tenant_id,site_id,parent_id,name,status,created_at,updated_at) VALUES (UUID_TO_BIN(@id),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@parent),@name,'active',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));", cancellationToken,
                ("@id", id.ToString()), ("@tenant", tenantId.ToString()), ("@site", siteId?.ToString()), ("@parent", request.ParentId?.ToString()), ("@name", name));
        }
        catch (MySqlException exception) when (exception.Number == 1062) { throw Error("KNOWLEDGE_FOLDER_EXISTS", "同一目录下已存在同名文件夹。", 409); }
        return (await GetFolderAsync(connection, tenantId, id, cancellationToken))!;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<KnowledgeFolderView>> ListFoldersAsync(Guid tenantId, Guid? siteId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        if (siteId is not null) await AssertSiteAsync(connection, tenantId, siteId.Value, cancellationToken);
        await using var command = new MySqlCommand("SELECT BIN_TO_UUID(id),BIN_TO_UUID(tenant_id),CASE WHEN site_id IS NULL THEN NULL ELSE BIN_TO_UUID(site_id) END,CASE WHEN parent_id IS NULL THEN NULL ELSE BIN_TO_UUID(parent_id) END,name,status,created_at,updated_at FROM knowledge_folders WHERE tenant_id=UUID_TO_BIN(@tenant) AND ((@site IS NULL AND site_id IS NULL) OR (@site IS NOT NULL AND site_id=UUID_TO_BIN(@site))) ORDER BY created_at,id;", connection);
        command.Parameters.AddWithValue("@tenant", tenantId.ToString()); command.Parameters.AddWithValue("@site", siteId?.ToString() ?? (object)DBNull.Value);
        var result = new List<KnowledgeFolderView>(); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadFolder(reader));
        return result;
    }

    /// <inheritdoc />
    public async Task<KnowledgeFolderView?> UpdateFolderAsync(Guid tenantId, Guid folderId, KnowledgeFolderUpdate request,
        CancellationToken cancellationToken = default)
    {
        ValidateFolderMove(request);
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // Serialize folder moves within a tenant so two concurrent moves cannot form a cycle.
        await LockTenantQuotaAsync(connection, transaction, tenantId, cancellationToken);
        var existing = await GetFolderAsync(connection, tenantId, folderId, cancellationToken, transaction);
        if (existing is null) { await transaction.RollbackAsync(CancellationToken.None); return null; }
        if (request.ParentId is not null)
        {
            await AssertFolderAsync(connection, tenantId, existing.SiteId, request.ParentId.Value, cancellationToken, transaction);
            await AssertNoFolderCycleAsync(connection, transaction, tenantId, folderId, request.ParentId.Value, cancellationToken);
        }
        var name = request.Name is null ? existing.Name : NormalizeFolderName(request.Name);
        try
        {
            await ExecuteAsync(connection, transaction, "UPDATE knowledge_folders SET name=@name,parent_id=CASE WHEN @root=1 THEN NULL WHEN @parent IS NOT NULL THEN UUID_TO_BIN(@parent) ELSE parent_id END,status=CASE WHEN @archive=1 THEN 'archived' ELSE status END,updated_at=UTC_TIMESTAMP(6) WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@id);", cancellationToken,
                ("@name", name), ("@parent", request.ParentId?.ToString()), ("@root", request.MoveToRoot ? 1 : 0),
                ("@archive", request.Archive ? 1 : 0), ("@tenant", tenantId.ToString()), ("@id", folderId.ToString()));
            await transaction.CommitAsync(cancellationToken);
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            throw Error("KNOWLEDGE_FOLDER_EXISTS", "同一目录下已存在同名文件夹。", 409);
        }
        return await GetFolderAsync(connection, tenantId, folderId, cancellationToken);
    }

    /// <summary>
    /// 通用资料写入口（修改元数据、停用、重试、上传新版本）对条目投影资料一律返回 409 KNOWLEDGE_DOCUMENT_MANAGED_BY_ITEM：
    /// 条目是品牌事实的唯一编辑入口，投影由 <c>KnowledgeItemSyncWorker</c> 单写，旧接口改写会与 Worker 竞争同一资料版本。
    /// </summary>
    internal static void AssertNotItemManaged(string? sourceKind) => BrandKnowledgeRules.AssertDocumentNotItemManaged(sourceKind);

    internal static void ValidateFolderMove(KnowledgeFolderUpdate request)
    {
        if (request.MoveToRoot && request.ParentId is not null)
            throw Error("INVALID_FOLDER_MOVE", "移动到根目录时不能同时指定父文件夹。", 400);
    }

    /// <inheritdoc />
    public Task<KnowledgeDocumentView> CreateDocumentAsync(Guid tenantId, Guid? siteId,
        KnowledgeUploadCommand command, CancellationToken cancellationToken = default)
        => CreateDocumentCoreAsync(tenantId, siteId, command, "upload", null, null, cancellationToken);

    /// <inheritdoc />
    public async Task<KnowledgeDocumentView> AddLinkDocumentAsync(Guid tenantId, Guid siteId, KnowledgeLinkWrite request,
        ArticleSourceReadResult page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(page);
        if (!string.Equals(page.ReadStatus, "ready", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(page.BodyMarkdown))
            throw Error("KNOWLEDGE_LINK_UNREADABLE", page.ErrorMessage ?? "网页没有可读取的正文。", 422);
        var normalizedUrl = string.IsNullOrWhiteSpace(page.UrlNormalized) ? page.UrlOriginal : page.UrlNormalized;
        var host = Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var uri) ? uri.Host : "link";
        var title = string.IsNullOrWhiteSpace(request.Title) ? (string.IsNullOrWhiteSpace(page.Title) ? host : page.Title.Trim()) : request.Title.Trim();
        if (title.Length > MaxTitleLength) title = title[..MaxTitleLength];
        var markdown = $"# {title}\n\n来源：{normalizedUrl}\n\n{page.BodyMarkdown.Trim()}\n";
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes(markdown), writable: false);
        var command = new KnowledgeUploadCommand(content, $"{SafeHostFileName(host)}.md", "text/markdown", title, request.FolderId,
            "link", null, null, "und", null, null, $"link:{page.UrlHash}:{Guid.NewGuid():N}", null);
        return await CreateDocumentCoreAsync(tenantId, siteId, command, "link", normalizedUrl, page.UrlHash, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<KnowledgeDocumentView> UploadProductDocumentAsync(Guid tenantId, Guid siteId, Guid productId, string title,
        string markdown, IReadOnlyList<string> tags, Guid? existingDocumentId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(markdown)) throw Error("KNOWLEDGE_EMPTY_CONTENT", "产品资料内容为空。", 400);
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes(markdown), writable: false);
        var command = new KnowledgeUploadCommand(content, $"product-{productId:N}.md", "text/markdown", title, null,
            "product", productId.ToString(), tags, "und", null, null, null, existingDocumentId);
        return await CreateDocumentCoreAsync(tenantId, siteId, command, "product", null, null, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<KnowledgeDocumentView> UploadKnowledgeItemDocumentAsync(Guid tenantId, Guid siteId, Guid itemId,
        BrandKnowledgeDimension dimension, string title, string markdown, Guid? existingDocumentId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(markdown)) throw Error("KNOWLEDGE_EMPTY_CONTENT", "品牌知识条目内容为空。", 400);
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes(markdown), writable: false);
        var command = new KnowledgeUploadCommand(content, $"item-{itemId:N}.md", "text/markdown", title, null,
            BrandKnowledgeRules.ToKey(dimension), itemId.ToString(), null, "und", null, null, null, existingDocumentId);
        return await CreateDocumentCoreAsync(tenantId, siteId, command, "item", null, null, cancellationToken);
    }

    private static string SafeHostFileName(string host)
    {
        var cleaned = new string(host.Where(character => char.IsLetterOrDigit(character) || character is '.' or '-').ToArray());
        return cleaned.Length == 0 ? "link" : cleaned;
    }

    private async Task<KnowledgeDocumentView> CreateDocumentCoreAsync(Guid tenantId, Guid? siteId,
        KnowledgeUploadCommand command, string sourceKind, string? sourceUrl, string? sourceUrlHash,
        CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty) throw Error("TENANT_REQUIRED", "租户标识无效。", 400);
        var extension = NormalizeExtension(Path.GetExtension(command.OriginalFileName));
        if (!SupportedExtensions.Contains(extension))
            throw Error("KNOWLEDGE_UNSUPPORTED_FILE_TYPE", "只支持 PDF、DOCX、Markdown、TXT、JPG 和 PNG。", 400);
        var fileName = NormalizeFileName(command.OriginalFileName);
        var title = NormalizeTitle(command.Title, fileName);
        var tags = NormalizeTags(command.Tags);
        var language = NormalizeShort(command.Language, "und", 16);
        var documentType = NormalizeShort(command.DocumentType, "general", 64);
        var productKey = NormalizeNullable(command.ProductKey, 191);
        ValidateDates(command.EffectiveAt, command.ExpiresAt);

        // Buffer once so quota, SHA-256 and object storage all observe the same bytes.
        await using var buffered = new MemoryStream();
        await CopyWithLimitAsync(command.Content, buffered, MaxFileBytes, cancellationToken);
        if (buffered.Length == 0) throw Error("KNOWLEDGE_EMPTY_CONTENT", "文件内容为空。", 400);
        var bytes = buffered.ToArray();
        ValidateMagic(bytes, extension);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        if (siteId is not null) await AssertSiteAsync(connection, tenantId, siteId.Value, cancellationToken);
        if (command.FolderId is not null) await AssertFolderAsync(connection, tenantId, siteId, command.FolderId.Value, cancellationToken);
        if (command.DocumentId is Guid existingDocumentId)
            return await CreateDocumentVersionAsync(connection, tenantId, siteId, existingDocumentId, command, sourceKind,
                extension, fileName, title, tags, language, documentType, productKey, bytes, hash, cancellationToken);
        if (!string.IsNullOrWhiteSpace(command.IdempotencyKey))
        {
            var existing = await FindByIdempotencyAsync(connection, tenantId, command.IdempotencyKey!, cancellationToken);
            if (existing is not null)
            {
                if (existing.SiteId != siteId)
                    throw Error("KNOWLEDGE_IDEMPOTENCY_CONFLICT", "幂等键已用于其他资料范围。", 409);
                return existing;
            }
        }
        var documentId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var objectKey = string.Empty;
        try
        {
            buffered.Position = 0;
            objectKey = await objectStorage.PutAsync(tenantId, versionId, buffered, cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await LockTenantQuotaAsync(connection, transaction, tenantId, cancellationToken);
            var usage = await ReadUsageAsync(connection, tenantId, cancellationToken, transaction);
            if (usage.UsedBytes > QuotaBytes - bytes.LongLength)
                throw Error("KNOWLEDGE_QUOTA_EXCEEDED", $"知识库容量已用 {usage.UsedBytes} 字节，上限为 {QuotaBytes} 字节。", 413);
            await ExecuteAsync(connection, transaction, """
                INSERT INTO knowledge_documents
                    (id, tenant_id, site_id, folder_id, title, status, language, document_type, product_key,
                     effective_at, expires_at, source_kind, source_url, source_url_hash, created_at, updated_at)
                VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), UUID_TO_BIN(@folder), @title, 'pending', @language,
                        @type, @product, @effective, @expires, @source_kind, @source_url, @source_url_hash, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                """, cancellationToken,
                ("@id", documentId.ToString()), ("@tenant", tenantId.ToString()), ("@site", siteId?.ToString()),
                ("@folder", command.FolderId?.ToString()), ("@title", title), ("@language", language),
                ("@type", documentType), ("@product", productKey), ("@effective", ToUtc(command.EffectiveAt)),
                ("@expires", ToUtc(command.ExpiresAt)), ("@source_kind", sourceKind), ("@source_url", sourceUrl),
                ("@source_url_hash", sourceUrlHash));
            await ExecuteAsync(connection, transaction, """
                INSERT INTO knowledge_document_versions
                    (id, tenant_id, document_id, version_number, original_file_name, extension,
                     detected_content_type, byte_length, sha256, object_key, status, created_at)
                VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), UUID_TO_BIN(@document), 1, @file, @extension,
                        @content_type, @size, @sha, @object_key, 'pending', UTC_TIMESTAMP(6));
                """, cancellationToken,
                ("@id", versionId.ToString()), ("@tenant", tenantId.ToString()), ("@document", documentId.ToString()),
                ("@file", fileName), ("@extension", extension), ("@content_type", NormalizeContentType(command.ContentType, extension)),
                ("@size", bytes.LongLength), ("@sha", hash), ("@object_key", objectKey));
            foreach (var tag in tags)
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO knowledge_document_tags (tenant_id, document_id, tag, created_at)
                    VALUES (UUID_TO_BIN(@tenant), UUID_TO_BIN(@document), @tag, UTC_TIMESTAMP(6));
                    """, cancellationToken, ("@tenant", tenantId.ToString()), ("@document", documentId.ToString()), ("@tag", tag));
            var idempotency = string.IsNullOrWhiteSpace(command.IdempotencyKey)
                ? $"upload:{versionId:N}" : command.IdempotencyKey!.Trim();
            await ExecuteAsync(connection, transaction, """
                INSERT INTO knowledge_ingest_jobs
                    (id, tenant_id, document_id, version_id, processor_version, step, status, next_attempt_at,
                     idempotency_key, created_at, updated_at)
                VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), UUID_TO_BIN(@document), UUID_TO_BIN(@version),
                        'local-v1', 'upload', 'pending', UTC_TIMESTAMP(6), @key, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                """, cancellationToken, ("@id", jobId.ToString()), ("@tenant", tenantId.ToString()),
                ("@document", documentId.ToString()), ("@version", versionId.ToString()), ("@key", idempotency));
            await transaction.CommitAsync(cancellationToken);
        }
        catch (MySqlException exception) when (exception.Number == 1062 && !string.IsNullOrWhiteSpace(command.IdempotencyKey))
        {
            await objectStorage.DeleteAsync(objectKey, CancellationToken.None);
            var existing = await FindByIdempotencyAsync(connection, tenantId, command.IdempotencyKey!, cancellationToken);
            if (existing is not null)
            {
                if (existing.SiteId != siteId)
                    throw Error("KNOWLEDGE_IDEMPOTENCY_CONFLICT", "幂等键已用于其他资料范围。", 409);
                return existing;
            }
            throw Error("KNOWLEDGE_IDEMPOTENCY_CONFLICT", "重复上传的幂等键与已有资料冲突。", 409);
        }
        catch
        {
            if (!string.IsNullOrEmpty(objectKey)) await objectStorage.DeleteAsync(objectKey, CancellationToken.None);
            throw;
        }
        return (await GetDocumentAsync(tenantId, documentId, cancellationToken))!;
    }

    private async Task<KnowledgeDocumentView> CreateDocumentVersionAsync(MySqlConnection connection, Guid tenantId,
        Guid? siteId, Guid documentId, KnowledgeUploadCommand command, string sourceKind, string extension, string fileName, string title,
        IReadOnlyList<string> tags, string language, string documentType, string? productKey, byte[] bytes, string hash,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockTenantQuotaAsync(connection, transaction, tenantId, cancellationToken);
        var row = await ReadDocumentScopeAsync(connection, transaction, tenantId, documentId, cancellationToken);
        if (row is null || row.Value.Status == "disabled"
            || (row.Value.SiteId is null) != (siteId is null)
            || row.Value.SiteId != siteId)
            throw Error("RESOURCE_NOT_FOUND", "资料不存在或不属于当前资料范围。", 404);
        // 条目投影只能由条目同步路径（UploadKnowledgeItemDocumentAsync，sourceKind=item）上传新版本；
        // 公开上传与产品同步都不能借 DocumentId 覆盖它。行已在锁内读取，判断与写入不会交错。
        if (sourceKind != BrandKnowledgeRules.ItemDocumentSourceKind) AssertNotItemManaged(row.Value.SourceKind);
        if (command.FolderId is not null)
            await AssertFolderAsync(connection, tenantId, siteId, command.FolderId.Value, cancellationToken, transaction);
        if (!string.IsNullOrWhiteSpace(command.IdempotencyKey))
        {
            var existing = await FindByIdempotencyAsync(connection, tenantId, command.IdempotencyKey!, cancellationToken, transaction);
            if (existing is not null)
            {
                if (existing.DocumentId != documentId || existing.SiteId != siteId)
                    throw Error("KNOWLEDGE_IDEMPOTENCY_CONFLICT", "幂等键已用于其他资料。", 409);
                await transaction.RollbackAsync(CancellationToken.None);
                return (await GetDocumentAsync(tenantId, documentId, cancellationToken))!;
            }
        }
        var usage = await ReadUsageAsync(connection, tenantId, cancellationToken, transaction);
        if (usage.UsedBytes > QuotaBytes - bytes.LongLength)
            throw Error("KNOWLEDGE_QUOTA_EXCEEDED", $"知识库容量已用 {usage.UsedBytes} 字节，上限为 {QuotaBytes} 字节。", 413);
        var nextVersion = await ReadNextVersionAsync(connection, transaction, tenantId, documentId, cancellationToken);
        var versionId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var objectKey = string.Empty;
        try
        {
            await using var content = new MemoryStream(bytes, writable: false);
            objectKey = await objectStorage.PutAsync(tenantId, versionId, content, cancellationToken);
            await ExecuteAsync(connection, transaction, """
                INSERT INTO knowledge_document_versions
                    (id, tenant_id, document_id, version_number, original_file_name, extension,
                     detected_content_type, byte_length, sha256, object_key, status, created_at)
                VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), UUID_TO_BIN(@document), @number, @file, @extension,
                        @content_type, @size, @sha, @object_key, 'pending', UTC_TIMESTAMP(6));
                """, cancellationToken, ("@id", versionId.ToString()), ("@tenant", tenantId.ToString()),
                ("@document", documentId.ToString()), ("@number", nextVersion), ("@file", fileName),
                ("@extension", extension), ("@content_type", NormalizeContentType(command.ContentType, extension)),
                ("@size", bytes.LongLength), ("@sha", hash), ("@object_key", objectKey));
            await ExecuteAsync(connection, transaction, "DELETE FROM knowledge_document_tags WHERE tenant_id=UUID_TO_BIN(@tenant) AND document_id=UUID_TO_BIN(@document);",
                cancellationToken, ("@tenant", tenantId.ToString()), ("@document", documentId.ToString()));
            foreach (var tag in tags)
                await ExecuteAsync(connection, transaction, "INSERT INTO knowledge_document_tags (tenant_id,document_id,tag,created_at) VALUES (UUID_TO_BIN(@tenant),UUID_TO_BIN(@document),@tag,UTC_TIMESTAMP(6));",
                    cancellationToken, ("@tenant", tenantId.ToString()), ("@document", documentId.ToString()), ("@tag", tag));
            var idempotency = string.IsNullOrWhiteSpace(command.IdempotencyKey) ? $"upload:{versionId:N}" : command.IdempotencyKey!.Trim();
            await ExecuteAsync(connection, transaction, """
                INSERT INTO knowledge_ingest_jobs
                    (id, tenant_id, document_id, version_id, processor_version, step, status, next_attempt_at,
                     idempotency_key, created_at, updated_at)
                VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), UUID_TO_BIN(@document), UUID_TO_BIN(@version),
                        'local-v1', 'upload', 'pending', UTC_TIMESTAMP(6), @key, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                """, cancellationToken, ("@id", jobId.ToString()), ("@tenant", tenantId.ToString()),
                ("@document", documentId.ToString()), ("@version", versionId.ToString()), ("@key", idempotency));
            await ExecuteAsync(connection, transaction, """
                UPDATE knowledge_documents SET title=@title, folder_id=UUID_TO_BIN(@folder), language=@language,
                    document_type=@type, product_key=@product, effective_at=@effective, expires_at=@expires,
                    config_version=config_version+1, updated_at=UTC_TIMESTAMP(6)
                WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@document) AND status <> 'disabled';
                """, cancellationToken, ("@title", title), ("@folder", command.FolderId?.ToString()),
                ("@language", language), ("@type", documentType), ("@product", productKey),
                ("@effective", ToUtc(command.EffectiveAt)), ("@expires", ToUtc(command.ExpiresAt)),
                ("@tenant", tenantId.ToString()), ("@document", documentId.ToString()));
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            if (!string.IsNullOrEmpty(objectKey))
                await objectStorage.DeleteAsync(objectKey, CancellationToken.None);
            throw;
        }
        return (await GetDocumentAsync(tenantId, documentId, cancellationToken))!;
    }

    /// <inheritdoc />
    public async Task<CursorPage<KnowledgeDocumentView>> ListDocumentsAsync(Guid tenantId, Guid? siteId,
        string? cursor = null, int limit = 50, KnowledgeDocumentListFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        filter = NormalizeDocumentListFilter(siteId, filter);
        var status = filter.Status;
        var tag = filter.Tag;
        var documentType = filter.DocumentType;
        var cursorContextHash = ComputeListCursorContextHash(tenantId, siteId, filter);
        limit = Math.Clamp(limit, 1, 100);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        if (siteId is not null) await AssertSiteAsync(connection, tenantId, siteId.Value, cancellationToken);
        var sql = """
            SELECT BIN_TO_UUID(d.id), BIN_TO_UUID(d.tenant_id),
                   CASE WHEN d.site_id IS NULL THEN NULL ELSE BIN_TO_UUID(d.site_id) END,
                   CASE WHEN d.site_id IS NULL THEN 'tenant' ELSE 'site' END,
                   CASE WHEN d.folder_id IS NULL THEN NULL ELSE BIN_TO_UUID(d.folder_id) END,
                   d.title, d.status, d.language, d.document_type, d.product_key, d.effective_at, d.expires_at,
                   CASE WHEN d.current_version_id IS NULL THEN NULL ELSE BIN_TO_UUID(d.current_version_id) END,
                   d.created_at, d.updated_at, d.config_version, d.source_kind, d.source_url
            FROM knowledge_documents d
            WHERE d.tenant_id = UUID_TO_BIN(@tenant)
              AND ((@site IS NULL AND d.site_id IS NULL) OR (@site IS NOT NULL AND (d.site_id IS NULL OR d.site_id = UUID_TO_BIN(@site))))
              AND (@brand_only=0 OR d.site_id=UUID_TO_BIN(@site))
              AND (@status IS NULL OR d.status=@status)
              AND (@type IS NULL OR d.document_type=@type)
              AND (@tag IS NULL OR EXISTS (SELECT 1 FROM knowledge_document_tags t
                   WHERE t.tenant_id=d.tenant_id AND t.document_id=d.id AND t.tag=@tag))
              AND (@cursor IS NULL OR d.updated_at < @cursor
                   OR (d.updated_at = @cursor AND (@cursor_id IS NULL OR d.id < UUID_TO_BIN(@cursor_id))))
            ORDER BY d.updated_at DESC, d.id DESC
            LIMIT @limit;
            """;
        var rows = new List<(Guid Id, Guid Tenant, Guid? Site, string Scope, Guid? Folder, string Title, string Status,
            string Language, string Type, string? Product, DateTime? Effective, DateTime? Expires, Guid? Current, DateTime Created, DateTime Updated, int ConfigVersion, string SourceKind, string? SourceUrl)>();
        await using (var command = new MySqlCommand(sql, connection))
        {
            command.Parameters.AddWithValue("@tenant", tenantId.ToString());
            command.Parameters.AddWithValue("@site", siteId?.ToString() ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@brand_only", filter.ScopeMode == KnowledgeScopeMode.BrandOnly ? 1 : 0);
            command.Parameters.AddWithValue("@status", status ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@type", documentType ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@tag", tag ?? (object)DBNull.Value);
            var parsedCursor = ParseCursor(cursor, cursorContextHash);
            command.Parameters.AddWithValue("@cursor", parsedCursor.Timestamp ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@cursor_id", parsedCursor.Id?.ToString() ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@limit", limit + 1);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) rows.Add(ReadDocumentRow(reader));
        }
        var hasNext = rows.Count > limit;
        if (hasNext) rows.RemoveAt(limit);
        var nextCursor = hasNext && rows.Count > 0
            ? EncodeCursor(rows[^1].Updated, rows[^1].Id, cursorContextHash)
            : null;
        return new CursorPage<KnowledgeDocumentView>(await HydrateDocumentsAsync(connection, rows, cancellationToken), nextCursor);
    }

    internal static KnowledgeDocumentListFilter NormalizeDocumentListFilter(Guid? siteId,
        KnowledgeDocumentListFilter? filter)
    {
        filter ??= new KnowledgeDocumentListFilter();
        if (siteId is null && filter.ScopeMode != KnowledgeScopeMode.Default)
            throw Error("INVALID_KNOWLEDGE_SCOPE", "公司级列表不支持 brand_only。", 400);
        var status = string.IsNullOrWhiteSpace(filter.Status) ? null : filter.Status.Trim().ToLowerInvariant();
        if (status is not null && status is not ("pending" or "processing" or "ready" or "failed" or "disabled"))
            throw Error("INVALID_KNOWLEDGE_STATUS", "资料状态无效。", 400);
        var tag = NormalizeTags(string.IsNullOrWhiteSpace(filter.Tag) ? null : [filter.Tag]).FirstOrDefault();
        var documentType = string.IsNullOrWhiteSpace(filter.DocumentType)
            ? null : NormalizeShort(filter.DocumentType, "general", 64);
        return filter with { Status = status, Tag = tag, DocumentType = documentType };
    }

    /// <inheritdoc />
    public async Task<KnowledgeDocumentView?> GetDocumentAsync(Guid tenantId, Guid documentId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            SELECT BIN_TO_UUID(d.id), BIN_TO_UUID(d.tenant_id),
                   CASE WHEN d.site_id IS NULL THEN NULL ELSE BIN_TO_UUID(d.site_id) END,
                   CASE WHEN d.site_id IS NULL THEN 'tenant' ELSE 'site' END,
                   CASE WHEN d.folder_id IS NULL THEN NULL ELSE BIN_TO_UUID(d.folder_id) END,
                   d.title, d.status, d.language, d.document_type, d.product_key, d.effective_at, d.expires_at,
                   CASE WHEN d.current_version_id IS NULL THEN NULL ELSE BIN_TO_UUID(d.current_version_id) END,
                   d.created_at, d.updated_at, d.config_version, d.source_kind, d.source_url
            FROM knowledge_documents d
            WHERE d.tenant_id = UUID_TO_BIN(@tenant) AND d.id = UUID_TO_BIN(@document)
            LIMIT 1;
            """, connection);
        command.Parameters.AddWithValue("@tenant", tenantId.ToString()); command.Parameters.AddWithValue("@document", documentId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var row = ReadDocumentRow(reader);
        await reader.DisposeAsync();
        return (await HydrateDocumentsAsync(connection, [row], cancellationToken)).Single();
    }

    /// <inheritdoc />
    public async Task<Stream?> OpenDocumentAsync(Guid tenantId, Guid documentId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            SELECT v.object_key FROM knowledge_documents d
            JOIN knowledge_document_versions v ON v.tenant_id=d.tenant_id AND v.id=d.current_version_id
            WHERE d.tenant_id=UUID_TO_BIN(@tenant) AND d.id=UUID_TO_BIN(@document)
              AND d.status <> 'disabled' LIMIT 1;
            """, connection);
        command.Parameters.AddWithValue("@tenant", tenantId.ToString()); command.Parameters.AddWithValue("@document", documentId.ToString());
        var key = await command.ExecuteScalarAsync(cancellationToken) as string;
        return key is null ? null : await objectStorage.OpenReadAsync(key, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<KnowledgeDocumentView?> UpdateDocumentAsync(Guid tenantId, Guid documentId,
        KnowledgeDocumentUpdate update, CancellationToken cancellationToken = default)
    {
        ValidateDates(update.EffectiveAt, update.ExpiresAt);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var scope = await ReadDocumentScopeAsync(connection, transaction, tenantId, documentId, cancellationToken);
        if (scope is null) { await transaction.RollbackAsync(CancellationToken.None); return null; }
        AssertNotItemManaged(scope.Value.SourceKind);
        var current = await GetDocumentAsync(connection, transaction, tenantId, documentId, cancellationToken);
        if (current is null) { await transaction.RollbackAsync(CancellationToken.None); return null; }
        if (update.FolderId is not null) await AssertFolderAsync(connection, tenantId, current.SiteId, update.FolderId.Value, cancellationToken, transaction);
        if (update.ExpectedVersion is not null && update.ExpectedVersion.Value != await ReadConfigVersionAsync(connection, transaction, tenantId, documentId, cancellationToken))
            throw Error("KNOWLEDGE_VERSION_CONFLICT", "资料已被其他请求修改。", 409);
        ValidateDates(update.EffectiveAt ?? current.EffectiveAt, update.ExpiresAt ?? current.ExpiresAt);
        var title = update.Title is null ? current.Title : NormalizeTitle(update.Title, current.Title);
        await ExecuteAsync(connection, transaction, """
            UPDATE knowledge_documents SET title=@title, folder_id=COALESCE(UUID_TO_BIN(@folder), folder_id),
              document_type=COALESCE(@type, document_type), product_key=COALESCE(@product, product_key),
              effective_at=COALESCE(@effective, effective_at), expires_at=COALESCE(@expires, expires_at),
              config_version=config_version+1, updated_at=UTC_TIMESTAMP(6)
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@document);
            """, cancellationToken, ("@title", title), ("@folder", update.FolderId?.ToString()),
            ("@type", update.DocumentType is null ? null : NormalizeShort(update.DocumentType, "general", 64)),
            ("@product", NormalizeNullable(update.ProductKey, 191)), ("@effective", ToUtc(update.EffectiveAt)),
            ("@expires", ToUtc(update.ExpiresAt)), ("@tenant", tenantId.ToString()), ("@document", documentId.ToString()));
        if (update.Tags is not null)
        {
            await ExecuteAsync(connection, transaction, "DELETE FROM knowledge_document_tags WHERE tenant_id=UUID_TO_BIN(@tenant) AND document_id=UUID_TO_BIN(@document);",
                cancellationToken, ("@tenant", tenantId.ToString()), ("@document", documentId.ToString()));
            foreach (var tag in NormalizeTags(update.Tags))
                await ExecuteAsync(connection, transaction, "INSERT INTO knowledge_document_tags (tenant_id,document_id,tag,created_at) VALUES (UUID_TO_BIN(@tenant),UUID_TO_BIN(@document),@tag,UTC_TIMESTAMP(6));",
                    cancellationToken, ("@tenant", tenantId.ToString()), ("@document", documentId.ToString()), ("@tag", tag));
        }
        await transaction.CommitAsync(cancellationToken);
        return await GetDocumentAsync(tenantId, documentId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<KnowledgeDocumentView?> RetryDocumentAsync(Guid tenantId, Guid documentId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var document = await ReadDocumentScopeAsync(connection, transaction, tenantId, documentId, cancellationToken);
        if (document is null) { await transaction.RollbackAsync(CancellationToken.None); return null; }
        AssertNotItemManaged(document.Value.SourceKind);
        if (document.Value.Status == "disabled")
            throw Error("KNOWLEDGE_NOT_RETRYABLE", "已停用的资料不能重试。", 409);
        var version = await ReadRetryVersionAsync(connection, transaction, tenantId, documentId, cancellationToken);
        if (version is null) { await transaction.RollbackAsync(CancellationToken.None); return null; }
        if (version.Value.Status != "failed") throw Error("KNOWLEDGE_NOT_RETRYABLE", "只有失败版本可以重试。", 409);
        await ExecuteAsync(connection, transaction, """
            UPDATE knowledge_document_versions SET status='pending', error_code=NULL, error_message=NULL, completed_at=NULL
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@version) AND status='failed';
            UPDATE knowledge_ingest_jobs SET status='pending', step='upload', next_attempt_at=UTC_TIMESTAMP(6),
                lease_owner=NULL, lease_until=NULL, error_code=NULL, error_message=NULL, updated_at=UTC_TIMESTAMP(6)
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND version_id=UUID_TO_BIN(@version);
            UPDATE knowledge_documents SET status=CASE WHEN current_version_id IS NULL THEN 'processing' ELSE 'ready' END, updated_at=UTC_TIMESTAMP(6)
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@document) AND status <> 'disabled';
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@version", version.Value.Id.ToString()), ("@document", documentId.ToString()));
        await transaction.CommitAsync(cancellationToken);
        return await GetDocumentAsync(tenantId, documentId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<KnowledgeDocumentView?> DisableDocumentAsync(Guid tenantId, Guid documentId,
        CancellationToken cancellationToken = default)
        => DisableDocumentCoreAsync(tenantId, documentId, allowItemManaged: false, cancellationToken);

    /// <inheritdoc />
    public Task<KnowledgeDocumentView?> DisableKnowledgeItemDocumentAsync(Guid tenantId, Guid documentId,
        CancellationToken cancellationToken = default)
        => DisableDocumentCoreAsync(tenantId, documentId, allowItemManaged: true, cancellationToken);

    private async Task<KnowledgeDocumentView?> DisableDocumentCoreAsync(Guid tenantId, Guid documentId, bool allowItemManaged,
        CancellationToken cancellationToken)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // 锁住资料行后再判断归属，判断与停用不会和迁移或其他写入交错；不存在的资料与原行为一致返回 null。
        var scope = await ReadDocumentScopeAsync(connection, transaction, tenantId, documentId, cancellationToken);
        if (scope is null) { await transaction.RollbackAsync(CancellationToken.None); return null; }
        if (!allowItemManaged) AssertNotItemManaged(scope.Value.SourceKind);
        await ExecuteAsync(connection, transaction, """
            UPDATE knowledge_documents SET status='disabled', disabled_at=UTC_TIMESTAMP(6), updated_at=UTC_TIMESTAMP(6)
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@document);
            UPDATE knowledge_document_versions v JOIN knowledge_documents d ON d.tenant_id=v.tenant_id AND d.id=v.document_id
            SET v.status=CASE WHEN v.status='ready' THEN 'disabled' ELSE v.status END
            WHERE d.tenant_id=UUID_TO_BIN(@tenant) AND d.id=UUID_TO_BIN(@document);
            UPDATE knowledge_ingest_jobs SET status='cancelled', lease_owner=NULL, lease_until=NULL,
              updated_at=UTC_TIMESTAMP(6)
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND document_id=UUID_TO_BIN(@document)
              AND status IN ('pending','running');
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@document", documentId.ToString()));
        await transaction.CommitAsync(cancellationToken);
        return await GetDocumentAsync(tenantId, documentId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<KnowledgeUsageView> GetUsageAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        var usage = await ReadUsageAsync(connection, tenantId, cancellationToken);
        return new KnowledgeUsageView(usage.UsedBytes, QuotaBytes, usage.Count, Math.Max(0, QuotaBytes - usage.UsedBytes));
    }

    /// <inheritdoc />
    public async Task<KnowledgeIngestJobLease?> ClaimIngestJobAsync(string workerId, TimeSpan lease,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            SELECT BIN_TO_UUID(j.id), BIN_TO_UUID(j.tenant_id), BIN_TO_UUID(j.document_id), BIN_TO_UUID(j.version_id),
                   v.object_key, v.original_file_name, v.extension, j.attempt_count
            FROM knowledge_ingest_jobs j
            JOIN knowledge_document_versions v ON v.tenant_id=j.tenant_id AND v.id=j.version_id
            JOIN knowledge_documents d ON d.tenant_id=j.tenant_id AND d.id=j.document_id
            WHERE ((j.status='pending' AND j.next_attempt_at <= UTC_TIMESTAMP(6))
                OR (j.status='running' AND j.lease_until < UTC_TIMESTAMP(6)))
              AND d.status <> 'disabled'
            ORDER BY j.next_attempt_at, j.created_at, j.id
            LIMIT 1 FOR UPDATE SKIP LOCKED;
            """, connection, transaction);
        // The reader must be closed before any further command (including
        // rollback) runs on this connection.
        KnowledgeIngestJobLease? job = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
                job = new KnowledgeIngestJobLease(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)),
                    Guid.Parse(reader.GetString(3)), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetInt32(7), workerId);
        }
        if (job is null) { await transaction.RollbackAsync(CancellationToken.None); return null; }
        await ExecuteAsync(connection, transaction, """
            UPDATE knowledge_ingest_jobs SET status='running', lease_owner=@owner, lease_until=DATE_ADD(UTC_TIMESTAMP(6), INTERVAL @seconds SECOND),
              attempt_count=attempt_count+1, started_at=COALESCE(started_at,UTC_TIMESTAMP(6)), updated_at=UTC_TIMESTAMP(6)
            WHERE id=UUID_TO_BIN(@id);
            UPDATE knowledge_document_versions SET status=CASE WHEN status='pending' THEN 'parsing' ELSE status END
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@version);
            UPDATE knowledge_documents SET status=CASE WHEN status='pending' THEN 'processing' ELSE status END
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@document) AND status <> 'disabled';
            """, cancellationToken, ("@owner", workerId), ("@seconds", Math.Max(5, (int)lease.TotalSeconds)),
            ("@id", job.JobId.ToString()), ("@tenant", job.TenantId.ToString()), ("@version", job.VersionId.ToString()), ("@document", job.DocumentId.ToString()));
        await transaction.CommitAsync(cancellationToken);
        return job with { AttemptCount = job.AttemptCount + 1 };
    }

    /// <inheritdoc />
    public async Task SaveExtractedDocumentAsync(KnowledgeIngestJobLease job, ExtractedDocument document,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await EnsureLeaseAsync(connection, transaction, job, cancellationToken);
        await ExecuteAsync(connection, transaction, """
            UPDATE knowledge_document_versions SET status='indexing', character_count=@characters, page_count=@pages,
              parser_version='local-v1', content_kind=@kind
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@version) AND status IN ('parsing','pending');
            UPDATE knowledge_ingest_jobs SET step='chunk', updated_at=UTC_TIMESTAMP(6)
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@job) AND status='running' AND lease_owner=@owner
              AND (lease_until IS NULL OR lease_until > UTC_TIMESTAMP(6));
            """, cancellationToken, ("@characters", document.CharacterCount), ("@pages", document.PageCount),
            ("@kind", string.Equals(document.ContentKind, "image", StringComparison.OrdinalIgnoreCase) ? "image" : "text"),
            ("@tenant", job.TenantId.ToString()), ("@version", job.VersionId.ToString()), ("@job", job.JobId.ToString()),
            ("@owner", job.LeaseOwner));
        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task PublishIndexedVersionAsync(KnowledgeIngestJobLease job, IReadOnlyList<KnowledgeChunk> chunks,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await EnsureLeaseAsync(connection, transaction, job, cancellationToken);
        // 图片版本没有可检索文本，允许 0 片段发布为 ready；文本版本保持「空内容不得 ready」。
        if (chunks.Count == 0 && !await IsImageVersionAsync(connection, transaction, job, cancellationToken))
            throw Error("KNOWLEDGE_EMPTY_CONTENT", "文件未生成可检索片段。", 422);
        ValidateEmbeddingMetadata(embeddingProvider);
        // A retry of the same version may produce fewer chunks than an earlier
        // attempt. Retain old rows for context-audit foreign keys, but mark
        // them inactive so stale text cannot re-enter new retrievals.
        await ExecuteAsync(connection, transaction, """
            UPDATE knowledge_embeddings e
            JOIN knowledge_document_chunks c ON c.tenant_id=e.tenant_id AND c.id=e.chunk_id
            SET e.status='deleted', e.updated_at=UTC_TIMESTAMP(6)
            WHERE c.tenant_id=UUID_TO_BIN(@tenant) AND c.version_id=UUID_TO_BIN(@version);
            UPDATE knowledge_document_chunks
            SET status='deleted', updated_at=UTC_TIMESTAMP(6)
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND version_id=UUID_TO_BIN(@version);
            """, cancellationToken, ("@tenant", job.TenantId.ToString()), ("@version", job.VersionId.ToString()));
        foreach (var chunk in chunks)
            await ExecuteAsync(connection, transaction, """
                INSERT INTO knowledge_document_chunks
                    (id,tenant_id,document_id,version_id,ordinal,text,heading_path,location_json,character_count,content_hash,status,created_at,updated_at)
                VALUES (UUID_TO_BIN(@id),UUID_TO_BIN(@tenant),UUID_TO_BIN(@document),UUID_TO_BIN(@version),@ordinal,@text,@heading,@location,@characters,@hash,'active',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6))
                ON DUPLICATE KEY UPDATE text=VALUES(text), heading_path=VALUES(heading_path), location_json=VALUES(location_json),
                    character_count=VALUES(character_count), content_hash=VALUES(content_hash), status='active', updated_at=UTC_TIMESTAMP(6);
                """, cancellationToken, ("@id", chunk.ChunkId.ToString()), ("@tenant", job.TenantId.ToString()),
                ("@document", job.DocumentId.ToString()), ("@version", job.VersionId.ToString()), ("@ordinal", chunk.Ordinal),
                ("@text", chunk.Text), ("@heading", chunk.HeadingPath), ("@location", JsonSerializer.Serialize(new { value = chunk.Location })),
                ("@characters", chunk.Text.Length), ("@hash", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(chunk.Text))).ToLowerInvariant()));
        foreach (var chunk in chunks)
        {
            var embeddingId = StableGuid($"{job.TenantId:N}:{chunk.ChunkId:N}:{embeddingProvider.Profile}");
            var vectorKey = $"{job.TenantId:N}/{job.VersionId:N}/{embeddingProvider.Profile}/{chunk.ChunkId:N}";
            await ExecuteAsync(connection, transaction, """
                INSERT INTO knowledge_embeddings
                    (id,tenant_id,chunk_id,profile,model,dimensions,vector_key,status,created_at,updated_at)
                VALUES (UUID_TO_BIN(@id),UUID_TO_BIN(@tenant),UUID_TO_BIN(@chunk),@profile,@model,@dimensions,@vector,'indexed',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6))
                ON DUPLICATE KEY UPDATE model=VALUES(model), dimensions=VALUES(dimensions), vector_key=VALUES(vector_key), status='indexed', updated_at=UTC_TIMESTAMP(6);
                """, cancellationToken, ("@id", embeddingId.ToString()), ("@tenant", job.TenantId.ToString()),
                ("@chunk", chunk.ChunkId.ToString()), ("@profile", embeddingProvider.Profile), ("@model", embeddingProvider.Model),
                ("@dimensions", embeddingProvider.Dimensions), ("@vector", vectorKey));
        }
        await ExecuteAsync(connection, transaction, """
            UPDATE knowledge_document_versions SET status='ready', completed_at=UTC_TIMESTAMP(6), error_code=NULL, error_message=NULL
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@version)
              AND EXISTS (SELECT 1 FROM knowledge_ingest_jobs WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@job)
                          AND status='running' AND lease_owner=@owner AND (lease_until IS NULL OR lease_until > UTC_TIMESTAMP(6)));
            UPDATE knowledge_documents d
            JOIN knowledge_document_versions publishing ON publishing.tenant_id=d.tenant_id
                AND publishing.document_id=d.id AND publishing.id=UUID_TO_BIN(@version)
            LEFT JOIN knowledge_document_versions current ON current.tenant_id=d.tenant_id
                AND current.id=d.current_version_id
            SET d.current_version_id=publishing.id, d.status='ready', d.updated_at=UTC_TIMESTAMP(6)
            WHERE d.tenant_id=UUID_TO_BIN(@tenant) AND d.id=UUID_TO_BIN(@document)
              AND d.status <> 'disabled'
              AND (current.id IS NULL OR current.version_number < publishing.version_number);
            UPDATE knowledge_ingest_jobs SET step='publish', status='done', lease_owner=NULL, lease_until=NULL,
              completed_at=UTC_TIMESTAMP(6), updated_at=UTC_TIMESTAMP(6)
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@job) AND status='running' AND lease_owner=@owner
              AND (lease_until IS NULL OR lease_until > UTC_TIMESTAMP(6));
            """, cancellationToken, ("@tenant", job.TenantId.ToString()), ("@version", job.VersionId.ToString()), ("@document", job.DocumentId.ToString()), ("@job", job.JobId.ToString()), ("@owner", job.LeaseOwner));
        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task FailIngestJobAsync(KnowledgeIngestJobLease job, string errorCode, string errorMessage,
        bool retryable, CancellationToken cancellationToken = default)
    {
        errorMessage = errorMessage.Length > 1000 ? errorMessage[..1000] : errorMessage;
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await EnsureLeaseAsync(connection, transaction, job, cancellationToken);
        var status = retryable && job.AttemptCount < 3 ? "pending" : "failed";
        await ExecuteAsync(connection, transaction, """
            UPDATE knowledge_document_versions SET status=@version_status, error_code=@code, error_message=@message,
              completed_at=CASE WHEN @version_status='failed' THEN UTC_TIMESTAMP(6) ELSE NULL END
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@version)
              AND EXISTS (SELECT 1 FROM knowledge_ingest_jobs WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@job)
                          AND status='running' AND lease_owner=@owner AND (lease_until IS NULL OR lease_until > UTC_TIMESTAMP(6)));
            UPDATE knowledge_ingest_jobs SET status=@job_status, error_code=@code, error_message=@message,
              next_attempt_at=DATE_ADD(UTC_TIMESTAMP(6), INTERVAL @delay SECOND), lease_owner=NULL, lease_until=NULL, updated_at=UTC_TIMESTAMP(6)
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@job) AND status='running' AND lease_owner=@owner
              AND (lease_until IS NULL OR lease_until > UTC_TIMESTAMP(6));
            UPDATE knowledge_documents SET status=CASE WHEN current_version_id IS NOT NULL THEN 'ready' ELSE
              CASE WHEN @job_status='failed' THEN 'failed' ELSE 'processing' END END,
              updated_at=UTC_TIMESTAMP(6)
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@document) AND status <> 'disabled';
            """, cancellationToken, ("@version_status", status), ("@job_status", status), ("@code", errorCode), ("@owner", job.LeaseOwner),
            ("@message", errorMessage), ("@tenant", job.TenantId.ToString()), ("@version", job.VersionId.ToString()),
            ("@job", job.JobId.ToString()), ("@document", job.DocumentId.ToString()), ("@delay", Math.Min(3600, 30 * Math.Max(1, job.AttemptCount))));
        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<KnowledgeSearchResponse> SearchAsync(Guid tenantId, Guid siteId, KnowledgeSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            throw Error("INVALID_KNOWLEDGE_QUERY", "检索问题不能为空。", 400);
        var terms = KnowledgeLexicalTerms.Extract(request.Query);
        var items = await SearchEvidenceAsync(tenantId, siteId, request, terms, cancellationToken);
        var gaps = items.Count == 0 ? [string.IsNullOrWhiteSpace(request.Topic) ? request.Query.Trim() : request.Topic.Trim()] : Array.Empty<string>();
        return new KnowledgeSearchResponse(items, gaps);
    }

    /// <inheritdoc />
    public async Task<KnowledgeContextResponse> CreateContextAsync(Guid tenantId, Guid siteId,
        KnowledgeContextRequest request, CancellationToken cancellationToken = default)
    {
        var queries = (request.Questions ?? []).Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item.Trim()).ToList();
        if (!string.IsNullOrWhiteSpace(request.Topic)) queries.Insert(0, request.Topic.Trim());
        if (queries.Count == 0)
            throw Error("INVALID_KNOWLEDGE_QUERY", "请提供文章主题或至少一个问题。", 400);
        var maxChunks = Math.Clamp(request.MaxChunks, 1, 100);
        var maxCharacters = Math.Clamp(request.MaxCharacters, 1000, 100_000);
        KnowledgeSearchRequest SearchFor(string query) => new(query, request.ScopeMode, request.Tags, request.DocumentType,
            request.ProductKey, request.Language, maxChunks, request.Topic, request.DocumentIds, request.FolderIds);
        var relevant = new List<IReadOnlyList<KnowledgeEvidenceView>>(queries.Count); var gaps = new List<string>();
        foreach (var query in queries)
        {
            var found = await SearchEvidenceAsync(tenantId, siteId, SearchFor(query), KnowledgeLexicalTerms.Extract(query), cancellationToken);
            // Gaps only reflect relevance: pinned chunks added below never close a gap.
            if (found.Count == 0 && query.Length > 0) gaps.Add(query);
            relevant.Add(found);
        }
        // Selected documents or a selected product are used directly even when their text
        // does not match the question; relevance only orders them. The same visibility
        // filter applies, so a pinned document cannot bypass scope/status/effective rules.
        var pinned = (request.DocumentIds ?? []).Any(id => id != Guid.Empty) || !string.IsNullOrWhiteSpace(request.ProductKey)
            ? await LoadPinnedEvidenceAsync(tenantId, siteId, SearchFor(queries[0]), cancellationToken)
            : [];
        var all = KnowledgeContextAssembler.Assemble(relevant, pinned, maxChunks, maxCharacters);
        var conflicts = KnowledgeConflictDetector.FindCandidates(all);
        var contextId = Guid.NewGuid();
        var filtersHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request)))).ToLowerInvariant();
        var response = new KnowledgeContextResponse(contextId, all, gaps.Distinct(StringComparer.Ordinal).ToArray(), conflicts,
            request.ScopeMode, all.Sum(item => item.Text.Length));
        await SaveContextAsync(tenantId, siteId, response, string.Join(" | ", queries), filtersHash, cancellationToken);
        return response;
    }

    /// <inheritdoc />
    public async Task<KnowledgeContextResponse?> GetContextAsync(Guid tenantId, Guid contextId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            SELECT scope_mode, snapshot_json FROM knowledge_context_requests
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@context) LIMIT 1;
            """, connection);
        command.Parameters.AddWithValue("@tenant", tenantId.ToString()); command.Parameters.AddWithValue("@context", contextId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var scope = reader.GetString(0);
        var snapshot = reader.IsDBNull(1) ? null : reader.GetString(1);
        await reader.DisposeAsync();
        if (snapshot is not null)
            return JsonSerializer.Deserialize<KnowledgeContextResponse>(snapshot)
                ?? throw Error("KNOWLEDGE_CONTEXT_INVALID", "上下文快照无效。", 500);
        var items = await ReadContextEvidenceAsync(connection, tenantId, contextId, cancellationToken);
        var response = new KnowledgeContextResponse(contextId, items, Array.Empty<string>(), Array.Empty<KnowledgeConflict>(),
            scope == "brand_only" ? KnowledgeScopeMode.BrandOnly : KnowledgeScopeMode.Default, items.Sum(item => item.Text.Length));
        return response;
    }

    private async Task<IReadOnlyList<KnowledgeEvidenceView>> SearchEvidenceAsync(Guid tenantId, Guid siteId,
        KnowledgeSearchRequest request, IReadOnlyList<string> terms, CancellationToken cancellationToken)
    {
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        await AssertSiteAsync(connection, tenantId, siteId, cancellationToken);
        var filter = BuildSearchFilter(request);
        var visibleVersions = new HashSet<Guid>();
        await using (var versionCommand = new MySqlCommand($"""
            SELECT BIN_TO_UUID(v.id)
            FROM knowledge_document_versions v
            JOIN knowledge_documents d ON d.tenant_id=v.tenant_id AND d.id=v.document_id
            {filter.Where};
            """, connection))
        {
            BindSearchParameters(versionCommand, tenantId, siteId, request, filter);
            await using var versionReader = await versionCommand.ExecuteReaderAsync(cancellationToken);
            while (await versionReader.ReadAsync(cancellationToken))
                visibleVersions.Add(Guid.Parse(versionReader.GetString(0)));
        }
        if (visibleVersions.Count == 0) return [];

        var topK = Math.Clamp(request.TopK, 1, 100);
        var vectorScores = new Dictionary<Guid, double>();
        // The development embedding hashes bytes, so its cosine score has no semantic
        // meaning. Search all literal matches instead of claiming unrelated evidence.
        if (embeddingProvider is not DeterministicEmbeddingProvider)
        {
            var queryText = string.IsNullOrWhiteSpace(request.Topic)
                ? request.Query.Trim()
                : $"{request.Topic.Trim()}\n{request.Query.Trim()}";
            var queryVector = (await embeddingProvider.EmbedAsync([queryText], cancellationToken)).SingleOrDefault();
            if (queryVector is not null)
            {
                var hits = await vectorIndex.SearchAsync(tenantId, visibleVersions, queryVector,
                    Math.Clamp(topK * 4, 1, 400), cancellationToken);
                foreach (var hit in hits)
                    vectorScores[hit.ChunkId] = Math.Max(vectorScores.GetValueOrDefault(hit.ChunkId, double.NegativeInfinity), hit.Score);
            }
        }
        if (vectorScores.Count == 0 && terms.Count == 0) return [];

        var chunkFilter = new StringBuilder(" AND c.status='active'");
        if (vectorScores.Count > 0)
            chunkFilter.Append(" AND c.id IN (").Append(string.Join(",", vectorScores.Keys.Select((_, index) => $"UUID_TO_BIN(@chunk{index})"))).Append(')');
        else
            chunkFilter.Append(" AND (").Append(string.Join(" OR ", terms.Select((_, index) => $"LOCATE(@term{index}, c.text)>0"))).Append(')');
        var sql = $"""
            SELECT {EvidenceColumns}
            FROM knowledge_document_chunks c
            JOIN knowledge_documents d ON d.tenant_id=c.tenant_id AND d.id=c.document_id
            JOIN knowledge_document_versions v ON v.tenant_id=c.tenant_id AND v.id=c.version_id
            {filter.Where} {chunkFilter};
            """;
        var candidates = new List<KnowledgeEvidenceView>(topK);
        // Literal mode: a chunk must share at least MinMatches distinct terms (two word
        // pairs for a Chinese question) so one common pair does not pull in unrelated text.
        var minMatches = KnowledgeLexicalTerms.MinMatches(terms);
        await using var command = new MySqlCommand(sql, connection);
        BindSearchParameters(command, tenantId, siteId, request, filter);
        if (vectorScores.Count > 0)
        {
            var index = 0;
            foreach (var id in vectorScores.Keys) command.Parameters.AddWithValue($"@chunk{index++}", id.ToString());
        }
        else
            for (var i = 0; i < terms.Count; i++) command.Parameters.AddWithValue($"@term{i}", terms[i]);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var chunkId = Guid.Parse(reader.GetString(0));
            var text = reader.GetString(4);
            var (lexicalScore, matches) = KnowledgeLexicalTerms.Score(text, terms);
            if (vectorScores.Count == 0 && matches < minMatches) continue;
            var score = vectorScores.TryGetValue(chunkId, out var vectorScore)
                ? vectorScore + Math.Min(lexicalScore, 1d) * 0.01d : lexicalScore;
            KeepBestEvidence(candidates, ReadEvidence(reader, score), topK);
        }
        candidates.Sort(CompareEvidence);
        return candidates;
    }

    /// <summary>
    /// Loads every visible active chunk of the pinned documents/product, using the exact
    /// visibility filter of <see cref="SearchEvidenceAsync"/>; score is 0 (not relevance).
    /// </summary>
    private async Task<IReadOnlyList<KnowledgePinnedChunk>> LoadPinnedEvidenceAsync(Guid tenantId, Guid siteId,
        KnowledgeSearchRequest request, CancellationToken cancellationToken)
    {
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        await AssertSiteAsync(connection, tenantId, siteId, cancellationToken);
        var filter = BuildSearchFilter(request);
        // Cap per document at the context's chunk budget (TopK = MaxChunks) so one very
        // large pinned document cannot starve the others, and order by that rank first so
        // the 1000-row bound still returns every document's leading chunks.
        await using var command = new MySqlCommand($"""
            SELECT * FROM (
                SELECT {EvidenceColumns}, c.ordinal,
                       ROW_NUMBER() OVER (PARTITION BY d.id ORDER BY c.ordinal) AS pinned_rank
                FROM knowledge_document_chunks c
                JOIN knowledge_documents d ON d.tenant_id=c.tenant_id AND d.id=c.document_id
                JOIN knowledge_document_versions v ON v.tenant_id=c.tenant_id AND v.id=c.version_id
                {filter.Where} AND c.status='active'
            ) pinned
            WHERE pinned.pinned_rank <= @pinned_limit
            ORDER BY pinned.pinned_rank, 2, 12
            LIMIT 1000;
            """, connection);
        BindSearchParameters(command, tenantId, siteId, request, filter);
        command.Parameters.AddWithValue("@pinned_limit", Math.Clamp(request.TopK, 1, 100));
        var pinned = new List<KnowledgePinnedChunk>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            pinned.Add(new KnowledgePinnedChunk(ReadEvidence(reader, 0d), Convert.ToInt64(reader.GetValue(11), CultureInfo.InvariantCulture)));
        return pinned;
    }

    private const string EvidenceColumns = """
        BIN_TO_UUID(c.id), BIN_TO_UUID(d.id), BIN_TO_UUID(v.id), d.title, c.text,
               CASE WHEN d.site_id IS NULL THEN 'tenant' ELSE 'site' END, v.original_file_name,
               CASE WHEN JSON_TYPE(JSON_EXTRACT(c.location_json, '$.value'))='STRING' THEN c.location_json->>'$.value' END, d.effective_at, d.expires_at, d.product_key
        """;

    private static KnowledgeEvidenceView ReadEvidence(MySqlDataReader reader, double score)
        => new($"{reader.GetString(2)}:{reader.GetString(0)}",
            Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)), Guid.Parse(reader.GetString(0)), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7),
            score, ReadNullableUtc(reader, 8), ReadNullableUtc(reader, 9),
            reader.IsDBNull(10) ? null : reader.GetString(10));

    /// <summary>Visibility WHERE (tenant/brand scope, ready current version, effective window) plus request filters.</summary>
    private sealed record SearchFilter(string Where, IReadOnlyList<string> TagFilters, Guid[] DocumentIds, Guid[] FolderIds);

    private static SearchFilter BuildSearchFilter(KnowledgeSearchRequest request)
    {
        var where = new StringBuilder("""
            WHERE d.tenant_id=UUID_TO_BIN(@tenant) AND d.current_version_id=v.id
              AND d.status='ready' AND v.status='ready'
              AND (d.effective_at IS NULL OR d.effective_at <= UTC_TIMESTAMP(6))
              AND (d.expires_at IS NULL OR d.expires_at > UTC_TIMESTAMP(6))
              AND (d.site_id=UUID_TO_BIN(@site) OR (d.site_id IS NULL AND @brand_only=0))
            """);
        if (!string.IsNullOrWhiteSpace(request.DocumentType)) where.Append(" AND d.document_type=@type");
        if (!string.IsNullOrWhiteSpace(request.ProductKey)) where.Append(" AND d.product_key=@product");
        // Uploads, links and product sync default to 'und'; an undetermined language must not hide them.
        if (!string.IsNullOrWhiteSpace(request.Language)) where.Append(" AND (d.language=@language OR d.language='und')");
        var tagFilters = NormalizeTags(request.Tags);
        for (var i = 0; i < tagFilters.Count; i++) where.Append($" AND EXISTS (SELECT 1 FROM knowledge_document_tags ft{i} WHERE ft{i}.tenant_id=d.tenant_id AND ft{i}.document_id=d.id AND ft{i}.tag=@tag{i})");
        var documentIds = (request.DocumentIds ?? []).Where(id => id != Guid.Empty).Distinct().Take(20).ToArray();
        if (documentIds.Length > 0)
            where.Append(" AND d.id IN (").Append(string.Join(",", documentIds.Select((_, index) => $"UUID_TO_BIN(@document{index})"))).Append(')');
        var folderIds = (request.FolderIds ?? []).Where(id => id != Guid.Empty).Distinct().Take(50).ToArray();
        if (folderIds.Length > 0)
            where.Append(" AND d.folder_id IN (").Append(string.Join(",", folderIds.Select((_, index) => $"UUID_TO_BIN(@folder{index})"))).Append(')');
        return new SearchFilter(where.ToString(), tagFilters, documentIds, folderIds);
    }

    private static void BindSearchParameters(MySqlCommand command, Guid tenantId, Guid siteId,
        KnowledgeSearchRequest request, SearchFilter filter)
    {
        command.Parameters.AddWithValue("@tenant", tenantId.ToString());
        command.Parameters.AddWithValue("@site", siteId.ToString());
        command.Parameters.AddWithValue("@brand_only", request.ScopeMode == KnowledgeScopeMode.BrandOnly ? 1 : 0);
        if (!string.IsNullOrWhiteSpace(request.DocumentType)) command.Parameters.AddWithValue("@type", request.DocumentType.Trim());
        if (!string.IsNullOrWhiteSpace(request.ProductKey)) command.Parameters.AddWithValue("@product", request.ProductKey.Trim());
        if (!string.IsNullOrWhiteSpace(request.Language)) command.Parameters.AddWithValue("@language", request.Language.Trim());
        for (var i = 0; i < filter.TagFilters.Count; i++) command.Parameters.AddWithValue($"@tag{i}", filter.TagFilters[i]);
        for (var i = 0; i < filter.DocumentIds.Length; i++) command.Parameters.AddWithValue($"@document{i}", filter.DocumentIds[i].ToString());
        for (var i = 0; i < filter.FolderIds.Length; i++) command.Parameters.AddWithValue($"@folder{i}", filter.FolderIds[i].ToString());
    }

    private static int CompareEvidence(KnowledgeEvidenceView left, KnowledgeEvidenceView right)
    {
        var byScore = right.Score.CompareTo(left.Score);
        if (byScore != 0) return byScore;
        var byDocument = left.DocumentId.CompareTo(right.DocumentId);
        return byDocument != 0 ? byDocument : left.ChunkId.CompareTo(right.ChunkId);
    }

    internal static void KeepBestEvidence(List<KnowledgeEvidenceView> candidates,
        KnowledgeEvidenceView item, int topK)
    {
        candidates.Add(item);
        if (candidates.Count <= topK) return;
        candidates.Sort(CompareEvidence);
        candidates.RemoveAt(candidates.Count - 1);
    }

    private async Task SaveContextAsync(Guid tenantId, Guid siteId, KnowledgeContextResponse response,
        string summary, string filtersHash, CancellationToken cancellationToken)
    {
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(connection, transaction, """
            INSERT INTO knowledge_context_requests (id,tenant_id,site_id,query_summary,scope_mode,filters_hash,requested_chunk_count,character_count,snapshot_json,created_at)
            VALUES (UUID_TO_BIN(@id),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),@summary,@scope,@hash,@count,@characters,@snapshot,UTC_TIMESTAMP(6));
            """, cancellationToken, ("@id", response.ContextId.ToString()), ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()),
            ("@summary", summary.Length > 1000 ? summary[..1000] : summary), ("@scope", response.ScopeMode == KnowledgeScopeMode.BrandOnly ? "brand_only" : "default"),
            ("@hash", filtersHash), ("@count", response.Items.Count), ("@characters", response.CharacterCount),
            ("@snapshot", JsonSerializer.Serialize(response)));
        for (var i = 0; i < response.Items.Count; i++)
            await ExecuteAsync(connection, transaction, """
                INSERT INTO knowledge_context_hits (context_id,tenant_id,position,chunk_id,version_id,score,created_at)
                VALUES (UUID_TO_BIN(@context),UUID_TO_BIN(@tenant),@position,UUID_TO_BIN(@chunk),UUID_TO_BIN(@version),@score,UTC_TIMESTAMP(6));
                """, cancellationToken, ("@context", response.ContextId.ToString()), ("@tenant", tenantId.ToString()), ("@position", i),
                ("@chunk", response.Items[i].ChunkId.ToString()), ("@version", response.Items[i].VersionId.ToString()), ("@score", response.Items[i].Score));
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<KnowledgeEvidenceView>> ReadContextEvidenceAsync(MySqlConnection connection, Guid tenantId,
        Guid contextId, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand("""
            SELECT BIN_TO_UUID(c.id), BIN_TO_UUID(d.id), BIN_TO_UUID(v.id), d.title, c.text,
                   CASE WHEN d.site_id IS NULL THEN 'tenant' ELSE 'site' END, v.original_file_name, CASE WHEN JSON_TYPE(JSON_EXTRACT(c.location_json, '$.value'))='STRING' THEN c.location_json->>'$.value' END,
                   d.effective_at, d.expires_at, h.score, d.product_key
            FROM knowledge_context_hits h JOIN knowledge_document_chunks c ON c.tenant_id=h.tenant_id AND c.id=h.chunk_id
            JOIN knowledge_documents d ON d.tenant_id=c.tenant_id AND d.id=c.document_id
            JOIN knowledge_document_versions v ON v.tenant_id=h.tenant_id AND v.id=h.version_id
            WHERE h.tenant_id=UUID_TO_BIN(@tenant) AND h.context_id=UUID_TO_BIN(@context)
            ORDER BY h.position;
            """, connection);
        command.Parameters.AddWithValue("@tenant", tenantId.ToString()); command.Parameters.AddWithValue("@context", contextId.ToString());
        var result = new List<KnowledgeEvidenceView>(); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(new KnowledgeEvidenceView($"{reader.GetString(2)}:{reader.GetString(0)}",
            Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)), Guid.Parse(reader.GetString(0)), reader.GetString(3), reader.GetString(4),
            reader.GetString(5), reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetDouble(10), ReadNullableUtc(reader, 8), ReadNullableUtc(reader, 9),
            reader.IsDBNull(11) ? null : reader.GetString(11)));
        return result;
    }

    private async Task<KnowledgeDocumentView?> FindByIdempotencyAsync(MySqlConnection connection, Guid tenantId, string key,
        CancellationToken cancellationToken, MySqlTransaction? transaction = null)
    {
        await using var command = new MySqlCommand("SELECT BIN_TO_UUID(document_id) FROM knowledge_ingest_jobs WHERE tenant_id=UUID_TO_BIN(@tenant) AND idempotency_key=@key LIMIT 1;", connection, transaction);
        command.Parameters.AddWithValue("@tenant", tenantId.ToString()); command.Parameters.AddWithValue("@key", key.Trim());
        var value = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (value is null) return null;
        if (transaction is not null)
        {
            var document = await GetDocumentAsync(connection, transaction, tenantId, Guid.Parse(value), cancellationToken);
            return document;
        }
        return await GetDocumentAsync(tenantId, Guid.Parse(value), cancellationToken);
    }

    private static async Task AssertSiteAsync(MySqlConnection connection, Guid tenantId, Guid siteId, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand("SELECT 1 FROM sites WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@site) AND status <> 'archived' LIMIT 1;", connection);
        command.Parameters.AddWithValue("@tenant", tenantId.ToString()); command.Parameters.AddWithValue("@site", siteId.ToString());
        if (await command.ExecuteScalarAsync(cancellationToken) is null) throw Error("RESOURCE_NOT_FOUND", "品牌不存在。", 404);
    }

    private static async Task AssertFolderAsync(MySqlConnection connection, Guid tenantId, Guid? siteId, Guid folderId,
        CancellationToken cancellationToken, MySqlTransaction? transaction = null)
    {
        await using var command = new MySqlCommand("SELECT COALESCE(BIN_TO_UUID(site_id), '') FROM knowledge_folders WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@folder) AND status='active' LIMIT 1;", connection, transaction);
        command.Parameters.AddWithValue("@tenant", tenantId.ToString()); command.Parameters.AddWithValue("@folder", folderId.ToString());
        var folderSite = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (folderSite is null)
            throw Error("RESOURCE_NOT_FOUND", "文件夹不存在。", 404);
        if (folderSite.Length == 0 && siteId is not null)
            throw Error("RESOURCE_NOT_FOUND", "文件夹不属于当前资料范围。", 404);
        if (folderSite.Length > 0 && (siteId is null || !Guid.Parse(folderSite).Equals(siteId.Value)))
            throw Error("RESOURCE_NOT_FOUND", "文件夹不属于当前资料范围。", 404);
    }

    private static async Task AssertNoFolderCycleAsync(MySqlConnection connection, MySqlTransaction transaction,
        Guid tenantId, Guid folderId, Guid proposedParentId, CancellationToken cancellationToken)
    {
        if (await KnowledgeFolderHierarchy.WouldCreateCycleAsync(folderId, proposedParentId, async (currentId, token) =>
        {
            await using var command = new MySqlCommand("""
                SELECT CASE WHEN parent_id IS NULL THEN NULL ELSE BIN_TO_UUID(parent_id) END
                FROM knowledge_folders WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@folder) LIMIT 1;
                """, connection, transaction);
            command.Parameters.AddWithValue("@tenant", tenantId.ToString());
            command.Parameters.AddWithValue("@folder", currentId.ToString());
            var value = await command.ExecuteScalarAsync(token) as string;
            return value is null ? null : Guid.Parse(value);
        }, cancellationToken))
            throw Error("KNOWLEDGE_FOLDER_CYCLE", "文件夹不能移动到自身或子文件夹。", 400);
    }

    private static async Task<KnowledgeFolderView?> GetFolderAsync(MySqlConnection connection, Guid tenantId, Guid folderId,
        CancellationToken cancellationToken, MySqlTransaction? transaction = null)
    {
        await using var command = new MySqlCommand("SELECT BIN_TO_UUID(id),BIN_TO_UUID(tenant_id),CASE WHEN site_id IS NULL THEN NULL ELSE BIN_TO_UUID(site_id) END,CASE WHEN parent_id IS NULL THEN NULL ELSE BIN_TO_UUID(parent_id) END,name,status,created_at,updated_at FROM knowledge_folders WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@id) LIMIT 1;", connection, transaction);
        command.Parameters.AddWithValue("@tenant", tenantId.ToString()); command.Parameters.AddWithValue("@id", folderId.ToString()); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadFolder(reader) : null;
    }

    private static KnowledgeFolderView ReadFolder(MySqlDataReader reader) => new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
        reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)), reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)), reader.GetString(4), reader.GetString(5),
        ToOffset(reader.GetDateTime(6))!.Value, ToOffset(reader.GetDateTime(7))!.Value);

    private static async Task<(long UsedBytes, long Count)> ReadUsageAsync(MySqlConnection connection, Guid tenantId,
        CancellationToken cancellationToken, MySqlTransaction? transaction = null)
    {
        await using var command = new MySqlCommand("SELECT COALESCE(SUM(byte_length),0), COUNT(*) FROM knowledge_document_versions WHERE tenant_id=UUID_TO_BIN(@tenant);", connection, transaction);
        command.Parameters.AddWithValue("@tenant", tenantId.ToString()); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return (Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture), reader.GetInt64(1));
    }

    private static async Task LockTenantQuotaAsync(MySqlConnection connection, MySqlTransaction transaction,
        Guid tenantId, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(
            "SELECT id FROM tenants WHERE id=UUID_TO_BIN(@tenant) FOR UPDATE;", connection, transaction);
        command.Parameters.AddWithValue("@tenant", tenantId.ToString());
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
            throw Error("RESOURCE_NOT_FOUND", "租户不存在。", 404);
    }

    private async Task<IReadOnlyList<KnowledgeDocumentView>> HydrateDocumentsAsync(MySqlConnection connection, IEnumerable<(Guid Id, Guid Tenant, Guid? Site, string Scope, Guid? Folder, string Title, string Status,
        string Language, string Type, string? Product, DateTime? Effective, DateTime? Expires, Guid? Current, DateTime Created, DateTime Updated, int ConfigVersion, string SourceKind, string? SourceUrl)> rows, CancellationToken cancellationToken)
    {
        var result = new List<KnowledgeDocumentView>();
        foreach (var row in rows)
        {
            var tags = new List<string>();
            await using (var tagCommand = new MySqlCommand("SELECT tag FROM knowledge_document_tags WHERE tenant_id=UUID_TO_BIN(@tenant) AND document_id=UUID_TO_BIN(@document) ORDER BY tag;", connection))
            { tagCommand.Parameters.AddWithValue("@tenant", row.Tenant.ToString()); tagCommand.Parameters.AddWithValue("@document", row.Id.ToString()); await using var tagReader = await tagCommand.ExecuteReaderAsync(cancellationToken); while (await tagReader.ReadAsync(cancellationToken)) tags.Add(tagReader.GetString(0)); }
            KnowledgeVersionView? version = null;
            if (row.Current is not null)
            {
                await using var versionCommand = new MySqlCommand("""
                    SELECT BIN_TO_UUID(id), version_number, original_file_name, extension, detected_content_type, byte_length, sha256,
                      status, error_code, error_message, character_count, page_count, created_at, completed_at, content_kind
                    FROM knowledge_document_versions WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@version) LIMIT 1;
                    """, connection);
                versionCommand.Parameters.AddWithValue("@tenant", row.Tenant.ToString()); versionCommand.Parameters.AddWithValue("@version", row.Current.Value.ToString());
                await using var vr = await versionCommand.ExecuteReaderAsync(cancellationToken);
                if (await vr.ReadAsync(cancellationToken)) version = ReadVersion(vr);
            }
            KnowledgeVersionView? latestVersion;
            await using (var latestCommand = new MySqlCommand("""
                SELECT BIN_TO_UUID(id), version_number, original_file_name, extension, detected_content_type, byte_length, sha256,
                  status, error_code, error_message, character_count, page_count, created_at, completed_at, content_kind
                FROM knowledge_document_versions
                WHERE tenant_id=UUID_TO_BIN(@tenant) AND document_id=UUID_TO_BIN(@document)
                ORDER BY version_number DESC LIMIT 1;
                """, connection))
            {
                latestCommand.Parameters.AddWithValue("@tenant", row.Tenant.ToString());
                latestCommand.Parameters.AddWithValue("@document", row.Id.ToString());
                await using var latestReader = await latestCommand.ExecuteReaderAsync(cancellationToken);
                latestVersion = await latestReader.ReadAsync(cancellationToken) ? ReadVersion(latestReader) : null;
            }
            result.Add(new KnowledgeDocumentView(row.Id, row.Tenant, row.Site, row.Scope, row.Folder, row.Title, row.Status, row.Language, row.Type, row.Product,
                tags, ToOffset(row.Effective), ToOffset(row.Expires), row.Current, version, ToOffset(row.Created)!.Value, ToOffset(row.Updated)!.Value, row.ConfigVersion, latestVersion,
                row.SourceKind, row.SourceUrl));
        }
        return result;
    }

    private static (Guid Id, Guid Tenant, Guid? Site, string Scope, Guid? Folder, string Title, string Status,
        string Language, string Type, string? Product, DateTime? Effective, DateTime? Expires, Guid? Current, DateTime Created, DateTime Updated, int ConfigVersion, string SourceKind, string? SourceUrl) ReadDocumentRow(MySqlDataReader reader)
        => (Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)), reader.GetString(3),
            reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetDateTime(10), reader.IsDBNull(11) ? null : reader.GetDateTime(11),
            reader.IsDBNull(12) ? null : Guid.Parse(reader.GetString(12)), reader.GetDateTime(13), reader.GetDateTime(14), reader.GetInt32(15),
            reader.GetString(16), reader.IsDBNull(17) ? null : reader.GetString(17));

    private static KnowledgeVersionView ReadVersion(MySqlDataReader reader) => new(Guid.Parse(reader.GetString(0)), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt64(5), reader.GetString(6), reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetInt32(10), reader.IsDBNull(11) ? null : reader.GetInt32(11), ToOffset(reader.GetDateTime(12))!.Value, reader.IsDBNull(13) ? null : ToOffset(reader.GetDateTime(13)),
        reader.FieldCount > 14 && !reader.IsDBNull(14) ? reader.GetString(14) : "text");

    private static async Task<(Guid? SiteId, string Status, string SourceKind)?> ReadDocumentScopeAsync(MySqlConnection connection,
        MySqlTransaction transaction, Guid tenantId, Guid documentId, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand("""
            SELECT CASE WHEN site_id IS NULL THEN NULL ELSE BIN_TO_UUID(site_id) END, status, source_kind
            FROM knowledge_documents
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@document)
            FOR UPDATE;
            """, connection, transaction);
        command.Parameters.AddWithValue("@tenant", tenantId.ToString());
        command.Parameters.AddWithValue("@document", documentId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return (reader.IsDBNull(0) ? null : Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2));
    }

    private static async Task<int> ReadNextVersionAsync(MySqlConnection connection, MySqlTransaction transaction,
        Guid tenantId, Guid documentId, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand("""
            SELECT COALESCE(MAX(version_number), 0) + 1
            FROM knowledge_document_versions
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND document_id=UUID_TO_BIN(@document);
            """, connection, transaction);
        command.Parameters.AddWithValue("@tenant", tenantId.ToString());
        command.Parameters.AddWithValue("@document", documentId.ToString());
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<KnowledgeDocumentView?> GetDocumentAsync(MySqlConnection connection, MySqlTransaction transaction, Guid tenantId, Guid documentId, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand("SELECT BIN_TO_UUID(id),BIN_TO_UUID(tenant_id),CASE WHEN site_id IS NULL THEN NULL ELSE BIN_TO_UUID(site_id) END,CASE WHEN site_id IS NULL THEN 'tenant' ELSE 'site' END,CASE WHEN folder_id IS NULL THEN NULL ELSE BIN_TO_UUID(folder_id) END,title,status,language,document_type,product_key,effective_at,expires_at,CASE WHEN current_version_id IS NULL THEN NULL ELSE BIN_TO_UUID(current_version_id) END,created_at,updated_at,config_version,source_kind,source_url FROM knowledge_documents WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@document) LIMIT 1;", connection, transaction);
        command.Parameters.AddWithValue("@tenant", tenantId.ToString()); command.Parameters.AddWithValue("@document", documentId.ToString()); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadDocumentRow(reader) is var row ? new KnowledgeDocumentView(row.Id,row.Tenant,row.Site,row.Scope,row.Folder,row.Title,row.Status,row.Language,row.Type,row.Product,Array.Empty<string>(),ToOffset(row.Effective),ToOffset(row.Expires),row.Current,null,ToOffset(row.Created)!.Value,ToOffset(row.Updated)!.Value,row.ConfigVersion,null,row.SourceKind,row.SourceUrl) : null : null;
    }

    private static async Task<int> ReadConfigVersionAsync(MySqlConnection connection, MySqlTransaction transaction, Guid tenantId, Guid documentId, CancellationToken cancellationToken)
    { await using var command = new MySqlCommand("SELECT config_version FROM knowledge_documents WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@document);", connection, transaction); command.Parameters.AddWithValue("@tenant", tenantId.ToString()); command.Parameters.AddWithValue("@document", documentId.ToString()); return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture); }
    private static async Task<(Guid Id, string Status)?> ReadRetryVersionAsync(MySqlConnection connection, MySqlTransaction transaction, Guid tenantId, Guid documentId, CancellationToken cancellationToken)
    { await using var command = new MySqlCommand("SELECT BIN_TO_UUID(v.id),v.status FROM knowledge_document_versions v WHERE v.tenant_id=UUID_TO_BIN(@tenant) AND v.document_id=UUID_TO_BIN(@document) ORDER BY v.version_number DESC LIMIT 1;", connection, transaction); command.Parameters.AddWithValue("@tenant", tenantId.ToString()); command.Parameters.AddWithValue("@document", documentId.ToString()); await using var reader = await command.ExecuteReaderAsync(cancellationToken); return await reader.ReadAsync(cancellationToken) ? (Guid.Parse(reader.GetString(0)), reader.GetString(1)) : null; }

    private static async Task<bool> IsImageVersionAsync(MySqlConnection connection, MySqlTransaction transaction,
        KnowledgeIngestJobLease job, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(
            "SELECT content_kind FROM knowledge_document_versions WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@version) LIMIT 1;",
            connection, transaction);
        command.Parameters.AddWithValue("@tenant", job.TenantId.ToString());
        command.Parameters.AddWithValue("@version", job.VersionId.ToString());
        return string.Equals(await command.ExecuteScalarAsync(cancellationToken) as string, "image", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task EnsureLeaseAsync(MySqlConnection connection, MySqlTransaction transaction,
        KnowledgeIngestJobLease job, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand("""
            SELECT status, lease_owner, lease_until
            FROM knowledge_ingest_jobs
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@job)
            FOR UPDATE;
            """, connection, transaction);
        command.Parameters.AddWithValue("@tenant", job.TenantId.ToString());
        command.Parameters.AddWithValue("@job", job.JobId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)
            || !string.Equals(reader.GetString(0), "running", StringComparison.Ordinal)
            || !string.Equals(reader.IsDBNull(1) ? null : reader.GetString(1), job.LeaseOwner, StringComparison.Ordinal)
            || reader.IsDBNull(2) || reader.GetDateTime(2) <= DateTime.UtcNow)
            throw Error("KNOWLEDGE_STALE_LEASE", "资料摄取租约已失效，请重试任务。", 409);
    }

    private static async Task CopyWithLimitAsync(Stream source, Stream target, long maxBytes, CancellationToken cancellationToken)
    { var buffer = new byte[128 * 1024]; long total = 0; int read; while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0) { total += read; if (total > maxBytes) throw Error("KNOWLEDGE_FILE_TOO_LARGE", "单个文件不能超过 100 MB。", 413); await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken); } }
    private static async Task ExecuteAsync(MySqlConnection connection, MySqlTransaction? transaction, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    { await using var command = new MySqlCommand(sql, connection, transaction); foreach (var (name,value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value); await command.ExecuteNonQueryAsync(cancellationToken); }
    private static CollectorStoreException Error(string code, string message, int status) => new(code, message, status);
    private static string NormalizeExtension(string extension) => string.IsNullOrWhiteSpace(extension) ? string.Empty : (extension.StartsWith('.') ? extension : "." + extension).ToLowerInvariant();
    private static string NormalizeFileName(string value) { var name = Path.GetFileName(value).Trim(); if (name.Length is < 1 or > 512 || name is "." or "..") throw Error("INVALID_FILE_NAME", "文件名无效。",400); return name; }
    private static string NormalizeTitle(string? value, string fallback) { var title = string.IsNullOrWhiteSpace(value) ? Path.GetFileNameWithoutExtension(fallback) : value.Trim(); if (title.Length is < 1 or > MaxTitleLength) throw Error("INVALID_TITLE", "资料标题长度必须为 1–500。",400); return title; }
    private static string NormalizeFolderName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw Error("INVALID_FOLDER_NAME", "文件夹名称不能为空。", 400);
        var name = value.Trim();
        if (name.Length > MaxFolderNameLength) throw Error("INVALID_FOLDER_NAME", "文件夹名称长度必须为 1–200。", 400);
        return name;
    }
    private static string NormalizeShort(string? value, string fallback, int max) { var text = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim(); if (text.Length is < 1 || text.Length > max) throw Error("INVALID_KNOWLEDGE_METADATA", "资料元数据长度无效。",400); return text; }
    private static string? NormalizeNullable(string? value, int max) { if (string.IsNullOrWhiteSpace(value)) return null; var text = value.Trim(); if (text.Length > max) throw Error("INVALID_KNOWLEDGE_METADATA", "资料元数据长度无效。",400); return text; }
    private static IReadOnlyList<string> NormalizeTags(IReadOnlyList<string>? values)
    {
        var tags = (values ?? []).Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim().Normalize(NormalizationForm.FormKC).ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToArray();
        if (tags.Length > 50 || tags.Any(item => item.Length > 128))
            throw Error("INVALID_KNOWLEDGE_METADATA", "资料标签最多 50 个且每个不超过 128 个字符。", 400);
        return tags;
    }
    private static void ValidateDates(DateTimeOffset? effective, DateTimeOffset? expires) { if (effective is not null && expires is not null && expires <= effective) throw Error("INVALID_KNOWLEDGE_DATES", "失效时间必须晚于生效时间。",400); }
    private static object? ToUtc(DateTimeOffset? value) => value?.UtcDateTime;
    private static DateTimeOffset? ToOffset(DateTime? value) => value is null ? null : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));
    private sealed record KnowledgeCursor(DateTimeOffset UpdatedAt, Guid Id, string ContextHash);

    internal static string ComputeListCursorContextHash(Guid tenantId, Guid? siteId, KnowledgeDocumentListFilter filter)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            tenantId,
            siteId,
            filter.ScopeMode,
            filter.Status,
            filter.Tag,
            filter.DocumentType
        }))).ToLowerInvariant();

    internal static string EncodeCursor(DateTime timestamp, Guid id, string contextHash)
        => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new KnowledgeCursor(
            new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)), id, contextHash)));

    internal static (DateTime? Timestamp, Guid? Id) ParseCursor(string? cursor, string expectedContextHash)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return (null, null);
        try
        {
            var decoded = JsonSerializer.Deserialize<KnowledgeCursor>(Convert.FromBase64String(cursor));
            if (decoded is not null && decoded.Id != Guid.Empty
                && string.Equals(decoded.ContextHash, expectedContextHash, StringComparison.Ordinal))
                return (decoded.UpdatedAt.UtcDateTime, decoded.Id);
        }
        catch (FormatException) { }
        catch (JsonException) { }
        throw Error("INVALID_CURSOR", "游标无效或与当前资料范围及筛选条件不匹配。", 400);
    }
    private static DateTimeOffset? ReadNullableUtc(MySqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : ToOffset(reader.GetDateTime(ordinal));
    private static string NormalizeContentType(string? value, string extension) => extension switch { ".pdf" => "application/pdf", ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document", ".md" or ".markdown" => "text/markdown", ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", _ => "text/plain" };
    private static void ValidateMagic(byte[] bytes, string extension)
    {
        if (extension == ".pdf" && (bytes.Length < 5 || Encoding.ASCII.GetString(bytes, 0, 5) != "%PDF-"))
            throw Error("KNOWLEDGE_INVALID_FILE", "文件内容与 PDF 扩展名不匹配。", 400);
        if (extension == ".docx" && (bytes.Length < 2 || bytes[0] != (byte)'P' || bytes[1] != (byte)'K'))
            throw Error("KNOWLEDGE_INVALID_FILE", "文件内容与 DOCX 扩展名不匹配。", 400);
        if (extension is ".txt" or ".md" or ".markdown" && bytes.Contains((byte)0))
            throw Error("KNOWLEDGE_INVALID_FILE", "文本文件包含无效二进制内容。", 400);
        if (extension is ".jpg" or ".jpeg" && (bytes.Length < 3 || bytes[0] != 0xFF || bytes[1] != 0xD8 || bytes[2] != 0xFF))
            throw Error("KNOWLEDGE_INVALID_FILE", "文件内容与 JPG 扩展名不匹配。", 400);
        if (extension == ".png" && (bytes.Length < 8 || bytes[0] != 0x89 || bytes[1] != 0x50 || bytes[2] != 0x4E || bytes[3] != 0x47
            || bytes[4] != 0x0D || bytes[5] != 0x0A || bytes[6] != 0x1A || bytes[7] != 0x0A))
            throw Error("KNOWLEDGE_INVALID_FILE", "文件内容与 PNG 扩展名不匹配。", 400);
    }

    private static void ValidateEmbeddingMetadata(IEmbeddingProvider provider)
    {
        if (string.IsNullOrWhiteSpace(provider.Profile) || provider.Profile.Length > 128
            || string.IsNullOrWhiteSpace(provider.Model) || provider.Model.Length > 256
            || provider.Dimensions is < 1 or > 8192)
            throw Error("KNOWLEDGE_INDEX_FAILED", "Embedding 配置无效，无法保存索引元数据。", 503);
    }

    private static Guid StableGuid(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(hash.AsSpan(0, 16));
    }
}
