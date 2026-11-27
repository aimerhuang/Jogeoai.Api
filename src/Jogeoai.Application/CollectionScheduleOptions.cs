namespace Jogeoai.Application;

/// <summary>采集计划的运行参数（配置节 Collection:Schedules）。</summary>
public sealed class CollectionScheduleOptions
{
    /// <summary>配置节名称。</summary>
    public const string SectionName = "Collection:Schedules";

    /// <summary>每个站点自定义计划数量上限的默认值。</summary>
    public const int DefaultMaxCustomPerSite = 20;

    /// <summary>
    /// 每个站点最多可以有多少个自定义计划（名称不是系统保留名称的计划，启用与停用的都计入）；默认 20。
    /// 达到上限后再创建返回 409 SCHEDULE_LIMIT_REACHED。系统维护的每日监控主计划、凌晨计划与 App 采集计划不计入。
    /// </summary>
    public int MaxCustomPerSite { get; set; } = DefaultMaxCustomPerSite;
}
