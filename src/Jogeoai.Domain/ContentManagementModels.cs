using System.Text.Json.Serialization;

namespace Jogeoai.Domain;

/// <summary>文章生成或导入来源。</summary>
public enum ArticleSourceKind
{
    /// <summary>由 AI 生成。</summary>
    [JsonStringEnumMemberName("ai_generated")] AiGenerated,
    /// <summary>从已发布链接导入。</summary>
    [JsonStringEnumMemberName("published_import")] PublishedImport,
    /// <summary>大模型回答引用品牌域名链接时自动发现并建文。</summary>
    [JsonStringEnumMemberName("citation_discovered")] CitationDiscovered
}

/// <summary>文章生命周期状态。</summary>
public enum ArticleStatus
{
    /// <summary>可编辑的文章。</summary>
    [JsonStringEnumMemberName("active")] Active,
    /// <summary>已归档文章。</summary>
    [JsonStringEnumMemberName("archived")] Archived
}

/// <summary>文章监控状态。</summary>
public enum ArticleMonitorStatus
{
    /// <summary>未启用监控。</summary>
    [JsonStringEnumMemberName("inactive")] Inactive,
    /// <summary>持续监控。</summary>
    [JsonStringEnumMemberName("active")] Active,
    /// <summary>暂时暂停。</summary>
    [JsonStringEnumMemberName("paused")] Paused
}

/// <summary>文章版本来源。</summary>
public enum ArticleVersionKind
{
    /// <summary>生成版本。</summary>
    [JsonStringEnumMemberName("generated")] Generated,
    /// <summary>手工编辑版本。</summary>
    [JsonStringEnumMemberName("manual_edit")] ManualEdit,
    /// <summary>链接导入版本。</summary>
    [JsonStringEnumMemberName("imported")] Imported,
    /// <summary>智能微调结果版本（迁移 041）。</summary>
    [JsonStringEnumMemberName("refined")] Refined
}

/// <summary>生成批次状态。</summary>
public enum ArticleGenerationBatchStatus
{
    /// <summary>等待执行。</summary>
    [JsonStringEnumMemberName("queued")] Queued,
    /// <summary>正在执行。</summary>
    [JsonStringEnumMemberName("running")] Running,
    /// <summary>全部成功。</summary>
    [JsonStringEnumMemberName("succeeded")] Succeeded,
    /// <summary>部分成功。</summary>
    [JsonStringEnumMemberName("partial")] Partial,
    /// <summary>全部失败。</summary>
    [JsonStringEnumMemberName("failed")] Failed,
    /// <summary>已取消。</summary>
    [JsonStringEnumMemberName("cancelled")] Cancelled
}

/// <summary>生成任务状态。</summary>
public enum ArticleGenerationTaskStatus
{
    /// <summary>待领取。</summary>
    [JsonStringEnumMemberName("pending")] Pending,
    /// <summary>正在生成。</summary>
    [JsonStringEnumMemberName("running")] Running,
    /// <summary>生成成功。</summary>
    [JsonStringEnumMemberName("succeeded")] Succeeded,
    /// <summary>生成失败。</summary>
    [JsonStringEnumMemberName("failed")] Failed,
    /// <summary>已取消。</summary>
    [JsonStringEnumMemberName("cancelled")] Cancelled,
    /// <summary>租约过期后的迟到结果。</summary>
    [JsonStringEnumMemberName("stale")] Stale
}

/// <summary>发布链接状态。</summary>
public enum ArticlePublicationStatus
{
    /// <summary>有效链接。</summary>
    [JsonStringEnumMemberName("active")] Active,
    /// <summary>已停用链接。</summary>
    [JsonStringEnumMemberName("disabled")] Disabled
}

/// <summary>积分预占状态。</summary>
public enum CreditReservationStatus
{
    /// <summary>已预占。</summary>
    [JsonStringEnumMemberName("held")] Held,
    /// <summary>已结算。</summary>
    [JsonStringEnumMemberName("captured")] Captured,
    /// <summary>已释放。</summary>
    [JsonStringEnumMemberName("released")] Released
}

/// <summary>积分账本流水类型。</summary>
public enum CreditLedgerEntryType
{
    /// <summary>运营入账。</summary>
    [JsonStringEnumMemberName("grant")] Grant,
    /// <summary>任务预占。</summary>
    [JsonStringEnumMemberName("reserve")] Reserve,
    /// <summary>任务成功扣费。</summary>
    [JsonStringEnumMemberName("capture")] Capture,
    /// <summary>任务失败释放。</summary>
    [JsonStringEnumMemberName("release")] Release,
    /// <summary>冲正流水。</summary>
    [JsonStringEnumMemberName("reversal")] Reversal,
    /// <summary>周期结束清零可用积分。</summary>
    [JsonStringEnumMemberName("expire")] Expire
}

/// <summary>文章列表中的引用摘要。</summary>
/// <param name="CitationCount">去重后的文章引用次数。</param>
/// <param name="Trend">最近窗口的每日引用次数。</param>
/// <param name="LastObservedAt">最近一次观测时间。</param>
/// <param name="Quality">数据质量状态。</param>
/// <param name="Engines">统计区间内引用过本文的大模型平台代码（visibility_checks.engine），去重并按各平台引用次数降序、同次数按代码升序；没有引用时为空数组。指定引用平台筛选时只含该平台。</param>
/// <param name="FirstObservedAt">统计区间内最早一次被引用的观测时间；没有引用时为空。</param>
public sealed record ArticleCitationSummary(long? CitationCount, IReadOnlyList<ArticleTrendPoint> Trend,
    DateTimeOffset? LastObservedAt, string Quality, IReadOnlyList<string>? Engines = null,
    DateTimeOffset? FirstObservedAt = null);

/// <summary>文章引用趋势点。</summary>
/// <param name="Date">站点时区下的自然日。</param>
/// <param name="CitationCount">当天去重后的引用次数。</param>
public sealed record ArticleTrendPoint(DateOnly Date, long CitationCount);

