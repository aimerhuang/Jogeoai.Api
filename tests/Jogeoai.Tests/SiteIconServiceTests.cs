using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Jogeoai.Tests;

/// <summary>站点图标获取编排：fake HttpMessageHandler + 内存 store + fake 主机校验，不访问 DNS 或外网。</summary>
public sealed class SiteIconServiceTests
{
    private const string Domain = "zy-cam.com";
    private static readonly string Google = SiteIconFetcher.ThirdPartyUrl("google", Domain).AbsoluteUri;
    private static readonly string Duck = SiteIconFetcher.ThirdPartyUrl("duckduckgo", Domain).AbsoluteUri;
    private static readonly string Yandex = SiteIconFetcher.ThirdPartyUrl("yandex", Domain).AbsoluteUri;
    private const string Www = "www." + Domain;

    [Fact]
    public async Task FreshCacheEntriesAreReturnedWithoutOutboundRequests()
    {
        var f = new Fixture();
        f.Store.Seed(new SiteIconRecord(Domain, SiteIconStatus.Found, "image/png", SiteIconTestImages.Png(32), "html",
            f.Now.AddDays(-1), f.Now.AddDays(10)));
        f.Store.Seed(new SiteIconRecord("inspectioncamera.cn", SiteIconStatus.Missing, null, null, null, f.Now.AddDays(-1), f.Now.AddDays(2)));

        var found = await f.Service.GetAsync("ZY-CAM.com.");
        var missing = await f.Service.GetAsync("inspectioncamera.cn");

        Assert.True(found.IsFound);
        Assert.False(missing.IsFound);
        Assert.Equal(0, f.Handler.Count);
        Assert.Equal(0, f.Store.Saves);
    }

    [Fact]
    public async Task DomainAliasesServeTheTargetIconForTheDomainAndItsSubdomains()
    {
        var f = new Fixture(o => o.DomainAliases = new() { ["IESDOUYIN.com"] = "douyin.com.", ["bad_host"] = "douyin.com" });
        f.Store.Seed(new SiteIconRecord("douyin.com", SiteIconStatus.Found, "image/png", SiteIconTestImages.Png(64), "google",
            f.Now.AddDays(-1), f.Now.AddDays(10)));

        foreach (var domain in new[] { "iesdouyin.com", "www.iesdouyin.com", "m.share.iesdouyin.com" })
        {
            var icon = await f.Service.GetAsync(domain);
            Assert.Equal(("douyin.com", true), (icon.Domain, icon.IsFound));
        }
        Assert.Null(f.Store.Get("iesdouyin.com"));
        Assert.Equal("notiesdouyin.com", f.Service.ResolveAlias("notiesdouyin.com"));
        Assert.Equal("douyin.com", f.Service.ResolveAlias("douyin.com"));
        Assert.Equal(0, f.Handler.Count);
    }

    [Fact]
    public async Task InvalidDomainsAreRejectedBeforeAnyLookup()
    {
        var f = new Fixture();
        foreach (var domain in new[] { "127.0.0.1", "localhost", "svc.internal", "bad_host.com", new string('a', 250) + ".com" })
        {
            var error = await Assert.ThrowsAsync<CollectorStoreException>(() => f.Service.GetAsync(domain));
            Assert.Equal(("INVALID_SITE_DOMAIN", 400), (error.Code, error.StatusCode));
        }
        Assert.Equal(0, f.Handler.Count);
        Assert.Equal(0, f.Store.Reads);
    }

    [Fact]
    public async Task HtmlDeclaredIconWinsAndResolvesProtocolRelativeCdn()
    {
        var f = new Fixture();
        f.Handler.Html($"https://{Domain}/", "<head><link rel=\"icon\" href=\"//cdn.example.net/fav.png\"></head>");
        f.Handler.Bytes("https://cdn.example.net/fav.png", SiteIconTestImages.Png(48), "image/png");

        var record = await f.Service.GetAsync(Domain);

        Assert.True(record.IsFound);
        Assert.Equal(("image/png", "html"), (record.ContentType, record.Source));
        Assert.Equal(SiteIconTestImages.Png(48), record.Bytes);
        Assert.Equal(f.Now.AddDays(30), record.ExpiresAt);
        Assert.Equal([$"https://{Domain}/", "https://cdn.example.net/fav.png"], f.Handler.Urls);
        Assert.Equal(record, f.Store.Get(Domain));
    }

