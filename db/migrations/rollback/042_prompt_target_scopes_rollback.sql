-- rollback/042_prompt_target_scopes_rollback.sql
-- 回滚 042_prompt_target_scopes.sql：删除 prompt_target_scopes 表，删除 prompts.target_scope / creation_source 及其 CHECK。
-- 执行前提（脚本会检查，不满足时 SIGNAL 中止且不做任何修改）：
--   1. 已先回滚前端，再回滚 API / Worker 到不含按提问平台范围的版本（design.md §8）；
--   2. 没有 target_scope = 'custom' 的提问（如需回滚，先由负责人确认把它们恢复为 'all'，即接受全量采集与计费）。
-- 注意：creation_source 的来源记录与 prompt_target_scopes 中的平台选择会随回滚丢失，执行前请列明对象并取得确认。
-- 手动执行，mysql 客户端须加 --default-character-set=utf8mb4。可重放：对象不存在时跳过。

DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_042_prompt_target_scopes_rollback//
CREATE PROCEDURE jogeoai_042_prompt_target_scopes_rollback()
BEGIN
    IF DATABASE() IS NULL THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '042 rollback requires a selected database';
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'prompts' AND column_name = 'target_scope') THEN
        IF EXISTS (SELECT 1 FROM prompts WHERE target_scope = 'custom' LIMIT 1) THEN
            SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '042 rollback refused: prompts with custom target scope exist';
        END IF;
    END IF;

    DROP TABLE IF EXISTS prompt_target_scopes;

    IF EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE()
        AND table_name = 'prompts' AND constraint_name = 'ck_prompts_creation_source') THEN
        ALTER TABLE prompts DROP CHECK ck_prompts_creation_source;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'prompts' AND column_name = 'creation_source') THEN
        ALTER TABLE prompts DROP COLUMN creation_source;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE()
        AND table_name = 'prompts' AND constraint_name = 'ck_prompts_target_scope') THEN
        ALTER TABLE prompts DROP CHECK ck_prompts_target_scope;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'prompts' AND column_name = 'target_scope') THEN
        ALTER TABLE prompts DROP COLUMN target_scope;
    END IF;
END//
DELIMITER ;
CALL jogeoai_042_prompt_target_scopes_rollback();
DROP PROCEDURE jogeoai_042_prompt_target_scopes_rollback;
