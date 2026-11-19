using System.Text.Json.Serialization;

namespace Jogeoai.Domain;

/// <summary>订阅来源。</summary>
public enum SubscriptionSource
{
    /// <summary>注册时默认写入。</summary>
    [JsonStringEnumMemberName("registration")] Registration,
    /// <summary>迁移 024 为既有租户回填。</summary>
    [JsonStringEnumMemberName("migration")] Migration,
    /// <summary>卡密兑换。</summary>
    [JsonStringEnumMemberName("redeem")] Redeem,
    /// <summary>系统管理员指派。</summary>
    [JsonStringEnumMemberName("admin")] Admin,
    /// <summary>付费到期自动降级。</summary>
    [JsonStringEnumMemberName("expiry")] Expiry,
}

/// <summary>订阅事件类型。</summary>
public enum SubscriptionEventType
{
    /// <summary>注册写入免费体验版。</summary>
    [JsonStringEnumMemberName("registration")] Registration,
    /// <summary>迁移回填。</summary>
    [JsonStringEnumMemberName("migration")] Migration,
    /// <summary>卡密兑换（含同套餐续期）。</summary>
    [JsonStringEnumMemberName("redeem")] Redeem,
    /// <summary>管理员指派（含降级）。</summary>
    [JsonStringEnumMemberName("admin_assign")] AdminAssign,
    /// <summary>到期自动降为免费体验版。</summary>
    [JsonStringEnumMemberName("expire_downgrade")] ExpireDowngrade,
    /// <summary>周期结束：清零可用积分并发放新额度。</summary>
    [JsonStringEnumMemberName("period_rollover")] PeriodRollover,
    /// <summary>管理员修改套餐目录。</summary>
    [JsonStringEnumMemberName("plan_updated")] PlanUpdated,
}

/// <summary>卡密状态；expired 为派生状态（未使用且已过兑换截止）。</summary>
public enum PlanCodeStatus
{
    /// <summary>未使用。</summary>
    [JsonStringEnumMemberName("unused")] Unused,
    /// <summary>已兑换。</summary>
    [JsonStringEnumMemberName("redeemed")] Redeemed,
    /// <summary>已作废。</summary>
    [JsonStringEnumMemberName("revoked")] Revoked,
    /// <summary>已过期（未使用且 expiresAt 已过）。</summary>
    [JsonStringEnumMemberName("expired")] Expired,
}

/// <summary>套餐上限项。</summary>
public enum PlanLimitKind
{
    /// <summary>品牌数（租户内 active 品牌）。</summary>
    [JsonStringEnumMemberName("brands")] Brands,
    /// <summary>成员数（含所有者，active 与 invited）。</summary>
    [JsonStringEnumMemberName("members")] Members,
    /// <summary>监控提问数（租户内启用、未归档且品牌 active）。</summary>
    [JsonStringEnumMemberName("prompts")] Prompts,
    /// <summary>每品牌竞品数（active）。</summary>
    [JsonStringEnumMemberName("competitors")] Competitors,
}

/// <summary>套餐上限；null 表示不限。</summary>
/// <param name="MaxBrands">品牌数上限。</param>
/// <param name="MaxMembers">成员数上限（含所有者）。</param>
/// <param name="MaxPrompts">监控提问上限（租户内启用中的提问总数）。</param>
/// <param name="MaxCompetitorsPerBrand">每品牌竞品上限。</param>
public sealed record PlanLimitsView(int? MaxBrands, int? MaxMembers, int? MaxPrompts, int? MaxCompetitorsPerBrand);

/// <summary>套餐功能标记；本期只展示，不做后端拦截。</summary>
/// <param name="DomainAnalysis">域名分析、全网文章追踪。</param>
/// <param name="PromptRecommendation">智能提问推荐。</param>
/// <param name="ArticleRecommendation">高引用文章推荐。</param>
/// <param name="GeoAgent">GEO Agent。</param>
public sealed record PlanFeaturesView(bool DomainAnalysis, bool PromptRecommendation, bool ArticleRecommendation, bool GeoAgent);

