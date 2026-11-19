-- 045_auth_login_throttle.sql
-- 登录失败频控的共享计数表（Trellis 11-07-sec-login-hardening，design.md §2）。
-- 手动执行于 001–044 之后；mysql 客户端须加 --default-character-set=utf8mb4；API / Worker 启动不会执行本文件。
-- 只做加法：新建 auth_login_throttle 一张表，不修改任何已有表；CREATE TABLE IF NOT EXISTS 可重复执行。
-- 规则（用户 2026-11-08 决定）：只统计失败，不设 IP 维度，不限制访问次数。按账号（规范化邮箱的 SHA-256）统计连续失败，
--   第 n 次连续失败（n ≥ 阈值，默认 30）后锁定 min(1 分钟 × 2^(n−30), 15 分钟)；成功登录复位；不永久锁号。阈值由配置 Auth:LoginThrottle 控制。
-- 库内只保存 SHA-256 hex，不保存邮箱明文，不记录 IP；不与 auth_users 建外键（不存在的邮箱同样计数与锁定，避免泄露账号是否存在）。
-- 时间由应用按 UTC 写入。清理为惰性：锁定到期后下一次请求即可通过，成功登录重置该行；可选清理（不自动执行）：
--   DELETE FROM auth_login_throttle WHERE updated_at < UTC_TIMESTAMP(6) - INTERVAL 7 DAY;
-- 发布顺序：先执行本迁移，再发布 API（Worker 不受影响）。未执行时 POST /api/v1/auth/login 返回 503 AUTH_THROTTLE_SCHEMA_NOT_MIGRATED
--   （失败即拒绝：漏迁移会让登录不可用，但不会在没有防护的情况下放行）。
-- 回滚：见 db/migrations/rollback/045_auth_login_throttle_rollback.sql。

DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_045_auth_login_throttle//
CREATE PROCEDURE jogeoai_045_auth_login_throttle()
BEGIN
    IF DATABASE() IS NULL OR NOT EXISTS (SELECT 1 FROM information_schema.tables
        WHERE table_schema = DATABASE() AND table_name = 'auth_users') OR NOT EXISTS (
        SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'auth_sessions') THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '045 requires a selected database and migration 008';
    END IF;
END//
DELIMITER ;
CALL jogeoai_045_auth_login_throttle();
DROP PROCEDURE jogeoai_045_auth_login_throttle;

-- scope：本期只写 'account'；保留该列便于以后增加维度而不改主键。
-- subject_hash：规范化邮箱的 SHA-256 小写 hex。
-- consecutive_failures：连续失败次数，成功登录时清零；locked_until：锁定截止（UTC），为 NULL 或早于当前时间即放行。
CREATE TABLE IF NOT EXISTS auth_login_throttle (
    scope VARCHAR(16) COLLATE utf8mb4_0900_bin NOT NULL,
    subject_hash CHAR(64) COLLATE utf8mb4_0900_bin NOT NULL,
    consecutive_failures INT NOT NULL DEFAULT 0,
    locked_until DATETIME(6) NULL,
    updated_at DATETIME(6) NOT NULL,
    PRIMARY KEY (scope, subject_hash),
    KEY ix_auth_login_throttle_updated (updated_at),
    CONSTRAINT ck_auth_login_throttle_scope CHECK (scope IN ('account')),
    CONSTRAINT ck_auth_login_throttle_hash CHECK (CHAR_LENGTH(subject_hash) = 64),
    CONSTRAINT ck_auth_login_throttle_failures CHECK (consecutive_failures >= 0)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
