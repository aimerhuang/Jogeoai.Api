-- 028_site_icons.sql
-- 引用来源站点图标缓存：GET /api/v1/site-icons/{domain} 按需抓取后写入，全局共享、不区分租户。
-- 手动执行于 001–027 之后；API / Worker 启动不会执行本迁移。只新增一张表，可重放。
-- 未执行本文件的库上，站点图标接口会因缺表返回 500（前端回退为首字母），不影响其他接口：发布顺序为「先迁移再发 API」。
-- 回滚：API 回退后本表保留无害；确认不再需要时再 DROP TABLE site_icons。

CREATE TABLE IF NOT EXISTS site_icons (
    -- 规范化后的 ASCII 主机名（小写、去末尾点、≤ 253），由应用层校验后写入。
    domain VARCHAR(253) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    -- found：有图标字节；missing：所有来源都拿不到（含仅因超时 / 网络错误失败的短期记录）。
    status VARCHAR(16) NOT NULL,
    -- 魔数识别出的真实类型：image/png | image/x-icon | image/gif | image/jpeg | image/webp | image/bmp。
    content_type VARCHAR(32) NULL,
    -- 图标原始字节，应用层上限 SiteIcons:MaxIconBytes（默认 100KB），库内硬上限 1MiB。
    icon_bytes MEDIUMBLOB NULL,
    -- 图标来源：html（首页 link 声明）| favicon（/favicon.ico）| google | duckduckgo。
    source VARCHAR(16) NULL,
    fetched_at DATETIME(6) NOT NULL,
    expires_at DATETIME(6) NOT NULL,
    PRIMARY KEY (domain),
    CONSTRAINT ck_site_icons_status CHECK (status IN ('found', 'missing')),
    CONSTRAINT ck_site_icons_payload CHECK ((status = 'found') = (icon_bytes IS NOT NULL)),
    CONSTRAINT ck_site_icons_bytes CHECK (icon_bytes IS NULL OR OCTET_LENGTH(icon_bytes) BETWEEN 1 AND 1048576),
    CONSTRAINT ck_site_icons_source CHECK (source IS NULL OR source IN ('html', 'favicon', 'google', 'duckduckgo'))
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
