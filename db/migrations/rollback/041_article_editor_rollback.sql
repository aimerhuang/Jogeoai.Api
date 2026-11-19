-- rollback/041_article_editor_rollback.sql
-- 回滚 041_article_editor.sql：删除 credit_ledger.refine_operation_id（外键、索引、列）、article_refine_operations、content_images，
-- 删除 content_article_versions.edit_revision，并把 version_kind CHECK 恢复为 generated / manual_edit / imported。
-- 执行前提（脚本会检查，不满足时 SIGNAL 中止且不做任何修改）：
--   1. 已回退 API 到不含文章编辑器接口的版本；
--   2. 没有 version_kind = 'refined' 的文章版本；
--   3. 没有智能微调操作（article_refine_operations 为空；有积分流水时须由负责人先确认对账处理）；
--   4. 没有编辑器图片（content_images 为空；对象存储 content-images/ 前缀下的对象需另行列明并确认后清理）。
-- 手动执行，mysql 客户端须加 --default-character-set=utf8mb4。可重放：对象不存在时跳过。
-- 注意：DROP TABLE / DROP COLUMN 不可逆，执行前请列明对象并取得确认。

DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_041_article_editor_rollback//
CREATE PROCEDURE jogeoai_041_article_editor_rollback()
BEGIN
    IF DATABASE() IS NULL THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '041 rollback requires a selected database';
    END IF;
    IF EXISTS (SELECT 1 FROM content_article_versions WHERE version_kind = 'refined' LIMIT 1) THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '041 rollback refused: refined article versions exist';
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE()
        AND table_name = 'article_refine_operations') THEN
        IF EXISTS (SELECT 1 FROM article_refine_operations LIMIT 1) THEN
            SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '041 rollback refused: article refine operations exist';
        END IF;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE()
        AND table_name = 'content_images') THEN
        IF EXISTS (SELECT 1 FROM content_images LIMIT 1) THEN
            SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '041 rollback refused: content images exist';
        END IF;
    END IF;

    IF EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE()
        AND table_name = 'credit_ledger' AND constraint_name = 'fk_credit_ledger_refine_operation') THEN
        ALTER TABLE credit_ledger DROP FOREIGN KEY fk_credit_ledger_refine_operation;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE()
        AND table_name = 'credit_ledger' AND index_name = 'ix_credit_ledger_refine_operation') THEN
        ALTER TABLE credit_ledger DROP INDEX ix_credit_ledger_refine_operation;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'credit_ledger' AND column_name = 'refine_operation_id') THEN
        ALTER TABLE credit_ledger DROP COLUMN refine_operation_id;
    END IF;

    DROP TABLE IF EXISTS article_refine_operations;
    DROP TABLE IF EXISTS content_images;

    IF EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE()
        AND table_name = 'content_article_versions' AND constraint_name = 'ck_content_article_versions_revision') THEN
        ALTER TABLE content_article_versions DROP CHECK ck_content_article_versions_revision;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'content_article_versions' AND column_name = 'edit_revision' AND LOWER(data_type) = 'int') THEN
        ALTER TABLE content_article_versions DROP COLUMN edit_revision;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.check_constraints WHERE constraint_schema = DATABASE()
        AND constraint_name = 'ck_content_article_versions_kind' AND check_clause LIKE '%refined%') THEN
        ALTER TABLE content_article_versions DROP CHECK ck_content_article_versions_kind;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.check_constraints WHERE constraint_schema = DATABASE()
        AND constraint_name = 'ck_content_article_versions_kind') THEN
        ALTER TABLE content_article_versions ADD CONSTRAINT ck_content_article_versions_kind
            CHECK (version_kind IN ('generated','manual_edit','imported'));
    END IF;
END//
DELIMITER ;
CALL jogeoai_041_article_editor_rollback();
DROP PROCEDURE jogeoai_041_article_editor_rollback;