    [Fact]
    public async Task TriesHtmlThenFaviconThenGoogleThenDuckDuckGoAndRejectsGooglePlaceholder()
    {
        var f = new Fixture();
        f.Handler.Html($"https://{Domain}/", "<link rel=\"icon\" href=\"/icon.png\">");
        f.Handler.Status($"https://{Domain}/icon.png", HttpStatusCode.NotFound);
        f.Handler.Status($"https://{Domain}/favicon.ico", HttpStatusCode.NotFound);
        f.Handler.Bytes(Google, SiteIconTestImages.Png(16), "image/png");
        f.Handler.Bytes(Duck, SiteIconTestImages.Ico(32), "image/x-icon");

        var record = await f.Service.GetAsync(Domain);

        Assert.Equal(("duckduckgo", "image/x-icon"), (record.Source, record.ContentType));
        Assert.Equal([$"https://{Domain}/", $"https://{Domain}/icon.png", $"https://{Domain}/favicon.ico",
            $"https://{Www}/", $"http://{Www}/", Google, Duck], f.Handler.Urls);
        Assert.StartsWith("https://t1.gstatic.com/faviconV2?", Google);
        Assert.Contains("fallback_opts=TYPE,SIZE,URL", Google);
        Assert.Contains("size=64", Google);
        Assert.Equal($"https://icons.duckduckgo.com/ip3/{Domain}.ico", Duck);
    }

    [Fact]
    public async Task GoogleIconLargerThanPlaceholderIsAccepted()
    {
        var f = new Fixture();
        f.Handler.Status($"https://{Domain}/", HttpStatusCode.Forbidden);
        f.Handler.Status($"https://{Domain}/favicon.ico", HttpStatusCode.NotFound);
        f.Handler.Bytes(Google, SiteIconTestImages.Png(64), "image/png");

        var record = await f.Service.GetAsync(Domain);

        Assert.Equal("google", record.Source);
        Assert.DoesNotContain(Duck, f.Handler.Urls);
    }

    [Fact]
    public async Task PlaceholderAndDuckDuckGo404AreCachedAsMissingForSevenDays()
    {
        var f = new Fixture();
        f.Handler.Html($"https://{Domain}/", "<html><head></head></html>");
        f.Handler.Status($"https://{Domain}/favicon.ico", HttpStatusCode.NotFound);
        f.Handler.Bytes(Google, SiteIconTestImages.Png(16), "image/png");
        f.Handler.Status(Duck, HttpStatusCode.NotFound);

        var record = await f.Service.GetAsync(Domain);

        Assert.Equal(SiteIconStatus.Missing, record.Status);
        Assert.Null(record.Bytes);
        Assert.Equal(f.Now.AddDays(7), record.ExpiresAt);
        var requests = f.Handler.Count;
        var again = await f.Service.GetAsync(Domain);
        Assert.Equal(SiteIconStatus.Missing, again.Status);
        Assert.Equal(requests, f.Handler.Count);
    }

    [Fact]
    public async Task DisabledThirdPartyIsNeverRequestedAndNetworkOnlyFailureUsesShortTtl()
    {
        var f = new Fixture(options => options.ThirdParty.Enabled = false);

        var record = await f.Service.GetAsync(Domain);

        Assert.Equal(SiteIconStatus.Missing, record.Status);
        Assert.Equal(f.Now.AddHours(24), record.ExpiresAt);
        Assert.Equal([$"https://{Domain}/", $"http://{Domain}/", $"https://{Www}/", $"http://{Www}/"], f.Handler.Urls);
        Assert.DoesNotContain(f.Handler.Urls, url => url.Contains("gstatic") || url.Contains("duckduckgo") || url.Contains("yandex"));
    }

    [Fact]
    public async Task ProviderListIsConfigurable()
    {
        var f = new Fixture(options => options.ThirdParty.Providers = ["duckduckgo"]);
        f.Handler.Bytes(Duck, SiteIconTestImages.Png(32), "image/png");

        var record = await f.Service.GetAsync(Domain);

        Assert.Equal("duckduckgo", record.Source);
        Assert.DoesNotContain(Google, f.Handler.Urls);
    }

    [Fact]
    public async Task HttpsFailureFallsBackToHttpHomepage()
    {
        var f = new Fixture(options => options.ThirdParty.Enabled = false);
        f.Handler.Html($"http://{Domain}/", "<link rel=icon href=/i.ico>");
        f.Handler.Bytes($"http://{Domain}/i.ico", SiteIconTestImages.Ico(32), "image/x-icon");

        var record = await f.Service.GetAsync(Domain);

        Assert.Equal("html", record.Source);
        Assert.Equal([$"https://{Domain}/", $"http://{Domain}/", $"http://{Domain}/i.ico"], f.Handler.Urls);
    }

