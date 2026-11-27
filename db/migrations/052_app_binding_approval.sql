-- 052_app_binding_approval.sql
-- App 节点绑定审批与每节点设备上限（Trellis 11-27-sec-app-node-binding-approval，design.md 第 2 节；2026-11-27 审计 P2 / P2b）。
-- 只做加法，不建新表、不建触发器：
--   collector_app_bindings 新增 approved_at DATETIME(6) NULL：运营批准这条绑定的时间；为空 = 待批准（不能领取任务，
--     它的失败上报不写事件、不计入平台熔断）。批准沿用现有的恢复接口，批准之后不再清空。
--   collector_app_nodes 新增 max_devices INT UNSIGNED NOT NULL DEFAULT 4：该节点可登记的不同 device_id 数上限，
--     并加 CHECK ck_collector_app_nodes_max_devices（1–64）。
-- 不新增停止原因的取值，ck_collector_app_bindings_stop_reason 不动：待批准用现有的 disabled_by_operator 表示，采集端不需要升级。
-- 回填（只在本迁移第一次完成之前执行，见下面的“可重放”）：
--   已有绑定全部视为已批准：approved_at = created_at，status / stop_reason 不变；
--   已有节点的上限 = GREATEST(4, 该节点已登记的不同设备数)，不会出现“已登记但超限”。
-- 拒绝条件（不满足时 SIGNAL 中止，不做任何修改）：没有选库、未执行 049，或者第一次执行时某个节点已登记超过 64 台不同设备
--   （回填的上限会违反 CHECK；先列出并处理这些绑定：
--    SELECT node_id, COUNT(DISTINCT device_id) FROM collector_app_bindings GROUP BY node_id HAVING COUNT(DISTINCT device_id) > 64;）。
-- 依赖 049（collector_app_nodes、collector_app_bindings）。手动执行于 001–051 之后，mysql 客户端须加 --default-character-set=utf8mb4；
-- API / Worker 启动不会执行本文件。预期可以用应用账号执行（与 049 相同：ALTER 加存储过程，没有触发器），本迁移还没有在任何库上执行过。
-- 可重放：列按 information_schema 判断后再 ALTER。两条回填与最后一步“加 CHECK”绑在一起，只在 CHECK 还不存在时执行：
--   CHECK 存在 = 本迁移已经完成，重放时什么都不做。这一点是必须的：新版本上线后 approved_at 为空表示“待批准”，
--   如果重放时再回填一次，会把所有待批准的绑定直接批准。中途失败（列已加、CHECK 未加）时重跑会补完；
--   新版 API 在 CHECK 存在之前把本迁移视为未执行（503），所以那时库里还没有“待批准”的绑定，重复回填是安全的。
-- 发布顺序：先执行本迁移，再同时发布 API 与 Worker。未执行时，新版本里用到这两列的 App 入口
--   （绑定上报、领取、发题回执、失败上报、运营的节点与绑定接口）返回 503 APP_CHANNEL_SCHEMA_NOT_MIGRATED；告警与每日汇报照常生成，只是没有“待批准”一栏。
--   旧版 API 在本迁移之后登记的绑定 approved_at 为空：新版本上线后它们不能领取任务，下一次上报时变为待批准，需要运营批准一次。
-- 回滚：db/migrations/rollback/052_app_binding_approval_rollback.sql（先回退 API / Worker，再删除这两列和 CHECK）。

DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_052_app_binding_approval//
CREATE PROCEDURE jogeoai_052_app_binding_approval()
BEGIN
    DECLARE completed INT DEFAULT 0;

    IF DATABASE() IS NULL
        OR (SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE()
            AND table_name IN ('collector_app_nodes', 'collector_app_bindings')) <> 2 THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '052 requires a selected database and migration 049';
    END IF;

    SELECT COUNT(*) INTO completed FROM information_schema.check_constraints
     WHERE constraint_schema = DATABASE() AND constraint_name = 'ck_collector_app_nodes_max_devices';

    IF completed = 0 AND EXISTS (SELECT 1 FROM collector_app_bindings GROUP BY node_id HAVING COUNT(DISTINCT device_id) > 64) THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '052 refused: a node has more than 64 registered devices';
    END IF;

    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'collector_app_bindings' AND column_name = 'approved_at') THEN
        ALTER TABLE collector_app_bindings ADD COLUMN approved_at DATETIME(6) NULL;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'collector_app_nodes' AND column_name = 'max_devices') THEN
        ALTER TABLE collector_app_nodes ADD COLUMN max_devices INT UNSIGNED NOT NULL DEFAULT 4;
    END IF;

    IF completed = 0 THEN
        UPDATE collector_app_bindings SET approved_at = created_at WHERE approved_at IS NULL;

        UPDATE collector_app_nodes n
           SET n.max_devices = GREATEST(4, (SELECT COUNT(DISTINCT b.device_id) FROM collector_app_bindings b WHERE b.node_id = n.node_id));

        ALTER TABLE collector_app_nodes
            ADD CONSTRAINT ck_collector_app_nodes_max_devices CHECK (max_devices BETWEEN 1 AND 64);
    END IF;
END//
DELIMITER ;
CALL jogeoai_052_app_binding_approval();
DROP PROCEDURE jogeoai_052_app_binding_approval;