/// <summary>套餐目录项。</summary>
/// <param name="Code">套餐编码（free_trial / starter / basic / standard / pro），固定不可改。</param>
/// <param name="Name">套餐名称。</param>
/// <param name="Tagline">定位文案。</param>
/// <param name="SortOrder">排序，越小越靠前。</param>
/// <param name="Recommended">是否标记为推荐。</param>
/// <param name="Limits">品牌 / 成员 / 提问 / 竞品上限。</param>
/// <param name="SignupCredits">注册 / 开通一次性赠送积分。</param>
/// <param name="MonthlyCredits">每月积分额度；0 表示无周期。</param>
/// <param name="DailyMonitoringLimit">大模型日级监控频次上限（仅展示）。</param>
/// <param name="EngineCount">AI 引擎检测数量（仅展示）。</param>
/// <param name="Features">功能标记。</param>
/// <param name="Enabled">是否可被指派 / 生成卡密；停用的套餐仍对已订阅租户生效。</param>
/// <param name="IsCurrent">是否为当前租户的生效套餐；无租户上下文时为 false。</param>
public sealed record SubscriptionPlanView(string Code, string Name, string Tagline, int SortOrder, bool Recommended,
    PlanLimitsView Limits, decimal SignupCredits, decimal MonthlyCredits, int DailyMonitoringLimit, int EngineCount,
    PlanFeaturesView Features, bool Enabled, bool IsCurrent);

/// <summary>当前额度周期。</summary>
/// <param name="Index">周期序号，从 1 开始。</param>
/// <param name="StartedAt">周期开始时间。</param>
/// <param name="EndsAt">周期结束时间；到达后 Worker 清零可用积分并发放下一周期额度。</param>
/// <param name="Quota">本周期发放额度。</param>
/// <param name="Used">本周期已消耗（周期内结算合计）。</param>
/// <param name="AvailableCredits">当前可用积分（含管理员额外入账）。</param>
/// <param name="ReservedCredits">当前预占中的积分（周期清零不影响）。</param>
public sealed record SubscriptionPeriodView(int Index, DateTimeOffset StartedAt, DateTimeOffset EndsAt, decimal Quota,
    decimal Used, decimal AvailableCredits, decimal ReservedCredits);

/// <summary>单项用量与上限。</summary>
/// <param name="Current">当前数量。</param>
/// <param name="Limit">上限；null 表示不限。</param>
public sealed record UsageItem(int Current, int? Limit);

/// <summary>每个品牌的竞品用量。</summary>
/// <param name="SiteId">品牌标识。</param>
/// <param name="BrandName">品牌名称。</param>
/// <param name="Current">该品牌 active 竞品数。</param>
/// <param name="Limit">每品牌竞品上限；null 表示不限。</param>
public sealed record BrandCompetitorUsage(Guid SiteId, string BrandName, int Current, int? Limit);

/// <summary>各上限的当前用量，以及积分余额。</summary>
/// <param name="Brands">品牌用量。</param>
/// <param name="Members">成员用量。</param>
/// <param name="Prompts">监控提问用量。</param>
/// <param name="Competitors">各 active 品牌的竞品用量。</param>
/// <param name="SignupCredits">套餐一次性赠送积分（免费体验版为 500）。</param>
/// <param name="SignupUsed">免费体验版自进入该套餐（注册、迁移回填或降级）以来已结算的积分，按消耗基线做差；有周期的套餐请看 Period.Used。</param>
/// <param name="AvailableCredits">当前可用积分。</param>
/// <param name="ReservedCredits">当前预占中的积分。</param>
public sealed record PlanUsageView(UsageItem Brands, UsageItem Members, UsageItem Prompts,
    IReadOnlyList<BrandCompetitorUsage> Competitors, decimal SignupCredits, decimal SignupUsed,
    decimal AvailableCredits, decimal ReservedCredits);

