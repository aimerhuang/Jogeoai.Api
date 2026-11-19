using System.Text;
using Jogeoai.Domain;

namespace Jogeoai.Tests;

/// <summary>站点图标纯函数规则：域名校验、魔数 / 宽度识别、HTML 图标链接解析与排序。</summary>
public sealed class SiteIconRulesTests
{
    [Theory]
    [InlineData("example.com", "example.com")]
    [InlineData("Example.COM.", "example.com")]
    [InlineData("  zy-cam.com ", "zy-cam.com")]
    [InlineData("a-b.co.uk", "a-b.co.uk")]
    [InlineData("xn--fiqs8s.cn", "xn--fiqs8s.cn")]
    [InlineData("www.sanyipace.com", "www.sanyipace.com")]
    public void AcceptsAndNormalizesPublicHostNames(string input, string expected)
    {
        Assert.True(SiteIconRules.TryNormalizeDomain(input, out var domain));
        Assert.Equal(expected, domain);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("localhost")]
    [InlineData("foo.localhost")]
    [InlineData("printer.local")]
    [InlineData("metadata.google.internal")]
    [InlineData("svc.internal")]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.8")]
    [InlineData("1.2.3.4")]
    [InlineData("127.1")]
    [InlineData("::1")]
    [InlineData("[::1]")]
    [InlineData("example")]
    [InlineData("例子.com")]
    [InlineData("bücher.de")]
    [InlineData("exa mple.com")]
    [InlineData("exa_mple.com")]
    [InlineData("-bad.com")]
    [InlineData("bad-.com")]
    [InlineData("a..com")]
    [InlineData(".example.com")]
    [InlineData("example.com/path")]
    [InlineData("example.com:8080")]
    [InlineData("user@example.com")]
    [InlineData("example.com..")]
    public void RejectsIpLiteralsLocalNamesAndMalformedHosts(string? input)
    {
        Assert.False(SiteIconRules.TryNormalizeDomain(input, out var domain));
        Assert.Null(domain);
    }

    [Fact]
    public void RejectsOverlongDomainsAndLabels()
    {
        var label = new string('a', 63);
        var longest = string.Join('.', label, label, label, new string('b', 61)); // 253
        Assert.Equal(253, longest.Length);
        Assert.True(SiteIconRules.TryNormalizeDomain(longest, out _));
        Assert.False(SiteIconRules.TryNormalizeDomain(longest + "c", out _));
        Assert.False(SiteIconRules.TryNormalizeDomain(new string('a', 64) + ".com", out _));
    }

    [Fact]
    public void DetectsSupportedBitmapsByMagicBytes()
    {
        Assert.Equal("image/png", SiteIconRules.DetectContentType(SiteIconTestImages.Png(32)));
        Assert.Equal("image/x-icon", SiteIconRules.DetectContentType(SiteIconTestImages.Ico(16, 32)));
        Assert.Equal("image/gif", SiteIconRules.DetectContentType(SiteIconTestImages.Gif(16)));
        Assert.Equal("image/jpeg", SiteIconRules.DetectContentType(SiteIconTestImages.Jpeg(40)));
        Assert.Equal("image/webp", SiteIconRules.DetectContentType(SiteIconTestImages.WebpVp8X(64)));
        Assert.Equal("image/bmp", SiteIconRules.DetectContentType(SiteIconTestImages.Bmp(24)));
    }

    [Theory]
    [InlineData("<!DOCTYPE html><html><head><title>x</title></head></html>")]
    [InlineData("<html><body>Not found</body></html>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>")]
    [InlineData("<?xml version=\"1.0\"?><svg xmlns=\"http://www.w3.org/2000/svg\"></svg>")]
    [InlineData("{\"error\":\"not found\"}")]
    [InlineData("BM")]
    [InlineData("")]
    public void RejectsHtmlSvgAndUnknownContent(string body)
    {
        Assert.Null(SiteIconRules.DetectContentType(Encoding.UTF8.GetBytes(body)));
    }

    [Fact]
    public void ReadsWidthsForPlaceholderDetection()
    {
        Assert.Equal(32, SiteIconRules.ReadWidth(SiteIconTestImages.Png(32)));
        Assert.Equal(16, SiteIconRules.ReadWidth(SiteIconTestImages.Png(16)));
        Assert.Equal(48, SiteIconRules.ReadWidth(SiteIconTestImages.Ico(16, 48)));
        Assert.Equal(256, SiteIconRules.ReadWidth(SiteIconTestImages.Ico(0)));
        Assert.Equal(16, SiteIconRules.ReadWidth(SiteIconTestImages.Gif(16)));
        Assert.Equal(24, SiteIconRules.ReadWidth(SiteIconTestImages.Bmp(24)));
        Assert.Equal(40, SiteIconRules.ReadWidth(SiteIconTestImages.Jpeg(40)));
        Assert.Equal(64, SiteIconRules.ReadWidth(SiteIconTestImages.WebpVp8X(64)));
        Assert.Null(SiteIconRules.ReadWidth(Encoding.UTF8.GetBytes("<html></html>")));
    }

