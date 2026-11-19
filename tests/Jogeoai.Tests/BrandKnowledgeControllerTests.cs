using System.Reflection;
using System.Text.Json;
using Jogeoai.Api;
using Jogeoai.Api.Controllers;
using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Jogeoai.Tests;

/// <summary>
/// 无库控制器测试：品牌知识库的角色权限矩阵（viewer / member 本人 / member 他人 / admin / owner）、204 当前任务、
/// 保存只走原子保存入口且错误码按状态输出，以及旧产品接口对已迁移产品返回 409 PRODUCT_MIGRATED。
/// Store 由 DispatchProxy 记录调用，未预期的调用直接失败。
/// </summary>
public sealed class BrandKnowledgeControllerTests
{
    private static readonly string[] WriteMethods =
    [
        nameof(IBrandKnowledgeItemStore.CreateManyAsync), nameof(IBrandKnowledgeItemStore.UpdateAsync), nameof(IBrandKnowledgeItemStore.DeleteAsync),
        nameof(IKnowledgeExtractJobStore.EnqueueAsync), nameof(IKnowledgeExtractJobStore.CancelAsync),
        nameof(IKnowledgeExtractJobStore.SaveCandidatesAsync), nameof(IKnowledgeExtractJobStore.MarkResolvedAsync)
    ];

    [Fact]
    public async Task AnonymousRequestsGet401WithoutStoreCalls()
    {
        var f = new Fixture(null);

        AssertError(await f.Controller.ListItems(f.Site), 401, "AUTHENTICATION_REQUIRED");
        AssertError(await f.Controller.CreateItem(f.Site, Write(), default), 401, "AUTHENTICATION_REQUIRED");
        Assert.Empty(f.Calls);
    }

    [Fact]
    public async Task ViewerReadsEverythingButEveryWriteIs403WithoutWriting()
    {
        var f = new Fixture("viewer");
        f.Current = Job("done");

        var list = Assert.IsType<OkObjectResult>(await f.Controller.ListItems(f.Site, BrandKnowledgeDimension.Faq));
        Assert.IsType<BrandKnowledgeItemList>(list.Value);
        Assert.Equal(BrandKnowledgeDimension.Faq, f.LastDimension);
        Assert.IsType<OkObjectResult>(await f.Controller.GetCurrentExtractJob(f.Site, default));
        Assert.IsType<OkObjectResult>(await f.Controller.GetExtractJob(f.Site, f.JobId, default));

        var item = f.AddItem(createdBy: f.User);
        foreach (var result in new[]
                 {
                     await f.Controller.CreateItem(f.Site, Write(), default),
                     await f.Controller.UpdateItem(f.Site, item, Update(), default),
                     await f.Controller.DeleteItem(f.Site, item, default),
                     await f.Controller.CreateExtractJob(f.Site, new KnowledgeExtractRequest("https://example.test/about"), default),
                     await f.Controller.CancelExtractJob(f.Site, f.JobId, default),
                     await f.Controller.SaveExtractJob(f.Site, f.JobId, SaveRequest("1"), default),
                     await f.Controller.DiscardExtractJob(f.Site, f.JobId, default)
                 })
            AssertError(result, 403, "CONTENT_FORBIDDEN");
        Assert.DoesNotContain(f.Calls, call => WriteMethods.Contains(call));
    }

