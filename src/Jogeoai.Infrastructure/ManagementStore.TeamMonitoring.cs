using Jogeoai.Application;
using Jogeoai.Domain;
using MySqlConnector;

namespace Jogeoai.Infrastructure;

/// <summary>
/// 团队默认监控配置（048）：读取、保存并下发到跟随品牌、恢复跟随、物化（预估与首次开启监控）。
/// 继承在写入时完成——下发把团队单元格物化为各品牌的 site_targets，并且只经过 <see cref="ApplySiteTargetsAsync"/>；
/// Worker 与时隙展开仍只读 site_targets。锁顺序：tenants → sites（按 site_id）→ site_targets。
/// </summary>
public sealed partial class ManagementStore
{
    /// <summary>
    /// 校验后的团队单元格：<see cref="StoredMode"/> 为折叠后的模式（与 Provider 默认模式相同时为空，对应
    /// tenant_monitoring_default_cells.answer_mode 空串与 site_targets.answer_mode IS NULL），<see cref="Mode"/> 为解析后的模式。
    /// </summary>
    internal sealed record NormalizedTeamCell(string Provider, string Engine, string? StoredMode, AnswerMode Mode, MonitoringFrequency Frequency)
    {
        internal string Signature => TeamMonitoringRules.Signature(Provider, Engine, StoredMode, Frequency);
    }

    /// <summary>品牌已有的站点目标行（物化时沿用其地区）。</summary>
    internal sealed record ExistingTargetRow(string Provider, string Engine, string Country, string Locale, bool Enabled);

    /// <summary>
    /// 团队单元格的纯函数校验（与品牌目标保存同一套 Provider / 引擎 / 回答模式 / 频率规则）：为空 400 MONITORING_TARGETS_REQUIRED；
    /// 超过 20 个 400 MONITORING_TOO_LARGE（<paramref name="enforceMonitoringLimit"/> 为 false 时不检查，用于影响预估）；
    /// 非可监控 Provider 或引擎不属于 Provider 400 TARGET_NOT_ALLOWED；同一 provider + engine + 模式重复 400 DUPLICATE_TARGET。
    /// App 单元格（app_collector，fast，weekly）不计入 20 个与 30 个的上限，另有上限：App 平台数。
    /// </summary>
    internal static NormalizedTeamCell[] ValidateTeamCells(IReadOnlyList<TeamMonitoringCellWrite>? cells, bool enforceMonitoringLimit = true)
    {
        if (cells is null || cells.Count == 0)
            throw Error("MONITORING_TARGETS_REQUIRED", "团队默认监控配置至少需要 1 个单元格。", 400);
        // App cells do not take any of the 20 daily-monitoring cells; they are capped by the number of App platforms instead.
        var appCount = cells.Count(c => MonitoringProviders.IsApp(c?.Provider));
        if (enforceMonitoringLimit && cells.Count - appCount > MonitoringMatrix.MaxTargets)
            throw Error("MONITORING_TOO_LARGE", $"团队默认监控配置最多 {MonitoringMatrix.MaxTargets} 个单元格（同一模型的两种回答模式计为 2 个）。", 400);
        if (cells.Count - appCount > 30) throw Error("INVALID_TARGETS", "单元格最多 30 个。", 400);
        if (appCount > AppChannelDefinition.Platforms.Count)
            throw Error("INVALID_TARGETS", $"App 单元格最多 {AppChannelDefinition.Platforms.Count} 个（每个平台一个）。", 400);
        var result = new List<NormalizedTeamCell>(cells.Count);
        foreach (var cell in cells)
        {
            if (cell is null) throw Error("INVALID_TARGETS", "单元格不能为空。", 400);
            if (MonitoringProviders.IsApp(cell.Provider))
            {
                // An App cell is the App target on its fixed surface (CN / zh-CN / app): fast only, weekly only.
                var app = NormalizeTarget(new SiteTargetWrite(AppChannelDefinition.Provider, cell.Engine, AppChannelDefinition.CaptureMode,
                    AppChannelDefinition.Country, AppChannelDefinition.Locale, true, cell.AnswerMode, cell.Frequency));
                result.Add(new(app.Write.Provider, app.Write.Engine, null, AppChannelDefinition.DefaultAnswerMode, app.Write.Frequency));
                continue;
            }
            var definition = AnswerProviderDefinition.TryGet(cell.Provider)
                ?? throw Error("TARGET_NOT_ALLOWED", "团队默认监控配置只能包含可参与每日监控的模型。", 400);
            // The same normalization as a site target on the provider's default surface: engine, mode and frequency rules.
            var normalized = NormalizeTarget(new SiteTargetWrite(definition.Provider, cell.Engine, definition.CaptureMode,
                definition.DefaultCountry, definition.DefaultLocale, true, cell.AnswerMode, cell.Frequency));
            result.Add(new(normalized.Write.Provider, normalized.Write.Engine, normalized.StoredMode,
                AnswerModeSchema.ParseStored(normalized.StoredMode) ?? definition.DefaultAnswerMode, normalized.Write.Frequency));
        }
        if (result.Select(c => (c.Provider, c.Engine, c.StoredMode)).Distinct().Count() != result.Count)
            throw Error("DUPLICATE_TARGET", "同一模型的同一回答模式不可重复。", 400);
        return result.ToArray();
    }

