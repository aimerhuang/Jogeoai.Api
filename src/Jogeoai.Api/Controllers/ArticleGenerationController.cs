using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Jogeoai.Api.Controllers;

/// <summary>文章生成向导、RAG 素材预览、费用预览和异步任务。</summary>
[ApiController]
[Route("api/v1/sites/{siteId:guid}/article-generation")]
public sealed class ArticleGenerationController(
    IContentStore store,
    IArticleMaterialSelectionService materials,
    TenantContextResolver tenantContext) : ContentControllerBase(tenantContext)
{
    /// <summary>读取目标、问题、标签、策略和积分余额。</summary>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    [HttpGet("options")]
    [ProducesResponseType(typeof(ArticleGenerationOptionsView), StatusCodes.Status200OK)]
    public Task<IActionResult> Options(Guid siteId, CancellationToken cancellationToken)
        => Execute(async () => { var user = RequireUser(); return Ok(await store.GetGenerationOptionsAsync(user.TenantId, siteId, cancellationToken)); });

    /// <summary>读取当前租户品牌可见的知识素材目录。</summary>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    [HttpGet("materials")]
    [ProducesResponseType(typeof(ArticleMaterialCatalogView), StatusCodes.Status200OK)]
    public Task<IActionResult> Materials(Guid siteId, CancellationToken cancellationToken)
        => Execute(async () => { var user = RequireUser(); return Ok(await materials.ListCatalogAsync(user.TenantId, siteId, cancellationToken)); });

    /// <summary>按问题和资料选择创建 15 分钟 RAG 上下文预览。</summary>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="request">素材筛选与问题选择。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    [HttpPost("material-preview")]
    [ProducesResponseType(typeof(ArticleMaterialPreviewView), StatusCodes.Status200OK)]
    public Task<IActionResult> MaterialPreview(Guid siteId, [FromBody] ArticleMaterialPreviewRequest request, CancellationToken cancellationToken)
        => Execute(async () => { var user = RequireWriter(); return Ok(await materials.PreviewAsync(user.TenantId, siteId, user.UserId, request, cancellationToken)); });

    /// <summary>计算文章数量、单价和预占总额；此步骤不扣费。</summary>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="request">写作预览请求。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    [HttpPost("preview")]
    [ProducesResponseType(typeof(ArticleGenerationPreviewView), StatusCodes.Status200OK)]
    public Task<IActionResult> Preview(Guid siteId, [FromBody] ArticleGenerationPreviewRequest request, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var user = RequireWriter();
            var promptIds = request.PromptIds?.Where(id => id != Guid.Empty).Distinct().ToArray() ?? [];
            var targetIds = request.TargetIds?.Where(id => id != Guid.Empty).Distinct().ToArray() ?? [];
            if (promptIds.Length is < 1 or > 10) throw new CollectorStoreException("INVALID_PROMPT_SELECTION", "主问题数量必须为 1–10。", 400);
            if (targetIds.Length is < 1 or > 5) throw new CollectorStoreException("INVALID_TARGET_SELECTION", "目标平台数量必须为 1–5。", 400);
            if (request.Language is not ("zh-CN" or "en-US")) throw new CollectorStoreException("INVALID_LANGUAGE", "首期只支持 zh-CN 和 en-US。", 400);
            if (!ArticleWritingRules.IsSupportedStrategy(request.Strategy)) throw new CollectorStoreException("INVALID_STRATEGY", "文章类型无效。", 400);
            if (!ArticleWritingRules.IsSupportedLength(request.LengthProfile)) throw new CollectorStoreException("INVALID_LENGTH_PROFILE", "文章篇幅档位无效。", 400);
            if (request.Style is not ("professional" or "plain_explainer" or "purchase_decision")) throw new CollectorStoreException("INVALID_STYLE", "写作风格无效。", 400);
            if (request.Strategy == "reference_structure" && request.ReferenceUrls?.Count is not > 0) throw new CollectorStoreException("REFERENCE_REQUIRED", "参考范文策略需要至少一个参考链接。", 400);
            if (!request.WithoutRag && request.MaterialPreviewId is null) throw new CollectorStoreException("KNOWLEDGE_CONTEXT_REQUIRED", "请完成文章参考素材预览或明确选择不使用知识库。", 400);
            if (request.MaterialPreviewId is not null)
            {
                var material = await store.GetMaterialPreviewAsync(user.TenantId, siteId, request.MaterialPreviewId.Value, cancellationToken);
                if (material is null || material.ExpiresAt <= DateTimeOffset.UtcNow) throw new CollectorStoreException("KNOWLEDGE_CONTEXT_STALE", "文章参考素材预览已过期。", 409);
                if (material.WithoutRag != request.WithoutRag) throw new CollectorStoreException("KNOWLEDGE_CONTEXT_STALE", "文章参考素材选择已变化。", 409);
            }
            var account = await store.GetGenerationOptionsAsync(user.TenantId, siteId, cancellationToken);
            var hashInput = JsonSerializer.Serialize(new
            {
                SiteId = siteId,
                Request = request with { PromptIds = promptIds, TargetIds = targetIds }
            });
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashInput))).ToLowerInvariant();
            var preview = new ArticleGenerationPreviewView(Guid.NewGuid(), hash, promptIds, targetIds, promptIds.Length, ArticleGenerationPricing.UnitPrice,
                promptIds.Length * ArticleGenerationPricing.UnitPrice, account.Credits.AvailableCredits, DateTimeOffset.UtcNow.AddMinutes(15),
                request.WithoutRag ? ["未使用知识库依据，品牌事实均需人工核对。"] : [], [$"按主问题去重，每篇 {ArticleGenerationPricing.UnitPrice:0} 积分。"], request.MaterialPreviewId, request.WithoutRag);
            return Ok(await store.SaveGenerationPreviewAsync(user.TenantId, siteId, user.UserId, request with { PromptIds = promptIds, TargetIds = targetIds }, preview, cancellationToken));
        });

    /// <summary>提交已预览的生成批次并原子预占积分。</summary>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="request">生成预览标识和摘要。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    [HttpPost]
    [ProducesResponseType(typeof(ArticleGenerationAccepted), StatusCodes.Status202Accepted)]
    public Task<IActionResult> Submit(Guid siteId, [FromBody] ArticleGenerationSubmitRequest request, CancellationToken cancellationToken)
        => Execute(async () => { var user = RequireWriter(); var key = Request.Headers["Idempotency-Key"].FirstOrDefault() ?? string.Empty; return Accepted(await store.SubmitGenerationAsync(user.TenantId, siteId, user.UserId, request, key, cancellationToken)); });

    /// <summary>查询生成批次和逐项积分结算状态。</summary>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="batchId">生成批次标识。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    [HttpGet("{batchId:guid}")]
    [ProducesResponseType(typeof(ArticleGenerationBatchView), StatusCodes.Status200OK)]
    public Task<IActionResult> Batch(Guid siteId, Guid batchId, CancellationToken cancellationToken)
        => Execute(async () => { var user = RequireUser(); var result = await store.GetGenerationBatchAsync(user.TenantId, siteId, batchId, cancellationToken); return result is null ? NotFound() : Ok(result); });

    /// <summary>取消尚未开始的生成任务并释放对应预占。</summary>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="batchId">生成批次标识。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    [HttpPost("{batchId:guid}/cancel")]
    [ProducesResponseType(typeof(ArticleGenerationBatchView), StatusCodes.Status200OK)]
    public Task<IActionResult> Cancel(Guid siteId, Guid batchId, CancellationToken cancellationToken)
        => Execute(async () => { var user = RequireWriter(); var result = await store.CancelGenerationAsync(user.TenantId, siteId, user.UserId, batchId, cancellationToken); return result is null ? NotFound() : Ok(result); });
}
