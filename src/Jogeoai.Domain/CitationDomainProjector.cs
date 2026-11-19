using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Jogeoai.Domain;

/// <summary>已发布详细分析的一条外部引用事实。</summary>
/// <param name="CheckId">贡献回答。</param>
/// <param name="Url">发布版本规范化 URL。</param>
/// <param name="FirstPosition">已证明的原始首位置。</param>
public sealed record CitationDomainEvent(Guid CheckId, string Url, int FirstPosition);

/// <summary>单次投影产出的全量指标、类别、域名与质量。</summary>
/// <param name="Metrics">完整指标。</param>
/// <param name="Categories">完整类别分布。</param>
/// <param name="Domains">稳定排序的域名行。</param>
/// <param name="Quality">互斥排除计数。</param>
/// <param name="Urls">稳定排序的 URL 行（事件降序、URL 摘要升序），标题由查询层按页补充。</param>
public sealed record CitationDomainProjection(CitationDomainMetrics Metrics,
    IReadOnlyList<CitationCategoryDistribution> Categories, IReadOnlyList<CitationDomainSummaryRow> Domains,
    CitationDomainQuality Quality, IReadOnlyList<CitationUrlSummaryRow> Urls);

/// <summary>新汇总与原引用归属共用的不可变 v1 规则；变更必须新增版本。</summary>
public static class CitationDomainRules
{
    /// <summary>投影版本。</summary>
    public const string ProjectionVersion = "domain-summary-v1";
    /// <summary>域名级网站类别规则版本。</summary>
    public const string CategoryVersion = "website-category-v1";
    /// <summary>包含前导 www 合并的归属版本。</summary>
    public const string OwnershipVersion = "canonical-host-ownership-v1";
    /// <summary>仅有限三个示例根域的排除规则版本。</summary>
    public const string PlaceholderVersion = "placeholder-v1";

    // Host-only rules reviewed from DetailedAnalyticsRules. Child inheritance is
    // explicit; multi-tenant platform children are not classified implicitly.
    private sealed record HostRule(string Host, CitationWebsiteCategory Category, bool Children = false);
    private static readonly HostRule[] HostRules =
    [
        new("wikipedia.org", CitationWebsiteCategory.Reference, true),
        new("britannica.com", CitationWebsiteCategory.Reference),
        new("reddit.com", CitationWebsiteCategory.CommunityForum),
        new("stackoverflow.com", CitationWebsiteCategory.CommunityForum),
        new("facebook.com", CitationWebsiteCategory.SocialMedia),
        new("instagram.com", CitationWebsiteCategory.SocialMedia),
        new("linkedin.com", CitationWebsiteCategory.SocialMedia),
        new("youtube.com", CitationWebsiteCategory.Video),
        new("github.com", CitationWebsiteCategory.Other),
        new("amazon.com", CitationWebsiteCategory.Other),
        new("amazon.co.uk", CitationWebsiteCategory.Other),
        new("amazon.de", CitationWebsiteCategory.Other),
        new("amazon.co.jp", CitationWebsiteCategory.Other),
        new("ebay.com", CitationWebsiteCategory.Other),
        new("ebay.co.uk", CitationWebsiteCategory.Other),
        new("ebay.de", CitationWebsiteCategory.Other),
        new("trustpilot.com", CitationWebsiteCategory.Other),
        new("g2.com", CitationWebsiteCategory.Other),
        new("prnewswire.com", CitationWebsiteCategory.NewsMedia),
        new("businesswire.com", CitationWebsiteCategory.NewsMedia),
        new("reuters.com", CitationWebsiteCategory.NewsMedia),
        new("apnews.com", CitationWebsiteCategory.NewsMedia),
        new("bbc.com", CitationWebsiteCategory.NewsMedia),
        new("nytimes.com", CitationWebsiteCategory.NewsMedia),
        new("theguardian.com", CitationWebsiteCategory.NewsMedia),
        new("techcrunch.com", CitationWebsiteCategory.NewsMedia)
    ];

