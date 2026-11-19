using System.Text.Json.Serialization;

namespace Jogeoai.Domain;

/// <summary>品牌知识维度，固定 6 个，不支持自定义。</summary>
public enum BrandKnowledgeDimension
{
    /// <summary>品牌介绍：公司定位、成立背景、资质荣誉。</summary>
    [JsonStringEnumMemberName("brand_intro")] BrandIntro,
    /// <summary>核心卖点：用户最该记住的卖点，一条写一个。</summary>
    [JsonStringEnumMemberName("selling_point")] SellingPoint,
    /// <summary>目标人群：谁在用、什么场景用。</summary>
    [JsonStringEnumMemberName("audience")] Audience,
    /// <summary>差异化优势：与竞品相比的不同点。</summary>
    [JsonStringEnumMemberName("differentiator")] Differentiator,
    /// <summary>产品/服务：一个产品或服务一条，文章生成「指定产品」从此维度选择。</summary>
    [JsonStringEnumMemberName("product_service")] ProductService,
    /// <summary>常见问题：标题写问题，内容写回答。</summary>
    [JsonStringEnumMemberName("faq")] Faq
}

/// <summary>品牌知识条目视图。</summary>
/// <param name="ItemId">条目标识；由已入库产品迁移的条目沿用原产品标识。</param>
/// <param name="SiteId">所属品牌标识。</param>
/// <param name="Dimension">维度。</param>
/// <param name="Title">标题，1–100 字。</param>
/// <param name="Content">内容，1–2000 字。</param>
/// <param name="SourceKind">来源：manual（手动填写）、url（网址识别）或 migrated（由已入库产品迁移）。</param>
/// <param name="SourceUrl">来源网址；手动填写的条目为空。</param>
/// <param name="SourceExcerpt">网址识别时的原文依据片段。</param>
/// <param name="SyncStatus">知识库检索同步状态：pending（待同步）、synced（已同步）或 failed（同步失败：可重试的错误按退避自动重试，最多 5 次；次数用尽或不可重试时，编辑或删除条目后才会再次同步）。</param>
/// <param name="Version">内容版本，编辑时作为乐观锁提交。</param>
/// <param name="CreatedBy">创建人。</param>
/// <param name="UpdatedBy">最后修改人。</param>
/// <param name="CreatedAt">创建时间（UTC）。</param>
/// <param name="UpdatedAt">最后修改时间（UTC）。</param>
public sealed record BrandKnowledgeItemView(Guid ItemId, Guid SiteId, BrandKnowledgeDimension Dimension, string Title, string Content,
    string SourceKind, string? SourceUrl, string? SourceExcerpt, string SyncStatus, int Version,
    Guid CreatedBy, Guid UpdatedBy, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>品牌知识条目列表。</summary>
/// <param name="Items">按维度固定顺序、同维度按创建时间正序的条目；受维度筛选影响。</param>
/// <param name="Counts">各维度未删除条目数，恒含 6 个维度键，与筛选无关。</param>
/// <param name="Total">全部维度未删除条目总数。</param>
/// <param name="Limit">每品牌条目上限。</param>
public sealed record BrandKnowledgeItemList(IReadOnlyList<BrandKnowledgeItemView> Items,
    IReadOnlyDictionary<BrandKnowledgeDimension, int> Counts, int Total, int Limit);

/// <summary>新增品牌知识条目的请求。</summary>
/// <param name="Dimension">维度，必填。</param>
/// <param name="Title">标题，1–100 字，同品牌同维度不重复。</param>
/// <param name="Content">内容，1–2000 字。</param>
public sealed record BrandKnowledgeItemWrite(BrandKnowledgeDimension Dimension, string Title, string Content);

/// <summary>编辑品牌知识条目的请求。</summary>
/// <param name="Dimension">维度，可修改。</param>
/// <param name="Title">标题，1–100 字，同品牌同维度不重复。</param>
/// <param name="Content">内容，1–2000 字。</param>
/// <param name="ExpectedVersion">读取时的内容版本；与当前版本不一致时返回冲突，需刷新后重试。</param>
public sealed record BrandKnowledgeItemUpdate(BrandKnowledgeDimension Dimension, string Title, string Content, int ExpectedVersion);

/// <summary>发起网址识别的请求。</summary>
/// <param name="Url">公开 http(s) 网址，不超过 2000 字符。</param>
/// <param name="Dimensions">只识别这些维度；为空表示全部维度。</param>
public sealed record KnowledgeExtractRequest(string Url, IReadOnlyList<BrandKnowledgeDimension>? Dimensions = null);

/// <summary>网址识别的候选知识。</summary>
/// <param name="CandidateId">任务内候选序号，保存时用于对应候选。</param>
/// <param name="Dimension">维度。</param>
/// <param name="Title">标题，不超过 100 字。</param>
/// <param name="Content">内容，不超过 2000 字。</param>
/// <param name="Evidence">网页原文依据片段。</param>
/// <param name="DuplicateOfItemId">与之可能重复的已有条目；不为空时前端默认不勾选。</param>
public sealed record KnowledgeExtractCandidate(string CandidateId, BrandKnowledgeDimension Dimension, string Title, string Content,
    string Evidence, Guid? DuplicateOfItemId);

/// <summary>网址识别任务视图。</summary>
/// <param name="JobId">任务标识。</param>
/// <param name="Status">pending（待处理）、running（进行中）、done（候选已就绪）、failed（失败）、cancelled（已取消）、saved（已保存）或 discarded（已放弃或过期）。</param>
/// <param name="Step">当前阶段：fetch（读取网页）、model（模型识别）或 finalize（校验与查重）。</param>
/// <param name="Url">识别的网址。</param>
/// <param name="PageTitle">网页标题。</param>
/// <param name="Candidates">候选知识；任务完成前为空。</param>
/// <param name="Truncated">网页正文或候选内容是否因超出长度被截断。</param>
/// <param name="ErrorCode">失败原因码。</param>
/// <param name="ErrorMessage">面向用户的失败说明。</param>
/// <param name="CreatedAt">创建时间（UTC）。</param>
/// <param name="CompletedAt">完成或失败时间（UTC）。</param>
/// <param name="ExpiresAt">候选过期时间（UTC），完成后 24 小时。</param>
public sealed record KnowledgeExtractJobView(Guid JobId, string Status, string Step, string Url, string? PageTitle,
    IReadOnlyList<KnowledgeExtractCandidate> Candidates, bool Truncated, string? ErrorCode, string? ErrorMessage,
    DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, DateTimeOffset? ExpiresAt);

/// <summary>保存网址识别结果时的一条候选（可被用户编辑）。</summary>
/// <param name="CandidateId">候选序号，必须属于该任务。</param>
/// <param name="Dimension">维度。</param>
/// <param name="Title">标题，1–100 字。</param>
/// <param name="Content">内容，1–2000 字。</param>
public sealed record KnowledgeExtractSaveItem(string CandidateId, BrandKnowledgeDimension Dimension, string Title, string Content);

/// <summary>保存网址识别结果的请求。</summary>
/// <param name="Items">用户勾选并确认的候选。</param>
public sealed record KnowledgeExtractSaveRequest(IReadOnlyList<KnowledgeExtractSaveItem> Items);

/// <summary>模型输出的原始候选，尚未校验。</summary>
/// <param name="Dimension">维度键（如 brand_intro）或中文名。</param>
/// <param name="Title">标题。</param>
/// <param name="Content">内容。</param>
/// <param name="Evidence">模型声称的网页原文依据。</param>
public sealed record KnowledgeExtractRawCandidate(string? Dimension, string? Title, string? Content, string? Evidence);

/// <summary>候选标准化结果。</summary>
/// <param name="Candidates">保留的候选，最多 30 条，序号从 1 开始。</param>
/// <param name="DroppedCount">被丢弃的候选数（维度无效或不在限定范围、标题或内容为空、原文依据不在网页正文中、批内重复、超出条数上限）。</param>
/// <param name="Truncated">是否有候选的标题或内容因超长被截断。</param>
public sealed record KnowledgeExtractNormalizeResult(IReadOnlyList<KnowledgeExtractCandidate> Candidates, int DroppedCount, bool Truncated);

/// <summary>品牌知识维度元数据。</summary>
/// <param name="Dimension">维度。</param>
/// <param name="Key">接口与数据库使用的小写蛇形键。</param>
/// <param name="Label">中文名。</param>
/// <param name="Order">固定排序（从 1 开始），也是品牌研究读取条目的优先级。</param>
/// <param name="Hint">填写提示。</param>
public sealed record BrandKnowledgeDimensionInfo(BrandKnowledgeDimension Dimension, string Key, string Label, int Order, string Hint);

/// <summary>文章生成「指定产品」解析出的产品引用：来自「产品/服务」维度条目，或迁移前冻结的旧产品。</summary>
/// <param name="Id">产品键：条目标识（迁移条目与原产品标识相同）。</param>
/// <param name="TenantId">所属租户。</param>
/// <param name="SiteId">所属品牌。</param>
/// <param name="Name">产品名称（条目标题）。</param>
/// <param name="KnowledgeDocumentId">检索投影文档；尚未同步时为空。</param>
public sealed record ArticleProductRef(Guid Id, Guid TenantId, Guid SiteId, string Name, Guid? KnowledgeDocumentId);
