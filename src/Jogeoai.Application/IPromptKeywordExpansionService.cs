using Jogeoai.Domain;

namespace Jogeoai.Application;

/// <summary>围绕核心词同步生成候选监测问题；不落库、不扣积分。</summary>
public interface IPromptKeywordExpansionService
{
    /// <summary>为指定品牌生成 5 个行业词问题和 5 个品牌口碑问题，并排除已有和已展示的问题。</summary>
    Task<KeywordExpansionView> ExpandAsync(Guid tenantId, Guid siteId, KeywordExpansionRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>引导与新增问题使用的同步快速推荐；30 秒内返回，不落库、不扣积分。</summary>
public interface IQuickSuggestionService
{
    /// <summary>依据品牌信息一次生成行业词与品牌词候选问题，并排除已有和已展示的问题。</summary>
    Task<QuickPromptSuggestionView> SuggestPromptsAsync(Guid tenantId, Guid siteId, QuickPromptSuggestionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>依据品牌信息一次给出与品牌直接竞争的品牌，剔除品牌自身、已有竞品和排除项。</summary>
    Task<CompetitorSuggestionView> SuggestCompetitorsAsync(Guid tenantId, Guid siteId, CompetitorSuggestionRequest request,
        CancellationToken cancellationToken = default);
}
