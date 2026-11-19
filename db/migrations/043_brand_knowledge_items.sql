-- 043_brand_knowledge_items.sql
-- 品牌知识库维度列表（Trellis 11-03-brand-knowledge-dimensions，design.md §2）。
-- 手动执行于 001–042 之后；mysql 客户端须加 --default-character-set=utf8mb4（数据段含中文字面量）；API / Worker 启动不会执行本文件。
-- 建表可重复执行；唯一非加法改动是 ck_knowledge_documents_source_kind 加入 'item'（判存后 DROP 再 ADD）。
--   新表 brand_knowledge_items          品牌知识条目（维度 + 标题 + 内容 + 来源），检索投影文档由同步 Worker 单写；
--   新表 brand_knowledge_extract_jobs   网址识别任务（与 knowledge_product_parse_jobs 同构的租约表），只存网页摘要，不存正文。
-- 数据段：approved 产品迁为 product_service 条目（沿用产品 UUID，接管原知识库文档），依赖 legacy_product_id 唯一键可重复执行；
-- 末尾输出迁移报告（各品牌条目数、截断条目、未迁移产品、活跃文件资料数）。不删除、不停用任何产品或文件资料。
-- 所有 UUID 使用 BINARY(16)，时间由应用按 UTC 写入。
-- 发布顺序：先执行本迁移，再发布 API / Worker。回滚：见 db/migrations/rollback/043_brand_knowledge_items_rollback.sql。

-- 1. 既有表调整：knowledge_documents.source_kind 允许 'item'（条目检索投影）。
DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_043_brand_knowledge_items//
CREATE PROCEDURE jogeoai_043_brand_knowledge_items()
BEGIN
    IF DATABASE() IS NULL OR NOT EXISTS (SELECT 1 FROM information_schema.tables
        WHERE table_schema = DATABASE() AND table_name = 'knowledge_products') OR NOT EXISTS (
        SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'knowledge_documents' AND column_name = 'source_kind') THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '043 requires a selected database and migrations 012 and 023';
    END IF;

    -- 旧约束不含 'item' 时先删除；CHECK 约束名在库内唯一，按 check_clause 判断是否已是新版本，重放不会反复 DROP。
    -- MySQL 存储的 check_clause 形如 _utf8mb4\'item\'（引号前带反斜杠），只能按 item 子串匹配；其余允许值都不含 item。
    IF EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE()
        AND table_name = 'knowledge_documents' AND constraint_name = 'ck_knowledge_documents_source_kind')
        AND NOT EXISTS (SELECT 1 FROM information_schema.check_constraints WHERE constraint_schema = DATABASE()
        AND constraint_name = 'ck_knowledge_documents_source_kind' AND check_clause LIKE '%item%') THEN
        ALTER TABLE knowledge_documents DROP CHECK ck_knowledge_documents_source_kind;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE()
        AND table_name = 'knowledge_documents' AND constraint_name = 'ck_knowledge_documents_source_kind') THEN
        ALTER TABLE knowledge_documents ADD CONSTRAINT ck_knowledge_documents_source_kind
            CHECK (source_kind IN ('upload', 'link', 'product', 'item'));
    END IF;
END//
DELIMITER ;
CALL jogeoai_043_brand_knowledge_items();
DROP PROCEDURE jogeoai_043_brand_knowledge_items;