    [Fact]
    public async Task HtmlServedAsFaviconIsRejected()
    {
        var f = new Fixture(options => options.ThirdParty.Enabled = false);
        f.Handler.Html("https://wohlerusa.com/", "<html><body>home</body></html>");
        f.Handler.Bytes("https://wohlerusa.com/favicon.ico", Encoding.UTF8.GetBytes("<!DOCTYPE html><html><body>Page</body></html>"), "image/x-icon");

        var record = await f.Service.GetAsync("wohlerusa.com");

        Assert.Equal(SiteIconStatus.Missing, record.Status);
        Assert.Equal(f.Now.AddDays(7), record.ExpiresAt);
    }

    [Fact]
    public async Task SvgIconsAreRejectedEvenWithImageContentType()
    {
        var f = new Fixture(options => options.ThirdParty.Enabled = false);
        f.Handler.Status($"https://{Domain}/", HttpStatusCode.NotFound);
        f.Handler.Bytes($"https://{Domain}/favicon.ico", Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"/>"), "image/svg+xml");

        Assert.Equal(SiteIconStatus.Missing, (await f.Service.GetAsync(Domain)).Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OversizedIconsAreRejectedWithOrWithoutContentLength(bool declaredLength)
    {
        var f = new Fixture(options => { options.ThirdParty.Enabled = false; options.MaxIconBytes = 102_400; });
        f.Handler.Status($"https://{Domain}/", HttpStatusCode.NotFound);
        f.Handler.Bytes($"https://{Domain}/favicon.ico", SiteIconTestImages.Png(32, padding: 120_000), "image/png", declaredLength);

        var record = await f.Service.GetAsync(Domain);

        Assert.Equal(SiteIconStatus.Missing, record.Status);
        Assert.Equal(f.Now.AddDays(7), record.ExpiresAt);
    }

    [Fact]
    public async Task IconJustUnderTheLimitIsAccepted()
    {
        var f = new Fixture(options => { options.ThirdParty.Enabled = false; options.MaxIconBytes = 102_400; });
        f.Handler.Status($"https://{Domain}/", HttpStatusCode.NotFound);
        var icon = SiteIconTestImages.Png(32, padding: 100_000);
        f.Handler.Bytes($"https://{Domain}/favicon.ico", icon, "image/png", declaredLength: false);

        var record = await f.Service.GetAsync(Domain);

        Assert.Equal(("favicon", icon.Length), (record.Source, record.Bytes!.Length));
    }

    [Fact]
    public async Task RedirectsAreFollowedWithEveryHopRevalidatedAndPrivateTargetsRefused()
    {
        var f = new Fixture(options => options.ThirdParty.Enabled = false);
        f.Guard.Private.Add("intranet.corp");
        f.Handler.Html($"https://{Domain}/", "<link rel=icon href=\"http://intranet.corp/icon.png\">");
        f.Handler.Redirect($"https://{Domain}/favicon.ico", "https://static.example.net/a.ico");
        f.Handler.Redirect("https://static.example.net/a.ico", "https://intranet.corp/b.ico");

        var record = await f.Service.GetAsync(Domain);

        Assert.Equal(SiteIconStatus.Missing, record.Status);
        Assert.DoesNotContain(f.Handler.Urls, url => url.Contains("intranet.corp"));
        Assert.Contains("intranet.corp", f.Guard.Checked);
        Assert.Contains("static.example.net", f.Guard.Checked);

        var g = new Fixture(options => options.ThirdParty.Enabled = false);
        g.Handler.Status($"https://{Domain}/", HttpStatusCode.NotFound);
        g.Handler.Redirect($"https://{Domain}/favicon.ico", "/assets/favicon.ico");
        g.Handler.Bytes($"https://{Domain}/assets/favicon.ico", SiteIconTestImages.Ico(32), "image/x-icon");
        Assert.Equal("favicon", (await g.Service.GetAsync(Domain)).Source);
    }

    [Fact]
    public async Task HomepageRedirectingToPrivateHostIsNotFollowed()
    {
        var f = new Fixture(options => options.ThirdParty.Enabled = false);
        f.Guard.Private.Add("10.0.0.5");
        f.Handler.Redirect($"https://{Domain}/", "http://10.0.0.5/");
        f.Handler.Status($"https://{Domain}/favicon.ico", HttpStatusCode.NotFound);

        await f.Service.GetAsync(Domain);

        Assert.DoesNotContain(f.Handler.Urls, url => url.Contains("10.0.0.5"));
    }

    [Fact]
    public async Task TooManyRedirectsAreRefused()
    {
        var f = new Fixture(options => options.ThirdParty.Enabled = false);
        f.Handler.Status($"https://{Domain}/", HttpStatusCode.NotFound);
        for (var i = 0; i < 10; i++)
            f.Handler.Redirect(i == 0 ? $"https://{Domain}/favicon.ico" : $"https://{Domain}/r{i}", $"https://{Domain}/r{i + 1}");

        var record = await f.Service.GetAsync(Domain);

        Assert.Equal(SiteIconStatus.Missing, record.Status);
        // 首页 + 6 跳重定向，之后 www 兜底的 https / http 首页各一次（均连接失败）。
        Assert.Equal(1 + 6 + 2, f.Handler.Count);
    }

    [Fact]
    public async Task ExpiredFoundIconIsRetainedWhenRefetchFailsOnlyTransiently()
    {
        var f = new Fixture();
        var old = new SiteIconRecord(Domain, SiteIconStatus.Found, "image/png", SiteIconTestImages.Png(32), "favicon",
            f.Now.AddDays(-40), f.Now.AddDays(-10));
        f.Store.Seed(old);

        var record = await f.Service.GetAsync(Domain);

        Assert.True(record.IsFound);
        Assert.Equal(old.Bytes, record.Bytes);
        Assert.Equal(old.FetchedAt, record.FetchedAt);
        Assert.Equal(f.Now.AddHours(24), record.ExpiresAt);
        Assert.Equal(record, f.Store.Get(Domain));
        Assert.True(f.Handler.Count > 0);
    }

    [Fact]
    public async Task ExpiredFoundIconIsReplacedWhenSourcesDefinitelyHaveNone()
    {
        var f = new Fixture(options => options.ThirdParty.Enabled = false);
        f.Store.Seed(new SiteIconRecord(Domain, SiteIconStatus.Found, "image/png", SiteIconTestImages.Png(32), "favicon",
            f.Now.AddDays(-40), f.Now.AddDays(-10)));
        f.Handler.Html($"https://{Domain}/", "<html></html>");
        f.Handler.Status($"https://{Domain}/favicon.ico", HttpStatusCode.NotFound);

        var record = await f.Service.GetAsync(Domain);

        Assert.Equal(SiteIconStatus.Missing, record.Status);
        Assert.Equal(f.Now.AddDays(7), record.ExpiresAt);
    }

    [Fact]
    public async Task ConcurrentRequestsForOneDomainFetchOnce()
    {
        var f = new Fixture(options => options.ThirdParty.Enabled = false);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Handler.Route($"https://{Domain}/", async _ =>
        {
            await gate.Task;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<link rel=icon href=/a.png>", Encoding.UTF8, "text/html") };
        });
        f.Handler.Bytes($"https://{Domain}/a.png", SiteIconTestImages.Png(32), "image/png");

        var calls = Enumerable.Range(0, 12).Select(_ => Task.Run(() => f.Service.GetAsync(Domain))).ToArray();
        await f.Handler.WaitForAsync($"https://{Domain}/");
        await Task.Delay(100);
        gate.SetResult();
        var records = await Task.WhenAll(calls);

        Assert.All(records, record => Assert.Equal("html", record.Source));
        Assert.Equal(1, f.Handler.CountOf($"https://{Domain}/"));
        Assert.Equal(1, f.Handler.CountOf($"https://{Domain}/a.png"));
        Assert.Equal(1, f.Store.Saves);
    }

    [Fact]
    public async Task UnresolvedHostsCountAsTransientFailures()
    {
        var f = new Fixture(options => options.ThirdParty.Enabled = false);
        f.Guard.Unresolved.Add(Domain);
        f.Guard.Unresolved.Add(Www);

        var record = await f.Service.GetAsync(Domain);

        Assert.Equal(f.Now.AddHours(24), record.ExpiresAt);
        Assert.Equal(0, f.Handler.Count);
    }

    [Fact]
    public async Task WwwFallbackIsUsedWhenTheApexHomepageIsUnreachable()
    {
        var f = new Fixture(options => options.ThirdParty.Enabled = false);
        f.Handler.Html($"https://{Www}/", "<link rel=icon href=/w.png>");
        f.Handler.Bytes($"https://{Www}/w.png", SiteIconTestImages.Png(32), "image/png");

        var record = await f.Service.GetAsync(Domain);

        Assert.Equal((Domain, "html"), (record.Domain, record.Source));
        Assert.Equal([$"https://{Domain}/", $"http://{Domain}/", $"https://{Www}/", $"https://{Www}/w.png"], f.Handler.Urls);
        Assert.Equal(record, f.Store.Get(Domain));
        Assert.Null(f.Store.Get(Www));
    }

    [Fact]
    public async Task WwwFallbackIsUsedWhenTheApexHasNoIcon()
    {
        var f = new Fixture();
        f.Handler.Html($"https://{Domain}/", "<html><head></head></html>");
        f.Handler.Status($"https://{Domain}/favicon.ico", HttpStatusCode.NotFound);
        f.Handler.Status($"https://{Www}/", HttpStatusCode.NotFound);
        f.Handler.Bytes($"https://{Www}/favicon.ico", SiteIconTestImages.Ico(32), "image/x-icon");

        var record = await f.Service.GetAsync(Domain);

        Assert.Equal("favicon", record.Source);
        Assert.Equal([$"https://{Domain}/", $"https://{Domain}/favicon.ico", $"https://{Www}/", $"https://{Www}/favicon.ico"], f.Handler.Urls);
        Assert.DoesNotContain(f.Handler.Urls, url => url.Contains("gstatic"));
    }

    [Fact]
    public async Task WwwFallbackIsSkippedWhenTheApexAlreadyRedirectedToWww()
    {
        var f = new Fixture(options => options.ThirdParty.Enabled = false);
        f.Handler.Redirect($"https://{Domain}/", $"https://{Www}/");
        f.Handler.Html($"https://{Www}/", "<html></html>");
        f.Handler.Status($"https://{Www}/favicon.ico", HttpStatusCode.NotFound);

        var record = await f.Service.GetAsync(Domain);

        Assert.Equal(SiteIconStatus.Missing, record.Status);
        Assert.Equal([$"https://{Domain}/", $"https://{Www}/", $"https://{Www}/favicon.ico"], f.Handler.Urls);
    }

    [Theory]
    [InlineData("shop.zy-cam.com")]
    [InlineData("www.zy-cam.com")]
    [InlineData("cg.sz-water.com.cn")]
    public void WwwFallbackOnlyAppliesToRegistrableDomainsWithoutWww(string domain) =>
        Assert.False(SiteIconFetcher.ShouldTryWww(domain));

    [Fact]
    public async Task WwwIsNotTriedForSubdomainsOrWwwInput()
    {
        var f = new Fixture(options => options.ThirdParty.Enabled = false);
        f.Store.Seed(new SiteIconRecord(Domain, SiteIconStatus.Missing, null, null, null, f.Now.AddDays(-1), f.Now.AddDays(2)));

        await f.Service.GetAsync("shop.zy-cam.com");
        await f.Service.GetAsync(Www);

        Assert.Equal(["https://shop.zy-cam.com/", "http://shop.zy-cam.com/", $"https://{Www}/", $"http://{Www}/"], f.Handler.Urls);
        Assert.DoesNotContain(f.Handler.Urls, url => url.Contains("www.shop.") || url.Contains("www.www."));
        Assert.True(SiteIconFetcher.ShouldTryWww(Domain));
    }

    [Fact]
    public async Task WwwFallbackIsSkippedWhenTheOwnSiteBudgetIsSpent()
    {
        // 总预算 3s、单次 1s、一个第三方 → 站点自身 2s；apex 两次首页各超时 1s 后剩余不足一次请求，不再试 www。
        var f = new Fixture(options =>
        {
            options.RequestTimeoutSeconds = 1;
            options.TotalBudgetSeconds = 3;
            options.ThirdParty.Providers = ["duckduckgo"];
        });
        f.Handler.Hang($"https://{Domain}/");
        f.Handler.Hang($"http://{Domain}/");
        f.Handler.Bytes(Duck, SiteIconTestImages.Png(32), "image/png");

        var record = await f.Service.GetAsync(Domain).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("duckduckgo", record.Source);
        Assert.DoesNotContain(f.Handler.Urls, url => url.Contains(Www));
    }

    [Theory]
    [InlineData(25, 5, 3, 15)]
    [InlineData(25, 5, 2, 15)]
    [InlineData(25, 5, 1, 20)]
    [InlineData(25, 5, 0, 25)]
    [InlineData(8, 5, 2, 3)]
    [InlineData(5, 5, 2, 5)]
    public void ThirdPartyReserveIsRequestTimeoutTimesAtMostTwoProviders(int total, int timeout, int providers, int expected) =>
        Assert.Equal(TimeSpan.FromSeconds(expected),
            SiteIconFetcher.SiteBudget(TimeSpan.FromSeconds(total), TimeSpan.FromSeconds(timeout), providers));

    [Fact]
    public void DefaultsAreTwentyFiveSecondBudgetAndGoogleDuckDuckGoYandex()
    {
        var options = new SiteIconOptions();
        Assert.Equal(25, options.TotalBudgetSeconds);
        Assert.Equal(["google", "duckduckgo", "yandex"], options.ThirdParty.EffectiveProviders());
        Assert.Equal(["yandex", "google"], new SiteIconThirdPartyOptions { Providers = ["Yandex", "icon.horse", "google", "yandex"] }.EffectiveProviders());
        Assert.Equal($"https://favicon.yandex.net/favicon/v2/{Domain}?size=32", Yandex);
    }

    [Fact]
    public async Task DefaultProviderOrderEndsWithYandex()
    {
        var f = new Fixture();
        f.Handler.Html($"https://{Domain}/", "<html></html>");
        f.Handler.Status($"https://{Domain}/favicon.ico", HttpStatusCode.NotFound);
        f.Handler.Status($"https://{Www}/", HttpStatusCode.NotFound);
        f.Handler.Status($"https://{Www}/favicon.ico", HttpStatusCode.NotFound);
        f.Handler.Bytes(Google, SiteIconTestImages.Png(16), "image/png");
        f.Handler.Status(Duck, HttpStatusCode.NotFound);
        f.Handler.Bytes(Yandex, SiteIconTestImages.Png(32), "image/png");

        var record = await f.Service.GetAsync(Domain);

        Assert.Equal(("yandex", "image/png"), (record.Source, record.ContentType));
        Assert.Equal([Google, Duck, Yandex], f.Handler.Urls.Where(url => !url.Contains(Domain + "/")).ToArray());
    }

    [Fact]
    public async Task YandexOneByOnePlaceholderIsRejected()
    {
        var f = new Fixture(options => options.ThirdParty.Providers = ["yandex"]);
        f.Handler.Html($"https://{Domain}/", "<html></html>");
        f.Handler.Status($"https://{Domain}/favicon.ico", HttpStatusCode.NotFound);
        f.Handler.Bytes(Yandex, SiteIconTestImages.Png(1), "image/png");

        var record = await f.Service.GetAsync(Domain);

        Assert.Equal(SiteIconStatus.Missing, record.Status);
        Assert.Equal(f.Now.AddDays(7), record.ExpiresAt);
        Assert.Contains(Yandex, f.Handler.Urls);
        Assert.True(SiteIconFetcher.IsPlaceholder("yandex", SiteIconTestImages.Png(16)));
        Assert.False(SiteIconFetcher.IsPlaceholder("yandex", SiteIconTestImages.Png(17)));
        Assert.False(SiteIconFetcher.IsPlaceholder("duckduckgo", SiteIconTestImages.Png(1)));
    }

    [Fact]
    public async Task SubdomainWithoutIconBorrowsTheParentIconAndCachesBoth()
    {
        const string child = "cg.sz-water.com.cn", parent = "sz-water.com.cn";
        var f = new Fixture();
        f.Handler.Html($"https://{child}/", "<html></html>");
        f.Handler.Status($"https://{child}/favicon.ico", HttpStatusCode.NotFound);
        f.Handler.Html($"https://{parent}/", "<link rel=icon href=/p.png>");
        f.Handler.Bytes($"https://{parent}/p.png", SiteIconTestImages.Png(48), "image/png");

        var record = await f.Service.GetAsync(child);

        Assert.Equal((child, SiteIconStatus.Found, "html", "image/png"), (record.Domain, record.Status, record.Source, record.ContentType));
        Assert.Equal(SiteIconTestImages.Png(48), record.Bytes);
        Assert.Equal(f.Now.AddDays(30), record.ExpiresAt);
        Assert.Equal(record, f.Store.Get(child));
        Assert.Equal(("html", f.Now.AddDays(30)), (f.Store.Get(parent)!.Source, f.Store.Get(parent)!.ExpiresAt));
        Assert.DoesNotContain(f.Handler.Urls, url => url.Contains("www.") || url.Contains("://com.cn"));
        Assert.Null(f.Store.Get("com.cn"));

        var requests = f.Handler.Count;
        Assert.True((await f.Service.GetAsync(child)).IsFound);
        Assert.Equal(requests, f.Handler.Count);
    }

    [Fact]
    public async Task DeepSubdomainsJumpStraightToTheRegistrableDomainCache()
    {
        var f = new Fixture(options => options.ThirdParty.Enabled = false);
        f.Store.Seed(new SiteIconRecord("example.com", SiteIconStatus.Found, "image/x-icon", SiteIconTestImages.Ico(32), "favicon",
            f.Now.AddDays(-1), f.Now.AddDays(10)));
        var deep = string.Join('.', Enumerable.Range(0, 20).Select(i => $"l{i}")) + ".example.com";

        var record = await f.Service.GetAsync("a.b.example.com");
        var deepRecord = await f.Service.GetAsync(deep);

        Assert.Equal(("favicon", "image/x-icon"), (record.Source, record.ContentType));
        Assert.True(deepRecord.IsFound);
        // 中间层级不抓取、不写缓存：任意深度的子域名只额外查一次可注册域名。
        Assert.Null(f.Store.Get("b.example.com"));
        Assert.Equal(new[] { "a.b.example.com", deep, "example.com" }.Order(StringComparer.Ordinal), f.Store.Domains.Order(StringComparer.Ordinal));
        Assert.All(f.Handler.Urls, url => Assert.True(url.Contains("://a.b.example.com") || url.Contains("://" + deep)));
    }

    [Fact]
    public async Task ChildKeepsItsOwnMissingResultWhenTheParentHasNoIcon()
    {
        const string child = "qxb-pdf-osscache.qixin.com", parent = "qixin.com";
        var f = new Fixture(options => options.ThirdParty.Enabled = false);
        f.Handler.Html($"https://{parent}/", "<html></html>");
        f.Handler.Status($"https://{parent}/favicon.ico", HttpStatusCode.NotFound);
        f.Handler.Status($"https://www.{parent}/", HttpStatusCode.NotFound);
        f.Handler.Status($"https://www.{parent}/favicon.ico", HttpStatusCode.NotFound);

        var record = await f.Service.GetAsync(child);

        Assert.Equal(SiteIconStatus.Missing, record.Status);
        Assert.Equal(f.Now.AddHours(24), record.ExpiresAt);
        Assert.Equal((SiteIconStatus.Missing, f.Now.AddDays(7)), (f.Store.Get(parent)!.Status, f.Store.Get(parent)!.ExpiresAt));
    }

    [Fact]
    public async Task RegistrableDomainsNeverLookUpAParent()
    {
        var f = new Fixture(options => options.ThirdParty.Enabled = false);

        var record = await f.Service.GetAsync("sz-water.com.cn");

        Assert.Equal(SiteIconStatus.Missing, record.Status);
        Assert.Equal(["sz-water.com.cn"], f.Store.Domains);
        Assert.All(f.Handler.Urls, url => Assert.Contains("sz-water.com.cn", url));
    }

    [Fact]
    public async Task ParentLookupDoesNotDeadlockWhenEveryConcurrencySlotIsAChild()
    {
        const string parent = "sz-water.com.cn";
        var f = new Fixture(options => { options.MaxConcurrency = 1; options.ThirdParty.Enabled = false; });
        f.Handler.Html($"https://{parent}/", "<link rel=icon href=/p.png>");
        f.Handler.Bytes($"https://{parent}/p.png", SiteIconTestImages.Png(32), "image/png");

        var children = new[] { "cg.sz-water.com.cn", "oa.sz-water.com.cn", "a.cg.sz-water.com.cn" };
        var records = await Task.WhenAll(children.Select(child => Task.Run(() => f.Service.GetAsync(child))))
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.All(records, record => Assert.True(record.IsFound));
        Assert.All(children, child => Assert.True(f.Store.Get(child)!.IsFound));
        Assert.True(f.Store.Get(parent)!.IsFound);
        Assert.Equal(1, f.Handler.CountOf($"https://{parent}/"));
    }

    [Fact]
    public async Task AliasStillResolvesBeforeFetchingAndParentFallback()
    {
        var f = new Fixture(options => { options.ThirdParty.Enabled = false; options.DomainAliases = new() { ["iesdouyin.com"] = "douyin.com" }; });
        f.Handler.Html("https://douyin.com/", "<link rel=icon href=/d.ico>");
        f.Handler.Bytes("https://douyin.com/d.ico", SiteIconTestImages.Ico(32), "image/x-icon");

        var record = await f.Service.GetAsync("m.share.iesdouyin.com");

        Assert.Equal(("douyin.com", "html"), (record.Domain, record.Source));
        Assert.Equal(["douyin.com"], f.Store.Domains);
        Assert.DoesNotContain(f.Handler.Urls, url => url.Contains("iesdouyin"));
    }

    [Fact]
    public async Task AliasTargetThatIsASubdomainFallsBackToItsParentWithoutLooping()
    {
        var f = new Fixture(options =>
        {
            options.ThirdParty.Enabled = false;
            options.DomainAliases = new() { ["iesdouyin.com"] = "share.douyin.com", ["douyin.com"] = "iesdouyin.com" };
        });
        f.Store.Seed(new SiteIconRecord("douyin.com", SiteIconStatus.Found, "image/x-icon", SiteIconTestImages.Ico(32), "favicon",
            f.Now.AddDays(-1), f.Now.AddDays(10)));

        var record = await f.Service.GetAsync("www.iesdouyin.com").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(("share.douyin.com", true, "favicon"), (record.Domain, record.IsFound, record.Source));
        Assert.Equal(["douyin.com", "share.douyin.com"], f.Store.Domains);
        Assert.All(f.Handler.Urls, url => Assert.Contains("://share.douyin.com/", url));
    }

    [Fact]
    public async Task ExpiredSubdomainIconIsRetainedOnTransientFailureBeforeParentFallback()
    {
        const string child = "cg.sz-water.com.cn", parent = "sz-water.com.cn";
        var f = new Fixture(options => options.ThirdParty.Enabled = false);
        var old = new SiteIconRecord(child, SiteIconStatus.Found, "image/png", SiteIconTestImages.Png(32), "html",
            f.Now.AddDays(-40), f.Now.AddDays(-1));
        f.Store.Seed(old);
        f.Store.Seed(new SiteIconRecord(parent, SiteIconStatus.Found, "image/x-icon", SiteIconTestImages.Ico(48), "favicon",
            f.Now.AddDays(-1), f.Now.AddDays(10)));

        var record = await f.Service.GetAsync(child);

        Assert.Equal((old.Source, old.ContentType, old.FetchedAt, f.Now.AddHours(24)),
            (record.Source, record.ContentType, record.FetchedAt, record.ExpiresAt));
        Assert.Equal(old.Bytes, record.Bytes);
        Assert.DoesNotContain(f.Handler.Urls, url => url.Contains("://" + parent));
    }

    internal sealed class Fixture
    {
        public DateTimeOffset Now { get; } = new(2026, 10, 21, 8, 0, 0, TimeSpan.Zero);
        public FakeIconHandler Handler { get; } = new();
        public FakeGuard Guard { get; } = new();
        public MemoryStore Store { get; } = new();
        public SiteIconService Service { get; }

        public Fixture(Action<SiteIconOptions>? configure = null)
        {
            var options = new SiteIconOptions();
            configure?.Invoke(options);
            var wrapped = Options.Create(options);
            var fetcher = new SiteIconFetcher(new SingleClientFactory(Handler), Guard, wrapped, NullLogger<SiteIconFetcher>.Instance);
            Service = new SiteIconService(Store, fetcher, wrapped, NullLogger<SiteIconService>.Instance, new FixedTime(Now));
        }
    }

    internal sealed class FakeIconHandler : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, Func<HttpRequestMessage, Task<HttpResponseMessage>>> routes = new();
        private readonly ConcurrentQueue<string> urls = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> seen = new();
        private readonly ConcurrentDictionary<string, bool> hanging = new();

        public int Count => urls.Count;
        public string[] Urls => [.. urls];
        public int CountOf(string url) => urls.Count(item => item == url);

        public void Route(string url, Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) => routes[url] = handler;

        /// <summary>请求一直挂起直到调用方取消（模拟超时）。</summary>
        public void Hang(string url) => hanging[url] = true;

        public void Status(string url, HttpStatusCode status) =>
            Route(url, _ => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("<html>error</html>", Encoding.UTF8, "text/html") }));

        public void Html(string url, string html) =>
            Route(url, _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") }));

        public void Bytes(string url, byte[] bytes, string contentType, bool declaredLength = true) =>
            Route(url, _ =>
            {
                HttpContent content = declaredLength ? new ByteArrayContent(bytes) : new UnknownLengthContent(bytes);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            });

        public void Redirect(string url, string location) =>
            Route(url, _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.Found);
                response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
                return Task.FromResult(response);
            });

        public Task WaitForAsync(string url) => seen.GetOrAdd(url, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task
            .WaitAsync(TimeSpan.FromSeconds(10));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            urls.Enqueue(url);
            seen.GetOrAdd(url, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
            if (hanging.ContainsKey(url)) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (!routes.TryGetValue(url, out var handler))
                throw new HttpRequestException("Simulated connection failure.");
            return await handler(request);
        }
    }

    internal sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes, 0, bytes.Length);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    internal sealed class FakeGuard : ISiteIconHostGuard
    {
        public HashSet<string> Private { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Unresolved { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentBag<string> Checked { get; } = [];

        public Task<SiteIconHostCheck> CheckAsync(Uri url, CancellationToken cancellationToken)
        {
            Checked.Add(url.Host);
            return Task.FromResult(Private.Contains(url.Host) ? SiteIconHostCheck.Private
                : Unresolved.Contains(url.Host) ? SiteIconHostCheck.Unresolved : SiteIconHostCheck.Public);
        }
    }

    internal sealed class MemoryStore : ISiteIconStore
    {
        private readonly ConcurrentDictionary<string, SiteIconRecord> rows = new();
        private int reads, saves;
        public int Reads => reads;
        public int Saves => saves;
        public void Seed(SiteIconRecord record) => rows[record.Domain] = record;
        public SiteIconRecord? Get(string domain) => rows.TryGetValue(domain, out var record) ? record : null;
        public string[] Domains => [.. rows.Keys.Order(StringComparer.Ordinal)];

        public Task<SiteIconRecord?> GetAsync(string domain, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref reads);
            return Task.FromResult(Get(domain));
        }

        public Task SaveAsync(SiteIconRecord record, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref saves);
            rows[record.Domain] = record;
            return Task.CompletedTask;
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(SiteIconFetcher.ClientName, name);
            return new HttpClient(handler, disposeHandler: false);
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
