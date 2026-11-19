using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

/// <summary>迁移 041 的存在性探测：未迁移的库上草稿、微调与图片接口返回 503，文章详情与积分用量按旧列读取。</summary>
internal static class ArticleEditorSchema
{
    internal const string NotMigratedCode = "ARTICLE_EDITOR_SCHEMA_NOT_MIGRATED";
    private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromSeconds(60);
    private static volatile bool migrated;
    private static long checkedAtTicks;

    internal static async Task RequireAsync(MySqlConnection connection, CancellationToken ct)
    {
        if (!await ExistsAsync(connection, ct))
            throw new CollectorStoreException(NotMigratedCode, "文章编辑器所需迁移 041 尚未部署。", 503);
    }

    // Positive results are cached for the process lifetime (041 is additive and is not rolled back while the
    // application runs); a negative result is re-checked after a minute so an applied migration is picked up.
    internal static async Task<bool> ExistsAsync(MySqlConnection connection, CancellationToken ct)
    {
        if (migrated) return true;
        var last = Interlocked.Read(ref checkedAtTicks);
        if (last != 0 && DateTime.UtcNow.Ticks - last < NegativeCacheTtl.Ticks) return false;
        await using var command = new MySqlCommand("""
            SELECT (SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE()
                      AND ((table_name = 'content_article_versions' AND column_name = 'edit_revision')
                        OR (table_name = 'credit_ledger' AND column_name = 'refine_operation_id')))
                 + (SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE()
                      AND table_name IN ('article_refine_operations', 'content_images'))
                 + (SELECT COUNT(*) FROM information_schema.check_constraints WHERE constraint_schema = DATABASE()
                      AND constraint_name = 'ck_content_article_versions_kind' AND check_clause LIKE '%refined%');
            """, connection);
        var exists = Convert.ToInt32(await command.ExecuteScalarAsync(ct)) == 5;
        if (exists) migrated = true;
        else Interlocked.Exchange(ref checkedAtTicks, DateTime.UtcNow.Ticks);
        return exists;
    }

    /// <summary>测试用：清除探测缓存。</summary>
    internal static void Reset()
    {
        migrated = false;
        Interlocked.Exchange(ref checkedAtTicks, 0);
    }
}
