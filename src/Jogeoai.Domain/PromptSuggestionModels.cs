namespace Jogeoai.Domain;

/// <summary>核心词拓展请求：围绕一个核心词生成行业词与品牌口碑候选问题，不落库、不扣积分。</summary>
/// <param name="Keyword">核心词，修剪后 1–20 字；同时作为候选问题的分组名。</param>
/// <param name="ExcludedQuestions">已展示、已选或不希望再次出现的问题，最多 300 条。</param>
/// <param name="Locale">候选问题语言；省略时使用品牌默认语言。</param>
public sealed record KeywordExpansionRequest(string Keyword, IReadOnlyList<string>? ExcludedQuestions = null, string? Locale = null);

/// <summary>核心词拓展得到的一条候选问题。</summary>
/// <param name="Prompt">候选问题正文。</param>
/// <param name="Branded">是否为点名当前品牌的品牌词（口碑）问题。</param>
/// <param name="Intent">可选的提问意图说明。</param>
public sealed record KeywordSuggestion(string Prompt, bool Branded, string? Intent = null);

/// <summary>核心词拓展结果。</summary>
/// <param name="Keyword">规范后的核心词。</param>
/// <param name="Industry">行业词问题（不含品牌名），最多 5 条。</param>
/// <param name="Branded">结合当前品牌的口碑问题（含品牌名或别名），最多 5 条。</param>
/// <param name="Partial">是否未凑满 5 + 5 条。</param>
public sealed record KeywordExpansionView(string Keyword, IReadOnlyList<KeywordSuggestion> Industry,
    IReadOnlyList<KeywordSuggestion> Branded, bool Partial);

/// <summary>核心词拓展的固定规则。</summary>
public static class KeywordExpansionLimits
{
    /// <summary>核心词最大字数。</summary>
    public const int KeywordMaxLength = 20;

    /// <summary>每类候选条数。</summary>
    public const int PerKind = 5;

    /// <summary>单条候选最大字数。</summary>
    public const int PromptMaxLength = 200;

    /// <summary>排除问题最大条数。</summary>
    public const int MaxExcluded = 300;
}

/// <summary>快速推荐请求：只依据品牌名称、别名、介绍和主域名，一次模型调用同步生成候选问题；不读取官网、不落库、不扣积分。</summary>
/// <param name="ExcludedQuestions">已展示、已选或不希望再次出现的问题，最多 300 条。</param>
/// <param name="Locale">候选问题语言；省略时使用品牌默认语言。</param>
/// <param name="Count">期望条数 1–10，默认 10；约五分之一为品牌词，其余为行业词。</param>
public sealed record QuickPromptSuggestionRequest(IReadOnlyList<string>? ExcludedQuestions = null, string? Locale = null, int? Count = null);

/// <summary>快速推荐结果。</summary>
/// <param name="Industry">行业词问题（不含品牌名），邀请 AI 列举、推荐、排行或比较品牌。</param>
/// <param name="Branded">品牌词问题（含品牌名或别名），覆盖质量、口碑、价格、对比、人群、售后等角度。</param>
/// <param name="Partial">是否未凑满期望条数。</param>
public sealed record QuickPromptSuggestionView(IReadOnlyList<KeywordSuggestion> Industry, IReadOnlyList<KeywordSuggestion> Branded,
    bool Partial);

/// <summary>竞品推荐请求：一次模型调用同步给出与品牌直接竞争的品牌；不落库、不扣积分。</summary>
/// <param name="ExcludedNames">已展示或已选、不希望再次出现的竞品名称，最多 50 个。</param>
/// <param name="Locale">竞品说明语言；省略时使用品牌默认语言。</param>
/// <param name="Market">目标市场（国家或地区代码）；省略时使用品牌默认国家。</param>
public sealed record CompetitorSuggestionRequest(IReadOnlyList<string>? ExcludedNames = null, string? Locale = null, string? Market = null);

/// <summary>一个推荐竞品。</summary>
/// <param name="Name">竞品名称。</param>
/// <param name="Domain">竞品官网主机名（如 example.com）；模型不确定时为空，未经联网核验。</param>
/// <param name="Reason">推荐理由（简短说明）。</param>
public sealed record CompetitorSuggestion(string Name, string? Domain = null, string? Reason = null);

/// <summary>竞品推荐结果。</summary>
/// <param name="Competitors">推荐竞品，最多 10 个，已剔除品牌自身与重复项；可以为空。</param>
public sealed record CompetitorSuggestionView(IReadOnlyList<CompetitorSuggestion> Competitors);

/// <summary>快速推荐与竞品推荐的固定规则。</summary>
public static class QuickSuggestionLimits
{
    /// <summary>快速推荐最多条数。</summary>
    public const int MaxPrompts = 10;

    /// <summary>竞品推荐最多个数。</summary>
    public const int MaxCompetitors = 10;

    /// <summary>排除竞品名称最大个数。</summary>
    public const int MaxExcludedNames = 50;

    /// <summary>竞品名称最大字数。</summary>
    public const int CompetitorNameMaxLength = 100;

    /// <summary>服务端总时间预算（含 Provider 回退），保证 30 秒内返回。</summary>
    public static readonly TimeSpan TotalBudget = TimeSpan.FromSeconds(28);

    /// <summary>首个 Provider 的单次调用上限，留出回退时间。</summary>
    public static readonly TimeSpan FirstProviderCap = TimeSpan.FromSeconds(20);

    /// <summary>剩余预算低于此值时不再尝试下一个 Provider。</summary>
    public static readonly TimeSpan MinimumAttempt = TimeSpan.FromSeconds(3);
}
