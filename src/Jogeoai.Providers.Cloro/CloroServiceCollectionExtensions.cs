using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Jogeoai.Providers.Abstractions;

namespace Jogeoai.Providers.Cloro;

/// <summary>注册 Cloro Provider 及其可恢复任务客户端。</summary>
public static class CloroServiceCollectionExtensions
{
    /// <summary>将 Cloro 适配器、任务客户端、余额客户端和 HTTP 客户端加入依赖注入容器；宿主需另行注册 ICloroKeyPool。</summary>
    /// <param name="services">应用服务集合。</param>
    /// <param name="configure">可选的适配器轮询配置。</param>
    /// <returns>同一个服务集合。</returns>
    public static IServiceCollection AddCloroAdapter(this IServiceCollection services, Action<CloroAdapterOptions>? configure = null)
    {
        if (configure is not null) services.Configure(configure);
        else services.AddOptions<CloroAdapterOptions>();
        services.AddOptions<CloroKeyPoolOptions>();

        services.AddHttpClient<CloroAdapter>(client =>
        {
            // The async task can take minutes; the adapter owns the total timeout.
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        services.AddTransient<ICloroAdapter>(serviceProvider => serviceProvider.GetRequiredService<CloroAdapter>());
        services.AddTransient<ICloroTaskClient>(serviceProvider => serviceProvider.GetRequiredService<CloroAdapter>());
        services.AddTransient<ICloroCreditsClient>(serviceProvider => serviceProvider.GetRequiredService<CloroAdapter>());
        services.AddTransient<CloroKeyCreditsRefresher>();
        return services;
    }
}
