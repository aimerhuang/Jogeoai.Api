using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Microsoft.Extensions.Configuration;
using MySqlConnector;

namespace Jogeoai.Tests;

/// <summary>产品信息 SQL 测试只连接已执行 001–023 的独立测试库；未配置时跳过，绝不使用业务库 jogeoai。</summary>
public sealed class KnowledgeProductMySqlFactAttribute : FactAttribute
{
    public KnowledgeProductMySqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JOGEOAI_KNOWLEDGE_TEST_CONNECTION")))
            Skip = "需提供已执行 001–023 迁移的独立测试库 JOGEOAI_KNOWLEDGE_TEST_CONNECTION；不能使用业务库 jogeoai。";
    }
}

[Collection("Knowledge isolated SQL")]
public sealed class KnowledgeProductStoreSqlTests
{
    [KnowledgeProductMySqlFact]
    public async Task EnqueueEnforcesActiveJobUniquenessLeaseFencingAndDailyRateLimit()
    {
        var f = await Fixture.CreateAsync();
        try
        {
            var created = await f.Store.CreateAsync(f.Tenant, f.Site, f.User, new KnowledgeProductWrite("X100 智能插座", "人工介绍"));
            Assert.Equal(KnowledgeProductStatus.Draft, created.Status);
            Assert.Equal("manual", created.FieldSources["introduction"].Origin);

            var duplicate = await Assert.ThrowsAsync<CollectorStoreException>(() =>
                f.Store.CreateAsync(f.Tenant, f.Site, f.User, new KnowledgeProductWrite("X100 智能插座")));
            Assert.Equal("PRODUCT_NAME_CONFLICT", duplicate.Code);

            var job = await f.Store.EnqueueParseAsync(f.Tenant, f.Site, created.ProductId, f.User, new ProductParseRequest(ProductParseJobKind.Completion));
            Assert.Equal("pending", job.Status);
            Assert.Equal(KnowledgeProductStatus.Parsing, (await f.Store.GetAsync(f.Tenant, f.Site, created.ProductId))!.Status);

            // 同一产品同一时间只允许一个进行中任务。
            var second = await Assert.ThrowsAsync<CollectorStoreException>(() =>
                f.Store.EnqueueParseAsync(f.Tenant, f.Site, created.ProductId, f.User, new ProductParseRequest(ProductParseJobKind.Completion)));
            Assert.Equal("PRODUCT_PARSE_IN_PROGRESS", second.Code);
            Assert.Equal(409, second.StatusCode);
            // 生成列 active_key 的唯一键在状态校验之外也兜底：直接插入第二个 pending 任务必须失败。
            await Assert.ThrowsAsync<MySqlException>(() => f.ExecAsync("""
                INSERT INTO knowledge_product_parse_jobs (id, tenant_id, site_id, product_id, job_kind, status, step, next_attempt_at, requested_by, created_at, updated_at)
                VALUES (UUID_TO_BIN(UUID()), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), UUID_TO_BIN(@product), 'completion', 'pending', 'read_source', UTC_TIMESTAMP(6), UUID_TO_BIN(@user), UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                """, ("@tenant", f.Tenant.ToString()), ("@site", f.Site.ToString()), ("@product", created.ProductId.ToString()), ("@user", f.User.ToString())));

            var lease = await f.Store.ClaimParseJobAsync("knowledge-product-sql-test", TimeSpan.FromMinutes(2));
            Assert.NotNull(lease);
            Assert.Equal(job.JobId, lease!.JobId);
            Assert.Equal(1, lease.AttemptCount);
            var result = new ProductParseResult(new KnowledgeProductWrite("X100 智能插座", "模型介绍", "模型优势"),
                new Dictionary<string, ProductFieldSource>
                {
                    ["introduction"] = new("ai", true, false, false, [new ProductFieldEvidence("引用", "page:1")]),
                    ["advantages"] = new("ai", true, false, false, [new ProductFieldEvidence("引用", "page:2")])
                }, "gpt-6-luna", "openai-compatible", 1234, false, new string('a', 64));

            // 过期或被他人持有的租约不能写入结果。
            var stale = await Assert.ThrowsAsync<CollectorStoreException>(() =>
                f.Store.CompleteParseAsync(lease with { LeaseOwner = "someone-else" }, result));
            Assert.Equal("PRODUCT_PARSE_STALE_LEASE", stale.Code);
            Assert.True(await f.Store.HeartbeatAsync(lease, TimeSpan.FromMinutes(2)));
            Assert.False(await f.Store.HeartbeatAsync(lease with { LeaseOwner = "someone-else" }, TimeSpan.FromMinutes(2)));

            await f.Store.CompleteParseAsync(lease, result);
            var reviewed = (await f.Store.GetAsync(f.Tenant, f.Site, created.ProductId))!;
            Assert.Equal(KnowledgeProductStatus.PendingReview, reviewed.Status);
            Assert.Equal("人工介绍", reviewed.Introduction);
            Assert.Equal("manual", reviewed.FieldSources["introduction"].Origin);
            Assert.Equal("模型优势", reviewed.Advantages);
            Assert.Equal("ai", reviewed.FieldSources["advantages"].Origin);
            Assert.Equal("done", reviewed.LatestJob!.Status);
            Assert.Equal("gpt-6-luna", reviewed.LatestJob.ModelName);
            Assert.Equal(1234, reviewed.LatestJob.DurationMs);

            // 未核对的 AI 字段阻止入库。
            var unverified = await Assert.ThrowsAsync<CollectorStoreException>(() => f.Store.ApproveAsync(f.Tenant, f.Site, created.ProductId, f.User,
                new ProductApproveRequest(new KnowledgeProductWrite("X100 智能插座", "人工介绍", "模型优势"), [])));
            Assert.Equal("PRODUCT_FIELDS_UNVERIFIED", unverified.Code);

            // 每租户每日 50 次：补足当日任务数后再次发起返回 429，且不写积分账本。
            var creditsBefore = await f.ScalarAsync("SELECT COUNT(*) FROM credit_ledger WHERE tenant_id=UUID_TO_BIN(@tenant);", ("@tenant", f.Tenant.ToString()));
            var today = await f.Store.CountParseJobsTodayAsync(f.Tenant);
            for (var i = today; i < KnowledgeProductStore.DailyParseLimit; i++)
                await f.ExecAsync("""
                    INSERT INTO knowledge_product_parse_jobs (id, tenant_id, site_id, product_id, job_kind, status, step, next_attempt_at, requested_by, created_at, updated_at, completed_at)
                    VALUES (UUID_TO_BIN(UUID()), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), UUID_TO_BIN(@product), 'completion', 'done', 'finalize', UTC_TIMESTAMP(6), UUID_TO_BIN(@user), UTC_TIMESTAMP(6), UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                    """, ("@tenant", f.Tenant.ToString()), ("@site", f.Site.ToString()), ("@product", created.ProductId.ToString()), ("@user", f.User.ToString()));
            var limited = await Assert.ThrowsAsync<CollectorStoreException>(() =>
                f.Store.EnqueueParseAsync(f.Tenant, f.Site, created.ProductId, f.User, new ProductParseRequest(ProductParseJobKind.Completion)));
            Assert.Equal("PRODUCT_PARSE_RATE_LIMITED", limited.Code);
            Assert.Equal(429, limited.StatusCode);
            Assert.Equal(KnowledgeProductStatus.PendingReview, (await f.Store.GetAsync(f.Tenant, f.Site, created.ProductId))!.Status);
            Assert.Equal(creditsBefore, await f.ScalarAsync("SELECT COUNT(*) FROM credit_ledger WHERE tenant_id=UUID_TO_BIN(@tenant);", ("@tenant", f.Tenant.ToString())));

            // 列表、筛选与角标。
            var page = await f.Store.ListAsync(f.Tenant, f.Site, new KnowledgeProductListFilter(), null, 20);
            Assert.Single(page.Items);
            Assert.Equal(1, page.TotalCount);
            Assert.Equal(1, page.PendingReviewCount);
            Assert.Empty((await f.Store.ListAsync(f.Tenant, f.Site, new KnowledgeProductListFilter(KnowledgeProductStatus.Approved), null, 20)).Items);
            Assert.Empty(await f.Store.ListApprovedAsync(f.Tenant, f.Site));
            Assert.Null(await f.Store.GetAsync(f.Tenant, Guid.NewGuid(), created.ProductId));
        }
        finally
        {
            await f.CleanupAsync();
        }
    }

