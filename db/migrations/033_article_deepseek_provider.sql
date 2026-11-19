-- 033_article_deepseek_provider.sql
-- 为 research_provider_settings（008/010）新增文章生成专用 Provider 行：deepseek-article，
-- DeepSeek 官方 OpenAI 兼容 Chat Completions（POST /chat/completions），代码固定模型 DeepSeek-V4.1-Flash（API 模型名 deepseek-flash，关闭思考）。
-- 文章生成不再读取 openai-compatible / openai-api；品牌研究、产品识别与品牌识别仍使用原 Luna 配置。
-- 依赖 008、010；手动执行于 001–032 之后，API / Worker 启动不会执行本文件。
-- 只插入种子，不改表结构，可重复执行。
-- 执行时必须使用 --default-character-set=utf8mb4，否则非 ASCII 注释会写成乱码。
-- 种子未启用、无凭据：本机用 scripts/set-article-deepseek-key.sh 写入 Key 并启用；凭据不进入本文件。
-- 发布顺序：先执行本迁移并写入 Key，再发布 API / Worker；否则文章生成返回 503 ARTICLE_PROVIDER_UNAVAILABLE。
-- 回滚：回退 API / Worker 后执行 DELETE FROM research_provider_settings WHERE provider = 'deepseek-article';

-- ON DUPLICATE KEY UPDATE provider = provider 保证重放不会覆盖已经保存的地址、凭据或开关。
INSERT INTO research_provider_settings
    (provider, model, api_url, credential, credential_hint, priority, enabled,
     web_search_enabled, timeout_seconds, created_at, updated_at)
VALUES
    ('deepseek-article', 'deepseek-flash', 'https://api.deepseek.com/chat/completions',
     NULL, NULL, 100, FALSE, FALSE, 600, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6))
ON DUPLICATE KEY UPDATE provider = provider;
