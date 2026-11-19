using System.Text.Json;
using Jogeoai.Providers.Cloro;
using static Jogeoai.Tests.CloroAdapterTestFixture;

namespace Jogeoai.Tests;

public sealed class PerplexityAdapterTests
{
    // Constructed from official Cloro HTML docs read 2026-10-11, not live Provider captures:
    // https://cloro.dev/docs/api-reference/endpoint/monitor-perplexity
    // https://cloro.dev/docs/api-reference/endpoint/perplexity/sources
    // https://cloro.dev/docs/api-reference/endpoint/perplexity/citation-pills
    // The official pages show the synchronous `result` envelope; these fixtures use the async `response` envelope.
    // Perplexity has no `model` field; `citationPills[].position` points into `sources`; `related_queries` are
    // suggested follow-ups, not search queries. Only the top-level media/shopping field names come from the docs;
    // their item shapes below are illustrative.
    private const string OfficialShape = """
        {"markdown":"Perplexity 回答 [1][2]，正文链接 https://body.example.org/ 不是引用","text":"Perplexity 纯文本回答",
         "sources":[
          {"position":1,"url":"https://www.Notion.so/help/guides#intro","label":"Notion 指南","description":"官方帮助"},
          {"position":2,"url":"https://github.com/perplexity-ai/docs","label":"","description":"仓库"},
          {"position":3,"url":"https://www.statista.com/statistics/1","label":"未被引用的来源","description":"只在 sources 中"}],
         "citationPills":[
          {"label":"Notion 指南","citationPillId":1,"url":"https://www.Notion.so/help/guides#pill","domain":"wrong-domain.example.org","position":1},
          {"label":"","citationPillId":1,"url":"https://github.com/perplexity-ai/docs","domain":"github.com","position":2,"description":"可选描述"}],
         "search_model_queries":["perplexity 检索词一","检索词二"],
         "related_queries":["推荐追问一","推荐追问二"],
         "images":[{"url":"https://images.example.org/a.png","title":"图片"}],
         "videos":[{"url":"https://videos.example.org/watch?v=1","title":"视频"}],
         "hotels":[{"url":"https://hotels.example.org/h/1","name":"酒店"}],
         "places":[{"url":"https://places.example.org/p/1","name":"地点"}],
         "shopping_cards":[{"url":"https://shopping.example.org/card",
          "products":[{"url":"https://shopping.example.org/product/1","offers":[{"url":"https://offers.example.org/o/1"}]}]}]}
        """;

    [Fact]
    public async Task OnePillWithTwoSourcesKeepsUncitedSourceEmptyLabelAndUrlDomainInFlattenedOrder()
    {
        var result = await NormalizeAsync(OfficialShape, "perplexity");

        Assert.Equal("Perplexity 回答 [1][2]，正文链接 https://body.example.org/ 不是引用", result.AnswerText);
        Assert.True(result.AnswerPresent);
        Assert.Equal(("cloro", "perplexity", "scraped", "observed_surface"),
            (result.Provider, result.Engine, result.CaptureMode, result.EvidenceClass));
        // sources (3) then citationPills (2); saved index is the flattened row, not the upstream `position`.
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, result.Citations.Select(c => c.CitationIndex));
        Assert.Equal(new[] { "https://notion.so/help/guides", "https://github.com/perplexity-ai/docs",
            "https://statista.com/statistics/1", "https://notion.so/help/guides", "https://github.com/perplexity-ai/docs" },
            result.Citations.Select(c => c.Url));
        Assert.Equal(new[] { "notion.so", "github.com", "statista.com", "notion.so", "github.com" },
            result.Citations.Select(c => c.Domain));
        Assert.Equal(new[] { "Notion 指南", null, "未被引用的来源", "Notion 指南", null }, result.Citations.Select(c => c.Title));
        Assert.DoesNotContain(result.Citations, c => c.Domain.Contains("wrong-domain", StringComparison.Ordinal));
        // Body, image, video, hotel, place and shopping links are never promoted to citations.
        foreach (var host in new[] { "body.", "images.", "videos.", "hotels.", "places.", "shopping.", "offers." })
            Assert.DoesNotContain(result.Citations, c => c.Url.Contains(host, StringComparison.Ordinal));

