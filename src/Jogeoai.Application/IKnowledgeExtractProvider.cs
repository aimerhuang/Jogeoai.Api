using Jogeoai.Domain;

namespace Jogeoai.Application;

/// <summary>固定 Luna 模型的网址识别 Provider：把网页正文拆成按品牌知识维度归类的原始候选（纯文本，不含图片）。</summary>
public interface IKnowledgeExtractProvider
{
    /// <summary>Provider 标识。</summary>
    string Provider { get; }

    /// <summary>调用模型识别候选；只校验输出结构，候选的维度、长度与原文依据由 <see cref="BrandKnowledgeRules.NormalizeCandidates"/> 判定。</summary>
    /// <param name="input">识别输入（网页正文只在内存中传递，不落库）。</param>
    /// <param name="configuration">研究 Provider 配置（openai-compatible，回退 openai-api）。</param>
    /// <param name="cancellationToken">取消令牌（含调用方超时）。</param>
    /// <exception cref="KnowledgeExtractException">KNOWLEDGE_EXTRACT_NOT_CONFIGURED、KNOWLEDGE_EXTRACT_RETRYABLE、KNOWLEDGE_EXTRACT_INVALID_OUTPUT、KNOWLEDGE_EXTRACT_FAILED。</exception>
    Task<KnowledgeExtractProviderResult> ExtractAsync(KnowledgeExtractInput input, ResearchProviderConfiguration configuration,
        CancellationToken cancellationToken = default);
}

/// <summary>网址识别的模型输入。</summary>
/// <param name="BrandName">品牌名称。</param>
/// <param name="Url">规范化后的网址。</param>
/// <param name="PageTitle">网页标题。</param>
/// <param name="PageText">网页正文；超过 60 000 字时由 Provider 截断并标记。</param>
/// <param name="Dimensions">限定维度；为空表示全部维度。</param>
/// <param name="ExistingTitles">品牌已有条目的维度与标题，仅作为减少重复的提示，最终查重由服务端判定。</param>
public sealed record KnowledgeExtractInput(string BrandName, string Url, string? PageTitle, string PageText,
    IReadOnlyList<BrandKnowledgeDimension>? Dimensions, IReadOnlyList<KnowledgeExtractExistingTitle> ExistingTitles);

/// <summary>已有条目的维度与标题（识别提示用）。</summary>
/// <param name="Dimension">维度。</param>
/// <param name="Title">标题。</param>
public sealed record KnowledgeExtractExistingTitle(BrandKnowledgeDimension Dimension, string Title);

/// <summary>模型识别结果（尚未标准化）。</summary>
/// <param name="Items">模型输出的原始候选，按输出顺序。</param>
/// <param name="Truncated">网页正文是否因超过 60 000 字被截断。</param>
/// <param name="ModelName">实际调用模型。</param>
/// <param name="Provider">Provider 标识。</param>
/// <param name="StrategyVersion">提示词契约版本。</param>
/// <param name="DurationMs">模型调用耗时（含一次修复重试）。</param>
/// <param name="RequestBytes">最后一次请求体字节数（仅用于日志）。</param>
/// <param name="ResponseBytes">最后一次响应体字节数（仅用于日志）。</param>
public sealed record KnowledgeExtractProviderResult(IReadOnlyList<KnowledgeExtractRawCandidate> Items, bool Truncated, string ModelName,
    string Provider, string StrategyVersion, long DurationMs, long RequestBytes, long ResponseBytes);

/// <summary>网址识别链路的稳定错误；Worker 据此决定是否重试。</summary>
/// <param name="code">稳定错误码，如 KNOWLEDGE_EXTRACT_RETRYABLE。</param>
/// <param name="message">面向用户的中文说明，不含网页正文、凭据或模型原始响应。</param>
/// <param name="retryable">是否允许 Worker 重试。</param>
/// <param name="inner">内部异常。</param>
public sealed class KnowledgeExtractException(string code, string message, bool retryable, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>稳定错误码。</summary>
    public string Code { get; } = code;
    /// <summary>是否允许 Worker 重试。</summary>
    public bool Retryable { get; } = retryable;
}
