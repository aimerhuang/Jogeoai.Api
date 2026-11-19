using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using MySqlConnector;

namespace Jogeoai.Tests;

/// <summary>App 渠道隔离库用例；未设置 JOGEOAI_APP_CHANNEL_TEST_CONNECTION 时跳过，绝不使用业务库 jogeoai。</summary>
public sealed class AppChannelMySqlFactAttribute : FactAttribute
{
    public const string ConnectionVariable = "JOGEOAI_APP_CHANNEL_TEST_CONNECTION";

    public AppChannelMySqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
            Skip = "需提供已执行 001–048 迁移、库名含 _test 的独立测试库 JOGEOAI_APP_CHANNEL_TEST_CONNECTION；不能使用业务库 jogeoai。";
    }
}

/// <summary>App 渠道的隔离库用例会执行 DDL（迁移与回滚），同一个库上不能并行。</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AppChannelSqlCollection
{
    public const string Name = "App channel isolated SQL";
}

/// <summary>
/// 迁移 049（App 采集渠道）与它的回滚脚本。隔离库用例在库上真实执行两个脚本：可重复执行、回滚后与 048 的结构一致、
/// 回滚的四种拒绝条件、现有数据不变、种子与放开后的 CHECK。每个用例结束时删除自己的夹具并把库恢复到「049 已执行」。
/// </summary>
[Collection(AppChannelSqlCollection.Name)]
public sealed class AppChannelMigrationTests
{
    private const string Migration = "049_app_channel.sql";
    private const string Rollback = "rollback/049_app_channel_rollback.sql";
    private const string AffectedTables = "'visibility_checks','collector_platform_entries','collector_tasks','collector_attempts'";
    private static readonly string[] NewTables =
    [
        "collector_app_bindings", "collector_app_daily_reports", "collector_app_events",
        "collector_app_nodes", "collector_app_platform_states", "prompt_app_exclusions",
    ];
    private static readonly string[] AppFailureCodes =
        ["emulator_rejected", "contract_changed", "contract_unverified", "version_mismatch", "device_offline", "app_crashed"];

    // SHA-256 of AffectedSnapshotAsync() (columns, indexes, CHECKs and foreign keys of the four altered tables), taken on
    // 2026-11-17 from jogeoai_app_channel_test with 001–048 applied and before 049 had ever run there (MySQL 8.4.11).
    private const string Post048AffectedSha256 = "4ce5bd855dc1ed8d9cd86f18ab6b5e85876c10f3ed61ce182ee0331b69b59071";

    private static string MigrationPath(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../db/migrations", relative));
        Assert.True(File.Exists(path), $"找不到 migration：{path}");
        return path;
    }

    private static string Code(string sql) => Regex.Replace(sql, @"--[^\r\n]*", "");

