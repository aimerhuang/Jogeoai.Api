namespace Jogeoai.Domain;

/// <summary>一个计入容量的 App 绑定的限速参数（绑定为 active，所在节点也为 active）。</summary>
/// <param name="MinIntervalSeconds">该绑定两次领取的最短间隔秒数（collector_app_bindings.min_interval_seconds，默认 100，迁移 050）。</param>
/// <param name="DailyQuota">运营设置的每日次数上限；为空表示不设上限（默认）。</param>
public sealed record AppBindingRate(int MinIntervalSeconds, int? DailyQuota = null);

/// <summary>账号池里的一个 App 绑定（设备 × App × 账号），容量计算用到的部分。</summary>
/// <param name="NodeId">采集节点标识。</param>
/// <param name="DeviceId">设备标识（AVD 名称），在节点内唯一；设备 = 节点 × 设备标识。</param>
/// <param name="Platform">平台代码。</param>
/// <param name="MinIntervalSeconds">该绑定两次领取的最短间隔秒数。</param>
/// <param name="DailyQuota">运营设置的每日次数上限；为空表示不设。</param>
/// <param name="Active">绑定是否为 active（未停止）。</param>
/// <param name="NodeActive">所在节点是否为 active（未停用）。</param>
public sealed record AppPoolBinding(string NodeId, string DeviceId, string Platform, int MinIntervalSeconds, int? DailyQuota = null,
    bool Active = true, bool NodeActive = true);

/// <summary>计算一个 App 平台周容量所需的输入（design 6.5），由数据读取层组装，计算本身不访问数据库。</summary>
/// <param name="Platform">平台代码。</param>
/// <param name="Available">平台的 App 入口是否启用；未启用时平台不会展开任务，周容量按 0 计。</param>
/// <param name="ActiveBindings">全部节点上计入容量的绑定（绑定为 active，所在节点为 active）。</param>
/// <param name="PlatformIntervalSeconds">平台全部绑定合计的最短领取间隔秒数（collector_app_platform_states.min_interval_seconds，默认 30）。</param>
/// <param name="PlatformDailyCap">运营设置的平台每日上限；为空表示不设上限（默认）。</param>
/// <param name="DeviceSecondsPerWeek">各设备每周分给该平台的秒数之和（<see cref="AppCapacityRules.DeviceSecondsByPlatform"/>）。</param>
/// <param name="TaskDurationSeconds">最近 <see cref="AppCapacityRules.MeasurementWindowDays"/> 天里该平台成功任务的耗时（秒，同一任务同一租约版本 leased 到 accepted）。</param>
/// <param name="WeeklyDemand">周需求：全部开放租户里启用了该平台 App 目标的品牌的“通过检查的启用提问数”之和。</param>
public sealed record AppPlatformCapacityInput(string Platform, bool Available, IReadOnlyList<AppBindingRate> ActiveBindings,
    int PlatformIntervalSeconds, int? PlatformDailyCap, decimal DeviceSecondsPerWeek, IReadOnlyList<double> TaskDurationSeconds, long WeeklyDemand);

/// <summary>一个 App 平台的周容量视图（design 6.5）。是估算，不用于拒绝保存；真正的保证来自领取时的闸门。</summary>
/// <param name="Platform">平台代码。</param>
/// <param name="Available">平台的 App 入口是否启用。</param>
/// <param name="ActiveBindings">计入容量的绑定数。</param>
/// <param name="RateLimitWeekly">设备每周可分配给该平台的秒数除以 100 秒，向下取整。旧绑定/平台间隔与配额不参与。</param>
/// <param name="MeasuredWeekly">实测产能（每周）：⌊各设备每周分给该平台的秒数之和 / 该平台成功任务耗时的中位数⌋；
/// 最近 14 天的成功任务少于 20 条时为空（不使用，只按限速上限）。</param>
/// <param name="WeeklyCapacity">周容量：入口未启用时为 0；否则为限速上限与实测产能（有时）中较小的一个。</param>
/// <param name="WeeklyDemand">周需求（全部开放租户合计）。</param>
/// <param name="ExpectedUncollected">预计排不到：max(0, 周需求 − 周容量)。</param>
/// <param name="MedianTaskSeconds">最近 14 天该平台成功任务耗时的中位数（秒）；没有样本时为空。样本少于 20 条时也给出，但不用于实测产能。</param>
/// <param name="TaskDurationSamples">最近 14 天该平台成功任务耗时的样本数。</param>
public sealed record AppPlatformCapacity(string Platform, bool Available, int ActiveBindings, long RateLimitWeekly, long? MeasuredWeekly,
    long WeeklyCapacity, long WeeklyDemand, long ExpectedUncollected, double? MedianTaskSeconds = null, int TaskDurationSamples = 0);

