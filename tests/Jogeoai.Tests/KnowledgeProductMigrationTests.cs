namespace Jogeoai.Tests;

/// <summary>023 迁移只做加法、可重复执行，并保留复合租户外键与活动任务唯一键。</summary>
public sealed class KnowledgeProductMigrationTests
{
    private static string Load()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../db/migrations/023_knowledge_products.sql"));
        Assert.True(File.Exists(path), $"找不到 migration：{path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void Migration023CreatesProductTablesWithTenantBoundariesAndActiveJobUniqueness()
    {
        var sql = Load();

        foreach (var table in new[] { "knowledge_products", "knowledge_product_tags", "knowledge_product_parse_jobs" })
            Assert.Contains($"CREATE TABLE IF NOT EXISTS {table}", sql, StringComparison.Ordinal);

        // 产品只属于品牌；来源版本、入库资料和任务都带租户的复合外键。
        Assert.Contains("FOREIGN KEY (tenant_id, site_id) REFERENCES sites (tenant_id, id)", sql, StringComparison.Ordinal);
        Assert.Contains("FOREIGN KEY (tenant_id, product_id)", sql, StringComparison.Ordinal);
        Assert.Contains("FOREIGN KEY (tenant_id, source_document_id, source_version_id)", sql, StringComparison.Ordinal);
        Assert.Contains("REFERENCES knowledge_document_versions (tenant_id, document_id, id)", sql, StringComparison.Ordinal);
        Assert.Contains("fk_knowledge_products_knowledge_document", sql, StringComparison.Ordinal);
        Assert.Contains("UNIQUE KEY uq_knowledge_products_name (tenant_id, site_id, name)", sql, StringComparison.Ordinal);
        Assert.Contains("name VARCHAR(25) COLLATE utf8mb4_0900_bin", sql, StringComparison.Ordinal);

        // 同一产品同一时间只允许一个进行中任务。
        Assert.Contains("active_key BINARY(16) GENERATED ALWAYS AS (CASE WHEN status IN ('pending', 'running') THEN product_id ELSE NULL END) STORED", sql, StringComparison.Ordinal);
        Assert.Contains("UNIQUE KEY uq_product_parse_jobs_active (tenant_id, active_key)", sql, StringComparison.Ordinal);
        Assert.Contains("ix_product_parse_jobs_claim (status, next_attempt_at, lease_until)", sql, StringComparison.Ordinal);
        Assert.Contains("ix_product_parse_jobs_rate (tenant_id, created_at)", sql, StringComparison.Ordinal);
        Assert.Contains("lease_owner", sql, StringComparison.Ordinal);

        // 状态、来源与阶段都用 CHECK 约束的 VARCHAR。
        Assert.Contains("status IN ('draft', 'parsing', 'pending_review', 'approved', 'parse_failed', 'archived')", sql, StringComparison.Ordinal);
        Assert.Contains("source_kind IN ('manual', 'file', 'url', 'completion')", sql, StringComparison.Ordinal);
        Assert.Contains("job_kind IN ('file', 'url', 'completion')", sql, StringComparison.Ordinal);
        Assert.Contains("step IN ('read_source', 'model', 'finalize')", sql, StringComparison.Ordinal);
        Assert.Contains("OCTET_LENGTH(specs_json) <= 16384", sql, StringComparison.Ordinal);
        Assert.Contains("OCTET_LENGTH(field_sources_json) <= 65536", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Migration023AddsSourceAndContentKindColumnsReplaySafely()
    {
        var sql = Load();

        Assert.Contains("DROP PROCEDURE IF EXISTS jogeoai_023_knowledge_products", sql, StringComparison.Ordinal);
        Assert.Contains("CALL jogeoai_023_knowledge_products();", sql, StringComparison.Ordinal);
        foreach (var column in new[] { "source_kind", "source_url", "source_url_hash", "content_kind" })
            Assert.Contains($"column_name = '{column}'", sql, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE knowledge_documents ADD COLUMN source_kind VARCHAR(16) NOT NULL DEFAULT 'upload'", sql, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE knowledge_document_versions ADD COLUMN content_kind VARCHAR(16) NOT NULL DEFAULT 'text'", sql, StringComparison.Ordinal);
        Assert.Contains("ix_knowledge_documents_source (tenant_id, scope_site_key, source_kind)", sql, StringComparison.Ordinal);
        Assert.Contains("source_kind IN ('upload', 'link', 'product')", sql, StringComparison.Ordinal);
        Assert.Contains("content_kind IN ('text', 'image')", sql, StringComparison.Ordinal);

        Assert.DoesNotContain("CREATE TABLE knowledge_", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP TABLE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ENUM(", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE FROM", sql, StringComparison.OrdinalIgnoreCase);
    }
}
