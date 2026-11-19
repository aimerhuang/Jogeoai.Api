using Jogeoai.Infrastructure;
using Jogeoai.Api;
using Jogeoai.Api.Authorization;
using Jogeoai.Api.Mcp;
using Jogeoai.Application;
using Jogeoai.Providers.Abstractions;
using Jogeoai.Providers.Cloro;
using Jogeoai.Providers.Domestic;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.OpenApi;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:Default. Run ./scripts/setup-local.sh first.");
var database = new MySqlDatabase(connectionString);
builder.Services.AddSingleton(database);
builder.Services.AddSingleton<IVisibilityCollectorStore, DomesticCollectorStore>();
// 采集器账号池（迁移 035）；缺表时运维接口返回 503，Collector 领取 / 结果接口返回 500，发布顺序为先迁移再发 API。
builder.Services.AddSingleton<ICollectorSessionPoolStore, CollectorSessionPoolStore>();
builder.Services.AddSingleton<ICloroVisibilityStore, CloroVisibilityStore>();
builder.Services.AddSingleton<IVisibilityRunQueryStore, VisibilityRunQueryStore>();
// App 渠道的开放范围：Collector:AppTenantIds 的形态与解析同 Collector:ChinaTenantIds，但为空或缺失表示不对任何租户开放。
builder.Services.Configure<AppChannelOptions>(options =>
    options.AppTenantIds = builder.Configuration.GetSection(AppChannelOptions.TenantIdsKey).Get<string[]>() ?? []);
builder.Services.AddSingleton<IManagementStore, ManagementStore>();
builder.Services.AddSingleton<ICollectionScheduleStore, CollectionScheduleStore>();
builder.Services.AddSingleton<IAppCollectionStore, AppCollectionStore>();
// App 全局账号池（迁移 049）：节点令牌鉴权、绑定登记、领取、续租与失败上报；未迁移时这些接口返回 503。
builder.Services.AddSingleton<IAppCollectorPoolStore, AppCollectorPoolStore>();
// App 各平台的容量视图（design 6.5）与品牌的 App 采集状态（GET /sites/{siteId}/app-collection/status）；只读。
builder.Services.AddSingleton<IAppCapacityQuery, AppCapacityQuery>();
builder.Services.AddSingleton<IAppCollectionStatusQuery, AppCollectionStatusQuery>();
// App 账号池的运营接口（/api/v1/admin/collector-app，只限系统管理员）：运营读写由同一个无状态的账号池实现提供；
// 告警信息与每日汇报的历史读同一个查询（Worker 负责每天生成汇报）。发送渠道首期只有不发送的占位实现（KD-19）。
builder.Services.AddSingleton<IAppCollectorAdminStore, AppCollectorPoolStore>();
builder.Services.AddSingleton<IAppAlertQuery, AppAlertQuery>();
builder.Services.AddSingleton<IDailyReportSender, NotConfiguredDailyReportSender>();
builder.Services.AddSingleton<IAppDailyReportStore, AppDailyReportStore>();
builder.Services.AddSingleton<IPromptMonitoringService, PromptMonitoringService>();
builder.Services.AddSingleton<IAnalysisStore, AnalysisStore>();
builder.Services.AddSingleton<IAnalyticsQueryStore, AnalyticsQueryStore>();
// 口碑补录任务与情感事实（迁移 038）；缺表时口碑接口返回 503 REPUTATION_SCHEMA_NOT_MIGRATED，其余接口不受影响。
builder.Services.AddSingleton<IReputationStore, ReputationStore>();
builder.Services.AddSingleton<ICloroSettingsStore, CloroSettingsStore>();
builder.Services.Configure<CloroKeyPoolOptions>(builder.Configuration.GetSection("Cloro:KeyPool"));
builder.Services.AddSingleton<CloroKeyPoolStore>();
builder.Services.AddSingleton<ICloroKeyPool>(serviceProvider => serviceProvider.GetRequiredService<CloroKeyPoolStore>());
builder.Services.AddSingleton<ICloroKeyAdminStore>(serviceProvider => serviceProvider.GetRequiredService<CloroKeyPoolStore>());
// API 回答 Provider（doubao_api、deepseek_api、packy_qwen、packy_glm、packy_kimi、packy_yuanbao）的设置行；依赖迁移 025（deepseek_api 种子行见 030，PackyAPI 三行见 036、packy_yuanbao 见 046），缺失时接口返回 503。
builder.Services.AddSingleton<IAnswerProviderSettingsStore, AnswerProviderSettingsStore>();
builder.Services.AddSingleton<IIdentityStore, IdentityStore>();
// 登录失败频控（迁移 045）：只按账号统计连续失败、不设 IP 维度，多实例共享计数；缺表时登录返回 503 AUTH_THROTTLE_SCHEMA_NOT_MIGRATED。
builder.Services.AddOptions<LoginThrottleOptions>()
    .Bind(builder.Configuration.GetSection(LoginThrottleOptions.SectionName))
    .Validate(options => options.IsValid(),
        "Auth:LoginThrottle requires AccountFailureThreshold >= 1, AccountBaseBackoff > 0 and AccountMaxBackoff >= AccountBaseBackoff.")
    .ValidateOnStart();
