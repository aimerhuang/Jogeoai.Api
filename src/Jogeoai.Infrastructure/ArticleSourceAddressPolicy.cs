using System.Net;
using Jogeoai.Application;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jogeoai.Infrastructure;

/// <summary>
/// 网页读取出站地址的放行规则：默认与 <see cref="ArticleSourceReader.IsPublicAddress"/> 一致；
/// 仅开发环境且配置 <see cref="ArticleSourceOptions.AllowFakeIpRange"/> 时额外放行 Clash/Surge fake-ip 网段 198.18.0.0/15
/// （范围判断复用 <see cref="SiteIconAddressPolicy.IsFakeIpAddress"/>）。
/// <see cref="ArticleSourceReader"/> 的 DNS 预检与 <c>research-web</c> 客户端的连接回调共用同一实例。
/// </summary>
public sealed class ArticleSourceAddressPolicy(bool allowFakeIpRange)
{
    /// <summary>只放行公网地址的默认规则（未注入策略的调用方与测试使用）。</summary>
    public static ArticleSourceAddressPolicy PublicOnly { get; } = new(false);

    /// <summary>是否实际放行 198.18.0.0/15（已结合运行环境计算）。</summary>
    public bool AllowFakeIpRange { get; } = allowFakeIpRange;

    /// <summary>地址是否允许作为网页读取的出站目标。</summary>
    public bool IsAllowed(IPAddress address) =>
        ArticleSourceReader.IsPublicAddress(address) || AllowFakeIpRange && SiteIconAddressPolicy.IsFakeIpAddress(address);

    /// <summary>
    /// 计算开关是否生效：仅 <see cref="IHostEnvironment.IsDevelopment"/> 时才采用配置值；
    /// 非开发环境配置为 true 时记录警告并按 false 处理。
    /// </summary>
    public static ArticleSourceAddressPolicy Create(ArticleSourceOptions options, IHostEnvironment environment, ILogger logger)
    {
        if (!options.AllowFakeIpRange) return PublicOnly;
        if (environment.IsDevelopment())
        {
            logger.LogInformation("ArticleSource:AllowFakeIpRange is enabled in Development; web page reads may connect to 198.18.0.0/15.");
            return new ArticleSourceAddressPolicy(true);
        }
        logger.LogWarning("ArticleSource:AllowFakeIpRange is ignored outside Development (environment {Environment}).", environment.EnvironmentName);
        return PublicOnly;
    }
}
