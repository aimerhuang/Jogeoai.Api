using Jogeoai.Domain;

namespace Jogeoai.Application;

/// <summary>按识别任务类型装配冻结的识别输入。</summary>
public interface IKnowledgeProductSourceReader
{
    /// <summary>
    /// file：读取版本对象，文本 ≥ 200 字用文本，否则用图片；url：读取链接资料的 Markdown；
    /// completion：不读对象，按产品名在品牌范围检索最多 12 条证据。
    /// </summary>
    /// <exception cref="ProductParseException">PRODUCT_PARSE_SOURCE_UNSUPPORTED、PRODUCT_PARSE_SOURCE_UNREADABLE。</exception>
    Task<ProductParseInput> ReadAsync(ProductParseJobLease job, CancellationToken cancellationToken = default);
}
