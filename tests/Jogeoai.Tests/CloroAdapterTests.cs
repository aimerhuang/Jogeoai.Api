using System.Net;
using System.Text.Json;
using Jogeoai.Providers.Abstractions;
using Jogeoai.Providers.Cloro;

using static Jogeoai.Tests.CloroAdapterTestFixture;

namespace Jogeoai.Tests;

public sealed class CloroAdapterTests
{
    // Synthetic fixtures built from the public Cloro Gemini schema (2026-10-08), not live captures.
    [Fact]
    public void GeminiSchemaFixturePreservesFlattenedPositionsAndUnknownVersion()
    {
        var result = NormalizeGemini("""
            {"markdown":"回答 [1]","text":"回答","sources":[
                {"position":1,"url":"https://example.com/a","label":"第一条"},
                {"position":2,"url":"https://example.org/b","label":"第二条"}],
             "citationPills":[{"position":1,"url":"https://example.com/a","label":"第一条","citationPillId":1}]}
            """);
        Assert.Equal("回答 [1]", result.AnswerText);
        Assert.Equal(new[] { 1, 2, 3 }, result.Citations.Select(c => c.CitationIndex));
        Assert.Equal(result.Citations[0].Url, result.Citations[2].Url);
        Assert.Equal("gemini", result.ModelVersion);
        Assert.False(result.ResultQuality!.ModelVersionProvided);
        Assert.Equal("available", result.ResultQuality.Citations.Availability);
        Assert.Equal("complete", result.ResultQuality.Citations.Completeness);
        Assert.Equal(3, result.ResultQuality.Citations.ReceivedCount);
        Assert.Equal(3, result.ResultQuality.Citations.RetainedCount);
        Assert.Empty(result.ResultQuality.Citations.Reasons);
        Assert.Equal("cloro-v2", result.ParserVersion);
    }

    [Theory]
    [InlineData("{}", "not_exposed", "unknown", null, 0, 0, 0)]
    [InlineData("{\"sources\":[]}", "empty", "complete", 0, 0, 0, 0)]
    [InlineData("{\"sources\":null}", "unknown", "unknown", null, 0, 0, 0)]
    [InlineData("{\"sources\":[{}]}", "unknown", "partial", 1, 0, 0, 1)]
    [InlineData("{\"sources\":[{\"url\":\"http://localhost/a\"}]}", "available", "partial", 1, 0, 1, 0)]
    [InlineData("{\"sources\":[{\"url\":\"https://example.org/a\"},null],\"citationPills\":false}", "unknown", "partial", null, 1, 0, 1)]
    public void GeminiSourceAvailabilityIsNotInferredFromRetainedCount(string response, string availability,
        string completeness, int? received, int retained, int filtered, int invalid)
    {
        var result = NormalizeGemini(response);
        var quality = result.ResultQuality!.Citations;
        Assert.Equal(availability, quality.Availability);
        Assert.Equal(completeness, quality.Completeness);
        Assert.Equal(received, quality.ReceivedCount);
        Assert.Equal(retained, quality.RetainedCount);
        Assert.Equal(filtered, quality.FilteredCount);
        Assert.Equal(invalid, quality.InvalidCount);
        Assert.Equal(retained, result.Citations.Count);
        Assert.False(result.AnswerPresent);
    }

    [Fact]
    public void GeminiCountsAllCandidatesAfterRetentionLimitWithoutInventingCitations()
    {
        var entries = Enumerable.Range(0, 102).Select(i => new { url = $"https://example.org/{i}" }).Cast<object>()
            .Concat([new { url = "https://example.org/?token=private" }, new { broken = true }]).ToArray();
        var result = NormalizeGemini(JsonSerializer.Serialize(new { text = "https://not-a-source.example/", sources = entries }));
        var q = result.ResultQuality!.Citations;
        Assert.Equal(104, q.ReceivedCount);
        Assert.Equal(100, q.RetainedCount);
        Assert.Equal(1, q.FilteredCount);
        Assert.Equal(1, q.InvalidCount);
        Assert.Equal(2, q.TruncatedCount);
        Assert.Equal("partial", q.Completeness);
        Assert.Contains("SOURCE_LIMIT", q.Reasons);
        Assert.Equal(100, result.Citations.Count);
    }

