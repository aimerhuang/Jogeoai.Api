using System.Net;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Worker;
using Jogeoai.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jogeoai.Tests;

public sealed class BrandResearchWorkerTests
{
    [Fact]
    public async Task DomainlessBrandUsesUserEvidenceWithoutRequestingAWebsite()
    {
        var store = new MemoryResearchStore { Request = new(null, "检测品牌", BusinessDescription: "研发工业检测设备与管道摄像头") };
        var provider = new RecordingProvider();
        var clients = new WebsiteClient("network");
        using var worker = new BrandResearchWorker(store, [provider], clients, NullLogger<BrandResearchWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await store.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, clients.Calls);
            Assert.Null(provider.Request!.Website);
            Assert.Contains(provider.Context!.Evidence, item => item.Id == "user" && item.Excerpt.Contains("管道摄像头", StringComparison.Ordinal));
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task DomainlessBrandWithOnlyKnowledgeItemsReachesTheModelWithoutNeedsInput()
    {
        var request = new StartBrandResearchRequest(null, "检测品牌", 10);
        var knowledge = ResearchContextRules.KnowledgeEvidence([BrandResearchContextTests.Item("公司介绍", "研发工业检测设备与管道摄像头。")]);
        var store = new MemoryResearchStore { Request = request, Progress = ResearchContextRules.Create(request, null, null, knowledge) };
        var provider = new RecordingProvider();
        var clients = new WebsiteClient("network");
        using var worker = new BrandResearchWorker(store, [provider], clients, NullLogger<BrandResearchWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await store.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, clients.Calls);
            Assert.Equal(0, store.Failures);
            Assert.Equal(knowledge, provider.Context!.Evidence);
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task WebsiteEvidenceIsCollectedAlongsideKnowledgeItemsAndUserInput()
    {
        var request = new StartBrandResearchRequest("https://example.com", "检测品牌", 10, BusinessDescription: "以市政管道为主");
        var knowledge = ResearchContextRules.KnowledgeEvidence([BrandResearchContextTests.Item("核心卖点", "推杆可达 120 米。", BrandKnowledgeDimension.SellingPoint)]);
        var store = new MemoryResearchStore { Request = request, Progress = ResearchContextRules.Create(request, null, null, knowledge) };
        var provider = new RecordingProvider();
        using var worker = new BrandResearchWorker(store, [provider], new WebsiteClient(""), NullLogger<BrandResearchWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await store.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var evidence = provider.Context!.Evidence;
            Assert.Contains(evidence, e => e.Url is not null && e.Excerpt.Contains("工业检测设备", StringComparison.Ordinal));
            Assert.Equal(knowledge[0], Assert.Single(evidence, ResearchContextRules.IsKnowledgeEvidence));
            Assert.Equal("user", evidence[^1].Id);
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    [Theory]
    [InlineData("http")]
    [InlineData("network")]
    [InlineData("timeout")]
    [InlineData("unsafe-redirect")]
    [InlineData("redirect-loop")]
    public async Task UnavailableWebsiteRequiresBusinessInputWithoutCallingModel(string failure)
    {
        var store = new MemoryResearchStore();
        var provider = new RecordingProvider();
        using var worker = new BrandResearchWorker(store, [provider], new WebsiteClient(failure),
            NullLogger<BrandResearchWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var code = await store.Failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("RESEARCH_NEEDS_INPUT", code);
            Assert.Null(provider.Request);
            Assert.False(store.Retryable);
            Assert.Equal("needs_input", store.Progress?.Phase);

        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("redirect")]
    public async Task AvailableWebsiteIsPassedToTheModelAsText(string mode)
    {
        var store = new MemoryResearchStore();
        var provider = new RecordingProvider();
        using var worker = new BrandResearchWorker(store, [provider], new WebsiteClient(mode),
            NullLogger<BrandResearchWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await store.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("工业检测设备", provider.Context!.Evidence[0].Excerpt);
            Assert.DoesNotContain("ignored()", provider.Context.Evidence[0].Excerpt);
            Assert.True(store.Renewals >= 2);
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task CancellationOrLostLeaseStopsAnInFlightProviderWithoutPublishing()
    {
        var store = new MemoryResearchStore();
        var provider = new WaitingProvider();
        using var worker = new BrandResearchWorker(store, [provider], new WebsiteClient(""), NullLogger<BrandResearchWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            store.AllowRenew = false;
            await provider.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(25));
            Assert.False(store.Completed.Task.IsCompleted);
            Assert.Equal(0, store.Failures);
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }
    private sealed class WaitingProvider : IResearchProvider
    {
        public string Provider => "openai-compatible";
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ResearchProviderResult> RunAsync(ResearchProviderConfiguration configuration, StartBrandResearchRequest request,
            ResearchContext context, Func<ResearchContext, CancellationToken, Task> saveProgress, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            throw new InvalidOperationException();
        }
    }

    [Fact]
    public async Task TransientProviderFailureReusesSavedProfileWithoutRecrawling()
    {
        var store = new MemoryResearchStore { RetryAfterFailure = true };
        var provider = new RetryProvider(); var clients = new WebsiteClient("");
        using var worker = new BrandResearchWorker(store, [provider], clients, NullLogger<BrandResearchWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await store.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, provider.Calls); Assert.Equal(1, clients.Calls);
            Assert.Equal(1, store.Failures); Assert.True(store.Retryable);
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }
    private sealed class RetryProvider : IResearchProvider
    {
        public string Provider => "openai-compatible";
        public int Calls { get; private set; }
        public async Task<ResearchProviderResult> RunAsync(ResearchProviderConfiguration configuration, StartBrandResearchRequest request,
            ResearchContext context, Func<ResearchContext, CancellationToken, Task> saveProgress, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Calls == 1)
            {
                await saveProgress(context with { Profile = new("v2", "设备", new("品牌", "web-1", "品牌原文"),
                    new("设备", "web-1", "产品原文"), [], [], [], [], [], [], []) }, cancellationToken);
                throw new HttpRequestException("transient provider outage");
            }
            Assert.NotNull(context.Profile);
            return new(new("品牌", [], [], [], [], Provider, "model"), Provider, "model", context);
        }
    }

    private sealed class WebsiteClient(string failure) : IHttpClientFactory
    {
        public int Calls { get; private set; }
        public HttpClient CreateClient(string name) { Calls++; return new(new Handler(failure)); }
        private sealed class Handler(string failure) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (failure is "unsafe-redirect" or "redirect-loop")
                {
                    var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                    redirect.Headers.Location = new Uri(failure == "unsafe-redirect" ? "file:///etc/passwd" : "https://example.com/");
                    return Task.FromResult(redirect);
                }
                if (failure == "redirect" && request.RequestUri!.AbsolutePath == "/")
                {
                    var redirect = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
                    redirect.Headers.Location = new Uri("/home", UriKind.Relative);
                    return Task.FromResult(redirect);
                }
                return failure switch
                {
                    "network" => throw new HttpRequestException("website unavailable"),
                    "timeout" => throw new TaskCanceledException("website timed out"),
                    _ => Task.FromResult(new HttpResponseMessage(failure == "http" ? HttpStatusCode.Forbidden : HttpStatusCode.OK)
                    {
                        Content = new StringContent("<style>.ignore{display:none}</style><script>ignored()</script><main>检测品牌研发工业检测设备，主要产品包括管道摄像头、检测机器人，应用于市政管道排查、建筑排水系统维护，帮助维修人员观察管内堵塞与损坏情况，并记录现场图像。</main>"),
                    }),
                };
            }
        }
    }

    private sealed class RecordingProvider : IResearchProvider
    {
        public string Provider => "openai-compatible";
        public ResearchContext? Context { get; private set; }
        public StartBrandResearchRequest? Request { get; private set; }
        public Task<ResearchProviderResult> RunAsync(ResearchProviderConfiguration configuration,
            StartBrandResearchRequest request, ResearchContext context, Func<ResearchContext, CancellationToken, Task> saveProgress, CancellationToken cancellationToken = default)
        {
            Request = request;
            Context = context;
            return Task.FromResult(new ResearchProviderResult(new BrandResearchSuggestion("检测品牌", [], [], [],
                Enumerable.Range(1, 10).Select(i => new SuggestedPrompt($"测试场景 {i}？", [], false)).ToArray(),
                Provider, configuration.Model), Provider, configuration.Model, context with { Phase = "done" }));
        }
    }

    private sealed class MemoryResearchStore : IResearchStore
    {
        private bool leased;
        private int attempts;
        public bool RetryAfterFailure { get; set; }
        public StartBrandResearchRequest Request { get; set; } = new("https://example.com", "检测品牌", 10);
        private readonly Guid jobId = Guid.NewGuid();
        public TaskCompletionSource<ResearchProviderResult> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Failures { get; private set; }
        public bool Retryable { get; private set; }
        public bool AllowRenew { get; set; } = true;
        public int Renewals { get; private set; }
        public ResearchContext? Progress { get; set; }
        public TaskCompletionSource<string> Failed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<bool> RenewLeaseAsync(Guid id, string owner, TimeSpan duration, CancellationToken cancellationToken = default)
        { Renewals++; return Task.FromResult(AllowRenew); }
        public Task<bool> SaveProgressAsync(Guid id, string owner, ResearchContext context, CancellationToken cancellationToken = default)
        { Progress = context; return Task.FromResult(AllowRenew); }
        public Task<ResearchWorkItem?> LeaseAsync(string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
        {
            if (leased) return Task.FromResult<ResearchWorkItem?>(null);
            leased = true;
            return Task.FromResult<ResearchWorkItem?>(new(jobId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                Request, ++attempts, workerId, Progress));
        }
        public Task CompleteAsync(Guid id, string owner, ResearchProviderResult result, CancellationToken cancellationToken = default)
        { Assert.Equal(jobId, id); Completed.TrySetResult(result); return Task.CompletedTask; }
        public Task FailAsync(Guid id, string owner, string code, string message, bool retryable, CancellationToken cancellationToken = default)
        { Failures++; Retryable = retryable; if (RetryAfterFailure && retryable) leased = false; Failed.TrySetResult(code); return Task.CompletedTask; }
        public Task<IReadOnlyList<ResearchProviderSettingsView>> ListProviderSettingsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ResearchProviderSettingsView>>([new("openai-compatible", "test-model", "https://llm.test", true, 1, false, 30, true, null, DateTimeOffset.UtcNow)]);
        public Task<ResearchProviderConfiguration?> GetProviderConfigurationAsync(string provider, CancellationToken cancellationToken = default)
            => Task.FromResult<ResearchProviderConfiguration?>(new(provider, "test-model", "https://llm.test", "test-secret", false, 30, 1));
        public Task<BrandResearchAccepted> EnqueueAsync(Guid tenantId, Guid siteId, Guid userId, StartBrandResearchRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BrandResearchJobView?> GetAsync(Guid tenantId, Guid siteId, Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> CancelAsync(Guid tenantId, Guid siteId, Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> IsSystemAdministratorAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveProviderSettingsAsync(string provider, SaveResearchProviderSettingsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
