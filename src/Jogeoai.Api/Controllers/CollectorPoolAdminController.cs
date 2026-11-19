using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Jogeoai.Api.Controllers;

/// <summary>
/// 系统管理员运维采集器账号池（迁移 035）：登记 / 启停账号、设置平台闸门、解除熔断、按账号查看指标。
/// 只保存非敏感元数据；label 是运维别名，不得填写手机号、邮箱或账号名。每个写操作都追加一条健康事件。
/// </summary>
[ApiController]
[Route("api/v1/admin/collector-pool")]
public sealed class CollectorPoolAdminController(ICollectorSessionPoolStore store, IResearchStore adminStore) : ControllerBase
{
    /// <summary>按账号列出状态、配额与近 1 / 24 小时领取、成功、验证码指标。</summary>
    /// <param name="tenantId">租户标识。</param>
    /// <param name="platformCode">可选，按平台过滤。</param>
    /// <param name="region">可选，按区域过滤（cn）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpGet("session-profiles")]
    [ProducesResponseType(typeof(IReadOnlyList<CollectorSessionProfileMetrics>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<IReadOnlyList<CollectorSessionProfileMetrics>>> ListProfiles([FromQuery] Guid tenantId,
        [FromQuery] string? platformCode, [FromQuery] string? region, CancellationToken cancellationToken)
    {
        if (await SystemAdministratorAccess.RequireAsync(this, adminStore, cancellationToken) is { } failure) return failure;
        try { return Ok(await store.ListProfileMetricsAsync(tenantId, platformCode, region, cancellationToken)); }
        catch (CollectorStoreException exception) { return Error(exception); }
    }

    /// <summary>登记一个授权账号，返回其标识（即 Collector 的 COLLECTOR_SESSION_PROFILE_ID）。</summary>
    /// <remarks>登记后，该租户该平台不带账号标识的领取请求不再派发任务（含账号停用后）。</remarks>
    /// <param name="request">租户、平台、区域、别名与可选配额。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpPost("session-profiles")]
    [ProducesResponseType(typeof(CollectorSessionProfileCreated), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CollectorSessionProfileCreated>> RegisterProfile([FromBody] RegisterCollectorSessionProfileRequest request,
        CancellationToken cancellationToken)
    {
        if (await SystemAdministratorAccess.RequireAsync(this, adminStore, cancellationToken) is { } failure) return failure;
        try
        {
            var id = await store.RegisterProfileAsync(new RegisterCollectorSessionProfileCommand(request.TenantId, request.PlatformCode,
                request.Region, request.Label, request.HourlyQuota, request.DailyQuota, request.MinIntervalSeconds, OperatorId()), cancellationToken);
            return StatusCode(StatusCodes.Status201Created, new CollectorSessionProfileCreated(id));
        }
        catch (CollectorStoreException exception) { return Error(exception); }
    }

    /// <summary>启用或停用账号。启用同时清除冷却和连续验证码次数，应先确认账号健康（优先使用 Collector probe）。</summary>
    /// <param name="id">账号标识。</param>
    /// <param name="request">租户与目标状态（active 或 disabled）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpPut("session-profiles/{id:guid}/status")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> SetProfileStatus(Guid id, [FromBody] SetCollectorSessionProfileStatusRequest request,
        CancellationToken cancellationToken)
    {
        if (await SystemAdministratorAccess.RequireAsync(this, adminStore, cancellationToken) is { } failure) return failure;
        try
        {
            await store.SetProfileStatusAsync(request.TenantId, id, request.Status, OperatorId(), cancellationToken);
            return NoContent();
        }
        catch (CollectorStoreException exception) { return Error(exception); }
    }

    /// <summary>列出租户的平台闸门状态；没有记录的平台按默认值运行。</summary>
    /// <param name="tenantId">租户标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpGet("platform-dispatch-states")]
    [ProducesResponseType(typeof(IReadOnlyList<CollectorPlatformDispatchStateView>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<CollectorPlatformDispatchStateView>>> ListPlatforms([FromQuery] Guid tenantId,
        CancellationToken cancellationToken)
    {
        if (await SystemAdministratorAccess.RequireAsync(this, adminStore, cancellationToken) is { } failure) return failure;
        try { return Ok(await store.ListPlatformStatesAsync(tenantId, cancellationToken)); }
        catch (CollectorStoreException exception) { return Error(exception); }
    }

    /// <summary>设置平台闸门参数；省略的字段保持当前值（或默认值）。</summary>
    /// <param name="request">租户、区域、平台与要修改的参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpPut("platform-dispatch-states")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> ConfigurePlatform([FromBody] ConfigureCollectorPlatformDispatchRequest request, CancellationToken cancellationToken)
    {
        if (await SystemAdministratorAccess.RequireAsync(this, adminStore, cancellationToken) is { } failure) return failure;
        try
        {
            await store.ConfigurePlatformAsync(new ConfigureCollectorPlatformDispatchCommand(request.TenantId, request.Region, request.PlatformCode,
                request.MinIntervalSeconds, request.DailyCap, request.BreakerProfileThreshold, request.BreakerWindowMinutes,
                request.RetryBackoffSeconds, request.MaxTaskFailures, request.RateLimitCooldownBaseSeconds,
                request.RateLimitCooldownMaxSeconds, request.ProbeFailureOfflineMinutes, OperatorId()), cancellationToken);
            return NoContent();
        }
        catch (CollectorStoreException exception) { return Error(exception); }
    }

    /// <summary>人工解除平台熔断；熔断不会自动恢复。</summary>
    /// <param name="request">租户、区域与平台。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpPost("platform-dispatch-states/resume")]
    [ProducesResponseType(typeof(CollectorPlatformResumeResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CollectorPlatformResumeResult>> ResumePlatform([FromBody] CollectorPlatformRequest request,
        CancellationToken cancellationToken)
    {
        if (await SystemAdministratorAccess.RequireAsync(this, adminStore, cancellationToken) is { } failure) return failure;
        try
        {
            return Ok(new CollectorPlatformResumeResult(
                await store.ResumePlatformAsync(request.TenantId, request.Region, request.PlatformCode, OperatorId(), cancellationToken)));
        }
        catch (CollectorStoreException exception) { return Error(exception); }
    }

    private Guid? OperatorId() => SessionAuthenticationMiddleware.GetContext(HttpContext)?.User.UserId;

    private ObjectResult Error(CollectorStoreException exception)
        => StatusCode(exception.StatusCode, new { error = new { code = exception.Code, message = exception.Message } });
}

/// <summary>登记账号请求。</summary>
/// <param name="TenantId">租户标识。</param>
/// <param name="PlatformCode">平台代码，例如 doubao。</param>
/// <param name="Region">区域，必须与平台区域一致（cn）。</param>
/// <param name="Label">运维别名，1–128 个字符，同平台唯一，例如 doubao-a。</param>
/// <param name="HourlyQuota">每小时配额，默认 4。</param>
/// <param name="DailyQuota">每日配额，默认 30。</param>
/// <param name="MinIntervalSeconds">账号最小领取间隔秒数，默认 600。</param>
public sealed record RegisterCollectorSessionProfileRequest(Guid TenantId, string PlatformCode, string Region, string Label,
    int? HourlyQuota = null, int? DailyQuota = null, int? MinIntervalSeconds = null);

/// <summary>登记账号结果。</summary>
/// <param name="Id">账号标识，配置为 Collector 的 COLLECTOR_SESSION_PROFILE_ID。</param>
public sealed record CollectorSessionProfileCreated(Guid Id);

/// <summary>启停账号请求。</summary>
/// <param name="TenantId">租户标识。</param>
/// <param name="Status">目标状态，只允许 active 或 disabled。</param>
public sealed record SetCollectorSessionProfileStatusRequest(Guid TenantId, CollectorSessionProfileStatus Status);

/// <summary>平台闸门参数；省略的字段保持原值。</summary>
/// <param name="TenantId">租户标识。</param>
/// <param name="Region">区域（cn）。</param>
/// <param name="PlatformCode">平台代码。</param>
/// <param name="MinIntervalSeconds">平台最小派发间隔秒数（0–86400），默认 120。</param>
/// <param name="DailyCap">平台每日总上限（北京时间自然日），默认 120。</param>
/// <param name="BreakerProfileThreshold">窗口内遇到验证码的不同账号数达到该值即熔断，默认 2。</param>
/// <param name="BreakerWindowMinutes">熔断统计窗口分钟数，默认 60。</param>
/// <param name="RetryBackoffSeconds">失败任务重新入队的退避秒数，默认 1800。</param>
/// <param name="MaxTaskFailures">任务失败上限，达到后任务终止，默认 3。</param>
/// <param name="RateLimitCooldownBaseSeconds">rate_limited 冷却起始秒数（连续限流翻倍），默认 1800。</param>
/// <param name="RateLimitCooldownMaxSeconds">rate_limited 冷却上限秒数，默认 28800。</param>
/// <param name="ProbeFailureOfflineMinutes">probe 失败后退出其他账号公平队列的分钟数（1–1440），默认 15。</param>
public sealed record ConfigureCollectorPlatformDispatchRequest(Guid TenantId, string Region, string PlatformCode,
    int? MinIntervalSeconds = null, int? DailyCap = null, int? BreakerProfileThreshold = null, int? BreakerWindowMinutes = null,
    int? RetryBackoffSeconds = null, int? MaxTaskFailures = null, int? RateLimitCooldownBaseSeconds = null,
    int? RateLimitCooldownMaxSeconds = null, int? ProbeFailureOfflineMinutes = null);

/// <summary>指定租户的一个平台。</summary>
/// <param name="TenantId">租户标识。</param>
/// <param name="Region">区域（cn）。</param>
/// <param name="PlatformCode">平台代码。</param>
public sealed record CollectorPlatformRequest(Guid TenantId, string Region, string PlatformCode);

/// <summary>解除熔断结果。</summary>
/// <param name="Resumed">true 表示本次解除了熔断；false 表示平台本来未暂停。</param>
public sealed record CollectorPlatformResumeResult(bool Resumed);
