-- 内容管理：文章、生成任务、发布链接、RAG 选择快照和积分账本。
-- 手动执行于 001–013 之后；应用启动不会执行本迁移。所有变更只新增表。

CREATE TABLE IF NOT EXISTS content_articles (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    created_by BINARY(16) NOT NULL,
    source_kind VARCHAR(32) NOT NULL,
    status VARCHAR(32) NOT NULL DEFAULT 'active',
    title VARCHAR(500) NOT NULL,
    language VARCHAR(16) NOT NULL DEFAULT 'zh-CN',
    monitor_status VARCHAR(32) NOT NULL DEFAULT 'inactive',
    current_version_id BINARY(16) NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    archived_at DATETIME(6) NULL,
    UNIQUE KEY uq_content_articles_tenant_id (tenant_id, id),
    UNIQUE KEY uq_content_articles_site_id (site_id, id),
    UNIQUE KEY uq_content_articles_tenant_site_id (tenant_id, site_id, id),
    KEY ix_content_articles_site_status (tenant_id, site_id, status, updated_at, id),
    KEY ix_content_articles_site_source (tenant_id, site_id, source_kind, created_at, id),
    CONSTRAINT fk_content_articles_tenant_site FOREIGN KEY (tenant_id, site_id) REFERENCES sites (tenant_id, id),
    CONSTRAINT fk_content_articles_creator FOREIGN KEY (created_by) REFERENCES auth_users (id),
    CONSTRAINT ck_content_articles_source CHECK (source_kind IN ('ai_generated','published_import')),
    CONSTRAINT ck_content_articles_status CHECK (status IN ('active','archived')),
    CONSTRAINT ck_content_articles_monitor CHECK (monitor_status IN ('inactive','active','paused')),
    CONSTRAINT ck_content_articles_title CHECK (CHAR_LENGTH(TRIM(title)) BETWEEN 1 AND 500),
    CONSTRAINT ck_content_articles_language CHECK (CHAR_LENGTH(TRIM(language)) BETWEEN 2 AND 16)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS content_article_versions (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    article_id BINARY(16) NOT NULL,
    version_no INT UNSIGNED NOT NULL,
    version_kind VARCHAR(32) NOT NULL,
    title VARCHAR(500) NOT NULL,
    summary VARCHAR(2000) NULL,
    body_markdown MEDIUMTEXT NOT NULL,
    seo_title VARCHAR(300) NULL,
    seo_description VARCHAR(1000) NULL,
    keywords_json JSON NULL,
    content_length INT UNSIGNED NOT NULL,
    language VARCHAR(16) NOT NULL,
    generation_batch_id BINARY(16) NULL,
    prompt_snapshot_id BINARY(16) NULL,
    knowledge_context_id BINARY(16) NULL,
    provider VARCHAR(64) NULL,
    model VARCHAR(128) NULL,
    strategy_key VARCHAR(64) NULL,
    strategy_version VARCHAR(32) NULL,
    without_rag BOOLEAN NOT NULL DEFAULT FALSE,
    evidence_ids_json JSON NULL,
    uncertainties_json JSON NULL,
    disclosure VARCHAR(2000) NULL,
    source_hash CHAR(64) COLLATE utf8mb4_0900_bin NULL,
    created_by BINARY(16) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_content_article_versions_no (tenant_id, article_id, version_no),
    UNIQUE KEY uq_content_article_versions_tenant_id (tenant_id, id),
    UNIQUE KEY uq_content_article_versions_tenant_article_id (tenant_id, article_id, id),
    UNIQUE KEY uq_content_article_versions_tenant_site_article_id (tenant_id, site_id, article_id, id),
    KEY ix_content_article_versions_article_created (tenant_id, article_id, created_at),
    CONSTRAINT fk_content_article_versions_article FOREIGN KEY (tenant_id, article_id)
        REFERENCES content_articles (tenant_id, id),
    CONSTRAINT fk_content_article_versions_tenant_site_article FOREIGN KEY (tenant_id, site_id, article_id)
        REFERENCES content_articles (tenant_id, site_id, id),
    CONSTRAINT fk_content_article_versions_site FOREIGN KEY (tenant_id, site_id)
        REFERENCES sites (tenant_id, id),
    CONSTRAINT fk_content_article_versions_creator FOREIGN KEY (created_by) REFERENCES auth_users (id),
    CONSTRAINT ck_content_article_versions_kind CHECK (version_kind IN ('generated','manual_edit','imported')),
    CONSTRAINT ck_content_article_versions_body CHECK (CHAR_LENGTH(body_markdown) BETWEEN 1 AND 16777215),
    CONSTRAINT ck_content_article_versions_json CHECK (
        (keywords_json IS NULL OR OCTET_LENGTH(keywords_json) <= 65536) AND
        (evidence_ids_json IS NULL OR OCTET_LENGTH(evidence_ids_json) <= 65536) AND
        (uncertainties_json IS NULL OR OCTET_LENGTH(uncertainties_json) <= 65536))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_014_content_current_version//
CREATE PROCEDURE jogeoai_014_content_current_version()
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE()
        AND table_name = 'content_articles' AND index_name = 'uq_content_articles_current_version') THEN
        ALTER TABLE content_articles ADD UNIQUE KEY uq_content_articles_current_version (tenant_id, id, current_version_id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE()
        AND table_name = 'content_articles' AND constraint_name = 'fk_content_articles_current_version') THEN
        ALTER TABLE content_articles ADD CONSTRAINT fk_content_articles_current_version
            FOREIGN KEY (tenant_id, id, current_version_id)
            REFERENCES content_article_versions (tenant_id, article_id, id);
    END IF;
END//
DELIMITER ;
CALL jogeoai_014_content_current_version();
DROP PROCEDURE jogeoai_014_content_current_version;

CREATE TABLE IF NOT EXISTS content_article_tags (
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    article_id BINARY(16) NOT NULL,
    tag_id BINARY(16) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    PRIMARY KEY (tenant_id, article_id, tag_id),
    CONSTRAINT fk_content_article_tags_article FOREIGN KEY (tenant_id, article_id)
        REFERENCES content_articles (tenant_id, id),
    CONSTRAINT fk_content_article_tags_tenant_site_article FOREIGN KEY (tenant_id, site_id, article_id)
        REFERENCES content_articles (tenant_id, site_id, id),
    CONSTRAINT fk_content_article_tags_site FOREIGN KEY (tenant_id, site_id)
        REFERENCES sites (tenant_id, id),
    CONSTRAINT fk_content_article_tags_tag FOREIGN KEY (site_id, tag_id)
        REFERENCES tags (site_id, id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS content_references (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    url_original VARCHAR(2000) NOT NULL,
    url_normalized VARCHAR(750) COLLATE utf8mb4_0900_bin NOT NULL,
    url_hash CHAR(64) COLLATE utf8mb4_0900_bin NOT NULL,
    kind VARCHAR(32) NOT NULL,
    read_status VARCHAR(32) NOT NULL DEFAULT 'pending',
    title VARCHAR(500) NULL,
    content_object_key VARCHAR(512) COLLATE utf8mb4_0900_bin NULL,
    content_hash CHAR(64) COLLATE utf8mb4_0900_bin NULL,
    redirect_chain_json JSON NULL,
    reader_version VARCHAR(32) NULL,
    last_checked_at DATETIME(6) NULL,
    error_code VARCHAR(64) NULL,
    error_message VARCHAR(1000) NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_content_references_site_kind_hash (tenant_id, site_id, kind, url_hash),
    UNIQUE KEY uq_content_references_tenant_id (tenant_id, id),
    KEY ix_content_references_site_status (tenant_id, site_id, kind, read_status),
    CONSTRAINT fk_content_references_site FOREIGN KEY (tenant_id, site_id) REFERENCES sites (tenant_id, id),
    CONSTRAINT ck_content_references_kind CHECK (kind IN ('reference','publication')),
    CONSTRAINT ck_content_references_status CHECK (read_status IN ('pending','ready','blocked','unreadable','failed')),
    CONSTRAINT ck_content_references_redirect_size CHECK (redirect_chain_json IS NULL OR OCTET_LENGTH(redirect_chain_json) <= 65536)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS content_article_references (
    tenant_id BINARY(16) NOT NULL,
    article_version_id BINARY(16) NOT NULL,
    reference_id BINARY(16) NOT NULL,
    role VARCHAR(32) NOT NULL,
    sort_order SMALLINT UNSIGNED NOT NULL DEFAULT 0,
    PRIMARY KEY (tenant_id, article_version_id, reference_id),
    CONSTRAINT fk_content_article_references_version FOREIGN KEY (tenant_id, article_version_id)
        REFERENCES content_article_versions (tenant_id, id),
    CONSTRAINT fk_content_article_references_reference FOREIGN KEY (tenant_id, reference_id)
        REFERENCES content_references (tenant_id, id),
    CONSTRAINT ck_content_article_references_role CHECK (role IN ('style','fact','publication'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS content_publication_links (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    article_id BINARY(16) NOT NULL,
    article_version_id BINARY(16) NULL,
    reference_id BINARY(16) NOT NULL,
    channel VARCHAR(64) NULL,
    published_at DATETIME(6) NULL,
    status VARCHAR(32) NOT NULL DEFAULT 'active',
    ownership_match BOOLEAN NULL,
    created_by BINARY(16) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    disabled_at DATETIME(6) NULL,
    UNIQUE KEY uq_content_publication_url (tenant_id, site_id, reference_id),
    UNIQUE KEY uq_content_publication_tenant_site_article_id (tenant_id, site_id, article_id, id),
    KEY ix_content_publication_article_status (tenant_id, article_id, status, created_at),
    CONSTRAINT fk_content_publication_article FOREIGN KEY (tenant_id, article_id)
        REFERENCES content_articles (tenant_id, id),
    CONSTRAINT fk_content_publication_tenant_site_article FOREIGN KEY (tenant_id, site_id, article_id)
        REFERENCES content_articles (tenant_id, site_id, id),
    CONSTRAINT fk_content_publication_version FOREIGN KEY (tenant_id, article_id, article_version_id)
        REFERENCES content_article_versions (tenant_id, article_id, id),
    CONSTRAINT fk_content_publication_tenant_site_version FOREIGN KEY (tenant_id, site_id, article_id, article_version_id)
        REFERENCES content_article_versions (tenant_id, site_id, article_id, id),
    CONSTRAINT fk_content_publication_site FOREIGN KEY (tenant_id, site_id)
        REFERENCES sites (tenant_id, id),
    CONSTRAINT fk_content_publication_reference FOREIGN KEY (tenant_id, reference_id)
        REFERENCES content_references (tenant_id, id),
    CONSTRAINT fk_content_publication_creator FOREIGN KEY (created_by) REFERENCES auth_users (id),
    CONSTRAINT ck_content_publication_status CHECK (status IN ('active','disabled'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS article_material_previews (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    created_by BINARY(16) NOT NULL,
    request_hash CHAR(64) COLLATE utf8mb4_0900_bin NOT NULL,
    selection_json JSON NOT NULL,
    response_json JSON NOT NULL,
    context_id BINARY(16) NULL,
    context_snapshot_hash CHAR(64) COLLATE utf8mb4_0900_bin NULL,
    without_rag BOOLEAN NOT NULL DEFAULT FALSE,
    expires_at DATETIME(6) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_article_material_previews_hash (tenant_id, id, request_hash),
    UNIQUE KEY uq_article_material_previews_tenant_id (tenant_id, id),
    UNIQUE KEY uq_article_material_previews_tenant_site_id (tenant_id, site_id, id),
    KEY ix_article_material_previews_expiry (tenant_id, site_id, expires_at),
    CONSTRAINT fk_article_material_previews_site FOREIGN KEY (tenant_id, site_id) REFERENCES sites (tenant_id, id),
    CONSTRAINT fk_article_material_previews_creator FOREIGN KEY (created_by) REFERENCES auth_users (id),
    CONSTRAINT ck_article_material_previews_size CHECK (OCTET_LENGTH(selection_json) <= 131072 AND OCTET_LENGTH(response_json) <= 4194304)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS article_generation_previews (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    created_by BINARY(16) NOT NULL,
    request_hash CHAR(64) COLLATE utf8mb4_0900_bin NOT NULL,
    request_json JSON NOT NULL,
    response_json JSON NOT NULL,
    material_preview_id BINARY(16) NULL,
    context_id BINARY(16) NULL,
    context_snapshot_hash CHAR(64) COLLATE utf8mb4_0900_bin NULL,
    reference_snapshot JSON NULL,
    expires_at DATETIME(6) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_article_generation_previews_hash (tenant_id, request_hash),
    UNIQUE KEY uq_article_generation_previews_tenant_site_id (tenant_id, site_id, id),
    CONSTRAINT fk_article_generation_previews_site FOREIGN KEY (tenant_id, site_id) REFERENCES sites (tenant_id, id),
    CONSTRAINT fk_article_generation_previews_creator FOREIGN KEY (created_by) REFERENCES auth_users (id),
    CONSTRAINT fk_article_generation_previews_material FOREIGN KEY (tenant_id, material_preview_id)
        REFERENCES article_material_previews (tenant_id, id),
    CONSTRAINT fk_article_generation_previews_site_material FOREIGN KEY (tenant_id, site_id, material_preview_id)
        REFERENCES article_material_previews (tenant_id, site_id, id),
    CONSTRAINT ck_article_generation_previews_size CHECK (OCTET_LENGTH(request_json) <= 131072 AND OCTET_LENGTH(response_json) <= 262144 AND (reference_snapshot IS NULL OR OCTET_LENGTH(reference_snapshot) <= 4194304))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS credit_accounts (
    tenant_id BINARY(16) NOT NULL PRIMARY KEY,
    available_credits DECIMAL(18,3) NOT NULL DEFAULT 0,
    reserved_credits DECIMAL(18,3) NOT NULL DEFAULT 0,
    lifetime_granted DECIMAL(18,3) NOT NULL DEFAULT 0,
    lifetime_captured DECIMAL(18,3) NOT NULL DEFAULT 0,
    version BIGINT UNSIGNED NOT NULL DEFAULT 0,
    updated_at DATETIME(6) NOT NULL,
    CONSTRAINT fk_credit_accounts_tenant FOREIGN KEY (tenant_id) REFERENCES tenants (id),
    CONSTRAINT ck_credit_accounts_nonnegative CHECK (available_credits >= 0 AND reserved_credits >= 0
        AND lifetime_granted >= 0 AND lifetime_captured >= 0)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS article_generation_batches (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    requested_by BINARY(16) NOT NULL,
    idempotency_key VARCHAR(191) COLLATE utf8mb4_0900_bin NOT NULL,
    request_hash CHAR(64) COLLATE utf8mb4_0900_bin NOT NULL,
    status VARCHAR(32) NOT NULL DEFAULT 'queued',
    total_tasks INT UNSIGNED NOT NULL,
    succeeded_tasks INT UNSIGNED NOT NULL DEFAULT 0,
    failed_tasks INT UNSIGNED NOT NULL DEFAULT 0,
    reserved_credits DECIMAL(18,3) NOT NULL DEFAULT 0,
    captured_credits DECIMAL(18,3) NOT NULL DEFAULT 0,
    request_snapshot JSON NOT NULL,
    preview_expires_at DATETIME(6) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    completed_at DATETIME(6) NULL,
    UNIQUE KEY uq_article_generation_batches_idempotency (tenant_id, idempotency_key),
    UNIQUE KEY uq_article_generation_batches_request (tenant_id, request_hash),
    UNIQUE KEY uq_article_generation_batches_tenant_id (tenant_id, id),
    UNIQUE KEY uq_article_generation_batches_tenant_site_id (tenant_id, site_id, id),
    KEY ix_article_generation_batches_status (tenant_id, site_id, status, created_at),
    CONSTRAINT fk_article_generation_batches_tenant_site FOREIGN KEY (tenant_id, site_id) REFERENCES sites (tenant_id, id),
    CONSTRAINT fk_article_generation_batches_user FOREIGN KEY (requested_by) REFERENCES auth_users (id),
    CONSTRAINT ck_article_generation_batches_status CHECK (status IN ('queued','running','succeeded','partial','failed','cancelled')),
    CONSTRAINT ck_article_generation_batches_snapshot CHECK (OCTET_LENGTH(request_snapshot) <= 524288)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS article_generation_tasks (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    batch_id BINARY(16) NOT NULL,
    article_id BINARY(16) NOT NULL,
    prompt_id BINARY(16) NULL,
    prompt_version_id BINARY(16) NULL,
    prompt_snapshot JSON NOT NULL,
    target_snapshot JSON NOT NULL,
    knowledge_selection_snapshot JSON NULL,
    context_snapshot JSON NULL,
    reference_snapshot JSON NULL,
    material_notes VARCHAR(4000) NULL,
    context_id BINARY(16) NULL,
    context_snapshot_hash CHAR(64) COLLATE utf8mb4_0900_bin NULL,
    language VARCHAR(16) NOT NULL,
    strategy_key VARCHAR(64) NOT NULL,
    length_profile VARCHAR(32) NOT NULL,
    style VARCHAR(32) NOT NULL DEFAULT 'professional',
    audience VARCHAR(400) NULL,
    keywords_json JSON NULL,
    extra_requirements VARCHAR(4000) NULL,
    without_rag BOOLEAN NOT NULL DEFAULT FALSE,
    status VARCHAR(32) NOT NULL DEFAULT 'pending',
    attempt_count INT UNSIGNED NOT NULL DEFAULT 0,
    next_attempt_at DATETIME(6) NOT NULL,
    lease_owner VARCHAR(128) NULL,
    lease_until DATETIME(6) NULL,
    provider_request_hash CHAR(64) COLLATE utf8mb4_0900_bin NULL,
    last_error_code VARCHAR(64) NULL,
    last_error_message VARCHAR(1000) NULL,
    reservation_id BINARY(16) NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_article_generation_task_prompt (tenant_id, batch_id, prompt_id),
    UNIQUE KEY uq_article_generation_task_id (tenant_id, id),
    KEY ix_article_generation_tasks_claim (status, next_attempt_at, lease_until, id),
    KEY ix_article_generation_tasks_batch (tenant_id, batch_id, status),
    CONSTRAINT fk_article_generation_tasks_batch FOREIGN KEY (tenant_id, batch_id)
        REFERENCES article_generation_batches (tenant_id, id),
    CONSTRAINT fk_article_generation_tasks_site_batch FOREIGN KEY (tenant_id, site_id, batch_id)
        REFERENCES article_generation_batches (tenant_id, site_id, id),
    CONSTRAINT fk_article_generation_tasks_article FOREIGN KEY (tenant_id, article_id)
        REFERENCES content_articles (tenant_id, id),
    CONSTRAINT fk_article_generation_tasks_site_article FOREIGN KEY (tenant_id, site_id, article_id)
        REFERENCES content_articles (tenant_id, site_id, id),
    CONSTRAINT fk_article_generation_tasks_site FOREIGN KEY (tenant_id, site_id) REFERENCES sites (tenant_id, id),
    CONSTRAINT ck_article_generation_tasks_status CHECK (status IN ('pending','running','succeeded','failed','cancelled','stale')),
    CONSTRAINT ck_article_generation_tasks_style CHECK (style IN ('professional','plain_explainer','purchase_decision')),
    CONSTRAINT ck_article_generation_tasks_json CHECK (
        OCTET_LENGTH(prompt_snapshot) <= 65536 AND OCTET_LENGTH(target_snapshot) <= 65536 AND
        (knowledge_selection_snapshot IS NULL OR OCTET_LENGTH(knowledge_selection_snapshot) <= 131072) AND
        (context_snapshot IS NULL OR OCTET_LENGTH(context_snapshot) <= 4194304) AND
        (reference_snapshot IS NULL OR OCTET_LENGTH(reference_snapshot) <= 131072))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS credit_reservations (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    batch_id BINARY(16) NOT NULL,
    task_id BINARY(16) NOT NULL,
    amount DECIMAL(18,3) NOT NULL,
    status VARCHAR(32) NOT NULL DEFAULT 'held',
    idempotency_key VARCHAR(191) COLLATE utf8mb4_0900_bin NOT NULL,
    created_at DATETIME(6) NOT NULL,
    captured_at DATETIME(6) NULL,
    released_at DATETIME(6) NULL,
    reason VARCHAR(500) NULL,
    UNIQUE KEY uq_credit_reservations_task (tenant_id, task_id),
    UNIQUE KEY uq_credit_reservations_key (tenant_id, idempotency_key),
    UNIQUE KEY uq_credit_reservations_tenant_id (tenant_id, id),
    CONSTRAINT fk_credit_reservations_account FOREIGN KEY (tenant_id) REFERENCES credit_accounts (tenant_id),
    CONSTRAINT fk_credit_reservations_batch FOREIGN KEY (tenant_id, batch_id) REFERENCES article_generation_batches (tenant_id, id),
    CONSTRAINT fk_credit_reservations_task FOREIGN KEY (tenant_id, task_id) REFERENCES article_generation_tasks (tenant_id, id),
    CONSTRAINT ck_credit_reservations_amount CHECK (amount > 0),
    CONSTRAINT ck_credit_reservations_status CHECK (status IN ('held','captured','released'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS credit_ledger (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    entry_type VARCHAR(32) NOT NULL,
    amount DECIMAL(18,3) NOT NULL,
    balance_after DECIMAL(18,3) NOT NULL,
    reservation_id BINARY(16) NULL,
    article_version_id BINARY(16) NULL,
    actor_user_id BINARY(16) NULL,
    external_reference VARCHAR(191) COLLATE utf8mb4_0900_bin NOT NULL,
    reason VARCHAR(500) NULL,
    created_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_credit_ledger_reference (tenant_id, external_reference),
    KEY ix_credit_ledger_tenant_created (tenant_id, created_at, id),
    CONSTRAINT fk_credit_ledger_account FOREIGN KEY (tenant_id) REFERENCES credit_accounts (tenant_id),
    CONSTRAINT fk_credit_ledger_reservation FOREIGN KEY (tenant_id, reservation_id) REFERENCES credit_reservations (tenant_id, id),
    CONSTRAINT fk_credit_ledger_version FOREIGN KEY (tenant_id, article_version_id) REFERENCES content_article_versions (tenant_id, id),
    CONSTRAINT fk_credit_ledger_actor FOREIGN KEY (actor_user_id) REFERENCES auth_users (id),
    CONSTRAINT ck_credit_ledger_type CHECK (entry_type IN ('grant','reserve','capture','release','reversal'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS content_monitor_scopes (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    article_id BINARY(16) NOT NULL,
    mode VARCHAR(32) NOT NULL DEFAULT 'all_brand_observations',
    filter_snapshot JSON NULL,
    version INT UNSIGNED NOT NULL DEFAULT 1,
    enabled_at DATETIME(6) NULL,
    disabled_at DATETIME(6) NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_content_monitor_scopes_article (tenant_id, article_id),
    UNIQUE KEY uq_content_monitor_scopes_tenant_site_article (tenant_id, site_id, article_id),
    CONSTRAINT fk_content_monitor_scopes_site FOREIGN KEY (tenant_id, site_id) REFERENCES sites (tenant_id, id),
    CONSTRAINT fk_content_monitor_scopes_article FOREIGN KEY (tenant_id, article_id) REFERENCES content_articles (tenant_id, id),
    CONSTRAINT fk_content_monitor_scopes_tenant_site_article FOREIGN KEY (tenant_id, site_id, article_id)
        REFERENCES content_articles (tenant_id, site_id, id),
    CONSTRAINT ck_content_monitor_scopes_mode CHECK (mode IN ('all_brand_observations','generation_targets','custom')),
    CONSTRAINT ck_content_monitor_scopes_snapshot CHECK (filter_snapshot IS NULL OR OCTET_LENGTH(filter_snapshot) <= 131072)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS article_import_previews (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    created_by BINARY(16) NOT NULL,
    request_hash CHAR(64) COLLATE utf8mb4_0900_bin NOT NULL,
    response_json JSON NOT NULL,
    expires_at DATETIME(6) NOT NULL,
    confirm_idempotency_key VARCHAR(191) COLLATE utf8mb4_0900_bin NULL,
    confirmed_article_ids_json JSON NULL,
    confirmed_at DATETIME(6) NULL,
    created_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_article_import_previews_hash (tenant_id, request_hash),
    UNIQUE KEY uq_article_import_previews_confirm_key (tenant_id, confirm_idempotency_key),
    CONSTRAINT fk_article_import_previews_site FOREIGN KEY (tenant_id, site_id) REFERENCES sites (tenant_id, id),
    CONSTRAINT fk_article_import_previews_creator FOREIGN KEY (created_by) REFERENCES auth_users (id),
    CONSTRAINT ck_article_import_previews_size CHECK (OCTET_LENGTH(response_json) <= 4194304)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
