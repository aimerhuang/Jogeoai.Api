using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Jogeoai.Api.Controllers;

/// <summary>新增监测问题时的候选问题生成；不落库、不扣积分。</summary>
[ApiController]
[Route("api/v1/sites/{siteId:guid}/prompt-suggestions")]
public sealed class PromptSuggestionsController(IPromptKeywordExpansionService expansion, IQuickSuggestionService quick,
    TenantContextResolver tenantContext) : ControllerBase
{
    /// <summary>围绕核心词生成 5 个行业词问题和 5 个结合当前品牌的口碑问题，排除品牌已有和请求中列出的问题；需要写权限。</summary>
    /// <param name="siteId">品牌站点标识。</param>
    /// <param name="request">核心词（1–20 字）、需要排除的问题和可选语言。</param>
    /// <param name="cancellationToken">请求取消令牌；取消后停止模型调用。</param>
    /// <returns>按行业词 / 品牌词分开的候选问题；未凑满时 partial 为 true。</returns>
    [HttpPost("keyword")]
    [EnableRateLimiting(PromptSuggestionsRateLimit.Policy)]
    [RequestSizeLimit(256 * 1024)]
    [ProducesResponseType(typeof(KeywordExpansionView), StatusCodes.Status200OK)]
    public async Task<ActionResult<KeywordExpansionView>> ExpandKeyword(Guid siteId, [FromBody] KeywordExpansionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            if (SessionAuthenticationMiddleware.GetContext(HttpContext) is null)
                throw new CollectorStoreException("AUTHENTICATION_REQUIRED", "请先登录。", 401);
            var tenantId = tenantContext.Resolve(HttpContext);
            BrandWriteAccess.Require(HttpContext, tenantId, requireSession: true);
            return Ok(await expansion.ExpandAsync(tenantId, siteId, request, cancellationToken));
        }
        catch (CollectorStoreException error) { return StatusCode(error.StatusCode, StoreErrorResponse.Body(error)); }
    }

    /// <summary>
    /// 快速推荐：只依据品牌名称、别名、介绍和主域名，一次模型调用同步生成约 8 个行业词问题和 2 个品牌词问题（30 秒内返回），
    /// 不读取官网、不经过后台任务；排除品牌已有和请求中列出的问题；需要写权限。
    /// </summary>
    /// <param name="siteId">品牌站点标识。</param>
    /// <param name="request">需要排除的问题、可选语言和期望条数（1–10）。</param>
    /// <param name="cancellationToken">请求取消令牌；取消后停止模型调用。</param>
    /// <returns>按行业词 / 品牌词分开的候选问题；未凑满时 partial 为 true。超时返回 504 PROMPT_SUGGESTION_TIMEOUT。</returns>
    [HttpPost("quick")]
    [EnableRateLimiting(PromptSuggestionsRateLimit.Policy)]
    [RequestSizeLimit(256 * 1024)]
    [ProducesResponseType(typeof(QuickPromptSuggestionView), StatusCodes.Status200OK)]
    public async Task<ActionResult<QuickPromptSuggestionView>> Quick(Guid siteId, [FromBody] QuickPromptSuggestionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var tenantId = RequireWriter();
            return Ok(await quick.SuggestPromptsAsync(tenantId, siteId, request, cancellationToken));
        }
        catch (CollectorStoreException error) { return StatusCode(error.StatusCode, StoreErrorResponse.Body(error)); }
    }

    private Guid RequireWriter()
    {
        if (SessionAuthenticationMiddleware.GetContext(HttpContext) is null)
            throw new CollectorStoreException("AUTHENTICATION_REQUIRED", "请先登录。", 401);
        var tenantId = tenantContext.Resolve(HttpContext);
        BrandWriteAccess.Require(HttpContext, tenantId, requireSession: true);
        return tenantId;
    }
}
