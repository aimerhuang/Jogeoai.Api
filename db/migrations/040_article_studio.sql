-- 040_article_studio.sql
-- 文章创作（智能学习）两步流程的后端生成管线（Trellis 10-30-article-studio-backend-pipeline；父任务 10-30-article-studio-two-step）。
-- 只做加法与放宽：
--   article_generation_tasks 增加 pipeline（legacy / studio_v1，默认 legacy）、include_competitors、optimization_goal，
--     extra_requirements 由 VARCHAR(4000) 放宽为 TEXT（应用层：studio 限 5000 字符，旧向导仍限 2000）；
--     新增生成列 studio_active_article_id + 唯一键，保证同一文章同时只有一个进行中的 studio 任务；
--   新表 article_generation_task_stages   六阶段状态、起止时间与公开明细（detail_json）及内部产物（output_text）；
--   新表 article_generation_task_sources  阶段 1 引用源 / 阶段 4 联网搜索来源，含抓取状态与截断后的正文。
-- 旧数据全部保持 pipeline = 'legacy'，旧向导提交与 Worker 行为不变。
-- 依赖 014（article_generation_tasks / content_articles）；手动执行于 001–039 之后，
-- mysql 客户端须加 --default-character-set=utf8mb4；API / Worker 启动不会执行本文件。
-- 可重放：列、约束、索引按 information_schema 判断后再 ALTER；新表用 CREATE TABLE IF NOT EXISTS。
-- 发布顺序：先执行本迁移，再发布 API / Worker。未执行时 studio 接口返回 503 ARTICLE_STUDIO_SCHEMA_NOT_MIGRATED，
-- 旧向导与 Worker 认领按旧列读取，不受影响。
-- 回滚：见 db/migrations/rollback/040_article_studio_rollback.sql（需先确认没有 studio 任务和超过 4000 字符的写作要求）。

DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_040_article_studio//
CREATE PROCEDURE jogeoai_040_article_studio()
BEGIN
    IF DATABASE() IS NULL OR NOT EXISTS (SELECT 1 FROM information_schema.tables
        WHERE table_schema = DATABASE() AND table_name = 'article_generation_tasks') THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '040 requires a selected database and migration 014';
    END IF;

    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'article_generation_tasks' AND column_name = 'pipeline') THEN
        -- legacy：旧向导（一次模型调用）；studio_v1：文章创作六阶段管线。
        ALTER TABLE article_generation_tasks
            ADD COLUMN pipeline VARCHAR(16) NOT NULL DEFAULT 'legacy' AFTER without_rag;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE()
        AND table_name = 'article_generation_tasks' AND constraint_name = 'ck_article_generation_tasks_pipeline') THEN
        ALTER TABLE article_generation_tasks
            ADD CONSTRAINT ck_article_generation_tasks_pipeline CHECK (pipeline IN ('legacy','studio_v1'));
    END IF;

    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'article_generation_tasks' AND column_name = 'include_competitors') THEN
        ALTER TABLE article_generation_tasks
            ADD COLUMN include_competitors BOOLEAN NOT NULL DEFAULT FALSE AFTER pipeline;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'article_generation_tasks' AND column_name = 'optimization_goal') THEN
        -- mention 提及优化；ranking 排名优化；defend 巩固优势。旧任务为空。
        ALTER TABLE article_generation_tasks
            ADD COLUMN optimization_goal VARCHAR(32) NULL AFTER include_competitors;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE()
        AND table_name = 'article_generation_tasks' AND constraint_name = 'ck_article_generation_tasks_goal') THEN
        ALTER TABLE article_generation_tasks
            ADD CONSTRAINT ck_article_generation_tasks_goal CHECK (optimization_goal IS NULL OR optimization_goal IN ('mention','ranking','defend'));
    END IF;

    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'article_generation_tasks' AND column_name = 'extra_requirements' AND LOWER(data_type) <> 'text') THEN
        -- 自定义写作要求最多 5000 字符（CJK 可达 20000 字节），VARCHAR(4000) 放不下。
        ALTER TABLE article_generation_tasks MODIFY COLUMN extra_requirements TEXT NULL;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'article_generation_tasks' AND column_name = 'studio_active_article_id') THEN
        -- 仅 studio 任务在 pending / running 时取 article_id，其余为 NULL（唯一键允许多个 NULL）。
        ALTER TABLE article_generation_tasks
            ADD COLUMN studio_active_article_id BINARY(16) GENERATED ALWAYS AS (
                CASE WHEN pipeline = 'studio_v1' AND status IN ('pending','running') THEN article_id ELSE NULL END) STORED;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE()
        AND table_name = 'article_generation_tasks' AND index_name = 'uq_article_generation_tasks_studio_active') THEN
        ALTER TABLE article_generation_tasks
            ADD UNIQUE KEY uq_article_generation_tasks_studio_active (tenant_id, studio_active_article_id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE()
        AND table_name = 'article_generation_tasks' AND index_name = 'ix_article_generation_tasks_studio_prompt') THEN
        -- 第 1 步「返回当前稿件」：按提问取最近一篇 studio 文章。
        ALTER TABLE article_generation_tasks
            ADD KEY ix_article_generation_tasks_studio_prompt (tenant_id, site_id, prompt_id, pipeline, created_at);
    END IF;
