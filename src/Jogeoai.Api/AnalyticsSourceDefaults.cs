namespace Jogeoai.Api;

/// <summary>未显式筛选来源时的「全部来源」取值；<c>AnalyticsController.AllSourcesFilter</c> 与 MCP 只读工具共用，新增 Provider 只改这里。</summary>
public static class AnalyticsSourceDefaults
{
    /// <summary>全部回答 Provider。</summary>
    public static readonly IReadOnlyList<string> Providers = ["cloro", "doubao_api", "deepseek_api", "packy_qwen", "packy_glm", "packy_kimi", "packy_yuanbao", "domestic_collector"];

    /// <summary>全部采集模式。</summary>
    public static readonly IReadOnlyList<string> CaptureModes = ["api", "scraped"];

    /// <summary>全部证据类别。</summary>
    public static readonly IReadOnlyList<string> EvidenceClasses = ["api_response", "observed_surface", "unknown"];
}
