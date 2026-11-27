using System.Text.Json;
using Jogeoai.Api;
using Jogeoai.Api.Controllers;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Jogeoai.Providers.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using static Jogeoai.Tests.TeamMonitoringConfigSqlTests;

namespace Jogeoai.Tests;

/// <summary>人工排除与 appEligibility（implement 4.6）、团队监控页的“监控已开启”（4.6a）、App Run 的读取（4.4a）。</summary>
public sealed partial class AppCycleRunSqlTests
{
    // Exclusions are appended and revoked, never deleted; at most one is active per prompt. The prompt list and view of an
    // open tenant carry appEligibility; a tenant the channel is not open for gets the same JSON as before.
    [AppChannelMySqlFact]
    public Task ExclusionsAreAppendedAndRevokedAndThePromptViewsCarryAppEligibilityForOpenTenantsOnly() => AppSiteTargetsSqlTests.IsolatedAsync(async (cs, seedTenant) =>
    {
        var rig = Build(cs, await seedTenant());
        var closed = Build(cs, rig.Tenant, open: false);
        var site = await SeedAppBrandAsync(rig, "人工排除", "doubao");
        var ok = await SeedPromptAsync(cs, site, "哪个记账工具好用？", 1);
        var url = await SeedPromptAsync(cs, site, "www.example.com 上的记账工具怎么样？", 2);
        var tooLong = await SeedPromptAsync(cs, site, new string('长', AppPromptRules.MaxLength + 1), 3);
        var excluded = await SeedPromptAsync(cs, site, "预算管理应用怎么选？", 4);
        var excludedAndBad = await SeedPromptAsync(cs, site, "第一行\n第二行", 5);
        var (operatorA, operatorB) = (Guid.NewGuid(), Guid.NewGuid());

        var added = await rig.App.ExcludePromptAsync(rig.Tenant, site, excluded, "  涉及竞品比较  ", operatorA);
        Assert.Equal((site, excluded, "涉及竞品比较", operatorA, null, null), (added.SiteId, added.PromptId, added.Reason, added.CreatedBy, added.RevokedBy, added.RevokedAt));
        Assert.InRange(added.CreatedAt, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
        await rig.App.ExcludePromptAsync(rig.Tenant, site, excludedAndBad, "多行文本", operatorA);

        // One active exclusion per prompt; invalid input and foreign prompts are refused without writing.
        Assert.Equal(("APP_PROMPT_ALREADY_EXCLUDED", 409), Code(await Assert.ThrowsAsync<CollectorStoreException>(()
            => rig.App.ExcludePromptAsync(rig.Tenant, site, excluded, "再次排除", operatorB))));
        Assert.Equal(("INVALID_EXCLUSION_REASON", 400), Code(await Assert.ThrowsAsync<CollectorStoreException>(()
            => rig.App.ExcludePromptAsync(rig.Tenant, site, ok, "   ", operatorA))));
        Assert.Equal(("INVALID_EXCLUSION_REASON", 400), Code(await Assert.ThrowsAsync<CollectorStoreException>(()
            => rig.App.ExcludePromptAsync(rig.Tenant, site, ok, new string('因', 501), operatorA))));
        Assert.Equal(("INVALID_ACTOR", 400), Code(await Assert.ThrowsAsync<CollectorStoreException>(()
            => rig.App.ExcludePromptAsync(rig.Tenant, site, ok, "原因", Guid.Empty))));
        Assert.Equal(("RESOURCE_NOT_FOUND", 404), Code(await Assert.ThrowsAsync<CollectorStoreException>(()
            => rig.App.ExcludePromptAsync(rig.Tenant, site, Guid.NewGuid(), "原因", operatorA))));
        Assert.Equal(("RESOURCE_NOT_FOUND", 404), Code(await Assert.ThrowsAsync<CollectorStoreException>(()
            => rig.App.ExcludePromptAsync(Guid.NewGuid(), site, ok, "原因", operatorA))));
        Assert.Equal(("APP_PROMPT_EXCLUSION_NOT_FOUND", 404), Code(await Assert.ThrowsAsync<CollectorStoreException>(()
            => rig.App.RevokePromptExclusionAsync(rig.Tenant, site, ok, operatorA))));
        Assert.Equal(("APP_PROMPT_EXCLUSION_NOT_FOUND", 404), Code(await Assert.ThrowsAsync<CollectorStoreException>(()
            => rig.App.RevokePromptExclusionAsync(Guid.NewGuid(), site, excluded, operatorA))));
        Task<string> ExclusionRowsAsync() => RowAsync(cs, "SELECT COUNT(*), COALESCE(SUM(revoked_at IS NULL), 0) FROM prompt_app_exclusions WHERE tenant_id = UUID_TO_BIN(@tenant);",
            ("@tenant", rig.Tenant.ToString()));
        Assert.Equal("2|2", await ExclusionRowsAsync());

        // appEligibility of an open tenant: the rule's reason, or the operator exclusion — which wins over the rule.
        var expected = new Dictionary<Guid, AppPromptEligibility>
        {
            [ok] = new(true), [url] = new(false, "CONTAINS_URL"), [tooLong] = new(false, "TOO_LONG"),
            [excluded] = new(false, "EXCLUDED_BY_OPERATOR"), [excludedAndBad] = new(false, "EXCLUDED_BY_OPERATOR"),
        };
        var listed = (await rig.Store.ListPromptsAsync(rig.Tenant, site, null, null, null, 100, includeAppEligibility: true)).Items;
        Assert.Equal(expected, listed.ToDictionary(prompt => prompt.PromptId, prompt => prompt.AppEligibility!));
        foreach (var (prompt, eligibility) in expected)
            Assert.Equal(eligibility, (await rig.Store.GetPromptAsync(rig.Tenant, site, prompt, includeAppEligibility: true))!.AppEligibility);
        Assert.Equal("""{"eligible":false,"reasonCode":"EXCLUDED_BY_OPERATOR"}""",
            JsonSerializer.SerializeToNode(listed.Single(prompt => prompt.PromptId == excluded), Api)!["appEligibility"]!.ToJsonString());
        Assert.Equal("""{"eligible":true,"reasonCode":null}""",
            JsonSerializer.SerializeToNode(listed.Single(prompt => prompt.PromptId == ok), Api)!["appEligibility"]!.ToJsonString());

        // A tenant the channel is not open for: no appEligibility member at all; everything else is the same JSON.
        var plain = (await closed.Store.ListPromptsAsync(rig.Tenant, site, null, null, null, 100, includeAppEligibility: true)).Items;
        Assert.All(plain, prompt => Assert.Null(prompt.AppEligibility));
        Assert.DoesNotContain("appEligibility", JsonSerializer.Serialize(plain, Api), StringComparison.Ordinal);
        Assert.Equal(JsonSerializer.Serialize(plain, Api), JsonSerializer.Serialize(listed.Select(prompt => prompt with { AppEligibility = null }), Api));
        Assert.Null((await closed.Store.GetPromptAsync(rig.Tenant, site, excluded, includeAppEligibility: true))!.AppEligibility);

        // 5.6b: only the two prompt-management reads ask for it. Every other caller of the shared reads — the article studio,
        // content generation, monitoring — gets the plain view of an open tenant too: the same JSON as the closed tenant's.
        var shared = (await rig.Store.ListPromptsAsync(rig.Tenant, site, null, null, null, 100)).Items;
        Assert.All(shared, prompt => Assert.Null(prompt.AppEligibility));
        Assert.Equal(JsonSerializer.Serialize(plain, Api), JsonSerializer.Serialize(shared, Api));
        Assert.Null((await rig.Store.GetPromptAsync(rig.Tenant, site, excluded))!.AppEligibility);

        // Revoking keeps the row and records who revoked it; the prompt is eligible again and can be excluded anew.
        var revoked = await rig.App.RevokePromptExclusionAsync(rig.Tenant, site, excluded, operatorB);
        Assert.Equal((added.Id, added.Reason, operatorA, added.CreatedAt, (Guid?)operatorB), (revoked.Id, revoked.Reason, revoked.CreatedBy, revoked.CreatedAt, revoked.RevokedBy));
        Assert.NotNull(revoked.RevokedAt);
        Assert.Equal("2|1", await ExclusionRowsAsync());
        Assert.Equal(new AppPromptEligibility(true), (await rig.Store.GetPromptAsync(rig.Tenant, site, excluded, includeAppEligibility: true))!.AppEligibility);
        // The other prompt's exclusion is revoked too: its own text still fails the rule.
        await rig.App.RevokePromptExclusionAsync(rig.Tenant, site, excludedAndBad, operatorB);
        Assert.Equal(new AppPromptEligibility(false, "MULTILINE"), (await rig.Store.GetPromptAsync(rig.Tenant, site, excludedAndBad, includeAppEligibility: true))!.AppEligibility);
        Assert.Equal(("APP_PROMPT_EXCLUSION_NOT_FOUND", 404), Code(await Assert.ThrowsAsync<CollectorStoreException>(()
            => rig.App.RevokePromptExclusionAsync(rig.Tenant, site, excluded, operatorB))));
        var again = await rig.App.ExcludePromptAsync(rig.Tenant, site, excluded, "第二次排除", operatorB);
        Assert.NotEqual(added.Id, again.Id);
        // Three rows: the history is kept, one exclusion is active.
        Assert.Equal("3|1", await ExclusionRowsAsync());
        Assert.Equal(new AppPromptEligibility(false, "EXCLUDED_BY_OPERATOR"), (await rig.Store.GetPromptAsync(rig.Tenant, site, excluded, includeAppEligibility: true))!.AppEligibility);
        // Nothing was collected, planned or charged by any of this.
        Assert.Equal(Footprint(0, 0), await FootprintAsync(cs, rig.Tenant));
    });

    private sealed class DevelopmentEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    // The real run controller over the real stores; only the stores a read never uses are fakes that fail when called.
    private static VisibilityRunsController RunsController(Rig rig, Guid tenant)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Tenant-Id"] = tenant.ToString();
        return new VisibilityRunsController(new DomesticCollectorStore(rig.Database), rig.Runs, StudioFakes.Unused<ICloroSettingsStore>(),
            StudioFakes.Unused<ICloroKeyPool>(), StudioFakes.Unused<IAnswerProviderSettingsStore>(), StudioFakes.Unused<IManagementStore>(),
            StudioFakes.Unused<IVisibilityRunQueryStore>(), new TenantContextResolver(new DevelopmentEnvironment()), rig.App,
            new DevelopmentEnvironment(), StudioFakes.Unused<IResearchStore>())
            { ControllerContext = new ControllerContext { HttpContext = context } };
    }

