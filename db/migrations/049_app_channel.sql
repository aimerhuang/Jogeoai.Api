-- 049_app_channel.sql
-- App 采集渠道的数据结构（Trellis 11-16-app-channel-monitoring-api，design.md 第 11 节；总 PRD KD-17 / KD-18 / KD-19 / KD-21）。
-- 只做加法，不改现有语义；不含任何业务代码依赖的行为变化（本迁移之后现有 API / Worker 的读写与之前一致）。
--
-- 现有表的改动：
--   visibility_checks：ck_visibility_checks_capture_mode 增加取值 'app'（原为 NULL / 'api' / 'scraped'）；
--     新增 app_binding_id BINARY(16) NULL。
--   collector_platform_entries：ck_collector_entries_channel 放开为 'consumer_web' / 'consumer_app'；
--     新增 unavailable_reason VARCHAR(64) NULL；新增 7 条 consumer_app 入口（见文件末尾的种子）。
--   collector_tasks：ck_collector_tasks_channel 放开为 'consumer_web' / 'consumer_app'；
--     新增 deadline_at、fair_rank、prompt_rule_version、lease_app_binding_id、last_block_reason（全部可空）；
--     新增索引 ix_collector_tasks_app_claim (channel, platform_code, status, fair_rank, created_at)。
--   collector_attempts：新增 app_binding_id BINARY(16) NULL 与索引 ix_collector_attempts_app_binding_quota
--     (app_binding_id, event_kind, created_at)；ck_collector_attempts_failure_code 在 035 的 9 个取值之外增加
--     emulator_rejected、contract_changed、contract_unverified、version_mismatch、device_offline、app_crashed。
--   collector_platforms 完全不动（包括它的 CHECK）：它是网页渠道的开关，渠道只由入口表表达（research/preflight.md 0.2）。
-- 新表：collector_app_nodes、collector_app_bindings、collector_app_platform_states、collector_app_events、
--   collector_app_daily_reports、prompt_app_exclusions。
--
-- 外键说明：
--   visibility_checks.app_binding_id、collector_attempts.app_binding_id、collector_tasks.lease_app_binding_id 都不建外键。
--     前两者的原因与 035 的 session_profile_id 相同：结果提交先锁任务再写这两张表，外键检查会在绑定行加共享锁，
--     与「绑定 → 平台 → 任务」的加锁顺序形成死锁；取值由代码从任务租约复制，不采信请求体。
--   collector_app_events 不建任何外键：被拒绝的请求（未知节点、未知绑定、重复登记的账号）也要留下审计事件。
--   collector_app_bindings / collector_app_platform_states 的 platform_code 指向 collector_platforms (code)：
--     只借用它作为平台代码的登记表，不读它的 enabled；App 的领取路径不锁 collector_platforms 的行。
-- 不使用触发器：本机 MySQL 开着 binlog 且 log_bin_trust_function_creators = 0，应用账号建不了触发器。
--   collector_app_events 的「只追加」由代码保证（Store 里没有对它的 UPDATE / DELETE）。
--
-- 依赖 003（collector_*）、004（visibility_checks.capture_mode）、035（collector_attempts 的失败码 CHECK）与 048；
-- 手动执行于 001–048 之后，可以用应用账号执行（不需要 root），mysql 客户端须加 --default-character-set=utf8mb4；
-- API / Worker 启动不会执行本文件。
-- 可重放：新表用 CREATE TABLE IF NOT EXISTS；列、索引按 information_schema 判断后再 ALTER；
--   CHECK 只在还没有包含新取值时替换，且在同一条 ALTER 里先删后加（中途失败不会留下没有约束的表）；
--   种子只插入缺少的行，不更新已有行：重放不会把运营停用的 App 入口重新启用，也不会改动任何 consumer_web 入口。
-- 大表提示：替换 visibility_checks 的 CHECK 会重建该表（ADD CHECK 只支持 COPY 算法），在数据量大的库上执行前先评估耗时。
-- 发布顺序：先执行本迁移，再同时发布 API 与 Worker。未执行时 App 相关入口返回 503 APP_CHANNEL_SCHEMA_NOT_MIGRATED，
--   目录不返回 App 行，现有功能不受影响。
-- 回滚：见 db/migrations/rollback/049_app_channel_rollback.sql（存在 App 回答、App 站点目标、启用的 App 计划或 App 任务时拒绝执行）。

DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_049_prerequisites//
CREATE PROCEDURE jogeoai_049_prerequisites()
BEGIN
    IF DATABASE() IS NULL
        OR NOT EXISTS (SELECT 1 FROM information_schema.tables
            WHERE table_schema = DATABASE() AND table_name = 'tenant_monitoring_default_cells')
        OR NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
            AND table_name = 'site_brand_settings' AND column_name = 'monitoring_follows_default')
        OR NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
            AND table_name = 'visibility_checks' AND column_name = 'answer_mode')
        OR NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
            AND table_name = 'collector_attempts' AND column_name = 'session_profile_id')
        OR (SELECT COUNT(*) FROM information_schema.table_constraints WHERE table_schema = DATABASE()
            AND constraint_type = 'CHECK'
            AND (table_name, constraint_name) IN (
                ('visibility_checks', 'ck_visibility_checks_capture_mode'),
                ('collector_platform_entries', 'ck_collector_entries_channel'),
                ('collector_tasks', 'ck_collector_tasks_channel'),
                ('collector_attempts', 'ck_collector_attempts_failure_code'))) <> 4 THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '049 requires a selected database and migrations through 048';
    END IF;
    IF (SELECT COUNT(*) FROM collector_platforms WHERE code IN
            ('doubao', 'deepseek', 'tencent_yuanbao', 'qwen', 'kimi', 'zhipu_qingyan', 'ernie')) <> 7 THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '049 requires the seven collector_platforms rows seeded by 003';
    END IF;
END//
DELIMITER ;
CALL jogeoai_049_prerequisites();
DROP PROCEDURE jogeoai_049_prerequisites;

