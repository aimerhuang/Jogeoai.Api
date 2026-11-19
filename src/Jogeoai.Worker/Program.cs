using Jogeoai.Worker;
using Jogeoai.Infrastructure;
using Jogeoai.Application;
using Jogeoai.Providers.Abstractions;
using Jogeoai.Providers.Cloro;
using Jogeoai.Providers.Domestic;

var builder = Host.CreateApplicationBuilder(args);
// 引导页可单独运行品牌研究，避免依赖采集、文章和知识库的后台任务。
var researchOnly = builder.Configuration.GetValue<bool>("ResearchOnly");
var monitoringOnly = builder.Configuration.GetValue<bool>("MonitoringOnly");
var reconcileRunId = builder.Configuration.GetValue<Guid?>("ReconcileCloroRun");
// 只跑口碑情感判定：与正在采集的完整 Worker 并行启动，不必为开启判定而重启采集进程。
var reputationOnly = builder.Configuration.GetValue<bool>("ReputationOnly");
if (researchOnly && monitoringOnly) throw new InvalidOperationException("ResearchOnly 与 MonitoringOnly 不能同时启用。");
if (reputationOnly && (researchOnly || monitoringOnly || reconcileRunId is not null))
    throw new InvalidOperationException("ReputationOnly 不能与 ResearchOnly、MonitoringOnly 或 Cloro 运行修复同时使用。");
if (reconcileRunId is not null && researchOnly) throw new InvalidOperationException("Cloro 运行修复不能使用 ResearchOnly 模式。");
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:Default. Run ./scripts/setup-local.sh first.");
var database = new MySqlDatabase(connectionString);
builder.Services.AddSingleton(database);
builder.Services.AddSingleton<IVisibilityCollectorStore, DomesticCollectorStore>();
builder.Services.AddSingleton<ICloroVisibilityStore, CloroVisibilityStore>();
// App 渠道的开放范围：与 API 进程相同的绑定（Collector:AppTenantIds，为空或缺失表示不对任何租户开放）。
// Worker 里读取它的是 CollectionScheduleStore（App 计划的时隙与惰性对账）和 ManagementStore。
builder.Services.Configure<AppChannelOptions>(options =>
    options.AppTenantIds = builder.Configuration.GetSection(AppChannelOptions.TenantIdsKey).Get<string[]>() ?? []);
