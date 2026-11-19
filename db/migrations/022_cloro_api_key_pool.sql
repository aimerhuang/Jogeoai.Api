-- 022_cloro_api_key_pool.sql
-- Cloro 多账号 API Key 池：一行 = 一个 Cloro 账号（组织）及其唯一 key。
-- 手动执行于 001–021 之后；API / Worker 启动不会执行本文件。只做加法，可重复执行。
-- key 按 010 的部署约定明文保存；任何接口、日志和事件只出现 key_hint（后 4 位）。
-- 单例表 cloro_provider_settings.api_key 改为可空并保留原值，供回滚到旧版本使用；新版本不再读取它。

CREATE TABLE IF NOT EXISTS cloro_api_keys (
    id BINARY(16) NOT NULL PRIMARY KEY,
    account_label VARCHAR(64) NOT NULL,
    api_key TEXT NOT NULL COMMENT '明文，按部署约定；任何出口只回显 key_hint。',
    api_key_sha256 CHAR(64) COLLATE utf8mb4_0900_bin NOT NULL,
    key_hint VARCHAR(8) NOT NULL COMMENT '后 4 位',
    priority INT NOT NULL DEFAULT 100,
    enabled BOOLEAN NOT NULL DEFAULT TRUE,
    status VARCHAR(16) NOT NULL DEFAULT 'unknown',
    credits_remaining DECIMAL(18,3) NULL,
    credits_per_cycle DECIMAL(18,3) NULL,
    cycle_resets_at DATETIME(6) NULL,
    credits_checked_at DATETIME(6) NULL COMMENT 'NULL 表示等待余额刷新',
    cooldown_until DATETIME(6) NULL,
    exhausted_at DATETIME(6) NULL COMMENT '最近一次确认耗尽的时间',
    last_used_at DATETIME(6) NULL,
    last_error_code VARCHAR(64) NULL,
    last_error_at DATETIME(6) NULL,
    archived_at DATETIME(6) NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_cloro_api_keys_label (account_label),
    UNIQUE KEY uq_cloro_api_keys_sha (api_key_sha256),
    KEY ix_cloro_api_keys_pick (enabled, archived_at, status, priority, cycle_resets_at),
    CONSTRAINT ck_cloro_api_keys_status CHECK (status IN ('unknown','active','exhausted','cooling','invalid')),
    CONSTRAINT ck_cloro_api_keys_label CHECK (CHAR_LENGTH(TRIM(account_label)) > 0)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- 只追加的审计记录；不保存 key 值和 Cloro 原始错误文本。
-- admin_changed 事件的 error_code 记录管理动作（CREATED / UPDATED / ARCHIVED / KEY_REPLACED）。
CREATE TABLE IF NOT EXISTS cloro_key_events (
    id BINARY(16) NOT NULL PRIMARY KEY,
    key_id BINARY(16) NOT NULL,
    provider_task_id BINARY(16) NULL,
    event_type VARCHAR(32) NOT NULL,
    error_code VARCHAR(64) NULL,
    external_task_id VARCHAR(255) COLLATE utf8mb4_0900_bin NULL,
    credits_remaining DECIMAL(18,3) NULL,
    created_at DATETIME(6) NOT NULL,
    KEY ix_cloro_key_events_key_time (key_id, created_at),
    KEY ix_cloro_key_events_task (provider_task_id),
    CONSTRAINT fk_cloro_key_events_key FOREIGN KEY (key_id) REFERENCES cloro_api_keys (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_022_cloro_api_key_pool//
CREATE PROCEDURE jogeoai_022_cloro_api_key_pool()
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'provider_tasks' AND column_name = 'cloro_key_id') THEN
        ALTER TABLE provider_tasks ADD COLUMN cloro_key_id BINARY(16) NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'provider_tasks' AND column_name = 'credit_resubmit_count') THEN
        ALTER TABLE provider_tasks ADD COLUMN credit_resubmit_count TINYINT UNSIGNED NOT NULL DEFAULT 0;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'provider_tasks' AND column_name = 'pool_exhausted_since') THEN
        ALTER TABLE provider_tasks ADD COLUMN pool_exhausted_since DATETIME(6) NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE()
        AND table_name = 'provider_tasks' AND index_name = 'ix_provider_tasks_cloro_key_state') THEN
        ALTER TABLE provider_tasks ADD KEY ix_provider_tasks_cloro_key_state (cloro_key_id, state);
    END IF;
    -- Keep the singleton value for rollback; only relax NOT NULL so new rows may omit it.
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'cloro_provider_settings' AND column_name = 'api_key' AND is_nullable = 'NO') THEN
        ALTER TABLE cloro_provider_settings MODIFY COLUMN api_key TEXT NULL;
    END IF;
END//
DELIMITER ;
CALL jogeoai_022_cloro_api_key_pool();
DROP PROCEDURE jogeoai_022_cloro_api_key_pool;

-- 导入现有单例 key 作为 default 账号；INSERT IGNORE 保证重放不会覆盖管理员之后的修改。
-- 与应用录入规则一致：去除首尾空格后再计算 SHA-256 和后 4 位，保证去重有效。
INSERT IGNORE INTO cloro_api_keys
    (id, account_label, api_key, api_key_sha256, key_hint, priority, enabled, status, created_at, updated_at)
SELECT UUID_TO_BIN(UUID()), 'default', TRIM(s.api_key), SHA2(TRIM(s.api_key), 256),
       IF(CHAR_LENGTH(TRIM(s.api_key)) > 8, RIGHT(TRIM(s.api_key), 4), '****'), 100, s.enabled, 'unknown',
       UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)
FROM cloro_provider_settings s
WHERE s.id = 1 AND s.api_key IS NOT NULL AND CHAR_LENGTH(TRIM(s.api_key)) > 0;

-- 已提交、未终态的 Cloro 任务关联到 default，保证升级后继续用原 key 轮询。
-- 终态历史任务保持 NULL，含义为“池化前，未记录”；轮询 NULL 时回退到 default。
UPDATE provider_tasks t
JOIN cloro_api_keys k ON k.account_label = 'default'
SET t.cloro_key_id = k.id
WHERE t.provider = 'cloro' AND t.cloro_key_id IS NULL AND t.external_task_id IS NOT NULL
  AND t.state IN ('pending','leased','submitting','queued','running','retryable_failed');