    [Fact]
    public void ResolvesRelativeProtocolRelativeAndAbsoluteIconLinks()
    {
        var page = new Uri("https://www.example.com/zh/index.html");
        Assert.Equal("https://www.example.com/zh/static/favicon.png",
            Assert.Single(SiteIconRules.ParseIconLinks("<link rel=\"icon\" href=\"static/favicon.png\">", page)).AbsoluteUri);
        Assert.Equal("https://www.example.com/favicon.ico",
            Assert.Single(SiteIconRules.ParseIconLinks("<LINK REL='SHORTCUT ICON' HREF='/favicon.ico'>", page)).AbsoluteUri);
        Assert.Equal("https://cdn.example.net/a/icon.png",
            Assert.Single(SiteIconRules.ParseIconLinks("<link href=//cdn.example.net/a/icon.png rel=icon>", page)).AbsoluteUri);
        Assert.Equal("http://img.example.org/i.ico",
            Assert.Single(SiteIconRules.ParseIconLinks("<link rel=\"icon\" href=\"http://img.example.org/i.ico\" />", page)).AbsoluteUri);
        Assert.Equal("https://www.example.com/x.png?v=1&s=2",
            Assert.Single(SiteIconRules.ParseIconLinks("<link rel=\"icon\" href=\"/x.png?v=1&amp;s=2\">", page)).AbsoluteUri);
        var httpPage = new Uri("http://plain.example.com/");
        Assert.Equal("http://cdn.example.net/i.png",
            Assert.Single(SiteIconRules.ParseIconLinks("<link rel=icon href=\"//cdn.example.net/i.png\">", httpPage)).AbsoluteUri);
    }

    [Fact]
    public void DropsDataSvgMaskAndNonIconLinks()
    {
        var page = new Uri("https://example.com/");
        const string html = """
            <link rel="stylesheet" href="/site.css">
            <link rel="icon" href="data:image/png;base64,AAAA">
            <link rel="icon" type="image/svg+xml" href="/icon-any">
            <link rel="icon" href="/logo.svg">
            <link rel="mask-icon" href="/mask.png" color="#000">
            <link rel="preload" href="/favicon.png" as="image">
            <!-- <link rel="icon" href="/commented.png"> -->
            <link rel="icon" href="javascript:alert(1)">
            <link rel="icon" href="ftp://example.com/i.ico">
            """;
        Assert.Empty(SiteIconRules.ParseIconLinks(html, page));
    }

    [Fact]
    public void RanksExplicitBitmapsNearestSixtyFourThenIconsThenAppleTouchAndKeepsThree()
    {
        var page = new Uri("https://example.com/");
        const string html = """
            <link rel="apple-touch-icon" sizes="180x180" href="/apple.png">
            <link rel="icon" href="/generic">
            <link rel="icon" type="image/png" sizes="16x16" href="/16.png">
            <link rel="icon" type="image/png" sizes="192x192" href="/192.png">
            <link rel="icon" type="image/png" sizes="32x32 64x64" href="/multi.png">
            <link rel="shortcut icon" href="/favicon.ico">
            """;
        var links = SiteIconRules.ParseIconLinks(html, page, max: 10).Select(url => url.AbsolutePath).ToArray();
        Assert.Equal(["/multi.png", "/16.png", "/192.png", "/favicon.ico", "/generic", "/apple.png"], links);
        Assert.Equal(["/multi.png", "/16.png", "/192.png"], SiteIconRules.ParseIconLinks(html, page).Select(url => url.AbsolutePath));
    }

    [Fact]
    public void DeduplicatesRepeatedLinks()
    {
        var page = new Uri("https://example.com/");
        var links = SiteIconRules.ParseIconLinks("<link rel=icon href=/f.ico><link rel='shortcut icon' href='https://example.com/f.ico'>", page);
        Assert.Single(links);
    }

