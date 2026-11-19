using System.Globalization;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Jogeoai.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;

namespace Jogeoai.Tests;

/// <summary>品牌知识条目 SQL 测试只连接已执行 001–043 的独立测试库；未配置时跳过，绝不使用业务库 jogeoai。</summary>
public sealed class BrandKnowledgeMySqlFactAttribute : FactAttribute
{
    public BrandKnowledgeMySqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JOGEOAI_KNOWLEDGE_TEST_CONNECTION")))
            Skip = "需提供已执行 001–043 迁移的独立测试库 JOGEOAI_KNOWLEDGE_TEST_CONNECTION；不能使用业务库 jogeoai。";
    }
}

/// <summary>
/// 每个用例自建随机 UUID 的租户、用户与品牌，结束时只删除本用例租户的数据；
/// 领取同步租约前把本用例条目的 sync_next_at 调到 2000 年，保证领取顺序排在库内其他待同步行之前，不触碰既有行。
/// </summary>
[Collection("Knowledge isolated SQL")]
public sealed class BrandKnowledgeItemStoreSqlTests
{
    [Fact]
    public void ResearchBudgetCapsItemsPerItemAndTotalCharacters()
    {
        var items = Enumerable.Range(0, 20).Select(index => new BrandKnowledgeResearchItem(Guid.NewGuid(), BrandKnowledgeDimension.SellingPoint,
            $"t{index}", new string('字', 2000), false, DateTimeOffset.UtcNow)).ToArray();
        var selected = BrandKnowledgeItemStore.ApplyResearchBudget(items);
        Assert.True(selected.Count <= BrandKnowledgeItemStore.ResearchMaxItems);
        Assert.All(selected, item => Assert.True(BrandKnowledgeRules.CharacterCount(item.Content) <= 1200 && item.Truncated));
        Assert.True(selected.Sum(item => BrandKnowledgeRules.CharacterCount(item.Content)) <= 8000);
        Assert.Equal(7, selected.Count); // 6 × 1200 + 800。

        var shortItems = Enumerable.Range(0, 15).Select(index => new BrandKnowledgeResearchItem(Guid.NewGuid(), BrandKnowledgeDimension.Faq,
            $"q{index}", "短回答", false, DateTimeOffset.UtcNow)).ToArray();
        var capped = BrandKnowledgeItemStore.ApplyResearchBudget(shortItems);
        Assert.Equal(12, capped.Count);
        Assert.All(capped, item => Assert.False(item.Truncated));
    }

    [Fact]
    public void SyncBackoffGrowsExponentiallyAndIsCappedAtThirtyMinutes()
    {
        Assert.Equal(60, BrandKnowledgeItemStore.SyncBackoffSeconds(1));
        Assert.Equal(120, BrandKnowledgeItemStore.SyncBackoffSeconds(2));
        Assert.Equal(480, BrandKnowledgeItemStore.SyncBackoffSeconds(4));
        Assert.Equal(1800, BrandKnowledgeItemStore.SyncBackoffSeconds(6));
        Assert.Equal(1800, BrandKnowledgeItemStore.SyncBackoffSeconds(50));
    }

    [BrandKnowledgeMySqlFact]
    public async Task LimitOfFiftyHoldsForBatchesAndConcurrentCreates()
    {
        var f = await Fixture.CreateAsync();
        try
        {
            var batch = Enumerable.Range(1, 45).Select(index => Create(BrandKnowledgeDimension.Faq, $"问题 {index}？", $"回答 {index}")).ToArray();
            var created = await f.Store.CreateManyAsync(f.Tenant, f.Site, f.User, batch, BrandKnowledgeItemSource.Manual);
            Assert.Equal(45, created.Count);
            Assert.Equal(batch.Select(item => item.Write.Title), created.Select(item => item.Title));

            // 超出剩余额度的批次整批拒绝，不写入任何一条。
            var over = await Assert.ThrowsAsync<CollectorStoreException>(() => f.Store.CreateManyAsync(f.Tenant, f.Site, f.User,
                Enumerable.Range(1, 6).Select(index => Create(BrandKnowledgeDimension.SellingPoint, $"卖点 {index}", "内容")).ToArray(),
                BrandKnowledgeItemSource.Manual));
            Assert.Equal("KNOWLEDGE_ITEM_LIMIT", over.Code);
            Assert.Equal(409, over.StatusCode);
            Assert.Equal(45, (await f.Store.ListAsync(f.Tenant, f.Site)).Total);

            // 10 个并发单条新增争抢剩余 5 个名额：锁品牌行后计数，恰好 5 个成功。
            var attempts = Enumerable.Range(1, 10).Select(index => Task.Run(async () =>
            {
                try
                {
                    await f.Store.CreateManyAsync(f.Tenant, f.Site, f.User,
                        [Create(BrandKnowledgeDimension.Audience, $"人群 {index}", "内容")], BrandKnowledgeItemSource.Manual);
                    return "ok";
                }
                catch (CollectorStoreException exception) { return exception.Code; }
            })).ToArray();
            var results = await Task.WhenAll(attempts);
            Assert.Equal(5, results.Count(result => result == "ok"));
            Assert.Equal(5, results.Count(result => result == "KNOWLEDGE_ITEM_LIMIT"));
            var list = await f.Store.ListAsync(f.Tenant, f.Site);
            Assert.Equal(50, list.Total);
            Assert.Equal(50, list.Limit);
            Assert.Equal(50, await f.ScalarAsync("SELECT COUNT(*) FROM brand_knowledge_items WHERE tenant_id=UUID_TO_BIN(@tenant) AND deleted_at IS NULL;",
                ("@tenant", f.Tenant.ToString())));

            // 软删一条后又能新增一条。
            await f.Store.DeleteAsync(f.Tenant, f.Site, created[0].ItemId, f.User);
            await f.Store.CreateManyAsync(f.Tenant, f.Site, f.User, [Create(BrandKnowledgeDimension.Faq, "新问题？", "新回答")], BrandKnowledgeItemSource.Manual);
            Assert.Equal(50, (await f.Store.ListAsync(f.Tenant, f.Site)).Total);
        }
        finally { await f.CleanupAsync(); }
    }

