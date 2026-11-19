using System.Text.Json;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;

namespace Jogeoai.Tests;

public sealed class MonitoringAnswerDetailTests
{
    private static readonly DateTimeOffset AsOf = DateTimeOffset.Parse("2026-09-30T00:00:00Z");
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Site = Guid.NewGuid();
    private static AnalyticsFilter Filter => new(AsOf.AddDays(-30), AsOf, "UTC",
        ["cloro", "domestic_collector"], [], [], [], ["api", "scraped"],
        ["observed_surface", "api_response", "unknown"], [], [], [], "any", null, "observed", [], null, [], []);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task DetailRequiresSnapshotBeforeAnyDatabaseAccess(string? token)
    {
        var store = new AnalyticsQueryStore(new MySqlDatabase("Server=127.0.0.1;Port=1;Database=not_used;ConnectionTimeout=1"), null!);
        var error = await Assert.ThrowsAsync<CollectorStoreException>(() =>
            store.MonitoringAnswerDetailAsync(Tenant, Site, Guid.NewGuid(), token, null));
        Assert.Equal("SNAPSHOT_REQUIRED", error.Code);
        Assert.Equal(400, error.StatusCode);
    }

    [Fact]
    public async Task InvalidSearchIsRejectedBeforeAnyDatabaseAccess()
    {
        var store = new AnalyticsQueryStore(new MySqlDatabase("Server=127.0.0.1;Port=1;Database=not_used;ConnectionTimeout=1"), null!);
        var error = await Assert.ThrowsAsync<CollectorStoreException>(() =>
            store.MonitoringAnswerDetailAsync(Tenant, Site, Guid.NewGuid(), "snapshot", new string('x', 201)));
        Assert.Equal("INVALID_FILTER", error.Code);
    }

    [Theory]
    [InlineData("chatgpt")]
    [InlineData("gemini")]
    [InlineData("copilot")]
    [InlineData("perplexity")]
    [InlineData("google-ai-overview")]
    public void TenantAndSiteAuthorizationAreIndependentOfFilterMembership(string engine)
    {
        var row = Stored() with { Fact = Stored().Fact with { Engine = engine } };
        var filter = Filter with { Engines = ["outside-filter"] };
        AnalyticsQueryStore.ValidateMonitoringRecordSnapshot(row, Tenant, Site, AsOf, 10);
        Assert.False(AnalyticsQueryStore.BuildMonitoringAnswerDetail(row, [], null, filter, null).MatchesListFilters);
        Assert.True(AnalyticsQueryStore.MonitoringMatchesListFilters(row.Fact, Filter with { Engines = [engine] }, null));
        Assert.True(AnalyticsQueryStore.BuildMonitoringAnswerDetail(row, [], null, Filter with { Engines = [engine] }, null).MatchesListFilters);
        foreach (var identities in new[] { (Guid.NewGuid(), Site), (Tenant, Guid.NewGuid()) })
        {
            var error = Assert.Throws<CollectorStoreException>(() =>
                AnalyticsQueryStore.ValidateMonitoringRecordSnapshot(row, identities.Item1, identities.Item2, AsOf, 10));
            Assert.Equal("RESOURCE_NOT_FOUND", error.Code);
            Assert.Equal(404, error.StatusCode);
        }
    }

