using Jogeoai.Domain;

namespace Jogeoai.Application;

/// <summary>文章编辑器的持久化边界（迁移 041）：草稿自动保存与智能微调计费。</summary>
public interface IArticleEditorStore
{
    /// <summary>自动保存草稿：当前版本为未被发布链接引用的人工编辑版本且与基础版本一致时原地覆盖并修订号 +1，否则新建人工编辑版本；不修改标签。</summary>
    /// <param name="tenantId">租户标识。</param>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="articleId">文章标识。</param>
    /// <param name="userId">当前用户标识。</param>
    /// <param name="request">草稿请求（调用前已完成长度与安全校验）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<ArticleDraftResult> SaveDraftAsync(Guid tenantId, Guid siteId, Guid articleId, Guid userId,
        ArticleDraftRequest request, CancellationToken cancellationToken = default);

    /// <summary>按幂等键读取微调操作；不存在时返回空。超过租期仍为 held 的操作会先被释放。</summary>
    /// <param name="tenantId">租户标识。</param>
    /// <param name="idempotencyKey">幂等键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<ArticleRefineOperationRecord?> FindRefineAsync(Guid tenantId, string idempotencyKey,
        CancellationToken cancellationToken = default);

    /// <summary>校验基础版本、修订号与框选片段后预占 40 积分并登记 held 操作；返回微调输入。</summary>
    /// <param name="reservation">预占参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<ArticleRefineHold> ReserveRefineAsync(ArticleRefineReservation reservation, CancellationToken cancellationToken = default);

    /// <summary>写入 refined 版本、切换当前版本并结算；基础版本已变化时释放预占并抛出 409。</summary>
    /// <param name="tenantId">租户标识。</param>
    /// <param name="operationId">微调操作标识。</param>
    /// <param name="title">新标题。</param>
    /// <param name="bodyMarkdown">新正文（已通过输出校验）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<ArticleRefineResult> CompleteRefineAsync(Guid tenantId, Guid operationId, string title, string bodyMarkdown,
        CancellationToken cancellationToken = default);

    /// <summary>释放预占并记录失败原因；已结算或已释放时不做任何修改。</summary>
    /// <param name="tenantId">租户标识。</param>
    /// <param name="operationId">微调操作标识。</param>
    /// <param name="errorCode">失败错误码。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task ReleaseRefineAsync(Guid tenantId, Guid operationId, string errorCode, CancellationToken cancellationToken = default);

    /// <summary>读取已结算微调的结果（重放）。</summary>
    /// <param name="tenantId">租户标识。</param>
    /// <param name="operationId">微调操作标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<ArticleRefineResult?> GetRefineResultAsync(Guid tenantId, Guid operationId, CancellationToken cancellationToken = default);
}

/// <summary>微调操作记录。</summary>
/// <param name="OperationId">操作标识。</param>
/// <param name="SiteId">品牌工作区标识。</param>
/// <param name="ArticleId">文章标识。</param>
/// <param name="RequestHash">请求摘要（幂等判断）。</param>
/// <param name="Status">预占状态：held、captured、released。</param>
/// <param name="ErrorCode">释放原因。</param>
public sealed record ArticleRefineOperationRecord(Guid OperationId, Guid SiteId, Guid ArticleId, string RequestHash,
    CreditReservationStatus Status, string? ErrorCode);

/// <summary>微调预占参数。</summary>
/// <param name="TenantId">租户标识。</param>
/// <param name="SiteId">品牌工作区标识。</param>
/// <param name="ArticleId">文章标识。</param>
/// <param name="UserId">当前用户标识。</param>
/// <param name="IdempotencyKey">幂等键。</param>
/// <param name="RequestHash">请求摘要。</param>
/// <param name="Scope">微调范围。</param>
/// <param name="SelectionMarkdown">框选片段（已去除首尾空白）。</param>
/// <param name="BaseVersionId">基础版本。</param>
/// <param name="ExpectedRevision">基础版本修订号。</param>
/// <param name="Credits">预占积分。</param>
public sealed record ArticleRefineReservation(Guid TenantId, Guid SiteId, Guid ArticleId, Guid UserId, string IdempotencyKey,
    string RequestHash, ArticleRefineScope Scope, string? SelectionMarkdown, Guid BaseVersionId, int ExpectedRevision, decimal Credits);

/// <summary>已预占的微调输入。</summary>
/// <param name="OperationId">操作标识。</param>
/// <param name="Title">基础版本标题。</param>
/// <param name="BodyMarkdown">基础版本正文。</param>
/// <param name="Language">基础版本语言。</param>
/// <param name="SelectionStart">框选片段在正文中的起始位置；整体微调为 -1。</param>
public sealed record ArticleRefineHold(Guid OperationId, string Title, string BodyMarkdown, string Language, int SelectionStart);

