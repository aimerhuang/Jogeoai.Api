using System.Text.Json;
using System.Text.Json.Serialization;
using Jogeoai.Api;
using Jogeoai.Api.Controllers;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Jogeoai.Tests.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace Jogeoai.Tests;

/// <summary>
/// 品牌研究额度（C4 R10）中不需要数据库的部分：<c>Research:Quota</c> 的默认值与启动校验、ResearchStore 在 API / Worker 两种注册方式下的解析、
/// 两个新错误在 Controller 的映射（含 <c>details.jobId</c>），以及只读成员先于额度错误被拒绝。
/// 入队检查与领取顺序的 SQL 行为见 <see cref="BrandResearchQuotaSqlTests"/>（需要独立测试库）。
/// </summary>
public sealed class BrandResearchQuotaTests
{
    [Fact]
    public void QuotaDefaultsToOneActiveJobPerSiteAndTwentyPerTenantPerDay()
    {
        var defaults = new ResearchQuotaOptions();
        Assert.Equal("Research:Quota", ResearchQuotaOptions.SectionName);
        Assert.Equal(1, defaults.MaxActivePerSite);
        Assert.Equal(20, defaults.DailyPerTenant);
        Assert.True(defaults.IsValid());
        Assert.False(new ResearchQuotaOptions { MaxActivePerSite = 0 }.IsValid());
        Assert.False(new ResearchQuotaOptions { DailyPerTenant = 0 }.IsValid());
        Assert.False(new ResearchQuotaOptions { DailyPerTenant = -1 }.IsValid());
    }

    [Fact]
    public async Task QuotaStartupValidationUsesDefaultsWithoutTheSectionAndFailsBelowOne()
    {
        // IStartupValidator 是 ValidateOnStart 注册的、宿主启动时执行的校验入口；这里不启动宿主，直接调用它。
        static ServiceProvider Build(params (string Key, string Value)[] values) => new ServiceCollection()
            .AddResearchQuota(new ConfigurationBuilder()
                .AddInMemoryCollection(values.ToDictionary(item => item.Key, item => (string?)item.Value)).Build())
            .BuildServiceProvider();

        // 配置节缺失：默认值，启动校验通过。
        await using var absent = Build();
        absent.GetRequiredService<IStartupValidator>().Validate();
        var defaults = absent.GetRequiredService<IOptions<ResearchQuotaOptions>>().Value;
        Assert.Equal((1, 20), (defaults.MaxActivePerSite, defaults.DailyPerTenant));

        // 配置值被绑定。
        await using var configured = Build(("Research:Quota:MaxActivePerSite", "2"), ("Research:Quota:DailyPerTenant", "50"));
        configured.GetRequiredService<IStartupValidator>().Validate();
        var bound = configured.GetRequiredService<IOptions<ResearchQuotaOptions>>().Value;
        Assert.Equal((2, 50), (bound.MaxActivePerSite, bound.DailyPerTenant));

        // 小于 1：启动校验失败，读取选项同样失败，不会带着无效值继续运行。
        foreach (var (key, value) in new[]
                 {
                     ("Research:Quota:MaxActivePerSite", "0"), ("Research:Quota:DailyPerTenant", "0"), ("Research:Quota:DailyPerTenant", "-5"),
                 })
        {
            await using var invalid = Build((key, value));
            Assert.Throws<OptionsValidationException>(() => invalid.GetRequiredService<IStartupValidator>().Validate());
            Assert.Throws<OptionsValidationException>(() => invalid.GetRequiredService<IOptions<ResearchQuotaOptions>>().Value);
        }

        // 不是数字：绑定阶段就失败，同样发生在启动校验里。
        await using var notANumber = Build(("Research:Quota:DailyPerTenant", "many"));
        Assert.ThrowsAny<Exception>(() => notANumber.GetRequiredService<IStartupValidator>().Validate());
    }

