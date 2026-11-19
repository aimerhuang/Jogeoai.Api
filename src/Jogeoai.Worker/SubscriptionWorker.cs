using Jogeoai.Application;
using Microsoft.Extensions.Options;

namespace Jogeoai.Worker;

/// <summary>
/// 定期处理套餐额度周期：到期的周期清零可用积分（预占不动）并发放下一周期额度；付费到期的订阅降为免费体验版。
/// 每次发放 / 清零都用确定的账本引用，重复执行不重复入账。默认每 60 秒一轮，异常后退避 1 分钟。
/// </summary>
public sealed class SubscriptionWorker(
    ILogger<SubscriptionWorker> logger,
    ISubscriptionStore store,
    IOptions<SubscriptionOptions> options) : BackgroundService
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(60);
    private const int DefaultBatchSize = 100;

    /// <summary>执行一轮：先滚动到期周期，再降级到期订阅；返回两步各自处理的订阅数。</summary>
    /// <param name="utcNow">本轮使用的 UTC 时间。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<(int RolledOver, int Downgraded)> RunPassAsync(DateTime utcNow, CancellationToken cancellationToken = default)
    {
        var batchSize = options.Value.BatchSize > 0 ? options.Value.BatchSize : DefaultBatchSize;
        var rolledOver = await store.RolloverDuePeriodsAsync(utcNow, batchSize, cancellationToken);
        var downgraded = await store.DowngradeExpiredAsync(utcNow, batchSize, cancellationToken);
        if (rolledOver > 0 || downgraded > 0)
            logger.LogInformation("Subscription pass rolled over {RolledOver} periods and downgraded {Downgraded} subscriptions.",
                rolledOver, downgraded);
        return (rolledOver, downgraded);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.RolloverInterval > TimeSpan.Zero ? options.Value.RolloverInterval : DefaultInterval;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunPassAsync(DateTime.UtcNow, stoppingToken);
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                logger.LogWarning("Subscription rollover loop failed with {ExceptionType}.", exception.GetType().Name);
                try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            }
        }
    }
}
