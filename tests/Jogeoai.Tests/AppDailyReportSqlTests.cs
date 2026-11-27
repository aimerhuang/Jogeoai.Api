using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jogeoai.Api;
using Jogeoai.Api.Controllers;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Jogeoai.Worker;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static Jogeoai.Tests.AppCycleRunSqlTests;
using static Jogeoai.Tests.AppLeaseSqlTests;

namespace Jogeoai.Tests;

/// <summary>
/// 隔离库（已执行 001–052、库名含 _test）上的每日告警汇报（implement 8.4 / 8.5）：内容与运营接口的告警视图一致；节点失联超过 15 分钟出现在
/// 下一份汇报里；没有告警的日子也有一份；同一天重复运行（含 Worker 重启）只有一份；汇报里没有提问原文、租户名称和品牌名称。
/// 未设置 JOGEOAI_APP_CHANNEL_TEST_CONNECTION 时跳过。汇报用远在未来的日期作为键（日期只是键，内容按数据库的当前时间计算），
/// 用例前后删除它用过的日期；其余行由 PoolAsync 清理并核对整库行数。时间推进靠改写数据库时间戳，不用 sleep。
/// </summary>
[Collection(AppChannelSqlCollection.Name)]
public sealed class AppDailyReportSqlTests
{
    internal static readonly JsonSerializerOptions ApiJson = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>运营一侧的全部真实服务，以及一个以系统管理员身份调用它们的控制器。</summary>
    internal sealed record Ops(AppCollectorPoolStore Pool, AppCollectionStore App, AppCapacityQuery Capacity, AppAlertQuery Alerts,
        AppDailyReportStore Reports, AppCollectorAdminController Controller, Guid UserId);

    internal static Ops BuildOps(AppLeaseSqlTests.Pool pool, params Guid[] openTenants)
    {
        var database = pool.Database;
        var store = pool.Store(openTenants);
        var app = new AppCollectionStore(database);
        var capacity = new AppCapacityQuery(database, AppSiteTargetsSqlTests.OpenFor(openTenants));
        var alerts = new AppAlertQuery(database, capacity);
        var reports = new AppDailyReportStore(database, alerts, new NotConfiguredDailyReportSender());
        var user = Guid.NewGuid();
        var context = new DefaultHttpContext();
        context.Items[SessionAuthenticationMiddleware.ContextItemKey] = new AuthenticatedRequestContext(
            new(user, "ops@example.test", "Ops", "active", []), Guid.NewGuid(), "session");
        var research = DispatchProxy.Create<IResearchStore, CloroKeysApiTests.ResearchProxy>();
        ((CloroKeysApiTests.ResearchProxy)(object)research).Admin = true;
        var controller = new AppCollectorAdminController(store, store, app, capacity, alerts, reports, research,
            NullLogger<AppCollectorAdminController>.Instance) { ControllerContext = new() { HttpContext = context } };
        return new Ops(store, app, capacity, alerts, reports, controller, user);
    }

    internal static T Ok<T>(ActionResult<T> result)
    {
        var response = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.True(response.StatusCode is >= 200 and < 300, $"期望成功，实际 {response.StatusCode}：{JsonSerializer.Serialize(response.Value)}");
        return (T)response.Value!;
    }

    // Dates far in the future are only keys; each case takes its own and removes them before and after.
    private static async Task WithDatesAsync(string cs, int count, Func<DateOnly[], Task> body)
    {
        var start = new DateOnly(2090, 1, 1).AddDays(Random.Shared.Next(0, 3000) * 4);
        var dates = Enumerable.Range(0, count).Select(start.AddDays).ToArray();
        async Task DeleteAsync()
        {
            foreach (var date in dates)
                await TeamMonitoringConfigSqlTests.ExecAsync(cs, "DELETE FROM collector_app_daily_reports WHERE report_date = @date;",
                    ("@date", date.ToString("yyyy-MM-dd")));
        }
        await DeleteAsync();
        try { await body(dates); }
        finally { await DeleteAsync(); }
    }