    /// <summary>
    /// 把团队单元格物化为某个品牌的站点目标（design §4.2）：品牌已有同 provider + engine 的行时沿用其国家 / 语言（优先启用的行，
    /// 再按国家、语言排序取第一行）；没有时 Cloro 取目录国家 + 品牌语言，其他 Provider 取其默认国家 / 语言。固定地区的 Provider
    /// （豆包、DeepSeek、PackyAPI）始终取其固定地区。结果全部启用，按单元格顺序排列。
    /// App 单元格固定为 CN / zh-CN / app，不取品牌的地区和语言；目录里该平台的 App 入口已不可用时跳过该单元格，
    /// 除非品牌已经启用了它（<paramref name="enabledAppEngines"/>，design 3.4：已启用的目标保留）。
    /// </summary>
    internal static SiteTargetWrite[] MaterializeCells(IReadOnlyList<NormalizedTeamCell> cells, IReadOnlyList<ExistingTargetRow> existing,
        string brandLocale, IReadOnlyList<CollectionTargetView>? catalog, IReadOnlySet<string>? enabledAppEngines = null)
    {
        var result = new SiteTargetWrite[cells.Count];
        var skipped = false;
        for (var i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            if (MonitoringProviders.IsApp(cell.Provider))
            {
                // A platform whose App entry became unavailable after the default was saved is not handed to brands that do
                // not have it yet (unavailable platforms are never newly enabled). A brand that already has it enabled keeps it.
                if (catalog?.FirstOrDefault(c => MonitoringProviders.IsApp(c.Provider) && c.Engine == cell.Engine)
                    is { Availability: not "available" } && enabledAppEngines?.Contains(cell.Engine) != true)
                {
                    skipped = true;
                    continue;
                }
                // App cells never take the brand's region or locale: the App surface is fixed.
                result[i] = new SiteTargetWrite(cell.Provider, cell.Engine, AppChannelDefinition.CaptureMode,
                    AppChannelDefinition.Country, AppChannelDefinition.Locale, true, cell.Mode, cell.Frequency);
                continue;
            }
            var definition = AnswerProviderDefinition.TryGet(cell.Provider)!;
            var current = existing.Where(r => r.Provider == cell.Provider && r.Engine == cell.Engine)
                .OrderByDescending(r => r.Enabled).ThenBy(r => r.Country, StringComparer.Ordinal).ThenBy(r => r.Locale, StringComparer.Ordinal)
                .FirstOrDefault();
            var (country, locale) = definition.PinnedSurface ? (definition.DefaultCountry, definition.DefaultLocale)
                : current is not null ? (current.Country, current.Locale)
                : definition == AnswerProviderDefinition.Cloro
                    ? (catalog?.FirstOrDefault(c => c.Provider == cell.Provider && c.Engine == cell.Engine)?.Country ?? definition.DefaultCountry, brandLocale)
                    : (definition.DefaultCountry, definition.DefaultLocale);
            result[i] = new SiteTargetWrite(cell.Provider, cell.Engine, definition.CaptureMode, country, locale, true, cell.Mode, cell.Frequency);
        }
        return skipped ? result.Where(target => target is not null).ToArray() : result;
    }

