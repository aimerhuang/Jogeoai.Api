using Jogeoai.Api;
using Jogeoai.Api.Authorization;
using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Jogeoai.Api.Controllers;

/// <summary>管理品牌、竞品、Prompt、标签、目标及采集预览。</summary>
/// <param name="store">管理数据存储。</param>
/// <param name="tenantContext">租户解析。</param>
/// <param name="monitoring">可选的每日监控服务；提供时，新增提问或提问新增模型后立即采集一轮。</param>
/// <param name="logger">可选日志；立即采集出现未预期错误时记录警告。</param>
[ApiController]
[Route("api/v1")]
public sealed class ManagementController(IManagementStore store, TenantContextResolver tenantContext,
    IPromptMonitoringService? monitoring = null, ILogger<ManagementController>? logger = null) : ControllerBase
{
    private static readonly EventId InstantCollectionFailedEvent = new(4301, "InstantCollectionFailed");

    private Guid Tenant() => tenantContext.Resolve(HttpContext);

    // The write has already been committed: any failure of the instant collection is only reported in the response body,
    // never as an error status. Cancellation of this request still propagates.
    private async Task<InstantCollectionView?> CollectInstantly(Guid siteId,
        Func<IPromptMonitoringService, Task<InstantCollectionView?>> collect, CancellationToken cancellationToken)
    {
        if (monitoring is null) return null;
        try { return await collect(monitoring); }
        catch (Exception exception) when (!(exception is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            logger?.LogWarning(InstantCollectionFailedEvent, exception, "InstantCollectionFailed site={SiteId} error={ErrorType}",
                siteId, exception.GetType().Name);
            return new InstantCollectionView(InstantCollectionStatus.Skipped, null, 0, 0m, "INSTANT_COLLECTION_FAILED",
                "提问已保存，但未能立即采集，将在下一个时隙按正常规则采集。");
        }
    }

    // After a committed prompt write: prompt × App platform pairs the current App cycle does not cover yet are queued at once
    // (11-27-app-first-cycle-now). Best effort like the instant collection; the response never depends on it.
    private async Task SyncAppPlan(Guid tenantId, Guid siteId, CancellationToken cancellationToken)
    {
        if (monitoring is null) return;
        try { await monitoring.SyncAppPlanAsync(tenantId, siteId, cancellationToken); }
        catch (Exception exception) when (!(exception is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            logger?.LogWarning(InstantCollectionFailedEvent, exception, "AppPlanSyncFailed site={SiteId} error={ErrorType}",
                siteId, exception.GetType().Name);
        }
    }

    // 写入前的租户解析与可写角色校验；规则见 TenantWriteAccess，必须在任何 Store 调用之前执行。
    private Guid TenantForWrite(string message = "当前角色无权修改提问。")
    {
        var tenant = Tenant();
        TenantWriteAccess.Require(HttpContext, tenant, message: message);
        return tenant;
    }

    private async Task<IActionResult> Execute(Func<Task<IActionResult>> action)
    {
        try { return await action(); }
        catch (CollectorStoreException exception)
        { return StatusCode(exception.StatusCode, StoreErrorResponse.Body(exception)); }
    }

    /// <summary>创建品牌或绑定已有 Site。</summary>
    [HttpPost("sites")]
    [ProducesResponseType(typeof(BrandView), StatusCodes.Status201Created)]
    public Task<IActionResult> CreateSite([FromBody] BrandWrite request, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var tenantId = Tenant();
            BrandWriteAccess.Require(HttpContext, tenantId);
            var result = await store.CreateBrandAsync(tenantId, request, cancellationToken);
            return CreatedAtAction(nameof(GetSite), new { siteId = result.SiteId }, result);
        });

    /// <summary>分页列出租户品牌。</summary>
    /// <param name="cursor">上页返回的游标。</param>
    /// <param name="limit">每页数量，范围 1–100。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpGet("sites")]
    [ProducesResponseType(typeof(CursorPage<BrandView>), StatusCodes.Status200OK)]
    public Task<IActionResult> Sites([FromQuery] string? cursor, [FromQuery] int limit = 50, CancellationToken cancellationToken = default)
        => Execute(async () => Ok(await store.ListBrandsAsync(Tenant(), cursor, limit, cancellationToken)));

    /// <summary>查询品牌配置。</summary>
    [HttpGet("sites/{siteId:guid}")]
    [ProducesResponseType(typeof(BrandView), StatusCodes.Status200OK)]
    public Task<IActionResult> GetSite(Guid siteId, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var result = await store.GetBrandAsync(Tenant(), siteId, cancellationToken);
            return result is null ? NotFound(new { error = new { code = "RESOURCE_NOT_FOUND", message = "工作区不存在。" } }) : Ok(result);
        });

    /// <summary>按配置版本修改或归档品牌；省略字段保留当前值。</summary>
    [HttpPatch("sites/{siteId:guid}")]
    [HttpPatch("sites/{siteId:guid}/onboarding-settings")]
    [ProducesResponseType(typeof(BrandView), StatusCodes.Status200OK)]
    public Task<IActionResult> PatchSite(Guid siteId, [FromBody] BrandPatch request, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var tenantId = Tenant();
            BrandWriteAccess.Require(HttpContext, tenantId);
            return Ok(await store.UpdateBrandAsync(tenantId, siteId, request, cancellationToken));
        });

    /// <summary>分页列出品牌工作区的竞品。</summary>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="cursor">上页返回的游标。</param>
    /// <param name="limit">每页数量，范围 1–100。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpGet("sites/{siteId:guid}/competitors")]
    [ProducesResponseType(typeof(CursorPage<CompetitorView>), StatusCodes.Status200OK)]
    public Task<IActionResult> Competitors(Guid siteId, [FromQuery] string? cursor, [FromQuery] int limit = 50, CancellationToken cancellationToken = default)
        => Execute(async () => Ok(await store.ListCompetitorsAsync(Tenant(), siteId, cursor, limit, cancellationToken)));

    /// <summary>创建具有稳定 ID 的竞品。</summary>
    [HttpPost("sites/{siteId:guid}/competitors")]
    [ProducesResponseType(typeof(CompetitorView), StatusCodes.Status201Created)]
    public Task<IActionResult> CreateCompetitor(Guid siteId, [FromBody] CompetitorWrite request, CancellationToken cancellationToken)
        => Execute(async () => StatusCode(201, await store.CreateCompetitorAsync(TenantForWrite(TenantWriteAccess.DefaultMessage), siteId, request, cancellationToken)));

    /// <summary>修改或归档竞品；省略字段保留当前值。</summary>
    [HttpPatch("sites/{siteId:guid}/competitors/{competitorId:guid}")]
    [ProducesResponseType(typeof(CompetitorView), StatusCodes.Status200OK)]
    public Task<IActionResult> PatchCompetitor(Guid siteId, Guid competitorId, [FromBody] CompetitorPatch request, CancellationToken cancellationToken)
        => Execute(async () => Ok(await store.UpdateCompetitorAsync(TenantForWrite(TenantWriteAccess.DefaultMessage), siteId, competitorId, request, cancellationToken)));

    /// <summary>分页列出站点标签。</summary>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="cursor">上页返回的游标。</param>
    /// <param name="limit">每页数量，范围 1–100。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpGet("sites/{siteId:guid}/tags")]
    [ProducesResponseType(typeof(CursorPage<TagView>), StatusCodes.Status200OK)]
    public Task<IActionResult> Tags(Guid siteId, [FromQuery] string? cursor, [FromQuery] int limit = 50, CancellationToken cancellationToken = default)
        => Execute(async () => Ok(await store.ListTagsAsync(Tenant(), siteId, cursor, limit, cancellationToken)));

    /// <summary>创建站点标签。</summary>
    [HttpPost("sites/{siteId:guid}/tags")]
    [ProducesResponseType(typeof(TagView), StatusCodes.Status201Created)]
    public Task<IActionResult> CreateTag(Guid siteId, [FromBody] TagWrite request, CancellationToken cancellationToken)
        => Execute(async () => StatusCode(201, await store.CreateTagAsync(TenantForWrite(TenantWriteAccess.DefaultMessage), siteId, request, cancellationToken)));

    /// <summary>修改或归档站点标签。</summary>
    [HttpPatch("sites/{siteId:guid}/tags/{tagId:guid}")]
    [ProducesResponseType(typeof(TagView), StatusCodes.Status200OK)]
    public Task<IActionResult> PatchTag(Guid siteId, Guid tagId, [FromBody] TagPatch request, CancellationToken cancellationToken)
        => Execute(async () => Ok(await store.UpdateTagAsync(TenantForWrite(TenantWriteAccess.DefaultMessage), siteId, tagId, request, cancellationToken)));

    /// <summary>按搜索词和标签稳定分页查询 Prompt。</summary>
    [HttpGet("sites/{siteId:guid}/prompts")]
    [ProducesResponseType(typeof(CursorPage<PromptView>), StatusCodes.Status200OK)]
    public Task<IActionResult> Prompts(Guid siteId, [FromQuery] string? search, [FromQuery] Guid? tagId,
        [FromQuery] string? cursor, [FromQuery] int limit = 50, [FromQuery] Guid? groupId = null,
        [FromQuery] bool ungrouped = false, CancellationToken cancellationToken = default)
        => Execute(async () => Ok(await store.ListPromptsAsync(Tenant(), siteId, search, tagId, cursor, limit, cancellationToken, groupId, ungrouped,
            includeAppEligibility: true)));

    /// <summary>分页列出现有 Prompt 的分组，并返回筛选后的未分组数量。</summary>
    [HttpGet("sites/{siteId:guid}/prompt-groups")]
    [ProducesResponseType(typeof(PromptGroupPage), StatusCodes.Status200OK)]
    public Task<IActionResult> PromptGroups(Guid siteId, [FromQuery] string? search, [FromQuery] Guid? tagId,
        [FromQuery] string? cursor, [FromQuery] int limit = 50, CancellationToken cancellationToken = default)
        => Execute(async () => Ok(await store.ListPromptGroupsAsync(Tenant(), siteId, search, tagId, cursor, limit, cancellationToken)));

    /// <summary>创建 Prompt 分组。</summary>
    [HttpPost("sites/{siteId:guid}/prompt-groups")]
    [ProducesResponseType(typeof(PromptGroupView), StatusCodes.Status201Created)]
    public Task<IActionResult> CreatePromptGroup(Guid siteId, [FromBody] PromptGroupWrite request, CancellationToken cancellationToken)
        => Execute(async () => StatusCode(201, await store.CreatePromptGroupAsync(TenantForWrite(), siteId, request, cancellationToken)));

    /// <summary>按配置版本修改 Prompt 分组名称。</summary>
    [HttpPatch("sites/{siteId:guid}/prompt-groups/{groupId:guid}")]
    [ProducesResponseType(typeof(PromptGroupView), StatusCodes.Status200OK)]
    public Task<IActionResult> PatchPromptGroup(Guid siteId, Guid groupId, [FromBody] PromptGroupPatch request,
        CancellationToken cancellationToken)
        => Execute(async () => Ok(await store.UpdatePromptGroupAsync(TenantForWrite(), siteId, groupId, request, cancellationToken)));

    /// <summary>读取品牌 Prompt 的全量数量摘要，不受当前列表筛选影响。</summary>
    [HttpGet("sites/{siteId:guid}/prompts/summary")]
    [ProducesResponseType(typeof(PromptSummaryView), StatusCodes.Status200OK)]
    public Task<IActionResult> PromptSummary(Guid siteId, CancellationToken cancellationToken)
        => Execute(async () => Ok(await store.SummarizePromptsAsync(Tenant(), siteId, cancellationToken)));

    /// <summary>
    /// 创建追踪 Prompt。新建为启用状态时，立即对该提问 × 它的有效模型采集一轮（不看采集频率，每次检测 10 积分，当天已采集的组合不重复采集），
    /// 结果在响应的 instantCollection 中返回；余额不足、采集服务不可用等原因未立即采集时同样返回 201，原因见 instantCollection。
    /// 提问保存后，当前 App 周期尚未覆盖的「提问 × App 平台」会立即进入一个即时时隙，由 Worker 建采集任务（仅限已开放 App 渠道的团队）。
    /// </summary>
    [HttpPost("sites/{siteId:guid}/prompts")]
    [ProducesResponseType(typeof(PromptView), StatusCodes.Status201Created)]
    public Task<IActionResult> CreatePrompt(Guid siteId, [FromBody] PromptWrite request, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var tenantId = TenantForWrite();
            var created = await store.CreatePromptAsync(tenantId, siteId, request, cancellationToken);
            if (created.Enabled && created.ArchivedAt is null)
                created = created with
                {
                    InstantCollection = await CollectInstantly(siteId,
                        service => service.CollectNewPromptsAsync(tenantId, siteId, [created.PromptId], cancellationToken), cancellationToken),
                };
            await SyncAppPlan(tenantId, siteId, cancellationToken);
            return StatusCode(201, created);
        });

    /// <summary>查询 Prompt 当前内容、版本、分类及标签。</summary>
    [HttpGet("sites/{siteId:guid}/prompts/{promptId:guid}")]
    [ProducesResponseType(typeof(PromptView), StatusCodes.Status200OK)]
    public Task<IActionResult> GetPrompt(Guid siteId, Guid promptId, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var result = await store.GetPromptAsync(Tenant(), siteId, promptId, cancellationToken, includeAppEligibility: true);
            return result is null ? NotFound(new { error = new { code = "RESOURCE_NOT_FOUND", message = "Prompt 不存在。" } }) : Ok(result);
        });

    /// <summary>
    /// 编辑、启停或归档 Prompt；内容、标签或分类变化产生新版本。请求修改平台范围（targetKeys 或 resetTargetScope=true）且保存后新增了有效模型时，
    /// 立即对该提问 × 新增模型补采一次（只减少模型不采集），结果在响应的 instantCollection 中返回；未立即采集不影响保存结果。
    /// 提问保存后，当前 App 周期尚未覆盖的「提问 × App 平台」会立即进入一个即时时隙，由 Worker 建采集任务（仅限已开放 App 渠道的团队）。
    /// </summary>
    [HttpPatch("sites/{siteId:guid}/prompts/{promptId:guid}")]
    [ProducesResponseType(typeof(PromptView), StatusCodes.Status200OK)]
    public Task<IActionResult> PatchPrompt(Guid siteId, Guid promptId, [FromBody] PromptPatch request, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var tenantId = TenantForWrite();
            var scopeChange = request is not null && (request.TargetKeys is not null || request.ResetTargetScope == true);
            // The update requires expectedVersion == the current config version, so a `before` read at that same version is
            // exactly the state the update replaced; any other version means the update fails (409) or the diff is skipped.
            var before = scopeChange && monitoring is not null
                ? await store.GetPromptAsync(tenantId, siteId, promptId, cancellationToken) : null;
            var after = await store.UpdatePromptAsync(tenantId, siteId, promptId, request!, cancellationToken);
            if (before is not null && request!.ExpectedVersion is { } expected && expected >= 0 && before.ConfigVersion == (uint)expected)
                after = after with
                {
                    InstantCollection = await CollectInstantly(siteId,
                        service => service.CollectAddedTargetsAsync(tenantId, siteId, before, after, cancellationToken), cancellationToken),
                };
            await SyncAppPlan(tenantId, siteId, cancellationToken);
            return Ok(after);
        });

    /// <summary>预览最多 200 行 Prompt 导入的错误、重复项与分组解析（归入已有分组或提交时新建）。</summary>
    [HttpPost("sites/{siteId:guid}/prompts/import-preview")]
    [ProducesResponseType(typeof(PromptImportPreviewView), StatusCodes.Status200OK)]
    public Task<IActionResult> ImportPreview(Guid siteId, [FromBody] IReadOnlyList<PromptImportRow> rows, CancellationToken cancellationToken)
        => Execute(async () => Ok(await store.PreviewImportAsync(Tenant(), siteId, rows, cancellationToken)));

    /// <summary>
    /// 按幂等键（Idempotency-Key 请求头）提交 Prompt 批量导入；行可带分组、品牌词分类与来源。实际新建了提问（createdPromptIds 非空）时，
    /// 立即对新提问 × 各自的有效模型采集一轮，结果在响应的 instantCollection 中返回；同一幂等键重放不会产生第二轮立即采集。
    /// 提问保存后，当前 App 周期尚未覆盖的「提问 × App 平台」会立即进入一个即时时隙，由 Worker 建采集任务（仅限已开放 App 渠道的团队）。
    /// </summary>
    [HttpPost("sites/{siteId:guid}/prompts/import")]
    [ProducesResponseType(typeof(PromptImportResult), StatusCodes.Status200OK)]
    public Task<IActionResult> Import(Guid siteId, [FromBody] IReadOnlyList<PromptImportRow> rows, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var tenantId = TenantForWrite();
            var result = await store.ImportAsync(tenantId, siteId, rows,
                Request.Headers["Idempotency-Key"].FirstOrDefault() ?? "", cancellationToken);
            return Ok(await WithInstantCollection(tenantId, siteId, result, cancellationToken));
        });

    /// <summary>
    /// 批量创建监测问题（新增监测问题弹窗）：按幂等键（Idempotency-Key 请求头）一次创建全部行及需要新建的分组，并统一写入平台范围；
    /// 要么全部成功，要么全部回滚。超出套餐额度返回 409 PLAN_LIMIT_EXCEEDED；创建后每品牌超过 200 个启用问题或每轮超过 1000 次检测返回
    /// 400 MONITORING_TOO_LARGE；平台无效返回 400 PROMPT_TARGET_INVALID；未执行迁移 042 返回 503 PROMPT_TARGET_SCHEMA_NOT_MIGRATED。
    /// 创建成功后立即对实际新建的提问（createdPromptIds）× 各自的有效模型采集一轮（不看采集频率，每次检测 10 积分），结果在响应的 instantCollection 中返回；
    /// 余额不足或采集服务不可用时提问照常创建，原因见 instantCollection。同一幂等键重放不会产生第二轮立即采集。
    /// 提问保存后，当前 App 周期尚未覆盖的「提问 × App 平台」会立即进入一个即时时隙，由 Worker 建采集任务（仅限已开放 App 渠道的团队）。
    /// </summary>
    [HttpPost("sites/{siteId:guid}/prompts/batch")]
    [ProducesResponseType(typeof(PromptImportResult), StatusCodes.Status200OK)]
    public Task<IActionResult> CreateBatch(Guid siteId, [FromBody] PromptBatchCreateRequest request, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var tenantId = TenantForWrite();
            var result = await store.ImportAsync(tenantId, siteId, request?.Rows!,
                Request.Headers["Idempotency-Key"].FirstOrDefault() ?? "", cancellationToken, request?.TargetKeys);
            return Ok(await WithInstantCollection(tenantId, siteId, result, cancellationToken));
        });

    // A replay returns the stored CreatedPromptIds and calls the service again; its same-day dedup answers already_collected,
    // so no second run is created, while a first request interrupted before its run still gets the instant collection.
    private async Task<PromptImportResult> WithInstantCollection(Guid tenantId, Guid siteId, PromptImportResult result,
        CancellationToken cancellationToken)
    {
        if (result.CreatedPromptIds is not { Count: > 0 } created) return result;
        result = result with
        {
            InstantCollection = await CollectInstantly(siteId,
                service => service.CollectNewPromptsAsync(tenantId, siteId, created, cancellationToken), cancellationToken),
        };
        await SyncAppPlan(tenantId, siteId, cancellationToken);
        return result;
    }

    /// <summary>
    /// 查询 Provider 采集目标目录和能力（含每个 Provider 支持的回答模式 supportedAnswerModes 与默认模式 defaultAnswerMode）。
    /// App 渠道的目标行（channel = app）只对已开放 App 渠道的团队返回；未开放的团队看不到这些行，保存 App 目标返回 400 TARGET_NOT_ALLOWED。
    /// </summary>
    [HttpGet("collection-targets")]
    [ProducesResponseType(typeof(IReadOnlyList<CollectionTargetView>), StatusCodes.Status200OK)]
    public Task<IActionResult> TargetCatalog(CancellationToken cancellationToken)
        => Execute(async () => Ok(await store.GetTargetCatalogAsync(cancellationToken, Tenant())));

    /// <summary>查询品牌的采集目标，含每个目标的回答模式、采集频率与频率锚点；同一模型的快速回答与深度思考是两个目标。</summary>
    [HttpGet("sites/{siteId:guid}/targets")]
    [ProducesResponseType(typeof(SiteTargetsView), StatusCodes.Status200OK)]
    public Task<IActionResult> Targets(Guid siteId, CancellationToken cancellationToken)
        => Execute(async () => Ok(await store.GetSiteTargetsAsync(Tenant(), siteId, cancellationToken)));

    /// <summary>
    /// 替换品牌目标集合，并保留旧 Target 的稳定标识。scope=all（默认）替换全部目标；scope=monitoring 只替换可参与每日监控的目标，国内网页采集目标保持原状。
    /// 回答模式不受支持返回 400 ANSWER_MODE_UNSUPPORTED；频率无效返回 400 FREQUENCY_INVALID；目标（含回答模式）重复返回 400 DUPLICATE_TARGET；
    /// 启用的每日监控目标超过 20 个返回 400 MONITORING_TOO_LARGE；版本不一致返回 409 CONFIG_VERSION_CONFLICT；
    /// 使用非默认回答模式或非 daily 频率而迁移 047 未部署时返回 503 ANSWER_MODE_SCHEMA_NOT_MIGRATED。
    /// 保存成功后品牌变为单独配置（followsTeamDefault = false），不再接收团队默认监控配置的变更；可通过 POST targets/follow-default 恢复跟随。
    /// </summary>
    [HttpPut("sites/{siteId:guid}/targets")]
    [ProducesResponseType(typeof(SiteTargetsView), StatusCodes.Status200OK)]
    public Task<IActionResult> PutTargets(Guid siteId, [FromBody] SiteTargetsWrite request, CancellationToken cancellationToken)
        => Execute(async () => Ok(await store.PutSiteTargetsAsync(TenantForWrite(), siteId, request, cancellationToken, markIndividual: true)));

    /// <summary>
    /// 品牌恢复跟随团队默认监控配置：可监控目标立即替换为团队默认配置（国家 / 语言沿用品牌已有目标，不在默认配置中的可监控目标停用，
    /// 国内网页采集目标不变），目标配置版本加 1，followsTeamDefault 变为 true；之后团队默认配置的变更会自动同步到该品牌。
    /// 团队从未保存过默认配置时按系统基线（ChatGPT 快速回答 1次/天）替换。权限同保存品牌目标（所有者、管理员、成员；只读成员 403）。
    /// 版本不一致返回 409 CONFIG_VERSION_CONFLICT；品牌已归档返回 404 RESOURCE_NOT_FOUND；迁移 048 未部署返回 503 TEAM_MONITORING_SCHEMA_NOT_MIGRATED。
    /// </summary>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="request">品牌当前目标配置版本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpPost("sites/{siteId:guid}/targets/follow-default")]
    [ProducesResponseType(typeof(SiteTargetsView), StatusCodes.Status200OK)]
    public Task<IActionResult> FollowTeamDefault(Guid siteId, [FromBody] FollowTeamDefaultRequest request, CancellationToken cancellationToken)
        => Execute(async () => Ok(await store.FollowTeamDefaultAsync(TenantForWrite("当前角色无权修改监控目标。"), siteId, request, cancellationToken)));

    /// <summary>固定 Prompt 版本、Target 与样本数并预览 Check 规模。</summary>
    [HttpPost("sites/{siteId:guid}/collection-previews")]
    [ProducesResponseType(typeof(CollectionPreviewView), StatusCodes.Status200OK)]
    public Task<IActionResult> CreatePreview(Guid siteId, [FromBody] CollectionSelection selection, CancellationToken cancellationToken)
        => Execute(async () => Ok(await store.CreatePreviewAsync(Tenant(), siteId, selection, cancellationToken)));
}
