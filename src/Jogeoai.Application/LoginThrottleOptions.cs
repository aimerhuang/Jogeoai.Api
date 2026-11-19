namespace Jogeoai.Application;

/// <summary>
/// 登录失败频控参数（配置节 <c>Auth:LoginThrottle</c>）。只按账号（规范化邮箱）统计连续失败，不设 IP 维度、不限制访问次数：
/// 第 n 次连续失败（n ≥ <see cref="AccountFailureThreshold"/>）后锁定
/// min(<see cref="AccountBaseBackoff"/> × 2^(n − 阈值), <see cref="AccountMaxBackoff"/>)；成功登录复位。
/// </summary>
public sealed class LoginThrottleOptions
{
    /// <summary>配置节名称。</summary>
    public const string SectionName = "Auth:LoginThrottle";

    /// <summary>账号连续失败达到该次数后开始锁定，默认 30。</summary>
    public int AccountFailureThreshold { get; set; } = 30;

    /// <summary>第一次锁定的时长，默认 1 分钟；之后每多失败一次翻倍。</summary>
    public TimeSpan AccountBaseBackoff { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>锁定时长上限，默认 15 分钟；不会永久锁号。</summary>
    public TimeSpan AccountMaxBackoff { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>参数是否有效：阈值至少为 1，首次锁定时长大于 0，上限不小于首次时长。</summary>
    public bool IsValid() => AccountFailureThreshold >= 1 && AccountBaseBackoff > TimeSpan.Zero
        && AccountMaxBackoff >= AccountBaseBackoff;
}
