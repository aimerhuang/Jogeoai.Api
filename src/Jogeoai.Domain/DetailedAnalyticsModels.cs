using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jogeoai.Domain;

/// <summary>搜索词在一个 Check 中的一次去重事件。</summary>
/// <param name="Key">归一化后的搜索词键。</param>
/// <param name="Original">Provider 返回的原始搜索词。</param>
/// <param name="Position">原始数组中从一开始的位置。</param>
/// <param name="Included">是否进入扩展词统计。</param>
/// <param name="ExclusionReason">被排除时的规则原因。</param>
public sealed record QueryEvent(string Key, string Original, int Position, bool Included, string? ExclusionReason);

/// <summary>搜索词相对原 Prompt 的词项变化。</summary>
/// <param name="Text">词项文本。</param>
/// <param name="Relation">新增、删除或保留关系。</param>
public sealed record QueryTerm(string Text, string Relation);

/// <summary>由一条公开引用派生的外部来源事件。</summary>
/// <param name="Url">规范化的公开 URL。</param>
/// <param name="Domain">URL 主机域名。</param>
/// <param name="Title">已有引用标题。</param>
/// <param name="FirstPosition">此 URL 首次出现的位置。</param>
/// <param name="SourceCategory">来源分类机器值。</param>
/// <param name="PageType">页面类型机器值。</param>
/// <param name="Ownership">品牌、竞品或第三方归属。</param>
/// <param name="SubjectId">匹配的品牌或竞品标识。</param>
/// <param name="RuleBasis">分类命中依据。</param>
public sealed record CitationEvent(string Url, string Domain, string? Title, int FirstPosition,
    string SourceCategory, string PageType, string Ownership, Guid? SubjectId,
    string RuleBasis);

/// <summary>Google 内部搜索或购物链接的可解析事实。</summary>
/// <param name="Url">规范化的 Google 内部 URL。</param>
/// <param name="Kind">搜索或购物链接类型。</param>
/// <param name="Query">从链接解析到的搜索词。</param>
/// <param name="Product">仅从已有标题取得的商品名称。</param>
/// <param name="Availability">可解析字段的可用状态。</param>
/// <param name="Ownership">由冻结配置判定的主体归属。</param>
/// <param name="SubjectId">匹配的品牌或竞品标识。</param>
/// <param name="FirstPosition">此链接首次出现的位置。</param>
public sealed record GoogleInternalEvent(string Url, string Kind, string? Query, string? Product,
    string Availability, string? Ownership, Guid? SubjectId, int FirstPosition);

/// <summary>版本化搜索词正规化、分词及公开引用分类规则。</summary>
public static partial class DetailedAnalyticsRules
{
    /// <summary>搜索词正规化版本。</summary>
    public const string QueryVersion = "query-v1";
    /// <summary>历史 URL 正规化版本；旧发布快照仍按此规则解释。</summary>
    public const string UrlV1 = "url-v1";
    /// <summary>当前 URL 正规化版本。</summary>
    public const string UrlV2 = "url-v2";
    /// <summary>新事实默认使用的 URL 正规化版本。</summary>
    public const string UrlVersion = UrlV2;
    /// <summary>来源分类规则版本。</summary>
    public const string ClassificationVersion = "citation-v1";
    /// <summary>中英文词项版本。</summary>
    public const string TokenizerVersion = "term-v1";

