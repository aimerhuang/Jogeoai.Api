-- Versioned answer analysis. Deploy after 005 with old API/Worker processes stopped.
-- All inserts are idempotent; a replay retains published releases and evidence.

DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_006_check_key//
CREATE PROCEDURE jogeoai_006_check_key()
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE()
                   AND table_name = 'visibility_checks' AND index_name = 'uq_visibility_checks_tenant_site_id') THEN
        ALTER TABLE visibility_checks ADD UNIQUE KEY uq_visibility_checks_tenant_site_id (tenant_id, site_id, id);
    END IF;
END//
DELIMITER ;
CALL jogeoai_006_check_key();
DROP PROCEDURE jogeoai_006_check_key;

CREATE TABLE IF NOT EXISTS analysis_releases (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    version INT UNSIGNED NOT NULL,
    subject_config_version INT UNSIGNED NOT NULL,
    subject_snapshot JSON NOT NULL,
    extractor_version VARCHAR(32) NOT NULL,
    metric_version VARCHAR(32) NOT NULL,
    status VARCHAR(16) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    published_at DATETIME(6) NULL,
    UNIQUE KEY uq_analysis_releases_site_version (site_id, version),
    UNIQUE KEY uq_analysis_releases_site_id (site_id, id),
    KEY ix_analysis_releases_site_status (site_id, status, version),
    CONSTRAINT fk_analysis_releases_site FOREIGN KEY (tenant_id, site_id) REFERENCES sites (tenant_id, id),
    CONSTRAINT ck_analysis_release_snapshot CHECK (OCTET_LENGTH(subject_snapshot) <= 1048576)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS site_analysis_state (
    site_id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    published_release_id BINARY(16) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_site_analysis_state_tenant_site (tenant_id, site_id),
    CONSTRAINT fk_site_analysis_state_site FOREIGN KEY (tenant_id, site_id) REFERENCES sites (tenant_id, id),
    CONSTRAINT fk_site_analysis_state_release FOREIGN KEY (site_id, published_release_id) REFERENCES analysis_releases (site_id, id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS analytics_check_arrivals (
    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
    check_id BINARY(16) NOT NULL,
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    captured_at DATETIME(6) NULL,
    arrived_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_analytics_arrival_check (check_id),
    KEY ix_analytics_arrivals_site_id (tenant_id, site_id, id),
    KEY ix_analytics_arrivals_site_capture (site_id, captured_at, id),
    CONSTRAINT fk_analytics_arrivals_check FOREIGN KEY (tenant_id, site_id, check_id)
        REFERENCES visibility_checks (tenant_id, site_id, id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS analysis_check_work (
    release_id BINARY(16) NOT NULL,
    check_id BINARY(16) NOT NULL,
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    status VARCHAR(16) NOT NULL DEFAULT 'pending',
    attempt_count INT UNSIGNED NOT NULL DEFAULT 0,
    lease_owner VARCHAR(128) NULL,
    lease_version INT UNSIGNED NOT NULL DEFAULT 0,
    lease_until DATETIME(6) NULL,
    error_code VARCHAR(64) NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    PRIMARY KEY (release_id, check_id),
    KEY ix_analysis_work_claim (status, lease_until, updated_at),
    KEY ix_analysis_work_site_release_status (site_id, release_id, status),
    CONSTRAINT fk_analysis_work_release FOREIGN KEY (site_id, release_id) REFERENCES analysis_releases (site_id, id),
    CONSTRAINT fk_analysis_work_check FOREIGN KEY (tenant_id, site_id, check_id)
        REFERENCES visibility_checks (tenant_id, site_id, id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS analysis_events (
    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
    release_id BINARY(16) NOT NULL,
    check_id BINARY(16) NOT NULL,
    completed_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_analysis_events_release_check (release_id, check_id),
    CONSTRAINT fk_analysis_events_work FOREIGN KEY (release_id, check_id) REFERENCES analysis_check_work (release_id, check_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS check_analysis (
    release_id BINARY(16) NOT NULL,
    check_id BINARY(16) NOT NULL,
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    status VARCHAR(16) NOT NULL,
    configuration_missing BOOLEAN NOT NULL DEFAULT FALSE,
    completed_event_id BIGINT UNSIGNED NULL,
    completed_at DATETIME(6) NULL,
    PRIMARY KEY (release_id, check_id),
    KEY ix_check_analysis_site_event (site_id, completed_event_id),
    CONSTRAINT fk_check_analysis_work FOREIGN KEY (release_id, check_id) REFERENCES analysis_check_work (release_id, check_id),
    CONSTRAINT fk_check_analysis_site FOREIGN KEY (tenant_id, site_id) REFERENCES sites (tenant_id, id),
    CONSTRAINT fk_check_analysis_event FOREIGN KEY (completed_event_id) REFERENCES analysis_events (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS check_subject_presence (
    release_id BINARY(16) NOT NULL,
    check_id BINARY(16) NOT NULL,
    subject_id BINARY(16) NOT NULL,
    subject_type VARCHAR(16) NOT NULL,
    first_offset INT UNSIGNED NOT NULL,
    PRIMARY KEY (release_id, check_id, subject_id),
    KEY ix_presence_release_subject (release_id, subject_id, check_id),
    CONSTRAINT fk_presence_analysis FOREIGN KEY (release_id, check_id) REFERENCES check_analysis (release_id, check_id),
    CONSTRAINT ck_presence_type CHECK (subject_type IN ('brand','competitor'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS mention_evidence (
    id BINARY(16) NOT NULL PRIMARY KEY,
    release_id BINARY(16) NOT NULL,
    check_id BINARY(16) NOT NULL,
    subject_id BINARY(16) NOT NULL,
    subject_type VARCHAR(16) NOT NULL,
    matched_text VARCHAR(512) NOT NULL,
    snippet VARCHAR(600) NOT NULL,
    match_basis VARCHAR(32) NOT NULL,
    offset_start INT UNSIGNED NOT NULL,
    offset_end INT UNSIGNED NOT NULL,
    extractor_version VARCHAR(32) NOT NULL,
    subject_config_version INT UNSIGNED NOT NULL,
    created_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_mention_evidence_position (release_id, check_id, subject_id, offset_start, offset_end),
    KEY ix_mention_evidence_check_offset (check_id, release_id, offset_start),
    CONSTRAINT fk_mention_evidence_presence FOREIGN KEY (release_id, check_id, subject_id)
        REFERENCES check_subject_presence (release_id, check_id, subject_id),
    CONSTRAINT ck_mention_evidence_offset CHECK (offset_end > offset_start)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS analysis_jobs (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    release_id BINARY(16) NOT NULL,
    idempotency_key VARCHAR(191) COLLATE utf8mb4_0900_bin NOT NULL,
    request_hash CHAR(64) COLLATE utf8mb4_0900_bin NOT NULL,
    scope_json JSON NOT NULL,
    status VARCHAR(16) NOT NULL,
    total_checks INT UNSIGNED NOT NULL DEFAULT 0,
    reused_checks INT UNSIGNED NOT NULL DEFAULT 0,
    completed_checks INT UNSIGNED NOT NULL DEFAULT 0,
    failed_checks INT UNSIGNED NOT NULL DEFAULT 0,
    error_summary VARCHAR(1000) NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    published_at DATETIME(6) NULL,
    UNIQUE KEY uq_analysis_jobs_tenant_key (tenant_id, idempotency_key),
    KEY ix_analysis_jobs_site_status (site_id, status, created_at),
    CONSTRAINT fk_analysis_jobs_release FOREIGN KEY (site_id, release_id) REFERENCES analysis_releases (site_id, id),
    CONSTRAINT fk_analysis_jobs_site FOREIGN KEY (tenant_id, site_id) REFERENCES sites (tenant_id, id),
    CONSTRAINT ck_analysis_jobs_scope CHECK (OCTET_LENGTH(scope_json) <= 65536)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS analytics_snapshots (
    token_hash CHAR(64) COLLATE utf8mb4_0900_bin NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    release_id BINARY(16) NOT NULL,
    subject_config_version INT UNSIGNED NOT NULL,
    current_subject_config_version INT UNSIGNED NOT NULL,
    filter_hash CHAR(64) COLLATE utf8mb4_0900_bin NOT NULL,
    filter_json JSON NOT NULL,
    arrival_watermark BIGINT UNSIGNED NOT NULL,
    analysis_watermark BIGINT UNSIGNED NOT NULL,
    as_of DATETIME(6) NOT NULL,
    expires_at DATETIME(6) NOT NULL,
    KEY ix_analytics_snapshots_expiry (expires_at),
    CONSTRAINT fk_analytics_snapshots_release FOREIGN KEY (site_id, release_id) REFERENCES analysis_releases (site_id, id),
    CONSTRAINT fk_analytics_snapshots_site FOREIGN KEY (tenant_id, site_id) REFERENCES sites (tenant_id, id),
    CONSTRAINT ck_analytics_snapshot_filter CHECK (OCTET_LENGTH(filter_json) <= 65536)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

INSERT IGNORE INTO analysis_releases (id, tenant_id, site_id, version, subject_config_version,
    subject_snapshot, extractor_version, metric_version, status, created_at, published_at)
SELECT UUID_TO_BIN(UUID()), r.tenant_id, r.site_id, 1, r.version, r.snapshot,
       'mention-v1', 'mentions-v1', 'published', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)
FROM subject_config_revisions r JOIN site_brand_settings b ON b.site_id = r.site_id
WHERE r.version = b.subject_config_version;

INSERT IGNORE INTO site_analysis_state (site_id, tenant_id, published_release_id, updated_at)
SELECT site_id, tenant_id, id, UTC_TIMESTAMP(6) FROM analysis_releases WHERE version = 1;

INSERT IGNORE INTO analytics_check_arrivals (check_id, tenant_id, site_id, captured_at, arrived_at)
SELECT id, tenant_id, site_id, captured_at, updated_at FROM visibility_checks
WHERE status IN ('succeeded','failed','cancelled');

INSERT IGNORE INTO analysis_check_work (release_id, check_id, tenant_id, site_id, created_at, updated_at)
SELECT s.published_release_id, c.id, c.tenant_id, c.site_id, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)
FROM visibility_checks c JOIN site_analysis_state s ON s.site_id = c.site_id
WHERE c.status = 'succeeded' AND c.answer IS NOT NULL AND TRIM(c.answer) <> '';
