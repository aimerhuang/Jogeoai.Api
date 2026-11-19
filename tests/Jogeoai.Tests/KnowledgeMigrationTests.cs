namespace Jogeoai.Tests;

public sealed class KnowledgeMigrationTests
{
    [Fact]
    public void Migration012IsReplaySafeAndKeepsTenantBoundaries()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../db/migrations/012_rag_knowledge.sql"));
        Assert.True(File.Exists(path), $"找不到 migration：{path}");
        var sql = File.ReadAllText(path);

        foreach (var table in new[]
                 {
                     "knowledge_folders", "knowledge_documents", "knowledge_document_tags",
                     "knowledge_document_versions", "knowledge_document_chunks", "knowledge_embeddings",
                     "knowledge_ingest_jobs", "knowledge_context_requests", "knowledge_context_hits"
                 })
            Assert.Contains($"CREATE TABLE IF NOT EXISTS {table}", sql, StringComparison.Ordinal);

        Assert.Contains("uq_knowledge_documents_current_version", sql, StringComparison.Ordinal);
        Assert.Contains("fk_knowledge_documents_current_version", sql, StringComparison.Ordinal);
        Assert.Contains("FOREIGN KEY (tenant_id, site_id)", sql, StringComparison.Ordinal);
        Assert.Contains("FOREIGN KEY (tenant_id, document_id)", sql, StringComparison.Ordinal);
        Assert.Contains("scope_site_key BINARY(16) GENERATED ALWAYS", sql, StringComparison.Ordinal);
        Assert.Contains("uq_knowledge_folders_scope_parent_name", sql, StringComparison.Ordinal);
        Assert.Contains("fk_knowledge_folders_parent_scope", sql, StringComparison.Ordinal);
        Assert.Contains("fk_knowledge_documents_scope_folder", sql, StringComparison.Ordinal);
        Assert.Contains("fk_knowledge_document_chunks_document_version", sql, StringComparison.Ordinal);
        Assert.Contains("fk_knowledge_ingest_jobs_document_version", sql, StringComparison.Ordinal);
        Assert.Contains("object_key VARCHAR(512)", sql, StringComparison.Ordinal);
        Assert.Contains("FOREIGN KEY (tenant_id, chunk_id, version_id)", sql, StringComparison.Ordinal);
        Assert.Contains("byte_length BETWEEN 1 AND 104857600", sql, StringComparison.Ordinal);
        Assert.Contains("OCTET_LENGTH(location_json) <= 16384", sql, StringComparison.Ordinal);
        Assert.Contains("lease_owner", sql, StringComparison.Ordinal);
        Assert.Contains("uq_knowledge_ingest_jobs_idempotency", sql, StringComparison.Ordinal);
        Assert.Contains("uq_knowledge_ingest_jobs_version_processor", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE TABLE knowledge_", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP TABLE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ENUM(", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Migration013AddsReplaySafeBoundedContextSnapshot()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../db/migrations/013_knowledge_context_snapshot.sql"));
        var sql = File.ReadAllText(path);

        Assert.Contains("column_name = 'snapshot_json'", sql, StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN snapshot_json JSON NULL", sql, StringComparison.Ordinal);
        Assert.Contains("ck_knowledge_context_requests_snapshot", sql, StringComparison.Ordinal);
        Assert.Contains("OCTET_LENGTH(snapshot_json) <= 4194304", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP TABLE", sql, StringComparison.OrdinalIgnoreCase);
    }
}
