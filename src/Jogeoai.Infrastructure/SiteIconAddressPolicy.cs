using System.Net;
using System.Net.Sockets;
using Jogeoai.Application;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jogeoai.Infrastructure;

/// <summary>
/// 站点图标出站地址的放行规则：默认与 <see cref="ArticleSourceReader.IsPublicAddress"/> 一致；
/// 仅开发环境且配置 <see cref="SiteIconOptions.AllowFakeIpRange"/> 时额外放行 Clash/Surge fake-ip 网段 198.18.0.0/15。
/// DNS 预检（<see cref="PublicSiteIconHostGuard"/>）与 <c>site-icon</c> 客户端的连接回调共用同一实例。
/// </summary>
public sealed class SiteIconAddressPolicy(bool allowFakeIpRange)
{
    /// <summary>是否实际放行 198.18.0.0/15（已结合运行环境计算）。</summary>
    public bool AllowFakeIpRange { get; } = allowFakeIpRange;

    /// <summary>地址是否允许作为站点图标的出站目标。</summary>
    public bool IsAllowed(IPAddress address) =>
        ArticleSourceReader.IsPublicAddress(address) || AllowFakeIpRange && IsFakeIpAddress(address);

    /// <summary>地址是否属于 198.18.0.0/15（含 IPv4 映射的 IPv6 形式）。</summary>
    public static bool IsFakeIpAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 198 && bytes[1] is 18 or 19;
    }

    /// <summary>
    /// 计算开关是否生效：仅 <see cref="IHostEnvironment.IsDevelopment"/> 时才采用配置值；
    /// 非开发环境配置为 true 时记录警告并按 false 处理。
    /// </summary>
    public static SiteIconAddressPolicy Create(SiteIconOptions options, IHostEnvironment environment, ILogger logger)
    {
        if (!options.AllowFakeIpRange) return new SiteIconAddressPolicy(false);
        if (environment.IsDevelopment())
        {
            logger.LogInformation("SiteIcons:AllowFakeIpRange is enabled in Development; site icon fetches may connect to 198.18.0.0/15.");
            return new SiteIconAddressPolicy(true);
        }
        logger.LogWarning("SiteIcons:AllowFakeIpRange is ignored outside Development (environment {Environment}).", environment.EnvironmentName);
        return new SiteIconAddressPolicy(false);
    }
}
