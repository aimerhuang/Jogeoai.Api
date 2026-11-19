using System.Text.Json.Serialization;

namespace Jogeoai.Domain;

/// <summary>文章生成定价：旧向导与文章创作共用同一单价，不按目标平台数量计费。</summary>
public static class ArticleGenerationPricing
{
    /// <summary>每篇文章（每次生成或重新生成）预占并在成功后结算的积分。</summary>
    public const decimal UnitPrice = 100m;
}

/// <summary>文章创作管线标识与长度上限。</summary>
public static class ArticleStudioLimits
{
    /// <summary>旧向导任务的管线值（迁移 040 之前的全部任务）。</summary>
    public const string LegacyPipeline = "legacy";
    /// <summary>文章创作六阶段管线值。</summary>
    public const string StudioPipeline = "studio_v1";
    /// <summary>文章创作自定义写作要求的最大字符数。</summary>
    public const int RequirementsMaxLength = 5000;
    /// <summary>旧向导写作补充要求的最大字符数（保持不变）。</summary>
    public const int LegacyRequirementsMaxLength = 2000;
    /// <summary>竞品视角最多纳入的已配置竞品数。</summary>
    public const int MaxCompetitors = 8;
    /// <summary>阶段 1 最多取用的高频引用源数。</summary>
    public const int MaxCitationSources = 12;
    /// <summary>每条来源保存的正文字符上限。</summary>
    public const int SourceContentMaxLength = 20_000;
    /// <summary>进入生成提示词的不可信内容（引用源正文与搜索结果）字符总上限。</summary>
    public const int UntrustedContentMaxLength = 60_000;
    /// <summary>目标平台自动补齐的最大数量。</summary>
    public const int MaxTargets = 5;
}

/// <summary>本次优化目标：由该提问近 7 天全来源表现推导。</summary>
public enum ArticleOptimizationGoal
{
    /// <summary>提及优化：近 7 天未被提及或没有数据。</summary>
    [JsonStringEnumMemberName("mention")] Mention,
    /// <summary>排名优化：已被提及但提及次数不是第一。</summary>
    [JsonStringEnumMemberName("ranking")] Ranking,
    /// <summary>巩固优势：提及次数已是第一（含并列第一）。</summary>
    [JsonStringEnumMemberName("defend")] Defend
}

/// <summary>优化目标的推导依据。</summary>
public enum ArticleStudioGoalBasis
{
    /// <summary>依据近 7 天已完成分析的回答推导。</summary>
    [JsonStringEnumMemberName("recent_data")] RecentData,
    /// <summary>近 7 天没有可用回答，默认提及优化。</summary>
    [JsonStringEnumMemberName("no_data")] NoData
}

/// <summary>竞品视角不可用的原因。</summary>
public enum ArticleStudioCompetitorReason
{
    /// <summary>品牌尚未配置任何启用中的竞品。</summary>
    [JsonStringEnumMemberName("none_configured")] NoneConfigured,
    /// <summary>已配置竞品，但该提问近 7 天没有竞品被提及。</summary>
    [JsonStringEnumMemberName("no_recent_data")] NoRecentData
}

/// <summary>文章创作六个写作阶段。</summary>
public enum ArticleStudioStageKey
{
    /// <summary>阶段 1：读取该提问近 30 天各引擎高频引用源。</summary>
    [JsonStringEnumMemberName("citations")] Citations,
    /// <summary>阶段 2：抓取引用源网页正文。</summary>
    [JsonStringEnumMemberName("fetch")] Fetch,
    /// <summary>阶段 3：从已抓正文提取共性结构与对比维度（不提取事实）。</summary>
    [JsonStringEnumMemberName("structure")] Structure,
    /// <summary>阶段 4：联网搜索权威资料并核验竞品公开信息。</summary>
    [JsonStringEnumMemberName("web_research")] WebResearch,
    /// <summary>阶段 5：应用自定义写作要求与优化目标，组装最终输入。</summary>
    [JsonStringEnumMemberName("requirements")] Requirements,
    /// <summary>阶段 6：按 GEO 规则生成文章。</summary>
    [JsonStringEnumMemberName("compose")] Compose
}

