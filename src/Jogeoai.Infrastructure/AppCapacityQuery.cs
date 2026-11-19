using System.Globalization;
using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.Extensions.Options;
using MySqlConnector;
using static Jogeoai.Infrastructure.DomesticCollectorStore;

namespace Jogeoai.Infrastructure;

/// <summary>
/// App 各平台容量的数据读取（design 6.5）。计算在 <see cref="AppCapacityRules"/>；这里只把绑定、平台闸门、App 入口、实测成功次数和
/// 开放租户的需求读出来。预估在自己的只读事务里调用 <see cref="ReadAsync"/>；运营视图与每日汇报（阶段 8B）调用
/// <see cref="GetPlatformCapacitiesAsync"/>。只读，不加锁。
/// </summary>
/// <param name="database">业务数据库。</param>
/// <param name="appChannel">App 渠道的开放范围（Collector:AppTenantIds）；不提供时没有开放的租户，周需求都是 0。</param>
public sealed class AppCapacityQuery(MySqlDatabase database, IOptions<AppChannelOptions>? appChannel = null) : IAppCapacityQuery
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<AppPlatformCapacity>> GetPlatformCapacitiesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await AppChannelSchema.RequireAsync(connection, null, cancellationToken);
        var snapshot = await ReadAsync(connection, null, appChannel?.Value.OpenTenantIds() ?? [], cancellationToken);
        return AppChannelDefinition.Engines.Select(engine => AppCapacityRules.Compute(snapshot.Platforms[engine])).ToArray();
    }

    /// <summary>One brand's demand on one platform: its enabled prompts that pass the App check and are in scope of the platform.</summary>
    internal sealed record BrandDemand(Guid TenantId, Guid SiteId, long Demand);

    /// <summary>
    /// Everything the capacity of every platform is computed from. <see cref="Platforms"/> carries the stored total demand;
    /// <see cref="ForBrand"/> recomputes it with one brand's demand replaced (the estimate's draft).
    /// </summary>
    internal sealed record Snapshot(IReadOnlyDictionary<string, AppPlatformCapacityInput> Platforms,
        IReadOnlyDictionary<string, IReadOnlyList<BrandDemand>> Demands)
    {
        internal AppBrandCapacityShare ForBrand(string platform, Guid tenantId, Guid siteId, long brandDemand)
        {
            var others = Demands.GetValueOrDefault(platform, [])
                .Where(demand => demand.TenantId != tenantId || demand.SiteId != siteId).Select(demand => demand.Demand).ToArray();
            return AppCapacityRules.ForBrand(Platforms[platform], others, brandDemand);
        }
    }

    /// <summary>
    /// Reads the inputs of every known platform (049 must be applied) and assembles them with <see cref="AppCapacityRules.Inputs"/>.
    /// Bindings: every binding with its own status and its node's status (the rules count active bindings on active nodes only,
    /// for the rate limit and for the device time). Task durations: per accepted App result whose accepted row falls in the last
    /// 14 Asia/Shanghai days including today, the time from the 'leased' row of the same task and lease version to the
    /// 'accepted' row; results without such a leased row are not samples. Demand: brands of the open tenants with an enabled
    /// App target on the platform (active brands only), their enabled prompts that pass the App check, are not excluded by an
    /// operator and whose scope covers the platform.
    /// </summary>
    internal static async Task<Snapshot> ReadAsync(MySqlConnection c, MySqlTransaction? t, IReadOnlyList<Guid> openTenants, CancellationToken ct)
    {
        var demands = await DemandsAsync(c, t, openTenants, ct);
        var bindings = (await QueryRowsAsync(c, t, $"""
                SELECT b.node_id, b.device_id, b.platform_code,
                       b.status = 'active' AND b.last_seen_at > UTC_TIMESTAMP(6) - INTERVAL {AppPoolRules.BindingOfflineMinutes} MINUTE
                         AND (b.cooldown_until IS NULL OR b.cooldown_until <= UTC_TIMESTAMP(6)), n.status = 'active'
                FROM collector_app_bindings b JOIN collector_app_nodes n ON n.node_id = b.node_id;
                """, ct))
            .Select(row => new AppPoolBinding(row[0], row[1], row[2], AppPoolRules.DeviceCooldownSeconds, null, Flag(row[3]), Flag(row[4])))
            .ToArray();
        var entries = (await QueryRowsAsync(c, t, """
                SELECT DISTINCT e.platform_code FROM collector_platform_entries e
                LEFT JOIN collector_app_platform_states p ON p.platform_code = e.platform_code
                WHERE e.channel = 'consumer_app' AND e.region = 'cn' AND e.enabled = TRUE AND p.paused_at IS NULL;
                """, ct)).Select(row => row[0]).ToHashSet(StringComparer.Ordinal);
        // Asia/Shanghai days, the same fixed offset as the platform daily cap (CollectorSessionDispatch.ShanghaiDayStartSql).
        // The accepted rows are found through the bindings (ix_collector_attempts_app_binding_quota); the leased row of the same
        // task and lease version through ix_collector_attempts_task_created.
        var durations = (await QueryRowsAsync(c, t, $"""
                SELECT b.platform_code,
                       TIMESTAMPDIFF(MICROSECOND,
                           (SELECT MIN(l.created_at) FROM collector_attempts l
                            WHERE l.collector_task_id = a.collector_task_id AND l.lease_version = a.lease_version AND l.event_kind = 'leased'),
                           a.created_at)
                FROM collector_app_bindings b
                JOIN collector_attempts a ON a.app_binding_id = b.id AND a.event_kind = 'accepted'
                    AND a.created_at >= {CollectorSessionDispatch.ShanghaiDayStartSql} - INTERVAL {AppCapacityRules.MeasurementWindowDays - 1} DAY;
                """, ct))
            .Where(row => row[1].Length > 0)
            .GroupBy(row => row[0], StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => (IReadOnlyList<double>)group.Select(row => long.Parse(row[1], CultureInfo.InvariantCulture) / 1_000_000d).ToArray(),
                StringComparer.Ordinal);
        var inputs = AppCapacityRules.Inputs(AppChannelDefinition.Engines, bindings, new Dictionary<string, (int, int?)>(), entries, durations,
            demands.ToDictionary(pair => pair.Key, pair => pair.Value.Sum(demand => demand.Demand), StringComparer.Ordinal));
        return new Snapshot(inputs.ToDictionary(input => input.Platform, StringComparer.Ordinal),
            demands.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<BrandDemand>)pair.Value, StringComparer.Ordinal));
    }

    private static async Task<Dictionary<string, List<BrandDemand>>> DemandsAsync(MySqlConnection c, MySqlTransaction? t,
        IReadOnlyList<Guid> openTenants, CancellationToken ct)
    {
        var result = new Dictionary<string, List<BrandDemand>>(StringComparer.Ordinal);
        if (openTenants.Count == 0) return result;
        var tenants = string.Join(",", openTenants.Select((_, i) => $"UUID_TO_BIN(@tenant{i})"));
        object[] parameters = [.. openTenants.Select((tenant, i) => (object)($"@tenant{i}", tenant.ToString())), ("@provider", AppChannelDefinition.Provider)];
        var targets = await QueryRowsAsync(c, t, $"""
            SELECT BIN_TO_UUID(st.tenant_id), BIN_TO_UUID(st.site_id), st.engine FROM site_targets st
            JOIN sites s ON s.id = st.site_id AND s.tenant_id = st.tenant_id
            WHERE st.tenant_id IN ({tenants}) AND st.provider = @provider AND st.enabled = TRUE AND s.status = 'active'
            ORDER BY st.tenant_id, st.site_id, st.engine;
            """, ct, parameters);
        foreach (var brand in targets.Where(row => AppChannelDefinition.SupportsEngine(row[2]))
                     .GroupBy(row => (Tenant: Guid.Parse(row[0]), Site: Guid.Parse(row[1]))))
        {
            var prompts = await AppMonitoringEstimate.BrandPromptsAsync(c, t, brand.Key.Tenant, brand.Key.Site, ct);
            foreach (var engine in brand.Select(row => row[2]).Distinct(StringComparer.Ordinal))
            {
                if (!result.TryGetValue(engine, out var list)) result[engine] = list = [];
                list.Add(new BrandDemand(brand.Key.Tenant, brand.Key.Site, AppMonitoringEstimate.Demand(prompts, engine)));
            }
        }
        return result;
    }

    private static int Int(string value) => int.Parse(value, CultureInfo.InvariantCulture);

    private static bool Flag(string value) => value is "1" or "True" or "true";
}
