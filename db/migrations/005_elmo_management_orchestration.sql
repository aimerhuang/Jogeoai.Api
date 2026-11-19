-- Elmo management and collection orchestration. Run after 001–004 with old workers stopped.
-- Replay safe: every ALTER is guarded; existing runs/checks are retained and checked before key changes.

CREATE TABLE IF NOT EXISTS site_brand_settings (
    site_id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    default_country CHAR(2) NOT NULL DEFAULT 'US',
    default_locale VARCHAR(16) NOT NULL DEFAULT 'en-US',
    time_zone VARCHAR(100) NOT NULL DEFAULT 'UTC',
    subject_config_version INT UNSIGNED NOT NULL DEFAULT 1,
    target_config_version INT UNSIGNED NOT NULL DEFAULT 1,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_brand_settings_tenant_site (tenant_id, site_id),
    CONSTRAINT fk_brand_settings_tenant_site FOREIGN KEY (tenant_id, site_id) REFERENCES sites (tenant_id, id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT IGNORE INTO site_brand_settings (site_id, tenant_id, created_at, updated_at)
SELECT id, tenant_id, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6) FROM sites;

CREATE TABLE IF NOT EXISTS competitors (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    name VARCHAR(200) NOT NULL,
    status VARCHAR(32) NOT NULL DEFAULT 'active',
    archived_at DATETIME(6) NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_competitors_site_id (site_id, id),
    KEY ix_competitors_site_status (site_id, status, created_at),
    CONSTRAINT fk_competitors_tenant_site FOREIGN KEY (tenant_id, site_id) REFERENCES sites (tenant_id, id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS subject_terms (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    subject_type VARCHAR(16) NOT NULL,
    subject_id BINARY(16) NOT NULL,
    term_type VARCHAR(32) NOT NULL,
    namespace_key VARCHAR(16) NOT NULL,
    normalized_key VARCHAR(253) COLLATE utf8mb4_0900_bin NOT NULL,
    display_value VARCHAR(253) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_subject_terms_scope_key (site_id, namespace_key, normalized_key),
    KEY ix_subject_terms_subject (site_id, subject_type, subject_id),
    CONSTRAINT fk_subject_terms_tenant_site FOREIGN KEY (tenant_id, site_id) REFERENCES sites (tenant_id, id),
    CONSTRAINT ck_subject_terms_kind CHECK (subject_type IN ('brand','competitor') AND namespace_key IN ('text','domain'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT IGNORE INTO subject_terms (id, tenant_id, site_id, subject_type, subject_id, term_type, namespace_key, normalized_key, display_value, created_at)
SELECT UUID_TO_BIN(UUID()), tenant_id, id, 'brand', id, 'primary_domain', 'domain', host, host, UTC_TIMESTAMP(6) FROM sites;

CREATE TABLE IF NOT EXISTS subject_config_revisions (
    site_id BINARY(16) NOT NULL,
    tenant_id BINARY(16) NOT NULL,
    version INT UNSIGNED NOT NULL,
    snapshot JSON NOT NULL,
    created_at DATETIME(6) NOT NULL,
    PRIMARY KEY (site_id, version),
    CONSTRAINT fk_subject_revisions_tenant_site FOREIGN KEY (tenant_id, site_id) REFERENCES sites (tenant_id, id),
    CONSTRAINT ck_subject_revisions_snapshot CHECK (OCTET_LENGTH(snapshot) <= 1048576)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT IGNORE INTO subject_config_revisions (site_id, tenant_id, version, snapshot, created_at)
SELECT s.id, s.tenant_id, 1, JSON_OBJECT('brandId', BIN_TO_UUID(s.id), 'name', s.display_name, 'primaryDomain', s.host), UTC_TIMESTAMP(6)
FROM sites s;

CREATE TABLE IF NOT EXISTS tags (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    name VARCHAR(100) NOT NULL,
    normalized_name VARCHAR(100) COLLATE utf8mb4_0900_bin NOT NULL,
    status VARCHAR(32) NOT NULL DEFAULT 'active',
    version INT UNSIGNED NOT NULL DEFAULT 1,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_tags_site_id (site_id, id),
    UNIQUE KEY uq_tags_site_name (site_id, normalized_name),
    CONSTRAINT fk_tags_tenant_site FOREIGN KEY (tenant_id, site_id) REFERENCES sites (tenant_id, id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS prompt_versions (
    id BINARY(16) NOT NULL PRIMARY KEY,
    site_id BINARY(16) NOT NULL,
    prompt_id BINARY(16) NOT NULL,
    version INT UNSIGNED NOT NULL,
    content TEXT NOT NULL,
    locale VARCHAR(16) NOT NULL,
    branded_status VARCHAR(16) NOT NULL DEFAULT 'unbranded',
    branded_source VARCHAR(16) NOT NULL DEFAULT 'system',
    created_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_prompt_versions_prompt_version (prompt_id, version),
    UNIQUE KEY uq_prompt_versions_prompt_id (prompt_id, id),
    CONSTRAINT fk_prompt_versions_site_prompt FOREIGN KEY (site_id, prompt_id) REFERENCES prompts (site_id, id),
    CONSTRAINT ck_prompt_versions_brand_status CHECK (branded_status IN ('branded','unbranded'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS prompt_version_tags (
    prompt_version_id BINARY(16) NOT NULL,
    tag_id BINARY(16) NOT NULL,
    tag_name_snapshot VARCHAR(100) NOT NULL,
    PRIMARY KEY (prompt_version_id, tag_id),
    CONSTRAINT fk_prompt_version_tags_version FOREIGN KEY (prompt_version_id) REFERENCES prompt_versions (id),
    CONSTRAINT fk_prompt_version_tags_tag FOREIGN KEY (tag_id) REFERENCES tags (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_005_alter//
CREATE PROCEDURE jogeoai_005_alter()
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'subject_terms'
               AND column_name = 'term_type' AND character_maximum_length < 32) THEN
        ALTER TABLE subject_terms MODIFY COLUMN term_type VARCHAR(32) NOT NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'site_brand_settings' AND column_name = 'target_config_version') THEN
        ALTER TABLE site_brand_settings ADD COLUMN target_config_version INT UNSIGNED NOT NULL DEFAULT 1;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'prompts' AND column_name = 'current_version_id') THEN
        ALTER TABLE prompts ADD COLUMN current_version_id BINARY(16) NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'prompts' AND column_name = 'archived_at') THEN
        ALTER TABLE prompts ADD COLUMN archived_at DATETIME(6) NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'prompts' AND column_name = 'config_version') THEN
        ALTER TABLE prompts ADD COLUMN config_version INT UNSIGNED NOT NULL DEFAULT 1;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'visibility_runs' AND column_name = 'subject_config_version') THEN
        ALTER TABLE visibility_runs ADD COLUMN subject_config_version INT UNSIGNED NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'visibility_runs' AND column_name = 'target_config_version') THEN
        ALTER TABLE visibility_runs ADD COLUMN target_config_version INT UNSIGNED NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'visibility_runs' AND column_name = 'collection_preview_id') THEN
        ALTER TABLE visibility_runs ADD COLUMN collection_preview_id BINARY(16) NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'visibility_runs' AND column_name = 'selection_snapshot') THEN
        ALTER TABLE visibility_runs ADD COLUMN selection_snapshot JSON NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'visibility_runs' AND column_name = 'http_request_hash') THEN
        ALTER TABLE visibility_runs ADD COLUMN http_request_hash CHAR(64) COLLATE utf8mb4_0900_bin NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'run_engines' AND column_name = 'target_key') THEN
        ALTER TABLE run_engines ADD COLUMN target_key CHAR(64) COLLATE utf8mb4_0900_bin NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'run_engines' AND column_name = 'capture_mode') THEN
        ALTER TABLE run_engines ADD COLUMN capture_mode VARCHAR(32) NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'run_engines' AND column_name = 'country') THEN
        ALTER TABLE run_engines ADD COLUMN country CHAR(2) NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'run_engines' AND column_name = 'locale') THEN
        ALTER TABLE run_engines ADD COLUMN locale VARCHAR(16) NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'visibility_checks' AND column_name = 'run_target_id') THEN
        ALTER TABLE visibility_checks ADD COLUMN run_target_id BINARY(16) NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'visibility_checks' AND column_name = 'sample_index') THEN
        ALTER TABLE visibility_checks ADD COLUMN sample_index TINYINT UNSIGNED NOT NULL DEFAULT 1;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'visibility_checks' AND column_name = 'prompt_version_id') THEN
        ALTER TABLE visibility_checks ADD COLUMN prompt_version_id BINARY(16) NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'visibility_checks' AND column_name = 'prompt_tags_snapshot') THEN
        ALTER TABLE visibility_checks ADD COLUMN prompt_tags_snapshot JSON NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'visibility_checks' AND column_name = 'branded_status_snapshot') THEN
        ALTER TABLE visibility_checks ADD COLUMN branded_status_snapshot VARCHAR(16) NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'visibility_checks' AND column_name = 'branded_source_snapshot') THEN
        ALTER TABLE visibility_checks ADD COLUMN branded_source_snapshot VARCHAR(16) NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'visibility_checks' AND column_name = 'subject_config_version') THEN
        ALTER TABLE visibility_checks ADD COLUMN subject_config_version INT UNSIGNED NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'visibility_checks' AND column_name = 'snapshot_missing') THEN
        ALTER TABLE visibility_checks ADD COLUMN snapshot_missing BOOLEAN NOT NULL DEFAULT FALSE;
    END IF;
