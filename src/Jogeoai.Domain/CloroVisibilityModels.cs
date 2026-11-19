using System.Text.Json.Serialization;

namespace Jogeoai.Domain;

/// <summary>API 回答 Provider 的采集目标；采集模式与证据类别由 Provider 定义派生（cloro 为页面抓取，doubao_api、deepseek_api 与 packy_qwen / packy_glm / packy_kimi / packy_yuanbao 为接口响应）。</summary>
/// <param name="Provider">已注册的 API 回答 Provider：cloro、doubao_api、deepseek_api、packy_qwen、packy_glm、packy_kimi 或 packy_yuanbao。</param>
/// <param name="Engine">该 Provider 支持的引擎代码。</param>
/// <param name="Locale">BCP-47 区域语言标识；空值继承 Prompt（豆包、DeepSeek 与 PackyAPI 固定 zh-CN）。</param>
/// <param name="Country">两位 ISO 国家代码；空值使用 Provider 默认国家（Cloro 为 US，豆包、DeepSeek 与 PackyAPI 固定 CN）。</param>
/// <param name="OptionsJson">Provider 选项；首期只允许空对象。</param>
/// <param name="AnswerMode">回答模式 fast / deep；空值表示 Provider 默认模式。规范化（<see cref="AnswerModeRules.TryNormalize"/>）会把与默认相同的值折叠为空值，
/// 为空时不参与 JSON 序列化，目标键、选择哈希与请求哈希与引入回答模式前逐字节一致。</param>
public sealed record CloroVisibilityTarget(string Provider, string Engine, string? Locale = null, string? Country = null, string? OptionsJson = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AnswerMode = null);

/// <summary>创建 API 回答 Provider（Cloro、豆包、DeepSeek、PackyAPI）Visibility Run 的命令。</summary>
/// <param name="TenantId">租户标识。</param>
/// <param name="SiteId">站点标识。</param>
/// <param name="PromptIds">互不重复的 Prompt 标识。</param>
/// <param name="Targets">采集目标集合，每个目标的 Provider 必须已注册。</param>
/// <param name="IdempotencyKey">租户范围内的幂等键。</param>
/// <param name="RequestedBy">发起人标识。</param>
/// <param name="SampleCount">每个 Prompt 与 Target 的样本数。</param>
/// <param name="PreviewId">可选固定预览标识。</param>
/// <param name="RequireEnabledTargets">是否仅允许站点当前启用的 Target。</param>
/// <param name="HttpRequestHash">HTTP 规范化请求的身份摘要；计划运行为空。</param>
/// <param name="ChargeMonitoringCredits">是否按 Prompt 每日监控规则预留和结算积分；手动采集默认不计费。</param>
/// <param name="PromptTargetKeys">每日监控中自定义平台范围提问的有效目标键，必须是 Targets 的子集；为空表示全部 Prompt × 全部 Target。手动采集不使用。</param>
public sealed record CreateCloroVisibilityRunCommand(Guid TenantId, Guid SiteId, IReadOnlyList<Guid> PromptIds,
    IReadOnlyList<CloroVisibilityTarget> Targets, string IdempotencyKey, string? RequestedBy = null,
    int SampleCount = 1, Guid? PreviewId = null, bool RequireEnabledTargets = false, string? HttpRequestHash = null,
    bool ChargeMonitoringCredits = false, IReadOnlyList<CollectionPromptTargets>? PromptTargetKeys = null);

/// <summary>Cloro 任务持久化状态。</summary>
public static class CloroTaskStates
{
    /// <summary>等待提交。</summary>
    public const string Pending = "pending";
    /// <summary>已领取，尚未开始外部调用。</summary>
    public const string Leased = "leased";
    /// <summary>正在提交；过期后不得自动重新提交。</summary>
    public const string Submitting = "submitting";
    /// <summary>Provider 排队中。</summary>
    public const string Queued = "queued";
    /// <summary>Provider 生成中。</summary>
    public const string Running = "running";
    /// <summary>结果已入库。</summary>
    public const string Succeeded = "succeeded";
    /// <summary>可重试失败。</summary>
    public const string RetryableFailed = "retryable_failed";
    /// <summary>外部提交结果未知，等待人工核对。</summary>
    public const string SubmissionUnknown = "submission_unknown";
    /// <summary>终态失败。</summary>
    public const string TerminalFailed = "terminal_failed";
}

/// <summary>Cloro 任务租约视图，版本隔离过期 Worker 的写入。</summary>
/// <param name="TaskId">Provider Task 标识。</param>
/// <param name="TenantId">租户标识。</param>
/// <param name="RunId">运行标识。</param>
/// <param name="CheckId">检查标识。</param>
/// <param name="Prompt">Prompt 快照。</param>
/// <param name="Engine">请求引擎。</param>
/// <param name="Locale">区域语言。</param>
/// <param name="Country">国家代码。</param>
/// <param name="State">领取前状态。</param>
/// <param name="ExternalTaskId">已持久化的外部任务标识。</param>
/// <param name="AttemptCount">包含本次的领取次数。</param>
/// <param name="LeaseUntil">租约 UTC 截止时间。</param>
/// <param name="LeaseVersion">单调递增的租约版本。</param>
/// <param name="CreatedAt">任务创建时间。</param>
/// <param name="SubmittedAt">提交成功时间。</param>
/// <param name="StartedAt">首次观察到生成状态的时间。</param>
/// <param name="CloroKeyId">提交所用的 key；轮询使用同一个 key，池化前的历史任务为空。</param>
/// <param name="CreditResubmitCount">排队后因积分不足换账号重提的次数。</param>
/// <param name="PoolExhaustedSince">首次因 key 池耗尽延后的时间；成功提交后清空。</param>
/// <param name="AnswerMode">本检测要使用的回答模式存储值（fast / deep），已按 Provider 默认模式解析、不为空；
/// 迁移 047 之前的历史检测（visibility_checks.answer_mode 为 NULL）解析为 Provider 默认模式。</param>
public sealed record CloroTaskLease(Guid TaskId, Guid TenantId, Guid RunId, Guid CheckId, string Prompt,
    string Engine, string Locale, string Country, string State, string? ExternalTaskId, int AttemptCount,
    DateTimeOffset LeaseUntil, int LeaseVersion, DateTimeOffset CreatedAt, DateTimeOffset? SubmittedAt, DateTimeOffset? StartedAt,
    Guid? CloroKeyId = null, int CreditResubmitCount = 0, DateTimeOffset? PoolExhaustedSince = null,
    string AnswerMode = AnswerModeRules.Fast);

