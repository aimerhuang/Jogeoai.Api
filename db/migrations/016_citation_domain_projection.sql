-- Additive only. Deploy explicitly after 015; application startup must not run this migration.
-- A token is bound once to immutable projection rules. No historical facts or business data are rewritten.
CREATE TABLE IF NOT EXISTS analytics_snapshot_projections (
    token_hash CHAR(64) COLLATE utf8mb4_0900_bin NOT NULL,
    projection_kind VARCHAR(64) COLLATE utf8mb4_0900_bin NOT NULL,
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    projection_version VARCHAR(64) COLLATE utf8mb4_0900_bin NOT NULL,
    category_rules_version VARCHAR(64) COLLATE utf8mb4_0900_bin NOT NULL,
    ownership_rules_version VARCHAR(64) COLLATE utf8mb4_0900_bin NOT NULL,
    placeholder_rules_version VARCHAR(64) COLLATE utf8mb4_0900_bin NOT NULL,
    url_normalization_version VARCHAR(64) COLLATE utf8mb4_0900_bin NOT NULL,
    created_at DATETIME(6) NOT NULL,
    expires_at DATETIME(6) NOT NULL,
    PRIMARY KEY (token_hash, projection_kind),
    KEY ix_snapshot_projection_expiry (expires_at),
    CONSTRAINT fk_snapshot_projection_snapshot FOREIGN KEY (token_hash) REFERENCES analytics_snapshots (token_hash),
    CONSTRAINT fk_snapshot_projection_site FOREIGN KEY (tenant_id, site_id) REFERENCES sites (tenant_id, id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
