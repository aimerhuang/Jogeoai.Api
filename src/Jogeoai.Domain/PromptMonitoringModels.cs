using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Jogeoai.Domain;

/// <summary>立即采集的处理状态。</summary>
public enum InstantCollectionStatus
{
    /// <summary>已开始采集：已建立立即采集运行（品牌尚无监控计划时为首轮运行）。</summary>
    [JsonStringEnumMemberName("started")]
    Started,

    /// <summary>当天已采集：要采集的「提问 × 模型单元格」当天都已有排队、进行中或成功的监控检测，未重复建立运行。</summary>
    [JsonStringEnumMemberName("already_collected")]
    AlreadyCollected,

    /// <summary>未立即采集：原因见 ReasonCode 与 Message；提问的创建或平台范围保存不受影响。</summary>
    [JsonStringEnumMemberName("skipped")]
    Skipped,
}

/// <summary>新增提问或给提问新增模型后的立即采集结果；只附在写接口响应中，立即采集失败不影响写操作本身。</summary>
/// <param name="Status">处理状态：started（已开始采集）、already_collected（当天已采集）或 skipped（未立即采集）。</param>
/// <param name="RunId">已建立的采集运行标识；未建立运行时为空。</param>
/// <param name="CheckCount">本次立即采集的检测次数（提问 × 模型单元格 × 采样数）；未建立运行时为本次预计的检测次数，无法计算时为 0。</param>
/// <param name="EstimatedCredits">本次立即采集预计积分（每次检测 10 积分，与每日监控同价）。</param>
/// <param name="ReasonCode">未立即采集的原因码：CREDIT_INSUFFICIENT（余额不足）、TARGET_UNAVAILABLE / CLORO_KEY_POOL_EXHAUSTED（采集服务未配置或 Key 池耗尽）、
/// MONITORING_PAUSED（监控计划已暂停）、MONITORING_TARGETS_REQUIRED（提问没有有效模型）、MONITORING_BUSY（品牌监控正忙）、
/// MONITORING_TOO_LARGE（超过每轮 1000 次检测）、INSTANT_COLLECTION_FAILED（未预期的错误）或其他业务错误码；已开始或当天已采集时为空。</param>
/// <param name="Message">面向用户的中文说明。</param>
/// <param name="Shortfall">余额不足时的积分缺口（预计积分 − 可用积分）；其他情况为空。</param>
public sealed record InstantCollectionView(InstantCollectionStatus Status, Guid? RunId, int CheckCount,
    decimal EstimatedCredits, string? ReasonCode = null, string? Message = null, decimal? Shortfall = null);

/// <summary>所选问题的每日监控状态；任务状态由真实采集运行提供。</summary>
/// <param name="PromptIds">每日监控计划（Schedule）覆盖的问题标识；没有每日监控计划时为空列表。</param>
/// <param name="Schedule">每日监控计划（API 渠道的 09:01 主计划）；尚未启动、或品牌只启用了 App 目标时为空。</param>
/// <param name="Run">当前正在执行或最近结束的监控运行（只含 API 渠道的运行）。</param>
/// <param name="Billing">当前轮次的服务端费用估算与结算摘要（只含 API 渠道的每日监控，不含 App 检测）。</param>
/// <param name="AppSchedule">App 渠道的每周采集计划（每天 09:11 检查到期的 App 目标，每个目标 1 次/周）；品牌没有 App 采集计划、
/// 或团队未开放 App 渠道时为空。只启用了 App 目标的品牌 Schedule 为空而本字段有值，表示监控已开启。
/// 暂停与恢复：品牌有 Schedule 时操作 Schedule，App 计划随之暂停或恢复；只有 AppSchedule 时操作 AppSchedule。</param>
public sealed record PromptMonitoringView(IReadOnlyList<Guid> PromptIds,
    CollectionScheduleView? Schedule, VisibilityRunSummary? Run,
    PromptMonitoringBillingView? Billing = null, CollectionScheduleView? AppSchedule = null);

/// <summary>每日监控积分摘要。每个问题、目标模型和采样占用 10 积分。</summary>
/// <param name="ActivePromptCount">本轮有效问题数。</param>
/// <param name="TargetCount">本轮目标模型数。</param>
/// <param name="SampleCount">每个模型的采样数。</param>
/// <param name="UnitPrice">每个 Check 的积分单价。</param>
/// <param name="EstimatedCredits">本轮预计积分。</param>
/// <param name="AvailableCredits">当前团队可用积分。</param>
/// <param name="HeldCredits">仍在预留中的积分。</param>
/// <param name="CapturedCredits">已结算的积分。</param>
/// <param name="ReleasedCredits">已释放的积分。</param>
/// <param name="Status">费用状态。</param>
/// <param name="Reason">余额不足或未知提交等状态说明。</param>
/// <param name="CheckCount">本轮检测次数：按每条提问 × 它的有效目标 × 采样数累加；未设置平台范围时等于问题数 × 目标数 × 采样数。</param>
public sealed record PromptMonitoringBillingView(
    int ActivePromptCount, int TargetCount, int SampleCount, decimal UnitPrice,
    decimal EstimatedCredits, decimal AvailableCredits, decimal HeldCredits,
    decimal CapturedCredits, decimal ReleasedCredits, string Status, string? Reason = null, long CheckCount = 0);

