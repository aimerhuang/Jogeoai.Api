using System.Data;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using static Jogeoai.Tests.TeamMonitoringConfigSqlTests;

namespace Jogeoai.Tests;

/// <summary>截止处理与 App Run 的状态汇总（implement 4.4a / 4.5）。</summary>
public sealed partial class AppCycleRunSqlTests
{
    private static Task TaskOfAsync(string cs, Guid run, Guid prompt, string set)
        => ExecAsync(cs, $"""
            UPDATE collector_tasks t JOIN visibility_checks c ON c.id = t.check_id SET {set}
            WHERE t.run_id = UUID_TO_BIN(@run) AND c.prompt_id = UUID_TO_BIN(@prompt);
            """, ("@run", run.ToString()), ("@prompt", prompt.ToString()));

    // At the deadline every check that did not succeed becomes "not collected": the task terminal, the check cancelled
    // with the reason code, an analysis arrival row, and the run recomputed. A succeeded check and a task whose lease has
    // not expired are left alone. Nothing is charged or reserved.
    [AppChannelMySqlFact]
    public Task TheDeadlinePassCancelsOverdueChecksWithTheirReasonAndLeavesSucceededAndLeasedOnes() => AppSiteTargetsSqlTests.IsolatedAsync(async (cs, seedTenant) =>
    {
        var rig = Build(cs, await seedTenant());
        var site = await SeedAppBrandAsync(rig, "截止", "doubao");
        var prompts = new Guid[7];
        for (var i = 0; i < prompts.Length; i++) prompts[i] = await SeedPromptAsync(cs, site, $"第 {i + 1} 条提问：哪个记账工具好用？", i + 1);
        var plan = await StartAsync(rig, site);
        // The slot is Monday 2000-01-03, so its deadline (2000-01-10 01:11 UTC) has long passed.
        var run = (await RunSlotAsync(rig, plan, 3)).RunId!.Value;
        Assert.Equal(7, (await PairsAsync(cs, run)).Length);

        // What phase 5 and 6 will leave on a task: the last reason a lease was refused, failures, a result, a live lease.
        await TaskOfAsync(cs, run, prompts[1], "t.last_block_reason = 'credit_insufficient'");
        await TaskOfAsync(cs, run, prompts[2], "t.last_block_reason = 'binding_stopped', t.status = 'retryable_failed', t.last_error_code = 'login_required'");
        await TaskOfAsync(cs, run, prompts[3], "t.last_block_reason = 'platform_paused'");
        await TaskOfAsync(cs, run, prompts[4], "t.status = 'terminal_failed', t.failure_count = 3, t.attempt_count = 3, t.last_error_code = 'collection_timeout'");
        await TaskOfAsync(cs, run, prompts[5], """
            t.status = 'succeeded', t.completed_at = UTC_TIMESTAMP(6), c.status = 'succeeded', c.answer = '合成回答',
            c.answer_present = TRUE, c.captured_at = UTC_TIMESTAMP(6)
            """);
        await TaskOfAsync(cs, run, prompts[6], """
            t.status = 'leased', t.lease_owner = 'app-node', t.lease_version = 1, t.attempt_count = 1,
            t.lease_until = UTC_TIMESTAMP(6) + INTERVAL 10 MINUTE
            """);

        Assert.Equal(5, await rig.App.ExpireOverdueTasksAsync());
        Assert.Equal(
        [
            Pair(prompts[0], "doubao", 1, "cancelled", "APP_NOT_COLLECTED_CAPACITY", "terminal_failed"),
            Pair(prompts[1], "doubao", 2, "cancelled", "APP_NOT_COLLECTED_CREDIT", "terminal_failed"),
            Pair(prompts[2], "doubao", 3, "cancelled", "APP_NOT_COLLECTED_BINDING", "terminal_failed"),
            Pair(prompts[3], "doubao", 4, "cancelled", "APP_NOT_COLLECTED_PLATFORM", "terminal_failed"),
            Pair(prompts[4], "doubao", 5, "cancelled", "APP_NOT_COLLECTED_FAILED", "terminal_failed"),
            Pair(prompts[5], "doubao", 6, "succeeded", "-", "succeeded"),
            Pair(prompts[6], "doubao", 7, "pending", "-", "leased"),
        ], await PairsAsync(cs, run));
        // Each cancelled check carries the message of its code; the answer of the succeeded one is untouched.
        var messages = await RowsAsync(cs, "SELECT error_code, error_message FROM visibility_checks WHERE run_id = UUID_TO_BIN(@run) AND status = 'cancelled';",
            ("@run", run.ToString()));
        Assert.All(messages, row => Assert.Equal(AppNotCollectedRules.Message(row[0]!), row[1]));
        Assert.Equal(5, messages.Select(row => row[1]).Distinct().Count());
        Assert.Equal("合成回答|True|∅|∅", await RowAsync(cs, """
            SELECT answer, answer_present, error_code, error_message FROM visibility_checks
            WHERE run_id = UUID_TO_BIN(@run) AND prompt_id = UUID_TO_BIN(@prompt);
            """, ("@run", run.ToString()), ("@prompt", prompts[5].ToString())));
        // The swept tasks are terminal and released; the evidence for the reason stays on them.
        Assert.Equal(5, await CountAsync(cs, """
            SELECT COUNT(*) FROM collector_tasks WHERE run_id = UUID_TO_BIN(@run) AND status = 'terminal_failed'
              AND completed_at IS NOT NULL AND lease_owner IS NULL AND lease_until IS NULL;
            """, ("@run", run.ToString())));
        Assert.Equal("binding_stopped|login_required", await RowAsync(cs, """
            SELECT t.last_block_reason, t.last_error_code FROM collector_tasks t JOIN visibility_checks c ON c.id = t.check_id
            WHERE t.run_id = UUID_TO_BIN(@run) AND c.prompt_id = UUID_TO_BIN(@prompt);
            """, ("@run", run.ToString()), ("@prompt", prompts[2].ToString())));
        // Same transaction as the cancellation: one analysis arrival per cancelled check, none for the open one.
        Assert.Equal("5|5", await RowAsync(cs, """
            SELECT COUNT(*), COALESCE(SUM(c.status = 'cancelled'), 0) FROM analytics_check_arrivals a JOIN visibility_checks c ON c.id = a.check_id
            WHERE a.tenant_id = UUID_TO_BIN(@tenant) AND a.site_id = UUID_TO_BIN(@site);
            """, ("@tenant", rig.Tenant.ToString()), ("@site", site.ToString())));
        // One check is still open: the run is running; failed_checks counts failed checks only, never the cancelled ones.
        Assert.Equal("running|7|1|0|1|0", await RunAsync(cs, run));
        Assert.Equal(Footprint(1, 7), await FootprintAsync(cs, rig.Tenant));

        // A second pass has nothing to do: an overdue task is handled once, the leased one is still protected.
        Assert.Equal(0, await rig.App.ExpireOverdueTasksAsync());
        Assert.Equal("running|7|1|0|1|0", await RunAsync(cs, run));

        // Its lease expires: the next pass takes it. Everything is finished: one succeeded, six not collected.
        await TaskOfAsync(cs, run, prompts[6], "t.lease_until = UTC_TIMESTAMP(6) - INTERVAL 1 SECOND");
        Assert.Equal(1, await rig.App.ExpireOverdueTasksAsync());
        Assert.Equal(Pair(prompts[6], "doubao", 7, "cancelled", "APP_NOT_COLLECTED_CAPACITY", "terminal_failed"), (await PairsAsync(cs, run))[6]);
        Assert.Equal("partial_failed|7|1|0|1|1", await RunAsync(cs, run));
        Assert.Equal(6, await CountAsync(cs, "SELECT COUNT(*) FROM analytics_check_arrivals WHERE tenant_id = UUID_TO_BIN(@tenant);", ("@tenant", rig.Tenant.ToString())));

        // A cycle nobody collected, larger than one batch: every check is cancelled in the same pass and the run is cancelled.
        for (var second = 8; second <= 59; second++) await SeedPromptAsync(cs, site, $"第 {second} 条提问：预算管理应用怎么选？", second);
        var idle = (await RunSlotAsync(rig, plan, 10)).RunId!.Value;
        Assert.True(59 > AppCollectionStore.DeadlineBatchSize);
        Assert.Equal(59, await rig.App.ExpireOverdueTasksAsync());
        Assert.Equal("cancelled|59|0|0|1|1", await RunAsync(cs, idle));
        Assert.Equal("59|1", await RowAsync(cs, """
            SELECT COUNT(*), COUNT(DISTINCT error_code) FROM visibility_checks
            WHERE run_id = UUID_TO_BIN(@run) AND status = 'cancelled' AND error_code = 'APP_NOT_COLLECTED_CAPACITY';
            """, ("@run", idle.ToString())));
        Assert.Equal("partial_failed|7|1|0|1|1", await RunAsync(cs, run));
        Assert.Equal(0, await rig.App.ExpireOverdueTasksAsync());
        // Still no provider task, reservation, credit item or ledger entry; the balance is untouched.
        Assert.Equal(Footprint(2, 66), await FootprintAsync(cs, rig.Tenant));
    });

