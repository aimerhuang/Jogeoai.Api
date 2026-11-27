using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Jogeoai.Api.Controllers;
using Jogeoai.Application;
using Jogeoai.Domain;

namespace Jogeoai.Tests;

/// <summary>
/// App 节点绑定审批与限速（Trellis 11-27-sec-app-node-binding-approval，迁移 052）不连数据库的部分：
/// 新绑定与未批准绑定的状态规则、设备上限的算术、租约总时长配置的解析、运营视图与告警的 JSON 形状、
/// 采集端契约没有变化（停止原因的取值、绑定上报响应的字段），以及迁移 052 文件本身的写法。
/// 入库后的行为在 AppLeaseSqlTests / AppAdminSqlTests / AppDailyReportSqlTests / AppChannelMigrationTests 的隔离库用例里。
/// </summary>
public sealed class AppBindingApprovalRulesTests
{
    private static readonly JsonSerializerOptions Api = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static readonly AppBindingStopReason?[] ReportableHealth =
        [null, .. Enum.GetValues<AppBindingStopReason>().Where(reason => reason != AppBindingStopReason.DisabledByOperator).Cast<AppBindingStopReason?>()];

    // RF1: whatever the node reports for a binding it registers, the binding is stored stopped / disabled_by_operator.
    [Fact]
    public void ANewBindingIsAlwaysDisabledByOperatorWhateverHealthIsReported()
    {
        foreach (var health in ReportableHealth)
            Assert.Equal(AppBindingStopReason.DisabledByOperator, AppPoolRules.StopReasonAfterReport(registered: false, approved: false, null, health));
        // "approved" has no meaning for a binding that is not registered yet.
        Assert.Equal(AppBindingStopReason.DisabledByOperator, AppPoolRules.StopReasonAfterReport(false, true, null, null));
    }

    // RF1: nothing a node can report turns an unapproved binding active or gives it another reason. The second half covers a
    // binding an older build registered after 052 (active, never approved): its next report puts it back to pending.
    [Fact]
    public void NoReportMakesAnUnapprovedBindingActiveOrChangesItsReason()
    {
        foreach (var current in new AppBindingStopReason?[] { AppBindingStopReason.DisabledByOperator, null, AppBindingStopReason.LoginRequired })
            foreach (var health in ReportableHealth)
                Assert.Equal(AppBindingStopReason.DisabledByOperator, AppPoolRules.StopReasonAfterReport(true, approved: false, current, health));

        // The two rules the report path and the failure path are built on: an ok report never clears the operator stop, and
        // no stop reason a node can report (as health or through a failure code) replaces it.
        Assert.False(AppPoolRules.ClearsOnOkReport(AppBindingStopReason.DisabledByOperator));
        foreach (var reported in Enum.GetValues<AppBindingStopReason>())
            Assert.Equal(AppBindingStopReason.DisabledByOperator, AppPoolRules.NextStopReason(AppBindingStopReason.DisabledByOperator, reported));
        foreach (var code in Enum.GetValues<AppFailureCode>())
            if (AppFailureRules.BindingStopReason(code) is { } stop)
                Assert.Equal(AppBindingStopReason.DisabledByOperator, AppPoolRules.NextStopReason(AppBindingStopReason.DisabledByOperator, stop));
        // A failure code never maps to the operator stop, and the report endpoint refuses it as a health value (INVALID_BINDING_HEALTH).
        Assert.DoesNotContain(AppBindingStopReason.DisabledByOperator,
            Enum.GetValues<AppFailureCode>().Select(AppFailureRules.BindingStopReason).OfType<AppBindingStopReason>());
    }

