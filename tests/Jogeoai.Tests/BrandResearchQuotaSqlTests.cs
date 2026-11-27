using System.Globalization;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using MySqlConnector;

namespace Jogeoai.Tests;

/// <summary>
/// 品牌研究额度与公平领取（C4 R10）的 SQL 行为。只连接 JOGEOAI_KNOWLEDGE_TEST_CONNECTION 指向的独立测试库（与
/// <see cref="BrandResearchStoreTests"/> 的知识条目用例同一约定；本类只用到 tenants / auth_users / sites / brand_research_jobs，
/// 需要迁移 008 与 032，使用已执行全部迁移的库即可），不调用任何 Provider。
/// 每个用例自建随机 UUID 的租户、用户与站点，结束时只删除这些租户的行，不修改库内其他行。
/// 领取是全局的：领取顺序用例把本用例任务的 created_at（以及需要的 started_at）调到 2000 年，使它们的排序值早于库内其他可领取任务
/// （做法同 <see cref="BrandKnowledgeItemStoreSqlTests"/> 的 sync_next_at）。
/// </summary>
[Collection("Knowledge isolated SQL")]
public sealed class BrandResearchQuotaSqlTests
{
    [BrandKnowledgeMySqlFact]
    public async Task SecondJobForSiteWithPendingOrRunningJobIsRejectedWithExistingJobId()
    {
        var f = Fixture.Create();
        try
        {
            var (tenant, user) = await f.AddTenantAsync();
            var site = await f.AddSiteAsync(tenant);
            var otherSite = await f.AddSiteAsync(tenant);
            var first = await f.Store.EnqueueAsync(tenant, site, user, Request());

            // pending：第二个请求被拒绝，并带回现有任务。
            var pending = await Assert.ThrowsAsync<ResearchAlreadyActiveException>(() => f.Store.EnqueueAsync(tenant, site, user, Request()));
            Assert.Equal("RESEARCH_ALREADY_ACTIVE", pending.Code);
            Assert.Equal(409, pending.StatusCode);
            Assert.Equal(first.JobId, pending.JobId);

            // reuseJobId 同样会新建任务，受同一限制（先于 reuseJobId 校验）。
            var reuse = await Assert.ThrowsAsync<ResearchAlreadyActiveException>(() =>
                f.Store.EnqueueAsync(tenant, site, user, Request() with { ReuseJobId = first.JobId }));
            Assert.Equal(first.JobId, reuse.JobId);

            // running：同样被拒绝。
            await f.ExecAsync("""
                UPDATE brand_research_jobs SET status='running', lease_owner='quota-test',
                    lease_until=UTC_TIMESTAMP(6) + INTERVAL 3 MINUTE, started_at=UTC_TIMESTAMP(6)
                WHERE id=UUID_TO_BIN(@id);
                """, ("@id", first.JobId.ToString()));
            var running = await Assert.ThrowsAsync<ResearchAlreadyActiveException>(() => f.Store.EnqueueAsync(tenant, site, user, Request()));
            Assert.Equal(first.JobId, running.JobId);
            Assert.Equal(1, await f.CountSiteJobsAsync(tenant, site));

            // 限制按站点计算：同一租户的另一个站点不受影响。
            var other = await f.Store.EnqueueAsync(tenant, otherSite, user, Request());
            Assert.NotEqual(first.JobId, other.JobId);
        }
        finally { await f.CleanupAsync(); }
    }

