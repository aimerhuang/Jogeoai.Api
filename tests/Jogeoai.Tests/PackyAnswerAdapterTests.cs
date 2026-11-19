using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Jogeoai.Providers.Abstractions;
using Jogeoai.Providers.Domestic;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jogeoai.Tests;

public sealed class PackyAnswerAdapterTests
{
    private const string Credential = "sk-packy-fixture-4242";
    private const string ResponsesUrl = "https://packy.test/v1/responses";
    private const string MessagesUrl = "https://packy.test/v1/messages";

    [Fact]
    public async Task QwenStreamIsReducedToTheTerminalResponseWithAllRetrievedSources()
    {
        var handler = new FakeHandler(_ => Sse(QwenCompleted()));
        var result = await Create(handler, "packy_qwen").AskAsync(new("packy_qwen", "深圳有哪些靠谱的工业物联网网关厂商？"));

        Assert.Equal(("packy_qwen", "qwen", "api", "api_response", "packy-responses-v1"),
            (result.Provider, result.Engine, result.CaptureMode, result.EvidenceClass, result.ParserVersion));
        Assert.Equal("qwen3.8-flash", result.ModelVersion);
        Assert.Equal("## 深圳工业网关厂商\n\n1. **宏电**：老牌厂商。", result.AnswerText);
        Assert.True(result.AnswerPresent);
        Assert.Equal(["深圳 工业物联网网关 厂商 推荐", "计讯物联 深圳 工业网关 公司"], result.WebQueries);
        Assert.Equal("available", result.QueryAvailability);
        // Duplicate URLs across the two searches count once; sources carry no title on this channel.
        // URLs are normalized by CitationUrlRules (www. dropped).
        Assert.Equal(["https://hongdian.com/", "https://mp.ofweek.com/iot/a1", "https://top-iot.com/"],
            result.Citations.Select(c => c.Url));
        Assert.All(result.Citations, c => Assert.Null(c.Title));
        Assert.Equal([1, 2, 3], result.Citations.Select(c => c.CitationIndex));
        var quality = result.ResultQuality!.Citations;
        Assert.Equal(("available", "complete", 3, 3), (quality.Availability, quality.Completeness, quality.ReceivedCount, quality.RetainedCount));

        var request = Assert.Single(handler.Requests);
        Assert.Equal((ResponsesUrl, $"Bearer {Credential}", null), (request.Uri, request.Authorization, request.ApiKey));
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("qwen3.8-flash", body.RootElement.GetProperty("model").GetString());
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal(16000, body.RootElement.GetProperty("max_output_tokens").GetInt32());
        // Qwen only searches with thinking off; its switch is enable_thinking, not thinking.type.
        Assert.False(body.RootElement.GetProperty("enable_thinking").GetBoolean());
        Assert.False(body.RootElement.TryGetProperty("thinking", out _));
        Assert.False(body.RootElement.TryGetProperty("max_tool_calls", out _));
        Assert.Equal("web_search", body.RootElement.GetProperty("tools")[0].GetProperty("type").GetString());

        var raw = result.RawOutput.GetRawText();
        Assert.DoesNotContain("encrypted_content", raw);
        Assert.DoesNotContain("REASONING-FIXTURE", raw);
        Assert.DoesNotContain(Credential, raw);
        Assert.Equal(13368, PackyUsageSummary.FromRawOutput(result.RawOutput)!.InputTokens);
        Assert.Equal(2, PackyUsageSummary.FromRawOutput(result.RawOutput)!.SearchCalls);
    }

