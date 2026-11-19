-- 042_prompt_target_scopes.sql
-- 按提问保存 AI 平台范围（Trellis 11-03-prompt-create-wizard，design.md §2）。
-- 只做加法：
--   prompts 新增 target_scope VARCHAR(16) NOT NULL DEFAULT 'all'（'all' | 'custom'）；既有行为 'all'，监测行为与迁移前完全一致；
--   prompts 新增 creation_source VARCHAR(32) NULL（manual | ai_recommend | keyword_expand | batch_import；历史行为空）；
--   新表 prompt_target_scopes：custom 提问选中的采集目标，按 CollectionIdentity.TargetKey 保存（与 site_targets 行解耦，
--   PUT /targets 整体替换导致 id 重建时不丢失选择）。有效平台 = 本表 ∩ 品牌当前启用且可监控的 site_targets（按 target_key）。
-- 依赖 002（prompts / sites）与 005；手动执行于 001–041 之后，mysql 客户端须加 --default-character-set=utf8mb4；
-- API / Worker 启动不会执行本文件。
-- 可重放：列、约束按 information_schema 判断后再 ALTER；新表用 CREATE TABLE IF NOT EXISTS。
-- 发布顺序：先执行本迁移，再同时发布 API 与 Worker（旧 Worker 会忽略计划快照中的 promptTargets 并退化为全笛卡尔积）。
-- 未执行时：不带平台范围的读写照常；带平台范围的写入返回 503 PROMPT_TARGET_SCHEMA_NOT_MIGRATED；每日监控按全部提问 × 全部目标。
-- 回滚：见 db/migrations/rollback/042_prompt_target_scopes_rollback.sql（存在 custom 提问时拒绝执行）。

DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_042_prompt_target_scopes//
CREATE PROCEDURE jogeoai_042_prompt_target_scopes()
BEGIN
    IF DATABASE() IS NULL OR NOT EXISTS (SELECT 1 FROM information_schema.tables
        WHERE table_schema = DATABASE() AND table_name = 'prompts') THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '042 requires a selected database and migration 002';
    END IF;

    -- 1. 平台范围：all 使用品牌全部启用且可监控的目标；custom 只使用 prompt_target_scopes 与之的交集。
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'prompts' AND column_name = 'target_scope') THEN
        ALTER TABLE prompts ADD COLUMN target_scope VARCHAR(16) NOT NULL DEFAULT 'all';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE()
        AND table_name = 'prompts' AND constraint_name = 'ck_prompts_target_scope') THEN
        ALTER TABLE prompts ADD CONSTRAINT ck_prompts_target_scope CHECK (target_scope IN ('all','custom'));
    END IF;

    -- 2. 创建来源：历史行为空。
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'prompts' AND column_name = 'creation_source') THEN
        ALTER TABLE prompts ADD COLUMN creation_source VARCHAR(32) NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE()
        AND table_name = 'prompts' AND constraint_name = 'ck_prompts_creation_source') THEN
        ALTER TABLE prompts ADD CONSTRAINT ck_prompts_creation_source
            CHECK (creation_source IS NULL OR creation_source IN ('manual','ai_recommend','keyword_expand','batch_import'));
    END IF;
END//
DELIMITER ;
CALL jogeoai_042_prompt_target_scopes();
DROP PROCEDURE jogeoai_042_prompt_target_scopes;

-- 3. custom 提问的平台选择；删除提问时级联删除。target_key 为 CollectionIdentity.TargetKey 的 64 位小写十六进制。
CREATE TABLE IF NOT EXISTS prompt_target_scopes (
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    prompt_id BINARY(16) NOT NULL,
    target_key CHAR(64) COLLATE utf8mb4_0900_bin NOT NULL,
    created_at DATETIME(6) NOT NULL,
    PRIMARY KEY (prompt_id, target_key),
    KEY ix_pts_site (tenant_id, site_id),
    KEY ix_pts_site_prompt (site_id, prompt_id),
    CONSTRAINT fk_pts_tenant_site FOREIGN KEY (tenant_id, site_id) REFERENCES sites (tenant_id, id),
    CONSTRAINT fk_pts_prompt FOREIGN KEY (site_id, prompt_id) REFERENCES prompts (site_id, id) ON DELETE CASCADE,
    CONSTRAINT ck_pts_target_key CHECK (CHAR_LENGTH(target_key) = 64)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
