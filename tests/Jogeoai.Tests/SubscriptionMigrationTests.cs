namespace Jogeoai.Tests;

/// <summary>024 套餐结构与种子、031 专业版权益升级的精确迁移合同。</summary>
public sealed class SubscriptionMigrationTests
{
    private static string Load()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../db/migrations/024_subscription_plans.sql"));
        Assert.True(File.Exists(path), $"找不到 migration：{path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void Migration031OnlyAssignsProBrandAndMemberLimitsWithoutChangingOtherState()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../db/migrations/031_pro_plan_brand_member_limits.sql"));
        Assert.True(File.Exists(path), $"找不到 migration：{path}");
        var sql = File.ReadAllText(path);
        var statements = System.Text.RegularExpressions.Regex.Replace(sql, @"--[^\r\n]*", "");
        statements = System.Text.RegularExpressions.Regex.Replace(statements, @"\s+", " ").Trim();

        // 精确单条赋值保证重复执行不累加权益，也不会触及周期、积分、其他套餐或其他列。
        Assert.Equal("UPDATE subscription_plans SET max_brands = 20, max_members = 20 WHERE code = 'pro';", statements);
    }

    [Fact]
    public void Migration024CreatesCatalogSubscriptionEventAndCodeTables()
    {
        var sql = Load();

        foreach (var table in new[] { "subscription_plans", "tenant_subscriptions", "subscription_events", "plan_code_batches", "plan_codes" })
            Assert.Contains($"CREATE TABLE IF NOT EXISTS {table}", sql, StringComparison.Ordinal);

        // 套餐编码固定、二进制排序；一租户一行订阅。
        Assert.Contains("code VARCHAR(32) COLLATE utf8mb4_0900_bin NOT NULL PRIMARY KEY", sql, StringComparison.Ordinal);
        Assert.Contains("UNIQUE KEY uq_tenant_subscriptions_tenant (tenant_id)", sql, StringComparison.Ordinal);
        Assert.Contains("KEY ix_tenant_subscriptions_due (period_ends_at, expires_at)", sql, StringComparison.Ordinal);
        Assert.Contains("FOREIGN KEY (plan_code) REFERENCES subscription_plans (code)", sql, StringComparison.Ordinal);
        Assert.Contains("source IN ('registration','migration','redeem','admin','expiry')", sql, StringComparison.Ordinal);
        Assert.Contains("period_captured_baseline DECIMAL(18,3)", sql, StringComparison.Ordinal);

        // 卡密只存哈希与末 4 位，状态为 CHECK 约束的 VARCHAR。
        Assert.Contains("code_sha256 CHAR(64) COLLATE utf8mb4_0900_bin NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("UNIQUE KEY uq_plan_codes_sha (code_sha256)", sql, StringComparison.Ordinal);
        Assert.Contains("code_hint VARCHAR(8) NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("status IN ('unused','redeemed','revoked')", sql, StringComparison.Ordinal);
        Assert.Contains("months BETWEEN 1 AND 36", sql, StringComparison.Ordinal);
        Assert.Contains("quantity BETWEEN 1 AND 500", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("code_plaintext", sql, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("('registration','migration','redeem','admin_assign','expire_downgrade','period_rollover','plan_updated')", sql, StringComparison.Ordinal);
        Assert.Contains("KEY ix_subscription_events_tenant_created (tenant_id, created_at, id)", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Migration024SeedsFivePlansMatchingTheCatalog()
    {
        var sql = Load();

        Assert.Contains("INSERT IGNORE INTO subscription_plans", sql, StringComparison.Ordinal);
        Assert.Contains("('free_trial', '免费体验版', '新注册默认，体验 GEO 核心能力', 0, FALSE, 1, 1, 20, 2,", sql, StringComparison.Ordinal);
        Assert.Contains("500, 0, 1, 3, TRUE, TRUE, TRUE, FALSE, TRUE", sql, StringComparison.Ordinal);
        Assert.Contains("('starter', '入门版', '中小企业 / 个人品牌首次体验 GEO', 10, FALSE, 1, 3, 100, 2,", sql, StringComparison.Ordinal);
        Assert.Contains("0, 6400, 1, 3, TRUE, TRUE, TRUE, FALSE, TRUE", sql, StringComparison.Ordinal);
        Assert.Contains("('basic', '基础版', '渠道伙伴、代运营机构和多品牌团队', 20, TRUE, 3, 10, NULL, 3,", sql, StringComparison.Ordinal);
        Assert.Contains("0, 42000, 3, 6, TRUE, TRUE, TRUE, FALSE, TRUE", sql, StringComparison.Ordinal);
        Assert.Contains("('standard', '标准版', '成长型企业建设品牌 AEO 全链路', 30, FALSE, 3, 10, NULL, 5,", sql, StringComparison.Ordinal);
        Assert.Contains("0, 350000, 3, 6, TRUE, TRUE, TRUE, TRUE, TRUE", sql, StringComparison.Ordinal);
        Assert.Contains("('pro', '专业版', '大型企业和集团客户全维度 AI 营销', 40, FALSE, 5, 20, NULL, 10,", sql, StringComparison.Ordinal);
        Assert.Contains("0, 1250000, 3, 6, TRUE, TRUE, TRUE, TRUE, TRUE", sql, StringComparison.Ordinal);
        Assert.Equal(5, sql.Split("TRUE, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6))").Length - 1);
    }

    [Fact]
    public void Migration024GuardsLedgerExpireTypeAndBackfillsTenantsWithoutLedgerWrites()
    {
        var sql = Load();

        Assert.Contains("DROP PROCEDURE IF EXISTS jogeoai_024_subscription_plans", sql, StringComparison.Ordinal);
        Assert.Contains("CALL jogeoai_024_subscription_plans();", sql, StringComparison.Ordinal);
        Assert.Contains("information_schema.check_constraints", sql, StringComparison.Ordinal);
        Assert.Contains("constraint_name = 'ck_credit_ledger_type' AND check_clause NOT LIKE '%expire%'", sql, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE credit_ledger DROP CHECK ck_credit_ledger_type", sql, StringComparison.Ordinal);
        Assert.Contains("CHECK (entry_type IN ('grant','reserve','capture','release','reversal','expire'))", sql, StringComparison.Ordinal);

        // 回填：每个没有订阅的租户补一行免费体验版，来源 migration；不碰账本与余额。
        var backfill = sql[sql.IndexOf("INSERT INTO tenant_subscriptions", StringComparison.Ordinal)..];
        Assert.Contains("'free_trial', 'migration', t.created_at, NULL, 0, NULL, NULL,", backfill, StringComparison.Ordinal);
        Assert.Contains("LEFT JOIN tenant_subscriptions s ON s.tenant_id = t.id", backfill, StringComparison.Ordinal);
        Assert.Contains("WHERE s.id IS NULL", backfill, StringComparison.Ordinal);
        Assert.DoesNotContain("credit_ledger", backfill, StringComparison.Ordinal);
        // 只读 credit_accounts 取消耗基线，不改余额；早期按 0 回填的行由幂等 UPDATE 修复。
        Assert.Contains("LEFT JOIN credit_accounts ca ON ca.tenant_id = t.id", backfill, StringComparison.Ordinal);
        Assert.Contains("COALESCE(ca.lifetime_captured, 0)", backfill, StringComparison.Ordinal);
        Assert.Contains("SET s.period_captured_baseline = ca.lifetime_captured", backfill, StringComparison.Ordinal);
        Assert.Contains("WHERE s.source = 'migration' AND s.period_index = 0 AND s.period_captured_baseline = 0 AND ca.lifetime_captured > 0", backfill, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT INTO credit_accounts", sql, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("DROP TABLE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ENUM(", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE FROM", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE credit_accounts", sql, StringComparison.OrdinalIgnoreCase);
    }
}
