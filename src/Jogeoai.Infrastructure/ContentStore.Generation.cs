using System.Globalization;
using System.Text;
using System.Text.Json;
using Jogeoai.Application;
using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

public sealed partial class ContentStore
{
    /// <inheritdoc />
    public async Task<ArticleGenerationOptionsView> GetGenerationOptionsAsync(Guid tenantId, Guid siteId,
        CancellationToken cancellationToken = default)
    {
        await using (var siteConnection = database.CreateConnection())
        {
            await siteConnection.OpenAsync(cancellationToken);
            await AssertSiteAsync(siteConnection, tenantId, siteId, cancellationToken);
        }
        var targets = await management.GetSiteTargetsAsync(tenantId, siteId, cancellationToken);
        var prompts = await management.ListPromptsAsync(tenantId, siteId, null, null, null, 100, cancellationToken);
        var tags = await management.ListTagsAsync(tenantId, siteId, null, 100, cancellationToken);
        var account = await credits.GetAccountAsync(tenantId, cancellationToken);
        var settings = await research.ListProviderSettingsAsync(cancellationToken);
        var provider = settings.FirstOrDefault(item => item.Enabled && item.CredentialConfigured
            && string.Equals(item.Provider, ArticleGenerationProvider.ProviderName, StringComparison.OrdinalIgnoreCase));
        var folders = await knowledge.ListFoldersAsync(tenantId, siteId, cancellationToken);
        var docsPage = await knowledge.ListDocumentsAsync(tenantId, siteId, null, 100,
            new KnowledgeDocumentListFilter(KnowledgeScopeMode.Default), cancellationToken);
        var docs = new ArticleMaterialCatalogView(folders, docsPage.Items.Where(d => d.CurrentVersion?.Status == "ready" && d.Status == "ready").ToArray(),
            docsPage.Items.SelectMany(d => d.Tags).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            docsPage.Items.Select(d => d.DocumentType).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            docsPage.Items.Select(d => d.ProductKey).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            docsPage.Items.Select(d => d.Language).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(), ["default", "brand_only"], 20, 24, 24000);
        // 「指定产品」列出品牌知识库「产品/服务」条目（未删除且已有检索投影文档）；DTO 形状不变。
        var approvedProducts = products is null ? [] : await products.ListOptionsAsync(tenantId, siteId, cancellationToken);
        // 文章生成不提供 App 口径：可选的目标平台不含 App 目标。
        return new ArticleGenerationOptionsView(targets.Items.Where(target => !MonitoringProviders.IsApp(target.Provider)).ToArray(), prompts.Items.Where(p => p.Enabled && p.ArchivedAt is null).ToArray(), tags.Items,
            ["zh-CN", "en-US"], ArticleWritingRules.Strategies, ArticleWritingRules.LengthProfiles,
            ["professional", "plain_explainer", "purchase_decision"],
            provider is not null, provider is null ? null : ArticleGenerationProvider.ModelName, provider?.UpdatedAt, account, docs, approvedProducts);
    }

    /// <inheritdoc />
    public async Task<ArticleMaterialPreviewView> SaveMaterialPreviewAsync(Guid tenantId, Guid siteId, Guid userId,
        ArticleMaterialPreviewView preview, CancellationToken cancellationToken = default)
    {
        var requestJson = JsonSerializer.Serialize(new { preview.Selection, preview.WithoutRag, preview.MaterialNotes, preview.PromptIds, preview.Language, preview.Topic }, JsonOptions);
        var responseJson = JsonSerializer.Serialize(preview);
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        await AssertSiteAsync(connection, tenantId, siteId, cancellationToken);
        await ExecAsync(connection, null, "INSERT INTO article_material_previews (id,tenant_id,site_id,created_by,request_hash,selection_json,response_json,context_id,context_snapshot_hash,without_rag,expires_at,created_at) VALUES (UUID_TO_BIN(@id),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@user),@hash,@selection,@response,UUID_TO_BIN(@context),@context_hash,@without_rag,@expires,UTC_TIMESTAMP(6));", cancellationToken,
            ("@id", preview.MaterialPreviewId.ToString()), ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@user", userId.ToString()),
            ("@hash", Hash(requestJson)), ("@selection", requestJson), ("@response", responseJson), ("@context", preview.ContextId?.ToString()), ("@context_hash", preview.ContextSnapshotHash), ("@without_rag", preview.WithoutRag), ("@expires", preview.ExpiresAt.UtcDateTime));
        return preview;
    }

