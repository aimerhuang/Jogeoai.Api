using Jogeoai.Domain;

namespace Jogeoai.Application;

/// <summary>全局站点图标缓存（表 <c>site_icons</c>，迁移 028、029）。</summary>
public interface ISiteIconStore
{
    /// <summary>读取规范化域名的缓存记录（不论是否过期）；没有记录时返回 null。</summary>
    Task<SiteIconRecord?> GetAsync(string domain, CancellationToken cancellationToken = default);

    /// <summary>按域名插入或覆盖缓存记录。</summary>
    Task SaveAsync(SiteIconRecord record, CancellationToken cancellationToken = default);
}

/// <summary>站点图标获取服务：缓存 → 首页 HTML 声明 → /favicon.ico →（可注册域名）www 子域名 → 第三方兜底 →（子域名）上级域名。</summary>
public interface ISiteIconService
{
    /// <summary>
    /// 返回域名的图标记录：found 带字节，missing 表示拿不到。
    /// 域名不合法时抛出 <see cref="CollectorStoreException"/>（INVALID_SITE_DOMAIN，400），且不发出出站请求。
    /// </summary>
    Task<SiteIconRecord> GetAsync(string domain, CancellationToken cancellationToken = default);
}

/// <summary>站点图标抓取与缓存参数（配置节 <c>SiteIcons</c>）。</summary>
public sealed class SiteIconOptions
{
    /// <summary>默认第三方服务顺序。</summary>
    public static readonly IReadOnlyList<string> DefaultProviders = ["google", "duckduckgo", "yandex"];

    /// <summary>第三方图标服务兜底设置。</summary>
    public SiteIconThirdPartyOptions ThirdParty { get; set; } = new();

    /// <summary>found 记录缓存天数，默认 30。</summary>
    public int FoundTtlDays { get; set; } = 30;

    /// <summary>明确拿不到图标的 missing 记录缓存天数，默认 7。</summary>
    public int MissingTtlDays { get; set; } = 7;

    /// <summary>仅因超时或网络错误失败时 missing 记录（或旧图标顺延）的缓存小时数，默认 24。</summary>
    public int TransientMissingTtlHours { get; set; } = 24;

    /// <summary>单个图标字节上限，默认 102400（100KB），不超过 1MiB。</summary>
    public int MaxIconBytes { get; set; } = 102_400;

    /// <summary>单次 HTTP 请求（含读取响应体）超时秒数，默认 5。</summary>
    public int RequestTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// 一次抓取链路（单个域名，不含上级域名兜底）的总预算秒数，默认 25。
    /// 启用第三方时为其保留 RequestTimeoutSeconds × min(2, 服务数)，站点自身步骤（含 www 兜底）只用剩余部分。
    /// </summary>
    public int TotalBudgetSeconds { get; set; } = 25;

    /// <summary>全进程同时进行的抓取数，默认 8。</summary>
    public int MaxConcurrency { get; set; } = 8;

    /// <summary>
    /// 仅开发环境：是否额外放行 Clash/Surge fake-ip 网段 198.18.0.0/15（本机 TUN fake-ip 模式下域名都会解析到该网段），默认 false。
    /// 只影响站点图标出站校验，其他私网、回环、元数据地址仍拒绝；非开发环境配置为 true 时启动记录警告并按 false 处理。
    /// </summary>
    public bool AllowFakeIpRange { get; set; }

    /// <summary>
    /// 域名别名：键为引用中出现的域名（同时匹配其子域名），值为实际取图标的域名，例如 <c>iesdouyin.com → douyin.com</c>。
    /// 只做一跳，按值域名读写缓存；键或值不合法的条目在启动时忽略并记录警告。
    /// </summary>
    public Dictionary<string, string> DomainAliases { get; set; } = [];
}

/// <summary>第三方图标服务兜底设置（配置节 <c>SiteIcons:ThirdParty</c>）。</summary>
public sealed class SiteIconThirdPartyOptions
{
    /// <summary>是否启用第三方兜底，默认 true。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 依次尝试的服务（google、duckduckgo、yandex）；未配置时用默认顺序。未知名称忽略，重复名称只取第一次。
    /// </summary>
    public string[]? Providers { get; set; }

    /// <summary>生效的服务顺序：已启用时为去重后的已知服务，未启用时为空。</summary>
    public IReadOnlyList<string> EffectiveProviders()
    {
        if (!Enabled) return [];
        return (Providers ?? [.. SiteIconOptions.DefaultProviders])
            .Select(provider => provider?.Trim().ToLowerInvariant())
            .Where(provider => provider is "google" or "duckduckgo" or "yandex")
            .Distinct()
            .ToArray()!;
    }
}
