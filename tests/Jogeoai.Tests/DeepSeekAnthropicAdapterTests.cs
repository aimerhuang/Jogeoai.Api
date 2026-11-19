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

public sealed class DeepSeekAnthropicAdapterTests
{
    private const string Credential = "sk-deepseek-fixture-9911";
    private const string ApiUrl = "https://deepseek.test/anthropic/v1/messages";
    private const string Model = "deepseek-flash";

    [Fact]
    public async Task RealFlashResponseIsNormalizedWithoutThePreSearchPreamble()
    {
        var handler = new FakeHandler(_ => Json(DeepSeekAnthropicFixtures.FlashNoThinking));
        var result = await Create(handler).AskAsync(new("市政管道巡检有哪些靠谱的管道摄像头厂家？"));

        Assert.Equal(("deepseek_api", "deepseek", "api", "api_response", "deepseek-api-v1"),
            (result.Provider, result.Engine, result.CaptureMode, result.EvidenceClass, result.ParserVersion));
        // The "我来帮您查找…" preamble precedes the first search and is not part of the answer.
        Assert.DoesNotContain("我来帮您查找", result.AnswerText);
        Assert.StartsWith("已为您整理出覆盖市政管道巡检", result.AnswerText);
        // Markdown is kept as returned: headings, horizontal rules, tables and bold text.
        Assert.Contains("\n\n---\n\n## 一、管道摄像头/检测设备的三大类产品\n\n| 设备类型 | 用途 | 典型场景 |\n|---|---|---|", result.AnswerText);
        Assert.Contains("| **管道CCTV检测机器人（爬行器）** |", result.AnswerText);
        Assert.Equal(result.AnswerText.Trim(), result.AnswerText);
        Assert.True(result.AnswerPresent);
        Assert.Equal("deepseek-flash", result.ModelVersion);
        Assert.Equal(["市政管道巡检 管道检测摄像头 厂家 推荐", "供水管道 排水管网 管道CCTV检测机器人 厂商", "电缆管井 管道潜望镜 内窥镜 检测设备 生产厂家"],
            result.WebQueries);
        Assert.Equal("available", result.QueryAvailability);

        Assert.Equal(9, result.Citations.Count);
        Assert.Equal(Enumerable.Range(1, 9), result.Citations.Select(c => c.CitationIndex));
        Assert.Equal(("https://installershow.com/exhibitors/forbest-products-co", "installershow.com", "Forbest Products Co"),
            (result.Citations[0].Url, result.Citations[0].Domain, result.Citations[0].Title));
        Assert.Equal(("srodcn.com", "重磅上新｜施罗德猫头鹰E30D，看得清更望得远！"), (result.Citations[1].Domain, result.Citations[1].Title));
        Assert.All(result.Citations, c => Assert.False(string.IsNullOrWhiteSpace(c.Title)));
        Assert.All(result.Citations, c => Assert.DoesNotContain("#", c.Url));
        Assert.Contains(result.Citations, c => c.Url.StartsWith("http://jdzj.com/", StringComparison.Ordinal));

        var quality = result.ResultQuality!;
        Assert.True(quality.ModelVersionProvided);
        Assert.Null(quality.AnswerAbsence);
        Assert.Equal(("available", "complete", 9, 9, 0, 0, 0), (quality.Citations.Availability, quality.Citations.Completeness,
            quality.Citations.ReceivedCount, quality.Citations.RetainedCount, quality.Citations.FilteredCount, quality.Citations.InvalidCount,
            quality.Citations.TruncatedCount));
        Assert.Empty(quality.Citations.Reasons);
        Assert.NotNull(ResultQualityCodec.Encode(result, result.AnswerText));

        var raw = result.RawOutput.GetRawText();
        Assert.DoesNotContain("encrypted_content", raw);
        Assert.DoesNotContain("ENC-FIXTURE", raw);
        Assert.Equal("8f7780e8-a960-4d6e-9e2a-409868bdfe9c", result.RawOutput.GetProperty("id").GetString());
        var usage = DeepSeekUsageSummary.FromRawOutput(result.RawOutput)!;
        Assert.Equal((21691L, 384L, 1428L, 3L), (usage.InputTokens, usage.CacheReadInputTokens, usage.OutputTokens, usage.WebSearchRequests));
        var summary = CloroVisibilityStore.UsageSummary(result.RawOutput)!;
        Assert.Equal(3, summary["server_tool_use.web_search_requests"]);
        Assert.DoesNotContain("service_tier", summary.Keys);
    }