/// <summary>新增监测问题的每轮检测与积分预估请求。</summary>
/// <param name="NewPromptCount">本次准备新增的启用问题数，范围 0–200。</param>
/// <param name="TargetKeys">新增问题使用的 AI 平台范围（采集目标键）；省略表示全部平台（all），非空时须为品牌当前启用且可监控的 1–20 个不重复目标键。
/// 已开放 App 渠道的团队还可以带上该品牌已启用的 App 目标键；它们不计入本预估的任何检测数。</param>
public sealed record PromptMonitoringEstimateRequest(int NewPromptCount, IReadOnlyList<string>? TargetKeys = null);

/// <summary>新增监测问题的每轮检测与积分预估；计算与每日监控预留使用同一检测矩阵。余额不足只体现在 Shortfall，不作为阻断项。</summary>
/// <param name="UnitPrice">每次检测的积分单价。</param>
/// <param name="SampleCount">每日监控每个检测对的采样数。</param>
/// <param name="CurrentPromptCount">品牌当前启用且未归档的问题数。</param>
/// <param name="CurrentChecksPerRound">当前每轮检测次数。</param>
/// <param name="CurrentCreditsPerRound">当前每轮积分。</param>
/// <param name="AddedChecksPerRound">本次新增带来的每轮检测次数。</param>
/// <param name="AddedCreditsPerRound">本次新增带来的每轮积分。</param>
/// <param name="TotalChecksPerRound">创建后品牌每轮检测次数。</param>
/// <param name="TotalCreditsPerRound">创建后品牌每轮积分。</param>
/// <param name="AvailableCredits">团队当前可用积分。</param>
/// <param name="Shortfall">创建后下一轮的积分差额，max(0, TotalCreditsPerRound − AvailableCredits)；只提示，不阻止创建。</param>
/// <param name="Blockers">阻止创建的原因码：MONITORING_TOO_LARGE（每品牌超过 200 个问题、每轮超过 1000 次检测或超过 20 个目标）、
/// PLAN_LIMIT_EXCEEDED（超出套餐提问额度）、MONITORING_TARGETS_REQUIRED（没有可用于每日监控的平台；品牌没有 API 目标但已启用 App 目标时不报告）。</param>
public sealed record PromptMonitoringEstimateView(decimal UnitPrice, int SampleCount,
    int CurrentPromptCount, long CurrentChecksPerRound, decimal CurrentCreditsPerRound,
    long AddedChecksPerRound, decimal AddedCreditsPerRound,
    long TotalChecksPerRound, decimal TotalCreditsPerRound,
    decimal AvailableCredits, decimal Shortfall, IReadOnlyList<string> Blockers);

/// <summary>品牌监控目标草稿（模型 × 回答模式 × 采集频率）的预估请求；只计算，不保存。</summary>
/// <param name="Targets">草稿目标集合，校验规则与保存站点目标（PUT）相同；只有已启用且可参与每日监控的目标计入预估，
/// 国内网页采集等不参与每日监控的目标与停用目标被忽略。</param>
public sealed record MonitoringTargetsEstimateRequest(IReadOnlyList<SiteTargetWrite> Targets);

/// <summary>预估中的一个已启用监控单元格（模型 × 回答模式）。</summary>
/// <param name="TargetKey">目标键（含回答模式段；默认模式与旧键一致）。</param>
/// <param name="Engine">模型（引擎）标识。</param>
/// <param name="AnswerMode">实际回答模式（省略时为 Provider 默认模式）。</param>
/// <param name="Frequency">采集频率。</param>
/// <param name="WeeklyRuns">每周采集轮数：2次/天 14、1次/天 7、每2天1次 3.5、1次/周 1。</param>
/// <param name="PromptCount">按提问平台范围取交集后，会采集该单元格的启用提问数。App 单元格只数通过进入 App 渠道检查的提问。</param>
/// <param name="Channel">单元格所属渠道：api 或 app。只在草稿含已启用的 App 单元格时出现（此时每个单元格都带）；没有 App 单元格时不出现，
/// 响应与引入 App 渠道之前完全相同。</param>
public sealed record MonitoringTargetCellEstimate(string TargetKey, string Engine, AnswerMode AnswerMode,
    MonitoringFrequency Frequency, decimal WeeklyRuns, int PromptCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CollectionChannel? Channel = null);

