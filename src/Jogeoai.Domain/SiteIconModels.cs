using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.RegularExpressions;

namespace Jogeoai.Domain;

/// <summary>站点图标缓存状态取值。</summary>
public static class SiteIconStatus
{
    /// <summary>已取得图标字节。</summary>
    public const string Found = "found";

    /// <summary>所有来源都拿不到图标。</summary>
    public const string Missing = "missing";
}

/// <summary>站点图标来源取值。</summary>
public static class SiteIconSource
{
    /// <summary>首页 HTML 中 <c>&lt;link rel="icon"&gt;</c> 声明的图标。</summary>
    public const string Html = "html";

    /// <summary>站点根路径 <c>/favicon.ico</c>。</summary>
    public const string Favicon = "favicon";

    /// <summary>Google faviconV2 第三方服务。</summary>
    public const string Google = "google";

    /// <summary>DuckDuckGo ip3 第三方服务。</summary>
    public const string DuckDuckGo = "duckduckgo";

    /// <summary>Yandex favicon v2 第三方服务。</summary>
    public const string Yandex = "yandex";
}

/// <summary>全局站点图标缓存记录（表 <c>site_icons</c>）。</summary>
/// <param name="Domain">规范化后的 ASCII 主机名。</param>
/// <param name="Status">found 或 missing。</param>
/// <param name="ContentType">魔数识别出的真实图片类型；missing 时为空。</param>
/// <param name="Bytes">图标字节；missing 时为空。</param>
/// <param name="Source">图标来源（html / favicon / google / duckduckgo / yandex）；子域名借用上级域名图标时为上级记录的来源；missing 时为空。</param>
/// <param name="FetchedAt">取得当前内容的 UTC 时间。</param>
/// <param name="ExpiresAt">缓存过期的 UTC 时间。</param>
public sealed record SiteIconRecord(
    string Domain,
    string Status,
    string? ContentType,
    byte[]? Bytes,
    string? Source,
    DateTimeOffset FetchedAt,
    DateTimeOffset ExpiresAt)
{
    /// <summary>是否为有字节的 found 记录。</summary>
    public bool IsFound => Status == SiteIconStatus.Found && Bytes is { Length: > 0 } && ContentType is not null;

    /// <summary>在给定时间点是否已过期。</summary>
    public bool IsExpired(DateTimeOffset now) => ExpiresAt <= now;
}

/// <summary>站点图标的纯函数规则：域名校验、图片魔数与尺寸识别、HTML 图标链接解析。</summary>
public static class SiteIconRules
{
    /// <summary>域名最大长度。</summary>
    public const int MaxDomainLength = 253;

    /// <summary>首页 HTML 中最多取用的图标候选数。</summary>
    public const int MaxHtmlCandidates = 3;

    /// <summary>与两字母国家码组成公共后缀的二级标签（如 com.cn、co.id、gov.cn），用于可注册域名判断。</summary>
    private static readonly HashSet<string> SecondLevelSuffixLabels = new(StringComparer.Ordinal) { "com", "net", "org", "gov", "edu", "co", "ac" };

