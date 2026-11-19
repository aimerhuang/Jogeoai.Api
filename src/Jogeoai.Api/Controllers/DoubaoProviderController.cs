using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Providers.Abstractions;
using Jogeoai.Providers.Domestic;
using Microsoft.AspNetCore.Mvc;

namespace Jogeoai.Api.Controllers;

/// <summary>系统管理员管理豆包（火山方舟 Responses API，或 options.requestMode=messages 时的 Messages API）连接设置并执行诊断请求；响应从不包含凭据。依赖迁移 025。</summary>
[ApiController]
[Route("api/v1/providers/doubao")]
public sealed class DoubaoProviderController(
    IAnswerProviderSettingsStore settingsStore,
    IDoubaoAnswerClient client,
    IResearchStore adminStore) : ControllerBase
{
    private const int MaximumPromptLength = 10_000;
    private static readonly string Provider = AnswerProviderDefinition.DoubaoApi.Provider;

    /// <summary>返回豆包设置（地址、模型、启用状态、超时、选项与凭据提示），不包含凭据；仅限系统管理员。</summary>
    /// <param name="cancellationToken">取消数据库读取的令牌。</param>
    [HttpGet("settings")]
    [ProducesResponseType(typeof(AnswerProviderSettingsView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<AnswerProviderSettingsView>> GetSettings(CancellationToken cancellationToken)
    {
        if (await SystemAdministratorAccess.RequireAsync(this, adminStore, cancellationToken) is { } failure) return failure;
        try { return Ok(await settingsStore.GetAsync(Provider, cancellationToken) ?? DefaultView()); }
        catch (CollectorStoreException exception) { return StoreError(exception); }
    }

    /// <summary>保存豆包设置；凭据为空时保留已保存的凭据。仅限系统管理员。</summary>
    /// <param name="request">地址、模型、可选凭据、启用开关、超时与选项。</param>
    /// <param name="cancellationToken">取消数据库写入的令牌。</param>
    [HttpPut("settings")]
    [ProducesResponseType(typeof(AnswerProviderSettingsView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<AnswerProviderSettingsView>> SaveSettings(
        [FromBody] SaveAnswerProviderSettingsRequest request,
        CancellationToken cancellationToken)
    {
        if (await SystemAdministratorAccess.RequireAsync(this, adminStore, cancellationToken) is { } failure) return failure;
        if (request is null) return StatusCode(StatusCodes.Status400BadRequest,
            new { error = new { code = AnswerProviderSettingsRules.InvalidSettings, message = "请求体不能为空。" } });
        try { return Ok(await settingsStore.SaveAsync(Provider, request, cancellationToken)); }
        catch (CollectorStoreException exception) { return StoreError(exception); }
    }

    /// <summary>用已保存的设置向豆包提交一个问题，返回规范化回答、搜索词、引用、模型版本与用量摘要；仅限系统管理员。</summary>
    /// <param name="request">问题与可选区域语言。</param>
    /// <param name="cancellationToken">取消外部请求的令牌。</param>
    [HttpPost("test")]
    [ProducesResponseType(typeof(DoubaoTestResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(StatusCodes.Status504GatewayTimeout)]
    public async Task<ActionResult<DoubaoTestResult>> Test(
        [FromBody] DoubaoTestRequest request,
        CancellationToken cancellationToken)
    {
        if (await SystemAdministratorAccess.RequireAsync(this, adminStore, cancellationToken) is { } failure) return failure;
        var prompt = request?.Prompt?.Trim();
        if (string.IsNullOrEmpty(prompt) || prompt.Length > MaximumPromptLength)
            return StatusCode(StatusCodes.Status400BadRequest,
                new { error = new { code = "INVALID_TEST_REQUEST", message = $"prompt 必须为 1–{MaximumPromptLength} 个字符。" } });
        var locale = string.IsNullOrWhiteSpace(request!.Locale) ? AnswerProviderDefinition.DoubaoApi.DefaultLocale : request.Locale.Trim();
        if (locale.Length > 35 || locale.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            return StatusCode(StatusCodes.Status400BadRequest,
                new { error = new { code = "INVALID_TEST_REQUEST", message = "locale 必须为 BCP-47 区域语言标识。" } });
        try
        {
            var answer = await client.AskAsync(new DoubaoAnswerRequest(prompt, locale, AnswerProviderDefinition.DoubaoApi.DefaultCountry), cancellationToken);
            return Ok(new DoubaoTestResult(answer.Provider, answer.Engine, answer.ModelVersion,
                answer.ResultQuality?.ModelVersionProvided ?? false, answer.AnswerText, answer.AnswerPresent, answer.Citations,
                answer.WebQueries, answer.QueryAvailability, answer.ResultQuality, DoubaoUsageSummary.FromRawOutput(answer.RawOutput),
                answer.CapturedAt));
        }
        catch (CollectorStoreException exception) { return StoreError(exception); }
        catch (DoubaoProviderException exception)
        {
            var status = exception.Code switch
            {
                DoubaoErrorCodes.NotConfigured or DoubaoErrorCodes.QuotaExceeded or DoubaoErrorCodes.RateLimited => StatusCodes.Status503ServiceUnavailable,
                DoubaoErrorCodes.Timeout => StatusCodes.Status504GatewayTimeout,
                DoubaoErrorCodes.InputFiltered or DoubaoErrorCodes.InvalidRequest => StatusCodes.Status400BadRequest,
                _ => StatusCodes.Status502BadGateway,
            };
            return StatusCode(status, new
            {
                error = new { code = exception.Code, message = exception.Message, retryable = exception.Retryable, deferMinutes = exception.DeferMinutes },
            });
        }
    }

    private ObjectResult StoreError(CollectorStoreException exception) => StatusCode(exception.StatusCode, StoreErrorResponse.Body(exception));

    // Shown when migration 025 is deployed but the seed row was removed; nothing is written.
    private static AnswerProviderSettingsView DefaultView() => new(Provider, DoubaoDefaults.ApiUrl, DoubaoDefaults.Model, false,
        DoubaoDefaults.TimeoutSeconds, new AnswerProviderOptions(), null, false, null);
}

/// <summary>豆包诊断请求。</summary>
/// <param name="Prompt">提交给豆包的问题，1–10000 个字符。</param>
/// <param name="Locale">BCP-47 区域语言；省略时为 zh-CN。</param>
public sealed record DoubaoTestRequest(string Prompt, string? Locale = null);

/// <summary>豆包诊断结果：与采集入库同源的规范化回答，不包含凭据与原始响应。</summary>
/// <param name="Provider">固定为 doubao_api。</param>
/// <param name="Engine">固定为 doubao。</param>
/// <param name="ModelVersion">方舟返回的模型版本；缺失时为配置的模型。</param>
/// <param name="ModelVersionProvided">模型版本是否由方舟响应提供。</param>
/// <param name="TextContent">回答正文；空正文表示未生成回答或被输出审核移除。</param>
/// <param name="AnswerPresent">回答是否非空。</param>
/// <param name="Citations">按出现顺序去重后的公开引用（地址、域名、标题、位置）。</param>
/// <param name="WebQueries">web_search_call 使用的搜索词，去重保序。</param>
/// <param name="QueryAvailability">搜索词可用状态：available 有搜索词、empty 模型未触发搜索、unknown 触发了搜索但未暴露搜索词。</param>
/// <param name="ResultQuality">规范化质量（引用保留情况、模型版本、无回答原因）。</param>
/// <param name="Usage">方舟 usage 摘要（token 与搜索次数）；响应未提供时为空。</param>
/// <param name="CapturedAt">回答捕获的 UTC 时间。</param>
public sealed record DoubaoTestResult(
    string Provider,
    string Engine,
    string ModelVersion,
    bool ModelVersionProvided,
    string TextContent,
    bool AnswerPresent,
    IReadOnlyList<CloroCitation> Citations,
    IReadOnlyList<string> WebQueries,
    string QueryAvailability,
    CloroResultQuality? ResultQuality,
    DoubaoUsageSummary? Usage,
    DateTimeOffset CapturedAt);
