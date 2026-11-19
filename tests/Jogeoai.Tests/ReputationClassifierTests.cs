using System.Net;
using System.Text;
using System.Text.Json;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;

namespace Jogeoai.Tests;

/// <summary>Luna 情感分类器适配器：全部使用假 HTTP 处理器，不访问任何真实网关、不消耗额度。</summary>
public sealed class ReputationClassifierTests
{
    private const string Answer = "Acme 的售后服务响应很快，价格偏高，但整体性价比不错。";
    private static readonly ResearchProviderConfiguration Configuration =
        new("openai-compatible", "attacker-model", "https://llm.test/v1/chat/completions", "secret-token-1234567890", false, 30, 1);
    private static readonly ReputationClassificationInput Input =
        new("Acme", ["艾克米"], ["acme.test"], Answer, ["Acme 的售后服务响应很快"], 0.6);

    private static string Payload(string state, double confidence, string? evidence, string terms = "[]")
        => JsonSerializer.Serialize(new { state, confidence, rationaleCode = "PRAISE", evidence }) is var json
            ? json[..^1] + ",\"terms\":" + terms + "}" : "";

    private static HttpResponseMessage Ok(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } }),
            Encoding.UTF8, "application/json")
    };

    [Fact]
    public void UsageTokensAreReadWhenPresentAndNullOtherwise()
    {
        Assert.Equal((1200L, 85L), LunaReputationSentimentClassifier.ReadUsage(
            "{\"choices\":[],\"usage\":{\"prompt_tokens\":1200,\"completion_tokens\":85,\"total_tokens\":1285}}"));
        Assert.Equal(((long?)null, (long?)null), LunaReputationSentimentClassifier.ReadUsage("{\"choices\":[]}"));
        Assert.Equal(((long?)null, (long?)null), LunaReputationSentimentClassifier.ReadUsage("not json"));
    }

    [Fact]
    public async Task ValidOutputIsClassifiedWithOffsetsTermsAndFixedModel()
    {
        var factory = new FakeHttpClientFactory(_ => Ok(Payload("positive", 0.92, "售后服务响应很快",
            "[{\"text\":\"售后服务响应很快\",\"polarity\":\"positive\"},{\"text\":\"价格偏高\",\"polarity\":\"negative\"},{\"text\":\"不存在的短语\",\"polarity\":\"positive\"}]")));
        var classifier = new LunaReputationSentimentClassifier(factory);

        var result = await classifier.ClassifyAsync(Input, Configuration);

        Assert.Equal(ReputationSentimentState.Positive, result.State);
        Assert.Equal(0.92, result.Confidence);
        Assert.Equal(Answer.IndexOf("售后服务响应很快", StringComparison.Ordinal), result.EvidenceStart);
        Assert.Equal(new[] { "售后服务响应很快", "价格偏高" }, result.Terms.Select(t => t.TermText));
        Assert.Equal("gpt-6-luna", result.ModelVersion);
        var request = Assert.Single(factory.Requests);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("gpt-6-luna", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("json_object", body.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Contains("<ANSWER>", body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
        Assert.DoesNotContain("secret-token", request.Body, StringComparison.Ordinal);
        Assert.Equal("Bearer secret-token-1234567890", request.Authorization);
    }

    [Fact]
    public async Task FencedJsonIsAccepted()
    {
        var factory = new FakeHttpClientFactory(_ => Ok("```json\n" + Payload("negative", 0.8, "价格偏高") + "\n```"));
        var result = await new LunaReputationSentimentClassifier(factory).ClassifyAsync(Input, Configuration);
        Assert.Equal(ReputationSentimentState.Negative, result.State);
        Assert.Single(factory.Requests);
    }

    [Fact]
    public async Task InvalidOutputIsRetriedOnceThenUnknown()
    {
        var factory = new FakeHttpClientFactory(_ => Ok("这不是 JSON"));
        var result = await new LunaReputationSentimentClassifier(factory).ClassifyAsync(Input, Configuration);
        Assert.Equal(ReputationSentimentState.Unknown, result.State);
        Assert.Equal(ReputationRules.InvalidOutput, result.RationaleCode);
        Assert.Equal(2, factory.Requests.Count);
        Assert.Contains("not a valid JSON object", factory.Requests[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidThenValidUsesTheRepairedAnswer()
    {
        var calls = 0;
        var factory = new FakeHttpClientFactory(_ => ++calls == 1 ? Ok("{\"broken\":") : Ok(Payload("neutral", 0.7, "整体性价比不错")));
        var result = await new LunaReputationSentimentClassifier(factory).ClassifyAsync(Input, Configuration);
        Assert.Equal(ReputationSentimentState.Neutral, result.State);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task LowConfidenceIsUnknown()
    {
        var factory = new FakeHttpClientFactory(_ => Ok(Payload("positive", 0.4, "售后服务响应很快")));
        var result = await new LunaReputationSentimentClassifier(factory).ClassifyAsync(Input, Configuration);
        Assert.Equal(ReputationSentimentState.Unknown, result.State);
        Assert.Equal(ReputationRules.LowConfidence, result.RationaleCode);
        Assert.Equal(0.4, result.Confidence);
    }

    [Fact]
    public async Task MentionDisagreementIsUnknownNeverNotMentioned()
    {
        var factory = new FakeHttpClientFactory(_ => Ok(Payload("not_mentioned", 0.99, null)));
        var result = await new LunaReputationSentimentClassifier(factory).ClassifyAsync(Input, Configuration);
        Assert.Equal(ReputationSentimentState.Unknown, result.State);
        Assert.Equal(ReputationRules.MentionDisagree, result.RationaleCode);
    }

    [Fact]
    public async Task EvidenceNotInAnswerIsUnknown()
    {
        var factory = new FakeHttpClientFactory(_ => Ok(Payload("positive", 0.9, "质量非常可靠")));
        var result = await new LunaReputationSentimentClassifier(factory).ClassifyAsync(Input, Configuration);
        Assert.Equal(ReputationSentimentState.Unknown, result.State);
        Assert.Equal(ReputationRules.EvidenceNotFound, result.RationaleCode);
        Assert.Empty(result.Terms);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ReputationClassifierException.ProviderConfigInvalid, false)]
    [InlineData(HttpStatusCode.Forbidden, ReputationClassifierException.ProviderConfigInvalid, false)]
    [InlineData(HttpStatusCode.TooManyRequests, ReputationClassifierException.Retryable, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ReputationClassifierException.Retryable, true)]
    [InlineData(HttpStatusCode.BadRequest, ReputationClassifierException.Rejected, false)]
    public async Task GatewayErrorsMapToStableCodesWithoutReadingTheBody(HttpStatusCode status, string code, bool retryable)
    {
        var factory = new FakeHttpClientFactory(_ => new HttpResponseMessage(status)
            { Content = new StringContent("raw provider secret body") });
        var exception = await Assert.ThrowsAsync<ReputationClassifierException>(() =>
            new LunaReputationSentimentClassifier(factory).ClassifyAsync(Input, Configuration));
        Assert.Equal(code, exception.Code);
        Assert.Equal(retryable, exception.IsRetryable);
        Assert.DoesNotContain("raw provider", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TransportFailureIsRetryable()
    {
        var factory = new FakeHttpClientFactory(_ => throw new HttpRequestException("connection refused"));
        var exception = await Assert.ThrowsAsync<ReputationClassifierException>(() =>
            new LunaReputationSentimentClassifier(factory).ClassifyAsync(Input, Configuration));
        Assert.True(exception.IsRetryable);
    }

    [Fact]
    public async Task MissingConfigurationNeverCallsTheGateway()
    {
        var factory = new FakeHttpClientFactory(_ => throw new InvalidOperationException("must not be called"));
        var exception = await Assert.ThrowsAsync<ReputationClassifierException>(() =>
            new LunaReputationSentimentClassifier(factory).ClassifyAsync(Input, Configuration with { Credential = "" }));
        Assert.Equal(ReputationClassifierException.ProviderConfigInvalid, exception.Code);
        Assert.Empty(factory.Requests);
    }

    [Fact]
    public void LongAnswersAreTruncatedInThePromptOnly()
    {
        var longAnswer = new string('长', LunaReputationSentimentClassifier.MaxAnswerCharacters + 50);
        var text = LunaReputationSentimentClassifier.BuildUserText(Input with { Answer = longAnswer });
        Assert.Contains("truncated=\"true\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain(longAnswer, text, StringComparison.Ordinal);
    }

    [Fact]
    public void TruncationNeverSplitsASurrogatePair()
    {
        // 截断点恰好落在 emoji（代理对）中间：不能留下孤立的高位代理项。
        var longAnswer = new string('长', LunaReputationSentimentClassifier.MaxAnswerCharacters - 1) + "😀" + new string('尾', 10);
        var text = LunaReputationSentimentClassifier.BuildUserText(Input with { Answer = longAnswer });
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i])) Assert.True(i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]));
            if (char.IsLowSurrogate(text[i])) Assert.True(i > 0 && char.IsHighSurrogate(text[i - 1]));
        }
        Assert.DoesNotContain("😀", text, StringComparison.Ordinal);
    }

    private sealed class FakeHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> responder) : IHttpClientFactory
    {
        public List<(string Body, string? Authorization)> Requests { get; } = [];

        public HttpClient CreateClient(string name)
        {
            Assert.Equal("research", name);
            return new HttpClient(new Handler(this, responder));
        }

        private sealed class Handler(FakeHttpClientFactory owner, Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                owner.Requests.Add((body, request.Headers.Authorization?.ToString()));
                var response = responder(request);
                response.RequestMessage = request;
                return response;
            }
        }
    }
}