    /// <summary>统一引用和配置域名的 ASCII 主机键；不合并普通子域名。</summary>
    public static string? CanonicalHost(string? host, bool alreadyCanonical = false)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        try
        {
            var value = new IdnMapping().GetAscii(host.Trim().TrimEnd('.')).ToLowerInvariant();
            if (!alreadyCanonical && value.StartsWith("www.", StringComparison.Ordinal)) value = value[4..];
            if (value.Length is 0 or > 253 || value.Contains(':')
                || Uri.CheckHostName(value) is not (UriHostNameType.Dns or UriHostNameType.IPv4)) return null;
            return value;
        }
        catch (ArgumentException) { return null; }
    }

    /// <summary>只信任无凭据 HTTP(S) URL 中的合法主机。</summary>
    public static string? HostFromUrl(string? url, bool alreadyNormalized = false)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length > 0) return null;
        try { return CanonicalHost(uri.IdnHost, alreadyNormalized); }
        catch (UriFormatException) { return null; }
    }

    /// <summary>有限点分边界占位集合，不按名称包含关系删除真实来源。</summary>
    public static bool IsPlaceholder(string host)
        => new[] { "example.com", "example.net", "example.org" }
            .Any(root => host == root || host.EndsWith('.' + root, StringComparison.Ordinal));

    /// <summary>统一合格回答集合；投影查询先按发布版本校验详细完成与事实完整性。</summary>
    public static bool IsEligible(AnalyticsCheckFact fact, SubjectConfiguration configuration)
        => fact.Status == "succeeded" && fact.AnswerPresent && !fact.ConfigurationMissing
            && configuration.IsComplete && fact.AnalysisStatus == "completed";

    /// <summary>只采用冻结当前品牌域名，主域精确、附加精确、最长子域边界依次优先。</summary>
    public static (CitationOwnership Ownership, CitationOwnershipBasis? Basis, string? Reason) Ownership(
        string? host, SubjectConfiguration configuration, bool alreadyCanonical = false)
    {
        host = CanonicalHost(host, alreadyCanonical);
        if (host is null) return (CitationOwnership.Unknown, null, "INVALID_URL");
        var brand = configuration.Subjects.SingleOrDefault(s => s.Type == "brand" && s.Id == configuration.BrandId);
        if (!configuration.IsComplete || brand is null)
            return (CitationOwnership.Unknown, null, "CONFIGURATION_UNAVAILABLE");
        var terms = configuration.Terms.Where(t => t.SubjectId == brand.Id && t.SubjectType == "brand"
            && t.Kind.Contains("domain", StringComparison.Ordinal)).Select(t => (Term: t, Host: CanonicalHost(t.Key)))
            .ToArray();
        // An older revision without typed domain terms cannot provide a stable basis.
        if (terms.Length == 0 || terms.Any(t => t.Host is null || t.Term.Kind is not ("primary_domain" or "additional_domain")))
            return (CitationOwnership.Unknown, null, "OWNERSHIP_BASIS_UNAVAILABLE");
        var match = terms.Where(t => host == t.Host || configuration.IncludeSubdomains
                && host.EndsWith('.' + t.Host, StringComparison.Ordinal))
            .OrderBy(t => host == t.Host ? t.Term.Kind == "primary_domain" ? 0 : 1 : 2)
            .ThenByDescending(t => t.Host!.Length).ThenBy(t => t.Host, StringComparer.Ordinal).FirstOrDefault();
        return match.Term is null ? (CitationOwnership.NonOwned, null, null)
            : (CitationOwnership.Owned, new CitationOwnershipBasis(brand.Id, brand.Name, match.Host!,
                match.Term.Kind, configuration.IncludeSubdomains, configuration.Version), null);
    }

    /// <summary>每个域名一次分类；主机规则优先，官网声明仅作用于精确主域。</summary>
    public static (CitationWebsiteCategory Category, CitationCategoryBasis Basis) Category(
        string host, SubjectConfiguration configuration)
    {
        var matches = HostRules.Where(r => host == r.Host || r.Children && host.EndsWith('.' + r.Host, StringComparison.Ordinal))
            .OrderByDescending(r => r.Host.Length).ToArray();
        if (matches.Length > 0)
        {
            var mostSpecific = matches.Where(r => r.Host.Length == matches[0].Host.Length).ToArray();
            if (mostSpecific.Select(r => r.Category).Distinct().Count() > 1)
                return (CitationWebsiteCategory.Unclassified, new("conflicting-host-rules", "主机规则存在冲突，保守保留未分类。"));
            return (matches[0].Category, new("host:" + matches[0].Host, "维护的主机类型规则，非人工认证。"));
        }
        if (host.EndsWith(".edu", StringComparison.Ordinal))
            return (CitationWebsiteCategory.Education, new("type:.edu", "教育专用域类型规则。"));
        if (host.EndsWith(".gov", StringComparison.Ordinal))
            return (CitationWebsiteCategory.GovernmentNonprofit, new("type:.gov", "政府专用域类型规则。"));
        if (configuration.IsComplete && configuration.Terms.Any(t => t.SubjectId == configuration.BrandId
            && t.SubjectType == "brand" && t.Kind == "primary_domain" && CanonicalHost(t.Key) == host))
            return (CitationWebsiteCategory.Corporate, new("frozen-primary-domain", "冻结品牌配置明确声明的官方主域。"));
        return (CitationWebsiteCategory.Unclassified, new("insufficient-evidence", "当前冻结规则没有可靠网站类型依据。"));
    }

    /// <summary>逐原始索引产生归属，保留重复 URL 与重复位置。</summary>
    public static IReadOnlyList<CitationAttributionItem> Attribute(IReadOnlyList<AnalysisCitationView> citations,
        SubjectConfiguration configuration, bool analysisAvailable)
        => citations.Select((citation, index) =>
        {
            var host = HostFromUrl(citation.Url);
            if (!analysisAvailable)
                return new CitationAttributionItem(index, host, CitationOwnership.Unknown, null, "ANALYSIS_NOT_READY");
            var result = Ownership(host, configuration, alreadyCanonical: true);
            var declared = CanonicalHost(citation.Domain);
            var reason = result.Reason ?? (citation.Domain is not null && declared != host ? "DECLARED_DOMAIN_MISMATCH" : null);
            return new CitationAttributionItem(index, host, result.Ownership, result.Basis, reason);
        }).ToArray();
}

