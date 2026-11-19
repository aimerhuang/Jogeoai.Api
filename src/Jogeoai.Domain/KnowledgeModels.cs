using System.Text.Json.Serialization;

namespace Jogeoai.Domain;

/// <summary>资料的存储范围。</summary>
public enum KnowledgeScopeMode
{
    /// <summary>同时查询公司级和当前品牌级资料。</summary>
    [JsonStringEnumMemberName("default")]
    Default,
    /// <summary>只查询当前品牌级资料。</summary>
    [JsonStringEnumMemberName("brand_only")]
    BrandOnly
}

/// <summary>资料版本的处理状态。</summary>
public enum KnowledgeVersionStatus
{
    /// <summary>已接收，等待摄取。</summary>
    Pending,
    /// <summary>正在解析正文。</summary>
    Parsing,
    /// <summary>正在建立检索索引。</summary>
    Indexing,
    /// <summary>已完整建立索引，可以检索。</summary>
    Ready,
    /// <summary>处理失败。</summary>
    Failed,
    /// <summary>资料已停用。</summary>
    Disabled
}

/// <summary>逻辑资料的生命周期状态。</summary>
public enum KnowledgeDocumentStatus
{
    /// <summary>等待首个版本处理。</summary>
    Pending,
    /// <summary>至少一个版本正在处理。</summary>
    Processing,
    /// <summary>已有当前可用版本。</summary>
    Ready,
    /// <summary>资料已停用。</summary>
    Disabled,
    /// <summary>资料处理失败。</summary>
    Failed
}

