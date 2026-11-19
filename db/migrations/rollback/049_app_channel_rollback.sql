-- rollback/049_app_channel_rollback.sql
-- 回滚 049_app_channel.sql：删除 049 新增的 6 张表、列、索引和 App 入口种子，把 4 个 CHECK 恢复成 049 之前的定义，
-- 回滚后的结构与执行完 048 时一致。这是 App 渠道回滚的第三级（design.md 第 12 节）：
--   第一级清空 Collector:AppTenantIds，第二级停用全部 consumer_app 入口，都不够时才执行本脚本。
-- 回滚顺序：先回滚前端，再回滚 API / Worker 到不含 App 渠道的版本，最后执行本脚本。
-- 执行前提（脚本会检查，任何一条不满足就 SIGNAL 中止，不做任何修改）：
--   1. 没有 capture_mode = 'app' 的回答：
--        SELECT COUNT(*) FROM visibility_checks WHERE capture_mode = 'app';
--   2. 没有 provider = 'app_collector' 的站点目标（最好在回滚 API 之前通过产品关闭全部 App 目标）：
--        SELECT BIN_TO_UUID(id), BIN_TO_UUID(site_id), engine, enabled FROM site_targets WHERE provider = 'app_collector';
--   3. 没有启用的 App 计划（name = 'jogeoai:auto-prompt-monitoring:app:v1' AND enabled = TRUE）。
--      已停用的 App 计划行可以保留（旧代码不识别该名称，停用后不会展开时隙）：
--        SELECT BIN_TO_UUID(id), BIN_TO_UUID(site_id), enabled, next_slot_at FROM collection_schedules
--         WHERE name = 'jogeoai:auto-prompt-monitoring:app:v1';
--   4. 没有 channel = 'consumer_app' 的采集任务：
--        SELECT status, COUNT(*) FROM collector_tasks WHERE channel = 'consumer_app' GROUP BY status;
--   另有两条保护性检查，正常情况下被第 4 条覆盖，只在数据不一致时触发，避免回滚做到一半失败：
--   5. 没有引用 App 入口的采集任务；
--   6. 没有使用 App 失败码（emulator_rejected、contract_changed、contract_unverified、version_mismatch、device_offline、
--      app_crashed）的 collector_attempts 行。
--   这些数据是采集证据和计费依据，本脚本不代为删除；需要清理时先列明对象并取得确认。
-- 会丢失的数据（执行前列明数量并取得确认；审计事件和每日汇报删除前先导出）：
--   SELECT COUNT(*) AS nodes FROM collector_app_nodes;
--   SELECT COUNT(*) AS bindings FROM collector_app_bindings;
--   SELECT COUNT(*) AS audit_events FROM collector_app_events;
--   SELECT COUNT(*) AS daily_reports FROM collector_app_daily_reports;
--   SELECT COUNT(*) AS exclusions FROM prompt_app_exclusions;
--   SELECT platform_code, min_interval_seconds, daily_cap, paused_at, pause_reason FROM collector_app_platform_states;
--   SELECT entry_code, enabled, unavailable_reason FROM collector_platform_entries WHERE channel = 'consumer_app';
-- 手动执行，可以用应用账号，mysql 客户端须加 --default-character-set=utf8mb4。
-- 可重放：对象不存在时跳过；CHECK 只在仍包含 049 的取值时替换。

DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_049_app_channel_rollback//
CREATE PROCEDURE jogeoai_049_app_channel_rollback()
BEGIN
    IF DATABASE() IS NULL THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '049 rollback requires a selected database';
    END IF;

    -- 前提检查：全部通过后才做任何修改。
    IF EXISTS (SELECT 1 FROM visibility_checks WHERE capture_mode = 'app' LIMIT 1) THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '049 rollback refused: visibility checks captured by the app channel exist';
    END IF;
    IF EXISTS (SELECT 1 FROM site_targets WHERE provider = 'app_collector' LIMIT 1) THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '049 rollback refused: app_collector site targets exist';
    END IF;
    IF EXISTS (SELECT 1 FROM collection_schedules
        WHERE name = 'jogeoai:auto-prompt-monitoring:app:v1' AND enabled = TRUE LIMIT 1) THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '049 rollback refused: enabled app monitoring schedules exist';
    END IF;
    IF EXISTS (SELECT 1 FROM collector_tasks WHERE channel = 'consumer_app' LIMIT 1) THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '049 rollback refused: consumer_app collector tasks exist';
    END IF;
    IF EXISTS (SELECT 1 FROM collector_tasks t JOIN collector_platform_entries e ON e.id = t.platform_entry_id
        WHERE e.channel = 'consumer_app' LIMIT 1) THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '049 rollback refused: collector tasks reference consumer_app entries';
    END IF;
    IF EXISTS (SELECT 1 FROM collector_attempts WHERE error_code IN ('emulator_rejected', 'contract_changed',
        'contract_unverified', 'version_mismatch', 'device_offline', 'app_crashed') LIMIT 1) THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '049 rollback refused: collector attempts with app failure codes exist';
    END IF;

    -- 1. App 入口种子：必须在恢复入口表的渠道 CHECK 之前删除。
    DELETE FROM collector_platform_entries WHERE channel = 'consumer_app';

    -- 2. 恢复 4 个 CHECK 的原始定义（003 / 004 / 035 的原文）。
    IF EXISTS (SELECT 1 FROM information_schema.check_constraints WHERE constraint_schema = DATABASE()
        AND constraint_name = 'ck_collector_entries_channel' AND check_clause LIKE '%consumer_app%') THEN
        ALTER TABLE collector_platform_entries
            DROP CHECK ck_collector_entries_channel,
            ADD CONSTRAINT ck_collector_entries_channel CHECK (channel = 'consumer_web');
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.check_constraints WHERE constraint_schema = DATABASE()
        AND constraint_name = 'ck_collector_tasks_channel' AND check_clause LIKE '%consumer_app%') THEN
        ALTER TABLE collector_tasks
            DROP CHECK ck_collector_tasks_channel,
            ADD CONSTRAINT ck_collector_tasks_channel CHECK (channel = 'consumer_web');
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.check_constraints WHERE constraint_schema = DATABASE()
        AND constraint_name = 'ck_collector_attempts_failure_code' AND check_clause LIKE '%emulator_rejected%') THEN
        ALTER TABLE collector_attempts
            DROP CHECK ck_collector_attempts_failure_code,
            ADD CONSTRAINT ck_collector_attempts_failure_code CHECK (
                (event_kind = 'failed') = (error_code IS NOT NULL)
                AND (error_code IS NULL OR error_code IN ('login_required', 'region_restricted', 'challenge_required', 'rate_limited',
                    'page_contract_changed', 'page_contract_unverified', 'collection_timeout', 'browser_failed', 'operator_archived')));
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.check_constraints WHERE constraint_schema = DATABASE()
        AND constraint_name = 'ck_visibility_checks_capture_mode' AND check_clause LIKE '%''app%') THEN
        ALTER TABLE visibility_checks
            DROP CHECK ck_visibility_checks_capture_mode,
            ADD CONSTRAINT ck_visibility_checks_capture_mode CHECK (capture_mode IS NULL OR capture_mode IN ('api','scraped'));
    END IF;

    -- 3. 049 新增的索引和列。索引先于它包含的列删除。
    IF EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE()
        AND table_name = 'collector_attempts' AND index_name = 'ix_collector_attempts_app_binding_quota') THEN
        ALTER TABLE collector_attempts DROP KEY ix_collector_attempts_app_binding_quota;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'collector_attempts' AND column_name = 'app_binding_id') THEN
        ALTER TABLE collector_attempts DROP COLUMN app_binding_id;
    END IF;

    IF EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE()
        AND table_name = 'collector_tasks' AND index_name = 'ix_collector_tasks_app_claim') THEN
        ALTER TABLE collector_tasks DROP KEY ix_collector_tasks_app_claim;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'collector_tasks' AND column_name = 'last_block_reason') THEN
        ALTER TABLE collector_tasks DROP COLUMN last_block_reason;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'collector_tasks' AND column_name = 'lease_app_binding_id') THEN
        ALTER TABLE collector_tasks DROP COLUMN lease_app_binding_id;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'collector_tasks' AND column_name = 'prompt_rule_version') THEN
        ALTER TABLE collector_tasks DROP COLUMN prompt_rule_version;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'collector_tasks' AND column_name = 'fair_rank') THEN
        ALTER TABLE collector_tasks DROP COLUMN fair_rank;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'collector_tasks' AND column_name = 'deadline_at') THEN
        ALTER TABLE collector_tasks DROP COLUMN deadline_at;
    END IF;

    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'collector_platform_entries' AND column_name = 'unavailable_reason') THEN
        ALTER TABLE collector_platform_entries DROP COLUMN unavailable_reason;
    END IF;

    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'visibility_checks' AND column_name = 'app_binding_id') THEN
        ALTER TABLE visibility_checks DROP COLUMN app_binding_id;
    END IF;

    -- 4. 049 新增的表；绑定表引用节点表，先删绑定。
    DROP TABLE IF EXISTS prompt_app_exclusions;
    DROP TABLE IF EXISTS collector_app_daily_reports;
    DROP TABLE IF EXISTS collector_app_events;
    DROP TABLE IF EXISTS collector_app_bindings;
    DROP TABLE IF EXISTS collector_app_platform_states;
    DROP TABLE IF EXISTS collector_app_nodes;
END//
DELIMITER ;
CALL jogeoai_049_app_channel_rollback();
DROP PROCEDURE jogeoai_049_app_channel_rollback;
