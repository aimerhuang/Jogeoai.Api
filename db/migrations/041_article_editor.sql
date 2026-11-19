-- 041_article_editor.sql
-- 文章编辑器后端：自动保存草稿、智能微调计费、编辑器图片（Trellis 10-30-article-editor-backend；父任务 10-30-article-studio-two-step）。
-- 只做加法与放宽：
--   content_article_versions.version_kind CHECK 增加 refined（智能微调结果版本）；
--   content_article_versions 新增 edit_revision INT NOT NULL DEFAULT 0（草稿原地覆盖时自增，作乐观锁；其他版本恒为 0）；
--   新表 article_refine_operations：智能微调 40 积分/次的独立预占（held → captured / released），幂等键租户内唯一；
--   credit_ledger 新增可空 refine_operation_id 及外键（参照 019 / 027 的独立预占 + 账本外键列模式，entry_type 不新增）；
--   新表 content_images：编辑器上传图片的元数据，对象本体在对象存储 content-images/{tenantId}/{imageId}，与知识库对象键隔离。
-- 依赖 014（content_articles / content_article_versions / credit_accounts / credit_ledger）；手动执行于 001–040 之后，
-- mysql 客户端须加 --default-character-set=utf8mb4；API / Worker 启动不会执行本文件。
-- 可重放：列、约束、索引按 information_schema 判断后再 ALTER；新表用 CREATE TABLE IF NOT EXISTS。
-- 发布顺序：先执行本迁移，再发布 API。未执行时 draft / refine / images 接口返回 503 ARTICLE_EDITOR_SCHEMA_NOT_MIGRATED，
-- 文章详情的 editRevision 恒为 0，积分用量不含智能微调；PATCH 编辑与 Markdown 导出不受影响。
-- 回滚：见 db/migrations/rollback/041_article_editor_rollback.sql（存在 refined 版本、微调流水或图片时拒绝执行）。

DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_041_article_editor//
CREATE PROCEDURE jogeoai_041_article_editor()
BEGIN
    IF DATABASE() IS NULL OR NOT EXISTS (SELECT 1 FROM information_schema.tables
        WHERE table_schema = DATABASE() AND table_name = 'content_article_versions') THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '041 requires a selected database and migration 014';
    END IF;

    -- 1. 版本来源增加 refined（智能微调）。
    IF EXISTS (SELECT 1 FROM information_schema.check_constraints WHERE constraint_schema = DATABASE()
        AND constraint_name = 'ck_content_article_versions_kind' AND check_clause NOT LIKE '%refined%') THEN
        ALTER TABLE content_article_versions DROP CHECK ck_content_article_versions_kind;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.check_constraints WHERE constraint_schema = DATABASE()
        AND constraint_name = 'ck_content_article_versions_kind') THEN
        ALTER TABLE content_article_versions ADD CONSTRAINT ck_content_article_versions_kind
            CHECK (version_kind IN ('generated','manual_edit','imported','refined'));
    END IF;

    -- 2. 草稿修订号：manual_edit 版本被自动保存原地覆盖时 +1；新建版本为 0。
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'content_article_versions' AND column_name = 'edit_revision') THEN
        ALTER TABLE content_article_versions ADD COLUMN edit_revision INT NOT NULL DEFAULT 0 AFTER version_kind;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE()
        AND table_name = 'content_article_versions' AND constraint_name = 'ck_content_article_versions_revision') THEN
        ALTER TABLE content_article_versions ADD CONSTRAINT ck_content_article_versions_revision CHECK (edit_revision >= 0);
    END IF;
END//
DELIMITER ;
CALL jogeoai_041_article_editor();
DROP PROCEDURE jogeoai_041_article_editor;

