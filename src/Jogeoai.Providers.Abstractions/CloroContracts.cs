using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jogeoai.Providers.Abstractions;

/// <summary>向 Cloro 请求的回答引擎，不固定供应商底层模型版本。</summary>
public enum CloroModel
{
    /// <summary>ChatGPT 回答引擎。</summary>
    [JsonStringEnumMemberName("chatgpt")]
    ChatGpt,
    /// <summary>Perplexity 回答引擎。</summary>
    [JsonStringEnumMemberName("perplexity")]
    Perplexity,
    /// <summary>Copilot 回答引擎。</summary>
    [JsonStringEnumMemberName("copilot")]
    Copilot,
    /// <summary>Gemini 回答引擎。</summary>
    [JsonStringEnumMemberName("gemini")]
    Gemini,
    /// <summary>Google AI Mode 回答引擎。</summary>
    [JsonStringEnumMemberName("google-ai-mode")]
    GoogleAiMode,
    /// <summary>Google AI Overview 回答引擎。</summary>
    [JsonStringEnumMemberName("google-ai-overview")]
    GoogleAiOverview,
}

/// <summary>Cloro 全局连接信息，API 不会返回 API Key。</summary>
/// <param name="ApiKey">兼容期保留的单例 API Key，仅为回滚旧版本保存；运行时改从 key 池读取，空字符串表示未保存。</param>
/// <param name="ApiUrl">Cloro 异步任务接口地址。</param>
/// <param name="Enabled">是否全局启用 Cloro。</param>
/// <param name="UpdatedAt">最近一次更新的 UTC 时间。</param>
public sealed record CloroConnectionSettings(
    string ApiKey,
    string ApiUrl,
    bool Enabled = true,
    DateTimeOffset? UpdatedAt = null);

/// <summary>持久化单例 Cloro 全局配置（地址和启用开关）。</summary>
public interface ICloroSettingsStore
{
    /// <summary>读取已保存的 Cloro 配置；未配置时返回空值。</summary>
    /// <param name="cancellationToken">取消读取的令牌。</param>
    Task<CloroConnectionSettings?> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>保存 Cloro 配置；ApiKey 为空时保留单例表中已有的值。</summary>
    /// <param name="settings">需要持久化的连接配置。</param>
    /// <param name="cancellationToken">取消写入的令牌。</param>
    Task SaveAsync(CloroConnectionSettings settings, CancellationToken cancellationToken = default);
}