builder.Services.AddSingleton<ICollectionScheduleStore, CollectionScheduleStore>();
builder.Services.AddSingleton<IAppCollectionStore, AppCollectionStore>();
builder.Services.AddSingleton<IAnalysisStore, AnalysisStore>();
builder.Services.AddSingleton<ICloroSettingsStore, CloroSettingsStore>();
builder.Services.Configure<CloroKeyPoolOptions>(builder.Configuration.GetSection("Cloro:KeyPool"));
builder.Services.AddSingleton<CloroKeyPoolStore>();
builder.Services.AddSingleton<ICloroKeyPool>(serviceProvider => serviceProvider.GetRequiredService<CloroKeyPoolStore>());
// API 回答 Provider（doubao_api、deepseek_api）的设置行；依赖迁移 025（deepseek_api 种子行见 030）。
builder.Services.AddSingleton<IAnswerProviderSettingsStore, AnswerProviderSettingsStore>();
builder.Services.AddSingleton<IResearchStore, ResearchStore>();
builder.Services.AddSingleton<IManagementStore, ManagementStore>();
builder.Services.AddSingleton<IResearchProvider, OpenAiCompatibleResearchProvider>();
builder.Services.AddKnowledgeServices(builder.Configuration);
builder.Services.AddSingleton<ICreditStore, CreditStore>();
builder.Services.AddSingleton<IArticleSourceReader, ArticleSourceReader>();
builder.Services.AddSingleton<IArticleGenerationProvider, ArticleGenerationProvider>();
builder.Services.AddSingleton<IContentStore, ContentStore>();
builder.Services.AddSingleton<IArticleMaterialSelectionService, ArticleMaterialSelectionService>();
// 文章创作六阶段管线（迁移 040）：阶段 1 读引用分析，阶段 3 结构提取，阶段 4 联网搜索（deepseek-article 行开启 web_search_enabled 才调用）。
builder.Services.AddSingleton<IAnalyticsQueryStore, AnalyticsQueryStore>();
builder.Services.AddSingleton<IArticleStudioStore>(serviceProvider => (ContentStore)serviceProvider.GetRequiredService<IContentStore>());
builder.Services.AddSingleton<IArticleStructureExtractor, ArticleStructureExtractor>();
builder.Services.AddSingleton<IArticleWebResearchService, ArticleWebResearchService>();
builder.Services.AddSingleton<ArticleStudioPipeline>();
builder.Services.AddHttpClient(ArticleWebResearchService.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
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
builder.Services.AddCloroAdapter();
// Registers the named "doubao" HttpClient and IDoubaoAnswerClient; the DoubaoApiTaskWorker below consumes doubao_api tasks.
builder.Services.AddDoubaoProvider();
builder.Services.Configure<DoubaoWorkerOptions>(builder.Configuration.GetSection("Doubao:Worker"));
var doubaoWorkerEnabled = builder.Configuration.GetValue<bool?>("Doubao:Worker:Enabled") ?? true;
// Registers the named "deepseek" HttpClient and IDeepSeekAnswerClient; the DeepSeekApiTaskWorker below consumes deepseek_api tasks.
builder.Services.AddDeepSeekProvider();
builder.Services.Configure<DeepSeekWorkerOptions>(builder.Configuration.GetSection("DeepSeek:Worker"));
var deepSeekWorkerEnabled = builder.Configuration.GetValue<bool?>("DeepSeek:Worker:Enabled") ?? true;
// Registers the named "packy" HttpClient and IPackyAnswerClient; the four Packy*TaskWorker below consume packy_qwen,
// packy_glm, packy_kimi and packy_yuanbao tasks (one PackyAPI group key each).
builder.Services.AddPackyProvider();
builder.Services.Configure<PackyWorkerOptions>(builder.Configuration.GetSection("Packy:Worker"));
var packyWorkerEnabled = builder.Configuration.GetValue<bool?>("Packy:Worker:Enabled") ?? true;
// 口碑情感判定（迁移 038）；appsettings 默认启用并自动登记历史与新回答，配置节缺失时按关闭处理；
// 回滚：置 Reputation__Worker__Enabled=false 后重启 Worker，已写入的判定保留。
// 独立于采集与提及分析：未注册、缺表或 Provider 未配置时，既有 Worker 行为不变。ReputationOnly 模式总是注册判定 Worker。
builder.Services.Configure<ReputationSentimentWorkerOptions>(builder.Configuration.GetSection("Reputation:Worker"));
builder.Services.AddSingleton<IReputationStore, ReputationStore>();
builder.Services.AddSingleton<IReputationClassifierSettings, ResearchStoreReputationClassifierSettings>();
builder.Services.AddSingleton<IReputationSentimentClassifier, LunaReputationSentimentClassifier>();
if (reputationOnly)
    builder.Services.AddHostedService<ReputationSentimentWorker>();
else if (!monitoringOnly) builder.Services.AddHostedService<BrandResearchWorker>();
if (!researchOnly && !reputationOnly)
{
    if (!monitoringOnly)
    {
        builder.Services.AddHostedService<ArticleGenerationWorker>();
        builder.Services.AddHostedService<KnowledgeIngestWorker>();
        builder.Services.AddHostedService<ProductParseWorker>();
        // 品牌知识条目检索投影同步（迁移 043）：条目增删改后由它单写知识库文档，再由 KnowledgeIngestWorker 摄取。
        builder.Services.AddHostedService<KnowledgeItemSyncWorker>();
        // 品牌知识网址识别（迁移 043）：读取公开网页 → Luna 识别 → 候选校验与查重；网页正文不落库。
        builder.Services.AddHostedService<KnowledgeExtractWorker>();
        builder.Services.AddHostedService<Worker>();
        // App 渠道每日告警汇报（results.md 8.4，迁移 049）：每天在 AppChannel:DailyReport:LocalTime（默认 09:30，
        // 时区 AppChannel:DailyReport:TimeZone，默认 Asia/Shanghai）生成并存档一份；内容与运营接口的告警视图同一个查询（含容量）。
        // 发送只占位（KD-19）：不发出任何网络请求，状态记为 not_configured。AppChannel:DailyReport:Enabled=false 时不注册。
        builder.Services.Configure<AppDailyReportWorkerOptions>(builder.Configuration.GetSection(AppDailyReportWorkerOptions.Section));
        builder.Services.AddSingleton<IAppCapacityQuery, AppCapacityQuery>();
        builder.Services.AddSingleton<IAppAlertQuery, AppAlertQuery>();
        builder.Services.AddSingleton<IDailyReportSender, NotConfiguredDailyReportSender>();
        builder.Services.AddSingleton<IAppDailyReportStore, AppDailyReportStore>();
        if (builder.Configuration.GetValue<bool?>($"{AppDailyReportWorkerOptions.Section}:Enabled") ?? true)
            builder.Services.AddHostedService<AppDailyReportWorker>();
        // 套餐周期滚动与到期降级只在完整 Worker 中运行；依赖迁移 024。
        builder.Services.Configure<SubscriptionOptions>(builder.Configuration.GetSection("Subscription"));
        builder.Services.AddSingleton<ISubscriptionStore, SubscriptionStore>();
        builder.Services.AddHostedService<SubscriptionWorker>();
        // 文章链接自动发现与每日监控计费；依赖迁移 027，ArticleLinkMonitoring:Enabled=false 时不注册以便回滚。
        builder.Services.Configure<ArticleLinkMonitoringOptions>(builder.Configuration.GetSection("ArticleLinkMonitoring"));
        builder.Services.AddSingleton<IArticleLinkMonitoringStore, ArticleLinkMonitoringStore>();
        if (builder.Configuration.GetValue<bool?>("ArticleLinkMonitoring:Enabled") ?? true)
            builder.Services.AddHostedService<ArticleLinkMonitoringWorker>();
    }
    builder.Services.AddHostedService<CloroTaskWorker>();
    // 豆包任务与 Cloro 任务同属监控采集；Doubao:Worker:Enabled=false 时跳过，便于回滚。
    if (doubaoWorkerEnabled) builder.Services.AddHostedService<DoubaoApiTaskWorker>();
    // DeepSeek 任务同理；DeepSeek:Worker:Enabled=false 时跳过，便于回滚（已创建的任务保持 pending）。
    if (deepSeekWorkerEnabled) builder.Services.AddHostedService<DeepSeekApiTaskWorker>();
    // PackyAPI 千问 / 智谱清言 / Kimi / 元宝同理；Packy:Worker:Enabled=false 时四个都跳过（已创建的任务保持 pending）。
    if (packyWorkerEnabled)
    {
        builder.Services.AddHostedService<PackyQwenTaskWorker>();
        builder.Services.AddHostedService<PackyGlmTaskWorker>();
        builder.Services.AddHostedService<PackyKimiTaskWorker>();
        builder.Services.AddHostedService<PackyYuanbaoTaskWorker>();
    }
    builder.Services.AddHostedService<CloroKeyCreditsWorker>();
    builder.Services.AddHostedService<CollectionScheduleWorker>();
    // App 渠道的截止处理（design 4.3）与创建 App 周期任务的计划 Worker 注册在一起，完整模式和 MonitoringOnly 模式都运行。
    builder.Services.AddHostedService<AppDeadlineWorker>();
    builder.Services.AddHostedService<AnalysisWorker>();
    if (builder.Configuration.GetValue<bool?>("Reputation:Worker:Enabled") ?? false)
        builder.Services.AddHostedService<ReputationSentimentWorker>();
    // 识别配置或识别器版本变化后自动全站重算；AnalysisRebuild:Enabled=false 时不注册，恢复手动发起。
    builder.Services.Configure<AnalysisRebuildOptions>(builder.Configuration.GetSection("AnalysisRebuild"));
    if (builder.Configuration.GetValue<bool?>("AnalysisRebuild:Enabled") ?? true)
        builder.Services.AddHostedService<AnalysisRebuildWorker>();
}

var host = builder.Build();
// 启动时即计算网页读取 fake-ip 开关：开发环境生效时记录一行信息日志，非开发环境误配时记录警告。
host.Services.GetRequiredService<ArticleSourceAddressPolicy>();
if (reconcileRunId is { } runId)
{
    var count = await host.Services.GetRequiredService<ICloroVisibilityStore>().RecoverTimedOutTasksAsync(runId);
    host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("CloroRecovery")
        .LogInformation("Requeued {Count} timed-out Cloro tasks in run {RunId} for external status reconciliation.", count, runId);
    return;
}
await host.RunAsync();