    private static readonly Regex Comment = new("<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex LinkTag = new("<link\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Attribute = new(
        "([a-zA-Z_:][-a-zA-Z0-9_:.]*)\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s\"'>]+))",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// 规范化并校验站点域名：小写、去掉末尾点、长度 ≤ 253、每段 1–63 个 <c>[a-z0-9-]</c> 且不以连字符开头结尾、至少两段；
    /// 拒绝 IP 字面量、纯数字顶级域、<c>localhost</c>、<c>.local</c>、<c>.localhost</c> 与 <c>.internal</c>。
    /// </summary>
    public static bool TryNormalizeDomain(string? input, [NotNullWhen(true)] out string? domain)
    {
        domain = null;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var value = input.Trim();
        if (value.EndsWith('.')) value = value[..^1];
        if (value.Length is 0 or > MaxDomainLength) return false;
        Span<char> buffer = stackalloc char[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c is >= 'A' and <= 'Z') c = (char)(c + 32);
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '.')) return false;
            buffer[i] = c;
        }
        value = new string(buffer);
        var labels = value.Split('.');
        if (labels.Length < 2) return false;
        foreach (var label in labels)
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-') return false;
        if (labels[^1].All(char.IsAsciiDigit)) return false;
        if (IPAddress.TryParse(value, out _)) return false;
        if (value is "localhost" || value.EndsWith(".localhost", StringComparison.Ordinal)
            || value.EndsWith(".local", StringComparison.Ordinal) || value.EndsWith(".internal", StringComparison.Ordinal)) return false;
        domain = value;
        return true;
    }

    /// <summary>
    /// 可注册域名的启发式判断（输入为已规范化的域名）：两段（example.com），
    /// 或三段且倒数第二段为 com/net/org/gov/edu/co/ac、最后一段为两字母国家码（sz-water.com.cn、ndt-instruments.co.id）。
    /// 不使用完整公共后缀列表，只覆盖常见情形。
    /// </summary>
    public static bool IsRegistrableDomain(string domain)
    {
        var labels = domain.Split('.');
        return labels.Length == 2
            || (labels.Length == 3 && SecondLevelSuffixLabels.Contains(labels[1])
                && labels[2].Length == 2 && labels[2].All(char.IsAsciiLetterLower));
    }

    /// <summary>
    /// 子域名向上一级的候选域名（止于可注册域名，不会返回 com.cn / co.id 这类公共后缀）；
    /// 域名本身已是可注册域名或不足两段时返回 null。例如 cg.sz-water.com.cn → sz-water.com.cn，a.b.example.com → b.example.com。
    /// </summary>
    public static string? ParentCandidate(string domain)
    {
        if (domain.Split('.').Length < 3 || IsRegistrableDomain(domain)) return null;
        return domain[(domain.IndexOf('.') + 1)..];
    }

    /// <summary>按魔数识别位图类型；只接受 png / ico / gif / jpeg / webp / bmp，HTML、SVG 及其他内容返回 null。</summary>
    public static string? DetectContentType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
            return "image/png";
        // ICO：保留字 0、类型 1、至少一个 16 字节目录项。
        if (bytes.Length >= 22 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 1 && bytes[3] == 0
            && (bytes[4] | bytes[5] << 8) >= 1)
            return "image/x-icon";
        if (bytes.Length >= 10 && bytes[0] == 'G' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == '8'
            && bytes[4] is (byte)'7' or (byte)'9' && bytes[5] == 'a')
            return "image/gif";
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "image/jpeg";
        if (bytes.Length >= 16 && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F'
            && bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P')
            return "image/webp";
        if (bytes.Length >= 26 && bytes[0] == 'B' && bytes[1] == 'M'
            && ReadInt32(bytes, 14) is 12 or 40 or 52 or 56 or 64 or 108 or 124)
            return "image/bmp";
        return null;
    }

    /// <summary>读取图片像素宽度（ICO 取最大条目）；无法识别时返回 null。</summary>
    public static int? ReadWidth(ReadOnlySpan<byte> bytes)
    {
        switch (DetectContentType(bytes))
        {
            case "image/png":
                return bytes.Length >= 24 ? (int)((uint)bytes[16] << 24 | (uint)bytes[17] << 16 | (uint)bytes[18] << 8 | bytes[19]) : null;
            case "image/x-icon":
            {
                var count = bytes[4] | bytes[5] << 8;
                int? max = null;
                for (var i = 0; i < count && 6 + 16 * i < bytes.Length; i++)
                {
                    var width = bytes[6 + 16 * i] == 0 ? 256 : bytes[6 + 16 * i];
                    max = max is null ? width : Math.Max(max.Value, width);
                }
                return max;
            }
            case "image/gif":
                return bytes[6] | bytes[7] << 8;
            case "image/bmp":
                return ReadInt32(bytes, 14) == 12 ? bytes[18] | bytes[19] << 8 : Math.Abs(ReadInt32(bytes, 18));
            case "image/jpeg":
                return ReadJpegWidth(bytes);
            case "image/webp":
                return ReadWebpWidth(bytes);
            default:
                return null;
        }
    }

    /// <summary>
    /// 从首页 HTML 中解析 <c>&lt;link rel="icon" | "shortcut icon" | "apple-touch-icon"&gt;</c> 的 href，
    /// 相对 <paramref name="baseUri"/> 解析为绝对 http(s) 地址；丢弃 <c>data:</c> 与 SVG。
    /// 排序：显式 png/ico 的 icon（sizes 越接近 64 越靠前）→ 其他 icon → apple-touch-icon；最多返回 <paramref name="max"/> 个。
    /// </summary>
    public static IReadOnlyList<Uri> ParseIconLinks(string html, Uri baseUri, int max = MaxHtmlCandidates)
    {
        if (string.IsNullOrEmpty(html) || max <= 0) return [];
        var candidates = new List<(Uri Url, int Group, int Distance, int Order)>();
        var order = 0;
        foreach (Match tag in LinkTag.Matches(Comment.Replace(html, " ")))
        {
            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match attribute in Attribute.Matches(tag.Value))
            {
                var name = attribute.Groups[1].Value;
                if (attributes.ContainsKey(name)) continue;
                var raw = attribute.Groups[2].Success ? attribute.Groups[2].Value
                    : attribute.Groups[3].Success ? attribute.Groups[3].Value : attribute.Groups[4].Value;
                attributes[name] = WebUtility.HtmlDecode(raw).Trim();
            }
            if (!attributes.TryGetValue("rel", out var rel) || !attributes.TryGetValue("href", out var href) || href.Length == 0) continue;
            var tokens = rel.ToLowerInvariant().Split((char[])[' ', '\t', '\r', '\n', '\f'], StringSplitOptions.RemoveEmptyEntries);
            var isIcon = tokens.Contains("icon");
            var isApple = tokens.Contains("apple-touch-icon") || tokens.Contains("apple-touch-icon-precomposed");
            if (!isIcon && !isApple) continue;
            if (href.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Uri.TryCreate(baseUri, href, out var url) || url.Scheme is not ("http" or "https")) continue;
            var type = attributes.TryGetValue("type", out var typeValue) ? typeValue.ToLowerInvariant() : string.Empty;
            var path = url.AbsolutePath.ToLowerInvariant();
            if (type.Contains("svg", StringComparison.Ordinal) || path.EndsWith(".svg", StringComparison.Ordinal)
                || path.EndsWith(".svgz", StringComparison.Ordinal)) continue;
            var explicitBitmap = type is "image/png" or "image/x-icon" or "image/vnd.microsoft.icon" or "image/ico" or "image/icon"
                || path.EndsWith(".png", StringComparison.Ordinal) || path.EndsWith(".ico", StringComparison.Ordinal);
            var group = isIcon ? (explicitBitmap ? 0 : 1) : 2;
            var size = attributes.TryGetValue("sizes", out var sizes) ? ClosestSize(sizes) : null;
            candidates.Add((url, group, size is null ? 1000 : Math.Abs(size.Value - 64), order++));
        }
        return candidates
            .OrderBy(candidate => candidate.Group).ThenBy(candidate => candidate.Distance).ThenBy(candidate => candidate.Order)
            .Select(candidate => candidate.Url)
            .DistinctBy(url => url.AbsoluteUri)
            .Take(max)
            .ToArray();
    }

    private static int? ClosestSize(string sizes)
    {
        int? best = null;
        foreach (var token in sizes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = token.IndexOfAny(['x', 'X']);
            if (separator <= 0 || !int.TryParse(token.AsSpan(0, separator), out var width) || width <= 0) continue;
            if (best is null || Math.Abs(width - 64) < Math.Abs(best.Value - 64)) best = width;
        }
        return best;
    }

    private static int ReadInt32(ReadOnlySpan<byte> bytes, int offset) =>
        bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16 | bytes[offset + 3] << 24;

    private static int? ReadJpegWidth(ReadOnlySpan<byte> bytes)
    {
        var i = 2;
        while (i + 8 < bytes.Length)
        {
            if (bytes[i] != 0xFF) return null;
            var marker = bytes[i + 1];
            if (marker == 0xFF) { i++; continue; }
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) { i += 2; continue; }
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                return bytes[i + 7] << 8 | bytes[i + 8];
            var length = bytes[i + 2] << 8 | bytes[i + 3];
            if (length < 2) return null;
            i += 2 + length;
        }
        return null;
    }

    private static int? ReadWebpWidth(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 30) return null;
        var chunk = System.Text.Encoding.ASCII.GetString(bytes.Slice(12, 4));
        return chunk switch
        {
            "VP8X" => 1 + (bytes[24] | bytes[25] << 8 | bytes[26] << 16),
            "VP8L" => 1 + (bytes[21] | (bytes[22] & 0x3F) << 8),
            "VP8 " => (bytes[26] | bytes[27] << 8) & 0x3FFF,
            _ => null,
        };
    }
}
