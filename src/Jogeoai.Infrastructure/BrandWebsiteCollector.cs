using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jogeoai.Application;
using Jogeoai.Domain;

namespace Jogeoai.Infrastructure;

/// <summary>有界收集官网首页、产品、案例和选型资料；不跟随站外链接。</summary>
public sealed class BrandWebsiteCollector(IHttpClientFactory clients)
{
    private const int MaxBytes = 768 * 1024;
    private const int MaxPages = 10;
    private static Regex Pattern(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline,
        TimeSpan.FromSeconds(1));
    private static readonly Regex Links = Pattern("<a\\b[^>]*href\\s*=\\s*[\"']([^\"']+)[\"'][^>]*>(.*?)</a>");
    private static readonly Regex Noise = Pattern("<(script|style|noscript|nav|footer|header|form|iframe)\\b[^>]*>.*?(?:</\\1>|$)");
    private static readonly Regex Tags = Pattern("<[^>]+>");
    private static readonly Regex Title = Pattern("<title\\b[^>]*>(.*?)</title>");
    private static readonly Regex Metadata = Pattern("<meta\\b[^>]*(?:name|property)=[\"'](?:description|og:description)[\"'][^>]*content=[\"']([^\"']*)[\"']");
    private static readonly Regex Structured = Pattern("<script\\b[^>]*type=[\"']application/ld\\+json[\"'][^>]*>(.*?)</script>");