    [Fact]
    public async Task ResearchStoreResolvesWithApiAndWorkerRegistrations()
    {
        // MySqlDatabase 只保存连接串，构造和解析 ResearchStore 都不会连接数据库。
        var database = new MySqlDatabase("Server=unused.invalid;Database=unused");

        // API：注册了额度配置。
        await using var api = new ServiceCollection()
            .AddSingleton(database)
            .AddResearchQuota(new ConfigurationBuilder().Build())
            .AddSingleton<IResearchStore, ResearchStore>()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        Assert.IsType<ResearchStore>(api.GetRequiredService<IResearchStore>());

        // Worker：不调用 AddResearchQuota，宿主只带有 Options 基础设施；Worker 不入队，额度取默认值。
        await using var worker = new ServiceCollection()
            .AddOptions()
            .AddSingleton(database)
            .AddSingleton<IResearchStore, ResearchStore>()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        Assert.IsType<ResearchStore>(worker.GetRequiredService<IResearchStore>());

        // 连 Options 基础设施都没有时，可选参数取默认值，仍可解析。
        await using var bare = new ServiceCollection()
            .AddSingleton(database)
            .AddSingleton<IResearchStore, ResearchStore>()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        Assert.IsType<ResearchStore>(bare.GetRequiredService<IResearchStore>());
    }

