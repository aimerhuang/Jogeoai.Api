using System.Globalization;
using System.Reflection;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using MySqlConnector;

namespace Jogeoai.Tests;

/// <summary>
/// 文章创作提交 / 认领 / 阶段 / 结算的 SQL 回归：只用显式隔离且已执行 001–040 的测试库（JOGEOAI_CONTENT_TEST_CONNECTION），
/// 不调用 HTTP、模型或真实账户；未提供连接时跳过。
/// </summary>
[Collection("Knowledge isolated SQL")]
public sealed class ArticleStudioSubmitSqlTests
{
    [ContentMySqlFact]
    public async Task SubmitReservesOnceReplaysAndClaimsStudioTaskThroughCapture()
    {
        var f = await Fixture.CreateAsync();
        var requirements = new string('要', ArticleStudioLimits.RequirementsMaxLength);
        var submission = f.Submission(requirements: requirements);
        var accepted = await f.Store.SubmitStudioGenerationAsync(f.Tenant, f.Site, f.User, submission, "studio-a");
        Assert.Equal(100m, accepted.ReservedCredits);
        Assert.False(accepted.RegeneratedAsNew);
        Assert.Equal(["900", "100", "0"], await f.AccountAsync());

        // Same key + same request replays without a second reservation; a different request conflicts.
        var replay = await f.Store.SubmitStudioGenerationAsync(f.Tenant, f.Site, f.User, submission, "studio-a");
        Assert.Equal((accepted.TaskId, accepted.ArticleId), (replay.TaskId, replay.ArticleId));
        var found = await f.Store.FindStudioSubmissionAsync(f.Tenant, f.Site, "studio-a");
        Assert.Equal(submission.RequestHash, found!.Value.RequestHash);
        Assert.Equal("IDEMPOTENCY_CONFLICT", (await Assert.ThrowsAsync<CollectorStoreException>(() =>
            f.Store.SubmitStudioGenerationAsync(f.Tenant, f.Site, f.User, submission with { RequestHash = new string('f', 64) }, "studio-a"))).Code);
        Assert.Equal(["900", "100", "0"], await f.AccountAsync());
        Assert.Equal(1, await f.LedgerCountAsync("reserve"));

        var pending = (await f.Store.GetStudioGenerationAsync(f.Tenant, f.Site, accepted.TaskId))!;
        Assert.Equal(ArticleStudioRules.Stages, pending.Stages.Select(stage => stage.Key));
        Assert.All(pending.Stages, stage => Assert.Equal(ArticleStudioStageStatus.Pending, stage.Status));

        var work = await f.ClaimAsync(accepted.BatchId);
        Assert.Equal(ArticleStudioLimits.StudioPipeline, work.Pipeline);
        Assert.Equal(requirements, work.ExtraRequirements);
        Assert.Equal(ArticleOptimizationGoal.Ranking, work.OptimizationGoal);
        Assert.Equal(f.Prompt.PromptId, work.PromptId);
        Assert.Equal("product_selection", work.Strategy);
        Assert.True(work.WithoutRag);

        var started = DateTimeOffset.UtcNow;
        Assert.True(await f.Store.SaveStageAsync(work, new(ArticleStudioStageKey.Citations, ArticleStudioStageStatus.Succeeded, started,
            started.AddMilliseconds(420), new ArticleStudioStageDetail(ItemCount: 2), null)));
        Assert.True(await f.Store.ReplaceSourcesAsync(work, ArticleStudioSourceKind.Citation,
        [
            new(ArticleStudioSourceKind.Citation, "https://open.example/a", "open.example", "指南", ["deepseek"], 4, "third_party", false, ArticleStudioFetchStatus.Fetched, null, "正文", 1),
            new(ArticleStudioSourceKind.Citation, "https://blocked.example/b", "blocked.example", null, ["doubao"], 2, "competitor", true, ArticleStudioFetchStatus.Blocked, "SOURCE_BLOCKED", null, 2),
        ]));
        var foreignLease = work with { LeaseOwner = "someone-else" };
        Assert.False(await f.Store.SaveStageAsync(foreignLease, new(ArticleStudioStageKey.Fetch, ArticleStudioStageStatus.Failed, started, started, null, null)));
        Assert.Equal("正文", Assert.Single(await f.Store.ListSourcesAsync(f.Tenant, work.TaskId, ArticleStudioSourceKind.Citation),
            source => source.FetchStatus == ArticleStudioFetchStatus.Fetched).Content);

        var running = (await f.Store.GetStudioGenerationAsync(f.Tenant, f.Site, accepted.TaskId))!;
        Assert.Equal((1, 2), (running.FetchedCount, running.HitCount));
        Assert.Equal("访问被拒绝", Assert.Single(running.Sources, source => source.FetchStatus == ArticleStudioFetchStatus.Blocked).ErrorLabel);
        var citations = running.Stages[0];
        Assert.Equal(ArticleStudioStageStatus.Succeeded, citations.Status);
        Assert.Equal(420, citations.DurationMs);
        Assert.Equal(ArticleStudioStageStatus.Pending, running.Stages[1].Status);

        await f.Store.CompleteGenerationTaskAsync(work, StudioFakes.Result());
        var done = (await f.Store.GetStudioGenerationAsync(f.Tenant, f.Site, accepted.TaskId))!;
        Assert.Equal(ArticleGenerationTaskStatus.Succeeded, done.Status);
        Assert.NotNull(done.VersionId);
        Assert.Equal(CreditReservationStatus.Captured, done.ReservationStatus);
        Assert.Equal(["900", "0", "100"], await f.AccountAsync());
        Assert.Equal(1, await f.LedgerCountAsync("capture"));
    }