    [Fact]
    public void Migration049IsAdditiveAndUsesNoTriggers()
    {
        var code = Code(File.ReadAllText(MigrationPath(Migration)));
        Assert.DoesNotContain("TRIGGER", code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP TABLE", code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP COLUMN", code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE FROM", code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ON DUPLICATE KEY", code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ENUM(", code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hourly_quota", code, StringComparison.OrdinalIgnoreCase);
        // Existing rows are never rewritten, and collector_platforms / site_targets are not altered at all.
        Assert.DoesNotMatch(@"(?i)\bUPDATE\s+\w", code);
        Assert.DoesNotMatch(@"(?i)ALTER\s+TABLE\s+(collector_platforms|site_targets|collection_schedules)\b", code);
        Assert.DoesNotMatch(@"(?i)INSERT\s+INTO\s+(collector_platforms|site_targets|collection_schedules)\b", code);
        Assert.Equal(NewTables.Length, Regex.Matches(code, "CREATE TABLE IF NOT EXISTS").Count);
        Assert.Contains("min_interval_seconds INT UNSIGNED NOT NULL DEFAULT 300", code, StringComparison.Ordinal);
        Assert.Contains("min_interval_seconds INT UNSIGNED NOT NULL DEFAULT 30,", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Rollback049ChecksEveryRefusalBeforeChangingAnything()
    {
        var code = Code(File.ReadAllText(MigrationPath(Rollback)));
        var firstChange = code.IndexOf("DELETE FROM collector_platform_entries", StringComparison.Ordinal);
        Assert.True(firstChange > 0);
        Assert.Equal(firstChange, new[] { "DELETE FROM", "ALTER TABLE", "DROP TABLE" }
            .Select(keyword => code.IndexOf(keyword, StringComparison.Ordinal)).Where(index => index >= 0).Min());
        foreach (var condition in new[]
        {
            "FROM visibility_checks WHERE capture_mode = 'app'",
            "FROM site_targets WHERE provider = 'app_collector'",
            "WHERE name = 'jogeoai:auto-prompt-monitoring:app:v1' AND enabled = TRUE",
            "FROM collector_tasks WHERE channel = 'consumer_app'",
        })
        {
            var index = code.IndexOf(condition, StringComparison.Ordinal);
            Assert.True(index > 0 && index < firstChange, $"拒绝条件必须出现在任何修改之前：{condition}");
        }
        Assert.Equal(6, Regex.Matches(code, "SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '049 rollback refused").Count);
    }

    [Fact]
    public void MissingSchemaIsReportedAs503()
    {
        var exception = AppChannelSchema.Missing();
        Assert.Equal(("APP_CHANNEL_SCHEMA_NOT_MIGRATED", 503), (exception.Code, exception.StatusCode));
        Assert.Equal(AppChannelSchema.NotMigratedCode, exception.Code);
        Assert.Contains("049", exception.Message, StringComparison.Ordinal);
    }

    [AppChannelMySqlFact]
    public async Task Migration049RunsTwiceUnderTheApplicationAccountAndLeavesExistingRowsUntouched()
    {
        var db = IsolatedDb.Open();
        var fixture = new Fixture(db);
        try
        {
            // implement 1.5：049 不建触发器，必须能用应用账号执行。
            var user = Assert.Single(await db.LinesAsync("SELECT CURRENT_USER();"));
            Assert.False(user.StartsWith("root@", StringComparison.OrdinalIgnoreCase), "049 必须用应用账号执行，不能用 root。");

            await db.RollbackAsync();
            Assert.False(await db.ProbeAsync());
            await fixture.CreateAsync();
            var platforms = await db.PlatformsAsync();
            var webEntries = await db.WebEntriesAsync();
            var siteTargets = await db.SiteTargetsAsync();
            var countsBefore = await db.RowCountsAsync();
            Assert.Equal(7, platforms.Split('\n').Length);
            Assert.Equal(7, webEntries.Split('\n').Length);
            Assert.True(siteTargets.Split('\n').Length >= 2);

            await db.ApplyAsync();
            var schemaOnce = await db.SchemaAsync();
            var countsOnce = await db.RowCountsAsync();
            const string seededIds = """
                SELECT CONCAT_WS('|', HEX(id), entry_code, created_at) FROM collector_platform_entries
                WHERE channel = 'consumer_app' ORDER BY entry_code;
                """;
            var seededOnce = await db.TextAsync(seededIds);
            var statesOnce = await db.TextAsync("SELECT CONCAT_WS('|', platform_code, created_at, updated_at) FROM collector_app_platform_states ORDER BY platform_code;");

            await db.ApplyAsync();
            Assert.Equal(schemaOnce, await db.SchemaAsync());
            Assert.Equal(countsOnce, await db.RowCountsAsync());
            Assert.Equal(seededOnce, await db.TextAsync(seededIds));
            Assert.Equal(statesOnce, await db.TextAsync("SELECT CONCAT_WS('|', platform_code, created_at, updated_at) FROM collector_app_platform_states ORDER BY platform_code;"));
            Assert.True(await db.ProbeAsync());
            await db.RequireAsync();

            // Existing rows: platforms, consumer_web entries and site targets are byte-for-byte what they were before 049.
            Assert.Equal(platforms, await db.PlatformsAsync());
            Assert.Equal(webEntries, await db.WebEntriesAsync());
            Assert.Equal(siteTargets, await db.SiteTargetsAsync());
            Assert.Equal(0, await db.CountAsync("SELECT COUNT(*) FROM collector_platform_entries WHERE channel = 'consumer_web' AND unavailable_reason IS NOT NULL;"));
            Assert.Contains("""chk|collector_platforms|ck_collector_platforms_channel|(`channel` = _utf8mb4\'consumer_web\')""", schemaOnce, StringComparison.Ordinal);
            Assert.Equal(1, await db.CountAsync("""
                SELECT COUNT(*) FROM collector_tasks WHERE id = UUID_TO_BIN(@task) AND channel = 'consumer_web' AND deadline_at IS NULL
                   AND fair_rank IS NULL AND prompt_rule_version IS NULL AND lease_app_binding_id IS NULL AND last_block_reason IS NULL;
                """, fixture.Ids()));
            Assert.Equal(1, await db.CountAsync("""
                SELECT COUNT(*) FROM visibility_checks WHERE id = UUID_TO_BIN(@check) AND capture_mode IS NULL AND app_binding_id IS NULL;
                """, fixture.Ids()));

            // Row counts: only the seven seeded App entries and the seven platform-state rows are new.
            Assert.Equal(countsBefore.Keys.Concat(NewTables).Order(StringComparer.Ordinal), countsOnce.Keys);
            foreach (var (table, count) in countsBefore)
                Assert.Equal(table == "collector_platform_entries" ? count + 7 : count, countsOnce[table]);
            foreach (var table in NewTables)
                Assert.Equal(table == "collector_app_platform_states" ? 7L : 0L, countsOnce[table]);
        }
        finally
        {
            await db.ApplyAsync();
            await fixture.DeleteAsync();
        }
    }

    [AppChannelMySqlFact]
    public async Task SeedsAreExactAndAReplayKeepsOperatorChanges()
    {
        var db = IsolatedDb.Open();
        try
        {
            await db.RollbackAsync();
            await db.ApplyAsync();
            Assert.Equal(
            [
                "deepseek|deepseek.consumer_app|1|DeepSeek App|cn|consumer_app|{}|1|-",
                "doubao|doubao.consumer_app|1|豆包 App|cn|consumer_app|{}|1|-",
                "ernie|ernie.consumer_app|1|文心一言 App|cn|consumer_app|{}|0|APP_CONTENT_UNREADABLE",
                "kimi|kimi.consumer_app|1|Kimi App|cn|consumer_app|{}|1|-",
                "qwen|qwen.consumer_app|1|千问 App|cn|consumer_app|{}|1|-",
                "tencent_yuanbao|tencent_yuanbao.consumer_app|1|腾讯元宝 App|cn|consumer_app|{}|1|-",
                "zhipu_qingyan|zhipu_qingyan.consumer_app|1|智谱清言 App|cn|consumer_app|{}|1|-",
            ], await db.AppEntriesAsync());
            Assert.Equal(
            [
                "deepseek|30|-|-|-|-", "doubao|30|-|-|-|-", "ernie|30|-|-|-|-", "kimi|30|-|-|-|-",
                "qwen|30|-|-|-|-", "tencent_yuanbao|30|-|-|-|-", "zhipu_qingyan|30|-|-|-|-",
            ], await db.PlatformStatesAsync());

            // An operator disables an entry and tunes a platform; a replay must not put the seed values back.
            await db.ExecAsync("""
                UPDATE collector_platform_entries SET enabled = FALSE, unavailable_reason = 'APP_EMULATOR_REJECTED'
                 WHERE entry_code = 'doubao.consumer_app';
                UPDATE collector_app_platform_states SET min_interval_seconds = 45, daily_cap = 500 WHERE platform_code = 'doubao';
                """);
            await db.ApplyAsync();
            var entries = await db.AppEntriesAsync();
            Assert.Equal(7, entries.Count);
            Assert.Equal("doubao|doubao.consumer_app|1|豆包 App|cn|consumer_app|{}|0|APP_EMULATOR_REJECTED", entries[1]);
            Assert.Equal("doubao|45|500|-|-|-", (await db.PlatformStatesAsync())[1]);
        }
        finally
        {
            await db.ApplyAsync();
            await db.ExecAsync("""
                UPDATE collector_platform_entries SET enabled = TRUE, unavailable_reason = NULL WHERE entry_code = 'doubao.consumer_app';
                UPDATE collector_app_platform_states SET min_interval_seconds = 30, daily_cap = NULL WHERE platform_code = 'doubao';
                """);
        }
    }

    [AppChannelMySqlFact]
    public async Task RollbackRestoresThePost048SchemaAndIsReplayable()
    {
        var db = IsolatedDb.Open();
        try
        {
            await db.ApplyAsync();
            var applied = await db.AffectedSnapshotAsync();
            await db.RollbackAsync();
            var affected = await db.AffectedSnapshotAsync();
            var restored = await db.SchemaAsync();
            Assert.NotEqual(applied, affected);
            Assert.False(await db.ProbeAsync());

            // The four altered tables are exactly what they were after 048 (pinned before 049 ever ran on this database).
            Assert.True(Sha256(affected) == Post048AffectedSha256, "回滚后的结构与 048 之后的不一致：\n" + affected);
            foreach (var original in new[]
            {
                """chk|visibility_checks|ck_visibility_checks_capture_mode|((`capture_mode` is null) or (`capture_mode` in (_utf8mb4\'api\',_utf8mb4\'scraped\')))""",
                """chk|collector_platform_entries|ck_collector_entries_channel|(`channel` = _utf8mb4\'consumer_web\')""",
                """chk|collector_tasks|ck_collector_tasks_channel|(`channel` = _utf8mb4\'consumer_web\')""",
                """_utf8mb4\'browser_failed\',_utf8mb4\'operator_archived\'))))""",
            })
                Assert.Contains(original, affected, StringComparison.Ordinal);
            foreach (var added in new[]
            {
                "app_binding_id", "unavailable_reason", "deadline_at", "fair_rank", "prompt_rule_version", "lease_app_binding_id",
                "last_block_reason", "ix_collector_tasks_app_claim", "ix_collector_attempts_app_binding_quota", "consumer_app", "emulator_rejected",
            })
                Assert.DoesNotContain(added, affected, StringComparison.Ordinal);
            foreach (var table in NewTables)
                Assert.DoesNotContain($"table|{table}|", restored, StringComparison.Ordinal);
            Assert.DoesNotContain("jogeoai_049", restored, StringComparison.Ordinal);
            Assert.Equal(7, await db.CountAsync("SELECT COUNT(*) FROM collector_platform_entries;"));
            Assert.Equal(7, await db.CountAsync("SELECT COUNT(*) FROM collector_platform_entries WHERE channel = 'consumer_web' AND enabled = TRUE;"));

            // Replaying the rollback is a no-op, and a second apply + rollback round-trips to the same schema.
            await db.RollbackAsync();
            Assert.Equal(restored, await db.SchemaAsync());
            await db.ApplyAsync();
            Assert.Equal(applied, await db.AffectedSnapshotAsync());
            await db.RollbackAsync();
            Assert.Equal(restored, await db.SchemaAsync());
        }
        finally
        {
            await db.ApplyAsync();
        }
    }

    [AppChannelMySqlFact]
    public async Task RollbackRefusesEachAppDataConditionAndChangesNothing()
    {
        var db = IsolatedDb.Open();
        var fixture = new Fixture(db);
        const string scheduleName = "jogeoai:auto-prompt-monitoring:app:v1";
        try
        {
            await db.ApplyAsync();
            await fixture.CreateAsync();
            var schema = await db.SchemaAsync();
            var ids = fixture.Ids(("@schedule", scheduleName),
                ("@app_key", CollectionIdentity.TargetKey("app_collector", "doubao", "app", "CN", "zh-CN")));

            async Task RefusedAsync(string message, string arrange, string undo)
            {
                Assert.Equal(1, await db.ExecAsync(arrange, ids));
                var counts = await db.RowCountsAsync();
                var entries = await db.AppEntriesAsync();
                var exception = await Assert.ThrowsAsync<MySqlException>(db.RollbackAsync);
                Assert.Equal("45000", exception.SqlState);
                Assert.Contains(message, exception.Message, StringComparison.Ordinal);
                Assert.Equal(schema, await db.SchemaAsync());
                Assert.Equal(counts, await db.RowCountsAsync());
                Assert.Equal(entries, await db.AppEntriesAsync());
                Assert.Equal(7, entries.Count);
                Assert.True(await db.ProbeAsync());
                Assert.Equal(1, await db.ExecAsync(undo, ids));
            }

            await RefusedAsync("visibility checks captured by the app channel exist",
                "UPDATE visibility_checks SET capture_mode = 'app' WHERE id = UUID_TO_BIN(@check);",
                "UPDATE visibility_checks SET capture_mode = NULL WHERE id = UUID_TO_BIN(@check);");
            // A disabled App target still blocks: the row would be orphaned by the old build.
            await RefusedAsync("app_collector site targets exist", """
                INSERT INTO site_targets (id, tenant_id, site_id, provider, engine, capture_mode, country, locale, target_key, enabled,
                    frequency, created_at, updated_at)
                VALUES (UUID_TO_BIN(UUID()), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), 'app_collector', 'doubao', 'app', 'CN', 'zh-CN', @app_key,
                    FALSE, 'weekly', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                """,
                "DELETE FROM site_targets WHERE tenant_id = UUID_TO_BIN(@tenant) AND provider = 'app_collector';");
            await RefusedAsync("enabled app monitoring schedules exist", """
                INSERT INTO collection_schedules (id, tenant_id, site_id, name, cadence, local_time, time_zone, selection_snapshot, enabled,
                    created_at, updated_at)
                VALUES (UUID_TO_BIN(UUID()), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), @schedule, 'daily', '09:11:00', 'Asia/Shanghai',
                    JSON_OBJECT(), TRUE, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                """,
                "UPDATE collection_schedules SET enabled = FALSE WHERE tenant_id = UUID_TO_BIN(@tenant) AND name = @schedule;");
            await RefusedAsync("consumer_app collector tasks exist",
                "UPDATE collector_tasks SET channel = 'consumer_app' WHERE id = UUID_TO_BIN(@task);",
                "UPDATE collector_tasks SET channel = 'consumer_web' WHERE id = UUID_TO_BIN(@task);");

            // With every condition cleared the rollback runs; a disabled App schedule row is allowed to stay.
            await db.RollbackAsync();
            Assert.False(await db.ProbeAsync());
            Assert.Equal(1, await db.CountAsync(
                "SELECT COUNT(*) FROM collection_schedules WHERE tenant_id = UUID_TO_BIN(@tenant) AND name = @schedule AND enabled = FALSE;", ids));
        }
        finally
        {
            await fixture.DeleteAsync();
            await db.ApplyAsync();
        }
    }

    [AppChannelMySqlFact]
    public async Task WidenedChecksAcceptAppValuesAndStillRejectUnknownOnes()
    {
        var db = IsolatedDb.Open();
        var fixture = new Fixture(db);
        try
        {
            await db.ApplyAsync();
            await fixture.CreateAsync();
            var ids = fixture.Ids(("@binding", Guid.NewGuid()));

            foreach (var mode in new string?[] { "app", "api", "scraped", null })
                Assert.Equal(1, await db.ExecAsync("""
                    UPDATE visibility_checks SET capture_mode = @mode, app_binding_id = UUID_TO_BIN(@binding) WHERE id = UUID_TO_BIN(@check);
                    """, [.. ids, ("@mode", mode)]));
            await AssertRejectedAsync(3819, "ck_visibility_checks_capture_mode", () => db.ExecAsync(
                "UPDATE visibility_checks SET capture_mode = 'consumer_web' WHERE id = UUID_TO_BIN(@check);", ids));

            Assert.Equal(7, await db.CountAsync("SELECT COUNT(*) FROM collector_platform_entries WHERE channel = 'consumer_app';"));
            Assert.Equal(7, await db.CountAsync("SELECT COUNT(*) FROM collector_platform_entries WHERE channel = 'consumer_web';"));
            await AssertRejectedAsync(3819, "ck_collector_entries_channel", () => db.ExecAsync("""
                INSERT INTO collector_platform_entries (id, platform_code, entry_code, version, display_name, region, channel, enabled, created_at)
                VALUES (UUID_TO_BIN(UUID()), 'doubao', 'doubao.consumer_tv', 1, '合成入口', 'cn', 'consumer_tv', FALSE, UTC_TIMESTAMP(6));
                """));

            Assert.Equal(1, await db.ExecAsync("""
                UPDATE collector_tasks SET channel = 'consumer_app', deadline_at = UTC_TIMESTAMP(6) + INTERVAL 7 DAY, fair_rank = 3,
                       prompt_rule_version = 1, lease_app_binding_id = UUID_TO_BIN(@binding), last_block_reason = 'binding_cooldown'
                 WHERE id = UUID_TO_BIN(@task);
                """, ids));
            await AssertRejectedAsync(3819, "ck_collector_tasks_channel", () => db.ExecAsync(
                "UPDATE collector_tasks SET channel = 'consumer_tv' WHERE id = UUID_TO_BIN(@task);", ids));
            Assert.Equal(1, await db.ExecAsync("""
                UPDATE collector_tasks SET channel = 'consumer_web', deadline_at = NULL, fair_rank = NULL, prompt_rule_version = NULL,
                       lease_app_binding_id = NULL, last_block_reason = NULL
                 WHERE id = UUID_TO_BIN(@task);
                """, ids));

            const string attempt = """
                INSERT INTO collector_attempts (id, tenant_id, collector_task_id, lease_version, node_id, region, event_kind, error_code,
                    app_binding_id, created_at)
                VALUES (UUID_TO_BIN(UUID()), UUID_TO_BIN(@tenant), UUID_TO_BIN(@task), 1, 'app-node-test', 'cn', @kind, @code,
                    UUID_TO_BIN(@binding), UTC_TIMESTAMP(6));
                """;
            Task<int> AttemptAsync(string kind, string? code) => db.ExecAsync(attempt, [.. ids, ("@kind", kind), ("@code", code)]);
            // Every App failure code is accepted, and every code 035 allowed still is.
            foreach (var code in AppFailureCodes.Concat(["login_required", "region_restricted", "challenge_required", "rate_limited",
                "page_contract_changed", "page_contract_unverified", "collection_timeout", "browser_failed", "operator_archived"]))
                Assert.Equal(1, await AttemptAsync("failed", code));
            Assert.Equal(1, await AttemptAsync("leased", null));
            await AssertRejectedAsync(3819, "ck_collector_attempts_failure_code", () => AttemptAsync("failed", "app_melted"));
            await AssertRejectedAsync(3819, "ck_collector_attempts_failure_code", () => AttemptAsync("failed", null));
            await AssertRejectedAsync(3819, "ck_collector_attempts_failure_code", () => AttemptAsync("leased", "device_offline"));
            Assert.Equal(AppFailureCodes.Length + 10, await db.CountAsync(
                "SELECT COUNT(*) FROM collector_attempts WHERE app_binding_id = UUID_TO_BIN(@binding);", ids));

            // Attempts that carry an App failure code would violate the restored CHECK, so the rollback refuses them too.
            var refused = await Assert.ThrowsAsync<MySqlException>(db.RollbackAsync);
            Assert.Equal("45000", refused.SqlState);
            Assert.Contains("collector attempts with app failure codes exist", refused.Message, StringComparison.Ordinal);
            Assert.True(await db.ProbeAsync());
        }
        finally
        {
            await db.ApplyAsync();
            await fixture.DeleteAsync();
        }
    }

    [AppChannelMySqlFact]
    public async Task NewTablesCarryTheAgreedDefaultsAndConstraints()
    {
        var db = IsolatedDb.Open();
        var fixture = new Fixture(db);
        var node = $"test-node-{Guid.NewGuid():N}";
        var other = $"test-node-{Guid.NewGuid():N}";
        var label = $"doubao-{Guid.NewGuid():N}";
        var actor = $"test-actor-{Guid.NewGuid():N}";
        const string reportDate = "2099-01-01";
        try
        {
            await db.ApplyAsync();
            await fixture.CreateAsync();
            await db.ExecAsync("DELETE FROM collector_app_daily_reports WHERE report_date = @date;", ("@date", reportDate));

            // 049 creates no trigger; the only triggers in the database are the two from 035.
            Assert.Equal(0, await db.CountAsync($"""
                SELECT COUNT(*) FROM information_schema.triggers WHERE trigger_schema = DATABASE()
                   AND event_object_table IN ({string.Join(",", NewTables.Select(table => $"'{table}'"))});
                """));
            Assert.Equal(2, await db.CountAsync("SELECT COUNT(*) FROM information_schema.triggers WHERE trigger_schema = DATABASE();"));

            // Nodes: one token per node, status active / disabled.
            const string addNode = """
                INSERT INTO collector_app_nodes (node_id, token_hash, created_at, updated_at)
                VALUES (@node, @hash, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                """;
            await db.ExecAsync(addNode, ("@node", node), ("@hash", Sha256(node)));
            await db.ExecAsync(addNode, ("@node", other), ("@hash", Sha256(other)));
            Assert.Equal("active", await db.TextAsync("SELECT status FROM collector_app_nodes WHERE node_id = @node;", ("@node", node)));
            await AssertRejectedAsync(1062, "uq_collector_app_nodes_token", () => db.ExecAsync(addNode,
                ("@node", $"{node}-copy"), ("@hash", Sha256(node))));
            await AssertRejectedAsync(3819, "ck_collector_app_nodes_status", () => db.ExecAsync(
                "UPDATE collector_app_nodes SET status = 'paused' WHERE node_id = @node;", ("@node", node)));
            Assert.Equal(1, await db.ExecAsync("UPDATE collector_app_nodes SET status = 'disabled' WHERE node_id = @node;", ("@node", other)));

            // Bindings: defaults per KD-21 (300 s, no daily cap, no hourly column), one account in one binding.
            const string addBinding = """
                INSERT INTO collector_app_bindings (id, node_id, device_id, platform_code, account_label, app_package, created_at, updated_at)
                VALUES (UUID_TO_BIN(UUID()), @node, @device, @platform, @label, 'com.example.synthetic', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                """;
            Task<int> BindAsync(string onNode, string device, string platform, string account) => db.ExecAsync(addBinding,
                ("@node", onNode), ("@device", device), ("@platform", platform), ("@label", account));
            Assert.Equal(1, await BindAsync(node, "AVD_A", "doubao", label));
            Assert.Equal("active|-|-|-|300|-", await db.TextAsync("""
                SELECT CONCAT_WS('|', status, IFNULL(stop_reason, '-'), IFNULL(stopped_at, '-'), IFNULL(cooldown_until, '-'),
                    min_interval_seconds, IFNULL(daily_quota, '-'))
                FROM collector_app_bindings WHERE node_id = @node;
                """, ("@node", node)));
            Assert.Equal(0, await db.CountAsync("""
                SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE()
                   AND table_name = 'collector_app_bindings' AND column_name LIKE '%hourly%';
                """));
            await AssertRejectedAsync(1062, "uq_collector_app_bindings_account", () => BindAsync(other, "AVD_B", "doubao", label));
            await AssertRejectedAsync(1062, "uq_collector_app_bindings_device", () => BindAsync(node, "AVD_A", "doubao", $"{label}-2"));
            Assert.Equal(1, await BindAsync(other, "AVD_B", "kimi", label)); // 同一个代号在另一个平台上是另一个账号
            await AssertRejectedAsync(1452, "fk_collector_app_bindings_node", () => BindAsync($"{node}-missing", "AVD_C", "qwen", $"{label}-3"));
            await AssertRejectedAsync(1452, "fk_collector_app_bindings_platform", () => BindAsync(node, "AVD_C", "not_a_platform", $"{label}-4"));

            Task<int> BindingAsync(string set, params (string Name, object? Value)[] extra) => db.ExecAsync(
                $"UPDATE collector_app_bindings SET {set} WHERE node_id = @node AND platform_code = 'doubao';", [("@node", node), .. extra]);
            await AssertRejectedAsync(3819, "ck_collector_app_bindings_stopped", () => BindingAsync("status = 'stopped'"));
            await AssertRejectedAsync(3819, "ck_collector_app_bindings_stop_reason", () => BindingAsync(
                "status = 'stopped', stop_reason = 'tired', stopped_at = UTC_TIMESTAMP(6)"));
            await AssertRejectedAsync(3819, "ck_collector_app_bindings_status", () => BindingAsync("status = 'cooling'"));
            foreach (var reason in new[] { "login_required", "challenge_required", "device_offline", "contract_changed",
                "contract_unverified", "version_mismatch", "emulator_rejected", "disabled_by_operator" })
                Assert.Equal(1, await BindingAsync("status = 'stopped', stop_reason = @reason, stopped_at = UTC_TIMESTAMP(6)", ("@reason", reason)));
            Assert.Equal(1, await BindingAsync("status = 'active', stop_reason = NULL, stopped_at = NULL"));
            await AssertRejectedAsync(3819, "ck_collector_app_bindings_limits", () => BindingAsync("min_interval_seconds = 0"));
            await AssertRejectedAsync(3819, "ck_collector_app_bindings_limits", () => BindingAsync("daily_quota = 0"));
            Assert.Equal(1, await BindingAsync("min_interval_seconds = 1, daily_quota = 1000000"));

            // Platform gates: positive values only, no ceiling; pause needs a reason.
            Task<int> PlatformAsync(string set) => db.ExecAsync($"UPDATE collector_app_platform_states SET {set} WHERE platform_code = 'ernie';");
            await AssertRejectedAsync(3819, "ck_collector_app_platform_states_limits", () => PlatformAsync("min_interval_seconds = 0"));
            await AssertRejectedAsync(3819, "ck_collector_app_platform_states_limits", () => PlatformAsync("daily_cap = 0"));
            await AssertRejectedAsync(3819, "ck_collector_app_platform_states_pause", () => PlatformAsync("paused_at = UTC_TIMESTAMP(6)"));
            Assert.Equal(1, await PlatformAsync("min_interval_seconds = 864000, daily_cap = 1000000, paused_at = UTC_TIMESTAMP(6), pause_reason = 'operator'"));
            await AssertRejectedAsync(1452, "fk_collector_app_platform_states_platform", () => db.ExecAsync("""
                INSERT INTO collector_app_platform_states (platform_code, created_at, updated_at)
                VALUES ('not_a_platform', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                """));

            // Audit events: no foreign keys (rejected requests are recorded too); detail is size-limited.
            const string addEvent = """
                INSERT INTO collector_app_events (id, occurred_at, event_kind, node_id, binding_id, platform_code, task_id, actor, detail)
                VALUES (UUID_TO_BIN(UUID()), UTC_TIMESTAMP(6), 'binding_rejected', @node, UUID_TO_BIN(@binding), 'doubao', UUID_TO_BIN(@task),
                    @actor, JSON_OBJECT('note', REPEAT('a', @size)));
                """;
            Task<int> EventAsync(int size) => db.ExecAsync(addEvent, ("@node", "node-that-was-never-registered"),
                ("@binding", Guid.NewGuid()), ("@task", Guid.NewGuid()), ("@actor", actor), ("@size", size));
            Assert.Equal(1, await EventAsync(100));
            await AssertRejectedAsync(3819, "ck_collector_app_events_detail_size", () => EventAsync(17000));
            Assert.Equal(1, await db.ExecAsync("""
                INSERT INTO collector_app_events (id, occurred_at, event_kind, actor) VALUES (UUID_TO_BIN(UUID()), UTC_TIMESTAMP(6), 'report_generated', @actor);
                """, ("@actor", actor)));

            // Daily reports: one per date, delivery is a placeholder that defaults to not_configured.
            const string addReport = """
                INSERT INTO collector_app_daily_reports (id, report_date, generated_at, has_alerts, summary, body)
                VALUES (UUID_TO_BIN(UUID()), @date, UTC_TIMESTAMP(6), FALSE, JSON_OBJECT('alerts', 0), '无告警');
                """;
            Assert.Equal(1, await db.ExecAsync(addReport, ("@date", reportDate)));
            Assert.Equal("not_configured|-|-", await db.TextAsync("""
                SELECT CONCAT_WS('|', delivery_status, IFNULL(sent_at, '-'), IFNULL(send_error, '-'))
                FROM collector_app_daily_reports WHERE report_date = @date;
                """, ("@date", reportDate)));
            await AssertRejectedAsync(1062, "uq_collector_app_daily_reports_date", () => db.ExecAsync(addReport, ("@date", reportDate)));
            await AssertRejectedAsync(3819, "ck_collector_app_daily_reports_delivery", () => db.ExecAsync(
                "UPDATE collector_app_daily_reports SET delivery_status = 'queued' WHERE report_date = @date;", ("@date", reportDate)));
            foreach (var status in new[] { "sent", "failed", "not_configured" })
                Assert.Equal(1, await db.ExecAsync("UPDATE collector_app_daily_reports SET delivery_status = @status WHERE report_date = @date;",
                    ("@status", status), ("@date", reportDate)));

            // Manual exclusions: append and revoke; at most one active exclusion per prompt.
            const string exclude = """
                INSERT INTO prompt_app_exclusions (id, tenant_id, site_id, prompt_id, reason, created_by, created_at)
                VALUES (UUID_TO_BIN(UUID()), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), UUID_TO_BIN(@target), '合成原因', UUID_TO_BIN(@operator),
                    UTC_TIMESTAMP(6));
                """;
            var ids = fixture.Ids(("@operator", Guid.NewGuid()));
            Task<int> ExcludeAsync(Guid prompt) => db.ExecAsync(exclude, [.. ids, ("@target", prompt)]);
            Assert.Equal(1, await ExcludeAsync(fixture.Prompt));
            await AssertRejectedAsync(1062, "uq_prompt_app_exclusions_active", () => ExcludeAsync(fixture.Prompt));
            await AssertRejectedAsync(3819, "ck_prompt_app_exclusions_revoke", () => db.ExecAsync(
                "UPDATE prompt_app_exclusions SET revoked_at = UTC_TIMESTAMP(6) WHERE prompt_id = UUID_TO_BIN(@prompt);", ids));
            Assert.Equal(1, await db.ExecAsync("""
                UPDATE prompt_app_exclusions SET revoked_at = UTC_TIMESTAMP(6), revoked_by = UUID_TO_BIN(@operator)
                 WHERE prompt_id = UUID_TO_BIN(@prompt) AND revoked_at IS NULL;
                """, ids));
            Assert.Equal(1, await ExcludeAsync(fixture.Prompt));
            Assert.Equal("2|1", await db.TextAsync("""
                SELECT CONCAT_WS('|', COUNT(*), COUNT(active_prompt_id)) FROM prompt_app_exclusions WHERE prompt_id = UUID_TO_BIN(@prompt);
                """, ids));
            await AssertRejectedAsync(1452, "fk_prompt_app_exclusions_prompt", () => ExcludeAsync(Guid.NewGuid()));
        }
        finally
        {
            await db.ApplyAsync();
            await db.ExecAsync("""
                DELETE FROM collector_app_bindings WHERE node_id IN (@node, @other);
                DELETE FROM collector_app_nodes WHERE node_id IN (@node, @other);
                DELETE FROM collector_app_events WHERE actor = @actor;
                DELETE FROM collector_app_daily_reports WHERE report_date = @date;
                UPDATE collector_app_platform_states SET min_interval_seconds = 30, daily_cap = NULL, paused_at = NULL, pause_reason = NULL
                 WHERE platform_code = 'ernie';
                """, ("@node", node), ("@other", other), ("@actor", actor), ("@date", reportDate));
            await fixture.DeleteAsync();
        }
    }

    private static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static async Task<MySqlException> AssertRejectedAsync(int number, string constraint, Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<MySqlException>(action);
        Assert.Equal(number, exception.Number);
        Assert.Contains(constraint, exception.Message, StringComparison.Ordinal);
        return exception;
    }

    /// <summary>隔离库的访问入口：只接受库名含 _test 的连接，执行迁移文件，读取结构与内容快照。</summary>
    private sealed class IsolatedDb
    {
        private readonly string connectionString;

        private IsolatedDb(string connectionString) => this.connectionString = connectionString;

        internal static IsolatedDb Open()
        {
            var value = Environment.GetEnvironmentVariable(AppChannelMySqlFactAttribute.ConnectionVariable);
            Assert.False(string.IsNullOrWhiteSpace(value), "未设置 JOGEOAI_APP_CHANNEL_TEST_CONNECTION；SQL 测试不会静默通过。");
            var builder = new MySqlConnectionStringBuilder(value!);
            Assert.False(string.IsNullOrWhiteSpace(builder.Database), "必须显式指定独立测试库。");
            Assert.NotEqual("jogeoai", builder.Database, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("_test", builder.Database, StringComparison.OrdinalIgnoreCase);
            return new IsolatedDb(value!);
        }

        internal Task ApplyAsync() => RunFileAsync(Migration);

        internal Task RollbackAsync() => RunFileAsync(Rollback);

        /// <summary>按 mysql 客户端的方式逐条执行迁移文件（识别 DELIMITER）；失败时清掉脚本留下的辅助存储过程。</summary>
        private async Task RunFileAsync(string relative)
        {
            var lines = await File.ReadAllLinesAsync(MigrationPath(relative));
            try
            {
                await using var connection = new MySqlConnection(connectionString);
                await connection.OpenAsync();
                var delimiter = ";";
                var statement = new StringBuilder();
                foreach (var raw in lines)
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("--", StringComparison.Ordinal)) continue;
                    if (line.StartsWith("DELIMITER ", StringComparison.Ordinal)) { delimiter = line[10..].Trim(); continue; }
                    statement.AppendLine(line);
                    if (!line.EndsWith(delimiter, StringComparison.Ordinal)) continue;
                    var sql = statement.ToString().TrimEnd();
                    await using var command = new MySqlCommand(sql[..^delimiter.Length], connection);
                    await command.ExecuteNonQueryAsync();
                    statement.Clear();
                }
                Assert.Equal(0, statement.Length);
            }
            finally
            {
                // A refused run stops at CALL, exactly like the mysql client, and leaves the helper procedure behind.
                foreach (var routine in await LinesAsync("""
                    SELECT routine_name FROM information_schema.routines
                    WHERE routine_schema = DATABASE() AND routine_name LIKE 'jogeoai\_049\_%';
                    """))
                    await ExecAsync($"DROP PROCEDURE IF EXISTS `{routine}`;");
            }
        }

        internal async Task<int> ExecAsync(string sql, params (string Name, object? Value)[] parameters)
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = Command(connection, sql, parameters);
            return await command.ExecuteNonQueryAsync();
        }

        internal async Task<List<string>> LinesAsync(string sql, params (string Name, object? Value)[] parameters)
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = Command(connection, sql, parameters);
            await using var reader = await command.ExecuteReaderAsync();
            var lines = new List<string>();
            while (await reader.ReadAsync())
            {
                var value = reader.IsDBNull(0) ? "" : reader.GetValue(0);
                lines.Add(value is byte[] bytes ? Encoding.UTF8.GetString(bytes)
                    : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "");
            }
            return lines;
        }

        internal async Task<string> TextAsync(string sql, params (string Name, object? Value)[] parameters)
            => string.Join("\n", await LinesAsync(sql, parameters));

        internal async Task<long> CountAsync(string sql, params (string Name, object? Value)[] parameters)
            => long.Parse(Assert.Single(await LinesAsync(sql, parameters)), System.Globalization.CultureInfo.InvariantCulture);

        internal async Task<bool> ProbeAsync()
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();
            return await AppChannelSchema.ProbeAsync(connection, null, CancellationToken.None);
        }

