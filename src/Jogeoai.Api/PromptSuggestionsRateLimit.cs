using System.Threading.RateLimiting;

namespace Jogeoai.Api;

/// <summary>候选问题生成按真实会话用户限流，切换或伪造租户头不能绕过。</summary>
public static class PromptSuggestionsRateLimit
{
    /// <summary>核心词拓展限流策略名称。</summary>
    public const string Policy = "prompt-suggestions";

    /// <summary>每个用户每分钟最多六次，不排队；这是资源保护而非套餐次数或积分计费。</summary>
    public static RateLimitPartition<string> Partition(HttpContext context)
        => RateLimitPartition.GetFixedWindowLimiter("prompt-suggestions:" + BrandRecognitionRateLimit.PartitionKey(context),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 6, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
}
