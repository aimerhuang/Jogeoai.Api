using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using MySqlConnector;
using static Jogeoai.Tests.AppCycleRunSqlTests;
using static Jogeoai.Tests.TeamMonitoringConfigSqlTests;

namespace Jogeoai.Tests;

/// <summary>排队顺序、开放范围与并发（implement 5.5，design 6.4；总 PRD AC-C9、AC-C11d）。</summary>
public sealed partial class AppLeaseSqlTests
{
    // No sends are recorded in this queue-order fixture: completing the task immediately frees the device.
    private static async Task<List<Guid>> DrainAsync(Pool pool, AppCollectorPoolStore store, string node, Guid binding)
    {
        var order = new List<Guid>();
        while (true)
        {
            var result = await store.LeaseAsync(new AppLeaseCommand(node, binding, 60));
            if (result.Task is null)
            {
                Assert.Equal(AppLeaseEmptyReason.NoTask, result.Reason);
                return order;
            }
            order.Add(result.Task.Id);
            await pool.FinishAsync(result.Task.Id);
        }
    }

    // fair_rank first: every brand's first prompt comes before any brand's second one, so a brand with many prompts only
    // waits on its own tail. Then creation time, then id: the order is a function of the data and repeats exactly.
    [AppChannelMySqlFact]
    public Task TheQueueOrderIsDeterministicAndABigBrandCannotStarveASmallOne() => PoolAsync(async (pool, seedTenant) =>
    {
        var (bigRig, smallRig) = (Build(pool.Cs, await seedTenant()), Build(pool.Cs, await seedTenant()));
        var store = pool.Store(bigRig.Tenant, smallRig.Tenant);
        var (node, _) = await pool.NodeAsync("mac");
        var binding = await pool.BindAsync(node, "AI_Apps_4KB", "doubao");
        var big = await CycleAsync(bigRig, "大品牌", 5, "doubao");
        var small = await CycleAsync(smallRig, "小品牌", 2, "doubao");
        var runs = new[] { ("@big", (object?)big.Run.ToString()), ("@small", (object?)small.Run.ToString()) };
        // The big brand's tasks were created first.
        await pool.ExecAsync("""
            UPDATE collector_tasks SET created_at = IF(run_id = UUID_TO_BIN(@big), '2026-01-01 00:00:00', '2026-01-01 00:00:01')
            WHERE run_id IN (UUID_TO_BIN(@big), UUID_TO_BIN(@small));
            """, runs);
        async Task<string[]> LabelsAsync(IEnumerable<Guid> tasks)
        {
            var labels = new List<string>();
            foreach (var task in tasks)
                labels.Add(await pool.RowAsync("SELECT IF(run_id = UUID_TO_BIN(@big), 'big', 'small'), fair_rank FROM collector_tasks WHERE id = UUID_TO_BIN(@id);",
                    ("@big", big.Run.ToString()), ("@id", task.ToString())));
            return labels.ToArray();
        }
        Task ResetAsync() => pool.ExecAsync("""
            UPDATE collector_tasks SET status = 'pending', completed_at = NULL WHERE run_id IN (UUID_TO_BIN(@big), UUID_TO_BIN(@small));
            """, runs);

        var firstPass = await DrainAsync(pool, store, node, binding);
        // The small brand is done after four leases although the big brand has five prompts.
        Assert.Equal(["big|1", "small|1", "big|2", "small|2", "big|3", "big|4", "big|5"], await LabelsAsync(firstPass));
        // The same data again: the same order, task for task.
        await ResetAsync();
        Assert.Equal(firstPass, await DrainAsync(pool, store, node, binding));

        // Equal rank and equal creation time: the id decides, and it decides the same way every time.
        await pool.ExecAsync("UPDATE collector_tasks SET created_at = '2026-01-01 00:00:00' WHERE run_id IN (UUID_TO_BIN(@big), UUID_TO_BIN(@small));", runs);
        var expected = (await RowsAsync(pool.Cs, """
            SELECT BIN_TO_UUID(id) FROM collector_tasks WHERE run_id IN (UUID_TO_BIN(@big), UUID_TO_BIN(@small)) ORDER BY fair_rank, created_at, id;
            """, runs)).Select(row => Guid.Parse(row[0]!)).ToList();
        await ResetAsync();
        var tied = await DrainAsync(pool, store, node, binding);
        Assert.Equal(expected, tied);
        Assert.Equal(["small|1", "small|2"], (await LabelsAsync(tied.Take(4))).Where(label => label.StartsWith("small", StringComparison.Ordinal)).Order());
        await ResetAsync();
        Assert.Equal(tied, await DrainAsync(pool, store, node, binding));
    });