/// <summary>智能微调业务编排：预占 → 同步调用模型 → 结算或释放。</summary>
public interface IArticleRefineService
{
    /// <summary>执行一次智能微调；同一幂等键重放返回同一结果且不重复扣费。</summary>
    /// <param name="tenantId">租户标识。</param>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="articleId">文章标识。</param>
    /// <param name="userId">当前用户标识。</param>
    /// <param name="request">微调请求。</param>
    /// <param name="idempotencyKey">幂等键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<ArticleRefineResult> RefineAsync(Guid tenantId, Guid siteId, Guid articleId, Guid userId, ArticleRefineRequest request,
        string idempotencyKey, CancellationToken cancellationToken = default);
}

/// <summary>智能微调的模型调用（deepseek-article 设置行，JSON 输出）。</summary>
public interface IArticleRefineProvider
{
    /// <summary>调用一次模型并返回未校验的输出字段。</summary>
    /// <param name="configuration">deepseek-article 配置。</param>
    /// <param name="request">微调输入。</param>
    /// <param name="cancellationToken">取消令牌（含 120 秒超时）。</param>
    Task<ArticleRefineModelOutput> RefineAsync(ResearchProviderConfiguration configuration, ArticleRefineModelRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>模型微调输入。</summary>
/// <param name="Scope">微调范围。</param>
/// <param name="Instruction">用户指令。</param>
/// <param name="Title">当前标题。</param>
/// <param name="BodyMarkdown">当前正文。</param>
/// <param name="SelectionMarkdown">框选片段；整体微调为空。</param>
/// <param name="Language">文章语言。</param>
public sealed record ArticleRefineModelRequest(ArticleRefineScope Scope, string Instruction, string Title, string BodyMarkdown,
    string? SelectionMarkdown, string Language);

/// <summary>模型微调输出（未校验）。</summary>
/// <param name="Title">整体微调时的新标题；为空表示保持原标题。</param>
/// <param name="BodyMarkdown">整体微调时的新正文。</param>
/// <param name="ReplacementMarkdown">框选微调时替换选中片段的新内容。</param>
public sealed record ArticleRefineModelOutput(string? Title, string? BodyMarkdown, string? ReplacementMarkdown);

/// <summary>编辑器图片：上传、读取与导出时批量加载。</summary>
public interface IContentImageService
{
    /// <summary>校验魔数与大小后写入对象存储并登记元数据。</summary>
    /// <param name="tenantId">租户标识。</param>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="userId">上传用户标识。</param>
    /// <param name="content">文件内容（≤5 MB）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<ContentImageUploadResult> UploadAsync(Guid tenantId, Guid siteId, Guid userId, ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default);

    /// <summary>读取图片元数据（含所属租户），不存在时返回空。</summary>
    /// <param name="imageId">图片标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<ContentImageRecord?> FindAsync(Guid imageId, CancellationToken cancellationToken = default);

    /// <summary>打开图片对象流；对象缺失时返回空。</summary>
    /// <param name="image">图片元数据。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<Stream?> OpenAsync(ContentImageRecord image, CancellationToken cancellationToken = default);

    /// <summary>批量读取同一租户的图片内容（导出 docx 嵌入用）；他租户或缺失的图片不返回。</summary>
    /// <param name="tenantId">租户标识。</param>
    /// <param name="imageIds">图片标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyDictionary<Guid, ContentImageBinary>> LoadAsync(Guid tenantId, IReadOnlyCollection<Guid> imageIds,
        CancellationToken cancellationToken = default);
}

/// <summary>图片元数据。</summary>
/// <param name="ImageId">图片标识。</param>
/// <param name="TenantId">所属租户。</param>
/// <param name="SiteId">上传时的品牌工作区。</param>
/// <param name="StorageKey">对象存储键。</param>
/// <param name="ContentType">图片类型。</param>
/// <param name="ByteSize">字节数。</param>
/// <param name="Width">宽度。</param>
/// <param name="Height">高度。</param>
public sealed record ContentImageRecord(Guid ImageId, Guid TenantId, Guid SiteId, string StorageKey, string ContentType,
    long ByteSize, int? Width, int? Height);

/// <summary>图片二进制内容。</summary>
/// <param name="ContentType">图片类型。</param>
/// <param name="Data">文件内容。</param>
/// <param name="Width">宽度。</param>
/// <param name="Height">高度。</param>
public sealed record ContentImageBinary(string ContentType, byte[] Data, int? Width, int? Height);
