using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MySqlConnector;
using static Jogeoai.Tests.TeamMonitoringConfigSqlTests;

namespace Jogeoai.Tests;

/// <summary>
/// 隔离库（已执行 001–049、库名含 _test）上的 App 目标：目录的 App 段、站点目标的保存与锚点、scope 的两种取值、团队默认单元格的下发，
/// 以及（AppSiteTargetsSqlTests.Compat.cs）没有 App 目标的品牌不变、有 App 目标的品牌其每日监控不变、立即采集与手动采集不含 App 目标。
/// 未设置 JOGEOAI_APP_CHANNEL_TEST_CONNECTION 时跳过，绝不使用业务库 jogeoai。每个用例只用自己新建的租户，结束时删除该租户的全部行，
/// 并核对整库每张表的行数与用例开始前相同。与迁移用例同一个集合：它们会执行 DDL，不能并行。
/// </summary>
[Collection(AppChannelSqlCollection.Name)]
public sealed partial class AppSiteTargetsSqlTests
{
    private const string Zone = "Asia/Shanghai";
    private static readonly SiteTargetWrite ChatGpt = new("cloro", "chatgpt", "scraped", "US", "en-US");
    private static readonly SiteTargetWrite Gemini = new("cloro", "gemini", "scraped", "US", "en-US");
    private static readonly string ChatGptKey = CollectionIdentity.TargetKey("cloro", "chatgpt", "scraped", "US", "en-US");
    private static readonly string GeminiKey = CollectionIdentity.TargetKey("cloro", "gemini", "scraped", "US", "en-US");
    private static readonly string WebKey = CollectionIdentity.TargetKey("domestic_collector", "doubao", "consumer_web", "CN", "zh-CN");

    private static SiteTargetWrite App(string engine, bool enabled = true, MonitoringFrequency frequency = MonitoringFrequency.Weekly)
        => new("app_collector", engine, "app", "CN", "zh-CN", enabled, null, frequency);

    private static readonly SiteTargetWrite[] AvailableApps =
        AppChannelDefinition.Engines.Where(engine => engine != "ernie").Select(engine => App(engine)).ToArray();

