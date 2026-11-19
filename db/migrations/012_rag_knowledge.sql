-- 品牌 RAG 知识库首期结构；手动执行于 001–011 之后，应用启动不会执行。
-- 所有 UUID 使用 BINARY(16)，时间由应用按 UTC 写入。资料正文不写入日志。

CREATE TABLE IF NOT EXISTS knowledge_folders (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NULL,
    parent_id BINARY(16) NULL,
    scope_site_key BINARY(16) GENERATED ALWAYS AS (COALESCE(site_id, 0x00000000000000000000000000000000)) STORED,
    scope_parent_key BINARY(16) GENERATED ALWAYS AS (COALESCE(parent_id, 0x00000000000000000000000000000000)) STORED,
    name VARCHAR(200) COLLATE utf8mb4_0900_bin NOT NULL,
    status VARCHAR(32) NOT NULL DEFAULT 'active',
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_knowledge_folders_scope_parent_name (tenant_id, scope_site_key, scope_parent_key, name),
    UNIQUE KEY uq_knowledge_folders_tenant_id (tenant_id, id),
    UNIQUE KEY uq_knowledge_folders_scope_id (tenant_id, scope_site_key, id),
    KEY ix_knowledge_folders_scope (tenant_id, site_id, status, created_at),
    CONSTRAINT fk_knowledge_folders_tenant FOREIGN KEY (tenant_id) REFERENCES tenants (id),
    CONSTRAINT fk_knowledge_folders_scope FOREIGN KEY (tenant_id, site_id) REFERENCES sites (tenant_id, id),
    CONSTRAINT fk_knowledge_folders_parent FOREIGN KEY (tenant_id, parent_id) REFERENCES knowledge_folders (tenant_id, id),
    CONSTRAINT fk_knowledge_folders_parent_scope FOREIGN KEY (tenant_id, scope_site_key, parent_id)
        REFERENCES knowledge_folders (tenant_id, scope_site_key, id),
    CONSTRAINT ck_knowledge_folders_name CHECK (CHAR_LENGTH(TRIM(name)) BETWEEN 1 AND 200),
    CONSTRAINT ck_knowledge_folders_status CHECK (status IN ('active', 'archived'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS knowledge_documents (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NULL,
    folder_id BINARY(16) NULL,
    scope_site_key BINARY(16) GENERATED ALWAYS AS (COALESCE(site_id, 0x00000000000000000000000000000000)) STORED,
    title VARCHAR(500) NOT NULL,
    status VARCHAR(32) NOT NULL DEFAULT 'pending',
    language VARCHAR(16) NOT NULL DEFAULT 'und',
    document_type VARCHAR(64) NOT NULL DEFAULT 'general',
    product_key VARCHAR(191) COLLATE utf8mb4_0900_bin NULL,
    effective_at DATETIME(6) NULL,
    expires_at DATETIME(6) NULL,
    current_version_id BINARY(16) NULL,
    config_version INT UNSIGNED NOT NULL DEFAULT 1,
    disabled_at DATETIME(6) NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_knowledge_documents_tenant_id (tenant_id, id),
    KEY ix_knowledge_documents_scope_folder (tenant_id, scope_site_key, folder_id),
    KEY ix_knowledge_documents_scope_status (tenant_id, site_id, status, updated_at),
    KEY ix_knowledge_documents_scope_current (tenant_id, site_id, current_version_id),
    KEY ix_knowledge_documents_expiry (tenant_id, site_id, status, effective_at, expires_at),
    CONSTRAINT fk_knowledge_documents_tenant FOREIGN KEY (tenant_id) REFERENCES tenants (id),
    CONSTRAINT fk_knowledge_documents_site FOREIGN KEY (tenant_id, site_id) REFERENCES sites (tenant_id, id),
    CONSTRAINT fk_knowledge_documents_folder FOREIGN KEY (tenant_id, folder_id) REFERENCES knowledge_folders (tenant_id, id),
    CONSTRAINT fk_knowledge_documents_scope_folder FOREIGN KEY (tenant_id, scope_site_key, folder_id)
        REFERENCES knowledge_folders (tenant_id, scope_site_key, id),
    CONSTRAINT ck_knowledge_documents_scope CHECK (site_id IS NULL OR site_id <> 0x00000000000000000000000000000000),
    CONSTRAINT ck_knowledge_documents_title CHECK (CHAR_LENGTH(TRIM(title)) BETWEEN 1 AND 500),
    CONSTRAINT ck_knowledge_documents_status CHECK (status IN ('pending', 'processing', 'ready', 'failed', 'disabled')),
    CONSTRAINT ck_knowledge_documents_dates CHECK (expires_at IS NULL OR effective_at IS NULL OR expires_at > effective_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS knowledge_document_tags (
    tenant_id BINARY(16) NOT NULL,
    document_id BINARY(16) NOT NULL,
    tag VARCHAR(128) COLLATE utf8mb4_0900_bin NOT NULL,
    created_at DATETIME(6) NOT NULL,
    PRIMARY KEY (tenant_id, document_id, tag),
    KEY ix_knowledge_document_tags_lookup (tenant_id, tag, document_id),
    CONSTRAINT fk_knowledge_document_tags_document FOREIGN KEY (tenant_id, document_id)
        REFERENCES knowledge_documents (tenant_id, id),
    CONSTRAINT ck_knowledge_document_tags_name CHECK (CHAR_LENGTH(TRIM(tag)) BETWEEN 1 AND 128)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS knowledge_document_versions (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    document_id BINARY(16) NOT NULL,
    version_number INT UNSIGNED NOT NULL,
    original_file_name VARCHAR(512) NOT NULL,
    extension VARCHAR(16) COLLATE utf8mb4_0900_bin NOT NULL,
    detected_content_type VARCHAR(128) NOT NULL,
    byte_length BIGINT UNSIGNED NOT NULL,
    sha256 CHAR(64) COLLATE utf8mb4_0900_bin NOT NULL,
    object_key VARCHAR(512) COLLATE utf8mb4_0900_bin NOT NULL,
    parser_version VARCHAR(64) NULL,
    character_count INT UNSIGNED NULL,
    page_count INT UNSIGNED NULL,
    status VARCHAR(32) NOT NULL DEFAULT 'pending',
    error_code VARCHAR(64) NULL,
    error_message VARCHAR(1000) NULL,
    completed_at DATETIME(6) NULL,
    created_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_knowledge_document_versions_number (tenant_id, document_id, version_number),
    UNIQUE KEY uq_knowledge_document_versions_tenant_id (tenant_id, id),
    UNIQUE KEY uq_knowledge_document_versions_document_id (tenant_id, document_id, id),
    UNIQUE KEY uq_knowledge_document_versions_object (object_key),
    KEY ix_knowledge_document_versions_status (tenant_id, document_id, status, created_at),
    KEY ix_knowledge_document_versions_hash (tenant_id, sha256),
    CONSTRAINT fk_knowledge_document_versions_document FOREIGN KEY (tenant_id, document_id)
        REFERENCES knowledge_documents (tenant_id, id),
    CONSTRAINT ck_knowledge_document_versions_size CHECK (byte_length BETWEEN 1 AND 104857600),
    CONSTRAINT ck_knowledge_document_versions_status CHECK (status IN ('pending', 'parsing', 'indexing', 'ready', 'failed', 'disabled')),
    CONSTRAINT ck_knowledge_document_versions_error CHECK (error_message IS NULL OR CHAR_LENGTH(error_message) <= 1000)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_012_current_version//
CREATE PROCEDURE jogeoai_012_current_version()
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics
        WHERE table_schema = DATABASE() AND table_name = 'knowledge_documents'
          AND index_name = 'uq_knowledge_documents_current_version') THEN
            ALTER TABLE knowledge_documents
            ADD UNIQUE KEY uq_knowledge_documents_current_version (tenant_id, id, current_version_id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.table_constraints
        WHERE table_schema = DATABASE() AND table_name = 'knowledge_documents'
          AND constraint_name = 'fk_knowledge_documents_current_version') THEN
            ALTER TABLE knowledge_documents
            ADD CONSTRAINT fk_knowledge_documents_current_version FOREIGN KEY (tenant_id, id, current_version_id)
                REFERENCES knowledge_document_versions (tenant_id, document_id, id);
    END IF;
END//
DELIMITER ;
CALL jogeoai_012_current_version();
DROP PROCEDURE jogeoai_012_current_version;

CREATE TABLE IF NOT EXISTS knowledge_document_chunks (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    document_id BINARY(16) NOT NULL,
    version_id BINARY(16) NOT NULL,
    ordinal INT UNSIGNED NOT NULL,
    text MEDIUMTEXT NOT NULL,
    heading_path VARCHAR(1000) NULL,
    location_json JSON NULL,
    character_count INT UNSIGNED NOT NULL,
    token_count INT UNSIGNED NULL,
    content_hash CHAR(64) COLLATE utf8mb4_0900_bin NOT NULL,
    status VARCHAR(32) NOT NULL DEFAULT 'active',
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_knowledge_document_chunks_version_ordinal (tenant_id, version_id, ordinal),
    UNIQUE KEY uq_knowledge_document_chunks_tenant_id (tenant_id, id),
    UNIQUE KEY uq_knowledge_document_chunks_tenant_version_id (tenant_id, version_id, id),
    UNIQUE KEY uq_knowledge_document_chunks_tenant_chunk_version (tenant_id, id, version_id),
    KEY ix_knowledge_document_chunks_document_version (tenant_id, document_id, version_id, status),
    CONSTRAINT fk_knowledge_document_chunks_document FOREIGN KEY (tenant_id, document_id)
        REFERENCES knowledge_documents (tenant_id, id),
    CONSTRAINT fk_knowledge_document_chunks_version FOREIGN KEY (tenant_id, version_id)
        REFERENCES knowledge_document_versions (tenant_id, id),
    CONSTRAINT fk_knowledge_document_chunks_document_version FOREIGN KEY (tenant_id, document_id, version_id)
        REFERENCES knowledge_document_versions (tenant_id, document_id, id),
    CONSTRAINT ck_knowledge_document_chunks_text CHECK (CHAR_LENGTH(TRIM(text)) > 0),
    CONSTRAINT ck_knowledge_document_chunks_location_size CHECK (OCTET_LENGTH(location_json) <= 16384)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS knowledge_embeddings (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    chunk_id BINARY(16) NOT NULL,
    profile VARCHAR(128) COLLATE utf8mb4_0900_bin NOT NULL,
    model VARCHAR(256) NOT NULL,
    dimensions INT UNSIGNED NOT NULL,
    vector_key VARCHAR(512) COLLATE utf8mb4_0900_bin NOT NULL,
    status VARCHAR(32) NOT NULL DEFAULT 'indexed',
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_knowledge_embeddings_chunk_profile (tenant_id, chunk_id, profile),
    UNIQUE KEY uq_knowledge_embeddings_vector_key (tenant_id, vector_key),
    KEY ix_knowledge_embeddings_status (tenant_id, profile, status),
    CONSTRAINT fk_knowledge_embeddings_chunk FOREIGN KEY (tenant_id, chunk_id)
        REFERENCES knowledge_document_chunks (tenant_id, id),
    CONSTRAINT ck_knowledge_embeddings_dimensions CHECK (dimensions > 0 AND dimensions <= 8192),
    CONSTRAINT ck_knowledge_embeddings_status CHECK (status IN ('pending', 'indexed', 'failed', 'deleted'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS knowledge_ingest_jobs (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    document_id BINARY(16) NOT NULL,
    version_id BINARY(16) NOT NULL,
    processor_version VARCHAR(64) NOT NULL DEFAULT 'local-v1',
    step VARCHAR(32) NOT NULL DEFAULT 'upload',
    status VARCHAR(32) NOT NULL DEFAULT 'pending',
    attempt_count INT UNSIGNED NOT NULL DEFAULT 0,
    lease_owner VARCHAR(128) NULL,
    lease_until DATETIME(6) NULL,
    next_attempt_at DATETIME(6) NOT NULL,
    error_code VARCHAR(64) NULL,
    error_message VARCHAR(1000) NULL,
    idempotency_key VARCHAR(191) COLLATE utf8mb4_0900_bin NOT NULL,
    started_at DATETIME(6) NULL,
    completed_at DATETIME(6) NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_knowledge_ingest_jobs_version_processor (tenant_id, version_id, processor_version),
    UNIQUE KEY uq_knowledge_ingest_jobs_tenant_id (tenant_id, id),
    UNIQUE KEY uq_knowledge_ingest_jobs_idempotency (tenant_id, idempotency_key),
    KEY ix_knowledge_ingest_jobs_claim (status, next_attempt_at, lease_until),
    KEY ix_knowledge_ingest_jobs_version (tenant_id, version_id, status),
    CONSTRAINT fk_knowledge_ingest_jobs_document FOREIGN KEY (tenant_id, document_id)
        REFERENCES knowledge_documents (tenant_id, id),
    CONSTRAINT fk_knowledge_ingest_jobs_version FOREIGN KEY (tenant_id, version_id)
        REFERENCES knowledge_document_versions (tenant_id, id),
    CONSTRAINT fk_knowledge_ingest_jobs_document_version FOREIGN KEY (tenant_id, document_id, version_id)
        REFERENCES knowledge_document_versions (tenant_id, document_id, id),
    CONSTRAINT ck_knowledge_ingest_jobs_status CHECK (status IN ('pending', 'running', 'done', 'failed', 'cancelled')),
    CONSTRAINT ck_knowledge_ingest_jobs_step CHECK (step IN ('upload', 'parse', 'chunk', 'embed', 'index', 'publish')),
    CONSTRAINT ck_knowledge_ingest_jobs_error CHECK (error_message IS NULL OR CHAR_LENGTH(error_message) <= 1000)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS knowledge_context_requests (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    query_summary VARCHAR(1000) NULL,
    scope_mode VARCHAR(32) NOT NULL DEFAULT 'default',
    filters_hash CHAR(64) COLLATE utf8mb4_0900_bin NOT NULL,
    requested_chunk_count INT UNSIGNED NOT NULL DEFAULT 0,
    character_count INT UNSIGNED NOT NULL DEFAULT 0,
    created_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_knowledge_context_requests_tenant_id (tenant_id, id),
    KEY ix_knowledge_context_requests_site_created (tenant_id, site_id, created_at),
    CONSTRAINT fk_knowledge_context_requests_site FOREIGN KEY (tenant_id, site_id)
        REFERENCES sites (tenant_id, id),
    CONSTRAINT ck_knowledge_context_requests_scope CHECK (scope_mode IN ('default', 'brand_only')),
    CONSTRAINT ck_knowledge_context_requests_summary CHECK (query_summary IS NULL OR CHAR_LENGTH(query_summary) <= 1000)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS knowledge_context_hits (
    context_id BINARY(16) NOT NULL,
    tenant_id BINARY(16) NOT NULL,
    position INT UNSIGNED NOT NULL,
    chunk_id BINARY(16) NOT NULL,
    version_id BINARY(16) NOT NULL,
    score DECIMAL(18,8) NOT NULL DEFAULT 0,
    created_at DATETIME(6) NOT NULL,
    PRIMARY KEY (context_id, position),
    KEY ix_knowledge_context_hits_chunk (tenant_id, chunk_id),
    CONSTRAINT fk_knowledge_context_hits_context FOREIGN KEY (tenant_id, context_id)
        REFERENCES knowledge_context_requests (tenant_id, id),
    CONSTRAINT fk_knowledge_context_hits_chunk FOREIGN KEY (tenant_id, chunk_id)
        REFERENCES knowledge_document_chunks (tenant_id, id),
    CONSTRAINT fk_knowledge_context_hits_chunk_version FOREIGN KEY (tenant_id, chunk_id, version_id)
        REFERENCES knowledge_document_chunks (tenant_id, id, version_id),
    CONSTRAINT ck_knowledge_context_hits_score CHECK (score >= -1000000 AND score <= 1000000)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
