using System.Net;
using System.Text.Json;
using Jogeoai.Providers.Cloro;
using static Jogeoai.Tests.CloroAdapterTestFixture;

namespace Jogeoai.Tests;

public sealed class CopilotAdapterTests
{
    // Constructed from official HTML schemas read 2026-10-09, not live Provider captures:
    // https://cloro.dev/docs/api-reference/endpoint/copilot/sources
    // https://cloro.dev/docs/api-reference/endpoint/copilot/citation-pills
    // The source fields use the existing async response envelope, not the synchronous result envelope.
    [Fact]
    public async Task OnePillWithMultipleSourcesPreservesOverlappingRowsEmptyTitlesAndSavedOrder()
    {
        var result = await NormalizeAsync("""
            {"markdown":"回答 [1]，普通链接 https://body.example.org/","text":"回答",
             "sources":[
              {"position":1,"url":"https://www.Microsoft.com/docs#first","label":"文档"},
              {"position":2,"url":"https://github.com/microsoft/vscode","label":""}],
             "citationPills":[
              {"citationPillId":1,"position":1,"url":"https://www.Microsoft.com/docs#second","domain":"wrong.example.org","label":"文档"},
              {"citationPillId":1,"position":2,"url":"https://github.com/microsoft/vscode","domain":"github.com","label":""}],
             "shoppingCards":[{"url":"https://shopping.example.org/"}],
             "mapEntries":[{"url":"https://maps.example.org/"}]}
            """, "copilot");

        Assert.Equal("回答 [1]，普通链接 https://body.example.org/", result.AnswerText);
        Assert.True(result.AnswerPresent);
        Assert.Equal(("cloro", "copilot", "scraped", "observed_surface"),
            (result.Provider, result.Engine, result.CaptureMode, result.EvidenceClass));
        Assert.Equal(new[] { 1, 2, 3, 4 }, result.Citations.Select(c => c.CitationIndex));
        Assert.Equal(new[] { "https://microsoft.com/docs", "https://github.com/microsoft/vscode",
            "https://microsoft.com/docs", "https://github.com/microsoft/vscode" }, result.Citations.Select(c => c.Url));
        Assert.Equal("microsoft.com", result.Citations[2].Domain);
        Assert.Null(result.Citations[1].Title);
        Assert.Null(result.Citations[3].Title);
        Assert.Equal("copilot", result.ModelVersion);
        Assert.False(result.ResultQuality!.ModelVersionProvided);
        Assert.Equal("not_exposed", result.QueryAvailability);
        Assert.Empty(result.WebQueries);
        var q = result.ResultQuality.Citations;
        Assert.Equal(("available", "complete", 4, 4), (q.Availability, q.Completeness, q.ReceivedCount, q.RetainedCount));
        Assert.Empty(q.Reasons);
        Assert.Equal("cloro-v2", result.ParserVersion);
    }

    [Theory]
    [InlineData("{\"sources\":[]}", "empty", "complete", 0, 0, 0, 0, null)]
    [InlineData("{}", "not_exposed", "unknown", null, 0, 0, 0, null)]
    [InlineData("{\"sources\":null}", "unknown", "unknown", null, 0, 0, 0, "INVALID_SOURCE_SHAPE")]
    [InlineData("{\"sources\":[{},null]}", "unknown", "partial", 2, 0, 0, 2, "INVALID_SOURCE_SHAPE")]
    [InlineData("{\"sources\":[{\"url\":\"https://github.com/a\"},null],\"citationPills\":false}", "unknown", "partial", null, 1, 0, 1, "INVALID_SOURCE_SHAPE")]
    [InlineData("{\"sources\":[{\"url\":\"http://localhost/a\"},{\"url\":\"https://user:pass@github.com/private\"},{\"url\":\"https://github.com/?token=private\"}]}", "available", "partial", 3, 0, 3, 0, "UNSAFE_SOURCE")]
    public async Task SourcesDistinguishExplicitEmptyMissingMalformedAndFiltered(string response,
        string availability, string completeness, int? received, int retained, int filtered, int invalid, string? reason)
    {
        var result = await NormalizeAsync(response, "copilot");
        var q = result.ResultQuality!.Citations;
        Assert.Equal(availability, q.Availability);
        Assert.Equal(completeness, q.Completeness);
        Assert.Equal(received, q.ReceivedCount);
        Assert.Equal(retained, q.RetainedCount);
        Assert.Equal(filtered, q.FilteredCount);
        Assert.Equal(invalid, q.InvalidCount);
        Assert.Equal(retained, result.Citations.Count);
        Assert.False(result.AnswerPresent);
        if (reason is null) Assert.Empty(q.Reasons); else Assert.Contains(reason, q.Reasons);
    }

