using Jogeoai.Domain;

namespace Jogeoai.Application;

/// <summary>独立品牌识别模型；只返回建议，不保存品牌、研究任务或积分记录。</summary>
public interface IBrandRecognitionProvider
{
    /// <summary>从有界、不可信资料中提取有来源依据的品牌建议。</summary>
    Task<BrandRecognitionView> RecognizeAsync(string sourceText, bool truncated,
        ResearchProviderConfiguration configuration, CancellationToken cancellationToken = default);
}

/// <summary>按品牌名称和官网联网搜索公司资料；结果是不可信资料，仍需提取步骤核对摘录。</summary>
public interface IBrandSearchProvider
{
    /// <summary>执行一次有界搜索；网关失败或无引用时返回空摘要与提示，调用方取消时抛出取消异常。</summary>
    Task<BrandSearchResult> SearchAsync(BrandSearchQuery query, ResearchProviderConfiguration configuration,
        CancellationToken cancellationToken = default);
}

/// <summary>联网搜索的检索依据，均为用户输入数据。</summary>
public sealed record BrandSearchQuery(string? Name, IReadOnlyList<string> Aliases, string? Url);

/// <summary>联网搜索结果；Summary 为 null 时表示没有可用资料，Warning 说明原因。</summary>
public sealed record BrandSearchResult(string? Summary, IReadOnlyList<BrandRecognitionSource> Sources, string? Warning);

/// <summary>品牌资料装配、超时控制与识别入口。</summary>
public interface IBrandRecognitionService
{
    /// <summary>联网搜索并读取可选公开网页后识别；名称、介绍或网址至少提供一项。</summary>
    Task<BrandRecognitionView> RecognizeAsync(BrandRecognitionRequest request, CancellationToken cancellationToken = default);
}