/// <summary>Cloro 账号 key 池的选 key、切换、冷却与余额刷新参数。</summary>
public sealed class CloroKeyPoolOptions
{
    /// <summary>余额小于或等于该值时视为耗尽。</summary>
    public decimal LowBalanceThreshold { get; set; }
    /// <summary>429 并发槽位已满后的冷却时长。</summary>
    public TimeSpan ConcurrencyCooldown { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>429 限流后的冷却时长。</summary>
    public TimeSpan RateLimitCooldown { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>429 队列已满后的冷却时长。</summary>
    public TimeSpan QueueFullCooldown { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>非积分原因的 403 之后的冷却时长。</summary>
    public TimeSpan ForbiddenCooldown { get; set; } = TimeSpan.FromMinutes(10);
    /// <summary>一次租约内最多尝试的账号数。</summary>
    public int MaxSubmitAttemptsPerLease { get; set; } = 3;
    /// <summary>排队后因积分不足失败时，每个任务最多换账号重提的次数。</summary>
    public int MaxCreditResubmitsPerTask { get; set; } = 2;
    /// <summary>池耗尽时单次延后的最长间隔。</summary>
    public TimeSpan PoolExhaustedRetryInterval { get; set; } = TimeSpan.FromMinutes(10);
    /// <summary>池耗尽时任务最长等待时间，超过后终态失败并释放用户积分预留。</summary>
    public TimeSpan PoolExhaustedMaxWait { get; set; } = TimeSpan.FromHours(2);
    /// <summary>定时刷新账号余额的间隔。</summary>
    public TimeSpan CreditRefreshInterval { get; set; } = TimeSpan.FromMinutes(10);
    /// <summary>池概览中“余额偏低”的阈值。</summary>
    public decimal PoolLowBalanceWarning { get; set; } = 100;
    /// <summary>余额接口地址；为空时由全局 api_url 的协议和主机推导为 /v1/credits。</summary>
    public string? CreditsUrl { get; set; }
}

/// <summary>选中的 Cloro 账号 key。ToString 和 JSON 序列化都不包含 key 值。</summary>
/// <param name="KeyId">key 池记录标识。</param>
/// <param name="AccountLabel">账号名称。</param>
/// <param name="ApiKey">完整 API Key，只用于 Authorization 请求头。</param>
/// <param name="KeyHint">key 后 4 位。</param>
public sealed record CloroKeyLease(Guid KeyId, string AccountLabel, [property: JsonIgnore] string ApiKey, string KeyHint)
{
    /// <summary>返回不含 key 值的描述。</summary>
    public override string ToString() => $"CloroKeyLease {{ KeyId = {KeyId}, AccountLabel = {AccountLabel}, KeyHint = {KeyHint} }}";
}

/// <summary>Cloro 账号余额快照，只包含三个已解析字段。</summary>
/// <param name="Remaining">当前剩余积分。</param>
/// <param name="PerCycle">每个计费周期的额度。</param>
/// <param name="CycleResetsAt">周期重置的 UTC 时间。</param>
/// <param name="CheckedAt">查询的 UTC 时间。</param>
public sealed record CloroCreditsSnapshot(decimal? Remaining, decimal? PerCycle, DateTimeOffset? CycleResetsAt, DateTimeOffset CheckedAt);

/// <summary>Cloro 明确拒绝提交（未创建外部任务）的原因。</summary>
public enum CloroKeyRejection
{
    /// <summary>403 INSUFFICIENT_CREDITS，账号积分不足。</summary>
    InsufficientCredits,
    /// <summary>429 CONCURRENCY_LIMIT_EXCEEDED，组织并发槽位已满。</summary>
    ConcurrencyLimited,
    /// <summary>429 RATE_LIMIT_EXCEEDED 或未知 429，触发限流。</summary>
    RateLimited,
    /// <summary>429 QUEUE_LIMIT_EXCEEDED，异步队列已满。</summary>
    QueueFull,
    /// <summary>401，key 无效或已作废。</summary>
    Unauthorized,
    /// <summary>非积分原因的 403。</summary>
    Forbidden,
}

/// <summary>Cloro key 池当前可用性。</summary>
/// <param name="EnabledKeys">启用且未归档的账号数。</param>
/// <param name="AvailableKeys">当前可被选中的账号数。</param>
/// <param name="NextAvailableAt">不可选账号中最早恢复的 UTC 时间（冷却结束或周期重置）。</param>
/// <param name="CapacityCoolingKeys">仅因 429 并发、限流或队列满而短暂冷却的账号数，冷却结束即可再用。</param>
public sealed record CloroKeyPoolAvailability(int EnabledKeys, int AvailableKeys, DateTimeOffset? NextAvailableAt,
    int CapacityCoolingKeys = 0)
{
    /// <summary>是否所有启用账号都因积分不足、key 无效或非积分 403 而不可用；仅短暂冷却不算耗尽。</summary>
    public bool CreditsExhausted => EnabledKeys > 0 && AvailableKeys == 0 && CapacityCoolingKeys == 0;
}

/// <summary>Cloro 账号 key 池：选 key 与状态写回。状态持久化在数据库，多 Worker 共享。</summary>
public interface ICloroKeyPool
{
    /// <summary>按优先级、最早重置时间、最近使用时间选出一个可用账号；没有可用账号时返回空值。</summary>
    /// <param name="excluded">本次租约内已被拒绝的账号。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<CloroKeyLease?> PickAsync(IReadOnlyCollection<Guid> excluded, CancellationToken cancellationToken = default);

    /// <summary>按标识读取 key（停用、归档的也返回），用于轮询；标识为空时返回 default 账号。</summary>
    /// <param name="keyId">提交任务时记录的 key 标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<CloroKeyLease?> GetAsync(Guid? keyId, CancellationToken cancellationToken = default);

    /// <summary>统计启用账号数、可用账号数和最早恢复时间。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<CloroKeyPoolAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default);

    /// <summary>记录一次成功提交。</summary>
    /// <param name="keyId">使用的 key。</param>
    /// <param name="providerTaskId">本地 Provider Task 标识。</param>
    /// <param name="externalTaskId">Cloro 外部任务标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task MarkUsedAsync(Guid keyId, Guid? providerTaskId, string externalTaskId, CancellationToken cancellationToken = default);

    /// <summary>记录一次明确拒绝，并把账号置为耗尽、冷却或无效。</summary>
    /// <param name="keyId">被拒绝的 key。</param>
    /// <param name="providerTaskId">本地 Provider Task 标识。</param>
    /// <param name="rejection">拒绝原因。</param>
    /// <param name="errorCode">白名单内的稳定错误码，或按 HTTP 状态生成的代码。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task MarkRejectedAsync(Guid keyId, Guid? providerTaskId, CloroKeyRejection rejection, string errorCode, CancellationToken cancellationToken = default);

    /// <summary>写入余额查询结果并据此转换状态。</summary>
    /// <param name="keyId">查询的 key。</param>
    /// <param name="credits">余额快照。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task ApplyCreditsAsync(Guid keyId, CloroCreditsSnapshot credits, CancellationToken cancellationToken = default);

    /// <summary>记录余额查询失败；不改变账号可用性。</summary>
    /// <param name="keyId">查询的 key。</param>
    /// <param name="errorCode">稳定错误码。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task MarkCreditsFailedAsync(Guid keyId, string errorCode, CancellationToken cancellationToken = default);

    /// <summary>列出启用、未归档且到期（或等待刷新）的账号。</summary>
    /// <param name="interval">距上次查询的最短间隔；为零时返回全部启用账号。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyList<CloroKeyLease>> ListRefreshDueAsync(TimeSpan interval, CancellationToken cancellationToken = default);
}

/// <summary>查询 Cloro 账号余额（GET /v1/credits）。</summary>
public interface ICloroCreditsClient
{
    /// <summary>用指定 key 查询所属账号的余额；401 抛出 CLORO_KEY_INVALID。</summary>
    /// <param name="key">需要查询的 key。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<CloroCreditsSnapshot> GetCreditsAsync(CloroKeyLease key, CancellationToken cancellationToken = default);
}

/// <summary>发送到 Cloro 诊断接口的请求。</summary>
/// <param name="Model">向 Cloro 请求的回答引擎。</param>
/// <param name="Prompt">提交给回答引擎的问题。</param>
/// <param name="Country">请求使用的两位国家/地区代码。</param>
public sealed record CloroTestRequest(
    CloroModel Model,
    string Prompt,
    string Country = "US");

/// <summary>Cloro 标准化回答引用的来源。</summary>
/// <param name="Url">完整引用地址。</param>
/// <param name="Domain">标准化后的来源域名。</param>
/// <param name="Title">可选的来源标题。</param>
/// <param name="CitationIndex">从 1 开始的引用位置。</param>
public sealed record CloroCitation(
    string Url,
    string Domain,
    string? Title,
    int CitationIndex);

/// <summary>Cloro 任务完成后返回的标准化结果。</summary>
/// <param name="TaskId">Cloro 任务标识。</param>
/// <param name="Model">Cloro 返回的模型版本。</param>
/// <param name="TextContent">回答正文。</param>
/// <param name="Citations">回答引用的来源。</param>
/// <param name="WebQueries">生成回答时使用的搜索词。</param>
/// <param name="RawOutput">为诊断返回的受控摘要，不包含完整 Provider 原始响应。</param>
/// <param name="CapturedAt">回答捕获时间，使用 UTC。</param>
/// <param name="AccountLabel">本次诊断使用的 Cloro 账号名称。</param>
/// <param name="KeyHint">本次诊断使用的 key 后 4 位。</param>
public sealed record CloroTestResult(
    string TaskId,
    string Model,
    string TextContent,
    IReadOnlyList<CloroCitation> Citations,
    IReadOnlyList<string> WebQueries,
    object RawOutput,
    DateTimeOffset CapturedAt,
    string? AccountLabel = null,
    string? KeyHint = null);

/// <summary>调用已配置的 Cloro Provider 执行请求。</summary>
public interface ICloroAdapter
{
    /// <summary>提交并轮询一次 Cloro 诊断请求。</summary>
    /// <param name="request">需要执行的请求。</param>
    /// <param name="cancellationToken">取消 Provider 调用的令牌。</param>
    Task<CloroTestResult> RunAsync(CloroTestRequest request, CancellationToken cancellationToken = default);
}

/// <summary>提交给 Cloro 业务任务的请求。</summary>
/// <param name="Engine">Cloro 引擎名称。</param>
/// <param name="Prompt">Prompt 快照。</param>
/// <param name="Locale">区域语言。</param>
/// <param name="Country">国家/地区代码。</param>
/// <param name="ProviderTaskId">本地 Provider Task 标识，用于 key 事件审计；诊断请求为空。</param>
public sealed record CloroTaskRequest(string Engine, string Prompt, string Locale, string Country, Guid? ProviderTaskId = null);

/// <summary>Cloro Submit 的安全返回值。</summary>
/// <param name="ExternalTaskId">Cloro 外部任务 ID。</param>
/// <param name="SubmittedAt">提交时间。</param>
/// <param name="RequestHash">脱敏请求摘要。</param>
/// <param name="KeyId">提交所用的 key 池记录标识；轮询必须使用同一个 key。</param>
public sealed record CloroSubmittedTask(string ExternalTaskId, DateTimeOffset SubmittedAt, string RequestHash, Guid? KeyId = null);

/// <summary>Cloro Poll 的脱敏状态快照。</summary>
/// <param name="ExternalTaskId">外部任务 ID。</param>
/// <param name="Status">Cloro 状态。</param>
/// <param name="Payload">脱敏响应 JSON。</param>
/// <param name="ErrorCode">稳定错误码。</param>
/// <param name="ErrorMessage">安全错误信息。</param>
/// <param name="AnswerRedacted">读取响应时正文是否经过安全替换。</param>
/// <param name="SourcesRedacted">读取响应时来源字段是否经过安全替换。</param>
/// <param name="ProviderFailureCode">FAILED 响应中读到的白名单错误码（目前只有 INSUFFICIENT_CREDITS）；不保留其他错误文本。</param>
public sealed record CloroTaskStatusSnapshot(
    string ExternalTaskId,
    string Status,
    JsonElement Payload,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    bool AnswerRedacted = false,
    bool SourcesRedacted = false,
    string? ProviderFailureCode = null);

/// <summary>可恢复 Cloro Provider 调用契约。业务层不读取供应商字段。</summary>
public interface ICloroTaskClient
{
    /// <summary>仅提交任务并立即返回外部任务 ID。</summary>
    /// <param name="request">提交所需的引擎、Prompt、区域和国家。</param>
    /// <param name="cancellationToken">取消外部请求的令牌。</param>
    Task<CloroSubmittedTask> SubmitAsync(CloroTaskRequest request, CancellationToken cancellationToken = default);

    /// <summary>用提交时的 key 轮询已提交任务。</summary>
    /// <param name="externalTaskId">Cloro 外部任务标识。</param>
    /// <param name="keyId">提交所用的 key；为空时（池化前的历史任务）回退到 default 账号。</param>
    /// <param name="cancellationToken">取消外部请求的令牌。</param>
    Task<CloroTaskStatusSnapshot> GetAsync(string externalTaskId, Guid? keyId = null, CancellationToken cancellationToken = default);

    /// <summary>把完成状态标准化为统一回答快照。</summary>
    /// <param name="completed">已完成的脱敏状态。</param>
    /// <param name="requestedEngine">创建任务时请求的引擎。</param>
    CloroAnswerSnapshot Normalize(CloroTaskStatusSnapshot completed, string requestedEngine);
}

/// <summary>Cloro 标准化回答的 Provider 层结构。</summary>
/// <param name="Provider">固定为 cloro。</param>
/// <param name="Engine">请求的 Cloro 引擎。</param>
/// <param name="CaptureMode">固定为 scraped。</param>
/// <param name="EvidenceClass">固定为 observed_surface。</param>
/// <param name="ModelVersion">Provider 返回的模型版本或请求引擎回退值。</param>
/// <param name="AnswerText">规范化回答正文。</param>
/// <param name="AnswerPresent">回答是否非空。</param>
/// <param name="Citations">按提供方顺序过滤后的公开引用，保留不同位置的重复链接。</param>
/// <param name="WebQueries">Provider 返回的搜索词。</param>
/// <param name="RawOutput">已脱敏的受控响应。</param>
/// <param name="CapturedAt">回答捕获的 UTC 时间。</param>
/// <param name="ParserVersion">解析器版本。</param>
/// <param name="QueryAvailability">Provider 搜索词字段的可用状态。</param>
/// <param name="ResultQuality">规范化质量，旧写入者未提供时为空。</param>
public sealed record CloroAnswerSnapshot(
    string Provider,
    string Engine,
    string CaptureMode,
    string EvidenceClass,
    string ModelVersion,
    string AnswerText,
    bool AnswerPresent,
    IReadOnlyList<CloroCitation> Citations,
    IReadOnlyList<string> WebQueries,
    JsonElement RawOutput,
    DateTimeOffset CapturedAt,
    string ParserVersion = "cloro-v1",
    string QueryAvailability = "unknown",
    CloroResultQuality? ResultQuality = null);
