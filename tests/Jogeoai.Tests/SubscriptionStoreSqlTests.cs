using System.Globalization;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace Jogeoai.Tests;

/// <summary>
/// 隔离库 SQL 场景：需要 <c>JOGEOAI_SUBSCRIPTION_TEST_CONNECTION</c> 指向已执行 001–032 的
/// <c>jogeoai_subscription_20261019</c>；缺失即跳过。仅 031 重放用例在回滚事务内执行 DML，不连接业务库。
/// </summary>
public sealed class SubscriptionStoreSqlTests
{
    private const string TestDatabase = "jogeoai_subscription_20261019";
    private const string Password = "Subscription-test-password-2026!";

    private static string ConnectionString
    {
        get
        {
            var value = Environment.GetEnvironmentVariable("JOGEOAI_SUBSCRIPTION_TEST_CONNECTION")!;
            Assert.Equal(TestDatabase, new MySqlConnectionStringBuilder(value).Database);
            return value;
        }
    }

    private static MySqlDatabase Database() => new(ConnectionString);
    private static SubscriptionStore Subscriptions(int failuresPerHour = 10) =>
        new(Database(), Options.Create(new SubscriptionOptions { RedeemFailuresPerHour = failuresPerHour }));

    private static async Task<(Guid TenantId, Guid UserId)> RegisterTenantAsync()
    {
        var sender = new RecordingSender();
        var identity = new IdentityStore(Database(), sender);
        var email = $"subscription-{Guid.NewGuid():N}@example.test";
        await identity.SendRegistrationCodeAsync(new(email));
        var session = await identity.RegisterAsync(new(email, Password, sender.Code!, "套餐测试"), new());
        var tenantId = session.User.Memberships[0].TenantId;
        await new OnboardingProgressStore(Database()).ConfigureTeamAsync(tenantId, session.User.UserId, "套餐团队");
        return (tenantId, session.User.UserId);
    }

