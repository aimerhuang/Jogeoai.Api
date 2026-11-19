-- 029_site_icons_yandex.sql
-- 站点图标新增第三方来源 yandex（favicon.yandex.net）：放宽 site_icons.source 的 CHECK。
-- 手动执行于 001–028 之后；API / Worker 启动不会执行本迁移。只放宽 CHECK，可重放。
-- 未执行本文件的库上，来源为 yandex 的图标写入会违反 ck_site_icons_source 而返回 500（前端回退为首字母）：
-- 发布顺序为「先迁移再发 API」；或在执行前将 SiteIcons:ThirdParty:Providers 配置为不含 yandex。
-- 回滚：API 回退后放宽的 CHECK 保留无害；如需恢复 028 的约束，先删除或改写 source = 'yandex' 的行再重建 CHECK。

DELIMITER //
DROP PROCEDURE IF EXISTS jogeoai_029_site_icons_yandex//
CREATE PROCEDURE jogeoai_029_site_icons_yandex()
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.check_constraints WHERE constraint_schema = DATABASE()
        AND constraint_name = 'ck_site_icons_source' AND check_clause NOT LIKE '%yandex%') THEN
        ALTER TABLE site_icons DROP CHECK ck_site_icons_source;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.check_constraints WHERE constraint_schema = DATABASE()
        AND constraint_name = 'ck_site_icons_source') THEN
        ALTER TABLE site_icons ADD CONSTRAINT ck_site_icons_source
            CHECK (source IS NULL OR source IN ('html', 'favicon', 'google', 'duckduckgo', 'yandex'));
    END IF;
END//
DELIMITER ;
CALL jogeoai_029_site_icons_yandex();
DROP PROCEDURE jogeoai_029_site_icons_yandex;