-- 3. 智能微调计费：一次微调一行；held 预占 40 → captured（写入 refined 版本）或 released（模型失败、冲突或超时）。
CREATE TABLE IF NOT EXISTS article_refine_operations (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    article_id BINARY(16) NOT NULL,
    base_version_id BINARY(16) NOT NULL,
    base_edit_revision INT NOT NULL DEFAULT 0,
    result_version_id BINARY(16) NULL,
    -- whole 整体文章；selection 框选段落。
    scope VARCHAR(16) NOT NULL,
    -- held 已预占；captured 已结算；released 已释放（error_code 记录原因）。
    status VARCHAR(16) NOT NULL DEFAULT 'held',
    credits DECIMAL(18,3) NOT NULL,
    idempotency_key VARCHAR(191) COLLATE utf8mb4_0900_bin NOT NULL,
    request_hash CHAR(64) COLLATE utf8mb4_0900_bin NOT NULL,
    requested_by BINARY(16) NOT NULL,
    error_code VARCHAR(64) NULL,
    created_at DATETIME(6) NOT NULL,
    settled_at DATETIME(6) NULL,
    UNIQUE KEY uq_article_refine_operations_key (tenant_id, idempotency_key),
    UNIQUE KEY uq_article_refine_operations_tenant_id (tenant_id, id),
    KEY ix_article_refine_operations_article (tenant_id, article_id, created_at),
    KEY ix_article_refine_operations_status (tenant_id, status, created_at),
    CONSTRAINT fk_article_refine_operations_account FOREIGN KEY (tenant_id) REFERENCES credit_accounts (tenant_id),
    CONSTRAINT fk_article_refine_operations_article FOREIGN KEY (tenant_id, site_id, article_id)
        REFERENCES content_articles (tenant_id, site_id, id),
    CONSTRAINT fk_article_refine_operations_base FOREIGN KEY (tenant_id, article_id, base_version_id)
        REFERENCES content_article_versions (tenant_id, article_id, id),
    CONSTRAINT fk_article_refine_operations_result FOREIGN KEY (tenant_id, article_id, result_version_id)
        REFERENCES content_article_versions (tenant_id, article_id, id),
    CONSTRAINT fk_article_refine_operations_user FOREIGN KEY (requested_by) REFERENCES auth_users (id),
    CONSTRAINT ck_article_refine_operations_scope CHECK (scope IN ('whole','selection')),
    CONSTRAINT ck_article_refine_operations_status CHECK (status IN ('held','captured','released')),
    CONSTRAINT ck_article_refine_operations_credits CHECK (credits > 0),
    CONSTRAINT ck_article_refine_operations_result CHECK (status <> 'captured' OR result_version_id IS NOT NULL)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- 4. 账本关联微调操作；reserve / capture / release 复用，capture 行 amount 为 0，用量金额取 article_refine_operations.credits。
DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_041_ledger//
CREATE PROCEDURE jogeoai_041_ledger()
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'credit_ledger' AND column_name = 'refine_operation_id') THEN
        ALTER TABLE credit_ledger ADD COLUMN refine_operation_id BINARY(16) NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE()
        AND table_name = 'credit_ledger' AND index_name = 'ix_credit_ledger_refine_operation') THEN
        ALTER TABLE credit_ledger ADD KEY ix_credit_ledger_refine_operation (tenant_id, refine_operation_id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.table_constraints WHERE table_schema = DATABASE()
        AND table_name = 'credit_ledger' AND constraint_name = 'fk_credit_ledger_refine_operation') THEN
        ALTER TABLE credit_ledger ADD CONSTRAINT fk_credit_ledger_refine_operation
            FOREIGN KEY (tenant_id, refine_operation_id) REFERENCES article_refine_operations (tenant_id, id);
    END IF;
END//
DELIMITER ;
CALL jogeoai_041_ledger();
DROP PROCEDURE jogeoai_041_ledger;

-- 5. 编辑器图片：只登记服务端生成的对象键；读取时校验租户。
CREATE TABLE IF NOT EXISTS content_images (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    -- content-images/{tenantId:N}/{imageId:N}；与知识库 {tenantId:N}/{versionId:N}/source 隔离。
    storage_key VARCHAR(255) COLLATE utf8mb4_0900_bin NOT NULL,
    -- 按文件头魔数识别：image/png、image/jpeg、image/webp、image/gif。
    content_type VARCHAR(32) NOT NULL,
    byte_size INT UNSIGNED NOT NULL,
    width INT UNSIGNED NULL,
    height INT UNSIGNED NULL,
    created_by BINARY(16) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_content_images_storage_key (storage_key),
    UNIQUE KEY uq_content_images_tenant_id (tenant_id, id),
    KEY ix_content_images_site_created (tenant_id, site_id, created_at),
    CONSTRAINT fk_content_images_site FOREIGN KEY (tenant_id, site_id) REFERENCES sites (tenant_id, id),
    CONSTRAINT fk_content_images_creator FOREIGN KEY (created_by) REFERENCES auth_users (id),
    CONSTRAINT ck_content_images_type CHECK (content_type IN ('image/png','image/jpeg','image/webp','image/gif')),
    CONSTRAINT ck_content_images_size CHECK (byte_size BETWEEN 1 AND 5242880)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