    private static readonly HashSet<string> Sentinels = new(StringComparer.Ordinal)
    {
        "unavailable", "not available", "n/a", "none", "null", "unknown", "无", "不可用", "未提供"
    };
    private static readonly HashSet<string> GoogleDomains = new(StringComparer.Ordinal)
    {
        "google.com", "google.co.uk", "google.com.au", "google.ca", "google.de", "google.fr",
        "google.es", "google.it", "google.nl", "google.co.jp", "google.co.in", "google.com.br",
        "google.com.hk", "google.com.sg", "google.cn"
    };

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Whitespace();
    [GeneratedRegex(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Word();
    [GeneratedRegex(@"[\p{IsCJKUnifiedIdeographs}]+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Han();

    /// <summary>折叠空白、Unicode 标准化并忽略大小写。</summary>
    public static string Key(string value) => Whitespace().Replace(value.Trim(), " ")
        .Normalize(NormalizationForm.FormKC).ToUpperInvariant();

    /// <summary>保留原顺序，对同一 Check 内的相同词只产生一次事件。</summary>
    public static IReadOnlyList<QueryEvent> Queries(IReadOnlyList<string> queries, string prompt)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var originalKey = Key(prompt);
        var events = new List<QueryEvent>();
        for (var i = 0; i < queries.Count; i++)
        {
            var original = queries[i];
            if (original is null) continue;
            var key = Key(original);
            if (key.Length == 0 || !seen.Add(key)) continue;
            var reason = key == originalKey ? "same_as_prompt"
                : Sentinels.Contains(key.ToLowerInvariant()) ? "unavailable_sentinel" : null;
            events.Add(new QueryEvent(key, original, i + 1, reason is null, reason));
        }
        return events;
    }

    /// <summary>按语言生成词项；中文连续汉字使用单字和相邻双字，英文按 Unicode 字词边界。</summary>
    public static IReadOnlyList<string>? Terms(string value, string? locale)
    {
        if (locale is null) return null;
        var language = locale.Split('-', '_')[0].ToLowerInvariant();
        if (language is not ("zh" or "en")) return null;
        var key = Key(value);
        var terms = new HashSet<string>(StringComparer.Ordinal);
        if (language == "en")
        {
            foreach (Match match in Word().Matches(key)) if (match.Value.Length > 0) terms.Add(match.Value);
        }
        else
        {
            foreach (Match match in Han().Matches(key))
            {
                var runes = match.Value.EnumerateRunes().Select(r => r.ToString()).ToArray();
                foreach (var rune in runes) terms.Add(rune);
                for (var i = 0; i + 1 < runes.Length; i++) terms.Add(runes[i] + runes[i + 1]);
            }
            foreach (Match match in Word().Matches(key))
                foreach (Match nonHan in Word().Matches(Han().Replace(match.Value, " ")))
                    terms.Add(nonHan.Value);
        }
        return terms.Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>相对原 Prompt 标记新增、删除和保留词项。</summary>
    public static IReadOnlyList<QueryTerm>? CompareTerms(string query, string prompt, string? locale)
    {
        var queryTerms = Terms(query, locale);
        var promptTerms = Terms(prompt, locale);
        if (queryTerms is null || promptTerms is null) return null;
        var querySet = queryTerms.ToHashSet(StringComparer.Ordinal);
        var promptSet = promptTerms.ToHashSet(StringComparer.Ordinal);
        return querySet.Union(promptSet).Order(StringComparer.Ordinal)
            .Select(t => new QueryTerm(t, querySet.Contains(t)
                ? promptSet.Contains(t) ? "retained" : "added" : "removed")).ToArray();
    }

    /// <summary>按指定版本规范化公开 URL。版本化方法保证旧发布快照不会被当前规则重解释。</summary>
    public static string? NormalizeUrl(string url, string version = UrlV2)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo)) return null;
        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        if (version is not (UrlV1 or UrlV2))
            throw new ArgumentException($"不支持的 URL 规范化版本：{version}。", nameof(version));
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => version == UrlV1 || !IsTrackingParameter(part));
        var builder = new UriBuilder(uri) { Host = host, Fragment = string.Empty, Query = string.Join('&', query) };
        var result = builder.Uri.AbsoluteUri;
        return result.Length <= 750 ? result : null;
    }

