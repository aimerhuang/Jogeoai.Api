using Jogeoai.Domain;

namespace Jogeoai.Application;

/// <summary>文章链接的自动发现与每日监控计费边界。</summary>
public interface IArticleLinkMonitoringStore
{
    /// <summary>列出需要处理的品牌（启用状态的 Site）。</summary>
    Task<IReadOnlyList<(Guid TenantId, Guid SiteId)>> ListSitesAsync(CancellationToken cancellationToken = default);

    /// <summary>按到达水位扫描已发布分析中归属品牌自有域名的引用，为未登记的 URL 自动建文并开启追踪。</summary>
    Task<ArticleDiscoveryPassResult> DiscoverAsync(Guid tenantId, Guid siteId, DateTimeOffset utcNow,
        CancellationToken cancellationToken = default);

    /// <summary>为尚未读取标题的自动发现链接读取页面标题；返回处理条数。</summary>
    Task<int> ReadPendingTitlesAsync(Guid tenantId, Guid siteId, int limit, CancellationToken cancellationToken = default);

    /// <summary>为当天尚未开轮且存在可计费链接的品牌开轮并预占积分；余额不足写跳过轮次。返回新开轮次数。siteId 非空时只处理该品牌。</summary>
    Task<int> OpenRoundsAsync(DateTimeOffset utcNow, Guid? siteId = null, CancellationToken cancellationToken = default);

    /// <summary>结算本地自然日已结束的预占轮次。返回结算轮次数。siteId 非空时只处理该品牌。</summary>
    Task<int> SettleRoundsAsync(DateTimeOffset utcNow, Guid? siteId = null, CancellationToken cancellationToken = default);

    /// <summary>读取品牌的计费摘要：单价、可计费链接数、今日轮次与最近轮次。</summary>
    Task<ArticleMonitoringBillingView> BillingAsync(Guid tenantId, Guid siteId, DateTimeOffset utcNow,
        CancellationToken cancellationToken = default);
}
