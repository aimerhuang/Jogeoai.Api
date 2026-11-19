using System.Runtime.CompilerServices;
using System.Text.Json;
using Jogeoai.Api;
using Jogeoai.Api.Controllers;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Tests.Mcp;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Jogeoai.Tests;

/// <summary>
/// C1 统一写权限（TenantWriteAccess）：9 个原先 viewer 可达的写动作。viewer → 403 CONTENT_FORBIDDEN 且 Store 零调用；
/// 同租户 member → 调用 Store 并返回成功状态；X-Tenant-Id 指向外租户 → 403 TENANT_FORBIDDEN 且 Store 零调用。
/// 直接构造 Controller（Production 环境的 TenantContextResolver），Store 为按方法名分派的 DispatchProxy；不连数据库。
/// </summary>
public sealed class ViewerWriteGuardTests
{
    /// <summary>受保护动作 → (被调用的 Store 方法, 成功状态码)。</summary>
    private static readonly Dictionary<string, (string StoreMethod, int SuccessStatus)> Expectations = new()
    {
        ["Management.CreateCompetitor"] = (nameof(IManagementStore.CreateCompetitorAsync), 201),
        ["Management.PatchCompetitor"] = (nameof(IManagementStore.UpdateCompetitorAsync), 200),
        ["Management.CreateTag"] = (nameof(IManagementStore.CreateTagAsync), 201),
        ["Management.PatchTag"] = (nameof(IManagementStore.UpdateTagAsync), 200),
        ["Onboarding.Complete"] = (nameof(IManagementStore.CompleteOnboardingAsync), 200),
        ["Onboarding.Draft"] = (nameof(IOnboardingProgressStore.SaveDraftAsync), 200),
        ["BrandResearch.Start"] = (nameof(IResearchStore.EnqueueAsync), 202),
        ["BrandResearch.Cancel"] = (nameof(IResearchStore.CancelAsync), 204),
        ["Analytics.CreateJob"] = (nameof(IAnalysisStore.CreateJobAsync), 202),
    };

    /// <summary>xUnit 数据源：全部 9 个动作。</summary>
    public static TheoryData<string> Actions => new(Expectations.Keys);

