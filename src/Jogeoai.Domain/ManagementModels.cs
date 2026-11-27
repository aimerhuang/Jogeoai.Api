using System.Text.Json.Serialization;

namespace Jogeoai.Domain;

/// <summary>品牌配置写入请求；Site ID 是品牌稳定标识。</summary>
/// <param name="Name">品牌名称。</param>
/// <param name="PrimaryDomain">可选主域名；省略、null 或空白表示没有官网。</param>
/// <param name="Aliases">品牌别名。</param>
/// <param name="AdditionalDomains">附加域名。</param>
/// <param name="DefaultCountry">默认国家代码。</param>
/// <param name="DefaultLocale">默认语言标识。</param>
/// <param name="TimeZone">默认 IANA 时区。</param>
/// <param name="Status">品牌状态。</param>
/// <param name="ExpectedVersion">绑定现有 Site 时的主体配置版本。</param>
/// <param name="SiteId">要绑定的现有 Site ID。</param>
/// <param name="ReportTitle">报告标题。</param>
/// <param name="IncludeSubdomains">是否将主域名下的子域名视为品牌域名；绑定已有 Site 时省略表示保留原值。</param>
/// <param name="Remark">品牌备注（团队内部标注），最长 200 字。</param>
/// <param name="Description">品牌描述，新建时必填，长度 1–2000 字；绑定已有品牌时可省略。</param>
/// <param name="CreationRequestId">可选创建请求标识；同团队同标识同内容重试返回同一品牌，不同内容返回冲突；不用于绑定已有 Site。</param>
public sealed record BrandWrite(string Name, string? PrimaryDomain = null, IReadOnlyList<string>? Aliases = null,
    IReadOnlyList<string>? AdditionalDomains = null, string DefaultCountry = "US", string DefaultLocale = "en-US",
    string TimeZone = "UTC", string Status = "active", uint? ExpectedVersion = null, Guid? SiteId = null,
    string? ReportTitle = null, bool? IncludeSubdomains = null, string? Remark = null, string? Description = null,
    Guid? CreationRequestId = null);

/// <summary>品牌局部更新；省略字段保留已有配置。</summary>
/// <param name="Name">可选品牌名称。</param>
/// <param name="PrimaryDomain">可选主域名；省略或 null 保留原值，空字符串清空，非空设置官网域名。</param>
/// <param name="Aliases">可选完整别名集合。</param>
/// <param name="AdditionalDomains">可选完整附加域名集合。</param>
/// <param name="DefaultCountry">可选默认国家代码。</param>
/// <param name="DefaultLocale">可选默认语言标识。</param>
/// <param name="TimeZone">可选 IANA 时区。</param>
/// <param name="Status">可选状态。</param>
/// <param name="ExpectedVersion">必填的当前主体配置版本。</param>
/// <param name="ReportTitle">可选报告标题。</param>
/// <param name="IncludeSubdomains">可选的子域名匹配开关。</param>
/// <param name="Remark">可选品牌备注，最长 200 字；传空串清空。只改备注、描述或报告标题时不递增主体配置版本。</param>
/// <param name="Description">可选品牌描述，最长 2000 字；传空串清空。</param>
public sealed record BrandPatch(string? Name = null, string? PrimaryDomain = null, IReadOnlyList<string>? Aliases = null,
    IReadOnlyList<string>? AdditionalDomains = null, string? DefaultCountry = null, string? DefaultLocale = null,
    string? TimeZone = null, string? Status = null, uint? ExpectedVersion = null, string? ReportTitle = null,
    bool? IncludeSubdomains = null, string? Remark = null, string? Description = null)
{
    /// <summary>补丁是否只包含展示字段（备注、描述、报告标题）；此类更新不进入主体配置快照。</summary>
    public bool IsDisplayOnly => (Remark is not null || Description is not null || ReportTitle is not null)
        && Name is null && PrimaryDomain is null && Aliases is null && AdditionalDomains is null
        && DefaultCountry is null && DefaultLocale is null && TimeZone is null && Status is null && IncludeSubdomains is null;
}

