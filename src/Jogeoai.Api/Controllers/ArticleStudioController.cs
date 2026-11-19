using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Jogeoai.Api.Controllers;

/// <summary>文章创作（智能学习）：第 1 步创作准备、生成提交与第 2 步写作进度轮询。</summary>
[ApiController]
[Route("api/v1/sites/{siteId:guid}/article-studio")]
public sealed class ArticleStudioController(
    IArticleStudioService studio,
    TenantContextResolver tenantContext) : ContentControllerBase(tenantContext)
{
    /// <summary>读取创作准备：可选监测问题、单价与余额；传入 promptId 时返回优化目标、自动引用知识条数、竞品可用性、引擎数与当前稿件。</summary>
    /// <remarks>优化目标按该问题近 7 天全来源表现推导；知识库 0 命中时返回 hitCount=0（将以不使用知识库方式生成），不报 422；只读角色不创建素材预览。</remarks>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="promptId">选中的监测问题标识；省略时只返回问题列表、单价与余额。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    [HttpGet("prepare")]
    [ProducesResponseType(typeof(ArticleStudioPrepareView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Prepare(Guid siteId, [FromQuery] Guid? promptId, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var user = RequireUser();
            var canWrite = !user.Role.Equals("viewer", StringComparison.OrdinalIgnoreCase);
            return Ok(await studio.PrepareAsync(user.TenantId, siteId, user.UserId, canWrite, promptId, cancellationToken));
        });

    /// <summary>提交文章创作生成：自动补齐目标平台、语言、策略、篇幅与风格，预占 100 积分并进入六阶段写作；需要 Idempotency-Key 请求头。</summary>
    /// <remarks>传 articleId 时为重新生成：该文章没有有效发布链接则在同一文章生成新版本，否则新建文章并返回 regeneratedAsNew=true；同一文章已有进行中的任务返回 409。</remarks>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="request">生成请求（自定义写作要求最多 5000 字符）。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    [HttpPost("generations")]
    [ProducesResponseType(typeof(ArticleStudioGenerationAccepted), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status402PaymentRequired)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> Generate(Guid siteId, [FromBody] ArticleStudioGenerationRequest request, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var user = RequireWriter();
            var key = Request.Headers["Idempotency-Key"].FirstOrDefault() ?? string.Empty;
            return Accepted(await studio.SubmitAsync(user.TenantId, siteId, user.UserId, request, key, cancellationToken));
        });

    /// <summary>轮询文章创作任务：六个阶段状态与耗时、引用源（已抓 / 命中及失败原因）、生成版本与错误。</summary>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="taskId">生成任务标识。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    [HttpGet("generations/{taskId:guid}")]
    [ProducesResponseType(typeof(ArticleStudioGenerationView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> Generation(Guid siteId, Guid taskId, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var user = RequireUser();
            var result = await studio.GetGenerationAsync(user.TenantId, siteId, taskId, cancellationToken);
            return result is null ? NotFound() : Ok(result);
        });
}
