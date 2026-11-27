using System.Text.Json;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using MySqlConnector;

namespace Jogeoai.Tests;

/// <summary>
/// 隔离库（已执行 001–042、库名 jogeoai_prompt_scope_*）上的提问平台范围、批量创建与预估。每个用例使用新的租户与品牌，
/// 不触碰共享 jogeoai 业务库；未配置 JOGEOAI_PROMPT_SCOPE_TEST_CONNECTION 时跳过。
/// </summary>
public sealed class PromptImportSqlTests
{
    private static readonly CloroVisibilityTarget ChatGpt = new("cloro", "chatgpt", "en-US", "US");
    private static readonly CloroVisibilityTarget Gemini = new("cloro", "gemini", "en-US", "US");
    private static readonly CloroVisibilityTarget Copilot = new("cloro", "copilot", "en-US", "US");
    private static readonly string ChatGptKey = CollectionIdentity.TargetKey(ChatGpt);
    private static readonly string GeminiKey = CollectionIdentity.TargetKey(Gemini);
    private static readonly string CopilotKey = CollectionIdentity.TargetKey(Copilot);
    private static readonly string DomesticKey = CollectionIdentity.TargetKey("domestic_collector", "doubao", "consumer_web", "CN", "zh-CN");

    private static string ConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("JOGEOAI_PROMPT_SCOPE_TEST_CONNECTION")!;
        Assert.StartsWith("jogeoai_prompt_scope_", new MySqlConnectionStringBuilder(connectionString).Database, StringComparison.Ordinal);
        return connectionString;
    }

    [PromptScopeMySqlFact]
    public async Task BatchCreatesGroupsOncePerNameWritesScopesSourcesAndReplaysIdempotently()
    {
        var cs = ConnectionString();
        var (tenant, site) = await SeedAsync(cs, plan: "pro");
        var store = new ManagementStore(new MySqlDatabase(cs));
        var existingGroup = await store.CreatePromptGroupAsync(tenant, site, new PromptGroupWrite("跑鞋"));
        var otherGroup = await store.CreatePromptGroupAsync(tenant, site, new PromptGroupWrite("品牌口碑"));
        var rows = new[]
        {
            new PromptImportRow("AI 写作工具哪个好用", "zh-CN", GroupName: "AI 写作工具", Source: "keyword_expand"),
            new PromptImportRow("AI 写作工具推荐", "zh-CN", GroupName: "  ai   写作工具 ", Source: "keyword_expand"),
            new PromptImportRow("2026 年跑鞋怎么选", "zh-CN", GroupName: " 跑鞋 ", Source: "batch_import"),
            new PromptImportRow("Seed 品牌怎么样", "zh-CN", GroupId: otherGroup.GroupId, BrandedStatus: "unbranded", Source: "ai_recommend"),
            new PromptImportRow("没有分组的问题", "zh-CN", Source: "manual"),
            new PromptImportRow("AI 写作工具哪个好用", "zh-CN", GroupName: "另一个分组", Source: "keyword_expand"), // 批内重复
        };
        var request = new PromptBatchCreateRequest(rows, [GeminiKey, ChatGptKey]);
        var key = $"batch-{Guid.NewGuid():N}";
        var result = await store.ImportAsync(tenant, site, request.Rows, key, default, request.TargetKeys);
        Assert.Equal((5, 1), (result.CreatedCount, result.DuplicateCount));

        // One new group for both spellings of "AI 写作工具"; "跑鞋" reused; the duplicate row's group is never created.
        var groups = await store.ListPromptGroupsAsync(tenant, site, null, null, null, 100);
        Assert.Equal(new[] { "AI 写作工具", "品牌口碑", "跑鞋" }.Order(), groups.Items.Select(g => g.Name).Order());
        var created = Assert.Single(groups.Items, g => g.Name == "AI 写作工具");
        Assert.Equal(2, created.PromptCount);
        Assert.Equal(1, Assert.Single(groups.Items, g => g.GroupId == existingGroup.GroupId).PromptCount);

        var prompts = (await store.ListPromptsAsync(tenant, site, null, null, null, 100)).Items
            .Where(p => result.PromptIds.Contains(p.PromptId)).ToDictionary(p => p.Content);
        Assert.Equal(5, prompts.Count);
        Assert.All(prompts.Values, p => Assert.Equal(("custom", 1, 1u), (p.TargetScope, p.Version, p.ConfigVersion)));
        Assert.All(prompts.Values, p => Assert.Equal(new[] { ChatGptKey, GeminiKey }.Order(StringComparer.Ordinal), p.TargetKeys));
        Assert.Equal(created.GroupId, prompts["AI 写作工具推荐"].GroupId);
        Assert.Equal(existingGroup.GroupId, prompts["2026 年跑鞋怎么选"].GroupId);
        Assert.Equal((otherGroup.GroupId, "unbranded", "manual", "ai_recommend"), (prompts["Seed 品牌怎么样"].GroupId!.Value,
            prompts["Seed 品牌怎么样"].BrandedStatus, prompts["Seed 品牌怎么样"].BrandedSource, prompts["Seed 品牌怎么样"].CreationSource!));
        // Automatic classification still applies when brandedStatus is omitted ("Seed" is the brand term).
        Assert.Equal("system", prompts["AI 写作工具推荐"].BrandedSource);
        Assert.Null(prompts["没有分组的问题"].GroupId);
        Assert.Equal("keyword_expand", prompts["AI 写作工具哪个好用"].CreationSource);
        Assert.Equal(10, await CountAsync(cs, "SELECT COUNT(*) FROM prompt_target_scopes WHERE site_id=UUID_TO_BIN(@site);", ("@site", site.ToString())));

        // Replay returns the stored result and writes nothing; a different body under the same key conflicts.
        var replay = await store.ImportAsync(tenant, site, request.Rows, key, default, request.TargetKeys);
        Assert.Equal(result.PromptIds, replay.PromptIds);
        Assert.Equal(1 + 5, (await store.SummarizePromptsAsync(tenant, site)).TotalCount);
        var conflict = await Assert.ThrowsAsync<CollectorStoreException>(() => store.ImportAsync(tenant, site, request.Rows, key, default, [GeminiKey]));
        Assert.Equal(("IMPORT_IDEMPOTENCY_CONFLICT", 409), (conflict.Code, conflict.StatusCode));
    }

    [PromptScopeMySqlFact]
    public async Task AnyFailureRollsBackPromptsAndNewGroups()
    {
        var cs = ConnectionString();
        var (tenant, site) = await SeedAsync(cs, plan: "pro");
        var store = new ManagementStore(new MySqlDatabase(cs));
        // The second row fails inside InsertPrompt (unknown tag) after the new group and the first prompt were written.
        var rows = new[]
        {
            new PromptImportRow("第一条", "zh-CN", GroupName: "新核心词", Source: "keyword_expand"),
            new PromptImportRow("第二条", "zh-CN", [Guid.NewGuid()], GroupName: "新核心词", Source: "keyword_expand"),
        };
        var error = await Assert.ThrowsAsync<CollectorStoreException>(() =>
            store.ImportAsync(tenant, site, rows, $"fail-{Guid.NewGuid():N}", default, [GeminiKey]));
        Assert.Equal(404, error.StatusCode);
        Assert.Equal(0, await CountAsync(cs, "SELECT COUNT(*) FROM prompt_groups WHERE site_id=UUID_TO_BIN(@site);", ("@site", site.ToString())));
        Assert.Equal(1, (await store.SummarizePromptsAsync(tenant, site)).TotalCount); // only the seeded prompt
        Assert.Equal(0, await CountAsync(cs, "SELECT COUNT(*) FROM prompt_target_scopes WHERE site_id=UUID_TO_BIN(@site);", ("@site", site.ToString())));
        Assert.Equal(0, await CountAsync(cs, "SELECT COUNT(*) FROM prompt_imports WHERE site_id=UUID_TO_BIN(@site);", ("@site", site.ToString())));

        // Mutually exclusive group fields fail the whole batch before any write.
        var both = await Assert.ThrowsAsync<CollectorStoreException>(() => store.ImportAsync(tenant, site,
            [new PromptImportRow("q", "zh-CN", GroupId: Guid.NewGuid(), GroupName: "g")], $"both-{Guid.NewGuid():N}"));
        Assert.Equal(("INVALID_GROUP", 400), (both.Code, both.StatusCode));
        var source = await Assert.ThrowsAsync<CollectorStoreException>(() => store.ImportAsync(tenant, site,
            [new PromptImportRow("q", "zh-CN", Source: "crawler")], $"src-{Guid.NewGuid():N}"));
        Assert.Equal(("INVALID_CREATION_SOURCE", 400), (source.Code, source.StatusCode));
        Assert.Equal(1, (await store.SummarizePromptsAsync(tenant, site)).TotalCount);
    }

    [PromptScopeMySqlFact]
    public async Task OldStyleImportKeepsAllScopeNoSourceAndTheLegacyHash()
    {
        var cs = ConnectionString();
        var (tenant, site) = await SeedAsync(cs, plan: "pro");
        var store = new ManagementStore(new MySqlDatabase(cs));
        var rows = new[] { new PromptImportRow("旧客户端导入的问题", "zh-CN") };
        var key = $"legacy-{Guid.NewGuid():N}";
        var result = await store.ImportAsync(tenant, site, rows, key);
        var prompt = (await store.GetPromptAsync(tenant, site, Assert.Single(result.PromptIds)))!;
        Assert.Equal(("all", (IReadOnlyList<string>?)null, (string?)null, (Guid?)null), (prompt.TargetScope, prompt.TargetKeys, prompt.CreationSource, prompt.GroupId));
        Assert.Equal(1, await CountAsync(cs, "SELECT COUNT(*) FROM prompt_imports WHERE tenant_id=UUID_TO_BIN(@tenant) AND idempotency_key=@key AND request_hash=@hash;",
            ("@tenant", tenant.ToString()), ("@key", key), ("@hash", ManagementStore.ImportRequestHash(rows, null))));
    }

    [PromptScopeMySqlFact]
    public async Task InvalidPlatformsAreRejected()
    {
        var cs = ConnectionString();
        var (tenant, site) = await SeedAsync(cs, plan: "pro");
        var store = new ManagementStore(new MySqlDatabase(cs));
        // Disabled (copilot), not monitoring-eligible (domestic collector), unknown and empty scopes.
        foreach (var keys in new IReadOnlyList<string>[] { [CopilotKey], [DomesticKey], [GeminiKey, new string('0', 64)], [] })
        {
            var batch = await Assert.ThrowsAsync<CollectorStoreException>(() =>
                store.ImportAsync(tenant, site, [new PromptImportRow("q", "zh-CN")], $"bad-{Guid.NewGuid():N}", default, keys));
            Assert.Equal(("PROMPT_TARGET_INVALID", 400), (batch.Code, batch.StatusCode));
            var single = await Assert.ThrowsAsync<CollectorStoreException>(() =>
                store.CreatePromptAsync(tenant, site, new PromptWrite("q2", "zh-CN", TargetKeys: keys)));
            Assert.Equal(("PROMPT_TARGET_INVALID", 400), (single.Code, single.StatusCode));
        }
        Assert.Equal(1, (await store.SummarizePromptsAsync(tenant, site)).TotalCount);
    }

    [PromptScopeMySqlFact]
    public async Task SinglePromptScopeChangesBumpConfigVersionWithoutANewVersion()
    {
        var cs = ConnectionString();
        var (tenant, site) = await SeedAsync(cs, plan: "pro");
        var store = new ManagementStore(new MySqlDatabase(cs));
        var prompt = await store.CreatePromptAsync(tenant, site, new PromptWrite("单条带平台", "zh-CN", TargetKeys: [GeminiKey]));
        Assert.Equal(("custom", 1, 1u), (prompt.TargetScope, prompt.Version, prompt.ConfigVersion));
        Assert.Equal([GeminiKey], prompt.TargetKeys);
        Assert.Null(prompt.CreationSource);

        var widened = await store.UpdatePromptAsync(tenant, site, prompt.PromptId,
            new PromptPatch(ExpectedVersion: 1, TargetKeys: [ChatGptKey, GeminiKey]));
        Assert.Equal(("custom", 1, 2u), (widened.TargetScope, widened.Version, widened.ConfigVersion));
        Assert.Equal(new[] { ChatGptKey, GeminiKey }.Order(StringComparer.Ordinal), widened.TargetKeys);

        // Omitted keys keep the scope while other fields change.
        var renamed = await store.UpdatePromptAsync(tenant, site, prompt.PromptId, new PromptPatch(Content: "单条带平台（改）", ExpectedVersion: 2));
        Assert.Equal(("custom", 2, 3u), (renamed.TargetScope, renamed.Version, renamed.ConfigVersion));

        var reset = await store.UpdatePromptAsync(tenant, site, prompt.PromptId, new PromptPatch(ExpectedVersion: 3, TargetKeys: []));
        Assert.Equal(("all", 2, 4u), (reset.TargetScope, reset.Version, reset.ConfigVersion));
        Assert.Null(reset.TargetKeys);
        Assert.Equal(0, await CountAsync(cs, "SELECT COUNT(*) FROM prompt_target_scopes WHERE prompt_id=UUID_TO_BIN(@p);", ("@p", prompt.PromptId.ToString())));

        await store.UpdatePromptAsync(tenant, site, prompt.PromptId, new PromptPatch(ExpectedVersion: 4, TargetKeys: [ChatGptKey]));
        var explicitReset = await store.UpdatePromptAsync(tenant, site, prompt.PromptId, new PromptPatch(ExpectedVersion: 5, ResetTargetScope: true));
        Assert.Equal(("all", 2, 6u), (explicitReset.TargetScope, explicitReset.Version, explicitReset.ConfigVersion));
        Assert.Equal(2, await CountAsync(cs, "SELECT COUNT(*) FROM prompt_versions WHERE prompt_id=UUID_TO_BIN(@p);", ("@p", prompt.PromptId.ToString())));
    }

    [PromptScopeMySqlFact]
    public async Task TargetViewsCarryTheStoredKeyAndMonitoringEligibility()
    {
        var cs = ConnectionString();
        var (tenant, site) = await SeedAsync(cs, plan: "pro");
        var store = new ManagementStore(new MySqlDatabase(cs));
        var targets = (await store.GetSiteTargetsAsync(tenant, site)).Items;
        var stored = await RowsAsync(cs, "SELECT BIN_TO_UUID(id), target_key FROM site_targets WHERE site_id=UUID_TO_BIN(@site);", ("@site", site.ToString()));
        Assert.Equal(4, targets.Count);
        foreach (var target in targets)
            Assert.Equal(Assert.Single(stored, row => Guid.Parse(row[0]) == target.TargetId)[1], target.TargetKey);
        Assert.Equal(new[] { false, true, true, true },
            targets.OrderBy(t => t.Provider == "cloro").ThenBy(t => t.Engine).Select(t => t.MonitoringEligible));
        Assert.False(Assert.Single(targets, t => t.Engine == "copilot").Enabled);
    }

    [PromptScopeMySqlFact]
    public async Task PreviewReportsGroupResolutionPerRow()
    {
        var cs = ConnectionString();
        var (tenant, site) = await SeedAsync(cs, plan: "pro");
        var store = new ManagementStore(new MySqlDatabase(cs));
        var group = await store.CreatePromptGroupAsync(tenant, site, new PromptGroupWrite("已有分组"));
        var preview = await store.PreviewImportAsync(tenant, site,
        [
            new PromptImportRow("a", "zh-CN", GroupName: " 已有分组 "),
            new PromptImportRow("b", "zh-CN", GroupName: "新分组"),
            new PromptImportRow("c", "zh-CN", GroupId: group.GroupId),
            new PromptImportRow("d", "zh-CN"),
            new PromptImportRow("e", "zh-CN", GroupId: group.GroupId, GroupName: "x"),
            new PromptImportRow("f", "zh-CN", GroupName: new string('长', 101)),
            new PromptImportRow("What is visible?", "en-US", GroupName: "新分组"),
        ]);
        Assert.Equal(("existing", group.GroupId), (preview.Rows[0].GroupResolution, preview.Rows[0].GroupId!.Value));
        Assert.Equal(("create", (Guid?)null), (preview.Rows[1].GroupResolution, preview.Rows[1].GroupId));
        Assert.Equal(("existing", group.GroupId), (preview.Rows[2].GroupResolution, preview.Rows[2].GroupId!.Value));
        Assert.Equal("none", preview.Rows[3].GroupResolution);
        Assert.Equal(("INVALID_GROUP", "none"), (preview.Rows[4].ErrorCode, preview.Rows[4].GroupResolution));
        Assert.Equal("INVALID_PROMPT_GROUP", preview.Rows[5].ErrorCode);
        Assert.Equal(("PROMPT_DUPLICATE", "create"), (preview.Rows[6].ErrorCode, preview.Rows[6].GroupResolution));
        Assert.Equal((4, 1), (preview.NewCount, preview.DuplicateCount));
        Assert.Equal(1, await CountAsync(cs, "SELECT COUNT(*) FROM prompt_groups WHERE site_id=UUID_TO_BIN(@site);", ("@site", site.ToString())));
    }

    [PromptScopeMySqlFact]
    public async Task ScaleAndPlanLimitsRejectTheWholeBatch()
    {
        var cs = ConnectionString();
        var (tenant, site) = await SeedAsync(cs, plan: "pro");
        var store = new ManagementStore(new MySqlDatabase(cs));
        // 10 samples per pair via the monitoring plan: 1 existing + 49 new prompts × 2 targets × 10 = 1000 fits, 50 new = 1020.
        await InsertMonitoringPlanAsync(cs, tenant, site, sampleCount: 10);
        IReadOnlyList<PromptImportRow> Rows(int count, string prefix) =>
            Enumerable.Range(0, count).Select(i => new PromptImportRow($"{prefix} {i}", "zh-CN", GroupName: prefix)).ToArray();
        var tooMany = await Assert.ThrowsAsync<CollectorStoreException>(() => store.ImportAsync(tenant, site, Rows(50, "规模"), $"big-{Guid.NewGuid():N}"));
        Assert.Equal(("MONITORING_TOO_LARGE", 400), (tooMany.Code, tooMany.StatusCode));
        Assert.Equal(0, await CountAsync(cs, "SELECT COUNT(*) FROM prompt_groups WHERE site_id=UUID_TO_BIN(@site);", ("@site", site.ToString())));
        // The same 50 prompts on Gemini only: 20 + 500 checks.
        var scoped = await store.ImportAsync(tenant, site, Rows(50, "规模"), $"scoped-{Guid.NewGuid():N}", default, [GeminiKey]);
        Assert.Equal(50, scoped.CreatedCount);

        // 200 enabled prompts per brand (separate brand, 1 sample): 1 + 199 = 200 fits (2 + 199 checks), one more is rejected.
        var (fullTenant, fullSite) = await SeedAsync(cs, plan: "pro");
        var fill = await store.ImportAsync(fullTenant, fullSite, Rows(199, "填满"), $"fill-{Guid.NewGuid():N}", default, [GeminiKey]);
        Assert.Equal(199, fill.CreatedCount);
        var over = await Assert.ThrowsAsync<CollectorStoreException>(() =>
            store.ImportAsync(fullTenant, fullSite, Rows(1, "超出"), $"over-{Guid.NewGuid():N}", default, [GeminiKey]));
        Assert.Equal(("MONITORING_TOO_LARGE", 400), (over.Code, over.StatusCode));
        Assert.Equal(0, await CountAsync(cs, "SELECT COUNT(*) FROM prompt_groups WHERE site_id=UUID_TO_BIN(@site) AND name='超出';",
            ("@site", fullSite.ToString())));

        // Plan limit first (free_trial: 20 prompts per tenant): 1 seeded + 20 new exceeds it with the upgrade details.
        var (limitedTenant, limitedSite) = await SeedAsync(cs, plan: null);
        var limit = await Assert.ThrowsAsync<PlanLimitExceededException>(() =>
            store.ImportAsync(limitedTenant, limitedSite, Rows(20, "额度"), $"plan-{Guid.NewGuid():N}", default, [GeminiKey]));
        Assert.Equal((PlanLimitKind.Prompts, 20, 1), (limit.Limit, limit.LimitValue, limit.CurrentValue));
        Assert.Equal(0, await CountAsync(cs, "SELECT COUNT(*) FROM prompt_groups WHERE site_id=UUID_TO_BIN(@site);", ("@site", limitedSite.ToString())));
    }

    [PromptScopeMySqlFact]
    public async Task EstimateMatchesTheDailyPlanMatrixAndReportsShortfallAndBlockers()
    {
        var cs = ConnectionString();
        var (tenant, site) = await SeedAsync(cs, plan: null, credits: 50m);
        var database = new MySqlDatabase(cs);
        var store = new ManagementStore(database);
        await store.CreatePromptAsync(tenant, site, new PromptWrite("只在 Gemini", "zh-CN", TargetKeys: [GeminiKey]));
        var service = new PromptMonitoringService(database, store, null!, null!);

        // Current: 1 'all' prompt × 2 targets + 1 Gemini-only prompt = 3 checks (no plan yet → 1 sample).
        var estimate = await service.EstimateAsync(tenant, site, new PromptMonitoringEstimateRequest(4, [ChatGptKey]));
        Assert.Equal((2, 1, 3L, 30m), (estimate.CurrentPromptCount, estimate.SampleCount, estimate.CurrentChecksPerRound, estimate.CurrentCreditsPerRound));
        Assert.Equal((4L, 40m, 7L, 70m), (estimate.AddedChecksPerRound, estimate.AddedCreditsPerRound, estimate.TotalChecksPerRound, estimate.TotalCreditsPerRound));
        Assert.Equal((50m, 20m), (estimate.AvailableCredits, estimate.Shortfall));
        Assert.Empty(estimate.Blockers);

        // Same matrix the monitoring plan reconcile reads from MySQL for the current prompts.
        await using (var connection = new MySqlConnection(cs))
        {
            await connection.OpenAsync();
            await using var tx = await connection.BeginTransactionAsync();
            var snapshot = await PromptMonitoringScale.ReadAsync(connection, tx, tenant, site, default);
            Assert.Equal(estimate.CurrentChecksPerRound, MonitoringMatrix.CountChecks(
                MonitoringMatrix.BuildSelection(snapshot.Prompts, snapshot.Targets, snapshot.SampleCount)));
        }

        // free_trial allows 20 prompts per tenant: 2 + 19 exceeds it.
        Assert.Equal(["PLAN_LIMIT_EXCEEDED"], (await service.EstimateAsync(tenant, site, new PromptMonitoringEstimateRequest(19))).Blockers);
        Assert.Equal(["MONITORING_TARGETS_REQUIRED"], (await service.EstimateAsync(tenant, site, new PromptMonitoringEstimateRequest(1, []))).Blockers);
        var invalid = await Assert.ThrowsAsync<CollectorStoreException>(() =>
            service.EstimateAsync(tenant, site, new PromptMonitoringEstimateRequest(1, [CopilotKey])));
        Assert.Equal(("PROMPT_TARGET_INVALID", 400), (invalid.Code, invalid.StatusCode));
    }

    // Tenant + active brand ("Seed" brand term) + one enabled 'all' prompt; targets: cloro chatgpt/gemini enabled,
    // cloro copilot disabled, domestic doubao enabled (not monitoring-eligible). plan=null keeps the free_trial fallback.
    private static async Task<(Guid Tenant, Guid Site)> SeedAsync(string cs, string? plan, decimal credits = 1000m)
    {
        var tenant = Guid.NewGuid();
        var site = Guid.NewGuid();
        await using var connection = new MySqlConnection(cs);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        async Task Exec(string sql, params (string, object?)[] args)
        {
            await using var command = new MySqlCommand(sql, connection, tx);
            command.Parameters.AddWithValue("@tenant", tenant.ToString());
            command.Parameters.AddWithValue("@site", site.ToString());
            foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            await command.ExecuteNonQueryAsync();
        }
        await Exec("INSERT INTO tenants (id,name,status,created_at,updated_at) VALUES (UUID_TO_BIN(@tenant),'prompt-scope-test','active',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));");
        await Exec("INSERT INTO sites (id,tenant_id,host,display_name,status,created_at,updated_at) VALUES (UUID_TO_BIN(@site),UUID_TO_BIN(@tenant),@host,'Seed','active',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));",
            ("@host", $"scope-{site:N}.test"));
        await Exec("INSERT INTO site_brand_settings (site_id,tenant_id,created_at,updated_at) VALUES (UUID_TO_BIN(@site),UUID_TO_BIN(@tenant),UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));");
        await Exec("""
            INSERT INTO subject_terms (id,tenant_id,site_id,subject_type,subject_id,namespace_key,term_type,display_value,normalized_key,created_at)
            VALUES (UUID_TO_BIN(@id),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),'brand',UUID_TO_BIN(@site),'text','name','Seed','seed',UTC_TIMESTAMP(6));
            """, ("@id", Guid.NewGuid().ToString()));
        foreach (var (provider, engine, capture, country, locale, enabled) in new[]
        {
            ("cloro", "chatgpt", "scraped", "US", "en-US", true), ("cloro", "gemini", "scraped", "US", "en-US", true),
            ("cloro", "copilot", "scraped", "US", "en-US", false), ("domestic_collector", "doubao", "consumer_web", "CN", "zh-CN", true),
        })
            await Exec("""
                INSERT INTO site_targets (id,tenant_id,site_id,provider,engine,capture_mode,country,locale,target_key,enabled,created_at,updated_at)
                VALUES (UUID_TO_BIN(@id),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),@provider,@engine,@capture,@country,@locale,@key,@enabled,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
                """, ("@id", Guid.NewGuid().ToString()), ("@provider", provider), ("@engine", engine), ("@capture", capture),
                ("@country", country), ("@locale", locale), ("@key", CollectionIdentity.TargetKey(provider, engine, capture, country, locale)),
                ("@enabled", enabled));
        var prompt = Guid.NewGuid();
        var version = Guid.NewGuid();
        await Exec("INSERT INTO prompts (id,site_id,content,locale,enabled,created_at,updated_at) VALUES (UUID_TO_BIN(@prompt),UUID_TO_BIN(@site),'What is visible?','en-US',TRUE,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));",
            ("@prompt", prompt.ToString()));
        await Exec("INSERT INTO prompt_versions (id,site_id,prompt_id,version,content,locale,branded_status,branded_source,created_at) VALUES (UUID_TO_BIN(@version),UUID_TO_BIN(@site),UUID_TO_BIN(@prompt),1,'What is visible?','en-US','unbranded','system',UTC_TIMESTAMP(6));",
            ("@version", version.ToString()), ("@prompt", prompt.ToString()));
        await Exec("UPDATE prompts SET current_version_id=UUID_TO_BIN(@version) WHERE id=UUID_TO_BIN(@prompt);",
            ("@version", version.ToString()), ("@prompt", prompt.ToString()));
        await Exec("INSERT INTO credit_accounts (tenant_id,available_credits,lifetime_granted,updated_at) VALUES (UUID_TO_BIN(@tenant),@credits,@credits,UTC_TIMESTAMP(6));",
            ("@credits", credits));
        if (plan is not null)
            await Exec("""
                INSERT INTO tenant_subscriptions (id,tenant_id,plan_code,source,started_at,expires_at,created_at,updated_at)
                VALUES (UUID_TO_BIN(@id),UUID_TO_BIN(@tenant),@plan,'admin',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6)+INTERVAL 1 MONTH,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
                """, ("@id", Guid.NewGuid().ToString()), ("@plan", plan));
        await tx.CommitAsync();
        return (tenant, site);
    }

    // A disabled monitoring plan row: only its frozen sample count matters here; no slot can become due.
    private static async Task InsertMonitoringPlanAsync(string cs, Guid tenant, Guid site, int sampleCount)
    {
        var prompt = Guid.Parse((await RowsAsync(cs, "SELECT BIN_TO_UUID(id) FROM prompts WHERE site_id=UUID_TO_BIN(@site) LIMIT 1;",
            ("@site", site.ToString())))[0][0]);
        var schedule = await new CollectionScheduleStore(new MySqlDatabase(cs)).CreateAutomaticPlanAsync(new CreateCollectionScheduleCommand(tenant, site,
            PromptMonitoringIdentity.ScheduleName, "daily", null, "09:00", "UTC",
            new CollectionScheduleSelection([prompt], [ChatGpt], sampleCount), Enabled: false));
        Assert.Equal(sampleCount, schedule.Selection.SampleCount);
    }

    private static async Task<int> CountAsync(string cs, string sql, params (string, object?)[] args)
        => int.Parse((await RowsAsync(cs, sql, args))[0][0]);

    private static async Task<List<string[]>> RowsAsync(string cs, string sql, params (string, object?)[] args)
    {
        await using var connection = new MySqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string[]>();
        while (await reader.ReadAsync())
        {
            var row = new string[reader.FieldCount];
            for (var i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? "" : Convert.ToString(reader.GetValue(i))!;
            rows.Add(row);
        }
        return rows;
    }
}
