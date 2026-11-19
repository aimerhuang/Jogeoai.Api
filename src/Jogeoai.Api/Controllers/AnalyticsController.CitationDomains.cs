using Jogeoai.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Jogeoai.Api.Controllers;

public sealed partial class AnalyticsController
{
    private void CitationQueryKeys(params string[] keys)
    {
        var allowed = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in Request.Query.Keys)
            if (!allowed.Contains(key)) throw new CollectorStoreException("INVALID_FILTER", $"不支持的筛选参数：{key}。", 400);
    }

    // 不带 channel 时仍走原有入口，旧调用的参数保持不变；带 channel 时由 Store 校验取值并按所选渠道比较快照。
    private bool HasCitationChannel() => Request.Query.ContainsKey("channel");

    /// <summary>
    /// 引用域名汇总：统一合格回答分母、十一类别和冻结品牌归属。channel 省略或为 api 时是全来源口径（与增加该参数之前相同）；
    /// 为 app 时只统计 App 渠道的回答。同族其余入口须带与摘要快照相同的 channel，否则返回 INVALID_FILTER。
    /// </summary>
    /// <param name="siteId">当前品牌标识。</param><param name="cancellationToken">取消令牌。</param>
    [HttpGet("analytics/citation-domain-summary")]
    [ProducesResponseType(typeof(CitationDomainSummaryView), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> CitationDomainSummary(Guid siteId, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            CitationQueryKeys("from", "to", "timeZone", "engine", "snapshotToken", "channel");
            var request = new AnalyticsFilterRequest(From: ParseTime("from"), To: ParseTime("to"),
                TimeZone: One("timeZone"), Engines: Many("engine"));
            return HasCitationChannel()
                ? await queries.CitationDomainSummaryAsync(Tenant(), siteId, request, One("snapshotToken"), One("channel"), cancellationToken)
                : await queries.CitationDomainSummaryAsync(Tenant(), siteId, request, One("snapshotToken"), cancellationToken);
        });

    /// <summary>域名全量筛选后分页；占比仍基于摘要的完整母集。</summary>
    /// <param name="siteId">当前品牌标识。</param><param name="cancellationToken">取消令牌。</param>
    [HttpGet("analytics/citation-domain-summary/domains")]
    [ProducesResponseType(typeof(CitationDomainSummaryPage), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> CitationDomainSummaryDomains(Guid siteId, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            CitationQueryKeys("snapshotToken", "q", "websiteCategory", "ownership", "cursor", "limit", "channel");
            return HasCitationChannel()
                ? await queries.CitationDomainSummaryDomainsAsync(Tenant(), siteId, One("snapshotToken"),
                    One("q"), One("websiteCategory"), One("ownership"), One("cursor"), Number("limit", 20), One("channel"), cancellationToken)
                : await queries.CitationDomainSummaryDomainsAsync(Tenant(), siteId, One("snapshotToken"),
                    One("q"), One("websiteCategory"), One("ownership"), One("cursor"), Number("limit", 20), cancellationToken);
        });

    /// <summary>按同一快照追溯域名的真实 URL 和贡献回答，保留可证明的原始首位置。</summary>
    /// <param name="siteId">当前品牌标识。</param><param name="cancellationToken">取消令牌。</param>
    [HttpGet("analytics/citation-domain-summary/evidence")]
    [ProducesResponseType(typeof(CitationDomainEvidencePage), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> CitationDomainSummaryEvidence(Guid siteId, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            CitationQueryKeys("snapshotToken", "domain", "cursor", "limit", "channel");
            return HasCitationChannel()
                ? await queries.CitationDomainSummaryEvidenceAsync(Tenant(), siteId, One("snapshotToken"),
                    One("domain") ?? "", One("cursor"), Number("limit", 20), One("channel"), cancellationToken)
                : await queries.CitationDomainSummaryEvidenceAsync(Tenant(), siteId, One("snapshotToken"),
                    One("domain") ?? "", One("cursor"), Number("limit", 20), cancellationToken);
        });

    /// <summary>引用文章（URL）榜：与域名汇总共用同一快照和完整母集，按引用事件降序、URL 摘要升序分页；可按完整域名筛选，并在域名内按平台、标题/URL 搜索和仅我的文章筛选。</summary>
    /// <param name="siteId">当前品牌标识。</param><param name="cancellationToken">取消令牌。</param>
    [HttpGet("analytics/citation-domain-summary/urls")]
    [ProducesResponseType(typeof(CitationUrlSummaryPage), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> CitationDomainSummaryUrls(Guid siteId, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            CitationQueryKeys("snapshotToken", "domain", "engine", "q", "mine", "cursor", "limit", "channel");
            return HasCitationChannel()
                ? await queries.CitationDomainSummaryUrlsAsync(Tenant(), siteId, One("snapshotToken"),
                    One("domain"), One("engine"), One("q"), CitationMine(), One("cursor"), Number("limit", 50), One("channel"), cancellationToken)
                : await queries.CitationDomainSummaryUrlsAsync(Tenant(), siteId, One("snapshotToken"),
                    One("domain"), One("engine"), One("q"), CitationMine(), One("cursor"), Number("limit", 50), cancellationToken);
        });

    /// <summary>单个引用域名详情：与域名榜同一快照的全部/我的引用次数、平台分布和按快照时区逐日补零的引用趋势。</summary>
    /// <param name="siteId">当前品牌标识。</param><param name="cancellationToken">取消令牌。</param>
    [HttpGet("analytics/citation-domain-summary/domain-detail")]
    [ProducesResponseType(typeof(CitationDomainDetailView), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> CitationDomainSummaryDetail(Guid siteId, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            CitationQueryKeys("snapshotToken", "domain", "engine", "channel");
            return HasCitationChannel()
                ? await queries.CitationDomainDetailAsync(Tenant(), siteId, One("snapshotToken"),
                    One("domain"), One("engine"), One("channel"), cancellationToken)
                : await queries.CitationDomainDetailAsync(Tenant(), siteId, One("snapshotToken"),
                    One("domain"), One("engine"), cancellationToken);
        });

    /// <summary>引用文章（URL）数据详情：与引用文章榜同一快照，按提问分组列出引用该 URL 的监测问题及各自引用次数，分组之和等于该行引用次数。</summary>
    /// <param name="siteId">当前品牌标识。</param><param name="cancellationToken">取消令牌。</param>
    [HttpGet("analytics/citation-domain-summary/url-prompts")]
    [ProducesResponseType(typeof(CitationUrlPromptBreakdown), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> CitationDomainSummaryUrlPrompts(Guid siteId, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            CitationQueryKeys("snapshotToken", "urlHash", "engine", "channel");
            return HasCitationChannel()
                ? await queries.CitationUrlPromptsAsync(Tenant(), siteId, One("snapshotToken"),
                    One("urlHash"), One("engine"), One("channel"), cancellationToken)
                : await queries.CitationUrlPromptsAsync(Tenant(), siteId, One("snapshotToken"),
                    One("urlHash"), One("engine"), cancellationToken);
        });

    private bool? CitationMine()
    {
        var value = One("mine");
        if (value is null) return null;
        if (bool.TryParse(value.Trim(), out var mine)) return mine;
        throw new CollectorStoreException("INVALID_FILTER", "mine 只接受 true 或 false。", 400);
    }
}
