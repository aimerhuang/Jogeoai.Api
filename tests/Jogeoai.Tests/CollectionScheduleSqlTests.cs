using System.Data;
using System.Reflection;
using System.Text.Json;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using MySqlConnector;

namespace Jogeoai.Tests;

public sealed class CollectionScheduleSqlTests
{
    [Stage1MySqlFact]
    public async Task MigratedMySql_ClaimsOnceRecoversAndRejectsStaleVersionAndTenant()
    {
        var connectionString = Environment.GetEnvironmentVariable("JOGEOAI_STAGE1_TEST_CONNECTION")!;
        var builder = new MySqlConnectionStringBuilder(connectionString);
        Assert.Equal("jogeoai_stage1_20260926", builder.Database);
        var database = new MySqlDatabase(connectionString);
        var schedules = new CollectionScheduleStore(database);
        var runs = new CloroVisibilityStore(database);
        var tenantId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var promptId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        await SeedAsync(connectionString, tenantId, siteId, promptId, versionId);
        var selection = new CollectionScheduleSelection([promptId], [new CloroVisibilityTarget("cloro", "chatgpt", "en-US", "US")], 1);

        var created = await schedules.CreateAsync(new CreateCollectionScheduleCommand(tenantId, siteId,
            "schedule-sql-test", "daily", null, "09:00", "UTC", selection));
        var missingTarget = await Assert.ThrowsAsync<CollectorStoreException>(() => schedules.CreateAsync(
            new CreateCollectionScheduleCommand(tenantId, siteId, "unknown-target", "daily", null, "09:00", "UTC",
                new CollectionScheduleSelection([promptId], [new CloroVisibilityTarget("cloro", "perplexity", "en-US", "US")]))));
        Assert.Equal("TARGET_NOT_ENABLED", missingTarget.Code);
        Assert.False(created.Enabled);
        Assert.Null(created.NextSlotAt);
        Assert.Equal(1, created.Version);
        var second = await schedules.CreateAsync(new CreateCollectionScheduleCommand(tenantId, siteId,
            "schedule-sql-test-2", "weekly", 6, "10:00", "UTC", selection));
        var firstPage = await schedules.ListAsync(tenantId, siteId, null, 1);
        Assert.Single(firstPage.Items);
        Assert.NotNull(firstPage.NextCursor);
        var secondPage = await schedules.ListAsync(tenantId, siteId, firstPage.NextCursor, 1);
        Assert.Single(secondPage.Items);
        Assert.Null(secondPage.NextCursor);
        Assert.Equal(new HashSet<Guid> { created.Id, second.Id },
            new HashSet<Guid> { firstPage.Items[0].Id, secondPage.Items[0].Id });
        var changedCadence = await schedules.UpdateAsync(new UpdateCollectionScheduleCommand(tenantId, siteId,
            second.Id, second.Version, Cadence: " DAILY "));
        Assert.NotNull(changedCadence);
        Assert.Equal("daily", changedCadence.Cadence);
        Assert.Null(changedCadence.DayOfWeek);
        var foreign = await Assert.ThrowsAsync<CollectorStoreException>(() => schedules.ListAsync(Guid.NewGuid(), siteId, null, 50));
        Assert.Equal(404, foreign.StatusCode);
        var invalidCursor = await Assert.ThrowsAsync<CollectorStoreException>(() => schedules.ListAsync(tenantId, siteId, "bad", 50));
        Assert.Equal("INVALID_CURSOR", invalidCursor.Code);

        var enabled = await schedules.UpdateAsync(new UpdateCollectionScheduleCommand(tenantId, siteId,
            created.Id, 1, Enabled: true));
        Assert.NotNull(enabled);
        Assert.Equal(2, enabled.Version);
        Assert.NotNull(enabled.NextSlotAt);
        var conflict = await Assert.ThrowsAsync<CollectorStoreException>(() => schedules.UpdateAsync(
            new UpdateCollectionScheduleCommand(tenantId, siteId, created.Id, 1, Name: "stale")));
        Assert.Equal("SCHEDULE_VERSION_CONFLICT", conflict.Code);
        Assert.Equal(409, conflict.StatusCode);

        // Simulate a three-day outage without waiting for the wall clock.
        await SetNextDueAsync(connectionString, created.Id, DateTime.UtcNow.Date.AddDays(-3).AddHours(9));
        Assert.Equal(1, await schedules.MaterializeDueSlotsAsync(DateTimeOffset.UtcNow, 10));
        var slot = Assert.Single(await schedules.GetPendingSlotsAsync(100), item => item.ScheduleId == created.Id);
        Assert.True(slot.MissedSlots >= 2);
        var otherWorker = new CollectionScheduleStore(database);
        var processed = await Task.WhenAll(schedules.ProcessSlotAsync(slot, runs), otherWorker.ProcessSlotAsync(slot, runs));
        Assert.Equal(1, processed.Count(value => value));
        Assert.Equal(1, await CountAsync(connectionString,
            "SELECT COUNT(*) FROM visibility_runs WHERE tenant_id=UUID_TO_BIN(@tenant) AND idempotency_key=@key;",
            ("@tenant", tenantId.ToString()), ("@key", Key(slot))));
        Assert.Equal(1, await CountAsync(connectionString,
            "SELECT COUNT(*) FROM schedule_slots WHERE id=UUID_TO_BIN(@slot) AND status='completed' AND run_id IS NOT NULL;",
            ("@slot", slot.Id.ToString())));

        // A target disabled after materialization skips a fresh slot, but a committed Run is still recoverable.
        await using (var connection = new MySqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await ExecuteAsync(connection, null,
                "UPDATE site_targets SET enabled=FALSE WHERE site_id=UUID_TO_BIN(@site);", ("@site", siteId.ToString()));
        }
        var disabledTargetSlot = await InsertPendingSlotAsync(connectionString, created.Id, selection, "20990103T0900");
        Assert.False(await schedules.ProcessSlotAsync(disabledTargetSlot, runs));
        Assert.Equal(1, await CountAsync(connectionString,
            "SELECT COUNT(*) FROM schedule_slots WHERE id=UUID_TO_BIN(@slot) AND status='skipped' AND run_id IS NULL;",
            ("@slot", disabledTargetSlot.Id.ToString())));
        Assert.Equal(0, await CountAsync(connectionString,
            "SELECT COUNT(*) FROM visibility_runs WHERE tenant_id=UUID_TO_BIN(@tenant) AND idempotency_key=@key;",
            ("@tenant", tenantId.ToString()), ("@key", Key(disabledTargetSlot))));
        var targetConflict = await Assert.ThrowsAsync<CollectorStoreException>(() => schedules.UpdateAsync(
            new UpdateCollectionScheduleCommand(tenantId, siteId, created.Id, 2, Name: "still-active")));
        Assert.Equal("TARGET_NOT_ENABLED", targetConflict.Code);

        // A Run may commit before the schedule transaction. The next pass links that same Run.
        var pending = await InsertPendingSlotAsync(connectionString, created.Id, selection, "20990101T0900");
        var command = RunCommand(tenantId, siteId, pending);
        var run = await runs.CreateRunAsync(command);
        var disabled = await schedules.UpdateAsync(new UpdateCollectionScheduleCommand(tenantId, siteId,
            created.Id, 2, Enabled: false));
        Assert.NotNull(disabled);
        Assert.Equal(3, disabled.Version);
        Assert.True(await schedules.ProcessSlotAsync(pending, runs));
        Assert.Equal(1, await CountAsync(connectionString,
            "SELECT COUNT(*) FROM schedule_slots WHERE id=UUID_TO_BIN(@slot) AND run_id=UUID_TO_BIN(@run) AND status='completed';",
            ("@slot", pending.Id.ToString()), ("@run", run.RunId.ToString())));

        var skipped = await InsertPendingSlotAsync(connectionString, created.Id, selection, "20990102T0900");
        Assert.False(await schedules.ProcessSlotAsync(skipped, runs));
        Assert.Equal(0, await CountAsync(connectionString,
            "SELECT COUNT(*) FROM visibility_runs WHERE tenant_id=UUID_TO_BIN(@tenant) AND idempotency_key=@key;",
            ("@tenant", tenantId.ToString()), ("@key", Key(skipped))));
        Assert.Equal(1, await CountAsync(connectionString,
            "SELECT COUNT(*) FROM schedule_slots WHERE id=UUID_TO_BIN(@slot) AND status='skipped' AND run_id IS NULL;",
            ("@slot", skipped.Id.ToString())));

        await using (var connection = new MySqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await ExecuteAsync(connection, null,
                "UPDATE site_targets SET enabled=TRUE WHERE site_id=UUID_TO_BIN(@site);", ("@site", siteId.ToString()));
        }
        var disabledPlan = await schedules.CreateAsync(new CreateCollectionScheduleCommand(tenantId, siteId,
            "race-disabled", "daily", null, "09:00", "UTC", selection));
        await using (var archiveConnection = new MySqlConnection(connectionString))
        {
            await archiveConnection.OpenAsync();
            await using var archiveTransaction = await archiveConnection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            await ExecuteAsync(archiveConnection, archiveTransaction,
                "UPDATE sites SET status='archived' WHERE id=UUID_TO_BIN(@site);", ("@site", siteId.ToString()));
            var racingCreate = schedules.CreateAsync(new CreateCollectionScheduleCommand(tenantId, siteId,
                "race-created", "daily", null, "09:00", "UTC", selection, Enabled: true));
            var racingEnable = schedules.UpdateAsync(new UpdateCollectionScheduleCommand(tenantId, siteId,
                disabledPlan.Id, disabledPlan.Version, Enabled: true));
            await Task.Delay(100);
            await archiveTransaction.CommitAsync();
            var archiveError = await Assert.ThrowsAsync<CollectorStoreException>(() => racingCreate);
            Assert.Equal(404, archiveError.StatusCode);
            Assert.Null(await racingEnable);
        }
        Assert.Equal(0, await CountAsync(connectionString,
            "SELECT COUNT(*) FROM collection_schedules WHERE site_id=UUID_TO_BIN(@site) AND name='race-created';",
            ("@site", siteId.ToString())));
        await using (var connection = new MySqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await ExecuteAsync(connection, null,
                "UPDATE sites SET status='active' WHERE id=UUID_TO_BIN(@site);", ("@site", siteId.ToString()));
        }
        var archiveCandidate = await schedules.CreateAsync(new CreateCollectionScheduleCommand(tenantId, siteId,
            "archive-candidate", "daily", null, "09:00", "UTC", selection, Enabled: true));
        await SetNextDueAsync(connectionString, archiveCandidate.Id, DateTime.UtcNow.Date.AddDays(-1).AddHours(9));
        await using (var connection = new MySqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await ExecuteAsync(connection, null,
                "UPDATE sites SET status='archived' WHERE id=UUID_TO_BIN(@site);", ("@site", siteId.ToString()));
        }
        Assert.Equal(0, await schedules.MaterializeDueSlotsAsync(DateTimeOffset.UtcNow, 10));
        Assert.Equal(1, await CountAsync(connectionString, """
            SELECT COUNT(*) FROM collection_schedules
            WHERE id=UUID_TO_BIN(@id) AND enabled=FALSE AND next_slot_at IS NULL AND version=2;
            """, ("@id", archiveCandidate.Id.ToString())));
        Assert.Equal(0, await CountAsync(connectionString,
            "SELECT COUNT(*) FROM schedule_slots WHERE schedule_id=UUID_TO_BIN(@id);",
            ("@id", archiveCandidate.Id.ToString())));
    }

