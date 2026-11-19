using Jogeoai.Infrastructure;
using Jogeoai.Application;

namespace Jogeoai.Worker;

public class Worker(ILogger<Worker> logger, MySqlDatabase database, IVisibilityCollectorStore collectorStore) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await database.CheckConnectionAsync(cancellationToken);
        await database.EnsureSchemaAsync(cancellationToken);
        logger.LogInformation("MySQL connection and Cloro settings schema verified. Domestic lease recovery and Cloro Submit/Poll workers started.");
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Worker running at: {time}", DateTimeOffset.UtcNow);
            }
            try
            {
                var recovered = await collectorStore.RecoverExpiredTasksAsync(stoppingToken);
                if (recovered > 0) logger.LogInformation("Recovered {Count} expired domestic collector tasks.", recovered);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning("Domestic collector recovery failed with {ExceptionType}.", exception.GetType().Name);
            }
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
