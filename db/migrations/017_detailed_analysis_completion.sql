-- Explicit additive deployment after 016. No backfill, analysis job or historical rewrite.
-- A completion exists even when the completed answer produced zero external citations.
CREATE TABLE IF NOT EXISTS check_detailed_completions (
    release_id BINARY(16) NOT NULL,
    check_id BINARY(16) NOT NULL,
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    completed_event_id BIGINT UNSIGNED NOT NULL,
    detail_version VARCHAR(32) COLLATE utf8mb4_0900_bin NOT NULL,
    url_normalization_version VARCHAR(32) COLLATE utf8mb4_0900_bin NOT NULL,
    completion_version VARCHAR(32) COLLATE utf8mb4_0900_bin NOT NULL,
    completed_at DATETIME(6) NOT NULL,
    PRIMARY KEY (release_id, check_id),
    KEY ix_detailed_completion_site_event (tenant_id, site_id, completed_event_id),
    CONSTRAINT fk_detailed_completion_analysis FOREIGN KEY (release_id, check_id)
        REFERENCES check_analysis (release_id, check_id),
    CONSTRAINT fk_detailed_completion_check FOREIGN KEY (tenant_id, site_id, check_id)
        REFERENCES visibility_checks (tenant_id, site_id, id),
    CONSTRAINT fk_detailed_completion_release FOREIGN KEY (site_id, release_id)
        REFERENCES analysis_releases (site_id, id),
    CONSTRAINT fk_detailed_completion_event FOREIGN KEY (completed_event_id)
        REFERENCES analysis_events (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
