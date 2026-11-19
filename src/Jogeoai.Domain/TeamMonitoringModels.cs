namespace Jogeoai.Domain;

/// <summary>团队默认监控配置中要保存的一个单元格（模型 × 回答模式 × 采集频率）；不含国家 / 语言，同步到品牌时按品牌取地区。</summary>
/// <param name="Provider">API 回答 Provider（如 cloro、deepseek_api）；只能是可参与每日监控的 Provider，否则返回 400 TARGET_NOT_ALLOWED。</param>
/// <param name="Engine">模型（引擎）标识，必须属于该 Provider，否则返回 400 TARGET_NOT_ALLOWED。</param>
/// <param name="AnswerMode">回答模式；省略为该 Provider 的默认模式（见目录 defaultAnswerMode）。不受支持返回 400 ANSWER_MODE_UNSUPPORTED。</param>
/// <param name="Frequency">采集频率；省略为 daily（1次/天）。取值无效返回 400 FREQUENCY_INVALID。
/// App 单元格（provider=app_collector，engine 为平台代码，answerMode 只能为 fast 或省略）的频率固定为 weekly，必须显式传 weekly，
/// 省略或其他取值返回 400 FREQUENCY_INVALID；同步到品牌时固定为 CN / zh-CN，不取品牌的地区。</param>
public sealed record TeamMonitoringCellWrite(string Provider, string Engine, AnswerMode? AnswerMode = null,
    MonitoringFrequency Frequency = MonitoringFrequency.Daily);

/// <summary>团队默认监控配置或品牌已启用监控配置中的一个单元格（模型 × 回答模式 × 采集频率）。</summary>
/// <param name="Provider">API 回答 Provider。</param>
/// <param name="Engine">模型（引擎）标识。</param>
/// <param name="DisplayName">模型显示名称（与目录 displayName 相同）。</param>
/// <param name="AnswerMode">实际回答模式（已按 Provider 默认模式解析，不为空）。</param>
/// <param name="Frequency">采集频率。</param>
public sealed record TeamMonitoringCellView(string Provider, string Engine, string DisplayName, AnswerMode AnswerMode,
    MonitoringFrequency Frequency);

/// <summary>团队内一个启用品牌的监控配置状态。</summary>
/// <param name="SiteId">品牌工作区标识。</param>
/// <param name="DisplayName">品牌名称。</param>
/// <param name="FollowsTeamDefault">是否跟随团队默认监控配置：true = 跟随，false = 单独配置。</param>
/// <param name="MonitoringStarted">品牌监控是否已开启：09:01 主计划存在且启用；对已开放 App 渠道的团队，只有 App 每周计划（存在且启用）的品牌也算已开启。</param>
/// <param name="TargetConfigVersion">品牌目标配置版本；恢复跟随（follow-default）与保存品牌目标时作为 expectedVersion。</param>
/// <param name="Cells">品牌当前启用的可监控单元格（同一模型多个地区时去重），按目录顺序排列。</param>
public sealed record TeamMonitoringBrandView(Guid SiteId, string DisplayName, bool FollowsTeamDefault, bool MonitoringStarted,
    uint TargetConfigVersion, IReadOnlyList<TeamMonitoringCellView> Cells);

/// <summary>团队默认监控配置及团队内各启用品牌的跟随状态。</summary>
/// <param name="ConfigVersion">团队默认配置版本；0 表示从未保存过，此时 cells 为系统基线（ChatGPT 快速回答 1次/天），保存时 expectedVersion 传 0。</param>
/// <param name="CanEdit">当前用户能否修改团队默认配置（团队所有者或管理员）。</param>
/// <param name="Cells">团队默认配置的单元格，按目录顺序排列。</param>
/// <param name="Brands">团队内的启用品牌，按创建时间排序。</param>
public sealed record TeamMonitoringConfigView(uint ConfigVersion, bool CanEdit, IReadOnlyList<TeamMonitoringCellView> Cells,
    IReadOnlyList<TeamMonitoringBrandView> Brands);

