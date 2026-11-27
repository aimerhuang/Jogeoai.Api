using System.Text.Json;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using static Jogeoai.Tests.AppCycleRunSqlTests;
using static Jogeoai.Tests.TeamMonitoringConfigSqlTests;

namespace Jogeoai.Tests;

/// <summary>
/// 隔离库（已执行 001–052、库名含 _test）上的 App 全局账号池（implement 阶段 5）：节点令牌、绑定登记、领取的评估顺序、
/// 排队顺序、过期租约、失败上报的处理矩阵、与网页渠道的隔离。未设置 JOGEOAI_APP_CHANNEL_TEST_CONNECTION 时跳过，绝不使用业务库 jogeoai。
/// 每个用例只用自己新建的租户、节点和绑定，结束时删除它们写下的全部行（含审计事件），把平台闸门和 App 入口恢复成用例开始前的内容，
/// 并核对整库每张表的行数与用例开始前相同。时间推进靠改写数据库里的时间戳，不用 sleep。与迁移用例同一个集合：它们会执行 DDL。
/// </summary>
[Collection(AppChannelSqlCollection.Name)]
public sealed partial class AppLeaseSqlTests
{
    private static (string, int) Code(CollectorStoreException exception) => (exception.Code, exception.StatusCode);

    /// <summary>一个用例的账号池：自己的节点、绑定和事件，结束时清掉；平台闸门与 App 入口是全局行，用例前后原样恢复。</summary>
    internal sealed class Pool(string cs)
    {
        private readonly List<string> nodes = [];
        public string Cs => cs;
        public MySqlDatabase Database { get; } = new(cs);

        /// <summary>The pool store with Collector:AppTenantIds set to the given tenants.</summary>
        public AppCollectorPoolStore Store(params Guid[] openTenants) => new(Database, AppSiteTargetsSqlTests.OpenFor(openTenants));

        /// <summary>
        /// Registers a node. Since migration 052 a node may register 4 devices by default; most cases are not about that cap
        /// and use more devices, so the cap of a test node is lifted to the maximum (written directly, like the other fixture
        /// knobs: no audit event). A case about the cap itself passes <paramref name="defaultDeviceCap"/> = true.
        /// </summary>
        public async Task<(string NodeId, string Token)> NodeAsync(string name, bool defaultDeviceCap = false)
        {
            var nodeId = $"{name}-{Guid.NewGuid():N}";
            nodes.Add(nodeId);
            var credential = await Store().RegisterNodeAsync(nodeId, null);
            if (!defaultDeviceCap)
                await ExecAsync("UPDATE collector_app_nodes SET max_devices = @max WHERE node_id = @node;",
                    ("@max", AppPoolRules.MaxDevicesPerNodeLimit), ("@node", nodeId));
            return (credential.NodeId, credential.Token);
        }

        public void Track(string nodeId) => nodes.Add(nodeId);

        /// <summary>
        /// Reports one healthy binding and returns its id. Since migration 052 a reported binding waits for the operator's
        /// approval, so by default it is approved here, through the store method behind the admin resume endpoint (an extra
        /// binding_resumed event with actor "operator"). A case about the state before approval passes
        /// <paramref name="approve"/> = false.
        /// </summary>
        public async Task<Guid> BindAsync(string nodeId, string device, string platform, string? label = null, bool approve = true)
        {
            var result = await Store().ReportBindingsAsync(new AppBindingReportCommand(nodeId, "test-rev",
                [new AppBindingReport(device, platform, label ?? $"{platform}-{Guid.NewGuid():N}", $"pkg.{platform}", "1.0", "1.0", "15", "ok")]));
            Assert.Empty(result.Rejected);
            var binding = Assert.Single(result.Bindings).BindingId;
            // Only a binding that has never been approved is approved here; reporting an existing binding again changes nothing.
            if (approve && await CountAsync("SELECT COUNT(*) FROM collector_app_bindings WHERE id = UUID_TO_BIN(@id) AND approved_at IS NULL;",
                    ("@id", binding.ToString())) == 1)
                Assert.True(await Store().ResumeBindingAsync(binding, null));
            return binding;
        }

        /// <summary>status | stop_reason | approved (0/1), for the cases about approval.</summary>
        public Task<string> ApprovalAsync(Guid binding)
            => RowAsync("SELECT status, COALESCE(stop_reason, '-'), approved_at IS NOT NULL FROM collector_app_bindings WHERE id = UUID_TO_BIN(@id);",
                ("@id", binding.ToString()));