    [BrandKnowledgeMySqlFact]
    public async Task TitleConflictIsPerDimensionAndTitleIsReusableAfterSoftDelete()
    {
        var f = await Fixture.CreateAsync();
        try
        {
            var first = (await f.Store.CreateManyAsync(f.Tenant, f.Site, f.User,
                [Create(BrandKnowledgeDimension.SellingPoint, "IP68 防水", "整机 IP68 防水。")], BrandKnowledgeItemSource.Manual)).Single();

            // 同维度：大小写、全角、空白差异都视为同名。
            var conflict = await Assert.ThrowsAsync<CollectorStoreException>(() => f.Store.CreateManyAsync(f.Tenant, f.Site, f.User,
                [Create(BrandKnowledgeDimension.SellingPoint, "  ｉｐ68   防水 ", "另一条")], BrandKnowledgeItemSource.Manual));
            Assert.Equal("KNOWLEDGE_ITEM_TITLE_CONFLICT", conflict.Code);
            Assert.Equal(409, conflict.StatusCode);
            Assert.Contains("核心卖点", conflict.Message, StringComparison.Ordinal);

            // 不同维度可同名。
            var otherDimension = (await f.Store.CreateManyAsync(f.Tenant, f.Site, f.User,
                [Create(BrandKnowledgeDimension.Faq, "IP68 防水", "是的，整机 IP68。")], BrandKnowledgeItemSource.Manual)).Single();

            // 批内重复整批拒绝。
            var inBatch = await Assert.ThrowsAsync<CollectorStoreException>(() => f.Store.CreateManyAsync(f.Tenant, f.Site, f.User,
                [Create(BrandKnowledgeDimension.Audience, "工程商", "a"), Create(BrandKnowledgeDimension.Audience, "工程商 ", "b")],
                BrandKnowledgeItemSource.Manual));
            Assert.Equal("KNOWLEDGE_ITEM_TITLE_CONFLICT", inBatch.Code);
            Assert.Equal(2, (await f.Store.ListAsync(f.Tenant, f.Site)).Total);

            // 编辑成同维度已有标题也冲突；改维度后与该维度已有标题冲突。
            var rename = await Assert.ThrowsAsync<CollectorStoreException>(() => f.Store.UpdateAsync(f.Tenant, f.Site, otherDimension.ItemId, f.User,
                new BrandKnowledgeItemUpdate(BrandKnowledgeDimension.SellingPoint, "ip68 防水", "x", otherDimension.Version)));
            Assert.Equal("KNOWLEDGE_ITEM_TITLE_CONFLICT", rename.Code);

            // 软删后同名可再用；重复删除返回 404。
            await f.Store.DeleteAsync(f.Tenant, f.Site, first.ItemId, f.User);
            var reused = (await f.Store.CreateManyAsync(f.Tenant, f.Site, f.User,
                [Create(BrandKnowledgeDimension.SellingPoint, "IP68 防水", "重新录入。")], BrandKnowledgeItemSource.Manual)).Single();
            Assert.NotEqual(first.ItemId, reused.ItemId);
            var deletedAgain = await Assert.ThrowsAsync<CollectorStoreException>(() => f.Store.DeleteAsync(f.Tenant, f.Site, first.ItemId, f.User));
            Assert.Equal("KNOWLEDGE_ITEM_NOT_FOUND", deletedAgain.Code);
            Assert.Equal(404, deletedAgain.StatusCode);
            Assert.Null(await f.Store.GetAsync(f.Tenant, f.Site, first.ItemId));
            Assert.Equal(1, await f.ScalarAsync("""
                SELECT COUNT(*) FROM brand_knowledge_items WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@id)
                  AND deleted_at IS NOT NULL AND deleted_by=UUID_TO_BIN(@user) AND sync_status='pending';
                """, ("@tenant", f.Tenant.ToString()), ("@id", first.ItemId.ToString()), ("@user", f.User.ToString())));
        }
        finally { await f.CleanupAsync(); }
    }

