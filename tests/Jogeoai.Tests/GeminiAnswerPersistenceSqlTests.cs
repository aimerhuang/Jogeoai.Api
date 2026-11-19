using System.Text;
using System.Text.Json;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Jogeoai.Providers.Abstractions;
using MySqlConnector;

namespace Jogeoai.Tests;

// Not part of the default focused filter. Running requires separately approved provisioning
// of 001-020 on this exact isolated database. Copilot cases reuse this guard unchanged. These tests retain their rows; no cleanup is implicit.
[Collection("Gemini isolated SQL")]
public sealed class GeminiAnswerPersistenceSqlTests
{
    internal static string RequireIsolatedConnection(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("需要另行确认并配置 JOGEOAI_GEMINI_TEST_CONNECTION，不能使用业务库或其他测试库。");
        var builder = new MySqlConnectionStringBuilder(connectionString);
        if (builder.Server is not ("127.0.0.1" or "localhost") || builder.Port != 33318
            || builder.Database != "jogeoai_gemini_test_20261008")
            throw new InvalidOperationException("Gemini SQL 测试仅允许指定的本机 33318 隔离库 jogeoai_gemini_test_20261008。");
        return connectionString;
    }

    [Theory]
    [InlineData("gemini")]
    [InlineData("copilot")]
    public async Task AdditiveMigrationReplaysWithoutChangingHistoricalPayloadAndEnforcesSize(string engine)
    {
        var f = await Fixture.Create(engine);
        var run = await f.Runs.CreateRunAsync(f.RunCommand());
        var check = Assert.Single((await f.Runs.GetChecksAsync(f.Tenant, run.RunId, null, 20, null, null)).Items);
        await f.Exec("UPDATE visibility_checks SET answer='legacy', result_metadata=NULL WHERE id=UUID_TO_BIN(@id);", ("@id", check.CheckId));
        await f.ApplyMigration();
        await f.ApplyMigration();
        var old = await f.Runs.GetCheckAsync(f.Tenant, check.CheckId);
        Assert.Null(old!.ResultQuality);
        Assert.Equal("legacy", await f.Scalar("SELECT answer FROM visibility_checks WHERE id=UUID_TO_BIN(@id);", ("@id", check.CheckId)));
        await Assert.ThrowsAsync<MySqlException>(() => f.Exec(
            "UPDATE visibility_checks SET result_metadata=JSON_OBJECT('oversize',@value) WHERE id=UUID_TO_BIN(@id);",
            ("@id", check.CheckId), ("@value", new string('x', 16384))));
        Assert.Null((await f.Runs.GetCheckAsync(f.Tenant, check.CheckId))!.ResultQuality);
        await f.StopUnused(run.RunId);
    }

    [Theory]
    [InlineData("gemini")]
    [InlineData("copilot")]
    public async Task CompletionRollsBackPartialCitationsThenReplaysWithoutDoubleChargingAndKeepsSnapshot(string engine)
    {
        var f = await Fixture.Create(engine);
        var command = f.RunCommand() with { ChargeMonitoringCredits = true, RequireEnabledTargets = true };
        var run = await f.Runs.CreateRunAsync(command);
        Assert.Equal(run.RunId, (await f.Runs.CreateRunAsync(command)).RunId);
        var check = Assert.Single((await f.Runs.GetChecksAsync(f.Tenant, run.RunId, null, 20, engine, null)).Items);
        var lease = await f.Lease(check);
        await f.Runs.MarkSubmittingAsync(lease, "gemini-sql");
        await f.Runs.RecordSubmissionAsync(lease, "gemini-sql", new("fixture-task-" + Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, "hash"));
        lease = await f.Lease(check);
        var quality = new CloroResultQuality(false, false, new("available", "complete", 2, 2, 0, 0, 0, []));
        var answer = new CloroAnswerSnapshot("cloro", engine, "scraped", "observed_surface", engine, "保存的答案", true,
            [new("https://example.org/a", "example.org", "A", 1), new("https://example.org/a", "example.org", "B", 2)],
            [], JsonSerializer.SerializeToElement(new { markdown = "保存的答案" }), DateTimeOffset.UtcNow,
            "cloro-v2", ResultQuality: quality);
        // The first insert succeeds, the second violates the existing URL capacity constraint.
        var invalid = answer with { Citations = [answer.Citations[0], answer.Citations[1] with { Url = new string('x', 2000) }] };
        await Assert.ThrowsAsync<MySqlException>(() => f.Runs.CompleteTaskAsync(lease, "gemini-sql", invalid, 15));
        var rolledBack = await f.Runs.GetCheckAsync(f.Tenant, check.CheckId);
        Assert.NotEqual("succeeded", rolledBack!.Status);
        Assert.Null(rolledBack.ResultQuality);
        Assert.Empty(rolledBack.Citations);
        await f.Runs.CompleteTaskAsync(lease, "gemini-sql", answer, 15);
        Assert.Equal("STALE_LEASE", (await Assert.ThrowsAsync<CollectorStoreException>(() =>
            f.Runs.CompleteTaskAsync(lease, "gemini-sql", answer, 15))).Code);
        var saved = await f.Runs.GetCheckAsync(f.Tenant, check.CheckId);
        Assert.Equal("保存的答案", saved!.Answer!.Text);
        Assert.Equal(2, saved.Citations.Count);
        Assert.Equal(new[] { 1, 2 }, saved.Citations.Select(c => c.Position));
        Assert.Equal(2, saved.ResultQuality!.Citations.RetainedCount);
        Assert.False(saved.ResultQuality.ModelVersionProvided);
        Assert.Null(await f.Runs.GetCheckAsync(Guid.NewGuid(), check.CheckId));
        Assert.Equal(10m, Convert.ToDecimal(await f.Scalar("SELECT captured_credits FROM monitoring_credit_reservations WHERE run_id=UUID_TO_BIN(@id);", ("@id", run.RunId))));
        Assert.Equal(1, Convert.ToInt32(await f.Scalar("SELECT COUNT(*) FROM analytics_check_arrivals WHERE check_id=UUID_TO_BIN(@id);", ("@id", check.CheckId))));
        await f.Exec("UPDATE prompts SET content='new question', enabled=FALSE, archived_at=UTC_TIMESTAMP(6) WHERE id=UUID_TO_BIN(@id);", ("@id", f.Prompt));
        Assert.Equal("已保存问题", (await f.Runs.GetCheckAsync(f.Tenant, check.CheckId))!.PromptSnapshot);
        Assert.Equal("保存的答案", (await f.Runs.GetCheckAsync(f.Tenant, check.CheckId))!.Answer!.Text);
    }

