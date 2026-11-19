using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Worker;
using Microsoft.Extensions.Logging;

namespace Jogeoai.Tests;

/// <summary>网址识别 Worker 用内存任务存储、假网页读取与假 Provider 验证 fetch → model → finalize 各分支，不触网、不连库。</summary>
public sealed class KnowledgeExtractWorkerTests
{
    private const string PageSentence = "优联科技成立于 2008 年，总部位于深圳，专注管道检测设备研发。";

    [Fact]
    public async Task UnreadablePageFailsWithoutRetryAndSkipsTheModel()
    {
        var harness = new Harness { Page = Failed("SOURCE_BLOCKED", "网页拒绝了读取请求。") };

        Assert.True(await harness.Worker.RunOnceAsync());

        var failure = Assert.Single(harness.Store.Failures);
        Assert.Equal("KNOWLEDGE_LINK_UNREADABLE", failure.Code);
        Assert.Equal("网页无法直接读取（网页拒绝了读取请求），AI 联网读取也未成功：AI 未能打开该网页。", failure.Message);
        Assert.False(failure.Retryable);
        // AI 联网读取首次未得到可用正文时再试一次。
        Assert.Equal(KnowledgeExtractWorker.ModelPageReadAttempts, harness.PageReader.Calls);
        Assert.Equal(0, harness.Provider.Calls);
        Assert.Empty(harness.Store.Completions);
        Assert.Equal(["fetch"], harness.Store.Steps);
        Assert.False(await harness.Worker.RunOnceAsync());
    }

    [Fact]
    public async Task PageBodyShorterThanTwoHundredCharactersFailsAsTooShort()
    {
        var harness = new Harness { Page = Ready(new string('字', KnowledgeExtractWorker.MinPageCharacters - 1)) };

        await harness.Worker.RunOnceAsync();

        var failure = Assert.Single(harness.Store.Failures);
        Assert.Equal("KNOWLEDGE_EXTRACT_SOURCE_TOO_SHORT", failure.Code);
        Assert.False(failure.Retryable);
        // 直接读取正文过短时先尝试 AI 联网读取（未得到可用正文时再试一次）；兜底也失败则保留「正文过少」。
        Assert.Equal(KnowledgeExtractWorker.ModelPageReadAttempts, harness.PageReader.Calls);
        Assert.Equal(0, harness.Provider.Calls);
    }