    private static async Task<CursorPage<CloroVisibilityCheckView>> ChecksAsync(VisibilityRunsController controller, Guid run, string? cursor = null,
        int limit = 50, string? provider = null, string? status = null, string? engine = null)
        => Assert.IsType<CursorPage<CloroVisibilityCheckView>>(
            Assert.IsType<OkObjectResult>(await controller.Checks(run, cursor, limit, provider, status, engine)).Value);

    private static async Task<(int Status, string Code)> ChecksErrorAsync(VisibilityRunsController controller, Guid run, string? cursor = null,
        int limit = 50, string? engine = null, string? status = null)
    {
        var result = Assert.IsType<ObjectResult>(await controller.Checks(run, cursor, limit, null, status, engine));
        return (result.StatusCode!.Value, JsonSerializer.SerializeToNode(result.Value, Web)!["error"]!["code"]!.GetValue<string>());
    }

    // 4.6a: a brand that only has the App plan counts as started on the team monitoring page (open tenants only).
    // 4.4a: its cycle run is readable through GET /visibility-runs/{id} and /checks with the API check view.
    [AppChannelMySqlFact]
    public Task AnAppOnlyBrandCountsAsStartedAndItsRunIsReadableThroughTheRunEndpoints() => AppSiteTargetsSqlTests.IsolatedAsync(async (cs, seedTenant) =>
    {
        var rig = Build(cs, await seedTenant());
        var closed = Build(cs, rig.Tenant, open: false);
        var site = await SeedAppBrandAsync(rig, "只有 App", "doubao", "kimi");
        var idle = await SeedBrandAsync(cs, rig.Tenant, "未开启", follows: false, locale: "zh-CN", created: "2026-06-22");
        var first = await SeedPromptAsync(cs, site, "哪个记账工具好用？", 1);
        var second = await SeedPromptAsync(cs, site, "预算管理应用怎么选？", 2);
        await ScopeAsync(cs, rig.Tenant, site, second, AppKey("kimi"));

        async Task<Dictionary<Guid, bool>> StartedAsync(Rig view)
            => (await view.Store.GetTeamMonitoringConfigAsync(rig.Tenant)).Brands.ToDictionary(brand => brand.SiteId, brand => brand.MonitoringStarted);
        Assert.Equal((false, false), ((await StartedAsync(rig))[site], (await StartedAsync(rig))[idle]));
        var plan = await StartAsync(rig, site);
        // Only the App plan exists (no main plan, no API run): started for the open tenant, unchanged for a closed one.
        Assert.Equal(1, await CountAsync(cs, "SELECT COUNT(*) FROM collection_schedules WHERE site_id = UUID_TO_BIN(@site);", ("@site", site.ToString())));
        Assert.Equal((true, false), ((await StartedAsync(rig))[site], (await StartedAsync(rig))[idle]));
        Assert.Equal((false, false), ((await StartedAsync(closed))[site], (await StartedAsync(closed))[idle]));
        // Paused through the plan's own switch (there is no main plan to follow): not started; resumed: started again.
        var version = (int)await CountAsync(cs, "SELECT version FROM collection_schedules WHERE id = UUID_TO_BIN(@id);", ("@id", plan.ToString()));
        var paused = await rig.Schedules.UpdateAsync(new UpdateCollectionScheduleCommand(rig.Tenant, site, plan, version, Enabled: false));
        Assert.False((await StartedAsync(rig))[site]);
        await rig.Schedules.UpdateAsync(new UpdateCollectionScheduleCommand(rig.Tenant, site, plan, paused!.Version, Enabled: true));
        Assert.True((await StartedAsync(rig))[site]);

        var run = (await RunSlotAsync(rig, plan, 3)).RunId!.Value;
        var controller = RunsController(rig, rig.Tenant);

        // GET /visibility-runs/{id}
        var summary = Assert.IsType<VisibilityRunSummary>(Assert.IsType<OkObjectResult>((await controller.Get(run, default)).Result).Value);
        Assert.Equal((run, site, "queued", 3, 0, 0, "app_collector"),
            (summary.RunId, summary.SiteId, summary.Status, summary.TotalChecks, summary.SucceededChecks, summary.FailedChecks, summary.Provider));

        // GET /visibility-runs/{id}/checks: the API check view with provider app_collector.
        var page = await ChecksAsync(controller, run);
        Assert.Null(page.NextCursor);
        Assert.Equal(new[] { (first, "doubao"), (first, "kimi"), (second, "kimi") }.Order(), page.Items.Select(check => (check.PromptId, check.Engine)).Order());
        Assert.All(page.Items, check =>
        {
            Assert.Equal((run, "app_collector", "app", "observed_surface", "zh-CN", "CN", "pending", false, AnswerMode.Fast, 1, "unknown"),
                (check.RunId, check.Provider, check.CaptureMode, check.EvidenceClass, check.Locale, check.Country, check.Status, check.AnswerPresent,
                    check.AnswerMode!.Value, check.SampleIndex, check.QueryAvailability));
            Assert.True(check.Answer is null && check.ErrorCode is null && check.ProviderTaskState is null && check.ExternalTaskId is null
                && check.RawResponseRef is null && check.CapturedAt is null);
            Assert.True(check.RunTargetId is not null && check.PromptVersionId is not null && check.SubjectConfigVersion is not null);
            Assert.Empty(check.Citations);
            Assert.Empty(check.WebQueries);
            Assert.Empty(check.PromptTagsSnapshot!);
        });
        Assert.Equal("哪个记账工具好用？", page.Items.First(check => check.PromptId == first).PromptSnapshot);
        // Nothing about the collector node, the binding, the device or the account is part of this view.
        var members = JsonSerializer.SerializeToNode(page.Items[0], Api)!.AsObject().Select(member => member.Key).ToArray();
        Assert.Contains("provider", members);
        Assert.DoesNotContain(members, name => new[] { "node", "binding", "account", "device", "collector", "label" }
            .Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)));

