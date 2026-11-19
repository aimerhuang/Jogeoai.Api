-- 023_knowledge_products.sql
-- 素材库产品信息：产品实体、识别任务与产品标签；knowledge_documents 增加来源列，knowledge_document_versions 增加内容类型。
-- 手动执行于 001–022 之后；API / Worker 启动不会执行本文件。只做加法，可重复执行。
-- 模型输出只落草稿字段（draft 字段 + field_sources_json），入库是显式人工动作；任务表只保存输入摘要，不保存资料正文。
-- 所有 UUID 使用 BINARY(16)，时间由应用按 UTC 写入。

-- 既有表加列：存储过程判存后 ALTER，重放不会报错。
DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_023_knowledge_products//
CREATE PROCEDURE jogeoai_023_knowledge_products()
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'knowledge_documents' AND column_name = 'source_kind') THEN
        ALTER TABLE knowledge_documents ADD COLUMN source_kind VARCHAR(16) NOT NULL DEFAULT 'upload';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'knowledge_documents' AND column_name = 'source_url') THEN
        ALTER TABLE knowledge_documents ADD COLUMN source_url VARCHAR(2048) NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'knowledge_documents' AND column_name = 'source_url_hash') THEN
        ALTER TABLE knowledge_documents ADD COLUMN source_url_hash CHAR(64) COLLATE utf8mb4_0900_bin NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE()
        AND table_name = 'knowledge_documents' AND index_name = 'ix_knowledge_documents_source') THEN
        ALTER TABLE knowledge_documents ADD KEY ix_knowledge_documents_source (tenant_id, scope_site_key, source_kind);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE()
        AND table_name = 'knowledge_documents' AND constraint_name = 'ck_knowledge_documents_source_kind') THEN
        ALTER TABLE knowledge_documents ADD CONSTRAINT ck_knowledge_documents_source_kind
            CHECK (source_kind IN ('upload', 'link', 'product'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'knowledge_document_versions' AND column_name = 'content_kind') THEN
        ALTER TABLE knowledge_document_versions ADD COLUMN content_kind VARCHAR(16) NOT NULL DEFAULT 'text';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE()
        AND table_name = 'knowledge_document_versions' AND constraint_name = 'ck_knowledge_document_versions_content_kind') THEN
        ALTER TABLE knowledge_document_versions ADD CONSTRAINT ck_knowledge_document_versions_content_kind
            CHECK (content_kind IN ('text', 'image'));
    END IF;
END//
DELIMITER ;
CALL jogeoai_023_knowledge_products();
DROP PROCEDURE jogeoai_023_knowledge_products;