    private static Task<long> ReportsOnAsync(string cs, DateOnly date)
        => CountAsync(cs, "SELECT COUNT(*) FROM collector_app_daily_reports WHERE report_date = @date;", ("@date", date.ToString("yyyy-MM-dd")));

    // The parts of the alerts that do not move between two reads a moment apart (the cut-off time and the durations do).
    internal static string Comparable(AppAlertsView view) => JsonSerializer.Serialize(view with
    {
        AsOf = default,
        OfflineNodes = view.OfflineNodes.Select(node => node with { OfflineSeconds = 0 }).ToArray(),
        StoppedBindings = view.StoppedBindings.Select(binding => binding with { StoppedSeconds = 0 }).ToArray(),
    }, ApiJson);

    // An approved binding that then reports a stop reason of its own. Since migration 052 a new binding waits for approval
    // whatever health it reports, so it is registered and approved first (Pool.BindAsync) and reports the reason afterwards.
    internal static async Task<Guid> BindStoppedAsync(AppLeaseSqlTests.Pool pool, string node, string device, string platform, string health)
    {
        var label = $"{platform}-{Guid.NewGuid():N}";
        var binding = await pool.BindAsync(node, device, platform, label);
        var result = await pool.Store().ReportBindingsAsync(new AppBindingReportCommand(node, "test-rev",
            [new AppBindingReport(device, platform, label, $"pkg.{platform}", "1.0", "1.0", "15", health)]));
        var state = Assert.Single(result.Bindings);
        Assert.Equal((binding, AppBindingStatus.Stopped), (state.BindingId, state.Status));
        return binding;
    }

    [AppChannelMySqlFact]
    public Task TheReportHasTheSameContentAsTheOperatorAlertViewAndIsStoredOncePerDay() => PoolAsync(async (pool, seedTenant) =>
        await WithDatesAsync(pool.Cs, 1, async dates =>
        {
            var rig = Build(pool.Cs, await seedTenant());
            var ops = BuildOps(pool, rig.Tenant);
            var (nodeA, _) = await pool.NodeAsync("mac-a");
            var (nodeB, _) = await pool.NodeAsync("mac-b");
            var cycle = await CycleAsync(rig, "汇报", 3, "doubao");
            var working = await pool.BindAsync(nodeA, "AVD_A", "doubao");
            var stopped = await BindStoppedAsync(pool, nodeB, "AVD_B", "doubao", "login_required");
            Assert.True(await ops.Pool.PausePlatformAsync("qwen", ops.UserId));
            Assert.NotNull((await ops.Pool.LeaseAsync(new AppLeaseCommand(nodeA, working, 120))).Task);
            await pool.ExecAsync("UPDATE collector_tasks SET deadline_at = UTC_TIMESTAMP(6) - INTERVAL 1 MINUTE WHERE run_id = UUID_TO_BIN(@run);",
                ("@run", cycle.Run.ToString()));
            Assert.Equal(2, await rig.App.ExpireOverdueTasksAsync());

            var generated = await ops.Reports.GenerateAsync(dates[0]);
            var view = Ok(await ops.Controller.Alerts(default));
            Assert.True(generated.Created);
            var report = generated.Report;
            Assert.Equal(Comparable(view), Comparable(report.Alerts));
            Assert.Equal(Comparable(view), Comparable(Ok(await ops.Controller.DailyReport(dates[0].ToString("yyyy-MM-dd"), default)).Alerts));
            Assert.Contains(Ok(await ops.Controller.DailyReports(null, 100, default)).Items, item => item.ReportDate == dates[0] && item.HasAlerts);

            // What the six items say on this data.
            Assert.True(report.HasAlerts);
            Assert.Equal((AppDailyReportDeliveryStatus.NotConfigured, (DateTimeOffset?)null, (string?)null), (report.DeliveryStatus, report.SentAt, report.SendError));
            var binding = Assert.Single(report.Alerts.StoppedBindings);
            Assert.Equal((stopped, nodeB, "AVD_B", "doubao", AppBindingStopReason.LoginRequired), (binding.BindingId, binding.NodeId, binding.DeviceId,
                binding.PlatformCode, binding.StopReason));
            var platform = Assert.Single(report.Alerts.Platforms);
            Assert.Equal(("qwen", true, AppAdminRules.OperatorPauseReason, false), (platform.PlatformCode, platform.Paused, platform.PauseReason, platform.Unavailable));
            Assert.Equal([new AppNotCollectedCount("doubao", AppNotCollectedCodes.Capacity, 2)], report.Alerts.NotCollectedLast24Hours);
            Assert.Equal(new AppPlatformActivity("doubao", 1, 0, 0, 0), report.Alerts.ActivityLast24Hours.Single(item => item.PlatformCode == "doubao"));
            Assert.Equal(AppChannelDefinition.Engines, report.Alerts.ActivityLast24Hours.Select(item => item.PlatformCode));
            Assert.Equal(AppChannelDefinition.Engines, report.Alerts.Capacity.Select(item => item.Capacity.Platform));
            Assert.Equal(3, report.Alerts.Capacity.Single(item => item.Capacity.Platform == "doubao").Capacity.WeeklyDemand);
            Assert.Empty(report.Alerts.OfflineNodes);
            Assert.Contains("结论：有告警", report.Body);
            Assert.Contains("login_required", report.Body);
            Assert.Contains(AppNotCollectedCodes.Capacity, report.Body);

            // A second run on the same day returns the stored report and stores nothing.
            var again = await ops.Reports.GenerateAsync(dates[0]);
            Assert.Equal((false, report.Id), (again.Created, again.Report.Id));
            Assert.Equal(1, await ReportsOnAsync(pool.Cs, dates[0]));
        }));

