using Jogeoai.Domain;

namespace Jogeoai.Application;

/// <summary>执行品牌结构化研究的 Provider 抽象。</summary>
public interface IResearchProvider
{
    /// <summary>Provider 标识。</summary>
    string Provider { get; }

    /// <summary>执行一次结构化研究。</summary>
    Task<ResearchProviderResult> RunAsync(ResearchProviderConfiguration configuration,
        StartBrandResearchRequest request, ResearchContext context,
        Func<ResearchContext, CancellationToken, Task> saveProgress, CancellationToken cancellationToken = default);
}
