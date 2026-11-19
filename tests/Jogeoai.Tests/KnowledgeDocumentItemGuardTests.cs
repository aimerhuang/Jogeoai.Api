using System.Text;
using System.Text.Json;
using Jogeoai.Api;
using Jogeoai.Api.Controllers;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Jogeoai.Tests;

/// <summary>
/// 无库测试：品牌知识条目的检索投影资料（source_kind=item）只由同步 Worker 写入。旧资料接口的修改、停用、重试与上传新版本
/// （公司级与品牌级路由）一律返回 409 KNOWLEDGE_DOCUMENT_MANAGED_BY_ITEM 且不调用 Store 写方法；读取不受影响；
/// Store 层的同一规则（锁内复查）抛出的 409 也按原状态输出。Store 的 SQL 行为见 BrandKnowledgeItemStoreSqlTests。
/// </summary>
public sealed class KnowledgeDocumentItemGuardTests
{
    private static readonly string[] WriteMethods =
    [
        nameof(IKnowledgeStore.UpdateDocumentAsync), nameof(IKnowledgeStore.RetryDocumentAsync), nameof(IKnowledgeStore.DisableDocumentAsync),
        nameof(IKnowledgeStore.DisableKnowledgeItemDocumentAsync), nameof(IKnowledgeStore.CreateDocumentAsync)
    ];

    [Fact]
    public void RuleRejectsOnlyItemProjectionDocuments()
    {
        foreach (var guard in new Action<string?>[] { BrandKnowledgeRules.AssertDocumentNotItemManaged, KnowledgeStore.AssertNotItemManaged })
        {
            var exception = Assert.Throws<CollectorStoreException>(() => guard("item"));
            Assert.Equal("KNOWLEDGE_DOCUMENT_MANAGED_BY_ITEM", exception.Code);
            Assert.Equal(409, exception.StatusCode);
            Assert.Contains("品牌知识库", exception.Message, StringComparison.Ordinal);
            foreach (var kind in new[] { "upload", "link", "product", null, "ITEM" }) guard(kind);
        }
    }

    [Fact]
    public async Task ItemOwnedDocumentsRejectEveryLegacyWriteOnBothRoutesButStayReadable()
    {
        var f = new Fixture();
        var siteDocument = f.Add(f.Site, "item");
        var tenantDocument = f.Add(null, "item");

        foreach (var result in new[]
                 {
                     await f.Controller.Update(tenantDocument, new KnowledgeDocumentUpdate("改名"), default),
                     await f.Controller.UpdateSite(f.Site, siteDocument, new KnowledgeDocumentUpdate("改名"), default),
                     await f.Controller.Retry(tenantDocument, default),
                     await f.Controller.RetrySite(f.Site, siteDocument, default),
                     await f.Controller.Disable(tenantDocument, default),
                     await f.Controller.DisableSite(f.Site, siteDocument, default),
#pragma warning disable CS0618 // 上传接口已标记 [Obsolete]，此处专门验证其兼容保护。
                     await f.Controller.UploadTenant(Form(tenantDocument), default),
                     await f.Controller.UploadSite(f.Site, Form(siteDocument), default)
#pragma warning restore CS0618
                 })
            AssertManagedByItem(result);
        Assert.DoesNotContain(f.Calls, call => WriteMethods.Contains(call));

        // 读接口不变。
        Assert.IsType<OkObjectResult>(await f.Controller.GetSite(f.Site, siteDocument, default));
        Assert.IsType<OkObjectResult>(await f.Controller.Get(tenantDocument, default));
    }

