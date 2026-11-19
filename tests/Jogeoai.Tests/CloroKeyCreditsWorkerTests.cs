using Jogeoai.Domain;
using Jogeoai.Providers.Abstractions;
using Jogeoai.Providers.Cloro;
using Jogeoai.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Jogeoai.Tests;

public sealed class CloroKeyCreditsWorkerTests
{
    private static readonly DateTimeOffset Reset = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RefreshAppliesBalancesMarksInvalidKeysAndOnlyRecordsOtherFailures()
    {
        var pool = new InMemoryCloroKeyPool();
        var funded = pool.Add("funded", "key-funded-0001");
        var empty = pool.Add("empty", "key-empty-00002");
        var revoked = pool.Add("revoked", "key-revoked-003");
        var flaky = pool.Add("flaky", "key-flaky-00004");
        var disabled = pool.Add("disabled", "key-disabled-05", enabled: false);
        var credits = new FakeCreditsClient();
        credits.Results[funded.KeyId] = () => new CloroCreditsSnapshot(250, 500, Reset, DateTimeOffset.UtcNow);
        credits.Results[empty.KeyId] = () => new CloroCreditsSnapshot(0, 500, Reset, DateTimeOffset.UtcNow);
        credits.Results[revoked.KeyId] = () => throw new CloroProviderException("CLORO_KEY_INVALID", "rejected", false);
        credits.Results[flaky.KeyId] = () => throw new CloroProviderException("CLORO_CREDITS_FAILED", "unavailable", true);
        var worker = new CloroKeyCreditsWorker(NullLogger<CloroKeyCreditsWorker>.Instance, pool,
            new CloroKeyCreditsRefresher(pool, credits, NullLogger<CloroKeyCreditsRefresher>.Instance),
            Options.Create(new CloroKeyPoolOptions()));

        Assert.Equal(2, await worker.RefreshDueAsync());

        Assert.Equal(new[] { funded.KeyId, empty.KeyId }, pool.Applied.Select(item => item.KeyId).ToArray());
        Assert.Equal("exhausted", pool.Find(empty.KeyId).Status);
        var rejection = Assert.Single(pool.Rejections);
        Assert.Equal((revoked.KeyId, CloroKeyRejection.Unauthorized), (rejection.KeyId, rejection.Rejection));
        Assert.Equal((flaky.KeyId, "CLORO_CREDITS_FAILED"), Assert.Single(pool.CreditFailures));
        Assert.DoesNotContain(disabled.KeyId, credits.Calls);
        Assert.Equal("unknown", pool.Find(flaky.KeyId).Status);
    }

    [Theory]
    [InlineData(CloroKeyStatus.Unknown, false, null, 100, CloroKeyStatus.Active)]
    [InlineData(CloroKeyStatus.Invalid, false, null, 50, CloroKeyStatus.Active)]
    [InlineData(CloroKeyStatus.Active, false, 100, 0, CloroKeyStatus.Exhausted)]
    [InlineData(CloroKeyStatus.Active, false, 100, -1, CloroKeyStatus.Exhausted)]
    [InlineData(CloroKeyStatus.Cooling, true, 100, 90, CloroKeyStatus.Cooling)]
    [InlineData(CloroKeyStatus.Cooling, false, 100, 90, CloroKeyStatus.Active)]
    [InlineData(CloroKeyStatus.Cooling, true, 100, 0, CloroKeyStatus.Exhausted)]
    [InlineData(CloroKeyStatus.Exhausted, false, null, 3, CloroKeyStatus.Active)]
    [InlineData(CloroKeyStatus.Exhausted, false, 0, 500, CloroKeyStatus.Active)]
    [InlineData(CloroKeyStatus.Exhausted, false, 3, 503, CloroKeyStatus.Active)]
    [InlineData(CloroKeyStatus.Exhausted, false, 3, 3, CloroKeyStatus.Exhausted)]
    public void CreditsRefreshTransitionsFollowThresholdAndRecovery(CloroKeyStatus current, bool coolingActive,
        int? previous, int remaining, CloroKeyStatus expected)
        => Assert.Equal(expected, CloroKeyStatusRules.AfterCreditsRefresh(current, coolingActive, previous, Reset,
            remaining, Reset, 0));

    [Fact]
    public void ExhaustedAccountRecoversAfterCycleResetEvenWithSameBalanceAndUnknownRemainingKeepsStatus()
    {
        Assert.Equal(CloroKeyStatus.Active, CloroKeyStatusRules.AfterCreditsRefresh(CloroKeyStatus.Exhausted, false, 3, Reset,
            3, Reset.AddMonths(1), 0));
        Assert.Equal(CloroKeyStatus.Exhausted, CloroKeyStatusRules.AfterCreditsRefresh(CloroKeyStatus.Exhausted, false, 3, Reset,
            null, Reset.AddMonths(1), 0));
        Assert.Equal(CloroKeyStatus.Exhausted, CloroKeyStatusRules.AfterCreditsRefresh(CloroKeyStatus.Active, false, 200, Reset,
            50, Reset, 50));
    }

    [Fact]
    public void KeyFormatTrimsValidatesAndNeverEchoesTheKey()
    {
        Assert.Equal("cloro-key-aaaa-1111", CloroKeyFormat.NormalizeApiKey("  cloro-key-aaaa-1111\n"));
        Assert.Equal("1111", CloroKeyFormat.Hint("cloro-key-aaaa-1111"));
        Assert.Equal("****", CloroKeyFormat.Hint("short-k1"));
        var invalid = Assert.Throws<ArgumentException>(() => CloroKeyFormat.NormalizeApiKey("cloro key with spaces"));
        Assert.DoesNotContain("spaces", invalid.Message);
        Assert.Throws<ArgumentException>(() => CloroKeyFormat.NormalizeLabel(" "));
        Assert.Throws<ArgumentException>(() => CloroKeyFormat.NormalizeLabel(new string('a', 65)));
        Assert.Throws<ArgumentException>(() => CloroKeyFormat.ValidatePriority(-1));
    }
}
