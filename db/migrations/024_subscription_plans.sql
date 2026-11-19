-- 024_subscription_plans.sql
-- 套餐目录、租户订阅、订阅事件、卡密批次与卡密。
-- 手动执行于 001–023 之后；API / Worker 启动不会执行本文件。只做加法，可重复执行。
-- 套餐编码固定为种子中的 5 个；管理员只能改上限、额度、文案与展示属性，不能新增 / 删除编码。
-- 既有租户回填为免费体验版（source='migration'），不写账本、不改 credit_accounts 余额。
-- credit_ledger.entry_type 新增 'expire'（周期结束时清零可用积分），只有新代码会写入该类型。

CREATE TABLE IF NOT EXISTS subscription_plans (
    code VARCHAR(32) COLLATE utf8mb4_0900_bin NOT NULL PRIMARY KEY,
    name VARCHAR(64) NOT NULL,
    tagline VARCHAR(500) NOT NULL DEFAULT '',
    sort_order INT NOT NULL,
    recommended BOOLEAN NOT NULL DEFAULT FALSE,
    max_brands INT UNSIGNED NULL COMMENT 'NULL = 不限',
    max_members INT UNSIGNED NULL COMMENT 'NULL = 不限；含所有者，计 active 与 invited',
    max_prompts INT UNSIGNED NULL COMMENT 'NULL = 不限；租户内启用且未归档且品牌 active 的提问总数',
    max_competitors_per_brand INT UNSIGNED NULL COMMENT 'NULL = 不限；按品牌计数',
    signup_credits DECIMAL(18,3) NOT NULL DEFAULT 0 COMMENT '注册 / 开通一次性赠送',
    monthly_credits DECIMAL(18,3) NOT NULL DEFAULT 0 COMMENT '每月额度；0 = 无周期',
    daily_monitoring_limit INT UNSIGNED NOT NULL DEFAULT 1 COMMENT '仅展示，不拦截',
    engine_count INT UNSIGNED NOT NULL DEFAULT 3 COMMENT '仅展示，不拦截',
    feature_domain_analysis BOOLEAN NOT NULL DEFAULT TRUE,
    feature_prompt_recommendation BOOLEAN NOT NULL DEFAULT TRUE,
    feature_article_recommendation BOOLEAN NOT NULL DEFAULT TRUE,
    feature_geo_agent BOOLEAN NOT NULL DEFAULT FALSE COMMENT '仅展示，不拦截',
    enabled BOOLEAN NOT NULL DEFAULT TRUE,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    CONSTRAINT ck_subscription_plans_credits CHECK (signup_credits >= 0 AND monthly_credits >= 0),
    CONSTRAINT ck_subscription_plans_name CHECK (CHAR_LENGTH(TRIM(name)) > 0)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- 种子与父任务 PRD §3 一致；INSERT IGNORE 保证重放不会覆盖管理员之后的修改。
INSERT IGNORE INTO subscription_plans
    (code, name, tagline, sort_order, recommended, max_brands, max_members, max_prompts, max_competitors_per_brand,
     signup_credits, monthly_credits, daily_monitoring_limit, engine_count,
     feature_domain_analysis, feature_prompt_recommendation, feature_article_recommendation, feature_geo_agent,
     enabled, created_at, updated_at)
VALUES
    ('free_trial', '免费体验版', '新注册默认，体验 GEO 核心能力', 0, FALSE, 1, 1, 20, 2,
     500, 0, 1, 3, TRUE, TRUE, TRUE, FALSE, TRUE, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)),
    ('starter', '入门版', '中小企业 / 个人品牌首次体验 GEO', 10, FALSE, 1, 3, 100, 2,
     0, 6400, 1, 3, TRUE, TRUE, TRUE, FALSE, TRUE, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)),
    ('basic', '基础版', '渠道伙伴、代运营机构和多品牌团队', 20, TRUE, 3, 10, NULL, 3,
     0, 42000, 3, 6, TRUE, TRUE, TRUE, FALSE, TRUE, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)),
    ('standard', '标准版', '成长型企业建设品牌 AEO 全链路', 30, FALSE, 3, 10, NULL, 5,
     0, 350000, 3, 6, TRUE, TRUE, TRUE, TRUE, TRUE, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)),
    ('pro', '专业版', '大型企业和集团客户全维度 AI 营销', 40, FALSE, 5, 20, NULL, 10,
     0, 1250000, 3, 6, TRUE, TRUE, TRUE, TRUE, TRUE, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));

