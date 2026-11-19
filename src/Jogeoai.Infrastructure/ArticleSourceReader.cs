using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Net.Http.Headers;
using Jogeoai.Application;
using Jogeoai.Domain;

namespace Jogeoai.Infrastructure;

/// <summary>按 SSRF 安全边界读取公开 HTML/纯文本文章。</summary>
/// <param name="clients">提供命名客户端 <c>research-web</c>。</param>
/// <param name="addressPolicy">DNS 预检的地址放行规则；应与 <c>research-web</c> 连接回调使用同一实例（开发环境可放行 fake-ip 网段）。</param>
public sealed class ArticleSourceReader(IHttpClientFactory clients, ArticleSourceAddressPolicy addressPolicy) : IArticleSourceReader
{
    /// <summary>只放行公网地址（<see cref="ArticleSourceAddressPolicy.PublicOnly"/>）的读取器。</summary>
    public ArticleSourceReader(IHttpClientFactory clients) : this(clients, ArticleSourceAddressPolicy.PublicOnly) { }

    private const int MaxBytes = 10 * 1024 * 1024;
    private const int MaxRedirects = 5;
    private static readonly Regex Script = new("<(script|style|noscript|form|iframe)\\b[^>]*>.*?(?:</\\1>|$)", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Tags = new("<[^>]+>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Whitespace = new("\\s+", RegexOptions.Compiled);
    private static readonly Regex LoginOrScriptChallenge = new("(?:enable\\s+javascript|javascript\\s+required|sign\\s+in\\s+to\\s+continue|log\\s+in\\s+to\\s+continue|cf-chl-|access\\s+denied)", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    // Keep synchronous preview/import reads bounded across all requests.  The
    // product contract caps one batch at four concurrent URL fetches.
    private static readonly SemaphoreSlim ReadSlots = new(4, 4);

    /// <summary>创建无代理、禁止自动跳转并在连接时重新校验公网地址的 Handler。</summary>
    /// <remarks>
    /// 该方法供 API/Worker 的 named <c>research-web</c> 客户端注册使用。连接回调
    /// 不把 DNS 结果交给系统默认连接器，避免验证和实际连接之间出现一次未检查的解析。
    /// </remarks>
    public static SocketsHttpHandler CreatePinnedHandler() => CreatePinnedHandler(IsPublicAddress);

    /// <summary>创建与 <see cref="CreatePinnedHandler()"/> 相同、但按给定谓词放行连接地址的 Handler。</summary>
    /// <param name="isAllowed">连接前对每个解析地址的放行判断；站点图标客户端在开发环境可额外放行 fake-ip 网段。</param>
    public static SocketsHttpHandler CreatePinnedHandler(Func<IPAddress, bool> isAllowed)
    {
        ArgumentNullException.ThrowIfNull(isAllowed);
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = async (context, cancellationToken) =>
            {
                var host = context.DnsEndPoint.Host.TrimEnd('.');
                IPAddress[] addresses;
                try { addresses = await Dns.GetHostAddressesAsync(host, cancellationToken); }
                catch (Exception exception) when (exception is SocketException or ArgumentException)
                { throw new HttpRequestException("Source host DNS resolution failed.", exception); }
                var publicAddresses = addresses.Where(isAllowed).Distinct().ToArray();
                if (publicAddresses.Length == 0)
                    throw new HttpRequestException("Source host resolved to a private address.");
                Exception? last = null;
                foreach (var address in publicAddresses)
                {
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception exception) when (exception is SocketException or IOException)
                    {
                        last = exception;
                        socket.Dispose();
                    }
                }
                throw new HttpRequestException("Source host connection failed.", last);
            },
        };
    }

    /// <inheritdoc />
    public async Task<ArticleSourceReadResult> ReadAsync(string url, CancellationToken cancellationToken = default)
    {
        // 抓取必须保留实际主机名（含 www.）：去 www 的统计规范化只用于去重键，否则
        // 「裸域名 301 到 www」的站点会在每次跳转后再次被去掉 www，直到重定向次数耗尽。
        var first = NormalizeFetchUrl(url);
        var redirects = new List<string> { first };
        var current = first;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var acquired = false;
        try
        {
            await ReadSlots.WaitAsync(timeout.Token);
            acquired = true;
            using var client = clients.CreateClient("research-web");
            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                var validation = await ValidatePublicHostAsync(current, addressPolicy.IsAllowed, timeout.Token);
                if (validation is not null) return Failed(url, current, redirects, validation, "网页地址不允许读取。");
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                request.Headers.UserAgent.ParseAdd("JogeoaiArticleReader/1.0");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if ((int)response.StatusCode is 401 or 403 or 429)
                    return Failed(url, current, redirects, "SOURCE_BLOCKED", "网页拒绝了读取请求。", "blocked");
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is not null)
                {
                    if (hop == MaxRedirects) return Failed(url, current, redirects, "TOO_MANY_REDIRECTS", "网页重定向次数过多。");
                    string next;
                    try { next = NormalizeFetchUrl(new Uri(new Uri(current), response.Headers.Location).ToString()); }
                    catch (CollectorStoreException exception) { return Failed(url, current, redirects, exception.Code, "重定向地址不允许读取。"); }
                    catch (UriFormatException) { return Failed(url, current, redirects, "INVALID_SOURCE_URL", "重定向地址格式无效。"); }
                    current = next; redirects.Add(current); continue;
                }
                if (!response.IsSuccessStatusCode) return Failed(url, current, redirects, "SOURCE_UNREADABLE", "网页暂时无法读取。");
                var contentType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
                if (contentType is not ("text/html" or "text/plain" or "application/xhtml+xml"))
                    return Failed(url, current, redirects, "SOURCE_UNSUPPORTED_TYPE", "网页返回的内容类型不受支持。");
                if (response.Content.Headers.ContentLength is > MaxBytes)
                    return Failed(url, current, redirects, "SOURCE_TOO_LARGE", "网页内容超过大小限制。");
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var buffer = new MemoryStream(); var bytes = new byte[8192];
                while (buffer.Length <= MaxBytes)
                {
                    var read = await stream.ReadAsync(bytes.AsMemory(0, Math.Min(bytes.Length, MaxBytes + 1 - (int)buffer.Length)), timeout.Token);
                    if (read == 0) break;
                    await buffer.WriteAsync(bytes.AsMemory(0, read), timeout.Token);
                    if (buffer.Length > MaxBytes) return Failed(url, current, redirects, "SOURCE_TOO_LARGE", "网页内容超过大小限制。");
                }
                var text = Encoding.UTF8.GetString(buffer.ToArray());
                var title = ExtractTitle(text);
                var body = CleanText(contentType == "text/plain" ? text : Script.Replace(text, " "));
                if (body.Length == 0) return Failed(url, current, redirects, "SOURCE_EMPTY", "网页没有可读取的正文。");
                if (LooksLikeLoginOrScriptChallenge(title, body))
                    return Failed(url, current, redirects, "SOURCE_LOGIN_REQUIRED", "网页需要登录或启用脚本才能读取正文。");
                var normalized = DetailedAnalyticsRules.NormalizeUrl(current) ?? current;
                return new ArticleSourceReadResult(url, normalized,
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant(),
                    "ready", title, body, null, redirects, null, null);
            }
            return Failed(url, current, redirects, "SOURCE_UNREADABLE", "网页暂时无法读取。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed(url, current, redirects, "SOURCE_TIMEOUT", "网页读取超时。");
        }
        catch (HttpRequestException)
        {
            return Failed(url, current, redirects, "SOURCE_UNREADABLE", "网页暂时无法读取。");
        }
        finally
        {
            if (acquired) ReadSlots.Release();
        }
    }