END//
DELIMITER ;
CALL jogeoai_005_alter();
DROP PROCEDURE jogeoai_005_alter;

INSERT IGNORE INTO prompt_versions (id, site_id, prompt_id, version, content, locale, created_at)
SELECT UUID_TO_BIN(UUID()), site_id, id, 1, content, locale, created_at FROM prompts;
UPDATE prompts p JOIN prompt_versions v ON v.prompt_id = p.id AND v.version = 1
SET p.current_version_id = v.id WHERE p.current_version_id IS NULL;

-- A legacy target is identified by its row ID. Existing checks map by (run, engine).
UPDATE run_engines SET target_key = SHA2(CONCAT('legacy:', BIN_TO_UUID(id)), 256)
WHERE target_key IS NULL;
UPDATE run_engines SET capture_mode = CASE WHEN provider = 'cloro' THEN 'scraped' ELSE 'consumer_web' END
WHERE capture_mode IS NULL;
UPDATE visibility_checks c JOIN run_engines e ON e.run_id = c.run_id AND e.engine = c.engine
SET c.run_target_id = e.id WHERE c.run_target_id IS NULL;
UPDATE visibility_checks c JOIN prompts p ON p.id = c.prompt_id
SET c.prompt_version_id = p.current_version_id, c.snapshot_missing = TRUE WHERE c.prompt_version_id IS NULL;

DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_005_keys//
CREATE PROCEDURE jogeoai_005_keys()
BEGIN
    IF EXISTS (SELECT 1 FROM visibility_checks WHERE run_target_id IS NULL) THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '005: legacy check has no matching run target';
    END IF;
    IF EXISTS (SELECT 1 FROM run_engines WHERE target_key IS NULL) THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '005: run target has no stable key';
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'run_engines'
               AND column_name = 'target_key' AND is_nullable = 'YES') THEN
        ALTER TABLE run_engines MODIFY COLUMN target_key CHAR(64) COLLATE utf8mb4_0900_bin NOT NULL;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'visibility_checks'
               AND column_name = 'run_target_id' AND is_nullable = 'YES') THEN
        ALTER TABLE visibility_checks MODIFY COLUMN run_target_id BINARY(16) NOT NULL;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'run_engines' AND index_name = 'uq_run_engines_run_engine') THEN
        IF EXISTS (SELECT 1 FROM (SELECT run_id, engine FROM run_engines GROUP BY run_id, engine HAVING COUNT(*) > 1) duplicates) THEN
            SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '005: ambiguous legacy run target';
        END IF;
        ALTER TABLE run_engines DROP INDEX uq_run_engines_run_engine;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'run_engines' AND index_name = 'uq_run_engines_run_target_key') THEN
        ALTER TABLE run_engines ADD UNIQUE KEY uq_run_engines_run_target_key (run_id, target_key),
            ADD UNIQUE KEY uq_run_engines_run_id_id (run_id, id);
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'visibility_checks' AND index_name = 'uq_visibility_checks_run_prompt_engine') THEN
        ALTER TABLE visibility_checks DROP INDEX uq_visibility_checks_run_prompt_engine;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'visibility_checks' AND index_name = 'uq_visibility_checks_run_prompt_target_sample') THEN
        ALTER TABLE visibility_checks ADD UNIQUE KEY uq_visibility_checks_run_prompt_target_sample (run_id, prompt_id, run_target_id, sample_index),
            ADD KEY ix_visibility_checks_prompt_version (prompt_version_id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE() AND table_name = 'visibility_checks' AND constraint_name = 'fk_visibility_checks_run_target') THEN
        ALTER TABLE visibility_checks ADD CONSTRAINT fk_visibility_checks_run_target FOREIGN KEY (run_id, run_target_id) REFERENCES run_engines (run_id, id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE() AND table_name = 'visibility_checks' AND constraint_name = 'fk_visibility_checks_prompt_version') THEN
        ALTER TABLE visibility_checks ADD CONSTRAINT fk_visibility_checks_prompt_version FOREIGN KEY (prompt_id, prompt_version_id) REFERENCES prompt_versions (prompt_id, id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE() AND table_name = 'prompts' AND constraint_name = 'fk_prompts_current_version') THEN
        ALTER TABLE prompts ADD CONSTRAINT fk_prompts_current_version FOREIGN KEY (id, current_version_id) REFERENCES prompt_versions (prompt_id, id);
    END IF;
