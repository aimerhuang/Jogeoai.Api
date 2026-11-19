using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Jogeoai.Infrastructure;

namespace Jogeoai.Tests;

/// <summary>纯函数：自然月锚点、卡密生成 / 规范化 / 哈希、失败限流。</summary>
public sealed class SubscriptionLogicTests
{
    [Theory]
    [InlineData("2026-01-31T09:30:00", 1, "2026-02-28T09:30:00")]
    [InlineData("2024-01-31T09:30:00", 1, "2024-02-29T09:30:00")]
    [InlineData("2026-03-31T00:00:00", 1, "2026-04-30T00:00:00")]
    [InlineData("2026-01-31T00:00:00", 2, "2026-03-31T00:00:00")]
    [InlineData("2026-01-31T00:00:00", 12, "2027-01-31T00:00:00")]
    [InlineData("2026-12-15T23:59:59", 1, "2027-01-15T23:59:59")]
    [InlineData("2026-10-19T08:00:00", 36, "2029-10-19T08:00:00")]
    [InlineData("2026-03-31T08:00:00", -1, "2026-02-28T08:00:00")]
    public void AddMonthsClampedAnchorsOnTheActivationDayAndClampsToMonthEnd(string anchor, int months, string expected)
    {
        var start = DateTime.SpecifyKind(DateTime.Parse(anchor, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);
        var result = SubscriptionStore.AddMonthsClamped(start, months);
        Assert.Equal(DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), result);
        Assert.Equal(DateTimeKind.Utc, result.Kind);
    }

    [Fact]
    public void AddMonthsClampedDoesNotDriftWhenAlwaysComputedFromTheAnchor()
    {
        var anchor = new DateTime(2026, 1, 31, 10, 0, 0, DateTimeKind.Utc);
        // 以开通日为锚：第 3 周期结束仍是 4 月 30 日，而不是 2 月 28 日累加出来的 3 月 28 → 4 月 28。
        Assert.Equal(new DateTime(2026, 4, 30, 10, 0, 0, DateTimeKind.Utc), SubscriptionStore.AddMonthsClamped(anchor, 3));
        Assert.Equal(new DateTime(2026, 5, 31, 10, 0, 0, DateTimeKind.Utc), SubscriptionStore.AddMonthsClamped(anchor, 4));
    }

    [Fact]
    public void GeneratedCodesUseTheUnambiguousAlphabetAndTheDocumentedShape()
    {
        var pattern = new Regex("^JG-[ABCDEFGHJKLMNPQRSTUVWXYZ23456789]{4}(-[ABCDEFGHJKLMNPQRSTUVWXYZ23456789]{4}){3}$");
        var codes = Enumerable.Range(0, 200).Select(_ => PlanCodeFormat.Generate()).ToArray();
        Assert.All(codes, code => Assert.Matches(pattern, code));
        Assert.Equal(codes.Length, codes.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(32, PlanCodeFormat.Alphabet.Length);
        Assert.DoesNotContain(PlanCodeFormat.Alphabet, c => c is 'I' or 'O' or '0' or '1');
    }

    [Theory]
    [InlineData("JG-ABCD-EFGH-JKLM-NPQR", true, "JG-ABCD-EFGH-JKLM-NPQR")]
    [InlineData("  jg-abcd-efgh-jklm-npqr\n", true, "JG-ABCD-EFGH-JKLM-NPQR")]
    [InlineData("ABCDEFGHJKLMNPQR", true, "JG-ABCD-EFGH-JKLM-NPQR")]
    [InlineData("abcd efgh jklm npqr", true, "JG-ABCD-EFGH-JKLM-NPQR")]
    [InlineData("JG_ABCD_EFGH_JKLM_NPQR", true, "JG-ABCD-EFGH-JKLM-NPQR")]
    [InlineData("JG-ABCD-EFGH-JKLM-NPQ0", false, "")]
    [InlineData("JG-ABCD-EFGH-JKLM-NPQ", false, "")]
    [InlineData("JG-ABCD-EFGH-JKLM-NPQRS", false, "")]
    [InlineData("", false, "")]
    [InlineData(null, false, "")]
    public void NormalizationIsCaseAndSeparatorInsensitiveAndRejectsConfusableCharacters(string? input, bool expected, string normalized)
    {
        Assert.Equal(expected, PlanCodeFormat.TryNormalize(input, out var actual));
        Assert.Equal(normalized, actual);
    }

    [Fact]
    public void HashIsSha256OfTheCanonicalFormAndHintIsTheLastFourCharacters()
    {
        var code = "JG-ABCD-EFGH-JKLM-NPQR";
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code))).ToLowerInvariant();
        Assert.Equal(expected, PlanCodeFormat.Hash(code));
        Assert.Matches("^[0-9a-f]{64}$", PlanCodeFormat.Hash(code));
        Assert.Equal("NPQR", PlanCodeFormat.Hint(code));
        Assert.True(PlanCodeFormat.TryNormalize("jg abcd efgh jklm npqr", out var normalized));
        Assert.Equal(PlanCodeFormat.Hash(code), PlanCodeFormat.Hash(normalized));
    }

    [Fact]
    public void FailureLimiterCountsOnlyFailuresPerKeyWithinASlidingWindow()
    {
        var limiter = new RedeemFailureLimiter(TimeSpan.FromHours(1));
        var start = new DateTimeOffset(2026, 10, 19, 8, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 9; i++) limiter.RecordFailure("tenant:a", start.AddMinutes(i));
        Assert.False(limiter.IsLimited("tenant:a", 10, start.AddMinutes(9)));
        limiter.RecordFailure("tenant:a", start.AddMinutes(9));
        Assert.True(limiter.IsLimited("tenant:a", 10, start.AddMinutes(10)));
        Assert.False(limiter.IsLimited("ip:1.2.3.4", 10, start.AddMinutes(10)));
        // 最早一次失败滑出窗口后恢复。
        Assert.False(limiter.IsLimited("tenant:a", 10, start.AddMinutes(60).AddSeconds(30)));
        Assert.True(limiter.IsLimited("tenant:a", 9, start.AddMinutes(60).AddSeconds(30)));
    }
}
