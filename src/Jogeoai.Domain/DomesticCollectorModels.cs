namespace Jogeoai.Domain;

/// <summary>国内采集任务支持的区域。</summary>
public enum CollectorRegion
{
    /// <summary>中国大陆网络区域。</summary>
    Cn,

    /// <summary>海外网络区域，当前国内采集任务不使用。</summary>
    Overseas,
}

/// <summary>采集任务的持久化状态。</summary>
public enum CollectorTaskStatus
{
    /// <summary>等待 Collector 领取。</summary>
    Pending,

    /// <summary>已分配租约。</summary>
    Leased,

    /// <summary>Collector 正在浏览器中采集。</summary>
    Collecting,

    /// <summary>结果已经入库。</summary>
    Succeeded,

    /// <summary>可重试失败。</summary>
    RetryableFailed,

    /// <summary>不可重试失败。</summary>
    TerminalFailed,
}

/// <summary>Collector 结果提交的处理结果。</summary>
public enum CollectorResultDisposition
{
    /// <summary>首次接受并写入业务结果。</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("accepted")]
    Accepted,

    /// <summary>相同幂等键已经处理过。</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("duplicate")]
    Duplicate,

    /// <summary>租约已经失效，结果只保留审计记录。</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("stale")]
    Stale,
}

/// <summary>国内 AI 平台入口选择。</summary>
/// <param name="PlatformCode">稳定的平台代码。</param>
/// <param name="EntryCode">平台入口代码，例如 doubao.consumer_web。</param>
/// <param name="Language">回答语言，例如 zh-CN。</param>
/// <param name="Channel">采集渠道，当前为 consumer_web。</param>
public sealed record CollectorPlatformSelection(
    string PlatformCode,
    string EntryCode,
    string Language = "zh-CN",
    string Channel = "consumer_web");

/// <summary>创建一次可见性运行的请求。</summary>
/// <param name="TenantId">租户标识。</param>
/// <param name="SiteId">目标站点标识。</param>
/// <param name="PromptIds">需要采集的 Prompt 标识。</param>
/// <param name="Platforms">需要执行的国内平台入口。</param>
/// <param name="IdempotencyKey">租户范围内的幂等键。</param>
/// <param name="RequestedBy">发起人标识。</param>
/// <param name="HttpRequestHash">HTTP 规范化请求的身份摘要；内部调用为空。</param>
public sealed record CreateVisibilityRunCommand(
    Guid TenantId,
    Guid SiteId,
    IReadOnlyList<Guid> PromptIds,
    IReadOnlyList<CollectorPlatformSelection> Platforms,
    string IdempotencyKey,
    string? RequestedBy = null,
    string? HttpRequestHash = null);

/// <summary>创建运行后的安全响应。</summary>
/// <param name="RunId">运行标识。</param>
/// <param name="Status">运行状态。</param>
/// <param name="TotalChecks">计划的检查数量。</param>
/// <param name="CreatedAt">创建时间。</param>
/// <param name="SucceededChecks">已成功的回答数。</param>
/// <param name="FailedChecks">已失败的回答数。</param>
public sealed record VisibilityRunCreated(
    Guid RunId,
    string Status,
    int TotalChecks,
    DateTimeOffset CreatedAt,
    int SucceededChecks = 0,
    int FailedChecks = 0);

/// <summary>Collector 领取任务的请求。</summary>
/// <param name="TenantId">Collector 允许访问的租户。</param>
/// <param name="NodeId">Collector 节点标识。</param>
/// <param name="Region">Collector 所在区域。</param>
/// <param name="SupportedPlatformCodes">节点支持的平台列表。</param>
/// <param name="Capacity">本次最多领取的任务数量。</param>
/// <param name="LeaseSeconds">租约秒数。</param>
/// <param name="SessionProfileId">账号池模式的账号标识；为空时走原有单账号领取，带账号时 capacity 强制为 1。</param>
public sealed record CollectorLeaseCommand(
    Guid TenantId,
    string NodeId,
    CollectorRegion Region,
    IReadOnlyList<string> SupportedPlatformCodes,
    int Capacity = 1,
    int LeaseSeconds = 60,
    Guid? SessionProfileId = null);