    // design 4.3: the App run status follows its checks; failed_checks counts failed only; completed_at is set when
    // nothing is pending or running and cleared again if a check reopens.
    [AppChannelMySqlFact]
    public Task TheAppRunStatusIsAggregatedFromItsChecks() => AppSiteTargetsSqlTests.IsolatedAsync(async (cs, seedTenant) =>
    {
        var rig = Build(cs, await seedTenant());
        var site = await SeedAppBrandAsync(rig, "状态汇总", "doubao");
        var prompts = new[]
        {
            await SeedPromptAsync(cs, site, "哪个记账工具好用？", 1), await SeedPromptAsync(cs, site, "预算管理应用怎么选？", 2),
            await SeedPromptAsync(cs, site, "家庭记账有什么建议？", 3),
        };
        var run = (await RunSlotAsync(rig, await StartAsync(rig, site), 3)).RunId!.Value;

        async Task<string> AggregateAsync(params string[] statuses)
        {
            for (var i = 0; i < statuses.Length; i++)
                await ExecAsync(cs, "UPDATE visibility_checks SET status = @status WHERE run_id = UUID_TO_BIN(@run) AND prompt_id = UUID_TO_BIN(@prompt);",
                    ("@status", statuses[i]), ("@run", run.ToString()), ("@prompt", prompts[i].ToString()));
            await using var connection = rig.Database.CreateConnection();
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            await AppCollectionStore.UpdateRunStatusAsync(connection, transaction, run, default);
            await transaction.CommitAsync();
            return await RunAsync(cs, run);
        }

        // status | total | succeeded | failed | started | completed
        Assert.Equal("queued|3|0|0|0|0", await RunAsync(cs, run));
        Assert.Equal("running|3|0|0|0|0", await AggregateAsync("pending", "pending", "pending"));
        Assert.Equal("running|3|0|0|1|0", await AggregateAsync("running", "pending", "pending"));
        Assert.Equal("running|3|2|0|1|0", await AggregateAsync("succeeded", "succeeded", "pending"));
        Assert.Equal("succeeded|3|3|0|1|1", await AggregateAsync("succeeded", "succeeded", "succeeded"));
        Assert.Equal("failed|3|0|3|1|1", await AggregateAsync("failed", "failed", "failed"));
        Assert.Equal("cancelled|3|0|0|1|1", await AggregateAsync("cancelled", "cancelled", "cancelled"));
        Assert.Equal("partial_failed|3|1|1|1|1", await AggregateAsync("succeeded", "failed", "cancelled"));
        Assert.Equal("partial_failed|3|1|0|1|1", await AggregateAsync("succeeded", "cancelled", "cancelled"));
        Assert.Equal("partial_failed|3|0|1|1|1", await AggregateAsync("failed", "cancelled", "cancelled"));
        Assert.Equal("running|3|1|0|1|0", await AggregateAsync("succeeded", "pending", "cancelled"));
        // A run that does not exist is ignored.
        await using var other = rig.Database.CreateConnection();
        await other.OpenAsync();
        await using var tx = await other.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await AppCollectionStore.UpdateRunStatusAsync(other, tx, Guid.NewGuid(), default);
        await tx.CommitAsync();
        await ParkSlotsAsync(cs, site);
    });
}
