-- 019_prompt_monitoring_credits.sql
-- Prompt management invariants and daily monitoring credit accounting.
-- Run after 005, 014 and 018. This migration is additive and replay-safe.

CREATE TABLE IF NOT EXISTS monitoring_credit_reservations (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    run_id BINARY(16) NOT NULL,
    unit_price DECIMAL(18,3) NOT NULL DEFAULT 10,
    estimated_credits DECIMAL(18,3) NOT NULL,
    held_credits DECIMAL(18,3) NOT NULL DEFAULT 0,
    captured_credits DECIMAL(18,3) NOT NULL DEFAULT 0,
    released_credits DECIMAL(18,3) NOT NULL DEFAULT 0,
    status VARCHAR(16) NOT NULL DEFAULT 'held',
    reason VARCHAR(500) NULL,
    created_at DATETIME(6) NOT NULL,
    captured_at DATETIME(6) NULL,
    released_at DATETIME(6) NULL,
    UNIQUE KEY uq_monitoring_credit_reservations_tenant_run (tenant_id, run_id),
    UNIQUE KEY uq_monitoring_credit_reservations_tenant_id (tenant_id, id),
    KEY ix_monitoring_credit_reservations_site_created (tenant_id, site_id, created_at),
    CONSTRAINT fk_monitoring_credit_reservations_account FOREIGN KEY (tenant_id)
        REFERENCES credit_accounts (tenant_id),
    CONSTRAINT fk_monitoring_credit_reservations_site FOREIGN KEY (tenant_id, site_id)
        REFERENCES sites (tenant_id, id),
    CONSTRAINT fk_monitoring_credit_reservations_run FOREIGN KEY (tenant_id, run_id)
        REFERENCES visibility_runs (tenant_id, id),
    CONSTRAINT ck_monitoring_credit_reservations_amount CHECK (
        unit_price > 0 AND estimated_credits > 0 AND held_credits >= 0
        AND captured_credits >= 0 AND released_credits >= 0
    ),
    CONSTRAINT ck_monitoring_credit_reservations_status CHECK (
        status IN ('held','partial','captured','released','unknown')
    )
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- The child table below references visibility_checks by the tenant/site/check
-- composite key. Migration 006 normally creates the equivalent unique key;
-- keep this guard before the child CREATE so a partially migrated database
-- cannot fail while adding the foreign key.
DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_019_prepare_visibility_check_key//
CREATE PROCEDURE jogeoai_019_prepare_visibility_check_key()
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.statistics
        WHERE table_schema = DATABASE() AND table_name = 'visibility_checks'
          AND index_name IN ('uq_visibility_checks_tenant_site_id', 'ix_visibility_checks_tenant_site_id')
    ) THEN
        ALTER TABLE visibility_checks ADD KEY ix_visibility_checks_tenant_site_id (tenant_id, site_id, id);
    END IF;
END//
DELIMITER ;
CALL jogeoai_019_prepare_visibility_check_key();
DROP PROCEDURE jogeoai_019_prepare_visibility_check_key;

CREATE TABLE IF NOT EXISTS monitoring_credit_items (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    reservation_id BINARY(16) NOT NULL,
    run_id BINARY(16) NOT NULL,
    check_id BINARY(16) NOT NULL,
    amount DECIMAL(18,3) NOT NULL,
    status VARCHAR(16) NOT NULL DEFAULT 'held',
    created_at DATETIME(6) NOT NULL,
    captured_at DATETIME(6) NULL,
    released_at DATETIME(6) NULL,
    UNIQUE KEY uq_monitoring_credit_items_tenant_check (tenant_id, check_id),
    UNIQUE KEY uq_monitoring_credit_items_tenant_id (tenant_id, id),
    KEY ix_monitoring_credit_items_reservation_status (tenant_id, reservation_id, status),
    CONSTRAINT fk_monitoring_credit_items_reservation FOREIGN KEY (tenant_id, reservation_id)
        REFERENCES monitoring_credit_reservations (tenant_id, id),
    CONSTRAINT fk_monitoring_credit_items_run FOREIGN KEY (tenant_id, run_id)
        REFERENCES visibility_runs (tenant_id, id),
    CONSTRAINT fk_monitoring_credit_items_check FOREIGN KEY (tenant_id, site_id, check_id)
        REFERENCES visibility_checks (tenant_id, site_id, id),
    CONSTRAINT ck_monitoring_credit_items_amount CHECK (amount > 0),
    CONSTRAINT ck_monitoring_credit_items_status CHECK (
        status IN ('held','captured','released','unknown')
    )
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_019_prepare_schema//
CREATE PROCEDURE jogeoai_019_prepare_schema()
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
          AND table_name = 'credit_ledger' AND column_name = 'monitoring_reservation_id'
    ) THEN
        ALTER TABLE credit_ledger ADD COLUMN monitoring_reservation_id BINARY(16) NULL;
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
          AND table_name = 'credit_ledger' AND column_name = 'monitoring_check_id'
    ) THEN
        ALTER TABLE credit_ledger ADD COLUMN monitoring_check_id BINARY(16) NULL;
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
          AND table_name = 'schedule_slots' AND column_name = 'skip_code'
    ) THEN
        ALTER TABLE schedule_slots ADD COLUMN skip_code VARCHAR(64) NULL;
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
          AND table_name = 'schedule_slots' AND column_name = 'skip_message'
    ) THEN
        ALTER TABLE schedule_slots ADD COLUMN skip_message VARCHAR(1000) NULL;
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE()
          AND table_name = 'credit_ledger' AND index_name = 'ix_credit_ledger_monitoring_reservation'
    ) THEN
        ALTER TABLE credit_ledger ADD KEY ix_credit_ledger_monitoring_reservation (tenant_id, monitoring_reservation_id);
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE()
          AND table_name = 'credit_ledger' AND index_name = 'ix_credit_ledger_monitoring_check'
    ) THEN
        ALTER TABLE credit_ledger ADD KEY ix_credit_ledger_monitoring_check (tenant_id, monitoring_check_id);
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE()
          AND table_name = 'credit_ledger' AND constraint_name = 'fk_credit_ledger_monitoring_reservation'
    ) THEN
        ALTER TABLE credit_ledger ADD CONSTRAINT fk_credit_ledger_monitoring_reservation
            FOREIGN KEY (tenant_id, monitoring_reservation_id)
            REFERENCES monitoring_credit_reservations (tenant_id, id);
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE()
          AND table_name = 'credit_ledger' AND constraint_name = 'fk_credit_ledger_monitoring_check'
    ) THEN
        ALTER TABLE credit_ledger ADD CONSTRAINT fk_credit_ledger_monitoring_check
            FOREIGN KEY (tenant_id, monitoring_check_id)
            REFERENCES monitoring_credit_items (tenant_id, check_id);
    END IF;
END//
DELIMITER ;
CALL jogeoai_019_prepare_schema();
DROP PROCEDURE jogeoai_019_prepare_schema;