/// <summary>保存团队默认监控配置的请求；保存后在同一事务内同步到所有跟随的启用品牌。</summary>
/// <param name="ExpectedVersion">当前团队默认配置版本（从未保存过时为 0）；不一致返回 409 CONFIG_VERSION_CONFLICT。</param>
/// <param name="Cells">新的完整单元格集合，1–20 个：为空返回 400 MONITORING_TARGETS_REQUIRED，超过 20 个返回 400 MONITORING_TOO_LARGE，
/// 同一 provider + engine + 回答模式重复返回 400 DUPLICATE_TARGET。App 单元格不计入这 20 个（每个平台最多一个 App 单元格）。</param>
public sealed record TeamMonitoringConfigWrite(uint ExpectedVersion, IReadOnlyList<TeamMonitoringCellWrite> Cells);

/// <summary>团队默认监控配置草稿的影响预估请求；只计算，不保存。</summary>
/// <param name="Cells">草稿单元格，校验与保存相同（不因超过 20 个返回 400，而在各品牌的 blockers 中报告）。</param>
public sealed record TeamMonitoringEstimateRequest(IReadOnlyList<TeamMonitoringCellWrite> Cells);

/// <summary>一个跟随品牌在草稿配置下的预估（与品牌目标预估 POST /sites/{siteId}/targets/estimate 的口径相同）。</summary>
/// <param name="SiteId">品牌工作区标识。</param>
/// <param name="DisplayName">品牌名称。</param>
/// <param name="PromptCount">品牌当前启用且未归档的提问数。</param>
/// <param name="WeeklyChecks">每周检测次数（每2天1次按 3.5 轮计，可能带小数）。</param>
/// <param name="WeeklyCredits">每周积分。</param>
/// <param name="NextSlotChecks">下一轮时隙按采集频率过滤后的检测次数。</param>
/// <param name="NextSlotCredits">下一轮时隙的积分。</param>
/// <param name="Blockers">阻止该品牌采集的原因码（MONITORING_TOO_LARGE、MONITORING_TARGETS_REQUIRED、ANSWER_MODE_UNSUPPORTED），见品牌目标预估。</param>
/// <param name="Channels">该品牌的渠道分项（API 与 App），与品牌目标预估的 channels 相同；只在该品牌的草稿含已启用的 App 单元格时出现，
/// 此时 WeeklyChecks / WeeklyCredits 是 API 与 App 可执行部分之和。没有 App 单元格时不出现，响应与引入 App 渠道之前完全相同。</param>
public sealed record TeamMonitoringBrandEstimate(Guid SiteId, string DisplayName, int PromptCount, decimal WeeklyChecks,
    decimal WeeklyCredits, long NextSlotChecks, decimal NextSlotCredits, IReadOnlyList<string> Blockers,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    MonitoringEstimateChannels? Channels = null);

/// <summary>团队默认监控配置草稿对所有跟随品牌的合计影响；余额不足只体现在 Shortfall，不阻止保存。</summary>
/// <param name="UnitPrice">每次检测的积分单价。</param>
/// <param name="Balance">团队当前可用积分。</param>
/// <param name="WeeklyChecks">跟随品牌合计每周检测次数（品牌有 App 单元格时含其 App 可执行部分）。</param>
/// <param name="WeeklyCredits">跟随品牌合计每周积分（品牌有 App 单元格时含其 App 可执行部分）。</param>
/// <param name="NextSlotCredits">跟随品牌各自下一轮积分之和。</param>
/// <param name="Shortfall">max(0, NextSlotCredits − Balance)；只提示，不阻止保存。</param>
/// <param name="Brands">参与预估的跟随品牌（启用且跟随团队默认配置），按创建时间排序。</param>
public sealed record TeamMonitoringEstimateView(decimal UnitPrice, decimal Balance, decimal WeeklyChecks, decimal WeeklyCredits,
    decimal NextSlotCredits, decimal Shortfall, IReadOnlyList<TeamMonitoringBrandEstimate> Brands);