    [BrandKnowledgeMySqlFact]
    public async Task UpdateUsesOptimisticVersionAndUnchangedContentKeepsVersion()
    {
        var f = await Fixture.CreateAsync();
        try
        {
            var item = (await f.Store.CreateManyAsync(f.Tenant, f.Site, f.User,
                [Create(BrandKnowledgeDimension.BrandIntro, "公司简介", "成立于 2010 年。")], BrandKnowledgeItemSource.Manual)).Single();
            Assert.Equal(1, item.Version);
            Assert.Equal("pending", item.SyncStatus);
            Assert.Equal(f.User, item.CreatedBy);
            await f.ExecAsync("UPDATE brand_knowledge_items SET sync_status='synced', synced_version=1 WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@id);",
                ("@tenant", f.Tenant.ToString()), ("@id", item.ItemId.ToString()));

            // 规范化后没有变化：不升版本、不重新同步。
            var unchanged = await f.Store.UpdateAsync(f.Tenant, f.Site, item.ItemId, f.OtherUser,
                new BrandKnowledgeItemUpdate(BrandKnowledgeDimension.BrandIntro, " 公司简介 ", "成立于 2010 年。\r\n", 1));
            Assert.Equal(1, unchanged.Version);
            Assert.Equal("synced", unchanged.SyncStatus);
            Assert.Equal(f.User, unchanged.UpdatedBy);

            var changed = await f.Store.UpdateAsync(f.Tenant, f.Site, item.ItemId, f.OtherUser,
                new BrandKnowledgeItemUpdate(BrandKnowledgeDimension.Differentiator, "公司简介", "成立于 2010 年，总部深圳。", 1));
            Assert.Equal(2, changed.Version);
            Assert.Equal("pending", changed.SyncStatus);
            Assert.Equal(BrandKnowledgeDimension.Differentiator, changed.Dimension);
            Assert.Equal(f.OtherUser, changed.UpdatedBy);
            Assert.Equal(f.User, changed.CreatedBy);
            Assert.Equal(1, await f.ScalarAsync("""
                SELECT COUNT(*) FROM brand_knowledge_items WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@id)
                  AND synced_version=1 AND sync_attempts=0 AND sync_next_at <= UTC_TIMESTAMP(6);
                """, ("@tenant", f.Tenant.ToString()), ("@id", item.ItemId.ToString())));

            var stale = await Assert.ThrowsAsync<CollectorStoreException>(() => f.Store.UpdateAsync(f.Tenant, f.Site, item.ItemId, f.User,
                new BrandKnowledgeItemUpdate(BrandKnowledgeDimension.BrandIntro, "公司简介", "旧页面提交。", 1)));
            Assert.Equal("KNOWLEDGE_ITEM_STALE", stale.Code);
            Assert.Equal(409, stale.StatusCode);
            Assert.Equal("成立于 2010 年，总部深圳。", (await f.Store.GetAsync(f.Tenant, f.Site, item.ItemId))!.Content);

            // 其他品牌、其他租户访问一律 404：品牌不属于该租户时与列表、新增相同返回 RESOURCE_NOT_FOUND；品牌内不存在的条目返回 null / KNOWLEDGE_ITEM_NOT_FOUND。
            var wrongSite = await Assert.ThrowsAsync<CollectorStoreException>(() => f.Store.UpdateAsync(f.Tenant, Guid.NewGuid(), item.ItemId, f.User,
                new BrandKnowledgeItemUpdate(BrandKnowledgeDimension.BrandIntro, "x", "y", 2)));
            Assert.Equal("RESOURCE_NOT_FOUND", wrongSite.Code);
            var wrongTenant = await Assert.ThrowsAsync<CollectorStoreException>(() => f.Store.DeleteAsync(Guid.NewGuid(), f.Site, item.ItemId, f.User));
            Assert.Equal("RESOURCE_NOT_FOUND", wrongTenant.Code);
            Assert.Equal("RESOURCE_NOT_FOUND",
                (await Assert.ThrowsAsync<CollectorStoreException>(() => f.Store.GetAsync(Guid.NewGuid(), f.Site, item.ItemId))).Code);
            Assert.Null(await f.Store.GetAsync(f.Tenant, f.Site, Guid.NewGuid()));
            Assert.Equal("KNOWLEDGE_ITEM_NOT_FOUND",
                (await Assert.ThrowsAsync<CollectorStoreException>(() => f.Store.DeleteAsync(f.Tenant, f.Site, Guid.NewGuid(), f.User))).Code);
            var listOther = await Assert.ThrowsAsync<CollectorStoreException>(() => f.Store.ListAsync(Guid.NewGuid(), f.Site));
            Assert.Equal(404, listOther.StatusCode);

            var invalid = await Assert.ThrowsAsync<CollectorStoreException>(() => f.Store.UpdateAsync(f.Tenant, f.Site, item.ItemId, f.User,
                new BrandKnowledgeItemUpdate(BrandKnowledgeDimension.BrandIntro, "公司简介", new string('字', 2001), 2)));
            Assert.Equal("KNOWLEDGE_ITEM_VALIDATION", invalid.Code);
        }
        finally { await f.CleanupAsync(); }
    }

