-- Dashboard analytics compatibility and URL-v2 rebuild. Execute manually after 007.
-- This migration never rewrites a published release. It creates one complete
-- url-v2 release per site and leaves the previous url-v1 snapshot readable.

DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_011_columns//
CREATE PROCEDURE jogeoai_011_columns()
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'analysis_releases' AND column_name = 'url_normalization_version') THEN
        ALTER TABLE analysis_releases ADD COLUMN url_normalization_version VARCHAR(32) NOT NULL
            DEFAULT 'url-v1' AFTER detail_version;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'citation_events' AND column_name = 'normalization_status') THEN
        ALTER TABLE citation_events ADD COLUMN normalization_status VARCHAR(24) NOT NULL
            DEFAULT 'complete' AFTER url_version;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE()
        AND table_name = 'citation_events' AND index_name = 'ix_citation_events_normalization') THEN
        CREATE INDEX ix_citation_events_normalization
            ON citation_events (release_id, normalization_status, check_id);
    END IF;
END//
DELIMITER ;
CALL jogeoai_011_columns();
DROP PROCEDURE jogeoai_011_columns;

UPDATE citation_events
SET normalization_status = 'normalization_pending'
WHERE url_version <> 'url-v2'
  AND normalized_url REGEXP '[?&](utm_[^=&]*|gclid|dclid|gbraid|wbraid|fbclid|msclkid|twclid|li_fat_id|mc_cid|mc_eid|igshid|_ga|_gl)(=|&|$)'
  AND normalization_status = 'complete';

CREATE TEMPORARY TABLE jogeoai_011_rebuild (
    site_id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    old_release_id BINARY(16) NOT NULL,
    release_id BINARY(16) NOT NULL,
    job_id BINARY(16) NOT NULL,
    next_version INT UNSIGNED NOT NULL
) ENGINE=InnoDB;

START TRANSACTION;

INSERT INTO jogeoai_011_rebuild (site_id, tenant_id, old_release_id, release_id, job_id, next_version)
SELECT s.site_id, s.tenant_id, s.published_release_id, UUID_TO_BIN(UUID()), UUID_TO_BIN(UUID()),
       (SELECT COALESCE(MAX(r2.version), 0) + 1 FROM analysis_releases r2 WHERE r2.site_id = s.site_id)
FROM site_analysis_state s
JOIN analysis_releases old ON old.id = s.published_release_id
WHERE old.url_normalization_version <> 'url-v2'
  AND NOT EXISTS (
      SELECT 1 FROM analysis_jobs j
      WHERE j.site_id = s.site_id
        AND j.idempotency_key = CONCAT('migration-011-url-normalization-', BIN_TO_UUID(s.site_id))
  );

INSERT INTO analysis_releases (id, tenant_id, site_id, version, subject_config_version,
    subject_snapshot, extractor_version, metric_version, detail_version,
    url_normalization_version, status, created_at)
SELECT m.release_id, m.tenant_id, m.site_id, m.next_version, old.subject_config_version,
    old.subject_snapshot, old.extractor_version, old.metric_version, 'detail-v2',
    'url-v2', 'building', UTC_TIMESTAMP(6)
FROM jogeoai_011_rebuild m JOIN analysis_releases old ON old.id = m.old_release_id;

INSERT INTO analysis_jobs (id, tenant_id, site_id, release_id, idempotency_key, request_hash,
    scope_json, status, total_checks, created_at, updated_at)
SELECT m.job_id, m.tenant_id, m.site_id, m.release_id,
    CONCAT('migration-011-url-normalization-', BIN_TO_UUID(m.site_id)), REPEAT('0', 64),
    JSON_OBJECT('effective', 'migration_011_url_v2_rebuild', 'previousReleaseId', BIN_TO_UUID(m.old_release_id)),
    'pending', COUNT(c.id), UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)
FROM jogeoai_011_rebuild m
LEFT JOIN visibility_checks c ON c.site_id = m.site_id AND c.tenant_id = m.tenant_id
    AND c.status = 'succeeded'
GROUP BY m.job_id, m.tenant_id, m.site_id, m.release_id, m.old_release_id;

INSERT INTO analysis_check_work (release_id, check_id, tenant_id, site_id, created_at, updated_at)
SELECT m.release_id, c.id, c.tenant_id, c.site_id, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)
FROM jogeoai_011_rebuild m
JOIN visibility_checks c ON c.site_id = m.site_id AND c.tenant_id = m.tenant_id
    AND c.status = 'succeeded';

-- Replay also repairs an interrupted migration that created its release/job
-- before the process stopped, without creating a second release.
INSERT IGNORE INTO analysis_check_work (release_id, check_id, tenant_id, site_id, created_at, updated_at)
SELECT j.release_id, c.id, c.tenant_id, c.site_id, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)
FROM analysis_jobs j JOIN analysis_releases r ON r.id = j.release_id
JOIN visibility_checks c ON c.site_id = j.site_id AND c.tenant_id = j.tenant_id
    AND c.status = 'succeeded'
WHERE j.idempotency_key = CONCAT('migration-011-url-normalization-', BIN_TO_UUID(j.site_id))
  AND r.status = 'building';

UPDATE analysis_jobs j
SET j.total_checks = (SELECT COUNT(*) FROM analysis_check_work w WHERE w.release_id = j.release_id)
WHERE j.idempotency_key = CONCAT('migration-011-url-normalization-', BIN_TO_UUID(j.site_id))
  AND j.status IN ('pending', 'processing');

UPDATE site_analysis_state s
JOIN jogeoai_011_rebuild m ON m.site_id = s.site_id
JOIN analysis_jobs j ON j.id = m.job_id
JOIN analysis_releases r ON r.id = m.release_id
SET s.published_release_id = r.id, s.updated_at = UTC_TIMESTAMP(6),
    r.status = 'published', r.published_at = UTC_TIMESTAMP(6),
    j.status = 'published', j.published_at = UTC_TIMESTAMP(6)
WHERE j.total_checks = 0;

COMMIT;
DROP TEMPORARY TABLE jogeoai_011_rebuild;
