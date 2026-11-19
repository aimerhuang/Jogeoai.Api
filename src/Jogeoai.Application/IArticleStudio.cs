using Jogeoai.Domain;

namespace Jogeoai.Application;

/// <summary>文章创作第 1 步准备、生成提交与轮询的业务编排。</summary>
public interface IArticleStudioService
{
    /// <summary>读取可选提问、价格与余额；传入 promptId 时推导优化目标、知识命中、竞品可用性与现有稿件。</summary>
    /// <param name="tenantId">租户标识。</param>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="userId">当前用户标识。</param>
    /// <param name="canWrite">当前角色是否可写；只读角色不创建素材预览。</param>
    /// <param name="promptId">选中的监测问题；为空时只返回列表。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<ArticleStudioPrepareView> PrepareAsync(Guid tenantId, Guid siteId, Guid userId, bool canWrite, Guid? promptId,
        CancellationToken cancellationToken = default);

    /// <summary>自动补齐写作参数，预占 100 积分并创建文章创作任务。</summary>
    /// <param name="tenantId">租户标识。</param>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="userId">当前用户标识。</param>
    /// <param name="request">生成请求。</param>
    /// <param name="idempotencyKey">请求幂等键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<ArticleStudioGenerationAccepted> SubmitAsync(Guid tenantId, Guid siteId, Guid userId,
        ArticleStudioGenerationRequest request, string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>读取文章创作任务的阶段、来源与结果。</summary>
    /// <param name="tenantId">租户标识。</param>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="taskId">任务标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<ArticleStudioGenerationView?> GetGenerationAsync(Guid tenantId, Guid siteId, Guid taskId,
        CancellationToken cancellationToken = default);
}

