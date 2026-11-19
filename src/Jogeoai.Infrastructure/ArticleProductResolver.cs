using Jogeoai.Application;
using Jogeoai.Domain;

namespace Jogeoai.Infrastructure;

/// <summary>文章生成「指定产品」解析：条目优先，未迁移的已入库旧产品兜底。</summary>
/// <param name="items">品牌知识条目存储。</param>
/// <param name="products">旧产品信息存储，仅用于兜底解析。</param>
public sealed class ArticleProductResolver(IBrandKnowledgeItemStore items, IKnowledgeProductStore products) : IArticleProductResolver
{
    /// <inheritdoc />
    public Task<IReadOnlyList<KnowledgeProductOption>> ListOptionsAsync(Guid tenantId, Guid siteId,
        CancellationToken cancellationToken = default)
        => items.ListProductOptionsAsync(tenantId, siteId, cancellationToken);

    /// <inheritdoc />
    public async Task<ArticleProductRef?> ResolveAsync(Guid tenantId, Guid siteId, Guid productKey,
        CancellationToken cancellationToken = default)
    {
        var item = await items.GetProductRefAsync(tenantId, siteId, productKey, cancellationToken);
        if (item is not null) return item;
        var legacy = await products.GetAsync(tenantId, siteId, productKey, cancellationToken);
        if (legacy is null || legacy.Status != KnowledgeProductStatus.Approved
            || legacy.TenantId != tenantId || legacy.SiteId != siteId) return null;
        // 已被 043 迁移接管的产品只能通过条目解析：条目删除后不能再借旧产品复活。
        if (await products.IsMigratedAsync(tenantId, productKey, cancellationToken)) return null;
        return new ArticleProductRef(legacy.ProductId, legacy.TenantId, legacy.SiteId, legacy.Name, legacy.KnowledgeDocumentId);
    }
}
