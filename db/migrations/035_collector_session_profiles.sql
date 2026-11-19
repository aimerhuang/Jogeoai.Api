-- 035_collector_session_profiles.sql
-- 采集器账号池与验证码冷却（Trellis 10-28-collector-account-pool；参考 Ai-visiblility-collector 0016）。
-- 新增：collector_session_profiles（每平台多个授权账号，只存非敏感元数据）、
-- collector_platform_dispatch_states（平台闸门与熔断）、collector_session_health_events（只追加、不可修改）；
-- collector_tasks 加 failure_count / lease_session_profile_id，collector_attempts 与 visibility_checks 加 session_profile_id。
-- 退避复用已有的 collector_tasks.next_attempt_at，失败码复用 last_error_code / collector_attempts.error_code。
-- collector_attempts / visibility_checks 的 session_profile_id 故意不建外键：结果提交先锁任务再写这两张表，
-- 外键检查会在账号行加共享锁，与失败上报「账号 → 任务」的加锁顺序形成死锁；取值由代码从租约任务行复制。
-- 手动执行于 001–034 之后（mysql 客户端须 --default-character-set=utf8mb4）；API / Worker 启动不会执行本文件。
-- 必须用 root（或具备 SUPER / log_bin_trust_function_creators=1）执行：开启 binlog 时创建触发器需要该权限，
-- 应用账号执行会报 1419 并中止在触发器之前（此前的表和列已创建，换 root 重放即可）。
-- 可重放：建表 IF NOT EXISTS；加列 / 约束 / 索引有 information_schema 守卫；触发器先 DROP 再建。
-- 发布顺序：先迁移再发 API。未执行本文件时，新 API 的 Collector 领取与结果接口会因缺列返回 500。
-- 回滚：回退 API 即可，旧代码忽略新表和新列；确认不再需要时再 DROP 新表、新列与触发器（健康事件为审计证据，删除前先导出）。

DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_035_prerequisites//
CREATE PROCEDURE jogeoai_035_prerequisites()
BEGIN
    IF DATABASE() IS NULL OR NOT EXISTS (SELECT 1 FROM information_schema.tables
        WHERE table_schema = DATABASE() AND table_name = 'citation_domain_types') THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '035 requires a selected database and migrations through 034';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics
        WHERE table_schema = DATABASE() AND table_name = 'collector_platforms' AND index_name = 'uq_collector_platforms_code_region') THEN
        ALTER TABLE collector_platforms ADD UNIQUE KEY uq_collector_platforms_code_region (code, region);
    END IF;
END//
DELIMITER ;
CALL jogeoai_035_prerequisites();
DROP PROCEDURE jogeoai_035_prerequisites;