    // An approved binding behaves exactly as before 052.
    [Theory]
    [InlineData(null, null, null)]
    [InlineData(null, AppBindingStopReason.LoginRequired, AppBindingStopReason.LoginRequired)]
    [InlineData(AppBindingStopReason.DeviceOffline, null, null)]
    [InlineData(AppBindingStopReason.ContractChanged, null, null)]
    [InlineData(AppBindingStopReason.LoginRequired, null, AppBindingStopReason.LoginRequired)]
    [InlineData(AppBindingStopReason.LoginRequired, AppBindingStopReason.DeviceOffline, AppBindingStopReason.LoginRequired)]
    [InlineData(AppBindingStopReason.DeviceOffline, AppBindingStopReason.ChallengeRequired, AppBindingStopReason.ChallengeRequired)]
    [InlineData(AppBindingStopReason.DisabledByOperator, null, AppBindingStopReason.DisabledByOperator)]
    [InlineData(AppBindingStopReason.DisabledByOperator, AppBindingStopReason.LoginRequired, AppBindingStopReason.DisabledByOperator)]
    public void AnApprovedBindingFollowsTheReportedHealthAsBefore(AppBindingStopReason? current, AppBindingStopReason? health, AppBindingStopReason? expected)
        => Assert.Equal(expected, AppPoolRules.StopReasonAfterReport(registered: true, approved: true, current, health));

    // RF2: the cap counts distinct devices; another platform on a registered device is never a new device.
    [Fact]
    public void TheDeviceCapAdmitsNewDevicesOnlyBelowTheLimit()
    {
        Assert.Equal((4, 64, "device_limit_exceeded"), (AppPoolRules.DefaultMaxDevicesPerNode, AppPoolRules.MaxDevicesPerNodeLimit,
            AppPoolRules.DeviceLimitExceededCode));
        for (var registered = 0; registered < AppPoolRules.DefaultMaxDevicesPerNode; registered++)
            Assert.True(AppPoolRules.DeviceWithinLimit(deviceRegistered: false, registered, AppPoolRules.DefaultMaxDevicesPerNode));
        // The fifth device with the default cap of four.
        Assert.False(AppPoolRules.DeviceWithinLimit(false, 4, 4));
        Assert.False(AppPoolRules.DeviceWithinLimit(false, 7, 4));
        // A device that already has a binding stays usable for its other platforms, at the cap and above it.
        Assert.True(AppPoolRules.DeviceWithinLimit(true, 4, 4));
        Assert.True(AppPoolRules.DeviceWithinLimit(true, 7, 4));
        // After the operator raised the cap the fifth device is admitted.
        Assert.True(AppPoolRules.DeviceWithinLimit(false, 4, 5));

        Assert.Equal([false, true, true, true, false], new[] { 0, 1, 4, 64, 65 }.Select(AppAdminRules.MaxDevicesValid));
        Assert.False(AppAdminRules.MaxDevicesValid(-1));
    }

