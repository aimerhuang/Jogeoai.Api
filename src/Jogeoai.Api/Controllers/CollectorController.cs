using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jogeoai.Api.Security;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Jogeoai.Api.Controllers;

/// <summary>
/// 供国内 Node.js Collector 使用的内部控制面接口。不走用户会话（匿名白名单），每个写入/领取动作由服务令牌
/// （Bearer <c>Collector:ChinaServiceToken</c>）认证；health 无令牌且只返回固定字段。新增 action 必须同步白名单测试。
/// </summary>
/// <remarks>
/// 服务令牌由 <see cref="CollectorServiceTokenFilter"/> 在模型绑定之前校验：令牌无效时不读取请求体；区域与租户名单依赖请求体，
/// 仍在动作内检查。每个 POST 动作都有请求体大小上限（<see cref="CollectorRequestLimits"/>，超限返回 413），整个控制器（含 health）
/// 按客户端 IP 限流（<see cref="CollectorRateLimit"/>，超限返回 429 并带 Retry-After）。
/// 新增需要令牌的 action 必须同时标注 <c>[CollectorServiceTokenFilter]</c> 与 <c>[RequestSizeLimit]</c>。
/// </remarks>
[ApiController]
[AllowAnonymous]
[EnableRateLimiting(CollectorRateLimit.Policy)]
[Route("internal/collector")]
public sealed class CollectorController(IVisibilityCollectorStore store, IConfiguration configuration, MySqlDatabase database) : ControllerBase
{
    /// <summary>领取一批带租约的国内采集任务；带 sessionProfileId 时走账号池派发（capacity 固定为 1，未通过配额或闸门时返回空列表）。</summary>
    [HttpPost("leases")]
    [CollectorServiceTokenFilter]
    [RequestSizeLimit(CollectorRequestLimits.SmallBody)]
    public async Task<ActionResult<CollectorLeaseResponse>> Lease([FromBody] CollectorLeaseRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (!Guid.TryParse(request.TenantId, out var tenantId)) throw new CollectorStoreException("INVALID_TENANT", "tenantId is invalid.", 400);
            var region = Authenticate(request.Region, tenantId);
            Guid? sessionProfileId = request.SessionProfileId is null ? null : ParseProfileId(request.SessionProfileId);
            // 与参考合同一致：先按 1–50 校验 capacity，再在账号模式下强制为 1，越界值不被静默改写。
            if (sessionProfileId is not null && request.Capacity is < 1 or > 50)
                throw new CollectorStoreException("INVALID_LEASE", "capacity or leaseSeconds is outside the allowed range.", 400);
            // 账号模式会把 nodeId 写入 VARCHAR(128) 的租约与健康事件；无账号路径保持原有行为。
            if (sessionProfileId is not null) RequireNode(request.NodeId);
            var tasks = await store.LeaseAsync(new CollectorLeaseCommand(tenantId, request.NodeId, region, request.SupportedPlatformCodes ?? [],
                sessionProfileId is null ? request.Capacity : 1, request.LeaseSeconds, sessionProfileId), cancellationToken);
            return Ok(new CollectorLeaseResponse(tasks.Select(ToResponse).ToArray()));
        }
        catch (CollectorStoreException exception) { return StatusCode(exception.StatusCode, Error(exception)); }
    }

    /// <summary>续租当前采集任务。</summary>
    [HttpPost("heartbeats")]
    [CollectorServiceTokenFilter]
    [RequestSizeLimit(CollectorRequestLimits.SmallBody)]
    public async Task<ActionResult<CollectorHeartbeatResponse>> Heartbeat([FromBody] CollectorHeartbeatRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var region = Authenticate(request.Region ?? "cn", request.TenantId);
            var expiry = await store.HeartbeatAsync(new CollectorHeartbeatCommand(request.TenantId, request.TaskId, request.NodeId, region, request.LeaseVersion, request.ExtendSeconds), cancellationToken);
            return Ok(new CollectorHeartbeatResponse(IsoUtc(expiry)));
        }
        catch (CollectorStoreException exception) { return StatusCode(exception.StatusCode, Error(exception)); }
    }

    /// <summary>接收 Collector 回答并幂等写入检查、引用和审计记录。</summary>
    [HttpPost("results")]
    [CollectorServiceTokenFilter]
    [RequestSizeLimit(CollectorRequestLimits.WebResult)]
    public async Task<ActionResult<CollectorResultResponse>> Result([FromBody] CollectorResultRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var region = Authenticate(request.Region, request.TenantId);
            var urls = request.Citations ?? [];
            var titles = request.CitationTitles;
            // citationTitles 与 citations 是按位对齐的并行数组；长度不一致时无法确定标题归属，整体拒绝。
            if (titles is not null && titles.Count != urls.Count)
                throw new CollectorStoreException("INVALID_RESULT", "citationTitles must have the same length as citations.", 400);
            var result = await store.SubmitResultAsync(new CollectorResultCommand(
                request.TenantId, request.TaskId, request.NodeId, region, request.LeaseVersion, request.IdempotencyKey,
                request.EgressCountry, request.CollectorBuildRevision, request.AnswerText,
                urls.Select((url, index) => new CollectorCitationInput(url, titles?[index], index + 1)).ToArray(),
                request.CompletedAt, request.ProviderMetadata?.GetRawText(), request.SearchQueries), cancellationToken);
            return Ok(result);
        }
        catch (CollectorStoreException exception) { return StatusCode(exception.StatusCode, Error(exception)); }
    }

    /// <summary>账号模式 Collector 访问平台页面前的只读预检；与租约共用账号、公平、平台闸门和待领任务判定，但不产生租约。</summary>
    [HttpPost("eligibility")]
    [CollectorServiceTokenFilter]
    [RequestSizeLimit(CollectorRequestLimits.SmallBody)]
    [ProducesResponseType(typeof(CollectorEligibilityResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<CollectorEligibilityResult>> Eligibility([FromBody] CollectorEligibilityRequest request,
        [FromServices] ICollectorSessionPoolStore pool, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = ParseTenant(request.TenantId);
            var region = Authenticate(request.Region, tenantId);
            var nodeId = RequireNode(request.NodeId);
            var profileId = ParseProfileId(request.SessionProfileId);
            if (request.PlatformCode is null || !PlatformCodes.Contains(request.PlatformCode))
                throw new CollectorStoreException("INVALID_PLATFORM", "platformCode is not a supported platform.", 400);
            return Ok(await pool.CheckEligibilityAsync(new CollectorEligibilityCommand(tenantId, nodeId, region, profileId, request.PlatformCode), cancellationToken));
        }
        catch (CollectorStoreException exception) { return StatusCode(exception.StatusCode, Error(exception)); }
    }

    /// <summary>上报分类失败：立即释放租约并按退避重新入队，更新账号健康状态并判断平台熔断。</summary>
    [HttpPost("failures")]
    [CollectorServiceTokenFilter]
    [RequestSizeLimit(CollectorRequestLimits.SmallBody)]
    [ProducesResponseType(typeof(CollectorFailureResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<CollectorFailureResult>> Failure([FromBody] CollectorFailureRequest request,
        [FromServices] ICollectorSessionPoolStore pool, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = ParseTenant(request.TenantId);
            var region = Authenticate(request.Region, tenantId);
            var nodeId = RequireNode(request.NodeId);
            var profileId = ParseProfileId(request.SessionProfileId);
            if (!FailureCodes.TryGetValue(request.FailureCode ?? "", out var failureCode))
                throw new CollectorStoreException("INVALID_FAILURE", "failureCode is not a reportable collector failure.", 400);
            var phase = ParsePhase(request.Phase, "INVALID_FAILURE");
            var task = ParseTask(request.Task, "INVALID_FAILURE");
            if ((phase == CollectorPhase.Probe) != (task is null))
                throw new CollectorStoreException("INVALID_FAILURE", "probe failures carry no task; pre_send and post_send failures require one.", 400);
            return Ok(await pool.ReportFailureAsync(new CollectorFailureCommand(tenantId, nodeId, region, profileId, failureCode, phase, task), cancellationToken));
        }
        catch (CollectorStoreException exception) { return StatusCode(exception.StatusCode, Error(exception)); }
    }

    /// <summary>上报非失败账号事件：验证码等待开始、验证码消失，或运维 probe 成功后的 verified。</summary>
    [HttpPost("session-events")]
    [CollectorServiceTokenFilter]
    [RequestSizeLimit(CollectorRequestLimits.SmallBody)]
    [ProducesResponseType(typeof(CollectorSessionEventResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<CollectorSessionEventResult>> SessionEvent([FromBody] CollectorSessionEventRequest request,
        [FromServices] ICollectorSessionPoolStore pool, CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = ParseTenant(request.TenantId);
            var region = Authenticate(request.Region, tenantId);
            var nodeId = RequireNode(request.NodeId);
            var profileId = ParseProfileId(request.SessionProfileId);
            if (!EventKinds.TryGetValue(request.EventKind ?? "", out var eventKind))
                throw new CollectorStoreException("INVALID_SESSION_EVENT", "eventKind is not a supported session event.", 400);
            var phase = ParsePhase(request.Phase, "INVALID_SESSION_EVENT");
            var task = ParseTask(request.Task, "INVALID_SESSION_EVENT");
            var valid = eventKind == CollectorSessionEventKind.Verified
                ? phase == CollectorPhase.Probe && task is null
                : phase != CollectorPhase.Probe && task is not null;
            if (!valid)
                throw new CollectorStoreException("INVALID_SESSION_EVENT", "verified is a probe event without a task; challenge events require a task.", 400);
            return Ok(await pool.RecordSessionEventAsync(new CollectorSessionEventCommand(tenantId, nodeId, region, profileId, eventKind, phase, task), cancellationToken));
        }
        catch (CollectorStoreException exception) { return StatusCode(exception.StatusCode, Error(exception)); }
    }

    /// <summary>返回 Collector 控制面健康状态。</summary>
    [HttpGet("health")]
    public async Task<ActionResult<object>> Health(CancellationToken cancellationToken)
    {
        try
        {
            await database.CheckConnectionAsync(cancellationToken);
            return Ok(new { status = "ok", region = "cn", database = "ok" });
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { status = "degraded", region = "cn", database = "unavailable" });
        }
    }

    // 与 Collector 合同 platformCodeSchema 一致；Jogeoai 是否启用某平台由 collector_platforms 决定。
    private static readonly HashSet<string> PlatformCodes = new(StringComparer.Ordinal)
    {
        "doubao", "ernie", "zhipu_qingyan", "qwen", "kimi", "deepseek", "tencent_yuanbao", "chatgpt", "claude", "gemini",
    };

    private static readonly Dictionary<string, CollectorFailureCode> FailureCodes = new(StringComparer.Ordinal)
    {
        ["login_required"] = CollectorFailureCode.LoginRequired,
        ["region_restricted"] = CollectorFailureCode.RegionRestricted,
        ["challenge_required"] = CollectorFailureCode.ChallengeRequired,
        ["rate_limited"] = CollectorFailureCode.RateLimited,
        ["page_contract_changed"] = CollectorFailureCode.PageContractChanged,
        ["page_contract_unverified"] = CollectorFailureCode.PageContractUnverified,
        ["collection_timeout"] = CollectorFailureCode.CollectionTimeout,
        ["browser_failed"] = CollectorFailureCode.BrowserFailed,
    };

    private static readonly Dictionary<string, CollectorPhase> Phases = new(StringComparer.Ordinal)
    {
        ["probe"] = CollectorPhase.Probe, ["pre_send"] = CollectorPhase.PreSend, ["post_send"] = CollectorPhase.PostSend,
    };

    private static readonly Dictionary<string, CollectorSessionEventKind> EventKinds = new(StringComparer.Ordinal)
    {
        ["challenge_wait_started"] = CollectorSessionEventKind.ChallengeWaitStarted,
        ["challenge_cleared"] = CollectorSessionEventKind.ChallengeCleared,
        ["verified"] = CollectorSessionEventKind.Verified,
    };

    private static Guid ParseTenant(string? value)
        => Guid.TryParse(value, out var tenantId) ? tenantId : throw new CollectorStoreException("INVALID_TENANT", "tenantId is invalid.", 400);

    private static Guid ParseProfileId(string? value)
        => Guid.TryParse(value, out var id) && id != Guid.Empty
            ? id
            : throw new CollectorStoreException("INVALID_SESSION_PROFILE", "sessionProfileId must be a UUID.", 400);

    private static string RequireNode(string? value)
        => value is { Length: >= 1 and <= 128 } ? value : throw new CollectorStoreException("INVALID_REQUEST", "nodeId must be 1-128 characters.", 400);

    private static CollectorPhase ParsePhase(string? value, string code)
        => Phases.TryGetValue(value ?? "", out var phase) ? phase : throw new CollectorStoreException(code, "phase is invalid.", 400);

    private static CollectorReportedTask? ParseTask(CollectorReportedTaskRequest? task, string code)
    {
        if (task is null) return null;
        if (!Guid.TryParse(task.TaskId, out var taskId) || task.LeaseVersion < 1)
            throw new CollectorStoreException(code, "task requires a UUID taskId and a positive leaseVersion.", 400);
        return new CollectorReportedTask(taskId, task.LeaseVersion);
    }

    // 服务令牌已由 CollectorServiceTokenFilter 在模型绑定之前校验过一次；这里按原有顺序（区域 → 令牌 → 租户名单）再检查，
    // 令牌比较不查库，动作被直接调用（不经过过滤器管道）时也不会跳过鉴权。
    private CollectorRegion Authenticate(string? claimedRegion, Guid? tenantId = null)
    {
        if (!string.Equals(claimedRegion, "cn", StringComparison.OrdinalIgnoreCase))
            throw new CollectorStoreException("COLLECTOR_REGION_FORBIDDEN", "Only cn collectors are enabled.", 403);
        RequireServiceToken(Request, configuration);
        if (tenantId is { } requestedTenant)
        {
            var allowedTenantIds = configuration.GetSection("Collector:ChinaTenantIds").Get<string[]>() ?? [];
            if (allowedTenantIds.Length > 0 && !allowedTenantIds.Any(value => Guid.TryParse(value, out var allowed) && allowed == requestedTenant))
                throw new CollectorStoreException("TENANT_NOT_ALLOWED", "Collector tenant is not allowed.", 403);
        }
        return CollectorRegion.Cn;
    }

    /// <summary>
    /// 只依赖请求头的共享令牌校验，过滤器与动作共用：服务端没有配置令牌（缺失或空白）、请求没有 Bearer 令牌或令牌不一致时
    /// 抛出 401 UNAUTHORIZED。比较是定长的，不查库。
    /// </summary>
    internal static void RequireServiceToken(HttpRequest request, IConfiguration configuration)
    {
        var expected = configuration["Collector:ChinaServiceToken"];
        var provided = request.Headers.Authorization.ToString();
        var token = provided.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? provided[7..] : string.Empty;
        if (string.IsNullOrWhiteSpace(expected) || !FixedEquals(token, expected))
            throw new CollectorStoreException("UNAUTHORIZED", "Collector service authentication failed.", 401);
    }

    private static bool FixedEquals(string provided, string expected)
    {
        var left = Encoding.UTF8.GetBytes(provided);
        var right = Encoding.UTF8.GetBytes(expected);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static object Error(CollectorStoreException exception) => new { error = new { code = exception.Code, message = exception.Message } };

    /// <summary>过滤器短路时使用的错误响应：与动作内 <c>StatusCode(exception.StatusCode, Error(exception))</c> 的状态码和响应体相同。</summary>
    internal static ObjectResult ErrorResult(CollectorStoreException exception) => new(Error(exception)) { StatusCode = exception.StatusCode };

    private static CollectorTaskResponse ToResponse(LeasedCollectorTask task) => new(
        task.Id, task.TenantId, task.RunId, task.CheckId, task.SiteId, task.PlatformCode, task.PlatformEntryCode,
        task.PlatformEntryVersionId, task.PlatformEntryVersion, task.PromptId, task.QuestionText, "cn", task.Language,
        task.Channel, task.LeaseVersion, IsoUtc(task.LeaseExpiresAt), task.SessionProfileId);

    /// <summary>采集器合同要求 ISO 8601 UTC 且以 Z 结尾；System.Text.Json 默认输出 +00:00 偏移，会被拒绝。</summary>
    private static string IsoUtc(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Collector 租约请求。</summary>
/// <param name="TenantId">租户标识。</param>
/// <param name="NodeId">Collector 节点标识。</param>
/// <param name="Region">Collector 所在区域，当前只接受 cn。</param>
/// <param name="SupportedPlatformCodes">节点支持的平台代码。</param>
/// <param name="Capacity">最多领取的任务数（1–50）；带 sessionProfileId 时固定为 1。</param>
/// <param name="LeaseSeconds">租约秒数（5–900）。</param>
/// <param name="SessionProfileId">可选，账号池中的账号标识（COLLECTOR_SESSION_PROFILE_ID）；省略时保持原有单账号领取，但已登记账号的平台不会派发。</param>
public sealed record CollectorLeaseRequest(string TenantId, string NodeId, string Region, IReadOnlyList<string>? SupportedPlatformCodes, int Capacity = 1, int LeaseSeconds = 60,
    string? SessionProfileId = null);

/// <summary>Collector 租约响应。</summary>
public sealed record CollectorLeaseResponse(IReadOnlyList<CollectorTaskResponse> Tasks);

/// <summary>Collector 任务响应。sessionProfileId 为租约绑定的账号，无账号租约为 null。</summary>
public sealed record CollectorTaskResponse(Guid Id, Guid TenantId, Guid RunId, Guid CheckId, Guid BrandProjectId, string PlatformCode,
    string PlatformEntryCode, Guid PlatformEntryVersionId, int PlatformEntryVersion, Guid QuestionVersionId, string QuestionText,
    string Region, string Language, string Channel, int LeaseVersion, string LeaseExpiresAt, Guid? SessionProfileId);

/// <summary>上报中引用的租约任务。</summary>
/// <param name="TaskId">采集任务标识。</param>
/// <param name="LeaseVersion">上报时持有的租约版本（正整数）。</param>
public sealed record CollectorReportedTaskRequest(string? TaskId, int LeaseVersion);

/// <summary>账号模式 Collector 的只读预检请求。</summary>
/// <param name="TenantId">租户标识。</param>
/// <param name="NodeId">Collector 节点标识（1–128 个字符）。</param>
/// <param name="Region">Collector 所在区域，当前只接受 cn。</param>
/// <param name="SessionProfileId">账号标识。</param>
/// <param name="PlatformCode">账号所属平台代码。</param>
public sealed record CollectorEligibilityRequest(string? TenantId, string? NodeId, string? Region, string? SessionProfileId, string? PlatformCode);

/// <summary>Collector 分类失败上报。probe 阶段不带 task；pre_send / post_send 必须带 task。</summary>
/// <param name="TenantId">租户标识。</param>
/// <param name="NodeId">Collector 节点标识（1–128 个字符）。</param>
/// <param name="Region">Collector 所在区域，当前只接受 cn。</param>
/// <param name="SessionProfileId">账号标识。</param>
/// <param name="FailureCode">失败码：login_required、region_restricted、challenge_required、rate_limited、page_contract_changed、page_contract_unverified、collection_timeout、browser_failed。</param>
/// <param name="Phase">阶段：probe、pre_send、post_send。</param>
/// <param name="Task">失败时持有的租约任务。</param>
public sealed record CollectorFailureRequest(string? TenantId, string? NodeId, string? Region, string? SessionProfileId,
    string? FailureCode, string? Phase, CollectorReportedTaskRequest? Task = null);

/// <summary>Collector 非失败账号事件。verified 只能是 probe 阶段且不带 task；验证码事件必须带 task。</summary>
/// <param name="TenantId">租户标识。</param>
/// <param name="NodeId">Collector 节点标识（1–128 个字符）。</param>
/// <param name="Region">Collector 所在区域，当前只接受 cn。</param>
/// <param name="SessionProfileId">账号标识。</param>
/// <param name="EventKind">事件：challenge_wait_started、challenge_cleared、verified。</param>
/// <param name="Phase">阶段：probe、pre_send、post_send。</param>
/// <param name="Task">事件发生时持有的租约任务。</param>
public sealed record CollectorSessionEventRequest(string? TenantId, string? NodeId, string? Region, string? SessionProfileId,
    string? EventKind, string? Phase, CollectorReportedTaskRequest? Task = null);

/// <summary>Collector 心跳请求。</summary>
public sealed record CollectorHeartbeatRequest(Guid TenantId, Guid TaskId, string NodeId, int LeaseVersion, int ExtendSeconds = 60, string? Region = null);

/// <summary>Collector 心跳响应。</summary>
/// <param name="LeaseExpiresAt">租约截止时间，ISO 8601 UTC（以 Z 结尾）。</param>
public sealed record CollectorHeartbeatResponse(string LeaseExpiresAt);

/// <summary>Collector 结果请求。</summary>
/// <param name="TenantId">租户标识。</param>
/// <param name="TaskId">采集任务标识。</param>
/// <param name="NodeId">Collector 节点标识。</param>
/// <param name="Region">Collector 所在区域，当前只接受 cn。</param>
/// <param name="LeaseVersion">提交时持有的租约版本。</param>
/// <param name="IdempotencyKey">任务范围内的结果幂等键，8–191 个字符。</param>
/// <param name="EgressCountry">采集出口国家，国内结果必须为 CN。</param>
/// <param name="CollectorBuildRevision">Collector 构建版本。</param>
/// <param name="AnswerText">回答正文。</param>
/// <param name="Citations">按页面顺序排列的引用链接，最多 100 条。</param>
/// <param name="CompletedAt">采集完成时间，必须为 UTC。</param>
/// <param name="ProviderMetadata">平台元数据 JSON。</param>
/// <param name="CitationTitles">可选，引用标题，与 citations 按位对齐且长度必须一致；单项可为 null，去除首尾空白后最多 512 个字符，空白视为无标题。</param>
/// <param name="SearchQueries">可选，页面展示的搜索关键词，最多 32 个，每个去除首尾空白后为 1–500 个字符；省略时保持关键词状态未知，空数组表示页面未展示关键词。</param>
public sealed record CollectorResultRequest(Guid TenantId, Guid TaskId, string NodeId, string Region, int LeaseVersion,
    string IdempotencyKey, string EgressCountry, string CollectorBuildRevision, string AnswerText,
    IReadOnlyList<string>? Citations, DateTimeOffset CompletedAt, JsonElement? ProviderMetadata = null,
    IReadOnlyList<string?>? CitationTitles = null, IReadOnlyList<string>? SearchQueries = null);
