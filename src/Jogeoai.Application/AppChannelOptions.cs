using Jogeoai.Domain;

namespace Jogeoai.Application;

/// <summary>
/// App 渠道的开放范围（配置项 Collector:AppTenantIds）。不在名单里的租户看不到 App 目录行和站点目标行，保存 App 目标被拒绝；
/// 名单为空或缺失时对所有租户如此。
/// </summary>
public sealed class AppChannelOptions
{
    /// <summary>配置键：租户标识（GUID 字符串）数组，形态与 Collector:ChinaTenantIds 相同。</summary>
    public const string TenantIdsKey = "Collector:AppTenantIds";

    /// <summary>配置键：一次 App 租约从领取起最多可以续到多少秒（900–86400，默认 1800）。只有 API 进程读取它。</summary>
    public const string MaxLeaseTotalSecondsKey = "AppChannel:MaxLeaseTotalSeconds";

    private static readonly char[] Separators = [',', ';', ' ', '\t', '\r', '\n'];

    /// <summary>
    /// 一次 App 租约从领取起最多可以续到多少秒；心跳不能把租约延长到这之后，也不能超过任务的截止时点。默认 1800。
    /// 取值不在 900–86400 之间时按默认值处理（启动时 <see cref="ParseMaxLeaseTotalSeconds"/> 已经拒绝这样的配置）。
    /// </summary>
    public int MaxLeaseTotalSeconds { get; set; } = AppPoolRules.DefaultMaxLeaseTotalSeconds;

    /// <summary>实际生效的租约总时长上限：<see cref="MaxLeaseTotalSeconds"/> 在 900–86400 之内时取它，否则取默认值 1800。</summary>
    public int EffectiveMaxLeaseTotalSeconds()
        => MaxLeaseTotalSeconds is >= AppPoolRules.MaxLeaseSeconds and <= AppPoolRules.MaxLeaseTotalSecondsLimit
            ? MaxLeaseTotalSeconds
            : AppPoolRules.DefaultMaxLeaseTotalSeconds;

    /// <summary>
    /// 解析配置项 <see cref="MaxLeaseTotalSecondsKey"/>：缺失或为空取默认值 1800；必须是 900–86400 之间的整数，否则抛出异常让进程启动失败。
    /// 下限是单次租约的上限（900 秒），这样领取时给出的租约不会超过总时长上限。异常信息只含配置键名。
    /// </summary>
    /// <param name="raw">配置里的原始值。</param>
    public static int ParseMaxLeaseTotalSeconds(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return AppPoolRules.DefaultMaxLeaseTotalSeconds;
        if (!int.TryParse(raw.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            || seconds < AppPoolRules.MaxLeaseSeconds || seconds > AppPoolRules.MaxLeaseTotalSecondsLimit)
            throw new InvalidOperationException(
                $"配置无效：{MaxLeaseTotalSecondsKey} 必须是 {AppPoolRules.MaxLeaseSeconds}–{AppPoolRules.MaxLeaseTotalSecondsLimit} 之间的整数。");
        return seconds;
    }

    /// <summary>
    /// 已开放 App 渠道的租户标识；默认为空，即不对任何租户开放。一项里可以用逗号、分号或空白分隔多个 GUID
    /// （部署时 <c>COLLECTOR_APP_TENANT_IDS</c> 以一个变量传入多个租户）。无法解析为 GUID 的部分被忽略。
    /// </summary>
    public string[] AppTenantIds { get; set; } = [];

    /// <summary>租户是否已开放 App 渠道：名单为空、缺失或不含该租户时为 false。</summary>
    /// <param name="tenantId">租户标识。</param>
    public bool IsOpen(Guid tenantId) => tenantId != Guid.Empty && OpenTenantIds().Contains(tenantId);

    /// <summary>已开放 App 渠道的全部租户标识（拆分各项、去重；无法解析为 GUID 的部分与全零 GUID 被忽略）；名单为空或缺失时为空列表。</summary>
    public IReadOnlyList<Guid> OpenTenantIds()
        => (AppTenantIds ?? []).Where(value => value is not null)
            .SelectMany(value => value.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(value => Guid.TryParse(value, out var tenant) ? tenant : Guid.Empty)
            .Where(tenant => tenant != Guid.Empty).Distinct().ToArray();
}
