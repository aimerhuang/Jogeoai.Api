using System.Text.RegularExpressions;

namespace Jogeoai.Tests;

/// <summary>041 迁移只做加法与放宽、可重复执行；回滚脚本先检查前提再删除。只读 SQL 文本，不连接数据库。</summary>
public sealed class ArticleEditorMigrationTests
{
    private static string Load(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../db/migrations", relative));
        Assert.True(File.Exists(path), $"找不到 migration：{path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void Migration041WidensVersionKindAndAddsRevisionReplaySafely()
    {
        var sql = Load("041_article_editor.sql");
        foreach (var procedure in new[] { "jogeoai_041_article_editor", "jogeoai_041_ledger" })
        {
            Assert.Contains($"DROP PROCEDURE IF EXISTS {procedure}//", sql, StringComparison.Ordinal);
            Assert.Contains($"CALL {procedure}();", sql, StringComparison.Ordinal);
        }
        Assert.Contains("check_clause NOT LIKE '%refined%'", sql, StringComparison.Ordinal);
        Assert.Contains("CHECK (version_kind IN ('generated','manual_edit','imported','refined'))", sql, StringComparison.Ordinal);
        Assert.Contains("column_name = 'edit_revision'", sql, StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN edit_revision INT NOT NULL DEFAULT 0", sql, StringComparison.Ordinal);
        Assert.Contains("column_name = 'refine_operation_id'", sql, StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN refine_operation_id BINARY(16) NULL", sql, StringComparison.Ordinal);
        Assert.Contains("index_name = 'ix_credit_ledger_refine_operation'", sql, StringComparison.Ordinal);
        Assert.Contains("constraint_name = 'fk_credit_ledger_refine_operation'", sql, StringComparison.Ordinal);
        Assert.Contains("FOREIGN KEY (tenant_id, refine_operation_id) REFERENCES article_refine_operations (tenant_id, id)", sql, StringComparison.Ordinal);

        var code = Regex.Replace(sql, @"--[^\r\n]*", "");
        Assert.DoesNotContain("DROP TABLE", code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE FROM", code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE content_article_versions", code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ENUM(", code, StringComparison.OrdinalIgnoreCase);
        // The ledger table must be created before the foreign key that references it.
        Assert.True(sql.IndexOf("CREATE TABLE IF NOT EXISTS article_refine_operations", StringComparison.Ordinal)
            < sql.IndexOf("ADD CONSTRAINT fk_credit_ledger_refine_operation", StringComparison.Ordinal));
    }

    [Fact]
    public void Migration041CreatesRefineAndImageTablesWithTenantScopedKeysAndBounds()
    {
        var sql = Load("041_article_editor.sql");
        foreach (var table in new[] { "article_refine_operations", "content_images" })
            Assert.Contains($"CREATE TABLE IF NOT EXISTS {table}", sql, StringComparison.Ordinal);
        Assert.Contains("UNIQUE KEY uq_article_refine_operations_key (tenant_id, idempotency_key)", sql, StringComparison.Ordinal);
        Assert.Contains("scope IN ('whole','selection')", sql, StringComparison.Ordinal);
        Assert.Contains("status IN ('held','captured','released')", sql, StringComparison.Ordinal);
        Assert.Contains("CHECK (status <> 'captured' OR result_version_id IS NOT NULL)", sql, StringComparison.Ordinal);
        Assert.Contains("REFERENCES content_articles (tenant_id, site_id, id)", sql, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(sql, @"REFERENCES content_article_versions \(tenant_id, article_id, id\)").Count);
        Assert.Contains("content_type IN ('image/png','image/jpeg','image/webp','image/gif')", sql, StringComparison.Ordinal);
        Assert.Contains("byte_size BETWEEN 1 AND 5242880", sql, StringComparison.Ordinal);
        Assert.Contains("FOREIGN KEY (tenant_id, site_id) REFERENCES sites (tenant_id, id)", sql, StringComparison.Ordinal);
        Assert.Contains("UNIQUE KEY uq_content_images_storage_key (storage_key)", sql, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(sql, "ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci").Count);
    }

    [Fact]
    public void Rollback041RefusesWithEditorDataAndDropsObjectsInDependencyOrder()
    {
        var sql = Load("rollback/041_article_editor_rollback.sql");
        Assert.Contains("WHERE version_kind = 'refined'", sql, StringComparison.Ordinal);
        Assert.Contains("SELECT 1 FROM article_refine_operations LIMIT 1", sql, StringComparison.Ordinal);
        Assert.Contains("SELECT 1 FROM content_images LIMIT 1", sql, StringComparison.Ordinal);
        Assert.Contains("SIGNAL SQLSTATE '45000'", sql, StringComparison.Ordinal);
        Assert.Contains("LOWER(data_type) = 'int'", sql, StringComparison.Ordinal);
        var order = new[]
        {
            "DROP FOREIGN KEY fk_credit_ledger_refine_operation", "DROP INDEX ix_credit_ledger_refine_operation",
            "DROP COLUMN refine_operation_id", "DROP TABLE IF EXISTS article_refine_operations", "DROP TABLE IF EXISTS content_images",
            "DROP CHECK ck_content_article_versions_revision", "DROP COLUMN edit_revision",
            "DROP CHECK ck_content_article_versions_kind", "CHECK (version_kind IN ('generated','manual_edit','imported'))"
        };
        var positions = order.Select(fragment => sql.IndexOf(fragment, StringComparison.Ordinal)).ToArray();
        Assert.All(positions, position => Assert.True(position >= 0));
        Assert.Equal(positions.Order(), positions);
        // Every refusal check precedes the first destructive statement.
        Assert.True(sql.LastIndexOf("SIGNAL SQLSTATE", StringComparison.Ordinal) < positions[0]);
    }
}