CREATE TABLE IF NOT EXISTS collector_session_profiles (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    platform_code VARCHAR(64) NOT NULL,
    region VARCHAR(16) NOT NULL,
    -- 运维别名，例如 doubao-a；不得写手机号、邮箱或账号名。
    label VARCHAR(128) NOT NULL,
    status VARCHAR(32) NOT NULL DEFAULT 'active',
    hourly_quota INT UNSIGNED NOT NULL DEFAULT 4,
    daily_quota INT UNSIGNED NOT NULL DEFAULT 30,
    min_interval_seconds INT UNSIGNED NOT NULL DEFAULT 600,
    cooldown_until DATETIME(6) NULL,
    consecutive_challenges INT UNSIGNED NOT NULL DEFAULT 0,
    last_leased_at DATETIME(6) NULL,
    -- 每次 eligibility / 带账号领取都会刷新；5 分钟内视为在线，参与最久未用优先的公平比较。
    last_seen_at DATETIME(6) NULL,
    -- 最近一次 probe 阶段失败；之后未再领取的账号在 probe_failure_offline_minutes 内退出其他账号的公平队列。
    last_probe_failed_at DATETIME(6) NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_collector_session_profiles_tenant_id (tenant_id, id),
    UNIQUE KEY uq_collector_session_profiles_label (tenant_id, platform_code, label),
    KEY ix_collector_session_profiles_dispatch (tenant_id, region, platform_code, status),
    CONSTRAINT fk_collector_session_profiles_tenant FOREIGN KEY (tenant_id) REFERENCES tenants (id),
    CONSTRAINT fk_collector_session_profiles_platform FOREIGN KEY (platform_code, region)
        REFERENCES collector_platforms (code, region),
    CONSTRAINT ck_collector_session_profiles_region CHECK (region IN ('cn', 'overseas')),
    CONSTRAINT ck_collector_session_profiles_label CHECK (CHAR_LENGTH(TRIM(label)) BETWEEN 1 AND 128),
    CONSTRAINT ck_collector_session_profiles_status
        CHECK (status IN ('active', 'cooling', 'needs_verification', 'expired', 'disabled')),
    CONSTRAINT ck_collector_session_profiles_quota CHECK (hourly_quota > 0 AND daily_quota > 0),
    CONSTRAINT ck_collector_session_profiles_cooling CHECK (status <> 'cooling' OR cooldown_until IS NOT NULL)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS collector_platform_dispatch_states (
    tenant_id BINARY(16) NOT NULL,
    region VARCHAR(16) NOT NULL,
    platform_code VARCHAR(64) NOT NULL,
    min_interval_seconds INT UNSIGNED NOT NULL DEFAULT 120,
    daily_cap INT UNSIGNED NOT NULL DEFAULT 120,
    last_dispatched_at DATETIME(6) NULL,
    paused_at DATETIME(6) NULL,
    pause_reason VARCHAR(64) NULL,
    breaker_profile_threshold INT UNSIGNED NOT NULL DEFAULT 2,
    breaker_window_minutes INT UNSIGNED NOT NULL DEFAULT 60,
    retry_backoff_seconds INT UNSIGNED NOT NULL DEFAULT 1800,
    max_task_failures INT UNSIGNED NOT NULL DEFAULT 3,
    rate_limit_cooldown_base_seconds INT UNSIGNED NOT NULL DEFAULT 1800,
    rate_limit_cooldown_max_seconds INT UNSIGNED NOT NULL DEFAULT 28800,
    probe_failure_offline_minutes INT UNSIGNED NOT NULL DEFAULT 15,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    PRIMARY KEY (tenant_id, region, platform_code),
    CONSTRAINT fk_collector_dispatch_states_tenant FOREIGN KEY (tenant_id) REFERENCES tenants (id),
    CONSTRAINT fk_collector_dispatch_states_platform FOREIGN KEY (platform_code, region)
        REFERENCES collector_platforms (code, region),
    CONSTRAINT ck_collector_dispatch_states_region CHECK (region IN ('cn', 'overseas')),
    CONSTRAINT ck_collector_dispatch_states_positive CHECK (daily_cap > 0 AND breaker_profile_threshold > 0
        AND breaker_window_minutes > 0 AND max_task_failures > 0 AND rate_limit_cooldown_base_seconds > 0),
    CONSTRAINT ck_collector_dispatch_states_pause CHECK ((paused_at IS NULL) = (pause_reason IS NULL)),
    CONSTRAINT ck_collector_dispatch_states_cooldown
        CHECK (rate_limit_cooldown_max_seconds >= rate_limit_cooldown_base_seconds),
    CONSTRAINT ck_collector_dispatch_states_probe_offline CHECK (probe_failure_offline_minutes BETWEEN 1 AND 1440)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_035_task_columns//
CREATE PROCEDURE jogeoai_035_task_columns()
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
        WHERE table_schema = DATABASE() AND table_name = 'collector_tasks' AND column_name = 'failure_count') THEN
        ALTER TABLE collector_tasks ADD COLUMN failure_count INT UNSIGNED NOT NULL DEFAULT 0;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
        WHERE table_schema = DATABASE() AND table_name = 'collector_tasks' AND column_name = 'lease_session_profile_id') THEN
        ALTER TABLE collector_tasks ADD COLUMN lease_session_profile_id BINARY(16) NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.table_constraints
        WHERE table_schema = DATABASE() AND table_name = 'collector_tasks' AND constraint_name = 'fk_collector_tasks_lease_profile') THEN
        ALTER TABLE collector_tasks ADD CONSTRAINT fk_collector_tasks_lease_profile
            FOREIGN KEY (tenant_id, lease_session_profile_id) REFERENCES collector_session_profiles (tenant_id, id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics
        WHERE table_schema = DATABASE() AND table_name = 'collector_tasks' AND index_name = 'ix_collector_tasks_platform_claim') THEN
        ALTER TABLE collector_tasks ADD KEY ix_collector_tasks_platform_claim (tenant_id, region, platform_code, status, next_attempt_at);
    END IF;

    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
        WHERE table_schema = DATABASE() AND table_name = 'collector_attempts' AND column_name = 'session_profile_id') THEN
        ALTER TABLE collector_attempts ADD COLUMN session_profile_id BINARY(16) NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.table_constraints
        WHERE table_schema = DATABASE() AND table_name = 'collector_attempts' AND constraint_name = 'ck_collector_attempts_failure_code') THEN
        IF EXISTS (SELECT 1 FROM collector_attempts WHERE (event_kind = 'failed') <> (error_code IS NOT NULL)) THEN
            SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '035: collector_attempts has failed/error_code rows that violate the new check';
        END IF;
        -- operator_archived 只用于运维人工归档任务时追加的失败审计，Collector 不会上报；健康事件的失败码白名单不含它。
        ALTER TABLE collector_attempts ADD CONSTRAINT ck_collector_attempts_failure_code CHECK (
            (event_kind = 'failed') = (error_code IS NOT NULL)
            AND (error_code IS NULL OR error_code IN ('login_required', 'region_restricted', 'challenge_required', 'rate_limited',
                'page_contract_changed', 'page_contract_unverified', 'collection_timeout', 'browser_failed', 'operator_archived')));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics
        WHERE table_schema = DATABASE() AND table_name = 'collector_attempts' AND index_name = 'ix_collector_attempts_profile_quota') THEN
        ALTER TABLE collector_attempts ADD KEY ix_collector_attempts_profile_quota (tenant_id, session_profile_id, event_kind, created_at);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics
        WHERE table_schema = DATABASE() AND table_name = 'collector_attempts' AND index_name = 'ix_collector_attempts_region_event') THEN
        ALTER TABLE collector_attempts ADD KEY ix_collector_attempts_region_event (tenant_id, region, event_kind, created_at);
    END IF;

    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
        WHERE table_schema = DATABASE() AND table_name = 'visibility_checks' AND column_name = 'session_profile_id') THEN
        ALTER TABLE visibility_checks ADD COLUMN session_profile_id BINARY(16) NULL;
    END IF;