    // Collector:AppTenantIds: a task of a tenant the channel is not open for is never leased and never marked.
    [AppChannelMySqlFact]
    public Task ATaskOfAClosedTenantIsNeverLeased() => PoolAsync(async (pool, seedTenant) =>
    {
        var (open, closed) = (Build(pool.Cs, await seedTenant()), Build(pool.Cs, await seedTenant()));
        var (node, _) = await pool.NodeAsync("mac");
        var binding = await pool.BindAsync(node, "AI_Apps_4KB", "doubao");
        // The closed tenant's cycle exists (it was created while the tenant was open) and would be first in the queue.
        var theirs = await CycleAsync(closed, "未开放", 2, "doubao");
        await pool.ExecAsync("UPDATE collector_tasks SET created_at = '2026-01-01 00:00:00' WHERE run_id = UUID_TO_BIN(@run);", ("@run", theirs.Run.ToString()));
        Task<long> TouchedAsync() => pool.CountAsync("""
            SELECT COUNT(*) FROM collector_tasks WHERE run_id = UUID_TO_BIN(@run)
              AND (status <> 'pending' OR attempt_count <> 0 OR lease_version <> 0 OR last_block_reason IS NOT NULL);
            """, ("@run", theirs.Run.ToString()));

        // No tenant open, then only the other tenant open: nothing to lease.
        foreach (var store in new[] { pool.Store(), pool.Store(open.Tenant) })
            AssertEmpty(await store.LeaseAsync(new AppLeaseCommand(node, binding, 60)), AppLeaseEmptyReason.NoTask, 60, 60);
        // A blocked platform marks the waiting tasks of open tenants only.
        var mine = await CycleAsync(open, "已开放", 1, "doubao");
        await pool.ExecAsync("UPDATE collector_app_platform_states SET paused_at = UTC_TIMESTAMP(6), pause_reason = 'operator' WHERE platform_code = 'doubao';");
        AssertEmpty(await pool.Store(open.Tenant).LeaseAsync(new AppLeaseCommand(node, binding, 60)), AppLeaseEmptyReason.PlatformPaused, 600, 600);
        Assert.Equal("platform_paused", await pool.RowAsync("SELECT last_block_reason FROM collector_tasks WHERE run_id = UUID_TO_BIN(@run);", ("@run", mine.Run.ToString())));
        Assert.Equal(0, await TouchedAsync());
        await pool.ExecAsync("UPDATE collector_app_platform_states SET paused_at = NULL, pause_reason = NULL WHERE platform_code = 'doubao';");

        // The open tenant's task is leased although the closed tenant's tasks are older.
        var leased = (await pool.Store(open.Tenant).LeaseAsync(new AppLeaseCommand(node, binding, 60))).Task!;
        Assert.Equal((open.Tenant, mine.Run), (leased.TenantId, leased.RunId));
        await pool.FinishAsync(leased.Id);
        AssertEmpty(await pool.Store(open.Tenant).LeaseAsync(new AppLeaseCommand(node, binding, 60)), AppLeaseEmptyReason.NoTask, 60, 60);
        Assert.Equal(0, await TouchedAsync());

        // Once the tenant is opened its tasks are leased like any other.
        Assert.Equal(theirs.Run, (await pool.Store(open.Tenant, closed.Tenant).LeaseAsync(new AppLeaseCommand(node, binding, 60))).Task!.RunId);
    });

