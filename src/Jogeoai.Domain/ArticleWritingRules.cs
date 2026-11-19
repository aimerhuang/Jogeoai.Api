namespace Jogeoai.Domain;

/// <summary>文章类型与篇幅的统一契约，供预览、提交和生成校验共同使用。</summary>
public static class ArticleWritingRules
{
    /// <summary>产品选型指南的稳定策略键。</summary>
    public const string ProductSelection = "product_selection";

    /// <summary>当前支持的文章类型；旧类型保持原有语义。</summary>
    public static IReadOnlyList<string> Strategies { get; } = Array.AsReadOnly(new[]
        { "answer_guide", ProductSelection, "pain_comparison", "reference_structure" });

    /// <summary>当前支持的篇幅档位。</summary>
    public static IReadOnlyList<string> LengthProfiles { get; } = Array.AsReadOnly(new[] { "short", "medium", "long" });

    /// <summary>判断文章类型是否受支持，键值区分大小写。</summary>
    public static bool IsSupportedStrategy(string? strategy) => strategy is not null && Strategies.Contains(strategy);

    /// <summary>判断篇幅档位是否受支持。</summary>
    public static bool IsSupportedLength(string? profile) => profile is not null && LengthProfiles.Contains(profile);

    /// <summary>返回所选类型的正文长度范围；中文按非空白字符，英文按词。</summary>
    public static (int Min, int Max) LengthRange(string language, string profile, string strategy)
    {
        if (language == "en-US")
            return profile switch { "short" => (600, 900), "long" => (1600, 2200), _ => (1000, 1400) };
        return profile switch
        {
            "short" => (800, 1200),
            "long" => (2500, 3500),
            _ when strategy == ProductSelection => (2000, 3000),
            _ => (1500, 2200)
        };
    }
}
