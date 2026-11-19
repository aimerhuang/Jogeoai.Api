using System.Text.Json;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Jogeoai.Providers.Abstractions;
using Jogeoai.Providers.Cloro;
using static Jogeoai.Tests.CloroAdapterTestFixture;

namespace Jogeoai.Tests;

public sealed class GoogleAiOverviewAdapterTests
{
    // Constructed from the official Cloro docs summary supplied on 2026-10-16 plus the field layout seen in one
    // live GOOGLE probe the same day (top-level organicResults / peopleAlsoAsk / relatedSearches / aioverview);
    // not a stored Provider capture. The async `response` envelope is used. AI Overview has no model and no
    // search-query field; `citationPills[].position` points into `sources`. Item shapes of relatedLinks, videos,
    // places, map and ads are illustrative: none of them may ever become a citation.
    private const string OfficialShape = """
        {"organicResults":[{"position":1,"title":"自然结果","link":"https://organic.example.org/1","snippet":"摘要"}],
         "peopleAlsoAsk":[{"question":"相关问题","link":"https://paa.example.org/q"}],
         "relatedSearches":[{"query":"相关搜索","link":"https://related-search.example.org/s"}],
         "ads":[{"link":"https://ads-top.example.org/a"}],
         "relatedLinks":[{"url":"https://related-top.example.org/r"}],
         "aioverview":{"markdown":"AI 概览 **回答**，正文链接 https://body.example.org/ 不是引用","text":"AI 概览纯文本",
          "sources":[
           {"position":1,"url":"https://www.Notion.so/help/guides#intro","label":"Notion 指南","description":"官方帮助"},
           {"position":2,"url":"https://github.com/google/docs","label":"","description":"仓库"},
           {"position":3,"url":"https://www.statista.com/statistics/1","label":"未被引用的来源","description":"只在 sources 中"}],
          "citationPills":[
           {"label":"Notion 指南","citationPillId":1,"url":"https://www.Notion.so/help/guides#pill","domain":"wrong-domain.example.org","position":1},
           {"label":"","citationPillId":2,"url":"https://github.com/google/docs","domain":"github.com","position":2,"description":"可选描述"}],
          "relatedLinks":[{"url":"https://related.example.org/r","title":"相关链接"}],
          "videos":[{"url":"https://videos.example.org/watch?v=1","title":"视频"}],
          "places":[{"url":"https://places.example.org/p/1","name":"地点"}],
          "map":{"url":"https://maps.example.org/m"},
          "ads":[{"url":"https://ads.example.org/a"}]}}
        """;

    [Fact]
    public async Task OverviewSourcesThenPillsAreFlattenedAndNoOtherSerpLinkBecomesACitation()
    {
        var result = await NormalizeAsync(OfficialShape, "google-ai-overview");

        Assert.Equal("AI 概览 **回答**，正文链接 https://body.example.org/ 不是引用", result.AnswerText);
        Assert.True(result.AnswerPresent);
        Assert.Equal(("cloro", "google-ai-overview", "scraped", "observed_surface"),
            (result.Provider, result.Engine, result.CaptureMode, result.EvidenceClass));
        // sources (3) then citationPills (2); saved index is the flattened row, not the upstream `position`; no dedupe.
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, result.Citations.Select(c => c.CitationIndex));
        Assert.Equal(new[] { "https://notion.so/help/guides", "https://github.com/google/docs",
            "https://statista.com/statistics/1", "https://notion.so/help/guides", "https://github.com/google/docs" },
            result.Citations.Select(c => c.Url));
        Assert.Equal(new[] { "notion.so", "github.com", "statista.com", "notion.so", "github.com" },
            result.Citations.Select(c => c.Domain));
        Assert.Equal(new[] { "Notion 指南", null, "未被引用的来源", "Notion 指南", null }, result.Citations.Select(c => c.Title));
        Assert.DoesNotContain(result.Citations, c => c.Domain.Contains("wrong-domain", StringComparison.Ordinal));
        foreach (var host in new[] { "body.", "organic.", "paa.", "related-search.", "ads-top.", "related-top.",
                     "related.", "videos.", "places.", "maps.", "ads." })
            Assert.DoesNotContain(result.Citations, c => c.Url.Contains("//" + host, StringComparison.Ordinal));