    [Fact]
    public async Task RequestUsesAnthropicHeadersAndExplicitThinking()
    {
        var handler = new FakeHandler(_ => Json(DeepSeekAnthropicFixtures.FlashNoThinking));
        await Create(handler).AskAsync(new("问题一"));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(ApiUrl, request.Uri);
        Assert.Equal(Credential, request.ApiKey);
        Assert.Equal("2023-06-01", request.AnthropicVersion);
        Assert.Null(request.Authorization);
        Assert.DoesNotContain(Credential, request.Body);
        using var body = JsonDocument.Parse(request.Body);
        var root = body.RootElement;
        Assert.Equal(Model, root.GetProperty("model").GetString());
        Assert.Equal(16000, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal("disabled", root.GetProperty("thinking").GetProperty("type").GetString());
        var message = Assert.Single(root.GetProperty("messages").EnumerateArray());
        Assert.Equal(("user", "问题一"), (message.GetProperty("role").GetString(), message.GetProperty("content").GetString()));
        var tool = Assert.Single(root.GetProperty("tools").EnumerateArray());
        Assert.Equal(("web_search_20250305", "web_search", 5),
            (tool.GetProperty("type").GetString(), tool.GetProperty("name").GetString(), tool.GetProperty("max_uses").GetInt32()));
        Assert.False(root.TryGetProperty("stream", out _));
    }

    [Theory]
    [InlineData("enabled", 3, "enabled")]
    [InlineData("disabled", 10, "disabled")]
    [InlineData(null, 1, "disabled")]
    public async Task ThinkingSwitchAndMaxUsesFollowTheSettings(string? thinking, int maxToolCalls, string expected)
    {
        var handler = new FakeHandler(_ => Json(DeepSeekAnthropicFixtures.FlashNoThinking));
        var options = new AnswerProviderOptions { Sources = [], MaxToolCalls = maxToolCalls, Thinking = thinking };
        await Create(handler, Configuration(options)).AskAsync(new("问题"));
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        Assert.Equal(expected, body.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal(maxToolCalls, body.RootElement.GetProperty("tools")[0].GetProperty("max_uses").GetInt32());
    }

    [Fact]
    public async Task ThinkingResponseStripsReasoningAndIgnoresMaxUsesExceeded()
    {
        var handler = new FakeHandler(_ => Json(DeepSeekAnthropicFixtures.FlashThinking));
        var result = await Create(handler, Configuration(Options(thinking: "enabled"))).AskAsync(new("问题"));

        Assert.Equal(6, result.WebQueries.Count);
        Assert.Equal("管道摄像头厂家 市政管道检测 CCTV管道内窥镜 品牌", result.WebQueries[0]);
        Assert.Equal("排水管道CCTV检测机器人 品牌对比 中仪 施罗德 博铭维 九泰 谁家好", result.WebQueries[^1]);
        // 15 search results; two differ only in the #1/#2 fragment and are one source.
        Assert.Equal(14, result.Citations.Count);
        Assert.Single(result.Citations, c => c.Url == "https://yhresearch.cn/reports/2292903/pipeline-cctv-inspection-robot");
        Assert.Equal(Enumerable.Range(1, 14), result.Citations.Select(c => c.CitationIndex));
        // The max_uses_exceeded error item of the sixth search is not a failure and not a source.
        var quality = result.ResultQuality!.Citations;
        Assert.Equal(("available", "complete", 14, 14), (quality.Availability, quality.Completeness, quality.ReceivedCount, quality.RetainedCount));
        Assert.True(result.AnswerPresent);
        Assert.NotNull(ResultQualityCodec.Encode(result, result.AnswerText));

        var raw = result.RawOutput.GetRawText();
        Assert.DoesNotContain("THINKING-FIXTURE", raw);
        Assert.DoesNotContain("SIG-FIXTURE", raw);
        Assert.DoesNotContain("\"signature\"", raw);
        Assert.DoesNotContain("ENC-FIXTURE", raw);
        Assert.Contains("max_uses_exceeded", raw);
        var thinkingBlocks = result.RawOutput.GetProperty("content").EnumerateArray()
            .Where(block => block.GetProperty("type").GetString() == "thinking").ToArray();
        Assert.Equal(4, thinkingBlocks.Length);
        Assert.All(thinkingBlocks, block => Assert.False(block.TryGetProperty("thinking", out _)));
        Assert.Equal(6L, DeepSeekUsageSummary.FromRawOutput(result.RawOutput)!.WebSearchRequests);
    }

    [Fact]
    public async Task EmptyQueryAndInvalidToolInputAreIgnored()
    {
        var result = await Create(new FakeHandler(_ => Json(DeepSeekAnthropicFixtures.ProNoThinking))).AskAsync(new("问题"));
        Assert.Equal(["市政管道CCTV检测 管道摄像头 厂家品牌", "管道机器人 排水管网检测 厂家 品牌 推荐"], result.WebQueries);
        Assert.Equal("available", result.QueryAvailability);
        Assert.Equal(6, result.Citations.Count);
        Assert.Equal("deepseek-v4-pro", result.ModelVersion);
        Assert.Equal(("available", "complete"), (result.ResultQuality!.Citations.Availability, result.ResultQuality.Citations.Completeness));
        Assert.True(result.AnswerPresent);
    }

    [Fact]
    public async Task OnlyEmptyQueriesMeanSearchedButQueriesUnknown()
    {
        const string response = """
            {"id":"m1","model":"deepseek-flash","stop_reason":"end_turn","content":[
              {"type":"server_tool_use","id":"c1","name":"web_search","input":{"query":"  "}},
              {"type":"web_search_tool_result","tool_use_id":"c1","content":[{"type":"web_search_tool_result_error","error_code":"invalid_tool_input"}]},
              {"type":"text","text":"无法搜索，直接回答。"}]}
            """;
        var result = await Create(new FakeHandler(_ => Json(response))).AskAsync(new("问题"));
        Assert.Empty(result.WebQueries);
        Assert.Equal("unknown", result.QueryAvailability);
        Assert.Empty(result.Citations);
        Assert.Equal(("empty", "complete", 0), (result.ResultQuality!.Citations.Availability, result.ResultQuality.Citations.Completeness,
            result.ResultQuality.Citations.ReceivedCount));
        Assert.NotNull(ResultQualityCodec.Encode(result, result.AnswerText));
    }

    [Fact]
    public async Task NoSearchMeansEmptyQueriesAndUnexposedSources()
    {
        const string response = """
            {"id":"m2","model":"deepseek-flash","stop_reason":"end_turn","content":[
              {"type":"text","text":"第一段。"},{"type":"text","text":"第二段。"}],
             "usage":{"input_tokens":10,"output_tokens":20}}
            """;
        var result = await Create(new FakeHandler(_ => Json(response))).AskAsync(new("问题"));
        Assert.Equal("第一段。\n\n第二段。", result.AnswerText);
        Assert.Empty(result.WebQueries);
        Assert.Equal("empty", result.QueryAvailability);
        Assert.Empty(result.Citations);
        Assert.Equal(("not_exposed", "unknown", (int?)null), (result.ResultQuality!.Citations.Availability,
            result.ResultQuality.Citations.Completeness, result.ResultQuality.Citations.ReceivedCount));
        Assert.NotNull(ResultQualityCodec.Encode(result, result.AnswerText));
        Assert.Null(DeepSeekUsageSummary.FromRawOutput(result.RawOutput)!.WebSearchRequests);
    }

    [Fact]
    public async Task EmptyAnswerAfterTheLastSearchIsAKnownAbsence()
    {
        const string response = """
            {"id":"m3","stop_reason":"end_turn","content":[
              {"type":"text","text":"我来帮您查找。"},
              {"type":"server_tool_use","id":"c1","name":"web_search","input":{"query":"查询"}},
              {"type":"web_search_tool_result","tool_use_id":"c1","content":[{"type":"web_search_result","url":"https://example.com/a","title":"A"}]},
              {"type":"text","text":"  \n "}]}
            """;
        var result = await Create(new FakeHandler(_ => Json(response))).AskAsync(new("问题"));
        Assert.False(result.AnswerPresent);
        Assert.Equal("", result.AnswerText);
        Assert.Equal("empty_answer", result.ResultQuality!.AnswerAbsence);
        // No model in the response: fall back to the configured model and say so.
        Assert.Equal(Model, result.ModelVersion);
        Assert.False(result.ResultQuality.ModelVersionProvided);
        Assert.Equal(["查询"], result.WebQueries);
        Assert.Single(result.Citations);
        var json = ResultQualityCodec.Encode(result, result.AnswerText)!;
        Assert.Equal("empty_answer", ResultQualityCodec.Decode(json)!.AnswerAbsence);
    }

    [Fact]
    public async Task WholeBlockSearchErrorIsNotAFailure()
    {
        const string response = """
            {"id":"m4","model":"deepseek-flash","stop_reason":"end_turn","content":[
              {"type":"server_tool_use","id":"c1","name":"web_search","input":{"query":"查询"}},
              {"type":"web_search_tool_result","tool_use_id":"c1","content":{"type":"web_search_tool_result_error","error_code":"max_uses_exceeded"}},
              {"type":"text","text":"回答"}]}
            """;
        var result = await Create(new FakeHandler(_ => Json(response))).AskAsync(new("问题"));
        Assert.Equal("回答", result.AnswerText);
        Assert.Empty(result.Citations);
        Assert.Equal(("empty", "complete"), (result.ResultQuality!.Citations.Availability, result.ResultQuality.Citations.Completeness));
        Assert.NotNull(ResultQualityCodec.Encode(result, result.AnswerText));
    }

    [Fact]
    public async Task MarkdownIsNormalizedAndTextBlocksAfterTheLastSearchAreJoined()
    {
        const string response = """
            {"id":"m5","model":"deepseek-flash","stop_reason":"end_turn","content":[
              {"type":"server_tool_use","id":"c1","name":"web_search","input":{"query":"查询"}},
              {"type":"web_search_tool_result","tool_use_id":"c1","content":[]},
              {"type":"text","text":"\r\n## 标题  \r\n\r\n\r\n\r\n- **要点**\t\r\n"},
              {"type":"text","text":"```\ncode  \n\n\n\nend\n```"}]}
            """;
        var result = await Create(new FakeHandler(_ => Json(response))).AskAsync(new("问题"));
        Assert.Equal("## 标题\n\n- **要点**\n\n```\ncode  \n\n\n\nend\n```", result.AnswerText);
    }

    [Fact]
    public async Task UnsafeInvalidAndOversizedSourcesAreCountedNotStored()
    {
        var longTitle = new string('标', 600);
        var response = $$$"""
            {"id":"m6","model":"m","stop_reason":"end_turn","content":[
              {"type":"server_tool_use","id":"c1","name":"web_search","input":{"query":"q"}},
              {"type":"web_search_tool_result","tool_use_id":"c1","content":[
                {"type":"web_search_result","title":"内网","url":"http://localhost/admin"},
                {"type":"web_search_result","title":"坏链接","url":"not a url"},
                {"type":"web_search_tool_result_error","error_code":"unavailable"},
                {"type":"web_search_result","title":"{{{longTitle}}}","url":"https://example.com/a?token=abc"},
                {"type":"web_search_result","title":"{{{longTitle}}}","url":"https://example.com/ok"},
                {"type":"web_search_result","url":"https://example.org/untitled"}
              ]},
              {"type":"text","text":"回答"}]}
            """;
        var result = await Create(new FakeHandler(_ => Json(response))).AskAsync(new("问题"));
        Assert.Equal(["https://example.com/ok", "https://example.org/untitled"], result.Citations.Select(c => c.Url));
        Assert.Equal(512, result.Citations[0].Title!.EnumerateRunes().Count());
        Assert.Null(result.Citations[1].Title);
        var c = result.ResultQuality!.Citations;
        Assert.Equal(("unknown", "partial", 5, 2, 2, 1, 0), (c.Availability, c.Completeness, c.ReceivedCount, c.RetainedCount,
            c.FilteredCount, c.InvalidCount, c.TruncatedCount));
        Assert.Equal(["INVALID_SOURCE_SHAPE", "SOURCE_FIELD_LIMIT", "UNSAFE_SOURCE"], c.Reasons);
        Assert.NotNull(ResultQualityCodec.Encode(result, result.AnswerText));
    }

    [Fact]
    public async Task MoreThanOneHundredSourcesAreTruncatedAsSourceLimit()
    {
        var blocks = new List<string>();
        for (var search = 0; search < 11; search++)
        {
            blocks.Add($$$"""{"type":"server_tool_use","id":"c{{{search}}}","name":"web_search","input":{"query":"查询{{{search}}}"}}""");
            var items = Enumerable.Range(0, 10).Select(i =>
                $$"""{"type":"web_search_result","title":"来源{{search}}-{{i}}","url":"https://s{{search}}.example.com/p/{{i}}","encrypted_content":"ENC"}""");
            blocks.Add($$"""{"type":"web_search_tool_result","tool_use_id":"c{{search}}","content":[{{string.Join(",", items)}}]}""");
        }
        blocks.Add("""{"type":"text","text":"回答"}""");
        var response = $$"""{"id":"m7","model":"deepseek-flash","stop_reason":"end_turn","content":[{{string.Join(",", blocks)}}]}""";
        var result = await Create(new FakeHandler(_ => Json(response))).AskAsync(new("问题"));
        Assert.Equal(CitationUrlRules.MaximumCitations, result.Citations.Count);
        Assert.Equal(11, result.WebQueries.Count);
        Assert.Equal("https://s9.example.com/p/9", result.Citations[^1].Url);
        var c = result.ResultQuality!.Citations;
        Assert.Equal(("available", "partial", 110, 100, 10), (c.Availability, c.Completeness, c.ReceivedCount, c.RetainedCount, c.TruncatedCount));
        Assert.Equal(["SOURCE_LIMIT"], c.Reasons);
        Assert.NotNull(ResultQualityCodec.Encode(result, result.AnswerText));
    }

    [Fact]
    public async Task TruncatedAnswerIsATerminalInvalidResponse()
    {
        var body = DeepSeekAnthropicFixtures.FlashNoThinking.Replace("\"end_turn\"", "\"max_tokens\"", StringComparison.Ordinal);
        var exception = await Assert.ThrowsAsync<DeepSeekProviderException>(() => Create(new FakeHandler(_ => Json(body))).AskAsync(new("问题")));
        Assert.Equal(DeepSeekErrorCodes.InvalidResponse, exception.Code);
        Assert.False(exception.Retryable);
    }

    [Fact]
    public async Task PauseTurnIsARetryableRequestFailure()
    {
        var body = DeepSeekAnthropicFixtures.FlashNoThinking.Replace("\"end_turn\"", "\"pause_turn\"", StringComparison.Ordinal);
        var exception = await Assert.ThrowsAsync<DeepSeekProviderException>(() => Create(new FakeHandler(_ => Json(body))).AskAsync(new("问题")));
        Assert.Equal(DeepSeekErrorCodes.RequestFailed, exception.Code);
        Assert.True(exception.Retryable);
    }

    [Theory]
    [InlineData(401, "authentication_error", DeepSeekErrorCodes.AuthFailed, false, null)]
    [InlineData(401, null, DeepSeekErrorCodes.AuthFailed, false, null)]
    [InlineData(403, "permission_error", DeepSeekErrorCodes.AuthFailed, false, null)]
    [InlineData(402, "insufficient_balance", DeepSeekErrorCodes.InsufficientBalance, true, 10)]
    [InlineData(402, null, DeepSeekErrorCodes.InsufficientBalance, true, 10)]
    [InlineData(429, "rate_limit_error", DeepSeekErrorCodes.RateLimited, true, null)]
    [InlineData(429, null, DeepSeekErrorCodes.RateLimited, true, null)]
    [InlineData(503, null, DeepSeekErrorCodes.RateLimited, true, null)]
    [InlineData(529, "overloaded_error", DeepSeekErrorCodes.RateLimited, true, null)]
    [InlineData(500, "overloaded_error", DeepSeekErrorCodes.RateLimited, true, null)]
    [InlineData(400, "invalid_request_error", DeepSeekErrorCodes.InvalidRequest, false, null)]
    [InlineData(404, "not_found_error", DeepSeekErrorCodes.InvalidRequest, false, null)]
    [InlineData(413, "request_too_large", DeepSeekErrorCodes.InvalidRequest, false, null)]
    [InlineData(422, null, DeepSeekErrorCodes.InvalidRequest, false, null)]
    [InlineData(418, null, DeepSeekErrorCodes.InvalidRequest, false, null)]
    [InlineData(408, null, DeepSeekErrorCodes.RequestFailed, true, null)]
    [InlineData(500, "api_error", DeepSeekErrorCodes.RequestFailed, true, null)]
    [InlineData(502, null, DeepSeekErrorCodes.RequestFailed, true, null)]
    public async Task HttpFailuresMapToStableCodesWithoutLeakingTheBody(int status, string? errorType, string code, bool retryable, int? deferMinutes)
    {
        var body = errorType is null ? "<html>gateway SECRET-BODY-TEXT</html>"
            : $$$"""{"type":"error","error":{"type":"{{{errorType}}}","message":"SECRET-BODY-TEXT {{{Credential}}}"}}""";
        var exception = await Assert.ThrowsAsync<DeepSeekProviderException>(() =>
            Create(new FakeHandler(_ => Status((HttpStatusCode)status, body))).AskAsync(new("问题")));
        Assert.Equal(code, exception.Code);
        Assert.Equal(retryable, exception.Retryable);
        Assert.Equal(deferMinutes, exception.DeferMinutes);
        Assert.DoesNotContain("SECRET-BODY-TEXT", exception.Message);
        Assert.DoesNotContain(Credential, exception.Message);
        Assert.DoesNotContain("<html>", exception.Message);
        if (errorType is not null) Assert.Contains(errorType, exception.Message);
        Assert.IsAssignableFrom<AnswerProviderException>(exception);
    }

    [Fact]
    public async Task OpenAiShapedErrorCodeIsReadWhenTypeIsMissing()
    {
        const string body = """{"error":{"message":"SECRET","code":"invalid_api_key","param":null}}""";
        var exception = await Assert.ThrowsAsync<DeepSeekProviderException>(() =>
            Create(new FakeHandler(_ => Status(HttpStatusCode.Unauthorized, body))).AskAsync(new("问题")));
        Assert.Equal(DeepSeekErrorCodes.AuthFailed, exception.Code);
        Assert.Contains("invalid_api_key", exception.Message);
        Assert.DoesNotContain("SECRET", exception.Message);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"id\":\"m\",\"type\":\"message\"}")]
    [InlineData("{\"content\":{\"type\":\"text\"}}")]
    [InlineData("[]")]
    public async Task MalformedOrShapelessResponsesAreTerminal(string body)
    {
        var exception = await Assert.ThrowsAsync<DeepSeekProviderException>(() => Create(new FakeHandler(_ => Json(body))).AskAsync(new("问题")));
        Assert.Equal(DeepSeekErrorCodes.InvalidResponse, exception.Code);
        Assert.False(exception.Retryable);
    }

    [Fact]
    public async Task ConfiguredTimeoutCancelsTheCallAsRetryableTimeout()
    {
        var handler = new FakeHandler(async (_, ct) => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return Json("{}"); });
        var exception = await Assert.ThrowsAsync<DeepSeekProviderException>(() =>
            Create(handler, Configuration(timeoutSeconds: 1)).AskAsync(new("问题")));
        Assert.Equal(DeepSeekErrorCodes.Timeout, exception.Code);
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
        var exception = await Assert.ThrowsAsync<DeepSeekProviderException>(() => Create(handler).AskAsync(new("问题")));
        Assert.Equal(DeepSeekErrorCodes.RequestFailed, exception.Code);
        Assert.True(exception.Retryable);
        Assert.DoesNotContain("connection refused", exception.Message);
    }

