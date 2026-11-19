using System.Text.Json;
using Jogeoai.Domain;

namespace Jogeoai.Tests;

/// <summary>
/// App 周期 Run 的纯规则（implement 4.3 / 4.4a / 4.5 / 4.6）：未采集原因码的映射、Run 状态汇总、排队序号、截止时点，
/// 以及提问能否进入 App 渠道的判定（人工排除优先）。不需要数据库。
/// </summary>
public sealed class AppCycleRunRulesTests
{
    [Theory]
    // Never blocked for another reason, never failed: it simply was not reached.
    [InlineData(0, null, null, AppNotCollectedCodes.Capacity)]
    [InlineData(0, "", "", AppNotCollectedCodes.Capacity)]
    [InlineData(0, "no_task", null, AppNotCollectedCodes.Capacity)]
    [InlineData(0, "binding_quota", null, AppNotCollectedCodes.Capacity)]
    [InlineData(0, "binding_cooldown", null, AppNotCollectedCodes.Capacity)]
    [InlineData(0, "platform_interval", null, AppNotCollectedCodes.Capacity)]
    [InlineData(0, "platform_daily_cap", null, AppNotCollectedCodes.Capacity)]
    [InlineData(0, "something_unknown", null, AppNotCollectedCodes.Capacity)]
    // The last reason a lease was refused.
    [InlineData(0, "credit_insufficient", null, AppNotCollectedCodes.Credit)]
    [InlineData(0, " Credit_Insufficient ", null, AppNotCollectedCodes.Credit)]
    [InlineData(0, "binding_stopped", null, AppNotCollectedCodes.Binding)]
    [InlineData(0, "platform_paused", null, AppNotCollectedCodes.Platform)]
    [InlineData(0, "platform_unavailable", null, AppNotCollectedCodes.Platform)]
    // The block reason is the later event when retries are not exhausted.
    [InlineData(1, "credit_insufficient", "collection_timeout", AppNotCollectedCodes.Credit)]
    [InlineData(2, "platform_paused", "app_crashed", AppNotCollectedCodes.Platform)]
    // Attempts were made and failed.
    [InlineData(1, null, "collection_timeout", AppNotCollectedCodes.Failed)]
    [InlineData(2, "no_task", "app_crashed", AppNotCollectedCodes.Failed)]
    [InlineData(0, null, "app_crashed", AppNotCollectedCodes.Failed)]
    [InlineData(0, null, "an_unknown_failure", AppNotCollectedCodes.Failed)]
    // Retries exhausted wins over everything.
    [InlineData(3, null, "collection_timeout", AppNotCollectedCodes.Failed)]
    [InlineData(3, "credit_insufficient", "collection_timeout", AppNotCollectedCodes.Failed)]
    [InlineData(7, "platform_paused", null, AppNotCollectedCodes.Failed)]
    // Failures that stop the binding and do not count as task failures.
    [InlineData(0, null, "login_required", AppNotCollectedCodes.Binding)]
    [InlineData(0, null, "challenge_required", AppNotCollectedCodes.Binding)]
    [InlineData(0, null, "contract_changed", AppNotCollectedCodes.Binding)]
    [InlineData(0, null, "contract_unverified", AppNotCollectedCodes.Binding)]
    [InlineData(0, null, "version_mismatch", AppNotCollectedCodes.Binding)]
    [InlineData(0, null, "device_offline", AppNotCollectedCodes.Binding)]
    [InlineData(0, null, "emulator_rejected", AppNotCollectedCodes.Platform)]
    [InlineData(0, null, "rate_limited", AppNotCollectedCodes.Capacity)]
    public void NotCollectedReasonComesFromFailuresThenTheLastBlockThenTheLastError(int failures, string? block, string? error, string expected)
    {
        var code = AppNotCollectedRules.Resolve(failures, block, error);
        Assert.Equal(expected, code);
        Assert.True(AppNotCollectedCodes.IsNotCollected(code));
    }

    [Fact]
    public void EveryNotCollectedCodeHasItsOwnShortChineseMessageAndTheBlockReasonsAreLeaseReasons()
    {
        var messages = AppNotCollectedCodes.All.Select(AppNotCollectedRules.Message).ToArray();
        Assert.Equal(messages.Length, messages.Distinct(StringComparer.Ordinal).Count());
        Assert.All(messages, message =>
        {
            Assert.StartsWith("未采集", message, StringComparison.Ordinal);
            Assert.InRange(message.Length, 8, 60);
        });
        Assert.DoesNotContain(AppNotCollectedRules.Message("SOMETHING_ELSE"), messages);
        Assert.Equal(3, AppFailureRules.MaxTaskFailures);
        // The block reasons the mapping reads are the strings the lease answers with (contract.md section 9).
        var lease = AppContractNames.All<AppLeaseEmptyReason>();
        Assert.Contains(AppTaskBlockReasons.BindingStopped, lease);
        Assert.Contains(AppTaskBlockReasons.PlatformPaused, lease);
        Assert.Contains(AppTaskBlockReasons.PlatformUnavailable, lease);
        Assert.Equal("credit_insufficient", AppTaskBlockReasons.CreditInsufficient);
    }