/// <summary>文章列表视图。</summary>
/// <param name="ArticleId">接口字段。</param>
/// <param name="TenantId">接口字段。</param>
/// <param name="SiteId">接口字段。</param>
/// <param name="CreatedBy">接口字段。</param>
/// <param name="SourceKind">接口字段。</param>
/// <param name="Status">接口字段。</param>
/// <param name="Title">接口字段。</param>
/// <param name="Language">接口字段。</param>
/// <param name="MonitorStatus">接口字段。</param>
/// <param name="CurrentVersionNo">接口字段。</param>
/// <param name="Tags">接口字段。</param>
/// <param name="PublicationLinkCount">接口字段。</param>
/// <param name="CitationSummary">接口字段。</param>
/// <param name="CreatedAt">接口字段。</param>
/// <param name="UpdatedAt">接口字段。</param>
/// <param name="ArchivedAt">接口字段。</param>
/// <param name="Stage">后端派生的工作台阶段，与页签计数及详情使用同一规则；前端不自行推断。</param>
/// <param name="Prompt">文章关联提问的冻结摘要；导入文章或没有生成任务时为空。</param>
/// <param name="Generation">最新一次生成任务的状态摘要；导入文章或没有生成任务时为空。</param>
/// <param name="OwnedPublication">是否存在归属品牌官网域名的有效（未停用）发布链接。</param>
/// <param name="Discovery">自动发现来源摘要（最早引用该链接的回答）；非自动发现文章为空。</param>
/// <param name="PrimaryLink">主发布链接：有效（未停用）链接中官网域名优先，其次发布时间（未填写时取登记时间）最早，再按链接标识；没有有效链接时为空。仅文章列表返回，详情接口为空。</param>
public sealed record ArticleView(Guid ArticleId, Guid TenantId, Guid SiteId, Guid CreatedBy,
    ArticleSourceKind SourceKind, ArticleStatus Status, string Title, string Language,
    ArticleMonitorStatus MonitorStatus, int CurrentVersionNo, IReadOnlyList<string> Tags,
    int PublicationLinkCount, ArticleCitationSummary CitationSummary, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, DateTimeOffset? ArchivedAt = null,
    ArticleStage Stage = ArticleStage.PendingPublish, ArticlePromptSummary? Prompt = null,
    ArticleGenerationSummary? Generation = null, bool OwnedPublication = false,
    ArticleDiscoverySummary? Discovery = null, ArticlePrimaryLinkView? PrimaryLink = null);

/// <summary>文章列表中的主发布链接摘要。</summary>
/// <param name="LinkId">发布链接标识。</param>
/// <param name="Url">用户登记的原始发布网址。</param>
/// <param name="PublishedAt">登记时填写的发布时间；未填写时为空。</param>
/// <param name="OwnershipMatch">是否归属品牌官网域名；品牌域名或链接主机无法判断时为空。</param>
public sealed record ArticlePrimaryLinkView(Guid LinkId, string Url, DateTimeOffset? PublishedAt, bool? OwnershipMatch);

/// <summary>文章列表的被引计数，用于「全部 / 已被引用 / 未被引用」切换。</summary>
/// <param name="All">除「是否被引用」与游标外全部筛选（含状态、阶段、引用平台、域名归属）命中的文章数，等于 Cited 与 Uncited 之和。</param>
/// <param name="Cited">其中统计区间内引用次数大于 0 的文章数。</param>
/// <param name="Uncited">其中统计区间内引用次数为 0 的文章数，包含没有发布链接的文章（待发布、生成中、生成失败）。</param>
public sealed record ArticleCitedCounts(long All, long Cited, long Uncited);

/// <summary>文章列表的域名归属筛选。</summary>
public enum ArticleOwnershipFilter
{
    /// <summary>存在归属品牌官网域名的有效（未停用）发布链接。</summary>
    [JsonStringEnumMemberName("owned")] Owned,
    /// <summary>存在有效发布链接，且其中没有归属品牌官网域名的链接（含归属无法判断的链接）。</summary>
    [JsonStringEnumMemberName("external")] External
}

/// <summary>文章在工作台中的派生阶段；由后端在列表、页签计数和详情中统一派生。</summary>
public enum ArticleStage
{
    /// <summary>生成中：没有成功版本，且最新生成任务为待领取/正在生成。</summary>
    [JsonStringEnumMemberName("generating")] Generating,
    /// <summary>生成失败：没有成功版本，且最新生成任务为失败/取消/过期。</summary>
    [JsonStringEnumMemberName("generation_failed")] GenerationFailed,
    /// <summary>待发布：有当前版本，没有有效（未停用）发布链接。</summary>
    [JsonStringEnumMemberName("pending_publish")] PendingPublish,
    /// <summary>发布中：有至少一个有效（未停用）发布链接，但追踪状态为未开启或已暂停。</summary>
    [JsonStringEnumMemberName("publishing")] Publishing,
    /// <summary>已追踪：有至少一个有效发布链接，且追踪状态为 active；已追踪不等于已被引用。</summary>
    [JsonStringEnumMemberName("tracking")] Tracking,
    /// <summary>已归档：文章 status=archived，仅在「已归档」筛选中出现。</summary>
    [JsonStringEnumMemberName("archived")] Archived
}

/// <summary>文章派生阶段的唯一规则；判断顺序与 ContentStore 列表 SQL 的阶段 CASE 表达式完全一致。</summary>
public static class ArticleStageRules
{
    /// <summary>全部阶段的 JSON 名称，按页签顺序排列；页签计数始终包含这六个键。</summary>
    public static IReadOnlyList<string> Keys { get; } =
        ["generating", "generation_failed", "pending_publish", "publishing", "tracking", "archived"];

    /// <summary>按「归档 → 生成中 → 生成失败 → 待发布 → 已追踪 → 发布中」的顺序派生阶段。</summary>
    /// <param name="archived">文章是否已归档。</param>
    /// <param name="hasCurrentVersion">文章是否已有当前版本。</param>
    /// <param name="latestTask">最新一次生成任务状态；没有生成任务时为空。</param>
    /// <param name="activeLinks">有效（未停用）发布链接数量。</param>
    /// <param name="monitor">文章追踪状态。</param>
    /// <returns>文章派生阶段；无版本且无任务（仅可能是导入残留）不伪造为生成失败，按链接与追踪状态继续判断。</returns>
    public static ArticleStage Derive(bool archived, bool hasCurrentVersion, ArticleGenerationTaskStatus? latestTask,
        long activeLinks, ArticleMonitorStatus monitor)
    {
        if (archived) return ArticleStage.Archived;
        if (!hasCurrentVersion && latestTask is ArticleGenerationTaskStatus.Pending or ArticleGenerationTaskStatus.Running)
            return ArticleStage.Generating;
        if (!hasCurrentVersion && latestTask is ArticleGenerationTaskStatus.Failed or ArticleGenerationTaskStatus.Cancelled
                or ArticleGenerationTaskStatus.Stale)
            return ArticleStage.GenerationFailed;
        if (activeLinks <= 0) return ArticleStage.PendingPublish;
        return monitor == ArticleMonitorStatus.Active ? ArticleStage.Tracking : ArticleStage.Publishing;
    }

    /// <summary>返回阶段的 JSON 名称（同时用作 SQL 派生值和页签计数键）。</summary>
    /// <param name="stage">文章阶段。</param>
    public static string Key(ArticleStage stage) => stage switch
    {
        ArticleStage.Generating => "generating",
        ArticleStage.GenerationFailed => "generation_failed",
        ArticleStage.PendingPublish => "pending_publish",
        ArticleStage.Publishing => "publishing",
        ArticleStage.Tracking => "tracking",
        ArticleStage.Archived => "archived",
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "未知文章阶段。")
    };

    /// <summary>把 JSON 名称解析为阶段；未知值返回 false。</summary>
    /// <param name="value">阶段 JSON 名称。</param>
    /// <param name="stage">解析出的阶段。</param>
    public static bool TryParse(string? value, out ArticleStage stage)
    {
        foreach (var candidate in Enum.GetValues<ArticleStage>())
        {
            if (string.Equals(Key(candidate), value, StringComparison.Ordinal))
            {
                stage = candidate;
                return true;
            }
        }
        stage = ArticleStage.PendingPublish;
        return false;
    }
}

