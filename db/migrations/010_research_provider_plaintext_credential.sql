-- 将研究 Provider 凭据改为按部署方要求直接保存明文。
-- 手动执行于 009_system_admin_provider_access.sql 之后；应用启动不会执行本文件。
-- 旧版 008 创建的 credential_ciphertext 列在仍有数据时保留用于人工迁移，应用不再读取它。

DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_010_plaintext_credential//
CREATE PROCEDURE jogeoai_010_plaintext_credential()
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
        WHERE table_schema = DATABASE() AND table_name = 'research_provider_settings'
          AND column_name = 'credential') THEN
        ALTER TABLE research_provider_settings ADD COLUMN credential TEXT NULL AFTER api_url;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns
        WHERE table_schema = DATABASE() AND table_name = 'research_provider_settings'
          AND column_name = 'credential_ciphertext') THEN
        IF NOT EXISTS (SELECT 1 FROM research_provider_settings WHERE credential_ciphertext IS NOT NULL LIMIT 1) THEN
            ALTER TABLE research_provider_settings DROP COLUMN credential_ciphertext;
        END IF;
    END IF;
END//
DELIMITER ;
CALL jogeoai_010_plaintext_credential();
DROP PROCEDURE jogeoai_010_plaintext_credential;

-- 为新库提供默认的 OpenAI-compatible 配置；没有 API Key 时保持禁用。
-- INSERT IGNORE 保证重放 migration 不会覆盖管理员已经保存的配置。
INSERT IGNORE INTO research_provider_settings
    (provider, model, api_url, credential, credential_hint, priority, enabled,
     web_search_enabled, timeout_seconds, created_at, updated_at)
VALUES
    ('openai-compatible', 'gpt-6-luna', 'https://api.openai.com/v1/chat/completions',
     NULL, NULL, 10, FALSE, FALSE, 90, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
