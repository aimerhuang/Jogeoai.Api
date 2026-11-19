using Microsoft.Extensions.DependencyInjection;

namespace Jogeoai.Providers.Domestic;

/// <summary>注册 PackyAPI（千问、智谱清言、Kimi）回答 Provider。</summary>
public static class PackyServiceCollectionExtensions
{
    /// <summary>注册命名 HttpClient <c>packy</c>（无限超时，由适配器按设置的 timeout_seconds 控制）和 <see cref="IPackyAnswerClient"/>；宿主需另行注册 IAnswerProviderSettingsStore。</summary>
    /// <param name="services">应用服务集合。</param>
    /// <returns>同一个服务集合。</returns>
    public static IServiceCollection AddPackyProvider(this IServiceCollection services)
    {
        // The per-provider timeout is persisted in MySQL (5–600 seconds) and enforced by the adapter with a linked
        // cancellation token. x-api-key (Kimi) is a custom header that HttpClient would replay to any redirect target,
        // so redirects are not followed; a 3xx surfaces as PACKY_REQUEST_FAILED.
        services.AddHttpClient(PackyDefaults.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddTransient<IPackyAnswerClient, PackyAnswerAdapter>();
        return services;
    }
}
