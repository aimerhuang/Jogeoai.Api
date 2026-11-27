using Jogeoai.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Jogeoai.Infrastructure;

/// <summary>注册品牌研究的入队额度配置。</summary>
public static class ResearchServiceCollectionExtensions
{
    /// <summary>
    /// 绑定配置节 <c>Research:Quota</c>（<see cref="ResearchQuotaOptions"/>）并在宿主启动时校验：配置节缺失时使用默认值，
    /// 任一数值小于 1 时启动失败，不静默修正。只有发起入队的 API 进程需要调用；Worker 不入队，使用默认值即可。
    /// </summary>
    /// <param name="services">依赖注入服务集合。</param>
    /// <param name="configuration">应用配置。</param>
    /// <returns>原服务集合。</returns>
    public static IServiceCollection AddResearchQuota(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ResearchQuotaOptions>()
            .Bind(configuration.GetSection(ResearchQuotaOptions.SectionName))
            .Validate(options => options.IsValid(),
                "Research:Quota requires MaxActivePerSite >= 1 and DailyPerTenant >= 1.")
            .ValidateOnStart();
        return services;
    }
}
