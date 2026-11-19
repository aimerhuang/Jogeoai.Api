using System.Reflection;
using System.Runtime.ExceptionServices;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using MySqlConnector;

namespace Jogeoai.Tests;

/// <summary>
/// 隔离库（已执行 001–048、库名 jogeoai_team_monitoring_*）上的团队默认监控配置：保存并下发到跟随品牌、锚点与地区规则、
/// 品牌保存变为单独配置、恢复跟随、两种保存先后顺序、首次开启监控使用团队默认配置、影响预估汇总。
/// 每个用例使用新的租户，不触碰共享 jogeoai 业务库；未配置 JOGEOAI_TEAM_MONITORING_TEST_CONNECTION 时跳过。用例保留其测试行，
/// 结束时停用计划并搁置 Provider 任务。
/// </summary>
public sealed class TeamMonitoringConfigSqlTests
{
    private const string Zone = "Asia/Shanghai";
    private static readonly string DomesticKey = CollectionIdentity.TargetKey("domestic_collector", "doubao", "consumer_web", "CN", "zh-CN");

    private static string ConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("JOGEOAI_TEAM_MONITORING_TEST_CONNECTION")!;
        Assert.StartsWith("jogeoai_team_monitoring_", new MySqlConnectionStringBuilder(connectionString).Database, StringComparison.Ordinal);
        return connectionString;
    }

    private static DateOnly Today() => ManagementStore.BrandLocalDate(Zone, DateTimeOffset.UtcNow);

    private static string Key(string provider, string engine, string country, string locale, string? mode = null)
        => ManagementStore.MonitoringTargetKey(provider, engine, provider == "cloro" ? "scraped" : "api", country, locale, mode);

    // A-3 / A-4 / A-5: following brands get exactly the cells (regions kept or derived), individual / archived brands and
    // domestic web collector rows are untouched, every affected brand's target version goes up by one per save.
    [TeamMonitoringMySqlFact]
    public async Task SavingTheTeamDefaultSyncsFollowingBrandsOnly()
    {
        var cs = ConnectionString();
        var tenant = await SeedTenantAsync(cs);
        var follower = await SeedBrandAsync(cs, tenant, "跟随A", follows: true, locale: "en-GB", created: "2026-01-01");
        await SeedTargetAsync(cs, tenant, follower, "cloro", "chatgpt", "JP", "ja-JP", enabled: true, anchor: "2026-01-01");
        await SeedTargetAsync(cs, tenant, follower, "cloro", "gemini", "DE", "de-DE", enabled: true, anchor: "2026-01-01");
        await SeedTargetAsync(cs, tenant, follower, "cloro", "perplexity", "US", "en-US", enabled: true, anchor: "2026-01-01");
        await SeedTargetAsync(cs, tenant, follower, "domestic_collector", "doubao", "CN", "zh-CN", enabled: true);
        var empty = await SeedBrandAsync(cs, tenant, "跟随B", follows: true, locale: "zh-CN", created: "2026-01-02");
        var individual = await SeedBrandAsync(cs, tenant, "单独C", follows: false, locale: "zh-CN", created: "2026-01-03");
        await SeedTargetAsync(cs, tenant, individual, "cloro", "copilot", "US", "en-US", enabled: true, anchor: "2026-01-01");
        var archived = await SeedBrandAsync(cs, tenant, "归档D", follows: true, locale: "zh-CN", created: "2026-01-04", status: "archived");
        var store = new ManagementStore(new MySqlDatabase(cs));
        var before = await SnapshotAsync(cs, individual, archived);
        var versions = await VersionsAsync(cs, tenant);

        var initial = await store.GetTeamMonitoringConfigAsync(tenant);
        Assert.Equal(0u, initial.ConfigVersion);
        Assert.Equal("cloro|chatgpt|fast|daily", string.Join(";", initial.Cells.Select(Cell)));
        Assert.Equal(["跟随A", "跟随B", "单独C"], initial.Brands.Select(b => b.DisplayName));
        Assert.Equal([true, true, false], initial.Brands.Select(b => b.FollowsTeamDefault));
        // Catalog order of the Cloro engines: chatgpt, perplexity, copilot, gemini, …
        Assert.Equal(["cloro|chatgpt|fast|daily", "cloro|perplexity|fast|daily", "cloro|gemini|fast|daily"],
            initial.Brands[0].Cells.Select(Cell));

        var view = await store.PutTeamMonitoringConfigAsync(tenant, Guid.NewGuid(), new TeamMonitoringConfigWrite(0, [
            new("deepseek_api", "deepseek", AnswerMode.Deep, MonitoringFrequency.TwiceDaily),
            new("cloro", "chatgpt", null, MonitoringFrequency.Weekly), new("cloro", "gemini")]));
        Assert.Equal(1u, view.ConfigVersion);
        // Catalog order, resolved modes.
        var expected = new[] { "cloro|chatgpt|fast|weekly", "cloro|gemini|fast|daily", "deepseek_api|deepseek|deep|twice_daily" };
        Assert.Equal(expected, view.Cells.Select(Cell));
        Assert.Equal(expected, view.Brands.Single(b => b.SiteId == follower).Cells.Select(Cell));
        Assert.Equal(expected, view.Brands.Single(b => b.SiteId == empty).Cells.Select(Cell));
        Assert.Equal(["cloro|copilot|fast|daily"], view.Brands.Single(b => b.SiteId == individual).Cells.Select(Cell));

        var today = Today();
        var rows = await TargetRowsAsync(cs, follower);
        // Existing regions are kept; the anchor moves for the change to weekly and for the newly enabled cell, not for daily gemini.
        Assert.Equal((true, (string?)null, "weekly", (DateOnly?)today), rows[Key("cloro", "chatgpt", "JP", "ja-JP")]);
        Assert.Equal((true, (string?)null, "daily", (DateOnly?)new DateOnly(2026, 1, 1)), rows[Key("cloro", "gemini", "DE", "de-DE")]);
        Assert.Equal((true, "deep", "twice_daily", (DateOnly?)today), rows[Key("deepseek_api", "deepseek", "CN", "zh-CN", "deep")]);
        Assert.False(rows[Key("cloro", "perplexity", "US", "en-US")].Enabled);
        Assert.True(rows[DomesticKey].Enabled);
        // No existing row: Cloro takes the catalog country and the brand locale, pinned providers their fixed surface.
        var created = await TargetRowsAsync(cs, empty);
        Assert.Equal(3, created.Count);
        Assert.True(created[Key("cloro", "chatgpt", "US", "zh-CN")].Enabled);
        Assert.True(created[Key("cloro", "gemini", "US", "zh-CN")].Enabled);
        Assert.True(created[Key("deepseek_api", "deepseek", "CN", "zh-CN", "deep")].Enabled);
        Assert.Equal(before, await SnapshotAsync(cs, individual, archived));
        var after = await VersionsAsync(cs, tenant);
        Assert.Equal((versions[follower] + 1, versions[empty] + 1, versions[individual], versions[archived]),
            (after[follower], after[empty], after[individual], after[archived]));

        // A later save removes cells (disabled, never deleted) and keeps anchors of unchanged cells.
        view = await store.PutTeamMonitoringConfigAsync(tenant, Guid.NewGuid(), new TeamMonitoringConfigWrite(1, [new("cloro", "chatgpt", null, MonitoringFrequency.Weekly)]));
        Assert.Equal(2u, view.ConfigVersion);
        rows = await TargetRowsAsync(cs, follower);
        Assert.Equal((true, (string?)null, "weekly", (DateOnly?)today), rows[Key("cloro", "chatgpt", "JP", "ja-JP")]);
        Assert.False(rows[Key("cloro", "gemini", "DE", "de-DE")].Enabled);
        Assert.False(rows[Key("deepseek_api", "deepseek", "CN", "zh-CN", "deep")].Enabled);
        Assert.True(rows[DomesticKey].Enabled);
        Assert.Equal(after[follower] + 1, (await VersionsAsync(cs, tenant))[follower]);

        // Stale version and validation failures write nothing.
        var conflict = await Assert.ThrowsAsync<CollectorStoreException>(() => store.PutTeamMonitoringConfigAsync(tenant, Guid.NewGuid(),
            new TeamMonitoringConfigWrite(1, [new("cloro", "gemini")])));
        Assert.Equal(("CONFIG_VERSION_CONFLICT", 409), (conflict.Code, conflict.StatusCode));
        var empty400 = await Assert.ThrowsAsync<CollectorStoreException>(() => store.PutTeamMonitoringConfigAsync(tenant, Guid.NewGuid(),
            new TeamMonitoringConfigWrite(2, [])));
        Assert.Equal(("MONITORING_TARGETS_REQUIRED", 400), (empty400.Code, empty400.StatusCode));
        Assert.Equal(2u, (await store.GetTeamMonitoringConfigAsync(tenant)).ConfigVersion);
        Assert.Equal(after[follower] + 1, (await VersionsAsync(cs, tenant))[follower]);
    }

    // A-8 and both save orders: a public brand save makes the brand individual (later team saves skip it); a team save that
    // lands first bumps the brand version, so the brand save started on the old version gets 409; follow-default restores.
    [TeamMonitoringMySqlFact]
    public async Task BrandSaveMakesItIndividualAndFollowDefaultRestoresTheTeamDefault()
    {
        var cs = ConnectionString();
        var tenant = await SeedTenantAsync(cs);
        var brand = await SeedBrandAsync(cs, tenant, "品牌", follows: true, locale: "zh-CN", created: "2026-02-01");
        await SeedTargetAsync(cs, tenant, brand, "domestic_collector", "doubao", "CN", "zh-CN", enabled: true);
        var store = new ManagementStore(new MySqlDatabase(cs));

        // Follow-default before any saved default materializes the baseline.
        var targets = await store.GetSiteTargetsAsync(tenant, brand);
        Assert.True(targets.FollowsTeamDefault);
        targets = await store.FollowTeamDefaultAsync(tenant, brand, new FollowTeamDefaultRequest(targets.ConfigVersion));
        Assert.True(targets.FollowsTeamDefault);
        Assert.Equal([Key("cloro", "chatgpt", "US", "zh-CN")], targets.Items.Where(i => i.Enabled && i.MonitoringEligible).Select(i => i.TargetKey));

        // Team save first: the brand follows and is updated; a brand save on the old version is rejected.
        var stale = targets.ConfigVersion;
        await store.PutTeamMonitoringConfigAsync(tenant, Guid.NewGuid(), new TeamMonitoringConfigWrite(0, [new("packy_kimi", "kimi", AnswerMode.Deep)]));
        var conflict = await Assert.ThrowsAsync<CollectorStoreException>(() => store.PutSiteTargetsAsync(tenant, brand,
            new SiteTargetsWrite(stale, [new("cloro", "gemini", "scraped", "US", "zh-CN")], SiteTargetsScope.Monitoring), default, markIndividual: true));
        Assert.Equal(("CONFIG_VERSION_CONFLICT", 409), (conflict.Code, conflict.StatusCode));
        targets = await store.GetSiteTargetsAsync(tenant, brand);
        Assert.True(targets.FollowsTeamDefault);
        Assert.Equal([Key("packy_kimi", "kimi", "CN", "zh-CN", "deep")], EnabledMonitoringKeys(targets));

        // Internal write (first monitoring) keeps the flag; a public brand save makes the brand individual.
        targets = await store.PutSiteTargetsAsync(tenant, brand, new SiteTargetsWrite(targets.ConfigVersion,
            [new("packy_kimi", "kimi", "api", "CN", "zh-CN", true, AnswerMode.Deep)], SiteTargetsScope.Monitoring));
        Assert.True(targets.FollowsTeamDefault);
        targets = await store.PutSiteTargetsAsync(tenant, brand, new SiteTargetsWrite(targets.ConfigVersion,
            [new("cloro", "gemini", "scraped", "US", "zh-CN")], SiteTargetsScope.Monitoring), default, markIndividual: true);
        Assert.False(targets.FollowsTeamDefault);
        Assert.False((await store.GetTeamMonitoringConfigAsync(tenant)).Brands.Single().FollowsTeamDefault);

        // Brand saved individually first: the next team save skips it entirely.
        var version = targets.ConfigVersion;
        await store.PutTeamMonitoringConfigAsync(tenant, Guid.NewGuid(), new TeamMonitoringConfigWrite(1, [new("deepseek_api", "deepseek")]));
        targets = await store.GetSiteTargetsAsync(tenant, brand);
        Assert.Equal(version, targets.ConfigVersion);
        Assert.Equal([Key("cloro", "gemini", "US", "zh-CN")], EnabledMonitoringKeys(targets));

        // Follow-default: stale version 409, then the cells equal the default, the flag is set and the version bumps.
        var stale409 = await Assert.ThrowsAsync<CollectorStoreException>(() => store.FollowTeamDefaultAsync(tenant, brand, new FollowTeamDefaultRequest(version - 1)));
        Assert.Equal(("CONFIG_VERSION_CONFLICT", 409), (stale409.Code, stale409.StatusCode));
        targets = await store.FollowTeamDefaultAsync(tenant, brand, new FollowTeamDefaultRequest(version));
        Assert.True(targets.FollowsTeamDefault);
        Assert.Equal(version + 1, targets.ConfigVersion);
        Assert.Equal([Key("deepseek_api", "deepseek", "CN", "zh-CN")], EnabledMonitoringKeys(targets));
        Assert.True(targets.Items.Single(i => i.TargetKey == DomesticKey).Enabled);
        var team = await store.GetTeamMonitoringConfigAsync(tenant);
        Assert.Equal(team.Cells.Select(Cell), team.Brands.Single().Cells.Select(Cell));

        // Archived brands cannot follow (404, same as the brand target save).
        await ExecAsync(cs, "UPDATE sites SET status='archived' WHERE id=UUID_TO_BIN(@site);", ("@site", brand.ToString()));
        var gone = await Assert.ThrowsAsync<CollectorStoreException>(() => store.FollowTeamDefaultAsync(tenant, brand, new FollowTeamDefaultRequest(targets.ConfigVersion)));
        Assert.Equal(("RESOURCE_NOT_FOUND", 404), (gone.Code, gone.StatusCode));
    }

    // A-9: first monitoring of a following brand uses the saved team default; a team without a saved default still gets the
    // single ChatGPT baseline target (catalog country + brand language), exactly as before. The follow flag is not changed.
    [TeamMonitoringMySqlFact]
    public async Task FirstMonitoringUsesTheSavedTeamDefaultOrTheHistoricalBaseline()
    {
        var cs = ConnectionString();
        var database = new MySqlDatabase(cs);
        var store = new ManagementStore(database);
        var management = DispatchProxy.Create<IManagementStore, AvailableCatalogProxy>();
        ((AvailableCatalogProxy)(object)management).Inner = store;
        var service = new PromptMonitoringService(database, management, new CollectionScheduleStore(database), new CloroVisibilityStore(database));

        var withDefault = await SeedTenantAsync(cs);
        await store.PutTeamMonitoringConfigAsync(withDefault, Guid.NewGuid(), new TeamMonitoringConfigWrite(0, [
            new("cloro", "chatgpt", null, MonitoringFrequency.Weekly), new("deepseek_api", "deepseek", AnswerMode.Deep)]));
        var brand = await SeedBrandAsync(cs, withDefault, "新品牌", follows: true, locale: "zh-CN", created: "2026-03-01");
        await SeedPromptAsync(cs, brand);
        var individual = await SeedBrandAsync(cs, withDefault, "单独品牌", follows: false, locale: "zh-CN", created: "2026-03-02");
        await SeedPromptAsync(cs, individual);

        var baselineTenant = await SeedTenantAsync(cs);
        var baseline = await SeedBrandAsync(cs, baselineTenant, "基线品牌", follows: true, locale: "zh-CN", created: "2026-03-03");
        await SeedPromptAsync(cs, baseline);
        try
        {
            Assert.NotNull(await store.MaterializeTeamDefaultAsync(withDefault, brand));
            Assert.Null(await store.MaterializeTeamDefaultAsync(withDefault, individual));
            Assert.Null(await store.MaterializeTeamDefaultAsync(baselineTenant, baseline));

            var started = await service.EnsureAsync(withDefault, brand);
            Assert.NotNull(started.Schedule);
            var targets = await store.GetSiteTargetsAsync(withDefault, brand);
            Assert.True(targets.FollowsTeamDefault);
            Assert.Equal(new[] { Key("cloro", "chatgpt", "US", "zh-CN"), Key("deepseek_api", "deepseek", "CN", "zh-CN", "deep") }.Order(StringComparer.Ordinal),
                EnabledMonitoringKeys(targets));
            Assert.Equal(MonitoringFrequency.Weekly, targets.Items.Single(i => i.Engine == "chatgpt").Frequency);
            // With enabled monitoring targets the brand is no longer "first time": nothing to materialize.
            Assert.Null(await store.MaterializeTeamDefaultAsync(withDefault, brand));

            // Individual brand of the same team and a brand of a team that never saved a default: the historical baseline.
            foreach (var (tenant, site) in new[] { (withDefault, individual), (baselineTenant, baseline) })
            {
                await service.EnsureAsync(tenant, site);
                var view = await store.GetSiteTargetsAsync(tenant, site);
                var only = Assert.Single(view.Items);
                Assert.Equal(("cloro", "chatgpt", "US", "zh-CN", true, AnswerMode.Fast, MonitoringFrequency.Daily),
                    (only.Provider, only.Engine, only.Country, only.Locale, only.Enabled, only.AnswerMode, only.Frequency));
            }
            Assert.False((await store.GetSiteTargetsAsync(withDefault, individual)).FollowsTeamDefault);
            Assert.True((await store.GetSiteTargetsAsync(baselineTenant, baseline)).FollowsTeamDefault);
        }
        finally
        {
            foreach (var site in new[] { brand, individual, baseline }) await ParkAsync(cs, site);
        }
    }

    // Team estimate = the brand target estimate on each following brand's materialized draft, summed.
    [TeamMonitoringMySqlFact]
    public async Task TeamEstimateSumsTheBrandEstimatesOfFollowingBrands()
    {
        var cs = ConnectionString();
        var database = new MySqlDatabase(cs);
        var store = new ManagementStore(database);
        var service = new PromptMonitoringService(database, store, new CollectionScheduleStore(database), new CloroVisibilityStore(database));
        var tenant = await SeedTenantAsync(cs, credits: 25m);
        var first = await SeedBrandAsync(cs, tenant, "甲", follows: true, locale: "zh-CN", created: "2026-04-01");
        await SeedPromptAsync(cs, first);
        await SeedPromptAsync(cs, first);
        var second = await SeedBrandAsync(cs, tenant, "乙", follows: true, locale: "en-US", created: "2026-04-02");
        await SeedPromptAsync(cs, second);
        var individual = await SeedBrandAsync(cs, tenant, "丙", follows: false, locale: "zh-CN", created: "2026-04-03");
        await SeedPromptAsync(cs, individual);

        TeamMonitoringCellWrite[] draft = [new("cloro", "chatgpt"), new("deepseek_api", "deepseek", AnswerMode.Deep, MonitoringFrequency.Weekly)];
        var estimate = await service.EstimateTeamMonitoringAsync(tenant, new TeamMonitoringEstimateRequest(draft));
        Assert.Equal([first, second], estimate.Brands.Select(b => b.SiteId));
        foreach (var brand in estimate.Brands)
        {
            var materialized = (await store.MaterializeTeamCellsForFollowersAsync(tenant, draft)).Single(b => b.SiteId == brand.SiteId);
            var single = await service.EstimateTargetsAsync(tenant, brand.SiteId, new MonitoringTargetsEstimateRequest(materialized.Targets));
            Assert.Equal((single.PromptCount, single.WeeklyChecks, single.WeeklyCredits, single.NextSlotChecks, single.NextSlotCredits),
                (brand.PromptCount, brand.WeeklyChecks, brand.WeeklyCredits, brand.NextSlotChecks, brand.NextSlotCredits));
            Assert.Equal(single.Blockers, brand.Blockers);
        }
        // 2 prompts × (7 + 1) + 1 prompt × (7 + 1) = 24 weekly checks.
        Assert.Equal((2, 1), (estimate.Brands[0].PromptCount, estimate.Brands[1].PromptCount));
        Assert.Equal(24m, estimate.WeeklyChecks);
        Assert.Equal(240m, estimate.WeeklyCredits);
        Assert.Equal(estimate.Brands.Sum(b => b.NextSlotCredits), estimate.NextSlotCredits);
        Assert.Equal((10m, 25m), (estimate.UnitPrice, estimate.Balance));
        Assert.Equal(Math.Max(0m, estimate.NextSlotCredits - 25m), estimate.Shortfall);
        Assert.True(estimate.NextSlotCredits >= 30m);
        Assert.True(estimate.Shortfall > 0m);

        // Read-only: nothing saved.
        Assert.Equal(0u, (await store.GetTeamMonitoringConfigAsync(tenant)).ConfigVersion);
        Assert.Empty(await TargetRowsAsync(cs, first));
        var invalid = await Assert.ThrowsAsync<CollectorStoreException>(() => service.EstimateTeamMonitoringAsync(tenant,
            new TeamMonitoringEstimateRequest([new("cloro", "chatgpt", AnswerMode.Deep)])));
        Assert.Equal(("ANSWER_MODE_UNSUPPORTED", 400), (invalid.Code, invalid.StatusCode));
    }

    private static string Cell(TeamMonitoringCellView cell)
        => $"{cell.Provider}|{cell.Engine}|{AnswerModeRules.ToStorage(cell.AnswerMode)}|{MonitoringFrequencyRules.ToStorage(cell.Frequency)}";

    private static string?[] EnabledMonitoringKeys(SiteTargetsView view)
        => view.Items.Where(i => i.Enabled && i.MonitoringEligible).Select(i => i.TargetKey).Order(StringComparer.Ordinal).ToArray();

    public class AvailableCatalogProxy : DispatchProxy
    {
        internal IManagementStore Inner { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IManagementStore.GetTargetCatalogAsync))
                return AvailableCatalogAsync(args is { Length: > 0 } && args[0] is CancellationToken ct ? ct : default,
                    args is { Length: > 1 } ? args[1] as Guid? : null);
            try { return targetMethod.Invoke(Inner, args); }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }

        private async Task<IReadOnlyList<CollectionTargetView>> AvailableCatalogAsync(CancellationToken ct, Guid? tenantId)
            => (await Inner.GetTargetCatalogAsync(ct, tenantId)).Select(t => t with { Availability = "available" }).ToArray();
    }

    internal static async Task<Guid> SeedTenantAsync(string cs, decimal credits = 1000m)
    {
        var tenant = Guid.NewGuid();
        await ExecAsync(cs, """
            INSERT INTO tenants (id,name,status,created_at,updated_at) VALUES (UUID_TO_BIN(@tenant),'team-monitoring-test','active',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
            INSERT INTO credit_accounts (tenant_id,available_credits,lifetime_granted,updated_at) VALUES (UUID_TO_BIN(@tenant),@credits,@credits,UTC_TIMESTAMP(6));
            """, ("@tenant", tenant.ToString()), ("@credits", credits));
        return tenant;
    }

    /// <param name="follows">跟随标记；为 null 时不写该列（迁移 048 之前的库）。</param>
    internal static async Task<Guid> SeedBrandAsync(string cs, Guid tenant, string name, bool? follows, string locale, string created,
        string status = "active")
    {
        var site = Guid.NewGuid();
        await ExecAsync(cs, $"""
            INSERT INTO sites (id,tenant_id,host,display_name,status,created_at,updated_at)
              VALUES (UUID_TO_BIN(@site),UUID_TO_BIN(@tenant),@host,@name,@status,@created,@created);
            INSERT INTO site_brand_settings (site_id,tenant_id,default_country,default_locale,time_zone,created_at,updated_at{(follows is null ? "" : ",monitoring_follows_default")})
              VALUES (UUID_TO_BIN(@site),UUID_TO_BIN(@tenant),'CN',@locale,@zone,@created,@created{(follows is null ? "" : ",@follows")});
            INSERT INTO subject_terms (id,tenant_id,site_id,subject_type,subject_id,namespace_key,term_type,display_value,normalized_key,created_at)
              VALUES (UUID_TO_BIN(@term),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),'brand',UUID_TO_BIN(@site),'text','name',@name,@key,UTC_TIMESTAMP(6));
            """, ("@site", site.ToString()), ("@tenant", tenant.ToString()), ("@host", $"team-monitoring-{site:N}.test"), ("@name", name),
            ("@status", status), ("@created", created), ("@locale", locale), ("@zone", Zone), ("@follows", follows),
            ("@term", Guid.NewGuid().ToString()), ("@key", $"brand-{site:N}"));
        return site;
    }

    internal static Task SeedTargetAsync(string cs, Guid tenant, Guid site, string provider, string engine, string country, string locale,
        bool enabled, string? anchor = null)
    {
        var capture = provider switch { "cloro" => "scraped", "domestic_collector" => "consumer_web", _ => "api" };
        return ExecAsync(cs, """
            INSERT INTO site_targets (id,tenant_id,site_id,provider,engine,capture_mode,country,locale,target_key,enabled,monitoring_enabled_at,frequency_anchor,created_at,updated_at)
            VALUES (UUID_TO_BIN(@id),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),@provider,@engine,@capture,@country,@locale,@key,@enabled,
                    IF(@enabled, '2026-01-01', NULL),@anchor,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
            """, ("@id", Guid.NewGuid().ToString()), ("@tenant", tenant.ToString()), ("@site", site.ToString()), ("@provider", provider),
            ("@engine", engine), ("@capture", capture), ("@country", country), ("@locale", locale),
            ("@key", ManagementStore.MonitoringTargetKey(provider, engine, capture, country, locale)), ("@enabled", enabled), ("@anchor", anchor));
    }

    internal static Task SeedPromptAsync(string cs, Guid site)
    {
        var prompt = Guid.NewGuid();
        var version = Guid.NewGuid();
        return ExecAsync(cs, """
            INSERT INTO prompts (id,site_id,content,locale,enabled,created_at,updated_at)
              VALUES (UUID_TO_BIN(@prompt),UUID_TO_BIN(@site),@content,'zh-CN',TRUE,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
            INSERT INTO prompt_versions (id,site_id,prompt_id,version,content,locale,branded_status,branded_source,created_at)
              VALUES (UUID_TO_BIN(@version),UUID_TO_BIN(@site),UUID_TO_BIN(@prompt),1,@content,'zh-CN','unbranded','system',UTC_TIMESTAMP(6));
            UPDATE prompts SET current_version_id=UUID_TO_BIN(@version) WHERE id=UUID_TO_BIN(@prompt);
            """, ("@prompt", prompt.ToString()), ("@site", site.ToString()), ("@version", version.ToString()),
            ("@content", $"哪个 AI 写作工具好用？{prompt:N}"));
    }

    private static async Task<Dictionary<string, (bool Enabled, string? Mode, string Frequency, DateOnly? Anchor)>> TargetRowsAsync(string cs, Guid site)
        => (await RowsAsync(cs, """
                SELECT target_key, enabled, answer_mode, frequency, DATE_FORMAT(frequency_anchor, '%Y-%m-%d')
                FROM site_targets WHERE site_id=UUID_TO_BIN(@site);
                """, ("@site", site.ToString())))
            .ToDictionary(r => r[0]!, r => (r[1] is "1" or "True", r[2], r[3]!, r[4] is null ? (DateOnly?)null : DateOnly.Parse(r[4]!)));

    // Full row content of the brands that must not change (ids, flags, frequencies, anchors, timestamps).
    private static async Task<string> SnapshotAsync(string cs, params Guid[] sites)
        => string.Join("\n", (await RowsAsync(cs, $"""
                SELECT BIN_TO_UUID(t.id), t.target_key, t.enabled, COALESCE(t.answer_mode,''), t.frequency, COALESCE(t.frequency_anchor,''),
                       COALESCE(t.monitoring_enabled_at,''), t.updated_at, b.target_config_version, b.monitoring_follows_default
                FROM site_brand_settings b LEFT JOIN site_targets t ON t.site_id = b.site_id
                WHERE b.site_id IN ({string.Join(",", sites.Select((_, i) => $"UUID_TO_BIN(@s{i})"))}) ORDER BY b.site_id, t.target_key;
                """, sites.Select((s, i) => ($"@s{i}", (object?)s.ToString())).ToArray()))
            .Select(r => string.Join("|", r)));

    private static async Task<Dictionary<Guid, uint>> VersionsAsync(string cs, Guid tenant)
        => (await RowsAsync(cs, "SELECT BIN_TO_UUID(site_id), target_config_version FROM site_brand_settings WHERE tenant_id=UUID_TO_BIN(@tenant);",
                ("@tenant", tenant.ToString())))
            .ToDictionary(r => Guid.Parse(r[0]!), r => uint.Parse(r[1]!));

    // Keep rows for inspection, but never leave a due plan, a pending slot or runnable provider tasks behind.
    private static async Task ParkAsync(string cs, Guid site)
    {
        await ExecAsync(cs, "UPDATE collection_schedules SET enabled=FALSE, next_slot_at=NULL WHERE site_id=UUID_TO_BIN(@site);", ("@site", site.ToString()));
        await ExecAsync(cs, """
            UPDATE schedule_slots s JOIN collection_schedules p ON p.id=s.schedule_id SET s.status='skipped', s.skip_code='TEST_PARKED'
            WHERE p.site_id=UUID_TO_BIN(@site) AND s.status='pending';
            """, ("@site", site.ToString()));
        await ExecAsync(cs, "UPDATE provider_tasks t JOIN visibility_runs r ON r.id=t.run_id SET t.next_attempt_at='2099-01-01' WHERE r.site_id=UUID_TO_BIN(@site);",
            ("@site", site.ToString()));
    }

    internal static async Task ExecAsync(string cs, string sql, params (string Name, object? Value)[] args)
    {
        await using var connection = new MySqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    internal static async Task<List<string?[]>> RowsAsync(string cs, string sql, params (string Name, object? Value)[] args)
    {
        await using var connection = new MySqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string?[]>();
        while (await reader.ReadAsync())
        {
            var row = new string?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
                row[i] = reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture);
            rows.Add(row);
        }
        return rows;
    }
}

