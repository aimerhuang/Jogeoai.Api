-- 026_site_remark_description.sql
-- 品牌备注与品牌描述（团队管理 / 品牌管理页展示字段）。
-- 手动执行于 001–025 之后；API / Worker 启动不会执行本文件。只做加法，可重复执行。
-- 两列都在 sites 上（与 report_title 同表），不进入 site_brand_settings，也不写入 subject_config_revisions 快照。
-- 未执行本文件的库上，GET /api/v1/sites/{siteId} 会因缺列失败：发布顺序必须「先迁移再发 API」。

DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_026_site_remark_description//
CREATE PROCEDURE jogeoai_026_site_remark_description()
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
        WHERE table_schema = DATABASE() AND table_name = 'sites' AND column_name = 'remark') THEN
        ALTER TABLE sites ADD COLUMN remark VARCHAR(200) NULL COMMENT '品牌备注（团队内部标注）' AFTER include_subdomains;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
        WHERE table_schema = DATABASE() AND table_name = 'sites' AND column_name = 'description') THEN
        ALTER TABLE sites ADD COLUMN description VARCHAR(2000) NULL COMMENT '品牌描述' AFTER remark;
    END IF;
END//
DELIMITER ;
CALL jogeoai_026_site_remark_description();
DROP PROCEDURE jogeoai_026_site_remark_description;