    [Theory]
    [InlineData("chatgpt")]
    [InlineData("gemini")]
    [InlineData("copilot")]
    [InlineData("perplexity")]
    [InlineData("google-ai-overview")]
    public void PostSnapshotCreationArrivalAndCompletionRequireCoordinatedRefresh(string engine)
    {
        var row = Stored() with { Fact = Stored().Fact with { Engine = engine } };
        foreach (var late in new[]
        {
            row with { CreatedAt = AsOf.AddTicks(1) },
            row with { ArrivalId = 11 },
            row with { ArrivedAt = AsOf.AddTicks(1) },
            row with { Fact = row.Fact with { CapturedAt = AsOf.AddTicks(1) } },
            row with { ArrivalId = null, ArrivedAt = null },
            row with { ArrivalId = null, ArrivedAt = null, Fact = row.Fact with { Status = "failed", CapturedAt = null } }
        })
        {
            var error = Assert.Throws<CollectorStoreException>(() =>
                AnalyticsQueryStore.ValidateMonitoringRecordSnapshot(late, Tenant, Site, AsOf, 10));
            Assert.Equal("SNAPSHOT_REFRESH_REQUIRED", error.Code);
            Assert.Equal(409, error.StatusCode);
        }
        AnalyticsQueryStore.ValidateMonitoringRecordSnapshot(row with
        {
            ArrivalId = 10,
            ArrivedAt = AsOf,
            CreatedAt = AsOf
        }, Tenant, Site, AsOf, 10);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("running")]
    [InlineData("processing")]
    public void ExplicitUnfinishedLinksKeepTrueStatusWithoutExposingResult(string status)
    {
        var row = Stored() with
        {
            ArrivalId = null,
            ArrivedAt = null,
            CreatedAt = AsOf,
            Fact = Stored().Fact with { Status = status, CapturedAt = null, DurationMs = null }
        };
        AnalyticsQueryStore.ValidateMonitoringRecordSnapshot(row, Tenant, Site, AsOf, 10);
        var detail = AnalyticsQueryStore.BuildMonitoringAnswerDetail(row, [Citation(1)], Analysis("completed"), Filter, null);
        Assert.Equal(status, detail.Status);
        Assert.False(detail.AnswerPresent);
        Assert.Null(detail.Answer);
        Assert.Empty(detail.WebQueries);
        Assert.Empty(detail.Citations);
        Assert.Null(detail.Analysis);
        Assert.Null(detail.ResultQuality);
        Assert.Null(detail.CapturedAt);
        Assert.Null(detail.DurationMs);
        Assert.False(detail.MatchesListFilters);
    }

    [Theory]
    [InlineData("cloro", 0)]
    [InlineData("domestic_collector", null)]
    public void ProvidersShareNormalizedShapeAndPreserveStoredMetadata(string provider, int? duration)
    {
        var row = Stored() with { Fact = Stored().Fact with { Provider = provider, DurationMs = duration } };
        var analysis = Analysis("pending");
        var citations = new[] { Citation(1), Citation(2) };
        var detail = AnalyticsQueryStore.BuildMonitoringAnswerDetail(row, citations, analysis, Filter, null);
        Assert.Equal(provider, detail.Provider);
        Assert.Equal(row.Fact.PromptSnapshot, detail.PromptSnapshot);
        Assert.Equal(" \nComplete **stored** answer <script>not executed</script>\n ", detail.Answer?.Text);
        Assert.Equal(row.Fact.ModelVersion, detail.ModelVersion);
        Assert.Equal(duration, detail.DurationMs);
        Assert.Equal(row.Fact.CapturedAt, detail.CapturedAt);
        Assert.Equal(row.WebQueries, detail.WebQueries);
        Assert.Equal("available", detail.QueryAvailability);
        Assert.Equal(2, detail.Citations.Count);
        Assert.Equal(detail.Citations[0].Url, detail.Citations[1].Url);
        Assert.Equal(new[] { 1, 2 }, detail.Citations.Select(c => c.Position));
        Assert.Same(analysis, detail.Analysis);
        Assert.Equal("pending", detail.Analysis!.Status);
        Assert.True(detail.MatchesListFilters);
    }

    [Theory]
    [InlineData("gemini")]
    [InlineData("copilot")]
    [InlineData("perplexity")]
    [InlineData("google-ai-overview")]
    public void QualityUsesSameArrivalFenceAndModelFallbackRemainsUnavailable(string engine)
    {
        var quality = new ResultQualityView(1, true, false, false,
            new("available", "partial", 1, 0, 1, 0, 0, ["UNSAFE_SOURCE"]));
        var row = Stored() with { ResultQuality = quality, Fact = Stored().Fact with { Engine = engine, ModelVersion = engine } };
        var detail = AnalyticsQueryStore.BuildMonitoringAnswerDetail(row, [], null, Filter, null);
        Assert.Same(quality, detail.ResultQuality);
        Assert.True(detail.ModelVersionUnavailable);
        Assert.Null(AnalyticsQueryStore.BuildMonitoringAnswerDetail(row with { ArrivalId = null }, [], null, Filter, null).ResultQuality);
        var late = row with { ArrivalId = 11 };
        Assert.Equal("SNAPSHOT_REFRESH_REQUIRED", Assert.Throws<CollectorStoreException>(() =>
            AnalyticsQueryStore.ValidateMonitoringRecordSnapshot(late, Tenant, Site, AsOf, 10)).Code);
    }

