using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jogeoai.Application;
using Jogeoai.Domain;

namespace Jogeoai.Infrastructure;

/// <summary>复用知识库可见性和上下文快照实现文章素材选择。</summary>
public sealed class ArticleMaterialSelectionService(
    IKnowledgeStore knowledgeStore,
    IKnowledgeRetrievalService retrieval,
    IContentStore contentStore,
    IManagementStore? management = null,
    IArticleProductResolver? products = null) : IArticleMaterialSelectionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async Task<ArticleMaterialCatalogView> ListCatalogAsync(Guid tenantId, Guid siteId,
        CancellationToken cancellationToken = default)
    {
        var folders = await knowledgeStore.ListFoldersAsync(tenantId, siteId, cancellationToken);
        var documents = await knowledgeStore.ListDocumentsAsync(tenantId, siteId, null, 100,
            new KnowledgeDocumentListFilter(KnowledgeScopeMode.Default), cancellationToken);
        var visible = documents.Items.Where(IsSelectable).Take(100).ToArray();
        return new ArticleMaterialCatalogView(folders, visible,
            visible.SelectMany(d => d.Tags).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            visible.Select(d => d.DocumentType).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            visible.Select(d => d.ProductKey).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            visible.Select(d => d.Language).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            ["default", "brand_only"], 20, 24, 24000);
    }

    /// <inheritdoc />
    public async Task<ArticleMaterialPreviewView> PreviewAsync(Guid tenantId, Guid siteId, Guid userId,
        ArticleMaterialPreviewRequest request, CancellationToken cancellationToken = default)
    {
        if (request.PromptIds is null || request.PromptIds.Count is < 1 or > 10)
            throw new CollectorStoreException("INVALID_PROMPT_SELECTION", "主问题数量必须为 1–10。", 400);
        var promptTexts = await ResolvePromptTextsAsync(tenantId, siteId, request.PromptIds, cancellationToken);
        var selection = NormalizeSelection(request.KnowledgeSelection);
        await AssertProductKeyAsync(tenantId, siteId, selection.ProductKey, cancellationToken);
        var language = string.IsNullOrWhiteSpace(request.Language) ? "zh-CN" : request.Language.Trim();
        if (language is not ("zh-CN" or "en-US"))
            throw new CollectorStoreException("INVALID_LANGUAGE", "文章语言暂只支持 zh-CN 或 en-US。", 400);
        if (request.MaterialNotes?.Length > 2000) throw new CollectorStoreException("INVALID_MATERIAL_NOTES", "文章重点不能超过 2000 字。", 400);
        if (request.WithoutRag)
        {
            var noRag = new ArticleMaterialPreviewView(Guid.NewGuid(), null, Hash(JsonSerializer.Serialize(new { request.PromptIds, promptTexts, language, request.Topic, request.MaterialNotes, withoutRag = true }, JsonOptions)),
                selection, [], [], [], 0, 0,
                DateTimeOffset.UtcNow.AddMinutes(15), true, request.MaterialNotes, request.PromptIds, language, request.Topic);
            return await contentStore.SaveMaterialPreviewAsync(tenantId, siteId, userId, noRag, cancellationToken);
        }
        // Prompt IDs are selection handles only.  Retrieval must use the frozen
        // question text, otherwise an edited prompt can silently change the
        // context produced by this preview.
        var effectiveTopic = string.IsNullOrWhiteSpace(request.Topic) ? null : request.Topic.Trim();
        var retrievalLanguage = selection.SourceLanguage is "all" or "*" ? null : selection.SourceLanguage ?? language;
        var context = await retrieval.CreateContextAsync(tenantId, siteId,
            new KnowledgeContextRequest(effectiveTopic, promptTexts, selection.ScopeMode,
                selection.TagNames, selection.DocumentType, selection.ProductKey, retrievalLanguage,
                Math.Clamp(selection.MaxChunks, 1, 24), Math.Clamp(selection.MaxCharacters, 1000, 24000),
                selection.DocumentIds, selection.FolderIds), cancellationToken);
        // The snapshot goes through a MySQL JSON column, which rewrites some doubles (7.6000000000000005 is read
        // back as 7.6). Submit and the Worker hash the reloaded snapshot, so the preview must hash it too.
        context = await retrieval.GetContextAsync(tenantId, context.ContextId, cancellationToken) ?? context;
        if (context.Items.Count == 0)
            throw new CollectorStoreException("KNOWLEDGE_CONTEXT_EMPTY", "未找到可用资料，请调整素材筛选条件后重试。", 422);
        var allowed = await ListCatalogAsync(tenantId, siteId, cancellationToken);
        var tagLookup = allowed.Documents.ToDictionary(d => d.DocumentId, d => d.Tags);
        var hits = context.Items.Select(item => new ArticleMaterialHit(item.EvidenceId, item.DocumentId, item.VersionId,
            item.Title, item.OriginalFileName, item.Location, item.Scope,
            tagLookup.GetValueOrDefault(item.DocumentId, []), item.Score, item.Text.Length)).ToArray();
        var snapshotHash = KnowledgeContextSnapshot.Hash(context);
        var preview = new ArticleMaterialPreviewView(Guid.NewGuid(), context.ContextId, snapshotHash, selection, hits,
            context.Gaps, context.Conflicts, hits.Length, context.CharacterCount,
            DateTimeOffset.UtcNow.AddMinutes(15), false, request.MaterialNotes, request.PromptIds, language, request.Topic);
        return await contentStore.SaveMaterialPreviewAsync(tenantId, siteId, userId, preview, cancellationToken);
    }

    /// <summary>productKey 为 UUID 时，必须能解析为当前品牌的「产品/服务」条目或未迁移的已入库旧产品；其它自由字符串按历史资料的 productKey 处理。</summary>
    private async Task AssertProductKeyAsync(Guid tenantId, Guid siteId, string? productKey, CancellationToken cancellationToken)
    {
        if (products is null || string.IsNullOrWhiteSpace(productKey) || !Guid.TryParse(productKey, out var productId)) return;
        if (await products.ResolveAsync(tenantId, siteId, productId, cancellationToken) is null)
            throw new CollectorStoreException("PRODUCT_NOT_FOUND", "指定产品不存在或已删除。", 404);
    }

    private static bool IsSelectable(KnowledgeDocumentView document) => document.CurrentVersion is not null
        && document.CurrentVersion.Status.Equals("ready", StringComparison.OrdinalIgnoreCase)
        && document.Status.Equals("ready", StringComparison.OrdinalIgnoreCase);

    private static ArticleKnowledgeSelection NormalizeSelection(ArticleKnowledgeSelection? selection)
    {
        selection ??= new ArticleKnowledgeSelection();
        if (selection.ScopeMode is not (KnowledgeScopeMode.Default or KnowledgeScopeMode.BrandOnly))
            throw new CollectorStoreException("KNOWLEDGE_SELECTION_INVALID", "资料范围无效。", 400);
        var tags = (selection.TagNames ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToArray();
        var documents = (selection.DocumentIds ?? []).Where(id => id != Guid.Empty).Distinct().Take(20).ToArray();
        if (selection.DocumentIds?.Count > 20) throw new CollectorStoreException("KNOWLEDGE_SELECTION_LIMIT", "最多选择 20 份资料。", 400);
        return selection with
        {
            TagNames = tags,
            DocumentIds = documents,
            FolderIds = (selection.FolderIds ?? []).Where(id => id != Guid.Empty).Distinct().Take(50).ToArray(),
            DocumentType = string.IsNullOrWhiteSpace(selection.DocumentType) ? null : selection.DocumentType.Trim(),
            ProductKey = string.IsNullOrWhiteSpace(selection.ProductKey) ? null : selection.ProductKey.Trim(),
            SourceLanguage = string.IsNullOrWhiteSpace(selection.SourceLanguage) ? null : selection.SourceLanguage.Trim(),
            MaxChunks = Math.Clamp(selection.MaxChunks, 1, 24),
            MaxCharacters = Math.Clamp(selection.MaxCharacters, 1000, 24000)
        };
    }

    private async Task<IReadOnlyList<string>> ResolvePromptTextsAsync(Guid tenantId, Guid siteId,
        IReadOnlyList<Guid> promptIds, CancellationToken cancellationToken)
    {
        var ids = promptIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        if (ids.Length != promptIds.Count)
            throw new CollectorStoreException("INVALID_PROMPT_SELECTION", "主问题不能重复或为空。", 400);
        if (management is null)
            return ids.Select(id => id.ToString()).ToArray();
        var prompts = await Task.WhenAll(ids.Select(id => management.GetPromptAsync(tenantId, siteId, id, cancellationToken)));
        if (prompts.Any(prompt => prompt is null || !prompt.Enabled || prompt.ArchivedAt is not null))
            throw new CollectorStoreException("INVALID_PROMPT_SELECTION", "所选主问题不可用。", 400);
        return prompts.Select(prompt => prompt!.Content.Trim()).ToArray();
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
