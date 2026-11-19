using Jogeoai.Api;
using Jogeoai.Api.Authorization;
using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Jogeoai.Api.Controllers;

/// <summary>提供注册后的团队、品牌和引导完成接口。</summary>
[ApiController]
[Route("api/v1/onboarding")]
public sealed class OnboardingController(IManagementStore management, IOnboardingProgressStore progress,
    TenantContextResolver tenantContext) : ControllerBase
{
    /// <summary>读取当前登录用户的活动租户、首个品牌和引导步骤。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpGet]
    [ProducesResponseType(typeof(OnboardingSummaryView), StatusCodes.Status200OK)]
    public async Task<ActionResult<OnboardingSummaryView>> Get(CancellationToken cancellationToken = default)
    {
        if (!TryTenant(out var tenantId, out var failure)) return failure!;
        try
        {
            var setup = await progress.GetWorkspaceAsync(tenantId, cancellationToken);
            var brand = setup.SiteId is { } siteId
                ? await management.GetBrandAsync(tenantId, siteId, cancellationToken)
                : setup.Required ? null : (await management.ListBrandsAsync(tenantId, null, 1, cancellationToken)).Items.FirstOrDefault();
            var onboarding = brand is null ? null : await progress.GetAsync(tenantId, brand.SiteId, cancellationToken);
            var membership = SessionAuthenticationMiddleware.GetContext(HttpContext)?.User.Memberships
                .FirstOrDefault(item => item.TenantId == tenantId);
            return Ok(new OnboardingSummaryView(tenantId, membership, brand, onboarding, setup.TeamConfigured, setup.Required || brand is null));
        }
        catch (CollectorStoreException exception) { return StatusCode(exception.StatusCode, StoreErrorResponse.Body(exception)); }
    }

    /// <summary>分页列出当前租户的品牌，供引导恢复和品牌切换使用。</summary>
    /// <param name="cursor">上一页返回的游标。</param>
    /// <param name="limit">每页数量，范围 1–100。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpGet("brands")]
    [ProducesResponseType(typeof(CursorPage<BrandView>), StatusCodes.Status200OK)]
    public async Task<ActionResult<CursorPage<BrandView>>> Brands([FromQuery] string? cursor, [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (!TryTenant(out var tenantId, out var failure)) return failure!;
        try { return Ok(await management.ListBrandsAsync(tenantId, cursor, limit, cancellationToken)); }
        catch (CollectorStoreException exception) { return StatusCode(exception.StatusCode, StoreErrorResponse.Body(exception)); }
    }

    /// <summary>为当前团队创建品牌；已完成团队引导时不修改原品牌和完成状态。</summary>
    /// <param name="request">必填名称与介绍、可选官网及创建请求标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpPost("brands")]
    [ProducesResponseType(typeof(BrandView), StatusCodes.Status201Created)]
    public async Task<ActionResult<BrandView>> CreateBrand([FromBody] BrandWrite request, CancellationToken cancellationToken)
    {
        if (!TryTenant(out var tenantId, out var failure)) return failure!;
        try
        {
            BrandWriteAccess.Require(HttpContext, tenantId);
            var brand = await management.CreateBrandAsync(tenantId, request, cancellationToken);
            return StatusCode(StatusCodes.Status201Created, brand);
        }
        catch (CollectorStoreException exception) { return StatusCode(exception.StatusCode, StoreErrorResponse.Body(exception)); }
    }

    /// <summary>读取指定品牌的引导步骤和问题选择上限。</summary>
    /// <param name="siteId">品牌站点标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpGet("{siteId:guid}")]
    [ProducesResponseType(typeof(OnboardingProgressView), StatusCodes.Status200OK)]
    public async Task<ActionResult<OnboardingProgressView>> Progress(Guid siteId, CancellationToken cancellationToken)
    {
        if (!TryTenant(out var tenantId, out var failure)) return failure!;
        var result = await progress.GetAsync(tenantId, siteId, cancellationToken);
        return result is null ? NotFound(new { error = new { code = "RESOURCE_NOT_FOUND", message = "品牌引导不存在。" } }) : Ok(result);
    }

    /// <summary>保存用户审核后的竞品和监控问题并完成引导。</summary>
    /// <param name="siteId">品牌站点标识。</param>
    /// <param name="request">已选择的竞品和 Prompt。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpPost("brands/{siteId:guid}/complete")]
    [HttpPost("/api/v1/sites/{siteId:guid}/onboarding/complete")]
    [ProducesResponseType(typeof(OnboardingCompletionView), StatusCodes.Status200OK)]
    public async Task<ActionResult<OnboardingCompletionView>> Complete(Guid siteId,
        [FromBody] OnboardingCompletionRequest request, CancellationToken cancellationToken)
    {
        if (!TryTenant(out var tenantId, out var failure)) return failure!;
        try
        {
            TenantWriteAccess.Require(HttpContext, tenantId);
            return Ok(await management.CompleteOnboardingAsync(tenantId, siteId, request, cancellationToken));
        }
        catch (CollectorStoreException exception) { return StatusCode(exception.StatusCode, StoreErrorResponse.Body(exception)); }
    }

    /// <summary>确认初始团队名称，要求当前用户为团队所有者。</summary>
    /// <param name="request">团队名称。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpPut("team")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Team([FromBody] OnboardingTeamWrite request, CancellationToken cancellationToken)
    {
        var identity = SessionAuthenticationMiddleware.GetContext(HttpContext);
        if (identity is null) return Unauthorized();
        if (!TryTenant(out var tenantId, out var failure)) return failure!;
        try
        {
            await progress.ConfigureTeamAsync(tenantId, identity.User.UserId, request.Name, cancellationToken);
            return NoContent();
        }
        catch (CollectorStoreException exception) { return StatusCode(exception.StatusCode, StoreErrorResponse.Body(exception)); }
    }

    /// <summary>保存当前品牌的引导步骤和选择。</summary>
    /// <param name="siteId">品牌标识。</param>
    /// <param name="request">选择草稿。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpPut("{siteId:guid}/draft")]
    [ProducesResponseType(typeof(OnboardingProgressView), StatusCodes.Status200OK)]
    public async Task<ActionResult<OnboardingProgressView>> Draft(Guid siteId, [FromBody] OnboardingDraftWrite request,
        CancellationToken cancellationToken)
    {
        if (!TryTenant(out var tenantId, out var failure)) return failure!;
        try
        {
            TenantWriteAccess.Require(HttpContext, tenantId);
            return Ok(await progress.SaveDraftAsync(tenantId, siteId, request, cancellationToken));
        }
        catch (CollectorStoreException exception) { return StatusCode(exception.StatusCode, StoreErrorResponse.Body(exception)); }
    }

    private bool TryTenant(out Guid tenantId, out ActionResult? failure)
    {
        tenantId = Guid.Empty;
        try
        {
            tenantId = tenantContext.Resolve(HttpContext);
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
}
