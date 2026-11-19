using System.Text.Json.Serialization;

namespace Jogeoai.Domain;

/// <summary>Cloro 账号 key 的系统维护状态；启用和归档由管理员维护，与状态正交。</summary>
public enum CloroKeyStatus
{
    /// <summary>新录入或余额未知，可被选用。</summary>
    [JsonStringEnumMemberName("unknown")]
    Unknown,
    /// <summary>最近一次查询余额高于阈值，可被选用。</summary>
    [JsonStringEnumMemberName("active")]
    Active,
    /// <summary>积分已耗尽；到达周期重置时间或余额恢复后重新成为候选。</summary>
    [JsonStringEnumMemberName("exhausted")]
    Exhausted,
    /// <summary>限流、并发打满或非积分原因被拒后短暂冷却，冷却结束后可再选。</summary>
    [JsonStringEnumMemberName("cooling")]
    Cooling,
    /// <summary>Cloro 返回 401，key 无效或已作废，需要管理员处理。</summary>
    [JsonStringEnumMemberName("invalid")]
    Invalid,
}

/// <summary>Cloro key 池整体状态。</summary>
public enum CloroKeyPoolStatus
{
    /// <summary>有可用账号且已知余额充足。</summary>
    [JsonStringEnumMemberName("healthy")]
    Healthy,
    /// <summary>可用账号的已知余额总和低于告警阈值。</summary>
    [JsonStringEnumMemberName("low")]
    Low,
    /// <summary>没有任何可用账号。</summary>
    [JsonStringEnumMemberName("exhausted")]
    Exhausted,
}