builder.Services.AddSingleton<ILoginThrottleStore, MySqlLoginThrottleStore>();
builder.Services.TryAddSingleton(TimeProvider.System);
// MCP 只读访问令牌（迁移 044）；缺表时 /api/v1/mcp-tokens 与 /mcp 返回 503 MCP_SCHEMA_NOT_MIGRATED，其余接口不受影响。
builder.Services.AddSingleton<IMcpTokenStore, McpTokenStore>();
builder.Services.Configure<SubscriptionOptions>(builder.Configuration.GetSection("Subscription"));
builder.Services.AddSingleton<ISubscriptionStore, SubscriptionStore>();
builder.Services.Configure<RegistrationEmailOptions>(builder.Configuration.GetSection("Email"));
builder.Services.AddSingleton<IRegistrationEmailSender, RegistrationEmailSender>();
builder.Services.AddRateLimiter(options =>
{
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.StatusCode = 429;
        await context.HttpContext.Response.WriteAsJsonAsync(new { error = new { code = "RATE_LIMITED", message = "请求过于频繁，请稍后重试。" } }, cancellationToken);
    };
    options.AddPolicy("registration-email", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        { PermitLimit = 20, Window = TimeSpan.FromHours(1), QueueLimit = 0 }));
    options.AddPolicy("registration", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        { PermitLimit = 30, Window = TimeSpan.FromMinutes(10), QueueLimit = 0 }));
    options.AddPolicy(BrandRecognitionRateLimit.Policy, BrandRecognitionRateLimit.Partition);
    options.AddPolicy(PromptSuggestionsRateLimit.Policy, PromptSuggestionsRateLimit.Partition);
    options.AddMcpTokenPolicy();
});
builder.Services.AddSingleton<TenantContextResolver>();
builder.Services.AddSingleton<IResearchStore, ResearchStore>();
builder.Services.AddSingleton<IOnboardingProgressStore, OnboardingProgressStore>();
builder.Services.AddSingleton<IResearchProvider, OpenAiCompatibleResearchProvider>();
builder.Services.AddSingleton<IBrandRecognitionProvider, LunaBrandRecognitionProvider>();
builder.Services.AddSingleton<IBrandSearchProvider, LunaBrandSearchProvider>();
builder.Services.AddSingleton<IBrandRecognitionService, BrandRecognitionService>();
builder.Services.AddSingleton<IPromptKeywordExpansionService, PromptKeywordExpansionService>();
builder.Services.AddSingleton<IQuickSuggestionService, QuickSuggestionService>();
builder.Services.AddKnowledgeServices(builder.Configuration);
builder.Services.AddSingleton<ICreditStore, CreditStore>();
builder.Services.AddSingleton<IArticleSourceReader, ArticleSourceReader>();
builder.Services.AddSingleton<IArticleGenerationProvider, ArticleGenerationProvider>();
builder.Services.AddSingleton<IContentStore, ContentStore>();
builder.Services.AddSingleton<IArticleMaterialSelectionService, ArticleMaterialSelectionService>();
// 文章创作（article-studio）：依赖迁移 040；未执行时提交与轮询返回 503 ARTICLE_STUDIO_SCHEMA_NOT_MIGRATED，旧向导不受影响。
builder.Services.AddSingleton<IArticleStudioStore>(serviceProvider => (ContentStore)serviceProvider.GetRequiredService<IContentStore>());
builder.Services.AddSingleton<IArticleStudioService, ArticleStudioService>();
// 文章编辑器（草稿自动保存、智能微调、图片、docx 导出）：依赖迁移 041；未执行时草稿 / 微调 / 图片返回 503 ARTICLE_EDITOR_SCHEMA_NOT_MIGRATED，
// PATCH 编辑与 Markdown 导出不受影响。图片对象复用知识库对象存储（content-images/ 前缀）。
builder.Services.AddSingleton<IArticleEditorStore>(serviceProvider => (ContentStore)serviceProvider.GetRequiredService<IContentStore>());
builder.Services.AddSingleton<IArticleRefineProvider, ArticleRefineProvider>();
builder.Services.AddSingleton<IArticleRefineService, ArticleRefineService>();
builder.Services.AddSingleton<IContentImageService, ContentImageService>();
builder.Services.AddSingleton<ICitationDomainTypeStore, CitationDomainTypeStore>();
builder.Services.AddSingleton<IArticleMonitoringQuery, ArticleMonitoringQuery>();
builder.Services.AddSingleton<IArticleLinkMonitoringStore, ArticleLinkMonitoringStore>();
builder.Services.AddSingleton<ITeamSummaryQuery, TeamSummaryQuery>();
// The per-provider timeout is persisted in MySQL (5–600 seconds) and enforced
// by OpenAiCompatibleResearchProvider with a linked cancellation token.  A
// shorter HttpClient-wide timeout would silently cap valid database settings.
builder.Services.AddHttpClient("research", client => client.Timeout = Timeout.InfiniteTimeSpan);
// 网页读取的地址放行规则：ArticleSource:AllowFakeIpRange 只在 Development 生效；ArticleSourceReader 的 DNS 预检
// 与 research-web 客户端的连接回调共用同一实例。
builder.Services.Configure<ArticleSourceOptions>(builder.Configuration.GetSection("ArticleSource"));
builder.Services.AddSingleton(serviceProvider => ArticleSourceAddressPolicy.Create(
    serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ArticleSourceOptions>>().Value,
    serviceProvider.GetRequiredService<IHostEnvironment>(),
    serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<ArticleSourceAddressPolicy>()));
