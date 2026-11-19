namespace Jogeoai.Tests;

/// <summary>043 迁移：建表可重复执行、复合租户外键、生成列唯一键、CHECK 约束、source_kind 加入 item，数据段幂等且不删数据。</summary>
public sealed class BrandKnowledgeMigrationTests
{
    private static string Load(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../db/migrations", relative));
        Assert.True(File.Exists(path), $"找不到 migration：{path}");
        return File.ReadAllText(path);
    }

    private static string Migration() => Load("043_brand_knowledge_items.sql");

    [Fact]
    public void Migration043CreatesBothTablesReplaySafelyWithTenantBoundaries()
    {
        var sql = Migration();

        Assert.Contains("CREATE TABLE IF NOT EXISTS brand_knowledge_items", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE IF NOT EXISTS brand_knowledge_extract_jobs", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE TABLE brand_", sql, StringComparison.Ordinal);
        // 识别任务表先建，条目表的 extract_job_id 外键才能引用它。
        Assert.True(sql.IndexOf("CREATE TABLE IF NOT EXISTS brand_knowledge_extract_jobs", StringComparison.Ordinal)
            < sql.IndexOf("CREATE TABLE IF NOT EXISTS brand_knowledge_items", StringComparison.Ordinal));

        // 两张表都只属于品牌：(tenant_id, site_id) 复合外键到 sites；条目的文档、任务、旧产品引用也带租户。
        Assert.Contains("CONSTRAINT fk_bki_site FOREIGN KEY (tenant_id, site_id) REFERENCES sites (tenant_id, id)", sql, StringComparison.Ordinal);
        Assert.Contains("CONSTRAINT fk_bkej_site FOREIGN KEY (tenant_id, site_id) REFERENCES sites (tenant_id, id)", sql, StringComparison.Ordinal);
        Assert.Contains("FOREIGN KEY (tenant_id, knowledge_document_id)\n        REFERENCES knowledge_documents (tenant_id, id)", sql, StringComparison.Ordinal);
        Assert.Contains("FOREIGN KEY (tenant_id, extract_job_id)\n        REFERENCES brand_knowledge_extract_jobs (tenant_id, id)", sql, StringComparison.Ordinal);
        Assert.Contains("FOREIGN KEY (tenant_id, legacy_product_id)\n        REFERENCES knowledge_products (tenant_id, id)", sql, StringComparison.Ordinal);

        // BINARY(16) UUID、DATETIME(6)、InnoDB + utf8mb4_0900_ai_ci。
        Assert.Contains("id BINARY(16) NOT NULL PRIMARY KEY", sql, StringComparison.Ordinal);
        Assert.Contains("created_at DATETIME(6) NOT NULL", sql, StringComparison.Ordinal);
        Assert.Equal(2, Count(sql, "ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci"));
        Assert.DoesNotContain("ENUM(", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Migration043DeclaresGeneratedUniqueKeysAndIndexes()
    {
        var sql = Migration();

        // 同品牌同维度未删除条目标题唯一（二进制排序），软删后可再用同名。
        Assert.Contains("active_title_key VARCHAR(140) COLLATE utf8mb4_0900_bin GENERATED ALWAYS AS (\n"
            + "        CASE WHEN deleted_at IS NULL THEN CONCAT(dimension, ':', title_key) ELSE NULL END) STORED", sql, StringComparison.Ordinal);
        Assert.Contains("UNIQUE KEY uq_bki_active_title (tenant_id, site_id, active_title_key)", sql, StringComparison.Ordinal);
        Assert.Contains("title_key VARCHAR(100) COLLATE utf8mb4_0900_bin NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("UNIQUE KEY uq_bki_legacy_product (tenant_id, legacy_product_id)", sql, StringComparison.Ordinal);
        Assert.Contains("KEY ix_bki_list (tenant_id, site_id, deleted_at, dimension, created_at)", sql, StringComparison.Ordinal);
        Assert.Contains("KEY ix_bki_sync (sync_status, sync_next_at, sync_lease_until)", sql, StringComparison.Ordinal);

        // 每品牌同一时间只有一个未结束识别任务。
        Assert.Contains("active_key BINARY(16) GENERATED ALWAYS AS (\n"
            + "        CASE WHEN status IN ('pending', 'running', 'done') AND resolved_at IS NULL THEN site_id ELSE NULL END) STORED", sql, StringComparison.Ordinal);
        Assert.Contains("UNIQUE KEY uq_bkej_active (tenant_id, active_key)", sql, StringComparison.Ordinal);
        Assert.Contains("KEY ix_bkej_claim (status, next_attempt_at, lease_until)", sql, StringComparison.Ordinal);
        Assert.Contains("KEY ix_bkej_rate (tenant_id, created_at)", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Migration043ConstrainsDimensionsLengthsAndStatuses()
    {
        var sql = Migration();

        Assert.Contains("dimension IN ('brand_intro', 'selling_point', 'audience', 'differentiator', 'product_service', 'faq')", sql, StringComparison.Ordinal);
        Assert.Contains("CHECK (CHAR_LENGTH(TRIM(title)) BETWEEN 1 AND 100)", sql, StringComparison.Ordinal);
        Assert.Contains("CHECK (CHAR_LENGTH(content) BETWEEN 1 AND 2000)", sql, StringComparison.Ordinal);
        Assert.Contains("source_kind IN ('manual', 'url', 'migrated')", sql, StringComparison.Ordinal);
        Assert.Contains("sync_status IN ('pending', 'synced', 'failed')", sql, StringComparison.Ordinal);
        Assert.Contains("status IN ('pending', 'running', 'done', 'failed', 'cancelled', 'saved', 'discarded')", sql, StringComparison.Ordinal);
        Assert.Contains("step IN ('fetch', 'model', 'finalize')", sql, StringComparison.Ordinal);
        Assert.Contains("OCTET_LENGTH(candidates_json) <= 262144", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Migration043ReplacesSourceKindCheckOnlyWhenItLacksItem()
    {
        var sql = Migration();

        Assert.Contains("DROP PROCEDURE IF EXISTS jogeoai_043_brand_knowledge_items//", sql, StringComparison.Ordinal);
        Assert.Contains("CALL jogeoai_043_brand_knowledge_items();", sql, StringComparison.Ordinal);
        // MySQL 把 check_clause 中的引号存为 \'，按 '%''item''%' 匹配永远不成立（2026-11-03 隔离库实测）。
        Assert.Contains("check_clause LIKE '%item%'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("LIKE '%''item''%'", sql, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE knowledge_documents DROP CHECK ck_knowledge_documents_source_kind", sql, StringComparison.Ordinal);
        Assert.Contains("CHECK (source_kind IN ('upload', 'link', 'product', 'item'))", sql, StringComparison.Ordinal);
        // 约束调整必须先于数据段的文档接管。
        Assert.True(sql.IndexOf("CALL jogeoai_043_brand_knowledge_items();", StringComparison.Ordinal)
            < sql.IndexOf("SET d.source_kind = 'item'", StringComparison.Ordinal));
    }

    [Fact]
    public void Migration043DataSectionIsIdempotentAndNeverDeletes()
    {
        var sql = Migration();

        // approved 产品沿用 UUID 迁为 product_service；按 legacy_product_id 与活跃同名判存。
        Assert.Contains("WHERE p.status = 'approved'", sql, StringComparison.Ordinal);
        Assert.Contains("SELECT t.id, t.tenant_id, t.site_id, 'product_service'", sql, StringComparison.Ordinal);
        Assert.Contains("x.tenant_id = t.tenant_id AND x.legacy_product_id = t.id", sql, StringComparison.Ordinal);
        Assert.Contains("x.dimension = 'product_service' AND x.title_key = t.title_key", sql, StringComparison.Ordinal);
        Assert.Contains("LOWER(TRIM(REGEXP_REPLACE(p.name, '[[:space:]]+', ' '))) AS title_key", sql, StringComparison.Ordinal);

        // 内容按模板拼接、规格来自 JSON_TABLE，最后截断到 2000 字；GROUP_CONCAT 上限先放大。
        Assert.Contains("CONCAT_WS(", sql, StringComparison.Ordinal);
        Assert.Contains("JSON_TABLE(ps.specs_json, '$[*]'", sql, StringComparison.Ordinal);
        Assert.Contains("LEFT(t.full_content, 2000)", sql, StringComparison.Ordinal);
        Assert.Contains("SET SESSION group_concat_max_len", sql, StringComparison.Ordinal);
        foreach (var label in new[] { "'优势：'", "'适用场景与人群：'", "'客户案例：'", "'规格参数：'" })
            Assert.Contains(label, sql, StringComparison.Ordinal);

        // 接管原产品文档：只改仍为 product 的行，重放无影响。
        Assert.Contains("SET d.source_kind = 'item', d.document_type = 'product_service'", sql, StringComparison.Ordinal);
        Assert.Contains("AND d.source_kind = 'product'", sql, StringComparison.Ordinal);
        Assert.Contains("START TRANSACTION;", sql, StringComparison.Ordinal);
        Assert.Contains("COMMIT;", sql, StringComparison.Ordinal);

        // 迁移报告：截断、未迁移产品、活跃文件资料。
        Assert.Contains("CHAR_LENGTH(t.full_content) > 2000", sql, StringComparison.Ordinal);
        Assert.Contains("'not_approved'", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE d.source_kind = 'upload' AND d.status <> 'disabled'", sql, StringComparison.Ordinal);

        Assert.DoesNotContain("DROP TABLE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE FROM", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TRUNCATE TABLE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE knowledge_products", sql, StringComparison.OrdinalIgnoreCase);
        // 数据段不依赖反斜杠转义（NO_BACKSLASH_ESCAPES 下结果一致）。
        Assert.DoesNotContain("\\n", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Rollback043RestoresOldCheckOnlyWithoutItemRowsAndKeepsNewTables()
    {
        var sql = Load("rollback/043_brand_knowledge_items_rollback.sql");

        Assert.Contains("IF EXISTS (SELECT 1 FROM knowledge_documents WHERE source_kind = 'item' LIMIT 1) THEN", sql, StringComparison.Ordinal);
        Assert.Contains("SIGNAL SQLSTATE '45000'", sql, StringComparison.Ordinal);
        Assert.Contains("CHECK (source_kind IN ('upload', 'link', 'product'))", sql, StringComparison.Ordinal);
        Assert.Contains("CALL jogeoai_043_brand_knowledge_items_rollback();", sql, StringComparison.Ordinal);
        // 拒绝检查先于任何 ALTER。
        Assert.True(sql.IndexOf("source_kind = 'item' LIMIT 1", StringComparison.Ordinal)
            < sql.IndexOf("ALTER TABLE knowledge_documents DROP CHECK", StringComparison.Ordinal));

        // 只在注释中给出人工处理语句；可执行部分不删除新表、不改数据。
        var executable = string.Join('\n', sql.Split('\n').Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal)));
        Assert.DoesNotContain("DROP TABLE", executable, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE FROM", executable, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE ", executable, StringComparison.OrdinalIgnoreCase);
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