/// <summary>
/// 品牌监控目标草稿的每周与下一轮检测、积分预估；与每日监控使用同一检测矩阵、提问平台范围交集与积分单价。
/// 余额不足只体现在 Shortfall，不作为阻断项。
/// </summary>
/// <param name="PromptCount">品牌当前启用且未归档的提问数。</param>
/// <param name="WeeklyChecks">每周检测次数：Σ 提问 × 有效单元格 × 采样数 × 每周轮数（每2天1次按 3.5，可能带小数）。</param>
/// <param name="WeeklyCredits">每周积分：WeeklyChecks × 单价。</param>
/// <param name="NextSlotChecks">下一轮时隙（凌晨 00:05 与早上 09:01 中较近者，品牌时区）按采集频率过滤后的实际检测次数。</param>
/// <param name="NextSlotCredits">下一轮时隙的积分。</param>
/// <param name="Cells">已启用且可参与每日监控的单元格明细，按目标键排序。</param>
/// <param name="Balance">团队当前可用积分。</param>
/// <param name="Shortfall">下一轮的积分差额 max(0, NextSlotCredits − Balance)；只提示，不阻止保存。</param>
/// <param name="Blockers">阻止保存或采集的原因码：MONITORING_TOO_LARGE（启用单元格超过 20 个、提问超过 200 个或一轮全部单元格到期时超过 1000 次检测）、
/// MONITORING_TARGETS_REQUIRED（没有启用可监控的单元格，或提问平台范围与启用单元格没有交集；草稿启用了 App 目标且 App 渠道会采集到提问时不报告）、
/// ANSWER_MODE_UNSUPPORTED（Provider 当前全局配置无法按该回答模式采集，例如豆包非 messages 模式下的快速回答）。</param>
/// <param name="UnitPrice">每次检测的积分单价。</param>
/// <param name="SampleCount">每日监控每个检测对的采样数。</param>
/// <param name="NextSlotAt">下一轮时隙的 UTC 时间。</param>
/// <param name="NextSlotKind">下一轮时隙种类：main（09:01）或 night（00:05，仅在存在 2次/天 单元格时参与比较）。</param>
/// <param name="Channels">渠道分项（API 与 App）。只在草稿含已启用的 App 单元格时出现，此时顶层 WeeklyChecks / WeeklyCredits 是 API 与 App
/// 可执行部分之和，Cells 在 API 单元格之后按目录顺序列出 App 单元格；NextSlot*、Shortfall、Blockers 仍只指 API 的每日监控。
/// 没有 App 单元格时不出现，响应与引入 App 渠道之前完全相同。</param>
public sealed record MonitoringTargetsEstimateView(int PromptCount, decimal WeeklyChecks, decimal WeeklyCredits,
    long NextSlotChecks, decimal NextSlotCredits, IReadOnlyList<MonitoringTargetCellEstimate> Cells,
    decimal Balance, decimal Shortfall, IReadOnlyList<string> Blockers,
    decimal UnitPrice, int SampleCount, DateTimeOffset NextSlotAt, MonitoringSlotKind NextSlotKind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MonitoringEstimateChannels? Channels = null);

/// <summary>监控每个 Check 的固定积分单价。</summary>
public static class PromptMonitoringPricing
{
    /// <summary>每个问题、目标模型和采样的固定积分单价。</summary>
    public const decimal UnitPrice = 10m;

    /// <summary>按问题、模型和采样数计算一轮监控预计积分。</summary>
    public static decimal Estimate(int promptCount, int targetCount, int sampleCount) =>
        Math.Max(0, promptCount) * Math.Max(0, targetCount) * Math.Max(0, sampleCount) * UnitPrice;

    /// <summary>按检测次数（<see cref="MonitoringMatrix.CountChecks(CollectionScheduleSelection)"/>）计算一轮监控预计积分。</summary>
    public static decimal Estimate(long checkCount) => Math.Max(0L, checkCount) * UnitPrice;
}

/// <summary>自动监控的稳定身份，刷新或多个浏览器进入时复用首轮运行。</summary>
public static class PromptMonitoringIdentity
{
    /// <summary>保留给自动监控的计划名称。</summary>
    public const string ScheduleName = "jogeoai:auto-prompt-monitoring:v1";

    /// <summary>保留给自动监控凌晨轮（只包含 2次/天 的单元格）的计划名称。</summary>
    public const string NightScheduleName = "jogeoai:auto-prompt-monitoring:night:v1";

    /// <summary>主计划的本地时刻（品牌时区，HH:mm）。</summary>
    public const string MainLocalTime = "09:01";

    /// <summary>凌晨计划的本地时刻（品牌时区，HH:mm）；同一本地日期内先于主计划执行。</summary>
    public const string NightLocalTime = "00:05";