        internal async Task RequireAsync()
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();
            await AppChannelSchema.RequireAsync(connection, null, CancellationToken.None);
        }

        /// <summary>列、索引、CHECK、外键的 information_schema 快照；<paramref name="affectedOnly"/> 时只含 049 改动的 4 张现有表。</summary>
        private async Task<List<string>> DefinitionsAsync(bool affectedOnly)
        {
            string Only(string column) => affectedOnly ? $" AND {column} IN ({AffectedTables})" : "";
            var lines = new List<string>();
            lines.AddRange(await LinesAsync($"""
                SELECT CONCAT_WS('|', 'col', table_name, ordinal_position, column_name, column_type, is_nullable,
                    IFNULL(column_default, '<null>'), IFNULL(collation_name, '-'), extra, column_comment, IFNULL(generation_expression, ''))
                FROM information_schema.columns WHERE table_schema = DATABASE(){Only("table_name")}
                ORDER BY table_name, ordinal_position;
                """));
            lines.AddRange(await LinesAsync($"""
                SELECT CONCAT_WS('|', 'idx', table_name, index_name, non_unique, seq_in_index, IFNULL(column_name, ''), IFNULL(sub_part, ''))
                FROM information_schema.statistics WHERE table_schema = DATABASE(){Only("table_name")}
                ORDER BY table_name, index_name, seq_in_index;
                """));
            lines.AddRange(await LinesAsync($"""
                SELECT CONCAT_WS('|', 'chk', tc.table_name, cc.constraint_name, cc.check_clause)
                FROM information_schema.check_constraints cc
                JOIN information_schema.table_constraints tc ON tc.constraint_schema = cc.constraint_schema
                    AND tc.constraint_name = cc.constraint_name AND tc.constraint_type = 'CHECK'
                WHERE cc.constraint_schema = DATABASE(){Only("tc.table_name")}
                ORDER BY tc.table_name, cc.constraint_name;
                """));
            lines.AddRange(await LinesAsync($"""
                SELECT CONCAT_WS('|', 'fk', rc.table_name, rc.constraint_name, rc.referenced_table_name, rc.update_rule, rc.delete_rule,
                    (SELECT GROUP_CONCAT(CONCAT(k.column_name, '>', k.referenced_column_name) ORDER BY k.ordinal_position)
                     FROM information_schema.key_column_usage k WHERE k.constraint_schema = rc.constraint_schema
                       AND k.constraint_name = rc.constraint_name AND k.table_name = rc.table_name))
                FROM information_schema.referential_constraints rc WHERE rc.constraint_schema = DATABASE(){Only("rc.table_name")}
                ORDER BY rc.table_name, rc.constraint_name;
                """));
            return lines;
        }

        internal async Task<string> AffectedSnapshotAsync() => string.Join("\n", await DefinitionsAsync(affectedOnly: true));

        /// <summary>整库的结构快照：全部表、列、索引、CHECK、外键、存储过程和触发器。</summary>
        internal async Task<string> SchemaAsync()
        {
            var lines = await LinesAsync("""
                SELECT CONCAT_WS('|', 'table', table_name, engine, table_collation) FROM information_schema.tables
                WHERE table_schema = DATABASE() ORDER BY table_name;
                """);
            lines.AddRange(await DefinitionsAsync(affectedOnly: false));
            lines.AddRange(await LinesAsync("""
                SELECT CONCAT_WS('|', 'routine', routine_name, routine_type) FROM information_schema.routines
                WHERE routine_schema = DATABASE() ORDER BY routine_name;
                """));
            lines.AddRange(await LinesAsync("""
                SELECT CONCAT_WS('|', 'trigger', trigger_name, event_object_table, action_timing, event_manipulation)
                FROM information_schema.triggers WHERE trigger_schema = DATABASE() ORDER BY trigger_name;
                """));
            return string.Join("\n", lines);
        }

        internal async Task<SortedDictionary<string, long>> RowCountsAsync()
        {
            var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
            foreach (var table in await LinesAsync("""
                SELECT table_name FROM information_schema.tables
                WHERE table_schema = DATABASE() AND table_type = 'BASE TABLE' ORDER BY table_name;
                """))
                counts[table] = await CountAsync($"SELECT COUNT(*) FROM `{table}`;");
            return counts;
        }

        internal Task<string> PlatformsAsync() => TextAsync("""
            SELECT CONCAT_WS('|', code, display_name, region, channel, enabled, contract_version,
                IFNULL(contract_reviewed_at, '-'), created_at, updated_at)
            FROM collector_platforms ORDER BY code;
            """);

        internal Task<string> WebEntriesAsync() => TextAsync("""
            SELECT CONCAT_WS('|', HEX(id), platform_code, entry_code, version, display_name, region, channel,
                IFNULL(CAST(config AS CHAR), '-'), enabled, created_at)
            FROM collector_platform_entries WHERE channel = 'consumer_web' ORDER BY entry_code, version;
            """);

        internal Task<string> SiteTargetsAsync() => TextAsync("""
            SELECT CONCAT_WS('|', HEX(id), HEX(tenant_id), HEX(site_id), provider, engine, capture_mode, country, locale, target_key,
                enabled, created_at, updated_at, IFNULL(monitoring_enabled_at, '-'), IFNULL(answer_mode, '-'), frequency,
                IFNULL(frequency_anchor, '-'))
            FROM site_targets ORDER BY id;
            """);

        internal Task<List<string>> AppEntriesAsync() => LinesAsync("""
            SELECT CONCAT_WS('|', platform_code, entry_code, version, display_name, region, channel, CAST(config AS CHAR), enabled,
                IFNULL(unavailable_reason, '-'))
            FROM collector_platform_entries WHERE channel = 'consumer_app' ORDER BY entry_code, version;
            """);

        internal Task<List<string>> PlatformStatesAsync() => LinesAsync("""
            SELECT CONCAT_WS('|', platform_code, min_interval_seconds, IFNULL(daily_cap, '-'), IFNULL(last_dispatched_at, '-'),
                IFNULL(paused_at, '-'), IFNULL(pause_reason, '-'))
            FROM collector_app_platform_states ORDER BY platform_code;
            """);

        private static MySqlCommand Command(MySqlConnection connection, string sql, (string Name, object? Value)[] parameters)
        {
            var command = new MySqlCommand(sql, connection);
            foreach (var (name, value) in parameters)
                command.Parameters.AddWithValue(name, value is Guid id ? id.ToString() : value ?? DBNull.Value);
            return command;
        }
    }

    /// <summary>最小的合成夹具：一个租户、品牌、提问、Run、网页采集的 check 与任务、两条站点目标。全部用显式 SQL 写入并可完整删除。</summary>
    private sealed class Fixture(IsolatedDb db)
    {
        internal Guid Tenant { get; } = Guid.NewGuid();
        internal Guid Site { get; } = Guid.NewGuid();
        internal Guid Prompt { get; } = Guid.NewGuid();
        internal Guid Run { get; } = Guid.NewGuid();
        internal Guid RunTarget { get; } = Guid.NewGuid();
        internal Guid Check { get; } = Guid.NewGuid();
        internal Guid Task { get; } = Guid.NewGuid();

        internal (string Name, object? Value)[] Ids(params (string Name, object? Value)[] extra) =>
        [
            ("@tenant", Tenant), ("@site", Site), ("@prompt", Prompt), ("@run", Run), ("@run_target", RunTarget),
            ("@check", Check), ("@task", Task), .. extra,
        ];

        internal async Task CreateAsync()
        {
            await db.ExecAsync("""
                INSERT INTO tenants (id, name, created_at, updated_at)
                VALUES (UUID_TO_BIN(@tenant), 'app channel migration fixture', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                INSERT INTO sites (id, tenant_id, display_name, created_at, updated_at)
                VALUES (UUID_TO_BIN(@site), UUID_TO_BIN(@tenant), 'App 渠道迁移夹具', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                INSERT INTO prompts (id, site_id, content, created_at, updated_at)
                VALUES (UUID_TO_BIN(@prompt), UUID_TO_BIN(@site), '合成提问：管道摄像机怎么选？', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                INSERT INTO visibility_runs (id, tenant_id, site_id, idempotency_key, requested_at, created_at, updated_at)
                VALUES (UUID_TO_BIN(@run), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), @key, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                INSERT INTO run_engines (id, run_id, engine, target_key, created_at)
                VALUES (UUID_TO_BIN(@run_target), UUID_TO_BIN(@run), 'doubao', @web_key, UTC_TIMESTAMP(6));
                INSERT INTO visibility_checks (id, tenant_id, site_id, run_id, prompt_id, engine, prompt_snapshot, run_target_id, created_at, updated_at)
                VALUES (UUID_TO_BIN(@check), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), UUID_TO_BIN(@run), UUID_TO_BIN(@prompt), 'doubao',
                    '合成提问：管道摄像机怎么选？', UUID_TO_BIN(@run_target), UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                INSERT INTO collector_tasks (id, tenant_id, run_id, check_id, platform_entry_id, platform_code, region, language, channel,
                    question_snapshot, next_attempt_at, created_at, updated_at)
                SELECT UUID_TO_BIN(@task), UUID_TO_BIN(@tenant), UUID_TO_BIN(@run), UUID_TO_BIN(@check), e.id, 'doubao', 'cn', 'zh-CN',
                    'consumer_web', '合成提问：管道摄像机怎么选？', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)
                FROM collector_platform_entries e WHERE e.entry_code = 'doubao.consumer_web' AND e.version = 1;
                INSERT INTO site_targets (id, tenant_id, site_id, provider, engine, capture_mode, country, locale, target_key, enabled, created_at, updated_at)
                VALUES (UUID_TO_BIN(UUID()), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), 'cloro', 'chatgpt', 'scraped', 'US', 'en-US', @api_key, TRUE,
                        UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)),
                       (UUID_TO_BIN(UUID()), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), 'domestic_collector', 'doubao', 'consumer_web', 'CN', 'zh-CN', @web_key, TRUE,
                        UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                """, Ids(("@key", $"app-channel-migration-{Run:N}"),
                    ("@api_key", CollectionIdentity.TargetKey("cloro", "chatgpt", "scraped", "US", "en-US")),
                    ("@web_key", CollectionIdentity.TargetKey("domestic_collector", "doubao", "consumer_web", "CN", "zh-CN"))));
            Assert.Equal(1, await db.CountAsync("SELECT COUNT(*) FROM collector_tasks WHERE id = UUID_TO_BIN(@task);", Ids()));
        }

        internal async Task DeleteAsync()
        {
            if (await db.CountAsync("""
                SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'prompt_app_exclusions';
                """) == 1)
                await db.ExecAsync("DELETE FROM prompt_app_exclusions WHERE tenant_id = UUID_TO_BIN(@tenant);", Ids());
            await db.ExecAsync("""
                DELETE FROM collector_attempts WHERE tenant_id = UUID_TO_BIN(@tenant);
                DELETE FROM collector_tasks WHERE tenant_id = UUID_TO_BIN(@tenant);
                DELETE FROM visibility_checks WHERE tenant_id = UUID_TO_BIN(@tenant);
                DELETE FROM run_engines WHERE run_id = UUID_TO_BIN(@run);
                DELETE FROM visibility_runs WHERE tenant_id = UUID_TO_BIN(@tenant);
                DELETE FROM collection_schedules WHERE tenant_id = UUID_TO_BIN(@tenant);
                DELETE FROM site_targets WHERE tenant_id = UUID_TO_BIN(@tenant);
                DELETE FROM prompts WHERE site_id = UUID_TO_BIN(@site);
                DELETE FROM sites WHERE tenant_id = UUID_TO_BIN(@tenant);
                DELETE FROM tenants WHERE id = UUID_TO_BIN(@tenant);
                """, Ids());
        }
    }
}
