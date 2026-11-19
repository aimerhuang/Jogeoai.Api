using System.Text.RegularExpressions;

namespace Jogeoai.Tests;

/// <summary>042 只做加法、可重复执行；回滚脚本在存在 custom 提问时拒绝执行。只读 SQL 文本，不连接数据库。</summary>
public sealed class PromptTargetScopeMigrationTests
{
    private static string Load(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../db/migrations", relative));
        Assert.True(File.Exists(path), $"找不到 migration：{path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void Migration042AddsScopeColumnsAndTableReplaySafely()
    {
        var sql = Load("042_prompt_target_scopes.sql");
        Assert.Contains("DROP PROCEDURE IF EXISTS jogeoai_042_prompt_target_scopes//", sql, StringComparison.Ordinal);
        Assert.Contains("CALL jogeoai_042_prompt_target_scopes();", sql, StringComparison.Ordinal);
        Assert.Contains("column_name = 'target_scope'", sql, StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN target_scope VARCHAR(16) NOT NULL DEFAULT 'all'", sql, StringComparison.Ordinal);
        Assert.Contains("CHECK (target_scope IN ('all','custom'))", sql, StringComparison.Ordinal);
        Assert.Contains("column_name = 'creation_source'", sql, StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN creation_source VARCHAR(32) NULL", sql, StringComparison.Ordinal);
        Assert.Contains("creation_source IN ('manual','ai_recommend','keyword_expand','batch_import')", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE IF NOT EXISTS prompt_target_scopes", sql, StringComparison.Ordinal);
        Assert.Contains("PRIMARY KEY (prompt_id, target_key)", sql, StringComparison.Ordinal);
        Assert.Contains("KEY ix_pts_site (tenant_id, site_id)", sql, StringComparison.Ordinal);
        Assert.Contains("FOREIGN KEY (site_id, prompt_id) REFERENCES prompts (site_id, id) ON DELETE CASCADE", sql, StringComparison.Ordinal);
        Assert.Contains("FOREIGN KEY (tenant_id, site_id) REFERENCES sites (tenant_id, id)", sql, StringComparison.Ordinal);
        Assert.Contains("ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci", sql, StringComparison.Ordinal);

        var code = Regex.Replace(sql, @"--[^\r\n]*", "");
        Assert.DoesNotContain("DROP TABLE", code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP COLUMN", code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE FROM", code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE prompts", code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ENUM(", code, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rollback042RefusesCustomScopesBeforeDropping()
    {
        var sql = Load("rollback/042_prompt_target_scopes_rollback.sql");
        Assert.Contains("CALL jogeoai_042_prompt_target_scopes_rollback();", sql, StringComparison.Ordinal);
        var refuse = sql.IndexOf("SELECT 1 FROM prompts WHERE target_scope = 'custom'", StringComparison.Ordinal);
        Assert.True(refuse > 0);
        Assert.True(refuse < sql.IndexOf("DROP TABLE IF EXISTS prompt_target_scopes", StringComparison.Ordinal));
        Assert.True(sql.IndexOf("DROP TABLE IF EXISTS prompt_target_scopes", StringComparison.Ordinal)
            < sql.IndexOf("DROP COLUMN target_scope", StringComparison.Ordinal));
        Assert.Contains("DROP COLUMN creation_source", sql, StringComparison.Ordinal);
        Assert.Contains("DROP CHECK ck_prompts_target_scope", sql, StringComparison.Ordinal);
    }
}