        // Paging, and the filters.
        var head = await ChecksAsync(controller, run, limit: 2);
        var tail = await ChecksAsync(controller, run, head.NextCursor, 2);
        Assert.Equal((2, 1, null), (head.Items.Count, tail.Items.Count, tail.NextCursor));
        Assert.Equal(page.Items.Select(check => check.CheckId), head.Items.Concat(tail.Items).Select(check => check.CheckId));
        Assert.Equal(2, (await ChecksAsync(controller, run, engine: " KIMI ")).Items.Count(check => check.Engine == "kimi"));
        Assert.Single((await ChecksAsync(controller, run, engine: "doubao")).Items);
        Assert.Equal(3, (await ChecksAsync(controller, run, provider: "APP_COLLECTOR", status: "pending")).Items.Count);
        Assert.Empty((await ChecksAsync(controller, run, provider: "cloro")).Items);
        Assert.Empty((await ChecksAsync(controller, run, provider: "domestic_collector")).Items);
        Assert.Empty((await ChecksAsync(controller, run, status: "cancelled")).Items);
        Assert.Equal((400, "UNSUPPORTED_ENGINE"), await ChecksErrorAsync(controller, run, engine: "chatgpt"));
        Assert.Equal((400, "INVALID_STATUS"), await ChecksErrorAsync(controller, run, status: "leased"));
        Assert.Equal((400, "INVALID_LIMIT"), await ChecksErrorAsync(controller, run, limit: 0));
        Assert.Equal((400, "INVALID_CURSOR"), await ChecksErrorAsync(controller, run, cursor: "not-a-cursor"));
        // The cursor is bound to its filters.
        Assert.Equal((400, "INVALID_CURSOR"), await ChecksErrorAsync(controller, run, cursor: head.NextCursor, limit: 2, engine: "kimi"));

