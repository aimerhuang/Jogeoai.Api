using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using MySqlConnector;

namespace Jogeoai.Tests;

/// <summary>完整提交回归：仅使用显式隔离测试库与人工素材，不调用 HTTP、模型或真实账户。</summary>
[Collection("Knowledge isolated SQL")]
public sealed class ArticleGenerationSubmissionSqlTests
{
    [ContentMySqlFact]
    public async Task RagSubmissionFreezesSelectedMaterialsAndClaimPreservesContextThroughCapture()
        => await SubmitAndCaptureAsync(withRag: true);

    [ContentMySqlFact]
    public async Task NoRagSubmissionBindsTitleAndCapturesExactlyOnceWithoutBrandMetadata()
        => await SubmitAndCaptureAsync(withRag: false);

    private static async Task SubmitAndCaptureAsync(bool withRag)
    {
        var f = await Fixture.CreateAsync();
        var preview = await f.PreviewAsync(withRag);
        var request = new ArticleGenerationSubmitRequest(preview.PreviewId, preview.RequestHash);
        var key = $"submit-{Guid.NewGuid():N}";
        var accepted = await f.Store.SubmitGenerationAsync(f.Tenant, f.Site, f.User, request, key);
        Assert.Equal(ArticleGenerationBatchStatus.Queued, accepted.Status);
        Assert.Equal(1, accepted.ArticleCount);
        Assert.Equal(100m, accepted.ReservedCredits);
        Assert.Equal(["900", "100", "0"], await f.AccountAsync());
        Assert.Equal(1, await f.CountAsync("content_articles"));
        var articles = await f.Store.ListArticlesAsync(f.Tenant, f.Site, new ArticleListFilter());
        Assert.Equal(f.Prompt.Content, Assert.Single(articles.Items).Title);

        // Accepted replays must survive preview expiry and cannot reserve twice.
        await f.ExecAsync("UPDATE article_generation_previews SET expires_at=DATE_SUB(UTC_TIMESTAMP(6),INTERVAL 1 MINUTE) WHERE tenant_id=UUID_TO_BIN(@tenant);", ("@tenant", f.Tenant.ToString()));
        Assert.Equal(accepted.BatchId, (await f.Store.SubmitGenerationAsync(f.Tenant, f.Site, f.User, request, key)).BatchId);
        Assert.Equal("IDEMPOTENCY_CONFLICT", (await Assert.ThrowsAsync<CollectorStoreException>(() =>
            f.Store.SubmitGenerationAsync(f.Tenant, f.Site, f.User, request, key + "-other"))).Code);
        Assert.Equal(["900", "100", "0"], await f.AccountAsync());

        // Freeze is before later edits. Worker must not re-read mutable product/brand fields.
        await f.ExecAsync("UPDATE sites SET description='changed-after-submit' WHERE tenant_id=UUID_TO_BIN(@tenant); UPDATE knowledge_products SET introduction='changed-after-submit' WHERE tenant_id=UUID_TO_BIN(@tenant);", ("@tenant", f.Tenant.ToString()));
        var work = await f.ClaimAsync(accepted.BatchId);
        Assert.Equal(!withRag, work.WithoutRag);
        Assert.Equal(withRag ? f.Context.ContextId : (Guid?)null, work.ContextId);
        Assert.Equal("product_selection", work.Strategy);
        Assert.Equal(["合成选型"], work.Keywords);
        var materials = ArticleWritingMaterialsSnapshot.Read(work.KnowledgeSelectionSnapshot);
        if (withRag)
        {
            Assert.Equal("合成品牌介绍", materials!.Brand!.Description);
            Assert.Equal(f.Product, materials.Product!.ProductId);
            Assert.Equal([f.Evidence.VersionId], materials.Product.VersionIds);
            var rehydrated = JsonSerializer.Deserialize<KnowledgeContextResponse>(work.ContextSnapshot!, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.Equal(KnowledgeContextSnapshot.Hash(f.Context), KnowledgeContextSnapshot.Hash(rehydrated!));
            Assert.DoesNotContain("changed-after-submit", ArticleGenerationProvider.BuildPrompt(work, rehydrated!.Items, []).User);
        }
        else
        {
            Assert.Null(materials);
            Assert.Null(work.ContextSnapshot);
        }

        var payload = new ArticleGenerationProvider.ArticleGenerationResultPayload("合成选型指南", null, new string('字', 2100), null, null,
            ["合成选型"], true, withRag ? [f.Evidence.EvidenceId] : [], withRag ? [] : ["未使用知识库，参数待核验"], "AI辅助生成");
        var result = ArticleGenerationProvider.Validate(payload, work, withRag ? new HashSet<string> { f.Evidence.EvidenceId } : [], "synthetic-output");
        await f.Store.CompleteGenerationTaskAsync(work, result);
        await f.Store.CompleteGenerationTaskAsync(work, result);
        var batch = (await f.Store.GetGenerationBatchAsync(f.Tenant, f.Site, accepted.BatchId))!;
        Assert.Equal(ArticleGenerationBatchStatus.Succeeded, batch.Status);
        Assert.Equal(CreditReservationStatus.Captured, Assert.Single(batch.Tasks).ReservationStatus);
        Assert.Equal(["900", "0", "100"], await f.AccountAsync());
        Assert.Equal(1, await f.CountAsync("content_article_versions"));
        Assert.Equal(1, await f.LedgerCountAsync("capture"));
        Assert.Equal(1, await f.LedgerCountAsync("reserve"));
    }

    [ContentMySqlFact]
    public async Task SubmittedTaskFailureReleasesOnceAndInsufficientCreditDoesNotCreateWork()
    {
        var f = await Fixture.CreateAsync();
        var preview = await f.PreviewAsync(withRag: false);
        var request = new ArticleGenerationSubmitRequest(preview.PreviewId, preview.RequestHash);
        await f.ExecAsync("UPDATE credit_accounts SET available_credits=99 WHERE tenant_id=UUID_TO_BIN(@tenant);", ("@tenant", f.Tenant.ToString()));
        Assert.Equal("CREDIT_INSUFFICIENT", (await Assert.ThrowsAsync<CollectorStoreException>(() =>
            f.Store.SubmitGenerationAsync(f.Tenant, f.Site, f.User, request, "insufficient"))).Code);
        Assert.Equal(0, await f.CountAsync("article_generation_batches"));
        Assert.Equal(0, await f.CountAsync("content_articles"));
        Assert.Equal(0, await f.CountAsync("credit_ledger"));
        await f.ExecAsync("UPDATE credit_accounts SET available_credits=1000 WHERE tenant_id=UUID_TO_BIN(@tenant);", ("@tenant", f.Tenant.ToString()));
        var accepted = await f.Store.SubmitGenerationAsync(f.Tenant, f.Site, f.User, request, "now-funded");
        var work = await f.ClaimAsync(accepted.BatchId);
        await f.Store.FailGenerationTaskAsync(work, "ARTICLE_GENERATION_FAILED", "人工测试失败", retryable: false);
        await f.Store.FailGenerationTaskAsync(work, "ARTICLE_GENERATION_FAILED", "人工测试重放", retryable: false);
        Assert.Equal(["1000", "0", "0"], await f.AccountAsync());
        Assert.Equal(1, await f.LedgerCountAsync("release"));
        Assert.Equal(0, await f.CountAsync("content_article_versions"));
        var batch = (await f.Store.GetGenerationBatchAsync(f.Tenant, f.Site, accepted.BatchId))!;
        Assert.Equal(ArticleGenerationBatchStatus.Failed, batch.Status);
        Assert.Equal(CreditReservationStatus.Released, Assert.Single(batch.Tasks).ReservationStatus);
    }

    [ContentMySqlFact]
    public async Task StaleContextAndUnavailableProviderFailBeforeReservation()
    {
        var f = await Fixture.CreateAsync();
        var preview = await f.PreviewAsync(withRag: true);
        await f.ExecAsync("UPDATE knowledge_context_requests SET snapshot_json=JSON_SET(snapshot_json,'$.Gaps',JSON_ARRAY('changed-after-preview')) WHERE tenant_id=UUID_TO_BIN(@tenant);", ("@tenant", f.Tenant.ToString()));
        Assert.Equal("KNOWLEDGE_CONTEXT_STALE", (await Assert.ThrowsAsync<CollectorStoreException>(() =>
            f.Store.SubmitGenerationAsync(f.Tenant, f.Site, f.User, new(preview.PreviewId, preview.RequestHash), "expired"))).Code);
        Assert.Equal(0, await f.CountAsync("credit_ledger"));
        var noRag = await f.PreviewAsync(withRag: false);
        f.Provider.Available = false;
        Assert.Equal("ARTICLE_PROVIDER_UNAVAILABLE", (await Assert.ThrowsAsync<CollectorStoreException>(() =>
            f.Store.SubmitGenerationAsync(f.Tenant, f.Site, f.User, new(noRag.PreviewId, noRag.RequestHash), "provider-missing"))).Code);
        Assert.Equal(0, await f.CountAsync("article_generation_batches"));
        Assert.Equal(["1000", "0", "0"], await f.AccountAsync());
    }

    private sealed class Fixture
    {
        private readonly string connectionString;
        private readonly ManagementStore management;
        internal ContentStore Store { get; }
        internal ProviderStub Provider { get; }
        internal Guid Tenant { get; } = Guid.NewGuid();
        internal Guid User { get; } = Guid.NewGuid();
        internal Guid Product { get; } = Guid.NewGuid();
        internal Guid Site { get; private set; }
        internal Guid Target { get; private set; }
        internal PromptView Prompt { get; private set; } = null!;
        internal KnowledgeEvidenceView Evidence { get; private set; } = null!;
        internal KnowledgeContextResponse Context { get; private set; } = null!;

        private Fixture(string connection)
        {
            connectionString = connection;
            var database = new MySqlDatabase(connection);
            management = new ManagementStore(database);
            var knowledge = new KnowledgeStore(database, null!, null!, null!);
            var research = DispatchProxy.Create<IResearchStore, ProviderStub>();
            Provider = (ProviderStub)research;
            Store = new ContentStore(database, management, new CreditStore(database), research, null!, knowledge, knowledge,
                new ArticleProductResolver(new BrandKnowledgeItemStore(database), new KnowledgeProductStore(database, knowledge, management)));
        }

        internal static async Task<Fixture> CreateAsync()
        {
            var value = Environment.GetEnvironmentVariable("JOGEOAI_CONTENT_TEST_CONNECTION");
            Assert.False(string.IsNullOrWhiteSpace(value));
            var builder = new MySqlConnectionStringBuilder(value!);
            Assert.Contains("_test", builder.Database, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual("jogeoai", builder.Database, StringComparer.OrdinalIgnoreCase);
            // Missing parameters must fail, not silently become MySQL user variables.
            builder.AllowUserVariables = false;
            builder.GuidFormat = MySqlGuidFormat.Default;
            var f = new Fixture(builder.ConnectionString);
            await f.ExecAsync("""
                INSERT INTO tenants (id,name,status,created_at,updated_at) VALUES (UUID_TO_BIN(@tenant),'article submit artificial fixture','active',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
                INSERT INTO auth_users (id,email,password_hash,status,created_at,updated_at) VALUES (UUID_TO_BIN(@user),@email,'not-a-login','active',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
                INSERT INTO credit_accounts (tenant_id,available_credits,updated_at) VALUES (UUID_TO_BIN(@tenant),1000,UTC_TIMESTAMP(6));
                """, ("@tenant", f.Tenant.ToString()), ("@user", f.User.ToString()), ("@email", $"submit-{f.User:N}@example.test"));
            f.Site = (await f.management.CreateBrandAsync(f.Tenant, new BrandWrite("合成品牌", "synthetic-article.test", Description: "合成品牌介绍"))).SiteId;
            f.Prompt = await f.management.CreatePromptAsync(f.Tenant, f.Site, new PromptWrite("合成品类如何选型？", "zh-CN"));
            var targets = await f.management.GetSiteTargetsAsync(f.Tenant, f.Site);
            if (targets.Items.All(item => !item.Enabled || item.TargetId is null))
                targets = await f.management.PutSiteTargetsAsync(f.Tenant, f.Site, new SiteTargetsWrite(targets.ConfigVersion, [new SiteTargetWrite("cloro", "chatgpt")]));
            f.Target = targets.Items.First(item => item.Enabled && item.TargetId is not null).TargetId!.Value;
            f.Evidence = new("synthetic-product-e1", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "合成产品资料", "合成参数续航4小时，仅为测试夹具", "site", "synthetic.md", "第1段", 0.2345678901234567, null, null, f.Product.ToString());
            f.Context = new(Guid.NewGuid(), [f.Evidence], ["缺报价"], [], KnowledgeScopeMode.BrandOnly, f.Evidence.Text.Length);
            await f.ExecAsync("""
                INSERT INTO knowledge_documents (id,tenant_id,site_id,title,status,created_at,updated_at) VALUES (UUID_TO_BIN(@document),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),'人工产品资料','ready',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
                INSERT INTO knowledge_products (id,tenant_id,site_id,name,status,source_kind,introduction,knowledge_document_id,approved_at,created_by,updated_by,created_at,updated_at)
                VALUES (UUID_TO_BIN(@product),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),'Synthetic-X','approved','manual','可变参数不得替代冻结证据',UUID_TO_BIN(@document),UTC_TIMESTAMP(6),UUID_TO_BIN(@user),UUID_TO_BIN(@user),UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
                INSERT INTO knowledge_context_requests (id,tenant_id,site_id,query_summary,scope_mode,filters_hash,requested_chunk_count,character_count,snapshot_json,created_at)
                VALUES (UUID_TO_BIN(@context),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),'synthetic','brand_only',REPEAT('b',64),1,@characters,@snapshot,UTC_TIMESTAMP(6));
                """, ("@tenant", f.Tenant.ToString()), ("@site", f.Site.ToString()), ("@user", f.User.ToString()), ("@product", f.Product.ToString()),
                ("@document", f.Evidence.DocumentId.ToString()), ("@context", f.Context.ContextId.ToString()), ("@characters", f.Context.CharacterCount), ("@snapshot", JsonSerializer.Serialize(f.Context)));
            return f;
        }

        internal async Task<ArticleGenerationPreviewView> PreviewAsync(bool withRag)
        {
            Guid? materialId = null;
            if (withRag)
            {
                var material = new ArticleMaterialPreviewView(Guid.NewGuid(), Context.ContextId, KnowledgeContextSnapshot.Hash(Context),
                    new ArticleKnowledgeSelection(KnowledgeScopeMode.BrandOnly, ProductKey: Product.ToString()),
                    [new(Evidence.EvidenceId, Evidence.DocumentId, Evidence.VersionId, Evidence.Title, Evidence.OriginalFileName, Evidence.Location, Evidence.Scope, [], Evidence.Score, Evidence.Text.Length)],
                    Context.Gaps, [], 1, Context.CharacterCount, DateTimeOffset.UtcNow.AddMinutes(15), false, PromptIds: [Prompt.PromptId], Language: "zh-CN");
                materialId = (await Store.SaveMaterialPreviewAsync(Tenant, Site, User, material)).MaterialPreviewId;
            }
            var request = new ArticleGenerationPreviewRequest([Prompt.PromptId], [Target], Strategy: "product_selection", Keywords: ["合成选型"], MaterialPreviewId: materialId, WithoutRag: !withRag);
            var preview = new ArticleGenerationPreviewView(Guid.NewGuid(), $"{Guid.NewGuid():N}{Guid.NewGuid():N}", request.PromptIds, request.TargetIds,
                1, 100, 100, 1000, DateTimeOffset.UtcNow.AddMinutes(15), [], [], materialId, !withRag);
            return await Store.SaveGenerationPreviewAsync(Tenant, Site, User, request, preview);
        }

        internal async Task<ArticleGenerationWorkItem> ClaimAsync(Guid batch)
        {
            // Prioritize only this fixture over retained diagnostic rows (including expired
            // leases from an earlier failing run); other tenants' rows remain untouched.
            var oldest = (await RowAsync("SELECT DATE_FORMAT(DATE_SUB(MIN(created_at),INTERVAL 1 SECOND),'%Y-%m-%d %H:%i:%s.%f') FROM article_generation_tasks WHERE @tenant IS NOT NULL;"))[0];
            await ExecAsync("UPDATE article_generation_tasks SET created_at=@created WHERE tenant_id=UUID_TO_BIN(@tenant) AND batch_id=UUID_TO_BIN(@batch);", ("@tenant", Tenant.ToString()), ("@batch", batch.ToString()), ("@created", oldest));
            var work = await Store.ClaimGenerationTaskAsync($"synthetic-{Guid.NewGuid():N}", TimeSpan.FromMinutes(10));
            Assert.NotNull(work);
            Assert.Equal(batch, work.BatchId);
            return work;
        }
        internal async Task<string[]> AccountAsync() => await RowAsync("SELECT CAST(available_credits AS SIGNED),CAST(reserved_credits AS SIGNED),CAST(lifetime_captured AS SIGNED) FROM credit_accounts WHERE tenant_id=UUID_TO_BIN(@tenant);");
        internal async Task<int> CountAsync(string table) => int.Parse((await RowAsync($"SELECT COUNT(*) FROM {table} WHERE tenant_id=UUID_TO_BIN(@tenant);"))[0], CultureInfo.InvariantCulture);
        internal async Task<int> LedgerCountAsync(string kind) => int.Parse((await RowAsync("SELECT COUNT(*) FROM credit_ledger WHERE tenant_id=UUID_TO_BIN(@tenant) AND entry_type=@kind;", ("@kind", kind)))[0], CultureInfo.InvariantCulture);
        private async Task<string[]> RowAsync(string sql, params (string Name, object? Value)[] parameters)
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new MySqlCommand(sql, connection);
            command.Parameters.AddWithValue("@tenant", Tenant.ToString());
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return Enumerable.Range(0, reader.FieldCount).Select(i => Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? "").ToArray();
        }
        internal async Task ExecAsync(string sql, params (string Name, object? Value)[] parameters)
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new MySqlCommand(sql, connection);
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            await command.ExecuteNonQueryAsync();
        }
        // Keep artificial fixtures in the explicitly isolated DB for audit. Database/container
        // cleanup is the task owner's separately confirmed operation, never a test side effect.
    }

    public class ProviderStub : DispatchProxy
    {
        internal bool Available { get; set; } = true;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == nameof(IResearchStore.GetProviderConfigurationAsync))
                return Task.FromResult<ResearchProviderConfiguration?>(Available ? new(ArticleGenerationProvider.ProviderName, "ignored", "https://example.test/no-http", "synthetic-configuration", false, 30, 1) : null);
            throw new InvalidOperationException($"Unexpected research call: {method?.Name}");
        }
    }
}
