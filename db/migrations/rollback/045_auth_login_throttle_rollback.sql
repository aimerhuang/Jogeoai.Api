-- rollback/045_auth_login_throttle_rollback.sql
-- 回滚 045_auth_login_throttle.sql：删除 auth_login_throttle 表。
-- 执行前提：已先把 API 回退到不使用该表的版本并重启（design.md §8）。新版 API 在表删除后会让登录返回
--   503 AUTH_THROTTLE_SCHEMA_NOT_MIGRATED，因此必须先回退 API、确认不再使用该表，再执行本脚本。
-- 表内只有按邮箱 SHA-256 统计的失败计数与锁定截止时间，没有业务数据；DROP 后所有账号的失败计数与锁定立即清空。
-- 执行前仍须列明对象并取得确认：
--   SELECT scope, LEFT(subject_hash, 12) AS subject_prefix, consecutive_failures, locked_until, updated_at
--   FROM auth_login_throttle ORDER BY updated_at DESC;
-- 手动执行，mysql 客户端须加 --default-character-set=utf8mb4。可重放：表不存在时跳过。

DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_045_auth_login_throttle_rollback//
CREATE PROCEDURE jogeoai_045_auth_login_throttle_rollback()
BEGIN
    IF DATABASE() IS NULL THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '045 rollback requires a selected database';
    END IF;

    DROP TABLE IF EXISTS auth_login_throttle;
END//
DELIMITER ;
CALL jogeoai_045_auth_login_throttle_rollback();
DROP PROCEDURE jogeoai_045_auth_login_throttle_rollback;
