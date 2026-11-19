using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using static Jogeoai.Tests.AppCycleRunSqlTests;
using static Jogeoai.Tests.AppLeaseSqlTests;

namespace Jogeoai.Tests;

/// <summary>余额不足（implement 6.2，总 PRD KD-23、AC-C14c）：不领取、不预留、记下原因；别的租户照常领取；补足后恢复；并发下只预留一次。</summary>
public sealed partial class AppCreditSqlTests
{
    [AppChannelMySqlFact]
    public Task ATenantWithoutTenCreditsIsPassedOverAndLeasedAgainAfterATopUp() => PoolAsync(async (pool, seedTenant) =>
    {
        var (poor, rich) = (Build(pool.Cs, await seedTenant()), Build(pool.Cs, await seedTenant()));
        var store = pool.Store(poor.Tenant, rich.Tenant);
        var (node, _) = await pool.NodeAsync("mac");
        var binding = await pool.BindAsync(node, "AI_Apps_4KB", "doubao");
        Task<AppLeaseResult> LeaseAsync(AppCollectorPoolStore? only = null) => (only ?? store).LeaseAsync(new AppLeaseCommand(node, binding, 60));
        async Task NextAsync(Guid finished)
        {
            await pool.FinishAsync(finished);
            await pool.AgeBindingAsync(binding);
            await pool.AgePlatformAsync("doubao");
        }
        // The poor tenant's tasks are first in the queue.
        var theirs = await CycleAsync(poor, "余额不足", 2, "doubao");
        await pool.ExecAsync("UPDATE collector_tasks SET created_at = '2026-01-01 00:00:00' WHERE run_id = UUID_TO_BIN(@run);", ("@run", theirs.Run.ToString()));
        var (first, second) = (await TaskOfAsync(pool.Cs, theirs.Run, theirs.Prompts[0], "doubao"), await TaskOfAsync(pool.Cs, theirs.Run, theirs.Prompts[1], "doubao"));
        const string blocked = "pending|0|-|0|0|-|credit_insufficient";

        // No account row counts as no credits; 9.999 is not enough either. Nothing is leased, reserved or dispatched.
        await pool.ExecAsync("DELETE FROM credit_accounts WHERE tenant_id = UUID_TO_BIN(@tenant);", ("@tenant", poor.Tenant.ToString()));
        AssertEmpty(await LeaseAsync(pool.Store(poor.Tenant)), AppLeaseEmptyReason.CreditInsufficient, 60, 60);
        Assert.Equal((blocked, blocked), (await TaskAsync(pool.Cs, first), await TaskAsync(pool.Cs, second)));
        await pool.ExecAsync("INSERT INTO credit_accounts (tenant_id, available_credits, updated_at) VALUES (UUID_TO_BIN(@tenant), 9.999, UTC_TIMESTAMP(6));",
            ("@tenant", poor.Tenant.ToString()));
        AssertEmpty(await LeaseAsync(), AppLeaseEmptyReason.CreditInsufficient, 60, 60);
        Assert.Equal("9.999|0.000|0.000|0|0|0", await AccountAsync(pool, poor.Tenant) + "|" + await CreditRowsAsync(pool, poor.Tenant));
        Assert.Equal("1|1|0", await pool.RowAsync("""
            SELECT (SELECT last_leased_at IS NULL FROM collector_app_bindings WHERE id = UUID_TO_BIN(@id)),
                   (SELECT last_dispatched_at IS NULL FROM collector_app_platform_states WHERE platform_code = 'doubao'),
                   (SELECT COUNT(*) FROM collector_attempts WHERE tenant_id = UUID_TO_BIN(@tenant));
            """, ("@id", binding.ToString()), ("@tenant", poor.Tenant.ToString())));

        // A tenant with credits is leased in the same call although the poor tenant's tasks are ahead of it.
        var mine = await CycleAsync(rich, "有余额", 2, "doubao");
        var leased = (await LeaseAsync()).Task!;
        Assert.Equal((rich.Tenant, mine.Run, mine.Prompts[0]), (leased.TenantId, leased.RunId, leased.PromptId));
        Assert.Equal("990.000|10.000|0.000|1|1|1", await AccountAsync(pool, rich.Tenant) + "|" + await CreditRowsAsync(pool, rich.Tenant));
        Assert.Equal("9.999|0.000|0.000|0|0|0", await AccountAsync(pool, poor.Tenant) + "|" + await CreditRowsAsync(pool, poor.Tenant));

        // The reason survives a later lease that reaches the pick step (which clears the binding and platform reasons) and
        // does not look at this tenant at all.
        await NextAsync(leased.Id);
        leased = (await LeaseAsync(pool.Store(rich.Tenant))).Task!;
        Assert.Equal(mine.Prompts[1], leased.PromptId);
        Assert.Equal((blocked, blocked), (await TaskAsync(pool.Cs, first), await TaskAsync(pool.Cs, second)));

        // Nothing else is leasable: the collector is told why.
        await NextAsync(leased.Id);
        AssertEmpty(await LeaseAsync(), AppLeaseEmptyReason.CreditInsufficient, 60, 60);

        // At the deadline the check is "not collected (credit)"; nothing was reserved, so nothing is released.
        await pool.ExecAsync("UPDATE collector_tasks SET deadline_at = UTC_TIMESTAMP(6) - INTERVAL 1 SECOND WHERE id = UUID_TO_BIN(@id);", ("@id", second.ToString()));
        Assert.Equal(1, await poor.App.ExpireOverdueTasksAsync());
        Assert.Equal($"cancelled|{AppNotCollectedCodes.Credit}|terminal_failed", await pool.RowAsync("""
            SELECT c.status, c.error_code, t.status FROM collector_tasks t JOIN visibility_checks c ON c.id = t.check_id WHERE t.id = UUID_TO_BIN(@id);
            """, ("@id", second.ToString())));
        Assert.Equal("9.999|0.000|0.000|0|0|0", await AccountAsync(pool, poor.Tenant) + "|" + await CreditRowsAsync(pool, poor.Tenant));

        // Exactly 10 credits are enough: the task is leased in the same cycle and its reason is cleared.
        await SetBalanceAsync(pool, poor.Tenant, 10m);
        var theirTask = (await LeaseAsync()).Task!;
        Assert.Equal((poor.Tenant, first), (theirTask.TenantId, theirTask.Id));
        Assert.Equal($"leased|1|{node}|1|0|-|-", await TaskAsync(pool.Cs, first));
        Assert.Equal("0.000|10.000|0.000|1|1|1", await AccountAsync(pool, poor.Tenant) + "|" + await CreditRowsAsync(pool, poor.Tenant));
        // The estimate is the run's total (2 checks), also for the check that was never collected.
        Assert.Equal("20.000|10.000|0.000|0.000|held|10.000|0|0", await ReservationAsync(pool, theirs.Run));
    });