    [BrandKnowledgeMySqlFact]
    public async Task ArchivedBrandRejectsReadForWriteUpdateAndDeleteLikeListAndCreate()
    {
        var f = await Fixture.CreateAsync();
        try
        {
            var item = (await f.Store.CreateManyAsync(f.Tenant, f.Site, f.User,
                [Create(BrandKnowledgeDimension.BrandIntro, "公司简介", "成立于 2010 年。")], BrandKnowledgeItemSource.Manual)).Single();
            await f.ExecAsync("UPDATE sites SET status='archived' WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@site);",
                ("@tenant", f.Tenant.ToString()), ("@site", f.Site.ToString()));

            foreach (var action in new Func<Task>[]
                     {
                         () => f.Store.ListAsync(f.Tenant, f.Site),
                         () => f.Store.CreateManyAsync(f.Tenant, f.Site, f.User, [Create(BrandKnowledgeDimension.Faq, "怎么买？", "官网下单。")],
                             BrandKnowledgeItemSource.Manual),
                         () => f.Store.GetAsync(f.Tenant, f.Site, item.ItemId),
                         () => f.Store.UpdateAsync(f.Tenant, f.Site, item.ItemId, f.User,
                             new BrandKnowledgeItemUpdate(BrandKnowledgeDimension.BrandIntro, "公司简介", "改写。", 1)),
                         () => f.Store.DeleteAsync(f.Tenant, f.Site, item.ItemId, f.User)
                     })
            {
                var archived = await Assert.ThrowsAsync<CollectorStoreException>(action);
                Assert.Equal("RESOURCE_NOT_FOUND", archived.Code);
                Assert.Equal(404, archived.StatusCode);
            }
            // 条目本身没有被改写或删除。
            Assert.Equal(1, await f.ScalarAsync("""
                SELECT COUNT(*) FROM brand_knowledge_items WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@id)
                  AND deleted_at IS NULL AND content_version=1 AND content='成立于 2010 年。';
                """, ("@tenant", f.Tenant.ToString()), ("@id", item.ItemId.ToString())));
        }
        finally { await f.CleanupAsync(); }
    }

    [BrandKnowledgeMySqlFact]
    public async Task ListCountsOrderingResearchAndProductOptions()
    {
        var f = await Fixture.CreateAsync();
        try
        {
            var jobId = await f.InsertExtractJobAsync("https://example.test/product");
            await f.Store.CreateManyAsync(f.Tenant, f.Site, f.User,
                [Create(BrandKnowledgeDimension.Faq, "保修多久？", "两年。"), Create(BrandKnowledgeDimension.ProductService, "3188DH", "管道检测仪。")],
                BrandKnowledgeItemSource.Manual);
            var fromUrl = await f.Store.CreateManyAsync(f.Tenant, f.Site, f.User,
                [new BrandKnowledgeItemCreate(new BrandKnowledgeItemWrite(BrandKnowledgeDimension.BrandIntro, "公司定位", "专注管道检测。"), "专注管道检测十余年"),
                 new BrandKnowledgeItemCreate(new BrandKnowledgeItemWrite(BrandKnowledgeDimension.ProductService, "3288DH", "大口径型号。"), "3288DH 大口径")],
                new BrandKnowledgeItemSource("url", "https://example.test/product", jobId));
            Assert.All(fromUrl, item => Assert.Equal("url", item.SourceKind));
            Assert.Equal("https://example.test/product", fromUrl[0].SourceUrl);
            Assert.Equal("专注管道检测十余年", fromUrl[0].SourceExcerpt);
            Assert.Equal(2, await f.ScalarAsync("""
                SELECT COUNT(*) FROM brand_knowledge_items WHERE tenant_id=UUID_TO_BIN(@tenant) AND extract_job_id=UUID_TO_BIN(@job)
                  AND source_url_hash=SHA2('https://example.test/product', 256);
                """, ("@tenant", f.Tenant.ToString()), ("@job", jobId.ToString())));
            var badSource = await Assert.ThrowsAsync<CollectorStoreException>(() => f.Store.CreateManyAsync(f.Tenant, f.Site, f.User,
                [Create(BrandKnowledgeDimension.Faq, "无网址？", "x")], new BrandKnowledgeItemSource("url")));
            Assert.Equal("KNOWLEDGE_ITEM_VALIDATION", badSource.Code);

            var all = await f.Store.ListAsync(f.Tenant, f.Site);
            Assert.Equal(["公司定位", "3188DH", "3288DH", "保修多久？"], all.Items.Select(item => item.Title));
            Assert.Equal(4, all.Total);
            Assert.Equal(6, all.Counts.Count);
            Assert.Equal(2, all.Counts[BrandKnowledgeDimension.ProductService]);
            Assert.Equal(0, all.Counts[BrandKnowledgeDimension.SellingPoint]);

            var filtered = await f.Store.ListAsync(f.Tenant, f.Site, BrandKnowledgeDimension.ProductService);
            Assert.Equal(["3188DH", "3288DH"], filtered.Items.Select(item => item.Title));
            Assert.Equal(4, filtered.Total);
            Assert.Equal(all.Counts, filtered.Counts);

            var research = await f.Store.ListForResearchAsync(f.Tenant, f.Site);
            Assert.Equal(BrandKnowledgeDimension.BrandIntro, research[0].Dimension);
            Assert.Equal(BrandKnowledgeDimension.Faq, research[^1].Dimension);
            Assert.Equal(4, research.Count);

            // 产品选项只列出已有检索投影文档的「产品/服务」条目。
            Assert.Empty(await f.Store.ListProductOptionsAsync(f.Tenant, f.Site));
            var product = all.Items.Single(item => item.Title == "3188DH");
            Assert.Null(await f.Store.GetProductRefAsync(f.Tenant, f.Site, product.ItemId));
            var document = await f.Knowledge.UploadKnowledgeItemDocumentAsync(f.Tenant, f.Site, product.ItemId, product.Dimension, product.Title,
                BrandKnowledgeRules.RenderMarkdown(product.Dimension, product.Title, product.Content, null), null);
            await f.ExecAsync("UPDATE brand_knowledge_items SET knowledge_document_id=UUID_TO_BIN(@document) WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@id);",
                ("@document", document.DocumentId.ToString()), ("@tenant", f.Tenant.ToString()), ("@id", product.ItemId.ToString()));
            var option = Assert.Single(await f.Store.ListProductOptionsAsync(f.Tenant, f.Site));
            Assert.Equal(new KnowledgeProductOption(product.ItemId, "3188DH"), option);
            Assert.Equal(new ArticleProductRef(product.ItemId, f.Tenant, f.Site, "3188DH", document.DocumentId),
                await f.Store.GetProductRefAsync(f.Tenant, f.Site, product.ItemId));
            Assert.Null(await f.Store.GetProductRefAsync(f.Tenant, Guid.NewGuid(), product.ItemId));
            await f.Store.DeleteAsync(f.Tenant, f.Site, product.ItemId, f.User);
            Assert.Empty(await f.Store.ListProductOptionsAsync(f.Tenant, f.Site));
            Assert.Null(await f.Store.GetProductRefAsync(f.Tenant, f.Site, product.ItemId));
        }
        finally { await f.CleanupAsync(); }
    }