END//
DELIMITER ;
CALL jogeoai_005_keys();
DROP PROCEDURE jogeoai_005_keys;

CREATE TABLE IF NOT EXISTS site_targets (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    provider VARCHAR(64) NOT NULL,
    engine VARCHAR(64) NOT NULL,
    capture_mode VARCHAR(32) NOT NULL,
    country CHAR(2) NOT NULL,
    locale VARCHAR(16) NOT NULL,
    target_key CHAR(64) COLLATE utf8mb4_0900_bin NOT NULL,
    enabled BOOLEAN NOT NULL DEFAULT TRUE,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_site_targets_key (site_id, target_key),
    KEY ix_site_targets_enabled (site_id, enabled),
    CONSTRAINT fk_site_targets_tenant_site FOREIGN KEY (tenant_id, site_id) REFERENCES sites (tenant_id, id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS collection_previews (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    selection_hash CHAR(64) COLLATE utf8mb4_0900_bin NOT NULL,
    selection_snapshot JSON NOT NULL,
    subject_config_version INT UNSIGNED NOT NULL,
    target_config_version INT UNSIGNED NOT NULL,
    expected_checks INT UNSIGNED NOT NULL,
    expires_at DATETIME(6) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    KEY ix_collection_previews_expiry (expires_at),
    CONSTRAINT fk_collection_previews_tenant_site FOREIGN KEY (tenant_id, site_id) REFERENCES sites (tenant_id, id),
    CONSTRAINT ck_collection_previews_size CHECK (OCTET_LENGTH(selection_snapshot) <= 1048576)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS prompt_imports (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    idempotency_key VARCHAR(191) COLLATE utf8mb4_0900_bin NOT NULL,
    request_hash CHAR(64) COLLATE utf8mb4_0900_bin NOT NULL,
    result JSON NOT NULL,
    created_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_prompt_imports_tenant_key (tenant_id, idempotency_key),
    CONSTRAINT fk_prompt_imports_tenant_site FOREIGN KEY (tenant_id, site_id) REFERENCES sites (tenant_id, id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS collection_schedules (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    name VARCHAR(200) NOT NULL,
    cadence VARCHAR(16) NOT NULL,
    day_of_week TINYINT UNSIGNED NULL,
    local_time TIME NOT NULL,
    time_zone VARCHAR(100) NOT NULL,
    selection_snapshot JSON NOT NULL,
    enabled BOOLEAN NOT NULL DEFAULT FALSE,
    version INT UNSIGNED NOT NULL DEFAULT 1,
    next_slot_at DATETIME(6) NULL,
    missed_slots INT UNSIGNED NOT NULL DEFAULT 0,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    KEY ix_collection_schedules_due (enabled, next_slot_at, id),
    CONSTRAINT fk_collection_schedules_tenant_site FOREIGN KEY (tenant_id, site_id) REFERENCES sites (tenant_id, id),
    CONSTRAINT ck_collection_schedules_cadence CHECK (cadence IN ('daily','weekly'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS schedule_slots (
    id BINARY(16) NOT NULL PRIMARY KEY,
    schedule_id BINARY(16) NOT NULL,
    local_slot_key VARCHAR(32) COLLATE utf8mb4_0900_bin NOT NULL,
    selection_snapshot JSON NOT NULL,
    status VARCHAR(16) NOT NULL DEFAULT 'pending',
    run_id BINARY(16) NULL,
    missed_slots INT UNSIGNED NOT NULL DEFAULT 0,
    created_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_schedule_slots_local (schedule_id, local_slot_key),
    KEY ix_schedule_slots_pending (status, created_at),
    CONSTRAINT fk_schedule_slots_schedule FOREIGN KEY (schedule_id) REFERENCES collection_schedules (id),
    CONSTRAINT fk_schedule_slots_run FOREIGN KEY (run_id) REFERENCES visibility_runs (id),
    CONSTRAINT ck_schedule_slots_status CHECK (status IN ('pending','completed','skipped')),
    CONSTRAINT ck_schedule_slots_snapshot CHECK (OCTET_LENGTH(selection_snapshot) <= 1048576)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