builder.Services.AddHttpClient("research-web", client => client.Timeout = TimeSpan.FromSeconds(35))
    .ConfigurePrimaryHttpMessageHandler(serviceProvider =>
        ArticleSourceReader.CreatePinnedHandler(serviceProvider.GetRequiredService<ArticleSourceAddressPolicy>().IsAllowed));
// 引用来源站点图标（GET /api/v1/site-icons/{domain}）；依赖迁移 028 的全局表 site_icons。
// 出站请求复用 SSRF pinned handler；单次超时与总预算由 SiteIconFetcher 的取消令牌控制，客户端超时只作兜底。
builder.Services.Configure<SiteIconOptions>(builder.Configuration.GetSection("SiteIcons"));
builder.Services.AddSingleton<ISiteIconStore, SiteIconStore>();
// AllowFakeIpRange 只在 Development 生效；DNS 预检与连接回调共用同一放行规则。
builder.Services.AddSingleton(serviceProvider => SiteIconAddressPolicy.Create(
    serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<SiteIconOptions>>().Value,
    serviceProvider.GetRequiredService<IHostEnvironment>(),
    serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<SiteIconAddressPolicy>()));
builder.Services.AddSingleton<ISiteIconHostGuard, PublicSiteIconHostGuard>();
builder.Services.AddSingleton<SiteIconFetcher>();
builder.Services.AddSingleton<ISiteIconService, SiteIconService>();
builder.Services.AddHttpClient(SiteIconFetcher.ClientName, client => client.Timeout = TimeSpan.FromSeconds(60))
    .ConfigurePrimaryHttpMessageHandler(serviceProvider =>
        ArticleSourceReader.CreatePinnedHandler(serviceProvider.GetRequiredService<SiteIconAddressPolicy>().IsAllowed));
