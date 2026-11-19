using System.Threading.RateLimiting;

namespace Jogeoai.Api;

/// <summary>品牌识别按真实会话用户限流，切换或伪造租户头不能绕过。</summary>
public static class BrandRecognitionRateLimit
{
    /// <summary>识别接口限流策略名称。</summary>
    public const string Policy = "brand-recognition";

    /// <summary>已认证用户使用服务端会话用户标识，匿名请求使用连接 IP；不信任请求头中的用户或租户标识。</summary>
    public static string PartitionKey(HttpContext context)
        => SessionAuthenticationMiddleware.GetContext(context) is { } identity
            ? "user:" + identity.User.UserId.ToString("N")
            : "anonymous:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");

    /// <summary>每个用户每分钟最多六次，不排队；这是资源保护而非套餐次数或积分计费。</summary>
    public static RateLimitPartition<string> Partition(HttpContext context)
        => RateLimitPartition.GetFixedWindowLimiter(PartitionKey(context), _ => new FixedWindowRateLimiterOptions
        { PermitLimit = 6, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
}
