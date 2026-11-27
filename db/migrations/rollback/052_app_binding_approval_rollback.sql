-- rollback/052_app_binding_approval_rollback.sql
-- 回滚 052_app_binding_approval.sql：删除 CHECK ck_collector_app_nodes_max_devices、collector_app_nodes.max_devices 和
-- collector_app_bindings.approved_at。其余列和全部行不动，没有拒绝条件。
-- 回滚顺序：先把 API / Worker 回退到不含绑定审批的版本并重启，再执行本脚本（新版本在这两列不存在时返回 503）。
-- 回滚之后的行为（旧版本）：新上报的绑定立即生效，没有每节点设备上限，心跳没有总时长上限。
--   回滚前待批准的绑定仍是 stopped / disabled_by_operator：旧版本里采集端上报不会解除它，只有运营调用恢复接口才会生效。
-- 会丢失的数据（执行前列明并取得确认）：
--   SELECT node_id, max_devices FROM collector_app_nodes ORDER BY node_id;
--   SELECT BIN_TO_UUID(id), node_id, device_id, platform_code, account_label, status, stop_reason, approved_at
--     FROM collector_app_bindings ORDER BY node_id, device_id, platform_code;
-- 注意：回滚后再次执行 052 时，当时库里的全部绑定（包括回滚前待批准的）都会被回填为已批准。
--   重新执行 052 之前，先删除或确认上面查询里 approved_at 为空的那些绑定。
-- 手动执行，预期可以用应用账号（与 052 相同，未实测），mysql 客户端须加 --default-character-set=utf8mb4。
-- 可重放：对象不存在时跳过。CHECK 先于它引用的列删除。

DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_052_app_binding_approval_rollback//
CREATE PROCEDURE jogeoai_052_app_binding_approval_rollback()
BEGIN
    IF DATABASE() IS NULL THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '052 rollback requires a selected database';
    END IF;

    IF EXISTS (SELECT 1 FROM information_schema.check_constraints WHERE constraint_schema = DATABASE()
        AND constraint_name = 'ck_collector_app_nodes_max_devices') THEN
        ALTER TABLE collector_app_nodes DROP CHECK ck_collector_app_nodes_max_devices;
    END IF;

    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'collector_app_nodes' AND column_name = 'max_devices') THEN
        ALTER TABLE collector_app_nodes DROP COLUMN max_devices;
    END IF;

    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'collector_app_bindings' AND column_name = 'approved_at') THEN
        ALTER TABLE collector_app_bindings DROP COLUMN approved_at;
    END IF;
END//
DELIMITER ;
CALL jogeoai_052_app_binding_approval_rollback();
DROP PROCEDURE jogeoai_052_app_binding_approval_rollback;