    [Fact]
    public void BoundedSourceTitleIsStillRetainedButCannotClaimCompleteRetention()
    {
        var result = NormalizeGemini(JsonSerializer.Serialize(new { sources = new[] { new { url = "https://example.org/a", label = new string('中', 513) } } }));
        Assert.Equal(512, result.Citations[0].Title!.Length);
        Assert.Equal(1, result.ResultQuality!.Citations.RetainedCount);
        Assert.Equal(0, result.ResultQuality.Citations.TruncatedCount);
        Assert.Equal("partial", result.ResultQuality.Citations.Completeness);
        Assert.Contains("SOURCE_FIELD_LIMIT", result.ResultQuality.Citations.Reasons);
    }

    [Fact]
    public async Task GeminiRedactionEvidenceSurvivesPollBoundary()
    {
        var adapter = Create(new FakeHandler(_ => Json("""
            {"task":{"status":"COMPLETED"},"response":{"markdown":"Bearer private-value",
            "sources":[{"url":"https://example.org/a","label":"Bearer private-value"}]}}
            """)));
        var result = adapter.Normalize(await adapter.GetAsync("task-1"), "gemini");
        Assert.True(result.ResultQuality!.AnswerRedacted);
        Assert.Contains("SOURCE_REDACTED", result.ResultQuality.Citations.Reasons);
        Assert.Equal("partial", result.ResultQuality.Citations.Completeness);
        Assert.DoesNotContain("private-value", result.RawOutput.GetRawText());
        Assert.DoesNotContain("private-value", result.AnswerText);
    }

    private static CloroAnswerSnapshot NormalizeGemini(string response)
        => Create(new FakeHandler(_ => Json("{}"))).Normalize(new("gemini-fixture", "COMPLETED",
            JsonSerializer.SerializeToElement(new { task = new { status = "COMPLETED" }, response = JsonSerializer.Deserialize<JsonElement>(response) })), "gemini");

    [Theory]
    [InlineData("chatgpt", "CHATGPT", "prompt")]
    [InlineData("perplexity", "PERPLEXITY", "prompt")]
    [InlineData("copilot", "COPILOT", "prompt")]
    [InlineData("gemini", "GEMINI", "prompt")]
    [InlineData("google-ai-mode", "AIMODE", "prompt")]
    [InlineData("google-ai-overview", "GOOGLE", "query")]
    public async Task Submit_UsesRegisteredCloroRoute(string engine, string taskType, string field)
    {
        var handler = new FakeHandler(_ => Json("{\"task\":{\"id\":\"task-1\"}}"));
        var adapter = Create(handler);
        var submitted = await adapter.SubmitAsync(new CloroTaskRequest(engine, "What should I buy?", "en-US", "us"));
        Assert.Equal("task-1", submitted.ExternalTaskId);
        using var requestJson = JsonDocument.Parse(handler.RequestBodies.Single());
        Assert.Equal(taskType, requestJson.RootElement.GetProperty("taskType").GetString());
        Assert.True(requestJson.RootElement.GetProperty("payload").TryGetProperty(field, out _));
        Assert.Equal("US", requestJson.RootElement.GetProperty("payload").GetProperty("country").GetString());
    }

    [Fact]
    public async Task PollAndNormalize_PreservesSafeCitationPositionsAndQueries()
    {
        var handler = new FakeHandler(_ => Json("""{"status":"COMPLETED","response":{"markdown":"Answer","model":"gpt-5","searchQueries":["one","two"],"sources":[{"url":"https://www.Example.com/a#frag","title":"A"},{"url":"https://example.com/a#other","title":"B"},{"url":"http://localhost/no"},{"url":"https://user:pass@example.com/private"},{"url":"https://example.com/a?token=secret"}]}}"""));
        var adapter = Create(handler);
        var snapshot = await adapter.GetAsync("task-1");
        var result = adapter.Normalize(snapshot, "chatgpt");
        Assert.Equal("Answer", result.AnswerText);
        Assert.Equal("gpt-5", result.ModelVersion);
        Assert.Equal(2, result.Citations.Count);
        Assert.Equal("example.com", result.Citations[0].Domain);
        Assert.Equal([1, 2], result.Citations.Select(c => c.CitationIndex));
        Assert.Equal(["one", "two"], result.WebQueries);
        Assert.Equal("available", result.QueryAvailability);
    }

