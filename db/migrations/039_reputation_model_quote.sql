-- 039_reputation_model_quote.sql
-- 口碑判定为 unknown 时保留模型给出的原始依据片段，便于排查 EVIDENCE_NOT_FOUND 等结论（2026-10-30 试跑后新增）。
-- 只给 038 新建的 reputation_check_sentiments 增加一个可空列，不修改任何旧表，也不回填已有行。
-- 依赖 038；手动执行于 038 之后，mysql 客户端须加 --default-character-set=utf8mb4；API / Worker 启动不会执行本文件。
-- 可重放：列已存在时跳过。未执行时 Worker 照常判定，只是不写该列。
-- 回滚：ALTER TABLE reputation_check_sentiments DROP COLUMN model_quote;

DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_039_reputation_model_quote//
CREATE PROCEDURE jogeoai_039_reputation_model_quote()
BEGIN
    IF DATABASE() IS NULL OR NOT EXISTS (SELECT 1 FROM information_schema.tables
        WHERE table_schema = DATABASE() AND table_name = 'reputation_check_sentiments') THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '039 requires a selected database and migration 038';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'reputation_check_sentiments' AND column_name = 'model_quote') THEN
        -- 模型原始依据片段（≤ 400 字符）；只在 state = unknown 时写入，已判定的依据以 evidence_start / evidence_end 回溯原文。
        ALTER TABLE reputation_check_sentiments
            ADD COLUMN model_quote VARCHAR(400) NULL AFTER evidence_end;
    END IF;
END//
DELIMITER ;
CALL jogeoai_039_reputation_model_quote();
DROP PROCEDURE jogeoai_039_reputation_model_quote;
