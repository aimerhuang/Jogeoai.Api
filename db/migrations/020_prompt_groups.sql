-- 020_prompt_groups.sql
-- Persisted grouping for the existing prompts management resource.
-- Old prompts remain ungrouped (group_id IS NULL).

CREATE TABLE IF NOT EXISTS prompt_groups (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    name VARCHAR(100) NOT NULL,
    normalized_name VARCHAR(100) COLLATE utf8mb4_0900_bin NOT NULL,
    version INT UNSIGNED NOT NULL DEFAULT 1,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_prompt_groups_site_id_id (site_id, id),
    UNIQUE KEY uq_prompt_groups_site_name (site_id, normalized_name),
    KEY ix_prompt_groups_site_created (site_id, created_at, id),
    CONSTRAINT fk_prompt_groups_site FOREIGN KEY (tenant_id, site_id)
        REFERENCES sites (tenant_id, id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_020_prompt_groups//
CREATE PROCEDURE jogeoai_020_prompt_groups()
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = DATABASE() AND table_name = 'prompts' AND column_name = 'group_id'
    ) THEN
        ALTER TABLE prompts ADD COLUMN group_id BINARY(16) NULL AFTER site_id;
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.statistics
        WHERE table_schema = DATABASE() AND table_name = 'prompts' AND index_name = 'ix_prompts_site_group'
    ) THEN
        ALTER TABLE prompts ADD KEY ix_prompts_site_group (site_id, group_id);
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.table_constraints
        WHERE table_schema = DATABASE() AND table_name = 'prompts' AND constraint_name = 'fk_prompts_site_group'
    ) THEN
        ALTER TABLE prompts ADD CONSTRAINT fk_prompts_site_group
            FOREIGN KEY (site_id, group_id) REFERENCES prompt_groups (site_id, id);
    END IF;
END//
DELIMITER ;
CALL jogeoai_020_prompt_groups();
DROP PROCEDURE jogeoai_020_prompt_groups;
