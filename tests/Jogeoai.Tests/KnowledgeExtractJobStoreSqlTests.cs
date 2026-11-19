using System.Globalization;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Microsoft.Extensions.Configuration;
using MySqlConnector;

namespace Jogeoai.Tests;

/// <summary>
/// 网址识别任务 SQL 测试：只连接已执行 001–043 的独立测试库（<see cref="BrandKnowledgeMySqlFactAttribute"/> 未配置时跳过，绝不使用业务库 jogeoai）。
/// 每个用例自建随机 UUID 的租户、用户与品牌，结束时只删除本用例租户的数据；领取前把本用例任务的 next_attempt_at 调到 2000 年并断言领取到的是它。
/// 覆盖原子保存（并发只一个成功、超限/重名不写入、过期 410）与产品识别、网址识别双向共用的每日额度。
/// </summary>
[Collection("Knowledge isolated SQL")]
public sealed class KnowledgeExtractJobStoreSqlTests
{
    private const string Url = "https://Example.test/about?utm_source=x#top";

    [Fact]
    public void UrlValidationRetryBackoffAndCandidateBudgetAreDeterministic()
    {
        var (url, hash) = KnowledgeExtractJobStore.NormalizeUrl("  https://Example.test/about?utm_source=x#top ");
        Assert.Equal("https://example.test/about", url);
        Assert.Equal(64, hash.Length);
        foreach (var invalid in new[] { "", "ftp://example.test/a", "not a url", "https://user:pw@example.test/", "https://example.test/" + new string('a', 2000) })
        {
            var exception = Assert.Throws<CollectorStoreException>(() => KnowledgeExtractJobStore.NormalizeUrl(invalid));
            Assert.Equal("KNOWLEDGE_ITEM_VALIDATION", exception.Code);
            Assert.Equal(400, exception.StatusCode);
        }

        Assert.Equal(30, KnowledgeExtractJobStore.RetryDelaySeconds(1));
        Assert.Equal(60, KnowledgeExtractJobStore.RetryDelaySeconds(2));
        Assert.Equal(1920, KnowledgeExtractJobStore.RetryDelaySeconds(10));

        // 30 条满长中文候选超过 256 KiB 约束：从末尾丢弃，余下的仍可完整读回。
        var candidates = Enumerable.Range(1, 30).Select(index => new KnowledgeExtractCandidate(index.ToString(CultureInfo.InvariantCulture),
            BrandKnowledgeDimension.ProductService, new string('题', 100), new string('容', 2000), new string('据', 1000), null)).ToArray();
        var (json, dropped) = KnowledgeExtractJobStore.SerializeCandidates(candidates);
        Assert.True(dropped > 0);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(json) <= KnowledgeExtractJobStore.CandidatesJsonBudget);
        var restored = KnowledgeExtractJobStore.DeserializeCandidates(json);
        Assert.Equal(30 - dropped, restored.Count);
        Assert.Equal(candidates[0], restored[0]);
        Assert.Contains("\"product_service\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MarkResolvedOnlyAcceptsDiscardBeforeOpeningAConnection()
    {
        // 保存只走 SaveCandidatesAsync；放弃以外的取值在打开连接前就被拒绝（连接串指向不存在的端口，任何连接尝试都会失败）。
        var store = new KnowledgeExtractJobStore(new MySqlDatabase("Server=127.0.0.1;Port=1;Database=jogeoai_none;Connect Timeout=1"));
        foreach (var resolution in new[] { "saved", "done", "" })
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                store.MarkResolvedAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), resolution));
    }

    [BrandKnowledgeMySqlFact]
    public async Task OneActiveJobPerSiteWithValidationAndTenantIsolation()
    {
        var f = await Fixture.CreateAsync();
        try
        {
            var job = await f.Store.EnqueueAsync(f.Tenant, f.Site, f.User, new KnowledgeExtractRequest(Url, [BrandKnowledgeDimension.Faq]));
            Assert.Equal("pending", job.Status);
            Assert.Equal("fetch", job.Step);
            Assert.Equal("https://example.test/about", job.Url);
            Assert.Empty(job.Candidates);
            Assert.Null(job.ExpiresAt);

            var duplicate = await Assert.ThrowsAsync<CollectorStoreException>(() =>
                f.Store.EnqueueAsync(f.Tenant, f.Site, f.User, new KnowledgeExtractRequest("https://example.test/other")));
            Assert.Equal("KNOWLEDGE_EXTRACT_IN_PROGRESS", duplicate.Code);
            Assert.Equal(409, duplicate.StatusCode);

            // 同租户另一品牌不受影响；跨品牌读取返回 null。
            var other = await f.Store.EnqueueAsync(f.Tenant, f.OtherSite, f.User, new KnowledgeExtractRequest(Url));
            Assert.Null(await f.Store.GetAsync(f.Tenant, f.Site, other.JobId));
            Assert.Null(await f.Store.GetAsync(Guid.NewGuid(), f.Site, job.JobId));
            Assert.Equal(job.JobId, (await f.Store.GetCurrentAsync(f.Tenant, f.Site))!.JobId);
            Assert.Equal(other.JobId, (await f.Store.GetCurrentAsync(f.Tenant, f.OtherSite))!.JobId);

            var badUrl = await Assert.ThrowsAsync<CollectorStoreException>(() =>
                f.Store.EnqueueAsync(f.Tenant, f.Site, f.User, new KnowledgeExtractRequest("javascript:alert(1)")));
            Assert.Equal("KNOWLEDGE_ITEM_VALIDATION", badUrl.Code);
            var badDimension = await Assert.ThrowsAsync<CollectorStoreException>(() =>
                f.Store.EnqueueAsync(f.Tenant, f.Site, f.User, new KnowledgeExtractRequest(Url, [(BrandKnowledgeDimension)99])));
            Assert.Equal("KNOWLEDGE_ITEM_VALIDATION", badDimension.Code);
            var missingSite = await Assert.ThrowsAsync<CollectorStoreException>(() =>
                f.Store.EnqueueAsync(f.Tenant, Guid.NewGuid(), f.User, new KnowledgeExtractRequest(Url)));
            Assert.Equal("RESOURCE_NOT_FOUND", missingSite.Code);
            Assert.Equal(404, missingSite.StatusCode);

            // 领取时带回品牌名与限定维度。
            var lease = await f.ClaimMineAsync(job.JobId);
            Assert.Equal("Extract fixture", lease.BrandName);
            Assert.Equal([BrandKnowledgeDimension.Faq], lease.Dimensions);
            Assert.Equal(1, lease.AttemptCount);
            Assert.Equal("running", (await f.Store.GetAsync(f.Tenant, f.Site, job.JobId))!.Status);
            await f.ClaimMineAsync(other.JobId);
        }
        finally { await f.CleanupAsync(); }
    }

    [BrandKnowledgeMySqlFact]
    public async Task DailyLimitIsSharedWithProductParseJobsOnTheUtcDay()
    {
        var f = await Fixture.CreateAsync();
        try
        {
            await f.InsertProductParseJobsAsync(KnowledgeExtractJobStore.DailyLimit - 1);
            var job = await f.Store.EnqueueAsync(f.Tenant, f.Site, f.User, new KnowledgeExtractRequest(Url));
            await f.Store.CancelAsync(f.Tenant, f.Site, job.JobId);

            // 49 个产品识别 + 1 个网址识别 = 50：再发起被拒，取消的任务也计数。
            var limited = await Assert.ThrowsAsync<CollectorStoreException>(() =>
                f.Store.EnqueueAsync(f.Tenant, f.OtherSite, f.User, new KnowledgeExtractRequest(Url)));
            Assert.Equal("KNOWLEDGE_EXTRACT_RATE_LIMITED", limited.Code);
            Assert.Equal(429, limited.StatusCode);
            Assert.Equal(1, await f.ScalarAsync("SELECT COUNT(*) FROM brand_knowledge_extract_jobs WHERE tenant_id=UUID_TO_BIN(@tenant);",
                ("@tenant", f.Tenant.ToString())));

            // 前一 UTC 日的产品识别不计入今天。
            await f.ExecAsync("UPDATE knowledge_product_parse_jobs SET created_at=DATE_SUB(UTC_DATE(), INTERVAL 1 SECOND) WHERE tenant_id=UUID_TO_BIN(@tenant);",
                ("@tenant", f.Tenant.ToString()));
            var allowed = await f.Store.EnqueueAsync(f.Tenant, f.OtherSite, f.User, new KnowledgeExtractRequest(Url));
            Assert.Equal("pending", allowed.Status);
        }
        finally { await f.CleanupAsync(); }
    }

    [BrandKnowledgeMySqlFact]
    public async Task CompletedCandidatesExpireLazilyAndResolveRulesHold()
    {
        var f = await Fixture.CreateAsync();
        try
        {
            var job = await f.Store.EnqueueAsync(f.Tenant, f.Site, f.User, new KnowledgeExtractRequest(Url));
            var lease = await f.ClaimMineAsync(job.JobId);
            Assert.True(await f.Store.MarkStepAsync(lease, "model"));
            Assert.True(await f.Store.CompleteAsync(lease, Completion(Candidate("1", "成立背景"), Candidate("2", "总部在哪里？"))));

            var done = (await f.Store.GetAsync(f.Tenant, f.Site, job.JobId))!;
            Assert.Equal("done", done.Status);
            Assert.Equal("finalize", done.Step);
            Assert.Equal("关于我们", done.PageTitle);
            Assert.Equal(["1", "2"], done.Candidates.Select(candidate => candidate.CandidateId));
            Assert.Equal(BrandKnowledgeDimension.BrandIntro, done.Candidates[0].Dimension);
            Assert.Equal(TimeSpan.FromHours(24), done.ExpiresAt - done.CompletedAt);
            Assert.Equal(1, await f.ScalarAsync("""
                SELECT COUNT(*) FROM brand_knowledge_extract_jobs WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@job)
                  AND page_hash=@hash AND page_characters=1234 AND model_name='gpt-6-luna' AND lease_owner IS NULL;
                """, ("@tenant", f.Tenant.ToString()), ("@job", job.JobId.ToString()), ("@hash", new string('b', 64))));
            Assert.Equal(job.JobId, (await f.Store.GetCurrentAsync(f.Tenant, f.Site))!.JobId);

            // done 任务仍占用品牌的单任务名额。
            var busy = await Assert.ThrowsAsync<CollectorStoreException>(() =>
                f.Store.EnqueueAsync(f.Tenant, f.Site, f.User, new KnowledgeExtractRequest(Url)));
            Assert.Equal("KNOWLEDGE_EXTRACT_IN_PROGRESS", busy.Code);

            // 保存只走原子保存入口：任务置 saved，再次保存与放弃都返回 409。
            Assert.Single(await f.Store.SaveCandidatesAsync(f.Tenant, f.Site, job.JobId, f.User,
                Save(("1", BrandKnowledgeDimension.BrandIntro, "成立背景", "内容"))));
            Assert.Equal("saved", (await f.Store.GetAsync(f.Tenant, f.Site, job.JobId))!.Status);
            Assert.Equal("KNOWLEDGE_EXTRACT_NOT_READY", (await Assert.ThrowsAsync<CollectorStoreException>(() =>
                f.Store.SaveCandidatesAsync(f.Tenant, f.Site, job.JobId, f.User, Save(("2", BrandKnowledgeDimension.Faq, "总部在哪里？", "内容"))))).Code);
            var discardSaved = await Assert.ThrowsAsync<CollectorStoreException>(() => f.Store.MarkResolvedAsync(f.Tenant, f.Site, job.JobId, "discarded"));
            Assert.Equal("KNOWLEDGE_EXTRACT_NOT_READY", discardSaved.Code);
            Assert.Null(await f.Store.GetCurrentAsync(f.Tenant, f.Site));

            // 过期：读取当前任务时惰性置为 discarded，保存返回 410。
            var second = await f.Store.EnqueueAsync(f.Tenant, f.Site, f.User, new KnowledgeExtractRequest(Url));
            Assert.True(await f.Store.CompleteAsync(await f.ClaimMineAsync(second.JobId), Completion(Candidate("1", "成立背景"))));
            await f.ExpireAsync(second.JobId);
            Assert.Null(await f.Store.GetCurrentAsync(f.Tenant, f.Site));
            Assert.Equal("discarded", (await f.Store.GetAsync(f.Tenant, f.Site, second.JobId))!.Status);
            var expired = await Assert.ThrowsAsync<CollectorStoreException>(() =>
                f.Store.SaveCandidatesAsync(f.Tenant, f.Site, second.JobId, f.User, Save(("1", BrandKnowledgeDimension.BrandIntro, "成立背景", "内容"))));
            Assert.Equal("KNOWLEDGE_EXTRACT_EXPIRED", expired.Code);
            Assert.Equal(410, expired.StatusCode);
            // 已过期放弃的任务再放弃直接返回。
            Assert.Equal("discarded", (await f.Store.MarkResolvedAsync(f.Tenant, f.Site, second.JobId, "discarded")).Status);

            // 入队本身也会先惰性过期：过期的 done 任务不挡新任务。
            var third = await f.Store.EnqueueAsync(f.Tenant, f.Site, f.User, new KnowledgeExtractRequest(Url));
            Assert.True(await f.Store.CompleteAsync(await f.ClaimMineAsync(third.JobId), Completion()));
            await f.ExpireAsync(third.JobId);
            var fourth = await f.Store.EnqueueAsync(f.Tenant, f.Site, f.User, new KnowledgeExtractRequest(Url));
            Assert.Equal("discarded", (await f.Store.GetAsync(f.Tenant, f.Site, third.JobId))!.Status);

            // 未完成的任务不能保存或放弃。
            Assert.Equal("KNOWLEDGE_EXTRACT_NOT_READY", (await Assert.ThrowsAsync<CollectorStoreException>(() =>
                f.Store.SaveCandidatesAsync(f.Tenant, f.Site, fourth.JobId, f.User, Save(("1", BrandKnowledgeDimension.BrandIntro, "成立背景", "内容"))))).Code);
            Assert.Equal("KNOWLEDGE_EXTRACT_NOT_READY",
                (await Assert.ThrowsAsync<CollectorStoreException>(() => f.Store.MarkResolvedAsync(f.Tenant, f.Site, fourth.JobId, "discarded"))).Code);
            Assert.Equal("KNOWLEDGE_EXTRACT_NOT_FOUND",
                (await Assert.ThrowsAsync<CollectorStoreException>(() => f.Store.MarkResolvedAsync(f.Tenant, f.Site, Guid.NewGuid(), "discarded"))).Code);

            // 完成后用户放弃：任务置 discarded，品牌名额释放。
            Assert.True(await f.Store.CompleteAsync(await f.ClaimMineAsync(fourth.JobId), Completion(Candidate("1", "成立背景"))));
            Assert.Equal("discarded", (await f.Store.MarkResolvedAsync(f.Tenant, f.Site, fourth.JobId, "discarded")).Status);
            Assert.Null(await f.Store.GetCurrentAsync(f.Tenant, f.Site));
            await f.Store.EnqueueAsync(f.Tenant, f.Site, f.User, new KnowledgeExtractRequest(Url));
        }
        finally { await f.CleanupAsync(); }
    }

    [BrandKnowledgeMySqlFact]
    public async Task CancelStopsPendingAndRunningJobsAndHeartbeatReportsIt()
    {
        var f = await Fixture.CreateAsync();
        try
        {
            var pending = await f.Store.EnqueueAsync(f.Tenant, f.Site, f.User, new KnowledgeExtractRequest(Url));
            Assert.Equal("cancelled", (await f.Store.CancelAsync(f.Tenant, f.Site, pending.JobId)).Status);
            Assert.Equal("cancelled", (await f.Store.CancelAsync(f.Tenant, f.Site, pending.JobId)).Status);
            Assert.Null(await f.Store.GetCurrentAsync(f.Tenant, f.Site));
            Assert.Equal("KNOWLEDGE_EXTRACT_NOT_FOUND",
                (await Assert.ThrowsAsync<CollectorStoreException>(() => f.Store.CancelAsync(f.Tenant, f.OtherSite, pending.JobId))).Code);

            // running 任务被取消后：心跳返回 Cancelled，阶段、完成与失败都不再写入。
            var running = await f.Store.EnqueueAsync(f.Tenant, f.Site, f.User, new KnowledgeExtractRequest(Url));
            var lease = await f.ClaimMineAsync(running.JobId);
            Assert.Equal(KnowledgeExtractHeartbeat.Alive, await f.Store.HeartbeatAsync(lease, TimeSpan.FromMinutes(2)));
            Assert.Equal("cancelled", (await f.Store.CancelAsync(f.Tenant, f.Site, running.JobId)).Status);
            Assert.Equal(KnowledgeExtractHeartbeat.Cancelled, await f.Store.HeartbeatAsync(lease, TimeSpan.FromMinutes(2)));
            Assert.False(await f.Store.MarkStepAsync(lease, "model"));
            Assert.False(await f.Store.CompleteAsync(lease, Completion(Candidate("1", "成立背景"))));
            Assert.False(await f.Store.FailAsync(lease, "KNOWLEDGE_EXTRACT_RETRYABLE", "x", true));
            var after = (await f.Store.GetAsync(f.Tenant, f.Site, running.JobId))!;
            Assert.Equal("cancelled", after.Status);
            Assert.Empty(after.Candidates);

            // 租约被其他 Worker 接管：心跳返回 LeaseLost。
            var fenced = await f.Store.EnqueueAsync(f.Tenant, f.Site, f.User, new KnowledgeExtractRequest(Url));
            var fencedLease = await f.ClaimMineAsync(fenced.JobId);
            await f.ExecAsync("UPDATE brand_knowledge_extract_jobs SET lease_owner='someone-else' WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@job);",
                ("@tenant", f.Tenant.ToString()), ("@job", fenced.JobId.ToString()));
            Assert.Equal(KnowledgeExtractHeartbeat.LeaseLost, await f.Store.HeartbeatAsync(fencedLease, TimeSpan.FromMinutes(2)));
            Assert.False(await f.Store.CompleteAsync(fencedLease, Completion()));

            // 已完成的任务不能取消。
            await f.ExecAsync("UPDATE brand_knowledge_extract_jobs SET lease_owner=@owner WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@job);",
                ("@owner", fencedLease.LeaseOwner), ("@tenant", f.Tenant.ToString()), ("@job", fenced.JobId.ToString()));
            Assert.True(await f.Store.CompleteAsync(fencedLease, Completion()));
            var conflict = await Assert.ThrowsAsync<CollectorStoreException>(() => f.Store.CancelAsync(f.Tenant, f.Site, fenced.JobId));
            Assert.Equal("KNOWLEDGE_EXTRACT_STATE_CONFLICT", conflict.Code);
            Assert.Equal(409, conflict.StatusCode);
        }
        finally { await f.CleanupAsync(); }
    }

    [BrandKnowledgeMySqlFact]
    public async Task RetryableFailuresBackOffUpToThreeAttemptsThenFail()
    {
        var f = await Fixture.CreateAsync();
        try
        {
            var job = await f.Store.EnqueueAsync(f.Tenant, f.Site, f.User, new KnowledgeExtractRequest(Url));
            for (var attempt = 1; attempt <= KnowledgeExtractJobStore.MaxAttempts; attempt++)
            {
                var lease = await f.ClaimMineAsync(job.JobId);
                Assert.Equal(attempt, lease.AttemptCount);
                Assert.True(await f.Store.FailAsync(lease, "KNOWLEDGE_EXTRACT_RETRYABLE", "识别服务暂时繁忙，请稍后重试。", true));
                var view = (await f.Store.GetAsync(f.Tenant, f.Site, job.JobId))!;
                if (attempt < KnowledgeExtractJobStore.MaxAttempts)
                {
                    Assert.Equal("pending", view.Status);
                    Assert.Null(view.CompletedAt);
                    var delay = await f.ScalarAsync("""
                        SELECT TIMESTAMPDIFF(SECOND, UTC_TIMESTAMP(6), next_attempt_at) FROM brand_knowledge_extract_jobs
                        WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@job);
                        """, ("@tenant", f.Tenant.ToString()), ("@job", job.JobId.ToString()));
                    Assert.InRange(delay, KnowledgeExtractJobStore.RetryDelaySeconds(attempt) - 5, KnowledgeExtractJobStore.RetryDelaySeconds(attempt));
                }
                else
                {
                    Assert.Equal("failed", view.Status);
                    Assert.Equal("KNOWLEDGE_EXTRACT_RETRYABLE", view.ErrorCode);
                    Assert.NotNull(view.CompletedAt);
                }
            }

            // 24 小时内的失败任务作为当前任务返回，便于展示原因；放弃后不再返回，也不占用品牌名额。
            Assert.Equal(job.JobId, (await f.Store.GetCurrentAsync(f.Tenant, f.Site))!.JobId);
            Assert.Equal("discarded", (await f.Store.MarkResolvedAsync(f.Tenant, f.Site, job.JobId, "discarded")).Status);
            Assert.Null(await f.Store.GetCurrentAsync(f.Tenant, f.Site));

            var direct = await f.Store.EnqueueAsync(f.Tenant, f.Site, f.User, new KnowledgeExtractRequest(Url));
            Assert.True(await f.Store.FailAsync(await f.ClaimMineAsync(direct.JobId), "KNOWLEDGE_LINK_UNREADABLE", "网页拒绝了读取请求。", false));
            var failed = (await f.Store.GetAsync(f.Tenant, f.Site, direct.JobId))!;
            Assert.Equal("failed", failed.Status);
            Assert.Equal("网页拒绝了读取请求。", failed.ErrorMessage);
            // 失败任务不占名额：可直接重新发起，新任务成为当前任务。
            var retry = await f.Store.EnqueueAsync(f.Tenant, f.Site, f.User, new KnowledgeExtractRequest(Url));
            Assert.Equal(retry.JobId, (await f.Store.GetCurrentAsync(f.Tenant, f.Site))!.JobId);

            // 租约过期且中断次数已满的 running 任务被直接记为失败，不再领取。
            var lease3 = await f.ClaimMineAsync(retry.JobId);
            await f.ExecAsync("""
                UPDATE brand_knowledge_extract_jobs SET attempt_count=3, lease_until=DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 1 MINUTE),
                    next_attempt_at='2000-01-01 00:00:00'
                WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@job);
                """, ("@tenant", f.Tenant.ToString()), ("@job", retry.JobId.ToString()));
            Assert.Null(await f.Store.ClaimAsync("extract-sql-test", TimeSpan.FromMinutes(2)));
            var abandoned = (await f.Store.GetAsync(f.Tenant, f.Site, retry.JobId))!;
            Assert.Equal("failed", abandoned.Status);
            Assert.False(await f.Store.CompleteAsync(lease3, Completion()));
        }
        finally { await f.CleanupAsync(); }
    }

    [BrandKnowledgeMySqlFact]
    public async Task SaveCandidatesWritesEditedItemsWithSourceAndResolvesJobInOneTransaction()
    {
        var f = await Fixture.CreateAsync();
        try
        {
            var jobId = await f.CompletedJobAsync(Candidate("1", "成立背景"), Candidate("2", "总部在哪里？"), Candidate("3", "资质荣誉"));

            // CandidateId 不属于该任务、重复提交、编辑后字段无效：400，什么都不写，任务保持 done。
            foreach (var request in new[]
                     {
                         Save(("999", BrandKnowledgeDimension.BrandIntro, "伪造", "伪造内容")),
                         Save(("1", BrandKnowledgeDimension.BrandIntro, "成立背景", "内容"), ("1", BrandKnowledgeDimension.Faq, "另一个", "内容")),
                         Save(("1", BrandKnowledgeDimension.BrandIntro, "   ", "内容")),
                         Save(("1", BrandKnowledgeDimension.BrandIntro, "成立背景", new string('长', BrandKnowledgeRules.MaxContentLength + 1))),
                         Save(("1", (BrandKnowledgeDimension)99, "成立背景", "内容"))
                     })
            {
                var invalid = await Assert.ThrowsAsync<CollectorStoreException>(() => f.Store.SaveCandidatesAsync(f.Tenant, f.Site, jobId, f.User, request));
                Assert.Equal("KNOWLEDGE_ITEM_VALIDATION", invalid.Code);
                Assert.Equal(400, invalid.StatusCode);
            }
            Assert.Equal("KNOWLEDGE_ITEM_VALIDATION", (await Assert.ThrowsAsync<CollectorStoreException>(() =>
                f.Store.SaveCandidatesAsync(f.Tenant, f.Site, jobId, f.User, new KnowledgeExtractSaveRequest([])))).Code);
            Assert.Equal(0, await f.ItemCountAsync());
            Assert.Equal("done", (await f.Store.GetAsync(f.Tenant, f.Site, jobId))!.Status);

            // 跨品牌、跨租户、不存在的任务：404。
            Assert.Equal("KNOWLEDGE_EXTRACT_NOT_FOUND", (await Assert.ThrowsAsync<CollectorStoreException>(() =>
                f.Store.SaveCandidatesAsync(f.Tenant, f.OtherSite, jobId, f.User, Save(("1", BrandKnowledgeDimension.BrandIntro, "a", "b"))))).Code);
            Assert.Equal("KNOWLEDGE_EXTRACT_NOT_FOUND", (await Assert.ThrowsAsync<CollectorStoreException>(() =>
                f.Store.SaveCandidatesAsync(Guid.NewGuid(), f.Site, jobId, f.User, Save(("1", BrandKnowledgeDimension.BrandIntro, "a", "b"))))).Code);

            // 保存两条：第 2 条改了维度与标题；原文依据取任务内候选，来源记任务网址。
            var created = await f.Store.SaveCandidatesAsync(f.Tenant, f.Site, jobId, f.User, Save(
                ("1", BrandKnowledgeDimension.BrandIntro, "  成立背景 ", "成立于 2010 年。"),
                ("2", BrandKnowledgeDimension.Faq, "公司总部在哪里？", "总部位于深圳。")));
            Assert.Equal(2, created.Count);
            Assert.Equal(["成立背景", "公司总部在哪里？"], created.Select(item => item.Title));
            Assert.Equal([BrandKnowledgeDimension.BrandIntro, BrandKnowledgeDimension.Faq], created.Select(item => item.Dimension));
            Assert.All(created, item =>
            {
                Assert.Equal("url", item.SourceKind);
                Assert.Equal("https://example.test/about", item.SourceUrl);
                Assert.Equal(f.User, item.CreatedBy);
                Assert.Equal("pending", item.SyncStatus);
            });
            Assert.Equal(["依据 成立背景", "依据 总部在哪里？"], created.Select(item => item.SourceExcerpt));
            Assert.Equal(2, await f.ScalarAsync("""
                SELECT COUNT(*) FROM brand_knowledge_items WHERE tenant_id=UUID_TO_BIN(@tenant) AND extract_job_id=UUID_TO_BIN(@job)
                  AND source_url_hash IS NOT NULL;
                """, ("@tenant", f.Tenant.ToString()), ("@job", jobId.ToString())));

            var job = (await f.Store.GetAsync(f.Tenant, f.Site, jobId))!;
            Assert.Equal("saved", job.Status);
            Assert.Equal(1, await f.ScalarAsync(
                "SELECT COUNT(*) FROM brand_knowledge_extract_jobs WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@job) AND resolved_at IS NOT NULL;",
                ("@tenant", f.Tenant.ToString()), ("@job", jobId.ToString())));
            Assert.Null(await f.Store.GetCurrentAsync(f.Tenant, f.Site));

            // 已保存的任务不能再次保存。
            var again = await Assert.ThrowsAsync<CollectorStoreException>(() =>
                f.Store.SaveCandidatesAsync(f.Tenant, f.Site, jobId, f.User, Save(("3", BrandKnowledgeDimension.BrandIntro, "资质荣誉", "内容"))));
            Assert.Equal("KNOWLEDGE_EXTRACT_NOT_READY", again.Code);
            Assert.Equal(409, again.StatusCode);
            Assert.Equal(2, await f.ItemCountAsync());

            // 未完成的任务不能保存。
            var pending = await f.Store.EnqueueAsync(f.Tenant, f.Site, f.User, new KnowledgeExtractRequest(Url));
            Assert.Equal("KNOWLEDGE_EXTRACT_NOT_READY", (await Assert.ThrowsAsync<CollectorStoreException>(() =>
                f.Store.SaveCandidatesAsync(f.Tenant, f.Site, pending.JobId, f.User, Save(("1", BrandKnowledgeDimension.BrandIntro, "a", "b"))))).Code);
        }
        finally { await f.CleanupAsync(); }
    }

    [BrandKnowledgeMySqlFact]
    public async Task ConcurrentSavesOfTheSameJobHaveExactlyOneWinner()
    {
        var f = await Fixture.CreateAsync();
        try
        {
            var jobId = await f.CompletedJobAsync(Candidate("1", "成立背景"), Candidate("2", "资质荣誉"));
            var attempts = Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
            {
                try
                {
                    return (Created: await f.Store.SaveCandidatesAsync(f.Tenant, f.Site, jobId, f.User,
                        Save(("1", BrandKnowledgeDimension.BrandIntro, "成立背景", "成立于 2010 年。"),
                             ("2", BrandKnowledgeDimension.BrandIntro, "资质荣誉", "高新技术企业。"))), Error: (CollectorStoreException?)null);
                }
                catch (CollectorStoreException exception) { return (Created: (IReadOnlyList<BrandKnowledgeItemView>?)null, Error: exception); }
            })).ToArray();
            var results = await Task.WhenAll(attempts);

            Assert.Single(results, result => result.Created is { Count: 2 });
            var losers = results.Where(result => result.Error is not null).ToArray();
            Assert.Equal(results.Length - 1, losers.Length);
            Assert.All(losers, loser =>
            {
                Assert.Equal("KNOWLEDGE_EXTRACT_NOT_READY", loser.Error!.Code);
                Assert.Equal(409, loser.Error.StatusCode);
            });
            Assert.Equal(2, await f.ItemCountAsync());
            Assert.Equal("saved", (await f.Store.GetAsync(f.Tenant, f.Site, jobId))!.Status);
        }
        finally { await f.CleanupAsync(); }
    }

    [BrandKnowledgeMySqlFact]
    public async Task SaveOverTheItemLimitOrWithTitleConflictWritesNothingAndKeepsJobDone()
    {
        var f = await Fixture.CreateAsync();
        try
        {
            await f.Items.CreateManyAsync(f.Tenant, f.Site, f.User, Enumerable.Range(1, BrandKnowledgeRules.MaxItemsPerSite - 1)
                .Select(index => new BrandKnowledgeItemCreate(new BrandKnowledgeItemWrite(BrandKnowledgeDimension.Faq, $"问题 {index}？", "回答。")))
                .ToArray(), BrandKnowledgeItemSource.Manual);
            var jobId = await f.CompletedJobAsync(Candidate("1", "成立背景"), Candidate("2", "资质荣誉"));

            var limited = await Assert.ThrowsAsync<CollectorStoreException>(() => f.Store.SaveCandidatesAsync(f.Tenant, f.Site, jobId, f.User,
                Save(("1", BrandKnowledgeDimension.BrandIntro, "成立背景", "内容"), ("2", BrandKnowledgeDimension.BrandIntro, "资质荣誉", "内容"))));
            Assert.Equal("KNOWLEDGE_ITEM_LIMIT", limited.Code);
            Assert.Equal(409, limited.StatusCode);
            Assert.Equal(BrandKnowledgeRules.MaxItemsPerSite - 1, await f.ItemCountAsync());
            var stillDone = (await f.Store.GetAsync(f.Tenant, f.Site, jobId))!;
            Assert.Equal("done", stillDone.Status);
            Assert.Equal(jobId, (await f.Store.GetCurrentAsync(f.Tenant, f.Site))!.JobId);

            // 与已有条目同维度重名：409，同样不写入。
            var conflict = await Assert.ThrowsAsync<CollectorStoreException>(() => f.Store.SaveCandidatesAsync(f.Tenant, f.Site, jobId, f.User,
                Save(("1", BrandKnowledgeDimension.Faq, "问题 1？", "内容"))));
            Assert.Equal("KNOWLEDGE_ITEM_TITLE_CONFLICT", conflict.Code);
            Assert.Equal(BrandKnowledgeRules.MaxItemsPerSite - 1, await f.ItemCountAsync());
            Assert.Equal("done", (await f.Store.GetAsync(f.Tenant, f.Site, jobId))!.Status);

            // 减少勾选后可以保存，恰好达到上限。
            Assert.Single(await f.Store.SaveCandidatesAsync(f.Tenant, f.Site, jobId, f.User, Save(("2", BrandKnowledgeDimension.BrandIntro, "资质荣誉", "内容"))));
            Assert.Equal(BrandKnowledgeRules.MaxItemsPerSite, await f.ItemCountAsync());
            Assert.Equal("saved", (await f.Store.GetAsync(f.Tenant, f.Site, jobId))!.Status);
        }
        finally { await f.CleanupAsync(); }
    }

    [BrandKnowledgeMySqlFact]
    public async Task SavingAnExpiredJobReturns410AndDiscardsIt()
    {
        var f = await Fixture.CreateAsync();
        try
        {
            var jobId = await f.CompletedJobAsync(Candidate("1", "成立背景"));
            await f.ExpireAsync(jobId);

            var expired = await Assert.ThrowsAsync<CollectorStoreException>(() =>
                f.Store.SaveCandidatesAsync(f.Tenant, f.Site, jobId, f.User, Save(("1", BrandKnowledgeDimension.BrandIntro, "成立背景", "内容"))));
            Assert.Equal("KNOWLEDGE_EXTRACT_EXPIRED", expired.Code);
            Assert.Equal(410, expired.StatusCode);
            // 惰性过期已提交：任务置为 discarded，再次保存仍是 410，没有写入条目。
            Assert.Equal("discarded", (await f.Store.GetAsync(f.Tenant, f.Site, jobId))!.Status);
            Assert.Equal("KNOWLEDGE_EXTRACT_EXPIRED", (await Assert.ThrowsAsync<CollectorStoreException>(() =>
                f.Store.SaveCandidatesAsync(f.Tenant, f.Site, jobId, f.User, Save(("1", BrandKnowledgeDimension.BrandIntro, "成立背景", "内容"))))).Code);
            Assert.Equal(0, await f.ItemCountAsync());

            // 品牌不属于该租户时当前任务返回 404 而不是「没有任务」。
            Assert.Equal("RESOURCE_NOT_FOUND",
                (await Assert.ThrowsAsync<CollectorStoreException>(() => f.Store.GetCurrentAsync(f.Tenant, Guid.NewGuid()))).Code);
        }
        finally { await f.CleanupAsync(); }
    }

    [BrandKnowledgeMySqlFact]
    public async Task JobsOfArchivedBrandsAreFailedAtClaimInsteadOfLeased()
    {
        var f = await Fixture.CreateAsync();
        try
        {
            var job = await f.Store.EnqueueAsync(f.Tenant, f.Site, f.User, new KnowledgeExtractRequest(Url));
            await f.ExecAsync("""
                UPDATE brand_knowledge_extract_jobs SET next_attempt_at='2000-01-01 00:00:00' WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@job);
                UPDATE sites SET status='archived' WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@site);
                """, ("@tenant", f.Tenant.ToString()), ("@job", job.JobId.ToString()), ("@site", f.Site.ToString()));

            // 领取时发现品牌已归档：不建立租约、不增加尝试次数，直接记为失败，不会一直停在 pending。
            Assert.Null(await f.Store.ClaimAsync("extract-sql-test", TimeSpan.FromMinutes(2)));
            var failed = (await f.Store.GetAsync(f.Tenant, f.Site, job.JobId))!;
            Assert.Equal("failed", failed.Status);
            Assert.Equal("RESOURCE_NOT_FOUND", failed.ErrorCode);
            Assert.Equal(0, await f.ScalarAsync("""
                SELECT attempt_count FROM brand_knowledge_extract_jobs WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@job) AND lease_owner IS NULL;
                """, ("@tenant", f.Tenant.ToString()), ("@job", job.JobId.ToString())));

            // 品牌恢复后可以重新发起并正常领取。
            await f.ExecAsync("UPDATE sites SET status='active' WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@site);",
                ("@tenant", f.Tenant.ToString()), ("@site", f.Site.ToString()));
            var again = await f.Store.EnqueueAsync(f.Tenant, f.Site, f.User, new KnowledgeExtractRequest(Url));
            Assert.Equal(again.JobId, (await f.ClaimMineAsync(again.JobId)).JobId);
        }
        finally { await f.CleanupAsync(); }
    }

    [BrandKnowledgeMySqlFact]
    public async Task ProductParseQuotaCountsTodaysExtractJobsOnTheUtcDay()
    {
        var f = await Fixture.CreateAsync();
        try
        {
            var first = await f.Products.CreateAsync(f.Tenant, f.Site, f.User, new KnowledgeProductWrite("额度产品 A"));
            var second = await f.Products.CreateAsync(f.Tenant, f.Site, f.User, new KnowledgeProductWrite("额度产品 B"));
            await f.InsertExtractJobsAsync(KnowledgeExtractJobStore.DailyLimit - 1, "UTC_TIMESTAMP(6)");
            Assert.Equal(KnowledgeExtractJobStore.DailyLimit - 1, await f.Products.CountParseJobsTodayAsync(f.Tenant));

            // 49 个网址识别 + 1 个产品识别 = 50：第二个产品识别被拒，错误码不变。
            await f.Products.EnqueueParseAsync(f.Tenant, f.Site, first.ProductId, f.User, new ProductParseRequest(ProductParseJobKind.Completion));
            Assert.Equal(KnowledgeExtractJobStore.DailyLimit, await f.Products.CountParseJobsTodayAsync(f.Tenant));
            var limited = await Assert.ThrowsAsync<CollectorStoreException>(() =>
                f.Products.EnqueueParseAsync(f.Tenant, f.Site, second.ProductId, f.User, new ProductParseRequest(ProductParseJobKind.Completion)));
            Assert.Equal("PRODUCT_PARSE_RATE_LIMITED", limited.Code);
            Assert.Equal(429, limited.StatusCode);
            Assert.Equal(KnowledgeProductStatus.Draft, (await f.Products.GetAsync(f.Tenant, f.Site, second.ProductId))!.Status);

            // 前一 UTC 日的网址识别不计入今天。
            await f.ExecAsync("UPDATE brand_knowledge_extract_jobs SET created_at=DATE_SUB(UTC_DATE(), INTERVAL 1 SECOND) WHERE tenant_id=UUID_TO_BIN(@tenant);",
                ("@tenant", f.Tenant.ToString()));
            Assert.Equal(1, await f.Products.CountParseJobsTodayAsync(f.Tenant));
            var allowed = await f.Products.EnqueueParseAsync(f.Tenant, f.Site, second.ProductId, f.User, new ProductParseRequest(ProductParseJobKind.Completion));
            Assert.Equal("pending", allowed.Status);
        }
        finally { await f.CleanupAsync(); }
    }

    [BrandKnowledgeMySqlFact]
    public async Task MigratedProductLookupMatchesLegacyProductIdIncludingDeletedItems()
    {
        var f = await Fixture.CreateAsync();
        try
        {
            var migrated = await f.Products.CreateAsync(f.Tenant, f.Site, f.User, new KnowledgeProductWrite("3188DH"));
            var untouched = await f.Products.CreateAsync(f.Tenant, f.Site, f.User, new KnowledgeProductWrite("其他产品"));
            Assert.False(await f.Products.IsMigratedAsync(f.Tenant, migrated.ProductId));

            await f.ExecAsync("""
                INSERT INTO brand_knowledge_items (id, tenant_id, site_id, dimension, title, title_key, content, source_kind, legacy_product_id,
                    content_version, synced_version, sync_status, sync_next_at, created_by, updated_by, created_at, updated_at, deleted_at, deleted_by)
                VALUES (UUID_TO_BIN(@product), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), 'product_service', '3188DH', '3188dh', '迁移内容', 'migrated',
                    UUID_TO_BIN(@product), 1, 0, 'pending', UTC_TIMESTAMP(6), UUID_TO_BIN(@user), UUID_TO_BIN(@user), UTC_TIMESTAMP(6), UTC_TIMESTAMP(6),
                    UTC_TIMESTAMP(6), UUID_TO_BIN(@user));
                """, ("@product", migrated.ProductId.ToString()), ("@tenant", f.Tenant.ToString()), ("@site", f.Site.ToString()), ("@user", f.User.ToString()));

            Assert.True(await f.Products.IsMigratedAsync(f.Tenant, migrated.ProductId));
            Assert.False(await f.Products.IsMigratedAsync(f.Tenant, untouched.ProductId));
            Assert.False(await f.Products.IsMigratedAsync(Guid.NewGuid(), migrated.ProductId));
        }
        finally { await f.CleanupAsync(); }
    }

    private static KnowledgeExtractSaveRequest Save(params (string CandidateId, BrandKnowledgeDimension Dimension, string Title, string Content)[] items)
        => new(items.Select(item => new KnowledgeExtractSaveItem(item.CandidateId, item.Dimension, item.Title, item.Content)).ToArray());

    private static KnowledgeExtractCandidate Candidate(string id, string title)
        => new(id, BrandKnowledgeDimension.BrandIntro, title, "内容 " + title, "依据 " + title, null);

    private static KnowledgeExtractCompletion Completion(params KnowledgeExtractCandidate[] candidates)
        => new("关于我们", new string('b', 64), 1234, false, candidates, "gpt-6-luna", "openai-compatible", 321);

    private sealed class Fixture
    {
        private readonly string connectionString;

        private readonly string storageRoot = Path.Combine(Path.GetTempPath(), $"jogeoai-knowledge-extract-sql-{Guid.NewGuid():N}");

        private Fixture(string connection)
        {
            connectionString = connection;
            var database = new MySqlDatabase(connection);
            Store = new KnowledgeExtractJobStore(database);
            Items = new BrandKnowledgeItemStore(database);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Knowledge:StorageRoot"] = storageRoot
            }).Build();
            Products = new KnowledgeProductStore(database,
                new KnowledgeStore(database, new LocalObjectStorage(configuration), new DeterministicEmbeddingProvider(), new InMemoryVectorIndex()));
        }

        public Guid Tenant { get; } = Guid.NewGuid();
        public Guid Site { get; } = Guid.NewGuid();
        public Guid OtherSite { get; } = Guid.NewGuid();
        public Guid User { get; } = Guid.NewGuid();
        public Guid Product { get; } = Guid.NewGuid();
        public KnowledgeExtractJobStore Store { get; }
        public BrandKnowledgeItemStore Items { get; }
        public KnowledgeProductStore Products { get; }

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
                VALUES (UUID_TO_BIN(@tenant), 'knowledge extract fixture', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                INSERT INTO auth_users (id, email, password_hash, status, created_at, updated_at)
                VALUES (UUID_TO_BIN(@user), @email, 'not-a-login', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                INSERT INTO sites (id, tenant_id, host, display_name, status, created_at, updated_at)
                VALUES (UUID_TO_BIN(@site), UUID_TO_BIN(@tenant), NULL, 'Extract fixture', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)),
                       (UUID_TO_BIN(@other), UUID_TO_BIN(@tenant), NULL, 'Extract fixture 2', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                """, ("@tenant", fixture.Tenant.ToString()), ("@user", fixture.User.ToString()),
                ("@email", $"knowledge-extract-{fixture.User:N}@example.test"), ("@site", fixture.Site.ToString()),
                ("@other", fixture.OtherSite.ToString()));
            return fixture;
        }

        /// <summary>只删除本夹具租户、品牌与用户的行；不触碰库内其他行。</summary>
        public async Task CleanupAsync()
        {
            foreach (var sql in new[]
                     {
                         "DELETE FROM brand_knowledge_items WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM brand_knowledge_extract_jobs WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM knowledge_product_parse_jobs WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM knowledge_product_tags WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM knowledge_products WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM sites WHERE tenant_id=UUID_TO_BIN(@tenant) AND id IN (UUID_TO_BIN(@site), UUID_TO_BIN(@other));",
                         "DELETE FROM auth_users WHERE id=UUID_TO_BIN(@user);",
                         "DELETE FROM tenants WHERE id=UUID_TO_BIN(@tenant);"
                     })
                await ExecAsync(sql, ("@tenant", Tenant.ToString()), ("@site", Site.ToString()), ("@other", OtherSite.ToString()),
                    ("@user", User.ToString()));
            try { if (Directory.Exists(storageRoot)) Directory.Delete(storageRoot, recursive: true); }
            catch (IOException) { /* 临时目录清理失败不影响断言。 */ }
        }

        /// <summary>发起、领取并完成一个识别任务，返回任务标识。</summary>
        public async Task<Guid> CompletedJobAsync(params KnowledgeExtractCandidate[] candidates)
        {
            var job = await Store.EnqueueAsync(Tenant, Site, User, new KnowledgeExtractRequest(Url));
            Assert.True(await Store.CompleteAsync(await ClaimMineAsync(job.JobId), Completion(candidates)));
            return job.JobId;
        }

        public Task<long> ItemCountAsync()
            => ScalarAsync("SELECT COUNT(*) FROM brand_knowledge_items WHERE tenant_id=UUID_TO_BIN(@tenant) AND deleted_at IS NULL;",
                ("@tenant", Tenant.ToString()));

        /// <summary>插入本租户已取消的网址识别任务（不占品牌单任务名额），created_at 取给定 SQL 表达式，用于验证共用频控。</summary>
        public async Task InsertExtractJobsAsync(int count, string createdAtSql)
        {
            for (var index = 0; index < count; index++)
                await ExecAsync($"""
                    INSERT INTO brand_knowledge_extract_jobs (id, tenant_id, site_id, requested_by, url, url_hash, status, step, next_attempt_at,
                        created_at, updated_at, completed_at)
                    VALUES (UUID_TO_BIN(UUID()), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), UUID_TO_BIN(@user), 'https://example.test/quota',
                        SHA2('https://example.test/quota', 256), 'cancelled', 'fetch', UTC_TIMESTAMP(6), {createdAtSql}, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                    """, ("@tenant", Tenant.ToString()), ("@site", Site.ToString()), ("@user", User.ToString()));
        }

        /// <summary>把本用例任务排到领取队列最前并领取，断言领取到的是它而不是库内其他行。</summary>
        public async Task<KnowledgeExtractJobLease> ClaimMineAsync(Guid jobId)
        {
            await ExecAsync("UPDATE brand_knowledge_extract_jobs SET next_attempt_at='2000-01-01 00:00:00' WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@job);",
                ("@tenant", Tenant.ToString()), ("@job", jobId.ToString()));
            var lease = await Store.ClaimAsync("extract-sql-test", TimeSpan.FromMinutes(2));
            Assert.NotNull(lease);
            Assert.Equal(jobId, lease!.JobId);
            return lease;
        }

        public Task ExpireAsync(Guid jobId)
            => ExecAsync("""
                UPDATE brand_knowledge_extract_jobs SET completed_at=DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 25 HOUR),
                    expires_at=DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 1 HOUR)
                WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@job);
                """, ("@tenant", Tenant.ToString()), ("@job", jobId.ToString()));

        /// <summary>插入本租户今日的已完成产品识别任务，用于验证共用频控。</summary>
        public async Task InsertProductParseJobsAsync(int count)
        {
            await ExecAsync("""
                INSERT INTO knowledge_products (id, tenant_id, site_id, name, status, created_by, updated_by, created_at, updated_at)
                VALUES (UUID_TO_BIN(@product), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), 'Extract quota', 'draft',
                        UUID_TO_BIN(@user), UUID_TO_BIN(@user), UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                """, ("@product", Product.ToString()), ("@tenant", Tenant.ToString()), ("@site", Site.ToString()), ("@user", User.ToString()));
            for (var index = 0; index < count; index++)
                await ExecAsync("""
                    INSERT INTO knowledge_product_parse_jobs (id, tenant_id, site_id, product_id, job_kind, status, step, next_attempt_at,
                        requested_by, created_at, updated_at)
                    VALUES (UUID_TO_BIN(UUID()), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), UUID_TO_BIN(@product), 'completion', 'done', 'finalize',
                        UTC_TIMESTAMP(6), UUID_TO_BIN(@user), UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                    """, ("@tenant", Tenant.ToString()), ("@site", Site.ToString()), ("@product", Product.ToString()), ("@user", User.ToString()));
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
            return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }
    }
}