-- 2. 网址识别任务：每品牌同一时间只有一个未结束任务。
-- active_key 在 pending / running / done 且未保存或放弃时等于 site_id，否则为 NULL（唯一键允许多个 NULL）；
-- 过期的 done 任务由应用在读取或新建时惰性置为 discarded，不依赖定时器。
-- 只保存规范化网址、网页 hash、标题与字数；candidates_json 为候选列表（含原文依据片段），不保存网页正文。
-- error_message 不含凭据和模型原始响应。dimensions_json 为空表示识别全部维度。
CREATE TABLE IF NOT EXISTS brand_knowledge_extract_jobs (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    requested_by BINARY(16) NOT NULL,
    url VARCHAR(2048) COLLATE utf8mb4_0900_bin NOT NULL,
    url_hash CHAR(64) COLLATE utf8mb4_0900_bin NOT NULL,
    dimensions_json JSON NULL,
    -- pending 待处理；running 进行中；done 候选已就绪；failed 失败；cancelled 已取消；saved 已保存；discarded 已放弃或过期。
    status VARCHAR(16) NOT NULL DEFAULT 'pending',
    -- fetch 读取网页；model 模型识别；finalize 候选校验与查重。
    step VARCHAR(16) NOT NULL DEFAULT 'fetch',
    attempt_count INT UNSIGNED NOT NULL DEFAULT 0,
    lease_owner VARCHAR(128) NULL,
    lease_until DATETIME(6) NULL,
    next_attempt_at DATETIME(6) NOT NULL,
    page_title VARCHAR(500) NULL,
    page_hash CHAR(64) COLLATE utf8mb4_0900_bin NULL,
    page_characters INT UNSIGNED NULL,
    truncated BOOLEAN NOT NULL DEFAULT FALSE,
    candidates_json JSON NULL,
    error_code VARCHAR(64) NULL,
    error_message VARCHAR(1000) NULL,
    model_name VARCHAR(128) NULL,
    provider VARCHAR(64) NULL,
    duration_ms BIGINT UNSIGNED NULL,
    active_key BINARY(16) GENERATED ALWAYS AS (
        CASE WHEN status IN ('pending', 'running', 'done') AND resolved_at IS NULL THEN site_id ELSE NULL END) STORED,
    created_at DATETIME(6) NOT NULL,
    started_at DATETIME(6) NULL,
    completed_at DATETIME(6) NULL,
    -- completed_at + 24h；过期后候选不可保存。
    expires_at DATETIME(6) NULL,
    resolved_at DATETIME(6) NULL,
    updated_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_bkej_tenant_id (tenant_id, id),
    UNIQUE KEY uq_bkej_active (tenant_id, active_key),
    KEY ix_bkej_claim (status, next_attempt_at, lease_until),
    KEY ix_bkej_rate (tenant_id, created_at),
    KEY ix_bkej_site (tenant_id, site_id, created_at),
    CONSTRAINT fk_bkej_tenant FOREIGN KEY (tenant_id) REFERENCES tenants (id),
    CONSTRAINT fk_bkej_site FOREIGN KEY (tenant_id, site_id) REFERENCES sites (tenant_id, id),
    CONSTRAINT ck_bkej_status CHECK (status IN ('pending', 'running', 'done', 'failed', 'cancelled', 'saved', 'discarded')),
    CONSTRAINT ck_bkej_step CHECK (step IN ('fetch', 'model', 'finalize')),
    CONSTRAINT ck_bkej_resolved CHECK (status NOT IN ('saved', 'discarded') OR resolved_at IS NOT NULL),
    CONSTRAINT ck_bkej_url CHECK (CHAR_LENGTH(url) BETWEEN 1 AND 2048),
    CONSTRAINT ck_bkej_error CHECK (error_message IS NULL OR CHAR_LENGTH(error_message) <= 1000),
    CONSTRAINT ck_bkej_dimensions_size CHECK (dimensions_json IS NULL OR OCTET_LENGTH(dimensions_json) <= 1024),
    CONSTRAINT ck_bkej_candidates_size CHECK (candidates_json IS NULL OR OCTET_LENGTH(candidates_json) <= 262144)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- 3. 品牌知识条目：只属于品牌；同品牌同维度未删除条目的 title_key 唯一，软删后可再用同名。
-- title_key 由应用层 BrandKnowledgeRules.TitleKey 计算（NFKC 全角转半角、Trim、折叠空白、ToLowerInvariant）。
-- id 默认应用层生成；迁移条目沿用原产品 UUID（legacy_product_id = id），新条目与产品不会撞 UUID。
-- 每次编辑 content_version + 1、sync_status = 'pending'；同步 Worker 上传检索投影成功后写 synced_version。
CREATE TABLE IF NOT EXISTS brand_knowledge_items (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    -- brand_intro 品牌介绍；selling_point 核心卖点；audience 目标人群；differentiator 差异化优势；product_service 产品/服务；faq 常见问题。
    dimension VARCHAR(32) COLLATE utf8mb4_0900_bin NOT NULL,
    title VARCHAR(100) NOT NULL,
    title_key VARCHAR(100) COLLATE utf8mb4_0900_bin NOT NULL,
    content TEXT NOT NULL,
    active_title_key VARCHAR(140) COLLATE utf8mb4_0900_bin GENERATED ALWAYS AS (
        CASE WHEN deleted_at IS NULL THEN CONCAT(dimension, ':', title_key) ELSE NULL END) STORED,
    -- manual 手动填写；url 网址识别；migrated 由已入库产品迁移。
    source_kind VARCHAR(16) NOT NULL DEFAULT 'manual',
    source_url VARCHAR(2048) NULL,
    source_url_hash CHAR(64) COLLATE utf8mb4_0900_bin NULL,
    source_excerpt VARCHAR(1000) NULL,
    extract_job_id BINARY(16) NULL,
    legacy_product_id BINARY(16) NULL,
    content_version INT UNSIGNED NOT NULL DEFAULT 1,
    synced_version INT UNSIGNED NOT NULL DEFAULT 0,
    knowledge_document_id BINARY(16) NULL COMMENT '检索投影文档（source_kind=item）；NULL 表示尚未首次同步。',
    -- pending 待同步；synced 已同步；failed 同步失败（按 sync_next_at 退避重试）。
    sync_status VARCHAR(16) NOT NULL DEFAULT 'pending',
    sync_attempts INT UNSIGNED NOT NULL DEFAULT 0,
    sync_error VARCHAR(500) NULL,
    sync_next_at DATETIME(6) NOT NULL,
    sync_lease_owner VARCHAR(128) NULL,
    sync_lease_until DATETIME(6) NULL,
    created_by BINARY(16) NOT NULL,
    updated_by BINARY(16) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    deleted_at DATETIME(6) NULL,
    deleted_by BINARY(16) NULL,
    UNIQUE KEY uq_bki_tenant_id (tenant_id, id),
    UNIQUE KEY uq_bki_active_title (tenant_id, site_id, active_title_key),
    UNIQUE KEY uq_bki_legacy_product (tenant_id, legacy_product_id),
    KEY ix_bki_list (tenant_id, site_id, deleted_at, dimension, created_at),
    KEY ix_bki_sync (sync_status, sync_next_at, sync_lease_until),
    KEY ix_bki_document (tenant_id, knowledge_document_id),
    KEY ix_bki_extract_job (tenant_id, extract_job_id),
    CONSTRAINT fk_bki_tenant FOREIGN KEY (tenant_id) REFERENCES tenants (id),
    CONSTRAINT fk_bki_site FOREIGN KEY (tenant_id, site_id) REFERENCES sites (tenant_id, id),
    CONSTRAINT fk_bki_knowledge_document FOREIGN KEY (tenant_id, knowledge_document_id)
        REFERENCES knowledge_documents (tenant_id, id),
    CONSTRAINT fk_bki_extract_job FOREIGN KEY (tenant_id, extract_job_id)
        REFERENCES brand_knowledge_extract_jobs (tenant_id, id),
    CONSTRAINT fk_bki_legacy_product FOREIGN KEY (tenant_id, legacy_product_id)
        REFERENCES knowledge_products (tenant_id, id),
    CONSTRAINT fk_bki_creator FOREIGN KEY (created_by) REFERENCES auth_users (id),
    CONSTRAINT ck_bki_dimension CHECK (dimension IN ('brand_intro', 'selling_point', 'audience', 'differentiator', 'product_service', 'faq')),
    CONSTRAINT ck_bki_title CHECK (CHAR_LENGTH(TRIM(title)) BETWEEN 1 AND 100),
    CONSTRAINT ck_bki_title_key CHECK (CHAR_LENGTH(title_key) BETWEEN 1 AND 100),
    CONSTRAINT ck_bki_content CHECK (CHAR_LENGTH(content) BETWEEN 1 AND 2000),
    CONSTRAINT ck_bki_source_kind CHECK (source_kind IN ('manual', 'url', 'migrated')),
    CONSTRAINT ck_bki_url_source CHECK (source_kind <> 'url' OR source_url IS NOT NULL),
    CONSTRAINT ck_bki_excerpt CHECK (source_excerpt IS NULL OR CHAR_LENGTH(source_excerpt) <= 1000),
    CONSTRAINT ck_bki_versions CHECK (content_version >= 1 AND synced_version <= content_version),
    CONSTRAINT ck_bki_sync_status CHECK (sync_status IN ('pending', 'synced', 'failed')),
    CONSTRAINT ck_bki_sync_error CHECK (sync_error IS NULL OR CHAR_LENGTH(sync_error) <= 500),
    CONSTRAINT ck_bki_deleted CHECK (deleted_at IS NOT NULL OR deleted_by IS NULL)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- 4. 存量迁移（design §2.4）：approved 产品 → product_service 条目；可重复执行。
-- 4.1 先在会话临时表里算出每个 approved 产品的条目字段，供插入与报告共用（临时表随会话结束消失）。
--   title / title_key：SQL 只能做到 Trim + 折叠空白（ICU [[:space:]] 含 U+3000）+ LOWER；
--   与应用层 BrandKnowledgeRules.TitleKey 的差距是没有 NFKC（全角字母数字、全角标点不转半角）。
--   产品名本身未做 NFKC，含全角 ASCII 的标题会在报告 5 中列出，需人工核对重名（应用层重名判断可能与之不一致）。
--   content：CONCAT_WS 按「介绍 / 优势：/ 适用场景与人群：/ 客户案例：/ 规格参数：」拼接，规格来自 JSON_TABLE(specs_json)
--   的「名称：值」行；最终 LEFT(…, 2000)，超长条目在报告 2 中列出供人工精简。
--   所有字符串显式 COLLATE utf8mb4_0900_ai_ci，避免产品名（_bin）与 JSON_TABLE 列混用排序规则；换行用 CHAR(10 USING utf8mb4)，
--   不依赖反斜杠转义（NO_BACKSLASH_ESCAPES 下同样正确）。
-- GROUP_CONCAT 默认只保留 1024 字节，规格最多 50 项 × 230 字，必须先放大，否则规格会被静默截断。
SET SESSION group_concat_max_len = 1048576;
DROP TEMPORARY TABLE IF EXISTS tmp_043_product_items;
CREATE TEMPORARY TABLE tmp_043_product_items AS
SELECT src.*,
    ROW_NUMBER() OVER (PARTITION BY src.tenant_id, src.site_id, src.title_key ORDER BY src.approved_at, src.id) AS title_rank
FROM (
    SELECT p.id, p.tenant_id, p.site_id,
        TRIM(REGEXP_REPLACE(p.name, '[[:space:]]+', ' ')) AS title,
        LOWER(TRIM(REGEXP_REPLACE(p.name, '[[:space:]]+', ' '))) AS title_key,
        COALESCE(NULLIF(CONCAT_WS(CHAR(10 USING utf8mb4),
            NULLIF(TRIM(p.introduction COLLATE utf8mb4_0900_ai_ci), ''),
            CONCAT('优势：', NULLIF(TRIM(p.advantages COLLATE utf8mb4_0900_ai_ci), '')),
            CONCAT('适用场景与人群：', NULLIF(TRIM(p.scenarios COLLATE utf8mb4_0900_ai_ci), '')),
            CONCAT('客户案例：', NULLIF(TRIM(p.cases COLLATE utf8mb4_0900_ai_ci), '')),
            CONCAT('规格参数：', NULLIF(specs.specs_text, ''))), ''),
            TRIM(p.name) COLLATE utf8mb4_0900_ai_ci) AS full_content,
        p.product_url, p.knowledge_document_id, p.created_by, p.updated_by, p.created_at, p.updated_at, p.approved_at
    FROM knowledge_products p
    LEFT JOIN (
        SELECT ps.tenant_id, ps.id,
            -- 每行自带前导换行、分隔符为空串：SEPARATOR 只接受字面量，这样不需要反斜杠转义的换行字面量。
            GROUP_CONCAT(CONCAT(CHAR(10 USING utf8mb4), TRIM(spec.spec_name COLLATE utf8mb4_0900_ai_ci), '：',
                TRIM(spec.spec_value COLLATE utf8mb4_0900_ai_ci)) ORDER BY spec.ord SEPARATOR '') AS specs_text
        FROM knowledge_products ps,
            JSON_TABLE(ps.specs_json, '$[*]' COLUMNS (
                ord FOR ORDINALITY,
                spec_name VARCHAR(1000) PATH '$.name' NULL ON EMPTY NULL ON ERROR,
                spec_value VARCHAR(1000) PATH '$.value' NULL ON EMPTY NULL ON ERROR)) AS spec
        WHERE ps.status = 'approved' AND ps.specs_json IS NOT NULL
            AND COALESCE(TRIM(spec.spec_name), '') <> '' AND COALESCE(TRIM(spec.spec_value), '') <> ''
        GROUP BY ps.tenant_id, ps.id
    ) AS specs ON specs.tenant_id = p.tenant_id AND specs.id = p.id
    WHERE p.status = 'approved'
) AS src;

-- 4.2 插入条目并接管原文档；同一事务内完成，避免条目已建而文档仍是 source_kind='product'。
--   幂等：legacy_product_id 已存在则跳过（uq_bki_legacy_product）；同品牌同维度 title_key 冲突（批内大小写不同的产品名，
--   或重放前已手动建了同名条目）只迁移第一个，其余在报告 3 中列为「标题冲突未迁移」。
--   knowledge_document_id 沿用产品文档，synced_version=0 / sync_status='pending'：同步 Worker 用条目模板给同一文档上传新版本，
--   不新建文档，检索不会出现两份。
START TRANSACTION;

INSERT INTO brand_knowledge_items
    (id, tenant_id, site_id, dimension, title, title_key, content, source_kind, source_url, legacy_product_id,
     content_version, synced_version, knowledge_document_id, sync_status, sync_attempts, sync_next_at,
     created_by, updated_by, created_at, updated_at)
SELECT t.id, t.tenant_id, t.site_id, 'product_service', t.title, t.title_key, LEFT(t.full_content, 2000), 'migrated',
    t.product_url, t.id, 1, 0, t.knowledge_document_id, 'pending', 0, UTC_TIMESTAMP(6),
    t.created_by, t.updated_by, t.created_at, t.updated_at
FROM tmp_043_product_items t
WHERE t.title_rank = 1
    AND NOT EXISTS (SELECT 1 FROM brand_knowledge_items x
        WHERE x.tenant_id = t.tenant_id AND x.legacy_product_id = t.id)
    AND NOT EXISTS (SELECT 1 FROM brand_knowledge_items x
        WHERE x.tenant_id = t.tenant_id AND x.site_id = t.site_id AND x.deleted_at IS NULL
          AND x.dimension = 'product_service' AND x.title_key = t.title_key);

-- 被接管的产品文档改为条目投影；product_key 本来就是产品 UUID（= 条目 UUID），不变。只改仍为 source_kind='product' 的行，重放无影响。
UPDATE knowledge_documents d
JOIN brand_knowledge_items i ON i.tenant_id = d.tenant_id AND i.knowledge_document_id = d.id
SET d.source_kind = 'item', d.document_type = 'product_service', d.updated_at = UTC_TIMESTAMP(6)
WHERE i.source_kind = 'migrated' AND i.legacy_product_id IS NOT NULL AND d.source_kind = 'product';

COMMIT;

-- 5. 迁移报告（只读）。执行时保存输出，供上线前按 D10 决策与人工精简。
-- 报告 1：每品牌产品数、迁移条目数、全部未删除条目数；超过 50 条上限的品牌标记 over_limit=1（迁移不强制上限，需人工处理）。
SELECT BIN_TO_UUID(s.tenant_id) AS tenant_id, BIN_TO_UUID(s.id) AS site_id, s.display_name,
    (SELECT COUNT(*) FROM knowledge_products p WHERE p.tenant_id = s.tenant_id AND p.site_id = s.id) AS products_total,
    (SELECT COUNT(*) FROM knowledge_products p WHERE p.tenant_id = s.tenant_id AND p.site_id = s.id AND p.status = 'approved') AS products_approved,
    (SELECT COUNT(*) FROM brand_knowledge_items i WHERE i.tenant_id = s.tenant_id AND i.site_id = s.id
        AND i.source_kind = 'migrated' AND i.deleted_at IS NULL) AS items_migrated,
    (SELECT COUNT(*) FROM brand_knowledge_items i WHERE i.tenant_id = s.tenant_id AND i.site_id = s.id
        AND i.deleted_at IS NULL) AS items_active,
    (SELECT COUNT(*) FROM knowledge_documents d WHERE d.tenant_id = s.tenant_id AND d.site_id = s.id
        AND d.status <> 'disabled') AS documents_active,
    (SELECT COUNT(*) > 50 FROM brand_knowledge_items i WHERE i.tenant_id = s.tenant_id AND i.site_id = s.id
        AND i.deleted_at IS NULL) AS over_limit
FROM sites s
WHERE EXISTS (SELECT 1 FROM knowledge_products p WHERE p.tenant_id = s.tenant_id AND p.site_id = s.id)
   OR EXISTS (SELECT 1 FROM brand_knowledge_items i WHERE i.tenant_id = s.tenant_id AND i.site_id = s.id)
ORDER BY s.tenant_id, s.id;

-- 报告 2：迁移内容超过 2000 字被截断的条目，需人工精简。
SELECT BIN_TO_UUID(t.tenant_id) AS tenant_id, BIN_TO_UUID(t.site_id) AS site_id, BIN_TO_UUID(t.id) AS item_id,
    t.title, CHAR_LENGTH(t.full_content) AS original_characters
FROM tmp_043_product_items t
WHERE CHAR_LENGTH(t.full_content) > 2000
ORDER BY t.tenant_id, t.site_id, t.title;

-- 报告 3：未迁移的产品——非 approved（草稿 / 识别中 / 待核验 / 识别失败 / 已归档），以及 approved 但因标题冲突未迁移。
SELECT BIN_TO_UUID(p.tenant_id) AS tenant_id, BIN_TO_UUID(p.site_id) AS site_id, BIN_TO_UUID(p.id) AS product_id,
    p.name, p.status,
    CASE WHEN p.status = 'approved' THEN 'title_conflict' ELSE 'not_approved' END AS reason
FROM knowledge_products p
WHERE NOT EXISTS (SELECT 1 FROM brand_knowledge_items i WHERE i.tenant_id = p.tenant_id AND i.legacy_product_id = p.id)
ORDER BY p.tenant_id, p.site_id, p.status, p.name;

-- 报告 4：仍处于活跃状态的上传文件资料（source_kind='upload' 且 status<>'disabled'），按品牌计数；site_id 为空表示租户级资料。
SELECT BIN_TO_UUID(d.tenant_id) AS tenant_id, BIN_TO_UUID(d.site_id) AS site_id, d.status, COUNT(*) AS documents
FROM knowledge_documents d
WHERE d.source_kind = 'upload' AND d.status <> 'disabled'
GROUP BY d.tenant_id, d.site_id, d.status
ORDER BY d.tenant_id, d.site_id, d.status;

-- 报告 5：迁移条目标题含全角 ASCII（U+FF01–U+FF5E），SQL 计算的 title_key 未做全角转半角，可能与应用层重名判断不一致。
SELECT BIN_TO_UUID(i.tenant_id) AS tenant_id, BIN_TO_UUID(i.site_id) AS site_id, BIN_TO_UUID(i.id) AS item_id, i.title, i.title_key
FROM brand_knowledge_items i
WHERE i.source_kind = 'migrated' AND i.deleted_at IS NULL AND i.title REGEXP '[！-～]'
ORDER BY i.tenant_id, i.site_id, i.title;

DROP TEMPORARY TABLE IF EXISTS tmp_043_product_items;
