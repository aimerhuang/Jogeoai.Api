using Jogeoai.Application;

namespace Jogeoai.Tests;

/// <summary>登录失败频控纯规则：只按账号统计连续失败，阈值 30，锁定 1、2、4、8、15、15… 分钟，成功复位。</summary>
public sealed class LoginThrottleRulesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 1, 0, 0, TimeSpan.Zero);
    private static readonly LoginThrottleOptions Defaults = new();

    [Fact]
    public void DefaultsMatchTheConfirmedThresholds()
    {
        Assert.Equal(30, Defaults.AccountFailureThreshold);
        Assert.Equal(TimeSpan.FromMinutes(1), Defaults.AccountBaseBackoff);
        Assert.Equal(TimeSpan.FromMinutes(15), Defaults.AccountMaxBackoff);
        Assert.True(Defaults.IsValid());
        Assert.Equal("Auth:LoginThrottle", LoginThrottleOptions.SectionName);
    }

    [Fact]
    public void TwentyNineFailuresDoNotLockAndTheThirtiethLocksForOneMinute()
    {
        LoginThrottleState? state = null;
        for (var i = 1; i <= 29; i++)
        {
            state = LoginThrottleRules.RecordFailure(state, T0, Defaults);
            Assert.Equal(i, state.ConsecutiveFailures);
            Assert.Null(state.LockedUntil);
            Assert.True(LoginThrottleRules.Evaluate(state, T0).IsAllowed);
        }

        state = LoginThrottleRules.RecordFailure(state, T0, Defaults);
        Assert.Equal(30, state.ConsecutiveFailures);
        Assert.Equal(T0.AddMinutes(1), state.LockedUntil);
        var decision = LoginThrottleRules.Evaluate(state, T0);
        Assert.Equal(LoginThrottleOutcome.AccountLocked, decision.Outcome);
        Assert.Equal(TimeSpan.FromMinutes(1), decision.RetryAfter);
        Assert.Equal(60, decision.RetryAfterSeconds);
    }

    [Theory]
    [InlineData(1, null)]
    [InlineData(29, null)]
    [InlineData(30, 1)]
    [InlineData(31, 2)]
    [InlineData(32, 4)]
    [InlineData(33, 8)]
    [InlineData(34, 15)]
    [InlineData(35, 15)]
    [InlineData(100, 15)]
    [InlineData(int.MaxValue, 15)]
    public void BackoffDoublesFromOneMinuteAndCapsAtFifteen(int failures, int? minutes)
        => Assert.Equal(minutes is null ? null : TimeSpan.FromMinutes(minutes.Value),
            LoginThrottleRules.LockDuration(failures, Defaults));

    [Fact]
    public void LockedUntilIsComputedFromTheFinalCountAfterEachExpiry()
    {
        LoginThrottleState? state = new(29, null);
        var now = T0;
        foreach (var minutes in new[] { 1, 2, 4, 8, 15, 15 })
        {
            state = LoginThrottleRules.RecordFailure(state, now, Defaults);
            Assert.Equal(now.AddMinutes(minutes), state.LockedUntil);
            now = state.LockedUntil!.Value; // 锁定到期后才可能出现下一次失败。
        }
    }

    [Fact]
    public void LockRejectsUntilExpiryAndThenAllows()
    {
        var state = new LoginThrottleState(30, T0.AddMinutes(1));
        Assert.False(LoginThrottleRules.Evaluate(state, T0.AddSeconds(59.5)).IsAllowed);
        Assert.Equal(1, LoginThrottleRules.Evaluate(state, T0.AddSeconds(59.5)).RetryAfterSeconds);
        Assert.Equal(31, LoginThrottleRules.Evaluate(state, T0.AddSeconds(29.2)).RetryAfterSeconds);
        Assert.True(LoginThrottleRules.Evaluate(state, T0.AddMinutes(1)).IsAllowed);
        Assert.True(LoginThrottleRules.Evaluate(state, T0.AddMinutes(2)).IsAllowed);
        Assert.True(LoginThrottleRules.Evaluate(null, T0).IsAllowed);
        Assert.Equal(0, LoginThrottleDecision.Allowed.RetryAfterSeconds);
    }

    [Fact]
    public void SuccessResetsCountAndLockAndCreatesNoStateWhenNoneExists()
    {
        Assert.Null(LoginThrottleRules.RecordSuccess(null));
        var reset = LoginThrottleRules.RecordSuccess(new LoginThrottleState(31, T0.AddMinutes(2)));
        Assert.Equal(new LoginThrottleState(0, null), reset);

        // 复位后需要重新累计 30 次才会锁定。
        LoginThrottleState? state = reset;
        for (var i = 0; i < 29; i++) state = LoginThrottleRules.RecordFailure(state, T0, Defaults);
        Assert.Null(state!.LockedUntil);
        Assert.NotNull(LoginThrottleRules.RecordFailure(state, T0, Defaults).LockedUntil);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(0.001, 1)]
    [InlineData(1, 1)]
    [InlineData(1.001, 2)]
    [InlineData(900, 900)]
    public void RetryAfterRoundsUpToWholeSecondsWithAMinimumOfOne(double seconds, int expected)
        => Assert.Equal(expected, LoginThrottleRules.RetryAfterSeconds(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void CustomOptionsAreHonoured()
    {
        var options = new LoginThrottleOptions
        {
            AccountFailureThreshold = 3, AccountBaseBackoff = TimeSpan.FromSeconds(10), AccountMaxBackoff = TimeSpan.FromSeconds(25)
        };
        Assert.Null(LoginThrottleRules.LockDuration(2, options));
        Assert.Equal(TimeSpan.FromSeconds(10), LoginThrottleRules.LockDuration(3, options));
        Assert.Equal(TimeSpan.FromSeconds(20), LoginThrottleRules.LockDuration(4, options));
        Assert.Equal(TimeSpan.FromSeconds(25), LoginThrottleRules.LockDuration(5, options));
    }

    [Theory]
    [InlineData(0, 60, 900)]
    [InlineData(30, 0, 900)]
    [InlineData(30, 60, 30)]
    public void InvalidOptionsAreRejected(int threshold, int baseSeconds, int maxSeconds)
        => Assert.False(new LoginThrottleOptions
        {
            AccountFailureThreshold = threshold,
            AccountBaseBackoff = TimeSpan.FromSeconds(baseSeconds),
            AccountMaxBackoff = TimeSpan.FromSeconds(maxSeconds)
        }.IsValid());

    [Fact]
    public void AccountSubjectHashIsLowercaseSha256HexOfTheNormalizedEmail()
    {
        var hash = LoginThrottleRules.AccountSubjectHash("owner@example.test");
        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.Equal(hash, LoginThrottleRules.AccountSubjectHash("owner@example.test"));
        Assert.NotEqual(hash, LoginThrottleRules.AccountSubjectHash("other@example.test"));
        Assert.DoesNotContain("owner", hash);
    }
}
