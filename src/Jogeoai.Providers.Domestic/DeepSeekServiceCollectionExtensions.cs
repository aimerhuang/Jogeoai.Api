using Microsoft.Extensions.DependencyInjection;

namespace Jogeoai.Providers.Domestic;

/// <summary>注册 DeepSeek（官方 Anthropic 兼容 Messages API）回答 Provider。</summary>
public static class DeepSeekServiceCollectionExtensions
{
    /// <summary>注册命名 HttpClient <c>deepseek</c>（无限超时，由适配器按设置的 timeout_seconds 控制）和 <see cref="IDeepSeekAnswerClient"/>；宿主需另行注册 IAnswerProviderSettingsStore。</summary>
    /// <param name="services">应用服务集合。</param>
    /// <returns>同一个服务集合。</returns>
    public static IServiceCollection AddDeepSeekProvider(this IServiceCollection services)
    {
        // The per-provider timeout is persisted in MySQL (5–600 seconds) and enforced by the adapter with a linked
        // cancellation token; a shorter HttpClient-wide timeout would silently cap valid database settings.
        // x-api-key is a custom header: unlike Authorization, HttpClient would replay it to any redirect target
        // (including another host), so redirects are not followed; a 3xx surfaces as DEEPSEEK_REQUEST_FAILED.
        services.AddHttpClient(DeepSeekDefaults.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddTransient<IDeepSeekAnswerClient, DeepSeekAnthropicAdapter>();
        return services;
    }
}