    [ContentMySqlFact]
    public async Task RegenerationReusesUnpublishedArticleCreatesNewWhenPublishedAndConflictsWhileRunning()
    {
        var f = await Fixture.CreateAsync();
        var first = await f.Store.SubmitStudioGenerationAsync(f.Tenant, f.Site, f.User, f.Submission(), "regen-1");
        await f.Store.CompleteGenerationTaskAsync(await f.ClaimAsync(first.BatchId), StudioFakes.Result());

        var again = await f.Store.SubmitStudioGenerationAsync(f.Tenant, f.Site, f.User, f.Submission(articleId: first.ArticleId), "regen-2");
        Assert.Equal(first.ArticleId, again.ArticleId);
        Assert.False(again.RegeneratedAsNew);
        var busy = await Assert.ThrowsAsync<CollectorStoreException>(() =>
            f.Store.SubmitStudioGenerationAsync(f.Tenant, f.Site, f.User, f.Submission(articleId: first.ArticleId), "regen-3"));
        Assert.Equal(("ARTICLE_GENERATION_IN_PROGRESS", 409), (busy.Code, busy.StatusCode));
        await f.Store.CompleteGenerationTaskAsync(await f.ClaimAsync(again.BatchId), StudioFakes.Result());
        var detail = (await f.Store.GetArticleAsync(f.Tenant, f.Site, first.ArticleId))!;
        Assert.Equal(2, detail.Versions.Count(version => version.VersionKind == ArticleVersionKind.Generated));
        Assert.Equal(2, detail.CurrentVersion!.VersionNo);
        Assert.Equal(1, await f.CountAsync("content_articles"));

        await f.Store.AddPublicationLinkAsync(f.Tenant, f.Site, first.ArticleId, f.User,
            new ArticlePublicationLinkWrite($"https://published.example/{Guid.NewGuid():N}", OwnershipMatch: false));
        var published = await f.Store.SubmitStudioGenerationAsync(f.Tenant, f.Site, f.User, f.Submission(articleId: first.ArticleId), "regen-4");
        Assert.NotEqual(first.ArticleId, published.ArticleId);
        Assert.True(published.RegeneratedAsNew);
        Assert.True((await f.Store.FindStudioSubmissionAsync(f.Tenant, f.Site, "regen-4"))!.Value.Accepted.RegeneratedAsNew);
        Assert.Equal(2, await f.CountAsync("content_articles"));
        var latest = await f.Store.GetLatestStudioArticleAsync(f.Tenant, f.Site, f.Prompt.PromptId);
        Assert.Equal(published.ArticleId, latest!.ArticleId);
        Assert.Equal(["700", "100", "200"], await f.AccountAsync());
    }

    private sealed class Fixture
    {
        private readonly string connectionString;
        private readonly ManagementStore management;
        internal ContentStore Store { get; }
        internal Guid Tenant { get; } = Guid.NewGuid();
        internal Guid User { get; } = Guid.NewGuid();
        internal Guid Site { get; private set; }
        internal PromptView Prompt { get; private set; } = null!;

        private Fixture(string connection)
        {
            connectionString = connection;
            var database = new MySqlDatabase(connection);
            management = new ManagementStore(database);
            var knowledge = new KnowledgeStore(database, null!, null!, null!);
            Store = new ContentStore(database, management, new CreditStore(database), DispatchProxy.Create<IResearchStore, StudioProxy>(),
                null!, knowledge, knowledge, new ArticleProductResolver(new BrandKnowledgeItemStore(database), new KnowledgeProductStore(database, knowledge, management)));
        }

        internal ArticleStudioSubmission Submission(string? requirements = null, Guid? articleId = null)
            => new($"{Guid.NewGuid():N}{Guid.NewGuid():N}", Prompt, "[]", "zh-CN", "product_selection", "medium", "professional",
                requirements, true, null, null, null, null, ArticleOptimizationGoal.Ranking, ArticleStudioGoalBasis.RecentData,
                false, [], articleId, ArticleGenerationPricing.UnitPrice);

