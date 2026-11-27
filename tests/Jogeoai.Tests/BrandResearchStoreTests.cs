using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace Jogeoai.Tests;

[Collection("Knowledge isolated SQL")]
public sealed class BrandResearchStoreTests
{
    [RegistrationDatabaseFact]
    public async Task PersistsContextAndEnforcesIdempotencyTenantScopeLeaseExpiryAndCancellation()
    {
        var connectionString = Environment.GetEnvironmentVariable("JOGEOAI_REGISTRATION_TEST_CONNECTION")!;
        Assert.Equal("jogeoai_registration_20260929", new MySqlConnectionStringBuilder(connectionString).Database);
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        Guid tenant, site, user;
        await using (var command = new MySqlCommand("SELECT BIN_TO_UUID(s.tenant_id), BIN_TO_UUID(s.id), BIN_TO_UUID(m.user_id) FROM sites s JOIN tenant_members m ON s.tenant_id = m.tenant_id WHERE s.status = 'active' LIMIT 1;", connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync()); tenant = Guid.Parse(reader.GetString(0)); site = Guid.Parse(reader.GetString(1)); user = Guid.Parse(reader.GetString(2));
        }
        // 本用例借用库内已有的租户与站点，无法控制它在最近 24 小时内已有多少任务，所以放开每日次数；每站点 1 个进行中任务保持默认值。
        // 额度本身的断言见 BrandResearchQuotaSqlTests（自建租户）。
        var store = new ResearchStore(new MySqlDatabase(connectionString),
            quota: Options.Create(new ResearchQuotaOptions { DailyPerTenant = int.MaxValue }));
        var request = BrandResearchProviderTests.Request with { RefreshKey = $"research-test-{Guid.NewGuid():N}" };
        var job = await store.EnqueueAsync(tenant, site, user, request);
        // 借用的站点上留下 pending / running 任务会让下一次运行的第一次入队直接得到 RESEARCH_ALREADY_ACTIVE，所以两条任务都在 finally 里取消。
        Guid? nextJobId = null;
        try
        {
            Assert.Equal(job.JobId, (await store.EnqueueAsync(tenant, site, user, request)).JobId);
            Assert.Equal(409, (await Assert.ThrowsAsync<CollectorStoreException>(() => store.EnqueueAsync(tenant, site, user, request with { Market = "US" }))).StatusCode);
            Assert.Null(await store.GetAsync(Guid.NewGuid(), site, job.JobId));
            Assert.Null(await store.GetAsync(tenant, Guid.NewGuid(), job.JobId));
            // 任务仍在等待：换一个 refreshKey 的新请求先被站点额度拒绝（早于 reuseJobId 校验），并带回现有任务。
            Assert.Equal(job.JobId, (await Assert.ThrowsAsync<ResearchAlreadyActiveException>(() => store.EnqueueAsync(tenant, site, user,
                request with { RefreshKey = Guid.NewGuid().ToString(), ReuseJobId = Guid.NewGuid() }))).JobId);
            var lease = await store.LeaseAsync("lease-test", TimeSpan.FromMinutes(3));
            Assert.NotNull(lease); Assert.Equal(job.JobId, lease.JobId);
            var context = BrandResearchProviderTests.Context() with { Phase = "profiling" };
            Assert.True(await store.SaveProgressAsync(job.JobId, lease.LeaseOwner, context));
            Assert.False(await store.RenewLeaseAsync(job.JobId, "other-owner", TimeSpan.FromMinutes(3)));
            Assert.True(await store.RenewLeaseAsync(job.JobId, lease.LeaseOwner, TimeSpan.FromMinutes(3)));
            var saved = await store.GetAsync(tenant, site, job.JobId);
            Assert.Equal(context.Evidence[0], saved!.Research!.Evidence[0]);
            await using (var command = new MySqlCommand("UPDATE brand_research_jobs SET lease_until = UTC_TIMESTAMP(6) - INTERVAL 1 SECOND WHERE id = UUID_TO_BIN(@id);", connection))
            { command.Parameters.AddWithValue("@id", job.JobId.ToString()); await command.ExecuteNonQueryAsync(); }
            Assert.False(await store.RenewLeaseAsync(job.JobId, lease.LeaseOwner, TimeSpan.FromMinutes(3)));
            var result = new ResearchProviderResult(new("测试品牌", [], [], [], [new("管道摄像头如何选？", [], false)], "test", "test"), "test", "test", context with { Phase = "done" });
            await store.CompleteAsync(job.JobId, lease.LeaseOwner, result);
            Assert.Equal(BrandResearchJobStatus.Running, (await store.GetAsync(tenant, site, job.JobId))!.Status);
            var newLease = await store.LeaseAsync("lease-test", TimeSpan.FromMinutes(3));
            Assert.Equal(job.JobId, newLease!.JobId); Assert.NotEqual(lease.LeaseOwner, newLease.LeaseOwner);
            Assert.False(await store.SaveProgressAsync(job.JobId, lease.LeaseOwner, context));
            await store.CompleteAsync(job.JobId, newLease.LeaseOwner, result);
            Assert.Equal(BrandResearchJobStatus.Done, (await store.GetAsync(tenant, site, job.JobId))!.Status);
            // 上一条已完成、站点没有进行中的任务时，才会走到 reuseJobId 校验。
            Assert.Equal(404, (await Assert.ThrowsAsync<CollectorStoreException>(() => store.EnqueueAsync(tenant, site, user,
                request with { RefreshKey = Guid.NewGuid().ToString(), ReuseJobId = Guid.NewGuid() }))).StatusCode);
            var next = await store.EnqueueAsync(tenant, site, user, request with
            { RefreshKey = Guid.NewGuid().ToString(), ReuseJobId = job.JobId, BusinessDescription = "只提供排水管摄像头", Feedback = [new("旧问题", "generic")] });
            nextJobId = next.JobId;
            var nextResult = (await store.GetAsync(tenant, site, next.JobId))!;
            Assert.Contains("管道摄像头如何选？", nextResult.Research!.ExcludedQuestions);
            Assert.Equal("只提供排水管摄像头", nextResult.Research.BusinessDescription);
            Assert.Null(nextResult.Research.Profile);
            Assert.Equal("user", nextResult.Research.Evidence.Last().Id);
            var nextLease = await store.LeaseAsync("lease-test", TimeSpan.FromMinutes(3));
            Assert.Equal(next.JobId, nextLease!.JobId);
            Assert.True(await store.CancelAsync(tenant, site, next.JobId));
            Assert.False(await store.RenewLeaseAsync(next.JobId, nextLease.LeaseOwner, TimeSpan.FromMinutes(3)));
            Assert.False(await store.SaveProgressAsync(next.JobId, nextLease.LeaseOwner, context));
            await store.CompleteAsync(next.JobId, nextLease.LeaseOwner, result);
            Assert.Equal(BrandResearchJobStatus.Cancelled, (await store.GetAsync(tenant, site, next.JobId))!.Status);
        }
        finally
        {
            await store.CancelAsync(tenant, site, job.JobId);
            if (nextJobId is { } leftover) await store.CancelAsync(tenant, site, leftover);
        }
    }

