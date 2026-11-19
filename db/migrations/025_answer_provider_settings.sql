-- 025_answer_provider_settings.sql
-- API 回答 Provider 的全局设置表：一行 = 一个 Provider（首个为 doubao_api，火山方舟 Responses API + 豆包搜索）。
-- 手动执行于 001–024 之后；API / Worker 启动不会执行本文件。只做加法，可重复执行。
-- 执行时必须使用 --default-character-set=utf8mb4，否则非 ASCII 注释/种子会写成乱码。
-- 凭据按 010 / 022 的部署约定明文保存；任何接口、日志只回显 credential_hint（后 4 位）。
-- options 为 Provider 专属 JSON：{"requestMode":"web_search"|"doubao_app","sources":[...],"maxToolCalls":1-10,"reasoningEffort":null|minimal|low|medium|high}。
-- 回滚：DROP TABLE answer_provider_settings;  不影响其他表；此后 /api/v1/providers/doubao 返回 503 ANSWER_PROVIDER_SCHEMA_NOT_MIGRATED，
-- 目录中的豆包 Target 可用性为 unavailable，Cloro 链路不受影响。

CREATE TABLE IF NOT EXISTS answer_provider_settings (
    provider VARCHAR(64) NOT NULL PRIMARY KEY,
    api_url VARCHAR(2048) NOT NULL,
    model VARCHAR(128) NOT NULL,
    credential TEXT NULL COMMENT '明文，按部署约定；任何出口只回显 credential_hint。',
    credential_hint VARCHAR(32) NULL COMMENT '凭据后 4 位脱敏提示',
    enabled TINYINT(1) NOT NULL DEFAULT 0,
    timeout_seconds INT UNSIGNED NOT NULL DEFAULT 180,
    options JSON NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    CONSTRAINT ck_answer_provider_settings_options
        CHECK (options IS NULL OR (JSON_VALID(options) AND OCTET_LENGTH(options) <= 16384))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- 若旧版本建表时缺少约束或超时默认值，按 information_schema 补齐；已存在时不做任何修改。
DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_025_answer_provider_settings//
CREATE PROCEDURE jogeoai_025_answer_provider_settings()
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'answer_provider_settings' AND column_name = 'options') THEN
        ALTER TABLE answer_provider_settings ADD COLUMN options JSON NULL AFTER timeout_seconds;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'answer_provider_settings' AND column_name = 'credential_hint') THEN
        ALTER TABLE answer_provider_settings ADD COLUMN credential_hint VARCHAR(32) NULL AFTER credential;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE()
        AND table_name = 'answer_provider_settings' AND constraint_name = 'ck_answer_provider_settings_options') THEN
        ALTER TABLE answer_provider_settings ADD CONSTRAINT ck_answer_provider_settings_options
            CHECK (options IS NULL OR (JSON_VALID(options) AND OCTET_LENGTH(options) <= 16384));
    END IF;
END//
DELIMITER ;
CALL jogeoai_025_answer_provider_settings();
DROP PROCEDURE jogeoai_025_answer_provider_settings;

-- 为新库提供豆包默认配置：未启用、无凭据。
-- ON DUPLICATE KEY UPDATE provider = provider 保证重放不会覆盖管理员已经保存的地址、模型、凭据或开关。
INSERT INTO answer_provider_settings
    (provider, api_url, model, credential, credential_hint, enabled, timeout_seconds, options, created_at, updated_at)
VALUES
    ('doubao_api', 'https://ark.cn-beijing.volces.com/api/v3/responses', 'doubao-seed-2-1-pro-260915',
     NULL, NULL, 0, 180, '{"requestMode":"web_search","sources":["doubao"],"maxToolCalls":3}',
     UTC_TIMESTAMP(6), UTC_TIMESTAMP(6))
ON DUPLICATE KEY UPDATE provider = provider;