/// <summary>租户当前订阅。</summary>
/// <param name="SubscriptionId">订阅标识。</param>
/// <param name="TenantId">租户标识。</param>
/// <param name="Plan">生效套餐；付费套餐已到期但尚未被 Worker 降级时按免费体验版返回。</param>
/// <param name="Source">订阅来源。</param>
/// <param name="StartedAt">生效时间。</param>
/// <param name="ExpiresAt">到期时间；免费体验版为 null。</param>
/// <param name="Period">当前额度周期；免费体验版为 null。</param>
/// <param name="Usage">各上限用量与积分余额。</param>
public sealed record TenantSubscriptionView(Guid SubscriptionId, Guid TenantId, SubscriptionPlanView Plan,
    SubscriptionSource Source, DateTimeOffset StartedAt, DateTimeOffset? ExpiresAt, SubscriptionPeriodView? Period,
    PlanUsageView Usage);

/// <summary>订阅事件。</summary>
/// <param name="EventId">事件标识。</param>
/// <param name="Type">事件类型。</param>
/// <param name="FromPlanCode">变更前套餐。</param>
/// <param name="ToPlanCode">变更后套餐。</param>
/// <param name="ActorUserId">操作者；Worker 产生的事件为 null。</param>
/// <param name="PlanCodeId">关联卡密标识。</param>
/// <param name="PeriodIndex">关联周期序号。</param>
/// <param name="CreditsExpired">清零的可用积分。</param>
/// <param name="CreditsGranted">发放的积分。</param>
/// <param name="Reason">原因或备注。</param>
/// <param name="CreatedAt">发生时间。</param>
public sealed record SubscriptionEventView(Guid EventId, SubscriptionEventType Type, string? FromPlanCode, string? ToPlanCode,
    Guid? ActorUserId, Guid? PlanCodeId, int? PeriodIndex, decimal? CreditsExpired, decimal? CreditsGranted, string? Reason,
    DateTimeOffset CreatedAt);

/// <summary>管理员查看的租户订阅详情。</summary>
/// <param name="Subscription">当前订阅。</param>
/// <param name="Events">最近事件（时间倒序，最多 50 条）。</param>
public sealed record AdminTenantSubscriptionView(TenantSubscriptionView Subscription, IReadOnlyList<SubscriptionEventView> Events);

/// <summary>卡密兑换请求。</summary>
/// <param name="Code">卡密明文，格式 JG-XXXX-XXXX-XXXX-XXXX；大小写与空白不敏感。</param>
public sealed record RedeemPlanCodeRequest(string Code);

/// <summary>批量生成卡密的请求。</summary>
/// <param name="PlanCode">套餐编码；不能为 free_trial。</param>
/// <param name="Months">兑换后的时长（月），1–36。</param>
/// <param name="Quantity">生成数量，1–500。</param>
/// <param name="ExpiresAt">卡密兑换截止时间；null 表示不限。</param>
/// <param name="Note">备注，最多 200 字符。</param>
public sealed record PlanCodeBatchRequest(string PlanCode, int Months, int Quantity, DateTimeOffset? ExpiresAt = null, string? Note = null);

/// <summary>卡密批次。</summary>
/// <param name="BatchId">批次标识。</param>
/// <param name="PlanCode">套餐编码。</param>
/// <param name="Months">时长（月）。</param>
/// <param name="Quantity">数量。</param>
/// <param name="ExpiresAt">兑换截止时间。</param>
/// <param name="Note">备注。</param>
/// <param name="CreatedBy">创建人。</param>
/// <param name="CreatedAt">创建时间。</param>
public sealed record PlanCodeBatchView(Guid BatchId, string PlanCode, int Months, int Quantity, DateTimeOffset? ExpiresAt,
    string? Note, Guid CreatedBy, DateTimeOffset CreatedAt);

/// <summary>批量生成卡密的结果；明文只在此响应中返回一次，数据库只保存哈希与末 4 位。</summary>
/// <param name="Batch">批次信息。</param>
/// <param name="Codes">卡密明文数组。</param>
public sealed record PlanCodeBatchResult(PlanCodeBatchView Batch, IReadOnlyList<string> Codes);

