using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using MySqlConnector;

namespace Jogeoai.Tests;

/// <summary>文章生成「指定产品」解析：条目优先、未迁移的已入库旧产品兜底、已删除或已被迁移接管的产品不可解析。</summary>
[Collection("Knowledge isolated SQL")]
public sealed class ArticleProductResolverTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Site = Guid.NewGuid();

    [Fact]
    public async Task ItemIsResolvedFirstEvenWhenAMigratedLegacyProductSharesItsUuid()
    {
        var id = Guid.NewGuid();
        var item = new ArticleProductRef(id, Tenant, Site, "3188DH 条目标题", Guid.NewGuid());
        var resolver = Resolver(new() { [id] = item }, new() { [id] = Legacy(id, "3188DH", KnowledgeProductStatus.Approved) }, migrated: [id]);

        Assert.Equal(item, await resolver.Resolver.ResolveAsync(Tenant, Site, id));
        Assert.Equal(0, resolver.LegacyCalls);
    }

    [Fact]
    public async Task UnmigratedApprovedLegacyProductIsTheFallback()
    {
        var id = Guid.NewGuid();
        var legacy = Legacy(id, "Legacy-X", KnowledgeProductStatus.Approved);
        var resolver = Resolver([], new() { [id] = legacy }, migrated: []);

        var resolved = await resolver.Resolver.ResolveAsync(Tenant, Site, id);

        Assert.Equal(new ArticleProductRef(id, Tenant, Site, "Legacy-X", legacy.KnowledgeDocumentId), resolved);
    }

    [Theory]
    [InlineData(KnowledgeProductStatus.Draft)]
    [InlineData(KnowledgeProductStatus.PendingReview)]
    [InlineData(KnowledgeProductStatus.Archived)]
    public async Task UnapprovedLegacyProductIsNotResolved(KnowledgeProductStatus status)
    {
        var id = Guid.NewGuid();
        var resolver = Resolver([], new() { [id] = Legacy(id, "Draft-X", status) }, migrated: []);
        Assert.Null(await resolver.Resolver.ResolveAsync(Tenant, Site, id));
    }

    [Fact]
    public async Task DeletedMigratedItemCannotFallBackToItsStillApprovedLegacyProductAndMapsTo404()
    {
        var id = Guid.NewGuid();
        // 条目已删除（条目存储返回 null），旧产品仍是 approved：已被迁移接管，必须视为不存在。
        var resolver = Resolver([], new() { [id] = Legacy(id, "3188DH", KnowledgeProductStatus.Approved) }, migrated: [id]);
        Assert.Null(await resolver.Resolver.ResolveAsync(Tenant, Site, id));
        Assert.Null(await resolver.Resolver.ResolveAsync(Tenant, Site, Guid.NewGuid()));

        var service = new ArticleMaterialSelectionService(StudioFakes.Unused<IKnowledgeStore>(), StudioFakes.Unused<IKnowledgeRetrievalService>(),
            StudioFakes.Unused<IContentStore>(), null, resolver.Resolver);
        var error = await Assert.ThrowsAsync<CollectorStoreException>(() => service.PreviewAsync(Tenant, Site, Guid.NewGuid(),
            new ArticleMaterialPreviewRequest([Guid.NewGuid()], "zh-CN", KnowledgeSelection: new ArticleKnowledgeSelection(ProductKey: id.ToString()))));
        Assert.Equal(("PRODUCT_NOT_FOUND", 404, "指定产品不存在或已删除。"), (error.Code, error.StatusCode, error.Message));
    }

    [Fact]
    public async Task OptionsComeOnlyFromProductServiceItems()
    {
        var options = new KnowledgeProductOption[] { new(Guid.NewGuid(), "条目产品") };
        var items = StudioFakes.Of<IBrandKnowledgeItemStore>((method, _) => method == nameof(IBrandKnowledgeItemStore.ListProductOptionsAsync)
            ? Task.FromResult<IReadOnlyList<KnowledgeProductOption>>(options) : throw new InvalidOperationException(method));
        var resolver = new ArticleProductResolver(items, StudioFakes.Unused<IKnowledgeProductStore>());
        Assert.Equal(options, await resolver.ListOptionsAsync(Tenant, Site));
    }

    /// <summary>
    /// 真实 SQL：迁移条目沿用产品 UUID 与原文档、条目优先解析；未迁移的已入库旧产品兜底；草稿、非产品维度、未同步、
    /// 其他品牌与已删除条目都不可解析。夹具自建随机租户并在结束时只删除本租户的行。
    /// </summary>
    [BrandKnowledgeMySqlFact]
    public async Task ResolvesItemsAndLegacyProductsAgainstIsolatedDatabase()
    {
        var f = await SqlFixture.CreateAsync();
        try
        {
            var migratedDocument = await f.DocumentAsync("3188DH 产品资料");
            var legacyDocument = await f.DocumentAsync("旧产品资料");
            var itemDocument = await f.DocumentAsync("新条目资料");
            var introDocument = await f.DocumentAsync("品牌介绍资料");
            var migrated = await f.ProductAsync("3188DH", "approved", migratedDocument);
            var legacy = await f.ProductAsync("Legacy-X", "approved", legacyDocument);
            var draft = await f.ProductAsync("Draft-X", "pending_review", null);
            await f.ItemAsync(migrated, "product_service", "3188DH 管道摄像头", migratedDocument, legacyProduct: migrated);
            var newItem = await f.ItemAsync(Guid.NewGuid(), "product_service", "新产品条目", itemDocument);
            var intro = await f.ItemAsync(Guid.NewGuid(), "brand_intro", "品牌介绍", introDocument);
            var unsynced = await f.ItemAsync(Guid.NewGuid(), "product_service", "尚未同步", null);
            var resolver = new ArticleProductResolver(new BrandKnowledgeItemStore(f.Database),
                new KnowledgeProductStore(f.Database, StudioFakes.Unused<IKnowledgeStore>()));

            Assert.Equal(new ArticleProductRef(migrated, f.Tenant, f.Site, "3188DH 管道摄像头", migratedDocument),
                await resolver.ResolveAsync(f.Tenant, f.Site, migrated));
            Assert.Equal(new ArticleProductRef(newItem, f.Tenant, f.Site, "新产品条目", itemDocument),
                await resolver.ResolveAsync(f.Tenant, f.Site, newItem));
            Assert.Equal(new ArticleProductRef(legacy, f.Tenant, f.Site, "Legacy-X", legacyDocument),
                await resolver.ResolveAsync(f.Tenant, f.Site, legacy));
            Assert.Null(await resolver.ResolveAsync(f.Tenant, f.Site, draft));
            Assert.Null(await resolver.ResolveAsync(f.Tenant, f.Site, intro));
            Assert.Null(await resolver.ResolveAsync(f.Tenant, f.Site, unsynced));
            Assert.Null(await resolver.ResolveAsync(f.Tenant, Guid.NewGuid(), migrated));
            Assert.Null(await resolver.ResolveAsync(Guid.NewGuid(), f.Site, newItem));
            // 选项只来自「产品/服务」且已有投影文档的条目：未迁移旧产品、品牌介绍与未同步条目都不出现。
            Assert.Equal(new[] { migrated, newItem }.Order(), (await resolver.ListOptionsAsync(f.Tenant, f.Site)).Select(o => o.ProductId).Order());

            await f.SoftDeleteItemAsync(migrated);
            Assert.Null(await resolver.ResolveAsync(f.Tenant, f.Site, migrated));
            Assert.Equal([newItem], (await resolver.ListOptionsAsync(f.Tenant, f.Site)).Select(o => o.ProductId));
        }
        finally { await f.CleanupAsync(); }
    }

    private static KnowledgeProductView Legacy(Guid id, string name, KnowledgeProductStatus status)
        => new(id, Tenant, Site, name, status, "manual", null, null, null, null, [], null, [],
            new Dictionary<string, ProductFieldSource>(), null, null, Guid.NewGuid(), null, Guid.NewGuid(), null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static Harness Resolver(Dictionary<Guid, ArticleProductRef> items, Dictionary<Guid, KnowledgeProductView> legacy, Guid[] migrated)
    {
        var harness = new Harness();
        var itemStore = StudioFakes.Of<IBrandKnowledgeItemStore>((method, args) => method == nameof(IBrandKnowledgeItemStore.GetProductRefAsync)
            ? Task.FromResult(items.GetValueOrDefault((Guid)args![2]!) is { } item && item.TenantId == (Guid)args[0]! && item.SiteId == (Guid)args[1]! ? item : null)
            : throw new InvalidOperationException(method));
        var products = StudioFakes.Of<IKnowledgeProductStore>((method, args) =>
        {
            harness.LegacyCalls++;
            return method switch
            {
                nameof(IKnowledgeProductStore.GetAsync) => Task.FromResult(legacy.GetValueOrDefault((Guid)args![2]!)),
                nameof(IKnowledgeProductStore.IsMigratedAsync) => Task.FromResult(migrated.Contains((Guid)args![1]!)),
                _ => throw new InvalidOperationException(method),
            };
        });
        harness.Resolver = new ArticleProductResolver(itemStore, products);
        return harness;
    }

    private sealed class Harness
    {
        public ArticleProductResolver Resolver { get; set; } = null!;
        public int LegacyCalls { get; set; }
    }

    private sealed class SqlFixture
    {
        private readonly string connectionString;
        private SqlFixture(string connection) { connectionString = connection; Database = new MySqlDatabase(connection); }
        public MySqlDatabase Database { get; }
        public Guid Tenant { get; } = Guid.NewGuid();
        public Guid Site { get; } = Guid.NewGuid();
        public Guid User { get; } = Guid.NewGuid();

        public static async Task<SqlFixture> CreateAsync()
        {
            var value = Environment.GetEnvironmentVariable("JOGEOAI_KNOWLEDGE_TEST_CONNECTION");
            Assert.False(string.IsNullOrWhiteSpace(value), "未设置 JOGEOAI_KNOWLEDGE_TEST_CONNECTION；SQL 测试不会静默通过。");
            var builder = new MySqlConnectionStringBuilder(value!);
            Assert.False(string.IsNullOrWhiteSpace(builder.Database), "必须显式指定独立测试库。");
            Assert.NotEqual("jogeoai", builder.Database, StringComparer.OrdinalIgnoreCase);
            var f = new SqlFixture(value!);
            await f.ExecAsync("""
                INSERT INTO tenants (id, name, status, created_at, updated_at)
                VALUES (UUID_TO_BIN(@tenant), 'article product resolver fixture', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                INSERT INTO auth_users (id, email, password_hash, status, created_at, updated_at)
                VALUES (UUID_TO_BIN(@user), @email, 'not-a-login', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                INSERT INTO sites (id, tenant_id, host, display_name, status, created_at, updated_at)
                VALUES (UUID_TO_BIN(@site), UUID_TO_BIN(@tenant), NULL, 'Article product resolver fixture', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                """, ("@tenant", f.Tenant.ToString()), ("@user", f.User.ToString()), ("@site", f.Site.ToString()),
                ("@email", $"article-product-{f.User:N}@example.test"));
            return f;
        }

        public async Task<Guid> DocumentAsync(string title)
        {
            var id = Guid.NewGuid();
            await ExecAsync("""
                INSERT INTO knowledge_documents (id, tenant_id, site_id, title, status, created_at, updated_at)
                VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), @title, 'ready', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                """, ("@id", id.ToString()), ("@tenant", Tenant.ToString()), ("@site", Site.ToString()), ("@title", title));
            return id;
        }

        public async Task<Guid> ProductAsync(string name, string status, Guid? document)
        {
            var id = Guid.NewGuid();
            await ExecAsync("""
                INSERT INTO knowledge_products (id, tenant_id, site_id, name, status, source_kind, introduction, knowledge_document_id, approved_at,
                    created_by, updated_by, created_at, updated_at)
                VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), @name, @status, 'manual', '夹具产品介绍',
                    UUID_TO_BIN(@document), IF(@status='approved', UTC_TIMESTAMP(6), NULL), UUID_TO_BIN(@user), UUID_TO_BIN(@user),
                    UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                """, ("@id", id.ToString()), ("@tenant", Tenant.ToString()), ("@site", Site.ToString()), ("@name", name), ("@status", status),
                ("@document", document?.ToString()), ("@user", User.ToString()));
            return id;
        }

        public async Task<Guid> ItemAsync(Guid id, string dimension, string title, Guid? document, Guid? legacyProduct = null)
        {
            await ExecAsync("""
                INSERT INTO brand_knowledge_items (id, tenant_id, site_id, dimension, title, title_key, content, source_kind, legacy_product_id,
                    content_version, synced_version, knowledge_document_id, sync_status, sync_next_at, created_by, updated_by, created_at, updated_at)
                VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), @dimension, @title, @title_key, '夹具条目内容',
                    IF(@legacy IS NULL, 'manual', 'migrated'), UUID_TO_BIN(@legacy), 1, IF(@document IS NULL, 0, 1), UUID_TO_BIN(@document),
                    IF(@document IS NULL, 'pending', 'synced'), '2999-01-01 00:00:00', UUID_TO_BIN(@user), UUID_TO_BIN(@user),
                    UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                """, ("@id", id.ToString()), ("@tenant", Tenant.ToString()), ("@site", Site.ToString()), ("@dimension", dimension), ("@title", title),
                ("@title_key", BrandKnowledgeRules.TitleKey(title)), ("@legacy", legacyProduct?.ToString()), ("@document", document?.ToString()),
                ("@user", User.ToString()));
            return id;
        }

        public Task SoftDeleteItemAsync(Guid id) => ExecAsync("""
            UPDATE brand_knowledge_items SET deleted_at=UTC_TIMESTAMP(6), deleted_by=UUID_TO_BIN(@user), sync_status='pending'
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@id);
            """, ("@tenant", Tenant.ToString()), ("@id", id.ToString()), ("@user", User.ToString()));

        /// <summary>只删除本夹具租户、品牌与用户的行。</summary>
        public async Task CleanupAsync()
        {
            foreach (var sql in new[]
                     {
                         "DELETE FROM brand_knowledge_items WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM knowledge_products WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM knowledge_documents WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM sites WHERE tenant_id=UUID_TO_BIN(@tenant) AND id=UUID_TO_BIN(@site);",
                         "DELETE FROM auth_users WHERE id=UUID_TO_BIN(@user);",
                         "DELETE FROM tenants WHERE id=UUID_TO_BIN(@tenant);"
                     })
                await ExecAsync(sql, ("@tenant", Tenant.ToString()), ("@site", Site.ToString()), ("@user", User.ToString()));
        }

        private async Task ExecAsync(string sql, params (string Name, object? Value)[] parameters)
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new MySqlCommand(sql, connection);
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            await command.ExecuteNonQueryAsync();
        }
    }
}