/// <summary>文章关联提问的冻结摘要，来自生成任务的 prompt_snapshot。</summary>
/// <param name="PromptId">提问标识。</param>
/// <param name="Content">生成时冻结的提问内容。</param>
/// <param name="Locale">提问语言地区。</param>
/// <param name="GroupId">提问所属分组标识；未分组时为空。</param>
public sealed record ArticlePromptSummary(Guid PromptId, string Content, string Locale, Guid? GroupId);

/// <summary>文章最新一次生成任务的状态摘要。</summary>
/// <param name="TaskId">生成任务标识。</param>
/// <param name="BatchId">所属生成批次标识，可用于取消待领取任务。</param>
/// <param name="Status">任务状态。</param>
/// <param name="AttemptCount">已尝试次数。</param>
/// <param name="ErrorCode">最近一次失败的错误码；没有失败时为空。</param>
/// <param name="ErrorMessage">最近一次失败的说明；没有失败时为空。</param>
/// <param name="QueuedAt">任务入队 UTC 时间，用于判断排队等待是否超时。</param>
/// <param name="UpdatedAt">任务最近更新 UTC 时间。</param>
/// <param name="ReservationStatus">本篇积分预占状态：已预占、已结算或已释放。</param>
public sealed record ArticleGenerationSummary(Guid TaskId, Guid BatchId, ArticleGenerationTaskStatus Status,
    int AttemptCount, string? ErrorCode, string? ErrorMessage, DateTimeOffset QueuedAt,
    DateTimeOffset UpdatedAt, CreditReservationStatus ReservationStatus);

/// <summary>文章列表分页；计数为同一筛选下的汇总，不随游标翻页变化。</summary>
/// <param name="Items">本页文章。</param>
/// <param name="NextCursor">下一页游标；没有下一页时为空。</param>
/// <param name="TotalCount">当前全部筛选（含阶段与状态筛选，不含游标）命中的文章总数。</param>
/// <param name="ActiveLinkCount">与 TotalCount 同口径文章的有效（未停用）发布链接总数。</param>
/// <param name="StageCounts">各阶段文章数，固定包含六个阶段键（可为 0）；按除阶段、状态与游标外的同一筛选计算，用于页签计数。</param>
/// <param name="CitedCounts">被引计数：按除「是否被引用」与游标外的全部筛选计算；引用口径随统计区间与引用平台。</param>
public sealed record ArticleListPage(IReadOnlyList<ArticleView> Items, string? NextCursor,
    long TotalCount, long ActiveLinkCount, IReadOnlyDictionary<string, long> StageCounts,
    ArticleCitedCounts? CitedCounts = null);

/// <summary>文章生成依据摘要（详情使用），不包含证据正文、参考正文或完整提示词。</summary>
/// <param name="TaskId">最新生成任务标识。</param>
/// <param name="BatchId">所属生成批次标识。</param>
/// <param name="Status">最新生成任务状态。</param>
/// <param name="Prompt">关联提问的冻结摘要。</param>
/// <param name="Targets">提交时冻结的目标大模型平台。</param>
/// <param name="Strategy">写作策略。</param>
/// <param name="LengthProfile">篇幅档位。</param>
/// <param name="Style">写作风格。</param>
/// <param name="Audience">目标受众；未填写时为空。</param>
/// <param name="Keywords">主关键词。</param>
/// <param name="ExtraRequirements">写作补充要求；未填写时为空。</param>
/// <param name="MaterialNotes">文章重点或素材备注；未填写时为空。</param>
/// <param name="References">仿写参考链接及读取状态。</param>
/// <param name="WithoutRag">是否在未使用知识库资料的情况下生成。</param>
/// <param name="KnowledgeHitCount">冻结素材上下文中的知识命中片段数；未使用知识库时为 0。</param>
/// <param name="ErrorCode">最新任务失败错误码；没有失败时为空。</param>
/// <param name="ErrorMessage">最新任务失败说明；没有失败时为空。</param>
public sealed record ArticleGenerationBasisView(Guid TaskId, Guid BatchId, ArticleGenerationTaskStatus Status,
    ArticlePromptSummary? Prompt, IReadOnlyList<ArticleTargetSummary> Targets, string Strategy,
    string LengthProfile, string Style, string? Audience, IReadOnlyList<string> Keywords,
    string? ExtraRequirements, string? MaterialNotes, IReadOnlyList<ArticleReferenceSummary> References,
    bool WithoutRag, int KnowledgeHitCount, string? ErrorCode, string? ErrorMessage);

/// <summary>生成时冻结的目标大模型平台摘要。</summary>
/// <param name="TargetId">站点目标标识；目录项为空。</param>
/// <param name="Provider">采集 Provider。</param>
/// <param name="Engine">大模型引擎代码。</param>
/// <param name="DisplayName">前端显示名称。</param>
/// <param name="Country">国家代码。</param>
/// <param name="Locale">语言地区。</param>
public sealed record ArticleTargetSummary(Guid? TargetId, string Provider, string Engine, string DisplayName,
    string Country, string Locale);

/// <summary>仿写参考链接摘要，不含正文。</summary>
/// <param name="Url">规范化后的参考链接。</param>
/// <param name="ReadStatus">提交时的正文读取状态。</param>
/// <param name="Title">读取到的标题；未读取到时为空。</param>
public sealed record ArticleReferenceSummary(string Url, string ReadStatus, string? Title);

/// <summary>文章版本视图。</summary>
/// <param name="VersionId">接口字段。</param>
/// <param name="ArticleId">接口字段。</param>
/// <param name="VersionNo">接口字段。</param>
/// <param name="VersionKind">接口字段。</param>
/// <param name="Title">接口字段。</param>
/// <param name="Summary">接口字段。</param>
/// <param name="BodyMarkdown">接口字段。</param>
/// <param name="SeoTitle">接口字段。</param>
/// <param name="SeoDescription">接口字段。</param>
/// <param name="Keywords">接口字段。</param>
/// <param name="ContentLength">接口字段。</param>
/// <param name="Language">接口字段。</param>
/// <param name="GenerationBatchId">接口字段。</param>
/// <param name="KnowledgeContextId">接口字段。</param>
/// <param name="Provider">接口字段。</param>
/// <param name="Model">接口字段。</param>
/// <param name="StrategyKey">接口字段。</param>
/// <param name="StrategyVersion">接口字段。</param>
/// <param name="CreatedBy">接口字段。</param>
/// <param name="CreatedAt">接口字段。</param>
/// <param name="WithoutRag">接口字段。</param>
/// <param name="EvidenceIds">接口字段。</param>
/// <param name="Uncertainties">接口字段。</param>
/// <param name="Disclosure">接口字段。</param>
/// <param name="EditRevision">草稿修订号：人工编辑版本被自动保存原地覆盖的次数，作为自动保存的乐观锁；其他版本为 0，迁移 041 之前恒为 0。</param>
public sealed record ArticleVersionView(Guid VersionId, Guid ArticleId, int VersionNo,
    ArticleVersionKind VersionKind, string Title, string? Summary, string BodyMarkdown,
    string? SeoTitle, string? SeoDescription, IReadOnlyList<string> Keywords, int ContentLength,
    string Language, Guid? GenerationBatchId, Guid? KnowledgeContextId, string? Provider,
    string? Model, string? StrategyKey, string? StrategyVersion, Guid CreatedBy,
    DateTimeOffset CreatedAt, bool WithoutRag = false, IReadOnlyList<string>? EvidenceIds = null,
    IReadOnlyList<string>? Uncertainties = null, string? Disclosure = null, int EditRevision = 0);