-- 产品信息实体：只属于品牌；名称在同品牌内唯一（二进制排序）。
-- 四个长文本字段应用层限 1000 字；specs_json 为 [{"name":"","value":""}]（≤ 50 项）；
-- field_sources_json 为 {字段: {origin, found, verified, truncated, evidence:[{quote, location}]}}。
CREATE TABLE IF NOT EXISTS knowledge_products (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    name VARCHAR(25) COLLATE utf8mb4_0900_bin NOT NULL,
    status VARCHAR(32) NOT NULL DEFAULT 'draft',
    source_kind VARCHAR(16) NOT NULL DEFAULT 'manual',
    introduction TEXT NULL,
    advantages TEXT NULL,
    scenarios TEXT NULL,
    cases TEXT NULL,
    specs_json JSON NULL,
    product_url VARCHAR(2048) NULL,
    field_sources_json JSON NULL,
    source_document_id BINARY(16) NULL,
    source_version_id BINARY(16) NULL,
    knowledge_document_id BINARY(16) NULL COMMENT '入库后对应的 document_type=product 资料；NULL 且 status=approved 表示知识库同步失败，可重试。',
    approved_at DATETIME(6) NULL,
    approved_by BINARY(16) NULL,
    created_by BINARY(16) NOT NULL,
    updated_by BINARY(16) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    archived_at DATETIME(6) NULL,
    UNIQUE KEY uq_knowledge_products_tenant_id (tenant_id, id),
    UNIQUE KEY uq_knowledge_products_name (tenant_id, site_id, name),
    KEY ix_knowledge_products_status (tenant_id, site_id, status, updated_at),
    KEY ix_knowledge_products_knowledge_document (tenant_id, knowledge_document_id),
    CONSTRAINT fk_knowledge_products_tenant FOREIGN KEY (tenant_id) REFERENCES tenants (id),
    CONSTRAINT fk_knowledge_products_site FOREIGN KEY (tenant_id, site_id) REFERENCES sites (tenant_id, id),
    CONSTRAINT fk_knowledge_products_source_document FOREIGN KEY (tenant_id, source_document_id)
        REFERENCES knowledge_documents (tenant_id, id),
    CONSTRAINT fk_knowledge_products_source_version FOREIGN KEY (tenant_id, source_document_id, source_version_id)
        REFERENCES knowledge_document_versions (tenant_id, document_id, id),
    CONSTRAINT fk_knowledge_products_knowledge_document FOREIGN KEY (tenant_id, knowledge_document_id)
        REFERENCES knowledge_documents (tenant_id, id),
    CONSTRAINT fk_knowledge_products_creator FOREIGN KEY (created_by) REFERENCES auth_users (id),
    CONSTRAINT ck_knowledge_products_name CHECK (CHAR_LENGTH(TRIM(name)) BETWEEN 1 AND 25),
    CONSTRAINT ck_knowledge_products_status CHECK (status IN ('draft', 'parsing', 'pending_review', 'approved', 'parse_failed', 'archived')),
    CONSTRAINT ck_knowledge_products_source_kind CHECK (source_kind IN ('manual', 'file', 'url', 'completion')),
    CONSTRAINT ck_knowledge_products_introduction CHECK (introduction IS NULL OR CHAR_LENGTH(introduction) <= 1000),
    CONSTRAINT ck_knowledge_products_advantages CHECK (advantages IS NULL OR CHAR_LENGTH(advantages) <= 1000),
    CONSTRAINT ck_knowledge_products_scenarios CHECK (scenarios IS NULL OR CHAR_LENGTH(scenarios) <= 1000),
    CONSTRAINT ck_knowledge_products_cases CHECK (cases IS NULL OR CHAR_LENGTH(cases) <= 1000),
    CONSTRAINT ck_knowledge_products_specs_size CHECK (specs_json IS NULL OR OCTET_LENGTH(specs_json) <= 16384),
    CONSTRAINT ck_knowledge_products_sources_size CHECK (field_sources_json IS NULL OR OCTET_LENGTH(field_sources_json) <= 65536),
    CONSTRAINT ck_knowledge_products_approved CHECK (status <> 'approved' OR approved_at IS NOT NULL)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- 产品标签，复用 knowledge_document_tags 的方式。
CREATE TABLE IF NOT EXISTS knowledge_product_tags (
    tenant_id BINARY(16) NOT NULL,
    product_id BINARY(16) NOT NULL,
    tag VARCHAR(128) COLLATE utf8mb4_0900_bin NOT NULL,
    created_at DATETIME(6) NOT NULL,
    PRIMARY KEY (tenant_id, product_id, tag),
    KEY ix_knowledge_product_tags_lookup (tenant_id, tag, product_id),
    CONSTRAINT fk_knowledge_product_tags_product FOREIGN KEY (tenant_id, product_id)
        REFERENCES knowledge_products (tenant_id, id),
    CONSTRAINT ck_knowledge_product_tags_name CHECK (CHAR_LENGTH(TRIM(tag)) BETWEEN 1 AND 128)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- 识别任务：与 knowledge_ingest_jobs 同构的租约表。
-- active_key 在 pending/running 时等于 product_id，否则为 NULL；唯一键保证同一产品同一时间只有一个进行中任务。
-- input_summary_json 只保存已填字段摘要、图片张数和文本字数，不含资料原文；error_message 不含凭据和模型原始响应。
CREATE TABLE IF NOT EXISTS knowledge_product_parse_jobs (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    product_id BINARY(16) NOT NULL,
    job_kind VARCHAR(16) NOT NULL,
    source_document_id BINARY(16) NULL,
    source_version_id BINARY(16) NULL,
    input_summary_json JSON NULL,
    status VARCHAR(32) NOT NULL DEFAULT 'pending',
    step VARCHAR(32) NOT NULL DEFAULT 'read_source',
    attempt_count INT UNSIGNED NOT NULL DEFAULT 0,
    lease_owner VARCHAR(128) NULL,
    lease_until DATETIME(6) NULL,
    next_attempt_at DATETIME(6) NOT NULL,
    error_code VARCHAR(64) NULL,
    error_message VARCHAR(1000) NULL,
    model_name VARCHAR(128) NULL,
    provider VARCHAR(64) NULL,
    duration_ms BIGINT UNSIGNED NULL,
    truncated BOOLEAN NOT NULL DEFAULT FALSE,
    image_count INT UNSIGNED NOT NULL DEFAULT 0,
    requested_by BINARY(16) NOT NULL,
    active_key BINARY(16) GENERATED ALWAYS AS (CASE WHEN status IN ('pending', 'running') THEN product_id ELSE NULL END) STORED,
    created_at DATETIME(6) NOT NULL,
    started_at DATETIME(6) NULL,
    completed_at DATETIME(6) NULL,
    updated_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_product_parse_jobs_tenant_id (tenant_id, id),
    UNIQUE KEY uq_product_parse_jobs_active (tenant_id, active_key),
    KEY ix_product_parse_jobs_claim (status, next_attempt_at, lease_until),
    KEY ix_product_parse_jobs_product (tenant_id, product_id, status),
    KEY ix_product_parse_jobs_rate (tenant_id, created_at),
    CONSTRAINT fk_product_parse_jobs_site FOREIGN KEY (tenant_id, site_id) REFERENCES sites (tenant_id, id),
    CONSTRAINT fk_product_parse_jobs_product FOREIGN KEY (tenant_id, product_id)
        REFERENCES knowledge_products (tenant_id, id),
    CONSTRAINT fk_product_parse_jobs_source_document FOREIGN KEY (tenant_id, source_document_id)
        REFERENCES knowledge_documents (tenant_id, id),
    CONSTRAINT fk_product_parse_jobs_source_version FOREIGN KEY (tenant_id, source_document_id, source_version_id)
        REFERENCES knowledge_document_versions (tenant_id, document_id, id),
    CONSTRAINT ck_product_parse_jobs_kind CHECK (job_kind IN ('file', 'url', 'completion')),
    CONSTRAINT ck_product_parse_jobs_status CHECK (status IN ('pending', 'running', 'done', 'failed', 'cancelled')),
    CONSTRAINT ck_product_parse_jobs_step CHECK (step IN ('read_source', 'model', 'finalize')),
    CONSTRAINT ck_product_parse_jobs_error CHECK (error_message IS NULL OR CHAR_LENGTH(error_message) <= 1000),
    CONSTRAINT ck_product_parse_jobs_summary_size CHECK (input_summary_json IS NULL OR OCTET_LENGTH(input_summary_json) <= 16384)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
