using Jogeoai.Providers.Abstractions;
using Jogeoai.Providers.Cloro;
using Microsoft.Extensions.Options;

namespace Jogeoai.Worker;

/// <summary>定期查询 Cloro 账号余额并据此转换 key 池状态；不在提交的关键路径上。</summary>
public sealed class CloroKeyCreditsWorker(
    ILogger<CloroKeyCreditsWorker> logger,
    ICloroKeyPool pool,
    CloroKeyCreditsRefresher refresher,
    IOptions<CloroKeyPoolOptions> options) : BackgroundService
{
    // Accounts flagged for refresh (credits_checked_at IS NULL) are picked up on the next short pass.
    private static readonly TimeSpan PassInterval = TimeSpan.FromSeconds(15);

    /// <summary>串行刷新所有到期或等待刷新的账号，返回成功刷新的数量。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<int> RefreshDueAsync(CancellationToken cancellationToken = default)
    {
        var interval = options.Value.CreditRefreshInterval > TimeSpan.Zero ? options.Value.CreditRefreshInterval : TimeSpan.FromMinutes(10);
        var refreshed = 0;
        foreach (var key in await pool.ListRefreshDueAsync(interval, cancellationToken))
            if (await refresher.RefreshAsync(key, cancellationToken) is not null) refreshed++;
        return refreshed;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshDueAsync(stoppingToken);
                await Task.Delay(PassInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                logger.LogWarning("Cloro key credits loop failed with {ExceptionType}.", exception.GetType().Name);
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
    }
}