    [BrandKnowledgeMySqlFact]
    public async Task CompleteSyncOnlyMarksSyncedWhenVersionAndDeleteStateAreUnchanged()
    {
        var f = await Fixture.CreateAsync();
        try
        {
            var item = (await f.Store.CreateManyAsync(f.Tenant, f.Site, f.User,
                [Create(BrandKnowledgeDimension.SellingPoint, "轻便", "整机 1.2 千克。")], BrandKnowledgeItemSource.Manual)).Single();
            var document = await f.Knowledge.UploadKnowledgeItemDocumentAsync(f.Tenant, f.Site, item.ItemId, item.Dimension, item.Title, "# 轻便\n", null);

            // 同步中途内容被编辑：保持 pending，不写 synced_version，但记录文档。
            var lease = await f.ClaimMineAsync(item.ItemId);
            Assert.Equal(1, lease.ContentVersion);
            Assert.Equal(1, lease.AttemptCount);
            Assert.False(lease.Deleted);
            await f.Store.UpdateAsync(f.Tenant, f.Site, item.ItemId, f.User,
                new BrandKnowledgeItemUpdate(BrandKnowledgeDimension.SellingPoint, "轻便", "整机仅 1.2 千克，单手可持。", 1));
            Assert.Equal(BrandKnowledgeSyncOutcome.StillPending, await f.Store.CompleteSyncAsync(lease, document.DocumentId));
            var row = await f.ReadSyncAsync(item.ItemId);
            Assert.Equal(("pending", 2, 0, (Guid?)document.DocumentId, (string?)null), (row.Status, row.ContentVersion, row.SyncedVersion, row.DocumentId, row.LeaseOwner));

            // 下一轮版本未再变化：写 synced。
            var second = await f.ClaimMineAsync(item.ItemId);
            Assert.Equal(2, second.ContentVersion);
            Assert.Equal(document.DocumentId, second.KnowledgeDocumentId);
            Assert.Equal(BrandKnowledgeSyncOutcome.Synced, await f.Store.CompleteSyncAsync(second, document.DocumentId));
            row = await f.ReadSyncAsync(item.ItemId);
            Assert.Equal(("synced", 2, 2, 0), (row.Status, row.SyncedVersion, row.ContentVersion, row.Attempts));

            // 同步中途被删除同样保持 pending。
            await f.Store.UpdateAsync(f.Tenant, f.Site, item.ItemId, f.User,
                new BrandKnowledgeItemUpdate(BrandKnowledgeDimension.SellingPoint, "轻便", "整机仅 1.2 千克。", 2));
            var third = await f.ClaimMineAsync(item.ItemId);
            await f.Store.DeleteAsync(f.Tenant, f.Site, item.ItemId, f.User);
            Assert.Equal(BrandKnowledgeSyncOutcome.StillPending, await f.Store.CompleteSyncAsync(third, document.DocumentId));
            Assert.Equal("pending", (await f.ReadSyncAsync(item.ItemId)).Status);
            var fourth = await f.ClaimMineAsync(item.ItemId);
            Assert.True(fourth.Deleted);
            Assert.Equal(BrandKnowledgeSyncOutcome.Synced, await f.Store.CompleteSyncAsync(fourth, null));
            Assert.Equal(document.DocumentId, (await f.ReadSyncAsync(item.ItemId)).DocumentId);
        }
        finally { await f.CleanupAsync(); }
    }

