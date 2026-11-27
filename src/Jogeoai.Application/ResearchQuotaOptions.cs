namespace Jogeoai.Application;

/// <summary>
/// 品牌研究入队额度（配置节 <c>Research:Quota</c>）。入队只发生在 API 进程；配置节缺失时使用默认值，任一数值小于 1 时拒绝启动。
/// </summary>
public sealed class ResearchQuotaOptions
{
    /// <summary>配置节名称。</summary>
    public const string SectionName = "Research:Quota";

    /// <summary>每个品牌站点同时允许的 pending / running 研究任务数，默认 1。</summary>
    public int MaxActivePerSite { get; set; } = 1;

    /// <summary>每个租户在滚动 24 小时内可新建的研究任务数，默认 20；已取消和失败的任务同样计入，幂等重放不计入。</summary>
    public int DailyPerTenant { get; set; } = 20;

    /// <summary>参数是否有效：两项都至少为 1。</summary>
    public bool IsValid() => MaxActivePerSite >= 1 && DailyPerTenant >= 1;
}