    [BrandKnowledgeMySqlFact]
    public async Task DoneFailedAndCancelledJobsDoNotBlockTheSite()
    {
        var f = Fixture.Create();
        try
        {
            var (tenant, user) = await f.AddTenantAsync();
            var site = await f.AddSiteAsync(tenant);

            var done = await f.Store.EnqueueAsync(tenant, site, user, Request());
            await f.SetStatusAsync(done.JobId, "done");
            var failed = await f.Store.EnqueueAsync(tenant, site, user, Request());
            await f.SetStatusAsync(failed.JobId, "failed");
            // 取消走真实的取消路径：它必须释放站点名额。
            var cancelled = await f.Store.EnqueueAsync(tenant, site, user, Request());
            Assert.True(await f.Store.CancelAsync(tenant, site, cancelled.JobId));
            var current = await f.Store.EnqueueAsync(tenant, site, user, Request());

            Assert.Equal(4, new[] { done.JobId, failed.JobId, cancelled.JobId, current.JobId }.Distinct().Count());
            Assert.Equal(4, await f.CountSiteJobsAsync(tenant, site));
            // 只有最后一条在进行中，冲突时带回的是它。
            var rejected = await Assert.ThrowsAsync<ResearchAlreadyActiveException>(() => f.Store.EnqueueAsync(tenant, site, user, Request()));
            Assert.Equal(current.JobId, rejected.JobId);
        }
        finally { await f.CleanupAsync(); }
    }

    [BrandKnowledgeMySqlFact]
    public async Task ReplayReturnsTheOriginalJobBeforeAnyQuotaCheck()
    {
        var f = Fixture.Create();
        try
        {
            var (tenant, user) = await f.AddTenantAsync();
            var site = await f.AddSiteAsync(tenant);
            var request = Request() with { RefreshKey = $"quota-replay-{Guid.NewGuid():N}" };
            var first = await f.Store.EnqueueAsync(tenant, site, user, request);

            // 原任务仍在等待：相同 refreshKey 与相同内容的重放返回原任务，而不是 RESEARCH_ALREADY_ACTIVE，也不新增行。
            var replay = await f.Store.EnqueueAsync(tenant, site, user, request);
            Assert.Equal(first.JobId, replay.JobId);
            Assert.Equal(BrandResearchJobStatus.Pending, replay.Status);
            Assert.Equal(1, await f.CountTenantJobsAsync(tenant));

            // 相同 refreshKey、不同内容仍是幂等冲突。
            var conflict = await Assert.ThrowsAsync<CollectorStoreException>(() => f.Store.EnqueueAsync(tenant, site, user, request with { Market = "US" }));
            Assert.Equal("RESEARCH_IDEMPOTENCY_CONFLICT", conflict.Code);
            Assert.Equal(1, await f.CountTenantJobsAsync(tenant));
        }
        finally { await f.CleanupAsync(); }
    }

    [BrandKnowledgeMySqlFact]
    public async Task TwentyFirstJobInRollingDayIsRejectedUntilAJobLeavesTheWindow()
    {
        var f = Fixture.Create();
        try
        {
            var (tenant, user) = await f.AddTenantAsync();
            var site = await f.AddSiteAsync(tenant);
            var (otherTenant, otherUser) = await f.AddTenantAsync();
            var otherSite = await f.AddSiteAsync(otherTenant);

            var lastRequest = Request();
            var last = default(BrandResearchAccepted)!;
            for (var index = 0; index < 20; index++)
            {
                lastRequest = Request() with { RefreshKey = $"quota-daily-{Guid.NewGuid():N}" };
                last = await f.Store.EnqueueAsync(tenant, site, user, lastRequest);
                // 取消后释放站点名额；已取消的任务仍计入 24 小时内的次数。
                Assert.True(await f.Store.CancelAsync(tenant, site, last.JobId));
            }
            Assert.Equal(20, await f.CountTenantJobsAsync(tenant));

            var limit = await Assert.ThrowsAsync<CollectorStoreException>(() => f.Store.EnqueueAsync(tenant, site, user, Request()));
            Assert.Equal("RESEARCH_DAILY_LIMIT", limit.Code);
            Assert.Equal(429, limit.StatusCode);

            // 达到上限后，重放仍返回原任务：既不被拦截，也不新增行。
            Assert.Equal(last.JobId, (await f.Store.EnqueueAsync(tenant, site, user, lastRequest)).JobId);
            Assert.Equal(20, await f.CountTenantJobsAsync(tenant));

            // 次数按租户计算：另一个租户不受影响。
            await f.Store.EnqueueAsync(otherTenant, otherSite, otherUser, Request());

            // 把一条记录移到 25 小时前，窗口内剩 19 条，可以再入队一次；之后窗口内又是 20 条。
            await f.ExecAsync("UPDATE brand_research_jobs SET created_at = created_at - INTERVAL 25 HOUR WHERE id=UUID_TO_BIN(@id);",
                ("@id", last.JobId.ToString()));
            var accepted = await f.Store.EnqueueAsync(tenant, site, user, Request());
            Assert.Equal(21, await f.CountTenantJobsAsync(tenant));
            Assert.True(await f.Store.CancelAsync(tenant, site, accepted.JobId));
            Assert.Equal("RESEARCH_DAILY_LIMIT",
                (await Assert.ThrowsAsync<CollectorStoreException>(() => f.Store.EnqueueAsync(tenant, site, user, Request()))).Code);
        }
        finally { await f.CleanupAsync(); }
    }

