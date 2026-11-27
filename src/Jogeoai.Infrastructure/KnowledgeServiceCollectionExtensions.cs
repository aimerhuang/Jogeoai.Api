using Jogeoai.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Jogeoai.Infrastructure;

/// <summary>注册知识库的存储、Embedding、向量索引和持久化服务。</summary>
public static class KnowledgeServiceCollectionExtensions
{
    /// <summary>按配置选择本地开发实现或外部知识库依赖。</summary>
    /// <param name="services">依赖注入服务集合。</param>
    /// <param name="configuration">应用配置。</param>
    /// <returns>原服务集合。</returns>
    public static IServiceCollection AddKnowledgeServices(this IServiceCollection services,
        IConfiguration configuration)
    {
        var storageProvider = configuration["Knowledge:Storage:Provider"];
        if (string.Equals(storageProvider, "s3", StringComparison.OrdinalIgnoreCase))
            services.AddSingleton<IObjectStorage, S3ObjectStorage>();
        else
            services.AddSingleton<IObjectStorage, LocalObjectStorage>();

        // 解析资源预算（页数、字符、图片像素、墙钟超时）：都有默认值；配置超出允许范围时启动失败，不静默修正。
        services.AddOptions<KnowledgeExtractionLimits>()
            .Bind(configuration.GetSection(KnowledgeExtractionLimits.SectionName))
            .Validate(options => options.IsValid(),
                "Knowledge:Extraction requires MaxPdfPages 1-100000, MaxCharacters 1-100000000, MaxImagePixels 1-1000000000 and ParseTimeoutSeconds 1-3600.")
            .ValidateOnStart();
        services.AddSingleton<IDocumentTextExtractor>(provider =>
            new BasicDocumentTextExtractor(provider.GetRequiredService<IOptions<KnowledgeExtractionLimits>>().Value));
        services.AddHttpClient<OpenAiCompatibleEmbeddingProvider>(client =>
            client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddHttpClient<QdrantVectorIndex>(client =>
            client.Timeout = TimeSpan.FromSeconds(60));

        var embeddingProvider = configuration["Knowledge:Embedding:Provider"];
        if (string.Equals(embeddingProvider, "openai-compatible", StringComparison.OrdinalIgnoreCase)
            || string.Equals(embeddingProvider, "openai", StringComparison.OrdinalIgnoreCase))
            services.AddSingleton<IEmbeddingProvider>(provider =>
                provider.GetRequiredService<OpenAiCompatibleEmbeddingProvider>());
        else
            services.AddSingleton<IEmbeddingProvider, DeterministicEmbeddingProvider>();

        var vectorProvider = configuration["Knowledge:Vector:Provider"];
        if (string.Equals(vectorProvider, "qdrant", StringComparison.OrdinalIgnoreCase))
            services.AddSingleton<IVectorIndex>(provider => provider.GetRequiredService<QdrantVectorIndex>());
        else
            services.AddSingleton<IVectorIndex, LocalVectorIndex>();

        services.AddSingleton<KnowledgeStore>();
        services.AddSingleton<IKnowledgeStore>(provider => provider.GetRequiredService<KnowledgeStore>());
        services.AddSingleton<IKnowledgeRetrievalService>(provider => provider.GetRequiredService<KnowledgeStore>());
        // 产品信息：实体存储、Luna 多模态识别 Provider 与来源装配；API 与 Worker 共用。
        services.AddSingleton<IKnowledgeProductStore, KnowledgeProductStore>();
        services.AddSingleton<IProductParseProvider, LunaProductParseProvider>();
        services.AddSingleton<IKnowledgeProductSourceReader, KnowledgeProductSourceReader>();
        // 品牌知识条目（迁移 043）：API 读写条目，Worker 领取同步租约写检索投影。
        services.AddSingleton<IBrandKnowledgeItemStore, BrandKnowledgeItemStore>();
        // 文章生成「指定产品」：条目优先、未迁移的已入库旧产品兜底；ContentStore、素材选择与文章创作共用。
        services.AddSingleton<IArticleProductResolver, ArticleProductResolver>();
        // 网址识别（迁移 043）：API 入队与查询，Worker 领取租约读取网页并调用 Luna 识别候选。
        services.AddSingleton<IKnowledgeExtractJobStore, KnowledgeExtractJobStore>();
        services.AddSingleton<IKnowledgeExtractProvider, LunaKnowledgeExtractProvider>();
        // 网页无法直接读取时由 Luna web_search 打开网址读取正文（同一网关与 research 客户端）。
        services.AddSingleton<IKnowledgePageReadProvider, LunaKnowledgePageReader>();
        return services;
    }
}