        public Task ExecAsync(string sql, params (string Name, object? Value)[] args) => TeamMonitoringConfigSqlTests.ExecAsync(cs, sql, args);

        public Task<string> RowAsync(string sql, params (string Name, object? Value)[] args) => AppCycleRunSqlTests.RowAsync(cs, sql, args);

        public Task<long> CountAsync(string sql, params (string Name, object? Value)[] args) => AppCycleRunSqlTests.CountAsync(cs, sql, args);

        // Time moves by rewriting timestamps.
        public async Task AgeBindingAsync(Guid binding, int seconds = 301)
        {
            await ExecAsync("UPDATE collector_app_bindings SET last_leased_at = last_leased_at - INTERVAL @s SECOND WHERE id = UUID_TO_BIN(@id);",
                ("@s", seconds), ("@id", binding.ToString()));
            // Advance the synthetic clock for prior sends of this binding, not just legacy dispatch metadata.
            await ExecAsync("""
                UPDATE collector_app_device_attempts SET leased_at = leased_at - INTERVAL @s SECOND, sent_at = sent_at - INTERVAL @s SECOND
                WHERE binding_id = UUID_TO_BIN(@id) AND sent_at IS NOT NULL;
                """, ("@s", seconds), ("@id", binding.ToString()));
        }

        public Task AgePlatformAsync(string platform, int seconds = 31)
            => ExecAsync("UPDATE collector_app_platform_states SET last_dispatched_at = last_dispatched_at - INTERVAL @s SECOND WHERE platform_code = @p;",
                ("@s", seconds), ("@p", platform));

        /// <summary>
        /// The collector finished the task, written directly (the result intake has its own cases): the device is free again.
        /// The task's device attempts are removed with it. A real finish records the send; an attempt left with no send
        /// report would keep its device in cooldown until 100 seconds after the lease would have ended, and would keep the
        /// device away from the task if a case puts it back in the queue.
        /// </summary>
        public Task FinishAsync(Guid task)
            => ExecAsync("""
                UPDATE collector_tasks SET status = 'succeeded', lease_owner = NULL, lease_until = NULL, completed_at = UTC_TIMESTAMP(6)
                WHERE id = UUID_TO_BIN(@id);
                DELETE FROM collector_app_device_attempts WHERE task_id = UUID_TO_BIN(@id);
                """, ("@id", task.ToString()));

        public Task<string> BindingAsync(Guid binding)
            => RowAsync("SELECT status, COALESCE(stop_reason, '-'), stopped_at IS NOT NULL, cooldown_until IS NOT NULL FROM collector_app_bindings WHERE id = UUID_TO_BIN(@id);",
                ("@id", binding.ToString()));

        /// <summary>Event kinds written for a binding, oldest first.</summary>
        public async Task<string[]> EventsAsync(Guid binding)
            => (await RowsAsync(cs, "SELECT event_kind FROM collector_app_events WHERE binding_id = UUID_TO_BIN(@id) ORDER BY occurred_at, id;",
                ("@id", binding.ToString()))).Select(row => row[0]!).ToArray();

        public async Task<JsonElement> LastEventAsync(string kind, Guid binding)
            => JsonSerializer.Deserialize<JsonElement>((await RowsAsync(cs, """
                SELECT CAST(detail AS CHAR) FROM collector_app_events WHERE event_kind = @kind AND binding_id = UUID_TO_BIN(@id)
                ORDER BY occurred_at DESC LIMIT 1;
                """, ("@kind", kind), ("@id", binding.ToString())))[0][0]!);