builder.Services.AddCloroAdapter();
// Registers the named "doubao" HttpClient (infinite timeout, per-settings timeout enforced by the adapter) and IDoubaoAnswerClient.
builder.Services.AddDoubaoProvider();
// Registers the named "deepseek" HttpClient (infinite timeout, per-settings timeout enforced by the adapter) and IDeepSeekAnswerClient.
builder.Services.AddDeepSeekProvider();
// Registers the named "packy" HttpClient (infinite timeout, per-settings timeout enforced by the adapter) and IPackyAnswerClient.
builder.Services.AddPackyProvider();
builder.Services.AddHealthChecks().AddAsyncCheck("mysql", async cancellationToken =>
{
    try
    {
        await database.CheckConnectionAsync(cancellationToken);
        return HealthCheckResult.Healthy();
    }
    catch (Exception)
    {
        // Do not expose connection strings or database exceptions in the health response/log.
        return HealthCheckResult.Unhealthy("MySQL is unavailable.");
    }
});

// MCP（Streamable HTTP，无状态）：只读工具按 MCP 令牌的租户与站点范围查询现有 Store；工具通过 HttpContext 读取调用方。
builder.Services.AddJogeoaiMcp();

// Add services to the container.

// /api/v1 等 Controller 默认要求服务端会话；匿名入口以 [AllowAnonymous] 显式标注，白名单由 SessionAuthorizationEndpointTests 维护。
builder.Services.AddJogeoaiSessionAuthorization();
builder.Services.AddControllers().AddJsonOptions(options =>
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.OperationFilter<AnalyticsOpenApiOperationFilter>();
    options.UseAllOfToExtendReferenceSchemas();
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Jogeoai API",
        Version = "v1",
        Description = "AI visibility API and Cloro provider diagnostics.",
    });
    options.IncludeXmlComments(
        Path.Combine(AppContext.BaseDirectory, $"{Assembly.GetExecutingAssembly().GetName().Name}.xml"),
        includeControllerXmlComments: true);
    options.IncludeXmlComments(
        Path.Combine(AppContext.BaseDirectory, "Jogeoai.Providers.Abstractions.xml"));
    options.IncludeXmlComments(
        Path.Combine(AppContext.BaseDirectory, "Jogeoai.Domain.xml"));
    options.IncludeXmlComments(
        Path.Combine(AppContext.BaseDirectory, "Jogeoai.Application.xml"));
    options.IncludeXmlComments(
        Path.Combine(AppContext.BaseDirectory, "Jogeoai.Providers.Domestic.xml"));
});

var allowedCorsOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>() ?? [];
var allowAnyCorsOrigin = builder.Configuration.GetValue("Cors:AllowAnyOrigin", false);
builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
    {
        if (allowAnyCorsOrigin)
        {
            policy.AllowAnyOrigin();
        }
        else if (allowedCorsOrigins.Length > 0)
        {
            policy.WithOrigins(allowedCorsOrigins);
        }

        policy.AllowAnyHeader().AllowAnyMethod();
    });
});

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
// [Obsolete] 接口在 /openapi/v1.json 中标记为 deprecated（Swashbuckle 文档自带该行为）。
builder.Services.AddOpenApi(options => options.AddOperationTransformer<ObsoleteOpenApiOperationTransformer>());

var swaggerEnabled = builder.Configuration.GetValue("Swagger:Enabled", builder.Environment.IsDevelopment());

var app = builder.Build();

// 启动时即计算站点图标 fake-ip 开关，非开发环境误配时立即记录警告。
app.Services.GetRequiredService<SiteIconAddressPolicy>();
// 同理计算网页读取 fake-ip 开关（开发环境生效时记录一行信息日志）。
app.Services.GetRequiredService<ArticleSourceAddressPolicy>();

// The first schema is intentionally additive and safe to run on every local start.
// The SQL is also checked in under db/migrations for deployment tooling.
await database.EnsureSchemaAsync();

// Configure the HTTP request pipeline.
app.UseCors("Frontend");

// User sessions are server-side records; this middleware only loads a valid
// session into the request context and never trusts a client-supplied user ID.
app.UseMiddleware<SessionAuthenticationMiddleware>();
// /mcp 只认 MCP 令牌（清除会话身份后按令牌哈希校验，失败直接 401）；必须在限流之前，限流才能按令牌分区。其他路径不经过此中间件。
app.UseMcpTokenAuthentication();
app.UseRateLimiter(); // 品牌识别按已解析的真实会话用户分区，不能按未验证的租户头分区。

if (swaggerEnabled)
{
    app.MapOpenApi();
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "Jogeoai API v1");
        options.RoutePrefix = "swagger";
    });
}

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseAuthorization();

app.MapControllers().RequireJogeoaiSession();
app.MapJogeoaiMcp();
app.MapHealthChecks("/health/ready");

app.Run();
