using System.Net;
using System.Text;
using System.Text.Json;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Jogeoai.Providers.Abstractions;
using Jogeoai.Providers.Domestic;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jogeoai.Tests;

public sealed class DoubaoArkAdapterTests
{
    private const string Credential = "ark-secret-key-7788";
    private const string ApiUrl = "https://ark.test/api/v3/responses";
    private const string Model = "doubao-seed-2-1-pro-260915";

    // Shape taken from the official Responses API + web_search documentation (research/ark-api-notes.md §2).
    private const string OfficialSuccess = """
        {
          "id": "resp_123", "object": "response", "model": "doubao-seed-2-1-pro-260915", "status": "completed",
          "output": [
            {"type": "reasoning", "id": "rs_1", "summary": [{"type": "summary_text", "text": "thinking"}], "encrypted_content": "ENC-SECRET-1"},
            {"type": "web_search_call", "id": "ws_1", "status": "completed",
             "action": {"type": "search", "query": "杭州 咖啡 推荐", "queries": ["杭州 咖啡 推荐", "杭州 精品咖啡"]}},
            {"type": "web_search_call", "id": "ws_2", "status": "completed",
             "action": {"type": "search", "queries": ["杭州 精品咖啡", "杭州 咖啡馆 排行"]}},
            {"type": "message", "id": "msg_1", "role": "assistant", "status": "completed", "content": [
              {"type": "output_text", "text": "推荐以下几家咖啡馆。", "annotations": [
                {"type": "url_citation", "title": "杭州十大咖啡馆", "url": "https://www.example.com/hz-coffee#top", "site_name": "示例", "publish_time": "2026-09-04T10:24:00+08:00", "summary": "摘要"},
                {"type": "url_citation", "title": "精品咖啡榜", "url": "https://news.example.org/list?id=2", "site_name": "示例新闻"},
                {"type": "url_citation", "title": "重复来源", "url": "https://example.com/hz-coffee"},
                {"type": "url_citation", "title": "第三篇", "url": "https://blog.example.net/post/3"}
              ]}
            ], "encrypted_content": "ENC-SECRET-2"}
          ],
          "usage": {"input_tokens": 120, "output_tokens": 480, "total_tokens": 600,
                    "output_tokens_details": {"reasoning_tokens": 200},
                    "tool_usage": {"web_search": 2}, "tool_usage_details": {"web_search": {"doubao": 2}}}
        }
        """;

    [Fact]
    public async Task OfficialShapeIsNormalizedIntoTheDoubaoSnapshot()
    {
        var handler = new FakeHandler(_ => Json(OfficialSuccess));
        var result = await Create(handler).AskAsync(new("杭州有哪些值得去的咖啡馆？"));

        Assert.Equal(("doubao_api", "doubao", "api", "api_response", "doubao-api-v1"),
            (result.Provider, result.Engine, result.CaptureMode, result.EvidenceClass, result.ParserVersion));
        Assert.Equal("推荐以下几家咖啡馆。", result.AnswerText);
        Assert.True(result.AnswerPresent);
        Assert.Equal(Model, result.ModelVersion);
        Assert.Equal(["杭州 咖啡 推荐", "杭州 精品咖啡", "杭州 咖啡馆 排行"], result.WebQueries);
        Assert.Equal("available", result.QueryAvailability);
        Assert.Collection(result.Citations,
            c => Assert.Equal(("https://example.com/hz-coffee", "example.com", "杭州十大咖啡馆", 1), (c.Url, c.Domain, c.Title, c.CitationIndex)),
            c => Assert.Equal(("https://news.example.org/list?id=2", "news.example.org", "精品咖啡榜", 2), (c.Url, c.Domain, c.Title, c.CitationIndex)),
            c => Assert.Equal(("https://blog.example.net/post/3", "blog.example.net", "第三篇", 3), (c.Url, c.Domain, c.Title, c.CitationIndex)));
        var quality = result.ResultQuality!;
        Assert.True(quality.ModelVersionProvided);
        Assert.False(quality.AnswerRedacted);
        Assert.Null(quality.AnswerAbsence);
        Assert.Equal(("available", "complete", 3, 3, 0, 0, 0), (quality.Citations.Availability, quality.Citations.Completeness,
            quality.Citations.ReceivedCount, quality.Citations.RetainedCount, quality.Citations.FilteredCount, quality.Citations.InvalidCount, quality.Citations.TruncatedCount));
        Assert.Empty(quality.Citations.Reasons);
        Assert.NotNull(ResultQualityCodec.Encode(result, result.AnswerText));

        var raw = result.RawOutput.GetRawText();
        Assert.DoesNotContain("encrypted_content", raw);
        Assert.DoesNotContain("ENC-SECRET", raw);
        Assert.Contains("\"usage\"", raw);
        var usage = DoubaoUsageSummary.FromRawOutput(result.RawOutput)!;
        Assert.Equal((120L, 480L, 200L, 600L, 2L, 2L), (usage.InputTokens, usage.OutputTokens, usage.ReasoningTokens, usage.TotalTokens, usage.WebSearchCalls, usage.DoubaoSearchCalls));

        Assert.Equal(Credential, Assert.Single(handler.Authorizations));
        Assert.Equal(ApiUrl, Assert.Single(handler.Uris)!.ToString());
        using var body = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        var root = body.RootElement;
        Assert.Equal(Model, root.GetProperty("model").GetString());
        Assert.Equal(3, root.GetProperty("max_tool_calls").GetInt32());
        var tool = Assert.Single(root.GetProperty("tools").EnumerateArray());
        Assert.Equal("web_search", tool.GetProperty("type").GetString());
        Assert.Equal(["doubao"], tool.GetProperty("sources").EnumerateArray().Select(s => s.GetString()));
        var input = Assert.Single(root.GetProperty("input").EnumerateArray());
        Assert.Equal("user", input.GetProperty("role").GetString());
        var part = Assert.Single(input.GetProperty("content").EnumerateArray());
        Assert.Equal(("input_text", "杭州有哪些值得去的咖啡馆？"), (part.GetProperty("type").GetString(), part.GetProperty("text").GetString()));
        Assert.False(root.TryGetProperty("reasoning", out _));
        Assert.False(root.TryGetProperty("stream", out _));
        Assert.DoesNotContain(Credential, handler.RequestBodies[0]);
    }

