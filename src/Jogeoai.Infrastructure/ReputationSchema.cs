using System.Collections.Concurrent;
using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

/// <summary>038 口碑表的存在性检查；只检查表是否存在，不把任意 SQL 错误当作缺表。</summary>
internal static class ReputationSchema
{
    internal const string MissingCode = "REPUTATION_SCHEMA_NOT_MIGRATED";
    private static readonly TimeSpan NegativeCacheDuration = TimeSpan.FromSeconds(30);
    // 迁移只会新增表：按服务器与数据库缓存，一旦确认存在就长期缓存；缺失结果只缓存 30 秒，迁移执行后无需重启即可生效。
    private static readonly ConcurrentDictionary<string, (bool Ready, DateTime CheckedAt)> Cache = new(StringComparer.Ordinal);

    internal static async Task<bool> ExistsAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        var key = Key(connection);
        if (Cache.TryGetValue(key, out var cached)
            && (cached.Ready || DateTime.UtcNow - cached.CheckedAt < NegativeCacheDuration))
            return cached.Ready;
        await using var command = new MySqlCommand("""
            SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE()
              AND table_name IN ('reputation_check_work','reputation_check_sentiments',
                                 'reputation_term_facts','reputation_analysis_jobs');
            """, connection);
        var exists = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 4;
        Cache[key] = (exists, DateTime.UtcNow);
        return exists;
    }

    internal static async Task<bool> ExistsAsync(MySqlDatabase database, CancellationToken cancellationToken)
    {
        await using var connection = database.CreateConnection();
        if (Cache.TryGetValue(Key(connection), out var cached) && cached.Ready) return true;
        await connection.OpenAsync(cancellationToken);
        return await ExistsAsync(connection, cancellationToken);
    }

    private static readonly ConcurrentDictionary<string, (bool Ready, DateTime CheckedAt)> QuoteColumnCache = new(StringComparer.Ordinal);

    /// <summary>039 是否已执行（reputation_check_sentiments.model_quote 存在）；未执行时写入路径跳过该列。缓存规则同 038 检查。</summary>
    internal static async Task<bool> ModelQuoteColumnExistsAsync(MySqlConnection connection, MySqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var key = Key(connection);
        if (QuoteColumnCache.TryGetValue(key, out var cached)
            && (cached.Ready || DateTime.UtcNow - cached.CheckedAt < NegativeCacheDuration))
            return cached.Ready;
        await using var command = new MySqlCommand("""
            SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE()
              AND table_name = 'reputation_check_sentiments' AND column_name = 'model_quote';
            """, connection, transaction);
        var exists = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
        QuoteColumnCache[key] = (exists, DateTime.UtcNow);
        return exists;
    }

    // 打开前后都可用的键：服务器 + 数据库（连接打开后 ConnectionString 可能去掉密码，不能作为键）。
    private static string Key(MySqlConnection connection) => $"{connection.DataSource}/{connection.Database}";

    internal static CollectorStoreException Missing()
        => new(MissingCode, "口碑分析所需迁移 038 尚未部署。", 503);
}