/// <summary>通过单事件计入判定的一条有效外部引用事件。</summary>
/// <param name="Event">原始已发布事件。</param>
/// <param name="Host">canonical-host-ownership-v1 域名键（与域名榜 domain 相同）。</param>
/// <param name="Fact">贡献该事件的合格回答事实。</param>
public sealed record CitationDomainCountedEvent(CitationDomainEvent Event, string Host, AnalyticsCheckFact Fact);

/// <summary>
/// 单个引用事件是否计入有效外部事件及其域名键的唯一实现：仅合格回答、同一回答内按规范化 URL 去重、
/// 剔除非法 URL、Google 内部链接与有限占位域名。域名榜累加器、域名详情和回答记录的被引域名筛选共用，
/// 保证「全部」口径只有一份。输入必须按回答标识连续分组（与累加器相同的 ORDER BY check_id）。
/// </summary>
public sealed class CitationDomainEventGate
{
    private readonly IReadOnlyDictionary<Guid, AnalyticsCheckFact> eligible;
    private readonly SubjectConfiguration configuration;
    private readonly HashSet<string> currentUrls = new(StringComparer.Ordinal);
    private Guid? currentCheck;

    /// <summary>以合格回答（键为回答标识）和冻结配置创建判定器。</summary>
    /// <param name="eligible">合格回答事实，键为回答标识。</param>
    /// <param name="configuration">冻结品牌配置。</param>
    public CitationDomainEventGate(IReadOnlyDictionary<Guid, AnalyticsCheckFact> eligible, SubjectConfiguration configuration)
    {
        this.eligible = eligible;
        this.configuration = configuration;
    }