    private static string ConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable(AppChannelMySqlFactAttribute.ConnectionVariable)!;
        var name = new MySqlConnectionStringBuilder(connectionString).Database;
        Assert.Contains("_test", name, StringComparison.Ordinal);
        Assert.NotEqual("jogeoai", name);
        return connectionString;
    }

    private static DateOnly Today() => ManagementStore.BrandLocalDate(Zone, DateTimeOffset.UtcNow);

    // The App channel is closed unless the tenant is listed in Collector:AppTenantIds: the cases open their own tenants.
    internal static IOptions<AppChannelOptions> OpenFor(params Guid[] tenants)
        => Options.Create(new AppChannelOptions { AppTenantIds = tenants.Select(tenant => tenant.ToString()).ToArray() });

    internal static ManagementStore OpenStore(MySqlDatabase database, params Guid[] tenants) => new(database, appChannel: OpenFor(tenants));

    private static ManagementStore OpenStore(string cs, params Guid[] tenants) => OpenStore(new MySqlDatabase(cs), tenants);

    private static (string, int) Code(CollectorStoreException exception) => (exception.Code, exception.StatusCode);

    // Runs a case on tenants it creates itself, removes every row of those tenants and then checks that no table gained or lost a row.
    internal static async Task IsolatedAsync(Func<string, Func<Task<Guid>>, Task> body)
    {
        var cs = ConnectionString();
        var before = await RowCountsAsync(cs);
        var tenants = new List<Guid>();
        try
        {
            await body(cs, async () =>
            {
                var tenant = await SeedTenantAsync(cs);
                tenants.Add(tenant);
                return tenant;
            });
        }
        finally
        {
            foreach (var tenant in tenants) await DeleteTenantAsync(cs, tenant);
        }
        Assert.Equal(before, await RowCountsAsync(cs));
    }

    private static async Task<SortedDictionary<string, long>> RowCountsAsync(string cs)
    {
        var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in await RowsAsync(cs, """
            SELECT table_name FROM information_schema.tables WHERE table_schema = DATABASE() AND table_type = 'BASE TABLE' ORDER BY table_name;
            """))
            counts[table[0]!] = long.Parse((await RowsAsync(cs, $"SELECT COUNT(*) FROM `{table[0]}`;"))[0][0]!, System.Globalization.CultureInfo.InvariantCulture);
        return counts;
    }

    // Deletes everything that belongs to one test tenant: every table that carries one of the owner columns below, by the ids
    // collected up front. Foreign-key checks are off for this session only, so the order of the tables does not matter.
    private static async Task DeleteTenantAsync(string cs, Guid tenant)
    {
        async Task<string[]> Ids(string sql) => (await RowsAsync(cs, sql, ("@tenant", tenant.ToString()))).Select(row => row[0]!).ToArray();
        var owners = new (string Column, string[] Ids)[]
        {
            ("tenant_id", [tenant.ToString()]),
            ("site_id", await Ids("SELECT BIN_TO_UUID(id) FROM sites WHERE tenant_id = UUID_TO_BIN(@tenant);")),
            ("run_id", await Ids("SELECT BIN_TO_UUID(id) FROM visibility_runs WHERE tenant_id = UUID_TO_BIN(@tenant);")),
            ("schedule_id", await Ids("SELECT BIN_TO_UUID(id) FROM collection_schedules WHERE tenant_id = UUID_TO_BIN(@tenant);")),
            ("check_id", await Ids("SELECT BIN_TO_UUID(id) FROM visibility_checks WHERE tenant_id = UUID_TO_BIN(@tenant);")),
            ("prompt_id", await Ids("SELECT BIN_TO_UUID(p.id) FROM prompts p JOIN sites s ON s.id = p.site_id WHERE s.tenant_id = UUID_TO_BIN(@tenant);")),
        };
        await using var connection = new MySqlConnection(cs);
        await connection.OpenAsync();
        await using (var off = new MySqlCommand("SET SESSION foreign_key_checks = 0;", connection)) await off.ExecuteNonQueryAsync();
        foreach (var (column, ids) in owners.Where(owner => owner.Ids.Length > 0))
        {
            var tables = new List<string>();
            await using (var find = new MySqlCommand("""
                SELECT c.table_name FROM information_schema.columns c
                JOIN information_schema.tables t ON t.table_schema = c.table_schema AND t.table_name = c.table_name
                WHERE c.table_schema = DATABASE() AND c.column_name = @column AND c.data_type = 'binary' AND t.table_type = 'BASE TABLE';
                """, connection))
            {
                find.Parameters.AddWithValue("@column", column);
                await using var reader = await find.ExecuteReaderAsync();
                while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
            }
            var list = string.Join(",", ids.Select(id => $"UUID_TO_BIN('{Guid.Parse(id)}')"));
            foreach (var table in tables)
            {
                await using var delete = new MySqlCommand($"DELETE FROM `{table}` WHERE `{column}` IN ({list});", connection);
                await delete.ExecuteNonQueryAsync();
            }
        }
        await using var last = new MySqlCommand("DELETE FROM tenants WHERE id = UUID_TO_BIN(@tenant); SET SESSION foreign_key_checks = 1;", connection);
        last.Parameters.AddWithValue("@tenant", tenant.ToString());
        await last.ExecuteNonQueryAsync();
    }

    private sealed record TargetRow(string Provider, string Engine, string Capture, string Country, string Locale, bool Enabled, string? Mode,
        string Frequency, string? Anchor, bool Activated, string UpdatedAt);

    private static async Task<Dictionary<string, TargetRow>> TargetRowsAsync(string cs, Guid site)
        => (await RowsAsync(cs, """
                SELECT target_key, provider, engine, capture_mode, country, locale, enabled, answer_mode, frequency,
                       DATE_FORMAT(frequency_anchor, '%Y-%m-%d'), monitoring_enabled_at IS NOT NULL, DATE_FORMAT(updated_at, '%Y-%m-%d %H:%i:%s.%f')
                FROM site_targets WHERE site_id = UUID_TO_BIN(@site);
                """, ("@site", site.ToString())))
            .ToDictionary(r => r[0]!, r => new TargetRow(r[1]!, r[2]!, r[3]!, r[4]!, r[5]!, r[6] is "1" or "True", r[7], r[8]!, r[9], r[10] is "1" or "True", r[11]!));

    private static string Day(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    // 3.1: the App segment follows the three existing segments; availability follows the consumer_app entry only.
    [AppChannelMySqlFact]
    public Task CatalogAppendsOneAppRowPerPlatformAndFollowsTheAppEntryOnly() => IsolatedAsync(async (cs, _) =>
    {
        var tenant = Guid.NewGuid();
        var store = OpenStore(cs, tenant);
        var catalog = await store.GetTargetCatalogAsync(tenantId: tenant);
        var app = catalog.Where(c => c.Provider == "app_collector").ToArray();
        Assert.Equal(AppChannelDefinition.Engines, app.Select(c => c.Engine));
        var others = catalog.Take(catalog.Count - app.Length).ToArray();
        Assert.Equal(app, catalog.Skip(others.Length));
        Assert.DoesNotContain(others, c => c.Provider == "app_collector");

        // The existing segments are still Cloro, the API providers and the domestic web collector, in that order.
        var api = AnswerProviderDefinition.All.Sum(d => d.Engines.Count);
        Assert.Equal(api + 7, others.Length);
        Assert.Equal(AnswerProviderDefinition.All.SelectMany(d => d.Engines.Select(e => $"{d.Provider}:{e}")), others.Take(api).Select(c => $"{c.Provider}:{c.Engine}"));
        Assert.All(others.Take(api), c => Assert.Equal((CollectionChannel.Api, true), (c.Channel!.Value, c.MonitoringEligible)));
        Assert.All(others.Skip(api), c => Assert.Equal(("domestic_collector", "consumer_web", CollectionChannel.Web, false),
            (c.Provider, c.CaptureMode, c.Channel!.Value, c.MonitoringEligible)));
        Assert.All(others, c => Assert.True(c.FixedFrequency is null && c.UnavailableReason is null));

        foreach (var platform in AppChannelDefinition.Platforms)
        {
            var state = platform.Code == "ernie"
                ? new ManagementStore.AppEntryState(false, AppUnavailableReasons.ContentUnreadable)
                : new ManagementStore.AppEntryState(true, null);
            Assert.Equal(ManagementStore.AppCatalogRow(platform, state), app.Single(c => c.Engine == platform.Code));
        }
        var ernie = app.Single(c => c.Engine == "ernie");
        Assert.Equal(("unavailable", "APP_CONTENT_UNREADABLE", "App 内容无法读取"), (ernie.Availability, ernie.UnavailableReason?.Code, ernie.UnavailableReason?.Message));
        Assert.Equal(6, app.Count(c => c.Availability == "available" && c.UnavailableReason is null));

        // collector_platforms.enabled is the web channel's switch: turning it off changes the web row, never the App row.
        try
        {
            await ExecAsync(cs, "UPDATE collector_platforms SET enabled = FALSE WHERE code = 'kimi';");
            var switched = await store.GetTargetCatalogAsync(tenantId: tenant);
            Assert.Equal("unavailable", switched.Single(c => c.Provider == "domestic_collector" && c.Engine == "kimi").Availability);
            Assert.Equal(app.Single(c => c.Engine == "kimi"), switched.Single(c => c.Provider == "app_collector" && c.Engine == "kimi"));
        }
        finally { await ExecAsync(cs, "UPDATE collector_platforms SET enabled = TRUE WHERE code = 'kimi';"); }
        Assert.Equal(catalog, await store.GetTargetCatalogAsync(tenantId: tenant));

        // 3.9 A: without a tenant, for a tenant that is not listed, and with an empty or missing list, the catalog is exactly
        // the other three segments.
        Assert.Equal(others, await store.GetTargetCatalogAsync());
        Assert.Equal(others, await store.GetTargetCatalogAsync(tenantId: Guid.NewGuid()));
        Assert.Equal(others, await OpenStore(cs).GetTargetCatalogAsync(tenantId: tenant));
        Assert.Equal(others, await new ManagementStore(new MySqlDatabase(cs)).GetTargetCatalogAsync(tenantId: tenant));

        // Through the container, registered and bound the way Program.cs does it: the list in configuration opens the tenant,
        // and a configuration without the key leaves the channel closed.
        IManagementStore Resolve(Dictionary<string, string?> values)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            var services = new ServiceCollection();
            services.AddSingleton(new MySqlDatabase(cs));
            services.Configure<AppChannelOptions>(options =>
                options.AppTenantIds = configuration.GetSection(AppChannelOptions.TenantIdsKey).Get<string[]>() ?? []);
            services.AddSingleton<IManagementStore, ManagementStore>();
            return services.BuildServiceProvider().GetRequiredService<IManagementStore>();
        }
        Assert.Equal(catalog, await Resolve(new() { ["Collector:AppTenantIds:0"] = tenant.ToString() }).GetTargetCatalogAsync(tenantId: tenant));
        Assert.Equal(others, await Resolve(new() { ["Collector:ChinaTenantIds:0"] = tenant.ToString() }).GetTargetCatalogAsync(tenantId: tenant));
    });

    // 3.2 / 3.5: an App target is stored on its fixed surface; the anchor is the enabling day (11-27-app-first-cycle-now: its first
    // cycle starts at once through an instant slot) and survives a re-save.
    [AppChannelMySqlFact]
    public Task PutStoresAppTargetsWithTodayAsAnchorAndAResaveKeepsIt() => IsolatedAsync(async (cs, seedTenant) =>
    {
        var tenant = await seedTenant();
        var site = await SeedBrandAsync(cs, tenant, "App 目标", follows: false, locale: "en-GB", created: "2026-05-01");
        var store = OpenStore(cs, tenant);
        var today = Day(Today());
        var version = (await store.GetSiteTargetsAsync(tenant, site)).ConfigVersion;
        SiteTargetWrite[] targets = [ChatGpt with { Frequency = MonitoringFrequency.Weekly }, App("doubao"), App("kimi", enabled: false)];

        var view = await store.PutSiteTargetsAsync(tenant, site, new SiteTargetsWrite(version, targets), markIndividual: true);
        var rows = await TargetRowsAsync(cs, site);
        Assert.Equal(3, rows.Count);
        var doubao = rows[CollectionIdentity.AppTargetKey("doubao")];
        Assert.Equal(new TargetRow("app_collector", "doubao", "app", "CN", "zh-CN", true, null, "weekly", today, true, doubao.UpdatedAt), doubao);
        var kimi = rows[CollectionIdentity.AppTargetKey("kimi")];
        Assert.Equal(new TargetRow("app_collector", "kimi", "app", "CN", "zh-CN", false, null, "weekly", null, false, kimi.UpdatedAt), kimi);
        // API targets are anchored today as well.
        Assert.Equal(("weekly", today), (rows[ChatGptKey].Frequency, rows[ChatGptKey].Anchor));

        // The response lists the App rows after the other rows, with the three new fields.
        Assert.Equal(["cloro:chatgpt", "app_collector:doubao", "app_collector:kimi"], view.Items.Select(i => $"{i.Provider}:{i.Engine}"));
        var item = view.Items[1];
        Assert.Equal((CollectionChannel.App, MonitoringFrequency.Weekly, MonitoringFrequency.Weekly, DateOnly.Parse(today), AnswerMode.Fast),
            (item.Channel!.Value, item.FixedFrequency!.Value, item.Frequency!.Value, item.FrequencyAnchor!.Value, item.AnswerMode!.Value));
        Assert.Equal(("豆包", "app", "CN", "zh-CN", true, "available", true, CollectionIdentity.AppTargetKey("doubao")),
            (item.DisplayName, item.CaptureMode, item.Country, item.Locale, item.Enabled, item.Availability, item.MonitoringEligible, item.TargetKey));
        Assert.Null(item.UnavailableReason);
        Assert.Equal("disabled", view.Items[2].Availability);
        Assert.Null(view.Items[2].FrequencyAnchor);
        Assert.Equal(CollectionChannel.Api, view.Items[0].Channel);
        Assert.Null(view.Items[0].FixedFrequency);
        Assert.Null(view.Items[0].UnavailableReason);

        // Re-saving an enabled App target keeps its anchor; a target enabled by the same save gets today.
        await ExecAsync(cs, "UPDATE site_targets SET frequency_anchor = IF(provider = 'cloro', '2026-01-06', '2026-01-05') WHERE site_id = UUID_TO_BIN(@site) AND enabled = TRUE;",
            ("@site", site.ToString()));
        view = await store.PutSiteTargetsAsync(tenant, site, new SiteTargetsWrite(view.ConfigVersion, [targets[0], App("doubao"), App("kimi")]), markIndividual: true);
        rows = await TargetRowsAsync(cs, site);
        Assert.Equal(("2026-01-05", today, "2026-01-06"),
            (rows[CollectionIdentity.AppTargetKey("doubao")].Anchor, rows[CollectionIdentity.AppTargetKey("kimi")].Anchor, rows[ChatGptKey].Anchor));

        // Disabling keeps the stored anchor; enabling again starts a new cycle today.
        view = await store.PutSiteTargetsAsync(tenant, site, new SiteTargetsWrite(view.ConfigVersion, [targets[0], App("doubao", enabled: false), App("kimi")]), markIndividual: true);
        var disabled = (await TargetRowsAsync(cs, site))[CollectionIdentity.AppTargetKey("doubao")];
        Assert.False(disabled.Enabled);
        Assert.Equal("2026-01-05", disabled.Anchor);
        view = await store.PutSiteTargetsAsync(tenant, site, new SiteTargetsWrite(view.ConfigVersion, [targets[0], App("doubao"), App("kimi")]), markIndividual: true);
        Assert.Equal(today, (await TargetRowsAsync(cs, site))[CollectionIdentity.AppTargetKey("doubao")].Anchor);

        // Rejected saves change nothing: an unavailable platform (with the reason), a non-weekly frequency, a wrong surface.
        var before = await TargetRowsAsync(cs, site);
        var unavailable = await Assert.ThrowsAsync<CollectorStoreException>(() => store.PutSiteTargetsAsync(tenant, site,
            new SiteTargetsWrite(view.ConfigVersion, [targets[0], App("ernie")]), markIndividual: true));
        Assert.Equal(("TARGET_NOT_ALLOWED", 400), Code(unavailable));
        Assert.Contains("App 内容无法读取", unavailable.Message, StringComparison.Ordinal);
        foreach (var (target, code) in new[]
        {
            (App("doubao", frequency: MonitoringFrequency.Daily), MonitoringFrequencyRules.FrequencyInvalid),
            (App("doubao") with { Country = "US", Locale = "en-US" }, "TARGET_NOT_ALLOWED"),
            (App("doubao") with { AnswerMode = AnswerMode.Deep }, AnswerModeRules.AnswerModeUnsupported),
        })
        {
            var rejected = await Assert.ThrowsAsync<CollectorStoreException>(() => store.PutSiteTargetsAsync(tenant, site,
                new SiteTargetsWrite(view.ConfigVersion, [target]), markIndividual: true));
            Assert.Equal((code, 400), Code(rejected));
        }
        Assert.Equal(before, await TargetRowsAsync(cs, site));
        Assert.Equal(view.ConfigVersion, (await store.GetSiteTargetsAsync(tenant, site)).ConfigVersion);
        // A disabled row for an unavailable platform is accepted and reads as disabled with the reason.
        view = await store.PutSiteTargetsAsync(tenant, site, new SiteTargetsWrite(view.ConfigVersion, [targets[0], App("ernie", enabled: false)]), markIndividual: true);
        var ernie = view.Items.Single(i => i.Engine == "ernie");
        Assert.Equal(("文心一言", "disabled", "APP_CONTENT_UNREADABLE"), (ernie.DisplayName, ernie.Availability, ernie.UnavailableReason?.Code));
    });
}
