using System.Globalization;
using System.Net;
using ForwardedHeaderKinds = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders;

namespace Jogeoai.Api.Security;

/// <summary>
/// 受信转发头配置（配置节 <c>ForwardedHeaders</c>）的解析结果。启动时解析一次，之后不再读取配置。
/// </summary>
/// <param name="KnownProxies">受信代理的精确 IP（<c>ForwardedHeaders:KnownProxies</c>），空白项已剔除。</param>
/// <param name="KnownNetworks">受信代理所在的网段（<c>ForwardedHeaders:KnownNetworks</c>，CIDR），空白项已剔除。</param>
/// <param name="ForwardLimit">最多处理的转发跳数（<c>ForwardedHeaders:ForwardLimit</c>），取值 1–5，缺省为 1。</param>
public sealed record ForwardedHeadersSettings(
    IReadOnlyList<IPAddress> KnownProxies,
    IReadOnlyList<System.Net.IPNetwork> KnownNetworks,
    int ForwardLimit)
{
    /// <summary>
    /// 是否处理转发头。只有至少配置了一个受信代理或受信网段时才为 true；
    /// 两个列表都为空时不注册中间件，任何 <c>X-Forwarded-*</c> 都不会被采用。
    /// </summary>
    public bool Enabled => KnownProxies.Count > 0 || KnownNetworks.Count > 0;
}

/// <summary>
/// 受信转发头：只采用来自已配置代理的 <c>X-Forwarded-For</c> 与 <c>X-Forwarded-Proto</c>，
/// 让限流分区、会话来源 IP 和 HTTPS 重定向看到真实的客户端地址与协议。不处理 <c>X-Forwarded-Host</c>。
/// </summary>
public static class ForwardedHeadersSetup
{
    /// <summary>配置节名称。</summary>
    public const string SectionName = "ForwardedHeaders";

    /// <summary>受信代理 IP 列表的配置键。</summary>
    public const string KnownProxiesKey = SectionName + ":KnownProxies";

    /// <summary>受信代理网段（CIDR）列表的配置键。</summary>
    public const string KnownNetworksKey = SectionName + ":KnownNetworks";

    /// <summary>转发跳数上限的配置键。</summary>
    public const string ForwardLimitKey = SectionName + ":ForwardLimit";

    /// <summary>未配置 <c>ForwardedHeaders:ForwardLimit</c> 时使用的跳数。</summary>
    public const int DefaultForwardLimit = 1;

    /// <summary>允许的最小跳数。</summary>
    public const int MinForwardLimit = 1;

    /// <summary>允许的最大跳数。</summary>
    public const int MaxForwardLimit = 5;

    /// <summary>
    /// 解析并注册受信转发头配置。配置无效时立即抛出 <see cref="InvalidOperationException"/>（应用无法启动），
    /// 错误信息只包含配置键名，不包含配置值。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">应用配置。</param>
    /// <returns>同一个服务集合。</returns>
    public static IServiceCollection AddJogeoaiForwardedHeaders(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(Parse(configuration));
        return services;
    }

    /// <summary>
    /// 注册转发头中间件，必须是管道中的第一个中间件，且只调用一次。两个受信列表都为空时什么也不注册；
    /// 没有对端地址的连接（例如 Unix 套接字）不处理转发头。
    /// </summary>
    /// <param name="app">应用管道。</param>
    /// <returns>同一个应用管道。</returns>
    public static IApplicationBuilder UseJogeoaiForwardedHeaders(this IApplicationBuilder app)
    {
        var settings = app.ApplicationServices.GetService<ForwardedHeadersSettings>()
            ?? throw new InvalidOperationException(
                "调用 UseJogeoaiForwardedHeaders() 之前必须先调用 AddJogeoaiForwardedHeaders(configuration)。");
        var logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ForwardedHeadersSetup).FullName!);

        // 框架中间件在两个可信列表都为空时会信任任意来源，所以这里不能带着空列表注册它。
        if (!settings.Enabled)
        {
            logger.LogInformation("ForwardedHeaders disabled: no trusted proxy or network is configured; X-Forwarded-* headers are ignored.");
            return app;
        }

        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaderKinds.XForwardedFor | ForwardedHeaderKinds.XForwardedProto,
            ForwardLimit = settings.ForwardLimit,
        };
        // 框架默认信任 loopback（127.0.0.0/8 与 ::1）；这里只信任配置中写明的地址。
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
#pragma warning disable ASPDEPR005
        // .NET 10 的中间件只读 KnownIPNetworks，过时的 KnownNetworks 是同一份数据的另一个视图（清一个，另一个也空）。
        // 这里仍然显式清空，不依赖这个实现细节。
        options.KnownNetworks.Clear();
