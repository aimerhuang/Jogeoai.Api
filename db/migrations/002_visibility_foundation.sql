-- Jogeoai 首期业务数据库基础结构。
-- 手动执行的可重复迁移，应用启动不会执行本文件；后续结构变更应新增迁移。
-- 应用提供 16 字节 UUID（UUID_TO_BIN/BIN_TO_UUID 默认字节序，不使用交换标志）。
-- 所有 DATETIME 值由应用以 UTC 写入。流水只能追加，更正应新增冲正记录。

CREATE TABLE IF NOT EXISTS tenants (
    id BINARY(16) NOT NULL PRIMARY KEY,
    name VARCHAR(200) NOT NULL,
    status VARCHAR(32) NOT NULL DEFAULT 'active',
    deleted_at DATETIME(6) NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    KEY ix_tenants_status (status),
    KEY ix_tenants_created_at (created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS sites (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    host VARCHAR(253) COLLATE utf8mb4_0900_bin NOT NULL COMMENT '应用先规范化为小写域名，再按字节精确比较。',
    display_name VARCHAR(200) NOT NULL,
    status VARCHAR(32) NOT NULL DEFAULT 'active',
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_sites_tenant_host (tenant_id, host),
    UNIQUE KEY uq_sites_tenant_id_id (tenant_id, id),
    KEY ix_sites_tenant_created_at (tenant_id, created_at),
    CONSTRAINT fk_sites_tenant FOREIGN KEY (tenant_id) REFERENCES tenants (id),
    CONSTRAINT ck_sites_host_lowercase CHECK (BINARY host = BINARY LOWER(host))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS prompts (
    id BINARY(16) NOT NULL PRIMARY KEY,
    site_id BINARY(16) NOT NULL,
    content TEXT NOT NULL,
    locale VARCHAR(16) NOT NULL DEFAULT 'en-US',
    enabled BOOLEAN NOT NULL DEFAULT TRUE,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_prompts_site_id_id (site_id, id),
    KEY ix_prompts_site_enabled (site_id, enabled),
    KEY ix_prompts_site_created_at (site_id, created_at),
    CONSTRAINT fk_prompts_site FOREIGN KEY (site_id) REFERENCES sites (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS visibility_runs (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    status VARCHAR(32) NOT NULL DEFAULT 'queued',
    idempotency_key VARCHAR(191) COLLATE utf8mb4_0900_bin NOT NULL,
    requested_by VARCHAR(128) NULL,
    requested_at DATETIME(6) NOT NULL,
    started_at DATETIME(6) NULL,
    completed_at DATETIME(6) NULL,
    total_checks INT UNSIGNED NOT NULL DEFAULT 0,
    succeeded_checks INT UNSIGNED NOT NULL DEFAULT 0,
    failed_checks INT UNSIGNED NOT NULL DEFAULT 0,
    error_code VARCHAR(64) NULL,
    error_message VARCHAR(1000) NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_visibility_runs_tenant_idempotency (tenant_id, idempotency_key),
    UNIQUE KEY uq_visibility_runs_tenant_id_id (tenant_id, id),
    UNIQUE KEY uq_visibility_runs_site_id_id (site_id, id),
    KEY ix_visibility_runs_tenant_created_at (tenant_id, created_at),
    KEY ix_visibility_runs_site_created_at (site_id, created_at),
    KEY ix_visibility_runs_status_created_at (status, created_at),
    CONSTRAINT fk_visibility_runs_tenant FOREIGN KEY (tenant_id) REFERENCES tenants (id),
    CONSTRAINT fk_visibility_runs_site FOREIGN KEY (site_id) REFERENCES sites (id),
    CONSTRAINT fk_visibility_runs_tenant_site FOREIGN KEY (tenant_id, site_id)
        REFERENCES sites (tenant_id, id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS run_engines (
    id BINARY(16) NOT NULL PRIMARY KEY,
    run_id BINARY(16) NOT NULL,
    engine VARCHAR(64) NOT NULL,
    provider VARCHAR(64) NULL,
    request_options JSON NULL COMMENT '仅保存请求选项，最大 64 KiB；禁止 API Key 和 Authorization Header。',
    sort_order SMALLINT UNSIGNED NOT NULL DEFAULT 0,
    created_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_run_engines_run_engine (run_id, engine),
    KEY ix_run_engines_run_sort (run_id, sort_order),
    CONSTRAINT fk_run_engines_run FOREIGN KEY (run_id) REFERENCES visibility_runs (id),
    CONSTRAINT ck_run_engines_options_size CHECK (OCTET_LENGTH(request_options) <= 65536)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS provider_tasks (
    id BINARY(16) NOT NULL PRIMARY KEY,
    run_id BINARY(16) NOT NULL,
    provider VARCHAR(64) NOT NULL,
    operation VARCHAR(32) NOT NULL,
    state VARCHAR(32) NOT NULL DEFAULT 'pending',
    external_task_id VARCHAR(255) COLLATE utf8mb4_0900_bin NULL,
    attempt_count INT UNSIGNED NOT NULL DEFAULT 0,
    next_attempt_at DATETIME(6) NOT NULL,
    lease_owner VARCHAR(128) NULL,
    lease_until DATETIME(6) NULL,
    last_error_code VARCHAR(64) NULL,
    last_error_message VARCHAR(1000) NULL,
    provider_request JSON NULL COMMENT '脱敏请求，最大 1 MiB；禁止 API Key 和 Authorization Header。',
    provider_response JSON NULL COMMENT '脱敏响应，最大 1 MiB；禁止 API Key 和 Authorization Header。',
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_provider_tasks_provider_external (provider, external_task_id),
    KEY ix_provider_tasks_state_next_attempt (state, next_attempt_at),
    KEY ix_provider_tasks_lease_until (lease_until),
    KEY ix_provider_tasks_run (run_id),
    CONSTRAINT fk_provider_tasks_run FOREIGN KEY (run_id) REFERENCES visibility_runs (id),
    CONSTRAINT ck_provider_tasks_request_size CHECK (OCTET_LENGTH(provider_request) <= 1048576),
    CONSTRAINT ck_provider_tasks_response_size CHECK (OCTET_LENGTH(provider_response) <= 1048576),
    CONSTRAINT ck_provider_tasks_external_id CHECK (external_task_id IS NULL OR CHAR_LENGTH(TRIM(external_task_id)) > 0)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS outbox_events (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    event_type VARCHAR(128) NOT NULL,
    aggregate_type VARCHAR(64) NOT NULL,
    aggregate_id BINARY(16) NOT NULL,
    dedupe_key VARCHAR(191) COLLATE utf8mb4_0900_bin NULL,
    payload JSON NOT NULL COMMENT '事件载荷，最大 1 MiB；禁止 API Key 和 Authorization Header。',
    status VARCHAR(32) NOT NULL DEFAULT 'pending',
    attempt_count INT UNSIGNED NOT NULL DEFAULT 0,
    available_at DATETIME(6) NOT NULL,
    published_at DATETIME(6) NULL,
    last_error_code VARCHAR(64) NULL,
    last_error_message VARCHAR(1000) NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_outbox_events_tenant_dedupe (tenant_id, dedupe_key),
    KEY ix_outbox_events_status_available (status, available_at),
    KEY ix_outbox_events_aggregate (aggregate_type, aggregate_id),
    CONSTRAINT fk_outbox_events_tenant FOREIGN KEY (tenant_id) REFERENCES tenants (id),
    CONSTRAINT ck_outbox_events_payload_size CHECK (OCTET_LENGTH(payload) <= 1048576)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS visibility_checks (
    id BINARY(16) NOT NULL PRIMARY KEY,
    site_id BINARY(16) NOT NULL,
    run_id BINARY(16) NOT NULL,
    prompt_id BINARY(16) NOT NULL,
    engine VARCHAR(64) NOT NULL,
    provider VARCHAR(64) NULL,
    status VARCHAR(32) NOT NULL DEFAULT 'pending',
    prompt_snapshot TEXT NOT NULL,
    model_version VARCHAR(128) NULL,
    answer MEDIUMTEXT NULL,
    search_queries JSON NULL,
    raw_response JSON NULL COMMENT '脱敏原始响应，最大 1 MiB；超限改存引用；禁止 API Key 和 Authorization Header。',
    raw_response_ref VARCHAR(1024) NULL,
    duration_ms INT UNSIGNED NULL,
    cost DECIMAL(18,6) NULL,
    error_code VARCHAR(64) NULL,
    error_message VARCHAR(1000) NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_visibility_checks_run_prompt_engine (run_id, prompt_id, engine),
    KEY ix_visibility_checks_run_status (run_id, status),
    KEY ix_visibility_checks_prompt_engine_created (prompt_id, engine, created_at),
    CONSTRAINT fk_visibility_checks_run FOREIGN KEY (run_id) REFERENCES visibility_runs (id),
    CONSTRAINT fk_visibility_checks_prompt FOREIGN KEY (prompt_id) REFERENCES prompts (id),
    CONSTRAINT fk_visibility_checks_site_run FOREIGN KEY (site_id, run_id) REFERENCES visibility_runs (site_id, id),
    CONSTRAINT fk_visibility_checks_site_prompt FOREIGN KEY (site_id, prompt_id) REFERENCES prompts (site_id, id),
    CONSTRAINT ck_visibility_checks_search_size CHECK (OCTET_LENGTH(search_queries) <= 65536),
    CONSTRAINT ck_visibility_checks_raw_size CHECK (OCTET_LENGTH(raw_response) <= 1048576),
    CONSTRAINT ck_visibility_checks_answer_size CHECK (OCTET_LENGTH(answer) <= 1048576)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS citations (
    id BINARY(16) NOT NULL PRIMARY KEY,
    check_id BINARY(16) NOT NULL,
    normalized_url VARCHAR(750) COLLATE utf8mb4_0900_bin NOT NULL,
    title VARCHAR(512) NULL,
    source_domain VARCHAR(253) NULL,
    position INT UNSIGNED NOT NULL,
    fetched_at DATETIME(6) NULL,
    created_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_citations_check_url_position (check_id, normalized_url, position),
    KEY ix_citations_check_position (check_id, position),
    CONSTRAINT fk_citations_check FOREIGN KEY (check_id) REFERENCES visibility_checks (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS mentions (
    id BINARY(16) NOT NULL PRIMARY KEY,
    check_id BINARY(16) NOT NULL,
    subject VARCHAR(255) NOT NULL,
    matched_text VARCHAR(512) NULL,
    sentiment VARCHAR(32) NULL,
    confidence DECIMAL(5,4) NULL,
    offset_start INT UNSIGNED NULL,
    offset_end INT UNSIGNED NULL,
    extractor_version VARCHAR(64) NULL,
    created_at DATETIME(6) NOT NULL,
    KEY ix_mentions_check_position (check_id, offset_start),
    KEY ix_mentions_check_subject (check_id, subject),
    CONSTRAINT fk_mentions_check FOREIGN KEY (check_id) REFERENCES visibility_checks (id),
    CONSTRAINT ck_mentions_confidence CHECK (confidence IS NULL OR (confidence >= 0 AND confidence <= 1)),
    CONSTRAINT ck_mentions_offsets CHECK (offset_end IS NULL OR offset_start IS NULL OR offset_end >= offset_start)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS usage_ledger (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    run_id BINARY(16) NULL,
    entry_type VARCHAR(32) NOT NULL,
    units DECIMAL(18,3) NOT NULL,
    amount DECIMAL(18,6) NOT NULL,
    currency CHAR(3) NOT NULL DEFAULT 'USD',
    reference VARCHAR(191) COLLATE utf8mb4_0900_bin NOT NULL,
    created_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_usage_ledger_tenant_reference (tenant_id, reference),
    KEY ix_usage_ledger_tenant_created (tenant_id, created_at),
    KEY ix_usage_ledger_run_created (run_id, created_at),
    CONSTRAINT fk_usage_ledger_tenant FOREIGN KEY (tenant_id) REFERENCES tenants (id),
    CONSTRAINT fk_usage_ledger_run FOREIGN KEY (run_id) REFERENCES visibility_runs (id),
    CONSTRAINT fk_usage_ledger_tenant_run FOREIGN KEY (tenant_id, run_id)
        REFERENCES visibility_runs (tenant_id, id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
