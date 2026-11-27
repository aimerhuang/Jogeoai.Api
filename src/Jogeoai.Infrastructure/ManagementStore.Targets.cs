using System.Globalization;
using System.Text.Json;
using Jogeoai.Application;
using Jogeoai.Domain;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

public sealed partial class ManagementStore
{
    private static readonly IReadOnlyList<AnswerMode> FastOnly = [AnswerMode.Fast];

    /// <summary>
    /// 站点目标经规范化后的写入形态：<see cref="StoredMode"/> 为 site_targets.answer_mode（默认模式折叠为 NULL），
    /// <see cref="Key"/> 为含模式段的目标键，<see cref="Eligible"/> 表示可参与每日监控（已注册的 API 回答 Provider）。
    /// <see cref="App"/> 表示 App 目标（app_collector）：它参与每周的 App 计划，但 <see cref="Eligible"/> 为 false，
    /// 因此不占每轮 20 个目标的名额，也不进入每日监控的预估、主计划和凌晨计划。
    /// </summary>
    internal sealed record NormalizedSiteTarget(SiteTargetWrite Write, string? StoredMode, string Key, bool Eligible, bool App = false);

    /// <summary>Provider 的回答模式能力；未注册 Provider（国内网页采集）没有能力表，按只支持 fast 处理。</summary>
    internal static (IReadOnlyList<AnswerMode> Supported, AnswerMode Default) AnswerModeCapabilities(string provider)
        => AnswerProviderDefinition.TryGet(provider) is { } definition
            ? (definition.SupportedAnswerModes, definition.DefaultAnswerMode)
            : (FastOnly, AnswerMode.Fast);

    /// <summary>站点目标的纯函数校验与规范化（不访问数据库）：Provider / 引擎 / 地区、回答模式与采集频率。</summary>
    internal static NormalizedSiteTarget NormalizeTarget(SiteTargetWrite target)
    {
        if (target is null) throw Error("INVALID_TARGETS", "目标不能为空。", 400);
        var provider = target.Provider?.Trim().ToLowerInvariant() ?? "";
        var engine = target.Engine?.Trim().ToLowerInvariant() ?? "";
        var capture = target.CaptureMode?.Trim().ToLowerInvariant() ?? "";
        var country = ValidateCountry(target.Country);
        var locale = ValidateLocale(target.Locale);
        var definition = AnswerProviderDefinition.TryGet(provider);
        if (definition is not null)
        {
            if (!definition.SupportsEngine(engine) || capture != definition.CaptureMode)
                throw Error("TARGET_NOT_ALLOWED", definition.Provider == "cloro"
                    ? "Cloro 目标只支持目录中的引擎与 scraped 模式。"
                    : $"{definition.DisplayName}目标只支持 {string.Join(" / ", definition.Engines)} 引擎与 {definition.CaptureMode} 模式。", 400);
            // Pinned providers (doubao_api, deepseek_api) only answer for the Chinese consumer surface;
            // other API providers keep their free country/locale choice.
            if (definition.PinnedSurface
                && (country != definition.DefaultCountry || !string.Equals(locale, definition.DefaultLocale, StringComparison.Ordinal)))
                throw Error("TARGET_NOT_ALLOWED", $"{definition.DisplayName}目标仅支持 {definition.DefaultCountry}/{definition.DefaultLocale}。", 400);
        }
        else if (provider == "domestic_collector")
        {
            if (capture != "consumer_web") throw Error("TARGET_NOT_ALLOWED", "国内目标只支持 consumer_web 模式。", 400);
        }
        else if (MonitoringProviders.IsApp(provider)) return NormalizeAppTarget(target, engine, capture, country, locale);
        else throw Error("TARGET_NOT_ALLOWED", "Provider 不受支持。", 400);
        if (!Enum.IsDefined(target.Frequency))
            throw Error(MonitoringFrequencyRules.FrequencyInvalid, "frequency 只支持 twice_daily、daily、every_other_day 或 weekly。", 400);
        var (supported, defaultMode) = AnswerModeCapabilities(provider);
        if (target.AnswerMode is { } mode && (!Enum.IsDefined(mode) || !supported.Contains(mode)))
            throw new CollectorStoreException(AnswerModeRules.AnswerModeUnsupported,
                $"{definition?.DisplayName ?? provider} 不支持回答模式 {(Enum.IsDefined(mode) ? AnswerModeRules.ToStorage(mode) : mode.ToString())}。", 400);
        // The default mode is stored as NULL so the key stays byte-identical to the pre-047 key.
        var stored = target.AnswerMode is { } explicitMode && explicitMode != defaultMode ? AnswerModeRules.ToStorage(explicitMode) : null;
        var write = new SiteTargetWrite(provider, engine, capture, country, locale, target.Enabled,
            stored is null ? null : target.AnswerMode, target.Frequency);
        return new NormalizedSiteTarget(write, stored, MonitoringTargetKey(provider, engine, capture, country, locale, stored),
            definition is not null);
    }

    /// <summary>
    /// App 目标（app_collector）的校验与规范化（design 3.4）：除平台外全部取值固定。平台不是已知的 App 平台、采集模式不是 app、
    /// 国家 / 语言不是 CN / zh-CN → 400 TARGET_NOT_ALLOWED；回答模式不是 fast → 400 ANSWER_MODE_UNSUPPORTED；频率不是 weekly →
    /// 400 FREQUENCY_INVALID。频率的省略与显式的 daily 无法区分（<see cref="SiteTargetWrite.Frequency"/> 的默认值就是 daily），
    /// 为了不把显式的非 weekly 频率静默改写成 weekly，两者都拒绝：调用方必须显式传 weekly。平台是否可用需要目录，见
    /// <see cref="EnsureAppTargetsAllowed"/>。
    /// </summary>
    private static NormalizedSiteTarget NormalizeAppTarget(SiteTargetWrite target, string engine, string capture, string country, string locale)
    {
        if (!AppChannelDefinition.SupportsEngine(engine) || capture != AppChannelDefinition.CaptureMode
            || country != AppChannelDefinition.Country || !string.Equals(locale, AppChannelDefinition.Locale, StringComparison.Ordinal))
            throw Error("TARGET_NOT_ALLOWED",
                $"App 目标只支持 {string.Join(" / ", AppChannelDefinition.Engines)} 平台、{AppChannelDefinition.CaptureMode} 模式与 {AppChannelDefinition.Country}/{AppChannelDefinition.Locale}。", 400);
        if (target.AnswerMode is { } mode && (!Enum.IsDefined(mode) || !AppChannelDefinition.SupportsAnswerMode(mode)))
            throw new CollectorStoreException(AnswerModeRules.AnswerModeUnsupported,
                $"{AppChannelDefinition.DisplayName} 不支持回答模式 {(Enum.IsDefined(mode) ? AnswerModeRules.ToStorage(mode) : mode.ToString())}。", 400);
        if (!Enum.IsDefined(target.Frequency) || !AppChannelDefinition.SupportsFrequency(target.Frequency))
            throw Error(MonitoringFrequencyRules.FrequencyInvalid,
                "App 目标的采集频率固定为 weekly（1次/周），请显式传入 frequency=weekly；更高的频率暂未开放。", 400);
        // The only mode is the default one, so nothing is stored and the key has no mode segment.
        var write = new SiteTargetWrite(AppChannelDefinition.Provider, engine, AppChannelDefinition.CaptureMode, country, locale,
            target.Enabled, null, AppChannelDefinition.Frequency);
        return new NormalizedSiteTarget(write, null, CollectionIdentity.AppTargetKey(engine), false, true);
    }

