using System.Text.Json.Serialization;

namespace Jogeoai.Domain;

/// <summary>产品信息状态。</summary>
public enum KnowledgeProductStatus
{
    /// <summary>草稿：尚未发起识别或已被驳回。</summary>
    [JsonStringEnumMemberName("draft")] Draft,
    /// <summary>识别中：存在进行中的识别任务。</summary>
    [JsonStringEnumMemberName("parsing")] Parsing,
    /// <summary>待核验：识别或手工填写完成，等待逐字段核对。</summary>
    [JsonStringEnumMemberName("pending_review")] PendingReview,
    /// <summary>已入库：核验通过并写入品牌知识库。</summary>
    [JsonStringEnumMemberName("approved")] Approved,
    /// <summary>识别失败：最近一次识别任务终止失败，可回到草稿重试。</summary>
    [JsonStringEnumMemberName("parse_failed")] ParseFailed,
    /// <summary>已归档：不再出现在文章生成「指定产品」中。</summary>
    [JsonStringEnumMemberName("archived")] Archived
}

/// <summary>识别任务类型。</summary>
public enum ProductParseJobKind
{
    /// <summary>从素材库文件识别。</summary>
    [JsonStringEnumMemberName("file")] File,
    /// <summary>从产品详情页网址识别。</summary>
    [JsonStringEnumMemberName("url")] Url,
    /// <summary>以产品名、关联品牌和已填内容结合品牌知识库补全。</summary>
    [JsonStringEnumMemberName("completion")] Completion
}

/// <summary>规格参数项。</summary>
/// <param name="Name">参数名，不超过 30 字。</param>
/// <param name="Value">参数值，不超过 200 字；数值与单位原样来自资料。</param>
public sealed record ProductSpecItem(string Name, string Value);

/// <summary>字段来源片段。</summary>
/// <param name="Quote">资料原文引用。</param>
/// <param name="Location">定位：文本为段落或页码，图片为 image:&lt;序号&gt; 与区域描述。</param>
public sealed record ProductFieldEvidence(string Quote, string? Location);

/// <summary>字段来源与核验状态。</summary>
/// <param name="Origin">manual（人工填写）或 ai（识别 / 补全）。</param>
/// <param name="Found">资料中是否找到该字段；false 时字段值为空并显示「未找到」。</param>
/// <param name="Verified">是否已人工核对；origin=ai 且 found 的字段必须核对后才能入库。</param>
/// <param name="Truncated">模型输出超出字段限制被截断，需人工精简。</param>
/// <param name="Evidence">来源片段列表。</param>
public sealed record ProductFieldSource(string Origin, bool Found, bool Verified, bool Truncated,
    IReadOnlyList<ProductFieldEvidence> Evidence);

/// <summary>识别任务视图。</summary>
/// <param name="JobId">任务标识。</param>
/// <param name="ProductId">目标产品标识。</param>
/// <param name="Kind">任务类型。</param>
/// <param name="Status">pending、running、done、failed 或 cancelled。</param>
/// <param name="Step">当前阶段：read_source、model 或 finalize。</param>
/// <param name="AttemptCount">已尝试次数。</param>
/// <param name="ErrorCode">失败原因码。</param>
/// <param name="ErrorMessage">面向用户的失败说明。</param>
/// <param name="ModelName">实际调用的模型名。</param>
/// <param name="ImageCount">送入模型的图片张数。</param>
/// <param name="Truncated">输入或输出是否被截断。</param>
/// <param name="DurationMs">模型调用耗时（毫秒）。</param>
/// <param name="CreatedAt">创建时间。</param>
/// <param name="StartedAt">首次开始时间。</param>
/// <param name="CompletedAt">完成或失败时间。</param>
public sealed record ProductParseJobView(Guid JobId, Guid ProductId, ProductParseJobKind Kind, string Status, string Step,
    int AttemptCount, string? ErrorCode, string? ErrorMessage, string? ModelName, int ImageCount, bool Truncated,
    long? DurationMs, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt);