    /// <summary>按统一合格规则挑出合格回答，重复回答标识只保留第一条。</summary>
    /// <param name="facts">快照候选回答事实。</param>
    /// <param name="configuration">冻结品牌配置。</param>
    public static Dictionary<Guid, AnalyticsCheckFact> Eligible(IEnumerable<AnalyticsCheckFact> facts,
        SubjectConfiguration configuration)
    {
        var result = new Dictionary<Guid, AnalyticsCheckFact>();
        foreach (var fact in facts)
            if (CitationDomainRules.IsEligible(fact, configuration)) result.TryAdd(fact.CheckId, fact);
        return result;
    }

    /// <summary>判定一个事件；计入时返回含域名键的有效事件，否则返回空。<paramref name="placeholder"/> 表示该事件去重后被有限占位规则排除。</summary>
    /// <param name="item">按回答标识连续分组输入的已发布事件。</param>
    /// <param name="placeholder">输出：去重后是否被有限占位规则排除。</param>
    public CitationDomainCountedEvent? Admit(CitationDomainEvent item, out bool placeholder)
    {
        placeholder = false;
        if (!eligible.TryGetValue(item.CheckId, out var fact)) return null;
        if (currentCheck != item.CheckId) { currentCheck = item.CheckId; currentUrls.Clear(); }
        // citation_events already contains the release-normalized URL. Applying
        // NormalizeUrl again would incorrectly strip a second www prefix.
        var url = item.Url;
        var host = CitationDomainRules.HostFromUrl(url, alreadyNormalized: true);
        if (host is null || !currentUrls.Add(url)) return null;
        if (DetailedAnalyticsRules.GoogleInternal(url, null, item.FirstPosition, configuration) is not null) return null;
        if (CitationDomainRules.IsPlaceholder(host)) { placeholder = true; return null; }
        return new CitationDomainCountedEvent(item, host, fact);
    }
}

/// <summary>
/// 流式事件投影；输入必须按回答分组。内存不随引用事件总数线性增加，
/// 但随不同域名数和不同规范化 URL 数线性增长（有界而非常量内存）。
/// </summary>
public sealed class CitationDomainAccumulator
{
    private sealed class DomainCount { public long Events; public long Answers; public Guid? LastCheck; }
    private sealed class UrlCount(string host) { public string Host { get; } = host; public long Events; public long Answers; public Guid? LastCheck; }
    private readonly SubjectConfiguration configuration;
    private readonly IReadOnlyList<AnalyticsCheckFact> candidates;
    private readonly Dictionary<Guid, AnalyticsCheckFact> eligible;
    private readonly CitationDomainEventGate gate;
    private readonly Action<CitationDomainCountedEvent>? onCounted;
    private readonly Dictionary<string, DomainCount> domains = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UrlCount> urls = new(StringComparer.Ordinal);
    private long placeholders;

    /// <summary>创建同一冻结母集的累加器，零引用合格回答仍进入分母。</summary>
    /// <param name="candidates">快照候选回答。</param>
    /// <param name="configuration">冻结品牌配置。</param>
    /// <param name="urlVersion">发布 URL 规范化版本。</param>
    /// <param name="onCounted">可选：每个计入有效外部事件的事件回调（域名详情、按平台排名复用同一判定）。</param>
    public CitationDomainAccumulator(IReadOnlyList<AnalyticsCheckFact> candidates,
        SubjectConfiguration configuration, string urlVersion, Action<CitationDomainCountedEvent>? onCounted = null)
    {
        if (urlVersion is not (DetailedAnalyticsRules.UrlV1 or DetailedAnalyticsRules.UrlV2))
            throw new ArgumentException("不支持的 URL 规范化版本。", nameof(urlVersion));
        this.candidates = candidates;
        this.configuration = configuration;
        this.onCounted = onCounted;
        eligible = CitationDomainEventGate.Eligible(candidates, configuration);
        gate = new CitationDomainEventGate(eligible, configuration);
    }