        public async Task CleanAsync(HashSet<string> eventsBefore, List<string?[]> platforms, List<string?[]> entries)
        {
            var after = await RowsAsync(cs, "SELECT BIN_TO_UUID(id) FROM collector_app_events;");
            foreach (var id in after.Select(row => row[0]!).Where(id => !eventsBefore.Contains(id)))
                await ExecAsync("DELETE FROM collector_app_events WHERE id = UUID_TO_BIN(@id);", ("@id", id));
            foreach (var node in nodes)
            {
                await ExecAsync("DELETE FROM collector_app_device_attempts WHERE node_id = @node;", ("@node", node));
                await ExecAsync("DELETE FROM collector_app_bindings WHERE node_id = @node;", ("@node", node));
                await ExecAsync("DELETE FROM collector_app_nodes WHERE node_id = @node;", ("@node", node));
            }
            foreach (var row in platforms)
                await ExecAsync("""
                    UPDATE collector_app_platform_states
                    SET min_interval_seconds = @interval, daily_cap = @cap, last_dispatched_at = @dispatched, paused_at = @paused, pause_reason = @reason
                    WHERE platform_code = @platform;
                    """, ("@platform", row[0]), ("@interval", row[1]), ("@cap", row[2]), ("@dispatched", row[3]), ("@paused", row[4]), ("@reason", row[5]));
            foreach (var row in entries)
                await ExecAsync("UPDATE collector_platform_entries SET enabled = @enabled, unavailable_reason = @reason WHERE id = UUID_TO_BIN(@id);",
                    ("@id", row[0]), ("@enabled", row[1] is "1" or "True"), ("@reason", row[2]));
        }
    }

    // Runs a case on its own tenants (removed by IsolatedAsync, which then compares every table's row count) and its own pool.
    internal static Task PoolAsync(Func<Pool, Func<Task<Guid>>, Task> body) => AppSiteTargetsSqlTests.IsolatedAsync(async (cs, seedTenant) =>
    {
        var eventsBefore = (await RowsAsync(cs, "SELECT BIN_TO_UUID(id) FROM collector_app_events;")).Select(row => row[0]!).ToHashSet();
        var platforms = await RowsAsync(cs, """
            SELECT platform_code, min_interval_seconds, daily_cap, DATE_FORMAT(last_dispatched_at, '%Y-%m-%d %H:%i:%s.%f'),
                   DATE_FORMAT(paused_at, '%Y-%m-%d %H:%i:%s.%f'), pause_reason FROM collector_app_platform_states;
            """);
        var entries = await RowsAsync(cs, "SELECT BIN_TO_UUID(id), enabled, unavailable_reason FROM collector_platform_entries WHERE channel = 'consumer_app';");
        // A case starts from an idle gate whatever an earlier case or run left in the global rows.
        await ExecAsync(cs, "UPDATE collector_app_platform_states SET last_dispatched_at = NULL, paused_at = NULL, pause_reason = NULL, daily_cap = NULL, min_interval_seconds = 30;");
        var pool = new Pool(cs);
        try { await body(pool, seedTenant); }
        finally { await pool.CleanAsync(eventsBefore, platforms, entries); }
    });

    /// <summary>
    /// 一个只有 App 目标的品牌和它的一轮周期 Run：每个平台 × 每条提问一条 consumer_app 任务，截止时点改到 6 天之后。
    /// 返回品牌、Run 和提问（按创建顺序，也就是排队序号的顺序）。
    /// </summary>
    internal static async Task<(Guid Site, Guid Run, Guid[] Prompts)> CycleAsync(Rig rig, string name, int prompts, params string[] platforms)
    {
        var site = await SeedAppBrandAsync(rig, name, platforms);
        var ids = new Guid[prompts];
        for (var i = 0; i < prompts; i++) ids[i] = await SeedPromptAsync(rig.Cs, site, $"{name}：第 {i + 1} 条提问，哪个记账工具好用？", i + 1);
        var run = (await RunSlotAsync(rig, await StartAsync(rig, site), 3)).RunId!.Value;
        await ExecAsync(rig.Cs, "UPDATE collector_tasks SET deadline_at = UTC_TIMESTAMP(6) + INTERVAL 6 DAY WHERE run_id = UUID_TO_BIN(@run);",
            ("@run", run.ToString()));
        return (site, run, ids);
    }

    // status | lease_version | lease_owner | attempt_count | failure_count | last_error_code | last_block_reason
    internal static Task<string> TaskAsync(string cs, Guid task)
        => AppCycleRunSqlTests.RowAsync(cs, """
            SELECT status, lease_version, COALESCE(lease_owner, '-'), attempt_count, failure_count, COALESCE(last_error_code, '-'),
                   COALESCE(last_block_reason, '-')
            FROM collector_tasks WHERE id = UUID_TO_BIN(@id);
            """, ("@id", task.ToString()));

