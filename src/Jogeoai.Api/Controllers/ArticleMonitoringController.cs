using Jogeoai.Api;
using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Jogeoai.Api.Controllers;

/// <summary>文章发布链接对应的引用趋势、域名排行和回答证据。</summary>
[ApiController]
[Route("api/v1/sites/{siteId:guid}/article-monitoring")]
public sealed class ArticleMonitoringController(IArticleMonitoringQuery query, IArticleLinkMonitoringStore linkMonitoring,
    TenantContextResolver tenantContext) : ContentControllerBase(tenantContext)
{
    /// <summary>读取文章链接追踪的计费摘要：单价、当前可计费链接数、下一轮预计消耗、可用积分、今日轮次与最近 7 轮。</summary>
    /// <remarks>
    /// 每条追踪中的有效发布链接按品牌本地自然日计费一次（10 积分）；开轮时预占，次日结算；余额不足时当天跳过（skipCode=CREDIT_INSUFFICIENT）并次日重试。
    /// 当天新开启追踪的链接从次日起计费。viewer 角色返回 403 CONTENT_FORBIDDEN。
    /// </remarks>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    [HttpGet("billing")]
    [ProducesResponseType(typeof(ArticleMonitoringBillingView), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status404NotFound)]
    public Task<IActionResult> Billing(Guid siteId, CancellationToken cancellationToken)
        => Execute(async () => { var user = RequireWriter(); return Ok(await linkMonitoring.BillingAsync(user.TenantId, siteId, DateTimeOffset.UtcNow, cancellationToken)); });

    /// <summary>读取文章引用汇总；不把零样本渲染成已验证的零引用。</summary>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="articleId">可选文章标识。</param>
    /// <param name="from">引用观测时间起点。</param>
    /// <param name="to">引用观测时间终点（不含）。</param>
    /// <param name="snapshotToken">可选分析快照令牌。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(ArticleMonitoringSummaryView), StatusCodes.Status200OK)]
    public Task<IActionResult> Summary(Guid siteId, [FromQuery] Guid? articleId, [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to, [FromQuery] string? snapshotToken, CancellationToken cancellationToken)
        => Execute(async () => { var user = RequireUser(); var end = to ?? DateTimeOffset.UtcNow; var start = from ?? end.AddDays(-30); return Ok(await query.SummaryAsync(user.TenantId, siteId, articleId, start, end, snapshotToken, cancellationToken)); });

    /// <summary>分页读取引用回答证据。</summary>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="articleId">可选文章标识。</param>
    /// <param name="from">引用观测时间起点。</param>
    /// <param name="to">引用观测时间终点（不含）。</param>
    /// <param name="cursor">证据分页游标。</param>
    /// <param name="limit">每页数量。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    [HttpGet("evidence")]
    [ProducesResponseType(typeof(CursorPage<ArticleMonitoringEvidenceView>), StatusCodes.Status200OK)]
    public Task<IActionResult> Evidence(Guid siteId, [FromQuery] Guid? articleId, [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to, [FromQuery] string? cursor, [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
        => Execute(async () => { var user = RequireUser(); var end = to ?? DateTimeOffset.UtcNow; var start = from ?? end.AddDays(-30); return Ok(await query.EvidenceAsync(user.TenantId, siteId, articleId, start, end, cursor, limit, cancellationToken)); });

    /// <summary>读取全品牌引用域名排行，并标注每个域名下本系统已登记的有效发布链接（发文标记）。</summary>
    /// <remarks>
    /// 母集与引用次数口径同品牌引用域名汇总（analytics/citation-domain-summary）：合格回答、按回答与规范化 URL 去重、占位域名排除。
    /// 未提供 snapshotToken 时按 from/to/timeZone/engine 冻结新快照（默认品牌时区最近 30 个自然日）；提供时以快照冻结的范围为准并忽略这些参数。
    /// 证据下钻使用返回的 meta.snapshotToken 调用 analytics/citation-domain-summary/evidence。快照过期返回 409 SNAPSHOT_EXPIRED。
    /// 登记计数为 0 只说明本系统未登记，不代表品牌从未在该域名发文。
    /// </remarks>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="from">引用观测时间起点（含）；默认品牌时区最近 30 个自然日的起点。</param>
    /// <param name="to">引用观测时间终点（不含）；默认当前时间。</param>
    /// <param name="timeZone">按日统计的 IANA 时区；默认品牌时区。</param>
    /// <param name="engine">大模型引擎筛选；可重复或用逗号分隔，省略时为全部引擎。</param>
    /// <param name="snapshotToken">已有分析快照令牌；翻页与「查看全部」必须沿用首页返回的令牌。</param>
    /// <param name="q">域名搜索词（字面量、不区分大小写，最多 200 字符）。</param>
    /// <param name="cursor">绑定快照与搜索条件的分页游标。</param>
    /// <param name="limit">每页数量，默认 6，范围 1–100。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    [HttpGet("domain-rankings")]
    [ProducesResponseType(typeof(ArticleDomainRankingPage), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> DomainRankings(Guid siteId, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
        [FromQuery] string? timeZone, [FromQuery] string[]? engine, [FromQuery] string? snapshotToken,
        [FromQuery] string? q, [FromQuery] string? cursor, [FromQuery] int limit = 6,
        CancellationToken cancellationToken = default)
        => Execute(async () =>
        {
            var user = RequireUser();
            var engines = engine?
                .SelectMany(value => (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .ToArray();
            var request = new AnalyticsFilterRequest(From: from, To: to,
                TimeZone: string.IsNullOrWhiteSpace(timeZone) ? null : timeZone.Trim(),
                Engines: engines is { Length: > 0 } ? engines : null);
            return Ok(await query.DomainRankingsAsync(user.TenantId, siteId, request, snapshotToken, q, cursor, limit,
                cancellationToken));
        });

    /// <summary>读取内容详情的推荐渠道：全品牌近 30 天 AI 回答引用最多的媒体平台（名称、图标域名、前往链接与引用次数）。</summary>
    /// <remarks>
    /// 每次请求冻结新的引用域名快照（默认品牌时区最近 30 个自然日，可按 engine 筛选），口径与 domain-rankings 相同；
    /// 只返回域名类型字典（citation_domain_types，迁移 034）中类型为媒体平台、且不属于当前品牌自有的域名，
    /// 品牌官网、电商B2B、其他和字典未收录的域名都不返回。同一平台的多个域名（如 iesdouyin.com 与 douyin.com、
    /// zhuanlan.zhihu.com 与 zhihu.com）合并为一行；有发布入口时返回发布入口，否则为站点首页。没有合格回答时返回空列表。
    /// 未执行迁移 034 的库上本接口返回 500。
    /// </remarks>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="engine">大模型引擎筛选；可重复或用逗号分隔，省略时为全部引擎。</param>
    /// <param name="limit">返回数量，默认 5，范围 1–20。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    [HttpGet("recommended-channels")]
    [ProducesResponseType(typeof(ArticleRecommendedChannelList), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> RecommendedChannels(Guid siteId, [FromQuery] string[]? engine,
        [FromQuery] int limit = 5, CancellationToken cancellationToken = default)
        => Execute(async () =>
        {
            var user = RequireUser();
            var engines = engine?
                .SelectMany(value => (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .ToArray();
            var request = new AnalyticsFilterRequest(Engines: engines is { Length: > 0 } ? engines : null);
            return Ok(await query.RecommendedChannelsAsync(user.TenantId, siteId, request, limit, cancellationToken));
        });
}