-- 每租户一行；变更原地更新，历史靠 subscription_events。
CREATE TABLE IF NOT EXISTS tenant_subscriptions (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    plan_code VARCHAR(32) COLLATE utf8mb4_0900_bin NOT NULL,
    source VARCHAR(16) NOT NULL COMMENT 'registration|migration|redeem|admin|expiry',
    started_at DATETIME(6) NOT NULL,
    expires_at DATETIME(6) NULL COMMENT '免费体验版为 NULL',
    period_index INT UNSIGNED NOT NULL DEFAULT 0 COMMENT '0 = 无周期',
    period_started_at DATETIME(6) NULL,
    period_ends_at DATETIME(6) NULL,
    period_quota DECIMAL(18,3) NOT NULL DEFAULT 0,
    period_captured_baseline DECIMAL(18,3) NOT NULL DEFAULT 0 COMMENT '周期开始时的 credit_accounts.lifetime_captured',
    version BIGINT UNSIGNED NOT NULL DEFAULT 0,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_tenant_subscriptions_tenant (tenant_id),
    KEY ix_tenant_subscriptions_due (period_ends_at, expires_at),
    KEY ix_tenant_subscriptions_expiry (expires_at, plan_code),
    CONSTRAINT fk_tenant_subscriptions_tenant FOREIGN KEY (tenant_id) REFERENCES tenants (id),
    CONSTRAINT fk_tenant_subscriptions_plan FOREIGN KEY (plan_code) REFERENCES subscription_plans (code),
    CONSTRAINT ck_tenant_subscriptions_source CHECK (source IN ('registration','migration','redeem','admin','expiry')),
    CONSTRAINT ck_tenant_subscriptions_period CHECK (period_quota >= 0 AND period_captured_baseline >= 0)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS plan_code_batches (
    id BINARY(16) NOT NULL PRIMARY KEY,
    plan_code VARCHAR(32) COLLATE utf8mb4_0900_bin NOT NULL,
    months INT UNSIGNED NOT NULL,
    quantity INT UNSIGNED NOT NULL,
    expires_at DATETIME(6) NULL COMMENT '卡密兑换截止；NULL = 不限',
    note VARCHAR(200) NULL,
    created_by BINARY(16) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    KEY ix_plan_code_batches_created (created_at, id),
    CONSTRAINT fk_plan_code_batches_plan FOREIGN KEY (plan_code) REFERENCES subscription_plans (code),
    CONSTRAINT fk_plan_code_batches_creator FOREIGN KEY (created_by) REFERENCES auth_users (id),
    CONSTRAINT ck_plan_code_batches_months CHECK (months BETWEEN 1 AND 36),
    CONSTRAINT ck_plan_code_batches_quantity CHECK (quantity BETWEEN 1 AND 500)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- 只存 SHA-256 与末 4 位；明文只在生成响应中出现一次。
-- 「已过期」是派生状态：status='unused' AND expires_at < now。
CREATE TABLE IF NOT EXISTS plan_codes (
    id BINARY(16) NOT NULL PRIMARY KEY,
    batch_id BINARY(16) NOT NULL,
    plan_code VARCHAR(32) COLLATE utf8mb4_0900_bin NOT NULL,
    months INT UNSIGNED NOT NULL,
    code_sha256 CHAR(64) COLLATE utf8mb4_0900_bin NOT NULL,
    code_hint VARCHAR(8) NOT NULL COMMENT '末 4 位',
    status VARCHAR(16) NOT NULL DEFAULT 'unused',
    expires_at DATETIME(6) NULL,
    redeemed_tenant_id BINARY(16) NULL,
    redeemed_by BINARY(16) NULL,
    redeemed_at DATETIME(6) NULL,
    revoked_at DATETIME(6) NULL,
    created_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_plan_codes_sha (code_sha256),
    KEY ix_plan_codes_batch_status (batch_id, status),
    KEY ix_plan_codes_tenant (redeemed_tenant_id),
    KEY ix_plan_codes_created (created_at, id),
    CONSTRAINT fk_plan_codes_batch FOREIGN KEY (batch_id) REFERENCES plan_code_batches (id),
    CONSTRAINT fk_plan_codes_plan FOREIGN KEY (plan_code) REFERENCES subscription_plans (code),
    CONSTRAINT fk_plan_codes_tenant FOREIGN KEY (redeemed_tenant_id) REFERENCES tenants (id),
    CONSTRAINT fk_plan_codes_user FOREIGN KEY (redeemed_by) REFERENCES auth_users (id),
    CONSTRAINT ck_plan_codes_status CHECK (status IN ('unused','redeemed','revoked'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- 只追加的订阅事件；不保存卡密明文。
CREATE TABLE IF NOT EXISTS subscription_events (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    subscription_id BINARY(16) NOT NULL,
    event_type VARCHAR(32) NOT NULL,
    from_plan_code VARCHAR(32) COLLATE utf8mb4_0900_bin NULL,
    to_plan_code VARCHAR(32) COLLATE utf8mb4_0900_bin NULL,
    actor_user_id BINARY(16) NULL,
    plan_code_id BINARY(16) NULL,
    period_index INT UNSIGNED NULL,
    credits_expired DECIMAL(18,3) NULL,
    credits_granted DECIMAL(18,3) NULL,
    reason VARCHAR(500) NULL,
    created_at DATETIME(6) NOT NULL,
    KEY ix_subscription_events_tenant_created (tenant_id, created_at, id),
    CONSTRAINT fk_subscription_events_tenant FOREIGN KEY (tenant_id) REFERENCES tenants (id),
    CONSTRAINT fk_subscription_events_subscription FOREIGN KEY (subscription_id) REFERENCES tenant_subscriptions (id),
    CONSTRAINT fk_subscription_events_plan_code FOREIGN KEY (plan_code_id) REFERENCES plan_codes (id),
    CONSTRAINT ck_subscription_events_type CHECK (event_type IN
        ('registration','migration','redeem','admin_assign','expire_downgrade','period_rollover','plan_updated'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- 账本类型增加 'expire'。只有当现有约束不含 expire 时才重建；没有该约束时直接补上。
DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_024_subscription_plans//
CREATE PROCEDURE jogeoai_024_subscription_plans()
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.check_constraints WHERE constraint_schema = DATABASE()
        AND constraint_name = 'ck_credit_ledger_type' AND check_clause NOT LIKE '%expire%') THEN
        ALTER TABLE credit_ledger DROP CHECK ck_credit_ledger_type;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.check_constraints WHERE constraint_schema = DATABASE()
        AND constraint_name = 'ck_credit_ledger_type') THEN
        ALTER TABLE credit_ledger ADD CONSTRAINT ck_credit_ledger_type
            CHECK (entry_type IN ('grant','reserve','capture','release','reversal','expire'));
    END IF;
END//
DELIMITER ;
CALL jogeoai_024_subscription_plans();
DROP PROCEDURE jogeoai_024_subscription_plans;

-- 既有租户回填免费体验版；不写账本、不动余额。重放时 LEFT JOIN 保证已回填的租户不会再插入。
INSERT INTO tenant_subscriptions
    (id, tenant_id, plan_code, source, started_at, expires_at, period_index, period_started_at, period_ends_at,
     period_quota, period_captured_baseline, version, created_at, updated_at)
SELECT UUID_TO_BIN(UUID()), t.id, 'free_trial', 'migration', t.created_at, NULL, 0, NULL, NULL,
       0, COALESCE(ca.lifetime_captured, 0), 0, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)
FROM tenants t
LEFT JOIN tenant_subscriptions s ON s.tenant_id = t.id
LEFT JOIN credit_accounts ca ON ca.tenant_id = t.id
WHERE s.id IS NULL;

-- 回填行的消耗基线 = 回填时的累计结算，免费体验版「已用」从此起算；修复早期版本按 0 回填的行（重放安全）。
UPDATE tenant_subscriptions s
JOIN credit_accounts ca ON ca.tenant_id = s.tenant_id
SET s.period_captured_baseline = ca.lifetime_captured, s.updated_at = UTC_TIMESTAMP(6)
WHERE s.source = 'migration' AND s.period_index = 0 AND s.period_captured_baseline = 0 AND ca.lifetime_captured > 0;

-- 为回填的订阅补一条 migration 事件，方便管理员页面区分来源；重放时按订阅去重。
INSERT INTO subscription_events
    (id, tenant_id, subscription_id, event_type, from_plan_code, to_plan_code, actor_user_id, plan_code_id,
     period_index, credits_expired, credits_granted, reason, created_at)
SELECT UUID_TO_BIN(UUID()), s.tenant_id, s.id, 'migration', NULL, 'free_trial', NULL, NULL,
       NULL, NULL, NULL, '024 迁移回填免费体验版', UTC_TIMESTAMP(6)
FROM tenant_subscriptions s
WHERE s.source = 'migration'
  AND NOT EXISTS (SELECT 1 FROM subscription_events e WHERE e.subscription_id = s.id AND e.event_type = 'migration');