        Assert.Equal("perplexity", result.ModelVersion);
        Assert.False(result.ResultQuality!.ModelVersionProvided);
        Assert.False(result.ResultQuality.AnswerRedacted);
        Assert.Equal("available", result.QueryAvailability);
        Assert.Equal(new[] { "perplexity 检索词一", "检索词二" }, result.WebQueries);
        var q = result.ResultQuality.Citations;
        Assert.Equal(("available", "complete", 5, 5, 0, 0, 0),
            (q.Availability, q.Completeness, q.ReceivedCount, q.RetainedCount, q.FilteredCount, q.InvalidCount, q.TruncatedCount));
        Assert.Equal(q.RetainedCount, result.Citations.Count);
        Assert.Empty(q.Reasons);
        Assert.Equal("cloro-v2", result.ParserVersion);
    }

    [Theory]
    [InlineData("{}", "not_exposed", new string[0])]
    [InlineData("{\"search_model_queries\":[\"perplexity 检索词\"]}", "available", new[] { "perplexity 检索词" })]
    [InlineData("{\"search_model_queries\":[]}", "empty", new string[0])]
    [InlineData("{\"related_queries\":[\"推荐追问\"]}", "not_exposed", new string[0])]
    [InlineData("{\"search_model_queries\":[],\"related_queries\":[\"推荐追问\"]}", "empty", new string[0])]
    [InlineData("{\"search_model_queries\":\"perplexity 检索词\"}", "unknown", new string[0])]
    [InlineData("{\"search_model_queries\":false,\"related_queries\":[\"推荐追问\"]}", "unknown", new string[0])]
    public async Task VersionFallsBackAndOnlySearchModelQueriesBecomeQueries(string response, string availability, string[] queries)
    {
        var result = await NormalizeAsync(response, "perplexity");
        Assert.Equal("perplexity", result.ModelVersion);
        Assert.False(result.ResultQuality!.ModelVersionProvided);
        Assert.Equal(availability, result.QueryAvailability);
        Assert.Equal(queries, result.WebQueries);
        Assert.DoesNotContain("推荐追问", result.WebQueries);
    }

    [Theory]
    // Explicit empty sources with citationPills omitted (no pill means no field) is a valid empty result.
    [InlineData("{\"sources\":[]}", "empty", "complete", 0, 0, 0, 0, null)]
    [InlineData("{}", "not_exposed", "unknown", null, 0, 0, 0, null)]
    [InlineData("{\"sources\":{\"position\":1,\"url\":\"https://github.com/a\"}}", "unknown", "unknown", null, 0, 0, 0, "INVALID_SOURCE_SHAPE")]
    [InlineData("{\"sources\":[{\"position\":1,\"url\":\"https://github.com/a\",\"label\":\"A\"}],\"citationPills\":{\"citationPillId\":1,\"position\":1}}",
        "unknown", "partial", null, 1, 0, 0, "INVALID_SOURCE_SHAPE")]
    [InlineData("{\"sources\":[],\"citationPills\":[{\"citationPillId\":1,\"position\":1,\"label\":\"缺少 url\"}]}",
        "unknown", "partial", 1, 0, 0, 1, "INVALID_SOURCE_SHAPE")]
    [InlineData("{\"sources\":[{\"position\":1,\"url\":\"http://localhost/a\"},{\"position\":2,\"url\":\"https://user:pass@github.com/private\"}],\"citationPills\":[{\"citationPillId\":1,\"position\":1,\"url\":\"https://github.com/?token=private\",\"domain\":\"github.com\",\"label\":\"\"}]}",
        "available", "partial", 3, 0, 3, 0, "UNSAFE_SOURCE")]
    public async Task SourcesDistinguishExplicitEmptyMissingMalformedAndFiltered(string response,
        string availability, string completeness, int? received, int retained, int filtered, int invalid, string? reason)
    {
        var result = await NormalizeAsync(response, "perplexity");
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
        if (reason is null) Assert.Empty(q.Reasons); else Assert.Contains(reason, q.Reasons);
    }

    [Fact]
    public async Task LimitAppliesToFlattenedSourcesAndPills()
    {
        var result = await NormalizeAsync(JsonSerializer.Serialize(new
        {
            text = "答案",
            sources = Enumerable.Range(1, 60).Select(i => new { position = i, url = $"https://github.com/source/{i}", label = $"来源 {i}" }),
            // Each pill repeats the URL of the source its `position` points to, so repeated rows count toward the limit.
            citationPills = Enumerable.Range(1, 45).Select(i => new
            {
                label = "", citationPillId = i, url = $"https://github.com/source/{i}#pill", domain = "github.com", position = i,
            }),
        }), "perplexity");
        var q = result.ResultQuality!.Citations;
        Assert.Equal(("available", "partial"), (q.Availability, q.Completeness));
        Assert.Equal((105, 100, 0, 0, 5), (q.ReceivedCount, q.RetainedCount, q.FilteredCount, q.InvalidCount, q.TruncatedCount));
        Assert.Equal(q.ReceivedCount, q.RetainedCount + q.FilteredCount + q.InvalidCount + q.TruncatedCount);
        Assert.Equal(new[] { "SOURCE_LIMIT" }, q.Reasons);
        Assert.Equal(100, result.Citations.Count);
        Assert.Equal(("https://github.com/source/1", 61), (result.Citations[60].Url, result.Citations[60].CitationIndex));
        Assert.Equal(("https://github.com/source/40", 100), (result.Citations[^1].Url, result.Citations[^1].CitationIndex));
    }

    [Fact]
    public async Task PollBoundaryRemovesSensitiveFieldsRawHtmlAndScreenshotsAndFlagsRedaction()
    {
        var result = await NormalizeAsync("""
            {"markdown":"Perplexity Bearer private-value test-api-key 回答","text":"纯文本",
             "sources":[
              {"position":1,"url":"https://github.com/a","label":"Bearer private-value"},
              {"position":2,"url":"https://github.com/test-api-key","label":"密钥路径"}],
             "citationPills":[
              {"label":"<html><body>raw-pill</body></html>","citationPillId":1,"url":"https://github.com/a","domain":"github.com","position":1}],
             "apiKey":"secret-key","headers":{"Authorization":"Bearer private-value"},"cookies":"session=private-cookie",
             "html":"<!doctype html><html>private-page</html>","screenshot":"data:image/png;base64,private-shot",
             "images":[{"url":"https://images.example.org/a.png","thumbnail":"data:image/png;base64,private-thumb"}]}
            """, "perplexity");
        Assert.True(result.ResultQuality!.AnswerRedacted);
        Assert.Equal("Perplexity Bearer [redacted] [redacted] 回答", result.AnswerText);
        Assert.Equal(new[] { "https://github.com/a", "https://github.com/a" }, result.Citations.Select(c => c.Url));
        Assert.Equal(new[] { "Bearer [redacted]", "[redacted]" }, result.Citations.Select(c => c.Title));
        var q = result.ResultQuality.Citations;
        Assert.Equal((3, 2, 1), (q.ReceivedCount, q.RetainedCount, q.FilteredCount));
        Assert.Equal("partial", q.Completeness);
        Assert.Contains("SOURCE_REDACTED", q.Reasons);
        Assert.Contains("UNSAFE_SOURCE", q.Reasons);
        var raw = result.RawOutput.GetRawText();
        foreach (var value in new[] { "private-value", "test-api-key", "secret-key", "Authorization", "private-cookie",
                     "<html>", "<!doctype", "private-page", "raw-pill", "screenshot", "private-shot", "data:image", "private-thumb" })
            Assert.DoesNotContain(value, raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EmptyAnswerTextFallbackFailureAndSynchronousEnvelopeRemainDistinct()
    {
        var textOnly = await NormalizeAsync("{\"text\":\"仅纯文本\",\"sources\":[]}", "perplexity");
        Assert.Equal(("仅纯文本", true), (textOnly.AnswerText, textOnly.AnswerPresent));
        var empty = await NormalizeAsync("{\"markdown\":\"\",\"text\":\"\",\"sources\":[]}", "perplexity");
        Assert.Equal(("", false), (empty.AnswerText, empty.AnswerPresent));

        var adapter = Create(new FakeHandler(_ => Json("{\"task\":{\"status\":\"FAILED\"},\"response\":{\"text\":\"private-provider-body\"}}")));
        var failed = await adapter.GetAsync("perplexity-task");
        Assert.Equal(("FAILED", "CLORO_TASK_FAILED"), (failed.Status, failed.ErrorCode));
        Assert.DoesNotContain("private-provider-body", failed.Payload.GetRawText());
        Assert.Throws<CloroProviderException>(() => adapter.Normalize(failed, "perplexity"));
        var synchronous = Create(new FakeHandler(_ => Json("{\"task\":{\"status\":\"COMPLETED\"},\"result\":{\"text\":\"answer\"}}")));
        var error = await Assert.ThrowsAsync<CloroProviderException>(async () =>
            synchronous.Normalize(await synchronous.GetAsync("perplexity-task"), "perplexity"));
        Assert.Equal("INVALID_PROVIDER_RESPONSE", error.Code);
    }

    [Fact]
    public async Task SubmissionUsesPerplexityOriginalPromptUpperCountryAndMarkdownOnly()
    {
        var handler = new FakeHandler(_ => Json("{\"task\":{\"id\":\"perplexity-task\"}}"));
        var result = await Create(handler).SubmitAsync(new("perplexity", "  原问题 %_ 保留原文  ", "zh-CN", "us"));
        Assert.Equal("perplexity-task", result.ExternalTaskId);
        using var json = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        Assert.Equal(new[] { "payload", "taskType" }, json.RootElement.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("PERPLEXITY", json.RootElement.GetProperty("taskType").GetString());
        var payload = json.RootElement.GetProperty("payload");
        Assert.Equal(new[] { "country", "include", "prompt" }, payload.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("  原问题 %_ 保留原文  ", payload.GetProperty("prompt").GetString());
        Assert.Equal("US", payload.GetProperty("country").GetString());
        var include = Assert.Single(payload.GetProperty("include").EnumerateObject());
        Assert.Equal("markdown", include.Name);
        Assert.Equal(JsonValueKind.True, include.Value.ValueKind);
    }
}
