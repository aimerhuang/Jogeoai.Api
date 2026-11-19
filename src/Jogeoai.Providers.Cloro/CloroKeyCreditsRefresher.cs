using Jogeoai.Providers.Abstractions;
using Microsoft.Extensions.Logging;

namespace Jogeoai.Providers.Cloro;

/// <summary>查询单个 Cloro 账号余额并写回 key 池；后台刷新、管理员手动刷新和积分不足判定共用。</summary>
public sealed class CloroKeyCreditsRefresher(ICloroKeyPool pool, ICloroCreditsClient client, ILogger<CloroKeyCreditsRefresher> logger)
{
    /// <summary>刷新余额；401 把账号标记为无效，其他失败只记录错误码。成功时返回余额快照，失败返回空值。</summary>
    /// <param name="key">需要刷新的 key。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<CloroCreditsSnapshot?> RefreshAsync(CloroKeyLease key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        CloroCreditsSnapshot credits;
        try { credits = await client.GetCreditsAsync(key, cancellationToken); }
        catch (CloroProviderException exception) when (exception.Code == "CLORO_KEY_INVALID")
        {
            logger.LogWarning("Cloro account {AccountLabel} (key ending {KeyHint}) was rejected as invalid.", key.AccountLabel, key.KeyHint);
            await pool.MarkRejectedAsync(key.KeyId, null, CloroKeyRejection.Unauthorized, "HTTP_401", cancellationToken);
            return null;
        }
        catch (CloroProviderException exception)
        {
            logger.LogInformation("Cloro credits refresh for account {AccountLabel} failed with {ErrorCode}.", key.AccountLabel, exception.Code);
            await pool.MarkCreditsFailedAsync(key.KeyId, exception.Code, cancellationToken);
            return null;
        }
        await pool.ApplyCreditsAsync(key.KeyId, credits, cancellationToken);
        return credits;
    }
}