/// <summary>Cloro 账号 key 的管理视图；只返回 key 后 4 位，不返回完整 key。</summary>
/// <param name="Id">key 池记录标识。</param>
/// <param name="AccountLabel">账号名称。</param>
/// <param name="KeyHint">key 后 4 位。</param>
/// <param name="Priority">优先级，越小越先用。</param>
/// <param name="Enabled">是否允许新任务使用；停用不影响已提交任务的轮询。</param>
/// <param name="Status">系统维护状态。</param>
/// <param name="Available">当前是否可被选中（已考虑冷却到期、周期重置与低余额阈值）。</param>
/// <param name="CreditsRemaining">最近一次查询到的剩余积分。</param>
/// <param name="CreditsPerCycle">每个计费周期的额度。</param>
/// <param name="CycleResetsAt">周期重置的 UTC 时间。</param>
/// <param name="CreditsCheckedAt">最近一次余额查询的 UTC 时间；为空表示等待刷新。</param>
/// <param name="CooldownUntil">冷却结束的 UTC 时间。</param>
/// <param name="LastUsedAt">最近一次成功提交的 UTC 时间。</param>
/// <param name="LastErrorCode">最近一次稳定错误码，不含 Cloro 原始错误文本。</param>
/// <param name="LastErrorAt">最近一次错误的 UTC 时间。</param>
/// <param name="InFlightTaskCount">引用该 key 且已提交、未终态的任务数。</param>
/// <param name="ArchivedAt">归档 UTC 时间；未归档为空。</param>
/// <param name="CreatedAt">录入 UTC 时间。</param>
/// <param name="UpdatedAt">最近更新 UTC 时间。</param>
public sealed record CloroKeyView(Guid Id, string AccountLabel, string KeyHint, int Priority, bool Enabled,
    CloroKeyStatus Status, bool Available, decimal? CreditsRemaining, decimal? CreditsPerCycle, DateTimeOffset? CycleResetsAt,
    DateTimeOffset? CreditsCheckedAt, DateTimeOffset? CooldownUntil, DateTimeOffset? LastUsedAt, string? LastErrorCode,
    DateTimeOffset? LastErrorAt, int InFlightTaskCount, DateTimeOffset? ArchivedAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Cloro key 池概览。</summary>
/// <param name="TotalKeys">未归档的账号数（含停用）。</param>
/// <param name="AvailableKeys">当前可被选中的账号数。</param>
/// <param name="ExhaustedKeys">启用中、已耗尽且尚未恢复的账号数。</param>
/// <param name="CoolingKeys">启用中、仍在冷却期的账号数。</param>
/// <param name="InvalidKeys">启用中、被判定为无效的账号数。</param>
/// <param name="KnownCreditsRemaining">可用账号中已知余额的总和；没有已知余额时为空。</param>
/// <param name="EarliestCycleResetsAt">启用账号中最早的未来周期重置时间。</param>
/// <param name="NextAvailableAt">不可用账号中最早恢复的 UTC 时间。</param>
/// <param name="Status">池状态：正常、余额偏低或已耗尽。</param>
public sealed record CloroKeyPoolSummary(int TotalKeys, int AvailableKeys, int ExhaustedKeys, int CoolingKeys, int InvalidKeys,
    decimal? KnownCreditsRemaining, DateTimeOffset? EarliestCycleResetsAt, DateTimeOffset? NextAvailableAt, CloroKeyPoolStatus Status);

/// <summary>录入一个 Cloro 账号的命令。</summary>
/// <param name="AccountLabel">账号名称，唯一，最长 64 字符。</param>
/// <param name="ApiKey">API Key，去除首尾空白后保存。</param>
/// <param name="Priority">优先级，越小越先用。</param>
/// <param name="Enabled">是否启用。</param>
public sealed record CreateCloroKeyCommand(string AccountLabel, string ApiKey, int Priority, bool Enabled);

/// <summary>修改 Cloro 账号的命令；key 本身不可修改。</summary>
/// <param name="Id">key 池记录标识。</param>
/// <param name="AccountLabel">新的账号名称；为空表示不修改。</param>
/// <param name="Priority">新的优先级；为空表示不修改。</param>
/// <param name="Enabled">新的启用状态；为空表示不修改。</param>
public sealed record UpdateCloroKeyCommand(Guid Id, string? AccountLabel, int? Priority, bool? Enabled);

/// <summary>归档 Cloro 账号的结果。</summary>
/// <param name="Archived">是否已归档（含此前已归档）。</param>
/// <param name="InFlightTaskCount">阻止归档的在途任务数。</param>
public sealed record CloroKeyArchiveResult(bool Archived, int InFlightTaskCount);

/// <summary>Cloro key 与账号名称的规范化规则；异常信息不包含 key。</summary>
public static class CloroKeyFormat
{
    /// <summary>default 账号名称：迁移导入的单例 key，也是池化前历史任务的轮询回退。</summary>
    public const string DefaultAccountLabel = "default";

    /// <summary>去除首尾空白并校验 key；为空、过长或含空白/控制字符时抛出参数异常。</summary>
    /// <param name="value">原始 key。</param>
    public static string NormalizeApiKey(string? value)
    {
        var key = value?.Trim() ?? "";
        if (key.Length is < 8 or > 1024 || key.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
            throw new ArgumentException("apiKey 必须为 8–1024 个非空白字符。");
        return key;
    }

    /// <summary>去除首尾空白并校验账号名称（1–64 字符，不含控制字符）。</summary>
    /// <param name="value">原始账号名称。</param>
    public static string NormalizeLabel(string? value)
    {
        var label = value?.Trim() ?? "";
        if (label.Length is < 1 or > 64 || label.Any(char.IsControl))
            throw new ArgumentException("accountLabel 必须为 1–64 个字符且不含控制字符。");
        return label;
    }

    /// <summary>校验优先级范围（0–1000000）。</summary>
    /// <param name="priority">优先级。</param>
    public static int ValidatePriority(int priority) => priority is >= 0 and <= 1_000_000
        ? priority : throw new ArgumentException("priority 必须在 0 到 1000000 之间。");

    /// <summary>生成展示用的 key 后 4 位；短 key 只返回掩码。</summary>
    /// <param name="apiKey">规范化后的 key。</param>
    public static string Hint(string apiKey) => apiKey.Length > 8 ? apiKey[^4..] : "****";
}

/// <summary>Cloro 账号状态转换规则。</summary>
public static class CloroKeyStatusRules
{
    /// <summary>
    /// 余额查询成功后的新状态：余额不高于阈值为耗尽；仍在冷却期的保持冷却；
    /// 已耗尽的账号只有在余额确实增加（充值）、周期已推进（重置）或此前余额未知时才恢复，
    /// 避免“余额为正但不足以支付单次任务”的账号在拒绝与刷新之间反复切换。
    /// </summary>
    /// <param name="current">当前状态。</param>
    /// <param name="coolingActive">冷却期是否尚未结束。</param>
    /// <param name="previousRemaining">刷新前记录的剩余积分。</param>
    /// <param name="previousCycleResetsAt">刷新前记录的周期重置时间。</param>
    /// <param name="remaining">查询到的剩余积分；为空时保持原状态。</param>
    /// <param name="cycleResetsAt">查询到的周期重置时间。</param>
    /// <param name="threshold">低余额阈值。</param>
    public static CloroKeyStatus AfterCreditsRefresh(CloroKeyStatus current, bool coolingActive, decimal? previousRemaining,
        DateTimeOffset? previousCycleResetsAt, decimal? remaining, DateTimeOffset? cycleResetsAt, decimal threshold)
    {
        if (remaining is null) return current;
        if (remaining <= threshold) return CloroKeyStatus.Exhausted;
        if (current == CloroKeyStatus.Cooling && coolingActive) return CloroKeyStatus.Cooling;
        // A cycle counts as advanced only when the reset time moved by more than storage rounding.
        if (current == CloroKeyStatus.Exhausted && previousRemaining is { } before && remaining <= before
            && !(cycleResetsAt is { } after && (previousCycleResetsAt is null || after - previousCycleResetsAt.Value > TimeSpan.FromMinutes(1))))
            return CloroKeyStatus.Exhausted;
        return CloroKeyStatus.Active;
    }

    /// <summary>把数据库中的状态字符串转换为枚举。</summary>
    /// <param name="value">数据库状态值。</param>
    public static CloroKeyStatus Parse(string value) => value switch
    {
        "active" => CloroKeyStatus.Active,
        "exhausted" => CloroKeyStatus.Exhausted,
        "cooling" => CloroKeyStatus.Cooling,
        "invalid" => CloroKeyStatus.Invalid,
        _ => CloroKeyStatus.Unknown,
    };

    /// <summary>把状态枚举转换为数据库字符串。</summary>
    /// <param name="status">状态。</param>
    public static string ToStorage(CloroKeyStatus status) => status switch
    {
        CloroKeyStatus.Active => "active",
        CloroKeyStatus.Exhausted => "exhausted",
        CloroKeyStatus.Cooling => "cooling",
        CloroKeyStatus.Invalid => "invalid",
        _ => "unknown",
    };
}
