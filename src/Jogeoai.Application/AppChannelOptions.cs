namespace Jogeoai.Application;

/// <summary>
/// App 渠道的开放范围（配置项 Collector:AppTenantIds）。不在名单里的租户看不到 App 目录行和站点目标行，保存 App 目标被拒绝；
/// 名单为空或缺失时对所有租户如此。
/// </summary>
public sealed class AppChannelOptions
{
    /// <summary>配置键：租户标识（GUID 字符串）数组，形态与 Collector:ChinaTenantIds 相同。</summary>
    public const string TenantIdsKey = "Collector:AppTenantIds";

    /// <summary>已开放 App 渠道的租户标识；默认为空，即不对任何租户开放。无法解析为 GUID 的条目被忽略。</summary>
    public string[] AppTenantIds { get; set; } = [];

    /// <summary>租户是否已开放 App 渠道：名单为空、缺失或不含该租户时为 false。</summary>
    /// <param name="tenantId">租户标识。</param>
    public bool IsOpen(Guid tenantId)
        => tenantId != Guid.Empty && AppTenantIds is { Length: > 0 } ids
            && ids.Any(value => Guid.TryParse(value, out var allowed) && allowed == tenantId);

    /// <summary>已开放 App 渠道的全部租户标识（去重；无法解析为 GUID 的条目与全零 GUID 被忽略）；名单为空或缺失时为空列表。</summary>
    public IReadOnlyList<Guid> OpenTenantIds()
        => (AppTenantIds ?? []).Select(value => Guid.TryParse(value, out var tenant) ? tenant : Guid.Empty)
            .Where(tenant => tenant != Guid.Empty).Distinct().ToArray();
}