/// <summary>写作阶段状态。</summary>
public enum ArticleStudioStageStatus
{
    /// <summary>未开始。</summary>
    [JsonStringEnumMemberName("pending")] Pending,
    /// <summary>进行中。</summary>
    [JsonStringEnumMemberName("running")] Running,
    /// <summary>已完成。</summary>
    [JsonStringEnumMemberName("succeeded")] Succeeded,
    /// <summary>已跳过（无数据或未配置，原因见明细）；不阻断生成。</summary>
    [JsonStringEnumMemberName("skipped")] Skipped,
    /// <summary>失败；阶段 1–4 失败不阻断生成，阶段 6 失败则任务失败。</summary>
    [JsonStringEnumMemberName("failed")] Failed
}

/// <summary>写作来源类型。</summary>
public enum ArticleStudioSourceKind
{
    /// <summary>该提问在各引擎回答中的高频引用源。</summary>
    [JsonStringEnumMemberName("citation")] Citation,
    /// <summary>阶段 4 联网搜索返回的来源（只记录标题与链接）。</summary>
    [JsonStringEnumMemberName("web_search")] WebSearch
}

/// <summary>来源网页读取状态。</summary>
public enum ArticleStudioFetchStatus
{
    /// <summary>待抓取。</summary>
    [JsonStringEnumMemberName("pending")] Pending,
    /// <summary>已读取正文。</summary>
    [JsonStringEnumMemberName("fetched")] Fetched,
    /// <summary>网页拒绝读取（401/403/429）。</summary>
    [JsonStringEnumMemberName("blocked")] Blocked,
    /// <summary>读取失败（超时、需要登录、内容不可读等）。</summary>
    [JsonStringEnumMemberName("failed")] Failed,
    /// <summary>不抓取正文（如联网搜索来源）。</summary>
    [JsonStringEnumMemberName("skipped")] Skipped
}

/// <summary>可在文章创作中选择的监测问题。</summary>
/// <param name="PromptId">监测问题标识。</param>
/// <param name="Text">问题正文。</param>
/// <param name="BrandedStatus">问题类型：branded 品牌词、unbranded 非品牌词、unknown 未分类。</param>
/// <param name="Locale">问题语言标识。</param>
/// <param name="GroupId">所属提问分组标识；未分组为空。</param>
/// <param name="GroupName">所属提问分组名称；未分组为空。</param>
public sealed record ArticleStudioPromptOption(Guid PromptId, string Text, string BrandedStatus, string Locale,
    Guid? GroupId, string? GroupName);

/// <summary>品牌知识库自动引用结果。</summary>
/// <param name="MaterialPreviewId">已冻结的素材预览标识（15 分钟内提交生成时可回传）；只读角色不创建预览，为空。</param>
/// <param name="HitCount">自动引用的知识条数；0 表示未命中，将按无素材披露规则生成。</param>
/// <param name="WithoutRag">是否将以不使用知识库的方式生成（命中为 0 时为真）。</param>
/// <param name="ExpiresAt">素材预览过期时间（UTC）；为空表示未创建预览。</param>
public sealed record ArticleStudioKnowledgeView(Guid? MaterialPreviewId, int HitCount, bool WithoutRag,
    DateTimeOffset? ExpiresAt);

/// <summary>竞品视角中的单个竞品。</summary>
/// <param name="SubjectId">竞品主体标识（与竞品配置标识相同）。</param>
/// <param name="Name">竞品名称。</param>
/// <param name="Mentions">该提问近 7 天全来源被提及次数。</param>
/// <param name="Rank">按提及次数排序的名次，从 1 开始。</param>
public sealed record ArticleStudioCompetitorView(Guid SubjectId, string Name, long Mentions, int Rank);

/// <summary>「加入竞品」开关所需的竞品可用性。</summary>
/// <param name="Available">是否可开启；为假时前端开关置灰并提示去配置。</param>
/// <param name="Reason">不可用原因：none_configured 未配置竞品、no_recent_data 近 7 天无竞品提及；可用时为空。</param>
/// <param name="ConfiguredCount">品牌已启用的竞品配置数。</param>
/// <param name="Items">近 7 天提及次数前 8 名的已配置竞品。</param>
public sealed record ArticleStudioCompetitorsView(bool Available, ArticleStudioCompetitorReason? Reason,
    int ConfiguredCount, IReadOnlyList<ArticleStudioCompetitorView> Items);

