using Jogeoai.Api;
using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Jogeoai.Api.Controllers;

/// <summary>管理系统级品牌研究 Provider 配置。</summary>
[ApiController]
[Route("api/v1/research/providers")]
public sealed class ResearchProvidersController(IResearchStore store) : ControllerBase
{
    /// <summary>列出已配置的 Provider 元数据，不返回密钥。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ResearchProviderSettingsView>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<IReadOnlyList<ResearchProviderSettingsView>>> List(CancellationToken cancellationToken)
    {
        var failure = await RequireAdministratorAsync(cancellationToken);
        if (failure is not null) return failure;
        return Ok(await store.ListProviderSettingsAsync(cancellationToken));
    }

    /// <summary>保存一个系统级 Provider 配置。</summary>
    /// <param name="provider">Provider 标识，如 openai-compatible。</param>
    /// <param name="request">模型、地址、凭据和运行开关。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpPut("{provider}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Save(string provider, [FromBody] SaveResearchProviderSettingsRequest request,
        CancellationToken cancellationToken)
    {
        var failure = await RequireAdministratorAsync(cancellationToken);
        if (failure is not null) return failure;
        try
        {
            await store.SaveProviderSettingsAsync(provider, request, cancellationToken);
            return NoContent();
        }
        catch (ArgumentException exception) { return BadRequest(new { error = new { code = "INVALID_PROVIDER_SETTINGS", message = exception.Message } }); }
    }

    private async Task<ActionResult?> RequireAdministratorAsync(CancellationToken cancellationToken)
    {
        var context = SessionAuthenticationMiddleware.GetContext(HttpContext);
        if (context is null)
        {
            return Unauthorized(new { error = new { code = "AUTHENTICATION_REQUIRED", message = "请先登录。" } });
        }
        try
        {
            if (!await store.IsSystemAdministratorAsync(context.User.UserId, cancellationToken))
                return StatusCode(StatusCodes.Status403Forbidden,
                    new { error = new { code = "ADMIN_REQUIRED", message = "需要系统管理员权限。" } });
        }
        catch (CollectorStoreException exception) when (exception.StatusCode == 503)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = new { code = exception.Code, message = exception.Message } });
        }
        return null;
    }
}