/// <summary>文章创作任务、阶段与来源的持久化边界（迁移 040）。</summary>
public interface IArticleStudioStore
{
    /// <summary>按幂等键读取已受理的提交及其请求摘要；未提交时返回空。</summary>
    Task<(ArticleStudioGenerationAccepted Accepted, string RequestHash)?> FindStudioSubmissionAsync(Guid tenantId,
        Guid siteId, string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>原子创建批次、任务、预占、六个待执行阶段；重新生成时决定复用原文章或新建。</summary>
    Task<ArticleStudioGenerationAccepted> SubmitStudioGenerationAsync(Guid tenantId, Guid siteId, Guid userId,
        ArticleStudioSubmission submission, string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>读取该提问最近一篇文章创作稿件。</summary>
    Task<ArticleStudioExistingArticleView?> GetLatestStudioArticleAsync(Guid tenantId, Guid siteId, Guid promptId,
        CancellationToken cancellationToken = default);

    /// <summary>读取文章创作任务轮询视图；非文章创作任务返回空。</summary>
    Task<ArticleStudioGenerationView?> GetStudioGenerationAsync(Guid tenantId, Guid siteId, Guid taskId,
        CancellationToken cancellationToken = default);

    /// <summary>读取提交时冻结的文章创作输入（竞品名单等）。</summary>
    Task<ArticleStudioSnapshot?> GetStudioSnapshotAsync(Guid tenantId, Guid batchId,
        CancellationToken cancellationToken = default);

    /// <summary>读取任务已有阶段记录（含内部产物）。</summary>
    Task<IReadOnlyList<ArticleStudioStageRecord>> ListStagesAsync(Guid tenantId, Guid taskId,
        CancellationToken cancellationToken = default);

    /// <summary>在租约仍属于当前 Worker 时写入阶段状态；租约丢失返回 false。</summary>
    Task<bool> SaveStageAsync(ArticleGenerationWorkItem workItem, ArticleStudioStageRecord stage,
        CancellationToken cancellationToken = default);

    /// <summary>读取任务的某类来源（含正文）。</summary>
    Task<IReadOnlyList<ArticleStudioSourceRecord>> ListSourcesAsync(Guid tenantId, Guid taskId, ArticleStudioSourceKind kind,
        CancellationToken cancellationToken = default);

    /// <summary>在租约仍属于当前 Worker 时整体替换某类来源；租约丢失返回 false。</summary>
    Task<bool> ReplaceSourcesAsync(ArticleGenerationWorkItem workItem, ArticleStudioSourceKind kind,
        IReadOnlyList<ArticleStudioSourceRecord> sources, CancellationToken cancellationToken = default);
}

/// <summary>服务端补齐并冻结后的文章创作提交。</summary>
/// <param name="RequestHash">原始请求摘要（幂等判断）。</param>
/// <param name="Prompt">提交时的监测问题。</param>
/// <param name="TargetSnapshotJson">自动补齐的目标平台快照 JSON。</param>
/// <param name="Language">生成语言。</param>
/// <param name="Strategy">写作策略。</param>
/// <param name="LengthProfile">篇幅档位。</param>
/// <param name="Style">写作风格。</param>
/// <param name="Requirements">自定义写作要求（≤5000）。</param>
/// <param name="WithoutRag">是否不使用知识库。</param>
/// <param name="ContextId">冻结知识上下文标识。</param>
/// <param name="ContextHash">冻结知识上下文摘要。</param>
/// <param name="ContextSnapshot">冻结知识上下文 JSON。</param>
/// <param name="KnowledgeSelectionSnapshot">素材选择快照 JSON（含品牌写作素材）。</param>
/// <param name="Goal">优化目标。</param>
/// <param name="GoalBasis">优化目标依据。</param>
/// <param name="IncludeCompetitors">实际是否纳入竞品视角。</param>
/// <param name="Competitors">冻结的竞品名单。</param>
/// <param name="ArticleId">请求重新生成的文章。</param>
/// <param name="UnitPrice">单价。</param>
public sealed record ArticleStudioSubmission(string RequestHash, PromptView Prompt, string TargetSnapshotJson,
    string Language, string Strategy, string LengthProfile, string Style, string? Requirements, bool WithoutRag,
    Guid? ContextId, string? ContextHash, string? ContextSnapshot, string? KnowledgeSelectionSnapshot,
    ArticleOptimizationGoal Goal, ArticleStudioGoalBasis GoalBasis, bool IncludeCompetitors,
    IReadOnlyList<ArticleStudioFrozenCompetitor> Competitors, Guid? ArticleId, decimal UnitPrice);

/// <summary>冻结到批次快照的竞品。</summary>
/// <param name="SubjectId">竞品标识。</param>
/// <param name="Name">竞品名称。</param>
/// <param name="Mentions">近 7 天提及次数。</param>
/// <param name="Domains">竞品配置域名，用于匹配核验来源。</param>
public sealed record ArticleStudioFrozenCompetitor(Guid SubjectId, string Name, long Mentions, IReadOnlyList<string> Domains);

/// <summary>批次 request_snapshot 中的文章创作冻结输入。</summary>
/// <param name="Pipeline">管线值。</param>
/// <param name="PromptId">监测问题标识。</param>
/// <param name="Goal">优化目标。</param>
/// <param name="GoalBasis">优化目标依据。</param>
/// <param name="IncludeCompetitors">是否纳入竞品视角。</param>
/// <param name="Competitors">竞品名单。</param>
/// <param name="RequestedArticleId">请求重新生成的文章。</param>
/// <param name="RegeneratedAsNew">是否因已有发布链接而新建文章。</param>
/// <param name="WithoutRag">是否不使用知识库。</param>
public sealed record ArticleStudioSnapshot(string Pipeline, Guid PromptId, ArticleOptimizationGoal Goal,
    ArticleStudioGoalBasis GoalBasis, bool IncludeCompetitors, IReadOnlyList<ArticleStudioFrozenCompetitor> Competitors,
    Guid? RequestedArticleId, bool RegeneratedAsNew, bool WithoutRag);

/// <summary>阶段记录。</summary>
/// <param name="Key">阶段键。</param>
/// <param name="Status">阶段状态。</param>
/// <param name="StartedAt">开始时间。</param>
/// <param name="FinishedAt">结束时间。</param>
/// <param name="Detail">公开明细。</param>
/// <param name="Output">内部产物（不通过接口返回）。</param>
public sealed record ArticleStudioStageRecord(ArticleStudioStageKey Key, ArticleStudioStageStatus Status,
    DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, ArticleStudioStageDetail? Detail, string? Output);

/// <summary>来源记录（含正文）。</summary>
/// <param name="Kind">来源类型。</param>
/// <param name="Url">规范化链接。</param>
/// <param name="Domain">域名。</param>
/// <param name="Title">标题。</param>
/// <param name="Engines">引擎。</param>
/// <param name="CitationCount">被引次数。</param>
/// <param name="Ownership">引用归属分类。</param>
/// <param name="IsCompetitor">是否竞品页面。</param>
/// <param name="FetchStatus">读取状态。</param>
/// <param name="ErrorCode">读取错误码。</param>
/// <param name="Content">正文（≤20000 字符）。</param>
/// <param name="Position">顺序，从 1 开始。</param>
public sealed record ArticleStudioSourceRecord(ArticleStudioSourceKind Kind, string Url, string Domain, string? Title,
    IReadOnlyList<string> Engines, int CitationCount, string? Ownership, bool IsCompetitor,
    ArticleStudioFetchStatus FetchStatus, string? ErrorCode, string? Content, int Position);

/// <summary>阶段 3：从已抓取的不可信网页正文中提取共性结构（只提取结构，不提取事实）。</summary>
public interface IArticleStructureExtractor
{
    /// <summary>调用一次文章模型 JSON 模式，返回结构洞察文本。</summary>
    Task<string> ExtractAsync(ResearchProviderConfiguration configuration, ArticleStructureRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>结构提取输入。</summary>
/// <param name="Question">主问题。</param>
/// <param name="Language">生成语言。</param>
/// <param name="Sources">已读取正文的引用源。</param>
/// <param name="CompetitorNames">开启竞品视角时的竞品名称。</param>
public sealed record ArticleStructureRequest(string Question, string Language,
    IReadOnlyList<ArticleStudioUntrustedSource> Sources, IReadOnlyList<string> CompetitorNames);

/// <summary>阶段 4：围绕提问联网检索权威资料（DeepSeek Anthropic 兼容 Messages + web_search）。</summary>
public interface IArticleWebResearchService
{
    /// <summary>执行联网搜索；未启用、未配置或模型服务不支持时返回 Skipped 及原因。</summary>
    Task<ArticleWebResearchResult> ResearchAsync(ResearchProviderConfiguration configuration, ArticleWebResearchRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>联网搜索输入。</summary>
/// <param name="Question">主问题。</param>
/// <param name="Language">生成语言。</param>
/// <param name="Goal">优化目标。</param>
/// <param name="CompetitorNames">需要核验公开信息的竞品名称。</param>
public sealed record ArticleWebResearchRequest(string Question, string Language, ArticleOptimizationGoal Goal,
    IReadOnlyList<string> CompetitorNames);

/// <summary>联网搜索结果。</summary>
/// <param name="Skipped">是否跳过（未启用、未配置或不支持）。</param>
/// <param name="SkipReason">跳过原因码。</param>
/// <param name="Summary">模型最终文字摘要（不可信内容）。</param>
/// <param name="Results">检索来源。</param>
/// <param name="SearchCount">实际搜索次数。</param>
public sealed record ArticleWebResearchResult(bool Skipped, string? SkipReason, string? Summary,
    IReadOnlyList<ArticleWebResearchSource> Results, int SearchCount)
{
    /// <summary>构造跳过结果。</summary>
    /// <param name="reason">原因码。</param>
    public static ArticleWebResearchResult Skip(string reason) => new(true, reason, null, [], 0);
}

/// <summary>联网搜索来源。</summary>
/// <param name="Url">规范化链接。</param>
/// <param name="Domain">域名。</param>
/// <param name="Title">标题。</param>
public sealed record ArticleWebResearchSource(string Url, string Domain, string? Title);

/// <summary>单个提问近 N 天全来源被引 URL 汇总。</summary>
/// <param name="EngineCount">有引用数据的引擎数。</param>
/// <param name="TotalUrls">去重 URL 总数。</param>
/// <param name="Items">按被引次数降序的前若干 URL。</param>
public sealed record PromptCitedSourcesView(int EngineCount, int TotalUrls, IReadOnlyList<PromptCitedUrlRow> Items);

/// <summary>单个被引 URL。</summary>
/// <param name="Url">规范化链接。</param>
/// <param name="Domain">域名。</param>
/// <param name="Title">最近一次非空标题。</param>
/// <param name="Engines">引用该链接的引擎（排序）。</param>
/// <param name="CitationCount">被引次数（按回答与 URL 去重的引用事件数）。</param>
/// <param name="Ownership">引用归属分类：brand、competitor、third_party、unknown。</param>
public sealed record PromptCitedUrlRow(string Url, string Domain, string? Title, IReadOnlyList<string> Engines,
    int CitationCount, string Ownership);

public partial interface IAnalyticsQueryStore
{
    /// <summary>读取单个提问在时间窗内全来源（全部 Provider、采集模式与证据类别）的高频被引 URL，带引擎集合、被引次数与归属。</summary>
    /// <param name="tenantId">租户标识。</param>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="promptId">监测问题标识。</param>
    /// <param name="from">开始时间（UTC，含）。</param>
    /// <param name="to">结束时间（UTC，不含）。</param>
    /// <param name="limit">返回 URL 数，1–100。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<PromptCitedSourcesView> TopCitedUrlsForPromptAsync(Guid tenantId, Guid siteId, Guid promptId,
        DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken cancellationToken = default);
}