/// <summary>恢复跟随团队默认监控配置的请求。</summary>
/// <param name="ExpectedVersion">品牌当前目标配置版本（GET /sites/{siteId}/targets 的 configVersion）；不一致返回 409 CONFIG_VERSION_CONFLICT。</param>
public sealed record FollowTeamDefaultRequest(uint ExpectedVersion);

/// <summary>团队默认单元格按某个品牌物化后的站点目标（内部使用：影响预估、首次开启监控）。</summary>
/// <param name="SiteId">品牌工作区标识。</param>
/// <param name="DisplayName">品牌名称。</param>
/// <param name="Targets">物化后的可监控站点目标（全部启用）。</param>
public sealed record TeamMonitoringBrandTargets(Guid SiteId, string DisplayName, IReadOnlyList<SiteTargetWrite> Targets);

/// <summary>团队默认监控配置的纯函数规则：基线、单元格签名与排序。</summary>
public static class TeamMonitoringRules
{
    /// <summary>团队接口在迁移 048 未部署时返回的错误码（503）。</summary>
    public const string SchemaNotMigratedCode = "TEAM_MONITORING_SCHEMA_NOT_MIGRATED";

    /// <summary>
    /// 系统基线（团队从未保存默认配置时的虚拟默认配置）：ChatGPT（Cloro）默认模式、1次/天，与首次开启监控的
    /// <see cref="PromptMonitoringDefaults.Target"/> 选择的模型一致。
    /// </summary>
    public static TeamMonitoringCellWrite Baseline { get; } = new("cloro", "chatgpt");

    /// <summary>
    /// 单元格签名 <c>provider|engine|storedMode|frequency</c>：storedMode 为折叠后的模式（与 Provider 默认模式相同时为空串），
    /// 与迁移 048 的归类签名一致；比较品牌与默认配置时忽略国家 / 语言。
    /// </summary>
    /// <param name="provider">Provider 标识（小写）。</param>
    /// <param name="engine">引擎标识（小写）。</param>
    /// <param name="storedMode">折叠后的模式存储值；默认模式为空。</param>
    /// <param name="frequency">采集频率。</param>
    public static string Signature(string provider, string engine, string? storedMode, MonitoringFrequency frequency)
        => string.Join('|', provider, engine, storedMode ?? "", MonitoringFrequencyRules.ToStorage(frequency));

    /// <summary>目录顺序的排序键：Provider 注册顺序、引擎在 Provider 中的顺序、回答模式（fast 在前）。
    /// App 单元格排在全部 API Provider 之后，App 平台之间按 <see cref="AppChannelDefinition.Platforms"/> 的顺序。</summary>
    /// <param name="provider">Provider 标识。</param>
    /// <param name="engine">引擎标识。</param>
    /// <param name="mode">已解析的回答模式。</param>
    public static (int Provider, int Engine, int Mode) CatalogOrder(string provider, string engine, AnswerMode mode)
    {
        // The App segment follows every registered API provider; App platforms keep the AppChannelDefinition.Platforms order.
        if (MonitoringProviders.IsApp(provider))
            return (AnswerProviderDefinition.All.Count, IndexOf(AppChannelDefinition.Engines, engine), (int)mode);
        var definition = AnswerProviderDefinition.TryGet(provider);
        var providerIndex = definition is null ? int.MaxValue : IndexOf(AnswerProviderDefinition.All, definition);
        var engineIndex = definition is null ? int.MaxValue : IndexOf(definition.Engines, engine);
        return (providerIndex, engineIndex, (int)mode);
    }

    private static int IndexOf<T>(IReadOnlyList<T> items, T value)
    {
        for (var i = 0; i < items.Count; i++)
            if (EqualityComparer<T>.Default.Equals(items[i], value)) return i;
        return int.MaxValue;
    }
}
