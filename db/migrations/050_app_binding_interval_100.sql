-- 050_app_binding_interval_100.sql
-- App 绑定的领取间隔默认值从 300 秒改为 100 秒（用户 2026-11-17 决定，取代 KD-21 的 300 秒；平台合计 30 秒不变）。
-- 只改 collector_app_bindings.min_interval_seconds 的列默认值，并把仍是旧默认值 300 的行改为 100：
--   运营单独设过其他值的绑定不动。没有新表、新列，不改 CHECK。
-- 依赖 049。手动执行于 049 之后，mysql 客户端须加 --default-character-set=utf8mb4；API / Worker 启动不会执行本文件。
-- 可重放：ALTER ... SET DEFAULT 与 UPDATE ... WHERE = 300 重复执行结果相同。
-- 回滚：db/migrations/rollback/050_app_binding_interval_100_rollback.sql（默认值改回 300，值为 100 的行改回 300）。

ALTER TABLE collector_app_bindings
    ALTER COLUMN min_interval_seconds SET DEFAULT 100;

UPDATE collector_app_bindings
   SET min_interval_seconds = 100, updated_at = UTC_TIMESTAMP(6)
 WHERE min_interval_seconds = 300;
