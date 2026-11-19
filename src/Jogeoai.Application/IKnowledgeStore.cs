using Jogeoai.Domain;

namespace Jogeoai.Application;

/// <summary>知识库元数据、版本和摄取任务的持久化边界。</summary>
public interface IKnowledgeStore
{
    /// <summary>创建公司级或品牌级文件夹。</summary>
    Task<KnowledgeFolderView> CreateFolderAsync(Guid tenantId, Guid? siteId, KnowledgeFolderWrite request,
        CancellationToken cancellationToken = default);

    /// <summary>列出指定资料范围的文件夹。</summary>
    Task<IReadOnlyList<KnowledgeFolderView>> ListFoldersAsync(Guid tenantId, Guid? siteId,
        CancellationToken cancellationToken = default);

    /// <summary>重命名、移动或归档文件夹。</summary>
    Task<KnowledgeFolderView?> UpdateFolderAsync(Guid tenantId, Guid folderId, KnowledgeFolderUpdate request,
        CancellationToken cancellationToken = default);

    /// <summary>接收一个公司级或品牌级资料版本；指定已有资料标识时作为新版本。</summary>
    /// <exception cref="CollectorStoreException">已有资料是品牌知识条目的检索投影（source_kind=item）时 KNOWLEDGE_DOCUMENT_MANAGED_BY_ITEM（409）。</exception>
    Task<KnowledgeDocumentView> CreateDocumentAsync(Guid tenantId, Guid? siteId, KnowledgeUploadCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>分页读取租户范围内的资料。</summary>
    Task<CursorPage<KnowledgeDocumentView>> ListDocumentsAsync(Guid tenantId, Guid? siteId,
        string? cursor = null, int limit = 50, KnowledgeDocumentListFilter? filter = null,
        CancellationToken cancellationToken = default);

    /// <summary>读取当前租户可见的资料详情。</summary>
    Task<KnowledgeDocumentView?> GetDocumentAsync(Guid tenantId, Guid documentId,
        CancellationToken cancellationToken = default);

    /// <summary>打开资料二进制流。</summary>
    Task<Stream?> OpenDocumentAsync(Guid tenantId, Guid documentId,
        CancellationToken cancellationToken = default);

    /// <summary>修改资料元数据。</summary>
    /// <exception cref="CollectorStoreException">资料是品牌知识条目的检索投影（source_kind=item）时 KNOWLEDGE_DOCUMENT_MANAGED_BY_ITEM（409）。</exception>
    Task<KnowledgeDocumentView?> UpdateDocumentAsync(Guid tenantId, Guid documentId,
        KnowledgeDocumentUpdate update, CancellationToken cancellationToken = default);

    /// <summary>重新排队失败的当前版本。</summary>
    /// <exception cref="CollectorStoreException">资料是品牌知识条目的检索投影（source_kind=item）时 KNOWLEDGE_DOCUMENT_MANAGED_BY_ITEM（409）。</exception>
    Task<KnowledgeDocumentView?> RetryDocumentAsync(Guid tenantId, Guid documentId,
        CancellationToken cancellationToken = default);

    /// <summary>停用资料并立即排除新检索；资料不存在时返回 null。</summary>
    /// <exception cref="CollectorStoreException">资料是品牌知识条目的检索投影（source_kind=item）时 KNOWLEDGE_DOCUMENT_MANAGED_BY_ITEM（409），这类资料只能经 <see cref="DisableKnowledgeItemDocumentAsync"/> 停用。</exception>
    Task<KnowledgeDocumentView?> DisableDocumentAsync(Guid tenantId, Guid documentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 停用品牌知识条目的检索投影资料（条目被删除后）。只由同步 Worker 调用：与 <see cref="DisableDocumentAsync"/> 行为相同，
    /// 但允许停用 source_kind=item 的资料。资料不存在时返回 null，已停用的资料重复停用无副作用。
    /// </summary>
    /// <param name="tenantId">租户。</param>
    /// <param name="documentId">检索投影资料。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<KnowledgeDocumentView?> DisableKnowledgeItemDocumentAsync(Guid tenantId, Guid documentId,
        CancellationToken cancellationToken = default);

    /// <summary>读取租户容量使用情况。</summary>
    Task<KnowledgeUsageView> GetUsageAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>领取一个摄取任务。</summary>
    Task<KnowledgeIngestJobLease?> ClaimIngestJobAsync(string workerId, TimeSpan lease,
        CancellationToken cancellationToken = default);

    /// <summary>保存解析结果并进入索引阶段。</summary>
    Task SaveExtractedDocumentAsync(KnowledgeIngestJobLease job, ExtractedDocument document,
        CancellationToken cancellationToken = default);

    /// <summary>保存片段索引并原子发布当前版本。</summary>
    Task PublishIndexedVersionAsync(KnowledgeIngestJobLease job, IReadOnlyList<KnowledgeChunk> chunks,
        CancellationToken cancellationToken = default);

    /// <summary>记录摄取失败并安排重试或终止。</summary>
    Task FailIngestJobAsync(KnowledgeIngestJobLease job, string errorCode, string errorMessage,
        bool retryable, CancellationToken cancellationToken = default);

    /// <summary>把已读取的公开网页正文作为 .md 版本写入品牌知识库（source_kind=link，配额同样生效）。</summary>
    /// <exception cref="CollectorStoreException">KNOWLEDGE_LINK_UNREADABLE（422）当网页读取未成功。</exception>
    Task<KnowledgeDocumentView> AddLinkDocumentAsync(Guid tenantId, Guid siteId, KnowledgeLinkWrite request,
        ArticleSourceReadResult page, CancellationToken cancellationToken = default);

    /// <summary>把已入库产品渲染出的 Markdown 写入品牌知识库（document_type=product，product_key=产品 ID，source_kind=product）；已有资料时作为新版本。</summary>
    Task<KnowledgeDocumentView> UploadProductDocumentAsync(Guid tenantId, Guid siteId, Guid productId, string title,
        string markdown, IReadOnlyList<string> tags, Guid? existingDocumentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 把品牌知识条目渲染出的 Markdown 写入品牌知识库作为检索投影（document_type=维度键，product_key=条目 ID，source_kind=item，
    /// 文件名 item-条目ID.md）；已有资料时作为新版本。只由同步 Worker 调用。
    /// </summary>
    /// <param name="tenantId">租户。</param>
    /// <param name="siteId">品牌。</param>
    /// <param name="itemId">条目标识。</param>
    /// <param name="dimension">条目维度。</param>
    /// <param name="title">资料标题（条目标题）。</param>
    /// <param name="markdown">条目 Markdown。</param>
    /// <param name="existingDocumentId">已有的检索投影文档；为空时新建资料。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="CollectorStoreException">KNOWLEDGE_EMPTY_CONTENT（400）；已有资料已停用或不属于该品牌时 RESOURCE_NOT_FOUND（404）；容量超限 KNOWLEDGE_QUOTA_EXCEEDED（413）。</exception>
    Task<KnowledgeDocumentView> UploadKnowledgeItemDocumentAsync(Guid tenantId, Guid siteId, Guid itemId,
        BrandKnowledgeDimension dimension, string title, string markdown, Guid? existingDocumentId,
        CancellationToken cancellationToken = default);
}

/// <summary>知识库证据检索与上下文快照边界。</summary>
public interface IKnowledgeRetrievalService
{
    /// <summary>按品牌范围检索当前有效版本的证据片段。</summary>
    Task<KnowledgeSearchResponse> SearchAsync(Guid tenantId, Guid siteId, KnowledgeSearchRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>创建并保存一次可追溯的写作上下文快照。</summary>
    Task<KnowledgeContextResponse> CreateContextAsync(Guid tenantId, Guid siteId, KnowledgeContextRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>读取同一租户的上下文命中快照。</summary>
    Task<KnowledgeContextResponse?> GetContextAsync(Guid tenantId, Guid contextId,
        CancellationToken cancellationToken = default);
}

/// <summary>二进制对象存储适配器。</summary>
public interface IObjectStorage
{
    /// <summary>写入对象并返回对象键。</summary>
    Task<string> PutAsync(Guid tenantId, Guid versionId, Stream content, CancellationToken cancellationToken = default);

    /// <summary>按服务端生成的非知识库对象键写入（目前只允许编辑器图片 content-images/{tenantId:N}/{imageId:N}）；知识库仍使用 <see cref="PutAsync"/>。</summary>
    Task PutAtAsync(string objectKey, Stream content, CancellationToken cancellationToken = default);

    /// <summary>读取对象。</summary>
    Task<Stream?> OpenReadAsync(string objectKey, CancellationToken cancellationToken = default);

    /// <summary>删除对象。</summary>
    Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default);
}

/// <summary>受限文档正文解析器。</summary>
public interface IDocumentTextExtractor
{
    /// <summary>判断扩展名和 MIME 是否属于首期支持范围。</summary>
    bool CanExtract(string extension, string? contentType);

    /// <summary>解析文件正文和可追溯定位信息。</summary>
    Task<ExtractedDocument> ExtractAsync(Stream content, string extension, string? contentType,
        CancellationToken cancellationToken = default);
}

/// <summary>Embedding 服务边界。</summary>
public interface IEmbeddingProvider
{
    /// <summary>当前 Embedding 配置档案。</summary>
    string Profile { get; }

    /// <summary>当前使用的模型标识。</summary>
    string Model { get; }

    /// <summary>当前模型向量维度。</summary>
    int Dimensions { get; }

    /// <summary>按顺序计算文本向量。</summary>
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default);
}

/// <summary>向量索引边界。</summary>
public interface IVectorIndex
{
    /// <summary>幂等写入片段向量。</summary>
    Task UpsertAsync(Guid tenantId, Guid versionId, IReadOnlyList<KnowledgeChunk> chunks,
        IReadOnlyList<float[]> vectors, CancellationToken cancellationToken = default);

    /// <summary>按租户和可见版本查询向量。</summary>
    Task<IReadOnlyList<(Guid ChunkId, double Score)>> SearchAsync(Guid tenantId, IReadOnlySet<Guid> versionIds,
        float[] query, int topK, CancellationToken cancellationToken = default);

    /// <summary>删除指定版本的向量。</summary>
    Task DeleteVersionAsync(Guid tenantId, Guid versionId, CancellationToken cancellationToken = default);
}
