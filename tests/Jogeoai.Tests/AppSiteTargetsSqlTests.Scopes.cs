using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using static Jogeoai.Tests.TeamMonitoringConfigSqlTests;

namespace Jogeoai.Tests;

/// <summary>
/// 提问的自定义平台范围里的 App 目标键（implement 3.9，总 PRD FR-C2，隔离库）：经真实写入路径（创建、PATCH、批量创建、预估）
/// 可以写入并读回该品牌已启用的 App 目标键；每日监控的计划、快照、选择哈希、时隙与 API 数字不受影响；只含 App 目标键的范围
/// 在 API 一侧不产生任何检测，也不回退成全部目标。
/// </summary>
public sealed partial class AppSiteTargetsSqlTests
{
    private static async Task<string[]> ScopeRowsAsync(string cs, Guid prompt)
        => (await RowsAsync(cs, "SELECT target_key FROM prompt_target_scopes WHERE prompt_id = UUID_TO_BIN(@prompt) ORDER BY target_key;",
            ("@prompt", prompt.ToString()))).Select(r => r[0]!).ToArray();

    private static async Task<string> ScopeKindAsync(string cs, Guid prompt)
        => (await RowsAsync(cs, "SELECT target_scope FROM prompts WHERE id = UUID_TO_BIN(@prompt);", ("@prompt", prompt.ToString())))[0][0]!;

    // The lines of ApiStateAsync that describe the daily plans themselves (not the prompt counts shown next to them).
    // night= is a planned 00:05 slot; it only means something for a brand that has a twice_daily cell.
    private static string PlanLines(string state, bool night = true)
        => string.Join("\n", state.Split('\n').Where(line => new[] { "plans=", "hash=", "targets=", "main=", night ? "night=" : "main=" }.Any(line.StartsWith)));

    private static string[] Sorted(params string[] keys) => keys.Order(StringComparer.Ordinal).ToArray();