/// <summary>产品信息视图。</summary>
/// <param name="ProductId">产品标识。</param>
/// <param name="TenantId">租户标识。</param>
/// <param name="SiteId">关联品牌标识。</param>
/// <param name="Name">产品名。</param>
/// <param name="Status">产品状态。</param>
/// <param name="SourceKind">最近一次来源：manual、file、url 或 completion。</param>
/// <param name="Introduction">产品介绍。</param>
/// <param name="Advantages">优势。</param>
/// <param name="Scenarios">适用场景与人群。</param>
/// <param name="Cases">客户案例。</param>
/// <param name="Specs">规格参数列表。</param>
/// <param name="ProductUrl">产品页面网址。</param>
/// <param name="Tags">标签。</param>
/// <param name="FieldSources">逐字段来源与核验状态，键为字段名。</param>
/// <param name="SourceDocumentId">最近一次识别的来源资料标识。</param>
/// <param name="SourceVersionId">最近一次识别的来源版本标识。</param>
/// <param name="KnowledgeDocumentId">入库后的产品资料文档；已入库但为空表示知识库同步失败，可重试。</param>
/// <param name="LatestJob">最近一次识别任务。</param>
/// <param name="CreatedBy">创建人。</param>
/// <param name="ApprovedBy">核验人。</param>
/// <param name="ApprovedAt">入库时间。</param>
/// <param name="CreatedAt">创建时间。</param>
/// <param name="UpdatedAt">最后更新时间。</param>
public sealed record KnowledgeProductView(Guid ProductId, Guid TenantId, Guid SiteId, string Name, KnowledgeProductStatus Status,
    string SourceKind, string? Introduction, string? Advantages, string? Scenarios, string? Cases,
    IReadOnlyList<ProductSpecItem> Specs, string? ProductUrl, IReadOnlyList<string> Tags,
    IReadOnlyDictionary<string, ProductFieldSource> FieldSources, Guid? SourceDocumentId, Guid? SourceVersionId,
    Guid? KnowledgeDocumentId, ProductParseJobView? LatestJob, Guid CreatedBy, Guid? ApprovedBy,
    DateTimeOffset? ApprovedAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>产品信息分页。</summary>
/// <param name="Items">按最后更新时间倒序的产品。</param>
/// <param name="NextCursor">下一页游标；为空表示没有更多。</param>
/// <param name="TotalCount">当前筛选下的总数。</param>
/// <param name="PendingReviewCount">同筛选（不含状态）下待核验的数量，用于页签角标。</param>
public sealed record KnowledgeProductListPage(IReadOnlyList<KnowledgeProductView> Items, string? NextCursor, long TotalCount,
    long PendingReviewCount);

/// <summary>产品信息写入字段。</summary>
/// <param name="Name">产品名，1–25 字，同品牌内不重复。</param>
/// <param name="Introduction">产品介绍，入库时必填，不超过 1000 字。</param>
/// <param name="Advantages">优势，不超过 1000 字。</param>
/// <param name="Scenarios">适用场景与人群，不超过 1000 字。</param>
/// <param name="Cases">客户案例，不超过 1000 字。</param>
/// <param name="Specs">规格参数，最多 50 项。</param>
/// <param name="ProductUrl">产品页面网址，公开 http(s)。</param>
/// <param name="Tags">标签，最多 10 个。</param>
public sealed record KnowledgeProductWrite(string Name, string? Introduction = null, string? Advantages = null,
    string? Scenarios = null, string? Cases = null, IReadOnlyList<ProductSpecItem>? Specs = null,
    string? ProductUrl = null, IReadOnlyList<string>? Tags = null);

/// <summary>产品列表筛选。</summary>
/// <param name="Status">按状态筛选。</param>
/// <param name="SourceKind">按来源筛选：manual、file、url 或 completion。</param>
/// <param name="Q">按产品名搜索。</param>
public sealed record KnowledgeProductListFilter(KnowledgeProductStatus? Status = null, string? SourceKind = null, string? Q = null);

/// <summary>发起识别的请求。</summary>
/// <param name="Kind">任务类型。</param>
/// <param name="DocumentId">file 任务的素材库资料标识；url 任务由服务端在读取网页后填入链接资料。</param>
/// <param name="Url">url 任务的产品详情页网址。</param>
public sealed record ProductParseRequest(ProductParseJobKind Kind, Guid? DocumentId = null, string? Url = null);

/// <summary>核验入库请求。</summary>
/// <param name="Fields">核验后的完整字段。</param>
/// <param name="VerifiedFields">已逐个核对的字段名；所有 origin=ai 且 found 的字段都必须出现。</param>
public sealed record ProductApproveRequest(KnowledgeProductWrite Fields, IReadOnlyList<string> VerifiedFields);

/// <summary>添加链接资料的请求。</summary>
/// <param name="Url">公开 http(s) 网址。</param>
/// <param name="Title">可选备注名称；为空时使用网页标题。</param>
/// <param name="FolderId">目标文件夹。</param>
public sealed record KnowledgeLinkWrite(string Url, string? Title = null, Guid? FolderId = null);

/// <summary>文章生成「指定产品」选项。</summary>
/// <param name="ProductId">产品标识，同时作为知识库资料的 productKey。</param>
/// <param name="Name">产品名。</param>
public sealed record KnowledgeProductOption(Guid ProductId, string Name);

/// <summary>Worker 领取到的识别任务租约。</summary>
/// <param name="JobId">任务标识。</param>
/// <param name="TenantId">租户标识。</param>
/// <param name="SiteId">品牌标识。</param>
/// <param name="ProductId">目标产品标识。</param>
/// <param name="Kind">任务类型。</param>
/// <param name="SourceDocumentId">file / url 任务读取的资料标识。</param>
/// <param name="SourceVersionId">file / url 任务读取的版本标识。</param>
/// <param name="SourceObjectKey">来源版本的对象存储键。</param>
/// <param name="SourceExtension">来源版本的扩展名。</param>
/// <param name="RequestedBy">发起人。</param>
/// <param name="AttemptCount">当前尝试次数。</param>
/// <param name="LeaseOwner">当前租约持有者。</param>
public sealed record ProductParseJobLease(Guid JobId, Guid TenantId, Guid SiteId, Guid ProductId, ProductParseJobKind Kind,
    Guid? SourceDocumentId, Guid? SourceVersionId, string? SourceObjectKey, string? SourceExtension, Guid RequestedBy,
    int AttemptCount, string LeaseOwner);

/// <summary>识别输入（冻结）。</summary>
/// <param name="Kind">任务类型。</param>
/// <param name="ProductName">产品名。</param>
/// <param name="BrandName">关联品牌名。</param>
/// <param name="Current">已填字段。</param>
/// <param name="SourceText">文本资料；图片识别或 AI 补全时为空。</param>
/// <param name="Images">按序号排列的图片。</param>
/// <param name="BrandEvidence">AI 补全时取自品牌知识库的证据。</param>
/// <param name="Truncated">来源是否被截断（文本超长或图片被丢弃）。</param>
public sealed record ProductParseInput(ProductParseJobKind Kind, string ProductName, string BrandName, KnowledgeProductWrite Current,
    string? SourceText, IReadOnlyList<ProductParseImage> Images, IReadOnlyList<KnowledgeEvidenceView> BrandEvidence, bool Truncated);

/// <summary>识别输入中的一张图片。</summary>
/// <param name="Ordinal">从 1 开始的序号，对应来源片段中的 image:&lt;序号&gt;。</param>
/// <param name="MediaType">image/png 或 image/jpeg。</param>
/// <param name="Bytes">图片字节。</param>
/// <param name="Label">可选标签，如页码。</param>
public sealed record ProductParseImage(int Ordinal, string MediaType, ReadOnlyMemory<byte> Bytes, string? Label);

/// <summary>识别输出。</summary>
/// <param name="Fields">模型返回并经服务端截断的字段；未找到的字段为空。</param>
/// <param name="FieldSources">逐字段来源，origin 固定为 ai。</param>
/// <param name="ModelName">实际调用的模型名。</param>
/// <param name="Provider">Provider 标识。</param>
/// <param name="DurationMs">模型调用耗时（毫秒）。</param>
/// <param name="Truncated">是否有字段被截断。</param>
/// <param name="RawHash">模型原始响应的 SHA-256，用于追溯；不保存原文。</param>
public sealed record ProductParseResult(KnowledgeProductWrite Fields, IReadOnlyDictionary<string, ProductFieldSource> FieldSources,
    string ModelName, string Provider, long DurationMs, bool Truncated, string RawHash);

/// <summary>识别链路的稳定错误；Worker 据此决定是否重试。</summary>
public sealed class ProductParseException(string code, string message, bool retryable, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>稳定错误码，如 PRODUCT_PARSE_IMAGE_UNSUPPORTED。</summary>
    public string Code { get; } = code;
    /// <summary>是否允许 Worker 重试。</summary>
    public bool Retryable { get; } = retryable;
}