    /// <summary>保留给 App 渠道每周计划（只包含 App 目标）的计划名称。</summary>
    public const string AppScheduleName = "jogeoai:auto-prompt-monitoring:app:v1";

    /// <summary>App 计划的本地时刻（主计划时区，HH:mm）；排在主计划之后，使当天的 API 监控先完成预留。</summary>
    public const string AppLocalTime = "09:11";

    /// <summary>
    /// 是否为 App 渠道的每周计划。App 计划有自己的时隙处理路径：<see cref="SlotKind"/> 对它返回 null，
    /// <see cref="IsAutomaticSchedule"/> 对它返回 false，主计划与凌晨计划的任何分支都不会把它当作自己的时隙。
    /// </summary>
    /// <param name="scheduleName">计划名称。</param>
    public static bool IsAppSchedule(string? scheduleName) => scheduleName == AppScheduleName;

    /// <summary>自动监控计划（09:01 主计划或 00:05 凌晨计划）对应的时隙种类；其他计划（包括 App 计划）返回 null。</summary>
    /// <param name="scheduleName">计划名称。</param>
    public static MonitoringSlotKind? SlotKind(string? scheduleName) => scheduleName switch
    {
        ScheduleName => MonitoringSlotKind.Main,
        NightScheduleName => MonitoringSlotKind.Night,
        _ => null,
    };

    /// <summary>是否为自动监控计划（主计划或凌晨计划）：按自动选择重算时隙、收取监控积分。</summary>
    /// <param name="scheduleName">计划名称。</param>
    public static bool IsAutomaticSchedule(string? scheduleName) => SlotKind(scheduleName) is not null;

    /// <summary>问题版本、采集目标或品牌分析配置变化后创建新的首轮运行。</summary>
    public static string InitialRunKey(Guid siteId, uint subjectConfigVersion,
        IReadOnlyList<PromptView> prompts, CollectionScheduleSelection selection)
    {
        var versions = string.Join("|", prompts.OrderBy(p => p.PromptId)
            .Select(p => $"{p.PromptId:N}:{p.Version}"));
        var selectionHash = CollectionIdentity.SelectionHash(selection);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{subjectConfigVersion}|{versions}|{selectionHash}"))).ToLowerInvariant();
        return $"prompt-monitor:{siteId:N}:{digest}";
    }

    /// <summary>首轮监控运行的发起人（requested_by）。</summary>
    /// <param name="siteId">品牌标识。</param>
    public static string InitialRequestedBy(Guid siteId) => $"prompt-monitor:{siteId:N}";

    /// <summary>新增提问或新增模型后立即采集运行的发起人（requested_by）。</summary>
    /// <param name="siteId">品牌标识。</param>
    public static string InstantRequestedBy(Guid siteId) => $"prompt-instant:{siteId:N}";

    /// <summary>
    /// 立即采集运行的幂等键：<c>prompt-instant:{siteId:N}:{yyyyMMdd}:{digest}</c>。日期是主计划时区的本地日期；
    /// digest 为 SHA-256(<c>sampleCount|</c> + 按序数排序的 <c>promptId:targetKey</c> 列表)，与检测对的输入顺序无关。
    /// </summary>
    /// <param name="siteId">品牌标识。</param>
    /// <param name="localDate">主计划时区的本地日期。</param>
    /// <param name="pairs">本次要采集的「提问 × 目标键」。</param>
    /// <param name="sampleCount">采样数。</param>
    public static string InstantRunKey(Guid siteId, DateOnly localDate, IEnumerable<PromptTargetPair> pairs, int sampleCount)
    {
        var list = string.Join("|", pairs.Select(p => $"{p.PromptId:N}:{p.TargetKey}").Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal));
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{sampleCount}|{list}"))).ToLowerInvariant();
        return $"{InstantRequestedBy(siteId)}:{localDate.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture)}:{digest}";
    }
}

/// <summary>首次自动监控的默认目标来自服务目录，而非品牌所在地区。</summary>
public static class PromptMonitoringDefaults
{
    /// <summary>保留提问语言，使用目录明确支持的采集地区，避免向 ChatGPT 发送不支持的 CN。</summary>
    public static SiteTargetWrite Target(IReadOnlyList<CollectionTargetView> catalog, string locale)
    {
        var target = catalog.FirstOrDefault(t => t.Provider == "cloro" && t.Engine == "chatgpt"
            && t.Enabled && t.Availability == "available");
        if (target is null)
            throw new CollectorStoreException("MONITORING_PROVIDER_UNAVAILABLE", "监控服务尚未配置或不可用，请联系管理员。", 503);
        return new SiteTargetWrite(target.Provider, target.Engine, target.CaptureMode, target.Country, locale);
    }
}