/// <summary>一个品牌在某个 App 平台上的容量份额：平台容量视图与该品牌预计排得到的检测数。</summary>
/// <param name="Capacity">平台的容量视图（周需求已按该品牌的需求重算）。</param>
/// <param name="Scheduled">该品牌每周预计排得到的检测数，不超过它的需求。</param>
public sealed record AppBrandCapacityShare(AppPlatformCapacity Capacity, long Scheduled);

/// <summary>
/// App 平台容量的纯计算（design 6.5），供预估（阶段 8A）和运营视图、每日汇报（阶段 8B）共用。不访问数据库，同样的输入结果恒定。
/// </summary>
public static class AppCapacityRules
{
    /// <summary>一天的秒数。</summary>
    public const int SecondsPerDay = 86_400;

    /// <summary>一周的天数（周容量 = 每日 × 7）。</summary>
    public const int DaysPerWeek = 7;

    /// <summary>一台设备每周提供的秒数（7 × 86400）。</summary>
    public const int DeviceSecondsPerWeek = DaysPerWeek * SecondsPerDay;

    /// <summary>任务耗时的统计窗口（Asia/Shanghai 自然日，含今天；按 accepted 的时间）。</summary>
    public const int MeasurementWindowDays = 14;

    /// <summary>使用实测产能所需的最少耗时样本数；不满时只按限速上限。</summary>
    public const int MinimumDurationSamples = 20;

    /// <summary>绑定是否计入容量：绑定为 active，且所在节点为 active。</summary>
    /// <param name="binding">绑定。</param>
    public static bool Counts(AppPoolBinding binding) => binding is { Active: true, NodeActive: true };

