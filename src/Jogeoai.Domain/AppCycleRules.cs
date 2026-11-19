using System.Globalization;

namespace Jogeoai.Domain;

/// <summary>
/// App 周期 Run 的纯规则（design 4.2 / 4.3 / 6.4）：截止时点、排队序号和 Run 状态汇总。不访问数据库，同样的输入结果恒定。
/// </summary>
public static class AppCycleRules
{
    /// <summary>一个 App 周期的天数：任务在周期起始日创建，截止时点是起始时隙之后的这么多天。</summary>
    public const int CycleDays = 7;

    /// <summary>App Run 状态：还有待采集或采集中的检测。</summary>
    public const string StatusRunning = "running";

    /// <summary>App Run 状态：全部检测成功。</summary>
    public const string StatusSucceeded = "succeeded";

    /// <summary>App Run 状态：全部检测未采集（cancelled）。</summary>
    public const string StatusCancelled = "cancelled";

    /// <summary>App Run 状态：全部检测失败。</summary>
    public const string StatusFailed = "failed";

    /// <summary>App Run 状态：已全部结束，但不是同一种结果（例如有成功也有未采集）。</summary>
    public const string StatusPartialFailed = "partial_failed";

    /// <summary>
    /// 周期的截止时点（UTC）：时隙的当地时刻加 <see cref="CycleDays"/> 天，即下一个到期时隙的时刻。
    /// 夏令时的处理与计划时隙相同：落在不存在的当地时刻时顺延到第一个有效分钟；落在重复的当地时刻时取较早的 UTC 时刻。
    /// </summary>
    /// <param name="localSlotKey">时隙键 <c>yyyyMMddTHHmm</c>（<see cref="CollectionScheduleCalendar.LocalSlotKey"/>）。</param>
    /// <param name="zone">计划的时区。</param>
    /// <exception cref="FormatException">时隙键不是 <c>yyyyMMddTHHmm</c>。</exception>
    public static DateTimeOffset DeadlineUtc(string localSlotKey, TimeZoneInfo zone)
    {
        var local = DateTime.ParseExact(localSlotKey, "yyyyMMdd'T'HHmm", CultureInfo.InvariantCulture, DateTimeStyles.None).AddDays(CycleDays);
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    /// <summary>
    /// 排队序号（collector_tasks.fair_rank）：提问在「本 Run × 本平台」里从 1 开始的序号，按提问的创建顺序排。
    /// 领取时先比序号，所以所有品牌的第 1 条提问排在任何品牌的第 2 条之前，提问多的品牌不会挡住别人。
    /// 序号只由输入决定；同一平台内同一提问出现多次时序号相同。
    /// </summary>
    /// <param name="promptsInCreationOrder">本 Run 的提问，按创建时间、再按标识排好序。不在其中的提问排在最后，彼此按标识的字符串序。</param>
    /// <param name="pairs">本 Run 的「提问 × 平台」。</param>
    /// <returns>与 <paramref name="pairs"/> 逐项对应的序号。</returns>
    public static IReadOnlyList<int> FairRanks(IReadOnlyList<Guid> promptsInCreationOrder,
        IReadOnlyList<(Guid PromptId, string Platform)> pairs)
    {
        var position = new Dictionary<Guid, int>();
        for (var i = 0; i < promptsInCreationOrder.Count; i++) position.TryAdd(promptsInCreationOrder[i], i);
        var ranks = new Dictionary<(Guid, string), int>();
        foreach (var platform in pairs.GroupBy(pair => pair.Platform, StringComparer.Ordinal))
        {
            var ordered = platform.Select(pair => pair.PromptId).Distinct()
                .OrderBy(id => position.TryGetValue(id, out var index) ? index : int.MaxValue)
                .ThenBy(id => id.ToString("N"), StringComparer.Ordinal);
            var rank = 0;
            foreach (var prompt in ordered) ranks[(prompt, platform.Key)] = ++rank;
        }
        return pairs.Select(pair => ranks[(pair.PromptId, pair.Platform)]).ToArray();
    }

    /// <summary>
    /// App 周期 Run 的状态汇总（design 4.3）。App Run 不能用现有的两个汇总：未采集的检测是 cancelled，
    /// API 的汇总会让这样的 Run 一直停在 running，国内网页的汇总按任务状态算、不认识截止。
    /// <list type="bullet">
    /// <item>还有 pending 或 running 的检测 → running；</item>
    /// <item>全部 succeeded → succeeded；全部 cancelled → cancelled；全部 failed → failed；</item>
    /// <item>其余（例如有成功也有未采集）→ partial_failed。</item>
    /// </list>
    /// </summary>
    /// <param name="total">检测总数，必须大于 0。</param>
    /// <param name="open">pending 与 running 的检测数。</param>
    /// <param name="succeeded">succeeded 的检测数。</param>
    /// <param name="failed">failed 的检测数。</param>
    /// <param name="cancelled">cancelled（未采集）的检测数。</param>
    /// <exception cref="ArgumentOutOfRangeException">检测总数不大于 0。</exception>
    public static string RunStatus(int total, int open, int succeeded, int failed, int cancelled)
    {
        if (total <= 0) throw new ArgumentOutOfRangeException(nameof(total), total, null);
        if (open > 0) return StatusRunning;
        if (succeeded == total) return StatusSucceeded;
        if (cancelled == total) return StatusCancelled;
        return failed == total ? StatusFailed : StatusPartialFailed;
    }
}

/// <summary>
/// App 任务最近一次“想领取但没领到”的原因（collector_tasks.last_block_reason）中，决定未采集原因码的取值。
/// 取值与领取接口返回空的原因（<see cref="AppLeaseEmptyReason"/> 的字符串）一致；领取到任务时应清空该列。
/// </summary>
public static class AppTaskBlockReasons
{
    /// <summary>余额不足：领取时可用积分不够一次检测。</summary>
    public const string CreditInsufficient = "credit_insufficient";