    /// <summary>加入按回答标识连续分组的事实；返回有效事件的规范化 URL，否则返回空。</summary>
    public string? Add(CitationDomainEvent item)
    {
        var counted = gate.Admit(item, out var placeholder);
        if (placeholder) placeholders++;
        if (counted is null) return null;
        var url = item.Url;
        var host = counted.Host;
        if (!domains.TryGetValue(host, out var count)) domains[host] = count = new DomainCount();
        count.Events++;
        if (count.LastCheck != item.CheckId) { count.Answers++; count.LastCheck = item.CheckId; }
        // Same (check, url) pair was already rejected by the gate, so each URL event is one answer.
        if (!urls.TryGetValue(url, out var urlCount)) urls[url] = urlCount = new UrlCount(host);
        urlCount.Events++;
        if (urlCount.LastCheck != item.CheckId) { urlCount.Answers++; urlCount.LastCheck = item.CheckId; }
        onCounted?.Invoke(counted);
        return url;
    }

    /// <summary>从同一事件计数生成指标、类别及完整域名榜。</summary>
    public CitationDomainProjection Complete()
    {
        var total = domains.Values.Sum(d => d.Events);
        var rows = domains.Select(pair =>
        {
            var category = CitationDomainRules.Category(pair.Key, configuration);
            var ownership = CitationDomainRules.Ownership(pair.Key, configuration, alreadyCanonical: true);
            return new CitationDomainSummaryRow(pair.Key, category.Category, category.Basis,
                ownership.Ownership, ownership.Basis, pair.Value.Events, CitationDomainRatio.Of(pair.Value.Events, total),
                pair.Value.Answers, CitationDomainRatio.Of(pair.Value.Answers, eligible.Count), 0, null);
        }).OrderByDescending(d => d.CitationEvents).ThenBy(d => d.Domain, StringComparer.Ordinal)
            .Select((row, index) => row with { Rank = index + 1 }).ToArray();
        var urlRows = urls.Select(pair => (Url: pair.Key, Hash: UrlHash(pair.Key), Count: pair.Value))
            .OrderByDescending(u => u.Count.Events).ThenBy(u => u.Hash, StringComparer.Ordinal)
            .Select((u, index) => new CitationUrlSummaryRow(index + 1, u.Url, u.Hash, u.Count.Host, null,
                u.Count.Events, CitationDomainRatio.Of(u.Count.Events, total), u.Count.Answers, false, 0, 0, null)).ToArray();
        var owned = rows.Where(r => r.Ownership == CitationOwnership.Owned).Sum(r => r.CitationEvents);
        var metrics = new CitationDomainMetrics(eligible.Count, total, rows.LongLength, owned,
            rows.Where(r => r.Ownership == CitationOwnership.Unknown).Sum(r => r.CitationEvents), CitationDomainRatio.Of(owned, total),
            urlRows.LongLength);
        var categories = Enum.GetValues<CitationWebsiteCategory>().Select(category =>
        {
            var count = rows.Where(r => r.WebsiteCategory == category).Sum(r => r.CitationEvents);
            return new CitationCategoryDistribution(category, count, CitationDomainRatio.Of(count, total));
        }).ToArray();
        var succeeded = candidates.Where(f => f.Status == "succeeded").ToArray();
        var answered = succeeded.Where(f => f.AnswerPresent).ToArray();
        var configured = answered.Where(f => !f.ConfigurationMissing && configuration.IsComplete).ToArray();
        var quality = new CitationDomainQuality(candidates.Count, candidates.Count - succeeded.Length,
            succeeded.Length - answered.Length, answered.Length - configured.Length,
            configured.LongCount(f => f.AnalysisStatus == "failed"),
            configured.LongCount(f => f.AnalysisStatus is not ("completed" or "failed")),
            placeholders, null, null,
            ["非法 URL 与 Google 内部搜索/购物链接不进入外部事件；历史外部事实不支持重建其精确排除数。",
             "引用证据只返回已证明的 firstPosition，完整重复位置以原回答引用数组为准。",
             "仅统计所选采集样本内实际观测，不代表不同来源的均衡曝光或可比表现。"]);
        return new CitationDomainProjection(metrics, categories, rows, quality, urlRows);
    }

