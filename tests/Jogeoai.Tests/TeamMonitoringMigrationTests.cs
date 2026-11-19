using System.Text.RegularExpressions;

namespace Jogeoai.Tests;

/// <summary>048 只做加法、可重复执行、不写 site_targets；回滚脚本只删除 048 新增的对象。只读 SQL 文本，不连接数据库。</summary>
public sealed class TeamMonitoringMigrationTests
{
    private static string Load(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../db/migrations", relative));
        Assert.True(File.Exists(path), $"找不到 migration：{path}");
        return File.ReadAllText(path);
    }

    private static string Code(string sql) => Regex.Replace(sql, @"--[^\r\n]*", "");

    [Fact]
    public void Migration048AddsTablesAndFollowFlagReplaySafely()
    {
        var sql = Load("048_team_monitoring_defaults.sql");
        Assert.Contains("CREATE TABLE IF NOT EXISTS tenant_monitoring_defaults", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE IF NOT EXISTS tenant_monitoring_default_cells", sql, StringComparison.Ordinal);
        Assert.Contains("PRIMARY KEY (tenant_id, provider, engine, answer_mode)", sql, StringComparison.Ordinal);
        Assert.Contains("FOREIGN KEY (tenant_id) REFERENCES tenants (id)", sql, StringComparison.Ordinal);
        Assert.Contains("FOREIGN KEY (tenant_id) REFERENCES tenant_monitoring_defaults (tenant_id) ON DELETE CASCADE", sql, StringComparison.Ordinal);
        Assert.Contains("CHECK (answer_mode IN ('', 'fast', 'deep'))", sql, StringComparison.Ordinal);
        Assert.Contains("CHECK (frequency IN ('twice_daily','daily','every_other_day','weekly'))", sql, StringComparison.Ordinal);
        Assert.Contains("ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci", sql, StringComparison.Ordinal);
        Assert.Contains("DROP PROCEDURE IF EXISTS jogeoai_048_team_monitoring_defaults//", sql, StringComparison.Ordinal);
        Assert.Contains("CALL jogeoai_048_team_monitoring_defaults();", sql, StringComparison.Ordinal);

        // Three steps: nullable column guarded by information_schema, transactional backfill of NULL flags only,
        // then NOT NULL DEFAULT 1 only while the column is still nullable.
        var add = sql.IndexOf("ADD COLUMN monitoring_follows_default TINYINT(1) NULL", StringComparison.Ordinal);
        var backfill = sql.IndexOf("WHERE b.monitoring_follows_default IS NULL;", StringComparison.Ordinal);
        var modify = sql.IndexOf("MODIFY COLUMN monitoring_follows_default TINYINT(1) NOT NULL DEFAULT 1", StringComparison.Ordinal);
        Assert.True(add > 0 && backfill > add && modify > backfill);
        Assert.Contains("column_name = 'monitoring_follows_default' AND is_nullable = 'YES'", sql, StringComparison.Ordinal);
        Assert.Contains("NOT EXISTS (SELECT 1 FROM tenant_monitoring_defaults d WHERE d.tenant_id = b.tenant_id)", sql, StringComparison.Ordinal);
        Assert.Contains("START TRANSACTION;", sql, StringComparison.Ordinal);
        Assert.Contains("COMMIT;", sql, StringComparison.Ordinal);
        Assert.Contains("SET SESSION group_concat_max_len", sql, StringComparison.Ordinal);
        // Baseline default = PromptMonitoringDefaults.Target (cloro / chatgpt, default mode, daily).
        Assert.Contains("'cloro', 'chatgpt', '', 'daily'", sql, StringComparison.Ordinal);
        // The monitoring provider set is the registered API answer providers.
        Assert.Contains("'cloro', 'doubao_api', 'deepseek_api', 'packy_qwen', 'packy_glm', 'packy_kimi', 'packy_yuanbao'", sql, StringComparison.Ordinal);
        foreach (var provider in Jogeoai.Domain.AnswerProviderDefinition.All)
            Assert.Contains($"'{provider.Provider}'", sql, StringComparison.Ordinal);

        var code = Code(sql);
        Assert.DoesNotContain("UPDATE site_targets", code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE FROM site_targets", code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INSERT INTO site_targets", code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP TABLE", code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP COLUMN", code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ENUM(", code, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rollback048DropsOnlyTheNewObjects()
    {
        var sql = Load("rollback/048_team_monitoring_defaults_rollback.sql");
        var code = Code(sql);
        var cells = code.IndexOf("DROP TABLE IF EXISTS tenant_monitoring_default_cells", StringComparison.Ordinal);
        var defaults = code.IndexOf("DROP TABLE IF EXISTS tenant_monitoring_defaults;", StringComparison.Ordinal);
        Assert.True(cells > 0 && defaults > cells, "子表须先于父表删除");
        Assert.Contains("DROP COLUMN monitoring_follows_default", code, StringComparison.Ordinal);
        Assert.Contains("CALL jogeoai_048_team_monitoring_defaults_rollback();", code, StringComparison.Ordinal);
        Assert.DoesNotContain("site_targets", code, StringComparison.OrdinalIgnoreCase);
        // The header lists the pre-rollback checks.
        Assert.Contains("SELECT COUNT(*) AS default_configs FROM tenant_monitoring_defaults;", sql, StringComparison.Ordinal);
        Assert.Contains("SELECT monitoring_follows_default, COUNT(*) AS brands FROM site_brand_settings GROUP BY monitoring_follows_default;", sql, StringComparison.Ordinal);
    }
}