    // Computed on use: static initializers across the partial files have no guaranteed order.
    private static NormalizedTeamCell[] BaselineCells => ValidateTeamCells([TeamMonitoringRules.Baseline]);

    private static TeamMonitoringCellView CellView(NormalizedTeamCell cell)
        => new(cell.Provider, cell.Engine, TargetDisplayName(cell.Provider, cell.Engine), cell.Mode, cell.Frequency);

    private static IEnumerable<NormalizedTeamCell> InCatalogOrder(IEnumerable<NormalizedTeamCell> cells)
        => cells.OrderBy(c => TeamMonitoringRules.CatalogOrder(c.Provider, c.Engine, c.Mode)).ThenBy(c => c.Frequency);

    // A stored cell or target row as a normalized cell; null for a provider that is no longer registered.
    private static NormalizedTeamCell? StoredCell(string provider, string engine, string? storedMode, string frequency)
    {
        // App cells and App target rows have no stored mode (fast only) and are always weekly.
        if (MonitoringProviders.IsApp(provider))
            return new(provider, engine, null, AppChannelDefinition.DefaultAnswerMode,
                MonitoringFrequencyRules.TryParse(frequency, out var appFrequency) ? appFrequency : AppChannelDefinition.Frequency);
        if (AnswerProviderDefinition.TryGet(provider) is not { } definition) return null;
        var stored = string.IsNullOrEmpty(storedMode) ? null : storedMode;
        return new(provider, engine, stored, AnswerModeSchema.ParseStored(stored) ?? definition.DefaultAnswerMode,
            MonitoringFrequencyRules.TryParse(frequency, out var parsed) ? parsed : MonitoringFrequency.Daily);
    }

    /// <summary>
    /// 团队默认配置的版本与单元格；没有行时为版本 0 与基线。<paramref name="locking"/> 为 true 时用锁定读取（读到最新提交）。
    /// <paramref name="appOpen"/> 为 false（租户未开放 App 渠道）时不返回已存的 App 单元格。
    /// </summary>
    private static async Task<(uint Version, bool Saved, NormalizedTeamCell[] Cells)> ReadTeamDefaultAsync(MySqlConnection connection,
        MySqlTransaction? transaction, Guid tenantId, bool locking, bool appOpen, CancellationToken cancellationToken)
    {
        var suffix = locking ? " FOR SHARE;" : ";";
        var row = await Row(connection, transaction, "SELECT config_version FROM tenant_monitoring_defaults WHERE tenant_id = UUID_TO_BIN(@tenant)" + suffix,
            cancellationToken, ("@tenant", tenantId.ToString()));
        if (row is null) return (0, false, BaselineCells);
        var cells = (await Rows(connection, transaction,
                "SELECT provider, engine, answer_mode, frequency FROM tenant_monitoring_default_cells WHERE tenant_id = UUID_TO_BIN(@tenant)" + suffix,
                cancellationToken, ("@tenant", tenantId.ToString())))
            .Select(r => StoredCell(r[0], r[1], r[2], r[3])).OfType<NormalizedTeamCell>()
            .Where(c => appOpen || !MonitoringProviders.IsApp(c.Provider));
        var ordered = InCatalogOrder(cells).ToArray();
        // A saved configuration always has at least one cell; an emptied row set falls back to the baseline.
        return (uint.Parse(row[0]), true, ordered.Length == 0 ? BaselineCells : ordered);
    }

