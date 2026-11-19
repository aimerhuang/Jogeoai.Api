using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using static Jogeoai.Tests.TeamMonitoringConfigSqlTests;

namespace Jogeoai.Tests;

/// <summary>
/// 总 PRD AC-C6a 在隔离库上的真实保存：上限只数 API。已启用 20 个 API 目标的品牌可以再启用 6 个 App 平台；第 21 个 API 目标、
/// API 部分超过每轮 1000 次检测仍被拒绝（有 App 目标时也一样）；提问数达到每品牌上限（<see cref="MonitoringMatrix.MaxPrompts"/> = 200，
/// 套餐的提问上限按租户计、另有 PLAN_LIMIT_EXCEEDED）的品牌启用 App 不返回 MONITORING_TOO_LARGE。
/// </summary>
public sealed partial class AppSiteTargetsSqlTests
{
    // Distinct daily-monitoring targets: the Cloro engines in several regions (the same set as AppSiteTargetsTests).
    private static SiteTargetWrite[] ApiTargets(int count)
        => (from region in new[] { ("US", "en-US"), ("GB", "en-GB"), ("DE", "de-DE"), ("JP", "ja-JP"), ("FR", "fr-FR"), ("CA", "en-CA") }
            from engine in AnswerProviderDefinition.Cloro.Engines
            select new SiteTargetWrite("cloro", engine, "scraped", region.Item1, region.Item2)).Take(count).ToArray();

    // count enabled 'all' prompts with distinct, known creation times (SeedPromptAsync only covers 59 seconds).
    private static async Task SeedPromptsAsync(string cs, Guid site, int count)
    {
        for (var i = 1; i <= count; i++)
        {
            var (prompt, version) = (Guid.NewGuid(), Guid.NewGuid());
            await ExecAsync(cs, """
                INSERT INTO prompts (id,site_id,content,locale,enabled,created_at,updated_at)
                  VALUES (UUID_TO_BIN(@prompt),UUID_TO_BIN(@site),@content,'zh-CN',TRUE,@created,@created);
                INSERT INTO prompt_versions (id,site_id,prompt_id,version,content,locale,branded_status,branded_source,created_at)
                  VALUES (UUID_TO_BIN(@version),UUID_TO_BIN(@site),UUID_TO_BIN(@prompt),1,@content,'zh-CN','unbranded','system',@created);
                UPDATE prompts SET current_version_id=UUID_TO_BIN(@version) WHERE id=UUID_TO_BIN(@prompt);
                """, ("@prompt", prompt.ToString()), ("@site", site.ToString()), ("@version", version.ToString()),
                ("@content", $"第 {i} 条提问：哪个记账工具好用？"), ("@created", $"2026-01-01 00:{i / 60:00}:{i % 60:00}"));
        }
    }

    // enabled API rows | enabled App rows of the brand
    private static Task<string> EnabledAsync(string cs, Guid site)
        => AppCycleRunSqlTests.RowAsync(cs, """
            SELECT COALESCE(SUM(enabled AND provider <> 'app_collector'), 0), COALESCE(SUM(enabled AND provider = 'app_collector'), 0)
            FROM site_targets WHERE site_id = UUID_TO_BIN(@site);
            """, ("@site", site.ToString()));

    [AppChannelMySqlFact]
    public Task TwentyApiTargetsTakeSixAppPlatformsAndTheTwentyFirstApiTargetIsStillRefused() => IsolatedAsync(async (cs, seedTenant) =>
    {
        var tenant = await seedTenant();
        var site = await SeedBrandAsync(cs, tenant, "二十加六", follows: false, locale: "zh-CN", created: "2026-05-01");
        var store = OpenStore(cs, tenant);
        Assert.Equal(6, AvailableApps.Length);
        Assert.Equal(20, MonitoringMatrix.MaxTargets);

        // Saved for real, with both scopes: 20 API targets and every available App platform.
        foreach (var scope in new[] { SiteTargetsScope.All, SiteTargetsScope.Monitoring })
        {
            var version = (await store.GetSiteTargetsAsync(tenant, site)).ConfigVersion;
            var view = await store.PutSiteTargetsAsync(tenant, site, new SiteTargetsWrite(version, [.. ApiTargets(20), .. AvailableApps], scope), markIndividual: true);
            Assert.Equal((20, 6), (view.Items.Count(i => i.Enabled && i.Provider != "app_collector"), view.Items.Count(i => i.Enabled && i.Provider == "app_collector")));
            Assert.Equal("20|6", await EnabledAsync(cs, site));
        }

        // The 21st API target is refused with the existing code and message, with or without the App targets; nothing changes.
        var before = await TargetRowsAsync(cs, site);
        var configVersion = (await store.GetSiteTargetsAsync(tenant, site)).ConfigVersion;
        foreach (var targets in new[] { ApiTargets(21), [.. ApiTargets(21), .. AvailableApps] })
        foreach (var scope in new[] { SiteTargetsScope.All, SiteTargetsScope.Monitoring })
        {
            var tooLarge = await Assert.ThrowsAsync<CollectorStoreException>(() =>
                store.PutSiteTargetsAsync(tenant, site, new SiteTargetsWrite(configVersion, targets, scope), markIndividual: true));
            Assert.Equal(("MONITORING_TOO_LARGE", 400), Code(tooLarge));
            Assert.Equal("每日监控最多启用 20 个目标（同一模型的两种回答模式计为 2 个）。", tooLarge.Message);
        }
        Assert.Equal(before, await TargetRowsAsync(cs, site));
        Assert.Equal(configVersion, (await store.GetSiteTargetsAsync(tenant, site)).ConfigVersion);
    });

