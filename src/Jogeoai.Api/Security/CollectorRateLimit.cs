using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Jogeoai.Api.Security;

/// <summary>
/// 采集接口（<c>/internal/collector/*</c>，含 health）按客户端 IP 的限流：固定窗口 1 分钟，不排队，超出返回 429。
/// 只有当请求的客户端地址已经确定、并且不是我们自己的受信代理时才限流（见 <see cref="LimitedClient"/>）；
/// 其余情况不限流，避免所有采集电脑落进同一个桶后被少量匿名请求一起锁住。分区键只来自连接地址，不读取任何请求头或请求体。
/// </summary>
public static class CollectorRateLimit
{
    /// <summary>限流策略名称。</summary>
    public const string Policy = "collector-internal";

    /// <summary>每个客户端 IP 每分钟允许请求数的配置键。</summary>
    public const string PermitPerMinuteKey = "Collector:RateLimit:PermitPerMinute";

    /// <summary>
    /// 未配置时的每分钟请求数。一个网页采集进程约 100 次 / 分钟，App 节点每个绑定约 1–5 次 / 分钟；
    /// 600 允许同一出口 IP 后面有数个采集进程。采集端把 429 当作“被拒绝”并退避，所以宁高勿低。
    /// </summary>
    public const int DefaultPermitPerMinute = 600;

    /// <summary>
    /// 读取每分钟请求数。未配置或为空白时取 <see cref="DefaultPermitPerMinute"/>；
    /// 不是不小于 1 的整数时抛出 <see cref="InvalidOperationException"/>（应用无法启动），错误信息只包含配置键名。
    /// </summary>
    /// <param name="configuration">应用配置。</param>
    /// <returns>每个客户端 IP 每分钟允许的请求数。</returns>
    public static int ParsePermitPerMinute(IConfiguration configuration)
    {
        var raw = configuration[PermitPerMinuteKey];
        if (string.IsNullOrWhiteSpace(raw)) return DefaultPermitPerMinute;
        if (!int.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var permit) || permit < 1)
            throw new InvalidOperationException($"配置无效：{PermitPerMinuteKey} 必须是不小于 1 的整数。");
        return permit;
    }

    /// <summary>不限流的请求共用的分区键；它不是 IP 地址的写法，不会与某个客户端的分区重名。</summary>
    public const string UnlimitedPartitionKey = "collector-internal:unlimited";

    /// <summary>
    /// 应当计入限流的客户端地址；返回 null 表示这个请求不限流。以下任一情况不限流：
    /// 没有启用受信转发头（客户端地址无从得知，所有请求都来自同一个对端）；连接没有对端地址；
    /// 处理转发头之后的地址仍然是我们自己的受信代理（网关没有告诉我们客户端是谁）。
    /// </summary>
    /// <param name="forwardedHeaders">受信转发头设置；没有注册时按未启用处理。</param>
    /// <param name="remoteIpAddress">转发头中间件处理之后的 <c>Connection.RemoteIpAddress</c>。</param>
    /// <returns>客户端地址，或 null。</returns>
    public static IPAddress? LimitedClient(ForwardedHeadersSettings? forwardedHeaders, IPAddress? remoteIpAddress)
    {
        if (forwardedHeaders is not { Enabled: true } || remoteIpAddress is null) return null;
        return IsTrustedProxy(forwardedHeaders, remoteIpAddress) ? null : remoteIpAddress;
    }

    /// <summary>
    /// 地址是否属于受信代理：等于 <c>KnownProxies</c> 中的某一项，或落在 <c>KnownNetworks</c> 的某个网段内。
    /// IPv4 地址与它的 IPv4 映射 IPv6 写法（<c>::ffff:a.b.c.d</c>）视为同一个地址，两个方向都比较。
    /// </summary>
    /// <param name="forwardedHeaders">受信转发头设置。</param>
    /// <param name="address">要判断的地址。</param>
    public static bool IsTrustedProxy(ForwardedHeadersSettings forwardedHeaders, IPAddress address)
    {
        if (Matches(address)) return true;
        if (address.IsIPv4MappedToIPv6) return Matches(address.MapToIPv4());
        return address.AddressFamily == AddressFamily.InterNetwork && Matches(address.MapToIPv6());

        bool Matches(IPAddress candidate)
        {
            foreach (var proxy in forwardedHeaders.KnownProxies)
                if (proxy.Equals(candidate)) return true;
            foreach (var network in forwardedHeaders.KnownNetworks)
                if (network.Contains(candidate)) return true;
            return false;
        }
    }

    /// <summary>限流分区：<see cref="LimitedClient"/> 给出地址时按该地址的固定窗口限流，否则不限流。</summary>
    /// <param name="context">当前 HTTP 请求。</param>
    /// <param name="permitPerMinute">每个客户端 IP 每分钟允许的请求数。</param>
    public static RateLimitPartition<string> Partition(HttpContext context, int permitPerMinute)
    {
        var client = LimitedClient(context.RequestServices.GetService<ForwardedHeadersSettings>(), context.Connection.RemoteIpAddress);
        return client is null
            ? RateLimitPartition.GetNoLimiter(UnlimitedPartitionKey)
            : RateLimitPartition.GetFixedWindowLimiter(client.ToString(),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = permitPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
    }

    /// <summary>注册 collector-internal 限流策略。</summary>
    /// <param name="options">限流配置。</param>
    /// <param name="permitPerMinute">每个客户端 IP 每分钟允许的请求数，不小于 1。</param>
    /// <returns>同一个限流配置。</returns>
    public static RateLimiterOptions AddCollectorInternalPolicy(this RateLimiterOptions options, int permitPerMinute)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(permitPerMinute, 1);
        return options.AddPolicy(Policy, context => Partition(context, permitPerMinute));
    }

    /// <summary>
    /// 启动时调用一次：没有启用受信转发头时采集接口不限流，记录一行日志（Development 为 Information，其他环境为 Warning）。
    /// 日志不包含任何配置值。
    /// </summary>
    /// <param name="services">应用的服务容器。</param>
    /// <param name="environment">宿主环境。</param>
    public static void LogStartupState(IServiceProvider services, IHostEnvironment environment)
        => LogStartupState(
            services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(CollectorRateLimit).FullName!),
            services.GetService<ForwardedHeadersSettings>(), environment.IsDevelopment());

    /// <summary>没有启用受信转发头时记录“采集接口限流未生效”；已启用时不记录。</summary>
    /// <param name="logger">日志记录器。</param>
    /// <param name="forwardedHeaders">受信转发头设置；没有注册时按未启用处理。</param>
    /// <param name="isDevelopment">是否为 Development 环境。</param>
    public static void LogStartupState(ILogger logger, ForwardedHeadersSettings? forwardedHeaders, bool isDevelopment)
    {
        if (forwardedHeaders is { Enabled: true }) return;
        logger.Log(isDevelopment ? LogLevel.Information : LogLevel.Warning,
            "Collector rate limit inactive: trusted forwarded headers are not configured, so the client address is unknown. "
            + "Requests to /internal/collector are not rate-limited by the application.");
    }
}

