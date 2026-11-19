-- 保存写作上下文的完整返回快照；在 012_rag_knowledge.sql 之后手动执行。
-- 保留旧记录的 NULL 值，读取时兼容历史命中记录。

DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_013_context_snapshot//
CREATE PROCEDURE jogeoai_013_context_snapshot()
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
        WHERE table_schema = DATABASE() AND table_name = 'knowledge_context_requests'
          AND column_name = 'snapshot_json') THEN
        ALTER TABLE knowledge_context_requests ADD COLUMN snapshot_json JSON NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.table_constraints
        WHERE table_schema = DATABASE() AND table_name = 'knowledge_context_requests'
          AND constraint_name = 'ck_knowledge_context_requests_snapshot') THEN
        ALTER TABLE knowledge_context_requests
            ADD CONSTRAINT ck_knowledge_context_requests_snapshot
            CHECK (snapshot_json IS NULL OR OCTET_LENGTH(snapshot_json) <= 4194304);
    END IF;
END//
DELIMITER ;
CALL jogeoai_013_context_snapshot();
DROP PROCEDURE jogeoai_013_context_snapshot;