    [Fact]
    public async Task GlmDefaultSendsLowReasoningEffortInsteadOfThinking()
    {
        var handler = new FakeHandler(_ => Sse(QwenCompleted(model: "glm-5.3-flash")));
        var result = await Create(handler, "packy_glm").AskAsync(new("packy_glm", "问题"));

        Assert.Equal(("packy_glm", "zhipu_qingyan", "glm-5.3-flash"), (result.Provider, result.Engine, result.ModelVersion));
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        Assert.Equal("glm-5.3-flash", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("low", body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.False(body.RootElement.TryGetProperty("thinking", out _));
        Assert.False(body.RootElement.TryGetProperty("enable_thinking", out _));
        // The Yuanbao-only output limit and search cap never leak into GLM requests.
        Assert.Equal(16000, body.RootElement.GetProperty("max_output_tokens").GetInt32());
        Assert.False(body.RootElement.TryGetProperty("max_tool_calls", out _));
    }

    [Fact]
    public async Task GlmWithoutReasoningEffortSendsThinkingTypeInsteadOfEnableThinking()
    {
        var handler = new FakeHandler(_ => Sse(QwenCompleted(model: "glm-5.2")));
        var configuration = Configuration("packy_glm") with
        {
            Model = "glm-5.2", Options = PackyDefaults.Options("packy_glm") with { ReasoningEffort = null },
        };
        await Create(handler, "packy_glm", configuration).AskAsync(new("packy_glm", "问题"));

        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        Assert.Equal("disabled", body.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.False(body.RootElement.TryGetProperty("reasoning", out _));
        Assert.False(body.RootElement.TryGetProperty("enable_thinking", out _));
    }

    [Fact]
    public async Task OnlyTheLastMessageIsTheAnswerButEveryMessageCitationIsKept()
    {
        var response = Response("completed", "doubao-like",
            SearchCall("深圳 工业网关;深圳 网关 厂家", Source("https://www.hongdian.com/")),
            Message("我先获取了部分厂商信息，让我再搜索确认。", Citation("https://www.hongdian.com/", "宏电官网"), Citation("https://m.qcc.com/a", "企查查")),
            SearchCall("钡铼 工业网关"),
            Message("# 推荐\n\n钡铼技术。", Citation("https://bliiot.cn/", "钡铼官网")));
        var result = await Create(new FakeHandler(_ => Sse(response)), "packy_qwen").AskAsync(new("packy_qwen", "问题"));

        Assert.Equal("# 推荐\n\n钡铼技术。", result.AnswerText);
        Assert.Equal(["深圳 工业网关", "深圳 网关 厂家", "钡铼 工业网关"], result.WebQueries);
        // The title-less search source is completed by the later url_citation of the same URL.
        Assert.Equal([("https://hongdian.com/", "宏电官网"), ("https://m.qcc.com/a", "企查查"), ("https://bliiot.cn/", "钡铼官网")],
            result.Citations.Select(c => (c.Url, c.Title)));
    }

    [Fact]
    public async Task NonStreamingJsonBodyIsAcceptedToo()
    {
        var handler = new FakeHandler(_ => Json(QwenCompleted().ToJsonString()));
        var result = await Create(handler, "packy_qwen").AskAsync(new("packy_qwen", "问题"));
        Assert.Equal(3, result.Citations.Count);
    }

    [Fact]
    public async Task YuanbaoSendsHy4WithMaxToolCallsAndKeepsVisitedPagesAsSources()
    {
        var visit = new JsonObject
        {
            ["type"] = "web_search_call", ["id"] = "ws_visit", ["status"] = "completed",
            ["action"] = new JsonObject { ["type"] = "visit", ["url"] = "https://www.szulink.com.cn/zh/knowledge/a", ["goal"] = "确认价格" },
        };
        var response = Response("completed", "hy4",
            SearchCall("工业管道内窥镜 品牌 价格"),
            Message("我先查一下主流品牌。"),
            visit,
            Message("## 推荐\n\n优联 3188XX。"));
        var handler = new FakeHandler(_ => Sse(response));
        var result = await Create(handler, "packy_yuanbao").AskAsync(new("packy_yuanbao", "管道内窥镜推荐"));

        Assert.Equal(("packy_yuanbao", "tencent_yuanbao", "hy4", "packy-responses-v1"),
            (result.Provider, result.Engine, result.ModelVersion, result.ParserVersion));
        Assert.Equal("## 推荐\n\n优联 3188XX。", result.AnswerText);
        Assert.Equal(["工业管道内窥镜 品牌 价格"], result.WebQueries);
        var citation = Assert.Single(result.Citations);
        Assert.Equal(("https://szulink.com.cn/zh/knowledge/a", null), (citation.Url, citation.Title));
        Assert.Equal("available", result.ResultQuality!.Citations.Availability);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(ResponsesUrl, request.Uri);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("hy4", body.RootElement.GetProperty("model").GetString());
        Assert.Equal(3, body.RootElement.GetProperty("max_tool_calls").GetInt32());
        // hy4 always reasons and the reasoning counts against the output limit.
        Assert.Equal(32000, body.RootElement.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal("disabled", body.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.False(body.RootElement.TryGetProperty("enable_thinking", out _));
        Assert.False(body.RootElement.TryGetProperty("reasoning", out _));
        Assert.Equal("web_search", body.RootElement.GetProperty("tools")[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task YuanbaoSearchWithoutAnySourceIsNotExposedButQwenStaysEmpty()
    {
        var response = Response("completed", "hy4", SearchCall("管道内窥镜 品牌"), Message("## 推荐\n\n几个品牌。"));
        var yuanbao = await Create(new FakeHandler(_ => Sse(response)), "packy_yuanbao").AskAsync(new("packy_yuanbao", "问题"));
        Assert.Equal(("not_exposed", "available"), (yuanbao.ResultQuality!.Citations.Availability, yuanbao.QueryAvailability));
        Assert.Empty(yuanbao.Citations);

        var qwen = await Create(new FakeHandler(_ => Sse(response)), "packy_qwen").AskAsync(new("packy_qwen", "问题"));
        Assert.Equal("empty", qwen.ResultQuality!.Citations.Availability);
    }

    [Fact]
    public async Task YuanbaoMaxToolCallsFollowsTheStoredOption()
    {
        var handler = new FakeHandler(_ => Sse(Response("completed", "hy4", Message("回答。"))));
        var configuration = Configuration("packy_yuanbao") with { Options = PackyDefaults.Options("packy_yuanbao") with { MaxToolCalls = 5 } };
        await Create(handler, "packy_yuanbao", configuration).AskAsync(new("packy_yuanbao", "问题"));
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        Assert.Equal(5, body.RootElement.GetProperty("max_tool_calls").GetInt32());
    }

    [Fact]
    public async Task AnswerWithoutAnySearchIsStoredAsNotExposed()
    {
        var response = Response("completed", "qwen3.8-flash", Message("直接回答。"));
        var result = await Create(new FakeHandler(_ => Sse(response)), "packy_qwen").AskAsync(new("packy_qwen", "问题"));

        Assert.True(result.AnswerPresent);
        Assert.Empty(result.Citations);
        Assert.Equal("empty", result.QueryAvailability);
        Assert.Equal("not_exposed", result.ResultQuality!.Citations.Availability);
    }

    [Fact]
    public async Task SearchWithoutAnswerTextIsStoredAsEmptyAnswer()
    {
        var response = Response("completed", "qwen3.8-flash", SearchCall("问题", Source("https://a.example.com/")));
        var result = await Create(new FakeHandler(_ => Sse(response)), "packy_qwen").AskAsync(new("packy_qwen", "问题"));

        Assert.False(result.AnswerPresent);
        Assert.Equal("empty_answer", result.ResultQuality!.AnswerAbsence);
        Assert.Single(result.Citations);
    }

    [Fact]
    public async Task TruncatedResponseFailsTerminally()
    {
        var response = Response("incomplete", "qwen3.8-flash", SearchCall("问题", Source("https://a.example.com/")), Message("写到一半"));
        response["incomplete_details"] = new JsonObject { ["reason"] = "max_output_tokens" };
        var exception = await Assert.ThrowsAsync<PackyProviderException>(() =>
            Create(new FakeHandler(_ => Sse(response, terminal: "response.incomplete")), "packy_qwen").AskAsync(new("packy_qwen", "问题")));
        Assert.Equal((PackyErrorCodes.InvalidResponse, false), (exception.Code, exception.Retryable));
        Assert.Equal("PackyAPI 回答未完成（max_output_tokens），已放弃。", exception.Message);
    }

    [Theory]
    [InlineData("max_output_tokens\n伪造日志行")]
    [InlineData("max_output_tokens\n")]
    [InlineData("上游说明")]
    public async Task IncompleteReasonThatIsNotATokenIsNeverEchoed(string reason)
    {
        var response = Response("incomplete", "hy4", Message("写到一半"));
        response["incomplete_details"] = new JsonObject { ["reason"] = reason };
        var exception = await Assert.ThrowsAsync<PackyProviderException>(() =>
            Create(new FakeHandler(_ => Sse(response, terminal: "response.incomplete")), "packy_yuanbao").AskAsync(new("packy_yuanbao", "问题")));
        Assert.Equal("PackyAPI 回答未完成（输出上限或内容过滤），已放弃。", exception.Message);
    }

    [Fact]
    public async Task FailedResponseIsRetryable()
    {
        var response = Response("failed", "qwen3.8-flash");
        var exception = await Assert.ThrowsAsync<PackyProviderException>(() =>
            Create(new FakeHandler(_ => Sse(response, terminal: "response.failed")), "packy_qwen").AskAsync(new("packy_qwen", "问题")));
        Assert.Equal((PackyErrorCodes.RequestFailed, true), (exception.Code, exception.Retryable));
    }

    [Fact]
    public async Task ErrorEventInTheStreamIsRetryable()
    {
        const string stream = "event: error\ndata: {\"type\":\"error\",\"code\":\"server_error\",\"message\":\"secret upstream text\"}\n\n";
        var exception = await Assert.ThrowsAsync<PackyProviderException>(() =>
            Create(new FakeHandler(_ => Raw(stream)), "packy_qwen").AskAsync(new("packy_qwen", "问题")));
        Assert.Equal((PackyErrorCodes.RequestFailed, true), (exception.Code, exception.Retryable));
        Assert.DoesNotContain("secret", exception.Message);
    }

    [Fact]
    public async Task StreamEndingBeforeTheTerminalEventIsRetryable()
    {
        const string stream = "data: {\"type\":\"response.created\",\"response\":{\"status\":\"in_progress\",\"output\":[]}}\n\n" +
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"半\"}\n\n";
        var exception = await Assert.ThrowsAsync<PackyProviderException>(() =>
            Create(new FakeHandler(_ => Raw(stream)), "packy_qwen").AskAsync(new("packy_qwen", "问题")));
        Assert.Equal((PackyErrorCodes.RequestFailed, true), (exception.Code, exception.Retryable));
    }

    [Fact]
    public async Task LeakedToolMarkupIsAnIncompleteAnswer()
    {
        var response = Response("completed", "kimi-k3", SearchCall("问题"),
            Message("<|open|>tools<|sep|><|open|>call tool=\"web_search\" index=\"1\"<|sep|><|close|>tools<|sep|>"));
        var exception = await Assert.ThrowsAsync<PackyProviderException>(() =>
            Create(new FakeHandler(_ => Sse(response)), "packy_qwen").AskAsync(new("packy_qwen", "问题")));
        Assert.Equal((PackyErrorCodes.IncompleteAnswer, true), (exception.Code, exception.Retryable));
    }

    [Fact]
    public async Task MoreThanOneHundredSourcesAreTruncated()
    {
        var sources = Enumerable.Range(1, 105).Select(i => Source($"https://site{i}.example.com/")).ToArray();
        var response = Response("completed", "glm-5.2", SearchCall("问题", sources), Message("回答"));
        var result = await Create(new FakeHandler(_ => Sse(response)), "packy_glm").AskAsync(new("packy_glm", "问题"));

        Assert.Equal(100, result.Citations.Count);
        var quality = result.ResultQuality!.Citations;
        Assert.Equal((105, 100, 5, "partial"), (quality.ReceivedCount, quality.RetainedCount, quality.TruncatedCount, quality.Completeness));
        Assert.Contains("SOURCE_LIMIT", quality.Reasons);
    }

    [Fact]
    public async Task KimiDropsTheSearchEchoAndKeepsTitledSources()
    {
        var handler = new FakeHandler(_ => Json(KimiMessage(withAnswer: true)));
        var result = await Create(handler, "packy_kimi").AskAsync(new("packy_kimi", "深圳有哪些靠谱的工业物联网网关厂商？"));

        Assert.Equal(("packy_kimi", "kimi", "packy-anthropic-v1", "kimi-k3"), (result.Provider, result.Engine, result.ParserVersion, result.ModelVersion));
        Assert.Equal("以下几家是深圳本地的代表厂商。\n\n**1. 宏电**", result.AnswerText);
        Assert.DoesNotContain("Search results for query", result.AnswerText);
        Assert.Equal(["工业物联网网关 深圳 厂商 推荐"], result.WebQueries);
        Assert.Equal([("https://hongdian.com/", "宏电官网"), ("https://bliiot.cn/", "钡铼技术")], result.Citations.Select(c => (c.Url, c.Title)));

        var request = Assert.Single(handler.Requests);
        Assert.Equal((MessagesUrl, Credential, "2023-06-01", $"Bearer {Credential}"),
            (request.Uri, request.ApiKey, request.AnthropicVersion, request.Authorization));
        using var body = JsonDocument.Parse(request.Body);
        var tool = body.RootElement.GetProperty("tools")[0];
        Assert.Equal(("web_search_20250305", 1), (tool.GetProperty("type").GetString(), tool.GetProperty("max_uses").GetInt32()));
        Assert.Equal("disabled", body.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.False(body.RootElement.TryGetProperty("stream", out _));
        Assert.Equal(16000, body.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.False(body.RootElement.TryGetProperty("max_tool_calls", out _));

        var raw = result.RawOutput.GetRawText();
        Assert.DoesNotContain("ENC-FIXTURE", raw);
        Assert.DoesNotContain("THINKING-FIXTURE", raw);
    }

    [Fact]
    public async Task KimiSearchWithoutAnAnswerIsRetried()
    {
        var exception = await Assert.ThrowsAsync<PackyProviderException>(() =>
            Create(new FakeHandler(_ => Json(KimiMessage(withAnswer: false))), "packy_kimi").AskAsync(new("packy_kimi", "问题")));
        Assert.Equal((PackyErrorCodes.IncompleteAnswer, true), (exception.Code, exception.Retryable));
    }

    [Fact]
    public async Task KimiProgressNoteInsteadOfAnAnswerIsRetried()
    {
        // Live 2026-10-29: the model announced another search round, which max_uses=1 does not allow, and ended the turn.
        var message = KimiMessage(withAnswer: true, answer: "我再搜索一些更具体的型号和实测案例信息。");
        var exception = await Assert.ThrowsAsync<PackyProviderException>(() =>
            Create(new FakeHandler(_ => Json(message)), "packy_kimi").AskAsync(new("packy_kimi", "问题")));
        Assert.Equal((PackyErrorCodes.IncompleteAnswer, true), (exception.Code, exception.Retryable));
    }

    [Fact]
    public async Task KimiMaxTokensFailsTerminally()
    {
        var message = KimiMessage(withAnswer: true).Replace("\"end_turn\"", "\"max_tokens\"", StringComparison.Ordinal);
        var exception = await Assert.ThrowsAsync<PackyProviderException>(() =>
            Create(new FakeHandler(_ => Json(message)), "packy_kimi").AskAsync(new("packy_kimi", "问题")));
        Assert.Equal((PackyErrorCodes.InvalidResponse, false), (exception.Code, exception.Retryable));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "{\"error\":{\"type\":\"packy_api_error\",\"code\":\"invalid_token\"}}", PackyErrorCodes.AuthFailed, false, null)]
    [InlineData(HttpStatusCode.PaymentRequired, "{\"error\":{\"type\":\"packy_api_error\"}}", PackyErrorCodes.InsufficientBalance, true, 10)]
    [InlineData(HttpStatusCode.TooManyRequests, "{}", PackyErrorCodes.RateLimited, true, null)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{\"error\":{\"type\":\"packy_api_error\",\"code\":\"model_not_found\",\"message\":\"分组 x 下模型 y 无可用渠道\"}}", PackyErrorCodes.InvalidRequest, false, null)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{\"error\":{\"type\":\"packy_api_error\"}}", PackyErrorCodes.RateLimited, true, null)]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":{\"type\":\"packy_invalid_request_error\",\"code\":\"400002\"}}", PackyErrorCodes.InvalidRequest, false, null)]
    [InlineData(HttpStatusCode.InternalServerError, "{\"error\":{\"type\":\"packy_fastapi_error\",\"code\":-1,\"message\":\"Unknown Error. TraceId: x\"}}", PackyErrorCodes.RequestFailed, true, null)]
    [InlineData((HttpStatusCode)524, "{\"error\":{\"type\":\"invalid_request_error\"}}", PackyErrorCodes.RequestFailed, true, null)]
    public async Task HttpFailuresAreMapped(HttpStatusCode status, string body, string code, bool retryable, int? defer)
    {
        var exception = await Assert.ThrowsAsync<PackyProviderException>(() =>
            Create(new FakeHandler(_ => Status(status, body)), "packy_qwen").AskAsync(new("packy_qwen", "问题")));
        Assert.Equal((code, retryable, defer), (exception.Code, exception.Retryable, exception.DeferMinutes));
        Assert.DoesNotContain("无可用渠道", exception.Message);
        Assert.DoesNotContain("TraceId", exception.Message);
    }

    [Fact]
    public async Task ConfiguredTimeoutIsRetryable()
    {
        var handler = new FakeHandler(async (_, ct) => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return Json("{}"); });
        var exception = await Assert.ThrowsAsync<PackyProviderException>(() =>
            Create(handler, "packy_kimi", Configuration("packy_kimi", timeoutSeconds: 1)).AskAsync(new("packy_kimi", "问题")));
        Assert.Equal((PackyErrorCodes.Timeout, true), (exception.Code, exception.Retryable));
    }

    [Fact]
    public async Task MissingConfigurationNeverCallsPackyApi()
    {
        var handler = new FakeHandler(_ => Sse(QwenCompleted()));
        var exception = await Assert.ThrowsAsync<PackyProviderException>(() =>
            Create(handler, "packy_glm", configuration: null).AskAsync(new("packy_glm", "问题")));
        Assert.Equal((PackyErrorCodes.NotConfigured, false), (exception.Code, exception.Retryable));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("deepseek_api")]
    [InlineData("packy")]
    public async Task NonPackyProviderIsRejectedBeforeAnyCall(string provider)
    {
        var handler = new FakeHandler(_ => Sse(QwenCompleted()));
        var exception = await Assert.ThrowsAsync<PackyProviderException>(() =>
            Create(handler, "packy_qwen").AskAsync(new(provider, "问题")));
        Assert.Equal(PackyErrorCodes.InvalidRequest, exception.Code);
        Assert.Empty(handler.Requests);
    }

    // Local baseline (jogeoai answer_provider_settings before 047) = PackyDefaults.Options: web_search, thinking disabled,
    // GLM reasoningEffort low, Kimi max 1 search, Yuanbao max 3 searches.
    private static readonly Dictionary<string, string> BaselineBodies = new()
    {
        ["packy_qwen"] = """{"model":"qwen3.8-flash","input":"baseline prompt","stream":true,"max_output_tokens":16000,"tools":[{"type":"web_search"}],"enable_thinking":false}""",
        ["packy_glm"] = """{"model":"glm-5.3-flash","input":"baseline prompt","stream":true,"max_output_tokens":16000,"tools":[{"type":"web_search"}],"reasoning":{"effort":"low"}}""",
        ["packy_kimi"] = """{"model":"kimi-k3","max_tokens":16000,"thinking":{"type":"disabled"},"messages":[{"role":"user","content":"baseline prompt"}],"tools":[{"type":"web_search_20250305","name":"web_search","max_uses":1}]}""",
        ["packy_yuanbao"] = """{"model":"hy4","input":"baseline prompt","stream":true,"max_output_tokens":32000,"tools":[{"type":"web_search"}],"max_tool_calls":3,"thinking":{"type":"disabled"}}""",
    };

    private static FakeHandler SuccessHandler(string provider) => provider == "packy_kimi"
        ? new FakeHandler(_ => Json(KimiMessage(withAnswer: true)))
        : new FakeHandler(_ => Sse(QwenCompleted(model: PackyDefaults.Model(provider))));

    [Theory]
    [InlineData("packy_qwen", AnswerMode.Fast)]
    [InlineData("packy_glm", AnswerMode.Fast)]
    [InlineData("packy_kimi", AnswerMode.Fast)]
    [InlineData("packy_yuanbao", AnswerMode.Deep)]
    public async Task DefaultAnswerModeSendsTheBaselineBodyByteForByte(string provider, AnswerMode expectedDefault)
    {
        var definition = AnswerProviderDefinition.Packy.Single(d => d.Provider == provider);
        Assert.Equal(expectedDefault, definition.DefaultAnswerMode);

        var withoutMode = SuccessHandler(provider);
        await Create(withoutMode, provider).AskAsync(new(provider, "baseline prompt"));
        var withDefault = SuccessHandler(provider);
        await Create(withDefault, provider).AskAsync(new(provider, "baseline prompt", AnswerMode: AnswerModeRules.ToStorage(definition.DefaultAnswerMode)));

        Assert.Equal(BaselineBodies[provider], Assert.Single(withoutMode.Requests).Body);
        Assert.Equal(BaselineBodies[provider], Assert.Single(withDefault.Requests).Body);
    }

    [Theory]
    [InlineData("packy_qwen", "\"enable_thinking\":false", "\"enable_thinking\":true")]
    [InlineData("packy_kimi", "\"thinking\":{\"type\":\"disabled\"}", "\"thinking\":{\"type\":\"enabled\"}")]
    public async Task DeepModeEnablesThinkingAndChangesNothingElse(string provider, string baselineField, string deepField)
    {
        var handler = SuccessHandler(provider);
        await Create(handler, provider).AskAsync(new(provider, "baseline prompt", AnswerMode: "deep"));
        Assert.Equal(BaselineBodies[provider].Replace(baselineField, deepField), Assert.Single(handler.Requests).Body);
    }

    [Theory]
    [InlineData("packy_qwen")]
    [InlineData("packy_glm")]
    [InlineData("packy_kimi")]
    public async Task FastModeOverridesAChangedGlobalThinkingSetting(string provider)
    {
        var configuration = Configuration(provider) with
        {
            Options = PackyDefaults.Options(provider) with
            {
                Thinking = AnswerProviderOptions.ThinkingEnabled,
                ReasoningEffort = provider == "packy_glm" ? "high" : null,
            },
        };
        var handler = SuccessHandler(provider);
        await Create(handler, provider, configuration).AskAsync(new(provider, "baseline prompt", AnswerMode: "fast"));
        Assert.Equal(BaselineBodies[provider], Assert.Single(handler.Requests).Body);

        // Without a mode (admin diagnostics) the stored global setting is sent unchanged.
        var diagnostic = SuccessHandler(provider);
        await Create(diagnostic, provider, configuration).AskAsync(new(provider, "baseline prompt"));
        Assert.NotEqual(BaselineBodies[provider], Assert.Single(diagnostic.Requests).Body);
    }

    [Theory]
    [InlineData("packy_glm", "deep")]
    [InlineData("packy_yuanbao", "fast")]
    [InlineData("packy_qwen", "turbo")]
    public async Task UnsupportedAnswerModeIsRejectedBeforeAnyCall(string provider, string mode)
    {
        var handler = SuccessHandler(provider);
        var exception = await Assert.ThrowsAsync<PackyProviderException>(() =>
            Create(handler, provider).AskAsync(new(provider, "问题", AnswerMode: mode)));
        Assert.Equal((PackyErrorCodes.InvalidRequest, false), (exception.Code, exception.Retryable));
        Assert.DoesNotContain("turbo", exception.Message);
        Assert.Empty(handler.Requests);
    }

    private static JsonObject QwenCompleted(string model = "qwen3.8-flash") => Response("completed", model,
        Reasoning(),
        SearchCall("深圳 工业物联网网关 厂商 推荐", Source("https://www.hongdian.com/"), Source("https://mp.ofweek.com/iot/a1")),
        SearchCall("计讯物联 深圳 工业网关 公司", Source("https://www.top-iot.com/"), Source("https://www.hongdian.com/")),
        Message("## 深圳工业网关厂商\n\n1. **宏电**：老牌厂商。"),
        usage: new JsonObject { ["input_tokens"] = 13368, ["output_tokens"] = 2707,
            ["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 1449 } });

    private static JsonObject Response(string status, string model, params JsonNode[] output) => Response(status, model, output, null);

    private static JsonObject Response(string status, string model, JsonNode first, JsonNode second, JsonNode third, JsonNode fourth,
        JsonObject usage) => Response(status, model, [first, second, third, fourth], usage);

    private static JsonObject Response(string status, string model, JsonNode[] output, JsonObject? usage) => new()
    {
        ["id"] = "resp_fixture",
        ["object"] = "response",
        ["model"] = model,
        ["status"] = status,
        ["output"] = new JsonArray(output.Select(node => node.DeepClone()).ToArray()),
        ["usage"] = usage ?? new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 5 },
    };

    private static JsonObject Reasoning() => new()
    {
        ["type"] = "reasoning",
        ["summary"] = new JsonArray(new JsonObject { ["type"] = "summary_text", ["text"] = "REASONING-FIXTURE" }),
        ["encrypted_content"] = "ENC-FIXTURE",
    };

    private static JsonObject SearchCall(string query, params JsonObject[] sources) => new()
    {
        ["type"] = "web_search_call",
        ["status"] = "completed",
        ["action"] = new JsonObject { ["type"] = "search", ["query"] = query, ["sources"] = new JsonArray(sources) },
    };

    private static JsonObject Source(string url) => new() { ["type"] = "url", ["url"] = url };

    private static JsonObject Citation(string url, string title) => new()
    {
        ["type"] = "url_citation", ["url"] = url, ["title"] = title, ["logo_url"] = "https://logo.example.com/x?x-signature=SIG",
    };

    private static JsonObject Message(string text, params JsonObject[] annotations) => new()
    {
        ["type"] = "message",
        ["role"] = "assistant",
        ["content"] = new JsonArray(new JsonObject
        {
            ["type"] = "output_text", ["text"] = text, ["annotations"] = new JsonArray(annotations),
        }),
    };

    // Shape of probe-kimioff-k3-anthropic-once.json: echo text, server search, results, thinking, answer.
    private static string KimiMessage(bool withAnswer, string answer = "以下几家是深圳本地的代表厂商。\n\n**1. 宏电**")
    {
        var content = new JsonArray(
            new JsonObject { ["type"] = "text", ["text"] = "Search results for query: 工业物联网网关 深圳 厂商 推荐" },
            new JsonObject
            {
                ["type"] = "server_tool_use", ["id"] = "srvtoolu_1", ["name"] = "web_search",
                ["input"] = new JsonObject { ["query"] = "工业物联网网关 深圳 厂商 推荐" },
            },
            new JsonObject
            {
                ["type"] = "web_search_tool_result", ["tool_use_id"] = "srvtoolu_1",
                ["content"] = new JsonArray(
                    new JsonObject { ["type"] = "web_search_result", ["url"] = "https://www.hongdian.com/", ["title"] = "宏电官网", ["encrypted_content"] = "ENC-FIXTURE" },
                    new JsonObject { ["type"] = "web_search_result", ["url"] = "https://bliiot.cn/", ["title"] = "钡铼技术", ["page_age"] = "2026-10-18" }),
            },
            new JsonObject { ["type"] = "thinking", ["thinking"] = "THINKING-FIXTURE", ["signature"] = "SIG" });
        if (withAnswer) content.Add(new JsonObject { ["type"] = "text", ["text"] = answer });
        return new JsonObject
        {
            ["id"] = "msg_fixture", ["type"] = "message", ["role"] = "assistant", ["model"] = "kimi-k3", ["content"] = content,
            ["stop_reason"] = "end_turn", ["usage"] = new JsonObject { ["input_tokens"] = 143, ["output_tokens"] = 1430 },
        }.ToJsonString();
    }

    private static HttpResponseMessage Sse(JsonObject response, string terminal = "response.completed")
    {
        var builder = new StringBuilder();
        builder.Append("event: response.created\ndata: {\"type\":\"response.created\",\"response\":{\"status\":\"in_progress\",\"output\":[]}}\n\n");
        builder.Append("event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"delta\":\"x\"}\n\n");
        builder.Append($"event: {terminal}\ndata: ").Append(new JsonObject { ["type"] = terminal, ["response"] = response.DeepClone() }.ToJsonString())
            .Append("\n\ndata: [DONE]\n\n");
        return Raw(builder.ToString());
    }

    private static HttpResponseMessage Raw(string stream) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(stream, Encoding.UTF8, "text/event-stream"),
    };

    private static HttpResponseMessage Json(string body) => Status(HttpStatusCode.OK, body);

    private static HttpResponseMessage Status(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static PackyAnswerAdapter Create(FakeHandler handler, string provider) => Create(handler, provider, Configuration(provider));

    private static PackyAnswerAdapter Create(FakeHandler handler, string provider, AnswerProviderConfiguration? configuration) =>
        new(new SingleClientFactory(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }),
            new FakeSettings(provider, configuration), NullLogger<PackyAnswerAdapter>.Instance);

    private static AnswerProviderConfiguration Configuration(string provider, int timeoutSeconds = 30) =>
        new(provider, provider == "packy_kimi" ? MessagesUrl : ResponsesUrl, PackyDefaults.Model(provider), Credential, timeoutSeconds,
            PackyDefaults.Options(provider));

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal("packy", name);
            return client;
        }
    }

    private sealed class FakeSettings(string expectedProvider, AnswerProviderConfiguration? configuration) : IAnswerProviderSettingsStore
    {
        public Task<AnswerProviderSettingsView?> GetAsync(string provider, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<AnswerProviderConfiguration?> GetConfigurationAsync(string provider, CancellationToken cancellationToken = default)
        {
            Assert.Equal(expectedProvider, provider);
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

public sealed class PackyProviderSettingsTests
{
    [Theory]
    [InlineData("packy_qwen")]
    [InlineData("packy_glm")]
    [InlineData("packy_kimi")]
    [InlineData("packy_yuanbao")]
    public void PackyProvidersAreRegisteredPinnedApiProviders(string provider)
    {
        var definition = AnswerProviderDefinition.TryGet(provider)!;
        Assert.Contains(definition, AnswerProviderDefinition.All);
        Assert.Equal(("api", "api_response", "CN", "zh-CN", true, PackyErrorCodes.NotConfigured),
            (definition.CaptureMode, definition.EvidenceClass, definition.DefaultCountry, definition.DefaultLocale,
                definition.PinnedSurface, definition.NotConfiguredCode));
    }

    [Fact]
    public void DefaultOptionsAreNormalizedWithThinkingDisabled()
    {
        var normalized = AnswerProviderSettingsRules.Normalize("packy_glm",
            new SaveAnswerProviderSettingsRequest(PackyDefaults.ResponsesUrl, "glm-5.2", "sk-group-key", true, 240,
                new AnswerProviderOptions { Sources = [], MaxToolCalls = 5 }));
        Assert.Equal(("packy_glm", "disabled", 5, "****-key"), (normalized.Provider, normalized.Options.Thinking,
            normalized.Options.MaxToolCalls, normalized.CredentialHint));
        using var json = JsonDocument.Parse(normalized.OptionsJson);
        Assert.Equal(("web_search", 0, "disabled"), (json.RootElement.GetProperty("requestMode").GetString(),
            json.RootElement.GetProperty("sources").GetArrayLength(), json.RootElement.GetProperty("thinking").GetString()));
    }

    [Fact]
    public void YuanbaoDefaultsMatchMigration046()
    {
        Assert.Equal(("hy4", PackyDefaults.ResponsesUrl, 540, 3, "disabled", (string?)null),
            (PackyDefaults.Model("packy_yuanbao"), PackyDefaults.ApiUrl("packy_yuanbao"), PackyDefaults.Timeout("packy_yuanbao"),
                PackyDefaults.Options("packy_yuanbao").MaxToolCalls, PackyDefaults.Options("packy_yuanbao").Thinking,
                PackyDefaults.Options("packy_yuanbao").ReasoningEffort));
        var definition = AnswerProviderDefinition.PackyYuanbao;
        Assert.Equal(("tencent_yuanbao", "packy_yuanbao_call", "packy_yuanbao:"),
            (Assert.Single(definition.Engines), definition.UsageEntryType, definition.UsageReferencePrefix));
        var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "db", "migrations", "046_packy_yuanbao_provider.sql"));
        Assert.Contains("'packy_yuanbao', 'https://www.packyapi.ai/v1/responses', 'hy4'", sql);
        Assert.Contains("0, 540, '{\"requestMode\":\"web_search\",\"sources\":[],\"maxToolCalls\":3,\"thinking\":\"disabled\"}'", sql);
    }

    [Fact]
    public void GlmReasoningEffortIsNormalizedAndStored()
    {
        var normalized = AnswerProviderSettingsRules.Normalize("packy_glm",
            new SaveAnswerProviderSettingsRequest(PackyDefaults.ResponsesUrl, "glm-5.3-flash", "sk-group-key", true, 240,
                new AnswerProviderOptions { Sources = [], MaxToolCalls = 5, ReasoningEffort = " LOW " }));
        Assert.Equal(("low", "disabled"), (normalized.Options.ReasoningEffort, normalized.Options.Thinking));
        using var json = JsonDocument.Parse(normalized.OptionsJson);
        Assert.Equal("low", json.RootElement.GetProperty("reasoningEffort").GetString());
    }

    [Theory]
    [InlineData("packy_kimi", 2, null, null, "options.maxToolCalls 必须为 1（Kimi 渠道只执行一轮服务端搜索）。")]
    [InlineData("packy_qwen", 5, "doubao", null, "options.sources 必须为空数组（PackyAPI web_search 不支持指定搜索源）。")]
    [InlineData("packy_qwen", 5, null, "low", "options.reasoningEffort 必须为空（PackyAPI 只支持 thinking 开关）。")]
    [InlineData("packy_glm", 5, null, "max", "options.reasoningEffort 必须为空或 minimal / low / medium / high。")]
    public void InvalidPackyOptionsAreRejected(string provider, int maxToolCalls, string? source, string? effort, string message)
    {
        var options = new AnswerProviderOptions
        {
            Sources = source is null ? [] : [source], MaxToolCalls = maxToolCalls, ReasoningEffort = effort,
        };
        var exception = Assert.Throws<CollectorStoreException>(() => AnswerProviderSettingsRules.NormalizeOptions(
            AnswerProviderDefinition.TryGet(provider)!, options));
        Assert.Equal((AnswerProviderSettingsRules.InvalidSettings, message), (exception.Code, exception.Message));
    }

    [Fact]
    public void DeepSeekAndDoubaoMessagesAreUnchanged()
    {
        var deepSeek = Assert.Throws<CollectorStoreException>(() => AnswerProviderSettingsRules.NormalizeOptions(
            AnswerProviderDefinition.DeepSeekApi, new AnswerProviderOptions { Sources = ["doubao"] }));
        Assert.Equal("options.sources 必须为空数组（DeepSeek web_search 不支持指定搜索源）。", deepSeek.Message);
        var doubao = Assert.Throws<CollectorStoreException>(() => AnswerProviderSettingsRules.NormalizeOptions(
            AnswerProviderDefinition.DoubaoApi, new AnswerProviderOptions { Thinking = "disabled" }));
        Assert.Equal("options.thinking 仅适用于 DeepSeek，豆包请使用 reasoningEffort。", doubao.Message);
    }

    [Theory]
    [InlineData("qwen", "packy_qwen")]
    [InlineData(" GLM ", "packy_glm")]
    [InlineData("kimi", "packy_kimi")]
    [InlineData(" Yuanbao ", "packy_yuanbao")]
    [InlineData("doubao", null)]
    public void ControllerChannelsMapToProviders(string channel, string? provider) =>
        Assert.Equal(provider, Jogeoai.Api.Controllers.PackyProviderController.ResolveProvider(channel));

    [Fact]
    public void PackyErrorCodesAreWhitelistedWithChineseMessages()
    {
        foreach (var code in PackyErrorCodes.All)
        {
            Assert.Equal(code, CloroVisibilityStore.SafeErrorCode(code));
            Assert.StartsWith("PackyAPI", CloroVisibilityStore.SafeErrorMessage(code));
        }
    }
}