    // Tasks that only wait for credits are passed over by the pick itself: however many of them are ahead in the queue,
    // they do not use up the candidates one lease call looks at under lock.
    [AppChannelMySqlFact]
    public Task TasksWaitingForCreditsDoNotUseUpTheCandidatesOfALeaseCall() => PoolAsync(async (pool, seedTenant) =>
    {
        var (poor, rich) = (Build(pool.Cs, await seedTenant()), Build(pool.Cs, await seedTenant()));
        var store = pool.Store(poor.Tenant, rich.Tenant);
        var (node, _) = await pool.NodeAsync("mac");
        var binding = await pool.BindAsync(node, "AI_Apps_4KB", "doubao");
        var ahead = AppPoolRules.MaxLeaseCandidates + 4;
        Assert.Equal(8, AppPoolRules.MaxLeaseCandidates);
        var theirs = await CycleAsync(poor, "挡在前面", ahead, "doubao");
        // Every one of them is ahead of the other tenant's task: the same rank, created earlier.
        await pool.ExecAsync("UPDATE collector_tasks SET fair_rank = 1, created_at = '2026-01-01 00:00:00' WHERE run_id = UUID_TO_BIN(@run);",
            ("@run", theirs.Run.ToString()));
        await SetBalanceAsync(pool, poor.Tenant, 0m);
        var mine = await CycleAsync(rich, "排在后面", 1, "doubao");

        var leased = (await store.LeaseAsync(new AppLeaseCommand(node, binding, 60))).Task!;
        Assert.Equal((rich.Tenant, mine.Run), (leased.TenantId, leased.RunId));
        Assert.Equal($"{ahead}|{ahead}|0", await pool.RowAsync("""
            SELECT COUNT(*), COALESCE(SUM(last_block_reason = 'credit_insufficient' AND status = 'pending'), 0), COALESCE(SUM(attempt_count), 0)
            FROM collector_tasks WHERE run_id = UUID_TO_BIN(@run);
            """, ("@run", theirs.Run.ToString())));
        Assert.Equal("0.000|0.000|0.000|0|0|0", await AccountAsync(pool, poor.Tenant) + "|" + await CreditRowsAsync(pool, poor.Tenant));
    });