/// <summary>
/// 迁移 048 之前的兼容（PRD A-10）：隔离库 JOGEOAI_TEAM_MONITORING_PRE048_TEST_CONNECTION（已执行 001–047、尚未执行 048，库名 jogeoai_team_monitoring_*）
/// 上团队接口 503、品牌目标读写照常、followsTeamDefault 为 false、首次开启监控不物化团队默认配置。未配置时跳过。
/// </summary>
public sealed class TeamMonitoringPre048SqlTests
{
    [TeamMonitoringPre048MySqlFact]
    public async Task UnmigratedDatabaseKeepsBrandTargetsAndRejectsTeamEndpoints()
    {
        var cs = Environment.GetEnvironmentVariable("JOGEOAI_TEAM_MONITORING_PRE048_TEST_CONNECTION")!;
        Assert.StartsWith("jogeoai_team_monitoring_", new MySqlConnectionStringBuilder(cs).Database, StringComparison.Ordinal);
        Assert.Empty(await TeamMonitoringConfigSqlTests.RowsAsync(cs, """
            SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'tenant_monitoring_defaults';
            """));
        var tenant = await TeamMonitoringConfigSqlTests.SeedTenantAsync(cs);
        var site = await TeamMonitoringConfigSqlTests.SeedBrandAsync(cs, tenant, "未迁移", follows: null, locale: "zh-CN", created: "2026-05-01");
        var store = new ManagementStore(new MySqlDatabase(cs));

        var view = await store.GetSiteTargetsAsync(tenant, site);
        Assert.False(view.FollowsTeamDefault);
        view = await store.PutSiteTargetsAsync(tenant, site, new SiteTargetsWrite(view.ConfigVersion,
            [new("deepseek_api", "deepseek", "api", "CN", "zh-CN", true, AnswerMode.Deep)], SiteTargetsScope.Monitoring), default, markIndividual: true);
        Assert.False(view.FollowsTeamDefault);
        Assert.Equal(2u, view.ConfigVersion);
        Assert.Null(await store.MaterializeTeamDefaultAsync(tenant, site));

        foreach (var call in new Func<Task>[]
        {
            () => store.GetTeamMonitoringConfigAsync(tenant),
            () => store.PutTeamMonitoringConfigAsync(tenant, Guid.NewGuid(), new TeamMonitoringConfigWrite(0, [new("cloro", "chatgpt")])),
            () => store.FollowTeamDefaultAsync(tenant, site, new FollowTeamDefaultRequest(view.ConfigVersion)),
            () => store.MaterializeTeamCellsForFollowersAsync(tenant, [new("cloro", "chatgpt")]),
        })
        {
            var error = await Assert.ThrowsAsync<CollectorStoreException>(call);
            Assert.Equal(("TEAM_MONITORING_SCHEMA_NOT_MIGRATED", 503), (error.Code, error.StatusCode));
        }
        Assert.Equal(2u, (await store.GetSiteTargetsAsync(tenant, site)).ConfigVersion);
    }
}