#pragma warning restore ASPDEPR005
        foreach (var proxy in settings.KnownProxies) options.KnownProxies.Add(proxy);
        foreach (var network in settings.KnownNetworks) options.KnownIPNetworks.Add(network);

        logger.LogInformation(
            "ForwardedHeaders enabled: knownProxies={KnownProxyCount} knownNetworks={KnownNetworkCount} forwardLimit={ForwardLimit}",
            settings.KnownProxies.Count, settings.KnownNetworks.Count, settings.ForwardLimit);

        // UseForwardedHeaders(options) 把这个对象原样交给中间件，不读取 DI 中的 IOptions<ForwardedHeadersOptions>。
        // 框架中间件对没有对端地址的连接（Unix 套接字、命名管道等）不做受信检查，会直接采用最右一项；
        // 这样的对端不是“已配置的受信代理”，所以只有对端地址存在时才进入框架中间件。
        return app.UseWhen(
            context => context.Connection.RemoteIpAddress is not null,
            branch => branch.UseForwardedHeaders(options));
    }

    /// <summary>
    /// 从配置解析受信转发头设置。空白项视为未填写（Compose 变量未设置时环境变量绑定会产生空字符串项）。
    /// </summary>
    /// <param name="configuration">应用配置。</param>
    /// <returns>解析结果。</returns>
    /// <exception cref="InvalidOperationException">某一项无法解析，或跳数超出 1–5。错误信息只含配置键名。</exception>
    public static ForwardedHeadersSettings Parse(IConfiguration configuration)
    {
        var proxies = new List<IPAddress>();
        foreach (var (key, value) in Entries(configuration, KnownProxiesKey))
        {
            if (!IPAddress.TryParse(value, out var address))
                throw new InvalidOperationException($"配置无效：{key} 不是有效的 IP 地址。");
            proxies.Add(address);
        }

        var networks = new List<System.Net.IPNetwork>();
        foreach (var (key, value) in Entries(configuration, KnownNetworksKey))
        {
            if (!System.Net.IPNetwork.TryParse(value, out var network))
                throw new InvalidOperationException($"配置无效：{key} 不是有效的 CIDR 网段（写法为“网络地址/前缀长度”）。");
            // 前缀长度为 0 的网段（0.0.0.0/0、::/0）等于信任所有来源，任何客户端都能伪造自己的地址。
            if (network.PrefixLength == 0)
                throw new InvalidOperationException($"配置无效：{key} 的前缀长度不能为 0（那会信任所有来源）。");
            networks.Add(network);
        }

        return new ForwardedHeadersSettings(proxies, networks, ParseForwardLimit(configuration));
    }

    private static IEnumerable<(string Key, string Value)> Entries(IConfiguration configuration, string listKey)
    {
        var section = configuration.GetSection(listKey);
        // 写成单个值（没有 __0、__1 下标）时配置绑定不会把它当作列表项；直接报错，避免以为已经生效。
        if (!string.IsNullOrWhiteSpace(section.Value))
            throw new InvalidOperationException($"配置无效：{listKey} 必须是列表（用下标 0、1… 逐项填写）。");

        foreach (var child in section.GetChildren())
        {
            if (string.IsNullOrWhiteSpace(child.Value)) continue;
            yield return (child.Path, child.Value.Trim());
        }
    }

    private static int ParseForwardLimit(IConfiguration configuration)
    {
        var raw = configuration[ForwardLimitKey];
        if (string.IsNullOrWhiteSpace(raw)) return DefaultForwardLimit;
        if (!int.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var limit)
            || limit < MinForwardLimit || limit > MaxForwardLimit)
        {
            throw new InvalidOperationException(
                $"配置无效：{ForwardLimitKey} 必须是 {MinForwardLimit}–{MaxForwardLimit} 之间的整数。");
        }

        return limit;
    }
}