    private static async Task<IReadOnlyList<ExistingTargetRow>> ExistingTargetsAsync(MySqlConnection connection, MySqlTransaction? transaction,
        Guid tenantId, Guid siteId, bool forUpdate, CancellationToken cancellationToken)
        => (await Rows(connection, transaction,
                "SELECT provider, engine, country, locale, enabled FROM site_targets WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site)"
                + (forUpdate ? " FOR UPDATE;" : ";"),
                cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString())))
            .Where(r => AnswerProviderDefinition.IsRegistered(r[0]))
            .Select(r => new ExistingTargetRow(r[0], r[1], r[2], r[3], Bool(r[4]))).ToArray();

    private static void EnsureInCatalog(IEnumerable<NormalizedTeamCell> cells, IReadOnlyList<CollectionTargetView> catalog, bool appOpen,
        IReadOnlySet<string>? savedAppEngines)
    {
        // App cells first (same rule as PUT /targets): 400 for a tenant the App channel is not open for, 503 before 049,
        // 400 with the reason for a new cell on an unavailable platform (a cell already in the saved default is kept).
        EnsureAppTargetsAllowed(cells.Where(c => MonitoringProviders.IsApp(c.Provider)).Select(c => (c.Engine, true)), catalog, appOpen,
            savedAppEngines);
        foreach (var cell in cells)
            if (!catalog.Any(c => c.Provider == cell.Provider && c.Engine == cell.Engine))
                throw Error("TARGET_NOT_ALLOWED", "目标不在 Provider 目录中。", 400);
    }

    // App platforms of the cells in the saved team default. Only read when the request has an App cell on an unavailable
    // platform; a later save is still guarded by its version check (a concurrent change of the default ends in 409).
    private async Task<IReadOnlySet<string>?> SavedAppCellEnginesAsync(Guid tenantId, IReadOnlyList<NormalizedTeamCell> cells,
        IReadOnlyList<CollectionTargetView> catalog, CancellationToken cancellationToken)
    {
        if (UnavailableAppEngines(cells.Where(c => MonitoringProviders.IsApp(c.Provider)).Select(c => (c.Engine, true)), catalog).Length == 0)
            return null;
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        if (!await TeamMonitoringSchema.IsMigratedAsync(connection, null, cancellationToken)) return null;
        return (await Rows(connection, null,
                "SELECT engine FROM tenant_monitoring_default_cells WHERE tenant_id = UUID_TO_BIN(@tenant) AND provider = @provider;",
                cancellationToken, ("@tenant", tenantId.ToString()), ("@provider", AppChannelDefinition.Provider)))
            .Select(row => row[0]).ToHashSet(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public async Task<TeamMonitoringConfigView> GetTeamMonitoringConfigAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await TeamMonitoringSchema.RequireAsync(connection, null, cancellationToken);
        var appOpen = AppOpen(tenantId);
        var (version, _, cells) = await ReadTeamDefaultAsync(connection, null, tenantId, false, appOpen, cancellationToken);
        // Same brand scope as GET /team/summary: active brands of the tenant ordered by creation.
        var brands = await Rows(connection, null, """
            SELECT BIN_TO_UUID(s.id), s.display_name, b.monitoring_follows_default, b.target_config_version
            FROM sites s JOIN site_brand_settings b ON b.site_id = s.id AND b.tenant_id = s.tenant_id
            WHERE s.tenant_id = UUID_TO_BIN(@tenant) AND s.status = 'active'
            ORDER BY s.created_at, s.id;
            """, cancellationToken, ("@tenant", tenantId.ToString()));
        // Started = the main plan, or (KD-24) the App plan of a brand that only uses the App channel. The App plan is not
        // read for a tenant the channel is not open for, so its answer is unchanged.
        var started = (await Rows(connection, null, """
                SELECT BIN_TO_UUID(site_id) FROM collection_schedules
                WHERE tenant_id = UUID_TO_BIN(@tenant) AND name IN (@name, @app) AND enabled = TRUE;
                """, cancellationToken, ("@tenant", tenantId.ToString()), ("@name", PromptMonitoringIdentity.ScheduleName),
                ("@app", appOpen ? PromptMonitoringIdentity.AppScheduleName : PromptMonitoringIdentity.ScheduleName)))
            .Select(r => Guid.Parse(r[0])).ToHashSet();
        var migrated = await AnswerModeSchema.IsMigratedAsync(connection, null, cancellationToken);
        var targets = (await Rows(connection, null, $"""
                SELECT BIN_TO_UUID(site_id), provider, engine, {(migrated ? "answer_mode, frequency" : "NULL, 'daily'")}
                FROM site_targets WHERE tenant_id = UUID_TO_BIN(@tenant) AND enabled = TRUE;
                """, cancellationToken, ("@tenant", tenantId.ToString())))
            .Select(r => (Site: Guid.Parse(r[0]), Cell: StoredCell(r[1], r[2], r[3], r[4])))
            // design 6.1: no App cells for a tenant the App channel is not open for.
            .Where(r => r.Cell is not null && (appOpen || !MonitoringProviders.IsApp(r.Cell.Provider)))
            .ToLookup(r => r.Site, r => r.Cell!);
        return new TeamMonitoringConfigView(version, false, cells.Select(CellView).ToArray(), brands.Select(row =>
        {
            var siteId = Guid.Parse(row[0]);
            // Several regions of one model collapse into one cell (the signature ignores country / locale).
            var brandCells = InCatalogOrder(targets[siteId].DistinctBy(c => c.Signature)).Select(CellView).ToArray();
            return new TeamMonitoringBrandView(siteId, row[1], Bool(row[2]), started.Contains(siteId), uint.Parse(row[3]), brandCells);
        }).ToArray());
    }

    /// <inheritdoc />
    public async Task<TeamMonitoringConfigView> PutTeamMonitoringConfigAsync(Guid tenantId, Guid userId, TeamMonitoringConfigWrite request,
        CancellationToken cancellationToken = default)
    {
        var cells = InCatalogOrder(ValidateTeamCells(request?.Cells)).ToArray();
        var appOpen = AppOpen(tenantId);
        var catalog = await GetTargetCatalogAsync(cancellationToken, tenantId);
        EnsureInCatalog(cells, catalog, appOpen, appOpen ? await SavedAppCellEnginesAsync(tenantId, cells, catalog, cancellationToken) : null);
        var affected = new List<Guid>();
        await using (var connection = database.CreateConnection())
        {
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await TeamMonitoringSchema.RequireAsync(connection, transaction, cancellationToken);
            await GeminiResultSchema.RequireAsync(connection, transaction, cancellationToken);
            var migrated = await AnswerModeSchema.IsMigratedAsync(connection, transaction, cancellationToken);
            if (!migrated && cells.Any(c => c.StoredMode is not null || c.Frequency != MonitoringFrequency.Daily))
                throw AnswerModeSchema.Missing();
            // tenants serializes team saves and follow-default of one team; every later read below is a locking read.
            if (await LockTenant(connection, transaction, tenantId, cancellationToken) is null)
                throw Error("RESOURCE_NOT_FOUND", "团队不存在。", 404);
            var current = await Row(connection, transaction,
                "SELECT config_version FROM tenant_monitoring_defaults WHERE tenant_id = UUID_TO_BIN(@tenant) FOR UPDATE;",
                cancellationToken, ("@tenant", tenantId.ToString()));
            if (request!.ExpectedVersion != (current is null ? 0u : uint.Parse(current[0])))
                throw Error("CONFIG_VERSION_CONFLICT", "团队默认监控配置已变化，请刷新后重试。", 409);
            await Exec(connection, transaction, """
                INSERT INTO tenant_monitoring_defaults (tenant_id, config_version, updated_by, created_at, updated_at)
                VALUES (UUID_TO_BIN(@tenant), 1, UUID_TO_BIN(@user), UTC_TIMESTAMP(6), UTC_TIMESTAMP(6))
                ON DUPLICATE KEY UPDATE config_version = config_version + 1, updated_by = VALUES(updated_by), updated_at = UTC_TIMESTAMP(6);
                """, cancellationToken, ("@tenant", tenantId.ToString()), ("@user", userId.ToString()));
            // A tenant the App channel is not open for cannot see or submit App cells: stored ones are left as they are.
            await Exec(connection, transaction, "DELETE FROM tenant_monitoring_default_cells WHERE tenant_id = UUID_TO_BIN(@tenant)"
                + (appOpen ? ";" : " AND provider <> @app;"),
                cancellationToken, ("@tenant", tenantId.ToString()), ("@app", AppChannelDefinition.Provider));
            foreach (var cell in cells)
                await Exec(connection, transaction, """
                    INSERT INTO tenant_monitoring_default_cells (tenant_id, provider, engine, answer_mode, frequency)
                    VALUES (UUID_TO_BIN(@tenant), @provider, @engine, @mode, @frequency);
                    """, cancellationToken, ("@tenant", tenantId.ToString()), ("@provider", cell.Provider), ("@engine", cell.Engine),
                    ("@mode", cell.StoredMode ?? ""), ("@frequency", MonitoringFrequencyRules.ToStorage(cell.Frequency)));
            // Locking read of the following brands (latest committed flags): a brand saved individually before this point is
            // skipped; one saved after it waits for these locks and then fails its version check (409).
            var followers = await Rows(connection, transaction, """
                SELECT BIN_TO_UUID(s.id) FROM sites s JOIN site_brand_settings b ON b.site_id = s.id AND b.tenant_id = s.tenant_id
                WHERE s.tenant_id = UUID_TO_BIN(@tenant) AND s.status = 'active' AND b.monitoring_follows_default = 1
                ORDER BY s.id FOR UPDATE;
                """, cancellationToken, ("@tenant", tenantId.ToString()));
            foreach (var siteId in followers.Select(r => Guid.Parse(r[0])))
            {
                var site = await LockSite(connection, transaction, tenantId, siteId, cancellationToken);
                if (site[2] != "active") continue;
                await ApplyTeamCellsAsync(connection, transaction, tenantId, siteId, site, cells, catalog, migrated, appOpen, cancellationToken);
                affected.Add(siteId);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        foreach (var siteId in affected) await ReconcileNightAfterSaveAsync(tenantId, siteId, cancellationToken);
        return await GetTeamMonitoringConfigAsync(tenantId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<SiteTargetsView> FollowTeamDefaultAsync(Guid tenantId, Guid siteId, FollowTeamDefaultRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null) throw Error("CONFIG_VERSION_REQUIRED", "缺少 expectedVersion。", 400);
        var appOpen = AppOpen(tenantId);
        var catalog = await GetTargetCatalogAsync(cancellationToken, tenantId);
        await using (var connection = database.CreateConnection())
        {
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await TeamMonitoringSchema.RequireAsync(connection, transaction, cancellationToken);
            await GeminiResultSchema.RequireAsync(connection, transaction, cancellationToken);
            var migrated = await AnswerModeSchema.IsMigratedAsync(connection, transaction, cancellationToken);
            await LockTenant(connection, transaction, tenantId, cancellationToken);
            var site = await LockSite(connection, transaction, tenantId, siteId, cancellationToken);
            if (site[2] != "active") throw Error("RESOURCE_NOT_FOUND", "工作区已归档。", 404);
            if (request.ExpectedVersion != uint.Parse(site[9])) throw Error("CONFIG_VERSION_CONFLICT", "Target 配置版本已变化。", 409);
            var (_, _, cells) = await ReadTeamDefaultAsync(connection, transaction, tenantId, true, appOpen, cancellationToken);
            if (!migrated && cells.Any(c => c.StoredMode is not null || c.Frequency != MonitoringFrequency.Daily))
                throw AnswerModeSchema.Missing();
            await ApplyTeamCellsAsync(connection, transaction, tenantId, siteId, site, cells, catalog, migrated, appOpen, cancellationToken);
            await Exec(connection, transaction, "UPDATE site_brand_settings SET monitoring_follows_default = 1 WHERE site_id = UUID_TO_BIN(@site);",
                cancellationToken, ("@site", siteId.ToString()));
            await transaction.CommitAsync(cancellationToken);
        }
        await ReconcileNightAfterSaveAsync(tenantId, siteId, cancellationToken);
        return await GetSiteTargetsAsync(tenantId, siteId, cancellationToken);
    }

    // Materializes the cells for one locked brand and writes them through the single site-target write path (scope = monitoring).
    private static async Task ApplyTeamCellsAsync(MySqlConnection connection, MySqlTransaction transaction, Guid tenantId, Guid siteId,
        string[] site, IReadOnlyList<NormalizedTeamCell> cells, IReadOnlyList<CollectionTargetView> catalog, bool migrated, bool appOpen,
        CancellationToken cancellationToken)
    {
        // Same anchor zone as PUT /targets: the main plan's zone once it exists, otherwise the brand zone.
        var today = BrandLocalDate(await AutomaticMonitoringSelection.MainPlanTimeZoneAsync(connection, transaction, tenantId, siteId,
            cancellationToken) ?? site[5], DateTimeOffset.UtcNow);
        var existing = await ExistingTargetsAsync(connection, transaction, tenantId, siteId, true, cancellationToken);
        // Teams without App cells run no extra query; with App cells, a brand's already-enabled App targets survive their
        // platform becoming unavailable.
        var enabledApps = cells.Any(c => MonitoringProviders.IsApp(c.Provider))
            ? await EnabledAppEnginesAsync(connection, transaction, tenantId, siteId, cancellationToken) : null;
        var normalized = MaterializeCells(cells, existing, site[4], catalog, enabledApps).Select(NormalizeTarget).ToArray();
        await ApplySiteTargetsAsync(connection, transaction, tenantId, siteId, normalized, SiteTargetsScope.Monitoring, today, migrated,
            appOpen, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SiteTargetWrite>?> MaterializeTeamDefaultAsync(Guid tenantId, Guid siteId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        if (!await TeamMonitoringSchema.IsMigratedAsync(connection, null, cancellationToken)) return null;
        var brand = await Row(connection, null, """
            SELECT s.status, b.monitoring_follows_default, b.default_locale
            FROM sites s JOIN site_brand_settings b ON b.site_id = s.id AND b.tenant_id = s.tenant_id
            WHERE s.tenant_id = UUID_TO_BIN(@tenant) AND s.id = UUID_TO_BIN(@site);
            """, cancellationToken, ("@tenant", tenantId.ToString()), ("@site", siteId.ToString()));
        if (brand is null || brand[0] != "active" || !Bool(brand[1])) return null;
        var (_, saved, cells) = await ReadTeamDefaultAsync(connection, null, tenantId, false, AppOpen(tenantId), cancellationToken);
        // A team that never saved a default keeps the historical baseline path (single ChatGPT target) in the caller.
        if (!saved) return null;
        var existing = await ExistingTargetsAsync(connection, null, tenantId, siteId, false, cancellationToken);
        if (existing.Any(r => r.Enabled)) return null;
        var enabledApps = cells.Any(c => MonitoringProviders.IsApp(c.Provider))
            ? await EnabledAppEnginesAsync(connection, null, tenantId, siteId, cancellationToken) : null;
        return MaterializeCells(cells, existing, brand[2], await GetTargetCatalogAsync(cancellationToken, tenantId), enabledApps);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TeamMonitoringBrandTargets>> MaterializeTeamCellsForFollowersAsync(Guid tenantId,
        IReadOnlyList<TeamMonitoringCellWrite> cells, CancellationToken cancellationToken = default)
    {
        var normalized = InCatalogOrder(ValidateTeamCells(cells, enforceMonitoringLimit: false)).ToArray();
        var appOpen = AppOpen(tenantId);
        var catalog = await GetTargetCatalogAsync(cancellationToken, tenantId);
        EnsureInCatalog(normalized, catalog, appOpen, appOpen ? await SavedAppCellEnginesAsync(tenantId, normalized, catalog, cancellationToken) : null);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await TeamMonitoringSchema.RequireAsync(connection, null, cancellationToken);
        var followers = await Rows(connection, null, """
            SELECT BIN_TO_UUID(s.id), s.display_name, b.default_locale
            FROM sites s JOIN site_brand_settings b ON b.site_id = s.id AND b.tenant_id = s.tenant_id
            WHERE s.tenant_id = UUID_TO_BIN(@tenant) AND s.status = 'active' AND b.monitoring_follows_default = 1
            ORDER BY s.created_at, s.id;
            """, cancellationToken, ("@tenant", tenantId.ToString()));
        var result = new List<TeamMonitoringBrandTargets>(followers.Count);
        foreach (var row in followers)
        {
            var siteId = Guid.Parse(row[0]);
            var existing = await ExistingTargetsAsync(connection, null, tenantId, siteId, false, cancellationToken);
            var enabledApps = normalized.Any(c => MonitoringProviders.IsApp(c.Provider))
                ? await EnabledAppEnginesAsync(connection, null, tenantId, siteId, cancellationToken) : null;
            result.Add(new(siteId, row[1], MaterializeCells(normalized, existing, row[2], catalog, enabledApps)));
        }
        return result;
    }
}
