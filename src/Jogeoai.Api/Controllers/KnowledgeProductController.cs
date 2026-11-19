using Jogeoai.Api;
using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Jogeoai.Api.Controllers;

/// <summary>
/// 素材库产品信息（不再推荐）：列表、草稿、Luna 识别任务、核验入库、归档与知识库同步。
/// 页面已由品牌知识库（/api/v1/sites/{siteId}/knowledge/items）替代；读接口保留兼容，已迁移为知识条目的产品的写操作返回 409 PRODUCT_MIGRATED。
/// </summary>
[ApiController]
[Obsolete("产品信息已由品牌知识库替代，请改用 /api/v1/sites/{siteId}/knowledge/items 与 /knowledge/extract-jobs；本接口仅为兼容保留。")]
[Route("api/v1/sites/{siteId:guid}/knowledge/products")]
public sealed class KnowledgeProductController(
    IKnowledgeProductStore products,
    IKnowledgeStore knowledge,
    IArticleSourceReader sourceReader,
    TenantContextResolver tenantContext) : ContentControllerBase(tenantContext)
{
    /// <summary>分页读取当前品牌的产品信息，并返回待核验数量。</summary>
    /// <param name="siteId">品牌标识。</param>
    /// <param name="status">状态筛选：draft、parsing、pending_review、approved、parse_failed 或 archived。</param>
    /// <param name="sourceKind">来源筛选：manual、file、url 或 completion。</param>
    /// <param name="q">按产品名搜索。</param>
    /// <param name="cursor">上一页返回的游标；与品牌和筛选条件绑定。</param>
    /// <param name="limit">每页数量，范围 1–100。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    [HttpGet]
    [ProducesResponseType(typeof(KnowledgeProductListPage), StatusCodes.Status200OK)]
    public Task<IActionResult> List(Guid siteId, [FromQueryJsonEnum] KnowledgeProductStatus? status = null,
        [FromQuery] string? sourceKind = null, [FromQuery] string? q = null, [FromQuery] string? cursor = null,
        [FromQuery] int limit = 20, CancellationToken cancellationToken = default)
        => Execute(async () =>
        {
            var user = RequireUser();
            return Ok(await products.ListAsync(user.TenantId, siteId, new KnowledgeProductListFilter(status, sourceKind, q), cursor, limit, cancellationToken));
        });

    /// <summary>创建产品草稿；可以只填产品名。</summary>
    /// <param name="siteId">品牌标识。</param>
    /// <param name="request">产品字段。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    [HttpPost]
    [ProducesResponseType(typeof(KnowledgeProductView), StatusCodes.Status201Created)]
    public Task<IActionResult> Create(Guid siteId, [FromBody] KnowledgeProductWrite request, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var user = RequireWriter();
            var value = await products.CreateAsync(user.TenantId, siteId, user.UserId, request, cancellationToken);
            return CreatedAtAction(nameof(Get), new { siteId, productId = value.ProductId }, value);
        });

    /// <summary>读取产品详情，含逐字段来源与最近一次识别任务。</summary>
    /// <param name="siteId">品牌标识。</param>
    /// <param name="productId">产品标识。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    [HttpGet("{productId:guid}")]
    [ProducesResponseType(typeof(KnowledgeProductView), StatusCodes.Status200OK)]
    public Task<IActionResult> Get(Guid siteId, Guid productId, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var user = RequireUser();
            var value = await products.GetAsync(user.TenantId, siteId, productId, cancellationToken);
            return value is null ? NotFound(NotFoundBody()) : Ok(value);
        });

    /// <summary>保存草稿；已入库产品的编辑直接生效并重新同步知识库。</summary>
    /// <param name="siteId">品牌标识。</param>
    /// <param name="productId">产品标识。</param>
    /// <param name="request">完整字段。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    [HttpPatch("{productId:guid}")]
    [ProducesResponseType(typeof(KnowledgeProductView), StatusCodes.Status200OK)]
    public Task<IActionResult> Update(Guid siteId, Guid productId, [FromBody] KnowledgeProductWrite request, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var user = await RequireProductWriterAsync(siteId, productId, cancellationToken);
            var value = await products.UpdateDraftAsync(user.TenantId, siteId, productId, user.UserId, request, cancellationToken);
            return value is null ? NotFound(NotFoundBody()) : Ok(value);
        });

    /// <summary>发起识别任务：file 从素材库文件、url 读取产品详情页、completion 结合品牌知识库补全；识别不消耗积分。</summary>
    /// <param name="siteId">品牌标识。</param>
    /// <param name="productId">产品标识。</param>
    /// <param name="request">任务类型与来源。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    [HttpPost("{productId:guid}/parse")]
    [ProducesResponseType(typeof(ProductParseJobView), StatusCodes.Status202Accepted)]
    public Task<IActionResult> Parse(Guid siteId, Guid productId, [FromBody] ProductParseRequest request, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var user = await RequireProductWriterAsync(siteId, productId, cancellationToken);
            var effective = request;
            if (request.Kind == ProductParseJobKind.Url)
            {
                if (string.IsNullOrWhiteSpace(request.Url))
                    throw new CollectorStoreException("PRODUCT_VALIDATION", "网址识别需要提供产品页面网址。", 400);
                var product = await products.GetAsync(user.TenantId, siteId, productId, cancellationToken)
                    ?? throw new CollectorStoreException("PRODUCT_NOT_FOUND", "产品信息不存在。", 404);
                // 读取网页会创建链接资料并占用知识库容量：先做状态与频控预检，避免被拒绝的请求仍留下链接资料；
                // 权威校验仍在 EnqueueParseAsync 的事务内完成。
                if (product.Status == KnowledgeProductStatus.Parsing)
                    throw new CollectorStoreException("PRODUCT_PARSE_IN_PROGRESS", "该产品已有进行中的识别任务。", 409);
                if (product.Status == KnowledgeProductStatus.Archived)
                    throw new CollectorStoreException("PRODUCT_STATE_CONFLICT", "当前状态不能发起识别。", 409);
                if (await products.CountParseJobsTodayAsync(user.TenantId, cancellationToken) >= KnowledgeProductRules.DailyParseLimit)
                    throw new CollectorStoreException("PRODUCT_PARSE_RATE_LIMITED",
                        $"今日识别次数已达上限（{KnowledgeProductRules.DailyParseLimit} 次，与网址识别共用），请明天再试。", 429);
                var page = await sourceReader.ReadAsync(request.Url, cancellationToken);
                var link = await knowledge.AddLinkDocumentAsync(user.TenantId, siteId, new KnowledgeLinkWrite(request.Url, product.Name), page, cancellationToken);
                effective = request with { DocumentId = link.DocumentId, Url = page.UrlNormalized };
            }
            var job = await products.EnqueueParseAsync(user.TenantId, siteId, productId, user.UserId, effective, cancellationToken);
            return AcceptedAtAction(nameof(GetJob), new { siteId, productId, jobId = job.JobId }, job);
        });

    /// <summary>轮询识别任务的阶段、结果与失败原因。</summary>
    /// <param name="siteId">品牌标识。</param>
    /// <param name="productId">产品标识。</param>
    /// <param name="jobId">任务标识。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    [HttpGet("{productId:guid}/parse-jobs/{jobId:guid}")]
    [ProducesResponseType(typeof(ProductParseJobView), StatusCodes.Status200OK)]
    public Task<IActionResult> GetJob(Guid siteId, Guid productId, Guid jobId, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var user = RequireUser();
            var value = await products.GetJobAsync(user.TenantId, siteId, productId, jobId, cancellationToken);
            return value is null ? NotFound(NotFoundBody()) : Ok(value);
        });

    /// <summary>取消尚未开始的识别任务。</summary>
    /// <param name="siteId">品牌标识。</param>
    /// <param name="productId">产品标识。</param>
    /// <param name="jobId">任务标识。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    [HttpPost("{productId:guid}/parse-jobs/{jobId:guid}/cancel")]
    [ProducesResponseType(typeof(ProductParseJobView), StatusCodes.Status200OK)]
    public Task<IActionResult> CancelJob(Guid siteId, Guid productId, Guid jobId, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var user = await RequireProductWriterAsync(siteId, productId, cancellationToken);
            var value = await products.CancelJobAsync(user.TenantId, siteId, productId, jobId, cancellationToken);
            return value is null ? NotFound(NotFoundBody()) : Ok(value);
        });

    /// <summary>核验入库：所有 AI 字段必须逐个核对；成功后写入品牌知识库并可在文章生成中指定该产品。</summary>
    /// <param name="siteId">品牌标识。</param>
    /// <param name="productId">产品标识。</param>
    /// <param name="request">核验后的字段与已核对字段名。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    [HttpPost("{productId:guid}/approve")]
    [ProducesResponseType(typeof(KnowledgeProductView), StatusCodes.Status200OK)]
    public Task<IActionResult> Approve(Guid siteId, Guid productId, [FromBody] ProductApproveRequest request, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var user = await RequireProductWriterAsync(siteId, productId, cancellationToken);
            var value = await products.ApproveAsync(user.TenantId, siteId, productId, user.UserId, request, cancellationToken);
            return value is null ? NotFound(NotFoundBody()) : Ok(value);
        });

    /// <summary>驳回待核验产品，回到草稿。</summary>
    /// <param name="siteId">品牌标识。</param>
    /// <param name="productId">产品标识。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    [HttpPost("{productId:guid}/reject")]
    [ProducesResponseType(typeof(KnowledgeProductView), StatusCodes.Status200OK)]
    public Task<IActionResult> Reject(Guid siteId, Guid productId, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var user = await RequireProductWriterAsync(siteId, productId, cancellationToken);
            var value = await products.RejectAsync(user.TenantId, siteId, productId, user.UserId, cancellationToken);
            return value is null ? NotFound(NotFoundBody()) : Ok(value);
        });

    /// <summary>归档产品并停用其知识库资料；归档后不再出现在「指定产品」中。</summary>
    /// <param name="siteId">品牌标识。</param>
    /// <param name="productId">产品标识。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    [HttpPost("{productId:guid}/archive")]
    [ProducesResponseType(typeof(KnowledgeProductView), StatusCodes.Status200OK)]
    public Task<IActionResult> Archive(Guid siteId, Guid productId, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var user = await RequireProductWriterAsync(siteId, productId, cancellationToken);
            var value = await products.ArchiveAsync(user.TenantId, siteId, productId, user.UserId, cancellationToken);
            return value is null ? NotFound(NotFoundBody()) : Ok(value);
        });

    /// <summary>重试把已入库产品写入品牌知识库。</summary>
    /// <param name="siteId">品牌标识。</param>
    /// <param name="productId">产品标识。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    [HttpPost("{productId:guid}/sync-knowledge")]
    [ProducesResponseType(typeof(KnowledgeProductView), StatusCodes.Status200OK)]
    public Task<IActionResult> SyncKnowledge(Guid siteId, Guid productId, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var user = await RequireProductWriterAsync(siteId, productId, cancellationToken);
            var value = await products.SyncKnowledgeAsync(user.TenantId, siteId, productId, user.UserId, cancellationToken);
            return value is null ? NotFound(NotFoundBody()) : Ok(value);
        });

    /// <summary>
    /// 写权限门禁：viewer 禁止；member 只能操作自己创建的产品；owner / admin 不限。
    /// 已迁移为品牌知识条目的产品一律返回 409 PRODUCT_MIGRATED，防止旧接口改写已被条目接管的知识库文档。
    /// </summary>
    private async Task<(Guid TenantId, Guid UserId, string Role)> RequireProductWriterAsync(Guid siteId, Guid productId, CancellationToken cancellationToken)
    {
        var user = RequireWriter();
        var product = await products.GetAsync(user.TenantId, siteId, productId, cancellationToken)
            ?? throw new CollectorStoreException("PRODUCT_NOT_FOUND", "产品信息不存在。", 404);
        if (user.Role.Equals("member", StringComparison.OrdinalIgnoreCase) && product.CreatedBy != user.UserId)
            throw new CollectorStoreException("CONTENT_FORBIDDEN", "成员只能修改自己创建的产品信息。", 403);
        if (await products.IsMigratedAsync(user.TenantId, productId, cancellationToken))
            throw new CollectorStoreException("PRODUCT_MIGRATED", "该产品已迁移到品牌知识库的「产品/服务」维度，请在品牌知识库中编辑。", 409);
        return user;
    }

    private static object NotFoundBody() => new { error = new { code = "PRODUCT_NOT_FOUND", message = "产品信息不存在。" } };
}