/// <summary>该提问最近一篇文章创作稿件，用于「返回当前稿件」与「重新生成文章」。</summary>
/// <param name="ArticleId">文章标识。</param>
/// <param name="Title">文章标题。</param>
/// <param name="Status">文章生命周期状态。</param>
/// <param name="HasPublication">是否已有有效发布链接；有时重新生成会新建一篇文章。</param>
/// <param name="LatestTaskId">该文章最近一次文章创作任务标识，可用于轮询进度。</param>
/// <param name="LatestTaskStatus">该任务状态。</param>
public sealed record ArticleStudioExistingArticleView(Guid ArticleId, string Title, ArticleStatus Status,
    bool HasPublication, Guid LatestTaskId, ArticleGenerationTaskStatus LatestTaskStatus);

/// <summary>选中提问后的创作准备信息。</summary>
/// <param name="PromptId">监测问题标识。</param>
/// <param name="Text">问题正文。</param>
/// <param name="BrandedStatus">问题类型：branded 品牌词、unbranded 非品牌词、unknown 未分类。</param>
/// <param name="Language">生成语言（由问题语言推导：en 开头为 en-US，其余为 zh-CN）。</param>
/// <param name="OptimizationGoal">本次优化目标。</param>
/// <param name="GoalBasis">优化目标推导依据。</param>
/// <param name="BrandMentions">近 7 天本品牌被提及次数。</param>
/// <param name="EligibleChecks">近 7 天参与推导的已分析回答数。</param>
/// <param name="Knowledge">品牌知识库自动引用结果；只读角色为空。</param>
/// <param name="Competitors">竞品视角可用性与前 8 名竞品。</param>
/// <param name="EngineCount">近 30 天该提问有引用数据的引擎数，用于「N 大引擎」文案；无数据为 0。</param>
/// <param name="ExistingArticle">该提问最近一篇文章创作稿件；没有时为空。</param>
public sealed record ArticleStudioSelectedPromptView(Guid PromptId, string Text, string BrandedStatus, string Language,
    ArticleOptimizationGoal OptimizationGoal, ArticleStudioGoalBasis GoalBasis, long BrandMentions, long EligibleChecks,
    ArticleStudioKnowledgeView? Knowledge, ArticleStudioCompetitorsView Competitors, int EngineCount,
    ArticleStudioExistingArticleView? ExistingArticle);

/// <summary>文章创作第 1 步「创作准备」视图。</summary>
/// <param name="Prompts">可选监测问题（已启用、未归档，最多 100 个）。</param>
/// <param name="Selected">传入 promptId 时的创作准备信息；未传时为空。</param>
/// <param name="UnitPrice">每次生成预占并结算的积分（100）。</param>
/// <param name="AvailableCredits">当前可用积分。</param>
/// <param name="ProviderConfigured">文章生成模型是否已配置；未配置时提交返回 503。</param>
/// <param name="RequirementsMaxLength">自定义写作要求的最大字符数（5000）。</param>
public sealed record ArticleStudioPrepareView(IReadOnlyList<ArticleStudioPromptOption> Prompts,
    ArticleStudioSelectedPromptView? Selected, decimal UnitPrice, decimal AvailableCredits, bool ProviderConfigured,
    int RequirementsMaxLength);

/// <summary>文章创作生成请求；目标平台、语言、策略、篇幅和风格由服务端自动补齐。</summary>
/// <param name="PromptId">监测问题标识（必填）。</param>
/// <param name="MaterialPreviewId">第 1 步返回的素材预览标识；省略或已过期时服务端重新自动引用知识库。</param>
/// <param name="IncludeCompetitors">是否加入竞品视角；竞品不可用时按关闭处理。</param>
/// <param name="Requirements">自定义写作要求，最多 5000 字符，完整进入生成提示词。</param>
/// <param name="ArticleId">重新生成的既有文章标识；无有效发布链接时在同一文章生成新版本，否则新建文章。</param>
public sealed record ArticleStudioGenerationRequest(Guid PromptId, Guid? MaterialPreviewId = null,
    bool IncludeCompetitors = false, string? Requirements = null, Guid? ArticleId = null);