    [Fact]
    public async Task ReasoningEffortAndCustomToolCallsAreForwardedWhenConfigured()
    {
        var handler = new FakeHandler(_ => Json(OfficialSuccess));
        var options = new AnswerProviderOptions { ReasoningEffort = "minimal", MaxToolCalls = 5 };
        await Create(handler, Configuration(options: options)).AskAsync(new("问题"));
        using var body = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        Assert.Equal("minimal", body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal(5, body.RootElement.GetProperty("max_tool_calls").GetInt32());
    }

    [Fact]
    public async Task NoWebSearchCallMeansEmptyQueriesAndEmptyCitations()
    {
        const string response = """
            {"id":"resp_2","model":"doubao-seed-2-1-pro-260915","status":"completed","output":[
              {"type":"message","role":"assistant","content":[{"type":"output_text","text":"直接回答。","annotations":[]}]}]}
            """;
        var result = await Create(new FakeHandler(_ => Json(response))).AskAsync(new("问题"));
        Assert.Empty(result.WebQueries);
        Assert.Equal("empty", result.QueryAvailability);
        Assert.Empty(result.Citations);
        Assert.Equal(("empty", "complete", 0), (result.ResultQuality!.Citations.Availability, result.ResultQuality.Citations.Completeness, result.ResultQuality.Citations.ReceivedCount));
        Assert.Null(result.ResultQuality.AnswerAbsence);
        Assert.NotNull(ResultQualityCodec.Encode(result, result.AnswerText));
    }

    [Fact]
    public async Task EmptyAnswerIsAKnownAbsence()
    {
        const string response = """
            {"id":"resp_3","status":"completed","output":[
              {"type":"web_search_call","status":"completed","action":{"type":"search","queries":["查询"]}},
              {"type":"message","role":"assistant","content":[{"type":"output_text","text":"   ","annotations":[]}]}]}
            """;
        var result = await Create(new FakeHandler(_ => Json(response))).AskAsync(new("问题"));
        Assert.False(result.AnswerPresent);
        Assert.Equal("empty_answer", result.ResultQuality!.AnswerAbsence);
        // No model in the response: fall back to the configured model and say so.
        Assert.Equal(Model, result.ModelVersion);
        Assert.False(result.ResultQuality.ModelVersionProvided);
        Assert.Equal(["查询"], result.WebQueries);
        var json = ResultQualityCodec.Encode(result, result.AnswerText)!;
        Assert.Equal("empty_answer", ResultQualityCodec.Decode(json)!.AnswerAbsence);
    }

    [Fact]
    public async Task UnsafeInvalidAndOversizedCitationsAreCountedNotStored()
    {
        var longTitle = new string('标', 600);
        var response = $$"""
            {"id":"resp_4","model":"m","status":"completed","output":[
              {"type":"message","role":"assistant","content":[{"type":"output_text","text":"回答","annotations":[
                {"type":"url_citation","title":"内网","url":"http://localhost/admin"},
                {"type":"url_citation","title":"坏链接","url":"not a url"},
                {"type":"url_citation","title":"{{longTitle}}","url":"https://example.com/a?token=abc"},
                {"type":"url_citation","title":"{{longTitle}}","url":"https://example.com/ok"},
                {"type":"file_citation","file_id":"f1"}
              ]}]}]}
            """;
        var result = await Create(new FakeHandler(_ => Json(response))).AskAsync(new("问题"));
        var citation = Assert.Single(result.Citations);
        Assert.Equal("https://example.com/ok", citation.Url);
        Assert.Equal(512, citation.Title!.EnumerateRunes().Count());
        var c = result.ResultQuality!.Citations;
        Assert.Equal(("unknown", "partial", 4, 1, 2, 1, 0), (c.Availability, c.Completeness, c.ReceivedCount, c.RetainedCount, c.FilteredCount, c.InvalidCount, c.TruncatedCount));
        Assert.Equal(["INVALID_SOURCE_SHAPE", "SOURCE_FIELD_LIMIT", "UNSAFE_SOURCE"], c.Reasons);
        Assert.NotNull(ResultQualityCodec.Encode(result, result.AnswerText));
    }

    [Theory]
    [InlineData(401, "AuthenticationError", DoubaoErrorCodes.AuthFailed, false, null)]
    [InlineData(401, "InvalidAccountStatus", DoubaoErrorCodes.AuthFailed, false, null)]
    [InlineData(401, null, DoubaoErrorCodes.AuthFailed, false, null)]
    [InlineData(403, "AccessDenied", DoubaoErrorCodes.ServiceNotOpen, false, null)]
    [InlineData(403, "OperationDenied.ServiceNotOpen", DoubaoErrorCodes.ServiceNotOpen, false, null)]
    [InlineData(403, "AccountOverdueError", DoubaoErrorCodes.QuotaExceeded, true, 10)]
    [InlineData(403, "OperationDenied.ServiceOverdue", DoubaoErrorCodes.QuotaExceeded, true, 10)]
    [InlineData(403, "SomethingElse", DoubaoErrorCodes.AuthFailed, false, null)]
    [InlineData(404, "ModelNotOpen", DoubaoErrorCodes.ServiceNotOpen, false, null)]
    [InlineData(404, "InvalidEndpointOrModel.NotFound", DoubaoErrorCodes.ServiceNotOpen, false, null)]
    [InlineData(429, "QuotaExceeded", DoubaoErrorCodes.QuotaExceeded, true, 10)]
    [InlineData(429, "QuotaExceeded.DoubaoSearchFreeQuotaExceeded", DoubaoErrorCodes.QuotaExceeded, true, 10)]
    [InlineData(429, "ModelAccountTpdRateLimitExceeded", DoubaoErrorCodes.QuotaExceeded, true, 10)]
    [InlineData(429, "SetLimitExceeded", DoubaoErrorCodes.QuotaExceeded, true, 10)]
    [InlineData(429, "RateLimitExceeded.FoundationModel", DoubaoErrorCodes.RateLimited, true, null)]
    [InlineData(429, "APIAccountRpmRateLimitExceeded", DoubaoErrorCodes.RateLimited, true, null)]
    [InlineData(429, "RequestBurstTooFast", DoubaoErrorCodes.RateLimited, true, null)]
    [InlineData(429, null, DoubaoErrorCodes.RateLimited, true, null)]
    [InlineData(503, "ServerOverloaded", DoubaoErrorCodes.RateLimited, true, null)]
    [InlineData(400, "InputTextSensitiveContentDetected", DoubaoErrorCodes.InputFiltered, false, null)]
    [InlineData(400, "InputImageRiskDetection", DoubaoErrorCodes.InputFiltered, false, null)]
    [InlineData(400, "ContentSecurityDetectionError", DoubaoErrorCodes.InputFiltered, false, null)]
    [InlineData(400, "InvalidEndpoint.ClosedEndpoint", DoubaoErrorCodes.ServiceNotOpen, false, null)]
    [InlineData(400, "InvalidParameter", DoubaoErrorCodes.InvalidRequest, false, null)]
    [InlineData(400, null, DoubaoErrorCodes.InvalidRequest, false, null)]
    [InlineData(408, "RequestTimeout", DoubaoErrorCodes.RequestFailed, true, null)]
    [InlineData(499, null, DoubaoErrorCodes.RequestFailed, true, null)]
    [InlineData(500, "InternalServiceError", DoubaoErrorCodes.RequestFailed, true, null)]
    [InlineData(500, "ModelLoading", DoubaoErrorCodes.RequestFailed, true, null)]
    [InlineData(503, "NoAvailableService", DoubaoErrorCodes.RequestFailed, true, null)]
    public async Task HttpFailuresMapToStableCodesWithoutLeakingTheBody(int status, string? arkCode, string code, bool retryable, int? deferMinutes)
    {
        var body = arkCode is null ? "<html>gateway</html>"
            : $$$"""{"error":{"type":"x","code":"{{{arkCode}}}","message":"SECRET-BODY-TEXT {{{Credential}}}","param":null}}""";
        var handler = new FakeHandler(_ => Status((HttpStatusCode)status, body));
        var exception = await Assert.ThrowsAsync<DoubaoProviderException>(() => Create(handler).AskAsync(new("问题")));
        Assert.Equal(code, exception.Code);
        Assert.Equal(retryable, exception.Retryable);
        Assert.Equal(deferMinutes, exception.DeferMinutes);
        Assert.DoesNotContain("SECRET-BODY-TEXT", exception.Message);
        Assert.DoesNotContain(Credential, exception.Message);
        Assert.DoesNotContain("<html>", exception.Message);
        if (arkCode is not null) Assert.Contains(arkCode, exception.Message);
    }

    [Theory]
    [InlineData("OutputTextSensitiveContentDetected")]
    [InlineData("OutputImageRiskDetection")]
    public async Task OutputModerationIsASuccessfulSafetyFilteredCheck(string arkCode)
    {
        var body = $$$"""{"error":{"code":"{{{arkCode}}}","message":"MODERATION-BODY-TEXT"}}""";
        var result = await Create(new FakeHandler(_ => Status(HttpStatusCode.BadRequest, body))).AskAsync(new("问题"));
        Assert.Equal(("doubao_api", "doubao", "api", "api_response"), (result.Provider, result.Engine, result.CaptureMode, result.EvidenceClass));
        Assert.False(result.AnswerPresent);
        Assert.Equal(string.Empty, result.AnswerText);
        Assert.Empty(result.Citations);
        Assert.Empty(result.WebQueries);
        Assert.Equal("safety_filtered", result.ResultQuality!.AnswerAbsence);
        Assert.Equal(Model, result.ModelVersion);
        Assert.False(result.ResultQuality.ModelVersionProvided);
        Assert.DoesNotContain("MODERATION-BODY-TEXT", result.RawOutput.GetRawText());
        Assert.Equal(arkCode, result.RawOutput.GetProperty("code").GetString());
        var json = ResultQualityCodec.Encode(result, result.AnswerText)!;
        Assert.Equal("safety_filtered", ResultQualityCodec.Decode(json)!.AnswerAbsence);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"id\":\"resp\",\"status\":\"completed\"}")]
    [InlineData("{\"output\":{\"type\":\"message\"}}")]
    [InlineData("[]")]
    public async Task MalformedOrShapelessResponsesAreTerminal(string body)
    {
        var exception = await Assert.ThrowsAsync<DoubaoProviderException>(() => Create(new FakeHandler(_ => Json(body))).AskAsync(new("问题")));
        Assert.Equal(DoubaoErrorCodes.InvalidResponse, exception.Code);
        Assert.False(exception.Retryable);
    }