    [SubscriptionDatabaseFact]
    public async Task RegistrationCreatesFreeTrialSubscriptionWithSignupCreditsAndNoPeriod()
    {
        var (tenantId, userId) = await RegisterTenantAsync();
        var view = await Subscriptions().GetAsync(tenantId);
        Assert.Equal("free_trial", view.Plan.Code);
        Assert.True(view.Plan.IsCurrent);
        Assert.Equal(SubscriptionSource.Registration, view.Source);
        Assert.Null(view.ExpiresAt);
        Assert.Null(view.Period);
        Assert.Equal(500m, view.Usage.AvailableCredits);
        Assert.Equal(500m, view.Usage.SignupCredits);
        Assert.Equal(new UsageItem(1, 1), view.Usage.Members);
        Assert.Equal(new UsageItem(0, 1), view.Usage.Brands);
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM credit_ledger WHERE tenant_id = UUID_TO_BIN(@tenant) AND entry_type = 'grant' AND external_reference = @ref",
            ("@tenant", tenantId.ToString()), ("@ref", $"registration-{userId:N}")));
        Assert.Equal("registration", await Scalar("SELECT event_type FROM subscription_events WHERE tenant_id = UUID_TO_BIN(@tenant)", ("@tenant", tenantId.ToString())));
        var plans = await Subscriptions().ListPlansAsync(tenantId);
        Assert.Equal(["free_trial", "starter", "basic", "standard", "pro"], plans.Select(plan => plan.Code).ToArray());
        Assert.Equal("free_trial", Assert.Single(plans, plan => plan.IsCurrent).Code);
        Assert.Equal("basic", Assert.Single(plans, plan => plan.Recommended).Code);
    }

    [SubscriptionDatabaseFact]
    public async Task Migration031UpgradesExistingProAndReplaysWithoutChangingCatalogOrTenantState()
    {
        var (tenantId, userId) = await RegisterTenantAsync();
        await Subscriptions().AssignAsync(tenantId, userId, new("pro", 1, null, "迁移回归"));
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        async Task<object?> Execute(string sql)
        {
            await using var command = new MySqlCommand(sql, connection, transaction);
            return await command.ExecuteScalarAsync();
        }
        async Task<string> Snapshot(string sql, params string[] omittedColumns)
        {
            await using var command = new MySqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("@tenant", tenantId.ToString());
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<Dictionary<string, object?>>();
            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object?>();
                for (var i = 0; i < reader.FieldCount; i++)
                    if (!omittedColumns.Contains(reader.GetName(i)))
                        row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(row);
            }
            return System.Text.Json.JsonSerializer.Serialize(rows);
        }

        await Execute("UPDATE subscription_plans SET max_brands = 5, max_members = 20 WHERE code = 'pro';");
        var queries = new[]
        {
            "SELECT * FROM subscription_plans WHERE code <> 'pro' ORDER BY code;",
            "SELECT * FROM tenant_subscriptions WHERE tenant_id = UUID_TO_BIN(@tenant);",
            "SELECT * FROM credit_accounts WHERE tenant_id = UUID_TO_BIN(@tenant);",
            "SELECT * FROM credit_ledger WHERE tenant_id = UUID_TO_BIN(@tenant) ORDER BY id;"
        };
        var before = new List<string>();
        foreach (var query in queries) before.Add(await Snapshot(query));
        var proBefore = await Snapshot("SELECT * FROM subscription_plans WHERE code = 'pro';", "max_brands", "max_members");
        var migration = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../db/migrations/031_pro_plan_brand_member_limits.sql")));

        await Execute(migration);
        Assert.Equal(20L, Convert.ToInt64(await Execute("SELECT max_brands FROM subscription_plans WHERE code = 'pro';"), CultureInfo.InvariantCulture));
        Assert.Equal(20L, Convert.ToInt64(await Execute("SELECT max_members FROM subscription_plans WHERE code = 'pro';"), CultureInfo.InvariantCulture));
        var first = await Snapshot("SELECT * FROM subscription_plans WHERE code = 'pro';");
        await Execute(migration);
        Assert.Equal(first, await Snapshot("SELECT * FROM subscription_plans WHERE code = 'pro';"));
        Assert.Equal(proBefore, await Snapshot("SELECT * FROM subscription_plans WHERE code = 'pro';", "max_brands", "max_members"));
        for (var i = 0; i < queries.Length; i++) Assert.Equal(before[i], await Snapshot(queries[i]));
        await transaction.RollbackAsync();
    }

    [SubscriptionDatabaseFact]
    public async Task ProLimitsAllowTwentiethBrandSerializeTheLastSlotAndGuardReactivation()
    {
        var (tenantId, userId) = await RegisterTenantAsync();
        var subscriptions = Subscriptions();
        var assigned = await subscriptions.AssignAsync(tenantId, userId, new("pro", 1, null, "专业版额度回归"));
        Assert.Equal((20, 20), (assigned.Plan.Limits.MaxBrands, assigned.Plan.Limits.MaxMembers));
        var catalog = Assert.Single(await subscriptions.ListPlansAsync(tenantId), plan => plan.Code == "pro");
        Assert.Equal(assigned.Plan.Limits, catalog.Limits);
        var management = new ManagementStore(Database());
        BrandWrite Brand(string key, string status = "active") =>
            new($"专业版品牌 {key}", $"pro-{key}.example.test", Status: status, Description: "专业版额度边界测试品牌。");
        for (var i = 1; i <= 19; i++) await management.CreateBrandAsync(tenantId, Brand(i.ToString(CultureInfo.InvariantCulture)));
        Assert.Equal(new UsageItem(19, 20), (await subscriptions.GetAsync(tenantId)).Usage.Brands);
        async Task<BrandView?> TryCreate(string key)
        {
            try { return await management.CreateBrandAsync(tenantId, Brand(key)); }
            catch (PlanLimitExceededException error)
            {
                Assert.Equal(("pro", PlanLimitKind.Brands, 20, 20), (error.PlanCode, error.Limit, error.LimitValue, error.CurrentValue));
                return null;
            }
        }
        var results = await Task.WhenAll(TryCreate("20-a"), TryCreate("20-b"));
        var twentieth = Assert.Single(results, item => item is not null)!;
        Assert.Equal(new UsageItem(20, 20), (await subscriptions.GetAsync(tenantId)).Usage.Brands);
        var limit = await Assert.ThrowsAsync<PlanLimitExceededException>(() => management.CreateBrandAsync(tenantId, Brand("21")));
        Assert.Equal((20, 20), (limit.LimitValue, limit.CurrentValue));

        var archived = await management.UpdateBrandAsync(tenantId, twentieth.SiteId, new(Status: "archived", ExpectedVersion: twentieth.SubjectConfigVersion));
        Assert.Equal(new UsageItem(19, 20), (await subscriptions.GetAsync(tenantId)).Usage.Brands);
        var restored = await management.UpdateBrandAsync(tenantId, archived.SiteId, new(Status: "active", ExpectedVersion: archived.SubjectConfigVersion));
        Assert.Equal("active", restored.Status);
        var extraArchived = await management.CreateBrandAsync(tenantId, Brand("archived", "archived"));
        Assert.Equal(new UsageItem(20, 20), (await subscriptions.GetAsync(tenantId)).Usage.Brands);
        var reactivation = await Assert.ThrowsAsync<PlanLimitExceededException>(() => management.UpdateBrandAsync(tenantId, extraArchived.SiteId,
            new(Status: "active", ExpectedVersion: extraArchived.SubjectConfigVersion)));
        Assert.Equal((PlanLimitKind.Brands, 20, 20), (reactivation.Limit, reactivation.LimitValue, reactivation.CurrentValue));
    }

    [SubscriptionDatabaseFact]
    public async Task ProMemberGuardCountsOwnerActiveAndInvitedButExcludesSuspendedAtTwenty()
    {
        var (tenantId, userId) = await RegisterTenantAsync();
        await Subscriptions().AssignAsync(tenantId, userId, new("pro", 1, null, "成员额度回归"));
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var tenantLock = new MySqlCommand("SELECT id FROM tenants WHERE id = UUID_TO_BIN(@tenant) FOR UPDATE;", connection, transaction))
        {
            tenantLock.Parameters.AddWithValue("@tenant", tenantId.ToString());
            await tenantLock.ExecuteScalarAsync();
        }
        async Task AddMember(string status)
        {
            var id = Guid.NewGuid();
            await using var command = new MySqlCommand("""
                INSERT INTO auth_users (id, email, password_hash, status, created_at, updated_at)
                VALUES (UUID_TO_BIN(@user), @email, 'not-a-login-password', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                INSERT INTO tenant_members (tenant_id, user_id, role, status, created_at, updated_at)
                VALUES (UUID_TO_BIN(@tenant), UUID_TO_BIN(@user), 'member', @status, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                """, connection, transaction);
            command.Parameters.AddWithValue("@user", id.ToString());
            command.Parameters.AddWithValue("@email", $"member-{id:N}@example.test");
            command.Parameters.AddWithValue("@tenant", tenantId.ToString());
            command.Parameters.AddWithValue("@status", status);
            await command.ExecuteNonQueryAsync();
        }
        for (var i = 0; i < 9; i++) await AddMember("active");
        for (var i = 0; i < 9; i++) await AddMember("invited");
        await AddMember("suspended");
        // 所有者 1 + active 9 + invited 9 = 19；suspended 不计数。
        await SubscriptionLimitGuard.EnsureMembersAsync(connection, transaction, tenantId, 1, default);
        await AddMember("invited");
        var limit = await Assert.ThrowsAsync<PlanLimitExceededException>(() => SubscriptionLimitGuard.EnsureMembersAsync(connection, transaction, tenantId, 1, default));
        Assert.Equal(("pro", PlanLimitKind.Members, 20, 20), (limit.PlanCode, limit.Limit, limit.LimitValue, limit.CurrentValue));
        await transaction.RollbackAsync();
    }

    [SubscriptionDatabaseFact]
    public async Task FreeTrialLimitsBlockEveryCreationPathIncludingConcurrentBrandCreation()
    {
        var (tenantId, _) = await RegisterTenantAsync();
        var management = new ManagementStore(Database());

        // 并发创建第 1 个品牌：租户锁 + 守卫保证只成功 1 个。
        async Task<BrandView?> TryCreate(string host)
        {
            try { return await management.CreateBrandAsync(tenantId, new("品牌 " + host, host, Description: "订阅额度测试品牌。")); }
            catch (PlanLimitExceededException) { return null; }
        }
        var attempts = await Task.WhenAll(TryCreate("one.example.test"), TryCreate("two.example.test"));
        var brand = Assert.Single(attempts, item => item is not null)!;
        var second = await Assert.ThrowsAsync<PlanLimitExceededException>(() => management.CreateBrandAsync(tenantId, new("第二品牌", "three.example.test", Description: "订阅额度测试品牌。")));
        Assert.Equal((PlanLimitKind.Brands, 1, 1, "free_trial"), (second.Limit, second.LimitValue, second.CurrentValue, second.PlanCode));

        // 归档后可再建；重新启用被归档品牌时也受上限约束。
        var archived = await management.UpdateBrandAsync(tenantId, brand.SiteId, new(Status: "archived", ExpectedVersion: brand.SubjectConfigVersion));
        var replacement = await management.CreateBrandAsync(tenantId, new("替代品牌", "four.example.test", Description: "订阅额度测试品牌。"));
        Assert.Equal(PlanLimitKind.Brands, (await Assert.ThrowsAsync<PlanLimitExceededException>(() =>
            management.UpdateBrandAsync(tenantId, brand.SiteId, new(Status: "active", ExpectedVersion: archived.SubjectConfigVersion)))).Limit);

        // 竞品：每品牌 2 个；第 3 个（创建 / 取消归档）均 409。
        var siteId = replacement.SiteId;
        var competitorA = await management.CreateCompetitorAsync(tenantId, siteId, new("竞品 A"));
        await management.CreateCompetitorAsync(tenantId, siteId, new("竞品 B"));
        Assert.Equal(PlanLimitKind.Competitors, (await Assert.ThrowsAsync<PlanLimitExceededException>(() =>
            management.CreateCompetitorAsync(tenantId, siteId, new("竞品 C")))).Limit);
        var site = (await management.GetBrandAsync(tenantId, siteId))!;
        await management.UpdateCompetitorAsync(tenantId, siteId, competitorA.CompetitorId, new(Status: "archived", ExpectedVersion: site.SubjectConfigVersion));
        site = (await management.GetBrandAsync(tenantId, siteId))!;
        await management.CreateCompetitorAsync(tenantId, siteId, new("竞品 C"));
        site = (await management.GetBrandAsync(tenantId, siteId))!;
        Assert.Equal(PlanLimitKind.Competitors, (await Assert.ThrowsAsync<PlanLimitExceededException>(() =>
            management.UpdateCompetitorAsync(tenantId, siteId, competitorA.CompetitorId, new(Status: "active", ExpectedVersion: site.SubjectConfigVersion)))).Limit);

        // 提问：租户内 20 条；导入 19 + 单条 1 = 20，第 21 条（单条 / 导入 / 启用）均 409。
        var rows = Enumerable.Range(1, 19).Select(i => new PromptImportRow($"导入问题 {i}", "zh-CN")).ToArray();
        var imported = await management.ImportAsync(tenantId, siteId, rows, "import-key-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(19, imported.CreatedCount);
        var twentieth = await management.CreatePromptAsync(tenantId, siteId, new("第二十个问题", "zh-CN"));
        var limit = await Assert.ThrowsAsync<PlanLimitExceededException>(() => management.CreatePromptAsync(tenantId, siteId, new("第二十一个问题", "zh-CN")));
        Assert.Equal((PlanLimitKind.Prompts, 20, 20), (limit.Limit, limit.LimitValue, limit.CurrentValue));
        Assert.Equal(PlanLimitKind.Prompts, (await Assert.ThrowsAsync<PlanLimitExceededException>(() =>
            management.ImportAsync(tenantId, siteId, [new PromptImportRow("导入第二十一", "zh-CN")], "import-key-" + Guid.NewGuid().ToString("N")))).Limit);
        var disabled = await management.CreatePromptAsync(tenantId, siteId, new("停用问题", "zh-CN", Enabled: false));
        Assert.Equal(PlanLimitKind.Prompts, (await Assert.ThrowsAsync<PlanLimitExceededException>(() =>
            management.UpdatePromptAsync(tenantId, siteId, disabled.PromptId, new(Enabled: true, ExpectedVersion: (int)disabled.ConfigVersion)))).Limit);
        _ = twentieth;

        // 成员：Store 级守卫在免费体验版下拒绝第 2 个成员（所有者已占 1）。
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var tenantLock = new MySqlCommand("SELECT id FROM tenants WHERE id = UUID_TO_BIN(@tenant) FOR UPDATE;", connection, transaction))
        {
            tenantLock.Parameters.AddWithValue("@tenant", tenantId.ToString());
            await tenantLock.ExecuteScalarAsync();
        }
        var members = await Assert.ThrowsAsync<PlanLimitExceededException>(() => SubscriptionLimitGuard.EnsureMembersAsync(connection, transaction, tenantId, 1, default));
        Assert.Equal((PlanLimitKind.Members, 1, 1), (members.Limit, members.LimitValue, members.CurrentValue));
        await transaction.RollbackAsync();
    }

    [SubscriptionDatabaseFact]
    public async Task OnboardingCompletionIsCheckedAgainstPromptAndCompetitorLimitsBeforeWriting()
    {
        var (tenantId, _) = await RegisterTenantAsync();
        var management = new ManagementStore(Database());
        var brand = await management.CreateBrandAsync(tenantId, new("引导品牌", "onboarding.example.test", Description: "引导额度测试品牌。"));
        var prompts = Enumerable.Range(1, 3).Select(i => new PromptWrite($"引导问题 {i}", "zh-CN")).ToArray();
        var competitors = Enumerable.Range(1, 3).Select(i => new CompetitorWrite($"引导竞品 {i}")).ToArray();
        var limit = await Assert.ThrowsAsync<PlanLimitExceededException>(() => management.CompleteOnboardingAsync(tenantId, brand.SiteId, new(competitors, prompts)));
        Assert.Equal((PlanLimitKind.Competitors, 2, 0), (limit.Limit, limit.LimitValue, limit.CurrentValue));
        Assert.Empty((await management.ListCompetitorsAsync(tenantId, brand.SiteId, null, 10)).Items);
        var result = await management.CompleteOnboardingAsync(tenantId, brand.SiteId, new(competitors.Take(2).ToArray(), prompts));
        Assert.Equal((2, 3), (result.CompetitorsCreated, result.PromptsCreated));
        Assert.Equal(new UsageItem(3, 20), (await Subscriptions().GetAsync(tenantId)).Usage.Prompts);
    }

    [SubscriptionDatabaseFact]
    public async Task AdminAssignmentGrantsPeriodQuotaAndRolloverExpiresThenGrantsIdempotently()
    {
        var (tenantId, userId) = await RegisterTenantAsync();
        var store = Subscriptions();
        var assigned = await store.AssignAsync(tenantId, userId, new("basic", 1, null, "验收指派"));
        Assert.Equal("basic", assigned.Plan.Code);
        Assert.Equal(SubscriptionSource.Admin, assigned.Source);
        Assert.NotNull(assigned.ExpiresAt);
        Assert.Equal(1, assigned.Period!.Index);
        Assert.Equal(42000m, assigned.Period.Quota);
        Assert.Equal(0m, assigned.Period.Used);
        Assert.Equal(42500m, assigned.Usage.AvailableCredits); // 升级不清零注册赠送的 500（FR-10）。
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM credit_ledger WHERE tenant_id = UUID_TO_BIN(@tenant) AND entry_type = 'grant' AND amount = 42000 AND external_reference LIKE 'plan-activate:%'",
            ("@tenant", tenantId.ToString())));

        // 模拟预占 100，再把周期结束时间调到过去并运行滚动：可用清零（expire）后发放 42,000（grant），预占不变。
        await Sql("UPDATE credit_accounts SET available_credits = available_credits - 100, reserved_credits = reserved_credits + 100 WHERE tenant_id = UUID_TO_BIN(@tenant)", ("@tenant", tenantId.ToString()));
        await Sql("UPDATE tenant_subscriptions SET period_ends_at = UTC_TIMESTAMP(6) - INTERVAL 1 SECOND WHERE tenant_id = UUID_TO_BIN(@tenant)", ("@tenant", tenantId.ToString()));
        Assert.Equal(1, await store.RolloverDuePeriodsAsync(DateTime.UtcNow, 100));
        var rolled = await store.GetAsync(tenantId);
        Assert.Equal(2, rolled.Period!.Index);
        Assert.Equal(42000m, rolled.Usage.AvailableCredits);
        Assert.Equal(100m, rolled.Usage.ReservedCredits);
        Assert.Equal(-42400m, Convert.ToDecimal(await Scalar("SELECT amount FROM credit_ledger WHERE tenant_id = UUID_TO_BIN(@tenant) AND entry_type = 'expire'", ("@tenant", tenantId.ToString())), CultureInfo.InvariantCulture));
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM credit_ledger WHERE tenant_id = UUID_TO_BIN(@tenant) AND external_reference LIKE 'plan-period:%'", ("@tenant", tenantId.ToString())));
        var ledgerRows = await Scalar("SELECT COUNT(*) FROM credit_ledger WHERE tenant_id = UUID_TO_BIN(@tenant)", ("@tenant", tenantId.ToString()));
        Assert.Equal(0, await store.RolloverDuePeriodsAsync(DateTime.UtcNow, 100));
        Assert.Equal(ledgerRows, await Scalar("SELECT COUNT(*) FROM credit_ledger WHERE tenant_id = UUID_TO_BIN(@tenant)", ("@tenant", tenantId.ToString())));
        Assert.Equal(42000m, (await store.GetAsync(tenantId)).Usage.AvailableCredits);

        // 降级指派立即生效且不发放；到期后 Worker 自动降为免费体验版并写事件。
        var downgraded = await store.AssignAsync(tenantId, userId, new("starter", 2, null, "降级验收"));
        Assert.Equal("starter", downgraded.Plan.Code);
        Assert.Equal(42000m, downgraded.Usage.AvailableCredits);
        await Sql("UPDATE tenant_subscriptions SET expires_at = UTC_TIMESTAMP(6) - INTERVAL 1 SECOND WHERE tenant_id = UUID_TO_BIN(@tenant)", ("@tenant", tenantId.ToString()));
        Assert.Equal("free_trial", (await store.GetAsync(tenantId)).Plan.Code); // 到期未降级时按免费体验版返回。
        Assert.Equal(1, await store.DowngradeExpiredAsync(DateTime.UtcNow, 100));
        var expired = await store.GetForAdminAsync(tenantId);
        Assert.Equal(("free_trial", SubscriptionSource.Expiry), (expired.Subscription.Plan.Code, expired.Subscription.Source));
        Assert.Null(expired.Subscription.Period);
        Assert.Equal(SubscriptionEventType.ExpireDowngrade, expired.Events[0].Type);
        Assert.Equal(0, await store.DowngradeExpiredAsync(DateTime.UtcNow, 100));
    }

    [SubscriptionDatabaseFact]
    public async Task RedeemCoversUpgradeReuseRenewalDowngradeRevokedAndRateLimit()
    {
        var (tenantId, userId) = await RegisterTenantAsync();
        var store = Subscriptions(failuresPerHour: 3);
        var batch = await store.CreateCodeBatchAsync(new("basic", 3, 2, null, "验收批次"), userId);
        Assert.Equal(2, batch.Codes.Count);
        Assert.Equal(0L, await Scalar("SELECT COUNT(*) FROM plan_codes WHERE code_hint = @hint AND code_sha256 <> @sha",
            ("@hint", PlanCodeFormat.Hint(batch.Codes[0])), ("@sha", PlanCodeFormat.Hash(batch.Codes[0]))));
        var listed = await store.ListCodesAsync(batch.Batch.BatchId, "unused", null, null, 10);
        Assert.Equal(2, listed.Items.Count);
        Assert.All(listed.Items, item => Assert.Equal(PlanCodeStatus.Unused, item.Status));

        var upgraded = await store.RedeemAsync(tenantId, userId, batch.Codes[0].ToLowerInvariant(), "203.0.113.1");
        Assert.Equal(("basic", SubscriptionSource.Redeem, 1), (upgraded.Plan.Code, upgraded.Source, upgraded.Period!.Index));
        Assert.Equal(42500m, upgraded.Usage.AvailableCredits);
        var firstExpiry = upgraded.ExpiresAt!.Value;
        Assert.Equal("PLAN_CODE_USED", (await Assert.ThrowsAsync<CollectorStoreException>(() => store.RedeemAsync(tenantId, userId, batch.Codes[0], "203.0.113.1"))).Code);

        var renewed = await store.RedeemAsync(tenantId, userId, batch.Codes[1], "203.0.113.1");
        Assert.Equal(SubscriptionStore.AddMonthsClamped(firstExpiry.UtcDateTime, 3), renewed.ExpiresAt!.Value.UtcDateTime, TimeSpan.FromSeconds(1));
        Assert.Equal(1, renewed.Period!.Index);
        Assert.Equal(42500m, renewed.Usage.AvailableCredits);
        Assert.Equal(1L, await Scalar("SELECT COUNT(*) FROM credit_ledger WHERE tenant_id = UUID_TO_BIN(@tenant) AND external_reference LIKE 'plan-activate:%'", ("@tenant", tenantId.ToString())));

        var starter = await store.CreateCodeBatchAsync(new("starter", 1, 2), userId);
        Assert.Equal("PLAN_DOWNGRADE_NOT_ALLOWED", (await Assert.ThrowsAsync<CollectorStoreException>(() => store.RedeemAsync(tenantId, userId, starter.Codes[0], "203.0.113.1"))).Code);
        // 同批次卡密创建时间相同、列表顺序不保证，按末 4 位找到第二张再作废。
        var secondStarter = Assert.Single((await store.ListCodesAsync(starter.Batch.BatchId, null, null, null, 10)).Items,
            item => item.CodeHint == PlanCodeFormat.Hint(starter.Codes[1]));
        var revoked = await store.RevokeCodeAsync(secondStarter.CodeId, userId);
        Assert.Equal(PlanCodeStatus.Revoked, revoked.Status);
        Assert.Equal("PLAN_CODE_REVOKED", (await Assert.ThrowsAsync<CollectorStoreException>(() => store.RedeemAsync(tenantId, userId, starter.Codes[1], "203.0.113.1"))).Code);

        // 失败限流（每小时 3 次，按租户与 IP 计数）：上面的「已使用」「已作废」已计 2 次失败，
        // 成功兑换与「不能降级」不计数；第 3 次无效仍返回 INVALID，第 4 次 429，换 IP 也按租户被限流。
        Assert.Equal("PLAN_CODE_INVALID", (await Assert.ThrowsAsync<CollectorStoreException>(() => store.RedeemAsync(tenantId, userId, "JG-AAAA-AAAA-AAAA-AAAA", "203.0.113.1"))).Code);
        Assert.Equal("PLAN_REDEEM_RATE_LIMITED", (await Assert.ThrowsAsync<CollectorStoreException>(() => store.RedeemAsync(tenantId, userId, "JG-AAAA-AAAA-AAAA-AAAA", "203.0.113.1"))).Code);
        Assert.Equal("PLAN_REDEEM_RATE_LIMITED", (await Assert.ThrowsAsync<CollectorStoreException>(() => store.RedeemAsync(tenantId, userId, "JG-AAAA-AAAA-AAAA-AAAA", "198.51.100.7"))).Code);
    }

    [SubscriptionDatabaseFact]
    public async Task SecondActivationCycleStillGrantsItsOwnPeriodTwo()
    {
        var (tenantId, userId) = await RegisterTenantAsync();
        var store = Subscriptions();
        var tenant = ("@tenant", (object?)tenantId.ToString());

        // 第一轮：开通基础版并滚动到第 2 周期。
        await store.AssignAsync(tenantId, userId, new("basic", 3, null, "第一轮"));
        await Sql("UPDATE tenant_subscriptions SET period_ends_at = UTC_TIMESTAMP(6) - INTERVAL 1 SECOND WHERE tenant_id = UUID_TO_BIN(@tenant)", tenant);
        Assert.Equal(1, await store.RolloverDuePeriodsAsync(DateTime.UtcNow, 100));
        Assert.Equal(2, (await store.GetAsync(tenantId)).Period!.Index);

        // 降回免费体验版，再次开通：周期序号重置为 1。
        await store.AssignAsync(tenantId, userId, new("free_trial", null, null, "降回免费"));
        var reactivated = await store.AssignAsync(tenantId, userId, new("basic", 3, null, "第二轮"));
        Assert.Equal(1, reactivated.Period!.Index);
        Assert.Equal(84000m, reactivated.Usage.AvailableCredits);

        // 第二轮滚动到第 2 周期必须真正清零再发放；引用带开通时间，不能与第一轮的第 2 周期撞号。
        await Sql("UPDATE tenant_subscriptions SET period_ends_at = UTC_TIMESTAMP(6) - INTERVAL 1 SECOND WHERE tenant_id = UUID_TO_BIN(@tenant)", tenant);
        Assert.Equal(1, await store.RolloverDuePeriodsAsync(DateTime.UtcNow, 100));
        var rolled = await store.GetAsync(tenantId);
        Assert.Equal(2, rolled.Period!.Index);
        Assert.Equal(42000m, rolled.Usage.AvailableCredits);
        Assert.Equal(2L, await Scalar("SELECT COUNT(DISTINCT external_reference) FROM credit_ledger WHERE tenant_id = UUID_TO_BIN(@tenant) AND external_reference LIKE 'plan-period:%'", tenant));
        Assert.Equal(2L, await Scalar("SELECT COUNT(*) FROM credit_ledger WHERE tenant_id = UUID_TO_BIN(@tenant) AND entry_type = 'expire'", tenant));
        Assert.Equal(0, await store.RolloverDuePeriodsAsync(DateTime.UtcNow, 100));
    }

    [SubscriptionDatabaseFact]
    public async Task AssigningFreeTrialToExpiredPaidRowStoresFreeTrialPlanCode()
    {
        var (tenantId, userId) = await RegisterTenantAsync();
        var store = Subscriptions();
        var tenant = ("@tenant", (object?)tenantId.ToString());
        await store.AssignAsync(tenantId, userId, new("standard", 1, null, "开通标准版"));
        // 已到期但 Worker 尚未降级：读取按免费体验版生效，行上仍是 standard。
        await Sql("UPDATE tenant_subscriptions SET expires_at = UTC_TIMESTAMP(6) - INTERVAL 1 SECOND WHERE tenant_id = UUID_TO_BIN(@tenant)", tenant);
        Assert.Equal("standard", await Scalar("SELECT plan_code FROM tenant_subscriptions WHERE tenant_id = UUID_TO_BIN(@tenant)", tenant));

        var assigned = await store.AssignAsync(tenantId, userId, new("free_trial", null, null, "显式降回免费"));
        Assert.Equal("free_trial", assigned.Plan.Code);
        Assert.Null(assigned.ExpiresAt);
        Assert.Null(assigned.Period);
        Assert.Equal("free_trial", await Scalar("SELECT plan_code FROM tenant_subscriptions WHERE tenant_id = UUID_TO_BIN(@tenant)", tenant));
        Assert.Equal(0L, Convert.ToInt64(await Scalar("SELECT period_index FROM tenant_subscriptions WHERE tenant_id = UUID_TO_BIN(@tenant)", tenant), CultureInfo.InvariantCulture));
        Assert.Equal(0, await store.DowngradeExpiredAsync(DateTime.UtcNow, 100)); // 已经是免费体验版，Worker 无事可做。
    }

    [SubscriptionDatabaseFact]
    public async Task FreeTrialSignupUsedCountsOnlyConsumptionSinceEnteringTrial()
    {
        var (tenantId, userId) = await RegisterTenantAsync();
        var store = Subscriptions();
        var tenant = ("@tenant", (object?)tenantId.ToString());
        async Task CaptureAsync(decimal amount) => await Sql(
            "UPDATE credit_accounts SET available_credits = available_credits - @amount, lifetime_captured = lifetime_captured + @amount WHERE tenant_id = UUID_TO_BIN(@tenant)",
            tenant, ("@amount", amount));

        await CaptureAsync(300m);
        Assert.Equal(300m, (await store.GetAsync(tenantId)).Usage.SignupUsed);

        await store.AssignAsync(tenantId, userId, new("basic", 1, null, "开通"));
        await CaptureAsync(200m);
        Assert.Equal(200m, (await store.GetAsync(tenantId)).Period!.Used);

        // 降回免费体验版：基线取当时的累计结算，免费版「已用」从 0 起算，不把付费期与历史消耗算进来。
        await store.AssignAsync(tenantId, userId, new("free_trial", null, null, "降回"));
        Assert.Equal(0m, (await store.GetAsync(tenantId)).Usage.SignupUsed);
        await CaptureAsync(50m);
        Assert.Equal(50m, (await store.GetAsync(tenantId)).Usage.SignupUsed);

        // 到期降级同样重置基线。
        await store.AssignAsync(tenantId, userId, new("starter", 1, null, "再开通"));
        await CaptureAsync(10m);
        await Sql("UPDATE tenant_subscriptions SET expires_at = UTC_TIMESTAMP(6) - INTERVAL 1 SECOND WHERE tenant_id = UUID_TO_BIN(@tenant)", tenant);
        Assert.Equal(1, await store.DowngradeExpiredAsync(DateTime.UtcNow, 100));
        Assert.Equal(0m, (await store.GetAsync(tenantId)).Usage.SignupUsed);
    }

    private static async Task<object?> Scalar(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await command.ExecuteScalarAsync();
    }

    private static async Task Sql(string sql, params (string Name, object? Value)[] parameters) => _ = await Scalar(sql, parameters);

    private sealed class RecordingSender : IRegistrationEmailSender
    {
        public string? Code { get; private set; }
        public void EnsureConfigured() { }
        public Task SendCodeAsync(string email, string code, CancellationToken cancellationToken = default)
        {
            Code = code;
            return Task.CompletedTask;
        }
    }
}

/// <summary>需要隔离测试库 jogeoai_subscription_20261019；缺少连接串即跳过。</summary>
public sealed class SubscriptionDatabaseFactAttribute : FactAttribute
{
    public SubscriptionDatabaseFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JOGEOAI_SUBSCRIPTION_TEST_CONNECTION")))
            Skip = "需要隔离测试库 jogeoai_subscription_20261019（已执行 001–032）；不能使用业务库。";
    }
}
