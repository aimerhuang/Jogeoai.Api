-- Additive only: no historical metadata/activation backfill and no task creation.
DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_021_gemini_result_quality//
CREATE PROCEDURE jogeoai_021_gemini_result_quality()
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'visibility_checks' AND column_name = 'result_metadata') THEN
        ALTER TABLE visibility_checks ADD COLUMN result_metadata JSON NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE()
        AND table_name = 'visibility_checks' AND constraint_name = 'chk_visibility_result_metadata_size') THEN
        ALTER TABLE visibility_checks ADD CONSTRAINT chk_visibility_result_metadata_size
            CHECK (result_metadata IS NULL OR OCTET_LENGTH(result_metadata) <= 16384);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'site_targets' AND column_name = 'monitoring_enabled_at') THEN
        ALTER TABLE site_targets ADD COLUMN monitoring_enabled_at DATETIME(6) NULL;
    END IF;
END//
DELIMITER ;
CALL jogeoai_021_gemini_result_quality();
DROP PROCEDURE jogeoai_021_gemini_result_quality;
