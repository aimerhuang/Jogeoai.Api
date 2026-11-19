-- 注册邮件验证码和注册后的团队引导；在 014 之后执行，可重复运行。
-- 不修改已有账号的认证状态，不删除已有业务数据。
CREATE TABLE IF NOT EXISTS auth_email_verifications (
    email VARCHAR(320) COLLATE utf8mb4_0900_bin NOT NULL PRIMARY KEY,
    challenge_id BINARY(16) NOT NULL,
    code_hash VARCHAR(512) NOT NULL,
    expires_at DATETIME(6) NOT NULL,
    sent_at DATETIME(6) NOT NULL,
    delivered BOOLEAN NOT NULL DEFAULT FALSE,
    attempts SMALLINT UNSIGNED NOT NULL DEFAULT 0,
    consumed_at DATETIME(6) NULL,
    window_started_at DATETIME(6) NOT NULL,
    send_count SMALLINT UNSIGNED NOT NULL DEFAULT 0,
    KEY ix_email_verifications_expiry (expires_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS tenant_onboarding (
    tenant_id BINARY(16) NOT NULL PRIMARY KEY,
    team_configured_at DATETIME(6) NULL,
    site_id BINARY(16) NULL,
    completed_at DATETIME(6) NULL,
    CONSTRAINT fk_tenant_onboarding_tenant FOREIGN KEY (tenant_id) REFERENCES tenants(id),
    CONSTRAINT fk_tenant_onboarding_site FOREIGN KEY (tenant_id, site_id) REFERENCES sites(tenant_id, id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS onboarding_drafts (
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    draft_json JSON NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    PRIMARY KEY (tenant_id, site_id),
    CONSTRAINT fk_onboarding_draft_site FOREIGN KEY (tenant_id, site_id) REFERENCES sites(tenant_id, id),
    CONSTRAINT ck_onboarding_draft_size CHECK (OCTET_LENGTH(draft_json) <= 131072)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