    /// <summary>绑定已停止（离线、需要登录、需要人工验证、契约或版本问题、被运营停用）。</summary>
    public const string BindingStopped = "binding_stopped";

    /// <summary>平台被运营暂停或熔断。</summary>
    public const string PlatformPaused = "platform_paused";

    /// <summary>平台的 App 入口已停用（判定为不可用）。</summary>
    public const string PlatformUnavailable = "platform_unavailable";
}

/// <summary>周期截止时仍未成功的 App 任务的未采集原因（design 4.3）。纯函数。</summary>
public static class AppNotCollectedRules
{
    /// <summary>
    /// 由任务截止时的状态得出未采集原因码（<see cref="AppNotCollectedCodes"/> 之一），按下列顺序判断：
    /// <list type="number">
    /// <item>失败次数已达 <see cref="AppFailureRules.MaxTaskFailures"/>（多次尝试都失败；这只是原因的判断，不是重试上限）→ <see cref="AppNotCollectedCodes.Failed"/>；</item>
    /// <item>最近一次没领到的原因：余额不足 → <see cref="AppNotCollectedCodes.Credit"/>；绑定已停止 → <see cref="AppNotCollectedCodes.Binding"/>；
    /// 平台暂停或不可用 → <see cref="AppNotCollectedCodes.Platform"/>；</item>
    /// <item>有过计入次数的失败 → <see cref="AppNotCollectedCodes.Failed"/>；</item>
    /// <item>最近一次失败码：使绑定停止的（需要登录、需要人工验证、契约与版本问题、设备离线）→ <see cref="AppNotCollectedCodes.Binding"/>；
    /// App 拒绝模拟器 → <see cref="AppNotCollectedCodes.Platform"/>；频率受限 → <see cref="AppNotCollectedCodes.Capacity"/>；
    /// 其他非空的失败码（尝试过但失败）→ <see cref="AppNotCollectedCodes.Failed"/>；</item>
    /// <item>其余（从未因别的原因被挡住，只是没有排到）→ <see cref="AppNotCollectedCodes.Capacity"/>。</item>
    /// </list>
    /// 未知的取值按“没有记录”处理，不会抛出异常。
    /// </summary>
    /// <param name="failureCount">任务计入次数的失败数（collector_tasks.failure_count）。</param>
    /// <param name="lastBlockReason">最近一次没领到的原因（collector_tasks.last_block_reason，<see cref="AppTaskBlockReasons"/>）；可空。</param>
    /// <param name="lastErrorCode">最近一次失败码（collector_tasks.last_error_code，<see cref="AppFailureCode"/> 的字符串）；可空。</param>
    public static string Resolve(int failureCount, string? lastBlockReason, string? lastErrorCode)
    {
        if (failureCount >= AppFailureRules.MaxTaskFailures) return AppNotCollectedCodes.Failed;
        switch (lastBlockReason?.Trim().ToLowerInvariant())
        {
            case AppTaskBlockReasons.CreditInsufficient: return AppNotCollectedCodes.Credit;
            case AppTaskBlockReasons.BindingStopped: return AppNotCollectedCodes.Binding;
            case AppTaskBlockReasons.PlatformPaused or AppTaskBlockReasons.PlatformUnavailable: return AppNotCollectedCodes.Platform;
        }
        if (failureCount > 0) return AppNotCollectedCodes.Failed;
        if (string.IsNullOrWhiteSpace(lastErrorCode)) return AppNotCollectedCodes.Capacity;
        if (!AppContractNames.TryParse<AppFailureCode>(lastErrorCode, out var failure)) return AppNotCollectedCodes.Failed;
        return failure switch
        {
            AppFailureCode.EmulatorRejected => AppNotCollectedCodes.Platform,
            AppFailureCode.RateLimited => AppNotCollectedCodes.Capacity,
            _ => AppFailureRules.BindingStopReason(failure) is not null ? AppNotCollectedCodes.Binding : AppNotCollectedCodes.Failed,
        };
    }

    /// <summary>未采集原因码对应的简短说明（visibility_checks.error_message）；未知的原因码返回通用说明。</summary>
    /// <param name="code">未采集原因码。</param>
    public static string Message(string code) => code switch
    {
        AppNotCollectedCodes.Capacity => "未采集：本周期内 App 采集容量不足，没有排到。",
        AppNotCollectedCodes.Credit => "未采集：本周期内可用积分不足。",
        AppNotCollectedCodes.Binding => "未采集：本周期内该平台的 App 账号不可用（离线或需要人工处理）。",
        AppNotCollectedCodes.Platform => "未采集：本周期内该平台的 App 采集被暂停或不可用。",
        AppNotCollectedCodes.Failed => "未采集：本周期内尝试采集但都失败。",
        _ => "未采集：本周期内没有完成 App 采集。",
    };
}