    [AppChannelMySqlFact]
    public Task PromptScopesStoreEnabledAppTargetKeysAndTheDailyMonitoringIgnoresThem() => IsolatedAsync(async (cs, seedTenant) =>
    {
        var tenant = await seedTenant();
        var database = new MySqlDatabase(cs);
        var store = OpenStore(database, tenant);
        var service = Service(database, store, tenant);
        var brand = await SeedBrandAsync(cs, tenant, "范围含 App", follows: false, locale: "zh-CN", created: "2026-05-15");
        var (doubao, kimi, qwen, deepseek) = (CollectionIdentity.AppTargetKey("doubao"), CollectionIdentity.AppTargetKey("kimi"),
            CollectionIdentity.AppTargetKey("qwen"), CollectionIdentity.AppTargetKey("deepseek"));
        SiteTargetWrite[] apiDrafts = [ChatGpt, Gemini, DeepSeekTwice];
        await store.PutSiteTargetsAsync(tenant, brand, new SiteTargetsWrite((await store.GetSiteTargetsAsync(tenant, brand)).ConfigVersion,
            [.. apiDrafts, App("doubao"), App("kimi"), App("qwen", enabled: false)]), markIndividual: true);

        // Two prompts through the real write path: one 'all', one custom with an API key only. Its stored form is what it
        // was before App keys were accepted: one row, the API key.
        var all = await store.CreatePromptAsync(tenant, brand, new PromptWrite("范围为全部平台的提问：哪个写作工具好用？", "zh-CN"));
        var custom = await store.CreatePromptAsync(tenant, brand, new PromptWrite("只在 Gemini 监测的提问：哪个翻译工具好用？", "zh-CN", TargetKeys: [GeminiKey]));
        Assert.Equal(("custom", GeminiKey), (custom.TargetScope, Assert.Single(custom.TargetKeys!)));
        Assert.Equal([GeminiKey], await ScopeRowsAsync(cs, custom.PromptId));
        await CreateMainPlanAsync(database, tenant, brand, [all.PromptId, custom.PromptId], apiDrafts);
        var before = await ApiStateAsync(cs, database, service, tenant, brand, apiDrafts);
        Assert.Equal(before, await ApiStateAsync(cs, database, service, tenant, brand, apiDrafts));
        Assert.Contains("promptTargets", before, StringComparison.Ordinal);

        // PATCH: the scope gains two App keys. They are stored and read back (sorted with the API key); the plans, their
        // snapshots and versions, the selection hash, the planned slots, billing and both estimates are identical.
        var patched = await store.UpdatePromptAsync(tenant, brand, custom.PromptId,
            new PromptPatch(ExpectedVersion: (int)custom.ConfigVersion, TargetKeys: [kimi, GeminiKey, doubao]));
        Assert.Equal(("custom", custom.Version), (patched.TargetScope, patched.Version));
        Assert.Equal(Sorted(GeminiKey, doubao, kimi), patched.TargetKeys);
        Assert.Equal(Sorted(GeminiKey, doubao, kimi), await ScopeRowsAsync(cs, custom.PromptId));
        Assert.Equal(patched.TargetKeys, (await store.GetPromptAsync(tenant, brand, custom.PromptId))!.TargetKeys);
        var after = await ApiStateAsync(cs, database, service, tenant, brand, apiDrafts);
        Assert.Equal(before, after);
        Assert.DoesNotContain(doubao, after, StringComparison.Ordinal);
        Assert.DoesNotContain(kimi, after, StringComparison.Ordinal);
        // An App key added to a scope adds no cell to collect right away.
        Assert.Null(await service.CollectAddedTargetsAsync(tenant, brand, custom, patched));

        // A prompt whose scope is App keys only: stored, and on the API side it has no check at all — it is not in the plan,
        // the plan does not change, and the per-round figures stay where they were (never "all targets").
        var appOnly = await store.CreatePromptAsync(tenant, brand, new PromptWrite("只在 App 监测的提问：哪个笔记工具好用？", "zh-CN", TargetKeys: [kimi, doubao]));
        Assert.Equal("custom", appOnly.TargetScope);
        Assert.Equal(Sorted(doubao, kimi), appOnly.TargetKeys);
        Assert.Equal(Sorted(doubao, kimi), await ScopeRowsAsync(cs, appOnly.PromptId));
        var withAppOnly = await ApiStateAsync(cs, database, service, tenant, brand, apiDrafts);
        Assert.Equal(PlanLines(before), PlanLines(withAppOnly));
        Assert.DoesNotContain(appOnly.PromptId.ToString(), PlanLines(withAppOnly), StringComparison.OrdinalIgnoreCase);
        var monitoring = await service.GetAsync(tenant, brand);
        Assert.DoesNotContain(appOnly.PromptId, monitoring.Schedule!.Selection.PromptIds);
        // 'all' prompt × 3 cells + custom prompt × gemini = 4 checks per round, with or without the App-only prompt.
        var current = await service.EstimateAsync(tenant, brand, new PromptMonitoringEstimateRequest(0));
        Assert.Equal((3, 4L), (current.CurrentPromptCount, current.CurrentChecksPerRound));
        Assert.Equal(4L, monitoring.Billing!.CheckCount);
        var targetsEstimate = await service.EstimateTargetsAsync(tenant, brand, new MonitoringTargetsEstimateRequest([.. apiDrafts, App("doubao"), App("kimi")]));
        // implement 8.1: the App cells are listed after the API cells; the API cells are what they were.
        Assert.Equal([1, 1, 2], targetsEstimate.Cells.Where(c => c.Channel == CollectionChannel.Api).OrderBy(c => c.PromptCount).Select(c => c.PromptCount));
        // Each App cell counts the prompts whose scope covers it: the 'all' prompt, the custom one and the App-only one.
        Assert.Equal([(doubao, 3), (kimi, 3)], targetsEstimate.Cells.Where(c => c.Channel == CollectionChannel.App).Select(c => (c.TargetKey, c.PromptCount)));

        // The estimate for new prompts takes the same scopes: App keys add nothing, an API key next to them counts once.
        var none = await service.EstimateAsync(tenant, brand, new PromptMonitoringEstimateRequest(2, [doubao]));
        Assert.Equal((0L, 4L), (none.AddedChecksPerRound, none.TotalChecksPerRound));
        Assert.Empty(none.Blockers);
        var mixed = await service.EstimateAsync(tenant, brand, new PromptMonitoringEstimateRequest(2, [doubao, ChatGptKey]));
        Assert.Equal((2L, 6L), (mixed.AddedChecksPerRound, mixed.TotalChecksPerRound));

        // Batch create with a scope that names an App key; a replay of the same key writes nothing more.
        PromptImportRow[] batch = [new("批量创建的提问一：哪个画图工具好用？", "zh-CN"), new("批量创建的提问二：哪个表格工具好用？", "zh-CN")];
        var idempotencyKey = $"app-scope-{Guid.NewGuid():N}";
        var imported = await store.ImportAsync(tenant, brand, batch, idempotencyKey, default, [doubao, ChatGptKey]);
        Assert.Equal(2, imported.CreatedCount);
        foreach (var prompt in imported.CreatedPromptIds!)
            Assert.Equal(Sorted(ChatGptKey, doubao), await ScopeRowsAsync(cs, prompt));
        var replay = await store.ImportAsync(tenant, brand, batch, idempotencyKey, default, [doubao, ChatGptKey]);
        Assert.Equal(imported.CreatedPromptIds, replay.CreatedPromptIds);
        Assert.Equal("5", (await RowsAsync(cs, "SELECT COUNT(*) FROM prompts WHERE site_id = UUID_TO_BIN(@site);", ("@site", brand.ToString())))[0][0]);
        // The instant collection of the new prompts covers their API cell only.
        Assert.Equal(6L, (await service.EstimateAsync(tenant, brand, new PromptMonitoringEstimateRequest(0))).CurrentChecksPerRound);

        // Refused: an App target that is disabled for the brand, one the brand does not have, and any App key for a tenant
        // the App channel is not open for. Nothing is written.
        var scopes = await ScopeRowsAsync(cs, custom.PromptId);
        foreach (var key in new[] { qwen, deepseek })
        {
            await RefusedAsync(() => store.CreatePromptAsync(tenant, brand, new PromptWrite($"被拒绝的提问 {key[..8]}？", "zh-CN", TargetKeys: [GeminiKey, key])),
                PromptMonitoringScale.TargetInvalidCode);
            await RefusedAsync(() => store.UpdatePromptAsync(tenant, brand, custom.PromptId, new PromptPatch(ExpectedVersion: (int)patched.ConfigVersion, TargetKeys: [key])),
                PromptMonitoringScale.TargetInvalidCode);
            await RefusedAsync(() => store.ImportAsync(tenant, brand, [new("被拒绝的批量提问？", "zh-CN")], $"app-scope-{Guid.NewGuid():N}", default, [key]),
                PromptMonitoringScale.TargetInvalidCode);
            await RefusedAsync(() => service.EstimateAsync(tenant, brand, new PromptMonitoringEstimateRequest(1, [key])), PromptMonitoringScale.TargetInvalidCode);
        }
        var closed = new ManagementStore(database);
        await RefusedAsync(() => closed.UpdatePromptAsync(tenant, brand, custom.PromptId, new PromptPatch(ExpectedVersion: (int)patched.ConfigVersion, TargetKeys: [GeminiKey, doubao])),
            PromptMonitoringScale.TargetInvalidCode);
        // The closed store still accepts and stores a scope of API keys, and reads back what is stored.
        Assert.Equal(scopes, await ScopeRowsAsync(cs, custom.PromptId));
        Assert.Equal(scopes, (await closed.GetPromptAsync(tenant, brand, custom.PromptId))!.TargetKeys);
        Assert.Equal("5", (await RowsAsync(cs, "SELECT COUNT(*) FROM prompts WHERE site_id = UUID_TO_BIN(@site);", ("@site", brand.ToString())))[0][0]);

        // Restoring 'all' removes the rows, App keys included.
        var reset = await store.UpdatePromptAsync(tenant, brand, appOnly.PromptId, new PromptPatch(ExpectedVersion: (int)appOnly.ConfigVersion, TargetKeys: []));
        Assert.Equal(("all", null), (reset.TargetScope, reset.TargetKeys));
        Assert.Empty(await ScopeRowsAsync(cs, appOnly.PromptId));
        Assert.Equal("all", await ScopeKindAsync(cs, appOnly.PromptId));
    });