/// <summary>返回给 Collector 的租约任务。</summary>
/// <param name="Id">采集任务标识。</param>
/// <param name="TenantId">租户标识。</param>
/// <param name="RunId">运行标识。</param>
/// <param name="CheckId">检查标识。</param>
/// <param name="SiteId">站点标识，兼容参考 Collector 的项目字段。</param>
/// <param name="PlatformCode">平台代码。</param>
/// <param name="PlatformEntryCode">平台入口代码。</param>
/// <param name="PlatformEntryVersionId">平台入口版本标识。</param>
/// <param name="PlatformEntryVersion">平台入口版本。</param>
/// <param name="PromptId">Prompt 标识，兼容参考 Collector 的问题版本字段。</param>
/// <param name="QuestionText">Prompt 快照。</param>
/// <param name="Region">采集区域。</param>
/// <param name="Language">回答语言。</param>
/// <param name="Channel">采集渠道。</param>
/// <param name="LeaseVersion">租约版本。</param>
/// <param name="LeaseExpiresAt">租约截止时间。</param>
/// <param name="SessionProfileId">租约绑定的账号；无账号租约为 null。</param>
public sealed record LeasedCollectorTask(
    Guid Id,
    Guid TenantId,
    Guid RunId,
    Guid CheckId,
    Guid SiteId,
    string PlatformCode,
    string PlatformEntryCode,
    Guid PlatformEntryVersionId,
    int PlatformEntryVersion,
    Guid PromptId,
    string QuestionText,
    CollectorRegion Region,
    string Language,
    string Channel,
    int LeaseVersion,
    DateTimeOffset LeaseExpiresAt,
    Guid? SessionProfileId = null);

/// <summary>Collector 心跳请求。</summary>
public sealed record CollectorHeartbeatCommand(
    Guid TenantId,
    Guid TaskId,
    string NodeId,
    CollectorRegion Region,
    int LeaseVersion,
    int ExtendSeconds = 60);

/// <summary>Collector 结果提交请求。</summary>
/// <param name="TenantId">租户标识。</param>
/// <param name="TaskId">采集任务标识。</param>
/// <param name="NodeId">Collector 节点标识。</param>
/// <param name="Region">Collector 所在区域。</param>
/// <param name="LeaseVersion">提交时持有的租约版本。</param>
/// <param name="IdempotencyKey">任务范围内的结果幂等键。</param>
/// <param name="EgressCountry">采集出口国家，国内结果必须为 CN。</param>
/// <param name="CollectorBuildRevision">Collector 构建版本。</param>
/// <param name="AnswerText">回答正文。</param>
/// <param name="Citations">按页面顺序排列的引用，可携带来源标题。</param>
/// <param name="CompletedAt">采集完成的 UTC 时间。</param>
/// <param name="ProviderMetadataJson">平台元数据 JSON。</param>
/// <param name="SearchQueries">页面展示的搜索关键词；null 表示 Collector 未读取到关键词，不改动已有值；空数组表示页面未展示关键词。</param>
public sealed record CollectorResultCommand(
    Guid TenantId,
    Guid TaskId,
    string NodeId,
    CollectorRegion Region,
    int LeaseVersion,
    string IdempotencyKey,
    string EgressCountry,
    string CollectorBuildRevision,
    string AnswerText,
    IReadOnlyList<CollectorCitationInput> Citations,
    DateTimeOffset CompletedAt,
    string? ProviderMetadataJson = null,
    IReadOnlyList<string>? SearchQueries = null);

/// <summary>Collector 提交的引用。</summary>
public sealed record CollectorCitationInput(string Url, string? Title = null, int Position = 0);

/// <summary>Collector 结果提交响应。</summary>
public sealed record CollectorResultResponse(
    CollectorResultDisposition Disposition,
    Guid? CheckId)
{
    /// <summary>兼容参考 Collector 合同的结果标识别名。</summary>
    public Guid? SampleId => CheckId;
}

