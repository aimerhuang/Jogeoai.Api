-- rollback/048_team_monitoring_defaults_rollback.sql
-- 回滚 048_team_monitoring_defaults.sql：删除 tenant_monitoring_default_cells、tenant_monitoring_defaults 两张表与
-- site_brand_settings.monitoring_follows_default 列。
-- 丢失的只有团队默认监控配置与品牌的跟随 / 单独配置标记；048 与之后的团队下发都只通过 site_targets 生效，
-- 回滚不改 site_targets，因此各品牌当前的采集行为（模型、回答模式、采集频率、凌晨计划）不变。没有拒绝条件。
-- 回滚顺序：先回滚前端（团队监控配置页），再回滚 API 到不含团队默认监控配置的版本，最后执行本脚本。
-- 执行前列明将丢失的对象数量并取得确认：
--   SELECT COUNT(*) AS default_configs FROM tenant_monitoring_defaults;
--   SELECT COUNT(*) AS default_cells FROM tenant_monitoring_default_cells;
--   SELECT monitoring_follows_default, COUNT(*) AS brands FROM site_brand_settings GROUP BY monitoring_follows_default;
--   -- 各团队默认配置明细（便于回滚后人工恢复）：
--   SELECT BIN_TO_UUID(d.tenant_id), d.config_version, c.provider, c.engine, c.answer_mode, c.frequency
--     FROM tenant_monitoring_defaults d LEFT JOIN tenant_monitoring_default_cells c ON c.tenant_id = d.tenant_id
--    ORDER BY d.tenant_id, c.provider, c.engine, c.answer_mode;
-- 手动执行，须用 root，mysql 客户端须加 --default-character-set=utf8mb4。可重放：对象不存在时跳过。

DROP TABLE IF EXISTS tenant_monitoring_default_cells;
DROP TABLE IF EXISTS tenant_monitoring_defaults;

DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_048_team_monitoring_defaults_rollback//
CREATE PROCEDURE jogeoai_048_team_monitoring_defaults_rollback()
BEGIN
    IF DATABASE() IS NULL THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '048 rollback requires a selected database';
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'site_brand_settings' AND column_name = 'monitoring_follows_default') THEN
        ALTER TABLE site_brand_settings DROP COLUMN monitoring_follows_default;
    END IF;
END//
DELIMITER ;
CALL jogeoai_048_team_monitoring_defaults_rollback();
DROP PROCEDURE jogeoai_048_team_monitoring_defaults_rollback;