    [Fact]
    public async Task MemberEditsAndDeletesOnlyOwnItemsButMayCreateExtractSaveAndDiscard()
    {
        var f = new Fixture("member");
        var own = f.AddItem(createdBy: f.User);
        var others = f.AddItem(createdBy: Guid.NewGuid());

        Assert.IsType<OkObjectResult>(await f.Controller.UpdateItem(f.Site, own, Update(), default));
        Assert.IsType<NoContentResult>(await f.Controller.DeleteItem(f.Site, own, default));
        Assert.Equal(1, f.Count(nameof(IBrandKnowledgeItemStore.UpdateAsync)));
        Assert.Equal(1, f.Count(nameof(IBrandKnowledgeItemStore.DeleteAsync)));

        AssertError(await f.Controller.UpdateItem(f.Site, others, Update(), default), 403, "CONTENT_FORBIDDEN");
        AssertError(await f.Controller.DeleteItem(f.Site, others, default), 403, "CONTENT_FORBIDDEN");
        Assert.Equal(1, f.Count(nameof(IBrandKnowledgeItemStore.UpdateAsync)));
        Assert.Equal(1, f.Count(nameof(IBrandKnowledgeItemStore.DeleteAsync)));

        var created = Assert.IsType<ObjectResult>(await f.Controller.CreateItem(f.Site, Write(), default));
        Assert.Equal(201, created.StatusCode);
        Assert.Equal(f.User, Assert.IsType<BrandKnowledgeItemView>(created.Value).CreatedBy);
        Assert.Equal("manual", f.LastSource!.Kind);

        var accepted = Assert.IsType<AcceptedAtActionResult>(await f.Controller.CreateExtractJob(f.Site, new KnowledgeExtractRequest("https://example.test/about"), default));
        Assert.Equal(nameof(BrandKnowledgeController.GetExtractJob), accepted.ActionName);
        Assert.IsType<OkObjectResult>(await f.Controller.CancelExtractJob(f.Site, f.JobId, default));
        var saved = Assert.IsType<ObjectResult>(await f.Controller.SaveExtractJob(f.Site, f.JobId, SaveRequest("1"), default));
        Assert.Equal(201, saved.StatusCode);
        Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<BrandKnowledgeItemView>>(saved.Value));
        Assert.IsType<OkObjectResult>(await f.Controller.DiscardExtractJob(f.Site, f.JobId, default));
        Assert.Equal("discarded", f.LastResolution);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("owner")]
    public async Task AdminAndOwnerEditAndDeleteAnyItem(string role)
    {
        var f = new Fixture(role);
        var others = f.AddItem(createdBy: Guid.NewGuid());

        Assert.IsType<OkObjectResult>(await f.Controller.UpdateItem(f.Site, others, Update(), default));
        Assert.IsType<NoContentResult>(await f.Controller.DeleteItem(f.Site, others, default));
    }

    [Theory]
    [InlineData("member")]
    [InlineData("owner")]
    public async Task ArchivedBrandItemWritesAre404ResourceNotFoundWithoutWriting(string role)
    {
        var f = new Fixture(role) { SiteArchived = true };
        var own = f.AddItem(createdBy: f.User);

        AssertError(await f.Controller.UpdateItem(f.Site, own, Update(), default), 404, "RESOURCE_NOT_FOUND");
        AssertError(await f.Controller.DeleteItem(f.Site, own, default), 404, "RESOURCE_NOT_FOUND");
        Assert.Equal(2, f.Count(nameof(IBrandKnowledgeItemStore.GetAsync)));
        Assert.DoesNotContain(f.Calls, call => WriteMethods.Contains(call));
    }

    [Fact]
    public async Task MissingItemIs404AndStoreErrorsKeepTheirStatus()
    {
        var f = new Fixture("owner");

        AssertError(await f.Controller.UpdateItem(f.Site, Guid.NewGuid(), Update(), default), 404, "KNOWLEDGE_ITEM_NOT_FOUND");
        AssertError(await f.Controller.DeleteItem(f.Site, Guid.NewGuid(), default), 404, "KNOWLEDGE_ITEM_NOT_FOUND");
        AssertError(await f.Controller.GetExtractJob(f.Site, Guid.NewGuid(), default), 404, "KNOWLEDGE_EXTRACT_NOT_FOUND");

        foreach (var (code, status) in new[]
                 {
                     ("KNOWLEDGE_ITEM_TITLE_CONFLICT", 409), ("KNOWLEDGE_ITEM_LIMIT", 409), ("KNOWLEDGE_ITEM_VALIDATION", 400),
                     ("RESOURCE_NOT_FOUND", 404)
                 })
        {
            f.Failure = new CollectorStoreException(code, "测试错误。", status);
            AssertError(await f.Controller.CreateItem(f.Site, Write(), default), status, code);
        }
        foreach (var (code, status) in new[]
                 {
                     ("KNOWLEDGE_EXTRACT_IN_PROGRESS", 409), ("KNOWLEDGE_EXTRACT_RATE_LIMITED", 429), ("KNOWLEDGE_EXTRACT_STATE_CONFLICT", 409)
                 })
        {
            f.Failure = new CollectorStoreException(code, "测试错误。", status);
            AssertError(code == "KNOWLEDGE_EXTRACT_STATE_CONFLICT"
                ? await f.Controller.CancelExtractJob(f.Site, f.JobId, default)
                : await f.Controller.CreateExtractJob(f.Site, new KnowledgeExtractRequest("https://example.test/about"), default), status, code);
        }
    }

    [Fact]
    public async Task CurrentJobIs204WhenNoneAnd200WhenPresent()
    {
        var f = new Fixture("member");

        Assert.IsType<NoContentResult>(await f.Controller.GetCurrentExtractJob(f.Site, default));
        f.Current = Job("failed");
        var ok = Assert.IsType<OkObjectResult>(await f.Controller.GetCurrentExtractJob(f.Site, default));
        Assert.Equal("failed", Assert.IsType<KnowledgeExtractJobView>(ok.Value).Status);
    }

    [Fact]
    public async Task SaveUsesOnlyTheAtomicStoreMethodAndMapsForeignCandidateNotReadyAndExpired()
    {
        var f = new Fixture("member");

        var saved = Assert.IsType<ObjectResult>(await f.Controller.SaveExtractJob(f.Site, f.JobId, SaveRequest("1"), default));
        Assert.Equal(201, saved.StatusCode);
        Assert.Equal(f.JobId, f.LastSaveJob);
        Assert.Equal(f.User, f.LastSaveUser);
        Assert.Equal(new[] { nameof(IKnowledgeExtractJobStore.SaveCandidatesAsync) }, f.Calls);

        foreach (var (code, status) in new[]
                 {
                     ("KNOWLEDGE_ITEM_VALIDATION", 400), ("KNOWLEDGE_EXTRACT_NOT_READY", 409), ("KNOWLEDGE_EXTRACT_EXPIRED", 410),
                     ("KNOWLEDGE_ITEM_LIMIT", 409), ("KNOWLEDGE_EXTRACT_NOT_FOUND", 404)
                 })
        {
            f.Failure = new CollectorStoreException(code, "测试错误。", status);
            AssertError(await f.Controller.SaveExtractJob(f.Site, f.JobId, SaveRequest("999"), default), status, code);
        }
        Assert.DoesNotContain(f.Calls, call => call is nameof(IKnowledgeExtractJobStore.MarkResolvedAsync)
            or nameof(IBrandKnowledgeItemStore.CreateManyAsync));
    }

    [Fact]
    public async Task ExtractIsRejectedWith503WhenProviderIsNotConfigured()
    {
        var f = new Fixture("owner") { Configured = false };

        AssertError(await f.Controller.CreateExtractJob(f.Site, new KnowledgeExtractRequest("https://example.test/about"), default),
            503, "KNOWLEDGE_EXTRACT_NOT_CONFIGURED");
        Assert.DoesNotContain(nameof(IKnowledgeExtractJobStore.EnqueueAsync), f.Calls);
    }

    [Fact]
    public async Task LegacyProductWritesReturnProductMigratedButReadsStillWork()
    {
        var f = new ProductFixture(migrated: true);
#pragma warning disable CS0618 // 旧产品接口已标记 [Obsolete]，此处专门验证其兼容保护。
        var controller = f.Controller;
#pragma warning restore CS0618
        var job = Guid.NewGuid();
        foreach (var result in new[]
                 {
                     await controller.Update(f.Site, f.Product, new KnowledgeProductWrite("3188DH"), default),
                     await controller.Parse(f.Site, f.Product, new ProductParseRequest(ProductParseJobKind.Completion), default),
                     await controller.CancelJob(f.Site, f.Product, job, default),
                     await controller.Approve(f.Site, f.Product, new ProductApproveRequest(new KnowledgeProductWrite("3188DH"), []), default),
                     await controller.Reject(f.Site, f.Product, default),
                     await controller.Archive(f.Site, f.Product, default),
                     await controller.SyncKnowledge(f.Site, f.Product, default)
                 })
        {
            AssertError(result, 409, "PRODUCT_MIGRATED");
            var message = JsonSerializer.SerializeToElement(Assert.IsAssignableFrom<ObjectResult>(result).Value)
                .GetProperty("error").GetProperty("message").GetString();
            Assert.Contains("品牌知识库", message, StringComparison.Ordinal);
        }
        Assert.Equal(7, f.Calls.Count(call => call == nameof(IKnowledgeProductStore.IsMigratedAsync)));
        Assert.All(f.Calls, call => Assert.Contains(call, new[] { nameof(IKnowledgeProductStore.GetAsync), nameof(IKnowledgeProductStore.IsMigratedAsync) }));

        Assert.IsType<OkObjectResult>(await controller.Get(f.Site, f.Product, default));

        // 未迁移的产品不受影响。
        var fresh = new ProductFixture(migrated: false);
#pragma warning disable CS0618
        Assert.IsType<OkObjectResult>(await fresh.Controller.Reject(fresh.Site, fresh.Product, default));
#pragma warning restore CS0618
        Assert.Contains(nameof(IKnowledgeProductStore.RejectAsync), fresh.Calls);
    }

    private static BrandKnowledgeItemWrite Write() => new(BrandKnowledgeDimension.SellingPoint, "一键配网", "三秒完成配网。");
    private static BrandKnowledgeItemUpdate Update() => new(BrandKnowledgeDimension.SellingPoint, "一键配网", "三秒完成配网，支持双频。", 1);
    private static KnowledgeExtractSaveRequest SaveRequest(string candidateId)
        => new([new KnowledgeExtractSaveItem(candidateId, BrandKnowledgeDimension.BrandIntro, "成立背景", "成立于 2010 年。")]);

    private static KnowledgeExtractJobView Job(string status)
        => new(Guid.NewGuid(), status, "finalize", "https://example.test/about", "关于我们", [], false, null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(24));

    private static void AssertError(IActionResult result, int status, string code)
    {
        var response = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, JsonSerializer.SerializeToElement(response.Value).GetProperty("error").GetProperty("code").GetString());
    }

    private static DefaultHttpContext Context(Guid tenant, Guid user, string? role)
    {
        var context = new DefaultHttpContext();
        if (role is not null)
            context.Items[SessionAuthenticationMiddleware.ContextItemKey] = new AuthenticatedRequestContext(
                new(user, "fixture@example.test", "Fixture", "active", [new(tenant, "Team", role, "active")]), Guid.NewGuid(), "fixture");
        return context;
    }

    private sealed class Fixture
    {
        private readonly Dictionary<Guid, BrandKnowledgeItemView> stored = [];

        internal Fixture(string? role)
        {
            Controller = new BrandKnowledgeController(Proxy<IBrandKnowledgeItemStore>(), Proxy<IKnowledgeExtractJobStore>(),
                Proxy<IResearchStore>(), new TenantContextResolver(new TestEnvironment()))
            { ControllerContext = new() { HttpContext = Context(Tenant, User, role) } };
        }

        internal Guid Tenant { get; } = Guid.NewGuid();
        internal Guid Site { get; } = Guid.NewGuid();
        internal Guid User { get; } = Guid.NewGuid();
        internal Guid JobId { get; } = Guid.NewGuid();
        internal BrandKnowledgeController Controller { get; }
        internal List<string> Calls { get; } = [];
        internal KnowledgeExtractJobView? Current { get; set; }
        internal bool Configured { get; set; } = true;
        /// <summary>模拟品牌已归档：Store 的 GetAsync 与列表、新增一样抛出 404 RESOURCE_NOT_FOUND。</summary>
        internal bool SiteArchived { get; set; }
        internal Exception? Failure { get; set; }
        internal BrandKnowledgeDimension? LastDimension { get; private set; }
        internal BrandKnowledgeItemSource? LastSource { get; private set; }
        internal string? LastResolution { get; private set; }
        internal Guid? LastSaveJob { get; private set; }
        internal Guid? LastSaveUser { get; private set; }

        internal int Count(string method) => Calls.Count(call => call == method);

        internal Guid AddItem(Guid createdBy)
        {
            var item = Item(Guid.NewGuid(), createdBy);
            stored[item.ItemId] = item;
            return item.ItemId;
        }

        private BrandKnowledgeItemView Item(Guid id, Guid createdBy)
            => new(id, Site, BrandKnowledgeDimension.SellingPoint, "一键配网", "三秒完成配网。", "manual", null, null, "pending", 1,
                createdBy, createdBy, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        private T Proxy<T>() where T : class
        {
            var proxy = DispatchProxy.Create<T, StoreProxy>();
            ((StoreProxy)(object)proxy).Handler = (method, args) =>
            {
                if (method.DeclaringType != typeof(IResearchStore))
                {
                    Calls.Add(method.Name);
                    Assert.Equal(Tenant, args[0]);
                    Assert.Equal(Site, args[1]);
                }
                if (Failure is not null && WriteMethods.Contains(method.Name)) throw Failure;
                switch (method.Name)
                {
                    case nameof(IResearchStore.GetProviderConfigurationAsync):
                        return Task.FromResult(Configured
                            ? new ResearchProviderConfiguration((string)args[0]!, "gpt-6-luna", "https://luna.test/v1", "secret-fixture", false, 60, 1)
                            : null);
                    case nameof(IBrandKnowledgeItemStore.ListAsync):
                        LastDimension = (BrandKnowledgeDimension?)args[2];
                        return Task.FromResult(new BrandKnowledgeItemList(stored.Values.ToArray(),
                            BrandKnowledgeRules.CompleteCounts(null), stored.Count, BrandKnowledgeRules.MaxItemsPerSite));
                    case nameof(IBrandKnowledgeItemStore.GetAsync) when method.DeclaringType == typeof(IBrandKnowledgeItemStore):
                        if (SiteArchived) throw new CollectorStoreException("RESOURCE_NOT_FOUND", "品牌不存在。", 404);
                        return Task.FromResult(stored.GetValueOrDefault((Guid)args[2]!));
                    case nameof(IBrandKnowledgeItemStore.CreateManyAsync):
                        Assert.Equal(User, args[2]);
                        LastSource = (BrandKnowledgeItemSource)args[4]!;
                        return Task.FromResult<IReadOnlyList<BrandKnowledgeItemView>>([Item(Guid.NewGuid(), (Guid)args[2]!)]);
                    case nameof(IBrandKnowledgeItemStore.UpdateAsync):
                        return Task.FromResult(stored[(Guid)args[2]!]);
                    case nameof(IBrandKnowledgeItemStore.DeleteAsync):
                        return Task.CompletedTask;
                    case nameof(IKnowledgeExtractJobStore.EnqueueAsync):
                        Assert.Equal(User, args[2]);
                        return Task.FromResult(Job("pending") with { JobId = JobId });
                    case nameof(IKnowledgeExtractJobStore.GetCurrentAsync):
                        return Task.FromResult(Current);
                    case nameof(IKnowledgeExtractJobStore.GetAsync):
                        return Task.FromResult((Guid)args[2]! == JobId ? Current ?? Job("running") with { JobId = JobId } : null);
                    case nameof(IKnowledgeExtractJobStore.CancelAsync):
                        return Task.FromResult(Job("cancelled") with { JobId = JobId });
                    case nameof(IKnowledgeExtractJobStore.SaveCandidatesAsync):
                        LastSaveJob = (Guid)args[2]!;
                        LastSaveUser = (Guid)args[3]!;
                        return Task.FromResult<IReadOnlyList<BrandKnowledgeItemView>>([Item(Guid.NewGuid(), (Guid)args[3]!)]);
                    case nameof(IKnowledgeExtractJobStore.MarkResolvedAsync):
                        LastResolution = (string)args[3]!;
                        return Task.FromResult(Job(LastResolution) with { JobId = JobId });
                    default:
                        throw new InvalidOperationException($"Unexpected store call: {method.DeclaringType?.Name}.{method.Name}");
                }
            };
            return proxy;
        }
    }

    private sealed class ProductFixture
    {
        private readonly bool migrated;

        internal ProductFixture(bool migrated)
        {
            this.migrated = migrated;
#pragma warning disable CS0618 // 旧产品接口已标记 [Obsolete]，此处专门验证其兼容保护。
            Controller = new KnowledgeProductController(Proxy<IKnowledgeProductStore>(), Proxy<IKnowledgeStore>(), Proxy<IArticleSourceReader>(),
                new TenantContextResolver(new TestEnvironment()))
            { ControllerContext = new() { HttpContext = Context(Tenant, User, "owner") } };
#pragma warning restore CS0618
        }

        internal Guid Tenant { get; } = Guid.NewGuid();
        internal Guid Site { get; } = Guid.NewGuid();
        internal Guid User { get; } = Guid.NewGuid();
        internal Guid Product { get; } = Guid.NewGuid();
#pragma warning disable CS0618
        internal KnowledgeProductController Controller { get; }
#pragma warning restore CS0618
        internal List<string> Calls { get; } = [];

        private KnowledgeProductView View()
            => new(Product, Tenant, Site, "3188DH", KnowledgeProductStatus.Approved, "manual", "介绍", null, null, null, [], null, [],
                new Dictionary<string, ProductFieldSource>(), null, null, Guid.NewGuid(), null, User, User,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        private T Proxy<T>() where T : class
        {
            var proxy = DispatchProxy.Create<T, StoreProxy>();
            ((StoreProxy)(object)proxy).Handler = (method, args) =>
            {
                Calls.Add(method.Name);
                Assert.Equal(Tenant, args[0]);
                switch (method.Name)
                {
                    case nameof(IKnowledgeProductStore.GetAsync):
                        return Task.FromResult<KnowledgeProductView?>(View());
                    case nameof(IKnowledgeProductStore.IsMigratedAsync):
                        Assert.Equal(Product, args[1]);
                        return Task.FromResult(migrated);
                    case nameof(IKnowledgeProductStore.RejectAsync):
                        return Task.FromResult<KnowledgeProductView?>(View());
                    default:
                        throw new InvalidOperationException($"Unexpected store call: {method.DeclaringType?.Name}.{method.Name}");
                }
            };
            return proxy;
        }
    }

    public class StoreProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args ?? []);
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
