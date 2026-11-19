using System.Reflection;
using System.Text.Json.Serialization;

namespace Jogeoai.Domain;

/// <summary>
/// App 渠道枚举的传输 / 存储字符串。取值就是枚举成员上 <see cref="JsonStringEnumMemberNameAttribute"/> 声明的名字，
/// 因此接口返回的字符串与写入数据库（受 049 的 CHECK 约束）的字符串必然相同。
/// </summary>
public static class AppContractNames
{
    /// <summary>采集器上报绑定健康时表示“正常”的取值；其他取值是 <see cref="AppBindingStopReason"/> 的字符串。</summary>
    public const string HealthOk = "ok";

    /// <summary>把枚举值转换为稳定的小写传输 / 存储字符串。</summary>
    /// <typeparam name="TEnum">带 JSON 成员名的枚举。</typeparam>
    /// <param name="value">枚举值。</param>
    /// <exception cref="ArgumentOutOfRangeException">不是已定义的枚举成员。</exception>
    public static string ToStorage<TEnum>(TEnum value) where TEnum : struct, Enum
        => Names<TEnum>.ByValue.TryGetValue(value, out var name)
            ? name
            : throw new ArgumentOutOfRangeException(nameof(value), value, null);

    /// <summary>解析传输 / 存储字符串（忽略首尾空白与大小写）；空值或未知取值返回 false。</summary>
    /// <typeparam name="TEnum">带 JSON 成员名的枚举。</typeparam>
    /// <param name="value">字符串。</param>
    /// <param name="result">解析结果。</param>
    public static bool TryParse<TEnum>(string? value, out TEnum result) where TEnum : struct, Enum
    {
        result = default;
        var normalized = value?.Trim();
        return !string.IsNullOrEmpty(normalized) && Names<TEnum>.ByName.TryGetValue(normalized, out result);
    }

    /// <summary>枚举的全部字符串取值，按成员声明顺序排列。</summary>
    /// <typeparam name="TEnum">带 JSON 成员名的枚举。</typeparam>
    public static IReadOnlyList<string> All<TEnum>() where TEnum : struct, Enum => Names<TEnum>.Ordered;

    private static class Names<TEnum> where TEnum : struct, Enum
    {
        internal static readonly IReadOnlyList<string> Ordered;
        internal static readonly Dictionary<TEnum, string> ByValue = [];
        internal static readonly Dictionary<string, TEnum> ByName = new(StringComparer.OrdinalIgnoreCase);

        static Names()
        {
            var ordered = new List<string>();
            foreach (var field in typeof(TEnum).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                var name = field.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name
                    ?? throw new InvalidOperationException($"{typeof(TEnum).Name}.{field.Name} 没有声明 JSON 成员名。");
                var value = (TEnum)field.GetValue(null)!;
                ByValue.Add(value, name);
                ByName.Add(name, value);
                ordered.Add(name);
            }
            Ordered = ordered;
        }
    }
}

/// <summary>
/// App 检测“未采集”的原因码。周期截止时仍未成功的 App 检测置为 cancelled（不是 failed：未采集不计为采集失败、不扣费），
/// 原因码写入 visibility_checks.error_code。
/// </summary>
public static class AppNotCollectedCodes
{
    /// <summary>容量不足：周期内一直没有排到。</summary>
    public const string Capacity = "APP_NOT_COLLECTED_CAPACITY";

    /// <summary>余额不足：每次要领取时可用积分都不够。</summary>
    public const string Credit = "APP_NOT_COLLECTED_CREDIT";

    /// <summary>绑定不可用：该平台的绑定在周期内不可用（离线、需要登录、需要人工验证、契约问题）。</summary>
    public const string Binding = "APP_NOT_COLLECTED_BINDING";

    /// <summary>平台不可用：平台被暂停或判定为不可用。</summary>
    public const string Platform = "APP_NOT_COLLECTED_PLATFORM";

    /// <summary>采集失败：尝试过但都失败，重试次数用尽。</summary>
    public const string Failed = "APP_NOT_COLLECTED_FAILED";

