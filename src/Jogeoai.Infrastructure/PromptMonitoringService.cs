using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Providers.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using System.Globalization;

namespace Jogeoai.Infrastructure;

/// <summary>复用现有 Target、计划和 Run；无需新增业务表。</summary>
/// <param name="database">业务数据库。</param>
/// <param name="management">品牌、提问与站点目标。</param>
/// <param name="schedules">采集计划。</param>
/// <param name="runs">采集运行。</param>
/// <param name="providerSettings">可选 API 回答 Provider 设置；提供时，目标预估按 Provider 当前全局配置检查回答模式能否采集，
/// 立即采集也据此预检豆包、DeepSeek 等 Provider 是否已配置。</param>
/// <param name="keyPool">可选 Cloro key 池；提供时，含 Cloro 目标且池耗尽的立即采集在预留积分前跳过（与时隙预检相同）。</param>
/// <param name="appChannel">可选的 App 渠道开放范围（Collector:AppTenantIds）；不提供时 App 渠道不对任何租户开放。</param>
public sealed class PromptMonitoringService(MySqlDatabase database, IManagementStore management,
    ICollectionScheduleStore schedules, ICloroVisibilityStore runs, IAnswerProviderSettingsStore? providerSettings = null,
    ICloroKeyPool? keyPool = null, IOptions<AppChannelOptions>? appChannel = null) : IPromptMonitoringService
{
    // Same switch as ManagementStore: closed for everyone unless the tenant is listed in Collector:AppTenantIds.
    private bool AppOpen(Guid tenantId) => appChannel?.Value.IsOpen(tenantId) == true;

    private const int MonitoringLockSeconds = 5;
    // Same-day retries of an identical pair set after the earlier instant run failed get a numbered key suffix.
    private const int MaxInstantKeyAttempts = 10;
    /// <inheritdoc />
    public async Task<PromptMonitoringView> EnsureAsync(Guid tenantId, Guid siteId,
        CancellationToken cancellationToken = default)
    {
        var brand = await RequireBrand(tenantId, siteId, cancellationToken);
        // MySQL 命名锁跨 API 实例串行化启动；连接持有期间显式释放，避免池化连接遗留锁。
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var lockName = $"prompt-monitor:{siteId:N}";
        await using var acquire = new MySqlCommand("SELECT GET_LOCK(@name, 5);", connection);
        acquire.Parameters.AddWithValue("@name", lockName);
        try
        {
            if (Convert.ToInt32(await acquire.ExecuteScalarAsync(cancellationToken)) != 1)
                throw new CollectorStoreException("MONITORING_BUSY", "监控正在初始化，请稍后重试。", 409);
            var schedule = await FindSchedule(tenantId, siteId, cancellationToken);
            if (schedule is not null)
            {
                await AutomaticMonitoringSelection.ReconcileAsync(database, tenantId, siteId, cancellationToken);
                // The App plan is reconciled at the same moment (created when the brand now has App targets, snapshot, pause).
                await ReconcileAppPlanAsync(tenantId, siteId, cancellationToken);
                return await View(tenantId, siteId, await FindSchedule(tenantId, siteId, cancellationToken), cancellationToken);
            }
            // KD-24: a brand may monitor through the App plan alone. With an App plan and no main plan this is not a first
            // start: the brand keeps its own targets (no team default, no baseline), and the main plan is only added below
            // once the brand has an enabled API target. Always null for a tenant the App channel is not open for.
            var appOpen = AppOpen(tenantId);
            var appPlan = appOpen
                ? await FindSchedule(tenantId, siteId, cancellationToken, PromptMonitoringIdentity.AppScheduleName) : null;
            var prompts = new List<PromptView>();
            string? cursor = null;
            do
            {
                var page = await management.ListPromptsAsync(tenantId, siteId, null, null, cursor, 100, cancellationToken);
                prompts.AddRange(page.Items.Where(p => p.Enabled && p.ArchivedAt is null));
                cursor = page.NextCursor;
            } while (cursor is not null);
            if (prompts.Count == 0)
            {
                if (appPlan is null) return new PromptMonitoringView([], null, null);
                await ReconcileAppPlanAsync(tenantId, siteId, cancellationToken);
                return await View(tenantId, siteId, null, cancellationToken);
            }
            if (prompts.Count > 200)
                throw new CollectorStoreException("MONITORING_TOO_LARGE", "自动监控每个品牌最多支持 200 个问题。", 400);

            var catalog = await management.GetTargetCatalogAsync(cancellationToken);
            var targets = await management.GetSiteTargetsAsync(tenantId, siteId, cancellationToken);
            // A following brand of a team with a saved default and no enabled monitoring target starts with the team default
            // (internal write: the follow flag is kept). Otherwise — virtual baseline, unmigrated 048 — the historical rule below.
            if (appPlan is null
                && await management.MaterializeTeamDefaultAsync(tenantId, siteId, cancellationToken) is { Count: > 0 } teamDefault)
            {
                targets = await management.PutSiteTargetsAsync(tenantId, siteId, new SiteTargetsWrite(
                    targets.ConfigVersion, teamDefault, SiteTargetsScope.Monitoring), cancellationToken);
            }
            else if (appPlan is null && targets.Items.Count == 0)
            {
                // 首次引导没有目标选择步骤；地区采用 Provider 目录，中文问题仍保持原文。
                targets = await management.PutSiteTargetsAsync(tenantId, siteId, new SiteTargetsWrite(
                    targets.ConfigVersion, [PromptMonitoringDefaults.Target(catalog, brand.DefaultLocale)]), cancellationToken);
            }
            var selected = SelectMonitoringTargets(targets.Items);
            // KD-24: the App plan does not depend on the API side. It is ensured first, so that a refusal of the API side
            // below (provider unavailable, too large) does not keep App monitoring from starting; it never gets a run here.
            // App rows are only listed for a tenant the App channel is open for.
            var appSide = appPlan is not null || appOpen && targets.Items.Any(t => t.Enabled && MonitoringProviders.IsApp(t.Provider));
            if (appSide)
            {
                await AppMonitoringPlan.ReconcileAsync(database, tenantId, siteId, true, true, cancellationToken);
                // App only: no main plan, no initial run, no baseline target.
                if (selected.Length == 0) return await View(tenantId, siteId, null, cancellationToken);
            }
            if (selected.Length == 0)
                throw new CollectorStoreException("MONITORING_TARGETS_REQUIRED", "请启用至少一个可用于每日监控的采集目标。", 409);
            if (selected.Any(t => !catalog.Any(c => c.Provider == t.Provider && c.Engine == t.Engine && c.Availability == "available")))
                throw new CollectorStoreException("MONITORING_PROVIDER_UNAVAILABLE", "监控服务当前不可用，请联系管理员检查采集配置。", 503);
            // Same matrix as the daily slot: 'all' prompts × every target, custom prompts × their enabled scope.
            // Without custom prompts this is exactly the historical cartesian selection.
            var scopes = await PromptScopes(tenantId, siteId, cancellationToken);
            var selection = MonitoringMatrix.BuildSelection(
                prompts.Select(p => scopes.GetValueOrDefault(p.PromptId) ?? new MonitoringPromptScope(p.PromptId)).ToArray(),
                selected.Select(ManagementStore.MonitoringTarget)
                    .OrderBy(CollectionIdentity.TargetKey).ToArray(), 1);
            // No prompt is collected on an enabled API target. With an App side this is a valid state (every prompt scoped
            // to App targets): there is no main plan to create. Without one it is refused as before.
            if (selection.PromptIds.Count == 0 && appSide) return await View(tenantId, siteId, null, cancellationToken);
            if (selection.PromptIds.Count == 0)
                throw new CollectorStoreException("MONITORING_TARGETS_REQUIRED", "已启用问题所选的 AI 平台均未启用，请启用平台或调整问题的平台范围。", 409);
            if (selection.Targets.Count > 20 || MonitoringMatrix.CountChecks(selection) > MonitoringMatrix.MaxChecks)
                throw new CollectorStoreException("MONITORING_TOO_LARGE", "监控目标最多 20 个，每轮最多 1000 次检测。", 400);
            // Pause is one brand-level switch: a brand whose App plan is paused gets its main plan paused as well, without an
            // initial run. Without an App plan (every API-only brand) the plan is created enabled, as always.
            var paused = appPlan is { Enabled: false };
            schedule = await schedules.CreateAsync(new(tenantId, siteId, PromptMonitoringIdentity.ScheduleName,
                "daily", null, PromptMonitoringIdentity.MainLocalTime, brand.TimeZone, selection, !paused), cancellationToken);

            // The first run is created only when this monitoring plan is first
            // created. Once the plan exists, a refresh or a re-login must not
            // turn a missed/insufficient day into an ad-hoc catch-up run; the
            // next scheduled slot owns the next attempt.
            if (!paused && !await HasMonitoringRunForLocalDateAsync(tenantId, siteId, schedule, cancellationToken))
            {
                var initialKey = PromptMonitoringIdentity.InitialRunKey(siteId, brand.SubjectConfigVersion, prompts, selection);
                try
                {
                    await runs.CreateRunAsync(new CreateCloroVisibilityRunCommand(tenantId, siteId, selection.PromptIds,
                        selection.Targets, initialKey, $"prompt-monitor:{siteId:N}", RequireEnabledTargets: true,
                        ChargeMonitoringCredits: true, PromptTargetKeys: selection.PromptTargets), cancellationToken);
                }
                catch (CollectorStoreException exception) when (exception.StatusCode == 402)
                {
                    // Keep the plan visible so the UI can show the server-side
                    // estimate and insufficient balance; the next slot retries.
                }
            }
            // twice_daily cells also get the 00:05 night plan; it never gets an initial or catch-up run. Runs after the
            // initial run and is best effort: once the main plan exists a later Ensure never creates the initial run, so a
            // failure here must not abort Ensure. The lazy ReconcileAsync (before each slot, on GET/POST) repeats it.
            try { await AutomaticMonitoringSelection.ReconcileNightScheduleAsync(database, tenantId, siteId, cancellationToken); }
            catch (Exception exception) when (exception is CollectorStoreException or System.Data.Common.DbException) { }
            // The App plan now follows the new main plan (pause flag, zone). Best effort like the night plan; the lazy
            // reconcile repeats it.
            await ReconcileAppPlanAsync(tenantId, siteId, cancellationToken);
            return await View(tenantId, siteId, schedule, cancellationToken);
        }
        finally
        {
            await using var release = new MySqlCommand("SELECT RELEASE_LOCK(@name);", connection);
            release.Parameters.AddWithValue("@name", lockName);
            await release.ExecuteScalarAsync(CancellationToken.None);
        }
    }

    /// <inheritdoc />
    public async Task<PromptMonitoringView> GetAsync(Guid tenantId, Guid siteId, CancellationToken cancellationToken = default)
    {
        await RequireBrand(tenantId, siteId, cancellationToken);
        var schedule = await FindSchedule(tenantId, siteId, cancellationToken);
        if (schedule is not null)
        {
            await AutomaticMonitoringSelection.ReconcileAsync(database, tenantId, siteId, cancellationToken);
            schedule = await FindSchedule(tenantId, siteId, cancellationToken);
        }
        // Lazy App plan reconcile, with or without a main plan (a no-op for a tenant the App channel is not open for).
        await ReconcileAppPlanAsync(tenantId, siteId, cancellationToken);
        return await View(tenantId, siteId, schedule, cancellationToken);
    }

    // Lazy reconcile of the App plan (design 4.1). Best effort: a failure must not fail the read or the start of the daily
    // monitoring; the next lazy moment repeats it. It only creates the plan for a brand that already has its main plan.
    private async Task ReconcileAppPlanAsync(Guid tenantId, Guid siteId, CancellationToken ct)
    {
        if (!AppOpen(tenantId)) return;
        try { await AppMonitoringPlan.ReconcileAsync(database, tenantId, siteId, true, false, ct); }
        catch (Exception exception) when (exception is CollectorStoreException or System.Data.Common.DbException) { }
    }

    /// <inheritdoc />
    public async Task<PromptMonitoringEstimateView> EstimateAsync(Guid tenantId, Guid siteId, PromptMonitoringEstimateRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null || request.NewPromptCount is < 0 or > MonitoringMatrix.MaxPrompts)
            throw new CollectorStoreException("INVALID_ESTIMATE", "newPromptCount 必须为 0–200。", 400);
        await RequireBrand(tenantId, siteId, cancellationToken);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        // One read-only snapshot for prompts, scopes, targets, plan sample count, plan usage and balance.
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        // An empty list is "no platform selected" (reported as MONITORING_TARGETS_REQUIRED), not a validation error.
        var scope = request.TargetKeys is { Count: > 0 } keys
            ? await PromptMonitoringScale.ValidateTargetKeysAsync(connection, transaction, tenantId, siteId, keys, cancellationToken,
                AppOpen(tenantId))
            : request.TargetKeys;
        var snapshot = await PromptMonitoringScale.ReadAsync(connection, transaction, tenantId, siteId, cancellationToken);
        var (limit, current) = await SubscriptionLimitGuard.ReadPromptUsageAsync(connection, transaction, tenantId, cancellationToken);
        await using var balance = new MySqlCommand("""
            SELECT available_credits FROM credit_accounts WHERE tenant_id = UUID_TO_BIN(@tenant);
            """, connection, transaction);
        balance.Parameters.AddWithValue("@tenant", tenantId.ToString());
        var value = await balance.ExecuteScalarAsync(cancellationToken);
        var available = value is null || value is DBNull ? 0m : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        // KD-24: a brand that only has App targets is not "without a monitoring platform". Only read when it has no API target.
        var appTargetCount = snapshot.Targets.Count == 0 && AppOpen(tenantId)
            ? (await AppMonitoringPlan.TargetsAsync(connection, transaction, tenantId, siteId, cancellationToken)).Count : 0;
        await transaction.CommitAsync(cancellationToken);
        return PromptMonitoringScale.Estimate(snapshot, request.NewPromptCount, scope, available, limit, current, appTargetCount);
    }

    /// <inheritdoc />
    public async Task<MonitoringTargetsEstimateView> EstimateTargetsAsync(Guid tenantId, Guid siteId, MonitoringTargetsEstimateRequest request,
        CancellationToken cancellationToken = default)
    {
        // Same validation as PUT /targets except that the 20-target limit is a blocker, not a 400.
        var normalized = ManagementStore.ValidateSiteTargetsWrite(new SiteTargetsWrite(0, request?.Targets!), enforceMonitoringLimit: false);
        var brand = await RequireBrand(tenantId, siteId, cancellationToken);
        var appOpen = AppOpen(tenantId);
        var catalog = await management.GetTargetCatalogAsync(cancellationToken, tenantId);
        // Same App rule as PUT /targets: 400 for a tenant the App channel is not open for, 503 before 049, 400 for newly
        // enabling an unavailable platform (a target the brand already has enabled does not fail the estimate).
        var appTargets = normalized.Where(t => t.App).Select(t => (t.Write.Engine, t.Write.Enabled)).ToArray();
        ManagementStore.EnsureAppTargetsAllowed(appTargets, catalog, appOpen, appOpen
            ? await ManagementStore.EnabledAppEnginesIfNeededAsync(database, tenantId, siteId, appTargets, catalog, cancellationToken) : null);
        foreach (var target in normalized)
            if (!catalog.Any(c => c.Provider == target.Write.Provider && c.Engine == target.Write.Engine))
                throw new CollectorStoreException("TARGET_NOT_ALLOWED", "目标不在 Provider 目录中。", 400);
        // App drafts are not Eligible: every figure below counts the API cells only (the App part of the estimate is added
        // with the per-channel breakdown). They only keep MONITORING_TARGETS_REQUIRED from being reported (see below).
        var drafts = normalized.Where(t => t.Eligible && t.Write.Enabled).ToArray();
        var unsupported = await RuntimeUnsupportedKeysAsync(drafts, cancellationToken);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        // One read-only snapshot for prompts, scopes, current targets (anchors), plan sample count and balance.
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        var snapshot = await PromptMonitoringScale.ReadAsync(connection, transaction, tenantId, siteId, cancellationToken);
        var existing = await AutomaticMonitoringSelection.Targets(connection, transaction, tenantId, siteId, false, cancellationToken);
        await using var balance = new MySqlCommand("""
            SELECT available_credits FROM credit_accounts WHERE tenant_id = UUID_TO_BIN(@tenant);
            """, connection, transaction);
        balance.Parameters.AddWithValue("@tenant", tenantId.ToString());
        var value = await balance.ExecuteScalarAsync(cancellationToken);
        var available = value is null || value is DBNull ? 0m : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        // Slots (and the anchors PUT writes) use the main plan's zone once it exists; the brand zone only before that.
        var planZone = await AutomaticMonitoringSelection.MainPlanTimeZoneAsync(connection, transaction, tenantId, siteId, cancellationToken);
        // implement 8.1: the App part is only read for a draft with an enabled App cell (an open tenant after 049, see the
        // check above); every other draft runs exactly the reads it ran before and gets the API estimate unchanged.
        var appEngines = normalized.Where(t => t.App && t.Write.Enabled).Select(t => t.Write.Engine).ToArray();
        AppMonitoringEstimate.BrandPrompts? appPrompts = null;
        AppCapacityQuery.Snapshot? appCapacity = null;
        IReadOnlyList<AutomaticMonitoringSelection.Target> appStored = [];
        if (appEngines.Length > 0)
        {
            appPrompts = await AppMonitoringEstimate.BrandPromptsAsync(connection, transaction, tenantId, siteId, cancellationToken);
            appStored = await AppMonitoringPlan.TargetsAsync(connection, transaction, tenantId, siteId, cancellationToken);
            appCapacity = await AppCapacityQuery.ReadAsync(connection, transaction, appChannel?.Value.OpenTenantIds() ?? [], cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        var zone = BrandZone(planZone ?? brand.TimeZone);
        var now = DateTimeOffset.UtcNow;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var cells = PromptMonitoringScale.ProjectCells(drafts, existing, today);
        // KD-24: enabled App drafts decide whether MONITORING_TARGETS_REQUIRED is reported; every API figure stays API-only.
        var appDrafts = normalized.Where(t => t.App && t.Write.Enabled)
            .Select(t => AutomaticMonitoringSelection.StoredTarget(t.Write.Provider, t.Write.Engine, t.Write.Locale, t.Write.Country, null)).ToArray();
        var estimate = PromptMonitoringScale.EstimateTargets(snapshot, cells, unsupported, now, zone, available, appDrafts);
        if (appPrompts is null || appCapacity is null) return estimate;
        // Anchors as PUT writes them (design 3.5): an App target that is already enabled keeps its anchor, a newly enabled
        // one starts its first weekly cycle tomorrow.
        var anchors = new Dictionary<string, DateOnly>(StringComparer.Ordinal);
        foreach (var target in appStored)
            if (target.Anchor is { } anchor) anchors.TryAdd(target.Value.Engine, anchor);
        var appCells = appEngines.Select(engine => new AppMonitoringEstimate.AppCell(engine,
            anchors.TryGetValue(engine, out var anchor) ? anchor : today.AddDays(1))).ToArray();
        return AppMonitoringEstimate.Combine(estimate, appPrompts, appCells,
            (engine, demand) => appCapacity.ForBrand(engine, tenantId, siteId, demand), now, zone);
    }

    /// <inheritdoc />
    public async Task<TeamMonitoringEstimateView> EstimateTeamMonitoringAsync(Guid tenantId, TeamMonitoringEstimateRequest request,
        CancellationToken cancellationToken = default)
    {
        // Validation as PUT team/monitoring-config (the 20-cell limit becomes a per-brand blocker); 503 before 048.
        var brands = await management.MaterializeTeamCellsForFollowersAsync(tenantId, request?.Cells!, cancellationToken);
        var rows = new List<TeamMonitoringBrandEstimate>(brands.Count);
        foreach (var brand in brands)
        {
            // Exactly the brand target estimate on the brand's materialized draft (anchors, scopes, sample count, next slot).
            var estimate = await EstimateTargetsAsync(tenantId, brand.SiteId, new MonitoringTargetsEstimateRequest(brand.Targets), cancellationToken);
            // The brand's channel breakdown is carried over as it is; it is null (and not written) without App cells.
            rows.Add(new(brand.SiteId, brand.DisplayName, estimate.PromptCount, estimate.WeeklyChecks, estimate.WeeklyCredits,
                estimate.NextSlotChecks, estimate.NextSlotCredits, estimate.Blockers, estimate.Channels));
        }
        var balance = await AvailableCredits(tenantId, cancellationToken);
        var nextSlotCredits = rows.Sum(r => r.NextSlotCredits);
        return new TeamMonitoringEstimateView(PromptMonitoringPricing.UnitPrice, balance, rows.Sum(r => r.WeeklyChecks),
            rows.Sum(r => r.WeeklyCredits), nextSlotCredits, Math.Max(0m, nextSlotCredits - balance), rows);
    }

    /// <inheritdoc />
    public Task<InstantCollectionView?> CollectNewPromptsAsync(Guid tenantId, Guid siteId, IReadOnlyList<Guid> promptIds,
        CancellationToken cancellationToken = default)
    {
        if (promptIds is not { Count: > 0 }) return Task.FromResult<InstantCollectionView?>(null);
        // Every effective cell of each new prompt (TargetKeys null); prompts that are no longer enabled are dropped later.
        var requests = promptIds.Where(id => id != Guid.Empty).Distinct().Select(id => new InstantCollectionRequest(id)).ToArray();
        return requests.Length == 0 ? Task.FromResult<InstantCollectionView?>(null) : CollectAsync(tenantId, siteId, requests, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<InstantCollectionView?> CollectAddedTargetsAsync(Guid tenantId, Guid siteId, PromptView before, PromptView after,
        CancellationToken cancellationToken = default)
    {
        if (before is null || after is null || before.PromptId != after.PromptId
            || !before.Enabled || before.ArchivedAt is not null || !after.Enabled || after.ArchivedAt is not null)
            return null;
        IReadOnlyList<string> added;
        try
        {
            // Both sides use the same current set of enabled monitoring cells.
            await using var connection = database.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            var cells = (await AutomaticMonitoringSelection.Targets(connection, null, tenantId, siteId, false, cancellationToken))
                .Select(t => CollectionIdentity.TargetKey(t.Value)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            added = InstantCollectionPlanner.AddedTargetKeys(before, after, cells);
        }
        catch (CollectorStoreException exception) { return InstantSkipped(exception.Code, exception.Message); }
        if (added.Count == 0) return null;
        return await CollectAsync(tenantId, siteId, [new InstantCollectionRequest(after.PromptId, added)], cancellationToken);
    }

    // Shared instant-collection flow (design §3). Business failures never throw: CollectorStoreException → skipped.
    private async Task<InstantCollectionView?> CollectAsync(Guid tenantId, Guid siteId, IReadOnlyList<InstantCollectionRequest> requests,
        CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            await RequireBrand(tenantId, siteId, ct);
            var schedule = await FindSchedule(tenantId, siteId, ct);
            if (schedule is null)
            {
                // No plan yet (e.g. the brand's first prompts): Ensure creates the plan and the initial run, which already covers
                // every enabled prompt. Ensure takes the brand monitoring lock itself, so it is called before ours.
                var ensured = await EnsureAsync(tenantId, siteId, ct);
                if (ensured.Run is { } initial && initial.CreatedAt >= startedAt.AddTicks(-10))
                    return InstantStarted(initial.RunId, initial.TotalChecks);
                // An initial run refused for credits (402) leaves the plan: continue, the run below reports the shortfall.
                schedule = await FindSchedule(tenantId, siteId, ct);
                // A brand that only monitors through the App plan has no main plan and nothing to collect instantly: the
                // same result as before the App plan existed (Ensure refused with this code), never a main plan or baseline.
                if (schedule is null && ensured.AppSchedule is not null)
                    return InstantSkipped("MONITORING_TARGETS_REQUIRED", "请启用至少一个可用于每日监控的采集目标。");
                if (schedule is null) return null;
            }
            if (!schedule.Enabled) return InstantPaused();
            return await CollectLockedAsync(tenantId, siteId, requests, ct);
        }
        catch (CollectorStoreException exception) { return InstantSkipped(exception.Code, exception.Message); }
    }

    private async Task<InstantCollectionView?> CollectLockedAsync(Guid tenantId, Guid siteId, IReadOnlyList<InstantCollectionRequest> requests,
        CancellationToken ct)
    {
        // Same named lock as EnsureAsync and the slot worker: plan creation, slot runs and instant runs of one brand are serialized.
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(ct);
        var lockName = $"prompt-monitor:{siteId:N}";
        await using (var acquire = new MySqlCommand("SELECT GET_LOCK(@name, @wait);", connection))
        {
            acquire.Parameters.AddWithValue("@name", lockName);
            acquire.Parameters.AddWithValue("@wait", MonitoringLockSeconds);
            var acquired = await acquire.ExecuteScalarAsync(ct);
            if (acquired is null or DBNull || Convert.ToInt32(acquired, CultureInfo.InvariantCulture) != 1)
                return InstantSkipped("MONITORING_BUSY", "品牌监控正在初始化或提交采集，本次未立即采集，将在下一个时隙按正常规则采集。");
        }
        try
        {
            var schedule = await FindSchedule(tenantId, siteId, ct);
            if (schedule is null) return null;
            if (!schedule.Enabled) return InstantPaused();
            // Enabled prompts with their scope and every enabled monitoring cell; no frequency filter (instant ignores due dates).
            var prompts = await AutomaticMonitoringSelection.Prompts(connection, null, tenantId, siteId, ct);
            var targets = (await AutomaticMonitoringSelection.Targets(connection, null, tenantId, siteId, false, ct))
                .Select(t => t.Value).OrderBy(CollectionIdentity.TargetKey, StringComparer.Ordinal).ToArray();
            var wanted = InstantCollectionPlanner.Plan(requests, prompts, targets);
            if (wanted.Count == 0)
                return InstantSkipped("MONITORING_TARGETS_REQUIRED", "提问没有可采集的 AI 平台（品牌已启用的可监控模型与提问的平台范围没有交集），未立即采集。");

            // Same-day dedup on the main plan's local day: initial, instant, main and night plan runs with a live or succeeded check.
            var zone = BrandZone(schedule.TimeZone);
            var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);
            var requestedBy = new List<string>
            {
                PromptMonitoringIdentity.InitialRequestedBy(siteId), PromptMonitoringIdentity.InstantRequestedBy(siteId), $"schedule:{schedule.Id:N}",
            };
            if (await NightScheduleIdAsync(connection, tenantId, siteId, ct) is { } nightId) requestedBy.Add($"schedule:{nightId:N}");
            var collected = await AutomaticMonitoringSelection.CollectedPairsAsync(connection, null, tenantId, siteId, requestedBy, localDate, zone, ct);
            var remaining = InstantCollectionPlanner.RemoveCollected(wanted, collected);
            if (remaining.Count == 0)
                return new InstantCollectionView(InstantCollectionStatus.AlreadyCollected, null, 0, 0m,
                    Message: "所选提问与模型今天已采集，未重复采集。");

            var sampleCount = Math.Clamp(schedule.Selection.SampleCount, 1, 10);
            var checks = (long)remaining.Count * sampleCount;
            var estimated = PromptMonitoringPricing.Estimate(checks);
            if (checks > MonitoringMatrix.MaxChecks)
                return InstantSkipped("MONITORING_TOO_LARGE", "本次立即采集超过每轮 1000 次检测的上限，未立即采集。", checks, estimated);
            var selection = InstantCollectionPlanner.BuildRun(remaining, targets, sampleCount);
            // Same pre-flight as the slots: an exhausted Cloro pool or an unconfigured API provider skips before any reservation.
            if (await CollectionScheduleStore.EvaluateProviderAvailabilityAsync(selection.Targets, keyPool, providerSettings, ct,
                    "本次立即采集") is { } unavailable)
                return InstantSkipped(unavailable.Code, unavailable.Message, checks, estimated);

            var baseKey = PromptMonitoringIdentity.InstantRunKey(siteId, localDate, remaining, sampleCount);
            CreateCloroVisibilityRunCommand? command = null;
            for (var attempt = 1; attempt <= MaxInstantKeyAttempts && command is null; attempt++)
            {
                // Dedup already removed live and succeeded pairs, so an existing run under this key only holds failed checks:
                // the same pairs may be collected again today under the next numbered key.
                var candidate = new CreateCloroVisibilityRunCommand(tenantId, siteId, selection.PromptIds, selection.Targets,
                    attempt == 1 ? baseKey : $"{baseKey}:{attempt}", PromptMonitoringIdentity.InstantRequestedBy(siteId), sampleCount,
                    RequireEnabledTargets: true, ChargeMonitoringCredits: true, PromptTargetKeys: selection.PromptTargets);
                try
                {
                    if (await runs.GetExistingRunByKeyAsync(candidate, ct) is null) command = candidate;
                }
                catch (CollectorStoreException exception) when (exception.Code == "RUN_IDEMPOTENCY_CONFLICT")
                {
                    // The key holds an earlier failed run whose request differs (e.g. a target's fields changed): also taken.
                }
            }
            if (command is null)
                return InstantSkipped("INSTANT_COLLECTION_FAILED", "今天对这些提问与模型的立即采集已多次失败，本次未再提交，将在下一个时隙按正常规则采集。",
                    checks, estimated);
            try
            {
                var created = await runs.CreateRunAsync(command, ct);
                return InstantStarted(created.RunId, created.TotalChecks);
            }
            catch (CollectorStoreException exception) when (exception.StatusCode == 402)
            {
                // The whole run was rolled back: no run, no reservation, no ledger entry.
                var available = await AvailableCredits(tenantId, ct);
                return new InstantCollectionView(InstantCollectionStatus.Skipped, null, (int)checks, estimated, "CREDIT_INSUFFICIENT",
                    $"余额不足，未立即采集：本次需要 {estimated:0.###} 积分，当前可用 {available:0.###} 积分；将在下一个时隙按正常规则采集。",
                    Math.Max(0m, estimated - available));
            }
        }
        finally
        {
            await using var release = new MySqlCommand("SELECT RELEASE_LOCK(@name);", connection);
            release.Parameters.AddWithValue("@name", lockName);
            await release.ExecuteScalarAsync(CancellationToken.None);
        }
    }

    private static async Task<Guid?> NightScheduleIdAsync(MySqlConnection connection, Guid tenantId, Guid siteId, CancellationToken ct)
    {
        await using var query = new MySqlCommand("""
            SELECT BIN_TO_UUID(id) FROM collection_schedules
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site) AND name=@name ORDER BY created_at, id LIMIT 1;
            """, connection);
        query.Parameters.AddWithValue("@tenant", tenantId.ToString());
        query.Parameters.AddWithValue("@site", siteId.ToString());
        query.Parameters.AddWithValue("@name", PromptMonitoringIdentity.NightScheduleName);
        return await query.ExecuteScalarAsync(ct) is string id ? Guid.Parse(id) : null;
    }

    private static InstantCollectionView InstantStarted(Guid runId, int checks)
    {
        var credits = PromptMonitoringPricing.Estimate((long)checks);
        return new InstantCollectionView(InstantCollectionStatus.Started, runId, checks, credits,
            Message: $"已开始采集：{checks} 次检测，约 {credits:0.###} 积分。");
    }

    private static InstantCollectionView InstantPaused()
        => InstantSkipped("MONITORING_PAUSED", "品牌监控已暂停，未立即采集。");

    private static InstantCollectionView InstantSkipped(string code, string message, long checks = 0, decimal credits = 0m)
        => new(InstantCollectionStatus.Skipped, null, (int)Math.Min(checks, int.MaxValue), credits, code, message);

    // Brand time zone as used by the monitoring plans; an invalid stored zone falls back to Asia/Shanghai (BrandLocalDate rule).
    private static TimeZoneInfo BrandZone(string? timeZone)
    {
        try { return CollectionScheduleCalendar.ParseTimeZone(timeZone ?? ""); }
        catch (CollectorStoreException) { return CollectionScheduleCalendar.ParseTimeZone("Asia/Shanghai"); }
    }

    // Draft cells whose mode cannot be applied with the provider's current global options (e.g. Doubao fast outside the
    // messages request mode). Unconfigured providers are an availability matter, not a mode blocker.
    private async Task<IReadOnlySet<string>> RuntimeUnsupportedKeysAsync(IReadOnlyList<ManagementStore.NormalizedSiteTarget> drafts,
        CancellationToken ct)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (providerSettings is null) return result;
        var options = new Dictionary<string, AnswerProviderOptions?>(StringComparer.Ordinal);
        foreach (var draft in drafts)
        {
            if (AnswerProviderDefinition.TryGet(draft.Write.Provider) is not { NotConfiguredCode: not null } definition) continue;
            if (!options.TryGetValue(definition.Provider, out var current))
            {
                try { current = (await providerSettings.GetConfigurationAsync(definition.Provider, ct))?.Options; }
                catch (CollectorStoreException exception) when (exception.Code == AnswerProviderSchema.NotMigratedCode) { current = null; }
                options[definition.Provider] = current;
            }
            var mode = draft.Write.AnswerMode ?? definition.DefaultAnswerMode;
            if (current is not null && !AnswerModeProfile.TryApply(definition, current, mode, out _)) result.Add(draft.Key);
        }
        return result;
    }

    private async Task<BrandView> RequireBrand(Guid tenantId, Guid siteId, CancellationToken ct)
    {
        var brand = await management.GetBrandAsync(tenantId, siteId, ct);
        if (brand is null || brand.Status != "active")
            throw new CollectorStoreException("RESOURCE_NOT_FOUND", "品牌不存在或已归档。", 404);
        return brand;
    }

    private async Task<CollectionScheduleView?> FindSchedule(Guid tenantId, Guid siteId, CancellationToken ct,
        string name = PromptMonitoringIdentity.ScheduleName)
    {
        string? cursor = null;
        do
        {
            var page = await schedules.ListAsync(tenantId, siteId, cursor, 100, ct);
            var schedule = page.Items.FirstOrDefault(s => s.Name == name);
            if (schedule is not null) return schedule;
            cursor = page.NextCursor;
        } while (cursor is not null);
        return null;
    }

    private async Task<bool> HasMonitoringRunForLocalDateAsync(Guid tenantId, Guid siteId,
        CollectionScheduleView schedule, CancellationToken ct)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZone);
        var localDate = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).Date;
        var localStart = new DateTime(localDate.Year, localDate.Month, localDate.Day, 0, 0, 0,
            DateTimeKind.Unspecified);
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(localStart, zone);
        var endUtc = TimeZoneInfo.ConvertTimeToUtc(localStart.AddDays(1), zone);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(ct);
        await using var query = new MySqlCommand("""
            SELECT EXISTS(
                SELECT 1 FROM visibility_runs
                WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site)
                  AND requested_by IN (@initial, @scheduled)
                  AND created_at >= @start AND created_at < @end);
            """, connection);
        query.Parameters.AddWithValue("@tenant", tenantId.ToString());
        query.Parameters.AddWithValue("@site", siteId.ToString());
        query.Parameters.AddWithValue("@initial", $"prompt-monitor:{siteId:N}");
        query.Parameters.AddWithValue("@scheduled", $"schedule:{schedule.Id:N}");
        query.Parameters.AddWithValue("@start", startUtc);
        query.Parameters.AddWithValue("@end", endUtc);
        return Convert.ToInt32(await query.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) == 1;
    }

    private async Task<PromptMonitoringView> View(Guid tenantId, Guid siteId, CollectionScheduleView? schedule, CancellationToken ct)
    {
        var promptSummary = await management.SummarizePromptsAsync(tenantId, siteId, ct);
        // The App plan next to the main plan (KD-24). Run and Billing below stay the API round only.
        var appSchedule = AppOpen(tenantId) ? await FindSchedule(tenantId, siteId, ct, PromptMonitoringIdentity.AppScheduleName) : null;
        if (schedule is null)
            return new PromptMonitoringView([], null, null,
                new PromptMonitoringBillingView(promptSummary.EnabledCount, 0, 1,
                    PromptMonitoringPricing.UnitPrice, 0, await AvailableCredits(tenantId, ct),
                    0, 0, 0, "not_started"), appSchedule);
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(ct);
        // The latest run also covers instant collections (new prompts / added models), so their progress shows in the banner.
        await using var latest = new MySqlCommand("""
            SELECT BIN_TO_UUID(id), created_at FROM visibility_runs
            WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site)
              AND requested_by IN (@initial, @scheduled, @instant)
              ORDER BY (status IN ('queued', 'running')) DESC, created_at DESC, id DESC LIMIT 1;
            """, connection);
        latest.Parameters.AddWithValue("@tenant", tenantId.ToString());
        latest.Parameters.AddWithValue("@site", siteId.ToString());
        latest.Parameters.AddWithValue("@initial", PromptMonitoringIdentity.InitialRequestedBy(siteId));
        latest.Parameters.AddWithValue("@scheduled", $"schedule:{schedule.Id:N}");
        latest.Parameters.AddWithValue("@instant", PromptMonitoringIdentity.InstantRequestedBy(siteId));
        string? id = null;
        DateTimeOffset? runCreatedAt = null;
        await using (var reader = await latest.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                id = reader.GetString(0);
                runCreatedAt = Utc(reader.GetDateTime(1));
            }
        }
        var run = id is null ? null : await runs.GetRunAsync(tenantId, Guid.Parse(id), ct);
        var billing = await Billing(connection, tenantId, siteId, schedule, promptSummary, id, runCreatedAt, ct);
        if (AutomaticMonitoringSelection.InvalidReason(schedule.Selection) is { } selectionError)
            billing = billing with { Status = "selection_invalid", Reason = selectionError == "MONITORING_SELECTION_EMPTY"
                ? "自动监控当前没有有效问题或目标，新轮次将跳过。" : "自动监控选择超过规模限制，新轮次将跳过，不会缩减后收费。" };
        return new PromptMonitoringView(schedule.Selection.PromptIds, schedule, run, billing, appSchedule);
    }

    private async Task<IReadOnlyDictionary<Guid, MonitoringPromptScope>> PromptScopes(Guid tenantId, Guid siteId, CancellationToken ct)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(ct);
        return (await AutomaticMonitoringSelection.Prompts(connection, null, tenantId, siteId, ct)).ToDictionary(p => p.PromptId);
    }

    // Checks per round for the billing view. With custom scopes it is the same matrix the slot reserves; without them
    // the historical enabled-prompt × target × sample product is kept unchanged.
    internal static long BillingCheckCount(CollectionScheduleSelection selection, int enabledPromptCount)
        => selection.PromptTargets is { Count: > 0 }
            ? MonitoringMatrix.CountChecks(selection)
            : (long)Math.Max(0, enabledPromptCount) * selection.Targets.Count * Math.Max(0, selection.SampleCount);

    // Enabled prompts whose custom scope names App target keys only (0 for every brand without such a prompt). The daily
    // plan drops them — they have no API cell — and when they are the brand's only custom prompts the plan carries no
    // PromptTargets, so the enabled-prompt product above would count them on every target.
    private static async Task<int> AppOnlyPromptCountAsync(MySqlConnection connection, Guid tenantId, Guid siteId, CancellationToken ct)
    {
        var appKeys = AppChannelDefinition.Engines.Select(CollectionIdentity.AppTargetKey).ToHashSet(StringComparer.Ordinal);
        return (await AutomaticMonitoringSelection.Prompts(connection, null, tenantId, siteId, ct))
            .Count(p => p.TargetKeys is { Count: > 0 } keys && keys.All(appKeys.Contains));
    }

    private static async Task<PromptMonitoringBillingView> Billing(MySqlConnection connection, Guid tenantId, Guid siteId,
        CollectionScheduleView schedule, PromptSummaryView summary, string? runId, DateTimeOffset? runCreatedAt,
        CancellationToken ct)
    {
        // The next 09:01 slot only takes cells due that day (weekly / every_other_day off-days are skipped).
        var planned = schedule.Selection;
        if (schedule.NextSlotAt is { } nextSlot)
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZone);
            planned = AutomaticMonitoringSelection.DueSelection(planned,
                await AutomaticMonitoringSelection.Targets(connection, null, tenantId, siteId, false, ct),
                DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nextSlot, zone).DateTime), MonitoringSlotKind.Main);
        }
        var targetCount = planned.Targets.Count;
        var sampleCount = planned.SampleCount;
        // A custom-scoped plan stays on the matrix count even when the frequency filter leaves no custom prompt in the slot.
        var checkCount = schedule.Selection.PromptTargets is { Count: > 0 }
            ? MonitoringMatrix.CountChecks(planned)
            : BillingCheckCount(planned, summary.EnabledCount - await AppOnlyPromptCountAsync(connection, tenantId, siteId, ct));
        var estimated = PromptMonitoringPricing.Estimate(checkCount);
        var account = await new MySqlCommand("""
            SELECT available_credits FROM credit_accounts WHERE tenant_id = UUID_TO_BIN(@tenant);
            """, connection) { Parameters = { new("@tenant", tenantId.ToString()) } }.ExecuteScalarAsync(ct);
        var available = account is null || account is DBNull ? 0m : Convert.ToDecimal(account, CultureInfo.InvariantCulture);
        await using (var skipped = new MySqlCommand("""
            SELECT skip_code, skip_message, created_at FROM schedule_slots
            WHERE schedule_id = UUID_TO_BIN(@schedule) AND status = 'skipped'
            ORDER BY created_at DESC LIMIT 1;
            """, connection))
        {
            skipped.Parameters.AddWithValue("@schedule", schedule.Id.ToString());
            await using var skipReader = await skipped.ExecuteReaderAsync(ct);
            if (await skipReader.ReadAsync(ct) && !skipReader.IsDBNull(0)
                && string.Equals(skipReader.GetString(0), "CREDIT_INSUFFICIENT", StringComparison.Ordinal)
                && (runCreatedAt is null || Utc(skipReader.GetDateTime(2)) > runCreatedAt.Value))
            {
                var skipReason = skipReader.IsDBNull(1) ? "本轮余额不足，已跳过；下一次计划检查时重试。" : skipReader.GetString(1);
                return new PromptMonitoringBillingView(summary.EnabledCount, targetCount, sampleCount,
                    PromptMonitoringPricing.UnitPrice, estimated, available, 0, 0, 0, "insufficient", skipReason, CheckCount: checkCount);
            }
        }
        var nextSlotNeedsCheck = runId is null || (runCreatedAt is not null && schedule.NextSlotAt is not null
            && schedule.NextSlotAt.Value > runCreatedAt.Value);
        if (nextSlotNeedsCheck && available < estimated)
        {
            return new PromptMonitoringBillingView(summary.EnabledCount, targetCount, sampleCount,
                PromptMonitoringPricing.UnitPrice, estimated, available, 0, 0, 0,
                "insufficient", $"下一轮预计需要 {estimated:0.###} 积分，当前可用 {available:0.###} 积分。", CheckCount: checkCount);
        }
        if (runId is null)
        {
            return new PromptMonitoringBillingView(summary.EnabledCount, targetCount, sampleCount,
                PromptMonitoringPricing.UnitPrice, estimated, available, 0, 0, 0,
                available < estimated ? "insufficient" : "ready",
                available < estimated ? $"本轮预计需要 {estimated:0.###} 积分，当前可用 {available:0.###} 积分。" : null, CheckCount: checkCount);
        }
        await using var reservation = new MySqlCommand("""
            SELECT estimated_credits, held_credits, captured_credits, released_credits, status, reason
            FROM monitoring_credit_reservations
            WHERE tenant_id = UUID_TO_BIN(@tenant) AND site_id = UUID_TO_BIN(@site)
              AND run_id = UUID_TO_BIN(@run)
            LIMIT 1;
            """, connection);
        reservation.Parameters.AddWithValue("@tenant", tenantId.ToString());
        reservation.Parameters.AddWithValue("@site", siteId.ToString());
        reservation.Parameters.AddWithValue("@run", runId);
        await using var reader = await reservation.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return new PromptMonitoringBillingView(summary.EnabledCount, targetCount, sampleCount,
                PromptMonitoringPricing.UnitPrice, estimated, available, 0, 0, 0,
                available < estimated ? "insufficient" : "pending",
                available < estimated ? $"本轮预计需要 {estimated:0.###} 积分，当前可用 {available:0.###} 积分。" : null, CheckCount: checkCount);
        }
        var status = reader.GetString(4);
        var reason = reader.IsDBNull(5) ? null : reader.GetString(5);
        return new PromptMonitoringBillingView(summary.EnabledCount, targetCount, sampleCount,
            PromptMonitoringPricing.UnitPrice, reader.GetDecimal(0), available,
            reader.GetDecimal(1), reader.GetDecimal(2), reader.GetDecimal(3), status, reason, CheckCount: checkCount);
    }

    private async Task<decimal> AvailableCredits(Guid tenantId, CancellationToken ct)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(ct);
        await using var command = new MySqlCommand("""
            SELECT available_credits FROM credit_accounts WHERE tenant_id = UUID_TO_BIN(@tenant);
            """, connection);
        command.Parameters.AddWithValue("@tenant", tenantId.ToString());
        var value = await command.ExecuteScalarAsync(ct);
        return value is null || value is DBNull ? 0m : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    // Daily monitoring covers every enabled target of a registered API answer provider (cloro, doubao_api, deepseek_api);
    // domestic_collector targets stay outside this plan.
    internal static CollectionTargetView[] SelectMonitoringTargets(IReadOnlyList<CollectionTargetView> items) =>
        items.Where(t => t.Enabled && AnswerProviderDefinition.IsRegistered(t.Provider)).ToArray();
}
