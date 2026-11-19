namespace Jogeoai.Domain;

/// <summary>
/// 哪些 Provider 可以参与定时监控。已注册的 API 回答 Provider（<see cref="AnswerProviderDefinition.All"/>）走主计划与凌晨计划；
/// App 渠道（<see cref="AppChannelDefinition.Provider"/>）走每周的 App 计划。自研国内网页采集（domestic_collector）不参与。
/// </summary>
public static class MonitoringProviders
{
    /// <summary>Provider 是否为 App 渠道（忽略首尾空白与大小写，与 <see cref="AnswerProviderDefinition.TryGet"/> 一致）。</summary>
    /// <param name="provider">Provider 标识。</param>
    public static bool IsApp(string? provider)
    {
        var normalized = provider?.Trim();
        return !string.IsNullOrEmpty(normalized)
            && string.Equals(normalized, AppChannelDefinition.Provider, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Provider 的目标能否参与定时监控：已注册的 API 回答 Provider，或 App 渠道。</summary>
    /// <param name="provider">Provider 标识。</param>
    public static bool IsEligible(string? provider) => AnswerProviderDefinition.IsRegistered(provider) || IsApp(provider);

    /// <summary>自研国内网页采集的 Provider 标识；不参与定时监控。</summary>
    public const string DomesticWebProvider = "domestic_collector";

    /// <summary>
    /// 由 Provider 推导采集渠道（全部接口一致）：app_collector 为 App，domestic_collector 为网页，其余（Cloro 与各 API Provider）为 API。
    /// 不能只看采集模式：国内网页回答的采集模式存的是 scraped，与 Cloro 相同。
    /// </summary>
    /// <param name="provider">Provider 标识（忽略首尾空白与大小写）。</param>
    public static CollectionChannel Channel(string? provider)
    {
        if (IsApp(provider)) return CollectionChannel.App;
        return string.Equals(provider?.Trim(), DomesticWebProvider, StringComparison.OrdinalIgnoreCase)
            ? CollectionChannel.Web
            : CollectionChannel.Api;
    }
}