    [Fact]
    public async Task MissingConfigurationNeverCallsDeepSeek()
    {
        var handler = new FakeHandler(_ => Json(DeepSeekAnthropicFixtures.FlashNoThinking));
        var exception = await Assert.ThrowsAsync<DeepSeekProviderException>(() => Create(handler, configuration: null).AskAsync(new("问题")));
        Assert.Equal(DeepSeekErrorCodes.NotConfigured, exception.Code);
        Assert.False(exception.Retryable);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("doubao_app", null)]
    [InlineData("web_search", "auto")]
    public async Task UnsupportedStoredOptionsAreRejectedBeforeAnyCall(string requestMode, string? thinking)
    {
        var handler = new FakeHandler(_ => Json(DeepSeekAnthropicFixtures.FlashNoThinking));
        var options = new AnswerProviderOptions { RequestMode = requestMode, Sources = [], Thinking = thinking };
        var exception = await Assert.ThrowsAsync<DeepSeekProviderException>(() => Create(handler, Configuration(options)).AskAsync(new("问题")));
        Assert.Equal(DeepSeekErrorCodes.InvalidRequest, exception.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task BlankPromptIsRejectedBeforeAnyCall()
    {
        var handler = new FakeHandler(_ => Json(DeepSeekAnthropicFixtures.FlashNoThinking));
        var exception = await Assert.ThrowsAsync<DeepSeekProviderException>(() => Create(handler).AskAsync(new("   ")));
        Assert.Equal(DeepSeekErrorCodes.InvalidRequest, exception.Code);
        Assert.Empty(handler.Requests);
    }

    // Local baseline (jogeoai answer_provider_settings before 047): {web_search, thinking disabled, max 5 searches}.
    private const string BaselineBody =
        """{"model":"deepseek-flash","max_tokens":16000,"thinking":{"type":"disabled"},"messages":[{"role":"user","content":"baseline prompt"}],"tools":[{"type":"web_search_20250305","name":"web_search","max_uses":5}]}""";

    [Fact]
    public async Task DefaultAnswerModeSendsTheBaselineBodyByteForByte()
    {
        var withoutMode = new FakeHandler(_ => Json(DeepSeekAnthropicFixtures.FlashNoThinking));
        await Create(withoutMode, Configuration(DeepSeekDefaults.Options())).AskAsync(new("baseline prompt"));
        var withDefault = new FakeHandler(_ => Json(DeepSeekAnthropicFixtures.FlashNoThinking));
        await Create(withDefault, Configuration(DeepSeekDefaults.Options()))
            .AskAsync(new("baseline prompt", AnswerMode: AnswerModeRules.ToStorage(AnswerProviderDefinition.DeepSeekApi.DefaultAnswerMode)));

        Assert.Equal(AnswerMode.Fast, AnswerProviderDefinition.DeepSeekApi.DefaultAnswerMode);
        Assert.Equal(BaselineBody, Assert.Single(withoutMode.Requests).Body);
        Assert.Equal(BaselineBody, Assert.Single(withDefault.Requests).Body);
    }

    [Theory]
    [InlineData("disabled", "deep", "enabled")]
    [InlineData("enabled", "fast", "disabled")]
    [InlineData(null, "deep", "enabled")]
    public async Task AnswerModeOverridesOnlyTheGlobalThinkingSwitch(string? globalThinking, string mode, string expected)
    {
        var handler = new FakeHandler(_ => Json(DeepSeekAnthropicFixtures.FlashNoThinking));
        var options = new AnswerProviderOptions { Sources = [], MaxToolCalls = 7, Thinking = globalThinking };
        await Create(handler, Configuration(options)).AskAsync(new("baseline prompt", AnswerMode: mode));

        var body = Assert.Single(handler.Requests).Body;
        var expectedBody = BaselineBody.Replace("\"thinking\":{\"type\":\"disabled\"}", $"\"thinking\":{{\"type\":\"{expected}\"}}")
            .Replace("\"max_uses\":5", "\"max_uses\":7");
        Assert.Equal(expectedBody, body);
    }

    [Fact]
    public async Task NullAnswerModeKeepsTheGlobalThinkingForDiagnostics()
    {
        var handler = new FakeHandler(_ => Json(DeepSeekAnthropicFixtures.FlashNoThinking));
        await Create(handler, Configuration(Options(thinking: "enabled"))).AskAsync(new("baseline prompt"));
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        Assert.Equal("enabled", body.RootElement.GetProperty("thinking").GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("turbo")]
    [InlineData("")]
    public async Task InvalidAnswerModeIsRejectedBeforeAnyCall(string mode)
    {
        var handler = new FakeHandler(_ => Json(DeepSeekAnthropicFixtures.FlashNoThinking));
        var exception = await Assert.ThrowsAsync<DeepSeekProviderException>(() =>
            Create(handler).AskAsync(new("问题", AnswerMode: mode)));
        Assert.Equal(DeepSeekErrorCodes.InvalidRequest, exception.Code);
        Assert.False(exception.Retryable);
        Assert.DoesNotContain("turbo", exception.Message);
        Assert.Empty(handler.Requests);
    }

    private static AnswerProviderOptions Options(string? thinking = "disabled", int maxToolCalls = 5) =>
        new() { Sources = [], MaxToolCalls = maxToolCalls, Thinking = thinking };

    private static DeepSeekAnthropicAdapter Create(FakeHandler handler) => Create(handler, Configuration());

    private static DeepSeekAnthropicAdapter Create(FakeHandler handler, AnswerProviderConfiguration? configuration) =>
        new(new SingleClientFactory(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }),
            new FakeSettings(configuration), NullLogger<DeepSeekAnthropicAdapter>.Instance);

    private static AnswerProviderConfiguration Configuration(AnswerProviderOptions? options = null, int timeoutSeconds = 30) =>
        new("deepseek_api", ApiUrl, Model, Credential, timeoutSeconds, options ?? Options());

    private static HttpResponseMessage Json(string body) => Status(HttpStatusCode.OK, body);

    private static HttpResponseMessage Status(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal("deepseek", name);
            return client;
        }
    }

    private sealed class FakeSettings(AnswerProviderConfiguration? configuration) : IAnswerProviderSettingsStore
    {
        public Task<AnswerProviderSettingsView?> GetAsync(string provider, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<AnswerProviderConfiguration?> GetConfigurationAsync(string provider, CancellationToken cancellationToken = default)
        {
            Assert.Equal("deepseek_api", provider);
            return Task.FromResult(configuration);
        }
        public Task<AnswerProviderSettingsView> SaveAsync(string provider, SaveAnswerProviderSettingsRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    internal sealed record CapturedRequest(string? Uri, string? ApiKey, string? AnthropicVersion, string? Authorization, string Body);

    internal sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        internal FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            : this((request, _) => Task.FromResult(responder(request))) { }
        internal List<CapturedRequest> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            static string? Header(HttpRequestMessage message, string name) =>
                message.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new(request.RequestUri?.ToString(), Header(request, "x-api-key"), Header(request, "anthropic-version"),
                request.Headers.Authorization?.ToString(), body));
            return await responder(request, cancellationToken);
        }
    }
}
