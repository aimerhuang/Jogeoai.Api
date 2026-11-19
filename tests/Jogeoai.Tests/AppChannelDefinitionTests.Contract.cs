using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Jogeoai.Domain;

namespace Jogeoai.Tests;

// App 契约类型：字符串取值与 contract.md 第 9 节一致，并与迁移 049 的 CHECK 约束一致。
public sealed partial class AppChannelDefinitionTests
{
    private static readonly JsonSerializerOptions EnumJson = new() { Converters = { new JsonStringEnumConverter() } };

    private static void AssertNames<TEnum>(params string[] expected) where TEnum : struct, Enum
    {
        Assert.Equal(expected, AppContractNames.All<TEnum>());
        var values = Enum.GetValues<TEnum>();
        Assert.Equal(expected.Length, values.Length);
        for (var i = 0; i < values.Length; i++)
        {
            // The storage string is the JSON string: what the API returns is what the CHECK constraints allow.
            Assert.Equal(expected[i], AppContractNames.ToStorage(values[i]));
            Assert.Equal($"\"{expected[i]}\"", JsonSerializer.Serialize(values[i], EnumJson));
            Assert.Equal(values[i], JsonSerializer.Deserialize<TEnum>($"\"{expected[i]}\"", EnumJson));
            Assert.True(AppContractNames.TryParse<TEnum>($" {expected[i].ToUpperInvariant()} ", out var parsed));
            Assert.Equal(values[i], parsed);
        }
        Assert.False(AppContractNames.TryParse<TEnum>(null, out _));
        Assert.False(AppContractNames.TryParse<TEnum>("", out _));
        Assert.False(AppContractNames.TryParse<TEnum>("no_such_value", out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => AppContractNames.ToStorage((TEnum)Enum.ToObject(typeof(TEnum), 999)));
    }

    [Fact]
    public void ContractEnumsUseTheDocumentedWireNames()
    {
        AssertNames<CollectionChannel>("api", "app", "web");
        AssertNames<AppNodeStatus>("active", "disabled");
        AssertNames<AppBindingStatus>("active", "stopped");
        AssertNames<AppBindingStopReason>("login_required", "challenge_required", "device_offline", "contract_changed",
            "contract_unverified", "version_mismatch", "emulator_rejected", "disabled_by_operator");
        AssertNames<AppLeaseEmptyReason>("no_task", "binding_stopped", "binding_cooldown", "binding_interval", "binding_quota", "device_busy",
            "device_offline", "device_cooldown", "platform_interval", "platform_daily_cap", "platform_paused", "platform_unavailable", "credit_insufficient");
        AssertNames<AppFailureCode>("login_required", "challenge_required", "rate_limited", "emulator_rejected", "contract_changed",
            "contract_unverified", "version_mismatch", "device_offline", "app_crashed", "collection_timeout");
        AssertNames<AppCitationsStatus>("read", "absent", "unreadable", "not_declared");
        AssertNames<AppVisibleState>("on", "off", "unknown");
        AssertNames<AppDailyReportDeliveryStatus>("not_configured", "sent", "failed");
        Assert.Equal("ok", AppContractNames.HealthOk);
        // Failure phases and task dispositions reuse the web collector enums; their wire names are the contract's.
        Assert.Equal("[\"probe\",\"pre_send\",\"post_send\"]", JsonSerializer.Serialize(Enum.GetValues<CollectorPhase>(), EnumJson));
        Assert.Equal("[\"requeued\",\"failed\",\"stale\"]", JsonSerializer.Serialize(Enum.GetValues<CollectorTaskDisposition>(), EnumJson));
        Assert.Equal("[\"accepted\",\"duplicate\",\"stale\"]", JsonSerializer.Serialize(Enum.GetValues<CollectorResultDisposition>(), EnumJson));
    }

    [Fact]
    public void NotCollectedAndUnavailableCodesArePinned()
    {
        Assert.Equal(["APP_NOT_COLLECTED_CAPACITY", "APP_NOT_COLLECTED_CREDIT", "APP_NOT_COLLECTED_BINDING",
            "APP_NOT_COLLECTED_PLATFORM", "APP_NOT_COLLECTED_FAILED"], AppNotCollectedCodes.All);
        Assert.Equal(("APP_NOT_COLLECTED_CAPACITY", "APP_NOT_COLLECTED_CREDIT", "APP_NOT_COLLECTED_BINDING",
                "APP_NOT_COLLECTED_PLATFORM", "APP_NOT_COLLECTED_FAILED"),
            (AppNotCollectedCodes.Capacity, AppNotCollectedCodes.Credit, AppNotCollectedCodes.Binding,
                AppNotCollectedCodes.Platform, AppNotCollectedCodes.Failed));
        Assert.All(AppNotCollectedCodes.All, code => Assert.True(AppNotCollectedCodes.IsNotCollected(code)));
        foreach (var other in new[] { null, "", "app_not_collected_capacity", "APP_NOT_COLLECTED", "CLORO_TIMEOUT", "MONITORING_NOT_DUE" })
            Assert.False(AppNotCollectedCodes.IsNotCollected(other));
        // error_code is VARCHAR(100) on visibility_checks; unavailable_reason is VARCHAR(64) on the entry table.
        Assert.All(AppNotCollectedCodes.All, code => Assert.True(code.Length <= 64));
        Assert.Equal("APP_CONTENT_UNREADABLE", AppUnavailableReasons.ContentUnreadable);
        Assert.Equal("APP_EMULATOR_REJECTED", AppUnavailableReasons.EmulatorRejected);
    }

    [Theory]
    [InlineData(AppFailureCode.LoginRequired, AppBindingStopReason.LoginRequired, false)]
    [InlineData(AppFailureCode.ChallengeRequired, AppBindingStopReason.ChallengeRequired, false)]
    [InlineData(AppFailureCode.RateLimited, null, false)]
    [InlineData(AppFailureCode.EmulatorRejected, AppBindingStopReason.EmulatorRejected, false)]
    [InlineData(AppFailureCode.ContractChanged, AppBindingStopReason.ContractChanged, false)]
    [InlineData(AppFailureCode.ContractUnverified, AppBindingStopReason.ContractUnverified, false)]
    [InlineData(AppFailureCode.VersionMismatch, AppBindingStopReason.VersionMismatch, false)]
    [InlineData(AppFailureCode.DeviceOffline, AppBindingStopReason.DeviceOffline, false)]
    [InlineData(AppFailureCode.AppCrashed, null, true)]
    [InlineData(AppFailureCode.CollectionTimeout, null, true)]
    public void FailureCodesFollowTheContractTable(AppFailureCode code, AppBindingStopReason? stops, bool countsTowardFailures)
    {
        Assert.Equal(stops, AppFailureRules.BindingStopReason(code));
        Assert.Equal(countsTowardFailures, AppFailureRules.CountsTowardTaskFailures(code));
        // A failure that stops the binding carries the same wire name as the stop reason it produces.
        if (stops is { } reason) Assert.Equal(AppContractNames.ToStorage(code), AppContractNames.ToStorage(reason));
    }

    [Fact]
    public void FailureRulesCoverEveryCodeAndRejectUndefinedOnes()
    {
        Assert.Equal(3, AppFailureRules.MaxTaskFailures);
        foreach (var code in Enum.GetValues<AppFailureCode>())
        {
            _ = AppFailureRules.BindingStopReason(code);
            _ = AppFailureRules.CountsTowardTaskFailures(code);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => AppFailureRules.BindingStopReason((AppFailureCode)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => AppFailureRules.CountsTowardTaskFailures((AppFailureCode)999));
        // Stop reasons no failure code produces are set by other paths (health report, operator).
        var produced = Enum.GetValues<AppFailureCode>().Select(AppFailureRules.BindingStopReason).OfType<AppBindingStopReason>().ToHashSet();
        Assert.Equal([AppBindingStopReason.DisabledByOperator],
            Enum.GetValues<AppBindingStopReason>().Where(reason => !produced.Contains(reason)));
        // AC-C11: only the failures that leave the binding as it is count towards the task's failures.
        Assert.Equal([AppFailureCode.AppCrashed, AppFailureCode.CollectionTimeout],
            Enum.GetValues<AppFailureCode>().Where(AppFailureRules.CountsTowardTaskFailures));
    }

    // ---- 049 的 CHECK 约束与种子（把 SQL 文件当文本读，不连数据库）----

    private static string Migration049()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../db/migrations/049_app_channel.sql"));
        Assert.True(File.Exists(path), $"找不到 migration：{path}");
        return Regex.Replace(File.ReadAllText(path), @"--[^\r\n]*", "");
    }

    private static string[] Quoted(string list) => Regex.Matches(list, "'([^']*)'").Select(m => m.Groups[1].Value).ToArray();

    // The first IN (...) list of the CHECK constraint as it is defined (CREATE TABLE or ADD CONSTRAINT).
    private static string[] CheckValues(string sql, string constraint)
    {
        var matches = Regex.Matches(sql, $@"CONSTRAINT\s+{Regex.Escape(constraint)}\s+CHECK\b.*?\bIN\s*\(([^)]*)\)", RegexOptions.Singleline);
        Assert.True(matches.Count == 1, $"{constraint}: {matches.Count} definitions");
        var values = Quoted(matches[0].Groups[1].Value);
        Assert.NotEmpty(values);
        return values;
    }

    private static void AssertSameSet(IEnumerable<string> expected, IEnumerable<string> actual)
        => Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));

    [Fact]
    public void EnumStringsEqualTheCheckConstraintsOfMigration049()
    {
        var sql = Migration049();
        AssertSameSet(CheckValues(sql, "ck_collector_app_nodes_status"), AppContractNames.All<AppNodeStatus>());
        AssertSameSet(CheckValues(sql, "ck_collector_app_bindings_status"), AppContractNames.All<AppBindingStatus>());
        AssertSameSet(CheckValues(sql, "ck_collector_app_bindings_stop_reason"), AppContractNames.All<AppBindingStopReason>());
        AssertSameSet(CheckValues(sql, "ck_collector_app_daily_reports_delivery"), AppContractNames.All<AppDailyReportDeliveryStatus>());
        Assert.Contains("delivery_status VARCHAR(16) NOT NULL DEFAULT 'not_configured'", sql);
        Assert.Equal("not_configured", AppContractNames.ToStorage(AppDailyReportDeliveryStatus.NotConfigured));
        Assert.Contains("status VARCHAR(16) NOT NULL DEFAULT 'active'", sql);

        // Failure codes share collector_attempts.error_code with the web collector: every App code must be allowed,
        // and every value 049 added must be an App failure code.
        var failureCodes = CheckValues(sql, "ck_collector_attempts_failure_code");
        Assert.All(AppContractNames.All<AppFailureCode>(), code => Assert.Contains(code, failureCodes));
        string[] webCodes =
        [
            "login_required", "region_restricted", "challenge_required", "rate_limited", "page_contract_changed",
            "page_contract_unverified", "collection_timeout", "browser_failed", "operator_archived",
        ];
        AssertSameSet(failureCodes.Except(webCodes), AppContractNames.All<AppFailureCode>().Except(webCodes));
        AssertSameSet(["emulator_rejected", "contract_changed", "contract_unverified", "version_mismatch", "device_offline", "app_crashed"],
            failureCodes.Except(webCodes));
    }

    [Fact]
    public void ChannelCaptureModeAndPlatformsEqualMigration049()
    {
        var sql = Migration049();
        AssertSameSet(["api", "scraped", AppChannelDefinition.CaptureMode], CheckValues(sql, "ck_visibility_checks_capture_mode"));
        AssertSameSet(["consumer_web", AppChannelDefinition.CollectorChannel], CheckValues(sql, "ck_collector_entries_channel"));
        AssertSameSet(["consumer_web", AppChannelDefinition.CollectorChannel], CheckValues(sql, "ck_collector_tasks_channel"));

        // The seven platform rows 049 requires are exactly the known App platforms.
        var required = Regex.Match(sql, @"FROM\s+collector_platforms\s+WHERE\s+code\s+IN\s*\(([^)]*)\)", RegexOptions.Singleline);
        Assert.True(required.Success);
        AssertSameSet(Quoted(required.Groups[1].Value), AppChannelDefinition.Engines);

        // Entry seed: <platform>.consumer_app, display name "<name> App", channel consumer_app; ernie is seeded disabled.
        Assert.Contains("CONCAT(s.platform_code, '.consumer_app')", sql);
        Assert.Contains("'cn', 'consumer_app', JSON_OBJECT()", sql);
        var seed = Regex.Match(sql, @"INSERT INTO collector_platform_entries.*?\) s\b", RegexOptions.Singleline);
        Assert.True(seed.Success);
        foreach (var platform in AppChannelDefinition.Platforms)
        {
            Assert.Matches($@"'{Regex.Escape(platform.Code)}'(?: AS platform_code)?, '{Regex.Escape(platform.DisplayName)} App'", seed.Value);
            Assert.EndsWith(".consumer_app", AppChannelDefinition.EntryCode(platform.Code));
        }
        Assert.Matches($@"'ernie', '文心一言 App', FALSE, '{AppUnavailableReasons.ContentUnreadable}'", seed.Value);
        Assert.Equal(6, Regex.Matches(seed.Value, @"\bTRUE\b").Count);
        Assert.Single(Regex.Matches(seed.Value, @"\bFALSE\b"));
    }
}
