using System.Globalization;
using Jogeoai.Api.Security;
using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Jogeoai.Api.Controllers;

/// <summary>
/// 供 App 采集节点（安卓模拟器采集器）使用的内部控制面接口。不走用户会话（匿名白名单），每个动作由节点自己的令牌
/// （Authorization: Bearer）认证：节点身份由令牌确定，请求里的 nodeId 必须与之一致。网页采集器的共享令牌在这里无效。
/// 新增 action 必须同步白名单测试。
/// </summary>
/// <remarks>
/// 节点令牌由 <see cref="AppNodeAuthenticationFilter"/> 在模型绑定之前校验：令牌无效或节点被停用时不读取请求体。
/// 每个动作都有请求体大小上限（<see cref="CollectorRequestLimits"/>，超限返回 413），整个控制器按客户端 IP 限流
/// （<see cref="CollectorRateLimit"/>，超限返回 429 并带 Retry-After）。新增 action 必须标注 <c>[RequestSizeLimit]</c>。
/// </remarks>
[ApiController]
[AllowAnonymous]
[AppNodeAuthenticationFilter]
[EnableRateLimiting(CollectorRateLimit.Policy)]
[Route("internal/collector/app")]
public sealed class AppCollectorController(IAppCollectorPoolStore store) : ControllerBase
{
    /// <summary>
    /// 上报本节点的绑定（设备 × App × 账号）及其健康状态。返回每条被接受的绑定在控制面里的状态（限速值以控制面为准），
    /// 以及被拒绝的绑定和原因；一条被拒绝不影响其余各条。
    /// 新登记的绑定要等运营批准：批准之前它的状态是 stopped、停止原因是 disabled_by_operator，上报的健康状态不能让它生效。
    /// 节点已登记的不同设备数达到上限（默认 4 台）时，新设备的绑定出现在 rejected 里，code 为 device_limit_exceeded，不会被登记。
    /// </summary>
    [HttpPost("bindings/report")]
    [RequestSizeLimit(CollectorRequestLimits.AppBindingReport)]
    [ProducesResponseType(typeof(AppBindingReportResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AppBindingReportResponse>> ReportBindings([FromBody] AppBindingReportRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var nodeId = await AuthenticateAsync(request.NodeId, cancellationToken);
            var result = await store.ReportBindingsAsync(
                new AppBindingReportCommand(nodeId, request.CollectorBuildRevision ?? "", request.Bindings ?? []), cancellationToken);
            return Ok(new AppBindingReportResponse(result.Bindings.Select(binding => new AppBindingResponse(binding.DeviceId, binding.PlatformCode,
                binding.BindingId, binding.Status, binding.StopReason, binding.CooldownUntil is { } until ? IsoUtc(until) : null,
                binding.MinIntervalSeconds, binding.DailyQuota)).ToArray(), result.Rejected));
        }
        catch (CollectorStoreException exception) { return StatusCode(exception.StatusCode, Error(exception)); }
    }

    /// <summary>为一个绑定领取至多一条 App 任务；没有领到时 task 为 null，并给出原因和建议的重试间隔。</summary>
    [HttpPost("leases")]
    [RequestSizeLimit(CollectorRequestLimits.SmallBody)]
    [ProducesResponseType(typeof(AppLeaseResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AppLeaseResponse>> Lease([FromBody] AppLeaseRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var nodeId = await AuthenticateAsync(request.NodeId, cancellationToken);
            var binding = ParseBinding(request.BindingId);
            if (request.DispatchProtocol != AppPoolRules.DispatchProtocol || request.DeviceOnline is null)
                throw new CollectorStoreException("DISPATCH_PROTOCOL_REQUIRED", "请使用支持设备级派单的采集端。", 409);
            if (request.DeviceOnline != true)
                return Ok(new AppLeaseResponse(null, AppLeaseEmptyReason.DeviceOffline, AppPoolRules.NoTaskRetrySeconds));
            var result = await store.LeaseAsync(new AppLeaseCommand(nodeId, binding, request.LeaseSeconds), cancellationToken);
            return Ok(new AppLeaseResponse(result.Task is { } task ? ToResponse(task) : null, result.Reason, result.RetryAfterSeconds));
        }
        catch (CollectorStoreException exception) { return StatusCode(exception.StatusCode, Error(exception)); }
    }

    /// <summary>确认当前设备实际发题的时刻；重放不会延长设备冷却。</summary>
    [HttpPost("sent")]
    [RequestSizeLimit(CollectorRequestLimits.SmallBody)]
    [ProducesResponseType(typeof(AppSentResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AppSentResponse>> Sent([FromBody] AppSentRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var nodeId = await AuthenticateAsync(request.NodeId, cancellationToken);
            if (!Guid.TryParse(request.TaskId, out var taskId) || request.LeaseVersion < 1 || request.SentAt is not { } at)
                throw new CollectorStoreException("INVALID_SENT", "需要有效任务、租约版本和发送时间。", 400);
            return Ok(new AppSentResponse(await store.ReportSentAsync(
                new AppSentCommand(nodeId, ParseBinding(request.BindingId), taskId, request.LeaseVersion, at), cancellationToken)));
        }
        catch (CollectorStoreException exception) { return StatusCode(exception.StatusCode, Error(exception)); }
    }

    /// <summary>
    /// 续租当前任务；租约已不是当前租约或已过期时返回 409 STALE_LEASE。
    /// 一次租约从领取起最多续到总时长上限（默认 1800 秒），也不超过任务的截止时点：返回的截止时间可能早于“现在 + extendSeconds”；
    /// 已超过上限或截止时点时同样返回 409 STALE_LEASE。
    /// </summary>
    [HttpPost("heartbeats")]
    [RequestSizeLimit(CollectorRequestLimits.SmallBody)]
    [ProducesResponseType(typeof(CollectorHeartbeatResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<CollectorHeartbeatResponse>> Heartbeat([FromBody] AppHeartbeatRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var nodeId = await AuthenticateAsync(request.NodeId, cancellationToken);
            if (!Guid.TryParse(request.TaskId, out var taskId) || request.LeaseVersion < 1)
                throw new CollectorStoreException("INVALID_LEASE", "taskId 必须是 UUID，leaseVersion 必须是正整数。", 400);
            var expiry = await store.HeartbeatAsync(new AppHeartbeatCommand(nodeId, taskId, request.LeaseVersion, request.ExtendSeconds), cancellationToken);
            return Ok(new CollectorHeartbeatResponse(IsoUtc(expiry)));
        }
        catch (CollectorStoreException exception) { return StatusCode(exception.StatusCode, Error(exception)); }
    }

    /// <summary>
    /// 上报分类失败：立即释放租约，按失败码处理绑定（停止或冷却）、任务（重新排队或终态）和平台（暂停或入口不可用）。
    /// 响应里的 cooldownUntil 是处理后绑定仍在生效的冷却截止时间；任务终态时它预留的积分随即释放。
    /// 还没有被运营批准的绑定的上报不生效：响应是绑定的当前状态，带任务时 taskDisposition 为 stale。
    /// </summary>
    [HttpPost("failures")]
    [RequestSizeLimit(CollectorRequestLimits.SmallBody)]
    [ProducesResponseType(typeof(AppFailureResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AppFailureResponse>> Failure([FromBody] AppFailureRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var nodeId = await AuthenticateAsync(request.NodeId, cancellationToken);
            var bindingId = ParseBinding(request.BindingId);
            if (!Exact<AppFailureCode>(request.FailureCode, out var failureCode))
                throw new CollectorStoreException("INVALID_FAILURE", "failureCode 不是 App 采集器可上报的失败码。", 400);
            if (!Exact<CollectorPhase>(request.Phase, out var phase))
                throw new CollectorStoreException("INVALID_FAILURE", "phase 必须是 probe、pre_send 或 post_send。", 400);
            CollectorReportedTask? task = null;
            if (request.Task is { } reported)
            {
                if (!Guid.TryParse(reported.TaskId, out var taskId) || reported.LeaseVersion < 1)
                    throw new CollectorStoreException("INVALID_FAILURE", "task 需要 UUID 形式的 taskId 和正整数 leaseVersion。", 400);
                task = new CollectorReportedTask(taskId, reported.LeaseVersion);
            }
            if ((phase == CollectorPhase.Probe) != (task is null))
                throw new CollectorStoreException("INVALID_FAILURE", "probe 阶段的失败不带 task；pre_send 与 post_send 必须带 task。", 400);
            if (task is null && (request.SendState is not null || request.SentAt.HasValue)
                || request.SendState is not (null or "sent" or "not_sent" or "unknown")
                || request.SendState == "not_sent" && phase != CollectorPhase.PreSend
                || request.SendState is "sent" or "unknown" && phase != CollectorPhase.PostSend
                || (request.SendState == "sent") != request.SentAt.HasValue)
                throw new CollectorStoreException("INVALID_FAILURE", "发送状态、阶段和发送时间不一致。", 400);
            var result = await store.ReportFailureAsync(new AppFailureCommand(nodeId, bindingId, failureCode, phase, task,
                request.SendState, request.SentAt), cancellationToken);
            return Ok(new AppFailureResponse(result.BindingStatus, result.StopReason, result.CooldownUntil is { } until ? IsoUtc(until) : null,
                result.TaskDisposition, result.PlatformPaused, result.PlatformUnavailable));
        }
        catch (CollectorStoreException exception) { return StatusCode(exception.StatusCode, Error(exception)); }
    }

    /// <summary>
    /// 提交一条采集结果。节点必须持有任务的当前租约，否则返回 stale（只保留审计记录，sampleId 为 null）。
    /// 同一任务同一 idempotencyKey：内容相同返回 duplicate，不同返回 409 RESULT_IDEMPOTENCY_CONFLICT。
    /// 回答与提问原文相同（忽略空白）返回 400 ECHO_ANSWER；校验或上限不满足返回 400 INVALID_RESULT；任务不存在返回 404 TASK_NOT_FOUND。
    /// 接受后该检测扣 10 积分；没有 URL 的引用不进入引用表，只随回答保存。绑定和账号代号由控制面从租约取，不在请求里。
    /// </summary>
    [HttpPost("results")]
    [RequestSizeLimit(CollectorRequestLimits.AppResult)]
    [ProducesResponseType(typeof(AppResultResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AppResultResponse>> Result([FromBody] AppResultRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var nodeId = await AuthenticateAsync(request.NodeId, cancellationToken);
            var result = await store.SubmitResultAsync(ToCommand(nodeId, request), cancellationToken);
            return Ok(new AppResultResponse(result.Disposition, result.CheckId));
        }
        catch (CollectorStoreException exception) { return StatusCode(exception.StatusCode, Error(exception)); }
    }

    // Shape only: required members, UUID, exact wire names of the enums. Lengths and consistency are checked by AppResultRules.
    private static AppResultCommand ToCommand(string nodeId, AppResultRequest request)
    {
        static CollectorStoreException Invalid(string message) => new(AppResultRules.InvalidCode, message, 400);
        if (!Guid.TryParse(request.TaskId, out var taskId) || request.LeaseVersion < 1)
            throw Invalid("taskId 必须是 UUID，leaseVersion 必须是正整数。");
        if (request.SentAt is not { } sentAt || request.CompletedAt is not { } completedAt) throw Invalid("sentAt 与 completedAt 必须提供。");
        if (!Exact<AppCitationsStatus>(request.CitationsStatus, out var citationsStatus))
            throw Invalid("citationsStatus 必须是 read、absent、unreadable 或 not_declared。");
        var (webSearch, deepThinking) = (AppVisibleState.Unknown, AppVisibleState.Unknown);
        if (request.SearchState is { } state && (!Exact(state.WebSearch, out webSearch) || !Exact(state.DeepThinking, out deepThinking)))
            throw Invalid("searchState 的 webSearch 与 deepThinking 必须是 on、off 或 unknown。");
        if (request.Device is not { } device || request.App is not { } app) throw Invalid("device 与 app 必须提供。");
        if (request.Citations is { } submitted && submitted.Any(citation => citation is null)) throw Invalid("citations 不能包含空项。");
        return new AppResultCommand(nodeId, taskId, request.LeaseVersion, request.IdempotencyKey ?? "", request.CollectorBuildRevision ?? "",
            sentAt, completedAt, request.AnswerText ?? "",
            (request.Citations ?? []).Select(citation => new AppResultCitation(citation.Title ?? "", citation.Site, citation.Url, citation.List)).ToArray(),
            citationsStatus, request.CitationsIssue, webSearch, deepThinking, request.SearchState?.SearchQueries,
            device.DeviceId ?? "", device.AndroidVersion ?? "", device.Abi, app.Package ?? "", app.Version ?? "", request.ScreenshotSha256);
    }

    /// <summary>
    /// 节点鉴权：令牌未知 401；节点被停用 403 APP_NODE_DISABLED；请求里的 nodeId 与令牌对应的节点不一致 403 APP_NODE_MISMATCH。
    /// 返回值是令牌对应的节点标识，后续处理只使用它。
    /// 令牌与停用状态已由 <see cref="AppNodeAuthenticationFilter"/> 在模型绑定之前检查，这里从 HttpContext.Items 读取结果，不再查库；
    /// 动作没有经过 MVC 过滤器管道（例如被直接调用）时 Items 里没有节点，就在这里按同一规则鉴权，不存在未鉴权的执行路径。
    /// </summary>
    private async Task<string> AuthenticateAsync(string? claimedNodeId, CancellationToken cancellationToken)
    {
        var node = AppNodeAuthenticationFilter.GetNode(HttpContext) ?? await AuthenticateNodeAsync(Request, store, cancellationToken);
        if (claimedNodeId is not { Length: >= 1 and <= 128 })
            throw new CollectorStoreException("INVALID_REQUEST", "nodeId 必须为 1–128 个字符。", 400);
        if (!string.Equals(claimedNodeId, node.NodeId, StringComparison.Ordinal))
            throw new CollectorStoreException("APP_NODE_MISMATCH", "请求里的 nodeId 与令牌对应的节点不一致。", 403);
        return node.NodeId;
    }

    /// <summary>
    /// 只依赖请求头的节点鉴权，过滤器与动作共用：没有 Bearer 令牌时不查库；令牌未知 401 UNAUTHORIZED；
    /// 节点被停用 403 APP_NODE_DISABLED。Store 抛出的错误（例如未执行迁移 049 的 503）原样向上传递。
    /// </summary>
    internal static async Task<AppNodeIdentity> AuthenticateNodeAsync(HttpRequest request, IAppCollectorPoolStore store,
        CancellationToken cancellationToken)
    {
        var provided = request.Headers.Authorization.ToString();
        var token = provided.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? provided[7..].Trim() : string.Empty;
        var node = token.Length == 0 ? null : await store.AuthenticateNodeAsync(token, cancellationToken);
        if (node is null) throw new CollectorStoreException("UNAUTHORIZED", "App collector node authentication failed.", 401);
        if (node.Status != AppNodeStatus.Active) throw new CollectorStoreException("APP_NODE_DISABLED", "该采集节点已被停用。", 403);
        return node;
    }

    private static Guid ParseBinding(string? value)
        => Guid.TryParse(value, out var id) && id != Guid.Empty
            ? id
            : throw new CollectorStoreException("INVALID_BINDING", "bindingId 必须是 UUID。", 400);

    // Exact wire names only: no trimming and no case folding.
    private static bool Exact<TEnum>(string? value, out TEnum result) where TEnum : struct, Enum
    {
        result = default;
        return value is not null && AppContractNames.All<TEnum>().Contains(value, StringComparer.Ordinal)
            && AppContractNames.TryParse(value, out result);
    }

    private static object Error(CollectorStoreException exception) => new { error = new { code = exception.Code, message = exception.Message } };

    /// <summary>过滤器短路时使用的错误响应：与动作内 <c>StatusCode(exception.StatusCode, Error(exception))</c> 的状态码和响应体相同。</summary>
    internal static ObjectResult ErrorResult(CollectorStoreException exception) => new(Error(exception)) { StatusCode = exception.StatusCode };

    private static AppTaskResponse ToResponse(AppLeasedTask task) => new(task.Id, task.TenantId, task.RunId, task.CheckId, task.SiteId,
        task.PlatformCode, task.PlatformEntryCode, task.PromptId, task.QuestionText, task.Region, task.Language, task.Channel,
        task.PromptRuleVersion, IsoUtc(task.DeadlineAt), task.LeaseVersion, IsoUtc(task.LeaseExpiresAt), task.BindingId);

    /// <summary>ISO 8601 UTC，毫秒精度并以 Z 结尾。</summary>
    private static string IsoUtc(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}

/// <summary>App 采集节点的绑定上报。</summary>
/// <param name="NodeId">节点标识，必须与令牌对应的节点一致。</param>
/// <param name="CollectorBuildRevision">采集器构建版本，1–128 个字符。</param>
/// <param name="Bindings">本节点的绑定，最多 200 条；没有出现在这里的已登记绑定保持不变。</param>
public sealed record AppBindingReportRequest(string? NodeId, string? CollectorBuildRevision, IReadOnlyList<AppBindingReport>? Bindings);

/// <summary>绑定上报的响应。bindings 与 rejected 合起来与请求里的绑定一一对应，用 deviceId 和 platformCode 对应。</summary>
/// <param name="Bindings">被接受的绑定在控制面里的状态。</param>
/// <param name="Rejected">被拒绝的绑定及原因；没有时为空数组。超出节点设备上限的绑定的 code 为 device_limit_exceeded。</param>
/// <param name="DispatchProtocol">设备级派单协议；采集器须核对此值后才领取。</param>
public sealed record AppBindingReportResponse(IReadOnlyList<AppBindingResponse> Bindings, IReadOnlyList<AppBindingRejection> Rejected,
    string DispatchProtocol = AppPoolRules.DispatchProtocol);

/// <summary>一条绑定在控制面里的状态。限速值和停用以控制面为准，采集器不能放宽。</summary>
/// <param name="DeviceId">设备标识（请求值去掉首尾空白后的值）。</param>
/// <param name="PlatformCode">平台代码（规范的小写代码）。</param>
/// <param name="BindingId">控制面生成的绑定标识，领取和失败上报时使用。</param>
/// <param name="Status">绑定状态：active 或 stopped。新登记、等待运营批准的绑定是 stopped。</param>
/// <param name="StopReason">停止原因；未停止时为 null。等待运营批准的绑定是 disabled_by_operator。</param>
/// <param name="CooldownUntil">仍在生效的冷却截止时间（ISO 8601 UTC，以 Z 结尾）；不在冷却中时为 null。</param>
/// <param name="MinIntervalSeconds">兼容字段：返回设备级两次发送的最小间隔，不作为绑定闸门。</param>
/// <param name="DailyQuota">兼容字段：设备级派单不设配额，返回 null。</param>
public sealed record AppBindingResponse(string DeviceId, string PlatformCode, Guid BindingId, AppBindingStatus Status,
    AppBindingStopReason? StopReason, string? CooldownUntil, int MinIntervalSeconds, int? DailyQuota);

/// <summary>App 任务的领取请求。</summary>
/// <param name="NodeId">节点标识，必须与令牌对应的节点一致。</param>
/// <param name="BindingId">领取任务的绑定（绑定上报返回的 bindingId），必须属于该节点。</param>
/// <param name="LeaseSeconds">租约秒数（5–900）。</param>
/// <param name="DispatchProtocol">必须为 device-v1；旧协议不发租约。</param>
/// <param name="DeviceOnline">本次领取前探测该虚机在线。</param>
public sealed record AppLeaseRequest(string? NodeId, string? BindingId, int LeaseSeconds = 60, string? DispatchProtocol = null, bool? DeviceOnline = null);

/// <summary>发题事实：绑定、任务版本和 UTC 发送时间。</summary>
/// <param name="NodeId">已认证的采集节点标识。</param>
/// <param name="BindingId">持有租约的设备绑定标识。</param>
/// <param name="TaskId">采集任务标识。</param>
/// <param name="LeaseVersion">所持租约版本。</param>
/// <param name="SentAt">设备实际发送提问的 UTC 时刻。</param>
public sealed record AppSentRequest(string? NodeId, string? BindingId, string? TaskId, int LeaseVersion, DateTimeOffset? SentAt);
/// <summary>发送事实回执：accepted、duplicate 或 stale。</summary>
/// <param name="Disposition">处理结果：已登记、重复或过期。</param>
public sealed record AppSentResponse(string Disposition);

/// <summary>App 任务的领取响应。三个字段总是出现：领到任务时 reason 与 retryAfterSeconds 为 null；没有领到时 task 为 null。</summary>
/// <param name="Task">领到的任务。</param>
/// <param name="Reason">没有领到任务的原因。</param>
/// <param name="RetryAfterSeconds">建议多少秒后再试（30–600）。</param>
public sealed record AppLeaseResponse(AppTaskResponse? Task, AppLeaseEmptyReason? Reason, int? RetryAfterSeconds);

/// <summary>领到的 App 采集任务。</summary>
/// <param name="Id">采集任务标识。</param>
/// <param name="TenantId">租户标识。</param>
/// <param name="RunId">周期 Run 标识。</param>
/// <param name="CheckId">检测标识。</param>
/// <param name="BrandProjectId">品牌工作区标识。</param>
/// <param name="PlatformCode">平台代码。</param>
/// <param name="PlatformEntryCode">平台入口代码，例如 doubao.consumer_app。</param>
/// <param name="QuestionVersionId">提问标识（与网页采集任务的同名字段含义相同）。</param>
/// <param name="QuestionText">提问原文；按原样发送，不加前缀、不改写。</param>
/// <param name="Region">采集区域，固定 cn。</param>
/// <param name="Language">回答语言，固定 zh-CN。</param>
/// <param name="Channel">采集渠道，固定 consumer_app。</param>
/// <param name="PromptRuleVersion">该提问通过的提问检查规则版本。</param>
/// <param name="DeadlineAt">所属周期的截止时点（ISO 8601 UTC，以 Z 结尾）。</param>
/// <param name="LeaseVersion">租约版本。</param>
/// <param name="LeaseExpiresAt">租约截止时间（ISO 8601 UTC，以 Z 结尾）。</param>
/// <param name="BindingId">持有租约的绑定。</param>
public sealed record AppTaskResponse(Guid Id, Guid TenantId, Guid RunId, Guid CheckId, Guid BrandProjectId, string PlatformCode,
    string PlatformEntryCode, Guid QuestionVersionId, string QuestionText, string Region, string Language, string Channel,
    int PromptRuleVersion, string DeadlineAt, int LeaseVersion, string LeaseExpiresAt, Guid BindingId);

/// <summary>App 任务的续租请求。</summary>
/// <param name="NodeId">节点标识，必须与令牌对应的节点一致。</param>
/// <param name="TaskId">采集任务标识。</param>
/// <param name="LeaseVersion">持有的租约版本（正整数）。</param>
/// <param name="ExtendSeconds">从现在起延长的秒数（5–900）；实际截止时间不超过租约的总时长上限和任务的截止时点。</param>
public sealed record AppHeartbeatRequest(string? NodeId, string? TaskId, int LeaseVersion, int ExtendSeconds = 60);

/// <summary>App 采集器的分类失败上报。probe 阶段不带 task；pre_send / post_send 必须带 task。</summary>
/// <param name="NodeId">节点标识，必须与令牌对应的节点一致。</param>
/// <param name="BindingId">发生失败的绑定，必须属于该节点。</param>
/// <param name="FailureCode">失败码：login_required、challenge_required、rate_limited、emulator_rejected、contract_changed、contract_unverified、version_mismatch、device_offline、app_crashed、collection_timeout。</param>
/// <param name="Phase">阶段：probe、pre_send、post_send。</param>
/// <param name="Task">失败时持有的租约任务。</param>
/// <param name="SendState">任务失败的发送状态；not_sent 仅发送前，sent 或 unknown 仅发送后。</param>
/// <param name="SentAt">sent 时必须提供实际发送时间，其他状态不能填写。</param>
public sealed record AppFailureRequest(string? NodeId, string? BindingId, string? FailureCode, string? Phase,
    CollectorReportedTaskRequest? Task = null, string? SendState = null, DateTimeOffset? SentAt = null);

/// <summary>失败上报的处理结果。可空字段总是出现，没有值时为 null。</summary>
/// <param name="BindingStatus">处理后的绑定状态：active 或 stopped。</param>
/// <param name="StopReason">绑定的停止原因；未停止时为 null。</param>
/// <param name="CooldownUntil">处理后绑定仍在生效的冷却截止时间（ISO 8601 UTC，以 Z 结尾）；不在冷却中时为 null。</param>
/// <param name="TaskDisposition">对上报任务的处理：requeued、failed 或 stale；没有带任务时为 null。</param>
/// <param name="PlatformPaused">处理后平台是否处于暂停。</param>
/// <param name="PlatformUnavailable">处理后平台的 App 入口是否不可用。</param>
public sealed record AppFailureResponse(AppBindingStatus BindingStatus, AppBindingStopReason? StopReason, string? CooldownUntil,
    CollectorTaskDisposition? TaskDisposition, bool PlatformPaused, bool PlatformUnavailable);

/// <summary>App 采集结果的提交。绑定和账号代号不在请求里：控制面从任务的租约取。</summary>
/// <param name="NodeId">节点标识，必须与令牌对应的节点一致。</param>
/// <param name="TaskId">采集任务标识。</param>
/// <param name="LeaseVersion">提交时持有的租约版本（正整数）。</param>
/// <param name="IdempotencyKey">幂等键，8–191 个字符。</param>
/// <param name="CollectorBuildRevision">采集器构建版本，1–128 个字符。</param>
/// <param name="SentAt">提问发出的时刻（ISO 8601 UTC，以 Z 结尾）。</param>
/// <param name="CompletedAt">回答采集完成的时刻（ISO 8601 UTC，以 Z 结尾）。</param>
/// <param name="AnswerText">完整的回答正文，UTF-8 不超过 1 MiB；不能与提问原文相同。</param>
/// <param name="Citations">引用来源，最多 100 条；citationsStatus 为 read 时非空，否则为空数组。没有 URL 的来源照常提交。</param>
/// <param name="CitationsStatus">引用来源的读取状态：read、absent、unreadable、not_declared。</param>
/// <param name="CitationsIssue">读取失败的原因词；只在 citationsStatus 为 unreadable 时出现，其余为 null。</param>
/// <param name="SearchState">界面上可见的搜索状态；为 null 时两个开关记为 unknown，搜索词记为未提交。</param>
/// <param name="Device">采集时的设备信息。</param>
/// <param name="App">采集时的 App 信息。</param>
/// <param name="ScreenshotSha256">完成截图文件的 SHA-256（64 位十六进制）；截图本身不上传。没有截图时为 null。</param>
public sealed record AppResultRequest(string? NodeId, string? TaskId, int LeaseVersion, string? IdempotencyKey, string? CollectorBuildRevision,
    DateTimeOffset? SentAt, DateTimeOffset? CompletedAt, string? AnswerText, IReadOnlyList<AppResultCitationRequest>? Citations,
    string? CitationsStatus, string? CitationsIssue = null, AppResultSearchStateRequest? SearchState = null, AppResultDeviceRequest? Device = null,
    AppResultAppRequest? App = null, string? ScreenshotSha256 = null);

/// <summary>App 回答里的一条引用来源，只含界面上展示的内容。</summary>
/// <param name="Title">来源标题，1–512 个字符。</param>
/// <param name="Site">站点名，最多 255 个字符；界面没有展示时为 null。</param>
/// <param name="Url">来源 URL，最多 2048 个字符；界面没有展示时为 null，不推测、不补全。</param>
/// <param name="List">来源所在的列表（例如 cited）：小写字母开头的 1–20 个小写字母、数字或下划线；界面只有一个列表时可以为 null。</param>
public sealed record AppResultCitationRequest(string? Title, string? Site = null, string? Url = null, string? List = null);

/// <summary>App 界面上可见的搜索状态；采集器只读取，不操作。</summary>
/// <param name="WebSearch">联网搜索：on、off 或 unknown。</param>
/// <param name="DeepThinking">深度思考：on、off 或 unknown。</param>
/// <param name="SearchQueries">界面上展示的搜索词，最多 32 条，每条 1–500 个字符；为 null 表示没有读取。</param>
public sealed record AppResultSearchStateRequest(string? WebSearch, string? DeepThinking, IReadOnlyList<string>? SearchQueries = null);

/// <summary>采集时的设备信息。样本上的设备标识取自租约的绑定；这里的 deviceId 只保留在原始提交里。</summary>
/// <param name="DeviceId">设备标识（AVD 名称），1–128 个字符。</param>
/// <param name="AndroidVersion">Android 版本，1–32 个字符。</param>
/// <param name="Abi">设备的 ABI，最多 32 个字符；可以为 null。</param>
public sealed record AppResultDeviceRequest(string? DeviceId, string? AndroidVersion, string? Abi = null);

/// <summary>采集时的 App 信息。</summary>
/// <param name="Package">App 包名，1–255 个字符。</param>
/// <param name="Version">采集时的 App 版本，1–64 个字符。</param>
public sealed record AppResultAppRequest(string? Package, string? Version);

/// <summary>App 采集结果的处理结果。</summary>
/// <param name="Disposition">accepted（首次接受并入库）、duplicate（相同幂等键与内容已处理过）或 stale（租约已不是当前租约，只保留审计记录）。</param>
/// <param name="SampleId">样本（检测）标识；stale 时为 null，duplicate 时只有首次提交被接受过才有值。</param>
public sealed record AppResultResponse(CollectorResultDisposition Disposition, Guid? SampleId);