    [Fact]
    public async Task OrdinaryDocumentsStillReachTheStoreAndStoreLevelGuardKeepsItsStatus()
    {
        var f = new Fixture();
        var upload = f.Add(f.Site, "upload");

        Assert.IsType<OkObjectResult>(await f.Controller.UpdateSite(f.Site, upload, new KnowledgeDocumentUpdate("改名"), default));
        Assert.IsType<AcceptedResult>(await f.Controller.RetrySite(f.Site, upload, default));
        Assert.IsType<OkObjectResult>(await f.Controller.DisableSite(f.Site, upload, default));
#pragma warning disable CS0618
        Assert.IsType<AcceptedAtActionResult>(await f.Controller.UploadSite(f.Site, Form(upload), default));
#pragma warning restore CS0618
        Assert.Equal(1, f.Calls.Count(call => call == nameof(IKnowledgeStore.UpdateDocumentAsync)));
        Assert.Equal(1, f.Calls.Count(call => call == nameof(IKnowledgeStore.RetryDocumentAsync)));
        Assert.Equal(1, f.Calls.Count(call => call == nameof(IKnowledgeStore.DisableDocumentAsync)));
        Assert.Equal(1, f.Calls.Count(call => call == nameof(IKnowledgeStore.CreateDocumentAsync)));
        // 公开接口永远不走 Worker 专用的停用入口。
        Assert.DoesNotContain(nameof(IKnowledgeStore.DisableKnowledgeItemDocumentAsync), f.Calls);

        // 控制器读取时资料还不是条目投影、Store 在锁内复查时已是：Store 抛出的 409 原样输出。
        f.StoreSideKind = "item";
        AssertManagedByItem(await f.Controller.UpdateSite(f.Site, upload, new KnowledgeDocumentUpdate("改名"), default));
        AssertManagedByItem(await f.Controller.DisableSite(f.Site, upload, default));
        AssertManagedByItem(await f.Controller.RetrySite(f.Site, upload, default));
#pragma warning disable CS0618
        AssertManagedByItem(await f.Controller.UploadSite(f.Site, Form(upload), default));
#pragma warning restore CS0618
    }

    private static KnowledgeUploadForm Form(Guid documentId)
    {
        var bytes = Encoding.UTF8.GetBytes("# 新版本\n");
        return new KnowledgeUploadForm
        {
            DocumentId = documentId,
            File = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "replace.md") { Headers = new HeaderDictionary(), ContentType = "text/markdown" }
        };
    }

    private static void AssertManagedByItem(IActionResult result)
    {
        var response = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(409, response.StatusCode);
        var error = JsonSerializer.SerializeToElement(response.Value).GetProperty("error");
        Assert.Equal("KNOWLEDGE_DOCUMENT_MANAGED_BY_ITEM", error.GetProperty("code").GetString());
        Assert.Contains("品牌知识库", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    private sealed class Fixture
    {
        private readonly Dictionary<Guid, KnowledgeDocumentView> documents = [];

        internal Fixture()
        {
            var context = new DefaultHttpContext();
            context.Items[SessionAuthenticationMiddleware.ContextItemKey] = new AuthenticatedRequestContext(
                new(User, "fixture@example.test", "Fixture", "active", [new(Tenant, "Team", "owner", "active")]), Guid.NewGuid(), "fixture");
            Controller = new KnowledgeController(StudioFakes.Of<IKnowledgeStore>(Handle), StudioFakes.Unused<IKnowledgeRetrievalService>(),
                new TenantContextResolver(new TestEnvironment()))
            { ControllerContext = new() { HttpContext = context } };
        }

        internal Guid Tenant { get; } = Guid.NewGuid();
        internal Guid Site { get; } = Guid.NewGuid();
        internal Guid User { get; } = Guid.NewGuid();
        internal KnowledgeController Controller { get; }
        internal List<string> Calls { get; } = [];
        /// <summary>非空时模拟 Store 在锁内复查到的 source_kind（与控制器先读到的不同）。</summary>
        internal string? StoreSideKind { get; set; }

        internal Guid Add(Guid? siteId, string sourceKind)
        {
            var now = DateTimeOffset.UtcNow;
            var id = Guid.NewGuid();
            documents[id] = new KnowledgeDocumentView(id, Tenant, siteId, siteId is null ? "tenant" : "site", null, "资料", "ready", "und",
                sourceKind == "item" ? "selling_point" : "general", null, [], null, null, null, null, now, now, SourceKind: sourceKind);
            return id;
        }

        private object? Handle(string method, object?[]? args)
        {
            Calls.Add(method);
            Assert.Equal(Tenant, args![0]);
            switch (method)
            {
                case nameof(IKnowledgeStore.GetDocumentAsync):
                    return Task.FromResult(documents.GetValueOrDefault((Guid)args[1]!));
                case nameof(IKnowledgeStore.UpdateDocumentAsync):
                case nameof(IKnowledgeStore.RetryDocumentAsync):
                case nameof(IKnowledgeStore.DisableDocumentAsync):
                    StoreGuard();
                    return Task.FromResult<KnowledgeDocumentView?>(documents[(Guid)args[1]!]);
                case nameof(IKnowledgeStore.CreateDocumentAsync):
                    StoreGuard();
                    var command = (KnowledgeUploadCommand)args[2]!;
                    return Task.FromResult(documents[command.DocumentId!.Value]);
                default:
                    throw new InvalidOperationException($"Unexpected call IKnowledgeStore.{method}");
            }
        }

        private void StoreGuard() => BrandKnowledgeRules.AssertDocumentNotItemManaged(StoreSideKind);
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
