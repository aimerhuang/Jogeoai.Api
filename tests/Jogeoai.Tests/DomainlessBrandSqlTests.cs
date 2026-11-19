using System.Text;
using System.Text.Json;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using MySqlConnector;

namespace Jogeoai.Tests;

// 此隔离库必须事先明确授权并已执行 001–031；不自动创建库、不使用业务库、不自动清理数据。
public sealed class DomainlessBrandSqlTests
{
    [DomainlessDatabaseFact]
    public async Task MigrationReplayNullBrandsIdempotencyAndRevisionHistoryRemainSafe()
    {
        var connectionString = Environment.GetEnvironmentVariable("JOGEOAI_DOMAINLESS_TEST_CONNECTION")!;
        var config = new MySqlConnectionStringBuilder(connectionString);
        Assert.Equal("jogeoai_domainless_20261025", config.Database);
        Assert.Contains(config.Server, new[] { "127.0.0.1", "localhost" });
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        async Task<object?> Sql(string sql, params (string Key, object? Value)[] values)
        {
            await using var command = new MySqlCommand(sql, connection);
            foreach (var item in values) command.Parameters.AddWithValue(item.Key, item.Value ?? DBNull.Value);
            return await command.ExecuteScalarAsync();
        }
        var tenant = Guid.NewGuid();
        await Sql("INSERT INTO tenants (id,name,status,created_at,updated_at) VALUES (UUID_TO_BIN(@tenant),'domainless-test','active',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));", ("@tenant", tenant.ToString()));
        var legacy = Guid.NewGuid();
        await Sql("INSERT INTO sites (id,tenant_id,host,display_name,status,created_at,updated_at) VALUES (UUID_TO_BIN(@site),UUID_TO_BIN(@tenant),'legacy.test','既有品牌','archived',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));", ("@tenant", tenant.ToString()), ("@site", legacy.ToString()));
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../db/migrations/032_domainless_brands.sql"));
        for (var run = 0; run < 2; run++)
        {
            var delimiter = ";";
            var statement = new StringBuilder();
            foreach (var raw in await File.ReadAllLinesAsync(path))
            {
                var line = raw.Trim();
                if (line.StartsWith("--", StringComparison.Ordinal) || line.Length == 0) continue;
                if (line.StartsWith("DELIMITER ", StringComparison.Ordinal)) { delimiter = line[10..]; continue; }
                statement.AppendLine(line);
                if (!line.EndsWith(delimiter, StringComparison.Ordinal)) continue;
                await Sql(statement.ToString().TrimEnd()[..^delimiter.Length]);
                statement.Clear();
            }
            Assert.Equal(0, statement.Length);
        }
        Assert.Equal("legacy.test", await Sql("SELECT host FROM sites WHERE id=UUID_TO_BIN(@site)", ("@site", legacy.ToString())));
        await Sql("""
            INSERT INTO tenant_subscriptions (id,tenant_id,plan_code,source,started_at,expires_at,created_at,updated_at)
            VALUES (UUID_TO_BIN(@id),UUID_TO_BIN(@tenant),'pro','admin',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6)+INTERVAL 1 MONTH,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
            """, ("@id", Guid.NewGuid().ToString()), ("@tenant", tenant.ToString()));
        var store = new ManagementStore(new MySqlDatabase(connectionString));
        var firstRequest = new BrandWrite("无官网一", Description: "研发工业设备。", Aliases: ["First brand"], CreationRequestId: Guid.NewGuid());
        var attempts = await Task.WhenAll(store.CreateBrandAsync(tenant, firstRequest), store.CreateBrandAsync(tenant, firstRequest));
        var first = attempts[0];
        Assert.Equal(first.SiteId, attempts[1].SiteId);
        Assert.Equal(first.SiteId, (await store.CreateBrandAsync(tenant, firstRequest with
        { Name = " 无官网一 ", PrimaryDomain = " ", Description = " 研发工业设备。 " })).SiteId);
        await Sql("INSERT INTO tenant_onboarding (tenant_id,site_id,team_configured_at) VALUES (UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UTC_TIMESTAMP(6));",
            ("@tenant", tenant.ToString()), ("@site", first.SiteId.ToString()));
        var completion = new OnboardingCompletionRequest([], [new("工业检测设备如何选择？", "zh-CN")]);
        var completed = await store.CompleteOnboardingAsync(tenant, first.SiteId, completion);
        Assert.True(completed.Progress.Completed); Assert.Null(completed.Brand.PrimaryDomain);
        Assert.Equal(1, completed.PromptsCreated);
        var replayedCompletion = await store.CompleteOnboardingAsync(tenant, first.SiteId, completion);
        Assert.True(replayedCompletion.Progress.Completed); Assert.Equal(0, replayedCompletion.PromptsCreated);
        Assert.Equal(0, replayedCompletion.CompetitorsCreated);
        var second = await store.CreateBrandAsync(tenant, new("无官网二", Description: "提供设备维护服务。", CreationRequestId: Guid.NewGuid()));
        Assert.NotEqual(first.SiteId, second.SiteId); Assert.Null(first.PrimaryDomain); Assert.Null(second.PrimaryDomain);
        Assert.Equal(2L, Convert.ToInt64(await Sql("SELECT COUNT(*) FROM sites WHERE tenant_id=UUID_TO_BIN(@tenant) AND host IS NULL", ("@tenant", tenant.ToString()))));
        Assert.Equal(0L, Convert.ToInt64(await Sql("SELECT COUNT(*) FROM subject_terms WHERE site_id=UUID_TO_BIN(@site) AND namespace_key='domain'", ("@site", first.SiteId.ToString()))));
        Assert.Null(await store.GetBrandAsync(Guid.NewGuid(), first.SiteId));
        Assert.Equal(first.SiteId.ToString(), await Sql("SELECT BIN_TO_UUID(site_id) FROM tenant_onboarding WHERE tenant_id=UUID_TO_BIN(@tenant) AND completed_at IS NOT NULL", ("@tenant", tenant.ToString())));
        var conflict = await Assert.ThrowsAsync<CollectorStoreException>(() => store.CreateBrandAsync(tenant, firstRequest with { Description = "不同资料" }));
        Assert.Equal("BRAND_CREATE_IDEMPOTENCY_CONFLICT", conflict.Code);
        var before = await Sql("SELECT snapshot FROM subject_config_revisions WHERE site_id=UUID_TO_BIN(@site) AND version=1", ("@site", first.SiteId.ToString()));
        var withDomain = await store.UpdateBrandAsync(tenant, first.SiteId, new(PrimaryDomain: "brand.test", ExpectedVersion: 1));
        Assert.Equal(2u, withDomain.SubjectConfigVersion);
        Assert.Equal("brand.test", withDomain.PrimaryDomain);
        Assert.Equal("PRIMARY_DOMAIN_CONFLICT", (await Assert.ThrowsAsync<CollectorStoreException>(() => store.UpdateBrandAsync(tenant, second.SiteId,
            new(PrimaryDomain: "brand.test", ExpectedVersion: second.SubjectConfigVersion)))).Code);
        var cleared = await store.UpdateBrandAsync(tenant, first.SiteId, new(PrimaryDomain: "", ExpectedVersion: 2));
        Assert.Null(cleared.PrimaryDomain); Assert.Equal(3u, cleared.SubjectConfigVersion);
        Assert.Equal(before, await Sql("SELECT snapshot FROM subject_config_revisions WHERE site_id=UUID_TO_BIN(@site) AND version=1", ("@site", first.SiteId.ToString())));
        using (var snapshot = JsonDocument.Parse((string)(await Sql("SELECT snapshot FROM subject_config_revisions WHERE site_id=UUID_TO_BIN(@site) AND version=3", ("@site", first.SiteId.ToString())))!))
            Assert.Equal(JsonValueKind.Null, snapshot.RootElement.GetProperty("primaryDomain").ValueKind);
        // 归一化词很短但原始展示词超长，使 subject_terms 插入失败；前面的 Site/设置写入必须一并回滚。
        var failedRequestId = Guid.NewGuid();
        await Assert.ThrowsAsync<MySqlException>(() => store.CreateBrandAsync(tenant, new("回滚品牌", Description: "回滚测试资料。",
            Aliases: ["A" + new string(' ', 300) + "B"], CreationRequestId: failedRequestId)));
        Assert.Equal(0L, Convert.ToInt64(await Sql("SELECT COUNT(*) FROM sites WHERE tenant_id=UUID_TO_BIN(@tenant) AND creation_request_id=UUID_TO_BIN(@request)",
            ("@tenant", tenant.ToString()), ("@request", failedRequestId.ToString()))));
        await store.CreateBrandAsync(tenant, new("回滚品牌", Description: "回滚测试资料。", CreationRequestId: failedRequestId));
        // 将本隔离租户填至专业版 20 个名额，已提交请求仍可重放。
        for (var index = 3; index < 20; index++)
            await store.CreateBrandAsync(tenant, new($"无官网{index}", Description: "隔离测试业务介绍。"));
        Assert.Equal(first.SiteId, (await store.CreateBrandAsync(tenant, firstRequest)).SiteId);
        Assert.Equal("PLAN_LIMIT_EXCEEDED", (await Assert.ThrowsAsync<PlanLimitExceededException>(() => store.CreateBrandAsync(tenant,
            new("超限品牌", Description: "隔离测试业务介绍。")))).Code);
        Assert.Equal(20L, Convert.ToInt64(await Sql("SELECT COUNT(*) FROM sites WHERE tenant_id=UUID_TO_BIN(@tenant) AND status='active'", ("@tenant", tenant.ToString()))));
    }
}

public sealed class DomainlessDatabaseFactAttribute : FactAttribute
{
    public DomainlessDatabaseFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JOGEOAI_DOMAINLESS_TEST_CONNECTION")))
            Skip = "需明确授权并提供独立库 jogeoai_domainless_20261025 的 JOGEOAI_DOMAINLESS_TEST_CONNECTION（已执行001–031）。";
    }
}
