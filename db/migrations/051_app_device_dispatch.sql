-- Additive, replayable, no historical backfill. Drain all old App nodes/leases before switching.
-- Keep this table on rollback: a lease is not proof that a question was sent.
CREATE TABLE IF NOT EXISTS collector_app_device_attempts (
    task_id BINARY(16) NOT NULL,
    lease_version INT UNSIGNED NOT NULL,
    binding_id BINARY(16) NOT NULL,
    node_id VARCHAR(128) NOT NULL,
    device_id VARCHAR(128) NOT NULL,
    leased_at DATETIME(6) NOT NULL,
    lease_until DATETIME(6) NOT NULL,
    sent_at DATETIME(6) NULL,
    failure_code VARCHAR(64) NULL,
    send_state VARCHAR(16) NULL,
    failed_at DATETIME(6) NULL,
    PRIMARY KEY (task_id, lease_version),
    KEY ix_app_device_send (node_id, device_id, sent_at),
    KEY ix_app_device_unknown (node_id, device_id, lease_until),
    CONSTRAINT ck_app_device_send_state CHECK (send_state IS NULL OR send_state IN ('not_sent','sent','unknown'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
