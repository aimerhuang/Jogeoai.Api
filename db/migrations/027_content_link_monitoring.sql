-- 027_content_link_monitoring.sql
-- 内容链接监控：AI 引用自动发现的文章来源、发现水位、每日链接监控计费轮次与账本关联。
-- 手动执行于 001–026 之后；应用启动不会执行本迁移。所有变更只新增表、新增列或放宽 CHECK，可重放。

-- 1. 文章来源增加 citation_discovered（大模型回答引用品牌域名链接时自动建文）。
DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_027_prepare_schema//
CREATE PROCEDURE jogeoai_027_prepare_schema()
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.check_constraints WHERE constraint_schema = DATABASE()
        AND constraint_name = 'ck_content_articles_source' AND check_clause NOT LIKE '%citation_discovered%') THEN
        ALTER TABLE content_articles DROP CHECK ck_content_articles_source;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.check_constraints WHERE constraint_schema = DATABASE()
        AND constraint_name = 'ck_content_articles_source') THEN
        ALTER TABLE content_articles ADD CONSTRAINT ck_content_articles_source
            CHECK (source_kind IN ('ai_generated','published_import','citation_discovered'));
    END IF;

    -- 2. 发布链接记录自动发现来源：最早引用该 URL 的回答 Check 与发现时间；手动登记 / 导入的链接为 NULL。
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'content_publication_links' AND column_name = 'discovered_check_id') THEN
        ALTER TABLE content_publication_links ADD COLUMN discovered_check_id BINARY(16) NULL AFTER ownership_match;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'content_publication_links' AND column_name = 'discovered_at') THEN
        ALTER TABLE content_publication_links ADD COLUMN discovered_at DATETIME(6) NULL AFTER discovered_check_id;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE()
        AND table_name = 'content_publication_links' AND index_name = 'ix_content_publication_discovered') THEN
        ALTER TABLE content_publication_links ADD KEY ix_content_publication_discovered (tenant_id, site_id, discovered_check_id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE()
        AND table_name = 'content_publication_links' AND constraint_name = 'fk_content_publication_discovered_check') THEN
        ALTER TABLE content_publication_links ADD CONSTRAINT fk_content_publication_discovered_check
            FOREIGN KEY (tenant_id, site_id, discovered_check_id) REFERENCES visibility_checks (tenant_id, site_id, id);
    END IF;
END//
DELIMITER ;
CALL jogeoai_027_prepare_schema();
DROP PROCEDURE jogeoai_027_prepare_schema;

-- 3. 每品牌一行的发现水位：按 analytics_check_arrivals.id 增量扫描；已发布 release 变化时重置水位并重新回溯；
--    首次回溯窗口起点保留作审计。
CREATE TABLE IF NOT EXISTS content_citation_discovery_state (
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    release_id BINARY(16) NULL,
    last_arrival_id BIGINT UNSIGNED NOT NULL DEFAULT 0,
    backfill_from DATETIME(6) NULL,
    backfill_completed_at DATETIME(6) NULL,
    last_run_at DATETIME(6) NULL,
    updated_at DATETIME(6) NOT NULL,
    PRIMARY KEY (tenant_id, site_id),
    CONSTRAINT fk_content_discovery_site FOREIGN KEY (tenant_id, site_id) REFERENCES sites (tenant_id, id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- 4. 每品牌每本地自然日一轮链接监控计费；与 019 的提问监控预留同形：预占 → capture / release。
CREATE TABLE IF NOT EXISTS article_monitoring_rounds (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    local_date DATE NOT NULL,
    time_zone VARCHAR(100) NOT NULL,
    unit_price DECIMAL(18,3) NOT NULL DEFAULT 10,
    link_count INT UNSIGNED NOT NULL,
    estimated_credits DECIMAL(18,3) NOT NULL,
    held_credits DECIMAL(18,3) NOT NULL DEFAULT 0,
    captured_credits DECIMAL(18,3) NOT NULL DEFAULT 0,
    released_credits DECIMAL(18,3) NOT NULL DEFAULT 0,
    status VARCHAR(16) NOT NULL,
    skip_code VARCHAR(64) NULL,
    skip_message VARCHAR(500) NULL,
    created_at DATETIME(6) NOT NULL,
    settled_at DATETIME(6) NULL,
    UNIQUE KEY uq_article_monitoring_rounds_day (tenant_id, site_id, local_date),
    UNIQUE KEY uq_article_monitoring_rounds_tenant_id (tenant_id, id),
    KEY ix_article_monitoring_rounds_status (status, local_date),
    CONSTRAINT fk_article_monitoring_rounds_account FOREIGN KEY (tenant_id) REFERENCES credit_accounts (tenant_id),
    CONSTRAINT fk_article_monitoring_rounds_site FOREIGN KEY (tenant_id, site_id) REFERENCES sites (tenant_id, id),
    CONSTRAINT ck_article_monitoring_rounds_status CHECK (status IN ('held','captured','released','skipped')),
    CONSTRAINT ck_article_monitoring_rounds_amount CHECK (unit_price > 0 AND estimated_credits >= 0
        AND held_credits >= 0 AND captured_credits >= 0 AND released_credits >= 0)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS article_monitoring_round_items (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    round_id BINARY(16) NOT NULL,
    article_id BINARY(16) NOT NULL,
    publication_link_id BINARY(16) NOT NULL,
    amount DECIMAL(18,3) NOT NULL,
    status VARCHAR(16) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    settled_at DATETIME(6) NULL,
    UNIQUE KEY uq_article_monitoring_items_link (tenant_id, round_id, publication_link_id),
    KEY ix_article_monitoring_items_round_status (tenant_id, round_id, status),
    KEY ix_article_monitoring_items_article (tenant_id, article_id, created_at),
    CONSTRAINT fk_article_monitoring_items_round FOREIGN KEY (tenant_id, round_id)
        REFERENCES article_monitoring_rounds (tenant_id, id),
    CONSTRAINT fk_article_monitoring_items_link FOREIGN KEY (tenant_id, site_id, article_id, publication_link_id)
        REFERENCES content_publication_links (tenant_id, site_id, article_id, id),
    CONSTRAINT ck_article_monitoring_items_status CHECK (status IN ('held','captured','released')),
    CONSTRAINT ck_article_monitoring_items_amount CHECK (amount > 0)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- 5. 账本关联轮次；entry_type 不新增（reserve / capture / release 复用）。
DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_027_ledger//
CREATE PROCEDURE jogeoai_027_ledger()
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'credit_ledger' AND column_name = 'article_monitoring_round_id') THEN
        ALTER TABLE credit_ledger ADD COLUMN article_monitoring_round_id BINARY(16) NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE()
        AND table_name = 'credit_ledger' AND index_name = 'ix_credit_ledger_article_round') THEN
        ALTER TABLE credit_ledger ADD KEY ix_credit_ledger_article_round (tenant_id, article_monitoring_round_id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE()
        AND table_name = 'credit_ledger' AND constraint_name = 'fk_credit_ledger_article_round') THEN
        ALTER TABLE credit_ledger ADD CONSTRAINT fk_credit_ledger_article_round
            FOREIGN KEY (tenant_id, article_monitoring_round_id) REFERENCES article_monitoring_rounds (tenant_id, id);
    END IF;
END//
DELIMITER ;
CALL jogeoai_027_ledger();
DROP PROCEDURE jogeoai_027_ledger;