    /// <summary>
    /// App 目标的目录校验（PUT、团队默认配置与预估共用，纯函数）：目录里没有任何 App 行表示迁移 049 未执行 → 503
    /// APP_CHANNEL_SCHEMA_NOT_MIGRATED；平台没有 App 入口 → 400 TARGET_NOT_ALLOWED；启用一个 App 入口不可用的平台 →
    /// 400 TARGET_NOT_ALLOWED 并附原因。不含 App 目标时什么都不做。
    /// <paramref name="open"/> 为 false（租户不在 Collector:AppTenantIds 里，design 6.1）时，任何 App 目标都按“不在目录中”拒绝
    /// （400 TARGET_NOT_ALLOWED），先于其他判断。<paramref name="alreadyEnabled"/> 是保存前已经启用的 App 平台：平台后来变为不可用时，
    /// 这些目标仍被接受并保持启用（design 3.4），只拒绝新启用。
    /// </summary>
    internal static void EnsureAppTargetsAllowed(IEnumerable<(string Engine, bool Enabled)> appTargets, IReadOnlyList<CollectionTargetView> catalog,
        bool open, IReadOnlySet<string>? alreadyEnabled = null)
    {
        var requested = appTargets.ToArray();
        if (requested.Length == 0) return;
        if (!open) throw Error("TARGET_NOT_ALLOWED", "目标不在 Provider 目录中。", 400);
        var rows = catalog.Where(c => MonitoringProviders.IsApp(c.Provider)).ToArray();
        if (rows.Length == 0) throw AppChannelSchema.Missing();
        foreach (var (engine, enabled) in requested)
        {
            var row = rows.FirstOrDefault(c => c.Engine == engine)
                ?? throw Error("TARGET_NOT_ALLOWED", "目标不在 Provider 目录中。", 400);
            // Only newly enabling an unavailable platform is refused. A target that was enabled before this save stays
            // (it is no longer expanded); refusing it would block every other change to the same brand or team default.
            if (enabled && row.Availability != "available" && alreadyEnabled?.Contains(engine) != true)
                throw Error("TARGET_NOT_ALLOWED",
                    $"{row.DisplayName} 的 App 目标当前不可用，不能启用：{row.UnavailableReason?.Message ?? "平台的 App 入口已停用"}。", 400);
        }
    }

    /// <summary>
    /// 请求里要启用、而目录中平台的 App 入口不可用的 App 平台。非空时调用方才需要读取保存前已启用的 App 目标
    /// （<see cref="EnsureAppTargetsAllowed"/> 的 alreadyEnabled），其余请求不多执行任何查询。
    /// </summary>
    internal static string[] UnavailableAppEngines(IEnumerable<(string Engine, bool Enabled)> appTargets, IReadOnlyList<CollectionTargetView> catalog)
        => appTargets.Where(t => t.Enabled && catalog.Any(c => MonitoringProviders.IsApp(c.Provider) && c.Engine == t.Engine
                && c.Availability != "available"))
            .Select(t => t.Engine).ToArray();