/// <summary>运行概要。</summary>
/// <param name="RunId">运行标识。</param>
/// <param name="SiteId">所属站点标识。</param>
/// <param name="Status">运行状态。</param>
/// <param name="TotalChecks">预期检查数。</param>
/// <param name="SucceededChecks">成功检查数。</param>
/// <param name="FailedChecks">失败检查数。</param>
/// <param name="CreatedAt">创建 UTC 时间。</param>
/// <param name="StartedAt">开始 UTC 时间。</param>
/// <param name="CompletedAt">结束 UTC 时间。</param>
/// <param name="ErrorCode">错误代码。</param>
/// <param name="ErrorMessage">错误说明。</param>
/// <param name="Provider">运行使用的 Provider。</param>
/// <param name="CancelledChecks">未采集的检查数（状态为 cancelled）。只有 App 周期运行会有未采集的检查，其他运行恒为零。</param>
public sealed record VisibilityRunSummary(
    Guid RunId,
    Guid SiteId,
    string Status,
    int TotalChecks,
    int SucceededChecks,
    int FailedChecks,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorCode,
    string? ErrorMessage,
    string? Provider = null,
    int CancelledChecks = 0)
{
    /// <summary>预期回答样本数。</summary>
    public int ExpectedChecks => TotalChecks;
    /// <summary>采集已结束的样本数：成功、失败与未采集（cancelled）之和；未采集不计为失败，但采集已经结束。</summary>
    public int CollectionCompletedChecks => SucceededChecks + FailedChecks + CancelledChecks;
    /// <summary>分析阶段状态；后续分析 Worker 接入前为 not_started。</summary>
    public string AnalysisStatus => "not_started";
    /// <summary>已分析的样本数；后续分析 Worker 接入前为零。</summary>
    public int AnalyzedChecks => 0;
}

/// <summary>回答引用。</summary>
public sealed record VisibilityCitation(
    Guid Id,
    string Url,
    string? Title,
    string? SourceDomain,
    int Position);

/// <summary>一次检查的查询视图。</summary>
/// <param name="CheckId">检查标识。</param>
/// <param name="PromptId">Prompt 标识。</param>
/// <param name="PromptSnapshot">采集时的问题正文。</param>
/// <param name="Engine">采集引擎。</param>
/// <param name="Status">检查状态。</param>
/// <param name="Answer">回答正文。</param>
/// <param name="CollectorNodeId">Collector 节点标识。</param>
/// <param name="CollectorRegion">Collector 区域。</param>
/// <param name="EgressCountry">出口国家。</param>
/// <param name="CollectorBuildRevision">Collector 构建版本。</param>
/// <param name="CapturedAt">采集 UTC 时间。</param>
/// <param name="Citations">引用列表。</param>
/// <param name="ErrorCode">错误代码。</param>
/// <param name="ErrorMessage">错误说明。</param>
/// <param name="PromptVersionId">冻结的 Prompt 版本标识。</param>
/// <param name="PromptTagsSnapshot">冻结的标签名称。</param>
/// <param name="BrandedStatusSnapshot">冻结的品牌词分类。</param>
/// <param name="BrandedSourceSnapshot">冻结的分类来源。</param>
/// <param name="SubjectConfigVersion">冻结的主体配置版本。</param>
/// <param name="SnapshotMissing">旧数据中是否缺少完整快照。</param>
public sealed record VisibilityCheckView(
    Guid CheckId,
    Guid PromptId,
    string PromptSnapshot,
    string Engine,
    string Status,
    string? Answer,
    string? CollectorNodeId,
    string? CollectorRegion,
    string? EgressCountry,
    string? CollectorBuildRevision,
    DateTimeOffset? CapturedAt,
    IReadOnlyList<VisibilityCitation> Citations,
    string? ErrorCode,
    string? ErrorMessage,
    Guid? PromptVersionId = null,
    IReadOnlyList<string>? PromptTagsSnapshot = null,
    string? BrandedStatusSnapshot = null,
    string? BrandedSourceSnapshot = null,
    uint? SubjectConfigVersion = null,
    bool SnapshotMissing = true);

/// <summary>既有运行的幂等重放信息。</summary>
/// <param name="Run">当前运行状态与原始创建时间。</param>
/// <param name="SiteId">运行所属站点。</param>
/// <param name="HttpRequestHash">创建时保存的 HTTP 请求身份摘要。</param>
/// <param name="PreviewId">旧数据中保存的预览标识。</param>
/// <param name="RequestedBy">原请求发起人。</param>
public sealed record VisibilityRunReplay(VisibilityRunCreated Run, Guid SiteId, string? HttpRequestHash,
    Guid? PreviewId, string? RequestedBy);

/// <summary>游标分页结果。</summary>
/// <param name="Items">当前页数据。</param>
/// <param name="NextCursor">下一页不透明游标；没有下一页时为空。</param>
public sealed record CursorPage<T>(IReadOnlyList<T> Items, string? NextCursor);