    [AppChannelMySqlFact]
    public Task MoreThanOneThousandApiChecksAreStillRefusedWhenTheBrandAlsoHasAppTargets() => IsolatedAsync(async (cs, seedTenant) =>
    {
        var rig = AppCycleRunSqlTests.Build(cs, await seedTenant());
        var site = await SeedBrandAsync(cs, rig.Tenant, "一千零二十", follows: false, locale: "zh-CN", created: "2026-05-01");
        // 51 prompts × 20 API targets = 1020 checks per round, over MonitoringMatrix.MaxChecks.
        await SeedPromptsAsync(cs, site, 51);
        Assert.Equal(1000, MonitoringMatrix.MaxChecks);
        await rig.PutAsync(site, [.. ApiTargets(20), .. AvailableApps]);
        Assert.Equal("20|6", await EnabledAsync(cs, site));

        // The estimate reports MONITORING_TOO_LARGE for the API part exactly as it does without the App targets.
        var apiOnly = await rig.Service.EstimateTargetsAsync(rig.Tenant, site, new MonitoringTargetsEstimateRequest(ApiTargets(20)));
        var withApps = await rig.Service.EstimateTargetsAsync(rig.Tenant, site, new MonitoringTargetsEstimateRequest([.. ApiTargets(20), .. AvailableApps]));
        Assert.Contains("MONITORING_TOO_LARGE", apiOnly.Blockers);
        Assert.Equal(apiOnly.Blockers, withApps.Blockers);
        Assert.NotNull(withApps.Channels);
        Assert.Equal((20, 6), (withApps.Channels!.Api.Targets, withApps.Channels.App.Targets));
        // App targets alone on the same brand: no blocker from the API limits.
        var appsOnly = await rig.Service.EstimateTargetsAsync(rig.Tenant, site, new MonitoringTargetsEstimateRequest(AvailableApps));
        Assert.DoesNotContain("MONITORING_TOO_LARGE", appsOnly.Blockers);

        // Starting monitoring is refused for the API part: no main plan, no API run, no reservation.
        var refused = await Assert.ThrowsAsync<CollectorStoreException>(() => rig.Service.EnsureAsync(rig.Tenant, site));
        Assert.Equal(("MONITORING_TOO_LARGE", 400), Code(refused));
        Assert.Equal("0|0|0", await AppCycleRunSqlTests.RowAsync(cs, """
            SELECT (SELECT COUNT(*) FROM collection_schedules WHERE site_id = UUID_TO_BIN(@site) AND name = @main),
                   (SELECT COUNT(*) FROM visibility_runs WHERE site_id = UUID_TO_BIN(@site)),
                   (SELECT COUNT(*) FROM monitoring_credit_reservations WHERE site_id = UUID_TO_BIN(@site));
            """, ("@site", site.ToString()), ("@main", PromptMonitoringIdentity.ScheduleName)));
    });

    [AppChannelMySqlFact]
    public Task ABrandAtThePromptLimitEnablesAppTargetsWithoutMonitoringTooLarge() => IsolatedAsync(async (cs, seedTenant) =>
    {
        var rig = AppCycleRunSqlTests.Build(cs, await seedTenant());
        var site = await SeedBrandAsync(cs, rig.Tenant, "两百条", follows: false, locale: "zh-CN", created: "2026-05-01");
        Assert.Equal(200, MonitoringMatrix.MaxPrompts);
        await SeedPromptsAsync(cs, site, MonitoringMatrix.MaxPrompts);

        // Saving every available App platform succeeds.
        await rig.PutAsync(site, AvailableApps);
        Assert.Equal("0|6", await EnabledAsync(cs, site));
        // The estimate of the App draft, and of the App draft with one API target (200 API checks), has no MONITORING_TOO_LARGE.
        var appsOnly = await rig.Service.EstimateTargetsAsync(rig.Tenant, site, new MonitoringTargetsEstimateRequest(AvailableApps));
        Assert.DoesNotContain("MONITORING_TOO_LARGE", appsOnly.Blockers);
        Assert.Equal(200, appsOnly.PromptCount);
        Assert.Equal((0, 6), (appsOnly.Channels!.Api.Targets, appsOnly.Channels.App.Targets));
        var withApi = await rig.Service.EstimateTargetsAsync(rig.Tenant, site, new MonitoringTargetsEstimateRequest([ApiTargets(1)[0], .. AvailableApps]));
        Assert.DoesNotContain("MONITORING_TOO_LARGE", withApi.Blockers);

        // Starting monitoring creates the App plan, no main plan, and nothing is refused.
        var started = await rig.Service.EnsureAsync(rig.Tenant, site);
        Assert.Null(started.Schedule);
        Assert.NotNull(started.AppSchedule);
    });
}
