-- 系统管理员标记与全局研究 Provider 管理权限。
-- 手动执行于 008_auth_brand_onboarding.sql 之后；应用启动不会执行本文件。
-- 默认所有已注册用户均不是系统管理员，须由数据库管理员按精确用户 ID 授权。

DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_009_system_admin_column//
CREATE PROCEDURE jogeoai_009_system_admin_column()
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
        WHERE table_schema = DATABASE() AND table_name = 'auth_users' AND column_name = 'is_system_admin') THEN
        ALTER TABLE auth_users ADD COLUMN is_system_admin BOOLEAN NOT NULL DEFAULT FALSE AFTER status;
    END IF;
END//
DELIMITER ;
CALL jogeoai_009_system_admin_column();
DROP PROCEDURE jogeoai_009_system_admin_column;