/// <summary>文章发布链接。</summary>
/// <param name="LinkId">接口字段。</param>
/// <param name="ArticleId">接口字段。</param>
/// <param name="ArticleVersionId">接口字段。</param>
/// <param name="UrlOriginal">接口字段。</param>
/// <param name="UrlNormalized">接口字段。</param>
/// <param name="UrlHash">接口字段。</param>
/// <param name="Channel">接口字段。</param>
/// <param name="PublishedAt">接口字段。</param>
/// <param name="Status">接口字段。</param>
/// <param name="OwnershipMatch">接口字段。</param>
/// <param name="ReadStatus">接口字段。</param>
/// <param name="CreatedAt">接口字段。</param>
/// <param name="DisabledAt">接口字段。</param>
/// <param name="DiscoveredCheckId">自动发现来源：最早引用该链接的回答 Check；手动登记或导入的链接为空。</param>
/// <param name="DiscoveredAt">自动发现时间（UTC）；手动登记或导入的链接为空。</param>
public sealed record ArticlePublicationLinkView(Guid LinkId, Guid ArticleId, Guid? ArticleVersionId,
    string UrlOriginal, string UrlNormalized, string UrlHash, string Channel,
    DateTimeOffset? PublishedAt, ArticlePublicationStatus Status, bool? OwnershipMatch,
    string ReadStatus, DateTimeOffset CreatedAt, DateTimeOffset? DisabledAt = null,
    Guid? DiscoveredCheckId = null, DateTimeOffset? DiscoveredAt = null);

/// <summary>文章详情视图。</summary>
/// <param name="Article">接口字段。</param>
/// <param name="CurrentVersion">接口字段。</param>
/// <param name="Versions">接口字段。</param>
/// <param name="PublicationLinks">接口字段。</param>
/// <param name="GenerationWarnings">接口字段。</param>
/// <param name="CreditStatus">接口字段；reservationStatus 为最新生成任务的积分预占状态（held/captured/released），没有生成任务时为空。</param>
/// <param name="Generation">最新生成任务的生成依据摘要；导入文章或没有生成任务时为空。</param>
public sealed record ArticleDetailView(ArticleView Article, ArticleVersionView? CurrentVersion,
    IReadOnlyList<ArticleVersionView> Versions, IReadOnlyList<ArticlePublicationLinkView> PublicationLinks,
    IReadOnlyList<string> GenerationWarnings, IReadOnlyDictionary<string, string?> CreditStatus,
    ArticleGenerationBasisView? Generation = null);

/// <summary>文章列表筛选和排序。</summary>
/// <param name="Query">接口字段。</param>
/// <param name="Status">接口字段。</param>
/// <param name="SourceKind">接口字段。</param>
/// <param name="MonitorStatus">接口字段。</param>
/// <param name="CreatedBy">接口字段。</param>
/// <param name="TagId">接口字段。</param>
/// <param name="CreatedFrom">接口字段。</param>
/// <param name="CreatedTo">接口字段。</param>
/// <param name="Sort">接口字段。</param>
/// <param name="Cursor">接口字段。</param>
/// <param name="Limit">接口字段。</param>
/// <param name="PromptId">生成文章关联的主问题筛选。</param>
/// <param name="TargetId">生成文章关联的目标平台筛选。</param>
/// <param name="Stage">派生阶段筛选。</param>
/// <param name="Language">文章语言筛选，仅支持 zh-CN 或 en-US。</param>
/// <param name="OwnedOnly">是否只返回存在官网域名有效发布链接的文章；等同于 Ownership=Owned，二者冲突时返回 INVALID_FILTER。</param>
/// <param name="CitationDays">引用统计区间：站点时区近 7、30 或 90 个自然日，默认 30；其他取值返回 INVALID_FILTER。</param>
/// <param name="Cited">是否被引用：true 只返回区间内引用次数大于 0 的文章，false 只返回等于 0 的文章（含没有发布链接的文章）；为空不筛选。</param>
/// <param name="CitationEngine">引用平台代码（visibility_checks.engine，规范化为小写）；设置后引用次数、平台、最早/最近被引、被引判定、被引计数与按引用排序只统计该平台。</param>
/// <param name="Ownership">域名归属筛选：owned 或 external；为空不筛选。</param>
public sealed record ArticleListFilter(string? Query = null, ArticleStatus? Status = null,
    ArticleSourceKind? SourceKind = null, ArticleMonitorStatus? MonitorStatus = null,
    Guid? CreatedBy = null, Guid? TagId = null, DateTimeOffset? CreatedFrom = null,
    DateTimeOffset? CreatedTo = null, string Sort = "created_desc", string? Cursor = null, int Limit = 20,
    Guid? PromptId = null, Guid? TargetId = null, ArticleStage? Stage = null, string? Language = null,
    bool OwnedOnly = false, int CitationDays = 30, bool? Cited = null, string? CitationEngine = null,
    ArticleOwnershipFilter? Ownership = null);

/// <summary>文章手工编辑请求。</summary>
/// <param name="Title">接口字段。</param>
/// <param name="BodyMarkdown">接口字段。</param>
/// <param name="Summary">接口字段。</param>
/// <param name="SeoTitle">接口字段。</param>
/// <param name="SeoDescription">接口字段。</param>
/// <param name="Keywords">接口字段。</param>
/// <param name="ExpectedVersionNo">接口字段。</param>
/// <param name="Language">接口字段。</param>
/// <param name="TagIds">接口字段。</param>
/// <param name="ExpectedRevision">可选：当前版本的草稿修订号（editRevision）。提供时与当前版本不一致返回 409 ARTICLE_VERSION_CONFLICT，防止旧页面覆盖已自动保存的草稿；不提供时保持旧行为。</param>
public sealed record ArticleEditRequest(string Title, string BodyMarkdown, string? Summary = null,
    string? SeoTitle = null, string? SeoDescription = null, IReadOnlyList<string>? Keywords = null,
    int? ExpectedVersionNo = null, string Language = "zh-CN", IReadOnlyList<Guid>? TagIds = null,
    int? ExpectedRevision = null);

