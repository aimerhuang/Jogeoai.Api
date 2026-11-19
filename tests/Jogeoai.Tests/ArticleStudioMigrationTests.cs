using System.Text.RegularExpressions;

namespace Jogeoai.Tests;

/// <summary>040 迁移只做加法与放宽、可重复执行；回滚脚本先检查前提再删除。只读 SQL 文本，不连接数据库。</summary>
public sealed class ArticleStudioMigrationTests
{
    private static string Load(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../db/migrations", relative));
        Assert.True(File.Exists(path), $"找不到 migration：{path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void Migration040AddsStudioColumnsReplaySafelyWithoutTouchingLegacyRows()
    {
        var sql = Load("040_article_studio.sql");
        Assert.Contains("CALL jogeoai_040_article_studio();", sql, StringComparison.Ordinal);
        Assert.Contains("DROP PROCEDURE IF EXISTS jogeoai_040_article_studio", sql, StringComparison.Ordinal);
        foreach (var column in new[] { "pipeline", "include_competitors", "optimization_goal", "studio_active_article_id" })
            Assert.Contains($"column_name = '{column}'", sql, StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN pipeline VARCHAR(16) NOT NULL DEFAULT 'legacy'", sql, StringComparison.Ordinal);
        Assert.Contains("CHECK (pipeline IN ('legacy','studio_v1'))", sql, StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN include_competitors BOOLEAN NOT NULL DEFAULT FALSE", sql, StringComparison.Ordinal);
        Assert.Contains("optimization_goal IN ('mention','ranking','defend')", sql, StringComparison.Ordinal);
        Assert.Contains("LOWER(data_type) <> 'text'", sql, StringComparison.Ordinal);
        Assert.Contains("MODIFY COLUMN extra_requirements TEXT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("CASE WHEN pipeline = 'studio_v1' AND status IN ('pending','running') THEN article_id ELSE NULL END) STORED", sql, StringComparison.Ordinal);
        Assert.Contains("UNIQUE KEY uq_article_generation_tasks_studio_active (tenant_id, studio_active_article_id)", sql, StringComparison.Ordinal);
        foreach (var constraint in new[] { "ck_article_generation_tasks_pipeline", "ck_article_generation_tasks_goal" })
            Assert.Contains($"constraint_name = '{constraint}'", sql, StringComparison.Ordinal);
        foreach (var index in new[] { "uq_article_generation_tasks_studio_active", "ix_article_generation_tasks_studio_prompt" })
            Assert.Contains($"index_name = '{index}'", sql, StringComparison.Ordinal);

        var code = Regex.Replace(sql, @"--[^\r\n]*", "");
        Assert.DoesNotContain("DROP TABLE", code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE FROM", code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE article_generation_tasks", code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ENUM(", code, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Migration040CreatesStageAndSourceTablesWithTenantForeignKeysAndBounds()
    {
        var sql = Load("040_article_studio.sql");
        foreach (var table in new[] { "article_generation_task_stages", "article_generation_task_sources" })
            Assert.Contains($"CREATE TABLE IF NOT EXISTS {table}", sql, StringComparison.Ordinal);
        Assert.Contains("PRIMARY KEY (tenant_id, task_id, stage_key)", sql, StringComparison.Ordinal);
        Assert.Contains("stage_key IN ('citations','fetch','structure','web_research','requirements','compose')", sql, StringComparison.Ordinal);
        Assert.Contains("status IN ('pending','running','succeeded','skipped','failed')", sql, StringComparison.Ordinal);
        Assert.Contains("OCTET_LENGTH(detail_json) <= 65536", sql, StringComparison.Ordinal);
        Assert.Contains("source_kind IN ('citation','web_search')", sql, StringComparison.Ordinal);
        Assert.Contains("fetch_status IN ('pending','fetched','blocked','failed','skipped')", sql, StringComparison.Ordinal);
        Assert.Contains("CHAR_LENGTH(content_text) <= 20000", sql, StringComparison.Ordinal);
        Assert.Contains("KEY ix_article_generation_task_sources_task (tenant_id, task_id, position)", sql, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(sql, @"FOREIGN KEY \(tenant_id, task_id\)\s+REFERENCES article_generation_tasks \(tenant_id, id\)").Count);
        Assert.Equal(2, Regex.Matches(sql, "ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci").Count);
    }

    [Fact]
    public void Rollback040RefusesWithStudioDataAndDropsObjectsInDependencyOrder()
    {
        var sql = Load("rollback/040_article_studio_rollback.sql");
        Assert.Contains("WHERE pipeline = 'studio_v1'", sql, StringComparison.Ordinal);
        Assert.Contains("CHAR_LENGTH(extra_requirements) > 4000", sql, StringComparison.Ordinal);
        Assert.Contains("SIGNAL SQLSTATE '45000'", sql, StringComparison.Ordinal);
        var order = new[]
        {
            "DROP TABLE IF EXISTS article_generation_task_sources", "DROP TABLE IF EXISTS article_generation_task_stages",
            "DROP INDEX uq_article_generation_tasks_studio_active", "DROP COLUMN studio_active_article_id",
            "DROP COLUMN optimization_goal", "DROP COLUMN include_competitors", "DROP CHECK ck_article_generation_tasks_pipeline",
            "DROP COLUMN pipeline", "MODIFY COLUMN extra_requirements VARCHAR(4000) NULL"
        };
        var positions = order.Select(fragment => sql.IndexOf(fragment, StringComparison.Ordinal)).ToArray();
        Assert.All(positions, position => Assert.True(position >= 0));
        Assert.Equal(positions.Order(), positions);
    }
}