-- 采集节点：一行 = 一台电脑上的 App 采集节点。节点身份由令牌确定，只存令牌的 SHA-256（64 位十六进制），不存明文。
CREATE TABLE IF NOT EXISTS collector_app_nodes (
    node_id VARCHAR(128) NOT NULL PRIMARY KEY,
    token_hash CHAR(64) COLLATE utf8mb4_0900_bin NOT NULL,
    status VARCHAR(16) NOT NULL DEFAULT 'active',
    last_seen_at DATETIME(6) NULL,
    collector_build_revision VARCHAR(128) NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_collector_app_nodes_token (token_hash),
    CONSTRAINT ck_collector_app_nodes_id CHECK (CHAR_LENGTH(TRIM(node_id)) BETWEEN 1 AND 128),
    CONSTRAINT ck_collector_app_nodes_token CHECK (CHAR_LENGTH(token_hash) = 64),
    CONSTRAINT ck_collector_app_nodes_status CHECK (status IN ('active', 'disabled'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- 绑定：一条 = 设备 × App × 账号，全局共用，不属于任何租户（总 PRD KD-17）。
-- device_id 是 AVD 名称，只在节点内唯一；account_label 是账号代号（例如 doubao-a），不得写手机号、邮箱或账号名，
-- 同一平台内全局唯一：同一个账号不能出现在两个绑定里。
-- 限速（KD-21）：min_interval_seconds 默认 300；daily_quota 为空 = 不设每日上限；没有每小时上限。
CREATE TABLE IF NOT EXISTS collector_app_bindings (
    id BINARY(16) NOT NULL PRIMARY KEY,
    node_id VARCHAR(128) NOT NULL,
    device_id VARCHAR(128) NOT NULL,
    platform_code VARCHAR(64) NOT NULL,
    account_label VARCHAR(128) NOT NULL,
    app_package VARCHAR(255) NOT NULL,
    reviewed_app_version VARCHAR(64) NULL,
    installed_app_version VARCHAR(64) NULL,
    android_version VARCHAR(32) NULL,
    status VARCHAR(16) NOT NULL DEFAULT 'active',
    stop_reason VARCHAR(32) NULL,
    -- 进入 stopped 的时刻；每日汇报用它计算「停止了多久」（results.md 8.4）。
    stopped_at DATETIME(6) NULL,
    -- 频率受限后的冷却，到 Asia/Shanghai 当日结束；不改变 status。
    cooldown_until DATETIME(6) NULL,
    min_interval_seconds INT UNSIGNED NOT NULL DEFAULT 300,
    daily_quota INT UNSIGNED NULL,
    last_seen_at DATETIME(6) NULL,
    last_leased_at DATETIME(6) NULL,
    last_success_at DATETIME(6) NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_collector_app_bindings_device (node_id, device_id, platform_code),
    UNIQUE KEY uq_collector_app_bindings_account (platform_code, account_label),
    CONSTRAINT fk_collector_app_bindings_node FOREIGN KEY (node_id) REFERENCES collector_app_nodes (node_id),
    CONSTRAINT fk_collector_app_bindings_platform FOREIGN KEY (platform_code) REFERENCES collector_platforms (code),
    CONSTRAINT ck_collector_app_bindings_device CHECK (CHAR_LENGTH(TRIM(device_id)) BETWEEN 1 AND 128),
    CONSTRAINT ck_collector_app_bindings_label CHECK (CHAR_LENGTH(TRIM(account_label)) BETWEEN 1 AND 128),
    CONSTRAINT ck_collector_app_bindings_package CHECK (CHAR_LENGTH(TRIM(app_package)) BETWEEN 1 AND 255),
    CONSTRAINT ck_collector_app_bindings_status CHECK (status IN ('active', 'stopped')),
    CONSTRAINT ck_collector_app_bindings_stop_reason CHECK (stop_reason IS NULL OR stop_reason IN (
        'login_required', 'challenge_required', 'device_offline', 'contract_changed', 'contract_unverified',
        'version_mismatch', 'emulator_rejected', 'disabled_by_operator')),
    CONSTRAINT ck_collector_app_bindings_stopped CHECK ((status = 'stopped') = (stop_reason IS NOT NULL)),
    CONSTRAINT ck_collector_app_bindings_stopped_at CHECK ((stop_reason IS NULL) = (stopped_at IS NULL)),
    CONSTRAINT ck_collector_app_bindings_limits CHECK (min_interval_seconds > 0 AND (daily_quota IS NULL OR daily_quota > 0))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- 平台闸门：每个平台一行，全部节点、全部账号合计（KD-21）。min_interval_seconds 默认 30；daily_cap 为空 = 不设每日上限。
-- 平台「能不能采」看 collector_platform_entries 里 consumer_app 入口的 enabled，不看本表。
CREATE TABLE IF NOT EXISTS collector_app_platform_states (
    platform_code VARCHAR(64) NOT NULL PRIMARY KEY,
    min_interval_seconds INT UNSIGNED NOT NULL DEFAULT 30,
    daily_cap INT UNSIGNED NULL,
    last_dispatched_at DATETIME(6) NULL,
    paused_at DATETIME(6) NULL,
    pause_reason VARCHAR(64) NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    CONSTRAINT fk_collector_app_platform_states_platform FOREIGN KEY (platform_code) REFERENCES collector_platforms (code),
    CONSTRAINT ck_collector_app_platform_states_limits CHECK (min_interval_seconds > 0 AND (daily_cap IS NULL OR daily_cap > 0)),
    CONSTRAINT ck_collector_app_platform_states_pause CHECK ((paused_at IS NULL) = (pause_reason IS NULL))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- 审计事件：只追加（由代码保证，没有触发器）。node_id / binding_id / platform_code / task_id 按事件类型填写，全部不建外键。
-- actor 记录是谁触发的：采集节点、运营人员或系统任务。detail 不得包含提问原文、回答内容或可识别的账号信息。
CREATE TABLE IF NOT EXISTS collector_app_events (
    id BINARY(16) NOT NULL PRIMARY KEY,
    occurred_at DATETIME(6) NOT NULL,
    event_kind VARCHAR(48) NOT NULL,
    node_id VARCHAR(128) NULL,
    binding_id BINARY(16) NULL,
    platform_code VARCHAR(64) NULL,
    task_id BINARY(16) NULL,
    actor VARCHAR(128) NOT NULL,
    detail JSON NULL,
    KEY ix_collector_app_events_occurred (occurred_at),
    KEY ix_collector_app_events_binding (binding_id, occurred_at),
    KEY ix_collector_app_events_platform_kind (platform_code, event_kind, occurred_at),
    CONSTRAINT ck_collector_app_events_kind CHECK (CHAR_LENGTH(TRIM(event_kind)) BETWEEN 1 AND 48),
    CONSTRAINT ck_collector_app_events_actor CHECK (CHAR_LENGTH(TRIM(actor)) BETWEEN 1 AND 128),
    CONSTRAINT ck_collector_app_events_detail_size CHECK (OCTET_LENGTH(detail) <= 16384)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- 每日告警汇报（results.md 8.4）：每个本地日期（Asia/Shanghai）一份，没有告警也有一份。
-- 首期只存档不发送（KD-19）：delivery_status 默认 not_configured；sent_at / send_error 留给以后接入群机器人。
CREATE TABLE IF NOT EXISTS collector_app_daily_reports (
    id BINARY(16) NOT NULL PRIMARY KEY,
    report_date DATE NOT NULL,
    generated_at DATETIME(6) NOT NULL,
    has_alerts BOOLEAN NOT NULL,
    summary JSON NOT NULL,
    body TEXT NOT NULL,
    delivery_status VARCHAR(16) NOT NULL DEFAULT 'not_configured',
    sent_at DATETIME(6) NULL,
    send_error VARCHAR(1000) NULL,
    UNIQUE KEY uq_collector_app_daily_reports_date (report_date),
    CONSTRAINT ck_collector_app_daily_reports_delivery CHECK (delivery_status IN ('not_configured', 'sent', 'failed')),
    CONSTRAINT ck_collector_app_daily_reports_summary_size CHECK (OCTET_LENGTH(summary) <= 262144)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- 运营人员把提问手动排除出 App 渠道（design 第 5 节）：只追加和撤销，不删除，本身就是审计记录。
-- active_prompt_id 只在未撤销时取 prompt_id（唯一键允许多个 NULL）：既用来查某条提问当前生效的排除，也保证同一条提问最多一条生效。
-- 提问的外键与 042 的 prompt_target_scopes 相同（site_id, prompt_id），但不级联删除：审计记录不随提问消失。
CREATE TABLE IF NOT EXISTS prompt_app_exclusions (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    prompt_id BINARY(16) NOT NULL,
    reason VARCHAR(500) NOT NULL,
    created_by BINARY(16) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    revoked_by BINARY(16) NULL,
    revoked_at DATETIME(6) NULL,
    active_prompt_id BINARY(16) GENERATED ALWAYS AS (CASE WHEN revoked_at IS NULL THEN prompt_id ELSE NULL END) STORED,
    UNIQUE KEY uq_prompt_app_exclusions_active (active_prompt_id),
    KEY ix_prompt_app_exclusions_site (tenant_id, site_id),
    KEY ix_prompt_app_exclusions_prompt (site_id, prompt_id, created_at),
    CONSTRAINT fk_prompt_app_exclusions_tenant_site FOREIGN KEY (tenant_id, site_id) REFERENCES sites (tenant_id, id),
    CONSTRAINT fk_prompt_app_exclusions_prompt FOREIGN KEY (site_id, prompt_id) REFERENCES prompts (site_id, id),
    CONSTRAINT ck_prompt_app_exclusions_reason CHECK (CHAR_LENGTH(TRIM(reason)) BETWEEN 1 AND 500),
    CONSTRAINT ck_prompt_app_exclusions_revoke CHECK ((revoked_at IS NULL) = (revoked_by IS NULL))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_049_app_channel//
CREATE PROCEDURE jogeoai_049_app_channel()
BEGIN
    -- 1. 回答：采集模式增加 app；记录采集它的绑定（无外键，见文件头）。
    IF NOT EXISTS (SELECT 1 FROM information_schema.check_constraints WHERE constraint_schema = DATABASE()
        AND constraint_name = 'ck_visibility_checks_capture_mode' AND check_clause LIKE '%''app%') THEN
        ALTER TABLE visibility_checks
            DROP CHECK ck_visibility_checks_capture_mode,
            ADD CONSTRAINT ck_visibility_checks_capture_mode
                CHECK (capture_mode IS NULL OR capture_mode IN ('api','scraped','app'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'visibility_checks' AND column_name = 'app_binding_id') THEN
        ALTER TABLE visibility_checks ADD COLUMN app_binding_id BINARY(16) NULL;
    END IF;

    -- 2. 平台入口：渠道放开为 consumer_web / consumer_app；不可用原因（例如 APP_CONTENT_UNREADABLE、APP_EMULATOR_REJECTED）。
    IF NOT EXISTS (SELECT 1 FROM information_schema.check_constraints WHERE constraint_schema = DATABASE()
        AND constraint_name = 'ck_collector_entries_channel' AND check_clause LIKE '%consumer_app%') THEN
        ALTER TABLE collector_platform_entries
            DROP CHECK ck_collector_entries_channel,
            ADD CONSTRAINT ck_collector_entries_channel CHECK (channel IN ('consumer_web', 'consumer_app'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'collector_platform_entries' AND column_name = 'unavailable_reason') THEN
        ALTER TABLE collector_platform_entries ADD COLUMN unavailable_reason VARCHAR(64) NULL;
    END IF;

    -- 3. 任务：渠道放开；App 任务的截止时点、排队序号、提问检查规则版本、当前租约的绑定、最近一次未能领取的原因。
    IF NOT EXISTS (SELECT 1 FROM information_schema.check_constraints WHERE constraint_schema = DATABASE()
        AND constraint_name = 'ck_collector_tasks_channel' AND check_clause LIKE '%consumer_app%') THEN
        ALTER TABLE collector_tasks
            DROP CHECK ck_collector_tasks_channel,
            ADD CONSTRAINT ck_collector_tasks_channel CHECK (channel IN ('consumer_web', 'consumer_app'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'collector_tasks' AND column_name = 'deadline_at') THEN
        ALTER TABLE collector_tasks ADD COLUMN deadline_at DATETIME(6) NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'collector_tasks' AND column_name = 'fair_rank') THEN
        ALTER TABLE collector_tasks ADD COLUMN fair_rank INT UNSIGNED NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'collector_tasks' AND column_name = 'prompt_rule_version') THEN
        ALTER TABLE collector_tasks ADD COLUMN prompt_rule_version INT UNSIGNED NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'collector_tasks' AND column_name = 'lease_app_binding_id') THEN
        ALTER TABLE collector_tasks ADD COLUMN lease_app_binding_id BINARY(16) NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'collector_tasks' AND column_name = 'last_block_reason') THEN
        ALTER TABLE collector_tasks ADD COLUMN last_block_reason VARCHAR(64) NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE()
        AND table_name = 'collector_tasks' AND index_name = 'ix_collector_tasks_app_claim') THEN
        ALTER TABLE collector_tasks ADD KEY ix_collector_tasks_app_claim (channel, platform_code, status, fair_rank, created_at);
    END IF;

    -- 4. 领取与失败记录：采集它的绑定（无外键）；按绑定统计最近领取次数的索引；失败码增加 App 的取值。
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'collector_attempts' AND column_name = 'app_binding_id') THEN
        ALTER TABLE collector_attempts ADD COLUMN app_binding_id BINARY(16) NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE()
        AND table_name = 'collector_attempts' AND index_name = 'ix_collector_attempts_app_binding_quota') THEN
        ALTER TABLE collector_attempts ADD KEY ix_collector_attempts_app_binding_quota (app_binding_id, event_kind, created_at);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.check_constraints WHERE constraint_schema = DATABASE()
        AND constraint_name = 'ck_collector_attempts_failure_code' AND check_clause LIKE '%emulator_rejected%') THEN
        ALTER TABLE collector_attempts
            DROP CHECK ck_collector_attempts_failure_code,
            ADD CONSTRAINT ck_collector_attempts_failure_code CHECK (
                (event_kind = 'failed') = (error_code IS NOT NULL)
                AND (error_code IS NULL OR error_code IN ('login_required', 'region_restricted', 'challenge_required', 'rate_limited',
                    'page_contract_changed', 'page_contract_unverified', 'collection_timeout', 'browser_failed', 'operator_archived',
                    'emulator_rejected', 'contract_changed', 'contract_unverified', 'version_mismatch', 'device_offline', 'app_crashed')));
    END IF;
END//
DELIMITER ;
CALL jogeoai_049_app_channel();
DROP PROCEDURE jogeoai_049_app_channel;

-- 5. App 入口种子：每个平台一条 <platform>.consumer_app，版本 1，地区 cn。文心一言的聊天内容读不到，入口停用。
-- 只插入缺少的行：已有的 App 入口（包括被运营停用的）和全部 consumer_web 入口都不改动。
INSERT INTO collector_platform_entries
    (id, platform_code, entry_code, version, display_name, region, channel, config, enabled, unavailable_reason, created_at)
SELECT UUID_TO_BIN(UUID()), s.platform_code, CONCAT(s.platform_code, '.consumer_app'), 1, s.display_name,
       'cn', 'consumer_app', JSON_OBJECT(), s.enabled, s.unavailable_reason, UTC_TIMESTAMP(6)
FROM (
    SELECT 'doubao' AS platform_code, '豆包 App' AS display_name, TRUE AS enabled, NULL AS unavailable_reason
    UNION ALL SELECT 'deepseek', 'DeepSeek App', TRUE, NULL
    UNION ALL SELECT 'tencent_yuanbao', '腾讯元宝 App', TRUE, NULL
    UNION ALL SELECT 'qwen', '千问 App', TRUE, NULL
    UNION ALL SELECT 'kimi', 'Kimi App', TRUE, NULL
    UNION ALL SELECT 'zhipu_qingyan', '智谱清言 App', TRUE, NULL
    UNION ALL SELECT 'ernie', '文心一言 App', FALSE, 'APP_CONTENT_UNREADABLE'
) s
WHERE NOT EXISTS (SELECT 1 FROM collector_platform_entries e
    WHERE e.entry_code = CONCAT(s.platform_code, '.consumer_app') AND e.version = 1);

-- 6. 平台闸门种子：7 个平台各一行，间隔取列默认值（30 秒），不设每日上限。只插入缺少的行，运营调整过的值不被覆盖。
INSERT INTO collector_app_platform_states (platform_code, created_at, updated_at)
SELECT s.platform_code, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)
FROM (
    SELECT 'doubao' AS platform_code
    UNION ALL SELECT 'deepseek'
    UNION ALL SELECT 'tencent_yuanbao'
    UNION ALL SELECT 'qwen'
    UNION ALL SELECT 'kimi'
    UNION ALL SELECT 'zhipu_qingyan'
    UNION ALL SELECT 'ernie'
) s
WHERE NOT EXISTS (SELECT 1 FROM collector_app_platform_states p WHERE p.platform_code = s.platform_code);
