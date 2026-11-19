using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;

namespace Jogeoai.Tests;

public sealed class ArticleGenerationSchemaTests
{
    [Fact]
    public async Task SystemPromptDefinesEveryFieldTypeWithoutChangingGatewayMode()
    {
        var factory = new RecordingFactory(Envelope(ValidPayload()));
        await Generate(factory);
        using var request = JsonDocument.Parse(Assert.Single(factory.Requests));
        Assert.Equal("json_object", request.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        var system = request.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
        foreach (var field in new[] { "title", "bodyMarkdown" }) Assert.Contains($"{field}: string", system);
        foreach (var field in new[] { "summary", "seoTitle", "seoDescription", "disclosure" })
            Assert.Contains($"{field}: string or null", system);
        foreach (var field in new[] { "keywords", "evidenceIds", "uncertainties" })
            Assert.Contains($"{field}: array of strings or null", system);
        Assert.Contains("answeredQuestion: boolean", system);
        Assert.Contains("unquoted true or false", system);
        Assert.Contains("not the question text", system);
        Assert.Contains("truthfully", system);
    }

    [Fact]
    public async Task StringAnsweredQuestionReceivesBooleanFeedbackThenCorrectedTruePasses()
    {
        var invalid = ValidPayload();
        invalid["answeredQuestion"] = "private incorrect answer text";
        var factory = new RecordingFactory(Envelope(invalid), Envelope(ValidPayload()));

        var result = await Generate(factory);

        Assert.True(result.AnsweredQuestion);
        Assert.Equal(2, factory.Requests.Count);
        AssertSchemaRepair(factory.Requests[1], "$.answeredQuestion", "boolean");
        Assert.DoesNotContain("private incorrect answer text", factory.Requests[1]);
    }

    [Fact]
    public async Task PersistentStringBooleanIsNotCoercedAndStopsAfterThreeRequests()
    {
        var invalid = ValidPayload();
        invalid["answeredQuestion"] = "true";
        var factory = new RecordingFactory(Envelope(invalid), Envelope(invalid), Envelope(invalid));

        var error = await Assert.ThrowsAsync<CollectorStoreException>(() => Generate(factory));

        Assert.Equal("ARTICLE_GENERATION_SCHEMA_INVALID", error.Code);
        Assert.Equal(502, error.StatusCode);
        Assert.Null(error.InnerException);
        Assert.Equal(3, factory.Requests.Count);
        foreach (var request in factory.Requests.Skip(1)) AssertSchemaRepair(request, "$.answeredQuestion", "boolean");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrNullAnsweredQuestionReceivesBooleanFeedback(bool omit)
    {
        var invalid = ValidPayload();
        if (omit) invalid.Remove("answeredQuestion");
        else invalid["answeredQuestion"] = null;
        var factory = new RecordingFactory(Envelope(invalid), Envelope(ValidPayload()));

        Assert.True((await Generate(factory)).AnsweredQuestion);
        AssertSchemaRepair(factory.Requests[1], "$.answeredQuestion", "boolean");
    }

    [Theory]
    [InlineData("title", "123", "string")]
    [InlineData("bodyMarkdown", "false", "string")]
    [InlineData("summary", "123", "string or null")]
    [InlineData("seoTitle", "{}", "string or null")]
    [InlineData("seoDescription", "[]", "string or null")]
    [InlineData("keywords", "[\"known text\",false]", "array of strings or null")]
    [InlineData("evidenceIds", "false", "array of strings or null")]
    [InlineData("uncertainties", "\"not an array\"", "array of strings or null")]
    [InlineData("disclosure", "{}", "string or null")]
    [InlineData("answeredQuestion", "1", "boolean")]
    public async Task OtherTypeFailuresReceiveOnlyCanonicalFieldFeedback(string field, string valueJson, string expectedType)
    {
        var invalid = ValidPayload();
        invalid[field] = JsonNode.Parse(valueJson);
        var factory = new RecordingFactory(Envelope(invalid), Envelope(ValidPayload()));

        Assert.True((await Generate(factory)).AnsweredQuestion);
        AssertSchemaRepair(factory.Requests[1], "$." + field, expectedType);
    }

    [Fact]
    public async Task CaseInsensitiveDtoFieldsStillMapToCanonicalFeedback()
    {
        var invalid = ValidPayload();
        invalid.Remove("answeredQuestion");
        invalid["AnsweredQuestion"] = "private value";
        var factory = new RecordingFactory(Envelope(invalid), Envelope(ValidPayload()));

        Assert.True((await Generate(factory)).AnsweredQuestion);
        AssertSchemaRepair(factory.Requests[1], "$.answeredQuestion", "boolean");
    }

    [Theory]
    [InlineData("answeredQuestion.privateMarker")]
    [InlineData("keywords[0].privateMarker")]
    [InlineData("privateMarker")]
    public async Task UnknownPropertyNamesAndValuesNeverBecomeRepairDiagnostics(string propertyName)
    {
        const string secret = "rk-thisIsASyntheticSecret123456";
        var invalid = ValidPayload();
        invalid[propertyName] = "private value " + secret;
        var factory = new RecordingFactory(Envelope(invalid), Envelope(ValidPayload()));

        Assert.True((await Generate(factory)).AnsweredQuestion);
        AssertSchemaRepair(factory.Requests[1], "$", "JSON object with only the documented fields and types");
        Assert.DoesNotContain("privateMarker", factory.Requests[1]);
        Assert.DoesNotContain("private value", factory.Requests[1]);
        Assert.DoesNotContain(secret, factory.Requests[1]);
    }

    [Fact]
    public async Task InvalidJsonIsOmittedInsteadOfEchoingPrivateResponseText()
    {
        var factory = new RecordingFactory("not-json-private-response-value", Envelope(ValidPayload()));

        Assert.True((await Generate(factory)).AnsweredQuestion);
        AssertSchemaRepair(factory.Requests[1], "$", "JSON object with only the documented fields and types");
        Assert.DoesNotContain("not-json-private-response-value", factory.Requests[1]);
    }

    [Fact]
    public async Task OptionalNullFieldsRemainBackwardCompatible()
    {
        var payload = ValidPayload();
        foreach (var name in new[] { "summary", "seoTitle", "seoDescription", "keywords", "evidenceIds", "uncertainties", "disclosure" })
            payload[name] = null;
        var factory = new RecordingFactory(Envelope(payload));

        var result = await Generate(factory);

        Assert.True(result.AnsweredQuestion);
        Assert.Null(result.Summary);
        Assert.Empty(result.Keywords);
        Assert.Empty(result.EvidenceIds);
        Assert.Empty(result.Uncertainties);
        Assert.Empty(result.Disclosure);
        Assert.Single(factory.Requests);
    }

    [Fact]
    public async Task FalseBooleanStillFailsSemanticValidationRatherThanBeingChangedToTrue()
    {
        var payload = ValidPayload();
        payload["answeredQuestion"] = false;
        var factory = new RecordingFactory(Envelope(payload), Envelope(payload), Envelope(payload));

        var error = await Assert.ThrowsAsync<CollectorStoreException>(() => Generate(factory));

        Assert.Equal("ARTICLE_GENERATION_SCHEMA_INVALID", error.Code);
        Assert.Equal("模型返回的文章没有确认回答主问题。", error.Message);
        Assert.Equal(3, factory.Requests.Count);
    }

    private static void AssertSchemaRepair(string request, string path, string expectedType)
    {
        using var body = JsonDocument.Parse(request);
        using var repair = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
        Assert.Equal("ARTICLE_GENERATION_SCHEMA_INVALID", repair.RootElement.GetProperty("validationError").GetString());
        var detail = repair.RootElement.GetProperty("schemaIssue");
        Assert.Equal(path, detail.GetProperty("path").GetString());
        Assert.Equal(expectedType, detail.GetProperty("expectedType").GetString());
        Assert.Equal(JsonValueKind.Null, repair.RootElement.GetProperty("previousOutput").ValueKind);
        Assert.True(repair.RootElement.GetProperty("originalRequest").TryGetProperty("MAIN_QUESTION", out _));
        Assert.Contains("1500–2200", repair.RootElement.GetProperty("requiredLength").GetString());
    }

    private static JsonObject ValidPayload() => new()
    {
        ["title"] = "合成文章标题", ["summary"] = "合成摘要", ["bodyMarkdown"] = new string('字', 1600),
        ["seoTitle"] = null, ["seoDescription"] = null, ["keywords"] = new JsonArray(),
        ["answeredQuestion"] = true, ["evidenceIds"] = new JsonArray(),
        ["uncertainties"] = new JsonArray(), ["disclosure"] = "合成内容"
    };

    private static string Envelope(JsonObject payload) => JsonSerializer.Serialize(new
    {
        choices = new[] { new { message = new { content = payload.ToJsonString() }, finish_reason = "stop" } }
    });

    private static Task<ArticleGenerationResult> Generate(RecordingFactory factory)
        => new ArticleGenerationProvider(factory).GenerateAsync(new ArticleGenerationWorkItem(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                "合成主问题", "[]", null, null, null, null, "zh-CN", "answer_guide", "medium", "professional",
                null, [], null, null, null, false, 1, "test", 100),
            new ResearchProviderConfiguration(ArticleGenerationProvider.ProviderName, "ignored", "https://llm.test/v1/chat/completions", "synthetic-credential", false, 30, 1),
            [new KnowledgeEvidenceView("known", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "合成资料", "合成事实", "site", "test.md", null, 1, null, null)], []);

    private sealed class RecordingFactory(params string[] responses) : IHttpClientFactory
    {
        private readonly string[] responseBodies = responses;
        internal List<string> Requests { get; } = [];
        public HttpClient CreateClient(string name) => new(new Handler(this));

        private sealed class Handler(RecordingFactory owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var index = owner.Requests.Count;
                owner.Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
                Assert.True(index < owner.responseBodies.Length, "Provider exceeded the configured synthetic response count.");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(owner.responseBodies[index]), RequestMessage = request
                };
            }
        }
    }
}