    /// <summary>品牌当前已启用的 App 目标的平台代码。</summary>
    internal static async Task<IReadOnlySet<string>> EnabledAppEnginesAsync(MySqlConnection connection, MySqlTransaction? transaction,
        Guid tenantId, Guid siteId, CancellationToken cancellationToken)
        => (await Rows(connection, transaction, """
                SELECT engine FROM site_targets
                WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site) AND provider = @provider AND enabled = TRUE;
                """, cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()), ("@provider", AppChannelDefinition.Provider)))
            .Select(row => row[0]).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// 请求里有要启用、但平台已不可用的 App 目标时，读取品牌保存前已启用的 App 平台；否则返回空（不访问数据库）。
    /// 读取不加锁：之后的保存有版本校验，期间任何对该品牌目标的改动都会使本次保存以 409 失败。
    /// </summary>
    internal static async Task<IReadOnlySet<string>?> EnabledAppEnginesIfNeededAsync(MySqlDatabase database, Guid tenantId, Guid siteId,
        IEnumerable<(string Engine, bool Enabled)> appTargets, IReadOnlyList<CollectionTargetView> catalog, CancellationToken cancellationToken)
    {
        if (UnavailableAppEngines(appTargets, catalog).Length == 0) return null;
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        return await EnabledAppEnginesAsync(connection, null, tenantId, siteId, cancellationToken);
    }

    /// <summary>
    /// PUT 的纯函数部分：逐项规范化、按含模式的目标键查重（DUPLICATE_TARGET）、限制启用的每日监控目标数（MONITORING_TOO_LARGE），
    /// scope=monitoring 时拒绝不参与定时监控的目标（TARGET_NOT_ALLOWED）。<paramref name="enforceMonitoringLimit"/> 为 false 时
    /// （目标预估）不因目标数超限抛错，由调用方作为阻断项报告。App 目标不计入 20 个目标的上限，也不计入单次请求 30 个的上限
    /// （它另有上限：App 平台数）。
    /// </summary>
    internal static NormalizedSiteTarget[] ValidateSiteTargetsWrite(SiteTargetsWrite request, bool enforceMonitoringLimit = true)
    {
        var targets = request?.Targets;
        // App targets have their own cap, so a request may carry 30 other targets plus one App target per platform.
        var appCount = targets?.Count(t => MonitoringProviders.IsApp(t?.Provider)) ?? 0;
        if (targets is null || targets.Count - appCount > 30) throw Error("INVALID_TARGETS", "目标最多 30 个。", 400);
        if (appCount > AppChannelDefinition.Platforms.Count)
            throw Error("INVALID_TARGETS", $"App 目标最多 {AppChannelDefinition.Platforms.Count} 个（每个平台一个）。", 400);
        if (!Enum.IsDefined(request!.Scope)) throw Error("INVALID_TARGETS", "scope 只支持 all 或 monitoring。", 400);
        var normalized = targets.Select(NormalizeTarget).ToArray();
        if (normalized.Select(t => t.Key).Distinct(StringComparer.Ordinal).Count() != normalized.Length)
            throw Error("DUPLICATE_TARGET", "同一 Target（含回答模式）不可重复。", 400);
        if (request.Scope == SiteTargetsScope.Monitoring && normalized.Any(t => !t.Eligible && !t.App))
            throw Error("TARGET_NOT_ALLOWED", "scope=monitoring 只能提交可参与每日监控的目标。", 400);
        // Both scopes end with exactly the request's eligible targets enabled (monitoring scope disables omitted eligible rows).
        if (enforceMonitoringLimit && normalized.Count(t => t.Eligible && t.Write.Enabled) > MonitoringMatrix.MaxTargets)
            throw Error("MONITORING_TOO_LARGE", $"每日监控最多启用 {MonitoringMatrix.MaxTargets} 个目标（同一模型的两种回答模式计为 2 个）。", 400);
        return normalized;
    }

    // Availability of a settings-backed API provider: migration 025 present, row enabled and a credential saved.
    // A missing settings store or an unmigrated schema makes the catalog row unavailable instead of failing the catalog.
    internal static async Task<string> ResolveApiProviderAvailabilityAsync(IAnswerProviderSettingsStore? settings,
        AnswerProviderDefinition definition, CancellationToken cancellationToken)
    {
        if (settings is null) return "unavailable";
        try { return await settings.GetConfigurationAsync(definition.Provider, cancellationToken) is null ? "unavailable" : "available"; }
        catch (CollectorStoreException exception) when (exception.Code == AnswerProviderSchema.NotMigratedCode) { return "unavailable"; }
    }

    internal static CollectionTargetView ApiProviderCatalogRow(AnswerProviderDefinition definition, string engine, string availability) =>
        new(null, definition.Provider, engine, EngineDisplayName(engine), definition.CaptureMode, definition.EvidenceClass,
            definition.DefaultCountry, definition.DefaultLocale, true, availability, "supported", "conditional", "conditional",
            MonitoringTargetKey(definition.Provider, engine, definition.CaptureMode, definition.DefaultCountry, definition.DefaultLocale),
            true, definition.SupportedAnswerModes, definition.DefaultAnswerMode);

    /// <summary>
    /// 与每日监控相同的目标键：API 回答 Provider 按 <see cref="CollectionIdentity.TargetKey(CloroVisibilityTarget)"/>（与计划、
    /// 运行展开和 prompt_target_scopes 一致），其他 Provider 按原始五元组；<paramref name="storedMode"/> 为空（默认模式）时与旧键逐字节一致。
    /// </summary>
    internal static string MonitoringTargetKey(string provider, string engine, string captureMode, string country, string locale,
        string? storedMode = null)
        => AnswerProviderDefinition.IsRegistered(provider)
            ? CollectionIdentity.TargetKey(new CloroVisibilityTarget(provider, engine, locale, country, null, storedMode))
            // App targets have one fixed key per platform (no region or mode segment of their own).
            : MonitoringProviders.IsApp(provider) ? CollectionIdentity.AppTargetKey(engine)
            : CollectionIdentity.TargetKey(provider, engine, captureMode, country, locale, storedMode);

    /// <summary>
    /// 由站点目标视图构造每日监控目标：回答模式按 <see cref="AnswerModeRules.Normalize"/> 折叠（默认模式为空），
    /// 目标键、选择哈希与请求哈希在只有默认模式时与引入回答模式前一致。
    /// </summary>
    internal static CloroVisibilityTarget MonitoringTarget(CollectionTargetView target)
        => AnswerModeRules.Normalize(new CloroVisibilityTarget(target.Provider, target.Engine, target.Locale, target.Country, "{}",
            target.AnswerMode is { } mode ? AnswerModeRules.ToStorage(mode) : null));

    private static string EngineDisplayName(string engine) => engine switch
    {
        "doubao" => "豆包",
        "deepseek" => "DeepSeek",
        "qwen" => "通义千问",
        "zhipu_qingyan" => "智谱清言",
        "kimi" => "Kimi",
        "tencent_yuanbao" => "腾讯元宝",
        _ => engine,
    };

    // App rows use the App platform names (AppChannelDefinition.Platforms); every other row keeps EngineDisplayName.
    private static string TargetDisplayName(string provider, string engine)
        => MonitoringProviders.IsApp(provider) ? AppChannelDefinition.TryGetPlatform(engine)?.DisplayName ?? engine : EngineDisplayName(engine);

    /// <summary>平台的 App 入口状态：<see cref="Enabled"/> 为是否有启用的 consumer_app 入口，<see cref="Reason"/> 为停用时登记的原因码。</summary>
    internal sealed record AppEntryState(bool Enabled, string? Reason);

    /// <summary>
    /// 读取每个平台的 consumer_app 入口状态（049 已执行时调用）。有任一启用的入口即可用；全部停用时取版本最高那条的原因码。
    /// 不读 collector_platforms.enabled：那是网页渠道的开关。
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, AppEntryState>> AppEntryStatesAsync(MySqlConnection connection,
        CancellationToken cancellationToken)
    {
        var rows = await Rows(connection, null, """
            SELECT e.platform_code, e.enabled, e.unavailable_reason FROM collector_platform_entries e
            WHERE e.channel = 'consumer_app' ORDER BY e.platform_code, e.enabled DESC, e.version DESC;
            """, cancellationToken);
        var states = new Dictionary<string, AppEntryState>(StringComparer.Ordinal);
        foreach (var row in rows)
            states.TryAdd(row[0], new AppEntryState(Bool(row[1]), row[2].Length == 0 ? null : row[2]));
        return states;
    }

    /// <summary>App 入口不可用的原因：有登记的原因码时返回原因码与中文说明，没有登记原因时为空。</summary>
    internal static CollectionTargetUnavailableReason? AppUnavailableReason(string? code) => code switch
    {
        null or "" => null,
        AppUnavailableReasons.ContentUnreadable => new(code, "App 内容无法读取"),
        AppUnavailableReasons.EmulatorRejected => new(code, "App 拒绝在模拟器上运行"),
        _ => new(code, "平台的 App 入口已停用"),
    };

    /// <summary>目录里的一行 App 目标（design 3.3）：每个平台一行，固定 CN / zh-CN / app，只有快速回答，频率固定 1次/周。</summary>
    internal static CollectionTargetView AppCatalogRow(AppChannelPlatform platform, AppEntryState entry) =>
        new(null, AppChannelDefinition.Provider, platform.Code, platform.DisplayName, AppChannelDefinition.CaptureMode,
            AppChannelDefinition.EvidenceClass, AppChannelDefinition.Country, AppChannelDefinition.Locale, entry.Enabled,
            entry.Enabled ? "available" : "unavailable", "supported", "conditional", "conditional",
            CollectionIdentity.AppTargetKey(platform.Code), true, AppChannelDefinition.SupportedAnswerModes,
            AppChannelDefinition.DefaultAnswerMode, Channel: CollectionChannel.App, FixedFrequency: AppChannelDefinition.Frequency,
            UnavailableReason: entry.Enabled ? null : AppUnavailableReason(entry.Reason));

    /// <summary>
    /// 给目录行或站点目标行补上渠道字段。渠道只由 Provider 推导。站点目标里的 App 行另外补上 App 的显示名、固定频率、
    /// 可参与定时监控的标记，以及入口停用时的不可用状态与原因（<paramref name="appEntries"/> 为空表示 049 未执行）。
    /// 非 App 行除 <see cref="CollectionTargetView.Channel"/> 外不变。
    /// </summary>
    internal static CollectionTargetView WithChannel(CollectionTargetView row, IReadOnlyDictionary<string, AppEntryState>? appEntries = null)
    {
        var channel = MonitoringProviders.Channel(row.Provider);
        if (channel != CollectionChannel.App) return row with { Channel = channel };
        if (row.TargetId is null) return row with { Channel = channel };
        var entry = appEntries?.GetValueOrDefault(row.Engine);
        var usable = entry is { Enabled: true };
        return row with
        {
            DisplayName = TargetDisplayName(row.Provider, row.Engine),
            Availability = !row.Enabled ? row.Availability : usable ? "available" : "unavailable",
            SearchQueryCapability = "conditional",
            MonitoringEligible = true,
            Channel = channel,
            FixedFrequency = AppChannelDefinition.Frequency,
            UnavailableReason = usable ? null : AppUnavailableReason(entry?.Reason),
        };
    }

    public async Task<IReadOnlyList<CollectionTargetView>> GetTargetCatalogAsync(CancellationToken cancellationToken = default,
        Guid? tenantId = null)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var settings = await Row(connection, null, "SELECT enabled, api_url FROM cloro_provider_settings WHERE id = 1;",
            cancellationToken);
        // Same rule as GET /providers/cloro/settings: calls use the 022 key pool, never the singleton api_key.
        var keys = await CloroKeyPoolSchema.ExistsAsync(connection, null, cancellationToken)
            ? await Row(connection, null, "SELECT COUNT(*) FROM cloro_api_keys WHERE enabled = TRUE AND archived_at IS NULL;",
                cancellationToken)
            : null;
        var cloroReady = settings is not null && Bool(settings[0]) && keys is not null && int.TryParse(keys[0], out var count) && count > 0
            && Uri.TryCreate(settings[1], UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
        var cloro = AnswerProviderDefinition.Cloro;
        var result = CloroEngines.Select(engine => new CollectionTargetView(null, "cloro", engine, engine,
            "scraped", "observed_surface", "US", "en-US", true, cloroReady ? "available" : "unavailable",
            "supported", "conditional", "conditional", MonitoringTargetKey("cloro", engine, "scraped", "US", "en-US"), true,
            cloro.SupportedAnswerModes, cloro.DefaultAnswerMode)).ToList();
        // Settings-backed API providers (doubao_api, deepseek_api) follow answer_provider_settings instead of the Cloro key pool.
        foreach (var definition in AnswerProviderDefinition.All.Where(d => d.Provider != AnswerProviderDefinition.Cloro.Provider))
        {
            var availability = await ResolveApiProviderAvailabilityAsync(providerSettings, definition, cancellationToken);
            result.AddRange(definition.Engines.Select(engine => ApiProviderCatalogRow(definition, engine, availability)));
        }
        var domestic = await Rows(connection, null, """
            SELECT p.code, p.display_name, p.enabled FROM collector_platforms p WHERE p.region = 'cn' ORDER BY p.code;
            """, cancellationToken);
        result.AddRange(domestic.Select(row => new CollectionTargetView(null, "domestic_collector", row[0], row[1],
            "consumer_web", "observed_surface", "CN", "zh-CN", Bool(row[2]), Bool(row[2]) ? "available" : "unavailable",
            "supported", "conditional", "unknown", MonitoringTargetKey("domestic_collector", row[0], "consumer_web", "CN", "zh-CN"), false,
            FastOnly, AnswerMode.Fast)));
        // App segment (049): one row per known platform that has a consumer_app entry, in AppChannelDefinition.Platforms order.
        // Before 049 the catalog has no App rows and is otherwise unchanged. The same holds for a tenant the App channel is
        // not open for (Collector:AppTenantIds, design 6.1) and for a call without a tenant.
        if (tenantId is { } tenant && AppOpen(tenant) && await AppChannelSchema.IsMigratedAsync(connection, null, cancellationToken))
        {
            var entries = await AppEntryStatesAsync(connection, cancellationToken);
            result.AddRange(AppChannelDefinition.Platforms.Where(platform => entries.ContainsKey(platform.Code))
                .Select(platform => AppCatalogRow(platform, entries[platform.Code])));
        }
        return result.Select(row => WithChannel(row)).ToList();
    }

    public async Task<SiteTargetsView> GetSiteTargetsAsync(Guid tenantId, Guid siteId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await EnsureSiteExists(connection, null, tenantId, siteId, cancellationToken);
        var config = await Row(connection, null, "SELECT target_config_version FROM site_brand_settings WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);",
            cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()));
        // Before 047 every target reads as the provider default mode with daily frequency (the pre-047 behaviour).
        var migrated = await AnswerModeSchema.IsMigratedAsync(connection, null, cancellationToken);
        var rows = await Rows(connection, null, $"""
            SELECT BIN_TO_UUID(id), provider, engine, capture_mode, country, locale, enabled,
                   {(migrated ? "answer_mode, frequency, DATE_FORMAT(frequency_anchor, '%Y-%m-%d')" : "NULL, 'daily', NULL")}
            FROM site_targets WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site)
            ORDER BY provider, engine, country, locale{(migrated ? ", answer_mode" : "")};
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()));
        // design 6.1: a tenant the App channel is not open for never sees App rows, even when such rows are stored.
        if (!AppOpen(tenantId)) rows.RemoveAll(row => MonitoringProviders.IsApp(row[1]));
        // Before 048 every brand reads as an individual configuration (followsTeamDefault = false).
        var follows = await TeamMonitoringSchema.IsMigratedAsync(connection, null, cancellationToken)
            && await Row(connection, null, "SELECT monitoring_follows_default FROM site_brand_settings WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site);",
                cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString())) is { } flag && Bool(flag[0]);
        // App rows also report whether the platform's App entry is usable; brands without App rows never run this query.
        var appEntries = rows.Any(row => MonitoringProviders.IsApp(row[1])) && await AppChannelSchema.IsMigratedAsync(connection, null, cancellationToken)
            ? await AppEntryStatesAsync(connection, cancellationToken)
            : null;
        // Evidence class and display name follow the catalog row of the same provider/engine (Cloro rows are unchanged).
        // App rows are listed after every other row, as in the catalog; the other rows keep their order.
        return new SiteTargetsView(uint.Parse(config![0]), rows.OrderBy(row => MonitoringProviders.IsApp(row[1])).Select(row =>
        {
            var (supported, defaultMode) = AnswerModeCapabilities(row[1]);
            var stored = row[7].Length == 0 ? null : row[7];
            return WithChannel(new CollectionTargetView(Guid.Parse(row[0]), row[1], row[2], EngineDisplayName(row[2]), row[3],
                AnswerProviderDefinition.TryGet(row[1])?.EvidenceClass ?? "observed_surface", row[4], row[5], Bool(row[6]), Bool(row[6]) ? "available" : "disabled",
                "supported", "conditional", AnswerProviderDefinition.IsRegistered(row[1]) ? "conditional" : "unknown",
                MonitoringTargetKey(row[1], row[2], row[3], row[4], row[5], stored),
                // Same rule as AutomaticMonitoringSelection.Targets: every registered API answer provider joins daily monitoring.
                AnswerProviderDefinition.IsRegistered(row[1]), supported, defaultMode,
                AnswerModeSchema.ParseStored(stored) ?? defaultMode,
                MonitoringFrequencyRules.TryParse(row[8], out var frequency) ? frequency : MonitoringFrequency.Daily,
                DateOnly.TryParseExact(row[9], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var anchor) ? anchor : null),
                appEntries);
        }).ToArray(), follows);
    }

    public async Task<SiteTargetsView> PutSiteTargetsAsync(Guid tenantId, Guid siteId,
        SiteTargetsWrite request, CancellationToken cancellationToken = default, bool markIndividual = false)
    {
        var normalized = ValidateSiteTargetsWrite(request);
        var appOpen = AppOpen(tenantId);
        var catalog = await GetTargetCatalogAsync(cancellationToken, tenantId);
        // App targets first: 400 for a tenant the App channel is not open for, 503 before 049, 400 with the reason when an
        // App target is newly enabled on a platform whose App entry is unavailable (one enabled before this save is kept).
        var appTargets = normalized.Where(t => t.App).Select(t => (t.Write.Engine, t.Write.Enabled)).ToArray();
        EnsureAppTargetsAllowed(appTargets, catalog, appOpen,
            appOpen ? await EnabledAppEnginesIfNeededAsync(database, tenantId, siteId, appTargets, catalog, cancellationToken) : null);
        foreach (var target in normalized)
            if (!catalog.Any(c => c.Provider == target.Write.Provider && c.Engine == target.Write.Engine))
                throw Error("TARGET_NOT_ALLOWED", "目标不在 Provider 目录中。", 400);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await GeminiResultSchema.RequireAsync(connection, transaction, cancellationToken);
        // Before 047 only the legacy shape (default mode, daily) can be written; anything else needs the new columns.
        var migrated = await AnswerModeSchema.IsMigratedAsync(connection, transaction, cancellationToken);
        if (!migrated && normalized.Any(t => t.StoredMode is not null || t.Write.Frequency != MonitoringFrequency.Daily))
            throw AnswerModeSchema.Missing();
        var site = await LockSite(connection, transaction, tenantId, siteId, cancellationToken);
        if (site[2] != "active") throw Error("RESOURCE_NOT_FOUND", "工作区已归档。", 404);
        if (request.ExpectedVersion != uint.Parse(site[9])) throw Error("CONFIG_VERSION_CONFLICT", "Target 配置版本已变化。", 409);
        // Frequency anchors are local dates in the zone the monitoring slots use: the main plan's zone once it exists
        // (it does not follow later brand time-zone edits), otherwise the brand zone the plan will be created with.
        var today = BrandLocalDate(await AutomaticMonitoringSelection.MainPlanTimeZoneAsync(connection, transaction, tenantId, siteId,
            cancellationToken) ?? site[5], DateTimeOffset.UtcNow);
        await ApplySiteTargetsAsync(connection, transaction, tenantId, siteId, normalized, request.Scope, today, migrated, appOpen, cancellationToken);
        // A public save makes the brand an individual configuration; the internal first-monitoring write keeps the flag.
        if (markIndividual && await TeamMonitoringSchema.IsMigratedAsync(connection, transaction, cancellationToken))
            await Exec(connection, transaction, "UPDATE site_brand_settings SET monitoring_follows_default = 0 WHERE site_id = UUID_TO_BIN(@site);",
                cancellationToken, ("@site", siteId.ToString()));
        await transaction.CommitAsync(cancellationToken);
        await ReconcileNightAfterSaveAsync(tenantId, siteId, cancellationToken);
        return await GetSiteTargetsAsync(tenantId, siteId, cancellationToken);
    }

    // The 00:05 night plan follows twice_daily cells right away (only once daily monitoring exists for the brand).
    // Best effort: the saved targets are committed, and the lazy reconcile before each slot and on
    // GET/POST prompt-monitoring repeats this, so a failure here is logged and never fails the save.
    private async Task ReconcileNightAfterSaveAsync(Guid tenantId, Guid siteId, CancellationToken cancellationToken)
    {
        try { await AutomaticMonitoringSelection.ReconcileNightScheduleAsync(database, tenantId, siteId, cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger?.LogWarning(exception, "站点目标已保存，但凌晨监控计划同步失败，将在下一次监控计划检查时重试。site={SiteId}", siteId);
        }
        // The App plan follows App targets the same way (design 4.1): for a brand that already monitors it is created when
        // the brand now has App targets; its snapshot is refreshed and its pause flag follows the main plan. Never creates
        // the main plan, a run or a reservation. Nothing is read or written for a tenant the App channel is not open for.
        if (!AppOpen(tenantId)) return;
        try { await AppMonitoringPlan.ReconcileAsync(database, tenantId, siteId, true, false, cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger?.LogWarning(exception, "站点目标已保存，但 App 采集计划同步失败，将在下一次监控计划检查时重试。site={SiteId}", siteId);
        }
    }

    /// <summary>
    /// 站点目标的唯一写入路径（品牌保存、团队默认配置下发、恢复跟随都经过这里）：停用 scope 内未提交的行 → 逐项 upsert
    /// （锚点、monitoring_enabled_at 按 047 语义）→ 目标配置版本加 1。调用方已持有该品牌的行锁（LockSite）并完成校验；
    /// <paramref name="today"/> 为该品牌时隙时区（主计划时区，没有主计划时为品牌时区）的本地日期。
    /// <paramref name="appOpen"/> 为 false（租户未开放 App 渠道）时不停用任何 App 行：这些行对该租户不可见，请求里也不可能带着它们。
    /// </summary>
    private static async Task ApplySiteTargetsAsync(MySqlConnection connection, MySqlTransaction transaction, Guid tenantId, Guid siteId,
        NormalizedSiteTarget[] normalized, SiteTargetsScope scope, DateOnly today, bool migrated, bool appOpen, CancellationToken cancellationToken)
    {
        var keys = normalized.Select(t => t.Key).ToArray();
        // Keep pre-PUT enablement intact until the upserts decide activation boundaries.
        var existing = await Rows(connection, transaction,
            "SELECT target_key, provider FROM site_targets WHERE site_id=UUID_TO_BIN(@site) FOR UPDATE;",
            cancellationToken, ("@site", siteId.ToString()));
        // scope=monitoring replaces API rows and App rows; every other row (domestic web collector) stays untouched.
        // App rows of a tenant the App channel is not open for are invisible to it and are left exactly as stored.
        foreach (var omitted in existing.Where(row => !keys.Contains(row[0], StringComparer.Ordinal)
                     && (scope == SiteTargetsScope.All || MonitoringProviders.IsEligible(row[1]))
                     && (appOpen || !MonitoringProviders.IsApp(row[1]))))
            await Exec(connection, transaction, "UPDATE site_targets SET enabled = FALSE, updated_at = UTC_TIMESTAMP(6) WHERE site_id = UUID_TO_BIN(@site) AND target_key=@key;",
                cancellationToken, ("@site", siteId.ToString()), ("@key", omitted[0]));
        foreach (var target in normalized)
        {
            var write = target.Write;
            // A newly enabled App target is anchored to the enabling day like an API target (11-27-app-first-cycle-now): its
            // first cycle starts at once through an instant slot of the App plan and runs to the same weekday's 09:11 next week,
            // after which the regular weekly slots on that weekday take over. The SQL below only takes this value when the
            // target is newly enabled, so re-saving an enabled App target keeps its anchor.
            var anchor = today;
            (string, object?)[] parameters = [("@id", Guid.NewGuid().ToString()), ("@tenant", tenantId.ToString()),
                ("@site", siteId.ToString()), ("@provider", write.Provider), ("@engine", write.Engine),
                ("@capture", write.CaptureMode), ("@country", write.Country), ("@locale", write.Locale),
                ("@key", target.Key), ("@enabled", write.Enabled), ("@mode", target.StoredMode),
                ("@frequency", MonitoringFrequencyRules.ToStorage(write.Frequency)), ("@today", anchor.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))];
            // ON DUPLICATE KEY UPDATE assigns left to right, so the anchor and the activation boundary read the old
            // enabled / frequency before they are overwritten. The anchor moves to today when the target is newly enabled
            // or its frequency changes to every_other_day / weekly; otherwise it is kept (daily / twice_daily ignore it).
            await Exec(connection, transaction, migrated
                ? """
                INSERT INTO site_targets (id, tenant_id, site_id, provider, engine, capture_mode, country, locale,
                    target_key, enabled, monitoring_enabled_at, answer_mode, frequency, frequency_anchor, created_at, updated_at)
                VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), @provider, @engine, @capture,
                    @country, @locale, @key, @enabled, IF(@enabled, UTC_TIMESTAMP(6), NULL), @mode, @frequency,
                    IF(@enabled, @today, NULL), UTC_TIMESTAMP(6), UTC_TIMESTAMP(6))
                ON DUPLICATE KEY UPDATE
                    frequency_anchor = IF((NOT enabled AND VALUES(enabled))
                        OR (VALUES(frequency) IN ('every_other_day', 'weekly') AND frequency <> VALUES(frequency)),
                        VALUES(frequency_anchor), frequency_anchor),
                    monitoring_enabled_at = IF(NOT enabled AND VALUES(enabled), UTC_TIMESTAMP(6), monitoring_enabled_at),
                    enabled = VALUES(enabled), frequency = VALUES(frequency), updated_at = UTC_TIMESTAMP(6);
                """
                : """
                INSERT INTO site_targets (id, tenant_id, site_id, provider, engine, capture_mode, country, locale,
                    target_key, enabled, monitoring_enabled_at, created_at, updated_at)
                VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), @provider, @engine, @capture,
                    @country, @locale, @key, @enabled, IF(@enabled, UTC_TIMESTAMP(6), NULL), UTC_TIMESTAMP(6), UTC_TIMESTAMP(6))
                ON DUPLICATE KEY UPDATE monitoring_enabled_at = IF(NOT enabled AND VALUES(enabled), UTC_TIMESTAMP(6), monitoring_enabled_at),
                    enabled = VALUES(enabled), updated_at = UTC_TIMESTAMP(6);
                """, cancellationToken, migrated ? parameters : parameters[..10]);
        }
        // Every PUT (enable/disable, mode or frequency change) bumps the target config version; the daily plan is
        // reconciled lazily before each slot and on GET/POST prompt-monitoring, exactly as before.
        await Exec(connection, transaction, "UPDATE site_brand_settings SET target_config_version = target_config_version + 1, updated_at = UTC_TIMESTAMP(6) WHERE site_id = UUID_TO_BIN(@site);",
            cancellationToken, ("@site", siteId.ToString()));
    }

    /// <summary>品牌时区的当地日期；时区无效时退回 Asia/Shanghai（与团队汇总的默认时区一致）。</summary>
    internal static DateOnly BrandLocalDate(string? timeZone, DateTimeOffset utcNow)
    {
        TimeZoneInfo zone;
        try { zone = CollectionScheduleCalendar.ParseTimeZone(timeZone ?? ""); }
        catch (CollectorStoreException) { zone = CollectionScheduleCalendar.ParseTimeZone("Asia/Shanghai"); }
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow, zone).DateTime);
    }

    private sealed record FrozenPrompt(Guid PromptId, Guid VersionId);
    private sealed record FrozenSelection(Guid[] PromptIds, FrozenPrompt[] PromptVersions, SiteTargetWrite[] Targets, int SampleCount);

    private static async Task<(FrozenSelection Frozen, uint ConfigVersion, uint TargetConfigVersion)> ResolveSelection(MySqlConnection connection,
        MySqlTransaction transaction, Guid tenantId, Guid siteId, CollectionSelection selection, CancellationToken cancellationToken)
    {
        if (selection.SampleCount is < 1 or > 10) throw Error("INVALID_SAMPLE_COUNT", "sampleCount 必须为 1–10。", 400);
        if ((selection.PromptIds?.Count ?? 0) + (selection.TagIds?.Count ?? 0) == 0)
            throw Error("PROMPTS_REQUIRED", "请选择 Prompt ID 或标签。", 400);
        if (selection.TargetIds is not { Count: > 0 and <= 20 })
            throw Error("TARGETS_REQUIRED", "请选择 1–20 个已启用 Target。", 400);
        if (selection.TargetIds.Distinct().Count() != selection.TargetIds.Count)
            throw Error("DUPLICATE_TARGET", "同一 Target ID 不可重复。", 400);
        var site = await LockSite(connection, transaction, tenantId, siteId, cancellationToken);
        if (site[2] != "active") throw Error("RESOURCE_NOT_FOUND", "工作区已归档。", 404);
        var promptIds = new HashSet<Guid>(selection.PromptIds ?? []);
        if (promptIds.Contains(Guid.Empty)) throw Error("INVALID_PROMPT", "Prompt ID 无效。", 400);
        foreach (var tagId in (selection.TagIds ?? []).Distinct())
        {
            var tag = await Row(connection, transaction, "SELECT status FROM tags WHERE site_id = UUID_TO_BIN(@site) AND id = UUID_TO_BIN(@tag);",
                cancellationToken, ("@site", siteId.ToString()), ("@tag", tagId.ToString()));
            if (tag is null || tag[0] != "active") throw Error("RESOURCE_NOT_FOUND", "标签不存在或已归档。", 404);
            var tagged = await Rows(connection, transaction, """
                SELECT BIN_TO_UUID(p.id) FROM prompts p JOIN prompt_version_tags pvt ON pvt.prompt_version_id = p.current_version_id
                WHERE p.site_id = UUID_TO_BIN(@site) AND pvt.tag_id = UUID_TO_BIN(@tag) AND p.enabled = TRUE AND p.archived_at IS NULL;
                """, cancellationToken, ("@site", siteId.ToString()), ("@tag", tagId.ToString()));
            foreach (var row in tagged) promptIds.Add(Guid.Parse(row[0]));
        }
        if (promptIds.Count is < 1 or > 200) throw Error("INVALID_PROMPTS", "有效 Prompt 必须为 1–200 个。", 400);
        var frozenPrompts = new List<FrozenPrompt>();
        foreach (var promptId in promptIds.Order())
        {
            var row = await Row(connection, transaction, """
                SELECT BIN_TO_UUID(current_version_id), enabled, archived_at FROM prompts
                WHERE site_id = UUID_TO_BIN(@site) AND id = UUID_TO_BIN(@prompt) FOR SHARE;
                """, cancellationToken, ("@site", siteId.ToString()), ("@prompt", promptId.ToString()));
            if (row is null || !Bool(row[1]) || row[2].Length > 0 || row[0].Length == 0)
                throw Error("RESOURCE_NOT_FOUND", "Prompt 不存在、未启用或已归档。", 404);
            frozenPrompts.Add(new FrozenPrompt(promptId, Guid.Parse(row[0])));
        }
        var targets = new List<SiteTargetWrite>();
        // A non-default mode is part of the target identity (deep and fast of one engine are two targets).
        var migrated = await AnswerModeSchema.IsMigratedAsync(connection, transaction, cancellationToken);
        foreach (var targetId in selection.TargetIds.Distinct())
        {
            var row = await Row(connection, transaction, $"""
                SELECT provider, engine, capture_mode, country, locale, enabled, {(migrated ? "answer_mode" : "NULL")} FROM site_targets
                WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site) AND id = UUID_TO_BIN(@target) FOR SHARE;
                """, cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()),
                ("@target", targetId.ToString()));
            if (row is null || !Bool(row[5])) throw Error("RESOURCE_NOT_FOUND", "Target 不存在或未启用。", 404);
            // App targets are only expanded by the weekly App plan: an explicit App target is refused, never silently dropped.
            if (MonitoringProviders.IsApp(row[0])) throw Error(AppChannelDefinition.TargetNotAllowedCode, AppChannelDefinition.TargetNotAllowedMessage, 400);
            if (!AnswerProviderDefinition.IsRegistered(row[0])) throw Error("TARGET_NOT_ALLOWED", "新编排仅支持 API 回答 Provider（Cloro、豆包）Target。", 400);
            targets.Add(new SiteTargetWrite(row[0], row[1], row[2], row[3], row[4], AnswerMode: AnswerModeSchema.ParseStored(row[6])));
        }
        if (targets.Select(SelectionTargetKey).Distinct().Count() != targets.Count)
            throw Error("DUPLICATE_TARGET", "同一 Target 不可重复。", 400);
        if (promptIds.Count * targets.Count * selection.SampleCount > 1000)
            throw Error("RUN_TOO_LARGE", "一批最多 1000 个 Check。", 400);
        // The mode is the last sort key, so selections without non-default modes keep their historical order.
        return (new FrozenSelection(promptIds.Order().ToArray(), frozenPrompts.ToArray(),
            targets.OrderBy(t => t.Engine, StringComparer.Ordinal).ThenBy(t => t.Country, StringComparer.Ordinal)
                .ThenBy(t => t.Locale, StringComparer.Ordinal)
                .ThenBy(t => t.AnswerMode is { } mode ? AnswerModeRules.ToStorage(mode) : "", StringComparer.Ordinal).ToArray(),
            selection.SampleCount), uint.Parse(site[6]), uint.Parse(site[9]));
    }

    public async Task<CollectionPreviewView> CreatePreviewAsync(Guid tenantId, Guid siteId, CollectionSelection selection,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var (frozen, version, targetVersion) = await ResolveSelection(connection, transaction, tenantId, siteId, selection, cancellationToken);
        var cloroTargets = frozen.Targets.Select(SelectionTarget).ToArray();
        var hash = CollectionIdentity.SelectionHash(frozen.PromptIds, cloroTargets, frozen.SampleCount);
        var previewId = Guid.NewGuid();
        var expires = DateTimeOffset.UtcNow.AddMinutes(10);
        var expected = frozen.PromptIds.Length * frozen.Targets.Length * frozen.SampleCount;
        await Exec(connection, transaction, """
            INSERT INTO collection_previews (id, tenant_id, site_id, selection_hash, selection_snapshot,
                subject_config_version, target_config_version, expected_checks, expires_at, created_at)
            VALUES (UUID_TO_BIN(@id), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), @hash, @snapshot,
                @version, @target_version, @expected, @expires, UTC_TIMESTAMP(6));
            """, cancellationToken, ("@id", previewId.ToString()), ("@tenant", tenantId.ToString()),
            ("@site", siteId.ToString()), ("@hash", hash), ("@snapshot", JsonSerializer.Serialize(frozen,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })),
            ("@version", version), ("@target_version", targetVersion), ("@expected", expected), ("@expires", expires.UtcDateTime));
        await transaction.CommitAsync(cancellationToken);
        var catalog = await GetTargetCatalogAsync(cancellationToken);
        var available = frozen.Targets.All(t => catalog.Any(c => c.Provider == t.Provider && c.Engine == t.Engine && c.Availability == "available"));
        // A run created from this preview reserves the same amount: checks × the monitoring unit price.
        return new CollectionPreviewView(previewId, siteId, frozen.PromptIds, frozen.Targets, frozen.SampleCount,
            expected, expected, PromptMonitoringPricing.Estimate((long)expected), available ? "available" : "unavailable", version, expires, targetVersion);
    }

    public async Task<(IReadOnlyList<Guid> PromptIds, IReadOnlyList<CloroVisibilityTarget> Targets, int SampleCount)> ResolveRunAsync(
        Guid tenantId, Guid siteId, CollectionSelection selection, Guid? previewId, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        FrozenSelection frozen;
        if (previewId is { } id)
        {
            if (selection.PromptIds is { Count: > 0 } || selection.TagIds is { Count: > 0 }
                || selection.TargetIds is { Count: > 0 } || selection.SampleCount != 1)
                throw Error("PREVIEW_SELECTION_CONFLICT", "提供 previewId 时不要重复指定选择条件。", 400);
            var site = await LockSite(connection, transaction, tenantId, siteId, cancellationToken);
            var row = await Row(connection, transaction, """
                SELECT selection_snapshot, subject_config_version, target_config_version, expires_at FROM collection_previews
                WHERE id = UUID_TO_BIN(@id) AND tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site) FOR SHARE;
                """, cancellationToken, ("@id", id.ToString()), ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()));
            if (row is null || uint.Parse(row[1]) != uint.Parse(site[6]) || uint.Parse(row[2]) != uint.Parse(site[9])
                || Utc(row[3]) <= DateTimeOffset.UtcNow)
                throw Error("PREVIEW_EXPIRED", "预览已过期或配置已变化。", 409);
            frozen = JsonSerializer.Deserialize<FrozenSelection>(row[0], new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            if (frozen is null) throw Error("PREVIEW_EXPIRED", "预览数据不可用。", 409);
            // Previews cannot freeze an App target (ResolveSelection refuses it); a snapshot that names one is refused as well.
            if (frozen.Targets.Any(t => MonitoringProviders.IsApp(t?.Provider)))
                throw Error(AppChannelDefinition.TargetNotAllowedCode, AppChannelDefinition.TargetNotAllowedMessage, 400);
        }
        else (frozen, _, _) = await ResolveSelection(connection, transaction, tenantId, siteId, selection, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (frozen.PromptIds, frozen.Targets.Select(SelectionTarget).ToArray(), frozen.SampleCount);
    }

    // Stored site-target rows carry the non-default mode (or none); the default folds to null so hashes stay unchanged.
    private static CloroVisibilityTarget SelectionTarget(SiteTargetWrite target)
        => AnswerModeRules.Normalize(new CloroVisibilityTarget(target.Provider, target.Engine, target.Locale, target.Country, "{}",
            target.AnswerMode is { } mode ? AnswerModeRules.ToStorage(mode) : null));

    private static string SelectionTargetKey(SiteTargetWrite target) => CollectionIdentity.TargetKey(SelectionTarget(target));
}
