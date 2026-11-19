using Jogeoai.Application;
using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

/// <summary><c>citation_domain_types</c> 的显式 SQL 读取（迁移 034）；全局表，整表读入内存并缓存 10 分钟。</summary>
public sealed class CitationDomainTypeStore(MySqlDatabase database, TimeProvider? time = null) : ICitationDomainTypeStore
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    private const string SelectSql = """
        SELECT domain, include_subdomains, site_type, platform_key, platform_name, publish_url
        FROM citation_domain_types;
        """;

    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly SemaphoreSlim gate = new(1, 1);
    // 不可变引用整体替换，无锁读取不会读到半写的缓存。
    private volatile CacheEntry? cached;

    private sealed record CacheEntry(CitationDomainTypeIndex Index, DateTimeOffset LoadedAt);

    /// <inheritdoc />
    public async Task<CitationDomainTypeIndex> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (Fresh() is { } hit) return hit;
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (Fresh() is { } again) return again;
            var index = await ReadAsync(cancellationToken);
            cached = new CacheEntry(index, clock.GetUtcNow());
            return index;
        }
        finally { gate.Release(); }
    }

    private CitationDomainTypeIndex? Fresh()
        => cached is { } entry && clock.GetUtcNow() - entry.LoadedAt < CacheDuration ? entry.Index : null;

    private async Task<CitationDomainTypeIndex> ReadAsync(CancellationToken cancellationToken)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(SelectSql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rules = new List<CitationDomainTypeRule>();
        while (await reader.ReadAsync(cancellationToken))
        {
            // CHECK 约束保证取值合法；万一出现未知类型（更新的迁移），跳过该行等同未分类。
            if (CitationDomainTypeRule.ParseSiteType(reader.GetString(2)) is not { } siteType) continue;
            rules.Add(new CitationDomainTypeRule(reader.GetString(0), reader.GetBoolean(1), siteType,
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }
        return new CitationDomainTypeIndex(rules);
    }
}
