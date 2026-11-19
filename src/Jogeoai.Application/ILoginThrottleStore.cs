namespace Jogeoai.Application;

/// <summary>
/// 登录失败频控的共享存储（迁移 045 <c>auth_login_throttle</c>），多实例之间共享计数。
/// 只按账号计数主体（规范化邮箱的 SHA-256，见 <see cref="LoginThrottleRules.AccountSubjectHash"/>）记录；
/// 未执行迁移时各方法抛出 503 <c>AUTH_THROTTLE_SCHEMA_NOT_MIGRATED</c>。
/// </summary>
public interface ILoginThrottleStore
{
    /// <summary>只读判定账号是否处于锁定期：不加锁、不写入，没有计数行即放行。</summary>
    /// <param name="accountHash">账号计数主体（64 位小写 hex）。</param>
    /// <param name="now">当前 UTC 时间。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<LoginThrottleDecision> CheckAsync(string accountHash, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>
    /// 记录一次登录结果。失败：原子递增连续失败数并按规则设置锁定截止时间（并发失败不丢计数）；
    /// 成功：已有计数行时清零并清除锁定，没有计数行时不写入。
    /// </summary>
    /// <param name="accountHash">账号计数主体（64 位小写 hex）。</param>
    /// <param name="succeeded">本次登录是否成功。</param>
    /// <param name="now">当前 UTC 时间。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task RecordResultAsync(string accountHash, bool succeeded, DateTimeOffset now, CancellationToken cancellationToken = default);
}