    // RF4: the total-lease cap comes from AppChannel:MaxLeaseTotalSeconds; an invalid value stops the process at startup
    // and names only the key.
    [Fact]
    public void TheTotalLeaseCapDefaultsTo1800AndRejectsValuesOutsideItsRange()
    {
        Assert.Equal((1800, "AppChannel:MaxLeaseTotalSeconds"), (AppPoolRules.DefaultMaxLeaseTotalSeconds, AppChannelOptions.MaxLeaseTotalSecondsKey));
        Assert.Equal(1800, new AppChannelOptions().MaxLeaseTotalSeconds);
        foreach (var missing in new[] { null, "", "   " }) Assert.Equal(1800, AppChannelOptions.ParseMaxLeaseTotalSeconds(missing));
        Assert.Equal([900, 1800, 86_400], new[] { "900", " 1800 ", "86400" }.Select(AppChannelOptions.ParseMaxLeaseTotalSeconds));
        foreach (var bad in new[] { "899", "86401", "0", "-1800", "1800.5", "30m", "0x708" })
        {
            var exception = Assert.Throws<InvalidOperationException>(() => AppChannelOptions.ParseMaxLeaseTotalSeconds(bad));
            Assert.Contains(AppChannelOptions.MaxLeaseTotalSecondsKey, exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(bad, exception.Message.Replace("900–86400", ""), StringComparison.Ordinal);
        }
        // The lower bound is the longest single lease, so the lease handed out at leasing time is never past the cap.
        Assert.Equal(900, AppPoolRules.MaxLeaseSeconds);
        // Options set in code (tests, other hosts) fall back to the default instead of using a value outside the range.
        Assert.Equal([1800, 900, 1800, 86_400, 1800], new[] { 1800, 900, 60, 86_400, 86_401 }
            .Select(value => new AppChannelOptions { MaxLeaseTotalSeconds = value }.EffectiveMaxLeaseTotalSeconds()));
    }

    // RF5: the operator views carry the new members; a view written before them still reads.
    [Fact]
    public void OperatorViewsCarryApprovalAndTheDeviceCap()
    {
        var now = new DateTimeOffset(2026, 11, 27, 8, 0, 0, TimeSpan.Zero);
        var pending = new AppBindingAdminView(Guid.NewGuid(), "node-a", "AI_Apps_4KB", "doubao", "doubao-a", "com.larus.nova", "1", "1", "15",
            AppBindingStatus.Stopped, AppBindingStopReason.DisabledByOperator, now, 60, null, false, now, null, null, 100, null, now, now)
        { ApprovalPending = true };
        var json = JsonSerializer.SerializeToNode(pending, Api)!;
        Assert.Equal((true, null, "stopped", "disabled_by_operator"), ((bool)json["approvalPending"]!, (string?)json["approvedAt"],
            (string)json["status"]!, (string)json["stopReason"]!));
        Assert.True(json.AsObject().ContainsKey("approvedAt"));

        var approved = JsonSerializer.SerializeToNode(pending with { ApprovalPending = false, ApprovedAt = now, Status = AppBindingStatus.Active,
            StopReason = null }, Api)!;
        Assert.Equal((false, "2026-11-27T08:00:00+00:00"), ((bool)approved["approvalPending"]!, (string)approved["approvedAt"]!));

        var node = new AppNodeView("node-a", AppNodeStatus.Active, now, now, false, "rev", 2, 1, now, now);
        Assert.Equal(4, (int)JsonSerializer.SerializeToNode(node, Api)!["maxDevices"]!);
        Assert.Equal(12, (int)JsonSerializer.SerializeToNode(node with { MaxDevices = 12 }, Api)!["maxDevices"]!);
        Assert.Equal(7, JsonSerializer.Deserialize<AppNodeView>(JsonSerializer.Serialize(node with { MaxDevices = 7 }, Api), Api)!.MaxDevices);

        Assert.Equal("{\"maxDevices\":6}", JsonSerializer.Serialize(new AppNodeMaxDevicesRequest(6), Api));
        Assert.Null(JsonSerializer.Deserialize<AppNodeMaxDevicesRequest>("{}", Api)!.MaxDevices);
    }

    // RF5: bindings waiting for approval are their own alert category; they make the report an alert and are not listed
    // among the stopped bindings. A summary stored before the category existed reads back with an empty list.
    [Fact]
    public void PendingBindingsAreTheirOwnAlertCategory()
    {
        var now = new DateTimeOffset(2026, 11, 27, 2, 30, 0, TimeSpan.Zero);
        var quiet = new AppAlertsView(now, false, [], [], [], [], [], []);
        Assert.Empty(quiet.PendingApprovalBindings);
        Assert.False(AppDailyReportRules.HasAlerts([], [], [], [], [], [], []));
        Assert.Contains("八、待批准的绑定（新登记，批准前不能领取任务；核对后在后台调用恢复接口批准）\n  无",
            AppDailyReportRules.Render(new DateOnly(2026, 11, 27), quiet).ReplaceLineEndings("\n"));

        var pending = new AppPendingApprovalBindingAlert(Guid.NewGuid(), "node-a", "AI_Apps_4KB_3", "doubao", "doubao-c", now.AddMinutes(-20));
        Assert.True(AppDailyReportRules.HasAlerts([], [], [], [], [], [], [pending]));
        var view = quiet with { HasAlerts = true, PendingApprovalBindings = [pending] };
        var body = AppDailyReportRules.Render(new DateOnly(2026, 11, 27), view).ReplaceLineEndings("\n");
        Assert.Contains("结论：有告警（待批准的绑定 1 条）。", body);
        Assert.Contains($"  - node-a / AI_Apps_4KB_3 / {AppChannelDefinition.TryGetPlatform("doubao")!.DisplayName}（doubao） / doubao-c：登记于 2026-11-27T02:10:00Z", body);
        // Not double-counted: the stopped-bindings section stays empty.
        Assert.Contains("二、停止的绑定\n  无", body);
        Assert.DoesNotContain("停止的绑定 1 条", body);

        var stored = JsonSerializer.Serialize(quiet, Api);
        Assert.Contains("\"pendingApprovalBindings\":[]", stored);
        var older = JsonSerializer.Deserialize<AppAlertsView>(stored.Replace(",\"pendingApprovalBindings\":[]", ""), Api)!;
        Assert.Empty(older.PendingApprovalBindings);
        var round = JsonSerializer.Deserialize<AppAlertsView>(JsonSerializer.Serialize(view, Api), Api)!;
        Assert.Equal(pending, Assert.Single(round.PendingApprovalBindings));
        Assert.Empty(round.StoppedBindings);
    }

    // The collector parses stop reasons as a closed enum and the report response as a fixed shape: neither changed.
    [Fact]
    public void TheCollectorContractIsUnchangedAPendingBindingIsAnOperatorStopAndTheRejectionCodeIsFreeText()
    {
        Assert.Equal(["login_required", "challenge_required", "device_offline", "contract_changed", "contract_unverified", "version_mismatch",
            "emulator_rejected", "disabled_by_operator"], AppContractNames.All<AppBindingStopReason>());
        Assert.Equal(["active", "stopped"], AppContractNames.All<AppBindingStatus>());

        var binding = Guid.Parse("7d9a4c2e-3b1f-4c55-9f0e-6a1d2b3c4d5e");
        var response = new AppBindingReportResponse(
            [new AppBindingResponse("AI_Apps_4KB", "doubao", binding, AppBindingStatus.Stopped, AppBindingStopReason.DisabledByOperator, null, 100, null)],
            [new AppBindingRejection("AI_Apps_4KB_5", "doubao", AppPoolRules.DeviceLimitExceededCode, "超出设备上限")]);
        var json = JsonSerializer.SerializeToNode(response, Api)!.AsObject();
        Assert.Equal(["bindings", "rejected", "dispatchProtocol"], json.Select(member => member.Key));
        Assert.Equal(["deviceId", "platformCode", "bindingId", "status", "stopReason", "cooldownUntil", "minIntervalSeconds", "dailyQuota"],
            json["bindings"]![0]!.AsObject().Select(member => member.Key));
        Assert.Equal(("stopped", "disabled_by_operator"), ((string)json["bindings"]![0]!["status"]!, (string)json["bindings"]![0]!["stopReason"]!));
        Assert.Equal(["deviceId", "platformCode", "code", "message"], json["rejected"]![0]!.AsObject().Select(member => member.Key));
        Assert.Equal("device_limit_exceeded", (string)json["rejected"]![0]!["code"]!);

        // The answer to an unapproved binding's failure report is an existing shape with existing values.
        var ignored = new AppFailureResponse(AppBindingStatus.Stopped, AppBindingStopReason.DisabledByOperator, null, CollectorTaskDisposition.Stale, false, false);
        Assert.Equal("{\"bindingStatus\":\"stopped\",\"stopReason\":\"disabled_by_operator\",\"cooldownUntil\":null,\"taskDisposition\":\"stale\","
            + "\"platformPaused\":false,\"platformUnavailable\":false}", JsonSerializer.Serialize(ignored, Api));
    }

    private static string SourceText(string file)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Jogeoai.Infrastructure", file));
        Assert.True(File.Exists(path), $"找不到源文件：{path}");
        return File.ReadAllText(path);
    }

