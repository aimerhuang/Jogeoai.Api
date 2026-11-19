-- rollback/040_article_studio_rollback.sql
-- 回滚 040_article_studio.sql：删除 studio 阶段/来源表与 article_generation_tasks 新列、约束、索引，
-- 并把 extra_requirements 恢复为 VARCHAR(4000)。
-- 执行前提（脚本会检查，不满足时 SIGNAL 中止且不做任何修改）：
--   1. 已回退 API / Worker 到不含文章创作管线的版本；
--   2. 没有 pipeline = 'studio_v1' 的任务（如需保留历史，请先导出后由负责人确认处理方式）；
--   3. 没有超过 4000 字符的 extra_requirements。
-- 手动执行，mysql 客户端须加 --default-character-set=utf8mb4。可重放：对象不存在时跳过。
-- 注意：DROP TABLE 会删除阶段记录与抓取正文，执行前请列明对象并取得确认。

DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_040_article_studio_rollback//
CREATE PROCEDURE jogeoai_040_article_studio_rollback()
BEGIN
    IF DATABASE() IS NULL THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '040 rollback requires a selected database';
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'article_generation_tasks' AND column_name = 'pipeline') THEN
        IF EXISTS (SELECT 1 FROM article_generation_tasks WHERE pipeline = 'studio_v1' LIMIT 1) THEN
            SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '040 rollback refused: studio_v1 tasks exist';
        END IF;
    END IF;
    IF EXISTS (SELECT 1 FROM article_generation_tasks WHERE CHAR_LENGTH(extra_requirements) > 4000 LIMIT 1) THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '040 rollback refused: extra_requirements longer than 4000 characters exist';
    END IF;

    DROP TABLE IF EXISTS article_generation_task_sources;
    DROP TABLE IF EXISTS article_generation_task_stages;

    IF EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE()
        AND table_name = 'article_generation_tasks' AND index_name = 'ix_article_generation_tasks_studio_prompt') THEN
        ALTER TABLE article_generation_tasks DROP INDEX ix_article_generation_tasks_studio_prompt;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE()
        AND table_name = 'article_generation_tasks' AND index_name = 'uq_article_generation_tasks_studio_active') THEN
        ALTER TABLE article_generation_tasks DROP INDEX uq_article_generation_tasks_studio_active;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'article_generation_tasks' AND column_name = 'studio_active_article_id') THEN
        ALTER TABLE article_generation_tasks DROP COLUMN studio_active_article_id;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE()
        AND table_name = 'article_generation_tasks' AND constraint_name = 'ck_article_generation_tasks_goal') THEN
        ALTER TABLE article_generation_tasks DROP CHECK ck_article_generation_tasks_goal;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'article_generation_tasks' AND column_name = 'optimization_goal') THEN
        ALTER TABLE article_generation_tasks DROP COLUMN optimization_goal;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'article_generation_tasks' AND column_name = 'include_competitors') THEN
        ALTER TABLE article_generation_tasks DROP COLUMN include_competitors;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE()
        AND table_name = 'article_generation_tasks' AND constraint_name = 'ck_article_generation_tasks_pipeline') THEN
        ALTER TABLE article_generation_tasks DROP CHECK ck_article_generation_tasks_pipeline;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'article_generation_tasks' AND column_name = 'pipeline') THEN
        ALTER TABLE article_generation_tasks DROP COLUMN pipeline;
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'article_generation_tasks' AND column_name = 'extra_requirements' AND LOWER(data_type) = 'text') THEN
        ALTER TABLE article_generation_tasks MODIFY COLUMN extra_requirements VARCHAR(4000) NULL;
    END IF;
END//
DELIMITER ;
CALL jogeoai_040_article_studio_rollback();
DROP PROCEDURE jogeoai_040_article_studio_rollback;
