-- 044_mcp_access_tokens.sql
-- MCP 只读访问令牌（Trellis 11-04-collection-mcp-endpoint，design.md「数据：迁移 044」）。
-- 手动执行于 001–043 之后；mysql 客户端须加 --default-character-set=utf8mb4；API / Worker 启动不会执行本文件。
-- 只做加法：新建 mcp_access_tokens 一张表，不修改任何已有表；CREATE TABLE IF NOT EXISTS 可重复执行。
-- 所有 UUID 使用 BINARY(16)（与 tenants / auth_users 外键一致），时间由应用按 UTC 写入。
-- 令牌明文只在创建响应中返回一次，库内只保存 SHA-256 hex 与前 12 个字符（含 jgm_ 前缀）用于识别；明文与哈希都不写日志。
-- 发布顺序：先执行本迁移，再发布 API（Worker 不受影响）。未执行时 /api/v1/mcp-tokens 与 /mcp 返回 503 MCP_SCHEMA_NOT_MIGRATED。
-- 回滚：见 db/migrations/rollback/044_mcp_access_tokens_rollback.sql。

DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_044_mcp_access_tokens//
CREATE PROCEDURE jogeoai_044_mcp_access_tokens()
BEGIN
    IF DATABASE() IS NULL OR NOT EXISTS (SELECT 1 FROM information_schema.tables
        WHERE table_schema = DATABASE() AND table_name = 'tenants') OR NOT EXISTS (
        SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'auth_users') THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '044 requires a selected database and migrations 002 and 008';
    END IF;
END//
DELIMITER ;
CALL jogeoai_044_mcp_access_tokens();
DROP PROCEDURE jogeoai_044_mcp_access_tokens;

-- site_ids 为 NULL 表示租户全部站点；否则为站点 UUID 字符串数组（最多 50 个，由应用校验均属于该租户）。
-- 启用令牌 = revoked_at IS NULL AND expires_at > 当前 UTC；每租户启用令牌上限 20 由应用在事务内（锁 tenants 行）检查。
-- last_used_at 由应用节流更新（距上次超过 60 秒才写）。
CREATE TABLE IF NOT EXISTS mcp_access_tokens (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    name VARCHAR(100) NOT NULL,
    token_prefix VARCHAR(12) COLLATE utf8mb4_0900_bin NOT NULL,
    token_hash CHAR(64) COLLATE utf8mb4_0900_bin NOT NULL,
    site_ids JSON NULL,
    created_by_user_id BINARY(16) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    expires_at DATETIME(6) NOT NULL,
    revoked_at DATETIME(6) NULL,
    last_used_at DATETIME(6) NULL,
    UNIQUE KEY uq_mcp_access_tokens_hash (token_hash),
    KEY ix_mcp_access_tokens_tenant_revoked (tenant_id, revoked_at),
    KEY ix_mcp_access_tokens_creator (created_by_user_id),
    CONSTRAINT fk_mcp_access_tokens_tenant FOREIGN KEY (tenant_id) REFERENCES tenants (id),
    CONSTRAINT fk_mcp_access_tokens_creator FOREIGN KEY (created_by_user_id) REFERENCES auth_users (id),
    CONSTRAINT ck_mcp_access_tokens_name CHECK (CHAR_LENGTH(TRIM(name)) BETWEEN 1 AND 100),
    CONSTRAINT ck_mcp_access_tokens_prefix CHECK (CHAR_LENGTH(token_prefix) = 12 AND LEFT(token_prefix, 4) = 'jgm_'),
    CONSTRAINT ck_mcp_access_tokens_hash CHECK (CHAR_LENGTH(token_hash) = 64),
    CONSTRAINT ck_mcp_access_tokens_site_ids CHECK (site_ids IS NULL
        OR (JSON_TYPE(site_ids) = 'ARRAY' AND OCTET_LENGTH(site_ids) <= 4096)),
    CONSTRAINT ck_mcp_access_tokens_dates CHECK (expires_at > created_at
        AND expires_at <= created_at + INTERVAL 366 DAY)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