/// <summary>品牌配置与主体版本。</summary>
/// <param name="SiteId">品牌及工作区标识。</param>
/// <param name="Name">品牌名称。</param>
/// <param name="PrimaryDomain">主域名；无官网时为 null。</param>
/// <param name="Aliases">品牌别名。</param>
/// <param name="AdditionalDomains">附加域名。</param>
/// <param name="DefaultCountry">默认国家代码。</param>
/// <param name="DefaultLocale">默认语言标识。</param>
/// <param name="TimeZone">默认 IANA 时区。</param>
/// <param name="Status">品牌状态。</param>
/// <param name="SubjectConfigVersion">主体配置版本。</param>
/// <param name="CreatedAt">创建 UTC 时间。</param>
/// <param name="UpdatedAt">最近更新 UTC 时间。</param>
/// <param name="TargetConfigVersion">目标配置版本。</param>
/// <param name="ReportTitle">报告标题。</param>
/// <param name="IncludeSubdomains">是否包含所有子域名。</param>
/// <param name="Remark">品牌备注（团队内部标注）；未填写时为空。</param>
/// <param name="Description">品牌描述；未填写时为空。</param>
public sealed record BrandView(Guid SiteId, string Name, string? PrimaryDomain, IReadOnlyList<string> Aliases,
    IReadOnlyList<string> AdditionalDomains, string DefaultCountry, string DefaultLocale, string TimeZone,
    string Status, uint SubjectConfigVersion, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    uint TargetConfigVersion = 1, string? ReportTitle = null, bool IncludeSubdomains = false,
    string? Remark = null, string? Description = null);

/// <summary>竞品配置写入请求。</summary>
/// <param name="Name">竞品名称。</param>
/// <param name="Aliases">竞品别名。</param>
/// <param name="Domains">竞品域名。</param>
/// <param name="Status">竞品状态。</param>
/// <param name="ExpectedVersion">绑定主体配置的预期版本。</param>
public sealed record CompetitorWrite(string Name, IReadOnlyList<string>? Aliases = null,
    IReadOnlyList<string>? Domains = null, string Status = "active", uint? ExpectedVersion = null);

/// <summary>竞品局部更新；省略字段保留已有配置。</summary>
/// <param name="Name">可选竞品名称。</param>
/// <param name="Aliases">可选完整别名集合。</param>
/// <param name="Domains">可选完整域名集合。</param>
/// <param name="Status">可选状态。</param>
/// <param name="ExpectedVersion">必填的当前主体配置版本。</param>
public sealed record CompetitorPatch(string? Name = null, IReadOnlyList<string>? Aliases = null,
    IReadOnlyList<string>? Domains = null, string? Status = null, uint? ExpectedVersion = null);