    [Fact]
    public void FilterMismatchIsMetadataNotAuthorization()
    {
        var row = Stored();
        foreach (var filter in new[]
        {
            Filter with { From = AsOf.AddHours(-1) },
            Filter with { To = row.Fact.CapturedAt!.Value },
            Filter with { Engines = ["gemini"] },
            Filter with { Statuses = ["failed"] },
            Filter with { ModelVersions = ["other"] },
            Filter with { PromptIds = [Guid.NewGuid()] }
        })
        {
            AnalyticsQueryStore.ValidateMonitoringRecordSnapshot(row, Tenant, Site, AsOf, 10);
            var detail = AnalyticsQueryStore.BuildMonitoringAnswerDetail(row, [], null, filter, null);
            Assert.NotNull(detail.Answer);
            Assert.False(detail.MatchesListFilters);
        }
        Assert.True(AnalyticsQueryStore.BuildMonitoringAnswerDetail(row, [], null, Filter,
            AnalyticsQueryStore.NormalizeMonitoringSearch(" historical question ")).MatchesListFilters);
        Assert.False(AnalyticsQueryStore.BuildMonitoringAnswerDetail(row, [], null, Filter,
            AnalyticsQueryStore.NormalizeMonitoringSearch("edited question")).MatchesListFilters);
    }

    [Fact]
    public void CopilotFilteringBeforePagingUsesHistoricalPromptAndLeavesUnanalyzedMentionUnknown()
    {
        var quality = new ResultQualityView(1, false, false, false,
            new("empty", "complete", 0, 0, 0, 0, 0, []));
        var rows = Enumerable.Range(0, 85).Select(i => Stored() with
        {
            ResultQuality = quality,
            Fact = Stored().Fact with
            {
                Engine = i < 40 ? "copilot" : "chatgpt", ModelVersion = i < 40 ? "copilot" : "actual-model",
                PromptSnapshot = "Historical Copilot question", CapturedAt = AsOf.AddMinutes(i - 100)
            }
        }).ToArray();
        var filter = Filter with { Engines = ["copilot"] };
        var search = AnalyticsQueryStore.NormalizeMonitoringSearch("copilot question");
        var matches = AnalyticsQueryStore.FilterMonitoringRecords(rows.Select(r => r.Fact)
            .Where(f => AnalyticsQueryStore.MonitoringMatchesListFilters(f, filter, search)), search).ToArray();
        Assert.Equal(40, matches.Length);
        Assert.Equal(rows[39].Fact.CheckId, matches.Take(20).First().CheckId);
        Assert.Equal(rows[19].Fact.CheckId, matches.Skip(20).Take(20).First().CheckId);
        Assert.All(matches, f => Assert.Equal("copilot", f.Engine));
        var selected = rows.Single(r => r.Fact.CheckId == matches[0].CheckId);
        AnalyticsQueryStore.ValidateMonitoringRecordSnapshot(selected, Tenant, Site, AsOf, 10);
        var detail = AnalyticsQueryStore.BuildMonitoringAnswerDetail(selected, [], null, filter, search, "unknown");
        Assert.True(detail.MatchesListFilters);
        Assert.Equal("Historical Copilot question", detail.PromptSnapshot);
        Assert.Equal("unknown", detail.MentionState);
        Assert.Null(detail.Analysis);
        Assert.Same(quality, detail.ResultQuality);
        Assert.True(detail.ModelVersionUnavailable);
        Assert.NotNull(detail.Answer);
        Assert.Null(AnalyticsQueryStore.BuildMonitoringAnswerDetail(selected with { ResultQuality = null },
            [], null, filter, search).ResultQuality);
        Assert.False(AnalyticsQueryStore.BuildMonitoringAnswerDetail(selected, [], null, filter,
            AnalyticsQueryStore.NormalizeMonitoringSearch("new edited question")).MatchesListFilters);
    }

    [Fact]
    public void EmptyAnswerFailureAndNullMetadataRemainDistinct()
    {
        var row = Stored();
        var empty = AnalyticsQueryStore.BuildMonitoringAnswerDetail(row with
        {
            Answer = "",
            WebQueries = [],
            Country = null,
            Locale = null,
            Fact = row.Fact with { AnswerPresent = false, ModelVersion = null, QueryAvailability = "empty", DurationMs = null }
        }, [], null, Filter, null);
        Assert.False(empty.AnswerPresent);
        Assert.Equal("", empty.Answer?.Text);
        Assert.Null(empty.Country);
        Assert.Null(empty.Locale);
        Assert.Null(empty.ModelVersion);
        Assert.Null(empty.DurationMs);
        var failed = AnalyticsQueryStore.BuildMonitoringAnswerDetail(row with
        {
            Answer = null,
            WebQueries = [],
            ErrorCode = "COLLECTION_FAILED",
            ErrorMessage = "采集失败。",
            Fact = row.Fact with { Status = "failed", AnswerPresent = false, QueryAvailability = "not_exposed", CapturedAt = null }
        }, [], null, Filter, null);
        Assert.Equal("failed", failed.Status);
        Assert.Null(failed.Answer);
        Assert.Equal("COLLECTION_FAILED", failed.ErrorCode);
        Assert.Equal("采集失败。", failed.ErrorMessage);
        Assert.False(failed.MatchesListFilters);
    }

