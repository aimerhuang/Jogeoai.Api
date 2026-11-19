-- Jogeoai Cloro visibility business contract. Run explicitly after 002 (and 003 when present).
-- Additive/replay-safe; application startup does not execute this migration.
DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_004_prepare_schema//
CREATE PROCEDURE jogeoai_004_prepare_schema()
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'visibility_runs')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'visibility_runs' AND column_name = 'request_hash') THEN
        ALTER TABLE visibility_runs ADD COLUMN request_hash CHAR(64) NULL;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'provider_tasks')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'provider_tasks' AND column_name = 'check_id') THEN
        ALTER TABLE provider_tasks ADD COLUMN check_id BINARY(16) NULL;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'provider_tasks')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'provider_tasks' AND column_name = 'request_hash') THEN
        ALTER TABLE provider_tasks ADD COLUMN request_hash CHAR(64) NULL;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'provider_tasks')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'provider_tasks' AND column_name = 'submitted_at') THEN
        ALTER TABLE provider_tasks ADD COLUMN submitted_at DATETIME(6) NULL;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'provider_tasks')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'provider_tasks' AND column_name = 'started_at') THEN
        ALTER TABLE provider_tasks ADD COLUMN started_at DATETIME(6) NULL;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'provider_tasks')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'provider_tasks' AND column_name = 'last_polled_at') THEN
        ALTER TABLE provider_tasks ADD COLUMN last_polled_at DATETIME(6) NULL;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'provider_tasks')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'provider_tasks' AND column_name = 'completed_at') THEN
        ALTER TABLE provider_tasks ADD COLUMN completed_at DATETIME(6) NULL;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'provider_tasks')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'provider_tasks' AND column_name = 'lease_version') THEN
        ALTER TABLE provider_tasks ADD COLUMN lease_version INT UNSIGNED NOT NULL DEFAULT 0;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'visibility_checks')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'visibility_checks' AND column_name = 'tenant_id') THEN
        ALTER TABLE visibility_checks ADD COLUMN tenant_id BINARY(16) NULL;
        UPDATE visibility_checks c JOIN visibility_runs r ON r.id = c.run_id SET c.tenant_id = r.tenant_id WHERE c.tenant_id IS NULL;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'visibility_checks')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'visibility_checks' AND column_name = 'capture_mode') THEN
        ALTER TABLE visibility_checks ADD COLUMN capture_mode VARCHAR(32) NULL;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'visibility_checks')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'visibility_checks' AND column_name = 'evidence_class') THEN
        ALTER TABLE visibility_checks ADD COLUMN evidence_class VARCHAR(64) NULL;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'visibility_checks')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'visibility_checks' AND column_name = 'locale') THEN
        ALTER TABLE visibility_checks ADD COLUMN locale VARCHAR(16) NULL;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'visibility_checks')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'visibility_checks' AND column_name = 'country') THEN
        ALTER TABLE visibility_checks ADD COLUMN country CHAR(2) NULL;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'visibility_checks')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'visibility_checks' AND column_name = 'answer_present') THEN
        ALTER TABLE visibility_checks ADD COLUMN answer_present BOOLEAN NULL;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'visibility_checks')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'visibility_checks' AND column_name = 'parser_version') THEN
        ALTER TABLE visibility_checks ADD COLUMN parser_version VARCHAR(32) NULL;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'visibility_checks')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'visibility_checks' AND column_name = 'raw_response_sha256') THEN
        ALTER TABLE visibility_checks ADD COLUMN raw_response_sha256 CHAR(64) NULL;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'visibility_checks')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'visibility_checks' AND column_name = 'captured_at') THEN
        ALTER TABLE visibility_checks ADD COLUMN captured_at DATETIME(6) NULL;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'visibility_checks')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'visibility_checks' AND column_name = 'result_hash') THEN
        ALTER TABLE visibility_checks ADD COLUMN result_hash CHAR(64) NULL;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'usage_ledger')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'usage_ledger' AND column_name = 'provider_task_id') THEN
        ALTER TABLE usage_ledger ADD COLUMN provider_task_id BINARY(16) NULL;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'usage_ledger')
       AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'usage_ledger' AND column_name = 'cost_status') THEN
        ALTER TABLE usage_ledger ADD COLUMN cost_status VARCHAR(16) NOT NULL DEFAULT 'unknown';
    END IF;
END//
DELIMITER ;
CALL jogeoai_004_prepare_schema();
DROP PROCEDURE jogeoai_004_prepare_schema;

DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_004_tenant_check_boundary//
CREATE PROCEDURE jogeoai_004_tenant_check_boundary()
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'visibility_checks' AND column_name = 'tenant_id'
               AND is_nullable = 'YES') THEN
        UPDATE visibility_checks c JOIN visibility_runs r ON r.id = c.run_id
        SET c.tenant_id = r.tenant_id WHERE c.tenant_id IS NULL;
        ALTER TABLE visibility_checks MODIFY COLUMN tenant_id BINARY(16) NOT NULL;
    END IF;
END//
DELIMITER ;
CALL jogeoai_004_tenant_check_boundary();
DROP PROCEDURE jogeoai_004_tenant_check_boundary;

DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_004_add_constraints//
CREATE PROCEDURE jogeoai_004_add_constraints()
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'visibility_runs')
       AND NOT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE() AND table_name = 'visibility_runs' AND constraint_name = 'ck_visibility_runs_request_hash') THEN
        ALTER TABLE visibility_runs ADD CONSTRAINT ck_visibility_runs_request_hash CHECK (request_hash IS NULL OR CHAR_LENGTH(request_hash) = 64);
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'provider_tasks')
       AND NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'provider_tasks' AND index_name = 'uq_provider_tasks_check_provider') THEN
        ALTER TABLE provider_tasks ADD UNIQUE KEY uq_provider_tasks_check_provider (check_id, provider);
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'provider_tasks')
       AND NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'provider_tasks' AND index_name = 'ix_provider_tasks_check') THEN
        ALTER TABLE provider_tasks ADD KEY ix_provider_tasks_check (check_id);
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'visibility_checks')
       AND NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'visibility_checks' AND index_name = 'uq_visibility_checks_run_id_id') THEN
        ALTER TABLE visibility_checks ADD UNIQUE KEY uq_visibility_checks_run_id_id (run_id, id);
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'provider_tasks')
       AND NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'provider_tasks' AND index_name = 'ix_provider_tasks_run_check') THEN
        ALTER TABLE provider_tasks ADD KEY ix_provider_tasks_run_check (run_id, check_id);
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'provider_tasks')
       AND NOT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE() AND table_name = 'provider_tasks' AND constraint_name = 'fk_provider_tasks_check') THEN
        ALTER TABLE provider_tasks ADD CONSTRAINT fk_provider_tasks_check FOREIGN KEY (check_id) REFERENCES visibility_checks (id);
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'provider_tasks')
       AND EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'provider_tasks' AND index_name = 'ix_provider_tasks_run_check')
       AND EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'visibility_checks' AND index_name = 'uq_visibility_checks_run_id_id')
       AND NOT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE() AND table_name = 'provider_tasks' AND constraint_name = 'fk_provider_tasks_run_check') THEN
        ALTER TABLE provider_tasks ADD CONSTRAINT fk_provider_tasks_run_check FOREIGN KEY (run_id, check_id) REFERENCES visibility_checks (run_id, id);
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'provider_tasks')
       AND NOT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE() AND table_name = 'provider_tasks' AND constraint_name = 'ck_provider_tasks_request_hash') THEN
        ALTER TABLE provider_tasks ADD CONSTRAINT ck_provider_tasks_request_hash CHECK (request_hash IS NULL OR CHAR_LENGTH(request_hash) = 64);
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'visibility_checks')
       AND NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'visibility_checks' AND index_name = 'ix_visibility_checks_tenant_created') THEN
        ALTER TABLE visibility_checks ADD KEY ix_visibility_checks_tenant_created (tenant_id, created_at, id);
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'visibility_checks')
       AND NOT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE() AND table_name = 'visibility_checks' AND constraint_name = 'fk_visibility_checks_tenant') THEN
        ALTER TABLE visibility_checks ADD CONSTRAINT fk_visibility_checks_tenant FOREIGN KEY (tenant_id) REFERENCES tenants (id);
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'visibility_checks')
       AND NOT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE() AND table_name = 'visibility_checks' AND constraint_name = 'fk_visibility_checks_tenant_run') THEN
        ALTER TABLE visibility_checks ADD CONSTRAINT fk_visibility_checks_tenant_run FOREIGN KEY (tenant_id, run_id) REFERENCES visibility_runs (tenant_id, id);
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'usage_ledger')
       AND NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'usage_ledger' AND index_name = 'ix_usage_ledger_provider_task') THEN
        ALTER TABLE usage_ledger ADD KEY ix_usage_ledger_provider_task (provider_task_id);
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'usage_ledger')
       AND NOT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE() AND table_name = 'usage_ledger' AND constraint_name = 'fk_usage_ledger_provider_task') THEN
        ALTER TABLE usage_ledger ADD CONSTRAINT fk_usage_ledger_provider_task FOREIGN KEY (provider_task_id) REFERENCES provider_tasks (id);
    END IF;
END//
DELIMITER ;
CALL jogeoai_004_add_constraints();
DROP PROCEDURE jogeoai_004_add_constraints;

-- New Cloro records are validated in the application; these checks only constrain
-- non-null values and intentionally preserve nullable domestic historical rows.
DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_004_add_checks//
CREATE PROCEDURE jogeoai_004_add_checks()
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'visibility_checks')
       AND NOT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE() AND table_name = 'visibility_checks' AND constraint_name = 'ck_visibility_checks_capture_mode') THEN
        ALTER TABLE visibility_checks ADD CONSTRAINT ck_visibility_checks_capture_mode CHECK (capture_mode IS NULL OR capture_mode IN ('api','scraped'));
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'visibility_checks')
       AND NOT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE() AND table_name = 'visibility_checks' AND constraint_name = 'ck_visibility_checks_country') THEN
        ALTER TABLE visibility_checks ADD CONSTRAINT ck_visibility_checks_country CHECK (country IS NULL OR CHAR_LENGTH(country) = 2);
    END IF;
END//
DELIMITER ;
CALL jogeoai_004_add_checks();
DROP PROCEDURE jogeoai_004_add_checks;