    [Theory]
    // total, open (pending + running), succeeded, failed, cancelled
    [InlineData(4, 4, 0, 0, 0, "running")]
    [InlineData(4, 1, 3, 0, 0, "running")]
    [InlineData(4, 1, 0, 0, 3, "running")]
    [InlineData(4, 2, 1, 1, 0, "running")]
    [InlineData(4, 0, 4, 0, 0, "succeeded")]
    [InlineData(4, 0, 0, 0, 4, "cancelled")]
    [InlineData(4, 0, 0, 4, 0, "failed")]
    [InlineData(4, 0, 3, 0, 1, "partial_failed")]
    [InlineData(4, 0, 3, 1, 0, "partial_failed")]
    [InlineData(4, 0, 0, 1, 3, "partial_failed")]
    [InlineData(4, 0, 1, 1, 2, "partial_failed")]
    [InlineData(1, 0, 0, 0, 1, "cancelled")]
    public void RunStatusFollowsTheChecks(int total, int open, int succeeded, int failed, int cancelled, string expected)
        => Assert.Equal(expected, AppCycleRules.RunStatus(total, open, succeeded, failed, cancelled));

    [Fact]
    public void RunStatusNeedsAtLeastOneCheck()
        => Assert.Throws<ArgumentOutOfRangeException>(() => AppCycleRules.RunStatus(0, 0, 0, 0, 0));

    [Fact]
    public void FairRankIsThePromptOrdinalWithinThePlatformInCreationOrder()
    {
        var (a, b, c, d) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Guid[] created = [c, a, d, b];
        // doubao: every prompt; kimi: only b and c; qwen: only d. The pair order is not the creation order.
        (Guid, string)[] pairs =
        [
            (a, "doubao"), (b, "doubao"), (b, "kimi"), (c, "doubao"), (c, "kimi"), (d, "doubao"), (d, "qwen"),
        ];
        Assert.Equal([2, 4, 2, 1, 1, 3, 1], AppCycleRules.FairRanks(created, pairs));
        // The same input in another order gives every pair the same rank: the rank depends on the data only.
        var shuffled = pairs.Reverse().ToArray();
        Assert.Equal(new[] { 2, 4, 2, 1, 1, 3, 1 }.Reverse(), AppCycleRules.FairRanks(created, shuffled));
        // Ranks are dense per platform and start at 1.
        Assert.Equal([1, 2, 3], AppCycleRules.FairRanks(created, [(c, "kimi"), (a, "kimi"), (b, "kimi")]));
        // A prompt outside the creation list sorts after the known ones; two of them by their id.
        var (x, y) = (Guid.Parse("00000000-0000-0000-0000-00000000000a"), Guid.Parse("00000000-0000-0000-0000-00000000000b"));
        Assert.Equal([3, 2, 1], AppCycleRules.FairRanks([a], [(y, "kimi"), (x, "kimi"), (a, "kimi")]));
        Assert.Empty(AppCycleRules.FairRanks(created, []));
    }

