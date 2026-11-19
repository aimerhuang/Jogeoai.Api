using System.Security.Cryptography;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;

namespace Jogeoai.Tests;

/// <summary>
/// 登录凭据校验的计时差消除：未知用户、非 active 用户与已知用户都恰好调用一次密码校验；
/// 前两种对静态假散列校验并返回 false。用计数委托观察，不连数据库。
/// </summary>
public sealed class IdentityCredentialCheckTests
{
    private const string RealHash = "pbkdf2-sha256$210000$c2FsdA==$aGFzaA==";

    [Fact]
    public void UnknownUserStillVerifiesOnceAgainstTheDummyHash()
    {
        var calls = new List<(string Password, string Hash)>();
        Assert.False(IdentityStore.CheckCredentials(null, "any-password", Counting(calls, result: true)));
        var call = Assert.Single(calls);
        Assert.Equal("any-password", call.Password);
        Assert.Equal(IdentityStore.DummyPasswordHash, call.Hash);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("pending")]
    [InlineData("deleted")]
    public void InactiveUserStillVerifiesOnceAndIsRejectedEvenWhenThePasswordMatches(string status)
    {
        var calls = new List<(string Password, string Hash)>();
        Assert.False(IdentityStore.CheckCredentials(Row(status), "right-password", Counting(calls, result: true)));
        var call = Assert.Single(calls);
        Assert.Equal(IdentityStore.DummyPasswordHash, call.Hash);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ActiveUserVerifiesOnceAgainstItsOwnHash(bool matches)
    {
        var calls = new List<(string Password, string Hash)>();
        Assert.Equal(matches, IdentityStore.CheckCredentials(Row("active"), "pw", Counting(calls, matches)));
        var call = Assert.Single(calls);
        Assert.Equal(RealHash, call.Hash);
    }

    [Fact]
    public void DummyHashHasTheSameFormatAndCostAsARealHash()
    {
        var parts = IdentityStore.DummyPasswordHash.Split('$');
        Assert.Equal(4, parts.Length);
        Assert.Equal("pbkdf2-sha256", parts[0]);
        Assert.Equal("210000", parts[1]);
        Assert.Equal(16, Convert.FromBase64String(parts[2]).Length);
        Assert.Equal(32, Convert.FromBase64String(parts[3]).Length);

        // 用与生产相同的 PBKDF2 参数确认假散列可被完整计算且不会匹配常见密码。
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        foreach (var password in new[] { "", "password", "12345678", "wrong-password" })
        {
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, 210_000, HashAlgorithmName.SHA256, expected.Length);
            Assert.False(CryptographicOperations.FixedTimeEquals(actual, expected));
        }
    }

    [Fact]
    public void NormalizeEmailIsSharedWithTheThrottleSubject()
    {
        Assert.Equal("owner@example.test", IdentityStore.NormalizeEmail("  Owner@Example.TEST "));
        var exception = Assert.Throws<CollectorStoreException>(() => IdentityStore.NormalizeEmail("not-an-email"));
        Assert.Equal("INVALID_EMAIL", exception.Code);
        Assert.Equal(400, exception.StatusCode);
    }

    private static IdentityStore.UserRow Row(string status)
        => new(Guid.NewGuid(), "owner@example.test", RealHash, "Owner", status, false);

    private static Func<string, string, bool> Counting(List<(string Password, string Hash)> calls, bool result)
        => (password, hash) =>
        {
            calls.Add((password, hash));
            return result;
        };
}
