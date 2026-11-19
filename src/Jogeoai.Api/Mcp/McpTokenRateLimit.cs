using System.Threading.RateLimiting;

namespace Jogeoai.Api.Mcp;

/// <summary>/mcp 按令牌限流：每个令牌固定窗口每分钟 60 次 HTTP 请求（含 initialize、tools/list、tools/call），不排队，超出返回 429。</summary>
public static class McpTokenRateLimit
{
    /// <summary>限流策略名称。</summary>
    public const string Policy = "mcp-token";

    /// <summary>每个窗口允许的请求数。</summary>
    public const int PermitLimit = 60;

    /// <summary>按已校验的令牌标识分区；令牌中间件已拒绝无令牌请求，兜底分区只用于异常路径。</summary>
    /// <param name="context">当前 HTTP 请求。</param>
    public static RateLimitPartition<string> Partition(HttpContext context)
        => RateLimitPartition.GetFixedWindowLimiter(PartitionKey(context),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = PermitLimit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });

    /// <summary>分区键：mcp:{tokenId}；没有调用方时为 mcp:anonymous。</summary>
    /// <param name="context">当前 HTTP 请求。</param>
    public static string PartitionKey(HttpContext context)
        => McpCallerContext.Get(context) is { } caller ? "mcp:" + caller.TokenId.ToString("N") : "mcp:anonymous";
}
