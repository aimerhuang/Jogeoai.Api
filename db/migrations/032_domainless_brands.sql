-- 032_domainless_brands.sql
-- 无官网品牌与租户内创建幂等；手动执行于 001–031 之后，可重放。
-- 不修改既有域名/主体快照，不回填虚构域名；写入 NULL 后不得直接回退旧服务或 NOT NULL。
DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_032_domainless_brands//
CREATE PROCEDURE jogeoai_032_domainless_brands()
BEGIN
    IF DATABASE() IS NULL OR NOT EXISTS (SELECT 1 FROM information_schema.columns
        WHERE table_schema = DATABASE() AND table_name = 'sites' AND column_name = 'description') THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = '032 requires a selected database and migrations through 031';
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns
        WHERE table_schema = DATABASE() AND table_name = 'sites' AND column_name = 'host' AND is_nullable = 'NO') THEN
        ALTER TABLE sites MODIFY COLUMN host VARCHAR(253) COLLATE utf8mb4_0900_bin NULL DEFAULT NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
        WHERE table_schema = DATABASE() AND table_name = 'sites' AND column_name = 'creation_request_id') THEN
        ALTER TABLE sites ADD COLUMN creation_request_id BINARY(16) NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
        WHERE table_schema = DATABASE() AND table_name = 'sites' AND column_name = 'creation_request_hash') THEN
        ALTER TABLE sites ADD COLUMN creation_request_hash CHAR(64) COLLATE utf8mb4_0900_bin NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.statistics
        WHERE table_schema = DATABASE() AND table_name = 'sites' AND index_name = 'uq_sites_tenant_creation_request') THEN
        ALTER TABLE sites ADD UNIQUE KEY uq_sites_tenant_creation_request (tenant_id, creation_request_id);
    END IF;
END//
DELIMITER ;
CALL jogeoai_032_domainless_brands();
DROP PROCEDURE jogeoai_032_domainless_brands;