    [BrandKnowledgeMySqlFact]
    public async Task ConcurrentEnqueuesForOneSiteInsertExactlyOneJob()
    {
        var f = Fixture.Create();
        // 整个用例的等待上限：不依赖连接串里的命令超时或服务器的 innodb_lock_wait_timeout，预期不成立时失败而不是挂住。
        var limit = TimeSpan.FromSeconds(60);
        Task<(Guid? Job, ResearchAlreadyActiveException? Rejected)>[] attempts = [];
        try
        {
            var (tenant, user) = await f.AddTenantAsync();
            var site = await f.AddSiteAsync(tenant);

            async Task<(Guid? Job, ResearchAlreadyActiveException? Rejected)> AttemptAsync()
            {
                try { return ((await f.Store.EnqueueAsync(tenant, site, user, Request())).JobId, null); }
                catch (ResearchAlreadyActiveException rejected) { return (null, rejected); }
            }

            // 先占住站点行锁，让两个请求都排在这把锁上，再放行：检查与插入在同一把锁内，后到的请求必须看到先到的任务。
            // 1 秒是给两个请求各自建立连接、开启事务并走到站点行锁的时间；这里只能断言“都还没有完成”，
            // 无法断言“都已经在等锁”（那需要 performance_schema 权限）。某个请求来得更晚时结果相同，只是竞争没有被真正触发。
            await using (var guard = f.OpenConnection())
            {
                await guard.OpenAsync();
                await using var guardTransaction = await guard.BeginTransactionAsync();
                await using (var lockSite = new MySqlCommand(
                                 "SELECT id FROM sites WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@site) FOR UPDATE;", guard, guardTransaction))
                {
                    lockSite.Parameters.AddWithValue("@tenant", tenant.ToString());
                    lockSite.Parameters.AddWithValue("@site", site.ToString());
                    Assert.NotNull(await lockSite.ExecuteScalarAsync());
                }
                attempts = [Task.Run(() => AttemptAsync()), Task.Run(() => AttemptAsync())];
                await Task.Delay(TimeSpan.FromSeconds(1));
                Assert.All(attempts, attempt => Assert.False(attempt.IsCompleted));
                await guardTransaction.CommitAsync();
            }

            var results = await Task.WhenAll(attempts).WaitAsync(limit);
            var winner = Assert.Single(results, result => result.Job is not null);
            var loser = Assert.Single(results, result => result.Rejected is not null);
            Assert.Equal(winner.Job, loser.Rejected!.JobId);
            Assert.Equal(1, await f.CountSiteJobsAsync(tenant, site));
        }
        finally
        {
            // 断言失败时两个请求可能还在进行：等它们结束（或超时）再清理，避免清理之后又插入一行、或清理被它们持有的锁挡住。
            try { await Task.WhenAll(attempts).WaitAsync(limit); }
            catch (Exception) { /* 用例本身的失败已在上面抛出；这里只负责等待。 */ }
            await f.CleanupAsync();
        }
    }