        internal static async Task<Fixture> CreateAsync()
        {
            var value = Environment.GetEnvironmentVariable("JOGEOAI_CONTENT_TEST_CONNECTION");
            Assert.False(string.IsNullOrWhiteSpace(value));
            var builder = new MySqlConnectionStringBuilder(value!);
            Assert.Contains("_test", builder.Database, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual("jogeoai", builder.Database, StringComparer.OrdinalIgnoreCase);
            builder.AllowUserVariables = false;
            builder.GuidFormat = MySqlGuidFormat.Default;
            var f = new Fixture(builder.ConnectionString);
            Assert.True(await f.ScalarAsync("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name IN ('article_generation_task_stages','article_generation_task_sources');") == 2,
                "测试库尚未执行 040_article_studio.sql。");
            await f.ExecAsync("""
                INSERT INTO tenants (id,name,status,created_at,updated_at) VALUES (UUID_TO_BIN(@tenant),'article studio artificial fixture','active',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
                INSERT INTO auth_users (id,email,password_hash,status,created_at,updated_at) VALUES (UUID_TO_BIN(@user),@email,'not-a-login','active',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6));
                INSERT INTO credit_accounts (tenant_id,available_credits,updated_at) VALUES (UUID_TO_BIN(@tenant),1000,UTC_TIMESTAMP(6));
                """, ("@tenant", f.Tenant.ToString()), ("@user", f.User.ToString()), ("@email", $"studio-{f.User:N}@example.test"));
            f.Site = (await f.management.CreateBrandAsync(f.Tenant, new BrandWrite("合成品牌", $"studio-{f.Tenant:N}.test", Description: "合成品牌介绍"))).SiteId;
            f.Prompt = await f.management.CreatePromptAsync(f.Tenant, f.Site, new PromptWrite("合成品类如何选型？", "zh-CN"));
            return f;
        }

        internal async Task<ArticleGenerationWorkItem> ClaimAsync(Guid batch)
        {
            // Prioritize only this fixture over retained diagnostic rows; other tenants' rows stay untouched.
            var oldest = (await RowAsync("SELECT DATE_FORMAT(DATE_SUB(MIN(created_at),INTERVAL 1 SECOND),'%Y-%m-%d %H:%i:%s.%f') FROM article_generation_tasks WHERE @tenant IS NOT NULL;"))[0];
            await ExecAsync("UPDATE article_generation_tasks SET created_at=@created WHERE tenant_id=UUID_TO_BIN(@tenant) AND batch_id=UUID_TO_BIN(@batch);",
                ("@tenant", Tenant.ToString()), ("@batch", batch.ToString()), ("@created", oldest));
            var work = await Store.ClaimGenerationTaskAsync($"synthetic-{Guid.NewGuid():N}", TimeSpan.FromMinutes(10));
            Assert.NotNull(work);
            Assert.Equal(batch, work.BatchId);
            return work;
        }

        internal async Task<string[]> AccountAsync() => await RowAsync("SELECT CAST(available_credits AS SIGNED),CAST(reserved_credits AS SIGNED),CAST(lifetime_captured AS SIGNED) FROM credit_accounts WHERE tenant_id=UUID_TO_BIN(@tenant);");
        internal async Task<int> CountAsync(string table) => int.Parse((await RowAsync($"SELECT COUNT(*) FROM {table} WHERE tenant_id=UUID_TO_BIN(@tenant);"))[0], CultureInfo.InvariantCulture);
        internal async Task<int> LedgerCountAsync(string kind) => int.Parse((await RowAsync("SELECT COUNT(*) FROM credit_ledger WHERE tenant_id=UUID_TO_BIN(@tenant) AND entry_type=@kind;", ("@kind", kind)))[0], CultureInfo.InvariantCulture);
        private async Task<long> ScalarAsync(string sql) => long.Parse((await RowAsync(sql))[0], CultureInfo.InvariantCulture);

        private async Task<string[]> RowAsync(string sql, params (string Name, object? Value)[] parameters)
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new MySqlCommand(sql, connection);
            if (sql.Contains("@tenant", StringComparison.Ordinal)) command.Parameters.AddWithValue("@tenant", Tenant.ToString());
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return Enumerable.Range(0, reader.FieldCount).Select(i => Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? "").ToArray();
        }

        internal async Task ExecAsync(string sql, params (string Name, object? Value)[] parameters)
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new MySqlCommand(sql, connection);
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            await command.ExecuteNonQueryAsync();
        }
        // Artificial fixtures stay in the isolated DB for audit; database cleanup is a separately confirmed operation.
    }
}
