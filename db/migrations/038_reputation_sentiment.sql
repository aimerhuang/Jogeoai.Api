-- 038_reputation_sentiment.sql
-- 品牌口碑情感事实（Trellis 10-30-reputation-backend；父任务 10-29-reputation-prd）。
-- 新增四张表，不修改任何旧表：
--   reputation_check_work        每个分析版本 × Check 的情感判定工作项（租约、重试、错误码）；
--   reputation_check_sentiments  每个分析版本 × Check × 本品牌主体一条判定（positive / neutral / negative / unknown / not_mentioned）；
--   reputation_term_facts        已判定回答中可在原文找到的印象词；
--   reputation_analysis_jobs     站点级手动补录任务（登记工作项，不立即调用模型）。
-- 事实绑定 release_id + check_id；查询只读快照绑定的已发布版本，且只计 classified_at <= 快照 as_of 的判定。
-- 判定写入事务先锁 site_analysis_state 行（与快照创建、提及分析完成共用的栅栏），因此快照之后提交的判定不会进入旧快照。
-- 判定表与印象词表不直接引用 sites，避免与快照创建（先锁 sites 再锁 site_analysis_state）形成反向加锁。
-- 依赖 006（analysis_releases、check_analysis、site_analysis_state）；手动执行于 001–037 之后，
-- mysql 客户端须加 --default-character-set=utf8mb4；API / Worker 启动不会执行本文件。
-- 可重放：全部 CREATE TABLE IF NOT EXISTS；若已存在旧形态的同名表不会被升级，需另写新编号迁移。
-- 发布顺序：先迁移再启用 Reputation:Worker:Enabled。未执行时口碑接口返回 503 REPUTATION_SCHEMA_NOT_MIGRATED，
-- 回答列表忽略情感字段（sentiment / term 筛选返回 503），采集、提及分析与旧分析接口不受影响。
-- 回滚：关闭 Worker 并回退 API 即可；确认不再需要时按 term_facts → sentiments → jobs → work 顺序 DROP。

DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_038_prerequisites//
CREATE PROCEDURE jogeoai_038_prerequisites()
BEGIN
    IF DATABASE() IS NULL OR NOT EXISTS (SELECT 1 FROM information_schema.tables
        WHERE table_schema = DATABASE() AND table_name = 'check_analysis') THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '038 requires a selected database and migration 006';
    END IF;
END//
DELIMITER ;
CALL jogeoai_038_prerequisites();
DROP PROCEDURE jogeoai_038_prerequisites;

