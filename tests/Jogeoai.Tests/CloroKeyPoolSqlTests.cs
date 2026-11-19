using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Jogeoai.Providers.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace Jogeoai.Tests;

public sealed class CloroKeyPoolSqlTests
{
    [Fact]
    public void Migration022IsAdditiveReplaySafeAndKeepsSingletonKeyForRollback()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../db/migrations/022_cloro_api_key_pool.sql"));
        Assert.True(File.Exists(path), $"找不到 migration：{path}");
        var sql = File.ReadAllText(path);

        Assert.Contains("CREATE TABLE IF NOT EXISTS cloro_api_keys", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE IF NOT EXISTS cloro_key_events", sql, StringComparison.Ordinal);
        Assert.Contains("UNIQUE KEY uq_cloro_api_keys_sha (api_key_sha256)", sql, StringComparison.Ordinal);
        Assert.Contains("ix_cloro_api_keys_pick (enabled, archived_at, status, priority, cycle_resets_at)", sql, StringComparison.Ordinal);
        Assert.Contains("fk_cloro_key_events_key FOREIGN KEY (key_id) REFERENCES cloro_api_keys (id)", sql, StringComparison.Ordinal);
        foreach (var column in new[] { "cloro_key_id", "credit_resubmit_count", "pool_exhausted_since" })
            Assert.Contains($"column_name = '{column}'", sql, StringComparison.Ordinal);
        Assert.Contains("index_name = 'ix_provider_tasks_cloro_key_state'", sql, StringComparison.Ordinal);
        // The singleton value is only relaxed to NULL, never cleared, so an older release can still read it.
        Assert.Contains("MODIFY COLUMN api_key TEXT NULL", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("SET api_key", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("INSERT IGNORE INTO cloro_api_keys", sql, StringComparison.Ordinal);
        Assert.Contains("'default'", sql, StringComparison.Ordinal);
        Assert.Contains("t.cloro_key_id IS NULL AND t.external_task_id IS NOT NULL", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("'succeeded'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP TABLE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE FROM", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ENUM(", sql, StringComparison.OrdinalIgnoreCase);
    }

    // Requires the isolated stage-1 test database with migration 022 already applied.
    [Stage1MySqlFact]
    public async Task MigratedMySql_PicksByPriorityThenResetThenLastUseAndWritesBackStates()
    {
        var connectionString = Environment.GetEnvironmentVariable("JOGEOAI_STAGE1_TEST_CONNECTION")!;
        Assert.Equal("jogeoai_stage1_20260926", new MySqlConnectionStringBuilder(connectionString).Database);
        var store = new CloroKeyPoolStore(new MySqlDatabase(connectionString), Options.Create(new CloroKeyPoolOptions()));
        ICloroKeyAdminStore admin = store;
        // Ignore keys created by other runs in the shared test database.
        var existing = (await admin.ListAsync(true)).Select(view => view.Id).ToList();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        async Task<CloroKeyView> Create(string name, int priority, bool enabled = true) =>
            await admin.CreateAsync(new CreateCloroKeyCommand($"sql-{name}-{suffix}", $"sql-key-{name}-{Guid.NewGuid():N}", priority, enabled));

        var later = await Create("later", 10);
        var sooner = await Create("sooner", 10);
        var disabled = await Create("disabled", 5, enabled: false);
        var cooling = await Create("cooling", 1);
        var now = DateTimeOffset.UtcNow;
        await store.ApplyCreditsAsync(later.Id, new CloroCreditsSnapshot(100, 500, now.AddDays(5), now));
        await store.ApplyCreditsAsync(sooner.Id, new CloroCreditsSnapshot(100, 500, now.AddDays(2), now));
        await store.MarkRejectedAsync(cooling.Id, null, CloroKeyRejection.ConcurrencyLimited, "CONCURRENCY_LIMIT_EXCEEDED");

        Assert.Equal(sooner.Id, (await store.PickAsync(existing))?.KeyId);
        Assert.Equal(CloroKeyStatus.Cooling, (await admin.GetAsync(cooling.Id))!.Status);
        Assert.False((await admin.GetAsync(disabled.Id))!.Available);

        await store.MarkRejectedAsync(sooner.Id, Guid.NewGuid(), CloroKeyRejection.InsufficientCredits, "INSUFFICIENT_CREDITS");
        var exhausted = (await admin.GetAsync(sooner.Id))!;
        Assert.Equal(CloroKeyStatus.Exhausted, exhausted.Status);
        Assert.False(exhausted.Available);
        Assert.Null(exhausted.CreditsCheckedAt);
        Assert.Equal("INSUFFICIENT_CREDITS", exhausted.LastErrorCode);
        Assert.Equal(later.Id, (await store.PickAsync(existing))?.KeyId);

        // Positive balance that has not grown since exhaustion stays exhausted; a top-up recovers it.
        await store.ApplyCreditsAsync(sooner.Id, new CloroCreditsSnapshot(100, 500, now.AddDays(2), now));
        Assert.Equal(CloroKeyStatus.Exhausted, (await admin.GetAsync(sooner.Id))!.Status);
        await store.ApplyCreditsAsync(sooner.Id, new CloroCreditsSnapshot(150, 500, now.AddDays(2), now));
        Assert.Equal(CloroKeyStatus.Active, (await admin.GetAsync(sooner.Id))!.Status);

        await store.MarkUsedAsync(sooner.Id, Guid.NewGuid(), $"sql-external-{suffix}");
        Assert.NotNull((await admin.GetAsync(sooner.Id))!.LastUsedAt);
        Assert.Equal(later.Id, (await store.PickAsync([.. existing, sooner.Id]))?.KeyId);

        var duplicate = await Assert.ThrowsAsync<CollectorStoreException>(() =>
            admin.CreateAsync(new CreateCloroKeyCommand(later.AccountLabel, $"sql-key-other-{Guid.NewGuid():N}", 1, true)));
        Assert.Equal("CLORO_KEY_DUPLICATE", duplicate.Code);

        Assert.Equal(new CloroKeyArchiveResult(true, 0), await admin.ArchiveAsync(later.Id));
        Assert.Equal(new CloroKeyArchiveResult(true, 0), await admin.ArchiveAsync(later.Id));
        Assert.DoesNotContain(await admin.ListAsync(false), view => view.Id == later.Id);
        Assert.NotNull((await store.GetAsync(later.Id))?.ApiKey);
    }
}