/// <summary>卡密（不含明文）。</summary>
/// <param name="CodeId">卡密标识。</param>
/// <param name="BatchId">批次标识。</param>
/// <param name="PlanCode">套餐编码。</param>
/// <param name="Months">时长（月）。</param>
/// <param name="CodeHint">末 4 位。</param>
/// <param name="Status">状态；expired 为派生状态。</param>
/// <param name="ExpiresAt">兑换截止时间。</param>
/// <param name="RedeemedTenantId">兑换租户。</param>
/// <param name="RedeemedBy">兑换用户。</param>
/// <param name="RedeemedAt">兑换时间。</param>
/// <param name="RevokedAt">作废时间。</param>
/// <param name="CreatedAt">生成时间。</param>
public sealed record PlanCodeView(Guid CodeId, Guid BatchId, string PlanCode, int Months, string CodeHint, PlanCodeStatus Status,
    DateTimeOffset? ExpiresAt, Guid? RedeemedTenantId, Guid? RedeemedBy, DateTimeOffset? RedeemedAt, DateTimeOffset? RevokedAt,
    DateTimeOffset CreatedAt);

/// <summary>卡密分页。</summary>
/// <param name="Items">本页卡密。</param>
/// <param name="NextCursor">下一页游标；没有更多时为 null。</param>
public sealed record PlanCodePage(IReadOnlyList<PlanCodeView> Items, string? NextCursor);

/// <summary>管理员指派套餐的请求；months 与 expiresAt 二选一（目标为 free_trial 时两者都省略）。</summary>
/// <param name="PlanCode">目标套餐编码。</param>
/// <param name="Months">自现在起的时长（月），1–36；同套餐时在现有到期时间上顺延。</param>
/// <param name="ExpiresAt">显式到期时间（UTC），必须晚于现在。</param>
/// <param name="Reason">指派原因，1–500 字符。</param>
public sealed record AssignSubscriptionRequest(string PlanCode, int? Months, DateTimeOffset? ExpiresAt, string Reason);

/// <summary>管理员修改套餐目录；省略的字段保持不变。limits 与 features 提供时整体替换。</summary>
/// <param name="Name">套餐名称，1–64 字符。</param>
/// <param name="Tagline">定位文案，最多 500 字符。</param>
/// <param name="Recommended">推荐标记。</param>
/// <param name="Enabled">是否可被指派 / 生成卡密。</param>
/// <param name="Limits">上限；项为 null 表示不限。</param>
/// <param name="SignupCredits">一次性赠送积分，≥ 0。</param>
/// <param name="MonthlyCredits">每月额度，≥ 0；修改不追溯已发放的周期。</param>
/// <param name="DailyMonitoringLimit">日级监控频次（仅展示）。</param>
/// <param name="EngineCount">AI 引擎数量（仅展示）。</param>
/// <param name="Features">功能标记（仅展示）。</param>
public sealed record SubscriptionPlanUpdate(string? Name = null, string? Tagline = null, bool? Recommended = null, bool? Enabled = null,
    PlanLimitsView? Limits = null, decimal? SignupCredits = null, decimal? MonthlyCredits = null, int? DailyMonitoringLimit = null,
    int? EngineCount = null, PlanFeaturesView? Features = null);

/// <summary>套餐上限校验失败（409 PLAN_LIMIT_EXCEEDED），携带前端提示升级所需的结构化字段。</summary>
public sealed class PlanLimitExceededException(string planCode, string planName, PlanLimitKind limit, int limitValue, int currentValue)
    : CollectorStoreException("PLAN_LIMIT_EXCEEDED", Describe(planName, limit, limitValue, currentValue), 409)
{
    /// <summary>当前生效套餐编码。</summary>
    public string PlanCode { get; } = planCode;

    /// <summary>超出的上限项。</summary>
    public PlanLimitKind Limit { get; } = limit;

    /// <summary>套餐上限值。</summary>
    public int LimitValue { get; } = limitValue;

    /// <summary>当前已有数量。</summary>
    public int CurrentValue { get; } = currentValue;

    private static string Describe(string planName, PlanLimitKind limit, int limitValue, int currentValue)
    {
        var item = limit switch
        {
            PlanLimitKind.Brands => "个品牌",
            PlanLimitKind.Members => "名成员",
            PlanLimitKind.Prompts => "条监控提问",
            PlanLimitKind.Competitors => "个竞品（每品牌）",
            _ => "项",
        };
        return $"当前套餐（{planName}）最多 {limitValue} {item}，已有 {currentValue}；请升级套餐。";
    }
}