END//
DELIMITER ;
CALL jogeoai_035_task_columns();
DROP PROCEDURE jogeoai_035_task_columns;

CREATE TABLE IF NOT EXISTS collector_session_health_events (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    region VARCHAR(16) NOT NULL,
    platform_code VARCHAR(64) NOT NULL,
    session_profile_id BINARY(16) NULL,
    event_kind VARCHAR(48) NOT NULL,
    failure_code VARCHAR(64) NULL,
    phase VARCHAR(16) NULL,
    collector_task_id BINARY(16) NULL,
    lease_version INT UNSIGNED NULL,
    node_id VARCHAR(128) NULL,
    from_status VARCHAR(32) NULL,
    to_status VARCHAR(32) NULL,
    -- 运维操作的系统管理员；Collector 上报的事件为空。
    operator_user_id BINARY(16) NULL,
    created_at DATETIME(6) NOT NULL,
    KEY ix_collector_health_events_breaker (tenant_id, region, platform_code, event_kind, created_at),
    KEY ix_collector_health_events_profile (tenant_id, session_profile_id, created_at),
    CONSTRAINT fk_collector_health_events_tenant FOREIGN KEY (tenant_id) REFERENCES tenants (id),
    CONSTRAINT fk_collector_health_events_platform FOREIGN KEY (platform_code, region)
        REFERENCES collector_platforms (code, region),
    CONSTRAINT fk_collector_health_events_profile FOREIGN KEY (tenant_id, session_profile_id)
        REFERENCES collector_session_profiles (tenant_id, id),
    CONSTRAINT fk_collector_health_events_task FOREIGN KEY (tenant_id, collector_task_id)
        REFERENCES collector_tasks (tenant_id, id),
    CONSTRAINT ck_collector_health_events_region CHECK (region IN ('cn', 'overseas')),
    CONSTRAINT ck_collector_health_events_kind CHECK (event_kind IN (
        'profile_registered', 'challenge_wait_started', 'challenge_cleared', 'challenge_timeout', 'failure_reported',
        'verified', 'cooldown_expired', 'status_changed_by_operator', 'breaker_tripped', 'breaker_reset',
        'platform_settings_changed')),
    CONSTRAINT ck_collector_health_events_failure_code CHECK (failure_code IS NULL OR failure_code IN (
        'login_required', 'region_restricted', 'challenge_required', 'rate_limited',
        'page_contract_changed', 'page_contract_unverified', 'collection_timeout', 'browser_failed')),
    CONSTRAINT ck_collector_health_events_phase CHECK (phase IS NULL OR phase IN ('probe', 'pre_send', 'post_send')),
    CONSTRAINT ck_collector_health_events_lease_version CHECK (lease_version IS NULL OR lease_version > 0),
    CONSTRAINT ck_collector_health_events_from_status CHECK (from_status IS NULL
        OR from_status IN ('active', 'cooling', 'needs_verification', 'expired', 'disabled')),
    CONSTRAINT ck_collector_health_events_to_status CHECK (to_status IS NULL
        OR to_status IN ('active', 'cooling', 'needs_verification', 'expired', 'disabled')),
    CONSTRAINT ck_collector_health_events_profile_scope CHECK (
        (session_profile_id IS NULL) = (event_kind IN ('breaker_reset', 'platform_settings_changed'))),
    CONSTRAINT ck_collector_health_events_status_pair CHECK (
        (session_profile_id IS NULL AND from_status IS NULL AND to_status IS NULL)
        OR (session_profile_id IS NOT NULL AND from_status IS NOT NULL AND to_status IS NOT NULL)),
    CONSTRAINT ck_collector_health_events_failure_scope CHECK (
        (failure_code IS NOT NULL) = (event_kind IN ('failure_reported', 'challenge_timeout'))),
    CONSTRAINT ck_collector_health_events_task_scope CHECK (collector_task_id IS NOT NULL OR lease_version IS NULL)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- 健康事件是审计证据：任何 UPDATE / DELETE 都被拒绝（含 root；清理只能整表 DROP 或整库删除）。
DROP TRIGGER IF EXISTS trg_collector_health_events_no_update;
DROP TRIGGER IF EXISTS trg_collector_health_events_no_delete;
DELIMITER //
CREATE TRIGGER trg_collector_health_events_no_update BEFORE UPDATE ON collector_session_health_events
FOR EACH ROW
BEGIN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'collector_session_health_events is append-only';
END//
CREATE TRIGGER trg_collector_health_events_no_delete BEFORE DELETE ON collector_session_health_events
FOR EACH ROW
BEGIN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'collector_session_health_events is append-only';
END//
DELIMITER ;