    /// <summary>与详细分析写入端相同的事件键：规范化 URL 的 UTF-8 SHA-256 十六进制小写摘要。</summary>
    public static string UrlHash(string normalizedUrl)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedUrl))).ToLowerInvariant();
}

/// <summary>
/// 按单个平台在完整母集中重新排定 URL 名次；只消费已通过 <see cref="CitationDomainEventGate"/> 的有效事件，
/// 排序与未过滤时一致（事件降序、URL 摘要升序），分母为该平台全部有效事件。
/// </summary>
public sealed class CitationUrlEngineRanking
{
    private readonly string engine;
    private readonly Dictionary<string, (string Host, long Events)> counts = new(StringComparer.Ordinal);
    private long total;

    /// <summary>为一个已规范化（小写）的平台代码创建排名。</summary>
    /// <param name="engine">小写平台代码。</param>
    public CitationUrlEngineRanking(string engine) => this.engine = engine;

    /// <summary>加入一个有效事件；其他平台的事件被忽略。</summary>
    /// <param name="counted">已通过统一判定的有效事件。</param>
    public void Add(CitationDomainCountedEvent counted)
    {
        if (!string.Equals(counted.Fact.Engine, engine, StringComparison.Ordinal)) return;
        total++;
        counts[counted.Event.Url] = counts.TryGetValue(counted.Event.Url, out var current)
            ? (current.Host, current.Events + 1) : (counted.Host, 1);
    }

    /// <summary>生成该平台的完整 URL 行；同一回答内同一 URL 只计一次，因此回答数等于事件数。</summary>
    public IReadOnlyList<CitationUrlSummaryRow> Complete()
        => counts.Select(pair => (Url: pair.Key, Hash: CitationDomainAccumulator.UrlHash(pair.Key), pair.Value.Host, pair.Value.Events))
            .OrderByDescending(u => u.Events).ThenBy(u => u.Hash, StringComparer.Ordinal)
            .Select((u, index) => new CitationUrlSummaryRow(index + 1, u.Url, u.Hash, u.Host, null, u.Events,
                CitationDomainRatio.Of(u.Events, total), u.Events, false, 0, 0, null)).ToArray();
}

/// <summary>域名详情与 URL 行补充统计的纯投影；输入均为已通过 <see cref="CitationDomainEventGate"/> 的有效事件。</summary>
public static class CitationDomainProjector
{
    /// <summary>
    /// 单个域名的总数、平台分布与逐日趋势。「全部」逐事件计数，与域名榜 citationEvents 同一口径；
    /// 「我的」= (回答, URL 摘要) 命中 <paramref name="ownPairs"/>。engines 与总数不受 <paramref name="engine"/> 影响，trend 受其影响。
    /// </summary>
    /// <param name="meta">快照元数据，原样返回。</param>
    /// <param name="domain">规范化域名键。</param>
    /// <param name="engine">可选的小写平台过滤，仅作用于趋势。</param>
    /// <param name="counted">有效事件；域名不同的事件被忽略。</param>
    /// <param name="ownPairs">满足文章监控门槛的我的引用对。</param>
    /// <param name="from">快照窗口开始（含）。</param>
    /// <param name="to">快照窗口结束（不含）。</param>
    /// <param name="zone">快照时区，用于本地日期分桶。</param>
    public static CitationDomainDetailView Detail(CitationDomainMeta meta, string domain, string? engine,
        IEnumerable<CitationDomainCountedEvent> counted, IReadOnlySet<(Guid CheckId, string UrlHash)> ownPairs,
        DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone)
    {
        var first = LocalDate(from, zone);
        var last = LocalDate(to.AddTicks(-1), zone);
        var days = Math.Max(0, last.DayNumber - first.DayNumber + 1);
        var trendAll = new long[days];
        var trendMine = new long[days];
        var engines = new Dictionary<string, (long Events, long Mine)>(StringComparer.Ordinal);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        long total = 0, mine = 0;
        foreach (var item in counted)
        {
            if (!string.Equals(item.Host, domain, StringComparison.Ordinal)) continue;
            var url = item.Event.Url;
            var isMine = false;
            if (ownPairs.Count > 0)
            {
                if (!hashes.TryGetValue(url, out var hash)) hashes[url] = hash = CitationDomainAccumulator.UrlHash(url);
                isMine = ownPairs.Contains((item.Event.CheckId, hash));
            }
            var increment = isMine ? 1 : 0;
            total++;
            mine += increment;
            engines[item.Fact.Engine] = engines.TryGetValue(item.Fact.Engine, out var row)
                ? (row.Events + 1, row.Mine + increment) : (1, increment);
            if (engine is not null && !string.Equals(item.Fact.Engine, engine, StringComparison.Ordinal)) continue;
            if (item.Fact.CapturedAt is not { } captured) continue;
            var index = LocalDate(captured, zone).DayNumber - first.DayNumber;
            if (index < 0 || index >= days) continue;
            trendAll[index]++;
            trendMine[index] += increment;
        }
        var engineRows = engines.Select(pair => new CitationDomainEngineRow(pair.Key, pair.Value.Events, pair.Value.Mine))
            .OrderByDescending(row => row.CitationEvents).ThenBy(row => row.Engine, StringComparer.Ordinal).ToArray();
        var trend = Enumerable.Range(0, days)
            .Select(index => new CitationDomainTrendPoint(first.AddDays(index), trendAll[index], trendMine[index])).ToArray();
        return new CitationDomainDetailView(meta, domain, engine, total, mine, engineRows, trend);
    }