    [BrandKnowledgeMySqlFact]
    public async Task LostLeaseAndFailuresFollowRetryRules()
    {
        var f = await Fixture.CreateAsync();
        try
        {
            var item = (await f.Store.CreateManyAsync(f.Tenant, f.Site, f.User,
                [Create(BrandKnowledgeDimension.Audience, "工程商", "市政施工单位。")], BrandKnowledgeItemSource.Manual)).Single();
            var document = await f.Knowledge.UploadKnowledgeItemDocumentAsync(f.Tenant, f.Site, item.ItemId, item.Dimension, item.Title, "# 工程商\n", null);

            // 租约被接管：状态不改写，但条目尚无文档时补记本次文档。
            var lease = await f.ClaimMineAsync(item.ItemId);
            await f.ExecAsync("UPDATE brand_knowledge_items SET sync_lease_owner='someone-else' WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@id);",
                ("@tenant", f.Tenant.ToString()), ("@id", item.ItemId.ToString()));
            Assert.Equal(BrandKnowledgeSyncOutcome.LeaseLost, await f.Store.CompleteSyncAsync(lease, document.DocumentId));
            var row = await f.ReadSyncAsync(item.ItemId);
            Assert.Equal(("pending", (string?)"someone-else", (Guid?)document.DocumentId), (row.Status, row.LeaseOwner, row.DocumentId));
            await f.ExecAsync("UPDATE brand_knowledge_items SET sync_lease_owner=NULL, sync_lease_until=NULL WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@id);",
                ("@tenant", f.Tenant.ToString()), ("@id", item.ItemId.ToString()));

            // 可重试失败：failed + 指数退避。
            var retry = await f.ClaimMineAsync(item.ItemId);
            Assert.Equal(2, retry.AttemptCount);
            await f.Store.FailSyncAsync(retry, "KNOWLEDGE_QUOTA_EXCEEDED", "知识库容量已满。", retryable: true);
            row = await f.ReadSyncAsync(item.ItemId);
            Assert.Equal(("failed", 2, (string?)null), (row.Status, row.Attempts, row.LeaseOwner));
            Assert.InRange(row.NextDelaySeconds, 100, 140);
            Assert.StartsWith("KNOWLEDGE_QUOTA_EXCEEDED", row.Error, StringComparison.Ordinal);

            // 不可重试失败：尝试次数直接用尽，领取条件 sync_attempts < 5 不再满足。
            var fatal = await f.ClaimMineAsync(item.ItemId);
            await f.Store.FailSyncAsync(fatal, "KNOWLEDGE_EMPTY_CONTENT", "内容为空。", retryable: false);
            row = await f.ReadSyncAsync(item.ItemId);
            Assert.Equal(("failed", BrandKnowledgeItemStore.MaxSyncAttempts), (row.Status, row.Attempts));

            // 再次编辑后重置尝试次数并立即待同步。
            await f.Store.UpdateAsync(f.Tenant, f.Site, item.ItemId, f.User,
                new BrandKnowledgeItemUpdate(BrandKnowledgeDimension.Audience, "工程商", "市政与园区施工单位。", 1));
            row = await f.ReadSyncAsync(item.ItemId);
            Assert.Equal(("pending", 0, (string?)null), (row.Status, row.Attempts, row.Error));

            // 领取后内容被编辑再失败：改回 pending 立即重试。
            var raced = await f.ClaimMineAsync(item.ItemId);
            await f.Store.UpdateAsync(f.Tenant, f.Site, item.ItemId, f.User,
                new BrandKnowledgeItemUpdate(BrandKnowledgeDimension.Audience, "工程商", "市政、园区与工厂施工单位。", 2));
            await f.Store.FailSyncAsync(raced, "KNOWLEDGE_ITEM_SYNC_FAILED", "失败。", retryable: true);
            row = await f.ReadSyncAsync(item.ItemId);
            Assert.Equal(("pending", 0), (row.Status, row.Attempts));
            Assert.True(row.NextDelaySeconds <= 1);
        }
        finally { await f.CleanupAsync(); }
    }

    [BrandKnowledgeMySqlFact]
    public async Task SyncWorkerProjectsItemIntoKnowledgeDocumentVersionsAndDisablesOnDelete()
    {
        var f = await Fixture.CreateAsync();
        try
        {
            var worker = new KnowledgeItemSyncWorker(f.Store, f.Knowledge, NullLogger<KnowledgeItemSyncWorker>.Instance);
            var item = (await f.Store.CreateManyAsync(f.Tenant, f.Site, f.User,
                [Create(BrandKnowledgeDimension.ProductService, "3188DH", "管道检测仪，适用于 DN100 以上管道。")], BrandKnowledgeItemSource.Manual)).Single();

            await f.PrioritizeAsync(item.ItemId);
            Assert.True(await worker.RunOnceAsync());
            var row = await f.ReadSyncAsync(item.ItemId);
            Assert.Equal(("synced", 1), (row.Status, row.SyncedVersion));
            var documentId = row.DocumentId!.Value;
            var document = (await f.Knowledge.GetDocumentAsync(f.Tenant, documentId))!;
            Assert.Equal(("item", "product_service", item.ItemId.ToString(), (Guid?)f.Site), (document.SourceKind, document.DocumentType, document.ProductKey, document.SiteId));
            Assert.Equal($"item-{item.ItemId:N}.md", document.LatestVersion!.OriginalFileName);
            Assert.Equal(1, document.LatestVersion.VersionNumber);
            Assert.Equal(1, await f.ScalarAsync("SELECT COUNT(*) FROM knowledge_ingest_jobs WHERE tenant_id=UUID_TO_BIN(@tenant) AND document_id=UUID_TO_BIN(@document);",
                ("@tenant", f.Tenant.ToString()), ("@document", documentId.ToString())));

            // 编辑（含改维度）后在同一文档上传新版本，document_type 跟随维度。
            await f.Store.UpdateAsync(f.Tenant, f.Site, item.ItemId, f.User,
                new BrandKnowledgeItemUpdate(BrandKnowledgeDimension.Differentiator, "3188DH", "唯一支持 DN100 以上管道的便携型号。", 1));
            await f.PrioritizeAsync(item.ItemId);
            Assert.True(await worker.RunOnceAsync());
            row = await f.ReadSyncAsync(item.ItemId);
            Assert.Equal(("synced", 2, documentId), (row.Status, row.SyncedVersion, row.DocumentId!.Value));
            document = (await f.Knowledge.GetDocumentAsync(f.Tenant, documentId))!;
            Assert.Equal(2, document.LatestVersion!.VersionNumber);
            Assert.Equal("differentiator", document.DocumentType);
            Assert.Equal(1, await f.ScalarAsync("SELECT COUNT(*) FROM knowledge_documents WHERE tenant_id=UUID_TO_BIN(@tenant);", ("@tenant", f.Tenant.ToString())));

            // 删除后停用文档。
            await f.Store.DeleteAsync(f.Tenant, f.Site, item.ItemId, f.User);
            await f.PrioritizeAsync(item.ItemId);
            Assert.True(await worker.RunOnceAsync());
            Assert.Equal("synced", (await f.ReadSyncAsync(item.ItemId)).Status);
            Assert.Equal("disabled", (await f.Knowledge.GetDocumentAsync(f.Tenant, documentId))!.Status);
        }
        finally { await f.CleanupAsync(); }
    }