    [Theory]
    [InlineData("{}", "not_exposed")]
    [InlineData("{\"searchQueries\":[]}", "empty")]
    [InlineData("{\"searchQueries\":[\"  \"]}", "empty")]
    [InlineData("{\"searchQueries\":null}", "unknown")]
    public void Normalize_RecordsSearchFieldAvailability(string answer, string expected)
    {
        var payload = JsonSerializer.SerializeToElement(new { status = "COMPLETED",
            response = JsonDocument.Parse(answer).RootElement });
        var snapshot = new CloroTaskStatusSnapshot("task-1", "COMPLETED", payload);
        var result = Create(new FakeHandler(_ => Json("{}"))).Normalize(snapshot, "chatgpt");
        Assert.Equal(expected, result.QueryAvailability);
        if (answer.Contains("  ", StringComparison.Ordinal)) Assert.Equal(["  "], result.WebQueries);
    }

    [Fact]
    public async Task OverviewWithoutAioverview_IsSuccessfulWithoutAnswer()
    {
        var handler = new FakeHandler(_ => Json("""{"status":"COMPLETED","response":{"aioverview":null,"model":"overview-v1"}}"""));
        var result = Create(handler).Normalize(await Create(handler).GetAsync("task-1"), "google-ai-overview");
        Assert.False(result.AnswerPresent);
        Assert.Equal("overview-v1", result.ModelVersion);
        Assert.Equal("ai_overview_not_shown", result.ResultQuality!.AnswerAbsence);
        Assert.Empty(result.AnswerText);
    }

    [Fact]
    public async Task Get_RedactsCredentialsAndHtmlFromPayload()
    {
        var handler = new FakeHandler(_ => Json("""{"status":"RUNNING","response":{"markdown":"safe","apiKey":"secret-key","headers":{"Authorization":"Bearer abc"},"html":"<html>private</html>","profilePath":"/tmp/profile"}}"""));
        var result = await Create(handler).GetAsync("task-1");
        var raw = result.Payload.GetRawText();
        Assert.DoesNotContain("secret-key", raw);
        Assert.DoesNotContain("Authorization", raw);
        Assert.DoesNotContain("<html", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("profilePath", raw, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("RUNNING", result.Status);
    }

    [Theory]
    // A single-key pool: the 429 only cools that account, so the pool is busy rather than out of credits.
    [InlineData(HttpStatusCode.TooManyRequests, "CLORO_KEY_POOL_BUSY", true, false)]
    [InlineData(HttpStatusCode.InternalServerError, "CLORO_SUBMISSION_UNKNOWN", false, true)]
    [InlineData(HttpStatusCode.RequestTimeout, "CLORO_SUBMISSION_UNKNOWN", false, true)]
    public async Task Submit_MapsProviderErrorsWithoutLeakingBody(HttpStatusCode status, string code, bool retryable, bool unknown)
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(status) { Content = new StringContent("apiKey=secret and provider body") });
        var exception = await Assert.ThrowsAsync<CloroProviderException>(() => Create(handler).SubmitAsync(new CloroTaskRequest("chatgpt", "p", "en-US", "US")));
        Assert.Equal(code, exception.Code);
        Assert.Equal(retryable, exception.Retryable);
        Assert.Equal(unknown, exception.SubmissionUnknown);
        Assert.DoesNotContain("secret", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SubmitWithoutTaskId_IsSubmissionUnknown()
    {
        var handler = new FakeHandler(_ => Json("{\"status\":\"QUEUED\"}"));
        var exception = await Assert.ThrowsAsync<CloroProviderException>(() => Create(handler).SubmitAsync(new CloroTaskRequest("chatgpt", "p", "en-US", "US")));
        Assert.Equal("CLORO_SUBMISSION_UNKNOWN", exception.Code);
        Assert.True(exception.SubmissionUnknown);
    }

    [Fact]
    public async Task NormalizeRejectsUnknownOrFailedStatus()
    {
        var handler = new FakeHandler(_ => Json("{\"status\":\"FAILED\",\"response\":{\"markdown\":\"do not read\"}}"));
        var adapter = Create(handler);
        var snapshot = await adapter.GetAsync("task-1");
        Assert.Equal("FAILED", snapshot.Status);
        var exception = Assert.Throws<CloroProviderException>(() => adapter.Normalize(snapshot, "chatgpt"));
        Assert.Equal("INVALID_PROVIDER_RESPONSE", exception.Code);
    }

}