    [Fact]
    public async Task FailedStatusInsideASuccessBodyIsRetryable()
    {
        var exception = await Assert.ThrowsAsync<DoubaoProviderException>(() => Create(new FakeHandler(_ =>
            Json("""{"id":"r","status":"failed","output":[],"error":{"code":"InternalServiceError"}}"""))).AskAsync(new("问题")));
        Assert.Equal(DoubaoErrorCodes.RequestFailed, exception.Code);
        Assert.True(exception.Retryable);
    }

    [Fact]
    public async Task ConfiguredTimeoutCancelsTheCallAsRetryableTimeout()
    {
        var handler = new FakeHandler(async (_, ct) => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return Json("{}"); });
        var exception = await Assert.ThrowsAsync<DoubaoProviderException>(() =>
            Create(handler, Configuration(timeoutSeconds: 1)).AskAsync(new("问题")));
        Assert.Equal(DoubaoErrorCodes.Timeout, exception.Code);
        Assert.True(exception.Retryable);
    }

    [Fact]
    public async Task CallerCancellationIsNotReportedAsATimeout()
    {
        using var cts = new CancellationTokenSource();
        var handler = new FakeHandler(async (_, ct) => { cts.Cancel(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return Json("{}"); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(handler).AskAsync(new("问题"), cts.Token));
    }

    [Fact]
    public async Task NetworkFailuresAreRetryableRequestFailures()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("connection refused"));
        var exception = await Assert.ThrowsAsync<DoubaoProviderException>(() => Create(handler).AskAsync(new("问题")));
        Assert.Equal(DoubaoErrorCodes.RequestFailed, exception.Code);
        Assert.True(exception.Retryable);
        Assert.DoesNotContain("connection refused", exception.Message);
    }

    [Fact]
    public async Task DoubaoAppModeIsAnExplicitPlaceholderError()
    {
        var handler = new FakeHandler(_ => Json(OfficialSuccess));
        var options = new AnswerProviderOptions { RequestMode = AnswerProviderOptions.DoubaoAppMode };
        var exception = await Assert.ThrowsAsync<DoubaoProviderException>(() => Create(handler, Configuration(options: options)).AskAsync(new("问题")));
        Assert.Equal(DoubaoErrorCodes.InvalidRequest, exception.Code);
        Assert.Equal("豆包助手 API 尚未开通。", exception.Message);
        Assert.False(exception.Retryable);
        Assert.Empty(handler.Uris);
    }

    [Fact]
    public async Task MissingConfigurationNeverCallsArk()
    {
        var handler = new FakeHandler(_ => Json(OfficialSuccess));
        var exception = await Assert.ThrowsAsync<DoubaoProviderException>(() => Create(handler, configuration: null).AskAsync(new("问题")));
        Assert.Equal(DoubaoErrorCodes.NotConfigured, exception.Code);
        Assert.False(exception.Retryable);
        Assert.Empty(handler.Uris);
    }

    [Fact]
    public async Task BlankPromptIsRejectedBeforeAnyCall()
    {
        var handler = new FakeHandler(_ => Json(OfficialSuccess));
        var exception = await Assert.ThrowsAsync<DoubaoProviderException>(() => Create(handler).AskAsync(new("   ")));
        Assert.Equal(DoubaoErrorCodes.InvalidRequest, exception.Code);
        Assert.Empty(handler.Uris);
    }

    [Fact]
    public void CodecAcceptsOnlyTheKnownAbsenceReasons()
    {
        var answer = new CloroAnswerSnapshot("doubao_api", "doubao", "api", "api_response", Model, "", false, [], [],
            JsonSerializer.SerializeToElement(new { }), DateTimeOffset.UtcNow, "doubao-api-v1", "empty",
            new(false, true, new("not_exposed", "unknown", null, 0, 0, 0, 0, []), "empty_answer"));
        Assert.NotNull(ResultQualityCodec.Encode(answer, ""));
        Assert.NotNull(ResultQualityCodec.Encode(answer with { ResultQuality = answer.ResultQuality! with { AnswerAbsence = "safety_filtered" } }, ""));
        Assert.NotNull(ResultQualityCodec.Encode(answer with { ResultQuality = answer.ResultQuality! with { AnswerAbsence = "ai_overview_not_shown" } }, ""));
        Assert.Throws<CollectorStoreException>(() => ResultQualityCodec.Encode(answer with { ResultQuality = answer.ResultQuality! with { AnswerAbsence = "provider_said_so" } }, ""));
    }

    // ---- messages mode (Ark Anthropic-compatible Messages API, research/ark-messages-sources-test.md) ----

    private const string MessagesApiUrl = "https://ark.test/api/compatible/v1/messages";

    // Live shape: thinking → server_tool_use → web_search_tool_result (20 results per search) → … → text; text has no citations.
    private const string MessagesSuccess = """
        {
          "id": "msg_1", "type": "message", "role": "assistant", "model": "doubao-seed-2-0-lite-260428", "stop_reason": "end_turn",
          "content": [
            {"type": "thinking", "thinking": "THINKING-SECRET-TEXT", "signature": "SIG-SECRET-1"},
            {"type": "text", "text": "我来搜索一下相关信息。"},
            {"type": "server_tool_use", "id": "srvtoolu_1", "name": "web_search", "input": {"query": "管道摄像头 厂家"}},
            {"type": "web_search_tool_result", "tool_use_id": "srvtoolu_1", "content": [
              {"type": "web_search_result", "title": "管道检测十大品牌", "url": "https://www.example.com/pipe#top", "page_age": "2026-09-04T10:24:00+08:00", "encrypted_content": "ENC-SECRET-1"},
              {"type": "web_search_result", "title": "市政管网巡检", "url": "https://news.example.org/list?id=2", "encrypted_content": "ENC-SECRET-2"},
              {"type": "web_search_tool_result_error", "error_code": "unavailable"},
              "not-an-object"
            ]},
            {"type": "thinking", "thinking": "THINKING-SECRET-TEXT-2", "signature": "SIG-SECRET-2"},
            {"type": "server_tool_use", "id": "srvtoolu_2", "name": "web_search", "input": {"query": "排水管网 CCTV 检测机器人"}},
            {"type": "web_search_tool_result", "tool_use_id": "srvtoolu_2", "content": [
              {"type": "web_search_result", "title": "重复来源", "url": "https://example.com/pipe", "encrypted_content": "ENC-SECRET-3"},
              {"type": "web_search_result", "title": "第三篇", "url": "https://blog.example.net/post/3", "encrypted_content": "ENC-SECRET-4"}
            ]},
            {"type": "server_tool_use", "id": "srvtoolu_3", "name": "web_search", "input": {"query": "管道摄像头 厂家"}},
            {"type": "web_search_tool_result", "tool_use_id": "srvtoolu_3", "content": {"type": "web_search_tool_result_error", "error_code": "max_uses_exceeded"}},
            {"type": "redacted_thinking", "data": "REDACTED-SECRET"},
            {"type": "text", "text": "## 推荐厂家  \n\n\n\n1. 示例公司"},
            {"type": "text", "text": "以上供参考。"}
          ],
          "usage": {"input_tokens": 1200, "output_tokens": 800, "cache_creation_input_tokens": 0, "cache_read_input_tokens": 0,
                    "server_tool_use": {"web_search_requests": 2}, "iterations": [{"input_tokens": 600, "output_tokens": 400}]}
        }
        """;

    private static AnswerProviderConfiguration MessagesConfiguration(string? thinking = null, int maxToolCalls = 3, string apiUrl = MessagesApiUrl) =>
        new("doubao_api", apiUrl, Model, Credential, 30, new AnswerProviderOptions
        {
            RequestMode = AnswerProviderOptions.MessagesMode, MaxToolCalls = maxToolCalls, Thinking = thinking,
        });

    [Fact]
    public async Task MessagesShapeRecordsEveryRetrievedSource()
    {
        var handler = new FakeHandler(_ => Json(MessagesSuccess));
        var result = await Create(handler, MessagesConfiguration()).AskAsync(new("市政管道巡检有哪些靠谱的管道摄像头厂家？"));

        Assert.Equal(("doubao_api", "doubao", "api", "api_response", "doubao-api-messages-v1"),
            (result.Provider, result.Engine, result.CaptureMode, result.EvidenceClass, result.ParserVersion));
        // Only text after the last tool block is the answer; whitespace is normalized and the preamble is dropped.
        Assert.Equal("## 推荐厂家\n\n1. 示例公司\n\n以上供参考。", result.AnswerText);
        Assert.True(result.AnswerPresent);
        Assert.Equal("doubao-seed-2-0-lite-260428", result.ModelVersion);
        Assert.Equal(["管道摄像头 厂家", "排水管网 CCTV 检测机器人"], result.WebQueries);
        Assert.Equal("available", result.QueryAvailability);
        Assert.Collection(result.Citations,
            c => Assert.Equal(("https://example.com/pipe", "example.com", "管道检测十大品牌", 1), (c.Url, c.Domain, c.Title, c.CitationIndex)),
            c => Assert.Equal(("https://news.example.org/list?id=2", "news.example.org", "市政管网巡检", 2), (c.Url, c.Domain, c.Title, c.CitationIndex)),
            c => Assert.Equal(("https://blog.example.net/post/3", "blog.example.net", "第三篇", 3), (c.Url, c.Domain, c.Title, c.CitationIndex)));
        var quality = result.ResultQuality!;
        Assert.True(quality.ModelVersionProvided);
        Assert.Null(quality.AnswerAbsence);
        Assert.Equal(("available", "complete", 3, 3, 0, 0, 0), (quality.Citations.Availability, quality.Citations.Completeness,
            quality.Citations.ReceivedCount, quality.Citations.RetainedCount, quality.Citations.FilteredCount, quality.Citations.InvalidCount, quality.Citations.TruncatedCount));
        Assert.Empty(quality.Citations.Reasons);
        Assert.Equal(result.Citations.Count, quality.Citations.RetainedCount);
        Assert.NotNull(ResultQualityCodec.Encode(result, result.AnswerText));
    }

    [Fact]
    public async Task MessagesRawOutputDropsOpaqueFieldsAndKeepsUsage()
    {
        var result = await Create(new FakeHandler(_ => Json(MessagesSuccess)), MessagesConfiguration()).AskAsync(new("问题"));
        var raw = result.RawOutput.GetRawText();
        foreach (var secret in (string[])["encrypted_content", "ENC-SECRET", "signature", "SIG-SECRET", "THINKING-SECRET-TEXT", "REDACTED-SECRET"])
            Assert.DoesNotContain(secret, raw);
        Assert.Contains("\"usage\"", raw);
        Assert.Contains("\"page_age\"", raw);
        Assert.Equal("thinking", result.RawOutput.GetProperty("content")[0].GetProperty("type").GetString());

        var usage = DoubaoUsageSummary.FromRawOutput(result.RawOutput)!;
        Assert.Equal((1200L, 800L, (long?)null, 2000L, 2L, 2L),
            (usage.InputTokens, usage.OutputTokens, usage.ReasoningTokens, usage.TotalTokens, usage.WebSearchCalls, usage.DoubaoSearchCalls));
    }

    [Fact]
    public async Task MessagesRequestUsesAnthropicHeadersAndNoSources()
    {
        var handler = new FakeHandler(_ => Json(MessagesSuccess));
        await Create(handler, MessagesConfiguration()).AskAsync(new("杭州有哪些值得去的咖啡馆？"));

        Assert.Equal(MessagesApiUrl, Assert.Single(handler.Uris)!.ToString());
        Assert.Null(Assert.Single(handler.Authorizations));
        var headers = Assert.Single(handler.Headers);
        Assert.Equal(Credential, headers["x-api-key"]);
        Assert.Equal("2023-06-01", headers["anthropic-version"]);
        Assert.False(headers.ContainsKey("Authorization"));

        var text = Assert.Single(handler.RequestBodies);
        Assert.DoesNotContain(Credential, text);
        Assert.DoesNotContain("sources", text);
        using var body = JsonDocument.Parse(text);
        var root = body.RootElement;
        Assert.Equal(["model", "max_tokens", "messages", "tools"], root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(Model, root.GetProperty("model").GetString());
        Assert.Equal(16000, root.GetProperty("max_tokens").GetInt32());
        var message = Assert.Single(root.GetProperty("messages").EnumerateArray());
        Assert.Equal(("user", "杭州有哪些值得去的咖啡馆？"), (message.GetProperty("role").GetString(), message.GetProperty("content").GetString()));
        var tool = Assert.Single(root.GetProperty("tools").EnumerateArray());
        Assert.Equal(["type", "name", "max_uses"], tool.EnumerateObject().Select(p => p.Name));
        Assert.Equal(("web_search_20250305", "web_search", 3),
            (tool.GetProperty("type").GetString(), tool.GetProperty("name").GetString(), tool.GetProperty("max_uses").GetInt32()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("enabled")]
    [InlineData("disabled")]
    public async Task MessagesThinkingIsSentOnlyWhenConfigured(string? thinking)
    {
        var handler = new FakeHandler(_ => Json(MessagesSuccess));
        await Create(handler, MessagesConfiguration(thinking, maxToolCalls: 7)).AskAsync(new("问题"));
        using var body = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        var root = body.RootElement;
        Assert.Equal(7, Assert.Single(root.GetProperty("tools").EnumerateArray()).GetProperty("max_uses").GetInt32());
        if (thinking is null) Assert.False(root.TryGetProperty("thinking", out _));
        else Assert.Equal(thinking, root.GetProperty("thinking").GetProperty("type").GetString());
        Assert.False(root.TryGetProperty("reasoning", out _));
    }

    [Theory]
    [InlineData("max_tokens", DoubaoErrorCodes.InvalidResponse, false)]
    [InlineData("pause_turn", DoubaoErrorCodes.RequestFailed, true)]
    public async Task MessagesStopReasonsThatLoseTheAnswerFail(string stopReason, string code, bool retryable)
    {
        var response = MessagesSuccess.Replace("\"end_turn\"", $"\"{stopReason}\"", StringComparison.Ordinal);
        var exception = await Assert.ThrowsAsync<DoubaoProviderException>(() =>
            Create(new FakeHandler(_ => Json(response)), MessagesConfiguration()).AskAsync(new("问题")));
        Assert.Equal((code, retryable, (int?)null), (exception.Code, exception.Retryable, exception.DeferMinutes));
    }

    [Theory]
    [InlineData(401, "AuthenticationError", DoubaoErrorCodes.AuthFailed, false, null)]
    [InlineData(404, "InvalidEndpointOrModel.NotFound", DoubaoErrorCodes.ServiceNotOpen, false, null)]
    [InlineData(429, "QuotaExceeded.DoubaoSearchFreeQuotaExceeded", DoubaoErrorCodes.QuotaExceeded, true, 10)]
    [InlineData(429, "RateLimitExceeded.FoundationModel", DoubaoErrorCodes.RateLimited, true, null)]
    [InlineData(500, "InternalServiceError", DoubaoErrorCodes.RequestFailed, true, null)]
    public async Task MessagesFailuresUseTheArkErrorMapping(int status, string arkCode, string code, bool retryable, int? deferMinutes)
    {
        var body = $$$"""{"error":{"code":"{{{arkCode}}}","message":"SECRET-BODY-TEXT {{{Credential}}}","param":"","type":"Unauthorized"},"type":"error"}""";
        var exception = await Assert.ThrowsAsync<DoubaoProviderException>(() =>
            Create(new FakeHandler(_ => Status((HttpStatusCode)status, body)), MessagesConfiguration()).AskAsync(new("问题")));
        Assert.Equal((code, retryable, deferMinutes), (exception.Code, exception.Retryable, exception.DeferMinutes));
        Assert.Contains(arkCode, exception.Message);
        Assert.DoesNotContain("SECRET-BODY-TEXT", exception.Message);
        Assert.DoesNotContain(Credential, exception.Message);
    }

    [Fact]
    public async Task MessagesOutputModerationKeepsTheMessagesParserVersion()
    {
        var body = """{"error":{"code":"OutputTextSensitiveContentDetected","message":"MODERATION-BODY-TEXT"}}""";
        var result = await Create(new FakeHandler(_ => Status(HttpStatusCode.BadRequest, body)), MessagesConfiguration()).AskAsync(new("问题"));
        Assert.Equal(("safety_filtered", "doubao-api-messages-v1"), (result.ResultQuality!.AnswerAbsence, result.ParserVersion));
    }

    [Fact]
    public async Task MessagesSourcesAreCappedAtOneHundred()
    {
        var results = string.Join(",", Enumerable.Range(1, 105).Select(i =>
            $$"""{"type":"web_search_result","title":"来源{{i}}","url":"https://site{{i}}.example.com/page","encrypted_content":"E"}"""));
        var response = $$$"""
            {"model":"m","stop_reason":"end_turn","content":[
              {"type":"server_tool_use","id":"s1","name":"web_search","input":{"query":"查询"}},
              {"type":"web_search_tool_result","tool_use_id":"s1","content":[{{{results}}}]},
              {"type":"text","text":"回答"}]}
            """;
        var result = await Create(new FakeHandler(_ => Json(response)), MessagesConfiguration()).AskAsync(new("问题"));
        Assert.Equal(100, result.Citations.Count);
        Assert.Equal(("https://site100.example.com/page", 100), (result.Citations[^1].Url, result.Citations[^1].CitationIndex));
        var c = result.ResultQuality!.Citations;
        Assert.Equal(("available", "partial", 105, 100, 5), (c.Availability, c.Completeness, c.ReceivedCount, c.RetainedCount, c.TruncatedCount));
        Assert.Equal(["SOURCE_LIMIT"], c.Reasons);
        Assert.NotNull(ResultQualityCodec.Encode(result, result.AnswerText));
    }

    [Fact]
    public async Task MessagesWithoutSearchOrAnswerIsEmpty()
    {
        const string response = """{"stop_reason":"end_turn","content":[{"type":"thinking","thinking":"t","signature":"s"},{"type":"text","text":"  "}]}""";
        var result = await Create(new FakeHandler(_ => Json(response)), MessagesConfiguration()).AskAsync(new("问题"));
        Assert.False(result.AnswerPresent);
        Assert.Equal(("empty", "not_exposed", "empty_answer"), (result.QueryAvailability, result.ResultQuality!.Citations.Availability, result.ResultQuality.AnswerAbsence));
        Assert.Equal(Model, result.ModelVersion);
        Assert.False(result.ResultQuality.ModelVersionProvided);
        Assert.NotNull(ResultQualityCodec.Encode(result, result.AnswerText));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"id\":\"msg\",\"stop_reason\":\"end_turn\"}")]
    [InlineData("{\"output\":[]}")]
    [InlineData("[]")]
    public async Task MalformedMessagesResponsesAreTerminal(string body)
    {
        var exception = await Assert.ThrowsAsync<DoubaoProviderException>(() =>
            Create(new FakeHandler(_ => Json(body)), MessagesConfiguration()).AskAsync(new("问题")));
        Assert.Equal((DoubaoErrorCodes.InvalidResponse, false), (exception.Code, exception.Retryable));
    }

    [Theory]
    [InlineData("messages", ApiUrl)]
    [InlineData("web_search", MessagesApiUrl)]
    public async Task ModeAndEndpointMismatchIsRejectedBeforeAnyCall(string mode, string apiUrl)
    {
        var handler = new FakeHandler(_ => Json(MessagesSuccess));
        var configuration = new AnswerProviderConfiguration("doubao_api", apiUrl, Model, Credential, 30, new AnswerProviderOptions { RequestMode = mode });
        var exception = await Assert.ThrowsAsync<DoubaoProviderException>(() => Create(handler, configuration).AskAsync(new("问题")));
        Assert.Equal((DoubaoErrorCodes.InvalidRequest, false), (exception.Code, exception.Retryable));
        Assert.Empty(handler.Uris);
    }

    [Theory]
    [InlineData(null, "minimal")]
    [InlineData("auto", null)]
    public async Task MessagesOptionsEditedOutsideTheRulesAreRejectedBeforeAnyCall(string? thinking, string? effort)
    {
        var handler = new FakeHandler(_ => Json(MessagesSuccess));
        var configuration = MessagesConfiguration(thinking) with
        {
            Options = MessagesConfiguration(thinking).Options with { ReasoningEffort = effort },
        };
        var exception = await Assert.ThrowsAsync<DoubaoProviderException>(() => Create(handler, configuration).AskAsync(new("问题")));
        Assert.Equal((DoubaoErrorCodes.InvalidRequest, false), (exception.Code, exception.Retryable));
        Assert.Empty(handler.Uris);
    }

    [Fact]
    public void UsageSummaryReadsTheMessagesShape()
    {
        var withTotal = DoubaoUsageSummary.FromRawOutput(JsonSerializer.SerializeToElement(new
        {
            content = Array.Empty<object>(),
            usage = new { input_tokens = 10, output_tokens = 5, total_tokens = 99, server_tool_use = new { web_search_requests = 3 } },
        }))!;
        Assert.Equal((10L, 5L, (long?)null, 99L, 3L, 3L),
            (withTotal.InputTokens, withTotal.OutputTokens, withTotal.ReasoningTokens, withTotal.TotalTokens, withTotal.WebSearchCalls, withTotal.DoubaoSearchCalls));
        var noSearch = DoubaoUsageSummary.FromRawOutput(JsonSerializer.SerializeToElement(new
        {
            content = Array.Empty<object>(), usage = new { input_tokens = 10, output_tokens = 5 },
        }))!;
        Assert.Equal((10L, 5L, (long?)null, 15L, (long?)null, (long?)null),
            (noSearch.InputTokens, noSearch.OutputTokens, noSearch.ReasoningTokens, noSearch.TotalTokens, noSearch.WebSearchCalls, noSearch.DoubaoSearchCalls));
        // The Responses shape is unchanged: no total means null, not a sum.
        var responses = DoubaoUsageSummary.FromRawOutput(JsonSerializer.SerializeToElement(new
        {
            output = Array.Empty<object>(), usage = new { input_tokens = 10, output_tokens = 5 },
        }))!;
        Assert.Null(responses.TotalTokens);
    }

    // Local baseline (jogeoai answer_provider_settings before 047): {messages, thinking null} → model default (thinking on).
    private const string MessagesBaselineBody =
        """{"model":"doubao-seed-2-1-pro-260915","max_tokens":16000,"messages":[{"role":"user","content":"baseline prompt"}],"tools":[{"type":"web_search_20250305","name":"web_search","max_uses":3}]}""";

    [Fact]
    public async Task DefaultAnswerModeSendsTheBaselineMessagesBodyByteForByte()
    {
        var withoutMode = new FakeHandler(_ => Json(MessagesSuccess));
        await Create(withoutMode, MessagesConfiguration()).AskAsync(new("baseline prompt"));
        var withDefault = new FakeHandler(_ => Json(MessagesSuccess));
        await Create(withDefault, MessagesConfiguration())
            .AskAsync(new("baseline prompt", AnswerMode: AnswerModeRules.ToStorage(AnswerProviderDefinition.DoubaoApi.DefaultAnswerMode)));

        Assert.Equal(AnswerMode.Deep, AnswerProviderDefinition.DoubaoApi.DefaultAnswerMode);
        Assert.Equal(MessagesBaselineBody, Assert.Single(withoutMode.RequestBodies));
        Assert.Equal(MessagesBaselineBody, Assert.Single(withDefault.RequestBodies));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("enabled")]
    public async Task FastModeDisablesMessagesThinkingAndDeepOmitsIt(string? globalThinking)
    {
        var fast = new FakeHandler(_ => Json(MessagesSuccess));
        await Create(fast, MessagesConfiguration(globalThinking)).AskAsync(new("baseline prompt", AnswerMode: "fast"));
        Assert.Equal(MessagesBaselineBody[..^1] + ""","thinking":{"type":"disabled"}}""", Assert.Single(fast.RequestBodies));

        var deep = new FakeHandler(_ => Json(MessagesSuccess));
        await Create(deep, MessagesConfiguration(globalThinking)).AskAsync(new("baseline prompt", AnswerMode: "deep"));
        Assert.Equal(MessagesBaselineBody, Assert.Single(deep.RequestBodies));
    }

    [Fact]
    public async Task NullAnswerModeKeepsTheGlobalMessagesThinkingForDiagnostics()
    {
        var handler = new FakeHandler(_ => Json(MessagesSuccess));
        await Create(handler, MessagesConfiguration("disabled")).AskAsync(new("baseline prompt"));
        Assert.Equal(MessagesBaselineBody[..^1] + ""","thinking":{"type":"disabled"}}""", Assert.Single(handler.RequestBodies));
    }

    [Fact]
    public async Task WebSearchModeKeepsItsBodyForDeepAndRejectsFastBeforeAnyCall()
    {
        var options = new AnswerProviderOptions { Sources = ["doubao"], MaxToolCalls = 3 };
        var withoutMode = new FakeHandler(_ => Json(OfficialSuccess));
        await Create(withoutMode, Configuration(options)).AskAsync(new("baseline prompt"));
        var deep = new FakeHandler(_ => Json(OfficialSuccess));
        await Create(deep, Configuration(options)).AskAsync(new("baseline prompt", AnswerMode: "deep"));
        Assert.Equal(Assert.Single(withoutMode.RequestBodies), Assert.Single(deep.RequestBodies));

        var fast = new FakeHandler(_ => Json(OfficialSuccess));
        var exception = await Assert.ThrowsAsync<DoubaoProviderException>(() =>
            Create(fast, Configuration(options)).AskAsync(new("baseline prompt", AnswerMode: "fast")));
        Assert.Equal((DoubaoErrorCodes.InvalidRequest, false), (exception.Code, exception.Retryable));
        Assert.Empty(fast.Uris);
    }

    [Fact]
    public async Task InvalidAnswerModeIsRejectedBeforeAnyCall()
    {
        var handler = new FakeHandler(_ => Json(MessagesSuccess));
        var exception = await Assert.ThrowsAsync<DoubaoProviderException>(() =>
            Create(handler, MessagesConfiguration()).AskAsync(new("问题", AnswerMode: "turbo")));
        Assert.Equal((DoubaoErrorCodes.InvalidRequest, false), (exception.Code, exception.Retryable));
        Assert.Empty(handler.Uris);
    }

    private static DoubaoArkAdapter Create(FakeHandler handler) => Create(handler, Configuration());

    private static DoubaoArkAdapter Create(FakeHandler handler, AnswerProviderConfiguration? configuration) =>
        new(new SingleClientFactory(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }),
            new FakeSettings(configuration), NullLogger<DoubaoArkAdapter>.Instance);

    private static AnswerProviderConfiguration Configuration(AnswerProviderOptions? options = null, int timeoutSeconds = 30) =>
        new("doubao_api", ApiUrl, Model, Credential, timeoutSeconds, options ?? new());

    private static HttpResponseMessage Json(string body) => Status(HttpStatusCode.OK, body);

    private static HttpResponseMessage Status(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal("doubao", name);
            return client;
        }
    }

    private sealed class FakeSettings(AnswerProviderConfiguration? configuration) : IAnswerProviderSettingsStore
    {
        public Task<AnswerProviderSettingsView?> GetAsync(string provider, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<AnswerProviderConfiguration?> GetConfigurationAsync(string provider, CancellationToken cancellationToken = default)
        {
            Assert.Equal("doubao_api", provider);
            return Task.FromResult(configuration);
        }
        public Task<AnswerProviderSettingsView> SaveAsync(string provider, SaveAnswerProviderSettingsRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    // Mirrors CloroAdapterTestFixture.FakeHandler and additionally lets a responder observe the cancellation token.
    internal sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        internal FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            : this((request, _) => Task.FromResult(responder(request))) { }
        internal List<string> RequestBodies { get; } = [];
        internal List<string?> Authorizations { get; } = [];
        internal List<Uri?> Uris { get; } = [];
        internal List<Dictionary<string, string>> Headers { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorizations.Add(request.Headers.Authorization?.Parameter);
            Headers.Add(request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase));
            Uris.Add(request.RequestUri);
            if (request.Content is not null) RequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            return await responder(request, cancellationToken);
        }
    }
}