    [Theory]
    [InlineData("deep", AnswerMode.Deep)]
    [InlineData("fast", AnswerMode.Fast)]
    [InlineData(null, null)]
    public void DetailReportsTheStoredAnswerModeAndNullForHistoricalChecks(string? stored, AnswerMode? expected)
    {
        var detail = AnalyticsQueryStore.BuildMonitoringAnswerDetail(Stored() with { AnswerMode = stored }, [], null, Filter, null);
        Assert.Equal(expected, detail.AnswerMode);
        // Not-arrived rows still report the mode chosen at creation (it is not answer content).
        Assert.Equal(expected, AnalyticsQueryStore.BuildMonitoringAnswerDetail(Stored() with { AnswerMode = stored, ArrivalId = null },
            [], null, Filter, null).AnswerMode);
    }

    [Fact]
    public void PublicPayloadContainsOnlyApprovedFieldsAndNestedAnswerText()
    {
        var detail = AnalyticsQueryStore.BuildMonitoringAnswerDetail(Stored(), [Citation(1)], null, Filter, null);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(detail, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var expected = new[] { "siteId", "checkId", "runId", "promptId", "promptSnapshot", "provider", "engine",
            "modelVersion", "country", "locale", "status", "answerPresent", "answer", "webQueries",
            "queryAvailability", "citations", "capturedAt", "durationMs", "errorCode", "errorMessage",
            "analysis", "matchesListFilters", "mentionState", "citationAttribution", "resultQuality", "modelVersionUnavailable",
            "brandMetrics", "reputationSentiment", "answerMode",
            // App 渠道任务（11-16）新增：采集模式、渠道、App 块与没有 URL 的引用。
            "captureMode", "channel", "app", "sourcesWithoutUrl" };
        Assert.Equal(expected.Order(), json.RootElement.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(detail.Answer!.Text, json.RootElement.GetProperty("answer").GetProperty("text").GetString());
        Assert.Equal(new[] { "domain", "position", "title", "url" },
            json.RootElement.GetProperty("citations")[0].EnumerateObject().Select(p => p.Name).Order());
    }

    [Theory]
    [InlineData("succeeded", true, "completed", false, true, false, "mentioned")]
    [InlineData("succeeded", true, "completed", false, false, false, "not_mentioned")]
    [InlineData("succeeded", true, "completed", false, true, true, "mentioned")]
    [InlineData("succeeded", true, "completed", false, false, true, "not_mentioned")]
    [InlineData("succeeded", true, "completed", true, true, false, "unknown")]
    [InlineData("succeeded", true, "completed", true, false, false, "unknown")]
    [InlineData("succeeded", true, "pending", false, false, false, "unknown")]
    [InlineData("succeeded", true, "processing", false, false, false, "unknown")]
    [InlineData("succeeded", true, "failed", false, false, false, "unknown")]
    [InlineData("succeeded", true, null, false, false, false, "unknown")]
    [InlineData("succeeded", false, "completed", false, true, false, "not_applicable")]
    [InlineData("failed", true, "completed", false, true, false, "not_applicable")]
    [InlineData("cancelled", false, null, false, false, false, "not_applicable")]
    [InlineData("pending", false, null, false, false, false, "not_applicable")]
    public void ListAndDetailShareMentionClassificationAndFilterMembership(string status, bool answerPresent,
        string? analysisStatus, bool missing, bool mentioned, bool outdated, string expected)
    {
        var brand = Guid.NewGuid();
        var competitor = Guid.NewGuid();
        var row = Stored();
        row = row with
        {
            Fact = row.Fact with
            {
                Status = status,
                AnswerPresent = answerPresent,
                AnalysisStatus = analysisStatus ?? "pending",
                ConfigurationMissing = missing,
                MentionedSubjects = mentioned ? [brand, competitor] : [competitor]
            }
        };
        var brandEvidence = new MentionEvidenceView(brand, "brand", "品牌", "品牌证据", "name",
            1, 3, "mention-v1", 1);
        var competitorEvidence = brandEvidence with { SubjectId = competitor, SubjectType = "competitor" };
        var analysis = analysisStatus is null ? null : Analysis(analysisStatus) with
        {
            ConfigurationMissing = missing,
            ConfigurationOutdated = outdated,
            Mentions = mentioned ? [brandEvidence, brandEvidence with { OffsetStart = 5, OffsetEnd = 7 }, competitorEvidence]
                : [competitorEvidence]
        };
        Assert.Equal(expected, AnalyticsQueryStore.MonitoringMentionState(row.Fact, brand));
        foreach (var mention in new string?[] { null, "mentioned", "not_mentioned", "unknown" })
        {
            var matches = mention is null || mention == expected;
            var detail = AnalyticsQueryStore.BuildMonitoringAnswerDetail(row, [], analysis, Filter, null, mention);
            Assert.Equal(expected, detail.MentionState);
            Assert.Equal(matches, detail.MatchesListFilters);
            Assert.Equal(matches, AnalyticsQueryStore.FilterMonitoringRecords([row.Fact], null, mention, brand).Any());
            Assert.Same(analysis, detail.Analysis);
            if (expected == "mentioned")
                Assert.Equal(2, detail.Analysis!.Mentions.Count(m => m.SubjectType == "brand"));
            if (expected == "not_mentioned")
                Assert.DoesNotContain(detail.Analysis!.Mentions, m => m.SubjectType == "brand");
        }
    }

    [Fact]
    public void SnapshotAnalysisRatherThanUnversionedFactOrCurrentConfigurationDeterminesDetailMembership()
    {
        // 详情原始采集事实的分析占位为 pending；真正的成员资格必须使用水位限定后的分析。
        var row = Stored();
        var positive = Analysis("completed") with
        {
            ConfigurationOutdated = true,
            Mentions = [new MentionEvidenceView(Guid.NewGuid(), "brand", "品牌", "原文中的品牌", "name", 0, 2, "v1", 1)]
        };
        var detail = AnalyticsQueryStore.BuildMonitoringAnswerDetail(row, [], positive, Filter, null, "mentioned");
        Assert.Equal("mentioned", detail.MentionState);
        Assert.True(detail.MatchesListFilters);
        Assert.True(detail.Analysis!.ConfigurationOutdated);
        // 水位之后完成的分析由读取层作为 pending 返回；不能沿用后来完成的提及结果。
        var snapshotPending = positive with { Status = "pending", Mentions = [] };
        var pending = AnalyticsQueryStore.BuildMonitoringAnswerDetail(row, [], snapshotPending, Filter, null, "unknown");
        Assert.Equal("unknown", pending.MentionState);
        Assert.True(pending.MatchesListFilters);
        Assert.False(AnalyticsQueryStore.BuildMonitoringAnswerDetail(row, [], snapshotPending, Filter, null, "mentioned").MatchesListFilters);
        Assert.False(AnalyticsQueryStore.BuildMonitoringAnswerDetail(row, [], positive, Filter, "OUTSIDE", "mentioned").MatchesListFilters);
        Assert.NotNull(AnalyticsQueryStore.BuildMonitoringAnswerDetail(row, [], positive, Filter, null, "not_mentioned").Answer);
    }

    [Fact]
    public void BrandMetricsAreSynthesizedFromSnapshotAnalysisAndCitationsLikeTheList()
    {
        var brand = Guid.NewGuid();
        var competitor = Guid.NewGuid();
        var configuration = new SubjectConfiguration(brand, 1,
            [new AnalysisSubject(brand, "brand", "Acme"), new AnalysisSubject(competitor, "competitor", "Beta")],
            [new AnalysisTerm(brand, "brand", "domain", "acme.test"), new AnalysisTerm(competitor, "competitor", "domain", "beta.test")],
            true);
        var row = Stored();
        var analysis = Analysis("completed") with
        {
            Mentions =
            [
                new MentionEvidenceView(competitor, "competitor", "Beta", "Beta leads", "name", 0, 4, "mention-v1", 1),
                new MentionEvidenceView(brand, "brand", "Acme", "while Acme follows", "name", 17, 21, "mention-v1", 1),
                new MentionEvidenceView(brand, "brand", "Acme", "Acme again", "name", 40, 44, "mention-v1", 1)
            ]
        };
        var own = new AnalysisCitationView("https://acme.test/source", "acme.test", null, 1);
        var other = new AnalysisCitationView("https://beta.test/source", "beta.test", null, 2);

        var metrics = AnalyticsQueryStore.MonitoringBrandMetrics(row, [other, own], analysis, configuration);
        Assert.Equal(2, metrics.Ranking.Value);
        Assert.Equal("check-subject-first-offset-v1", metrics.Ranking.MetricVersion);
        // 第 2 名，本品牌出现 2 次、竞品 1 次，有自有引用：40 + 30 × (1/2) + 20 × (2/3) + 10 = 78.33。
        Assert.Equal(40 + 15 + 40d / 3 + 10, metrics.Exposure.Value!.Value, 6);
        Assert.Equal(MetricRatio.Of(2, 3), metrics.Exposure.ShareOfVoice);
        Assert.Equal(MetricMean.Of(.5, 1), metrics.Exposure.PositionScore);
        Assert.Equal(MetricRatio.Of(1, 1), metrics.Exposure.OwnExternalCitationRate);

        // 同一记录经列表路径（check_subject_presence 的提及集合与 mention_evidence 的首次偏移、出现次数）得到相同结果。
        var listFact = row.Fact with { AnalysisStatus = "completed", MentionedSubjects = [brand, competitor], OwnCitation = true };
        Assert.Equal(metrics, DashboardAnalyticsProjector.CheckBrandMetrics(listFact, configuration,
            new Dictionary<Guid, SubjectStats> { [competitor] = new(0, 1), [brand] = new(17, 2) }));

        var withoutOwn = AnalyticsQueryStore.MonitoringBrandMetrics(row, [other], analysis, configuration);
        Assert.Equal(40 + 15 + 40d / 3, withoutOwn.Exposure.Value!.Value, 6);
        Assert.Equal(MetricRatio.Of(0, 1), withoutOwn.Exposure.OwnExternalCitationRate);

        var pending = AnalyticsQueryStore.MonitoringBrandMetrics(row, [own], Analysis("pending"), configuration);
        Assert.Equal("ANALYSIS_NOT_READY", pending.Ranking.UnavailableReason);
        Assert.Equal("ANALYSIS_NOT_READY", pending.Exposure.EligibilityReason);
        Assert.Equal("ANALYSIS_NOT_READY",
            AnalyticsQueryStore.MonitoringBrandMetrics(row, [own], null, configuration).Ranking.UnavailableReason);
        var missing = AnalyticsQueryStore.MonitoringBrandMetrics(row, [own], analysis with { ConfigurationMissing = true }, configuration);
        Assert.Equal("CONFIGURATION_MISSING", missing.Exposure.EligibilityReason);

        // 未到达的记录不计算，也不泄露并发完成的分析。
        var notArrived = AnalyticsQueryStore.MonitoringBrandMetrics(row with { ArrivalId = null, ArrivedAt = null },
            [own], analysis, configuration);
        Assert.Null(notArrived.Ranking.Value);
        Assert.Null(notArrived.Exposure.Value);
        Assert.Equal("ANALYSIS_NOT_READY", notArrived.Ranking.UnavailableReason);
        Assert.Equal("ANALYSIS_NOT_READY", notArrived.Exposure.EligibilityReason);

        var detail = AnalyticsQueryStore.BuildMonitoringAnswerDetail(row, [other, own], analysis, Filter, null, null, metrics);
        Assert.Same(metrics, detail.BrandMetrics);
        Assert.Null(AnalyticsQueryStore.BuildMonitoringAnswerDetail(row, [other, own], analysis, Filter, null).BrandMetrics);
    }

    private static AnalyticsQueryStore.MonitoringStoredAnswer Stored()
        => new(Tenant, Site, new AnalyticsCheckFact(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            null, null, 1, AsOf.AddDays(-1), "succeeded", true, "pending", false, false, false, [],
            "cloro", "chatgpt", "US", "en-US", "scraped", "observed_surface", [], null,
            "Historical question snapshot", "available", "actual-model", 0),
            " \nComplete **stored** answer <script>not executed</script>\n ", ["real provider query"],
            "US", "en-US", null, null, AsOf.AddDays(-2), 5, AsOf.AddDays(-1));

    private static AnalysisCitationView Citation(int position)
        => new("https://sources.example.test/same", "sources.example.test", null, position);

    private static CheckAnalysisView Analysis(string status)
        => new(Guid.NewGuid(), Guid.NewGuid(), 2, 1, status, false, false, []);
}
