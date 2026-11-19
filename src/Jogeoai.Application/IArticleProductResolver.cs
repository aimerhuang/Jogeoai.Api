using Jogeoai.Domain;

namespace Jogeoai.Application;

/// <summary>
/// 文章生成「指定产品」的解析边界：选项来自品牌知识库「产品/服务」维度条目；
/// 解析时先找条目，找不到再回退到未迁移的已入库旧产品（只为迁移前已冻结的素材预览兜底）。
/// </summary>
public interface IArticleProductResolver
{
    /// <summary>列出可指定的产品：「产品/服务」维度、未删除且已有检索投影文档的条目，产品标识即条目标识。</summary>
    /// <param name="tenantId">租户。</param>
    /// <param name="siteId">品牌。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyList<KnowledgeProductOption>> ListOptionsAsync(Guid tenantId, Guid siteId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 按产品键解析指定产品：先查「产品/服务」条目；查不到时回退到同品牌、已入库且未被 043 迁移接管的旧产品。
    /// 条目已删除、尚无检索投影文档、旧产品未入库或已被迁移、不属于该租户与品牌时返回 null（调用方映射为 PRODUCT_NOT_FOUND）。
    /// </summary>
    /// <param name="tenantId">租户。</param>
    /// <param name="siteId">品牌。</param>
    /// <param name="productKey">产品键：条目标识；迁移条目沿用原产品标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<ArticleProductRef?> ResolveAsync(Guid tenantId, Guid siteId, Guid productKey,
        CancellationToken cancellationToken = default);
}
