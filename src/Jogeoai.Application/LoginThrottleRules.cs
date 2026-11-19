using System.Security.Cryptography;
using System.Text;

namespace Jogeoai.Application;

/// <summary>登录频控对单个账号的判定结果类型。</summary>
public enum LoginThrottleOutcome
{
    /// <summary>放行，可以继续校验密码。</summary>
    Allowed,

    /// <summary>账号处于锁定期，直接拒绝，不校验密码。</summary>
    AccountLocked
}

/// <summary>登录频控判定。</summary>
/// <param name="Outcome">放行或账号锁定。</param>
/// <param name="RetryAfter">锁定剩余时长；放行时为零。</param>
public sealed record LoginThrottleDecision(LoginThrottleOutcome Outcome, TimeSpan RetryAfter)
{
    /// <summary>放行。</summary>
    public static LoginThrottleDecision Allowed { get; } = new(LoginThrottleOutcome.Allowed, TimeSpan.Zero);

    /// <summary>账号锁定，附剩余时长。</summary>
    /// <param name="retryAfter">锁定剩余时长。</param>
    public static LoginThrottleDecision AccountLocked(TimeSpan retryAfter) => new(LoginThrottleOutcome.AccountLocked, retryAfter);

    /// <summary>是否放行。</summary>
    public bool IsAllowed => Outcome == LoginThrottleOutcome.Allowed;

    /// <summary><c>Retry-After</c> 响应头使用的秒数：向上取整，至少 1 秒；放行时为 0。</summary>
    public int RetryAfterSeconds => IsAllowed ? 0 : LoginThrottleRules.RetryAfterSeconds(RetryAfter);
}

/// <summary>一个计数主体（本期只有账号）的持久化状态。</summary>
/// <param name="ConsecutiveFailures">连续失败次数。</param>
/// <param name="LockedUntil">锁定截止时间（UTC）；为空表示未锁定。</param>
public sealed record LoginThrottleState(int ConsecutiveFailures, DateTimeOffset? LockedUntil);

/// <summary>
/// 登录失败频控的纯规则（无 IO）。只统计失败、只按账号计数：第 n 次连续失败（n ≥ 阈值）后锁定
/// min(首次时长 × 2^(n − 阈值), 上限)，默认依次 1、2、4、8、15、15… 分钟；成功登录复位；锁定到期自动放行。
/// </summary>
public static class LoginThrottleRules
{
    /// <summary>账号维度的 scope 值（迁移 045 <c>auth_login_throttle.scope</c>）。</summary>
    public const string AccountScope = "account";

    /// <summary>迁移 045 未执行时登录返回的错误码（503）。</summary>
    public const string SchemaNotMigratedCode = "AUTH_THROTTLE_SCHEMA_NOT_MIGRATED";

    /// <summary>账号计数主体：规范化邮箱的 SHA-256 小写 hex（64 字符）。库与日志都不保存邮箱明文。</summary>
    /// <param name="normalizedEmail">已规范化的邮箱。</param>
    public static string AccountSubjectHash(string normalizedEmail)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedEmail))).ToLowerInvariant();

    /// <summary>根据当前状态判定是否放行：<paramref name="state"/> 为空、未锁定或锁定已到期时放行。</summary>
    /// <param name="state">当前状态，可为空。</param>
    /// <param name="now">当前 UTC 时间。</param>
    public static LoginThrottleDecision Evaluate(LoginThrottleState? state, DateTimeOffset now)
        => state?.LockedUntil is { } lockedUntil && lockedUntil > now
            ? LoginThrottleDecision.AccountLocked(lockedUntil - now)
            : LoginThrottleDecision.Allowed;

    /// <summary>记录一次失败：连续失败数加一，达到阈值后按退避规则设置锁定截止时间。</summary>
    /// <param name="state">当前状态，可为空（首次失败）。</param>
    /// <param name="now">当前 UTC 时间。</param>
    /// <param name="options">频控参数。</param>
    public static LoginThrottleState RecordFailure(LoginThrottleState? state, DateTimeOffset now, LoginThrottleOptions options)
    {
        var failures = state is null ? 1 : state.ConsecutiveFailures >= int.MaxValue ? int.MaxValue : state.ConsecutiveFailures + 1;
        return new LoginThrottleState(failures, LockedUntilAfterFailure(failures, now, options));
    }

    /// <summary>根据记录本次失败后的连续失败数计算锁定截止时间；未达阈值时为空。</summary>
    /// <param name="consecutiveFailures">包含本次在内的连续失败数。</param>
    /// <param name="now">当前 UTC 时间。</param>
    /// <param name="options">频控参数。</param>
    public static DateTimeOffset? LockedUntilAfterFailure(int consecutiveFailures, DateTimeOffset now, LoginThrottleOptions options)
        => LockDuration(consecutiveFailures, options) is { } duration ? now + duration : null;

    /// <summary>记录一次成功：复位为零且清除锁定；没有状态时保持为空（成功不产生计数行）。</summary>
    /// <param name="state">当前状态，可为空。</param>
    public static LoginThrottleState? RecordSuccess(LoginThrottleState? state)
        => state is null ? null : new LoginThrottleState(0, null);

    /// <summary>第 <paramref name="consecutiveFailures"/> 次连续失败后的锁定时长；未达阈值时为空。</summary>
    /// <param name="consecutiveFailures">连续失败数。</param>
    /// <param name="options">频控参数。</param>
    public static TimeSpan? LockDuration(int consecutiveFailures, LoginThrottleOptions options)
    {
        if (consecutiveFailures < options.AccountFailureThreshold) return null;
        var exponent = consecutiveFailures - options.AccountFailureThreshold;
        var baseTicks = options.AccountBaseBackoff.Ticks;
        var maxTicks = options.AccountMaxBackoff.Ticks;
        // 先判断再移位，避免溢出（C# 对 long 的移位位数按 64 取模）。
        if (baseTicks >= maxTicks || exponent >= 62 || baseTicks > maxTicks >> exponent)
            return options.AccountMaxBackoff;
        return TimeSpan.FromTicks(baseTicks << exponent);
    }

    /// <summary>剩余时长换算为 <c>Retry-After</c> 秒数：向上取整，至少 1 秒。</summary>
    /// <param name="remaining">剩余时长。</param>
    public static int RetryAfterSeconds(TimeSpan remaining)
        => remaining <= TimeSpan.Zero ? 1 : (int)Math.Max(1, Math.Ceiling(remaining.TotalSeconds));
}
