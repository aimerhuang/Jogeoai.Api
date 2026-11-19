-- rollback/047_answer_mode_frequency_rollback.sql
-- 回滚 047_answer_mode_frequency.sql：删除 site_targets.answer_mode / frequency / frequency_anchor、
-- visibility_checks.answer_mode 及其 CHECK，主计划 jogeoai:auto-prompt-monitoring:v1 的时刻 09:01 改回 09:00。
-- 047 没有改动 visibility_checks 的唯一键（现有 uq_visibility_checks_run_prompt_target_sample 已按 run_target_id 区分模式），
-- 因此本脚本不恢复 002 的 uq_visibility_checks_run_prompt_engine（005 起就不存在），也不按 (run_id, prompt_id, engine) 查重：
-- sampleCount > 1 或同一引擎多个国家/语言目标的历史轮次本来就会有重复组，查重会让回滚永远被拒绝。
-- 回滚顺序：先回滚前端，再回滚 API / Worker 到不含回答模式与采集频率的版本，最后执行本脚本。
-- 执行前提（脚本会检查，不满足时 SIGNAL 中止且不做任何修改）：
--   1. 没有启用的凌晨计划（name = 'jogeoai:auto-prompt-monitoring:night:v1' AND enabled = TRUE）。
--      最好在回滚 API 之前，通过新版产品把所有目标改回 daily，由 API 停用凌晨计划；否则由负责人确认后手工停用：
--        SELECT BIN_TO_UUID(id), BIN_TO_UUID(site_id), enabled, next_slot_at FROM collection_schedules
--         WHERE name = 'jogeoai:auto-prompt-monitoring:night:v1';
--        -- UPDATE collection_schedules SET enabled = FALSE, next_slot_at = NULL, version = version + 1, updated_at = UTC_TIMESTAMP(6)
--        --  WHERE name = 'jogeoai:auto-prompt-monitoring:night:v1' AND enabled = TRUE;
--      已停用的凌晨计划行可以保留（旧代码不识别该名称，停用后不会展开时隙）。
--   2. 没有 answer_mode IS NOT NULL 或 frequency <> 'daily' 的目标。最好在回滚 API 之前通过新版产品改回默认模式 + 1次/天
--      （API 会同步改写目标键）。手工处理前先列明对象并取得确认：
--        SELECT BIN_TO_UUID(id), BIN_TO_UUID(site_id), provider, engine, answer_mode, frequency, frequency_anchor, enabled, target_key
--          FROM site_targets WHERE answer_mode IS NOT NULL OR frequency <> 'daily';
--      手工处理须逐行审阅：
--        -- 频率：UPDATE site_targets SET frequency = 'daily', frequency_anchor = NULL, updated_at = UTC_TIMESTAMP(6) WHERE frequency <> 'daily';
--        -- answer_mode 等于该 provider 默认模式的行，目标键不含模式段，可直接置空：UPDATE site_targets SET answer_mode = NULL ... WHERE id = ...;
--        -- answer_mode 不等于默认模式的行，目标键带 |mode 段，旧代码会把它当成另一个同名目标；应删除该行（或保留为停用并接受键漂移），
--        --   不要只把 answer_mode 置空。
-- 注意：visibility_checks.answer_mode 记录的检测模式会随回滚丢失（历史 deep 检测之后显示为模式未知），执行前列明数量并取得确认：
--   SELECT answer_mode, COUNT(*) FROM visibility_checks GROUP BY answer_mode;
-- 手动执行，mysql 客户端须加 --default-character-set=utf8mb4。可重放：对象不存在时跳过；计划时刻只改 local_time = '09:01:00' 的行。

DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_047_answer_mode_frequency_rollback//
CREATE PROCEDURE jogeoai_047_answer_mode_frequency_rollback()
BEGIN
    IF DATABASE() IS NULL THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '047 rollback requires a selected database';
    END IF;

    -- 前提检查：全部通过后才做任何修改。
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE()
        AND table_name = 'collection_schedules') THEN
        IF EXISTS (SELECT 1 FROM collection_schedules
            WHERE name = 'jogeoai:auto-prompt-monitoring:night:v1' AND enabled = TRUE LIMIT 1) THEN
            SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '047 rollback refused: enabled night monitoring schedules exist';
        END IF;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'site_targets' AND column_name = 'answer_mode') THEN
        IF EXISTS (SELECT 1 FROM site_targets WHERE answer_mode IS NOT NULL LIMIT 1) THEN
            SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '047 rollback refused: site targets with an explicit answer mode exist';
        END IF;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'site_targets' AND column_name = 'frequency') THEN
        IF EXISTS (SELECT 1 FROM site_targets WHERE frequency <> 'daily' LIMIT 1) THEN
            SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '047 rollback refused: site targets with non-daily frequency exist';
        END IF;
    END IF;

    -- 主计划 09:01 → 09:00；next_slot_at 同步提前 1 分钟（时隙键带时分，避免切换当天多出或漏掉一轮，见 047 第 3 步）。
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE()
        AND table_name = 'collection_schedules') THEN
        UPDATE collection_schedules
           SET local_time = '09:00:00',
               next_slot_at = CASE WHEN next_slot_at IS NULL THEN NULL ELSE DATE_SUB(next_slot_at, INTERVAL 1 MINUTE) END,
               version = version + 1,
               updated_at = UTC_TIMESTAMP(6)
         WHERE name = 'jogeoai:auto-prompt-monitoring:v1' AND local_time = '09:01:00';
    END IF;

    IF EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE()
        AND table_name = 'visibility_checks' AND constraint_name = 'ck_visibility_checks_answer_mode') THEN
        ALTER TABLE visibility_checks DROP CHECK ck_visibility_checks_answer_mode;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'visibility_checks' AND column_name = 'answer_mode') THEN
        ALTER TABLE visibility_checks DROP COLUMN answer_mode;
    END IF;

    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'site_targets' AND column_name = 'frequency_anchor') THEN
        ALTER TABLE site_targets DROP COLUMN frequency_anchor;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE()
        AND table_name = 'site_targets' AND constraint_name = 'ck_site_targets_frequency') THEN
        ALTER TABLE site_targets DROP CHECK ck_site_targets_frequency;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'site_targets' AND column_name = 'frequency') THEN
        ALTER TABLE site_targets DROP COLUMN frequency;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE()
        AND table_name = 'site_targets' AND constraint_name = 'ck_site_targets_answer_mode') THEN
        ALTER TABLE site_targets DROP CHECK ck_site_targets_answer_mode;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'site_targets' AND column_name = 'answer_mode') THEN
        ALTER TABLE site_targets DROP COLUMN answer_mode;
    END IF;
END//
DELIMITER ;
CALL jogeoai_047_answer_mode_frequency_rollback();
DROP PROCEDURE jogeoai_047_answer_mode_frequency_rollback;