    internal static async Task<Guid> TaskOfAsync(string cs, Guid run, Guid prompt, string platform)
        => Guid.Parse((await RowsAsync(cs, """
            SELECT BIN_TO_UUID(t.id) FROM collector_tasks t JOIN visibility_checks c ON c.id = t.check_id
            WHERE t.run_id = UUID_TO_BIN(@run) AND c.prompt_id = UUID_TO_BIN(@prompt) AND t.platform_code = @platform;
            """, ("@run", run.ToString()), ("@prompt", prompt.ToString()), ("@platform", platform)))[0][0]!);

    // 5.1: the token is returned once, only its SHA-256 is stored, and it identifies exactly one node.
    [AppChannelMySqlFact]
    public Task ANodeGetsItsTokenOnceOnlyTheHashIsStoredAndRotationAndDisablingTakeEffect() => PoolAsync(async (pool, _) =>
    {
        var store = pool.Store();
        var (node, token) = await pool.NodeAsync("node");
        Assert.True(token.Length >= 43);
        Assert.Equal($"{AppCollectorPoolStore.HashToken(token)}|active|∅|∅", await pool.RowAsync(
            "SELECT token_hash, status, last_seen_at, collector_build_revision FROM collector_app_nodes WHERE node_id = @node;", ("@node", node)));
        Assert.Equal(new AppNodeIdentity(node, AppNodeStatus.Active), await store.AuthenticateNodeAsync(token));
        foreach (var other in new[] { null, "", "   ", "unknown-token", AppCollectorPoolStore.HashToken(token), token + "x", new string('t', 600) })
            Assert.Null(await store.AuthenticateNodeAsync(other));
        Assert.Equal(("APP_NODE_EXISTS", 409), Code(await Assert.ThrowsAsync<CollectorStoreException>(() => store.RegisterNodeAsync(node, null))));
        Assert.Equal(("INVALID_APP_NODE", 400), Code(await Assert.ThrowsAsync<CollectorStoreException>(() => store.RegisterNodeAsync("  ", null))));
        Assert.Equal(("APP_NODE_NOT_FOUND", 404), Code(await Assert.ThrowsAsync<CollectorStoreException>(() => store.RotateNodeTokenAsync("missing-node", null))));
        Assert.Equal(("APP_NODE_NOT_FOUND", 404), Code(await Assert.ThrowsAsync<CollectorStoreException>(()
            => store.SetNodeStatusAsync("missing-node", AppNodeStatus.Disabled, null))));

        // A second node has its own token; each token finds its own node.
        var (second, secondToken) = await pool.NodeAsync("node");
        Assert.NotEqual(token, secondToken);
        Assert.Equal(second, (await store.AuthenticateNodeAsync(secondToken))!.NodeId);

        // Rotation: the old token stops working at once.
        var rotated = await store.RotateNodeTokenAsync(node, Guid.NewGuid());
        Assert.NotEqual(token, rotated.Token);
        Assert.Null(await store.AuthenticateNodeAsync(token));
        Assert.Equal(node, (await store.AuthenticateNodeAsync(rotated.Token))!.NodeId);

        // Disabling is visible to the caller; the other node is not affected.
        await store.SetNodeStatusAsync(node, AppNodeStatus.Disabled, null);
        await store.SetNodeStatusAsync(node, AppNodeStatus.Disabled, null);
        Assert.Equal(AppNodeStatus.Disabled, (await store.AuthenticateNodeAsync(rotated.Token))!.Status);
        Assert.Equal(AppNodeStatus.Active, (await store.AuthenticateNodeAsync(secondToken))!.Status);
        await store.SetNodeStatusAsync(node, AppNodeStatus.Active, null);
        Assert.Equal(AppNodeStatus.Active, (await store.AuthenticateNodeAsync(rotated.Token))!.Status);

        // Every change is an event; no event and no column holds a token.
        Assert.Equal("node_registered,node_token_rotated,node_status_changed,node_status_changed", await pool.RowAsync(
            "SELECT GROUP_CONCAT(event_kind ORDER BY occurred_at, id) FROM collector_app_events WHERE node_id = @node;", ("@node", node)));
        foreach (var secret in new[] { token, rotated.Token, secondToken })
            Assert.Equal(0, await pool.CountAsync("""
                SELECT (SELECT COUNT(*) FROM collector_app_events WHERE CAST(detail AS CHAR) LIKE CONCAT('%', @secret, '%') OR actor LIKE CONCAT('%', @secret, '%'))
                     + (SELECT COUNT(*) FROM collector_app_nodes WHERE token_hash = @secret OR node_id = @secret);
                """, ("@secret", secret)));
    });
}
