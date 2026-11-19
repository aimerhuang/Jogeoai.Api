using Jogeoai.Infrastructure;

namespace Jogeoai.Tests;

// Query-shape regression only; live/API readback separately verifies actual MySQL execution.
public sealed class DashboardOpsSqlTests
{
    [Fact]
    public void LargeAnswersAreNotExpandedOrSortedForCitationCounts()
    {
        var sql = AnalyticsQueryStore.DashboardOpsSql;
        Assert.Contains("SELECT COUNT(*) FROM citations ci WHERE ci.check_id = c.id", sql);
        Assert.DoesNotContain("GROUP BY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ORDER BY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("JOIN citations", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provider_tasks", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DISTINCT ci.url", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OptimizationPreservesTenantSiteTimeAndArrivalFences()
    {
        var sql = AnalyticsQueryStore.DashboardOpsSql;
        foreach (var fence in new[] { "c.tenant_id = UUID_TO_BIN(@tenant)", "c.site_id = UUID_TO_BIN(@site)",
                     "c.captured_at >= @from", "c.captured_at < @to", "a.check_id = c.id AND a.id <= @arrival" })
            Assert.Contains(fence, sql);
    }
}