    /// <summary>全部未采集原因码。</summary>
    public static IReadOnlyList<string> All { get; } = [Capacity, Credit, Binding, Platform, Failed];

    /// <summary>错误码是否为 App 未采集原因码（区分大小写）。</summary>
    /// <param name="errorCode">visibility_checks.error_code。</param>
    public static bool IsNotCollected(string? errorCode) => errorCode is not null && All.Contains(errorCode, StringComparer.Ordinal);
}

/// <summary>平台的 App 入口不可用时的原因码（collector_platform_entries.unavailable_reason，目录的 unavailableReason.code）。</summary>
public static class AppUnavailableReasons
{
    /// <summary>App 的聊天内容读不到（文心一言的入口种子）。</summary>
    public const string ContentUnreadable = "APP_CONTENT_UNREADABLE";

    /// <summary>App 拒绝在模拟器上运行或要求设备验证。</summary>
    public const string EmulatorRejected = "APP_EMULATOR_REJECTED";
}

/// <summary>App 失败上报的处理规则（纯函数）：失败码对绑定和任务失败次数的影响。</summary>
public static class AppFailureRules
{
    /// <summary>
    /// 截止时判断未采集原因用的失败次数：达到这个次数的任务记为“尝试采集但都失败”（<see cref="AppNotCollectedRules.Resolve"/>）。
    /// 它不是重试上限：失败不会因为次数让任务终态，任务只受“每台设备尝试一次”和截止时点约束。
    /// </summary>
    public const int MaxTaskFailures = 3;

    /// <summary>
    /// 该失败码使绑定停止时的停止原因：需要登录、需要人工验证、App 拒绝模拟器、契约变化、契约未审阅、版本不一致、设备离线各自对应同名原因
    /// （设备离线之后绑定上报 ok 即自动恢复，需要运营恢复的原因不被它覆盖，见 <see cref="AppPoolRules.NextStopReason"/>）；
    /// 频率受限（冷却，不停止）与 App 崩溃、采集超时（绑定状态不变）返回空值。
    /// </summary>
    /// <param name="code">失败码。</param>
    public static AppBindingStopReason? BindingStopReason(AppFailureCode code) => code switch
    {
        AppFailureCode.LoginRequired => AppBindingStopReason.LoginRequired,
        AppFailureCode.ChallengeRequired => AppBindingStopReason.ChallengeRequired,
        AppFailureCode.EmulatorRejected => AppBindingStopReason.EmulatorRejected,
        AppFailureCode.ContractChanged => AppBindingStopReason.ContractChanged,
        AppFailureCode.ContractUnverified => AppBindingStopReason.ContractUnverified,
        AppFailureCode.VersionMismatch => AppBindingStopReason.VersionMismatch,
        AppFailureCode.DeviceOffline => AppBindingStopReason.DeviceOffline,
        AppFailureCode.RateLimited or AppFailureCode.AppCrashed or AppFailureCode.CollectionTimeout => null,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, null),
    };

    /// <summary>
    /// 该失败是否计入任务的失败次数。绑定级原因（需要登录、需要人工验证、频率受限、App 拒绝模拟器、契约与版本问题、设备离线）不计入：
    /// 任务重新排队、不退避，等待其他绑定或该绑定恢复，直到截止（总 PRD AC-C11）。App 崩溃、采集超时计入。
    /// </summary>
    /// <param name="code">失败码。</param>
    public static bool CountsTowardTaskFailures(AppFailureCode code) => code switch
    {
        AppFailureCode.AppCrashed or AppFailureCode.CollectionTimeout => true,
        AppFailureCode.LoginRequired or AppFailureCode.ChallengeRequired or AppFailureCode.RateLimited
            or AppFailureCode.EmulatorRejected or AppFailureCode.ContractChanged or AppFailureCode.ContractUnverified
            or AppFailureCode.VersionMismatch or AppFailureCode.DeviceOffline => false,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, null),
    };
}