    [Theory]
    [InlineData("example.com", true)]
    [InlineData("sz-water.com.cn", true)]
    [InlineData("ndt-instruments.co.id", true)]
    [InlineData("ubuti.com.tw", true)]
    [InlineData("ln.gov.cn", true)]
    [InlineData("a-b.co.uk", true)]
    [InlineData("xx.ac.jp", true)]
    [InlineData("www.example.com", false)]
    [InlineData("cg.sz-water.com.cn", false)]
    [InlineData("qxb-pdf-osscache.qixin.com", false)]
    [InlineData("a.b.example.com", false)]
    [InlineData("shop.example.io", false)]
    [InlineData("foo.biz.cn", false)]
    [InlineData("foo.com.c1", false)]
    [InlineData("foo.com.com", false)]
    public void RegistrableDomainHeuristic(string domain, bool expected) =>
        Assert.Equal(expected, SiteIconRules.IsRegistrableDomain(domain));

    [Theory]
    [InlineData("cg.sz-water.com.cn", "sz-water.com.cn")]
    [InlineData("qxb-pdf-osscache.qixin.com", "qixin.com")]
    [InlineData("a.b.example.com", "b.example.com")]
    [InlineData("b.example.com", "example.com")]
    [InlineData("zjt.ln.gov.cn", "ln.gov.cn")]
    [InlineData("www.ndt-instruments.co.id", "ndt-instruments.co.id")]
    [InlineData("www.example.com", "example.com")]
    [InlineData("example.com", null)]
    [InlineData("sz-water.com.cn", null)]
    [InlineData("ln.gov.cn", null)]
    [InlineData("ubuti.com.tw", null)]
    public void ParentCandidateStopsAtTheRegistrableDomain(string domain, string? expected) =>
        Assert.Equal(expected, SiteIconRules.ParentCandidate(domain));

    [Fact]
    public void ParentChainNeverReachesAPublicSuffix()
    {
        var chain = new List<string>();
        for (var current = SiteIconRules.ParentCandidate("x.y.cg.sz-water.com.cn"); current is not null; current = SiteIconRules.ParentCandidate(current))
            chain.Add(current);
        Assert.Equal(["y.cg.sz-water.com.cn", "cg.sz-water.com.cn", "sz-water.com.cn"], chain);
    }

    [Fact]
    public void Migration029WidensTheSourceCheckReplayablyForYandex()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../db/migrations/029_site_icons_yandex.sql"));
        Assert.True(File.Exists(path), $"找不到 migration：{path}");
        var sql = File.ReadAllText(path);
        Assert.Contains("ck_site_icons_source", sql, StringComparison.Ordinal);
        Assert.Contains("check_clause NOT LIKE '%yandex%'", sql, StringComparison.Ordinal);
        Assert.Contains("'html', 'favicon', 'google', 'duckduckgo', 'yandex'", sql, StringComparison.Ordinal);
        Assert.Contains("IF NOT EXISTS", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DELETE FROM", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP TABLE", sql, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>构造最小的图片头字节，用于魔数与宽度识别测试。</summary>
internal static class SiteIconTestImages
{
    public static byte[] Png(int width, int height = -1, int padding = 0)
    {
        height = height < 0 ? width : height;
        var bytes = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' };
        bytes.AddRange(BigEndian(width));
        bytes.AddRange(BigEndian(height));
        bytes.AddRange([8, 6, 0, 0, 0, 0, 0, 0, 0]);
        bytes.AddRange(new byte[padding]);
        return [.. bytes];
    }

    public static byte[] Ico(params int[] widths)
    {
        var bytes = new List<byte> { 0, 0, 1, 0, (byte)widths.Length, 0 };
        foreach (var width in widths)
        {
            bytes.Add((byte)width);
            bytes.Add((byte)width);
            bytes.AddRange(new byte[14]);
        }
        bytes.AddRange(new byte[8]);
        return [.. bytes];
    }

    public static byte[] Gif(int width) => [(byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a',
        (byte)width, (byte)(width >> 8), 16, 0, 0, 0, 0];

    public static byte[] Jpeg(int width) =>
    [
        0xFF, 0xD8,
        0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0,
        0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x20, (byte)(width >> 8), (byte)width, 0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    ];

    public static byte[] WebpVp8X(int width)
    {
        var bytes = new byte[32];
        "RIFF"u8.CopyTo(bytes);
        "WEBP"u8.CopyTo(bytes.AsSpan(8));
        "VP8X"u8.CopyTo(bytes.AsSpan(12));
        var w = width - 1;
        bytes[24] = (byte)w; bytes[25] = (byte)(w >> 8); bytes[26] = (byte)(w >> 16);
        return bytes;
    }

    public static byte[] Bmp(int width)
    {
        var bytes = new byte[54];
        bytes[0] = (byte)'B'; bytes[1] = (byte)'M';
        bytes[14] = 40;
        bytes[18] = (byte)width; bytes[19] = (byte)(width >> 8);
        bytes[22] = (byte)width;
        return bytes;
    }

    private static byte[] BigEndian(int value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
}
