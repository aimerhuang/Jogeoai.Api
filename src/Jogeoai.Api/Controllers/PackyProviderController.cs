using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Providers.Abstractions;
using Jogeoai.Providers.Domestic;
using Microsoft.AspNetCore.Mvc;

namespace Jogeoai.Api.Controllers;

/// <summary>
/// 系统管理员管理经 PackyAPI 接入的千问（qwen → packy_qwen）、智谱清言（glm → packy_glm）、Kimi（kimi → packy_kimi）与元宝（yuanbao → packy_yuanbao）连接设置并执行诊断请求；
/// 响应从不包含凭据。PackyAPI 的 key 按分组发放、不能互用，因此每个渠道单独保存。依赖迁移 025（表）与 036 / 046（默认行）。
/// 千问 / 智谱清言的来源是全部检索结果（只有 URL）；Kimi 的来源是一轮搜索的检索结果（带标题）；元宝渠道不返回检索结果，只有模型打开过的网页。
/// </summary>
[ApiController]
[Route("api/v1/providers/packy/{channel}")]
public sealed class PackyProviderController(
    IAnswerProviderSettingsStore settingsStore,
    IPackyAnswerClient client,
    IResearchStore adminStore) : ControllerBase
{
    private const int MaximumPromptLength = 10_000;

    /// <summary>返回一个 PackyAPI 渠道的设置（地址、模型、启用状态、超时、选项与凭据提示），不包含凭据；没有设置行时返回默认值。仅限系统管理员。</summary>
    /// <param name="channel">渠道：qwen（千问）、glm（智谱清言）、kimi 或 yuanbao（元宝）。</param>
    /// <param name="cancellationToken">取消数据库读取的令牌。</param>
    [HttpGet("settings")]
    [ProducesResponseType(typeof(AnswerProviderSettingsView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<AnswerProviderSettingsView>> GetSettings(string channel, CancellationToken cancellationToken)
    {
        if (await SystemAdministratorAccess.RequireAsync(this, adminStore, cancellationToken) is { } failure) return failure;
        if (ResolveProvider(channel) is not { } provider) return ChannelNotFound();
        try { return Ok(await settingsStore.GetAsync(provider, cancellationToken) ?? DefaultView(provider)); }
        catch (CollectorStoreException exception) { return StoreError(exception); }
    }

    /// <summary>
    /// 保存一个 PackyAPI 渠道的设置；凭据为空时保留已保存的凭据。选项只接受 requestMode=web_search、空 sources、maxToolCalls 1–10
    /// （kimi 必须为 1）、thinking=disabled/enabled（省略时为 disabled）与空 reasoningEffort；glm 另可设 reasoningEffort=minimal/low/medium/high，
    /// 设置后代替 thinking 下发（默认 low，对应网页端“快速”）。timeoutSeconds 不应超过 Worker 租约
    /// Packy:Worker:LeaseSeconds（默认 600 秒）。仅限系统管理员。
    /// </summary>
    /// <param name="channel">渠道：qwen、glm、kimi 或 yuanbao。</param>
    /// <param name="request">地址、模型、可选凭据、启用开关、超时与选项。</param>
    /// <param name="cancellationToken">取消数据库写入的令牌。</param>
    [HttpPut("settings")]
    [ProducesResponseType(typeof(AnswerProviderSettingsView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<AnswerProviderSettingsView>> SaveSettings(
        string channel,
        [FromBody] SaveAnswerProviderSettingsRequest request,
        CancellationToken cancellationToken)
    {
        if (await SystemAdministratorAccess.RequireAsync(this, adminStore, cancellationToken) is { } failure) return failure;
        if (ResolveProvider(channel) is not { } provider) return ChannelNotFound();
        if (request is null) return StatusCode(StatusCodes.Status400BadRequest,
            new { error = new { code = AnswerProviderSettingsRules.InvalidSettings, message = "请求体不能为空。" } });
        try { return Ok(await settingsStore.SaveAsync(provider, request, cancellationToken)); }
        catch (CollectorStoreException exception) { return StoreError(exception); }
    }

    /// <summary>用已保存的设置经 PackyAPI 提交一个问题（开启联网搜索），返回规范化 Markdown 回答、搜索词、检索来源、模型版本与用量摘要；仅限系统管理员。</summary>
    /// <param name="channel">渠道：qwen、glm、kimi 或 yuanbao。</param>
    /// <param name="request">问题与可选区域语言。</param>
    /// <param name="cancellationToken">取消外部请求的令牌。</param>
    [HttpPost("test")]
    [ProducesResponseType(typeof(PackyTestResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(StatusCodes.Status504GatewayTimeout)]
    public async Task<ActionResult<PackyTestResult>> Test(
        string channel,
        [FromBody] PackyTestRequest request,
        CancellationToken cancellationToken)
    {
        if (await SystemAdministratorAccess.RequireAsync(this, adminStore, cancellationToken) is { } failure) return failure;
        if (ResolveProvider(channel) is not { } provider) return ChannelNotFound();
        var prompt = request?.Prompt?.Trim();
        if (string.IsNullOrEmpty(prompt) || prompt.Length > MaximumPromptLength)
            return StatusCode(StatusCodes.Status400BadRequest,
                new { error = new { code = "INVALID_TEST_REQUEST", message = $"prompt 必须为 1–{MaximumPromptLength} 个字符。" } });
        var definition = AnswerProviderDefinition.TryGet(provider)!;
        var locale = string.IsNullOrWhiteSpace(request!.Locale) ? definition.DefaultLocale : request.Locale.Trim();
        if (locale.Length > 35 || locale.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            return StatusCode(StatusCodes.Status400BadRequest,
                new { error = new { code = "INVALID_TEST_REQUEST", message = "locale 必须为 BCP-47 区域语言标识。" } });
        try
        {
            var answer = await client.AskAsync(new PackyAnswerRequest(provider, prompt, locale, definition.DefaultCountry), cancellationToken);
            return Ok(new PackyTestResult(answer.Provider, answer.Engine, answer.ModelVersion,
                answer.ResultQuality?.ModelVersionProvided ?? false, answer.AnswerText, answer.AnswerPresent, answer.Citations,
                answer.WebQueries, answer.QueryAvailability, answer.ResultQuality, PackyUsageSummary.FromRawOutput(answer.RawOutput),
                answer.CapturedAt));
        }
        catch (CollectorStoreException exception) { return StoreError(exception); }
        catch (PackyProviderException exception)
        {
            var status = exception.Code switch
            {
                PackyErrorCodes.NotConfigured or PackyErrorCodes.InsufficientBalance or PackyErrorCodes.RateLimited
                    => StatusCodes.Status503ServiceUnavailable,
                PackyErrorCodes.Timeout => StatusCodes.Status504GatewayTimeout,
                PackyErrorCodes.InvalidRequest => StatusCodes.Status400BadRequest,
                _ => StatusCodes.Status502BadGateway,
            };
            return StatusCode(status, new
            {
                error = new { code = exception.Code, message = exception.Message, retryable = exception.Retryable, deferMinutes = exception.DeferMinutes },
            });
        }
    }

    /// <summary>把路由中的渠道名映射为 Provider 标识；未知渠道返回空值。</summary>
    /// <param name="channel">qwen、glm、kimi 或 yuanbao（忽略大小写与首尾空白）。</param>
    public static string? ResolveProvider(string? channel) => channel?.Trim().ToLowerInvariant() switch
    {
        "qwen" => AnswerProviderDefinition.PackyQwen.Provider,
        "glm" => AnswerProviderDefinition.PackyGlm.Provider,
        "kimi" => AnswerProviderDefinition.PackyKimi.Provider,
        "yuanbao" => AnswerProviderDefinition.PackyYuanbao.Provider,
        _ => null,
    };

    private ObjectResult ChannelNotFound() => StatusCode(StatusCodes.Status404NotFound,
        new { error = new { code = AnswerProviderSettingsRules.ProviderNotSupported, message = "PackyAPI 渠道只支持 qwen、glm、kimi、yuanbao。" } });

    private ObjectResult StoreError(CollectorStoreException exception) => StatusCode(exception.StatusCode, StoreErrorResponse.Body(exception));

    // Shown when migration 025 is deployed but the 036 / 046 seed row is missing; nothing is written.
    private static AnswerProviderSettingsView DefaultView(string provider) => new(provider, PackyDefaults.ApiUrl(provider),
        PackyDefaults.Model(provider), false, PackyDefaults.Timeout(provider), PackyDefaults.Options(provider), null, false, null);
}

/// <summary>PackyAPI 诊断请求。</summary>
/// <param name="Prompt">提交的问题，1–10000 个字符。</param>
/// <param name="Locale">BCP-47 区域语言；省略时为 zh-CN。</param>
public sealed record PackyTestRequest(string Prompt, string? Locale = null);

/// <summary>PackyAPI 诊断结果：与采集入库同源的规范化回答，不包含凭据与原始响应。</summary>
/// <param name="Provider">packy_qwen、packy_glm、packy_kimi 或 packy_yuanbao。</param>
/// <param name="Engine">qwen、zhipu_qingyan、kimi 或 tencent_yuanbao。</param>
/// <param name="ModelVersion">PackyAPI 返回的模型；缺失时为配置的模型。</param>
/// <param name="ModelVersionProvided">模型版本是否由响应提供。</param>
/// <param name="TextContent">规范化后的 Markdown 回答正文（不含搜索过程中的过渡语或回显）；空正文表示未生成回答。</param>
/// <param name="AnswerPresent">回答是否非空。</param>
/// <param name="Citations">检索来源按出现顺序去重后的公开链接（地址、域名、标题、位置），最多 100 条；千问 / 智谱清言多数没有标题。</param>
/// <param name="WebQueries">搜索词，按分号拆分、忽略空串、去重保序。</param>
/// <param name="QueryAvailability">搜索词可用状态：available 有搜索词、empty 模型未触发搜索、unknown 触发了搜索但没有非空搜索词。</param>
/// <param name="ResultQuality">规范化质量（来源保留情况、模型版本、无回答原因）。</param>
/// <param name="Usage">usage 摘要（token 与搜索调用数）；响应未提供时为空。</param>
/// <param name="CapturedAt">回答捕获的 UTC 时间。</param>
public sealed record PackyTestResult(
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
    PackyUsageSummary? Usage,
    DateTimeOffset CapturedAt);
