using Jogeoai.Api;
using Jogeoai.Api.Mcp;
using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.AspNetCore.Http;

namespace Jogeoai.Tests.Mcp;

/// <summary>
/// 只读工具（Store 用桩）：令牌站点范围与租户归属校验、越权与不存在同一提示、分页与日期上限、全部来源默认筛选、
/// 快照翻页、回答截断、详情映射与 MonitoringAnswerDetailView 一致、跨来源可见性映射、Store 错误转换与审计结果码。
/// </summary>
public sealed class McpReadToolsTests
{
    [Fact]
    public async Task SiteOutsideTokenScopeIsDeniedWithoutAnyStoreCall()
    {
        var f = new Fixture(siteIds: [Guid.NewGuid()]);

        var error = await Assert.ThrowsAsync<McpToolException>(() => f.Tools.GetVisibility(f.Site));
        Assert.Equal(McpScope.SiteDenied, error.Message);
        Assert.Equal("denied", error.Outcome);
        Assert.Empty(f.Management.Calls);
        Assert.Empty(f.Analytics.Calls);
        Assert.Contains(f.Audit(), entry => entry.Message.Contains("get_visibility", StringComparison.Ordinal)
            && entry.Message.Contains("denied", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SiteOfAnotherTenantGetsTheSameMessageAsOutOfScope()
    {
        var f = new Fixture();
        var foreign = Guid.NewGuid();

        var error = await Assert.ThrowsAsync<McpToolException>(() => f.Tools.ListMonitoringRecords(foreign));
        Assert.Equal(McpScope.SiteDenied, error.Message);
        var lookup = Assert.Single(f.Management.Calls);
        Assert.Equal(f.Tenant, lookup.Args[0]);
        Assert.Empty(f.Analytics.Calls);
    }

    [Fact]
    public async Task ForeignRecordIdIsDeniedWithRecordMessage()
    {
        var f = new Fixture();
        f.Analytics.Handlers[nameof(IAnalyticsQueryStore.MonitoringAnswerDetailAsync)] = args =>
        {
            Assert.Equal(f.Tenant, args[0]);
            Assert.Equal(f.Site, args[1]);
            return Task.FromResult<MonitoringAnswerDetailView?>(null);
        };

        var error = await Assert.ThrowsAsync<McpToolException>(() => f.Tools.GetMonitoringRecord(f.Site, Guid.NewGuid(), "snapshot-token-0123456789"));
        Assert.Equal(McpScope.RecordDenied, error.Message);
    }

    [Fact]
    public async Task ListSitesHonoursTokenScopeOrPagesTenantBrands()
    {
        var scoped = new Fixture(siteIds: null);
        var other = Guid.NewGuid();
        scoped.Management.Handlers[nameof(IManagementStore.ListBrandsAsync)] = args =>
        {
            Assert.Equal(scoped.Tenant, args[0]);
            Assert.Equal(50, args[2]);
            return Task.FromResult(new CursorPage<BrandView>([McpFixtures.Brand(scoped.Site), McpFixtures.Brand(other)], "next"));
        };
        var all = await scoped.Tools.ListSites();
        Assert.Equal(new[] { scoped.Site, other }, all.Items.Select(item => item.SiteId));
        Assert.Equal("next", all.NextCursor);

        var limited = new Fixture(siteIds: null);
        limited = new Fixture(siteIds: [limited.Site, Guid.NewGuid()], site: limited.Site);
        var result = await limited.Tools.ListSites();
        Assert.Equal(new[] { limited.Site }, result.Items.Select(item => item.SiteId));
        Assert.Null(result.NextCursor);
        Assert.Equal(0, limited.Management.Count(nameof(IManagementStore.ListBrandsAsync)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public async Task PageSizeOutsideOneToFiftyIsRejected(int limit)
    {
        var f = new Fixture();
        var error = await Assert.ThrowsAsync<McpToolException>(() => f.Tools.ListMonitoringRecords(f.Site, limit: limit));
        Assert.Equal("invalid_argument", error.Outcome);
        Assert.Empty(f.Analytics.Calls);
    }

    [Fact]
    public async Task RecordsDefaultToSevenDaysAllSourcesAndTwentyRows()
    {
        var f = new Fixture();
        AnalyticsFilterRequest? filter = null;
        f.Analytics.Handlers[nameof(IAnalyticsQueryStore.MonitoringRecordsAsync)] = args =>
        {
            filter = (AnalyticsFilterRequest)args[2]!;
            Assert.Null(args[3]);
            Assert.Equal(20, args[5]);
            Assert.Equal("推荐", args[6]);
            Assert.Equal("mentioned", args[7]);
            return Task.FromResult(new MonitoringRecordPage(McpFixtures.Meta(), [McpFixtures.Record(Guid.NewGuid())], "cursor-2", 7));
        };

        var page = await f.Tools.ListMonitoringRecords(f.Site, search: " 推荐 ", mention: "mentioned");

        Assert.NotNull(filter);
        Assert.Equal("Asia/Shanghai", filter!.TimeZone);
        Assert.Equal(AnalyticsSourceDefaults.Providers, filter.Providers);
        Assert.Equal(AnalyticsSourceDefaults.CaptureModes, filter.CaptureModes);
        Assert.Equal(AnalyticsSourceDefaults.EvidenceClasses, filter.EvidenceClasses);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
        var localFrom = TimeZoneInfo.ConvertTime(filter.From!.Value, zone);
        Assert.Equal(TimeSpan.Zero, localFrom.TimeOfDay);
        Assert.Equal(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(filter.To!.Value, zone).DateTime).AddDays(-6),
            DateOnly.FromDateTime(localFrom.DateTime));
        Assert.Equal("cursor-2", page.NextCursor);
        Assert.Equal(7, page.TotalCount);
        Assert.Equal("snapshot-token-0123456789abcdef", page.Window.SnapshotToken);
    }

    [Fact]
    public async Task SnapshotPagingSendsOnlyExplicitFiltersAndIgnoresDates()
    {
        var f = new Fixture();
        var prompt = Guid.NewGuid();
        f.Analytics.Handlers[nameof(IAnalyticsQueryStore.MonitoringRecordsAsync)] = args =>
        {
            var filter = (AnalyticsFilterRequest)args[2]!;
            Assert.Null(filter.From);
            Assert.Null(filter.To);
            Assert.Null(filter.CaptureModes);
            Assert.Equal(new[] { "doubao_api" }, filter.Providers);
            Assert.Equal(new[] { prompt }, filter.PromptIds);
            Assert.Equal("snap-token-0123456789", args[3]);
            Assert.Equal("cursor-2", args[4]);
            return Task.FromResult(new MonitoringRecordPage(McpFixtures.Meta(), [], null, 0));
        };

        await f.Tools.ListMonitoringRecords(f.Site, from: "garbage", provider: "doubao_api", promptId: prompt,
            snapshotToken: "snap-token-0123456789", cursor: "cursor-2");
    }

    // App 渠道任务（11-16）阶段 7：MCP 只读工具不提供 App 口径。App 的 Provider 现在是 Store 接受的显式筛选，
    // 工具仍按不支持的来源拒绝，Store 不会被调用；带快照翻页时同样如此。
    [Theory]
    [InlineData("app_collector", null)]
    [InlineData(" App_Collector ", null)]
    [InlineData("app_collector", "snap-token-0123456789")]
    public async Task TheAppProviderIsStillRefusedAndNoStoreIsQueried(string provider, string? snapshotToken)
    {
        var f = new Fixture();
        var records = await Assert.ThrowsAsync<McpToolException>(() => f.Tools.ListMonitoringRecords(f.Site, provider: provider, snapshotToken: snapshotToken));
        Assert.Equal(("invalid_argument", "来源或证据类别筛选不受支持。"), (records.Outcome, records.Message));
        if (snapshotToken is null)
        {
            Assert.Equal("invalid_argument", (await Assert.ThrowsAsync<McpToolException>(() => f.Tools.GetVisibility(f.Site, provider: provider))).Outcome);
            Assert.Equal("invalid_argument", (await Assert.ThrowsAsync<McpToolException>(() => f.Tools.GetShareOfVoice(f.Site, provider: provider))).Outcome);
        }
        Assert.Empty(f.Analytics.Calls);
    }

    // App 渠道任务（11-16）阶段 7 补充 G：网页端为 App 口径冻结的快照令牌（captureMode=app、provider=app_collector）
    // 传给 MCP 工具时，在 Store 解析出冻结筛选后拒绝；App 回答的详情同样拒绝（详情不带筛选，按记录本身的渠道判断）。
    [Theory]
    [InlineData(new[] { "app_collector" }, new[] { "app" })]
    [InlineData(new[] { "cloro" }, new[] { "app" })]
    [InlineData(new[] { "app_collector" }, new[] { "scraped" })]
    public async Task ASnapshotTokenOfTheAppViewIsRefusedByEveryToolThatAcceptsATokenAfterTheStoreResolvesIt(string[] providers, string[] modes)
    {
        var f = new Fixture();
        const string token = "app-view-token-0123456789";
        var meta = MetaWithSources(token, providers, modes);
        f.Analytics.Handlers[nameof(IAnalyticsQueryStore.MonitoringRecordsAsync)] = _
            => Task.FromResult(new MonitoringRecordPage(meta, [McpFixtures.Record(Guid.NewGuid()) with { Provider = "app_collector", CaptureMode = "app" }], null, 1));
        f.Analytics.Handlers[nameof(IAnalyticsQueryStore.PromptsAsync)] = _
            => Task.FromResult(new AnalyticsPage<PromptPerformanceView>(meta, [], null));
        f.Analytics.Handlers[nameof(IAnalyticsQueryStore.CitationDomainsAsync)] = _
            => Task.FromResult(new AnalyticsPage<CitationDomainRow>(meta, [], null));
        foreach (var call in new Func<Task>[]
        {
            () => f.Tools.ListMonitoringRecords(f.Site, snapshotToken: token),
            () => f.Tools.ListPrompts(f.Site, snapshotToken: token),
            () => f.Tools.ListCitationDomains(f.Site, snapshotToken: token),
        })
        {
            var error = await Assert.ThrowsAsync<McpToolException>(call);
            Assert.Equal(("invalid_argument", "MCP 只读工具不提供 App 口径。"), (error.Outcome, error.Message));
        }
        // The token reached the store each time (the frozen filter is only known after it has been resolved).
        Assert.All(f.Analytics.Calls, call => Assert.Contains(token, call.Args));
        Assert.Equal(3, f.Analytics.Calls.Count);
        Assert.Equal(3, f.Audit().Count(entry => entry.State.Any(pair => pair.Key == "Outcome" && Equals(pair.Value, "invalid_argument"))));
    }

    [Fact]
    public async Task AnAppAnswerIsRefusedByTheDetailToolWhateverTheTokenAndOtherAnswersAreRead()
    {
        var f = new Fixture();
        var (app, web) = (Guid.NewGuid(), Guid.NewGuid());
        f.Analytics.Handlers[nameof(IAnalyticsQueryStore.MonitoringAnswerDetailAsync)] = args =>
        {
            var check = (Guid)args[2]!;
            var detail = McpFixtures.Detail(f.Site, check, "回答");
            return Task.FromResult<MonitoringAnswerDetailView?>(check == app ? detail with { Provider = "app_collector", CaptureMode = "app" } : detail);
        };
        var error = await Assert.ThrowsAsync<McpToolException>(() => f.Tools.GetMonitoringRecord(f.Site, app, "default-token-0123456789"));
        Assert.Equal(("invalid_argument", "MCP 只读工具不提供 App 口径。"), (error.Outcome, error.Message));
        // A domestic web answer (channel web) is read as before.
        Assert.Equal("回答", (await f.Tools.GetMonitoringRecord(f.Site, web, "default-token-0123456789")).Answer);
    }

    private static AnalyticsMeta MetaWithSources(string token, IReadOnlyList<string> providers, IReadOnlyList<string> modes)
    {
        var meta = McpFixtures.Meta(token);
        return meta with { Filters = meta.Filters with { Providers = providers, CaptureModes = modes } };
    }

    // The tools' own default and a token of the default view are not affected.
    [Fact]
    public async Task ADefaultViewTokenIsReadAsBefore()
    {
        var f = new Fixture();
        f.Analytics.Handlers[nameof(IAnalyticsQueryStore.MonitoringRecordsAsync)] = _
            => Task.FromResult(new MonitoringRecordPage(MetaWithSources("default-token-0123456789", AnalyticsSourceDefaults.Providers,
                AnalyticsSourceDefaults.CaptureModes), [McpFixtures.Record(Guid.NewGuid())], null, 1));
        Assert.Single((await f.Tools.ListMonitoringRecords(f.Site, snapshotToken: "default-token-0123456789")).Items);
        Assert.Single((await f.Tools.ListMonitoringRecords(f.Site)).Items);
    }

    [Theory]
    [InlineData("2026-08-06", "2026-11-03", true)]
    [InlineData("2026-08-05", "2026-11-03", false)]
    [InlineData("2026-11-03", "2026-11-01", false)]
    [InlineData("2026/11/01", null, false)]
    [InlineData("2026-11-01T00:00:00", null, false)]
    public void DateRangeIsCappedAtNinetyLocalDays(string from, string? to, bool valid)
    {
        var now = new DateTimeOffset(2026, 11, 4, 2, 0, 0, TimeSpan.Zero);
        if (valid)
        {
            var (start, end) = McpScope.Range(from, to, "Asia/Shanghai", now);
            Assert.Equal(new DateTimeOffset(2026, 8, 5, 16, 0, 0, TimeSpan.Zero), start);
            Assert.Equal(new DateTimeOffset(2026, 11, 3, 16, 0, 0, TimeSpan.Zero), end);
        }
        else
        {
            Assert.Equal("invalid_argument", Assert.Throws<McpToolException>(() => McpScope.Range(from, to, "Asia/Shanghai", now)).Outcome);
        }
    }

    [Fact]
    public void DstGapMidnightMovesToFirstValidLocalTimeAndExtremeDatesAreInvalidArguments()
    {
        var now = new DateTimeOffset(2026, 11, 4, 2, 0, 0, TimeSpan.Zero);
        // 智利 2026-09-06 当地 0 点跳到 1 点（UTC-4 → UTC-3），当日起点为当地 01:00 = 04:00Z。
        var (start, end) = McpScope.Range("2026-09-06", "2026-09-06", "America/Santiago", now);
        Assert.Equal(new DateTimeOffset(2026, 9, 6, 4, 0, 0, TimeSpan.Zero), start);
        Assert.Equal(new DateTimeOffset(2026, 9, 7, 3, 0, 0, TimeSpan.Zero), end);

        Assert.Equal("invalid_argument", Assert.Throws<McpToolException>(
            () => McpScope.Range("9999-12-30", "9999-12-31", "Asia/Shanghai", now)).Outcome);
        Assert.Equal("invalid_argument", Assert.Throws<McpToolException>(
            () => McpScope.Range(null, "0001-01-01T00:00:00Z", "Asia/Shanghai", now)).Outcome);
    }

    [Fact]
    public void IsoTimesWithZoneAreAcceptedAndDefaultsCoverSevenLocalDays()
    {
        var now = new DateTimeOffset(2026, 11, 4, 2, 0, 0, TimeSpan.Zero); // 上海 11-04 10:00
        var (start, end) = McpScope.Range(null, null, "Asia/Shanghai", now);
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 16, 0, 0, TimeSpan.Zero), start);
        Assert.Equal(now, end);

        var (isoStart, isoEnd) = McpScope.Range("2026-11-01T08:00:00+08:00", "2026-11-02T00:00:00Z", "Asia/Shanghai", now);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), isoStart);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero), isoEnd);
    }

    [Fact]
    public void AnswerIsTruncatedAtTwentyThousandCharactersWithoutSplittingSurrogates()
    {
        var (none, noneTruncated, noneLength) = McpReadTools.TruncateAnswer(null);
        Assert.Null(none);
        Assert.False(noneTruncated);
        Assert.Equal(0, noneLength);
        var exact = new string('答', McpReadTools.MaxAnswerCharacters);
        Assert.Equal((exact, false, exact.Length), McpReadTools.TruncateAnswer(exact));

        var (text, truncated, length) = McpReadTools.TruncateAnswer(new string('答', 25000));
        Assert.True(truncated);
        Assert.Equal(25000, length);
        Assert.Equal(McpReadTools.MaxAnswerCharacters, text!.Length);

        var emoji = new string('a', McpReadTools.MaxAnswerCharacters - 1) + "😀" + "tail";
        var (cut, cutTruncated, _) = McpReadTools.TruncateAnswer(emoji);
        Assert.True(cutTruncated);
        Assert.Equal(McpReadTools.MaxAnswerCharacters - 1, cut!.Length);
        Assert.False(char.IsHighSurrogate(cut[^1]));
    }

    [Fact]
    public async Task RecordDetailMatchesMonitoringAnswerDetailView()
    {
        var f = new Fixture();
        var check = Guid.NewGuid();
        var view = McpFixtures.Detail(f.Site, check, "柯丽尔吹风机风速快，详见 https://news.example/1。", citations: 3);
        f.Analytics.Handlers[nameof(IAnalyticsQueryStore.MonitoringAnswerDetailAsync)] = args =>
        {
            Assert.Equal(check, args[2]);
            Assert.Equal("snap-token-0123456789", args[3]);
            return Task.FromResult<MonitoringAnswerDetailView?>(view);
        };

        var detail = await f.Tools.GetMonitoringRecord(f.Site, check, "snap-token-0123456789");

        Assert.Equal(view.Answer!.Text, detail.Answer);
        Assert.False(detail.AnswerTruncated);
        Assert.Equal(view.Answer.Text.Length, detail.AnswerLength);
        Assert.Equal(view.CheckId, detail.CheckId);
        Assert.Equal(view.RunId, detail.RunId);
        Assert.Equal(view.PromptSnapshot, detail.Prompt);
        Assert.Equal(view.Provider, detail.Provider);
        Assert.Equal(view.Engine, detail.Engine);
        Assert.Equal(view.WebQueries, detail.WebQueries);
        Assert.Equal(view.QueryAvailability, detail.QueryAvailability);
        Assert.Equal(view.MentionState, detail.MentionState);
        Assert.Equal(view.Citations.OrderBy(c => c.Position).Select(c => (c.Position, c.Url, c.Title, c.Domain)),
            detail.Citations.Select(c => (c.Position, c.Url, c.Title, c.Domain)));
        Assert.False(detail.CitationsTruncated);
        Assert.Equal(view.BrandMetrics!.Ranking.Value, detail.BrandMetrics!.Rank);
        Assert.Equal(view.BrandMetrics.Exposure.Value, detail.BrandMetrics.ExposureScore);
        Assert.Equal("exposure-v2", detail.BrandMetrics.ExposureScoreVersion);
        Assert.Equal("snap-token-0123456789", detail.SnapshotToken);
    }

    [Fact]
    public async Task RecordDetailWithoutSnapshotCreatesOneAndCapsCitations()
    {
        var f = new Fixture();
        var check = Guid.NewGuid();
        f.Analytics.Handlers[nameof(IAnalyticsQueryStore.MonitoringRecordsAsync)] = args =>
        {
            Assert.Null(args[3]);
            Assert.Equal(1, args[5]);
            return Task.FromResult(new MonitoringRecordPage(McpFixtures.Meta("fresh-snapshot-0123456789"), [], null, 0));
        };
        f.Analytics.Handlers[nameof(IAnalyticsQueryStore.MonitoringAnswerDetailAsync)] = args =>
        {
            Assert.Equal("fresh-snapshot-0123456789", args[3]);
            return Task.FromResult<MonitoringAnswerDetailView?>(McpFixtures.Detail(f.Site, check, new string('答', 30000), citations: 130));
        };

        var detail = await f.Tools.GetMonitoringRecord(f.Site, check);

        Assert.True(detail.AnswerTruncated);
        Assert.Equal(30000, detail.AnswerLength);
        Assert.Equal(McpReadTools.MaxCitations, detail.Citations.Count);
        Assert.True(detail.CitationsTruncated);
        Assert.Equal(Enumerable.Range(1, McpReadTools.MaxCitations), detail.Citations.Select(c => c.Position));
        Assert.Equal("fresh-snapshot-0123456789", detail.SnapshotToken);
    }

    [Fact]
    public void MultiSourceVisibilityHidesMergedMetricsAndListsSources()
    {
        var point = new AnalyticsDayPoint(new DateOnly(2026, 11, 1), MetricRatio.Of(1, 2), MetricRatio.Of(1, 3), 2, 2, 0, false, null, null);
        var multi = new VisibilityView(McpFixtures.Meta(), McpFixtures.Metrics(0, 0), [], [], McpFixtures.Metrics(0, 0),
            new Dictionary<string, VoiceProjection>
            {
                ["domestic_collector|scraped|observed_surface"] = new(McpFixtures.Metrics(4, 2), []),
                ["cloro|scraped|observed_surface"] = new(McpFixtures.Metrics(6, 3), []),
            },
            new Dictionary<string, IReadOnlyList<AnalyticsDayPoint>> { ["cloro|scraped|observed_surface"] = [point] });

        var mapped = McpReadTools.Visibility(multi);
        Assert.Null(mapped.Metrics);
        Assert.Empty(mapped.Trend);
        Assert.Equal(new[] { "cloro|scraped|observed_surface", "domestic_collector|scraped|observed_surface" },
            mapped.Sources.Select(source => source.Source));
        Assert.Equal(6, mapped.Sources[0].Metrics.EligibleChecks);
        Assert.Single(mapped.Sources[0].Trend);
        Assert.Empty(mapped.Sources[1].Trend);

        var slices = Enumerable.Range(0, 80).Select(i => new VisibilitySlice("tag", $"t{i}", McpFixtures.Metrics(2, 1))).ToArray();
        var single = new VisibilityView(McpFixtures.Meta(), McpFixtures.Metrics(5, 4), [point], slices, McpFixtures.Metrics(5, 4),
            new Dictionary<string, VoiceProjection> { ["cloro|scraped|observed_surface"] = new(McpFixtures.Metrics(5, 4), []) }, null);
        var one = McpReadTools.Visibility(single);
        Assert.Equal(5, one.Metrics!.EligibleChecks);
        Assert.Single(one.Trend);
        Assert.Equal(McpReadTools.MaxSlices, one.Slices.Count);
    }

    [Fact]
    public void ShareOfVoiceCapsEachListAtTopN()
    {
        var entries = Enumerable.Range(1, 30)
            .Select(i => new SubjectShareEntry(Guid.NewGuid(), $"主体{i}", i == 1, 31 - i, MetricRatio.Of(31 - i, 465), 1, i)).ToArray();
        var view = new ShareOfVoiceView(McpFixtures.Meta(), McpFixtures.Metrics(10, 5), entries, entries.Take(5).ToArray(),
            MetricRatio.Of(0, 1), [], McpFixtures.Metrics(10, 5),
            new Dictionary<string, VoiceProjection> { ["cloro|scraped|observed_surface"] = new(McpFixtures.Metrics(10, 5), entries) }, null);

        var mapped = McpReadTools.ShareOfVoice(view, 10);
        Assert.Equal(10, mapped.Entries.Count);
        Assert.True(mapped.Entries[0].IsBrand);
        Assert.Equal(10, Assert.Single(mapped.Sources).Entries.Count);
    }

    [Theory]
    [InlineData(404, "RESOURCE_NOT_FOUND", "denied")]
    [InlineData(409, "SNAPSHOT_EXPIRED", "conflict")]
    [InlineData(400, "INVALID_CURSOR", "invalid_argument")]
    [InlineData(503, "REPUTATION_SCHEMA_NOT_MIGRATED", "unavailable")]
    [InlineData(500, "SOMETHING_INTERNAL", "error")]
    public async Task StoreErrorsAreTranslatedAndAudited(int status, string code, string outcome)
    {
        var f = new Fixture();
        f.Analytics.Handlers[nameof(IAnalyticsQueryStore.CitationDomainsAsync)] = _ =>
            throw new CollectorStoreException(code, "内部说明：表 x 不存在。", status);

        var error = await Assert.ThrowsAsync<McpToolException>(() => f.Tools.ListCitationDomains(f.Site));
        Assert.Equal(outcome, error.Outcome);
        if (status is 404) Assert.Equal(McpScope.SiteDenied, error.Message);
        if (status is 500) Assert.DoesNotContain("内部说明", error.Message, StringComparison.Ordinal);
        var entry = Assert.Single(f.Audit());
        Assert.Contains(entry.State, pair => pair.Key == "Outcome" && Equals(pair.Value, outcome));
        Assert.Contains(entry.State, pair => pair.Key == "Tool" && Equals(pair.Value, "list_citation_domains"));
        Assert.Contains(entry.State, pair => pair.Key == "SiteId" && Equals(pair.Value, f.Site));
        Assert.Contains(entry.State, pair => pair.Key == "TokenId" && Equals(pair.Value, f.TokenId));
        Assert.Contains(entry.State, pair => pair.Key == "TenantId" && Equals(pair.Value, f.Tenant));
        Assert.Contains(entry.State, pair => pair.Key == "ElapsedMs");
    }

    [Fact]
    public async Task SummaryPromptsDomainsAndShareOfVoiceReachTheStoreWithTenantAndSite()
    {
        var f = new Fixture();
        void Expect(string method, Func<object?[], object?> result) => f.Analytics.Handlers[method] = args =>
        {
            Assert.Equal(f.Tenant, args[0]);
            Assert.Equal(f.Site, args[1]);
            return result(args);
        };
        Expect(nameof(IAnalyticsQueryStore.MonitoringSummaryAsync), _ => Task.FromResult(new MonitoringSummaryView(McpFixtures.Meta(),
            12, 3, 2, MetricRatio.Of(6, 12), new LatencySummary(1500, 1200, 3000, 12), null, new Dictionary<string, long> { ["healthy"] = 2 },
            new MonitoringComparison(MetricRatio.Of(3, 10), 20, new LatencySummary(null, null, null, 0), null),
            new MonitoringQuality(1, 1, 0, 0, 0, 0, new Dictionary<string, long>(), "rule", 3))));
        Expect(nameof(IAnalyticsQueryStore.PromptsAsync), args =>
        {
            Assert.Equal(20, args[5]);
            Assert.Null(args[6]);
            return Task.FromResult(new AnalyticsPage<PromptPerformanceView>(McpFixtures.Meta(),
                [new(Guid.NewGuid(), "推荐吹风机", 4, 4, 2, MetricRatio.Of(2, 4), MetricRatio.Of(0, 4), MetricRatio.Of(1, 4), null, 0)], null));
        });
        Expect(nameof(IAnalyticsQueryStore.CitationDomainsAsync), _ => Task.FromResult(new AnalyticsPage<CitationDomainRow>(McpFixtures.Meta(),
            [new("news.example", 5, MetricRatio.Of(5, 10), 3, 2, null, null, "third_party", "news", "/evidence")], "c2")));
        Expect(nameof(IAnalyticsQueryStore.ShareOfVoiceAsync), args =>
        {
            Assert.Equal(10, args[4]);
            return Task.FromResult(new ShareOfVoiceView(McpFixtures.Meta(), McpFixtures.Metrics(1, 1), [], [], MetricRatio.Of(0, 0), [],
                McpFixtures.Metrics(1, 1)));
        });

        var summary = await f.Tools.GetMonitoringSummary(f.Site, from: "2026-11-01", to: "2026-11-03");
        Assert.Equal(12, summary.TotalChecks);
        Assert.Equal(1500, summary.AverageCollectionMs);
        var prompts = await f.Tools.ListPrompts(f.Site);
        Assert.Equal("推荐吹风机", Assert.Single(prompts.Items).Prompt);
        var domains = await f.Tools.ListCitationDomains(f.Site);
        Assert.Equal("news.example", Assert.Single(domains.Items).Domain);
        Assert.Equal("c2", domains.NextCursor);
        var share = await f.Tools.GetShareOfVoice(f.Site);
        Assert.Empty(share.Sources);
        Assert.Equal(4, f.Audit().Count(entry => entry.State.Any(pair => pair.Key == "Outcome" && Equals(pair.Value, "ok"))));
    }

    private sealed class Fixture
    {
        private readonly CapturingLoggerProvider logs = new();

        internal Fixture(IReadOnlyList<Guid>? siteIds = null, Guid? site = null)
        {
            Site = site ?? Guid.NewGuid();
            (var management, Management) = McpStoreProxy.Create<IManagementStore>();
            (var analytics, Analytics) = McpStoreProxy.Create<IAnalyticsQueryStore>();
            Management.Handlers[nameof(IManagementStore.GetBrandAsync)] = args =>
                Task.FromResult((Guid)args[0]! == Tenant && (Guid)args[1]! == Site ? McpFixtures.Brand(Site) : null);
            var context = new DefaultHttpContext();
            context.Items[McpCallerContext.ItemKey] = new McpCallerContext(TokenId, Tenant, siteIds);
            Tools = new McpReadTools(management, analytics, new HttpContextAccessor { HttpContext = context },
                logs.For<McpReadTools>());
        }

        internal Guid Tenant { get; } = Guid.NewGuid();
        internal Guid TokenId { get; } = Guid.NewGuid();
        internal Guid Site { get; }
        internal McpStoreProxy Management { get; }
        internal McpStoreProxy Analytics { get; }
        internal McpReadTools Tools { get; }

        internal IReadOnlyList<(string Category, string Message, IReadOnlyList<KeyValuePair<string, object?>> State)> Audit()
            => logs.Entries.Where(entry => entry.Message.StartsWith("MCP tool call", StringComparison.Ordinal)).ToArray();
    }
}
