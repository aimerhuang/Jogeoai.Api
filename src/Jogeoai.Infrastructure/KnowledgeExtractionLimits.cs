namespace Jogeoai.Infrastructure;

/// <summary>知识库文件解析的资源预算，绑定配置节 <c>Knowledge:Extraction</c>；API 与 Worker 共用同一份注册。</summary>
public sealed class KnowledgeExtractionLimits
{
    /// <summary>配置节名称。</summary>
    public const string SectionName = "Knowledge:Extraction";

    /// <summary>PDF 页数上限；超过时在解析任何页之前拒绝（<c>KNOWLEDGE_PDF_TOO_MANY_PAGES</c>）。默认 1000，允许 1–100000。</summary>
    public int MaxPdfPages { get; set; } = 1000;

    /// <summary>正文字符数上限；PDF 逐页累计，超过即停止（<c>KNOWLEDGE_CONTENT_TOO_LARGE</c>）。默认 10,000,000，允许 1–100,000,000。</summary>
    public int MaxCharacters { get; set; } = 10_000_000;

    /// <summary>
    /// PDF 内嵌图片在解码之前按声明宽 × 高计算的像素上限，只约束需要解码（转成 PNG）的图片：超过的图片不解码、跳过并标记截断。
    /// 原始字节就是 JPEG、不解码直接送出的图片不受本项约束，仍只受单张 10 MB 的字节上限约束。默认 40,000,000，允许 1–1,000,000,000。
    /// </summary>
    public long MaxImagePixels { get; set; } = 40_000_000;

    /// <summary>单次解析的墙钟超时秒数；超时返回 <c>KNOWLEDGE_PARSE_TIMEOUT</c>。默认 90，允许 1–3600。</summary>
    public int ParseTimeoutSeconds { get; set; } = 90;

    /// <summary>四项预算是否都在允许范围内；启动时校验，不合法的配置不会被静默修正。</summary>
    public bool IsValid()
        => MaxPdfPages is >= 1 and <= 100_000
            && MaxCharacters is >= 1 and <= 100_000_000
            && MaxImagePixels is >= 1 and <= 1_000_000_000
            && ParseTimeoutSeconds is >= 1 and <= 3600;
}