/// <summary>新增文章发布链接请求。</summary>
/// <param name="Url">接口字段。</param>
/// <param name="Channel">接口字段。</param>
/// <param name="PublishedAt">接口字段。</param>
/// <param name="ArticleVersionId">接口字段。</param>
/// <param name="OwnershipMatch">接口字段。</param>
public sealed record ArticlePublicationLinkWrite(string Url, string? Channel = null,
    DateTimeOffset? PublishedAt = null, Guid? ArticleVersionId = null, bool? OwnershipMatch = null);

/// <summary>导入链接预览单项。</summary>
/// <param name="UrlOriginal">接口字段。</param>
/// <param name="UrlNormalized">接口字段。</param>
/// <param name="UrlHash">接口字段。</param>
/// <param name="Title">接口字段。</param>
/// <param name="BodyMarkdown">接口字段。</param>
/// <param name="ReadStatus">接口字段。</param>
/// <param name="ErrorCode">接口字段。</param>
/// <param name="ErrorMessage">接口字段。</param>
/// <param name="Duplicate">接口字段。</param>
/// <param name="ExistingArticleId">接口字段。</param>
public sealed record ArticleImportPreviewItem(string UrlOriginal, string? UrlNormalized, string? UrlHash,
    string? Title, string? BodyMarkdown, string ReadStatus, string? ErrorCode, string? ErrorMessage,
    bool Duplicate, Guid? ExistingArticleId);

/// <summary>导入链接预览结果。</summary>
/// <param name="PreviewId">接口字段。</param>
/// <param name="RequestHash">接口字段。</param>
/// <param name="Items">接口字段。</param>
/// <param name="ExpiresAt">接口字段。</param>
public sealed record ArticleImportPreviewView(Guid PreviewId, string RequestHash,
    IReadOnlyList<ArticleImportPreviewItem> Items, DateTimeOffset ExpiresAt);

/// <summary>确认导入链接请求。</summary>
/// <param name="PreviewId">接口字段。</param>
/// <param name="RequestHash">接口字段。</param>
/// <param name="Channels">接口字段。</param>
/// <param name="TitleOverrides">不可读链接的手动标题。</param>
public sealed record ArticleImportConfirmRequest(Guid PreviewId, string RequestHash,
    IReadOnlyList<string>? Channels = null,
    IReadOnlyList<ArticleImportTitleOverride>? TitleOverrides = null);

/// <summary>导入链接的手动标题覆盖。</summary>
/// <param name="UrlHash">预览结果中的规范 URL 哈希。</param>
/// <param name="Title">用户确认的文章标题。</param>
public sealed record ArticleImportTitleOverride(string UrlHash, string Title);

/// <summary>生成素材选择。</summary>
/// <param name="ScopeMode">接口字段。</param>
/// <param name="TagNames">接口字段。</param>
/// <param name="FolderIds">接口字段。</param>
/// <param name="DocumentIds">接口字段。</param>
/// <param name="DocumentType">接口字段。</param>
/// <param name="ProductKey">接口字段。</param>
/// <param name="SourceLanguage">接口字段。</param>
/// <param name="MaxChunks">接口字段。</param>
/// <param name="MaxCharacters">接口字段。</param>
public sealed record ArticleKnowledgeSelection(KnowledgeScopeMode ScopeMode = KnowledgeScopeMode.Default,
    IReadOnlyList<string>? TagNames = null, IReadOnlyList<Guid>? FolderIds = null,
    IReadOnlyList<Guid>? DocumentIds = null, string? DocumentType = null, string? ProductKey = null,
    string? SourceLanguage = null, int MaxChunks = 24, int MaxCharacters = 24000);

/// <summary>文章参考素材预览请求。</summary>
/// <param name="PromptIds">接口字段。</param>
/// <param name="Language">接口字段。</param>
/// <param name="Topic">接口字段。</param>
/// <param name="KnowledgeSelection">接口字段。</param>
/// <param name="MaterialNotes">接口字段。</param>
/// <param name="WithoutRag">接口字段。</param>
public sealed record ArticleMaterialPreviewRequest(IReadOnlyList<Guid> PromptIds,
    string? Language = "zh-CN", string? Topic = null, ArticleKnowledgeSelection? KnowledgeSelection = null,
    string? MaterialNotes = null, bool WithoutRag = false);

/// <summary>素材命中摘要。</summary>
/// <param name="EvidenceId">接口字段。</param>
/// <param name="DocumentId">接口字段。</param>
/// <param name="VersionId">接口字段。</param>
/// <param name="Title">接口字段。</param>
/// <param name="FileName">接口字段。</param>
/// <param name="Location">接口字段。</param>
/// <param name="Scope">接口字段。</param>
/// <param name="Tags">接口字段。</param>
/// <param name="Score">接口字段。</param>
/// <param name="CharacterCount">接口字段。</param>
public sealed record ArticleMaterialHit(string EvidenceId, Guid DocumentId, Guid VersionId,
    string Title, string? FileName, string? Location, string Scope, IReadOnlyList<string> Tags,
    double Score, int CharacterCount);

/// <summary>文章参考素材预览。</summary>
/// <param name="MaterialPreviewId">接口字段。</param>
/// <param name="ContextId">接口字段。</param>
/// <param name="ContextSnapshotHash">接口字段。</param>
/// <param name="Selection">接口字段。</param>
/// <param name="Hits">接口字段。</param>
/// <param name="Gaps">接口字段。</param>
/// <param name="Conflicts">接口字段。</param>
/// <param name="HitCount">接口字段。</param>
/// <param name="CharacterCount">接口字段。</param>
/// <param name="ExpiresAt">接口字段。</param>
/// <param name="WithoutRag">接口字段。</param>
/// <param name="MaterialNotes">接口字段。</param>
/// <param name="PromptIds">接口字段。</param>
/// <param name="Language">接口字段。</param>
/// <param name="Topic">接口字段。</param>
public sealed record ArticleMaterialPreviewView(Guid MaterialPreviewId, Guid? ContextId,
    string ContextSnapshotHash, ArticleKnowledgeSelection Selection, IReadOnlyList<ArticleMaterialHit> Hits,
    IReadOnlyList<string> Gaps, IReadOnlyList<KnowledgeConflict> Conflicts, int HitCount,
    int CharacterCount, DateTimeOffset ExpiresAt, bool WithoutRag, string? MaterialNotes = null,
    IReadOnlyList<Guid>? PromptIds = null, string? Language = null, string? Topic = null);