/// <summary>具有稳定标识的竞品配置。</summary>
/// <param name="CompetitorId">竞品稳定标识。</param>
/// <param name="Name">竞品名称。</param>
/// <param name="Aliases">竞品别名。</param>
/// <param name="Domains">竞品域名。</param>
/// <param name="Status">竞品状态。</param>
/// <param name="CreatedAt">创建 UTC 时间。</param>
/// <param name="UpdatedAt">最近更新 UTC 时间。</param>
public sealed record CompetitorView(Guid CompetitorId, string Name, IReadOnlyList<string> Aliases,
    IReadOnlyList<string> Domains, string Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Prompt 新版本或新建内容。</summary>
/// <param name="Content">问题正文。</param>
/// <param name="Locale">BCP-47 语言标识。</param>
/// <param name="Enabled">是否进入新采集。</param>
/// <param name="TagIds">关联标签标识。</param>
/// <param name="BrandedStatus">人工指定品牌词分类。</param>
/// <param name="BrandedSource">人工分类来源。</param>
/// <param name="GroupId">可选的提问分组标识。</param>
/// <param name="TargetKeys">可选的 AI 平台范围（采集目标键 targetKey）。省略表示全部平台（all）；非空表示只在这些平台监测（custom），
/// 须为 1–20 个不重复、品牌当前启用且可参与每日监控的目标键，否则返回 400 PROMPT_TARGET_INVALID；空数组无效。
/// 已开放 App 渠道的团队还可以加入该品牌已启用的 App 目标键（每个平台一个，不占这 20 个）；App 目标键只决定该提问是否进入 App 渠道，
/// 不参与每日监控的检测与计费，范围里只有 App 目标键的提问没有每日监控检测。</param>
public sealed record PromptWrite(string Content, string Locale = "en-US", bool Enabled = true,
    IReadOnlyList<Guid>? TagIds = null, string? BrandedStatus = null, string? BrandedSource = null,
    Guid? GroupId = null, IReadOnlyList<string>? TargetKeys = null);

/// <summary>Prompt 局部更新；省略字段保留当前版本。</summary>
/// <param name="Content">可选问题正文。</param>
/// <param name="Locale">可选语言标识。</param>
/// <param name="Enabled">可选启用状态。</param>
/// <param name="TagIds">可选完整标签集合。</param>
/// <param name="BrandedStatus">可选人工品牌词分类。</param>
/// <param name="BrandedSource">可选分类来源。</param>
/// <param name="Archive">是否归档。</param>
/// <param name="ExpectedVersion">必填的当前配置版本。</param>
/// <param name="GroupId">可选的目标分组标识；省略表示保留当前分组。</param>
/// <param name="ClearGroup">是否清除当前分组；与 GroupId 不能同时为 true。</param>
/// <param name="TargetKeys">可选的 AI 平台范围。省略表示保持不变；非空表示改为只在这些平台监测（custom，规则同创建）；
/// 空数组表示恢复为全部平台（all）。平台范围变化只递增 configVersion，不产生新的提问版本。</param>
/// <param name="ResetTargetScope">为 true 时恢复为全部平台（all），是空数组 TargetKeys 的显式写法；不能与非空 TargetKeys 同时指定。</param>
public sealed record PromptPatch(string? Content = null, string? Locale = null, bool? Enabled = null,
    IReadOnlyList<Guid>? TagIds = null, string? BrandedStatus = null, string? BrandedSource = null,
    bool Archive = false, int? ExpectedVersion = null, Guid? GroupId = null, bool ClearGroup = false,
    IReadOnlyList<string>? TargetKeys = null, bool? ResetTargetScope = null);

/// <summary>Prompt 当前版本及标签。</summary>
/// <param name="PromptId">Prompt 稳定标识。</param>
/// <param name="Content">当前问题正文。</param>
/// <param name="Version">内容与标签快照版本。</param>
/// <param name="Locale">语言标识。</param>
/// <param name="Enabled">是否进入新采集。</param>
/// <param name="Tags">当前版本标签。</param>
/// <param name="BrandedStatus">品牌词分类。</param>
/// <param name="BrandedSource">分类来源。</param>
/// <param name="CreatedAt">创建 UTC 时间。</param>
/// <param name="UpdatedAt">最近更新 UTC 时间。</param>
/// <param name="ArchivedAt">归档 UTC 时间。</param>
/// <param name="ConfigVersion">乐观并发配置版本。</param>
/// <param name="GroupId">所属提问分组标识；未分组时为空。</param>
/// <param name="TargetScope">AI 平台范围：all 表示品牌启用的全部可监控平台；custom 表示只在 TargetKeys 所列平台监测。</param>
/// <param name="TargetKeys">custom 时选中的采集目标键（按序数排序）；all 时为空。实际监测平台为它与品牌当前启用平台的交集。</param>
/// <param name="CreationSource">创建来源：manual、ai_recommend、keyword_expand 或 batch_import；历史提问为空。</param>
/// <param name="InstantCollection">写接口附带的立即采集结果：创建启用的提问（POST /prompts）或平台范围变更后新增了有效模型（PATCH /prompts/{promptId}）时返回；
/// 其他情况及列表、详情接口不返回该字段。</param>
/// <param name="AppEligibility">该提问当前能否进入 App 渠道（自动检查与运营人员的人工排除，人工排除优先）：eligible 为 false 时 reasonCode 给出原因，
/// 该提问不生成 App 任务，API 渠道照常采集。只对已开放 App 渠道的团队返回；未开放的团队不返回该字段。</param>
public sealed record PromptView(Guid PromptId, string Content, int Version, string Locale, bool Enabled,
    IReadOnlyList<TagView> Tags, string BrandedStatus, string BrandedSource,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? ArchivedAt = null, uint ConfigVersion = 1,
    Guid? GroupId = null, string TargetScope = "all", IReadOnlyList<string>? TargetKeys = null, string? CreationSource = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InstantCollectionView? InstantCollection = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AppPromptEligibility? AppEligibility = null);

/// <summary>Prompt 分组中的提问数量摘要。</summary>
/// <param name="PromptCount">匹配筛选条件的未归档 Prompt 数量。</param>
/// <param name="EnabledCount">匹配筛选条件的未归档且启用 Prompt 数量。</param>
public sealed record PromptGroupCounts(int PromptCount, int EnabledCount);

/// <summary>现有 Prompt 的分组视图。</summary>
/// <param name="GroupId">分组标识。</param>
/// <param name="Name">分组名称。</param>
/// <param name="Version">分组配置版本。</param>
/// <param name="PromptCount">匹配筛选条件的 Prompt 数量。</param>
/// <param name="EnabledCount">匹配筛选条件的启用 Prompt 数量。</param>
/// <param name="CreatedAt">创建 UTC 时间。</param>
/// <param name="UpdatedAt">最近更新时间。</param>
public sealed record PromptGroupView(Guid GroupId, string Name, uint Version, int PromptCount, int EnabledCount,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>分组列表及始终独立返回的未分组摘要。</summary>
/// <param name="Items">真实分组列表。</param>
/// <param name="NextCursor">下一页游标。</param>
/// <param name="Ungrouped">未分组 Prompt 摘要。</param>
public sealed record PromptGroupPage(IReadOnlyList<PromptGroupView> Items, string? NextCursor,
    PromptGroupCounts Ungrouped);

/// <summary>创建 Prompt 分组。</summary>
/// <param name="Name">分组名称，去除首尾空白后长度为 1–100。</param>
public sealed record PromptGroupWrite(string Name);

/// <summary>修改 Prompt 分组名称。</summary>
/// <param name="Name">新的分组名称。</param>
/// <param name="ExpectedVersion">当前分组配置版本。</param>
public sealed record PromptGroupPatch(string? Name = null, uint? ExpectedVersion = null);

/// <summary>品牌 Prompt 的权威数量摘要，不受列表分页和筛选影响。</summary>
/// <param name="TotalCount">未归档 Prompt 总数。</param>
/// <param name="EnabledCount">未归档且启用 Prompt 数量。</param>
/// <param name="ArchivedCount">已逻辑删除 Prompt 数量。</param>
public sealed record PromptSummaryView(int TotalCount, int EnabledCount, int ArchivedCount);

/// <summary>标签写入请求。</summary>
/// <param name="Name">标签名称。</param>
/// <param name="Status">标签状态。</param>
public sealed record TagWrite(string Name, string Status = "active");

/// <summary>标签局部更新。</summary>
/// <param name="Name">可选标签名称。</param>
/// <param name="Status">可选状态。</param>
/// <param name="ExpectedVersion">必填的当前标签版本。</param>
public sealed record TagPatch(string? Name = null, string? Status = null, uint? ExpectedVersion = null);

/// <summary>站点标签。</summary>
/// <param name="TagId">标签稳定标识。</param>
/// <param name="Name">标签名称。</param>
/// <param name="Status">标签状态。</param>
/// <param name="Version">乐观并发版本。</param>
public sealed record TagView(Guid TagId, string Name, string Status, uint Version = 1);

/// <summary>配置启用的采集目标。</summary>
/// <param name="Provider">采集 Provider。</param>
/// <param name="Engine">采集引擎。</param>
/// <param name="CaptureMode">采集模式。</param>
/// <param name="Country">采集国家代码。</param>
/// <param name="Locale">采集语言标识。</param>
/// <param name="Enabled">是否启用。</param>
/// <param name="AnswerMode">回答模式（快速回答 / 深度思考）；省略表示 Provider 默认模式（见目录 defaultAnswerMode），旧客户端行为不变。
/// 必须属于目录中该 Provider 的 supportedAnswerModes，否则返回 400 ANSWER_MODE_UNSUPPORTED。同一模型的两种模式是两个不同的目标。</param>
/// <param name="Frequency">每日监控采集频率；省略为 daily（1次/天）。取值无效返回 400 FREQUENCY_INVALID。
/// App 目标（provider=app_collector）的频率固定为 weekly（见目录 fixedFrequency），必须显式传 weekly：省略与显式的 daily 无法区分，
/// 两者及其他取值都返回 400 FREQUENCY_INVALID，不会被改写成 weekly。App 目标的 captureMode 必须为 app，国家 / 语言必须为 CN / zh-CN。</param>
public sealed record SiteTargetWrite(string Provider, string Engine, string CaptureMode = "scraped",
    string Country = "US", string Locale = "en-US", bool Enabled = true,
    AnswerMode? AnswerMode = null, MonitoringFrequency Frequency = MonitoringFrequency.Daily);

/// <summary>站点目标替换范围。</summary>
public enum SiteTargetsScope
{
    /// <summary>替换全部站点目标（默认，与旧行为一致）：请求中没有的已有目标全部停用。</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("all")]
    All,

    /// <summary>只替换可参与定时监控的目标（已注册的 API 回答 Provider 与 App 目标）；国内网页采集等其他目标保持原状，请求中也不能包含这类目标。</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("monitoring")]
    Monitoring,
}

/// <summary>替换站点目标集合时携带当前版本。</summary>
/// <param name="ExpectedVersion">必填的当前目标配置版本；不一致返回 409 CONFIG_VERSION_CONFLICT。</param>
/// <param name="Targets">新的完整目标集合（scope 范围内）；启用的每日监控目标最多 20 个，超过返回 400 MONITORING_TOO_LARGE；同一目标（含回答模式）重复返回 400 DUPLICATE_TARGET。
/// App 目标不占这 20 个名额（每个平台最多一个 App 目标）；单次请求最多 30 个非 App 目标。</param>
/// <param name="Scope">替换范围：all（默认，替换全部目标）或 monitoring（只替换可参与每日监控的目标，保留国内网页采集目标）。</param>
public sealed record SiteTargetsWrite(uint ExpectedVersion, IReadOnlyList<SiteTargetWrite> Targets,
    SiteTargetsScope Scope = SiteTargetsScope.All);

/// <summary>站点目标集合及配置版本。</summary>
/// <param name="ConfigVersion">目标配置版本。</param>
/// <param name="Items">目标列表。</param>
/// <param name="FollowsTeamDefault">品牌监控配置是否跟随团队默认监控配置：true = 跟随（团队默认配置变更时自动同步可监控目标），
/// false = 单独配置（保存品牌目标后即为单独配置，可通过 POST /sites/{siteId}/targets/follow-default 恢复跟随）。迁移 048 未部署时恒为 false。</param>
public sealed record SiteTargetsView(uint ConfigVersion, IReadOnlyList<CollectionTargetView> Items, bool FollowsTeamDefault = false);

/// <summary>采集目标及能力。</summary>
/// <param name="TargetId">站点目标标识；目录项为空。</param>
/// <param name="Provider">采集 Provider。</param>
/// <param name="Engine">采集引擎。</param>
/// <param name="DisplayName">前端显示名称。</param>
/// <param name="CaptureMode">采集模式。</param>
/// <param name="EvidenceClass">证据类别。</param>
/// <param name="Country">国家代码。</param>
/// <param name="Locale">语言标识。</param>
/// <param name="Enabled">站点是否启用。</param>
/// <param name="Availability">当前可用性。</param>
/// <param name="AnswerCapability">回答能力。</param>
/// <param name="CitationCapability">引用能力。</param>
/// <param name="SearchQueryCapability">搜索词能力。</param>
/// <param name="TargetKey">采集目标键（Provider、引擎、模式、国家与语言的稳定摘要），提问平台范围 targetKeys 使用该值。</param>
/// <param name="MonitoringEligible">该 Provider 是否可参与定时监控（API 回答 Provider 走每日监控，App 目标走每周的 App 计划）；国内网页采集目标为 false。是否启用见 Enabled。</param>
/// <param name="SupportedAnswerModes">该 Provider 支持的回答模式，按界面列顺序排列；不支持的模式在弹窗中显示为「—」。未注册的 Provider（如国内网页采集）为 [fast]。</param>
/// <param name="DefaultAnswerMode">Provider 默认回答模式（等于引入回答模式前的实际采集行为，例如豆包、元宝为 deep）；写入时省略 answerMode 即为该模式。</param>
/// <param name="AnswerMode">站点目标的实际回答模式（已按默认模式解析，不为空）；目录项为空。</param>
/// <param name="Frequency">站点目标的采集频率；目录项为空。</param>
/// <param name="FrequencyAnchor">只读：每2天1次 / 1次/周 的锚点日期（品牌时区的本地日期），目标首次启用或频率改为这两种时写为当天；目录项及未设置时为空。
/// App 目标的锚点是下一个每周周期的起始日期：新启用时写为启用日的次日。</param>
/// <param name="Channel">采集渠道，由 Provider 推导：Cloro 与各 API Provider 为 api，App 目标（app_collector）为 app，自研国内网页采集（domestic_collector）为 web。</param>
/// <param name="FixedFrequency">固定的采集频率：App 目标为 weekly，表示频率不可修改；其他目标为空。</param>
/// <param name="UnavailableReason">不可用且原因已知时的原因（目前只有 App 目标：平台的 App 入口被停用）；可用或原因未知时为空。</param>
public sealed record CollectionTargetView(Guid? TargetId, string Provider, string Engine, string DisplayName,
    string CaptureMode, string EvidenceClass, string Country, string Locale, bool Enabled,
    string Availability, string AnswerCapability, string CitationCapability, string SearchQueryCapability,
    string? TargetKey = null, bool MonitoringEligible = false,
    IReadOnlyList<AnswerMode>? SupportedAnswerModes = null, AnswerMode DefaultAnswerMode = Domain.AnswerMode.Fast,
    AnswerMode? AnswerMode = null, MonitoringFrequency? Frequency = null, DateOnly? FrequencyAnchor = null,
    CollectionChannel? Channel = null, MonitoringFrequency? FixedFrequency = null,
    CollectionTargetUnavailableReason? UnavailableReason = null);

/// <summary>采集目标不可用的原因。</summary>
/// <param name="Code">稳定的原因码，例如 APP_CONTENT_UNREADABLE（App 内容无法读取）、APP_EMULATOR_REJECTED（App 拒绝在模拟器上运行）。</param>
/// <param name="Message">面向用户的中文说明。</param>
public sealed record CollectionTargetUnavailableReason(string Code, string Message);

/// <summary>按 Prompt、标签及目标选取待采集样本。</summary>
/// <param name="PromptIds">显式 Prompt 标识。</param>
/// <param name="TagIds">按标签解析 Prompt。</param>
/// <param name="TargetIds">站点已启用目标标识。</param>
/// <param name="SampleCount">每组回答样本数，范围 1–10。</param>
public sealed record CollectionSelection(IReadOnlyList<Guid>? PromptIds, IReadOnlyList<Guid>? TagIds,
    IReadOnlyList<Guid>? TargetIds, int SampleCount = 1);

/// <summary>固定规模预览，含按此预览创建运行时预计预占的积分。</summary>
/// <param name="PreviewId">预览标识。</param>
/// <param name="SiteId">品牌工作区标识。</param>
/// <param name="PromptIds">固定的 Prompt 集合。</param>
/// <param name="Targets">固定的 Target 集合。</param>
/// <param name="SampleCount">每组回答样本数。</param>
/// <param name="ExpectedChecks">预期 Check 数。</param>
/// <param name="EstimatedUnits">预计 Provider 调用单位。</param>
/// <param name="EstimatedAmount">预计预占的积分：预期检测数 × 每日监控单价（10）；按此预览创建运行时预占同样的数额。</param>
/// <param name="Availability">所选目标可用性。</param>
/// <param name="SubjectConfigVersion">主体配置版本。</param>
/// <param name="ExpiresAt">预览过期 UTC 时间。</param>
/// <param name="TargetConfigVersion">目标配置版本。</param>
public sealed record CollectionPreviewView(Guid PreviewId, Guid SiteId, IReadOnlyList<Guid> PromptIds,
    IReadOnlyList<SiteTargetWrite> Targets, int SampleCount, int ExpectedChecks, decimal EstimatedUnits,
    decimal? EstimatedAmount, string Availability, uint SubjectConfigVersion, DateTimeOffset ExpiresAt,
    uint TargetConfigVersion = 1);

/// <summary>导入或批量创建的单行内容。新增字段均可省略，省略时行为与旧版导入一致（幂等哈希也不变）。</summary>
/// <param name="Content">问题正文。</param>
/// <param name="Locale">语言标识。</param>
/// <param name="TagIds">标签标识。</param>
/// <param name="GroupId">归入的已有分组标识；与 GroupName 互斥。</param>
/// <param name="GroupName">分组名称，去除首尾空白后 1–100 字；按规范化名称命中已有分组则归入，否则在同一事务中新建（同批同名只建一次）。与 GroupId 互斥。</param>
/// <param name="BrandedStatus">手动指定的品牌词分类（branded 或 unbranded），写为人工分类；省略时由服务端自动判定。</param>
/// <param name="Source">创建来源：ai_recommend、keyword_expand、batch_import 或 manual；需要迁移 042。</param>
public sealed record PromptImportRow(string Content, string Locale = "en-US", IReadOnlyList<Guid>? TagIds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? GroupId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? GroupName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BrandedStatus = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Source = null);

/// <summary>批量创建监测问题的请求体（POST /sites/{siteId}/prompts/batch，幂等键放在 Idempotency-Key 请求头）。</summary>
/// <param name="Rows">1–200 行待创建的问题。</param>
/// <param name="TargetKeys">本次新建问题统一使用的 AI 平台范围；省略表示全部平台（all），非空时规则同 PromptWrite.TargetKeys。</param>
public sealed record PromptBatchCreateRequest(IReadOnlyList<PromptImportRow> Rows, IReadOnlyList<string>? TargetKeys = null);

/// <summary>导入预览的一行校验结果。</summary>
/// <param name="Row">从 1 开始的行号。</param>
/// <param name="ErrorCode">错误或重复码；有效新行为空。</param>
/// <param name="ExistingPromptId">已有 Prompt 的标识。</param>
/// <param name="GroupResolution">分组解析结果：existing 表示归入已有分组，create 表示提交时新建分组，none 表示不分组或该行无效。</param>
/// <param name="GroupId">归入的已有分组标识；新建或不分组时为空。</param>
public sealed record PromptImportRowResult(int Row, string? ErrorCode, Guid? ExistingPromptId,
    string GroupResolution = "none", Guid? GroupId = null);

/// <summary>导入预览结果。</summary>
/// <param name="Rows">逐行校验结果。</param>
/// <param name="NewCount">可新建行数。</param>
/// <param name="DuplicateCount">重复行数。</param>
public sealed record PromptImportPreviewView(IReadOnlyList<PromptImportRowResult> Rows, int NewCount, int DuplicateCount);

/// <summary>导入提交结果。</summary>
/// <param name="CreatedCount">新建 Prompt 数。</param>
/// <param name="DuplicateCount">重复行数。</param>
/// <param name="PromptIds">新建及已存在的 Prompt 标识。</param>
/// <param name="CreatedPromptIds">本次实际新建的 Prompt 标识（不含与已有提问重复的行）；同一幂等键重放时返回首次提交的结果，早于该字段的历史导入为空。</param>
/// <param name="InstantCollection">新建提问后的立即采集结果；没有新建提问时不返回该字段。</param>
public sealed record PromptImportResult(int CreatedCount, int DuplicateCount, IReadOnlyList<Guid> PromptIds,
    IReadOnlyList<Guid>? CreatedPromptIds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InstantCollectionView? InstantCollection = null);