END//
DELIMITER ;
CALL jogeoai_040_article_studio();
DROP PROCEDURE jogeoai_040_article_studio;

CREATE TABLE IF NOT EXISTS article_generation_task_stages (
    tenant_id BINARY(16) NOT NULL,
    task_id BINARY(16) NOT NULL,
    -- citations 高频引用源；fetch 抓取引用源；structure 结构提取；web_research 联网搜索；requirements 应用写作要求；compose 生成文章。
    stage_key VARCHAR(32) NOT NULL,
    position TINYINT UNSIGNED NOT NULL,
    -- pending 未开始；running 进行中；succeeded 完成；skipped 跳过（无数据或未配置，见 detail_json.reason）；failed 失败。
    status VARCHAR(16) NOT NULL DEFAULT 'pending',
    started_at DATETIME(6) NULL,
    finished_at DATETIME(6) NULL,
    -- 公开明细：计数、跳过原因、错误码；不含正文、提示词或凭据。
    detail_json JSON NULL,
    -- 内部产物（结构提取结果、联网搜索摘要），只供重试复用与生成输入，不通过接口返回。
    output_text MEDIUMTEXT NULL,
    updated_at DATETIME(6) NOT NULL,
    PRIMARY KEY (tenant_id, task_id, stage_key),
    CONSTRAINT fk_article_generation_task_stages_task FOREIGN KEY (tenant_id, task_id)
        REFERENCES article_generation_tasks (tenant_id, id),
    CONSTRAINT ck_article_generation_task_stages_key CHECK (stage_key IN ('citations','fetch','structure','web_research','requirements','compose')),
    CONSTRAINT ck_article_generation_task_stages_status CHECK (status IN ('pending','running','succeeded','skipped','failed')),
    CONSTRAINT ck_article_generation_task_stages_detail CHECK (detail_json IS NULL OR OCTET_LENGTH(detail_json) <= 65536),
    CONSTRAINT ck_article_generation_task_stages_output CHECK (output_text IS NULL OR CHAR_LENGTH(output_text) <= 100000)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS article_generation_task_sources (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    task_id BINARY(16) NOT NULL,
    -- citation：阶段 1 该提问近 30 天全来源高频引用 URL；web_search：阶段 4 联网搜索返回的来源（只记录标题与链接）。
    source_kind VARCHAR(16) NOT NULL,
    normalized_url VARCHAR(2048) COLLATE utf8mb4_0900_bin NOT NULL,
    domain VARCHAR(255) NOT NULL,
    title VARCHAR(500) NULL,
    engines_json JSON NULL,
    citation_count INT UNSIGNED NOT NULL DEFAULT 0,
    -- citation_classifications.ownership（brand / competitor / third_party / unknown）。
    ownership VARCHAR(32) NULL,
    is_competitor BOOLEAN NOT NULL DEFAULT FALSE,
    -- pending 待抓取；fetched 已读取正文；blocked 网页拒绝读取；failed 读取失败；skipped 不抓取。
    fetch_status VARCHAR(16) NOT NULL DEFAULT 'pending',
    error_code VARCHAR(64) NULL,
    -- 外部网页正文：只作结构参考与可引用来源，永不作为品牌事实；每条最多 20000 字符。
    content_text MEDIUMTEXT NULL,
    position INT UNSIGNED NOT NULL,
    created_at DATETIME(6) NOT NULL,
    KEY ix_article_generation_task_sources_task (tenant_id, task_id, position),
    CONSTRAINT fk_article_generation_task_sources_task FOREIGN KEY (tenant_id, task_id)
        REFERENCES article_generation_tasks (tenant_id, id),
    CONSTRAINT ck_article_generation_task_sources_kind CHECK (source_kind IN ('citation','web_search')),
    CONSTRAINT ck_article_generation_task_sources_fetch CHECK (fetch_status IN ('pending','fetched','blocked','failed','skipped')),
    CONSTRAINT ck_article_generation_task_sources_engines CHECK (engines_json IS NULL OR OCTET_LENGTH(engines_json) <= 4096),
    CONSTRAINT ck_article_generation_task_sources_content CHECK (content_text IS NULL OR CHAR_LENGTH(content_text) <= 20000)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
