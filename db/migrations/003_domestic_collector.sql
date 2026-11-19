-- Jogeoai domestic browser collector control-plane schema.
-- Run explicitly after 002_visibility_foundation.sql; the application does not
-- execute this migration on startup.

-- ALTER TABLE has no portable IF NOT EXISTS form for columns, indexes and
-- constraints in MySQL 8.4. Keep this migration safe to replay by guarding
-- each additive change with information_schema checks.
DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_003_prepare_schema//
CREATE PROCEDURE jogeoai_003_prepare_schema()
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'visibility_runs' AND column_name = 'collector_request_hash') THEN
        ALTER TABLE visibility_runs ADD COLUMN collector_request_hash CHAR(64) NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'visibility_checks' AND column_name = 'tenant_id') THEN
        ALTER TABLE visibility_checks ADD COLUMN tenant_id BINARY(16) NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'visibility_checks' AND column_name = 'collector_task_id') THEN
        ALTER TABLE visibility_checks ADD COLUMN collector_task_id BINARY(16) NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'visibility_checks' AND column_name = 'collector_node_id') THEN
        ALTER TABLE visibility_checks ADD COLUMN collector_node_id VARCHAR(128) NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'visibility_checks' AND column_name = 'collector_region') THEN
        ALTER TABLE visibility_checks ADD COLUMN collector_region VARCHAR(16) NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'visibility_checks' AND column_name = 'egress_country') THEN
        ALTER TABLE visibility_checks ADD COLUMN egress_country CHAR(2) NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'visibility_checks' AND column_name = 'collector_build_revision') THEN
        ALTER TABLE visibility_checks ADD COLUMN collector_build_revision VARCHAR(128) NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'visibility_checks' AND column_name = 'captured_at') THEN
        ALTER TABLE visibility_checks ADD COLUMN captured_at DATETIME(6) NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'visibility_checks' AND column_name = 'result_hash') THEN
        ALTER TABLE visibility_checks ADD COLUMN result_hash CHAR(64) NULL;
    END IF;
END//
DELIMITER ;
CALL jogeoai_003_prepare_schema();
DROP PROCEDURE jogeoai_003_prepare_schema;

UPDATE visibility_checks AS vc
JOIN visibility_runs AS vr ON vr.id = vc.run_id
SET vc.tenant_id = vr.tenant_id
WHERE vc.tenant_id IS NULL;

ALTER TABLE visibility_checks
    MODIFY COLUMN tenant_id BINARY(16) NOT NULL;

DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_003_add_constraints//
CREATE PROCEDURE jogeoai_003_add_constraints()
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'visibility_checks' AND index_name = 'uq_visibility_checks_tenant_id') THEN
        ALTER TABLE visibility_checks ADD UNIQUE KEY uq_visibility_checks_tenant_id (tenant_id, id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'visibility_checks' AND index_name = 'uq_visibility_checks_collector_task') THEN
        ALTER TABLE visibility_checks ADD UNIQUE KEY uq_visibility_checks_collector_task (collector_task_id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'visibility_checks' AND index_name = 'ix_visibility_checks_collector_task') THEN
        ALTER TABLE visibility_checks ADD KEY ix_visibility_checks_collector_task (collector_task_id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE() AND table_name = 'visibility_checks' AND constraint_name = 'fk_visibility_checks_tenant') THEN
        ALTER TABLE visibility_checks ADD CONSTRAINT fk_visibility_checks_tenant FOREIGN KEY (tenant_id) REFERENCES tenants (id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE() AND table_name = 'visibility_checks' AND constraint_name = 'fk_visibility_checks_tenant_run') THEN
        ALTER TABLE visibility_checks ADD CONSTRAINT fk_visibility_checks_tenant_run FOREIGN KEY (tenant_id, run_id) REFERENCES visibility_runs (tenant_id, id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE() AND table_name = 'visibility_checks' AND constraint_name = 'ck_visibility_checks_collector_region') THEN
        ALTER TABLE visibility_checks ADD CONSTRAINT ck_visibility_checks_collector_region CHECK (collector_region IS NULL OR collector_region IN ('cn', 'overseas'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE() AND table_name = 'visibility_checks' AND constraint_name = 'ck_visibility_checks_egress_country') THEN
        ALTER TABLE visibility_checks ADD CONSTRAINT ck_visibility_checks_egress_country CHECK (egress_country IS NULL OR CHAR_LENGTH(egress_country) = 2);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE() AND table_name = 'visibility_checks' AND constraint_name = 'ck_visibility_checks_result_hash') THEN
        ALTER TABLE visibility_checks ADD CONSTRAINT ck_visibility_checks_result_hash CHECK (result_hash IS NULL OR CHAR_LENGTH(result_hash) = 64);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE() AND table_name = 'visibility_runs' AND constraint_name = 'ck_visibility_runs_collector_request_hash') THEN
        ALTER TABLE visibility_runs ADD CONSTRAINT ck_visibility_runs_collector_request_hash CHECK (collector_request_hash IS NULL OR CHAR_LENGTH(collector_request_hash) = 64);
    END IF;
