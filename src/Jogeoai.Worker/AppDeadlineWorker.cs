using Jogeoai.Application;

namespace Jogeoai.Worker;

/// <summary>
/// App 渠道的截止处理（design 4.3）：每 30 秒把已过周期截止时点、仍未成功的 App 检测置为未采集，并释放其预留积分。
/// 与创建 App 周期任务的 <see cref="CollectionScheduleWorker"/> 注册在同一处，所以完整模式和 MonitoringOnly 模式都会运行；
/// 不能放在只在完整模式注册的网页租约回收循环里，否则 MonitoringOnly 模式下 App 任务能被创建却永远不会被判为未采集。
/// 处理按任务加锁并跳过已被锁住的任务（SKIP LOCKED），每个过期任务只处理一次；049 未执行时什么都不做。
/// </summary>
public sealed class AppDeadlineWorker(ILogger<AppDeadlineWorker> logger, IAppCollectionStore appCollectionStore) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var expired = await appCollectionStore.ExpireOverdueTasksAsync(stoppingToken);
                if (expired > 0) logger.LogInformation("Marked {Count} overdue App checks as not collected.", expired);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning("App collection deadline pass failed with {ExceptionType}.", exception.GetType().Name);
            }
            await Task.Delay(Interval, stoppingToken);
        }
    }
}