    private static bool IsTrackingParameter(string part)
    {
        var key = part.Split('=', 2)[0];
        try { key = Uri.UnescapeDataString(key); } catch (UriFormatException) { }
        return key.StartsWith("utm_", StringComparison.OrdinalIgnoreCase)
            || key.Equals("gclid", StringComparison.OrdinalIgnoreCase)
            || key.Equals("dclid", StringComparison.OrdinalIgnoreCase)
            || key.Equals("gbraid", StringComparison.OrdinalIgnoreCase)
            || key.Equals("wbraid", StringComparison.OrdinalIgnoreCase)
            || key.Equals("fbclid", StringComparison.OrdinalIgnoreCase)
            || key.Equals("msclkid", StringComparison.OrdinalIgnoreCase)
            || key.Equals("twclid", StringComparison.OrdinalIgnoreCase)
            || key.Equals("li_fat_id", StringComparison.OrdinalIgnoreCase)
            || key.Equals("mc_cid", StringComparison.OrdinalIgnoreCase)
            || key.Equals("mc_eid", StringComparison.OrdinalIgnoreCase)
            || key.Equals("igshid", StringComparison.OrdinalIgnoreCase)
            || key.Equals("_ga", StringComparison.OrdinalIgnoreCase)
            || key.Equals("_gl", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>解析 Google 内部搜索和购物链接；未提供的字段保持空值。</summary>
    public static GoogleInternalEvent? GoogleInternal(string url, string? title, int position,
        SubjectConfiguration configuration)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var host = uri.IdnHost.ToLowerInvariant();
        var root = host.StartsWith("www.", StringComparison.Ordinal) ? host[4..]
            : host.StartsWith("shopping.", StringComparison.Ordinal) ? host[9..] : host;
        if (!GoogleDomains.Contains(root)) return null;
        var path = uri.AbsolutePath.ToLowerInvariant();
        var kind = path.StartsWith("/shopping", StringComparison.Ordinal) || host.StartsWith("shopping.", StringComparison.Ordinal)
            ? "shopping" : path is "/search" or "/url" ? "search" : null;
        if (kind is null) return null;
        var query = QueryParameter(uri.Query, "q") ?? QueryParameter(uri.Query, "query");
        var product = kind == "shopping" ? title : null;
        var text = product ?? query;
        var candidates = text is null ? [] : configuration.Subjects.Where(s =>
            configuration.Terms.Any(t => t.SubjectId == s.Id && !t.Kind.Contains("domain", StringComparison.Ordinal)
                && text.Contains(t.Key, StringComparison.OrdinalIgnoreCase))).ToArray();
        var subject = candidates.Length == 1 ? candidates[0] : null;
        return new GoogleInternalEvent(url, kind, query, product,
            query is null && product is null ? "unavailable" : "available",
            subject?.Type, subject?.Id, position);
    }

    /// <summary>只用配置域名、URL 和标题分类；不读取引用页正文。</summary>
    public static CitationEvent Classify(string url, string? title, int position,
        SubjectConfiguration configuration, string version = UrlV2)
    {
        var normalized = NormalizeUrl(url, version) ?? url;
        var uri = new Uri(normalized);
        var domain = uri.IdnHost.ToLowerInvariant();
        var owner = configuration.Terms.FirstOrDefault(t => t.Kind.Contains("domain", StringComparison.Ordinal)
            && DomainMatches(domain, t.Key, configuration.IncludeSubdomains));
        var subject = owner is null ? null : configuration.Subjects.FirstOrDefault(s => s.Id == owner.SubjectId);
        var (thirdPartyCategory, thirdPartyBasis) = ThirdPartyCategory(domain, uri, title);
        var category = subject?.Type ?? thirdPartyCategory;
        var pageType = PageType(uri, title);
        var basis = subject is not null ? "configured_domain" : category != "other" ? thirdPartyBasis
            : pageType != "other" ? "url_or_title_pattern" : "unknown";
        return new CitationEvent(normalized, domain, title, position, category, pageType,
            subject?.Type ?? "third_party", subject?.Id, basis);
    }

    private static (string Category, string Basis) ThirdPartyCategory(string domain, Uri uri, string? title)
    {
        if (HostMatches(domain, "wikipedia.org", "britannica.com")) return ("reference", "maintained_host_rule");
        if (HostMatches(domain, "reddit.com", "facebook.com", "instagram.com", "linkedin.com", "youtube.com"))
            return ("social", "maintained_host_rule");
        if (HostMatches(domain, "github.com", "stackoverflow.com")) return ("developer", "maintained_host_rule");
        if (HostMatches(domain, "amazon.com", "amazon.co.uk", "amazon.de", "amazon.co.jp",
                "ebay.com", "ebay.co.uk", "ebay.de")) return ("ecommerce", "maintained_host_rule");
        if (HostMatches(domain, "trustpilot.com", "g2.com")) return ("reviews", "maintained_host_rule");
        if (HostMatches(domain, "prnewswire.com", "businesswire.com")) return ("pr", "maintained_host_rule");
        if (HostMatches(domain, "reuters.com", "apnews.com", "bbc.com", "nytimes.com",
                "theguardian.com", "techcrunch.com")) return ("editorial", "maintained_host_rule");
        if (domain.EndsWith(".edu", StringComparison.Ordinal) || domain.EndsWith(".gov", StringComparison.Ordinal))
            return ("institutional", "maintained_host_rule");
        var evidence = (uri.AbsolutePath + " " + (title ?? "")).ToLowerInvariant();
        if (evidence.Contains("press-release", StringComparison.Ordinal)
            || evidence.Contains("press release", StringComparison.Ordinal)
            || evidence.Contains("新闻稿", StringComparison.Ordinal)) return ("pr", "url_or_title_pattern");
        if (evidence.Contains("review", StringComparison.Ordinal)
            || evidence.Contains("评测", StringComparison.Ordinal)) return ("reviews", "url_or_title_pattern");
        if (evidence.Contains("/news/", StringComparison.Ordinal)
            || evidence.Contains("/article/", StringComparison.Ordinal)) return ("editorial", "url_or_title_pattern");
        return ("other", "unknown");
    }

    private static bool HostMatches(string domain, params string[] roots)
        => roots.Any(root => domain == root || domain.EndsWith('.' + root, StringComparison.Ordinal));

    private static bool DomainMatches(string domain, string configuredDomain, bool includeSubdomains)
        => domain == configuredDomain || includeSubdomains && domain.EndsWith('.' + configuredDomain, StringComparison.Ordinal);

    private static string PageType(Uri uri, string? title)
    {
        var path = uri.AbsolutePath.Trim('/').ToLowerInvariant();
        var evidence = "/" + path + " " + (title ?? "").ToLowerInvariant();
        if (path.Length == 0) return "homepage";
        if (evidence.Contains("/search", StringComparison.Ordinal)) return "search";
        if (evidence.Contains("/shop", StringComparison.Ordinal) || evidence.Contains("/cart", StringComparison.Ordinal)) return "shopping";
        if (evidence.Contains("/docs", StringComparison.Ordinal) || evidence.Contains("documentation", StringComparison.Ordinal)) return "doc";
        if (evidence.Contains("/forum", StringComparison.Ordinal) || evidence.Contains("/thread", StringComparison.Ordinal)) return "forum";
        if (evidence.Contains("/video", StringComparison.Ordinal) || evidence.Contains("watch", StringComparison.Ordinal)) return "video";
        if (evidence.Contains("review", StringComparison.Ordinal) || evidence.Contains("评测", StringComparison.Ordinal)) return "review";
        if (evidence.Contains("compare", StringComparison.Ordinal) || evidence.Contains("comparison", StringComparison.Ordinal)
            || evidence.Contains("对比", StringComparison.Ordinal)) return "comparison";
        if (evidence.Contains("how-to", StringComparison.Ordinal) || evidence.Contains("tutorial", StringComparison.Ordinal)
            || evidence.Contains("教程", StringComparison.Ordinal)) return "howto";
        if (evidence.Contains("top-", StringComparison.Ordinal) || evidence.Contains("best-", StringComparison.Ordinal)
            || evidence.Contains("榜单", StringComparison.Ordinal)) return "listicle";
        if (path is "product" or "item" || evidence.Contains("/product", StringComparison.Ordinal)
            || evidence.Contains("/item", StringComparison.Ordinal)) return "product";
        if (evidence.Contains("/blog", StringComparison.Ordinal) || evidence.Contains("/news", StringComparison.Ordinal)
            || evidence.Contains("article", StringComparison.Ordinal)) return "article";
        if (path is "about" or "contact" or "faq" or "info"
            || evidence.Contains("/about", StringComparison.Ordinal)
            || evidence.Contains("/faq", StringComparison.Ordinal)) return "info";
        return "other";
    }

    private static string? QueryParameter(string query, string name)
    {
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = part.Split('=', 2);
            if (!string.Equals(Uri.UnescapeDataString(parts[0]), name, StringComparison.OrdinalIgnoreCase)) continue;
            var value = Uri.UnescapeDataString(parts.Length > 1 ? parts[1].Replace('+', ' ') : "");
            return string.IsNullOrWhiteSpace(value) ? null : value[..Math.Min(value.Length, 2048)];
        }
        return null;
    }
}
