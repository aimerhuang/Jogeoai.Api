using Jogeoai.Application;
using Jogeoai.Domain;

namespace Jogeoai.Infrastructure;

/// <summary>按品牌并行（并发度 3）汇总分析指标、提问、分组、竞品、文章与启用目标。</summary>
public sealed class TeamSummaryQuery(IManagementStore management, IAnalyticsQueryStore analytics,
    IContentStore content, IArticleMonitoringQuery monitoring) : ITeamSummaryQuery
{
    private const int MaxParallelBrands = 3;
    private const int CitedWindowDays = 30;
    private static readonly string[] CaptureModes = ["api", "scraped"];
    private static readonly string[] EvidenceClasses = ["api_response", "observed_surface", "unknown"];

    /// <inheritdoc />
    public async Task<TeamSummaryView> SummaryAsync(Guid tenantId, Guid userId, TeamSummaryRequest request,
        CancellationToken cancellationToken = default)
    {
        var brands = (await management.ListBrandsAsync(tenantId, null, 100, cancellationToken)).Items
            .Where(b => b.Status == "active").OrderBy(b => b.CreatedAt).ThenBy(b => b.SiteId).ToList();
        if (request.SiteId is not null)
        {
            brands = brands.Where(b => b.SiteId == request.SiteId.Value).ToList();
            if (brands.Count == 0) throw new CollectorStoreException("RESOURCE_NOT_FOUND", "工作区不存在。", 404);
        }
        var provider = Normalize(request.Provider);
        if (provider is not null && !AnalyticsQueryStore.DefaultProviders.Contains(provider))
            throw new CollectorStoreException("INVALID_FILTER", "来源筛选不受支持。", 400);
        var engine = Normalize(request.Engine);
        var (zoneId, zone) = ResolveZone(request.TimeZone, brands);
        var range = TeamSummaryProjector.Range(request.From, request.To, zoneId, zone, DateTimeOffset.UtcNow);
        // 其余过滤项固定取品牌概览的默认集合，保证与 /overview 口径一致。
        var filter = new AnalyticsFilterRequest(range.From, range.To, zoneId,
            provider is null ? AnalyticsQueryStore.DefaultProviders : [provider],
            engine is null ? null : [engine], CaptureModes: CaptureModes, EvidenceClasses: EvidenceClasses);
        using var gate = new SemaphoreSlim(MaxParallelBrands);
        var rows = await Task.WhenAll(brands.Select(brand => RowAsync(tenantId, brand, filter, range.To, gate, cancellationToken)));
        return TeamSummaryProjector.View(range, rows);
    }

    private async Task<TeamBrandRow> RowAsync(Guid tenantId, BrandView brand, AnalyticsFilterRequest filter,
        DateTimeOffset to, SemaphoreSlim gate, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            BrandPeriodMetrics? metrics = null;
            string? reason = null;
            try { metrics = await analytics.BrandPeriodMetricsAsync(tenantId, brand.SiteId, filter, cancellationToken); }
            catch (CollectorStoreException exception) { reason = exception.Code; }
            catch (Exception exception) when (exception is not OperationCanceledException) { reason = "ANALYTICS_UNAVAILABLE"; }
            var prompts = await management.SummarizePromptsAsync(tenantId, brand.SiteId, cancellationToken);
            var groups = await management.CountPromptGroupsAsync(tenantId, brand.SiteId, cancellationToken);
            var competitors = (await management.ListCompetitorsAsync(tenantId, brand.SiteId, null, 100, cancellationToken)).Items
                .Count(c => c.Status == "active");
            var articles = await content.ListArticlesAsync(tenantId, brand.SiteId, new ArticleListFilter(Limit: 1), cancellationToken);
            long? cited = null;
            try
            {
                cited = (await monitoring.SummaryAsync(tenantId, brand.SiteId, null, to.AddDays(-CitedWindowDays), to, null,
                    cancellationToken)).CitedArticleCount;
            }
            catch (CollectorStoreException) { /* 引用汇总依赖分析快照；不可用时保持为空。 */ }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { /* 引用汇总的 SQL / 连接故障同样只影响本品牌的被引用计数，不能让整个团队汇总 500。 */ }
            // 同一模型开启快速 / 深度两种模式时是两个站点目标，品牌行仍只列一次该模型（按 Provider + 引擎去重）。
            // 团队汇总不提供 App 口径：指标的来源集合不含 App 样本，模型列表也不列已启用的 App 目标。
            var targets = (await management.GetSiteTargetsAsync(tenantId, brand.SiteId, cancellationToken)).Items
                .Where(t => t.Enabled && !MonitoringProviders.IsApp(t.Provider))
                .Select(t => new TeamBrandTarget(t.Provider, t.Engine, t.DisplayName))
                .DistinctBy(t => (t.Provider, t.Engine)).ToArray();
            return TeamSummaryProjector.Row(brand, metrics, reason, prompts.EnabledCount, groups, competitors,
                TeamSummaryProjector.Articles(articles, cited), targets);
        }
        finally { gate.Release(); }
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    /// <summary>请求时区必须是有效 IANA；否则取最早创建的启用品牌时区，无品牌或无法解析时为 Asia/Shanghai。</summary>
    private static (string Id, TimeZoneInfo Zone) ResolveZone(string? requested, IReadOnlyList<BrandView> brands)
    {
        if (!string.IsNullOrWhiteSpace(requested))
            return (requested.Trim(), CollectionScheduleCalendar.ParseTimeZone(requested.Trim()));
        var candidate = brands.Count == 0 || string.IsNullOrWhiteSpace(brands[0].TimeZone)
            ? TeamSummaryProjector.DefaultTimeZone : brands[0].TimeZone;
        try { return (candidate, TimeZoneInfo.FindSystemTimeZoneById(candidate)); }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        { return (TeamSummaryProjector.DefaultTimeZone, TimeZoneInfo.FindSystemTimeZoneById(TeamSummaryProjector.DefaultTimeZone)); }
    }
}