    [Fact]
    public async Task PrivateHostOnADomainFallsBackToModelReadAndEvidenceComesFromItsText()
    {
        const string modelText = "柯丽尔 CR-10 牙科影像板，分辨率 20 lp/mm，支持 DICOM 3.0 输出。";
        var fallbackBody = modelText + "\n" + new string('述', 200);
        var harness = new Harness
        {
            Page = Failed("SOURCE_PRIVATE_HOST", "网页地址不允许读取。", "unreadable"),
            PageRead = PageRead(fallbackBody),
            Items =
            [
                new KnowledgeExtractRawCandidate("product_service", "分辨率", "分辨率 20 lp/mm。", "分辨率 20 lp/mm"),
                // 原文依据只在直接读取的正文里才会有（这里直接读取失败），应被丢弃。
                new KnowledgeExtractRawCandidate("brand_intro", "成立背景", "成立于 2008 年。", "优联科技成立于 2008 年")
            ]
        };

        await harness.Worker.RunOnceAsync();

        Assert.Empty(harness.Store.Failures);
        Assert.Equal(1, harness.PageReader.Calls);
        Assert.Equal(Harness.Url, harness.PageReader.Urls.Single());
        Assert.Same(harness.Configuration, harness.PageReader.Configurations.Single());
        var input = Assert.Single(harness.Provider.Inputs);
        Assert.Equal(fallbackBody, input.PageText);
        Assert.Null(input.PageTitle);
        var completion = Assert.Single(harness.Store.Completions);
        var candidate = Assert.Single(completion.Candidates);
        Assert.Equal("分辨率", candidate.Title);
        Assert.Null(completion.PageTitle);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fallbackBody))).ToLowerInvariant(), completion.PageHash);
        Assert.Equal(["fetch", "model", "finalize"], harness.Store.Steps);
        Assert.Contains(harness.Logs.Messages, message => message.Contains("ReadMode=model", StringComparison.Ordinal));
        Assert.DoesNotContain(harness.Logs.Messages, message => message.Contains("lp/mm", StringComparison.Ordinal)
            || message.Contains("柯丽尔", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ShortDirectBodyFallsBackToModelReadAndKeepsTheDirectTitle()
    {
        var fallbackBody = PageSentence + new string('述', 200);
        var harness = new Harness
        {
            Page = Ready("请启用脚本。"),
            PageRead = PageRead(fallbackBody),
            Items = [new KnowledgeExtractRawCandidate("brand_intro", "成立背景", "2008 年成立。", "优联科技成立于 2008 年")]
        };

        await harness.Worker.RunOnceAsync();

        Assert.Empty(harness.Store.Failures);
        Assert.Equal(1, harness.PageReader.Calls);
        Assert.Equal(fallbackBody, harness.Provider.Inputs.Single().PageText);
        var completion = Assert.Single(harness.Store.Completions);
        Assert.Equal("关于我们", completion.PageTitle);
        Assert.Single(completion.Candidates);
    }

    [Fact]
    public async Task DirectReadUsableSkipsTheModelRead()
    {
        var harness = new Harness { Page = Ready(PageSentence + new string('。', 200)) };

        await harness.Worker.RunOnceAsync();

        Assert.Equal(0, harness.PageReader.Calls);
        Assert.Single(harness.Store.Completions);
        Assert.Contains(harness.Logs.Messages, message => message.Contains("ReadMode=direct", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, "AI 未能打开该网页。", "网页无法直接读取（网页地址不允许读取），AI 联网读取也未成功：AI 未能打开该网页。")]
    [InlineData(false, "AI 联网读取网页超时。", "网页无法直接读取（网页地址不允许读取），AI 联网读取也未成功：AI 联网读取网页超时。")]
    [InlineData(true, null, "网页无法直接读取（网页地址不允许读取），AI 联网读取也未成功：读取到的正文过少（不足 200 字）。")]
    public async Task FallbackFailureFailsAsUnreadableWithCombinedMessage(bool succeeded, string? error, string expected)
    {
        var harness = new Harness
        {
            Page = Failed("SOURCE_PRIVATE_HOST", "网页地址不允许读取。", "unreadable"),
            PageRead = succeeded ? PageRead("太短的正文。") : KnowledgePageReadResult.Failed(error!, 10)
        };

        await harness.Worker.RunOnceAsync();

        var failure = Assert.Single(harness.Store.Failures);
        Assert.Equal("KNOWLEDGE_LINK_UNREADABLE", failure.Code);
        Assert.Equal(expected, failure.Message);
        Assert.False(failure.Retryable);
        Assert.Equal(KnowledgeExtractWorker.ModelPageReadAttempts, harness.PageReader.Calls);
        Assert.Equal(0, harness.Provider.Calls);
        Assert.Equal(["fetch"], harness.Store.Steps);
    }

    [Fact]
    public async Task FirstModelReadFailureIsRetriedOnceAndTheSecondReadIsUsed()
    {
        var fallbackBody = PageSentence + new string('述', 200);
        var harness = new Harness
        {
            Page = Failed("SOURCE_PRIVATE_HOST", "网页地址不允许读取。", "unreadable"),
            PageReads = [KnowledgePageReadResult.Failed("我无法打开该页面：访问给定网址时请求超时。", 10), PageRead(fallbackBody)],
            Items = [new KnowledgeExtractRawCandidate("brand_intro", "成立背景", "2008 年成立。", "优联科技成立于 2008 年")]
        };

        await harness.Worker.RunOnceAsync();

        Assert.Empty(harness.Store.Failures);
        Assert.Equal(2, harness.PageReader.Calls);
        Assert.All(harness.PageReader.Urls, url => Assert.Equal(Harness.Url, url));
        Assert.Equal(fallbackBody, harness.Provider.Inputs.Single().PageText);
        Assert.Single(Assert.Single(harness.Store.Completions).Candidates);
        Assert.Equal(["fetch", "model", "finalize"], harness.Store.Steps);
        var logs = harness.Logs.Messages;
        Assert.Contains(logs, message => message.Contains("Attempt=1/2", StringComparison.Ordinal) && message.Contains("Usable=False", StringComparison.Ordinal));
        Assert.Contains(logs, message => message.Contains("Attempt=2/2", StringComparison.Ordinal) && message.Contains("Usable=True", StringComparison.Ordinal));
        Assert.Contains(logs, message => message.Contains("ReadMode=model", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UsableFirstModelReadIsNotRetried()
    {
        var harness = new Harness
        {
            Page = Failed("SOURCE_PRIVATE_HOST", "网页地址不允许读取。", "unreadable"),
            PageReads = [PageRead(PageSentence + new string('述', 200)), KnowledgePageReadResult.Failed("不应被调用。")]
        };

        await harness.Worker.RunOnceAsync();

        Assert.Empty(harness.Store.Failures);
        Assert.Equal(1, harness.PageReader.Calls);
        Assert.Single(harness.Store.Completions);
    }

    [Fact]
    public async Task BothModelReadsFailingReportsTheLastReasonInTheCombinedMessage()
    {
        var harness = new Harness
        {
            Page = Failed("SOURCE_PRIVATE_HOST", "网页地址不允许读取。", "unreadable"),
            PageReads = [KnowledgePageReadResult.Failed("AI 联网读取网页超时。", 10), KnowledgePageReadResult.Failed("AI 未能打开该网页。", 10)]
        };

        await harness.Worker.RunOnceAsync();

        var failure = Assert.Single(harness.Store.Failures);
        Assert.Equal("KNOWLEDGE_LINK_UNREADABLE", failure.Code);
        Assert.Equal("网页无法直接读取（网页地址不允许读取），AI 联网读取也未成功：AI 未能打开该网页。", failure.Message);
        Assert.False(failure.Retryable);
        Assert.Equal(2, harness.PageReader.Calls);
        Assert.Equal(0, harness.Provider.Calls);
        Assert.Equal(["fetch"], harness.Store.Steps);
        Assert.Contains(harness.Logs.Messages, message => message.Contains("Attempt=2/2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UserCancellationDuringModelReadIsNotRetried()
    {
        var harness = new Harness(TimeSpan.FromMilliseconds(20))
        {
            Page = Failed("SOURCE_PRIVATE_HOST", "网页地址不允许读取。", "unreadable"),
            BlockPageRead = true,
            HeartbeatState = KnowledgeExtractHeartbeat.Cancelled
        };

        var run = harness.Worker.RunOnceAsync();
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(run, finished);
        Assert.True(await run);
        Assert.Equal(1, harness.PageReader.Calls);
        Assert.True(harness.PageReader.ObservedCancellation);
        Assert.Equal(0, harness.Provider.Calls);
        Assert.Empty(harness.Store.Failures);
        Assert.Empty(harness.Store.Completions);
        Assert.Contains(harness.Logs.Messages, message => message.Contains("cancelled by the user", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UserCancellationDuringTheRetryDelaySkipsTheSecondModelRead()
    {
        var harness = new Harness(TimeSpan.FromMilliseconds(20), retryDelay: TimeSpan.FromMinutes(10))
        {
            Page = Failed("SOURCE_PRIVATE_HOST", "网页地址不允许读取。", "unreadable"),
            HeartbeatState = KnowledgeExtractHeartbeat.Cancelled
        };

        var run = harness.Worker.RunOnceAsync();
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(run, finished);
        Assert.True(await run);
        Assert.Equal(1, harness.PageReader.Calls);
        Assert.Empty(harness.Store.Failures);
        Assert.Empty(harness.Store.Completions);
        Assert.Contains(harness.Logs.Messages, message => message.Contains("cancelled by the user", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StoppingTheWorkerDuringModelReadIsNotRetried()
    {
        using var stopping = new CancellationTokenSource();
        var harness = new Harness
        {
            Page = Failed("SOURCE_PRIVATE_HOST", "网页地址不允许读取。", "unreadable"),
            BlockPageRead = true,
            OnPageRead = stopping.Cancel
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Worker.RunOnceAsync(stopping.Token));

        Assert.Equal(1, harness.PageReader.Calls);
        Assert.Empty(harness.Store.Failures);
        Assert.Empty(harness.Store.Completions);
    }

    [Fact]
    public void ModelReadRetryDefaultsToOneRetryAfterTwoSeconds()
    {
        Assert.Equal(2, KnowledgeExtractWorker.ModelPageReadAttempts);
        Assert.Equal(TimeSpan.FromSeconds(2), KnowledgeExtractWorker.DefaultModelPageReadRetryDelay);
    }

    [Theory]
    [InlineData("http://198.18.0.5/about")]
    [InlineData("http://[::1]/about")]
    [InlineData("http://localhost/about")]
    [InlineData("https://printer.local/about")]
    [InlineData("https://metadata.google.internal/computeMetadata")]
    [InlineData("https://intranet/about")]
    public async Task IpLiteralAndReservedHostsNeverUseModelRead(string url)
    {
        var harness = new Harness
        {
            JobUrl = url,
            Page = Failed("SOURCE_PRIVATE_HOST", "网页地址不允许读取。", "unreadable", url),
            PageRead = PageRead(PageSentence + new string('述', 200))
        };

        await harness.Worker.RunOnceAsync();

        var failure = Assert.Single(harness.Store.Failures);
        Assert.Equal("KNOWLEDGE_LINK_UNREADABLE", failure.Code);
        Assert.Equal("网页地址不允许读取。", failure.Message);
        Assert.Equal(0, harness.PageReader.Calls);
        Assert.Equal(0, harness.Provider.Calls);
    }

    [Fact]
    public async Task MissingConfigurationFailsBeforeAnyReadOrFallback()
    {
        var harness = new Harness { Page = Failed("SOURCE_PRIVATE_HOST", "网页地址不允许读取。", "unreadable"), Configuration = null };

        await harness.Worker.RunOnceAsync();

        var failure = Assert.Single(harness.Store.Failures);
        Assert.Equal("KNOWLEDGE_EXTRACT_NOT_CONFIGURED", failure.Code);
        Assert.False(failure.Retryable);
        Assert.Equal(0, harness.Reader.Calls);
        Assert.Equal(0, harness.PageReader.Calls);
        Assert.Equal(0, harness.Provider.Calls);
    }

    [Fact]
    public async Task CandidatesWithoutPageEvidenceAreDroppedAndDuplicatesAreMarked()
    {
        var existing = Item(BrandKnowledgeDimension.SellingPoint, "IP68 防水", "整机 IP68 防水，可在 2 米水深工作。");
        var body = PageSentence + "\n\n产品整机 IP68 防水。" + new string('。', 200);
        var harness = new Harness
        {
            Page = Ready(body),
            Existing = [existing],
            Items =
            [
                new KnowledgeExtractRawCandidate("brand_intro", "成立背景", "优联科技成立于 2008 年，总部在深圳。", "优联科技成立于 2008 年，总部位于深圳"),
                // 原文依据是编造的：网页里没有「ISO 9001」，整条丢弃。
                new KnowledgeExtractRawCandidate("differentiator", "国际认证", "通过 ISO 9001 认证。", "公司已通过 ISO 9001 质量体系认证"),
                new KnowledgeExtractRawCandidate("selling_point", "ip68 防水", "整机 IP68 防水。", "产品整机 IP68 防水。")
            ]
        };

        await harness.Worker.RunOnceAsync();

        Assert.Empty(harness.Store.Failures);
        var completion = Assert.Single(harness.Store.Completions);
        Assert.Equal(2, completion.Candidates.Count);
        Assert.Equal(["1", "2"], completion.Candidates.Select(candidate => candidate.CandidateId));
        Assert.Equal(BrandKnowledgeDimension.BrandIntro, completion.Candidates[0].Dimension);
        Assert.Null(completion.Candidates[0].DuplicateOfItemId);
        Assert.Equal(existing.ItemId, completion.Candidates[1].DuplicateOfItemId);
        Assert.DoesNotContain(completion.Candidates, candidate => candidate.Content.Contains("ISO", StringComparison.Ordinal));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body.Trim()))).ToLowerInvariant(), completion.PageHash);
        Assert.Equal(BrandKnowledgeRules.CharacterCount(body.Trim()), completion.PageCharacters);
        Assert.Equal("关于我们", completion.PageTitle);
        Assert.Equal("gpt-6-luna", completion.ModelName);
        Assert.Equal(["fetch", "model", "finalize"], harness.Store.Steps);

        // 模型收到品牌名、规范化网址、限定维度与已有标题提示。
        var input = Assert.Single(harness.Provider.Inputs);
        Assert.Equal("优联科技", input.BrandName);
        Assert.Equal(Harness.Url, input.Url);
        Assert.Equal(body.Trim(), input.PageText);
        Assert.Equal(new KnowledgeExtractExistingTitle(BrandKnowledgeDimension.SellingPoint, "IP68 防水"), Assert.Single(input.ExistingTitles));
        // 日志只记录标识、模型、字节数与耗时，不记录网页正文或候选内容。
        Assert.DoesNotContain(harness.Logs.Messages, message => message.Contains("深圳", StringComparison.Ordinal)
            || message.Contains("IP68", StringComparison.Ordinal));
        Assert.Contains(harness.Logs.Messages, message => message.Contains("Model=gpt-6-luna", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DimensionLimitFromTheJobIsPassedToProviderAndNormalization()
    {
        var body = PageSentence + new string('。', 200);
        var harness = new Harness
        {
            Page = Ready(body),
            Dimensions = [BrandKnowledgeDimension.Faq],
            Items =
            [
                new KnowledgeExtractRawCandidate("brand_intro", "成立背景", "2008 年成立。", "优联科技成立于 2008 年"),
                new KnowledgeExtractRawCandidate("faq", "公司在哪里？", "总部位于深圳。", "总部位于深圳")
            ]
        };

        await harness.Worker.RunOnceAsync();

        Assert.Equal([BrandKnowledgeDimension.Faq], harness.Provider.Inputs.Single().Dimensions);
        var candidate = Assert.Single(harness.Store.Completions.Single().Candidates);
        Assert.Equal(BrandKnowledgeDimension.Faq, candidate.Dimension);
    }

    [Fact]
    public async Task CancellationDetectedByHeartbeatAbandonsTheRunWithoutWriting()
    {
        var harness = new Harness(TimeSpan.FromMilliseconds(20))
        {
            Page = Ready(PageSentence + new string('。', 200)),
            BlockProvider = true,
            HeartbeatState = KnowledgeExtractHeartbeat.Cancelled
        };

        var run = harness.Worker.RunOnceAsync();
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(run, finished);
        Assert.True(await run);
        Assert.Equal(1, harness.Provider.Calls);
        Assert.True(harness.Provider.ObservedCancellation);
        Assert.Empty(harness.Store.Completions);
        Assert.Empty(harness.Store.Failures);
        Assert.Contains(harness.Logs.Messages, message => message.Contains("cancelled by the user", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProviderErrorsAndMissingConfigurationAreRecordedWithTheirRetryability()
    {
        var retryable = new Harness
        {
            Page = Ready(PageSentence + new string('。', 200)),
            ProviderError = new KnowledgeExtractException("KNOWLEDGE_EXTRACT_RETRYABLE", "识别服务暂时繁忙，请稍后重试。", true)
        };
        await retryable.Worker.RunOnceAsync();
        var failure = Assert.Single(retryable.Store.Failures);
        Assert.Equal("KNOWLEDGE_EXTRACT_RETRYABLE", failure.Code);
        Assert.True(failure.Retryable);

        var unconfigured = new Harness { Page = Ready(PageSentence + new string('。', 200)), Configuration = null };
        await unconfigured.Worker.RunOnceAsync();
        var notConfigured = Assert.Single(unconfigured.Store.Failures);
        Assert.Equal("KNOWLEDGE_EXTRACT_NOT_CONFIGURED", notConfigured.Code);
        Assert.False(notConfigured.Retryable);
        Assert.Equal(0, unconfigured.Provider.Calls);
    }

    [Fact]
    public async Task LostLeaseAtCompletionIsLoggedAsFenced()
    {
        var harness = new Harness
        {
            Page = Ready(PageSentence + new string('。', 200)),
            Items = [new KnowledgeExtractRawCandidate("brand_intro", "成立背景", "2008 年成立。", "优联科技成立于 2008 年")],
            CompleteAccepted = false
        };

        await harness.Worker.RunOnceAsync();

        Assert.Single(harness.Store.Completions);
        Assert.Empty(harness.Store.Failures);
        Assert.Contains(harness.Logs.Messages, message => message.Contains("fenced", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0, 180)]
    [InlineData(-5, 180)]
    [InlineData(30, 30)]
    [InlineData(300, 180)]
    public void ModelTimeoutUsesConfigurationCappedAtOneHundredEightySeconds(int configured, int expected)
        => Assert.Equal(expected, KnowledgeExtractWorker.ModelTimeoutSeconds(configured));

    private static ArticleSourceReadResult Ready(string body)
        => new(Harness.Url, Harness.Url, new string('a', 64), "ready", "关于我们", body, null, [Harness.Url], null, null);

    private static ArticleSourceReadResult Failed(string code, string message, string status = "blocked", string url = Harness.Url)
        => new(url, url, new string('a', 64), status, null, null, null, [url], code, message);

    private static KnowledgePageReadResult PageRead(string body)
        => new(true, "模型引用标题", body, [Harness.Url], null, 1234, 456, 7890);

    private static BrandKnowledgeItemView Item(BrandKnowledgeDimension dimension, string title, string content)
        => new(Guid.NewGuid(), Harness.Site, dimension, title, content, "manual", null, null, "synced", 1, Guid.NewGuid(), Guid.NewGuid(),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    /// <summary>DispatchProxy 桩：只实现被调用的方法，其余抛 NotSupportedException。</summary>
    public class Stub : DispatchProxy
    {
        /// <summary>调用处理器。</summary>
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = (method, _) => throw new NotSupportedException(method.Name);

        /// <inheritdoc />
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);

        /// <summary>创建桩。</summary>
        public static T Create<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
        {
            var proxy = Create<T, Stub>();
            ((Stub)(object)proxy).Handler = handler;
            return proxy;
        }
    }

    private sealed class Harness
    {
        public const string Url = "https://example.test/about";
        public static readonly Guid Site = Guid.NewGuid();

        public Harness(TimeSpan? heartbeat = null, TimeSpan? retryDelay = null)
        {
            Store = new MemoryJobStore(this);
            Provider = new FakeProvider(this);
            Reader = new FakeReader(this);
            PageReader = new FakePageReader(this);
            var research = Stub.Create<IResearchStore>((method, args) => method.Name == nameof(IResearchStore.GetProviderConfigurationAsync)
                ? Task.FromResult(args![0] as string == "openai-compatible" ? Configuration : null)
                : throw new NotSupportedException(method.Name));
            var items = Stub.Create<IBrandKnowledgeItemStore>((method, _) => method.Name == nameof(IBrandKnowledgeItemStore.ListAsync)
                ? Task.FromResult(new BrandKnowledgeItemList(Existing, BrandKnowledgeRules.CompleteCounts(null), Existing.Count, 50))
                : throw new NotSupportedException(method.Name));
            Worker = new KnowledgeExtractWorker(Store, Reader, PageReader, Provider, items, research, Logs)
            {
                HeartbeatInterval = heartbeat ?? TimeSpan.FromMinutes(5),
                // 默认 2 秒的重试等待在测试中缩为 0，避免拖慢用例。
                ModelPageReadRetryDelay = retryDelay ?? TimeSpan.Zero
            };
        }

        public string JobUrl { get; init; } = Url;
        public ArticleSourceReadResult Page { get; init; } = Ready(PageSentence);
        public KnowledgePageReadResult PageRead { get; init; } = KnowledgePageReadResult.Failed("AI 未能打开该网页。");
        /// <summary>按调用顺序返回的读取结果；用完后回落到 <see cref="PageRead"/>。</summary>
        public IReadOnlyList<KnowledgePageReadResult>? PageReads { get; init; }
        public bool BlockPageRead { get; init; }
        public Action? OnPageRead { get; init; }
        public IReadOnlyList<BrandKnowledgeItemView> Existing { get; init; } = [];
        public IReadOnlyList<KnowledgeExtractRawCandidate> Items { get; init; } = [];
        public IReadOnlyList<BrandKnowledgeDimension>? Dimensions { get; init; }
        public bool BlockProvider { get; init; }
        public Exception? ProviderError { get; init; }
        public KnowledgeExtractHeartbeat HeartbeatState { get; init; } = KnowledgeExtractHeartbeat.Alive;
        public bool CompleteAccepted { get; init; } = true;
        public ResearchProviderConfiguration? Configuration { get; init; } =
            new("openai-compatible", "gpt-6-luna", "https://llm.test/v1/chat/completions", "token", false, 30, 1);

        public MemoryJobStore Store { get; }
        public FakeProvider Provider { get; }
        public FakeReader Reader { get; }
        public FakePageReader PageReader { get; }
        public CapturingLogger Logs { get; } = new();
        public KnowledgeExtractWorker Worker { get; }
    }

    private sealed class MemoryJobStore(Harness harness) : IKnowledgeExtractJobStore
    {
        private bool claimed;
        public List<string> Steps { get; } = [];
        public List<KnowledgeExtractCompletion> Completions { get; } = [];
        public List<(string Code, string Message, bool Retryable)> Failures { get; } = [];

        public Task<KnowledgeExtractJobLease?> ClaimAsync(string workerId, TimeSpan lease, CancellationToken cancellationToken = default)
        {
            if (claimed) return Task.FromResult<KnowledgeExtractJobLease?>(null);
            claimed = true;
            return Task.FromResult<KnowledgeExtractJobLease?>(new KnowledgeExtractJobLease(Guid.NewGuid(), Guid.NewGuid(), Harness.Site, "优联科技",
                Guid.NewGuid(), harness.JobUrl, harness.Dimensions, 1, workerId));
        }

        public Task<KnowledgeExtractHeartbeat> HeartbeatAsync(KnowledgeExtractJobLease lease, TimeSpan extend, CancellationToken cancellationToken = default)
            => Task.FromResult(harness.HeartbeatState);

        public Task<bool> MarkStepAsync(KnowledgeExtractJobLease lease, string step, CancellationToken cancellationToken = default)
        {
            Steps.Add(step);
            return Task.FromResult(true);
        }

        public Task<bool> CompleteAsync(KnowledgeExtractJobLease lease, KnowledgeExtractCompletion completion, CancellationToken cancellationToken = default)
        {
            Completions.Add(completion);
            return Task.FromResult(harness.CompleteAccepted);
        }

        public Task<bool> FailAsync(KnowledgeExtractJobLease lease, string errorCode, string errorMessage, bool retryable,
            CancellationToken cancellationToken = default)
        {
            Failures.Add((errorCode, errorMessage, retryable));
            return Task.FromResult(true);
        }

        public Task<KnowledgeExtractJobView> EnqueueAsync(Guid tenantId, Guid siteId, Guid userId, KnowledgeExtractRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<KnowledgeExtractJobView?> GetCurrentAsync(Guid tenantId, Guid siteId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<KnowledgeExtractJobView?> GetAsync(Guid tenantId, Guid siteId, Guid jobId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<KnowledgeExtractJobView> CancelAsync(Guid tenantId, Guid siteId, Guid jobId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<BrandKnowledgeItemView>> SaveCandidatesAsync(Guid tenantId, Guid siteId, Guid jobId, Guid userId,
            KnowledgeExtractSaveRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<KnowledgeExtractJobView> MarkResolvedAsync(Guid tenantId, Guid siteId, Guid jobId, string resolution,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeReader(Harness harness) : IArticleSourceReader
    {
        public int Calls { get; private set; }

        public Task<ArticleSourceReadResult> ReadAsync(string url, CancellationToken cancellationToken = default)
        {
            Calls++;
            Assert.Equal(harness.JobUrl, url);
            return Task.FromResult(harness.Page);
        }
    }

    private sealed class FakePageReader(Harness harness) : IKnowledgePageReadProvider
    {
        public int Calls => Urls.Count;
        public bool ObservedCancellation { get; private set; }
        public List<string> Urls { get; } = [];
        public List<ResearchProviderConfiguration> Configurations { get; } = [];

        public async Task<KnowledgePageReadResult> ReadAsync(string url, ResearchProviderConfiguration configuration,
            CancellationToken cancellationToken = default)
        {
            Urls.Add(url);
            Configurations.Add(configuration);
            harness.OnPageRead?.Invoke();
            if (harness.BlockPageRead)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) { ObservedCancellation = true; throw; }
            }
            var sequence = harness.PageReads;
            return sequence is not null && Calls <= sequence.Count ? sequence[Calls - 1] : harness.PageRead;
        }
    }

    private sealed class FakeProvider(Harness harness) : IKnowledgeExtractProvider
    {
        public int Calls { get; private set; }
        public bool ObservedCancellation { get; private set; }
        public List<KnowledgeExtractInput> Inputs { get; } = [];
        public string Provider => "openai-compatible";

        public async Task<KnowledgeExtractProviderResult> ExtractAsync(KnowledgeExtractInput input, ResearchProviderConfiguration configuration,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Inputs.Add(input);
            if (harness.ProviderError is not null) throw harness.ProviderError;
            if (harness.BlockProvider)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) { ObservedCancellation = true; throw; }
            }
            return new KnowledgeExtractProviderResult(harness.Items, false, "gpt-6-luna", "openai-compatible", "knowledge-extract-v3", 12, 345, 678);
        }
    }

    private sealed class CapturingLogger : ILogger<KnowledgeExtractWorker>
    {
        private readonly object gate = new();
        private readonly List<string> messages = [];

        public IReadOnlyList<string> Messages { get { lock (gate) return messages.ToArray(); } }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (gate) messages.Add(formatter(state, exception));
        }
    }
}
