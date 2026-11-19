using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Jogeoai.Application;
using Jogeoai.Domain;
using Jogeoai.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;

namespace Jogeoai.Tests;

/// <summary>
/// 迁移 027 的隔离库用例：自动发现（去重、回溯窗口、水位、标题读取）与每日轮次（开轮、结算、余额不足、重放）。
/// 只连接 JOGEOAI_CONTENT_TEST_CONNECTION 指向的、已执行 001–027 的独立测试库；未配置时跳过。
/// </summary>
public sealed class ArticleLinkMonitoringSqlTests
{
    [ContentMySqlFact]
    public async Task DiscoveryCreatesTrackedArticlesOnlyForUnregisteredOwnedCitationsAndAdvancesTheWatermark()
    {
        var f = await Fixture.CreateAsync();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var fresh = "https://acme-link.test/blog/fresh";
            var registered = "https://acme-link.test/blog/registered";
            var disabledUrl = "https://acme-link.test/blog/disabled";
            var stale = "https://acme-link.test/blog/stale";
            var thirdParty = "https://zhihu.com/p/owned-by-someone-else";
            // 已登记（手动）与已停用链接不再自动建文。
            var manual = await f.ArticleAsync("手动登记", now.AddHours(-3));
            await f.Store.AddPublicationLinkAsync(f.Tenant, f.Site, manual, f.User, new ArticlePublicationLinkWrite(registered));
            var disabledArticle = await f.ArticleAsync("已停用", now.AddHours(-3));
            var disabledLink = await f.Store.AddPublicationLinkAsync(f.Tenant, f.Site, disabledArticle, f.User, new ArticlePublicationLinkWrite(disabledUrl));
            await f.Store.DisablePublicationLinkAsync(f.Tenant, f.Site, disabledArticle, disabledLink.LinkId, f.User);

            var early = await f.CheckAsync(now.AddHours(-2), [(fresh, "brand", "Fresh page title"), (registered, "brand", null), (disabledUrl, "brand", null), (thirdParty, "third_party", null)]);
            await f.CheckAsync(now.AddHours(-1), [(fresh, "brand", "Later title")]);
            await f.CheckAsync(now.AddDays(-40), [(stale, "brand", "Too old")]);

            var first = await f.Monitoring.DiscoverAsync(f.Tenant, f.Site, now);
            Assert.Equal(3, first.Scanned);
            Assert.Equal(1, first.Created);
            Assert.Equal(2, first.SkippedExisting);
            Assert.Null(first.SkipReason);

            var list = await f.Store.ListArticlesAsync(f.Tenant, f.Site, new ArticleListFilter(SourceKind: ArticleSourceKind.CitationDiscovered));
            var discovered = Assert.Single(list.Items);
            Assert.Equal("Fresh page title", discovered.Title);
            Assert.Equal(ArticleStage.Tracking, discovered.Stage);
            Assert.Equal(ArticleMonitorStatus.Active, discovered.MonitorStatus);
            Assert.True(discovered.OwnedPublication);
            Assert.Equal(f.Owner, discovered.CreatedBy);
            Assert.Equal(early, discovered.Discovery!.CheckId);
            Assert.Equal("chatgpt", discovered.Discovery.Engine);
            var detail = (await f.Store.GetArticleAsync(f.Tenant, f.Site, discovered.ArticleId))!;
            var link = Assert.Single(detail.PublicationLinks);
            Assert.Equal(early, link.DiscoveredCheckId);
            Assert.NotNull(link.DiscoveredAt);
            Assert.Equal(fresh, link.UrlNormalized);
            Assert.Equal("pending", link.ReadStatus);

            var state = await f.RowAsync("SELECT last_arrival_id, backfill_completed_at FROM content_citation_discovery_state WHERE tenant_id=UUID_TO_BIN(@tenant) AND site_id=UUID_TO_BIN(@site);");
            Assert.True(long.Parse(state![0], CultureInfo.InvariantCulture) > 0);
            Assert.False(string.IsNullOrWhiteSpace(state[1]));

            // 重跑不重复建文；新的 owned 引用在水位之后被增量发现（回溯窗口不再限制）。
            var again = await f.Monitoring.DiscoverAsync(f.Tenant, f.Site, now);
            Assert.Equal(0, again.Created);
            await f.CheckAsync(now.AddMinutes(-5), [("https://acme-link.test/blog/incremental", "brand", null)]);
            var incremental = await f.Monitoring.DiscoverAsync(f.Tenant, f.Site, now);
            Assert.Equal(1, incremental.Created);
            var all = await f.Store.ListArticlesAsync(f.Tenant, f.Site, new ArticleListFilter(SourceKind: ArticleSourceKind.CitationDiscovered));
            Assert.Equal(2, all.Items.Count);
            Assert.Contains(all.Items, item => item.Title == "https://acme-link.test/blog/incremental");

            // 标题读取：假读取器返回页面标题 → 占位标题被替换；读取失败的保留 URL 并记录原因。
            f.Reader.Results[fresh] = new ArticleSourceReadResult(fresh, fresh, "", "ready", "Read from page", "body", null, [], null, null);
            f.Reader.Results["https://acme-link.test/blog/incremental"] = new ArticleSourceReadResult(fresh, fresh, "", "blocked", null, null, null, [], "SOURCE_BLOCKED", "网页拒绝了读取请求。");
            Assert.Equal(2, await f.Monitoring.ReadPendingTitlesAsync(f.Tenant, f.Site, 20));
            Assert.Equal(0, await f.Monitoring.ReadPendingTitlesAsync(f.Tenant, f.Site, 20));
            var refreshed = await f.Store.ListArticlesAsync(f.Tenant, f.Site, new ArticleListFilter(SourceKind: ArticleSourceKind.CitationDiscovered));
            Assert.Contains(refreshed.Items, item => item.Title == "Read from page");
            Assert.Contains(refreshed.Items, item => item.Title == "https://acme-link.test/blog/incremental");
            var blocked = await f.RowAsync("SELECT r.read_status, r.error_code FROM content_references r WHERE r.tenant_id=UUID_TO_BIN(@tenant) AND r.site_id=UUID_TO_BIN(@site) AND r.url_normalized='https://acme-link.test/blog/incremental';");
            Assert.Equal(["blocked", "SOURCE_BLOCKED"], blocked!);
        }
        finally { await f.CleanupAsync(); }
    }

    [ContentMySqlFact]
    public async Task DailyRoundReservesTenPerBillableLinkSettlesAfterLocalMidnightAndSkipsWhenInsufficient()
    {
        var f = await Fixture.CreateAsync();
        try
        {
            var now = DateTimeOffset.Parse("2026-10-20T03:00:00Z");
            var tracked = await f.ArticleAsync("已追踪两链接", now.AddHours(-3), monitor: "active");
            await f.Store.AddPublicationLinkAsync(f.Tenant, f.Site, tracked, f.User, new ArticlePublicationLinkWrite("https://acme-link.test/a"));
            await f.Store.AddPublicationLinkAsync(f.Tenant, f.Site, tracked, f.User, new ArticlePublicationLinkWrite("https://zhihu.com/p/a"));
            var disabledOn = await f.Store.AddPublicationLinkAsync(f.Tenant, f.Site, tracked, f.User, new ArticlePublicationLinkWrite("https://acme-link.test/gone"));
            await f.Store.DisablePublicationLinkAsync(f.Tenant, f.Site, tracked, disabledOn.LinkId, f.User);
            var paused = await f.ArticleAsync("已暂停", now.AddHours(-3), monitor: "paused");
            await f.Store.AddPublicationLinkAsync(f.Tenant, f.Site, paused, f.User, new ArticlePublicationLinkWrite("https://acme-link.test/paused"));
            var inactive = await f.ArticleAsync("未追踪", now.AddHours(-3));
            await f.Store.AddPublicationLinkAsync(f.Tenant, f.Site, inactive, f.User, new ArticlePublicationLinkWrite("https://acme-link.test/inactive"));
            await f.SetCreditsAsync(available: 25m, reserved: 0m);

            // 2 条可计费链接（停用链接、暂停与未追踪文章不计）= 20 积分，余额 25 足够：先验证正常开轮与幂等。
            Assert.Equal(1, await f.Monitoring.OpenRoundsAsync(now, f.Site));
            Assert.Equal(0, await f.Monitoring.OpenRoundsAsync(now.AddMinutes(5), f.Site));
            var billing = await f.Monitoring.BillingAsync(f.Tenant, f.Site, now);
            Assert.Equal(10m, billing.UnitPrice);
            Assert.Equal(2, billing.BillableLinkCount);
            Assert.Equal(20m, billing.NextEstimate);
            Assert.Equal(5m, billing.AvailableCredits);
            Assert.Equal("held", billing.TodayStatus);
            Assert.Equal(new DateOnly(2026, 10, 20), billing.Today!.LocalDate);
            Assert.Equal((2, 20m, 20m, 0m), (billing.Today.LinkCount, billing.Today.EstimatedCredits, billing.Today.HeldCredits, billing.Today.CapturedCredits));
            var account = await f.RowAsync("SELECT available_credits, reserved_credits, lifetime_captured FROM credit_accounts WHERE tenant_id=UUID_TO_BIN(@tenant);");
            Assert.Equal(["5.000", "20.000", "0.000"], account!);
            var reserve = await f.RowAsync("SELECT amount, external_reference FROM credit_ledger WHERE tenant_id=UUID_TO_BIN(@tenant) AND entry_type='reserve';");
            Assert.Equal("-20.000", reserve![0]);
            Assert.StartsWith("article-monitoring:reserve:", reserve[1]);
            Assert.Equal(2, await f.CountAsync("SELECT COUNT(*) FROM article_monitoring_round_items i JOIN article_monitoring_rounds r ON r.tenant_id=i.tenant_id AND r.id=i.round_id WHERE r.tenant_id=UUID_TO_BIN(@tenant) AND r.site_id=UUID_TO_BIN(@site) AND i.status='held';"));

            // 本地日（UTC）未结束不结算；跨日后结算一次，重跑不再结算。
            Assert.Equal(0, await f.Monitoring.SettleRoundsAsync(DateTimeOffset.Parse("2026-10-20T23:59:00Z"), f.Site));
            Assert.Equal(1, await f.Monitoring.SettleRoundsAsync(DateTimeOffset.Parse("2026-10-21T00:01:00Z"), f.Site));
            Assert.Equal(0, await f.Monitoring.SettleRoundsAsync(DateTimeOffset.Parse("2026-10-21T00:02:00Z"), f.Site));
            account = await f.RowAsync("SELECT available_credits, reserved_credits, lifetime_captured FROM credit_accounts WHERE tenant_id=UUID_TO_BIN(@tenant);");
            Assert.Equal(["5.000", "0.000", "20.000"], account!);
            var capture = await f.RowAsync("SELECT amount, BIN_TO_UUID(article_monitoring_round_id) FROM credit_ledger WHERE tenant_id=UUID_TO_BIN(@tenant) AND entry_type='capture';");
            Assert.Equal("0.000", capture![0]);
            Assert.False(string.IsNullOrWhiteSpace(capture[1]));
            var usage = await f.Credits.UsageAsync(f.Tenant, null, new CreditUsageRequest(new DateOnly(2026, 10, 21), new DateOnly(2026, 10, 21), "UTC"));
            var usageItem = Assert.Single(Assert.Single(usage.Days).Items);
            Assert.Equal((CreditUsageType.ArticleMonitoring, 20m, 1), (usageItem.Type, usageItem.Credits, usageItem.Count));

            // 次日：余额 5 < 20 → 跳过，不写账本；同日重跑不重复；补足余额后再次日正常开轮。
            var nextDay = DateTimeOffset.Parse("2026-10-21T01:00:00Z");
            Assert.Equal(1, await f.Monitoring.OpenRoundsAsync(nextDay, f.Site));
            Assert.Equal(0, await f.Monitoring.OpenRoundsAsync(nextDay.AddHours(1), f.Site));
            var skipped = await f.Monitoring.BillingAsync(f.Tenant, f.Site, nextDay);
            Assert.Equal("skipped", skipped.TodayStatus);
            Assert.Equal("CREDIT_INSUFFICIENT", skipped.Today!.SkipCode);
            Assert.Contains("20", skipped.Today.SkipMessage);
            Assert.Equal(2, skipped.RecentRounds.Count);
            Assert.Equal(1, await f.CountAsync("SELECT COUNT(*) FROM credit_ledger WHERE tenant_id=UUID_TO_BIN(@tenant) AND entry_type='reserve';"));
            await f.SetCreditsAsync(available: 100m, reserved: 0m);
            Assert.Equal(1, await f.Monitoring.OpenRoundsAsync(DateTimeOffset.Parse("2026-10-22T01:00:00Z"), f.Site));
            Assert.Equal(2, await f.CountAsync("SELECT COUNT(*) FROM credit_ledger WHERE tenant_id=UUID_TO_BIN(@tenant) AND entry_type='reserve';"));
            var rounds = await f.Monitoring.BillingAsync(f.Tenant, f.Site, DateTimeOffset.Parse("2026-10-22T01:00:00Z"));
            Assert.Equal(["held", "skipped", "captured"], rounds.RecentRounds.Select(round => round.Status).ToArray());
        }
        finally { await f.CleanupAsync(); }
    }

    private sealed class FakeReader : IArticleSourceReader
    {
        public Dictionary<string, ArticleSourceReadResult> Results { get; } = new(StringComparer.Ordinal);

        public Task<ArticleSourceReadResult> ReadAsync(string url, CancellationToken cancellationToken = default)
            => Task.FromResult(Results.TryGetValue(url, out var result)
                ? result
                : new ArticleSourceReadResult(url, url, "", "failed", null, null, null, [], "SOURCE_UNREADABLE", "网页暂时无法读取。"));
    }

    private sealed class Fixture
    {
        private readonly string connectionString;
        private readonly Guid run = Guid.NewGuid();
        private readonly Guid release = Guid.NewGuid();
        private readonly Guid target = Guid.NewGuid();
        private int sample;

        private Fixture(string connection)
        {
            connectionString = connection;
            var database = new MySqlDatabase(connection);
            Management = new ManagementStore(database);
            Store = new ContentStore(database, Management, null!, null!, null!, null!, null!);
            Credits = new CreditStore(database);
            Reader = new FakeReader();
            Monitoring = new ArticleLinkMonitoringStore(database, Management, Reader, NullLogger<ArticleLinkMonitoringStore>.Instance);
        }

        public Guid Tenant { get; } = Guid.NewGuid();
        public Guid User { get; } = Guid.NewGuid();
        public Guid Owner { get; } = Guid.NewGuid();
        public Guid Site { get; private set; }
        public Guid Prompt { get; private set; }
        public ManagementStore Management { get; }
        public ContentStore Store { get; }
        public CreditStore Credits { get; }
        public FakeReader Reader { get; }
        public ArticleLinkMonitoringStore Monitoring { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var value = Environment.GetEnvironmentVariable("JOGEOAI_CONTENT_TEST_CONNECTION");
            Assert.False(string.IsNullOrWhiteSpace(value), "未设置 JOGEOAI_CONTENT_TEST_CONNECTION；SQL 测试不会静默通过。");
            var builder = new MySqlConnectionStringBuilder(value!);
            Assert.False(string.IsNullOrWhiteSpace(builder.Database), "必须显式指定独立测试库。");
            Assert.NotEqual("jogeoai", builder.Database, StringComparer.OrdinalIgnoreCase);
            var f = new Fixture(value!);
            await f.ExecAsync("""
                INSERT INTO tenants (id, name, status, created_at, updated_at)
                VALUES (UUID_TO_BIN(@tenant), 'link monitoring fixture', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                INSERT INTO auth_users (id, email, password_hash, status, created_at, updated_at)
                VALUES (UUID_TO_BIN(@user), @email, 'not-a-login', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)),
                       (UUID_TO_BIN(@owner), @ownerEmail, 'not-a-login', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                INSERT INTO tenant_members (tenant_id, user_id, role, status, created_at, updated_at)
                VALUES (UUID_TO_BIN(@tenant), UUID_TO_BIN(@owner), 'owner', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)),
                       (UUID_TO_BIN(@tenant), UUID_TO_BIN(@user), 'member', 'active', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                INSERT INTO credit_accounts (tenant_id, available_credits, updated_at) VALUES (UUID_TO_BIN(@tenant), 1000, UTC_TIMESTAMP(6));
                """, ("@tenant", f.Tenant.ToString()), ("@user", f.User.ToString()), ("@owner", f.Owner.ToString()),
                ("@email", $"link-{f.User:N}@example.test"), ("@ownerEmail", $"owner-{f.Owner:N}@example.test"));
            var brand = await f.Management.CreateBrandAsync(f.Tenant, new BrandWrite("Acme Link", "acme-link.test", TimeZone: "UTC", IncludeSubdomains: false, Description: "测试品牌提供设备与服务。"));
            f.Site = brand.SiteId;
            f.Prompt = (await f.Management.CreatePromptAsync(f.Tenant, f.Site, new PromptWrite("管道摄像机怎么选？", "zh-CN"))).PromptId;
            await f.ExecAsync("""
                INSERT INTO visibility_runs (id, tenant_id, site_id, status, idempotency_key, requested_at, total_checks, succeeded_checks, created_at, updated_at)
                VALUES (UUID_TO_BIN(@run), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), 'succeeded', @key, UTC_TIMESTAMP(6), 0, 0, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                INSERT INTO run_engines (id, run_id, engine, provider, target_key, capture_mode, country, locale, created_at)
                VALUES (UUID_TO_BIN(@target), UUID_TO_BIN(@run), 'chatgpt', 'cloro', @targetKey, 'scraped', 'US', 'en-US', UTC_TIMESTAMP(6));
                INSERT INTO analysis_releases (id, tenant_id, site_id, version, subject_config_version, subject_snapshot, extractor_version, metric_version, status, created_at, published_at)
                VALUES (UUID_TO_BIN(@release), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), 1, 1, JSON_OBJECT('brand','Acme Link'), 'test', 'test', 'published', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                """, ("@run", f.run.ToString()), ("@tenant", f.Tenant.ToString()), ("@site", f.Site.ToString()),
                ("@key", Guid.NewGuid().ToString()), ("@release", f.release.ToString()),
                ("@target", f.target.ToString()), ("@targetKey", new string('t', 64)));
            return f;
        }

        /// <summary>插入一条已分析完成的回答，并写入 owned / non_owned 引用事实与到达记录。</summary>
        public async Task<Guid> CheckAsync(DateTimeOffset captured, (string Url, string Ownership, string? Title)[] citations)
        {
            var check = Guid.NewGuid();
            await ExecAsync("""
                INSERT INTO visibility_checks (id, tenant_id, site_id, run_id, prompt_id, engine, provider, status, prompt_snapshot, captured_at, run_target_id, sample_index, created_at, updated_at)
                VALUES (UUID_TO_BIN(@check), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), UUID_TO_BIN(@run), UUID_TO_BIN(@prompt), 'chatgpt', 'cloro', 'succeeded', 'snapshot', @captured, UUID_TO_BIN(@target), @sample, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                INSERT INTO analytics_check_arrivals (check_id, tenant_id, site_id, captured_at, arrived_at)
                VALUES (UUID_TO_BIN(@check), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), @captured, UTC_TIMESTAMP(6));
                INSERT INTO analysis_check_work (release_id, check_id, tenant_id, site_id, status, created_at, updated_at)
                VALUES (UUID_TO_BIN(@release), UUID_TO_BIN(@check), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), 'completed', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                INSERT INTO analysis_events (release_id, check_id, completed_at) VALUES (UUID_TO_BIN(@release), UUID_TO_BIN(@check), UTC_TIMESTAMP(6));
                INSERT INTO check_analysis (release_id, check_id, tenant_id, site_id, status, configuration_missing, completed_event_id, completed_at)
                SELECT UUID_TO_BIN(@release), UUID_TO_BIN(@check), UUID_TO_BIN(@tenant), UUID_TO_BIN(@site), 'completed', FALSE, e.id, UTC_TIMESTAMP(6)
                FROM analysis_events e WHERE e.release_id=UUID_TO_BIN(@release) AND e.check_id=UUID_TO_BIN(@check);
                """, ("@check", check.ToString()), ("@tenant", Tenant.ToString()), ("@site", Site.ToString()), ("@run", run.ToString()),
                ("@prompt", Prompt.ToString()), ("@captured", captured.UtcDateTime), ("@sample", ++sample), ("@release", release.ToString()),
                ("@target", target.ToString()));
            var position = 0;
            foreach (var (url, ownership, title) in citations)
            {
                var normalized = DetailedAnalyticsRules.NormalizeUrl(url)!;
                await ExecAsync("""
                    INSERT INTO citation_events (release_id, check_id, url_hash, normalized_url, domain, title, first_position, url_version, normalization_status)
                    VALUES (UUID_TO_BIN(@release), UUID_TO_BIN(@check), @hash, @url, @domain, @title, @position, 'url-v1', 'complete');
                    INSERT INTO citation_classifications (release_id, check_id, url_hash, source_category, page_type, ownership, subject_id, rule_version, rule_basis)
                    VALUES (UUID_TO_BIN(@release), UUID_TO_BIN(@check), @hash, 'other', 'other', @ownership, NULL, 'test', 'test');
                    """, ("@release", release.ToString()), ("@check", check.ToString()), ("@hash", Hash(normalized)), ("@url", normalized),
                    ("@domain", new Uri(normalized).Host), ("@title", title), ("@position", ++position), ("@ownership", ownership));
            }
            return check;
        }

        public async Task<Guid> ArticleAsync(string title, DateTimeOffset created, string monitor = "inactive")
        {
            var article = Guid.NewGuid();
            await ExecAsync("""
                INSERT INTO content_articles (id,tenant_id,site_id,created_by,source_kind,status,title,language,monitor_status,created_at,updated_at)
                VALUES (UUID_TO_BIN(@article),UUID_TO_BIN(@tenant),UUID_TO_BIN(@site),UUID_TO_BIN(@user),'ai_generated','active',@title,'zh-CN',@monitor,@created,@created);
                """, ("@article", article.ToString()), ("@tenant", Tenant.ToString()), ("@site", Site.ToString()),
                ("@user", User.ToString()), ("@title", title), ("@monitor", monitor), ("@created", created.UtcDateTime));
            return article;
        }

        public Task SetCreditsAsync(decimal available, decimal reserved) => ExecAsync(
            "UPDATE credit_accounts SET available_credits=@available, reserved_credits=@reserved, updated_at=UTC_TIMESTAMP(6) WHERE tenant_id=UUID_TO_BIN(@tenant);",
            ("@available", available), ("@reserved", reserved), ("@tenant", Tenant.ToString()));

        public async Task<string[]?> RowAsync(string sql)
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new MySqlCommand(sql, connection);
            command.Parameters.AddWithValue("@tenant", Tenant.ToString());
            command.Parameters.AddWithValue("@site", Site.ToString());
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;
            var row = new string[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
                row[i] = reader.IsDBNull(i) ? string.Empty : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? string.Empty;
            return row;
        }

        public async Task<long> CountAsync(string sql) => long.Parse((await RowAsync(sql))![0], CultureInfo.InvariantCulture);

        public async Task CleanupAsync()
        {
            foreach (var sql in new[]
                     {
                         "DELETE i FROM article_monitoring_round_items i WHERE i.tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM credit_ledger WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM article_monitoring_rounds WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM content_citation_discovery_state WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM content_publication_links WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM content_references WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM content_monitor_scopes WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM content_articles WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE c FROM citation_classifications c JOIN check_analysis a ON a.release_id=c.release_id AND a.check_id=c.check_id WHERE a.tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE c FROM citation_events c JOIN check_analysis a ON a.release_id=c.release_id AND a.check_id=c.check_id WHERE a.tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM check_analysis WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE e FROM analysis_events e JOIN analysis_check_work w ON w.release_id=e.release_id AND w.check_id=e.check_id WHERE w.tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM analysis_check_work WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM analysis_releases WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM analytics_check_arrivals WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM visibility_checks WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE e FROM run_engines e JOIN visibility_runs r ON r.id=e.run_id WHERE r.tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM visibility_runs WHERE tenant_id=UUID_TO_BIN(@tenant);",
                         "DELETE FROM credit_accounts WHERE tenant_id=UUID_TO_BIN(@tenant);"
                     })
                await ExecAsync(sql, ("@tenant", Tenant.ToString()));
        }

        private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

        private async Task ExecAsync(string sql, params (string Name, object? Value)[] parameters)
        {
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new MySqlCommand(sql, connection);
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            await command.ExecuteNonQueryAsync();
        }
    }
}
