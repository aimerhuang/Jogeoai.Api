using System.Globalization;
using Jogeoai.Api;
using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Jogeoai.Api.Controllers;

/// <summary>团队管理：跨品牌汇总与团队默认监控配置。</summary>
/// <param name="summary">团队汇总查询。</param>
/// <param name="tenantContext">租户解析。</param>
/// <param name="management">管理数据存储；团队默认监控配置的读写与下发。</param>
/// <param name="monitoring">每日监控服务；团队默认监控配置的影响预估。</param>
[ApiController]
[Route("api/v1/team")]
public sealed class TeamController(ITeamSummaryQuery summary, TenantContextResolver tenantContext,
    IManagementStore? management = null, IPromptMonitoringService? monitoring = null) : ContentControllerBase(tenantContext)
{
    /// <summary>团队级跨品牌汇总：每个启用品牌的提及率、曝光分数、平均排名、监控提问数及环比，提问、分组、竞品、文章计数与启用的监控目标；所有角色可读。</summary>
    /// <param name="from">起点（含）：带时区的 ISO 时间；默认最近 7 个当地自然日的起点。</param>
    /// <param name="to">终点（不含）：带时区的 ISO 时间；默认当前时间，跨度最长 366 个当地自然日。</param>
    /// <param name="timeZone">IANA 时区；默认最早创建的启用品牌时区，没有品牌时为 Asia/Shanghai。</param>
    /// <param name="provider">可选 Provider（cloro、doubao_api、deepseek_api、packy_qwen、packy_glm、packy_kimi、packy_yuanbao、domestic_collector）；省略时统计全部。</param>
    /// <param name="engine">可选引擎；省略时统计全部。</param>
    /// <param name="siteId">可选品牌工作区标识；省略时返回全部启用品牌。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(TeamSummaryView), StatusCodes.Status200OK)]
    public Task<IActionResult> Summary([FromQuery] string? from, [FromQuery] string? to, [FromQuery] string? timeZone,
        [FromQuery] string? provider, [FromQuery] string? engine, [FromQuery] Guid? siteId, CancellationToken cancellationToken = default)
        => Execute(async () =>
        {
            var user = RequireUser();
            var request = new TeamSummaryRequest(ParseTime(from, "from"), ParseTime(to, "to"),
                string.IsNullOrWhiteSpace(timeZone) ? null : timeZone.Trim(), provider, engine, siteId);
            return Ok(await summary.SummaryAsync(user.TenantId, user.UserId, request, cancellationToken));
        });

    /// <summary>
    /// 读取团队默认监控配置（模型 × 回答模式 × 采集频率）及团队内各启用品牌的跟随状态与已启用的监控单元格；所有角色可读，
    /// canEdit 只对团队所有者或管理员为 true。从未保存过时 configVersion 为 0、cells 为系统基线（ChatGPT 快速回答 1次/天）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <response code="200">团队默认配置与品牌列表。</response>
    /// <response code="401">未登录（AUTHENTICATION_REQUIRED）。</response>
    /// <response code="403">当前用户不是该团队成员（TENANT_FORBIDDEN）。</response>
    /// <response code="503">迁移 048 尚未部署（TEAM_MONITORING_SCHEMA_NOT_MIGRATED）。</response>
    [HttpGet("monitoring-config")]
    [ProducesResponseType(typeof(TeamMonitoringConfigView), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> MonitoringConfig(CancellationToken cancellationToken = default)
        => Execute(async () =>
        {
            var user = RequireUser();
            var view = await Management.GetTeamMonitoringConfigAsync(user.TenantId, cancellationToken);
            return Ok(view with { CanEdit = IsTeamAdmin(user.Role) });
        });

    /// <summary>
    /// 保存团队默认监控配置，并在同一事务内同步到团队内所有启用且跟随的品牌：跟随品牌的可监控目标替换为新配置（国家 / 语言沿用品牌已有目标，
    /// 被移除的单元格停用，国内网页采集目标不变），各品牌目标配置版本加 1；单独配置的品牌不受影响。从下一个尚未冻结的时隙开始生效，不补采。
    /// 仅团队所有者或管理员可调用。
    /// </summary>
    /// <param name="request">期望版本与新的完整单元格集合。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <response code="200">保存并下发后的最新团队默认配置与品牌列表。</response>
    /// <response code="400">单元格为空（MONITORING_TARGETS_REQUIRED）、超过 20 个（MONITORING_TOO_LARGE）、重复（DUPLICATE_TARGET）、
    /// 不可参与每日监控或不在目录中（TARGET_NOT_ALLOWED）、回答模式不受支持（ANSWER_MODE_UNSUPPORTED）或频率无效（FREQUENCY_INVALID）。</response>
    /// <response code="401">未登录（AUTHENTICATION_REQUIRED）。</response>
    /// <response code="403">当前角色不是所有者或管理员（CONTENT_FORBIDDEN），或不是该团队成员（TENANT_FORBIDDEN）。</response>
    /// <response code="409">团队默认配置版本已变化（CONFIG_VERSION_CONFLICT）。</response>
    /// <response code="503">迁移 048 尚未部署（TEAM_MONITORING_SCHEMA_NOT_MIGRATED）。</response>
    [HttpPut("monitoring-config")]
    [ProducesResponseType(typeof(TeamMonitoringConfigView), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> PutMonitoringConfig([FromBody] TeamMonitoringConfigWrite request, CancellationToken cancellationToken = default)
        => Execute(async () =>
        {
            var user = RequireTeamAdmin();
            var view = await Management.PutTeamMonitoringConfigAsync(user.TenantId, user.UserId, request, cancellationToken);
            return Ok(view with { CanEdit = true });
        });

    /// <summary>
    /// 预估团队默认监控配置草稿对所有启用且跟随品牌的影响：各品牌与合计的每周检测 / 积分、下一轮积分、阻断项，以及团队余额与缺口；
    /// 只计算，不保存。校验与保存相同，但单元格超过 20 个不返回 400，而在各品牌的 blockers 中报告；余额不足只体现在 shortfall。
    /// 仅团队所有者或管理员可调用（响应包含团队余额）。
    /// </summary>
    /// <param name="request">草稿单元格。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <response code="200">影响预估。</response>
    /// <response code="400">单元格为空、重复、不可参与每日监控、回答模式不受支持或频率无效（错误码同保存）。</response>
    /// <response code="401">未登录（AUTHENTICATION_REQUIRED）。</response>
    /// <response code="403">当前角色不是所有者或管理员（CONTENT_FORBIDDEN），或不是该团队成员（TENANT_FORBIDDEN）。</response>
    /// <response code="503">迁移 048 尚未部署（TEAM_MONITORING_SCHEMA_NOT_MIGRATED）。</response>
    [HttpPost("monitoring-config/estimate")]
    [ProducesResponseType(typeof(TeamMonitoringEstimateView), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(MonitoringAnswerErrorResponse), StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> EstimateMonitoringConfig([FromBody] TeamMonitoringEstimateRequest request,
        CancellationToken cancellationToken = default)
        => Execute(async () =>
        {
            var user = RequireTeamAdmin();
            return Ok(await Monitoring.EstimateTeamMonitoringAsync(user.TenantId, request, cancellationToken));
        });

    private IManagementStore Management => management
        ?? throw new CollectorStoreException("TEAM_MONITORING_UNAVAILABLE", "团队监控配置服务未启用。", 503);

    private IPromptMonitoringService Monitoring => monitoring
        ?? throw new CollectorStoreException("TEAM_MONITORING_UNAVAILABLE", "团队监控配置服务未启用。", 503);

    private static bool IsTeamAdmin(string role)
        => role.Equals("owner", StringComparison.OrdinalIgnoreCase) || role.Equals("admin", StringComparison.OrdinalIgnoreCase);

    // Real session required (RequireUser), owner / admin of the current tenant; checked before any store call (D5).
    private (Guid TenantId, Guid UserId, string Role) RequireTeamAdmin()
    {
        var user = RequireUser();
        if (!IsTeamAdmin(user.Role))
            throw new CollectorStoreException("CONTENT_FORBIDDEN", "只有团队所有者或管理员可以修改团队默认监控配置。", 403);
        return user;
    }

    /// <summary>与分析接口相同的时间规则：必须带 Z 或 ±hh:mm 时区。</summary>
    private static DateTimeOffset? ParseTime(string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (!(value.EndsWith("Z", StringComparison.OrdinalIgnoreCase)
              || System.Text.RegularExpressions.Regex.IsMatch(value, @"[+-]\d{2}:\d{2}$"))
            || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var result))
            throw new CollectorStoreException("INVALID_TIME_RANGE", $"{key} 必须是带时区的 ISO 时间。", 400);
        return result;
    }
}
