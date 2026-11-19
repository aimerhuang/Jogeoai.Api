using System.Text.Json;
using System.Text.Json.Serialization;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Providers.Abstractions;
using Jogeoai.Providers.Cloro;
using Microsoft.AspNetCore.Mvc;

namespace Jogeoai.Api.Controllers;

/// <summary>
/// 系统管理员管理 Cloro 多账号 API Key 池。一条记录代表一个 Cloro 账号（组织）及其唯一 key；
/// 同一组织的多个 key 共享余额，请勿把它们录成多个账号。任何响应都只返回 key 后 4 位。
/// </summary>
[ApiController]
[Route("api/v1/providers/cloro")]
public sealed class CloroKeysController(
    ICloroKeyAdminStore store,
    ICloroKeyPool pool,
    ICloroCreditsClient creditsClient,
    CloroKeyCreditsRefresher refresher,
    IResearchStore adminStore) : ControllerBase
{
    /// <summary>列出 Cloro 账号及其状态、余额、重置时间和在途任务数；默认不含已归档账号。</summary>
    /// <param name="includeArchived">是否包含已归档账号。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpGet("keys")]
    [ProducesResponseType(typeof(IReadOnlyList<CloroKeyView>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<IReadOnlyList<CloroKeyView>>> List([FromQuery] bool includeArchived = false,
        CancellationToken cancellationToken = default)
    {
        if (await SystemAdministratorAccess.RequireAsync(this, adminStore, cancellationToken) is { } failure) return failure;
        try { return Ok(await store.ListAsync(includeArchived, cancellationToken)); }
        catch (CollectorStoreException exception) { return Error(exception); }
    }

    /// <summary>录入一个 Cloro 账号；默认先调用 Cloro 余额接口校验 key，401 拒绝保存。</summary>
    /// <param name="request">账号名称、API Key、优先级、启用开关和是否跳过校验。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpPost("keys")]
    [ProducesResponseType(typeof(CloroKeyView), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<CloroKeyView>> Create([FromBody] CreateCloroKeyRequest request, CancellationToken cancellationToken)
    {
        if (await SystemAdministratorAccess.RequireAsync(this, adminStore, cancellationToken) is { } failure) return failure;
        string label, key;
        int priority;
        try
        {
            label = CloroKeyFormat.NormalizeLabel(request.AccountLabel);
            key = CloroKeyFormat.NormalizeApiKey(request.ApiKey);
            priority = CloroKeyFormat.ValidatePriority(request.Priority);
        }
        catch (ArgumentException exception) { return Error(StatusCodes.Status400BadRequest, "INVALID_CLORO_KEY", exception.Message); }
        try
        {
            // Check duplicates first so a duplicate key is never sent to Cloro.
            if (await store.ExistsAsync(label, key, cancellationToken))
                return Error(StatusCodes.Status409Conflict, "CLORO_KEY_DUPLICATE", "账号名称或 API Key 已存在。");
            CloroCreditsSnapshot? credits = null;
            if (!request.SkipValidation)
            {
                try { credits = await creditsClient.GetCreditsAsync(new CloroKeyLease(Guid.Empty, label, key, CloroKeyFormat.Hint(key)), cancellationToken); }
                catch (CloroProviderException exception) when (exception.Code == "CLORO_KEY_INVALID")
                {
                    return Error(StatusCodes.Status400BadRequest, "CLORO_KEY_INVALID", "Cloro 拒绝了该 API Key，未保存。");
                }
                catch (CloroProviderException exception) when (exception.Code == "CLORO_NOT_CONFIGURED")
                {
                    return Error(StatusCodes.Status503ServiceUnavailable, "CLORO_NOT_CONFIGURED",
                        "请先保存 Cloro 地址，或设置 skipValidation=true 跳过校验。");
                }
                catch (CloroProviderException)
                {
                    return Error(StatusCodes.Status502BadGateway, "CLORO_KEY_VALIDATION_UNAVAILABLE",
                        "暂时无法向 Cloro 校验该 key；可稍后重试，或设置 skipValidation=true 以“余额未知”状态保存。");
                }
            }
            var created = await store.CreateAsync(new CreateCloroKeyCommand(label, key, priority, request.Enabled), cancellationToken);
            if (credits is not null)
            {
                await pool.ApplyCreditsAsync(created.Id, credits, cancellationToken);
                created = await store.GetAsync(created.Id, cancellationToken) ?? created;
            }
            return StatusCode(StatusCodes.Status201Created, created);
        }
        catch (CollectorStoreException exception) { return Error(exception); }
        catch (ArgumentException exception) { return Error(StatusCodes.Status400BadRequest, "INVALID_CLORO_KEY", exception.Message); }
    }

    /// <summary>修改账号名称、优先级或启用状态；key 不可原地修改，轮换时请新增账号后停用旧账号。</summary>
    /// <param name="id">key 池记录标识。</param>
    /// <param name="request">需要修改的字段；不接受 apiKey。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpPatch("keys/{id:guid}")]
    [ProducesResponseType(typeof(CloroKeyView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CloroKeyView>> Update(Guid id, [FromBody] UpdateCloroKeyRequest request,
        CancellationToken cancellationToken)
    {
        if (await SystemAdministratorAccess.RequireAsync(this, adminStore, cancellationToken) is { } failure) return failure;
        if (request.UnsupportedFields is { Count: > 0 } extra)
            return extra.Keys.Any(name => name.Equals("apiKey", StringComparison.OrdinalIgnoreCase))
                ? Error(StatusCodes.Status400BadRequest, "CLORO_KEY_IMMUTABLE", "key 不可原地修改；请新增账号后停用旧账号，待在途任务结束再归档。")
                : Error(StatusCodes.Status400BadRequest, "INVALID_CLORO_KEY", "只能修改 accountLabel、priority 和 enabled。");
        try
        {
            var updated = await store.UpdateAsync(new UpdateCloroKeyCommand(id, request.AccountLabel, request.Priority, request.Enabled),
                cancellationToken);
            return updated is null ? NotFoundError() : Ok(updated);
        }
        catch (ArgumentException exception) { return Error(StatusCodes.Status400BadRequest, "INVALID_CLORO_KEY", exception.Message); }
        catch (CollectorStoreException exception) { return Error(exception); }
    }

    /// <summary>软归档账号；仍有已提交、未终态的任务引用该 key 时返回 409 和在途任务数。</summary>
    /// <param name="id">key 池记录标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpDelete("keys/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Archive(Guid id, CancellationToken cancellationToken)
    {
        if (await SystemAdministratorAccess.RequireAsync(this, adminStore, cancellationToken) is { } failure) return failure;
        try
        {
            var result = await store.ArchiveAsync(id, cancellationToken);
            if (result is null) return NotFoundError();
            if (!result.Archived)
                return StatusCode(StatusCodes.Status409Conflict, new
                {
                    error = new
                    {
                        code = "CLORO_KEY_IN_USE",
                        message = $"仍有 {result.InFlightTaskCount} 个在途任务使用该 key，请先停用并等待任务结束。",
                        inFlightTaskCount = result.InFlightTaskCount,
                    },
                });
            return NoContent();
        }
        catch (CollectorStoreException exception) { return Error(exception); }
    }

    /// <summary>同步刷新单个账号的余额，返回更新后的视图；查询失败时视图中的 lastErrorCode 记录原因。</summary>
    /// <param name="id">key 池记录标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpPost("keys/{id:guid}/refresh-credits")]
    [ProducesResponseType(typeof(CloroKeyView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CloroKeyView>> RefreshCredits(Guid id, CancellationToken cancellationToken)
    {
        if (await SystemAdministratorAccess.RequireAsync(this, adminStore, cancellationToken) is { } failure) return failure;
        try
        {
            var view = await store.GetAsync(id, cancellationToken);
            if (view is null || view.ArchivedAt is not null || await pool.GetAsync(id, cancellationToken) is not { } key)
                return NotFoundError();
            await refresher.RefreshAsync(key, cancellationToken);
            return Ok(await store.GetAsync(id, cancellationToken));
        }
        catch (CollectorStoreException exception) { return Error(exception); }
    }

    /// <summary>串行刷新所有启用、未归档账号的余额，返回更新后的账号列表。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpPost("keys/refresh-credits")]
    [ProducesResponseType(typeof(IReadOnlyList<CloroKeyView>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<CloroKeyView>>> RefreshAllCredits(CancellationToken cancellationToken)
    {
        if (await SystemAdministratorAccess.RequireAsync(this, adminStore, cancellationToken) is { } failure) return failure;
        try
        {
            foreach (var key in await pool.ListRefreshDueAsync(TimeSpan.Zero, cancellationToken))
                await refresher.RefreshAsync(key, cancellationToken);
            return Ok(await store.ListAsync(false, cancellationToken));
        }
        catch (CollectorStoreException exception) { return Error(exception); }
    }

    /// <summary>key 池概览：账号数、可用数、耗尽数、冷却数、无效数、已知总余额、最早重置与恢复时间、池状态。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpGet("pool")]
    [ProducesResponseType(typeof(CloroKeyPoolSummary), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<CloroKeyPoolSummary>> Pool(CancellationToken cancellationToken)
    {
        if (await SystemAdministratorAccess.RequireAsync(this, adminStore, cancellationToken) is { } failure) return failure;
        try { return Ok(await store.GetSummaryAsync(cancellationToken)); }
        catch (CollectorStoreException exception) { return Error(exception); }
    }

    private ObjectResult NotFoundError() => Error(StatusCodes.Status404NotFound, "RESOURCE_NOT_FOUND", "Cloro 账号不存在或已归档。");
    private ObjectResult Error(CollectorStoreException exception) => Error(exception.StatusCode, exception.Code, exception.Message);
    private ObjectResult Error(int status, string code, string message) => StatusCode(status, new { error = new { code, message } });
}

/// <summary>录入 Cloro 账号的请求体；key 只保存，不会在任何响应中返回。</summary>
/// <param name="AccountLabel">账号名称，唯一，1–64 字符。</param>
/// <param name="ApiKey">该账号的 Cloro API Key；同一 key 不能重复录入。</param>
/// <param name="Priority">优先级，越小越先用，默认 100。</param>
/// <param name="Enabled">是否启用，默认启用。</param>
/// <param name="SkipValidation">是否跳过 Cloro 余额校验；网络不可用时可设为 true，以“余额未知”状态保存。</param>
public sealed record CreateCloroKeyRequest(string AccountLabel, string ApiKey, int Priority = 100, bool Enabled = true,
    bool SkipValidation = false);

/// <summary>修改 Cloro 账号的请求体；只接受名称、优先级和启用状态，提供 apiKey 会返回 400。</summary>
public sealed record UpdateCloroKeyRequest
{
    /// <summary>新的账号名称；不提供表示不修改。</summary>
    public string? AccountLabel { get; init; }
    /// <summary>新的优先级（越小越先用）；不提供表示不修改。</summary>
    public int? Priority { get; init; }
    /// <summary>新的启用状态；停用只影响新任务，重新启用无效账号会触发重新校验。</summary>
    public bool? Enabled { get; init; }
    /// <summary>未支持的字段（例如 apiKey），提供时请求被拒绝。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnsupportedFields { get; init; }
}
