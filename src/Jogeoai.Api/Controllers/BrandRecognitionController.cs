using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Jogeoai.Api.Controllers;

/// <summary>创建前的 Luna 品牌资料识别；不保存品牌，不占品牌名额或积分。</summary>
[ApiController]
[Route("api/v1/brands/recognize")]
public sealed class BrandRecognitionController(IBrandRecognitionService recognition, TenantContextResolver tenantContext)
    : ControllerBase
{
    /// <summary>按品牌名称和官网经 Luna 联网搜索，并结合介绍文字与可读网页返回可编辑建议；需要活动团队写权限。</summary>
    /// <param name="request">品牌名称、别名、介绍或资料网址；名称、介绍与网址至少填写一项。</param>
    /// <param name="cancellationToken">请求取消令牌；取消后停止联网搜索、网页读取和模型调用。</param>
    [HttpPost]
    [EnableRateLimiting(BrandRecognitionRateLimit.Policy)]
    [RequestSizeLimit(32 * 1024)]
    [ProducesResponseType(typeof(BrandRecognitionView), StatusCodes.Status200OK)]
    public async Task<ActionResult<BrandRecognitionView>> Recognize([FromBody] BrandRecognitionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            if (SessionAuthenticationMiddleware.GetContext(HttpContext) is null)
                throw new CollectorStoreException("AUTHENTICATION_REQUIRED", "请先登录。", 401);
            var tenantId = tenantContext.Resolve(HttpContext);
            BrandWriteAccess.Require(HttpContext, tenantId, requireSession: true);
            return Ok(await recognition.RecognizeAsync(request, cancellationToken));
        }
        catch (CollectorStoreException error) { return StatusCode(error.StatusCode, StoreErrorResponse.Body(error)); }
    }
}
