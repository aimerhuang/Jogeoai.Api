namespace Jogeoai.Domain;

/// <summary>引用来源域名类型（citation_domain_types.site_type）。</summary>
public enum CitationDomainSiteType
{
    /// <summary>品牌官网：企业/品牌/厂商/经销商官网，含品牌自营商城与帮助中心。</summary>
    BrandSite,
    /// <summary>媒体平台：自媒体/内容平台、社区问答、新闻门户、行业媒体、百科、视频、博客、文库、测评导购站。</summary>
    MediaPlatform,
    /// <summary>电商B2B：零售电商、B2B 采购/供应商平台及其店铺子域。</summary>
    EcommerceB2b,
    /// <summary>其他：政府、协会、学术、应用商店、云服务与工具等。</summary>
    Other
}

/// <summary>域名类型字典中的一条规则。</summary>
/// <param name="Domain">规范化 ASCII 主机（去一个前导 www.）。</param>
/// <param name="IncludeSubdomains">是否同时覆盖全部子域。</param>
/// <param name="SiteType">站点类型。</param>
/// <param name="PlatformKey">平台标识；同一平台的多条规则共用，为空时按规则域名合并。</param>
/// <param name="PlatformName">媒体平台中文显示名。</param>
/// <param name="PublishUrl">媒体平台发布入口（https）。</param>
public sealed record CitationDomainTypeRule(string Domain, bool IncludeSubdomains, CitationDomainSiteType SiteType,
    string? PlatformKey, string? PlatformName, string? PublishUrl)
{
    /// <summary>存储值到类型的映射；未知值返回空。</summary>
    public static CitationDomainSiteType? ParseSiteType(string value) => value switch
    {
        "brand_site" => CitationDomainSiteType.BrandSite,
        "media_platform" => CitationDomainSiteType.MediaPlatform,
        "ecommerce_b2b" => CitationDomainSiteType.EcommerceB2b,
        "other" => CitationDomainSiteType.Other,
        _ => null
    };
}

/// <summary>域名类型字典的内存索引：精确域名优先，其次是包含子域名且最长的规则；未命中即未分类。</summary>
public sealed class CitationDomainTypeIndex
{
    private readonly Dictionary<string, CitationDomainTypeRule> rules;

    /// <summary>空字典（全部未分类）。</summary>
    public static CitationDomainTypeIndex Empty { get; } = new([]);

    /// <summary>以规则列表建立索引；域名重复时保留后者。</summary>
    /// <param name="rules">字典规则。</param>
    public CitationDomainTypeIndex(IEnumerable<CitationDomainTypeRule> rules)
    {
        this.rules = new(StringComparer.Ordinal);
        foreach (var rule in rules) this.rules[Normalize(rule.Domain)] = rule;
    }

    /// <summary>规则数量。</summary>
    public int Count => rules.Count;

    /// <summary>查找域名命中的规则；逐级去掉最左一段向上查找包含子域名的规则，因此最长规则优先。</summary>
    /// <param name="domain">规范化 ASCII 主机域名。</param>
    public CitationDomainTypeRule? Match(string domain)
    {
        var host = Normalize(domain);
        if (rules.TryGetValue(host, out var exact)) return exact;
        for (var dot = host.IndexOf('.'); dot >= 0; dot = host.IndexOf('.', dot + 1))
            if (rules.TryGetValue(host[(dot + 1)..], out var parent) && parent.IncludeSubdomains) return parent;
        return null;
    }

    private static string Normalize(string domain) => domain.Trim().TrimEnd('.').ToLowerInvariant();
}