    /// <summary>
    /// 为 URL 行补充「我的文章」、我的引用次数与不同提问数。<paramref name="citingFacts"/> 是引用该 URL 的合格回答
    /// （同一回答多次出现只计一次）；传 <paramref name="engine"/> 时只统计该平台。
    /// </summary>
    /// <param name="row">原 URL 行。</param>
    /// <param name="citingFacts">引用该 URL 的合格回答事实。</param>
    /// <param name="ownPairs">满足文章监控门槛的我的引用对。</param>
    /// <param name="activeLinkHashes">本品牌当前有效发布链接的 URL 摘要。</param>
    /// <param name="engine">可选的小写平台过滤。</param>
    public static CitationUrlSummaryRow WithUrlStats(CitationUrlSummaryRow row, IEnumerable<AnalyticsCheckFact> citingFacts,
        IReadOnlySet<(Guid CheckId, string UrlHash)> ownPairs, IReadOnlySet<string> activeLinkHashes, string? engine)
    {
        var facts = citingFacts.Where(f => engine is null || string.Equals(f.Engine, engine, StringComparison.Ordinal))
            .DistinctBy(f => f.CheckId).ToArray();
        return row with
        {
            IsMine = activeLinkHashes.Contains(row.UrlHash),
            MyCitationEvents = facts.LongCount(f => ownPairs.Contains((f.CheckId, row.UrlHash))),
            PromptCount = facts.Select(f => f.PromptId).Distinct().LongCount()
        };
    }

    /// <summary>
    /// 只保留该域名的 URL 行，并按 rank 相同的规范排序（事件降序、URL 摘要升序）赋予从 1 开始连续的 domainRank。
    /// 必须在 engine 过滤之后、q 与 mine 过滤之前调用，后两者不重新编号；全局 rank 保持不变。
    /// </summary>
    /// <param name="rows">完整母集（或单平台）URL 行。</param>
    /// <param name="domain">规范化域名键。</param>
    public static IReadOnlyList<CitationUrlSummaryRow> WithinDomain(IEnumerable<CitationUrlSummaryRow> rows, string domain)
        => rows.Where(r => string.Equals(r.Domain, domain, StringComparison.Ordinal))
            .OrderByDescending(r => r.CitationEvents).ThenBy(r => r.UrlHash, StringComparer.Ordinal)
            .Select((r, index) => r with { DomainRank = index + 1 }).ToArray();