        Assert.Equal("google-ai-overview", result.ModelVersion);
        Assert.False(result.ResultQuality!.ModelVersionProvided);
        Assert.False(result.ResultQuality.AnswerRedacted);
        Assert.Null(result.ResultQuality.AnswerAbsence);
        // relatedSearches / peopleAlsoAsk are not search queries.
        Assert.Equal("not_exposed", result.QueryAvailability);
        Assert.Empty(result.WebQueries);
        var q = result.ResultQuality.Citations;
        Assert.Equal(("available", "complete", 5, 5, 0, 0, 0),
            (q.Availability, q.Completeness, q.ReceivedCount, q.RetainedCount, q.FilteredCount, q.InvalidCount, q.TruncatedCount));
        Assert.Equal(q.RetainedCount, result.Citations.Count);
        Assert.Empty(q.Reasons);
        Assert.Equal("cloro-v2", result.ParserVersion);
    }

    [Theory]
    // Google showed no AI Overview: the key is missing or null. Cloro still returns 200 and still bills.
    [InlineData("{\"organicResults\":[{\"position\":1,\"link\":\"https://organic.example.org/1\"}]}", "ai_overview_not_shown")]
    [InlineData("{\"organicResults\":[],\"aioverview\":null}", "ai_overview_not_shown")]
    [InlineData("{}", "ai_overview_not_shown")]
    // An overview object without text is an ordinary empty answer, not "not shown".
    [InlineData("{\"aioverview\":{}}", null)]
    [InlineData("{\"aioverview\":{\"markdown\":\"\",\"text\":\"\"}}", null)]
    public async Task MissingOrNullOverviewIsFlaggedButEmptyOverviewIsAnOrdinaryEmptyAnswer(string response, string? absence)
    {
        var result = await NormalizeAsync(response, "google-ai-overview");
        Assert.False(result.AnswerPresent);
        Assert.Empty(result.AnswerText);
        Assert.Equal(absence, result.ResultQuality!.AnswerAbsence);
        Assert.Equal("google-ai-overview", result.ModelVersion);
        Assert.False(result.ResultQuality.ModelVersionProvided);
        Assert.Equal("not_exposed", result.QueryAvailability);
        Assert.Empty(result.WebQueries);
        Assert.Empty(result.Citations);
        var q = result.ResultQuality.Citations;
        Assert.Equal(("not_exposed", "unknown", (int?)null), (q.Availability, q.Completeness, q.ReceivedCount));
    }

    [Theory]
    [InlineData("chatgpt")]
    [InlineData("perplexity")]
    [InlineData("copilot")]
    [InlineData("gemini")]
    [InlineData("google-ai-mode")]
    public async Task OtherEnginesNeverReceiveTheOverviewAbsenceFlag(string engine)
    {
        var result = await NormalizeAsync("{\"organicResults\":[]}", engine);
        Assert.False(result.AnswerPresent);
        Assert.Null(result.ResultQuality!.AnswerAbsence);
    }

    [Theory]
    [InlineData("{\"aioverview\":\"AI 概览字符串\"}")]
    [InlineData("{\"aioverview\":[{\"markdown\":\"数组\"}]}")]
    [InlineData("{\"aioverview\":true}")]
    public async Task NonObjectOverviewIsAnInvalidResponse(string response)
    {
        var error = await Assert.ThrowsAsync<CloroProviderException>(() => NormalizeAsync(response, "google-ai-overview"));
        Assert.Equal("INVALID_PROVIDER_RESPONSE", error.Code);
    }

    [Fact]
    public async Task TextFallbackFailureAndSynchronousEnvelopeRemainDistinct()
    {
        var textOnly = await NormalizeAsync("{\"aioverview\":{\"text\":\"仅纯文本\",\"sources\":[]}}", "google-ai-overview");
        Assert.Equal(("仅纯文本", true), (textOnly.AnswerText, textOnly.AnswerPresent));
        Assert.Equal(("empty", "complete"), (textOnly.ResultQuality!.Citations.Availability, textOnly.ResultQuality.Citations.Completeness));

        var adapter = Create(new FakeHandler(_ => Json("{\"task\":{\"status\":\"FAILED\"},\"response\":{\"aioverview\":{\"text\":\"private-provider-body\"}}}")));
        var failed = await adapter.GetAsync("overview-task");
        Assert.Equal(("FAILED", "CLORO_TASK_FAILED"), (failed.Status, failed.ErrorCode));
        Assert.DoesNotContain("private-provider-body", failed.Payload.GetRawText());
        Assert.Throws<CloroProviderException>(() => adapter.Normalize(failed, "google-ai-overview"));
        var synchronous = Create(new FakeHandler(_ => Json("{\"task\":{\"status\":\"COMPLETED\"},\"result\":{\"aioverview\":{\"text\":\"answer\"}}}")));
        var error = await Assert.ThrowsAsync<CloroProviderException>(async () =>
            synchronous.Normalize(await synchronous.GetAsync("overview-task"), "google-ai-overview"));
        Assert.Equal("INVALID_PROVIDER_RESPONSE", error.Code);
    }

    [Theory]
    [InlineData("{\"sources\":[]}", "empty", "complete", 0, 0, 0, 0, null)]
    [InlineData("{}", "not_exposed", "unknown", null, 0, 0, 0, null)]
    [InlineData("{\"sources\":{\"position\":1,\"url\":\"https://github.com/a\"}}", "unknown", "unknown", null, 0, 0, 0, "INVALID_SOURCE_SHAPE")]
    [InlineData("{\"sources\":[],\"citationPills\":[{\"citationPillId\":1,\"position\":1,\"label\":\"缺少 url\"}]}",
        "unknown", "partial", 1, 0, 0, 1, "INVALID_SOURCE_SHAPE")]
    [InlineData("{\"sources\":[{\"position\":1,\"url\":\"http://localhost/a\"},{\"position\":2,\"url\":\"https://user:pass@github.com/private\"}],\"citationPills\":[{\"citationPillId\":1,\"position\":1,\"url\":\"https://github.com/?token=private\",\"domain\":\"github.com\",\"label\":\"\"}]}",
        "available", "partial", 3, 0, 3, 0, "UNSAFE_SOURCE")]
    public async Task OverviewSourcesDistinguishExplicitEmptyMissingMalformedAndFiltered(string overview,
        string availability, string completeness, int? received, int retained, int filtered, int invalid, string? reason)
    {
        var result = await NormalizeAsync("{\"aioverview\":" + overview + "}", "google-ai-overview");
        var q = result.ResultQuality!.Citations;
        Assert.Equal(availability, q.Availability);
        Assert.Equal(completeness, q.Completeness);
        Assert.Equal(received, q.ReceivedCount);
        Assert.Equal(retained, q.RetainedCount);
        Assert.Equal(filtered, q.FilteredCount);
        Assert.Equal(invalid, q.InvalidCount);
        Assert.Equal(0, q.TruncatedCount);
        Assert.Equal(retained, result.Citations.Count);
        Assert.False(result.AnswerPresent);
        Assert.Null(result.ResultQuality.AnswerAbsence);
        if (reason is null) Assert.Empty(q.Reasons); else Assert.Contains(reason, q.Reasons);
    }

    [Fact]
    public async Task TopLevelSourcesOutsideTheOverviewAreIgnored()
    {
        var result = await NormalizeAsync("""
            {"sources":[{"url":"https://top-level.example.org/a"}],"citationPills":[{"url":"https://top-level.example.org/b"}],
             "aioverview":{"text":"答案"}}
            """, "google-ai-overview");
        Assert.Empty(result.Citations);
        Assert.Equal("not_exposed", result.ResultQuality!.Citations.Availability);
    }

    [Fact]
    public async Task LimitAppliesToFlattenedOverviewSourcesAndPills()
    {
        var result = await NormalizeAsync(JsonSerializer.Serialize(new
        {
            aioverview = new
            {
                text = "答案",
                sources = Enumerable.Range(1, 60).Select(i => new { position = i, url = $"https://github.com/source/{i}", label = $"来源 {i}" }),
                citationPills = Enumerable.Range(1, 45).Select(i => new
                {
                    label = "", citationPillId = i, url = $"https://github.com/source/{i}#pill", domain = "github.com", position = i,
                }),
            },
        }), "google-ai-overview");
        var q = result.ResultQuality!.Citations;
        Assert.Equal(("available", "partial"), (q.Availability, q.Completeness));
        Assert.Equal((105, 100, 0, 0, 5), (q.ReceivedCount, q.RetainedCount, q.FilteredCount, q.InvalidCount, q.TruncatedCount));
        Assert.Equal(new[] { "SOURCE_LIMIT" }, q.Reasons);
        Assert.Equal(100, result.Citations.Count);
        Assert.Equal(("https://github.com/source/1", 61), (result.Citations[60].Url, result.Citations[60].CitationIndex));
        Assert.Equal(("https://github.com/source/40", 100), (result.Citations[^1].Url, result.Citations[^1].CitationIndex));
    }

    [Fact]
    public async Task PollBoundaryRedactsInsideTheNestedOverviewAndFlagsRedaction()
    {
        var result = await NormalizeAsync("""
            {"html":"<!doctype html><html>private-page</html>","screenshot":"data:image/png;base64,private-shot",
             "aioverview":{"markdown":"AI 概览 Bearer private-value test-api-key 回答","text":"纯文本",
              "sources":[
               {"position":1,"url":"https://github.com/a","label":"Bearer private-value"},
               {"position":2,"url":"https://github.com/test-api-key","label":"密钥路径"}],
              "citationPills":[
               {"label":"<html><body>raw-pill</body></html>","citationPillId":1,"url":"https://github.com/a","domain":"github.com","position":1}],
              "apiKey":"secret-key","headers":{"Authorization":"Bearer private-value"},"cookies":"session=private-cookie"}}
            """, "google-ai-overview");
        Assert.True(result.ResultQuality!.AnswerRedacted);
        Assert.Equal("AI 概览 Bearer [redacted] [redacted] 回答", result.AnswerText);
        Assert.Equal(new[] { "https://github.com/a", "https://github.com/a" }, result.Citations.Select(c => c.Url));
        Assert.Equal(new[] { "Bearer [redacted]", "[redacted]" }, result.Citations.Select(c => c.Title));
        var q = result.ResultQuality.Citations;
        Assert.Equal((3, 2, 1), (q.ReceivedCount, q.RetainedCount, q.FilteredCount));
        Assert.Equal("partial", q.Completeness);
        Assert.Contains("SOURCE_REDACTED", q.Reasons);
        Assert.Contains("UNSAFE_SOURCE", q.Reasons);
        var raw = result.RawOutput.GetRawText();
        foreach (var value in new[] { "private-value", "test-api-key", "secret-key", "Authorization", "private-cookie",
                     "<html>", "<!doctype", "private-page", "raw-pill", "screenshot", "private-shot", "data:image" })
            Assert.DoesNotContain(value, raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SubmissionSendsQueryCountryOverviewIncludeAndLocaleAsInterfaceLanguage()
    {
        var handler = new FakeHandler(_ => Json("{\"task\":{\"id\":\"overview-task\"}}"));
        var result = await Create(handler).SubmitAsync(new("google-ai-overview", "  深圳 SZULINK %_ 保留原文  ", "zh-CN", "us"));
        Assert.Equal("overview-task", result.ExternalTaskId);
        using var json = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        Assert.Equal(new[] { "payload", "taskType" }, json.RootElement.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("GOOGLE", json.RootElement.GetProperty("taskType").GetString());
        var payload = json.RootElement.GetProperty("payload");
        Assert.Equal(new[] { "country", "hl", "include", "query" }, payload.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("  深圳 SZULINK %_ 保留原文  ", payload.GetProperty("query").GetString());
        Assert.Equal("US", payload.GetProperty("country").GetString());
        // Live probe 2026-10-16: Cloro accepted hl="zh-CN" unchanged, so the locale is not reduced to its primary subtag.
        Assert.Equal("zh-CN", payload.GetProperty("hl").GetString());
        var include = Assert.Single(payload.GetProperty("include").EnumerateObject());
        Assert.Equal("aioverview", include.Name);
        var markdown = Assert.Single(include.Value.EnumerateObject());
        Assert.Equal(("markdown", JsonValueKind.True), (markdown.Name, markdown.Value.ValueKind));
        Assert.Contains("\"hl\":\"zh-CN\"", handler.RequestBodies[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task BlankLocaleSendsNoInterfaceLanguage(string locale)
    {
        var handler = new FakeHandler(_ => Json("{\"task\":{\"id\":\"overview-task\"}}"));
        await Create(handler).SubmitAsync(new("google-ai-overview", "What should I buy?", locale, "us"));
        Assert.Equal("""{"taskType":"GOOGLE","payload":{"query":"What should I buy?","country":"US","include":{"aioverview":{"markdown":true}}}}""",
            Assert.Single(handler.RequestBodies));
    }

    [Theory]
    // Byte-for-byte request bodies of the other engines; a locale is supplied but must never be sent as hl.
    [InlineData("chatgpt", """{"taskType":"CHATGPT","payload":{"prompt":"What should I buy?","country":"US","include":{"markdown":true,"searchQueries":true}}}""")]
    [InlineData("perplexity", """{"taskType":"PERPLEXITY","payload":{"prompt":"What should I buy?","country":"US","include":{"markdown":true}}}""")]
    [InlineData("copilot", """{"taskType":"COPILOT","payload":{"prompt":"What should I buy?","country":"US","include":{"markdown":true}}}""")]
    [InlineData("gemini", """{"taskType":"GEMINI","payload":{"prompt":"What should I buy?","country":"US","include":{"markdown":true}}}""")]
    [InlineData("google-ai-mode", """{"taskType":"AIMODE","payload":{"prompt":"What should I buy?","country":"US","include":{"markdown":true}}}""")]
    public async Task OtherEnginePayloadsStayByteIdentical(string engine, string expected)
    {
        foreach (var locale in new[] { "zh-CN", "en-US" })
        {
            var handler = new FakeHandler(_ => Json("{\"task\":{\"id\":\"task-1\"}}"));
            await Create(handler).SubmitAsync(new(engine, "What should I buy?", locale, "us"));
            Assert.Equal(expected, Assert.Single(handler.RequestBodies));
        }
    }

    [Fact]
    public async Task AbsenceFlagRoundTripsThroughStoredMetadata()
    {
        var notShown = await NormalizeAsync("{\"organicResults\":[]}", "google-ai-overview");
        var json = ResultQualityCodec.Encode(notShown, notShown.AnswerText)!;
        Assert.Contains("\"answerAbsence\":\"ai_overview_not_shown\"", json, StringComparison.Ordinal);
        var decoded = ResultQualityCodec.Decode(json)!;
        Assert.Equal("ai_overview_not_shown", decoded.AnswerAbsence);
        Assert.Equal(1, decoded.SchemaVersion);
        Assert.Equal("not_exposed", decoded.Citations.Availability);

        var shown = await NormalizeAsync(OfficialShape, "google-ai-overview");
        Assert.Null(ResultQualityCodec.Decode(ResultQualityCodec.Encode(shown, shown.AnswerText))!.AnswerAbsence);

        var unknown = notShown with { ResultQuality = notShown.ResultQuality! with { AnswerAbsence = "provider_said_so" } };
        Assert.Equal("INVALID_RESULT_QUALITY", Assert.Throws<CollectorStoreException>(() =>
            ResultQualityCodec.Encode(unknown, unknown.AnswerText)).Code);
        Assert.Null(ResultQualityCodec.Decode(json.Replace("ai_overview_not_shown", "provider_said_so", StringComparison.Ordinal)));
    }

    // Without a known absence the stored metadata must equal what the pre-field record shape serialized.
    [Theory]
    [InlineData("chatgpt", "{\"organicResults\":[]}")]
    [InlineData("perplexity", "{\"text\":\"answer\",\"sources\":[{\"url\":\"https://example.com/a\",\"title\":\"A\"}]}")]
    [InlineData("copilot", "{\"text\":\"answer\",\"sources\":[]}")]
    [InlineData("gemini", "{\"text\":\"answer\"}")]
    [InlineData("google-ai-mode", "{\"text\":\"answer\"}")]
    [InlineData("google-ai-overview", OfficialShape)]
    public async Task MetadataWithoutAbsenceStaysByteIdenticalToThePreFieldShape(string engine, string response)
    {
        var answer = await NormalizeAsync(response, engine);
        var json = ResultQualityCodec.Encode(answer, answer.AnswerText)!;
        Assert.DoesNotContain("answerAbsence", json, StringComparison.Ordinal);
        var view = ResultQualityCodec.Decode(json)!;
        var legacy = JsonSerializer.Serialize(new LegacyResultQualityView(view.SchemaVersion, view.AnswerTruncated,
            view.AnswerRedacted, view.ModelVersionProvided, view.Citations), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(legacy, json);
    }

    private const string LegacyMetadata = """
        {"schemaVersion":1,"answerTruncated":false,"answerRedacted":false,"modelVersionProvided":false,
         "citations":{"availability":"empty","completeness":"complete","receivedCount":0,"retainedCount":0,
          "filteredCount":0,"invalidCount":0,"truncatedCount":0,"reasons":[]}}
        """;

    [Fact]
    public void HistoricMetadataWithoutTheFieldDecodesAsUnknownAbsence()
    {
        var decoded = ResultQualityCodec.Decode(LegacyMetadata)!;
        Assert.Null(decoded.AnswerAbsence);
        Assert.Equal("empty", decoded.Citations.Availability);
    }

    [Fact]
    public void RollbackSafetyUnknownPropertiesAreIgnoredOnDeserialization()
    {
        // New code reading future fields, and the pre-change record shape reading the new field, both succeed.
        var future = LegacyMetadata.Replace("\"schemaVersion\":1,", "\"schemaVersion\":1,\"futureField\":\"x\",", StringComparison.Ordinal);
        Assert.NotNull(ResultQualityCodec.Decode(future));
        var current = LegacyMetadata.Replace("\"schemaVersion\":1,", "\"schemaVersion\":1,\"answerAbsence\":\"ai_overview_not_shown\",", StringComparison.Ordinal);
        var legacy = JsonSerializer.Deserialize<LegacyResultQualityView>(current, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(1, legacy.SchemaVersion);
        Assert.Equal("empty", legacy.Citations.Availability);
    }

    [Fact]
    public void ModelUnavailableHasAStableSafeCodeAndChineseMessage()
    {
        Assert.Equal("CLORO_MODEL_UNAVAILABLE", CloroVisibilityStore.SafeErrorCode("CLORO_MODEL_UNAVAILABLE"));
        Assert.Equal("Cloro 暂时无法访问该模型。", CloroVisibilityStore.SafeErrorMessage("CLORO_MODEL_UNAVAILABLE"));
        Assert.Equal("Cloro 拒绝了提交请求。", CloroVisibilityStore.SafeErrorMessage("CLORO_SUBMIT_FAILED"));
    }

    // Shape of ResultQualityView before AnswerAbsence existed.
    private sealed record LegacyResultQualityView(int SchemaVersion, bool? AnswerTruncated, bool? AnswerRedacted,
        bool? ModelVersionProvided, CitationQualityView Citations);
}
