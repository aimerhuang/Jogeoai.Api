using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Jogeoai.Api.Controllers;

/// <summary>竞品推荐；不落库、不扣积分。</summary>
[ApiController]
[Route("api/v1/sites/{siteId:guid}/competitor-suggestions")]
public sealed class CompetitorSuggestionsController(IQuickSuggestionService quick, TenantContextResolver tenantContext) : ControllerBase
{
    /// <summary>
    /// 依据品牌名称、别名、介绍和主域名，一次模型调用同步给出最多 10 个直接竞品（30 秒内返回），
    /// 剔除品牌自身、品牌已有竞品和请求中列出的名称；官网域名由模型给出、未经联网核验；需要写权限。
    /// </summary>
    /// <param name="siteId">品牌站点标识。</param>
    /// <param name="request">需要排除的竞品名称、可选语言和目标市场。</param>
    /// <param name="cancellationToken">请求取消令牌；取消后停止模型调用。</param>
    /// <returns>推荐竞品，可以为空。超时返回 504 COMPETITOR_SUGGESTION_TIMEOUT。</returns>
    [HttpPost]
    [EnableRateLimiting(PromptSuggestionsRateLimit.Policy)]
    [RequestSizeLimit(64 * 1024)]
    [ProducesResponseType(typeof(CompetitorSuggestionView), StatusCodes.Status200OK)]
    public async Task<ActionResult<CompetitorSuggestionView>> Suggest(Guid siteId, [FromBody] CompetitorSuggestionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            if (SessionAuthenticationMiddleware.GetContext(HttpContext) is null)
                throw new CollectorStoreException("AUTHENTICATION_REQUIRED", "请先登录。", 401);
            var tenantId = tenantContext.Resolve(HttpContext);
            BrandWriteAccess.Require(HttpContext, tenantId, requireSession: true);
            return Ok(await quick.SuggestCompetitorsAsync(tenantId, siteId, request, cancellationToken));
        }
        catch (CollectorStoreException error) { return StatusCode(error.StatusCode, StoreErrorResponse.Body(error)); }
    }
}