    private static string Between(string text, string from, string to)
    {
        var start = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, from);
        var end = text.IndexOf(to, start + from.Length, StringComparison.Ordinal);
        Assert.True(end > start, to);
        return text[start..end];
    }

    private static int FirstOf(string text, params string[] calls)
        => calls.Select(call => text.IndexOf(call, StringComparison.Ordinal)).Where(index => index >= 0).DefaultIfEmpty(int.MaxValue).Min();

    // RF1 / RF3 pinned on the source (the SQL cases need a database): approved_at is written in one place only, the
    // operator's resume; an unapproved binding's lease request and failure report return before the first write, lock of
    // another row or task lookup; and every platform-wide count looks at approved bindings only.
    [Fact]
    public void OnlyTheOperatorsResumeApprovesAndAnUnapprovedBindingReturnsBeforeAnyWrite()
    {
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Jogeoai.Infrastructure"));
        var writers = Directory.GetFiles(directory, "App*.cs")
            .SelectMany(file => Regex.Matches(File.ReadAllText(file), @"approved_at\s*=(?!=)").Select(_ => Path.GetFileName(file))).ToArray();
        Assert.Equal(["AppCollectorPoolStore.Bindings.cs"], writers);
        var bindings = SourceText("AppCollectorPoolStore.Bindings.cs");
        Assert.Contains("approved_at = COALESCE(approved_at, UTC_TIMESTAMP(6))",
            bindings[bindings.IndexOf("public Task<bool> ResumeBindingAsync", StringComparison.Ordinal)..], StringComparison.Ordinal);
        // The insert of a new binding names the column and gives it NULL; the node row is locked before the binding is read.
        Assert.Matches(@"stopped_at, approved_at,\s+last_seen_at, created_at, updated_at\)", bindings);
        Assert.Matches(@"END, NULL, UTC_TIMESTAMP\(6\), UTC_TIMESTAMP\(6\), UTC_TIMESTAMP\(6\)\);", bindings);
        var upsert = Between(bindings, "UpsertBindingAsync(MySqlConnection c", "ReadBindingStateAsync(MySqlConnection c");
        Assert.True(upsert.IndexOf("FROM collector_app_nodes WHERE node_id = @node FOR UPDATE", StringComparison.Ordinal)
            < FirstOf(upsert, "FROM collector_app_bindings", "INSERT INTO", "InsertEventAsync("));

        var lease = Between(SourceText("AppCollectorPoolStore.Lease.cs"), "public Task<AppLeaseResult> LeaseAsync", "private IReadOnlyList<Guid> OpenTenants()");
        var refused = lease.IndexOf("if (!binding.Approved) return Empty(AppLeaseEmptyReason.BindingStopped, null);", StringComparison.Ordinal);
        Assert.True(refused > 0 && refused < FirstOf(lease, "ExecuteAsync(", "InsertEventAsync(", "StampBindingStoppedAsync(", "StampWaitingTasksAsync(",
            "LockPlatformAsync(", "PickLeasableTaskAsync("));

        var failures = SourceText("AppCollectorPoolStore.Failures.cs");
        var report = Between(failures, "public Task<AppFailureResult> ReportFailureAsync", "private static async Task<bool> AllBindingsRejectedAsync");
        var ignored = report.IndexOf("if (!binding.Approved)", StringComparison.Ordinal);
        Assert.True(ignored > 0 && ignored < FirstOf(report, "ExecuteAsync(", "InsertEventAsync(", "LockPlatformAsync(", "FROM collector_tasks",
            "RecordSendAsync("));
        Assert.Contains("JOIN collector_app_bindings b ON b.id = e.binding_id AND b.approved_at IS NOT NULL", report, StringComparison.Ordinal);
        Assert.Contains("WHERE platform_code = @platform AND approved_at IS NOT NULL AND (stop_reason IS NULL OR stop_reason <> @operator)",
            failures, StringComparison.Ordinal);
        // "Is any binding of the platform usable" (it decides whether waiting tasks are marked binding_stopped).
        Assert.Contains("WHERE platform_code = @platform AND status = 'active' AND approved_at IS NOT NULL",
            SourceText("AppCollectorPoolStore.BlockReasons.cs"), StringComparison.Ordinal);
    }

    // RF4 pinned on the source: the heartbeat is an App-channel statement, locks the task before it reads the attempt row of
    // the current lease version, never sets an end that is not in the future, and answers STALE_LEASE when nothing was extended.
    [Fact]
    public void TheHeartbeatIsCappedOnTheAttemptOfTheCurrentLeaseAndNeverEndsInThePast()
    {
        var source = SourceText("AppCollectorPoolStore.Lease.cs");
        var heartbeat = source[source.IndexOf("public Task<DateTimeOffset> HeartbeatAsync", StringComparison.Ordinal)..];
        var locked = heartbeat.IndexOf("FOR UPDATE;", StringComparison.Ordinal);
        var capped = heartbeat.IndexOf("UPDATE collector_tasks task", StringComparison.Ordinal);
        Assert.True(locked > 0 && locked < capped);
        var guard = heartbeat[..locked];
        foreach (var condition in new[] { "channel = '{AppChannel}'", "lease_owner = @owner", "lease_version = @version", "status IN ('leased', 'collecting')",
            "lease_until > UTC_TIMESTAMP(6)" })
            Assert.Contains(condition, guard, StringComparison.Ordinal);
        var update = heartbeat[capped..heartbeat.IndexOf("if (affected == 0)", StringComparison.Ordinal)];
        Assert.Contains("JOIN collector_app_device_attempts a ON a.task_id = task.id AND a.lease_version = task.lease_version", update, StringComparison.Ordinal);
        Assert.Contains("LEAST(UTC_TIMESTAMP(6) + INTERVAL @seconds SECOND, a.leased_at + INTERVAL @maxTotal SECOND,", update, StringComparison.Ordinal);
        Assert.Contains("COALESCE(task.deadline_at, UTC_TIMESTAMP(6) + INTERVAL @seconds SECOND)", update, StringComparison.Ordinal);
        // Each operand of LEAST is in the future: the extension is at least 5 seconds, and the two guards are strict.
        Assert.Contains("AND a.leased_at + INTERVAL @maxTotal SECOND > UTC_TIMESTAMP(6)", update, StringComparison.Ordinal);
        Assert.Contains("AND (task.deadline_at IS NULL OR task.deadline_at > UTC_TIMESTAMP(6))", update, StringComparison.Ordinal);
        Assert.True(AppPoolRules.MinLeaseSeconds > 0);
        Assert.Contains("if (affected == 0) throw new CollectorStoreException(\"STALE_LEASE\"", heartbeat, StringComparison.Ordinal);
    }

    private static string MigrationText(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../db/migrations", relative));
        Assert.True(File.Exists(path), $"找不到 migration：{path}");
        return Regex.Replace(File.ReadAllText(path), @"--[^\r\n]*", "");
    }

    // 052 as a file: additive, no new stop-reason value, and the back-fill tied to the completion marker so that a replay
    // after release cannot approve the bindings that are waiting for approval.
    [Fact]
    public void Migration052IsAdditiveAndItsBackfillRunsOnlyUntilTheMigrationIsComplete()
    {
        var code = MigrationText("052_app_binding_approval.sql");
        foreach (var forbidden in new[] { "TRIGGER", "DROP TABLE", "DROP COLUMN", "DROP CHECK", "DELETE FROM", "CREATE TABLE", "stop_reason", "pending_approval" })
            Assert.DoesNotContain(forbidden, code, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ALTER TABLE collector_app_bindings ADD COLUMN approved_at DATETIME(6) NULL;", code, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE collector_app_nodes ADD COLUMN max_devices INT UNSIGNED NOT NULL DEFAULT 4;", code, StringComparison.Ordinal);
        Assert.Contains("ADD CONSTRAINT ck_collector_app_nodes_max_devices CHECK (max_devices BETWEEN 1 AND 64);", code, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(code, @"(?i)\bALTER\s+TABLE\s+collector_app_(nodes|bindings)\s+ADD\s+COLUMN").Count);
        Assert.Equal(3, Regex.Matches(code, @"(?i)\bALTER\s+TABLE\b").Count);

        // Both back-fills and the CHECK sit in the one block that runs only while the CHECK does not exist yet.
        var block = code.IndexOf("IF completed = 0 THEN", StringComparison.Ordinal);
        Assert.True(block > 0);
        var updates = Regex.Matches(code, @"(?i)\bUPDATE\s+collector_app_(bindings|nodes)\b");
        Assert.Equal(2, updates.Count);
        Assert.All(updates, update => Assert.True(update.Index > block));
        Assert.True(code.IndexOf("ADD CONSTRAINT ck_collector_app_nodes_max_devices", StringComparison.Ordinal) > updates[^1].Index);
        Assert.Contains("SET approved_at = created_at WHERE approved_at IS NULL;", code, StringComparison.Ordinal);
        Assert.Contains("GREATEST(4, (SELECT COUNT(DISTINCT b.device_id) FROM collector_app_bindings b WHERE b.node_id = n.node_id))", code, StringComparison.Ordinal);
        // Refusals come before the first change.
        var firstChange = code.IndexOf("ALTER TABLE", StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(code[..firstChange], "SIGNAL SQLSTATE '45000'").Count);
        Assert.Equal(2, Regex.Matches(code, "SIGNAL SQLSTATE '45000'").Count);
        Assert.Contains("CALL jogeoai_052_app_binding_approval();", code, StringComparison.Ordinal);
        Assert.Contains("DROP PROCEDURE jogeoai_052_app_binding_approval;", code, StringComparison.Ordinal);

        var rollback = MigrationText("rollback/052_app_binding_approval_rollback.sql");
        var dropCheck = rollback.IndexOf("DROP CHECK ck_collector_app_nodes_max_devices", StringComparison.Ordinal);
        var dropCap = rollback.IndexOf("ALTER TABLE collector_app_nodes DROP COLUMN max_devices;", StringComparison.Ordinal);
        var dropApproval = rollback.IndexOf("ALTER TABLE collector_app_bindings DROP COLUMN approved_at;", StringComparison.Ordinal);
        Assert.True(dropCheck > 0 && dropCheck < dropCap && dropCap < dropApproval);
        foreach (var forbidden in new[] { "DROP TABLE", "DELETE FROM", "UPDATE " }) Assert.DoesNotContain(forbidden, rollback, StringComparison.OrdinalIgnoreCase);
    }

    // deploy/Dockerfile.mysql copies db/migrations/*.sql and runs them in file-name order before the completion marker.
    [Fact]
    public void Migration052IsTheLatestFileAndStillSortsBeforeTheCompletionMarker()
    {
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../db/migrations"));
        var files = Directory.GetFiles(directory, "*.sql").Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToArray();
        Assert.Contains("052_app_binding_approval.sql", files);
        var approval = Array.IndexOf(files, "052_app_binding_approval.sql");
        Assert.True(Array.IndexOf(files, "049_app_channel.sql") < approval && Array.IndexOf(files, "051_app_device_dispatch.sql") < approval);
        Assert.All(files, file => Assert.True(string.CompareOrdinal(file, "999-jogeoai-initdb-complete.sh") < 0, file));
        Assert.True(File.Exists(Path.Combine(directory, "rollback", "052_app_binding_approval_rollback.sql")));
    }
}