    /// <summary>
    /// 租户 A 有三条排队任务，租户 B 有一条更晚的任务：A 被服务一次后，下一次领取的是 B（旧的全局先进先出会继续领取 A）。
    /// 排序值：从未被服务过的租户用任务自己的 created_at，被服务过的租户用最近一次 started_at。
    /// 只做这两次领取：两个租户都是新建的，任务的 created_at 在 2000 年，排序值早于库内其他任何可领取任务，所以这两次领取不受库内其他任务影响；
    /// 第三次领取时 A 的排序值已是“刚才”，会让位给库内任何排序值更早的任务，除非队列里没有别的任务，因此不在这里断言。
    /// 前提：没有 Worker 连着这个测试库；库内没有其他排序值在 2000 年或更早的可领取任务（例如上一次运行中途中断留下的夹具行）。
    /// </summary>
    [BrandKnowledgeMySqlFact]
    public async Task ClaimServesTheLeastRecentlyServedTenantFirst()
    {
        var f = Fixture.Create();
        try
        {
            var (tenantA, userA) = await f.AddTenantAsync();
            var (tenantB, userB) = await f.AddTenantAsync();
            // 每个站点同时只能有一个进行中的任务，所以 A 的三条任务分属三个站点。
            var a1 = await f.Store.EnqueueAsync(tenantA, await f.AddSiteAsync(tenantA), userA, Request());
            var a2 = await f.Store.EnqueueAsync(tenantA, await f.AddSiteAsync(tenantA), userA, Request());
            var a3 = await f.Store.EnqueueAsync(tenantA, await f.AddSiteAsync(tenantA), userA, Request());
            var b1 = await f.Store.EnqueueAsync(tenantB, await f.AddSiteAsync(tenantB), userB, Request());
            var order = new[] { a1.JobId, a2.JobId, a3.JobId, b1.JobId };
            for (var index = 0; index < order.Length; index++)
                await f.ExecAsync("UPDATE brand_research_jobs SET created_at = TIMESTAMP('2000-01-01 00:00:00') + INTERVAL @seconds SECOND WHERE id=UUID_TO_BIN(@id);",
                    ("@seconds", index + 1), ("@id", order[index].ToString()));

            // 两个租户都从未被服务，排序值就是各任务的 created_at：a1 最早，先领取它，并写入 started_at。
            var first = await f.Store.LeaseAsync("quota-claim-test", TimeSpan.FromMinutes(3));
            Assert.NotNull(first);
            Assert.Equal(a1.JobId, first.JobId);
            Assert.Equal(tenantA, first.TenantId);
            Assert.Equal(1, await f.ScalarAsync(
                "SELECT COUNT(*) FROM brand_research_jobs WHERE id=UUID_TO_BIN(@id) AND status='running' AND started_at IS NOT NULL;",
                ("@id", a1.JobId.ToString())));

            // A 刚被服务过，剩下两条的排序值变成“刚才”；B 从未被服务，排序值仍是它在 2000 年的 created_at。
            // B 的任务比 A 剩下的两条都晚创建，但下一次领取的是 B。
            var second = await f.Store.LeaseAsync("quota-claim-test", TimeSpan.FromMinutes(3));
            Assert.NotNull(second);
            Assert.Equal(b1.JobId, second.JobId);
            Assert.Equal(tenantB, second.TenantId);

            // A 剩下的两条仍在排队，没有被这两次领取改动。
            Assert.Equal(2, await f.ScalarAsync(
                "SELECT COUNT(*) FROM brand_research_jobs WHERE tenant_id=UUID_TO_BIN(@tenant) AND status='pending' AND started_at IS NULL;",
                ("@tenant", tenantA.ToString())));
        }
        finally { await f.CleanupAsync(); }
    }