    [Fact]
    public void ClaimStatementOrdersByLeastRecentlyServedTenantAndLocksOnlyCandidateRows()
    {
        var sql = string.Join(' ', ResearchStore.ClaimSql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        const string lastServed = "(SELECT MAX(s.started_at) FROM brand_research_jobs s WHERE s.tenant_id = j.tenant_id)";
        // 从未被服务过的租户用任务创建时间代替“最近服务时间”，不排在所有人之前（那样每个新租户都能插队一次）。
        Assert.Contains($"ORDER BY COALESCE({lastServed}, j.created_at), j.created_at, j.id", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("IS NOT NULL", sql, StringComparison.Ordinal);
        Assert.Equal(1, sql.Split(lastServed).Length - 1);
        Assert.EndsWith("LIMIT 1 FOR UPDATE OF j SKIP LOCKED;", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void EnqueueTransactionIsReadCommitted()
    {
        // 同站点只入队一条的保证：站点行锁排队 + 取得锁之后的读能看到上一个请求已提交的任务。
        // READ COMMITTED 下后者按定义成立；这里只固定这个选择（文本层面），并发行为见 BrandResearchQuotaSqlTests。
        Assert.Equal(System.Data.IsolationLevel.ReadCommitted, ResearchStore.EnqueueIsolation);
    }

    [Fact]
    public void AlreadyActiveExceptionCarriesStableCodeStatusAndJobId()
    {
        var jobId = Guid.NewGuid();
        var exception = new ResearchAlreadyActiveException(jobId);
        Assert.Equal("RESEARCH_ALREADY_ACTIVE", exception.Code);
        Assert.Equal(StatusCodes.Status409Conflict, exception.StatusCode);
        Assert.Equal("该品牌已有研究任务在进行中。", exception.Message);
        Assert.Equal(jobId, exception.JobId);
    }

    [Fact]
    public async Task ActiveJobConflictReturns409WithExistingJobIdInDetails()
    {
        var existing = Guid.NewGuid();
        var fixture = new Fixture("member", new ResearchAlreadyActiveException(existing));
        var result = await fixture.StartAsync();

        var error = AssertError(result, StatusCodes.Status409Conflict, "RESEARCH_ALREADY_ACTIVE");
        Assert.Equal("该品牌已有研究任务在进行中。", error.GetProperty("message").GetString());
        Assert.Equal(existing, error.GetProperty("details").GetProperty("jobId").GetGuid());
        Assert.Equal(1, fixture.Proxy.Count(nameof(IResearchStore.EnqueueAsync)));
    }

    [Fact]
    public async Task DailyLimitReturns429WithoutDetails()
    {
        var fixture = new Fixture("member",
            new CollectorStoreException("RESEARCH_DAILY_LIMIT", "今天的品牌研究次数已用完，请明天再试。", StatusCodes.Status429TooManyRequests));
        var result = await fixture.StartAsync();

        var error = AssertError(result, StatusCodes.Status429TooManyRequests, "RESEARCH_DAILY_LIMIT");
        Assert.Equal("今天的品牌研究次数已用完，请明天再试。", error.GetProperty("message").GetString());
        Assert.False(error.TryGetProperty("details", out _));
    }

    [Fact]
    public async Task IdempotencyConflictKeepsItsCodeAndHasNoDetails()
    {
        var fixture = new Fixture("member", new CollectorStoreException("RESEARCH_IDEMPOTENCY_CONFLICT", "refreshKey 已用于不同的研究请求。", 409));
        var error = AssertError(await fixture.StartAsync(), StatusCodes.Status409Conflict, "RESEARCH_IDEMPOTENCY_CONFLICT");
        Assert.False(error.TryGetProperty("details", out _));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ViewerIsForbiddenBeforeAnyQuotaErrorCanSurface(bool siteAlreadyActive)
    {
        // Store 一旦被调用就会抛出额度错误；只读成员必须在此之前得到 403，且 Store 零调用。
        CollectorStoreException failure = siteAlreadyActive
            ? new ResearchAlreadyActiveException(Guid.NewGuid())
            : new CollectorStoreException("RESEARCH_DAILY_LIMIT", "今天的品牌研究次数已用完，请明天再试。", StatusCodes.Status429TooManyRequests);
        var fixture = new Fixture("viewer", failure);

        AssertError(await fixture.StartAsync(), StatusCodes.Status403Forbidden, "CONTENT_FORBIDDEN");
        Assert.Empty(fixture.Proxy.Calls);
    }

    [Fact]
    public async Task StartDocumentsBothQuotaResponsesInChineseInSwagger()
    {
        // 只含 Controller 与 Swagger 的文档宿主（随机回环端口）：不注册任何 Store，不连接数据库。
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(BrandResearchController).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production,
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddControllers().AddApplicationPart(typeof(BrandResearchController).Assembly)
            .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(options =>
        {
            options.OperationFilter<AnalyticsOpenApiOperationFilter>();
            options.UseAllOfToExtendReferenceSchemas();
            options.SwaggerDoc("v1", new OpenApiInfo { Title = "Jogeoai API", Version = "v1" });
            foreach (var assembly in new[] { "Jogeoai.Api", "Jogeoai.Domain", "Jogeoai.Application", "Jogeoai.Providers.Abstractions" })
                options.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, assembly + ".xml"),
                    includeControllerXmlComments: assembly == "Jogeoai.Api");
        });
        await using var app = builder.Build();
        app.UseSwagger();
        app.MapControllers();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using var swagger = JsonDocument.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"));
            var operation = swagger.RootElement.GetProperty("paths").GetProperty("/api/v1/sites/{siteId}/brand-research").GetProperty("post");
            Assert.Contains("24 小时", operation.GetProperty("summary").GetString(), StringComparison.Ordinal);
            var responses = operation.GetProperty("responses");
            Assert.True(responses.TryGetProperty("202", out _));
            var conflict = responses.GetProperty("409").GetProperty("description").GetString();
            Assert.Contains("RESEARCH_ALREADY_ACTIVE", conflict, StringComparison.Ordinal);
            Assert.Contains("details.jobId", conflict, StringComparison.Ordinal);
            Assert.Contains("现有任务", conflict, StringComparison.Ordinal);
            var limited = responses.GetProperty("429").GetProperty("description").GetString();
            Assert.Contains("RESEARCH_DAILY_LIMIT", limited, StringComparison.Ordinal);
            Assert.Contains("24 小时", limited, StringComparison.Ordinal);
        }
        finally { await app.StopAsync(); }
    }

    private static JsonElement AssertError(IActionResult result, int status, string code)
    {
        var response = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(status, Assert.IsAssignableFrom<IStatusCodeActionResult>(result).StatusCode);
        var error = JsonSerializer.SerializeToElement(response.Value).GetProperty("error");
        Assert.Equal(code, error.GetProperty("code").GetString());
        return error;
    }

    /// <summary>直接构造 Controller（Production 环境的租户解析），Store 为按方法名分派的桩；入队总是抛出给定的业务异常。</summary>
    private sealed class Fixture
    {
        private readonly IResearchStore store;
        private readonly DefaultHttpContext context = new();
        private readonly Guid tenant = Guid.NewGuid();

        internal Fixture(string role, CollectorStoreException enqueueFailure)
        {
            (store, Proxy) = McpStoreProxy.Create<IResearchStore>();
            Proxy.Handlers[nameof(IResearchStore.EnqueueAsync)] = _ => Task.FromException<BrandResearchAccepted>(enqueueFailure);
            context.Items[SessionAuthenticationMiddleware.ContextItemKey] = new AuthenticatedRequestContext(
                new(Guid.NewGuid(), "quota@example.test", "Quota", "active", [new TenantMembershipView(tenant, "Team", role, "active")]),
                Guid.NewGuid(), "quota-session");
        }

        internal McpStoreProxy Proxy { get; }

        internal async Task<IActionResult> StartAsync()
        {
            var controller = new BrandResearchController(store, new TenantContextResolver(new TestEnvironment()))
            {
                ControllerContext = new ControllerContext { HttpContext = context },
            };
            var result = await controller.Start(Guid.NewGuid(), new StartBrandResearchRequest(BrandName: "品牌"), CancellationToken.None);
            return ((IConvertToActionResult)result).Convert();
        }
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
