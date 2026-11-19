using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace Jogeoai.Tests;

public sealed class RegistrationOnboardingTests
{
    private const string TestDatabase = "jogeoai_registration_20260929";
    private const string Password = "Registration-test-password-2026!";
    private static string ConnectionString
    {
        get
        {
            var value = Environment.GetEnvironmentVariable("JOGEOAI_REGISTRATION_TEST_CONNECTION")!;
            Assert.Equal(TestDatabase, new MySqlConnectionStringBuilder(value).Database);
            return value;
        }
    }
    private static string Email() => $"registration-{Guid.NewGuid():N}@example.test";
    private static (IdentityStore Identity, RecordingSender Sender) Identity()
    {
        var sender = new RecordingSender();
        return (new IdentityStore(new MySqlDatabase(ConnectionString), sender), sender);
    }
    private static async Task<AuthSessionView> Register(IdentityStore identity, RecordingSender sender, string email)
    {
        await identity.SendRegistrationCodeAsync(new(email));
        return await identity.RegisterAsync(new(email, Password, sender.Code!, "测试账号"), new());
    }

    [Fact]
    public void SmtpConfigurationRequiresAnAuthorizationCodeAndImplicitTlsPort()
    {
        var options = new RegistrationEmailOptions { Username = "sender@163.com" };
        var sender = new RegistrationEmailSender(Options.Create(options));
        Assert.Equal("EMAIL_NOT_CONFIGURED", Assert.Throws<CollectorStoreException>(sender.EnsureConfigured).Code);
        options.Password = "test-only"; options.Port = 587;
        Assert.Throws<CollectorStoreException>(sender.EnsureConfigured);
        options.Port = 465;
        sender.EnsureConfigured();
    }

    [Fact]
    public async Task MissingOrMalformedCodeCannotReachAccountCreation()
    {
        var identity = new IdentityStore(new MySqlDatabase("invalid"), new RecordingSender());
        foreach (var code in new[] { "", "12345", "1234567", "１２３４５６", "abcdef" })
            Assert.Equal("INVALID_EMAIL_CODE", (await Assert.ThrowsAsync<CollectorStoreException>(() =>
                identity.RegisterAsync(new("test@example.test", Password, code), new()))).Code);
        Assert.Equal("INVALID_EMAIL", (await Assert.ThrowsAsync<CollectorStoreException>(() =>
            identity.SendRegistrationCodeAsync(new("test\r\n@example.test")))).Code);
    }

    [RegistrationDatabaseFact]
    public async Task VerificationIsHashedEmailBoundAndLimitedToFiveAttempts()
    {
        var (identity, sender) = Identity(); var email = Email();
        var receipt = await identity.SendRegistrationCodeAsync(new(email.ToUpperInvariant()));
        Assert.Equal(email, sender.Email);
        Assert.Equal(60, receipt.ResendAfterSeconds);
        Assert.InRange(receipt.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(9), DateTimeOffset.UtcNow.AddMinutes(11));
        Assert.Matches("^[0-9]{6}$", sender.Code!);
        var hash = (string)(await Scalar("SELECT code_hash FROM auth_email_verifications WHERE email = @email", email))!;
        Assert.StartsWith("pbkdf2-sha256$", hash);
        Assert.NotEqual(sender.Code, hash);
        Assert.Equal("EMAIL_CODE_RATE_LIMITED", (await Assert.ThrowsAsync<CollectorStoreException>(() => identity.SendRegistrationCodeAsync(new(email)))).Code);
        Assert.Equal("EMAIL_CODE_EXPIRED", (await Assert.ThrowsAsync<CollectorStoreException>(() => identity.RegisterAsync(new(Email(), Password, sender.Code!), new()))).Code);
        var wrong = sender.Code == "000000" ? "000001" : "000000";
        for (var i = 0; i < 5; i++)
            Assert.Equal("INVALID_EMAIL_CODE", (await Assert.ThrowsAsync<CollectorStoreException>(() => identity.RegisterAsync(new(email, Password, wrong), new()))).Code);
        Assert.Equal("EMAIL_CODE_EXPIRED", (await Assert.ThrowsAsync<CollectorStoreException>(() => identity.RegisterAsync(new(email, Password, sender.Code!), new()))).Code);
        Assert.Null(await identity.LoginAsync(new(email, Password), new()));
        Assert.Equal(5L, Convert.ToInt64(await Scalar("SELECT attempts FROM auth_email_verifications WHERE email = @email", email)));
    }

