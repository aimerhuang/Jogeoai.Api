using Jogeoai.Api;
using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Jogeoai.Api.Controllers;

/// <summary>
/// 品牌知识库：按 6 个固定维度维护品牌事实（每品牌最多 50 条），以及「从网址识别」批量生成候选、勾选编辑后保存。
/// 权限：所有成员可读；viewer 不能写；member 可新增、发起识别并保存候选，只能编辑或删除自己创建的条目；owner / admin 不限。
/// 品牌不属于当前租户时返回 404。识别不扣积分，每租户每日 50 次（与产品识别共用）。
/// </summary>
[ApiController]
[Route("api/v1/sites/{siteId:guid}/knowledge")]
public sealed class BrandKnowledgeController(
    IBrandKnowledgeItemStore items,
    IKnowledgeExtractJobStore extractJobs,
    IResearchStore research,
    TenantContextResolver tenantContext) : ContentControllerBase(tenantContext)
{
    /// <summary>列出品牌知识条目：按维度固定顺序、同维度按创建时间正序；各维度计数与筛选无关，恒含 6 个维度。</summary>
    /// <param name="siteId">品牌标识。</param>
    /// <param name="dimension">维度筛选：brand_intro、selling_point、audience、differentiator、product_service 或 faq；为空表示全部。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    /// <response code="200">条目列表、各维度计数、总数与上限。</response>
    /// <response code="400">INVALID_REQUEST：维度取值无效。</response>
    /// <response code="401">AUTHENTICATION_REQUIRED：未登录。</response>
    /// <response code="404">RESOURCE_NOT_FOUND：品牌不存在或不属于当前租户。</response>
    [HttpGet("items")]
    [ProducesResponseType(typeof(BrandKnowledgeItemList), StatusCodes.Status200OK)]
    public Task<IActionResult> ListItems(Guid siteId, [FromQueryJsonEnum] BrandKnowledgeDimension? dimension = null,
        CancellationToken cancellationToken = default)
        => Execute(async () =>
        {
            var user = RequireUser();
            return Ok(await items.ListAsync(user.TenantId, siteId, dimension, cancellationToken));
        });

    /// <summary>手动新增一条知识（来源为手动填写）。</summary>
    /// <param name="siteId">品牌标识。</param>
    /// <param name="request">维度、标题（1–100 字）与内容（1–2000 字）。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    /// <response code="201">新增的条目。</response>
    /// <response code="400">KNOWLEDGE_ITEM_VALIDATION：维度、标题或内容不符合要求。</response>
    /// <response code="403">CONTENT_FORBIDDEN：只读成员不能新增。</response>
    /// <response code="404">RESOURCE_NOT_FOUND：品牌不存在或不属于当前租户。</response>
    /// <response code="409">KNOWLEDGE_ITEM_TITLE_CONFLICT：同维度已有同名知识；KNOWLEDGE_ITEM_LIMIT：已达 50 条上限。</response>
    [HttpPost("items")]
    [ProducesResponseType(typeof(BrandKnowledgeItemView), StatusCodes.Status201Created)]
    public Task<IActionResult> CreateItem(Guid siteId, [FromBody] BrandKnowledgeItemWrite request, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var user = RequireKnowledgeWriter();
            var created = await items.CreateManyAsync(user.TenantId, siteId, user.UserId,
                [new BrandKnowledgeItemCreate(request)], BrandKnowledgeItemSource.Manual, cancellationToken);
            return StatusCode(StatusCodes.Status201Created, created[0]);
        });

    /// <summary>编辑知识条目，可修改维度；需提交读取时的内容版本作为乐观锁，内容未变化时不升版本。</summary>
    /// <param name="siteId">品牌标识。</param>
    /// <param name="itemId">条目标识。</param>
    /// <param name="request">维度、标题、内容与读取时的内容版本。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    /// <response code="200">编辑后的条目。</response>
    /// <response code="400">KNOWLEDGE_ITEM_VALIDATION：字段不符合要求。</response>
    /// <response code="403">CONTENT_FORBIDDEN：只读成员不能编辑，普通成员只能编辑自己创建的条目。</response>
    /// <response code="404">KNOWLEDGE_ITEM_NOT_FOUND：条目不存在、已删除或不属于该品牌；RESOURCE_NOT_FOUND：品牌已归档、不存在或不属于当前租户。</response>
    /// <response code="409">KNOWLEDGE_ITEM_STALE：条目已被他人修改；KNOWLEDGE_ITEM_TITLE_CONFLICT：同维度已有同名知识。</response>
    [HttpPatch("items/{itemId:guid}")]
    [ProducesResponseType(typeof(BrandKnowledgeItemView), StatusCodes.Status200OK)]
    public Task<IActionResult> UpdateItem(Guid siteId, Guid itemId, [FromBody] BrandKnowledgeItemUpdate request,
        CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var user = await RequireItemWriterAsync(siteId, itemId, cancellationToken);
            return Ok(await items.UpdateAsync(user.TenantId, siteId, itemId, user.UserId, request, cancellationToken));
        });

    /// <summary>删除知识条目；删除后不再被写稿与 AI 推荐监测问题检索到，同维度可再次使用该标题。</summary>
    /// <param name="siteId">品牌标识。</param>
    /// <param name="itemId">条目标识。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    /// <response code="204">已删除。</response>
    /// <response code="403">CONTENT_FORBIDDEN：只读成员不能删除，普通成员只能删除自己创建的条目。</response>
    /// <response code="404">KNOWLEDGE_ITEM_NOT_FOUND：条目不存在、已删除或不属于该品牌；RESOURCE_NOT_FOUND：品牌已归档、不存在或不属于当前租户。</response>
    [HttpDelete("items/{itemId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public Task<IActionResult> DeleteItem(Guid siteId, Guid itemId, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var user = await RequireItemWriterAsync(siteId, itemId, cancellationToken);
            await items.DeleteAsync(user.TenantId, siteId, itemId, user.UserId, cancellationToken);
            return NoContent();
        });

    /// <summary>发起网址识别：读取一个公开网页，由模型按维度拆成多条候选；识别不扣积分，每品牌同一时间只有一个任务。</summary>
    /// <param name="siteId">品牌标识。</param>
    /// <param name="request">公开 http(s) 网址与可选的限定维度。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    /// <response code="202">已创建识别任务，轮询任务接口获取进度与候选。</response>
    /// <response code="400">KNOWLEDGE_ITEM_VALIDATION：网址或维度无效。</response>
    /// <response code="403">CONTENT_FORBIDDEN：只读成员不能发起识别。</response>
    /// <response code="404">RESOURCE_NOT_FOUND：品牌不存在或不属于当前租户。</response>
    /// <response code="409">KNOWLEDGE_EXTRACT_IN_PROGRESS：该品牌已有进行中或待处理的识别。</response>
    /// <response code="429">KNOWLEDGE_EXTRACT_RATE_LIMITED：今日识别次数已达上限（与产品识别共用）。</response>
    /// <response code="503">KNOWLEDGE_EXTRACT_NOT_CONFIGURED：识别服务未配置。</response>
    [HttpPost("extract-jobs")]
    [ProducesResponseType(typeof(KnowledgeExtractJobView), StatusCodes.Status202Accepted)]
    public Task<IActionResult> CreateExtractJob(Guid siteId, [FromBody] KnowledgeExtractRequest request, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var user = RequireKnowledgeWriter();
            // 未配置识别服务时直接拒绝，不占用当日额度；Worker 调用前仍会再次检查。
            var configuration = await research.GetProviderConfigurationAsync("openai-compatible", cancellationToken)
                ?? await research.GetProviderConfigurationAsync("openai-api", cancellationToken);
            if (configuration is null || string.IsNullOrWhiteSpace(configuration.Credential))
                throw new CollectorStoreException("KNOWLEDGE_EXTRACT_NOT_CONFIGURED", "网址识别服务未配置，请联系管理员。", 503);
            var job = await extractJobs.EnqueueAsync(user.TenantId, siteId, user.UserId, request, cancellationToken);
            return AcceptedAtAction(nameof(GetExtractJob), new { siteId, jobId = job.JobId }, job);
        });

    /// <summary>读取品牌当前的识别任务：进行中或候选待处理的任务，或 24 小时内失败且未放弃的任务；没有时返回 204。</summary>
    /// <param name="siteId">品牌标识。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    /// <response code="200">当前识别任务。</response>
    /// <response code="204">没有需要处理的识别任务。</response>
    /// <response code="404">RESOURCE_NOT_FOUND：品牌不存在或不属于当前租户。</response>
    [HttpGet("extract-jobs/current")]
    [ProducesResponseType(typeof(KnowledgeExtractJobView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public Task<IActionResult> GetCurrentExtractJob(Guid siteId, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var user = RequireUser();
            var job = await extractJobs.GetCurrentAsync(user.TenantId, siteId, cancellationToken);
            return job is null ? NoContent() : Ok(job);
        });

    /// <summary>轮询识别任务的状态、阶段、候选与失败原因。</summary>
    /// <param name="siteId">品牌标识。</param>
    /// <param name="jobId">识别任务标识。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    /// <response code="200">识别任务。</response>
    /// <response code="404">KNOWLEDGE_EXTRACT_NOT_FOUND：任务不存在或不属于该品牌。</response>
    [HttpGet("extract-jobs/{jobId:guid}")]
    [ProducesResponseType(typeof(KnowledgeExtractJobView), StatusCodes.Status200OK)]
    public Task<IActionResult> GetExtractJob(Guid siteId, Guid jobId, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var user = RequireUser();
            var job = await extractJobs.GetAsync(user.TenantId, siteId, jobId, cancellationToken);
            return job is null ? NotFound(ExtractNotFoundBody()) : Ok(job);
        });

    /// <summary>取消待处理或进行中的识别任务；进行中的任务在下一次心跳时停止。</summary>
    /// <param name="siteId">品牌标识。</param>
    /// <param name="jobId">识别任务标识。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    /// <response code="200">已取消的任务。</response>
    /// <response code="403">CONTENT_FORBIDDEN：只读成员不能取消识别。</response>
    /// <response code="404">KNOWLEDGE_EXTRACT_NOT_FOUND：任务不存在或不属于该品牌。</response>
    /// <response code="409">KNOWLEDGE_EXTRACT_STATE_CONFLICT：识别已结束，候选只能保存或放弃。</response>
    [HttpPost("extract-jobs/{jobId:guid}/cancel")]
    [ProducesResponseType(typeof(KnowledgeExtractJobView), StatusCodes.Status200OK)]
    public Task<IActionResult> CancelExtractJob(Guid siteId, Guid jobId, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var user = RequireKnowledgeWriter();
            return Ok(await extractJobs.CancelAsync(user.TenantId, siteId, jobId, cancellationToken));
        });

    /// <summary>
    /// 保存勾选（可编辑）的候选为知识条目，来源记为识别网址并保留原文依据；服务端重新校验字段、上限与重名，
    /// 全部成功才写入并把任务置为已保存，任一失败都不写入、任务保持可保存。
    /// </summary>
    /// <param name="siteId">品牌标识。</param>
    /// <param name="jobId">识别任务标识。</param>
    /// <param name="request">勾选的候选：候选序号必须属于该任务，维度、标题与内容可编辑。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    /// <response code="201">新增的条目，顺序与提交一致。</response>
    /// <response code="400">KNOWLEDGE_ITEM_VALIDATION：候选不属于该任务、重复提交或字段不符合要求。</response>
    /// <response code="403">CONTENT_FORBIDDEN：只读成员不能保存。</response>
    /// <response code="404">KNOWLEDGE_EXTRACT_NOT_FOUND：任务不存在或不属于该品牌。</response>
    /// <response code="409">KNOWLEDGE_EXTRACT_NOT_READY：任务未完成或已处理；KNOWLEDGE_ITEM_LIMIT：超过剩余额度；KNOWLEDGE_ITEM_TITLE_CONFLICT：同维度已有同名知识。</response>
    /// <response code="410">KNOWLEDGE_EXTRACT_EXPIRED：候选已超过 24 小时，需要重新识别。</response>
    [HttpPost("extract-jobs/{jobId:guid}/save")]
    [ProducesResponseType(typeof(IReadOnlyList<BrandKnowledgeItemView>), StatusCodes.Status201Created)]
    public Task<IActionResult> SaveExtractJob(Guid siteId, Guid jobId, [FromBody] KnowledgeExtractSaveRequest request,
        CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var user = RequireKnowledgeWriter();
            var created = await extractJobs.SaveCandidatesAsync(user.TenantId, siteId, jobId, user.UserId, request, cancellationToken);
            return StatusCode(StatusCodes.Status201Created, created);
        });

    /// <summary>放弃识别结果（已完成或失败的任务），任务置为已放弃，品牌可以重新发起识别。</summary>
    /// <param name="siteId">品牌标识。</param>
    /// <param name="jobId">识别任务标识。</param>
    /// <param name="cancellationToken">取消请求令牌。</param>
    /// <response code="200">已放弃的任务。</response>
    /// <response code="403">CONTENT_FORBIDDEN：只读成员不能放弃。</response>
    /// <response code="404">KNOWLEDGE_EXTRACT_NOT_FOUND：任务不存在或不属于该品牌。</response>
    /// <response code="409">KNOWLEDGE_EXTRACT_NOT_READY：任务尚未完成（请先取消）或已保存。</response>
    [HttpPost("extract-jobs/{jobId:guid}/discard")]
    [ProducesResponseType(typeof(KnowledgeExtractJobView), StatusCodes.Status200OK)]
    public Task<IActionResult> DiscardExtractJob(Guid siteId, Guid jobId, CancellationToken cancellationToken)
        => Execute(async () =>
        {
            var user = RequireKnowledgeWriter();
            return Ok(await extractJobs.MarkResolvedAsync(user.TenantId, siteId, jobId, "discarded", cancellationToken));
        });

    /// <summary>写权限门禁：viewer 禁止（403 CONTENT_FORBIDDEN）。</summary>
    private (Guid TenantId, Guid UserId, string Role) RequireKnowledgeWriter()
    {
        var user = RequireUser();
        if (user.Role.Equals("viewer", StringComparison.OrdinalIgnoreCase))
            throw new CollectorStoreException("CONTENT_FORBIDDEN", "当前角色只能查看品牌知识库。", 403);
        return user;
    }

    /// <summary>条目写权限：viewer 禁止；品牌已归档或不存在时 404 RESOURCE_NOT_FOUND（由 Store 的 GetAsync 抛出）；member 只能编辑或删除自己创建的条目；owner / admin 不限。</summary>
    private async Task<(Guid TenantId, Guid UserId, string Role)> RequireItemWriterAsync(Guid siteId, Guid itemId,
        CancellationToken cancellationToken)
    {
        var user = RequireKnowledgeWriter();
        var item = await items.GetAsync(user.TenantId, siteId, itemId, cancellationToken)
            ?? throw new CollectorStoreException("KNOWLEDGE_ITEM_NOT_FOUND", "知识不存在或已删除。", 404);
        if (user.Role.Equals("member", StringComparison.OrdinalIgnoreCase) && item.CreatedBy != user.UserId)
            throw new CollectorStoreException("CONTENT_FORBIDDEN", "成员只能编辑或删除自己创建的知识。", 403);
        return user;
    }

    private static object ExtractNotFoundBody() => new { error = new { code = "KNOWLEDGE_EXTRACT_NOT_FOUND", message = "识别任务不存在。" } };
}