    // Really parallel: both leases are started while the test holds the tenant's account row, and are let go only after
    // both are waiting for it. The tenant can pay for exactly one check.
    [AppChannelMySqlFact]
    public Task TwoConcurrentLeasesWithCreditsForOneCheckReserveExactlyOnce() => PoolAsync(async (pool, seedTenant) =>
    {
        var rig = Build(pool.Cs, await seedTenant());
        var store = pool.Store(rig.Tenant);
        var (nodeA, _) = await pool.NodeAsync("mac");
        var (nodeB, _) = await pool.NodeAsync("mac");
        await CycleAsync(rig, "并发余额", 3, "doubao", "kimi");
        // Different devices and different platforms: nothing but the account row makes the two leases wait for each other.
        var (onDoubao, onKimi) = (await pool.BindAsync(nodeA, "AVD_1", "doubao"), await pool.BindAsync(nodeB, "AVD_B", "kimi"));
        for (var round = 1; round <= 3; round++)
        {
            await SetBalanceAsync(pool, rig.Tenant, 10m);
            var results = await RaceAsync(pool, $"SELECT 1 FROM credit_accounts WHERE tenant_id = UUID_TO_BIN('{rig.Tenant}') FOR UPDATE;",
                "%FROM credit_accounts WHERE tenant_id = %FOR UPDATE%",
                () => store.LeaseAsync(new AppLeaseCommand(nodeA, onDoubao, 60)), () => store.LeaseAsync(new AppLeaseCommand(nodeB, onKimi, 60)));
            var winner = Assert.Single(results, result => result.Task is not null).Task!;
            AssertEmpty(Assert.Single(results, result => result.Task is null), AppLeaseEmptyReason.CreditInsufficient, 60, 60);
            // One reservation of 10; no figure went below zero and the books balance.
            Assert.Equal($"0.000|{10 * round}.000|0.000", await AccountAsync(pool, rig.Tenant));
            Assert.Equal($"1|{round}|{round}", await CreditRowsAsync(pool, rig.Tenant));
            Assert.Equal("held|10.000|0|0", await ItemAsync(pool, winner.CheckId));
            Assert.Equal($"{round}|{round}|0.000", await pool.RowAsync("""
                SELECT COUNT(*), COALESCE(SUM(amount = -10 AND entry_type = 'reserve'), 0), MIN(balance_after)
                FROM credit_ledger WHERE tenant_id = UUID_TO_BIN(@tenant);
                """, ("@tenant", rig.Tenant.ToString())));
            // The loser's task is marked and stays unleased; exactly one task of the tenant holds a lease.
            var loser = winner.PlatformCode == "doubao" ? "kimi" : "doubao";
            Assert.Equal("1|0", await pool.RowAsync("""
                SELECT COALESCE(SUM(last_block_reason = 'credit_insufficient'), 0) >= 1, COALESCE(SUM(status IN ('leased', 'collecting')), 0)
                FROM collector_tasks WHERE tenant_id = UUID_TO_BIN(@tenant) AND platform_code = @platform;
                """, ("@tenant", rig.Tenant.ToString()), ("@platform", loser)));
            Assert.Equal(1, await pool.CountAsync("SELECT COUNT(*) FROM collector_tasks WHERE tenant_id = UUID_TO_BIN(@tenant) AND status = 'leased';",
                ("@tenant", rig.Tenant.ToString())));
            await pool.FinishAsync(winner.Id);
            foreach (var binding in new[] { onDoubao, onKimi }) await pool.AgeBindingAsync(binding);
            foreach (var platform in new[] { "doubao", "kimi" }) await pool.AgePlatformAsync(platform);
        }
    });
}