    [RegistrationDatabaseFact]
    public async Task ExpiryResendAndDeliveryFailureCannotBypassVerification()
    {
        var (identity, sender) = Identity(); var email = Email();
        await identity.SendRegistrationCodeAsync(new(email));
        await Sql("UPDATE auth_email_verifications SET expires_at = UTC_TIMESTAMP(6) - INTERVAL 1 SECOND, sent_at = UTC_TIMESTAMP(6) - INTERVAL 61 SECOND WHERE email = @email", email);
        Assert.Equal("EMAIL_CODE_EXPIRED", (await Assert.ThrowsAsync<CollectorStoreException>(() => identity.RegisterAsync(new(email, Password, sender.Code!), new()))).Code);
        await identity.SendRegistrationCodeAsync(new(email));
        var valid = await identity.RegisterAsync(new(email, Password, sender.Code!), new());
        Assert.Equal(email, valid.User.Email);
        var failedEmail = Email(); sender.Fail = true;
        await Assert.ThrowsAsync<CollectorStoreException>(() => identity.SendRegistrationCodeAsync(new(failedEmail)));
        Assert.Equal("EMAIL_CODE_EXPIRED", (await Assert.ThrowsAsync<CollectorStoreException>(() => identity.RegisterAsync(new(failedEmail, Password, sender.Code!), new()))).Code);
    }

    [RegistrationDatabaseFact]
    public async Task ConcurrentCodeConsumptionCreatesOneAccountAndOneCreditGrant()
    {
        var (identity, sender) = Identity(); var email = Email();
        await identity.SendRegistrationCodeAsync(new(email));
        var request = new RegisterRequest(email, Password, sender.Code!);
        async Task<AuthSessionView?> Attempt()
        {
            try { return await identity.RegisterAsync(request, new()); }
            catch (CollectorStoreException e) when (e.Code == "EMAIL_CODE_EXPIRED") { return null; }
        }
        var results = await Task.WhenAll(Attempt(), Attempt());
        var session = Assert.Single(results, r => r is not null)!;
        Assert.Equal(500m, Convert.ToDecimal(await Scalar("SELECT a.available_credits FROM credit_accounts a JOIN tenant_members m ON m.tenant_id = a.tenant_id JOIN auth_users u ON u.id = m.user_id WHERE u.email = @email", email)));
        Assert.Equal(1L, Convert.ToInt64(await Scalar("SELECT COUNT(*) FROM credit_ledger l JOIN auth_users u ON u.id = l.actor_user_id WHERE u.email = @email", email)));
        // 024 之后注册同时写入免费体验版订阅（隔离库需先执行 024）。
        Assert.Equal("free_trial", (string)(await Scalar("SELECT s.plan_code FROM tenant_subscriptions s JOIN tenant_members m ON m.tenant_id = s.tenant_id JOIN auth_users u ON u.id = m.user_id WHERE u.email = @email", email))!);
        Assert.Equal("registration", (string)(await Scalar("SELECT s.source FROM tenant_subscriptions s JOIN tenant_members m ON m.tenant_id = s.tenant_id JOIN auth_users u ON u.id = m.user_id WHERE u.email = @email", email))!);
        Assert.NotEqual(DBNull.Value, await Scalar("SELECT email_verified_at FROM auth_users WHERE email = @email", email));
        Assert.NotNull(await identity.LoginAsync(new(email, Password), new()));
        // IdentityStore 始终返回令牌；只有 AuthController 按投递方式在响应体中省略。
        var sessionToken = Assert.IsType<string>(session.SessionToken);
        Assert.NotNull(await identity.ValidateSessionAsync(sessionToken));
        await identity.RevokeSessionAsync(sessionToken);
        Assert.Null(await identity.ValidateSessionAsync(sessionToken));
    }

