namespace Jogeoai.Tests;

public sealed class UrlNormalizationMigrationTests
{
    [Fact]
    public void Migration011_QueuesVersionedRebuildWithoutReplacingPublishedFacts()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../db/migrations/011_dashboard_analytics.sql"));
        Assert.True(File.Exists(path), $"找不到 migration：{path}");
        var sql = File.ReadAllText(path);

        Assert.Contains("url_normalization_version", sql, StringComparison.Ordinal);
        Assert.Contains("normalization_status", sql, StringComparison.Ordinal);
        Assert.Contains("'detail-v2'", sql, StringComparison.Ordinal);
        Assert.Contains("'url-v2'", sql, StringComparison.Ordinal);
        Assert.Contains("old_release_id", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT IGNORE INTO analysis_check_work", sql, StringComparison.Ordinal);
        Assert.Contains("NOT EXISTS", sql, StringComparison.Ordinal);
        Assert.Contains("r.status = 'published'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DELETE FROM", sql, StringComparison.OrdinalIgnoreCase);
    }
}