    /// <summary>
    /// 单个 URL 按提问分组的问题引用明细。每个通过统一判定的有效事件计 1 次（同一回答内同一 URL 已去重），
    /// 因此 citationEvents 与引用文章榜该行一致、各分组之和等于总数。分组取 <paramref name="promptGroups"/>
    /// （提问当前所属分组），不在映射中或没有分组的问题归入「未分组」（groupId 与 name 为空，排最后）。
    /// </summary>
    /// <param name="meta">快照元数据，原样返回。</param>
    /// <param name="urlHash">目标 URL 摘要（小写十六进制）。</param>
    /// <param name="url">目标规范化 URL。</param>
    /// <param name="title">最近一次观测的标题。</param>
    /// <param name="engine">可选的小写平台过滤。</param>
    /// <param name="counted">有效事件；URL 摘要不同或平台不符的事件被忽略。</param>
    /// <param name="promptGroups">问题标识 → 当前分组。</param>
    public static CitationUrlPromptBreakdown UrlPrompts(CitationDomainMeta meta, string urlHash, string url,
        string? title, string? engine, IEnumerable<CitationDomainCountedEvent> counted,
        IReadOnlyDictionary<Guid, PromptGroupAssignment> promptGroups)
    {
        var prompts = new Dictionary<Guid, (long Events, string? Prompt, DateTimeOffset? LatestAt)>();
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in counted)
        {
            var eventUrl = item.Event.Url;
            if (!hashes.TryGetValue(eventUrl, out var hash)) hashes[eventUrl] = hash = CitationDomainAccumulator.UrlHash(eventUrl);
            if (!string.Equals(hash, urlHash, StringComparison.Ordinal)) continue;
            var fact = item.Fact;
            if (engine is not null && !string.Equals(fact.Engine, engine, StringComparison.Ordinal)) continue;
            if (!prompts.TryGetValue(fact.PromptId, out var current))
            {
                prompts[fact.PromptId] = (1, fact.PromptSnapshot, fact.CapturedAt);
                continue;
            }
            // Latest observed prompt text wins; a missing capture time never replaces a known one.
            var newer = fact.CapturedAt is { } at && (current.LatestAt is null || at > current.LatestAt
                || at == current.LatestAt && string.IsNullOrEmpty(current.Prompt));
            prompts[fact.PromptId] = newer ? (current.Events + 1, fact.PromptSnapshot, fact.CapturedAt)
                : (current.Events + 1, current.Prompt, current.LatestAt);
        }
        var groups = prompts.Select(pair =>
            {
                var assignment = promptGroups.TryGetValue(pair.Key, out var found) && found.GroupId is not null ? found : null;
                return (GroupId: assignment?.GroupId, Name: assignment?.Name,
                    Row: new CitationUrlPromptRow(pair.Key, pair.Value.Prompt ?? "", pair.Value.Events));
            })
            .GroupBy(entry => entry.GroupId)
            .Select(group => new CitationUrlPromptGroup(group.Key, group.Key is null ? null : group.First().Name,
                group.Sum(entry => entry.Row.CitationEvents),
                group.Select(entry => entry.Row).OrderByDescending(row => row.CitationEvents)
                    .ThenBy(row => row.Prompt, StringComparer.Ordinal).ThenBy(row => row.PromptId).ToArray()))
            .OrderBy(group => group.GroupId is null ? 1 : 0).ThenByDescending(group => group.CitationEvents)
            .ThenBy(group => group.Name, StringComparer.Ordinal).ThenBy(group => group.GroupId).ToArray();
        return new CitationUrlPromptBreakdown(meta, urlHash, url, title, engine, groups.Sum(group => group.CitationEvents),
            groups.Length, prompts.Count, groups);
    }

    /// <summary>标题或 URL 的不区分大小写字面包含匹配；标题为空时只匹配 URL。</summary>
    public static bool MatchesUrlSearch(CitationUrlSummaryRow row, string? title, string search)
        => row.Url.Contains(search, StringComparison.OrdinalIgnoreCase)
            || (title?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);

    private static DateOnly LocalDate(DateTimeOffset timestamp, TimeZoneInfo zone)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timestamp, zone).DateTime);
}