/// <summary>
/// 所有限流策略共用的拒绝响应：429、<c>Retry-After</c> 头与统一的 RATE_LIMITED 错误体。
/// Program.cs 与集成测试共用，保证响应一致。
/// </summary>
public static class RateLimitRejection
{
    /// <summary>限流器没有给出重试时间时填写的 <c>Retry-After</c> 秒数。</summary>
    public const int FallbackRetryAfterSeconds = 60;

    /// <summary>写入 429 响应。</summary>
    /// <param name="context">被拒绝请求的上下文。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async ValueTask WriteAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var response = context.HttpContext.Response;
        response.StatusCode = StatusCodes.Status429TooManyRequests;
        response.Headers.RetryAfter = RetryAfterSeconds(context.Lease).ToString(CultureInfo.InvariantCulture);
        await response.WriteAsJsonAsync(new { error = new { code = "RATE_LIMITED", message = "请求过于频繁，请稍后重试。" } }, cancellationToken);
    }

    /// <summary>
    /// <c>Retry-After</c> 的秒数：取限流器给出的重试时间并向上取整，至少为 1；
    /// 限流器没有给出时为 <see cref="FallbackRetryAfterSeconds"/>。
    /// </summary>
    /// <param name="lease">被拒绝的限流租约。</param>
    public static int RetryAfterSeconds(RateLimitLease lease)
    {
        if (!lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)) return FallbackRetryAfterSeconds;
        return (int)Math.Clamp(Math.Ceiling(retryAfter.TotalSeconds), 1d, int.MaxValue);
    }
}