    [Fact]
    public async Task LimitCountsIncludeAllReceivedCandidatesAndUnsafeUrlsDoNotBecomeEmpty()
    {
        var sources = Enumerable.Range(0, 102).Select(i => new { url = $"https://github.com/source/{i}" }).Cast<object>()
            .Concat([new { url = "https://github.com/" + new string('a', 750) }, new { url = "ftp://github.com/a" }, new { broken = true }]);
        var result = await NormalizeAsync(JsonSerializer.Serialize(new { text = "答案", sources }), "copilot");
        var q = result.ResultQuality!.Citations;
        Assert.Equal((105, 100, 2, 1, 2), (q.ReceivedCount, q.RetainedCount, q.FilteredCount, q.InvalidCount, q.TruncatedCount));
        Assert.Equal(q.ReceivedCount, q.RetainedCount + q.FilteredCount + q.InvalidCount + q.TruncatedCount);
        Assert.Equal("unknown", q.Availability); // A malformed candidate makes availability uncertain even with safe retained rows.
        Assert.Equal("partial", q.Completeness);
        Assert.Equal(new[] { "INVALID_SOURCE_SHAPE", "SOURCE_LIMIT", "UNSAFE_SOURCE" }, q.Reasons);
        Assert.Equal(100, result.Citations.Count);
        Assert.Equal(100, result.Citations[^1].CitationIndex);
    }