/// <summary>文章创作生成已受理。</summary>
/// <param name="BatchId">生成批次标识。</param>
/// <param name="TaskId">生成任务标识，用于轮询进度。</param>
/// <param name="ArticleId">文章标识（重新生成时为原文章或新建文章）。</param>
/// <param name="ReservedCredits">本次预占积分（100）。</param>
/// <param name="RegeneratedAsNew">请求了重新生成但原文章已有有效发布链接，因此新建了文章。</param>
/// <param name="OptimizationGoal">冻结到任务的优化目标。</param>
/// <param name="IncludeCompetitors">实际是否纳入竞品视角。</param>
/// <param name="WithoutRag">是否以不使用知识库的方式生成。</param>
/// <param name="PollPath">轮询路径。</param>
public sealed record ArticleStudioGenerationAccepted(Guid BatchId, Guid TaskId, Guid ArticleId, decimal ReservedCredits,
    bool RegeneratedAsNew, ArticleOptimizationGoal OptimizationGoal, bool IncludeCompetitors, bool WithoutRag, string PollPath);

/// <summary>写作阶段的公开明细；只含计数、原因和错误码。</summary>
/// <param name="ItemCount">本阶段处理的条目数（引用源、抓取、搜索结果等）。</param>
/// <param name="FetchedCount">阶段 2 已读取正文的引用源数。</param>
/// <param name="BlockedCount">阶段 2 被网页拒绝读取的引用源数。</param>
/// <param name="FailedCount">阶段 2 读取失败的引用源数。</param>
/// <param name="SearchCount">阶段 4 实际执行的搜索次数。</param>
/// <param name="Reason">跳过或失败原因码（如 no_citation_data、web_search_disabled、provider_unsupported）。</param>
/// <param name="ReasonLabel">原因的中文说明。</param>
/// <param name="ErrorCode">失败时的稳定错误码。</param>
public sealed record ArticleStudioStageDetail(int? ItemCount = null, int? FetchedCount = null, int? BlockedCount = null,
    int? FailedCount = null, int? SearchCount = null, string? Reason = null, string? ReasonLabel = null,
    string? ErrorCode = null);

/// <summary>写作阶段进度。</summary>
/// <param name="Key">阶段键。</param>
/// <param name="Position">阶段序号，1–6。</param>
/// <param name="Status">阶段状态。</param>
/// <param name="StartedAt">开始时间（UTC）。</param>
/// <param name="FinishedAt">结束时间（UTC）。</param>
/// <param name="DurationMs">耗时毫秒；未结束时为空。</param>
/// <param name="Detail">公开明细。</param>
public sealed record ArticleStudioStageView(ArticleStudioStageKey Key, int Position, ArticleStudioStageStatus Status,
    DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, long? DurationMs, ArticleStudioStageDetail? Detail);

/// <summary>写作来源（引用源或联网搜索来源）。</summary>
/// <param name="SourceId">来源记录标识。</param>
/// <param name="Kind">来源类型。</param>
/// <param name="Url">规范化链接。</param>
/// <param name="Domain">域名。</param>
/// <param name="Title">网页标题；未知时为空。</param>
/// <param name="Engines">引用该链接的引擎。</param>
/// <param name="CitationCount">近 30 天被引次数（按回答去重）。</param>
/// <param name="IsCompetitor">是否被分类为竞品自有页面。</param>
/// <param name="FetchStatus">网页读取状态。</param>
/// <param name="ErrorCode">读取失败错误码。</param>
/// <param name="ErrorLabel">读取失败的中文说明（如「访问被拒绝」）。</param>
/// <param name="Position">展示顺序，从 1 开始。</param>
public sealed record ArticleStudioSourceView(Guid SourceId, ArticleStudioSourceKind Kind, string Url, string Domain,
    string? Title, IReadOnlyList<string> Engines, int CitationCount, bool IsCompetitor, ArticleStudioFetchStatus FetchStatus,
    string? ErrorCode, string? ErrorLabel, int Position);

