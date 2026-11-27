using System.Globalization;
using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Jogeoai.Api.Controllers;

/// <summary>
/// 系统管理员运维 App 全局账号池（迁移 049，implement 8.3）：节点与令牌、绑定、平台、容量与队列、任务的尝试历史、提问的人工排除、审计事件、
/// 当前告警信息与每日汇报的历史。全部路由只限系统管理员：未登录 401，非系统管理员 403，授权在任何读写之前完成。
/// 每个实际发生变化的写操作都追加一条 actor 为运营人员的审计事件。节点令牌的明文只出现在登记和轮换的响应里，库里只存哈希。
/// </summary>
/// <param name="pool">App 账号池（节点登记、令牌、节点启停、绑定恢复、平台恢复）。</param>
/// <param name="admin">App 账号池的运营读写。</param>
/// <param name="collection">App 周期 Run 与提问的人工排除。</param>
/// <param name="capacity">各平台的容量视图。</param>
/// <param name="alerts">当前告警信息（与每日汇报同一个查询）。</param>
/// <param name="reports">每日汇报的存档。</param>
/// <param name="adminStore">系统管理员校验。</param>
/// <param name="logger">日志；不记录令牌、提问原文或排除原因。</param>
[ApiController]
[Route("api/v1/admin/collector-app")]
public sealed class AppCollectorAdminController(IAppCollectorPoolStore pool, IAppCollectorAdminStore admin, IAppCollectionStore collection,
    IAppCapacityQuery capacity, IAppAlertQuery alerts, IAppDailyReportStore reports, IResearchStore adminStore,
    ILogger<AppCollectorAdminController> logger) : ControllerBase
{
    // ---------------- 节点 ----------------

    /// <summary>列出全部 App 采集节点：状态、最近出现时间、是否失联（超过 15 分钟没有出现）、绑定数、设备上限。不返回令牌。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <response code="200">节点列表，按节点标识排序。</response>
    /// <response code="401">未登录（AUTHENTICATION_REQUIRED）。</response>
    /// <response code="403">不是系统管理员（ADMIN_REQUIRED）。</response>
    /// <response code="503">迁移 049 或 052 未部署（APP_CHANNEL_SCHEMA_NOT_MIGRATED）。</response>
    [HttpGet("nodes")]
    [ProducesResponseType(typeof(IReadOnlyList<AppNodeView>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public Task<ActionResult<IReadOnlyList<AppNodeView>>> ListNodes(CancellationToken cancellationToken)
        => RunAsync(() => admin.ListNodesAsync(cancellationToken), cancellationToken);

    /// <summary>登记一个 App 采集节点并签发令牌。令牌明文只在这个响应里出现一次，库里只存它的 SHA-256；请立即配置到该节点上。</summary>
    /// <param name="request">节点标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <response code="201">节点与令牌明文（只出现这一次）。</response>
    /// <response code="400">节点标识不是 1–128 个字符（INVALID_APP_NODE）。</response>
    /// <response code="401">未登录（AUTHENTICATION_REQUIRED）。</response>
    /// <response code="403">不是系统管理员（ADMIN_REQUIRED）。</response>
    /// <response code="409">节点已登记（APP_NODE_EXISTS）。</response>
    [HttpPost("nodes")]
    [ProducesResponseType(typeof(AppNodeCredential), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AppNodeCredential>> RegisterNode([FromBody] RegisterAppNodeRequest request, CancellationToken cancellationToken)
    {
        if (await SystemAdministratorAccess.RequireAsync(this, adminStore, cancellationToken) is { } failure) return failure;
        try { return StatusCode(StatusCodes.Status201Created, await pool.RegisterNodeAsync(request?.NodeId ?? "", OperatorId(), cancellationToken)); }
        catch (CollectorStoreException exception) { return Error(exception); }
    }

    /// <summary>轮换节点令牌：签发新令牌，旧令牌立即失效。新令牌明文只在这个响应里出现一次。</summary>
    /// <param name="nodeId">节点标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <response code="200">节点与新令牌明文（只出现这一次）。</response>
    /// <response code="401">未登录（AUTHENTICATION_REQUIRED）。</response>
    /// <response code="403">不是系统管理员（ADMIN_REQUIRED）。</response>
    /// <response code="404">节点不存在（APP_NODE_NOT_FOUND）。</response>
    [HttpPost("nodes/{nodeId}/token")]
    [ProducesResponseType(typeof(AppNodeCredential), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status404NotFound)]
    public Task<ActionResult<AppNodeCredential>> RotateNodeToken(string nodeId, CancellationToken cancellationToken)
        => RunAsync(() => pool.RotateNodeTokenAsync(nodeId, OperatorId(), cancellationToken), cancellationToken);

    /// <summary>停用节点：它的全部请求被拒绝（403 APP_NODE_DISABLED），其他节点不受影响；它的绑定不计入容量。已停用时不做修改。</summary>
    /// <param name="nodeId">节点标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <response code="200">是否发生了变化，以及操作之后的节点。</response>
    /// <response code="401">未登录（AUTHENTICATION_REQUIRED）。</response>
    /// <response code="403">不是系统管理员（ADMIN_REQUIRED）。</response>
    /// <response code="404">节点不存在（APP_NODE_NOT_FOUND）。</response>
    [HttpPost("nodes/{nodeId}/disable")]
    [ProducesResponseType(typeof(AppNodeChange), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status404NotFound)]
    public Task<ActionResult<AppNodeChange>> DisableNode(string nodeId, CancellationToken cancellationToken)
        => RunAsync(() => SetNodeStatusAsync(nodeId, AppNodeStatus.Disabled, cancellationToken), cancellationToken);

    /// <summary>恢复被停用的节点。已是启用状态时不做修改。</summary>
    /// <param name="nodeId">节点标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <response code="200">是否发生了变化，以及操作之后的节点。</response>
    /// <response code="401">未登录（AUTHENTICATION_REQUIRED）。</response>
    /// <response code="403">不是系统管理员（ADMIN_REQUIRED）。</response>
    /// <response code="404">节点不存在（APP_NODE_NOT_FOUND）。</response>
    [HttpPost("nodes/{nodeId}/resume")]
    [ProducesResponseType(typeof(AppNodeChange), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status404NotFound)]
    public Task<ActionResult<AppNodeChange>> ResumeNode(string nodeId, CancellationToken cancellationToken)
        => RunAsync(() => SetNodeStatusAsync(nodeId, AppNodeStatus.Active, cancellationToken), cancellationToken);

    /// <summary>
    /// 设置节点可登记的不同设备（AVD）数上限，默认 4。节点上报的绑定里，超出上限的新设备会被拒绝（device_limit_exceeded）且不入库；
    /// 已登记设备的绑定不受影响。不能调到低于该节点已登记的设备数。与现值相同时不做修改。
    /// </summary>
    /// <param name="nodeId">节点标识。</param>
    /// <param name="request">新的设备上限。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <response code="200">是否发生了变化，以及操作之后的节点。</response>
    /// <response code="400">maxDevices 缺失或不在 1–64 之间（INVALID_APP_LIMITS）。</response>
    /// <response code="401">未登录（AUTHENTICATION_REQUIRED）。</response>
    /// <response code="403">不是系统管理员（ADMIN_REQUIRED）。</response>
    /// <response code="404">节点不存在（APP_NODE_NOT_FOUND）。</response>
    /// <response code="409">新的上限低于该节点已登记的设备数（APP_NODE_MAX_DEVICES_BELOW_REGISTERED）。</response>
    /// <response code="503">迁移 049 或 052 未部署（APP_CHANNEL_SCHEMA_NOT_MIGRATED）。</response>
    [HttpPut("nodes/{nodeId}/max-devices")]
    [ProducesResponseType(typeof(AppNodeChange), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public Task<ActionResult<AppNodeChange>> SetNodeMaxDevices(string nodeId, [FromBody] AppNodeMaxDevicesRequest request,
        CancellationToken cancellationToken)
        => RunAsync(async () =>
        {
            // Checked here, before any store call; the store checks the same range again.
            if (request?.MaxDevices is not { } maxDevices || !AppAdminRules.MaxDevicesValid(maxDevices))
                throw new CollectorStoreException("INVALID_APP_LIMITS", $"maxDevices 必填，且必须在 1–{AppPoolRules.MaxDevicesPerNodeLimit} 之间。", 400);
            return new AppNodeChange(await admin.SetNodeMaxDevicesAsync(nodeId, maxDevices, OperatorId(), cancellationToken),
                await admin.GetNodeAsync(nodeId, cancellationToken));
        }, cancellationToken);

    private async Task<AppNodeChange> SetNodeStatusAsync(string nodeId, AppNodeStatus status, CancellationToken cancellationToken)
    {
        var before = await admin.GetNodeAsync(nodeId, cancellationToken);
        await pool.SetNodeStatusAsync(nodeId, status, OperatorId(), cancellationToken);
        return new AppNodeChange(before.Status != status, await admin.GetNodeAsync(nodeId, cancellationToken));
    }

    // ---------------- 绑定 ----------------

    /// <summary>
    /// 列出绑定：状态、停止原因与停止时长、是否待批准与批准时间、冷却、是否离线、最近出现 / 领取 / 成功时间、领取间隔和每日上限。账号只以代号出现。
    /// 采集节点新登记的绑定是待批准（approvalPending 为 true，状态 stopped、原因 disabled_by_operator），调用恢复接口批准后才能领取任务。
    /// </summary>
    /// <param name="nodeId">可选，只看该节点的绑定。</param>
    /// <param name="platformCode">可选，只看该平台的绑定，例如 doubao。</param>
    /// <param name="status">可选，只看该状态的绑定：active 或 stopped。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <response code="200">绑定列表，按平台、节点、设备排序。</response>
    /// <response code="400">筛选条件不合法（INVALID_FILTER、INVALID_PLATFORM、INVALID_APP_NODE）。</response>
    /// <response code="401">未登录（AUTHENTICATION_REQUIRED）。</response>
    /// <response code="403">不是系统管理员（ADMIN_REQUIRED）。</response>
    [HttpGet("bindings")]
    [ProducesResponseType(typeof(IReadOnlyList<AppBindingAdminView>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status403Forbidden)]
    public Task<ActionResult<IReadOnlyList<AppBindingAdminView>>> ListBindings([FromQuery] string? nodeId, [FromQuery] string? platformCode,
        [FromQuery] string? status, CancellationToken cancellationToken)
        => RunAsync(() =>
        {
            AppBindingStatus? parsed = null;
            if (!string.IsNullOrWhiteSpace(status))
                parsed = AppContractNames.TryParse<AppBindingStatus>(status, out var value)
                    ? value
                    : throw new CollectorStoreException("INVALID_FILTER", "status 只能是 active 或 stopped。", 400);
            return admin.ListBindingsAsync(nodeId, platformCode, parsed, cancellationToken);
        }, cancellationToken);

    /// <summary>
    /// 运营停用绑定（停止原因 disabled_by_operator）：之后它的领取返回 binding_stopped；采集器上报 ok 或其他停止原因都不会解除，只能由运营恢复。
    /// 原来的停止原因被替换，记在审计事件里。正在执行的租约不收回。已是运营停用时不做修改。
    /// </summary>
    /// <param name="bindingId">绑定标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <response code="200">是否发生了变化，以及操作之后的绑定。</response>
    /// <response code="401">未登录（AUTHENTICATION_REQUIRED）。</response>
    /// <response code="403">不是系统管理员（ADMIN_REQUIRED）。</response>
    /// <response code="404">绑定不存在（APP_BINDING_NOT_FOUND）。</response>
    [HttpPost("bindings/{bindingId:guid}/disable")]
    [ProducesResponseType(typeof(AppBindingChange), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status404NotFound)]
    public Task<ActionResult<AppBindingChange>> DisableBinding(Guid bindingId, CancellationToken cancellationToken)
        => RunAsync(async () => new AppBindingChange(await admin.DisableBindingAsync(bindingId, OperatorId(), cancellationToken),
            await admin.GetBindingAsync(bindingId, cancellationToken)), cancellationToken);

    /// <summary>
    /// 恢复一个已停止的绑定（任何停止原因，包括需要登录、需要人工验证、App 拒绝模拟器和运营停用）。冷却不受影响；
    /// 平台被判定不可用的 App 入口不会随之恢复，需要另外调用恢复入口。先确认账号已在模拟器内由授权人员处理好。
    /// 这个接口同时是批准：采集节点新登记的绑定在这里第一次恢复时记下批准时间，之后才能领取任务；
    /// 批准前先核对节点、设备和账号代号确实是自己接入的。已批准的绑定再次停用、恢复，批准时间不变。
    /// </summary>
    /// <param name="bindingId">绑定标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <response code="200">是否发生了变化，以及操作之后的绑定。</response>
    /// <response code="401">未登录（AUTHENTICATION_REQUIRED）。</response>
    /// <response code="403">不是系统管理员（ADMIN_REQUIRED）。</response>
    /// <response code="404">绑定不存在（APP_BINDING_NOT_FOUND）。</response>
    [HttpPost("bindings/{bindingId:guid}/resume")]
    [ProducesResponseType(typeof(AppBindingChange), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status404NotFound)]
    public Task<ActionResult<AppBindingChange>> ResumeBinding(Guid bindingId, CancellationToken cancellationToken)
        => RunAsync(async () => new AppBindingChange(await pool.ResumeBindingAsync(bindingId, OperatorId(), cancellationToken),
            await admin.GetBindingAsync(bindingId, cancellationToken)), cancellationToken);

    /// <summary>
    /// 已退役的兼容接口。device-v1 使用设备实际发送后的固定冷却，不再修改绑定间隔或每日上限。
    /// </summary>
    /// <param name="bindingId">绑定标识。</param>
    /// <param name="request">新的领取间隔与每日上限。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <response code="200">是否发生了变化，以及操作之后的绑定。</response>
    /// <response code="400">取值超出范围（INVALID_APP_LIMITS）。</response>
    /// <response code="401">未登录（AUTHENTICATION_REQUIRED）。</response>
    /// <response code="403">不是系统管理员（ADMIN_REQUIRED）。</response>
    /// <response code="409">旧限速设置已退役（APP_LEGACY_LIMITS_RETIRED）。</response>
    [HttpPut("bindings/{bindingId:guid}/limits")]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status404NotFound)]
    public Task<ActionResult<AppBindingChange>> SetBindingLimits(Guid bindingId, [FromBody] AppBindingLimitsRequest request,
        CancellationToken cancellationToken)
        => RunAsync(async () =>
        {
            var interval = request?.MinIntervalSeconds ?? throw new CollectorStoreException("INVALID_APP_LIMITS", "minIntervalSeconds 必填。", 400);
            return new AppBindingChange(await admin.SetBindingLimitsAsync(bindingId, interval, request.DailyQuota, OperatorId(), cancellationToken),
                await admin.GetBindingAsync(bindingId, cancellationToken));
        }, cancellationToken);

    // ---------------- 平台 ----------------

    /// <summary>列出平台的暂停及入口状态。旧平台间隔和每日上限固定为 0 和 null，均不参与派单。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <response code="200">平台列表，按平台顺序。</response>
    /// <response code="401">未登录（AUTHENTICATION_REQUIRED）。</response>
    /// <response code="403">不是系统管理员（ADMIN_REQUIRED）。</response>
    [HttpGet("platforms")]
    [ProducesResponseType(typeof(IReadOnlyList<AppPlatformStateView>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status403Forbidden)]
    public Task<ActionResult<IReadOnlyList<AppPlatformStateView>>> ListPlatforms(CancellationToken cancellationToken)
        => RunAsync(() => admin.ListPlatformsAsync(cancellationToken), cancellationToken);

    /// <summary>运营暂停平台：之后该平台的领取返回 platform_paused。已暂停（含验证熔断）时不做修改，保留原来的暂停原因。</summary>
    /// <param name="platformCode">平台代码，例如 doubao。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <response code="200">是否发生了变化，以及操作之后的平台。</response>
    /// <response code="400">不是 App 平台（INVALID_PLATFORM）。</response>
    /// <response code="401">未登录（AUTHENTICATION_REQUIRED）。</response>
    /// <response code="403">不是系统管理员（ADMIN_REQUIRED）。</response>
    [HttpPost("platforms/{platformCode}/pause")]
    [ProducesResponseType(typeof(AppPlatformChange), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status403Forbidden)]
    public Task<ActionResult<AppPlatformChange>> PausePlatform(string platformCode, CancellationToken cancellationToken)
        => RunAsync(async () => new AppPlatformChange(await admin.PausePlatformAsync(platformCode, OperatorId(), cancellationToken),
            await PlatformAsync(platformCode, cancellationToken)), cancellationToken);

    /// <summary>解除平台的暂停（运营暂停或验证熔断）；验证熔断的计数从这一刻重新开始。未暂停时不做修改。</summary>
    /// <param name="platformCode">平台代码。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <response code="200">是否发生了变化，以及操作之后的平台。</response>
    /// <response code="400">不是 App 平台（INVALID_PLATFORM）。</response>
    /// <response code="401">未登录（AUTHENTICATION_REQUIRED）。</response>
    /// <response code="403">不是系统管理员（ADMIN_REQUIRED）。</response>
    [HttpPost("platforms/{platformCode}/resume")]
    [ProducesResponseType(typeof(AppPlatformChange), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status403Forbidden)]
    public Task<ActionResult<AppPlatformChange>> ResumePlatform(string platformCode, CancellationToken cancellationToken)
        => RunAsync(async () => new AppPlatformChange(await pool.ResumePlatformAsync(platformCode, OperatorId(), cancellationToken),
            await PlatformAsync(platformCode, cancellationToken)), cancellationToken);

    /// <summary>
    /// 已退役的兼容接口。device-v1 不再修改平台领取间隔或每日上限。
    /// </summary>
    /// <param name="platformCode">平台代码。</param>
    /// <param name="request">新的领取间隔与每日上限。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <response code="200">是否发生了变化，以及操作之后的平台。</response>
    /// <response code="400">不是 App 平台（INVALID_PLATFORM），或取值超出范围（INVALID_APP_LIMITS）。</response>
    /// <response code="401">未登录（AUTHENTICATION_REQUIRED）。</response>
    /// <response code="403">不是系统管理员（ADMIN_REQUIRED）。</response>
    [HttpPut("platforms/{platformCode}/limits")]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status403Forbidden)]
    public Task<ActionResult<AppPlatformChange>> SetPlatformLimits(string platformCode, [FromBody] AppPlatformLimitsRequest request,
        CancellationToken cancellationToken)
        => RunAsync(async () =>
        {
            var interval = request?.MinIntervalSeconds ?? throw new CollectorStoreException("INVALID_APP_LIMITS", "minIntervalSeconds 必填。", 400);
            return new AppPlatformChange(await admin.SetPlatformLimitsAsync(platformCode, interval, request.DailyCap, OperatorId(), cancellationToken),
                await PlatformAsync(platformCode, cancellationToken));
        }, cancellationToken);

    /// <summary>
    /// 恢复被判定为不可用（APP_EMULATOR_REJECTED）的 App 入口。恢复绑定不会自动恢复入口，入口恢复也不会恢复任何绑定。
    /// 入口本来可用时不做修改；因其他原因停用的入口（如文心一言的 APP_CONTENT_UNREADABLE）不能在这里恢复。
    /// </summary>
    /// <param name="platformCode">平台代码。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <response code="200">是否发生了变化，以及操作之后的平台。</response>
    /// <response code="400">不是 App 平台（INVALID_PLATFORM）。</response>
    /// <response code="401">未登录（AUTHENTICATION_REQUIRED）。</response>
    /// <response code="403">不是系统管理员（ADMIN_REQUIRED）。</response>
    /// <response code="409">入口不是因为 App 拒绝模拟器而停用（APP_ENTRY_NOT_RESTORABLE）。</response>
    [HttpPost("platforms/{platformCode}/entry/restore")]
    [ProducesResponseType(typeof(AppPlatformChange), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status409Conflict)]
    public Task<ActionResult<AppPlatformChange>> RestorePlatformEntry(string platformCode, CancellationToken cancellationToken)
        => RunAsync(async () => new AppPlatformChange(await admin.RestorePlatformEntryAsync(platformCode, OperatorId(), cancellationToken),
            await PlatformAsync(platformCode, cancellationToken)), cancellationToken);

    private async Task<AppPlatformStateView> PlatformAsync(string platformCode, CancellationToken cancellationToken)
    {
        var code = AppChannelDefinition.TryGetPlatform(platformCode)?.Code
            ?? throw new CollectorStoreException("INVALID_PLATFORM", "platformCode 不是 App 渠道的平台。", 400);
        return (await admin.ListPlatformsAsync(cancellationToken)).Single(platform => platform.PlatformCode == code);
    }

    // ---------------- 容量与队列 ----------------

    /// <summary>每个 App 平台的周容量（限速上限、实测产能、周容量、周需求、预计排不到）与当前队列（待领取数、进行中数）。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <response code="200">每个平台一行，按平台顺序。</response>
    /// <response code="401">未登录（AUTHENTICATION_REQUIRED）。</response>
    /// <response code="403">不是系统管理员（ADMIN_REQUIRED）。</response>
    [HttpGet("capacity")]
    [ProducesResponseType(typeof(IReadOnlyList<AppPlatformCapacityQueueView>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status403Forbidden)]
    public Task<ActionResult<IReadOnlyList<AppPlatformCapacityQueueView>>> Capacity(CancellationToken cancellationToken)
        => RunAsync<IReadOnlyList<AppPlatformCapacityQueueView>>(async () =>
        {
            var capacities = await capacity.GetPlatformCapacitiesAsync(cancellationToken);
            var queue = (await admin.GetQueueAsync(cancellationToken)).ToDictionary(item => item.PlatformCode, StringComparer.Ordinal);
            return capacities.Select(item => new AppPlatformCapacityQueueView(item,
                queue.GetValueOrDefault(item.Platform) ?? new AppPlatformQueueView(item.Platform, 0, 0))).ToArray();
        }, cancellationToken);

    // ---------------- 任务的尝试历史 ----------------

    /// <summary>
    /// 按任务标识追溯一条 App 采集任务：任务当前的状态与最终结果，以及每一次尝试（按租约版本升序）所在的节点、设备、绑定与账号代号、
    /// 是否已发题与发题时间、失败原因，以及哪一次的结果被接受。只读；不含提问原文或回答内容。
    /// </summary>
    /// <param name="taskId">采集任务标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <response code="200">任务与它的全部尝试。</response>
    /// <response code="401">未登录（AUTHENTICATION_REQUIRED）。</response>
    /// <response code="403">不是系统管理员（ADMIN_REQUIRED）。</response>
    /// <response code="404">任务不存在，或不是 App 渠道的任务（APP_TASK_NOT_FOUND）。</response>
    /// <response code="503">迁移 049 或 051 未部署（APP_CHANNEL_SCHEMA_NOT_MIGRATED）。</response>
    [HttpGet("tasks/{taskId:guid}/attempts")]
    [ProducesResponseType(typeof(AppTaskAttemptsView), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public Task<ActionResult<AppTaskAttemptsView>> TaskAttempts(Guid taskId, CancellationToken cancellationToken)
        => RunAsync(() => admin.GetTaskAttemptsAsync(taskId, cancellationToken), cancellationToken);

    // ---------------- 提问的人工排除 ----------------

    /// <summary>
    /// 运营代表租户把一条提问排除出 App 渠道：从下一个尚未展开的周期起不再生成 App 任务，API 渠道不受影响。
    /// 排除记录本身只追加、不删除；另外追加一条审计事件（只含标识，不含提问原文或原因）。
    /// </summary>
    /// <param name="request">租户、品牌、提问与排除原因。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <response code="201">排除记录。</response>
    /// <response code="400">原因不是 1–500 个字符（INVALID_EXCLUSION_REASON）。</response>
    /// <response code="401">未登录（AUTHENTICATION_REQUIRED）。</response>
    /// <response code="403">不是系统管理员（ADMIN_REQUIRED）。</response>
    /// <response code="404">提问不存在或不属于该租户的该品牌（RESOURCE_NOT_FOUND）。</response>
    /// <response code="409">该提问已有生效中的排除（APP_PROMPT_ALREADY_EXCLUDED）。</response>
    [HttpPost("prompt-exclusions")]
    [ProducesResponseType(typeof(AppPromptExclusionView), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AppPromptExclusionView>> ExcludePrompt([FromBody] AppPromptExclusionRequest request,
        CancellationToken cancellationToken)
    {
        if (await SystemAdministratorAccess.RequireAsync(this, adminStore, cancellationToken) is { } failure) return failure;
        if (request is null) return Error(new CollectorStoreException("INVALID_REQUEST", "请求体不能为空。", 400));
        try
        {
            var view = await collection.ExcludePromptAsync(request.TenantId, request.SiteId, request.PromptId, request.Reason ?? "",
                OperatorId() ?? Guid.Empty, cancellationToken);
            await AuditExclusionAsync(request.TenantId, view, revoked: false, cancellationToken);
            return StatusCode(StatusCodes.Status201Created, view);
        }
        catch (CollectorStoreException exception) { return Error(exception); }
    }

    /// <summary>撤销一条提问生效中的 App 渠道排除；记录保留，只写入撤销人和撤销时间，另外追加一条审计事件。</summary>
    /// <param name="request">租户、品牌与提问。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <response code="200">撤销后的排除记录。</response>
    /// <response code="401">未登录（AUTHENTICATION_REQUIRED）。</response>
    /// <response code="403">不是系统管理员（ADMIN_REQUIRED）。</response>
    /// <response code="404">该提问没有生效中的排除（APP_PROMPT_EXCLUSION_NOT_FOUND）。</response>
    [HttpPost("prompt-exclusions/revoke")]
    [ProducesResponseType(typeof(AppPromptExclusionView), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AppPromptExclusionView>> RevokePromptExclusion([FromBody] AppPromptExclusionRevokeRequest request,
        CancellationToken cancellationToken)
    {
        if (await SystemAdministratorAccess.RequireAsync(this, adminStore, cancellationToken) is { } failure) return failure;
        if (request is null) return Error(new CollectorStoreException("INVALID_REQUEST", "请求体不能为空。", 400));
        try
        {
            var view = await collection.RevokePromptExclusionAsync(request.TenantId, request.SiteId, request.PromptId, OperatorId() ?? Guid.Empty,
                cancellationToken);
            await AuditExclusionAsync(request.TenantId, view, revoked: true, cancellationToken);
            return Ok(view);
        }
        catch (CollectorStoreException exception) { return Error(exception); }
    }

    // The exclusion is committed by its own store; the event is a second, separate write. If it fails the exclusion stands
    // (prompt_app_exclusions is itself the audit record of who excluded or revoked what), and only a warning is logged.
    private async Task AuditExclusionAsync(Guid tenantId, AppPromptExclusionView view, bool revoked, CancellationToken cancellationToken)
    {
        try { await admin.RecordPromptExclusionEventAsync(tenantId, view, revoked, OperatorId(), cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("App prompt exclusion {ExclusionId} was saved but its audit event failed with {ExceptionType}.",
                view.Id, exception.GetType().Name);
        }
    }

    // ---------------- 审计事件、告警、每日汇报 ----------------

    /// <summary>
    /// 按条件查询 App 审计事件（只追加），按发生时间倒序；用返回的 nextCursor 翻页，翻页期间新写入的事件不会让已读过的事件重复出现。
    /// </summary>
    /// <param name="from">可选，起始时间（含），ISO 8601。</param>
    /// <param name="to">可选，结束时间（不含），ISO 8601。</param>
    /// <param name="nodeId">可选，节点标识。</param>
    /// <param name="bindingId">可选，绑定标识。</param>
    /// <param name="platformCode">可选，平台代码。</param>
    /// <param name="eventKind">可选，事件种类，例如 binding_stopped、platform_paused、binding_limits_changed。</param>
    /// <param name="cursor">可选，上一页返回的游标。</param>
    /// <param name="limit">每页条数（1–200），默认 50。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <response code="200">一页事件与下一页的游标（没有下一页时为空）。</response>
    /// <response code="400">筛选条件或游标不合法（INVALID_FILTER、INVALID_CURSOR）。</response>
    /// <response code="401">未登录（AUTHENTICATION_REQUIRED）。</response>
    /// <response code="403">不是系统管理员（ADMIN_REQUIRED）。</response>
    [HttpGet("events")]
    [ProducesResponseType(typeof(CursorPage<AppEventView>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status403Forbidden)]
    public Task<ActionResult<CursorPage<AppEventView>>> Events([FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
        [FromQuery] string? nodeId, [FromQuery] Guid? bindingId, [FromQuery] string? platformCode, [FromQuery] string? eventKind,
        [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken cancellationToken)
        => RunAsync(() => admin.ListEventsAsync(new AppEventQuery(from, to, nodeId, bindingId, platformCode, eventKind), cursor,
            limit ?? AppAdminRules.DefaultEventPageSize, cancellationToken), cancellationToken);

    /// <summary>
    /// 当前告警信息（与每日汇报同一个查询）：失联的节点及失联时长、停止的绑定及原因和停止时长、被暂停或判定不可用的平台、
    /// 过去 24 小时未采集的任务数（按平台和原因）、过去 24 小时的领取 / 成功 / 失败次数（按平台）、各平台本周的容量占用、
    /// 最近连续在发题前失败的绑定（只提示，不会因此停用绑定或暂停平台）。
    /// 只含节点、设备、平台、账号代号和计数。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <response code="200">告警信息。</response>
    /// <response code="401">未登录（AUTHENTICATION_REQUIRED）。</response>
    /// <response code="403">不是系统管理员（ADMIN_REQUIRED）。</response>
    [HttpGet("alerts")]
    [ProducesResponseType(typeof(AppAlertsView), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status403Forbidden)]
    public Task<ActionResult<AppAlertsView>> Alerts(CancellationToken cancellationToken)
        => RunAsync(() => alerts.GetAlertsAsync(cancellationToken), cancellationToken);

    /// <summary>每日告警汇报的历史（不含内容），按汇报日期倒序。首期汇报只存档不发送，发送状态为 not_configured。</summary>
    /// <param name="cursor">可选，上一页返回的游标。</param>
    /// <param name="limit">每页条数（1–100），默认 30。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <response code="200">一页汇报与下一页的游标（没有下一页时为空）。</response>
    /// <response code="400">游标或条数不合法（INVALID_CURSOR、INVALID_FILTER）。</response>
    /// <response code="401">未登录（AUTHENTICATION_REQUIRED）。</response>
    /// <response code="403">不是系统管理员（ADMIN_REQUIRED）。</response>
    [HttpGet("daily-reports")]
    [ProducesResponseType(typeof(CursorPage<AppDailyReportItem>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status403Forbidden)]
    public Task<ActionResult<CursorPage<AppDailyReportItem>>> DailyReports([FromQuery] string? cursor, [FromQuery] int? limit,
        CancellationToken cancellationToken)
        => RunAsync(() => reports.ListAsync(cursor, limit ?? AppAdminRules.DefaultReportPageSize, cancellationToken), cancellationToken);

    /// <summary>读取某一天的每日告警汇报：生成时的告警信息与中文正文。</summary>
    /// <param name="reportDate">汇报日期（Asia/Shanghai 本地日期），格式 yyyy-MM-dd。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <response code="200">这一天的汇报。</response>
    /// <response code="400">日期格式不对（INVALID_FILTER）。</response>
    /// <response code="401">未登录（AUTHENTICATION_REQUIRED）。</response>
    /// <response code="403">不是系统管理员（ADMIN_REQUIRED）。</response>
    /// <response code="404">这一天没有汇报（APP_DAILY_REPORT_NOT_FOUND）。</response>
    [HttpGet("daily-reports/{reportDate}")]
    [ProducesResponseType(typeof(AppDailyReportView), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status404NotFound)]
    public Task<ActionResult<AppDailyReportView>> DailyReport(string reportDate, CancellationToken cancellationToken)
        => RunAsync(async () =>
        {
            if (!DateOnly.TryParseExact(reportDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                throw new CollectorStoreException("INVALID_FILTER", "reportDate 的格式必须是 yyyy-MM-dd。", 400);
            return await reports.GetAsync(date, cancellationToken)
                ?? throw new CollectorStoreException("APP_DAILY_REPORT_NOT_FOUND", "这一天没有每日汇报。", 404);
        }, cancellationToken);

    // ---------------- 共用 ----------------

    /// <summary>先做系统管理员校验（未通过时不调用任何 Store），再执行读写；业务异常映射为 { error: { code, message } }。</summary>
    private async Task<ActionResult<T>> RunAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken)
    {
        if (await SystemAdministratorAccess.RequireAsync(this, adminStore, cancellationToken) is { } failure) return failure;
        try { return Ok(await work()); }
        catch (CollectorStoreException exception) { return Error(exception); }
    }

    private Guid? OperatorId() => SessionAuthenticationMiddleware.GetContext(HttpContext)?.User.UserId;

    private ObjectResult Error(CollectorStoreException exception) => StatusCode(exception.StatusCode, StoreErrorResponse.Body(exception));
}

/// <summary>登记 App 采集节点的请求。</summary>
/// <param name="NodeId">节点标识，1–128 个字符，例如 mac-mini-01；登记后不能修改。</param>
public sealed record RegisterAppNodeRequest(string? NodeId);

/// <summary>设置节点设备上限的请求。</summary>
/// <param name="MaxDevices">必填，该节点可登记的不同设备（AVD）数上限，1–64；不能低于该节点已登记的设备数。</param>
public sealed record AppNodeMaxDevicesRequest(int? MaxDevices);

/// <summary>设置绑定限速的请求；两个值整体替换。</summary>
/// <param name="MinIntervalSeconds">必填，两次领取的最小间隔秒数（60–86400），默认值是 100（迁移 050）。</param>
/// <param name="DailyQuota">最近 24 小时的领取上限（1–10000）；为 null 或省略表示不设上限。</param>
public sealed record AppBindingLimitsRequest(int? MinIntervalSeconds, int? DailyQuota = null);

/// <summary>设置平台限速的请求；两个值整体替换。</summary>
/// <param name="MinIntervalSeconds">必填，全部节点、全部账号合计的最小领取间隔秒数（10–86400），默认值是 30。</param>
/// <param name="DailyCap">Asia/Shanghai 自然日的领取上限（1–100000）；为 null 或省略表示不设上限。</param>
public sealed record AppPlatformLimitsRequest(int? MinIntervalSeconds, int? DailyCap = null);

/// <summary>运营代表租户把一条提问排除出 App 渠道的请求。</summary>
/// <param name="TenantId">租户标识。</param>
/// <param name="SiteId">品牌工作区标识。</param>
/// <param name="PromptId">提问标识。</param>
/// <param name="Reason">排除原因，1–500 个字符；只存在排除记录里，不进入审计事件。</param>
public sealed record AppPromptExclusionRequest(Guid TenantId, Guid SiteId, Guid PromptId, string? Reason);

/// <summary>撤销一条提问的 App 渠道排除的请求。</summary>
/// <param name="TenantId">租户标识。</param>
/// <param name="SiteId">品牌工作区标识。</param>
/// <param name="PromptId">提问标识。</param>
public sealed record AppPromptExclusionRevokeRequest(Guid TenantId, Guid SiteId, Guid PromptId);
