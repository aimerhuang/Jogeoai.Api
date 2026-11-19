-- 用户身份、租户成员关系与品牌引导的基础结构。
-- 手动执行于 001–007 之后；应用启动不会执行本文件。
-- 所有 UUID 使用 BINARY(16)，时间由应用按 UTC 写入。

DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_008_columns//
CREATE PROCEDURE jogeoai_008_columns()
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
        WHERE table_schema = DATABASE() AND table_name = 'sites' AND column_name = 'report_title') THEN
        ALTER TABLE sites ADD COLUMN report_title VARCHAR(200) NULL AFTER display_name;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
        WHERE table_schema = DATABASE() AND table_name = 'sites' AND column_name = 'include_subdomains') THEN
        ALTER TABLE sites ADD COLUMN include_subdomains BOOLEAN NOT NULL DEFAULT FALSE AFTER report_title;
    END IF;
END//
DELIMITER ;
CALL jogeoai_008_columns();
DROP PROCEDURE jogeoai_008_columns;

UPDATE sites SET report_title = display_name WHERE report_title IS NULL;

CREATE TABLE IF NOT EXISTS auth_users (
    id BINARY(16) NOT NULL PRIMARY KEY,
    email VARCHAR(320) COLLATE utf8mb4_0900_bin NOT NULL,
    password_hash VARCHAR(512) NOT NULL,
    display_name VARCHAR(200) NULL,
    status VARCHAR(32) NOT NULL DEFAULT 'active',
    email_verified_at DATETIME(6) NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_auth_users_email (email),
    KEY ix_auth_users_status_created (status, created_at),
    CONSTRAINT ck_auth_users_email CHECK (CHAR_LENGTH(TRIM(email)) BETWEEN 3 AND 320),
    CONSTRAINT ck_auth_users_status CHECK (status IN ('active', 'disabled', 'pending'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS auth_sessions (
    id BINARY(16) NOT NULL PRIMARY KEY,
    user_id BINARY(16) NOT NULL,
    token_hash CHAR(64) COLLATE utf8mb4_0900_bin NOT NULL,
    expires_at DATETIME(6) NOT NULL,
    revoked_at DATETIME(6) NULL,
    last_seen_at DATETIME(6) NULL,
    created_at DATETIME(6) NOT NULL,
    created_ip VARCHAR(64) NULL,
    user_agent VARCHAR(512) NULL,
    UNIQUE KEY uq_auth_sessions_token_hash (token_hash),
    KEY ix_auth_sessions_user_expiry (user_id, expires_at),
    KEY ix_auth_sessions_expiry (expires_at, revoked_at),
    CONSTRAINT fk_auth_sessions_user FOREIGN KEY (user_id) REFERENCES auth_users (id),
    CONSTRAINT ck_auth_sessions_token_hash CHECK (CHAR_LENGTH(token_hash) = 64),
    CONSTRAINT ck_auth_sessions_dates CHECK (expires_at > created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS tenant_members (
    tenant_id BINARY(16) NOT NULL,
    user_id BINARY(16) NOT NULL,
    role VARCHAR(32) NOT NULL DEFAULT 'member',
    status VARCHAR(32) NOT NULL DEFAULT 'active',
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    PRIMARY KEY (tenant_id, user_id),
    KEY ix_tenant_members_user_status (user_id, status),
    CONSTRAINT fk_tenant_members_tenant FOREIGN KEY (tenant_id) REFERENCES tenants (id),
    CONSTRAINT fk_tenant_members_user FOREIGN KEY (user_id) REFERENCES auth_users (id),
    CONSTRAINT ck_tenant_members_role CHECK (role IN ('owner', 'admin', 'member', 'viewer')),
    CONSTRAINT ck_tenant_members_status CHECK (status IN ('active', 'invited', 'suspended'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS onboarding_progress (
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    current_step VARCHAR(32) NOT NULL DEFAULT 'create_team',
    completed BOOLEAN NOT NULL DEFAULT FALSE,
    selected_prompt_limit SMALLINT UNSIGNED NOT NULL DEFAULT 5,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    PRIMARY KEY (tenant_id, site_id),
    KEY ix_onboarding_progress_tenant_updated (tenant_id, updated_at),
    CONSTRAINT fk_onboarding_progress_tenant_site FOREIGN KEY (tenant_id, site_id)
        REFERENCES sites (tenant_id, id),
    CONSTRAINT ck_onboarding_progress_step CHECK (current_step IN ('create_team', 'create_brand', 'select_prompts', 'select_competitors', 'complete')),
    CONSTRAINT ck_onboarding_progress_prompt_limit CHECK (selected_prompt_limit BETWEEN 1 AND 30)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS research_provider_settings (
    provider VARCHAR(64) COLLATE utf8mb4_0900_bin NOT NULL PRIMARY KEY,
    model VARCHAR(128) NOT NULL,
    api_url VARCHAR(2048) NOT NULL,
    credential TEXT NULL,
    credential_hint VARCHAR(32) NULL,
    priority INT NOT NULL DEFAULT 100,
    enabled BOOLEAN NOT NULL DEFAULT FALSE,
    web_search_enabled BOOLEAN NOT NULL DEFAULT FALSE,
    timeout_seconds INT UNSIGNED NOT NULL DEFAULT 90,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    KEY ix_research_provider_enabled_priority (enabled, priority, provider),
    CONSTRAINT ck_research_provider_provider CHECK (CHAR_LENGTH(TRIM(provider)) BETWEEN 1 AND 64),
    CONSTRAINT ck_research_provider_timeout CHECK (timeout_seconds BETWEEN 5 AND 600),
    CONSTRAINT ck_research_provider_credential_hint CHECK (credential_hint IS NULL OR CHAR_LENGTH(credential_hint) <= 32)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS brand_research_jobs (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    user_id BINARY(16) NOT NULL,
    status VARCHAR(32) NOT NULL DEFAULT 'pending',
    attempt_count INT UNSIGNED NOT NULL DEFAULT 0,
    provider VARCHAR(64) NULL,
    model VARCHAR(128) NULL,
    request_json JSON NOT NULL,
    result_json JSON NULL,
    error_code VARCHAR(64) NULL,
    error_message VARCHAR(1000) NULL,
    idempotency_key VARCHAR(191) COLLATE utf8mb4_0900_bin NOT NULL,
    lease_owner VARCHAR(128) NULL,
    lease_until DATETIME(6) NULL,
    next_attempt_at DATETIME(6) NOT NULL,
    requested_at DATETIME(6) NULL,
    started_at DATETIME(6) NULL,
    completed_at DATETIME(6) NULL,
    cancelled_at DATETIME(6) NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_brand_research_jobs_tenant_idempotency (tenant_id, idempotency_key),
    UNIQUE KEY uq_brand_research_jobs_tenant_id (tenant_id, id),
    KEY ix_brand_research_jobs_claim (status, next_attempt_at, lease_until),
    KEY ix_brand_research_jobs_site_created (tenant_id, site_id, created_at),
    CONSTRAINT fk_brand_research_jobs_site FOREIGN KEY (tenant_id, site_id) REFERENCES sites (tenant_id, id),
    CONSTRAINT fk_brand_research_jobs_user FOREIGN KEY (user_id) REFERENCES auth_users (id),
    CONSTRAINT ck_brand_research_jobs_status CHECK (status IN ('pending', 'running', 'done', 'failed', 'cancelled')),
    CONSTRAINT ck_brand_research_jobs_input_size CHECK (OCTET_LENGTH(request_json) <= 1048576),
    CONSTRAINT ck_brand_research_jobs_result_size CHECK (OCTET_LENGTH(result_json) <= 1048576)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