END//
DELIMITER ;
CALL jogeoai_003_add_constraints();
DROP PROCEDURE jogeoai_003_add_constraints;

CREATE TABLE IF NOT EXISTS collector_platforms (
    code VARCHAR(64) NOT NULL PRIMARY KEY,
    display_name VARCHAR(128) NOT NULL,
    region VARCHAR(16) NOT NULL,
    channel VARCHAR(32) NOT NULL,
    enabled BOOLEAN NOT NULL DEFAULT TRUE,
    contract_version INT UNSIGNED NOT NULL DEFAULT 1,
    contract_reviewed_at DATETIME(6) NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    KEY ix_collector_platforms_region_enabled (region, enabled),
    KEY ix_collector_platforms_contract_reviewed (contract_reviewed_at),
    CONSTRAINT ck_collector_platforms_region CHECK (region IN ('cn', 'overseas')),
    CONSTRAINT ck_collector_platforms_channel CHECK (channel = 'consumer_web')
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS collector_platform_entries (
    id BINARY(16) NOT NULL PRIMARY KEY,
    platform_code VARCHAR(64) NOT NULL,
    entry_code VARCHAR(128) NOT NULL,
    version INT UNSIGNED NOT NULL DEFAULT 1,
    display_name VARCHAR(128) NOT NULL,
    region VARCHAR(16) NOT NULL,
    channel VARCHAR(32) NOT NULL,
    config JSON NULL COMMENT '脱敏入口摘要；禁止 Cookie、Token 和 Authorization Header。',
    enabled BOOLEAN NOT NULL DEFAULT TRUE,
    created_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_collector_entries_code_version (entry_code, version),
    KEY ix_collector_entries_platform_enabled (platform_code, enabled),
    CONSTRAINT fk_collector_entries_platform FOREIGN KEY (platform_code) REFERENCES collector_platforms (code),
    CONSTRAINT ck_collector_entries_region CHECK (region IN ('cn', 'overseas')),
    CONSTRAINT ck_collector_entries_channel CHECK (channel = 'consumer_web'),
    CONSTRAINT ck_collector_entries_config_size CHECK (OCTET_LENGTH(config) <= 65536)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS collector_tasks (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    run_id BINARY(16) NOT NULL,
    check_id BINARY(16) NOT NULL,
    platform_entry_id BINARY(16) NOT NULL,
    platform_code VARCHAR(64) NOT NULL,
    region VARCHAR(16) NOT NULL,
    language VARCHAR(16) NOT NULL,
    channel VARCHAR(32) NOT NULL,
    question_snapshot TEXT NOT NULL,
    status VARCHAR(32) NOT NULL DEFAULT 'pending',
    lease_version INT UNSIGNED NOT NULL DEFAULT 0,
    lease_owner VARCHAR(128) NULL,
    lease_until DATETIME(6) NULL,
    attempt_count INT UNSIGNED NOT NULL DEFAULT 0,
    next_attempt_at DATETIME(6) NOT NULL,
    last_error_code VARCHAR(64) NULL,
    last_error_message VARCHAR(1000) NULL,
    started_at DATETIME(6) NULL,
    completed_at DATETIME(6) NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_collector_tasks_check (check_id),
    UNIQUE KEY uq_collector_tasks_tenant_id_id (tenant_id, id),
    KEY ix_collector_tasks_claim (tenant_id, region, status, next_attempt_at, lease_until),
    KEY ix_collector_tasks_run (run_id),
    KEY ix_collector_tasks_lease_until (lease_until),
    CONSTRAINT fk_collector_tasks_tenant FOREIGN KEY (tenant_id) REFERENCES tenants (id),
    CONSTRAINT fk_collector_tasks_run FOREIGN KEY (tenant_id, run_id)
        REFERENCES visibility_runs (tenant_id, id),
    CONSTRAINT fk_collector_tasks_check FOREIGN KEY (tenant_id, check_id)
        REFERENCES visibility_checks (tenant_id, id),
    CONSTRAINT fk_collector_tasks_entry FOREIGN KEY (platform_entry_id) REFERENCES collector_platform_entries (id),
    CONSTRAINT ck_collector_tasks_region CHECK (region IN ('cn', 'overseas')),
    CONSTRAINT ck_collector_tasks_channel CHECK (channel = 'consumer_web'),
    CONSTRAINT ck_collector_tasks_status CHECK (
        status IN ('pending', 'leased', 'collecting', 'succeeded', 'retryable_failed', 'terminal_failed')
    ),
    CONSTRAINT ck_collector_tasks_lease CHECK (
        (status IN ('leased', 'collecting') AND lease_owner IS NOT NULL AND lease_until IS NOT NULL)
        OR (status NOT IN ('leased', 'collecting'))
    )
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS collector_attempts (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    collector_task_id BINARY(16) NOT NULL,
    lease_version INT UNSIGNED NOT NULL,
    node_id VARCHAR(128) NOT NULL,
    region VARCHAR(16) NOT NULL,
    event_kind VARCHAR(32) NOT NULL,
    idempotency_key VARCHAR(191) COLLATE utf8mb4_0900_bin NULL,
    payload_hash CHAR(64) NULL,
    error_code VARCHAR(64) NULL,
    created_at DATETIME(6) NOT NULL,
    KEY ix_collector_attempts_task_created (collector_task_id, created_at),
    KEY ix_collector_attempts_tenant_created (tenant_id, created_at),
    CONSTRAINT fk_collector_attempts_task FOREIGN KEY (collector_task_id) REFERENCES collector_tasks (id),
    CONSTRAINT fk_collector_attempts_tenant_task FOREIGN KEY (tenant_id, collector_task_id) REFERENCES collector_tasks (tenant_id, id),
    CONSTRAINT fk_collector_attempts_tenant FOREIGN KEY (tenant_id) REFERENCES tenants (id),
    CONSTRAINT ck_collector_attempts_event CHECK (event_kind IN ('leased', 'heartbeat', 'accepted', 'duplicate', 'stale', 'failed')),
    CONSTRAINT ck_collector_attempts_hash CHECK (payload_hash IS NULL OR CHAR_LENGTH(payload_hash) = 64)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS collector_result_submissions (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    collector_task_id BINARY(16) NOT NULL,
    lease_version INT UNSIGNED NOT NULL,
    node_id VARCHAR(128) NOT NULL,
    region VARCHAR(16) NOT NULL,
    idempotency_key VARCHAR(191) COLLATE utf8mb4_0900_bin NOT NULL,
    payload_hash CHAR(64) NOT NULL,
    disposition VARCHAR(16) NOT NULL,
    answer_text MEDIUMTEXT NOT NULL,
    citations JSON NOT NULL,
    egress_country CHAR(2) NOT NULL,
    collector_build_revision VARCHAR(128) NOT NULL,
    provider_metadata JSON NULL,
    completed_at DATETIME(6) NOT NULL,
    received_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_collector_submissions_task_key (collector_task_id, idempotency_key),
    KEY ix_collector_submissions_tenant_received (tenant_id, received_at),
    KEY ix_collector_submissions_task_received (collector_task_id, received_at),
    CONSTRAINT fk_collector_submissions_task FOREIGN KEY (collector_task_id) REFERENCES collector_tasks (id),
    CONSTRAINT fk_collector_submissions_tenant_task FOREIGN KEY (tenant_id, collector_task_id) REFERENCES collector_tasks (tenant_id, id),
    CONSTRAINT fk_collector_submissions_tenant FOREIGN KEY (tenant_id) REFERENCES tenants (id),
    CONSTRAINT ck_collector_submissions_region CHECK (region IN ('cn', 'overseas')),
    CONSTRAINT ck_collector_submissions_disposition CHECK (disposition IN ('accepted', 'duplicate', 'stale')),
    CONSTRAINT ck_collector_submissions_answer_size CHECK (OCTET_LENGTH(answer_text) <= 1048576),
    CONSTRAINT ck_collector_submissions_citations_size CHECK (OCTET_LENGTH(citations) <= 262144),
    CONSTRAINT ck_collector_submissions_metadata_size CHECK (OCTET_LENGTH(provider_metadata) <= 1048576),
    CONSTRAINT ck_collector_submissions_egress CHECK (CHAR_LENGTH(egress_country) = 2),
    CONSTRAINT ck_collector_submissions_hash CHECK (CHAR_LENGTH(payload_hash) = 64)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_003_add_task_tenant_keys//
CREATE PROCEDURE jogeoai_003_add_task_tenant_keys()
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'collector_tasks' AND index_name = 'uq_collector_tasks_tenant_id_id') THEN
        ALTER TABLE collector_tasks ADD UNIQUE KEY uq_collector_tasks_tenant_id_id (tenant_id, id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE() AND table_name = 'collector_attempts' AND constraint_name = 'fk_collector_attempts_tenant_task') THEN
        ALTER TABLE collector_attempts ADD CONSTRAINT fk_collector_attempts_tenant_task FOREIGN KEY (tenant_id, collector_task_id) REFERENCES collector_tasks (tenant_id, id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE() AND table_name = 'collector_result_submissions' AND constraint_name = 'fk_collector_submissions_tenant_task') THEN
        ALTER TABLE collector_result_submissions ADD CONSTRAINT fk_collector_submissions_tenant_task FOREIGN KEY (tenant_id, collector_task_id) REFERENCES collector_tasks (tenant_id, id);
    END IF;
END//
DELIMITER ;
CALL jogeoai_003_add_task_tenant_keys();
DROP PROCEDURE jogeoai_003_add_task_tenant_keys;

INSERT INTO collector_platforms
    (code, display_name, region, channel, enabled, contract_version, contract_reviewed_at, created_at, updated_at)
VALUES
    ('doubao', '豆包', 'cn', 'consumer_web', TRUE, 1, NULL, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)),
    ('ernie', '文心', 'cn', 'consumer_web', TRUE, 1, NULL, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)),
    ('zhipu_qingyan', '智谱清言', 'cn', 'consumer_web', TRUE, 1, NULL, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)),
    ('qwen', '通义千问', 'cn', 'consumer_web', TRUE, 1, NULL, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)),
    ('kimi', 'Kimi', 'cn', 'consumer_web', TRUE, 1, NULL, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)),
    ('deepseek', 'DeepSeek', 'cn', 'consumer_web', TRUE, 1, NULL, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)),
    ('tencent_yuanbao', '腾讯元宝', 'cn', 'consumer_web', TRUE, 1, NULL, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6))
ON DUPLICATE KEY UPDATE
    display_name = VALUES(display_name),
    enabled = VALUES(enabled),
    updated_at = UTC_TIMESTAMP(6);

INSERT INTO collector_platform_entries
    (id, platform_code, entry_code, version, display_name, region, channel, config, enabled, created_at)
SELECT UUID_TO_BIN(UUID()), code, CONCAT(code, '.consumer_web'), 1,
       CONCAT(display_name, ' 消费者 Web'), region, channel, JSON_OBJECT(), TRUE, UTC_TIMESTAMP(6)
FROM collector_platforms
WHERE region = 'cn'
ON DUPLICATE KEY UPDATE enabled = VALUES(enabled);
