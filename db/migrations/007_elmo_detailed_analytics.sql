-- Versioned search and citation facts. Apply after 006 with old API/Worker stopped.
DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_007_columns//
CREATE PROCEDURE jogeoai_007_columns()
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'visibility_checks' AND column_name = 'query_availability') THEN
        ALTER TABLE visibility_checks ADD COLUMN query_availability VARCHAR(16) NOT NULL DEFAULT 'unknown';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'analysis_releases' AND column_name = 'detail_version') THEN
        ALTER TABLE analysis_releases ADD COLUMN detail_version VARCHAR(32) NOT NULL DEFAULT 'unavailable';
    END IF;
END//
DELIMITER ;
CALL jogeoai_007_columns();
DROP PROCEDURE jogeoai_007_columns;

CREATE TABLE IF NOT EXISTS check_query_events (
    release_id BINARY(16) NOT NULL,
    check_id BINARY(16) NOT NULL,
    query_hash CHAR(64) COLLATE utf8mb4_0900_bin NOT NULL,
    query_key VARCHAR(2048) COLLATE utf8mb4_0900_bin NOT NULL,
    original_query VARCHAR(2048) NOT NULL,
    first_position INT UNSIGNED NOT NULL,
    included BOOLEAN NOT NULL,
    exclusion_reason VARCHAR(32) NULL,
    normalization_version VARCHAR(32) NOT NULL,
    PRIMARY KEY (release_id, check_id, query_hash),
    KEY ix_query_key (release_id, query_hash, check_id),
    KEY ix_query_included (release_id, included, check_id),
    CONSTRAINT fk_query_check_analysis FOREIGN KEY (release_id, check_id)
        REFERENCES check_analysis (release_id, check_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS query_term_facts (
    release_id BINARY(16) NOT NULL,
    check_id BINARY(16) NOT NULL,
    query_hash CHAR(64) COLLATE utf8mb4_0900_bin NOT NULL,
    term_hash CHAR(64) COLLATE utf8mb4_0900_bin NOT NULL,
    term VARCHAR(256) COLLATE utf8mb4_0900_bin NOT NULL,
    relation VARCHAR(16) NOT NULL,
    tokenizer_version VARCHAR(32) NOT NULL,
    PRIMARY KEY (release_id, check_id, query_hash, term_hash),
    KEY ix_query_terms_term (release_id, term_hash, check_id),
    CONSTRAINT fk_query_term_event FOREIGN KEY (release_id, check_id, query_hash)
        REFERENCES check_query_events (release_id, check_id, query_hash)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS citation_events (
    release_id BINARY(16) NOT NULL,
    check_id BINARY(16) NOT NULL,
    url_hash CHAR(64) COLLATE utf8mb4_0900_bin NOT NULL,
    normalized_url VARCHAR(750) COLLATE utf8mb4_0900_bin NOT NULL,
    domain VARCHAR(253) COLLATE utf8mb4_0900_bin NOT NULL,
    title VARCHAR(512) NULL,
    first_position INT UNSIGNED NOT NULL,
    url_version VARCHAR(32) NOT NULL,
    PRIMARY KEY (release_id, check_id, url_hash),
    KEY ix_citation_url (release_id, url_hash, check_id),
    KEY ix_citation_domain (release_id, domain, check_id),
    CONSTRAINT fk_citation_event_analysis FOREIGN KEY (release_id, check_id)
        REFERENCES check_analysis (release_id, check_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS citation_classifications (
    release_id BINARY(16) NOT NULL,
    check_id BINARY(16) NOT NULL,
    url_hash CHAR(64) COLLATE utf8mb4_0900_bin NOT NULL,
    source_category VARCHAR(24) NOT NULL,
    page_type VARCHAR(24) NOT NULL,
    ownership VARCHAR(24) NOT NULL,
    subject_id BINARY(16) NULL,
    rule_version VARCHAR(32) NOT NULL,
    rule_basis VARCHAR(64) NOT NULL,
    PRIMARY KEY (release_id, check_id, url_hash),
    KEY ix_citation_class_category (release_id, source_category, check_id),
    KEY ix_citation_class_subject (release_id, subject_id, check_id),
    CONSTRAINT fk_citation_class_event FOREIGN KEY (release_id, check_id, url_hash)
        REFERENCES citation_events (release_id, check_id, url_hash)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS google_internal_events (
    release_id BINARY(16) NOT NULL,
    check_id BINARY(16) NOT NULL,
    url_hash CHAR(64) COLLATE utf8mb4_0900_bin NOT NULL,
    normalized_url VARCHAR(750) COLLATE utf8mb4_0900_bin NOT NULL,
    kind VARCHAR(16) NOT NULL,
    search_query VARCHAR(2048) NULL,
    product VARCHAR(512) NULL,
    availability VARCHAR(16) NOT NULL,
    ownership VARCHAR(24) NULL,
    subject_id BINARY(16) NULL,
    first_position INT UNSIGNED NOT NULL,
    rule_version VARCHAR(32) NOT NULL,
    PRIMARY KEY (release_id, check_id, url_hash),
    KEY ix_google_kind (release_id, kind, check_id),
    CONSTRAINT fk_google_event_analysis FOREIGN KEY (release_id, check_id)
        REFERENCES check_analysis (release_id, check_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- Old empty arrays cannot reveal whether the Provider exposed a query field.
UPDATE visibility_checks SET query_availability = 'available'
WHERE JSON_LENGTH(search_queries) > 0 AND query_availability = 'unknown';

-- Published 006 releases remain immutable. Queue one complete detail-v1 rebuild
-- for each existing site; the Worker publishes only after every fact commits.
CREATE TEMPORARY TABLE jogeoai_007_rebuild (
    site_id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    release_id BINARY(16) NOT NULL,
    job_id BINARY(16) NOT NULL,
    next_version INT UNSIGNED NOT NULL
) ENGINE=InnoDB;

START TRANSACTION;

-- A previous interrupted run may have inserted a release before its job.
-- Such a release can never be claimed, so preserve it as failed and retry.
UPDATE analysis_releases r LEFT JOIN analysis_jobs j ON j.release_id = r.id
SET r.status = 'failed'
WHERE r.detail_version = 'detail-v1' AND r.status = 'building' AND j.id IS NULL;

INSERT INTO jogeoai_007_rebuild (site_id, tenant_id, release_id, job_id, next_version)
SELECT s.site_id, s.tenant_id, UUID_TO_BIN(UUID()), UUID_TO_BIN(UUID()),
       (SELECT COALESCE(MAX(version), 0) + 1 FROM analysis_releases r2 WHERE r2.site_id = s.site_id)
FROM site_analysis_state s JOIN analysis_releases r ON r.id = s.published_release_id
WHERE r.detail_version = 'unavailable'
  AND NOT EXISTS (SELECT 1 FROM analysis_jobs j WHERE j.site_id = s.site_id
                  AND j.idempotency_key IN ('migration-007-detailed-analytics',
                      CONCAT('migration-007-detailed-analytics-', BIN_TO_UUID(s.site_id))));

INSERT INTO analysis_releases (id, tenant_id, site_id, version, subject_config_version,
    subject_snapshot, extractor_version, metric_version, detail_version, status, created_at)
SELECT m.release_id, m.tenant_id, m.site_id, m.next_version, r.subject_config_version,
       r.subject_snapshot, r.extractor_version, r.metric_version, 'detail-v1', 'building', UTC_TIMESTAMP(6)
FROM jogeoai_007_rebuild m JOIN site_analysis_state s ON s.site_id = m.site_id
JOIN analysis_releases r ON r.id = s.published_release_id;

INSERT INTO analysis_jobs (id, tenant_id, site_id, release_id, idempotency_key, request_hash,
    scope_json, status, created_at, updated_at)
SELECT m.job_id, m.tenant_id, m.site_id, m.release_id,
       CONCAT('migration-007-detailed-analytics-', BIN_TO_UUID(m.site_id)), REPEAT('0', 64),
       JSON_OBJECT('effective', 'migration_007_full_rebuild'), 'pending', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)
FROM jogeoai_007_rebuild m;

-- Also resume an older interrupted run that had inserted its job but not all work.
INSERT INTO analysis_check_work (release_id, check_id, tenant_id, site_id, created_at, updated_at)
SELECT j.release_id, c.id, c.tenant_id, c.site_id, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)
FROM analysis_jobs j JOIN analysis_releases r ON r.id = j.release_id
JOIN visibility_checks c ON c.site_id = j.site_id
WHERE r.detail_version = 'detail-v1' AND r.status = 'building'
  AND j.idempotency_key LIKE 'migration-007-detailed-analytics%'
  AND c.status = 'succeeded'
  AND NOT EXISTS (SELECT 1 FROM analysis_check_work w
                  WHERE w.release_id = j.release_id AND w.check_id = c.id);

UPDATE analysis_jobs j JOIN analysis_releases r ON r.id = j.release_id
SET j.total_checks = (SELECT COUNT(*) FROM analysis_check_work w WHERE w.release_id = j.release_id)
WHERE r.detail_version = 'detail-v1' AND r.status = 'building'
  AND j.idempotency_key LIKE 'migration-007-detailed-analytics%';

-- Empty sites can publish immediately; no facts are needed.
UPDATE site_analysis_state s JOIN analysis_jobs j ON j.site_id = s.site_id
JOIN analysis_releases r ON r.id = j.release_id
SET s.published_release_id = r.id, s.updated_at = UTC_TIMESTAMP(6),
    r.status = 'published', r.published_at = UTC_TIMESTAMP(6),
    j.status = 'published', j.published_at = UTC_TIMESTAMP(6)
WHERE r.detail_version = 'detail-v1' AND r.status = 'building'
  AND j.idempotency_key LIKE 'migration-007-detailed-analytics%'
  AND j.total_checks = 0;

COMMIT;
DROP TEMPORARY TABLE jogeoai_007_rebuild;