    [Fact]
    public void CoversExactlyNineActions() => Assert.Equal(9, Expectations.Count);

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task ViewerIsForbiddenBeforeAnyStoreCall(string action)
    {
        var fixture = new Fixture([("viewer", null)]);
        var result = await fixture.InvokeAsync(action);
        AssertError(result, StatusCodes.Status403Forbidden, "CONTENT_FORBIDDEN");
        Assert.Equal(0, fixture.TotalCalls);
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task ViewerInTargetTenantIsForbiddenEvenWhenWriterElsewhere(string action)
    {
        // 用户在租户 A 是 viewer、在租户 B 是 member；带 X-Tenant-Id=A 写入 A 必须被拒绝。
        var other = Guid.NewGuid();
        var fixture = new Fixture([("viewer", null), ("member", other)]);
        fixture.Context.Request.Headers["X-Tenant-Id"] = fixture.Tenant.ToString();
        var result = await fixture.InvokeAsync(action);
        AssertError(result, StatusCodes.Status403Forbidden, "CONTENT_FORBIDDEN");
        Assert.Equal(0, fixture.TotalCalls);
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task SameTenantMemberSucceeds(string action)
    {
        var fixture = new Fixture([("member", null)]);
        var result = await fixture.InvokeAsync(action);
        var (storeMethod, successStatus) = Expectations[action];
        Assert.Equal(successStatus, Status(result));
        Assert.Equal(1, fixture.TotalCalls);
        var call = Assert.Single(fixture.AllCalls);
        Assert.Equal(storeMethod, call.Method);
        Assert.Equal(fixture.Tenant, call.Args[0]);
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task ForeignTenantHeaderIsRejectedBeforeAnyStoreCall(string action)
    {
        var fixture = new Fixture([("member", null)]);
        fixture.Context.Request.Headers["X-Tenant-Id"] = Guid.NewGuid().ToString();
        var result = await fixture.InvokeAsync(action);
        AssertError(result, StatusCodes.Status403Forbidden, "TENANT_FORBIDDEN");
        Assert.Equal(0, fixture.TotalCalls);
    }

    private static int Status(IActionResult result) => result switch
    {
        IStatusCodeActionResult { StatusCode: { } code } => code,
        _ => throw new InvalidOperationException($"无法读取状态码：{result.GetType().Name}"),
    };

    private static void AssertError(IActionResult result, int status, string code)
    {
        Assert.Equal(status, Status(result));
        var body = JsonSerializer.SerializeToElement(Assert.IsAssignableFrom<ObjectResult>(result).Value);
        Assert.Equal(code, body.GetProperty("error").GetProperty("code").GetString());
    }

    private sealed class Fixture
    {
        private readonly McpStoreProxy[] proxies;
        private readonly IManagementStore management;
        private readonly IOnboardingProgressStore progress;
        private readonly IResearchStore research;
        private readonly IAnalysisStore analysis;
        private readonly IAnalyticsQueryStore queries;
        private readonly TenantContextResolver resolver = new(new TestEnvironment());

        /// <param name="memberships">角色与租户；租户为 null 时使用 <see cref="Tenant"/>。</param>
        internal Fixture(IReadOnlyList<(string Role, Guid? TenantId)> memberships)
        {
            var (managementStore, managementProxy) = McpStoreProxy.Create<IManagementStore>();
            var (progressStore, progressProxy) = McpStoreProxy.Create<IOnboardingProgressStore>();
            var (researchStore, researchProxy) = McpStoreProxy.Create<IResearchStore>();
            var (analysisStore, analysisProxy) = McpStoreProxy.Create<IAnalysisStore>();
            var (queryStore, queryProxy) = McpStoreProxy.Create<IAnalyticsQueryStore>();
            (management, progress, research, analysis, queries) = (managementStore, progressStore, researchStore, analysisStore, queryStore);
            proxies = [managementProxy, progressProxy, researchProxy, analysisProxy, queryProxy];

            managementProxy.Handlers[nameof(IManagementStore.CreateCompetitorAsync)] = Returns(Blank<CompetitorView>());
            managementProxy.Handlers[nameof(IManagementStore.UpdateCompetitorAsync)] = Returns(Blank<CompetitorView>());
            managementProxy.Handlers[nameof(IManagementStore.CreateTagAsync)] = Returns(Blank<TagView>());
            managementProxy.Handlers[nameof(IManagementStore.UpdateTagAsync)] = Returns(Blank<TagView>());
            managementProxy.Handlers[nameof(IManagementStore.CompleteOnboardingAsync)] = Returns(Blank<OnboardingCompletionView>());
            progressProxy.Handlers[nameof(IOnboardingProgressStore.SaveDraftAsync)] = Returns(Blank<OnboardingProgressView>());
            researchProxy.Handlers[nameof(IResearchStore.EnqueueAsync)] = Returns(Blank<BrandResearchAccepted>());
            researchProxy.Handlers[nameof(IResearchStore.CancelAsync)] = Returns(true);
            analysisProxy.Handlers[nameof(IAnalysisStore.CreateJobAsync)] = Returns(Blank<AnalysisJobView>());

            Context.Items[SessionAuthenticationMiddleware.ContextItemKey] = new AuthenticatedRequestContext(
                new(Guid.NewGuid(), "guard@example.test", "Guard", "active",
                    memberships.Select(item => new TenantMembershipView(item.TenantId ?? Tenant, "Team", item.Role, "active")).ToArray()),
                Guid.NewGuid(), "guard-session");
        }

        internal Guid Tenant { get; } = Guid.NewGuid();
        internal Guid Site { get; } = Guid.NewGuid();
        internal DefaultHttpContext Context { get; } = new();
        internal IEnumerable<(string Method, object?[] Args)> AllCalls => proxies.SelectMany(proxy => proxy.Calls);
        internal int TotalCalls => AllCalls.Count();

        internal async Task<IActionResult> InvokeAsync(string action)
        {
            var none = CancellationToken.None;
            object result = action switch
            {
                "Management.CreateCompetitor" => await Management().CreateCompetitor(Site, new CompetitorWrite("竞品"), none),
                "Management.PatchCompetitor" => await Management().PatchCompetitor(Site, Guid.NewGuid(), new CompetitorPatch("竞品"), none),
                "Management.CreateTag" => await Management().CreateTag(Site, new TagWrite("标签"), none),
                "Management.PatchTag" => await Management().PatchTag(Site, Guid.NewGuid(), new TagPatch("标签"), none),
                "Onboarding.Complete" => await Onboarding().Complete(Site, new OnboardingCompletionRequest([], []), none),
                "Onboarding.Draft" => await Onboarding().Draft(Site, new OnboardingDraftWrite("prompts", [], []), none),
                "BrandResearch.Start" => await BrandResearch().Start(Site, new StartBrandResearchRequest(BrandName: "品牌"), none),
                "BrandResearch.Cancel" => await BrandResearch().Cancel(Site, Guid.NewGuid(), none),
                "Analytics.CreateJob" => await Analytics().CreateJob(Site, new AnalysisJobRequest(), none),
                _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
            };
            return result is IConvertToActionResult convertible ? convertible.Convert() : (IActionResult)result;
        }

        private ControllerContext ControllerContext() => new() { HttpContext = Context };
        private ManagementController Management() => new(management, resolver) { ControllerContext = ControllerContext() };
        private OnboardingController Onboarding() => new(management, progress, resolver) { ControllerContext = ControllerContext() };
        private BrandResearchController BrandResearch() => new(research, resolver) { ControllerContext = ControllerContext() };
        private AnalyticsController Analytics() => new(queries, analysis, resolver) { ControllerContext = ControllerContext() };

        private static Func<object?[], object?> Returns<T>(T value) => _ => Task.FromResult(value);

        // 结果对象只用于构造 ActionResult，不会被序列化，因此用未初始化实例即可，无需拼装完整 DTO。
        private static T Blank<T>() where T : class => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
