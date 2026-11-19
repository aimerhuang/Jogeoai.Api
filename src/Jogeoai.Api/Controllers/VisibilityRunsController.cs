using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jogeoai.Api;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Providers.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace Jogeoai.Api.Controllers;

/// <summary>创建和查询可见性运行。</summary>
[ApiController]
[Route("api/v1/visibility-runs")]
public sealed class VisibilityRunsController(
    IVisibilityCollectorStore store,
    ICloroVisibilityStore cloroStore,
    ICloroSettingsStore cloroSettingsStore,
    ICloroKeyPool cloroKeyPool,
    IAnswerProviderSettingsStore providerSettings,
    IManagementStore managementStore,
    IVisibilityRunQueryStore runQueryStore,
    TenantContextResolver tenantContext,
    IAppCollectionStore appStore) : ControllerBase
{
    /// <summary>由有写权限的成员创建一次国内 Collector 或 Cloro 可见性运行。</summary>
    /// <param name="request">站点、Prompt 和平台入口。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpPost]
    [ProducesResponseType(typeof(VisibilityRunCreated), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<VisibilityRunCreated>> Create(
        [FromBody] CreateVisibilityRunRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryTenant(out var tenantId, out var tenantFailure, forWrite: true)) return tenantFailure!;
        var key = Request.Headers["Idempotency-Key"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(key))
            return BadRequest(new { error = new { code = "IDEMPOTENCY_KEY_REQUIRED", message = "Idempotency-Key is required." } });
        var httpRequestHash = HashRequest(request);
        try
        {
            var replay = await ReplayAsync(tenantId, key, httpRequestHash, request, cancellationToken);
            if (replay is not null)
                return AcceptedAtAction(nameof(Get), new { runId = replay.RunId }, replay);
            if (request.PreviewId is not null || request.TargetIds is { Count: > 0 } || request.TagIds is { Count: > 0 })
            {
                if (request.Targets is { Count: > 0 } || request.Platforms is { Count: > 0 })
                    return BadRequest(new { error = new { code = "TARGET_SELECTION_CONFLICT", message = "预览/站点 Target 与旧 targets/platforms 不能混用。" } });
                if (request.PreviewId is not null && (request.PromptIds is { Count: > 0 } || request.TagIds is { Count: > 0 }
                    || request.TargetIds is { Count: > 0 } || request.SampleCount is not null))
                    return BadRequest(new { error = new { code = "PREVIEW_SELECTION_CONFLICT", message = "提供 previewId 时不要重复指定 Prompt、Target 或 sampleCount。" } });
                var resolved = await managementStore.ResolveRunAsync(tenantId, request.SiteId,
                    new CollectionSelection(request.PromptIds, request.TagIds, request.TargetIds, request.SampleCount ?? 1),
                    request.PreviewId, cancellationToken);
                if (await ProviderUnavailableAsync(resolved.Targets, cancellationToken) is { } unavailable) return unavailable;
                var planned = await cloroStore.CreateRunAsync(new CreateCloroVisibilityRunCommand(
                    tenantId, request.SiteId, resolved.PromptIds, resolved.Targets, key, request.RequestedBy,
                    resolved.SampleCount, request.PreviewId, RequireEnabledTargets: true, HttpRequestHash: httpRequestHash), cancellationToken);
                return AcceptedAtAction(nameof(Get), new { runId = planned.RunId }, planned);
            }
            if (request.Targets is { Count: > 0 })
            {
                if (request.Platforms is { Count: > 0 })
                    return BadRequest(new { error = new { code = "TARGETS_PLATFORMS_MUTUALLY_EXCLUSIVE", message = "targets and platforms cannot be used together." } });
                var targets = request.Targets.Select(ToTarget).ToArray();
                // App targets are only expanded by the weekly App plan; naming one in a manual run is refused, not ignored.
                if (targets.Any(target => MonitoringProviders.IsApp(target.Provider))) return AppTargetNotAllowed();
                if (await ProviderUnavailableAsync(targets, cancellationToken) is { } unavailable) return unavailable;
                var cloroResult = await cloroStore.CreateRunAsync(new CreateCloroVisibilityRunCommand(
                    tenantId, request.SiteId, request.PromptIds ?? [], targets, key, request.RequestedBy,
                    request.SampleCount ?? 1, HttpRequestHash: httpRequestHash), cancellationToken);
                return AcceptedAtAction(nameof(Get), new { runId = cloroResult.RunId }, cloroResult);
            }
            if ((request.Platforms ?? []).Any(platform => string.Equals(platform?.Channel?.Trim(), AppChannelDefinition.CollectorChannel,
                    StringComparison.OrdinalIgnoreCase)))
                return AppTargetNotAllowed();
            if ((request.SampleCount ?? 1) != 1)
                return BadRequest(new { error = new { code = "SAMPLE_COUNT_UNSUPPORTED", message = "国内 Collector 兼容请求仅支持单样本。" } });
            var result = await store.CreateRunAsync(new CreateVisibilityRunCommand(
                tenantId, request.SiteId, request.PromptIds ?? [], (request.Platforms ?? [])
                    .Select(p => new CollectorPlatformSelection(p.PlatformCode, p.EntryCode, p.Language, p.Channel)).ToArray(),
                key, request.RequestedBy, httpRequestHash), cancellationToken);
            return AcceptedAtAction(nameof(Get), new { runId = result.RunId }, result);
        }
        catch (CollectorStoreException exception)
        {
            // A concurrent creator may have committed while this request was
            // resolving mutable Prompt/Target or Provider state.
            var committed = await runQueryStore.FindReplayAsync(tenantId, key, cancellationToken);
            if (committed?.HttpRequestHash is not null)
            {
                if (committed.HttpRequestHash != httpRequestHash)
                    return StatusCode(409, new { error = new { code = "RUN_IDEMPOTENCY_CONFLICT", message = "幂等键已用于不同的运行请求。" } });
                return AcceptedAtAction(nameof(Get), new { runId = committed.Run.RunId }, committed.Run);
            }
            return StatusCode(exception.StatusCode, new { error = new { code = exception.Code, message = exception.Message } });
        }
    }

    private async Task<VisibilityRunCreated?> ReplayAsync(Guid tenantId, string key, string httpRequestHash,
        CreateVisibilityRunRequest request, CancellationToken cancellationToken)
    {
        var existing = await runQueryStore.FindReplayAsync(tenantId, key, cancellationToken);
        if (existing is null) return null;
        if (existing.HttpRequestHash is not null)
        {
            if (existing.HttpRequestHash != httpRequestHash) throw ReplayConflict();
            return existing.Run;
        }
        // Rows written before http_request_hash was introduced keep their
        // original Provider canonical hash. Preview-only rows can be compared
        // using their persisted preview ID without reading an expired preview.
        if (request.PreviewId is not null && request.SiteId == existing.SiteId
            && request.PreviewId == existing.PreviewId && request.RequestedBy == existing.RequestedBy
            && request.PromptIds is not { Count: > 0 } && request.TagIds is not { Count: > 0 }
            && request.TargetIds is not { Count: > 0 } && request.SampleCount is null
            && request.Targets is not { Count: > 0 } && request.Platforms is not { Count: > 0 })
            return existing.Run;
        if (request.PreviewId is null && request.TargetIds is not { Count: > 0 } && request.TagIds is not { Count: > 0 })
        {
            if (request.Targets is { Count: > 0 } && request.Platforms is not { Count: > 0 })
            {
                var command = new CreateCloroVisibilityRunCommand(tenantId, request.SiteId, request.PromptIds ?? [],
                    request.Targets.Select(ToTarget).ToArray(), key, request.RequestedBy,
                    request.SampleCount ?? 1);
                if (await cloroStore.GetExistingRunByKeyAsync(command, cancellationToken) == existing.Run.RunId)
                    return existing.Run;
            }
            else if (request.Targets is not { Count: > 0 } && (request.SampleCount ?? 1) == 1)
            {
                var command = new CreateVisibilityRunCommand(tenantId, request.SiteId, request.PromptIds ?? [],
                    (request.Platforms ?? []).Select(p => new CollectorPlatformSelection(p.PlatformCode, p.EntryCode,
                        p.Language, p.Channel)).ToArray(), key, request.RequestedBy);
                if (await store.GetExistingRunByKeyAsync(command, cancellationToken) == existing.Run.RunId)
                    return existing.Run;
            }
        }
        throw ReplayConflict();
    }

    private BadRequestObjectResult AppTargetNotAllowed() => BadRequest(new
    {
        error = new { code = AppChannelDefinition.TargetNotAllowedCode, message = AppChannelDefinition.TargetNotAllowedMessage },
    });

    private static CloroVisibilityTarget ToTarget(CloroTargetRequest? target) => new(target?.Provider ?? string.Empty,
        target?.Engine ?? string.Empty, target?.Locale, target?.Country, target?.Options?.GetRawText(),
        target?.AnswerMode is { } mode ? Enum.IsDefined(mode) ? AnswerModeRules.ToStorage(mode) : mode.ToString() : null);

    private static CollectorStoreException ReplayConflict() => new("RUN_IDEMPOTENCY_CONFLICT", "幂等键已用于不同的运行请求。", 409);

    private static string HashRequest(CreateVisibilityRunRequest request)
    {
        var payload = JsonSerializer.Serialize(new
        {
            request.SiteId,
            PromptIds = (request.PromptIds ?? []).Order().ToArray(),
            TagIds = (request.TagIds ?? []).Order().ToArray(),
            TargetIds = (request.TargetIds ?? []).Order().ToArray(),
            Platforms = (request.Platforms ?? []).Select(p => new { p.PlatformCode, p.EntryCode, p.Language, p.Channel })
                .OrderBy(p => p.PlatformCode, StringComparer.Ordinal).ThenBy(p => p.EntryCode, StringComparer.Ordinal).ToArray(),
            // A requested answer mode is appended to the engine segment only when present, so requests without it keep
            // their historical HTTP request hash.
            Targets = (request.Targets ?? []).Select(t => new { Provider = t?.Provider?.Trim().ToLowerInvariant(),
                Engine = t?.AnswerMode is { } mode ? $"{t.Engine?.Trim().ToLowerInvariant()}|{mode.ToString().ToLowerInvariant()}" : t?.Engine?.Trim().ToLowerInvariant(),
                Country = t?.Country?.Trim().ToUpperInvariant() ?? "US",
                Locale = t?.Locale?.Trim().ToLowerInvariant(), Options = NormalizeOptions(t?.Options) })
                .OrderBy(t => t.Provider, StringComparer.Ordinal).ThenBy(t => t.Engine, StringComparer.Ordinal)
                .ThenBy(t => t.Country, StringComparer.Ordinal).ThenBy(t => t.Locale, StringComparer.Ordinal).ToArray(),
            SampleCount = request.SampleCount ?? 1,
            request.PreviewId,
            request.RequestedBy,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private static string NormalizeOptions(JsonElement? options)
    {
        if (options is null || options.Value.ValueKind == JsonValueKind.Object && !options.Value.EnumerateObject().Any())
            return "{}";
        return options.Value.GetRawText();
    }

    /// <summary>查询运行概要。</summary>
    /// <param name="runId">运行标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpGet("{runId:guid}")]
    [ProducesResponseType(typeof(VisibilityRunSummary), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<VisibilityRunSummary>> Get(Guid runId, CancellationToken cancellationToken)
    {
        if (!TryTenant(out var tenantId, out var tenantFailure)) return tenantFailure!;
        var provider = await cloroStore.GetRunProviderAsync(tenantId, runId, cancellationToken);
        // Runs of any registered API answer provider (cloro, doubao_api, deepseek_api) live in the shared CloroVisibilityStore.
        // An App cycle run (app_collector) is read by the second branch: that summary reads visibility_runs by tenant and id
        // only, whichever store created the run.
        var result = AnswerProviderDefinition.IsRegistered(provider)
            ? await cloroStore.GetRunAsync(tenantId, runId, cancellationToken)
            : await store.GetRunAsync(tenantId, runId, cancellationToken);
        if (result is null) return NotFound();
        // Only an App cycle run can have checks that were not collected (cancelled); they count as finished collection.
        var cancelled = MonitoringProviders.IsApp(provider)
            ? (await appStore.CountNotCollectedAsync(tenantId, [runId], cancellationToken)).GetValueOrDefault(runId)
            : 0;
        return Ok(result with { Provider = provider, CancelledChecks = cancelled });
    }

    /// <summary>按站点、状态、Provider 和创建时间稳定分页查询运行历史。</summary>
    /// <param name="siteId">品牌工作区标识。</param>
    /// <param name="status">可选运行状态。</param>
    /// <param name="provider">可选 Provider。</param>
    /// <param name="from">可选创建时间下界，含端点。</param>
    /// <param name="to">可选创建时间上界，含端点。</param>
    /// <param name="cursor">上页返回的筛选绑定游标。</param>
    /// <param name="limit">每页数量，范围 1 到 100。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpGet("/api/v1/sites/{siteId:guid}/visibility-runs")]
    [ProducesResponseType(typeof(CursorPage<VisibilityRunSummary>), StatusCodes.Status200OK)]
    public async Task<ActionResult<CursorPage<VisibilityRunSummary>>> ListSiteRuns(Guid siteId, [FromQuery] string? status,
        [FromQuery] string? provider, [FromQuery(Name = "from")] DateTimeOffset? from,
        [FromQuery(Name = "to")] DateTimeOffset? to, [FromQuery] string? cursor, [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (!TryTenant(out var tenantId, out var tenantFailure)) return tenantFailure!;
        try { return Ok(await runQueryStore.ListSiteRunsAsync(tenantId, siteId, status, provider, from, to, cursor, limit, cancellationToken)); }
        catch (CollectorStoreException exception)
        { return StatusCode(exception.StatusCode, new { error = new { code = exception.Code, message = exception.Message } }); }
    }

    /// <summary>分页查询运行中的回答和引用。</summary>
    /// <param name="runId">运行标识。</param>
    /// <param name="cursor">上页返回的不透明游标。</param>
    /// <param name="limit">每页数量，范围 1 到 100。</param>
    /// <param name="provider">可选的 Provider 过滤；API 回答 Provider 运行只接受其自身 Provider（cloro、doubao_api、deepseek_api、packy_qwen、packy_glm、packy_kimi 或 packy_yuanbao），App 周期运行只接受 app_collector。</param>
    /// <param name="status">可选的检查状态；App 周期运行中未采集的检查为 cancelled，错误码是未采集原因码。</param>
    /// <param name="engine">可选的引擎（Cloro 引擎、doubao、deepseek、qwen、zhipu_qingyan、kimi 或 tencent_yuanbao）；App 周期运行按平台代码筛选。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpGet("{runId:guid}/checks")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> Checks(
        Guid runId, [FromQuery] string? cursor, [FromQuery] int limit = 50, [FromQuery] string? provider = null, [FromQuery] string? status = null, [FromQuery] string? engine = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryTenant(out var tenantId, out var tenantFailure)) return tenantFailure!;
        try
        {
            var runProvider = await cloroStore.GetRunProviderAsync(tenantId, runId, cancellationToken);
            if (AnswerProviderDefinition.IsRegistered(runProvider))
            {
                if (!string.IsNullOrWhiteSpace(provider) && !string.Equals(provider.Trim(), runProvider, StringComparison.OrdinalIgnoreCase))
                    return Ok(new CursorPage<CloroVisibilityCheckView>([], null));
                return Ok(await cloroStore.GetChecksAsync(tenantId, runId, cursor, limit, engine, status, cancellationToken));
            }
            // An App cycle run is neither an API run nor a domestic web run: same check view as an API run, read by the
            // App store (no collector node, binding or account details).
            if (MonitoringProviders.IsApp(runProvider))
            {
                if (!string.IsNullOrWhiteSpace(provider) && !MonitoringProviders.IsApp(provider))
                    return Ok(new CursorPage<CloroVisibilityCheckView>([], null));
                return Ok(await appStore.GetChecksAsync(tenantId, runId, cursor, limit, engine, status, cancellationToken));
            }
            // Resolve existence before applying a provider filter. Otherwise a
            // missing run queried with provider=domestic_collector would look
            // like an empty, successful page instead of a tenant-scoped 404.
            if (await store.GetRunAsync(tenantId, runId, cancellationToken) is null)
                return NotFound();
            if (provider is not null && !string.Equals(provider, "domestic_collector", StringComparison.OrdinalIgnoreCase))
                return Ok(new CursorPage<VisibilityCheckView>([], null));
            if (engine is not null)
                return BadRequest(new { error = new { code = "ENGINE_FILTER_NOT_SUPPORTED", message = "engine filter is only available for Cloro runs." } });
            return Ok(await store.GetChecksAsync(tenantId, runId, cursor, limit, status, cancellationToken));
        }
        catch (CollectorStoreException exception)
        {
            return StatusCode(exception.StatusCode, new { error = new { code = exception.Code, message = exception.Message } });
        }
    }

    // Availability is checked per provider present in the target set: Cloro keeps its settings + key pool rule,
    // every settings-backed provider (NotConfiguredCode set: doubao_api, deepseek_api) requires an enabled
    // answer_provider_settings row with a credential (503 DOUBAO_NOT_CONFIGURED / DEEPSEEK_NOT_CONFIGURED), checked in registry order.
    // Unregistered providers are left to the store, which rejects them with 400 TARGET_NOT_ALLOWED.
    private async Task<ActionResult?> ProviderUnavailableAsync(IReadOnlyList<CloroVisibilityTarget> targets, CancellationToken cancellationToken)
    {
        var providers = targets.Select(t => AnswerProviderDefinition.TryGet(t.Provider)?.Provider).Where(p => p is not null).Distinct().ToArray();
        if (providers.Contains(AnswerProviderDefinition.Cloro.Provider)
            && await CloroUnavailableAsync(cancellationToken) is { } cloroUnavailable) return cloroUnavailable;
        foreach (var definition in AnswerProviderDefinition.All.Where(d => d.NotConfiguredCode is not null && providers.Contains(d.Provider)))
        {
            if (!await IsApiProviderConfiguredAsync(definition, cancellationToken))
                return StatusCode(StatusCodes.Status503ServiceUnavailable,
                    new { error = new { code = definition.NotConfiguredCode, message = $"{definition.DisplayName}服务尚未配置或未启用。" } });
        }
        return null;
    }

    private async Task<bool> IsApiProviderConfiguredAsync(AnswerProviderDefinition definition, CancellationToken cancellationToken)
    {
        try { return await providerSettings.GetConfigurationAsync(definition.Provider, cancellationToken) is not null; }
        catch (CollectorStoreException exception) when (exception.StatusCode == StatusCodes.Status503ServiceUnavailable) { return false; }
    }

    // Global settings must be enabled with a valid URL and the pool must hold an enabled key (else
    // CLORO_NOT_CONFIGURED); keys that are all out of credits or invalid give CLORO_KEY_POOL_EXHAUSTED.
    // A pool that is only cooling down after 429s accepts the Run: its tasks wait for the cooldown.
    private async Task<ActionResult?> CloroUnavailableAsync(CancellationToken cancellationToken)
    {
        var settings = await cloroSettingsStore.GetAsync(cancellationToken);
        var availability = IsCloroEndpointConfigured(settings) ? await cloroKeyPool.GetAvailabilityAsync(cancellationToken) : null;
        if (availability is not { EnabledKeys: > 0 })
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = new { code = "CLORO_NOT_CONFIGURED", message = "Cloro 未配置、未启用或配置无效。" } });
        if (availability.CreditsExhausted)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = new
                {
                    code = "CLORO_KEY_POOL_EXHAUSTED",
                    message = "Cloro 积分池已耗尽，请等待账号恢复或联系管理员补充账号。",
                    nextAvailableAt = availability.NextAvailableAt,
                },
            });
        return null;
    }

    private static bool IsCloroEndpointConfigured(CloroConnectionSettings? settings)
    {
        if (settings is null || !settings.Enabled
            || !Uri.TryCreate(settings.ApiUrl, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment)) return false;
        return true;
    }

    private bool TryTenant(out Guid tenantId, out ActionResult? failure, bool forWrite = false)
    {
        tenantId = Guid.Empty;
        try
        {
            tenantId = tenantContext.Resolve(HttpContext);
            if (forWrite)
            {
                var identity = SessionAuthenticationMiddleware.GetContext(HttpContext);
                var tenant = tenantId;
                var membership = identity?.User.Memberships.FirstOrDefault(item => item.TenantId == tenant && item.Status == "active");
                if (membership?.Role.Equals("viewer", StringComparison.OrdinalIgnoreCase) == true)
                    throw new CollectorStoreException("CONTENT_FORBIDDEN", "当前角色无权创建采集运行。", 403);
            }
            failure = null;
            return true;
        }
        catch (CollectorStoreException exception)
        {
            failure = StatusCode(exception.StatusCode,
                new { error = new { code = exception.Code, message = exception.Message } });
            return false;
        }
    }
}

/// <summary>创建运行请求。</summary>
/// <param name="SiteId">目标站点标识。</param>
/// <param name="PromptIds">需要采集的 Prompt 标识。</param>
/// <param name="Platforms">兼容的国内 Collector 平台入口。</param>
/// <param name="RequestedBy">发起人标识。</param>
/// <param name="Targets">API 回答 Provider（cloro、doubao_api、deepseek_api）采集目标。</param>
/// <param name="TagIds">按标签解析的 Prompt 集合。</param>
/// <param name="TargetIds">站点已启用 Target 标识。</param>
/// <param name="SampleCount">每个 Prompt 和 Target 的回答样本数，范围 1–10。</param>
/// <param name="PreviewId">可选的固定采集预览标识。</param>
public sealed record CreateVisibilityRunRequest(
    Guid SiteId,
    IReadOnlyList<Guid>? PromptIds,
    IReadOnlyList<PlatformSelectionRequest>? Platforms,
    string? RequestedBy = null,
    IReadOnlyList<CloroTargetRequest>? Targets = null,
    IReadOnlyList<Guid>? TagIds = null,
    IReadOnlyList<Guid>? TargetIds = null,
    int? SampleCount = null,
    Guid? PreviewId = null);

/// <summary>平台入口请求。</summary>
/// <param name="PlatformCode">国内平台代码。</param>
/// <param name="EntryCode">平台入口代码。</param>
/// <param name="Language">回答语言。</param>
/// <param name="Channel">采集渠道。</param>
public sealed record PlatformSelectionRequest(
    string PlatformCode,
    string EntryCode,
    string Language = "zh-CN",
    string Channel = "consumer_web");

/// <summary>API 回答 Provider 目标请求。</summary>
/// <param name="Provider">Provider：cloro、doubao_api 或 deepseek_api。</param>
/// <param name="Engine">引擎代码：Cloro 引擎、doubao 或 deepseek。</param>
/// <param name="Locale">可选的 BCP-47 区域语言。</param>
/// <param name="Country">可选的 ISO 国家代码。</param>
/// <param name="Options">可选的脱敏 Provider 选项。</param>
/// <param name="AnswerMode">可选的回答模式（快速回答 / 深度思考）；省略为 Provider 默认模式。必须是采集目标目录中该 Provider 支持的模式，否则返回 400 ANSWER_MODE_UNSUPPORTED。</param>
public sealed record CloroTargetRequest(
    string Provider,
    string Engine,
    string? Locale = null,
    string? Country = null,
    System.Text.Json.JsonElement? Options = null,
    AnswerMode? AnswerMode = null);