/// <summary>文章创作生成任务的轮询视图。</summary>
/// <param name="TaskId">生成任务标识。</param>
/// <param name="BatchId">生成批次标识。</param>
/// <param name="ArticleId">文章标识。</param>
/// <param name="PromptId">监测问题标识。</param>
/// <param name="Status">任务状态。</param>
/// <param name="OptimizationGoal">优化目标。</param>
/// <param name="IncludeCompetitors">是否纳入竞品视角。</param>
/// <param name="AttemptCount">已尝试次数（失败可重试最多 3 次）。</param>
/// <param name="Stages">六个写作阶段，按序返回。</param>
/// <param name="Sources">引用源与联网搜索来源（不含正文）。</param>
/// <param name="FetchedCount">已抓取正文的引用源数（「已抓 X」）。</param>
/// <param name="HitCount">阶段 1 找到的高频引用源数（「命中 Y」）。</param>
/// <param name="VersionId">成功后生成的文章版本标识。</param>
/// <param name="ErrorCode">最近一次失败错误码。</param>
/// <param name="ErrorMessage">最近一次失败的中文说明。</param>
/// <param name="ReservationStatus">积分预占状态。</param>
/// <param name="CreatedAt">任务创建时间（UTC）。</param>
/// <param name="UpdatedAt">任务更新时间（UTC）。</param>
public sealed record ArticleStudioGenerationView(Guid TaskId, Guid BatchId, Guid ArticleId, Guid? PromptId,
    ArticleGenerationTaskStatus Status, ArticleOptimizationGoal? OptimizationGoal, bool IncludeCompetitors, int AttemptCount,
    IReadOnlyList<ArticleStudioStageView> Stages, IReadOnlyList<ArticleStudioSourceView> Sources, int FetchedCount,
    int HitCount, Guid? VersionId, string? ErrorCode, string? ErrorMessage, CreditReservationStatus ReservationStatus,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>阶段 6 生成时追加的文章创作输入；旧向导不使用。</summary>
/// <param name="Goal">优化目标。</param>
/// <param name="StructureInsights">阶段 3 从不可信网页提取的结构洞察；没有时为空。</param>
/// <param name="Competitors">竞品名称及其核验来源链接（不含竞品参数）。</param>
/// <param name="Sources">引用源正文与联网搜索来源，全部作为不可信内容。</param>
/// <param name="WebResearchSummary">阶段 4 联网搜索摘要（不可信内容）；没有时为空。</param>
public sealed record ArticleStudioComposeInput(ArticleOptimizationGoal Goal, string? StructureInsights,
    IReadOnlyList<ArticleStudioCompetitorInput> Competitors, IReadOnlyList<ArticleStudioUntrustedSource> Sources,
    string? WebResearchSummary);

/// <summary>进入生成提示词的竞品名称。</summary>
/// <param name="Name">竞品名称。</param>
/// <param name="VerificationUrls">联网搜索中属于该竞品域名的核验来源链接。</param>
public sealed record ArticleStudioCompetitorInput(string Name, IReadOnlyList<string> VerificationUrls);

/// <summary>进入生成提示词的不可信来源。</summary>
/// <param name="Kind">来源类型。</param>
/// <param name="Url">链接。</param>
/// <param name="Title">标题。</param>
/// <param name="Content">已截断正文；联网搜索来源为空。</param>
public sealed record ArticleStudioUntrustedSource(ArticleStudioSourceKind Kind, string Url, string? Title, string? Content);

/// <summary>文章创作的纯函数规则：优化目标、竞品筛选、自动补齐和中文原因文案。</summary>
public static class ArticleStudioRules
{
    /// <summary>阶段顺序。</summary>
    public static IReadOnlyList<ArticleStudioStageKey> Stages { get; } = Array.AsReadOnly(new[]
    {
        ArticleStudioStageKey.Citations, ArticleStudioStageKey.Fetch, ArticleStudioStageKey.Structure,
        ArticleStudioStageKey.WebResearch, ArticleStudioStageKey.Requirements, ArticleStudioStageKey.Compose
    });

    /// <summary>按近 7 天表现推导优化目标：无数据或未提及→提及优化；提及但不是第一→排名优化；第一（含并列）→巩固优势。</summary>
    /// <param name="eligibleChecks">已完成分析的回答数。</param>
    /// <param name="brandMentions">本品牌被提及次数。</param>
    /// <param name="topCompetitorMentions">被提及最多的竞品的提及次数。</param>
    public static (ArticleOptimizationGoal Goal, ArticleStudioGoalBasis Basis) DeriveGoal(long eligibleChecks,
        long brandMentions, long topCompetitorMentions)
    {
        if (eligibleChecks <= 0) return (ArticleOptimizationGoal.Mention, ArticleStudioGoalBasis.NoData);
        if (brandMentions <= 0) return (ArticleOptimizationGoal.Mention, ArticleStudioGoalBasis.RecentData);
        return brandMentions >= topCompetitorMentions
            ? (ArticleOptimizationGoal.Defend, ArticleStudioGoalBasis.RecentData)
            : (ArticleOptimizationGoal.Ranking, ArticleStudioGoalBasis.RecentData);
    }

    /// <summary>只保留当前启用的已配置竞品、提及次数大于 0，按提及次数降序取前 8。</summary>
    /// <param name="configuredCompetitors">当前启用的竞品配置（标识 → 名称）。</param>
    /// <param name="mentions">主体标识 → 合并后的提及次数（含品牌，品牌会被排除）。</param>
    public static ArticleStudioCompetitorsView SelectCompetitors(IReadOnlyDictionary<Guid, string> configuredCompetitors,
        IReadOnlyDictionary<Guid, long> mentions)
    {
        if (configuredCompetitors.Count == 0)
            return new(false, ArticleStudioCompetitorReason.NoneConfigured, 0, []);
        var items = configuredCompetitors
            .Select(pair => (Id: pair.Key, Name: pair.Value, Mentions: mentions.GetValueOrDefault(pair.Key)))
            .Where(item => item.Mentions > 0)
            .OrderByDescending(item => item.Mentions).ThenBy(item => item.Name, StringComparer.Ordinal).ThenBy(item => item.Id)
            .Take(ArticleStudioLimits.MaxCompetitors)
            .Select((item, index) => new ArticleStudioCompetitorView(item.Id, item.Name, item.Mentions, index + 1))
            .ToArray();
        return items.Length == 0
            ? new(false, ArticleStudioCompetitorReason.NoRecentData, configuredCompetitors.Count, [])
            : new(true, null, configuredCompetitors.Count, items);
    }

    /// <summary>按优化目标选择写作策略：提及→answer_guide，排名→product_selection，巩固→pain_comparison。</summary>
    /// <param name="goal">优化目标。</param>
    public static string StrategyFor(ArticleOptimizationGoal goal) => goal switch
    {
        ArticleOptimizationGoal.Ranking => ArticleWritingRules.ProductSelection,
        ArticleOptimizationGoal.Defend => "pain_comparison",
        _ => "answer_guide"
    };

    /// <summary>由提问语言推导生成语言：en 开头为 en-US，其余为 zh-CN。</summary>
    /// <param name="locale">提问语言标识。</param>
    public static string LanguageFor(string? locale)
        => locale is not null && locale.Trim().StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en-US" : "zh-CN";

    /// <summary>优化目标的 JSON 值。</summary>
    /// <param name="goal">优化目标。</param>
    public static string GoalValue(ArticleOptimizationGoal goal) => goal switch
    {
        ArticleOptimizationGoal.Ranking => "ranking",
        ArticleOptimizationGoal.Defend => "defend",
        _ => "mention"
    };

    /// <summary>解析数据库中的优化目标值；未知值返回空。</summary>
    /// <param name="value">数据库值。</param>
    public static ArticleOptimizationGoal? ParseGoal(string? value) => value switch
    {
        "mention" => ArticleOptimizationGoal.Mention,
        "ranking" => ArticleOptimizationGoal.Ranking,
        "defend" => ArticleOptimizationGoal.Defend,
        _ => null
    };

    /// <summary>阶段键的数据库值。</summary>
    /// <param name="key">阶段键。</param>
    public static string StageValue(ArticleStudioStageKey key) => key switch
    {
        ArticleStudioStageKey.Citations => "citations",
        ArticleStudioStageKey.Fetch => "fetch",
        ArticleStudioStageKey.Structure => "structure",
        ArticleStudioStageKey.WebResearch => "web_research",
        ArticleStudioStageKey.Requirements => "requirements",
        _ => "compose"
    };

    /// <summary>解析阶段键；未知值返回空。</summary>
    /// <param name="value">数据库值。</param>
    public static ArticleStudioStageKey? ParseStage(string? value)
        => Stages.Cast<ArticleStudioStageKey?>().FirstOrDefault(key => StageValue(key!.Value) == value);

    /// <summary>阶段状态的数据库值。</summary>
    /// <param name="status">阶段状态。</param>
    public static string StatusValue(ArticleStudioStageStatus status) => status switch
    {
        ArticleStudioStageStatus.Running => "running",
        ArticleStudioStageStatus.Succeeded => "succeeded",
        ArticleStudioStageStatus.Skipped => "skipped",
        ArticleStudioStageStatus.Failed => "failed",
        _ => "pending"
    };

    /// <summary>解析阶段状态；未知值视为未开始。</summary>
    /// <param name="value">数据库值。</param>
    public static ArticleStudioStageStatus ParseStatus(string? value) => value switch
    {
        "running" => ArticleStudioStageStatus.Running,
        "succeeded" => ArticleStudioStageStatus.Succeeded,
        "skipped" => ArticleStudioStageStatus.Skipped,
        "failed" => ArticleStudioStageStatus.Failed,
        _ => ArticleStudioStageStatus.Pending
    };

    /// <summary>来源读取状态的数据库值。</summary>
    /// <param name="status">读取状态。</param>
    public static string FetchValue(ArticleStudioFetchStatus status) => status switch
    {
        ArticleStudioFetchStatus.Fetched => "fetched",
        ArticleStudioFetchStatus.Blocked => "blocked",
        ArticleStudioFetchStatus.Failed => "failed",
        ArticleStudioFetchStatus.Skipped => "skipped",
        _ => "pending"
    };

    /// <summary>解析来源读取状态；未知值视为待抓取。</summary>
    /// <param name="value">数据库值。</param>
    public static ArticleStudioFetchStatus ParseFetch(string? value) => value switch
    {
        "fetched" => ArticleStudioFetchStatus.Fetched,
        "blocked" => ArticleStudioFetchStatus.Blocked,
        "failed" => ArticleStudioFetchStatus.Failed,
        "skipped" => ArticleStudioFetchStatus.Skipped,
        _ => ArticleStudioFetchStatus.Pending
    };

    /// <summary>网页读取错误码的中文说明；SOURCE_BLOCKED 显示为「访问被拒绝」。</summary>
    /// <param name="errorCode">读取错误码。</param>
    public static string? FetchErrorLabel(string? errorCode) => errorCode switch
    {
        null or "" => null,
        "SOURCE_BLOCKED" => "访问被拒绝",
        "SOURCE_LOGIN_REQUIRED" => "需要登录才能读取",
        "SOURCE_TIMEOUT" => "读取超时",
        "SOURCE_PRIVATE_HOST" or "SOURCE_DNS_FAILED" => "地址无法访问",
        "SOURCE_TOO_LARGE" => "网页过大",
        "SOURCE_UNSUPPORTED_TYPE" => "内容类型不支持",
        "SOURCE_EMPTY" => "没有可读正文",
        "INVALID_SOURCE_URL" => "链接无效",
        _ => "读取失败"
    };

    /// <summary>阶段跳过或失败原因码的中文说明。</summary>
    /// <param name="reason">原因码。</param>
    public static string? ReasonLabel(string? reason) => reason switch
    {
        null or "" => null,
        "no_citation_data" => "该提问近 30 天没有引用源数据",
        "analysis_not_ready" => "引用分析尚未就绪",
        "no_sources" => "没有可抓取的引用源",
        "all_sources_unreadable" => "引用源均无法读取",
        "no_fetched_content" => "没有可用于结构提取的正文",
        "web_search_disabled" => "联网搜索未启用",
        "provider_not_configured" => "联网搜索服务未配置",
        "provider_unsupported" => "当前模型服务不支持联网搜索",
        "provider_failed" => "联网搜索调用失败",
        "structure_failed" => "结构提取失败",
        "citations_failed" => "引用源读取失败",
        "compose_failed" => "文章生成失败",
        _ => "阶段未完成"
    };
}