    [BrandKnowledgeMySqlFact]
    public async Task LegacyDocumentWritesCannotMutateItemProjectionButTheSyncWorkerCan()
    {
        var f = await Fixture.CreateAsync();
        try
        {
            var worker = new KnowledgeItemSyncWorker(f.Store, f.Knowledge, NullLogger<KnowledgeItemSyncWorker>.Instance);
            var item = (await f.Store.CreateManyAsync(f.Tenant, f.Site, f.User,
                [Create(BrandKnowledgeDimension.SellingPoint, "一键配网", "三秒完成配网。")], BrandKnowledgeItemSource.Manual)).Single();
            await f.PrioritizeAsync(item.ItemId);
            Assert.True(await worker.RunOnceAsync());
            var documentId = (await f.ReadSyncAsync(item.ItemId)).DocumentId!.Value;
            var before = (await f.Knowledge.GetDocumentAsync(f.Tenant, documentId))!;
            await f.ExecAsync("""
                UPDATE knowledge_document_versions SET status='failed' WHERE tenant_id=UUID_TO_BIN(@tenant) AND document_id=UUID_TO_BIN(@document);
                """, ("@tenant", f.Tenant.ToString()), ("@document", documentId.ToString()));

            await using var replacement = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("# 覆盖\n"));
            foreach (var write in new Func<Task>[]
                     {
                         () => f.Knowledge.UpdateDocumentAsync(f.Tenant, documentId, new KnowledgeDocumentUpdate("改名")),
                         () => f.Knowledge.RetryDocumentAsync(f.Tenant, documentId),
                         () => f.Knowledge.DisableDocumentAsync(f.Tenant, documentId),
                         () => f.Knowledge.CreateDocumentAsync(f.Tenant, f.Site, new KnowledgeUploadCommand(replacement, "replace.md", "text/markdown",
                             "覆盖", null, "general", null, null, "und", null, null, null, documentId)),
                         () => f.Knowledge.UploadProductDocumentAsync(f.Tenant, f.Site, item.ItemId, "覆盖", "# 覆盖\n", [], documentId)
                     })
            {
                var rejected = await Assert.ThrowsAsync<CollectorStoreException>(write);
                Assert.Equal("KNOWLEDGE_DOCUMENT_MANAGED_BY_ITEM", rejected.Code);
                Assert.Equal(409, rejected.StatusCode);
            }
            var after = (await f.Knowledge.GetDocumentAsync(f.Tenant, documentId))!;
            Assert.Equal((before.Title, before.Status, before.ConfigurationVersion, 1), (after.Title, after.Status, after.ConfigurationVersion, after.LatestVersion!.VersionNumber));

            // Worker 自己的路径不受影响：编辑后上传新版本，删除后经专用入口停用。
            await f.Store.UpdateAsync(f.Tenant, f.Site, item.ItemId, f.User,
                new BrandKnowledgeItemUpdate(BrandKnowledgeDimension.SellingPoint, "一键配网", "三秒完成配网，支持双频。", 1));
            await f.PrioritizeAsync(item.ItemId);
            Assert.True(await worker.RunOnceAsync());
            Assert.Equal(2, (await f.Knowledge.GetDocumentAsync(f.Tenant, documentId))!.LatestVersion!.VersionNumber);
            await f.Store.DeleteAsync(f.Tenant, f.Site, item.ItemId, f.User);
            await f.PrioritizeAsync(item.ItemId);
            Assert.True(await worker.RunOnceAsync());
            Assert.Equal("synced", (await f.ReadSyncAsync(item.ItemId)).Status);
            Assert.Equal("disabled", (await f.Knowledge.GetDocumentAsync(f.Tenant, documentId))!.Status);
        }
        finally { await f.CleanupAsync(); }
    }

    private static BrandKnowledgeItemCreate Create(BrandKnowledgeDimension dimension, string title, string content)
        => new(new BrandKnowledgeItemWrite(dimension, title, content));

    private sealed record SyncRow(string Status, int ContentVersion, int SyncedVersion, Guid? DocumentId, string? LeaseOwner, int Attempts,
        string? Error, double NextDelaySeconds);

    private sealed class Fixture
    {
        private readonly string connectionString;
        private readonly string storageRoot = Path.Combine(Path.GetTempPath(), $"jogeoai-brand-knowledge-sql-{Guid.NewGuid():N}");

        private Fixture(string connection)
        {
            connectionString = connection;
            var database = new MySqlDatabase(connection);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Knowledge:StorageRoot"] = storageRoot
            }).Build();
            Knowledge = new KnowledgeStore(database, new LocalObjectStorage(configuration), new DeterministicEmbeddingProvider(), new InMemoryVectorIndex());
            Store = new BrandKnowledgeItemStore(database);
        }

        public Guid Tenant { get; } = Guid.NewGuid();
        public Guid Site { get; } = Guid.NewGuid();
        public Guid User { get; } = Guid.NewGuid();
        public Guid OtherUser { get; } = Guid.NewGuid();
        public KnowledgeStore Knowledge { get; }
        public BrandKnowledgeItemStore Store { get; }

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
                VALUES (UUID_TO_BIN(@tenant), 'brand knowledge items fixture', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                INSERT INTO auth_users (id, email, password_hash, status, created_at, updated_at)
                VALUES (UUID_TO_BIN(@user), @email, 'not-a-login', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)),
                       (UUID_TO_BIN(@other), @other_email, 'not-a-login', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                INSERT INTO sites (id, tenant_id, host, display_name, status, created_at, updated_at)
                VALUES (UUID_TO_BIN(@site), UUID_TO_BIN(@tenant), NULL, 'Brand knowledge fixture', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                """, ("@tenant", fixture.Tenant.ToString()), ("@user", fixture.User.ToString()), ("@other", fixture.OtherUser.ToString()),
                ("@email", $"brand-knowledge-{fixture.User:N}@example.test"), ("@other_email", $"brand-knowledge-{fixture.OtherUser:N}@example.test"),
                ("@site", fixture.Site.ToString()));
            return fixture;
        }

        /// <summary>只删除本夹具租户、品牌与用户的行，以及本地对象存储临时目录；不触碰库内其他行。</summary>
        public async Task CleanupAsync()
        {
            var tenant = Tenant.ToString();
            foreach (var sql in new[]
                     {
                         "DELETE FROM brand_knowledge_items WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM brand_knowledge_extract_jobs WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM knowledge_ingest_jobs WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM knowledge_document_chunks WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM knowledge_document_tags WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "UPDATE knowledge_documents SET current_version_id=NULL WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM knowledge_document_versions WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM knowledge_documents WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM sites WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@site);",
                         "DELETE FROM auth_users WHERE id IN (UUID_TO_BIN(@user), UUID_TO_BIN(@other));",
                         "DELETE FROM tenants WHERE id=UUID_TO_BIN(@tenant);"
                     })
                await ExecAsync(sql, ("@tenant", tenant), ("@site", Site.ToString()), ("@user", User.ToString()), ("@other", OtherUser.ToString()));
            try { if (Directory.Exists(storageRoot)) Directory.Delete(storageRoot, recursive: true); }
            catch (IOException) { /* 临时目录清理失败不影响断言。 */ }
        }

        /// <summary>把本用例条目排到领取队列最前，保证领取到的是它而不是库内其他待同步行。</summary>
        public Task PrioritizeAsync(Guid itemId)
            => ExecAsync("UPDATE brand_knowledge_items SET sync_next_at='2000-01-01 00:00:00' WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@id);",
                ("@tenant", Tenant.ToString()), ("@id", itemId.ToString()));

        public async Task<BrandKnowledgeSyncLease> ClaimMineAsync(Guid itemId)
        {
            await PrioritizeAsync(itemId);
            var lease = await Store.ClaimSyncAsync("brand-knowledge-sql-test", TimeSpan.FromMinutes(2));
            Assert.NotNull(lease);
            Assert.Equal(itemId, lease!.ItemId);
            return lease;
        }

        public async Task<Guid> InsertExtractJobAsync(string url)
        {
            var id = Guid.NewGuid();
            await ExecAsync("""
                INSERT INTO brand_knowledge_extract_jobs (id, tenant_id, site_id, requested_by, url, url_hash, status, step, next_attempt_at,
                    created_at, updated_at, resolved_at)
                VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), UUID_TO_BIN(@user), @url, SHA2(@url, 256), 'saved', 'finalize',
                    UTC_TIMESTAMP(6), UTC_TIMESTAMP(6), UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                """, ("@id", id.ToString()), ("@tenant", Tenant.ToString()), ("@site", Site.ToString()), ("@user", User.ToString()), ("@url", url));
            return id;
        }

        public async Task<SyncRow> ReadSyncAsync(Guid itemId)
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new MySqlCommand("""
                SELECT sync_status, content_version, synced_version, BIN_TO_UUID(knowledge_document_id), sync_lease_owner, sync_attempts, sync_error,
                       TIMESTAMPDIFF(MICROSECOND, UTC_TIMESTAMP(6), sync_next_at) / 1000000
                FROM brand_knowledge_items WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@id);
                """, connection);
            command.Parameters.AddWithValue("@tenant", Tenant.ToString());
            command.Parameters.AddWithValue("@id", itemId.ToString());
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return new SyncRow(reader.GetString(0), Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture), reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4), Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture),
                reader.IsDBNull(6) ? null : reader.GetString(6), Convert.ToDouble(reader.GetValue(7), CultureInfo.InvariantCulture));
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

/// <summary>共用知识库隔离测试库、且会从全库领取租约行的 SQL 测试串行执行，避免并行测试互相领走对方的数据。</summary>
[CollectionDefinition("Knowledge isolated SQL", DisableParallelization = true)]
public sealed class KnowledgeIsolatedSqlCollection;
