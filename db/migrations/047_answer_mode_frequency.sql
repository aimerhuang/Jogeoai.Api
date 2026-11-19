-- 047_answer_mode_frequency.sql
-- 品牌监控模型配置：回答模式 × 采集频率（Trellis 11-09-brand-model-modes，design.md §2 / §5）。
-- 只做加法：
--   site_targets 新增 answer_mode VARCHAR(16) NULL（NULL = provider 默认模式；'fast' | 'deep'）、
--     frequency VARCHAR(16) NOT NULL DEFAULT 'daily'（'twice_daily' | 'daily' | 'every_other_day' | 'weekly'）、
--     frequency_anchor DATE NULL（团队时区的锚点日期）；既有行为默认模式 + daily，目标键与监测行为不变；
--   visibility_checks 新增 answer_mode VARCHAR(16) NULL（NULL = 历史检测，模式未知；新代码始终写入解析后的非空值）；
--   主计划 jogeoai:auto-prompt-monitoring:v1 的本地时刻 09:00 → 09:01（凌晨 00:05 计划由 API 按需创建，不在本迁移中）。
-- 唯一键说明（与 design.md §2 初稿不同，见下）：
--   002 的 uq_visibility_checks_run_prompt_engine (run_id, prompt_id, engine) 已被 005 删除，
--   当前唯一键是 uq_visibility_checks_run_prompt_target_sample (run_id, prompt_id, run_target_id, sample_index)。
--   非默认模式的目标键带 |mode 段（CollectionIdentity.TargetKey），同一模型的两种模式在一轮内是两个 run_engines 行、
--   两个 run_target_id，现有唯一键已经允许二者并存，因此本迁移不改唯一键。
--   不能按初稿加 (run_id, prompt_id, engine, answer_mode)：sampleCount 允许 1–10，且同一引擎可有多个国家/语言目标，
--   这两种合法情况都会在同一 (run, prompt, engine, mode) 下产生多条检测。
--   外键核对：visibility_checks 上 leftmost 为 run_id 的外键（fk_visibility_checks_run、fk_visibility_checks_run_target）
--   由 uq_visibility_checks_run_prompt_target_sample / uq_visibility_checks_run_id_id / ix_visibility_checks_run_status 支撑，
--   本迁移不删除任何索引，不影响外键。
-- 依赖 002（visibility_checks）与 005（site_targets / collection_schedules）；手动执行于 001–046 之后，
-- mysql 客户端须加 --default-character-set=utf8mb4；API / Worker 启动不会执行本文件。
-- 可重放：列、约束按 information_schema 判断后再 ALTER；计划时刻只更新 local_time = '09:00:00' 的行，第二次执行不再顺延。
-- 发布顺序：先执行本迁移，再同时发布 API 与 Worker（旧 Worker 不认识 answer_mode，会按全局参数采集）。
-- 回滚：见 db/migrations/rollback/047_answer_mode_frequency_rollback.sql（存在非默认模式或非 daily 目标、或启用的凌晨计划时拒绝执行）。

DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_047_answer_mode_frequency//
CREATE PROCEDURE jogeoai_047_answer_mode_frequency()
BEGIN
    IF DATABASE() IS NULL
        OR NOT EXISTS (SELECT 1 FROM information_schema.tables
            WHERE table_schema = DATABASE() AND table_name = 'site_targets')
        OR NOT EXISTS (SELECT 1 FROM information_schema.tables
            WHERE table_schema = DATABASE() AND table_name = 'visibility_checks')
        OR NOT EXISTS (SELECT 1 FROM information_schema.tables
            WHERE table_schema = DATABASE() AND table_name = 'collection_schedules') THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '047 requires a selected database and migrations 002 and 005';
    END IF;

    -- 1. 站点目标的回答模式与采集频率。
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'site_targets' AND column_name = 'answer_mode') THEN
        ALTER TABLE site_targets ADD COLUMN answer_mode VARCHAR(16) NULL COMMENT 'NULL=provider 默认模式';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE()
        AND table_name = 'site_targets' AND constraint_name = 'ck_site_targets_answer_mode') THEN
        ALTER TABLE site_targets ADD CONSTRAINT ck_site_targets_answer_mode
            CHECK (answer_mode IS NULL OR answer_mode IN ('fast','deep'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'site_targets' AND column_name = 'frequency') THEN
        ALTER TABLE site_targets ADD COLUMN frequency VARCHAR(16) NOT NULL DEFAULT 'daily';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE()
        AND table_name = 'site_targets' AND constraint_name = 'ck_site_targets_frequency') THEN
        ALTER TABLE site_targets ADD CONSTRAINT ck_site_targets_frequency
            CHECK (frequency IN ('twice_daily','daily','every_other_day','weekly'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'site_targets' AND column_name = 'frequency_anchor') THEN
        ALTER TABLE site_targets ADD COLUMN frequency_anchor DATE NULL;
    END IF;

    -- 2. 检测实际使用的回答模式；历史行保持 NULL。唯一键不变（见文件头）。
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'visibility_checks' AND column_name = 'answer_mode') THEN
        ALTER TABLE visibility_checks ADD COLUMN answer_mode VARCHAR(16) NULL COMMENT 'NULL=历史检测，模式未知';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE()
        AND table_name = 'visibility_checks' AND constraint_name = 'ck_visibility_checks_answer_mode') THEN
        ALTER TABLE visibility_checks ADD CONSTRAINT ck_visibility_checks_answer_mode
            CHECK (answer_mode IS NULL OR answer_mode IN ('fast','deep'));
    END IF;
END//
DELIMITER ;
CALL jogeoai_047_answer_mode_frequency();
DROP PROCEDURE jogeoai_047_answer_mode_frequency;

-- 3. 主计划 09:00 → 09:01。
-- 时隙键带时分（CollectionScheduleCalendar：yyyyMMddTHHmm），如果只改 local_time 并按当前时间重算 next_slot_at，
-- 在本地 09:00–09:01 之间执行迁移时，当天已展开 T0900 后还会再展开一个 T0901，多出一轮。
-- 把 next_slot_at 一起顺延 1 分钟：未到 09:00 的计划当天只跑 T0901；已跑过 T0900 的计划 next_slot_at 已是次日 09:00，顺延为次日 09:01。
-- next_slot_at 为 NULL（已停用）的计划只改时刻，重新启用时由 API 按新时刻计算。
-- 条件 local_time = '09:00:00' 保证重复执行时不会再加 1 分钟；用户自行改过时刻的计划不受影响。
UPDATE collection_schedules
   SET local_time = '09:01:00',
       next_slot_at = CASE WHEN next_slot_at IS NULL THEN NULL ELSE DATE_ADD(next_slot_at, INTERVAL 1 MINUTE) END,
       version = version + 1,
       updated_at = UTC_TIMESTAMP(6)
 WHERE name = 'jogeoai:auto-prompt-monitoring:v1' AND local_time = '09:00:00';
