using System.Net;
using System.Text;

namespace Jogeoai.Providers.Abstractions;

/// <summary>公开引用 URL 的接受结果。</summary>
public enum CitationUrlOutcome
{
    /// <summary>可保存的公开 http(s) 链接。</summary>
    Accepted,
    /// <summary>缺失或无法解析为绝对 URL（INVALID_SOURCE_SHAPE）。</summary>
    Invalid,
    /// <summary>非公开、过长、含凭据或已脱敏的链接（UNSAFE_SOURCE）。</summary>
    Unsafe,
}

/// <summary>URL 规范化结果。</summary>
/// <param name="Outcome">接受结果。</param>
/// <param name="Url">规范化后的绝对地址（去 www. 与片段）；未接受时为空。</param>
/// <param name="Host">规范化后的来源域名；未接受时为空。</param>
public sealed record CitationUrlResult(CitationUrlOutcome Outcome, string? Url, string? Host);

/// <summary>
/// 引用来源的 URL 安全与规范化规则，供各 Provider 适配器共用。
/// 规则与 <c>CloroAdapter.ExtractCitations</c>（src/Jogeoai.Providers.Cloro/CloroAdapter.cs）逐条一致：
/// 仅 http/https、≤750 字符、合法 DNS 主机、去 www. 与片段、拒绝凭据类查询参数；标题最长 512 个标量值。
/// Cloro 适配器暂保留自己的实现，后续统一时以本类为准。
/// </summary>
public static class CitationUrlRules
{
    /// <summary>单条链接的最大长度，与 citations.normalized_url 的索引上限一致。</summary>
    public const int MaximumUrlLength = 750;
    /// <summary>标题保留的最大标量值数。</summary>
    public const int MaximumTitleLength = 512;
    /// <summary>每个回答保留的引用上限。</summary>
    public const int MaximumCitations = 100;

    private static readonly string[] PrivateSuffixes = ["localhost", "local", "internal", "intranet", "invalid", "test", "home", "lan", "onion"];
    private static readonly HashSet<string> SensitiveQueryFields =
    [
        "authorization", "proxyauthorization", "apikey", "key", "token", "accesstoken", "refreshtoken", "idtoken", "authtoken", "auth", "signature", "sig",
        "cookie", "cookies", "setcookie", "password", "secret", "clientsecret", "credentials", "credential",
        "profile", "profilepath", "browserprofile", "userdatadir", "storagestate", "headers", "requestheaders", "responseheaders",
        "html", "rawhtml", "pagehtml", "innerhtml", "outerhtml", "screenshot", "screenshots", "screenshoturl", "screenshotbase64",
        "error", "errormessage", "stack", "stacktrace",
    ];

    /// <summary>校验并规范化一条引用链接。</summary>
    /// <param name="url">Provider 返回的原始链接。</param>
    public static CitationUrlResult Normalize(string? url)
    {
        if (url?.Contains("redacted", StringComparison.OrdinalIgnoreCase) == true) return Unsafe();
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return new(CitationUrlOutcome.Invalid, null, null);
        if (url.Length > MaximumUrlLength || parsed.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(parsed.UserInfo)) return Unsafe();
        var host = parsed.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (Uri.CheckHostName(host) != UriHostNameType.Dns || !host.Contains('.') || IPAddress.TryParse(host, out _)
            || host.Split('.').Any(label => label.Length is < 1 or > 63 || label[0] == '-' || label[^1] == '-' || label.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
            || PrivateSuffixes.Any(suffix => host == suffix || host.EndsWith('.' + suffix, StringComparison.Ordinal)))
            return Unsafe();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        var builder = new UriBuilder(parsed) { Host = host, Fragment = string.Empty };
        var normalized = builder.Uri.AbsoluteUri;
        if (normalized.Length > MaximumUrlLength || normalized.Contains("redacted", StringComparison.OrdinalIgnoreCase) || HasCredentialQuery(parsed))
            return Unsafe();
        return new(CitationUrlOutcome.Accepted, normalized, host);
    }

    /// <summary>按标量值数截断标题；返回是否发生截断。</summary>
    /// <param name="title">原始标题。</param>
    /// <param name="bounded">截断后的标题；原始为空时为空。</param>
    public static bool BoundTitle(string? title, out string? bounded)
    {
        if (string.IsNullOrWhiteSpace(title)) { bounded = null; return false; }
        bounded = BoundString(title, MaximumTitleLength);
        return bounded != title;
    }

    /// <summary>按 Unicode 标量值数截断字符串，不拆分代理对。</summary>
    /// <param name="value">原始字符串。</param>
    /// <param name="maximumScalars">最大标量值数。</param>
    public static string BoundString(string value, int maximumScalars)
    {
        var builder = new StringBuilder(Math.Min(value.Length, maximumScalars));
        var count = 0;
        foreach (var rune in value.EnumerateRunes()) { if (count++ == maximumScalars) break; builder.Append(rune.ToString()); }
        return builder.ToString();
    }

    private static CitationUrlResult Unsafe() => new(CitationUrlOutcome.Unsafe, null, null);

    private static bool HasCredentialQuery(Uri uri)
    {
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var field = Uri.UnescapeDataString(part.Split('=', 2)[0]);
            var key = string.Concat(field.Where(char.IsLetterOrDigit)).ToLowerInvariant();
            if (SensitiveQueryFields.Contains(key) || key is "signature" or "sig" or "auth") return true;
        }
        return false;
    }
}