/// <summary>文章生成预览请求。</summary>
/// <param name="PromptIds">接口字段。</param>
/// <param name="TargetIds">接口字段。</param>
/// <param name="Language">接口字段。</param>
/// <param name="Strategy">文章类型：answer_guide 问题解答、product_selection 产品选型、pain_comparison 方案比较、reference_structure 参考范文。</param>
/// <param name="LengthProfile">篇幅档位 short、medium、long；产品选型中文中篇为 2000–3000 字，其余沿用对应语言的既有范围。</param>
/// <param name="Style">接口字段。</param>
/// <param name="Audience">接口字段。</param>
/// <param name="Keywords">接口字段。</param>
/// <param name="ExtraRequirements">接口字段。</param>
/// <param name="ReferenceUrls">接口字段。</param>
/// <param name="MaterialPreviewId">接口字段。</param>
/// <param name="WithoutRag">接口字段。</param>
/// <param name="MaterialNotes">接口字段。</param>
/// <param name="TagIds">接口字段。</param>
public sealed record ArticleGenerationPreviewRequest(IReadOnlyList<Guid> PromptIds,
    IReadOnlyList<Guid> TargetIds, string Language = "zh-CN", string Strategy = "answer_guide",
    string LengthProfile = "medium", string Style = "professional", string? Audience = null,
    IReadOnlyList<string>? Keywords = null, string? ExtraRequirements = null,
    IReadOnlyList<string>? ReferenceUrls = null, Guid? MaterialPreviewId = null,
    bool WithoutRag = false, string? MaterialNotes = null, IReadOnlyList<Guid>? TagIds = null);

/// <summary>单价和文章数量的服务端费用预览。</summary>
/// <param name="PreviewId">接口字段。</param>
/// <param name="RequestHash">接口字段。</param>
/// <param name="PromptIds">接口字段。</param>
/// <param name="TargetIds">接口字段。</param>
/// <param name="ArticleCount">接口字段。</param>
/// <param name="UnitPrice">接口字段。</param>
/// <param name="TotalCredits">接口字段。</param>
/// <param name="AvailableCredits">接口字段。</param>
/// <param name="ExpiresAt">接口字段。</param>
/// <param name="Warnings">接口字段。</param>
/// <param name="QualityChecks">接口字段。</param>
/// <param name="MaterialPreviewId">接口字段。</param>
/// <param name="WithoutRag">接口字段。</param>
public sealed record ArticleGenerationPreviewView(Guid PreviewId, string RequestHash,
    IReadOnlyList<Guid> PromptIds, IReadOnlyList<Guid> TargetIds, int ArticleCount,
    decimal UnitPrice, decimal TotalCredits, decimal AvailableCredits, DateTimeOffset ExpiresAt,
    IReadOnlyList<string> Warnings, IReadOnlyList<string> QualityChecks, Guid? MaterialPreviewId,
    bool WithoutRag);

/// <summary>提交生成请求。</summary>
/// <param name="PreviewId">接口字段。</param>
/// <param name="RequestHash">接口字段。</param>
public sealed record ArticleGenerationSubmitRequest(Guid PreviewId, string RequestHash);

/// <summary>生成批次接受结果。</summary>
/// <param name="BatchId">接口字段。</param>
/// <param name="Status">接口字段。</param>
/// <param name="ArticleCount">接口字段。</param>
/// <param name="ReservedCredits">接口字段。</param>
/// <param name="PollPath">接口字段。</param>
public sealed record ArticleGenerationAccepted(Guid BatchId, ArticleGenerationBatchStatus Status,
    int ArticleCount, decimal ReservedCredits, string PollPath);

/// <summary>生成任务视图。</summary>
/// <param name="TaskId">接口字段。</param>
/// <param name="ArticleId">接口字段。</param>
/// <param name="PromptId">接口字段。</param>
/// <param name="Status">接口字段。</param>
/// <param name="AttemptCount">接口字段。</param>
/// <param name="ArticleVersionId">接口字段。</param>
/// <param name="ErrorCode">接口字段。</param>
/// <param name="ErrorMessage">接口字段。</param>
/// <param name="ReservationStatus">接口字段。</param>
public sealed record ArticleGenerationTaskView(Guid TaskId, Guid ArticleId, Guid? PromptId,
    ArticleGenerationTaskStatus Status, int AttemptCount, Guid? ArticleVersionId,
    string? ErrorCode, string? ErrorMessage, CreditReservationStatus ReservationStatus);

