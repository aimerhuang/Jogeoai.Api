using System.Text.Json.Serialization;

namespace Jogeoai.Domain;

/// <summary>智能微调定价：独立于文章生成，单次固定积分。</summary>
public static class ArticleRefinePricing
{
    /// <summary>每次智能微调预占并在成功后结算的积分；失败释放。</summary>
    public const decimal UnitPrice = 40m;
}

/// <summary>文章编辑器（自动保存、智能微调、图片、导出）的长度与大小上限。</summary>
public static class ArticleEditorLimits
{
    /// <summary>标题最大字符数（与 PATCH 编辑一致）。</summary>
    public const int TitleMaxLength = 200;
    /// <summary>正文最大字符数（与 PATCH 编辑一致）。</summary>
    public const int BodyMaxLength = 100_000;
    /// <summary>智能微调指令最大字符数。</summary>
    public const int InstructionMaxLength = 500;
    /// <summary>整体微调时正文的最大字符数（受模型单次输出上限约束）。</summary>
    public const int WholeRefineMaxLength = 20_000;
    /// <summary>框选微调时选中片段的最大字符数。</summary>
    public const int SelectionMaxLength = 10_000;
    /// <summary>智能微调模型调用的最长等待秒数。</summary>
    public const int RefineTimeoutSeconds = 120;
    /// <summary>单张编辑器图片的最大字节数（5 MB）。</summary>
    public const int ImageMaxBytes = 5 * 1024 * 1024;
    /// <summary>平台图片读取地址前缀；正文中只允许该前缀加图片标识或 https 图片链接。</summary>
    public const string ImageUrlPrefix = "/api/v1/content/images/";
}

/// <summary>编辑器相关的稳定错误码。</summary>
public static class ArticleEditorErrorCodes
{
    /// <summary>基础版本或修订号与当前不一致（409）。</summary>
    public const string VersionConflict = "ARTICLE_VERSION_CONFLICT";
    /// <summary>编辑内容包含危险标记（400）。</summary>
    public const string ContentUnsafe = "ARTICLE_CONTENT_UNSAFE";
    /// <summary>正文图片地址不是平台图片或 https 链接（400）。</summary>
    public const string ImageSourceInvalid = "ARTICLE_IMAGE_SOURCE_INVALID";
    /// <summary>框选片段在当前正文中不存在或不唯一（422）。</summary>
    public const string SelectionNotFound = "REFINE_SELECTION_NOT_FOUND";
    /// <summary>微调输入超过长度上限（422）。</summary>
    public const string RefineInputTooLong = "REFINE_INPUT_TOO_LONG";
    /// <summary>同一幂等键的微调仍在进行（409）。</summary>
    public const string RefineInProgress = "REFINE_IN_PROGRESS";
    /// <summary>模型输出未通过格式或安全校验（502）。</summary>
    public const string RefineOutputInvalid = "REFINE_OUTPUT_INVALID";
    /// <summary>模型调用超时（504）。</summary>
    public const string RefineTimeout = "REFINE_TIMEOUT";
    /// <summary>预占长时间未结算，被自动释放（502）。</summary>
    public const string RefineAbandoned = "REFINE_ABANDONED";
    /// <summary>图片格式不受支持或文件头与图片格式不符（415）。</summary>
    public const string ImageTypeUnsupported = "CONTENT_IMAGE_TYPE_UNSUPPORTED";
    /// <summary>图片超过 5 MB（413）。</summary>
    public const string ImageTooLarge = "CONTENT_IMAGE_TOO_LARGE";
}

/// <summary>智能微调范围。</summary>
public enum ArticleRefineScope
{
    /// <summary>整体文章：重写全文，可同时调整标题。</summary>
    [JsonStringEnumMemberName("whole")] Whole,
    /// <summary>框选段落：只重写选中的完整段落，服务端替换回正文。</summary>
    [JsonStringEnumMemberName("selection")] Selection
}

/// <summary>文章导出格式。</summary>
public enum ArticleExportFormat
{
    /// <summary>Markdown 文本（默认，保持旧行为）。</summary>
    [JsonStringEnumMemberName("markdown")] Markdown,
    /// <summary>Word 文档（.docx），嵌入平台图片，外链图片降级为链接文字。</summary>
    [JsonStringEnumMemberName("docx")] Docx
}

