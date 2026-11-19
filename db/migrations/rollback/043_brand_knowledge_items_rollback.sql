-- rollback/043_brand_knowledge_items_rollback.sql
-- 回滚 043_brand_knowledge_items.sql 的唯一非加法改动：把 ck_knowledge_documents_source_kind 恢复为 023 的允许集
-- ('upload', 'link', 'product')。
-- 执行前提（脚本会检查，不满足时 SIGNAL 中止且不做任何修改）：
--   1. 已先回滚前端，再回滚 API / Worker 到不含品牌知识库条目的版本（design.md §7）；
--   2. knowledge_documents 中不存在 source_kind = 'item' 的行。
-- 不删除 brand_knowledge_items / brand_knowledge_extract_jobs 两张表及其数据：旧构建不读取它们，保留不影响运行。
-- 如需长期回滚，先只读列明对象，取得负责人确认后再人工处理（下列语句默认注释，不会随本脚本执行）：
--   -- 列出条目投影文档：迁移接管的产品文档（有 legacy_product_id）与新建条目的文档（无对应产品）。
--   SELECT BIN_TO_UUID(d.tenant_id) AS tenant_id, BIN_TO_UUID(d.site_id) AS site_id, BIN_TO_UUID(d.id) AS document_id,
--       d.title, d.status, BIN_TO_UUID(i.id) AS item_id, BIN_TO_UUID(i.legacy_product_id) AS legacy_product_id
--   FROM knowledge_documents d
--   LEFT JOIN brand_knowledge_items i ON i.tenant_id = d.tenant_id AND i.knowledge_document_id = d.id
--   WHERE d.source_kind = 'item';
--   -- 确认后：迁移接管的产品文档交还给产品（旧构建按 document_type=product 识别产品资料）。
--   -- 注意：同步 Worker 可能已用条目模板上传过新版本，交还后由旧构建的 sync-knowledge 重新上传产品模板。
--   UPDATE knowledge_documents d
--   JOIN brand_knowledge_items i ON i.tenant_id = d.tenant_id AND i.knowledge_document_id = d.id
--   SET d.source_kind = 'product', d.document_type = 'product', d.updated_at = UTC_TIMESTAMP(6)
--   WHERE d.source_kind = 'item' AND i.legacy_product_id IS NOT NULL;
--   -- 确认后：无对应产品的条目文档由负责人决定停用（经知识库停用接口或单独审定的 SQL），本脚本不代为处理。
-- 手动执行，mysql 客户端须加 --default-character-set=utf8mb4。可重放：约束已是旧版本时跳过。

DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_043_brand_knowledge_items_rollback//
CREATE PROCEDURE jogeoai_043_brand_knowledge_items_rollback()
BEGIN
    IF DATABASE() IS NULL THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '043 rollback requires a selected database';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'knowledge_documents' AND column_name = 'source_kind') THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '043 rollback requires migration 023 (knowledge_documents.source_kind)';
    END IF;
    IF EXISTS (SELECT 1 FROM knowledge_documents WHERE source_kind = 'item' LIMIT 1) THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '043 rollback refused: knowledge_documents with source_kind item exist';
    END IF;

    -- MySQL 存储的 check_clause 形如 _utf8mb4\'item\'（引号前带反斜杠），只能按 item 子串匹配；其余允许值都不含 item。
    IF EXISTS (SELECT 1 FROM information_schema.check_constraints WHERE constraint_schema = DATABASE()
        AND constraint_name = 'ck_knowledge_documents_source_kind' AND check_clause LIKE '%item%') THEN
        ALTER TABLE knowledge_documents DROP CHECK ck_knowledge_documents_source_kind;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE()
        AND table_name = 'knowledge_documents' AND constraint_name = 'ck_knowledge_documents_source_kind') THEN
        ALTER TABLE knowledge_documents ADD CONSTRAINT ck_knowledge_documents_source_kind
            CHECK (source_kind IN ('upload', 'link', 'product'));
    END IF;
END//
DELIMITER ;
CALL jogeoai_043_brand_knowledge_items_rollback();
DROP PROCEDURE jogeoai_043_brand_knowledge_items_rollback;