    [Stage1MySqlFact]
    public async Task MigratedMySql_MonitoringSlotReleasesNamedLockOnFailureAndAfterCommittedRun()
    {
        var connectionString = Environment.GetEnvironmentVariable("JOGEOAI_STAGE1_TEST_CONNECTION")!;
        Assert.Equal("jogeoai_stage1_20260926", new MySqlConnectionStringBuilder(connectionString).Database);
        var database = new MySqlDatabase(connectionString);
        var schedules = new CollectionScheduleStore(database);
        var tenantId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var promptId = Guid.NewGuid();
        await SeedAsync(connectionString, tenantId, siteId, promptId, Guid.NewGuid());
        await using (var connection = new MySqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await ExecuteAsync(connection, null, """
                INSERT INTO credit_accounts (tenant_id,available_credits,lifetime_granted,updated_at)
                VALUES (UUID_TO_BIN(@tenant),1000,1000,UTC_TIMESTAMP(6));
                """, ("@tenant", tenantId.ToString()));
        }
        var selection = new CollectionScheduleSelection([promptId], [new CloroVisibilityTarget("cloro", "chatgpt", "en-US", "US")], 1);
        var plan = await schedules.CreateAsync(new CreateCollectionScheduleCommand(tenantId, siteId,
            PromptMonitoringIdentity.ScheduleName, "daily", null, "09:00", "UTC", selection, Enabled: true));
        try
        {
            var slot = await InsertPendingSlotAsync(connectionString, plan.Id, selection, "20990105T0900");

            // Failure after GET_LOCK leaves the slot transaction active when finally releases the lock.
            var failing = DispatchProxy.Create<ICloroVisibilityStore, FailingRunsProxy>();
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => schedules.ProcessSlotAsync(slot, failing));
            Assert.Equal(FailingRunsProxy.Message, failure.Message);
            Assert.Null(await MonitoringLockOwnerAsync(connectionString, siteId));
            Assert.Equal(1, await CountAsync(connectionString,
                "SELECT COUNT(*) FROM schedule_slots WHERE id=UUID_TO_BIN(@slot) AND status='pending' AND run_id IS NULL;",
                ("@slot", slot.Id.ToString())));

            // Normal path: acquire, create the charged Run, commit, then release without the finished transaction.
            Assert.True(await schedules.ProcessSlotAsync(slot, new CloroVisibilityStore(database)));
            Assert.Null(await MonitoringLockOwnerAsync(connectionString, siteId));
            Assert.Equal(1, await CountAsync(connectionString, """
                SELECT COUNT(*) FROM schedule_slots slot JOIN visibility_runs run ON run.id=slot.run_id
                WHERE slot.id=UUID_TO_BIN(@slot) AND slot.status='completed' AND run.idempotency_key=@key
                  AND run.requested_by=@by AND run.total_checks=1;
                """, ("@slot", slot.Id.ToString()), ("@key", Key(slot)), ("@by", $"schedule:{plan.Id:N}")));
            Assert.Equal(1, await CountAsync(connectionString, """
                SELECT COUNT(*) FROM monitoring_credit_reservations reservation
                JOIN schedule_slots slot ON slot.run_id=reservation.run_id
                WHERE slot.id=UUID_TO_BIN(@slot) AND reservation.held_credits=@held;
                """, ("@slot", slot.Id.ToString()), ("@held", PromptMonitoringPricing.UnitPrice)));
        }
        finally
        {
            // Keep rows for inspection, but never leave a due plan or runnable provider tasks behind.
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();
            await ExecuteAsync(connection, null,
                "UPDATE collection_schedules SET enabled=FALSE,next_slot_at=NULL WHERE id=UUID_TO_BIN(@id);",
                ("@id", plan.Id.ToString()));
            await ExecuteAsync(connection, null,
                "UPDATE provider_tasks t JOIN visibility_checks c ON c.id=t.check_id SET t.next_attempt_at='2099-01-01' WHERE c.site_id=UUID_TO_BIN(@site);",
                ("@site", siteId.ToString()));
        }
    }

    [PromptScopeMySqlFact]
    public async Task MigratedMySql_CustomPromptScopesExpandOnlyAllowedPairsAndReserveTheSameCount()
    {
        // Isolated database with 001–042 applied; never the shared jogeoai business database.
        var connectionString = Environment.GetEnvironmentVariable("JOGEOAI_PROMPT_SCOPE_TEST_CONNECTION")!;
        Assert.StartsWith("jogeoai_prompt_scope_", new MySqlConnectionStringBuilder(connectionString).Database, StringComparison.Ordinal);
        var database = new MySqlDatabase(connectionString);
        var schedules = new CollectionScheduleStore(database);
        var tenantId = Guid.NewGuid();
        var siteId = Guid.NewGuid();
        var allPrompt = Guid.NewGuid();
        var geminiPrompt = Guid.NewGuid();
        var disabledScopePrompt = Guid.NewGuid();
        var chatgpt = new CloroVisibilityTarget("cloro", "chatgpt", "en-US", "US");
        var gemini = new CloroVisibilityTarget("cloro", "gemini", "en-US", "US");
        var copilot = new CloroVisibilityTarget("cloro", "copilot", "en-US", "US");
        await SeedAsync(connectionString, tenantId, siteId, allPrompt, Guid.NewGuid());
        await using (var connection = new MySqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await ExecuteAsync(connection, null, """
                INSERT INTO site_targets (id,tenant_id,site_id,provider,engine,capture_mode,country,locale,target_key,enabled,created_at,updated_at)
                VALUES (UUID_TO_BIN(@id),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),'cloro','gemini','scraped','US','en-US',@key,TRUE,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
                """, ("@id", Guid.NewGuid().ToString()), ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()),
                ("@key", CollectionIdentity.TargetKey(gemini)));
            foreach (var (prompt, key) in new[] { (geminiPrompt, CollectionIdentity.TargetKey(gemini)), (disabledScopePrompt, CollectionIdentity.TargetKey(copilot)) })
            {
                var version = Guid.NewGuid();
                await ExecuteAsync(connection, null,
                    "INSERT INTO prompts (id,site_id,content,locale,enabled,target_scope,created_at,updated_at) VALUES (UUID_TO_BIN(@id),UUID_TO_BIN(@site),@content,'en-US',TRUE,'custom',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));",
                    ("@id", prompt.ToString()), ("@site", siteId.ToString()), ("@content", $"Scoped question {prompt:N}?"));
                await ExecuteAsync(connection, null,
                    "INSERT INTO prompt_versions (id,site_id,prompt_id,version,content,locale,branded_status,branded_source,created_at) VALUES (UUID_TO_BIN(@id),UUID_TO_BIN(@site),UUID_TO_BIN(@prompt),1,@content,'en-US','unbranded','system',UTC_TIMESTAMP(6));",
                    ("@id", version.ToString()), ("@site", siteId.ToString()), ("@prompt", prompt.ToString()), ("@content", $"Scoped question {prompt:N}?"));
                await ExecuteAsync(connection, null, "UPDATE prompts SET current_version_id=UUID_TO_BIN(@version) WHERE id=UUID_TO_BIN(@prompt);",
                    ("@version", version.ToString()), ("@prompt", prompt.ToString()));
                await ExecuteAsync(connection, null,
                    "INSERT INTO prompt_target_scopes (tenant_id,site_id,prompt_id,target_key,created_at) VALUES (UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@prompt),@key,UTC_TIMESTAMP(6));",
                    ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@prompt", prompt.ToString()), ("@key", key));
            }
            await ExecuteAsync(connection, null, """
                INSERT INTO credit_accounts (tenant_id,available_credits,lifetime_granted,updated_at)
                VALUES (UUID_TO_BIN(@tenant),1000,1000,UTC_TIMESTAMP(6));
                """, ("@tenant", tenantId.ToString()));
        }
        var plan = await schedules.CreateAsync(new CreateCollectionScheduleCommand(tenantId, siteId,
            PromptMonitoringIdentity.ScheduleName, "daily", null, "09:00", "UTC",
            new CollectionScheduleSelection([allPrompt], [chatgpt], 1), Enabled: true));
        try
        {
            // Reconcile reads target_scope + prompt_target_scopes: the copilot-only prompt has no enabled target and is dropped.
            await AutomaticMonitoringSelection.ReconcileAsync(database, tenantId, siteId, default);
            var selection = Assert.Single((await schedules.ListAsync(tenantId, siteId, null, 100)).Items, s => s.Id == plan.Id).Selection;
            Assert.Equal(new[] { allPrompt, geminiPrompt }.Order(), selection.PromptIds);
            Assert.Equal(2, selection.Targets.Count);
            var scope = Assert.Single(selection.PromptTargets!);
            Assert.Equal((geminiPrompt, CollectionIdentity.TargetKey(gemini)), (scope.PromptId, Assert.Single(scope.TargetKeys)));
            Assert.Equal(3, MonitoringMatrix.CountChecks(selection));

            var slot = await InsertPendingSlotAsync(connectionString, plan.Id, selection, "20990106T0900");
            Assert.True(new PromptTargetsComparer().Equals(selection.PromptTargets, slot.Selection.PromptTargets));
            Assert.True(await schedules.ProcessSlotAsync(slot, new CloroVisibilityStore(database)));
            // total_checks, the reservation and the per-check items all come from the same 2 + 1 pair count.
            Assert.Equal(1, await CountAsync(connectionString, """
                SELECT COUNT(*) FROM schedule_slots slot JOIN visibility_runs run ON run.id=slot.run_id
                WHERE slot.id=UUID_TO_BIN(@slot) AND slot.status='completed' AND run.total_checks=3
                  AND JSON_LENGTH(run.selection_snapshot, '$.promptTargets')=1;
                """, ("@slot", slot.Id.ToString())));
            Assert.Equal(1, await CountAsync(connectionString, """
                SELECT COUNT(*) FROM monitoring_credit_reservations reservation
                JOIN schedule_slots slot ON slot.run_id=reservation.run_id
                WHERE slot.id=UUID_TO_BIN(@slot) AND reservation.held_credits=@held AND reservation.estimated_credits=@held;
                """, ("@slot", slot.Id.ToString()), ("@held", 3 * PromptMonitoringPricing.UnitPrice)));
            Assert.Equal(3, await CountAsync(connectionString, """
                SELECT COUNT(*) FROM monitoring_credit_items item JOIN schedule_slots slot ON slot.run_id=item.run_id
                WHERE slot.id=UUID_TO_BIN(@slot) AND item.amount=@unit AND item.status='held';
                """, ("@slot", slot.Id.ToString()), ("@unit", PromptMonitoringPricing.UnitPrice)));
            Assert.Equal(1, await CountAsync(connectionString, """
                SELECT COUNT(*) FROM visibility_checks c JOIN schedule_slots slot ON slot.run_id=c.run_id
                WHERE slot.id=UUID_TO_BIN(@slot) AND c.prompt_id=UUID_TO_BIN(@prompt);
                """, ("@slot", slot.Id.ToString()), ("@prompt", geminiPrompt.ToString())));
            Assert.Equal(1, await CountAsync(connectionString, """
                SELECT COUNT(*) FROM visibility_checks c JOIN schedule_slots slot ON slot.run_id=c.run_id
                WHERE slot.id=UUID_TO_BIN(@slot) AND c.prompt_id=UUID_TO_BIN(@prompt) AND c.engine='gemini';
                """, ("@slot", slot.Id.ToString()), ("@prompt", geminiPrompt.ToString())));
            Assert.Equal(0, await CountAsync(connectionString, """
                SELECT COUNT(*) FROM visibility_checks c JOIN schedule_slots slot ON slot.run_id=c.run_id
                WHERE slot.id=UUID_TO_BIN(@slot) AND c.prompt_id=UUID_TO_BIN(@prompt);
                """, ("@slot", slot.Id.ToString()), ("@prompt", disabledScopePrompt.ToString())));
        }
        finally
        {
            // Keep rows for inspection, but never leave a due plan or runnable provider tasks behind.
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();
            await ExecuteAsync(connection, null,
                "UPDATE collection_schedules SET enabled=FALSE,next_slot_at=NULL WHERE id=UUID_TO_BIN(@id);",
                ("@id", plan.Id.ToString()));
            await ExecuteAsync(connection, null,
                "UPDATE provider_tasks t JOIN visibility_checks c ON c.id=t.check_id SET t.next_attempt_at='2099-01-01' WHERE c.site_id=UUID_TO_BIN(@site);",
                ("@site", siteId.ToString()));
        }
    }

    private sealed class PromptTargetsComparer : IEqualityComparer<IReadOnlyList<CollectionPromptTargets>?>
    {
        public bool Equals(IReadOnlyList<CollectionPromptTargets>? x, IReadOnlyList<CollectionPromptTargets>? y) =>
            x is null || y is null ? x is null && y is null
                : x.Count == y.Count && x.Zip(y).All(pair => pair.First.PromptId == pair.Second.PromptId
                    && pair.First.TargetKeys.SequenceEqual(pair.Second.TargetKeys, StringComparer.Ordinal));
        public int GetHashCode(IReadOnlyList<CollectionPromptTargets>? obj) => obj?.Count ?? 0;
    }

    private static string Key(PendingCollectionSlot slot) => $"schedule:{slot.ScheduleId:N}:{slot.LocalSlotKey}";

    private static async Task<object?> MonitoringLockOwnerAsync(string connectionString, Guid siteId)
    {
        // A separate, unpooled session cannot reuse (and thereby reset) a pooled session that still holds the lock.
        await using var connection = new MySqlConnection(
            new MySqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand("SELECT IS_USED_LOCK(@name);", connection);
        command.Parameters.AddWithValue("@name", $"prompt-monitor:{siteId:N}");
        var owner = await command.ExecuteScalarAsync();
        return owner is DBNull ? null : owner;
    }

    public class FailingRunsProxy : DispatchProxy
    {
        internal const string Message = "fixture: run lookup failed after the monitoring lock was acquired";

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == nameof(ICloroVisibilityStore.GetExistingRunByKeyAsync)
                ? Task.FromException<Guid?>(new InvalidOperationException(Message))
                : throw new NotSupportedException(targetMethod?.Name);
    }

    private static CreateCloroVisibilityRunCommand RunCommand(Guid tenantId, Guid siteId, PendingCollectionSlot slot) =>
        new(tenantId, siteId, slot.Selection.PromptIds, slot.Selection.Targets, Key(slot),
            $"schedule:{slot.ScheduleId:N}", slot.Selection.SampleCount);

    private static async Task SeedAsync(string connectionString, Guid tenantId, Guid siteId, Guid promptId, Guid versionId)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var now = DateTime.UtcNow;
        await ExecuteAsync(connection, transaction,
            "INSERT INTO tenants (id,name,status,created_at,updated_at) VALUES (UUID_TO_BIN(@id),'schedule-test','active',@now,@now);",
            ("@id", tenantId.ToString()), ("@now", now));
        await ExecuteAsync(connection, transaction,
            "INSERT INTO sites (id,tenant_id,host,display_name,status,created_at,updated_at) VALUES (UUID_TO_BIN(@id),UUID_TO_BIN(@tenant),@host,'schedule-test','active',@now,@now);",
            ("@id", siteId.ToString()), ("@tenant", tenantId.ToString()), ("@host", $"schedule-{siteId:N}.test"), ("@now", now));
        await ExecuteAsync(connection, transaction,
            "INSERT INTO site_brand_settings (site_id,tenant_id,created_at,updated_at) VALUES (UUID_TO_BIN(@site),UUID_TO_BIN(@tenant),@now,@now);",
            ("@site", siteId.ToString()), ("@tenant", tenantId.ToString()), ("@now", now));
        await ExecuteAsync(connection, transaction, """
            INSERT INTO site_targets (id,tenant_id,site_id,provider,engine,capture_mode,country,locale,target_key,enabled,created_at,updated_at)
            VALUES (UUID_TO_BIN(@id),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),'cloro','chatgpt','scraped','US','en-US',@key,TRUE,@now,@now);
            """, ("@id", Guid.NewGuid().ToString()), ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()),
            ("@key", CollectionIdentity.TargetKey("cloro", "chatgpt", "scraped", "US", "en-US")), ("@now", now));
        await ExecuteAsync(connection, transaction,
            "INSERT INTO prompts (id,site_id,content,locale,enabled,created_at,updated_at) VALUES (UUID_TO_BIN(@id),UUID_TO_BIN(@site),'What is visible?','en-US',TRUE,@now,@now);",
            ("@id", promptId.ToString()), ("@site", siteId.ToString()), ("@now", now));
        await ExecuteAsync(connection, transaction,
            "INSERT INTO prompt_versions (id,site_id,prompt_id,version,content,locale,branded_status,branded_source,created_at) VALUES (UUID_TO_BIN(@id),UUID_TO_BIN(@site),UUID_TO_BIN(@prompt),1,'What is visible?','en-US','unbranded','system',@now);",
            ("@id", versionId.ToString()), ("@site", siteId.ToString()), ("@prompt", promptId.ToString()), ("@now", now));
        await ExecuteAsync(connection, transaction,
            "UPDATE prompts SET current_version_id=UUID_TO_BIN(@version) WHERE id=UUID_TO_BIN(@prompt);",
            ("@version", versionId.ToString()), ("@prompt", promptId.ToString()));
        await transaction.CommitAsync();
    }

    private static async Task SetNextDueAsync(string connectionString, Guid scheduleId, DateTime dueUtc)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, null, "UPDATE collection_schedules SET next_slot_at=@due WHERE id=UUID_TO_BIN(@id);",
            ("@due", dueUtc), ("@id", scheduleId.ToString()));
    }

    private static async Task<PendingCollectionSlot> InsertPendingSlotAsync(string connectionString, Guid scheduleId,
        CollectionScheduleSelection selection, string localKey)
    {
        var slotId = Guid.NewGuid();
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, null, """
            INSERT INTO schedule_slots (id,schedule_id,local_slot_key,selection_snapshot,status,missed_slots,created_at)
            VALUES (UUID_TO_BIN(@id),UUID_TO_BIN(@schedule),@key,@selection,'pending',0,UTC_TIMESTAMP(6));
            """, ("@id", slotId.ToString()), ("@schedule", scheduleId.ToString()), ("@key", localKey),
            ("@selection", JsonSerializer.Serialize(selection, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        var list = await new CollectionScheduleStore(new MySqlDatabase(connectionString)).GetPendingSlotsAsync(100);
        return Assert.Single(list, item => item.Id == slotId);
    }

    private static async Task<int> CountAsync(string connectionString, string sql, params (string, object?)[] args)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task ExecuteAsync(MySqlConnection connection, MySqlTransaction? tx, string sql,
        params (string, object?)[] args)
    {
        await using var command = new MySqlCommand(sql, connection, tx);
        foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }
}

public sealed class Stage1MySqlFactAttribute : FactAttribute
{
    public Stage1MySqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JOGEOAI_STAGE1_TEST_CONNECTION")))
            Skip = "需提供独立迁移测试库的 JOGEOAI_STAGE1_TEST_CONNECTION。";
    }
}

/// <summary>需要已执行 001–042 的独立库（库名形如 jogeoai_prompt_scope_YYYYMMDD）；未配置时跳过。</summary>
public sealed class PromptScopeMySqlFactAttribute : FactAttribute
{
    public PromptScopeMySqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JOGEOAI_PROMPT_SCOPE_TEST_CONNECTION")))
            Skip = "需提供已执行 001–042 的独立测试库 JOGEOAI_PROMPT_SCOPE_TEST_CONNECTION（库名 jogeoai_prompt_scope_*）。";
    }
}