/// <summary>资料文件夹。</summary>
/// <param name="FolderId">文件夹标识。</param>
/// <param name="TenantId">租户标识。</param>
/// <param name="SiteId">品牌标识；公司级文件夹为空。</param>
/// <param name="ParentId">父文件夹标识。</param>
/// <param name="Name">文件夹名称。</param>
/// <param name="Status">active 或 archived。</param>
/// <param name="CreatedAt">创建时间。</param>
/// <param name="UpdatedAt">更新时间。</param>
public sealed record KnowledgeFolderView(Guid FolderId, Guid TenantId, Guid? SiteId, Guid? ParentId,
    string Name, string Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>创建资料文件夹的命令。</summary>
/// <param name="Name">文件夹名称。</param>
/// <param name="ParentId">父文件夹。</param>
public sealed record KnowledgeFolderWrite(string Name, Guid? ParentId = null);

/// <summary>修改资料文件夹的命令。</summary>
/// <param name="Name">新名称。</param>
/// <param name="ParentId">新父文件夹。</param>
/// <param name="Archive">是否归档。</param>
/// <param name="MoveToRoot">将文件夹移动到当前资料范围的根目录；不能同时指定父文件夹。</param>
public sealed record KnowledgeFolderUpdate(string? Name = null, Guid? ParentId = null,
    bool Archive = false, bool MoveToRoot = false);

/// <summary>资料版本视图。</summary>
/// <param name="VersionId">版本标识。</param>
/// <param name="VersionNumber">逻辑版本号。</param>
/// <param name="OriginalFileName">上传时的文件名。</param>
/// <param name="Extension">规范化扩展名。</param>
/// <param name="ContentType">检测后的 MIME 类型。</param>
/// <param name="ByteLength">文件字节数。</param>
/// <param name="Sha256">文件内容摘要。</param>
/// <param name="Status">处理状态。</param>
/// <param name="ErrorCode">安全错误码。</param>
/// <param name="ErrorMessage">面向用户的简短错误消息。</param>
/// <param name="CharacterCount">解析出的字符数。</param>
/// <param name="PageCount">适用时的页数。</param>
/// <param name="CreatedAt">上传时间。</param>
/// <param name="CompletedAt">处理完成时间。</param>
/// <param name="ContentKind">内容类型：text（文本，可检索）或 image（图片，允许 0 片段发布，供多模态识别）。</param>
public sealed record KnowledgeVersionView(Guid VersionId, int VersionNumber, string OriginalFileName,
    string Extension, string ContentType, long ByteLength, string Sha256, string Status,
    string? ErrorCode, string? ErrorMessage, int? CharacterCount, int? PageCount,
    DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, string ContentKind = "text");

/// <summary>知识库逻辑资料视图。</summary>
/// <param name="DocumentId">稳定的逻辑资料标识。</param>
/// <param name="TenantId">租户标识。</param>
/// <param name="SiteId">品牌标识；公司级资料为空。</param>
/// <param name="Scope">tenant 或 site。</param>
/// <param name="FolderId">文件夹标识。</param>
/// <param name="Title">资料标题。</param>
/// <param name="Status">资料状态。</param>
/// <param name="Language">资料语言。</param>
/// <param name="DocumentType">资料类型。</param>
/// <param name="ProductKey">产品标识。</param>
/// <param name="Tags">标签。</param>
/// <param name="EffectiveAt">资料生效时间。</param>
/// <param name="ExpiresAt">资料失效时间。</param>
/// <param name="CurrentVersionId">当前版本标识。</param>
/// <param name="CurrentVersion">当前版本详情。</param>
/// <param name="CreatedAt">创建时间。</param>
/// <param name="UpdatedAt">更新时间。</param>
/// <param name="ConfigurationVersion">资料元数据配置版本，用于并发更新保护。</param>
/// <param name="LatestVersion">最近上传的版本及其处理状态；替换期间可与当前可用版本不同。</param>
/// <param name="SourceKind">资料来源：upload（上传文件）、link（添加链接）或 product（已入库产品信息投影）。</param>
/// <param name="SourceUrl">link 来源的规范化网址。</param>
public sealed record KnowledgeDocumentView(Guid DocumentId, Guid TenantId, Guid? SiteId, string Scope,
    Guid? FolderId, string Title, string Status, string Language, string DocumentType, string? ProductKey,
    IReadOnlyList<string> Tags, DateTimeOffset? EffectiveAt, DateTimeOffset? ExpiresAt,
    Guid? CurrentVersionId, KnowledgeVersionView? CurrentVersion, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, int ConfigurationVersion = 1, KnowledgeVersionView? LatestVersion = null,
    string SourceKind = "upload", string? SourceUrl = null);

/// <summary>租户知识库容量使用情况。</summary>
/// <param name="UsedBytes">所有保留版本文件的字节数。</param>
/// <param name="QuotaBytes">租户容量上限。</param>
/// <param name="RetainedVersionCount">保留版本数量。</param>
/// <param name="AvailableBytes">剩余可用空间。</param>
public sealed record KnowledgeUsageView(long UsedBytes, long QuotaBytes, long RetainedVersionCount,
    long AvailableBytes);

/// <summary>上传资料的应用命令。</summary>
/// <param name="Content">上传文件流，调用方负责释放。</param>
/// <param name="OriginalFileName">原始文件名。</param>
/// <param name="ContentType">客户端提供的 MIME 类型。</param>
/// <param name="Title">资料标题；为空时从文件名推导。</param>
/// <param name="FolderId">目标文件夹。</param>
/// <param name="DocumentType">资料类型。</param>
/// <param name="ProductKey">产品标识。</param>
/// <param name="Tags">资料标签。</param>
/// <param name="Language">资料语言。</param>
/// <param name="EffectiveAt">生效时间。</param>
/// <param name="ExpiresAt">失效时间。</param>
/// <param name="IdempotencyKey">租户范围幂等键。</param>
/// <param name="DocumentId">替换已有逻辑资料时的标识；省略时创建新资料。</param>
public sealed record KnowledgeUploadCommand(Stream Content, string OriginalFileName, string? ContentType,
    string? Title = null, Guid? FolderId = null, string DocumentType = "general", string? ProductKey = null,
    IReadOnlyList<string>? Tags = null, string Language = "und", DateTimeOffset? EffectiveAt = null,
    DateTimeOffset? ExpiresAt = null, string? IdempotencyKey = null, Guid? DocumentId = null);

/// <summary>资料元数据更新命令。</summary>
/// <param name="Title">新标题。</param>
/// <param name="FolderId">新文件夹。</param>
/// <param name="DocumentType">新资料类型。</param>
/// <param name="ProductKey">新产品标识。</param>
/// <param name="Tags">完整标签集合。</param>
/// <param name="EffectiveAt">新的生效时间。</param>
/// <param name="ExpiresAt">新的失效时间。</param>
/// <param name="ExpectedVersion">期望的文档配置版本。</param>
public sealed record KnowledgeDocumentUpdate(string? Title = null, Guid? FolderId = null,
    string? DocumentType = null, string? ProductKey = null, IReadOnlyList<string>? Tags = null,
    DateTimeOffset? EffectiveAt = null, DateTimeOffset? ExpiresAt = null, int? ExpectedVersion = null);

/// <summary>资料列表筛选条件。</summary>
/// <param name="ScopeMode">品牌列表的资料范围；默认合并公司级资料。</param>
/// <param name="Status">逻辑资料状态。</param>
/// <param name="Tag">资料标签。</param>
/// <param name="DocumentType">资料类型。</param>
public sealed record KnowledgeDocumentListFilter(KnowledgeScopeMode ScopeMode = KnowledgeScopeMode.Default,
    string? Status = null, string? Tag = null, string? DocumentType = null);

/// <summary>检索请求。</summary>
/// <param name="Query">检索问题或关键词。</param>
/// <param name="ScopeMode">资料范围模式。</param>
/// <param name="Tags">按标签过滤。</param>
/// <param name="DocumentType">按资料类型过滤。</param>
/// <param name="ProductKey">按产品标识过滤。</param>
/// <param name="Language">按语言过滤。</param>
/// <param name="TopK">最多返回的片段数。</param>
/// <param name="Topic">可选文章主题。</param>
/// <param name="DocumentIds">可选的逻辑资料白名单。</param>
/// <param name="FolderIds">可选的资料文件夹白名单。</param>
public sealed record KnowledgeSearchRequest(string Query, KnowledgeScopeMode ScopeMode = KnowledgeScopeMode.Default,
    IReadOnlyList<string>? Tags = null, string? DocumentType = null, string? ProductKey = null,
    string? Language = null, int TopK = 8, string? Topic = null, IReadOnlyList<Guid>? DocumentIds = null,
    IReadOnlyList<Guid>? FolderIds = null);

/// <summary>写作上下文请求。</summary>
/// <param name="Topic">文章主题。</param>
/// <param name="Questions">需要回答的问题集合。</param>
/// <param name="ScopeMode">资料范围模式。</param>
/// <param name="Tags">按标签过滤。</param>
/// <param name="DocumentType">按资料类型过滤。</param>
/// <param name="ProductKey">按产品标识过滤。</param>
/// <param name="Language">按语言过滤。</param>
/// <param name="MaxChunks">上下文最多片段数。</param>
/// <param name="MaxCharacters">上下文最多字符数。</param>
/// <param name="DocumentIds">可选的逻辑资料白名单；越权资料按不可见处理。</param>
/// <param name="FolderIds">可选的资料文件夹白名单。</param>
public sealed record KnowledgeContextRequest(string? Topic = null, IReadOnlyList<string>? Questions = null,
    KnowledgeScopeMode ScopeMode = KnowledgeScopeMode.Default, IReadOnlyList<string>? Tags = null,
    string? DocumentType = null, string? ProductKey = null, string? Language = null,
    int MaxChunks = 24, int MaxCharacters = 24000, IReadOnlyList<Guid>? DocumentIds = null,
    IReadOnlyList<Guid>? FolderIds = null);

/// <summary>可追溯资料证据片段。</summary>
/// <param name="EvidenceId">稳定的证据标识。</param>
/// <param name="DocumentId">逻辑资料标识。</param>
/// <param name="VersionId">资料版本标识。</param>
/// <param name="ChunkId">片段标识。</param>
/// <param name="Title">资料标题。</param>
/// <param name="Text">片段原文。</param>
/// <param name="Scope">tenant 或 site。</param>
/// <param name="OriginalFileName">原始文件名。</param>
/// <param name="Location">原文定位信息。</param>
/// <param name="Score">检索分数。</param>
/// <param name="EffectiveAt">资料生效时间。</param>
/// <param name="ExpiresAt">资料失效时间。</param>
/// <param name="ProductKey">资料所属产品标识，用于提示同产品不同来源需核对。</param>
public sealed record KnowledgeEvidenceView(string EvidenceId, Guid DocumentId, Guid VersionId, Guid ChunkId,
    string Title, string Text, string Scope, string OriginalFileName, string? Location, double Score,
    DateTimeOffset? EffectiveAt, DateTimeOffset? ExpiresAt, string? ProductKey = null);

/// <summary>检索响应。</summary>
/// <param name="Items">有序证据片段。</param>
/// <param name="Gaps">没有资料依据的查询缺口。</param>
public sealed record KnowledgeSearchResponse(IReadOnlyList<KnowledgeEvidenceView> Items,
    IReadOnlyList<string> Gaps);

/// <summary>上下文中的资料冲突。</summary>
/// <param name="Key">冲突事实的归一化键。</param>
/// <param name="EvidenceIds">涉及的证据标识。</param>
/// <param name="Message">面向调用方的冲突说明。</param>
public sealed record KnowledgeConflict(string Key, IReadOnlyList<string> EvidenceIds, string Message);

/// <summary>写作上下文响应。</summary>
/// <param name="ContextId">上下文快照标识。</param>
/// <param name="Items">有序证据片段。</param>
/// <param name="Gaps">缺少依据的主题或问题。</param>
/// <param name="Conflicts">检测到的资料冲突。</param>
/// <param name="ScopeMode">实际使用的资料范围模式。</param>
/// <param name="CharacterCount">返回正文字符数。</param>
public sealed record KnowledgeContextResponse(Guid ContextId, IReadOnlyList<KnowledgeEvidenceView> Items,
    IReadOnlyList<string> Gaps, IReadOnlyList<KnowledgeConflict> Conflicts,
    KnowledgeScopeMode ScopeMode, int CharacterCount);

/// <summary>文档解析出的原文段落。</summary>
/// <param name="Text">段落正文。</param>
/// <param name="HeadingPath">标题路径。</param>
/// <param name="Location">页码、行号或段落定位。</param>
public sealed record ExtractedTextSegment(string Text, string? HeadingPath, string? Location);

/// <summary>文档中提取出的图片（图片文件本身或 PDF 内嵌页图）。</summary>
/// <param name="Ordinal">从 1 开始的序号。</param>
/// <param name="MediaType">image/png 或 image/jpeg。</param>
/// <param name="Bytes">图片字节。</param>
/// <param name="Location">定位，如 page:3。</param>
public sealed record ExtractedImage(int Ordinal, string MediaType, byte[] Bytes, string? Location);

/// <summary>文档解析结果。</summary>
/// <param name="Segments">按原文顺序排列的段落。</param>
/// <param name="CharacterCount">正文字符数。</param>
/// <param name="PageCount">适用时的页数。</param>
/// <param name="ContentKind">text 或 image；image 表示文本不足，以图片内容为主。</param>
/// <param name="Images">图片列表；仅在图片文件或文本不足的 PDF 中填充，最多 20 张。</param>
/// <param name="Truncated">图片因超过单张上限被丢弃或页数超过上限。</param>
public sealed record ExtractedDocument(IReadOnlyList<ExtractedTextSegment> Segments, int CharacterCount,
    int? PageCount = null, string ContentKind = "text", IReadOnlyList<ExtractedImage>? Images = null,
    bool Truncated = false);

/// <summary>待处理摄取任务租约。</summary>
/// <param name="JobId">任务标识。</param>
/// <param name="TenantId">租户标识。</param>
/// <param name="DocumentId">逻辑资料标识。</param>
/// <param name="VersionId">版本标识。</param>
/// <param name="ObjectKey">对象存储键。</param>
/// <param name="OriginalFileName">原始文件名。</param>
/// <param name="Extension">文件扩展名。</param>
/// <param name="AttemptCount">当前尝试次数。</param>
/// <param name="LeaseOwner">当前租约持有者。</param>
public sealed record KnowledgeIngestJobLease(Guid JobId, Guid TenantId, Guid DocumentId, Guid VersionId,
    string ObjectKey, string OriginalFileName, string Extension, int AttemptCount, string LeaseOwner);

/// <summary>分段器输出的稳定片段。</summary>
/// <param name="ChunkId">稳定片段标识。</param>
/// <param name="Ordinal">片段顺序。</param>
/// <param name="Text">片段正文。</param>
/// <param name="HeadingPath">标题路径。</param>
/// <param name="Location">原文定位。</param>
public sealed record KnowledgeChunk(Guid ChunkId, int Ordinal, string Text, string? HeadingPath, string? Location);