/// <summary>生成批次详情。</summary>
/// <param name="BatchId">接口字段。</param>
/// <param name="Status">接口字段。</param>
/// <param name="TotalTasks">接口字段。</param>
/// <param name="SucceededTasks">接口字段。</param>
/// <param name="FailedTasks">未成功结束的任务数：最终失败与用户取消的任务都计入（批次按成功数 + 此数是否达到总数判断结束）。</param>
/// <param name="ReservedCredits">接口字段。</param>
/// <param name="CapturedCredits">接口字段。</param>
/// <param name="Tasks">接口字段。</param>
/// <param name="CreatedAt">接口字段。</param>
/// <param name="CompletedAt">接口字段。</param>
public sealed record ArticleGenerationBatchView(Guid BatchId, ArticleGenerationBatchStatus Status,
    int TotalTasks, int SucceededTasks, int FailedTasks, decimal ReservedCredits,
    decimal CapturedCredits, IReadOnlyList<ArticleGenerationTaskView> Tasks,
    DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

/// <summary>生成任务工作租约。</summary>
/// <param name="TaskId">接口字段。</param>
/// <param name="BatchId">接口字段。</param>
/// <param name="ArticleId">接口字段。</param>
/// <param name="TenantId">接口字段。</param>
/// <param name="SiteId">接口字段。</param>
/// <param name="UserId">接口字段。</param>
/// <param name="PromptSnapshot">接口字段。</param>
/// <param name="TargetSnapshot">接口字段。</param>
/// <param name="KnowledgeSelectionSnapshot">接口字段。</param>
/// <param name="ContextSnapshot">接口字段。</param>
/// <param name="ReferenceSnapshot">接口字段。</param>
/// <param name="MaterialNotes">接口字段。</param>
/// <param name="Language">接口字段。</param>
/// <param name="Strategy">接口字段。</param>
/// <param name="LengthProfile">接口字段。</param>
/// <param name="Style">接口字段。</param>
/// <param name="Audience">接口字段。</param>
/// <param name="Keywords">接口字段。</param>
/// <param name="ExtraRequirements">接口字段。</param>
/// <param name="ContextId">接口字段。</param>
/// <param name="ContextHash">接口字段。</param>
/// <param name="WithoutRag">接口字段。</param>
/// <param name="AttemptCount">接口字段。</param>
/// <param name="LeaseOwner">接口字段。</param>
/// <param name="ReservationAmount">接口字段。</param>
/// <param name="Pipeline">生成管线：legacy 旧向导单次调用；studio_v1 文章创作六阶段（迁移 040 之前恒为 legacy）。</param>
/// <param name="IncludeCompetitors">文章创作是否纳入竞品视角。</param>
/// <param name="OptimizationGoal">文章创作优化目标；旧向导为空。</param>
/// <param name="PromptId">任务所属监测问题标识。</param>
public sealed record ArticleGenerationWorkItem(Guid TaskId, Guid BatchId, Guid ArticleId,
    Guid TenantId, Guid SiteId, Guid UserId, string PromptSnapshot, string TargetSnapshot,
    string? KnowledgeSelectionSnapshot, string? ContextSnapshot, string? ReferenceSnapshot,
    string? MaterialNotes, string Language, string Strategy, string LengthProfile, string Style,
    string? Audience, IReadOnlyList<string> Keywords, string? ExtraRequirements, Guid? ContextId,
    string? ContextHash, bool WithoutRag, int AttemptCount, string LeaseOwner, decimal ReservationAmount,
    string Pipeline = ArticleStudioLimits.LegacyPipeline, bool IncludeCompetitors = false,
    ArticleOptimizationGoal? OptimizationGoal = null, Guid? PromptId = null);

/// <summary>生成 Provider 的结构化结果。</summary>
/// <param name="Title">接口字段。</param>
/// <param name="Summary">接口字段。</param>
/// <param name="BodyMarkdown">接口字段。</param>
/// <param name="SeoTitle">接口字段。</param>
/// <param name="SeoDescription">接口字段。</param>
/// <param name="Keywords">接口字段。</param>
/// <param name="AnsweredQuestion">接口字段。</param>
/// <param name="EvidenceIds">接口字段。</param>
/// <param name="Uncertainties">接口字段。</param>
/// <param name="Disclosure">接口字段。</param>
/// <param name="Provider">接口字段。</param>
/// <param name="Model">接口字段。</param>
/// <param name="StrategyVersion">接口字段。</param>
/// <param name="ResponseHash">接口字段。</param>
/// <param name="PromptCharacterCount">接口字段。</param>
/// <param name="ResponseCharacterCount">接口字段。</param>
public sealed record ArticleGenerationResult(string Title, string? Summary, string BodyMarkdown,
    string? SeoTitle, string? SeoDescription, IReadOnlyList<string> Keywords,
    bool AnsweredQuestion, IReadOnlyList<string> EvidenceIds, IReadOnlyList<string> Uncertainties,
    string Disclosure, string Provider, string Model, string StrategyVersion,
    string ResponseHash, int PromptCharacterCount, int ResponseCharacterCount);

/// <summary>租户积分账户视图。</summary>
/// <param name="AvailableCredits">接口字段。</param>
/// <param name="ReservedCredits">接口字段。</param>
/// <param name="LifetimeGranted">接口字段。</param>
/// <param name="LifetimeCaptured">接口字段。</param>
/// <param name="Version">接口字段。</param>
/// <param name="UpdatedAt">接口字段。</param>
public sealed record CreditAccountView(decimal AvailableCredits, decimal ReservedCredits,
    decimal LifetimeGranted, decimal LifetimeCaptured, long Version, DateTimeOffset UpdatedAt);

/// <summary>积分账本流水视图。</summary>
/// <param name="EntryId">接口字段。</param>
/// <param name="EntryType">接口字段。</param>
/// <param name="Amount">接口字段。</param>
/// <param name="BalanceAfter">接口字段。</param>
/// <param name="ReservationId">接口字段。</param>
/// <param name="ArticleVersionId">接口字段。</param>
/// <param name="ActorUserId">接口字段。</param>
/// <param name="ExternalReference">接口字段。</param>
/// <param name="Reason">接口字段。</param>
/// <param name="CreatedAt">接口字段。</param>
public sealed record CreditLedgerEntryView(Guid EntryId, CreditLedgerEntryType EntryType,
    decimal Amount, decimal BalanceAfter, Guid? ReservationId, Guid? ArticleVersionId,
    Guid? ActorUserId, string ExternalReference, string? Reason, DateTimeOffset CreatedAt);

/// <summary>分页账本结果。</summary>
/// <param name="Items">接口字段。</param>
/// <param name="NextCursor">接口字段。</param>
public sealed record CreditLedgerPage(IReadOnlyList<CreditLedgerEntryView> Items, string? NextCursor);

/// <summary>文章监控汇总。</summary>
/// <param name="SiteId">接口字段。</param>
/// <param name="ArticleId">接口字段。</param>
/// <param name="From">接口字段。</param>
/// <param name="To">接口字段。</param>
/// <param name="Quality">接口字段。</param>
/// <param name="SnapshotToken">接口字段。</param>
/// <param name="SnapshotExpiresAt">接口字段。</param>
/// <param name="EligibleChecks">接口字段。</param>
/// <param name="CitationEvents">接口字段。</param>
/// <param name="ArticleCitationCount">接口字段。</param>
/// <param name="RegisteredArticleCount">接口字段。</param>
/// <param name="RegisteredUrlCount">接口字段。</param>
/// <param name="Trend">接口字段。</param>
/// <param name="SourceGroups">接口字段。</param>
/// <param name="DomainRankings">接口字段。</param>
/// <param name="CitedArticleCount">窗口内至少被引用一次的去重文章数；没有合格检测时为空。</param>
public sealed record ArticleMonitoringSummaryView(Guid SiteId, Guid? ArticleId,
    DateTimeOffset From, DateTimeOffset To, string Quality, string? SnapshotToken,
    DateTimeOffset? SnapshotExpiresAt, long? EligibleChecks, long? CitationEvents,
    long? ArticleCitationCount, long RegisteredArticleCount, long RegisteredUrlCount,
    IReadOnlyList<ArticleTrendPoint> Trend, IReadOnlyDictionary<string, long> SourceGroups,
    IReadOnlyList<ArticleDomainRankingView> DomainRankings, long? CitedArticleCount = null);

/// <summary>文章监控域名排行。</summary>
/// <param name="Domain">接口字段。</param>
/// <param name="CitationEvents">接口字段。</param>
/// <param name="RegisteredArticleCount">接口字段。</param>
/// <param name="RegisteredUrlCount">接口字段。</param>
public sealed record ArticleDomainRankingView(string Domain, long CitationEvents,
    long RegisteredArticleCount, long RegisteredUrlCount);

/// <summary>全品牌引用域名排行分页：分析快照中的域名榜单附带本系统已登记的发文标记。</summary>
/// <param name="Meta">同一分析快照与规则版本；snapshotToken 用于翻页、查看全部和证据下钻。</param>
/// <param name="Items">本页域名行，按引用次数降序、域名升序稳定排序。</param>
/// <param name="NextCursor">绑定快照与搜索条件的下一页游标；没有下一页时为空。</param>
/// <param name="TotalDomainCount">分页前（按搜索条件筛选后）的域名总数，即卡片上的「共 N 条」。</param>
public sealed record ArticleDomainRankingPage(CitationDomainMeta Meta, IReadOnlyList<ArticleDomainRankingRow> Items,
    string? NextCursor, long TotalDomainCount);

/// <summary>引用域名排行行；引用口径与品牌引用域名汇总一致，登记计数只说明本系统登记情况。</summary>
/// <param name="Domain">规范化 ASCII 主机域名（去除一个前导 www.）。</param>
/// <param name="WebsiteCategory">冻结规则得出的网站类型。</param>
/// <param name="Ownership">相对当前品牌的归属。</param>
/// <param name="CitationEvents">该域名去重后的引用事件数（按回答与规范化 URL 去重）。</param>
/// <param name="EventShare">该域名引用事件占全部引用事件的比例。</param>
/// <param name="AnswerCount">至少引用该域名一次的不同合格回答数。</param>
/// <param name="RegisteredArticleCount">当前品牌在该域名下登记了有效发布链接的文章数；0 只表示本系统未登记，不代表从未发文。</param>
/// <param name="RegisteredLinkCount">当前品牌在该域名下登记的有效（未停用）发布链接数。</param>
public sealed record ArticleDomainRankingRow(string Domain, CitationWebsiteCategory WebsiteCategory,
    CitationOwnership Ownership, long CitationEvents, CitationDomainRatio EventShare, long AnswerCount,
    long RegisteredArticleCount, long RegisteredLinkCount);

/// <summary>内容详情「推荐渠道」：全品牌非自有、字典类型为媒体平台的引用域名按平台合并后的高引用 Top N。</summary>
/// <param name="Meta">本次冻结的引用域名快照；filters.from/to 即统计窗口。</param>
/// <param name="Items">按引用次数降序、名称升序排列的推荐渠道，名次从 1 连续编号。</param>
public sealed record ArticleRecommendedChannelList(CitationDomainMeta Meta, IReadOnlyList<ArticleRecommendedChannel> Items);

/// <summary>推荐渠道行；同一平台标识（或同一条字典规则）下的域名合并为一行。</summary>
/// <param name="Rank">名次，从 1 开始连续。</param>
/// <param name="PlatformKey">字典平台标识（如 douyin、zhihu）；规则未设置平台标识时为空，按规则域名合并。</param>
/// <param name="Name">显示名称：字典中的平台中文名；未设置时为命中的规则域名。</param>
/// <param name="IconDomain">获取站点图标使用的域名（引用最多的规则域名），配合 GET /api/v1/site-icons/{domain}。</param>
/// <param name="Url">前往链接：字典中的发布入口；未设置时为 https://{规则域名}。</param>
/// <param name="CitationEvents">窗口内合并后的引用事件数（各域名事件按回答与 URL 去重，相加不重复计数）。</param>
/// <param name="Domains">合并进本行的规范化域名，按引用事件降序。</param>
public sealed record ArticleRecommendedChannel(int Rank, string? PlatformKey, string Name, string IconDomain,
    string Url, long CitationEvents, IReadOnlyList<string> Domains);

/// <summary>文章监控证据行。</summary>
/// <param name="ArticleId">接口字段。</param>
/// <param name="PublicationLinkId">接口字段。</param>
/// <param name="CheckId">接口字段。</param>
/// <param name="PromptId">接口字段。</param>
/// <param name="PromptSnapshot">接口字段。</param>
/// <param name="Provider">接口字段。</param>
/// <param name="Engine">接口字段。</param>
/// <param name="Locale">接口字段。</param>
/// <param name="Country">接口字段。</param>
/// <param name="CapturedAt">接口字段。</param>
/// <param name="Url">接口字段。</param>
/// <param name="Position">接口字段。</param>
/// <param name="Title">接口字段。</param>
/// <param name="Snippet">接口字段。</param>
/// <param name="CheckPath">接口字段。</param>
/// <param name="CaptureMode">引用该链接的回答保存的采集模式（api、scraped 或 app）。文章监控不按来源筛选，App 回答与其他回答一样计入。</param>
public sealed record ArticleMonitoringEvidenceView(Guid ArticleId, Guid PublicationLinkId,
    Guid CheckId, Guid PromptId, string PromptSnapshot, string Provider, string Engine,
    string? Locale, string? Country, DateTimeOffset? CapturedAt, string Url, int Position,
    string? Title, string? Snippet, string CheckPath, string? CaptureMode = null)
{
    /// <summary>该回答的采集渠道，由 Provider 推导（全部接口同一规则）：app_collector 为 app，domestic_collector 为 web，其余为 api。</summary>
    public CollectionChannel Channel => MonitoringProviders.Channel(Provider);
}

/// <summary>把一个引用 URL 标记为本品牌「自有」的请求：在内容管理中登记为有效发布链接（不新增表、不抓取网页、不消耗积分）。</summary>
/// <param name="Url">要标记的公开 HTTP(S) URL，通常取自引用文章榜行的 url；服务端按发布链接规则规范化（去片段、跟踪参数与一个前导 www.）后计算 URL 摘要，与引用文章榜 urlHash 一致。非法地址返回 400 INVALID_SOURCE_URL。</param>
/// <param name="Title">可选标题，通常取自引用文章榜行的 title；仅在新建导入文章时使用，去首尾空白后最多五百字符（超出截断），为空时使用规范化 URL。</param>
public sealed record OwnedUrlMarkRequest(string Url, string? Title = null);

/// <summary>自有标记或移除的处理结果。</summary>
public enum OwnedUrlMarkOutcome
{
    /// <summary>新建：该 URL 此前未登记，已新建一篇导入文章（published_import，追踪开启）及一条有效发布链接。</summary>
    [JsonStringEnumMemberName("created")] Created,
    /// <summary>恢复：该 URL 只有已停用的发布链接，已恢复为有效；所属文章已归档时一并恢复。</summary>
    [JsonStringEnumMemberName("reactivated")] Reactivated,
    /// <summary>未变化：标记时已有有效发布链接，或移除时没有有效发布链接（幂等成功）。</summary>
    [JsonStringEnumMemberName("unchanged")] Unchanged,
    /// <summary>已停用：移除时停用了该 URL 在本品牌下的全部有效发布链接；导入文章因此不再有有效链接时同时归档。</summary>
    [JsonStringEnumMemberName("disabled")] Disabled
}

/// <summary>自有标记或移除后的 URL 状态。</summary>
/// <param name="UrlHash">规范化 URL 的 SHA-256 十六进制小写摘要，与引用文章榜 urlHash 相同。</param>
/// <param name="Url">规范化后的 URL；移除一个从未登记过的摘要时为空。</param>
/// <param name="IsMine">操作后该 URL 是否命中本品牌有效发布链接（即引用文章榜的 isMine）；标记后为 true，移除后为 false。</param>
/// <param name="ArticleId">承载该发布链接的文章标识；移除一个没有有效链接的摘要时为空。</param>
/// <param name="Outcome">处理结果：created、reactivated、unchanged 或 disabled。</param>
public sealed record OwnedUrlMarkResult(string UrlHash, string? Url, bool IsMine, Guid? ArticleId, OwnedUrlMarkOutcome Outcome);