    // Waits until `count` statements matching the pattern are running on other connections of this account, i.e. until that
    // many lease requests are blocked on the row lock the test holds. Polling the process list is not a way to move time.
    internal static async Task BlockedAsync(Pool pool, string pattern, int count)
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            if (await pool.CountAsync("SELECT COUNT(*) FROM information_schema.processlist WHERE id <> CONNECTION_ID() AND info LIKE @pattern;",
                    ("@pattern", pattern)) >= count) return;
            await Task.Delay(20);
        }
        Assert.Fail($"Expected {count} lease requests to be waiting on the held row lock.");
    }

    // Holds a row lock, starts both leases, waits until both are blocked behind it, then lets them go.
    internal static async Task<AppLeaseResult[]> RaceAsync(Pool pool, string lockSql, string waitingPattern, params Func<Task<AppLeaseResult>>[] leases)
    {
        Task<AppLeaseResult>[] running;
        await using (var holder = new MySqlConnection(pool.Cs))
        {
            await holder.OpenAsync();
            await using var transaction = await holder.BeginTransactionAsync();
            await using (var command = new MySqlCommand(lockSql, holder, transaction)) await command.ExecuteNonQueryAsync();
            running = leases.Select(lease => Task.Run(lease)).ToArray();
            await BlockedAsync(pool, waitingPattern, leases.Length);
            Assert.All(running, task => Assert.False(task.IsCompleted));
            await transaction.CommitAsync();
        }
        return await Task.WhenAll(running);
    }

    // The AVD name is scoped to its node; the platform does not serialize dispatch onto different devices.
    [AppChannelMySqlFact]
    public Task SeparateDevicesCanLeaseOnePlatformConcurrentlyButOneDeviceCannotHoldTwoLeases() => PoolAsync(async (pool, seedTenant) =>
    {
        var rig = Build(pool.Cs, await seedTenant());
        var store = pool.Store(rig.Tenant);
        var (nodeA, _) = await pool.NodeAsync("mac");
        var (nodeB, _) = await pool.NodeAsync("mac");
        await CycleAsync(rig, "并发", 9, "doubao", "kimi");
        var (aDoubao, aKimi) = (await pool.BindAsync(nodeA, "AI_Apps_4KB", "doubao"), await pool.BindAsync(nodeA, "AI_Apps_4KB", "kimi"));
        var bDoubao = await pool.BindAsync(nodeB, "AI_Apps_4KB", "doubao");
        var leasedTasks = new List<Guid>();

        async Task NextRoundAsync(AppLeaseResult[] results)
        {
            foreach (var task in results.Where(result => result.Task is not null).Select(result => result.Task!.Id))
            {
                leasedTasks.Add(task);
                await pool.FinishAsync(task);
            }
        }

        // Two nodes, one platform: both obtain distinct leases, even with identical AVD names.
        for (var round = 0; round < 3; round++)
        {
            var results = await RaceAsync(pool, "SELECT 1 FROM collector_app_platform_states WHERE platform_code = 'doubao' FOR UPDATE;",
                "%FROM collector_app_platform_states WHERE platform_code = %FOR UPDATE%",
                () => store.LeaseAsync(new AppLeaseCommand(nodeA, aDoubao, 60)), () => store.LeaseAsync(new AppLeaseCommand(nodeB, bDoubao, 60)));
            Assert.All(results, result => Assert.NotNull(result.Task));
            Assert.NotEqual(results[0].Task!.Id, results[1].Task!.Id);
            Assert.Equal(2, await pool.CountAsync("SELECT COUNT(*) FROM collector_tasks WHERE tenant_id = UUID_TO_BIN(@tenant) AND status = 'leased';",
                ("@tenant", rig.Tenant.ToString())));
            await NextRoundAsync(results);
        }

        // One device, two platforms: both wait on the device's binding rows; one leases, the other finds the device busy.
        for (var round = 0; round < 3; round++)
        {
            var results = await RaceAsync(pool,
                $"SELECT 1 FROM collector_app_bindings WHERE node_id = '{nodeA}' AND device_id = 'AI_Apps_4KB' FOR UPDATE;",
                "%FROM collector_app_bindings%WHERE node_id = %FOR UPDATE%",
                () => store.LeaseAsync(new AppLeaseCommand(nodeA, aDoubao, 60)), () => store.LeaseAsync(new AppLeaseCommand(nodeA, aKimi, 60)));
            Assert.Single(results, result => result.Task is not null);
            AssertEmpty(Assert.Single(results, result => result.Task is null), AppLeaseEmptyReason.DeviceBusy, 30, 60);
            Assert.Equal(1, await pool.CountAsync("""
                SELECT COUNT(*) FROM collector_tasks t JOIN collector_app_bindings b ON b.id = t.lease_app_binding_id
                WHERE b.node_id = @node AND b.device_id = 'AI_Apps_4KB' AND t.status IN ('leased', 'collecting') AND t.lease_until > UTC_TIMESTAMP(6);
                """, ("@node", nodeA)));
            await NextRoundAsync(results);
        }
        // Six parallel allocations to two devices, then three mutually exclusive allocations to one device.
        Assert.Equal(9, leasedTasks.Distinct().Count());
        Assert.Equal(9, await pool.CountAsync("SELECT COUNT(*) FROM collector_attempts WHERE tenant_id = UUID_TO_BIN(@tenant) AND event_kind = 'leased';",
            ("@tenant", rig.Tenant.ToString())));
    });
}
