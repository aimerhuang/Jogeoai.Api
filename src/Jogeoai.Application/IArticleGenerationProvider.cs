using Jogeoai.Domain;

namespace Jogeoai.Application;

/// <summary>固定 DeepSeek 模型的文章生成 Provider。</summary>
public interface IArticleGenerationProvider
{
    /// <summary>Provider 标识。</summary>
    string Provider { get; }

    /// <summary>执行一次冻结输入的文章生成。</summary>
    Task<ArticleGenerationResult> GenerateAsync(ArticleGenerationWorkItem workItem,
        ResearchProviderConfiguration configuration, IReadOnlyList<KnowledgeEvidenceView> evidence,
        IReadOnlyList<ArticlePublicationLinkView> references,
        CancellationToken cancellationToken = default);

    /// <summary>文章创作阶段 6：在旧输入段之外追加优化目标、结构洞察、竞品名称与不可信来源后生成；未实现的 Provider 视为未配置。</summary>
    Task<ArticleGenerationResult> GenerateStudioAsync(ArticleGenerationWorkItem workItem,
        ResearchProviderConfiguration configuration, IReadOnlyList<KnowledgeEvidenceView> evidence,
        ArticleStudioComposeInput studio, CancellationToken cancellationToken = default)
        => throw new CollectorStoreException("ARTICLE_PROVIDER_UNAVAILABLE", "文章生成 Provider 不支持文章创作。", 503);
}

/// <summary>公开网页或发布链接安全读取边界。</summary>
public interface IArticleSourceReader
{
    /// <summary>读取并抽取公开文章。</summary>
    Task<ArticleSourceReadResult> ReadAsync(string url, CancellationToken cancellationToken = default);
}

/// <summary>网页读取结果。</summary>
public sealed record ArticleSourceReadResult(string UrlOriginal, string UrlNormalized, string UrlHash,
    string ReadStatus, string? Title, string? BodyMarkdown, DateTimeOffset? PublishedAt,
    IReadOnlyList<string> RedirectChain, string? ErrorCode, string? ErrorMessage);

/// <summary>文章监控查询边界。</summary>
public interface IArticleMonitoringQuery
{
    /// <summary>读取文章引用汇总。</summary>
    Task<ArticleMonitoringSummaryView> SummaryAsync(Guid tenantId, Guid siteId, Guid? articleId,
        DateTimeOffset from, DateTimeOffset to, string? snapshotToken,
        CancellationToken cancellationToken = default);

    /// <summary>分页读取文章引用证据。</summary>
    Task<CursorPage<ArticleMonitoringEvidenceView>> EvidenceAsync(Guid tenantId, Guid siteId,
        Guid? articleId, DateTimeOffset from, DateTimeOffset to, string? cursor, int limit,
        CancellationToken cancellationToken = default);

    /// <summary>读取全品牌引用域名排行（品牌引用域名汇总同一快照母集），并附带每个域名下已登记的有效发布链接计数。</summary>
    /// <param name="tenantId">租户标识。</param>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="request">时间窗、时区与模型筛选；仅在未提供快照令牌时用于冻结新快照。</param>
    /// <param name="snapshotToken">已有分析快照令牌；提供时以快照冻结的范围为准。</param>
    /// <param name="q">域名搜索词。</param>
    /// <param name="cursor">绑定快照与搜索条件的分页游标。</param>
    /// <param name="limit">每页数量，1–100。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    Task<ArticleDomainRankingPage> DomainRankingsAsync(Guid tenantId, Guid siteId, AnalyticsFilterRequest request,
        string? snapshotToken, string? q, string? cursor, int limit, CancellationToken cancellationToken = default);

    /// <summary>冻结新的引用域名快照，只保留域名类型字典中的媒体平台（排除自有域名），按平台合并后返回高引用推荐渠道。</summary>
    /// <param name="tenantId">租户标识。</param>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="request">时间窗、时区与模型筛选。</param>
    /// <param name="limit">返回数量，1–20。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    Task<ArticleRecommendedChannelList> RecommendedChannelsAsync(Guid tenantId, Guid siteId,
        AnalyticsFilterRequest request, int limit, CancellationToken cancellationToken = default);
}
