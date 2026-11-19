using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Jogeoai.Tests;

public sealed class SubscriptionWorkerTests
{
    private static readonly DateTime Now = new(2026, 10, 19, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task PassRollsOverBeforeDowngradingWithTheConfiguredBatchSize()
    {
        var store = new FakeSubscriptionStore { Rollover = 3, Downgrade = 1 };
        var worker = new SubscriptionWorker(NullLogger<SubscriptionWorker>.Instance, store,
            Options.Create(new SubscriptionOptions { BatchSize = 25 }));

        Assert.Equal((3, 1), await worker.RunPassAsync(Now));

        Assert.Equal(new[] { ("rollover", Now, 25), ("downgrade", Now, 25) }, store.Calls);
    }

    [Fact]
    public async Task InvalidBatchSizeFallsBackToDefaultAndOptionsCarryDocumentedDefaults()
    {
        var options = new SubscriptionOptions();
        Assert.Equal(TimeSpan.FromSeconds(60), options.RolloverInterval);
        Assert.Equal(100, options.BatchSize);
        Assert.Equal(10, options.RedeemFailuresPerHour);

        var store = new FakeSubscriptionStore();
        var worker = new SubscriptionWorker(NullLogger<SubscriptionWorker>.Instance, store,
            Options.Create(new SubscriptionOptions { BatchSize = 0 }));
        await worker.RunPassAsync(Now);
        Assert.All(store.Calls, call => Assert.Equal(100, call.Batch));
    }

    [Fact]
    public async Task LoopSurvivesStoreFailuresAndStopsOnCancellation()
    {
        var store = new FakeSubscriptionStore { Failure = new InvalidOperationException("schema missing") };
        var worker = new SubscriptionWorker(NullLogger<SubscriptionWorker>.Instance, store,
            Options.Create(new SubscriptionOptions { RolloverInterval = TimeSpan.FromMilliseconds(10) }));

        await worker.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (store.Calls.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
        await worker.StopAsync(CancellationToken.None);

        // 异常被记录并退避，不会让宿主崩溃；取消后循环退出。
        Assert.NotEmpty(store.Calls);
        Assert.True(worker.ExecuteTask is null || worker.ExecuteTask.IsCompleted);
    }

    private sealed class FakeSubscriptionStore : ISubscriptionStore
    {
        internal List<(string Kind, DateTime Now, int Batch)> Calls { get; } = [];
        internal int Rollover { get; set; }
        internal int Downgrade { get; set; }
        internal Exception? Failure { get; set; }

        public Task<int> RolloverDuePeriodsAsync(DateTime utcNow, int batchSize, CancellationToken cancellationToken = default)
        {
            Calls.Add(("rollover", utcNow, batchSize));
            if (Failure is not null) throw Failure;
            return Task.FromResult(Rollover);
        }

        public Task<int> DowngradeExpiredAsync(DateTime utcNow, int batchSize, CancellationToken cancellationToken = default)
        {
            Calls.Add(("downgrade", utcNow, batchSize));
            if (Failure is not null) throw Failure;
            return Task.FromResult(Downgrade);
        }

        public Task<IReadOnlyList<SubscriptionPlanView>> ListPlansAsync(Guid? tenantId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TenantSubscriptionView> GetAsync(Guid tenantId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TenantSubscriptionView> RedeemAsync(Guid tenantId, Guid userId, string code, string clientIp, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TenantSubscriptionView> AssignAsync(Guid tenantId, Guid actorUserId, AssignSubscriptionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AdminTenantSubscriptionView> GetForAdminAsync(Guid tenantId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SubscriptionPlanView> UpdatePlanAsync(string code, SubscriptionPlanUpdate update, Guid actorUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PlanCodeBatchResult> CreateCodeBatchAsync(PlanCodeBatchRequest request, Guid actorUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PlanCodePage> ListCodesAsync(Guid? batchId, string? status, string? planCode, string? cursor, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PlanCodeView> RevokeCodeAsync(Guid codeId, Guid actorUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
