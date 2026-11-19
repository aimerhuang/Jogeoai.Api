using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using MySqlConnector;

namespace Jogeoai.Tests;

/// <summary>积分用量 SQL 测试只连接已执行 001–026 的独立测试库；未配置时跳过，绝不使用业务库。</summary>
public sealed class CreditMySqlFactAttribute : FactAttribute
{
    public CreditMySqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JOGEOAI_CREDIT_TEST_CONNECTION")))
            Skip = "需提供已执行 001–026 迁移的独立测试库 JOGEOAI_CREDIT_TEST_CONNECTION；不能使用业务库 jogeoai。";
    }
}

/// <summary>
/// 用量聚合对账：文章 capture 的金额来自 credit_reservations（100），监控 capture 的金额来自 monitoring_credit_items（10），
/// capture 流水本身 amount 为 0；成员只看得到自己请求批次的文章消耗，看不到监控消耗。
/// </summary>
public sealed class CreditStoreSqlTests
{
    [CreditMySqlFact]
    public async Task UsageJoinsRealAmountsBucketsByDayAndHonoursTheMemberScope()
    {
        var f = await Fixture.CreateAsync();
        try
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var articleAt = today.AddDays(-1).ToDateTime(new TimeOnly(8, 0), DateTimeKind.Utc);
            var monitoringAt = today.ToDateTime(new TimeOnly(9, 0), DateTimeKind.Utc);
            await f.ArticleCaptureAsync(articleAt);
            await f.MonitoringCaptureAsync(monitoringAt);
            var store = new CreditStore(new MySqlDatabase(f.ConnectionString));
            var request = new CreditUsageRequest(today.AddDays(-2), today, "UTC");

            var tenantWide = await store.UsageAsync(f.Tenant, null, request);
            Assert.Equal("UTC", tenantWide.TimeZone);
            Assert.Equal(110m, tenantWide.TotalCredits);
            Assert.Equal(2, tenantWide.ActiveDays);
            Assert.Equal([CreditUsageType.ArticleGeneration, CreditUsageType.PromptMonitoring], tenantWide.Totals.Select(t => t.Type));
            Assert.Equal([100m, 10m], tenantWide.Totals.Select(t => t.Credits));
            Assert.Equal([1, 1], tenantWide.Totals.Select(t => t.Count));
            Assert.Equal([today.AddDays(-1), today], tenantWide.Days.Select(d => d.Date));
            Assert.NotNull(tenantWide.Latest);
            Assert.Equal(CreditUsageType.PromptMonitoring, tenantWide.Latest!.Type);
            Assert.Equal(10m, tenantWide.Latest.Credits);

            // 区间外：没有日期，但最新一笔仍然返回。
            var outside = await store.UsageAsync(f.Tenant, null, new CreditUsageRequest(today.AddDays(-30), today.AddDays(-10), "UTC"));
            Assert.Empty(outside.Days);
            Assert.NotNull(outside.Latest);

            // 成员：文章批次由本人请求，可见；监控结算没有 actor，不可见。
            var member = await store.UsageAsync(f.Tenant, f.User, request);
            var article = Assert.Single(member.Totals);
            Assert.Equal((CreditUsageType.ArticleGeneration, 100m, 1), (article.Type, article.Credits, article.Count));
            Assert.Equal(1, member.ActiveDays);
            Assert.Equal(CreditUsageType.ArticleGeneration, member.Latest!.Type);

            var stranger = await store.UsageAsync(f.Tenant, Guid.NewGuid(), request);
            Assert.Empty(stranger.Totals);
            Assert.Null(stranger.Latest);

            // 默认时区取最早创建的启用品牌（夹具品牌为 Asia/Shanghai）。
            var defaults = await store.UsageAsync(f.Tenant, null, new CreditUsageRequest());
            Assert.Equal("Asia/Shanghai", defaults.TimeZone);
            Assert.Equal(29, defaults.To.DayNumber - defaults.From.DayNumber);
        }
        finally
        {
            await f.CleanupAsync();
        }
    }

    private sealed class Fixture
    {
        private Fixture(string connection)
        {
            ConnectionString = connection;
            Management = new ManagementStore(new MySqlDatabase(connection));
        }

        public string ConnectionString { get; }
        public Guid Tenant { get; } = Guid.NewGuid();
        public Guid User { get; } = Guid.NewGuid();
        public Guid Site { get; private set; }
        public Guid Prompt { get; private set; }
        public ManagementStore Management { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var value = Environment.GetEnvironmentVariable("JOGEOAI_CREDIT_TEST_CONNECTION");
            Assert.False(string.IsNullOrWhiteSpace(value), "未设置 JOGEOAI_CREDIT_TEST_CONNECTION；SQL 测试不会静默通过。");
            var builder = new MySqlConnectionStringBuilder(value!);
            Assert.False(string.IsNullOrWhiteSpace(builder.Database), "必须显式指定独立测试库。");
            Assert.NotEqual("jogeoai", builder.Database, StringComparer.OrdinalIgnoreCase);
            var fixture = new Fixture(value!);
            await fixture.ExecAsync("""
                INSERT INTO tenants (id, name, status, created_at, updated_at)
                VALUES (UUID_TO_BIN(@tenant), 'credit usage fixture', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                INSERT INTO auth_users (id, email, password_hash, status, created_at, updated_at)
                VALUES (UUID_TO_BIN(@user), @email, 'not-a-login', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                INSERT INTO credit_accounts (tenant_id, updated_at) VALUES (UUID_TO_BIN(@tenant), UTC_TIMESTAMP(6));
                """, ("@tenant", fixture.Tenant.ToString()), ("@user", fixture.User.ToString()),
                ("@email", $"credit-{fixture.User:N}@example.test"));
            var brand = await fixture.Management.CreateBrandAsync(fixture.Tenant, new BrandWrite("Acme Credits",
                $"acme-credits-{fixture.Tenant:N}.test", TimeZone: "Asia/Shanghai", Description: "测试品牌提供设备与服务。"));
            fixture.Site = brand.SiteId;
            fixture.Prompt = (await fixture.Management.CreatePromptAsync(fixture.Tenant, fixture.Site,
                new PromptWrite("哪款扫地机器人适合养宠家庭？", "zh-CN"))).PromptId;
            return fixture;
        }

        /// <summary>文章：批次由夹具成员请求，任务预占 100 并已结算；capture 流水 amount=0、关联预占与版本。</summary>
        public async Task ArticleCaptureAsync(DateTime capturedAt)
        {
            var article = Guid.NewGuid(); var version = Guid.NewGuid();
            var batch = Guid.NewGuid(); var task = Guid.NewGuid(); var reservation = Guid.NewGuid();
            await ExecAsync("""
                INSERT INTO content_articles (id,tenant_id,site_id,created_by,source_kind,status,title,language,monitor_status,created_at,updated_at)
                VALUES (UUID_TO_BIN(@article),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@user),'ai_generated','active','用量文章','zh-CN','inactive',@at,@at);
                INSERT INTO content_article_versions (id,tenant_id,site_id,article_id,version_no,version_kind,title,body_markdown,content_length,language,created_by,created_at)
                VALUES (UUID_TO_BIN(@version),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@article),1,'generated','用量文章','正文',2,'zh-CN',UUID_TO_BIN(@user),@at);
                UPDATE content_articles SET current_version_id=UUID_TO_BIN(@version) WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@article);
                INSERT INTO article_generation_batches (id,tenant_id,site_id,requested_by,idempotency_key,request_hash,status,total_tasks,succeeded_tasks,captured_credits,request_snapshot,preview_expires_at,created_at)
                VALUES (UUID_TO_BIN(@batch),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@user),@key,@hash,'succeeded',1,1,100,JSON_OBJECT(),@at,@at);
                INSERT INTO article_generation_tasks (id,tenant_id,site_id,batch_id,article_id,prompt_id,prompt_snapshot,target_snapshot,language,strategy_key,length_profile,style,without_rag,status,next_attempt_at,reservation_id,created_at,updated_at)
                VALUES (UUID_TO_BIN(@task),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@batch),UUID_TO_BIN(@article),UUID_TO_BIN(@prompt),JSON_OBJECT(),JSON_ARRAY(),'zh-CN','answer_guide','medium','professional',TRUE,'succeeded',@at,UUID_TO_BIN(@reservation),@at,@at);
                INSERT INTO credit_reservations (id,tenant_id,batch_id,task_id,amount,status,idempotency_key,created_at,captured_at)
                VALUES (UUID_TO_BIN(@reservation),UUID_TO_BIN(@tenant),UUID_TO_BIN(@batch),UUID_TO_BIN(@task),100,'captured',CONCAT('task:',@task),@at,@at);
                INSERT INTO credit_ledger (id,tenant_id,entry_type,amount,balance_after,reservation_id,article_version_id,external_reference,created_at)
                VALUES (UUID_TO_BIN(UUID()),UUID_TO_BIN(@tenant),'capture',0,0,UUID_TO_BIN(@reservation),UUID_TO_BIN(@version),CONCAT('capture:',@reservation),@at);
                """, ("@article", article.ToString()), ("@version", version.ToString()), ("@tenant", Tenant.ToString()),
                ("@site", Site.ToString()), ("@user", User.ToString()), ("@batch", batch.ToString()), ("@task", task.ToString()),
                ("@reservation", reservation.ToString()), ("@prompt", Prompt.ToString()), ("@key", $"credit-usage-{batch:N}"),
                ("@hash", $"{Guid.NewGuid():N}{Guid.NewGuid():N}"), ("@at", capturedAt));
        }

        /// <summary>监控：真实运行与检测（CloroVisibilityStore），计费项 10 积分已结算；capture 流水 amount=0、无 actor。</summary>
        public async Task MonitoringCaptureAsync(DateTime capturedAt)
        {
            var runs = new CloroVisibilityStore(new MySqlDatabase(ConnectionString));
            var run = await runs.CreateRunAsync(new CreateCloroVisibilityRunCommand(Tenant, Site, [Prompt],
                [new CloroVisibilityTarget("cloro", "chatgpt", "en-US", "US")], $"credit-usage-{Guid.NewGuid():N}", "credit-usage-test"));
            var check = Assert.Single((await runs.GetChecksAsync(Tenant, run.RunId, null, 50, null, null)).Items);
            var reservation = Guid.NewGuid();
            await ExecAsync("""
                INSERT INTO monitoring_credit_reservations (id,tenant_id,site_id,run_id,unit_price,estimated_credits,held_credits,captured_credits,released_credits,status,created_at,captured_at)
                VALUES (UUID_TO_BIN(@reservation),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@run),10,10,0,10,0,'captured',@at,@at);
                INSERT INTO monitoring_credit_items (id,tenant_id,site_id,reservation_id,run_id,check_id,amount,status,created_at,captured_at)
                VALUES (UUID_TO_BIN(UUID()),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@reservation),UUID_TO_BIN(@run),UUID_TO_BIN(@check),10,'captured',@at,@at);
                INSERT INTO credit_ledger (id,tenant_id,entry_type,amount,balance_after,monitoring_reservation_id,monitoring_check_id,external_reference,reason,created_at)
                VALUES (UUID_TO_BIN(UUID()),UUID_TO_BIN(@tenant),'capture',0,0,UUID_TO_BIN(@reservation),UUID_TO_BIN(@check),CONCAT('monitoring:capture:',@check),'每日监控已提交',@at);
                """, ("@reservation", reservation.ToString()), ("@tenant", Tenant.ToString()), ("@site", Site.ToString()),
                ("@run", run.RunId.ToString()), ("@check", check.CheckId.ToString()), ("@at", capturedAt));
        }

        /// <summary>只删除本夹具租户的积分与内容数据；品牌、提问与运行记录留在独立测试库中待统一清理。</summary>
        public async Task CleanupAsync()
        {
            foreach (var sql in new[]
                     {
                         "DELETE FROM credit_ledger WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM monitoring_credit_items WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM monitoring_credit_reservations WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM credit_reservations WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM article_generation_tasks WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM article_generation_batches WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "UPDATE content_articles SET current_version_id=NULL WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM content_article_versions WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM content_articles WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM credit_accounts WHERE tenant_id=UUID_TO_BIN(@tenant);",
                     })
                await ExecAsync(sql, ("@tenant", Tenant.ToString()));
        }

        private async Task ExecAsync(string sql, params (string Name, object? Value)[] parameters)
        {
            await using var connection = new MySqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = new MySqlCommand(sql, connection);
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            await command.ExecuteNonQueryAsync();
        }
    }
}