    // Migration 052, RF5: a binding waiting for the operator's approval is its own alert category. It is stored as stopped /
    // disabled_by_operator, like a binding the operator disabled, but it is not listed among the stopped bindings.
    [AppChannelMySqlFact]
    public Task BindingsWaitingForApprovalAreListedOnTheirOwnAndNotAmongTheStoppedBindings() => PoolAsync(async (pool, _) =>
        await WithDatesAsync(pool.Cs, 1, async dates =>
        {
            var ops = BuildOps(pool);
            var (node, _) = await pool.NodeAsync("mac");
            var working = await pool.BindAsync(node, "AVD_A", "doubao");
            var label = $"doubao-{Guid.NewGuid():N}";
            var pending = await pool.BindAsync(node, "AVD_B", "doubao", label, approve: false);
            var disabled = await pool.BindAsync(node, "AVD_A", "kimi");
            Assert.True(await ops.Pool.DisableBindingAsync(disabled, ops.UserId));

            var view = Ok(await ops.Controller.Alerts(default));
            var waiting = Assert.Single(view.PendingApprovalBindings);
            Assert.Equal((pending, node, "AVD_B", "doubao", label), (waiting.BindingId, waiting.NodeId, waiting.DeviceId, waiting.PlatformCode, waiting.AccountLabel));
            Assert.InRange(waiting.RegisteredAt, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
            var stopped = Assert.Single(view.StoppedBindings);
            Assert.Equal((disabled, AppBindingStopReason.DisabledByOperator), (stopped.BindingId, stopped.StopReason));
            Assert.True(view.HasAlerts);
            Assert.Contains("\"pendingApprovalBindings\":[{", JsonSerializer.Serialize(view, ApiJson));

            // The operator's lists: the pending binding and the disabled one have the same status and reason; approvalPending
            // tells them apart.
            var bindings = Ok(await ops.Controller.ListBindings(node, null, null, default)).ToDictionary(item => item.Id);
            Assert.Equal((AppBindingStatus.Stopped, (AppBindingStopReason?)AppBindingStopReason.DisabledByOperator, true, (DateTimeOffset?)null),
                (bindings[pending].Status, bindings[pending].StopReason, bindings[pending].ApprovalPending, bindings[pending].ApprovedAt));
            Assert.Equal((AppBindingStatus.Stopped, (AppBindingStopReason?)AppBindingStopReason.DisabledByOperator, false),
                (bindings[disabled].Status, bindings[disabled].StopReason, bindings[disabled].ApprovalPending));
            Assert.Equal((AppBindingStatus.Active, false), (bindings[working].Status, bindings[working].ApprovalPending));
            Assert.NotNull(bindings[disabled].ApprovedAt);
            Assert.NotNull(bindings[working].ApprovedAt);
            var listed = Assert.Single(Ok(await ops.Controller.ListNodes(default)), item => item.NodeId == node);
            Assert.Equal((3, 1), (listed.Bindings, listed.ActiveBindings));

            // The daily report has the same content, with its own section and its own count in the conclusion.
            var report = (await ops.Reports.GenerateAsync(dates[0])).Report;
            Assert.Equal(Comparable(view), Comparable(report.Alerts));
            Assert.True(report.HasAlerts);
            var body = report.Body.ReplaceLineEndings("\n");
            Assert.Contains("停止的绑定 1 条", body);
            Assert.Contains("待批准的绑定 1 条", body);
            Assert.Contains("八、待批准的绑定", body);
            Assert.Contains($"  - {node} / AVD_B / ", body);
            Assert.Contains($" / {label}：登记于 ", body);

            // Approved through the resume route: it leaves the category and does not become a stopped binding.
            Assert.True(Ok(await ops.Controller.ResumeBinding(pending, default)).Changed);
            var after = await ops.Alerts.GetAlertsAsync();
            Assert.Empty(after.PendingApprovalBindings);
            Assert.Equal(disabled, Assert.Single(after.StoppedBindings).BindingId);
        }));

    // There is no automatic breaker for repeated failures: a binding whose last three attempts all failed before the send is
    // listed in the alert view, and that is all. Nothing is stopped or paused; a send takes it off the list.
    [AppChannelMySqlFact]
    public Task ABindingWhoseLastThreeAttemptsFailedBeforeTheSendIsListedAndNothingIsStopped() => PoolAsync(async (pool, seedTenant) =>
    {
        var rig = Build(pool.Cs, await seedTenant());
        var ops = BuildOps(pool, rig.Tenant);
        var (node, _) = await pool.NodeAsync("mac");
        await CycleAsync(rig, "连续失败", 5, "doubao");
        var label = $"doubao-{Guid.NewGuid():N}";
        var binding = await pool.BindAsync(node, "AVD_A", "doubao", label);
        var healthy = await pool.BindAsync(node, "AVD_B", "doubao");
        async Task<AppPreSendFailureAlert?> ListedAsync(Guid id)
            => (await ops.Alerts.GetAlertsAsync()).PreSendFailures.SingleOrDefault(alert => alert.BindingId == id);
        async Task FailBeforeSendAsync(AppFailureCode code)
        {
            // A pre-send failure does not start a cooldown, and each task is attempted once per device: every lease is a new task.
            var task = (await ops.Pool.LeaseAsync(new AppLeaseCommand(node, binding, 120))).Task!;
            Assert.Equal(CollectorTaskDisposition.Requeued, (await ops.Pool.ReportFailureAsync(new AppFailureCommand(node, binding, code,
                CollectorPhase.PreSend, new CollectorReportedTask(task.Id, task.LeaseVersion), "not_sent"))).TaskDisposition);
        }

        Assert.Equal(3, AppDailyReportRules.PreSendFailureStreak);
        await FailBeforeSendAsync(AppFailureCode.ContractChanged);
        await FailBeforeSendAsync(AppFailureCode.ContractChanged);
        Assert.Null(await ListedAsync(binding));
        await FailBeforeSendAsync(AppFailureCode.AppCrashed);
        var listed = await ListedAsync(binding);
        Assert.NotNull(listed);
        Assert.Equal((node, "AVD_A", "doubao", label, "app_crashed"), (listed.NodeId, listed.DeviceId, listed.PlatformCode, listed.AccountLabel,
            listed.LastFailureCode));
        Assert.InRange(listed.LastFailedAt, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
        var view = Ok(await ops.Controller.Alerts(default));
        Assert.True(view.HasAlerts);
        Assert.Null(await ListedAsync(healthy));

        // Reporting only: the binding is still active and keeps leasing, the platform is not paused, and no event was written.
        Assert.Equal("active|-|0|0", await pool.BindingAsync(binding));
        Assert.Empty(view.StoppedBindings);
        Assert.Empty(view.Platforms);
        // binding_resumed is the operator's approval of the new binding (Pool.BindAsync).
        Assert.Equal(["binding_registered", "binding_resumed", "failure_reported", "failure_reported", "failure_reported"], await pool.EventsAsync(binding));

        // The next attempt is in flight and has not sent yet: it has no verdict, so the binding stays listed.
        var fourth = (await ops.Pool.LeaseAsync(new AppLeaseCommand(node, binding, 120))).Task!;
        Assert.NotNull(await ListedAsync(binding));
        // It gets its question out: the streak is over.
        Assert.Equal("accepted", await ops.Pool.ReportSentAsync(new AppSentCommand(node, binding, fourth.Id, fourth.LeaseVersion, DateTimeOffset.UtcNow)));
        Assert.Null(await ListedAsync(binding));
        Assert.Empty((await ops.Alerts.GetAlertsAsync()).PreSendFailures);
    });

    [AppChannelMySqlFact]
    public Task ANodeSilentForMoreThan15MinutesAppearsInTheNextReport() => PoolAsync(async (pool, _) =>
        await WithDatesAsync(pool.Cs, 3, async dates =>
        {
            var ops = BuildOps(pool);
            var (node, _) = await pool.NodeAsync("mac");
            await pool.BindAsync(node, "AVD_A", "doubao");
            Assert.DoesNotContain((await ops.Reports.GenerateAsync(dates[0])).Report.Alerts.OfflineNodes, item => item.NodeId == node);

            // Only the node's own report time is old: its binding was seen (a lease, a result) a moment ago, so it is not silent.
            await pool.ExecAsync("UPDATE collector_app_nodes SET last_seen_at = last_seen_at - INTERVAL 16 MINUTE WHERE node_id = @node;", ("@node", node));
            Assert.DoesNotContain((await ops.Reports.GenerateAsync(dates[1])).Report.Alerts.OfflineNodes, item => item.NodeId == node);

            // Nothing heard from the node for 16 minutes: the next report lists it with how long it has been silent.
            await pool.ExecAsync("UPDATE collector_app_bindings SET last_seen_at = last_seen_at - INTERVAL 16 MINUTE WHERE node_id = @node;", ("@node", node));
            var report = (await ops.Reports.GenerateAsync(dates[2])).Report;
            var offline = Assert.Single(report.Alerts.OfflineNodes, item => item.NodeId == node);
            Assert.InRange(offline.OfflineSeconds, 16 * 60, 17 * 60);
            Assert.NotNull(offline.LastActivityAt);
            Assert.True(report.HasAlerts);
            Assert.Contains($"{node}：最近出现", report.Body);
            Assert.Contains("已失联 16 分钟", report.Body);
            Assert.True(Assert.Single(Ok(await ops.Controller.ListNodes(default)), item => item.NodeId == node).Offline);

            // A node the operator disabled is not "silent".
            await ops.Pool.SetNodeStatusAsync(node, AppNodeStatus.Disabled, ops.UserId);
            Assert.DoesNotContain((await ops.Alerts.GetAlertsAsync()).OfflineNodes, item => item.NodeId == node);
        }));

    [AppChannelMySqlFact]
    public Task ADayWithoutAlertsStillGetsAReportThatSaysSo() => PoolAsync(async (pool, _) =>
        await WithDatesAsync(pool.Cs, 1, async dates =>
        {
            var ops = BuildOps(pool);
            var (node, _) = await pool.NodeAsync("mac");
            await pool.BindAsync(node, "AVD_A", "doubao");
            var result = await ops.Reports.GenerateAsync(dates[0]);
            Assert.True(result.Created);
            var report = result.Report;
            Assert.False(report.HasAlerts);
            Assert.Equal((0, 0, 0, 0L), (report.Alerts.OfflineNodes.Count, report.Alerts.StoppedBindings.Count, report.Alerts.Platforms.Count,
                report.Alerts.NotCollectedLast24Hours.Sum(item => item.Count)));
            Assert.Contains("结论：无告警。", report.Body);
            Assert.Contains("一、失联的节点（超过 15 分钟没有出现）\n  无", report.Body.ReplaceLineEndings("\n"));
            Assert.Equal("0", await RowAsync(pool.Cs, "SELECT has_alerts + 0 FROM collector_app_daily_reports WHERE report_date = @date;",
                ("@date", dates[0].ToString("yyyy-MM-dd"))));
            Assert.Equal(1, await ReportsOnAsync(pool.Cs, dates[0]));
        }));

    // The worker generates at the configured local time (09:30 Asia/Shanghai by default), once per day; a restarted worker
    // catches up the same day and still leaves a single report.
    [AppChannelMySqlFact]
    public Task TheWorkerGeneratesOneReportPerDayAfterTheLocalTimeAndCatchesUpAfterARestart() => PoolAsync(async (pool, _) =>
        await WithDatesAsync(pool.Cs, 1, async dates =>
        {
            var ops = BuildOps(pool);
            AppDailyReportWorker NewWorker() => new(NullLogger<AppDailyReportWorker>.Instance, ops.Reports, Options.Create(new AppDailyReportWorkerOptions()));
            // 09:29 and 09:31 local on the report day (UTC+8).
            var day = dates[0].ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            var before = new DateTimeOffset(day.AddHours(1).AddMinutes(29), TimeSpan.Zero);
            var after = before.AddMinutes(2);

            var worker = NewWorker();
            Assert.Null(await worker.RunPassAsync(before));
            Assert.Equal(0, await ReportsOnAsync(pool.Cs, dates[0]));
            var first = await worker.RunPassAsync(after);
            Assert.True(first!.Created);
            Assert.Equal(dates[0], first.Report.ReportDate);
            Assert.Null(await worker.RunPassAsync(after.AddHours(3)));

            // A restart in the afternoon of the same day: the new process does not know the day is done, the store does.
            var restarted = await NewWorker().RunPassAsync(after.AddHours(5));
            Assert.Equal((false, first.Report.Id), (restarted!.Created, restarted.Report.Id));
            Assert.Equal(1, await ReportsOnAsync(pool.Cs, dates[0]));

            // Configuration: another local time is honoured; unknown values fall back to the defaults.
            var custom = AppDailyReportWorker.Schedule(new AppDailyReportWorkerOptions { LocalTime = "07:05", TimeZone = "Asia/Shanghai" });
            Assert.Equal(new TimeOnly(7, 5), custom.LocalTime);
            var fallback = AppDailyReportWorker.Schedule(new AppDailyReportWorkerOptions { LocalTime = "late", TimeZone = "Nowhere/Unknown" });
            Assert.Equal((new TimeOnly(9, 30), "Asia/Shanghai"), (fallback.LocalTime, fallback.Zone.Id));
        }));

    [AppChannelMySqlFact]
    public Task TheReportCarriesNoPromptTextTenantNameOrBrandName() => PoolAsync(async (pool, seedTenant) =>
        await WithDatesAsync(pool.Cs, 1, async dates =>
        {
            var marker = Guid.NewGuid().ToString("N")[..10];
            var (tenantName, brandName, promptText) = ($"租户名独特串{marker}", $"品牌名独特串{marker}", $"提问原文独特串{marker}");
            var rig = Build(pool.Cs, await seedTenant());
            await pool.ExecAsync("UPDATE tenants SET name = @name WHERE id = UUID_TO_BIN(@tenant);", ("@name", tenantName), ("@tenant", rig.Tenant.ToString()));
            var site = await SeedAppBrandAsync(rig, brandName, "doubao", "kimi");
            for (var i = 0; i < 3; i++) await SeedPromptAsync(pool.Cs, site, $"{promptText} 第 {i + 1} 条，哪个记账工具好用？", i + 1);
            var run = (await RunSlotAsync(rig, await StartAsync(rig, site), 3)).RunId!.Value;
            await pool.ExecAsync("UPDATE collector_tasks SET deadline_at = UTC_TIMESTAMP(6) + INTERVAL 6 DAY WHERE run_id = UUID_TO_BIN(@run);", ("@run", run.ToString()));

            var ops = BuildOps(pool, rig.Tenant);
            var (node, _) = await pool.NodeAsync("mac");
            var binding = await pool.BindAsync(node, "AVD_A", "doubao", "doubao-acct-a");
            var leased = (await ops.Pool.LeaseAsync(new AppLeaseCommand(node, binding, 120))).Task!;
            Assert.StartsWith(promptText, leased.QuestionText);
            await ops.Pool.ReportFailureAsync(new AppFailureCommand(node, binding, AppFailureCode.CollectionTimeout, CollectorPhase.PostSend,
                new CollectorReportedTask(leased.Id, leased.LeaseVersion), "sent", DateTimeOffset.UtcNow));
            await pool.ExecAsync("UPDATE collector_tasks SET deadline_at = UTC_TIMESTAMP(6) - INTERVAL 1 MINUTE WHERE run_id = UUID_TO_BIN(@run);", ("@run", run.ToString()));
            Assert.Equal(6, await rig.App.ExpireOverdueTasksAsync());

            var report = (await ops.Reports.GenerateAsync(dates[0])).Report;
            // The case is not vacuous: the report does describe this tenant's work.
            Assert.Equal(6, report.Alerts.NotCollectedLast24Hours.Sum(item => item.Count));
            Assert.Equal(new AppPlatformActivity("doubao", 1, 0, 1, 0), report.Alerts.ActivityLast24Hours.Single(item => item.PlatformCode == "doubao"));
            Assert.Equal(3, report.Alerts.Capacity.Single(item => item.Capacity.Platform == "kimi").Capacity.WeeklyDemand);

            var stored = Assert.Single(await TeamMonitoringConfigSqlTests.RowsAsync(pool.Cs,
                "SELECT CAST(summary AS CHAR), body FROM collector_app_daily_reports WHERE report_date = @date;", ("@date", dates[0].ToString("yyyy-MM-dd"))));
            var api = JsonSerializer.Serialize(Ok(await ops.Controller.DailyReport(dates[0].ToString("yyyy-MM-dd"), default)), ApiJson);
            var host = await RowAsync(pool.Cs, "SELECT host FROM sites WHERE id = UUID_TO_BIN(@site);", ("@site", site.ToString()));
            foreach (var text in new[] { stored[0]!, stored[1]!, api, report.Body })
                foreach (var secret in new[] { marker, tenantName, brandName, promptText, host, rig.Tenant.ToString(), site.ToString() })
                    Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        }));
}
