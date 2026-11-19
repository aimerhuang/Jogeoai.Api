using System.Net;
using System.Text.Json;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;

namespace Jogeoai.Tests;

public sealed class BrandResearchProviderTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static ResearchProviderConfiguration Configuration => new("openai-compatible", "gpt-6-luna", "https://llm.test/chat/completions", "test-secret", true, 30, 1);
    internal static StartBrandResearchRequest Request => new("https://example.com/", "测试品牌", 10);
    internal static ResearchContext Context(string product = "管道摄像头", string industry = "管道检测设备") => ResearchContextRules.Create(Request) with
    {
        Evidence = [new("web-1", "https://example.com/", "业务资料", $"测试品牌从事{industry}，提供{product}，可用于现场检查及产品选型。", "hash", DateTimeOffset.UtcNow)],
    };
    private static object Profile(string product = "管道摄像头", string industry = "管道检测设备") => new
    {
        sufficient = true, summary = $"测试品牌提供{product}", identity = new { value = "测试品牌", evidenceId = "web-1", quote = "测试品牌从事" + industry },
        industry = new { value = industry, evidenceId = "web-1", quote = "从事" + industry },
        products = new[] { new { value = product, evidenceId = "web-1", quote = "提供" + product } },
        vocabulary = new[] { product, "专业", "领先" }, scenarios = new[] { "现场检查" }, customers = new[] { "采购人员" },
        needs = new[] { "解决现场问题" }, decisionFactors = new[] { "适用范围" }, constraints = new[] { "价格未证实" },
    };
    private static readonly string[] EquipmentQuestions = [
        "管道摄像头检查弯管时，镜头大小怎么选？", "地下排水管破损后，管道摄像头怎样配合定位？", "管道摄像头能看清管壁的细小裂纹吗？",
        "排水管反复堵塞，管道摄像头如何检查水泥残留？", "管道摄像头推杆长度怎样匹配检测距离？", "管道摄像头的计米功能对复检有什么帮助？",
        "管道摄像头在积水条件下如何保持画面清晰？", "管道摄像头的镜头维护应注意哪些问题？", "采购管道摄像头时应怎么验收录像质量？", "管道摄像头过弯困难可能是什么原因？",
    ];
    private static object Candidate(string prompt, string product = "管道摄像头", string evidence = "web-1") => new
    { prompt, product, scenario = "现场检查", intent = "选型", reason = "对应官网产品和使用场景", evidenceIds = new[] { evidence }, tags = new[] { "选型" }, branded = false };
    private static object Candidates(string[]? prompts = null, string product = "管道摄像头") => new { candidates = (prompts ?? EquipmentQuestions).Select(p => Candidate(p, product)) };
    private static object Review(int count = 10, bool supported = true) => new
    {
        profileSupported = supported,
        decisions = Enumerable.Range(0, count).Select(i => new { index = i, accept = true, reason = "对应官网介绍的产品及现场检查需求", topicKey = $"具体任务-{i}" }),
    };
    private static Task<ResearchProviderResult> Run(Factory factory, ResearchContext? context = null, List<ResearchContext>? progress = null) =>
        new OpenAiCompatibleResearchProvider(factory) { GenerationShards = 1 }.RunAsync(Configuration, Request, context ?? Context(), (c, _) => { progress?.Add(c); return Task.CompletedTask; });

    [Fact]
    public async Task ExtractsFactsThenGeneratesAndIndependentlyReviewsTenQuestions()
    {
        var factory = new Factory(Profile(), Candidates(), Review()); var progress = new List<ResearchContext>();
        var result = await Run(factory, progress: progress);
        Assert.Equal(EquipmentQuestions, result.Suggestion.Prompts.Select(p => p.Prompt));
        Assert.All(result.Suggestion.Prompts, p => { Assert.NotEmpty(p.Reason!); Assert.Equal("web-1", Assert.Single(p.EvidenceIds!)); });
        Assert.Equal(["profiling", "profiling", "generating", "reviewing"], progress.Select(p => p.Phase));
        Assert.Equal("done", result.Research!.Phase);
        Assert.DoesNotContain("专业", result.Research.Profile!.Vocabulary);
        Assert.Equal(3, factory.Bodies.Count);
        foreach (var body in factory.Bodies)
        {
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("gpt-6-luna", doc.RootElement.GetProperty("model").GetString());
            Assert.False(doc.RootElement.TryGetProperty("web_search", out _));
            Assert.False(doc.RootElement.TryGetProperty("temperature", out _));
            Assert.Equal(2, doc.RootElement.GetProperty("messages").GetArrayLength());
        }
        Assert.Contains("独立", factory.Prompt(2));
    }

    [Theory]
    [InlineData("餐饮", "烤鱼", "聚餐时烤鱼的辣度应该如何选？")]
    [InlineData("企业软件", "工单系统", "工单系统如何分配不同门店的维修请求？")]
    [InlineData("消费品", "运动鞋", "长距离通勤的运动鞋怎样选择缓震性能？")]
    public async Task UsesDifferentIndustryVocabularyInGenerationAndReview(string industry, string product, string question)
    {
        var factory = new Factory(Profile(product, industry), Candidates([question], product), Review(1));
        var provider = new OpenAiCompatibleResearchProvider(factory) { GenerationShards = 1 };
        var result = await provider.RunAsync(Configuration, Request with { MaxPrompts = 1 }, Context(product, industry), (_, _) => Task.CompletedTask);
        Assert.Equal(question, Assert.Single(result.Suggestion.Prompts).Prompt);
        Assert.Contains(industry, factory.Prompt(1)); Assert.Contains(product, factory.Prompt(2));
        Assert.DoesNotContain("管道摄像头", factory.Prompt(1));
    }

    [Fact]
    public async Task AdjacentChineseAndEnglishBrandNamesCanMatchOfficialIdentity()
    {
        var context = Context() with { BrandName = "优联ulinktek" };
        context = context with { Evidence = [context.Evidence[0] with { Excerpt = context.Evidence[0].Excerpt.Replace("测试品牌", "优联科技 Ulinktek") }] };
        var profile = JsonSerializer.SerializeToNode(Profile(), JsonOptions)!;
        profile["identity"]!["quote"] = "优联科技 Ulinktek";
        var factory = new Factory(profile, Candidates(), Review());
        var result = await Run(factory, context);
        Assert.Equal(10, result.Suggestion.Prompts.Count);
    }

    [Fact]
    public async Task MissingEvidenceNeverGuessesFromBrandName()
    {
        var factory = new Factory();
        await Assert.ThrowsAsync<ResearchNeedsInputException>(() => Run(factory, ResearchContextRules.Create(Request)));
        Assert.Empty(factory.Bodies);
    }

    [Fact]
    public async Task KnowledgeEvidenceIsTrustedForIdentityLikeUserInputWithoutWebsite()
    {
        var item = BrandResearchContextTests.Item("公司介绍", "本公司从事管道检测设备，提供管道摄像头，可用于现场检查及产品选型。");
        var knowledge = ResearchContextRules.KnowledgeEvidence([item]);
        var id = knowledge[0].Id;
        var context = ResearchContextRules.Create(Request with { Website = null }, null, null, knowledge);
        var raw = JsonSerializer.SerializeToNode(Profile(), JsonOptions)!;
        // 原文不含品牌名：官网证据会被拒绝（见下方第三个用例），品牌知识与用户补充一样视为品牌方自述。
        raw["identity"]!["evidenceId"] = id; raw["identity"]!["quote"] = "本公司从事管道检测设备";
        raw["industry"]!["evidenceId"] = id; raw["products"]![0]!["evidenceId"] = id;
        var factory = new Factory(raw, new { candidates = EquipmentQuestions.Select(p => Candidate(p, evidence: id)) }, Review());
        var result = await new OpenAiCompatibleResearchProvider(factory) { GenerationShards = 1 }.RunAsync(Configuration, Request with { Website = null }, context,
            (_, _) => Task.CompletedTask);
        Assert.Equal(10, result.Suggestion.Prompts.Count);
        Assert.All(result.Suggestion.Prompts, p => Assert.Equal(id, Assert.Single(p.EvidenceIds!)));
        Assert.Equal(id, result.Research!.Profile!.Identity.EvidenceId);
        Assert.Contains(id, factory.Prompt(0));
        Assert.Contains("knowledge:", factory.Prompt(0));
    }

    [Theory]
    [InlineData("不存在的来源", "测试品牌从事管道检测设备")]
    [InlineData("web-1", "伪造的原文内容")]
    [InlineData("web-1", "可用于现场检查及产品选型")]
    public async Task InvalidEvidenceOrMismatchedBrandCannotBecomeVerifiedFacts(string evidenceId, string quote)
    {
        var raw = JsonSerializer.SerializeToNode(Profile(), JsonOptions)!;
        raw["identity"]!["evidenceId"] = evidenceId; raw["identity"]!["quote"] = quote;
        var factory = new Factory(raw);
        await Assert.ThrowsAsync<ResearchNeedsInputException>(() => Run(factory));
        Assert.Single(factory.Bodies);
    }

    [Fact]
    public async Task UnrelatedFabricatedAndSemanticallyRejectedCandidatesNeverPublish()
    {
        var candidates = new { candidates = new[] { Candidate("选择性价比高的供应商应注意什么？"), Candidate("管道摄像头有哪些秘密认证？", evidence: "invented") }
            .Concat(EquipmentQuestions.Select(p => Candidate(p))).Append(Candidate("管道摄像头镜头如何选才能更好过弯？")) };
        var decisions = new { profileSupported = true, decisions = Enumerable.Range(0, 11).Select(i => new
        { index = i, accept = i < 10, reason = i < 10 ? "产品与客户任务相关" : "与第一条语义重复", topicKey = $"任务-{i}" }) };
        var factory = new Factory(Profile(), candidates, decisions);
        var result = await Run(factory);
        Assert.Equal(EquipmentQuestions, result.Suggestion.Prompts.Select(p => p.Prompt));
        Assert.DoesNotContain("秘密认证", factory.Prompt(2));
        Assert.DoesNotContain("供应商应注意", factory.Prompt(2));
    }

    [Fact]
    public async Task ReusesCorrectedProfileAndPassesExclusionsToBothStages()
    {
        var first = await Run(new Factory(Profile(), Candidates(), Review()));
        var context = first.Research! with { ExcludedQuestions = [EquipmentQuestions[0]], BusinessDescription = "以排水检查为主", Feedback = [new("旧问题", "generic")] };
        var factory = new Factory(Candidates(EquipmentQuestions.Skip(1).Append("管道摄像头录像导出时如何保存检测记录？").ToArray()), Review());
        var result = await Run(factory, context);
        Assert.Equal(2, factory.Bodies.Count);
        Assert.DoesNotContain(result.Suggestion.Prompts, p => p.Prompt == EquipmentQuestions[0]);
        Assert.Contains("以排水检查为主", factory.Prompt(0)); Assert.Contains("旧问题", factory.Prompt(1));
    }

    [Fact]
    public async Task QualityShortageHasBoundedRewritesAndReturnsOnlyReviewedQuestions()
    {
        var factory = new Factory(Profile(), Candidates([EquipmentQuestions[0]]), Review(1), Candidates([]), Candidates([]));
        var result = await Run(factory);
        Assert.Equal(5, factory.Bodies.Count);
        Assert.Equal([EquipmentQuestions[0]], result.Suggestion.Prompts.Select(p => p.Prompt));
    }

    [Fact]
    public async Task ParallelGenerationBatchesSplitIntentAndAreReviewedTogether()
    {
        var factory = new Factory(Profile(), Candidates(EquipmentQuestions[..5]), Candidates(EquipmentQuestions[5..]), Review());
        var result = await new OpenAiCompatibleResearchProvider(factory).RunAsync(Configuration, Request, Context(), (_, _) => Task.CompletedTask);
        Assert.Equal(EquipmentQuestions.Order(), result.Suggestion.Prompts.Select(p => p.Prompt).Order());
        Assert.Equal(4, factory.Bodies.Count);
        var generation = new[] { factory.Prompt(1), factory.Prompt(2) };
        Assert.All(generation, p => { Assert.Contains("生成 10 条", p); Assert.DoesNotContain("reason", p); });
        // 一批只写行业推荐类，另一批写榜单清单类并负责全部品牌词。
        Assert.Single(generation, p => p.Contains("本批只负责：行业词·推荐类"));
        Assert.Single(generation, p => p.Contains("本批只负责：行业词·榜单清单类") && p.Contains("本批负责全部品牌词"));
        // 审核一次看到两批全部候选，跨批去重仍由同一审核负责。
        Assert.All(EquipmentQuestions, q => Assert.Contains(q, factory.Prompt(3)));
    }

    [Fact]
    public async Task GenerationAndReviewShareTheMonitoringQuestionRules()
    {
        var factory = new Factory(Profile(), Candidates(), Review());
        await Run(factory);
        foreach (var prompt in new[] { factory.Prompt(1), factory.Prompt(2) })
        {
            // 行业词邀请 AI 列举品牌、不写判断/科普/操作题；品牌词要有具体评价维度。
            Assert.Contains("{品类}哪个牌子好」", prompt);
            Assert.Contains("不能写成判断题、科普题、操作题或注意事项", prompt);
            Assert.Contains("不得只问「{品牌}怎么样」「{品牌}好用吗」", prompt);
        }
        Assert.DoesNotContain("应用任务、痛点", factory.Prompt(1));
        Assert.Contains("一律拒绝", factory.Prompt(2));
    }

    [Fact]
    public async Task OneFailedGenerationBatchKeepsTheOther()
    {
        var factory = new Factory(Profile(), "not json", Candidates(), Review());
        var result = await new OpenAiCompatibleResearchProvider(factory).RunAsync(Configuration, Request, Context(), (_, _) => Task.CompletedTask);
        Assert.Equal(EquipmentQuestions, result.Suggestion.Prompts.Select(p => p.Prompt));
    }

    [Fact]
    public async Task SingleRoundReturnsFirstReviewWithoutRewrites()
    {
        var factory = new Factory(Profile(), Candidates([EquipmentQuestions[0]]), Review(1));
        var result = await new OpenAiCompatibleResearchProvider(factory) { GenerationShards = 1 }
            .RunAsync(Configuration, Request with { SingleRound = true }, Context(), (_, _) => Task.CompletedTask);
        Assert.Equal(3, factory.Bodies.Count);
        Assert.Equal([EquipmentQuestions[0]], result.Suggestion.Prompts.Select(p => p.Prompt));
    }

    [Fact]
    public async Task NoReviewedQuestionFailsWithoutTemplateFallback()
    {
        var factory = new Factory(Profile(), Candidates([]), Candidates([]), Candidates([]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(factory));
        Assert.Equal(4, factory.Bodies.Count);
    }

    [Fact]
    public async Task ShortOwnerDescriptionWithoutBrandNameUsesRequestBrandAndKeepsVerifiedProducts()
    {
        const string description = "主营spa，按摩，精油按摩";
        var request = Request with { Website = null, BusinessDescription = description };
        var raw = new
        {
            // 模型因原文缺少品牌名给出 sufficient=false、并引用不存在的品牌名：品牌方资料下仍以请求品牌为准。
            sufficient = false, summary = "提供 SPA 与按摩服务",
            identity = new { value = "测试品牌", evidenceId = "user", quote = "测试品牌" },
            industry = new { value = "SPA 按摩", evidenceId = "user", quote = "主营spa" },
            products = new[]
            {
                new { value = "按摩", evidenceId = "user", quote = "按摩" },
                new { value = "精油按摩", evidenceId = "user", quote = "精油按摩" },
                new { value = "足疗", evidenceId = "user", quote = "足疗套餐" },
            },
            vocabulary = new[] { "按摩", "精油按摩" },
        };
        string[] questions = ["肩颈酸痛适合做哪种按摩？", "第一次做精油按摩需要注意什么？"];
        var factory = new Factory(raw, new { candidates = questions.Select(p => Candidate(p, "按摩", "user")) }, Review(2),
            Candidates([]), Candidates([]));
        var result = await new OpenAiCompatibleResearchProvider(factory) { GenerationShards = 1 }.RunAsync(Configuration, request, ResearchContextRules.Create(request),
            (_, _) => Task.CompletedTask);
        var profile = result.Research!.Profile!;
        Assert.Equal(("测试品牌", "user", description), (profile.Identity.Value, profile.Identity.EvidenceId, profile.Identity.Quote));
        Assert.Equal(["按摩", "精油按摩"], profile.Products.Select(p => p.Value));
        Assert.Equal(questions, result.Suggestion.Prompts.Select(p => p.Prompt));
    }
    [Fact]
    public async Task ReviewerCanRequireBusinessCorrection()
    {
        var factory = new Factory(Profile(), Candidates(), Review(supported: false));
        await Assert.ThrowsAsync<ResearchNeedsInputException>(() => Run(factory));
        Assert.Equal(3, factory.Bodies.Count);
    }
    [Fact]
    public async Task MalformedModelResponseIsNotPublished()
    {
        await Assert.ThrowsAnyAsync<JsonException>(() => Run(new Factory("not json")));
    }

    private sealed class Factory(params object[] responses) : IHttpClientFactory
    {
        public List<string> Bodies { get; } = [];
        public string Prompt(int index) { using var doc = JsonDocument.Parse(Bodies[index]); return doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!; }
        public HttpClient CreateClient(string name) => new(new Handler(this));
        private sealed class Handler(Factory owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                object raw;
                // 并行生成批次会同时到达：按到达顺序原子地分配响应。
                lock (owner.Bodies) { owner.Bodies.Add(body); raw = owner.responses[owner.Bodies.Count - 1]; }
                var content = raw is string text ? text : JsonSerializer.Serialize(raw, JsonOptions);
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } })) };
            }
        }
        private readonly object[] responses = responses;
    }
}