    [Fact]
    public void TheDeadlineIsTheSlotLocalTimeSevenDaysLater()
    {
        var shanghai = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
        Assert.Equal(new DateTimeOffset(2000, 1, 10, 1, 11, 0, TimeSpan.Zero), AppCycleRules.DeadlineUtc("20000103T0911", shanghai));
        Assert.Equal(new DateTimeOffset(2026, 11, 23, 1, 11, 0, TimeSpan.Zero), AppCycleRules.DeadlineUtc("20261116T0911", shanghai));
        Assert.Equal(TimeSpan.Zero, AppCycleRules.DeadlineUtc("20261116T0911", shanghai).Offset);
        // The local wall-clock time is kept across a daylight-saving change, so the UTC distance is not always 168 hours:
        // New York leaves daylight time on 2026-11-01 (09:11 EDT = 13:11 UTC, 09:11 EST = 14:11 UTC).
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 14, 11, 0, TimeSpan.Zero), AppCycleRules.DeadlineUtc("20261026T0911", newYork));
        Assert.Equal(7, AppCycleRules.CycleDays);
        Assert.Throws<FormatException>(() => AppCycleRules.DeadlineUtc("2026-11-16", shanghai));
    }

    [Fact]
    public void AnOperatorExclusionWinsOverTheRuleAndTheViewOmitsEligibilityWhenItIsNotSet()
    {
        Assert.Equal(new AppPromptEligibility(true), AppPromptRules.Eligibility("哪个记账工具好用？", false));
        Assert.Equal(new AppPromptEligibility(false, AppPromptRules.ReasonContainsUrl), AppPromptRules.Eligibility("看看 https://example.com", false));
        Assert.Equal(new AppPromptEligibility(false, AppPromptRules.ReasonTooLong), AppPromptRules.Eligibility(new string('问', AppPromptRules.MaxLength + 1), false));
        // Excluded by an operator: the reason is the exclusion, whether the text passes the rule or not.
        Assert.Equal(new AppPromptEligibility(false, "EXCLUDED_BY_OPERATOR"), AppPromptRules.Eligibility("哪个记账工具好用？", true));
        Assert.Equal(new AppPromptEligibility(false, "EXCLUDED_BY_OPERATOR"), AppPromptRules.Eligibility("看看 https://example.com", true));
        Assert.DoesNotContain(AppPromptRules.ReasonExcludedByOperator, AppPromptRules.ReasonCodes);

        // PromptView: the member is appended, optional, and left out of the JSON when it is not set (closed tenants).
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var prompt = new PromptView(Guid.NewGuid(), "q", 1, "zh-CN", true, [], "unbranded", "system", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Assert.Null(prompt.AppEligibility);
        Assert.DoesNotContain("appEligibility", JsonSerializer.Serialize(prompt, web), StringComparison.Ordinal);
        var open = JsonSerializer.SerializeToNode(prompt with { AppEligibility = new(false, "TOO_LONG") }, web)!.AsObject();
        Assert.Equal("appEligibility", open.Last().Key);
        Assert.Equal("""{"eligible":false,"reasonCode":"TOO_LONG"}""", open["appEligibility"]!.ToJsonString());
    }

    [Fact]
    public void TheStoreIsRegisteredInBothProcessesAndTheDeadlinePassRunsWhereverAppTasksAreCreated()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Jogeoai.slnx"))) directory = directory.Parent;
        string Source(params string[] path) => File.ReadAllText(Path.Combine([directory!.FullName, "src", .. path]));
        foreach (var project in new[] { "Jogeoai.Api", "Jogeoai.Worker" })
            Assert.Contains("builder.Services.AddSingleton<IAppCollectionStore, AppCollectionStore>();", Source(project, "Program.cs"), StringComparison.Ordinal);
        // The deadline pass (design 4.3) has its own hosted service, registered in the same block as the schedule worker that
        // creates App tasks: both the full and the MonitoringOnly Worker run it. The full-only web recovery loop must not run it
        // too (it once did, so a MonitoringOnly Worker created App tasks that never expired).
        Assert.Contains("appCollectionStore.ExpireOverdueTasksAsync(stoppingToken)", Source("Jogeoai.Worker", "AppDeadlineWorker.cs"), StringComparison.Ordinal);
        var worker = Source("Jogeoai.Worker", "Worker.cs");
        Assert.Contains("collectorStore.RecoverExpiredTasksAsync(stoppingToken)", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("ExpireOverdueTasksAsync", worker, StringComparison.Ordinal);
        var program = Source("Jogeoai.Worker", "Program.cs");
        const string schedule = "builder.Services.AddHostedService<CollectionScheduleWorker>();";
        const string deadline = "builder.Services.AddHostedService<AppDeadlineWorker>();";
        Assert.Equal(1, program.Split(deadline).Length - 1);
        var (start, end) = (program.IndexOf(schedule, StringComparison.Ordinal), program.IndexOf(deadline, StringComparison.Ordinal));
        Assert.True(start >= 0 && end > start, "AppDeadlineWorker is registered after CollectionScheduleWorker.");
        Assert.DoesNotContain(program[start..end], character => character is '{' or '}');
        // 4B replaced the 4A placeholder: nothing consumes a due App slot as APP_CYCLE_PENDING any more.
        Assert.DoesNotContain("APP_CYCLE_PENDING", Source("Jogeoai.Infrastructure", "CollectionScheduleStore.cs") + Source("Jogeoai.Infrastructure", "AppMonitoringPlan.cs"),
            StringComparison.Ordinal);
        Assert.Contains("AppCollectionStore.CreateCycleRunAsync(", Source("Jogeoai.Infrastructure", "CollectionScheduleStore.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheNewMembersAreDocumentedInChinese()
    {
        var xml = System.Xml.Linq.XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Jogeoai.Domain.xml"));
        string Param(string type, string name) => xml.Descendants("member").Single(m => (string?)m.Attribute("name") == $"T:Jogeoai.Domain.{type}")
            .Elements("param").Single(p => (string?)p.Attribute("name") == name).Value;
        foreach (var text in new[]
                 {
                     Param("PromptView", "AppEligibility"), Param("TeamMonitoringBrandView", "MonitoringStarted"),
                     Param("AppPromptEligibility", "Eligible"), Param("AppPromptEligibility", "ReasonCode"),
                     Param("AppPromptExclusionView", "Reason"), Param("AppPromptExclusionView", "RevokedAt"),
                 })
            Assert.Contains(text, c => c is >= '一' and <= '鿿');
        Assert.Contains("App", Param("TeamMonitoringBrandView", "MonitoringStarted"), StringComparison.Ordinal);
    }
}
