using Jogeoai.Api;
using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Jogeoai.Api.Controllers;

/// <summary>管理站点的每日或每周采集计划。</summary>
[ApiController]
[Route("api/v1/sites/{siteId:guid}/collection-schedules")]
public sealed class CollectionSchedulesController(ICollectionScheduleStore schedules, TenantContextResolver tenantContext) : ControllerBase
{
    /// <summary>按创建顺序列出当前站点的采集计划。</summary>
    /// <param name="siteId">站点标识。</param>
    /// <param name="cursor">上一页返回的游标。</param>
    /// <param name="limit">每页数量，范围 1–100，默认 50。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpGet]
    [ProducesResponseType(typeof(CursorPage<CollectionScheduleView>), StatusCodes.Status200OK)]
    public async Task<ActionResult<CursorPage<CollectionScheduleView>>> List(Guid siteId,
        [FromQuery] string? cursor, [FromQuery] int limit = 50, CancellationToken cancellationToken = default)
    {
        if (!TryTenant(out var tenantId, out var failure)) return failure!;
        try { return Ok(await schedules.ListAsync(tenantId, siteId, cursor, limit, cancellationToken)); }
        catch (CollectorStoreException exception) { return StoreError(exception); }
    }

    /// <summary>创建采集计划；未指定 enabled 时保持关闭。App 采集计划的名称为系统保留，使用它返回 400 INVALID_SCHEDULE_NAME。</summary>
    /// <param name="siteId">站点标识。</param>
    /// <param name="request">计划时区、频率和固定采集选择。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpPost]
    [ProducesResponseType(typeof(CollectionScheduleView), StatusCodes.Status201Created)]
    public async Task<ActionResult<CollectionScheduleView>> Create(Guid siteId,
        [FromBody] CreateCollectionScheduleRequest request, CancellationToken cancellationToken)
    {
        if (!TryTenant(out var tenantId, out var failure, requireWriter: true)) return failure!;
        try
        {
            var result = await schedules.CreateAsync(new CreateCollectionScheduleCommand(tenantId, siteId,
                request.Name, request.Cadence, request.DayOfWeek, request.LocalTime, request.TimeZone,
                request.Selection, request.Enabled), cancellationToken);
            return CreatedAtAction(nameof(List), new { siteId }, result);
        }
        catch (CollectorStoreException exception) { return StoreError(exception); }
    }

    /// <summary>
    /// 修改计划配置或启停状态；已领取的时隙仍使用领取时快照。App 采集计划（jogeoai:auto-prompt-monitoring:app:v1）由系统维护，
    /// 只能修改 enabled，修改其他属性返回 400 INVALID_SCHEDULE；品牌已有每日监控计划时它的启停跟随每日监控计划，
    /// 单独修改返回 409 APP_SCHEDULE_FOLLOWS_MAIN。
    /// </summary>
    /// <param name="siteId">站点标识。</param>
    /// <param name="id">计划标识。</param>
    /// <param name="request">要修改的属性；未传属性保持原值。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpPatch("{id:guid}")]
    [ProducesResponseType(typeof(CollectionScheduleView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CollectionScheduleView>> Update(Guid siteId, Guid id,
        [FromBody] UpdateCollectionScheduleRequest request, CancellationToken cancellationToken)
    {
        if (!TryTenant(out var tenantId, out var failure, requireWriter: true)) return failure!;
        try
        {
            var result = await schedules.UpdateAsync(new UpdateCollectionScheduleCommand(tenantId, siteId, id, request.ExpectedVersion,
                request.Name, request.Cadence, request.DayOfWeek, request.LocalTime, request.TimeZone,
                request.Selection, request.Enabled), cancellationToken);
            return result is null ? NotFound(new { error = new { code = "RESOURCE_NOT_FOUND", message = "计划不存在。" } }) : Ok(result);
        }
        catch (CollectorStoreException exception) { return StoreError(exception); }
    }

    private bool TryTenant(out Guid tenantId, out ActionResult? failure, bool requireWriter = false)
    {
        tenantId = Guid.Empty;
        try
        {
            tenantId = tenantContext.Resolve(HttpContext);
            if (requireWriter)
            {
                var identity = SessionAuthenticationMiddleware.GetContext(HttpContext);
                var resolvedTenantId = tenantId;
                var membership = identity?.User.Memberships.FirstOrDefault(item => item.TenantId == resolvedTenantId && item.Status == "active");
                if (membership?.Role.Equals("viewer", StringComparison.OrdinalIgnoreCase) == true)
                    throw new CollectorStoreException("CONTENT_FORBIDDEN", "当前角色无权修改监控计划。", 403);
            }
            failure = null;
            return true;
        }
        catch (CollectorStoreException exception)
        {
            failure = StatusCode(exception.StatusCode,
                new { error = new { code = exception.Code, message = exception.Message } });
            return false;
        }
    }

    private ActionResult StoreError(CollectorStoreException exception) =>
        StatusCode(exception.StatusCode, new { error = new { code = exception.Code, message = exception.Message } });
}

/// <summary>创建采集计划的请求。</summary>
/// <param name="Name">计划名称。</param>
/// <param name="Cadence">daily 或 weekly。</param>
/// <param name="DayOfWeek">每周执行日；星期日为 0，星期六为 6。</param>
/// <param name="LocalTime">当地时刻，格式 HH:mm。</param>
/// <param name="TimeZone">IANA 时区标识。</param>
/// <param name="Selection">固定的 Prompt、Target 和样本数。</param>
/// <param name="Enabled">是否立即启用，默认为关闭。</param>
public sealed record CreateCollectionScheduleRequest(string Name, string Cadence, int? DayOfWeek,
    string LocalTime, string TimeZone, CollectionScheduleSelection Selection, bool Enabled = false);

/// <summary>修改采集计划的请求；null 属性保持原值。</summary>
/// <param name="ExpectedVersion">当前配置版本，版本不匹配返回 409。</param>
/// <param name="Name">新的名称。</param>
/// <param name="Cadence">新的 daily 或 weekly 频率。</param>
/// <param name="DayOfWeek">新的每周执行日。</param>
/// <param name="LocalTime">新的当地时刻。</param>
/// <param name="TimeZone">新的 IANA 时区。</param>
/// <param name="Selection">新的固定采集选择。</param>
/// <param name="Enabled">新的启用状态。</param>
public sealed record UpdateCollectionScheduleRequest(int ExpectedVersion, string? Name = null, string? Cadence = null,
    int? DayOfWeek = null, string? LocalTime = null, string? TimeZone = null,
    CollectionScheduleSelection? Selection = null, bool? Enabled = null);
