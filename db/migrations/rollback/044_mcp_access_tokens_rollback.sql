-- rollback/044_mcp_access_tokens_rollback.sql
-- 回滚 044_mcp_access_tokens.sql：删除 mcp_access_tokens 表。
-- 执行前提（脚本会检查，不满足时 SIGNAL 中止且不做任何修改）：
--   1. 已先回滚 API 到不含 MCP 端点的版本（design.md「兼容与发布」）；
--   2. 不存在启用中的令牌（revoked_at IS NULL 且 expires_at 晚于当前 UTC）。如需回滚，先经 DELETE /api/v1/mcp-tokens/{id}
--      吊销，或由负责人确认后人工处理；已过期或已吊销的令牌不阻止回滚。
-- 注意：DROP TABLE 会删除令牌记录（名称、前缀、创建人、最后使用时间），执行前请列明对象并取得确认：
--   SELECT BIN_TO_UUID(tenant_id) AS tenant_id, BIN_TO_UUID(id) AS token_id, name, token_prefix, expires_at, revoked_at
--   FROM mcp_access_tokens ORDER BY tenant_id, created_at;
-- 手动执行，mysql 客户端须加 --default-character-set=utf8mb4。可重放：表不存在时跳过。

DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_044_mcp_access_tokens_rollback//
CREATE PROCEDURE jogeoai_044_mcp_access_tokens_rollback()
BEGIN
    IF DATABASE() IS NULL THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '044 rollback requires a selected database';
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE()
        AND table_name = 'mcp_access_tokens') THEN
        IF EXISTS (SELECT 1 FROM mcp_access_tokens
            WHERE revoked_at IS NULL AND expires_at > UTC_TIMESTAMP(6) LIMIT 1) THEN
            SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '044 rollback refused: active MCP access tokens exist';
        END IF;
    END IF;

    DROP TABLE IF EXISTS mcp_access_tokens;
END//
DELIMITER ;
CALL jogeoai_044_mcp_access_tokens_rollback();
DROP PROCEDURE jogeoai_044_mcp_access_tokens_rollback;
