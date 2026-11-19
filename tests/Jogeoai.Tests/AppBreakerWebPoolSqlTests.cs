using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using MySqlConnector;

namespace Jogeoai.Tests;

/// <summary>
/// 总 PRD AC-C10 后半句的网页一侧：同一平台（doubao）App 的验证熔断把 App 平台暂停后，网页账号池对同一平台的领取照常派发。
/// 用网页账号池的隔离库 JOGEOAI_COLLECTOR_POOL_TEST_CONNECTION（jogeoai_collector_pool_test_app049，已执行 049），
/// 复用 <see cref="CollectorSessionPoolSqlTests.PoolFixture"/>；未设置时跳过。App 一侧的节点、绑定、审计事件在用例结束时删除，
/// doubao 的 App 平台状态行恢复成用例开始前的内容。网页夹具建的租户与 CollectorSessionPoolSqlTests 的其他用例一样留在该库里。
/// </summary>
[Collection(AppChannelSqlCollection.Name)]
public sealed class AppBreakerWebPoolSqlTests
{
    [CollectorPoolMySqlFact]
    public async Task AnAppVerificationBreakerOnDoubaoLeavesWebPoolLeasesOfDoubaoDispatching()
    {
        var f = await CollectorSessionPoolSqlTests.PoolFixture.CreateAsync(3);
        var cs = Environment.GetEnvironmentVariable("JOGEOAI_COLLECTOR_POOL_TEST_CONNECTION")!;
        Assert.Contains("_test", new MySqlConnectionStringBuilder(cs).Database, StringComparison.OrdinalIgnoreCase);
        var database = new MySqlDatabase(cs);
        await using (var connection = database.CreateConnection())
        {
            await connection.OpenAsync();
            Assert.True(await AppChannelSchema.IsMigratedAsync(connection, null, default), "网页账号池的隔离库没有执行 049。");
        }
        await f.PlatformAsync(minInterval: 0);
        var a = await f.RegisterAsync("doubao-web-a");
        var b = await f.RegisterAsync("doubao-web-b");

        var app = new AppCollectorPoolStore(database);
        var nodes = new List<string>();
        var state = await f.RowAsync("""
            SELECT COALESCE(DATE_FORMAT(paused_at, '%Y-%m-%d %H:%i:%s.%f'), ''), COALESCE(pause_reason, ''),
                   COALESCE(DATE_FORMAT(last_dispatched_at, '%Y-%m-%d %H:%i:%s.%f'), '')
            FROM collector_app_platform_states WHERE platform_code = 'doubao';
            """);
        var eventsBefore = (await f.RowsAsync("SELECT BIN_TO_UUID(id) FROM collector_app_events;")).Select(row => row[0]).ToHashSet();
        try
        {
            await f.ExecAsync("UPDATE collector_app_platform_states SET paused_at = NULL, pause_reason = NULL WHERE platform_code = 'doubao';");
            // The breaker for real: two App bindings of doubao on two nodes report a challenge within the window.
            async Task<Guid> BindAsync(string device)
            {
                var node = $"breaker-{Guid.NewGuid():N}";
                nodes.Add(node);
                await app.RegisterNodeAsync(node, null);
                var result = await app.ReportBindingsAsync(new AppBindingReportCommand(node, "test-rev",
                    [new AppBindingReport(device, "doubao", $"doubao-{Guid.NewGuid():N}", "pkg.doubao", "1.0", "1.0", "15", "ok")]));
                return Assert.Single(result.Bindings).BindingId;
            }
            var (first, second, third) = (await BindAsync("AVD_1"), await BindAsync("AVD_2"), await BindAsync("AVD_3"));
            Assert.False((await app.ReportFailureAsync(new AppFailureCommand(nodes[0], first, AppFailureCode.ChallengeRequired, CollectorPhase.Probe, null))).PlatformPaused);
            Assert.True((await app.ReportFailureAsync(new AppFailureCommand(nodes[1], second, AppFailureCode.ChallengeRequired, CollectorPhase.Probe, null))).PlatformPaused);
            Assert.Equal(["1", AppPoolRules.VerificationBreakerPauseReason], await f.RowAsync(
                "SELECT paused_at IS NOT NULL, pause_reason FROM collector_app_platform_states WHERE platform_code = 'doubao';"));
            // The App side is really paused: an active App binding of doubao is refused with platform_paused.
            Assert.Equal(AppLeaseEmptyReason.PlatformPaused, (await app.LeaseAsync(new AppLeaseCommand(nodes[2], third, 60))).Reason);

            // ---- The web account pool of the same platform is not affected: eligibility and leases go on as before.
            Assert.True((await f.EligibilityAsync(a)).Eligible);
            var leased = Assert.Single(await f.LeaseAsync(a));
            Assert.Equal((a, "consumer_web", "doubao.consumer_web"), (leased.SessionProfileId!.Value, leased.Channel, leased.PlatformEntryCode));
            Assert.True((await f.EligibilityAsync(b)).Eligible);
            Assert.Equal(b, Assert.Single(await f.LeaseAsync(b)).SessionProfileId);
            Assert.Equal(2, await f.ScalarAsync<long>(
                "SELECT COUNT(*) FROM collector_attempts WHERE tenant_id = UUID_TO_BIN(@tenant) AND event_kind = 'leased' AND app_binding_id IS NULL;"));
            // The web platform gate of the tenant has no pause of its own.
            Assert.Equal("0", await f.ScalarAsync<string>(
                "SELECT COUNT(*) FROM collector_platform_dispatch_states WHERE tenant_id = UUID_TO_BIN(@tenant) AND paused_at IS NOT NULL;"));
        }
        finally
        {
            foreach (var id in (await f.RowsAsync("SELECT BIN_TO_UUID(id) FROM collector_app_events;")).Select(row => row[0]).Where(id => !eventsBefore.Contains(id)))
                await f.ExecAsync("DELETE FROM collector_app_events WHERE id = UUID_TO_BIN(@id);", ("@id", id));
            foreach (var node in nodes)
            {
                await f.ExecAsync("DELETE FROM collector_app_bindings WHERE node_id = @node;", ("@node", node));
                await f.ExecAsync("DELETE FROM collector_app_nodes WHERE node_id = @node;", ("@node", node));
            }
            await f.ExecAsync("""
                UPDATE collector_app_platform_states
                SET paused_at = NULLIF(@paused, ''), pause_reason = NULLIF(@reason, ''), last_dispatched_at = NULLIF(@dispatched, '')
                WHERE platform_code = 'doubao';
                """, ("@paused", state[0]), ("@reason", state[1]), ("@dispatched", state[2]));
        }
    }
}