/// <summary>需要已执行 001–048 的隔离库 JOGEOAI_TEAM_MONITORING_TEST_CONNECTION（库名 jogeoai_team_monitoring_*）；未配置时跳过。</summary>
public sealed class TeamMonitoringMySqlFactAttribute : FactAttribute
{
    public TeamMonitoringMySqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JOGEOAI_TEAM_MONITORING_TEST_CONNECTION")))
            Skip = "需提供已执行 001–048 的独立测试库 JOGEOAI_TEAM_MONITORING_TEST_CONNECTION（库名 jogeoai_team_monitoring_*）。";
    }
}

/// <summary>需要已执行 001–047、尚未执行 048 的隔离库 JOGEOAI_TEAM_MONITORING_PRE048_TEST_CONNECTION（库名 jogeoai_team_monitoring_*）；未配置时跳过。</summary>
public sealed class TeamMonitoringPre048MySqlFactAttribute : FactAttribute
{
    public TeamMonitoringPre048MySqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JOGEOAI_TEAM_MONITORING_PRE048_TEST_CONNECTION")))
            Skip = "需提供已执行 001–047、尚未执行 048 的独立测试库 JOGEOAI_TEAM_MONITORING_PRE048_TEST_CONNECTION（库名 jogeoai_team_monitoring_*）。";
    }
}