    // The only custom prompt of the brand is scoped to App keys: the plan carries no promptTargets at all, and the API side
    // still counts the 'all' prompt only.
    [AppChannelMySqlFact]
    public Task APromptScopedToAppKeysOnlyHasNoApiCheckEvenWhenItIsTheOnlyCustomPrompt() => IsolatedAsync(async (cs, seedTenant) =>
    {
        var tenant = await seedTenant();
        var database = new MySqlDatabase(cs);
        var store = OpenStore(database, tenant);
        var service = Service(database, store, tenant);
        var brand = await SeedBrandAsync(cs, tenant, "只有 App 范围", follows: false, locale: "zh-CN", created: "2026-05-16");
        var kimi = CollectionIdentity.AppTargetKey("kimi");
        SiteTargetWrite[] apiDrafts = [ChatGpt, Gemini];
        await store.PutSiteTargetsAsync(tenant, brand, new SiteTargetsWrite((await store.GetSiteTargetsAsync(tenant, brand)).ConfigVersion,
            [.. apiDrafts, App("kimi")]), markIndividual: true);
        var all = await store.CreatePromptAsync(tenant, brand, new PromptWrite("全部平台的提问：哪个邮箱好用？", "zh-CN"));
        await CreateMainPlanAsync(database, tenant, brand, [all.PromptId], apiDrafts);
        var before = await ApiStateAsync(cs, database, service, tenant, brand, apiDrafts);
        var billingBefore = (await service.GetAsync(tenant, brand)).Billing!;
        Assert.Equal(2L, billingBefore.CheckCount);

        var appOnly = await store.CreatePromptAsync(tenant, brand, new PromptWrite("只在 Kimi App 监测的提问：哪个日历好用？", "zh-CN", TargetKeys: [kimi]));
        Assert.Equal([kimi], await ScopeRowsAsync(cs, appOnly.PromptId));
        var after = await ApiStateAsync(cs, database, service, tenant, brand, apiDrafts);
        // No twice_daily cell here, so the brand has no night plan: its plans are the main plan only.
        Assert.DoesNotContain(PromptMonitoringIdentity.NightScheduleName, after, StringComparison.Ordinal);
        Assert.Equal(PlanLines(before, night: false), PlanLines(after, night: false));
        Assert.DoesNotContain("promptTargets", after, StringComparison.Ordinal);
        var monitoring = await service.GetAsync(tenant, brand);
        Assert.Equal([all.PromptId], monitoring.Schedule!.Selection.PromptIds);
        var estimate = await service.EstimateAsync(tenant, brand, new PromptMonitoringEstimateRequest(0));
        Assert.Equal((2, 2L), (estimate.CurrentPromptCount, estimate.CurrentChecksPerRound));
        // The billing view counts what the next round will reserve: the App-only prompt adds nothing.
        Assert.Equal((billingBefore.CheckCount, billingBefore.EstimatedCredits), (monitoring.Billing!.CheckCount, monitoring.Billing.EstimatedCredits));
    });
}
