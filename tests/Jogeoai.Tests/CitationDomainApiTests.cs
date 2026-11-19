using System.Reflection;
using System.Text.Json;
using Jogeoai.Api;
using Jogeoai.Api.Controllers;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Jogeoai.Tests;

public sealed class CitationDomainApiTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Site = Guid.NewGuid();

    [Fact]
    public async Task SummaryPassesFixedWindowAndEngineWithoutAcceptingLegacySourceOverrides()
    {
        var controller = Controller("?from=2026-09-01T00:00:00Z&to=2026-09-30T00:00:00Z&engine=doubao", (method, args) =>
        {
            Assert.Equal(nameof(IAnalyticsQueryStore.CitationDomainSummaryAsync), method.Name);
            Assert.Equal(Tenant, args[0]); Assert.Equal(Site, args[1]);
            var filter = Assert.IsType<AnalyticsFilterRequest>(args[2]);
            Assert.Equal(DateTimeOffset.Parse("2026-09-01T00:00:00Z"), filter.From);
            Assert.Equal(new[] { "doubao" }, filter.Engines);
            return Task.FromResult(new CitationDomainSummaryView(null!, null!, [], null!));
        });
        Assert.Equal(200, Assert.IsType<ObjectResult>(await controller.CitationDomainSummary(Site, default)).StatusCode);
        await AssertError(Controller("?provider=cloro", Unexpected).CitationDomainSummary(Site, default), "INVALID_FILTER", 400);
    }

    [Fact]
    public async Task DomainsAndEvidenceForwardSnapshotAndAllRealFilterFields()
    {
        var controller = Controller("?snapshotToken=frozen&q=%25_&websiteCategory=other&ownership=non_owned&cursor=next&limit=7", (method, args) =>
        {
            Assert.Equal(nameof(IAnalyticsQueryStore.CitationDomainSummaryDomainsAsync), method.Name);
            Assert.Equal(new object?[] { Tenant, Site, "frozen", "%_", "other", "non_owned", "next", 7, CancellationToken.None }, args);
            return Task.FromResult(new CitationDomainSummaryPage(null!, [], null, 0));
        });
        Assert.Equal(200, Assert.IsType<ObjectResult>(await controller.CitationDomainSummaryDomains(Site, default)).StatusCode);
        controller = Controller("?snapshotToken=frozen&domain=brand.test", (method, args) =>
        {
            Assert.Equal(nameof(IAnalyticsQueryStore.CitationDomainSummaryEvidenceAsync), method.Name);
            Assert.Equal(new object?[] { Tenant, Site, "frozen", "brand.test", null, 20, CancellationToken.None }, args);
            return Task.FromResult(new CitationDomainEvidencePage(null!, [], null, null));
        });
        Assert.Equal(200, Assert.IsType<ObjectResult>(await controller.CitationDomainSummaryEvidence(Site, default)).StatusCode);
        await AssertError(Controller("?from=2026-09-01T00:00:00Z", Unexpected).CitationDomainSummaryDomains(Site, default), "INVALID_FILTER", 400);
    }

    [Fact]
    public async Task UrlsForwardSnapshotDomainCursorAndDefaultLimitOfFifty()
    {
        var controller = Controller("?snapshotToken=frozen&domain=brand.test&cursor=next&limit=5", (method, args) =>
        {
            Assert.Equal(nameof(IAnalyticsQueryStore.CitationDomainSummaryUrlsAsync), method.Name);
            Assert.Equal(new object?[] { Tenant, Site, "frozen", "brand.test", null, null, null, "next", 5, CancellationToken.None }, args);
            return Task.FromResult(new CitationUrlSummaryPage(null!, [], null, 0));
        });
        Assert.Equal(200, Assert.IsType<ObjectResult>(await controller.CitationDomainSummaryUrls(Site, default)).StatusCode);
        controller = Controller("?snapshotToken=frozen", (method, args) =>
        {
            Assert.Equal(nameof(IAnalyticsQueryStore.CitationDomainSummaryUrlsAsync), method.Name);
            Assert.Equal(new object?[] { Tenant, Site, "frozen", null, null, null, null, null, 50, CancellationToken.None }, args);
            return Task.FromResult(new CitationUrlSummaryPage(null!, [], null, 0));
        });
        Assert.Equal(200, Assert.IsType<ObjectResult>(await controller.CitationDomainSummaryUrls(Site, default)).StatusCode);
        foreach (var query in new[] { "?snapshotToken=frozen&websiteCategory=other", "?snapshotToken=frozen&from=2026-09-01T00:00:00Z", "?snapshotToken=frozen&provider=cloro",
                     "?snapshotToken=frozen&domain=brand.test&mine=yes", "?snapshotToken=frozen&domain=brand.test&mine=1" })
            await AssertError(Controller(query, Unexpected).CitationDomainSummaryUrls(Site, default), "INVALID_FILTER", 400);
    }

    [Theory]
    [InlineData("&engine=Doubao&q=%25_&mine=true", "Doubao", "%_", true)]
    [InlineData("&mine=FALSE", null, null, false)]
    [InlineData("&q=guide", null, "guide", null)]
    public async Task UrlsForwardEngineSearchAndMineUnchangedToTheStore(string extra, string? engine, string? q, bool? mine)
    {
        var controller = Controller("?snapshotToken=frozen&domain=brand.test" + extra, (method, args) =>
        {
            Assert.Equal(nameof(IAnalyticsQueryStore.CitationDomainSummaryUrlsAsync), method.Name);
            Assert.Equal(new object?[] { Tenant, Site, "frozen", "brand.test", engine, q, mine, null, 50, CancellationToken.None }, args);
            return Task.FromResult(new CitationUrlSummaryPage(null!, [], null, 0));
        });
        Assert.Equal(200, Assert.IsType<ObjectResult>(await controller.CitationDomainSummaryUrls(Site, default)).StatusCode);
    }

    [Fact]
    public async Task UrlsRejectEngineSearchOrMineWithoutDomainAndInvalidValuesBeforeDatabaseAccess()
    {
        var store = new AnalyticsQueryStore(new MySqlDatabase("Server=127.0.0.1;Port=1;Database=not_used;ConnectionTimeout=1"), null!);
        foreach (var (engine, q, mine) in new (string?, string?, bool?)[] { ("doubao", null, null), (null, "guide", null), (null, null, true), (null, null, false) })
            Assert.Equal("INVALID_FILTER", (await Assert.ThrowsAsync<CollectorStoreException>(() =>
                store.CitationDomainSummaryUrlsAsync(Tenant, Site, "frozen", null, engine, q, mine, null, 20))).Code);
        foreach (var (engine, q) in new (string?, string?)[] { ("dou bao", null), ("doubao,chatgpt", null), (null, new string('x', 101)) })
            Assert.Equal("INVALID_FILTER", (await Assert.ThrowsAsync<CollectorStoreException>(() =>
                store.CitationDomainSummaryUrlsAsync(Tenant, Site, "frozen", "brand.test", engine, q, null, null, 20))).Code);
    }

    [Fact]
    public async Task DomainDetailForwardsOnlySnapshotDomainAndEngine()
    {
        var controller = Controller("?snapshotToken=frozen&domain=brand.test&engine=doubao", (method, args) =>
        {
            Assert.Equal(nameof(IAnalyticsQueryStore.CitationDomainDetailAsync), method.Name);
            Assert.Equal(new object?[] { Tenant, Site, "frozen", "brand.test", "doubao", CancellationToken.None }, args);
            return Task.FromResult(new CitationDomainDetailView(null!, "brand.test", "doubao", 0, 0, [], []));
        });
        Assert.Equal(200, Assert.IsType<ObjectResult>(await controller.CitationDomainSummaryDetail(Site, default)).StatusCode);
        foreach (var query in new[] { "?snapshotToken=frozen&domain=brand.test&q=x", "?snapshotToken=frozen&domain=brand.test&cursor=x",
                     "?snapshotToken=frozen&domain=brand.test&mine=true", "?snapshotToken=frozen&domain=brand.test&from=2026-09-01T00:00:00Z" })
            await AssertError(Controller(query, Unexpected).CitationDomainSummaryDetail(Site, default), "INVALID_FILTER", 400);
    }

    private static readonly string SampleHash = CitationDomainAccumulator.UrlHash("https://zhihu.com/q/1");

    [Fact]
    public async Task UrlPromptsForwardOnlySnapshotUrlHashAndEngine()
    {
        var controller = Controller($"?snapshotToken=frozen&urlHash={SampleHash}&engine=doubao", (method, args) =>
        {
            Assert.Equal(nameof(IAnalyticsQueryStore.CitationUrlPromptsAsync), method.Name);
            Assert.Equal(new object?[] { Tenant, Site, "frozen", SampleHash, "doubao", CancellationToken.None }, args);
            return Task.FromResult(new CitationUrlPromptBreakdown(null!, SampleHash, "https://zhihu.com/q/1", null, "doubao", 0, 0, 0, []));
        });
        Assert.Equal(200, Assert.IsType<ObjectResult>(await controller.CitationDomainSummaryUrlPrompts(Site, default)).StatusCode);
        controller = Controller($"?snapshotToken=frozen&urlHash={SampleHash}", (method, args) =>
        {
            Assert.Equal(new object?[] { Tenant, Site, "frozen", SampleHash, null, CancellationToken.None }, args);
            return Task.FromResult(new CitationUrlPromptBreakdown(null!, SampleHash, "https://zhihu.com/q/1", null, null, 0, 0, 0, []));
        });
        Assert.Equal(200, Assert.IsType<ObjectResult>(await controller.CitationDomainSummaryUrlPrompts(Site, default)).StatusCode);
        foreach (var extra in new[] { "&domain=zhihu.com", "&cursor=x", "&limit=5", "&q=x", "&mine=true", "&from=2026-09-01T00:00:00Z" })
            await AssertError(Controller($"?snapshotToken=frozen&urlHash={SampleHash}" + extra, Unexpected)
                .CitationDomainSummaryUrlPrompts(Site, default), "INVALID_FILTER", 400);
    }

    [Theory]
    [InlineData("SNAPSHOT_REQUIRED", 400)]
    [InlineData("INVALID_FILTER", 400)]
    [InlineData("RESOURCE_NOT_FOUND", 404)]
    [InlineData("SNAPSHOT_EXPIRED", 409)]
    [InlineData("SNAPSHOT_REFRESH_REQUIRED", 409)]
    [InlineData("ANALYSIS_VERSION_NOT_READY", 503)]
    [InlineData("CITATION_PROJECTION_SCHEMA_NOT_MIGRATED", 503)]
    public async Task UrlPromptsMapErrorsExactlyLikeDomainDetail(string code, int status)
    {
        var prompts = Controller($"?snapshotToken=frozen&urlHash={SampleHash}",
            (_, _) => Task.FromException<CitationUrlPromptBreakdown>(new CollectorStoreException(code, "说明", status)));
        await AssertError(prompts.CitationDomainSummaryUrlPrompts(Site, default), code, status);
    }

    [Fact]
    public async Task UrlPromptsValidateSnapshotHashAndEngineBeforeDatabaseAccess()
    {
        var store = new AnalyticsQueryStore(new MySqlDatabase("Server=127.0.0.1;Port=1;Database=not_used;ConnectionTimeout=1"), null!);
        Assert.Equal("SNAPSHOT_REQUIRED", (await Assert.ThrowsAsync<CollectorStoreException>(() =>
            store.CitationUrlPromptsAsync(Tenant, Site, null, SampleHash, null))).Code);
        foreach (var hash in new[] { null, "", "abc", new string('g', 64), SampleHash + "0", "https://zhihu.com/q/1" })
            Assert.Equal("INVALID_FILTER", (await Assert.ThrowsAsync<CollectorStoreException>(() =>
                store.CitationUrlPromptsAsync(Tenant, Site, "frozen", hash, null))).Code);
        Assert.Equal("INVALID_FILTER", (await Assert.ThrowsAsync<CollectorStoreException>(() =>
            store.CitationUrlPromptsAsync(Tenant, Site, "frozen", SampleHash, "chat gpt"))).Code);
        Assert.Equal(SampleHash, AnalyticsQueryStore.NormalizeCitationUrlHash(" " + SampleHash.ToUpperInvariant() + " "));
    }

    [Theory]
    [InlineData("SNAPSHOT_REQUIRED", 400)]
    [InlineData("INVALID_FILTER", 400)]
    [InlineData("RESOURCE_NOT_FOUND", 404)]
    [InlineData("SNAPSHOT_EXPIRED", 409)]
    [InlineData("SNAPSHOT_REFRESH_REQUIRED", 409)]
    [InlineData("ANALYSIS_VERSION_NOT_READY", 503)]
    [InlineData("CITATION_PROJECTION_SCHEMA_NOT_MIGRATED", 503)]
    public async Task DomainDetailMapsErrorsExactlyLikeDomains(string code, int status)
    {
        var detail = Controller("?snapshotToken=frozen&domain=brand.test", (_, _) => Task.FromException<CitationDomainDetailView>(new CollectorStoreException(code, "说明", status)));
        await AssertError(detail.CitationDomainSummaryDetail(Site, default), code, status);
    }

    [Fact]
    public async Task DomainDetailValidatesSnapshotDomainAndEngineBeforeDatabaseAccess()
    {
        var store = new AnalyticsQueryStore(new MySqlDatabase("Server=127.0.0.1;Port=1;Database=not_used;ConnectionTimeout=1"), null!);
        Assert.Equal("SNAPSHOT_REQUIRED", (await Assert.ThrowsAsync<CollectorStoreException>(() =>
            store.CitationDomainDetailAsync(Tenant, Site, null, "brand.test", null))).Code);
        foreach (var domain in new[] { null, "", "https://brand.test/a", "bad host", "brand.test:443" })
            Assert.Equal("INVALID_FILTER", (await Assert.ThrowsAsync<CollectorStoreException>(() =>
                store.CitationDomainDetailAsync(Tenant, Site, "frozen", domain, null))).Code);
        Assert.Equal("INVALID_FILTER", (await Assert.ThrowsAsync<CollectorStoreException>(() =>
            store.CitationDomainDetailAsync(Tenant, Site, "frozen", "brand.test", "chat gpt"))).Code);
    }

    [Fact]
    public async Task RecordsAndDetailAcceptCitedDomainAndRejectInvalidHosts()
    {
        var store = new AnalyticsQueryStore(new MySqlDatabase("Server=127.0.0.1;Port=1;Database=not_used;ConnectionTimeout=1"), null!);
        foreach (var invalid in new[] { "https://zhihu.com/a", "bad host", "zhihu.com:443" })
            Assert.Equal("INVALID_FILTER", (await Assert.ThrowsAsync<CollectorStoreException>(() =>
                store.MonitoringRecordsAsync(Tenant, Site, new AnalyticsFilterRequest(), null, null, 20, null, null, invalid))).Code);
        Assert.Equal("zhihu.com", AnalyticsQueryStore.NormalizeMonitoringCitedDomain(" ZHIHU.com "));
        Assert.Null(AnalyticsQueryStore.NormalizeMonitoringCitedDomain("  "));
        // Old cursors without the new filter keep their scope; the filter value is part of the new scope.
        Assert.Equal(AnalyticsQueryStore.MonitoringRecordScope("Q", "mentioned"), AnalyticsQueryStore.MonitoringRecordScope("Q", "mentioned", null));
        Assert.NotEqual(AnalyticsQueryStore.MonitoringRecordScope(null, null, "zhihu.com"), AnalyticsQueryStore.MonitoringRecordScope(null, null, "baidu.com"));
        Assert.NotEqual(AnalyticsQueryStore.MonitoringRecordScope(null), AnalyticsQueryStore.MonitoringRecordScope(null, null, "zhihu.com"));

        var check = Guid.NewGuid();
        var controller = Controller("?snapshotToken=frozen&citedDomain=zhihu.com", (method, args) =>
        {
            Assert.Equal(nameof(IAnalyticsQueryStore.MonitoringAnswerDetailAsync), method.Name);
            Assert.Equal(new object?[] { Tenant, Site, check, "frozen", null, null, CancellationToken.None }, args);
            return Task.FromResult<MonitoringAnswerDetailView?>(null);
        });
        await AssertError(controller.MonitoringAnswerDetail(Site, check, default), "RESOURCE_NOT_FOUND", 404);
        await AssertError(Controller("?snapshotToken=frozen&citedDomain=bad%20host", Unexpected).MonitoringAnswerDetail(Site, check, default), "INVALID_FILTER", 400);
    }

    [Fact]
    public void UrlScopeKeepsLegacyValueWithoutNewFiltersAndBindsEachNewFilter()
    {
        Assert.Equal("domain:brand.test", AnalyticsQueryStore.CitationUrlScopeValue("brand.test", null, null, null));
        Assert.Equal("domain:", AnalyticsQueryStore.CitationUrlScopeValue(null, null, null, null));
        var scopes = new[]
        {
            AnalyticsQueryStore.CitationUrlScopeValue("brand.test", null, null, null),
            AnalyticsQueryStore.CitationUrlScopeValue("brand.test", "doubao", null, null),
            AnalyticsQueryStore.CitationUrlScopeValue("brand.test", "chatgpt", null, null),
            AnalyticsQueryStore.CitationUrlScopeValue("brand.test", null, "GUIDE", null),
            AnalyticsQueryStore.CitationUrlScopeValue("brand.test", null, null, true),
            AnalyticsQueryStore.CitationUrlScopeValue("other.test", "doubao", null, null)
        };
        Assert.Equal(scopes.Length, scopes.Distinct().Count());
        Assert.Equal("doubao", AnalyticsQueryStore.NormalizeCitationEngine(" DouBao "));
        Assert.Null(AnalyticsQueryStore.NormalizeCitationEngine(" "));
        Assert.Equal("GUIDE %_", AnalyticsQueryStore.NormalizeCitationUrlSearch("  guide %_ "));
        Assert.Null(AnalyticsQueryStore.NormalizeCitationUrlSearch("   "));
        Assert.Equal(100, AnalyticsQueryStore.NormalizeCitationUrlSearch(new string('a', 100))!.Length);
    }

    [Theory]
    [InlineData("SNAPSHOT_REQUIRED", 400)]
    [InlineData("INVALID_CURSOR", 400)]
    [InlineData("INVALID_LIMIT", 400)]
    [InlineData("RESOURCE_NOT_FOUND", 404)]
    [InlineData("SNAPSHOT_EXPIRED", 409)]
    [InlineData("SNAPSHOT_REFRESH_REQUIRED", 409)]
    [InlineData("ANALYSIS_VERSION_NOT_READY", 503)]
    [InlineData("CITATION_PROJECTION_SCHEMA_NOT_MIGRATED", 503)]
    public async Task UrlsMapErrorsExactlyLikeDomains(string code, int status)
    {
        var urls = Controller("?snapshotToken=frozen", (_, _) => Task.FromException<CitationUrlSummaryPage>(new CollectorStoreException(code, "说明", status)));
        await AssertError(urls.CitationDomainSummaryUrls(Site, default), code, status);
        var domains = Controller("?snapshotToken=frozen", (_, _) => Task.FromException<CitationDomainSummaryPage>(new CollectorStoreException(code, "说明", status)));
        await AssertError(domains.CitationDomainSummaryDomains(Site, default), code, status);
    }

    [Fact]
    public async Task UrlsValidateSnapshotLimitAndDomainBeforeDatabaseAccess()
    {
        var store = new AnalyticsQueryStore(new MySqlDatabase("Server=127.0.0.1;Port=1;Database=not_used;ConnectionTimeout=1"), null!);
        var missing = await Assert.ThrowsAsync<CollectorStoreException>(() => store.CitationDomainSummaryUrlsAsync(Tenant, Site, null, null, null, 50));
        Assert.Equal("SNAPSHOT_REQUIRED", missing.Code);
        foreach (var limit in new[] { 0, 101 })
            Assert.Equal("INVALID_LIMIT", (await Assert.ThrowsAsync<CollectorStoreException>(() =>
                store.CitationDomainSummaryUrlsAsync(Tenant, Site, "frozen", null, null, limit))).Code);
        foreach (var domain in new[] { "https://brand.test/a", "bad host", "brand.test:443" })
            Assert.Equal("INVALID_FILTER", (await Assert.ThrowsAsync<CollectorStoreException>(() =>
                store.CitationDomainSummaryUrlsAsync(Tenant, Site, "frozen", domain, null, 20))).Code);
    }

    [Fact]
    public async Task OptionalDetailProjectionUsesOnlyTheExplicitNewOverload()
    {
        var check = Guid.NewGuid();
        var controller = Controller("?snapshotToken=frozen&citationProjection=domain-summary-v1", (method, args) =>
        {
            Assert.Equal(nameof(IAnalyticsQueryStore.MonitoringAnswerDetailAsync), method.Name);
            Assert.Equal(new object?[] { Tenant, Site, check, "frozen", null, null, "domain-summary-v1", CancellationToken.None }, args);
            return Task.FromResult<MonitoringAnswerDetailView?>(null);
        });
        await AssertError(controller.MonitoringAnswerDetail(Site, check, default), "RESOURCE_NOT_FOUND", 404);
        await AssertError(Controller("?citationProjection=latest", Unexpected).MonitoringAnswerDetail(Site, check, default), "INVALID_FILTER", 400);
    }

    [Theory]
    [InlineData("ANALYSIS_VERSION_NOT_READY", 503)]
    [InlineData("CITATION_PROJECTION_SCHEMA_NOT_MIGRATED", 503)]
    [InlineData("SNAPSHOT_EXPIRED", 409)]
    [InlineData("SNAPSHOT_REFRESH_REQUIRED", 409)]
    public async Task SummaryPreservesUnavailableAndSnapshotErrors(string code, int status)
    {
        var controller = Controller("", (_, _) => Task.FromException<CitationDomainSummaryView>(new CollectorStoreException(code, "说明", status)));
        await AssertError(controller.CitationDomainSummary(Site, default), code, status);
    }

    [Fact]
    public async Task RequiredSnapshotAndInvalidTableFiltersFailBeforeDatabaseAccess()
    {
        var store = new AnalyticsQueryStore(new MySqlDatabase("Server=127.0.0.1;Port=1;Database=not_used;ConnectionTimeout=1"), null!);
        var missing = await Assert.ThrowsAsync<CollectorStoreException>(() => store.CitationDomainSummaryDomainsAsync(Tenant, Site, null, null, null, null, null, 20));
        Assert.Equal("SNAPSHOT_REQUIRED", missing.Code);
        var invalid = await Assert.ThrowsAsync<CollectorStoreException>(() => store.CitationDomainSummaryDomainsAsync(Tenant, Site, "frozen", null, "brand", null, null, 20));
        Assert.Equal("INVALID_FILTER", invalid.Code);
        var controller = Controller("", Unexpected);
        controller.HttpContext.Items.Clear();
        await AssertError(controller.CitationDomainSummary(Site, default), "AUTHENTICATION_REQUIRED", 401);
    }

    private static AnalyticsController Controller(string query, Func<MethodInfo, object?[], object?> handler)
    {
        var queries = DispatchProxy.Create<IAnalyticsQueryStore, MonitoringAnswerApiTests.QueryProxy>();
        ((MonitoringAnswerApiTests.QueryProxy)queries).Handler = handler;
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(query);
        context.Items[SessionAuthenticationMiddleware.ContextItemKey] = new AuthenticatedRequestContext(
            new AuthUserView(Guid.NewGuid(), "member@fixture.test", "Member", "active",
                [new TenantMembershipView(Tenant, "Team", "owner", "active")]), Guid.NewGuid(), "fixture-token");
        return new(queries, null!, new TenantContextResolver(new TestEnvironment()))
        { ControllerContext = new ControllerContext { HttpContext = context } };
    }

    private static object? Unexpected(MethodInfo method, object?[] args) => throw new InvalidOperationException(method.Name);
    private static async Task AssertError(Task<IActionResult> action, string code, int status)
    {
        var result = Assert.IsAssignableFrom<ObjectResult>(await action);
        Assert.Equal(status, result.StatusCode);
        Assert.Equal(code, JsonSerializer.SerializeToElement(result.Value).GetProperty("error").GetProperty("code").GetString());
    }
    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
