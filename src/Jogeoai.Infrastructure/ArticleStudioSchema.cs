using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

/// <summary>迁移 040 的存在性探测：旧向导与 Worker 认领在未迁移的库上继续按旧列读取。</summary>
internal static class ArticleStudioSchema
{
    internal const string NotMigratedCode = "ARTICLE_STUDIO_SCHEMA_NOT_MIGRATED";
    private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromSeconds(60);
    private static volatile bool migrated;
    private static long checkedAtTicks;

    internal static async Task RequireAsync(MySqlConnection connection, CancellationToken ct)
    {
        if (!await ExistsAsync(connection, ct))
            throw new CollectorStoreException(NotMigratedCode, "文章创作所需迁移 040 尚未部署。", 503);
    }

    // Positive results are cached for the process lifetime (040 is additive and never rolled back while the
    // application runs); a negative result is re-checked after a minute so an applied migration is picked up.
    internal static async Task<bool> ExistsAsync(MySqlConnection connection, CancellationToken ct)
    {
        if (migrated) return true;
        var last = Interlocked.Read(ref checkedAtTicks);
        if (last != 0 && DateTime.UtcNow.Ticks - last < NegativeCacheTtl.Ticks) return false;
        await using var command = new MySqlCommand("""
            SELECT (SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE()
                      AND table_name = 'article_generation_tasks'
                      AND column_name IN ('pipeline', 'include_competitors', 'optimization_goal', 'studio_active_article_id'))
                 + (SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE()
                      AND table_name IN ('article_generation_task_stages', 'article_generation_task_sources'));
            """, connection);
        var exists = Convert.ToInt32(await command.ExecuteScalarAsync(ct)) == 6;
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
