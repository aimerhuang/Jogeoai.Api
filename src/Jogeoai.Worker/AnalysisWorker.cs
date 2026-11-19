using Jogeoai.Application;

namespace Jogeoai.Worker;

/// <summary>只处理已入库回答的异步提及分析 Worker。</summary>
public sealed class AnalysisWorker(IAnalysisStore store, ILogger<AnalysisWorker> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workerId = $"analysis-{Environment.MachineName}-{Guid.NewGuid():N}";
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await store.ProcessPendingAsync(workerId, 10, stoppingToken);
                if (processed == 0) await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Analysis worker iteration failed");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
