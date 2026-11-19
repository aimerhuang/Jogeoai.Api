-- rollback/050_app_binding_interval_100_rollback.sql
-- 回滚 050：collector_app_bindings.min_interval_seconds 的默认值改回 300，值为 100 的行改回 300。
-- 注意：回滚后无法区分“050 改成 100 的行”和“运营手工设为 100 的行”，两者都会变回 300。

ALTER TABLE collector_app_bindings
    ALTER COLUMN min_interval_seconds SET DEFAULT 300;

UPDATE collector_app_bindings
   SET min_interval_seconds = 300, updated_at = UTC_TIMESTAMP(6)
 WHERE min_interval_seconds = 100;