    [Fact]
    public async Task PollBoundaryRetainsOnlyRedactionEvidenceAndSafeFields()
    {
        var result = await NormalizeAsync("""
            {"markdown":"Bearer private-value test-api-key","sources":[
             {"url":"https://github.com/a","label":"Bearer private-value"},
             {"url":"https://github.com/test-api-key"}],
             "apiKey":"secret-key","headers":{"Authorization":"Bearer private-value"},
             "html":"<html>private</html>","screenshot":"data:image/png;base64,private"}
            """, "copilot");
        Assert.True(result.ResultQuality!.AnswerRedacted);
        Assert.Equal("Bearer [redacted] [redacted]", result.AnswerText);
        Assert.Single(result.Citations);
        Assert.Equal("Bearer [redacted]", result.Citations[0].Title);
        Assert.Equal(2, result.ResultQuality.Citations.ReceivedCount);
        Assert.Equal(1, result.ResultQuality.Citations.FilteredCount);
        Assert.Equal("partial", result.ResultQuality.Citations.Completeness);
        Assert.Contains("SOURCE_REDACTED", result.ResultQuality.Citations.Reasons);
        var raw = result.RawOutput.GetRawText();
        foreach (var value in new[] { "private-value", "test-api-key", "secret-key", "Authorization", "<html>", "screenshot" })
            Assert.DoesNotContain(value, raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SafeOverLimitSourcesRemainAvailableWithPartialRetention()
    {
        var result = await NormalizeAsync(JsonSerializer.Serialize(new
        {
            text = "答案", sources = Enumerable.Range(0, 101).Select(i => new { url = $"https://github.com/source/{i}" })
        }), "copilot");
        var q = result.ResultQuality!.Citations;
        Assert.Equal("available", q.Availability);
        Assert.Equal("partial", q.Completeness);
        Assert.Equal((101, 100, 0, 0, 1), (q.ReceivedCount, q.RetainedCount, q.FilteredCount, q.InvalidCount, q.TruncatedCount));
        Assert.Equal(new[] { "SOURCE_LIMIT" }, q.Reasons);
        Assert.Equal(q.RetainedCount, result.Citations.Count);
    }

    [Theory]
    [InlineData("{}", "copilot", false, "not_exposed", 0)]
    [InlineData("{\"model\":\"copilot\",\"searchQueries\":[]}", "copilot", false, "empty", 0)]
    [InlineData("{\"modelVersion\":\"provider-version\",\"searchQueries\":[\"真实查询\"]}", "provider-version", true, "available", 1)]
    [InlineData("{\"searchQueries\":false}", "copilot", false, "unknown", 0)]
    public async Task ModelAndQueriesAreTakenOnlyFromExposedFields(string response, string model,
        bool versionProvided, string availability, int count)
    {
        var result = await NormalizeAsync(response, "copilot");
        Assert.Equal(model, result.ModelVersion);
        Assert.Equal(versionProvided, result.ResultQuality!.ModelVersionProvided);
        Assert.Equal(availability, result.QueryAvailability);
        Assert.Equal(count, result.WebQueries.Count);
        if (count > 0) Assert.Equal("真实查询", result.WebQueries[0]);
    }

    [Fact]
    public async Task SubmissionUsesCopilotOriginalPromptCountryAndMarkdown()
    {
        var handler = new FakeHandler(_ => Json("{\"task\":{\"id\":\"copilot-task\"}}"));
        var result = await Create(handler).SubmitAsync(new("copilot", "  原问题 %_ 保留原文  ", "zh-CN", "us"));
        Assert.Equal("copilot-task", result.ExternalTaskId);
        using var json = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        Assert.Equal("COPILOT", json.RootElement.GetProperty("taskType").GetString());
        var payload = json.RootElement.GetProperty("payload");
        Assert.Equal("  原问题 %_ 保留原文  ", payload.GetProperty("prompt").GetString());
        Assert.Equal("US", payload.GetProperty("country").GetString());
        Assert.True(payload.GetProperty("include").GetProperty("markdown").GetBoolean());
        Assert.Equal(new[] { "country", "include", "prompt" }, payload.EnumerateObject().Select(p => p.Name).Order());
    }

    [Theory]
    // 429 is a definite per-account rejection (no task created): the only key cools down, so the pool reports busy.
    [InlineData(HttpStatusCode.TooManyRequests, "CLORO_KEY_POOL_BUSY", true, false)]
    [InlineData(HttpStatusCode.RequestTimeout, "CLORO_SUBMISSION_UNKNOWN", false, true)]
    [InlineData(HttpStatusCode.InternalServerError, "CLORO_SUBMISSION_UNKNOWN", false, true)]
    public async Task SubmissionErrorsPreserveUnknownBoundaryWithoutProviderBody(HttpStatusCode status,
        string code, bool retryable, bool unknown)
    {
        var adapter = Create(new FakeHandler(_ => new(status) { Content = new StringContent("private-provider-body") }));
        var error = await Assert.ThrowsAsync<CloroProviderException>(() => adapter.SubmitAsync(new("copilot", "问题", "zh-CN", "US")));
        Assert.Equal(code, error.Code);
        Assert.Equal(retryable, error.Retryable);
        Assert.Equal(unknown, error.SubmissionUnknown);
        Assert.DoesNotContain("private-provider-body", error.Message);
    }

    [Fact]
    public async Task FailedCaptureAndMalformedOrSynchronousEnvelopeDoNotBecomeEmptySuccess()
    {
        var adapter = Create(new FakeHandler(_ => Json("{\"task\":{\"status\":\"FAILED\"},\"response\":{\"text\":\"private-provider-body\"}}")));
        var failed = await adapter.GetAsync("copilot-task");
        Assert.Equal("FAILED", failed.Status);
        Assert.Equal("CLORO_TASK_FAILED", failed.ErrorCode);
        Assert.DoesNotContain("private-provider-body", failed.Payload.GetRawText());
        Assert.Throws<CloroProviderException>(() => adapter.Normalize(failed, "copilot"));
        foreach (var envelope in new[] { "{\"task\":{\"status\":\"COMPLETED\"},\"result\":{\"text\":\"answer\"}}", "{\"task\":{\"status\":\"UNRECOGNIZED\"}}" })
        {
            var invalid = Create(new FakeHandler(_ => Json(envelope)));
            var error = await Assert.ThrowsAsync<CloroProviderException>(async () => invalid.Normalize(await invalid.GetAsync("copilot-task"), "copilot"));
            Assert.Equal("INVALID_PROVIDER_RESPONSE", error.Code);
        }
    }
}
