using Jogeoai.Domain;

namespace Jogeoai.Application;

/// <summary>固定 Luna 模型的产品信息多模态识别 Provider。</summary>
public interface IProductParseProvider
{
    /// <summary>Provider 标识。</summary>
    string Provider { get; }

    /// <summary>以文本与图片内容块调用模型，返回逐字段草稿与来源片段。</summary>
    /// <param name="input">冻结的识别输入。</param>
    /// <param name="configuration">研究 Provider 配置（openai-compatible，回退 openai-api）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="ProductParseException">PRODUCT_PARSE_IMAGE_UNSUPPORTED、PRODUCT_PARSE_RETRYABLE、PRODUCT_PARSE_INVALID_OUTPUT 等。</exception>
    Task<ProductParseResult> ParseAsync(ProductParseInput input, ResearchProviderConfiguration configuration,
        CancellationToken cancellationToken = default);
}
