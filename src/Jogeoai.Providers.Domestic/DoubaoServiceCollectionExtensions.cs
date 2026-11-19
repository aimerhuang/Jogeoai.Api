using Microsoft.Extensions.DependencyInjection;

namespace Jogeoai.Providers.Domestic;

/// <summary>注册豆包（火山方舟）回答 Provider。</summary>
public static class DoubaoServiceCollectionExtensions
{
    /// <summary>注册命名 HttpClient <c>doubao</c>（无限超时，由适配器按设置的 timeout_seconds 控制）和 <see cref="IDoubaoAnswerClient"/>；宿主需另行注册 IAnswerProviderSettingsStore。</summary>
    /// <param name="services">应用服务集合。</param>
    /// <returns>同一个服务集合。</returns>
    public static IServiceCollection AddDoubaoProvider(this IServiceCollection services)
    {
        // The per-provider timeout is persisted in MySQL (5–600 seconds) and enforced by the adapter with a linked
        // cancellation token; a shorter HttpClient-wide timeout would silently cap valid database settings.
        services.AddHttpClient(DoubaoDefaults.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddTransient<IDoubaoAnswerClient, DoubaoArkAdapter>();
        return services;
    }
}