    /// <summary>
    /// 设备时间（design 6.5）：一台设备（节点 × 设备标识）同一时刻只做一条任务。每台至少有一个计入容量的绑定的设备每周提供
    /// <see cref="DeviceSecondsPerWeek"/> 秒，平均分给它有计入容量的绑定的平台。返回每个平台分到的秒数之和；没有设备的平台不在结果里。
    /// </summary>
    /// <param name="bindings">账号池的绑定（不计入容量的会被忽略）。</param>
    public static IReadOnlyDictionary<string, decimal> DeviceSecondsByPlatform(IEnumerable<AppPoolBinding> bindings)
    {
        var result = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var device in (bindings ?? []).Where(binding => binding is not null && Counts(binding))
                     .GroupBy(binding => (binding.NodeId, binding.DeviceId)))
        {
            var platforms = device.Select(binding => binding.Platform).Distinct(StringComparer.Ordinal).ToArray();
            var share = (decimal)DeviceSecondsPerWeek / platforms.Length;
            foreach (var platform in platforms) result[platform] = result.GetValueOrDefault(platform) + share;
        }
        return result;
    }

    /// <summary>中位数；样本数为偶数时取中间两个的平均值。没有样本时为空。</summary>
    /// <param name="values">样本。</param>
    public static double? Median(IReadOnlyList<double> values)
    {
        if (values is not { Count: > 0 }) return null;
        var sorted = values.Order().ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    /// <summary>
    /// 实测产能（每周）：⌊设备每周分给该平台的秒数 / 成功任务耗时的中位数⌋。样本少于 <see cref="MinimumDurationSamples"/> 条、
    /// 或中位数不大于 0 时为空（不使用）。不大于 0 的耗时不算样本。
    /// </summary>
    /// <param name="deviceSecondsPerWeek">设备每周分给该平台的秒数。</param>
    /// <param name="taskDurationSeconds">成功任务的耗时（秒）。</param>
    public static long? MeasuredWeekly(decimal deviceSecondsPerWeek, IReadOnlyList<double> taskDurationSeconds)
    {
        var samples = Samples(taskDurationSeconds);
        if (samples.Count < MinimumDurationSamples || Median(samples) is not { } median || median <= 0) return null;
        return (long)Math.Floor(Math.Max(0m, deviceSecondsPerWeek) / (decimal)median);
    }

    private static IReadOnlyList<double> Samples(IReadOnlyList<double>? durations)
        => (durations ?? []).Where(value => value > 0 && !double.IsNaN(value) && !double.IsInfinity(value)).ToArray();

    /// <summary>
    /// 由账号池的全部绑定组装各平台的输入：限速与设备时间都只看计入容量的绑定（绑定为 active、所在节点为 active）。
    /// 缺少平台闸门行的平台按列默认值（间隔 30 秒、不设每日上限）。
    /// </summary>
    /// <param name="platforms">要计算的平台，按返回顺序。</param>
    /// <param name="bindings">账号池的全部绑定。</param>
    /// <param name="gates">各平台的闸门（平台间隔秒数、每日上限）。</param>
    /// <param name="available">App 入口启用的平台。</param>
    /// <param name="taskDurations">各平台最近 14 天成功任务的耗时（秒）。</param>
    /// <param name="demand">各平台的周需求。</param>
    public static IReadOnlyList<AppPlatformCapacityInput> Inputs(IReadOnlyList<string> platforms, IEnumerable<AppPoolBinding> bindings,
        IReadOnlyDictionary<string, (int IntervalSeconds, int? DailyCap)> gates, IReadOnlySet<string> available,
        IReadOnlyDictionary<string, IReadOnlyList<double>> taskDurations, IReadOnlyDictionary<string, long> demand)
    {
        var counted = (bindings ?? []).Where(binding => binding is not null && Counts(binding)).ToArray();
        var deviceSeconds = DeviceSecondsByPlatform(counted.Where(binding => available.Contains(binding.Platform)));
        return platforms.Select(platform =>
        {
            var gate = gates.TryGetValue(platform, out var stored) ? stored : (IntervalSeconds: 0, DailyCap: null);
            return new AppPlatformCapacityInput(platform, available.Contains(platform),
                counted.Where(binding => binding.Platform == platform).Select(binding => new AppBindingRate(binding.MinIntervalSeconds, binding.DailyQuota)).ToArray(),
                gate.IntervalSeconds, gate.DailyCap, deviceSeconds.GetValueOrDefault(platform), taskDurations.GetValueOrDefault(platform, []),
                demand.GetValueOrDefault(platform));
        }).ToArray();
    }

    /// <summary>按 design 6.5 计算一个平台的容量视图。</summary>
    /// <param name="input">平台的限速、设备时间、任务耗时与需求。</param>
    public static AppPlatformCapacity Compute(AppPlatformCapacityInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var bindings = input.ActiveBindings ?? [];
        var rate = input.Available && bindings.Count > 0
            ? (long)Math.Floor(Math.Max(0m, input.DeviceSecondsPerWeek) / AppPoolRules.DeviceCooldownSeconds) : 0;
        var samples = Samples(input.TaskDurationSeconds);
        var measured = MeasuredWeekly(input.DeviceSecondsPerWeek, samples);
        var capacity = !input.Available ? 0 : measured is { } value ? Math.Min(rate, value) : rate;
        var demand = Math.Max(0, input.WeeklyDemand);
        return new AppPlatformCapacity(input.Platform, input.Available, bindings.Count, rate, measured, capacity, demand,
            Math.Max(0, demand - capacity), Median(samples), samples.Count);
    }

    /// <summary>
    /// 按排队顺序（design 6.4：先比提问在本品牌本平台里的序号）把周容量分给各品牌：找最大的序号 r，使 Σ min(需求ᵢ, r) 不超过容量，
    /// 每个品牌得到 min(需求ᵢ, r)。序号 r + 1 那一层放不满时谁先排到取决于创建时间，这里不计入任何品牌（按排不到估算）。
    /// 容量够用时每个品牌都得到全部需求；容量不大于 0 时都得到 0。返回值与 <paramref name="demands"/> 逐项对应。
    /// </summary>
    /// <param name="demands">各品牌的需求（负数按 0 计）。</param>
    /// <param name="capacity">周容量。</param>
    public static IReadOnlyList<long> AllocateByFairRank(IReadOnlyList<long> demands, long capacity)
    {
        ArgumentNullException.ThrowIfNull(demands);
        var wanted = demands.Select(demand => Math.Max(0, demand)).ToArray();
        if (capacity <= 0) return new long[wanted.Length];
        if (wanted.Sum() <= capacity) return wanted;
        long low = 0, high = wanted.Max();
        while (low < high)
        {
            var mid = low + (high - low + 1) / 2;
            if (wanted.Sum(demand => Math.Min(demand, mid)) <= capacity) low = mid;
            else high = mid - 1;
        }
        return wanted.Select(demand => Math.Min(demand, low)).ToArray();
    }

    /// <summary>
    /// 一个品牌在某个平台上的份额：周需求 = 其他品牌的需求 + 该品牌的需求（预估时是草稿下的需求），再按 <see cref="AllocateByFairRank"/> 分配。
    /// </summary>
    /// <param name="input">平台的输入；其中的 <see cref="AppPlatformCapacityInput.WeeklyDemand"/> 被忽略，由下面两项重算。</param>
    /// <param name="otherBrandDemands">同一平台上其他品牌（全部开放租户）的需求。</param>
    /// <param name="brandDemand">该品牌的需求。</param>
    public static AppBrandCapacityShare ForBrand(AppPlatformCapacityInput input, IReadOnlyList<long> otherBrandDemands, long brandDemand)
    {
        ArgumentNullException.ThrowIfNull(input);
        IReadOnlyList<long> demands = [.. otherBrandDemands ?? [], Math.Max(0, brandDemand)];
        var capacity = Compute(input with { WeeklyDemand = demands.Sum() });
        return new AppBrandCapacityShare(capacity, AllocateByFairRank(demands, capacity.WeeklyCapacity)[^1]);
    }
}
