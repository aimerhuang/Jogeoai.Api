using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using MySqlConnector;

namespace Jogeoai.Tests;

/// <summary>需要已执行 028 与 029 的独立测试库 JOGEOAI_SITE_ICON_TEST_CONNECTION；缺少连接串即跳过，且拒绝业务库 jogeoai。</summary>
public sealed class SiteIconMySqlFactAttribute : FactAttribute
{
    public SiteIconMySqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JOGEOAI_SITE_ICON_TEST_CONNECTION")))
            Skip = "需提供已执行 028、029 迁移的独立测试库 JOGEOAI_SITE_ICON_TEST_CONNECTION；不能使用业务库 jogeoai。";
    }
}

/// <summary>迁移 028/029 <c>site_icons</c> 的隔离库用例：upsert 覆盖、字节往返、UTC 时间与 CHECK 约束（含 029 放行的 yandex 来源）。</summary>
public sealed class SiteIconStoreSqlTests
{
    [SiteIconMySqlFact]
    public async Task UpsertRoundTripsBytesAndOverwritesFoundWithMissing()
    {
        var (connection, store) = Create();
        var domain = $"t{Guid.NewGuid():N}.sqltest.example";
        try
        {
            Assert.Null(await store.GetAsync(domain));
            var fetched = new DateTimeOffset(2026, 10, 21, 8, 0, 0, 123, TimeSpan.Zero);
            var bytes = SiteIconTestImages.Ico(16, 32);
            var found = new SiteIconRecord(domain, SiteIconStatus.Found, "image/x-icon", bytes, "favicon", fetched, fetched.AddDays(30));
            await store.SaveAsync(found);
            var read = await store.GetAsync(domain);
            Assert.NotNull(read);
            Assert.Equal((found.Status, found.ContentType, found.Source, found.FetchedAt, found.ExpiresAt),
                (read.Status, read.ContentType, read.Source, read.FetchedAt, read.ExpiresAt));
            Assert.Equal(bytes, read.Bytes);
            Assert.Equal(TimeSpan.Zero, read.FetchedAt.Offset);

            var missing = new SiteIconRecord(domain, SiteIconStatus.Missing, null, null, null, fetched.AddDays(31), fetched.AddDays(38));
            await store.SaveAsync(missing);
            var overwritten = await store.GetAsync(domain);
            Assert.Equal(SiteIconStatus.Missing, overwritten!.Status);
            Assert.Null(overwritten.Bytes);
            Assert.Null(overwritten.ContentType);
            Assert.Null(overwritten.Source);
            Assert.Equal(missing.ExpiresAt, overwritten.ExpiresAt);

            var inconsistent = new SiteIconRecord(domain, SiteIconStatus.Found, "image/png", null, "html", fetched, fetched);
            await Assert.ThrowsAsync<MySqlException>(() => store.SaveAsync(inconsistent));

            var yandex = new SiteIconRecord(domain, SiteIconStatus.Found, "image/png", SiteIconTestImages.Png(32), SiteIconSource.Yandex,
                fetched, fetched.AddDays(30));
            await store.SaveAsync(yandex);
            Assert.Equal(SiteIconSource.Yandex, (await store.GetAsync(domain))!.Source);
            var unknown = yandex with { Source = "iconhorse" };
            await Assert.ThrowsAsync<MySqlException>(() => store.SaveAsync(unknown));
        }
        finally
        {
            await using var cleanup = new MySqlConnection(connection);
            await cleanup.OpenAsync();
            await using var command = new MySqlCommand("DELETE FROM site_icons WHERE domain = @domain;", cleanup);
            command.Parameters.AddWithValue("@domain", domain);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static (string Connection, SiteIconStore Store) Create()
    {
        var value = Environment.GetEnvironmentVariable("JOGEOAI_SITE_ICON_TEST_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(value), "未设置 JOGEOAI_SITE_ICON_TEST_CONNECTION；SQL 测试不会静默通过。");
        var builder = new MySqlConnectionStringBuilder(value!);
        Assert.False(string.IsNullOrWhiteSpace(builder.Database), "必须显式指定独立测试库。");
        Assert.NotEqual("jogeoai", builder.Database, StringComparer.OrdinalIgnoreCase);
        return (value!, new SiteIconStore(new MySqlDatabase(value!)));
    }
}