        // Not collected at the deadline: cancelled checks with their reason, through the same endpoints.
        Assert.Equal(3, await rig.App.ExpireOverdueTasksAsync());
        var cancelled = await ChecksAsync(controller, run, status: "cancelled");
        Assert.Equal(3, cancelled.Items.Count);
        Assert.All(cancelled.Items, check => Assert.Equal(("cancelled", "APP_NOT_COLLECTED_CAPACITY", AppNotCollectedRules.Message(AppNotCollectedCodes.Capacity), false),
            (check.Status, check.ErrorCode, check.ErrorMessage, check.AnswerPresent)));
        summary = Assert.IsType<VisibilityRunSummary>(Assert.IsType<OkObjectResult>((await controller.Get(run, default)).Result).Value);
        Assert.Equal(("cancelled", 3, 0, 0, "app_collector"), (summary.Status, summary.TotalChecks, summary.SucceededChecks, summary.FailedChecks, summary.Provider));
        Assert.NotNull(summary.CompletedAt);

        // Another tenant cannot read the run or its checks.
        var stranger = RunsController(rig, await seedTenant());
        Assert.IsType<NotFoundResult>((await stranger.Get(run, default)).Result);
        Assert.IsType<NotFoundResult>(await stranger.Checks(run, null));
        // The store itself is tenant-scoped as well.
        Assert.Empty((await rig.App.GetChecksAsync(Guid.NewGuid(), run, null, 50, null, null)).Items);
    });
}