/// <summary>自动保存草稿请求：只保存标题与正文，不修改标签、SEO 与语言。</summary>
/// <param name="Title">文章标题，1–200 字符。</param>
/// <param name="BodyMarkdown">Markdown 正文，1–100000 字符；拒绝 script/iframe/object/embed/form、javascript:、data: 链接与 on* 事件属性，图片只允许平台图片或 https 链接。</param>
/// <param name="BaseVersionId">编辑所基于的版本标识（文章当前版本）；文章尚无版本时可为空。</param>
/// <param name="ExpectedRevision">编辑所基于版本的草稿修订号（editRevision）；与当前不一致返回 409。</param>
public sealed record ArticleDraftRequest(string Title, string BodyMarkdown, Guid? BaseVersionId, int ExpectedRevision);

/// <summary>自动保存结果。</summary>
/// <param name="ArticleId">文章标识。</param>
/// <param name="VersionId">保存后的当前版本标识；下一次保存作为 baseVersionId。</param>
/// <param name="VersionNo">版本号。</param>
/// <param name="VersionKind">版本来源，自动保存恒为 manual_edit。</param>
/// <param name="EditRevision">保存后的草稿修订号；下一次保存作为 expectedRevision。</param>
/// <param name="Title">保存后的标题。</param>
/// <param name="ContentLength">正文字符数。</param>
/// <param name="CreatedNewVersion">是否新建了人工编辑版本（false 表示原地覆盖了当前草稿版本）。</param>
/// <param name="SavedAt">保存时间（UTC）。</param>
public sealed record ArticleDraftResult(Guid ArticleId, Guid VersionId, int VersionNo, ArticleVersionKind VersionKind,
    int EditRevision, string Title, int ContentLength, bool CreatedNewVersion, DateTimeOffset SavedAt);

/// <summary>智能微调请求；需要 Idempotency-Key 请求头。</summary>
/// <param name="Scope">微调范围：whole 整体文章，selection 框选段落。</param>
/// <param name="SelectionMarkdown">框选段落的 Markdown（前端扩展到完整块）；selection 时必填，且必须在当前正文中恰好出现一次。</param>
/// <param name="Instruction">微调指令，1–500 字符。</param>
/// <param name="BaseVersionId">微调所基于的版本标识，必须是文章当前版本。</param>
/// <param name="ExpectedRevision">该版本的草稿修订号；与当前不一致返回 409。</param>
public sealed record ArticleRefineRequest(ArticleRefineScope Scope, string? SelectionMarkdown, string Instruction,
    Guid BaseVersionId, int ExpectedRevision);

/// <summary>智能微调结果：新的 refined 版本已成为当前版本。</summary>
/// <param name="OperationId">微调操作标识（计费记录）。</param>
/// <param name="ArticleId">文章标识。</param>
/// <param name="Scope">微调范围。</param>
/// <param name="VersionId">新版本标识。</param>
/// <param name="VersionNo">新版本号。</param>
/// <param name="VersionKind">版本来源，恒为 refined。</param>
/// <param name="EditRevision">新版本的草稿修订号（恒为 0）。</param>
/// <param name="Title">新版本标题。</param>
/// <param name="BodyMarkdown">新版本 Markdown 正文。</param>
/// <param name="ContentLength">正文字符数。</param>
/// <param name="ChargedCredits">本次结算积分（40）。</param>
/// <param name="Replayed">是否为同一幂等键的重放结果（重放不重复扣费）。</param>
/// <param name="CreatedAt">版本创建时间（UTC）。</param>
public sealed record ArticleRefineResult(Guid OperationId, Guid ArticleId, ArticleRefineScope Scope, Guid VersionId, int VersionNo,
    ArticleVersionKind VersionKind, int EditRevision, string Title, string BodyMarkdown, int ContentLength,
    decimal ChargedCredits, bool Replayed, DateTimeOffset CreatedAt);

/// <summary>编辑器图片上传结果。</summary>
/// <param name="ImageId">图片标识。</param>
/// <param name="Url">平台图片读取地址（/api/v1/content/images/{imageId}），可直接写入 Markdown 图片语法。</param>
/// <param name="ContentType">按文件头识别的图片类型：image/png、image/jpeg、image/webp 或 image/gif。</param>
/// <param name="ByteSize">文件字节数。</param>
/// <param name="Width">像素宽度；无法识别时为空。</param>
/// <param name="Height">像素高度；无法识别时为空。</param>
public sealed record ContentImageUploadResult(Guid ImageId, string Url, string ContentType, long ByteSize, int? Width, int? Height);
