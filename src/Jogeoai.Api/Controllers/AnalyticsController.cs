using System.Globalization;
using Jogeoai.Api;
using Jogeoai.Api.Authorization;
using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Jogeoai.Api.Controllers;

/// <summary>基于已发布分析版本查询 Overview、Visibility、声量和证据。</summary>
[ApiController]
[Route("api/v1/sites/{siteId:guid}")]
public sealed partial class AnalyticsController(IAnalyticsQueryStore queries, IAnalysisStore analysis,
    TenantContextResolver tenantContext) : ControllerBase
{
    private Guid Tenant() => tenantContext.Resolve(HttpContext);

    // 写入动作的租户解析：先解析租户，再按 TenantWriteAccess 拒绝只读角色（早于任何 Store 调用）。
    private Guid WriterTenant()
    {
        var tenant = Tenant();
        TenantWriteAccess.Require(HttpContext, tenant);
        return tenant;
    }

    private async Task<IActionResult> Execute(Func<Task<object?>> action, int success = 200)
    {
        try
        {
            var value = await action();
            return value is null ? NotFound(new { error = new { code = "RESOURCE_NOT_FOUND", message = "资源不存在。" } })
                : StatusCode(success, value);
        }
        catch (CollectorStoreException exception)
        { return StatusCode(exception.StatusCode, new { error = new { code = exception.Code, message = exception.Message } }); }
    }

    private AnalyticsFilterRequest Filter(params string[] extra)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "from", "to", "timeZone", "provider", "engine", "country", "locale", "captureMode",
            "evidenceClass", "promptId", "promptVersionId", "tag", "tagMode", "brandedStatus",
            "aggregationMode", "modelVersion", "modelVersionUnavailable", "subjectId", "competitorId", "status", "snapshotToken"
        };
        allowed.UnionWith(extra);
        foreach (var key in Request.Query.Keys)
            if (!allowed.Contains(key)) throw new CollectorStoreException("INVALID_FILTER", $"不支持的筛选参数：{key}。", 400);
        RequireSingleChannelSources();
        return new AnalyticsFilterRequest(ParseTime("from"), ParseTime("to"), One("timeZone"),
            Many("provider"), Many("engine"), Many("country"), Many("locale"),
            Many("captureMode"), Many("evidenceClass"), ManyGuid("promptId"),
            ManyGuid("promptVersionId"), Many("tag"), One("tagMode"), One("brandedStatus"),
            One("aggregationMode"), Many("modelVersion"), OptionalBool("modelVersionUnavailable"),
            MergeIds(ManyGuid("subjectId"), ManyGuid("competitorId")), Many("status"));
    }

    // An explicitly empty captureMode= is an empty list, which the Store reads as "every capture mode". Together with a
    // provider list that is also "every provider" (an explicit empty provider=) or that names the App provider, one request
    // would merge App samples with the other channels into one number. The Store cannot refuse it: article monitoring passes
    // empty lists on purpose. Without the provider key the provider defaults exclude the App source, so captureMode= alone
    // keeps working.
    private void RequireSingleChannelSources()
    {
        if (!Request.Query.ContainsKey("captureMode") || Many("captureMode") is not [] || !Request.Query.ContainsKey("provider"))
            return;
        var providers = Many("provider")!;
        if (providers.Length == 0 || providers.Any(MonitoringProviders.IsApp))
            throw new CollectorStoreException("INVALID_FILTER",
                "captureMode 为空表示全部采集模式，此时 provider 不能为空或包含 app_collector；App 口径请显式传 captureMode=app 与 provider=app_collector。", 400);
    }

    private string? One(string key) => Request.Query.TryGetValue(key, out var values) ? values.FirstOrDefault() : null;
    // 未显式筛选来源时覆盖全部 Provider、采集模式与证据类别；其余旧分析入口仍沿用 Store 的 cloro scraped/observed_surface 默认。
    private AnalyticsFilterRequest AllSourcesFilter(params string[] extra)
    {
        var filter = Filter(extra);
        return filter with
        {
            Providers = Request.Query.ContainsKey("provider") ? filter.Providers : AnalyticsSourceDefaults.Providers,
            CaptureModes = Request.Query.ContainsKey("captureMode") ? filter.CaptureModes : AnalyticsSourceDefaults.CaptureModes,
            EvidenceClasses = Request.Query.ContainsKey("evidenceClass") ? filter.EvidenceClasses
                : AnalyticsSourceDefaults.EvidenceClasses
        };
    }
    private string[]? Many(string key) => Request.Query.TryGetValue(key, out var values)
        ? values.SelectMany(v => (v ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToArray()
        : null;
    private Guid[]? ManyGuid(string key)
    {
        var values = Many(key);
        if (values is null) return null;
        var ids = new List<Guid>();
        foreach (var value in values)
        {
            if (!Guid.TryParse(value, out var id) || id == Guid.Empty)
                throw new CollectorStoreException("INVALID_FILTER", $"{key} 必须是有效 ID。", 400);
            ids.Add(id);
        }
        return ids.ToArray();
    }
    private DateTimeOffset? ParseTime(string key)
    {
        var value = One(key);
        if (value is null) return null;
        if (!(value.EndsWith("Z", StringComparison.OrdinalIgnoreCase)
              || System.Text.RegularExpressions.Regex.IsMatch(value, @"[+-]\d{2}:\d{2}$"))
            || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var result))
            throw new CollectorStoreException("INVALID_TIME_RANGE", $"{key} 必须是带时区的 ISO 时间。", 400);
        return result;
    }
    private int Number(string key, int fallback)
    {
        var value = One(key);
        if (value is null) return fallback;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result))
            throw new CollectorStoreException("INVALID_FILTER", $"{key} 必须是整数。", 400);
        return result;
    }

    private double DecimalRatio(string key, double fallback)
    {
        var value = One(key);
        if (value is null) return fallback;
        if (!double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var result)
            || double.IsNaN(result) || result is < 0 or > 1)
            throw new CollectorStoreException("INVALID_FILTER", $"{key} 必须为 0–1 之间的小数。", 400);
        return result;
    }

    /// <summary>模型监控摘要：检测数、可见率、采集耗时和质量水位。</summary>
    [HttpGet("monitoring/summary")]
    [ProducesResponseType(typeof(MonitoringSummaryView), StatusCodes.Status200OK)]
    public Task<IActionResult> MonitoringSummary(Guid siteId, CancellationToken cancellationToken)
        => Execute(async () => await queries.MonitoringSummaryAsync(Tenant(), siteId,
            AllSourcesFilter(), One("snapshotToken"), cancellationToken));

    /// <summary>模型监控卡片：模型身份、状态、样本和采集耗时。</summary>
    [HttpGet("monitoring/models")]
    [ProducesResponseType(typeof(IReadOnlyList<MonitoringModelView>), StatusCodes.Status200OK)]
    public Task<IActionResult> MonitoringModels(Guid siteId, CancellationToken cancellationToken)
        => Execute(async () => await queries.MonitoringModelsAsync(Tenant(), siteId,
            AllSourcesFilter(), One("snapshotToken"), cancellationToken));

    /// <summary>最近检测记录，历史问题搜索、品牌提及、被引域名、口碑状态、印象词和词极性筛选在分页前执行，按采集时间稳定游标分页；每行附口碑判定。</summary>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpGet("monitoring/records")]
    [ProducesResponseType(typeof(MonitoringRecordPage), StatusCodes.Status200OK)]
    public Task<IActionResult> MonitoringRecords(Guid siteId, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var filter = AllSourcesFilter("cursor", "limit", "search", "mention", "citedDomain", "sentiment", "term",
                "termPolarity");
            // 不带口碑筛选时仍走原有入口，旧调用的参数与游标作用域保持不变；单独的 termPolarity 也走口碑入口，由其返回 400。
            if (One("sentiment") is null && One("term") is null && One("termPolarity") is null)
                return await queries.MonitoringRecordsAsync(Tenant(), siteId, filter, One("snapshotToken"), One("cursor"),
                    Number("limit", 50), One("search"), One("mention"), One("citedDomain"), cancellationToken);
            return await queries.MonitoringRecordsAsync(Tenant(), siteId, filter, One("snapshotToken"), One("cursor"),
                Number("limit", 50), One("search"), One("mention"), One("citedDomain"), One("sentiment"), One("term"),
                One("termPolarity"), cancellationToken);
        });

    /// <summary>查看同品牌、同列表快照下的完整回答、实际搜索词和引用来源；筛选不匹配仍可查看授权记录。</summary>
    /// <param name="siteId">当前品牌工作区标识。</param>
    /// <param name="checkId">检测标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpGet("monitoring/records/{checkId:guid}")]
    [ProducesResponseType(typeof(MonitoringAnswerDetailView), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> MonitoringAnswerDetail(Guid siteId, Guid checkId, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            foreach (var key in Request.Query.Keys)
                if (!key.Equals("snapshotToken", StringComparison.OrdinalIgnoreCase)
                    && !key.Equals("search", StringComparison.OrdinalIgnoreCase)
                    && !key.Equals("mention", StringComparison.OrdinalIgnoreCase)
                    && !key.Equals("citedDomain", StringComparison.OrdinalIgnoreCase)
                    && !key.Equals("sentiment", StringComparison.OrdinalIgnoreCase)
                    && !key.Equals("term", StringComparison.OrdinalIgnoreCase)
                    && !key.Equals("termPolarity", StringComparison.OrdinalIgnoreCase)
                    && !key.Equals("citationProjection", StringComparison.OrdinalIgnoreCase))
                    throw new CollectorStoreException("INVALID_FILTER", $"不支持的筛选参数：{key}。", 400);
            // sentiment / term / termPolarity 只校验格式，便于从口碑筛选后的列表进入详情；不改变详情内容。
            if (One("sentiment") is { } sentimentValue && !string.IsNullOrWhiteSpace(sentimentValue)
                && ReputationRules.ParseState(sentimentValue) is null)
                throw new CollectorStoreException("INVALID_FILTER",
                    "sentiment 仅支持 positive、neutral、negative、unknown、not_mentioned、pending、failed 或 not_applicable。", 400);
            if (One("term") is { } termValue && ReputationRules.NormalizeTerm(termValue).Length > 64)
                throw new CollectorStoreException("INVALID_FILTER", "term 规范化后必须为 1–64 个字符。", 400);
            if (One("termPolarity") is { } polarityValue && !string.IsNullOrWhiteSpace(polarityValue))
            {
                if (ReputationRules.ParsePolarity(polarityValue) is null)
                    throw new CollectorStoreException("INVALID_FILTER", "termPolarity 仅支持 positive、neutral 或 negative。", 400);
                if (string.IsNullOrWhiteSpace(One("term")))
                    throw new CollectorStoreException("INVALID_FILTER", "termPolarity 只能与 term 一起使用。", 400);
            }
            // citedDomain 只校验，便于从按被引域名筛选的列表进入详情；不改变详情内容。
            if (One("citedDomain") is { } cited && !string.IsNullOrWhiteSpace(cited)
                && CitationDomainRules.CanonicalHost(cited.Trim(), alreadyCanonical: true) is null)
                throw new CollectorStoreException("INVALID_FILTER", "citedDomain 必须是有效的规范化主机域名。", 400);
            if (One("citationProjection") is { } projection)
            {
                if (projection != CitationDomainRules.ProjectionVersion)
                    throw new CollectorStoreException("INVALID_FILTER", "不支持的引用归属投影。", 400);
                return await queries.MonitoringAnswerDetailAsync(Tenant(), siteId, checkId,
                    One("snapshotToken"), One("search"), One("mention"), projection, cancellationToken);
            }
            return await queries.MonitoringAnswerDetailAsync(Tenant(), siteId, checkId,
                One("snapshotToken"), One("search"), One("mention"), cancellationToken);
        });

    /// <summary>
    /// 品牌口碑：KPI（健康度 = 正面占比 × 100 + 中性占比 × 50，覆盖率不乘入）、环比、趋势、平台（provider + engine + captureMode）、
    /// 监测问题、问题 × 平台热力图、情感因子（按主要极性分组的印象词，每组前 10）和质量，全部来自同一筛选与同一快照；口碑迁移 038 未部署时返回 503 REPUTATION_SCHEMA_NOT_MIGRATED。
    /// </summary>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpGet("analytics/reputation")]
    [ProducesResponseType(typeof(ReputationSummaryView), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> Reputation(Guid siteId, CancellationToken cancellationToken)
        => Execute(async () => await queries.ReputationAsync(Tenant(), siteId, AllSourcesFilter("view"),
            One("snapshotToken"), One("view"), cancellationToken));

    /// <summary>品牌概览：KPI、趋势、竞品和提问分组使用同一分析快照；分组按提问当前所属的提问分组统计，「未分组」在最后。</summary>
    [HttpGet("analytics/brand-dashboard")]
    [ProducesResponseType(typeof(BrandDashboardView), StatusCodes.Status200OK)]
    public Task<IActionResult> BrandDashboard(Guid siteId, CancellationToken cancellationToken)
        => Execute(async () => await queries.BrandDashboardAsync(Tenant(), siteId,
            AllSourcesFilter(), One("snapshotToken"), cancellationToken));

    /// <summary>品牌概览提问分组（按提问当前所属分组统计，「未分组」在最后），按稳定游标分页。</summary>
    [HttpGet("analytics/brand-dashboard/breakdowns")]
    [ProducesResponseType(typeof(AnalyticsPage<BrandDashboardGroup>), StatusCodes.Status200OK)]
    public Task<IActionResult> BrandDashboardBreakdowns(Guid siteId, CancellationToken cancellationToken)
        => Execute(async () => await queries.BrandDashboardBreakdownsAsync(Tenant(), siteId,
            AllSourcesFilter("cursor", "limit"), One("snapshotToken"), One("cursor"), Number("limit", 50), cancellationToken));

    /// <summary>概览：统一 C/N/M 指标、趋势、引擎分布与前期对比。</summary>
    [HttpGet("analytics/overview")]
    [ProducesResponseType(typeof(OverviewView), StatusCodes.Status200OK)]
    public Task<IActionResult> Overview(Guid siteId, CancellationToken cancellationToken)
        => Execute(async () => await queries.OverviewAsync(Tenant(), siteId, Filter(), One("snapshotToken"), cancellationToken));

    /// <summary>可见性：支持实际观测和显式最近观测。</summary>
    [HttpGet("analytics/visibility")]
    [ProducesResponseType(typeof(VisibilityView), StatusCodes.Status200OK)]
    public Task<IActionResult> Visibility(Guid siteId, CancellationToken cancellationToken)
        => Execute(async () => await queries.VisibilityAsync(Tenant(), siteId, Filter(), One("snapshotToken"), cancellationToken));

    /// <summary>声量卡片、环图、每日趋势及完整主体集合。</summary>
    [HttpGet("analytics/share-of-voice")]
    [ProducesResponseType(typeof(ShareOfVoiceView), StatusCodes.Status200OK)]
    public Task<IActionResult> ShareOfVoice(Guid siteId, CancellationToken cancellationToken)
        => Execute(async () => await queries.ShareOfVoiceAsync(Tenant(), siteId, Filter("topN"),
            One("snapshotToken"), Number("topN", 5), cancellationToken));

    /// <summary>按同一快照分页读取包含零提及竞品的全榜。</summary>
    [HttpGet("analytics/share-of-voice/leaderboard")]
    [ProducesResponseType(typeof(AnalyticsPage<SubjectShareEntry>), StatusCodes.Status200OK)]
    public Task<IActionResult> Leaderboard(Guid siteId, CancellationToken cancellationToken)
        => Execute(async () => await queries.LeaderboardAsync(Tenant(), siteId, Filter("cursor", "limit"),
            One("snapshotToken"), One("cursor"), Number("limit", 50), cancellationToken));

    /// <summary>稳定分页查询 Prompt 的提及率和自有引用率；默认覆盖 cloro、doubao_api、deepseek_api、packy_qwen、packy_glm、packy_kimi、packy_yuanbao、domestic_collector 全部来源，每行对应一个 Prompt 版本与来源（provider|captureMode|evidenceClass）。</summary>
    [HttpGet("analytics/prompts")]
    [ProducesResponseType(typeof(AnalyticsPage<PromptPerformanceView>), StatusCodes.Status200OK)]
    public Task<IActionResult> Prompts(Guid siteId, CancellationToken cancellationToken)
        => Execute(async () => await queries.PromptsAsync(Tenant(), siteId, AllSourcesFilter("cursor", "limit", "sort"),
            One("snapshotToken"), One("cursor"), Number("limit", 50), One("sort"), cancellationToken));

    /// <summary>查看 Prompt 的历史版本、引擎表现、趋势、运行及证据摘要。</summary>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="promptId">Prompt 稳定标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpGet("analytics/prompts/{promptId:guid}")]
    [ProducesResponseType(typeof(PromptDetailView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> PromptDetail(Guid siteId, Guid promptId, CancellationToken cancellationToken)
        => Execute(async () => await queries.PromptDetailAsync(Tenant(), siteId, promptId,
            Filter(), One("snapshotToken"), cancellationToken));

    /// <summary>按主体或 Prompt 下钻贡献 Check，返回真实采集时间。</summary>
    [HttpGet("analytics/evidence")]
    [ProducesResponseType(typeof(AnalyticsPage<AnalyticsEvidenceRow>), StatusCodes.Status200OK)]
    public Task<IActionResult> Evidence(Guid siteId, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            Guid? subjectId = ParseOptionalId("subjectId");
            Guid? promptId = ParseOptionalId("evidencePromptId");
            var queryHash = One("queryHash");
            var urlHash = One("urlHash");
            var domain = One("domain");
            var filter = Filter("cursor", "limit", "subjectId", "evidencePromptId",
                "queryHash", "urlHash", "domain");
            if (subjectId is not null && (queryHash is not null || urlHash is not null || domain is not null)
                || queryHash is not null && (urlHash is not null || domain is not null))
                throw new CollectorStoreException("INVALID_FILTER", "证据筛选维度不能混用。", 400);
            if (queryHash is not null)
                return await queries.FanOutEvidenceAsync(Tenant(), siteId, filter,
                    One("snapshotToken"), One("cursor"), Number("limit", 50),
                    queryHash, promptId, cancellationToken);
            if (urlHash is not null || domain is not null)
                return await queries.CitationEvidenceAsync(Tenant(), siteId, filter,
                    One("snapshotToken"), One("cursor"), Number("limit", 50),
                    urlHash, domain, promptId, cancellationToken);
            return await queries.EvidenceAsync(Tenant(), siteId,
                filter,
                One("snapshotToken"), One("cursor"), Number("limit", 50), subjectId, promptId, cancellationToken);
        });

    /// <summary>按同一筛选和快照查看单个 Prompt 的跨 Run Check 历史。</summary>
    [HttpGet("prompts/{promptId:guid}/checks")]
    [ProducesResponseType(typeof(AnalyticsPage<AnalyticsEvidenceRow>), StatusCodes.Status200OK)]
    public Task<IActionResult> PromptChecks(Guid siteId, Guid promptId, CancellationToken cancellationToken)
        => Execute(async () => await queries.EvidenceAsync(Tenant(), siteId, Filter("cursor", "limit"),
            One("snapshotToken"), One("cursor"), Number("limit", 50), null, promptId, cancellationToken));

    /// <summary>实际搜索词的扩展事件、可用率及引擎比较。</summary>
    [HttpGet("analytics/query-fanout")]
    [ProducesResponseType(typeof(FanOutView), StatusCodes.Status200OK)]
    public Task<IActionResult> FanOut(Guid siteId, CancellationToken cancellationToken)
        => Execute(async () => await queries.FanOutAsync(Tenant(), siteId, Filter(),
            One("snapshotToken"), cancellationToken));

    /// <summary>按事件数列出 Provider 返回的搜索词。</summary>
    [HttpGet("analytics/query-fanout/queries")]
    [ProducesResponseType(typeof(AnalyticsPage<FanOutQueryRow>), StatusCodes.Status200OK)]
    public Task<IActionResult> FanOutQueries(Guid siteId, CancellationToken cancellationToken)
        => Execute(async () => await queries.FanOutQueriesAsync(Tenant(), siteId,
            Filter("cursor", "limit"), One("snapshotToken"), One("cursor"), Number("limit", 50), cancellationToken));

    /// <summary>搜索词的 Prompt 变体、词项和 Check 入口。</summary>
    [HttpGet("analytics/query-fanout/queries/{queryHash}")]
    [ProducesResponseType(typeof(FanOutQueryDetail), StatusCodes.Status200OK)]
    public Task<IActionResult> FanOutQueryDetail(Guid siteId, string queryHash, CancellationToken cancellationToken)
        => Execute(async () => await queries.FanOutQueryDetailAsync(Tenant(), siteId, queryHash,
            Filter(), One("snapshotToken"), cancellationToken));

    /// <summary>中英文搜索词项频次与相对 Prompt 的变化。</summary>
    [HttpGet("analytics/query-fanout/terms")]
    [ProducesResponseType(typeof(FanOutTermsView), StatusCodes.Status200OK)]
    public Task<IActionResult> FanOutTerms(Guid siteId, CancellationToken cancellationToken)
        => Execute(async () => await queries.FanOutTermsAsync(Tenant(), siteId,
            Filter("cursor", "limit"), One("snapshotToken"), One("cursor"), Number("limit", 50), cancellationToken));

    /// <summary>按搜索词或 Prompt 分页下钻贡献 Check。</summary>
    [HttpGet("analytics/query-fanout/evidence")]
    [ProducesResponseType(typeof(AnalyticsPage<AnalyticsEvidenceRow>), StatusCodes.Status200OK)]
    public Task<IActionResult> FanOutEvidence(Guid siteId, CancellationToken cancellationToken)
        => Execute(async () => await queries.FanOutEvidenceAsync(Tenant(), siteId,
            Filter("cursor", "limit", "queryHash", "evidencePromptId"), One("snapshotToken"),
            One("cursor"), Number("limit", 50), One("queryHash"), ParseOptionalId("evidencePromptId"), cancellationToken));

    /// <summary>外部引用事件、分类分布、日趋势与 Google 内部链接数。</summary>
    [HttpGet("analytics/citations")]
    [ProducesResponseType(typeof(CitationsView), StatusCodes.Status200OK)]
    public Task<IActionResult> Citations(Guid siteId, CancellationToken cancellationToken)
        => Execute(async () => await queries.CitationsAsync(Tenant(), siteId, Filter(),
            One("snapshotToken"), cancellationToken));

    /// <summary>外部引用 URL 榜，可按域名筛选。</summary>
    [HttpGet("analytics/citations/urls")]
    [ProducesResponseType(typeof(AnalyticsPage<CitationUrlRow>), StatusCodes.Status200OK)]
    public Task<IActionResult> CitationUrls(Guid siteId, CancellationToken cancellationToken)
        => Execute(async () => await queries.CitationUrlsAsync(Tenant(), siteId,
            Filter("cursor", "limit", "domain"), One("snapshotToken"), One("cursor"), Number("limit", 50),
            One("domain"), cancellationToken));

    /// <summary>外部引用域名榜。</summary>
    [HttpGet("analytics/citations/domains")]
    [ProducesResponseType(typeof(AnalyticsPage<CitationDomainRow>), StatusCodes.Status200OK)]
    public Task<IActionResult> CitationDomains(Guid siteId, CancellationToken cancellationToken)
        => Execute(async () => await queries.CitationDomainsAsync(Tenant(), siteId,
            Filter("cursor", "limit"), One("snapshotToken"), One("cursor"), Number("limit", 50), cancellationToken));

    /// <summary>单个引用 URL 的分类与原始位置证据。</summary>
    [HttpGet("analytics/citations/urls/{urlHash}")]
    [ProducesResponseType(typeof(CitationUrlDetail), StatusCodes.Status200OK)]
    public Task<IActionResult> CitationUrlDetail(Guid siteId, string urlHash, CancellationToken cancellationToken)
        => Execute(async () => await queries.CitationUrlDetailAsync(Tenant(), siteId, urlHash,
            Filter(), One("snapshotToken"), cancellationToken));

    /// <summary>一个引用域名的 URL 和原始位置证据。</summary>
    [HttpGet("analytics/citations/domains/{domain}")]
    [ProducesResponseType(typeof(CitationDomainDetail), StatusCodes.Status200OK)]
    public Task<IActionResult> CitationDomainDetail(Guid siteId, string domain, CancellationToken cancellationToken)
        => Execute(async () => await queries.CitationDomainDetailAsync(Tenant(), siteId, domain,
            Filter(), One("snapshotToken"), cancellationToken));

    /// <summary>与前一等长窗口相比的引用新增、消失、次数和标题变化。</summary>
    [HttpGet("analytics/citations/changes")]
    [ProducesResponseType(typeof(CitationChangesView), StatusCodes.Status200OK)]
    public Task<IActionResult> CitationChanges(Guid siteId, CancellationToken cancellationToken)
        => Execute(async () => await queries.CitationChangesAsync(Tenant(), siteId,
            Filter("minimumEvents", "declineThreshold", "cursor", "limit"), One("snapshotToken"),
            Number("minimumEvents", 2), DecimalRatio("declineThreshold", 0.5),
            One("cursor"), Number("limit", 50), cancellationToken));

    /// <summary>仅引用竞品、没有引用自有域名的 Prompt。</summary>
    [HttpGet("analytics/citations/gaps")]
    [ProducesResponseType(typeof(AnalyticsPage<CompetitorCitationGapRow>), StatusCodes.Status200OK)]
    public Task<IActionResult> CompetitorCitationGaps(Guid siteId, CancellationToken cancellationToken)
        => Execute(async () => await queries.CompetitorCitationGapsAsync(Tenant(), siteId,
            Filter("cursor", "limit"), One("snapshotToken"), One("cursor"), Number("limit", 50), cancellationToken));

    /// <summary>已解析的 Google 内部搜索与购物链接。</summary>
    [HttpGet("analytics/citations/google-internal")]
    [ProducesResponseType(typeof(AnalyticsPage<GoogleInternalRow>), StatusCodes.Status200OK)]
    public Task<IActionResult> GoogleInternal(Guid siteId, CancellationToken cancellationToken)
        => Execute(async () => await queries.GoogleInternalAsync(Tenant(), siteId,
            Filter("cursor", "limit"), One("snapshotToken"), One("cursor"), Number("limit", 50), cancellationToken));

    /// <summary>按 URL、域名或 Prompt 下钻贡献外部引用的 Check。</summary>
    [HttpGet("analytics/citations/evidence")]
    [ProducesResponseType(typeof(AnalyticsPage<AnalyticsEvidenceRow>), StatusCodes.Status200OK)]
    public Task<IActionResult> CitationEvidence(Guid siteId, CancellationToken cancellationToken)
        => Execute(async () => await queries.CitationEvidenceAsync(Tenant(), siteId,
            Filter("cursor", "limit", "urlHash", "domain", "evidencePromptId"), One("snapshotToken"),
            One("cursor"), Number("limit", 50), One("urlHash"), One("domain"),
            ParseOptionalId("evidencePromptId"), cancellationToken));

    /// <summary>显式创建异步重算任务；同幂等键返回原任务。</summary>
    [HttpPost("analysis-jobs")]
    [ProducesResponseType(typeof(AnalysisJobView), StatusCodes.Status202Accepted)]
    public Task<IActionResult> CreateJob(Guid siteId, [FromBody] AnalysisJobRequest request,
        CancellationToken cancellationToken)
        => Execute(async () => await analysis.CreateJobAsync(WriterTenant(), siteId,
            Request.Headers["Idempotency-Key"].FirstOrDefault() ?? "", request, cancellationToken), 202);

    /// <summary>按租户和工作区读取重算进度与待发布版本。</summary>
    [HttpGet("analysis-jobs/{jobId:guid}")]
    [ProducesResponseType(typeof(AnalysisJobView), StatusCodes.Status200OK)]
    public Task<IActionResult> GetJob(Guid siteId, Guid jobId, CancellationToken cancellationToken)
        => Execute(async () => await analysis.GetJobAsync(Tenant(), siteId, jobId, cancellationToken));

    private Guid? ParseOptionalId(string key)
    {
        var value = One(key);
        if (value is null) return null;
        if (!Guid.TryParse(value, out var id) || id == Guid.Empty)
            throw new CollectorStoreException("INVALID_FILTER", $"{key} 必须是有效 ID。", 400);
        return id;
    }

    private bool? OptionalBool(string key)
    {
        var value = One(key);
        if (value is null) return null;
        if (!bool.TryParse(value, out var result))
            throw new CollectorStoreException("INVALID_FILTER", $"{key} 必须是布尔值。", 400);
        return result;
    }

    private static Guid[]? MergeIds(Guid[]? left, Guid[]? right)
    {
        if (left is null && right is null) return null;
        return (left ?? []).Concat(right ?? []).Distinct().ToArray();
    }
}