    /// <summary>
    /// 入队时把品牌知识条目冻结为 knowledge: 证据：无官网也无业务介绍时只要有条目就能入队；条目编辑后复用上一轮任务会替换旧条目证据。
    /// 只连接 JOGEOAI_KNOWLEDGE_TEST_CONNECTION 指向的独立库，自建随机租户并在结束时只删除本租户的行。
    /// </summary>
    [BrandKnowledgeMySqlFact]
    public async Task EnqueueFreezesKnowledgeItemsAsEvidenceAndAllowsWebsitelessBrands()
    {
        var connectionString = Environment.GetEnvironmentVariable("JOGEOAI_KNOWLEDGE_TEST_CONNECTION")!;
        var builder = new MySqlConnectionStringBuilder(connectionString);
        Assert.False(string.IsNullOrWhiteSpace(builder.Database), "必须显式指定独立测试库。");
        Assert.NotEqual("jogeoai", builder.Database, StringComparer.OrdinalIgnoreCase);
        Guid tenant = Guid.NewGuid(), site = Guid.NewGuid(), emptySite = Guid.NewGuid(), user = Guid.NewGuid();
        async Task Exec(string sql)
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new MySqlCommand(sql, connection);
            command.Parameters.AddWithValue("@tenant", tenant.ToString()); command.Parameters.AddWithValue("@site", site.ToString());
            command.Parameters.AddWithValue("@empty", emptySite.ToString()); command.Parameters.AddWithValue("@user", user.ToString());
            command.Parameters.AddWithValue("@email", $"research-knowledge-{user:N}@example.test");
            await command.ExecuteNonQueryAsync();
        }
        await Exec("""
            INSERT INTO tenants (id, name, status, created_at, updated_at)
            VALUES (UUID_TO_BIN(@tenant), 'research knowledge fixture', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
            INSERT INTO auth_users (id, email, password_hash, status, created_at, updated_at)
            VALUES (UUID_TO_BIN(@user), @email, 'not-a-login', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
            INSERT INTO sites (id, tenant_id, host, display_name, status, created_at, updated_at)
            VALUES (UUID_TO_BIN(@site), UUID_TO_BIN(@tenant), NULL, 'Research knowledge fixture', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)),
                   (UUID_TO_BIN(@empty), UUID_TO_BIN(@tenant), NULL, 'Research knowledge empty fixture', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
            """);
        try
        {
            var database = new MySqlDatabase(connectionString);
            var items = new BrandKnowledgeItemStore(database);
            var store = new ResearchStore(database, items);
            var created = await items.CreateManyAsync(tenant, site, user,
            [
                new(new(BrandKnowledgeDimension.Faq, "支持上门吗？", "支持一线城市上门检测。")),
                new(new(BrandKnowledgeDimension.BrandIntro, "公司介绍", "检测品牌研发工业检测设备与管道摄像头。")),
            ], BrandKnowledgeItemSource.Manual);
            var faq = created[0]; var intro = created[1];
            var request = new StartBrandResearchRequest(null, "检测品牌", 10, RefreshKey: $"research-knowledge-{Guid.NewGuid():N}");

            var first = await store.EnqueueAsync(tenant, site, user, request);
            var context = (await store.GetAsync(tenant, site, first.JobId))!.Research!;
            Assert.Null(context.Website);
            Assert.Null(context.BusinessDescription);
            Assert.Equal(ResearchContext.CurrentStrategy, context.StrategyVersion);
            Assert.Equal([$"knowledge:{intro.ItemId:N}", $"knowledge:{faq.ItemId:N}"], context.Evidence.Select(e => e.Id));
            var introEvidence = context.Evidence[0];
            Assert.Null(introEvidence.Url);
            Assert.Equal("品牌知识·品牌介绍·公司介绍", introEvidence.Title);
            Assert.Equal("公司介绍\n检测品牌研发工业检测设备与管道摄像头。", introEvidence.Excerpt);
            Assert.Equal(ResearchContextRules.Hash(introEvidence.Excerpt), introEvidence.ContentHash);
            Assert.Equal(intro.UpdatedAt, introEvidence.FetchedAt);

            // 没有条目的品牌仍按原规则要求官网或业务介绍。
            await Assert.ThrowsAsync<ArgumentException>(() => store.EnqueueAsync(tenant, emptySite, user,
                request with { RefreshKey = Guid.NewGuid().ToString() }));

            var edited = await items.UpdateAsync(tenant, site, intro.ItemId, user,
                new BrandKnowledgeItemUpdate(BrandKnowledgeDimension.BrandIntro, "公司介绍", "检测品牌只做排水管道摄像头。", intro.Version));
            await items.DeleteAsync(tenant, site, faq.ItemId, user);
            // 每个站点同时只允许一个进行中的任务：先取消上一轮，已取消的任务仍可作为 reuseJobId 的来源。
            Assert.True(await store.CancelAsync(tenant, site, first.JobId));
            var next = await store.EnqueueAsync(tenant, site, user, request with { RefreshKey = Guid.NewGuid().ToString(), ReuseJobId = first.JobId });
            var nextContext = (await store.GetAsync(tenant, site, next.JobId))!.Research!;
            var nextEvidence = Assert.Single(nextContext.Evidence);
            Assert.Equal($"knowledge:{intro.ItemId:N}", nextEvidence.Id);
            Assert.Contains("只做排水管道摄像头", nextEvidence.Excerpt, StringComparison.Ordinal);
            Assert.NotEqual(introEvidence.ContentHash, nextEvidence.ContentHash);
            Assert.Equal(edited.UpdatedAt, nextEvidence.FetchedAt);
            Assert.Null(nextContext.Profile);
        }
        finally
        {
            foreach (var sql in new[]
                     {
                         "DELETE FROM brand_research_jobs WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM brand_knowledge_items WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM sites WHERE tenant_id=UUID_TO_BIN(@tenant) AND id IN (UUID_TO_BIN(@site), UUID_TO_BIN(@empty));",
                         "DELETE FROM auth_users WHERE id=UUID_TO_BIN(@user);",
                         "DELETE FROM tenants WHERE id=UUID_TO_BIN(@tenant);",
                     })
                await Exec(sql);
        }
    }
}
