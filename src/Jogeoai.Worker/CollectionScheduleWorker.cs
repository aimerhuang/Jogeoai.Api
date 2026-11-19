using Jogeoai.Application;
using Jogeoai.Domain;

namespace Jogeoai.Worker;

/// <summary>领取到期计划并恢复尚未关联 Run 的时隙。</summary>
public sealed class CollectionScheduleWorker(
    ILogger<CollectionScheduleWorker> logger,
    ICollectionScheduleStore schedules,
    ICloroVisibilityStore runs) : BackgroundService
{
    /// <summary>执行一轮计划领取和 pending 时隙恢复。</summary>
    public async Task<int> ProcessOnceAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        await schedules.MaterializeDueSlotsAsync(nowUtc, 20, cancellationToken);
        var pending = await schedules.GetPendingSlotsAsync(20, cancellationToken);
        var completed = 0;
        foreach (var slot in pending)
        {
            try
            {
                if (await schedules.ProcessSlotAsync(slot, runs, cancellationToken)) completed++;
            }
            catch (CollectorStoreException exception)
            {
                // A pending slot remains durable. The next pass retries with the same request and key.
                logger.LogWarning("Schedule slot {SlotId} could not create a Run: {Code}.", slot.Id, exception.Code);
            }
        }
        return completed;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var completed = await ProcessOnceAsync(DateTimeOffset.UtcNow, stoppingToken);
                if (completed > 0) logger.LogInformation("Created {Count} scheduled Runs.", completed);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                logger.LogWarning("Schedule loop failed with {ExceptionType}.", exception.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }
}