    [RegistrationDatabaseFact]
    public async Task SameEmailCannotSendConcurrentlyOrExceedHourlyQuota()
    {
        var (identity, _) = Identity(); var email = Email();
        async Task<bool> Send()
        {
            try { await identity.SendRegistrationCodeAsync(new(email)); return true; }
            catch (CollectorStoreException e) when (e.Code == "EMAIL_CODE_RATE_LIMITED") { return false; }
        }
        Assert.Single(await Task.WhenAll(Send(), Send()), sent => sent);
        await Sql("UPDATE auth_email_verifications SET sent_at = UTC_TIMESTAMP(6) - INTERVAL 61 SECOND, send_count = 10 WHERE email = @email", email);
        Assert.False(await Send());
    }

    [RegistrationDatabaseFact]
    public async Task OnboardingRequiresTeamAndBrandPersistsDraftAndCompletesAtomically()
    {
        var (identity, sender) = Identity(); var session = await Register(identity, sender, Email());
        var tenantId = session.User.Memberships[0].TenantId;
        var database = new MySqlDatabase(ConnectionString);
        var progress = new OnboardingProgressStore(database); var management = new ManagementStore(database);
        var workspace = await progress.GetWorkspaceAsync(tenantId);
        Assert.True(workspace.Required); Assert.False(workspace.TeamConfigured); Assert.Null(workspace.SiteId);
        Assert.Equal("TEAM_REQUIRED", (await Assert.ThrowsAsync<CollectorStoreException>(() => management.CreateBrandAsync(tenantId, new("品牌", "example.test", Description: "测试品牌提供设备与服务。")))).Code);
        Assert.Equal("INVALID_TEAM_NAME", (await Assert.ThrowsAsync<CollectorStoreException>(() => progress.ConfigureTeamAsync(tenantId, session.User.UserId, "  "))).Code);
        Assert.Equal("TEAM_OWNER_REQUIRED", (await Assert.ThrowsAsync<CollectorStoreException>(() => progress.ConfigureTeamAsync(tenantId, Guid.NewGuid(), "团队"))).Code);
        await progress.ConfigureTeamAsync(tenantId, session.User.UserId, "增长团队");
        await Assert.ThrowsAsync<CollectorStoreException>(() => management.CreateBrandAsync(tenantId, new(" ", "example.test")));
        await Assert.ThrowsAsync<CollectorStoreException>(() => management.CreateBrandAsync(tenantId, new("品牌", "")));
        var brand = await management.CreateBrandAsync(tenantId, new("测试品牌", "brand.example.test", DefaultCountry: "CN", DefaultLocale: "zh-CN", TimeZone: "Asia/Shanghai", Description: "测试品牌提供设备与服务。"));
        Assert.Equal(brand.SiteId, (await progress.GetWorkspaceAsync(tenantId)).SiteId);
        // 免费体验版只允许 1 个品牌（024 套餐上限）；指派基础版后再建第二个品牌，引导锚点仍是第一个品牌。
        var brandLimit = await Assert.ThrowsAsync<PlanLimitExceededException>(() => management.CreateBrandAsync(tenantId, new("第二品牌", "second.example.test", Description: "第二品牌提供技术支持。")));
        Assert.Equal((PlanLimitKind.Brands, 1, 1), (brandLimit.Limit, brandLimit.LimitValue, brandLimit.CurrentValue));
        await new SubscriptionStore(database, Options.Create(new SubscriptionOptions()))
            .AssignAsync(tenantId, session.User.UserId, new("basic", 1, null, "注册引导测试需要多品牌"));
        await management.CreateBrandAsync(tenantId, new("第二品牌", "second.example.test", Description: "第二品牌提供技术支持。"));
        Assert.Equal(brand.SiteId, (await progress.GetWorkspaceAsync(tenantId)).SiteId);
        var prompts = new[] { new PromptWrite("哪个品牌适合我？", "zh-CN") };
        await Assert.ThrowsAsync<CollectorStoreException>(() => progress.SaveDraftAsync(tenantId, brand.SiteId, new("select_competitors", [], [])));
        await progress.SaveDraftAsync(tenantId, brand.SiteId, new("select_competitors", prompts, []));
        var saved = await progress.GetAsync(tenantId, brand.SiteId);
        Assert.Equal("select_competitors", saved!.CurrentStep); Assert.Single(saved.Draft!.Prompts);
        await Assert.ThrowsAsync<CollectorStoreException>(() => management.CompleteOnboardingAsync(tenantId, brand.SiteId, new([], [])));
        await Assert.ThrowsAsync<CollectorStoreException>(() => management.CompleteOnboardingAsync(tenantId, brand.SiteId, new([], Enumerable.Repeat(prompts[0], 6).ToArray())));
        var competitors = new[] { new CompetitorWrite("测试竞品", Domains: ["rival.example.test"]) };
        // 在竞品写入之后由重复问题触发失败，验证整个事务回滚。
        Assert.Equal("PROMPT_DUPLICATE", (await Assert.ThrowsAsync<CollectorStoreException>(() => management.CompleteOnboardingAsync(tenantId, brand.SiteId, new(competitors, [prompts[0], prompts[0]])))).Code);
        Assert.Empty((await management.ListCompetitorsAsync(tenantId, brand.SiteId, null, 10)).Items);
        Assert.Empty((await management.ListPromptsAsync(tenantId, brand.SiteId, null, null, null, 10)).Items);
        Assert.False((await progress.GetAsync(tenantId, brand.SiteId))!.Completed);
        var result = await management.CompleteOnboardingAsync(tenantId, brand.SiteId, new(competitors, prompts));
        Assert.True(result.Progress.Completed); Assert.Equal(1, result.PromptsCreated); Assert.Equal(1, result.CompetitorsCreated);
        Assert.False((await progress.GetWorkspaceAsync(tenantId)).Required);
        var replay = await management.CompleteOnboardingAsync(tenantId, brand.SiteId, new(competitors, prompts));
        Assert.Equal(0, replay.PromptsCreated); Assert.Equal(0, replay.CompetitorsCreated);
        await Assert.ThrowsAsync<CollectorStoreException>(() => progress.SaveDraftAsync(tenantId, brand.SiteId, new("select_prompts", prompts, [])));
        Assert.Null(await progress.GetAsync(Guid.NewGuid(), brand.SiteId));
        await Assert.ThrowsAsync<CollectorStoreException>(() => management.CompleteOnboardingAsync(Guid.NewGuid(), brand.SiteId, new([], prompts)));
    }

    private static async Task<object?> Scalar(string sql, string email)
    {
        await using var connection = new MySqlConnection(ConnectionString); await connection.OpenAsync();
        await using var command = new MySqlCommand(sql, connection); command.Parameters.AddWithValue("@email", email);
        return await command.ExecuteScalarAsync();
    }
    private static async Task Sql(string sql, string email) => _ = await Scalar(sql, email);

    private sealed class RecordingSender : IRegistrationEmailSender
    {
        public string? Email { get; private set; }
        public string? Code { get; private set; }
        public bool Fail { get; set; }
        public void EnsureConfigured() { }
        public Task SendCodeAsync(string email, string code, CancellationToken cancellationToken = default)
        {
            Email = email; Code = code;
            if (Fail) throw new CollectorStoreException("EMAIL_SEND_FAILED", "模拟发送失败", 503);
            return Task.CompletedTask;
        }
    }
}

public sealed class RegistrationDatabaseFactAttribute : FactAttribute
{
    public RegistrationDatabaseFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JOGEOAI_REGISTRATION_TEST_CONNECTION")))
            Skip = "需要隔离测试库 jogeoai_registration_20260929；不能使用业务库。";
    }
}