    [Theory]
    [InlineData("gemini")]
    [InlineData("copilot")]
    public async Task ExplicitActivationPreservesUnchangedPutAndRecoveryDoesNotExpandOldOrCustomSlots(string engine)
    {
        var f = await Fixture.Create(engine);
        var targets = await f.Management.GetSiteTargetsAsync(f.Tenant, f.Site);
        var oldVersion = targets.ConfigVersion;
        await f.Management.PutSiteTargetsAsync(f.Tenant, f.Site, new(oldVersion, [Fixture.ChatGpt]));
        Assert.Equal("CONFIG_VERSION_CONFLICT", (await Assert.ThrowsAsync<CollectorStoreException>(() =>
            f.Management.PutSiteTargetsAsync(f.Tenant, f.Site, new(oldVersion, [Fixture.ChatGpt, f.AddedTarget])))).Code);
        var selection = new CollectionScheduleSelection([f.Prompt], [Fixture.ChatGptTarget]);
        var auto = await f.Schedules.CreateAsync(new(f.Tenant, f.Site, PromptMonitoringIdentity.ScheduleName, "daily", null, "09:00", "UTC", selection, true));
        var custom = await f.Schedules.CreateAsync(new(f.Tenant, f.Site, "custom", "daily", null, "09:00", "UTC", selection, true));
        var paused = await f.Schedules.CreateAsync(new(f.Tenant, f.Site, "paused-custom", "daily", null, "09:00", "UTC", selection, false));
        targets = await f.Management.GetSiteTargetsAsync(f.Tenant, f.Site);
        targets = await f.Management.PutSiteTargetsAsync(f.Tenant, f.Site, new(targets.ConfigVersion, [Fixture.ChatGpt, f.AddedTarget]));
        var enabledAt = await f.Scalar("SELECT monitoring_enabled_at FROM site_targets WHERE site_id=UUID_TO_BIN(@id) AND engine=@engine;", ("@id", f.Site), ("@engine", engine));
        await f.Management.PutSiteTargetsAsync(f.Tenant, f.Site, new(targets.ConfigVersion, [Fixture.ChatGpt, f.AddedTarget]));
        Assert.Equal(enabledAt, await f.Scalar("SELECT monitoring_enabled_at FROM site_targets WHERE site_id=UUID_TO_BIN(@id) AND engine=@engine;", ("@id", f.Site), ("@engine", engine)));
        var now = DateTimeOffset.UtcNow;
        await f.Exec("UPDATE collection_schedules SET next_slot_at=@due WHERE id IN(UUID_TO_BIN(@auto),UUID_TO_BIN(@custom));",
            ("@due", now.UtcDateTime.Date.AddDays(-3).AddHours(9)), ("@auto", auto.Id), ("@custom", custom.Id));
        await f.Schedules.MaterializeDueSlotsAsync(now, 10);
        var slots = await f.Schedules.GetPendingSlotsAsync(100);
        var oldAuto = Assert.Single(slots, s => s.ScheduleId == auto.Id);
        Assert.Single(oldAuto.Selection.Targets); // today's nominal 09:00 or older, always before activation
        Assert.Single(Assert.Single(slots, s => s.ScheduleId == custom.Id).Selection.Targets);
        var plans = await f.Schedules.ListAsync(f.Tenant, f.Site, null, 100);
        var nextAuto = Assert.Single(plans.Items, p => p.Id == auto.Id);
        Assert.Equal(2, nextAuto.Selection.Targets.Count);
        Assert.False(Assert.Single(plans.Items, p => p.Id == paused.Id).Enabled);
        Assert.Equal(0, Convert.ToInt32(await f.Scalar("SELECT COUNT(*) FROM visibility_runs WHERE site_id=UUID_TO_BIN(@id);", ("@id", f.Site))));
        await f.Schedules.MaterializeDueSlotsAsync(nextAuto.NextSlotAt!.Value, 10);
        slots = await f.Schedules.GetPendingSlotsAsync(100);
        Assert.Equal(2, Assert.Single(slots, s => s.ScheduleId == auto.Id && s.Id != oldAuto.Id).Selection.Targets.Count);
        Assert.Single(Assert.Single(slots, s => s.Id == oldAuto.Id).Selection.Targets);
        // Race the ordered coordinator with the Site-first slot path; no external Provider is involved.
        var race = Task.WhenAll(AutomaticMonitoringSelection.ReconcileAsync(f.Database, f.Tenant, f.Site, default),
            f.Schedules.ProcessSlotAsync(oldAuto, f.Runs));
        await race.WaitAsync(TimeSpan.FromSeconds(15));
        var completed = await f.Scalar("SELECT run_id FROM schedule_slots WHERE id=UUID_TO_BIN(@id);", ("@id", oldAuto.Id));
        Assert.NotNull(completed);
        // Disable retained fixtures to prevent later materializers consuming them; do not delete rows.
        await f.Exec("UPDATE collection_schedules SET enabled=FALSE,next_slot_at=NULL WHERE site_id=UUID_TO_BIN(@id);", ("@id", f.Site));
        await f.Exec("UPDATE provider_tasks t JOIN visibility_checks c ON c.id=t.check_id SET t.next_attempt_at='2099-01-01' WHERE c.site_id=UUID_TO_BIN(@id);", ("@id", f.Site));
    }

