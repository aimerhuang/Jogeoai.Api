using System.Text.Json;
using System.Text.Json.Nodes;
using Jogeoai.Domain;

namespace Jogeoai.Infrastructure;

/// <summary>文章专用补充材料快照；只保存允许的品牌信息和产品证据索引。</summary>
internal static class ArticleWritingMaterialsSnapshot
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal sealed record BrandContext(string Name, string? Description, DateTimeOffset UpdatedAt);
    internal sealed record ProductContext(Guid ProductId, string Name, Guid KnowledgeDocumentId,
        IReadOnlyList<Guid> VersionIds, IReadOnlyList<string> EvidenceIds);
    internal sealed record Materials(int SchemaVersion, BrandContext? Brand, ProductContext? Product);

    /// <summary>
    /// 冻结品牌资料与指定产品的证据索引。<paramref name="product"/> 由 <see cref="Application.IArticleProductResolver"/> 解析
    /// （条目或未迁移的已入库旧产品）；证据按「文档 + 产品键」与素材预览匹配，迁移条目沿用原产品标识与文档，旧快照仍可解析。
    /// </summary>
    internal static Materials Freeze(Guid tenantId, Guid siteId, BrandView? brand,
        ArticleProductRef? product, KnowledgeContextResponse context)
    {
        if (brand is not null && brand.SiteId != siteId)
            throw new CollectorStoreException("SELECTION_STALE", "品牌资料已变化，请重新预览。", 409);
        ProductContext? selected = null;
        if (product is not null)
        {
            if (product.TenantId != tenantId || product.SiteId != siteId)
                throw new CollectorStoreException("PRODUCT_NOT_FOUND", "指定产品不存在或已删除。", 404);
            var evidence = context.Items.Where(item => item.DocumentId == product.KnowledgeDocumentId
                && Guid.TryParse(item.ProductKey, out var productId) && productId == product.Id).ToArray();
            if (product.KnowledgeDocumentId is null || evidence.Length == 0)
                throw new CollectorStoreException("KNOWLEDGE_CONTEXT_STALE", "指定产品的资料已变化，请重新预览。", 409);
            // Facts come from the material preview's frozen document versions, never from
            // mutable product fields that could have changed since the preview.
            selected = new(product.Id, Limit(product.Name, 200)!, product.KnowledgeDocumentId.Value,
                evidence.Select(item => item.VersionId).Distinct().ToArray(),
                evidence.Select(item => item.EvidenceId).Distinct(StringComparer.Ordinal).ToArray());
        }
        return new(1, brand is null ? null : new BrandContext(Limit(brand.Name, 200)!, Limit(brand.Description, 2000), brand.UpdatedAt), selected);
    }

    internal static string Attach(string? selectionSnapshot, Materials materials)
    {
        JsonObject root;
        try { root = string.IsNullOrWhiteSpace(selectionSnapshot) ? new JsonObject() : JsonNode.Parse(selectionSnapshot) as JsonObject ?? throw new JsonException(); }
        catch (JsonException) { throw new CollectorStoreException("KNOWLEDGE_CONTEXT_STALE", "素材选择快照无效，请重新预览。", 409); }
        root["writingMaterials"] = JsonSerializer.SerializeToNode(materials, JsonOptions);
        return root.ToJsonString(JsonOptions);
    }

    internal static Materials? Read(string? snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot)) return null;
        try
        {
            using var document = JsonDocument.Parse(snapshot);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("writingMaterials", out var value)) return null;
            var materials = value.Deserialize<Materials>(JsonOptions);
            if (materials is null || materials.SchemaVersion != 1) throw new JsonException();
            return materials;
        }
        catch (JsonException)
        { throw new CollectorStoreException("ARTICLE_GENERATION_INPUT_INVALID", "文章补充资料快照无效。", 422); }
    }

    private static string? Limit(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, max)];
}