    private sealed class Fixture
    {
        private readonly string connectionString;

        private Fixture(string connection)
        {
            connectionString = connection;
            var database = new MySqlDatabase(connection);
            Management = new ManagementStore(database);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Knowledge:StorageRoot"] = Path.Combine(Path.GetTempPath(), $"jogeoai-knowledge-product-sql-{Guid.NewGuid():N}")
            }).Build();
            var knowledge = new KnowledgeStore(database, new LocalObjectStorage(configuration), new DeterministicEmbeddingProvider(), new InMemoryVectorIndex());
            Store = new KnowledgeProductStore(database, knowledge, Management);
        }

        public Guid Tenant { get; } = Guid.NewGuid();
        public Guid User { get; } = Guid.NewGuid();
        public Guid Site { get; private set; }
        public ManagementStore Management { get; }
        public KnowledgeProductStore Store { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var value = Environment.GetEnvironmentVariable("JOGEOAI_KNOWLEDGE_TEST_CONNECTION");
            Assert.False(string.IsNullOrWhiteSpace(value), "未设置 JOGEOAI_KNOWLEDGE_TEST_CONNECTION；SQL 测试不会静默通过。");
            var builder = new MySqlConnectionStringBuilder(value!);
            Assert.False(string.IsNullOrWhiteSpace(builder.Database), "必须显式指定独立测试库。");
            Assert.NotEqual("jogeoai", builder.Database, StringComparer.OrdinalIgnoreCase);
            var fixture = new Fixture(value!);
            await fixture.ExecAsync("""
                INSERT INTO tenants (id, name, status, created_at, updated_at)
                VALUES (UUID_TO_BIN(@tenant), 'knowledge products fixture', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                INSERT INTO auth_users (id, email, password_hash, status, created_at, updated_at)
                VALUES (UUID_TO_BIN(@user), @email, 'not-a-login', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                """, ("@tenant", fixture.Tenant.ToString()), ("@user", fixture.User.ToString()),
                ("@email", $"knowledge-products-{fixture.User:N}@example.test"));
            var brand = await fixture.Management.CreateBrandAsync(fixture.Tenant, new BrandWrite("Acme Products", "acme-products.test", Description: "测试品牌提供设备与服务。"));
            fixture.Site = brand.SiteId;
            return fixture;
        }

        /// <summary>只删除本夹具租户的产品数据；品牌等管理数据留在独立测试库中待统一清理。</summary>
        public async Task CleanupAsync()
        {
            foreach (var sql in new[]
                     {
                         "DELETE FROM knowledge_product_parse_jobs WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM knowledge_product_tags WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM knowledge_products WHERE tenant_id=UUID_TO_BIN(@tenant);"
                     })
                await ExecAsync(sql, ("@tenant", Tenant.ToString()));
        }

        public async Task ExecAsync(string sql, params (string Name, object? Value)[] parameters)
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new MySqlCommand(sql, connection);
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<long> ScalarAsync(string sql, params (string Name, object? Value)[] parameters)
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new MySqlCommand(sql, connection);
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