    /// <inheritdoc />
    public async Task<ArticleMaterialPreviewView?> GetMaterialPreviewAsync(Guid tenantId, Guid siteId, Guid previewId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        var row = await RowAsync(connection, null, "SELECT response_json,expires_at FROM article_material_previews WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND id=UUID_TO_BIN(@id);", cancellationToken,
            ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@id", previewId.ToString()));
        if (row is null) return null;
        if (ParseDate(row[1]) <= DateTimeOffset.UtcNow) throw new CollectorStoreException("ARTICLE_PREVIEW_EXPIRED", "素材预览已过期，请重新检索。", 409);
        return JsonSerializer.Deserialize<ArticleMaterialPreviewView>(row[0], JsonOptions);
    }

    /// <inheritdoc />
    public async Task<ArticleGenerationPreviewView> SaveGenerationPreviewAsync(Guid tenantId, Guid siteId, Guid userId,
        ArticleGenerationPreviewRequest request, ArticleGenerationPreviewView preview,
        CancellationToken cancellationToken = default)
    {
        ValidateGenerationRequest(request);
        var requestJson = JsonSerializer.Serialize(request, JsonOptions);
        string? referenceSnapshot = null;
        var referenceUrls = request.ReferenceUrls ?? [];
        if (referenceUrls.Count > 5)
            throw new CollectorStoreException("REFERENCE_LIMIT", "参考链接最多 5 个。", 400);
        var distinctUrls = referenceUrls.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (string.Equals(request.Strategy, "reference_structure", StringComparison.OrdinalIgnoreCase)
            && distinctUrls.Length is < 1 or > 5)
            throw new CollectorStoreException("REFERENCE_REQUIRED", "参考范文策略需要 1–5 个参考链接。", 400);
        if (distinctUrls.Length > 0)
        {
            var references = new List<ReferenceSnapshotItem>();
            foreach (var url in distinctUrls)
            {
                var reference = await sourceReader.ReadAsync(url, cancellationToken);
                if (!reference.ReadStatus.Equals("ready", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(reference.BodyMarkdown))
                    throw new CollectorStoreException("REFERENCE_NOT_READABLE", "参考范文无法读取正文，请替换链接或移除该策略。", 422);
                // Keep only fields consumed by the worker and budget the JSON
                // snapshot in bytes.  A character cap alone can exceed the
                // 131 KiB column check for CJK text or redirect metadata.
                references.Add(new(reference.UrlNormalized, reference.ReadStatus, reference.Title, reference.BodyMarkdown));
            }
            referenceSnapshot = SerializeReferenceSnapshot(references);
        }
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        await AssertSiteAsync(connection, tenantId, siteId, cancellationToken);
        Guid? contextId = null; string? contextHash = null;
        if (request.MaterialPreviewId is not null)
        {
            var material = await RowAsync(connection, null, "SELECT response_json,expires_at FROM article_material_previews WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND id=UUID_TO_BIN(@id);", cancellationToken,
                ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@id", request.MaterialPreviewId.Value.ToString()));
            if (material is null) throw new CollectorStoreException("KNOWLEDGE_CONTEXT_STALE", "文章参考素材预览不存在。", 409);
            if (ParseDate(material[1]) <= DateTimeOffset.UtcNow) throw new CollectorStoreException("KNOWLEDGE_CONTEXT_STALE", "文章参考素材预览已过期。", 409);
            var materialView = JsonSerializer.Deserialize<ArticleMaterialPreviewView>(material[0], JsonOptions)
                ?? throw new CollectorStoreException("KNOWLEDGE_CONTEXT_STALE", "文章参考素材预览无效。", 409);
            if (materialView.WithoutRag != request.WithoutRag
                || materialView.PromptIds is { Count: > 0 } && !materialView.PromptIds.Order().SequenceEqual(request.PromptIds.Order())
                || materialView.Language is not null && !string.Equals(materialView.Language, request.Language, StringComparison.OrdinalIgnoreCase))
                throw new CollectorStoreException("KNOWLEDGE_CONTEXT_STALE", "文章参考素材选择已发生变化，请重新预览。", 409);
            contextId = materialView.ContextId; contextHash = materialView.ContextSnapshotHash;
        }
        // uq_article_generation_batches_request allows one batch per request hash.  Without a
        // knowledge-base preview the hash of an identical request never changes, so once its
        // batch was submitted every later regeneration would be rejected as a duplicate, while
        // the PRD treats regeneration as a new order.  Move to the first round hash no batch has
        // consumed.  It is deterministic, so concurrent identical previews still share one hash
        // and the submit-time duplicate guard keeps blocking double submission.
        var roundHash = await FirstUnconsumedRequestHashAsync(connection, tenantId, preview.RequestHash, cancellationToken);
        if (!string.Equals(roundHash, preview.RequestHash, StringComparison.Ordinal))
            preview = preview with { RequestHash = roundHash };
        // request_hash is the preview idempotency boundary.  A retry must return
        // the row that was actually written; returning the caller's newly
        // generated id after an ON DUPLICATE KEY update would create a preview
        // handle that does not exist in the database.
        var existing = await RowAsync(connection, null,
            "SELECT BIN_TO_UUID(id),response_json,expires_at FROM article_generation_previews WHERE tenant_id=UUID_TO_BIN(@tenant) AND request_hash=@hash LIMIT 1;",
            cancellationToken, ("@tenant", tenantId.ToString()), ("@hash", preview.RequestHash));
        if (existing is not null && ParseDate(existing[2]) > DateTimeOffset.UtcNow)
        {
            var stored = JsonSerializer.Deserialize<ArticleGenerationPreviewView>(existing[1], JsonOptions);
            if (stored is not null) return stored;
        }
        if (existing is not null && Guid.TryParse(existing[0], out var existingId))
            preview = preview with { PreviewId = existingId };
        // Serialize after the id/hash adjustments above: a stored response must name the row
        // that actually exists, otherwise a later reuse returns an id that submit cannot find.
        var responseJson = JsonSerializer.Serialize(preview, JsonOptions);
        await ExecAsync(connection, null, "INSERT INTO article_generation_previews (id,tenant_id,site_id,created_by,request_hash,request_json,response_json,material_preview_id,context_id,context_snapshot_hash,reference_snapshot,expires_at,created_at) VALUES (UUID_TO_BIN(@id),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@user),@hash,@request,@response,UUID_TO_BIN(@material),UUID_TO_BIN(@context),@context_hash,@references,@expires,UTC_TIMESTAMP(6)) ON DUPLICATE KEY UPDATE response_json=VALUES(response_json),request_json=VALUES(request_json),material_preview_id=VALUES(material_preview_id),context_id=VALUES(context_id),context_snapshot_hash=VALUES(context_snapshot_hash),reference_snapshot=VALUES(reference_snapshot),expires_at=VALUES(expires_at);", cancellationToken,
            ("@id", preview.PreviewId.ToString()), ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@user", userId.ToString()),
            ("@hash", preview.RequestHash), ("@request", requestJson), ("@response", responseJson), ("@material", preview.MaterialPreviewId?.ToString()), ("@context", contextId?.ToString()), ("@context_hash", contextHash), ("@references", referenceSnapshot), ("@expires", preview.ExpiresAt.UtcDateTime));
        return preview;
    }

    /// <inheritdoc />
    public async Task<ArticleGenerationPreviewRecord?> GetGenerationPreviewAsync(Guid tenantId, Guid siteId, Guid previewId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        var row = await RowAsync(connection, null, "SELECT request_json,response_json,request_hash,expires_at,BIN_TO_UUID(created_by),BIN_TO_UUID(material_preview_id),BIN_TO_UUID(context_id),context_snapshot_hash,reference_snapshot FROM article_generation_previews WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND id=UUID_TO_BIN(@id);", cancellationToken,
            ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@id", previewId.ToString()));
        if (row is null) return null;
        var expires = ParseDate(row[3]); if (expires <= DateTimeOffset.UtcNow) throw new CollectorStoreException("ARTICLE_PREVIEW_EXPIRED", "生成预览已过期，请重新预览。", 409);
        var request = JsonSerializer.Deserialize<ArticleGenerationPreviewRequest>(row[0], JsonOptions) ?? throw new CollectorStoreException("ARTICLE_PREVIEW_INVALID", "生成预览无效。", 409);
        var preview = JsonSerializer.Deserialize<ArticleGenerationPreviewView>(row[1], JsonOptions) ?? throw new CollectorStoreException("ARTICLE_PREVIEW_INVALID", "生成预览无效。", 409);
        string? selectionSnapshot = null;
        string? contextSnapshot = null;
        if (request.MaterialPreviewId is not null)
        {
            var material = await RowAsync(connection, null,
                "SELECT selection_json,response_json FROM article_material_previews WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND id=UUID_TO_BIN(@material);",
                cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()),
                ("@material", request.MaterialPreviewId.Value.ToString()));
            if (material is null) throw new CollectorStoreException("KNOWLEDGE_CONTEXT_STALE", "文章参考素材预览不存在。", 409);
            selectionSnapshot = material[0];
            contextSnapshot = material[1];
        }
        return new ArticleGenerationPreviewRecord(preview, request, expires, row[2], tenantId, siteId, Guid.Parse(row[4]), request.PromptIds,
            request.TargetIds, request.MaterialPreviewId, ParseGuid(row[6]), Null(row[7]), selectionSnapshot, Null(row[8]));
    }

    /// <inheritdoc />
    public async Task<ArticleGenerationAccepted> SubmitGenerationAsync(Guid tenantId, Guid siteId, Guid userId,
        ArticleGenerationSubmitRequest request, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey)) throw new CollectorStoreException("IDEMPOTENCY_KEY_REQUIRED", "请提供 Idempotency-Key。", 400);
        if (idempotencyKey.Length > 191) throw new CollectorStoreException("INVALID_IDEMPOTENCY_KEY", "幂等键无效。", 400);

        // A replay must be resolved before reading the (possibly expired)
        // preview that was used by the original accepted request.
        await using (var idempotencyConnection = database.CreateConnection())
        {
            await idempotencyConnection.OpenAsync(cancellationToken);
            var accepted = await RowAsync(idempotencyConnection, null,
                "SELECT BIN_TO_UUID(id),status,total_tasks,reserved_credits,request_hash FROM article_generation_batches WHERE tenant_id=UUID_TO_BIN(@tenant) AND idempotency_key=@key LIMIT 1;",
                cancellationToken, ("@tenant", tenantId.ToString()), ("@key", idempotencyKey));
            if (accepted is not null)
            {
                if (!string.Equals(accepted[4], request.RequestHash, StringComparison.Ordinal))
                    throw new CollectorStoreException("IDEMPOTENCY_CONFLICT", "幂等键已用于不同生成请求。", 409);
                return new ArticleGenerationAccepted(Guid.Parse(accepted[0]), ParseBatchStatus(accepted[1]),
                    int.Parse(accepted[2], CultureInfo.InvariantCulture), decimal.Parse(accepted[3], CultureInfo.InvariantCulture),
                    $"/api/v1/sites/{siteId}/article-generation/{accepted[0]}");
            }
            var sameRequest = await RowAsync(idempotencyConnection, null,
                "SELECT BIN_TO_UUID(id),idempotency_key FROM article_generation_batches WHERE tenant_id=UUID_TO_BIN(@tenant) AND request_hash=@hash LIMIT 1;",
                cancellationToken, ("@tenant", tenantId.ToString()), ("@hash", request.RequestHash));
            if (sameRequest is not null && !string.Equals(sameRequest[1], idempotencyKey, StringComparison.Ordinal))
                throw new CollectorStoreException("IDEMPOTENCY_CONFLICT", "相同生成请求已经提交，请继续使用原幂等键查询。", 409);
        }
        var preview = await GetGenerationPreviewAsync(tenantId, siteId, request.PreviewId, cancellationToken)
            ?? throw new CollectorStoreException("RESOURCE_NOT_FOUND", "生成预览不存在。", 404);
        if (!string.Equals(preview.RequestHash, request.RequestHash, StringComparison.Ordinal)) throw new CollectorStoreException("IDEMPOTENCY_CONFLICT", "生成预览请求摘要不匹配。", 409);
        ArticleMaterialPreviewView? materialPreview = null;
        KnowledgeContextResponse? frozenContext = null;
        if (preview.MaterialPreviewId is not null)
        {
            materialPreview = await GetMaterialPreviewAsync(tenantId, siteId, preview.MaterialPreviewId.Value, cancellationToken)
                ?? throw new CollectorStoreException("KNOWLEDGE_CONTEXT_STALE", "文章参考素材预览不存在。", 409);
            if (materialPreview.WithoutRag != preview.Request.WithoutRag
                || (preview.ContextId is not null && materialPreview.ContextId != preview.ContextId)
                || (!string.IsNullOrWhiteSpace(preview.ContextSnapshotHash) && materialPreview.ContextSnapshotHash != preview.ContextSnapshotHash))
                throw new CollectorStoreException("KNOWLEDGE_CONTEXT_STALE", "文章参考素材已发生变化，请重新预览。", 409);
            if (!materialPreview.WithoutRag)
            {
                if (materialPreview.ContextId is null)
                    throw new CollectorStoreException("KNOWLEDGE_CONTEXT_STALE", "文章参考素材上下文不存在。", 409);
                frozenContext = await knowledgeRetrieval.GetContextAsync(tenantId, materialPreview.ContextId.Value, cancellationToken)
                    ?? throw new CollectorStoreException("KNOWLEDGE_CONTEXT_STALE", "文章参考素材上下文不存在。", 409);
                var frozenHash = KnowledgeContextSnapshot.Hash(frozenContext);
                if (!string.Equals(frozenHash, materialPreview.ContextSnapshotHash, StringComparison.OrdinalIgnoreCase))
                    throw new CollectorStoreException("KNOWLEDGE_CONTEXT_STALE", "文章参考素材快照校验失败。", 409);
            }
        }
        var count = preview.PromptIds.Distinct().Count();
        const decimal unitPrice = ArticleGenerationPricing.UnitPrice;
        var total = count * unitPrice;
        var targetCount = preview.TargetIds.Distinct().Count();
        if (count is < 1 or > 10 || targetCount is < 1 or > 5 || preview.Preview.ArticleCount != count
            || preview.Preview.UnitPrice != unitPrice || preview.Preview.TotalCredits != total)
            throw new CollectorStoreException("ARTICLE_PREVIEW_INVALID", "生成费用预览已失效，请重新预览。", 409);
        var contextSnapshot = frozenContext is null ? null : KnowledgeContextSnapshot.Serialize(frozenContext);
        var promptSnapshots = new Dictionary<Guid, string>();
        foreach (var promptId in preview.PromptIds.Distinct())
        {
            var prompt = await management.GetPromptAsync(tenantId, siteId, promptId, cancellationToken);
            if (prompt is null || !prompt.Enabled || prompt.ArchivedAt is not null)
                throw new CollectorStoreException("SELECTION_STALE", "主问题配置已变化，请重新预览。", 409);
            promptSnapshots[promptId] = JsonSerializer.Serialize(prompt, JsonOptions);
        }
        var targetCatalog = await management.GetSiteTargetsAsync(tenantId, siteId, cancellationToken);
        // App 目标不是文章生成的可选目标（与 GetGenerationOptionsAsync 一致）；预览里带着它时按配置已变化处理。
        var selectedTargets = targetCatalog.Items.Where(target => target.TargetId is not null && !MonitoringProviders.IsApp(target.Provider)
            && preview.TargetIds.Contains(target.TargetId.Value)).ToArray();
        if (selectedTargets.Length != preview.TargetIds.Distinct().Count() || selectedTargets.Any(target => !target.Enabled))
            throw new CollectorStoreException("SELECTION_STALE", "目标平台配置已变化，请重新预览。", 409);
        var providerConfiguration = await research.GetProviderConfigurationAsync(ArticleGenerationProvider.ProviderName, cancellationToken);
        if (providerConfiguration is null || string.IsNullOrWhiteSpace(providerConfiguration.Credential))
            throw new CollectorStoreException("ARTICLE_PROVIDER_UNAVAILABLE", "文章生成 Provider 未配置。", 503);
        var knowledgeSnapshot = preview.KnowledgeSelectionSnapshot;
        if (!preview.Request.WithoutRag && frozenContext is not null)
        {
            var brand = await management.GetBrandAsync(tenantId, siteId, cancellationToken);
            ArticleProductRef? selectedProduct = null;
            if (Guid.TryParse(materialPreview?.Selection.ProductKey, out var productId))
            {
                selectedProduct = products is null ? null : await products.ResolveAsync(tenantId, siteId, productId, cancellationToken);
                if (selectedProduct is null)
                    throw new CollectorStoreException("PRODUCT_NOT_FOUND", "指定产品不存在或已删除。", 404);
            }
            knowledgeSnapshot = ArticleWritingMaterialsSnapshot.Attach(knowledgeSnapshot,
                ArticleWritingMaterialsSnapshot.Freeze(tenantId, siteId, brand, selectedProduct, frozenContext));
        }
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        await AssertSiteAsync(connection, tenantId, siteId, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var tagIds = (preview.Request.TagIds ?? []).Where(id => id != Guid.Empty).Distinct().ToArray();
        if (tagIds.Length > 10 || (preview.Request.TagIds?.Count ?? 0) != tagIds.Length)
            throw new CollectorStoreException("INVALID_TAGS", "文章标签最多 10 个且不能重复。", 400);
        foreach (var tagId in tagIds)
        {
            var tag = await RowAsync(connection, transaction,
                "SELECT 1 FROM tags WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND id=UUID_TO_BIN(@tag) AND status='active';",
                cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@tag", tagId.ToString()));
            if (tag is null) throw new CollectorStoreException("RESOURCE_NOT_FOUND", "文章标签不存在或已归档。", 404);
        }
        var existing = await RowAsync(connection, transaction, "SELECT BIN_TO_UUID(id),status,total_tasks,reserved_credits,request_hash FROM article_generation_batches WHERE tenant_id=UUID_TO_BIN(@tenant) AND idempotency_key=@key FOR UPDATE;", cancellationToken,
            ("@tenant", tenantId.ToString()), ("@key", idempotencyKey));
        if (existing is not null)
        {
            if (!existing[4].Equals(preview.RequestHash, StringComparison.Ordinal)) throw new CollectorStoreException("IDEMPOTENCY_CONFLICT", "幂等键已用于不同生成请求。", 409);
            await transaction.CommitAsync(cancellationToken);
            return new ArticleGenerationAccepted(Guid.Parse(existing[0]), ParseBatchStatus(existing[1]), int.Parse(existing[2], CultureInfo.InvariantCulture), decimal.Parse(existing[3], CultureInfo.InvariantCulture), $"/api/v1/sites/{siteId}/article-generation/{existing[0]}");
        }
        await ExecAsync(connection, transaction, "INSERT IGNORE INTO credit_accounts (tenant_id,updated_at) VALUES (UUID_TO_BIN(@tenant),UTC_TIMESTAMP(6));", cancellationToken, ("@tenant", tenantId.ToString()));
        var account = await RowAsync(connection, transaction, "SELECT available_credits FROM credit_accounts WHERE tenant_id=UUID_TO_BIN(@tenant) FOR UPDATE;", cancellationToken, ("@tenant", tenantId.ToString()));
        if (account is null || decimal.Parse(account[0], CultureInfo.InvariantCulture) < total) throw new CollectorStoreException("CREDIT_INSUFFICIENT", "积分余额不足。", 402);
        var batchId = Guid.NewGuid(); var snapshot = JsonSerializer.Serialize(preview.Request, JsonOptions);
        await ExecAsync(connection, transaction, """
            INSERT INTO article_generation_batches (id,tenant_id,site_id,requested_by,idempotency_key,request_hash,status,total_tasks,reserved_credits,request_snapshot,preview_expires_at,created_at)
            VALUES (UUID_TO_BIN(@batch),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@user),@key,@hash,'queued',@count,@total,@snapshot,@expires,UTC_TIMESTAMP(6));
            UPDATE credit_accounts SET available_credits=available_credits-@total,reserved_credits=reserved_credits+@total,version=version+1,updated_at=UTC_TIMESTAMP(6) WHERE tenant_id=UUID_TO_BIN(@tenant);
            INSERT INTO credit_ledger (id,tenant_id,entry_type,amount,balance_after,actor_user_id,external_reference,reason,created_at)
            SELECT UUID_TO_BIN(UUID()),tenant_id,'reserve',-@total,available_credits,UUID_TO_BIN(@user),CONCAT('batch-reserve:',@batch),'文章生成预占',UTC_TIMESTAMP(6) FROM credit_accounts WHERE tenant_id=UUID_TO_BIN(@tenant);
            """, cancellationToken, ("@batch", batchId.ToString()), ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@user", userId.ToString()),
            ("@key", idempotencyKey), ("@hash", preview.RequestHash), ("@count", count), ("@total", total), ("@snapshot", snapshot), ("@expires", preview.ExpiresAt.UtcDateTime));
        var perArticle = preview.Preview.UnitPrice;
        foreach (var promptId in preview.PromptIds.Distinct())
        {
            var articleId = Guid.NewGuid(); var taskId = Guid.NewGuid(); var reservationId = Guid.NewGuid();
            var promptJson = promptSnapshots[promptId];
            var title = $"待生成文章 · {promptId:N}";
            try
            {
                var prompt = JsonSerializer.Deserialize<PromptView>(promptJson, JsonOptions);
                if (prompt is not null && !string.IsNullOrWhiteSpace(prompt.Content)) title = prompt.Content.Trim()[..Math.Min(200, prompt.Content.Trim().Length)];
            }
            catch { /* snapshot remains authoritative even if title fallback is used */ }
            await ExecAsync(connection, transaction, "INSERT INTO content_articles (id,tenant_id,site_id,created_by,source_kind,status,title,language,monitor_status,created_at,updated_at) VALUES (UUID_TO_BIN(@article),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@user),'ai_generated','active',@title,@language,'inactive',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6)); INSERT INTO content_monitor_scopes (id,tenant_id,site_id,article_id,mode,filter_snapshot,version,created_at,updated_at) VALUES (UUID_TO_BIN(UUID()),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@article),'all_brand_observations',JSON_OBJECT('activeWindows',JSON_ARRAY()),1,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6)); INSERT INTO article_generation_tasks (id,tenant_id,site_id,batch_id,article_id,prompt_id,prompt_snapshot,target_snapshot,knowledge_selection_snapshot,context_snapshot,reference_snapshot,material_notes,context_id,context_snapshot_hash,language,strategy_key,length_profile,style,audience,keywords_json,extra_requirements,without_rag,status,next_attempt_at,reservation_id,created_at,updated_at) VALUES (UUID_TO_BIN(@task),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@batch),UUID_TO_BIN(@article),UUID_TO_BIN(@prompt),@prompt_snapshot,@target_snapshot,@knowledge,@context_snapshot,@references,@notes,UUID_TO_BIN(@context),@context_hash,@language,@strategy,@length,@style,@audience,@keywords,@extra,@without_rag,'pending',UTC_TIMESTAMP(6),UUID_TO_BIN(@reservation),UTC_TIMESTAMP(6),UTC_TIMESTAMP(6)); INSERT INTO credit_reservations (id,tenant_id,batch_id,task_id,amount,status,idempotency_key,created_at) VALUES (UUID_TO_BIN(@reservation),UUID_TO_BIN(@tenant),UUID_TO_BIN(@batch),UUID_TO_BIN(@task),@amount,'held',CONCAT('task:',@task),UTC_TIMESTAMP(6));", cancellationToken,
                ("@article", articleId.ToString()), ("@title", title), ("@task", taskId.ToString()), ("@reservation", reservationId.ToString()), ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@user", userId.ToString()), ("@batch", batchId.ToString()), ("@prompt", promptId.ToString()), ("@prompt_snapshot", promptJson), ("@target_snapshot", JsonSerializer.Serialize(selectedTargets, JsonOptions)), ("@knowledge", knowledgeSnapshot), ("@context_snapshot", contextSnapshot), ("@references", preview.ReferenceSnapshot), ("@notes", preview.Request.MaterialNotes), ("@context", preview.ContextId?.ToString()), ("@context_hash", preview.ContextSnapshotHash), ("@language", preview.Request.Language), ("@strategy", preview.Request.Strategy), ("@length", preview.Request.LengthProfile), ("@style", preview.Request.Style), ("@audience", preview.Request.Audience), ("@keywords", JsonSerializer.Serialize(preview.Request.Keywords ?? [])), ("@extra", preview.Request.ExtraRequirements), ("@without_rag", preview.Request.WithoutRag), ("@amount", perArticle));
            foreach (var tagId in tagIds)
                await ExecAsync(connection, transaction, "INSERT INTO content_article_tags (tenant_id,site_id,article_id,tag_id,created_at) VALUES (UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@article),UUID_TO_BIN(@tag),UTC_TIMESTAMP(6));", cancellationToken,
                    ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@article", articleId.ToString()), ("@tag", tagId.ToString()));
        }
        await transaction.CommitAsync(cancellationToken);
        return new ArticleGenerationAccepted(batchId, ArticleGenerationBatchStatus.Queued, count, total, $"/api/v1/sites/{siteId}/article-generation/{batchId}");
    }

    /// <inheritdoc />
    public async Task<ArticleGenerationBatchView?> GetGenerationBatchAsync(Guid tenantId, Guid siteId, Guid batchId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        var batch = await RowAsync(connection, null, "SELECT BIN_TO_UUID(id),status,total_tasks,succeeded_tasks,failed_tasks,reserved_credits,captured_credits,created_at,completed_at FROM article_generation_batches WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND id=UUID_TO_BIN(@batch);", cancellationToken,
            ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@batch", batchId.ToString()));
        if (batch is null) return null;
        var tasks = await RowsAsync(connection, null, "SELECT BIN_TO_UUID(t.id),BIN_TO_UUID(t.article_id),BIN_TO_UUID(t.prompt_id),t.status,t.attempt_count,BIN_TO_UUID(v.id),t.last_error_code,t.last_error_message,COALESCE(r.status,'held') FROM article_generation_tasks t LEFT JOIN content_article_versions v ON v.tenant_id=t.tenant_id AND v.article_id=t.article_id AND v.generation_batch_id=t.batch_id LEFT JOIN credit_reservations r ON r.tenant_id=t.tenant_id AND r.id=t.reservation_id WHERE t.tenant_id=UUID_TO_BIN(@tenant) AND t.batch_id=UUID_TO_BIN(@batch) ORDER BY t.created_at;", cancellationToken,
            ("@tenant", tenantId.ToString()), ("@batch", batchId.ToString()));
        return new ArticleGenerationBatchView(Guid.Parse(batch[0]), ParseBatchStatus(batch[1]), int.Parse(batch[2], CultureInfo.InvariantCulture), int.Parse(batch[3], CultureInfo.InvariantCulture), int.Parse(batch[4], CultureInfo.InvariantCulture), decimal.Parse(batch[5], CultureInfo.InvariantCulture), decimal.Parse(batch[6], CultureInfo.InvariantCulture), tasks.Select(ToTask).ToArray(), ParseDate(batch[7]), string.IsNullOrWhiteSpace(batch[8]) ? null : ParseDate(batch[8]));
    }

    /// <inheritdoc />
    public async Task<ArticleGenerationBatchView?> CancelGenerationAsync(Guid tenantId, Guid siteId, Guid userId, Guid batchId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var batch = await RowAsync(connection, transaction,
            "SELECT status FROM article_generation_batches WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND id=UUID_TO_BIN(@batch);",
            cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@batch", batchId.ToString()));
        if (batch is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var pending = await RowsAsync(connection, transaction,
            "SELECT BIN_TO_UUID(id),BIN_TO_UUID(reservation_id) FROM article_generation_tasks WHERE tenant_id=UUID_TO_BIN(@tenant) AND batch_id=UUID_TO_BIN(@batch) AND status='pending' FOR UPDATE;",
            cancellationToken, ("@tenant", tenantId.ToString()), ("@batch", batchId.ToString()));
        decimal released = 0;
        foreach (var row in pending)
        {
            await ExecAsync(connection, transaction,
                "UPDATE article_generation_tasks SET status='cancelled',lease_owner=NULL,lease_until=NULL,updated_at=UTC_TIMESTAMP(6) WHERE tenant_id=UUID_TO_BIN(@tenant) AND batch_id=UUID_TO_BIN(@batch) AND id=UUID_TO_BIN(@task) AND status='pending';",
                cancellationToken, ("@tenant", tenantId.ToString()), ("@batch", batchId.ToString()), ("@task", row[0]));
            if (Guid.TryParse(row[1], out var reservationId))
                released += await ReleaseReservationInTransactionAsync(connection, transaction, tenantId,
                    reservationId, "用户取消生成", cancellationToken);
        }

        var running = await RowAsync(connection, transaction,
            "SELECT COUNT(*) FROM article_generation_tasks WHERE tenant_id=UUID_TO_BIN(@tenant) AND batch_id=UUID_TO_BIN(@batch) AND status='running';",
            cancellationToken, ("@tenant", tenantId.ToString()), ("@batch", batchId.ToString()));
        if (pending.Count > 0)
        {
            var hasRunning = running is not null && int.Parse(running[0], CultureInfo.InvariantCulture) > 0;
            // Claim/complete/fail paths lock a task before they update its
            // batch. Acquire the batch lock only after pending task locks to
            // keep the lock order consistent and avoid a worker deadlock.
            _ = await RowAsync(connection, transaction,
                "SELECT status FROM article_generation_batches WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND id=UUID_TO_BIN(@batch) FOR UPDATE;",
                cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@batch", batchId.ToString()));
            // Cancelled tasks are counted in failed_tasks: completion/failure of a task
            // that was still running finishes the batch by counters alone, so every
            // task must be accounted for there.
            await ExecAsync(connection, transaction,
                "UPDATE article_generation_batches SET reserved_credits=GREATEST(0,reserved_credits-@released),failed_tasks=failed_tasks+@cancelled,status=CASE WHEN @running=1 THEN 'running' ELSE 'cancelled' END,completed_at=CASE WHEN @running=1 THEN NULL ELSE UTC_TIMESTAMP(6) END WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND id=UUID_TO_BIN(@batch) AND status IN ('queued','running');",
                cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@batch", batchId.ToString()),
                ("@released", released), ("@running", hasRunning ? 1 : 0), ("@cancelled", pending.Count));
        }
        await transaction.CommitAsync(cancellationToken);
        return await GetGenerationBatchAsync(tenantId, siteId, batchId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ArticleGenerationWorkItem?> ClaimGenerationTaskAsync(string workerId, TimeSpan lease,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        // Migration 040 adds the studio columns.  Before it is applied the claim keeps reading only the
        // original columns, so the legacy wizard and Worker are unaffected by the deployment order.
        var studioColumns = await ArticleStudioSchema.ExistsAsync(connection, cancellationToken)
            ? ",t.pipeline,t.include_competitors,t.optimization_goal,BIN_TO_UUID(t.prompt_id)"
            : string.Empty;
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var row = await RowAsync(connection, transaction, $"""
            SELECT BIN_TO_UUID(t.id),BIN_TO_UUID(t.batch_id),BIN_TO_UUID(t.article_id),BIN_TO_UUID(t.tenant_id),BIN_TO_UUID(t.site_id),BIN_TO_UUID(b.requested_by),
                   t.prompt_snapshot,t.target_snapshot,t.knowledge_selection_snapshot,t.context_snapshot,t.reference_snapshot,t.material_notes,t.language,t.strategy_key,t.length_profile,t.style,t.audience,t.keywords_json,t.extra_requirements,BIN_TO_UUID(t.context_id),t.context_snapshot_hash,t.without_rag,t.attempt_count,t.reservation_id
                   ,r.amount{studioColumns}
            FROM article_generation_tasks t
            JOIN article_generation_batches b ON b.tenant_id=t.tenant_id AND b.id=t.batch_id
            JOIN credit_reservations r ON r.tenant_id=t.tenant_id AND r.id=t.reservation_id AND r.status='held'
            WHERE b.status IN ('queued','running')
              AND ((t.status='pending' AND t.next_attempt_at<=UTC_TIMESTAMP(6)) OR (t.status='running' AND t.lease_until<UTC_TIMESTAMP(6)))
            ORDER BY t.created_at,t.id LIMIT 1 FOR UPDATE SKIP LOCKED;
            """, cancellationToken);
        if (row is null) { await transaction.CommitAsync(cancellationToken); return null; }
        var attempt = int.Parse(row[22], CultureInfo.InvariantCulture) + 1;
        var until = DateTime.UtcNow.Add(lease);
        await ExecAsync(connection, transaction, "UPDATE article_generation_tasks SET status='running',attempt_count=@attempt,lease_owner=@owner,lease_until=@until,updated_at=UTC_TIMESTAMP(6) WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@id); UPDATE article_generation_batches SET status='running' WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@batch) AND status='queued';", cancellationToken,
            ("@attempt", attempt), ("@owner", workerId), ("@until", until), ("@tenant", row[3]), ("@id", row[0]), ("@batch", row[1]));
        await transaction.CommitAsync(cancellationToken);
        return new ArticleGenerationWorkItem(Guid.Parse(row[0]), Guid.Parse(row[1]), Guid.Parse(row[2]), Guid.Parse(row[3]), Guid.Parse(row[4]), Guid.Parse(row[5]),
            row[6], row[7], Null(row[8]), Null(row[9]), Null(row[10]), Null(row[11]), row[12], row[13], row[14], row[15], Null(row[16]),
            DeserializeStrings(row[17]), Null(row[18]), ParseGuid(row[19]), Null(row[20]), ParseBool(row[21]), attempt, workerId,
            decimal.Parse(row[24], CultureInfo.InvariantCulture),
            row.Length > 25 && row[25] == ArticleStudioLimits.StudioPipeline ? ArticleStudioLimits.StudioPipeline : ArticleStudioLimits.LegacyPipeline,
            row.Length > 26 && ParseBool(row[26]), row.Length > 27 ? ArticleStudioRules.ParseGoal(Null(row[27])) : null,
            row.Length > 28 ? ParseGuid(row[28]) : null);
    }

    /// <inheritdoc />
    public async Task<bool> HeartbeatGenerationTaskAsync(ArticleGenerationWorkItem workItem, TimeSpan lease,
        CancellationToken cancellationToken = default)
    {
        var seconds = Math.Clamp((int)Math.Ceiling(lease.TotalSeconds), 30, 900);
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        var affected = await ExecAsync(connection, null,
            "UPDATE article_generation_tasks SET lease_until=DATE_ADD(UTC_TIMESTAMP(6),INTERVAL @seconds SECOND),updated_at=UTC_TIMESTAMP(6) WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@task) AND status='running' AND lease_owner=@owner AND lease_until>UTC_TIMESTAMP(6);",
            cancellationToken, ("@seconds", seconds), ("@tenant", workItem.TenantId.ToString()),
            ("@task", workItem.TaskId.ToString()), ("@owner", workItem.LeaseOwner));
        return affected > 0;
    }

    /// <inheritdoc />
    public async Task CompleteGenerationTaskAsync(ArticleGenerationWorkItem workItem, ArticleGenerationResult result,
        CancellationToken cancellationToken = default)
    {
        var versionId = Guid.NewGuid();
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var task = await RowAsync(connection, transaction, "SELECT BIN_TO_UUID(reservation_id),status FROM article_generation_tasks WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@task) AND lease_owner=@owner AND lease_until>UTC_TIMESTAMP(6) FOR UPDATE;", cancellationToken,
            ("@tenant", workItem.TenantId.ToString()), ("@task", workItem.TaskId.ToString()), ("@owner", workItem.LeaseOwner));
        if (task is null || task[1] != "running") { await transaction.CommitAsync(cancellationToken); return; }
        if (!Guid.TryParse(task[0], out var reservationId)) { await transaction.RollbackAsync(cancellationToken); return; }
        var reservation = await RowAsync(connection, transaction,
            "SELECT amount,status FROM credit_reservations WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@reservation) FOR UPDATE;",
            cancellationToken, ("@tenant", workItem.TenantId.ToString()), ("@reservation", reservationId.ToString()));
        if (reservation is null || !reservation[1].Equals("held", StringComparison.OrdinalIgnoreCase))
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }
        var amount = decimal.Parse(reservation[0], CultureInfo.InvariantCulture);
        var account = await RowAsync(connection, transaction,
            "SELECT reserved_credits FROM credit_accounts WHERE tenant_id=UUID_TO_BIN(@tenant) FOR UPDATE;",
            cancellationToken, ("@tenant", workItem.TenantId.ToString()));
        if (account is null || decimal.Parse(account[0], CultureInfo.InvariantCulture) < amount)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new CollectorStoreException("CREDIT_RECONCILIATION_REQUIRED", "积分账户与预占流水不一致，请稍后重试。", 503);
        }
        var article = await RowAsync(connection, transaction,
            "SELECT status FROM content_articles WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND id=UUID_TO_BIN(@article) FOR UPDATE;",
            cancellationToken, ("@tenant", workItem.TenantId.ToString()), ("@site", workItem.SiteId.ToString()), ("@article", workItem.ArticleId.ToString()));
        if (article is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }
        var next = (await RowAsync(connection, transaction, "SELECT COALESCE(MAX(version_no),0)+1 FROM content_article_versions WHERE tenant_id=UUID_TO_BIN(@tenant) AND article_id=UUID_TO_BIN(@article);", cancellationToken,
            ("@tenant", workItem.TenantId.ToString()), ("@article", workItem.ArticleId.ToString())))![0];
        await ExecAsync(connection, transaction, """
            INSERT INTO content_article_versions (id,tenant_id,site_id,article_id,version_no,version_kind,title,summary,body_markdown,seo_title,seo_description,keywords_json,content_length,language,generation_batch_id,knowledge_context_id,provider,model,strategy_key,strategy_version,without_rag,evidence_ids_json,uncertainties_json,disclosure,source_hash,created_by,created_at)
            VALUES (UUID_TO_BIN(@version),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@article),@number,'generated',@title,@summary,@body,@seo_title,@seo_description,@keywords,CHAR_LENGTH(@body),@language,UUID_TO_BIN(@batch),UUID_TO_BIN(@context),@provider,@model,@strategy,@strategy_version,@without_rag,@evidence,@uncertainties,@disclosure,@hash,UUID_TO_BIN(@user),UTC_TIMESTAMP(6));
            UPDATE content_articles SET current_version_id=UUID_TO_BIN(@version),title=@title,language=@language,updated_at=UTC_TIMESTAMP(6) WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@article);
            UPDATE article_generation_tasks SET status='succeeded',lease_owner=NULL,lease_until=NULL,last_error_code=NULL,last_error_message=NULL,updated_at=UTC_TIMESTAMP(6) WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@task) AND status='running' AND lease_owner=@owner;
            UPDATE credit_accounts SET reserved_credits=reserved_credits-@amount,lifetime_captured=lifetime_captured+@amount,version=version+1,updated_at=UTC_TIMESTAMP(6) WHERE tenant_id=UUID_TO_BIN(@tenant);
            UPDATE credit_reservations SET status='captured',captured_at=UTC_TIMESTAMP(6) WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@reservation) AND status='held';
            INSERT IGNORE INTO credit_ledger (id,tenant_id,entry_type,amount,balance_after,reservation_id,article_version_id,external_reference,created_at)
            SELECT UUID_TO_BIN(UUID()),tenant_id,'capture',0,available_credits,UUID_TO_BIN(@reservation),UUID_TO_BIN(@version),CONCAT('capture:',@reservation),UTC_TIMESTAMP(6)
            FROM credit_accounts WHERE tenant_id=UUID_TO_BIN(@tenant);
            -- Evaluate the terminal status before incrementing succeeded_tasks.
            -- MySQL evaluates single-table UPDATE assignments left-to-right;
            -- using the incremented value here would count this task twice and
            -- close a batch one task early.  The decision uses only the batch
            -- row's counters (serialized by its row lock) and never reads sibling
            -- task rows: a locking sibling read here deadlocks with cancellation
            -- and with a second worker finishing another task of the batch
            -- (task -> account -> sibling task cycle).  Cancelled tasks are counted
            -- in failed_tasks by CancelGenerationAsync so the counters still add up.
            UPDATE article_generation_batches SET completed_at=CASE WHEN succeeded_tasks+failed_tasks+1>=total_tasks THEN UTC_TIMESTAMP(6) ELSE NULL END,status=CASE WHEN succeeded_tasks+failed_tasks+1>=total_tasks THEN CASE WHEN failed_tasks>0 THEN 'partial' ELSE 'succeeded' END ELSE 'running' END,succeeded_tasks=succeeded_tasks+1,reserved_credits=GREATEST(0,reserved_credits-@amount),captured_credits=captured_credits+@amount WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@batch);
            """, cancellationToken, ("@version", versionId.ToString()), ("@tenant", workItem.TenantId.ToString()), ("@site", workItem.SiteId.ToString()), ("@article", workItem.ArticleId.ToString()), ("@number", int.Parse(next, CultureInfo.InvariantCulture)), ("@title", result.Title), ("@summary", result.Summary), ("@body", result.BodyMarkdown), ("@seo_title", result.SeoTitle), ("@seo_description", result.SeoDescription), ("@keywords", JsonSerializer.Serialize(result.Keywords)), ("@language", workItem.Language), ("@batch", workItem.BatchId.ToString()), ("@context", workItem.ContextId?.ToString()), ("@provider", result.Provider), ("@model", result.Model), ("@strategy", workItem.Strategy), ("@strategy_version", result.StrategyVersion), ("@without_rag", workItem.WithoutRag), ("@evidence", JsonSerializer.Serialize(result.EvidenceIds)), ("@uncertainties", JsonSerializer.Serialize(result.Uncertainties)), ("@disclosure", result.Disclosure), ("@hash", result.ResponseHash), ("@user", workItem.UserId.ToString()), ("@task", workItem.TaskId.ToString()), ("@owner", workItem.LeaseOwner), ("@reservation", reservationId.ToString()), ("@amount", amount));

        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task FailGenerationTaskAsync(ArticleGenerationWorkItem workItem, string errorCode, string message,
        bool retryable, CancellationToken cancellationToken = default)
    {
        var final = !retryable || workItem.AttemptCount >= 3;
        await using var connection = database.CreateConnection(); await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var row = await RowAsync(connection, transaction,
            "SELECT BIN_TO_UUID(t.reservation_id),t.status,t.lease_until,COALESCE(r.amount,0),COALESCE(r.status,'') FROM article_generation_tasks t LEFT JOIN credit_reservations r ON r.tenant_id=t.tenant_id AND r.id=t.reservation_id WHERE t.tenant_id=UUID_TO_BIN(@tenant) AND t.id=UUID_TO_BIN(@task) AND t.lease_owner=@owner AND t.lease_until>UTC_TIMESTAMP(6) FOR UPDATE;",
            cancellationToken, ("@tenant", workItem.TenantId.ToString()), ("@task", workItem.TaskId.ToString()), ("@owner", workItem.LeaseOwner));
        if (row is null || !row[1].Equals("running", StringComparison.OrdinalIgnoreCase))
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        var status = final ? "failed" : "pending";
        var delay = final ? 0 : Math.Min(300, 15 * workItem.AttemptCount);
        var amount = decimal.Parse(row[3], CultureInfo.InvariantCulture);
        var releasedAmount = final && row[4].Equals("held", StringComparison.OrdinalIgnoreCase) ? amount : 0m;
        // Keep the lock order task -> reservation -> account -> batch, matching
        // completion and cancellation paths.  Updating the batch in the same
        // transaction preserves the task/counter/ledger atomicity without
        // acquiring the batch lock before the reservation lock.
        await ExecAsync(connection, transaction,
            "UPDATE article_generation_tasks SET status=@status,next_attempt_at=DATE_ADD(UTC_TIMESTAMP(6),INTERVAL @delay SECOND),lease_owner=NULL,lease_until=NULL,last_error_code=@code,last_error_message=@message,updated_at=UTC_TIMESTAMP(6) WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@task) AND lease_owner=@owner AND status='running';",
            cancellationToken, ("@status", status), ("@delay", delay), ("@code", errorCode),
            ("@message", message.Length > 1000 ? message[..1000] : message), ("@tenant", workItem.TenantId.ToString()),
            ("@task", workItem.TaskId.ToString()), ("@owner", workItem.LeaseOwner));
        if (final && Guid.TryParse(row[0], out var reservation) && row[4].Equals("held", StringComparison.OrdinalIgnoreCase))
        {
            // Keep task state, batch counters, the account cache, reservation
            // state, and the release ledger entry in one transaction.
            await ReleaseReservationInTransactionAsync(connection, transaction, workItem.TenantId,
                reservation, errorCode, cancellationToken);
        }
        // Same counter rule as the completion path: the batch is finished when every
        // task is accounted for.  The previous failed+1>=total check ignored
        // succeeded siblings and left such a batch "running" forever.  Counters only,
        // no sibling task reads (see CompleteGenerationTaskAsync for the deadlock).
        await ExecAsync(connection, transaction,
            "UPDATE article_generation_batches SET status=CASE WHEN @status='failed' AND succeeded_tasks+failed_tasks+1>=total_tasks THEN CASE WHEN succeeded_tasks>0 THEN 'partial' ELSE 'failed' END ELSE status END,completed_at=CASE WHEN @status='failed' AND succeeded_tasks+failed_tasks+1>=total_tasks THEN UTC_TIMESTAMP(6) ELSE completed_at END,failed_tasks=failed_tasks+IF(@status='failed',1,0),reserved_credits=CASE WHEN @status='failed' THEN GREATEST(0,reserved_credits-@released_amount) ELSE reserved_credits END WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@batch);",
            cancellationToken, ("@status", status), ("@tenant", workItem.TenantId.ToString()),
            ("@batch", workItem.BatchId.ToString()), ("@released_amount", releasedAmount));
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>同一请求可被重新生成的轮次上限；超出时要求用户调整写作要求。</summary>
    internal const int MaxRequestRounds = 1000;

    /// <summary>第 round 轮的请求摘要：第 0 轮即原摘要，后续轮次由原摘要确定性派生。</summary>
    internal static string RequestRoundHash(string baseHash, int round)
        => round == 0 ? baseHash : Hash($"{baseHash}:round:{round.ToString(CultureInfo.InvariantCulture)}");

    private static async Task<string> FirstUnconsumedRequestHashAsync(MySqlConnection connection, Guid tenantId,
        string baseHash, CancellationToken cancellationToken)
    {
        for (var round = 0; round < MaxRequestRounds; round++)
        {
            var hash = RequestRoundHash(baseHash, round);
            var consumed = await RowAsync(connection, null,
                "SELECT 1 FROM article_generation_batches WHERE tenant_id=UUID_TO_BIN(@tenant) AND request_hash=@hash LIMIT 1;",
                cancellationToken, ("@tenant", tenantId.ToString()), ("@hash", hash));
            if (consumed is null) return hash;
        }
        throw new CollectorStoreException("ARTICLE_PREVIEW_INVALID", "相同写作要求的生成次数已达上限，请调整写作要求后重试。", 409);
    }

    internal static string SerializeReferenceSnapshot(IReadOnlyList<ReferenceSnapshotItem> references)
    {
        var cap = 12_000;
        while (true)
        {
            var limited = references.Select(reference => reference with
            {
                BodyMarkdown = reference.BodyMarkdown is null
                    ? null
                    : reference.BodyMarkdown.Length <= cap
                        ? reference.BodyMarkdown
                        : reference.BodyMarkdown[..cap]
            }).ToArray();
            var json = JsonSerializer.Serialize(limited, JsonOptions);
            if (Encoding.UTF8.GetByteCount(json) <= 120_000 || cap <= 500)
                return json;
            cap /= 2;
        }
    }

    /// <summary>与 <see cref="SerializeReferenceSnapshot"/> 对称地读取冻结参考快照；空值或无法解析时返回空列表。</summary>
    internal static IReadOnlyList<ReferenceSnapshotItem> DeserializeReferenceSnapshot(string? snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot)) return [];
        try
        {
            return (JsonSerializer.Deserialize<ReferenceSnapshotItem[]>(snapshot, JsonOptions) ?? [])
                .Where(reference => !string.IsNullOrWhiteSpace(reference.UrlNormalized))
                .ToArray();
        }
        catch (JsonException) { return []; }
    }

    internal sealed record ReferenceSnapshotItem(string UrlNormalized, string ReadStatus,
        string? Title, string? BodyMarkdown);
}
