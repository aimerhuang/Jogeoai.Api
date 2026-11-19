using Microsoft.AspNetCore.Mvc;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Providers.Abstractions;
using Jogeoai.Providers.Cloro;

namespace Jogeoai.Api.Controllers;

/// <summary>系统管理员管理 Cloro 全局连接并执行诊断请求；多账号 key 由 key 池接口管理。</summary>
[ApiController]
[Route("api/v1/providers/cloro")]
public sealed class CloroController(
    ICloroSettingsStore settingsStore,
    ICloroAdapter adapter,
    ICloroKeyPool keyPool,
    ICloroKeyAdminStore keyStore,
    IResearchStore adminStore) : ControllerBase
{
    /// <summary>返回 Cloro 全局地址与启用状态，不暴露 API Key；仅限系统管理员。</summary>
    /// <param name="cancellationToken">取消数据库读取的令牌。</param>
    [HttpGet("settings")]
    [ProducesResponseType(typeof(CloroSettingsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<CloroSettingsResponse>> GetSettings(CancellationToken cancellationToken)
    {
        if (await SystemAdministratorAccess.RequireAsync(this, adminStore, cancellationToken) is { } failure) return failure;
        try { return Ok(await BuildSettingsAsync(cancellationToken)); }
        catch (CollectorStoreException exception) { return Error(exception.StatusCode, exception.Code, exception.Message); }
    }

    /// <summary>保存 Cloro 地址和全局启用开关；仅限系统管理员。提供 apiKey 时兼容写入 default 账号。</summary>
    /// <param name="request">Cloro 地址、可选 API Key 和启用标志。</param>
    /// <param name="cancellationToken">取消数据库写入的令牌。</param>
    [HttpPut("settings")]
    [ProducesResponseType(typeof(CloroSettingsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CloroSettingsResponse>> SaveSettings(
        [FromBody] SaveCloroSettingsRequest request,
        CancellationToken cancellationToken)
    {
        if (await SystemAdministratorAccess.RequireAsync(this, adminStore, cancellationToken) is { } failure) return failure;
        if (!Uri.TryCreate(request.ApiUrl, UriKind.Absolute, out var apiUrl)
            || apiUrl.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(apiUrl.UserInfo)
            || !string.IsNullOrEmpty(apiUrl.Query)
            || !string.IsNullOrEmpty(apiUrl.Fragment))
        {
            return BadRequest("apiUrl must be an absolute HTTP or HTTPS URL.");
        }

        string? apiKey = null;
        if (!string.IsNullOrWhiteSpace(request.ApiKey))
        {
            try { apiKey = CloroKeyFormat.NormalizeApiKey(request.ApiKey); }
            catch (ArgumentException exception) { return Error(StatusCodes.Status400BadRequest, "INVALID_CLORO_KEY", exception.Message); }
        }
        try
        {
            if (apiKey is not null) await keyStore.UpsertDefaultKeyAsync(apiKey, cancellationToken);
            // The singleton copy is kept only so an older release can be rolled back to.
            await settingsStore.SaveAsync(
                new CloroConnectionSettings(apiKey ?? "", apiUrl.ToString().TrimEnd('/'), request.Enabled),
                cancellationToken);
            return Ok(await BuildSettingsAsync(cancellationToken));
        }
        catch (CollectorStoreException exception) { return Error(exception.StatusCode, exception.Code, exception.Message); }
    }

    /// <summary>从 key 池选账号提交 Cloro 任务并轮询，返回标准化回答和所用账号；仅限系统管理员。</summary>
    /// <param name="request">发送给 Cloro 的回答引擎、Prompt 和国家/地区。</param>
    /// <param name="cancellationToken">取消外部请求的令牌。</param>
    [HttpPost("test")]
    [ProducesResponseType(typeof(CloroTestResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(StatusCodes.Status504GatewayTimeout)]
    public async Task<ActionResult<CloroTestResult>> Test(
        [FromBody] CloroTestRequest request,
        CancellationToken cancellationToken)
    {
        if (await SystemAdministratorAccess.RequireAsync(this, adminStore, cancellationToken) is { } failure) return failure;
        if (!Enum.IsDefined(request.Model)) return BadRequest("model must be one of: chatgpt, perplexity, copilot, gemini, google-ai-mode, google-ai-overview.");
        if (string.IsNullOrWhiteSpace(request.Prompt)) return BadRequest("prompt is required.");

        try
        {
            return Ok(await adapter.RunAsync(request, cancellationToken));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(exception.Message);
        }
        catch (CloroProviderException exception)
        {
            var status = exception.Code is "CLORO_NOT_CONFIGURED" or "CLORO_KEY_POOL_EXHAUSTED" or "CLORO_KEY_POOL_BUSY"
                    or "CLORO_KEY_POOL_UNAVAILABLE" or "CLORO_KEY_SWITCH_LIMIT" ? StatusCodes.Status503ServiceUnavailable
                : exception.Code is "CLORO_POLL_TIMEOUT" or "CLORO_GENERATION_TIMEOUT" ? StatusCodes.Status504GatewayTimeout
                : StatusCodes.Status502BadGateway;
            var details = new ProblemDetails
            {
                Status = status,
                Title = "Cloro provider request failed.",
                Detail = exception.Message,
            };
            details.Extensions["code"] = exception.Code;
            if (exception.NextAttemptAt is { } nextAttemptAt) details.Extensions["nextAvailableAt"] = nextAttemptAt;
            return new ObjectResult(details) { StatusCode = status };
        }
        catch (TimeoutException exception)
        {
            return Problem(statusCode: StatusCodes.Status504GatewayTimeout, detail: exception.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Problem(statusCode: StatusCodes.Status504GatewayTimeout, detail: "Cloro task polling timed out.");
        }
        catch (HttpRequestException exception)
        {
            return Problem(statusCode: StatusCodes.Status502BadGateway, detail: exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return Problem(statusCode: StatusCodes.Status502BadGateway, detail: exception.Message);
        }
    }

    // configured = globally enabled and at least one enabled, unarchived key in the pool.
    private async Task<CloroSettingsResponse> BuildSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsStore.GetAsync(cancellationToken);
        var availability = await keyPool.GetAvailabilityAsync(cancellationToken);
        return new CloroSettingsResponse(
            Configured: settings is { Enabled: true } && availability.EnabledKeys > 0,
            settings?.ApiUrl,
            settings?.Enabled ?? false,
            settings?.UpdatedAt,
            availability.EnabledKeys,
            availability.AvailableKeys);
    }

    private ObjectResult Error(int status, string code, string message) =>
        StatusCode(status, new { error = new { code, message } });
}

/// <summary>保存 Cloro 全局连接的请求体。</summary>
/// <param name="ApiKey">可选 API Key（兼容旧接口）：default 账号不存在时新增，存在且无在途任务时替换，否则 409。只保存，不会返回。</param>
/// <param name="ApiUrl">Cloro 异步任务接口的绝对 HTTP 或 HTTPS 地址。</param>
/// <param name="Enabled">是否全局启用 Cloro。</param>
public sealed record SaveCloroSettingsRequest(string? ApiKey, string ApiUrl, bool Enabled = true);

/// <summary>已保存 Cloro 连接的安全视图。</summary>
/// <param name="Configured">是否全局启用且 key 池中至少有一个启用中的 key。</param>
/// <param name="ApiUrl">已配置的 Cloro 地址。</param>
/// <param name="Enabled">是否全局启用。</param>
/// <param name="UpdatedAt">最近一次更新的 UTC 时间。</param>
/// <param name="EnabledKeys">key 池中启用且未归档的账号数。</param>
/// <param name="AvailableKeys">key 池中当前可被选中的账号数。</param>
public sealed record CloroSettingsResponse(
    bool Configured,
    string? ApiUrl,
    bool Enabled,
    DateTimeOffset? UpdatedAt,
    int EnabledKeys = 0,
    int AvailableKeys = 0);