    [Theory]
    [InlineData("gemini")]
    [InlineData("copilot")]
    public async Task BusyMonitoringInitializationYieldsPendingSlotThenContinuesAfterRelease(string engine)
    {
        var f = await Fixture.Create(engine);
        var auto = await f.Schedules.CreateAsync(new(f.Tenant, f.Site, PromptMonitoringIdentity.ScheduleName,
            "daily", null, "09:00", "UTC", new([f.Prompt], [Fixture.ChatGptTarget]), true));
        await f.Exec("UPDATE collection_schedules SET next_slot_at=@due WHERE id=UUID_TO_BIN(@id);",
            ("@due", DateTime.UtcNow.Date.AddDays(-1).AddHours(9)), ("@id", auto.Id));
        await f.Schedules.MaterializeDueSlotsAsync(DateTimeOffset.UtcNow, 10);
        var slot = Assert.Single(await f.Schedules.GetPendingSlotsAsync(100), s => s.ScheduleId == auto.Id);
        await using var owner = f.Database.CreateConnection();
        await owner.OpenAsync();
        var name = $"prompt-monitor:{f.Site:N}";
        await using var acquire = new MySqlCommand("SELECT GET_LOCK(@name, 0);", owner);
        acquire.Parameters.AddWithValue("@name", name);
        Assert.Equal(1, Convert.ToInt32(await acquire.ExecuteScalarAsync()));
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            Assert.False(await f.Schedules.ProcessSlotAsync(slot, f.Runs, deadline.Token));
            Assert.Equal("pending", await f.Scalar("SELECT status FROM schedule_slots WHERE id=UUID_TO_BIN(@id);", ("@id", slot.Id)));
            Assert.Null(await f.Scalar("SELECT run_id FROM schedule_slots WHERE id=UUID_TO_BIN(@id);", ("@id", slot.Id)));
            Assert.Equal(0, Convert.ToInt32(await f.Scalar("SELECT COUNT(*) FROM visibility_runs WHERE site_id=UUID_TO_BIN(@id);", ("@id", f.Site))));
        }
        finally
        {
            await using var release = new MySqlCommand("SELECT RELEASE_LOCK(@name);", owner);
            release.Parameters.AddWithValue("@name", name);
            Assert.Equal(1, Convert.ToInt32(await release.ExecuteScalarAsync()));
        }
        Assert.True(await f.Schedules.ProcessSlotAsync(slot, f.Runs));
        Assert.Equal("completed", await f.Scalar("SELECT status FROM schedule_slots WHERE id=UUID_TO_BIN(@id);", ("@id", slot.Id)));
        Assert.NotNull(await f.Scalar("SELECT run_id FROM schedule_slots WHERE id=UUID_TO_BIN(@id);", ("@id", slot.Id)));
        await f.Exec("UPDATE collection_schedules SET enabled=FALSE,next_slot_at=NULL WHERE site_id=UUID_TO_BIN(@id);", ("@id", f.Site));
        await f.Exec("UPDATE provider_tasks t JOIN visibility_checks c ON c.id=t.check_id SET t.next_attempt_at='2099-01-01' WHERE c.site_id=UUID_TO_BIN(@id);", ("@id", f.Site));
    }

    [Theory]
    [InlineData("gemini")]
    [InlineData("copilot")]
    public async Task EmptyAutomaticSelectionCreatesOnlySkippedSlotAndPreservesPause(string engine)
    {
        var f = await Fixture.Create(engine);
        var auto = await f.Schedules.CreateAsync(new(f.Tenant, f.Site, PromptMonitoringIdentity.ScheduleName,
            "daily", null, "09:00", "UTC", new([f.Prompt], [Fixture.ChatGptTarget]), true));
        await f.Exec("UPDATE prompts SET enabled=FALSE WHERE id=UUID_TO_BIN(@id);", ("@id", f.Prompt));
        await f.Exec("UPDATE collection_schedules SET next_slot_at=@due WHERE id=UUID_TO_BIN(@id);",
            ("@due", DateTime.UtcNow.Date.AddDays(-1).AddHours(9)), ("@id", auto.Id));
        await f.Schedules.MaterializeDueSlotsAsync(DateTimeOffset.UtcNow, 10);
        Assert.Equal("MONITORING_SELECTION_EMPTY", await f.Scalar("SELECT skip_code FROM schedule_slots WHERE schedule_id=UUID_TO_BIN(@id);", ("@id", auto.Id)));
        var current = Assert.Single((await f.Schedules.ListAsync(f.Tenant, f.Site, null, 100)).Items);
        Assert.Empty(current.Selection.PromptIds);
        var paused = await f.Schedules.UpdateAsync(new(f.Tenant, f.Site, auto.Id, current.Version, Enabled: false));
        Assert.False(paused!.Enabled);
        await AutomaticMonitoringSelection.ReconcileAsync(f.Database, f.Tenant, f.Site, default);
        Assert.False(Assert.Single((await f.Schedules.ListAsync(f.Tenant, f.Site, null, 100)).Items).Enabled);
        Assert.Equal(0, Convert.ToInt32(await f.Scalar("SELECT COUNT(*) FROM visibility_runs WHERE site_id=UUID_TO_BIN(@id);", ("@id", f.Site))));
    }

    [Theory]
    [InlineData("gemini")]
    [InlineData("copilot")]
    public async Task LegacyNewMemberGetsForwardBoundaryAndReenableRenewsOnlyItsBoundary(string engine)
    {
        var f = await Fixture.Create(engine);
        await f.Exec("UPDATE site_targets SET monitoring_enabled_at=NULL WHERE site_id=UUID_TO_BIN(@id);", ("@id", f.Site));
        var auto = await f.Schedules.CreateAsync(new(f.Tenant, f.Site, PromptMonitoringIdentity.ScheduleName,
            "daily", null, "09:00", "UTC", new([f.Prompt], [Fixture.ChatGptTarget]), false));
        var before = DateTime.UtcNow.AddSeconds(-1);
        await AutomaticMonitoringSelection.ReconcileAsync(f.Database, f.Tenant, f.Site, default);
        Assert.Null(await f.Scalar("SELECT monitoring_enabled_at FROM site_targets WHERE site_id=UUID_TO_BIN(@id) AND engine='chatgpt';", ("@id", f.Site)));
        Assert.True((DateTime)(await f.Scalar("SELECT monitoring_enabled_at FROM site_targets WHERE site_id=UUID_TO_BIN(@id) AND engine=@engine;", ("@id", f.Site), ("@engine", engine)))! >= before);
        var plan = Assert.Single((await f.Schedules.ListAsync(f.Tenant, f.Site, null, 100)).Items);
        Assert.Equal(auto.Id, plan.Id);
        Assert.False(plan.Enabled);
        Assert.Null(plan.NextSlotAt);
        Assert.Equal(2, plan.Selection.Targets.Count);
        var targets = await f.Management.GetSiteTargetsAsync(f.Tenant, f.Site);
        targets = await f.Management.PutSiteTargetsAsync(f.Tenant, f.Site, new(targets.ConfigVersion, [Fixture.ChatGpt, f.AddedTarget with { Enabled = false }]));
        var disabledBoundary = await f.Scalar("SELECT monitoring_enabled_at FROM site_targets WHERE site_id=UUID_TO_BIN(@id) AND engine=@engine;", ("@id", f.Site), ("@engine", engine));
        await f.Management.PutSiteTargetsAsync(f.Tenant, f.Site, new(targets.ConfigVersion, [Fixture.ChatGpt, f.AddedTarget]));
        Assert.True((DateTime)(await f.Scalar("SELECT monitoring_enabled_at FROM site_targets WHERE site_id=UUID_TO_BIN(@id) AND engine=@engine;", ("@id", f.Site), ("@engine", engine)))! > (DateTime)disabledBoundary!);
        Assert.Null(await f.Scalar("SELECT monitoring_enabled_at FROM site_targets WHERE site_id=UUID_TO_BIN(@id) AND engine='chatgpt';", ("@id", f.Site)));
        Assert.Equal(0, Convert.ToInt32(await f.Scalar("SELECT COUNT(*) FROM schedule_slots WHERE schedule_id=UUID_TO_BIN(@id);", ("@id", auto.Id))));
    }

    [Theory]
    [InlineData("gemini")]
    [InlineData("copilot")]
    public async Task MonitoringSnapshotReadbackPreservesQualityHistoryPaginationAndArrivalFence(string engine)
    {
        var f = await Fixture.Create(engine);
        var run = await f.Runs.CreateRunAsync(f.RunCommand() with { SampleCount = 2 });
        var checks = (await f.Runs.GetChecksAsync(f.Tenant, run.RunId, null, 20, engine, null)).Items;
        Assert.Equal(2, checks.Count);
        foreach (var check in checks) await f.Complete(check);
        var analytics = new AnalyticsQueryStore(f.Database, new AnalysisStore(f.Database));
        var filter = new AnalyticsFilterRequest(Providers: ["cloro"], Engines: [engine],
            CaptureModes: ["scraped"], EvidenceClasses: ["observed_surface"]);
        var first = await analytics.MonitoringRecordsAsync(f.Tenant, f.Site, filter, null, null, 1);
        var row = Assert.Single(first.Items);
        Assert.Equal(engine, row.Engine);
        Assert.Equal("unknown", row.MentionState);
        Assert.NotNull(first.NextCursor);
        var detail = (await analytics.MonitoringAnswerDetailAsync(f.Tenant, f.Site, row.CheckId, first.Meta.SnapshotToken, null))!;
        Assert.Equal("已保存问题", detail.PromptSnapshot);
        Assert.Equal("保存的答案", detail.Answer!.Text);
        Assert.True(detail.ModelVersionUnavailable);
        Assert.Equal("unknown", detail.MentionState);
        Assert.Equal(JsonSerializer.Serialize(row.ResultQuality), JsonSerializer.Serialize(detail.ResultQuality));
        Assert.Equal(2, detail.Citations.Count);
        var laterRun = await f.Runs.CreateRunAsync(f.RunCommand());
        var laterCheck = Assert.Single((await f.Runs.GetChecksAsync(f.Tenant, laterRun.RunId, null, 20, engine, null)).Items);
        await f.Complete(laterCheck);
        Assert.Equal("SNAPSHOT_REFRESH_REQUIRED", (await Assert.ThrowsAsync<CollectorStoreException>(() =>
            analytics.MonitoringAnswerDetailAsync(f.Tenant, f.Site, laterCheck.CheckId, first.Meta.SnapshotToken, null))).Code);
        await f.Exec("UPDATE prompts SET content='edited',enabled=FALSE,archived_at=UTC_TIMESTAMP(6) WHERE id=UUID_TO_BIN(@id);", ("@id", f.Prompt));
        var second = await analytics.MonitoringRecordsAsync(f.Tenant, f.Site, filter, first.Meta.SnapshotToken, first.NextCursor, 1);
        Assert.Single(second.Items);
        Assert.NotEqual(row.CheckId, second.Items[0].CheckId);
        Assert.Equal("已保存问题", (await analytics.MonitoringAnswerDetailAsync(f.Tenant, f.Site,
            row.CheckId, first.Meta.SnapshotToken, null))!.PromptSnapshot);
        Assert.Equal("RESOURCE_NOT_FOUND", (await Assert.ThrowsAsync<CollectorStoreException>(() =>
            analytics.MonitoringAnswerDetailAsync(Guid.NewGuid(), f.Site, row.CheckId, first.Meta.SnapshotToken, null))).Code);
        Assert.Equal("RESOURCE_NOT_FOUND", (await Assert.ThrowsAsync<CollectorStoreException>(() =>
            analytics.MonitoringAnswerDetailAsync(f.Tenant, Guid.NewGuid(), row.CheckId, first.Meta.SnapshotToken, null))).Code);
    }

    [Theory]
    [InlineData("gemini")]
    [InlineData("copilot")]
    public async Task InsufficientAutomaticCreditsCreateNoRunTasksOrReservation(string engine)
    {
        var f = await Fixture.Create(engine);
        await f.Exec("UPDATE credit_accounts SET available_credits=9 WHERE tenant_id=UUID_TO_BIN(@id);", ("@id", f.Tenant));
        var error = await Assert.ThrowsAsync<CollectorStoreException>(() => f.Runs.CreateRunAsync(
            f.RunCommand() with { ChargeMonitoringCredits = true, RequireEnabledTargets = true }));
        Assert.Equal("CREDIT_INSUFFICIENT", error.Code);
        Assert.Equal(0, Convert.ToInt32(await f.Scalar("SELECT COUNT(*) FROM visibility_runs WHERE site_id=UUID_TO_BIN(@id);", ("@id", f.Site))));
        Assert.Equal(0, Convert.ToInt32(await f.Scalar("SELECT COUNT(*) FROM provider_tasks t JOIN visibility_runs r ON r.id=t.run_id WHERE r.tenant_id=UUID_TO_BIN(@id);", ("@id", f.Tenant))));
        Assert.Equal(0, Convert.ToInt32(await f.Scalar("SELECT COUNT(*) FROM monitoring_credit_reservations WHERE tenant_id=UUID_TO_BIN(@id);", ("@id", f.Tenant))));
    }

    [Theory]
    [InlineData("gemini")]
    [InlineData("copilot")]
    public async Task AutomaticSlotIncludesOnlyTwoSavedEnabledPromptsAndReservesTwentyCredits(string engine)
    {
        var f = await Fixture.Create(engine);
        var second = await f.Management.CreatePromptAsync(f.Tenant, f.Site, new("第二个已保存问题"));
        var disabled = await f.Management.CreatePromptAsync(f.Tenant, f.Site, new("停用问题", Enabled: false));
        var targets = await f.Management.GetSiteTargetsAsync(f.Tenant, f.Site);
        await f.Management.PutSiteTargetsAsync(f.Tenant, f.Site, new(targets.ConfigVersion, [f.AddedTarget]));
        var auto = await f.Schedules.CreateAsync(new(f.Tenant, f.Site, PromptMonitoringIdentity.ScheduleName,
            "daily", null, "09:00", "UTC", new([f.Prompt], f.RunCommand().Targets), true));
        var due = DateTimeOffset.UtcNow.AddDays(1).Date.AddHours(9);
        await f.Exec("UPDATE collection_schedules SET next_slot_at=@due WHERE id=UUID_TO_BIN(@id);", ("@due", due), ("@id", auto.Id));
        await f.Schedules.MaterializeDueSlotsAsync(new DateTimeOffset(due, TimeSpan.Zero), 10);
        var slot = Assert.Single(await f.Schedules.GetPendingSlotsAsync(100), s => s.ScheduleId == auto.Id);
        Assert.Equal(new[] { f.Prompt, second.PromptId }.Order(), slot.Selection.PromptIds.Order());
        Assert.DoesNotContain(disabled.PromptId, slot.Selection.PromptIds);
        Assert.Equal(engine, Assert.Single(slot.Selection.Targets).Engine);
        Assert.True(await f.Schedules.ProcessSlotAsync(slot, f.Runs));
        var run = Guid.Parse((string)(await f.Scalar("SELECT BIN_TO_UUID(run_id) FROM schedule_slots WHERE id=UUID_TO_BIN(@id);", ("@id", slot.Id)))!);
        var checks = (await f.Runs.GetChecksAsync(f.Tenant, run, null, 20, engine, null)).Items;
        Assert.Equal(2, checks.Count);
        Assert.Equal(new[] { f.Prompt, second.PromptId }.Order(), checks.Select(c => c.PromptId).Order());
        Assert.Equal(20m, Convert.ToDecimal(await f.Scalar("SELECT held_credits FROM monitoring_credit_reservations WHERE run_id=UUID_TO_BIN(@id);", ("@id", run))));
        await f.Exec("UPDATE collection_schedules SET enabled=FALSE,next_slot_at=NULL WHERE id=UUID_TO_BIN(@id);", ("@id", auto.Id));
        await f.StopUnused(run);
    }

    [Theory]
    [InlineData("gemini")]
    [InlineData("copilot")]
    public async Task SubmissionUnknownRecoveryHoldsCreditsWhilePreSubmitFailureReleasesAndSubmittedFailureStaysCaptured(string engine)
    {
        var f = await Fixture.Create(engine);
        var run = await f.Runs.CreateRunAsync(f.RunCommand() with { SampleCount = 3, ChargeMonitoringCredits = true });
        var checks = (await f.Runs.GetChecksAsync(f.Tenant, run.RunId, null, 20, engine, null)).Items;
        Assert.Equal(3, checks.Count);
        var submitted = await f.Lease(checks[0]);
        await f.Runs.MarkSubmittingAsync(submitted, "gemini-sql");
        await f.Runs.RecordSubmissionAsync(submitted, "gemini-sql", new("fixture-task-" + Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, "hash"));
        submitted = await f.Lease(checks[0]);
        await f.Runs.FailTaskAsync(submitted, "gemini-sql", "CLORO_TASK_FAILED", false);
        var unknown = await f.Lease(checks[1]);
        await f.Runs.MarkSubmittingAsync(unknown, "gemini-sql");
        await f.Exec("UPDATE provider_tasks SET lease_until=DATE_SUB(UTC_TIMESTAMP(6),INTERVAL 1 MINUTE) WHERE id=UUID_TO_BIN(@id);", ("@id", unknown.TaskId));
        var scoped = new CloroVisibilityStore(f.Database, f.Tenant, run.RunId);
        Assert.Equal(1, await scoped.RecoverExpiredTasksAsync());
        Assert.Equal("submission_unknown", await f.Scalar("SELECT state FROM provider_tasks WHERE id=UUID_TO_BIN(@id);", ("@id", unknown.TaskId)));
        var rejected = await f.Lease(checks[2]);
        await f.Runs.MarkSubmittingAsync(rejected, "gemini-sql");
        await f.Runs.FailTaskAsync(rejected, "gemini-sql", "CLORO_SUBMIT_FAILED", false);
        Assert.Empty(await scoped.LeaseTasksAsync("must-not-resubmit"));
        foreach (var column in new[] { "held_credits", "captured_credits", "released_credits" })
            Assert.Equal(10m, Convert.ToDecimal(await f.Scalar($"SELECT {column} FROM monitoring_credit_reservations WHERE run_id=UUID_TO_BIN(@id);", ("@id", run.RunId))));
        Assert.Equal("unknown", await f.Scalar("SELECT status FROM monitoring_credit_reservations WHERE run_id=UUID_TO_BIN(@id);", ("@id", run.RunId)));
        Assert.Equal("STALE_LEASE", (await Assert.ThrowsAsync<CollectorStoreException>(() =>
            f.Runs.FailTaskAsync(submitted, "gemini-sql", "CLORO_TASK_FAILED", false))).Code);
        Assert.Equal(1, Convert.ToInt32(await f.Scalar("SELECT COUNT(*) FROM credit_ledger WHERE monitoring_check_id=UUID_TO_BIN(@id) AND entry_type='capture';", ("@id", checks[0].CheckId))));
    }

    private sealed class Fixture(string connectionString, string engine)
    {
        internal static readonly SiteTargetWrite ChatGpt = new("cloro", "chatgpt", "scraped", "US", "en-US");
        internal SiteTargetWrite AddedTarget => ChatGpt with { Engine = engine };
        internal static readonly CloroVisibilityTarget ChatGptTarget = new("cloro", "chatgpt", "en-US", "US", "{}");
        internal Guid Tenant { get; } = Guid.NewGuid();
        internal Guid Site { get; } = Guid.NewGuid();
        internal Guid Prompt { get; } = Guid.NewGuid();
        internal Guid Version { get; } = Guid.NewGuid();
        internal MySqlDatabase Database { get; } = new(connectionString);
        internal CloroVisibilityStore Runs => new(Database);
        internal ManagementStore Management => new(Database);
        internal CollectionScheduleStore Schedules => new(Database);
        internal CreateCloroVisibilityRunCommand RunCommand() => new(Tenant, Site, [Prompt],
            [new("cloro", engine, "en-US", "US", "{}")], engine + "-fixture-" + Guid.NewGuid().ToString("N"));

        internal static async Task<Fixture> Create(string engine)
        {
            var f = new Fixture(RequireIsolatedConnection(Environment.GetEnvironmentVariable("JOGEOAI_GEMINI_TEST_CONNECTION")), engine);
            await f.ApplyMigration();
            await f.Exec("""
                INSERT INTO tenants(id,name,status,created_at,updated_at) VALUES(UUID_TO_BIN(@tenant),'Gemini SQL fixture','active',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
                INSERT INTO sites(id,tenant_id,host,display_name,status,created_at,updated_at)
                  VALUES(UUID_TO_BIN(@site),UUID_TO_BIN(@tenant),@host,'Gemini SQL fixture','active',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
                INSERT INTO site_brand_settings(site_id,tenant_id,created_at,updated_at)
                  VALUES(UUID_TO_BIN(@site),UUID_TO_BIN(@tenant),UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
                INSERT INTO prompts(id,site_id,content,locale,enabled,created_at,updated_at)
                  VALUES(UUID_TO_BIN(@prompt),UUID_TO_BIN(@site),'已保存问题','en-US',TRUE,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
                INSERT INTO prompt_versions(id,site_id,prompt_id,version,content,locale,branded_status,branded_source,created_at)
                  VALUES(UUID_TO_BIN(@version),UUID_TO_BIN(@site),UUID_TO_BIN(@prompt),1,'已保存问题','en-US','unbranded','system',UTC_TIMESTAMP(6));
                UPDATE prompts SET current_version_id=UUID_TO_BIN(@version) WHERE id=UUID_TO_BIN(@prompt);
                INSERT INTO credit_accounts(tenant_id,available_credits,lifetime_granted,updated_at)
                  VALUES(UUID_TO_BIN(@tenant),1000,1000,UTC_TIMESTAMP(6));
                """, ("@tenant", f.Tenant), ("@site", f.Site), ("@host", $"gemini-{f.Site:N}.test"), ("@prompt", f.Prompt), ("@version", f.Version));
            var targets = await f.Management.GetSiteTargetsAsync(f.Tenant, f.Site);
            await f.Management.PutSiteTargetsAsync(f.Tenant, f.Site, new(targets.ConfigVersion, [ChatGpt, f.AddedTarget]));
            // Treat initial ChatGPT as the existing plan member, with unknown historical activation.
            await f.Exec("UPDATE site_targets SET monitoring_enabled_at=NULL WHERE site_id=UUID_TO_BIN(@id) AND engine='chatgpt';", ("@id", f.Site));
            return f;
        }

        internal async Task<CloroTaskLease> Lease(CloroVisibilityCheckView check)
        {
            await Exec("UPDATE provider_tasks SET state='leased',lease_owner='gemini-sql',lease_until=DATE_ADD(UTC_TIMESTAMP(6),INTERVAL 2 MINUTE),lease_version=lease_version+1 WHERE check_id=UUID_TO_BIN(@id);", ("@id", check.CheckId));
            var task = Guid.Parse((string)(await Scalar("SELECT BIN_TO_UUID(id) FROM provider_tasks WHERE check_id=UUID_TO_BIN(@id);", ("@id", check.CheckId)))!);
            var version = Convert.ToInt32(await Scalar("SELECT lease_version FROM provider_tasks WHERE id=UUID_TO_BIN(@id);", ("@id", task)));
            return new(task, Tenant, check.RunId, check.CheckId, check.PromptSnapshot, engine, "en-US", "US",
                "leased", null, 0, DateTimeOffset.UtcNow.AddMinutes(2), version, DateTimeOffset.UtcNow, null, null);
        }

        internal async Task Complete(CloroVisibilityCheckView check)
        {
            var lease = await Lease(check);
            await Runs.MarkSubmittingAsync(lease, "gemini-sql");
            await Runs.RecordSubmissionAsync(lease, "gemini-sql", new("fixture-task-" + Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, "hash"));
            lease = await Lease(check);
            var answer = new CloroAnswerSnapshot("cloro", engine, "scraped", "observed_surface", engine,
                "保存的答案", true, [new("https://github.com/a", "github.com", null, 1), new("https://github.com/a", "github.com", "第二行", 2)],
                [], JsonSerializer.SerializeToElement(new { text = "保存的答案" }), DateTimeOffset.UtcNow, "cloro-v2",
                QueryAvailability: "not_exposed", ResultQuality: new(false, false, new("available", "complete", 2, 2, 0, 0, 0, [])));
            await Runs.CompleteTaskAsync(lease, "gemini-sql", answer, 5);
        }

        internal Task StopUnused(Guid run) => Exec("UPDATE provider_tasks SET next_attempt_at='2099-01-01' WHERE run_id=UUID_TO_BIN(@id);", ("@id", run));
        internal async Task Exec(string sql, params (string Key, object? Value)[] args)
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = Bind(new(sql, connection), args);
            await command.ExecuteNonQueryAsync();
        }
        internal async Task<object?> Scalar(string sql, params (string Key, object? Value)[] args)
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = Bind(new(sql, connection), args);
            var value = await command.ExecuteScalarAsync();
            return value is DBNull ? null : value;
        }
        private static MySqlCommand Bind(MySqlCommand command, (string Key, object? Value)[] args)
        {
            foreach (var (key, value) in args) command.Parameters.AddWithValue(key, value is Guid id ? id.ToString() : value ?? DBNull.Value);
            return command;
        }
        internal async Task ApplyMigration()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Jogeoai.slnx"))) directory = directory.Parent;
            Assert.NotNull(directory);
            var lines = await File.ReadAllLinesAsync(Path.Combine(directory.FullName, "db/migrations/021_gemini_result_quality.sql"));
            var delimiter = ";";
            var statement = new StringBuilder();
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.StartsWith("--", StringComparison.Ordinal) || line.Length == 0) continue;
                if (line.StartsWith("DELIMITER ", StringComparison.Ordinal)) { delimiter = line[10..]; continue; }
                statement.AppendLine(line);
                if (!line.EndsWith(delimiter, StringComparison.Ordinal)) continue;
                var sql = statement.ToString().TrimEnd();
                await Exec(sql[..^delimiter.Length]);
                statement.Clear();
            }
            Assert.Equal(0, statement.Length);
        }
    }
}

[CollectionDefinition("Gemini isolated SQL", DisableParallelization = true)]
public sealed class GeminiIsolatedSqlCollection;