    /// <summary>
    /// 新租户不插队：租户 C 在 T0 被服务过；全新的租户 D 在 T0 之后入队。两者都在排队时先领取 C，再领取 D。
    /// 旧规则（从未被服务过的租户排最前）会先领取 D；这里 C 排队任务的 created_at 还晚于 D 的任务，所以全局先进先出同样会先领取 D，
    /// 只有“C 的排序值是上次服务时间 T0、D 的排序值是任务创建时间”才会先领取 C。
    /// 三个时间都放在 2000 年（T0 = 00:00:10，D 的任务 00:00:20，C 的排队任务 00:00:30），排序值早于库内其他任何可领取任务。
    /// 前提同上：没有 Worker 连着这个测试库；库内没有其他排序值在 2000 年或更早的可领取任务；数据库时钟晚于 2000 年。
    /// </summary>
    [BrandKnowledgeMySqlFact]
    public async Task NewTenantQueuesBehindATenantServedBeforeItsJobWasCreated()
    {
        var f = Fixture.Create();
        try
        {
            var (tenantC, userC) = await f.AddTenantAsync();
            var (tenantD, userD) = await f.AddTenantAsync();
            var siteC = await f.AddSiteAsync(tenantC);

            // C 的历史：一条在 T0 被领取并已完成的任务。完成的任务不占站点名额，也不可领取。
            var served = await f.Store.EnqueueAsync(tenantC, siteC, userC, Request());
            await f.ExecAsync("""
                UPDATE brand_research_jobs SET status='done', attempt_count=1,
                    created_at = TIMESTAMP('2000-01-01 00:00:05'), started_at = TIMESTAMP('2000-01-01 00:00:10'),
                    completed_at = TIMESTAMP('2000-01-01 00:00:15')
                WHERE id=UUID_TO_BIN(@id);
                """, ("@id", served.JobId.ToString()));

            // D 是全新的租户，任务创建于 T0 之后；C 的新任务创建得比 D 的还晚。
            var d1 = await f.Store.EnqueueAsync(tenantD, await f.AddSiteAsync(tenantD), userD, Request());
            var c2 = await f.Store.EnqueueAsync(tenantC, siteC, userC, Request());
            await f.ExecAsync("UPDATE brand_research_jobs SET created_at = TIMESTAMP('2000-01-01 00:00:20') WHERE id=UUID_TO_BIN(@id);",
                ("@id", d1.JobId.ToString()));
            await f.ExecAsync("UPDATE brand_research_jobs SET created_at = TIMESTAMP('2000-01-01 00:00:30') WHERE id=UUID_TO_BIN(@id);",
                ("@id", c2.JobId.ToString()));

            // C 的排序值是 T0（00:00:10），早于 D 的 00:00:20：先领取 C。
            var first = await f.Store.LeaseAsync("quota-claim-test", TimeSpan.FromMinutes(3));
            Assert.NotNull(first);
            Assert.Equal(c2.JobId, first.JobId);
            Assert.Equal(tenantC, first.TenantId);

            // C 刚被服务，D 的排序值仍是 00:00:20，是库内最早的：接着领取 D。D 没有被饿死，只是不再插队。
            var second = await f.Store.LeaseAsync("quota-claim-test", TimeSpan.FromMinutes(3));
            Assert.NotNull(second);
            Assert.Equal(d1.JobId, second.JobId);
            Assert.Equal(tenantD, second.TenantId);
        }
        finally { await f.CleanupAsync(); }
    }

    private static StartBrandResearchRequest Request() => new("https://example.com/", "额度测试品牌", 10);

    private sealed class Fixture
    {
        private readonly string connectionString;
        private readonly List<(Guid Tenant, Guid User)> tenants = [];

        private Fixture(string connection)
        {
            connectionString = connection;
            Store = new ResearchStore(new MySqlDatabase(connection));
        }

        /// <summary>使用默认额度（每站点 1 个进行中任务、每租户滚动 24 小时 20 次）且不读取品牌知识条目的 Store。</summary>
        public ResearchStore Store { get; }