    /// <summary>规范化并校验 URL 的无网络版本。</summary>
    public static string NormalizeAndValidate(string url)
    {
        var input = url?.Trim();
        if (string.IsNullOrWhiteSpace(input) || input.Length > 2_000
            || !Uri.TryCreate(input, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)
            || (uri.IsDefaultPort is false && uri.Port is not (80 or 443)))
            throw new CollectorStoreException("INVALID_SOURCE_URL", "参考链接必须是公开 HTTP(S) 地址。", 400);
        var normalized = DetailedAnalyticsRules.NormalizeUrl(uri.ToString())
            ?? throw new CollectorStoreException("INVALID_SOURCE_URL", "参考链接格式无效。", 400);
        return normalized;
    }

    /// <summary>校验抓取地址并保留实际主机名（小写、去片段与跟踪参数）；去 www 的去重规范化不能用于网络请求。</summary>
    public static string NormalizeFetchUrl(string url)
    {
        var validated = NormalizeAndValidate(url);
        var host = new Uri(url.Trim()).IdnHost.TrimEnd('.').ToLowerInvariant();
        return new UriBuilder(validated) { Host = host }.Uri.AbsoluteUri;
    }

    /// <summary>判断 IP 是否属于不可访问的本地、保留或云元数据网段。</summary>
    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return IsPublicAddress(address.MapToIPv4());
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return false;
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 16 && ((bytes[0] & 0xfe) == 0xfc || address.Equals(IPAddress.IPv6None)
            // Documentation, benchmarking and discard-only IPv6 ranges are
            // non-routable even though the platform may not classify them as
            // link-local/site-local.
            || bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8
            || bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x00 && bytes[3] == 0x02)) return false;
        if (bytes.Length == 4)
        {
            if (bytes[0] is 0 or 10 or 127 || bytes[0] >= 224) return false;
            if (bytes[0] == 100 && bytes[1] is >= 64 and <= 127) return false;
            if (bytes[0] == 169 && bytes[1] == 254) return false;
            if (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) return false;
            if (bytes[0] == 192 && bytes[1] == 168) return false;
            if (bytes[0] == 192 && bytes[1] == 0) return false;
            if (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2) return false;
            if (bytes[0] == 198 && bytes[1] is 18 or 19) return false;
            if (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100) return false;
            if (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113) return false;
        }
        return true;
    }

    /// <summary>
    /// 校验 URL 主机是否为公网地址：返回 null 表示允许；<c>SOURCE_PRIVATE_HOST</c> 表示本地 / 保留 / 内网主机；
    /// <c>SOURCE_DNS_FAILED</c> 表示解析失败。站点图标抓取（<c>SiteIconFetcher</c>）复用同一规则。
    /// </summary>
    public static Task<string?> ValidatePublicHostAsync(string url, CancellationToken cancellationToken) =>
        ValidatePublicHostAsync(url, IsPublicAddress, cancellationToken);

    /// <summary>与 <see cref="ValidatePublicHostAsync(string, CancellationToken)"/> 相同，但 IP 字面量与 DNS 结果按给定谓词判断。</summary>
    public static async Task<string?> ValidatePublicHostAsync(string url, Func<IPAddress, bool> isAllowed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(isAllowed);
        var uri = new Uri(url);
        var host = uri.DnsSafeHost.TrimEnd('.').ToLowerInvariant();
        if (IsReservedHostName(host)) return "SOURCE_PRIVATE_HOST";
        if (IPAddress.TryParse(host, out var address)) return isAllowed(address) ? null : "SOURCE_PRIVATE_HOST";
        IPAddress[] addresses;
        try { addresses = await Dns.GetHostAddressesAsync(host, cancellationToken); }
        catch { return "SOURCE_DNS_FAILED"; }
        return addresses.Length > 0 && addresses.All(isAllowed) ? null : "SOURCE_PRIVATE_HOST";
    }

    /// <summary>
    /// 不做 DNS 解析，判断网址主机是否为可交给外部服务读取的公开 DNS 域名：排除 IP 字面量、单标签主机，
    /// 以及 <see cref="ValidatePublicHostAsync(string, CancellationToken)"/> 同样拒绝的 localhost / .local / .internal 等保留名。
    /// </summary>
    /// <param name="url">绝对网址。</param>
    public static bool IsPublicDnsHostName(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.HostNameType != UriHostNameType.Dns) return false;
        var host = uri.DnsSafeHost.TrimEnd('.').ToLowerInvariant();
        return host.Contains('.', StringComparison.Ordinal) && !IsReservedHostName(host) && !IPAddress.TryParse(host, out _);
    }

    private static bool IsReservedHostName(string host)
        => host is "localhost" or "metadata.google.internal" || host.EndsWith(".localhost", StringComparison.Ordinal)
           || host.EndsWith(".local", StringComparison.Ordinal) || host.EndsWith(".internal", StringComparison.Ordinal);

    private static string CleanText(string value)
    {
        var decoded = WebUtility.HtmlDecode(Tags.Replace(value, " "));
        // Decode entities before the second pass so an encoded tag cannot be
        // carried into an imported Markdown body as active HTML.
        decoded = Tags.Replace(decoded, " ");
        return Whitespace.Replace(decoded, " ").Trim();
    }

    private static bool LooksLikeLoginOrScriptChallenge(string? title, string body)
    {
        var titleText = title?.Trim() ?? string.Empty;
        if (titleText.Contains("login", StringComparison.OrdinalIgnoreCase)
            || titleText.Contains("sign in", StringComparison.OrdinalIgnoreCase)
            || titleText.Contains("access denied", StringComparison.OrdinalIgnoreCase)) return true;
        return body.Length < 4_000 && LoginOrScriptChallenge.IsMatch(body);
    }

    private static string? ExtractTitle(string html)
    {
        var match = Regex.Match(html, "<title\\b[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? CleanText(match.Groups[1].Value)[..Math.Min(500, CleanText(match.Groups[1].Value).Length)] : null;
    }

    private static ArticleSourceReadResult Failed(string original, string normalized, IReadOnlyList<string> redirects,
        string code, string message, string status = "unreadable") => new(original, normalized,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant(), status,
            null, null, null, redirects, code, message);
}