CREATE TABLE IF NOT EXISTS reputation_check_work (
    release_id BINARY(16) NOT NULL,
    check_id BINARY(16) NOT NULL,
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    -- pending 待处理；processing 租约中；retry 可重试失败；completed 已写入判定；failed 重试耗尽（终态）。
    status VARCHAR(16) NOT NULL DEFAULT 'pending',
    attempt_count INT UNSIGNED NOT NULL DEFAULT 0,
    lease_owner VARCHAR(128) NULL,
    lease_version INT UNSIGNED NOT NULL DEFAULT 0,
    lease_until DATETIME(6) NULL,
    error_code VARCHAR(64) NULL,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    PRIMARY KEY (release_id, check_id),
    KEY ix_reputation_work_claim (status, lease_until, updated_at),
    KEY ix_reputation_work_site_release_status (site_id, release_id, status),
    CONSTRAINT fk_reputation_work_release FOREIGN KEY (site_id, release_id) REFERENCES analysis_releases (site_id, id),
    CONSTRAINT fk_reputation_work_analysis FOREIGN KEY (release_id, check_id) REFERENCES check_analysis (release_id, check_id),
    CONSTRAINT fk_reputation_work_check FOREIGN KEY (tenant_id, site_id, check_id)
        REFERENCES visibility_checks (tenant_id, site_id, id),
    CONSTRAINT ck_reputation_work_status CHECK (status IN ('pending','processing','retry','completed','failed'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS reputation_check_sentiments (
    release_id BINARY(16) NOT NULL,
    check_id BINARY(16) NOT NULL,
    brand_subject_id BINARY(16) NOT NULL,
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    state VARCHAR(16) NOT NULL,
    confidence DECIMAL(5,4) NULL,
    rationale_code VARCHAR(64) NOT NULL,
    classifier_version VARCHAR(64) NOT NULL,
    model_version VARCHAR(128) NULL,
    -- 判定依据片段在回答原文中的 UTF-16 左闭右开偏移；无可回溯片段时为空。
    evidence_start INT UNSIGNED NULL,
    evidence_end INT UNSIGNED NULL,
    -- SHA-256(口碑口径版本 + 品牌名 + 别名 + 回答原文)，用于在新分析版本中复用相同输入的判定。
    input_hash CHAR(64) COLLATE utf8mb4_0900_bin NOT NULL,
    classified_at DATETIME(6) NOT NULL,
    PRIMARY KEY (release_id, check_id, brand_subject_id),
    KEY ix_reputation_sentiments_site_release (site_id, release_id, classified_at),
    KEY ix_reputation_sentiments_reuse (check_id, brand_subject_id, input_hash),
    CONSTRAINT fk_reputation_sentiments_work FOREIGN KEY (release_id, check_id)
        REFERENCES reputation_check_work (release_id, check_id),
    CONSTRAINT ck_reputation_sentiments_state CHECK (state IN ('positive','neutral','negative','unknown','not_mentioned')),
    CONSTRAINT ck_reputation_sentiments_confidence CHECK (confidence IS NULL OR (confidence >= 0 AND confidence <= 1)),
    CONSTRAINT ck_reputation_sentiments_evidence CHECK ((evidence_start IS NULL AND evidence_end IS NULL)
        OR (evidence_start IS NOT NULL AND evidence_end IS NOT NULL AND evidence_end > evidence_start))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS reputation_term_facts (
    release_id BINARY(16) NOT NULL,
    check_id BINARY(16) NOT NULL,
    brand_subject_id BINARY(16) NOT NULL,
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    -- SHA-256(term_key)；term_key 为 NFKC、去首尾空白、合并空白、小写后的词键，回答列表 term 筛选使用同一规范化。
    term_hash CHAR(64) COLLATE utf8mb4_0900_bin NOT NULL,
    term_key VARCHAR(64) COLLATE utf8mb4_0900_bin NOT NULL,
    term_text VARCHAR(64) NOT NULL,
    polarity VARCHAR(16) NOT NULL,
    occurrence_count INT UNSIGNED NOT NULL,
    PRIMARY KEY (release_id, check_id, brand_subject_id, term_hash, polarity),
    KEY ix_reputation_terms_site_release_term (site_id, release_id, term_hash),
    CONSTRAINT fk_reputation_terms_sentiment FOREIGN KEY (release_id, check_id, brand_subject_id)
        REFERENCES reputation_check_sentiments (release_id, check_id, brand_subject_id) ON DELETE CASCADE,
    CONSTRAINT ck_reputation_terms_polarity CHECK (polarity IN ('positive','neutral','negative')),
    CONSTRAINT ck_reputation_terms_text CHECK (CHAR_LENGTH(term_text) BETWEEN 1 AND 64 AND CHAR_LENGTH(term_key) BETWEEN 1 AND 64),
    CONSTRAINT ck_reputation_terms_count CHECK (occurrence_count >= 1)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE IF NOT EXISTS reputation_analysis_jobs (
    id BINARY(16) NOT NULL PRIMARY KEY,
    tenant_id BINARY(16) NOT NULL,
    site_id BINARY(16) NOT NULL,
    release_id BINARY(16) NOT NULL,
    kind VARCHAR(32) NOT NULL DEFAULT 'manual_backfill',
    idempotency_key VARCHAR(191) COLLATE utf8mb4_0900_bin NOT NULL,
    request_hash CHAR(64) COLLATE utf8mb4_0900_bin NOT NULL,
    scope_json JSON NOT NULL,
    total_checks INT UNSIGNED NOT NULL DEFAULT 0,
    created_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL,
    UNIQUE KEY uq_reputation_jobs_tenant_key (tenant_id, idempotency_key),
    KEY ix_reputation_jobs_site_created (site_id, created_at),
    CONSTRAINT fk_reputation_jobs_release FOREIGN KEY (site_id, release_id) REFERENCES analysis_releases (site_id, id),
    CONSTRAINT fk_reputation_jobs_site FOREIGN KEY (tenant_id, site_id) REFERENCES sites (tenant_id, id),
    CONSTRAINT ck_reputation_jobs_kind CHECK (kind IN ('manual_backfill')),
    CONSTRAINT ck_reputation_jobs_scope CHECK (OCTET_LENGTH(scope_json) <= 4096)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