        public static Fixture Create()
        {
            var value = Environment.GetEnvironmentVariable("JOGEOAI_KNOWLEDGE_TEST_CONNECTION");
            Assert.False(string.IsNullOrWhiteSpace(value), "未设置 JOGEOAI_KNOWLEDGE_TEST_CONNECTION；SQL 测试不会静默通过。");
            var builder = new MySqlConnectionStringBuilder(value!);
            Assert.False(string.IsNullOrWhiteSpace(builder.Database), "必须显式指定独立测试库。");
            Assert.NotEqual("jogeoai", builder.Database, StringComparer.OrdinalIgnoreCase);
            return new Fixture(value!);
        }

        public MySqlConnection OpenConnection() => new(connectionString);

        public async Task<(Guid Tenant, Guid User)> AddTenantAsync()
        {
            Guid tenant = Guid.NewGuid(), user = Guid.NewGuid();
            // 先登记再插入：插入中途失败时清理仍会覆盖这两个标识。
            tenants.Add((tenant, user));
            await ExecAsync("""
                INSERT INTO tenants (id, name, status, created_at, updated_at)
                VALUES (UUID_TO_BIN(@tenant), 'research quota fixture', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                INSERT INTO auth_users (id, email, password_hash, status, created_at, updated_at)
                VALUES (UUID_TO_BIN(@user), @email, 'not-a-login', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                """, ("@tenant", tenant.ToString()), ("@user", user.ToString()), ("@email", $"research-quota-{user:N}@example.test"));
            return (tenant, user);
        }

        public async Task<Guid> AddSiteAsync(Guid tenant)
        {
            var site = Guid.NewGuid();
            await ExecAsync("""
                INSERT INTO sites (id, tenant_id, host, display_name, status, created_at, updated_at)
                VALUES (UUID_TO_BIN(@site), UUID_TO_BIN(@tenant), NULL, 'Research quota fixture', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                """, ("@site", site.ToString()), ("@tenant", tenant.ToString()));
            return site;
        }

        public Task SetStatusAsync(Guid jobId, string status)
            => ExecAsync("""
                UPDATE brand_research_jobs SET status=@status, lease_owner=NULL, lease_until=NULL,
                    completed_at=UTC_TIMESTAMP(6), updated_at=UTC_TIMESTAMP(6)
                WHERE id=UUID_TO_BIN(@id);
                """, ("@status", status), ("@id", jobId.ToString()));

        public Task<long> CountSiteJobsAsync(Guid tenant, Guid site)
            => ScalarAsync("SELECT COUNT(*) FROM brand_research_jobs WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site);",
                ("@tenant", tenant.ToString()), ("@site", site.ToString()));

        public Task<long> CountTenantJobsAsync(Guid tenant)
            => ScalarAsync("SELECT COUNT(*) FROM brand_research_jobs WHERE tenant_id=UUID_TO_BIN(@tenant);", ("@tenant", tenant.ToString()));

        /// <summary>只删除本夹具创建的租户、用户及其站点和研究任务；不触碰库内其他行。</summary>
        public async Task CleanupAsync()
        {
            foreach (var (tenant, user) in tenants)
                foreach (var sql in new[]
                         {
                             "DELETE FROM brand_research_jobs WHERE tenant_id=UUID_TO_BIN(@tenant);",
                             "DELETE FROM sites WHERE tenant_id=UUID_TO_BIN(@tenant);",
                             "DELETE FROM auth_users WHERE id=UUID_TO_BIN(@user);",
                             "DELETE FROM tenants WHERE id=UUID_TO_BIN(@tenant);",
                         })
                    await ExecAsync(sql, ("@tenant", tenant.ToString()), ("@user", user.ToString()));
        }

        public async Task<int> ExecAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new MySqlCommand(sql, connection);
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            return await command.ExecuteNonQueryAsync();
        }

        public async Task<long> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new MySqlCommand(sql, connection);
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }
    }
}