/// <summary>规范化回答正文。</summary>
/// <param name="Text">Markdown 或纯文本回答。</param>
public sealed record VisibilityAnswer(string Text);

/// <summary>Cloro 回答中的公开引用。</summary>
/// <param name="Id">引用标识。</param>
/// <param name="Url">规范化 URL。</param>
/// <param name="Domain">来源域名。</param>
/// <param name="Title">来源标题。</param>
/// <param name="Position">从 1 开始的位置。</param>
public sealed record CloroCitationView(Guid Id, string Url, string? Domain, string? Title, int Position);

/// <summary>Cloro Check 查询视图；不公开原始响应。</summary>
/// <param name="CheckId">检查标识。</param>
/// <param name="RunId">运行标识。</param>
/// <param name="PromptId">Prompt 标识。</param>
/// <param name="PromptSnapshot">提交时的问题快照。</param>
/// <param name="Provider">Provider。</param>
/// <param name="Engine">请求引擎。</param>
/// <param name="CaptureMode">采集模式，固定 scraped。</param>
/// <param name="EvidenceClass">证据类别，固定 observed_surface。</param>
/// <param name="Locale">区域语言。</param>
/// <param name="Country">国家代码。</param>
/// <param name="Status">检查状态。</param>
/// <param name="AnswerPresent">是否有非空回答。</param>
/// <param name="Answer">规范化回答。</param>
/// <param name="WebQueries">Provider 返回的搜索词。</param>
/// <param name="ModelVersion">实际模型版本。</param>
/// <param name="Citations">按位置排序的引用。</param>
/// <param name="CapturedAt">采集 UTC 时间。</param>
/// <param name="DurationMs">从提交到完成的毫秒数。</param>
/// <param name="RawResponseRef">超限响应的摘要标识；不代表可下载地址。</param>
/// <param name="RawResponseSha256">脱敏响应的 SHA-256。</param>
/// <param name="ParserVersion">解析器版本。</param>
/// <param name="ErrorCode">稳定错误码。</param>
/// <param name="ErrorMessage">安全错误摘要。</param>
/// <param name="ProviderTaskState">关联 Provider Task 状态。</param>
/// <param name="ExternalTaskId">Cloro 外部任务标识。</param>
/// <param name="RunTargetId">本次运行中固定的 Target 标识。</param>
/// <param name="SampleIndex">同 Prompt 和 Target 下的样本序号。</param>
/// <param name="PromptVersionId">采集时 Prompt 版本标识。</param>
/// <param name="PromptTagsSnapshot">采集时的标签名称快照。</param>
/// <param name="BrandedStatusSnapshot">采集时的品牌词分类。</param>
/// <param name="SubjectConfigVersion">采集时的主体配置版本。</param>
/// <param name="SnapshotMissing">历史行缺失完整快照的标记。</param>
/// <param name="Analysis">已发布版本的提及分析和原文证据。</param>
/// <param name="QueryAvailability">Provider 搜索词字段的可用状态。</param>
/// <param name="ResultQuality">回答和来源的保留质量；历史缺失或未知版本为空。</param>
/// <param name="AnswerMode">本检测实际使用的回答模式（快速回答 / 深度思考）；迁移 047 之前的历史检测为空（显示为「历史」）。</param>
public sealed record CloroVisibilityCheckView(Guid CheckId, Guid RunId, Guid PromptId, string PromptSnapshot,
    string Provider, string Engine, string CaptureMode, string EvidenceClass, string Locale, string Country,
    string Status, bool AnswerPresent, VisibilityAnswer? Answer, IReadOnlyList<string> WebQueries, string? ModelVersion,
    IReadOnlyList<CloroCitationView> Citations, DateTimeOffset? CapturedAt, int? DurationMs,
    string? RawResponseRef, string? RawResponseSha256, string? ParserVersion, string? ErrorCode, string? ErrorMessage,
    string? ProviderTaskState, string? ExternalTaskId,
    Guid? RunTargetId = null, int SampleIndex = 1, Guid? PromptVersionId = null,
    IReadOnlyList<string>? PromptTagsSnapshot = null, string? BrandedStatusSnapshot = null,
    int? SubjectConfigVersion = null, bool SnapshotMissing = false,
    CheckAnalysisView? Analysis = null, string QueryAvailability = "unknown", ResultQualityView? ResultQuality = null,
    AnswerMode? AnswerMode = null);