    /// <summary>最多十页、深度二、并发三，总预算四十五秒；部分失败保留已读证据。</summary>
    public async Task<IReadOnlyList<ResearchEvidence>> CollectAsync(string website, string language,
        CancellationToken cancellationToken = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(45));
        var root = new Uri(NormalizeFetchUrl(website));
        var pending = new List<(Uri Url, int Depth, int Score, string Kind)> { (root, 0, 100, "home") };
        var kindCounts = new Dictionary<string, int>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pages = new List<ResearchEvidence>();
        var total = 0;
        while (pending.Count > 0 && visited.Count < MaxPages && !budget.IsCancellationRequested && total < 48000)
        {
            var batch = new List<(Uri Url, int Depth, int Score, string Kind)>();
            while (batch.Count < 3 && pending.Count > 0 && visited.Count < MaxPages)
            {
                // 平衡公司、产品与案例/指南，避免众多产品分类挤掉客户场景资料。
                var item = pending.OrderByDescending(p => p.Score - kindCounts.GetValueOrDefault(p.Kind) * 12).First();
                pending.Remove(item); visited.Add(item.Url.AbsoluteUri); batch.Add(item);
                kindCounts[item.Kind] = kindCounts.GetValueOrDefault(item.Kind) + 1;
            }
            var fetched = await Task.WhenAll(batch.Select(async item => (item, page: await TryReadAsync(item.Url, root, budget.Token))));
            foreach (var (item, page) in fetched)
            {
                if (page is null) continue;
                if (page.Text.Length >= 60 && pages.All(p => p.Url != page.Url.AbsoluteUri))
                {
                    var excerpt = page.Text[..Math.Min(page.Text.Length, Math.Min(6000, 48000 - total))];
                    total += excerpt.Length;
                    pages.Add(new($"web-{pages.Count + 1}", page.Url.AbsoluteUri, page.Title, excerpt,
                        ResearchContextRules.Hash(excerpt), DateTimeOffset.UtcNow));
                }
                if (item.Depth >= 2) continue;
                foreach (var link in page.Links)
                {
                    if (!Uri.TryCreate(page.Url, WebUtility.HtmlDecode(link.Href), out var uri) || !SameSite(root, uri)) continue;
                    try { uri = new Uri(NormalizeFetchUrl(new UriBuilder(uri) { Fragment = "", Query = "" }.Uri.ToString())); }
                    catch (CollectorStoreException) { continue; }
                    var score = Score(uri.AbsolutePath + " " + link.Text, language);
                    if (score == 0 || visited.Contains(uri.AbsoluteUri) || pending.Any(p => p.Url == uri)) continue;
                    pending.Add((uri, item.Depth + 1, score, Kind(uri.AbsolutePath + " " + link.Text)));
                }
                // 防止大站点导航让候选集合无界增长。
                pending = pending.OrderByDescending(p => p.Score).Take(100).ToList();
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return pages;
    }

    private async Task<Page?> TryReadAsync(Uri current, Uri root, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var client = clients.CreateClient("research-web");
            for (var hop = 0; hop <= 5; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                request.Headers.UserAgent.ParseAdd("JogeoaiBrandResearch/2.0");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is not null)
                {
                    if (hop == 5) return null;
                    current = new Uri(NormalizeFetchUrl(new Uri(current, response.Headers.Location).ToString()));
                    if (!SameSite(root, current)) return null;
                    continue;
                }
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType is not
                    ("text/html" or "text/plain" or "application/xhtml+xml")) return null;
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var content = new MemoryStream();
                var bytes = new byte[8192];
                while (content.Length < MaxBytes)
                {
                    var count = await stream.ReadAsync(bytes.AsMemory(0, Math.Min(bytes.Length, MaxBytes - (int)content.Length)), timeout.Token);
                    if (count == 0) break;
                    await content.WriteAsync(bytes.AsMemory(0, count), timeout.Token);
                }
                var html = Encoding.UTF8.GetString(content.ToArray());
                var title = Clean(Title.Match(html).Groups[1].Value);
                var description = Clean(Metadata.Match(html).Groups[1].Value);
                var structured = new List<string>();
                foreach (Match match in Structured.Matches(html).Take(8))
                {
                    try { using var json = JsonDocument.Parse(match.Groups[1].Value); ReadStructured(json.RootElement, structured, 0); }
                    catch (JsonException) { }
                }
                var text = Clean($"{title} {description} {Noise.Replace(html, " ")} {string.Join(' ', structured)}");
                if (Pattern("access denied|cf-chl-|enable javascript|verify you are human").IsMatch(text[..Math.Min(600, text.Length)])) return null;
                return new(current, title, text, Links.Matches(html).Take(500)
                    .Select(m => (m.Groups[1].Value, Clean(m.Groups[2].Value))).ToArray());
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or IOException
            or CollectorStoreException or UriFormatException or RegexMatchTimeoutException) { }
        return null;
    }

    private static void ReadStructured(JsonElement node, List<string> values, int depth)
    {
        if (depth > 5 || values.Count > 50) return;
        if (node.ValueKind == JsonValueKind.Array)
            foreach (var child in node.EnumerateArray().Take(20)) ReadStructured(child, values, depth + 1);
        if (node.ValueKind != JsonValueKind.Object) return;
        foreach (var property in node.EnumerateObject())
            if (property.Value.ValueKind == JsonValueKind.String && property.Name is "name" or "description" or "category" or "applicationCategory")
                values.Add(property.Value.GetString()![..Math.Min(1200, property.Value.GetString()!.Length)]);
            else if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                ReadStructured(property.Value, values, depth + 1);
    }

    /// <summary>校验抓取地址并保留实际主机名；统计用的去 www 规范化不能用于网络跳转。</summary>
    public static string NormalizeFetchUrl(string url) => ArticleSourceReader.NormalizeFetchUrl(url);
    private static string SiteHost(Uri uri) => uri.IdnHost.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.IdnHost[4..] : uri.IdnHost;
    private static bool SameSite(Uri root, Uri candidate) => candidate.Scheme is "http" or "https"
        && SiteHost(root).Equals(SiteHost(candidate), StringComparison.OrdinalIgnoreCase);
    private static string Clean(string html) => Regex.Replace(WebUtility.HtmlDecode(Tags.Replace(html, " ")), @"\s+", " ").Trim();
    private static int Score(string value, string language)
    {
        if (Pattern(@"\.(pdf|zip|jpg|png|mp4)\b|privacy|login|cart|certificate|honor|隐私|登录|资质|荣誉").IsMatch(value)) return 0;
        var score = Pattern("about|公司|关于").IsMatch(value) ? 30
            : Pattern("categor|分类|产品中心|product[s]?/?$").IsMatch(value) ? 28
            : Pattern("faq|guide|knowledge|case|solution|常见|选型|案例|解决方案").IsMatch(value) ? 24
            : Pattern("product|service|产品|服务").IsMatch(value) ? 20 : 0;
        if (score > 0 && language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) && Pattern("/zh/|/cn/|[\u4e00-\u9fff]").IsMatch(value)) score += 4;
        return score;
    }
    private static string Kind(string value) => Pattern("about|公司|关于").IsMatch(value) ? "about"
        : Pattern("faq|guide|knowledge|case|solution|常见|选型|案例|解决方案").IsMatch(value) ? "guide"
        : Pattern("categor|分类|产品中心").IsMatch(value) ? "category" : "product";
    private sealed record Page(Uri Url, string Title, string Text, IReadOnlyList<(string Href, string Text)> Links);
}
